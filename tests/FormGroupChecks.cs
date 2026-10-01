using System.IO;
using AoTPresetEditor;

internal static class FormGroupChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "AoTPresetEditor-groups-" + Guid.NewGuid().ToString("N"));
        var groupPath = FormGroupCatalog.GetExportPath(root, "ColdPlaces");
        var folder = Path.GetDirectoryName(groupPath)!;
        var nested = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nested);
        var ignoredExtension = Path.Combine(folder, "Ignored.TXT");
        var ignoredNested = Path.Combine(nested, "Ignored.txt");
        const string contents = " 0x800~Locations.esp \n\nLocationWinterhold\nLocationWinterhold\nUnknownOptionalModForm\nOtherGroup\n#not a comment syntax\n";
        try
        {
            File.WriteAllText(groupPath, contents);
            File.WriteAllText(ignoredExtension, "Ignored");
            File.WriteAllText(ignoredNested, "Ignored");
            var groups = FormGroupCatalog.Load(root);
            Require(groups.Count == 1, "QTR's folder depth or lowercase extension rules changed.");
            var group = groups[0];
            Require(group.Name == "ColdPlaces" && group.SourcePath == groupPath, "The file-stem identity or source path was lost.");
            Require(group.Members.SequenceEqual(["0x800~Locations.esp", "LocationWinterhold", "UnknownOptionalModForm", "OtherGroup", "#not a comment syntax"]), "Members were dropped or interpreted as nested groups/comments.");
            Require(File.ReadAllText(groupPath) == contents, "Reading altered an installed group file.");
            var serialized = FormGroupCatalog.Serialize(new("NewGroup", [" FormA ", "", "FormA", "forma"]));
            Require(serialized == string.Join(Environment.NewLine, new[] { "FormA", "forma", "" }), "Member normalization changed identifier case or retained exact duplicates.");
            Require(FormGroupCatalog.Serialize(new("EmptyGroup", [])).Length == 0, "QTR's valid empty groups were rejected.");
            foreach (var invalid in new[] { "", "../escape", "..", "CON", "nul.extra", "LPT1", "Trailing.", " Leading", "Trailing " })
                Require(FormGroupCatalog.ValidateName(invalid) is not null, "Unsafe name was accepted: " + invalid);
            Require(FormGroupCatalog.ValidateName("My Cold Places") is null, "A valid group name was rejected.");
            Require(FormGroupCatalog.Validate(new("Valid", ["FormA\nFormB"])).Count > 0, "One member injected another line.");
            var document = PresetDocument.Create("FOOD", PresetKind.Custom);
            document.PendingGroups.Add(new("DraftGroup", ["FormA"]));
            var copy = document.Clone();
            ((string[])copy.PendingGroups[0].Members)[0] = "Changed";
            copy.PendingGroups.Add(new("CancelledGroup", ["FormB"]));
            Require(document.PendingGroups.Count == 1 && document.PendingGroups[0].Members[0] == "FormA", "A cancelled nested draft changed its parent's groups.");
            Require(!document.ToYaml().Contains("DraftGroup"), "Companion group definitions leaked into preset YAML.");
        }
        finally
        {
            File.Delete(groupPath);
            File.Delete(ignoredExtension);
            File.Delete(ignoredNested);
            Directory.Delete(nested);
            for (var directory = folder; directory != root; directory = Path.GetDirectoryName(directory)!) Directory.Delete(directory);
            Directory.Delete(root);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
