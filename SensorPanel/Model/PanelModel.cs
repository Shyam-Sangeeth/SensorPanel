using System.Text.Json;
using System.Text.Json.Serialization;

namespace SensorPanel.Model;

/// <summary>How a property is edited in the properties panel.</summary>
public enum EditKind { Text, Number, Decimal, Bool, Color, Font, Sensor, Image, Choice, Page, Cells }

/// <summary>Describes a property for the generated properties panel.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EditAttribute(string label, string group, EditKind kind) : Attribute
{
    public string Label => label;
    public string Group => group;
    public EditKind Kind => kind;
    public double Min { get; init; } = double.MinValue;
    public double Max { get; init; } = double.MaxValue;
    /// <summary>For <see cref="EditKind.Choice"/>: the allowed values.</summary>
    public string[] Choices { get; init; } = [];
    /// <summary>Only shown when the panel has more than one page.</summary>
    public bool MultiPageOnly { get; init; }
    /// <summary>Other settings depend on this one, so the properties panel is rebuilt after it changes.</summary>
    public bool Rebuilds { get; init; }
}

/// <summary>An item whose properties panel shows some settings only in some cases.</summary>
public interface IConditionalEdit
{
    bool Shows(string property);
}

/// <summary>The whole panel: display settings plus the items drawn on it.</summary>
public sealed class PanelLayout
{
    [Edit("Background", "Appearance", EditKind.Color)]
    public string Background { get; set; } = "#000000";

    [Edit("Rotation", "Display", EditKind.Choice, Choices = ["0", "90", "180", "270"])]
    public string Rotation { get; set; } = "90";

    /// <summary>
    /// The rotation the design was made for, which the panel is drawn and sent with. With another <see cref="Rotation"/>,
    /// the preview shows the screen turned that way, with the design turned along with it.
    /// </summary>
    [Edit("Designed for rotation", "Display", EditKind.Choice, Choices = ["0", "90", "180", "270"])]
    public string DesignRotation { get; set; } = "90";

    [Edit("Backlight (0-7)", "Display", EditKind.Number, Min = 0, Max = 7)]
    public int Brightness { get; set; } = 7;

    [Edit("Refresh every (ms)", "Display", EditKind.Number, Min = 250, Max = 10000)]
    public int RefreshMs { get; set; } = 1000;

    [Edit("Switch page every (s)", "Pages", EditKind.Number, Min = 1, Max = 3600, MultiPageOnly = true)]
    public int PageSeconds { get; set; } = 10;

    /// <summary>How many pages the panel has. The display shows them in turn, <see cref="PageSeconds"/> each.</summary>
    public int PageCount { get; set; } = 1;

    public List<PanelItem> Items { get; set; } = [];

    public int RotationDegrees => int.TryParse(Rotation, out int r) ? r : 0;

    /// <summary>The rotation the panel is drawn and sent with: the one it was designed for.</summary>
    [JsonIgnore]
    public int DesignDegrees => int.TryParse(DesignRotation, out int d) ? d : RotationDegrees;

    /// <summary>
    /// How far the preview is turned (clockwise, in degrees): the screen and the design on it, as a screen mounted for
    /// <see cref="Rotation"/> shows a panel made for <see cref="DesignRotation"/>. The frame is turned anticlockwise by
    /// the design's rotation before it's sent, so the screen's own picture doesn't change.
    /// </summary>
    [JsonIgnore]
    public int PreviewTurn => (RotationDegrees - DesignDegrees + 360) % 360;

    /// <summary>The items drawn on a page (from 1): its own and those on every page, in drawing order.</summary>
    public IEnumerable<PanelItem> OnPage(int page) => Items.Where(i => i.Page == PanelItem.EveryPage || i.Page == page);

    /// <summary>Every sensor the panel shows (on any page).</summary>
    public IEnumerable<string> SensorKeys() =>
        Items.OfType<ISensorItem>().Select(i => i.Sensor).Concat(Items.OfType<TableItem>().SelectMany(t => t.SensorKeys()));

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static PanelLayout Deserialize(string json)
    {
        var layout = JsonSerializer.Deserialize<PanelLayout>(json, Json) ?? new PanelLayout();
        // Panels from before this setting were designed for the rotation they have.
        if (!json.Contains($"\"{nameof(DesignRotation)}\"", StringComparison.Ordinal)) layout.DesignRotation = layout.Rotation;
        // Every item's page exists (panels from before pages have none, so they're all on page 1).
        foreach (var item in layout.Items) if (item.Page < PanelItem.EveryPage) item.Page = 1;
        layout.PageCount = Math.Max(1, Math.Max(layout.PageCount, layout.Items.Select(i => i.Page).DefaultIfEmpty(1).Max()));
        return layout;
    }

