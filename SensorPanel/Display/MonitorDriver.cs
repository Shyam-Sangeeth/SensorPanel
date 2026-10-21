using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SensorPanel.Plugins;
using Forms = System.Windows.Forms;

namespace SensorPanel.Display;

/// <summary>
/// The built-in display type for screens Windows sees as a monitor (small HDMI or DisplayPort panels, a spare monitor):
/// the panel is shown full screen on the chosen monitor, in a window that never takes focus and stays on top.
/// </summary>
public sealed partial class MonitorDriver : IDisplayDriver
{
    private const string Fit = "Fit (keep the shape)", Stretch = "Stretch to fill";

    public string Id => "monitor";
    public string Name => "HDMI / DisplayPort screen";
    public string Description =>
        "Any screen Windows sees as a monitor, such as small HDMI sensor panels: the panel is shown full screen on it. " +
        "Leave the panel size empty to use the monitor's resolution, or enter the size the panel was designed for and it's scaled to fit.";

    public IReadOnlyList<DisplaySetting> Settings =>
    [
        new("monitor", "Monitor", DefaultMonitor()) { Choices = Monitors().Select(m => m.Label).ToList() },
        new("width", "Panel width (pixels)", "", "Empty: the monitor's width."),
        new("height", "Panel height (pixels)", "", "Empty: the monitor's height."),
        new("scaling", "Scaling", Fit, "How a panel smaller or a different shape than the monitor fills it.") { Choices = [Fit, Stretch] },
    ];

    public System.Drawing.Size DefaultSize(IReadOnlyDictionary<string, string> settings)
    {
        var monitor = Find(settings);
        var native = monitor?.Bounds.Size ?? new System.Drawing.Size(1920, 1080);
        return new System.Drawing.Size(Number(settings, "width") ?? native.Width, Number(settings, "height") ?? native.Height);
    }

    /// <summary>The chosen monitor's resolution, from Windows.</summary>
    public System.Drawing.Size? DetectSize(IReadOnlyDictionary<string, string> settings) => Find(settings)?.Bounds.Size;

    public IDisplay? Connect(IReadOnlyDictionary<string, string> settings)
    {
        var monitor = Find(settings);
        if (monitor == null) return null; // not connected (or switched off)
        var size = DefaultSize(settings);
        bool stretch = settings.GetValueOrDefault("scaling") == Stretch;
        var app = Application.Current ?? throw new InvalidOperationException("SensorPanel isn't ready yet.");
        return app.Dispatcher.Invoke(() => new MonitorOutput(monitor, size.Width, size.Height, stretch));
    }

    // ---- monitors ------------------------------------------------------------------------------------------

    private sealed record Monitor(string DeviceName, string Label, Rectangle Bounds, bool Primary);

    [GeneratedRegex(@"\((DISPLAY\d+)\)\s*$")]
    private static partial Regex DeviceInLabel();

    private static List<Monitor> Monitors() =>
        Forms.Screen.AllScreens.Select(s =>
        {
            string device = s.DeviceName.Replace(@"\\.\", "");
            string label = $"{FriendlyName(s.DeviceName)} — {s.Bounds.Width} × {s.Bounds.Height}{(s.Primary ? ", main" : "")} ({device})";
            return new Monitor(s.DeviceName, label, s.Bounds, s.Primary);
        }).ToList();

    /// <summary>A secondary monitor if there is one (a sensor screen is rarely the main one), otherwise the main one.</summary>
    private static string DefaultMonitor()
    {
        var all = Monitors();
        return (all.FirstOrDefault(m => !m.Primary) ?? all.FirstOrDefault())?.Label ?? "";
    }

    private static Monitor? Find(IReadOnlyDictionary<string, string> settings)
    {
        var all = Monitors();
        string chosen = settings.GetValueOrDefault("monitor") ?? "";
        var match = DeviceInLabel().Match(chosen);
        string device = match.Success ? @"\\.\" + match.Groups[1].Value : chosen;
        return all.FirstOrDefault(m => m.DeviceName.Equals(device, StringComparison.OrdinalIgnoreCase));
    }

    private static int? Number(IReadOnlyDictionary<string, string> settings, string key) =>
        int.TryParse(settings.GetValueOrDefault(key), out int v) && v >= 16 ? Math.Min(v, 8192) : null;

    private static string FriendlyName(string deviceName)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(deviceName, 0, ref device, 0) && device.DeviceString.Trim().Length > 0
            ? device.DeviceString.Trim()
            : deviceName.Replace(@"\\.\", "");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);

    // ---- the full-screen window ----------------------------------------------------------------------------

    /// <summary>A borderless, always-on-top window covering the monitor. Lives on SensorPanel's UI thread.</summary>
    private sealed class MonitorOutput : IDisplay
    {
        private readonly Monitor _monitor;
        private readonly Window _window;
        private readonly System.Windows.Controls.Image _image;
        private readonly WriteableBitmap _bitmap;
        private DisplayFrame? _pending;

        public MonitorOutput(Monitor monitor, int width, int height, bool stretch)
        {
            _monitor = monitor;
            Width = width;
            Height = height;
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _image = new System.Windows.Controls.Image
            {
                Source = _bitmap,
                Stretch = stretch ? System.Windows.Media.Stretch.Fill : System.Windows.Media.Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _window = new Window
            {
                Title = "SensorPanel display",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = System.Windows.Media.Brushes.Black,
                Content = _image,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Width = 1,
                Height = 1,
            };
            _window.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(_window).Handle;
                // Never take focus, and stay out of Alt+Tab.
                SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow);
                Place(hwnd);
            };
            _window.Show();
            Place(new WindowInteropHelper(_window).Handle);
        }

        public string Name => FriendlyName(_monitor.DeviceName);
        public int Width { get; }
        public int Height { get; }

        /// <summary>Covers the monitor exactly, in physical pixels.</summary>
        private void Place(IntPtr hwnd)
        {
            var b = _monitor.Bounds;
            SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, b.X, b.Y, b.Width, b.Height, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOWWINDOW */);
        }

        public void SetBrightness(int percent)
        {
            // Most HDMI screens can't be dimmed from Windows, so dim the picture instead.
            double opacity = Math.Clamp(percent, 0, 100) / 100.0;
            _window.Dispatcher.BeginInvoke(() => _image.Opacity = opacity);
        }

        public void Draw(DisplayFrame frame, IReadOnlyList<Rectangle> changed)
        {
            if (!Forms.Screen.AllScreens.Any(s => s.DeviceName == _monitor.DeviceName))
                throw new InvalidOperationException("the monitor was disconnected");
            // Only the newest frame matters if the UI thread is busy.
            if (Interlocked.Exchange(ref _pending, frame) != null) return;
            _window.Dispatcher.BeginInvoke(() =>
            {
                var latest = Interlocked.Exchange(ref _pending, null);
                if (latest == null || latest.Width != Width || latest.Height != Height) return;
                _bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), latest.Pixels, Width * 4, 0);
            });
        }

        public void Dispose() => _window.Dispatcher.BeginInvoke(() => _window.Close());

        private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
