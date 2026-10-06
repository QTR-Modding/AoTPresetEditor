using System.IO;

namespace AoTPresetEditor;

// Finds authored stage definitions; it does not predict module settings, exclusions, or runtime activation.
public sealed class StageAvailability
{
    private readonly List<(PresetFile File, PresetDocument? Document)> installed = [];
    private bool loaded;

    public static async Task<StageAvailability> LoadAsync(IReadOnlyList<PresetFile> files, CancellationToken token = default)
    {
        var result = new StageAvailability();
        foreach (var file in files.Where(IsStageFile))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var source = await File.ReadAllTextAsync(file.FullPath, token);
                var expanded = await PresetCatalog.Expand(source, token);
                result.installed.Add((file, PresetDocument.Parse(expanded, file.Category, file.Kind)));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { result.installed.Add((file, null)); }
        }
        result.loaded = true;
        return result;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Check(PresetProject project,
        IReadOnlyDictionary<string, PresetValidationResult> validations, IReadOnlyList<FormRow> forms,
        IReadOnlyList<FormGroupDefinition> groups)
    {
        var warnings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var providers = new List<(PresetDocument Document, PresetValue Rule)>();
        var uncertainCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var openedPaths = project.Entries.Select(entry => entry.OpenedPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaults = project.Entries.Where(entry => !entry.IsInactive && entry.Document.Kind == PresetKind.Default)
            .Select(entry => entry.Document.Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, document) in installed)
        {
            if (openedPaths.Contains(file.FullPath) || file.Kind == PresetKind.Default && defaults.Contains(file.Category)) continue;
            if (document is null) uncertainCategories.Add(file.Category);
            else AddProviders(document, providers);
        }
        foreach (var entry in project.Entries.Where(entry => !entry.IsInactive && entry.Document.Kind is PresetKind.Custom or PresetKind.Default))
        {
            if (validations.GetValueOrDefault(entry.Id) is not { Expanded.Length: > 0 } validation)
            { uncertainCategories.Add(entry.Document.Category); continue; }
            AddProviders(PresetDocument.Parse(validation.Expanded, entry.Document.Category, entry.Document.Kind), providers);
        }

        var allGroups = groups.Concat(project.Entries.SelectMany(entry => entry.Document.PendingGroups))
            .GroupBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var catalog = new PresetPresentation(new PresetDocument(), forms, allGroups.Values.ToArray());
        foreach (var entry in project.Entries.Where(entry => !entry.IsInactive && entry.Document.Kind == PresetKind.Addon))
        {
            if (validations.GetValueOrDefault(entry.Id) is not { Expanded.Length: > 0 } validation) continue;
            var presentation = new PresetPresentation(entry.Document, forms, allGroups.Values.ToArray(), validation.Expanded);
            foreach (var block in entry.Document.Blocks.Where(block => block.Kind == BlockKind.Rule))
            {
                var effective = presentation.EffectiveValue(block);
                if (effective is null) continue;
                var messages = new List<string>();
                foreach (var rule in effective.Kind == ValueKind.Sequence ? effective.Items : [effective])
                    CheckRule(rule, entry.Document.Category, providers, catalog, allGroups,
                        !loaded || uncertainCategories.Contains(entry.Document.Category), messages);
                if (messages.Count > 0) warnings[block.Id] = messages.Distinct(StringComparer.Ordinal).ToArray();
            }
        }
        return warnings;
    }

    private static bool IsStageFile(PresetFile file) => file.Kind == PresetKind.Default ||
        file.Kind == PresetKind.Custom && Path.GetDirectoryName(file.RulePath) is "";

    private static void AddProviders(PresetDocument document, List<(PresetDocument Document, PresetValue Rule)> providers)
    {
        foreach (var block in document.Blocks.Where(block => block.Kind == BlockKind.Rule)) providers.Add((document, block.Value));
    }

