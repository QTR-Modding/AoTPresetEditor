using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace AoTPresetEditor;

public enum ValueKind { Scalar, Mapping, Sequence, Alias }
public enum BlockKind { Rule, Shared, Template }

public sealed class PresetValue
{
    private const string UnsetArgumentTag = "!aot-editor-unset";
    public bool IsUnsetArgument => Tag == UnsetArgumentTag;
    public static PresetValue UnsetArgument() => new() { Kind = ValueKind.Scalar, Tag = UnsetArgumentTag };
    public ValueKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string Anchor { get; set; } = "";
    public string Tag { get; set; } = "";
    public ScalarStyle Style { get; set; } = ScalarStyle.Plain;
    public bool Flow { get; set; }
    public Dictionary<string, PresetValue> Fields { get; } = new(StringComparer.Ordinal);
    public List<PresetValue> Items { get; } = [];
    public List<string> Comments { get; } = [];
    public List<string> TrailingComments { get; } = [];
    public string InlineComment { get; set; } = "";
    public bool IsNull => Kind == ValueKind.Scalar && (Tag == "tag:yaml.org,2002:null" ||
        (Style == ScalarStyle.Plain && Tag.Length == 0 && (Text.Length == 0 || Text is "~" or "null" or "Null" or "NULL")));

    public static PresetValue Map() => new() { Kind = ValueKind.Mapping };
    public static PresetValue List() => new() { Kind = ValueKind.Sequence };
    public static PresetValue Scalar(string text) => new() { Kind = ValueKind.Scalar, Text = text };
    public static PresetValue Alias(string name) => new() { Kind = ValueKind.Alias, Text = name };
    public PresetValue? Get(string key) => Fields.GetValueOrDefault(key);
    public void Set(string key, PresetValue value) => Fields[key] = value;
    public bool Remove(string key) => Fields.Remove(key);
    public PresetValue Clone()
    {
        var clone = new PresetValue { Kind = Kind, Text = Text, Anchor = Anchor, Tag = Tag, Style = Style, Flow = Flow, InlineComment = InlineComment };
        foreach (var field in Fields) clone.Fields.Add(field.Key, field.Value.Clone());
        clone.Items.AddRange(Items.Select(item => item.Clone()));
        clone.Comments.AddRange(Comments);
        clone.TrailingComments.AddRange(TrailingComments);
        return clone;
    }
}

public sealed class PresetBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public BlockKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public string Context { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsBehavior { get; set; }
    public PresetValue Value { get; set; } = PresetValue.Map();
    public PresetBlock Clone() => new() { Id = Id, Kind = Kind, Name = Name, Key = Key, Context = Context, IsBehavior = IsBehavior, Value = Value.Clone() };
}

public sealed class PresetDocument
{
    private const string MetadataPrefix = "# AoT Preset Editor: ";
    private static readonly HashSet<string> DefaultFields = ["<<", "stages", "finalFormEditorID", "containers", "timeModulators", "transformers"];
    public string Category { get; set; } = "FOOD";
    public PresetKind Kind { get; set; } = PresetKind.Custom;
    public List<PresetBlock> Blocks { get; } = [];
    public List<FormGroupDefinition> PendingGroups { get; } = [];
    public List<string> Comments { get; } = [];
    public List<string> TrailingComments { get; } = [];
    private List<string> ruleComments = [];
    private List<string> templateComments = [];
    private List<string> ruleTrailingComments = [];
    private List<string> templateTrailingComments = [];
    [JsonIgnore]
    public bool HasRetainedContent => Blocks.Count > 0 || PendingGroups.Count > 0 || Comments.Count > 0 ||
        TrailingComments.Count > 0 || ruleComments.Count > 0 || templateComments.Count > 0 ||
        ruleTrailingComments.Count > 0 || templateTrailingComments.Count > 0;

    private sealed record BlockMetadata(string Id, string Name, string Key, string Context, BlockKind Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsBehavior = false);

    public static PresetDocument Create(string category, PresetKind kind) => new() { Category = category, Kind = kind };

