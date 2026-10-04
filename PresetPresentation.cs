using System.Globalization;

namespace AoTPresetEditor;

// Describes the authored model and the native expander's result without changing either.
public sealed class PresetPresentation
{
    private PresetDocument document;
    private readonly IReadOnlyList<FormGroupDefinition> installedGroups;
    private readonly Dictionary<string, FormRow> forms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FormGroupDefinition> groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PresetValue, PresetValue> effective = [];
    private readonly Dictionary<string, HashSet<string>> dependencies = new(StringComparer.Ordinal);

    public PresetPresentation(PresetDocument document, IReadOnlyList<FormRow> forms,
        IReadOnlyList<FormGroupDefinition>? groups = null, string? expandedYaml = null)
    {
        this.document = document;
        installedGroups = groups ?? [];
        foreach (var form in forms)
        {
            this.forms.TryAdd(NormalizeIdentifier(form.Identifier), form);
            if (form.EditorId.Length != 0) this.forms.TryAdd(form.EditorId, form);
        }
        Update(document, expandedYaml);
    }

    public void Update(PresetDocument document, string? expandedYaml = null)
    {
        this.document = document;
        groups.Clear();
        foreach (var group in installedGroups.Concat(document.PendingGroups)) groups[group.Name] = group;
        effective.Clear();
        dependencies.Clear();
        IndexDependencies();
        if (!string.IsNullOrWhiteSpace(expandedYaml)) LinkExpanded(PresetDocument.Parse(expandedYaml, document.Category, document.Kind));
    }

    public PresetValue? EffectiveValue(PresetBlock block) => EffectiveValue(block.Value);
    public PresetValue? EffectiveValue(string blockId) => document.Blocks.FirstOrDefault(block => block.Id == blockId) is { } block ? EffectiveValue(block) : null;
    public PresetValue? EffectiveValue(PresetValue value) => effective.GetValueOrDefault(value);

    public FormRow? ResolveForm(string identifier, bool allowGroups = false) => allowGroups && groups.ContainsKey(identifier)
        ? null : forms.GetValueOrDefault(NormalizeIdentifier(identifier)) ?? forms.GetValueOrDefault(identifier);

    public IReadOnlyList<FormRow> SourceForms(PresetValue rule)
    {
        var owners = rule.Get("owners");
        var selection = owners ?? rule.Get("forms");
        if (selection is null) return [];
        var result = new HashSet<FormRow>();
        foreach (var value in selection.Kind == ValueKind.Sequence ? selection.Items : [selection])
        {
            if (value.Kind != ValueKind.Scalar || value.IsNull) continue;
            if (owners is null && groups.TryGetValue(value.Text, out var group))
            {
                foreach (var member in group.Members)
                    if (ResolveForm(member) is { } form) result.Add(form);
            }
            else if (ResolveForm(value.Text) is { } form) result.Add(form);
        }
        return result.ToArray();
    }

    public string FormLabel(string identifier, bool includeIdentifier = false)
    {
        if (groups.TryGetValue(identifier, out var group)) return $"{group.Name} ({group.Members.Count} forms)";
        if (ResolveForm(identifier) is not { } form) return identifier;
        var name = form.Name.Length != 0 ? form.Name : form.EditorId.Length != 0 ? form.EditorId : identifier;
        return includeIdentifier && name != identifier ? $"{name} ({identifier})" : name;
    }

    public string RuleSummary(PresetBlock block, bool behaviorMode = false) => Summary(EffectiveValue(block) ?? block.Value, block.Context, behaviorMode);

