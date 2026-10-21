using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SensorPanel.Plugins;

namespace SensorPanel.Plugin.ImageFile;

/// <summary>
/// A "display" that saves the panel as a PNG picture every time it changes, for OBS, Rainmeter, a web page or any program
/// that can show a picture. Also an example of a SensorPanel display plugin.
/// </summary>
public sealed class ImageFileDriver : IDisplayDriver
{
    public string Id => "image-file";
    public string Name => "Picture file (PNG)";
    public string Description =>
        "Saves the panel as a PNG picture whenever it changes, for OBS, Rainmeter, a web page or another program to show. No hardware needed.";

    public IReadOnlyList<DisplaySetting> Settings { get; } =
    [
        new("file", "File", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SensorPanel.png"),
            "Where to save the picture. It's replaced in one step, so readers never see half a picture."),
        new("width", "Width (pixels)", "480"),
        new("height", "Height (pixels)", "320", "The panel's rotation still applies: with rotation 90, a 480 × 320 picture shows the panel sideways; use rotation 0 and 320 × 480 for an upright one."),
    ];

    public Size DefaultSize(IReadOnlyDictionary<string, string> settings) => new(Number(settings, "width", 480), Number(settings, "height", 320));

    public IDisplay? Connect(IReadOnlyDictionary<string, string> settings)
    {
        string file = settings.TryGetValue("file", out var f) && f.Trim().Length > 0 ? Environment.ExpandEnvironmentVariables(f.Trim()) : "";
        if (file.Length == 0) throw new InvalidOperationException("Picture file: choose where to save the picture (Settings → Display).");
        string? folder = Path.GetDirectoryName(Path.GetFullPath(file));
        if (folder != null) Directory.CreateDirectory(folder);
        var size = DefaultSize(settings);
        return new PictureFile(Path.GetFullPath(file), size.Width, size.Height);
    }

    private static int Number(IReadOnlyDictionary<string, string> settings, string key, int fallback) =>
        settings.TryGetValue(key, out var text) && int.TryParse(text, out int value) ? Math.Clamp(value, 16, 4096) : fallback;

    private sealed class PictureFile(string path, int width, int height) : IDisplay
    {
        public string Name => "Picture file " + Path.GetFileName(path);
        public int Width => width;
        public int Height => height;

        public void SetBrightness(int percent) { } // a picture has no backlight

        public void Draw(DisplayFrame frame, IReadOnlyList<Rectangle> changed)
        {
            using var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < frame.Height; y++)
                    Marshal.Copy(frame.Pixels, y * frame.Width * 4, data.Scan0 + y * data.Stride, frame.Width * 4);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            string temp = path + ".tmp";
            bitmap.Save(temp, ImageFormat.Png);
            File.Move(temp, path, overwrite: true);
        }

        public void Dispose() { }
    }
}