    public static PresetDocument Parse(string yaml, string category, PresetKind kind)
    {
        var document = Create(category, kind);
        List<BlockMetadata>? metadata = null;
        using var lines = new StringReader(yaml);
        var retained = new System.Text.StringBuilder();
        while (lines.ReadLine() is { } line)
        {
            if (line.StartsWith(MetadataPrefix, StringComparison.Ordinal) && metadata is null)
            {
                try { metadata = JsonSerializer.Deserialize<List<BlockMetadata>>(line[MetadataPrefix.Length..]); }
                catch (JsonException) { retained.AppendLine(line); }
            }
            else retained.AppendLine(line);
        }
        var root = PresetYaml.Parse(retained.ToString());
        if (root.IsNull) root = PresetValue.Map();
        if (root.Kind != ValueKind.Mapping) throw new InvalidDataException("The preset must contain named settings at its top level.");
        document.Comments.AddRange(root.Comments);
        document.TrailingComments.AddRange(root.TrailingComments);
        PresetBlock? defaultRule = null;
        var ruleKey = kind == PresetKind.Addon ? "formsLists" : "ownerLists";
        foreach (var (key, value) in root.Fields)
        {
            if (key == "templates")
            {
                if (value.Kind != ValueKind.Mapping) throw new InvalidDataException("Templates must be a collection of named templates.");
                document.templateComments = [.. value.Comments];
                document.templateTrailingComments = [.. value.TrailingComments];
                foreach (var (name, body) in value.Fields)
                    document.Blocks.Add(new() { Kind = BlockKind.Template, Name = name, Key = name, Context = InferContext(body, kind), Value = body });
            }
            else if (kind != PresetKind.Default && key == ruleKey)
            {
                if (value.Kind != ValueKind.Sequence)
                {
                    if (value.Kind != ValueKind.Alias && !(value.Kind == ValueKind.Scalar && TemplateCall.TryParse(value.Text, out _, out _)))
                        throw new InvalidDataException($"{ruleKey} must be a list of rules or a reference to one.");
                    document.Blocks.Add(new() { Kind = BlockKind.Rule, Name = "Shared rules", Key = ruleKey, Context = "Rules", Value = value });
                    continue;
                }
                document.ruleComments = [.. value.Comments];
                document.ruleTrailingComments = [.. value.TrailingComments];
                foreach (var rule in value.Items)
                {
                    var label = rule.Kind == ValueKind.Scalar && TemplateCall.TryParse(rule.Text, out var callName, out _)
                        ? callName : $"Rule {document.Blocks.Count(block => block.Kind == BlockKind.Rule) + 1}";
                    document.Blocks.Add(new() { Kind = BlockKind.Rule, Name = label, Context = RuleContext(kind), Value = rule });
                }
            }
            else if (kind == PresetKind.Default && DefaultFields.Contains(key))
            {
                if (defaultRule is null)
                {
                    defaultRule = new() { Kind = BlockKind.Rule, Name = "Default rule", Context = "DefaultRule" };
                    document.Blocks.Add(defaultRule);
                }
                defaultRule.Value.Set(key, value);
            }
            else document.Blocks.Add(new() { Kind = BlockKind.Shared, Name = key, Key = key, Context = InferContext(value, kind), Value = value });
        }
        if (metadata is not null)
        {
            var ruleNames = new Queue<BlockMetadata>(metadata.Where(item => item.Kind == BlockKind.Rule));
            foreach (var block in document.Blocks)
            {
                var saved = block.Kind == BlockKind.Rule ? (ruleNames.TryDequeue(out var next) ? next : null)
                    : metadata.FirstOrDefault(item => item.Kind == block.Kind && item.Key == block.Key);
                if (saved is null) continue;
                block.Id = saved.Id;
                block.Name = saved.Name;
                block.Context = saved.Context;
                block.IsBehavior = saved.IsBehavior;
            }
            var order = metadata.Select((item, index) => (item.Id, index)).ToDictionary(item => item.Id, item => item.index);
            document.Blocks.Sort((left, right) => order.GetValueOrDefault(left.Id, int.MaxValue).CompareTo(order.GetValueOrDefault(right.Id, int.MaxValue)));
        }
        return document;
    }

