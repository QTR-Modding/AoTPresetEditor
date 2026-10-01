using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AoTPresetEditor;

public partial class MainWindow : Window
{
    private const int DisplayLimit = 1000;
    private const string AllPresetCategories = "All types";
    private const string AllPresetKinds = "All kinds";
    private const int SearchDelayMilliseconds = 180;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(SearchDelayMilliseconds) };
    private IReadOnlyList<FormRow> forms = [];
    private IReadOnlyList<PresetFile> presets = [];
    private CancellationTokenSource? expansion;
    private bool loading;

    public MainWindow(string? dataFolder)
    {
        InitializeComponent();
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); FilterForms(); };
        Closed += (_, _) => { searchTimer.Stop(); expansion?.Cancel(); };
        if (dataFolder is not null)
        {
            DataPath.Text = dataFolder;
            Loaded += async (_, _) => await LoadData();
        }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Select Skyrim's Data folder" };
        if (picker.ShowDialog(this) != true) return;
        DataPath.Text = picker.FolderName;
        await LoadData();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadData();

    private async Task LoadData()
    {
        if (loading) return;
        loading = true;
        ReloadButton.IsEnabled = BrowseButton.IsEnabled = false;
        expansion?.Cancel();
        forms = []; presets = [];
        FormGrid.ItemsSource = null; PresetList.ItemsSource = null; LoadOrderList.ItemsSource = null;
        Original.Clear(); Expanded.Clear(); FormDetail.Clear();
        Types.ItemsSource = Plugins.ItemsSource = null;
        ResultCount.Text = LoadOrderInfo.Text = "";
        var timer = Stopwatch.StartNew();
        try
        {
            var data = DataPath.Text;
            if (!File.Exists(Path.Combine(data, "Skyrim.esm")))
                throw new DirectoryNotFoundException("Select the Data folder containing Skyrim.esm.");
            presets = await Task.Run(() => PresetCatalog.Discover(data));
            UpdatePresetFilters();
            var progress = new Progress<string>(text => Status.Text = text);
            var result = await Task.Run(() => FormCatalog.Load(data, progress));
            forms = result.Forms;
            Types.ItemsSource = new[] { "All types" }.Concat(forms.Select(f => f.Type).Distinct().Order());
            Plugins.ItemsSource = new[] { "All plugins" }.Concat(result.Plugins);
            Types.SelectedIndex = Plugins.SelectedIndex = 0;
            LoadOrderInfo.Text = $"{result.Plugins.Count:N0} active plugins. Order read from: {result.PluginsPath}";
            LoadOrderList.ItemsSource = result.Plugins.Select((name, i) => $"{i + 1,4}  {name}");
            FilterForms();
            Status.Text = $"Loaded {forms.Count:N0} forms, {result.Plugins.Count:N0} plugins, and {presets.Count:N0} presets in {timer.Elapsed.TotalSeconds:F1}s.";
        }
        catch (Exception error)
        {
            Status.Text = "Could not load forms: " + error.Message;
        }
        finally { loading = false; ReloadButton.IsEnabled = BrowseButton.IsEnabled = true; }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        searchTimer.Stop();
        searchTimer.Start();
    }

    private void FilterForms()
    {
        var query = Search.Text.Trim();
        var type = Types.SelectedItem as string;
        var plugin = Plugins.SelectedItem as string;
        var matches = forms.Where(f =>
            (type is null or "All types" || f.Type == type) &&
            (plugin is null or "All plugins" || f.Plugin == plugin || f.Winner == plugin) &&
            (query.Length == 0 || f.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             f.EditorId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             f.Identifier.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             f.Plugin.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             f.Winner.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        FormGrid.ItemsSource = matches.Take(DisplayLimit);
        ResultCount.Text = matches.Length > DisplayLimit
            ? $"{matches.Length:N0} matches. Showing the first {DisplayLimit:N0}; narrow your search to see more."
            : $"{matches.Length:N0} matching forms";
    }

    private void Form_Selected(object sender, SelectionChangedEventArgs e)
    {
        FormDetail.Text = FormGrid.SelectedItem is FormRow row ? row.Identifier : "";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (FormDetail.Text.Length == 0) return;
        try { Clipboard.SetText(FormDetail.Text); }
        catch (ExternalException) { Status.Text = "The clipboard is busy. Try Copy identifier again."; }
    }

    private void PresetFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (PresetList is not null) FilterPresets();
    }

    private void UpdatePresetFilters()
    {
        var selectedCategory = PresetCategories.SelectedItem as string;
        var selectedKind = PresetKinds.SelectedItem;
        var categories = new[] { AllPresetCategories }
            .Concat(presets.Select(p => p.Category).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)).ToArray();
        var kinds = new object[] { AllPresetKinds }.Concat(presets.Select(p => p.Kind).Distinct().Order().Cast<object>()).ToArray();
        PresetCategories.ItemsSource = categories;
        PresetKinds.ItemsSource = kinds;
        PresetCategories.SelectedItem = categories.FirstOrDefault(category =>
            string.Equals(category, selectedCategory, StringComparison.OrdinalIgnoreCase)) ?? AllPresetCategories;
        PresetKinds.SelectedItem = kinds.Contains(selectedKind) ? selectedKind : AllPresetKinds;
        FilterPresets();
    }

    private void FilterPresets()
    {
        var category = PresetCategories.SelectedItem as string;
        var allCategories = category is null or AllPresetCategories;
        var kind = PresetKinds.SelectedItem as PresetKind?;
        PresetList.DisplayMemberPath = allCategories ? nameof(PresetFile.RelativePath)
            : kind is null ? nameof(PresetFile.CategoryPath) : nameof(PresetFile.RulePath);
        PresetList.ItemsSource = presets.Where(p =>
            (allCategories || string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (kind is null || p.Kind == kind) &&
            p.RelativePath.Contains(PresetSearch.Text, StringComparison.OrdinalIgnoreCase));
    }

    private async void Preset_Selected(object sender, SelectionChangedEventArgs e)
    {
        expansion?.Cancel();
        if (PresetList.SelectedItem is not PresetFile preset)
        {
            PresetTitle.Text = "Select a preset";
            PresetStatus.Text = "Original text on the left; expanded templates and YAML merges on the right.";
            Original.Clear(); Expanded.Clear();
            return;
        }
        using var request = new CancellationTokenSource();
        expansion = request;
        PresetTitle.Text = preset.RelativePath;
        Original.Clear(); Expanded.Clear();
        PresetStatus.Text = "Expanding templates and YAML merges...";
        try
        {
            var source = await File.ReadAllTextAsync(preset.FullPath, request.Token);
            request.Token.ThrowIfCancellationRequested();
            Original.Text = source;
            var expanded = await PresetCatalog.Expand(source, request.Token);
            request.Token.ThrowIfCancellationRequested();
            Expanded.Text = expanded;
            PresetStatus.Text = "Template and merge expansion only. Form availability and gameplay conditions are not validated here.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested) PresetStatus.Text = "Cannot expand this preset: " + error.Message;
        }
        finally { if (ReferenceEquals(expansion, request)) expansion = null; }
    }
}
