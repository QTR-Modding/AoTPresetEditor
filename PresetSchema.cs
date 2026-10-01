using System.Globalization;

namespace AoTPresetEditor;

public enum FieldKind { Text, Form, FormList, Number, Integer, IntegerList, Boolean, Color, ObjectList }

public sealed record PresetField(string Key, string Label, FieldKind Kind, string Help,
    bool Required = false, string ChildContext = "", string[]? FormTypes = null, string? DefaultText = null,
    bool AllowsGroups = false, bool MatchPresetCategory = false, bool TriggerOnly = false);

public static class PresetSchema
{
    private static readonly string[] ItemTypes = ["Ingestible", "Ingredient", "Armor", "Weapon", "Scroll", "Book", "SoulGem", "MiscItem"];
    private static readonly PresetField[] VisualFields =
    [
        new("color", "World tint", FieldKind.Color, "RGB or RGBA hexadecimal color for items lying in the world. Zero removes tint."),
        new("sound", "World sound", FieldKind.Form, "Sound played by items lying in the world.", FormTypes: ["SoundDescriptor"]),
        new("art_object", "World art object", FieldKind.Form, "Art effect attached to items lying in the world.", FormTypes: ["ArtObject"]),
        new("effect_shader", "World effect shader", FieldKind.Form, "Shader applied to items lying in the world.", FormTypes: ["EffectShader"])
    ];
    private static readonly PresetField[] TriggerFields =
    [
        new("allowed_stages", "Allowed stages", FieldKind.IntegerList, "Stage numbers this trigger affects. Unset or empty means all stages; 0 selects the initial stage."),
        new("containers", "Inventory filter", FieldKind.FormList, "Inventory owner's base forms where this trigger can apply. Unset or empty allows all inventories. World items ignore this filter.", AllowsGroups: true)
    ];
    private static readonly PresetField[] StageFields =
    [
        new("no", "Stage number", FieldKind.Integer, "Start at 0 and increase by one for each stage.", true, DefaultText: "0"),
        new("name", "Stage name", FieldKind.Text, "Added to the item name for generated FOOD or MISC stages. An empty name leaves the item name unchanged.", true, DefaultText: ""),
        new("duration", "Duration (game hours)", FieldKind.Number, "How long this stage lasts. Use a positive number or .inf for an infinite stage.", true, DefaultText: "1"),
        new("FormEditorID", "Stage item", FieldKind.Form, "Stage 0 always uses the source item. Later FOOD or MISC stages may leave this unset to generate a derivative. Other item types need a replacement item.", FormTypes: ItemTypes, MatchPresetCategory: true),
        new("value", "Value override", FieldKind.Integer, "Value of a generated stage item. Unset or a negative number keeps its original value. Zero is a valid override."),
        new("weight", "Weight override", FieldKind.Number, "Weight of a generated stage item. Unset or a negative number keeps its original weight. Zero is a valid override."),
        new("crafting_allowed", "Allow crafting", FieldKind.Boolean, "Allows this stage item to stand in for its source item in crafting menus. Defaults to false.", DefaultText: "false"),
        new("mgeffect", "Magic effects", FieldKind.ObjectList, "Replaces effects on generated FOOD stages. An unset or empty list keeps original effects. Add an entry with no magic effect to clear an existing effect.", ChildContext: "effect"),
        .. VisualFields
    ];
    private static readonly PresetField[] ModulatorFields =
    [
        new("FormEditorID", "Trigger", FieldKind.FormList, "Nearby object, item in the same inventory, location, or perk conditions. A form group can supply several triggers.", true, AllowsGroups: true, TriggerOnly: true),
        new("magnitude", "Time multiplier", FieldKind.Number, "1 is normal speed, 0 pauses progression, and negative values reverse it. This does not change transformer duration.", true, DefaultText: "1"),
        .. TriggerFields,
        .. VisualFields
    ];
    private static readonly PresetField[] TransformerFields =
    [
        new("FormEditorID", "Trigger", FieldKind.FormList, "Nearby object, item in the same inventory, location, or perk conditions. A form group can supply several triggers.", true, AllowsGroups: true, TriggerOnly: true),
        new("finalFormEditorID", "Result item", FieldKind.Form, "The item produced when the transformation finishes.", true, FormTypes: ItemTypes),
        new("duration", "Duration (game hours)", FieldKind.Number, "Positive time needed to finish this transformation while its trigger applies.", true, DefaultText: "0.05"),
        .. TriggerFields,
        .. VisualFields
    ];
    private static readonly PresetField[] EffectFields =
    [
        new("FormEditorID", "Magic effect", FieldKind.Form, "Choose the effect to apply. Leave it unset to clear an existing effect.", FormTypes: ["MagicEffect"]),
        new("magnitude", "Magnitude", FieldKind.Number, "Strength of the chosen effect. Required when a magic effect is selected; zero is allowed.", DefaultText: "0"),
        new("duration", "Duration (seconds)", FieldKind.Integer, "Duration of the chosen effect in seconds. Required when an effect is selected; zero is allowed.", DefaultText: "0")
    ];
    private static readonly PresetField[] RuleFields =
    [
        new("stages", "Stages", FieldKind.ObjectList, "The sequence of stages, beginning with stage 0.", true, "stage"),
        new("finalFormEditorID", "Final result item", FieldKind.Form, "Item produced after the last stage. Required even if an infinite stage prevents reaching it.", true, FormTypes: ItemTypes),
        new("containers", "Evolution filter", FieldKind.FormList, "Inventory owner's base forms where evolution is allowed. For a world item, this checks the item's own base form. Unset or empty is unrestricted.", AllowsGroups: true),
        new("timeModulators", "Time modulators", FieldKind.ObjectList, "Changes stage progression while a trigger applies. The first matching modulator wins when no transformer matches.", ChildContext: "modulator"),
        new("transformers", "Transformers", FieldKind.ObjectList, "Triggered transformations, in priority order. The first matching transformer wins.", ChildContext: "transformer")
    ];
    private static readonly PresetField[] CustomFields =
    [
        new("owners", "Applies to", FieldKind.FormList, "Items selected by form identifier or words in their names. A scalar or a list is accepted.", true, FormTypes: ItemTypes, MatchPresetCategory: true),
        .. RuleFields
    ];
    private static readonly PresetField[] AddonFields =
    [
        new("forms", "Applies to", FieldKind.FormList, "Items selected by form identifiers or form groups. A scalar or a list is accepted.", true, FormTypes: ItemTypes, AllowsGroups: true, MatchPresetCategory: true),
        .. RuleFields.Where(field => field.Key is not ("stages" or "finalFormEditorID"))
    ];

