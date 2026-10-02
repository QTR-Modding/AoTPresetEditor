using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AoTPresetEditor;

public partial class MainWindow : Window
{
    private const string AllPresetCategories = "All types";
    private const string AllPresetKinds = "All kinds";
    private IReadOnlyList<FormRow> forms = [];
    private IReadOnlyList<PresetFile> presets = [];
    private PresetFile? selectedPreset;
    private bool selectingPreset;
    private bool loading;

    public MainWindow(string? dataFolder)
    {
        InitializeComponent();
        Closing += (_, e) => e.Cancel = !Workspace.CanLeave();
        Workspace.Opened += () => { PresetList.SelectedItem = null; selectedPreset = null; PresetBrowser.Visibility = Visibility.Collapsed; };
        Workspace.BrowseRequested += () =>
        {
            PresetBrowser.Visibility = PresetBrowser.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            PresetBrowser.IsExpanded = true;
        };
        if (dataFolder is not null)
        {
            DataPath.Text = dataFolder;
            ReloadButton.Visibility = Visibility.Visible;
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

    private void Appearance_Click(object sender, RoutedEventArgs e) => new AppearanceWindow((App)Application.Current) { Owner = this }.ShowDialog();

    private void FormBrowser_Click(object sender, RoutedEventArgs e) => new FormPickerWindow(forms, browseOnly: true) { Owner = this }.ShowDialog();

    private async Task LoadData()
    {
        if (loading) return;
        loading = true;
        ReloadButton.Visibility = BrowseButton.Visibility = Visibility.Collapsed;
        DataLoading.Visibility = Visibility.Visible;

        forms = []; presets = [];
        Workspace.SetCatalog([], []);
        PresetList.ItemsSource = null; LoadOrderList.ItemsSource = null;
        FormBrowserAction.Visibility = Visibility.Collapsed;
        LoadOrderInfo.Text = "";
        var timer = Stopwatch.StartNew();
        try
        {
            var data = DataPath.Text;
            if (!File.Exists(Path.Combine(data, "Skyrim.esm")))
                throw new DirectoryNotFoundException("Select the Data folder containing Skyrim.esm.");
            presets = await Task.Run(() => PresetCatalog.Discover(data));
            var groups = await Task.Run(() => FormGroupCatalog.Load(data));
            UpdatePresetFilters();
            var progress = new Progress<string>(text => Status.Text = text);
            var result = await Task.Run(() => FormCatalog.Load(data, progress));
            forms = result.Forms;
            Workspace.SetCatalog(forms, groups);
            FormBrowserAction.Visibility = forms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadOrderInfo.Text = $"{result.Plugins.Count:N0} active plugins. Order read from: {result.PluginsPath}";
            LoadOrderList.ItemsSource = result.Plugins.Select((name, i) => $"{i + 1,4}  {name}");
            Status.Text = $"Loaded {forms.Count:N0} forms, {result.Plugins.Count:N0} plugins, and {presets.Count:N0} presets in {timer.Elapsed.TotalSeconds:F1}s.";
        }
        catch (Exception error)
        {
            Status.Text = "Could not load forms: " + error.Message;
        }
        finally
        {
            loading = false;
            DataLoading.Visibility = Visibility.Collapsed;
            BrowseButton.Visibility = Visibility.Visible;
            ReloadButton.Visibility = string.IsNullOrWhiteSpace(DataPath.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
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
        if (selectingPreset || PresetList.SelectedItem is not PresetFile preset) return;
        selectingPreset = true;
        try
        {
            if (await Workspace.Open(preset)) { selectedPreset = preset; PresetList.SelectedItem = preset; }
            else PresetList.SelectedItem = selectedPreset;
        }
        finally { selectingPreset = false; }
    }
}
