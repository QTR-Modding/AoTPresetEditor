namespace AoTPresetEditor;

internal static class EffectPreviewTargets
{
    internal static IReadOnlyList<FormRow> ForPath(PresetDocument document, PresetPresentation presentation,
        PresetValue rule, IReadOnlyList<string> path, IReadOnlyList<FormRow> forms)
    {
        var sources = SourceForms(document, presentation, rule, forms);
        var stages = BehaviorFamily.ResolveValue(document, rule, "stages");
        stages = stages is null ? null : BehaviorFamily.ResolveValue(document, stages);
        if (path.Count >= 2 && path[0] == "stages" && int.TryParse(path[1], out var index) &&
            stages is { Kind: ValueKind.Sequence } && index >= 0 && index < stages.Items.Count)
            return StageForms(document, presentation, stages.Items[index], sources);
        if (path.Count < 2 || path[0] is not ("transformers" or "timeModulators") ||
            stages is not { Kind: ValueKind.Sequence, Items.Count: > 0 }) return sources;
        var trigger = ValueAtPath(document, rule, path.Take(2).ToArray());
        var allowed = trigger is null ? null : BehaviorFamily.ResolveValue(document, trigger, "allowed_stages");
        allowed = allowed is null ? null : BehaviorFamily.ResolveValue(document, allowed);
        IEnumerable<PresetValue> allowedValues = allowed?.Kind == ValueKind.Sequence ? allowed.Items : allowed is null ? [] : [allowed];
        var numbers = allowedValues
            .Select(value => BehaviorFamily.ResolveValue(document, value)).OfType<PresetValue>()
            .Where(value => PresetSchema.TryUnsigned(value.Text, out _))
            .Select(value => { PresetSchema.TryUnsigned(value.Text, out var number); return number; }).ToHashSet();
        return stages.Items.Where(stage => numbers.Count == 0 ||
                BehaviorFamily.ResolveValue(document, stage, "no") is { } number &&
                BehaviorFamily.ResolveValue(document, number) is { } resolved &&
                PresetSchema.TryUnsigned(resolved.Text, out var parsed) && numbers.Contains(parsed))
            .SelectMany(stage => StageForms(document, presentation, stage, sources)).Distinct().ToArray();
    }

    internal static IReadOnlyList<FormRow> ForParameter(PresetDocument document, PresetPresentation presentation,
        string rowId, string parameter, IReadOnlyList<string> nestedPath, IReadOnlyList<FormRow> forms)
    {
        var row = document.Blocks.FirstOrDefault(block => block.Id == rowId);
        return row is null ? [] : ParameterPaths(document, rowId, parameter, nestedPath)
            .SelectMany(path => ForPath(document, presentation, row.Value, path, forms)).Distinct().ToArray();
    }

    internal static bool CanChooseForParameter(PresetDocument document, string rowId, string parameter, IReadOnlyList<string> nestedPath)
    {
        var row = document.Blocks.FirstOrDefault(block => block.Id == rowId);
        if (row is null) return false;
        return ParameterPaths(document, rowId, parameter, nestedPath).Any(path =>
        {
            if (path.Count < 2 || path[0] != "stages") return true;
            var stage = ValueAtPath(document, row.Value, path.Take(2).ToArray());
            if (stage is null) return false;
            var item = StageItem(document, stage);
            return item is null || item.IsNull || item.Kind == ValueKind.Scalar && string.IsNullOrWhiteSpace(item.Text);
        });
    }

    private static IEnumerable<IReadOnlyList<string>> ParameterPaths(PresetDocument document, string rowId, string parameter,
        IReadOnlyList<string> nestedPath)
    {
        var family = BehaviorFamily.Find(document, rowId);
        var row = document.Blocks.FirstOrDefault(block => block.Id == rowId);
        return family is null || row is null ? [] : Paths(document, row.Value, family.Definition.Context).Where(path =>
        {
            var use = ValueEditorControl.IssueParameter(document, family.Definition.Value, path);
            return use.Parameter == parameter && path.Skip(use.PathLength).SequenceEqual(nestedPath);
        });
    }

    internal static IReadOnlyList<FormRow> StageForms(PresetDocument document, PresetPresentation presentation,
        PresetValue stage, IReadOnlyList<FormRow> sources)
    {
        var item = StageItem(document, stage);
        if (item is null || item.IsNull || item.Kind == ValueKind.Scalar && string.IsNullOrWhiteSpace(item.Text)) return sources;
        var number = BehaviorFamily.ResolveValue(document, stage, "no");
        if (number is null || BehaviorFamily.ResolveValue(document, number) is not { } resolved ||
            !PresetSchema.TryUnsigned(resolved.Text, out _)) return [];
        return item.Kind == ValueKind.Scalar && presentation.ResolveForm(item.Text) is { } form ? [form] : [];
    }

