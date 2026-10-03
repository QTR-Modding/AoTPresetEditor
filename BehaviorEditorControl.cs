using System.ComponentModel;
using System.Text.RegularExpressions;
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
    private readonly DockPanel variationNavigation = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button backToSettings;
    private readonly TextBlock variationContext = EditorControls.Text("");
    private readonly DataGrid table = new();
    private readonly ComboBox fieldChoice = new() { MinWidth = 160, MaxWidth = 380 };
    private readonly Expander cellSection = new() { IsExpanded = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock rowStatus = EditorControls.Text("");
    private readonly TextBlock cellFeedback = EditorControls.Text("");
    private readonly WrapPanel valueActions = new();
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
    private Button addItems = null!;
    private Button duplicate = null!;
    private Button remove = null!;
    private Button paste = null!;
    private Button fill = null!;
    private Button same = null!;

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
        DockPanel.SetDock(variationNavigation, Dock.Top); layout.Children.Add(variationNavigation);
        layout.Children.Add(tabs); Content = layout;
        BuildVariationView();
        backToSettings = EditorControls.Button("Back to settings", () => tabs.SelectedIndex = 0);
        backToSettings.ToolTip = "Return to the behavior's settings and keep your edits.";
        DockPanel.SetDock(backToSettings, Dock.Left); variationNavigation.Children.Add(backToSettings);
        DockPanel.SetDock(same, Dock.Right); variationNavigation.Children.Add(same);
        variationContext.VerticalAlignment = VerticalAlignment.Center;
        variationContext.Margin = new Thickness(8, 0, 8, 0); variationNavigation.Children.Add(variationContext);
        commonScroll = new ScrollViewer { Content = commonContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        variationScroll = new ScrollViewer { Content = variationView, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        tabs.Items.Add(new TabItem { Header = "Common settings", Content = commonScroll });
        tabs.SelectionChanged += (_, args) =>
        {
            if (refreshing || !ReferenceEquals(args.Source, tabs)) return;
            Capture(); variationsActive = tabs.SelectedIndex == 1;
            if (variationsActive) { RefreshRows(); OpenCell(); }
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
            var row = family.Rows.FirstOrDefault(candidate => candidate.Id == rowId) ?? family.Rows[0];
            var normalized = NormalizeLabel(message);
            var column = message.StartsWith("Fill the missing values", StringComparison.OrdinalIgnoreCase)
                ? columns.FirstOrDefault(candidate => BehaviorFamily.GetArgument(document, row.Id, candidate.Parameter).IsUnsetArgument)
                : columns.OrderByDescending(candidate => candidate.Label.Length)
                    .FirstOrDefault(candidate => normalized.Contains(NormalizeLabel(candidate.Label), StringComparison.Ordinal));
            column ??= columns.FirstOrDefault(candidate => normalized.Contains(NormalizeLabel(candidate.Label.Split(" / ")[^1]), StringComparison.Ordinal));
            if (column is not null)
            {
                tabs.SelectedIndex = 1; SelectCell(row.Id, column.Parameter);
                cellSection.IsExpanded = true; cellSection.BringIntoView();
                if (cellEditor?.FocusIssue(message) != true) cellEditor?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                return true;
            }
        }
        tabs.SelectedIndex = 0;
        return commonEditor.FocusIssue(message);
    }

    private static string NormalizeLabel(string label) =>
        string.Join(" ", Regex.Matches(label.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(match => match.Value));

    private void BuildCommonEditor()
    {
        var family = BehaviorFamily.Find(document, RuleId);
        var definition = family?.Definition ?? Rule(); commonId = definition.Id;
        commonEditor = new ValueEditorControl(document, forms, definition.Value, definition.Context,
            family?.IsTemplate == true ? "Common settings" : "Settings", parametersAllowed: family?.IsTemplate == true,
            groups: groups, behaviorMode: true, allowVariations: family is not null, sourceForms: CommonSources());
        document = commonEditor.Document;
        commonEditor.Changed += (_, _) => { if (!refreshing) NotifyChanged(); };
        commonEditor.VariationRequested += MakeVariable;
        var panel = new StackPanel();
        if (family?.IsTemplate == true)
        {
            var otherUses = new PresetPresentation(document, forms, groups).Uses(family.Definition)
                .Count(block => block.Kind == BlockKind.Rule && !family.Rows.Any(row => row.Id == block.Id));
            if (otherUses > 0) panel.Children.Add(EditorControls.Text($"Changes to these common settings also affect {otherUses} other behaviors."));
        }
        panel.Children.Add(commonEditor); commonContent.Content = panel;
    }

    private void Rebuild(bool showVariations, string? selectRow = null, string? selectParameter = null)
    {
        refreshing = true; ClearCell(); BuildCommonEditor();
        var family = BehaviorFamily.Find(document, RuleId);
        if (family is { IsTemplate: true, Parameters.Count: > 0 })
        {
            ((TabItem)tabs.Items[0]).Header = "Common settings";
            if (tabs.Items.Count == 1) tabs.Items.Add(new TabItem { Header = "Variations", Content = variationScroll });
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

    private void MakeVariable(IReadOnlyList<string> path, PresetField field)
    {
        Capture();
        try
        {
            var parameter = BehaviorFamily.MakeVariable(document, RuleId, path);
            Rebuild(true, RuleId, parameter); variationScroll.ScrollToTop(); NotifyChanged();
        }
        catch (Exception error) { ShowError(error, "Cannot vary this setting"); }
    }

    private readonly StackPanel variationView = new();

    private void BuildVariationView()
    {
        var help = EditorControls.Text("Each row is one use of this behavior. Add rows to use it with different items or values. The other settings stay shared. Select a cell to edit its value below.");
        help.FontSize = 12; variationView.Children.Add(help);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        addRow = EditorControls.Button("Add row", AddRow); actions.Children.Add(addRow);
        addItems = EditorControls.Button("Add items...", AddItems); actions.Children.Add(addItems);
        duplicate = EditorControls.Button("Duplicate selected", DuplicateRows); actions.Children.Add(duplicate);
        remove = EditorControls.Button("Remove selected", RemoveRows); actions.Children.Add(remove);
        paste = EditorControls.Button("Paste rows", PasteRows); actions.Children.Add(paste); variationView.Children.Add(actions);
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
        };
        variationView.Children.Add(table); rowStatus.FontSize = 12; rowStatus.Margin = new Thickness(0, 5, 0, 8);
        variationView.Children.Add(rowStatus);
        var fieldLabel = EditorControls.Text("Setting"); fieldLabel.VerticalAlignment = VerticalAlignment.Center;
        fieldLabel.Margin = new Thickness(0, 0, 8, 0); valueActions.Children.Add(fieldLabel);
        fieldChoice.DisplayMemberPath = nameof(VariationColumn.Label);
        fieldChoice.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            if (fieldChoice.SelectedItem is VariationColumn column && table.CurrentItem is VariationRow row)
                SelectCell(row.Id, column.Parameter);
            UpdateActions();
        };
        valueActions.Children.Add(fieldChoice);
        fill = EditorControls.Button("Fill selected rows", FillSelected); valueActions.Children.Add(fill);
        same = EditorControls.Button("Use one value for this setting", SameForAll);
        same.ToolTip = "Remove this variation and use the selected value throughout the behavior. You will be asked before replacing different values.";
        variationView.Children.Add(valueActions); variationView.Children.Add(cellSection);
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
                var label = ParameterLabel(family.Definition.Value, family.Definition.Context, parameter) ?? $"Value {index + 1}";
                var field = PresetSchema.GetParameterField(family.Definition, parameter, document);
                if (field is not null) field = field with { Label = label };
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
        fieldChoice.ItemsSource = null; fieldChoice.ItemsSource = columns;
        fieldChoice.SelectedIndex = columns.Count == 0 ? -1 : Math.Max(0, columns.FindIndex(column => column.Parameter == cellParameter));
        refreshing = wasRefreshing; UpdateActions();
    }

    private string? ParameterLabel(PresetValue value, string context, string parameter)
    {
        if (value.Kind == ValueKind.Scalar) return value.Text == "$" + parameter ? "" : null;
        if (value.Kind == ValueKind.Sequence)
        {
            for (var index = 0; index < value.Items.Count; index++)
                if (ParameterLabel(value.Items[index], context, parameter) is { } child)
                    return $"{index + 1}" + (child.Length == 0 ? "" : " / " + child);
        }
        else
            foreach (var (key, child) in value.Fields)
            {
                var field = PresetSchema.GetFields(context, document.Kind).FirstOrDefault(candidate => candidate.Key == key);
                if (ParameterLabel(child, field?.ChildContext ?? PresetSchema.InferContext(key, child), parameter) is { } label)
                    return (field?.Label ?? key) + (label.Length == 0 ? "" : " / " + label);
            }
        return null;
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
        if (columns.Count > 0) fieldChoice.SelectedIndex = columnIndex;
        refreshing = false; table.ScrollIntoView(row); OpenCell();
    }

    private void OpenCell()
    {
        if (!variationsActive) return;
        var row = table.CurrentItem as VariationRow ?? table.SelectedItem as VariationRow;
        var index = table.CurrentColumn is null ? -1 : table.CurrentColumn.DisplayIndex - 1;
        var column = index >= 0 && index < columns.Count ? columns[index] : fieldChoice.SelectedItem as VariationColumn;
        if (row is null || column is null) { CaptureCell(); ClearCell(); UpdateActions(); return; }
        if (row.Id == cellRowId && column.Parameter == cellParameter) { UpdateActions(); return; }
        CaptureCell(); ClearCell();
        cellRowId = row.Id; cellParameter = column.Parameter;
        var argument = BehaviorFamily.GetArgument(document, row.Id, column.Parameter);
        var value = argument.IsUnsetArgument && column.Field?.Kind == FieldKind.ObjectList
            ? ValueEditorControl.NewValue(column.Field.ChildContext, column.Field) : argument;
        cellEditor = new ValueEditorControl(document, forms, value, column.Field?.ChildContext ?? PresetSchema.InferContext("", value),
            column.Label, field: column.Field, groups: groups, behaviorMode: true, allowVariations: false, sourceForms: Sources(row.Id));
        cellEditor.Changed += (_, _) => { cellDirty = true; NotifyChanged(); RefreshSummaries(); };
        var panel = new StackPanel(); cellFeedback.FontSize = 12; panel.Children.Add(cellFeedback); panel.Children.Add(cellEditor);
        cellSection.Header = $"Row {row.Number}: {column.Label}"; cellSection.Content = panel; cellSection.Visibility = Visibility.Visible;
        refreshing = true; fieldChoice.SelectedItem = column; refreshing = false; UpdateActions();
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
        var selected = SelectedRows(); var column = fieldChoice.SelectedItem as VariationColumn;
        variationNavigation.Visibility = variationsActive ? Visibility.Visible : Visibility.Collapsed;
        variationContext.Text = column is null ? "Different values per item" : "Different values: " + column.Label;
        addRow.Visibility = document.Kind != PresetKind.Default ? Visibility.Visible : Visibility.Collapsed;
        addItems.Visibility = document.Kind != PresetKind.Default && column?.Field?.Kind is FieldKind.Form or FieldKind.FormList
            ? Visibility.Visible : Visibility.Collapsed;
        addItems.ToolTip = column is null ? "First vary an item setting in Common settings." : "Add one row for each form selected for " + column.Label + ".";
        duplicate.Visibility = document.Kind != PresetKind.Default && selected.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        remove.Visibility = selected.Length > 0 && selected.Length < rows.Count ? Visibility.Visible : Visibility.Collapsed;
        var hasValue = cellRowId is not null && column is not null &&
            !BehaviorFamily.GetArgument(document, cellRowId, column.Parameter).IsUnsetArgument;
        fill.Visibility = hasValue && selected.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        same.Visibility = variationsActive && hasValue ? Visibility.Visible : Visibility.Collapsed;
        paste.Visibility = valueActions.Visibility = columns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        rowStatus.Text = $"{rows.Count} rows" + (selected.Length > 1 ? $" · {selected.Length} selected" : "") +
            (missing > 0 ? $" · {missing} values to fill" : columns.Count == 0 ? " · All settings are common." : "");
    }

    private void AddRow()
    {
        Capture(); var parameter = (fieldChoice.SelectedItem as VariationColumn)?.Parameter;
        var row = BehaviorFamily.AddUse(document, RuleId); Rebuild(true, row.Id, parameter); NotifyChanged();
    }

    private void AddItems()
    {
        if (fieldChoice.SelectedItem is not VariationColumn { Field: { } field } column) return;
        Capture();
        var picker = new FormPickerWindow(forms, multiple: true,
            selection: BulkFormSelection(field, column.Parameter), purpose: "Add items for " + column.Label.ToLowerInvariant()) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true) return;
        string? first = null;
        foreach (var form in picker.SelectedForms)
        {
            var row = BehaviorFamily.AddUse(document, RuleId); first ??= row.Id;
            BehaviorFamily.SetArgument(document, row.Id, column.Parameter, PresetValue.Scalar(form.Identifier));
        }
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
        if (family is null || selected.Length == 0 || selected.Length >= family.Rows.Count) return;
        Capture(); var next = family.Rows.First(row => !selected.Contains(row.Id)).Id;
        foreach (var row in selected) BehaviorFamily.RemoveUse(document, row);
        RuleId = selected.Contains(RuleId) ? next : RuleId;
        Rebuild(true, RuleId); NotifyChanged();
    }

    private void FillSelected()
    {
        if (cellRowId is null || fieldChoice.SelectedItem is not VariationColumn column) return;
        var selected = SelectedRows(); var rowId = cellRowId; Capture();
        var value = BehaviorFamily.GetArgument(document, rowId, column.Parameter);
        if (value.IsUnsetArgument) return;
        foreach (var row in selected) BehaviorFamily.SetArgument(document, row, column.Parameter, value);
        Rebuild(true, rowId, column.Parameter); NotifyChanged();
    }

    private void SameForAll()
    {
        if (cellRowId is null || fieldChoice.SelectedItem is not VariationColumn column) return;
        var rowId = cellRowId; Capture(); var value = BehaviorFamily.GetArgument(document, rowId, column.Parameter);
        if (value.IsUnsetArgument) return;
        var family = BehaviorFamily.Find(document, RuleId)!;
        var chosen = PresetYaml.Write(value, flow: true);
        var different = family.Rows.Count(row => PresetYaml.Write(BehaviorFamily.GetArgument(document, row.Id, column.Parameter), flow: true) != chosen);
        if (different > 0 && MessageBox.Show(Window.GetWindow(this),
            $"Use {presentation.Summary(value, column.Field?.ChildContext ?? "", behaviorMode: true)} for {column.Label} in all {family.Rows.Count} rows?\n\nThis replaces {different} differing values and moves the setting into Common settings.",
            "Use one value for this setting", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try { BehaviorFamily.SameForAll(document, RuleId, column.Parameter, value); Rebuild(false); NotifyChanged(); }
        catch (Exception error) { ShowError(error, "Cannot use this value for all rows"); }
    }

    private void PasteRows()
    {
        if (columns.Count == 0) return;
        try
        {
            var text = Clipboard.GetText(); if (string.IsNullOrEmpty(text)) return;
            var startColumn = Math.Max(0, table.CurrentColumn?.DisplayIndex - 1 ?? 0);
            var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            var values = lines.Select(line => line.Split('\t')).ToArray();
            if (values.Any(row => row.Length > columns.Count - startColumn))
                throw new InvalidOperationException("The pasted rows have more columns than the selected setting and the settings to its right.");
            var parsed = values.Select(row => row.Select((value, index) => PasteValue(value, columns[startColumn + index].Field)).ToArray()).ToArray();
            var startRow = table.CurrentItem is VariationRow selected ? rows.IndexOf(selected) : rows.Count;
            if (document.Kind == PresetKind.Default && startRow + parsed.Length > rows.Count)
                throw new InvalidOperationException("This preset has one row. Paste the values into that row.");
            Capture(); var existing = BehaviorFamily.Find(document, RuleId)!.Rows.ToArray(); string? first = null;
            for (var rowIndex = 0; rowIndex < parsed.Length; rowIndex++)
            {
                var target = startRow + rowIndex < existing.Length ? existing[startRow + rowIndex] : BehaviorFamily.AddUse(document, RuleId);
                first ??= target.Id;
                for (var columnIndex = 0; columnIndex < parsed[rowIndex].Length; columnIndex++)
                    BehaviorFamily.SetArgument(document, target.Id, columns[startColumn + columnIndex].Parameter, parsed[rowIndex][columnIndex]);
            }
            Rebuild(true, first, columns[startColumn].Parameter); NotifyChanged();
        }
        catch (Exception error) { ShowError(error, "Cannot paste rows"); }
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
