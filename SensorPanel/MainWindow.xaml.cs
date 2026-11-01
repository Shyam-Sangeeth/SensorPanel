using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SensorPanel.Engine;
using SensorPanel.Model;
using SensorPanel.Rendering;
using SensorPanel.Sensors;
using SensorPanel.Services;
using SensorPanel.UI;

namespace SensorPanel;

/// <summary>A row in the item list.</summary>
public sealed class ItemRow(PanelItem item, bool multiPage) : INotifyPropertyChanged
{
    private string _value = "";

    public PanelItem Item => item;
    public string Kind => item.KindName;
    public string Title => (item.Title.Length > 0 ? item.Title : "(empty)") +
                           (multiPage && item.Page == PanelItem.EveryPage ? " (every page)" : "");
    public double Opacity => item.Visible ? 1 : 0.45;

    public string Value
    {
        get => _value;
        set { if (value != _value) { _value = value; Raise(); } }
    }

    public void Refresh()
    {
        Raise(nameof(Title));
        Raise(nameof(Opacity));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window
{
    private readonly PanelStore _store;
    private readonly SensorHub _sensors;
    private readonly PanelEngine _engine;
    private readonly AppSettings _settings;
    private readonly PropertyEditor _properties;
    private readonly ObservableCollection<ItemRow> _rows = [];
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _valuesTimer;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();

    private PanelLayout _layout;
    private PanelItem? _selected;
    private int _page = 1; // the page being edited
    private PanelFrame? _frame;
    private PanelFrame? _pendingFrame;
    private bool _syncingSelection;
    private string? _lastCheckpoint;
    private long _lastCheckpointTime;

    private bool _dragging;
    private Point _dragStart;
    private (int X, int Y) _dragOrigin;
    private System.Drawing.Rectangle? _dragBounds;

    private readonly IReadOnlyList<Plugins.IDisplayDriver> _drivers;

    public MainWindow(PanelStore store, PanelLayout layout, SensorHub sensors, PanelEngine engine, AppSettings settings, string? note,
                      IReadOnlyList<Plugins.IDisplayDriver> drivers, IReadOnlyList<Display.PluginInfo> plugins)
    {
        _drivers = drivers;
        _store = store;
        _layout = layout;
        _sensors = sensors;
        _engine = engine;
        _settings = settings;
        InitializeComponent();

        _properties = new PropertyEditor(sensors, engine, () => _layout.PageCount, Checkpoint, Commit);

        // Reserve the panel's size so the loading placeholder matches it.
        var panelSize = engine.LogicalSize;
        PreviewImage.Width = panelSize.Width;
        PreviewImage.Height = panelSize.Height;
        FitPreview(panelSize);
        StageTurn.Angle = layout.PreviewTurn;
        ItemList.ItemsSource = _rows;
        RebuildPageBar();
        RebuildRows();
        ShowProperties();

        OutputBox.IsChecked = settings.OutputEnabled;
        TrayBox.IsChecked = settings.ShowTrayIcon;
        StartupBox.IsChecked = SensorHub.IsAdministrator && StartupTask.IsEnabled;
        StartupBox.IsEnabled = SensorHub.IsAdministrator;
        if (!SensorHub.IsAdministrator) StartupBox.ToolTip = "Needs administrator rights (restart SensorPanel as administrator).";
        AutoUpdateBox.IsChecked = settings.CheckForUpdates;
        VersionText.Text = $"SensorPanel {UpdateService.CurrentVersion}";
        RepositoryLink.NavigateUri = new Uri(UpdateService.RepositoryUrl);
        UpdateStatusText.Text = $"You have SensorPanel {UpdateService.CurrentVersion}.";

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => Save();
        _valuesTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _valuesTimer.Tick += (_, _) => RefreshValues();
        _valuesTimer.Start();

        engine.FrameReady += OnFrame;
        engine.DisplayPageChanged += _ => Dispatcher.BeginInvoke(ShowDisplayPage);
        engine.StatusChanged += status => Dispatcher.BeginInvoke(() =>
        {
            ShowDisplayStatus(status);
            UpdateResolution(); // connecting or disconnecting changes what's known
        });
        ShowDisplayStatus(engine.Status);
        if (note != null) ShowStatus(note);
        ShowBanner();
        UpdateButtons();
        ShowDisplaySettings(plugins);

        PreviewKeyDown += Window_PreviewKeyDown;
        IsVisibleChanged += (_, _) =>
        {
            _engine.PreviewEnabled = IsVisible;
            if (!IsVisible) _displayWindow?.Close();
        };
        DpiChanged += (_, _) => UpdatePreviewScale();
        Loaded += (_, _) => UpdatePreviewScale();
        Closed += (_, _) => Save();
    }

    // ---- frames from the engine ----------------------------------------------------------------------------

    private void OnFrame(PanelFrame frame)
    {
        // Only the newest frame matters; skip ones that arrive while the UI is busy.
        if (Interlocked.Exchange(ref _pendingFrame, frame) != null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            var latest = Interlocked.Exchange(ref _pendingFrame, null);
            if (latest == null || !IsVisible) return;
            _frame = latest;
            PreviewLoading.Visibility = Visibility.Collapsed;
            PreviewImage.Source = latest.Image;
            PreviewImage.Width = latest.PanelSize.Width;
            PreviewImage.Height = latest.PanelSize.Height;
            FitPreview(latest.PanelSize);
            UpdateSelectionBox();
        });
    }

    private string _displayStatus = "";
    private DispatcherTimer? _messageTimer;

    /// <summary>The display's connection state: what the status line shows when there's no message.</summary>
    private void ShowDisplayStatus(string status)
    {
        _displayStatus = status;
        if (_messageTimer?.IsEnabled != true) StatusText.Text = DisplayStatusLine();
    }

    /// <summary>The connection state, and with several pages, the page on the display ("… · Page 2 / 3").</summary>
    private string DisplayStatusLine() =>
        _layout.PageCount > 1 && _engine.IsDisplayConnected && _settings.OutputEnabled
            ? $"{_displayStatus} · Page {Math.Min(_engine.DisplayPage, _layout.PageCount)} / {_layout.PageCount}"
            : _displayStatus;

    /// <summary>A message about something that just happened (import, export, an error); the display status comes back after 5 s.</summary>
    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        if (_messageTimer == null)
        {
            _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _messageTimer.Tick += (_, _) =>
            {
                _messageTimer.Stop();
                StatusText.Text = DisplayStatusLine();
            };
        }
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    /// <summary>Shows an unexpected error in the status line (the app keeps running).</summary>
    public void ReportError(string message) => ShowStatus(message);

    private void ShowBanner()
    {
        if (!SensorHub.IsAdministrator)
        {
            BannerText.Text = "SensorPanel isn't running as administrator, so CPU, motherboard, fan and memory temperature sensors are missing.";
            BannerButton.Content = "Restart as administrator";
            BannerButton.Tag = "elevate";
        }
        else if (!SensorHub.IsPawnIoInstalled)
        {
            BannerText.Text = SensorHub.IsPawnIoDriverPresent
                ? "CPU, motherboard, fan and memory temperature sensors need PawnIO. Its driver is on this PC (another program added it), but PawnIO itself isn't installed, so the sensors can't use it."
                : "CPU, motherboard, fan and memory temperature sensors need the free PawnIO driver (the one FanControl and LibreHardwareMonitor use).";
            BannerButton.Content = "Install PawnIO";
            BannerButton.Tag = "pawnio";
        }
        else
        {
            Banner.Visibility = Visibility.Collapsed;
            return;
        }
        Banner.Visibility = Visibility.Visible;
    }

    private async void BannerButton_Click(object sender, RoutedEventArgs e)
    {
        switch (BannerButton.Tag as string)
        {
            case "elevate":
                Save();
                ((App)Application.Current).Restart(elevated: true);
                break;

            case "pawnio":
                BannerButton.IsEnabled = false;
                BannerText.Text = "Installing PawnIO…";
                int code = await Task.Run(() =>
                {
                    try
                    {
                        using var p = Process.Start(new ProcessStartInfo("winget",
                            "install --id namazso.PawnIO -e --accept-package-agreements --accept-source-agreements --disable-interactivity")
                        { CreateNoWindow = true, UseShellExecute = false });
                        p!.WaitForExit();
                        return p.ExitCode;
                    }
                    catch { return -1; }
                });
                BannerButton.IsEnabled = true;
                if (SensorHub.IsPawnIoInstalled)
                {
                    BannerText.Text = "PawnIO is installed. Restart SensorPanel to read all sensors.";
                    BannerButton.Content = "Restart SensorPanel";
                    BannerButton.Tag = "restart";
                }
                else
                {
                    BannerText.Text = $"PawnIO couldn't be installed (winget exit code {code}). You can install it from pawnio.eu.";
                }
                break;

            case "restart":
                Save();
                ((App)Application.Current).Restart(elevated: false);
                break;
        }
    }

    // ---- item list -----------------------------------------------------------------------------------------

    private void RebuildRows()
    {
        _syncingSelection = true;
        _rows.Clear();
        foreach (var item in InPanelOrder()) _rows.Add(new ItemRow(item, _layout.PageCount > 1));
        ItemList.SelectedItem = _rows.FirstOrDefault(r => r.Item == _selected);
        _syncingSelection = false;
        RefreshValues();
    }

    private void RefreshValues()
    {
        if (!IsVisible) return;
        foreach (var row in _rows)
        {
            row.Value = row.Item switch
            {
                ValueItem v => PanelRenderer.Format(_engine, v.Sensor, v.Decimals) + (v.Sensor.Length > 0 && _engine.Read(v.Sensor).HasValue ? v.Unit : ""),
                BarItem b => PanelRenderer.Format(_engine, b.Sensor, b.Decimals) + (_engine.Read(b.Sensor).HasValue ? b.Unit : ""),
                GaugeItem g => PanelRenderer.Format(_engine, g.Sensor, g.Decimals),
                GraphItem g => PanelRenderer.Format(_engine, g.Sensor, -1),
                _ => "",
            };
        }
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        Select((ItemList.SelectedItem as ItemRow)?.Item, fromList: true);
    }

    private void Select(PanelItem? item, bool fromList = false)
    {
        _selected = item;
        if (!fromList)
        {
            _syncingSelection = true;
            var row = _rows.FirstOrDefault(r => r.Item == item);
            ItemList.SelectedItem = row;
            if (row != null) ItemList.ScrollIntoView(row);
            _syncingSelection = false;
        }
        ShowProperties();
        UpdateSelectionBox();
        UpdateButtons();
    }

    private void ShowProperties()
    {
        if (_selected == null)
        {
            PropertiesTitle.Text = "Panel settings (select an item to edit it)";
            _properties.Build(PropertiesHost, _layout);
        }
        else
        {
            PropertiesTitle.Text = $"{_selected.KindName}: {_selected.Title}";
            _properties.Build(PropertiesHost, _selected);
        }
    }

    private void UpdateButtons()
    {
        bool has = _selected != null;
        ItemActions.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        var onPage = _layout.OnPage(_page).ToList();
        int index = has ? onPage.IndexOf(_selected!) : -1;
        ForwardButton.IsEnabled = has && index >= 0 && index < onPage.Count - 1;
        BackwardButton.IsEnabled = has && index > 0;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    // ---- changes, undo and saving --------------------------------------------------------------------------

    /// <summary>Remembers the panel before a change. Repeated changes of the same kind (typing, dragging) undo together.</summary>
    private void Checkpoint(string action)
    {
        long now = Environment.TickCount64;
        if (action != _lastCheckpoint || now - _lastCheckpointTime > 1500)
        {
            _undo.Push(_layout.Serialize());
            _redo.Clear();
        }
        _lastCheckpoint = action;
        _lastCheckpointTime = now;
    }

    /// <summary>
    /// The page's items in the order they appear on the panel: top to bottom, then left to right (not drawing order).
    /// </summary>
    private IEnumerable<PanelItem> InPanelOrder() => _layout.OnPage(_page).OrderBy(i => i.Y).ThenBy(i => i.X);

    /// <summary>
    /// After a change: hand the panel to the engine, refresh the list and save soon. The display shows the page being
    /// edited.
    /// </summary>
    private void Commit()
    {
        // An empty panel is designed for whatever rotation is chosen (so a landscape panel can be started).
        if (_layout.Items.Count == 0 && _layout.DesignRotation != _layout.Rotation)
        {
            _layout.DesignRotation = _layout.Rotation;
            Dispatcher.BeginInvoke(ShowProperties, DispatcherPriority.Input);
        }
        TurnPreview();
        // Follow the selected item when it's moved to another page; keep the page in range after undo.
        if (_selected != null && _selected.Page != PanelItem.EveryPage) _page = _selected.Page;
        _page = Math.Clamp(_page, 1, _layout.PageCount);
        _engine.PreviewPage = _page;
        _engine.SetLayout(_layout.Clone());
        _engine.ShowPage(_page);
        bool pagesAdded = RebuildPageBar();
        if (pagesAdded) ShowProperties(); // the page settings appear or disappear
        // Keep the list in panel order when items move (or appear/disappear).
        if (pagesAdded || !_rows.Select(r => r.Item).SequenceEqual(InPanelOrder())) RebuildRows();
        foreach (var row in _rows) row.Refresh();
        if (_selected != null) PropertiesTitle.Text = $"{_selected.KindName}: {_selected.Title}";
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateButtons();
    }

    /// <summary>
    /// Turns the preview (screen and design together) by the difference between the rotation and the one the design was
    /// made for (none when they're the same).
    /// </summary>
    private void TurnPreview()
    {
        StageTurn.Angle = _layout.PreviewTurn;
        if (!_previewPanel.IsEmpty) FitPreview(_previewPanel);
    }

    private void Restore(string json)
    {
        Guid? selected = _selected?.Id;
        _layout = PanelLayout.Deserialize(json);
        _selected = _layout.Items.FirstOrDefault(i => i.Id == selected);
        _lastCheckpoint = null;
        RebuildRows();
        ShowProperties();
        Commit();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_layout.Serialize());
        Restore(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_layout.Serialize());
        Restore(_redo.Pop());
    }

    private void Save()
    {
        _saveTimer.Stop();
        try { _store.Save(_layout); }
        catch (Exception ex) { ShowStatus("Couldn't save the panel: " + ex.Message); }
    }

    // ---- toolbar -------------------------------------------------------------------------------------------

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import a panel", Filter = PanelFile.ImportFilter };
        if (dialog.ShowDialog(this) != true) return;
        if (_layout.Items.Count > 0 &&
            MessageBox.Show(this, "Replace the current panel with the imported one? (Undo can bring it back.)", "SensorPanel",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            var imported = PanelFile.Import(dialog.FileName, PanelStore.ImageFolder, _layout);
            Checkpoint("import");
            _lastCheckpoint = null;
            _layout = imported;
            _selected = null;
            _page = 1;
            RebuildRows();
            ShowProperties();
            Commit();
            ShowStatus($"Imported {imported.Items.Count} items from {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't import: " + ex.Message, "SensorPanel");
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the panel",
            Filter = PanelFile.ExportFilter,
            DefaultExt = PanelFile.Extension,
            FileName = $"Panel {DateTime.Now:yyyy-MM-dd}{PanelFile.Extension}",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            PanelFile.Export(_layout, dialog.FileName);
            ShowStatus($"Exported the panel to {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't export: " + ex.Message, "SensorPanel");
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        AddButton.ContextMenu.PlacementTarget = AddButton;
        AddButton.ContextMenu.Placement = PlacementMode.Bottom;
        AddButton.ContextMenu.IsOpen = true;
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        PanelItem item = ((MenuItem)sender).Tag switch
        {
            "label" => new LabelItem(),
            "value" => new ValueItem { Sensor = "sys/time", Label = "Time" },
            "bar" => new BarItem { Sensor = "sys/memory-load", Label = "Memory", Unit = "%", UnitWidth = 12, Width = 200, BarWidth = 200 },
            "gauge" => new GaugeItem { Sensor = "sys/memory-load" },
            "graph" => new GraphItem { Sensor = "sys/memory-load" },
            "table" => new TableItem(),
            _ => new ImageItem(),
        };
        item.X = 10;
        item.Y = 10;
        item.Page = _page;
        Checkpoint("add");
        _lastCheckpoint = null;
        _layout.Items.Add(item);
        RebuildRows();
        Select(item);
        Commit();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => Duplicate();

    private void Duplicate()
    {
        if (_selected == null) return;
        var copy = Copy(_selected);
        copy.X += 6;
        copy.Y += 6;
        Checkpoint("duplicate");
        _lastCheckpoint = null;
        _layout.Items.Insert(_layout.Items.IndexOf(_selected) + 1, copy);
        RebuildRows();
        Select(copy);
        Commit();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_selected == null) return;
        Checkpoint("delete");
        _lastCheckpoint = null;
        _layout.Items.Remove(_selected);
        _selected = null;
        RebuildRows();
        Select(null);
        Commit();
    }

    private void Forward_Click(object sender, RoutedEventArgs e) => MoveLayer(+1);
    private void Backward_Click(object sender, RoutedEventArgs e) => MoveLayer(-1);

    /// <summary>Draws the selected item over (or under) the next item on this page.</summary>
    private void MoveLayer(int delta)
    {
        if (_selected == null) return;
        var onPage = _layout.OnPage(_page).ToList();
        int target = onPage.IndexOf(_selected) + delta;
        if (target < 0 || target >= onPage.Count) return;
        Checkpoint("layer");
        _layout.Items.Remove(_selected);
        int other = _layout.Items.IndexOf(onPage[target]);
        _layout.Items.Insert(delta > 0 ? other + 1 : other, _selected);
        RebuildRows();
        Commit();
    }

    /// <summary>A copy of an item with a new ID.</summary>
    private static PanelItem Copy(PanelItem item)
    {
        var copy = PanelLayout.Deserialize(new PanelLayout { Items = [item] }.Serialize()).Items[0];
        copy.Id = Guid.NewGuid();
        return copy;
    }

    // ---- pages ---------------------------------------------------------------------------------------------

    private (int Count, int Page) _pageBar;

    private bool _fillingPages;

    /// <summary>
    /// Fills the page list (shown from two pages on) and the "Page 2 / 3" above the preview with the page being edited.
    /// Returns whether the number of pages changed since last time.
    /// </summary>
    private bool RebuildPageBar()
    {
        int count = _layout.PageCount;
        if (_pageBar == (count, _page)) return false;
        bool countChanged = _pageBar.Count != count && _pageBar.Count != 0;
        _pageBar = (count, _page);

        _fillingPages = true;
        PageBox.ItemsSource = Enumerable.Range(1, count).Select(i => $"Page {i}").ToList();
        PageBox.SelectedIndex = _page - 1;
        _fillingPages = false;
        ShowDisplayPage();

        var multiPage = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageBox.Visibility = multiPage;
        if (!_previewPanel.IsEmpty) FitPreview(_previewPanel); // the page list takes room under the preview
        DeletePageButton.Visibility = multiPage;
        return countChanged;
    }

    /// <summary>Puts the page on the display in the status line (it changes as the pages switch).</summary>
    private void ShowDisplayPage()
    {
        if (_messageTimer?.IsEnabled != true) StatusText.Text = DisplayStatusLine();
    }

    private void PageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingPages && PageBox.SelectedIndex >= 0 && PageBox.SelectedIndex + 1 != _page) GoToPage(PageBox.SelectedIndex + 1);
    }

