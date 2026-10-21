using System.Drawing;

namespace SensorPanel.Plugins;

/// <summary>
/// A kind of display SensorPanel can drive. Plugins implement this in a public class with a parameterless constructor;
/// SensorPanel finds it in any DLL in its plugins folder. Display types appear in Settings → Display.
/// </summary>
public interface IDisplayDriver
{
    /// <summary>A stable identifier, saved in the settings (e.g. "ax206").</summary>
    string Id { get; }

    /// <summary>Shown in the display type list, e.g. "AX206 USB display".</summary>
    string Name { get; }

    /// <summary>One or two sentences shown under the list: what hardware this is for, what's needed.</summary>
    string Description { get; }

    /// <summary>Settings the user fills in (port, file name, size…). Can be empty.</summary>
    IReadOnlyList<DisplaySetting> Settings { get; }

    /// <summary>The panel's size (in the display's own orientation) while it isn't connected, for laying out the panel.</summary>
    Size DefaultSize(IReadOnlyDictionary<string, string> settings);

    /// <summary>
    /// Finds and opens the display. Return null if it isn't there (SensorPanel tries again every few seconds); throw with a
    /// helpful message for other problems (it's shown in the status line). Called on SensorPanel's display thread.
    /// </summary>
    IDisplay? Connect(IReadOnlyDictionary<string, string> settings);

    /// <summary>
    /// Optional: the display's own resolution if it can be read without connecting (e.g. a monitor's), or null. When the
    /// display type has "width" and "height" settings, SensorPanel fills them with it when the display is chosen.
    /// </summary>
    Size? DetectSize(IReadOnlyDictionary<string, string> settings) => null;
}

/// <summary>A setting of a display type. Values are saved and passed back as strings.</summary>
/// <param name="Key">Stable name the value is stored under.</param>
/// <param name="Label">Shown next to the box.</param>
/// <param name="DefaultValue">Used until the user changes it.</param>
/// <param name="Description">Optional hint shown under the box.</param>
public sealed record DisplaySetting(string Key, string Label, string DefaultValue, string Description = "")
{
    /// <summary>If set, the value is picked from these (a dropdown) instead of typed.</summary>
    public IReadOnlyList<string>? Choices { get; init; }
}

/// <summary>A connected display. Only used from SensorPanel's display thread, one call at a time.</summary>
public interface IDisplay : IDisposable
{
    /// <summary>Shown in the status line: "Display connected: {Name} (width × height)".</summary>
    string Name { get; }

    /// <summary>The panel's size in pixels, in the display's own orientation (SensorPanel rotates the panel to match).</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>Backlight from 0 (off) to 100. Ignore it if the display can't change brightness.</summary>
    void SetBrightness(int percent);

    /// <summary>
    /// Shows a frame. <paramref name="changed"/> lists the areas that differ from the previous frame (the whole frame the
    /// first time and after reconnecting), so slow displays can send just those; drawing everything is fine too. Throw if
    /// the display is gone: SensorPanel then disposes it and reconnects.
    /// </summary>
    void Draw(DisplayFrame frame, IReadOnlyList<Rectangle> changed);
}

/// <summary>A frame for the display: 32-bit pixels, 4 bytes each in B, G, R, A order, row by row, no padding.</summary>
public sealed class DisplayFrame(int width, int height, byte[] pixels)
{
    public int Width => width;
    public int Height => height;
    public byte[] Pixels => pixels;

    /// <summary>
    /// The pixels of <paramref name="area"/> as 16-bit RGB565 (the format of most small LCDs), row by row.
    /// <paramref name="bigEndian"/> puts the high byte first.
    /// </summary>
    public byte[] ToRgb565(Rectangle area, bool bigEndian)
    {
        var output = new byte[area.Width * area.Height * 2];
        int o = 0;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            int i = (y * width + area.Left) * 4;
            for (int x = 0; x < area.Width; x++, i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                byte high = (byte)((r & 0xF8) | (g >> 5)), low = (byte)(((g & 0x1C) << 3) | (b >> 3));
                output[o++] = bigEndian ? high : low;
                output[o++] = bigEndian ? low : high;
            }
        }
        return output;
    }

    /// <summary>The whole frame as RGB565.</summary>
    public byte[] ToRgb565(bool bigEndian) => ToRgb565(new Rectangle(0, 0, width, height), bigEndian);
}
