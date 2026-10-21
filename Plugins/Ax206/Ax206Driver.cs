using System.Drawing;
using SensorPanel.Plugins;

namespace SensorPanel.Plugin.Ax206;

/// <summary>Display type for AX206-based USB screens (USB ID 1908:0102), as a SensorPanel plugin.</summary>
public sealed class Ax206Driver : IDisplayDriver
{
    public string Id => "ax206";
    public string Name => "AX206 USB display";
    public string Description =>
        "Small USB screens with the AX206 chip (USB ID 1908:0102), such as 3.5\" sensor panels. Uses the libusb-win32 driver.";
    public IReadOnlyList<DisplaySetting> Settings => [];

    public Size DefaultSize(IReadOnlyDictionary<string, string> settings) => new(480, 320);

    public IDisplay? Connect(IReadOnlyDictionary<string, string> settings)
    {
        try
        {
            return new Connection(Ax206Display.Open());
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("No AX206", StringComparison.Ordinal))
        {
            return null; // not plugged in
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("claim", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The display is in use by another program. Close that program (or turn off its LCD output) to use it here.");
        }
    }

    private sealed class Connection(Ax206Display display) : IDisplay
    {
        public string Name => "AX206";
        public int Width => display.Width;
        public int Height => display.Height;

        public void SetBrightness(int percent) => display.SetBacklight((int)Math.Round(Math.Clamp(percent, 0, 100) * 7 / 100.0));

        public void Draw(DisplayFrame frame, IReadOnlyList<Rectangle> changed)
        {
            foreach (var area in changed)
                display.Blit(area.X, area.Y, area.Width, area.Height, frame.ToRgb565(area, bigEndian: true));
        }

        public void Dispose() => display.Dispose();
    }
}
