using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using YamlDotNet.Core;

namespace AoTPresetEditor;

public sealed class BehaviorEditorControl : UserControl
{
    private const double VariationTableHeight = 210;
    private readonly IReadOnlyList<FormRow> forms;
    private readonly IReadOnlyList<FormGroupDefinition> groups;
    private readonly TextBox name = new();
    private readonly ContentControl commonContent = new();
    private readonly TabControl tabs = new();
    private readonly ScrollViewer commonScroll;
    private readonly ScrollViewer variationScroll;
    private readonly DataGrid table = new();
    private readonly ContentControl cellSection = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock rowStatus = EditorControls.Text("");
    private readonly TextBlock cellFeedback = EditorControls.Text("");
    private readonly List<VariationColumn> columns = [];
    private readonly List<VariationRow> rows = [];
    private PresetDocument document;
    private PresetPresentation presentation;
    private ValueEditorControl commonEditor = null!;
    private ValueEditorControl? cellEditor;
    private string commonId = "";
    private string? cellRowId;
    private string? cellParameter;
    private bool cellDirty;
    private bool variationsActive;
    private bool refreshing;
    private Button addRow = null!;
    private Button addOptions = null!;
    private readonly ContextMenu addMenu = new();
    private readonly MenuItem duplicate = new() { Header = "Duplicate selected rows" };
    private readonly MenuItem remove = new() { Header = "Remove selected rows" };
    private readonly MenuItem paste = new() { Header = "Paste cells", InputGestureText = "Ctrl+V" };
    private readonly MenuItem fill = new() { Header = "Fill selected rows with this value" };

    private sealed record VariationColumn(string Parameter, string Label, PresetField? Field);

