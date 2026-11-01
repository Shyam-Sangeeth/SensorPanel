using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SensorPanel.Sensors;

/// <summary>
/// The processes using the most CPU and GPU, grouped by name like Task Manager's Apps view (a browser's many processes
/// count as one). Nothing is measured unless asked for. CPU comes from one system call that lists every process with its
/// CPU time; GPU from Windows' "GPU Engine" performance counters, which Task Manager's GPU column also uses (a process's
/// busiest engine type counts). Both are worked out between two readings, so the first one after starting is empty.
/// </summary>
internal sealed partial class ProcessMonitor : IDisposable
{
    public const int Count = 10;

    private readonly int _ownId = Environment.ProcessId;
    private Dictionary<(int Id, long Created), long> _lastTimes = [];
    private long _lastStamp;
    private IntPtr _buffer;
    private int _bufferSize;
    private IntPtr _gpuQuery, _gpuCounter;

    /// <summary>A running process: its ID and start time (IDs get reused), name and CPU time so far (100 ns units).</summary>
    private readonly record struct Entry(int Id, long Created, string Name, long Time);

    public IReadOnlyList<(string Name, double Usage)> TopCpu { get; private set; } = [];
    public IReadOnlyList<(string Name, double Usage)> TopGpu { get; private set; } = [];

    /// <summary>Measures what's asked for and forgets the rest (so a later reading doesn't average over the gap).</summary>
    public void Update(bool cpu, bool gpu)
    {
        if (!cpu)
        {
            _lastTimes = [];
            TopCpu = [];
        }
        if (!gpu)
        {
            CloseGpu();
            TopGpu = [];
        }
        if ((!cpu && !gpu) || !Environment.Is64BitProcess) return; // the list's layout below is the 64-bit one

        var processes = ListProcesses();
        if (processes == null) return;
        if (cpu) TopCpu = MeasureCpu(processes);
        if (gpu) TopGpu = MeasureGpu(processes);
    }

    private List<Entry>? ListProcesses()
    {
        if (_buffer == IntPtr.Zero)
        {
            _bufferSize = 1 << 20;
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }
        int status;
        while ((status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out int needed)) == StatusInfoLengthMismatch)
        {
            Marshal.FreeHGlobal(_buffer);
            _bufferSize = Math.Max(_bufferSize * 2, needed + 65536);
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }
        if (status != 0) return null;

        // SYSTEM_PROCESS_INFORMATION entries, one after another (64-bit offsets).
        var list = new List<Entry>();
        for (int offset = 0; ;)
        {
            IntPtr entry = _buffer + offset;
            int id = (int)Marshal.ReadIntPtr(entry, 80);
            if (id != 0 && id != _ownId) // not the idle "process", nor SensorPanel itself
            {
                long created = Marshal.ReadInt64(entry, 32);
                long time = Marshal.ReadInt64(entry, 40) + Marshal.ReadInt64(entry, 48); // user + kernel
                int length = (ushort)Marshal.ReadInt16(entry, 56);
                IntPtr name = Marshal.ReadIntPtr(entry, 64);
                string image = name == IntPtr.Zero ? "" : Marshal.PtrToStringUni(name, length / 2);
                list.Add(new Entry(id, created, DisplayName(image), time));
            }
            int next = Marshal.ReadInt32(entry, 0);
            if (next == 0) break;
            offset += next;
        }
        return list;
    }

    /// <summary>"chrome.exe" → "chrome"; the System process has no file name.</summary>
    private static string DisplayName(string image)
    {
        if (image.Length == 0) return "System";
        return image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;
    }

    private IReadOnlyList<(string, double)> MeasureCpu(List<Entry> processes)
    {
        long now = Stopwatch.GetTimestamp();
        // The time since the last reading on every logical processor, in the same 100 ns units as the CPU times.
        double available = (now - _lastStamp) * 1e7 / Stopwatch.Frequency * Environment.ProcessorCount;
        bool first = _lastTimes.Count == 0;
        var times = new Dictionary<(int, long), long>(processes.Count);
        var usage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes)
        {
            times[(p.Id, p.Created)] = p.Time;
            if (first || available <= 0 || !_lastTimes.TryGetValue((p.Id, p.Created), out long before)) continue;
            usage[p.Name] = usage.GetValueOrDefault(p.Name) + (p.Time - before) * 100.0 / available;
        }
        _lastTimes = times;
        _lastStamp = now;
        return Top(usage);
    }

    private IReadOnlyList<(string, double)> MeasureGpu(List<Entry> processes)
    {
        if (_gpuQuery == IntPtr.Zero)
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) != 0)
            {
                _gpuQuery = IntPtr.Zero;
                return [];
            }
            if (PdhAddEnglishCounter(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
            {
                CloseGpu();
                return [];
            }
            PdhCollectQueryData(_gpuQuery); // the first of two readings
            return [];
        }
        if (PdhCollectQueryData(_gpuQuery) != 0) return [];

        uint size = 0;
        if (PdhGetFormattedCounterArray(_gpuCounter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, IntPtr.Zero) != PdhMoreData) return [];
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_gpuCounter, PdhFmtDouble | PdhFmtNoCap100, ref size, out uint count, buffer) != 0) return [];

            // Instances look like "pid_1234_luid_0x…_phys_0_eng_3_engtype_3D". Add up each process's engines of a type,
            // then take its busiest type.
            var engines = new Dictionary<(int Id, string Type), double>();
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buffer + i * 24; // PDH_FMT_COUNTERVALUE_ITEM_W: name, status, value
                if ((uint)Marshal.ReadInt32(item, 8) > 1) continue; // only valid or new data
                var match = GpuInstance().Match(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)) ?? "");
                if (!match.Success) continue;
                var key = (int.Parse(match.Groups[1].ValueSpan), match.Groups[2].Value);
                engines[key] = engines.GetValueOrDefault(key) + BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
            }
            var names = new Dictionary<int, string>();
            foreach (var p in processes) names[p.Id] = p.Name;
            var usage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in engines.GroupBy(e => e.Key.Id))
                if (names.TryGetValue(process.Key, out var name)) // not SensorPanel, nor processes that have just ended
                    usage[name] = usage.GetValueOrDefault(name) + process.Max(e => e.Value);
            return Top(usage);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The busiest (at least 0.05%), most first, at most 100% each.</summary>
    private static IReadOnlyList<(string, double)> Top(Dictionary<string, double> usage) =>
        usage.Where(u => u.Value >= 0.05).OrderByDescending(u => u.Value).ThenBy(u => u.Key)
             .Take(Count).Select(u => (u.Key, Math.Min(100, u.Value))).ToList();

    private void CloseGpu()
    {
        if (_gpuQuery == IntPtr.Zero) return;
        PdhCloseQuery(_gpuQuery);
        _gpuQuery = _gpuCounter = IntPtr.Zero;
    }

    public void Dispose()
    {
        CloseGpu();
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }

    [GeneratedRegex(@"^pid_(\d+)_.*_engtype_(.+)$")]
    private static partial Regex GpuInstance();

    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint PdhFmtDouble = 0x200, PdhFmtNoCap100 = 0x8000;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
