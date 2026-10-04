using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AoTPresetEditor;

internal static class SchemaPickerChecks
{
    public static void Run()
    {
        GroupFields();
        ParameterFields();
        AcousticResultSelection();
        GroupDrafts();
    }

    private static void GroupFields()
    {
        PresetField Get(string context, string name, PresetKind kind = PresetKind.Custom) =>
            PresetSchema.GetFields(context, kind).Single(field => field.Key == name);
        Require(!Get("rule", "owners").AllowsGroups, "Custom owners must not offer form groups.");
        Require(Get("rule", "forms", PresetKind.Addon).AllowsGroups, "Addon forms must offer form groups.");
        Require(Get("rule", "containers").AllowsGroups, "Rule container filters must offer form groups.");
        foreach (var context in new[] { "modulator", "transformer" })
        {
            Require(Get(context, "FormEditorID").AllowsGroups && Get(context, "FormEditorID").TriggerOnly,
                "Triggers must offer groups and constrain individual forms to trigger types.");
            Require(Get(context, "containers").AllowsGroups, "Trigger inventory filters must offer groups.");
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
        var editor = new GroupEditorWindow([installed], forms);
        Field<TextBox>(editor, "name").Text = "existing";
        Require(Field<TextBlock>(editor, "nameStatus").Text.Contains("already exists"), "New group names must avoid case-insensitive filename collisions.");
        Field<TextBox>(editor, "name").Text = "FoodBeef";
        Require(Field<TextBlock>(editor, "nameStatus").Text.Contains("use the group first"), "Form-name collisions must explain group-first resolution.");
        editor.Close();
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