    private static void CheckRule(PresetValue rule, string category,
        IReadOnlyList<(PresetDocument Document, PresetValue Rule)> providers, PresetPresentation catalog,
        IReadOnlyDictionary<string, FormGroupDefinition> groups, bool incomplete, List<string> warnings)
    {
        var transformations = rule.Get("transformers") is { Kind: ValueKind.Sequence, Items.Count: > 0 };
        var modifiers = rule.Get("timeModulators") is { Kind: ValueKind.Sequence, Items.Count: > 0 };
        if (!transformations && !modifiers || rule.Get("forms") is not { IsNull: false } selectors) return;
        var requirement = transformations && modifiers ? "Transformations and time modifiers need stages to work."
            : transformations ? "Transformations need stages to work." : "Time modifiers need stages to work.";
        var sources = new HashSet<FormRow>();
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in selectors.Kind == ValueKind.Sequence ? selectors.Items : [selectors])
        {
            if (value.Kind != ValueKind.Scalar || value.IsNull) continue;
            var identifiers = groups.TryGetValue(value.Text, out var group) ? group.Members : [value.Text];
            foreach (var identifier in identifiers)
                if (catalog.ResolveForm(identifier) is { } form) sources.Add(form);
                else unresolved.Add(identifier);
        }
        var missing = new List<string>();
        var uncertain = new List<string>();
        foreach (var source in sources)
        {
            var matches = providers.Where(provider => provider.Document.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
                (provider.Document.Kind == PresetKind.Default || MatchesOwner(provider.Rule.Get("owners"), source, catalog))).ToArray();
            if (matches.Any(provider => HasStages(provider.Document, provider.Rule, source, catalog))) continue;
            (incomplete || matches.Length > 0 ? uncertain : missing).Add(catalog.FormLabel(source.Identifier));
        }
        if (missing.Count > 0) warnings.Add($"No stages were found for {Labels(missing)} in this preset or the loaded presets. {requirement}");
        if (uncertain.Count > 0) warnings.Add($"Could not verify stages for {Labels(uncertain)} in this preset and the loaded presets. {requirement}");
        if (unresolved.Count > 0) warnings.Add($"Could not check stages for {Labels(unresolved)}: forms were not found in the loaded game data. {requirement}");
    }

    private static string Labels(IReadOnlyCollection<string> labels)
    {
        const int previewCount = 3;
        return string.Join(", ", labels.Take(previewCount)) + (labels.Count > previewCount ? $" and {labels.Count - previewCount} more" : "");
    }

    private static bool MatchesOwner(PresetValue? owners, FormRow form, PresetPresentation catalog)
    {
        if (owners is null) return false;
        return (owners.Kind == ValueKind.Sequence ? owners.Items : [owners]).Any(owner => owner.Kind == ValueKind.Scalar && !owner.IsNull &&
            catalog.MatchesOwner(owner.Text, form));
    }

    private static bool HasStages(PresetDocument document, PresetValue rule, FormRow source, PresetPresentation catalog)
    {
        if (rule.Get("stages") is not { Kind: ValueKind.Sequence, Items.Count: > 0 } stages ||
            PresetSchema.Validate(rule, "rule", document.Category, document.Kind).Count > 0 ||
            rule.Get("finalFormEditorID") is not { Kind: ValueKind.Scalar } final ||
            catalog.ResolveForm(final.Text) is not { IsBoundObject: true }) return false;
        foreach (var stage in stages.Items)
        {
            if (stage.Get("no") is { } number && PresetSchema.TryUnsigned(number.Text, out var index) && index == 0 ||
                stage.Get("FormEditorID") is not { IsNull: false } replacement) continue;
            if (replacement.Kind != ValueKind.Scalar || catalog.ResolveForm(replacement.Text) is not { } form ||
                !new FormSelection(Category: document.Category).Matches(form) ||
                !form.Type.Equals(source.Type, StringComparison.OrdinalIgnoreCase) || form == source) return false;
        }
        return true;
    }
}