    public static IReadOnlyList<PresetField> GetFields(string context, PresetKind kind)
    {
        kind = ContextKind(context, kind);
        return NormalizeContext(context) switch
        {
            "rule" => kind switch { PresetKind.Custom => CustomFields, PresetKind.Addon => AddonFields, _ => RuleFields },
            "stage" => StageFields,
            "modulator" => ModulatorFields,
            "transformer" => TransformerFields,
            "effect" => EffectFields,
            _ => []
        };
    }

    public static string InferContext(string key, PresetValue value)
    {
        if (key == "stages") return "stages";
        if (key == "timeModulators") return "modulators";
        if (key == "transformers") return "transformers";
        if (key == "mgeffect") return "effects";
        if (value.Kind == ValueKind.Sequence)
            return value.Items.Count == 0 ? "values" : InferContext("", value.Items[0]) + "s";
        if (value.Kind != ValueKind.Mapping) return "value";
        if (value.Fields.ContainsKey("no")) return "stage";
        if (value.Fields.ContainsKey("owners") || value.Fields.ContainsKey("forms") || value.Fields.ContainsKey("stages") ||
            value.Fields.ContainsKey("transformers") || value.Fields.ContainsKey("timeModulators")) return "rule";
        if (value.Fields.ContainsKey("finalFormEditorID")) return "transformer";
        if (value.Fields.ContainsKey("duration") && value.Fields.ContainsKey("magnitude")) return "effect";
        if (value.Fields.ContainsKey("duration")) return "transformer";
        if (value.Fields.ContainsKey("FormEditorID") || value.Fields.ContainsKey("magnitude")) return "modulator";
        return "rule";
    }

