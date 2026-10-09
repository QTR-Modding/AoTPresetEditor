using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace AoTPresetEditor;

public sealed class ValueEditorControl : UserControl
{
    private PresetDocument document;
    private readonly ValueEditorControl root;
    private readonly ValueEditorControl? parent;
    private readonly string? blockId;
    private readonly Action<PresetValue>? replace;
    private readonly IReadOnlyList<FormRow> forms;
    private readonly IReadOnlyList<FormGroupDefinition> groups;
    private readonly bool parametersAllowed;
    private readonly bool behaviorMode;
    private readonly bool allowVariations;
    private readonly Action<string, bool>? editSections;
    private readonly Action<bool>? changeAppliesTo;
    private const string AllItemsPrecedence = "If another preset defines stages for a particular item, that item uses those stages instead.";
    private IReadOnlyList<FormRow>? sourceForms;
    private readonly Func<IEnumerable<FormRow>>? otherStageForms;
    private readonly string context;
    private readonly string title;
    private readonly PresetField? field;
    private readonly StackPanel content = new();
    private readonly TextBox? name;
    private readonly List<(ValueEditorControl Editor, Func<PresetValue?, PresetValue?> Resolve)> children = [];
    private sealed record FieldView(Border Target, UIElement Body, TextBlock Label, TextBlock Status, PresetField Field, Expander Unused);
    private readonly Dictionary<string, FieldView> fields = [];
    private Action capture = () => { };
    private PresetPresentation presentation;
    private PresetValue? effective;
    private TextBlock? resolvedPreview;
    private StackPanel? resolvedDetails;
    private Expander? resolvedSection;
    private Button? independentCopy;
    private Action refreshSummary = () => { };
    private ListBox? entryList;
    private Action? revealEntry;
    private WrapPanel? valueOptions;
    private bool refreshing;
    public PresetValue Value { get; private set; }
    public PresetDocument Document => root.document;
    private bool IsDefinition => !root.behaviorMode && (parametersAllowed || Document.Blocks.Any(block => block.Id == root.blockId && block.Kind == BlockKind.Shared));
    public string BlockName => name?.Text.Trim() ?? "";
    public event EventHandler? Changed;
    public event Func<IReadOnlyList<string>, PresetField, bool, bool>? VariableChangeRequested;

    public ValueEditorControl(PresetDocument document, IReadOnlyList<FormRow> forms, PresetValue value,
        string context, string title, bool parametersAllowed = false, PresetField? field = null, string? blockName = null,
        IReadOnlyList<string>? issues = null, IReadOnlyList<FormGroupDefinition>? groups = null, bool behaviorMode = false, bool allowVariations = true,
        IReadOnlyList<FormRow>? sourceForms = null, bool showHeading = true, Action<string, bool>? editSections = null,
        Action<bool>? changeAppliesTo = null, Func<IEnumerable<FormRow>>? otherStageForms = null)
        : this(document, forms, value, context, title, parametersAllowed, field, blockName, groups ?? [], null, null, behaviorMode, allowVariations, sourceForms, showHeading, editSections, changeAppliesTo, otherStageForms) { }

    private ValueEditorControl(PresetDocument document, IReadOnlyList<FormRow> forms, PresetValue value,
        string context, string title, bool parametersAllowed, PresetField? field, string? blockName,
        IReadOnlyList<FormGroupDefinition> groups, ValueEditorControl? parent, Action<PresetValue>? replace, bool behaviorMode = false, bool allowVariations = true,
        IReadOnlyList<FormRow>? sourceForms = null, bool showHeading = true, Action<string, bool>? editSections = null,
        Action<bool>? changeAppliesTo = null, Func<IEnumerable<FormRow>>? otherStageForms = null)
    {
        this.parent = parent;
        root = parent?.root ?? this;
        this.behaviorMode = parent?.behaviorMode ?? behaviorMode;
        this.allowVariations = parent?.allowVariations ?? allowVariations;
        this.editSections = editSections;
        this.changeAppliesTo = parent is null ? changeAppliesTo : null;
        this.sourceForms = parent?.sourceForms ?? sourceForms;
        this.otherStageForms = otherStageForms;
        blockId = parent is null ? document.Blocks.FirstOrDefault(block => ReferenceEquals(block.Value, value))?.Id : root.blockId;
        this.document = parent is null ? document.Clone() : root.document;
        this.forms = forms; this.groups = groups; this.context = context; this.title = title;
        this.parametersAllowed = parametersAllowed; this.field = field; this.replace = replace;
        Value = parent is null ? value.Clone() : value;
        presentation = parent?.presentation ?? new PresetPresentation(Document, forms, groups);
        var layout = new StackPanel();
        if (parent is null)
        {
            if (blockName is not null)
            {
                var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var caption = EditorControls.Text(title + " name");
                caption.FontWeight = FontWeights.SemiBold;
                caption.Margin = new Thickness(0, 0, 10, 0); caption.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(caption, Dock.Left); header.Children.Add(caption);
                if (field is { Help.Length: > 0 })
                {
                    var info = EditorControls.Info(field.Label, field.Help);
                    DockPanel.SetDock(info, Dock.Left); header.Children.Add(info);
                }
                name = new TextBox { Text = blockName };
                AutomationProperties.SetName(name, title + " name");
                name.TextChanged += (_, _) => NotifyChanged(); header.Children.Add(name); layout.Children.Add(header);
            }
            else if (showHeading)
            {
                var heading = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                var caption = EditorControls.Text(title, true); caption.Margin = new Thickness(0); heading.Children.Add(caption);
                if (field is { Help.Length: > 0 }) heading.Children.Add(EditorControls.Info(field.Label, field.Help));
                layout.Children.Add(heading);
            }
        }
        layout.Children.Add(content); Content = layout; Render();
    }

    public void Capture()
    {
        capture();
        foreach (var child in children) child.Editor.Capture();
    }

    public void UpdateSourceForms(IReadOnlyList<FormRow>? sourceForms) => root.sourceForms = sourceForms;

    public void UpdatePresentation(PresetPresentation updated, PresetValue? effectiveRoot = null)
    {
        presentation = updated; effective = effectiveRoot; RefreshFeedback();
        if (resolvedPreview is not null) resolvedPreview.Text = effective is null ? "Complete the preset to preview the resulting settings."
            : presentation.Summary(effective, context, behaviorMode);
        if (independentCopy is not null) independentCopy.Visibility = effective is null ? Visibility.Collapsed : Visibility.Visible;
        if (resolvedSection is not null) resolvedSection.Visibility = effective is null ? Visibility.Collapsed : Visibility.Visible;
        if (resolvedDetails is not null)
        {
            resolvedDetails.Children.Clear();
            if (effective is not null) DescribeResolved(effective, context, resolvedDetails);
        }
        refreshSummary();
        foreach (var child in children) child.Editor.UpdatePresentation(updated, child.Resolve(effective));
    }

