using System.Drawing;
using System.Drawing.Imaging;
using PixelFormats = System.Windows.Media.PixelFormats;
using System.Windows.Media.Imaging;
using SensorPanel.Display;
using SensorPanel.Model;
using SensorPanel.Rendering;
using SensorPanel.Sensors;

namespace SensorPanel.Engine;

/// <summary>
/// One rendered panel for the preview: the picture (frozen, usable on any thread; drawn at <see cref="Scale"/> times the
/// panel's size), the panel's size and where each item is (both in panel pixels).
/// </summary>
public sealed record PanelFrame(BitmapSource Image, Size PanelSize, double Scale, IReadOnlyDictionary<Guid, Rectangle> Bounds);

/// <summary>
/// Runs the panel on three threads so nothing slow ever holds up the preview while editing:
/// a sensor thread reads every sensor each refresh; the render thread records graph history, draws the preview straight
/// away on every change and draws the display's frame; the <see cref="DisplayWorker"/> sends frames over USB. Layout edits
/// are handed over as copies via <see cref="SetLayout"/>.
/// </summary>
public sealed class PanelEngine : IRenderSource, IDisposable
{
    private const int MaxHistory = 2000;
    private static readonly Size DefaultPanelSize = new(480, 320);

    // While editing, the display catches up once changes pause, so it doesn't redraw at every mouse movement.
    private const int DisplayCatchUpMs = 300;

    private readonly SensorHub _sensors;
    private readonly DisplayWorker _display = new();
    private readonly Thread _sensorThread;
    private readonly Thread _renderThread;
    private readonly AutoResetEvent _renderWake = new(false);
    private readonly AutoResetEvent _sensorWake = new(false);
    private readonly Dictionary<Guid, List<double>> _history = [];

    private volatile PanelLayout _layout = new();
    private volatile bool _outputEnabled = true;
    private volatile bool _previewEnabled = true;
    private volatile bool _stopping;
    private int _sampled;
    private int _layoutChanged;
    private int _previewScalePercent = 100;
    private long _displayDueAt = long.MaxValue;

    public PanelEngine(SensorHub sensors)
    {
        _sensors = sensors;
        _sensorThread = new Thread(ReadSensors) { IsBackground = true, Name = "SensorPanel sensors" };
        _renderThread = new Thread(Render) { IsBackground = true, Name = "SensorPanel render" };
        _display.StatusChanged += status => StatusChanged?.Invoke(status);
    }

    public void Start()
    {
        _display.Start();
        _sensorThread.Start();
        _renderThread.Start();
    }

    /// <summary>Raised on the render thread after every preview frame.</summary>
    public event Action<PanelFrame>? FrameReady;

    /// <summary>Raised when the display connects, disconnects or fails.</summary>
    public event Action<string>? StatusChanged;

    public string Status => _display.Status;

    public bool IsDisplayConnected => _display.IsConnected;

    /// <summary>The connected display's resolution, if it's connected as display type <paramref name="driverId"/>.</summary>
    public Size? ConnectedSize(string driverId) => _display.ConnectedSize(driverId);

    /// <summary>Uses another display type (built in or from a plugin), or new settings for it.</summary>
    public void SetDisplayDriver(Plugins.IDisplayDriver? driver, IReadOnlyDictionary<string, string> settings)
    {
        _display.SetDriver(driver, settings);
        Volatile.Write(ref _layoutChanged, 1); // the panel's size may change
        _renderWake.Set();
    }

    /// <summary>Whether to publish preview frames (only while the editor window is open).</summary>
    public bool PreviewEnabled
    {
        get => _previewEnabled;
        set
        {
            _previewEnabled = value;
            if (value) _renderWake.Set();
        }
    }

    /// <summary>How much larger than the panel to draw the preview (zoom × screen scaling), for a sharp picture.</summary>
    public double PreviewScale
    {
        get => Volatile.Read(ref _previewScalePercent) / 100.0;
        set
        {
            Volatile.Write(ref _previewScalePercent, (int)Math.Round(Math.Clamp(value, 0.25, 8) * 100));
            _renderWake.Set();
        }
    }

    /// <summary>The panel's size in its rotated orientation.</summary>
    public Size LogicalSize
    {
        get
        {
            var physical = _display.PanelSize ?? DefaultPanelSize;
            return _layout.RotationDegrees is 90 or 270 ? new Size(physical.Height, physical.Width) : physical;
        }
    }

    public void SetLayout(PanelLayout layout)
    {
        SensorMatcher.DimmSlots = PanelImporter.DimmSlots(layout);
        _layout = layout;
        Volatile.Write(ref _layoutChanged, 1);
        _renderWake.Set();
        _sensorWake.Set(); // the refresh interval may have changed
    }

