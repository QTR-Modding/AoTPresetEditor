using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AoTPresetEditor;

public sealed class GroupPickerWindow : Window
{
    private readonly List<FormGroupDefinition> groups;
    private readonly IReadOnlyList<FormRow> forms;
    private readonly FormSelection? constraint;
    private readonly List<FormGroupDefinition> created = [];
    private readonly ListBox groupList = new() { DisplayMemberPath = nameof(FormGroupDefinition.Name) };
    private readonly TextBox search = new();
    private readonly TextBlock description = EditorControls.Text("");
    private readonly TextBlock source = EditorControls.Text("");
    private readonly DataGrid members = CreateMemberGrid();
    private readonly Button choose;

    public FormGroupDefinition? SelectedGroup { get; private set; }
    public IReadOnlyList<FormGroupDefinition> CreatedGroups { get; private set; } = [];

    public GroupPickerWindow(IReadOnlyList<FormGroupDefinition> groups, IReadOnlyList<FormRow> forms, FormSelection? constraint = null, string? purpose = null)
    {
        this.groups = [.. groups];
        this.forms = forms;
        this.constraint = constraint;
        Title = purpose ?? "Choose a form group"; Width = 1080; Height = 660; MinWidth = 780; MinHeight = 450;
        EditorControls.FitToOwner(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var layout = new DockPanel { Margin = new Thickness(14) };
        var heading = new StackPanel();
        heading.Children.Add(EditorControls.Text("Choose an existing group, or create a reusable list of forms."));
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var footer = new StackPanel();
        choose = EditorControls.Button("Choose", Choose); choose.IsDefault = true; choose.Visibility = Visibility.Collapsed;
        choose.SetResourceReference(StyleProperty, "PrimaryButton");
        EditorControls.Footer(footer, EditorControls.Button("New group...", NewGroup), choose,
            EditorControls.Button("Cancel", () => DialogResult = false));
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160, MaxWidth = 280 });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star), MinWidth = 360 });
        var left = new DockPanel { Margin = new Thickness(0, 0, 6, 0) };
        search.Margin = new Thickness(0, 0, 0, 10); search.ToolTip = "Search group names";
        DockPanel.SetDock(search, Dock.Top); left.Children.Add(search); left.Children.Add(groupList);
        columns.Children.Add(left);
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Center };
        splitter.SetResourceReference(BackgroundProperty, "DividerStrokeColorDefaultBrush");
        Grid.SetColumn(splitter, 1); columns.Children.Add(splitter);
        var right = new DockPanel();
        source.TextWrapping = TextWrapping.NoWrap; source.TextTrimming = TextTrimming.CharacterEllipsis;
        source.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = source });
        var details = new StackPanel(); details.Children.Add(description); source.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); details.Children.Add(source);
        DockPanel.SetDock(details, Dock.Top); right.Children.Add(details); right.Children.Add(members);
        Grid.SetColumn(right, 2); columns.Children.Add(right); layout.Children.Add(columns); Content = layout;
        search.TextChanged += (_, _) => Filter();
        groupList.SelectionChanged += (_, _) => ShowGroup();
        groupList.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject target && ItemsControl.ContainerFromElement(groupList, target) is ListBoxItem) Choose();
        };
        Filter();
    }

    private void Filter()
    {
        var selected = groupList.SelectedItem;
        groupList.ItemsSource = groups.Where(group => group.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        groupList.SelectedItem = selected;
        ShowGroup();
    }

    private void ShowGroup()
    {
        if (groupList.SelectedItem is not FormGroupDefinition group)
        {
            choose.Visibility = Visibility.Collapsed; description.Text = "Select a group to see its members."; source.Text = ""; members.ItemsSource = null; return;
        }
        choose.Visibility = Visibility.Visible;
        var rows = MemberRows(group.Members, forms, constraint);
        members.ItemsSource = rows;
        var resolved = rows.Count(row => row.Form is not null);
        var applicable = rows.Count(row => row.Applicable);
        description.Text = constraint is null
            ? $"{group.Members.Count} members; {resolved} matched in the loaded catalog."
            : $"{group.Members.Count} members; {applicable} known members applicable to this field.";
        if (resolved != rows.Count) description.Text += $" {rows.Count - resolved} not found in this profile; their identifiers are kept.";
        if (resolved != applicable) description.Text += $" {resolved - applicable} cannot be used for this field.";
        source.Text = group.SourcePath ?? "New group - saved with this preset.";
    }

    private void NewGroup()
    {
        var editor = new GroupEditorWindow(groups, forms, constraint) { Owner = this };
        if (editor.ShowDialog() != true || editor.Group is not { } group) return;
        groups.Add(group); created.Add(group); search.Text = ""; Filter(); groupList.SelectedItem = group;
    }

    private void Choose()
    {
        if (groupList.SelectedItem is not FormGroupDefinition group) return;
        SelectedGroup = group; CreatedGroups = created.ToArray(); DialogResult = true;
    }

    internal sealed record MemberRow(string Identifier, FormRow? Form, bool Applicable)
    {
        public string Name => Form?.Name is { Length: > 0 } name ? name : Form?.EditorId ?? "";
        public string Type => Form?.Type ?? "";
        public string Plugin => Form?.Plugin ?? "";
        public string Status => Form is null ? "Not found" : Applicable ? "Available" : "Not applicable";
    }

    internal static IReadOnlyList<MemberRow> MemberRows(IReadOnlyList<string> identifiers, IReadOnlyList<FormRow> forms, FormSelection? constraint)
    {
        var requested = identifiers.Concat(identifiers.Select(PresetPresentation.NormalizeIdentifier)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = new Dictionary<string, FormRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var form in forms)
        {
            var identifier = PresetPresentation.NormalizeIdentifier(form.Identifier);
            if (requested.Contains(identifier)) matched.TryAdd(identifier, form);
            if (form.EditorId.Length != 0 && requested.Contains(form.EditorId)) matched.TryAdd(form.EditorId, form);
        }
        return identifiers.Select(identifier =>
        {
            var form = matched.GetValueOrDefault(PresetPresentation.NormalizeIdentifier(identifier)) ?? matched.GetValueOrDefault(identifier);
            return new MemberRow(identifier, form, form is not null && (constraint?.Matches(form) ?? true));
        }).ToArray();
    }

    internal static DataGrid CreateMemberGrid()
    {
        var grid = new DataGrid
        {
            IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow,
            EnableRowVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column
        };
        foreach (var (title, property, weight) in new[]
        {
            ("Status", nameof(MemberRow.Status), 125d), ("Name", nameof(MemberRow.Name), 180d), ("Identifier", nameof(MemberRow.Identifier), 240d),
            ("Type", nameof(MemberRow.Type), 105d), ("Plugin", nameof(MemberRow.Plugin), 170d)
        })
        {
            var text = new Style(typeof(TextBlock));
            text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            text.Setters.Add(new Setter(ToolTipProperty, new Binding(property)));
            grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property),
                Width = new DataGridLength(weight, DataGridLengthUnitType.Star), MinWidth = title == "Status" ? 110 : 55, ElementStyle = text });
        }
        return grid;
    }
}