    public PresetBlock Add(BlockKind kind, string name, string context)
    {
        if (kind == BlockKind.Rule && Kind == PresetKind.Default && Blocks.Any(block => block.Kind == BlockKind.Rule))
            throw new InvalidOperationException("A default preset has one rule.");
        if (kind == BlockKind.Rule && Blocks.Any(block => block.Kind == BlockKind.Rule && block.Key is "ownerLists" or "formsLists"))
            throw new InvalidOperationException("This preset uses a shared list for all rules. Edit that shared list to add a rule.");
        var block = new PresetBlock { Kind = kind, Name = name, Context = context };
        if (kind != BlockKind.Rule)
        {
            var stem = Regex.Replace(name, "[^a-zA-Z0-9_-]", "_");
            if (string.IsNullOrEmpty(stem)) stem = kind == BlockKind.Shared ? "sharedSettings" : "template";
            var key = stem;
            var suffix = 2;
            while (Blocks.Any(existing => existing.Key == key) || key is "templates" or "ownerLists" or "formsLists" || DefaultFields.Contains(key))
                key = stem + suffix++;
            block.Key = key;
            if (kind == BlockKind.Shared) block.Value.Anchor = key;
        }
        Blocks.Add(block);
        return block;
    }
    public bool Remove(PresetBlock block)
    {
        if (!Blocks.Remove(block)) return false;
        if (block.Kind == BlockKind.Template && !Blocks.Any(item => item.Kind == BlockKind.Template))
        {
            Comments.AddRange(templateComments);
            TrailingComments.AddRange(templateTrailingComments);
            templateComments.Clear();
            templateTrailingComments.Clear();
        }
        return true;
    }

    public string ToYaml()
    {
        var formatted = Clone();
        foreach (var block in formatted.Blocks) formatted.FormatSettings(block.Value, block.Context);
        formatted.RebindTemplateCalls(this);
        return formatted.WriteYaml();
    }

    private string WriteYaml()
    {
        var root = PresetValue.Map();
        root.Comments.AddRange(Comments);
        root.TrailingComments.AddRange(TrailingComments);
        var templates = PresetValue.Map();
        templates.Comments.AddRange(templateComments);
        templates.TrailingComments.AddRange(templateTrailingComments);
        var rules = PresetValue.List();
        rules.Comments.AddRange(ruleComments);
        rules.TrailingComments.AddRange(ruleTrailingComments);
        foreach (var block in Blocks)
        {
            var value = block.Value;
            switch (block.Kind)
            {
                case BlockKind.Shared:
                    AddUnique(root, block.Key, value);
                    break;
                case BlockKind.Template:
                    if (!root.Fields.ContainsKey("templates")) root.Set("templates", templates);
                    AddUnique(templates, block.Key, value);
                    break;
                case BlockKind.Rule when Kind == PresetKind.Default:
                    if (value.Kind == ValueKind.Alias || value.Kind == ValueKind.Scalar && TemplateCall.TryParse(value.Text, out _, out _))
                    {
                        AddUnique(root, "<<", value);
                        break;
                    }
                    if (value.Kind != ValueKind.Mapping) throw new InvalidDataException("The default rule must be a settings block, shared reference, or template use.");
                    foreach (var field in value.Fields) AddUnique(root, field.Key, field.Value);
                    break;
                case BlockKind.Rule:
                    var key = Kind == PresetKind.Addon ? "formsLists" : "ownerLists";
                    if (block.Key == key)
                    {
                        if (Blocks.Count(item => item.Kind == BlockKind.Rule) != 1)
                            throw new InvalidDataException("A shared rule list cannot be combined with separate rules in the same preset.");
                        AddUnique(root, key, value);
                        break;
                    }
                    if (!root.Fields.ContainsKey(key)) root.Set(key, rules);
                    rules.Items.Add(value);
                    break;
            }
        }
        var names = Blocks.Select(block => new BlockMetadata(block.Id, block.Name, block.Key, block.Context, block.Kind, block.IsBehavior));
        OrderDefinitions(templates);
        OrderDefinitions(root);
        CheckAliases(root);
        return MetadataPrefix + JsonSerializer.Serialize(names) + Environment.NewLine + PresetYaml.Write(root);
    }

    private void FormatSettings(PresetValue value, string context)
    {
        if (value.Kind == ValueKind.Sequence)
        {
            var itemContext = PresetSchema.GetFields(context, Kind).Count > 0 ? context :
                PresetSchema.NormalizeContext(context) switch
                {
                    "rules" => RuleContext(Kind), "stages" => "stage", "transformers" => "transformer",
                    "modulators" => "modulator", "effects" => "effect", _ => ""
                };
            foreach (var item in value.Items) FormatSettings(item, itemContext.Length > 0 ? itemContext : InferContext(item, Kind));
            return;
        }
        foreach (var field in PresetSchema.GetFields(context, Kind))
        {
            if (value.Get(field.Key) is not { } child) continue;
            if (field.Kind is FieldKind.FormList or FieldKind.IntegerList)
            {
                child = CompactSelector(child);
                value.Set(field.Key, child);
                if (child.Kind == ValueKind.Sequence && child.TrailingComments.Count == 0 &&
                    child.Items.All(item => item.Kind is ValueKind.Scalar or ValueKind.Alias &&
                        !item.Text.Contains('\n') && !item.Text.Contains('\r') && item.Comments.Count == 0 &&
                        item.InlineComment.Length == 0 && item.TrailingComments.Count == 0)) child.Flow = true;
            }
            else if (field.Kind == FieldKind.ObjectList) FormatSettings(child, field.ChildContext);
        }
        if (value.Kind == ValueKind.Mapping && PresetSchema.NormalizeContext(context) == "rule") OrderRuleFields(value);
    }