    /// <summary>Turns sending to the display on or off (off also switches its backlight off).</summary>
    public bool OutputEnabled
    {
        get => _outputEnabled;
        set
        {
            _outputEnabled = value;
            _display.Resend();
            Volatile.Write(ref _layoutChanged, 1);
            _renderWake.Set();
        }
    }

    // ---- IRenderSource -------------------------------------------------------------------------------------

    public Reading Read(string sensor) => _sensors.Read(sensor);

    public SensorInfo? Info(string sensor) => _sensors.Info(sensor);

    public IReadOnlyList<double> History(Guid item) => _history.TryGetValue(item, out var list) ? list : [];

    // ---- threads -------------------------------------------------------------------------------------------

    /// <summary>Reads every sensor once per refresh (this can take a moment, so it has its own thread).</summary>
    private void ReadSensors()
    {
        while (!_stopping)
        {
            long started = Environment.TickCount64;
            try { _sensors.Update(); } catch { }
            Volatile.Write(ref _sampled, 1);
            _renderWake.Set();

            int wait = (int)Math.Max(50, Math.Max(250, _layout.RefreshMs) - (Environment.TickCount64 - started));
            _sensorWake.WaitOne(wait);
        }
    }

    private void Render()
    {
        while (!_stopping)
        {
            var layout = _layout;
            long now = Environment.TickCount64;
            try
            {
                bool sampled = Interlocked.Exchange(ref _sampled, 0) == 1;
                if (sampled) Sample(layout);
                if (Interlocked.Exchange(ref _layoutChanged, 0) == 1) _displayDueAt = now + DisplayCatchUpMs;

                // The display follows the sensor refresh, except while editing: then once changes pause.
                bool editing = _displayDueAt != long.MaxValue;
                bool sendDisplay = editing ? now >= _displayDueAt : sampled;
                if (sendDisplay) _displayDueAt = long.MaxValue;

                RenderFrames(layout, sendDisplay);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Error: " + ex.Message);
            }

            long wait = _displayDueAt == long.MaxValue ? 2000 : _displayDueAt - Environment.TickCount64;
            _renderWake.WaitOne((int)Math.Clamp(wait, 1, 2000));
        }
    }

    private void Sample(PanelLayout layout)
    {
        var graphs = layout.Items.OfType<GraphItem>().ToList();
        foreach (var graph in graphs)
        {
            if (!_history.TryGetValue(graph.Id, out var list)) _history[graph.Id] = list = [];
            list.Add(_sensors.Read(graph.Sensor).Value ?? 0);
            if (list.Count > MaxHistory) list.RemoveRange(0, list.Count - MaxHistory);
        }
        foreach (var gone in _history.Keys.Where(id => graphs.All(g => g.Id != id)).ToList())
            _history.Remove(gone);
    }

    private void RenderFrames(PanelLayout layout, bool sendDisplay)
    {
        var size = LogicalSize;
        if (_previewEnabled)
        {
            double scale = PreviewScale;
            var bounds = new Dictionary<Guid, Rectangle>();
            using var preview = PanelRenderer.Render(layout, size.Width, size.Height, this, bounds, scale);
            FrameReady?.Invoke(new PanelFrame(ToBitmapSource(preview), size, scale, bounds));
        }

        if (!sendDisplay || !_display.IsConnected || _display.PanelSize is not { } physical) return;
        int brightness = (int)Math.Round(Math.Clamp(layout.Brightness, 0, 7) * 100 / 7.0); // the panel's 0-7 as a percentage
        if (!_outputEnabled)
        {
            _display.Submit(null, brightness, output: false);
            return;
        }

        using var bitmap = PanelRenderer.Render(layout, size.Width, size.Height, this);
        bitmap.RotateFlip(layout.RotationDegrees switch
        {
            90 => RotateFlipType.Rotate270FlipNone,
            180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate90FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        });
        if (bitmap.Width != physical.Width || bitmap.Height != physical.Height) return;
        _display.Submit(ToBgra(bitmap), brightness, output: true);
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr24, null,
                                             data.Scan0, data.Stride * bitmap.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>The frame as 32-bit BGRA pixels, row by row (the format display drivers receive).</summary>
    private static byte[] ToBgra(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bitmap.Width * 4;
            var output = new byte[rowBytes * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, output, y * rowBytes, rowBytes);
            return output;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _renderWake.Set();
        _sensorWake.Set();
        if (_renderThread.IsAlive) _renderThread.Join(3000);
        if (_sensorThread.IsAlive) _sensorThread.Join(3000);
        _display.Dispose();
    }
}
