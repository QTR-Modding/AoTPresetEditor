using System.Globalization;

namespace AoTPresetEditor;

public enum FieldKind { Text, Form, FormList, Number, Integer, IntegerList, Boolean, Color, ObjectList }

public sealed record PresetField(string Key, string Label, FieldKind Kind, string Help,
    bool Required = false, string ChildContext = "", string[]? FormTypes = null, string? DefaultText = null,
    bool AllowsGroups = false, bool MatchPresetCategory = false, bool TriggerOnly = false, bool BoundObjectOnly = false,
    bool MatchSourceType = false, bool RequiresEditorId = false, bool InventoryOwnerOnly = false);

public static class PresetSchema
{
    private static readonly string[] ItemTypes = ["Ingestible", "Ingredient", "Armor", "Weapon", "Scroll", "Book", "SoulGem", "MiscItem"];
    private static readonly PresetField[] VisualFields =
    [
        new("color", "Tint", FieldKind.Color, "Color for items lying in the world. Use the color picker, or enter #RRGGBB or #RRGGBBAA (including opacity). Zero removes tint. Existing colors without # keep their original meaning."),
        new("sound", "Sound", FieldKind.Form, "Sound played by items lying in the world.", FormTypes: ["SoundDescriptor"]),
        new("art_object", "Art object", FieldKind.Form, "Art effect attached to items lying in the world.", FormTypes: ["ArtObject"]),
        new("effect_shader", "Effect shader", FieldKind.Form, "Shader applied to items lying in the world.", FormTypes: ["EffectShader"])
    ];
    private static readonly PresetField[] TriggerFields =
    [
        new("allowed_stages", "Allowed stages", FieldKind.IntegerList, "Stage numbers this trigger affects. Unset or empty means all stages; 0 selects the initial stage."),
        new("containers", "Containers", FieldKind.FormList, "Restrict this effect to the selected inventories; leave empty for no restriction. Items lying in the world ignore this filter.", AllowsGroups: true, InventoryOwnerOnly: true)
    ];
    private static readonly PresetField[] StageFields =
    [
        new("no", "Stage number", FieldKind.Integer, "Start at 0 and increase by one for each stage.", true, DefaultText: "0"),
        new("name", "Stage name", FieldKind.Text, "Stage label shown in LoreBox. Also added to generated FOOD or MISC item names. Leave empty to keep the original name.", true, DefaultText: ""),
        new("duration", "Duration (game hours)", FieldKind.Number, "How long this stage lasts. Use a positive number or .inf for an infinite stage.", true, DefaultText: "1"),
        new("FormEditorID", "Stage item", FieldKind.Form, "Choose a different item of the same type and AoT category as the source. FOOD or MISC stages may leave this unset to generate an item; other categories need a replacement.", FormTypes: ItemTypes, MatchPresetCategory: true, MatchSourceType: true),
        new("value", "Value", FieldKind.Integer, "Value of a generated stage item. Unset or a negative number keeps its original value. Zero is a valid override."),
        new("weight", "Weight", FieldKind.Number, "Weight of a generated stage item. Unset or a negative number keeps its original weight. Zero is a valid override."),
        new("crafting_allowed", "Allow crafting", FieldKind.Boolean, "Allows a later FOOD stage to stand in for its source item in cooking recipes. Defaults to false.", DefaultText: "false"),
        new("mgeffect", "Magic effects", FieldKind.ObjectList, "Overwrites existing effect slots on generated FOOD items, in order. An unset or empty list keeps original effects. A nonempty list zeroes remaining slots; extra entries cannot add slots.", ChildContext: "effect"),
        .. VisualFields
    ];
    private static readonly PresetField[] ModulatorFields =
    [
        new("FormEditorID", "Trigger", FieldKind.FormList, "Nearby object, item in the same inventory, location, or perk conditions. For world items, an art object, effect shader, or active magic effect on the item itself or a nearby reference can also trigger this effect. Keywords on the item's or a nearby reference's base object also trigger world items. In inventories, keywords on any contained item, including the evolving item, can trigger this effect; the inventory owner's keywords do not count. In inventories, an art object, effect shader, or active magic effect triggers when the inventory owner has it. Factions match nearby actors that belong to the faction or nearby non-actor references explicitly owned by it. In inventories, the inventory owner must match; ownership of contained items or the cell does not count. A form group or Skyrim form list can supply several triggers.", true, AllowsGroups: true, TriggerOnly: true),
        new("magnitude", "Time multiplier", FieldKind.Number, "1 is normal speed, 0 pauses progression, and negative values reverse it. This does not change transformation duration.", true, DefaultText: "1"),
        .. TriggerFields,
        .. VisualFields
    ];
    private static readonly PresetField[] TransformerFields =
    [
        new("FormEditorID", "Trigger", FieldKind.FormList, "Nearby object, item in the same inventory, location, or perk conditions. For world items, an art object, effect shader, or active magic effect on the item itself or a nearby reference can also trigger this effect. Keywords on the item's or a nearby reference's base object also trigger world items. In inventories, keywords on any contained item, including the evolving item, can trigger this effect; the inventory owner's keywords do not count. In inventories, an art object, effect shader, or active magic effect triggers when the inventory owner has it. Factions match nearby actors that belong to the faction or nearby non-actor references explicitly owned by it. In inventories, the inventory owner must match; ownership of contained items or the cell does not count. A form group or Skyrim form list can supply several triggers.", true, AllowsGroups: true, TriggerOnly: true),
        new("finalFormEditorID", "Result item", FieldKind.Form, "The object produced when the transformation finishes.", true, BoundObjectOnly: true),
        new("duration", "Duration (game hours)", FieldKind.Number, "Positive time needed to finish this transformation while its trigger applies.", true, DefaultText: "0.05"),
        .. TriggerFields,
        .. VisualFields
    ];
    private static readonly PresetField[] EffectFields =
    [
        new("FormEditorID", "Magic effect", FieldKind.Form, "Choose the effect for this existing slot. Leaving it unset keeps that slot's base effect and sets its magnitude and duration to zero.", FormTypes: ["MagicEffect"]),
        new("magnitude", "Magnitude", FieldKind.Number, "Strength of the chosen effect. Required when a magic effect is selected; zero is allowed.", DefaultText: "0"),
        new("duration", "Duration (seconds)", FieldKind.Integer, "Duration of the chosen effect in seconds. Required when an effect is selected; zero is allowed.", DefaultText: "0")
    ];
    private static readonly PresetField[] RuleFields =
    [
        new("stages", "Stages", FieldKind.ObjectList, "The sequence of stages, beginning with stage 0.", true, "stage"),
        new("finalFormEditorID", "Final item", FieldKind.Form, "Object produced after the last stage. Required even if an infinite stage prevents reaching it.", true, BoundObjectOnly: true),
        new("containers", "Containers", FieldKind.FormList, "Restrict stage progression and transformations to the selected inventories and the listed items lying in the world; leave empty for no restriction.", AllowsGroups: true, InventoryOwnerOnly: true),
        new("timeModulators", "Time modifiers", FieldKind.ObjectList, "Speed up, slow down, pause, or reverse an item's progress through its stages while the selected conditions apply.", ChildContext: "modulator"),
        new("transformers", "Transformations", FieldKind.ObjectList, "Pause stage progression and turn the item into the chosen result after the selected conditions have remained active for the specified time.", ChildContext: "transformer")
    ];
    private static readonly PresetField[] CustomFields =
    [
        new("owners", "Applies to", FieldKind.FormList, "Items selected by form identifiers, form groups, or words in their names. A scalar or a list is accepted. Group names are case-sensitive and select only their members.", true, FormTypes: ItemTypes, AllowsGroups: true, MatchPresetCategory: true, RequiresEditorId: true),
        .. RuleFields
    ];
    private static readonly PresetField[] AddonFields =
    [
        new("forms", "Applies to", FieldKind.FormList, "Items selected by form identifiers or form groups. A scalar or a list is accepted.", true, FormTypes: ItemTypes, AllowsGroups: true, MatchPresetCategory: true, RequiresEditorId: true),
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

    public static string? InactiveReason(PresetField field, PresetValue value, string context, string category,
        PresetValue? effective = null, bool partial = false)
    {
        if (partial) return null;
        PresetValue? Setting(string key)
        {
            var own = value.Get(key);
            return own is not null && IsReference(own) || own is null && value.Get("<<") is not null
                ? effective?.Get(key) ?? own : own;
        }
        context = NormalizeContext(context);
        if (context == "effect" && field.Key is "magnitude" or "duration")
        {
            if (value.Get("FormEditorID") is null && value.Get("<<") is not null && effective is null) return null;
            var effect = Setting("FormEditorID");
            if (effect is not null && IsReference(effect)) return null;
            if (!HasText(effect) || IsZeroForm(effect!)) return "AoT sets magnitude and duration to zero when no magic effect is selected.";
        }
        if (context != "stage") return null;
        var number = Setting("no");
        var initial = number is not null && TryUnsigned(number.Text, out var index) && index == 0;
        if (initial && field.Key is "FormEditorID" or "value" or "weight" or "mgeffect")
            return "Stage 0 uses the source item unchanged.";
        if (field.Key == "crafting_allowed" && (initial || category != "FOOD"))
            return "Crafting substitution applies only to FOOD stages after stage 0.";
        if (field.Key is not ("value" or "weight" or "mgeffect")) return null;
        if (field.Key == "mgeffect" && category != "FOOD") return "Magic effect overrides apply only to generated FOOD items.";
        if (category is not ("FOOD" or "MISC")) return "Only FOOD and MISC can generate stage items with overrides.";
        var item = Setting("FormEditorID");
        return item is not null && !IsReference(item) && HasText(item) && !IsZeroForm(item)
            ? "An explicit stage item keeps its own properties. Overrides apply only to generated items." : null;
    }

    internal static bool IsZeroForm(PresetValue value)
    {
        var text = value.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return text.Length > 0 && text.All(character => character == '0');
    }

    public static string FieldHelp(PresetField field, string context, PresetKind kind)
    {
        var help = field.Help;
        if (field.Key == "containers" && NormalizeContext(context) is "transformer" or "modulator")
        {
            var effect = NormalizeContext(context) == "transformer" ? "transformation" : "time modifier";
            help = $"Restrict this {effect} to the selected inventories; leave empty for no restriction. Items lying in the world ignore this filter.";
        }
        if (kind == PresetKind.Addon && field.Key == "containers" && NormalizeContext(context) == "rule")
            help = "Replace the item's shared Containers filter with this list. It affects stage progression and all transformations, including those from other presets. An empty list removes the restriction. Use existing filter removes this override and keeps the filter supplied by the default/custom rule or earlier addons.";
        else if (kind == PresetKind.Addon && field.Key is "containers" or "allowed_stages")
            help += NormalizeContext(context) == "rule"
                ? " These entries are added to the Containers filter in the item's stage settings. Leaving this empty keeps that filter."
                : " When the item's stage settings already define this trigger, the lists are combined. Leaving this empty keeps the existing list.";
        if (kind == PresetKind.Default && NormalizeContext(context) == "stage" && field.Key == "duration")
            help += " For this scope, when stage 0 lasts 9999 game hours or more (including .inf), progression only starts for items with matching transformation or time-modifier settings.";
        return help;
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

    public static PresetField? GetParameterField(PresetBlock template, string parameter, PresetDocument document,
        PresetField? destinationField = null, string? destinationContext = null) =>
        GetParameterField(template, parameter, document, destinationField, destinationContext, []);

    private static PresetField? GetParameterField(PresetBlock template, string parameter, PresetDocument document,
        PresetField? destinationField, string? destinationContext, HashSet<(string, string)> activeTemplates)
    {
        var key = (template.Id, parameter);
        if (!activeTemplates.Add(key)) return null;
        try
        {
            var uses = new List<PresetField?>();
            var context = destinationContext is null || NormalizeContext(destinationContext) is "" or "value" or "values"
                ? template.Context : destinationContext;
            FindParameterUses(template.Value, context, destinationField, parameter, document, uses, activeTemplates, []);
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
                        var argumentField = index < parameters.Count ? GetParameterField(called, parameters[index], document, field, context, activeTemplates) : null;
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
                        if (childField is not null) childField = childField with { Help = FieldHelp(childField, context, document.Kind) };
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
        if (kind == FieldKind.ObjectList && constrained.Any(field => NormalizeContext(field.ChildContext) != NormalizeContext(first.ChildContext)))
            return null;
        string[]? types = null;
        foreach (var field in constrained.Where(field => field.FormTypes is not null))
            types = types is null ? field.FormTypes : types.Intersect(field.FormTypes!, StringComparer.OrdinalIgnoreCase).ToArray();
        return first with
        {
            Key = parameter, Label = parameter, Kind = kind, FormTypes = types,
            AllowsGroups = uses.All(field => field is { AllowsGroups: true }),
            MatchPresetCategory = known.Any(field => field.MatchPresetCategory),
            TriggerOnly = known.Any(field => field.TriggerOnly),
            BoundObjectOnly = known.Any(field => field.BoundObjectOnly),
            MatchSourceType = known.Any(field => field.MatchSourceType),
            RequiresEditorId = known.Any(field => field.RequiresEditorId),
            InventoryOwnerOnly = known.Any(field => field.InventoryOwnerOnly)
        };
    }
    public static IReadOnlyList<string> Validate(PresetValue value, string context, string category, bool partial = false) =>
        Validate(value, context, category, ContextKind(context, PresetKind.Custom), partial);

    public static IReadOnlyList<string> ValidateField(PresetValue value, PresetField field, string context,
        string category, PresetKind kind, bool partial = false)
    {
        var errors = new List<string>();
        if (partial && IsReference(value)) return errors;
        if (field.Kind == FieldKind.ObjectList)
        {
            if (value.Kind != ValueKind.Sequence) errors.Add("Expected a list.");
            else
            {
                if (field.Required && value.Items.Count == 0 && !partial) errors.Add("Add at least one entry.");
                for (var index = 0; index < value.Items.Count; index++)
                    ValidateValue(value.Items[index], field.ChildContext, category, kind, partial, $"Entry {index + 1}", errors);
            }
        }
        else if (field.Kind is FieldKind.FormList or FieldKind.IntegerList) ValidateList(value, field, partial, field.Label, errors);
        else ValidateScalar(value, field, partial, NormalizeContext(context), field.Label, errors);
        return errors;
    }

    public static string DefaultDescription(PresetField field, string context, PresetKind kind = PresetKind.Custom) => field.Key switch
    {
        "containers" when kind == PresetKind.Addon => "Uses the existing filter",
        "allowed_stages" when kind == PresetKind.Addon => "Keeps any restriction from the source rule",
        "weight" or "value" => "Unchanged",
        "crafting_allowed" => "Cannot stand in for the source item in crafting",
        "mgeffect" => "Unchanged",
        "allowed_stages" => "Applies to every stage",
        "containers" => "Unrestricted",
        "timeModulators" => "Normal stage progression",
        "transformers" => "None",
        "sound" or "art_object" or "effect_shader" or "color" when NormalizeContext(context) is "transformer" or "modulator" => "Uses the stage setting",
        "sound" or "art_object" or "effect_shader" or "color" => "None",
        "FormEditorID" when NormalizeContext(context) == "stage" => "Source item at stage 0; generated item at later FOOD or MISC stages",
        "FormEditorID" when NormalizeContext(context) == "effect" => "Zeroes the corresponding effect's magnitude and duration",
        _ => field.Required ? "Choose a value to complete this field" : "Not set"
    };

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
            if (context == "effect" && InactiveReason(field, value, context, category, partial: partial) is not null) continue;
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
        if (context == "effect" && !partial && HasText(value.Get("FormEditorID")) && !IsZeroForm(value.Get("FormEditorID")!))
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
                if (field.Required && (text.Length == 0 || IsZeroForm(value)) && !partial) errors.Add($"{label}: choose an item.");
                else if (context == "stage" && field.Key == "FormEditorID" && IsZeroForm(value))
                    errors.Add($"{label}: clear this field instead of using a zero identifier.");
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
                var explicitColor = text.StartsWith('#');
                if (explicitColor) text = text[1..];
                else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
                if (text.Length == 0 || explicitColor && text.Length is not (6 or 8) ||
                    !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
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
        foreach (var field in RuleFields.Where(field => field.Key is "timeModulators" or "transformers"))
        {
            if (rule.Get(field.Key) is not { Kind: ValueKind.Sequence } triggers) continue;
            for (var index = 0; index < triggers.Items.Count; index++)
            {
                if (triggers.Items[index].Get("allowed_stages") is not { IsNull: false } allowed) continue;
                var allowedItems = allowed.Kind == ValueKind.Sequence ? allowed.Items : [allowed];
                foreach (var item in allowedItems)
                    if (TryUnsigned(item.Text, out var stageNumber) && !numbers.Contains(stageNumber))
                        errors.Add(At(path, $"{field.Label} {index + 1}: stage {stageNumber} is not defined by this rule."));
            }
        }
    }

    private static bool HasText(PresetValue? value) => value is { Kind: ValueKind.Scalar, IsNull: false } && !string.IsNullOrWhiteSpace(value.Text);
    private static bool IsReference(PresetValue value) => value.Kind == ValueKind.Alias || value.Kind == ValueKind.Scalar &&
        (value.Text.StartsWith('$') || TemplateCall.TryParse(value.Text, out _, out _));
    internal static bool TryUnsigned(string text, out uint number)
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
    internal static string NormalizeContext(string context) => context.ToLowerInvariant() switch
    {
        "defaultrule" or "customrule" or "addonrule" => "rule", "defaultrules" or "customrules" or "addonrules" => "rules", "timemodulator" => "modulator", "timemodulators" => "modulators",
        "magiceffect" => "effect", "magiceffects" => "effects", _ => context.ToLowerInvariant()
    };
}
