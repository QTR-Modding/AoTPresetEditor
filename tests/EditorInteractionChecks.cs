using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
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
        ResolvedPreviewInvalidation();
        TypedChildControls();
        StagePickerApplicability();
        ListEditing();
        DraftIsolation();
        InlineEditing();
        TriggerStageIssueNavigation();
        CompactFields();
        VariableCheckboxes();
        EmptyScalarDisplay();
        CompactScalarLists();
        CollectionDisclosure();
        StageCreationAfterRemoval();
        CollectionSections();
        FieldApplicability();
        RetainedUnusedSettings();
        ReusableDefinitionApplicability();
        EffectApplicability();
        ContextualActions();
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
        Descendants<TextBox>(argument.Editor).Single().Text = "FoodChickenF";
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

    private static void ResolvedPreviewInvalidation()
    {
        var document = PresetDocument.Parse("""
            templates:
              cooking:
                FormEditorID: Fire
                finalFormEditorID: FoodBeef
                duration: $duration
            formsLists: []
            """, "FOOD", PresetKind.Addon);
        var call = new ValueEditorControl(document, Forms, PresetValue.Scalar("cooking(1)"), "Transformer", "Cooking");
        var expanded = document.Blocks.Single().Value.Clone(); expanded.Get("duration")!.Text = "1";
        call.UpdatePresentation(new PresetPresentation(document, Forms), expanded);
        var copy = Descendants<Button>(call).Single(button => Equals(button.Content, "Make independent copy for this use"));
        Require(copy.Visibility == Visibility.Visible, "A current expansion should allow making an independent copy.");
        Descendants<TextBox>(call).Single().Text = "2";
        Require(copy.Visibility == Visibility.Collapsed && call.Value.Text == "cooking(2)" && Field<PresetValue?>(call, "effective") is null,
            "Editing a template argument must invalidate the previous expansion before another action can use it.");
        Click(copy);
        Require(call.Value.Kind == ValueKind.Scalar && call.Value.Text == "cooking(2)",
            "An invalidated copy action must not replace newly edited arguments with stale generated settings.");
        expanded.Get("duration")!.Text = "2";
        call.UpdatePresentation(new PresetPresentation(document, Forms), expanded);
        Require(copy.Visibility == Visibility.Visible, "A new expansion must restore the independent-copy action.");
        Click(copy);
        Require(call.Value.Kind == ValueKind.Mapping && call.Value.Get("duration")?.Text == "2",
            "Making a copy after fresh expansion must retain the edited argument's resulting value.");
    }

    private static void StagePickerApplicability()
    {
        FormRow[] catalog =
        [
            new("Raw Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C99~Skyrim.esm", ItemCategories.FOOD, IsBoundObject: true),
            new("Cooked Beef", "FoodCooked", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x721E8~Skyrim.esm", ItemCategories.FOOD, IsBoundObject: true),
            new("Stew", "FoodStew", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0xF4312~Skyrim.esm", ItemCategories.FOOD, IsBoundObject: true),
            new("Berries", "FoodBerries", "Ingredient", "Skyrim.esm", "Skyrim.esm", "0x800~Skyrim.esm", ItemCategories.FOOD | ItemCategories.INGR, IsBoundObject: true),
            new("Other Berries", "FoodOtherBerries", "Ingredient", "Skyrim.esm", "Skyrim.esm", "0x801~Skyrim.esm", ItemCategories.FOOD | ItemCategories.INGR, IsBoundObject: true),
            new("Used Berries", "FoodUsedBerries", "Ingredient", "Skyrim.esm", "Skyrim.esm", "0x802~Skyrim.esm", ItemCategories.FOOD | ItemCategories.INGR, IsBoundObject: true),
            new("Key", "TestKey", "Key", "Skyrim.esm", "Skyrim.esm", "0x803~Skyrim.esm", IsBoundObject: true),
            new("Location", "TestLocation", "Location", "Skyrim.esm", "Skyrim.esm", "0x804~Skyrim.esm")
        ];
        foreach (var (sourceIndex, allowedIndex, wrongTypeIndex, usedIndex) in new[] { (0, 2, 4, 1), (3, 4, 2, 5) })
        {
            var document = PresetDocument.Create("FOOD", PresetKind.Custom);
            var rule = document.Add(BlockKind.Rule, "Food stages", "CustomRule");
            rule.Value.Set("owners", PresetValue.Scalar(catalog[sourceIndex].EditorId));
            rule.Value.Set("finalFormEditorID", PresetValue.Scalar("TestKey"));
            var stages = PresetValue.List();
            for (var index = 0; index < 3; index++)
            {
                var stage = PresetValue.Map(); stage.Set("no", PresetValue.Scalar(index.ToString()));
                stage.Set("name", PresetValue.Scalar("Stage")); stage.Set("duration", PresetValue.Scalar("1"));
                if (index > 0) stage.Set("FormEditorID", PresetValue.Scalar(index == 1 ? "" : catalog[usedIndex].EditorId));
                stages.Items.Add(stage);
            }
            rule.Value.Set("stages", stages);
            var editor = new ValueEditorControl(document, catalog, rule.Value, "CustomRule", "Rule");
            var stageList = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "stages");
            Field<ListBox>(stageList, "entryList").SelectedIndex = 1;
            var stageItem = Descendants<ValueEditorControl>(stageList).Single(child => Field<PresetField?>(child, "field")?.Key == "FormEditorID");
            var selection = (FormSelection)Invoke(stageItem, "FormSelection")!;
            Require(selection.Matches(catalog[allowedIndex]) && !selection.Matches(catalog[wrongTypeIndex]),
                "A later FOOD stage must match its source's native type even when both candidate types belong to FOOD.");
            Require(!selection.Matches(catalog[sourceIndex]) && !selection.Matches(catalog[usedIndex]),
                "A stage replacement picker must exclude the source item and items already used by another stage.");
            var finalItem = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "finalFormEditorID");
            var finalSelection = (FormSelection)Invoke(finalItem, "FormSelection")!;
            Require(finalSelection.Matches(catalog[6]) && !finalSelection.Matches(catalog[7]),
                "A final result picker must accept a bound Key while rejecting a Location.");
        }
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
        var actions = Field<Button>(workspace, "addBehavior");
        Require(actions.Visibility == Visibility.Collapsed, "An unopened workspace must hide behavior editing actions.");
        var project = new PresetProject { Name = "Several behaviors" };
        SetField(workspace, "project", project);
        Invoke(workspace, "UpdateActions");
        Require(actions.Visibility == Visibility.Visible && AutomationProperties.GetName(actions) == "Add behavior",
            "A new project must offer its first behavior without exposing YAML block types.");
        var document = PresetDocument.Create("FOOD", PresetKind.Default);
        var entry = project.Add(document, "Fallback");
        SetField(workspace, "activeEntry", entry);
        SetField(workspace, "document", document);
        Invoke(workspace, "UpdateActions");
        Require(actions.Visibility == Visibility.Visible,
            "A project with an empty fallback document must offer its first behavior.");
        document.Add(BlockKind.Rule, "Default", "DefaultRule");
        Invoke(workspace, "UpdateActions");
        Require(actions.Visibility == Visibility.Visible && actions.IsEnabled,
            "A completed fallback must still allow adding other behaviors or item categories to the project.");
        document = PresetDocument.Create("FOOD", PresetKind.Addon);
        document.Blocks.Add(new PresetBlock { Kind = BlockKind.Rule, Key = "formsLists", Value = PresetValue.Alias("sharedRules") });
        entry = project.Add(document, "Shared cooking");
        SetField(workspace, "activeEntry", entry);
        SetField(workspace, "document", document);
        Invoke(workspace, "UpdateActions");
        Require(actions.Visibility == Visibility.Visible && actions.IsEnabled,
            "An imported shared rule list must still allow new behaviors in another generated document.");
    }

    private static void TriggerStageIssueNavigation()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        foreach (var key in new[] { "transformers", "timeModulators" })
        {
            var rule = PresetYaml.Parse("owners: FoodBeef\nstages: [{no: 0, name: Fresh, duration: 1}]\nfinalFormEditorID: FoodBeef");
            var trigger = PresetYaml.Parse(key == "transformers"
                ? "FormEditorID: Fire\nfinalFormEditorID: FoodCookedBeef\nduration: 1"
                : "FormEditorID: Fire\nmagnitude: 0");
            var triggers = PresetValue.List();
            triggers.Items.Add(trigger.Clone()); triggers.Items.Add(trigger.Clone());
            triggers.Items[1].Set("allowed_stages", PresetValue.Scalar("9")); rule.Set(key, triggers);
            var issue = PresetSchema.Validate(rule, "CustomRule", "FOOD").Single();
            var editor = new ValueEditorControl(document, Forms, rule, "CustomRule", "Rule");
            Require(editor.FocusIssue(issue), "An undefined-stage issue must have an editing target.");
            var list = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == key);
            Require(Field<ListBox>(list, "entryList").SelectedIndex == 1,
                "An issue produced by validation must open the affected transformation or time modifier.");
            var triggerEditor = Descendants<ValueEditorControl>(list).First(child => child.Value.Kind == ValueKind.Mapping);
            Require(Ancestors<Expander>(FieldCard(triggerEditor, "allowed_stages")).All(section => section.IsExpanded),
                "An undefined-stage issue must reveal its Allowed stages field inside Filters.");
        }
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
        var entry = Descendants<TextBox>(duration).Single();
        entry.Text = "3";
        Require(editor.Value.Get("transformers")!.Items[0].Get("duration")!.Text == "3",
            "An inline duration edit must reach the containing rule without opening or applying another window.");
        Require(rule.Value.Get("transformers")!.Items[0].Get("duration")!.Text == "1", "Inline edits must remain isolated from their input document.");
        var afterChange = changes;
        editor.Capture();
        editor.UpdatePresentation(new PresetPresentation(document, Forms), editor.Value.Clone());
        Require(changes == afterChange && ReferenceEquals(entry, Descendants<TextBox>(duration).Single()),
            "Capturing and refreshing summaries must preserve the input control and must not report another edit.");
        Require(editor.FocusIssue("Transformations 2: Duration (game hours): enter a number."), "A list issue must have an editing target.");
        var listEditor = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "transformers");
        Require(Field<ListBox>(listEditor, "entryList").SelectedIndex == 1, "An issue for the second transformation must select the second entry.");
        var secondDuration = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "duration");
        Require(secondDuration.Value.Text == "2", "Issue navigation must show the requested transformation's own values.");

        var empty = new ValueEditorControl(document, Forms, PresetValue.Map(), "Transformer", "Transformation");
        var missingDuration = Descendants<ValueEditorControl>(empty).Single(child => Field<PresetField?>(child, "field")?.Key == "duration");
        Require(Descendants<TextBox>(missingDuration).Single().Text.Length == 0,
            "A missing required value must not display a default that has never been set.");

        var independentValue = rule.Value.Get("transformers")!.Items[0].Clone();
        independentValue.Set("color", PresetValue.Scalar("ff006433"));
        var independent = new ValueEditorControl(document, Forms, independentValue, "Transformer", "Transformation");
        independent.UpdatePresentation(new PresetPresentation(document, Forms), independent.Value.Clone());
        var durationCard = Field<StackPanel>(independent, "content").Children.OfType<Border>()
            .Single(card => Descendants<TextBlock>(card).Any(text => text.Text == "Duration (game hours)"));
        Require(!Descendants<Button>(durationCard).Any(button => Equals(button.Content, "Clear")),
            "Required fields should be edited directly without an action that only makes them missing.");
        var colorField = Descendants<ValueEditorControl>(independent).Single(child => Field<PresetField?>(child, "field")?.Key == "color");
        var colorCard = (StackPanel)colorField.Parent;
        Click(Descendants<Button>(colorCard).Single(button => Equals(button.Content, "Clear")));
        Require(independent.Value.Get("color") is null, "Clearing an optional field must remain safe while the previous expansion is still displayed.");

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

    private static void CompactFields()
    {
        var document = PresetDocument.Parse("""
            formsLists:
            - forms: FoodBeef
              transformers:
              - FormEditorID: Fire
                finalFormEditorID: FoodCookedBeef
                duration: 1
            """, "FOOD", PresetKind.Addon);
        var value = document.Blocks.Single().Value.Get("transformers")!.Items.Single();
        var editor = new ValueEditorControl(document, Forms, value, "Transformer", "Transformation");
        var changes = 0;
        editor.Changed += (_, _) => changes++;
        var trigger = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "FormEditorID");
        var row = Field<StackPanel>(trigger, "content").Children.OfType<Grid>().Single();
        Require(row.Children.OfType<TextBox>().Count() == 1 && Descendants<Button>(row).Any(button => Equals(button.Content, "Choose form...")),
            "A form input and its picker must share a responsive row.");
        var card = Field<StackPanel>(editor, "content").Children.OfType<Border>()
            .Single(candidate => Descendants<TextBlock>(candidate).Any(text => text.Text == "Duration (game hours)"));
        var explanation = PresetSchema.GetFields("Transformer", PresetKind.Addon).Single(field => field.Key == "duration").Help;
        var help = Descendants<Button>(card).Single(button => AutomationProperties.GetName(button) == "Help for Duration (game hours)");
        var tooltip = (ToolTip)help.ToolTip;
        Require(help.Focusable && help.IsTabStop && AutomationProperties.GetHelpText(help) == explanation &&
            tooltip.Content is TextBlock { TextWrapping: TextWrapping.Wrap } tooltipText && tooltipText.Text == explanation &&
            !Descendants<TextBlock>(card).Any(text => text.Text == explanation) &&
            !Descendants<Expander>(editor).Any(expander => Equals(expander.Header, "Field help")),
            "Field instructions must be available through a focusable information icon without occupying the input layout.");
        Click(help);
        Require(tooltip.IsOpen && changes == 0 && editor.Value.Get("duration")!.Text == "1",
            "Opening the information tooltip must leave the preset draft unchanged.");
        tooltip.IsOpen = false;
        var durationField = PresetSchema.GetFields("Transformer", PresetKind.Addon).Single(field => field.Key == "duration");
        var compact = new ValueEditorControl(document, Forms, PresetValue.Scalar("2"), "Value", durationField.Label,
            field: durationField, showHeading: false);
        Require(Descendants<TextBox>(compact).Single().Text == "2" &&
            !Descendants<TextBlock>(compact).Any(text => text.Text == durationField.Label) &&
            !Descendants<Button>(compact).Any(button => AutomationProperties.GetName(button).StartsWith("Help for ", StringComparison.Ordinal)),
            "A selected-cell editor must support an external heading and tooltip without duplicating them.");
    }

    private static void VariableCheckboxes()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Addon);
        var value = PresetYaml.Parse("FormEditorID: Fire\nfinalFormEditorID: Cooked\nduration: 3\n");
        var editor = new ValueEditorControl(document, Forms, value, "Transformer", "Transformation", behaviorMode: true);
        var requested = new List<(string Path, bool Enabled)>();
        editor.VariableChangeRequested += (path, field, enabled) =>
        {
            requested.Add((string.Join("/", path), enabled));
            Require(editor.Value.Get("duration")!.Text == "7", "The variable request must capture the current value before asking for a name.");
            return false;
        };
        FieldInput(editor, "duration").Text = "7";
        var duration = Descendants<CheckBox>(FieldCard(editor, "duration")).Single();
        Require(Equals(duration.Content, "Variable") && duration.IsChecked == false &&
            AutomationProperties.GetName(duration) == "Variable: Duration (game hours)",
            "Eligible fields must show a named, unchecked Variable control without a menu.");
        duration.IsChecked = true; duration.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var optional = Descendants<CheckBox>(FieldCard(editor, "color")).Single();
        optional.IsChecked = true; optional.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Require(requested.SequenceEqual([("duration", true), ("color", true)]) && duration.IsChecked == false && optional.IsChecked == false &&
            editor.Value.Get("duration")!.Text == "7" && editor.Value.Get("color") is null,
            "Cancelling variable naming must restore both checkboxes and preserve configured and absent values.");

        var stage = PresetYaml.Parse("no: 0\nname: Fresh\nduration: $hours\nvalue: $price\n");
        var named = new ValueEditorControl(document, Forms, stage, "Stage", "Stage", parametersAllowed: true, behaviorMode: true);
        var checkedVariable = Descendants<CheckBox>(FieldCard(named, "duration")).Single();
        Require(checkedVariable.IsChecked == true && Descendants<CheckBox>(FieldCard(named, "value")).Single().IsChecked == true,
            "Existing variables, including currently unused settings, must keep a checked control so they can be cleared.");
        var cleared = false;
        named.VariableChangeRequested += (path, field, enabled) => { cleared = path.SequenceEqual(["duration"]) && !enabled; return false; };
        checkedVariable.IsChecked = false; checkedVariable.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Require(cleared && checkedVariable.IsChecked == true && named.Value.Get("duration")!.Text == "$hours",
            "A rejected removal must preserve the existing variable and checked state.");

        foreach (var (context, yaml, keys) in new (string, string, string[])[]
        {
            ("AddonRule", "forms: $raw\ntransformers: $changes\n", ["forms", "transformers"]),
            ("Transformer", "FormEditorID: Fire\nfinalFormEditorID: $result\nduration: $hours\nallowed_stages: $numbers\n",
                ["finalFormEditorID", "duration", "allowed_stages"])
        })
        {
            var values = PresetYaml.Parse(yaml);
            var compact = new ValueEditorControl(document, Forms, values, context, "Settings",
                parametersAllowed: true, behaviorMode: true);
            compact.UpdatePresentation(new PresetPresentation(compact.Document, Forms));
            foreach (var key in keys)
            {
                var card = FieldCard(compact, key);
                Require(Descendants<CheckBox>(card).Single().IsChecked == true &&
                    card.Child is StackPanel body && body.Children.OfType<FrameworkElement>()
                        .Where(child => child.Visibility != Visibility.Collapsed).All(child => child is DockPanel) &&
                    !Descendants<ValueEditorControl>(card).Any() && !Descendants<Expander>(card).Any() &&
                    !Descendants<TextBlock>(card).Any(text => text.Text == "Varies per item"),
                    "A checked variable must show its field header and checkbox without a duplicate status or empty expandable body: " + key);
            }
            compact.Capture();
            Require(keys.All(key => compact.Value.Get(key)!.Text == values.Get(key)!.Text),
                "Removing variable display bodies must preserve their parameter values when settings are captured.");
        }
        Require(Descendants<TextBlock>(FieldCard(named, "value")).Any(text =>
            text.Visibility == Visibility.Visible && text.Text.Contains("stage 0", StringComparison.OrdinalIgnoreCase)),
            "A compact variable field must retain a real inactive-setting warning.");

        var rule = new ValueEditorControl(document, Forms,
            PresetYaml.Parse("owners: FoodBeef\nstages: $steps\nfinalFormEditorID: FoodVenison\n"),
            "CustomRule", "Rule", parametersAllowed: true, behaviorMode: true);
        Require(!Descendants<CheckBox>(FieldCard(rule, "stages")).Any(),
            "The entire Stages list must not offer a Variable checkbox.");
        rule.Capture();
        Require(rule.Value.Get("stages")!.Text == "$steps",
            "Imported whole-list stage arguments must survive editing other settings.");
    }

    private static void EmptyScalarDisplay()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var field = PresetSchema.GetFields("rule", document.Kind).Single(item => item.Key == "finalFormEditorID");
        foreach (var text in new[] { "null", "~", "!!null null", "\"null\"", "\"\"" })
        {
            var value = PresetYaml.Parse(text);
            var editor = new ValueEditorControl(document, Forms, value, "value", "Final item", field: field);
            var input = Descendants<TextBox>(editor).Single();
            Require(input.Text == (value.IsNull ? "" : value.Text), "Only YAML nulls should display as blank fields.");
            editor.Capture();
            Require(editor.Value.Text == value.Text && editor.Value.Tag == value.Tag && editor.Value.Style == value.Style,
                "Opening or capturing a null or quoted scalar must preserve its original value and style.");
            input.Text = "FoodBeef";
            editor.Capture();
            Require(!editor.Value.IsNull && editor.Value.Text == "FoodBeef", "Entering a form must replace the empty value.");
        }
    }

    private static void CompactScalarLists()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Addon);
        var descriptor = PresetSchema.GetFields("AddonRule", PresetKind.Addon).Single(field => field.Key == "forms");
        var values = PresetValue.List(); values.Items.Add(PresetValue.Scalar("FoodBeef"));
        var editor = new ValueEditorControl(document, Forms, values, "Values", "Applies to", field: descriptor);
        var panel = Field<StackPanel>(editor, "content");
        var details = panel.Children.OfType<StackPanel>().Single();
        var actions = panel.Children.OfType<WrapPanel>().Single();
        var edit = actions.Children.OfType<Button>().Single(button => Equals(button.Content, "Edit entry"));
        Require(details.Visibility == Visibility.Collapsed && edit.Visibility == Visibility.Visible,
            "A scalar list should initially show its summary and an explicit edit action without repeated inputs.");
        Click(edit);
        Require(details.Visibility == Visibility.Visible && Descendants<TextBlock>(details).Any(text => text.Text == "Identifier"),
            "Editing a form-list entry must reveal its identifier input.");
        Descendants<TextBox>(details).Single().Text = "FoodVenison";
        editor.Capture();
        Require(editor.Value.Items[0].Text == "FoodVenison" && values.Items[0].Text == "FoodBeef",
            "Editing an expanded scalar entry must preserve draft isolation.");
        Click(edit);
        Require(details.Visibility == Visibility.Collapsed, "The entry editor should close without removing the selection.");
        Click(actions.Children.OfType<Button>().Single(button => Equals(button.Content, "Add by identifier")));
        Require(editor.Value.Items.Count == 2 && details.Visibility == Visibility.Visible,
            "Adding an empty identifier must open its input immediately.");
        Field<ListBox>(editor, "entryList").SelectedIndex = 0;
        Require(details.Visibility == Visibility.Collapsed, "Selecting a different existing form should show its summary until editing is requested.");
    }

    private static void StageCreationAfterRemoval()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var value = PresetYaml.Parse("owners: FoodBeef\nstages: [{no: 0, name: Fresh, duration: 1}, {no: 1, name: Middle, duration: 2}, {no: 0x2, name: Last, duration: 3}]\nfinalFormEditorID: FoodVenison\ntransformers: [{FormEditorID: Fire, finalFormEditorID: FoodVenison, duration: 1, allowed_stages: [0, 2]}]");
        var editor = new ValueEditorControl(document, Forms, value, "CustomRule", "Rule");
        var stages = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "stages");
        var list = Field<ListBox>(stages, "entryList");
        var actions = Field<StackPanel>(stages, "content").Children.OfType<WrapPanel>().Single();
        list.SelectedIndex = 1;
        Require(actions.Children.OfType<Button>().Where(button => button.Content is "Move up" or "Move down")
            .All(button => button.Visibility == Visibility.Collapsed),
            "Stages must not offer list reordering that cannot change numeric progression.");
        Click(actions.Children.OfType<Button>().Single(button => Equals(button.Content, "Remove")));
        Click(actions.Children.OfType<Button>().Single(button => Equals(button.Content, "Add stage")));
        editor.Capture();
        Require(stages.Value.Items.Select(stage => stage.Get("no")!.Text).SequenceEqual(["0", "0x2", "1"]),
            "Adding a stage after removing a middle stage must fill the unused number without renumbering existing stages.");
        Require(editor.Value.Get("transformers")!.Items.Single().Get("allowed_stages")!.Items.Select(stage => stage.Text).SequenceEqual(["0", "2"]),
            "Creating a stage must preserve trigger stage references.");
        Require(PresetSchema.Validate(editor.Value, "CustomRule", "FOOD").Count == 0,
            "The stage list must remain valid after replacing a removed stage.");
        list.SelectedIndex = 0;
        Click(actions.Children.OfType<Button>().Single(button => Equals(button.Content, "Remove")));
        Click(actions.Children.OfType<Button>().Single(button => Equals(button.Content, "Add stage")));
        Require(stages.Value.Items.Last().Get("no")!.Text == "0",
            "Adding a stage must restore stage 0 when it is missing.");
        var transformerField = PresetSchema.GetFields("CustomRule", document.Kind).Single(field => field.Key == "transformers");
        var transformations = PresetYaml.Parse("[{FormEditorID: Fire, duration: 1}, {FormEditorID: Snow, duration: 2}]");
        var triggers = new ValueEditorControl(document, Forms, transformations, "transformer", "Transformations", field: transformerField);
        var triggerActions = Field<StackPanel>(triggers, "content").Children.OfType<WrapPanel>().Single();
        var down = triggerActions.Children.OfType<Button>().Single(button => Equals(button.Content, "Move down"));
        Require(down.Visibility == Visibility.Visible, "Transformations must keep ordering controls because earlier triggers take priority.");
        Click(down);
        Require(triggers.Value.Items.Select(item => item.Get("FormEditorID")!.Text).SequenceEqual(["Snow", "Fire"]),
            "Moving a transformation must update its trigger priority.");
    }

    private static void CollectionDisclosure()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        foreach (var (parentContext, key, editedKey) in new[]
        {
            ("CustomRule", "stages", "duration"),
            ("CustomRule", "transformers", "duration"),
            ("CustomRule", "timeModulators", "magnitude"),
            ("Stage", "mgeffect", "duration")
        })
        {
            var descriptor = PresetSchema.GetFields(parentContext, document.Kind).Single(field => field.Key == key);
            var item = PresetValue.Map();
            item.Set(editedKey, PresetValue.Scalar("1"));
            if (key == "stages")
            {
                item.Set("no", PresetValue.Scalar("0"));
                item.Set("name", PresetValue.Scalar("Fresh"));
            }
            else
            {
                item.Set("FormEditorID", PresetValue.Scalar("Fire"));
                if (key == "transformers") item.Set("finalFormEditorID", PresetValue.Scalar("FoodBeef"));
                if (key == "mgeffect") item.Set("magnitude", PresetValue.Scalar("1"));
            }
            var values = PresetValue.List(); values.Items.Add(item); values.Items.Add(item.Clone());
            if (key == "stages") values.Items[1].Get("no")!.Text = "1";
            var editor = new ValueEditorControl(document, Forms, values, descriptor.ChildContext, descriptor.Label, field: descriptor);
            var panel = Field<StackPanel>(editor, "content");
            var details = panel.Children.OfType<StackPanel>().Single();
            var actions = panel.Children.OfType<WrapPanel>().Single();
            var list = Field<ListBox>(editor, "entryList");
            var edit = actions.Children.OfType<Button>().Single(button => Equals(button.Content, "Edit entry"));
            var child = details.Children.OfType<ValueEditorControl>().Single();
            var input = Descendants<TextBox>(Descendants<ValueEditorControl>(child).Single(control => Field<PresetField?>(control, "field")?.Key == editedKey)).Single();
            var changes = 0; editor.Changed += (_, _) => changes++;
            Require(list.SelectedIndex == 0 && details.Visibility == Visibility.Collapsed && edit.Visibility == Visibility.Visible,
                $"{descriptor.Label} must begin with a selected summary and collapsed entry details.");
            if (key == "stages")
            {
                Require(details.Children.OfType<TextBlock>().Single().Text == "Stage 0",
                    "A stage detail heading must use the authored stage number, not its one-based list position.");
                Require(!((TextBlock)((ListBoxItem)list.Items[0]).Content).Text.StartsWith("1. "),
                    "A stage summary must not add a conflicting one-based stage number.");
            }
            Click(edit); input.Text = "2"; var afterEdit = changes;
            Click(edit); editor.Capture(); editor.UpdatePresentation(new PresetPresentation(document, Forms), editor.Value.Clone());
            Require(details.Visibility == Visibility.Collapsed && list.SelectedIndex == 0 && changes == afterEdit,
                $"Refreshing hidden {descriptor.Label} must retain selection and must not dirty the draft.");
            Click(edit);
            Require(details.Visibility == Visibility.Visible && ReferenceEquals(child, details.Children.OfType<ValueEditorControl>().Single()) &&
                Descendants<TextBox>(child).Contains(input) && input.Text == "2" && editor.Value.Items[0].Get(editedKey)!.Text == "2" && item.Get(editedKey)!.Text == "1",
                $"Reopening {descriptor.Label} must preserve the same input and isolated edited value.");
            editor.UpdatePresentation(new PresetPresentation(document, Forms), editor.Value.Clone());
            Require(details.Visibility == Visibility.Visible && list.SelectedIndex == 0 && changes == afterEdit,
                $"Refreshing open {descriptor.Label} must preserve disclosure and must not report another edit.");
            if (key == "stages")
            {
                var number = Descendants<ValueEditorControl>(child).Single(control => Field<PresetField?>(control, "field")?.Key == "no");
                Descendants<TextBox>(number).Single().Text = "7";
                editor.UpdatePresentation(new PresetPresentation(document, Forms), editor.Value.Clone());
                Require(details.Children.OfType<TextBlock>().Single().Text == "Stage 7",
                    "Changing a stage number must update the detail heading without replacing the input controls.");
            }
            list.SelectedIndex = 1;
            Require(details.Visibility == Visibility.Collapsed && Equals(edit.Content, "Edit entry"),
                $"Selecting another {descriptor.Label} entry must return to its summary.");
            editor.UpdatePresentation(new PresetPresentation(document, Forms), editor.Value.Clone());
            Require(list.SelectedIndex == 1 && details.Visibility == Visibility.Collapsed,
                $"Validation must not reset the selected {descriptor.Label} entry.");
            var fieldLabel = PresetSchema.GetFields(descriptor.ChildContext, document.Kind).Single(field => field.Key == editedKey).Label;
            Require(editor.FocusIssue($"{descriptor.Label} 2: {fieldLabel}: enter a number.") && list.SelectedIndex == 1 && details.Visibility == Visibility.Visible,
                $"An issue must open the named {descriptor.Label} entry.");
            Click(actions.Children.OfType<Button>().Single(button => button.Content is string label && label.StartsWith("Add ")));
            Require(editor.Value.Items.Count == 3 && list.SelectedIndex == 2 && details.Visibility == Visibility.Visible,
                $"Adding to {descriptor.Label} must immediately open the new entry for editing.");
        }
    }

    private static void CollectionSections()
    {
        var document = PresetDocument.Parse("""
            ownerLists:
            - owners: FoodBeef
              stages:
              - no: 0
                name: Fresh
                duration: 1
              - no: 1
                name: Stale
                duration: 2
              finalFormEditorID: FoodBeef
            """, "FOOD", PresetKind.Custom);
        var editor = new ValueEditorControl(document, Forms, document.Blocks.Single().Value, "CustomRule", "Rule");
        var stages = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "stages");
        var section = Ancestors<Expander>(stages).First();
        section.IsExpanded = false;
        var list = Field<ListBox>(stages, "entryList");
        var details = Field<StackPanel>(stages, "content").Children.OfType<StackPanel>().Single();
        Require(editor.FocusIssue("Stages 2: Duration (game hours): enter a number.") && section.IsExpanded &&
            list.SelectedIndex == 1 && details.Visibility == Visibility.Visible,
            "Issue navigation must open the collapsed collection and the requested entry together.");
        section.IsExpanded = false;
        editor.Value.Get("stages")!.Items[1].Set("no", PresetValue.Scalar("7"));
        Require(editor.FocusIssue("Stage 7: this replacement item is already used by another stage.") && section.IsExpanded &&
            list.SelectedIndex == 1 && details.Visibility == Visibility.Visible,
            "A stage-number issue must navigate by the configured stage number instead of treating it as a list position.");
        var selected = details.Children.OfType<ValueEditorControl>().Single();
        section.IsExpanded = false;
        var evolution = Descendants<Border>(editor).Single(card => Descendants<TextBlock>(card).Any(text => text.Text.StartsWith("Containers", StringComparison.Ordinal)));
        Click(Descendants<Button>(evolution).Single(button => Equals(button.Content, "Set")));
        Require(ReferenceEquals(stages, Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "stages")) &&
            !section.IsExpanded && list.SelectedIndex == 1 && ReferenceEquals(selected, details.Children.OfType<ValueEditorControl>().Single()),
            "Setting another field must preserve the selected stage editor and its collapsed section.");
        evolution = Descendants<Border>(editor).Single(card => Descendants<TextBlock>(card).Any(text => text.Text.StartsWith("Containers", StringComparison.Ordinal)));
        Click(Descendants<Button>(evolution).Single(button => Equals(button.Content, "Clear")));
        Require(!section.IsExpanded && list.SelectedIndex == 1 && ReferenceEquals(selected, details.Children.OfType<ValueEditorControl>().Single()),
            "Clearing another field must preserve the selected stage editor and its collapsed section.");

        var transformation = PresetValue.Map();
        transformation.Set("FormEditorID", PresetValue.Scalar("Fire"));
        transformation.Set("finalFormEditorID", PresetValue.Scalar("FoodBeef"));
        transformation.Set("duration", PresetValue.Scalar("1"));
        transformation.Set("color", PresetValue.Scalar("ff006433"));
        var configured = new ValueEditorControl(document, Forms, transformation, "Transformer", "Transformation");
        var color = Descendants<ValueEditorControl>(configured).Single(child => Field<PresetField?>(child, "field")?.Key == "color");
        Require(Ancestors<Expander>(color).Any(expander => Equals(expander.Tag, "World effects")),
            "A configured world effect must remain in its named section instead of moving among required fields.");
    }

    private static void FieldApplicability()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var stage = PresetValue.Map();
        stage.Set("no", PresetValue.Scalar("0")); stage.Set("name", PresetValue.Scalar("Fresh")); stage.Set("duration", PresetValue.Scalar("1"));
        var editor = new ValueEditorControl(document, Forms, stage, "Stage", "Stage");
        var number = FieldInput(editor, "no"); var duration = FieldInput(editor, "duration");
        string[] stageOverrides = ["FormEditorID", "value", "weight", "mgeffect", "crafting_allowed"];
        Require(stageOverrides.All(key => FieldCard(editor, key).Visibility == Visibility.Collapsed) && FieldCard(editor, "name").Visibility == Visibility.Visible,
            "Stage 0 must hide unset inapplicable item fields while keeping its required name.");
        number.Text = "1";
        Require(stageOverrides.All(key => FieldCard(editor, key).Visibility == Visibility.Visible) && ReferenceEquals(duration, FieldInput(editor, "duration")),
            "Entering a later FOOD stage must reveal generated-item settings immediately without replacing unrelated inputs.");
        number.Text = "0";
        Require(stageOverrides.All(key => FieldCard(editor, key).Visibility == Visibility.Collapsed) && ReferenceEquals(duration, FieldInput(editor, "duration")),
            "Returning to stage 0 must hide unset item settings immediately without replacing unrelated inputs.");

        foreach (var (category, hidden) in new[]
        {
            ("FOOD", Array.Empty<string>()),
            ("MISC", new[] { "mgeffect", "crafting_allowed" }),
            ("BOOK", new[] { "value", "weight", "mgeffect", "crafting_allowed" })
        })
        {
            var later = stage.Clone(); later.Get("no")!.Text = "1";
            var categoryEditor = new ValueEditorControl(PresetDocument.Create(category, PresetKind.Custom), Forms, later, "Stage", "Stage");
            foreach (var key in stageOverrides)
                Require(FieldCard(categoryEditor, key).Visibility == (hidden.Contains(key) ? Visibility.Collapsed : Visibility.Visible),
                    $"The {category} stage must expose only applicable {key} settings.");
        }

        foreach (var (stageNumber, replacement) in new[] { ("$stage", ""), ("1", "$replacement") })
        {
            var parameterized = stage.Clone(); parameterized.Get("no")!.Text = stageNumber;
            if (replacement.Length > 0) parameterized.Set("FormEditorID", PresetValue.Scalar(replacement));
            var template = new ValueEditorControl(document, Forms, parameterized, "Stage", "Template stage", parametersAllowed: true);
            Require(stageOverrides.All(key => FieldCard(template, key).Visibility == Visibility.Visible),
                "An unresolved stage or replacement parameter must keep potentially applicable settings available.");
        }
    }

    private static void RetainedUnusedSettings()
    {
        var document = PresetDocument.Parse("""
            ownerLists:
            - owners: FoodBeef
              stages:
              - no: 0
                name: Fresh
                duration: 1
                FormEditorID: FoodVenison
                value: 5
                weight: 0.25
                crafting_allowed: true
                mgeffect:
                - FormEditorID: Heal
                  magnitude: 2
                  duration: 3
              finalFormEditorID: FoodVenison
            """, "FOOD", PresetKind.Custom);
        var value = document.Blocks.Single().Value.Get("stages")!.Items.Single();
        var editor = new ValueEditorControl(document, Forms, value, "Stage", "Stage");
        foreach (var key in new[] { "FormEditorID", "value", "weight", "mgeffect", "crafting_allowed" })
        {
            var card = FieldCard(editor, key); var unused = (Expander)card.Child;
            Require(card.Visibility == Visibility.Visible && unused.Header is string header && header.StartsWith("Unused: ") && !unused.IsExpanded &&
                Descendants<TextBlock>(unused).Any(text => text.Text.Length > 0) && Descendants<Button>(unused).Any(button => Equals(button.Content, "Remove setting")),
                "An imported inactive setting must remain visible in collapsed details with its reason and explicit removal action.");
        }
        editor.Capture(); var saved = document.Clone(); saved.Blocks.Single().Value.Get("stages")!.Items[0] = editor.Value;
        var restored = PresetDocument.Parse(saved.ToYaml(), "FOOD", PresetKind.Custom).Blocks.Single().Value.Get("stages")!.Items.Single();
        Require(restored.Get("FormEditorID")?.Text == "FoodVenison" && restored.Get("value")?.Text == "5" && restored.Get("weight")?.Text == "0.25" &&
            restored.Get("crafting_allowed")?.Text == "true" && restored.Get("mgeffect")?.Items.Single().Get("duration")?.Text == "3",
            "Opening, capturing, and serializing unused settings must preserve every imported value.");
        Click(Descendants<Button>(FieldCard(editor, "value")).Single(button => Equals(button.Content, "Remove setting")));
        Require(editor.Value.Get("value") is null && FieldCard(editor, "value").Visibility == Visibility.Collapsed && value.Get("value")?.Text == "5",
            "Only explicitly removing an unused setting may delete it from the isolated draft.");

        var generated = value.Clone(); generated.Get("no")!.Text = "1"; generated.Remove("FormEditorID");
        var later = new ValueEditorControl(document, Forms, generated, "Stage", "Stage");
        var duration = FieldInput(later, "duration");
        var weight = FieldInput(later, "weight");
        Click(Descendants<Button>(FieldCard(later, "FormEditorID")).Single(button => Equals(button.Content, "Set")));
        FieldInput(later, "FormEditorID").Text = "FoodVenison";
        Require(new[] { "value", "weight", "mgeffect" }.All(key => FieldCard(later, key).Child is Expander { Header: string label } && label.StartsWith("Unused: ")) &&
            ReferenceEquals(duration, FieldInput(later, "duration")),
            "Choosing an explicit replacement must mark generated-item overrides unused immediately without disturbing duration input.");
        Click(Descendants<Button>(FieldCard(later, "FormEditorID")).Single(button => Equals(button.Content, "Clear")));
        Require(ReferenceEquals(weight, FieldInput(later, "weight")) && weight.Text == "0.25" &&
            new[] { "value", "weight", "mgeffect" }.All(key => FieldCard(later, key).Visibility == Visibility.Visible &&
                !(FieldCard(later, key).Child is Expander { Header: string label } && label.StartsWith("Unused: "))),
            "Clearing the replacement must restore the original generated-item controls and values.");

        var inheritedDocument = document.Clone();
        var shared = inheritedDocument.Add(BlockKind.Shared, "baseStage", "Stage");
        shared.Value = value.Clone(); shared.Value.Anchor = shared.Key;
        var inherited = PresetValue.Map(); inherited.Set("<<", PresetValue.Alias(shared.Value.Anchor));
        var inheritedEditor = new ValueEditorControl(inheritedDocument, Forms, inherited, "Stage", "Inherited stage");
        inheritedEditor.UpdatePresentation(new PresetPresentation(inheritedDocument, Forms), value.Clone());
        var inheritedValue = FieldCard(inheritedEditor, "value");
        Require(inheritedValue.Child is Expander { Header: "Unused: Value", IsExpanded: false } &&
            !Descendants<Button>(inheritedValue).Any(button => Equals(button.Content, "Remove setting")) && inheritedEditor.Value.Get("value") is null,
            "An inherited stage-0 value must be identified as unused without offering to remove the shared definition's field.");
    }

    private static void ReusableDefinitionApplicability()
    {
        foreach (var kind in new[] { BlockKind.Shared, BlockKind.Template })
        foreach (var (context, number, key) in new[] { ("Stage", "0", "value"), ("Stage", "1", "value"), ("Effect", "", "magnitude") })
        {
            var document = PresetDocument.Create("FOOD", PresetKind.Custom);
            var definition = document.Add(kind, "Settings", context);
            definition.Value.Set("FormEditorID", PresetValue.Scalar(context == "Stage" ? "FoodVenison" : "0"));
            definition.Value.Set(key, PresetValue.Scalar("5"));
            definition.Value.Set("duration", PresetValue.Scalar("1"));
            if (context == "Stage")
            {
                definition.Value.Set("no", PresetValue.Scalar(number));
                definition.Value.Set("name", PresetValue.Scalar("Fresh"));
            }
            var editor = new ValueEditorControl(document, Forms, definition.Value, context, "Definition", parametersAllowed: kind == BlockKind.Template);
            var input = FieldInput(editor, key);
            Require(FieldCard(editor, key).Visibility == Visibility.Visible && input.Text == "5",
                "A reusable definition must keep settings editable because a use can override its stage number or selected form.");
            editor.Capture();
            Require(editor.Value.Get(key)?.Text == "5" && definition.Value.Get(key)?.Text == "5",
                "Opening and capturing a reusable definition must preserve its configured values.");
            input.Text = "6";
            Require(editor.Value.Get(key)?.Text == "6" && definition.Value.Get(key)?.Text == "5",
                "Reusable settings must remain editable inside the isolated draft despite the definition's current discriminator.");
        }
    }

    private static void EffectApplicability()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var empty = new ValueEditorControl(document, Forms, PresetValue.Map(), "Effect", "Effect");
        Require(FieldCard(empty, "magnitude").Visibility == Visibility.Collapsed && FieldCard(empty, "duration").Visibility == Visibility.Collapsed,
            "An effect without a chosen ID must hide unset magnitude and duration.");
        var value = PresetValue.Map(); value.Set("FormEditorID", PresetValue.Scalar(""));
        value.Set("magnitude", PresetValue.Scalar("2")); value.Set("duration", PresetValue.Scalar("5"));
        var editor = new ValueEditorControl(document, Forms, value, "Effect", "Effect");
        Require(FieldCard(editor, "magnitude").Child is Expander { Header: "Unused: Magnitude" } &&
            FieldCard(editor, "duration").Child is Expander { Header: "Unused: Duration (seconds)" },
            "Imported effect stats with an empty ID must be retained as unused settings.");
        FieldInput(editor, "FormEditorID").Text = "Heal";
        var magnitude = FieldInput(editor, "magnitude");
        Require(magnitude.Text == "2" && FieldInput(editor, "duration").Text == "5",
            "Choosing an effect must immediately restore its preserved stat inputs.");
        Click(Descendants<Button>(FieldCard(editor, "FormEditorID")).Single(button => Equals(button.Content, "Clear")));
        Require(FieldCard(editor, "magnitude").Child is Expander { Header: "Unused: Magnitude" } && editor.Value.Get("magnitude")?.Text == "2",
            "Clearing the effect ID must mark its stats unused without deleting them.");
        Click(Descendants<Button>(FieldCard(editor, "FormEditorID")).Single(button => Equals(button.Content, "Set")));
        FieldInput(editor, "FormEditorID").Text = "Heal";
        Require(ReferenceEquals(magnitude, FieldInput(editor, "magnitude")) && value.Get("FormEditorID")!.Text.Length == 0,
            "Choosing an effect again must reuse its preserved stat input while leaving the original document untouched.");
        var inheritedValue = PresetYaml.Parse("<<: *effectBase\nmagnitude: 5\nduration: 60\n");
        var inherited = new ValueEditorControl(document, Forms, inheritedValue, "Effect", "Inherited effect");
        inherited.UpdatePresentation(new PresetPresentation(document, Forms), PresetYaml.Parse("FormEditorID: Heal\nmagnitude: 5\nduration: 60\n"));
        var inheritedMagnitude = FieldInput(inherited, "magnitude");
        inheritedMagnitude.Text = "25";
        Require(ReferenceEquals(inheritedMagnitude, FieldInput(inherited, "magnitude")) && inherited.Value.Get("magnitude")?.Text == "25",
            "Pending inherited-effect expansion must keep magnitude editable while the user types.");
    }

    private static void ContextualActions()
    {
        var document = PresetDocument.Parse("""
            cooking:
              duration: 1
            formsLists: []
            """, "FOOD", PresetKind.Addon);
        var definition = document.Blocks.Single();
        var source = new ValueEditorControl(document, Forms, definition.Value, definition.Context, "Shared settings");
        Require(!Descendants<Button>(source).Any(button => Equals(button.Content, "Use shared settings or template...")),
            "A definition must not offer itself as its only reuse choice.");
        var alias = new ValueEditorControl(document, Forms, PresetValue.Alias("cooking"), "Transformer", "Shared use");
        var preview = Descendants<Expander>(alias).Single(expander => Equals(expander.Header, "Resulting settings"));
        Require(preview.Visibility == Visibility.Collapsed, "Unresolved references must not show an empty results control.");
        var options = Descendants<Expander>(alias).Single(expander => Equals(expander.Header, "More options"));
        Require(!options.IsExpanded && Descendants<Button>(options).Any(button => Equals(button.Content, "Replace with own settings...")),
            "Whole-reference replacement must remain discoverable in collapsed options.");
        alias.UpdatePresentation(new PresetPresentation(document, Forms), definition.Value.Clone());
        Require(preview.Visibility == Visibility.Visible, "Resolved settings must become available after validation.");

        var dialog = new ValueEditorWindow(document, Forms, PresetValue.Map(), "Transformer", "Transformation", blockName: "");
        var apply = Descendants<Button>(dialog).Single(button => Equals(button.Content, "Apply"));
        var cancel = Descendants<Button>(dialog).Single(button => Equals(button.Content, "Cancel"));
        Require(apply.Visibility == Visibility.Collapsed && cancel.Visibility == Visibility.Visible && cancel.IsCancel,
            "An unnamed draft must keep Cancel available and explain the missing name before offering Apply.");
        Field<TextBox>(dialog, "name").Text = "Cooking";
        Require(apply.Visibility == Visibility.Visible && apply.IsDefault, "Apply must appear when the required name is supplied.");
        dialog.Close();
    }

    private static T Field<T>(object owner, string name)
    {
        if (owner is ValueEditorWindow window) owner = window.Editor;
        return (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    }
    private static Border FieldCard(ValueEditorControl editor, string key)
    {
        var view = Field<System.Collections.IDictionary>(editor, "fields")[key]!;
        return (Border)view.GetType().GetProperty("Target")!.GetValue(view)!;
    }
    private static TextBox FieldInput(ValueEditorControl editor, string key) =>
        Descendants<TextBox>(FieldCard(editor, key)).Single();
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
    private static IEnumerable<T> Ancestors<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = LogicalTreeHelper.GetParent(child); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is T match) yield return match;
    }
    private static void Search(object window, string text) { Field<TextBox>(window, "search").Text = text; Invoke(window, "FilterForms"); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
