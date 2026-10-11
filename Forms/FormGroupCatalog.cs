using System.IO;
using System.Text.RegularExpressions;

namespace AoTPresetEditor;

public sealed record FormGroupDefinition(string Name, IReadOnlyList<string> Members, string? SourcePath = null);

public static class FormGroupCatalog
{
    private const string Extension = ".txt";
    private const int MaximumFilenameLength = 255;
    private static readonly string RelativeFolder = Path.Combine("SKSE", "Plugins", "AlchemyOfTime", "formGroups");

    public static IReadOnlyList<FormGroupDefinition> Load(string dataFolder)
    {
        var folder = Path.Combine(Path.GetFullPath(dataFolder), RelativeFolder);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(Extension, StringComparison.Ordinal))
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(path => new FormGroupDefinition(Path.GetFileNameWithoutExtension(path),
                ReadMembers(path), path)).ToArray();
    }

    private static IReadOnlyList<string> ReadMembers(string path)
    {
        try { return NormalizeMembers(File.ReadLines(path)).ToArray(); }
        catch (IOException error) { throw new IOException($"Could not read form group '{Path.GetFileNameWithoutExtension(path)}'.", error); }
    }

    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the form group a name.";
        if (name != name.Trim()) return "A form group name cannot start or end with whitespace.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.'))
            return "Use a file name without path separators or a trailing period.";
        if (name.Length + Extension.Length > MaximumFilenameLength) return "The form group name is too long for a file name.";
        if (Regex.IsMatch(name, @"\A(?:CON|PRN|AUX|NUL|COM[1-9\u00B9\u00B2\u00B3]|LPT[1-9\u00B9\u00B2\u00B3])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "That name is reserved by Windows. Choose a different form group name.";
        return null;
    }

    public static IReadOnlyList<string> Validate(FormGroupDefinition group)
    {
        var errors = new List<string>();
        if (ValidateName(group.Name) is { } nameError) errors.Add(nameError);
        if (group.Members.Any(member => member.IndexOfAny(['\r', '\n', '\0']) >= 0))
            errors.Add("Each member must be one form identifier. Add further forms as separate members.");
        return errors;
    }

    public static string Serialize(FormGroupDefinition group)
    {
        var errors = Validate(group);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        var members = NormalizeMembers(group.Members).ToArray();
        return members.Length == 0 ? "" : string.Join(Environment.NewLine, members) + Environment.NewLine;
    }

    public static string GetExportPath(string modFolder, string name)
    {
        if (ValidateName(name) is { } error) throw new InvalidDataException(error);
        return Path.Combine(Path.GetFullPath(modFolder), RelativeFolder, name + Extension);
    }

    private static IEnumerable<string> NormalizeMembers(IEnumerable<string> members) =>
        members.Select(member => member.Trim(' ', '\t', '\n', '\r')).Where(member => member.Length > 0).Distinct(StringComparer.Ordinal);
}

