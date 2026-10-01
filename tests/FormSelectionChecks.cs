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
        foreach (var id in new[] { "Food", "FoodIngredient", "Armor", "Key", "Location", "Perk", "Fire" })
            Require(Row(id).IsTrigger, $"{id} must be accepted by the trigger picker.");
        foreach (var id in new[] { "Keyword", "FormList", "Sound", "MagicEffect" })
            Require(!Row(id).IsTrigger, $"{id} must not be offered as a direct trigger.");

        var foodSelection = new FormSelection(["Ingestible", "Ingredient"], "FOOD");
        Require(forms.Count(foodSelection.Matches) == 2, "FOOD source selection must hide medicine, poison, and ordinary ingredients.");
        Require(new FormSelection(Category: "INGR").Matches(Row("FoodIngredient")), "An overlapping category must not be lost during filtering.");
        Require(new FormSelection().Matches(Row("Sound")), "No constraint must preserve unrestricted browsing.");
        Require(!forms.Any(new FormSelection([]).Matches), "An empty type intersection must have no valid choices.");
        Require(!forms.Any(new FormSelection(Category: "Unknown").Matches), "An unknown category must not broaden a constrained picker.");

        var picker = new FormPickerWindow(forms, multiple: true, selection: foodSelection);
        var grid = Field<DataGrid>(picker, "results");
        Require(grid.Items.Count == 2, "The picker must apply its semantic constraint before showing results.");
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
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