    /// <summary>Edits another page, and shows it on the display (from there the pages switch as usual).</summary>
    private void GoToPage(int page)
    {
        _page = Math.Clamp(page, 1, _layout.PageCount);
        if (_selected != null && _selected.Page != PanelItem.EveryPage && _selected.Page != _page) _selected = null;
        _engine.PreviewPage = _page;
        _engine.ShowPage(_page);
        RebuildPageBar();
        RebuildRows();
        ShowProperties();
        UpdateSelectionBox();
        UpdateButtons();
    }

    private void AddPage_Click(object sender, RoutedEventArgs e)
    {
        Checkpoint("add page");
        _lastCheckpoint = null;
        _layout.PageCount++;
        _selected = null;
        _page = _layout.PageCount;
        Commit();
        GoToPage(_page);
    }

    private void DuplicatePage_Click(object sender, RoutedEventArgs e)
    {
        Checkpoint("duplicate page");
        _lastCheckpoint = null;
        int source = _page, target = _page + 1;
        foreach (var item in _layout.Items) if (item.Page >= target) item.Page++;
        // Each copy goes right after its original, so the drawing order matches the original page.
        var items = new List<PanelItem>();
        foreach (var item in _layout.Items)
        {
            items.Add(item);
            if (item.Page != source) continue;
            var copy = Copy(item);
            copy.Page = target;
            items.Add(copy);
        }
        _layout.Items = items;
        _layout.PageCount++;
        _selected = null;
        _page = target;
        Commit();
        GoToPage(target);
    }