    private static void OrderRuleFields(PresetValue value)
    {
        string[] priority = ["owners", "forms", "stages", "finalFormEditorID", "transformers", "timeModulators"];
        var ordered = value.Fields.OrderBy(entry =>
        {
            var index = Array.IndexOf(priority, entry.Key);
            return index < 0 ? priority.Length : index;
        }).ToArray();
        value.Fields.Clear();
        foreach (var entry in ordered) value.Fields.Add(entry.Key, entry.Value);
        // An anchor must still be written before any field that refers to it.
        OrderDefinitions(value);
    }

    internal static PresetValue CompactSelector(PresetValue value)
    {
        if (value.Kind != ValueKind.Sequence || value.Items.Count != 1 || value.Anchor.Length > 0 || value.Tag.Length > 0 ||
            value.Items[0] is not { Kind: ValueKind.Scalar, IsNull: false, IsUnsetArgument: false } item ||
            item.Text.StartsWith('$') || TemplateCall.TryParse(item.Text, out _, out _)) return value;
        var scalar = item.Clone();
        scalar.Comments.InsertRange(0, value.Comments);
        scalar.TrailingComments.AddRange(value.TrailingComments);
        if (value.InlineComment.Length > 0)
        {
            if (scalar.InlineComment.Length == 0) scalar.InlineComment = value.InlineComment;
            else scalar.TrailingComments.Add(value.InlineComment);
        }
        return scalar;
    }
    internal static IEnumerable<PresetValue> Descendants(PresetValue value)
    {
        yield return value;
        foreach (var child in value.Fields.Values.Concat(value.Items))
            foreach (var nested in Descendants(child)) yield return nested;
    }

