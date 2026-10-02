using System.Reflection;
using System.Windows.Controls;
using AoTPresetEditor;

internal static class PresetPresentationChecks
{
    private static readonly FormRow[] Forms =
    [
        new("Raw Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C99~Skyrim.esm", ItemCategories.FOOD),
        new("Cooked Beef", "FoodBeefCooked", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x721E8~Skyrim.esm", ItemCategories.FOOD),
        new("Venison", "FoodVenison", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x669A2~Skyrim.esm", ItemCategories.FOOD),
        new("Venison Chop", "FoodVenisonCooked", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x722BD~Skyrim.esm", ItemCategories.FOOD),
        new("Crack", "IceCrack", "SoundDescriptor", "Sounds.esp", "Sounds.esp", "0x800~Sounds.esp")
    ];

    public static void Run()
    {
        var document = PresetDocument.Parse("""
            cooking: &cooking
              FormEditorID: Fire
              duration: 0.06
            portableHeat: &portableHeat
              <<: *cooking
              FormEditorID: Torch
            templates:
              cookFood:
                forms: $raw
                transformers:
                - <<: *cooking
                  finalFormEditorID: $cooked
                - <<: *portableHeat
                  finalFormEditorID: $cooked
            formsLists:
            - cookFood(00065C99, 000721E8)
            - cookFood(FoodVenison, FoodVenisonCooked)
            """, "FOOD", PresetKind.Addon);
        var source = document.ToYaml();
        var expanded = PresetCatalog.Expand(source, CancellationToken.None).GetAwaiter().GetResult();
        var presentation = new PresetPresentation(document, Forms, expandedYaml: expanded);
        var rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        Require(presentation.RuleSummary(rules[0]) == "Raw Beef → Cooked Beef · 2 triggered transformations",
            "The overview must describe the real transformation instead of repeating the template name.");
        Require(presentation.RuleSummary(rules[1]) == "Venison → Venison Chop · 2 triggered transformations",
            "Repeated uses of a template must be distinguishable.");
        Require(presentation.EffectiveValue(rules[0])?.Get("transformers")?.Items[1].Get("duration")?.Text == "0.06",
            "The presentation must use native expansion for inherited fields.");
        Require(presentation.FormLabel("0x000800~sounds.esp") == "Crack" && presentation.FormLabel("00065C99") == "Raw Beef",
            "Equivalent supported identifiers should resolve to readable names.");
        Require(presentation.FormLabel("01000800") == "01000800", "Do not guess the load order for full form IDs.");
        Require(presentation.FormLabel("FoodBeef", includeIdentifier: true) == "Raw Beef (FoodBeef)",
            "Exact authored identifiers must remain available beside names.");
        var cooking = document.Blocks.Single(block => block.Key == "cooking");
        Require(presentation.Uses(cooking).Count == 4 && presentation.Uses(cooking).Contains(rules[1]),
            "Shared-setting impact must include rules that reach the setting through a template.");
        Require(presentation.ReferenceLabel(rules[0].Value) == "Template: cookFood" &&
            new PresetPresentation(document, Forms).RuleSummary(rules[0]).Contains("Raw Beef, Cooked Beef"),
            "An incomplete document must still show useful template arguments before expansion succeeds.");
        Require(document.ToYaml() == source, "Presentation must not mutate the authored preset.");

        var group = new FormGroupDefinition("Mixed", ["00065C99", "0x000800~Sounds.esp", "UnknownOptionalForm"]);
        var picker = new GroupPickerWindow([group], Forms, new(Category: "FOOD"), purpose: "Choose affected item group");
        Field<ListBox>(picker, "groupList").SelectedItem = group;
        var rows = Field<DataGrid>(picker, "members").Items.Cast<object>().ToArray();
        var statuses = rows.Select(row => row.GetType().GetProperty("Status")!.GetValue(row)).ToArray();
        Require(statuses.SequenceEqual(["Available", "Not applicable", "Not found"]),
            "Every group member must show whether it exists and fits this field.");
        Require(rows.Select(row => row.GetType().GetProperty("Identifier")!.GetValue(row)).SequenceEqual(group.Members),
            "Group previews must preserve unresolved identifiers and their spelling.");
        Require(picker.Title == "Choose affected item group", "A group picker must keep the editing task in its heading.");
        picker.Close();
        var formPicker = new FormPickerWindow(Forms, selection: new(Category: "FOOD"), purpose: "Choose result item");
        Require(formPicker.Title == "Choose result item" && Field<DataGrid>(formPicker, "results").Items.Count == 4,
            "Contextual form selection must preserve applicability filtering.");
        formPicker.Close();
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