    private void DeletePage_Click(object sender, RoutedEventArgs e)
    {
        if (_layout.PageCount <= 1) return;
        int page = _page;
        Checkpoint("delete page");
        _lastCheckpoint = null;
        int removed = _layout.Items.RemoveAll(i => i.Page == page);
        foreach (var item in _layout.Items) if (item.Page > page) item.Page--;
        _layout.PageCount--;
        if (_selected != null && !_layout.Items.Contains(_selected)) _selected = null;
        _page = Math.Min(page, _layout.PageCount);
        Commit();
        GoToPage(_page);
        ShowStatus(removed switch
        {
            0 => $"Deleted page {page}.",
            1 => $"Deleted page {page} and its item. Undo brings it back.",
            _ => $"Deleted page {page} and its {removed} items. Undo brings them back.",
        });
    }

    // The preview's longer side: a 480-pixel display at 150%.
    private const double PreviewSize = 720;

    private System.Drawing.Size _previewPanel;

    /// <summary>
    /// Scales the preview so every display, small or large, is shown the same size, or smaller if the card isn't tall
    /// enough (e.g. with the note at the top of the window).
    /// </summary>
    private void FitPreview(System.Drawing.Size panel)
    {
        _previewPanel = panel;
        double scale = Math.Min(PreviewSize / panel.Width, PreviewSize / panel.Height);
        double pageList = PageBox.Visibility == Visibility.Visible ? Math.Max(PageBox.ActualHeight, 32) + 8 : 0;
        double room = StageScroll.ActualHeight - 24 - pageList; // the preview's margins and the page list
        double shownHeight = _layout.PreviewTurn is 90 or 270 ? panel.Width : panel.Height; // when turned sideways
        if (room > 100) scale = Math.Min(scale, room / shownHeight);
        if (StageScale.ScaleX == scale) return;
        StageScale.ScaleX = StageScale.ScaleY = scale;
        SelectionBox.StrokeThickness = 1 / scale; // one screen pixel
        UpdatePreviewScale();
    }