    public string ReferenceLabel(PresetValue value, bool behaviorMode = false)
    {
        if (value.Kind == ValueKind.Alias)
            return (behaviorMode ? "Common settings: " : "Shared: ") + (document.Blocks.FirstOrDefault(block => PresetDocument.Descendants(block.Value)
                .Any(node => node.Anchor == value.Text))?.Name ?? value.Text);
        if (IsCall(value, out var name, out _))
            return (behaviorMode ? "Behavior: " : "Template: ") + (document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Template && block.Key == name)?.Name ?? name);
        if (value.Kind == ValueKind.Sequence) return string.Join(", ", value.Items.Select(item => ReferenceLabel(item, behaviorMode)).Where(label => label.Length != 0));
        return "";
    }

    public IReadOnlyList<PresetBlock> Uses(PresetBlock definition) => document.Blocks
        .Where(block => block.Id != definition.Id && DependsOn(block.Id, definition.Id, []))
        .ToArray();

    public string Summary(PresetValue value, string context = "", bool behaviorMode = false, bool parametersAllowed = false)
    {
        if (value.IsUnsetArgument) return "Choose a value";
        if (value.Kind == ValueKind.Alias) return ReferenceLabel(value, behaviorMode);
        if (value.Kind == ValueKind.Scalar)
        {
            if (IsCall(value, out _, out var arguments))
                return ReferenceLabel(value, behaviorMode) + (arguments.Count == 0 ? "" : " · " + ListLabels(arguments, behaviorMode, parametersAllowed));
            return value.IsNull ? "Empty" : FormLabel(ValueText(value, behaviorMode, parametersAllowed));
        }
        if (value.Kind == ValueKind.Sequence)
        {
            var noun = context.ToLowerInvariant() switch
            {
                "stages" => "stages", "transformers" => "transformations", "timemodulators" or "modulators" => "time modifiers",
                "effects" or "magiceffects" => "effects", "rules" => "rules", _ => "entries"
            };
            return value.Items.Count == 0 ? "No " + noun : $"{value.Items.Count} {noun}: " + ListLabels(value.Items, behaviorMode, parametersAllowed);
        }
        var normalized = PresetSchema.NormalizeContext(context.Length == 0 ? PresetSchema.InferContext("", value) : context);
        return normalized switch
        {
            "rule" => SummarizeRule(value, behaviorMode, parametersAllowed),
            "stage" => Join("Stage " + (value.Get("no") is { } number ? ValueText(number, behaviorMode, parametersAllowed) : "?"),
                value.Get("name") is { } name ? ValueText(name, behaviorMode, parametersAllowed) : "",
                Duration(value.Get("duration"), "game hour", behaviorMode, parametersAllowed)),
            "transformer" => Join(value.Get("FormEditorID") is { } trigger ? "Trigger: " + ListLabels(trigger, behaviorMode, parametersAllowed) : "Choose a trigger",
                value.Get("finalFormEditorID") is { } result ? "Produces " + ListLabels(result, behaviorMode, parametersAllowed) : "Choose a result item",
                Duration(value.Get("duration"), "game hour", behaviorMode, parametersAllowed)),
            "modulator" => Join(value.Get("FormEditorID") is { } trigger ? "Trigger: " + ListLabels(trigger, behaviorMode, parametersAllowed) : "Choose a trigger",
                value.Get("magnitude") is { } multiplier ? multiplier.Text == "0" ? "Progress paused" : "Time multiplier: " + ValueText(multiplier, behaviorMode, parametersAllowed) : "Choose a time multiplier"),
            "effect" => SummarizeEffect(value, behaviorMode, parametersAllowed),
            _ => value.Fields.Count == 0 ? "No settings" : string.Join(", ", value.Fields.Keys.Take(4)) + (value.Fields.Count > 4 ? $" +{value.Fields.Count - 4}" : "")
        };
    }

    private string SummarizeEffect(PresetValue value, bool behaviorMode, bool parametersAllowed)
    {
        var magnitudeField = PresetSchema.GetFields("effect", document.Kind).Single(field => field.Key == "magnitude");
        if (PresetSchema.InactiveReason(magnitudeField, value, "effect", document.Category) is not null)
            return "Zeroes the existing effect slot";
        return Join(value.Get("FormEditorID") is { IsNull: false } effect ? ListLabels(effect, behaviorMode, parametersAllowed) : "Inherited effect",
            value.Get("magnitude") is { } magnitude ? "Magnitude " + ValueText(magnitude, behaviorMode, parametersAllowed) : "",
            Duration(value.Get("duration"), "second", behaviorMode, parametersAllowed));
    }

    private string SummarizeRule(PresetValue value, bool behaviorMode, bool parametersAllowed)
    {
        var items = value.Get("owners") ?? value.Get("forms");
        var target = value.Get("finalFormEditorID");
        var transformers = value.Get("transformers");
        var results = transformers?.Kind == ValueKind.Sequence
            ? transformers.Items.Select(item => item.Get("finalFormEditorID")).OfType<PresetValue>()
                .Select(item => ListLabels(item, behaviorMode, parametersAllowed)).Distinct(StringComparer.Ordinal).ToArray() : [];
        var label = items is null ? document.Kind == PresetKind.Default ? "Default item progression" : "Choose affected items" : ListLabels(items, behaviorMode, parametersAllowed);
        if (target is not null) label += " → " + ListLabels(target, behaviorMode, parametersAllowed);
        else if (results.Length == 1) label += " → " + results[0];
        return Join(label, Count(value.Get("stages"), "stage", "stages"), Count(transformers, "transformation", "transformations"),
            Count(value.Get("timeModulators"), "time modifier", "time modifiers"));
    }

    private string ListLabels(PresetValue value, bool behaviorMode, bool parametersAllowed) => value.Kind == ValueKind.Sequence
        ? ListLabels(value.Items, behaviorMode, parametersAllowed) : Summary(value, behaviorMode: behaviorMode, parametersAllowed: parametersAllowed);
    private string ListLabels(IReadOnlyList<PresetValue> values, bool behaviorMode, bool parametersAllowed) =>
        string.Join(", ", values.Take(3).Select(value => Summary(value, behaviorMode: behaviorMode, parametersAllowed: parametersAllowed))) +
        (values.Count > 3 ? $" +{values.Count - 3}" : "");
    private static string Count(PresetValue? value, string singular, string plural) => value?.Kind == ValueKind.Sequence
        ? value.Items.Count == 0 ? "" : $"{value.Items.Count} {(value.Items.Count == 1 ? singular : plural)}" : "";
    private static string Duration(PresetValue? value, string unit, bool behaviorMode, bool parametersAllowed)
    {
        if (value is null) return "";
        if (behaviorMode && parametersAllowed && value.Text.StartsWith('$') && TemplateCall.IsName(value.Text[1..])) return "Varies per item";
        if (value.Text.Equals(".inf", StringComparison.OrdinalIgnoreCase)) return "No time limit";
        var singular = double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == 1;
        return value.Text + " " + unit + (singular ? "" : "s");
    }

    private static string ValueText(PresetValue value, bool behaviorMode, bool parametersAllowed)
    {
        if (!behaviorMode || !parametersAllowed) return value.Text;
        if (value.Text.StartsWith("$$", StringComparison.Ordinal)) return value.Text[1..];
        return value.Text.StartsWith('$') && TemplateCall.IsName(value.Text[1..]) ? "Varies per item" : value.Text;
    }
    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(part => part.Length != 0));
    private static bool IsCall(PresetValue value, out string name, out List<PresetValue> arguments)
    {
        name = ""; arguments = [];
        return value.Kind == ValueKind.Scalar && value.Tag != "tag:yaml.org,2002:str" && TemplateCall.TryParse(value.Text, out name, out arguments);
    }

    private void LinkExpanded(PresetDocument expanded)
    {
        var originals = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        var rules = expanded.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        if (originals.Length == 1 && originals[0].Key is "ownerLists" or "formsLists")
        {
            var list = PresetValue.List(); list.Items.AddRange(rules.Select(rule => rule.Value)); Link(originals[0].Value, list);
        }
        else if (originals.Length == rules.Length)
            for (var index = 0; index < originals.Length; ++index) Link(originals[index].Value, rules[index].Value);
        foreach (var block in document.Blocks.Where(block => block.Kind == BlockKind.Shared))
            if (expanded.Blocks.FirstOrDefault(item => item.Kind == BlockKind.Shared && item.Key == block.Key) is { } result) Link(block.Value, result.Value);
    }

    private void Link(PresetValue authored, PresetValue expanded)
    {
        effective[authored] = expanded;
        foreach (var (key, value) in authored.Fields)
            if (expanded.Get(key) is { } field) Link(value, field);
        if (authored.Kind == ValueKind.Sequence && expanded.Kind == ValueKind.Sequence && authored.Items.Count == expanded.Items.Count)
            for (var index = 0; index < authored.Items.Count; ++index) Link(authored.Items[index], expanded.Items[index]);
    }

    private void IndexDependencies()
    {
        var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in document.Blocks.Where(block => block.Kind == BlockKind.Template)) templates.TryAdd(block.Key, block.Id);
        foreach (var block in document.Blocks)
            foreach (var value in PresetDocument.Descendants(block.Value))
                if (value.Anchor.Length != 0) anchors.TryAdd(value.Anchor, block.Id);
        foreach (var block in document.Blocks)
        {
            var targets = dependencies[block.Id] = [];
            CollectReferences(block.Value, targets, anchors, templates);
        }
    }

    private static void CollectReferences(PresetValue value, HashSet<string> targets, Dictionary<string, string> anchors, Dictionary<string, string> templates)
    {
        if (value.Kind == ValueKind.Alias && anchors.TryGetValue(value.Text, out var shared)) targets.Add(shared);
        if (IsCall(value, out var name, out var arguments))
        {
            if (templates.TryGetValue(name, out var template)) targets.Add(template);
            foreach (var argument in arguments) CollectReferences(argument, targets, anchors, templates);
        }
        foreach (var child in value.Fields.Values.Concat(value.Items)) CollectReferences(child, targets, anchors, templates);
    }

    private bool DependsOn(string source, string target, HashSet<string> visited)
    {
        if (!visited.Add(source) || !dependencies.TryGetValue(source, out var references)) return false;
        return references.Contains(target) || references.Any(reference => DependsOn(reference, target, visited));
    }

    internal static string NormalizeIdentifier(string identifier)
    {
        var parts = identifier.Split('~');
        if (parts.Length > 2) return identifier;
        var number = parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[0][2..] : parts[0];
        if (parts.Length == 1 && !parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) && number.Length is not (7 or 8)) return identifier;
        if (!uint.TryParse(number, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id)) return identifier;
        // Full IDs outside Skyrim.esm depend on the current load order, which this catalog does not record.
        const uint maximumMasterLocalId = 0xFFFFFF;
        if (parts.Length == 1 && id > maximumMasterLocalId) return identifier;
        return $"0x{id:X}~{(parts.Length == 2 ? parts[1] : "Skyrim.esm")}";
    }
}
