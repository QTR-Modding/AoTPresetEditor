using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YamlDotNet.Core;

namespace AoTPresetEditor;

public sealed class ValueEditorWindow : Window
{
    private PresetDocument document;
    private readonly IReadOnlyList<FormRow> forms;
    private readonly IReadOnlyList<FormGroupDefinition> groups;
    private readonly bool parametersAllowed;
    private readonly string context;
    private readonly PresetField? field;
    private readonly StackPanel content = new();
    private readonly TextBlock problems = EditorControls.Text("");
    private readonly Expander validationDetails = new() { Header = "Validation details", Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox? name;
    private readonly IReadOnlyList<string> initialIssues;
    private Action capture = () => { };
    public PresetValue Value { get; private set; }
    public PresetDocument Document => document;
    public string BlockName => name?.Text.Trim() ?? "";

    public ValueEditorWindow(PresetDocument document, IReadOnlyList<FormRow> forms, PresetValue value,
        string context, string title, bool parametersAllowed = false, PresetField? field = null, string? blockName = null,
        IReadOnlyList<string>? issues = null, IReadOnlyList<FormGroupDefinition>? groups = null)
    {
        this.document = document.Clone();
        this.forms = forms;
        this.groups = groups ?? [];
        this.context = context;
        this.parametersAllowed = parametersAllowed;
        this.field = field;
        initialIssues = issues ?? [];
        Value = value.Clone();
        SetResourceReference(StyleProperty, typeof(Window));
        Title = blockName is null ? title : blockName + " - " + title;
        Width = 780; Height = 700; MinWidth = 620; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(22) };
        var header = new StackPanel();
        header.Children.Add(EditorControls.Text(title, true));
        if (blockName is not null)
        {
            header.Children.Add(EditorControls.Text("Name"));
            name = new TextBox { Text = blockName, Margin = new Thickness(0, 0, 0, 12) };
            header.Children.Add(name);
        }
        if (field is not null) header.Children.Add(EditorControls.Text(field.Help));
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var footer = new StackPanel();
        validationDetails.Content = new ScrollViewer { Content = problems, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        footer.Children.Add(validationDetails);
        var apply = EditorControls.Button("Apply", Apply);
        apply.SetResourceReference(StyleProperty, "PrimaryButton");
        EditorControls.Footer(footer, apply, EditorControls.Button("Cancel", () => DialogResult = false));
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
        Render();
    }

    private void Apply()
    {
        capture();
        if (name is not null && BlockName.Length == 0)
        {
            problems.Text = "Give this block a name.";
            validationDetails.Visibility = Visibility.Visible;
            validationDetails.IsExpanded = true;
            name.Focus();
            return;
        }
        DialogResult = true;
    }

    private void Render()
    {
        capture = () => { };
        content.Children.Clear();
        problems.Text = initialIssues.Count > 0
            ? "Last preset check:" + Environment.NewLine + string.Join(Environment.NewLine, initialIssues) +
                Environment.NewLine + "Apply to check your changes in the overview."
            : "";
        validationDetails.Visibility = initialIssues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        problems.Foreground = EditorControls.Muted;
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        if (document.Blocks.Any(block => block.Kind is BlockKind.Shared or BlockKind.Template))
            modes.Children.Add(EditorControls.Button("Use shared settings or template...", ChooseReference));
        if (parametersAllowed) modes.Children.Add(EditorControls.Button("Use parameter...", ChooseParameter));
        if (modes.Children.Count > 0) content.Children.Add(modes);
        if (parametersAllowed && name is not null)
        {
            var parameters = TemplateCall.FindParameters(Value, document);
            content.Children.Add(EditorControls.Text(parameters.Count == 0 ? "No parameters yet. Use a parameter on any field that should vary."
                : "Parameters, in order: " + string.Join(", ", parameters)));
        }
        if (Value.Kind == ValueKind.Alias)
        {
            var source = document.Blocks.FirstOrDefault(block => block.Value.Anchor == Value.Text);
            content.Children.Add(EditorControls.Text("Uses shared settings: " + (source?.Name ?? Value.Text)));
            AddReferenceActions(source);
        }
        else if (parametersAllowed && Value.Kind == ValueKind.Scalar && Value.Text.StartsWith('$') &&
            TemplateCall.IsName(Value.Text[1..]))
        {
            content.Children.Add(EditorControls.Text("Uses parameter: " + Value.Text[1..]));
            content.Children.Add(EditorControls.Text("Each use of this template supplies this value."));
            content.Children.Add(EditorControls.Button("Use own value...", () => SetValue(NewValue(context, field))));
        }
        else if (IsCall(Value, out var template, out var args)) RenderCall(template!, args);
        else if (Value.Kind == ValueKind.Mapping) RenderMap();
        else if (Value.Kind == ValueKind.Sequence) RenderList();
        else RenderScalar();
    }

    private bool IsCall(PresetValue value, out PresetBlock? template, out List<PresetValue> args)
    {
        template = null; args = [];
        if (value.Kind != ValueKind.Scalar || value.Tag == "tag:yaml.org,2002:str" ||
            !TemplateCall.TryParse(value.Text, out var key, out args)) return false;
        template = document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Template && block.Key == key);
        return template is not null;
    }

