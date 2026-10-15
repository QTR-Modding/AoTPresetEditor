using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class SchemaPickerChecks
{
    public static void Run()
    {
        GroupFields();
        ParameterFields();
        UntypedParameterControls();
        ConflictingListContexts();
        NestedDestinationFields();
        AcousticResultSelection();
        GroupDrafts();
        GroupListEligibility();
        GroupListCreation();
    }

    private static void GroupFields()
    {
        PresetField Get(string context, string name, PresetKind kind = PresetKind.Custom) =>
            PresetSchema.GetFields(context, kind).Single(field => field.Key == name);
        Require(Get("rule", "owners").AllowsGroups, "Custom owners must offer form groups.");
        Require(Get("rule", "forms", PresetKind.Addon).AllowsGroups, "Addon forms must offer form groups.");
        Require(Get("rule", "containers").AllowsGroups, "Rule container filters must offer form groups.");
        foreach (var context in new[] { "modulator", "transformer" })
        {
            Require(Get(context, "FormEditorID").AllowsGroups && Get(context, "FormEditorID").TriggerOnly,
                "Triggers must offer groups and constrain individual forms to trigger types.");
            Require(Get(context, "containers").AllowsGroups, "Trigger inventory filters must offer groups.");
            Require(Get(context, "FormEditorID").Help.Contains("active magic effect") &&
                Get(context, "FormEditorID").Help.Contains("In inventories, an art object, effect shader, or active magic effect triggers when the inventory owner has it"),
                "Applied effect trigger help must describe world references and effects on the inventory owner.");
        }
        Require(!Get("stage", "FormEditorID").AllowsGroups && !Get("effect", "FormEditorID").AllowsGroups &&
                !Get("transformer", "finalFormEditorID").AllowsGroups, "Singular results and effects must not offer groups.");
    }

    private static void ParameterFields()
    {
        var document = PresetDocument.Parse("""
            common: &common
              FormEditorID: $heat
              duration: $time
              sound: $sound
            templates:
              cook:
                forms: $raw
                transformers:
                - <<: *common
                  finalFormEditorID: $cooked
              outer:
                forms: $input
                transformers:
                - inner($target, $seconds, $audio)
              inner:
                finalFormEditorID: $result
                duration: $time
                sound: $sound
              aliasOnly: *common
              both:
                FormEditorID: $shared
                finalFormEditorID: $shared
                duration: 1
              incompatible:
                sound: $same
                art_object: $same
              soundOnly:
                sound: $audio
            formsLists: []
            """, "FOOD", PresetKind.Addon);
        PresetField Get(string template, string parameter) => PresetSchema.GetParameterField(
            document.Blocks.Single(block => block.Kind == BlockKind.Template && block.Key == template), parameter, document)
            ?? throw new Exception($"No field inferred for {template}.{parameter}.");
        Require(Get("cook", "raw") is { Kind: FieldKind.FormList, AllowsGroups: true, MatchPresetCategory: true, RequiresEditorId: true },
            "An addon source parameter must retain group and category constraints.");
        Require(Get("cook", "heat") is { TriggerOnly: true, AllowsGroups: true }, "Merged trigger fields must retain their constraints.");
        Require(Get("cook", "time").Kind == FieldKind.Number, "Duration parameters must use numeric controls.");
        Require(Get("cook", "sound").FormTypes!.SequenceEqual(["SoundDescriptor"]), "Sound parameters must show sounds.");
        Require(Get("cook", "cooked") is { AllowsGroups: false, MatchPresetCategory: false, BoundObjectOnly: true, FormTypes: null },
            "Results accept bound objects across categories, not groups or only source item types.");
        Require(Get("outer", "audio").FormTypes!.SequenceEqual(["SoundDescriptor"]) && Get("outer", "seconds").Kind == FieldKind.Number,
            "Nested template calls must propagate field types.");
        Require(Get("aliasOnly", "time").Kind == FieldKind.Number, "An alias-only template must infer the shared body's context.");
        Require(Get("both", "shared") is { Kind: FieldKind.Form, AllowsGroups: false, TriggerOnly: true, BoundObjectOnly: true },
            "A parameter used as trigger and result must not accept a group.");
        Require(Get("incompatible", "same").FormTypes is { Length: 0 }, "Incompatible form types must yield no picker choices.");
        Require(Get("soundOnly", "audio").FormTypes!.SequenceEqual(["SoundDescriptor"]), "A standalone sound template must infer its form type.");
        document.Blocks.Single(block => block.Kind == BlockKind.Template && block.Key == "inner").Context = "Value";
        Require(Get("inner", "time").Kind == FieldKind.Number, "A generic template containing duration must infer its field context.");
    }

    private static void UntypedParameterControls()
    {
        var document = PresetDocument.Parse("""
            templates:
              incompatible:
                owners: FoodBeef
                transformers:
                - {FormEditorID: $value, duration: $value, finalFormEditorID: FoodCooked}
              unknown:
                owners: FoodBeef
                additional: $value
              trigger:
                owners: FoodBeef
                transformers:
                - {FormEditorID: $value, duration: 1, finalFormEditorID: FoodCooked}
            ownerLists:
            - incompatible(Fire)
            - unknown(Fire)
            - trigger(Fire)
            """, "FOOD", PresetKind.Custom);
        foreach (var template in document.Blocks.Where(block => block.Kind == BlockKind.Template))
        {
            var known = template.Key == "trigger";
            var field = PresetSchema.GetParameterField(template, "value", document);
            Require(known ? field is { TriggerOnly: true } : field is null,
                "Only a parameter with compatible known field uses must infer a form constraint.");
            var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule && block.Value.Text == template.Key + "(Fire)");
            var behavior = new BehaviorEditorControl(document, [], rule.Id);
            Field<TabControl>(behavior, "tabs").SelectedIndex = 1;
            var table = Field<DataGrid>(behavior, "table");
            table.SelectedItem = table.Items[0];
            table.CurrentCell = new DataGridCellInfo(table.Items[0], table.Columns.Single(column => Equals(column.Header, "value")));
            CheckControl(Field<ValueEditorControl>(behavior, "cellEditor"), known);
            Require(Field<ContextMenu>(behavior, "addMenu").Items.Count == (known ? 1 : 0),
                "Bulk form choices must be offered only for an inferred form parameter.");

            var call = new ValueEditorControl(document, [], rule.Value, "CustomRule", template.Key);
            CheckControl(Descendants<ValueEditorControl>(call).Single(), known);
        }
        var number = PresetSchema.GetFields("transformer", PresetKind.Custom).Single(field => field.Key == "duration");
        var invalidList = new ValueEditorControl(document, [], PresetYaml.Parse("[1]"), "Value", "Duration", field: number);
        CheckControl(Descendants<ValueEditorControl>(invalidList).Single(), false);

        static void CheckControl(ValueEditorControl editor, bool known)
        {
            var buttons = Descendants<Button>(editor).ToArray();
            Require(buttons.Any(button => Equals(button.Content, "Choose form...")) == known,
                "Unresolved or incompatible field kinds must not expose an unrestricted form picker; known forms must keep theirs.");
            Require(Descendants<TextBox>(editor).Any(), "Untyped arguments must remain editable as manual values.");
            if (!known)
                Require(buttons.Any(button => Equals(button.Content, "Use a list of values...")) &&
                    buttons.Any(button => Equals(button.Content, "Use a settings block...")),
                    "Untyped arguments must retain their manual list and settings-block choices.");
        }
    }

    private static void ConflictingListContexts()
    {
        foreach (var (first, second) in new[] { ("transformers", "stages"), ("stages", "transformers") })
        {
            var document = PresetDocument.Parse($"""
                templates:
                  sharedRows:
                    owners: FoodBeef
                    {first}: $rows
                    {second}: $rows
                    finalFormEditorID: FoodCooked
                ownerLists: []
                """, "FOOD", PresetKind.Custom);
            var template = document.Blocks.Single();
            Require(PresetSchema.GetParameterField(template, "rows", document) is null,
                "A list shared by different field schemas must not inherit whichever schema appears first.");
            var argument = PresetYaml.Parse("[{no: 1, name: Later, duration: 1, FormEditorID: Fire, finalFormEditorID: FoodCooked}]");
            var rule = document.Add(BlockKind.Rule, "Shared rows", "CustomRule");
            rule.Value = PresetValue.Scalar(TemplateCall.Format("sharedRows", [argument]));
            var behavior = new BehaviorEditorControl(document, [], rule.Id);
            Field<TabControl>(behavior, "tabs").SelectedIndex = 1;
            var table = Field<DataGrid>(behavior, "table");
            table.SelectedItem = table.Items[0];
            table.CurrentCell = new DataGridCellInfo(table.Items[0], table.Columns.Single(column => Equals(column.Header, "rows")));
            CheckControl(Field<ValueEditorControl>(behavior, "cellEditor"));

            var call = new ValueEditorControl(document, [], rule.Value, "CustomRule", "Shared rows");
            CheckControl(Descendants<ValueEditorControl>(call).Single(editor => editor.Value.Kind == ValueKind.Sequence));
        }

        static void CheckControl(ValueEditorControl editor)
        {
            var before = PresetYaml.Write(editor.Value);
            var buttons = Descendants<Button>(editor).ToArray();
            Require(!buttons.Any(button => Equals(button.Content, "Choose form...") || Equals(button.Content, "Choose forms...")),
                "A conflicting structured argument must not regain a guessed field schema or its form choices.");
            Require(buttons.Any(button => Equals(button.Content, "Add value")) &&
                buttons.Any(button => Equals(button.Content, "Add setting...")),
                "A conflicting structured argument must retain manual list and settings-block editing.");
            editor.Capture();
            Require(PresetYaml.Write(editor.Value) == before, "Opening a generic structured argument must preserve its fields and values.");
            var duration = Descendants<ValueEditorControl>(editor).Single(child => Field<PresetField?>(child, "field")?.Key == "duration");
            Descendants<TextBox>(duration).Single().Text = "2";
            editor.Capture();
            Require(editor.Value.Items.Single().Get("duration")?.Text == "2",
                "Manual edits must still update a conflicting structured argument.");
        }
    }

    private static void NestedDestinationFields()
    {
        const string yaml = """
            templates:
              identity: $x
              forward: identity($x)
              items: [forward($x)]
              stageItem:
                FormEditorID: forward($replacement)
              cooking:
                owners: items($raw)
                stages:
                - {no: 0, name: '', duration: 1}
                - <<: stageItem($stage)
                  no: 1
                  name: ''
                  duration: 1
                finalFormEditorID: forward($result)
                containers: forward($where)
                transformers:
                - FormEditorID: Fire
                  duration: 1
                  finalFormEditorID: forward($result)
                  containers: forward($where)
              conflicting:
                sound: forward($same)
                art_object: identity($same)
            ownerLists:
            - cooking(FoodBeef, FoodCooked, FoodFinal, Player)
            """;
        var expanded = PresetYaml.Parse(PresetCatalog.Expand(yaml, CancellationToken.None).GetAwaiter().GetResult());
        var expandedRule = expanded.Get("ownerLists")!.Items.Single();
        Require(expandedRule.Get("owners")!.Items.Single().Text == "FoodBeef" &&
            expandedRule.Get("stages")!.Items[1].Get("FormEditorID")!.Text == "FoodCooked" &&
            expandedRule.Get("finalFormEditorID")!.Text == "FoodFinal",
            "The native helper must support scalar, list, and merged-map templates used in this regression.");
        var document = PresetDocument.Parse(yaml, "FOOD", PresetKind.Custom);
        var template = document.Blocks.Single(block => block.Key == "cooking");
        PresetField Parameter(string name) => PresetSchema.GetParameterField(template, name, document)
            ?? throw new Exception("No field inferred for cooking." + name);
        Require(Parameter("raw") is { Kind: FieldKind.Form, MatchPresetCategory: true, RequiresEditorId: true },
            "A form passed through a list wrapper must retain the source field's constraints.");
        Require(Parameter("stage") is { MatchSourceType: true, MatchPresetCategory: true, TriggerOnly: false },
            "A merged map inside a stage must infer its FormEditorID as a stage item, not a trigger.");
        Require(Parameter("result") is { Kind: FieldKind.Form, BoundObjectOnly: true },
            "Scalar forwarding templates must retain the final item's bound-object constraint.");
        Require(Parameter("where") is { InventoryOwnerOnly: true },
            "A parameter passed through nested rule and trigger filters must retain inventory-owner-only picker choices.");
        var conflict = document.Blocks.Single(block => block.Key == "conflicting");
        Require(PresetSchema.GetParameterField(conflict, "same", document)?.FormTypes is { Length: 0 },
            "Different destination types must still intersect when a parameter passes through separate wrappers.");
        FormRow[] forms =
        [
            new("Beef", "FoodBeef", "Ingestible", "Test.esp", "Test.esp", "0x800~Test.esp", ItemCategories.FOOD, true, true),
            new("Cooked", "FoodCooked", "Ingestible", "Test.esp", "Test.esp", "0x801~Test.esp", ItemCategories.FOOD, true, true),
            new("Final", "FoodFinal", "Ingestible", "Test.esp", "Test.esp", "0x802~Test.esp", ItemCategories.FOOD, true, true),
            new("Heal", "Heal", "MagicEffect", "Test.esp", "Test.esp", "0x803~Test.esp")
        ];
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var behavior = new BehaviorEditorControl(document, forms, rule.Id);
        Field<TabControl>(behavior, "tabs").SelectedIndex = 1;
        var table = Field<DataGrid>(behavior, "table");
        table.SelectedItem = table.Items[0];
        table.CurrentCell = new DataGridCellInfo(table.Items[0], table.Columns.Single(column => Equals(column.Header, "result")));
        var cell = Field<ValueEditorControl>(behavior, "cellEditor");
        Require(Field<PresetField?>(cell, "field") is { BoundObjectOnly: true },
            "The actual Variables cell must retain the nested final item's constraint.");
        CheckResultPicker(cell, forms);

        var final = PresetSchema.GetFields("rule", PresetKind.Custom).Single(field => field.Key == "finalFormEditorID");
        var direct = new ValueEditorControl(document, forms, PresetValue.Scalar("forward(FoodFinal)"),
            "Value", "Final item", field: final);
        var argument = Descendants<ValueEditorControl>(direct).Single();
        Require(Field<PresetField?>(argument, "field") is { BoundObjectOnly: true },
            "Editing a nested call directly must pass its destination field into the argument editor.");
        CheckResultPicker(argument, forms);

        var containers = PresetSchema.GetFields("rule", PresetKind.Addon).Single(field => field.Key == "containers");
        Require(PresetSchema.DefaultDescription(containers, "rule", PresetKind.Addon) ==
            "Uses the existing filter", "Unset addon containers must describe the existing filters.");
        var allowed = PresetSchema.GetFields("transformer", PresetKind.Addon).Single(field => field.Key == "allowed_stages");
        Require(!PresetSchema.DefaultDescription(allowed, "transformer", PresetKind.Addon).Contains("containers"),
            "The container explanation must not replace the unrelated allowed-stage explanation.");
    }

    private static void CheckResultPicker(ValueEditorControl editor, IReadOnlyList<FormRow> forms)
    {
        var selection = (FormSelection)typeof(ValueEditorControl).GetMethod("FormSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, null)!;
        var picker = new FormPickerWindow(forms, selection: selection);
        try
        {
            Require(Field<DataGrid>(picker, "results").Items.Cast<FormRow>().SequenceEqual(forms.Take(3)),
                "A final item reached through a nested template must offer bound objects and exclude magic effects.");
        }
        finally { picker.Close(); }
    }
    private static void AcousticResultSelection()
    {
        FormRow Row(string type) => new(type, type, type, "Test.esp", "Test.esp", type,
            IsTrigger: true, IsBoundObject: true);
        var acoustic = Row("AcousticSpace");
        FormRow[] forms = [acoustic, Row("Static"), Row("Weapon")];
        foreach (var context in new[] { "rule", "transformer" })
        {
            var field = PresetSchema.GetFields(context, PresetKind.Custom).Single(field => field.Key == "finalFormEditorID");
            var selection = FormSelection.ForField(field, "FOOD");
            Require(selection.Matches(acoustic), "AoT-accepted acoustic space identifiers must remain valid when imported or entered manually.");
            var picker = new FormPickerWindow(forms, selection: selection);
            Require(Field<DataGrid>(picker, "results").Items.Cast<FormRow>().SequenceEqual(forms.Skip(1)),
                "Result pickers must omit acoustic spaces while retaining inventory and world objects.");
            Require(!Field<ComboBox>(picker, "types").Items.Cast<string>().Contains("AcousticSpace"),
                "The result type dropdown must omit acoustic spaces too.");
            picker.Close();
        }
        Require(new FormSelection().IsPickerChoice(acoustic) && new FormSelection(TriggerOnly: true).IsPickerChoice(acoustic),
            "Result picker policy must not narrow the form browser or trigger selection.");
        var document = PresetDocument.Parse("""
            ownerLists:
            - owners: FoodBeef
              stages: [{no: 0, name: '', duration: 1}]
              finalFormEditorID: AcousticSpace
              transformers:
              - {FormEditorID: Static, finalFormEditorID: AcousticSpace, duration: 1}
            """, "FOOD", PresetKind.Custom);
        var errors = (IReadOnlyList<string>)typeof(PresetValidation)
            .GetMethod("KnownFormErrors", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [document.Blocks.Single().Value, "FOOD", PresetKind.Custom, new PresetPresentation(document, forms)])!;
        Require(errors.Count == 0, "Hiding acoustic space choices must not invalidate existing final result identifiers.");
    }

    private static void GroupDrafts()
    {
        FormRow[] forms =
        [
            new("Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C99~Skyrim.esm", ItemCategories.FOOD, true),
            new("Crack", "IceCrack", "SoundDescriptor", "Sounds.esp", "Sounds.esp", "0x800~Sounds.esp")
        ];
        var installed = new FormGroupDefinition("Existing", ["FoodBeef", "0x800~Sounds.esp", "FF123456"], "existing.txt");
        var picker = new GroupPickerWindow([installed], forms, new FormSelection(Category: "FOOD"));
        Require(Field<Button>(picker, "choose").Visibility == Visibility.Collapsed, "Choose group must stay hidden until selection.");
        Field<ListBox>(picker, "groupList").SelectedItem = installed;
        Require(Field<Button>(picker, "choose").Visibility == Visibility.Visible, "A selected group must become selectable.");
        Require(Field<DataGrid>(picker, "members").Items.Count == 3, "Unresolved and inapplicable group members must remain visible.");
        Require(Field<TextBlock>(picker, "description").Text.Contains("1 known members applicable"), "Known member applicability must reflect the field constraint.");
        Field<List<FormGroupDefinition>>(picker, "created").Add(new("Draft", [forms[0].Identifier]));
        picker.Close();
        Require(picker.SelectedGroup is null && picker.CreatedGroups.Count == 0, "Canceling the picker must not expose pending group drafts.");
        Require(installed.Members.Count == 3, "Viewing a group must preserve its source members.");
        var editor = new GroupEditorWindow([installed], forms, initialMembers: ["FoodBeef", "IceCrack"]);
        var create = Field<Button>(editor, "create");
        Require(!create.IsEnabled, "A group with members still needs a valid name before creation.");
        Field<TextBox>(editor, "name").Text = "existing";
        Require(Field<TextBlock>(editor, "nameStatus").Text.Contains("already exists"), "New group names must avoid case-insensitive filename collisions.");
        var members = Field<DataGrid>(editor, "members");
        members.SelectedItem = members.Items[1];
        Click(Descendants<Button>(editor).Single(button => Equals(button.Content, "Remove selected")));
        Require(members.Items.Count == 1 && !create.IsEnabled, "Editing members must not permit a duplicate group name.");
        Field<TextBox>(editor, "name").Text = "FoodBeef";
        Require(Field<TextBlock>(editor, "nameStatus").Text.Contains("use the group first"), "Form-name collisions must explain group-first resolution.");
        Require(create.IsEnabled, "Correcting the name must enable creation with the remaining valid member.");
        Field<TextBox>(editor, "name").Clear();
        Require(!create.IsEnabled, "Clearing a previously valid name must prevent creation again.");
        editor.Close();
    }

    private static readonly FormRow[] GroupForms =
    [
        new("Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C99~Skyrim.esm", ItemCategories.FOOD),
        new("Venison", "FoodVenison", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C9E~Skyrim.esm", ItemCategories.FOOD)
    ];

    private static void GroupListEligibility()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        document.PendingGroups.Add(new("PendingMeat", ["FoodBeef"]));
        FormGroupDefinition[] installed = [new("InstalledMeat", ["FoodVenison"])];
        var owners = PresetSchema.GetFields("rule", PresetKind.Custom).Single(field => field.Key == "owners");
        ValueEditorControl Editor(PresetValue list, PresetField? field = null) =>
            new(document, GroupForms, list, "Value", "Applies to", field: field ?? owners, groups: installed);
        bool Offered(ValueEditorControl editor) => Descendants<Button>(editor).Any(button =>
            Equals(button.Content, "Create group from list...") && button.Visibility == Visibility.Visible);
        PresetValue List(params PresetValue[] items) { var value = PresetValue.List(); value.Items.AddRange(items); return value; }

        var concrete = List(PresetValue.Scalar("FoodBeef"), PresetValue.Scalar("0x800~Optional.esp"));
        Require(Offered(Editor(concrete)), "Known forms and unresolved plugin/local identifiers must allow group creation.");
        Require(!Offered(Editor(PresetValue.List())), "An empty selection must not offer group creation.");
        var tagged = PresetValue.Scalar("FoodBeef"); tagged.Tag = "tag:yaml.org,2002:str";
        Require(Offered(Editor(List(tagged))), "An explicitly tagged literal form string must remain eligible.");
        tagged.Tag = "!custom";
        Require(!Offered(Editor(List(tagged))), "Creating a group must not discard an unknown member tag.");
        tagged.Tag = "tag:yaml.org,2002:null";
        Require(!Offered(Editor(List(tagged))), "An explicitly tagged null must not become a group member.");
        foreach (var item in new[]
        {
            PresetValue.Scalar("Beef"), PresetValue.Scalar("UnresolvedEditorId"), PresetValue.Scalar("PendingMeat"),
            PresetValue.Scalar("InstalledMeat"), PresetValue.Scalar("Raw~Meat"), PresetValue.Scalar("null"), PresetValue.Scalar("$meat"),
            PresetValue.Scalar("pick(FoodBeef)"), PresetValue.Alias("meats"), PresetValue.Map(), PresetValue.UnsetArgument()
        })
            Require(!Offered(Editor(List(PresetValue.Scalar("FoodBeef"), item))),
                "Group creation must not reinterpret a nonliteral or nonconcrete list entry: " + item.Kind + " " + item.Text);
        foreach (var annotate in new Action<PresetValue>[]
        {
            value => value.Anchor = "member",
            value => value.Comments.Add("Member note"),
            value => value.TrailingComments.Add("After member"),
            value => value.InlineComment = "Inline member note"
        })
        {
            var item = PresetValue.Scalar("FoodBeef"); annotate(item);
            Require(!Offered(Editor(List(item))), "Group creation must not discard annotations on an individual member.");
        }
        Require(!Offered(Editor(concrete, owners with { AllowsGroups = false })),
            "Fields that cannot accept groups must not offer group creation.");
        Require(Offered(Editor(concrete, PresetSchema.GetFields("rule", PresetKind.Addon).Single(field => field.Key == "forms"))),
            "Addon form lists must offer the same group creation action.");

        var templateDocument = PresetDocument.Parse("""
            templates:
              age:
                owners: $items
                stages: [{no: 0, name: '', duration: 1}]
                finalFormEditorID: ''
            ownerLists:
            - age(PendingMeat)
            """, "FOOD", PresetKind.Custom);
        templateDocument.PendingGroups.Add(new("PendingMeat", ["FoodBeef"]));
        var template = templateDocument.Blocks.Single(block => block.Kind == BlockKind.Template);
        var parameter = PresetSchema.GetParameterField(template, "items", templateDocument);
        Require(parameter is { AllowsGroups: true, Kind: FieldKind.FormList, MatchPresetCategory: true },
            "Owner template parameters must retain form-group and category choices.");
        var parameterEditor = new ValueEditorControl(templateDocument, GroupForms, PresetValue.Scalar("PendingMeat"),
            "Value", "Items", field: parameter);
        parameterEditor.Capture();
        Require(parameterEditor.Value.Text == "PendingMeat" &&
            Descendants<Button>(parameterEditor).Any(button => Equals(button.Content, "Choose form group...")),
            "Editing an owner parameter must preserve its group selector and offer group selection.");
    }

    private static void GroupListCreation()
    {
        var document = PresetDocument.Parse("""
            ownerLists:
            - owners: [FoodBeef, 0x65C9E~Skyrim.esm, 0x800~Optional.esp, FoodBeef]
              stages: [{no: 0, name: '', duration: 1}]
              finalFormEditorID: ''
            """, "FOOD", PresetKind.Custom);
        var rule = document.Blocks.Single();
        var originalSelection = rule.Value.Get("owners")!;
        originalSelection.Anchor = "selectedMeats";
        originalSelection.Comments.Add("Selected meats");
        originalSelection.InlineComment = "Keep this note";
        originalSelection.TrailingComments.Add("After selection");
        rule.Value.Set("containers", PresetValue.Alias("selectedMeats"));
        var original = document.ToDraft();
        var editor = new BehaviorEditorControl(document, GroupForms, rule.Id);
        var changed = 0; editor.Changed += (_, _) => changed++;
        var listEditor = Descendants<ValueEditorControl>(Field<ValueEditorControl>(editor, "commonEditor"))
            .Single(value => value.Value.Kind == ValueKind.Sequence && Field<PresetField?>(value, "field")?.Key == "owners");
        var before = PresetYaml.Write(listEditor.Value);
        var beforeDraft = editor.Document.ToDraft();
        string[] expected = [GroupForms[0].Identifier, GroupForms[1].Identifier, "0x800~Optional.esp"];
        void CheckPrefill(GroupEditorWindow dialog)
        {
            Require(Field<List<string>>(dialog, "identifiers").SequenceEqual(expected),
                "The group editor must prefill canonical known identifiers, retain unresolved plugin forms, and remove duplicate members.");
            Require(editor.Document.ToDraft() == beforeDraft && PresetYaml.Write(listEditor.Value) == before,
                "Opening a detached group draft must not change the selector or pending definitions.");
            Field<TextBox>(dialog, "name").Text = "SavedMeat";
            var grid = Field<DataGrid>(dialog, "members");
            grid.SelectedItem = grid.Items[1];
            Click(Descendants<Button>(dialog).Single(button => Equals(button.Content, "Remove selected")));
        }
        ShowGroupEditor(editor, listEditor, dialog =>
        {
            CheckPrefill(dialog);
            Click(Descendants<Button>(dialog).Single(button => Equals(button.Content, "Cancel")));
        });
        editor.Capture();
        Require(editor.Document.ToDraft() == beforeDraft && PresetYaml.Write(listEditor.Value) == before && changed == 0,
            "Cancelling a renamed and edited group must leave the full draft unchanged and emit no edit.");
        ShowGroupEditor(editor, listEditor, dialog =>
        {
            CheckPrefill(dialog);
            Click(Field<Button>(dialog, "create"));
        });
        editor.Capture();
        var selection = editor.Document.Blocks.Single(block => block.Id == rule.Id).Value.Get("owners")!;
        var group = editor.Document.PendingGroups.Single();
        Require(selection is { Kind: ValueKind.Scalar, Text: "SavedMeat", Anchor: "selectedMeats" } &&
            selection.Comments.SequenceEqual(originalSelection.Comments) &&
            selection.TrailingComments.SequenceEqual(originalSelection.TrailingComments) &&
            selection.InlineComment == originalSelection.InlineComment && editor.Document.ToYaml().Contains("*selectedMeats"),
            "Accepting the group must replace the list with its name while preserving the selection's anchor and comments.");
        Require(group.Name == "SavedMeat" && group.Members.SequenceEqual([GroupForms[0].Identifier, "0x800~Optional.esp"]) &&
            changed > 0 && document.ToDraft() == original,
            "Only acceptance must commit the edited members to the isolated document.");
        Require(PresetDocument.FromDraft(editor.Document.ToDraft()).ToDraft() == editor.Document.ToDraft(),
            "The created group and annotated selector must survive a draft round trip together.");
        Require(!Descendants<Button>(listEditor).Any(button => Equals(button.Content, "Create group from list...")),
            "The scalar group reference must replace the list's creation action.");
    }

    private static void ShowGroupEditor(BehaviorEditorControl editor, ValueEditorControl listEditor, Action<GroupEditorWindow> respond)
    {
        var host = new Window { Content = editor, Width = 1000, Height = 750, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        Exception? failure = null;
        try
        {
            host.Show();
            var response = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var dialog = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                    .OfType<GroupEditorWindow>().Single(window => window.Owner == host);
                try { dialog.Opacity = 0; dialog.ShowInTaskbar = false; respond(dialog); }
                catch (Exception error) { failure = error; dialog.DialogResult = false; }
            }));
            try { Click(Descendants<Button>(listEditor).Single(button => Equals(button.Content, "Create group from list..."))); }
            finally { response.Abort(); }
            if (failure is not null) throw failure;
            Require(response.Status == DispatcherOperationStatus.Completed, "The list action must open its owned group editor.");
        }
        finally { host.Content = null; host.Close(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
