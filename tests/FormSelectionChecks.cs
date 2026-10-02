using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AoTPresetEditor;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

internal static class FormSelectionChecks
{
    public static void Run()
    {
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("PickerTest.esp"), SkyrimRelease.SkyrimSE);
        var food = mod.Ingestibles.AddNew();
        food.EditorID = "Food"; food.Flags = Ingestible.Flag.FoodItem;
        var otherFood = mod.Ingestibles.AddNew();
        otherFood.EditorID = "OtherFood"; otherFood.Flags = Ingestible.Flag.FoodItem;
        var medicine = mod.Ingestibles.AddNew();
        medicine.EditorID = "Medicine"; medicine.Flags = Ingestible.Flag.Medicine;
        var poisonedFood = mod.Ingestibles.AddNew();
        poisonedFood.EditorID = "PoisonedFood"; poisonedFood.Flags = Ingestible.Flag.FoodItem | Ingestible.Flag.Poison;
        var unclassified = mod.Ingestibles.AddNew(); unclassified.EditorID = "Unclassified";
        var foodIngredient = mod.Ingredients.AddNew();
        foodIngredient.EditorID = "FoodIngredient"; foodIngredient.Flags = Ingredient.Flag.FoodItem;
        var ingredient = mod.Ingredients.AddNew(); ingredient.EditorID = "Ingredient";
        var armor = mod.Armors.AddNew(); armor.EditorID = "Armor";
        var misc = mod.MiscItems.AddNew(); misc.EditorID = "Misc";
        var key = mod.Keys.AddNew(); key.EditorID = "Key";
        var ammunition = mod.Ammunitions.AddNew(); ammunition.EditorID = "Ammunition";
        var light = mod.Lights.AddNew(); light.EditorID = "Light";
        var spell = mod.Spells.AddNew(); spell.EditorID = "Spell";
        var armorAddon = mod.ArmorAddons.AddNew(); armorAddon.EditorID = "ArmorAddon";
        var location = mod.Locations.AddNew(); location.EditorID = "Location";
        var perk = mod.Perks.AddNew(); perk.EditorID = "Perk";
        var fire = mod.Statics.AddNew(); fire.EditorID = "Fire";
        var keyword = mod.Keywords.AddNew(); keyword.EditorID = "Keyword";
        var list = mod.FormLists.AddNew(); list.EditorID = "FormList";
        var sound = mod.SoundDescriptors.AddNew(); sound.EditorID = "Sound";
        var effect = mod.MagicEffects.AddNew(); effect.EditorID = "MagicEffect";
        var catalog = new Dictionary<FormKey, FormRow>();
        typeof(FormCatalog).GetMethod("AddRecords", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [mod, "PickerTest.esp", catalog]);
        var forms = catalog.Values.ToArray();
        FormRow Row(string editorId) => forms.Single(form => form.EditorId == editorId);

        Require(Row("Food").Categories == ItemCategories.FOOD, "Food flags must select the FOOD module.");
        Require(Row("Medicine").Categories == ItemCategories.MEDC, "Medicine must use its explicit flag.");
        Require(Row("PoisonedFood").Categories == ItemCategories.POSN, "A poison must not be offered as food even with its food flag set.");
        Require(Row("Unclassified").Categories == ItemCategories.None, "Unflagged ingestibles must not be assumed to be medicine.");
        Require(Row("FoodIngredient").Categories == (ItemCategories.FOOD | ItemCategories.INGR), "Food ingredients must remain eligible for both categories.");
        Require(Row("Ingredient").Categories == ItemCategories.INGR, "Ordinary ingredients belong to INGR only.");
        Require(Row("Armor").Categories == ItemCategories.ARMO && Row("Misc").Categories == ItemCategories.MISC,
            "Equipment and miscellaneous item categories must come from record type.");
        Require(Row("Key").Categories == ItemCategories.None, "Keys must not inherit the MISC category from the engine class hierarchy.");
        foreach (var id in new[] { "Food", "FoodIngredient", "Armor", "Key", "Ammunition", "Light", "Spell", "Location", "Perk", "Fire" })
            Require(Row(id).IsTrigger, $"{id} must be accepted by the trigger picker.");
        foreach (var id in new[] { "Keyword", "FormList", "Sound", "MagicEffect", "ArmorAddon" })
            Require(!Row(id).IsTrigger, $"{id} must not be offered as a direct trigger.");