    public bool FocusIssue(string message)
    {
        var path = IssuePath(effective ?? Value, context, Document.Kind, message);
        if (path.Count > 0 && FocusPath(path)) return true;
        var openedEntry = false;
        if (entryList is not null)
        {
            var match = Regex.Match(message, "^(?:" + Regex.Escape(field?.Label ?? "Entry") + @"|Entry) (\d+):?\s*", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number) && number > 0 && number <= Value.Items.Count)
            { entryList.SelectedIndex = number - 1; revealEntry?.Invoke(); openedEntry = true; message = message[match.Length..]; }
            else if (field is not null && entryList.SelectedIndex >= 0 && message.StartsWith(field.Label, StringComparison.OrdinalIgnoreCase))
            { revealEntry?.Invoke(); openedEntry = true; }
        }
        foreach (var (_, view) in fields)
            if (message.StartsWith(view.Field.Label, StringComparison.OrdinalIgnoreCase) || message.StartsWith(view.Field.Key + ":", StringComparison.OrdinalIgnoreCase))
            {
                if (view.Target.Child == view.Unused) { Reveal(view.Unused); return true; }
                var nested = children.FirstOrDefault(child => child.Editor.field?.Key == view.Field.Key).Editor;
                if (nested?.FocusIssue(message) == true) return true;
                Reveal(view.Target); return true;
            }
        foreach (var child in children) if (child.Editor.FocusIssue(message)) return true;
        foreach (var (_, view) in fields)
            if (message.Contains(view.Field.Label, StringComparison.OrdinalIgnoreCase) || message.Contains(view.Field.Key, StringComparison.OrdinalIgnoreCase))
            {
                Reveal(view.Target); return true;
            }
        if (name is not null && message.Contains("name", StringComparison.OrdinalIgnoreCase)) { name.Focus(); return true; }
        return openedEntry;
    }

    internal static IReadOnlyList<string> IssuePath(PresetValue? value, string context, PresetKind kind, string message)
    {
        context = PresetSchema.NormalizeContext(context);
        if (context == "rule" && value?.Get("stages") is { Kind: ValueKind.Sequence } stages)
        {
            var stage = Regex.Match(message, @"^Stage (number )?(\d+)(?::| is used)", RegexOptions.IgnoreCase);
            if (stage.Success && uint.TryParse(stage.Groups[2].Value, out var number))
            {
                var index = stages.Items.FindLastIndex(item => item.Get("no") is { } stageNumber && PresetSchema.TryUnsigned(stageNumber.Text, out var candidate) && candidate == number);
                if (index >= 0) return ["stages", index.ToString(), stage.Groups[1].Success ? "no" : "FormEditorID"];
            }
            if (message.StartsWith("Stage numbers must", StringComparison.OrdinalIgnoreCase)) return ["stages"];
        }
        if (context is "transformer" or "modulator" && Regex.IsMatch(message, @"^stage \d+ is not defined", RegexOptions.IgnoreCase))
            return ["allowed_stages"];
        foreach (var descriptor in PresetSchema.GetFields(context, kind))
        {
            var match = Regex.Match(message, "^(?:" + Regex.Escape(descriptor.Label) + "|" + Regex.Escape(descriptor.Key) + @")(?=$|[:\s])\s*", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var rest = message[match.Length..].TrimStart(':', ' ');
            if (descriptor.Kind == FieldKind.ObjectList)
            {
                var entry = Regex.Match(rest, @"^(\d+):?\s*");
                if (entry.Success && int.TryParse(entry.Groups[1].Value, out var number) && number > 0)
                {
                    var items = value?.Get(descriptor.Key)?.Items;
                    var item = items is not null && number <= items.Count ? items[number - 1] : null;
                    return [descriptor.Key, (number - 1).ToString(), .. IssuePath(item, descriptor.ChildContext, kind, rest[entry.Length..])];
                }
            }
            return [descriptor.Key];
        }
        return [];
    }

    internal static (string? Parameter, int PathLength) IssueParameter(PresetDocument document, PresetValue? value,
        IReadOnlyList<string> path) => IssueParameter(document, value, path, []);

    private static (string? Parameter, int PathLength) IssueParameter(PresetDocument document, PresetValue? value,
        IReadOnlyList<string> path, HashSet<PresetValue> active)
    {
        if (value is null || !active.Add(value)) return (null, 0);
        try
        {
            if (value.Kind == ValueKind.Alias)
                return IssueParameter(document, document.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value))
                    .FirstOrDefault(candidate => candidate.Anchor == value.Text), path, active);
            if (value.Kind == ValueKind.Scalar)
            {
                if (value.Text.StartsWith('$') && TemplateCall.IsName(value.Text[1..])) return (value.Text[1..], 0);
                if (value.Tag != "tag:yaml.org,2002:str" && TemplateCall.TryParse(value.Text, out var name, out var arguments) &&
                    document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Template && block.Key == name) is { } template)
                {
                    var inner = IssueParameter(document, template.Value, path, active);
                    var index = TemplateCall.FindParameters(template.Value, document).ToList().IndexOf(inner.Parameter ?? "");
                    if (index < 0 || index >= arguments.Count) return (null, 0);
                    var bound = IssueParameter(document, arguments[index], path.Skip(inner.PathLength).ToArray(), active);
                    return (bound.Parameter, inner.PathLength + bound.PathLength);
                }
            }
            if (path.Count == 0) return (null, 0);
            if (value.Kind == ValueKind.Mapping && value.Get(path[0]) is null && value.Get("<<") is { } inherited)
                return IssueParameter(document, inherited, path, active);
            if (value.Kind == ValueKind.Sequence && !int.TryParse(path[0], out _))
                return IssueParameter(document, value.Items.FirstOrDefault(item => BehaviorFamily.ResolveValue(document, item, path[0]) is not null), path, active);
            var child = value.Kind == ValueKind.Mapping ? value.Get(path[0])
                : value.Kind == ValueKind.Sequence && int.TryParse(path[0], out var position) && position >= 0 && position < value.Items.Count ? value.Items[position] : null;
            var result = IssueParameter(document, child, path.Skip(1).ToArray(), active);
            return (result.Parameter, result.PathLength + 1);
        }
        finally { active.Remove(value); }
    }

    internal bool FocusPath(IReadOnlyList<string> path)
    {
        if (path.Count == 0) { Reveal(this); SelectFocusedText(); return true; }
        if (IsCall(Value, out var template, out _))
        {
            var parameter = IssueParameter(Document, template!.Value, path);
            var argumentIndex = TemplateCall.FindParameters(template.Value, Document).ToList().IndexOf(parameter.Parameter ?? "");
            if (argumentIndex >= 0 && argumentIndex < children.Count) return children[argumentIndex].Editor.FocusPath(path.Skip(parameter.PathLength).ToArray());
        }
        if (entryList is not null && int.TryParse(path[0], out var index) && index >= 0 && index < Value.Items.Count)
        {
            entryList.SelectedIndex = index; revealEntry?.Invoke();
            return children.LastOrDefault().Editor?.FocusPath(path.Skip(1).ToArray()) == true;
        }
        if (!fields.TryGetValue(path[0], out var view)) return false;
        if (view.Target.Child != view.Unused && path.Count > 1 &&
            children.FirstOrDefault(child => child.Editor.field?.Key == path[0]).Editor is { } nested && nested.FocusPath(path.Skip(1).ToArray())) return true;
        Reveal(view.Target);
        if (view.Target.Child != view.Unused && children.FirstOrDefault(child => child.Editor.field?.Key == path[0]).Editor is { } input)
            input.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        SelectFocusedText();
        return true;
    }

    private static void SelectFocusedText()
    {
        if (Keyboard.FocusedElement is TextBox input) input.SelectAll();
    }

    private static void Reveal(FrameworkElement target)
    {
        for (DependencyObject? current = target; current is not null; current = LogicalTreeHelper.GetParent(current))
            if (current is Expander expander) expander.IsExpanded = true;
        target.UpdateLayout();
        target.BringIntoView(); target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void NotifyChanged()
    {
        if (refreshing) return;
        replace?.Invoke(Value); root.UpdatePresentation(presentation); root.Changed?.Invoke(root, EventArgs.Empty);
    }

    private void Render()
    {
        refreshing = true; capture = () => { }; refreshSummary = () => { }; children.Clear(); fields.Clear(); content.Children.Clear(); resolvedPreview = null; resolvedDetails = null; resolvedSection = null; independentCopy = null; entryList = null; revealEntry = null; valueOptions = null;
        if (parametersAllowed && name is not null && !behaviorMode)
        {
            var parameters = TemplateCall.FindParameters(Value, Document);
            content.Children.Add(Help(parameters.Count == 0 ? "Use a parameter on a field that should vary between uses."
                : "Parameters in call order: " + string.Join(", ", parameters)));
        }
        if (Value.Kind == ValueKind.Alias)
        {
            var source = Document.Blocks.FirstOrDefault(block => block.Value.Anchor == Value.Text);
            content.Children.Add(EditorControls.Text("Uses common settings: " + (source?.Name ?? Value.Text)));
            AddResolvedPreview(); AddReferenceActions(source);
        }
        else if (parametersAllowed && Value.Kind == ValueKind.Scalar && Value.Text.StartsWith('$') && TemplateCall.IsName(Value.Text[1..]))
        {
            content.Children.Add(EditorControls.Text(behaviorMode ? "Varies per item" : "Parameter: " + Value.Text[1..]));
            if (!behaviorMode) content.Children.Add(Help("Each use of this template supplies this value."));
            if (!behaviorMode) content.Children.Add(EditorControls.Button("Use a fixed value", () => SetValue(NewValue(context, field))));
        }
        else if (IsCall(Value, out var template, out var args)) RenderCall(template!, args);
        else if (Value.Kind == ValueKind.Mapping) RenderMap();
        else if (Value.Kind == ValueKind.Sequence) RenderList();
        else RenderScalar();
        var reuse = valueOptions ?? new WrapPanel();
        reuse.Margin = new Thickness(0, 3, 0, 4);
        if (AvailableDefinitions().Any())
            reuse.Children.Add(EditorControls.Button(behaviorMode ? "Reuse settings..." : "Use shared settings or template...", ChooseReference));
        if (parametersAllowed && !behaviorMode) reuse.Children.Add(EditorControls.Button("Use parameter...", ChooseParameter));
        if (reuse.Children.Count > 0) content.Children.Add(new Expander { Header = valueOptions is null ? "Reuse a value" : "More options", Content = reuse });
        refreshing = false; RefreshFeedback();
    }

    private void AddResolvedPreview()
    {
        resolvedPreview = Help(effective is null ? "Complete the preset to preview the resulting settings." : presentation.Summary(effective, context, behaviorMode));
        content.Children.Add(resolvedPreview);
        resolvedDetails = new StackPanel();
        if (effective is not null) DescribeResolved(effective, context, resolvedDetails);
        resolvedSection = new Expander { Header = "Resulting settings", Content = resolvedDetails, Margin = new Thickness(0, 0, 0, 8),
            Visibility = effective is null ? Visibility.Collapsed : Visibility.Visible };
        content.Children.Add(resolvedSection);
    }

    private void DescribeResolved(PresetValue value, string valueContext, Panel target)
    {
        if (value.Kind == ValueKind.Mapping)
        {
            var descriptors = PresetSchema.GetFields(valueContext, Document.Kind);
            foreach (var (key, child) in value.Fields)
            {
                var descriptor = descriptors.FirstOrDefault(item => item.Key == key);
                var label = descriptor?.Label ?? key;
                if (child.Kind is ValueKind.Mapping or ValueKind.Sequence)
                {
                    var nested = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
                    DescribeResolved(child, descriptor?.ChildContext ?? PresetSchema.InferContext(key, child), nested);
                    target.Children.Add(new Expander { Header = label + ": " + presentation.Summary(child, descriptor?.ChildContext ?? "", behaviorMode), Content = nested });
                }
                else target.Children.Add(Help(label + ": " + presentation.Summary(child, descriptor?.ChildContext ?? "", behaviorMode)));
            }
        }
        else if (value.Kind == ValueKind.Sequence)
        {
            for (var index = 0; index < value.Items.Count; index++)
            {
                var item = value.Items[index];
                if (item.Kind != ValueKind.Mapping) { target.Children.Add(Help($"{index + 1}. " + presentation.Summary(item, valueContext, behaviorMode))); continue; }
                var nested = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
                DescribeResolved(item, valueContext, nested);
                target.Children.Add(new Expander { Header = $"{index + 1}. " + presentation.Summary(item, valueContext, behaviorMode), Content = nested });
            }
        }
        else target.Children.Add(Help(presentation.Summary(value, valueContext, behaviorMode)));
    }

    private bool IsCall(PresetValue value, out PresetBlock? template, out List<PresetValue> args)
    {
        template = null; args = [];
        if (value.Kind != ValueKind.Scalar || value.Tag == "tag:yaml.org,2002:str" || !TemplateCall.TryParse(value.Text, out var key, out args)) return false;
        template = Document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Template && block.Key == key); return template is not null;
    }

    private void AddReferenceActions(PresetBlock? source)
    {
        if (source is not null)
        {
            var uses = presentation.Uses(source);
            content.Children.Add(Help("Editing this definition changes every use" + (uses.Count == 0 ? "." : ": " + string.Join(", ", uses.Take(4).Select(block => block.Name)) + (uses.Count > 4 ? $" and {uses.Count - 4} more." : "."))));
            content.Children.Add(EditorControls.Button(behaviorMode ? "Edit common settings..." : "Edit shared definition...", () => EditDefinition(source)));
        }
        independentCopy = EditorControls.Button("Make independent copy for this use", () => { if (effective is not null) SetValue(effective.Clone()); });
        independentCopy.Visibility = effective is null ? Visibility.Collapsed : Visibility.Visible;
        valueOptions ??= new WrapPanel();
        valueOptions.Children.Add(independentCopy);
        valueOptions.Children.Add(EditorControls.Button("Replace with own settings...", () =>
        {
            if (MessageBox.Show(Window.GetWindow(this), "Replace this reference with new independent settings?", title,
                MessageBoxButton.OKCancel) == MessageBoxResult.OK) SetValue(NewValue(context, field));
        }));
    }

    private void EditDefinition(PresetBlock source)
    {
        root.Capture();
        var view = new ValueEditorWindow(Document, forms, source.Value, source.Context, "Common settings: " + source.Name,
            source.Kind == BlockKind.Template, source.Kind == BlockKind.Shared && source.Value.Kind != ValueKind.Mapping ? field : null,
            groups: groups, behaviorMode: behaviorMode) { Owner = Window.GetWindow(this) };
        if (view.ShowDialog() != true) return;
        var updated = view.Document; var previous = updated.Clone(); updated.Blocks.First(block => block.Id == source.Id).Value = view.Value;
        try { updated.RebindTemplateCalls(previous); root.AdoptDocument(updated); root.Render(); root.NotifyChanged(); }
        catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, "Cannot apply definition changes", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RenderCall(PresetBlock template, List<PresetValue> args)
    {
        content.Children.Add(EditorControls.Text((behaviorMode ? "Behavior: " : "Template: ") + template.Name)); content.Children.Add(Help("These values affect this use only."));
        var parameters = TemplateCall.FindParameters(template.Value, Document); var suppliedArguments = args.Count;
        for (var index = 0; index < parameters.Count; index++)
        {
            var position = index; var argumentField = PresetSchema.GetParameterField(template, parameters[index], Document, field, context);
            var argument = index < args.Count ? args[index] : PresetValue.UnsetArgument();
            if (argument.IsUnsetArgument && argumentField?.Kind == FieldKind.ObjectList) argument = NewValue(argumentField.ChildContext, argumentField);
            content.Children.Add(EditorControls.Text(parameters[index]));
            var editor = Child(argument, argumentField?.ChildContext ?? "Value", parameters[index], argumentField, value =>
            {
                if (!TemplateCall.TryParse(Value.Text, out _, out var currentArguments)) return;
                while (currentArguments.Count <= position) currentArguments.Add(PresetValue.UnsetArgument());
                currentArguments[position] = value; Value.Text = TemplateCall.Format(template.Key, currentArguments);
            });
            content.Children.Add(editor);
        }
        if (suppliedArguments != parameters.Count) content.Children.Add(Help(behaviorMode ? "Fill the missing values for this item." : $"This use has {suppliedArguments} arguments; the template needs {parameters.Count}. Fill the missing values."));
        AddResolvedPreview(); AddReferenceActions(template);
    }

    private void RenderMap()
    {
        var descriptors = PresetSchema.GetFields(context, Document.Kind);
        if (CanChangeAppliesTo && Document.Kind == PresetKind.Default) RenderAllItemsTarget();
        if (editSections is not null)
        {
            foreach (var descriptor in descriptors.Where(item => item.Key is "owners" or "forms")) RenderField(descriptor, content);
            var add = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
            foreach (var (key, label) in new[] { ("stages", "Add stages"), ("transformers", "Add transformation"), ("timeModulators", "Add time modifier") })
                if (!HasSection(key) && (key != "stages" || Document.Kind == PresetKind.Addon))
                    add.Children.Add(EditorControls.Button(label, () => editSections(key, true)));
            if (add.Children.Count > 0) content.Children.Add(add);
            foreach (var key in new[] { "stages", "transformers", "timeModulators" })
                if (descriptors.FirstOrDefault(item => item.Key == key) is { } descriptor &&
                    (descriptor.Required || HasSection(key))) RenderField(descriptor, content);
            descriptors = descriptors.Where(item => item.Key is not ("owners" or "forms" or "stages" or "finalFormEditorID" or "transformers" or "timeModulators")).ToArray();
        }
        var primary = descriptors.Where(descriptor => descriptor.Required ||
            descriptor.Kind == FieldKind.ObjectList && Value.Get(descriptor.Key) is not null ||
            PresetSchema.NormalizeContext(context) == "effect").ToArray();
        foreach (var descriptor in primary) RenderField(descriptor, content);
        foreach (var section in descriptors.Except(primary).GroupBy(FieldSection))
        {
            var panel = new StackPanel(); foreach (var descriptor in section) RenderField(descriptor, panel);
            content.Children.Add(new Expander { Header = section.Key, Tag = section.Key, Content = panel, Margin = new Thickness(0, 6, 0, 8) });
        }
        foreach (var key in Value.Fields.Keys.Where(key => key != "<<" && !PresetSchema.GetFields(context, Document.Kind).Any(item => item.Key == key)).ToArray())
            RenderField(new PresetField(key, key, FieldKind.Text, "Additional setting preserved from this file.",
                ChildContext: PresetSchema.InferContext(key, Value.Fields[key])), content);
        if (descriptors.Count == 0) content.Children.Add(EditorControls.Button("Add setting...", AddSetting));
        var inherited = Value.Get("<<");
        if (inherited is null && !AvailableDefinitions().Any()) return;
        var panelReuse = new StackPanel();
        panelReuse.Children.Add(Help(inherited is null ? "Reuse fields from a named definition. Values entered here take precedence."
            : "From " + presentation.ReferenceLabel(inherited, behaviorMode) + ". Values entered here take precedence."));
        var actions = new WrapPanel();
        actions.Children.Add(EditorControls.Button(inherited is null ? "Add..." : "Change...", () =>
        {
            var editor = new ValueEditorWindow(Document, forms, inherited ?? PresetValue.List(), "Inheritance", "Inherited settings", parametersAllowed, groups: groups)
                { Owner = Window.GetWindow(this) };
            if (editor.ShowDialog() != true) return;
            AdoptDocument(editor.Document); Value.Set("<<", editor.Value); Render(); NotifyChanged();
        }));
        if (inherited is not null)
        {
            actions.Children.Add(EditorControls.Button("Remove", () => { Value.Remove("<<"); Render(); NotifyChanged(); }));
            foreach (var alias in PresetDocument.Descendants(inherited).Where(node => node.Kind == ValueKind.Alias))
            {
                var definition = Document.Blocks.FirstOrDefault(block => block.Value.Anchor == alias.Text);
                if (definition is not null) actions.Children.Add(EditorControls.Button("Edit " + definition.Name + "...", () => EditDefinition(definition)));
            }
        }
        panelReuse.Children.Add(actions);
        content.Children.Add(new Expander { Header = "Common settings", IsExpanded = inherited is not null, Content = panelReuse, Margin = new Thickness(0, 10, 0, 0) });
    }

    private PresetValue? BehaviorSetting(string key) => Value.Get(key) ?? effective?.Get(key) ??
        ((editSections is not null || changeAppliesTo is not null) && blockId is not null ? BehaviorFamily.GetSetting(Document, blockId, key) : null);

    private bool HasSection(string key) => BehaviorSetting(key) is { } setting &&
        (key == "stages" || setting.Kind != ValueKind.Sequence || setting.Items.Count > 0);

    private bool CanChangeAppliesTo => parent is null && behaviorMode && changeAppliesTo is not null && this.field is null &&
        Value.Kind == ValueKind.Mapping && PresetSchema.NormalizeContext(context) == "rule";

    private bool IsAppliesToField(PresetField descriptor) => CanChangeAppliesTo && descriptor.Key is "owners" or "forms";

    private bool CanChooseAllItems => Document.Kind != PresetKind.Default && blockId is not null &&
        BehaviorFamily.Find(Document, blockId) is { Rows.Count: <= 1 };

    private void AddAllItemsChoice(Panel target, PresetField? descriptor)
    {
        if (descriptor is not null && IsAppliesToField(descriptor) && CanChooseAllItems)
            target.Children.Add(EditorControls.Button("All " + PresetWorkspace.CategoryLabel(Document.Category).ToLowerInvariant(),
                () => changeAppliesTo!(true)));
    }

    private void RenderAllItemsTarget()
    {
        var box = new StackPanel();
        var caption = new WrapPanel { Margin = new Thickness(0, 0, 8, 2), VerticalAlignment = VerticalAlignment.Center };
        var label = EditorControls.Text("Applies to"); label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0); label.VerticalAlignment = VerticalAlignment.Center;
        caption.Children.Add(label); caption.Children.Add(EditorControls.Info("Applies to", AllItemsPrecedence));
        box.Children.Add(caption);
        var choices = new WrapPanel();
        var selected = EditorControls.Text("All " + PresetWorkspace.CategoryLabel(Document.Category).ToLowerInvariant());
        selected.Margin = new Thickness(0, 0, 8, 0); selected.VerticalAlignment = VerticalAlignment.Center;
        choices.Children.Add(selected);
        choices.Children.Add(EditorControls.Button("Choose forms...", () => changeAppliesTo!(false)));
        box.Children.Add(choices);
        var card = new Border { Child = box, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6) };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        content.Children.Add(card);
    }

    private static string FieldSection(PresetField descriptor) => descriptor.Key switch
    {
        "color" or "sound" or "art_object" or "effect_shader" => "World effects",
        "value" or "weight" or "crafting_allowed" or "mgeffect" or "FormEditorID" => "Item properties",
        "containers" or "allowed_stages" => "Filters", _ => "Additional behavior"
    };

    private void RenderField(PresetField descriptor, Panel target)
    {
        var current = Value.Get(descriptor.Key); var box = new StackPanel(); var row = new DockPanel();
        var label = EditorControls.Text(descriptor.Label); label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0); label.VerticalAlignment = VerticalAlignment.Center;
        var caption = new WrapPanel { Margin = new Thickness(0, 0, 8, 2), VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(label);
        if (descriptor.Help.Length > 0)
        {
            var help = PresetSchema.FieldHelp(descriptor, context, Document.Kind);
            if (IsAppliesToField(descriptor) && CanChooseAllItems) help += "\n\n" + AllItemsPrecedence;
            caption.Children.Add(EditorControls.Info(descriptor.Label, help));
        }
        var actions = new WrapPanel();
        var variable = behaviorMode && allowVariations && IsVariable(current);
        var canVary = behaviorMode && allowVariations && descriptor.Key is not ("<<" or "stages") &&
            (current is null || !PresetDocument.Descendants(current).Any(value => value.Anchor.Length > 0));
        var hasEditor = !(variable && canVary) && (current is not null || descriptor.Required && Value.Get("<<") is null);
        if (canVary)
        {
            var choice = new CheckBox { Content = "Variable", IsChecked = variable, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0) };
            AutomationProperties.SetName(choice, "Variable: " + descriptor.Label);
            choice.Click += (_, _) => RequestVariable(descriptor, choice, variable);
            actions.Children.Add(choice);
        }
        if (!hasEditor || IsVariable(current) || current?.Kind == ValueKind.Alias || current is not null && IsCall(current, out _, out _))
            AddAllItemsChoice(actions, descriptor);
        var section = editSections is not null && descriptor.Key is "stages" or "transformers" or "timeModulators";
        if (section)
        {
            if (current is null && BehaviorSetting(descriptor.Key) is not null)
                actions.Children.Add(EditorControls.Button("Override here", () => editSections!(descriptor.Key, true)));
            actions.Children.Add(EditorControls.Button("Remove section", () => editSections!(descriptor.Key, false)));
        }
        else if (!variable && current is not null && (!descriptor.Required || Value.Get("<<") is not null)) actions.Children.Add(EditorControls.Button(Value.Get("<<") is null ? "Clear" : "Use inherited value", () =>
            ReplaceField(descriptor, target, null)));
        else if (!variable && (!descriptor.Required || Value.Get("<<") is not null))
            actions.Children.Add(EditorControls.Button(Value.Get("<<") is null ? "Set" : "Override here", () =>
            {
                var replacement = effective?.Get(descriptor.Key)?.Clone() ?? NewValue(descriptor.ChildContext, descriptor); replacement.Anchor = "";
                ReplaceField(descriptor, target, replacement);
            }));
        DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions); row.Children.Add(caption); box.Children.Add(row);
        var status = Help(""); box.Children.Add(status);
        if (hasEditor)
        {
            var editable = current ?? NewValue(descriptor.ChildContext, descriptor);
            if (current is null && editable.Kind == ValueKind.Scalar) editable.Text = "";
            var child = Child(editable, descriptor.ChildContext, descriptor.Label, descriptor,
                value => Value.Set(descriptor.Key, value), resolved => resolved?.Get(descriptor.Key));
            box.Children.Add(child);
        }
        if (section && descriptor.Key == "stages")
            RenderField(PresetSchema.GetFields(context, Document.Kind).Single(item => item.Key == "finalFormEditorID"), box);
        var collection = descriptor.Kind == FieldKind.ObjectList;
        var card = new Border { Child = box, BorderThickness = collection ? new Thickness(0, 0, 0, 1) : new Thickness(1),
            CornerRadius = new CornerRadius(collection ? 0 : 5), Padding = collection ? new Thickness(0, 6, 0, 6) : new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6) };
        if (!collection) card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        if (!(variable && canVary) && descriptor.Kind is (FieldKind.ObjectList or FieldKind.FormList or FieldKind.IntegerList))
        {
            box.Children.Remove(row);
            card.Child = new Expander { Header = row, Content = box, IsExpanded = !section };
        }
        var unused = new StackPanel();
        unused.Children.Add(Help(""));
        if (current is not null) unused.Children.Add(EditorControls.Button("Remove setting", () => ReplaceField(descriptor, target, null)));
        fields[descriptor.Key] = new(card, card.Child, label, status, descriptor,
            new Expander { Header = "Unused: " + descriptor.Label, Content = unused });
        target.Children.Add(card);
    }

    private bool IsVariable(PresetValue? value) => parametersAllowed && value is { Kind: ValueKind.Scalar } &&
        value.Text.StartsWith('$') && TemplateCall.IsName(value.Text[1..]);

    private void RequestVariable(PresetField descriptor, CheckBox choice, bool previous)
    {
        var enabled = choice.IsChecked == true;
        choice.IsChecked = previous;
        if (enabled == previous) return;
        root.Capture();
        var path = new List<string>();
        if (!FindPath(root.Value, Value, path)) return;
        path.Add(descriptor.Key);
        if (root.VariableChangeRequested?.Invoke(path, descriptor, enabled) == true) choice.IsChecked = enabled;
    }

    private static bool FindPath(PresetValue node, PresetValue target, List<string> path)
    {
        if (ReferenceEquals(node, target)) return true;
        var children = node.Kind == ValueKind.Mapping ? node.Fields.AsEnumerable()
            : node.Items.Select((value, index) => new KeyValuePair<string, PresetValue>(index.ToString(System.Globalization.CultureInfo.InvariantCulture), value));
        foreach (var (key, child) in children)
        {
            path.Add(key);
            if (FindPath(child, target, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    private void ReplaceField(PresetField descriptor, Panel target, PresetValue? replacement)
    {
        if (replacement is null) Value.Remove(descriptor.Key); else Value.Set(descriptor.Key, replacement);
        var previous = fields[descriptor.Key].Target;
        var position = target.Children.IndexOf(previous);
        children.RemoveAll(child => child.Editor.field?.Key == descriptor.Key);
        target.Children.Remove(previous);
        RenderField(descriptor, target);
        var updated = fields[descriptor.Key].Target;
        target.Children.Remove(updated); target.Children.Insert(position, updated);
        NotifyChanged();
    }

    private void RefreshFeedback()
    {
        var partial = IsDefinition;
        foreach (var (key, view) in fields)
        {
            var own = Value.Get(key); var inherited = effective?.Get(key) ?? (editSections is not null ? BehaviorSetting(key) : null); string text; IReadOnlyList<string> errors = [];
            var inactive = PresetSchema.InactiveReason(view.Field, Value, context, Document.Category, effective, partial);
            view.Target.Visibility = inactive is not null && own is null && inherited is null ? Visibility.Collapsed : Visibility.Visible;
            if (inactive is not null)
            {
                if (IsVariable(own))
                {
                    view.Target.Child = view.Body; view.Status.Text = inactive; view.Status.Visibility = Visibility.Visible;
                    continue;
                }
                ((TextBlock)((StackPanel)view.Unused.Content).Children[0]).Text = inactive;
                view.Target.Child = view.Unused;
                continue;
            }
            view.Target.Child = view.Body;
            if (own is not null)
            {
                errors = PresetSchema.ValidateField(own, view.Field, context, Document.Category, Document.Kind,
                    partial: parametersAllowed || Value.Get("<<") is not null || IsReference(own));
                text = errors.Count > 0 ? string.Join(Environment.NewLine, errors)
                    : own.Kind == ValueKind.Scalar && view.Field.Kind is FieldKind.Form or FieldKind.FormList
                        ? behaviorMode && IsVariable(own) ? "" : presentation.FormLabel(own.Text, true) : "";
            }
            else if (inherited is not null && Value.Get("<<") is { } source) text = presentation.Summary(inherited, view.Field.ChildContext, behaviorMode) + " · from " + presentation.ReferenceLabel(source, behaviorMode);
            else if (Value.Get("<<") is not null && effective is null) text = "Inherited value will appear after validation.";
            else text = (view.Field.Required ? "Incomplete: " : "") + PresetSchema.DefaultDescription(view.Field, context, Document.Kind);
            view.Status.Text = text; view.Status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            view.Status.SetResourceReference(TextBlock.ForegroundProperty, errors.Count > 0 || own is null && inherited is null && view.Field.Required && (Value.Get("<<") is null || effective is not null)
                ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
            if (view.Target is Border { Child: Expander { Header: DockPanel } })
            {
                var label = view.Label;
                var value = own ?? inherited;
                var incomplete = errors.Count > 0 || own is null && inherited is null && view.Field.Required;
                label.Text = view.Field.Label + (value?.Kind == ValueKind.Sequence ? $" ({value.Items.Count})" : "") + (incomplete ? " · Needs attention" : "");
                label.SetResourceReference(TextBlock.ForegroundProperty, incomplete ? "SystemFillColorCautionBrush" : "TextFillColorPrimaryBrush");
            }
        }
        foreach (var section in content.Children.OfType<Expander>().Where(section => section.Tag is string))
        {
            var category = (string)section.Tag;
            var members = fields.Values.Where(view => ReferenceEquals(view.Target.Parent, section.Content) && view.Target.Visibility == Visibility.Visible).ToArray();
            var count = members.Count(view => Value.Get(view.Field.Key) is not null);
            section.Header = category + (count > 0 ? $" ({count} set)" : "");
            section.Visibility = members.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void RenderList()
    {
        var compact = field?.Kind is FieldKind.FormList or FieldKind.IntegerList ||
            field?.Kind != FieldKind.ObjectList && context != "Inheritance" && PresetSchema.GetFields(ItemContext(), Document.Kind).Count == 0 &&
            Value.Items.All(item => item.Kind is ValueKind.Scalar or ValueKind.Alias);
        var list = new ListBox { MaxHeight = 160, Margin = new Thickness(0, 0, 0, 4) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        entryList = list;
        var details = new StackPanel { Margin = new Thickness(0, 6, 0, 4) }; ValueEditorControl? activeEditor = null; var actions = new WrapPanel();
        var remove = new Button { Content = "Remove" };
        var up = new Button { Content = "Move up" };
        var down = new Button { Content = "Move down" };
        var edit = new Button { Content = "Edit entry" };
        var createGroup = EditorControls.Button("Create group from list...", CreateGroupFromList);
        void RefreshList(int selected)
        {
            list.ItemsSource = Value.Items.Select((item, index) => new ListBoxItem
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock { Text = ListEntrySummary(item, index), TextWrapping = TextWrapping.Wrap }
            }).ToArray();
            list.Visibility = Value.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            list.SelectedIndex = selected;
        }
        refreshSummary = () =>
        {
            createGroup.Visibility = GroupMembersFromList() is null ? Visibility.Collapsed : Visibility.Visible;
            for (var index = 0; index < Math.Min(Value.Items.Count, list.Items.Count); index++)
                ((TextBlock)((ListBoxItem)list.Items[index]).Content).Text = effective is { Kind: ValueKind.Sequence } && index < effective.Items.Count
                    ? ListEntrySummary(effective.Items[index], index, resolved: true) : ListEntrySummary(Value.Items[index], index);
            if (!compact && list.SelectedIndex >= 0 && details.Children.OfType<TextBlock>().FirstOrDefault() is { } caption)
                caption.Text = ItemHeading(list.SelectedIndex);
        };
        void SelectEntry()
        {
            if (activeEditor is not null) { activeEditor.Capture(); children.RemoveAll(pair => pair.Editor == activeEditor); }
            details.Children.Clear(); details.Visibility = Visibility.Collapsed;
            activeEditor = null; var index = list.SelectedIndex; if (index < 0) return;
            var label = compact ? field?.Kind switch { FieldKind.FormList => "Identifier", FieldKind.IntegerList => "Number", _ => "Entry" }
                : ItemHeading(index);
            var caption = EditorControls.Text(label);
            caption.FontWeight = FontWeights.SemiBold; caption.Margin = new Thickness(0, 0, 0, 4);
            details.Children.Add(caption);
            activeEditor = Child(Value.Items[index], ItemContext(), label, ItemField(), value => Value.Items[index] = value,
                resolved => resolved?.Kind == ValueKind.Sequence && index < resolved.Items.Count ? resolved.Items[index] : null);
            details.Children.Add(activeEditor);
        }
        var addLabel = field?.Kind switch { FieldKind.FormList => "Add by identifier", FieldKind.IntegerList => "Add number", _ => "Add " + ItemLabel().ToLowerInvariant() };
        var add = EditorControls.Button(addLabel, () =>
        { Capture(); Value.Items.Add(NewListItem()); RefreshList(Value.Items.Count - 1); ShowEntry(); NotifyChanged(); });
        if (field?.Kind == FieldKind.FormList) actions.Children.Add(EditorControls.Button("Choose forms...", () =>
        {
            var picker = new FormPickerWindow(forms, multiple: true, selection: FormSelection(), purpose: "Choose " + title.ToLowerInvariant()) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() != true) return;
            foreach (var form in picker.SelectedForms)
                if (!Value.Items.Any(item => item.Kind == ValueKind.Scalar && item.Text == form.Identifier)) Value.Items.Add(PresetValue.Scalar(form.Identifier));
            RefreshList(Value.Items.Count - 1); NotifyChanged();
        }));
        if (parent == root) root.AddAllItemsChoice(actions, field);
        if (field?.AllowsGroups == true) actions.Children.Add(EditorControls.Button("Choose form group...", () => ChooseGroup(group =>
        {
            if (!Value.Items.Any(item => item.Kind == ValueKind.Scalar && item.Text == group.Name)) Value.Items.Add(PresetValue.Scalar(group.Name));
            RefreshList(Value.Items.Count - 1); NotifyChanged();
        })));
        actions.Children.Add(add);
        actions.Children.Add(createGroup);
        remove.Click += (_, _) =>
        {
            var index = list.SelectedIndex; if (index < 0) return;
            activeEditor?.Capture(); if (activeEditor is not null) children.RemoveAll(pair => pair.Editor == activeEditor); activeEditor = null;
            Value.Items.RemoveAt(index); RefreshList(Math.Min(index, Value.Items.Count - 1)); NotifyChanged();
        };
        up.Click += (_, _) => MoveItem(-1); down.Click += (_, _) => MoveItem(1);
        edit.Click += (_, _) =>
        {
            if (details.Visibility == Visibility.Visible) { activeEditor?.Capture(); details.Visibility = Visibility.Collapsed; ShowActions(); }
            else ShowEntry();
        };
        actions.Children.Add(edit); actions.Children.Add(remove); actions.Children.Add(up); actions.Children.Add(down);
        void ShowActions()
        {
            createGroup.Visibility = GroupMembersFromList() is null ? Visibility.Collapsed : Visibility.Visible;
            edit.Visibility = list.SelectedIndex >= 0 ? Visibility.Visible : Visibility.Collapsed;
            edit.Content = details.Visibility == Visibility.Visible ? "Hide entry" : "Edit entry";
            remove.Visibility = list.SelectedIndex >= 0 ? Visibility.Visible : Visibility.Collapsed;
            var canReorder = !ItemContext().Equals("stage", StringComparison.OrdinalIgnoreCase);
            up.Visibility = canReorder && list.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
            down.Visibility = canReorder && list.SelectedIndex >= 0 && list.SelectedIndex < Value.Items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        void ShowEntry()
        {
            if (list.SelectedIndex < 0) return;
            details.Visibility = Visibility.Visible; ShowActions();
            Reveal(details);
            activeEditor?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
        revealEntry = ShowEntry;
        void MoveItem(int direction)
        {
            var index = list.SelectedIndex; if (index < 0 || index + direction < 0 || index + direction >= Value.Items.Count) return;
            activeEditor?.Capture(); if (activeEditor is not null) children.RemoveAll(pair => pair.Editor == activeEditor); activeEditor = null;
            (Value.Items[index], Value.Items[index + direction]) = (Value.Items[index + direction], Value.Items[index]);
            RefreshList(index + direction); NotifyChanged();
        }
        list.SelectionChanged += (_, _) => { SelectEntry(); ShowActions(); };
        list.MouseDoubleClick += (_, _) => ShowEntry();
        content.Children.Add(list); content.Children.Add(actions); content.Children.Add(details);
        RefreshList(Value.Items.Count > 0 ? 0 : -1); ShowActions();
        if (context == "Inheritance") content.Children.Add(Help("Earlier settings take precedence. Values entered on the containing block override inherited values."));
        else if (ItemContext() is "transformer" or "modulator") content.Children.Insert(0, Help("Checked from top to bottom. The first matching trigger wins."));
    }

    private PresetField? ItemField() => field?.Kind switch
    { FieldKind.FormList => field with { Kind = FieldKind.Form }, FieldKind.IntegerList => field with { Kind = FieldKind.Integer }, _ => null };
    private string ItemContext() => context.ToLowerInvariant() switch
    {
        "stages" => "stage", "transformers" => "transformer", "timemodulators" or "modulators" => "modulator", "magiceffects" or "effects" => "effect",
        "values" => "Value", "rules" => PresetDocument.RuleContext(Document.Kind), "defaultrules" => "DefaultRule", "customrules" => "CustomRule", "addonrules" => "AddonRule", _ => field?.ChildContext ?? context
    };
    private string ItemLabel() => ItemContext().ToLowerInvariant() switch
    { "stage" => "Stage", "transformer" => "Transformation", "modulator" => "Time modifier", "effect" => "Magic effect", _ => "Value" };

    private string ItemHeading(int index)
    {
        var resolved = effective is { Kind: ValueKind.Sequence } && index < effective.Items.Count;
        var item = resolved ? effective!.Items[index] : Value.Items[index];
        return ItemContext().Equals("stage", StringComparison.OrdinalIgnoreCase)
            ? "Stage " + (item.Get("no") is { } number ? presentation.Summary(number, behaviorMode: behaviorMode, parametersAllowed: parametersAllowed && !resolved) : "?")
            : ItemLabel() + " " + (index + 1);
    }

    private string ListEntrySummary(PresetValue item, int index, bool resolved = false) =>
        (ItemContext().Equals("stage", StringComparison.OrdinalIgnoreCase) ? "" : $"{index + 1}. ") +
        presentation.Summary(item, ItemContext(), behaviorMode, parametersAllowed && !resolved);
    private PresetValue NewListItem()
    {
        var child = ItemContext(); if (context == "Inheritance") return PresetValue.Map();
        var value = PresetSchema.GetFields(child, Document.Kind).Count > 0 ? PresetValue.Map() : PresetValue.Scalar("");
        if (child.Equals("stage", StringComparison.OrdinalIgnoreCase))
            return NewStage(NextStageNumber());
        return value;
    }

    public static PresetValue NewStage(uint number)
    {
        var stage = PresetValue.Map();
        stage.Set("no", PresetValue.Scalar(number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        stage.Set("name", new() { Kind = ValueKind.Scalar, Text = "", Style = ScalarStyle.DoubleQuoted });
        stage.Set("duration", PresetValue.Scalar("1"));
        return stage;
    }

    private uint NextStageNumber()
    {
        var used = new HashSet<uint>();
        for (var index = 0; index < Value.Items.Count; index++)
        {
            if (Value.Items[index].Get("no") is { Kind: ValueKind.Scalar } number && PresetSchema.TryUnsigned(number.Text, out var parsed))
                used.Add(parsed);
            else if (effective is { Kind: ValueKind.Sequence } && index < effective.Items.Count &&
                effective.Items[index].Get("no") is { Kind: ValueKind.Scalar } resolved && PresetSchema.TryUnsigned(resolved.Text, out parsed))
                used.Add(parsed);
        }
        uint next = 0;
        while (used.Contains(next)) next++;
        return next;
    }

    private ValueEditorControl Child(PresetValue value, string childContext, string childTitle, PresetField? childField,
        Action<PresetValue> assign, Func<PresetValue?, PresetValue?>? resolve = null)
    {
        var editor = new ValueEditorControl(Document, forms, value, childContext, childTitle, parametersAllowed, childField, null, groups, this,
            replacement => { assign(replacement); replace?.Invoke(Value); });
        children.Add((editor, resolve ?? (_ => null))); editor.UpdatePresentation(presentation, resolve?.Invoke(effective)); return editor;
    }

    private void RenderScalar()
    {
        if (field?.Kind == FieldKind.Boolean)
        {
            var choice = new CheckBox { Content = "Enabled", IsChecked = Value.IsUnsetArgument ? null : Value.Text.ToLowerInvariant() is "true" or "yes" or "y" or "on", Margin = new Thickness(0, 6, 0, 8) };
            AutomationProperties.SetName(choice, field.Label);
            capture = () => { if (choice.IsChecked is null) return; Value.Text = choice.IsChecked == true ? "true" : "false"; Value.Style = ScalarStyle.Plain; Value.Tag = ""; };
            choice.Checked += (_, _) => { capture(); NotifyChanged(); }; choice.Unchecked += (_, _) => { capture(); NotifyChanged(); };
            content.Children.Add(choice); return;
        }
        var entry = new TextBox { Text = Value.IsNull ? "" : Value.Text, Margin = new Thickness(0, 0, 0, 4), MinWidth = 80 };
        capture = () =>
        {
            if ((Value.IsNull ? "" : Value.Text) == entry.Text) return;
            Value.Text = entry.Text; Value.Tag = ""; Value.Style = entry.Text.Length == 0 ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain;
        };
        entry.TextChanged += (_, _) => { capture(); NotifyChanged(); };
        var choices = new WrapPanel();
        if (field?.Kind == FieldKind.Color) choices.Children.Add(TintPickerWindow.CreateButton(entry));
        var soundPreview = field?.FormTypes?.Contains("SoundDescriptor") == true ? new SoundPreview() : null;
        valueOptions = new WrapPanel();
        if (Value.IsUnsetArgument && field?.Kind == FieldKind.Text)
            valueOptions.Children.Add(EditorControls.Button("Use empty text", () => SetValue(new PresetValue { Kind = ValueKind.Scalar, Style = ScalarStyle.DoubleQuoted })));
        if (field?.Kind is FieldKind.Form or FieldKind.FormList) choices.Children.Add(EditorControls.Button("Choose form...", () =>
        {
            soundPreview?.Stop();
            var picker = new FormPickerWindow(forms, selection: FormSelection(), purpose: "Choose " + title.ToLowerInvariant()) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() == true && picker.SelectedForms.FirstOrDefault() is { } form) entry.Text = form.Identifier;
        }));
        if (parent == root) root.AddAllItemsChoice(choices, field);
        if (field?.AllowsGroups == true) choices.Children.Add(EditorControls.Button("Choose form group...", () => ChooseGroup(group => entry.Text = group.Name)));
        content.Children.Add(choices.Children.Count == 0 ? entry : FormEntryRow(entry, choices));
        if (soundPreview is not null)
        {
            void UpdateSound()
            {
                var form = presentation.ResolveForm(entry.Text);
                soundPreview.Visibility = form?.Type == "SoundDescriptor" ? Visibility.Visible : Visibility.Collapsed;
                soundPreview.SetSamples(form?.Assets, form?.SoundFiles ?? []);
            }
            UpdateSound();
            entry.TextChanged += (_, _) => UpdateSound();
            content.Children.Add(soundPreview);
        }
        if (field?.Kind is FieldKind.FormList or FieldKind.IntegerList || field is null) valueOptions.Children.Add(EditorControls.Button("Use a list of values...", () =>
        {
            Capture(); var list = PresetValue.List();
            if (Value.Text.Length > 0)
            {
                var first = Value.Clone(); first.Anchor = ""; first.Comments.Clear(); first.TrailingComments.Clear(); first.InlineComment = ""; list.Items.Add(first);
            }
            SetValue(list);
        }));
        if (field is null) valueOptions.Children.Add(EditorControls.Button("Use a settings block...", () => SetValue(PresetValue.Map())));
    }

    private static Grid FormEntryRow(TextBox entry, WrapPanel choices)
    {
        const double minimumInlineWidth = 520;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(choices, 1);
        row.Children.Add(entry); row.Children.Add(choices);
        row.SizeChanged += (_, args) =>
        {
            var inline = args.NewSize.Width >= minimumInlineWidth;
            Grid.SetRow(choices, inline ? 0 : 1);
            Grid.SetColumn(choices, inline ? 1 : 0);
            Grid.SetColumnSpan(choices, inline ? 1 : 2);
            Grid.SetColumnSpan(entry, inline ? 1 : 2);
        };
        return row;
    }

    private FormSelection FormSelection()
    {
        var sources = new HashSet<FormRow>();
        var excluded = new HashSet<FormRow>();
        if (field?.MatchSourceType == true && !IsDefinition)
        {
            sources.UnionWith(root.sourceForms ?? []);
            excluded.UnionWith(root.otherStageForms?.Invoke() ?? []);
            for (var ancestor = this; ancestor is not null; ancestor = ancestor.parent)
            {
                var normalized = PresetSchema.NormalizeContext(ancestor.context);
                if (normalized is "stage" or "stages" && (ancestor.Value.Kind == ValueKind.Sequence || ancestor.field?.Kind == FieldKind.ObjectList))
                    excluded.UnionWith(StageItemForms(Document, presentation, ancestor.Value,
                        index => ancestor.EditsPath([index.ToString(), "FormEditorID"], this)));
                if (normalized != "rule") continue;
                sources.UnionWith(RuleSourceForms(Document, presentation, ancestor.Value));
                var stages = BehaviorFamily.ResolveValue(Document, ancestor.Value, "stages") ?? ancestor.effective?.Get("stages");
                excluded.UnionWith(StageItemForms(Document, presentation, stages,
                    index => ancestor.EditsPath(["stages", index.ToString(), "FormEditorID"], this)));
                break;
            }
        }
        return AoTPresetEditor.FormSelection.ForField(field, Document.Category, sources, excluded);
    }

    internal static IReadOnlyList<FormRow> RuleSourceForms(PresetDocument document, PresetPresentation presentation, PresetValue rule)
    {
        var selection = BehaviorFamily.ResolveValue(document, rule, "owners") ?? BehaviorFamily.ResolveValue(document, rule, "forms");
        if (selection is null || BehaviorFamily.ResolveValue(document, selection) is not { } resolved) return [];
        var values = resolved.Kind == ValueKind.Sequence ? resolved.Items : [resolved];
        var items = PresetValue.List();
        items.Items.AddRange(values.Select(value => BehaviorFamily.ResolveValue(document, value)).OfType<PresetValue>());
        var source = PresetValue.Map(); source.Set("owners", items);
        return presentation.SourceForms(source).Where(new FormSelection(Category: document.Category).Matches).ToArray();
    }
    internal static IEnumerable<FormRow> StageItemForms(PresetDocument document, PresetPresentation presentation,
        PresetValue? value, Func<int, bool> editing)
    {
        if (value is null || BehaviorFamily.ResolveValue(document, value) is not { Kind: ValueKind.Sequence } stages) yield break;
        for (var index = 0; index < stages.Items.Count; index++)
        {
            if (editing(index)) continue;
            var stage = stages.Items[index];
            var number = BehaviorFamily.ResolveValue(document, stage, "no");
            if (number is not null && BehaviorFamily.ResolveValue(document, number) is { } resolvedNumber &&
                PresetSchema.TryUnsigned(resolvedNumber.Text, out var stageNumber) && stageNumber == 0) continue;
            var item = BehaviorFamily.ResolveValue(document, stage, "FormEditorID");
            if (item is not null && BehaviorFamily.ResolveValue(document, item) is { Kind: ValueKind.Scalar } resolved &&
                presentation.ResolveForm(resolved.Text) is { } form) yield return form;
        }
    }

    private bool EditsPath(IReadOnlyList<string> path, ValueEditorControl editor)
    {
        if (ReferenceEquals(this, editor)) return true;
        if (IsCall(Value, out var template, out _))
        {
            var parameter = IssueParameter(Document, template!.Value, path);
            var argument = children.FirstOrDefault(child => child.Editor.field?.Key == parameter.Parameter).Editor;
            return argument?.EditsPath(path.Skip(parameter.PathLength).ToArray(), editor) == true;
        }
        if (path.Count == 0) return false;
        var child = Value.Kind == ValueKind.Mapping
            ? children.FirstOrDefault(child => child.Editor.field?.Key == path[0]).Editor
            : int.TryParse(path[0], out var index) && index >= 0 && index < Value.Items.Count
                ? children.FirstOrDefault(child => ReferenceEquals(child.Editor.Value, Value.Items[index])).Editor : null;
        return child?.EditsPath(path.Skip(1).ToArray(), editor) == true;
    }

    private void AdoptDocument(PresetDocument updated)
    {
        updated.RebindValue(root.Value, root.document); root.document = updated; root.presentation = new PresetPresentation(updated, forms, groups);
    }
    private IReadOnlyList<string>? GroupMembersFromList()
    {
        if (field?.AllowsGroups != true || Value.Kind != ValueKind.Sequence || Value.Items.Count == 0) return null;
        var members = new List<string>();
        foreach (var item in Value.Items)
        {
            if (item.Kind != ValueKind.Scalar || item.IsNull || string.IsNullOrWhiteSpace(item.Text) || IsReference(item) ||
                item.Tag is not ("" or "tag:yaml.org,2002:str") ||
                item.Anchor.Length != 0 || item.Comments.Count != 0 || item.TrailingComments.Count != 0 || item.InlineComment.Length != 0 ||
                presentation.ResolveGroup(item.Text) is not null) return null;
            var form = presentation.ResolveForm(item.Text);
            if (form is null && !PresetPresentation.TryNormalizeIdentifier(item.Text, out _)) return null;
            members.Add(form?.Identifier ?? item.Text);
        }
        return members.Distinct(StringComparer.Ordinal).ToArray();
    }

    private void CreateGroupFromList()
    {
        Capture();
        if (GroupMembersFromList() is not { } members) return;
        if (field?.Key == "owners" && Value.Items.Any(item =>
        {
            var form = presentation.ResolveForm(item.Text);
            return forms.Any(other => other != form && presentation.MatchesOwner(item.Text, other));
        }))
        {
            MessageBox.Show(Window.GetWindow(this), "This list also selects items by name. Choose the individual items first, then create the group.",
                "Choose individual items", MessageBoxButton.OK);
            return;
        }
        var available = Document.PendingGroups.Concat(groups).DistinctBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var editor = new GroupEditorWindow(available, forms, FormSelection(), members) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Group is not { } group) return;
        Document.PendingGroups.Add(group);
        SetValue(PresetValue.Scalar(group.Name));
    }
    private void ChooseGroup(Action<FormGroupDefinition> use)
    {
        var available = Document.PendingGroups.Concat(groups).DistinctBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var picker = new GroupPickerWindow(available, forms, FormSelection(), purpose: "Choose a form group for " + title.ToLowerInvariant()) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.SelectedGroup is not { } selected) return;
        Document.PendingGroups.AddRange(picker.CreatedGroups); use(selected);
    }
    private void ChooseReference()
    {
        Capture();
        var dialog = new Window { Owner = Window.GetWindow(this), Title = "Reuse settings", Width = 500, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var heading = EditorControls.Text("Choose a named block", true);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var available = AvailableDefinitions().ToArray();
        var choices = new ListBox();
        foreach (var definition in available)
        {
            var description = new StackPanel();
            description.Children.Add(EditorControls.Text(definition.Name + (behaviorMode ? "" : definition.Kind == BlockKind.Template ? " · Template" : " · Shared settings")));
            var resolved = presentation.EffectiveValue(definition);
            description.Children.Add(Help(presentation.Summary(resolved ?? definition.Value, definition.Context, behaviorMode,
                parametersAllowed: resolved is null && definition.Kind == BlockKind.Template)));
            choices.Items.Add(new ListBoxItem { Content = description, Tag = definition });
        }
        var use = EditorControls.Button("Use", () =>
        {
            if ((choices.SelectedItem as ListBoxItem)?.Tag is not PresetBlock chosen) return;
            UseDefinition(chosen);
            dialog.DialogResult = true;
        });
        use.Visibility = Visibility.Collapsed; choices.SelectionChanged += (_, _) => use.Visibility = choices.SelectedItem is not null ? Visibility.Visible : Visibility.Collapsed;
        var footer = EditorControls.Footer(panel, use, EditorControls.Button("Cancel", () => dialog.DialogResult = false, isCancel: true));
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(choices);
        dialog.Content = panel; dialog.ShowDialog();
    }
    private IEnumerable<PresetBlock> AvailableDefinitions()
    {
        IsCall(Value, out var currentTemplate, out _);
        return Document.Blocks.Where(block => block.Kind is BlockKind.Shared or BlockKind.Template && block.Id != root.blockId &&
            block.Id != currentTemplate?.Id && !(Value.Kind == ValueKind.Alias && block.Kind == BlockKind.Shared && block.Value.Anchor == Value.Text) &&
            CanReuse(block));
    }

    private bool CanReuse(PresetBlock definition)
    {
        var value = presentation.EffectiveValue(definition) ?? definition.Value;
        if (IsReference(value)) return true;
        if (field is not null) return field.Kind switch
        {
            FieldKind.ObjectList => value.Kind == ValueKind.Sequence,
            FieldKind.FormList or FieldKind.IntegerList => value.Kind is ValueKind.Scalar or ValueKind.Sequence,
            _ => value.Kind == ValueKind.Scalar
        };
        if (PresetSchema.GetFields(context, Document.Kind).Count > 0) return value.Kind == ValueKind.Mapping;
        if (context == "Inheritance") return value.Kind is ValueKind.Mapping or ValueKind.Sequence;
        return NewValue(context).Kind != ValueKind.Sequence || value.Kind == ValueKind.Sequence;
    }
    private void UseDefinition(PresetBlock chosen)
    {
        if (chosen.Kind == BlockKind.Shared)
        {
            if (chosen.Value.Anchor.Length == 0)
            {
                var stem = Regex.Replace(chosen.Key, "[^a-zA-Z0-9_-]", "_");
                if (stem.Length == 0) stem = "sharedSettings";
                var used = Document.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value)).Select(value => value.Anchor).ToHashSet(StringComparer.Ordinal);
                var anchor = stem; var suffix = 2;
                while (used.Contains(anchor)) anchor = stem + suffix++;
                chosen.Value.Anchor = anchor;
            }
            SetValue(PresetValue.Alias(chosen.Value.Anchor));
        }
        else SetValue(PresetValue.Scalar(TemplateCall.Format(chosen.Key, TemplateCall.FindParameters(chosen.Value, Document).Select(_ => PresetValue.UnsetArgument()))));
    }
    private void ChooseParameter()
    {
        Capture();
        var dialog = new Window { Owner = Window.GetWindow(this), Title = "Template parameter", Width = 470, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(EditorControls.Text("Parameter name", true));
        var entry = new TextBox { Text = Value.Kind == ValueKind.Scalar && Value.Text.StartsWith('$') ? Value.Text[1..] : "" }; panel.Children.Add(entry);
        var error = Help("Each use of the template supplies a value for this name."); panel.Children.Add(error);
        var use = EditorControls.Button("Use parameter", () =>
        {
            var parameter = entry.Text.Trim(); if (!TemplateCall.IsName(parameter)) { error.Text = "Use letters, digits, underscores or hyphens."; return; }
            SetValue(PresetValue.Scalar("$" + parameter)); dialog.DialogResult = true;
        }, isDefault: true);
        void UpdateChoice() => use.Visibility = TemplateCall.IsName(entry.Text.Trim()) ? Visibility.Visible : Visibility.Collapsed;
        entry.TextChanged += (_, _) => UpdateChoice(); UpdateChoice();
        EditorControls.Footer(panel, use, EditorControls.Button("Cancel", () => dialog.DialogResult = false, isCancel: true)); dialog.Content = panel; dialog.ShowDialog();
    }
    private void AddSetting()
    {
        var dialog = new Window { Owner = Window.GetWindow(this), Title = "Add setting", Width = 450, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(EditorControls.Text("Setting name")); var entry = new TextBox(); panel.Children.Add(entry);
        var status = Help("Enter a unique setting name."); panel.Children.Add(status);
        var add = EditorControls.Button("Add", () =>
        {
            var key = entry.Text.Trim(); if (key.Length == 0 || Value.Fields.ContainsKey(key)) return;
            Value.Set(key, PresetValue.Scalar("")); dialog.DialogResult = true; Render(); NotifyChanged();
        }, isDefault: true);
        void UpdateChoice()
        {
            var key = entry.Text.Trim(); var duplicate = Value.Fields.ContainsKey(key);
            add.Visibility = key.Length > 0 && !duplicate ? Visibility.Visible : Visibility.Collapsed;
            status.Text = duplicate ? "This setting already exists." : "Enter a unique setting name.";
        }
        entry.TextChanged += (_, _) => UpdateChoice(); UpdateChoice();
        EditorControls.Footer(panel, add, EditorControls.Button("Cancel", () => dialog.DialogResult = false, isCancel: true)); dialog.Content = panel; dialog.ShowDialog();
    }
    private void SetValue(PresetValue value)
    {
        value.Anchor = Value.Anchor; value.Comments.AddRange(Value.Comments); value.TrailingComments.AddRange(Value.TrailingComments); value.InlineComment = Value.InlineComment;
        Value = value; replace?.Invoke(value); Render(); NotifyChanged();
    }
    private static bool IsReference(PresetValue value) => value.Kind == ValueKind.Alias || value.Kind == ValueKind.Scalar &&
        (value.Text.StartsWith('$') || TemplateCall.TryParse(value.Text, out _, out _));
    private static TextBlock Help(string text)
    {
        var label = EditorControls.Text(text); label.FontSize = 12; label.Margin = new Thickness(0, 0, 0, 4);
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); return label;
    }
    public static PresetValue NewValue(string context, PresetField? field = null)
    {
        if (field?.Kind is FieldKind.ObjectList or FieldKind.FormList or FieldKind.IntegerList ||
            context.ToLowerInvariant() is "stages" or "transformers" or "timemodulators" or "modulators" or "magiceffects" or "effects" or "values" or "rules" or "defaultrules" or "customrules" or "addonrules" or "inheritance") return PresetValue.List();
        if (field is null && context.ToLowerInvariant() is not ("value" or "")) return PresetValue.Map();
        return PresetValue.Scalar(field?.DefaultText ?? "");
    }
    public static string Summary(PresetValue value, PresetDocument document) => new PresetPresentation(document, []).Summary(value);
}
