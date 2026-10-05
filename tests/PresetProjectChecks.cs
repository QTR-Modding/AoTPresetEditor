using System.IO;
using AoTPresetEditor;

internal static class PresetProjectChecks
{
    public static void Run()
    {
        var project = new PresetProject { Name = "Campfire food" };
        var addon = PresetDocument.Parse("""
            # Shared heat
            heat: &heat
              FormEditorID: Fire
              duration: 0.06
            templates:
              cook:
                forms: $raw
                transformers:
                - <<: *heat
                  finalFormEditorID: $cooked
            # Cooking rules
            formsLists:
            - cook(FoodBeef, FoodBeefCooked) # beef
            # End
            """, "FOOD", PresetKind.Addon);
        addon.PendingGroups.Add(new("Fires", ["Fire", "OptionalFire"]));
        var cooking = project.Add(addon, "ImportedCooking", "source.yml", "original hash");
        var aging = project.GetOrCreate("FOOD", PresetKind.Custom);
        aging.Document.Add(BlockKind.Rule, "Beef ages", "CustomRule").Value.Set("forms", PresetValue.UnsetArgument());
        var fallback = project.GetOrCreate("INGR", PresetKind.Default);
        fallback.Document.Add(BlockKind.Rule, "Ingredient aging", "DefaultRule");
        Require(project.Entries.Count == 3 && ReferenceEquals(project.GetOrCreate("food", PresetKind.Addon), cooking),
            "Different behaviors and categories must coexist, and an existing document must be reused.");
        Require(project.FindByBlockId(addon.Blocks[0].Id) == cooking,
            "Selecting an imported block must resolve its owning document.");
        Require(cooking.FileName == "ImportedCooking", "Imported basenames must be retained.");

        var restored = PresetProject.FromDraft(project.ToDraft());
        Require(restored.Name == project.Name && restored.Entries.Count == project.Entries.Count,
            "A project draft must restore every document and the project name.");
        for (var index = 0; index < project.Entries.Count; index++)
        {
            var before = project.Entries[index];
            var after = restored.Entries[index];
            Require(after.Id == before.Id && after.FileName == before.FileName &&
                after.Document.ToDraft() == before.Document.ToDraft(),
                "Project drafts must preserve document IDs, names, kinds, categories, comments, ordering, groups, and unfinished arguments.");
        }
        Require(restored.Entries[0].OpenedPath == cooking.OpenedPath && restored.Entries[0].OpenedHash == cooking.OpenedHash,
            "Reopened drafts must retain the original file change guard.");
        var cloned = project.Clone();
        cloned.Entries[0].Document.Blocks[0].Value.Set("duration", PresetValue.Scalar("7"));
        cloned.Entries[0].Document.PendingGroups.Clear();
        Require(cloned.Entries[0].Id == cooking.Id && cloned.Entries[0].Document.ToDraft() != cooking.Document.ToDraft() &&
            cooking.Document.PendingGroups.Count == 1, "Project snapshots must keep stable entry IDs and isolate mutable documents.");
        Require(PresetProject.FromDraft(addon.ToDraft(), "Old preset").Entries.Single().Document.ToDraft() == addon.ToDraft(),
            "Existing single-document drafts must still open.");

        var duplicate = project.Add(addon.Clone(), "ImportedCooking");
        Require(duplicate.FileName != cooking.FileName && duplicate.Document.Blocks.All(block =>
            addon.Blocks.All(original => original.Id != block.Id)), "Imported duplicates need distinct output names and selectable block IDs.");
        Reject(() => project.Add(PresetDocument.Create("INGR", PresetKind.Default), "Another fallback"));
        var root = Path.Combine(Path.GetTempPath(), "AoTPresetEditor-project-" + Guid.NewGuid().ToString("N"));
        Require(project.GetExportPath(cooking, root) == Path.Combine(root, "SKSE", "Plugins", "AlchemyOfTime", "FOOD", "addon", "ImportedCooking.yml"),
            "Triggered behaviors must retain their original basename in the addon directory.");
        Require(project.GetExportPath(aging, root) == Path.Combine(root, "SKSE", "Plugins", "AlchemyOfTime", "FOOD", "custom", "Campfire food.yml"),
            "Aging definitions must export in the custom directory.");
        Require(project.GetExportPath(fallback, root) == Path.Combine(root, "SKSE", "Plugins", "AlchemyOfTime", "INGR", "AoT_defaultINGR.yml"),
            "Fallback behavior must use the category's fixed default filename.");
        Reject(() => PresetProject.FromDraft("{\"Version\":999,\"Name\":\"Future\",\"Entries\":[]}"));
        ExportTransaction(root, project.GetExportPath(cooking, root), project.GetExportPath(aging, root));
        UnifiedBehaviorKinds();
    }

