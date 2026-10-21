using System.Drawing;
using SensorPanel.Plugins;

namespace SensorPanel.Display;

/// <summary>
/// Owns the display on its own thread, through the chosen <see cref="IDisplayDriver"/> (built in or from a plugin):
/// connects (and reconnects), sets the brightness and draws frames. Sending a frame can be slow (about half a second for a
/// full AX206 frame), so frames are handed over with <see cref="Submit"/> and only the newest is drawn; nothing else waits.
/// Only the parts that changed are passed on.
/// </summary>
public sealed class DisplayWorker : IDisposable
{
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private (byte[]? Pixels, int Brightness, bool Output)? _pending;
    private volatile bool _stopping;
    private volatile IDisplay? _display;
    private volatile bool _driverChanged;
    private IDisplayDriver? _driver;
    private IReadOnlyDictionary<string, string> _settings = new Dictionary<string, string>();
    private byte[]? _lastSent;
    private int _sentBrightness = -1;

    public DisplayWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SensorPanel display" };
    }

    public void Start() => _thread.Start();

    /// <summary>The panel's size in the display's orientation: the connected display's, otherwise the driver's default.</summary>
    public Size? PanelSize
    {
        get
        {
            if (_display is { } d) return new Size(d.Width, d.Height);
            lock (_gate)
            {
                try { return _driver?.DefaultSize(_settings); }
                catch { return null; }
            }
        }
    }

    public bool IsConnected => _display != null;

    /// <summary>The connected display's size, if it's connected through the display type <paramref name="driverId"/>.</summary>
    public Size? ConnectedSize(string driverId)
    {
        var display = _display;
        string? active;
        lock (_gate) active = _driver?.Id;
        return display != null && active == driverId ? new Size(display.Width, display.Height) : null;
    }

    public string Status { get; private set; } = "Looking for the display…";

    /// <summary>Raised on the display thread when the connection changes.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Switches to another display type (or the same one with new settings).</summary>
    public void SetDriver(IDisplayDriver? driver, IReadOnlyDictionary<string, string> settings)
    {
        lock (_gate)
        {
            _driver = driver;
            _settings = new Dictionary<string, string>(settings);
        }
        _driverChanged = true;
        _wake.Set();
    }

    /// <summary>
    /// Queues a frame: 32-bit BGRA pixels for the whole panel (null to only change the brightness), the brightness 0-100
    /// and whether the display should show anything. Replaces a frame that hasn't been drawn yet.
    /// </summary>
    public void Submit(byte[]? pixels, int brightness, bool output)
    {
        lock (_gate)
        {
            pixels ??= _pending?.Pixels;
            _pending = (pixels, brightness, output);
        }
        _wake.Set();
    }

    /// <summary>Forget what was drawn, so the next frame is drawn whole.</summary>
    public void Resend()
    {
        lock (_gate) _lastSent = null;
        _wake.Set();
    }

    private void Run()
    {
        long nextConnect = 0;
        while (!_stopping)
        {
            if (_driverChanged)
            {
                _driverChanged = false;
                Disconnect(null);
                nextConnect = 0;
            }
            if (_display == null && Environment.TickCount64 >= nextConnect)
            {
                nextConnect = Environment.TickCount64 + 3000;
                Connect();
            }

            _wake.WaitOne(1000);
            if (_stopping || _display is not { } display) continue;

            (byte[]? Pixels, int Brightness, bool Output)? frame;
            lock (_gate)
            {
                frame = _pending;
                _pending = null;
            }
            if (frame is not { } f) continue;

            try
            {
                int brightness = f.Output ? f.Brightness : 0;
                if (brightness != _sentBrightness)
                {
                    display.SetBrightness(brightness);
                    _sentBrightness = brightness;
                }
                if (!f.Output || f.Pixels == null || f.Pixels.Length != display.Width * display.Height * 4) continue;

                byte[]? previous;
                lock (_gate) previous = _lastSent;
                var changed = ChangedBands(previous, f.Pixels, display.Width, display.Height);
                if (changed.Count > 0) display.Draw(new DisplayFrame(display.Width, display.Height, f.Pixels), changed);
                lock (_gate) _lastSent = f.Pixels;
            }
            catch (Exception ex)
            {
                // Unplugged or taken over: try again shortly.
                Disconnect("Display disconnected" + (string.IsNullOrWhiteSpace(ex.Message) ? "" : $" ({ex.Message})") + ". Reconnecting…");
                nextConnect = Environment.TickCount64 + 2000;
            }
        }
        Disconnect(null);
    }

    private void Connect()
    {
        IDisplayDriver? driver;
        IReadOnlyDictionary<string, string> settings;
        lock (_gate)
        {
            driver = _driver;
            settings = _settings;
        }
        if (driver == null)
        {
            SetStatus("No display: the chosen display type isn't installed (Settings → Display).");
            return;
        }
        try
        {
            var display = driver.Connect(settings);
            if (display == null)
            {
                SetStatus($"Display not found ({driver.Name}). Plug it in, or check its settings.");
                return;
            }
            lock (_gate) _lastSent = null;
            _sentBrightness = -1;
            _display = display;
            SetStatus($"Display connected: {display.Name} ({display.Width} × {display.Height})");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void Disconnect(string? status)
    {
        var display = _display;
        _display = null;
        lock (_gate) _lastSent = null;
        try { display?.Dispose(); } catch { }
        if (status != null) SetStatus(status);
    }

    private void SetStatus(string status)
    {
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// The areas that differ from the last frame drawn, as horizontal bands (rows that changed, close together, with their
    /// own left and right edges). The whole frame when nothing was drawn yet.
    /// </summary>
    private static List<Rectangle> ChangedBands(byte[]? previous, byte[] current, int width, int height)
    {
        if (previous == null || previous.Length != current.Length) return [new Rectangle(0, 0, width, height)];
        var before = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(previous);
        var after = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(current);
        const int joinGap = 8; // rows; nearby changes go in one transfer (each transfer has a small overhead)

        var bands = new List<Rectangle>();
        int top = -1, bottom = -1, left = width, right = -1;
        for (int y = 0; y < height; y++)
        {
            var a = before.Slice(y * width, width);
            var b = after.Slice(y * width, width);
            if (a.SequenceEqual(b)) continue;
            if (top >= 0 && y - bottom > joinGap)
            {
                bands.Add(Rectangle.FromLTRB(left, top, right + 1, bottom + 1));
                top = -1;
                left = width;
                right = -1;
            }
            if (top < 0) top = y;
            bottom = y;
            int first = a.CommonPrefixLength(b);
            int last = width - 1;
            while (last > first && a[last] == b[last]) last--;
            left = Math.Min(left, first);
            right = Math.Max(right, last);
        }
        if (top >= 0) bands.Add(Rectangle.FromLTRB(left, top, right + 1, bottom + 1));
        return bands;
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(3000);
    }
}
