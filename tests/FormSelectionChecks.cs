using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
        var unnamedFood = mod.Ingestibles.AddNew();
        unnamedFood.Name = "Food without EditorID"; unnamedFood.Flags = Ingestible.Flag.FoodItem;
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
        var npc = mod.Npcs.AddNew(); npc.EditorID = "Player";
        var container = mod.Containers.AddNew(); container.EditorID = "Chest";
        var ammunition = mod.Ammunitions.AddNew(); ammunition.EditorID = "Ammunition";
        var light = mod.Lights.AddNew(); light.EditorID = "Light";
        var spell = mod.Spells.AddNew(); spell.EditorID = "Spell";
        var enchantment = mod.ObjectEffects.AddNew(); enchantment.EditorID = "Enchantment";
        var leveledSpell = mod.LeveledSpells.AddNew(); leveledSpell.EditorID = "LeveledSpell";
        var dualCast = mod.DualCastData.AddNew(); dualCast.EditorID = "DualCast";
        var acoustic = mod.AcousticSpaces.AddNew(); acoustic.EditorID = "AcousticSpace";
        var texture = mod.TextureSets.AddNew(); texture.EditorID = "TextureSet";
        var grass = mod.Grasses.AddNew(); grass.EditorID = "Grass";
        var leveledNpc = mod.LeveledNpcs.AddNew(); leveledNpc.EditorID = "LeveledNpc";
        var explosion = mod.Explosions.AddNew(); explosion.EditorID = "Explosion";
        var hazard = mod.Hazards.AddNew(); hazard.EditorID = "Hazard";
        var armorAddon = mod.ArmorAddons.AddNew(); armorAddon.EditorID = "ArmorAddon";
        var location = mod.Locations.AddNew(); location.EditorID = "Location";
        var perk = mod.Perks.AddNew(); perk.EditorID = "Perk";
        var fire = mod.Statics.AddNew(); fire.EditorID = "Fire";
        var keyword = mod.Keywords.AddNew(); keyword.EditorID = "Keyword";
        var list = mod.FormLists.AddNew(); list.EditorID = "FormList";
        var sound = mod.SoundDescriptors.AddNew(); sound.EditorID = "Sound";
        var effect = mod.MagicEffects.AddNew(); effect.EditorID = "MagicEffect";
        var art = mod.ArtObjects.AddNew(); art.EditorID = "Art";
        var shader = mod.EffectShaders.AddNew(); shader.EditorID = "Shader";
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
        foreach (var id in new[] { "Food", "FoodIngredient", "Armor", "Key", "Ammunition", "Light", "Spell", "Location", "Perk", "Fire", "Grass", "LeveledNpc", "Explosion", "Hazard", "Art", "Shader", "FormList", "Keyword" })
            Require(Row(id).IsTrigger, $"{id} must pass AoT's native trigger type check.");
        foreach (var id in new[] { "Sound", "MagicEffect", "ArmorAddon" })
            Require(!Row(id).IsTrigger, $"{id} must fail AoT's native trigger type check.");

        var resultSelection = new FormSelection(BoundObjectOnly: true);
        foreach (var id in new[] { "Food", "Medicine", "PoisonedFood", "Ingredient", "Armor", "Key", "Ammunition", "Light", "Spell", "Fire" })
            Require(resultSelection.Matches(Row(id)), $"{id} must pass native bound-object validation, independently of the source category.");
        foreach (var id in new[] { "Keyword", "FormList", "Shader", "Sound", "MagicEffect", "ArmorAddon", "Location", "Perk" })
            Require(!resultSelection.Matches(Row(id)), $"{id} must fail native bound-object validation.");
        Require(new FormSelection(BoundObjectOnly: true, TriggerOnly: true).Matches(Row("Key")) &&
            !new FormSelection(BoundObjectOnly: true, TriggerOnly: true).Matches(Row("Location")),
            "Combined parameter constraints must retain the bound-object requirement.");

        var sourceField = PresetSchema.GetFields("rule", PresetKind.Custom).Single(field => field.Key == "owners");
        var foodSelection = FormSelection.ForField(sourceField, "FOOD");
        Require(forms.Count(foodSelection.Matches) == 3, "FOOD source selection must hide incompatible types and sources without EditorIDs.");
        var unnamed = forms.Single(form => form.EditorId.Length == 0);
        Require(resultSelection.Matches(unnamed) && new FormSelection(TriggerOnly: true).Matches(unnamed) &&
            FormSelection.ForField(PresetSchema.GetFields("stage", PresetKind.Custom).Single(field => field.Key == "FormEditorID"), "FOOD", [Row("Food")]).Matches(unnamed),
            "EditorIDs are required only for source items, not stage replacements, final results, or triggers.");
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
        PickerControls(forms);
        ContainerSelections(forms);
        AddonSettings(forms);
        SourceGroups(forms);
        KnownForms(forms);
        ExpandedTriggerForms(forms);
    }

    private static void PickerControls(IReadOnlyList<FormRow> forms)
    {
        string[] magicDefinitions = ["Spell", "ObjectEffect", "LeveledSpell", "DualCastData"];
        foreach (var form in forms.Where(form => magicDefinitions.Contains(form.Type)))
            Require(new FormSelection(TriggerOnly: true).Matches(form) && new FormSelection(BoundObjectOnly: true).Matches(form),
                "Hiding magic definitions from suggestions must preserve AoT-compatible manual/imported identifiers.");

        foreach (var form in forms.Where(form => form.Type is "Grass" or "LeveledNpc"))
            Require(new FormSelection(TriggerOnly: true).Matches(form) &&
                new FormSelection(BoundObjectOnly: true).IsPickerChoice(form) && new FormSelection().IsPickerChoice(form),
                "Omitting grass and leveled NPCs from Trigger suggestions must preserve manual identifiers, result choices, and general browsing.");

        foreach (var kind in new[] { PresetKind.Default, PresetKind.Custom, PresetKind.Addon })
        foreach (var context in new[] { "rule", "stage", "transformer", "modulator", "effect" })
        foreach (var descriptor in PresetSchema.GetFields(context, kind).Where(field => field.Kind is FieldKind.Form or FieldKind.FormList))
        {
            var document = new PresetDocument { Category = "FOOD", Kind = kind };
            var source = forms.Single(form => form.EditorId == "Food");
            var expected = forms.Where(form => descriptor.Key switch
            {
                "owners" or "forms" => (form.Categories & ItemCategories.FOOD) != 0 && form.EditorId.Length > 0,
                "containers" => form.Type is "Npc" or "Container",
                "sound" => form.Type == "SoundDescriptor",
                "art_object" => form.Type == "ArtObject",
                "effect_shader" => form.Type == "EffectShader",
                "finalFormEditorID" => form.IsBoundObject && form.Type != "AcousticSpace" && !magicDefinitions.Contains(form.Type),
                "FormEditorID" when context == "stage" => form.Type == "Ingestible" && (form.Categories & ItemCategories.FOOD) != 0 && form != source,
                "FormEditorID" when context == "effect" => form.Type == "MagicEffect",
                "FormEditorID" => form.IsTrigger && !magicDefinitions.Contains(form.Type) && form.Type is not ("Grass" or "LeveledNpc"),
                _ => throw new InvalidOperationException("Unreviewed form field: " + context + "." + descriptor.Key)
            }).ToHashSet();
            var scalar = new ValueEditorControl(document, forms, PresetValue.Scalar(""), "Value", descriptor.Label,
                field: descriptor, sourceForms: [source]);
            CheckPicker(scalar, "Choose form...", expected, kind + "/" + context + "/" + descriptor.Key);
            if (descriptor.Kind != FieldKind.FormList) continue;
            var sequence = PresetValue.List(); sequence.Items.Add(PresetValue.Scalar(""));
            var list = new ValueEditorControl(document, forms, sequence, "Value", descriptor.Label,
                field: descriptor, sourceForms: [source]);
            CheckPicker(list, "Choose forms...", expected, kind + "/" + context + "/" + descriptor.Key + " list");
            var entry = Descendants<ValueEditorControl>(list).Single();
            CheckPicker(entry, "Choose form...", expected, kind + "/" + context + "/" + descriptor.Key + " entry");
            if (descriptor.TriggerOnly && kind == PresetKind.Addon)
                CheckTriggerVariable(forms, context, expected);
        }
    }

    private static void CheckTriggerVariable(IReadOnlyList<FormRow> forms, string context, HashSet<FormRow> expected)
    {
        var section = context == "transformer" ? "transformers" : "timeModulators";
        var document = PresetDocument.Parse($"""
            templates:
              behavior:
                forms: Food
                {section}:
                - FormEditorID: $trigger
            formsLists:
            - behavior(Fire)
            """, "FOOD", PresetKind.Addon);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var behavior = new BehaviorEditorControl(document, forms, rule.Id);
        Field<TabControl>(behavior, "tabs").SelectedIndex = 1;
        var table = Field<DataGrid>(behavior, "table");
        table.SelectedItem = table.Items[0];
        table.CurrentCell = new DataGridCellInfo(table.Items[0], table.Columns.Single(column => Equals(column.Header, "trigger")));
        CheckPicker(Field<ValueEditorControl>(behavior, "cellEditor"), "Choose form...", expected, context + " Trigger variable");
    }

    private static void CheckPicker(ValueEditorControl editor, string action, HashSet<FormRow> expected, string route)
    {
        // Invoke the real button and inspect its modal's displayed rows, including inherited list-entry constraints.
        Exception? failure = null;
        var response = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var picker = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                .OfType<FormPickerWindow>().Single();
            try
            {
                picker.Opacity = 0; picker.ShowInTaskbar = false;
                var displayed = Field<DataGrid>(picker, "results").Items.Cast<FormRow>().ToHashSet();
                Require(displayed.SetEquals(expected), route + " offered incorrect forms: " +
                    string.Join(", ", displayed.Except(expected).Select(form => form.Type + ":" + form.EditorId)));
                Require(Field<ComboBox>(picker, "types").Items.Cast<string>().Skip(1).ToHashSet()
                    .SetEquals(expected.Select(form => form.Type)), route + " type filter must contain only applicable types.");
            }
            catch (Exception error) { failure = error; }
            finally { picker.Close(); }
        }));
        try { Descendants<Button>(editor).Single(button => Equals(button.Content, action)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        finally { response.Abort(); }
        if (failure is not null) throw failure;
        Require(response.Status == DispatcherOperationStatus.Completed, route + " must open its form picker.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void ContainerSelections(IReadOnlyList<FormRow> forms)
    {
        FormRow Row(string editorId) => forms.Single(form => form.EditorId == editorId);
        foreach (var kind in new[] { PresetKind.Default, PresetKind.Custom, PresetKind.Addon })
        foreach (var context in new[] { "rule", "transformer", "modulator" })
        foreach (var category in Enum.GetValues<ItemCategories>().Where(category => category != ItemCategories.None))
        {
            var field = PresetSchema.GetFields(context, kind).Single(field => field.Key == "containers");
            var selection = FormSelection.ForField(field, category.ToString());
            foreach (var id in new[] { "Player", "Chest" })
                Require(selection.IsPickerChoice(Row(id)), $"{kind}/{context}/{category} Containers must offer inventory-owner base forms.");
            foreach (var id in new[] { "Food", "FoodIngredient", "Ingredient", "Armor", "Keyword", "MagicEffect", "Sound", "Fire" })
            {
                Require(!selection.IsPickerChoice(Row(id)), $"{kind}/{context}/{category} Containers must not suggest {id}.");
                Require(selection.Matches(Row(id)), "World-item and nonmatching manual/imported Containers identifiers must remain valid.");
            }
            var unnamed = forms.Single(form => form.EditorId.Length == 0);
            Require(!selection.IsPickerChoice(unnamed) && selection.Matches(unnamed),
                "World-item identifiers without EditorIDs must remain valid through manual entry without appearing in Containers choices.");
            var picker = new FormPickerWindow(forms, selection: selection);
            var displayed = Field<DataGrid>(picker, "results").Items.Cast<FormRow>().ToArray();
            Require(displayed.Length == 2 && displayed.Contains(Row("Player")) && displayed.Contains(Row("Chest")),
                "Every Containers picker must offer only NPC and Container base forms, regardless of preset category or filter position.");
            picker.Close();
        }
        Require(new FormSelection().IsPickerChoice(Row("MagicEffect")), "The standalone form browser must remain unrestricted.");
    }

    private static void AddonSettings(IReadOnlyList<FormRow> forms)
    {
        FormGroupDefinition[] groups = [new("Empty", []), new("Missing", ["OptionalFire"]), new("Zero", ["0", "0x0"]), new("Fires", ["Fire", "OptionalFire"])];
        PresetValidationResult Validate(string yaml)
        {
            var document = PresetDocument.Parse(yaml, "FOOD", PresetKind.Addon);
            return PresetValidation.Check(document, CancellationToken.None, new PresetPresentation(document, forms, groups)).GetAwaiter().GetResult();
        }
        foreach (var settings in new[]
        {
            "", ", containers: 0", ", containers: 0x0", ", containers: Zero", ", containers: null, transformers: null, timeModulators: null",
            ", containers: [], transformers: [], timeModulators: []", ", containers: Empty",
            ", containers: [Empty, Missing]", ", timeModulators: [{FormEditorID: 0, magnitude: 0}]", ", timeModulators: [{FormEditorID: Empty, magnitude: 0}]",
            ", transformers: [{FormEditorID: Missing, finalFormEditorID: Food, duration: 1}]"
        })
        {
            var validation = Validate("formsLists: [{forms: Food" + settings + "}]");
            Require(validation.Errors.Values.SelectMany(errors => errors).Any(error => error.Contains("No effective settings")),
                "An addon without effective settings must explain what is missing: " + settings);
        }
        foreach (var fields in new[] { "containers: ''", "containers: ' '", "timeModulators: [{FormEditorID: null, magnitude: 0}]" })
            Require(!Validate("formsLists: [{forms: Food, " + fields + "}]").IsValid,
                "Blank or null form selections cannot make an addon effective.");
        foreach (var settings in new[]
        {
            "containers: Player", "containers: Food", "containers: [Empty, Player]", "containers: OptionalContainer",
            "timeModulators: [{FormEditorID: Fires, magnitude: 0}]", "timeModulators: [{FormEditorID: Fire, magnitude: 1}]",
            "timeModulators: [{FormEditorID: Fire, magnitude: -1}]",
            "transformers: [{FormEditorID: Fires, finalFormEditorID: Food, duration: 1}], containers: Empty"
        })
            Require(Validate("formsLists: [{forms: Food, " + settings + "}]").IsValid,
                "Containers-only addons, zero/reverse modifiers, and optional unresolved identifiers must remain valid: " + settings);
        foreach (var yaml in new[]
        {
            "base: &base {forms: Food}\nformsLists: [{<<: *base}]",
            "templates:\n  identity: $value\n  patch:\n    forms: $items\n    transformers: identity([])\nformsLists:\n- patch(Food)",
            "templates: {rules: [{forms: $items, timeModulators: []}]}\nformsLists: rules(Food)"
        })
            Require(Validate(yaml).Errors.Values.SelectMany(errors => errors).Any(error => error.Contains("No effective settings")),
                "No-effective-settings validation must inspect expanded shared, nested template, and whole-list rules.");
        var shared = Validate("""
            partial: &partial {forms: Food}
            settings: &settings {containers: Player}
            templates:
              target: {forms: $item}
              patch: {forms: $item, <<: *settings}
            formsLists:
            - patch(Food)
            """);
        Require(shared.IsValid, "Partial definitions must stay valid and merged settings must count after native expansion.");
        foreach (var kind in new[] { PresetKind.Default, PresetKind.Custom })
        {
            var yaml = "stages: [{no: 0, name: '', duration: .inf}]\nfinalFormEditorID: OtherFood";
            if (kind == PresetKind.Custom) yaml = "ownerLists:\n- owners: Food\n  " + yaml.Replace("\n", "\n  ");
            var document = PresetDocument.Parse(yaml, "FOOD", kind);
            Require(PresetValidation.Check(document, CancellationToken.None, new PresetPresentation(document, forms)).GetAwaiter().GetResult().IsValid,
                "The effective-addon-settings check must not change default or custom stage validation.");
        }
    }

    private static void SourceGroups(IReadOnlyList<FormRow> forms)
    {
        forms = [.. forms, forms.Single(form => form.EditorId == "Food") with
        {
            EditorId = "DlcFood", Plugin = "Dragonborn.esm", Winner = "Dragonborn.esm",
            Identifier = "0x800~Dragonborn.esm", RuntimeId = new FormID(0x02000800)
        }];
        FormGroupDefinition[] groups =
        [
            new("qAoTFireMain", ["Fire", "OptionalFire"]), new("Missing", ["OptionalFood"]), new("Empty", []),
            new("Meat", ["Food"]), new("Dlc", ["02000800"]), new("Mixed", ["Fire", "Food", "Armor", "OptionalFood"]),
            new("Nameless", [forms.Single(form => form.EditorId.Length == 0).Identifier]), new("Food", ["Fire"])
        ];
        var bare = PresetDocument.Parse("formsLists:\n- forms: qAoTFireMain", "FOOD", PresetKind.Addon);
        var bareValidation = PresetValidation.Check(bare, CancellationToken.None, new PresetPresentation(bare, forms, groups)).GetAwaiter().GetResult();
        var bareErrors = bareValidation.Errors.Values.SelectMany(errors => errors).ToArray();
        Require(bareErrors.Any(error => error.Contains("No effective settings")) && bareErrors.Any(error => error.Contains("qAoTFireMain")),
            "The reported forms-only fire-group example must expose both missing settings and incompatible affected items.");
        foreach (var kind in new[] { PresetKind.Custom, PresetKind.Addon })
        {
            var key = kind == PresetKind.Custom ? "owners" : "forms";
            var list = kind == PresetKind.Custom ? "ownerLists" : "formsLists";
            var settings = kind == PresetKind.Custom
                ? "stages: [{no: 0, name: '', duration: 1}], finalFormEditorID: OtherFood"
                : "timeModulators: [{FormEditorID: qAoTFireMain, magnitude: 0}]";
            var field = PresetSchema.GetFields("rule", kind).Single(field => field.Key == key);
            var picker = new GroupPickerWindow(groups, forms, FormSelection.ForField(field, "FOOD"));
            foreach (var group in groups)
            {
                var valid = group.Name is "Meat" or "Dlc" or "Mixed";
                Field<ListBox>(picker, "groupList").SelectedItem = group;
                Require(Field<Button>(picker, "choose").IsEnabled == valid,
                    $"{kind} must require a known applicable member before selecting {group.Name}; unknown optional members cannot make a fire group usable.");
                Require(Field<DataGrid>(picker, "members").Items.Count == group.Members.Count,
                    "Showing group compatibility must retain unresolved and incompatible members.");
                var document = PresetDocument.Parse($"{list}: [{{{key}: {group.Name}, {settings}}}]", "FOOD", kind);
                var validation = PresetValidation.Check(document, CancellationToken.None,
                    new PresetPresentation(document, forms, groups)).GetAwaiter().GetResult();
                Require(validation.IsValid == valid, $"Imported {kind} source group {group.Name} must obey the same source constraints.");
                if (!valid)
                    Require(validation.Errors.Values.SelectMany(errors => errors).Any(error => error.StartsWith("Applies to:") &&
                        error.Contains("FOOD") && error.Contains("loaded game data")),
                        "A group with no resolved eligible sources must explain the category and loaded-data limitation.");
            }
            picker.Close();
            foreach (var source in new[] { "qAoTFireMain", "[Meat, qAoTFireMain]" })
            {
                var document = PresetDocument.Parse($"templates:\n  identity: $value\n  patch: {{{key}: identity($items), {settings}}}\n{list}:\n- patch({source})", "FOOD", kind);
                var validation = PresetValidation.Check(document, CancellationToken.None,
                    new PresetPresentation(document, forms, groups)).GetAwaiter().GetResult();
                var template = document.Blocks.Single(block => block.Kind == BlockKind.Template && block.Key == "patch");
                var parameter = PresetSchema.GetParameterField(template, "items", document);
                Require(parameter is { MatchPresetCategory: true, RequiresEditorId: true }, "Nested source variables must retain their field constraints.");
                var variablePicker = new GroupPickerWindow(groups, forms, FormSelection.ForField(parameter, "FOOD"));
                Field<ListBox>(variablePicker, "groupList").SelectedItem = groups[0];
                Require(!Field<Button>(variablePicker, "choose").IsEnabled, "Variables must not bypass source-group eligibility.");
                variablePicker.Close();
                Require(!validation.IsValid && validation.Errors.Values.SelectMany(errors => errors).Any(error => error.Contains("qAoTFireMain")),
                    "A source group in a nested variable call or selector list must be validated after expansion.");
            }
            var editor = new GroupEditorWindow([], forms, FormSelection.ForField(field, "FOOD"), ["Fire", "OptionalFire"]);
            Field<TextBox>(editor, "name").Text = "NewFires";
            Require(!Field<Button>(editor, "create").IsEnabled,
                "Creating a source group must use the same known-member eligibility as selecting an existing one.");
            editor.Close();
        }
        var containers = PresetSchema.GetFields("rule", PresetKind.Addon).Single(field => field.Key == "containers");
        var containerPicker = new GroupPickerWindow(groups, forms, FormSelection.ForField(containers, "FOOD"));
        Field<ListBox>(containerPicker, "groupList").SelectedItem = groups.Single(group => group.Name == "Empty");
        Require(Field<Button>(containerPicker, "choose").IsEnabled, "Empty Containers groups retain their native empty-filter meaning.");
        containerPicker.Close();
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
            .GetMethod("KnownFormErrors", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [value, "FOOD", PresetKind.Custom, catalog])!;
        Require(Errors(rule).Count == 0, "A FOOD rule may produce keys and ammunition while explicit stages stay compatible with its source.");
        foreach (var result in new[] { "Location", "Perk", "FormList", "Shader", "Sound", "MagicEffect" })
        {
            var invalid = rule.Clone(); invalid.Set("finalFormEditorID", PresetValue.Scalar(result));
            Require(Errors(invalid).Any(error => error.StartsWith("Final item:", StringComparison.Ordinal)),
                $"Known non-bound result {result} must not pass validation.");
        }
        var badTransformation = rule.Clone();
        badTransformation.Get("transformers")!.Items[0].Set("finalFormEditorID", PresetValue.Scalar("Location"));
        Require(Errors(badTransformation).Single().StartsWith("Transformations 1: Result item:", StringComparison.Ordinal),
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

        var invalidSource = rule.Clone(); invalidSource.Set("owners", PresetValue.Scalar(forms.Single(form => form.EditorId == "Armor").Identifier));
        Require(Errors(invalidSource).Any(error => error.StartsWith("Applies to:", StringComparison.Ordinal)),
            "A numeric source identifier with an incompatible category must not pass validation.");
        invalidSource.Set("owners", PresetValue.Scalar("Armor"));
        Require(Errors(invalidSource).Count == 0, "Custom owner text may be a name selector even when it also resolves to an unrelated EditorID.");
        invalidSource.Set("owners", PresetValue.Scalar(forms.Single(form => form.EditorId.Length == 0).Identifier));
        Require(Errors(invalidSource).Any(error => error.Contains("no EditorID")), "AoT rejects source items without EditorIDs even when selected by numeric ID.");

        foreach (var (key, invalid, expected) in new[]
        {
            ("sound", "Art", "SoundDescriptor"), ("art_object", "Sound", "ArtObject"),
            ("effect_shader", "MagicEffect", "EffectShader")
        })
        {
            var typed = rule.Clone(); typed.Get("stages")!.Items[0].Set(key, PresetValue.Scalar(invalid));
            Require(Errors(typed).Any(error => error.StartsWith("Stages 1:", StringComparison.Ordinal) && error.Contains(expected)),
                $"A known incompatible {key} form must be reported at its stage field.");
        }
        var trigger = rule.Clone(); trigger.Get("transformers")!.Items[0].Set("FormEditorID", PresetValue.Scalar("Sound"));
        Require(Errors(trigger).Single().StartsWith("Transformations 1: Trigger:", StringComparison.Ordinal),
            "Known incompatible trigger identifiers must not silently pass validation.");
        var visuals = rule.Clone();
        visuals.Get("stages")!.Items[0].Set("sound", PresetValue.Scalar("Sound"));
        visuals.Get("stages")!.Items[0].Set("art_object", PresetValue.Scalar("Art"));
        visuals.Get("stages")!.Items[0].Set("effect_shader", PresetValue.Scalar("Shader"));
        visuals.Set("containers", PresetValue.Scalar("Sound"));
        visuals.Get("transformers")!.Items[0].Set("containers", PresetValue.Scalar("Sound"));
        Require(Errors(visuals).Count == 0, "Valid effect types and intentionally nonmatching inventory-filter sentinels must remain allowed.");

        foreach (var filterForm in new[] { food, forms.Single(form => form.EditorId == "FoodIngredient"), forms.Single(form => form.EditorId == "Armor"),
            forms.Single(form => form.EditorId.Length == 0), forms.Single(form => form.EditorId == "Sound") })
        {
            var manual = rule.Clone();
            manual.Set("containers", PresetValue.Scalar(filterForm.Identifier));
            manual.Get("transformers")!.Items[0].Set("containers", PresetValue.Scalar(filterForm.Identifier));
            var modulator = PresetYaml.Parse("{FormEditorID: Fire, magnitude: 0}");
            modulator.Set("containers", PresetValue.Scalar(filterForm.Identifier));
            var modulators = PresetValue.List(); modulators.Items.Add(modulator); manual.Set("timeModulators", modulators);
            Require(Errors(manual).Count == 0,
                "Hiding world-item and nonmatching Containers suggestions must not invalidate manually entered identifiers at any filter position.");
        }

        var effects = rule.Clone(); var generatedStage = effects.Get("stages")!.Items[1];
        generatedStage.Fields.Remove("FormEditorID");
        generatedStage.Set("mgeffect", PresetYaml.Parse("[{FormEditorID: Sound, magnitude: 1, duration: 2}]"));
        Require(Errors(effects).Single().StartsWith("Stages 2: Magic effects 1: Magic effect:", StringComparison.Ordinal),
            "Generated stage magic effects must use actual magic-effect forms.");
        generatedStage.Set("FormEditorID", PresetValue.Scalar("OtherFood"));
        Require(Errors(effects).Count == 0, "Effect overrides ignored for an explicit stage item must not create blocking issues.");

        var grouped = PresetDocument.Parse("formsLists: [{forms: Mixed, transformers: [{FormEditorID: Sound, finalFormEditorID: Key, duration: 1}]}]", "FOOD", PresetKind.Addon);
        var groupCatalog = new PresetPresentation(grouped, forms,
            [new("Mixed", ["Food", "Armor", "OptionalItem"]), new("Sound", ["Fire", "OptionalTrigger"])]);
        var groupedErrors = (IReadOnlyList<string>)typeof(PresetValidation).GetMethod("KnownFormErrors", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [grouped.Blocks.Single().Value, "FOOD", PresetKind.Addon, groupCatalog])!;
        Require(groupedErrors.Count == 0, "Mixed groups, unresolved members, and group names shadowing EditorIDs must keep their native meaning.");
        grouped.Blocks.Single().Value.Set("forms", PresetValue.Scalar("Armor"));
        groupedErrors = (IReadOnlyList<string>)typeof(PresetValidation).GetMethod("KnownFormErrors", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [grouped.Blocks.Single().Value, "FOOD", PresetKind.Addon, groupCatalog])!;
        Require(groupedErrors.Single().StartsWith("Applies to:", StringComparison.Ordinal), "Addon sources have no name-selector fallback for incompatible EditorIDs.");

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

    private static void ExpandedTriggerForms(IReadOnlyList<FormRow> forms)
    {
        foreach (var kind in new[] { PresetKind.Default, PresetKind.Custom, PresetKind.Addon })
        foreach (var section in new[] { "transformers", "timeModulators" })
        foreach (var selector in new[] { "Art", "Shader", "FormList", "Keyword", "[Art, Shader, FormList, Keyword]", "AppliedEffects" })
        {
            var settings = section == "transformers" ? "finalFormEditorID: OtherFood, duration: 1" : "magnitude: 0";
            var rule = PresetYaml.Parse($"{{{section}: [{{FormEditorID: $trigger, {settings}}}]}}");
            if (kind != PresetKind.Addon)
            {
                rule.Set("stages", PresetYaml.Parse("[{no: 0, name: '', duration: 1}]"));
                rule.Set("finalFormEditorID", PresetValue.Scalar("OtherFood"));
            }
            if (kind != PresetKind.Default) rule.Set(kind == PresetKind.Custom ? "owners" : "forms", PresetValue.Scalar("Food"));
            var call = $"behavior({selector})";
            var output = kind == PresetKind.Default ? "<<: " + call : (kind == PresetKind.Custom ? "ownerLists" : "formsLists") + ":\n- " + call;
            var document = PresetDocument.Parse("templates:\n  behavior: " + PresetYaml.Write(rule, flow: true).Trim() + "\n" + output, "FOOD", kind);
            var catalog = new PresetPresentation(document, forms, [new("AppliedEffects", ["Art", "Shader", "FormList", "Keyword"])]);
            Require(PresetValidation.Check(document, CancellationToken.None, catalog).GetAwaiter().GetResult().IsValid,
                $"{kind}/{section} must accept applied effects and FLST identifiers after template expansion: {selector}");
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