    private void AddReferenceActions(PresetBlock? source)
    {
        if (source is not null)
            content.Children.Add(EditorControls.Button("Edit definition...", () => EditDefinition(source)));
        content.Children.Add(EditorControls.Button("Replace with own settings...", () =>
        {
            if (MessageBox.Show(this, "Replace this reference with independent settings?", Title,
                MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            SetValue(NewValue(context, field));
        }));
    }

    private void EditDefinition(PresetBlock source)
    {
        var view = new ValueEditorWindow(document, forms, source.Value, source.Context, source.Name,
            source.Kind == BlockKind.Template,
            source.Kind == BlockKind.Shared && source.Value.Kind != ValueKind.Mapping ? field : null,
            groups: groups) { Owner = this };
        if (view.ShowDialog() != true) return;
        var updated = view.Document;
        var previous = updated.Clone();
        updated.Blocks.First(block => block.Id == source.Id).Value = view.Value;
        try
        {
            updated.RebindTemplateCalls(previous);
            AdoptDocument(updated);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Cannot apply definition changes", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Render();
    }

    private void RenderCall(PresetBlock template, List<PresetValue> args)
    {
        content.Children.Add(EditorControls.Text("Uses template: " + template.Name));
        var parameters = TemplateCall.FindParameters(template.Value, document);
        var suppliedArguments = args.Count;
        for (var i = 0; i < parameters.Count; i++)
        {
            var index = i;
            while (args.Count <= index) args.Add(PresetValue.UnsetArgument());
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var edit = EditorControls.Button("Edit...", () => EditArgument(template, parameters[index], args[index]));
            DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            row.Children.Add(EditorControls.Text(parameters[i] + ": " + Summary(args[i], document)));
            content.Children.Add(row);
        }
        if (suppliedArguments != parameters.Count)
            content.Children.Add(EditorControls.Text($"This use has {suppliedArguments} arguments; the template needs {parameters.Count}. Choose the template again to reset its arguments."));
        AddReferenceActions(template);
    }

    private void EditArgument(PresetBlock template, string parameter, PresetValue argument)
    {
        var argumentField = PresetSchema.GetParameterField(template, parameter, document);
        if (argument.IsUnsetArgument && argumentField?.Kind == FieldKind.ObjectList)
            argument = NewValue(argumentField.ChildContext, argumentField);
        var editor = new ValueEditorWindow(document, forms, argument, argumentField?.ChildContext ?? "Value",
            parameter, parametersAllowed, argumentField, groups: groups) { Owner = this };
        if (editor.ShowDialog() != true) return;
        AdoptDocument(editor.Document);
        var updatedTemplate = document.Blocks.First(block => block.Id == template.Id);
        var currentParameters = TemplateCall.FindParameters(updatedTemplate.Value, document);
        var currentIndex = currentParameters.ToList().IndexOf(parameter);
        if (currentIndex >= 0 && TemplateCall.TryParse(Value.Text, out _, out var currentArguments))
        {
            while (currentArguments.Count < currentParameters.Count) currentArguments.Add(PresetValue.UnsetArgument());
            currentArguments[currentIndex] = editor.Value;
            Value.Text = TemplateCall.Format(template.Key, currentArguments);
        }
        Render();
    }

    private void RenderMap()
    {
        var descriptors = PresetSchema.GetFields(context, document.Kind);
        foreach (var descriptor in descriptors) RenderField(descriptor);
        foreach (var key in Value.Fields.Keys.Where(key => key != "<<" && !descriptors.Any(item => item.Key == key)).ToArray())
        {
            var item = Value.Fields[key];
            RenderField(new PresetField(key, key, FieldKind.Text, "Additional setting preserved from this file.",
                ChildContext: PresetSchema.InferContext(key, item)));
        }
        if (descriptors.Count == 0)
            content.Children.Add(EditorControls.Button("Add setting...", AddSetting));
        var inherited = Value.Get("<<");
        if (inherited is null && !document.Blocks.Any(block => block.Kind is BlockKind.Shared or BlockKind.Template)) return;
        var reuse = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var edit = EditorControls.Button(inherited is null ? "Add..." : "Edit...", () =>
        {
            var editor = new ValueEditorWindow(document, forms, inherited ?? PresetValue.List(), "Inheritance", "Inherited settings", parametersAllowed, groups: groups) { Owner = this };
            if (editor.ShowDialog() == true) { AdoptDocument(editor.Document); Value.Set("<<", editor.Value); Render(); }
        });
        DockPanel.SetDock(edit, Dock.Right); reuse.Children.Add(edit);
        if (inherited is not null)
        {
            var clear = EditorControls.Button("Remove", () => { Value.Remove("<<"); Render(); });
            DockPanel.SetDock(clear, Dock.Right); reuse.Children.Add(clear);
        }
        reuse.Children.Add(EditorControls.Text("Inherited settings" + (inherited is null ? " (none)" : ": " + Summary(inherited, document))));
        content.Children.Add(new Expander { Header = "Shared settings and overrides", IsExpanded = inherited is not null,
            Content = reuse, Margin = new Thickness(0, 10, 0, 0) });
    }

    private void RenderField(PresetField descriptor)
    {
        var value = Value.Get(descriptor.Key);
        var box = new StackPanel();
        var row = new DockPanel();
        var label = EditorControls.Text(descriptor.Label);
        label.FontWeight = FontWeights.SemiBold;
        label.ToolTip = descriptor.Help;
        label.Margin = new Thickness(0, 0, 0, 2);
        var edit = EditorControls.Button(value is null ? "Set..." : "Edit...", () =>
        {
            var current = Value.Get(descriptor.Key) ?? NewValue(descriptor.ChildContext, descriptor);
            var editor = new ValueEditorWindow(document, forms, current, descriptor.ChildContext, descriptor.Label,
                parametersAllowed, descriptor, groups: groups) { Owner = this };
            if (editor.ShowDialog() != true) return;
            AdoptDocument(editor.Document);
            Value.Set(descriptor.Key, editor.Value);
            Render();
        });
        edit.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
        if (value is not null)
        {
            var clear = EditorControls.Button("Clear", () => { Value.Remove(descriptor.Key); Render(); });
            clear.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(clear, Dock.Right); row.Children.Add(clear);
        }
        box.Children.Add(label);
        var inherited = Value.Get("<<") is not null;
        var detail = EditorControls.Text(value is null ? (inherited ? "Not set here" : descriptor.Required ? "Required" : "Optional") : Summary(value, document));
        detail.FontSize = 12;
        detail.Foreground = value is null && descriptor.Required && !inherited ? Brushes.DarkOrange : EditorControls.Muted;
        detail.Margin = new Thickness(0);
        box.Children.Add(detail);
        row.Children.Add(box);
        content.Children.Add(new Border { Child = row, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(224, 229, 237)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(12, 9, 10, 9), Margin = new Thickness(0, 0, 6, 7) });
    }

    private void RenderList()
    {
        var list = new ListBox { MinHeight = 160, MaxHeight = 380, Margin = new Thickness(0, 0, 0, 12) };
        list.ItemsSource = Value.Items.Select((item, index) => $"{index + 1}. {Summary(item, document)}");
        content.Children.Add(list);
        var actions = new WrapPanel();
        if (field?.Kind == FieldKind.FormList)
            actions.Children.Add(EditorControls.Button("Choose forms...", () =>
            {
                var picker = new FormPickerWindow(forms, multiple: true, selection: FormSelection()) { Owner = this };
                if (picker.ShowDialog() != true) return;
                foreach (var form in picker.SelectedForms)
                    if (!Value.Items.Any(item => item.Kind == ValueKind.Scalar && item.Text == form.Identifier))
                        Value.Items.Add(PresetValue.Scalar(form.Identifier));
                Render();
            }));
        if (field?.AllowsGroups == true)
            actions.Children.Add(EditorControls.Button("Choose form group...", () => ChooseGroup(group =>
            {
                if (!Value.Items.Any(item => item.Kind == ValueKind.Scalar && item.Text == group.Name))
                    Value.Items.Add(PresetValue.Scalar(group.Name));
                Render();
            })));
        actions.Children.Add(EditorControls.Button("Add...", () =>
        {
            var item = NewListItem();
            var child = ChildEditor(item, "New " + ItemLabel());
            if (child.ShowDialog() == true) { AdoptDocument(child.Document); Value.Items.Add(child.Value); Render(); }
        }));
        void EditSelected()
        {
            var index = list.SelectedIndex;
            if (index < 0) return;
            var child = ChildEditor(Value.Items[index], ItemLabel() + " " + (index + 1));
            if (child.ShowDialog() == true) { AdoptDocument(child.Document); Value.Items[index] = child.Value; Render(); }
        }
        var edit = EditorControls.Button("Edit...", EditSelected);
        var remove = EditorControls.Button("Remove", () => { if (list.SelectedIndex >= 0) { Value.Items.RemoveAt(list.SelectedIndex); Render(); } });
        var up = EditorControls.Button("Move up", () => MoveItem(-1));
        var down = EditorControls.Button("Move down", () => MoveItem(1));
        actions.Children.Add(edit); actions.Children.Add(remove); actions.Children.Add(up); actions.Children.Add(down);
        void ShowAvailableActions()
        {
            edit.Visibility = remove.Visibility = list.SelectedIndex >= 0 ? Visibility.Visible : Visibility.Collapsed;
            up.Visibility = list.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
            down.Visibility = list.SelectedIndex >= 0 && list.SelectedIndex < Value.Items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        list.SelectionChanged += (_, _) => ShowAvailableActions();
        ShowAvailableActions();
        list.MouseDoubleClick += (_, _) => EditSelected();
        void MoveItem(int direction)
        {
            var index = list.SelectedIndex;
            if (index < 0 || index + direction < 0 || index + direction >= Value.Items.Count) return;
            (Value.Items[index], Value.Items[index + direction]) = (Value.Items[index + direction], Value.Items[index]);
            list.ItemsSource = Value.Items.Select((item, position) => $"{position + 1}. {Summary(item, document)}");
            list.SelectedIndex = index + direction;
        }
        content.Children.Add(actions);
        if (context == "Inheritance") content.Children.Add(EditorControls.Text("Settings earlier in this list take precedence. Fields set on this block override inherited values."));
    }

    private string ItemContext() => context switch
    {
        "Stages" or "stages" => "Stage", "Transformers" or "transformers" => "Transformer", "TimeModulators" or "modulators" => "TimeModulator",
        "MagicEffects" or "effects" => "MagicEffect", "Values" or "values" => "Value",
        "Rules" or "rules" => PresetDocument.RuleContext(document.Kind),
        "DefaultRules" or "defaultrules" => "DefaultRule", "CustomRules" or "customrules" => "CustomRule",
        "AddonRules" or "addonrules" => "AddonRule", _ => field?.ChildContext ?? context
    };
    private string ItemLabel() => ItemContext() switch
    {
        "Stage" or "stage" => "Stage", "Transformer" or "transformer" => "Transformation",
        "TimeModulator" or "modulator" => "Time modifier", "MagicEffect" or "effect" => "Magic effect", _ => "Value"
    };
    private PresetValue NewListItem()
    {
        var child = ItemContext();
        if (context == "Inheritance") return PresetValue.Map();
        var value = PresetSchema.GetFields(child, document.Kind).Count > 0 ? PresetValue.Map() : PresetValue.Scalar("");
        if (child is "Stage" or "stage")
        {
            value.Set("no", PresetValue.Scalar(Value.Items.Count.ToString()));
            value.Set("name", new() { Kind = ValueKind.Scalar, Text = "", Style = ScalarStyle.DoubleQuoted });
        }
        return value;
    }
    private ValueEditorWindow ChildEditor(PresetValue item, string title) => new(document, forms, item, ItemContext(), title,
        parametersAllowed, field?.Kind switch { FieldKind.FormList => field with { Kind = FieldKind.Form },
            FieldKind.IntegerList => field with { Kind = FieldKind.Integer }, _ => null }, groups: groups) { Owner = this };

    private void RenderScalar()
    {
        if (field?.Kind == FieldKind.Boolean)
        {
            var choice = new CheckBox { Content = field.Label, IsChecked = Value.Text.ToLowerInvariant() is "true" or "yes" or "y" or "on", Margin = new Thickness(0, 12, 0, 0) };
            content.Children.Add(choice);
            capture = () => { Value.Text = choice.IsChecked == true ? "true" : "false"; Value.Style = ScalarStyle.Plain; Value.Tag = ""; };
            return;
        }
        var entry = new TextBox { Text = Value.Text, Margin = new Thickness(0, 0, 0, 12) };
        content.Children.Add(entry);
        capture = () =>
        {
            if (Value.IsUnsetArgument) Value.Tag = "";
            if (Value.Text != entry.Text)
            {
                Value.Text = entry.Text; Value.Tag = "";
                Value.Style = entry.Text.Length == 0 ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain;
            }
        };
        if (field?.Kind is FieldKind.Form or FieldKind.FormList || field is null)
        {
            content.Children.Add(EditorControls.Button("Choose form...", () =>
            {
                var picker = new FormPickerWindow(forms, selection: FormSelection()) { Owner = this };
                if (picker.ShowDialog() == true && picker.SelectedForms.FirstOrDefault() is { } form) entry.Text = form.Identifier;
            }));
        }
        if (field?.AllowsGroups == true)
            content.Children.Add(EditorControls.Button("Choose form group...", () => ChooseGroup(group => entry.Text = group.Name)));
        if (field?.Kind is FieldKind.FormList or FieldKind.IntegerList || field is null)
        {
            content.Children.Add(EditorControls.Button("Use a list of values...", () =>
            {
                capture();
                var list = PresetValue.List();
                if (Value.Text.Length > 0)
                {
                    var first = Value.Clone();
                    first.Anchor = "";
                    first.Comments.Clear();
                    first.TrailingComments.Clear();
                    list.Items.Add(first);
                }
                SetValue(list);
            }));
        }
        if (field is null)
        {
            content.Children.Add(EditorControls.Button("Use a settings block...", () => SetValue(PresetValue.Map())));
            content.Children.Add(EditorControls.Text("Enter a value, choose a form, or use shared settings or a template. In a template, a parameter supplies a different value for each use."));
        }
    }

    private FormSelection FormSelection() => new(field?.FormTypes,
        field?.MatchPresetCategory == true ? document.Category : null, field?.TriggerOnly == true);

    private void AdoptDocument(PresetDocument updated)
    {
        updated.RebindValue(Value, document);
        document = updated;
    }

    private void ChooseGroup(Action<FormGroupDefinition> use)
    {
        var available = document.PendingGroups.Concat(groups)
            .DistinctBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var picker = new GroupPickerWindow(available, forms, FormSelection()) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedGroup is not { } selected) return;
        document.PendingGroups.AddRange(picker.CreatedGroups);
        use(selected);
    }

    private void ChooseReference()
    {
        capture();
        var dialog = new Window { Owner = this, Title = "Reuse settings", Width = 500, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(EditorControls.Text("Choose a named block", true));
        var available = document.Blocks.Where(block => block.Kind is BlockKind.Shared or BlockKind.Template).ToArray();
        var choices = new ListBox { ItemsSource = available, DisplayMemberPath = nameof(PresetBlock.Name), MinHeight = 170, MaxHeight = 240 };
        panel.Children.Add(choices);
        if (available.Length == 0) panel.Children.Add(EditorControls.Text("Create shared settings or a template from the preset overview first."));
        var use = EditorControls.Button("Use", () =>
        {
            if (choices.SelectedItem is not PresetBlock chosen) return;
            if (chosen.Kind == BlockKind.Shared)
            {
                if (chosen.Value.Anchor.Length == 0)
                {
                    MessageBox.Show(dialog, "This imported block has no shared anchor. Open it from the overview and enable sharing first.");
                    return;
                }
                SetValue(PresetValue.Alias(chosen.Value.Anchor));
            }
            else
            {
                var args = TemplateCall.FindParameters(chosen.Value, document).Select(_ => PresetValue.UnsetArgument());
                SetValue(PresetValue.Scalar(TemplateCall.Format(chosen.Key, args)));
            }
            dialog.DialogResult = true;
        });
        use.Visibility = Visibility.Collapsed;
        choices.SelectionChanged += (_, _) => use.Visibility = choices.SelectedItem is not null ? Visibility.Visible : Visibility.Collapsed;
        EditorControls.Footer(panel, use, EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private void ChooseParameter()
    {
        capture();
        var dialog = new Window { Owner = this, Title = "Template parameter", Width = 470, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(EditorControls.Text("Parameter name", true));
        var entry = new TextBox { Text = Value.Kind == ValueKind.Scalar && Value.Text.StartsWith('$') ? Value.Text[1..] : "" };
        panel.Children.Add(entry);
        var error = EditorControls.Text("Each use of the template supplies a value for this name."); panel.Children.Add(error);
        EditorControls.Footer(panel, EditorControls.Button("Use parameter", () =>
        {
            var parameter = entry.Text.Trim();
            if (!TemplateCall.IsName(parameter)) { error.Text = "Use letters, digits, underscores or hyphens."; return; }
            SetValue(PresetValue.Scalar("$" + parameter)); dialog.DialogResult = true;
        }), EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel; dialog.ShowDialog();
    }

    private void AddSetting()
    {
        var dialog = new Window { Owner = this, Title = "Add setting", Width = 450, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(EditorControls.Text("Setting name")); var entry = new TextBox(); panel.Children.Add(entry);
        EditorControls.Footer(panel, EditorControls.Button("Add", () =>
        {
            var key = entry.Text.Trim();
            if (key.Length == 0 || Value.Fields.ContainsKey(key)) return;
            Value.Set(key, PresetValue.Scalar("")); dialog.DialogResult = true; Render();
        }), EditorControls.Button("Cancel", () => dialog.DialogResult = false));
        dialog.Content = panel; dialog.ShowDialog();
    }

    private void SetValue(PresetValue value)
    {
        value.Anchor = Value.Anchor;
        value.Comments.AddRange(Value.Comments);
        value.TrailingComments.AddRange(Value.TrailingComments);
        Value = value;
        Render();
    }

    public static PresetValue NewValue(string context, PresetField? field = null)
    {
        if (field?.Kind is FieldKind.ObjectList or FieldKind.FormList or FieldKind.IntegerList ||
            context is "Stages" or "stages" or "Transformers" or "transformers" or "TimeModulators" or "modulators" or "MagicEffects" or "effects" or "Values" or "values" or "Rules" or "rules" or "DefaultRules" or "defaultrules" or "CustomRules" or "customrules" or "AddonRules" or "addonrules" or "Inheritance") return PresetValue.List();
        if (field is null && context is not ("Value" or "value" or "")) return PresetValue.Map();
        return PresetValue.Scalar(field?.DefaultText ?? "");
    }

    public static string Summary(PresetValue value, PresetDocument document)
    {
        if (value.IsUnsetArgument) return "Not set";
        if (value.Kind == ValueKind.Alias) return "Uses " + (document.Blocks.FirstOrDefault(block => block.Value.Anchor == value.Text)?.Name ?? value.Text);
        if (value.Kind == ValueKind.Scalar)
        {
            if (value.Tag != "tag:yaml.org,2002:str" && TemplateCall.TryParse(value.Text, out var key, out _))
                return "Uses " + (document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Template && block.Key == key)?.Name ?? key);
            return value.Text.Length == 0 ? "Empty" : value.Text;
        }
        if (value.Kind == ValueKind.Sequence) return value.Items.Count == 0 ? "Empty list" : $"{value.Items.Count} entries";
        return value.Get("name")?.Text is { Length: > 0 } name ? name : $"{value.Fields.Count} settings";
    }
}