        var resultSelection = new FormSelection(BoundObjectOnly: true);
        foreach (var id in new[] { "Food", "Medicine", "PoisonedFood", "Ingredient", "Armor", "Key", "Ammunition", "Light", "Spell", "Fire" })
            Require(resultSelection.Matches(Row(id)), $"{id} must be offered as a bound-object result, independently of the source category.");
        foreach (var id in new[] { "Keyword", "FormList", "Sound", "MagicEffect", "ArmorAddon", "Location", "Perk" })
            Require(!resultSelection.Matches(Row(id)), $"{id} must not be offered as a bound-object result.");
        Require(new FormSelection(BoundObjectOnly: true, TriggerOnly: true).Matches(Row("Key")) &&
            !new FormSelection(BoundObjectOnly: true, TriggerOnly: true).Matches(Row("Location")),
            "Combined parameter constraints must retain the bound-object requirement.");

        var foodSelection = new FormSelection(["Ingestible", "Ingredient"], "FOOD");
        Require(forms.Count(foodSelection.Matches) == 3, "FOOD source selection must hide medicine, poison, and ordinary ingredients.");
        Require(new FormSelection(Category: "INGR").Matches(Row("FoodIngredient")), "An overlapping category must not be lost during filtering.");
        Require(new FormSelection().Matches(Row("Sound")), "No constraint must preserve unrestricted browsing.");
        Require(!forms.Any(new FormSelection([]).Matches), "An empty type intersection must have no valid choices.");
        Require(!forms.Any(new FormSelection(Category: "Unknown").Matches), "An unknown category must not broaden a constrained picker.");

        var picker = new FormPickerWindow(forms, multiple: true, selection: foodSelection);
        var grid = Field<DataGrid>(picker, "results");
        Require(grid.Items.Count == 3, "The picker must apply its semantic constraint before showing results.");
        Require(Field<Button>(picker, "choose").Visibility == Visibility.Collapsed &&
            Field<Button>(picker, "clear").Visibility == Visibility.Collapsed, "Unavailable selection actions must be hidden.");
        grid.SelectedItems.Add(Row("Food"));
        Require(Field<Button>(picker, "choose").Visibility == Visibility.Visible &&
            Field<Button>(picker, "clear").Visibility == Visibility.Visible, "Choosing an applicable row must show selection actions.");
        picker.Close();

