using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Ax206Tool;

// Small test tool for an AX206 USB display:
//   Ax206Tool info                         show the display's resolution
//   Ax206Tool brightness <0-7>             set the backlight
//   Ax206Tool test [--rotate 0|90|180|270] draw a test pattern
//   Ax206Tool image <file> [--rotate ...]  show a picture, scaled to fit
try
{
    string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "info";
    int rotate = 0;
    int r = Array.IndexOf(args, "--rotate");
    if (r >= 0 && r + 1 < args.Length) rotate = int.Parse(args[r + 1]);

    using var display = Ax206Display.Open();
    Console.WriteLine($"AX206 display: {display.Width} x {display.Height}");

    switch (command)
    {
        case "info":
            break;
        case "brightness":
            display.SetBacklight(int.Parse(args[1]));
            Console.WriteLine($"Backlight set to {args[1]}.");
            break;
        case "test":
            Show(display, rotate, (g, w, h) => DrawTestPattern(g, w, h, display, rotate));
            Console.WriteLine("Test pattern sent.");
            break;
        case "image":
            using (var picture = Image.FromFile(args[1]))
                Show(display, rotate, (g, w, h) => DrawFitted(g, picture, w, h));
            Console.WriteLine("Image sent.");
            break;
        default:
            Console.Error.WriteLine("Commands: info | brightness <0-7> | test [--rotate N] | image <file> [--rotate N]");
            return 2;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

// Draws on a canvas in the logical orientation, then rotates it to the panel's physical one and sends it.
static void Show(Ax206Display display, int rotate, Action<Graphics, int, int> draw)
{
    bool sideways = rotate is 90 or 270;
    int w = sideways ? display.Height : display.Width;
    int h = sideways ? display.Width : display.Height;

    using var canvas = new Bitmap(w, h, PixelFormat.Format24bppRgb);
    using (var g = Graphics.FromImage(canvas))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Black);
        draw(g, w, h);
    }
    canvas.RotateFlip(rotate switch
    {
        90 => RotateFlipType.Rotate270FlipNone,
        180 => RotateFlipType.Rotate180FlipNone,
        270 => RotateFlipType.Rotate90FlipNone,
        _ => RotateFlipType.RotateNoneFlipNone,
    });
    display.Blit(0, 0, display.Width, display.Height, ToRgb565(canvas));
}

// 24-bit pixels to the panel's 16-bit format, high byte first.
static byte[] ToRgb565(Bitmap bitmap)
{
    var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
    var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
    try
    {
        var row = new byte[data.Stride];
        var output = new byte[bitmap.Width * bitmap.Height * 2];
        int o = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
            for (int x = 0; x < bitmap.Width; x++)
            {
                byte b = row[x * 3], g = row[x * 3 + 1], r = row[x * 3 + 2];
                output[o++] = (byte)((r & 0xF8) | (g >> 5));
                output[o++] = (byte)(((g & 0x1C) << 3) | (b >> 3));
            }
        }
        return output;
    }
    finally
    {
        bitmap.UnlockBits(data);
    }
}

static void DrawTestPattern(Graphics g, int w, int h, Ax206Display display, int rotate)
{
    Color[] bars = [Color.White, Color.Yellow, Color.Cyan, Color.Lime, Color.Magenta, Color.Red, Color.Blue, Color.Black];
    int barHeight = h / 4;
    for (int i = 0; i < bars.Length; i++)
    {
        using var brush = new SolidBrush(bars[i]);
        g.FillRectangle(brush, i * w / bars.Length, 0, w / bars.Length + 1, barHeight);
    }
    using (var gradient = new LinearGradientBrush(new Rectangle(0, barHeight, w, barHeight / 2), Color.Black, Color.White, 0f))
        g.FillRectangle(gradient, 0, barHeight, w, barHeight / 2);

    using var frame = new Pen(Color.FromArgb(0, 200, 255), 3);
    g.DrawRectangle(frame, 1, 1, w - 3, h - 3);

    using var big = new Font("Segoe UI", 20, FontStyle.Bold);
    using var small = new Font("Segoe UI", 12);
    float y = barHeight * 1.7f;
    g.DrawString("AX206 test", big, Brushes.White, 12, y);
    g.DrawString($"Panel {display.Width} x {display.Height}, rotated {rotate}°", small, Brushes.LightGray, 14, y + 40);
    g.DrawString(DateTime.Now.ToString("HH:mm:ss"), small, Brushes.LightGray, 14, y + 64);
    g.DrawString("↑ top", small, Brushes.Orange, w / 2f - 20, barHeight + barHeight / 2f + 4);
}

static void DrawFitted(Graphics g, Image picture, int w, int h)
{
    double scale = Math.Min((double)w / picture.Width, (double)h / picture.Height);
    int pw = (int)(picture.Width * scale), ph = (int)(picture.Height * scale);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.DrawImage(picture, (w - pw) / 2, (h - ph) / 2, pw, ph);
}
