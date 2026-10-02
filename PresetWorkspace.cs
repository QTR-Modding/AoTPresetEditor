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
    private static readonly (string Name, string Context, string ListContext, string Example)[] Contents =
    [
        ("Rule", "rule", "Rules", "For example, the items and behaviour used by several food rules."),
        ("Stage", "Stage", "Stages", "For example, one stale stage, or a sequence from fresh to rotten."),
        ("Transformation", "Transformer", "Transformers", "For example, one fire-triggered change, or several changes in priority order."),
        ("Time modifier", "TimeModulator", "TimeModulators", "For example, one preservation trigger, or several modifiers in priority order."),
        ("Magic effect", "MagicEffect", "MagicEffects", "For example, one restore-health effect, or a food item's set of effects."),
        ("Value", "Value", "Values", "For example, a shared duration, form, or list of forms."),
        ("Other settings", "Mapping", "Values", "A named collection of settings with custom keys.")
    ];
    private static readonly string[] CategoryNames = ["Food", "Ingredients", "Medicine", "Poisons", "Armor", "Weapons", "Books", "Scrolls", "Soul gems", "Miscellaneous items"];
    private readonly TextBlock title = EditorControls.Text("Open or create a preset", true);
    private readonly TextBlock state = EditorControls.Text("Choose a file from the list, or create a new preset.");
    private readonly TextBlock location = EditorControls.Text("");
    private readonly ListBox blocks = new() { BorderThickness = new Thickness(0), Margin = new Thickness(0, 4, 0, 4) };
    private readonly StackPanel addActions = new();
    private readonly Expander addMenu = new() { Header = "Add block" };
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
    private ValueEditorControl? editor;
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
    private string? openedPath;
    private string? openedHash;
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
        focus.ToolTip = "Hide the rule list to give the selected settings the full width.";
        DockPanel.SetDock(focus, Dock.Right); toolbar.Children.Add(focus);
        reviewIssues = EditorControls.Button("Review issues", () =>
        {
            issues.IsExpanded = !issues.IsExpanded;
            issues.Visibility = issues.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        });
        DockPanel.SetDock(reviewIssues, Dock.Right); toolbar.Children.Add(reviewIssues);
        var fileMenu = new Menu { VerticalAlignment = VerticalAlignment.Center };
        var file = new MenuItem { Header = "File" };
        foreach (var (label, action) in new (string, Action)[] { ("New preset...", New), ("Open file or draft...", OpenFile), ("Installed presets...", () => BrowseRequested?.Invoke()) })
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
        addActions.Children.Add(EditorControls.Button("Add rule", () => Add(BlockKind.Rule)));
        addActions.Children.Add(EditorControls.Button("Add shared settings", () => Add(BlockKind.Shared)));
        addActions.Children.Add(EditorControls.Button("Add template", () => Add(BlockKind.Template)));
        addMenu.Content = addActions;
        foreach (Button button in addActions.Children) button.Margin = new Thickness(0, 3, 0, 3);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var actions = new Menu();
        var remove = new MenuItem { Header = "Delete..." };
        remove.Click += (_, _) => Remove();
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        blockMenu.Items.Add(up); blockMenu.Items.Add(down); blockMenu.Items.Add(remove);
        actions.Items.Add(blockMenu);
        blockActions.Children.Add(actions);
        blocks.ToolTip = "Select a block to edit.";
        issues.Content = new ScrollViewer { Content = issueList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        issues.Collapsed += (_, _) => issues.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(issues, Dock.Bottom); layout.Children.Add(issues);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(sidebarColumn);
        columns.ColumnDefinitions.Add(splitterColumn);
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 });
        DockPanel.SetDock(addMenu, Dock.Top); blockSidebar.Children.Add(addMenu);
        DockPanel.SetDock(blockActions, Dock.Bottom); blockSidebar.Children.Add(blockActions); blockSidebar.Children.Add(blocks);
        columns.Children.Add(blockSidebar);
        splitter.SetResourceReference(BackgroundProperty, "DividerStrokeColorDefaultBrush");
        Grid.SetColumn(splitter, 1); columns.Children.Add(splitter);
        details.Margin = new Thickness(8, 0, 0, 0);
        var editorScroll = new ScrollViewer { Name = "EditorViewport", Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
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
        focus.Content = focused ? "Show rule list" : "Focus editor";
    }

    private void ShowWelcome()
    {
        var welcome = new StackPanel { Margin = new Thickness(15) };
        welcome.Children.Add(EditorControls.Text("Build a preset", true));
        welcome.Children.Add(EditorControls.Text("Start with a rule: choose the items it affects, then define how they change."));
        welcome.Children.Add(EditorControls.Text("Shared settings reuse the same values. Templates reuse a structure with values you supply for each use."));
        if (document is null)
        {
            var start = new WrapPanel();
            start.Children.Add(EditorControls.Button("New preset...", New));
            start.Children.Add(EditorControls.Button("Open file...", OpenFile));
            welcome.Children.Add(start);
        }
        details.Content = welcome;
    }

    public async void SetCatalog(IReadOnlyList<FormRow> catalog, IReadOnlyList<FormGroupDefinition> formGroups)
    {
        forms = catalog;
        groups = formGroups;
        presentation = null;
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

    public async Task<bool> Open(PresetFile file)
    {
        if (!CanLeave()) return false;
        try
        {
            var kind = file.Kind;
            var category = file.Category;
            if (kind == PresetKind.Other || !Categories.Contains(category))
            {
                var options = ChoosePreset("Open preset", Path.GetFileNameWithoutExtension(file.FullPath), category, kind);
                if (options is null) return false;
                category = options.Value.Category; kind = options.Value.Kind;
            }
            var source = await File.ReadAllTextAsync(file.FullPath);
            var parsed = PresetDocument.Parse(source, category, kind);
            ResetEditor();
            document = parsed; filename = Path.GetFileNameWithoutExtension(file.FullPath);
            openedPath = file.FullPath; openedHash = Hash(Encoding.UTF8.GetBytes(source));
            exportRoot = null; draftPath = null; dirty = false;
            location.Text = "Opened from: " + file.FullPath;
            Opened?.Invoke();
            await Refresh();
            return true;
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot open preset", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    private async void OpenFile()
    {
        var picker = new OpenFileDialog { Title = "Open AoT preset or draft", Filter = "Presets and drafts|*.yml;*.yaml;*.aotdraft|YAML presets|*.yml;*.yaml|Editor drafts|*.aotdraft" };
        if (picker.ShowDialog(Host) != true) return;
        if (Path.GetExtension(picker.FileName).Equals(".aotdraft", StringComparison.OrdinalIgnoreCase))
        {
            if (!CanLeave()) return;
            try
            {
                var parsed = PresetDocument.FromDraft(await File.ReadAllTextAsync(picker.FileName));
                ResetEditor(); document = parsed; filename = Path.GetFileNameWithoutExtension(picker.FileName);
                draftPath = picker.FileName; openedPath = null; openedHash = null; exportRoot = null; dirty = false;
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
        await Open(new PresetFile(picker.FileName, relative));
    }

    private async void New()
    {
        if (!CanLeave()) return;
        var options = ChoosePreset("New preset", "New preset", "FOOD", PresetKind.Custom);
        if (options is null) return;
        ResetEditor(); filename = options.Value.Name;
        document = PresetDocument.Create(options.Value.Category, options.Value.Kind);
        openedPath = null; openedHash = null;
        exportRoot = null; draftPath = null; dirty = true;
        location.Text = "Not saved";
        Opened?.Invoke();
        await Refresh();
    }

    private (string Name, string Category, PresetKind Kind)? ChoosePreset(string dialogTitle, string initialName, string category, PresetKind kind)
    {
        var dialog = new Window { Title = dialogTitle, Owner = Host, Width = 500, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(EditorControls.Text("Preset name")); var name = new TextBox { Text = initialName }; panel.Children.Add(name);
        panel.Children.Add(EditorControls.Text("Item type"));
        var types = new ComboBox { ItemsSource = CategoryNames, SelectedIndex = Math.Max(0, Array.IndexOf(Categories, category)) }; panel.Children.Add(types);
        panel.Children.Add(EditorControls.Text("Preset kind"));
        var kinds = new ComboBox { ItemsSource = new[] { PresetKind.Default, PresetKind.Custom, PresetKind.Addon }, SelectedItem = kind == PresetKind.Other ? PresetKind.Custom : kind }; panel.Children.Add(kinds);
        var kindHelp = EditorControls.Text(""); panel.Children.Add(kindHelp);
        void ExplainKind() => kindHelp.Text = (PresetKind)kinds.SelectedItem switch
        {
            PresetKind.Default => "The fallback rule for this item type. Items with a custom rule use that rule instead.",
            PresetKind.Custom => "Rules for items you choose. Define their stages and what they become.",
            _ => "Add transformations, time modifiers, or filters to selected items. AoT uses their custom or default source rule."
        };
        kinds.SelectionChanged += (_, _) => ExplainKind(); ExplainKind();
        var error = EditorControls.Text(""); panel.Children.Add(error);
        EditorControls.Footer(panel, EditorControls.Button("Continue", () =>
        {
            if (!ValidFilename(name.Text.Trim())) { error.Text = "Choose a file name without path separators or reserved characters."; return; }
            dialog.DialogResult = true;
        }), EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? (name.Text.Trim(), Categories[types.SelectedIndex], (PresetKind)kinds.SelectedItem) : null;
    }

    private async void Add(BlockKind kind)
    {
        if (document is null || !CommitEditor()) return;
        var dialog = new Window { Owner = Host, Title = "Add " + KindLabel(kind), Width = 490, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(EditorControls.Text("Name")); var name = new TextBox(); panel.Children.Add(name);
        var type = new ComboBox { ItemsSource = Contents.Select(choice => choice.Name), SelectedIndex = kind == BlockKind.Shared ? 2 : 0 };
        var shape = new ComboBox { ItemsSource = new[] { "One entry", "A list" }, SelectedIndex = 0 };
        var example = EditorControls.Text(Contents[type.SelectedIndex].Example);
        if (kind != BlockKind.Rule)
        {
            panel.Children.Add(EditorControls.Text("What will this contain?")); panel.Children.Add(type);
            panel.Children.Add(shape); panel.Children.Add(example);
            type.SelectionChanged += (_, _) => example.Text = Contents[type.SelectedIndex].Example;
        }
        var error = EditorControls.Text(""); panel.Children.Add(error);
        EditorControls.Footer(panel, EditorControls.Button("Create", () =>
        {
            if (name.Text.Trim().Length == 0) { error.Text = "Give this block a name."; return; }
            dialog.DialogResult = true;
        }), EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        ResetEditor();
        var choice = Contents[type.SelectedIndex];
        var context = kind == BlockKind.Rule ? PresetDocument.RuleContext(document.Kind) : shape.SelectedIndex == 1 ? choice.ListContext : choice.Context;
        var added = document.Add(kind, name.Text.Trim(), context);
        var anchor = added.Value.Anchor;
        added.Value = ValueEditorWindow.NewValue(context);
        added.Value.Anchor = anchor;
        addMenu.IsExpanded = false;
        dirty = true;
        await Refresh(added.Id);
    }

    private void ResetEditor()
    {
        editTimer.Stop(); checking?.Cancel();
        editor = null; editingId = null; editorDirty = false;
        validation = null; details.Content = null;
    }

    private async void SelectBlock()
    {
        var id = SelectedId;
        if (id == editingId && editor is not null) return;
        var needsValidation = editorDirty;
        if (!CommitEditor()) { RenderBlocks(editingId); return; }
        if (document is null || id is null) { editor = null; editingId = null; ShowWelcome(); UpdateActions(); return; }
        var draft = document.Clone();
        var block = draft.Blocks.First(entry => entry.Id == id);
        editor = new ValueEditorControl(draft, forms, block.Value, block.Context, KindLabel(block.Kind),
            block.Kind == BlockKind.Template, blockName: block.Name,
            issues: validation?.Errors.GetValueOrDefault(block.Id), groups: groups);
        editingId = id; editorDirty = false;
        editor.Changed += EditorChanged;
        details.Content = editor;
        if (presentation is not null) editor.UpdatePresentation(presentation, presentation.EffectiveValue(document.Blocks.First(entry => entry.Id == id)));
        UpdateActions();
        if (needsValidation) await Refresh(id);
    }

    private void EditorChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, editor)) return;
        editorDirty = true; dirty = true; validation = null; checking?.Cancel();
        title.Text = filename + " *"; state.Text = "Unsaved changes · Checking after edit...";
        UpdateActions(); editTimer.Stop(); editTimer.Start();
    }

    private bool CommitEditor()
    {
        editTimer.Stop();
        if (!editorDirty || editor is null || editingId is null) return true;
        editor.Capture();
        var updated = editor.Document;
        var previous = updated.Clone();
        var block = updated.Blocks.First(entry => entry.Id == editingId);
        block.Name = editor.BlockName; block.Value = editor.Value.Clone();
        try { updated.RebindTemplateCalls(previous); }
        catch (Exception error)
        {
            MessageBox.Show(Host, error.Message, "Cannot apply template changes", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        document = updated;
        editorDirty = false; dirty = true;
        return true;
    }

    private async void Remove()
    {
        if (document is null || !CommitEditor() || blocks.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var block = document.Blocks.First(entry => entry.Id == id);
        if (MessageBox.Show(Host, $"Delete '{block.Name}'? Uses of this block will need a replacement.", "Delete block", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        ResetEditor(); document.Remove(block); dirty = true; await Refresh();
    }

    private async void Move(int direction)
    {
        if (document is null || !CommitEditor() || blocks.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var index = document.Blocks.FindIndex(entry => entry.Id == id);
        var next = index + direction;
        while (next >= 0 && next < document.Blocks.Count && document.Blocks[next].Kind != document.Blocks[index].Kind) next += direction;
        if (next < 0 || next >= document.Blocks.Count) return;
        ResetEditor();
        (document.Blocks[index], document.Blocks[next]) = (document.Blocks[next], document.Blocks[index]);
        dirty = true; await Refresh(id);
    }

    private async Task Refresh(string? selection = null)
    {
        checking?.Cancel();
        if (document is null) return;
        validation = null;
        title.Text = filename + (dirty ? " *" : "");
        state.Text = document.Category + " / " + document.Kind + " - Checking preset...";
        UpdatePresentation();
        RenderBlocks(selection); UpdateActions();
        SelectBlock();
        using var request = new CancellationTokenSource(); checking = request;
        try
        {
            var result = await PresetValidation.Check(document.Clone(), request.Token, presentation);
            request.Token.ThrowIfCancellationRequested();
            validation = result;
            UpdatePresentation(result.Expanded);
            var errorCount = result.FileErrors.Count + result.Errors.Values.Sum(errors => errors.Count);
            state.Text = document.Category + " / " + document.Kind + (result.IsValid ? " · ✓ Ready to save to a mod" : $" · {errorCount} {(errorCount == 1 ? "issue" : "issues")}") + (dirty ? " · Unsaved changes" : "");
            state.ToolTip = "Checks fields, templates and shared settings. Test the exported preset in game to verify its behaviour.";
            var selectedNow = SelectedId;
            RenderBlocks(selectedNow); UpdateActions();
            RenderIssues();
            if (editor is not null && editingId is not null && presentation is not null)
            {
                var block = document.Blocks.First(entry => entry.Id == editingId);
                editor.UpdatePresentation(presentation, presentation.EffectiveValue(block));
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
        if (validation is null || document is null) return;
        foreach (var error in validation.FileErrors) issueList.Children.Add(EditorControls.Text(error));
        foreach (var block in document.Blocks)
            foreach (var error in validation.Errors.GetValueOrDefault(block.Id) ?? [])
            {
                var button = EditorControls.Button(block.Name + ": " + error, () =>
                {
                    var item = blocks.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, block.Id));
                    blocks.SelectedItem = item;
                    editor?.FocusIssue(error);
                });
                button.HorizontalAlignment = HorizontalAlignment.Left;
                issueList.Children.Add(button);
            }
        issues.Header = $"Review issues ({issueList.Children.Count})";
        reviewIssues.Content = issues.Header;
        reviewIssues.Visibility = issueList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        issues.Visibility = issueList.Children.Count > 0 && issues.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderBlocks(string? selected)
    {
        rendering = true;
        try
        {
            blocks.Items.Clear();
            if (document is null) return;
            foreach (var kind in new[] { BlockKind.Rule, BlockKind.Shared, BlockKind.Template })
            {
                var entries = document.Blocks.Where(block => block.Kind == kind).ToArray();
                if (entries.Length == 0) continue;
                var heading = EditorControls.Text((kind == BlockKind.Rule ? "Rules" : kind == BlockKind.Template ? "Templates" : "Shared settings") + $" ({entries.Length})");
                heading.FontWeight = FontWeights.SemiBold;
                heading.Margin = new Thickness(0);
                blocks.Items.Add(new ListBoxItem { Content = heading, IsHitTestVisible = false, Focusable = false, Margin = new Thickness(0, 6, 0, 0) });
                foreach (var block in entries)
                {
                    var errors = validation?.Errors.GetValueOrDefault(block.Id) ?? [];
                    var incomplete = errors.Count > 0 && errors.All(error => error.Contains("required", StringComparison.OrdinalIgnoreCase) || error.Contains("unset", StringComparison.OrdinalIgnoreCase));
                    var status = validation is null ? "•" : errors.Count > 0 ? incomplete ? "○" : "!" : validation.FileErrors.Count > 0 ? "•" : "✓";
                    var row = new DockPanel { Margin = new Thickness(4, 5, 4, 5), MinHeight = 40 };
                    var mark = EditorControls.Text(status); mark.FontSize = 20; mark.Width = 27; DockPanel.SetDock(mark, Dock.Left); row.Children.Add(mark);
                    var text = new StackPanel();
                    var name = EditorControls.Text(block.Name); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0, 0, 0, 3);
                    name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; text.Children.Add(name);
                    var description = presentation?.RuleSummary(block) ?? "";
                    if (block.Kind != BlockKind.Rule && presentation is not null)
                    {
                        var uses = presentation.Uses(block);
                        description += Environment.NewLine + (uses.Count == 0 ? "No uses yet" : "Used by " + string.Join(", ", uses.Take(3).Select(use => use.Name)) + (uses.Count > 3 ? $" +{uses.Count - 3}" : ""));
                    }
                    var summary = EditorControls.Text(description);
                    summary.FontSize = 12; summary.LineHeight = 16; summary.MaxHeight = 32; summary.Margin = new Thickness(0);
                    summary.TextTrimming = TextTrimming.CharacterEllipsis;
                    summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); text.Children.Add(summary);
                    row.Children.Add(text);
                    var item = new ListBoxItem { Content = row, Tag = block.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        ToolTip = block.Name + Environment.NewLine + description + (errors.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, errors) : "") };
                    blocks.Items.Add(item);
                    if (block.Id == selected) blocks.SelectedItem = item;
                }
            }
            if (blocks.SelectedItem is null) blocks.SelectedItem = blocks.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag is string);
        }
        finally { rendering = false; }
    }

    private void UpdateActions()
    {
        addMenu.Visibility = addActions.Visibility = document is not null ? Visibility.Visible : Visibility.Collapsed;
        if (document?.Blocks.Count == 0) addMenu.IsExpanded = true;
        if (focused && document?.Blocks.Count is null or 0) ToggleFocusMode();
        focus.Visibility = document?.Blocks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var canAddRule = document is not null &&
            !document.Blocks.Any(block => block.Kind == BlockKind.Rule && block.Key is "ownerLists" or "formsLists") &&
            (document.Kind != PresetKind.Default || !document.Blocks.Any(block => block.Kind == BlockKind.Rule));
        ((Button)addActions.Children[0]).Visibility = canAddRule ? Visibility.Visible : Visibility.Collapsed;
        save.Visibility = yaml.Visibility = validation?.IsValid == true ? Visibility.Visible : Visibility.Collapsed;
        fileDraft.Visibility = document is not null ? Visibility.Visible : Visibility.Collapsed;
        draftSave.Visibility = document is not null && validation?.IsValid != true ? Visibility.Visible : Visibility.Collapsed;
        var selected = document?.Blocks.FirstOrDefault(block => block.Id == SelectedId);
        var siblings = document?.Blocks.Where(block => block.Kind == selected?.Kind).ToArray() ?? [];
        var selectedIndex = Array.IndexOf(siblings, selected);
        blockActions.Visibility = selected is not null ? Visibility.Visible : Visibility.Collapsed;
        blockMenu.Header = selected is null ? "Block actions" : KindLabel(selected.Kind) + " actions";
        up.Visibility = selectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        down.Visibility = selectedIndex >= 0 && selectedIndex < siblings.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
        if (validation is null) issues.Visibility = reviewIssues.Visibility = Visibility.Collapsed;
    }

    private void ShowYaml()
    {
        if (validation?.IsValid != true) return;
        new YamlPreviewWindow(filename, validation.Source, validation.Expanded) { Owner = Host }.ShowDialog();
    }

    private bool SaveDraft()
    {
        if (document is null || !CommitEditor()) return false;
        var picker = new SaveFileDialog { Title = "Save editor draft", Filter = "AoT editor draft (*.aotdraft)|*.aotdraft",
            DefaultExt = ".aotdraft", FileName = draftPath ?? filename + ".aotdraft", AddExtension = true };
        if (picker.ShowDialog(Host) != true) return false;
        try
        {
            PresetExport.Save(picker.FileName, document.ToDraft(), [], Path.GetDirectoryName(picker.FileName)!);
            draftPath = picker.FileName; dirty = false; title.Text = filename;
            location.Text = "Draft saved: " + draftPath + " · Installed preset unchanged";
            _ = Refresh(SelectedId);
            return true;
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot save draft", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    private void Save()
    {
        if (document is null || validation?.IsValid != true) return;
        var picker = new OpenFolderDialog { Title = "Choose the destination mod folder (its SKSE folder will be created inside)" };
        if (exportRoot is not null && Directory.Exists(exportRoot)) picker.InitialDirectory = exportRoot;
        if (picker.ShowDialog(Host) != true) return;
        var relative = Path.Combine("SKSE", "Plugins", "AlchemyOfTime", document.Category);
        relative = document.Kind switch
        {
            PresetKind.Default => Path.Combine(relative, $"AoT_default{document.Category}.yml"),
            PresetKind.Custom => Path.Combine(relative, "custom", filename + ".yml"),
            _ => Path.Combine(relative, "addon", filename + ".yml")
        };
        var path = Path.GetFullPath(Path.Combine(picker.FolderName, relative));
        try
        {
            if (openedPath is not null && File.Exists(openedPath) &&
                openedHash != Hash(Encoding.UTF8.GetBytes(File.ReadAllText(openedPath))))
                throw new IOException("The source preset changed on disk after you opened it. Reopen it before saving your changes.");
            if (File.Exists(path))
            {
                if (MessageBox.Show(Host, "Replace this preset?" + Environment.NewLine + path, "Save preset", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            }
            PresetExport.Save(path, validation.Source, document.PendingGroups, picker.FolderName);
            exportRoot = picker.FolderName; dirty = false;
            // The selected destination may be the same file through MO2's virtual Data path.
            openedPath = path; openedHash = Hash(Encoding.UTF8.GetBytes(validation.Source));
            location.Text = "Saved to: " + path;
            title.Text = filename;
            state.Text = document.Category + " / " + document.Kind + " · ✓ Saved to mod";
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot save preset", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool ValidFilename(string name) => name.Length > 0 && name != "." && name != ".." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');
    private static string KindLabel(BlockKind kind) => kind switch { BlockKind.Shared => "Shared settings", BlockKind.Template => "Template", _ => "Rule" };
}
