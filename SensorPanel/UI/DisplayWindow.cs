using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SensorPanel.Engine;

namespace SensorPanel.UI;

/// <summary>
/// The panel as the display shows it (the page on the display, turned like the preview), one screen pixel per display
/// pixel. A display too large for the screen is scaled down to fit, and the title says so.
/// </summary>
public sealed class DisplayWindow : Window
{
    private readonly PanelEngine _engine;
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private System.Drawing.Size _size;
    private int _turn;
    private BitmapSource? _pending;

    public DisplayWindow(PanelEngine engine)
    {
        _engine = engine;
        Title = "SensorPanel display";
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/SensorPanel.ico"));
        ResizeMode = ResizeMode.CanMinimize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.Black;
        Content = _image;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        UseLayoutRounding = true;

        Loaded += (_, _) =>
        {
            Fit();
            engine.MirrorFrameReady += OnFrame;
            engine.MirrorEnabled = true;
        };
        Closed += (_, _) =>
        {
            engine.MirrorEnabled = false;
            engine.MirrorFrameReady -= OnFrame;
        };
        DpiChanged += (_, _) => Fit();
    }

    /// <summary>Sizes the window to the display's resolution in screen pixels (or as large as fits on the screen).</summary>
    private void Fit()
    {
        _size = _engine.LogicalSize;
        _turn = _engine.PreviewTurn;
        _image.LayoutTransform = new RotateTransform(_turn);
        var shown = _turn is 90 or 270 ? new System.Drawing.Size(_size.Height, _size.Width) : _size;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var work = SystemParameters.WorkArea;
        double fit = Math.Min(1, Math.Min((work.Width - 40) * dpi / shown.Width, (work.Height - 80) * dpi / shown.Height));
        _engine.MirrorScale = fit;
        _image.Width = _size.Width * fit / dpi;
        _image.Height = _size.Height * fit / dpi;
        Title = fit < 1
            ? $"SensorPanel display: {shown.Width} × {shown.Height}, shown at {fit:P0} to fit the screen"
            : $"SensorPanel display: {shown.Width} × {shown.Height}";
    }

    private void OnFrame(BitmapSource frame)
    {
        // Only the newest frame matters; skip ones that arrive while the UI is busy.
        if (Interlocked.Exchange(ref _pending, frame) != null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            var latest = Interlocked.Exchange(ref _pending, null);
            if (latest == null) return;
            if (_engine.LogicalSize != _size || _engine.PreviewTurn != _turn) Fit(); // the display connected, or its size or rotation changed
            _image.Source = latest;
        });
    }
}
