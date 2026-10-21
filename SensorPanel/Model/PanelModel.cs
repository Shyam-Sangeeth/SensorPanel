using System.Text.Json;
using System.Text.Json.Serialization;

namespace SensorPanel.Model;

/// <summary>How a property is edited in the properties panel.</summary>
public enum EditKind { Text, Number, Decimal, Bool, Color, Font, Sensor, Image, Choice }

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
}

/// <summary>The whole panel: display settings plus the items drawn on it.</summary>
public sealed class PanelLayout
{
    [Edit("Background", "Appearance", EditKind.Color)]
    public string Background { get; set; } = "#000000";

    [Edit("Rotation", "Display", EditKind.Choice, Choices = ["0", "90", "180", "270"])]
    public string Rotation { get; set; } = "90";

    [Edit("Backlight (0-7)", "Display", EditKind.Number, Min = 0, Max = 7)]
    public int Brightness { get; set; } = 7;

    [Edit("Refresh every (ms)", "Display", EditKind.Number, Min = 250, Max = 10000)]
    public int RefreshMs { get; set; } = 1000;

    public List<PanelItem> Items { get; set; } = [];

    public int RotationDegrees => int.TryParse(Rotation, out int r) ? r : 0;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static PanelLayout Deserialize(string json) => JsonSerializer.Deserialize<PanelLayout>(json, Json) ?? new PanelLayout();

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
public abstract class PanelItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Edit("X", "Position", EditKind.Number, Min = -1000, Max = 2000)]
    public int X { get; set; }

    [Edit("Y", "Position", EditKind.Number, Min = -1000, Max = 2000)]
    public int Y { get; set; }

    [Edit("Visible", "Position", EditKind.Bool)]
    public bool Visible { get; set; } = true;

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
