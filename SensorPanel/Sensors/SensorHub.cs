using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace SensorPanel.Sensors;

/// <summary>A sensor the panel can show.</summary>
public sealed record SensorInfo(string Key, string Group, string Name, string Unit, int Decimals, bool IsText = false)
{
    public string Display => $"{Group} › {Name}";
}

/// <summary>A sensor reading: a number, or text for things like the time.</summary>
public readonly record struct Reading(double? Value, string? Text = null)
{
    public static readonly Reading None = new(null);
    public bool HasValue => Value.HasValue || Text != null;
}

/// <summary>
/// All sensors: hardware sensors from LibreHardwareMonitor, plus time, memory, drive usage and RivaTuner FPS.
/// <see cref="Update"/> runs on the engine thread; readers get immutable snapshots.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsControllerEnabled = true,
    };

    private readonly object _gate = new();
    private readonly FrameCounter? _frames = FrameCounter.TryStart();
    private bool _opened;
    private volatile Snapshot _snapshot = new([], new Dictionary<string, Reading>(), []);
    private Dictionary<string, string?> _matchCache = [];

    private sealed record Snapshot(
        IReadOnlyList<SensorInfo> Sensors,
        IReadOnlyDictionary<string, Reading> Values,
        IReadOnlyList<HardwareSensor> Hardware);

    /// <summary>A LibreHardwareMonitor sensor with what's needed to match imported sensor IDs.</summary>
    internal sealed record HardwareSensor(string Key, HardwareType HardwareType, string HardwareName, SensorType Type, string Name);

    public static bool IsAdministrator { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// Whether PawnIO is properly installed. LibreHardwareMonitor only uses it when PawnIO's own setup registered it
    /// (its uninstall entry); a driver copied in by another program isn't enough.
    /// </summary>
    public static bool IsPawnIoInstalled
    {
        get
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key != null;
        }
    }

    /// <summary>The PawnIO driver is on the PC (possibly put there by another program).</summary>
    public static bool IsPawnIoDriverPresent =>
        Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO") != null;

    /// <summary>Why some sensors are missing, or null when everything can be read.</summary>
    public static string? Limitation =>
        !IsAdministrator ? "Running without administrator rights: CPU, motherboard, fan and memory temperature sensors are unavailable."
        : !IsPawnIoInstalled ? "The PawnIO driver isn't installed: CPU, motherboard, fan and memory temperature sensors are unavailable."
        : null;

    public IReadOnlyList<SensorInfo> Sensors => _snapshot.Sensors;

    /// <summary>Raised on the engine thread when sensors appear or disappear.</summary>
    public event Action? SensorsChanged;

    public Reading Read(string key)
    {
        var snapshot = _snapshot;
        string? resolved = Resolve(key);
        return resolved != null && snapshot.Values.TryGetValue(resolved, out var reading) ? reading : Reading.None;
    }

    public SensorInfo? Info(string key)
    {
        string? resolved = Resolve(key);
        return resolved == null ? null : _snapshot.Sensors.FirstOrDefault(s => s.Key == resolved);
    }

    /// <summary>Turns a "match:XXX" sensor into the matching real sensor key (others are returned as they are).</summary>
    public string? Resolve(string key)
    {
        if (!key.StartsWith(SensorMatcher.Prefix, StringComparison.Ordinal)) return key;
        lock (_gate)
        {
            if (_matchCache.TryGetValue(key, out var cached)) return cached;
            string? found = SensorMatcher.Resolve(key[SensorMatcher.Prefix.Length..], _snapshot.Hardware, _snapshot.Sensors);
            _matchCache[key] = found;
            return found;
        }
    }

    /// <summary>Reads every sensor. Only called from the engine thread; readers never wait for it.</summary>
    public void Update()
    {
        if (!_opened)
        {
            _opened = true;
            try { _computer.Open(); } catch { /* partial sensor sets are still useful */ }
        }

        var hardware = new List<HardwareSensor>();
        var sensors = new List<SensorInfo>();
        var values = new Dictionary<string, Reading>();

        foreach (var hw in _computer.Hardware)
            Collect(hw, hw.HardwareType, hardware, sensors, values);

        AddBuiltIns(sensors, values);

        bool changed = sensors.Count != _snapshot.Sensors.Count ||
                       !sensors.Select(s => s.Key).SequenceEqual(_snapshot.Sensors.Select(s => s.Key));
        lock (_gate)
        {
            _snapshot = new Snapshot(sensors, values, hardware);
            if (changed) _matchCache = [];
        }
        if (changed) SensorsChanged?.Invoke();

        // A readable list of every sensor, for checking what's available (refreshed every minute).
        if (ReportPath != null && (changed || Environment.TickCount64 - _lastReport > 60_000))
        {
            _lastReport = Environment.TickCount64;
            WriteReport(ReportPath, hardware, values);
        }

        // Which apps are drawing frames, and how each is seen (fps.txt, every second).
        if (ReportPath != null && _frames != null && Environment.TickCount64 - _lastFpsReport > 900)
        {
            _lastFpsReport = Environment.TickCount64;
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(ReportPath)!, "fps.txt"), _frames.Report()); } catch { }
        }
    }

    /// <summary>Where to write the sensor list (sensors.txt), or null for none.</summary>
    public string? ReportPath { get; set; }

    private long _lastReport;
    private long _lastFpsReport;

    private static void WriteReport(string path, List<HardwareSensor> hardware, Dictionary<string, Reading> values)
    {
        try
        {
            var lines = new List<string> { $"SensorPanel sensors, {DateTime.Now:yyyy-MM-dd HH:mm:ss}", "Type | Hardware | Sensor | Key | Value", "" };
            foreach (var s in hardware)
            {
                var v = values.GetValueOrDefault(s.Key).Value;
                lines.Add($"{s.HardwareType} | {s.HardwareName} | {s.Name} ({s.Type}) | {s.Key} | {(v.HasValue ? v.Value.ToString("0.###") : "-")}");
            }
            File.WriteAllLines(path, lines);
        }
        catch { }
    }

    private static void Collect(IHardware hw, HardwareType rootType, List<HardwareSensor> hardware, List<SensorInfo> sensors,
                                Dictionary<string, Reading> values)
    {
        try { hw.Update(); } catch { return; }
        foreach (var sensor in hw.Sensors)
        {
            string key = sensor.Identifier.ToString();
            if (values.ContainsKey(key)) continue;
            var (unit, decimals) = UnitFor(sensor.SensorType);
            sensors.Add(new SensorInfo(key, hw.Name, $"{sensor.Name} ({sensor.SensorType})", unit, decimals));
            values[key] = new Reading(sensor.Value);
            hardware.Add(new HardwareSensor(key, hw.HardwareType == HardwareType.SuperIO ? HardwareType.SuperIO : rootType,
                                            hw.Name, sensor.SensorType, sensor.Name));
        }
        foreach (var sub in hw.SubHardware)
            Collect(sub, rootType, hardware, sensors, values);
    }

    private static (string Unit, int Decimals) UnitFor(SensorType type) => type switch
    {
        SensorType.Voltage => (" V", 3),
        SensorType.Current => (" A", 2),
        SensorType.Power => (" W", 2),
        SensorType.Clock => (" MHz", 0),
        SensorType.Temperature => ("°C", 0),
        SensorType.Load => ("%", 0),
        SensorType.Frequency => (" Hz", 0),
        SensorType.Fan => (" RPM", 0),
        SensorType.Flow => (" L/h", 0),
        SensorType.Control => ("%", 0),
        SensorType.Level => ("%", 0),
        SensorType.Data => (" GB", 1),
        SensorType.SmallData => (" MB", 0),
        SensorType.Throughput => (" B/s", 0),
        SensorType.Energy => (" mWh", 0),
        SensorType.Noise => (" dBA", 0),
        _ => ("", 1),
    };

    private void AddBuiltIns(List<SensorInfo> sensors, Dictionary<string, Reading> values)
    {
        var now = DateTime.Now;
        Add("sys/time", "Time, 24-hour (21:42)", "", now.ToString("HH:mm"));
        Add("sys/time-seconds", "Time, 24-hour with seconds (21:42:07)", "", now.ToString("HH:mm:ss"));
        // AM/PM in English, as on most sensor panels, whatever the Windows language.
        var english = System.Globalization.CultureInfo.InvariantCulture;
        Add("sys/time12", "Time, 12-hour (9:42 PM)", "", now.ToString("h:mm tt", english));
        Add("sys/time12-seconds", "Time, 12-hour with seconds (9:42:07 PM)", "", now.ToString("h:mm:ss tt", english));
        Add("sys/date", "Date", "", now.ToShortDateString());
        Add("sys/weekday", "Day of week", "", now.ToString("dddd"));

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            Add("sys/memory-load", "Memory utilization", "%", memory.Load, 0);
            Add("sys/memory-used", "Memory used", " GB", (memory.TotalPhys - memory.AvailPhys) / 1073741824.0, 1);
            Add("sys/memory-free", "Memory free", " GB", memory.AvailPhys / 1073741824.0, 1);
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string letter = drive.Name[..1].ToUpperInvariant();
                double used = 100.0 * (drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize;
                Add($"sys/drive/{letter}/used", $"Drive {letter}: used", "%", used, 0);
                Add($"sys/drive/{letter}/free", $"Drive {letter}: free", " GB", drive.TotalFreeSpace / 1073741824.0, 1);
            }
            catch { }
        }

        // Framerate of full-screen apps from Windows' frame events. RivaTuner is optional: when it's running, its figure
        // fills in for apps those events don't see, and it's offered as a sensor of its own.
        double? rtss = RtssFps.Read();
        double counted = _frames?.FullScreenFps() ?? 0;
        double fps = counted > 0 ? counted : rtss != null ? RtssFps.Read(FullScreen.Contains) ?? 0 : 0;
        Add("fps/foreground", "Framerate (full screen)", " FPS", fps, 0);
        Add("fps/frametime", "Frame time (full screen)", " ms", fps > 0 ? 1000 / fps : 0, 1);
        if (rtss != null) Add("rtss/fps", "Framerate (RivaTuner)", " FPS", rtss.Value, 0);

        void Add(string key, string name, string unit, object value, int decimals = 0)
        {
            string group = key.StartsWith("rtss", StringComparison.Ordinal) ? "RivaTuner"
                         : key.StartsWith("fps", StringComparison.Ordinal) ? "Framerate" : "System";
            bool isText = value is string;
            sensors.Add(new SensorInfo(key, group, name, unit, decimals, isText));
            values[key] = value is string text ? new Reading(null, text) : new Reading(Convert.ToDouble(value));
        }
    }

    /// <summary>Call after the engine thread has stopped.</summary>
    public void Dispose()
    {
        _frames?.Dispose();
        try { _computer.Close(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
