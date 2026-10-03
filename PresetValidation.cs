namespace AoTPresetEditor;

public sealed record PresetValidationResult(string Source, string Expanded,
    Dictionary<string, IReadOnlyList<string>> Errors, IReadOnlyList<string> FileErrors)
{
    public bool IsValid => FileErrors.Count == 0 && Errors.Values.All(errors => errors.Count == 0);
}

public static class PresetValidation
{
    public static async Task<PresetValidationResult> Check(PresetDocument document, CancellationToken token, PresetPresentation? catalog = null)
    {
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var fileErrors = new List<string>();
        var source = "";
        var expanded = "";
        var templateNames = document.Blocks.Where(block => block.Kind == BlockKind.Template).Select(block => block.Key).ToHashSet(StringComparer.Ordinal);
        var rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        if (rules.Length == 0) fileErrors.Add("Add at least one rule.");
        if (document.Kind == PresetKind.Default && rules.Length > 1) fileErrors.Add("A default preset can contain one rule.");
        if (document.Kind == PresetKind.Other) fileErrors.Add("Choose Default, Custom, or Addon as the preset kind.");
        if (document.Blocks.Select(block => block.Id).Distinct(StringComparer.Ordinal).Count() != document.Blocks.Count)
            fileErrors.Add("Two blocks have the same editor identifier. Reopen a copy of the preset before editing.");
        foreach (var group in document.PendingGroups)
            fileErrors.AddRange(FormGroupCatalog.Validate(group).Select(error => group.Name + ": " + error));
        if (document.PendingGroups.Select(group => group.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.PendingGroups.Count)
            fileErrors.Add("Each new form group needs a different name.");
        foreach (var block in document.Blocks)
        {
            var blockErrors = new List<string>();
            if (string.IsNullOrWhiteSpace(block.Name)) blockErrors.Add("Give this block a name.");
            if (HasUnsetArgument(block.Value, templateNames)) blockErrors.Add("Fill the missing values in Variations for this item.");
            if (block.Kind != BlockKind.Rule)
                blockErrors.AddRange(PresetSchema.Validate(block.Value, block.Context, document.Category, document.Kind, partial: true));
            errors[block.Id] = blockErrors;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            source = document.ToYaml();
            expanded = await PresetCatalog.Expand(source, token);
            var result = PresetDocument.Parse(expanded, document.Category, document.Kind);
            var expandedRules = result.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
            if (rules.Length == 1 && rules[0].Key is "ownerLists" or "formsLists")
            {
                var expandedList = PresetValue.List();
                expandedList.Items.AddRange(expandedRules.Select(block => block.Value));
                var block = rules[0];
                errors[block.Id] = [.. errors[block.Id], .. PresetSchema.Validate(expandedList, "Rules", document.Category, document.Kind)];
                if (catalog is not null)
                    for (var index = 0; index < expandedList.Items.Count; index++)
                        errors[block.Id] = [.. errors[block.Id], .. KnownFormErrors(expandedList.Items[index], document.Category, catalog)
                            .Select(error => $"Entry {index + 1}: {error}")];
                if (expandedList.Items.Count == 0) errors[block.Id] = [.. errors[block.Id], "The rule list is empty."];
            }
            else
            {
                if (expandedRules.Length != rules.Length)
                    fileErrors.Add("Template expansion changed the number of top-level rules.");
                for (var index = 0; index < Math.Min(rules.Length, expandedRules.Length); index++)
                {
                    token.ThrowIfCancellationRequested();
                    var block = rules[index];
                    errors[block.Id] = [.. errors[block.Id], .. PresetSchema.Validate(expandedRules[index].Value,
                        PresetDocument.RuleContext(document.Kind), document.Category, document.Kind)];
                    if (catalog is not null) errors[block.Id] = [.. errors[block.Id], .. KnownFormErrors(expandedRules[index].Value, document.Category, catalog)];
                }
            }
            foreach (var block in document.Blocks.Where(block => block.Kind == BlockKind.Shared))
            {
                var resolved = result.Blocks.FirstOrDefault(candidate => candidate.Kind == BlockKind.Shared && candidate.Key == block.Key);
                if (resolved is null) continue;
                errors[block.Id] = errors[block.Id].Concat(PresetSchema.Validate(resolved.Value, block.Context,
                    document.Category, document.Kind, partial: true)).Distinct(StringComparer.Ordinal).ToArray();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            fileErrors.Add(error.Message);
        }
        return new(source, expanded, errors, fileErrors);
    }

    private static IReadOnlyList<string> KnownFormErrors(PresetValue rule, string category, PresetPresentation catalog)
    {
        var errors = new List<string>();
        void CheckResult(PresetValue? value, string label)
        {
            if (value is { Kind: ValueKind.Scalar } && catalog.ResolveForm(value.Text) is { IsBoundObject: false } form)
                errors.Add($"{label}: {catalog.FormLabel(value.Text)} is {form.Type}, not a bound object that AoT can produce.");
        }
        CheckResult(rule.Get("finalFormEditorID"), "Final result item");
        if (rule.Get("transformers") is { Kind: ValueKind.Sequence } transformations)
            for (var index = 0; index < transformations.Items.Count; index++)
                CheckResult(transformations.Items[index].Get("finalFormEditorID"), $"Transformations {index + 1}: Result item");
        if (rule.Get("stages") is not { Kind: ValueKind.Sequence } stages) return errors;
        var stageField = PresetSchema.GetFields("stage", PresetKind.Custom).Single(field => field.Key == "FormEditorID");
        var categorySelection = new FormSelection(Category: category);
        var sources = catalog.SourceForms(rule).Where(categorySelection.Matches).ToArray();
        var replacements = new Dictionary<FormRow, string>();
        for (var index = 0; index < stages.Items.Count; index++)
        {
            var stage = stages.Items[index];
            if (PresetSchema.InactiveReason(stageField, stage, "stage", category) is not null ||
                stage.Get("FormEditorID") is not { Kind: ValueKind.Scalar } value || catalog.ResolveForm(value.Text) is not { } replacement) continue;
            var label = $"Stages {index + 1}: Stage item";
            if (!categorySelection.Matches(replacement)) errors.Add($"{label}: {catalog.FormLabel(value.Text)} does not belong to {category}.");
            if (sources.Any(source => !source.Type.Equals(replacement.Type, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"{label}: the replacement must have the same form type as every source item ({string.Join(", ", sources.Select(source => source.Type).Distinct())}).");
            if (sources.Contains(replacement)) errors.Add($"{label}: a later stage cannot reuse the source item.");
            if (replacements.TryGetValue(replacement, out var previous) && !previous.Equals(value.Text, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{label}: this form is already used by another stage, under a different identifier.");
            replacements.TryAdd(replacement, value.Text);
        }
        return errors;
    }

    private static bool HasUnsetArgument(PresetValue value, HashSet<string> templates)
    {
        if (value.IsUnsetArgument) return true;
        if (value.Kind == ValueKind.Scalar && value.Tag != "tag:yaml.org,2002:str" &&
            TemplateCall.TryParse(value.Text, out var name, out var arguments) && templates.Contains(name) &&
            arguments.Any(argument => HasUnsetArgument(argument, templates))) return true;
        return value.Fields.Values.Concat(value.Items).Any(child => HasUnsetArgument(child, templates));
    }

}

