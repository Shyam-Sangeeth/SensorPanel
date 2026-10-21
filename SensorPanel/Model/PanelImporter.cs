using System.Text;
using System.Text.RegularExpressions;
using SensorPanel.Sensors;

namespace SensorPanel.Model;

/// <summary>
/// Reads an LCD panel from a settings file (.ini, HWMonLCDItemAX… lines) or an LCD backup (.axlcd) into a
/// <see cref="PanelLayout"/>.
/// </summary>
public static partial class PanelImporter
{
    /// <summary>Gauge sizes for the files' S/M/L settings, in pixels.</summary>
    private static int GaugeSize(string size) => size switch { "L" => 100, "M" => 75, _ => 52 };

    [GeneratedRegex(@"<(\w+)>([^<]*)</\1>")]
    private static partial Regex Field();

    /// <summary>
    /// Imports a settings file (.ini) or an LCD backup (.axlcd). A backup doesn't hold
    /// the display settings (rotation, backlight, refresh), so those are taken from <paramref name="current"/>.
    /// </summary>
    public static PanelLayout Import(string path, string imageFolder, PanelLayout? current = null) =>
        Path.GetExtension(path).Equals(".axlcd", StringComparison.OrdinalIgnoreCase)
            ? ImportBackup(path, imageFolder, current)
            : ImportIni(path, imageFolder);

    /// <summary>
    /// An .axlcd backup: &lt;LCDBGCOLOR&gt;, then one &lt;LCDPAGEn&gt; block per page with one item per line, in the same
    /// format as the settings file, and pictures embedded as hex (&lt;IMGDAT&gt;). Only the first page is used.
    /// </summary>
    private static PanelLayout ImportBackup(string path, string imageFolder, PanelLayout? current)
    {
        string text = File.ReadAllText(path, Encoding.Latin1);
        var page = Regex.Match(text, @"<LCDPAGE1>(.*?)</LCDPAGE1>", RegexOptions.Singleline);
        if (!page.Success) throw new InvalidDataException("This file doesn't contain a panel.");

        var layout = new PanelLayout
        {
            Background = Color(Regex.Match(text, "<LCDBGCOLOR>([^<]*)</LCDBGCOLOR>").Groups[1].Value, "#000000"),
            Rotation = current?.Rotation ?? "90",
            Brightness = current?.Brightness ?? 7,
            RefreshMs = current?.RefreshMs ?? 1000,
        };
        var itemLines = page.Groups[1].Value.Split('\n').Where(l => l.Contains("<ID>", StringComparison.Ordinal)).ToList();
        if (itemLines.Count == 0) throw new InvalidDataException("This file's panel has no items.");
        AddItems(layout, itemLines, imageFolder);
        return layout;
    }

    private static PanelLayout ImportIni(string iniPath, string imageFolder)
    {
        // Settings files are written in the ANSI code page; Latin-1 covers the characters they use (°, µ).
        var lines = File.ReadAllLines(iniPath, Encoding.Latin1);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var itemLines = new List<string>();
        foreach (string line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq];
            if (key.StartsWith("HWMonLCDItemAX", StringComparison.OrdinalIgnoreCase)) itemLines.Add(line[(eq + 1)..]);
            else settings.TryAdd(key, line[(eq + 1)..]);
        }
        if (itemLines.Count == 0)
            throw new InvalidDataException("This file doesn't contain a panel.");

        var layout = new PanelLayout
        {
            Rotation = settings.GetValueOrDefault("HWMonAXLCDRotate", "0"),
            Brightness = Math.Clamp(Int(settings.GetValueOrDefault("HWMonAXLCDBright"), 7), 0, 7),
            Background = Color(settings.GetValueOrDefault("HWMonAXLCDBGColor"), "#000000"),
            RefreshMs = Math.Clamp(Int(settings.GetValueOrDefault("HWMonUpdateFreqLCD"), 1000), 250, 10000),
        };

