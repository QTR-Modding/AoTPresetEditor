using System.IO;
using System.Text;
using AoTPresetEditor;

internal static class PresetExportChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "AoTPresetEditor-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var preset = Path.Combine(root, "SKSE", "Plugins", "AlchemyOfTime", "FOOD", "custom", "Example.yml");
        var group = new FormGroupDefinition("ColdPlaces", ["0x123~Skyrim.esm", "LocationWinterhold"]);
        var groupPath = FormGroupCatalog.GetExportPath(root, group.Name);
        try
        {
            PresetExport.Save(preset, "original: 1\n", [group], root);
            Require(File.ReadAllText(preset) == "original: 1\n", "The preset was not written.");
            Require(File.ReadAllText(groupPath) == FormGroupCatalog.Serialize(group), "The companion group was not written.");
            Require(!File.ReadAllBytes(preset).AsSpan().StartsWith(Encoding.UTF8.Preamble), "Preset output gained a UTF8 byte-order mark.");
            var groupTime = File.GetLastWriteTimeUtc(groupPath);
            PresetExport.Save(preset, "changed: 2\n", [group], root);
            Require(File.GetLastWriteTimeUtc(groupPath) == groupTime, "An identical installed group was rewritten.");
            var added = new FormGroupDefinition("NewGroup", ["FormA"]);
            var addedPath = FormGroupCatalog.GetExportPath(root, added.Name);
            var conflicting = group with { Members = new[] { "Different" } };
            Reject(() => PresetExport.Save(preset, "must not replace\n", [added, conflicting], root));
            Require(File.ReadAllText(preset) == "changed: 2\n" && !File.Exists(addedPath), "A conflicting group allowed a partial export.");
            Require(File.ReadAllText(groupPath) == FormGroupCatalog.Serialize(group), "A conflicting group changed an existing file.");
            using (var lockedPreset = File.Open(preset, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => PresetExport.Save(preset, "must roll back\n", [added], root));
            Require(!File.Exists(addedPath) && File.ReadAllText(preset) == "changed: 2\n", "A late failure did not roll back the new companion file.");
            Require(!Directory.EnumerateFiles(root, "*.aot-*", SearchOption.AllDirectories).Any(), "Successful rollback left staging or backup files.");
            Reject(() => PresetExport.Save(preset, "duplicate\n", [added, added with { Name = "newgroup" }], root));
            Reject(() => PresetExport.Save(Path.Combine(Path.GetDirectoryName(root)!, "outside.yml"), "outside\n", [], root));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) Directory.Delete(directory);
            Directory.Delete(root);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("An unsafe or failing export was accepted.");
    }
}

