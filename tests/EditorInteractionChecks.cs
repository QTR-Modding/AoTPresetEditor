using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AoTPresetEditor;

internal static class EditorInteractionChecks
{
    private static readonly FormRow[] Forms =
    [
        new("Raw Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Gourmet.esp", "0x65C99~Skyrim.esm"),
        new("Venison", "FoodVenison", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x669A2~Skyrim.esm"),
        new("Leather Armor", "ArmorLeather", "Armor", "Skyrim.esm", "Skyrim.esm", "0x3619E~Skyrim.esm")
    ];

    public static void Run()
    {
        PickerFiltering();
        ParametersAndArguments();
        NestedTemplateEditing();
        TypedChildControls();
        ListEditing();
        DraftIsolation();
        InlineEditing();
        OverviewActions();
    }

    private static void PickerFiltering()
    {
        var multi = new FormPickerWindow(Forms, ["Ingestible"], true);
        var grid = Field<DataGrid>(multi, "results");
        Require(grid.Items.Count == 2, "Picker must respect the field's allowed types.");
        grid.SelectedItems.Add(Forms[0]);
        Search(multi, "Venison");
        Require(grid.Items.Count == 1 && grid.SelectedItems.Count == 0, "Search must hide nonmatching rows.");
        grid.SelectedItems.Add(Forms[1]);
        Search(multi, "");
        Require(grid.SelectedItems.Count == 2, "Selections must survive changes to the search filter.");
        Field<ComboBox>(multi, "plugins").SelectedItem = "Gourmet.esp";
        Invoke(multi, "FilterForms");
        Require(grid.Items.Count == 1, "Plugin filtering must include winning overrides.");
        multi.Close();

        var single = new FormPickerWindow(Forms);
        grid = Field<DataGrid>(single, "results");
        grid.SelectedItem = Forms[0];
        Search(single, "Beef");
        Require(Equals(grid.SelectedItem, Forms[0]), "A matching single selection must survive filtering.");
        Search(single, "Venison");
        Require(grid.SelectedItem is null && !Field<Button>(single, "choose").IsEnabled, "A hidden single selection must be cleared.");
        single.Close();

        const int matchCount = 1100;
        var many = Enumerable.Range(0, matchCount).Select(index => Forms[0] with { EditorId = $"Beef{index}" }).ToArray();
        var limited = new FormPickerWindow(many);
        Require(Field<DataGrid>(limited, "results").Items.Count == 1000, "Large searches must limit rendered rows.");
        Require(Field<TextBlock>(limited, "count").Text.Contains(matchCount.ToString("N0")), "Limited results must still report every match.");
        limited.Close();
    }

    private static void ParametersAndArguments()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var boolean = new PresetField("crafting_allowed", "Allow crafting", FieldKind.Boolean, "", DefaultText: "false");
        var parameter = new ValueEditorWindow(document, Forms, PresetValue.Scalar("$enabled"), "Value", "Flag", true, boolean);
        Field<Action>(parameter, "capture")();
        Require(parameter.Value.Text == "$enabled", "A Boolean parameter must not become false when its editor is applied.");
        Require(!Field<StackPanel>(parameter, "content").Children.OfType<CheckBox>().Any(), "Parameters need parameter controls, not Boolean controls.");
        parameter.Close();

        var body = PresetValue.Map();
        body.Set("duration", PresetValue.Scalar("$duration"));
        body.Set("sound", PresetValue.Scalar("$sound"));
        document.Blocks.Add(new PresetBlock { Name = "Sound setup", Key = "sound", Kind = BlockKind.Template, Context = "Value", Value = body });
        var call = new ValueEditorWindow(document, Forms, PresetValue.Scalar("sound(2)"), "Value", "Call");
        Require(Field<StackPanel>(call, "content").Children.OfType<TextBlock>().Any(text => text.Text.Contains("1 arguments")),
            "Opening a call with missing arguments must preserve its warning.");
        Require(call.Value.Text == "sound(2)", "Opening a call must not silently add arguments.");
        call.Close();
    }