        AddItems(layout, itemLines, imageFolder);
        return layout;
    }

    private static void AddItems(PanelLayout layout, IEnumerable<string> itemLines, string imageFolder)
    {
        Directory.CreateDirectory(imageFolder);
        foreach (string line in itemLines)
        {
            var f = new Dictionary<string, string>();
            foreach (Match m in Field().Matches(line)) f.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
            var item = Convert(f, imageFolder);
            if (item == null) continue;
            item.X = Int(f.GetValueOrDefault("ITMX"), 0);
            item.Y = Int(f.GetValueOrDefault("ITMY"), 0);
            layout.Items.Add(item);
        }
    }

    public const string FramerateSensor = "fps/foreground";

    /// <summary>
    /// SensorPanel's own sensor for imported sensor IDs that aren't hardware (time, date, memory use, drive space, framerate), or
    /// null for hardware sensors, which are matched to this PC's sensors at run time.
    /// </summary>
    public static string? OwnSensor(string id) => id switch
    {
        "STIMENS" => "sys/time",
        "STIME" => "sys/time-seconds",
        "SDATE" => "sys/date",
        "SMEMUTI" => "sys/memory-load",
        "SRTSSFPS" => FramerateSensor,
        _ when id.Length == 8 && id.StartsWith("SDRV", StringComparison.Ordinal) && id.EndsWith("UTI", StringComparison.Ordinal)
            => $"sys/drive/{id[4]}/used",
        _ => null,
    };

    /// <summary>
    /// Points items still using an imported sensor ID for something SensorPanel provides itself (time, memory use, drive space,
    /// framerate), or RivaTuner's framerate, at SensorPanel's own sensor. Returns whether anything changed.
    /// </summary>
    public static bool UseOwnSensors(PanelLayout layout)
    {
        bool changed = false;
        foreach (var item in layout.Items)
        {
            if (item is not ISensorItem s) continue;
            string? own = s.Sensor == "rtss/fps" ? FramerateSensor
                : s.Sensor.StartsWith(SensorMatcher.Prefix, StringComparison.Ordinal) ? OwnSensor(s.Sensor[SensorMatcher.Prefix.Length..].Split('@')[0])
                : null;
            if (own == null) continue;
            s.Sensor = own;
            if (own == FramerateSensor)
            {
                switch (item)
                {
                    case ValueItem v when v.Label == "RTSS FPS": v.Label = "Framerate"; break;
                    case GraphItem g when g.Label == "RTSS FPS": g.Label = "Framerate"; break;
                    case BarItem b when b.Label == "RTSS FPS": b.Label = "Framerate"; break;
                    case GaugeItem g when g.Label == "RTSS FPS": g.Label = "Framerate"; break;
                }
            }
            changed = true;
        }
        return changed;
    }

    /// <summary>The memory slots used by imported DIMM temperature items (TDIMMTS2 → 2).</summary>
    public static IReadOnlyList<int> DimmSlots(PanelLayout layout) =>
        layout.Items.OfType<ISensorItem>()
            .Select(i => Regex.Match(i.Sensor, @"^match:TDIMMTS(\d+)"))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(x => x).ToList();

    private static PanelItem? Convert(Dictionary<string, string> f, string imageFolder)
    {
        string id = f.GetValueOrDefault("ID", "");
        string kind = "";
        var prefix = Regex.Match(id, @"^\[(\w+)\](.*)$");
        if (prefix.Success)
        {
            kind = prefix.Groups[1].Value;
            id = prefix.Groups[2].Value;
        }

        if (id == "IMG")
        {
            string source = f.GetValueOrDefault("IMGFIL", "");
            string file = source;
            string name = Path.GetFileName(source);
            if (f.TryGetValue("IMGDAT", out var hex) && hex.Length > 0 && name.Length > 0)
            {
                // Backups carry the picture itself.
                file = Path.Combine(imageFolder, name);
                File.WriteAllBytes(file, System.Convert.FromHexString(hex));
            }
            else if (File.Exists(source))
            {
                file = Path.Combine(imageFolder, name);
                File.Copy(source, file, overwrite: true);
            }
            return new ImageItem { File = file };
        }

        string label = f.GetValueOrDefault("LBL", "");
        if (id == "LBL" || id == "LABEL")
            return new LabelItem
            {
                Text = label,
                Font = f.GetValueOrDefault("FNTNAM", "Tahoma"),
                FontSize = Dbl(f.GetValueOrDefault("TXTSIZ"), 8),
                Color = Color(f.GetValueOrDefault("TXTCOL") ?? f.GetValueOrDefault("LBLCOL"), "#FFFFFF"),
                Bold = Flag(f.GetValueOrDefault("TXTBIR") ?? f.GetValueOrDefault("LBLBIS"), 0),
                Italic = Flag(f.GetValueOrDefault("TXTBIR") ?? f.GetValueOrDefault("LBLBIS"), 1),
            };

        // Drive temperatures are matched by model name, which the file puts in the label.
        string sensor = SensorMatcher.Prefix + id + (id.StartsWith("THDD", StringComparison.Ordinal) ? "@" + label : "");
        // Time, memory use, drive space and framerate come from SensorPanel itself.
        if (OwnSensor(id) is { } own)
        {
            sensor = own;
            if (own == FramerateSensor && label == "RTSS FPS") label = "Framerate";
        }

        switch (kind)
        {
            case "SIMPLE":
                return new ValueItem
                {
                    Sensor = sensor,
                    Label = label,
                    ShowLabel = Bool(f, "SHWLBL", true),
                    Unit = f.GetValueOrDefault("UNT", ""),
                    ShowUnit = Bool(f, "SHWUNT", true),
                    Font = f.GetValueOrDefault("FNTNAM", "Tahoma"),
                    FontSize = Dbl(f.GetValueOrDefault("TXTSIZ"), 8),
                    Color = Color(f.GetValueOrDefault("TXTCOL"), "#FFFFFF"),
                    Bold = Flag(f.GetValueOrDefault("TXTBIR"), 0),
                    Italic = Flag(f.GetValueOrDefault("TXTBIR"), 1),
                };

            case "GAUGE":
                return new GaugeItem
                {
                    Sensor = sensor,
                    Label = label,
                    Size = GaugeSize(f.GetValueOrDefault("SIZ", "S")),
                    Min = Dbl(f.GetValueOrDefault("MINVAL"), 0),
                    Max = Dbl(f.GetValueOrDefault("MAXVAL"), 100),
                    ShowValue = Bool(f, "SHWVAL", true),
                    Font = f.GetValueOrDefault("FNTNAM", "Tahoma"),
                    FontSize = Dbl(f.GetValueOrDefault("TXTSIZ"), 8),
                    ValueColor = Color(f.GetValueOrDefault("VALCOL"), "#FFFFFF"),
                    Bold = Flag(f.GetValueOrDefault("VALBI"), 0),
                    Italic = Flag(f.GetValueOrDefault("VALBI"), 1),
                };

            case "GRAPH":
                string flags = f.GetValueOrDefault("GPHBFG", "110");
                return new GraphItem
                {
                    Sensor = sensor,
                    Label = label,
                    Width = Int(f.GetValueOrDefault("WID"), 200),
                    Height = Int(f.GetValueOrDefault("HEI"), 50),
                    Style = f.GetValueOrDefault("TYP", "LG") switch { "AG" => "Area", "BG" or "HG" => "Bars", _ => "Line" },
                    Step = Math.Max(1, Int(f.GetValueOrDefault("GPHSTP"), 1)),
                    Thickness = Math.Max(1, Int(f.GetValueOrDefault("GPHTCK"), 1)),
                    GridSpacing = Math.Max(2, Int(f.GetValueOrDefault("GRDDNS"), 10)),
                    Min = Dbl(f.GetValueOrDefault("MINVAL"), 0),
                    Max = Dbl(f.GetValueOrDefault("MAXVAL"), 100),
                    AutoScale = Bool(f, "AUTSCL", false),
                    GridColor = Color(f.GetValueOrDefault("GRDCOL"), "#008000"),
                    LineColor = Color(f.GetValueOrDefault("GPHCOL"), "#00FF00"),
                    BackgroundColor = Color(f.GetValueOrDefault("BGCOL"), "#000000"),
                    FrameColor = Color(f.GetValueOrDefault("FRMCOL"), "#666666"),
                    ShowBackground = Flag(flags, 0),
                    ShowFrame = Flag(flags, 1),
                    ShowGrid = Flag(flags, 2),
                    ShowScale = Bool(f, "SHWSCL", false),
                    ScaleColor = Color(f.GetValueOrDefault("SCLCOL"), "#FFFFFF"),
                    Font = f.GetValueOrDefault("FNTNAM", "Tahoma"),
                    FontSize = Dbl(f.GetValueOrDefault("TXTSIZ"), 8),
                    Bold = Flag(f.GetValueOrDefault("SCLBI"), 0),
                    Italic = Flag(f.GetValueOrDefault("SCLBI"), 1),
                };
        }

        // A plain sensor item: label, value, unit and (optionally) a bar.
        int width = Int(f.GetValueOrDefault("WID"), 200);
        return new BarItem
        {
            Sensor = sensor,
            Label = label,
            ShowLabel = Bool(f, "SHWLBL", true),
            ShowValue = Bool(f, "SHWVAL", true),
            Unit = f.GetValueOrDefault("UNT", ""),
            ShowUnit = Bool(f, "SHWUNT", true),
            Width = width,
            UnitWidth = Int(f.GetValueOrDefault("UNTWID"), 20),
            Font = f.GetValueOrDefault("FNTNAM", "Tahoma"),
            FontSize = Dbl(f.GetValueOrDefault("TXTSIZ"), 8),
            Bold = Flag(f.GetValueOrDefault("LBLBIS"), 0),
            Italic = Flag(f.GetValueOrDefault("LBLBIS"), 1),
            LabelColor = Color(f.GetValueOrDefault("LBLCOL"), "#FFFFFF"),
            ValueColor = Color(f.GetValueOrDefault("VALCOL"), "#FFFFFF"),
            UnitColor = Color(f.GetValueOrDefault("UNTCOL"), "#FFFFFF"),
            ShowBar = Bool(f, "SHWBAR", false),
            BarPlacement = f.GetValueOrDefault("BARPLC", "SEP") == "SEP" ? "Below" : "Inline",
            BarWidth = Int(f.GetValueOrDefault("BARWID"), width),
            BarHeight = Int(f.GetValueOrDefault("BARHEI"), 15),
            BarIndent = Int(f.GetValueOrDefault("BARIND"), 0),
            BarFrame = Flag(f.GetValueOrDefault("BARFS"), 0),
            BarFrameColor = Color(f.GetValueOrDefault("BARFRMCOL"), "#666666"),
            Min = Dbl(f.GetValueOrDefault("BARMIN"), 0),
            Max = Dbl(f.GetValueOrDefault("BARMAX"), 100),
            Limit1 = f.GetValueOrDefault("BARLIM1", ""),
            Limit2 = f.GetValueOrDefault("BARLIM2", ""),
            Limit3 = f.GetValueOrDefault("BARLIM3", ""),
            MinColor = Color(f.GetValueOrDefault("BARMINFGC"), "#FFFFFF"),
            MinBackground = Color(f.GetValueOrDefault("BARMINBGC"), "#333333"),
            Limit1Color = Color(f.GetValueOrDefault("BARLIM1FGC"), "#FFFFFF"),
            Limit1Background = Color(f.GetValueOrDefault("BARLIM1BGC"), "#333333"),
            Limit2Color = Color(f.GetValueOrDefault("BARLIM2FGC"), "#FFFFFF"),
            Limit2Background = Color(f.GetValueOrDefault("BARLIM2BGC"), "#333333"),
            Limit3Color = Color(f.GetValueOrDefault("BARLIM3FGC"), "#FF0000"),
            Limit3Background = Color(f.GetValueOrDefault("BARLIM3BGC"), "#333333"),
        };
    }

    private static int Int(string? s, int fallback) => int.TryParse(s, out int v) ? v : fallback;

    private static double Dbl(string? s, double fallback) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;

    private static bool Bool(Dictionary<string, string> f, string key, bool fallback) =>
        f.TryGetValue(key, out var v) ? v == "1" : fallback;

    /// <summary>One digit of a flag string such as "110" (the files pack several on/off settings this way).</summary>
    private static bool Flag(string? flags, int index) => flags != null && flags.Length > index && flags[index] == '1';

    /// <summary>Colours are stored as Windows COLORREF numbers (0x00BBGGRR).</summary>
    private static string Color(string? value, string fallback)
    {
        if (!int.TryParse(value, out int c)) return fallback;
        return $"#{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}";
    }
}