    public static PresetField? GetParameterField(PresetBlock template, string parameter, PresetDocument document) =>
        GetParameterField(template, parameter, document, []);

    private static PresetField? GetParameterField(PresetBlock template, string parameter, PresetDocument document,
        HashSet<(string, string)> activeTemplates)
    {
        var key = (template.Id, parameter);
        if (!activeTemplates.Add(key)) return null;
        try
        {
            var uses = new List<PresetField?>();
            FindParameterUses(template.Value, template.Context, null, parameter, document, uses, activeTemplates, []);
            return CombineParameterFields(parameter, uses);
        }
        finally { activeTemplates.Remove(key); }
    }

    private static void FindParameterUses(PresetValue value, string context, PresetField? field, string parameter,
        PresetDocument document, List<PresetField?> uses, HashSet<(string, string)> activeTemplates, HashSet<PresetValue> activeValues)
    {
        if (!activeValues.Add(value)) return;
        try
        {
            if (value.Kind == ValueKind.Alias)
            {
                var source = document.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value))
                    .FirstOrDefault(node => node.Anchor == value.Text);
                if (source is not null)
                {
                    var sourceContext = NormalizeContext(context) is "value" or "values" or "" ? InferContext("", source) : context;
                    FindParameterUses(source, sourceContext, field, parameter, document, uses, activeTemplates, activeValues);
                }
            }
            else if (value.Kind == ValueKind.Scalar)
            {
                if (value.Text == "$" + parameter) uses.Add(field);
                else if (value.Tag != "tag:yaml.org,2002:str" && TemplateCall.TryParse(value.Text, out var name, out var arguments))
                {
                    var called = document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Template && block.Key == name);
                    if (called is null) return;
                    var parameters = TemplateCall.FindParameters(called.Value, document);
                    for (var index = 0; index < arguments.Count; index++)
                    {
                        var argumentField = index < parameters.Count ? GetParameterField(called, parameters[index], document, activeTemplates) : null;
                        FindParameterUses(arguments[index], argumentField?.ChildContext ?? "value", argumentField,
                            parameter, document, uses, activeTemplates, activeValues);
                    }
                }
            }
            else if (value.Kind == ValueKind.Sequence)
            {
                var childContext = NormalizeContext(context) switch
                {
                    "stages" => "stage", "modulators" => "modulator", "transformers" => "transformer",
                    "effects" => "effect", "rules" => "rule", _ => field?.ChildContext ?? context
                };
                var childField = field?.Kind switch
                {
                    FieldKind.FormList => field with { Kind = FieldKind.Form },
                    FieldKind.IntegerList => field with { Kind = FieldKind.Integer },
                    FieldKind.ObjectList => null,
                    _ => field
                };
                foreach (var child in value.Items)
                    FindParameterUses(child, childContext, childField, parameter, document, uses, activeTemplates, activeValues);
            }
            else
            {
                var fields = GetFields(context, document.Kind);
                if (fields.Count == 0) fields = GetFields(InferContext("", value), document.Kind);
                foreach (var (name, child) in value.Fields)
                {
                    if (name == "<<") FindParameterUses(child, context, field, parameter, document, uses, activeTemplates, activeValues);
                    else
                    {
                        var childField = fields.FirstOrDefault(candidate => candidate.Key == name)
                            ?? VisualFields.FirstOrDefault(candidate => candidate.Key == name);
                        FindParameterUses(child, childField?.ChildContext ?? InferContext(name, child), childField,
                            parameter, document, uses, activeTemplates, activeValues);
                    }
                }
            }
        }
        finally { activeValues.Remove(value); }
    }

    private static PresetField? CombineParameterFields(string parameter, IReadOnlyList<PresetField?> uses)
    {
        var known = uses.OfType<PresetField>().ToArray();
        if (known.Length == 0) return null;
        var constrained = known.Where(field => field.Kind != FieldKind.Text).ToArray();
        if (constrained.Length == 0) constrained = known;
        var first = constrained[0];
        var kind = first.Kind;
        if (constrained.All(field => field.Kind is FieldKind.Form or FieldKind.FormList))
            kind = constrained.Any(field => field.Kind == FieldKind.Form) ? FieldKind.Form : FieldKind.FormList;
        else if (constrained.All(field => field.Kind is FieldKind.Integer or FieldKind.Number))
            kind = constrained.Any(field => field.Kind == FieldKind.Integer) ? FieldKind.Integer : FieldKind.Number;
        else if (constrained.Any(field => field.Kind != kind)) return null;
        string[]? types = null;
        foreach (var field in constrained.Where(field => field.FormTypes is not null))
            types = types is null ? field.FormTypes : types.Intersect(field.FormTypes!, StringComparer.OrdinalIgnoreCase).ToArray();
        return first with
        {
            Key = parameter, Label = parameter, Kind = kind, FormTypes = types,
            AllowsGroups = uses.All(field => field is { AllowsGroups: true }),
            MatchPresetCategory = known.Any(field => field.MatchPresetCategory),
            TriggerOnly = known.Any(field => field.TriggerOnly)
        };
    }
    public static IReadOnlyList<string> Validate(PresetValue value, string context, string category, bool partial = false) =>
        Validate(value, context, category, ContextKind(context, PresetKind.Custom), partial);

    public static IReadOnlyList<string> Validate(PresetValue value, string context, string category, PresetKind kind, bool partial = false)
    {
        var errors = new List<string>();
        ValidateValue(value, context, category, kind, partial, "", errors);
        return errors;
    }

    private static void ValidateValue(PresetValue value, string context, string category, PresetKind kind, bool partial,
        string path, List<string> errors)
    {
        kind = ContextKind(context, kind);
        context = NormalizeContext(context);
        if (partial && IsReference(value)) return;
        if (context is "value" or "values") return;
        var itemContext = context switch
        {
            "stages" => "stage", "modulators" => "modulator", "transformers" => "transformer", "effects" => "effect", "rules" => "rule", _ => ""
        };
        if (itemContext.Length != 0)
        {
            if (value.Kind != ValueKind.Sequence) errors.Add(At(path, "Expected a list."));
            else for (var index = 0; index < value.Items.Count; index++)
                ValidateValue(value.Items[index], itemContext, category, kind, partial, At(path, $"Entry {index + 1}"), errors);
            return;
        }
        if (value.Kind != ValueKind.Mapping)
        {
            errors.Add(At(path, "Expected a settings block."));
            return;
        }
        foreach (var field in GetFields(context, kind))
        {
            var child = value.Get(field.Key);
            if (child is null || child.IsNull)
            {
                if (field.Required && !partial && !(child is not null && ((context == "stage" && field.Key == "name") || (context == "modulator" && field.Key == "magnitude")))) errors.Add(At(path, $"{field.Label} is required."));
                continue;
            }
            if (partial && IsReference(child)) continue;
            var label = At(path, field.Label);
            switch (field.Kind)
            {
                case FieldKind.ObjectList:
                    if (child.Kind != ValueKind.Sequence) errors.Add($"{label}: expected a list.");
                    else
                    {
                        if (field.Required && child.Items.Count == 0 && !partial) errors.Add($"{label}: add at least one entry.");
                        for (var index = 0; index < child.Items.Count; index++)
                            ValidateValue(child.Items[index], field.ChildContext, category, kind, partial, $"{label} {index + 1}", errors);
                    }
                    break;
                case FieldKind.FormList:
                case FieldKind.IntegerList:
                    ValidateList(child, field, partial, label, errors);
                    break;
                default:
                    ValidateScalar(child, field, partial, context, label, errors);
                    break;
            }
        }
        if (context == "rule" && kind != PresetKind.Addon) ValidateStages(value, category, partial, path, errors);
        if (context == "effect" && !partial && HasText(value.Get("FormEditorID")))
        {
            foreach (var key in new[] { "magnitude", "duration" })
                if (value.Get(key) is not { IsNull: false }) errors.Add(At(path, $"{key} is required when a magic effect is selected."));
        }
    }

    private static void ValidateScalar(PresetValue value, PresetField field, bool partial, string context, string label, List<string> errors)
    {
        if (partial && IsReference(value)) return;
        if (value.Kind != ValueKind.Scalar) { errors.Add($"{label}: expected one value."); return; }
        var text = value.Text.Trim();
        switch (field.Kind)
        {
            case FieldKind.Form:
                if (field.Required && text.Length == 0 && !partial) errors.Add($"{label}: choose an item.");
                break;
            case FieldKind.Number:
                if (!TryNumber(text, out var number)) errors.Add($"{label}: enter a number.");
                else if (field.Key == "duration" && number <= 0) errors.Add($"{label}: duration must be greater than zero.");
                break;
            case FieldKind.Integer:
                if (field.Key == "no" || context == "effect")
                {
                    if (!TryUnsigned(text, out _)) errors.Add($"{label}: enter a whole number from 0 to {uint.MaxValue}.");
                }
                else if (!TryInteger(text, out var signed) || signed < int.MinValue || signed > int.MaxValue) errors.Add($"{label}: enter a whole number.");
                break;
            case FieldKind.Boolean:
                if (text is not ("true" or "True" or "TRUE" or "false" or "False" or "FALSE" or "yes" or "Yes" or "YES" or "no" or "No" or "NO" or "y" or "Y" or "n" or "N" or "on" or "On" or "ON" or "off" or "Off" or "OFF"))
                    errors.Add($"{label}: choose true or false.");
                break;
            case FieldKind.Color:
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
                if (text.Length == 0 || !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
                    errors.Add($"{label}: enter an RGB or RGBA hexadecimal color.");
                break;
        }
    }

    private static void ValidateList(PresetValue value, PresetField field, bool partial, string label, List<string> errors)
    {
        if (value.Kind is not (ValueKind.Scalar or ValueKind.Sequence)) { errors.Add($"{label}: expected a value or list."); return; }
        var items = value.Kind == ValueKind.Sequence ? value.Items : [value];
        if (field.Required && items.Count == 0 && !partial) errors.Add($"{label}: add at least one entry.");
        foreach (var item in items)
        {
            if (partial && IsReference(item)) continue;
            if (item.Kind != ValueKind.Scalar || item.IsNull || string.IsNullOrWhiteSpace(item.Text))
                errors.Add($"{label}: each entry needs a value.");
            else if (field.Kind == FieldKind.IntegerList && !TryUnsigned(item.Text, out _))
                errors.Add($"{label}: stage numbers must be whole numbers from 0 to {uint.MaxValue}.");
        }
    }

    private static void ValidateStages(PresetValue rule, string category, bool partial, string path, List<string> errors)
    {
        if (rule.Get("stages") is not { Kind: ValueKind.Sequence } stages) return;
        var numbers = new HashSet<uint>();
        var unresolved = false;
        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in stages.Items)
        {
            if (stage.Get("no") is not { Kind: ValueKind.Scalar } number || !TryUnsigned(number.Text, out var index))
            { unresolved = true; continue; }
            if (!numbers.Add(index)) errors.Add(At(path, $"Stage number {index} is used more than once."));
            if (index == 0) continue;
            var form = stage.Get("FormEditorID");
            if (partial && form is not null && IsReference(form)) continue;
            if (!HasText(form))
            {
                if (!partial && category is not ("FOOD" or "MISC"))
                    errors.Add(At(path, $"Stage {index}: {category} needs a replacement item."));
            }
            else if (!forms.Add(form!.Text)) errors.Add(At(path, $"Stage {index}: this replacement item is already used by another stage."));
        }
        if (!partial && !unresolved && Enumerable.Range(0, stages.Items.Count).Any(index => !numbers.Contains((uint)index)))
            errors.Add(At(path, "Stage numbers must start at 0 and increase by one without gaps."));
        if (partial || unresolved) return;
        foreach (var key in new[] { "timeModulators", "transformers" })
        {
            if (rule.Get(key) is not { Kind: ValueKind.Sequence } triggers) continue;
            for (var index = 0; index < triggers.Items.Count; index++)
            {
                if (triggers.Items[index].Get("allowed_stages") is not { IsNull: false } allowed) continue;
                var allowedItems = allowed.Kind == ValueKind.Sequence ? allowed.Items : [allowed];
                foreach (var item in allowedItems)
                    if (TryUnsigned(item.Text, out var stageNumber) && !numbers.Contains(stageNumber))
                        errors.Add(At(path, $"{key} {index + 1}: stage {stageNumber} is not defined by this rule."));
            }
        }
    }

    private static bool HasText(PresetValue? value) => value is { Kind: ValueKind.Scalar, IsNull: false } && !string.IsNullOrWhiteSpace(value.Text);
    private static bool IsReference(PresetValue value) => value.Kind == ValueKind.Alias || value.Kind == ValueKind.Scalar &&
        (value.Text.StartsWith('$') || TemplateCall.TryParse(value.Text, out _, out _));
    private static bool TryUnsigned(string text, out uint number)
    {
        number = 0;
        if (text.TrimStart().StartsWith('-') || !TryInteger(text, out var parsed) || parsed < 0 || parsed > uint.MaxValue) return false;
        number = (uint)parsed;
        return true;
    }
    private static bool TryInteger(string text, out long number)
    {
        number = 0;
        text = text.Trim();
        var negative = text.StartsWith('-');
        if (negative || text.StartsWith('+')) text = text[1..];
        const int decimalBase = 10, octalBase = 8, hexadecimalBase = 16;
        var numberBase = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hexadecimalBase
            : text.Length > 1 && text[0] == '0' ? octalBase : decimalBase;
        if (numberBase == hexadecimalBase) text = text[2..];
        try
        {
            var absolute = Convert.ToInt64(text, numberBase);
            if (absolute < 0) return false;
            number = negative ? -absolute : absolute;
            return true;
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { return false; }
    }
    private static bool TryNumber(string text, out float number)
    {
        if (text is ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF") { number = float.PositiveInfinity; return true; }
        if (text is "-.inf" or "-.Inf" or "-.INF") { number = float.NegativeInfinity; return true; }
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number);
    }
    private static string At(string path, string text) => path.Length == 0 ? text : $"{path}: {text}";
    private static PresetKind ContextKind(string context, PresetKind fallback) => context.ToLowerInvariant() switch
    { "defaultrule" or "defaultrules" => PresetKind.Default, "customrule" or "customrules" => PresetKind.Custom, "addonrule" or "addonrules" => PresetKind.Addon, _ => fallback };
    private static string NormalizeContext(string context) => context.ToLowerInvariant() switch
    {
        "defaultrule" or "customrule" or "addonrule" => "rule", "defaultrules" or "customrules" or "addonrules" => "rules", "timemodulator" => "modulator", "timemodulators" => "modulators",
        "magiceffect" => "effect", "magiceffects" => "effects", _ => context.ToLowerInvariant()
    };
}
