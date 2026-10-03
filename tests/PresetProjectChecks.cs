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