    private sealed class VariationRow(string id, int number, string[] values) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public int Number { get; } = number;
        public string this[int index] => values[index];
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(int index, string value)
        {
            values[index] = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public PresetDocument Document => document;
    public string BlockName => name.Text.Trim();
    public string RuleId { get; private set; }
    public event EventHandler? Changed;
    public event Func<PresetDocument, string, PresetKind, PresetDocument?>? KindChangeRequested;

    public BehaviorEditorControl(PresetDocument document, IReadOnlyList<FormRow> forms, string ruleId,
        IReadOnlyList<FormGroupDefinition>? groups = null)
    {
        this.document = document.Clone(); this.forms = forms; this.groups = groups ?? []; RuleId = ruleId;
        presentation = new PresetPresentation(this.document, forms, this.groups);
        var family = BehaviorFamily.Find(this.document, RuleId);
        name.Text = (family?.Definition ?? Rule()).Name;
        AutomationProperties.SetName(name, "Behavior name");
        name.TextChanged += (_, _) => { if (!refreshing) NotifyChanged(); };
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var caption = EditorControls.Text("Behavior name");
        caption.VerticalAlignment = VerticalAlignment.Center; caption.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(caption, Dock.Left); heading.Children.Add(caption); heading.Children.Add(name);
        var layout = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        layout.Children.Add(tabs); Content = layout;
        BuildVariationView();
        commonScroll = new ScrollViewer { Content = commonContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        variationScroll = new ScrollViewer { Content = variationView, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        tabs.Items.Add(new TabItem { Header = "Settings", Content = commonScroll });
        tabs.SelectionChanged += (_, args) =>
        {
            if (refreshing || !ReferenceEquals(args.Source, tabs)) return;
            Capture(); variationsActive = tabs.SelectedIndex == 1;
            if (variationsActive)
            {
                var rowId = (table.CurrentItem as VariationRow)?.Id ?? RuleId;
                var parameter = SelectedColumn?.Parameter;
                RefreshRows(); SelectCell(rowId, parameter);
            }
            else { ClearCell(); BuildCommonEditor(); }
            UpdateActions();
        };
        Rebuild(false);
    }

    private PresetBlock Rule() => document.Blocks.First(block => block.Id == RuleId);

    public void Capture()
    {
        if (variationsActive) CaptureCell(); else CaptureCommon();
        var family = BehaviorFamily.Find(document, RuleId);
        (family?.Definition ?? Rule()).Name = BlockName;
    }

    private void CaptureCommon()
    {
        if (commonEditor is null) return;
        commonEditor.Capture();
        document = commonEditor.Document;
        if (BehaviorFamily.Find(document, RuleId) is not null) BehaviorFamily.SetCommon(document, RuleId, commonEditor.Value);
        else document.Blocks.First(item => item.Id == commonId).Value = commonEditor.Value.Clone();
    }

    private void CaptureCell()
    {
        if (cellEditor is null || !cellDirty || cellRowId is null || cellParameter is null) return;
        cellEditor.Capture(); document = cellEditor.Document;
        BehaviorFamily.SetArgument(document, cellRowId, cellParameter, cellEditor.Value);
    }

    public void UpdatePresentation(PresetPresentation updated, PresetValue? effectiveRoot = null)
    {
        presentation = updated;
        commonEditor.UpdateSourceForms(CommonSources());
        commonEditor.UpdatePresentation(updated, BehaviorFamily.Find(document, RuleId)?.IsTemplate == true ? null : effectiveRoot);
        if (cellRowId is not null) cellEditor?.UpdateSourceForms(Sources(cellRowId));
        cellEditor?.UpdatePresentation(updated);
        RefreshSummaries();
    }

    public bool FocusIssue(string message, string? rowId = null)
    {
        if (message.StartsWith("Give this", StringComparison.OrdinalIgnoreCase) && message.Contains("name", StringComparison.OrdinalIgnoreCase))
        { name.Focus(); return true; }
        if (BehaviorFamily.Find(document, RuleId) is { IsTemplate: true } family)
        {
            if (family.Rows.Count == 0) { tabs.SelectedIndex = 1; table.Focus(); return true; }
            var row = family.Rows.FirstOrDefault(candidate => candidate.Id == rowId) ?? family.Rows[0];
            var path = ValueEditorControl.IssuePath(presentation.EffectiveValue(row.Id) ?? family.Definition.Value, family.Definition.Context, document.Kind, message);
            var variable = IssueVariable(document, family, row.Id, message, path);
            var column = columns.FirstOrDefault(candidate => candidate.Parameter == variable.Parameter);
            if (column is not null)
            {
                tabs.SelectedIndex = 1; SelectCell(row.Id, column.Parameter);
                cellSection.BringIntoView();
                cellEditor?.FocusPath(path.Skip(variable.PathLength).ToArray());
                return true;
            }
        }
        tabs.SelectedIndex = 0;
        return commonEditor.FocusIssue(message);
    }

    public static string IssueLocation(PresetDocument document, string blockId, string message, PresetValue? effectiveValue = null)
    {
        var block = document.Blocks.FirstOrDefault(candidate => candidate.Id == blockId);
        if (block is null) return "Preset";
        var family = BehaviorFamily.Find(document, blockId);
        var definition = family?.Definition ?? block;
        var labels = new List<string> { definition.Name };
        if (message.StartsWith("Give this", StringComparison.OrdinalIgnoreCase) && message.Contains("name", StringComparison.OrdinalIgnoreCase))
            labels.Add("Behavior name");
        else if (family is { IsTemplate: true, Rows.Count: 0 }) labels.Add("Variables");
        else
        {
            var path = ValueEditorControl.IssuePath(effectiveValue ?? definition.Value, definition.Context, document.Kind, message);
            var value = effectiveValue ?? definition.Value; var context = definition.Context;
            var row = family?.Rows.FirstOrDefault(candidate => candidate.Id == blockId) ?? family?.Rows.FirstOrDefault();
            if (family is { IsTemplate: true } && row is not null && IssueVariable(document, family, row.Id, message, path) is { Parameter: { } parameter } variable)
            {
                labels.Add("Variables"); labels.Add("Row " + (family.Rows.ToList().IndexOf(row) + 1)); labels.Add(parameter);
                value = BehaviorFamily.GetArgument(document, row.Id, parameter);
                context = PresetSchema.GetParameterField(definition, parameter, document)?.ChildContext ?? "";
                path = path.Skip(variable.PathLength).ToArray();
            }
            if (family is not null && path.FirstOrDefault() == "finalFormEditorID" && PresetSchema.NormalizeContext(context) == "rule") labels.Add("Stages");
            labels.AddRange(IssuePathLabels(value, context, document.Kind, path));
        }
        return string.Join(" → ", labels);
    }

    private static (string? Parameter, int PathLength) IssueVariable(PresetDocument document, BehaviorFamily family,
        string rowId, string message, IReadOnlyList<string> path)
    {
        if (message.StartsWith("Fill the missing values", StringComparison.OrdinalIgnoreCase))
            return (family.Parameters.FirstOrDefault(parameter => BehaviorFamily.GetArgument(document, rowId, parameter).IsUnsetArgument), path.Count);
        var variable = ValueEditorControl.IssueParameter(document, family.Definition.Value, path);
        return variable.Parameter is not null && family.Parameters.Contains(variable.Parameter) ? variable : (null, 0);
    }

    private static IEnumerable<string> IssuePathLabels(PresetValue? value, string context, PresetKind kind, IReadOnlyList<string> path)
    {
        foreach (var part in path)
        {
            if (int.TryParse(part, out var index))
            {
                value = value is { Kind: ValueKind.Sequence } && index < value.Items.Count ? value.Items[index] : null;
                yield return PresetSchema.NormalizeContext(context) switch
                {
                    "stage" => "Stage " + (value?.Get("no")?.Text ?? "?"),
                    "transformer" => "Transformation " + (index + 1),
                    "modulator" => "Time modifier " + (index + 1),
                    "effect" => "Magic effect " + (index + 1),
                    _ => "Entry " + (index + 1)
                };
                continue;
            }
            var descriptor = PresetSchema.GetFields(context, kind).FirstOrDefault(candidate => candidate.Key == part);
            if (part is "containers" or "allowed_stages") yield return "Filters";
            yield return descriptor?.Label ?? part;
            value = value?.Get(part); context = descriptor?.ChildContext ?? "";
        }
    }

    private void BuildCommonEditor()
    {
        var family = BehaviorFamily.Find(document, RuleId);
        var definition = family?.Definition ?? Rule(); commonId = definition.Id;
        commonEditor = new ValueEditorControl(document, forms, definition.Value, definition.Context,
            "Settings", parametersAllowed: family?.IsTemplate == true,
            groups: groups, behaviorMode: true, allowVariations: family is not null, sourceForms: CommonSources(), showHeading: false,
            editSections: family is null ? null : ChangeSection, changeAppliesTo: family is null ? null : ChangeAppliesTo);
        document = commonEditor.Document;
        commonEditor.Changed += (_, _) => { if (!refreshing) NotifyChanged(); };
        commonEditor.VariableChangeRequested += ChangeVariable;
        var panel = new StackPanel();
        if (family?.IsTemplate == true)
        {
            var otherUses = new PresetPresentation(document, forms, groups).Uses(family.Definition)
                .Count(block => block.Kind == BlockKind.Rule && !family.Rows.Any(row => row.Id == block.Id));
            if (otherUses > 0) panel.Children.Add(EditorControls.Text($"These settings also affect {otherUses} other behaviors."));
        }
        panel.Children.Add(commonEditor); commonContent.Content = panel;
    }

    private bool ChangeKind(PresetKind kind)
    {
        if (document.Kind == kind) return true;
        PresetDocument? changed;
        if (KindChangeRequested is not null) changed = KindChangeRequested(document, RuleId, kind);
        else
        {
            var project = new PresetProject(); project.Add(document, "Behavior");
            changed = project.ChangeBehaviorKind(RuleId, kind).Document;
        }
        if (changed is null) return false;
        document = changed; return true;
    }

    private void ChangeAppliesTo(bool all)
    {
        Capture();
        try
        {
            if (all)
            {
                if (document.Kind == PresetKind.Default || BehaviorFamily.GetSetting(document, RuleId, "stages") is null) return;
                if (!ChangeKind(PresetKind.Default)) return;
            }
            else
            {
                if (document.Kind != PresetKind.Default) return;
                var kind = BehaviorFamily.GetSetting(document, RuleId, "stages") is null ? PresetKind.Addon : PresetKind.Custom;
                var field = PresetSchema.GetFields("rule", kind).Single(field => field.Key is "owners" or "forms");
                var picker = new FormPickerWindow(forms, multiple: true,
                    selection: FormSelection.ForField(field, document.Category), purpose: "Choose affected items") { Owner = Window.GetWindow(this) };
                if (picker.ShowDialog() != true || picker.SelectedForms.Count == 0) return;
                if (!ChangeKind(kind)) return;
                var value = BehaviorFamily.Find(document, RuleId)!.Definition.Value.Clone();
                var selected = PresetValue.List();
                selected.Items.AddRange(picker.SelectedForms.Select(form => PresetValue.Scalar(form.Identifier)));
                value.Set(field.Key, selected);
                BehaviorFamily.SetCommon(document, RuleId, value);
            }
            RuleId = BehaviorFamily.Normalize(document, RuleId);
            Rebuild(false); NotifyChanged();
        }
        catch (Exception error) { ShowError(error, "Cannot change affected items"); }
    }

    private void EnsureStages(bool overrideInherited = false)
    {
        var family = BehaviorFamily.Find(document, RuleId)!;
        var value = family.Definition.Value.Clone();
        if (BehaviorFamily.GetSetting(document, RuleId, "stages") is { } existing)
        {
            if (!overrideInherited || value.Get("stages") is not null) return;
            value.Set("stages", existing.Clone());
        }
        else
        {
            var stages = PresetValue.List(); stages.Items.Add(ValueEditorControl.NewStage(0)); value.Set("stages", stages);
            value.Set("finalFormEditorID", PresetValue.Scalar(""));
        }
        BehaviorFamily.SetCommon(document, RuleId, value);
    }

    private void ChangeSection(string key, bool add)
    {
        Capture();
        try
        {
            var label = key switch { "stages" => "Stages", "transformers" => "Transformations", _ => "Time modifiers" };
            if (!add && MessageBox.Show(Window.GetWindow(this), key == "stages"
                ? "Remove these stages and their final item? Transformations and time modifiers will be kept." +
                    (document.Kind == PresetKind.Default ? " You will need to choose which items they affect." : "")
                : "Remove all " + label.ToLowerInvariant() + " from this behavior?", "Remove " + label.ToLowerInvariant(),
                MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            if (key == "stages")
            {
                if (!ChangeKind(add ? document.Kind == PresetKind.Default ? PresetKind.Default : PresetKind.Custom : PresetKind.Addon)) return;
                if (add) EnsureStages(overrideInherited: true);
            }
            else
            {
                var family = BehaviorFamily.Find(document, RuleId)!;
                var value = family.Definition.Value.Clone();
                if (add)
                {
                    var setting = BehaviorFamily.GetSetting(document, RuleId, key)?.Clone() ?? PresetValue.List();
                    if (setting.Kind == ValueKind.Sequence && setting.Items.Count == 0) setting.Items.Add(PresetValue.Map());
                    value.Set(key, setting);
                }
                else
                {
                    value.Remove(key);
                    var without = document.Clone(); BehaviorFamily.SetCommon(without, RuleId, value);
                    if (BehaviorFamily.GetSetting(without, RuleId, key) is not null) value.Set(key, PresetValue.List());
                }
                BehaviorFamily.SetCommon(document, RuleId, value);
            }
            RuleId = BehaviorFamily.Normalize(document, RuleId);
            Rebuild(false); NotifyChanged();
            if (add) commonEditor.FocusIssue(label + " 1");
        }
        catch (Exception error) { ShowError(error, "Cannot change behavior settings"); }
    }

    private void Rebuild(bool showVariations, string? selectRow = null, string? selectParameter = null)
    {
        refreshing = true; ClearCell(); BuildCommonEditor();
        var family = BehaviorFamily.Find(document, RuleId);
        if (family is { IsTemplate: true, Parameters.Count: > 0 })
        {
            ((TabItem)tabs.Items[0]).Header = "Settings";
            if (tabs.Items.Count == 1) tabs.Items.Add(new TabItem { Header = "Variables", Content = variationScroll });
            variationsActive = showVariations; tabs.SelectedIndex = showVariations ? 1 : 0;
            RefreshRows();
        }
        else
        {
            ((TabItem)tabs.Items[0]).Header = "Settings";
            variationsActive = false; tabs.SelectedIndex = 0;
            if (tabs.Items.Count > 1) { ((TabItem)tabs.Items[1]).Content = null; tabs.Items.RemoveAt(1); }
            RefreshRows();
        }
        refreshing = false;
        if (variationsActive) SelectCell(selectRow ?? RuleId, selectParameter ?? columns.FirstOrDefault()?.Parameter);
        UpdateActions();
    }

    private bool ChangeVariable(IReadOnlyList<string> path, PresetField field, bool enabled)
    {
        Capture();
        try
        {
            if (enabled)
            {
                var parameter = ChooseVariableName(field);
                if (parameter is null) return false;
                BehaviorFamily.MakeVariable(document, RuleId, path, parameter,
                    ValueEditorControl.NewValue(field.ChildContext, field));
                Rebuild(false); NotifyChanged();
            }
            else
            {
                var family = BehaviorFamily.Find(document, RuleId)!;
                var value = BehaviorFamily.GetValue(family.Definition.Value, path);
                if (value is not { Kind: ValueKind.Scalar } || !value.Text.StartsWith('$')) return false;
                RuleId = BehaviorFamily.ClearVariable(document, RuleId, value.Text[1..]);
                Rebuild(false); NotifyChanged();
            }
            return true;
        }
        catch (Exception error) { ShowError(error, "Cannot change variable"); return false; }
    }

    private string? ChooseVariableName(PresetField field)
    {
        var dialog = new Window { Owner = Window.GetWindow(this), Title = "Name variable", Width = 420,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(EditorControls.Text($"Variable name for {field.Label}"));
        var input = new TextBox(); AutomationProperties.SetName(input, "Variable name"); panel.Children.Add(input);
        var error = EditorControls.Text(""); error.Visibility = Visibility.Collapsed; panel.Children.Add(error);
        EditorControls.Footer(panel, EditorControls.Button("Create", () =>
        {
            var candidate = input.Text.Trim();
            error.Text = !TemplateCall.IsName(candidate) ? "Use letters, numbers, underscores, or hyphens."
                : BehaviorFamily.Find(document, RuleId)?.Parameters.Contains(candidate) == true ? "That variable name is already in use." : "";
            error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (error.Text.Length == 0) dialog.DialogResult = true;
        }, isDefault: true), EditorControls.Button("Cancel", () => dialog.DialogResult = false, isCancel: true));
        dialog.Content = panel; dialog.Loaded += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private readonly StackPanel variationView = new();

    private void BuildVariationView()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        addRow = EditorControls.Button("Add row", AddRow); actions.Children.Add(addRow);
        addRow.Margin = new Thickness(0); addRow.ToolTip = "Add an empty row.";
        addOptions = EditorControls.Button("▾", () => { addMenu.PlacementTarget = addOptions; addMenu.IsOpen = true; });
        AutomationProperties.SetName(addOptions, "Add rows from forms");
        addOptions.Margin = new Thickness(1, 0, 0, 0); addOptions.ToolTip = "Add one row per selected form.";
        actions.Children.Add(addOptions); variationView.Children.Add(actions);
        duplicate.Click += (_, _) => DuplicateRows(); remove.Click += (_, _) => RemoveRows();
        paste.Click += (_, _) => PasteRows(); fill.Click += (_, _) => FillSelected();
        paste.ToolTip = "Paste tab-separated values, starting at the selected cell. Each line fills a row.";
        table.ContextMenu = new ContextMenu();
        foreach (var item in new[] { duplicate, remove, paste, fill }) table.ContextMenu.Items.Add(item);
        table.ContextMenuOpening += (_, _) => UpdateActions();
        table.AutoGenerateColumns = false; table.IsReadOnly = true; table.CanUserAddRows = table.CanUserDeleteRows = false;
        table.CanUserReorderColumns = false; table.CanUserSortColumns = false;
        table.SelectionMode = DataGridSelectionMode.Extended; table.SelectionUnit = DataGridSelectionUnit.FullRow;
        table.HeadersVisibility = DataGridHeadersVisibility.Column; table.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        table.MaxHeight = VariationTableHeight; table.MinHeight = 100;
        table.CurrentCellChanged += (_, _) => { if (!refreshing) OpenCell(); };
        table.SelectionChanged += (_, _) => { if (!refreshing) UpdateActions(); };
        table.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) { PasteRows(); args.Handled = true; }
            else if (args.Key is Key.Delete or Key.Back && Keyboard.Modifiers == ModifierKeys.None)
            { RemoveRows(); args.Handled = true; }
        };
        AutomationProperties.SetName(table, "Variable values; select a cell to edit it below");
        variationView.Children.Add(table); rowStatus.FontSize = 12; rowStatus.Margin = new Thickness(0, 5, 0, 8);
        variationView.Children.Add(rowStatus);
        variationView.Children.Add(cellSection);
    }

    private void RefreshRows()
    {
        var wasRefreshing = refreshing; refreshing = true;
        var family = BehaviorFamily.Find(document, RuleId); columns.Clear(); rows.Clear(); table.Columns.Clear();
        table.Columns.Add(new DataGridTextColumn { Header = "Row", Binding = new Binding(nameof(VariationRow.Number)), Width = DataGridLength.Auto });
        if (family is not null)
        {
            for (var index = 0; index < family.Parameters.Count; index++)
            {
                var parameter = family.Parameters[index];
                var label = parameter;
                var field = PresetSchema.GetParameterField(family.Definition, parameter, document);
                columns.Add(new(parameter, label, field));
                table.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding($"[{index}]"), MinWidth = 130,
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            }
            for (var index = 0; index < family.Rows.Count; index++)
            {
                var row = family.Rows[index];
                rows.Add(new(row.Id, index + 1, columns.Select(column => CellSummary(row.Id, column)).ToArray()));
            }
        }
        table.ItemsSource = null; table.ItemsSource = rows;
        addMenu.Items.Clear();
        foreach (var column in columns.Where(column => column.Field?.Kind is FieldKind.Form or FieldKind.FormList))
        {
            var item = new MenuItem { Header = $"Choose forms for {column.Label}..." };
            item.Click += (_, _) => AddItems(column); addMenu.Items.Add(item);
        }
        refreshing = wasRefreshing; UpdateActions();
    }

    private string CellSummary(string rowId, VariationColumn column)
    {
        var argument = BehaviorFamily.GetArgument(document, rowId, column.Parameter);
        return argument.IsUnsetArgument ? "Choose a value" : presentation.Summary(argument, column.Field?.ChildContext ?? "", behaviorMode: true);
    }

    private void RefreshSummaries()
    {
        var family = BehaviorFamily.Find(document, RuleId);
        if (family is null || !family.Parameters.SequenceEqual(columns.Select(column => column.Parameter)) ||
            !family.Rows.Select(row => row.Id).SequenceEqual(rows.Select(row => row.Id)))
        {
            if (columns.Count > 0 || family?.IsTemplate == true) RefreshRows();
            return;
        }
        foreach (var row in rows)
            for (var index = 0; index < columns.Count; index++) row.Set(index, CellSummary(row.Id, columns[index]));
        UpdateActions();
    }

    private void SelectCell(string rowId, string? parameter)
    {
        var row = rows.FirstOrDefault(candidate => candidate.Id == rowId); if (row is null) return;
        var columnIndex = Math.Max(0, columns.FindIndex(column => column.Parameter == parameter));
        refreshing = true; table.SelectedItem = row;
        table.CurrentCell = new DataGridCellInfo(row, table.Columns[columns.Count == 0 ? 0 : columnIndex + 1]);
        refreshing = false; table.ScrollIntoView(row); OpenCell();
    }

    private VariationColumn? SelectedColumn => table.Columns.IndexOf(table.CurrentCell.Column) is var index && index > 0 && index <= columns.Count
        ? columns[index - 1] : columns.FirstOrDefault(column => column.Parameter == cellParameter) ?? columns.FirstOrDefault();

    private void OpenCell()
    {
        if (!variationsActive || !table.CurrentCell.IsValid) return;
        var row = table.CurrentItem as VariationRow ?? table.SelectedItem as VariationRow;
        var column = SelectedColumn;
        if (row is null || column is null) { CaptureCell(); ClearCell(); UpdateActions(); return; }
        if (row.Id == cellRowId && column.Parameter == cellParameter) { UpdateActions(); return; }
        CaptureCell(); ClearCell();
        cellRowId = row.Id; cellParameter = column.Parameter;
        var argument = BehaviorFamily.GetArgument(document, row.Id, column.Parameter);
        var value = argument.IsUnsetArgument && column.Field?.Kind == FieldKind.ObjectList
            ? ValueEditorControl.NewValue(column.Field.ChildContext, column.Field) : argument;
        cellEditor = new ValueEditorControl(document, forms, value, column.Field?.ChildContext ?? PresetSchema.InferContext("", value),
            column.Label, field: column.Field, groups: groups, behaviorMode: true, allowVariations: false, sourceForms: Sources(row.Id), showHeading: false);
        cellEditor.Changed += (_, _) => { cellDirty = true; NotifyChanged(); RefreshSummaries(); };
        var panel = new StackPanel();
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(EditorControls.Text($"Row {row.Number} · {column.Label}", true));
        if (column.Field is { } field) heading.Children.Add(EditorControls.Info(field.Label, field.Help));
        panel.Children.Add(heading); panel.Children.Add(cellEditor);
        cellFeedback.FontSize = 12; panel.Children.Add(cellFeedback);
        cellSection.Content = panel; cellSection.Visibility = Visibility.Visible;
        UpdateActions();
    }

    private void ClearCell()
    {
        cellEditor = null; cellRowId = null; cellParameter = null; cellDirty = false;
        if (cellFeedback.Parent is Panel previous) previous.Children.Remove(cellFeedback);
        cellSection.Content = null; cellSection.Visibility = Visibility.Collapsed;
    }

    private string[] SelectedRows() => table.SelectedItems.OfType<VariationRow>().Select(row => row.Id).ToArray();

    private IReadOnlyList<FormRow> CommonSources() => (BehaviorFamily.Find(document, RuleId)?.Rows ?? [Rule()])
        .SelectMany(row => Sources(row.Id)).Distinct().ToArray();

    private IReadOnlyList<FormRow> Sources(string rowId)
    {
        var family = BehaviorFamily.Find(document, rowId);
        var rule = family?.Definition.Value ?? document.Blocks.First(block => block.Id == rowId).Value;
        var key = rule.Get("owners") is not null ? "owners" : "forms";
        var selection = rule.Get(key);
        if (selection is { Kind: ValueKind.Scalar } && selection.Text.StartsWith('$') &&
            family?.Parameters.Contains(selection.Text[1..]) == true)
            selection = BehaviorFamily.GetArgument(document, rowId, selection.Text[1..]);
        var source = PresetValue.Map();
        if (selection is not null && !PresetDocument.Descendants(selection).Any(value => value.Kind == ValueKind.Alias ||
            value.Kind == ValueKind.Scalar && (value.Text.StartsWith('$') || TemplateCall.TryParse(value.Text, out _, out _))))
            source.Set(key, selection);
        else source = presentation.EffectiveValue(rowId) ?? source;
        return presentation.SourceForms(source).Where(new FormSelection(Category: document.Category).Matches).ToArray();
    }

    private void UpdateActions()
    {
        var selected = SelectedRows(); var column = SelectedColumn;
        addRow.Visibility = document.Kind != PresetKind.Default || rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        addOptions.Visibility = document.Kind != PresetKind.Default && addMenu.Items.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        duplicate.Visibility = document.Kind != PresetKind.Default && selected.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        remove.Visibility = selected.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var hasValue = cellRowId is not null && column is not null &&
            !BehaviorFamily.GetArgument(document, cellRowId, column.Parameter).IsUnsetArgument;
        fill.Visibility = hasValue && selected.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        paste.Visibility = columns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (cellRowId is not null && column is not null)
        {
            var value = BehaviorFamily.GetArgument(document, cellRowId, column.Parameter);
            var errors = value.IsUnsetArgument ? new[] { "Choose a value for this row." } : column.Field is { } field
                ? PresetSchema.ValidateField(value, field, field.ChildContext, document.Category, document.Kind) : [];
            cellFeedback.Text = string.Join(Environment.NewLine, errors);
            cellFeedback.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            cellFeedback.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        }
        var missing = rows.Sum(row => columns.Count(item => BehaviorFamily.GetArgument(document, row.Id, item.Parameter).IsUnsetArgument));
        rowStatus.Text = $"{rows.Count} {(rows.Count == 1 ? "row" : "rows")}" + (selected.Length > 1 ? $" · {selected.Length} selected" : "") +
            (missing > 0 ? $" · {missing} {(missing == 1 ? "value" : "values")} to fill" : "");
    }

    private void AddRow()
    {
        Capture(); var parameter = SelectedColumn?.Parameter;
        var row = BehaviorFamily.AddUse(document, RuleId);
        if (Rule().Kind == BlockKind.Template) RuleId = row.Id;
        Rebuild(true, row.Id, parameter); NotifyChanged();
    }

    private void AddItems(VariationColumn column)
    {
        if (column.Field is not { } field) return;
        Capture();
        var picker = new FormPickerWindow(forms, multiple: true,
            selection: BulkFormSelection(field, column.Parameter), purpose: "Choose forms for " + column.Label) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true) return;
        string? first = null;
        foreach (var form in picker.SelectedForms)
        {
            var row = BehaviorFamily.AddUse(document, RuleId); first ??= row.Id;
            BehaviorFamily.SetArgument(document, row.Id, column.Parameter, PresetValue.Scalar(form.Identifier));
        }
        if (Rule().Kind == BlockKind.Template && first is not null) RuleId = first;
        Rebuild(true, first, column.Parameter); NotifyChanged();
    }

    private FormSelection BulkFormSelection(PresetField field, string parameter)
    {
        var excluded = new HashSet<FormRow>();
        var family = BehaviorFamily.Find(document, RuleId)!;
        if (field.MatchSourceType && family.Definition.Value.Get("stages") is { Kind: ValueKind.Sequence } stages)
            foreach (var stage in stages.Items)
            {
                if (PresetSchema.InactiveReason(field, stage, "stage", document.Category) is not null ||
                    stage.Get("FormEditorID") is not { Kind: ValueKind.Scalar } item || item.Text == "$" + parameter) continue;
                var values = item.Text.StartsWith('$') && family.Parameters.Contains(item.Text[1..])
                    ? family.Rows.Select(row => BehaviorFamily.GetArgument(document, row.Id, item.Text[1..])) : [item];
                foreach (var value in values)
                    if (presentation.ResolveForm(value.Text) is { } form) excluded.Add(form);
            }
        return FormSelection.ForField(field, document.Category, CommonSources(), excluded);
    }

    private void DuplicateRows()
    {
        var selected = SelectedRows(); if (selected.Length == 0) return; Capture();
        var parameter = cellParameter; string? first = null;
        foreach (var row in selected)
        {
            var added = BehaviorFamily.DuplicateUse(document, row); first ??= added.Id;
        }
        Rebuild(true, first, parameter); NotifyChanged();
    }

    private void RemoveRows()
    {
        var selected = SelectedRows(); var family = BehaviorFamily.Find(document, RuleId);
        if (family is null || selected.Length == 0) return;
        Capture(); var next = family.Rows.FirstOrDefault(row => !selected.Contains(row.Id))?.Id ?? family.Definition.Id;
        foreach (var row in selected) BehaviorFamily.RemoveUse(document, row);
        RuleId = selected.Contains(RuleId) ? next : RuleId;
        Rebuild(true, RuleId); NotifyChanged();
    }

    private void FillSelected()
    {
        if (cellRowId is null || SelectedColumn is not { } column) return;
        var selected = SelectedRows(); var rowId = cellRowId; Capture();
        var value = BehaviorFamily.GetArgument(document, rowId, column.Parameter);
        if (value.IsUnsetArgument) return;
        foreach (var row in selected) BehaviorFamily.SetArgument(document, row, column.Parameter, value);
        Rebuild(true, rowId, column.Parameter); NotifyChanged();
    }

    private void PasteRows()
    {
        try { PasteCells(Clipboard.GetText()); }
        catch (Exception error) { ShowError(error, "Cannot paste cells"); }
    }

    private void PasteCells(string text)
    {
        if (columns.Count == 0) return;
        if (string.IsNullOrEmpty(text)) return;
        var startColumn = Math.Max(0, columns.IndexOf(SelectedColumn!));
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var values = lines.Select(line => line.Split('\t')).ToArray();
        if (values.Any(row => row.Length > columns.Count - startColumn))
            throw new InvalidOperationException($"The clipboard has {values.Max(row => row.Length)} columns. Only {columns.Count - startColumn} fit from '{columns[startColumn].Label}'.\n\nCopy only the matching columns, or select an earlier column before pasting. Separate columns with tabs and rows with new lines.");
        var parsed = values.Select(row => row.Select((value, index) => PasteValue(value, columns[startColumn + index].Field)).ToArray()).ToArray();
        var startRow = rows.FindIndex(row => row.Id == cellRowId);
        if (startRow < 0) startRow = rows.Count;
        if (document.Kind == PresetKind.Default && startRow + parsed.Length > Math.Max(1, rows.Count))
            throw new InvalidOperationException("This preset has one row. Paste the values into that row.");
        Capture(); var existing = BehaviorFamily.Find(document, RuleId)!.Rows.ToArray(); string? first = null;
        for (var rowIndex = 0; rowIndex < parsed.Length; rowIndex++)
        {
            var target = startRow + rowIndex < existing.Length ? existing[startRow + rowIndex] : BehaviorFamily.AddUse(document, RuleId);
            first ??= target.Id;
            for (var columnIndex = 0; columnIndex < parsed[rowIndex].Length; columnIndex++)
                BehaviorFamily.SetArgument(document, target.Id, columns[startColumn + columnIndex].Parameter, parsed[rowIndex][columnIndex]);
        }
        if (Rule().Kind == BlockKind.Template && first is not null) RuleId = first;
        Rebuild(true, first, columns[startColumn].Parameter); NotifyChanged();
    }

    private static PresetValue PasteValue(string text, PresetField? field)
    {
        if (text.Length == 0) return PresetValue.UnsetArgument();
        if (text.StartsWith('[') || text.StartsWith('{') || text.StartsWith('"') || text.StartsWith('\'')) return PresetYaml.Parse(text);
        return new PresetValue { Kind = ValueKind.Scalar, Text = text,
            Style = field?.Kind == FieldKind.Text ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain };
    }

    private void NotifyChanged()
    {
        Capture(); presentation = new PresetPresentation(document, forms, groups); Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ShowError(Exception error, string title) =>
        MessageBox.Show(Window.GetWindow(this), error.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
