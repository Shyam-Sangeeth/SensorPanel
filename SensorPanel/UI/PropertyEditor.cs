using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SensorPanel.Model;
using SensorPanel.Rendering;
using SensorPanel.Sensors;

namespace SensorPanel.UI;

/// <summary>A sensor in the sensor picker.</summary>
public sealed record SensorChoice(string Key, string Name, string Group, string Value, string Keywords = "")
{
    public string Display => Group.Length > 0 ? $"{Group} › {Name}" : Name;
    public bool HasGroup => Group.Length > 0;
    public override string ToString() => Display;

    /// <summary>Every word typed appears in the name, the hardware, the kind of hardware (e.g. "CPU") or the value.</summary>
    public bool Matches(string[] words) =>
        words.All(w => Display.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                       Keywords.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                       Value.Contains(w, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>
/// Builds the properties panel for an item (or the panel itself) from its <see cref="EditAttribute"/>s.
/// <paramref name="beforeChange"/> runs before each change (for undo), <paramref name="afterChange"/> after it.
/// <paramref name="pageCount"/> is the panel's number of pages.
/// </summary>
public sealed class PropertyEditor(SensorHub sensors, IRenderSource values, Func<int> pageCount, Action<string> beforeChange, Action afterChange)
{
    private static List<string>? _fontNames;

    public void Build(Panel host, object target)
    {
        host.Children.Clear();
        _host = host;
        _builds++;
        bool multiPage = pageCount() > 1;
        var props = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Prop: p, Edit: p.GetCustomAttribute<EditAttribute>()))
            .Where(x => x.Edit != null && x.Prop.CanWrite && (multiPage || !x.Edit.MultiPageOnly) &&
                        (target is not IConditionalEdit conditional || conditional.Shows(x.Prop.Name)))
            .ToList();

        foreach (var group in props.GroupBy(x => x.Edit!.Group))
        {
            host.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, host.Children.Count == 0 ? 0 : 14, 0, 6),
            });
            foreach (var (prop, edit) in group)
                host.Children.Add(edit!.Kind == EditKind.Cells && target is TableItem table ? CellsEditor(table) : Row(target, prop, edit));
        }
    }

    private Panel? _host;
    private int _builds;

    /// <summary>
    /// Builds the panel again for <paramref name="target"/> (after a setting others depend on has changed), unless it has
    /// been built for something else meanwhile (another item selected, undo).
    /// </summary>
    private void Rebuild(object target)
    {
        if (_host is not { } host) return;
        int builds = _builds;
        host.Dispatcher.BeginInvoke(() => { if (_builds == builds) Build(host, target); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private FrameworkElement Row(object target, PropertyInfo prop, EditAttribute edit)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var label = new TextBlock
        {
            Text = edit.Label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 0),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        grid.Children.Add(label);

        var editor = Editor(target, prop, edit);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private FrameworkElement Editor(object target, PropertyInfo prop, EditAttribute edit)
    {
        bool changed = false;
        void Set(object? value)
        {
            if (Equals(prop.GetValue(target), value)) return;
            beforeChange(prop.Name + target.GetHashCode());
            prop.SetValue(target, value);
            afterChange();
            changed = true;
            if (edit.Rebuilds && edit.Kind != EditKind.Number) Rebuild(target);
        }

        switch (edit.Kind)
        {
            case EditKind.Bool:
                var box = new CheckBox { IsChecked = (bool)prop.GetValue(target)!, VerticalAlignment = VerticalAlignment.Center };
                box.Checked += (_, _) => Set(true);
                box.Unchecked += (_, _) => Set(false);
                return box;

            case EditKind.Number:
            case EditKind.Decimal:
                var number = NumberBox(prop, target, edit, Set);
                // Rebuild once typing is done, not at every key (the box would lose the focus).
                if (edit.Rebuilds) number.LostKeyboardFocus += (_, _) => { if (changed) Rebuild(target); };
                return number;

            case EditKind.Color:
                return ColorEditor((string)prop.GetValue(target)!, Set);

            case EditKind.Font:
                _fontNames ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().Order().ToList();
                var fonts = new ComboBox { IsEditable = true, ItemsSource = _fontNames, Text = (string)prop.GetValue(target)! };
                fonts.SelectionChanged += (_, _) => { if (fonts.SelectedItem is string f) Set(f); };
                fonts.LostKeyboardFocus += (_, _) => { if (fonts.Text.Length > 0) Set(fonts.Text); };
                return fonts;

            case EditKind.Choice:
                var choice = new ComboBox { ItemsSource = edit.Choices, SelectedItem = prop.GetValue(target) };
                choice.SelectionChanged += (_, _) => { if (choice.SelectedItem is string c) Set(c); };
                return choice;

            case EditKind.Page:
                // "Every page" is page 0, so the list index is the page number.
                var pages = new List<string> { "Every page" };
                for (int i = 1; i <= pageCount(); i++) pages.Add($"Page {i}");
                var page = new ComboBox { ItemsSource = pages, SelectedIndex = Math.Clamp((int)prop.GetValue(target)!, 0, pages.Count - 1) };
                page.SelectionChanged += (_, _) => { if (page.SelectedIndex >= 0) Set(page.SelectedIndex); };
                return page;

            case EditKind.Sensor:
                return SensorPicker((string)prop.GetValue(target)!, Set);

            case EditKind.Image:
                return ImageEditor((string)prop.GetValue(target)!, Set);

            default:
                var text = new TextBox { Text = (string?)prop.GetValue(target) ?? "" };
                text.TextChanged += (_, _) => Set(text.Text);
                return text;
        }
    }

    /// <summary>A number box: type a value, or use the Up/Down keys (Shift for steps of 10).</summary>
    private static TextBox NumberBox(PropertyInfo prop, object target, EditAttribute edit, Action<object?> set)
    {
        bool isInt = prop.PropertyType == typeof(int);
        var box = new TextBox { Text = Convert.ToString(prop.GetValue(target), CultureInfo.CurrentCulture) };
        box.ToolTip = "Up/Down keys change the value (Shift: by 10)";

        bool TryParse(string? s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v) ||
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        void Apply(double v)
        {
            v = Math.Clamp(v, edit.Min, edit.Max);
            // Box as the property's own type (a plain "isInt ? int : double" would always give a double).
            set(isInt ? (object)(int)Math.Round(v) : v);
        }

        box.TextChanged += (_, _) => { if (TryParse(box.Text, out double v)) Apply(v); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down) || !TryParse(box.Text, out double v)) return;
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            v = Math.Clamp(v + (e.Key == Key.Up ? step : -step), edit.Min, edit.Max);
            box.Text = (isInt ? Math.Round(v) : v).ToString(CultureInfo.CurrentCulture);
            box.CaretIndex = box.Text.Length;
            e.Handled = true;
        };
        return box;
    }

    private static FrameworkElement ColorEditor(string value, Action<object?> set)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var swatch = new Button { Width = 28, Height = 28, Padding = new Thickness(0), ToolTip = "Pick a colour" };
        var fill = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
        swatch.Content = fill;
        var text = new TextBox { Text = value };
        Grid.SetColumn(text, 1);
        grid.Children.Add(swatch);
        grid.Children.Add(text);

        void ShowSwatch(string hex)
        {
            try { fill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { fill.Background = Brushes.Transparent; }
        }
        ShowSwatch(value);

        text.TextChanged += (_, _) =>
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text.Text, "^#[0-9A-Fa-f]{6}$"))
            {
                ShowSwatch(text.Text);
                set(text.Text.ToUpperInvariant());
            }
        };
        swatch.Click += (_, _) =>
        {
            var current = PanelRenderer.ParseColor(text.Text);
            using var dialog = new System.Windows.Forms.ColorDialog { Color = current, FullOpen = true, AnyColor = true };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                text.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        };
        return grid;
    }

    /// <summary>
    /// A dropdown button showing the current sensor. It opens a list of all sensors with their live values and a search
    /// box at the top: type any words (e.g. "gpu temp") to filter, Enter or a click picks one, Esc closes.
    /// </summary>
    private FrameworkElement SensorPicker(string current, Action<object?> set)
    {
        var panel = new StackPanel();

        var buttonText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                                      FontSize = 10, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var buttonContent = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        buttonContent.Children.Add(chevron);
        buttonContent.Children.Add(buttonText);
        var button = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = buttonContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        // Popup: search box over the list.
        var search = new TextBox();
        var hint = new TextBlock { Text = "Search sensors", IsHitTestVisible = false, Margin = new Thickness(11, 0, 0, 0),
                                   VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        var searchBox = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        searchBox.Children.Add(search);
        searchBox.Children.Add(hint);

        var list = new ListBox { MaxHeight = 440, ItemTemplate = ChoiceTemplate, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var empty = new TextBlock { Text = "No sensors match.", Margin = new Thickness(8, 6, 8, 6), Visibility = Visibility.Collapsed };
        empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var content = new DockPanel();
        DockPanel.SetDock(searchBox, Dock.Top);
        DockPanel.SetDock(empty, Dock.Top);
        content.Children.Add(searchBox);
        content.Children.Add(empty);
        content.Children.Add(list);
        var card = new Border { Width = 440, Margin = new Thickness(8), Padding = new Thickness(8), CornerRadius = new CornerRadius(8),
                                BorderThickness = new Thickness(1), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = card,
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        var note = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        List<SensorChoice> all = [];
        void ShowCurrent()
        {
            var choice = Choices(current).FirstOrDefault(c => c.Key == current);
            buttonText.Text = choice?.Name ?? "(none)";
            button.ToolTip = choice?.Display;
            note.Text = Note(current);
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Filter()
        {
            string[] words = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var shown = words.Length == 0 ? all : all.Where(c => c.Matches(words)).ToList();
            list.ItemsSource = shown;
            list.SelectedItem = shown.FirstOrDefault(c => c.Key == current) ?? shown.FirstOrDefault();
            if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
            empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Pick(SensorChoice? choice)
        {
            popup.IsOpen = false;
            if (choice == null || choice.Key == current) return;
            current = choice.Key;
            set(choice.Key);
            ShowCurrent();
        }

        button.Checked += (_, _) => popup.IsOpen = true;
        popup.Opened += (_, _) =>
        {
            // Right-align with the button so the wider list opens towards the preview.
            popup.HorizontalOffset = button.ActualWidth - card.Width - card.Margin.Left - card.Margin.Right + 8;
            all = Choices(current);
            search.Text = "";
            Filter();
            button.IsHitTestVisible = false; // so clicking the button again closes rather than reopens
            search.Dispatcher.BeginInvoke(() => Keyboard.Focus(search), System.Windows.Threading.DispatcherPriority.Input);
        };
        popup.Closed += (_, _) =>
        {
            button.IsChecked = false;
            button.Dispatcher.BeginInvoke(() => button.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);
        };
        search.TextChanged += (_, _) => Filter();
        search.PreviewKeyDown += (_, e) =>
        {
            int index = list.SelectedIndex;
            switch (e.Key)
            {
                case Key.Down when index < list.Items.Count - 1:
                    list.SelectedIndex = index + 1;
                    list.ScrollIntoView(list.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Up when index > 0:
                    list.SelectedIndex = index - 1;
                    list.ScrollIntoView(list.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Pick(list.SelectedItem as SensorChoice);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    popup.IsOpen = false;
                    e.Handled = true;
                    break;
            }
        };
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListBoxItem item)
                Pick(item.DataContext as SensorChoice);
        };
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Pick(list.SelectedItem as SensorChoice); };

        ShowCurrent();
        panel.Children.Add(button);
        panel.Children.Add(popup);
        panel.Children.Add(note);
        return panel;
    }

    /// <summary>One line per cell, row by row: a sensor, or text when "(none)" is chosen.</summary>
    private FrameworkElement CellsEditor(TableItem table)
    {
        table.AddMissingCells();
        var panel = new StackPanel();
        for (int r = 0; r < table.ShownRows; r++)
        {
            for (int c = 0; c < table.ShownColumns; c++)
            {
                var cell = table.Cells[r][c];
                string id = $"cell{r},{c}" + table.GetHashCode();
                var text = new TextBox { Text = cell.Text, Margin = new Thickness(0, 4, 0, 0), ToolTip = "Text to show (when no sensor is chosen)" };
                text.Visibility = cell.Sensor.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                text.TextChanged += (_, _) =>
                {
                    if (text.Text == cell.Text) return;
                    beforeChange(id + "text");
                    cell.Text = text.Text;
                    afterChange();
                };
                var content = new StackPanel();
                content.Children.Add(SensorPicker(cell.Sensor, value =>
                {
                    beforeChange(id);
                    cell.Sensor = (string?)value ?? "";
                    afterChange();
                    text.Visibility = cell.Sensor.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                }));
                content.Children.Add(text);

                var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                var label = new TextBlock { Text = $"Row {r + 1}, column {c + 1}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 8, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                Grid.SetColumn(content, 1);
                grid.Children.Add(label);
                grid.Children.Add(content);
                panel.Children.Add(grid);
            }
        }
        return panel;
    }

    private static DataTemplate? _choiceTemplate;

    /// <summary>A sensor row: its name, the hardware it belongs to underneath, and the live value on the right.</summary>
    private static DataTemplate ChoiceTemplate => _choiceTemplate ??= (DataTemplate)System.Windows.Markup.XamlReader.Parse(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid Margin="0,1">
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <StackPanel>
              <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis"/>
              <TextBlock Text="{Binding Group}" FontSize="11" Opacity="0.65" TextTrimming="CharacterEllipsis">
                <TextBlock.Style>
                  <Style TargetType="TextBlock">
                    <Style.Triggers>
                      <DataTrigger Binding="{Binding HasGroup}" Value="False"><Setter Property="Visibility" Value="Collapsed"/></DataTrigger>
                    </Style.Triggers>
                  </Style>
                </TextBlock.Style>
              </TextBlock>
            </StackPanel>
            <TextBlock Grid.Column="1" Text="{Binding Value}" Margin="12,0,0,0" VerticalAlignment="Center" Opacity="0.75"/>
          </Grid>
        </DataTemplate>
        """);

    private List<SensorChoice> Choices(string current)
    {
        var list = new List<SensorChoice> { new("", "(none)", "", "") };
        if (current.StartsWith(SensorMatcher.Prefix, StringComparison.Ordinal))
            list.Add(new SensorChoice(current, SensorMatcher.Describe(current[SensorMatcher.Prefix.Length..]), "Matched automatically",
                                      PanelRenderer.Format(values, current, -1)));
        foreach (var s in sensors.Sensors)
            list.Add(new SensorChoice(s.Key, s.Name, s.Group, PanelRenderer.Format(values, s.Key, -1) + (s.IsText ? "" : s.Unit), s.Keywords));
        if (current.Length > 0 && list.All(c => c.Key != current))
            list.Add(new SensorChoice(current, current + " (not found)", "", ""));
        return list;
    }

    private string Note(string key)
    {
        if (!key.StartsWith(SensorMatcher.Prefix, StringComparison.Ordinal)) return "";
        var info = sensors.Info(key);
        return info != null
            ? $"Matched automatically to {info.Display}."
            : "Matched automatically, but not found on this PC yet" + (SensorHub.Limitation != null ? " (see the note at the top)." : ". Pick a sensor instead.");
    }

    private static FrameworkElement ImageEditor(string value, Action<object?> set)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBox { Text = value, IsReadOnly = true, ToolTip = value };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(browse, 1);
        grid.Children.Add(text);
        grid.Children.Add(browse);
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                Title = "Choose a picture",
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                string copy = PanelStore.ImportImage(dialog.FileName);
                text.Text = copy;
                text.ToolTip = copy;
                set(copy);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't use that picture: " + ex.Message, "SensorPanel");
            }
        };
        return grid;
    }
}
