using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace SensorPanel.Sensors;

/// <summary>
/// Framerate of full-screen apps (games, full-screen video), measured like Intel's PresentMon: Windows' own event tracing (ETW) reports
/// every frame an app presents, so nothing is injected into games. DirectX 10/11/12 (DXGI) and DirectX 9 present events
/// are counted per process; for apps that present another way (Vulkan, OpenGL), the graphics kernel's present events are
/// used instead. Needs administrator rights.
/// </summary>
public sealed class FrameCounter : IDisposable
{
    private static readonly Guid Dxgi = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
    private static readonly Guid D3D9 = new("783aca0a-790e-4d7f-8451-aa850511c6b9");
    private static readonly Guid DxgKrnl = new("802ec45a-1e99-4b83-9920-87c98277ba9d");

    // Event IDs (from the providers' manifests): DXGI Present_Start 42 and PresentMultiplaneOverlay_Start 55;
    // D3D9 Present_Start 1; DxgKrnl PresentHistory_Start 171, PresentHistoryDetailed_Start 215 and Blit 166.
    private const int DxgiPresent = 42, DxgiOverlayPresent = 55, D3D9Present = 1;
    private const int KernelHistory = 171, KernelHistoryDetailed = 215, KernelBlit = 166;

    private enum Source { Api, History, HistoryDetailed, Blit }

    private sealed class Process
    {
        public readonly Queue<double>[] Times = [new(), new(), new(), new()];
        public double Last;
    }

    private readonly TraceEventSession _session;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly Dictionary<int, Process> _processes = [];
    private double _now;

    private FrameCounter(TraceEventSession session)
    {
        _session = session;
        _session.Source.AllEvents += OnEvent;
        _thread = new Thread(() =>
        {
            try { _session.Source.Process(); } catch { }
        }) { IsBackground = true, Name = "SensorPanel frames" };
        _thread.Start();
    }

    /// <summary>Starts counting, or returns null when it can't (no administrator rights).</summary>
    public static FrameCounter? TryStart()
    {
        if (!SensorHub.IsAdministrator) return null;
        try
        {
            // A session left over from a crash has the same name; creating it again takes it over.
            var session = new TraceEventSession("SensorPanel-Frames") { StopOnDispose = true, BufferSizeMB = 4 };
            const ulong analyticEvents = 0x8000000000000002; // "Events" keyword, analytic channel
            session.EnableProvider(Dxgi, TraceEventLevel.Informational, analyticEvents);
            session.EnableProvider(D3D9, TraceEventLevel.Informational, analyticEvents);
            session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, 0x8000000); // "Present" keyword
            return new FrameCounter(session);
        }
        catch
        {
            return null;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        Source? source = null;
        int id = (int)e.ID;
        if (e.ProviderGuid == Dxgi && id is DxgiPresent or DxgiOverlayPresent) source = Source.Api;
        else if (e.ProviderGuid == D3D9 && id == D3D9Present) source = Source.Api;
        else if (e.ProviderGuid == DxgKrnl)
            source = id switch { KernelHistory => Source.History, KernelHistoryDetailed => Source.HistoryDetailed, KernelBlit => Source.Blit, _ => null };
        // SensorPanel's own window (e.g. full screen on an HDMI panel) isn't something to measure.
        if (source is not { } s || e.ProcessID <= 0 || e.ProcessID == Environment.ProcessId) return;

        double t = e.TimeStampRelativeMSec;
        lock (_gate)
        {
            if (!_processes.TryGetValue(e.ProcessID, out var p)) _processes[e.ProcessID] = p = new Process();
            var q = p.Times[(int)s];
            q.Enqueue(t);
            while (q.Count > 0 && t - q.Peek() > 1000) q.Dequeue();
            p.Last = t;
            if (t > _now) _now = t;
        }
    }

    /// <summary>
    /// Frames per second of a full-screen app: the one in the foreground, otherwise the one drawing the most (e.g. a game
    /// on the other monitor). 0 when none is drawing.
    /// </summary>
    public double FullScreenFps()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
        List<int> drawing;
        lock (_gate)
        {
            Prune();
            drawing = _processes.Keys.ToList();
        }
        if (drawing.Contains((int)foreground) && FullScreen.Contains((int)foreground))
        {
            double fps = Fps((int)foreground);
            if (fps > 0) return fps;
        }
        double best = 0;
        foreach (int pid in drawing)
        {
            if (pid == foreground) continue;
            double fps = Fps(pid);
            if (fps > best && FullScreen.Contains(pid)) best = fps;
        }
        return best;
    }

    public double Fps(int pid)
    {
        lock (_gate)
        {
            Prune();
            if (!_processes.TryGetValue(pid, out var p)) return 0;
            // DirectX's own present events when the app has them; otherwise the kernel's (Vulkan, OpenGL).
            double api = Rate(p.Times[(int)Source.Api]);
            if (api > 0) return api;
            double history = Math.Max(Rate(p.Times[(int)Source.History]), Rate(p.Times[(int)Source.HistoryDetailed]));
            return history > 0 ? history : Rate(p.Times[(int)Source.Blit]);
        }
    }

    /// <summary>Frames per second over the last second of events (events arrive in batches, so use their own times).</summary>
    private double Rate(Queue<double> times)
    {
        if (times.Count < 2) return 0;
        double first = times.Peek(), last = 0;
        foreach (double t in times) last = t;
        if (_now - last > 1500) return 0; // stopped drawing
        return (times.Count - 1) * 1000.0 / Math.Max(1, last - first);
    }

    /// <summary>Forgets processes that haven't drawn for a while.</summary>
    private void Prune()
    {
        foreach (var pid in _processes.Where(kv => _now - kv.Value.Last > 10_000).Select(kv => kv.Key).ToList())
            _processes.Remove(pid);
    }

    /// <summary>Per-app counts of each kind of event in the last second (for checking how games are seen).</summary>
    public string Report()
    {
        lock (_gate)
        {
            Prune();
            var lines = new List<string>
            {
                "PID | Process | Full screen | DirectX presents | Kernel history | Kernel history detailed | Kernel blits | FPS",
            };
            foreach (var (pid, p) in _processes.OrderByDescending(kv => kv.Value.Times.Sum(q => q.Count)))
            {
                string name;
                try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { name = "?"; }
                string full = FullScreen.Contains(pid) ? "yes" : "no";
                lines.Add($"{pid} | {name} | {full} | {string.Join(" | ", p.Times.Select(q => q.Count))} | {Fps(pid):0}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
