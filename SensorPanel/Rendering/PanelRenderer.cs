using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;
using SensorPanel.Model;
using SensorPanel.Sensors;

namespace SensorPanel.Rendering;

/// <summary>Where the renderer gets sensor values and graph history from.</summary>
public interface IRenderSource
{
    Reading Read(string sensor);
    SensorInfo? Info(string sensor);
    IReadOnlyList<double> History(Guid item);
}

/// <summary>Draws a <see cref="PanelLayout"/> into a bitmap the size of the panel (in its rotated, logical orientation).</summary>
public static class PanelRenderer
{
    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    // Set for the duration of one Render call (renders only happen on the engine thread).
    [ThreadStatic] private static float _scale;
    [ThreadStatic] private static Dictionary<Font, Font>? _scaledFonts;

    /// <summary>
    /// Draws the panel. <paramref name="scale"/> above 1 draws a sharper, larger copy (for the editor's preview); layout and
    /// <paramref name="bounds"/> stay in panel pixels, so the result looks the same, only with more detail. Only the items
    /// on <paramref name="page"/> are drawn.
    /// </summary>
    public static Bitmap Render(PanelLayout layout, int page, int width, int height, IRenderSource source,
                                Dictionary<Guid, Rectangle>? bounds = null, double scale = 1)
    {
        scale = Math.Clamp(scale, 0.25, 8);
        var bitmap = new Bitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(ParseColor(layout.Background));
        _scale = (float)scale;
        _scaledFonts = [];
        if (scale != 1)
        {
            g.ScaleTransform((float)scale, (float)scale);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }
        using var fonts = new FontCache();
        try
        {
            DrawItems(g, layout.OnPage(page), source, bounds, fonts);
        }
        finally
        {
            foreach (var font in _scaledFonts.Values) font.Dispose();
            _scaledFonts = null;
            _scale = 1;
        }
        return bitmap;
    }

    private static void DrawItems(Graphics g, IEnumerable<PanelItem> items, IRenderSource source, Dictionary<Guid, Rectangle>? bounds, FontCache fonts)
    {
        foreach (var item in items)
        {
            if (!item.Visible) continue;
            Rectangle area;
            try
            {
                area = item switch
                {
                    LabelItem label => DrawText(g, fonts.Get(label), label.Text, label.X, label.Y, ParseColor(label.Color)),
                    ValueItem value => DrawValue(g, fonts.Get(value), value, source),
                    BarItem bar => DrawBar(g, fonts.Get(bar), bar, source),
                    GaugeItem gauge => DrawGauge(g, fonts.Get(gauge), gauge, source),
                    GraphItem graph => DrawGraph(g, fonts.Get(graph), graph, source),
                    ImageItem image => DrawImage(g, image),
                    TableItem table => DrawTable(g, fonts.Get(table), fonts.Get(table, bold: true), table, source),
                    _ => Rectangle.Empty,
                };
            }
            catch
            {
                area = new Rectangle(item.X, item.Y, 10, 10); // never let one bad item stop the panel
            }
            bounds?.Add(item.Id, area);
        }
    }

    // ---- items -------------------------------------------------------------------------------------------

    private static Rectangle DrawValue(Graphics g, Font font, ValueItem item, IRenderSource source)
    {
        string text = (item.ShowLabel && item.Label.Length > 0 ? item.Label + " " : "") +
                      Format(source, item.Sensor, item.Decimals) + (item.ShowUnit ? item.Unit : "");
        return DrawText(g, font, text, item.X, item.Y, ParseColor(item.Color));
    }

