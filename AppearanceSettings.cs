using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AoTPresetEditor;

public enum EditorTheme { Dark, Light, System }
public enum EditorAccent { System, Blue, Teal, Purple, Amber }

public sealed record AppearanceSettings(EditorTheme Theme = EditorTheme.Dark, EditorAccent Accent = EditorAccent.System)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<EditorTheme>(allowIntegerValues: false),
            new JsonStringEnumConverter<EditorAccent>(allowIntegerValues: false)
        }
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AoTPresetEditor", "appearance.json");

    public static AppearanceSettings Load(string? path = null)
    {
        try
        {
            return JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(path ?? DefaultPath), JsonOptions) ?? new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var destination = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, destination, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