    private static void NestedTemplateEditing()
    {
        var original = PresetDocument.Parse("""
            templates:
              transforms:
              - FormEditorID: Fire
                finalFormEditorID: $cooked
                duration: 1
              - FormEditorID: Frost
                finalFormEditorID: $frozen
                duration: 1
            formsLists:
            - forms: FoodBeef
              transformers: transforms(FoodCookedBeef, FoodBeefF)
            """, "FOOD", PresetKind.Addon);
        var rule = original.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var descriptor = PresetSchema.GetFields("AddonRule", PresetKind.Addon).Single(field => field.Key == "transformers");
        var outer = new ValueEditorWindow(original, Forms, rule.Value, "AddonRule", "Rule");
        var call = new ValueEditorWindow(outer.Document, Forms, outer.Value.Get("transformers")!, descriptor.ChildContext,
            descriptor.Label, field: descriptor);
        var template = call.Document.Blocks.Single(block => block.Kind == BlockKind.Template);
        var definition = new ValueEditorWindow(call.Document, Forms, template.Value, template.Context, template.Name, true);
        var controls = Field<StackPanel>(definition, "content");
        controls.Children.OfType<ListBox>().Single().SelectedIndex = 0;
        Click(controls.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(button => Equals(button.Content, "Move down")));

        var changed = definition.Document;
        var previous = changed.Clone();
        changed.Blocks.Single(block => block.Id == template.Id).Value = definition.Value;
        changed.RebindTemplateCalls(previous);
        Invoke(call, "AdoptDocument", changed);
        Invoke(call, "Render");
        Require(TemplateCall.TryParse(call.Value.Text, out var callName, out var arguments) &&
            arguments.Select(value => value.Text).SequenceEqual(["FoodBeefF", "FoodCookedBeef"]),
            "Returning from a reordered definition must bind the open call to the new parameter order immediately.");
        Require(Descendants<TextBox>(call.Editor).Any(text => text.Text == "FoodBeefF"),
            "The open call must display each argument beside its actual parameter after a nested definition edit.");

        var argumentField = PresetSchema.GetParameterField(changed.Blocks.Single(block => block.Id == template.Id), "frozen", changed)!;
        var argument = new ValueEditorWindow(call.Document, Forms, arguments[0], argumentField.ChildContext, "frozen", field: argumentField);
        Field<StackPanel>(argument, "content").Children.OfType<TextBox>().Single().Text = "FoodChickenF";
        Field<Action>(argument, "capture")();
        Invoke(call, "AdoptDocument", argument.Document);
        Require(TemplateCall.TryParse(call.Value.Text, out callName, out arguments), "The edited call must remain readable.");
        arguments[0] = argument.Value;
        call.Value.Text = TemplateCall.Format(callName, arguments);
        Invoke(outer, "AdoptDocument", call.Document);
        outer.Value.Set("transformers", call.Value);

        var applied = outer.Document;
        previous = applied.Clone();
        applied.Blocks.Single(block => block.Id == rule.Id).Value = outer.Value;
        applied.RebindTemplateCalls(previous);
        var savedCall = applied.Blocks.Single(block => block.Id == rule.Id).Value.Get("transformers")!;
        Require(TemplateCall.TryParse(savedCall.Text, out _, out arguments) &&
            arguments.Select(value => value.Text).SequenceEqual(["FoodChickenF", "FoodCookedBeef"]),
            "Applying the containing rule must preserve the edited frozen result without rebinding it a second time.");
        Require(original.Blocks.Single(block => block.Id == rule.Id).Value.Get("transformers")!.Text ==
            "transforms(FoodCookedBeef, FoodBeefF)", "Nested edits must not mutate the original preset before it is applied.");
        argument.Close(); definition.Close(); call.Close(); outer.Close();
    }

