using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace AoTPresetEditor;

public sealed class FormPickerWindow : Window
{
    private const int DisplayLimit = 1000;
    private const string AllTypes = "All types";
    private const string AllPlugins = "All plugins";
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(180);
    private readonly IReadOnlyList<FormRow> forms;
    private readonly bool multiple;
    private readonly bool browseOnly;
    private readonly string purpose;
    private readonly TextBox search = new();
    private readonly ComboBox types = new();
    private readonly ComboBox plugins = new();
    private readonly TextBlock count = new();
    private readonly TextBlock selection = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DataGrid results = new();
    private readonly Button choose = new() { Content = "Choose", IsDefault = true, Visibility = Visibility.Collapsed };
    private readonly Button clear = new() { Content = "Clear selection", Visibility = Visibility.Collapsed };
    private readonly HashSet<FormRow> selected = [];
    private readonly DispatcherTimer searchTimer = new() { Interval = SearchDelay };
    private bool replacingResults;

    public IReadOnlyList<FormRow> SelectedForms { get; private set; } = [];

    public FormPickerWindow(IReadOnlyList<FormRow> forms, IReadOnlyCollection<string>? allowedTypes = null, bool multiple = false,
        FormSelection? selection = null, bool browseOnly = false, string? purpose = null)
    {
        this.forms = allowedTypes is { Count: > 0 } || selection is not null
            ? forms.Where(form => (allowedTypes is not { Count: > 0 } || allowedTypes.Contains(form.Type, StringComparer.OrdinalIgnoreCase)) &&
                (selection?.Matches(form) ?? true)).ToArray()
            : forms;
        this.multiple = multiple;
        this.browseOnly = browseOnly;
        this.purpose = purpose ?? (browseOnly ? "Form browser" : multiple ? "Choose forms" : "Choose a form");
        Title = this.purpose;
        if (browseOnly) choose.Content = "Copy identifier";
        Width = 1120; Height = 700; MinWidth = 800; MinHeight = 460;
        EditorControls.FitToOwner(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        Content = BuildContent();

        types.ItemsSource = new[] { AllTypes }.Concat(this.forms.Select(form => form.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        plugins.ItemsSource = new[] { AllPlugins }.Concat(this.forms.SelectMany(form => new[] { form.Plugin, form.Winner })
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        types.SelectedIndex = plugins.SelectedIndex = 0;
        search.TextChanged += (_, _) => ScheduleSearch();
        types.SelectionChanged += (_, _) => ScheduleSearch();
        plugins.SelectionChanged += (_, _) => ScheduleSearch();
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); FilterForms(); };
        results.SelectionChanged += Results_SelectionChanged;
        results.MouseDoubleClick += Results_DoubleClick;
        choose.Click += (_, _) => ActivateSelection();
        Closed += (_, _) => searchTimer.Stop();
        Loaded += (_, _) => search.Focus();
        FilterForms();
    }

    private UIElement BuildContent()
    {
        var layout = new Grid { Margin = new Thickness(14) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock { Text = purpose, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
            ToolTip = browseOnly ? "Explore the active profile's forms and copy an identifier."
                : "Forms shown here are applicable to this field in the active profile." };
        layout.Children.Add(header);
        var filters = new Grid();
        var showTypes = forms.Select(form => form.Type).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
        var showPlugins = forms.SelectMany(form => new[] { form.Plugin, form.Winner }).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = showTypes ? new GridLength(1, GridUnitType.Star) : new GridLength(0) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = showPlugins ? new GridLength(1, GridUnitType.Star) : new GridLength(0) });
        AddFilter(filters, "Search name, EditorID, ID, or plugin", search, 0);
        AddFilter(filters, "Type", types, 1, showTypes);
        AddFilter(filters, "Plugin", plugins, 2, showPlugins);
        Grid.SetRow(filters, 1); layout.Children.Add(filters);

        ConfigureGrid();
        results.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(results, 2); layout.Children.Add(results);

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selection.VerticalAlignment = VerticalAlignment.Center;
        selection.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = selection });
        var summary = new StackPanel { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        count.FontSize = 12; count.TextWrapping = TextWrapping.Wrap;
        count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        summary.Children.Add(count); summary.Children.Add(selection); footer.Children.Add(summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        if (multiple)
        {
            clear.Click += (_, _) => { selected.Clear(); results.UnselectAll(); UpdateSelection(); };
            buttons.Children.Add(clear);
        }
        buttons.Children.Add(new Button { Content = browseOnly ? "Close" : "Cancel", IsCancel = true });
        buttons.Children.Add(choose);
        Grid.SetColumn(buttons, 1); footer.Children.Add(buttons);
        Grid.SetRow(footer, 3); layout.Children.Add(footer);
        return layout;
    }

    private static void AddFilter(Grid grid, string label, Control control, int column, bool visible = true)
    {
        var panel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 12, 0, 0, 0), Visibility = visible ? Visibility.Visible : Visibility.Collapsed };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(control);
        Grid.SetColumn(panel, column); grid.Children.Add(panel);
    }

    private void ConfigureGrid()
    {
        results.IsReadOnly = true;
        results.AutoGenerateColumns = false;
        results.CanUserAddRows = false;
        results.CanUserDeleteRows = false;
        results.SelectionMode = multiple ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single;
        results.SelectionUnit = DataGridSelectionUnit.FullRow;
        results.HeadersVisibility = DataGridHeadersVisibility.Column;
        results.EnableRowVirtualization = true;
        results.EnableColumnVirtualization = true;
        results.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        AddColumn("Name", nameof(FormRow.Name), 220);
        AddColumn("EditorID", nameof(FormRow.EditorId), 230);
        AddColumn("Identifier", nameof(FormRow.Identifier), 210);
        AddColumn("Type", nameof(FormRow.Type), 120);
        AddColumn("Defined in", nameof(FormRow.Plugin), 180);
        AddColumn("Winning plugin", nameof(FormRow.Winner), 180);
    }

    private void AddColumn(string title, string property, double weight)
    {
        var text = new Style(typeof(TextBlock));
        text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        text.Setters.Add(new Setter(ToolTipProperty, new Binding(property)));
        results.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property),
            Width = new DataGridLength(weight, DataGridLengthUnitType.Star), MinWidth = 65, ElementStyle = text });
    }

    private void ScheduleSearch()
    {
        searchTimer.Stop(); searchTimer.Start();
    }

    private void FilterForms()
    {
        var query = search.Text.Trim();
        var type = types.SelectedItem as string;
        var plugin = plugins.SelectedItem as string;
        var visible = new List<FormRow>();
        int total = 0;
        foreach (var form in forms)
        {
            if (type is not null and not AllTypes && form.Type != type) continue;
            if (plugin is not null and not AllPlugins && form.Plugin != plugin && form.Winner != plugin) continue;
            if (query.Length != 0 && !Matches(form, query)) continue;
            ++total;
            if (visible.Count < DisplayLimit) visible.Add(form);
        }
        replacingResults = true;
        try
        {
            if (!multiple && selected.Any(form => !visible.Contains(form))) selected.Clear();
            results.ItemsSource = visible;
            if (multiple)
            {
                foreach (var form in visible.Where(selected.Contains)) results.SelectedItems.Add(form);
            }
            else results.SelectedItem = selected.FirstOrDefault();
        }
        finally { replacingResults = false; }
        count.Text = total > DisplayLimit
            ? $"{total:N0} matches. Showing the first {DisplayLimit:N0}; narrow your search to see more."
            : $"{total:N0} matching forms";
        UpdateSelection();
    }

    private static bool Matches(FormRow form, string query) =>
        form.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        form.EditorId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        form.Identifier.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        form.Plugin.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        form.Winner.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (replacingResults) return;
        foreach (FormRow form in args.RemovedItems) selected.Remove(form);
        foreach (FormRow form in args.AddedItems) selected.Add(form);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        choose.IsEnabled = selected.Count > 0;
        choose.Visibility = clear.Visibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        selection.Text = multiple
            ? $"{selected.Count:N0} selected · Ctrl/Shift for multiple · Kept when filtering"
            : selected.FirstOrDefault() is { } form ? $"{(form.Name.Length == 0 ? form.EditorId : form.Name)} · {form.Identifier}" : "Choose a form from the list.";
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (args.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(results, source) is DataGridRow)
            ActivateSelection();
    }

    private void ActivateSelection()
    {
        if (selected.Count == 0) return;
        if (browseOnly)
        {
            try { Clipboard.SetText(selected.First().Identifier); }
            catch (ExternalException) { count.Text = "The clipboard is busy. Try Copy identifier again."; }
            return;
        }
        SelectedForms = forms.Where(selected.Contains).ToArray();
        DialogResult = true;
    }
}