    internal static PresetValue? StageItem(PresetDocument document, PresetValue stage)
    {
        var number = BehaviorFamily.ResolveValue(document, stage, "no");
        if (number is not null && BehaviorFamily.ResolveValue(document, number) is { } resolved &&
            PresetSchema.TryUnsigned(resolved.Text, out var parsed) && parsed == 0) return null;
        var item = BehaviorFamily.ResolveValue(document, stage, "FormEditorID");
        return item is null ? null : BehaviorFamily.ResolveValue(document, item);
    }

    internal static IEnumerable<IReadOnlyList<string>> Paths(PresetDocument document, PresetValue value, string context) =>
        Paths(document, value, context, [], []);

    private static IEnumerable<IReadOnlyList<string>> Paths(PresetDocument document, PresetValue value, string context,
        IReadOnlyList<string> path, HashSet<PresetValue> active)
    {
        var resolved = BehaviorFamily.ResolveValue(document, value);
        if (resolved is null || !active.Add(resolved)) yield break;
        try
        {
            if (resolved.Kind == ValueKind.Sequence)
            {
                var childContext = PresetSchema.NormalizeContext(context) switch
                { "stages" => "stage", "transformers" => "transformer", "modulators" or "timemodulators" => "modulator", _ => context };
                for (var index = 0; index < resolved.Items.Count; index++)
                    foreach (var nested in Paths(document, resolved.Items[index], childContext, path.Append(index.ToString()).ToArray(), active))
                        yield return nested;
                yield break;
            }
            foreach (var descriptor in PresetSchema.GetFields(context, document.Kind))
            {
                var child = BehaviorFamily.ResolveValue(document, resolved, descriptor.Key);
                if (child is null) continue;
                var childPath = path.Append(descriptor.Key).ToArray();
                if (descriptor.Kind is FieldKind.Form or FieldKind.FormList)
                {
                    yield return childPath;
                    if (descriptor.Kind == FieldKind.FormList && BehaviorFamily.ResolveValue(document, child) is { Kind: ValueKind.Sequence } values)
                        for (var index = 0; index < values.Items.Count; index++) yield return childPath.Append(index.ToString()).ToArray();
                }
                else if (descriptor.Kind == FieldKind.ObjectList &&
                    BehaviorFamily.ResolveValue(document, child) is { Kind: ValueKind.Sequence } list)
                    for (var index = 0; index < list.Items.Count; index++)
                        foreach (var nested in Paths(document, list.Items[index], descriptor.ChildContext,
                            childPath.Append(index.ToString()).ToArray(), active)) yield return nested;
            }
        }
        finally { active.Remove(resolved); }
    }

    private static PresetValue? ValueAtPath(PresetDocument document, PresetValue value, IReadOnlyList<string> path)
    {
        PresetValue? current = value;
        foreach (var part in path)
        {
            current = current is null ? null : BehaviorFamily.ResolveValue(document, current);
            current = current?.Kind == ValueKind.Mapping ? BehaviorFamily.ResolveValue(document, current, part)
                : current is { Kind: ValueKind.Sequence } && int.TryParse(part, out var index) && index >= 0 && index < current.Items.Count
                    ? current.Items[index] : null;
        }
        return current;
    }

    private static IReadOnlyList<FormRow> SourceForms(PresetDocument document, PresetPresentation presentation,
        PresetValue rule, IReadOnlyList<FormRow> forms)
    {
        var result = ValueEditorControl.RuleSourceForms(document, presentation, rule).ToHashSet();
        var owners = BehaviorFamily.ResolveValue(document, rule, "owners");
        owners = owners is null ? null : BehaviorFamily.ResolveValue(document, owners);
        if (owners is null) return result.ToArray();
        var selectors = (owners.Kind == ValueKind.Sequence ? owners.Items : [owners])
            .Select(value => BehaviorFamily.ResolveValue(document, value)).OfType<PresetValue>()
            .Where(value => value.Kind == ValueKind.Scalar && !value.IsNull && !string.IsNullOrWhiteSpace(value.Text)).Select(value => value.Text).ToArray();
        var category = new FormSelection(Category: document.Category);
        result.UnionWith(forms.Where(form => category.Matches(form) && selectors.Any(selector => presentation.MatchesOwner(selector, form))));
        return result.ToArray();
    }
}