    /// <summary>A deep copy (the engine renders its own copy while the editor changes the original).</summary>
    public PanelLayout Clone() => Deserialize(Serialize());
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LabelItem), "label")]
[JsonDerivedType(typeof(ValueItem), "value")]
[JsonDerivedType(typeof(BarItem), "bar")]
[JsonDerivedType(typeof(GaugeItem), "gauge")]
[JsonDerivedType(typeof(GraphItem), "graph")]
[JsonDerivedType(typeof(ImageItem), "image")]
[JsonDerivedType(typeof(TableItem), "table")]
public abstract class PanelItem
{
    /// <summary>The <see cref="Page"/> of items shown on every page.</summary>
    public const int EveryPage = 0;

    public Guid Id { get; set; } = Guid.NewGuid();

    [Edit("X", "Position", EditKind.Number, Min = -1000, Max = 2000)]
    public int X { get; set; }

    [Edit("Y", "Position", EditKind.Number, Min = -1000, Max = 2000)]
    public int Y { get; set; }

    [Edit("Visible", "Position", EditKind.Bool)]
    public bool Visible { get; set; } = true;

    /// <summary>The page the item is on (from 1), or <see cref="EveryPage"/>.</summary>
    [Edit("Page", "Position", EditKind.Page, MultiPageOnly = true)]
    public int Page { get; set; } = 1;

    [JsonIgnore] public abstract string KindName { get; }

    /// <summary>What the item list shows.</summary>
    [JsonIgnore] public virtual string Title => KindName;
}

/// <summary>Common text settings.</summary>
public abstract class TextItem : PanelItem
{
    [Edit("Font", "Text", EditKind.Font)]
    public string Font { get; set; } = "Calibri";

    [Edit("Size (pt)", "Text", EditKind.Decimal, Min = 4, Max = 96)]
    public double FontSize { get; set; } = 12;

    [Edit("Bold", "Text", EditKind.Bool)]
    public bool Bold { get; set; }

    [Edit("Italic", "Text", EditKind.Bool)]
    public bool Italic { get; set; }
}

/// <summary>Fixed text.</summary>
public sealed class LabelItem : TextItem
{
    [Edit("Text", "Content", EditKind.Text)]
    public string Text { get; set; } = "Label";

    [Edit("Colour", "Text", EditKind.Color)]
    public string Color { get; set; } = "#FFFFFF";

    public override string KindName => "Text";
    public override string Title => Text;
}

/// <summary>Things that show a sensor.</summary>
public interface ISensorItem
{
    string Sensor { get; set; }
}

/// <summary>A sensor value as one line of text: "label value unit" (a "simple" item in LCD files).</summary>
public sealed class ValueItem : TextItem, ISensorItem
{
    [Edit("Sensor", "Content", EditKind.Sensor)]
    public string Sensor { get; set; } = "";

    [Edit("Label", "Content", EditKind.Text)]
    public string Label { get; set; } = "";

    [Edit("Show label", "Content", EditKind.Bool)]
    public bool ShowLabel { get; set; } = true;

    [Edit("Unit", "Content", EditKind.Text)]
    public string Unit { get; set; } = "";

    [Edit("Show unit", "Content", EditKind.Bool)]
    public bool ShowUnit { get; set; } = true;

    [Edit("Decimals (-1 = auto)", "Content", EditKind.Number, Min = -1, Max = 4)]
    public int Decimals { get; set; } = -1;

    [Edit("Colour", "Text", EditKind.Color)]
    public string Color { get; set; } = "#FFFFFF";

    public override string KindName => "Value";
    public override string Title => Label.Length > 0 ? Label : Sensor;
}

/// <summary>A sensor with label, value, unit and a coloured bar (a sensor item in LCD files).</summary>
public sealed class BarItem : TextItem, ISensorItem
{
    [Edit("Sensor", "Content", EditKind.Sensor)]
    public string Sensor { get; set; } = "";

    [Edit("Label", "Content", EditKind.Text)]
    public string Label { get; set; } = "";

    [Edit("Show label", "Content", EditKind.Bool)]
    public bool ShowLabel { get; set; } = true;

    [Edit("Show value", "Content", EditKind.Bool)]
    public bool ShowValue { get; set; } = true;