    private static void UnifiedBehaviorKinds()
    {
        var project = new PresetProject { Name = "Cooking" };
        var source = PresetDocument.Parse("""
            # Shared heat remains shared
            heat: &heat
              FormEditorID: Fire
              duration: 0.05
            templates:
              cook:
                forms: $raw
                transformers:
                - <<: *heat
                  finalFormEditorID: $cooked
            formsLists:
            - cook(BeefGroup, CookedBeef) # first meal
            - cook(Rabbit, CookedRabbit)
            - forms: Potato
              transformers:
              - <<: *heat
                finalFormEditorID: BakedPotato
            """, "FOOD", PresetKind.Addon);
        source.PendingGroups.Add(new("BeefGroup", ["RawBeef", "HorseMeat"]));
        var entry = project.Add(source, "Cooking", "original.yml", "source hash");
        var cooking = source.Blocks.First(block => block.Kind == BlockKind.Rule);
        var potato = source.Blocks.Last();
        var originalPotato = PresetYaml.Write(potato.Value);
        Require(project.BehaviorSelectors(cooking.Id).Select(value => value.Text).SequenceEqual(["BeefGroup", "Rabbit"]),
            "Conversion preflight must inspect every selector argument, excluding transformation results and shared triggers.");
        var before = project.ToDraft();
        try { project.ChangeBehaviorKind(cooking.Id, PresetKind.Custom, _ => throw new InvalidOperationException("Canceled")); }
        catch (InvalidOperationException) { }
        Require(project.ToDraft() == before, "A declined selector conversion must leave the entire project unchanged.");
        var stages = project.ChangeBehaviorKind(cooking.Id, PresetKind.Custom, value =>
        {
            if (value.Text != "BeefGroup") return value;
            var members = PresetValue.List();
            members.Items.AddRange(new[] { "RawBeef", "HorseMeat" }.Select(PresetValue.Scalar));
            return members;
        });
        var family = BehaviorFamily.Find(stages.Document, cooking.Id)!;
        Require(project.Entries.Count == 2 && entry.Document.Kind == PresetKind.Addon && stages.Document.Kind == PresetKind.Custom,
            "Adding stages to one family must split its output without retyping other behaviors.");
        Require(entry.OpenedPath == "original.yml" && stages.OpenedPath is null &&
            PresetYaml.Write(entry.Document.Blocks.Single(block => block.Kind == BlockKind.Rule).Value) == originalPotato,
            "A split must preserve the remaining source file, source change guard, and unrelated rule.");
        Require(family.Rows.Count == 2 && family.Parameters.SequenceEqual(["raw", "cooked"]) &&
            family.Definition.Value.Get("forms") is null && family.Definition.Value.Get("owners")?.Text == "$raw",
            "Retyping must preserve the family rows and parameter order while changing only the affected selector key.");
        Require(BehaviorFamily.GetArgument(stages.Document, cooking.Id, "raw").Items.Select(value => value.Text)
            .SequenceEqual(["RawBeef", "HorseMeat"]), "An explicitly approved group conversion must retain every current member.");
        Require(stages.Document.ToYaml().Contains("first meal") && stages.Document.ToYaml().Contains("*heat") &&
            entry.Document.ToYaml().Contains("*heat") && stages.Document.PendingGroups.Count == 1,
            "Both outputs must preserve their shared dependencies, comments, and pending groups.");
        Require(project.Entries.SelectMany(item => item.Document.Blocks).Select(block => block.Id).Distinct().Count() ==
            project.Entries.Sum(item => item.Document.Blocks.Count), "Split documents need globally distinct editor IDs.");
        var stage = PresetValue.Map(); stage.Set("no", PresetValue.Scalar("0"));
        stage.Set("name", PresetValue.Scalar("")); stage.Set("duration", PresetValue.Scalar("1"));
        var sequence = PresetValue.List(); sequence.Items.Add(stage);
        family.Definition.Value.Set("stages", sequence);
        family.Definition.Value.Set("finalFormEditorID", PresetValue.Scalar("Ash"));
        var duration = BehaviorFamily.MakeVariable(stages.Document, cooking.Id, ["stages", "0", "duration"], "hours");
        BehaviorFamily.SetArgument(stages.Document, cooking.Id, duration, PresetValue.Scalar("2"));
        var triggers = project.ChangeBehaviorKind(cooking.Id, PresetKind.Addon);
        family = BehaviorFamily.Find(triggers.Document, cooking.Id)!;
        Require(family.Parameters.SequenceEqual(["raw", "cooked"]) && family.Definition.Value.Get("stages") is null &&
            family.Definition.Value.Get("finalFormEditorID") is null &&
            BehaviorFamily.GetArgument(triggers.Document, cooking.Id, "cooked").Text == "CookedBeef",
            "Removing stages must remove their variables and rebind existing trigger arguments without shifting values.");
        Require(triggers.FileName != entry.FileName, "Two output files with the same category and purpose need distinct basenames.");

        var one = new PresetProject { Name = "Single" };
        var single = one.Add(PresetDocument.Parse("""
            templates:
              selected:
                owners: $item
                stages:
                - no: 0
                  name: ""
                  duration: $hours
                finalFormEditorID: Ash
            ownerLists:
            - selected(RawBeef, 3)
            """, "FOOD", PresetKind.Custom), "Single", "single.yml", "hash");
        var id = single.Document.Blocks.Single(block => block.Kind == BlockKind.Rule).Id;
        one.ChangeBehaviorKind(id, PresetKind.Default);
        family = BehaviorFamily.Find(single.Document, id)!;
        Require(family.Definition.Value.Get("owners") is null && family.Parameters.SequenceEqual(["hours"]) &&
            BehaviorFamily.GetArgument(single.Document, id, "hours").Text == "3" &&
            single.OpenedPath == "single.yml" && single.OpenedHash == "hash",
            "An explicit type-wide scope must remove selection while preserving stage variables and prior output tracking.");
        var yaml = single.Document.ToYaml();
        single.Document = PresetDocument.Parse(yaml, "FOOD", PresetKind.Default);
        one.ChangeBehaviorKind(id, PresetKind.Custom);
        family = BehaviorFamily.Find(single.Document, id)!;
        Require(family.IsTemplate && family.Definition.Value.Get("owners") is { Kind: ValueKind.Sequence, Items.Count: 0 } &&
            BehaviorFamily.GetArgument(single.Document, id, "hours").Text == "3",
            "Returning from a saved default template use must restore selected-item scope with an empty selector and intact variables.");
        Require(PresetProject.FromDraft(one.ToDraft()).ToDraft() == one.ToDraft(), "Converted behavior output tracking must survive draft round trips.");
        var existingDefault = one.Add(PresetDocument.Create("FOOD", PresetKind.Default), "Existing default");
        existingDefault.Document.Add(BlockKind.Rule, "Other items", "DefaultRule");
        var collisionSnapshot = one.ToDraft();
        try { one.ChangeBehaviorKind(id, PresetKind.Default); }
        catch (InvalidOperationException) { }
        Require(one.ToDraft() == collisionSnapshot && single.Document.Kind == PresetKind.Custom,
            "Rejecting a second type-wide behavior must preserve both existing documents.");

        var inherited = new PresetProject();
        var inheritedEntry = inherited.Add(PresetDocument.Parse("""
            items: &items [BeefGroup]
            base: &base
              forms: *items
            formsLists:
            - <<: *base
              transformers:
              - FormEditorID: Fire
                duration: 0.05
                finalFormEditorID: CookedBeef
            """, "FOOD", PresetKind.Addon), "Inherited");
        var inheritedId = inheritedEntry.Document.Blocks.Single(block => block.Kind == BlockKind.Rule).Id;
        Require(inherited.BehaviorSelectors(inheritedId).Single().Text == "BeefGroup",
            "Selector preflight must follow shared settings and aliased lists.");
        inherited.ChangeBehaviorKind(inheritedId, PresetKind.Custom, value => PresetValue.Scalar("RawBeef"));
        var convertedBase = inheritedEntry.Document.Blocks.Single(block => block.Key == "base");
        Require(convertedBase.Value.Get("forms") is null && convertedBase.Value.Get("owners")?.Items.Single().Text == "RawBeef" &&
            inheritedEntry.Document.ToYaml().Contains("<<: *base"),
            "Inherited selector conversion must update the shared rule layer without breaking its alias.");
    }

