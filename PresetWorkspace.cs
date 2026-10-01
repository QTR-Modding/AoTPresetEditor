using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AoTPresetEditor;

public sealed class PresetWorkspace : UserControl
{
    public event Action? Opened;
    private static readonly string[] Categories = ["FOOD", "INGR", "MEDC", "POSN", "ARMO", "WEAP", "BOOK", "SCRL", "SLGM", "MISC"];
    private static readonly (string Name, string Context)[] Contents =
    [
        ("Rule settings", "rule"), ("Stage", "Stage"), ("Stages", "Stages"),
        ("Transformation", "Transformer"), ("Transformations", "Transformers"),
        ("Time modifier", "TimeModulator"), ("Time modifiers", "TimeModulators"),
        ("Magic effect", "MagicEffect"), ("Magic effects", "MagicEffects"), ("Single value", "Value"), ("List of values", "Values"),
        ("Other settings", "Mapping")
    ];
    private readonly TextBlock title = EditorControls.Text("Open or create a preset", true);
    private readonly TextBlock state = EditorControls.Text("Choose a file from the list, or create a new preset.");
    private readonly TextBlock location = EditorControls.Text("");
    private readonly ListBox blocks = new() { BorderThickness = new Thickness(0), Margin = new Thickness(0, 14, 0, 14) };
    private readonly StackPanel addActions = new() { Orientation = Orientation.Horizontal };
    private readonly Button save;
    private readonly Button yaml;
    private readonly Button edit;
    private readonly Button remove;
    private readonly Button up;
    private readonly Button down;
    private PresetDocument? document;
    private PresetValidationResult? validation;
    private IReadOnlyList<FormRow> forms = [];
    private string filename = "New preset";
    private string? exportRoot;
    private string? openedPath;
    private string? openedHash;
    private bool dirty;
    private CancellationTokenSource? checking;
    private Window Host => Window.GetWindow(this);

