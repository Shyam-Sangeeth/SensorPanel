using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace SensorPanel.Sensors;

/// <summary>
/// Matches the sensor IDs used in imported LCD files (e.g. TCPU, PGPU1, THDD2) to this PC's sensors. Imported items keep
/// "match:ID" and are matched at run time, so sensors that appear later (e.g. after installing PawnIO) are picked up
/// without re-importing.
/// </summary>
internal static partial class SensorMatcher
{
    public const string Prefix = "match:";

    /// <summary>Friendly names for the IDs this importer knows.</summary>
    public static string Describe(string id) => DescribeId(id.Split('@')[0]);

    private static string DescribeId(string id) => id switch
    {
        "PCPUPKG" => "CPU package power",
        "SCPUUTI" => "CPU utilization",
        "TCPU" => "CPU temperature",
        "SCPUCLK" => "CPU clock",
        "VCPU" => "CPU core voltage",
        "FCPU" => "CPU fan",
        "FWPUMP" => "Water pump",
        "SMEMUTI" => "Memory utilization",
        "SRTSSFPS" => "Framerate",
        "STIMENS" => "Time, 24-hour",
        "STIME" => "Time, 24-hour with seconds",
        "SDATE" => "Date",
        "TMOBO" => "Motherboard temperature",
        _ when GpuStat().Match(id) is { Success: true } s => $"GPU{s.Groups[1].Value} {(s.Groups[2].Value == "UTI" ? "utilization" : "clock")}",
        _ when GpuId().Match(id) is { Success: true } m => $"GPU{m.Groups[2].Value} {m.Groups[1].Value switch
        {
            "P" => "power", "T" => "temperature", "V" => "voltage", _ => "fan duty",
        }}{(m.Groups[3].Success ? " #2" : "")}",
        _ when id.StartsWith("SDRV") && id.EndsWith("UTI") => $"Drive {id[4]}: used",
        _ when id.StartsWith("THDD") => $"Drive {id[4..]} temperature",
        _ when id.StartsWith("TDIMMTS") => $"Memory module {id[7..]} temperature",
        _ => id,
    };

    [GeneratedRegex(@"^(P|T|V|D)GPU(\d)(GPU2)?$")]
    private static partial Regex GpuId();

    [GeneratedRegex(@"^SGPU(\d)(UTI|CLK)$")]
    private static partial Regex GpuStat();

    /// <summary>
    /// <paramref name="id"/> is the imported sensor ID, optionally followed by "@hint" (the item's label, e.g. a drive's model name).
    /// </summary>
    public static string? Resolve(string id, IReadOnlyList<SensorHub.HardwareSensor> hw, IReadOnlyList<SensorInfo> all)
    {
        string? hint = null;
        int at = id.IndexOf('@');
        if (at >= 0)
        {
            hint = id[(at + 1)..];
            id = id[..at];
        }

        var stat = GpuStat().Match(id);
        if (stat.Success)
            return stat.Groups[2].Value == "UTI" ? Gpu(hw, SensorType.Load, "GPU Core") : Gpu(hw, SensorType.Clock, "GPU Core");

        switch (id)
        {
            case "PCPUPKG": return Find(hw, Cpu, SensorType.Power, "Package") ?? Find(hw, Cpu, SensorType.Power);
            case "SCPUUTI": return Find(hw, Cpu, SensorType.Load, "CPU Total") ?? Find(hw, Cpu, SensorType.Load);
            case "TCPU":
                return Find(hw, Cpu, SensorType.Temperature, "CPU Package") ?? Find(hw, Cpu, SensorType.Temperature, "Tctl")
                    ?? Find(hw, Cpu, SensorType.Temperature, "Core Average") ?? Find(hw, Cpu, SensorType.Temperature)
                    ?? Find(hw, Board, SensorType.Temperature, "CPU");
            case "SCPUCLK":
                // The fastest core (core #1 alone reads low whenever it's idle).
                return all.FirstOrDefault(s => s.Key == "/cpu-clock/0/max")?.Key
                    ?? Find(hw, Cpu, SensorType.Clock, "Core #1") ?? hw.FirstOrDefault(s => Cpu(s) && s.Type == SensorType.Clock &&
                    !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))?.Key;
            case "VCPU":
                return Find(hw, Board, SensorType.Voltage, "Vcore") ?? Find(hw, Board, SensorType.Voltage, "CPU Core")
                    ?? Find(hw, Cpu, SensorType.Voltage, "Core") ?? Find(hw, Cpu, SensorType.Voltage);
            case "FCPU": return Find(hw, Board, SensorType.Fan, "CPU") ?? Find(hw, Board, SensorType.Fan);
            case "FWPUMP":
                return Find(hw, Board, SensorType.Fan, "Pump") ?? Find(hw, s => s.HardwareType == HardwareType.Cooler, SensorType.Fan)
                    ?? Find(hw, Board, SensorType.Fan, "AIO");
            case "TMOBO":
                return Find(hw, Board, SensorType.Temperature, "System") ?? Find(hw, Board, SensorType.Temperature, "Motherboard")
                    ?? Find(hw, Board, SensorType.Temperature);
            case "SMEMUTI": return "sys/memory-load";
            case "SRTSSFPS": return "fps/foreground";
            case "STIMENS": return "sys/time";
            case "STIME": return "sys/time-seconds";
            case "SDATE": return "sys/date";
        }

        var m = GpuId().Match(id);
        if (m.Success)
        {
            bool second = m.Groups[3].Success;
            return m.Groups[1].Value switch
            {
                "P" => Gpu(hw, SensorType.Power, "Package") ?? Gpu(hw, SensorType.Power),
                "T" => Gpu(hw, SensorType.Temperature, "GPU Core") ?? Gpu(hw, SensorType.Temperature),
                "V" => Gpu(hw, SensorType.Voltage),
                "D" => Gpu(hw, SensorType.Control, second ? "Fan 2" : "Fan 1") ?? Nth(hw, IsGpu, SensorType.Control, second ? 1 : 0),
                _ => null,
            };
        }

        if (id.StartsWith("SDRV", StringComparison.Ordinal) && id.EndsWith("UTI", StringComparison.Ordinal) && id.Length == 8)
            return $"sys/drive/{id[4]}/used";

        if (id.StartsWith("THDD", StringComparison.Ordinal) && int.TryParse(id[4..], out int disk))
        {
            // The label normally holds the drive's model name; match on that first.
            // Skip the drive's warning/critical limits, which are listed as temperatures too.
            var drives = hw.Where(s => s.HardwareType == HardwareType.Storage && s.Type == SensorType.Temperature &&
                                       !s.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase) &&
                                       !s.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase)).ToList();
            var byName = drives.FirstOrDefault(s => !string.IsNullOrWhiteSpace(hint) &&
                                                    (hint.Contains(s.HardwareName, StringComparison.OrdinalIgnoreCase) ||
                                                     s.HardwareName.Contains(hint, StringComparison.OrdinalIgnoreCase)));
            if (byName != null) return byName.Key;
            var perDrive = drives.GroupBy(s => s.HardwareName).Select(g => g.First()).ToList();
            return disk - 1 < perDrive.Count ? perDrive[disk - 1].Key : null;
        }

        if (id.StartsWith("TDIMMTS", StringComparison.Ordinal) && int.TryParse(id[7..], out int slot))
        {
            // Each module also reports its sensor's resolution and limits as "temperatures"; only "DIMM #n" is the reading.
            var temps = hw.Where(s => s.HardwareType == HardwareType.Memory && s.Type == SensorType.Temperature &&
                                      s.Name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase)).ToList();
            // The files number slots from 1 (TS2 = second slot); LibreHardwareMonitor from 0 (/memory/dimm/1/ = second slot).
            var bySlot = temps.FirstOrDefault(s => s.Key.Contains($"/dimm/{slot - 1}/", StringComparison.OrdinalIgnoreCase));
            if (bySlot != null) return bySlot.Key;
            // Otherwise number the installed modules in order of the slots the panel asks for.
            int index = DimmSlots.OrderBy(x => x).ToList().IndexOf(slot);
            return index >= 0 && index < temps.Count ? temps[index].Key : null;
        }

        return null;
    }

    /// <summary>The memory slots the panel shows (TDIMMTS2, TDIMMTS4 → 2, 4), to number modules in order.</summary>
    public static IReadOnlyList<int> DimmSlots { get; set; } = [];

    private static bool Cpu(SensorHub.HardwareSensor s) => s.HardwareType == HardwareType.Cpu;
    private static bool Board(SensorHub.HardwareSensor s) => s.HardwareType is HardwareType.SuperIO or HardwareType.Motherboard;
    private static bool IsGpu(SensorHub.HardwareSensor s) => s.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static string? Gpu(IReadOnlyList<SensorHub.HardwareSensor> hw, SensorType type, string? name = null)
    {
        // Prefer a discrete card over integrated graphics.
        return Find(hw, s => s.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd, type, name)
               ?? Find(hw, IsGpu, type, name);
    }

    private static string? Find(IReadOnlyList<SensorHub.HardwareSensor> hw, Func<SensorHub.HardwareSensor, bool> where,
                                SensorType type, string? name = null) =>
        hw.FirstOrDefault(s => where(s) && s.Type == type &&
                               (name == null || s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))?.Key;

    private static string? Nth(IReadOnlyList<SensorHub.HardwareSensor> hw, Func<SensorHub.HardwareSensor, bool> where, SensorType type, int n) =>
        hw.Where(s => where(s) && s.Type == type).Skip(n).FirstOrDefault()?.Key;
}
