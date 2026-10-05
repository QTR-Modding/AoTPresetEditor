using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AoTPresetEditor;

public partial class MainWindow : Window
{
    private const string AllPresetCategories = "All types";
    private IReadOnlyList<FormRow> forms = [];
    private IReadOnlyList<PresetFile> presets = [];
    private bool selectingPreset;
    private bool loading;
    private bool wasMaximized;

    public MainWindow(string? dataFolder, string? placementPath = null)
    {
        InitializeComponent();
        if (WindowPlacement.Load(placementPath) is { } placement)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (_, _) => placement.Restore(this);
        }
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) wasMaximized = WindowState == WindowState.Maximized;
        };
        Closing += (_, e) =>
        {
            e.Cancel = !Workspace.CanLeave();
            if (!e.Cancel) SavePlacement(placementPath);
        };
        Workspace.Opened += ShowEditor;
        Workspace.BrowseRequested += ShowPresetBrowser;
        Closed += (_, _) => (HelpTab.Content as WikiView)?.Dispose();
        if (dataFolder is not null)
        {
            DataPath.Text = dataFolder;
            ReloadButton.Visibility = Visibility.Visible;
            Loaded += async (_, _) => await LoadData();
        }
    }

    private void ShowEditor()
    {
        PresetBrowser.Visibility = Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible;
    }

    private void ShowPresetBrowser()
    {
        Workspace.Visibility = Visibility.Collapsed;
        PresetList.SelectedItem = null;
        ResumeEditing.Visibility = Workspace.HasProject ? Visibility.Visible : Visibility.Collapsed;
        PresetBrowser.Visibility = Visibility.Visible;
        PresetSearch.Focus();
    }

    private void Resume_Click(object sender, RoutedEventArgs e) => ShowEditor();
    private void NewPreset_Click(object sender, RoutedEventArgs e) => Workspace.New();
    private void OpenPreset_Click(object sender, RoutedEventArgs e) => Workspace.OpenFile();

    private void MainTab_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, MainTabs) && HelpTab?.IsSelected == true) HelpTab.Content ??= new WikiView();
    }

    private void SavePlacement(string? path)
    {
        try { WindowPlacement.Capture(this, wasMaximized)?.Save(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("Could not remember window placement: " + error.Message); }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Select Skyrim's Data folder" };
        if (picker.ShowDialog(this) != true) return;
        DataPath.Text = picker.FolderName;
        await LoadData();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadData();

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        new AppearanceWindow((App)Application.Current) { Owner = this }.ShowDialog();
        (HelpTab.Content as WikiView)?.ApplyAppearance();
    }

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
        PresetFilters.Visibility = Visibility.Collapsed;
        PresetEmpty.Text = "Loading installed presets...";
        PresetEmpty.Visibility = Visibility.Visible;
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
            var stages = await StageAvailability.LoadAsync(presets);
            Workspace.SetCatalog(forms, groups, stages);
            FormBrowserAction.Visibility = forms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadOrderInfo.Text = $"{result.Plugins.Count:N0} active plugins. Order read from: {result.PluginsPath}";
            LoadOrderList.ItemsSource = result.Plugins.Select((name, i) => $"{i + 1,4}  {name}");
            Status.Text = $"Loaded {forms.Count:N0} forms, {result.Plugins.Count:N0} plugins, and {presets.Count:N0} presets in {timer.Elapsed.TotalSeconds:F1}s.";
        }
        catch (Exception error)
        {
            Status.Text = "Could not load forms: " + error.Message;
            if (presets.Count == 0) PresetEmpty.Text = "Could not load installed presets. Check the Data folder on the Game data tab.";
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
        var selectedKind = (PresetKinds.SelectedItem as KeyValuePair<PresetKind?, string>?)?.Key;
        var categories = new[] { AllPresetCategories }
            .Concat(presets.Select(p => p.Category).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)).ToArray();
        var kinds = new[] { new KeyValuePair<PresetKind?, string>(null, "All behaviors") }.Concat(presets.Select(p => p.Kind).Distinct().Order()
            .Select(kind => new KeyValuePair<PresetKind?, string>(kind, kind switch
            { PresetKind.Default => "Stages for other items", PresetKind.Custom => "Stages for selected items", PresetKind.Addon => "Transformations and time modifiers", _ => "Other" }))).ToArray();
        PresetCategories.ItemsSource = categories;
        PresetKinds.ItemsSource = kinds;
        PresetCategories.SelectedItem = categories.FirstOrDefault(category =>
            string.Equals(category, selectedCategory, StringComparison.OrdinalIgnoreCase)) ?? AllPresetCategories;
        PresetKinds.SelectedItem = kinds.FirstOrDefault(item => item.Key == selectedKind);
        FilterPresets();
    }

    private void FilterPresets()
    {
        var category = PresetCategories.SelectedItem as string;
        var allCategories = category is null or AllPresetCategories;
        var kind = (PresetKinds.SelectedItem as KeyValuePair<PresetKind?, string>?)?.Key;
        var matches = presets.Where(p =>
            (allCategories || string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (kind is null || p.Kind == kind) &&
            p.RelativePath.Contains(PresetSearch.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        PresetList.ItemsSource = matches;
        PresetFilters.Visibility = presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PresetEmpty.Text = presets.Count == 0 ? "No installed presets found in this Data folder." : "No presets match these filters.";
        PresetEmpty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Preset_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (selectingPreset || PresetList.SelectedItem is not PresetFile preset) return;
        selectingPreset = true;
        try
        {
            await Workspace.Open(preset);
        }
        finally { PresetList.SelectedItem = null; selectingPreset = false; }
    }
}