public sealed class GroupEditorWindow : Window
{
    private readonly IReadOnlyList<FormGroupDefinition> existing;
    private readonly IReadOnlyList<FormRow> forms;
    private readonly FormSelection? constraint;
    private readonly List<string> identifiers = [];
    private readonly TextBox name = new();
    private readonly DataGrid members = GroupPickerWindow.CreateMemberGrid();
    private readonly TextBlock status = EditorControls.Text("");
    private readonly TextBlock nameStatus = EditorControls.Text("");
    private readonly Button create;

    public FormGroupDefinition? Group { get; private set; }

    public GroupEditorWindow(IReadOnlyList<FormGroupDefinition> existing, IReadOnlyList<FormRow> forms, FormSelection? constraint = null)
    {
        this.existing = existing; this.forms = forms; this.constraint = constraint;
        Title = "New form group"; Width = 980; Height = 650; MinWidth = 760; MinHeight = 460;
        EditorControls.FitToOwner(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var layout = new DockPanel { Margin = new Thickness(14) };
        var header = new StackPanel();
        var nameRow = new DockPanel();
        var label = new Label { Content = "Name", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 10, 0), Target = name };
        DockPanel.SetDock(label, Dock.Left); nameRow.Children.Add(label); nameRow.Children.Add(name); header.Children.Add(nameRow);
        nameStatus.Margin = new Thickness(0, 5, 0, 8); nameStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); header.Children.Add(nameStatus);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var add = EditorControls.Button("Add forms...", AddForms);
        add.Visibility = forms.Any(form => constraint?.Matches(form) ?? true) ? Visibility.Visible : Visibility.Collapsed;
        actions.Children.Add(add);
        var remove = EditorControls.Button("Remove selected", RemoveSelected); remove.Visibility = Visibility.Collapsed;
        members.SelectionChanged += (_, _) => remove.Visibility = members.SelectedItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        actions.Children.Add(remove); header.Children.Add(actions);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var footer = new StackPanel(); footer.Children.Add(status);
        create = EditorControls.Button("Create group", Create);
        create.ToolTip = "Adds this group to the preset. Save to mod exports it with the preset.";
        EditorControls.Footer(footer, create, EditorControls.Button("Cancel", () => DialogResult = false));
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer); layout.Children.Add(members); Content = layout;
        name.TextChanged += (_, _) => CheckName();
        Refresh(); CheckName();
    }

    private void AddForms()
    {
        var picker = new FormPickerWindow(forms, multiple: true, selection: constraint, purpose: "Choose group members") { Owner = this };
        if (picker.ShowDialog() != true) return;
        foreach (var form in picker.SelectedForms)
            if (!identifiers.Contains(form.Identifier, StringComparer.OrdinalIgnoreCase)) identifiers.Add(form.Identifier);
        Refresh();
    }

    private void RemoveSelected()
    {
        foreach (var selected in members.SelectedItems.Cast<GroupPickerWindow.MemberRow>().ToArray()) identifiers.Remove(selected.Identifier);
        Refresh();
    }

    private void Refresh()
    {
        members.ItemsSource = GroupPickerWindow.MemberRows(identifiers, forms, constraint);
        status.Text = $"{identifiers.Count} members" + (forms.Any(form => constraint?.Matches(form) ?? true) ? "" : " · No applicable forms in the loaded catalog.");
    }

    private string? NameError()
    {
        var groupName = name.Text.Trim();
        if (FormGroupCatalog.ValidateName(groupName) is { } error) return error;
        return existing.Any(group => group.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase))
            ? "A group with this name already exists. Choose a different name." : null;
    }

    private void CheckName()
    {
        var error = NameError();
        create.Visibility = error is null ? Visibility.Visible : Visibility.Collapsed;
        nameStatus.Text = error ?? (forms.Any(form => form.EditorId.Equals(name.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            ? "This name also identifies a form. AoT will use the group first in fields that accept groups."
            : "Use this name wherever a preset field accepts form groups.");
    }

    private void Create()
    {
        if (NameError() is { } error) { nameStatus.Text = error; return; }
        var group = new FormGroupDefinition(name.Text.Trim(), identifiers.ToArray());
        var errors = FormGroupCatalog.Validate(group);
        if (errors.Count != 0) { status.Text = string.Join(Environment.NewLine, errors); return; }
        Group = group; DialogResult = true;
    }
}
