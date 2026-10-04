using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AoTPresetEditor;

public sealed class PresetWorkspace : UserControl
{
    public event Action? Opened;
    public event Action? BrowseRequested;
    private static readonly string[] Categories = ["FOOD", "INGR", "MEDC", "POSN", "ARMO", "WEAP", "BOOK", "SCRL", "SLGM", "MISC"];
    private static readonly string[] CategoryNames = ["Food", "Ingredients", "Medicine", "Poisons", "Armor", "Weapons", "Books", "Scrolls", "Soul gems", "Miscellaneous items"];
    private readonly TextBlock title = EditorControls.Text("Open or create a preset", true);
    private readonly TextBlock state = EditorControls.Text("Choose a file from the list, or create a new preset.");
    private readonly TextBlock location = EditorControls.Text("");
    private readonly ListBox blocks = new() { BorderThickness = new Thickness(0), Margin = new Thickness(0, 4, 0, 4) };
    private readonly Button addBehavior;
    private readonly StackPanel blockActions = new();
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
    private readonly MenuItem blockMenu = new();
    private readonly MenuItem up = new() { Header = "Move up" };
    private readonly MenuItem down = new() { Header = "Move down" };
    private readonly MenuItem fileDraft = new() { Header = "Save draft..." };
    private readonly Button draftSave;
    private readonly ContentControl details = new();
    private readonly StackPanel issueList = new();
    private readonly Expander issues = new() { Header = "Review issues", MaxHeight = 150 };
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
        focus = EditorControls.Button("Focus editor", ToggleFocusMode);
        focus.ToolTip = "Hide the behavior list to give the selected settings the full width.";
        DockPanel.SetDock(focus, Dock.Right); toolbar.Children.Add(focus);
        reviewIssues = EditorControls.Button("Review issues", () =>
        {
            issues.IsExpanded = !issues.IsExpanded;
            issues.Visibility = issues.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        });
        DockPanel.SetDock(reviewIssues, Dock.Right); toolbar.Children.Add(reviewIssues);
        var fileMenu = new Menu { VerticalAlignment = VerticalAlignment.Center };
        var file = new MenuItem { Header = "File" };
        foreach (var (label, action) in new (string, Action)[] { ("New preset...", New), ("Open file or draft...", OpenFile), ("Import into this preset...", ImportFile), ("Installed presets...", () => BrowseRequested?.Invoke()) })
        {
            var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); file.Items.Add(item);
        }
        fileDraft.Click += (_, _) => SaveDraft(); file.Items.Add(fileDraft);
        fileMenu.Items.Add(file); DockPanel.SetDock(fileMenu, Dock.Left); toolbar.Children.Add(fileMenu);
        var saveActions = new StackPanel { Orientation = Orientation.Horizontal };
        draftSave = EditorControls.Button("Save draft...", () => SaveDraft());
        save = EditorControls.Button("Save to mod...", Save);
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
        addBehavior = EditorControls.Button("Add behavior...", AddBehavior);
        addBehavior.Margin = new Thickness(0, 3, 0, 3);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var actions = new Menu();
        var remove = new MenuItem { Header = "Delete..." };
        remove.Click += (_, _) => Remove();
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        blockMenu.Items.Add(up); blockMenu.Items.Add(down); blockMenu.Items.Add(remove);
        actions.Items.Add(blockMenu);
        blockActions.Children.Add(actions);
        blocks.ToolTip = "Select a behavior to edit.";
        issues.Content = new ScrollViewer { Content = issueList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        issues.Collapsed += (_, _) => issues.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(issues, Dock.Bottom); layout.Children.Add(issues);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(sidebarColumn);
        columns.ColumnDefinitions.Add(splitterColumn);
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 });
        DockPanel.SetDock(addBehavior, Dock.Top); blockSidebar.Children.Add(addBehavior);
        DockPanel.SetDock(blockActions, Dock.Bottom); blockSidebar.Children.Add(blockActions); blockSidebar.Children.Add(blocks);
        columns.Children.Add(blockSidebar);
        splitter.SetResourceReference(BackgroundProperty, "DividerStrokeColorDefaultBrush");
        Grid.SetColumn(splitter, 1); columns.Children.Add(splitter);
        details.Margin = new Thickness(8, 0, 0, 0);
        var editorScroll = new ScrollViewer { Name = "EditorViewport", Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(editorScroll, 2); columns.Children.Add(editorScroll);
        blocks.SelectionChanged += (_, _) => { if (!rendering) SelectBlock(); };
        editTimer.Tick += async (_, _) => { editTimer.Stop(); if (CommitEditor()) await Refresh(SelectedId); };
        Unloaded += (_, _) => { editTimer.Stop(); checking?.Cancel(); };
        layout.Children.Add(columns); Content = layout;
        ShowWelcome();
        UpdateActions();
    }

    private string? SelectedId => (blocks.SelectedItem as ListBoxItem)?.Tag as string;

    private void ToggleFocusMode()
    {
        focused = !focused;
        if (focused) sidebarWidth = new GridLength(sidebarColumn.ActualWidth);
        sidebarColumn.MinWidth = focused ? 0 : 180;
        sidebarColumn.Width = focused ? new GridLength(0) : sidebarWidth;
        splitterColumn.Width = new GridLength(focused ? 0 : 8);
        blockSidebar.Visibility = splitter.Visibility = focused ? Visibility.Collapsed : Visibility.Visible;
        details.Margin = focused ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        focus.Content = focused ? "Show behaviors" : "Focus editor";
    }

    private void ShowWelcome()
    {
        var welcome = new StackPanel { Margin = new Thickness(15) };
        welcome.Children.Add(EditorControls.Text("Describe what happens to your items", true));
        welcome.Children.Add(EditorControls.Text("Add a behavior, choose the items it affects, then set how they change. Use a field's options to vary its value between items."));
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
        && project.Entries.All(entry => validations.GetValueOrDefault(entry.Id)?.IsValid == true);

    private static string CategoryLabel(string category)
    {
        var index = Array.IndexOf(Categories, category);
        return index < 0 ? category : CategoryNames[index];
    }

    private string StatusText()
    {
        var count = validations.Values.Sum(result => result.FileErrors.Count + result.Errors.Values.Sum(errors => errors.Count));
        return (Ready ? "✓ Ready to save to a mod" : count > 0 ? $"{count} {(count == 1 ? "issue" : "issues")}" : "Add or complete a behavior")
            + (dirty ? " · Unsaved changes" : "");
    }

    public async void SetCatalog(IReadOnlyList<FormRow> catalog, IReadOnlyList<FormGroupDefinition> formGroups)
    {
        forms = catalog;
        groups = formGroups;
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
            EditorControls.Button("Discard", () => dialog.DialogResult = true), EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    public async Task<bool> Open(PresetFile file) => await Open(file, false);

    private async Task<bool> Open(PresetFile file, bool import)
    {
        if (!import && !CanLeave() || import && !CommitEditor()) return false;
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
                category = ChooseCategory();
                if (category is null) return false;
            }
            var parsed = PresetDocument.Parse(source, category, kind);
            var name = Path.GetFileNameWithoutExtension(file.FullPath);
            if (import && !CommitEditor()) return false;
            if (!import || project is null) project = new PresetProject { Name = name };
            var entry = project.Add(parsed, name, file.FullPath, Hash(Encoding.UTF8.GetBytes(source)));
            ResetEditor(); activeEntry = entry; document = parsed; filename = project.Name;
            exportRoot = null; draftPath = null; dirty = import;
            validations.Clear(); presentation = null;
            location.Text = "Opened from: " + file.FullPath;
            Opened?.Invoke(); await Refresh(parsed.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Rule)?.Id);
            return true;
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot open preset", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    private void ImportFile() => PickFile(true);
    private void OpenFile() => PickFile(false);

    private async void PickFile(bool import)
    {
        var picker = new OpenFileDialog { Title = import ? "Import behaviors" : "Open AoT preset or draft",
            Filter = import ? "YAML presets|*.yml;*.yaml" : "Presets and drafts|*.yml;*.yaml;*.aotdraft|YAML presets|*.yml;*.yaml|Editor drafts|*.aotdraft" };
        if (picker.ShowDialog(Host) != true) return;
        if (Path.GetExtension(picker.FileName).Equals(".aotdraft", StringComparison.OrdinalIgnoreCase))
        {
            if (!CanLeave()) return;
            try
            {
                var parsed = PresetProject.FromDraft(await File.ReadAllTextAsync(picker.FileName), Path.GetFileNameWithoutExtension(picker.FileName));
                ResetEditor(); project = parsed; activeEntry = project.Entries.FirstOrDefault(); document = activeEntry?.Document;
                filename = project.Name; draftPath = picker.FileName; exportRoot = null; dirty = false;
                validations.Clear(); presentation = null;
                location.Text = "Draft: " + draftPath;
                Opened?.Invoke(); await Refresh();
            }
            catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot open draft", MessageBoxButton.OK, MessageBoxImage.Error); }
            return;
        }
        var relative = picker.FileName;
        var marker = "AlchemyOfTime" + Path.DirectorySeparatorChar;
        var start = relative.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        relative = start >= 0 ? relative[(start + marker.Length)..] : Path.GetFileName(relative);
        await Open(new PresetFile(picker.FileName, relative), import);
    }

    private string? ChooseCategory()
    {
        var dialog = new Window { Title = "Which items does this file affect?", Owner = Host, Width = 460,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(EditorControls.Text("Item type"));
        var types = new ComboBox { ItemsSource = CategoryNames, SelectedIndex = 0 }; panel.Children.Add(types);
        EditorControls.Footer(panel, EditorControls.Button("Open", () => dialog.DialogResult = true),
            EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? Categories[types.SelectedIndex] : null;
    }

    private async void New()
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
        }), EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel; if (dialog.ShowDialog() != true) return;
        ResetEditor(); filename = name.Text.Trim(); project = new PresetProject { Name = filename };
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
        panel.Children.Add(EditorControls.Text("What should this behavior do?"));
        var purpose = new ComboBox { ItemsSource = new[] { "Stages", "Add reactions and preservation", "Set fallback aging for this item type" }, SelectedIndex = 0 }; panel.Children.Add(purpose);
        var explanation = EditorControls.Text(""); panel.Children.Add(explanation);
        void Explain() => explanation.Text = purpose.SelectedIndex switch
        {
            0 => "Choose items and define the states they pass through.",
            1 => "Add triggered transformations or time modifiers. Keep the items' existing aging progression.",
            _ => "Define progression for items of this type that have no more specific aging rule."
        };
        purpose.SelectionChanged += (_, _) => Explain(); Explain();
        var error = EditorControls.Text(""); panel.Children.Add(error);
        EditorControls.Footer(panel, EditorControls.Button("Create", () =>
        {
            if (name.Text.Trim().Length == 0) { error.Text = "Give this behavior a name."; return; }
            if (purpose.SelectedIndex == 2 && project.Entries.Any(entry => entry.Document.Category == Categories[types.SelectedIndex] && entry.Document.Kind == PresetKind.Default && entry.Document.Blocks.Any(block => block.Kind == BlockKind.Rule)))
            { error.Text = "This item type already has fallback aging. Edit that behavior."; return; }
            dialog.DialogResult = true;
        }), EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel; if (dialog.ShowDialog() != true) return;
        var kind = purpose.SelectedIndex switch { 0 => PresetKind.Custom, 1 => PresetKind.Addon, _ => PresetKind.Default };
        var entry = project.GetOrCreate(Categories[types.SelectedIndex], kind);
        if (entry.Document.Blocks.Any(block => block.Kind == BlockKind.Rule && block.Key is "ownerLists" or "formsLists"))
            entry = project.Add(PresetDocument.Create(entry.Document.Category, kind), filename);
        ResetEditor(); activeEntry = entry; document = entry.Document; presentation = null;
        var context = PresetDocument.RuleContext(kind);
        var added = document.Add(BlockKind.Rule, name.Text.Trim(), context); added.Value = ValueEditorWindow.NewValue(context);
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
        if (entry is null || id is null) { editor = null; editingId = null; ShowWelcome(); UpdateActions(); return; }
        activeEntry = entry; document = entry.Document; validation = validations.GetValueOrDefault(entry.Id);
        presentation = new PresetPresentation(document, forms, groups, validation?.Expanded);
        editor = new BehaviorEditorControl(document, forms, id, groups);
        editingId = id; editorDirty = false; editor.Changed += EditorChanged;
        details.Content = editor;
        editor.UpdatePresentation(presentation, presentation.EffectiveValue(document.Blocks.First(block => block.Id == id)));
        UpdateActions(); if (needsValidation) await Refresh(id);
    }

    private void EditorChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, editor)) return;
        editorDirty = true; dirty = true; validation = null; checking?.Cancel();
        if (activeEntry is not null) validations.Remove(activeEntry.Id);
        title.Text = filename + " *"; state.Text = "Unsaved changes · Checking after edit...";
        UpdateActions(); editTimer.Stop(); editTimer.Start();
    }

    private bool CommitEditor()
    {
        if (!editorDirty || editor is null || editingId is null) return true;
        try
        {
            editor.Capture(); document = editor.Document; editingId = editor.RuleId;
            if (activeEntry is not null) { activeEntry.Document = document; validations.Remove(activeEntry.Id); }
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
        if (MessageBox.Show(Host, $"Delete '{family?.Definition.Name ?? block.Name}'" + (affected.Length > 1 ? $" and its {affected.Length} variations?" : "?"), "Delete behavior", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        ResetEditor(); foreach (var row in affected) document.Remove(row);
        if (activeEntry is not null) validations.Remove(activeEntry.Id);
        dirty = true; await Refresh();
    }

    private async void Move(int direction)
    {
        if (document is null || !CommitEditor() || SelectedId is not { } id) return;
        var index = document.Blocks.FindIndex(entry => entry.Id == id);
        var next = index + direction;
        while (next >= 0 && next < document.Blocks.Count && document.Blocks[next].Kind != BlockKind.Rule) next += direction;
        if (next < 0 || next >= document.Blocks.Count) return;
        ResetEditor(); (document.Blocks[index], document.Blocks[next]) = (document.Blocks[next], document.Blocks[index]);
        if (activeEntry is not null) validations.Remove(activeEntry.Id);
        dirty = true; await Refresh(id);
    }



    private async Task Refresh(string? selection = null)
    {
        editTimer.Stop(); checking?.Cancel(); EnsureProject();
        title.Text = filename + (dirty ? " *" : "");
        if (project is null || project.Entries.Count == 0)
        { RenderBlocks(null); ShowWelcome(); state.Text = "Add your first behavior"; UpdateActions(); return; }
        state.Text = "Checking behaviors..."; UpdateActions();
        UpdatePresentation(); RenderBlocks(selection); SelectBlock();
        using var request = new CancellationTokenSource(); checking = request;
        try
        {
            foreach (var entry in project.Entries)
            {
                if (validations.ContainsKey(entry.Id)) continue;
                var catalog = new PresetPresentation(entry.Document, forms, groups);
                var result = await PresetValidation.Check(entry.Document.Clone(), request.Token, catalog);
                request.Token.ThrowIfCancellationRequested(); validations[entry.Id] = result;
            }
            validation = activeEntry is null ? null : validations.GetValueOrDefault(activeEntry.Id);
            state.Text = StatusText();
            state.ToolTip = "Checks values and form compatibility. Test the saved behavior in game.";
            UpdatePresentation(validation?.Expanded);
            RenderBlocks(editingId ?? SelectedId); UpdateActions(); RenderIssues();
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
        if (presentation is null) presentation = new PresetPresentation(document, forms, groups, expanded);
        else presentation.Update(document, expanded);
    }

    private void RenderIssues()
    {
        issueList.Children.Clear();
        if (project is null) return;
        foreach (var entry in project.Entries)
        {
            if (!validations.TryGetValue(entry.Id, out var result)) continue;
            foreach (var error in result.FileErrors) issueList.Children.Add(EditorControls.Text(entry.FileName + ": " + error));
            foreach (var block in entry.Document.Blocks)
                foreach (var error in result.Errors.GetValueOrDefault(block.Id) ?? [])
                {
                    var button = EditorControls.Button(block.Name + ": " + error, () =>
                    {
                        var family = BehaviorFamily.Find(entry.Document, block.Id);
                        var id = family?.Rows.FirstOrDefault()?.Id ?? block.Id;
                        var item = blocks.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, id));
                        if (item is null)
                        {
                            var catalog = new PresetPresentation(entry.Document, forms, groups);
                            var use = catalog.Uses(block).FirstOrDefault(use => use.Kind == BlockKind.Rule);
                            if (use is not null) item = blocks.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, BehaviorFamily.Find(entry.Document, use.Id)?.Rows.FirstOrDefault()?.Id ?? use.Id));
                        }
                        if (item is not null) blocks.SelectedItem = item;
                        editor?.FocusIssue(error, block.Id);
                    });
                    button.HorizontalAlignment = HorizontalAlignment.Left; issueList.Children.Add(button);
                }
        }
        issues.Header = $"Review issues ({issueList.Children.Count})"; reviewIssues.Content = issues.Header;
        reviewIssues.Visibility = issueList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        issues.Visibility = issueList.Children.Count > 0 && issues.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderBlocks(string? selected)
    {
        EnsureProject(); rendering = true;
        try
        {
            blocks.Items.Clear(); if (project is null) return;
            foreach (var entry in project.Entries)
            {
                var catalog = new PresetPresentation(entry.Document, forms, groups, validations.GetValueOrDefault(entry.Id)?.Expanded);
                var seen = new HashSet<string>();
                foreach (var block in entry.Document.Blocks.Where(block => block.Kind == BlockKind.Rule))
                {
                    var family = BehaviorFamily.Find(entry.Document, block.Id);
                    if (family is not null && !seen.Add(family.Definition.Id)) continue;
                    var rows = family?.Rows ?? [block];
                    var errors = rows.SelectMany(row => validations.GetValueOrDefault(entry.Id)?.Errors.GetValueOrDefault(row.Id) ?? []).ToArray();
                    var result = validations.GetValueOrDefault(entry.Id);
                    var status = result is null ? "•" : errors.Length > 0 ? "!" : result.FileErrors.Count > 0 ? "•" : "✓";
                    var rowPanel = new DockPanel { Margin = new Thickness(4, 5, 4, 5), MinHeight = 40 };
                    var mark = EditorControls.Text(status); mark.FontSize = 20; mark.Width = 27; DockPanel.SetDock(mark, Dock.Left); rowPanel.Children.Add(mark);
                    var text = new StackPanel();
                    var label = family?.Definition.Name ?? block.Name;
                    var name = EditorControls.Text(label); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0, 0, 0, 3);
                    name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; text.Children.Add(name);
                    var description = CategoryLabel(entry.Document.Category) + " · " + (rows.Count > 1 ? $"{rows.Count} variations" : catalog.RuleSummary(block, behaviorMode: true));
                    var summary = EditorControls.Text(description); summary.FontSize = 12; summary.LineHeight = 16; summary.MaxHeight = 32; summary.Margin = new Thickness(0);
                    summary.TextTrimming = TextTrimming.CharacterEllipsis; summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); text.Children.Add(summary);
                    rowPanel.Children.Add(text);
                    var item = new ListBoxItem { Content = rowPanel, Tag = block.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        ToolTip = label + Environment.NewLine + description + (errors.Length > 0 ? Environment.NewLine + string.Join(Environment.NewLine, errors) : "") };
                    blocks.Items.Add(item);
                    if (rows.Any(row => row.Id == selected)) blocks.SelectedItem = item;
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
        focus.Visibility = blocks.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        save.Visibility = Ready ? Visibility.Visible : Visibility.Collapsed;
        yaml.Visibility = Ready ? Visibility.Visible : Visibility.Collapsed;
        fileDraft.Visibility = project is not null ? Visibility.Visible : Visibility.Collapsed;
        draftSave.Visibility = project is not null && !Ready ? Visibility.Visible : Visibility.Collapsed;
        var selected = document?.Blocks.FirstOrDefault(block => block.Id == SelectedId);
        var siblings = document?.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray() ?? [];
        var index = Array.IndexOf(siblings, selected);
        var single = selected is not null && (BehaviorFamily.Find(document!, selected.Id)?.Rows.Count ?? 1) == 1;
        blockActions.Visibility = selected is not null ? Visibility.Visible : Visibility.Collapsed;
        blockMenu.Header = "Behavior actions";
        up.Visibility = single && index > 0 ? Visibility.Visible : Visibility.Collapsed;
        down.Visibility = single && index >= 0 && index < siblings.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
        if (validations.Count == 0) issues.Visibility = reviewIssues.Visibility = Visibility.Collapsed;
    }

    private void ShowYaml()
    {
        if (!Ready || project is null) return;
        var entries = project.Entries.ToArray();
        if (entries.Length == 0) return;
        if (entries.Length == 1)
        {
            var result = validations[entries[0].Id];
            new YamlPreviewWindow(filename, result.Source, result.Expanded) { Owner = Host }.ShowDialog(); return;
        }
        var picker = new Window { Title = "Generated files", Owner = Host, Width = 620, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(18) };
        foreach (var entry in entries)
            panel.Children.Add(EditorControls.Button(CategoryLabel(entry.Document.Category) + " · " + entry.FileName, () =>
            {
                var result = validations[entry.Id];
                new YamlPreviewWindow(entry.FileName, result.Source, result.Expanded) { Owner = picker }.ShowDialog();
            }));
        picker.Content = panel; picker.ShowDialog();
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

    private void Save()
    {
        if (!CommitEditor() || !Ready || project is null) return;
        var picker = new OpenFolderDialog { Title = "Choose the destination mod folder" };
        if (exportRoot is not null && Directory.Exists(exportRoot)) picker.InitialDirectory = exportRoot;
        if (picker.ShowDialog(Host) != true) return;
        try
        {
            foreach (var entry in project.Entries)
                if (entry.OpenedPath is not null && File.Exists(entry.OpenedPath) && entry.OpenedHash != Hash(Encoding.UTF8.GetBytes(File.ReadAllText(entry.OpenedPath))))
                    throw new IOException("A source file changed after you opened it: " + entry.OpenedPath);
            var changes = project.Entries.Select(entry => (Entry: entry,
                Output: new PresetOutput(project.GetExportPath(entry, picker.FolderName), validations[entry.Id].Source))).ToArray();
            var outputs = changes.Select(change => change.Output).ToArray();
            var existing = outputs.Where(output => File.Exists(output.Path)).ToArray();
            if (existing.Length > 0 && MessageBox.Show(Host, "Update these files?" + Environment.NewLine + string.Join(Environment.NewLine, existing.Select(output => output.Path)), "Save preset", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            var pending = project.Entries.SelectMany(entry => entry.Document.PendingGroups).GroupBy(group => group.Name, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var first = group.First();
                if (group.Any(value => FormGroupCatalog.Serialize(value) != FormGroupCatalog.Serialize(first))) throw new IOException("Different form groups share the name: " + group.Key);
                return first;
            }).ToArray();
            PresetExport.Save(outputs, pending, picker.FolderName);
            foreach (var change in changes)
            { change.Entry.OpenedPath = change.Output.Path; change.Entry.OpenedHash = Hash(Encoding.UTF8.GetBytes(change.Output.Source)); }
            exportRoot = picker.FolderName; dirty = false; location.Text = "Saved to: " + exportRoot;
            title.Text = filename; state.Text = "✓ Saved to mod";
            _ = Refresh(editingId);
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot save preset", MessageBoxButton.OK, MessageBoxImage.Error); }
    }



    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool ValidFilename(string name) => name.Length > 0 && name != "." && name != ".." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');
}
