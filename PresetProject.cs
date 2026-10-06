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
    public bool PendingRemoval { get; set; }
    public bool HasBehaviors => Document.Blocks.Any(block => block.Kind == BlockKind.Rule ||
        block.Kind == BlockKind.Template && block.IsBehavior);
    public bool IsInactive => !HasBehaviors && (OpenedPath is null || PendingRemoval);

    public PresetProjectEntry Clone() => new()
    {
        Id = Id, FileName = FileName, Document = Document.Clone(),
        OpenedPath = OpenedPath, OpenedHash = OpenedHash, PendingRemoval = PendingRemoval
    };
}

public sealed class PresetProject
{
    public string Name { get; set; } = "New preset";
    public List<PresetProjectEntry> Entries { get; } = [];

    public PresetProjectEntry Add(PresetDocument document, string fileName, string? openedPath = null, string? openedHash = null,
        bool pendingRemoval = false)
    {
        var result = new PresetProjectEntry { Document = document, OpenedPath = openedPath,
            OpenedHash = openedHash, PendingRemoval = pendingRemoval };
        if (pendingRemoval && (openedPath is null || result.HasBehaviors))
            throw new InvalidDataException("Only an emptied source file can be marked for removal.");
        if (!result.IsInactive && document.Kind == PresetKind.Default && Entries.Any(entry =>
            !entry.IsInactive && SamePurpose(entry.Document, document.Category, document.Kind)))
            throw new InvalidDataException("This preset already defines stages for other " + document.Category + " items.");
        var name = fileName;
        if (!ValidFileName(name)) throw new InvalidDataException("Give the preset file a valid name without path separators.");
        var suffix = 2;
        while (!pendingRemoval && Entries.Any(entry => !entry.IsInactive && SamePurpose(entry.Document, document.Category, document.Kind) &&
            entry.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = fileName + " " + suffix++;
        var ids = Entries.SelectMany(entry => entry.Document.Blocks).Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var block in document.Blocks)
            while (!ids.Add(block.Id)) block.Id = Guid.NewGuid().ToString("N");
        result.FileName = name;
        Entries.Add(result);
        return result;
    }

    public PresetProjectEntry? Find(string category, PresetKind kind) =>
        Entries.FirstOrDefault(entry => !entry.IsInactive && SamePurpose(entry.Document, category, kind)) ??
        Entries.FirstOrDefault(entry => SamePurpose(entry.Document, category, kind));

    public PresetProjectEntry? FindByBlockId(string id) =>
        Entries.FirstOrDefault(entry => entry.Document.Blocks.Any(block => block.Id == id));

    public PresetProjectEntry GetOrCreate(string category, PresetKind kind) =>
        Find(category, kind) ?? Add(PresetDocument.Create(category, kind), Name);

    public void RemoveBehavior(string id)
    {
        var entry = FindByBlockId(id) ?? throw new InvalidOperationException("This behavior is no longer present.");
        var document = entry.Document;
        var block = document.Blocks.First(block => block.Id == id);
        var family = BehaviorFamily.Find(document, id);
        foreach (var row in family?.Rows.ToArray() ?? [block]) document.Remove(row);
        if (family is { IsTemplate: true })
        {
            family.Definition.IsBehavior = false;
            if (new PresetPresentation(document, []).Uses(family.Definition).Count == 0)
                document.Remove(family.Definition);
        }
        entry.PendingRemoval = !entry.HasBehaviors && entry.OpenedPath is not null;
    }

    public IReadOnlyList<string> RemoveEmptyEntries()
    {
        var empty = Entries.Where(entry => entry.OpenedPath is null && !entry.Document.HasRetainedContent).ToArray();
        foreach (var entry in empty) Entries.Remove(entry);
        return empty.Select(entry => entry.Id).ToArray();
    }

    public IReadOnlyList<PresetValue> BehaviorSelectors(string ruleId)
    {
        var entry = FindByBlockId(ruleId) ?? throw new InvalidOperationException("This behavior is no longer present.");
        var selectors = new List<PresetValue>();
        var copy = BehaviorDocument(entry.Document, ruleId, out _);
        BehaviorFamily.ConvertSelectors(copy, ruleId, value => { selectors.Add(value.Clone()); return value; });
        return selectors;
    }

    public PresetProjectEntry ChangeBehaviorKind(string ruleId, PresetKind targetKind,
        Func<PresetValue, PresetValue>? convertSelector = null)
    {
        if (targetKind is not (PresetKind.Custom or PresetKind.Addon or PresetKind.Default))
            throw new InvalidOperationException("Choose whether this behavior defines stages for selected items or other items of this type.");
        var entry = FindByBlockId(ruleId) ?? throw new InvalidOperationException("This behavior is no longer present.");
        if (entry.Document.Kind == targetKind) return entry;
        if (targetKind == PresetKind.Default && Entries.Any(other => other != entry && !other.IsInactive &&
            SamePurpose(other.Document, entry.Document.Category, PresetKind.Default)))
            throw new InvalidOperationException("This preset already defines stages for other items of this type.");
        var target = BehaviorDocument(entry.Document, ruleId, out var familyIds);
        var family = BehaviorFamily.Find(target, ruleId)!;
        if (targetKind == PresetKind.Default && family.Rows.Count > 1)
            throw new InvalidOperationException("Stages for other items of this type can have only one Variables row.");
        BehaviorFamily.ChangeKind(target, ruleId, targetKind, convertSelector);

        var remaining = entry.Document.Clone();
        foreach (var block in remaining.Blocks.Where(block => familyIds.Contains(block.Id)).ToArray())
        {
            if (block.Kind == BlockKind.Rule || !new PresetPresentation(remaining, []).Uses(block)
                .Any(use => !familyIds.Contains(use.Id))) remaining.Remove(block);
            else block.Id = Guid.NewGuid().ToString("N");
        }
        if (new PresetPresentation(remaining, []).Behaviors().Count == 0)
        {
            // Keep unused imported definitions too when the complete file changes purpose.
            var transferred = target.Blocks.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var block in entry.Document.Blocks.Where(block => !transferred.Contains(block.Id)))
                target.Blocks.Add(block.Clone());
            entry.Document = target;
            var stem = entry.FileName;
            var suffix = 2;
            while (Entries.Any(other => other != entry && !other.IsInactive && SamePurpose(other.Document, target.Category, target.Kind) &&
                other.FileName.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase))) entry.FileName = stem + " " + suffix++;
            return entry;
        }
        entry.Document = remaining;
        return Add(target, entry.FileName);
    }

    private static PresetDocument BehaviorDocument(PresetDocument source, string ruleId, out HashSet<string> familyIds)
    {
        var family = BehaviorFamily.Find(source, ruleId)
            ?? throw new InvalidOperationException("This imported behavior uses a shared rule list. Edit its original structure before changing which items it applies to.");
        familyIds = family.Rows.Select(row => row.Id).Append(family.Definition.Id).ToHashSet(StringComparer.Ordinal);
        var selected = familyIds;
        var presentation = new PresetPresentation(source, []);
        if (family.Rows.Any(row => presentation.Uses(row).Any(use => !selected.Contains(use.Id))))
            throw new InvalidOperationException("Another behavior refers to shared data inside this behavior. Move that data to shared settings before changing its stage settings.");
        var dependencies = source.Blocks.Where(block => selected.Contains(block.Id) ||
            presentation.Uses(block).Any(use => selected.Contains(use.Id))).Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        if (source.Blocks.Any(block => dependencies.Contains(block.Id) && block.Kind == BlockKind.Rule && !selected.Contains(block.Id)))
            throw new InvalidOperationException("This behavior shares data stored inside another behavior. Move that data to shared settings before changing its stage settings.");
        var result = source.Clone();
        foreach (var block in result.Blocks.Where(block => !dependencies.Contains(block.Id)).ToArray()) result.Remove(block);
        return result;
    }

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

    private sealed record DraftEntry(string Id, string FileName, string Document, string? OpenedPath, string? OpenedHash,
        bool PendingRemoval = false);
    private sealed record DraftData(int Version, string Name, IReadOnlyList<DraftEntry> Entries);
    private const int DraftVersion = 1;
    private static readonly JsonSerializerOptions DraftOptions = new() { WriteIndented = true };

    public string ToDraft() => JsonSerializer.Serialize(new DraftData(DraftVersion, Name,
        Entries.Select(entry => new DraftEntry(entry.Id, entry.FileName, entry.Document.ToDraft(), entry.OpenedPath, entry.OpenedHash,
            entry.PendingRemoval)).ToArray()), DraftOptions);

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
            var entry = project.Add(PresetDocument.FromDraft(saved.Document), saved.FileName, saved.OpenedPath, saved.OpenedHash,
                saved.PendingRemoval);
            entry.Id = saved.Id;
        }
        return project;
    }

    private static bool SamePurpose(PresetDocument document, string category, PresetKind kind) =>
        document.Kind == kind && document.Category.Equals(category, StringComparison.OrdinalIgnoreCase);

    private static bool ValidFileName(string name) => !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');
}