    private static Rectangle DrawBar(Graphics g, Font font, BarItem item, IRenderSource source)
    {
        int textHeight = TextRenderer.MeasureText(g, "Ag", font, Size.Empty, TextFlags).Height;
        var reading = source.Read(item.Sensor);
        bool inline = item.BarPlacement == "Inline";

        int barY = inline ? item.Y + (textHeight - item.BarHeight) / 2 : item.Y + textHeight + 1;
        if (item.ShowBar)
        {
            var bar = new Rectangle(item.X + item.BarIndent, barY, item.BarWidth, item.BarHeight);
            double value = reading.Value ?? item.Min;
            double fraction = item.Max > item.Min ? Math.Clamp((value - item.Min) / (item.Max - item.Min), 0, 1) : 0;
            var (fill, back) = BarColors(item, value);
            using (var backBrush = new SolidBrush(back)) g.FillRectangle(backBrush, bar);
            int filled = (int)Math.Round(bar.Width * fraction);
            if (filled > 0)
                using (var fillBrush = new SolidBrush(fill)) g.FillRectangle(fillBrush, bar.X, bar.Y, filled, bar.Height);
            if (item.BarFrame)
                using (var pen = new Pen(ParseColor(item.BarFrameColor))) Frame(g, pen, bar);
        }

        if (item.ShowLabel && item.Label.Length > 0)
            DrawText(g, font, item.Label, item.X, item.Y, ParseColor(item.LabelColor));

        int right = item.X + item.Width;
        if (item.ShowUnit && item.Unit.Length > 0)
        {
            right -= item.UnitWidth;
            DrawText(g, font, item.Unit, right, item.Y, ParseColor(item.UnitColor));
        }
        if (item.ShowValue)
        {
            string text = Format(source, item.Sensor, item.Decimals);
            int w = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFlags).Width;
            DrawText(g, font, text, right - w, item.Y, ParseColor(item.ValueColor));
        }

