namespace AoTPresetEditor;

public sealed record PresetValidationResult(string Source, string Expanded,
    Dictionary<string, IReadOnlyList<string>> Errors, IReadOnlyList<string> FileErrors)
{
    public bool IsValid => FileErrors.Count == 0 && Errors.Values.All(errors => errors.Count == 0);
}

public static class PresetValidation
{
    public static IReadOnlyList<string> GroupErrors(PresetDocument document)
    {
        var errors = document.PendingGroups.SelectMany(group => FormGroupCatalog.Validate(group)
            .Select(error => group.Name + ": " + error)).ToList();
        if (document.PendingGroups.Select(group => group.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.PendingGroups.Count)
            errors.Add("Each new form group needs a different name.");
        return errors;
    }

    public static async Task<PresetValidationResult> Check(PresetDocument document, CancellationToken token, PresetPresentation? catalog = null)
    {
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var fileErrors = new List<string>();
        var source = "";
        var expanded = "";
        var templateNames = document.Blocks.Where(block => block.Kind == BlockKind.Template).Select(block => block.Key).ToHashSet(StringComparer.Ordinal);
        var rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        if (rules.Length == 0 && !document.Blocks.Any(block => block.IsBehavior)) fileErrors.Add("Add at least one rule.");
        if (document.Kind == PresetKind.Default && rules.Length > 1) fileErrors.Add("A default preset can contain one rule.");
        if (document.Kind == PresetKind.Other) fileErrors.Add("Choose Default, Custom, or Addon as the preset kind.");
        if (document.Blocks.Select(block => block.Id).Distinct(StringComparer.Ordinal).Count() != document.Blocks.Count)
            fileErrors.Add("Two blocks have the same editor identifier. Reopen a copy of the preset before editing.");
        fileErrors.AddRange(GroupErrors(document));
        foreach (var block in document.Blocks)
        {
            var blockErrors = new List<string>();
            if (block.IsBehavior && BehaviorFamily.Find(document, block.Id) is { Rows.Count: 0 })
                blockErrors.Add("Add a row in Variables.");
            if (string.IsNullOrWhiteSpace(block.Name)) blockErrors.Add("Give this block a name.");
            if (HasUnsetArgument(block.Value, templateNames)) blockErrors.Add("Fill the missing values in Variables for this row.");
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
                var block = rules[0];
                for (var index = 0; index < expandedRules.Length; index++)
                    errors[block.Id] = [.. errors[block.Id], .. ValidateRule(expandedRules[index].Value, document.Category, document.Kind, catalog)
                        .Select(error => $"Entry {index + 1}: {error}")];
                if (expandedRules.Length == 0) errors[block.Id] = [.. errors[block.Id], "The rule list is empty."];
            }
            else
            {
                if (expandedRules.Length != rules.Length)
                    fileErrors.Add("Template expansion changed the number of top-level rules.");
                for (var index = 0; index < Math.Min(rules.Length, expandedRules.Length); index++)
                {
                    token.ThrowIfCancellationRequested();
                    var block = rules[index];
                    errors[block.Id] = [.. errors[block.Id], .. ValidateRule(expandedRules[index].Value, document.Category, document.Kind, catalog)];
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

    private static IReadOnlyList<string> ValidateRule(PresetValue rule, string category, PresetKind kind, PresetPresentation? catalog)
    {
        var errors = PresetSchema.Validate(rule, PresetDocument.RuleContext(kind), category, kind).ToList();
        if (errors.Count == 0 && kind == PresetKind.Addon && !HasEffectiveAddonSettings(rule, catalog))
            errors.Add("No effective settings: add a container filter, transformation, or time modifier.");
        if (catalog is not null) errors.AddRange(KnownFormErrors(rule, category, kind, catalog));
        return errors;
    }

    private static bool HasEffectiveAddonSettings(PresetValue rule, PresetPresentation? catalog)
    {
        if (HasConfiguredForms(rule.Get("containers"), catalog)) return true;
        var triggerSelection = new FormSelection(TriggerOnly: true);
        foreach (var key in new[] { "transformers", "timeModulators" })
            if (rule.Get(key) is { Kind: ValueKind.Sequence } triggers &&
                triggers.Items.Any(trigger => HasConfiguredForms(trigger.Get("FormEditorID"), catalog, triggerSelection))) return true;
        return false;
    }

    private static bool HasConfiguredForms(PresetValue? value, PresetPresentation? catalog, FormSelection? selection = null)
    {
        if (value is null || value.IsNull) return false;
        foreach (var item in value.Kind == ValueKind.Sequence ? value.Items : [value])
        {
            if (item.Kind != ValueKind.Scalar || item.IsNull || string.IsNullOrWhiteSpace(item.Text)) continue;
            if (catalog?.ResolveGroup(item.Text) is { } group)
            {
                if (group.Members.Any(member => catalog.ResolveForm(member) is not null && HasApplicableForm(member, catalog, selection))) return true;
            }
            else if (!PresetSchema.IsZeroForm(item) && HasApplicableForm(item.Text, catalog, selection)) return true;
        }
        return false;
    }

    private static bool HasApplicableForm(string identifier, PresetPresentation? catalog, FormSelection? selection)
    {
        if (catalog?.ResolveForm(identifier) is not { } form) return true;
        if (selection is null || form.FormListMembers is null) return selection?.Matches(form) ?? true;
        return FormCatalog.ExpandFormList(form, id => catalog.ResolveForm(id))
            .Any(selection.Matches);
    }

    private static void CheckTriggerFormList(string identifier, PresetPresentation catalog, FormSelection selection, string label, List<string> errors)
    {
        if (catalog.ResolveForm(identifier) is not { FormListMembers: not null } list) return;
        var members = FormCatalog.ExpandFormList(list, id => catalog.ResolveForm(id));
        if (!members.Any(selection.Matches))
            errors.Add($"{label}: form list '{catalog.FormLabel(identifier)}' has no compatible triggers in loaded game data.");
        foreach (var member in members.DistinctBy(member => member.Identifier))
            if (!selection.Matches(member))
                errors.Add($"{label}: form list '{catalog.FormLabel(identifier)}' contains {member.EditorId} ({member.Identifier}, {member.Type}), which cannot be a trigger.");
    }

    private static IReadOnlyList<string> KnownFormErrors(PresetValue rule, string category, PresetKind kind, PresetPresentation catalog)
    {
        var errors = new List<string>();
        CheckKnownFields(rule, "rule", category, kind, catalog, "", errors);
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

    private static void CheckKnownFields(PresetValue value, string context, string category, PresetKind kind,
        PresetPresentation catalog, string path, List<string> errors)
    {
        foreach (var field in PresetSchema.GetFields(context, kind))
        {
            if (value.Get(field.Key) is not { IsNull: false } child ||
                PresetSchema.InactiveReason(field, value, context, category) is not null) continue;
            var label = path.Length == 0 ? field.Label : path + ": " + field.Label;
            if (field.Kind == FieldKind.ObjectList && child.Kind == ValueKind.Sequence)
            {
                for (var index = 0; index < child.Items.Count; index++)
                    CheckKnownFields(child.Items[index], field.ChildContext, category, kind, catalog, $"{label} {index + 1}", errors);
                continue;
            }
            if (field.Kind is not (FieldKind.Form or FieldKind.FormList) || field.MatchSourceType) continue;
            var selection = FormSelection.ForField(field, category);
            foreach (var item in child.Kind == ValueKind.Sequence ? child.Items : [child])
            {
                if (item.Kind != ValueKind.Scalar || item.IsNull) continue;
                if (field.AllowsGroups && catalog.ResolveGroup(item.Text) is { } group)
                {
                    if (selection.RequiresKnownGroupMember && !group.Members.Any(member => catalog.ResolveForm(member) is { } memberForm && selection.Matches(memberForm)))
                        errors.Add($"{label}: group '{group.Name}' has no compatible {category} source items in loaded game data.");
                    if (field.TriggerOnly)
                        foreach (var member in group.Members) CheckTriggerFormList(member, catalog, selection, label, errors);
                    continue;
                }
                if (field.TriggerOnly) CheckTriggerFormList(item.Text, catalog, selection, label, errors);
                // Custom owner text can match item names even when the same text is another form's EditorID.
                if (field.Key == "owners" && !PresetPresentation.NormalizeIdentifier(item.Text).Contains('~')) continue;
                if (catalog.ResolveForm(item.Text) is not { } form || selection.Matches(form)) continue;
                var name = catalog.FormLabel(item.Text);
                var reason = field.RequiresEditorId && form.EditorId.Length == 0
                    ? $"{name} has no EditorID; AoT cannot use it as a source item."
                    : field.BoundObjectOnly ? $"{name} is {form.Type}, not a bound object that AoT can produce."
                    : field.MatchPresetCategory ? $"{name} does not belong to {category}."
                    : field.TriggerOnly ? $"{name} is {form.Type}; a trigger must be a bound object, location, perk, effect shader, or form list."
                    : $"{name} is {form.Type}; this setting requires {string.Join(" or ", field.FormTypes ?? [])}.";
                errors.Add($"{label}: {reason}");
            }
        }
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