    private static void OrderDefinitions(PresetValue mapping)
    {
        var entries = mapping.Fields.ToArray();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            foreach (var node in Descendants(entry.Value))
                if (node.Anchor.Length > 0 && !owners.TryAdd(node.Anchor, entry.Key))
                    throw new InvalidDataException($"Shared name '{node.Anchor}' is defined more than once.");
        var ordered = new List<KeyValuePair<string, PresetValue>>();
        var complete = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries) Visit(entry.Key);
        mapping.Fields.Clear();
        foreach (var entry in ordered) mapping.Fields.Add(entry.Key, entry.Value);
        return;
        void Visit(string key)
        {
            if (complete.Contains(key)) return;
            if (!active.Add(key)) throw new InvalidDataException($"Shared settings around '{key}' refer to each other in a circle.");
            var entry = entries.First(item => item.Key == key);
            foreach (var alias in Descendants(entry.Value).Where(node => node.Kind == ValueKind.Alias))
                if (owners.TryGetValue(alias.Text, out var owner) && owner != key) Visit(owner);
            active.Remove(key);
            complete.Add(key);
            ordered.Add(entry);
        }
    }

    private static void CheckAliases(PresetValue root)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        Visit(root);
        return;
        void Visit(PresetValue value)
        {
            if (value.Kind == ValueKind.Alias)
            {
                if (active.Contains(value.Text)) throw new InvalidDataException($"Shared settings '{value.Text}' refer back to themselves.");
                if (!defined.Contains(value.Text)) throw new InvalidDataException($"Shared settings '{value.Text}' are missing or used before their definition inside a block.");
                return;
            }
            if (value.Anchor.Length > 0)
            {
                if (!defined.Add(value.Anchor)) throw new InvalidDataException($"Shared name '{value.Anchor}' is defined more than once.");
                active.Add(value.Anchor);
            }
            foreach (var child in value.Fields.Values.Concat(value.Items)) Visit(child);
            if (value.Anchor.Length > 0) active.Remove(value.Anchor);
        }
    }

    private static void AddUnique(PresetValue map, string key, PresetValue value)
    {
        if (!map.Fields.TryAdd(key, value)) throw new InvalidDataException($"The name '{key}' is used more than once.");
    }
    public PresetDocument Clone()
    {
        var clone = Create(Category, Kind);
        clone.Blocks.AddRange(Blocks.Select(block => block.Clone()));
        clone.PendingGroups.AddRange(PendingGroups.Select(group => group with { Members = group.Members.ToArray() }));
        clone.Comments.AddRange(Comments);
        clone.TrailingComments.AddRange(TrailingComments);
        clone.ruleComments = [.. ruleComments];
        clone.templateComments = [.. templateComments];
        clone.ruleTrailingComments = [.. ruleTrailingComments];
        clone.templateTrailingComments = [.. templateTrailingComments];
        return clone;
    }

    private sealed record DraftData(int Version, PresetDocument Document, List<string> RuleComments,
        List<string> TemplateComments, List<string> RuleTrailingComments, List<string> TemplateTrailingComments);
    private static readonly JsonSerializerOptions DraftOptions = new()
    {
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
        WriteIndented = true,
        MaxDepth = 256
    };
    private const int DraftVersion = 1;

    public string ToDraft() => JsonSerializer.Serialize(new DraftData(DraftVersion, this, ruleComments,
        templateComments, ruleTrailingComments, templateTrailingComments), DraftOptions);

    public static PresetDocument FromDraft(string source)
    {
        var draft = JsonSerializer.Deserialize<DraftData>(source, DraftOptions)
            ?? throw new InvalidDataException("The draft is empty.");
        if (draft.Version != DraftVersion || draft.Document is null)
            throw new InvalidDataException("This draft version is not supported.");
        var document = draft.Document;
        document.ruleComments = draft.RuleComments ?? [];
        document.templateComments = draft.TemplateComments ?? [];
        document.ruleTrailingComments = draft.RuleTrailingComments ?? [];
        document.templateTrailingComments = draft.TemplateTrailingComments ?? [];
        return document;
    }
    public void RebindTemplateCalls(PresetDocument previous)
    {
        var signatures = TemplateSignatures(previous);
        var templateCount = Blocks.Count(block => block.Kind == BlockKind.Template);
        // A changed inner call can change its enclosing template's parameter order.
        for (var pass = 0; pass <= templateCount; pass++)
        {
            var current = TemplateSignatures(this);
            if (!current.Any(entry => signatures.TryGetValue(entry.Key, out var before) && !entry.Value.SequenceEqual(before))) return;
            foreach (var block in Blocks) RebindCalls(block.Value, signatures, current);
            signatures = current;
        }
        throw new InvalidDataException("Template parameters could not settle. Check for recursive template calls.");
    }

    public void RebindValue(PresetValue value, PresetDocument previous) =>
        RebindCalls(value, TemplateSignatures(previous), TemplateSignatures(this));

    private static bool RebindCalls(PresetValue value,
        IReadOnlyDictionary<string, IReadOnlyList<string>> previous,
        IReadOnlyDictionary<string, IReadOnlyList<string>> current)
    {
        var changed = false;
        if (value.Kind == ValueKind.Scalar && value.Tag != "tag:yaml.org,2002:str" &&
            TemplateCall.TryParse(value.Text, out var name, out var arguments) && current.TryGetValue(name, out var after))
        {
            foreach (var argument in arguments) changed |= RebindCalls(argument, previous, current);
            if (previous.TryGetValue(name, out var before) && !after.SequenceEqual(before) && arguments.Count == before.Count)
            {
                var values = before.Select((parameter, index) => (parameter, arguments[index]))
                    .ToDictionary(pair => pair.parameter, pair => pair.Item2, StringComparer.Ordinal);
                arguments = after.Select(parameter => values.GetValueOrDefault(parameter) ?? PresetValue.UnsetArgument()).ToList();
                changed = true;
            }
            if (changed) value.Text = TemplateCall.Format(name, arguments);
        }
        else
        {
            foreach (var child in value.Fields.Values.Concat(value.Items)) changed |= RebindCalls(child, previous, current);
        }
        return changed;
    }

    private static Dictionary<string, IReadOnlyList<string>> TemplateSignatures(PresetDocument document) =>
        document.Blocks.Where(block => block.Kind == BlockKind.Template).ToDictionary(block => block.Key,
            block => TemplateCall.FindParameters(block.Value, document), StringComparer.Ordinal);

    public static string RuleContext(PresetKind kind) => kind switch { PresetKind.Default => "DefaultRule", PresetKind.Addon => "AddonRule", _ => "CustomRule" };
    public static string InferContext(PresetValue value, PresetKind kind)
    {
        var context = PresetSchema.InferContext("", value);
        return context == "rule" ? RuleContext(kind) : context;
    }
}