    [Edit("Unit", "Content", EditKind.Text)]
    public string Unit { get; set; } = "";

    [Edit("Show unit", "Content", EditKind.Bool)]
    public bool ShowUnit { get; set; } = true;

    [Edit("Decimals (-1 = auto)", "Content", EditKind.Number, Min = -1, Max = 4)]
    public int Decimals { get; set; } = -1;

    [Edit("Width", "Content", EditKind.Number, Min = 10, Max = 2000)]
    public int Width { get; set; } = 200;

    [Edit("Unit width", "Content", EditKind.Number, Min = 0, Max = 200)]
    public int UnitWidth { get; set; } = 20;

    [Edit("Label colour", "Text", EditKind.Color)]
    public string LabelColor { get; set; } = "#FFFFFF";

    [Edit("Value colour", "Text", EditKind.Color)]
    public string ValueColor { get; set; } = "#FFFFFF";

    [Edit("Unit colour", "Text", EditKind.Color)]
    public string UnitColor { get; set; } = "#FFFFFF";

    [Edit("Show bar", "Bar", EditKind.Bool)]
    public bool ShowBar { get; set; } = true;

    [Edit("Bar placement", "Bar", EditKind.Choice, Choices = ["Below", "Inline"])]
    public string BarPlacement { get; set; } = "Below";

    [Edit("Bar width", "Bar", EditKind.Number, Min = 1, Max = 2000)]
    public int BarWidth { get; set; } = 200;

    [Edit("Bar height", "Bar", EditKind.Number, Min = 1, Max = 200)]
    public int BarHeight { get; set; } = 15;

    [Edit("Bar indent", "Bar", EditKind.Number, Min = 0, Max = 500)]
    public int BarIndent { get; set; }

    [Edit("Bar frame", "Bar", EditKind.Bool)]
    public bool BarFrame { get; set; } = true;

    [Edit("Frame colour", "Bar", EditKind.Color)]
    public string BarFrameColor { get; set; } = "#666666";

    [Edit("Minimum", "Bar ranges", EditKind.Decimal)]
    public double Min { get; set; }

    [Edit("Maximum", "Bar ranges", EditKind.Decimal)]
    public double Max { get; set; } = 100;

    [Edit("Colour below limit 1", "Bar ranges", EditKind.Color)]
    public string MinColor { get; set; } = "#FFFFFF";

    [Edit("Background below limit 1", "Bar ranges", EditKind.Color)]
    public string MinBackground { get; set; } = "#333333";

    [Edit("Limit 1 (empty = off)", "Bar ranges", EditKind.Text)]
    public string Limit1 { get; set; } = "";

    [Edit("Colour from limit 1", "Bar ranges", EditKind.Color)]
    public string Limit1Color { get; set; } = "#FEE2E2";

    [Edit("Background from limit 1", "Bar ranges", EditKind.Color)]
    public string Limit1Background { get; set; } = "#333333";

    [Edit("Limit 2 (empty = off)", "Bar ranges", EditKind.Text)]
    public string Limit2 { get; set; } = "";

    [Edit("Colour from limit 2", "Bar ranges", EditKind.Color)]
    public string Limit2Color { get; set; } = "#FCA5A5";

    [Edit("Background from limit 2", "Bar ranges", EditKind.Color)]
    public string Limit2Background { get; set; } = "#333333";

    [Edit("Limit 3 (empty = off)", "Bar ranges", EditKind.Text)]
    public string Limit3 { get; set; } = "";

    [Edit("Colour from limit 3", "Bar ranges", EditKind.Color)]
    public string Limit3Color { get; set; } = "#FF0000";

    [Edit("Background from limit 3", "Bar ranges", EditKind.Color)]
    public string Limit3Background { get; set; } = "#333333";

    public override string KindName => "Bar";
    public override string Title => Label.Length > 0 ? Label : Sensor;
}

/// <summary>A round dial.</summary>
public sealed class GaugeItem : TextItem, ISensorItem
{
    [Edit("Sensor", "Content", EditKind.Sensor)]
    public string Sensor { get; set; } = "";

    [Edit("Label", "Content", EditKind.Text)]
    public string Label { get; set; } = "";

    [Edit("Size", "Gauge", EditKind.Number, Min = 20, Max = 400)]
    public int Size { get; set; } = 52;

    [Edit("Minimum", "Gauge", EditKind.Decimal)]
    public double Min { get; set; }

    [Edit("Maximum", "Gauge", EditKind.Decimal)]
    public double Max { get; set; } = 100;

