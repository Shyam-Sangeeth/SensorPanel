using System.Text.Json;

namespace SensorPanel.Model;

/// <summary>App preferences that aren't part of the panel design.</summary>
public sealed class AppSettings
{
    public bool OutputEnabled { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>The chosen display type (a driver's ID; "ax206" is built in).</summary>
    public string DisplayType { get; set; } = "ax206";

    /// <summary>Each display type's settings, by driver ID, then setting key.</summary>
    public Dictionary<string, Dictionary<string, string>> DisplaySettings { get; set; } = [];
}

/// <summary>Where the panel and its pictures are kept: %APPDATA%\SensorPanel.</summary>
public sealed class PanelStore
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SensorPanel");

    public static string ImageFolder { get; } = Path.Combine(Folder, "images");


    private static string LayoutPath => Path.Combine(Folder, "panel.json");
    private static string SettingsPath => Path.Combine(Folder, "settings.json");

    /// <summary>
    /// The saved panel, or an empty one the first time (Import brings one in); <paramref name="note"/> says if the saved
    /// one couldn't be read.
    /// </summary>
    public PanelLayout Load(out string? note)
    {
        note = null;
        try
        {
            if (File.Exists(LayoutPath))
            {
                var saved = PanelLayout.Deserialize(File.ReadAllText(LayoutPath));
                if (PanelImporter.UseOwnSensors(saved)) Save(saved);
                return saved;
            }
        }
        catch (Exception ex)
        {
            note = $"Couldn't read the saved panel ({ex.Message}).";
        }

        return new PanelLayout();
    }

    public void Save(PanelLayout layout)
    {
        Directory.CreateDirectory(Folder);
        string temp = LayoutPath + ".tmp";
        File.WriteAllText(temp, layout.Serialize());
        File.Move(temp, LayoutPath, overwrite: true);
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Copies a picture into the app's folder so the panel keeps working if the original moves.</summary>
    public static string ImportImage(string source)
    {
        Directory.CreateDirectory(ImageFolder);
        if (Path.GetDirectoryName(Path.GetFullPath(source))?.Equals(ImageFolder, StringComparison.OrdinalIgnoreCase) == true)
            return source;
        string name = Path.GetFileNameWithoutExtension(source);
        string ext = Path.GetExtension(source);
        string target = Path.Combine(ImageFolder, name + ext);
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(ImageFolder, $"{name} ({i}){ext}");
        File.Copy(source, target);
        return target;
    }
}