    private void StageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_previewPanel.IsEmpty) FitPreview(_previewPanel);
    }

    /// <summary>The preview is drawn at its shown size times Windows display scaling, so it's sharp.</summary>
    private void UpdatePreviewScale()
    {
        double dpi = PresentationSource.FromVisual(this) != null ? VisualTreeHelper.GetDpi(this).DpiScaleX : 1;
        _engine.PreviewScale = StageScale.ScaleX * dpi;
        RenderOptions.SetBitmapScalingMode(PreviewImage, BitmapScalingMode.HighQuality);
    }

    private DisplayWindow? _displayWindow;

    /// <summary>Opens the display at its own resolution in a window of its own (or brings it back to the front).</summary>
    private void Popout_Click(object sender, RoutedEventArgs e)
    {
        if (_displayWindow != null)
        {
            _displayWindow.WindowState = WindowState.Normal;
            _displayWindow.Activate();
            return;
        }
        _displayWindow = new DisplayWindow(_engine) { Owner = this };
        _displayWindow.Closed += (_, _) => _displayWindow = null;
        _displayWindow.Show();
    }

    private void OutputBox_Changed(object sender, RoutedEventArgs e)
    {
        bool on = OutputBox.IsChecked == true;
        _engine.OutputEnabled = on;
        if (_settings.OutputEnabled == on) return;
        _settings.OutputEnabled = on;
        _store.SaveSettings(_settings);
        ((App)Application.Current).OutputChanged();
    }

    /// <summary>Keeps the checkbox in step when the tray menu turns the display on or off.</summary>
    public void SetOutput(bool on) => OutputBox.IsChecked = on;

    // ---- display type and plugins --------------------------------------------------------------------------

    private bool _fillingDisplay;

    /// <summary>A display type in the list (its name is also what screen readers announce).</summary>
    private sealed record DriverChoice(Plugins.IDisplayDriver Driver)
    {
        public string Name => Driver.Name;
        public override string ToString() => Driver.Name;
    }

    private void ShowDisplaySettings(IReadOnlyList<Display.PluginInfo> plugins)
    {
        _fillingDisplay = true;
        var choices = _drivers.Select(d => new DriverChoice(d)).ToList();
        DisplayTypeBox.ItemsSource = choices;
        DisplayTypeBox.SelectedItem = choices.FirstOrDefault(c => c.Driver.Id == _settings.DisplayType);
        if (DisplayTypeBox.SelectedItem == null)
            DisplayTypeDescription.Text = $"The display type in use (\"{_settings.DisplayType}\") isn't installed: its plugin is missing. Choose another, or put the plugin back and restart SensorPanel.";
        _fillingDisplay = false;
        ShowDriverSettings();

        PluginList.Text = plugins.Count == 0
            ? "No plugins installed."
            : string.Join(Environment.NewLine, plugins.Select(p => p.Error == null
                ? $"{Path.GetFileName(p.File)}: {string.Join(", ", p.DisplayTypes)}"
                : $"{Path.GetFileName(p.File)}: couldn't load ({p.Error})"));
    }

    private void DisplayTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingDisplay || DisplayTypeBox.SelectedItem is not DriverChoice { Driver: var driver }) return;
        _settings.DisplayType = driver.Id;
        _store.SaveSettings(_settings);
        ShowDriverSettings(detect: true);
        ApplyDisplay();
    }

    private readonly Dictionary<string, TextBox> _settingBoxes = [];
    private TextBlock? _resolutionText;

    /// <summary>
    /// Boxes for the chosen display type's settings; changes apply when you leave a box. When a display type was just chosen
    /// (<paramref name="detect"/>), or its size boxes are empty, they're filled with the display's own resolution if it can
    /// be read; otherwise they're left as they are.
    /// </summary>
    private void ShowDriverSettings(bool detect = false)
    {
        DisplaySettingsHost.Children.Clear();
        _settingBoxes.Clear();
        _resolutionText = null;
        if (DisplayTypeBox.SelectedItem is not DriverChoice { Driver: var driver }) return;
        DisplayTypeDescription.Text = driver.Description;
        var values = ((App)Application.Current).DisplaySettingsFor(driver);
        foreach (var setting in driver.Settings)
        {
            var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var label = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = setting.Label, TextWrapping = TextWrapping.Wrap });
            if (setting.Description.Length > 0)
                label.Children.Add(new TextBlock { Text = setting.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                                                   Style = (Style)FindResource("SecondaryText") });
            string current = values.GetValueOrDefault(setting.Key, setting.DefaultValue);
            bool Save(string value)
            {
                if (!SaveDisplaySetting(driver, setting.Key, value)) return false;
                ApplyDisplay();
                return true;
            }

            FrameworkElement box;
            if (setting.Choices is { } options)
            {
                // A saved choice that's no longer offered (e.g. an unplugged monitor) stays visible.
                var list = options.Contains(current) || current.Length == 0 ? options.ToList() : [.. options, current];
                var combo = new ComboBox { ItemsSource = list, SelectedItem = current, VerticalAlignment = VerticalAlignment.Center };
                combo.SelectionChanged += (_, _) =>
                {
                    // A different monitor (say) has its own resolution: fill it in, then reconnect once.
                    if (combo.SelectedItem is not string value || !SaveDisplaySetting(driver, setting.Key, value)) return;
                    FillDetectedSize(driver);
                    ApplyDisplay();
                };
                box = combo;
            }
            else
            {
                var text = new TextBox { Text = current, VerticalAlignment = VerticalAlignment.Center };
                text.LostKeyboardFocus += (_, _) => Save(text.Text);
                _settingBoxes[setting.Key] = text;
                box = text;
            }
            Grid.SetColumn(box, 1);
            row.Children.Add(label);
            row.Children.Add(box);
            DisplaySettingsHost.Children.Add(row);
        }

        // The display's resolution: as connected, or as read from the device; blank when it can't be read.
        var resolutionRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        resolutionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        resolutionRow.ColumnDefinitions.Add(new ColumnDefinition());
        resolutionRow.Children.Add(new TextBlock { Text = "Resolution", Margin = new Thickness(0, 0, 16, 0) });
        _resolutionText = new TextBlock { Style = (Style)FindResource("SecondaryText") };
        Grid.SetColumn(_resolutionText, 1);
        resolutionRow.Children.Add(_resolutionText);
        DisplaySettingsHost.Children.Add(resolutionRow);

        // Fill in the display's own resolution when it's just been chosen, or when no size has been entered yet.
        bool sizeEmpty = _settingBoxes.TryGetValue("width", out var w) && _settingBoxes.TryGetValue("height", out var h) &&
                         w.Text.Trim().Length == 0 && h.Text.Trim().Length == 0;
        if (detect || sizeEmpty) FillDetectedSize(driver);
        UpdateResolution();
    }

    private bool SaveDisplaySetting(Plugins.IDisplayDriver driver, string key, string value)
    {
        if (!_settings.DisplaySettings.TryGetValue(driver.Id, out var saved)) _settings.DisplaySettings[driver.Id] = saved = [];
        if (saved.GetValueOrDefault(key) == value) return false;
        saved[key] = value;
        _store.SaveSettings(_settings);
        return true;
    }

    /// <summary>Fills the width and height boxes with the display's own resolution, when the display type can read it.</summary>
    private void FillDetectedSize(Plugins.IDisplayDriver driver)
    {
        System.Drawing.Size? size;
        try { size = driver.DetectSize(((App)Application.Current).DisplaySettingsFor(driver)); }
        catch { size = null; }
        if (size is not { } s || !_settingBoxes.TryGetValue("width", out var width) || !_settingBoxes.TryGetValue("height", out var height))
            return; // can't be read: leave the boxes as they are
        width.Text = s.Width.ToString();
        height.Text = s.Height.ToString();
        SaveDisplaySetting(driver, "width", width.Text);
        SaveDisplaySetting(driver, "height", height.Text);
        UpdateResolution();
    }

    /// <summary>Shows the chosen display's resolution, or nothing if it can't be read.</summary>
    private void UpdateResolution()
    {
        if (_resolutionText == null || DisplayTypeBox.SelectedItem is not DriverChoice { Driver: var driver }) return;
        // As read from the device (a monitor's own resolution, even when the panel is scaled onto it), otherwise the
        // connected display's (AX206 reports it when connecting).
        System.Drawing.Size? size;
        try { size = driver.DetectSize(((App)Application.Current).DisplaySettingsFor(driver)); }
        catch { size = null; }
        size ??= _engine.ConnectedSize(driver.Id);
        _resolutionText.Text = size is { } s ? $"{s.Width} × {s.Height}" : "";
    }

    private void ApplyDisplay()
    {
        if (DisplayTypeBox.SelectedItem is not DriverChoice { Driver: var driver }) return;
        _engine.SetDisplayDriver(driver, ((App)Application.Current).DisplaySettingsFor(driver));
    }

    private void OpenPlugins_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Display.PluginLoader.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Display.PluginLoader.Folder}\"") { UseShellExecute = true });
    }

    private void TrayBox_Changed(object sender, RoutedEventArgs e)
    {
        bool show = TrayBox.IsChecked == true;
        ((App)Application.Current).SetTrayVisible(show);
        if (_settings.ShowTrayIcon == show) return;
        _settings.ShowTrayIcon = show;
        _store.SaveSettings(_settings);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitFromSettings();

    private void StartupBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        try
        {
            if (StartupBox.IsChecked == true) StartupTask.Enable();
            else StartupTask.Disable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't change Start with Windows: " + ex.Message, "SensorPanel");
        }
    }

    // ---- about and updates ---------------------------------------------------------------------------------

    private UpdateInfo? _update;
    private bool _updateBusy;

    /// <summary>Raised when a check finds a newer release (the app shows a tray notification while the window is closed).</summary>
    public event Action<UpdateInfo>? UpdateFound;

    public void ShowAbout() => Tabs.SelectedItem = AboutTab;

    /// <summary>Checks GitHub for a newer release. <paramref name="quiet"/> keeps error messages short (automatic checks).</summary>
    public async Task CheckForUpdatesAsync(bool quiet)
    {
        if (_updateBusy) return;
        SetUpdateBusy(true);
        UpdateStatusText.Text = "Checking for updates…";
        try
        {
            _update = await UpdateService.CheckAsync();
            UpdateStatusText.Text = _update == null
                ? $"You're up to date (SensorPanel {UpdateService.CurrentVersion})."
                : $"SensorPanel {_update.Version} is available (you have {UpdateService.CurrentVersion}).";
            if (_update != null) UpdateFound?.Invoke(_update);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = quiet ? "Couldn't check for updates." : $"Couldn't check for updates: {ex.Message}";
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private void SetUpdateBusy(bool busy, bool downloading = false)
    {
        _updateBusy = busy;
        CheckUpdatesButton.IsEnabled = !busy;
        InstallUpdateButton.IsEnabled = !busy;
        var available = _update != null ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Visibility = available;
        WhatsNew.Visibility = available;
        UpdateDot.Visibility = available;
        if (_update != null)
        {
            InstallUpdateButton.Content = $"Download and install {_update.Version}";
            WhatsNewLink.NavigateUri = new Uri(_update.PageUrl);
        }
        UpdateProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(quiet: false);

    /// <summary>Downloads and verifies the installer, starts it, then exits so it can replace SensorPanel's files.</summary>
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null || _updateBusy) return;
        SetUpdateBusy(true, downloading: true);
        UpdateProgress.Value = 0;
        UpdateStatusText.Text = $"Downloading SensorPanel {_update.Version}…";
        try
        {
            string installer = await UpdateService.DownloadAsync(_update, new Progress<double>(p => UpdateProgress.Value = p * 100));
            UpdateStatusText.Text = "Installing… SensorPanel will restart when it's done.";
            UpdateService.RunInstaller(installer);
            await Task.Delay(1500);
            ((App)Application.Current).ExitFromSettings();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update failed: {ex.Message}";
            SetUpdateBusy(false);
        }
    }

    private void AutoUpdateBox_Changed(object sender, RoutedEventArgs e)
    {
        bool on = AutoUpdateBox.IsChecked == true;
        if (_settings.CheckForUpdates == on) return;
        _settings.CheckForUpdates = on;
        _store.SaveSettings(_settings);
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { ShowStatus($"Couldn't open the link: {ex.Message}"); }
        e.Handled = true;
    }

    // ---- preview: select, drag, nudge ----------------------------------------------------------------------

    private PanelItem? HitTest(Point p)
    {
        if (_frame == null) return null;
        // Top-most first. Prefer the selected item when it's under the pointer, so it can be dragged off things above it.
        if (_selected != null && _frame.Bounds.TryGetValue(_selected.Id, out var sel) && Contains(sel, p)) return _selected;
        var onPage = _layout.OnPage(_page).ToList();
        for (int i = onPage.Count - 1; i >= 0; i--)
        {
            var item = onPage[i];
            if (item.Visible && _frame.Bounds.TryGetValue(item.Id, out var r) && Contains(r, p)) return item;
        }
        return null;
    }

    private static bool Contains(System.Drawing.Rectangle r, Point p) =>
        p.X >= r.Left - 2 && p.X <= r.Right + 2 && p.Y >= r.Top - 2 && p.Y <= r.Bottom + 2;

    /// <summary>Keeps the preview within the room the item list and properties leave at their minimum widths.</summary>
    private void EditorGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = EditorGrid.ColumnDefinitions;
        double room = e.NewSize.Width - columns[0].MinWidth - columns[2].MinWidth - 20; // 10 px gap after each of the first two cards
        PreviewCard.MaxWidth = Math.Max(200, room);
    }

    /// <summary>Clicking the preview area around the panel clears the selection.</summary>
    private void PreviewArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // The panel handles its own clicks; the pop-out button and the page list keep the selection.
        if (e.OriginalSource is DependencyObject source && (IsInside(source, Overlay) || IsInside(source, PopoutButton) || IsInside(source, PageBox))) return;
        if (_selected != null) Select(null);
    }

    /// <summary>Clicking an empty part of the item list clears the selection.</summary>
    private void ItemList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ItemList, source) == null &&
            !IsInside(source, typeof(System.Windows.Controls.Primitives.ScrollBar)) && _selected != null)
            Select(null);
    }

    // Text elements (Run etc.) aren't visuals; follow their logical parent instead.
    private static DependencyObject? ParentOf(DependencyObject e) =>
        e is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(e) : LogicalTreeHelper.GetParent(e);

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var e = element; e != null; e = ParentOf(e))
            if (e == ancestor) return true;
        return false;
    }

    private static bool IsInside(DependencyObject element, Type type)
    {
        for (var e = element; e != null; e = ParentOf(e))
            if (type.IsInstanceOfType(e)) return true;
        return false;
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Overlay.Focus();
        var p = e.GetPosition(Overlay);
        var hit = HitTest(p);
        if (hit != _selected) Select(hit);
        if (hit == null) return;
        _dragging = true;
        _dragStart = p;
        _dragOrigin = (hit.X, hit.Y);
        _dragBounds = _frame != null && _frame.Bounds.TryGetValue(hit.Id, out var b) ? b : null;
        Overlay.CaptureMouse();
        e.Handled = true;
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        CursorText.Text = $"{(int)p.X}, {(int)p.Y}";
        if (!_dragging || _selected == null) return;
        int x = _dragOrigin.X + (int)Math.Round(p.X - _dragStart.X);
        int y = _dragOrigin.Y + (int)Math.Round(p.Y - _dragStart.Y);
        if (x == _selected.X && y == _selected.Y) return;
        Checkpoint("move" + _selected.Id);
        _selected.X = x;
        _selected.Y = y;
        _engine.SetLayout(_layout.Clone());
        // Move the outline with the mouse right away; the preview follows within a frame.
        if (_dragBounds is { } start) PlaceSelectionBox(start with { X = start.X + x - _dragOrigin.X, Y = start.Y + y - _dragOrigin.Y });
    }

    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Overlay.ReleaseMouseCapture();
        if (_selected != null && (_selected.X, _selected.Y) != _dragOrigin)
        {
            Commit();
            ShowProperties();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Tabs.SelectedIndex != 0) return; // editor shortcuts only on the Editor tab
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool typing = Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem;

        if (ctrl && e.Key == Key.Z && !typing) { Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y && !typing) { Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D && !typing) { Duplicate(); e.Handled = true; return; }
        if (typing || _selected == null) return;

        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; return; }
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        (int dx, int dy) = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0) return;
        // Move the way the arrow points on the preview, also when it's shown turned.
        (dx, dy) = _layout.PreviewTurn switch
        {
            90 => (dy, -dx),
            180 => (-dx, -dy),
            270 => (-dy, dx),
            _ => (dx, dy),
        };
        Checkpoint("nudge" + _selected.Id);
        _selected.X += dx;
        _selected.Y += dy;
        Commit();
        ShowProperties();
        e.Handled = true;
    }

    private void UpdateSelectionBox()
    {
        if (_dragging) return; // the drag places it
        if (_selected == null || _frame == null || !_frame.Bounds.TryGetValue(_selected.Id, out var r))
        {
            SelectionBox.Visibility = Visibility.Collapsed;
            return;
        }
        PlaceSelectionBox(r);
    }

    private void PlaceSelectionBox(System.Drawing.Rectangle r)
    {
        Canvas.SetLeft(SelectionBox, r.X - 1);
        Canvas.SetTop(SelectionBox, r.Y - 1);
        SelectionBox.Width = r.Width + 2;
        SelectionBox.Height = r.Height + 2;
        SelectionBox.Visibility = Visibility.Visible;
    }
}