    private static void ExportTransaction(string root, string first, string second)
    {
        Directory.CreateDirectory(root);
        try
        {
            PresetExport.Save([new(first, "first original"), new(second, "second original")], [], root);
            Require(File.ReadAllText(first) == "first original" && File.ReadAllText(second) == "second original",
                "All behavior files must be written by one export.");
            var group = new FormGroupDefinition("TransactionGroup", ["Fire"]);
            var groupPath = FormGroupCatalog.GetExportPath(root, group.Name);
            using (var locked = File.Open(second, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => PresetExport.Save([new(first, "first changed"), new(second, "second changed")], [group], root));
            Require(File.ReadAllText(first) == "first original" && File.ReadAllText(second) == "second original" && !File.Exists(groupPath),
                "A late failure must restore earlier preset files and remove newly created companion groups.");
            Reject(() => PresetExport.Save([new(first, "duplicate"), new(first.ToUpperInvariant(), "duplicate")], [], root));
            Require(File.ReadAllText(first) == "first original" &&
                !Directory.EnumerateFiles(root, "*.aot-*", SearchOption.AllDirectories).Any(),
                "Rejected output collisions and recovered exports must leave no partial writes or staging files.");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                Directory.Delete(directory);
            Directory.Delete(root);
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("An invalid draft or unsafe export was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