    [Edit("Show value", "Gauge", EditKind.Bool)]
    public bool ShowValue { get; set; } = true;

    [Edit("Decimals (-1 = auto)", "Gauge", EditKind.Number, Min = -1, Max = 4)]
    public int Decimals { get; set; } = -1;

    [Edit("Value colour", "Text", EditKind.Color)]
    public string ValueColor { get; set; } = "#FFFFFF";

    [Edit("Face", "Gauge", EditKind.Color)]
    public string FaceColor { get; set; } = "#141414";

    [Edit("Rim", "Gauge", EditKind.Color)]
    public string RimColor { get; set; } = "#5A5A5A";

    [Edit("Needle", "Gauge", EditKind.Color)]
    public string NeedleColor { get; set; } = "#FF3B30";

    public override string KindName => "Gauge";
    public override string Title => Label.Length > 0 ? Label : Sensor;
}

/// <summary>A history graph of a sensor.</summary>
public sealed class GraphItem : TextItem, ISensorItem
{
    [Edit("Sensor", "Content", EditKind.Sensor)]
    public string Sensor { get; set; } = "";

    [Edit("Label", "Content", EditKind.Text)]
    public string Label { get; set; } = "";

    [Edit("Width", "Graph", EditKind.Number, Min = 10, Max = 2000)]
    public int Width { get; set; } = 200;

    [Edit("Height", "Graph", EditKind.Number, Min = 10, Max = 2000)]
    public int Height { get; set; } = 50;

    [Edit("Style", "Graph", EditKind.Choice, Choices = ["Line", "Area", "Bars"])]
    public string Style { get; set; } = "Line";

    [Edit("Step (px per sample)", "Graph", EditKind.Number, Min = 1, Max = 50)]
    public int Step { get; set; } = 1;

    [Edit("Line thickness", "Graph", EditKind.Number, Min = 1, Max = 10)]
    public int Thickness { get; set; } = 1;

    [Edit("Minimum", "Graph", EditKind.Decimal)]
    public double Min { get; set; }

    [Edit("Maximum", "Graph", EditKind.Decimal)]
    public double Max { get; set; } = 100;

    [Edit("Auto scale", "Graph", EditKind.Bool)]
    public bool AutoScale { get; set; }

    [Edit("Line colour", "Graph", EditKind.Color)]
    public string LineColor { get; set; } = "#0080FF";

    [Edit("Show background", "Graph", EditKind.Bool)]
    public bool ShowBackground { get; set; } = true;

    [Edit("Background", "Graph", EditKind.Color)]
    public string BackgroundColor { get; set; } = "#000000";

    [Edit("Show frame", "Graph", EditKind.Bool)]
    public bool ShowFrame { get; set; } = true;

    [Edit("Frame colour", "Graph", EditKind.Color)]
    public string FrameColor { get; set; } = "#666666";

    [Edit("Show grid", "Graph", EditKind.Bool)]
    public bool ShowGrid { get; set; }

    [Edit("Grid spacing", "Graph", EditKind.Number, Min = 2, Max = 500)]
    public int GridSpacing { get; set; } = 10;

    [Edit("Grid colour", "Graph", EditKind.Color)]
    public string GridColor { get; set; } = "#008000";

    [Edit("Show scale", "Graph", EditKind.Bool)]
    public bool ShowScale { get; set; }

    [Edit("Scale colour", "Text", EditKind.Color)]
    public string ScaleColor { get; set; } = "#FFFFFF";

    public override string KindName => "Graph";
    public override string Title => Label.Length > 0 ? Label : Sensor;
}