        var triggerPicker = new FormPickerWindow(forms, selection: new(TriggerOnly: true));
        Require(Field<DataGrid>(triggerPicker, "results").Items.Cast<FormRow>().All(form => form.IsTrigger),
            "Trigger filtering must exclude unrelated record types.");
        triggerPicker.Close();
        KnownForms(forms);
    }

    private static void KnownForms(IReadOnlyList<FormRow> forms)
    {
        var document = PresetDocument.Parse("""
            ownerLists:
            - owners: Food
              stages:
              - {no: 0, name: '', duration: 1}
              - {no: 1, name: Cooled, duration: 2, FormEditorID: OtherFood}
              finalFormEditorID: Key
              transformers:
              - {FormEditorID: Fire, finalFormEditorID: Ammunition, duration: 1}
            """, "FOOD", PresetKind.Custom);
        var rule = document.Blocks.Single().Value;
        var catalog = new PresetPresentation(document, forms);
        var food = forms.Single(form => form.EditorId == "Food");
        var replacement = forms.Single(form => form.EditorId == "OtherFood");
        Require(catalog.ResolveForm(food.Identifier) == catalog.ResolveForm("Food"), "Known identifiers must resolve to one catalog form.");
        Require(catalog.SourceForms(rule).SequenceEqual([food]), "Concrete rule sources must be available without rescanning the catalog.");
        IReadOnlyList<string> Errors(PresetValue value) => (IReadOnlyList<string>)typeof(PresetValidation)
            .GetMethod("KnownFormErrors", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [value, "FOOD", catalog])!;
        Require(Errors(rule).Count == 0, "A FOOD rule may produce keys and ammunition while explicit stages stay compatible with its source.");
        foreach (var result in new[] { "Location", "Perk", "FormList", "Sound", "MagicEffect" })
        {
            var invalid = rule.Clone(); invalid.Set("finalFormEditorID", PresetValue.Scalar(result));
            Require(Errors(invalid).Any(error => error.StartsWith("Final result item:", StringComparison.Ordinal)),
                $"Known non-bound result {result} must not pass validation.");
        }
        var badTransformation = rule.Clone();
        badTransformation.Get("transformers")!.Items[0].Set("finalFormEditorID", PresetValue.Scalar("Location"));
        Require(Errors(badTransformation).Single().StartsWith("Transformers 1: Result item:", StringComparison.Ordinal),
            "Known transformer results need the same bound-object check and an addressable field path.");
        var crossClass = rule.Clone(); crossClass.Get("stages")!.Items[1].Set("FormEditorID", PresetValue.Scalar("FoodIngredient"));
        Require(Errors(crossClass).Any(error => error.Contains("same form type")),
            "Food ingredients and ingestibles share FOOD but cannot replace one another as explicit stages.");
        var crossCategory = rule.Clone(); crossCategory.Get("stages")!.Items[1].Set("FormEditorID", PresetValue.Scalar("Medicine"));
        Require(Errors(crossCategory).Any(error => error.Contains("does not belong to FOOD")),
            "Matching native class does not allow a medicine stage inside a FOOD rule.");
        var sourceReused = rule.Clone(); sourceReused.Get("stages")!.Items[1].Set("FormEditorID", PresetValue.Scalar(food.Identifier));
        Require(Errors(sourceReused).Any(error => error.Contains("reuse the source")),
            "A later stage cannot reuse its source via a different spelling of the same form.");
        var duplicated = rule.Clone(); var last = duplicated.Get("stages")!.Items[1].Clone();
        last.Set("no", PresetValue.Scalar("2")); last.Set("FormEditorID", PresetValue.Scalar(replacement.Identifier));
        duplicated.Get("stages")!.Items.Add(last);
        Require(Errors(duplicated).Any(error => error.Contains("different identifier")),
            "EditorID and plugin-local ID references to one replacement must count as a duplicate stage item.");
        var ignoredInitial = rule.Clone(); ignoredInitial.Get("stages")!.Items[0].Set("FormEditorID", PresetValue.Scalar("Location"));
        Require(Errors(ignoredInitial).Count == 0, "A preserved stage-zero replacement is ignored by AoT and must not be treated as active.");
        var unresolved = rule.Clone(); unresolved.Set("owners", PresetValue.Scalar("Raw meat selector"));
        unresolved.Set("finalFormEditorID", PresetValue.Scalar("OptionalModResult"));
        unresolved.Get("stages")!.Items[1].Set("FormEditorID", PresetValue.Scalar("OptionalModStage"));
        Require(Errors(unresolved).Count == 0, "Unknown name selectors and optional forms must remain authorable.");

        var expanded = PresetDocument.Parse("""
            templates:
              rule:
                owners: $source
                stages: [{no: 0, name: '', duration: 1}]
                finalFormEditorID: $result
            ownerLists:
            - rule(Food, Location)
            """, "FOOD", PresetKind.Custom);
        var validation = PresetValidation.Check(expanded, CancellationToken.None, catalog).GetAwaiter().GetResult();
        Require(validation.FileErrors.Count == 0 && validation.Errors.Values.SelectMany(errors => errors).Any(error => error.Contains("bound object")),
            "Known-form validation must inspect native-expanded template arguments, not only authored mappings.");
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
