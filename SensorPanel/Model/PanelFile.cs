using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SensorPanel.Model;

/// <summary>
/// Exported panels (.sensorpanel): one JSON file holding the panel and its pictures, so it can be copied to another PC
/// or kept as a backup.
/// </summary>
public static class PanelFile
{
    public const string Extension = ".sensorpanel";

    /// <summary>File types Import understands: SensorPanel files, LCD backups and settings files.</summary>
    public const string ImportFilter =
        "Panels|*.sensorpanel;*.axlcd;*.ini|SensorPanel panels|*.sensorpanel|LCD backups|*.axlcd|Settings files|*.ini";

    public const string ExportFilter = "SensorPanel panel|*.sensorpanel";

    public static void Export(PanelLayout layout, string path)
    {
        var copy = layout.Clone();
        var images = new JsonObject();
        foreach (var image in copy.Items.OfType<ImageItem>())
        {
            if (!File.Exists(image.File)) continue;
            string name = Path.GetFileName(image.File);
            images[name] = Convert.ToBase64String(File.ReadAllBytes(image.File));
            image.File = name;
        }
        var root = new JsonObject
        {
            ["format"] = "SensorPanel",
            ["version"] = 1,
            ["panel"] = JsonNode.Parse(copy.Serialize()),
            ["images"] = images,
        };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Opens any supported file. Pictures are saved into <paramref name="imageFolder"/>.</summary>
    public static PanelLayout Import(string path, string imageFolder, PanelLayout current)
    {
        if (!Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase))
            return PanelImporter.Import(path, imageFolder, current);

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (root?["format"]?.GetValue<string>() != "SensorPanel" || root["panel"] is not JsonObject panel)
            throw new InvalidDataException("This isn't a SensorPanel file.");
        var layout = PanelLayout.Deserialize(panel.ToJsonString());

        Directory.CreateDirectory(imageFolder);
        var saved = new Dictionary<string, string>();
        if (root["images"] is JsonObject images)
        {
            foreach (var (name, data) in images)
            {
                if (data == null) continue;
                saved[name] = SaveImage(imageFolder, Path.GetFileName(name), Convert.FromBase64String(data.GetValue<string>()));
            }
        }
        foreach (var image in layout.Items.OfType<ImageItem>())
            if (saved.TryGetValue(image.File, out var file)) image.File = file;

        // New IDs, so graph history and undo never mix the imported items up with the current ones.
        foreach (var item in layout.Items) item.Id = Guid.NewGuid();
        return layout;
    }

    /// <summary>Saves a picture, reusing an identical file and never overwriting a different one with the same name.</summary>
    private static string SaveImage(string folder, string name, byte[] bytes)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string target = Path.Combine(folder, name);
        for (int i = 2; File.Exists(target); i++)
        {
            if (SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(bytes))) return target;
            target = Path.Combine(folder, $"{stem} ({i}){ext}");
        }
        File.WriteAllBytes(target, bytes);
        return target;
    }
}
