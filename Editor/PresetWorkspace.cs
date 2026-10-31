using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AoTPresetEditor;

public sealed class PresetWorkspace : UserControl
{
    public event Action? Opened;
    public event Action? BrowseRequested;
    public bool HasProject => project is not null;
    private static readonly string[] Categories = ["FOOD", "INGR", "MEDC", "POSN", "ARMO", "WEAP", "BOOK", "SCRL", "SLGM", "MISC", "STAT", "MSTT"];
    private static readonly string[] CategoryNames = ["Food", "Ingredients", "Medicine", "Poisons", "Armor", "Weapons", "Books", "Scrolls", "Soul gems", "Miscellaneous items", "Statics", "Movable statics"];
    private readonly TextBlock title = EditorControls.Text("Open or create a preset", true);
    private readonly TextBlock state = EditorControls.Text("Choose a file from the list, or create a new preset.");
    private readonly TextBlock location = EditorControls.Text("");
    private readonly ListBox blocks = new() { BorderThickness = new Thickness(0), Margin = new Thickness(0, 4, 0, 4) };
    private readonly Button addBehavior;
    private readonly StackPanel blockActions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
    private readonly DockPanel blockSidebar = new() { Name = "BlockSidebar" };
    private readonly ColumnDefinition sidebarColumn = new() { Width = new GridLength(240), MinWidth = 180, MaxWidth = 400 };
    private readonly ColumnDefinition splitterColumn = new() { Width = new GridLength(8) };
    private readonly GridSplitter splitter = new() { Width = 4, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button focus;
    private readonly Button reviewIssues;
    private GridLength sidebarWidth = new(240);
    private bool focused;
    private readonly Button save;
    private readonly Button yaml;
    private readonly Button delete;
    private readonly Button up;
    private readonly Button down;
    private readonly MenuItem fileDraft = new() { Header = "Save draft..." };
    private readonly MenuItem fileSaveAs = new() { Header = "Save to mod as..." };
    private readonly Button draftSave;
    private readonly Button reset;
    private readonly ContentControl details = new();
    private readonly StackPanel issueList = new();
    private readonly Expander issues = new() { Header = "Review issues", Visibility = Visibility.Collapsed };
    private readonly ScrollViewer issueScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly RowDefinition issueRow = new() { Height = new GridLength(0) };
    private readonly GridSplitter issueSplitter = new() { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch,
        ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        Visibility = Visibility.Collapsed, ToolTip = "Drag to resize the issues panel" };
    private GridLength issueHeight = new(0.4, GridUnitType.Star);
    private readonly DispatcherTimer editTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private BehaviorEditorControl? editor;
    private PresetProject? project;
    private PresetProjectEntry? activeEntry;
    private readonly Dictionary<string, PresetValidationResult> validations = [];
    private string? editingId;
    private bool editorDirty;
    private bool rendering;
    private PresetPresentation? presentation;
    private PresetDocument? document;
    private PresetValidationResult? validation;
    private IReadOnlyList<FormRow> forms = [];
    private IReadOnlyList<FormGroupDefinition> groups = [];
    private StageAvailability stageAvailability = new();
    private IReadOnlyDictionary<string, IReadOnlyList<string>> warnings = new Dictionary<string, IReadOnlyList<string>>();
    private string filename = "New preset";
    private string? exportRoot;
    private string? draftPath;
    private bool dirty;
    private CancellationTokenSource? checking;
    private Window Host => Window.GetWindow(this);

    public PresetWorkspace()
    {
        var layout = new DockPanel();
        var header = new StackPanel();
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var back = EditorControls.Button("Back to presets", () => BrowseRequested?.Invoke());
        back.ToolTip = "Return to the preset list. Your current edit is kept until you close it or open another preset.";
        DockPanel.SetDock(back, Dock.Left); toolbar.Children.Add(back);
        focus = EditorControls.Button("Focus editor", ToggleFocusMode);
        focus.ToolTip = "Hide the behavior list to give the selected settings the full width.";
        DockPanel.SetDock(focus, Dock.Right); toolbar.Children.Add(focus);
        reviewIssues = EditorControls.Button("Review issues", () => issues.IsExpanded = !issues.IsExpanded);
        DockPanel.SetDock(reviewIssues, Dock.Right); toolbar.Children.Add(reviewIssues);
        reset = EditorControls.Button("Reset...", ResetPreset);
        reset.ToolTip = "Clear the whole preset and start again.";
        DockPanel.SetDock(reset, Dock.Right); toolbar.Children.Add(reset);
        var fileMenu = new Menu { VerticalAlignment = VerticalAlignment.Center };
        var file = new MenuItem { Header = "File" };
        foreach (var (label, action) in new (string, Action)[] { ("New preset...", New), ("Open file or draft...", OpenFile), ("Import into this preset...", ImportFile) })
        {
            var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); file.Items.Add(item);
        }
        fileDraft.Click += (_, _) => SaveDraft(); file.Items.Add(fileDraft);
        fileSaveAs.Click += (_, _) => Save(true); file.Items.Add(fileSaveAs);
        fileMenu.Items.Add(file); DockPanel.SetDock(fileMenu, Dock.Left); toolbar.Children.Add(fileMenu);
        var saveActions = new StackPanel { Orientation = Orientation.Horizontal };
        draftSave = EditorControls.Button("Save draft...", () => SaveDraft());
        save = new Button { Content = "Save to mod...", Command = ApplicationCommands.Save, CommandTarget = this,
            ToolTip = "Save to mod (Ctrl+S)" };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (_, _) => Save(),
            (_, args) => args.CanExecute = IsVisible && IsEnabled && Ready));
        yaml = EditorControls.Button("View YAML...", ShowYaml);
        saveActions.Children.Add(draftSave); saveActions.Children.Add(save); saveActions.Children.Add(yaml); toolbar.Children.Add(saveActions);
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        title.FontSize = 15; title.Margin = new Thickness(0); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = location });
        state.FontSize = 12; state.Margin = new Thickness(12, 0, 0, 0); state.TextWrapping = TextWrapping.NoWrap; state.TextTrimming = TextTrimming.CharacterEllipsis;
        var contextRow = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        contextRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contextRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        contextRow.Children.Add(title); Grid.SetColumn(state, 1); contextRow.Children.Add(state);
        header.Children.Add(toolbar); header.Children.Add(contextRow);
        addBehavior = EditorControls.IconButton("\uE710", "Add behavior", AddBehavior);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        up = EditorControls.IconButton("\uE74A", "Move behavior up", () => Move(-1));
        down = EditorControls.IconButton("\uE74B", "Move behavior down", () => Move(1));
        delete = EditorControls.IconButton("\uE74D", "Delete behavior", Remove);
        blockActions.Children.Add(addBehavior);
        blockActions.Children.Add(up);
        blockActions.Children.Add(down);
        blockActions.Children.Add(delete);
        blocks.ToolTip = "Select a behavior to edit.";
        issueScroll.Content = issueList; issues.Content = issueScroll;
        issueScroll.PreviewMouseWheel += ScrollIssue;
        issues.Expanded += (_, _) => UpdateIssuePanel();
        issues.Collapsed += (_, _) => UpdateIssuePanel();
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(issueRow);
        issueSplitter.SetResourceReference(BackgroundProperty, "DividerStrokeColorDefaultBrush");
        Grid.SetRow(issueSplitter, 1); body.Children.Add(issueSplitter);
        Grid.SetRow(issues, 2); body.Children.Add(issues);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(sidebarColumn);
        columns.ColumnDefinitions.Add(splitterColumn);
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 });
        DockPanel.SetDock(blockActions, Dock.Top); blockSidebar.Children.Add(blockActions); blockSidebar.Children.Add(blocks);
        columns.Children.Add(blockSidebar);
        splitter.SetResourceReference(BackgroundProperty, "DividerStrokeColorDefaultBrush");
        Grid.SetColumn(splitter, 1); columns.Children.Add(splitter);
        details.Margin = new Thickness(8, 0, 0, 0);
        var editorScroll = new ScrollViewer { Name = "EditorViewport", Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(editorScroll, 2); columns.Children.Add(editorScroll);
        blocks.SelectionChanged += (_, _) => { if (!rendering) SelectBlock(); };
        editTimer.Tick += async (_, _) => { editTimer.Stop(); if (CommitEditor()) await Refresh(SelectedId); };
        Loaded += (_, _) =>
        {
            if (editorDirty || project is not null && checking is not { IsCancellationRequested: false } &&
                project.Entries.Any(entry => !validations.ContainsKey(entry.Id))) editTimer.Start();
        };
        Unloaded += (_, _) => { editTimer.Stop(); checking?.Cancel(); };
        body.Children.Add(columns); layout.Children.Add(body); Content = layout;
        ShowWelcome();
        UpdateActions();
    }

    private string? SelectedId => (blocks.SelectedItem as ListBoxItem)?.Tag as string;

    private void ToggleFocusMode()
    {
        focused = !focused;
        UpdateSidebar();
        focus.Content = focused ? "Show behaviors" : "Focus editor";
    }

    private void UpdateSidebar()
    {
        var visible = blocks.Items.Count > 0 && !focused;
        if ((blockSidebar.Visibility == Visibility.Visible) == visible) return;
        if (!visible) sidebarWidth = sidebarColumn.Width;
        sidebarColumn.MinWidth = visible ? 180 : 0;
        sidebarColumn.Width = visible ? sidebarWidth : new GridLength(0);
        splitterColumn.Width = new GridLength(visible ? 8 : 0);
        blockSidebar.Visibility = splitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        details.Margin = visible ? new Thickness(8, 0, 0, 0) : new Thickness(0);
    }

    private void ShowWelcome()
    {
        var welcome = new StackPanel { Margin = new Thickness(15) };
        if (blocks.Items.Count > 0)
        {
            welcome.Children.Add(EditorControls.Text("Select a behavior to edit its settings."));
            details.Content = welcome;
            return;
        }
        if (project?.Entries.Any(entry => entry.IsInactive && entry.OpenedPath is not null) == true)
        {
            welcome.Children.Add(EditorControls.Text("All behaviors have been removed", true));
            welcome.Children.Add(EditorControls.Text("Save to mod to review the files to disable. Their contents will be kept as inactive backups."));
        }
        else welcome.Children.Add(EditorControls.Text("Describe what happens to your items", true));
        welcome.Children.Add(EditorControls.Text("Add a behavior, choose the items it affects, then set how they change."));
        var start = new WrapPanel();
        if (project is null)
        {
            start.Children.Add(EditorControls.Button("New preset...", New));
            start.Children.Add(EditorControls.Button("Open file...", OpenFile));
        }
        else start.Children.Add(EditorControls.Button("Add behavior...", AddBehavior));
        welcome.Children.Add(start); details.Content = welcome;
    }

    private void EnsureProject()
    {
        if (project is null && document is not null)
        {
            project = new PresetProject { Name = filename };
            activeEntry = project.Add(document, filename);
        }
    }

    private bool Ready => project is { Entries.Count: > 0 }
        && project.Entries.Any(entry => !entry.IsInactive || entry.OpenedPath is not null)
        && project.Entries.All(entry => validations.GetValueOrDefault(entry.Id)?.IsValid == true);

    internal static string CategoryLabel(string category)
    {
        var index = Array.IndexOf(Categories, category);
        return index < 0 ? category : CategoryNames[index];
    }

    private string StatusText(bool saved = false)
    {
        var count = validations.Values.Sum(result => result.FileErrors.Count + result.Errors.Values.Sum(errors => errors.Count));
        var warningCount = warnings.Values.Sum(items => items.Count);
        var removals = project?.Entries.Count(entry => entry.IsInactive && entry.OpenedPath is not null) ?? 0;
        return (Ready ? saved && !dirty ? "✓ Saved to mod" : "✓ Ready to save to a mod" : count > 0 ? $"{count} {(count == 1 ? "issue" : "issues")}" : "Add or complete a behavior")
            + (warningCount > 0 ? $" · {warningCount} {(warningCount == 1 ? "warning" : "warnings")}" : "")
            + (removals > 0 ? $" · {removals} {(removals == 1 ? "file" : "files")} to remove on save" : "")
            + (dirty ? " · Unsaved changes" : "");
    }

    public async void SetCatalog(IReadOnlyList<FormRow> catalog, IReadOnlyList<FormGroupDefinition> formGroups, StageAvailability? stages = null)
    {
        forms = catalog;
        groups = formGroups;
        stageAvailability = stages ?? new(); warnings = new Dictionary<string, IReadOnlyList<string>>();
        presentation = null;
        validations.Clear();
        if (document is not null && CommitEditor())
        {
            var selected = SelectedId;
            ResetEditor();
            await Refresh(selected);
        }
    }

    public bool CanLeave()
    {
        if (!dirty) return true;
        var dialog = new Window { Owner = Host, Title = "Unsaved changes", Width = 530, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(EditorControls.Text("Save your work before leaving?", true));
        panel.Children.Add(EditorControls.Text("A draft keeps unfinished settings and new form groups. It does not change your installed preset."));
        EditorControls.Footer(panel, EditorControls.Button("Save draft...", () => { if (SaveDraft()) dialog.DialogResult = true; }),
            EditorControls.Button("Discard", () => dialog.DialogResult = true), EditorControls.Button("Cancel", () => dialog.DialogResult = false, isCancel: true));
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    public Task<bool> Open(PresetFile file) => OpenPresets([file], false, Path.GetFileNameWithoutExtension(file.FullPath));

    private async Task<bool> OpenPresets(IReadOnlyList<PresetFile> files, bool import, string projectName)
    {
        if (!import && !CanLeave() || import && !CommitEditor()) return false;
        try
        {
            var opened = new PresetProject { Name = projectName };
            var simplified = false;
            foreach (var file in files)
            {
                try
                {
                    var source = await File.ReadAllTextAsync(file.FullPath);
                    var kind = file.Kind;
                    if (kind == PresetKind.Other)
                    {
                        var root = PresetYaml.Parse(source);
                        kind = root.Get("formsLists") is not null ? PresetKind.Addon
                            : root.Get("ownerLists") is not null ? PresetKind.Custom : PresetKind.Default;
                    }
                    var category = file.Category;
                    if (!Categories.Contains(category))
                    {
                        category = ChooseCategory(file.FullPath);
                        if (category is null) return false;
                    }
                    var parsed = PresetDocument.Parse(source, category, kind);
                    simplified |= SimplifyBehaviors(parsed);
                    opened.Add(parsed, Path.GetFileNameWithoutExtension(file.FullPath),
                        file.FullPath, Hash(Encoding.UTF8.GetBytes(source)));
                }
                catch (Exception error) { throw new InvalidDataException(file.FullPath + Environment.NewLine + error.Message, error); }
            }
            var selected = opened.Entries.First();
            if (import && project is not null)
            {
                if (!CommitEditor()) return false;
                var combined = project.Clone();
                foreach (var entry in opened.Entries)
                {
                    try
                    {
                        var added = combined.Add(entry.Document, entry.FileName, entry.OpenedPath, entry.OpenedHash);
                        if (entry == selected) selected = added;
                    }
                    catch (Exception error) { throw new InvalidDataException(entry.OpenedPath + Environment.NewLine + error.Message, error); }
                }
                opened = combined;
            }
            ResetEditor(); project = opened; activeEntry = selected; document = activeEntry.Document;
            filename = project.Name; exportRoot = null; draftPath = null; dirty = import || simplified;
            validations.Clear(); presentation = null;
            location.Text = files.Count == 1 ? "Opened from: " + files[0].FullPath : $"Opened {files.Count} preset files";
            Opened?.Invoke(); await Refresh(document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Rule)?.Id);
            return true;
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot open preset", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    private void ImportFile() => PickFile(true);
    public void OpenFile() => PickFile(false);

    private async void PickFile(bool import)
    {
        try
        {
            var paths = PresetOpenDialog.Show(Host, includeDrafts: !import);
            if (paths is null) return;
            var files = await Task.Run(() => PresetCatalog.DiscoverSelection(paths, Categories));
            if (Path.GetExtension(files[0].FullPath).Equals(".aotdraft", StringComparison.OrdinalIgnoreCase))
            {
                await OpenDraft(files[0].FullPath);
                return;
            }
            var name = paths.Count == 1
                ? (Directory.Exists(paths[0]) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))
                    : Path.GetFileNameWithoutExtension(paths[0])) : "Imported presets";
            if (!ValidFilename(name)) name = "Imported presets";
            await OpenPresets(files, import, name);
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot open preset", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task OpenDraft(string path)
    {
        if (!CanLeave()) return;
        var parsed = PresetProject.FromDraft(await File.ReadAllTextAsync(path), Path.GetFileNameWithoutExtension(path));
        var simplified = false;
        foreach (var entry in parsed.Entries) simplified |= SimplifyBehaviors(entry.Document);
        ResetEditor(); project = parsed; activeEntry = project.Entries.FirstOrDefault(); document = activeEntry?.Document;
        filename = project.Name; draftPath = path; exportRoot = null; dirty = simplified;
        validations.Clear(); presentation = null;
        location.Text = "Draft: " + draftPath;
        Opened?.Invoke(); await Refresh();
    }

    private string? ChooseCategory(string path)
    {
        var dialog = new Window { Title = "Which items does this file affect?", Owner = Host, Width = 460,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(EditorControls.Text(Path.GetFileName(path)));
        panel.Children.Add(EditorControls.Text("Item type"));
        var types = new ComboBox { ItemsSource = CategoryNames, SelectedIndex = 0 }; panel.Children.Add(types);
        EditorControls.Footer(panel, EditorControls.Button("Open", () => dialog.DialogResult = true, isDefault: true),
            EditorControls.Button("Cancel", () => dialog.DialogResult = false, isCancel: true));
        dialog.Content = panel;
        dialog.ToolTip = path;
        return dialog.ShowDialog() == true ? Categories[types.SelectedIndex] : null;
    }
    private static bool SimplifyBehaviors(PresetDocument preset)
    {
        var before = preset.Blocks.Count;
        foreach (var block in preset.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray())
            if (preset.Blocks.Contains(block)) BehaviorFamily.Normalize(preset, block.Id);
        return preset.Blocks.Count != before;
    }

    public async void New()
    {
        if (!CanLeave()) return;
        var dialog = new Window { Title = "New preset", Owner = Host, Width = 460,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(EditorControls.Text("Name"));
        var name = new TextBox { Text = "New preset" }; panel.Children.Add(name);
        var error = EditorControls.Text(""); panel.Children.Add(error);
        EditorControls.Footer(panel, EditorControls.Button("Create", () =>
        {
            if (!ValidFilename(name.Text.Trim())) { error.Text = "Choose a valid file name."; return; }
            dialog.DialogResult = true;
        }, isDefault: true), EditorControls.Button("Cancel", () => dialog.DialogResult = false, isCancel: true));
        dialog.Content = panel; if (dialog.ShowDialog() != true) return;
        await StartEmptyPreset(name.Text.Trim());
    }

    private async void ResetPreset()
    {
        if (project is null) return;
        var dialog = new Window { Title = "Reset preset", Owner = Host, Width = 440,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(EditorControls.Text("Clear the whole preset?", true));
        panel.Children.Add(EditorControls.Text("This removes all behaviors, variables, and unsaved form groups from this edit. Saved files stay on disk."));
        EditorControls.Footer(panel, EditorControls.Button("Clear preset", () => dialog.DialogResult = true),
            EditorControls.Button("Cancel", () => dialog.DialogResult = false, isCancel: true));
        dialog.Content = panel;
        if (dialog.ShowDialog() == true) await StartEmptyPreset(filename);
    }

    private async Task StartEmptyPreset(string presetName)
    {
        ResetEditor(); filename = presetName; project = new PresetProject { Name = filename };
        activeEntry = null; document = null; presentation = null; validations.Clear();
        exportRoot = null; draftPath = null; dirty = true;
        location.Text = "Not saved"; Opened?.Invoke(); await Refresh();
    }

    private async void AddBehavior()
    {
        EnsureProject(); if (project is null || !CommitEditor()) return;
        var dialog = new Window { Title = "Add behavior", Owner = Host, Width = 550,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(EditorControls.Text("Name"));
        var name = new TextBox(); panel.Children.Add(name);
        panel.Children.Add(EditorControls.Text("Item type"));
        var types = new ComboBox { ItemsSource = CategoryNames, SelectedIndex = Math.Max(0, Array.IndexOf(Categories, document?.Category ?? "FOOD")) }; panel.Children.Add(types);
        var error = EditorControls.Text(""); panel.Children.Add(error);
        EditorControls.Footer(panel, EditorControls.Button("Create", () =>
        {
            if (name.Text.Trim().Length == 0) { error.Text = "Give this behavior a name."; return; }
            dialog.DialogResult = true;
        }, isDefault: true), EditorControls.Button("Cancel", () => dialog.DialogResult = false, isCancel: true));
        dialog.Content = panel; if (dialog.ShowDialog() != true) return;
        var kind = PresetKind.Addon;
        var entry = project.GetOrCreate(Categories[types.SelectedIndex], kind);
        if (entry.Document.Blocks.Any(block => block.Kind == BlockKind.Rule && block.Key is "ownerLists" or "formsLists"))
            entry = project.Add(PresetDocument.Create(entry.Document.Category, kind), filename);
        ResetEditor(); activeEntry = entry; document = entry.Document; presentation = null;
        var context = PresetDocument.RuleContext(kind);
        var added = document.Add(BlockKind.Rule, name.Text.Trim(), context); added.Value = ValueEditorWindow.NewValue(context);
        entry.PendingRemoval = false;
        added.Value.Set("forms", PresetValue.List());
        validations.Remove(entry.Id); dirty = true; await Refresh(added.Id);
    }

    private void ResetEditor()
    {
        editTimer.Stop(); checking?.Cancel();
        editor = null; editingId = null; editorDirty = false;
        validation = null; details.Content = null;
    }

    private async void SelectBlock()
    {
        EnsureProject(); var id = SelectedId;
        if (id == editingId && editor is not null) return;
        var needsValidation = editorDirty;
        if (!CommitEditor()) { RenderBlocks(editingId); return; }
        var entry = id is null ? null : project?.FindByBlockId(id);
        if (entry is null || id is null)
        {
            editor = null; editingId = null;
            if (project?.Entries.FirstOrDefault(candidate => candidate.Id == id) is { } fileEntry) ShowFileIssues(fileEntry);
            else ShowWelcome();
            UpdateActions(); return;
        }
        activeEntry = entry; document = entry.Document; validation = validations.GetValueOrDefault(entry.Id);
        var availableGroups = AvailableGroups();
        presentation = new PresetPresentation(document, forms, availableGroups, validation?.Expanded);
        editor = new BehaviorEditorControl(document, forms, id, availableGroups);
        editor.KindChangeRequested += ChangeBehaviorKind;
        editingId = id; editorDirty = false; editor.Changed += EditorChanged;
        details.Content = editor;
        editor.UpdatePresentation(presentation, presentation.EffectiveValue(document.Blocks.First(block => block.Id == id)));
        UpdateActions(); if (needsValidation) await Refresh(id);
    }

    private void EditorChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, editor)) return;
        editorDirty = true; dirty = true; validation = null; checking?.Cancel();
        warnings = new Dictionary<string, IReadOnlyList<string>>();
        if (activeEntry is not null) validations.Remove(activeEntry.Id);
        title.Text = filename + " *"; state.Text = "Unsaved changes · Checking after edit...";
        UpdateActions(); editTimer.Stop(); editTimer.Start();
    }

    private PresetDocument? ChangeBehaviorKind(PresetDocument edited, string ruleId, PresetKind kind)
    {
        if (project is null) return null;
        var candidate = project.Clone();
        var source = candidate.FindByBlockId(ruleId)!; source.Document = edited.Clone();
        var catalog = new PresetPresentation(edited, forms, AvailableGroups());
        var conversions = new Dictionary<string, PresetValue>(StringComparer.Ordinal);
        if (kind == PresetKind.Default)
        {
            if (MessageBox.Show(Host, "Apply this behavior to all " + CategoryLabel(edited.Category).ToLowerInvariant() +
                "? This replaces the current item selection.", "Change affected items",
                MessageBoxButton.OKCancel) != MessageBoxResult.OK) return null;
        }
        else if (edited.Kind == PresetKind.Custom)
        {
            foreach (var selector in candidate.BehaviorSelectors(ruleId))
            {
                if (selector.IsNull || string.IsNullOrWhiteSpace(selector.Text) || selector.IsUnsetArgument ||
                    catalog.ResolveGroup(selector.Text) is not null) continue;
                var matches = forms.Where(form => new FormSelection(Category: edited.Category).Matches(form) &&
                    catalog.MatchesOwner(selector.Text, form)).ToArray();
                var exact = catalog.ResolveForm(selector.Text);
                if (matches.Length > 1 || matches.Length == 1 && matches[0] != exact)
                {
                    var items = PresetValue.List(); items.Items.AddRange(matches.Select(form => PresetValue.Scalar(form.Identifier)));
                    conversions[selector.Text] = items;
                }
                else if (matches.Length == 0 && exact is null &&
                    !PresetPresentation.NormalizeIdentifier(selector.Text).Contains('~'))
                    throw new InvalidOperationException("No loaded items match '" + selector.Text +
                        "'. Choose individual items before removing the stages.");
            }
            if (conversions.Count > 0 && MessageBox.Show(Host,
                "Replace these name matches with their current items?\n\n" +
                string.Join("\n", conversions.Select(pair => pair.Key + ": " + pair.Value.Items.Count + " items")) +
                "\n\nLater changes to these name matches will not change the selection.",
                "Use current items", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return null;
        }
        var changed = candidate.ChangeBehaviorKind(ruleId, kind,
            value => conversions.TryGetValue(value.Text, out var replacement) ? replacement.Clone() : value);
        checking?.Cancel(); validations.Clear(); warnings = new Dictionary<string, IReadOnlyList<string>>();
        project = candidate; activeEntry = changed; document = changed.Document; presentation = null; validation = null;
        return changed.Document;
    }

    private IReadOnlyList<FormGroupDefinition> AvailableGroups() => project is null ? groups : groups
        .Concat(project.Entries.SelectMany(entry => entry.Document.PendingGroups))
        .GroupBy(group => group.Name, StringComparer.Ordinal).Select(group => group.Last()).ToArray();

    private void UpdateDocument(PresetProjectEntry entry, PresetDocument updated)
    {
        var previousGroups = entry.Document.PendingGroups;
        if (previousGroups.Count != updated.PendingGroups.Count || previousGroups.Zip(updated.PendingGroups)
            .Any(pair => pair.First.Name != pair.Second.Name || !pair.First.Members.SequenceEqual(pair.Second.Members)))
        { validations.Clear(); presentation = null; }
        else validations.Remove(entry.Id);
        entry.Document = updated;
    }

    private bool CommitEditor()
    {
        if (!editorDirty || editor is null || editingId is null) return true;
        try
        {
            editor.Capture(); document = editor.Document; editingId = editor.RuleId;
            if (activeEntry is not null) UpdateDocument(activeEntry, document);
            editorDirty = false; dirty = true; editTimer.Start(); return true;
        }
        catch (Exception error)
        { MessageBox.Show(Host, error.Message, "Cannot apply behavior changes", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    private async void Remove()
    {
        if (document is null || !CommitEditor() || SelectedId is not { } id) return;
        var block = document.Blocks.First(entry => entry.Id == id);
        var family = BehaviorFamily.Find(document, id);
        var affected = family?.Rows.ToArray() ?? [block];
        if (MessageBox.Show(Host, $"Delete '{family?.Definition.Name ?? block.Name}'" + (affected.Length > 1 ? $" and its {affected.Length} rows?" : "?"), "Delete behavior", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        ResetEditor(); project!.RemoveBehavior(id);
        if (activeEntry is not null) validations.Remove(activeEntry.Id);
        dirty = true; await Refresh();
    }

    private PresetBlock? GetMoveTarget(int direction)
    {
        if (document is null) return null;
        var siblings = blocks.Items.OfType<ListBoxItem>()
            .Select(item => document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Rule && Equals(item.Tag, block.Id)))
            .OfType<PresetBlock>().ToArray();
        var index = Array.FindIndex(siblings, block => block.Id == SelectedId);
        var next = index + direction;
        return index >= 0 && next >= 0 && next < siblings.Length ? siblings[next] : null;
    }

    private async void Move(int direction)
    {
        if (document is null || !CommitEditor() || SelectedId is not { } id || GetMoveTarget(direction) is not { } target) return;
        var rows = BehaviorFamily.Find(document, id)?.Rows ?? [document.Blocks.First(block => block.Id == id)];
        var targetRows = BehaviorFamily.Find(document, target.Id)?.Rows ?? [target];
        var index = Math.Min(document.Blocks.IndexOf(rows[0]), document.Blocks.IndexOf(targetRows[0]));
        var reordered = (direction < 0 ? rows.Concat(targetRows) : targetRows.Concat(rows)).ToArray();
        ResetEditor();
        foreach (var row in reordered) document.Blocks.Remove(row);
        document.Blocks.InsertRange(index, reordered);
        if (activeEntry is not null) validations.Remove(activeEntry.Id);
        dirty = true; await Refresh(id);
    }



    private async Task Refresh(string? selection = null)
    {
        editTimer.Stop(); checking?.Cancel(); EnsureProject();
        if (project is not null)
            foreach (var id in project.RemoveEmptyEntries())
            {
                validations.Remove(id);
                if (activeEntry?.Id != id) continue;
                ResetEditor(); activeEntry = null; document = null; presentation = null;
            }
        title.Text = filename + (dirty ? " *" : "");
        if (project is null || project.Entries.Count == 0)
        { RenderBlocks(null); ShowWelcome(); state.Text = "Add your first behavior"; UpdateActions(); RenderIssues(); return; }
        state.Text = "Checking behaviors..."; UpdateActions();
        UpdatePresentation(); RenderBlocks(selection); SelectBlock();
        using var request = new CancellationTokenSource(); checking = request;
        try
        {
            var availableGroups = AvailableGroups();
            foreach (var entry in project.Entries)
            {
                if (entry.IsInactive)
                {
                    validations[entry.Id] = new("", "", new(), PresetValidation.GroupErrors(entry.Document));
                    continue;
                }
                if (validations.ContainsKey(entry.Id)) continue;
                var catalog = new PresetPresentation(entry.Document, forms, availableGroups);
                var result = await PresetValidation.Check(entry.Document.Clone(), request.Token, catalog);
                request.Token.ThrowIfCancellationRequested(); validations[entry.Id] = result;
            }
            validation = activeEntry is null ? null : validations.GetValueOrDefault(activeEntry.Id);
            warnings = stageAvailability.Check(project, validations, forms, groups);
            state.Text = StatusText();
            state.ToolTip = "Checks values and form compatibility. Test the saved behavior in game.";
            UpdatePresentation(validation?.Expanded);
            RenderBlocks(editingId ?? SelectedId); SelectBlock(); UpdateActions(); RenderIssues();
            if (editor is not null && editingId is not null && presentation is not null && document is not null)
            {
                var block = document.Blocks.FirstOrDefault(entry => entry.Id == editingId);
                if (block is not null) editor.UpdatePresentation(presentation, presentation.EffectiveValue(block));
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(checking, request)) checking = null; }
    }

    private void UpdatePresentation(string? expanded = null)
    {
        if (document is null) return;
        if (presentation is null) presentation = new PresetPresentation(document, forms, AvailableGroups(), expanded);
        else presentation.Update(document, expanded);
    }

    private void RenderIssues()
    {
        issueList.Children.Clear();
        if (project is null) return;
        var availableGroups = AvailableGroups();
        foreach (var entry in project.Entries)
        {
            if (!validations.TryGetValue(entry.Id, out var result)) continue;
            var catalog = new PresetPresentation(entry.Document, forms, availableGroups, result.Expanded);
            foreach (var error in result.FileErrors)
                issueList.Children.Add(IssueButton(issueList.Children.Count + 1, FileLocation(entry), error, () => SelectFileIssue(entry)));
            foreach (var block in entry.Document.Blocks)
                foreach (var error in result.Errors.GetValueOrDefault(block.Id) ?? [])
                {
                    issueList.Children.Add(IssueButton(issueList.Children.Count + 1, BehaviorEditorControl.IssueLocation(entry.Document, block.Id, error, catalog.EffectiveValue(block)),
                        error, () => SelectIssue(entry, block, error)));
                }
        }
        var removals = project.Entries.Where(entry => entry.IsInactive && entry.OpenedPath is not null).ToArray();
        foreach (var entry in removals)
            issueList.Children.Add(EditorControls.Text(FileLocation(entry) + "\nAll behaviors removed. Save to mod will ask before disabling this file."));
        foreach (var entry in project.Entries)
            foreach (var block in entry.Document.Blocks)
                foreach (var warning in warnings.GetValueOrDefault(block.Id) ?? [])
                {
                    var text = EditorControls.Text(block.Name + ": " + warning);
                    text.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
                    issueList.Children.Add(text);
                }
        var errors = validations.Values.Sum(result => result.FileErrors.Count + result.Errors.Values.Sum(items => items.Count));
        var warningCount = warnings.Values.Sum(items => items.Count);
        issues.Header = errors > 0 ? $"Review issues ({errors})" + (warningCount > 0 ? $" · Warnings ({warningCount})" : "")
            : warningCount > 0 ? $"Warnings ({warningCount})" : $"Pending file removals ({removals.Length})";
        reviewIssues.Content = issues.Header;
        reviewIssues.Visibility = issueList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateIssuePanel();
    }

    private void UpdateIssuePanel()
    {
        var visible = issueList.Children.Count > 0 && validations.Count > 0 && issues.IsExpanded;
        if (visible == (issues.Visibility == Visibility.Visible)) return;
        if (visible) { issueRow.MinHeight = 80; issueRow.Height = issueHeight; }
        else { issueHeight = issueRow.Height; issueRow.MinHeight = 0; issueRow.Height = new GridLength(0); }
        issues.Visibility = issueSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollIssue(object sender, MouseWheelEventArgs args)
    {
        if (args.Delta == 0) return;
        issueScroll.UpdateLayout();
        var starts = issueList.Children.OfType<FrameworkElement>()
            .Select(child => child.TranslatePoint(new Point(), issueList).Y - child.Margin.Top).ToArray();
        var offset = Math.Round(issueScroll.VerticalOffset);
        var target = args.Delta < 0 ? starts.FirstOrDefault(start => Math.Round(start) > offset, issueScroll.ExtentHeight)
            : starts.LastOrDefault(start => Math.Round(start) < offset, 0);
        issueScroll.ScrollToVerticalOffset(target);
        args.Handled = true;
    }

    private static Button IssueButton(int number, string location, string message, Action open)
    {
        var content = new StackPanel();
        var heading = new DockPanel();
        heading.Children.Add(EditorControls.Text(number + ". ", true));
        heading.Children.Add(EditorControls.Text(location, true));
        content.Children.Add(heading);
        content.Children.Add(EditorControls.Text(message));
        var button = EditorControls.Button("", open);
        button.Content = content; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    private string FileLocation(PresetProjectEntry entry)
    {
        if (entry.OpenedPath is not null) return entry.OpenedPath;
        var root = exportRoot ?? AppContext.BaseDirectory;
        return Path.GetRelativePath(Path.Combine(root, "SKSE", "Plugins", "AlchemyOfTime"), project!.GetExportPath(entry, root));
    }

    private void SelectIssue(PresetProjectEntry entry, PresetBlock block, string error)
    {
        if (!CommitEditor()) return;
        block = entry.Document.Blocks.FirstOrDefault(candidate => candidate.Id == block.Id) ?? block;
        var family = BehaviorFamily.Find(entry.Document, block.Id);
        if (block.Kind != BlockKind.Rule && family is null) { EditDefinitionIssue(entry, block, error); return; }
        var id = family?.Rows.FirstOrDefault()?.Id ?? block.Id;
        var item = blocks.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, id));
        if (item is null) { SelectFileIssue(entry); return; }
        blocks.SelectedItem = item;
        editor?.FocusIssue(error, block.Id);
    }

    private async void EditDefinitionIssue(PresetProjectEntry entry, PresetBlock block, string error)
    {
        var view = new ValueEditorWindow(entry.Document, forms, block.Value, block.Context, "Shared settings",
            parametersAllowed: block.Kind == BlockKind.Template, blockName: block.Name,
            issues: validations.GetValueOrDefault(entry.Id)?.Errors.GetValueOrDefault(block.Id), groups: AvailableGroups()) { Owner = Host };
        view.Loaded += (_, _) => view.Editor.FocusIssue(error);
        if (view.ShowDialog() != true) return;
        try
        {
            var updated = view.Document; var previous = updated.Clone();
            var changed = updated.Blocks.First(candidate => candidate.Id == block.Id);
            changed.Value = view.Value; changed.Name = view.BlockName;
            updated.RebindTemplateCalls(previous);
            var selected = SelectedId;
            ResetEditor(); UpdateDocument(entry, updated);
            if (activeEntry == entry) { document = updated; presentation = null; }
            dirty = true; await Refresh(selected);
        }
        catch (Exception failure) { MessageBox.Show(Host, failure.Message, "Cannot apply shared settings", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SelectFileIssue(PresetProjectEntry entry)
    {
        if (!CommitEditor()) return;
        var item = blocks.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, entry.Id));
        if (ReferenceEquals(blocks.SelectedItem, item)) SelectBlock();
        else blocks.SelectedItem = item;
    }

    private static IReadOnlyList<string> FileIssues(PresetProjectEntry entry, PresetValidationResult? result)
    {
        if (result is null) return [];
        var catalog = new PresetPresentation(entry.Document, []);
        return [.. result.FileErrors, .. entry.Document.Blocks.Where(block => block.Kind != BlockKind.Rule && !block.IsBehavior &&
            !catalog.Uses(block).Any(use => use.Kind == BlockKind.Rule)).SelectMany(block =>
                (result.Errors.GetValueOrDefault(block.Id) ?? []).Select(error => block.Name + ": " + error))];
    }

    private void ShowFileIssues(PresetProjectEntry entry)
    {
        activeEntry = entry; document = entry.Document; validation = validations.GetValueOrDefault(entry.Id);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(EditorControls.Text("File: " + entry.FileName + ".yml", true));
        panel.Children.Add(EditorControls.Text(FileLocation(entry)));
        foreach (var message in FileIssues(entry, validation)) panel.Children.Add(EditorControls.Text(message));
        if (!entry.HasBehaviors && !entry.IsInactive && entry.OpenedPath is not null)
        {
            panel.Children.Add(EditorControls.Text("This file has no behaviors. Removing it on save keeps an inactive backup of the original file."));
            panel.Children.Add(EditorControls.Button("Remove file on save", () => QueueEmptyFileRemoval(entry)));
        }
        if (validation is { Source.Length: > 0 } result)
            panel.Children.Add(EditorControls.Button("View YAML...", () =>
                new YamlPreviewWindow(entry.FileName, result.Source, result.Expanded) { Owner = Host }.ShowDialog()));
        details.Content = panel;
    }

    private async void QueueEmptyFileRemoval(PresetProjectEntry entry)
    {
        if (entry.HasBehaviors || entry.OpenedPath is null) return;
        entry.PendingRemoval = true; validations.Remove(entry.Id); dirty = true;
        await Refresh();
    }

    private void RenderBlocks(string? selected)
    {
        EnsureProject(); rendering = true;
        try
        {
            blocks.Items.Clear(); if (project is null) return;
            var availableGroups = AvailableGroups();
            foreach (var entry in project.Entries)
            {
                var catalog = new PresetPresentation(entry.Document, forms, availableGroups, validations.GetValueOrDefault(entry.Id)?.Expanded);
                foreach (var block in catalog.Behaviors())
                {
                    var family = BehaviorFamily.Find(entry.Document, block.Id);
                    var rows = family?.Rows ?? [block];
                    var errors = (rows.Count == 0 ? [block] : rows).SelectMany(row =>
                        validations.GetValueOrDefault(entry.Id)?.Errors.GetValueOrDefault(row.Id) ?? []).ToArray();
                    var result = validations.GetValueOrDefault(entry.Id);
                    var status = result is null ? "•" : errors.Length > 0 || result.FileErrors.Count > 0 ? "!" : "✓";
                    var rowPanel = new DockPanel { Margin = new Thickness(4, 5, 4, 5), MinHeight = 40 };
                    var mark = EditorControls.Text(status); mark.FontSize = 20; mark.Width = 27; DockPanel.SetDock(mark, Dock.Left); rowPanel.Children.Add(mark);
                    var text = new StackPanel();
                    var label = family?.Definition.Name ?? block.Name;
                    var name = EditorControls.Text(label); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0, 0, 0, 3);
                    name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; text.Children.Add(name);
                    var description = CategoryLabel(entry.Document.Category) + " · " + (rows.Count > 1 ? $"{rows.Count} rows" : catalog.RuleSummary(block, behaviorMode: true));
                    var summary = EditorControls.Text(description); summary.FontSize = 12; summary.LineHeight = 16; summary.MaxHeight = 32; summary.Margin = new Thickness(0);
                    summary.TextTrimming = TextTrimming.CharacterEllipsis; summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); text.Children.Add(summary);
                    rowPanel.Children.Add(text);
                    var item = new ListBoxItem { Content = rowPanel, Tag = block.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        ToolTip = label + Environment.NewLine + description + (errors.Length > 0 ? Environment.NewLine + string.Join(Environment.NewLine, errors) : "") };
                    blocks.Items.Add(item);
                    if (block.Id == selected || rows.Any(row => row.Id == selected)) blocks.SelectedItem = item;
                }
                if (FileIssues(entry, validations.GetValueOrDefault(entry.Id)) is { Count: > 0 } fileIssues)
                {
                    var content = new StackPanel { Margin = new Thickness(4, 5, 4, 5) };
                    content.Children.Add(EditorControls.Text("! File: " + entry.FileName + ".yml", true));
                    content.Children.Add(EditorControls.Text($"{fileIssues.Count} {(fileIssues.Count == 1 ? "issue" : "issues")} · {entry.Document.Category}"));
                    var item = new ListBoxItem { Content = content, Tag = entry.Id, ToolTip = FileLocation(entry),
                        HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    blocks.Items.Add(item);
                    if (entry.Id == selected) blocks.SelectedItem = item;
                }
            }
            if (blocks.SelectedItem is null) blocks.SelectedItem = blocks.Items.OfType<ListBoxItem>().FirstOrDefault();
        }
        finally { rendering = false; }
    }

    private void UpdateActions()
    {
        EnsureProject();
        addBehavior.Visibility = project is not null ? Visibility.Visible : Visibility.Collapsed;
        if (focused && blocks.Items.Count == 0) ToggleFocusMode();
        UpdateSidebar();
        focus.Visibility = blocks.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        save.Visibility = Ready ? Visibility.Visible : Visibility.Collapsed;
        CommandManager.InvalidateRequerySuggested();
        yaml.Visibility = Ready && project!.Entries.Any(entry => !entry.IsInactive) ? Visibility.Visible : Visibility.Collapsed;
        fileDraft.Visibility = project is not null ? Visibility.Visible : Visibility.Collapsed;
        fileSaveAs.IsEnabled = Ready;
        draftSave.Visibility = project is not null && !Ready ? Visibility.Visible : Visibility.Collapsed;
        reset.Visibility = project is { Entries.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        var selected = document?.Blocks.FirstOrDefault(block => block.Id == SelectedId);
        up.IsEnabled = GetMoveTarget(-1) is not null;
        down.IsEnabled = GetMoveTarget(1) is not null;
        delete.IsEnabled = selected is not null;
        if (validations.Count == 0) reviewIssues.Visibility = Visibility.Collapsed;
        UpdateIssuePanel();
    }

    private void ShowYaml()
    {
        if (!Ready || project is null) return;
        var entries = project.Entries.Where(entry => !entry.IsInactive).ToArray();
        if (entries.Length == 0) return;
        if (entries.Length == 1)
        {
            var result = validations[entries[0].Id];
            new YamlPreviewWindow(filename, result.Source, result.Expanded) { Owner = Host }.ShowDialog(); return;
        }
        var picker = new Window { Title = "Generated files", Owner = Host, MinWidth = 620, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        EditorControls.FitToOwner(picker);
        var layout = new DockPanel { Margin = new Thickness(18) };
        var close = EditorControls.Button("Close", picker.Close, isCancel: true);
        var footer = EditorControls.Footer(layout, close); DockPanel.SetDock(footer, Dock.Bottom);
        var panel = new StackPanel();
        var root = exportRoot ?? AppContext.BaseDirectory;
        var presetRoot = Path.Combine(root, "SKSE", "Plugins", "AlchemyOfTime");
        foreach (var entry in entries)
        {
            var path = Path.GetRelativePath(presetRoot, project.GetExportPath(entry, root));
            var button = EditorControls.Button(path, () =>
            {
                var result = validations[entry.Id];
                new YamlPreviewWindow(entry.FileName, result.Source, result.Expanded) { Owner = picker }.ShowDialog();
            });
            button.Content = new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(button);
        }
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        picker.Content = layout; picker.ShowDialog();
    }

    private bool SaveDraft()
    {
        EnsureProject(); if (project is null || !CommitEditor()) return false;
        var picker = new SaveFileDialog { Title = "Save editor draft", Filter = "AoT editor draft (*.aotdraft)|*.aotdraft",
            DefaultExt = ".aotdraft", FileName = draftPath ?? filename + ".aotdraft", AddExtension = true };
        if (picker.ShowDialog(Host) != true) return false;
        try
        {
            PresetExport.Save(picker.FileName, project.ToDraft(), [], Path.GetDirectoryName(picker.FileName)!);
            draftPath = picker.FileName; dirty = false; title.Text = filename;
            location.Text = "Draft saved: " + draftPath + " · Installed preset unchanged";
            _ = Refresh(editingId); return true;
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot save draft", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    private async void Save(bool chooseDestination = false)
    {
        if (!CommitEditor() || !Ready || project is null) return;
        try
        {
            var modFolder = project.GetSaveFolder() ?? exportRoot;
            if (chooseDestination || modFolder is null)
            {
                var picker = new OpenFolderDialog { Title = "Choose the destination mod folder" };
                if (modFolder is not null && Directory.Exists(modFolder)) picker.InitialDirectory = modFolder;
                if (picker.ShowDialog(Host) != true) return;
                modFolder = picker.FolderName;
            }
            foreach (var entry in project.Entries)
                if (entry.OpenedPath is not null && File.Exists(entry.OpenedPath) && entry.OpenedHash != Hash(Encoding.UTF8.GetBytes(File.ReadAllText(entry.OpenedPath))))
                    throw new IOException("A source file changed after you opened it: " + entry.OpenedPath);
            var changes = project.Entries.Where(entry => !entry.IsInactive).Select(entry => (Entry: entry,
                Output: new PresetOutput(chooseDestination ? project.GetExportPath(entry, modFolder) : project.GetSavePath(entry, modFolder), validations[entry.Id].Source))).ToArray();
            var outputs = changes.Select(change => change.Output).ToArray();
            var existing = outputs.Where(output => File.Exists(output.Path) && (chooseDestination ||
                !project.Entries.Any(entry => entry.OpenedPath is { } path &&
                    Path.GetFullPath(path).Equals(Path.GetFullPath(output.Path), StringComparison.OrdinalIgnoreCase)))).ToArray();
            var destination = Path.GetFullPath(modFolder);
            if (!Path.EndsInDirectorySeparator(destination)) destination += Path.DirectorySeparatorChar;
            var retired = project.Entries.Select(entry => entry.OpenedPath).OfType<string>()
                .Where(path => Path.GetFullPath(path).StartsWith(destination, StringComparison.OrdinalIgnoreCase) &&
                    !outputs.Any(output => Path.GetFullPath(output.Path).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (outputs.Length == 0 && retired.Length == 0)
                throw new IOException("Choose the mod folder containing the files marked for removal.");
            var confirmation = existing.Length > 0 ? "Update these files?\n" + string.Join("\n", existing.Select(output => output.Path)) : "";
            if (retired.Length > 0) confirmation += (confirmation.Length > 0 ? "\n\n" : "") +
                "Disable these files? Existing files will be kept as inactive .aot-backup copies.\n" + string.Join("\n", retired);
            var externalSources = project.Entries.Where(entry => entry.OpenedPath is { } path && File.Exists(path) &&
                    !Path.GetFullPath(path).StartsWith(destination, StringComparison.OrdinalIgnoreCase) &&
                    (entry.IsInactive || !outputs.Any(output => Path.GetFullPath(path).EndsWith(Path.DirectorySeparatorChar +
                        Path.GetRelativePath(destination, output.Path), StringComparison.OrdinalIgnoreCase))))
                .Select(entry => entry.OpenedPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (externalSources.Length > 0) confirmation += (confirmation.Length > 0 ? "\n\n" : "") +
                "These original files are outside the chosen mod folder and will stay unchanged. Disable them in their owning mods to replace or remove their behaviors:\n" +
                string.Join("\n", externalSources);
            if (confirmation.Length > 0 && MessageBox.Show(Host, confirmation, "Save preset", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            var pending = project.Entries.SelectMany(entry => entry.Document.PendingGroups).GroupBy(group => group.Name, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var first = group.First();
                if (group.Any(value => FormGroupCatalog.Serialize(value) != FormGroupCatalog.Serialize(first))) throw new IOException("Different form groups share the name: " + group.Key);
                return first;
            }).ToArray();
            PresetExport.Save(outputs, pending, modFolder, retired);
            foreach (var entry in project.Entries.Where(entry => entry.IsInactive && entry.OpenedPath is not null))
                if (retired.Concat(outputs.Select(output => output.Path)).Any(path =>
                    Path.GetFullPath(path).Equals(Path.GetFullPath(entry.OpenedPath!), StringComparison.OrdinalIgnoreCase)))
                { entry.OpenedPath = null; entry.OpenedHash = null; entry.PendingRemoval = false; }
            foreach (var change in changes)
            { change.Entry.OpenedPath = change.Output.Path; change.Entry.OpenedHash = Hash(Encoding.UTF8.GetBytes(change.Output.Source)); }
            exportRoot = modFolder; dirty = project.Entries.Any(entry => entry.PendingRemoval); location.Text = "Saved to: " + exportRoot;
            title.Text = filename;
            await Refresh(editingId);
            state.Text = StatusText(saved: true);
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot save preset", MessageBoxButton.OK, MessageBoxImage.Error); }
    }



    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool ValidFilename(string name) => name.Length > 0 && name != "." && name != ".." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');
}