        int bottom = item.ShowBar && !inline ? barY + item.BarHeight : item.Y + textHeight;
        return Rectangle.FromLTRB(item.X, item.Y, Math.Max(item.X + item.Width, item.X + item.BarIndent + item.BarWidth), bottom);
    }

    /// <summary>The bar's fill and background colours for the range the value is in (limits left empty are skipped).</summary>
    private static (Color Fill, Color Back) BarColors(BarItem item, double value)
    {
        (string Fill, string Back) chosen = (item.MinColor, item.MinBackground);
        if (Limit(item.Limit1) is double l1 && value >= l1) chosen = (item.Limit1Color, item.Limit1Background);
        if (Limit(item.Limit2) is double l2 && value >= l2) chosen = (item.Limit2Color, item.Limit2Background);
        if (Limit(item.Limit3) is double l3 && value >= l3) chosen = (item.Limit3Color, item.Limit3Background);
        return (ParseColor(chosen.Fill), ParseColor(chosen.Back));
    }

    private static double? Limit(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    private static Rectangle DrawGauge(Graphics g, Font font, GaugeItem item, IRenderSource source)
    {
        var area = new Rectangle(item.X, item.Y, item.Size, item.Size);
        var reading = source.Read(item.Sensor);
        double fraction = item.Max > item.Min && reading.Value is double v ? Math.Clamp((v - item.Min) / (item.Max - item.Min), 0, 1) : 0;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = area.X + area.Width / 2f, cy = area.Y + area.Height / 2f, r = area.Width / 2f - 1;

        using (var face = new SolidBrush(ParseColor(item.FaceColor))) g.FillEllipse(face, cx - r, cy - r, r * 2, r * 2);
        using (var rim = new Pen(ParseColor(item.RimColor), Math.Max(2f, r / 12))) g.DrawEllipse(rim, cx - r, cy - r, r * 2, r * 2);

        // 270° scale from bottom-left to bottom-right: lit ticks run green → yellow → red up to the value.
        const int ticks = 19;
        float inner = r * 0.62f, outer = r * 0.86f;
        for (int i = 0; i < ticks; i++)
        {
            double t = i / (double)(ticks - 1);
            double angle = (135 + 270 * t) * Math.PI / 180;
            bool lit = t <= fraction && reading.Value.HasValue;
            var color = lit ? Ramp(t) : Color.FromArgb(55, 55, 55);
            using var pen = new Pen(color, Math.Max(2f, r / 8.5f));
            g.DrawLine(pen, cx + (float)Math.Cos(angle) * inner, cy + (float)Math.Sin(angle) * inner,
                            cx + (float)Math.Cos(angle) * outer, cy + (float)Math.Sin(angle) * outer);
        }

        double needle = (135 + 270 * fraction) * Math.PI / 180;
        using (var pen = new Pen(ParseColor(item.NeedleColor), Math.Max(2f, r / 10)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, cx, cy, cx + (float)Math.Cos(needle) * r * 0.66f, cy + (float)Math.Sin(needle) * r * 0.66f);
        float cap = Math.Max(3f, r / 6);
        using (var capBrush = new SolidBrush(ParseColor(item.RimColor))) g.FillEllipse(capBrush, cx - cap, cy - cap, cap * 2, cap * 2);
        g.SmoothingMode = oldMode;

        if (item.ShowValue)
        {
            string text = Format(source, item.Sensor, item.Decimals);
            var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFlags);
            DrawText(g, font, text, (int)(cx - size.Width / 2f), (int)(area.Bottom - size.Height - r * 0.08f), ParseColor(item.ValueColor));
        }
        return area;
    }

    private static Color Ramp(double t)
    {
        // Green (0) → yellow (0.5) → red (1).
        int red = (int)(255 * Math.Clamp(t * 2, 0, 1));
        int green = (int)(255 * Math.Clamp((1 - t) * 2, 0, 1));
        return Color.FromArgb(red, green, 40);
    }

    private static Rectangle DrawGraph(Graphics g, Font font, GraphItem item, IRenderSource source)
    {
        var area = new Rectangle(item.X, item.Y, item.Width, item.Height);
        var state = g.Save();
        g.SetClip(area);

        if (item.ShowBackground)
            using (var back = new SolidBrush(ParseColor(item.BackgroundColor))) g.FillRectangle(back, area);

        if (item.ShowGrid && item.GridSpacing > 1)
        {
            using var grid = new Pen(ParseColor(item.GridColor));
            for (int y = area.Bottom - 1 - item.GridSpacing; y > area.Top; y -= item.GridSpacing) g.DrawLine(grid, area.Left, y, area.Right, y);
            for (int x = area.Right - 1 - item.GridSpacing; x > area.Left; x -= item.GridSpacing) g.DrawLine(grid, x, area.Top, x, area.Bottom);
        }

        var history = source.History(item.Id);
        double min = item.Min, max = item.Max;
        if (item.AutoScale && history.Count > 0) max = Math.Max(min + 1, history.Max() * 1.1);
        if (max <= min) max = min + 1;

        int count = Math.Min(history.Count, item.Width / item.Step + 2);
        if (count > 0)
        {
            var points = new PointF[count];
            for (int i = 0; i < count; i++)
            {
                double value = history[history.Count - count + i];
                float x = area.Right - 1 - (count - 1 - i) * item.Step;
                float y = (float)(area.Bottom - 1 - Math.Clamp((value - min) / (max - min), 0, 1) * (area.Height - 1));
                points[i] = new PointF(x, y);
            }

            var color = ParseColor(item.LineColor);
            switch (item.Style)
            {
                case "Area":
                    using (var brush = new SolidBrush(color))
                    {
                        var poly = points.Append(new PointF(points[^1].X, area.Bottom)).Append(new PointF(points[0].X, area.Bottom)).ToArray();
                        g.FillPolygon(brush, poly);
                    }
                    break;
                case "Bars":
                    using (var pen = new Pen(color, Math.Max(1, item.Step - 1)))
                        foreach (var p in points) g.DrawLine(pen, p.X, area.Bottom - 1, p.X, p.Y);
                    break;
                default:
                    using (var pen = new Pen(color, item.Thickness))
                    {
                        if (points.Length == 1) g.DrawLine(pen, points[0].X - 1, points[0].Y, points[0].X, points[0].Y);
                        else g.DrawLines(pen, points);
                    }
                    break;
            }
        }

        if (item.ShowScale)
        {
            var scale = ParseColor(item.ScaleColor);
            DrawText(g, font, max.ToString("0.#", CultureInfo.CurrentCulture), area.X + 2, area.Y + 1, scale);
            int h = TextRenderer.MeasureText(g, "0", font, Size.Empty, TextFlags).Height;
            DrawText(g, font, min.ToString("0.#", CultureInfo.CurrentCulture), area.X + 2, area.Bottom - h - 1, scale);
        }

        g.Restore(state);
        if (item.ShowFrame)
            using (var frame = new Pen(ParseColor(item.FrameColor))) Frame(g, frame, area);
        return area;
    }

    private static Rectangle DrawImage(Graphics g, ImageItem item)
    {
        var image = ImageCache.Get(item.File);
        if (image == null) return new Rectangle(item.X, item.Y, Math.Max(item.Width, 20), Math.Max(item.Height, 20));
        int w = item.Width > 0 ? item.Width : image.Width;
        int h = item.Height > 0 ? item.Height : image.Height;
        g.DrawImage(image, new Rectangle(item.X, item.Y, w, h));
        return new Rectangle(item.X, item.Y, w, h);
    }

    /// <summary>
    /// A table: an optional header row in bold, then the cells. Columns holding numbers are right-aligned. Column widths
    /// are as given, or the columns after the first fit their contents and the first takes the rest of the width.
    /// </summary>
    private static Rectangle DrawTable(Graphics g, Font font, Font bold, TableItem item, IRenderSource source)
    {
        int rows = item.ShownRows, columns = item.ShownColumns;
        var cells = new string[rows, columns];
        var numeric = new bool[columns];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                var (sensor, text) = item.Cell(r, c);
                if (sensor.Length == 0)
                {
                    cells[r, c] = text;
                    continue;
                }
                var reading = source.Read(sensor);
                if (reading.Text != null) cells[r, c] = reading.Text;
                else if (reading.Value.HasValue)
                {
                    cells[r, c] = Format(source, sensor, item.Decimals) + (item.ShowUnits ? source.Info(sensor)?.Unit ?? "" : "");
                    numeric[c] = true;
                }
                else cells[r, c] = ""; // e.g. fewer busy processes than rows
            }
        }

        string[] titles = item.Titles();
        bool header = item.ShowHeader && titles.Any(t => t.Length > 0);
        int Measure(Font f, string text) => text.Length == 0 ? 0 : TextRenderer.MeasureText(g, text, f, Size.Empty, TextFlags).Width;

        // Column widths: as typed, otherwise fitted to the contents (the first column gets what's left).
        const int gap = 8;
        var typed = item.ColumnWidths.Split('|');
        var widths = new int[columns];
        for (int c = 1; c < columns; c++)
        {
            if (c < typed.Length && int.TryParse(typed[c].Trim(), out int w) && w > 0) { widths[c] = w; continue; }
            int widest = header && c < titles.Length ? Measure(bold, titles[c]) : 0;
            for (int r = 0; r < rows; r++) widest = Math.Max(widest, Measure(font, cells[r, c]));
            widths[c] = widest + gap;
        }
        widths[0] = typed.Length > 0 && int.TryParse(typed[0].Trim(), out int first) && first > 0
            ? first : Math.Max(gap, item.Width - widths.Skip(1).Sum());
        int width = widths.Sum();

        int lineHeight = TextRenderer.MeasureText(g, "Ag", font, Size.Empty, TextFlags).Height;
        int rowHeight = lineHeight + item.RowSpacing;
        using var line = item.ShowLines ? new Pen(ParseColor(item.LineColor)) : null;
        int y = item.Y;

        void DrawRow(Func<int, string> text, Font f, Color color)
        {
            int x = item.X;
            for (int c = 0; c < columns; c++)
            {
                string fitted = Fit(g, f, text(c), widths[c] - (c < columns - 1 ? gap / 2 : 0));
                int left = numeric[c] ? x + widths[c] - Measure(f, fitted) - (c < columns - 1 ? gap / 2 : 0) : x;
                DrawText(g, f, fitted, left, y, color);
                x += widths[c];
            }
            y += rowHeight;
        }

        if (header)
        {
            DrawRow(c => c < titles.Length ? titles[c] : "", bold, ParseColor(item.HeaderColor));
            if (line != null) g.DrawLine(line, item.X, y - item.RowSpacing / 2 - 1, item.X + width - 1, y - item.RowSpacing / 2 - 1);
        }
        var color = ParseColor(item.Color);
        for (int r = 0; r < rows; r++)
        {
            int row = r;
            if (line != null && r > 0) g.DrawLine(line, item.X, y - item.RowSpacing / 2 - 1, item.X + width - 1, y - item.RowSpacing / 2 - 1);
            DrawRow(c => cells[row, c], font, color);
        }
        return new Rectangle(item.X, item.Y, width, Math.Max(1, y - item.Y - item.RowSpacing));
    }

    /// <summary>The text, shortened with "…" if it's wider than <paramref name="width"/>.</summary>
    private static string Fit(Graphics g, Font font, string text, int width)
    {
        if (text.Length == 0 || TextRenderer.MeasureText(g, text, font, Size.Empty, TextFlags).Width <= width) return text;
        for (int n = text.Length - 1; n > 0; n--)
        {
            string shorter = text[..n].TrimEnd() + "…";
            if (TextRenderer.MeasureText(g, shorter, font, Size.Empty, TextFlags).Width <= width) return shorter;
        }
        return "";
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>A 1-pixel frame just inside <paramref name="r"/> (also when drawing the larger preview).</summary>
    private static void Frame(Graphics g, Pen pen, Rectangle r)
    {
        if (_scale == 1)
        {
            g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            return;
        }
        pen.Alignment = PenAlignment.Inset;
        g.DrawRectangle(pen, r);
    }

    private static Rectangle DrawText(Graphics g, Font font, string text, int x, int y, Color color)
    {
        if (text.Length == 0) return new Rectangle(x, y, 1, font.Height);
        if (_scale == 1 || _scaledFonts == null)
        {
            TextRenderer.DrawText(g, text, font, new Point(x, y), color, TextFlags);
        }
        else
        {
            // GDI text ignores the drawing scale, so scale the font and position instead. Positions are worked out with
            // the panel-size font, so everything lines up exactly as on the display.
            if (!_scaledFonts.TryGetValue(font, out var scaled))
                _scaledFonts[font] = scaled = new Font(font.FontFamily, font.SizeInPoints * _scale, font.Style, GraphicsUnit.Point);
            TextRenderer.DrawText(g, text, scaled, new Point((int)Math.Round(x * _scale), (int)Math.Round(y * _scale)), color, TextFlags);
        }
        var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFlags);
        return new Rectangle(x, y, size.Width, size.Height);
    }

    /// <summary>A sensor value as text, with the item's decimals or the sensor's usual ones.</summary>
    public static string Format(IRenderSource source, string sensor, int decimals)
    {
        if (string.IsNullOrEmpty(sensor)) return "-";
        var reading = source.Read(sensor);
        if (reading.Text != null) return reading.Text;
        if (reading.Value is not double value) return "-";
        int places = decimals >= 0 ? decimals : source.Info(sensor)?.Decimals ?? 0;
        return value.ToString("F" + places, CultureInfo.CurrentCulture);
    }

    private static readonly Dictionary<string, Color> Colors = [];

    public static Color ParseColor(string text)
    {
        lock (Colors)
        {
            if (Colors.TryGetValue(text, out var c)) return c;
            try { c = ColorTranslator.FromHtml(text); } catch { c = Color.Magenta; }
            Colors[text] = c;
            return c;
        }
    }

    /// <summary>Fonts used during one render, disposed afterwards.</summary>
    private sealed class FontCache : IDisposable
    {
        private readonly Dictionary<(string, double, FontStyle), Font> _fonts = [];

        public Font Get(TextItem item, bool bold = false)
        {
            var style = (item.Bold || bold ? FontStyle.Bold : 0) | (item.Italic ? FontStyle.Italic : 0);
            var key = (item.Font, item.FontSize, style);
            if (!_fonts.TryGetValue(key, out var font))
            {
                try { font = new Font(item.Font, (float)Math.Max(1, item.FontSize), style, GraphicsUnit.Point); }
                catch { font = new Font("Tahoma", (float)Math.Max(1, item.FontSize), style, GraphicsUnit.Point); }
                _fonts[key] = font;
            }
            return font;
        }

        public void Dispose()
        {
            foreach (var font in _fonts.Values) font.Dispose();
        }
    }
}

/// <summary>Pictures loaded from disk, reloaded when the file changes.</summary>
internal static class ImageCache
{
    private static readonly Dictionary<string, (DateTime Stamp, Image? Image)> Images = [];

    public static Image? Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (Images)
        {
            DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (Images.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Image;
            cached.Image?.Dispose();
            Image? image = null;
            try
            {
                if (stamp != DateTime.MinValue)
                {
                    // Load into memory so the file isn't kept locked.
                    using var stream = new MemoryStream(File.ReadAllBytes(path));
                    using var loaded = Image.FromStream(stream);
                    image = new Bitmap(loaded);
                }
            }
            catch { }
            Images[path] = (stamp, image);
            return image;
        }
    }
}
