using System.IO;
using AoTPresetEditor;

internal static class StageAvailabilityChecks
{
    private static readonly FormRow Beef = new("Raw Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C99~Skyrim.esm", ItemCategories.FOOD, true, true);
    private static readonly FormRow Goat = new("Leg of Goat", "FoodGoat", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C9A~Skyrim.esm", ItemCategories.FOOD, true, true);
    private static readonly FormRow Cooked = new("Cooked Beef", "FoodBeefCooked", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x721E8~Skyrim.esm", ItemCategories.FOOD, true, true);
    private static readonly FormRow[] Forms = [Beef, Goat, Cooked];
    private const string StageSettings = "stages: [{no: 0, name: fresh, duration: .inf}]\nfinalFormEditorID: FoodBeefCooked\n";

    public static void Run()
    {
        OwnerSelectors();
        OwnerGroups();
        var empty = StageAvailability.LoadAsync([]).GetAwaiter().GetResult();
        var project = Addon("FoodBeef");
        var validation = Validate(project);
        Require(Messages(empty, project, validation).Single().StartsWith("No stages were found for Raw Beef", StringComparison.Ordinal),
            "A complete empty catalog must report missing stages by item name.");
        Require(validation.Values.All(result => result.IsValid), "A missing-stage warning must not change validation or block exporting.");
        Require(Messages(new StageAvailability(), project, validation).Single().StartsWith("Could not verify", StringComparison.Ordinal),
            "An unavailable catalog must not claim that stages do not exist.");

        var custom = PresetDocument.Parse("ownerLists:\n- owners: FoodBeef\n  " + StageSettings.Replace("\n", "\n  ").TrimEnd(), "FOOD", PresetKind.Custom);
        project.Add(custom, "Beef stages");
        Require(Messages(empty, project, Validate(project)).Count == 0, "Stages from the current unsaved project must satisfy the dependency.");
        custom.Blocks.Single().Value.Set("owners", PresetValue.Scalar("bEeF"));
        Require(Messages(empty, project, Validate(project)).Count == 0, "Custom stage owners must support runtime case-insensitive word matching.");
        custom.Blocks.Single().Value.Set("owners", PresetValue.Scalar("eef"));
        Require(Messages(empty, project, Validate(project)).Single().StartsWith("No stages", StringComparison.Ordinal),
            "A partial name substring must not be treated as the runtime's word match.");
        custom.Blocks.Single().Value.Set("owners", PresetValue.Scalar("FoodBeef"));
        custom.Blocks.Single().Value.Get("stages")!.Items[0].Set("duration", PresetValue.Scalar("0"));
        Require(Messages(empty, project, Validate(project)).Single().StartsWith("Could not verify", StringComparison.Ordinal),
            "An invalid matching stage definition must not suppress the warning.");

        project = Addon("FoodBeef");
        project.Add(PresetDocument.Parse(StageSettings, "FOOD", PresetKind.Default), "Default stages");
        Require(Messages(empty, project, Validate(project)).Count == 0, "An infinite stage-0 default must cover an addon source.");
        project.Entries.Last().Document.Category = "MISC";
        Require(Messages(empty, project, Validate(project)).Single().StartsWith("No stages", StringComparison.Ordinal),
            "Defaults for another category must not satisfy FOOD dependencies.");

        project = Addon("Meat");
        project.Entries[0].Document.PendingGroups.Add(new("Meat", ["FoodBeef", "FoodGoat"]));
        var groupWarning = Messages(empty, project, Validate(project)).Single();
        Require(groupWarning.Contains("Raw Beef") && groupWarning.Contains("Leg of Goat"), "Pending form groups must expand to their members for stage checks.");
        project.Add(PresetDocument.Parse("ownerLists:\n- owners: FoodBeef\n  " + StageSettings.Replace("\n", "\n  ").TrimEnd(), "FOOD", PresetKind.Custom), "Beef only");
        groupWarning = Messages(empty, project, Validate(project)).Single();
        Require(!groupWarning.Contains("Raw Beef") && groupWarning.Contains("Leg of Goat"),
            "One covered group member must not hide the missing stages of another member.");
        project = Addon("MissingFood");
        Require(Messages(empty, project, Validate(project)).Single().StartsWith("Could not check stages for MissingFood", StringComparison.Ordinal),
            "Unresolved form identifiers must not become definite missing-stage claims.");
        project = Addon("FoodBeef", modifiersOnly: true);
        Require(Messages(empty, project, Validate(project)).Single().Contains("Time modifiers need stages"),
            "Time-modifier-only behaviors need their own dependency warning.");
        project.Entries[0].Document.Blocks.Single().Value.Remove("timeModulators");
        Require(Messages(empty, project, Validate(project)).Count == 0, "A rule with no transformations or modifiers needs no stage warning.");

        project = Addon("FoodBeef");
        project.Add(PresetDocument.Create("FOOD", PresetKind.Custom), "Unused stages");
        Require(Messages(empty, project, Validate(project)).Single().StartsWith("No stages", StringComparison.Ordinal),
            "An inactive generated stage file must not make known missing stages uncertain.");

        LoadedPresets();
    }

    private static void OwnerGroups()
    {
        var empty = StageAvailability.LoadAsync([]).GetAwaiter().GetResult();
        var project = Addon("FoodBeef");
        var stages = PresetDocument.Parse("ownerLists:\n- owners: Meat\n  " + StageSettings.Replace("\n", "\n  ").TrimEnd(),
            "FOOD", PresetKind.Custom);
        stages.PendingGroups.Add(new("Meat", ["FoodBeef", "UnknownOptionalFood"]));
        project.Add(stages, "Meat stages");
        Require(Messages(empty, project, Validate(project)).Count == 0,
            "A custom owner group must provide stages to its members, including when its definition is pending export.");
        stages.PendingGroups[0] = new("Meat", []);
        Require(Messages(empty, project, Validate(project)).Single().StartsWith("No stages", StringComparison.Ordinal),
            "An empty custom owner group must not supply stages.");
        stages.PendingGroups[0] = new("Raw Beef", []);
        stages.Blocks.Single().Value.Set("owners", PresetValue.Scalar("Raw Beef"));
        Require(Messages(empty, project, Validate(project)).Single().StartsWith("No stages", StringComparison.Ordinal),
            "A group named like an item must not satisfy stage coverage through name matching when it has no members.");
        stages.PendingGroups[0] = new("Meat", ["FoodBeef"]);
        stages.Blocks.Single().Value.Set("owners", PresetValue.Scalar("meat"));
        Require(Messages(empty, project, Validate(project)).Single().StartsWith("No stages", StringComparison.Ordinal),
            "A differently cased custom group name must not supply the group's stage coverage.");
        stages.Blocks.Single().Value.Set("owners", PresetValue.Scalar("Meat"));
        project.Entries[0].Document.Blocks.Single().Value.Set("forms", PresetValue.Scalar("meat"));
        Require(Messages(empty, project, Validate(project)).Single().StartsWith("Could not check stages for meat", StringComparison.Ordinal),
            "Addon group lookup must use the same exact-case semantics as custom owners.");
    }

    private static void OwnerSelectors()
    {
        var alias = new FormRow("Unrelated item", "Beef", "Ingestible", "Test.esp", "Test.esp", "0x800~Test.esp", ItemCategories.FOOD, true, true);
        var catalog = new PresetPresentation(new PresetDocument(), [.. Forms, alias]);
        Require(catalog.MatchesOwner("00065C99", Beef) && !catalog.MatchesOwner("00065C99", Goat),
            "Numeric owners must match the resolved item.");
        Require(catalog.MatchesOwner("Beef", alias) && catalog.MatchesOwner("Beef", Beef),
            "A selector that resolves to another form must still match item-name words, as AoT does.");
        Require(catalog.MatchesOwner("  rAw BeEf\t", Beef) && catalog.MatchesOwner("Raw Beef", Beef with { Name = "Raw\nBeef" }),
            "Owner matching must trim selector whitespace and match phrases across item-name line breaks.");
        Require(!catalog.MatchesOwner("eef", Beef) && !catalog.MatchesOwner("Beef", Beef with { Name = "Raw Beef," }),
            "Name matches use space boundaries, not substrings or punctuation-aware word splitting.");
    }

    private static void LoadedPresets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AoTStageAvailability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var files = new List<string>();
        try
        {
            var path = Path.Combine(directory, "stages.yml"); files.Add(path);
            File.WriteAllText(path, """
                templates:
                  foodStages:
                    owners: $food
                    stages: [{no: 0, name: fresh, duration: 1}]
                    finalFormEditorID: FoodBeefCooked
                ownerLists:
                - foodStages(FoodBeef)
                """);
            var preset = new PresetFile(path, Path.Combine("FOOD", "custom", "stages.yml"));
            var loaded = StageAvailability.LoadAsync([preset]).GetAwaiter().GetResult();
            var project = Addon("FoodBeef");
            Require(Messages(loaded, project, Validate(project)).Count == 0, "Loaded template calls must expand before matching their owners.");
            var replacement = project.Add(PresetDocument.Parse("ownerLists:\n- owners: FoodGoat\n  " + StageSettings.Replace("\n", "\n  ").TrimEnd(), "FOOD", PresetKind.Custom),
                "Changed stages", path);
            Require(Messages(loaded, project, Validate(project)).Single().StartsWith("No stages", StringComparison.Ordinal),
                "Editing an installed preset must replace its old stage coverage in the check.");
            project.RemoveBehavior(replacement.Document.Blocks.Single().Id);
            Require(Messages(loaded, project, Validate(project)).Single().StartsWith("No stages", StringComparison.Ordinal),
                "Deleting the last installed stage behavior must mask its old coverage without introducing uncertainty.");
            project.Entries.Remove(replacement);
            loaded = StageAvailability.LoadAsync([preset with { RelativePath = Path.Combine("FOOD", "custom", "nested", "stages.yml") }]).GetAwaiter().GetResult();
            Require(Messages(loaded, project, Validate(project)).Single().StartsWith("No stages", StringComparison.Ordinal),
                "AoT does not load nested custom files, so they must not satisfy dependencies.");

            File.WriteAllText(path, "ownerLists: [");
            loaded = StageAvailability.LoadAsync([preset]).GetAwaiter().GetResult();
            Require(Messages(loaded, project, Validate(project)).Single().StartsWith("Could not verify", StringComparison.Ordinal),
                "A failed installed preset expansion must leave coverage uncertain.");

            File.WriteAllText(path, StageSettings);
            preset = preset with { RelativePath = Path.Combine("FOOD", "AoT_defaultFOOD.yml") };
            loaded = StageAvailability.LoadAsync([preset]).GetAwaiter().GetResult();
            project.Add(PresetDocument.Parse("stages: []\nfinalFormEditorID: FoodBeefCooked", "FOOD", PresetKind.Default), "Replacement default");
            Require(Messages(loaded, project, Validate(project)).Single().StartsWith("Could not verify", StringComparison.Ordinal),
                "An unfinished local default replaces the installed default; old coverage must not hide it.");

            project = new PresetProject();
            project.Add(PresetDocument.Parse("""
                templates:
                  cook:
                    forms: $food
                    transformers: [{FormEditorID: Fire, duration: 1, finalFormEditorID: FoodBeefCooked}]
                formsLists:
                - cook(FoodBeef)
                - cook(FoodGoat)
                """, "FOOD", PresetKind.Addon), "Variable cooking");
            var empty = StageAvailability.LoadAsync([]).GetAwaiter().GetResult();
            var warnings = empty.Check(project, Validate(project), Forms, []);
            Require(warnings.Count == 2 && project.Entries[0].Document.Blocks.Where(block => block.Kind == BlockKind.Rule).All(block => warnings.ContainsKey(block.Id)),
                "Expanded variable rows must keep warnings attached to their authored block IDs.");
        }
        finally
        {
            foreach (var file in files) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static PresetProject Addon(string selector, bool modifiersOnly = false)
    {
        var effects = modifiersOnly ? "timeModulators: [{FormEditorID: Fire, magnitude: 0}]"
            : "transformers: [{FormEditorID: Fire, duration: 1, finalFormEditorID: FoodBeefCooked}]";
        var project = new PresetProject();
        project.Add(PresetDocument.Parse($"formsLists:\n- forms: {selector}\n  {effects}", "FOOD", PresetKind.Addon), "Cooking");
        return project;
    }

    private static Dictionary<string, PresetValidationResult> Validate(PresetProject project) => project.Entries.ToDictionary(entry => entry.Id,
        entry => PresetValidation.Check(entry.Document, CancellationToken.None, new PresetPresentation(entry.Document, Forms)).GetAwaiter().GetResult());

    private static IReadOnlyList<string> Messages(StageAvailability stages, PresetProject project,
        IReadOnlyDictionary<string, PresetValidationResult> validations) => stages.Check(project, validations, Forms, []).Values.SelectMany(value => value).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