    private static void TypedChildControls()
    {
        var document = PresetDocument.Parse("""
            sharedSound: &sharedSound IceCrack
            templates:
              progress:
                owners: FoodBeef
                stages: $steps
                finalFormEditorID: FoodBeef
            ownerLists: []
            """, "FOOD", PresetKind.Custom);
        var template = document.Blocks.Single(block => block.Kind == BlockKind.Template);
        var stageField = PresetSchema.GetParameterField(template, "steps", document)!;
        Require(stageField.Kind == FieldKind.ObjectList, "A stage-list parameter must retain its structured field type.");
        var argument = ValueEditorWindow.NewValue(stageField.ChildContext, stageField);
        var list = new ValueEditorWindow(document, Forms, argument, stageField.ChildContext, "steps", field: stageField);
        Require(Field<StackPanel>(list, "content").Children.OfType<ListBox>().Any(),
            "An initialized structured argument must open with entry controls.");
        var stage = (PresetValue)Invoke(list, "NewListItem")!;
        Require(stage.Kind == ValueKind.Mapping && stage.Get("no")?.Text == "0" && stage.Get("name") is not null,
            "Adding a stage argument must create stage fields rather than a scalar value.");
        list.Close();

        var soundField = PresetSchema.GetFields("Transformer", PresetKind.Custom).Single(field => field.Key == "sound");
        var shared = document.Blocks.Single(block => block.Kind == BlockKind.Shared);
        var definition = new ValueEditorWindow(document, Forms, shared.Value, shared.Context, shared.Name, field: soundField);
        var constraint = (FormSelection)Invoke(definition, "FormSelection")!;
        var sound = new FormRow("Crack", "IceCrack", "SoundDescriptor", "Sounds.esp", "Sounds.esp", "0x800~Sounds.esp");
        Require(constraint.Matches(sound) && !constraint.Matches(Forms[2]),
            "A scalar definition opened from a sound field must restrict its form picker to sound descriptors.");
        Field<Action>(definition, "capture")();
        Require(definition.Value.Text == "IceCrack" && definition.Value.Anchor == "sharedSound",
            "Opening a typed shared scalar must preserve its existing value and shared anchor.");
        definition.Close();
    }

    private static void ListEditing()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var values = PresetValue.List();
        values.Items.AddRange([PresetValue.Scalar("a"), PresetValue.Scalar("b"), PresetValue.Scalar("c")]);
        var order = new ValueEditorWindow(document, Forms, values, "Values", "Values");
        var content = Field<StackPanel>(order, "content");
        var list = content.Children.OfType<ListBox>().Single();
        list.SelectedIndex = 0;
        var down = content.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(button => Equals(button.Content, "Move down"));
        Click(down); Click(down);
        Require(order.Value.Items[2].Text == "a" && list.SelectedIndex == 2, "Repeated moves must continue moving the selected entry.");
        order.Close();

        var scalar = PresetValue.Scalar("1");
        scalar.Anchor = "sharedNumber";
        scalar.Comments.Add("A shared value.");
        scalar.TrailingComments.Add("End of shared value.");
        var conversion = new ValueEditorWindow(document, Forms, scalar, "Value", "Shared number");
        Click(Descendants<Button>(conversion.Editor).Single(button => Equals(button.Content, "Use a list of values...")));
        Require(conversion.Value.Kind == ValueKind.Sequence && conversion.Value.Anchor == "sharedNumber", "Converting to a list must retain the shared definition.");
        Require(conversion.Value.Items.Single().Anchor.Length == 0, "The wrapped scalar must not duplicate the shared anchor.");
        Require(conversion.Value.Comments.Count == 1 && conversion.Value.Items.Single().Comments.Count == 0 && conversion.Value.TrailingComments.Count == 1,
            "Converting to a list must keep comments once, on the shared definition.");
        var block = document.Add(BlockKind.Shared, "Shared number", "Values");
        block.Value = conversion.Value;
        Require(document.ToYaml().Contains("&sharedNumber"), "The converted shared list must serialize successfully.");
        conversion.Close();

