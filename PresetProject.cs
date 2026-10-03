using System.IO;
using System.Text.Json;

namespace AoTPresetEditor;

public sealed class PresetProjectEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public PresetDocument Document { get; set; } = new();
    public string? OpenedPath { get; set; }
    public string? OpenedHash { get; set; }

    public PresetProjectEntry Clone() => new()
    {
        Id = Id, FileName = FileName, Document = Document.Clone(),
        OpenedPath = OpenedPath, OpenedHash = OpenedHash
    };
}

public sealed class PresetProject
{
    public string Name { get; set; } = "New preset";
    public List<PresetProjectEntry> Entries { get; } = [];

    public PresetProjectEntry Add(PresetDocument document, string fileName, string? openedPath = null, string? openedHash = null)
    {
        if (document.Kind == PresetKind.Default && Find(document.Category, document.Kind) is not null)
            throw new InvalidDataException("This preset already contains fallback aging for " + document.Category + ".");
        var name = fileName;
        if (!ValidFileName(name)) throw new InvalidDataException("Give the preset file a valid name without path separators.");
        var suffix = 2;
        while (Entries.Any(entry => SamePurpose(entry.Document, document.Category, document.Kind) &&
            entry.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = fileName + " " + suffix++;
        var ids = Entries.SelectMany(entry => entry.Document.Blocks).Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var block in document.Blocks)
            while (!ids.Add(block.Id)) block.Id = Guid.NewGuid().ToString("N");
        var result = new PresetProjectEntry { FileName = name, Document = document, OpenedPath = openedPath, OpenedHash = openedHash };
        Entries.Add(result);
        return result;
    }

    public PresetProjectEntry? Find(string category, PresetKind kind) =>
        Entries.FirstOrDefault(entry => SamePurpose(entry.Document, category, kind));

    public PresetProjectEntry? FindByBlockId(string id) =>
        Entries.FirstOrDefault(entry => entry.Document.Blocks.Any(block => block.Id == id));

    public PresetProjectEntry GetOrCreate(string category, PresetKind kind) =>
        Find(category, kind) ?? Add(PresetDocument.Create(category, kind), Name);

    public PresetProject Clone()
    {
        var clone = new PresetProject { Name = Name };
        clone.Entries.AddRange(Entries.Select(entry => entry.Clone()));
        return clone;
    }

    public string GetExportPath(PresetProjectEntry entry, string modFolder)
    {
        var document = entry.Document;
        if (!ValidFileName(document.Category)) throw new InvalidDataException("Choose a valid item category.");
        var directory = Path.Combine(Path.GetFullPath(modFolder), "SKSE", "Plugins", "AlchemyOfTime", document.Category);
        if (document.Kind == PresetKind.Default) return Path.Combine(directory, "AoT_default" + document.Category + ".yml");
        if (!ValidFileName(entry.FileName)) throw new InvalidDataException("Give the preset file a valid name without path separators.");
        var folder = document.Kind switch
        {
            PresetKind.Custom => "custom",
            PresetKind.Addon => "addon",
            _ => throw new InvalidDataException("Choose the behavior for this preset before exporting.")
        };
        return Path.Combine(directory, folder, entry.FileName + ".yml");
    }

    private sealed record DraftEntry(string Id, string FileName, string Document, string? OpenedPath, string? OpenedHash);
    private sealed record DraftData(int Version, string Name, IReadOnlyList<DraftEntry> Entries);
    private const int DraftVersion = 1;
    private static readonly JsonSerializerOptions DraftOptions = new() { WriteIndented = true };

    public string ToDraft() => JsonSerializer.Serialize(new DraftData(DraftVersion, Name,
        Entries.Select(entry => new DraftEntry(entry.Id, entry.FileName, entry.Document.ToDraft(), entry.OpenedPath, entry.OpenedHash)).ToArray()), DraftOptions);

    public static PresetProject FromDraft(string source, string fallbackName = "Untitled")
    {
        using var json = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 256 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The draft must contain a preset.");
        if (json.RootElement.TryGetProperty("Document", out _))
        {
            var legacy = new PresetProject { Name = fallbackName };
            legacy.Add(PresetDocument.FromDraft(source), fallbackName);
            return legacy;
        }
        var draft = JsonSerializer.Deserialize<DraftData>(source, DraftOptions)
            ?? throw new InvalidDataException("The draft is empty.");
        if (draft.Version != DraftVersion || draft.Name is null || draft.Entries is null)
            throw new InvalidDataException("This draft version is not supported.");
        var project = new PresetProject { Name = draft.Name };
        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var saved in draft.Entries)
        {
            if (saved is null || string.IsNullOrWhiteSpace(saved.Id) || !entryIds.Add(saved.Id) ||
                saved.Document is null || saved.FileName is null)
                throw new InvalidDataException("The draft contains an invalid preset entry.");
            var entry = project.Add(PresetDocument.FromDraft(saved.Document), saved.FileName, saved.OpenedPath, saved.OpenedHash);
            entry.Id = saved.Id;
        }
        return project;
    }

    private static bool SamePurpose(PresetDocument document, string category, PresetKind kind) =>
        document.Kind == kind && document.Category.Equals(category, StringComparison.OrdinalIgnoreCase);

    private static bool ValidFileName(string name) => !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');
}