/// <summary>A table cell: a sensor, or text when no sensor is chosen.</summary>
public sealed class TableCell
{
    public string Sensor { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// Rows and columns of sensors and text, e.g. the processes using the most CPU: either filled in from a list of top
/// processes, or cell by cell.
/// </summary>
public sealed class TableItem : TextItem, IConditionalEdit
{
    public const string TopCpu = "Top CPU processes";
    public const string TopGpu = "Top GPU processes";
    public const string Custom = "Custom cells";
    public const int MaxRows = 20, MaxColumns = 6, MaxProcesses = 10;

    [Edit("Content", "Content", EditKind.Choice, Choices = [TopCpu, TopGpu, Custom], Rebuilds = true)]
    public string Source { get; set; } = TopCpu;

    [Edit("Rows", "Content", EditKind.Number, Min = 1, Max = MaxRows, Rebuilds = true)]
    public int Rows { get; set; } = 5;

    [Edit("Columns", "Content", EditKind.Number, Min = 1, Max = MaxColumns, Rebuilds = true)]
    public int Columns { get; set; } = 2;

    [Edit("Show header", "Content", EditKind.Bool)]
    public bool ShowHeader { get; set; } = true;

    [Edit("Column titles (a|b)", "Content", EditKind.Text)]
    public string Header { get; set; } = "";

    [Edit("Show units", "Content", EditKind.Bool)]
    public bool ShowUnits { get; set; } = true;

    [Edit("Decimals (-1 = auto)", "Content", EditKind.Number, Min = -1, Max = 4)]
    public int Decimals { get; set; } = -1;

    [Edit("Width", "Table", EditKind.Number, Min = 20, Max = 2000)]
    public int Width { get; set; } = 240;

    [Edit("Column widths (a|b)", "Table", EditKind.Text)]
    public string ColumnWidths { get; set; } = "";

    [Edit("Row spacing", "Table", EditKind.Number, Min = 0, Max = 100)]
    public int RowSpacing { get; set; } = 2;

    [Edit("Lines between rows", "Table", EditKind.Bool)]
    public bool ShowLines { get; set; }

    [Edit("Line colour", "Table", EditKind.Color)]
    public string LineColor { get; set; } = "#404040";

    [Edit("Colour", "Text", EditKind.Color)]
    public string Color { get; set; } = "#FFFFFF";

    [Edit("Header colour", "Text", EditKind.Color)]
    public string HeaderColor { get; set; } = "#A0A0A0";

    /// <summary>The cells row by row (used with <see cref="Custom"/>); there may be more than are shown.</summary>
    [Edit("Cells", "Cells", EditKind.Cells)]
    public List<List<TableCell>> Cells { get; set; } = [];

    public override string KindName => "Table";
    public override string Title => Source == Custom ? "Table" : Source;

    public bool Shows(string property) => property != nameof(Cells) || Source == Custom;

    [JsonIgnore] public bool IsProcessList => Source is TopCpu or TopGpu;

    /// <summary>The rows shown (a process list has at most <see cref="MaxProcesses"/>).</summary>
    [JsonIgnore] public int ShownRows => Math.Clamp(Rows, 1, IsProcessList ? MaxProcesses : MaxRows);

    [JsonIgnore] public int ShownColumns => Math.Clamp(Columns, 1, MaxColumns);

    /// <summary>What a cell shows (row and column from 0): a sensor, or text when there's none.</summary>
    public (string Sensor, string Text) Cell(int row, int column)
    {
        if (IsProcessList)
        {
            string kind = Source == TopCpu ? "cpu" : "gpu";
            return (column switch { 0 => $"proc/{kind}/{row + 1}/name", 1 => $"proc/{kind}/{row + 1}/usage", _ => "" }, "");
        }
        return row < Cells.Count && column < Cells[row].Count ? (Cells[row][column].Sensor, Cells[row][column].Text) : ("", "");
    }

    /// <summary>The column titles: as typed (separated by |), or for a process list "Process" and "CPU" or "GPU".</summary>
    public string[] Titles() =>
        Header.Trim().Length > 0 ? Header.Split('|').Select(t => t.Trim()).ToArray()
        : Source == TopCpu ? ["Process", "CPU"]
        : Source == TopGpu ? ["Process", "GPU"]
        : [];

    public IEnumerable<string> SensorKeys()
    {
        for (int r = 0; r < ShownRows; r++)
            for (int c = 0; c < ShownColumns; c++)
                if (Cell(r, c).Sensor is { Length: > 0 } sensor) yield return sensor;
    }

    /// <summary>Adds cells for any rows or columns added since (cells of removed ones are kept, in case they come back).</summary>
    public void AddMissingCells()
    {
        while (Cells.Count < ShownRows) Cells.Add([]);
        foreach (var row in Cells)
            while (row.Count < ShownColumns) row.Add(new TableCell());
    }
}

/// <summary>A picture (PNG, JPG, BMP or GIF), drawn with transparency.</summary>
public sealed class ImageItem : PanelItem
{
    [Edit("File", "Content", EditKind.Image)]
    public string File { get; set; } = "";

    [Edit("Width (0 = original)", "Content", EditKind.Number, Min = 0, Max = 2000)]
    public int Width { get; set; }

    [Edit("Height (0 = original)", "Content", EditKind.Number, Min = 0, Max = 2000)]
    public int Height { get; set; }

    public override string KindName => "Image";
    public override string Title => File.Length > 0 ? Path.GetFileName(File) : "Image";
}
