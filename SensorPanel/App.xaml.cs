using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using SensorPanel.Engine;
using SensorPanel.Model;
using SensorPanel.Sensors;
using SensorPanel.Services;
using Forms = System.Windows.Forms;

namespace SensorPanel;

public partial class App : Application
{
    private const string InstanceName = "SensorPanel.SingleInstance";
    private const string ShowEventName = "SensorPanel.ShowWindow";
    private const string ExitEventName = "SensorPanel.Exit";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private PanelStore? _store;
    private AppSettings? _settings;
    private SensorHub? _sensors;
    private PanelEngine? _engine;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _outputItem;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unexpected error in the editor shouldn't close the app (and stop the display): log it and carry on.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            _window?.ReportError("Something went wrong (details in %APPDATA%\\SensorPanel\\errors.log): " + args.Exception.Message);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception ex) LogError(ex); };

        // WPF's GPU path draws an empty window on this PC; the software renderer is plenty for this window.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // Used by the installer before an upgrade or uninstall: close a running copy and wait for it to be gone.
        if (e.Args.Contains("--exit"))
        {
            try { EventWaitHandle.OpenExisting(ExitEventName).Set(); } catch { }
            var others = Process.GetProcessesByName("SensorPanel").Where(p => p.Id != Environment.ProcessId).ToList();
            foreach (var other in others)
            {
                try { other.WaitForExit(10_000); } catch { }
            }
            Shutdown();
            return;
        }

        // Already running? Just show its window. This works without administrator rights, so there's no prompt; a restart
        // (which waits for the old copy to exit) skips it.
        if (!e.Args.Contains("--restarted") && EventWaitHandle.TryOpenExisting(ShowEventName, out var running))
        {
            running.Set();
            running.Dispose();
            Shutdown();
            return;
        }

        // Hardware sensors need administrator rights. Ask once at start; if declined, run with the sensors that work anyway.
        if (!SensorHub.IsAdministrator && !e.Args.Contains("--no-elevate") && Relaunch(e.Args, elevated: true))
        {
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(true, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            // The other copy may be restarting this one; wait briefly for it to let go.
            try { isFirst = _instanceMutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { isFirst = true; }
            if (!isFirst)
            {
                Shutdown();
                return;
            }
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() => { while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowWindow); }) { IsBackground = true }.Start();
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        new Thread(() => { exitEvent.WaitOne(); Dispatcher.BeginInvoke(ExitApp); }) { IsBackground = true }.Start();

        _store = new PanelStore();
        _settings = _store.LoadSettings();
        var layout = _store.Load(out string? note);

        // Display types: HDMI screens are built in; AX206 and others come as plugins (bundled ones first, then the user's).
        var (pluginDrivers, plugins) = Display.PluginLoader.Load(Display.PluginLoader.Folder);
        var drivers = new List<Plugins.IDisplayDriver>(pluginDrivers);
        drivers.Insert(Math.Min(1, drivers.Count), new Display.MonitorDriver());

        _sensors = new SensorHub { ReportPath = Path.Combine(PanelStore.Folder, "sensors.txt") };
        _engine = new PanelEngine(_sensors) { OutputEnabled = _settings.OutputEnabled, PreviewEnabled = false };
        // If the saved display type's plugin is missing, don't fall back to another display (it might take over a monitor).
        var driver = drivers.FirstOrDefault(d => d.Id == _settings.DisplayType);
        _engine.SetDisplayDriver(driver, driver != null ? DisplaySettingsFor(driver) : new Dictionary<string, string>());
        _engine.SetLayout(layout.Clone());
        _engine.Start();

        // The installer's "Start SensorPanel when Windows starts" choice. The startup task belongs to the signed-in user
        // and needs administrator rights, so it's set up here, on the first (elevated) start after installing.
        if (SensorHub.IsAdministrator)
        {
            try
            {
                if (e.Args.Contains("--enable-startup")) StartupTask.Enable();
                else if (e.Args.Contains("--disable-startup") && StartupTask.IsEnabled) StartupTask.Disable();
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        _window = new MainWindow(_store, layout, _sensors, _engine, _settings, note, drivers, plugins);
        _window.Closing += (_, args) =>
        {
            // Closing the window keeps the display running from the tray.
            if (_exiting) return;
            args.Cancel = true;
            _window.Hide();
        };

        CreateTray();
        if (!e.Args.Contains("--minimized")) ShowWindow();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open SensorPanel", null, (_, _) => ShowWindow());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        _outputItem = new Forms.ToolStripMenuItem("Show on display", null, (_, _) => _window?.SetOutput(!_engine!.OutputEnabled))
        {
            Checked = _settings!.OutputEnabled,
        };
        menu.Items.Add(open);
        menu.Items.Add(_outputItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/SensorPanel.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            Text = "SensorPanel",
            ContextMenuStrip = menu,
            Visible = _settings.ShowTrayIcon,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };
    }

    /// <summary>Shows or hides the tray icon (Settings). Without it, the window comes back by starting SensorPanel again.</summary>
    public void SetTrayVisible(bool visible)
    {
        if (_tray != null) _tray.Visible = visible;
    }

    /// <summary>Closes SensorPanel (the Exit button in Settings, for when the tray icon is hidden).</summary>
    public void ExitFromSettings() => ExitApp();

    /// <summary>The window's "Show on display" changed: keep the tray menu in step.</summary>
    public void OutputChanged()
    {
        if (_outputItem != null && _engine != null) _outputItem.Checked = _engine.OutputEnabled;
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Starts a new copy (optionally as administrator) and exits this one.</summary>
    public void Restart(bool elevated)
    {
        _instanceMutex?.ReleaseMutex();
        _instanceMutex = null;
        if (Relaunch(_window?.IsVisible == true ? ["--restarted"] : ["--minimized", "--restarted"], elevated)) ExitApp();
        else
        {
            // Declined: carry on as before.
            _instanceMutex = new Mutex(true, InstanceName, out _);
        }
    }

    private static bool Relaunch(string[] args, bool elevated)
    {
        try
        {
            // Keep "--restarted" when elevating a restart, so the new copy still waits for this one.
            var info = new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', args.Where(a => a != "--no-elevate")))
            {
                UseShellExecute = true,
            };
            if (elevated) info.Verb = "runas";
            Process.Start(info);
            return true;
        }
        catch (Win32Exception)
        {
            return false; // the UAC prompt was declined
        }
    }

    /// <summary>A display type's saved settings, with defaults for any not set yet.</summary>
    public IReadOnlyDictionary<string, string> DisplaySettingsFor(Plugins.IDisplayDriver driver)
    {
        var saved = _settings!.DisplaySettings.GetValueOrDefault(driver.Id) ?? [];
        var values = new Dictionary<string, string>();
        foreach (var setting in driver.Settings) values[setting.Key] = saved.GetValueOrDefault(setting.Key) ?? setting.DefaultValue;
        return values;
    }

    private static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(PanelStore.Folder);
            File.AppendAllText(Path.Combine(PanelStore.Folder, "errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _engine?.Dispose();
        _sensors?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _window?.Close();
        Shutdown();
    }
}