        foreach (var context in new[] { "Rules", "DefaultRules", "CustomRules", "AddonRules" })
        {
            var initial = ValueEditorWindow.NewValue(context);
            Require(initial.Kind == ValueKind.Sequence, $"{context} must start as a list.");
            var editor = new ValueEditorWindow(document, Forms, initial, context, "Rules");
            Require(((PresetValue)Invoke(editor, "NewListItem")!).Kind == ValueKind.Mapping, $"{context} must add a settings block.");
            editor.Close();
        }
    }

    private static void DraftIsolation()
    {
        var original = PresetDocument.Create("FOOD", PresetKind.Custom);
        var shared = original.Add(BlockKind.Shared, "Shared", "Value");
        shared.Value.Text = "1"; shared.Value.Kind = ValueKind.Scalar;
        var outer = new ValueEditorWindow(original, Forms, PresetValue.Alias(shared.Value.Anchor), "Value", "Use shared");
        var child = new ValueEditorWindow(outer.Document, Forms, shared.Value, "Value", "Nested editor");
        child.Document.Blocks[0].Value.Text = "2";
        child.Value.Text = "3";
        child.Close();
        Require(outer.Document.Blocks[0].Value.Text == "1", "Canceling a child editor must leave the parent's definitions unchanged.");
        outer.Document.Blocks[0].Value.Text = "4";
        outer.Close();
        Require(original.Blocks[0].Value.Text == "1", "Canceling a block editor must leave the preset unchanged.");
    }

    private static void OverviewActions()
    {
        var workspace = new PresetWorkspace();
        var actions = Field<StackPanel>(workspace, "addActions");
        Require(actions.Visibility == Visibility.Collapsed, "An empty workspace must hide block editing actions.");
        var document = PresetDocument.Create("FOOD", PresetKind.Default);
        SetField(workspace, "document", document);
        Invoke(workspace, "UpdateActions");
        Require(actions.Visibility == Visibility.Visible && ((Button)actions.Children[0]).Visibility == Visibility.Visible,
            "An empty default preset must show the action for its first rule.");
        document.Add(BlockKind.Rule, "Default", "DefaultRule");
        Invoke(workspace, "UpdateActions");
        Require(((Button)actions.Children[0]).Visibility == Visibility.Collapsed, "A default preset must hide the unavailable second-rule action.");
        document = PresetDocument.Create("FOOD", PresetKind.Addon);
        document.Blocks.Add(new PresetBlock { Kind = BlockKind.Rule, Key = "formsLists", Value = PresetValue.Alias("sharedRules") });
        SetField(workspace, "document", document);
        Invoke(workspace, "UpdateActions");
        Require(((Button)actions.Children[0]).Visibility == Visibility.Collapsed, "A preset with a shared rule list must hide the unavailable Add rule action.");
    }

    private static void InlineEditing()
    {
        var document = PresetDocument.Parse("""
            cooking: &cooking
              FormEditorID: Fire
              duration: 1
            formsLists:
            - forms: FoodBeef
              transformers:
              - FormEditorID: Fire
                finalFormEditorID: FoodCookedBeef
                duration: 1
              - FormEditorID: Frost
                finalFormEditorID: FoodBeefF
                duration: 2
            """, "FOOD", PresetKind.Addon);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var editor = new ValueEditorControl(document, Forms, rule.Value, "AddonRule", "Rule");
        var changes = 0;
        editor.Changed += (_, _) => changes++;
        var duration = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "duration");
        var entry = Field<StackPanel>(duration, "content").Children.OfType<TextBox>().Single();
        entry.Text = "3";
        Require(editor.Value.Get("transformers")!.Items[0].Get("duration")!.Text == "3",
            "An inline duration edit must reach the containing rule without opening or applying another window.");
        Require(rule.Value.Get("transformers")!.Items[0].Get("duration")!.Text == "1", "Inline edits must remain isolated from their input document.");
        var afterChange = changes;
        editor.Capture();
        editor.UpdatePresentation(new PresetPresentation(document, Forms), editor.Value.Clone());
        Require(changes == afterChange && ReferenceEquals(entry, Field<StackPanel>(duration, "content").Children.OfType<TextBox>().Single()),
            "Capturing and refreshing summaries must preserve the input control and must not report another edit.");
        Require(editor.FocusIssue("Transformers 2: Duration (game hours): enter a number."), "A list issue must have an editing target.");
        var listEditor = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "transformers");
        Require(Field<ListBox>(listEditor, "entryList").SelectedIndex == 1, "An issue for the second transformation must select the second entry.");
        var secondDuration = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "duration");
        Require(secondDuration.Value.Text == "2", "Issue navigation must show the requested transformation's own values.");

        var empty = new ValueEditorControl(document, Forms, PresetValue.Map(), "Transformer", "Transformation");
        var missingDuration = Descendants<ValueEditorControl>(empty).Single(child => Field<PresetField?>(child, "field")?.Key == "duration");
        Require(Field<StackPanel>(missingDuration, "content").Children.OfType<TextBox>().Single().Text.Length == 0,
            "A missing required value must not display a default that has never been set.");

        var independent = new ValueEditorControl(document, Forms, rule.Value.Get("transformers")!.Items[0], "Transformer", "Transformation");
        independent.UpdatePresentation(new PresetPresentation(document, Forms), independent.Value.Clone());
        var durationCard = Field<StackPanel>(independent, "content").Children.OfType<Border>()
            .Single(card => Descendants<TextBlock>(card).Any(text => text.Text == "Duration (game hours)"));
        Click(Descendants<Button>(durationCard).Single(button => Equals(button.Content, "Clear")));
        Require(independent.Value.Get("duration") is null, "Clearing a field must remain safe while the previous expansion is still displayed.");

        var alias = new ValueEditorControl(document, Forms, PresetValue.Alias("cooking"), "Transformer", "Shared use");
        var copy = Descendants<Button>(alias).Single(button => Equals(button.Content, "Make independent copy for this use"));
        Require(copy.Visibility == Visibility.Collapsed, "An unresolved reference must not offer an unavailable copy.");
        alias.UpdatePresentation(new PresetPresentation(document, Forms), document.Blocks.Single(block => block.Key == "cooking").Value.Clone());
        Require(copy.Visibility == Visibility.Visible, "A resolved reference must expose the independent-copy action without rebuilding inputs.");

        var imported = PresetDocument.Parse("""
            cooking:
              duration: 1
            other: &cooking 2
            formsLists: []
            """, "FOOD", PresetKind.Addon);
        var reuse = new ValueEditorControl(imported, Forms, PresetValue.Map(), "Transformer", "Transformation");
        var shared = reuse.Document.Blocks.Single(block => block.Key == "cooking");
        Invoke(reuse, "UseDefinition", shared);
        Require(reuse.Value.Kind == ValueKind.Alias && reuse.Value.Text == shared.Value.Anchor && shared.Value.Anchor == "cooking2",
            "Reusing an imported definition must create an available shared anchor without colliding with existing anchors.");
        Require(imported.Blocks.Single(block => block.Key == "cooking").Value.Anchor.Length == 0,
            "Choosing a shared definition must leave the original document untouched until its draft is applied.");
    }

    private static T Field<T>(object owner, string name)
    {
        if (owner is ValueEditorWindow window) owner = window.Editor;
        return (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    }
    private static void SetField(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);
    private static object? Invoke(object owner, string name, params object?[] arguments)
    {
        if (owner is ValueEditorWindow window) owner = window.Editor;
        return owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.Invoke(owner, arguments);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Search(object window, string text) { Field<TextBox>(window, "search").Text = text; Invoke(window, "FilterForms"); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