    public PresetWorkspace()
    {
        var layout = new DockPanel { Margin = new Thickness(14, 0, 0, 0) };
        var header = new StackPanel();
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 18) };
        toolbar.Children.Add(EditorControls.Button("New preset...", New));
        toolbar.Children.Add(EditorControls.Button("Open file...", OpenFile));
        save = EditorControls.Button("Save to mod...", Save);
        yaml = EditorControls.Button("View YAML...", ShowYaml);
        toolbar.Children.Add(save); toolbar.Children.Add(yaml);
        header.Children.Add(toolbar); header.Children.Add(title); header.Children.Add(location); header.Children.Add(state);
        addActions.Children.Add(EditorControls.Button("Add rule", () => Add(BlockKind.Rule)));
        addActions.Children.Add(EditorControls.Button("Add shared settings", () => Add(BlockKind.Shared)));
        addActions.Children.Add(EditorControls.Button("Add template", () => Add(BlockKind.Template)));
        header.Children.Add(addActions);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var footer = new StackPanel();
        var actions = new WrapPanel();
        edit = EditorControls.Button("Open block...", Edit);
        remove = EditorControls.Button("Delete block", Remove);
        up = EditorControls.Button("Move up", () => Move(-1));
        down = EditorControls.Button("Move down", () => Move(1));
        actions.Children.Add(edit); actions.Children.Add(remove); actions.Children.Add(up); actions.Children.Add(down);
        footer.Children.Add(actions);
        var help = EditorControls.Text("Open a block to edit its fields. Rule order controls priority; shared settings and templates keep their own definitions.");
        help.Margin = new Thickness(0, 14, 0, 0); help.Foreground = EditorControls.Muted; footer.Children.Add(help);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        blocks.SelectionChanged += (_, _) => UpdateActions();
        blocks.MouseDoubleClick += (_, _) => Edit();
        layout.Children.Add(blocks); Content = layout;
        UpdateActions();
    }

    public void SetForms(IReadOnlyList<FormRow> catalog) => forms = catalog;

    public bool CanLeave() => !dirty || MessageBox.Show(Host, "Discard unsaved changes to this preset?", "Unsaved preset",
        MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

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
            document = parsed; filename = Path.GetFileNameWithoutExtension(file.FullPath);
            openedPath = file.FullPath; openedHash = Hash(Encoding.UTF8.GetBytes(source));
            exportRoot = null; dirty = false;
            location.Text = "Opened from: " + file.FullPath;
            Opened?.Invoke();
            await Refresh();
            return true;
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot open preset", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    private async void OpenFile()
    {
        var picker = new OpenFileDialog { Title = "Open AoT preset", Filter = "YAML preset (*.yml;*.yaml)|*.yml;*.yaml" };
        if (picker.ShowDialog(Host) != true) return;
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
        filename = options.Value.Name;
        document = PresetDocument.Create(options.Value.Category, options.Value.Kind);
        openedPath = null; openedHash = null;
        exportRoot = null; dirty = true;
        location.Text = "Not saved";
        Opened?.Invoke();
        await Refresh();
    }

    private (string Name, string Category, PresetKind Kind)? ChoosePreset(string dialogTitle, string initialName, string category, PresetKind kind)
    {
        var dialog = new Window { Title = dialogTitle, Owner = Host, Width = 500, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(EditorControls.Text("Preset name")); var name = new TextBox { Text = initialName }; panel.Children.Add(name);
        panel.Children.Add(EditorControls.Text("Item type")); var types = new ComboBox { ItemsSource = Categories, SelectedItem = Categories.Contains(category) ? category : "FOOD" }; panel.Children.Add(types);
        panel.Children.Add(EditorControls.Text("Preset kind"));
        var kinds = new ComboBox { ItemsSource = new[] { PresetKind.Default, PresetKind.Custom, PresetKind.Addon }, SelectedItem = kind == PresetKind.Other ? PresetKind.Custom : kind }; panel.Children.Add(kinds);
        panel.Children.Add(EditorControls.Text("Default supplies a module's fallback rule. Custom defines rules for selected items. Addon adds triggers and filters to items covered by source rules."));
        var error = EditorControls.Text(""); panel.Children.Add(error);
        EditorControls.Footer(panel, EditorControls.Button("Continue", () =>
        {
            if (!ValidFilename(name.Text.Trim())) { error.Text = "Choose a file name without path separators or reserved characters."; return; }
            dialog.DialogResult = true;
        }), EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? (name.Text.Trim(), (string)types.SelectedItem, (PresetKind)kinds.SelectedItem) : null;
    }

    private async void Add(BlockKind kind)
    {
        if (document is null) return;
        var dialog = new Window { Owner = Host, Title = "Add " + KindLabel(kind), Width = 490, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(EditorControls.Text("Name")); var name = new TextBox(); panel.Children.Add(name);
        var type = new ComboBox { ItemsSource = Contents.Select(choice => choice.Name), SelectedIndex = kind == BlockKind.Shared ? 3 : 0 };
        if (kind != BlockKind.Rule) { panel.Children.Add(EditorControls.Text("Contents")); panel.Children.Add(type); }
        var error = EditorControls.Text(""); panel.Children.Add(error);
        EditorControls.Footer(panel, EditorControls.Button("Create", () =>
        {
            if (name.Text.Trim().Length == 0) { error.Text = "Give this block a name."; return; }
            dialog.DialogResult = true;
        }), EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        var context = kind == BlockKind.Rule ? PresetDocument.RuleContext(document.Kind) : Contents[type.SelectedIndex].Context;
        var added = document.Add(kind, name.Text.Trim(), context);
        var anchor = added.Value.Anchor;
        added.Value = ValueEditorWindow.NewValue(context);
        added.Value.Anchor = anchor;
        dirty = true;
        await Refresh(added.Id);
        Edit();
    }

    private async void Edit()
    {
        if (document is null || blocks.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var draft = document.Clone();
        var block = draft.Blocks.First(entry => entry.Id == id);
        if (block.Kind == BlockKind.Shared && block.Value.Anchor.Length == 0) block.Value.Anchor = block.Key;
        var editor = new ValueEditorWindow(draft, forms, block.Value, block.Context, KindLabel(block.Kind),
            block.Kind == BlockKind.Template, blockName: block.Name,
            issues: validation?.Errors.GetValueOrDefault(block.Id)) { Owner = Host };
        if (editor.ShowDialog() != true) return;
        var updated = editor.Document;
        block = updated.Blocks.First(entry => entry.Id == id);
        block.Name = editor.BlockName; block.Value = editor.Value;
        try { updated.RebindTemplateCalls(document); }
        catch (Exception error)
        {
            MessageBox.Show(Host, error.Message, "Cannot apply template changes", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        document = updated;
        dirty = true;
        await Refresh(id);
    }

    private async void Remove()
    {
        if (document is null || blocks.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var block = document.Blocks.First(entry => entry.Id == id);
        if (MessageBox.Show(Host, $"Delete '{block.Name}'? Uses of this block will need a replacement.", "Delete block", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        document.Remove(block); dirty = true; await Refresh();
    }

    private async void Move(int direction)
    {
        if (document is null || blocks.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var index = document.Blocks.FindIndex(entry => entry.Id == id);
        if (index + direction < 0 || index + direction >= document.Blocks.Count) return;
        (document.Blocks[index], document.Blocks[index + direction]) = (document.Blocks[index + direction], document.Blocks[index]);
        dirty = true; await Refresh(id);
    }

    private async Task Refresh(string? selection = null)
    {
        checking?.Cancel();
        if (document is null) return;
        validation = null;
        title.Text = filename + (dirty ? " *" : "");
        state.Text = document.Category + " / " + document.Kind + " - Checking preset...";
        RenderBlocks(selection); UpdateActions();
        using var request = new CancellationTokenSource(); checking = request;
        try
        {
            var result = await PresetValidation.Check(document.Clone(), request.Token);
            request.Token.ThrowIfCancellationRequested();
            validation = result;
            var errorCount = result.FileErrors.Count + result.Errors.Values.Sum(errors => errors.Count);
            state.Text = document.Category + " / " + document.Kind + (result.IsValid ? " - \u2713 Preset structure valid" : $" - \u2717 {errorCount} issues to resolve");
            if (result.FileErrors.Count > 0) state.Text += Environment.NewLine + string.Join(Environment.NewLine, result.FileErrors);
            state.ToolTip = "Checks AoT fields, YAML templates and shared settings. Trigger behavior and form availability still depend on the installed game configuration.";
            RenderBlocks(selection); UpdateActions();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(checking, request)) checking = null; }
    }

    private void RenderBlocks(string? selected)
    {
        blocks.Items.Clear();
        if (document is null) return;
        foreach (var block in document.Blocks)
        {
            var errors = validation?.Errors.GetValueOrDefault(block.Id) ?? [];
            var status = validation is null ? "\u2022" : errors.Count > 0 || validation.FileErrors.Count > 0 ? "\u2717" : "\u2713";
            var row = new DockPanel { Margin = new Thickness(12), MinHeight = 44 };
            var mark = EditorControls.Text(status); mark.FontSize = 22; mark.Width = 36; DockPanel.SetDock(mark, Dock.Left); row.Children.Add(mark);
            var kind = EditorControls.Text(KindLabel(block.Kind)); kind.Foreground = EditorControls.Muted; DockPanel.SetDock(kind, Dock.Right); row.Children.Add(kind);
            row.Children.Add(EditorControls.Text(block.Name));
            var item = new ListBoxItem { Content = row, Tag = block.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "Open to edit" };
            blocks.Items.Add(item);
            if (block.Id == selected) blocks.SelectedItem = item;
        }
    }

    private void UpdateActions()
    {
        addActions.IsEnabled = document is not null;
        ((Button)addActions.Children[0]).IsEnabled = document is not null &&
            !document.Blocks.Any(block => block.Kind == BlockKind.Rule && block.Key is "ownerLists" or "formsLists") &&
            (document.Kind != PresetKind.Default || !document.Blocks.Any(block => block.Kind == BlockKind.Rule));
        save.IsEnabled = yaml.IsEnabled = validation?.IsValid == true;
        edit.IsEnabled = remove.IsEnabled = up.IsEnabled = down.IsEnabled = blocks.SelectedItem is not null;
    }

    private void ShowYaml()
    {
        if (validation?.IsValid != true) return;
        var window = new Window { Owner = Host, Title = filename + " - YAML", Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var tabs = new TabControl();
        foreach (var (label, text) in new[] { ("Preset", validation.Source), ("Expanded rules", validation.Expanded) })
            tabs.Items.Add(new TabItem { Header = label, Content = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top } });
        window.Content = tabs; window.ShowDialog();
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
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = new UTF8Encoding(false).GetBytes(validation.Source);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            exportRoot = picker.FolderName; dirty = false;
            // The selected destination may be the same file through MO2's virtual Data path.
            openedPath = path; openedHash = Hash(Encoding.UTF8.GetBytes(validation.Source));
            location.Text = "Saved to: " + path;
            title.Text = filename;
        }
        catch (Exception error) { MessageBox.Show(Host, error.Message, "Cannot save preset", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool ValidFilename(string name) => name.Length > 0 && name != "." && name != ".." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');
    private static string KindLabel(BlockKind kind) => kind switch { BlockKind.Shared => "Shared settings", BlockKind.Template => "Template", _ => "Rule" };
}
