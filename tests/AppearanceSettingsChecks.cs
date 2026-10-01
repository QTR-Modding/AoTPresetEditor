using System.IO;
using System.Text;
using AoTPresetEditor;

internal static class AppearanceSettingsChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AoT-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "appearance.json");
        try
        {
            Require(AppearanceSettings.Load(path) == new AppearanceSettings(), "Missing appearance settings did not use defaults.");
            Require(AppearanceSettings.DefaultPath == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AoTPresetEditor", "appearance.json"), "Appearance settings use the wrong per-user location.");

            var settings = new AppearanceSettings(EditorTheme.Light, EditorAccent.Purple);
            settings.Save(path);
            Require(AppearanceSettings.Load(path) == settings, "Appearance settings did not round-trip.");
            var bytes = File.ReadAllBytes(path);
            var json = Encoding.UTF8.GetString(bytes);
            Require(json.Contains("\"Light\"") && json.Contains("\"Purple\""), "Appearance enums were not written as names.");
            Require(!bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "Appearance JSON has a UTF-8 BOM.");

            foreach (var invalid in new[] { "broken json", "null", "{\"Theme\":\"Unknown\"}", "{\"Accent\":\"Unknown\"}", "{\"Theme\":1}", "{\"Accent\":99}" })
            {
                File.WriteAllText(path, invalid);
                Require(AppearanceSettings.Load(path) == new AppearanceSettings(), "Invalid appearance settings did not use defaults: " + invalid);
            }
            File.WriteAllText(path, "{\"Accent\":\"Teal\"}");
            Require(AppearanceSettings.Load(path) == new AppearanceSettings(Accent: EditorAccent.Teal), "An omitted theme did not retain its default.");

            settings.Save(path);
            bytes = File.ReadAllBytes(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try { new AppearanceSettings(EditorTheme.Dark, EditorAccent.Amber).Save(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
                Require(failed, "Saving locked appearance settings did not report its failure.");
                Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), "A failed save changed existing appearance settings.");
            }
            Require(Directory.GetFiles(folder).Length == 1, "Appearance save left a temporary file behind.");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
