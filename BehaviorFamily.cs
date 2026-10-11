using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AoTPresetEditor;

public sealed class BehaviorFamily
{
    public PresetBlock Definition { get; }
    public IReadOnlyList<PresetBlock> Rows { get; }
    public IReadOnlyList<string> Parameters { get; }
    public bool IsTemplate => Definition.Kind == BlockKind.Template;

    private BehaviorFamily(PresetBlock definition, IReadOnlyList<PresetBlock> rows, IReadOnlyList<string> parameters)
    {
        Definition = definition;
        Rows = rows;
        Parameters = parameters;
    }

    public static BehaviorFamily? Find(PresetDocument document, string ruleId)
    {
        var block = document.Blocks.FirstOrDefault(block => block.Id == ruleId);
        if (block is null) return null;
        PresetBlock? definition;
        if (block.Kind == BlockKind.Template && PresetSchema.NormalizeContext(block.Context) == "rule") definition = block;
        else
        {
            if (block.Kind != BlockKind.Rule || block.Key is "ownerLists" or "formsLists") return null;
            var value = CallValue(document, block);
            if (ReferenceEquals(value, block.Value) && value.Kind == ValueKind.Mapping)
                return block.Value.Anchor.Length == 0 ? new(block, [block], []) : null;
            if (!TryCall(value, out var name, out _)) return null;
            definition = document.Blocks.FirstOrDefault(candidate => candidate.Kind == BlockKind.Template && candidate.Key == name);
        }
        if (definition?.Value.Kind != ValueKind.Mapping) return null;
        var parameters = TemplateCall.FindParameters(definition.Value, document);
        var rows = document.Blocks.Where(block => block.Kind == BlockKind.Rule && block.Key.Length == 0 &&
            TryCall(CallValue(document, block), out var called, out _) && called == definition.Key).ToArray();
        if (rows.Any(row => TryCall(CallValue(document, row), out _, out var arguments) && arguments.Count > parameters.Count)) return null;
        return new(definition, rows, parameters);
    }

    public static PresetValue? GetValue(PresetValue value, IReadOnlyList<string> path)
    {
        foreach (var part in path)
        {
            if (value.Kind == ValueKind.Mapping)
            {
                if (!value.Fields.TryGetValue(part, out var child)) return null;
                value = child;
            }
            else if (value.Kind == ValueKind.Sequence && TryIndex(part, value.Items.Count, out var index)) value = value.Items[index];
            else return null;
        }
        return value;
    }

    public static string MakeVariable(PresetDocument document, string ruleId, IReadOnlyList<string> path, string? requestedName = null,
        PresetValue? initialValue = null)
    {
        var family = RequireFamily(document, ruleId);
        if (path.Count == 0) throw new InvalidOperationException("Choose a setting inside the behavior.");
        var parent = GetValue(family.Definition.Value, path.Take(path.Count - 1).ToArray());
        var value = GetValue(family.Definition.Value, path);
        if (value is null && parent?.Kind != ValueKind.Mapping) throw new InvalidOperationException("This setting is no longer present.");
        value ??= initialValue ?? PresetValue.UnsetArgument();
        if (value.Kind == ValueKind.Scalar && value.Text.StartsWith('$') && family.Parameters.Contains(value.Text[1..]))
            return value.Text[1..];
        if (PresetDocument.Descendants(value).Any(node => node.Anchor.Length > 0))
            throw new InvalidOperationException("This setting defines shared data. Edit its shared definition before varying it.");
        var parameter = requestedName;
        if (parameter is not null)
        {
            if (!TemplateCall.IsName(parameter)) throw new InvalidOperationException("Variable names can contain letters, digits, underscores and hyphens.");
            if (family.Parameters.Contains(parameter)) throw new InvalidOperationException("This behavior already has a variable with that name.");
        }
        else
        {
            var stem = string.Join("_", path.Select(part => Regex.Replace(part, "[^a-zA-Z0-9_-]", "_")));
            if (stem.Length == 0) stem = "value";
            parameter = stem;
            var suffix = 2;
            while (family.Parameters.Contains(parameter)) parameter = stem + suffix++;
        }
        var previous = document.Clone();
        var originalWasTemplate = family.IsTemplate;
        family = EnsureTemplate(document, family);
        var before = family.Parameters;
        var original = value.Clone();
        SetValue(family.Definition.Value, path, PresetValue.Scalar("$" + parameter));
        if (IsSelectorParameter(document, family.Definition, parameter)) original = PresetDocument.CompactSelector(original);
        var after = TemplateCall.FindParameters(family.Definition.Value, document);
        foreach (var block in document.Blocks)
            RewriteCalls(block.Value, family.Definition.Key, before, after, original, parameter, document,
                originalWasTemplate && block.Kind != BlockKind.Template);
        var oldDefinition = previous.Blocks.FirstOrDefault(block => block.Id == family.Definition.Id);
        // These calls are already rebound with their former value. Propagate enclosing signature changes separately.
        if (oldDefinition is not null) oldDefinition.Value = family.Definition.Value.Clone();
        document.RebindTemplateCalls(previous);
        return parameter;
    }

    public static void SetCommon(PresetDocument document, string ruleId, PresetValue value)
    {
        var family = RequireFamily(document, ruleId);
        var signatureChanged = family.IsTemplate && !family.Parameters.SequenceEqual(TemplateCall.FindParameters(value, document));
        var previous = signatureChanged ? document.Clone() : null;
        if (signatureChanged) PadCalls(document, family.Definition.Key, family.Parameters.Count);
        family.Definition.Value = value.Clone();
        if (previous is not null) document.RebindTemplateCalls(previous);
    }

    internal static void ConvertSelectors(PresetDocument document, string ruleId, Func<PresetValue, PresetValue> convert)
    {
        if (document.Kind == PresetKind.Default) return;
        var family = RequireFamily(document, ruleId);
        var key = document.Kind == PresetKind.Addon ? "forms" : "owners";
        var parameters = document.Blocks.Where(block => block.Kind == BlockKind.Template).ToDictionary(block => block.Key,
            block => SelectorParameters(document, block, key), StringComparer.Ordinal);
        var layers = RuleLayers(document, family.Definition.Value).ToArray();
        foreach (var layer in layers)
            if (layer.Get(key) is { } selector) layer.Set(key, ConvertSelector(selector, document, convert, []));
        foreach (var block in document.Blocks) ConvertCallSelectors(block.Value, parameters, document, convert);
    }

    private static bool[] SelectorParameters(PresetDocument document, PresetBlock template, string key)
    {
        var direct = RuleLayers(document, template.Value).Select(layer => layer.Get(key)).OfType<PresetValue>()
            .SelectMany(value => TemplateCall.FindParameters(value, document)).ToHashSet(StringComparer.Ordinal);
        return TemplateCall.FindParameters(template.Value, document).Select(parameter => direct.Contains(parameter) ||
            PresetSchema.GetParameterField(template, parameter, document)?.RequiresEditorId == true).ToArray();
    }

    public static PresetValue? GetSetting(PresetDocument document, string ruleId, string key)
    {
        var family = RequireFamily(document, ruleId);
        var setting = ResolveSetting(document, family.Definition.Value, key, !family.IsTemplate, []);
        return setting is { Kind: ValueKind.Alias }
            ? Substitute(setting, new Dictionary<string, PresetValue>(), document, [], !family.IsTemplate, resolveAliases: true)
            : setting;
    }

    internal static PresetValue? ResolveValue(PresetDocument document, PresetValue value, string? key = null)
    {
        try { return ResolveSetting(document, value, key, false, []); }
        catch (InvalidDataException) { return null; }
    }

    private static PresetValue? ResolveSetting(PresetDocument document, PresetValue value, string? key,
        bool materialize, HashSet<string> active)
    {
        if (key is null && value.Kind is ValueKind.Mapping or ValueKind.Sequence) return value;
        if (value.Kind == ValueKind.Mapping)
            return value.Get(key!) ?? (value.Get("<<") is { } inherited ? ResolveSetting(document, inherited, key, materialize, active) : null);
        if (value.Kind == ValueKind.Sequence)
        {
            foreach (var child in value.Items)
                if (ResolveSetting(document, child, key, materialize, active) is { } setting) return setting;
            return null;
        }
        if (value.Kind == ValueKind.Alias)
        {
            var source = document.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value)).FirstOrDefault(node => node.Anchor == value.Text);
            if (source is null || !active.Add("anchor:" + value.Text)) return null;
            try { return ResolveSetting(document, source, key, materialize, active); }
            finally { active.Remove("anchor:" + value.Text); }
        }
        if (!TryCall(value, out var name, out var arguments) ||
            document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Template && block.Key == name) is not { } template ||
            !active.Add("template:" + name)) return key is null ? value : null;
        try
        {
            var parameters = TemplateCall.FindParameters(template.Value, document);
            if (arguments.Count > parameters.Count) return null;
            var bindings = parameters.Select((parameter, index) => (parameter,
                value: index < arguments.Count ? arguments[index] : PresetValue.UnsetArgument()))
                .ToDictionary(item => item.parameter, item => item.value, StringComparer.Ordinal);
            if (key is not null && ResolveSetting(document, template.Value, key, materialize, active) is { } setting)
                return Substitute(setting, bindings, document, [], materialize, resolveAliases: true);
            var resolved = Substitute(template.Value, bindings, document, [], materialize, resolveAliases: true);
            return ResolveSetting(document, resolved, key, materialize, active);
        }
        finally { active.Remove("template:" + name); }
    }

    internal static void ChangeKind(PresetDocument document, string ruleId, PresetKind targetKind,
        Func<PresetValue, PresetValue>? convertSelector)
    {
        var previous = document.Clone();
        var family = RequireFamily(document, ruleId);
        if (targetKind != PresetKind.Default && convertSelector is not null) ConvertSelectors(document, ruleId, convertSelector);
        var sourceKey = document.Kind == PresetKind.Addon ? "forms" : "owners";
        var targetKey = targetKind == PresetKind.Addon ? "forms" : "owners";
        var layers = RuleLayers(document, family.Definition.Value).ToArray();
        if (targetKind != PresetKind.Default && sourceKey != targetKey &&
            layers.Any(layer => layer.Get(sourceKey) is not null && layer.Get(targetKey) is not null))
            throw new InvalidOperationException("This imported behavior contains both forms and owners. Resolve those selections before changing its stages.");
        var removed = layers.SelectMany(layer => layer.Fields)
            .Where(field => targetKind == PresetKind.Default && field.Key == sourceKey ||
                targetKind == PresetKind.Addon && field.Key is "stages" or "finalFormEditorID")
            .SelectMany(field => PresetDocument.Descendants(field.Value)).ToHashSet();
        var removedAnchors = removed.Where(value => value.Anchor.Length > 0).Select(value => value.Anchor).ToHashSet(StringComparer.Ordinal);
        if (document.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value)).Any(value =>
            !removed.Contains(value) && value.Kind == ValueKind.Alias && removedAnchors.Contains(value.Text)))
            throw new InvalidOperationException("A setting being removed supplies shared data used elsewhere. Remove that reference before changing these stage settings.");
        foreach (var layer in layers)
        {
            var fields = layer.Fields.ToArray();
            layer.Fields.Clear();
            foreach (var (key, value) in fields)
            {
                if (key == sourceKey)
                {
                    if (targetKind != PresetKind.Default) layer.Set(targetKey, value);
                    else KeepComments(value, layer);
                }
                else if (targetKind == PresetKind.Addon && key is "stages" or "finalFormEditorID") KeepComments(value, layer);
                else layer.Set(key, value);
            }
        }
        if (document.Kind == PresetKind.Default)
        {
            var fields = family.Definition.Value.Fields.ToArray();
            family.Definition.Value.Fields.Clear();
            family.Definition.Value.Set(targetKey, PresetValue.List());
            foreach (var field in fields) family.Definition.Value.Set(field.Key, field.Value);
            foreach (var row in family.Rows)
                if (!ReferenceEquals(CallValue(document, row), row.Value))
                {
                    var call = CallValue(document, row).Clone();
                    KeepComments(row.Value, call);
                    row.Value = call;
                }
        }
        foreach (var block in document.Blocks.Where(block => block.Id == family.Definition.Id ||
            family.Rows.Contains(block) || layers.Contains(block.Value)))
            if (PresetSchema.NormalizeContext(block.Context) == "rule") block.Context = PresetDocument.RuleContext(targetKind);
        document.Kind = targetKind;
        document.RebindTemplateCalls(previous);
    }

    private static IEnumerable<PresetValue> RuleLayers(PresetDocument document, PresetValue root)
    {
        var visited = new HashSet<PresetValue>();
        return Visit(root);
        IEnumerable<PresetValue> Visit(PresetValue value)
        {
            if (!visited.Add(value)) yield break;
            if (value.Kind == ValueKind.Mapping)
            {
                yield return value;
                if (value.Get("<<") is { } inherited)
                    foreach (var layer in Visit(inherited)) yield return layer;
            }
            else if (value.Kind == ValueKind.Sequence)
            {
                foreach (var child in value.Items)
                    foreach (var layer in Visit(child)) yield return layer;
            }
            else
            {
                var definition = value.Kind == ValueKind.Alias
                    ? document.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value)).FirstOrDefault(node => node.Anchor == value.Text)
                    : TryCall(value, out var name, out _) ? document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Template && block.Key == name)?.Value : null;
                if (definition is not null)
                    foreach (var layer in Visit(definition)) yield return layer;
            }
        }
    }

    private static PresetValue ConvertSelector(PresetValue value, PresetDocument document,
        Func<PresetValue, PresetValue> convert, HashSet<string> active)
    {
        if (value.Kind == ValueKind.Scalar && (value.Text.StartsWith('$') || value.IsNull || value.IsUnsetArgument)) return value;
        if (value.Kind == ValueKind.Alias)
        {
            var source = document.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value)).FirstOrDefault(node => node.Anchor == value.Text);
            if (source is null || !active.Add(value.Text)) throw new InvalidDataException("The affected items refer to missing or circular shared settings.");
            var resolved = source.Clone();
            foreach (var node in PresetDocument.Descendants(resolved)) node.Anchor = "";
            var converted = ConvertSelector(resolved, document, convert, active);
            active.Remove(value.Text);
            if (PresetYaml.Write(resolved) == PresetYaml.Write(converted)) return value;
            PreserveSelectorAnnotations(value, converted);
            return converted;
        }
        if (TryCall(value, out var name, out var arguments) &&
            document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Template && block.Key == name) is { } template)
        {
            if (!active.Add("template:" + name)) throw new InvalidDataException("The affected items refer to circular templates.");
            var parameters = TemplateCall.FindParameters(template.Value, document);
            if (arguments.Count > parameters.Count) throw new InvalidDataException("The affected items supply too many template arguments.");
            var bindings = parameters.Select((parameter, index) => (parameter, value: index < arguments.Count ? arguments[index] : PresetValue.UnsetArgument()))
                .ToDictionary(item => item.parameter, item => item.value, StringComparer.Ordinal);
            var resolved = Substitute(template.Value, bindings, document, [], false, resolveAliases: true);
            foreach (var node in PresetDocument.Descendants(resolved)) node.Anchor = "";
            var converted = ConvertSelector(resolved, document, convert, active);
            active.Remove("template:" + name);
            if (PresetYaml.Write(resolved) == PresetYaml.Write(converted)) return value;
            PreserveSelectorAnnotations(value, converted);
            return converted;
        }
        if (value.Kind == ValueKind.Sequence)
        {
            var result = value.Clone();
            result.Items.Clear();
            foreach (var item in value.Items)
            {
                var converted = ConvertSelector(item, document, convert, active);
                if (converted.Kind == ValueKind.Sequence && item.Kind != ValueKind.Sequence)
                {
                    if (converted.Anchor.Length > 0)
                    {
                        if (value.Items.Count != 1 || result.Anchor.Length > 0)
                            throw new InvalidOperationException("A selected group defines shared data inside this list. Move that shared value to its own definition before expanding the selection.");
                        result.Anchor = converted.Anchor;
                    }
                    result.Items.AddRange(converted.Items);
                    KeepComments(converted, result);
                }
                else result.Items.Add(converted);
            }
            return result;
        }
        var replacement = convert(value.Clone()).Clone();
        PreserveSelectorAnnotations(value, replacement);
        return replacement;
    }

    private static void PreserveSelectorAnnotations(PresetValue source, PresetValue target)
    {
        if (source.Anchor.Length > 0) target.Anchor = source.Anchor;
        if (!source.Comments.SequenceEqual(target.Comments)) target.Comments.InsertRange(0, source.Comments);
        if (!source.TrailingComments.SequenceEqual(target.TrailingComments)) target.TrailingComments.AddRange(source.TrailingComments);
        if (source.InlineComment.Length == 0 || source.InlineComment == target.InlineComment) return;
        if (target.InlineComment.Length > 0) target.TrailingComments.Add(target.InlineComment);
        target.InlineComment = source.InlineComment;
    }

    private static void ConvertCallSelectors(PresetValue value, IReadOnlyDictionary<string, bool[]> selectors,
        PresetDocument document, Func<PresetValue, PresetValue> convert)
    {
        if (TryCall(value, out var name, out var arguments) && selectors.TryGetValue(name, out var fields))
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                if (index < fields.Length && fields[index]) arguments[index] =
                    PresetDocument.CompactSelector(ConvertSelector(arguments[index], document, convert, []));
                else ConvertCallSelectors(arguments[index], selectors, document, convert);
            }
            value.Text = TemplateCall.Format(name, arguments);
        }
        else foreach (var child in value.Fields.Values.Concat(value.Items)) ConvertCallSelectors(child, selectors, document, convert);
    }

    public static string ClearVariable(PresetDocument document, string ruleId, string parameter)
    {
        var family = RequireFamily(document, ruleId);
        if (!family.Parameters.Contains(parameter)) throw new InvalidOperationException("This variable is no longer present.");
        var anchors = PresetDocument.Descendants(family.Definition.Value)
            .Where(node => node.Kind == ValueKind.Scalar && node.Text == "$" + parameter && node.Anchor.Length > 0)
            .Select(node => node.Anchor).ToHashSet(StringComparer.Ordinal);
        if (document.Blocks.Where(block => block.Id != family.Definition.Id).SelectMany(block => PresetDocument.Descendants(block.Value))
            .Any(node => node.Kind == ValueKind.Alias && anchors.Contains(node.Text)))
            throw new InvalidOperationException("Another behavior refers to this field's shared name. Remove that reference before clearing the variable.");
        var cleared = ClearParameter(family.Definition.Value, parameter, document, [])!;
        SetCommon(document, ruleId, cleared);
        if (Find(document, ruleId) is { Rows.Count: 0, Parameters.Count: 0 }) ruleId = AddUse(document, ruleId).Id;
        return Normalize(document, ruleId);
    }

    public static string Normalize(PresetDocument document, string ruleId)
    {
        var family = Find(document, ruleId);
        if (family is not { IsTemplate: true, Parameters.Count: 0, Rows.Count: > 0 } ||
            CallNames(family.Definition.Value).Contains(family.Definition.Key) ||
            PresetDocument.Descendants(family.Definition.Value).Any(node => node.Anchor.Length > 0 || node.Kind == ValueKind.Alias) ||
            family.Rows.Any(row => PresetDocument.Descendants(row.Value).Any(HasAnnotations)) ||
            new PresetPresentation(document, []).Uses(family.Definition).Any(use => !family.Rows.Contains(use))) return ruleId;
        var first = family.Rows[0];
        var firstIndex = document.Blocks.IndexOf(first);
        for (var index = 0; index < family.Rows.Count; index++)
            if (document.Blocks.IndexOf(family.Rows[index]) != firstIndex + index || family.Rows[index].Name != first.Name ||
                family.Rows[index].Context != first.Context || !TryCall(CallValue(document, family.Rows[index]), out _, out var arguments) || arguments.Count != 0)
                return ruleId;
        var value = Substitute(family.Definition.Value, new Dictionary<string, PresetValue>(), document, [], true);
        first.Value = value;
        first.Name = family.Definition.Name;
        foreach (var row in family.Rows.Skip(1)) document.Remove(row);
        document.Remove(family.Definition);
        return first.Id;
    }

    private static bool HasAnnotations(PresetValue value) => value.Anchor.Length > 0 || value.Tag.Length > 0 ||
        value.Comments.Count > 0 || value.TrailingComments.Count > 0 || value.InlineComment.Length > 0;

    private static PresetValue? ClearParameter(PresetValue value, string parameter, PresetDocument document, HashSet<string> active)
    {
        if (value.Kind == ValueKind.Scalar && value.Text == "$" + parameter) return null;
        PresetValue? source = null;
        var reference = "";
        if (value.Kind == ValueKind.Alias)
        {
            source = document.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value)).FirstOrDefault(node => node.Anchor == value.Text);
            reference = "&" + value.Text;
        }
        else if (TryCall(value, out var name, out var arguments) && TemplateCall.FindParameters(value, document).Contains(parameter))
        {
            var definition = document.Blocks.FirstOrDefault(block => block.Kind == BlockKind.Template && block.Key == name);
            if (definition is not null)
            {
                var parameters = TemplateCall.FindParameters(definition.Value, document);
                var bindings = parameters.Select((key, index) => (key, value: index < arguments.Count ? arguments[index] : PresetValue.UnsetArgument()))
                    .ToDictionary(item => item.key, item => item.value, StringComparer.Ordinal);
                source = Substitute(definition.Value, bindings, document, [], false);
                reference = name;
            }
        }
        if (source is not null && TemplateCall.FindParameters(source, document).Contains(parameter))
        {
            if (!active.Add(reference)) throw new InvalidDataException("Shared settings refer to each other in a circle.");
            var resolved = ClearParameter(source, parameter, document, active);
            active.Remove(reference);
            if (resolved is not null)
            {
                foreach (var node in PresetDocument.Descendants(resolved)) node.Anchor = "";
                KeepComments(value, resolved);
            }
            return resolved;
        }
        var result = value.Clone();
        foreach (var (key, child) in value.Fields)
        {
            var cleared = ClearParameter(child, parameter, document, active);
            if (cleared is not null) result.Set(key, cleared);
            else { result.Remove(key); KeepComments(child, result); }
        }
        result.Items.Clear();
        foreach (var child in value.Items)
        {
            var cleared = ClearParameter(child, parameter, document, active);
            if (cleared is not null) result.Items.Add(cleared);
            else KeepComments(child, result);
        }
        return result;
    }

    private static void KeepComments(PresetValue source, PresetValue target)
    {
        target.TrailingComments.AddRange(source.Comments);
        if (source.InlineComment.Length > 0) target.TrailingComments.Add(source.InlineComment);
        target.TrailingComments.AddRange(source.TrailingComments);
    }

    public static PresetValue GetArgument(PresetDocument document, string rowId, string parameter)
    {
        var family = RequireFamily(document, rowId);
        var index = ParameterIndex(family, parameter);
        var row = family.Rows.First(block => block.Id == rowId);
        TryCall(CallValue(document, row), out _, out var arguments);
        return index < arguments.Count ? arguments[index] : PresetValue.UnsetArgument();
    }

    public static void SetArgument(PresetDocument document, string rowId, string parameter, PresetValue value)
    {
        var family = RequireFamily(document, rowId);
        var index = ParameterIndex(family, parameter);
        var row = family.Rows.First(block => block.Id == rowId);
        var call = CallValue(document, row);
        TryCall(call, out _, out var arguments);
        while (arguments.Count < family.Parameters.Count) arguments.Add(PresetValue.UnsetArgument());
        arguments[index] = IsSelectorParameter(document, family.Definition, parameter)
            ? PresetDocument.CompactSelector(value).Clone() : value.Clone();
        call.Text = TemplateCall.Format(family.Definition.Key, arguments);
    }

    public static PresetBlock AddUse(PresetDocument document, string ruleId)
    {
        var family = EnsureTemplate(document, RequireFamily(document, ruleId));
        if (document.Kind == PresetKind.Default && family.Rows.Count > 0) throw new InvalidOperationException("A default preset has one use.");
        var source = family.Rows.LastOrDefault() ?? family.Definition;
        var row = new PresetBlock
        {
            Kind = BlockKind.Rule, Name = family.Definition.Name, Context = source.Context,
            Value = PresetValue.Scalar(TemplateCall.Format(family.Definition.Key, family.Parameters.Select(_ => PresetValue.UnsetArgument())))
        };
        document.Blocks.Insert(document.Blocks.IndexOf(source) + 1, row);
        return row;
    }

    public static PresetBlock DuplicateUse(PresetDocument document, string rowId)
    {
        if (document.Kind == PresetKind.Default) throw new InvalidOperationException("A default preset has one use.");
        var family = EnsureTemplate(document, RequireFamily(document, rowId));
        var source = family.Rows.First(block => block.Id == rowId);
        var row = source.Clone();
        row.Id = Guid.NewGuid().ToString("N");
        row.Value.Anchor = "";
        document.Blocks.Insert(document.Blocks.IndexOf(source) + 1, row);
        return row;
    }

    public static bool RemoveUse(PresetDocument document, string rowId)
    {
        var family = RequireFamily(document, rowId);
        if (family.IsTemplate && family.Rows.Count == 1) family.Definition.IsBehavior = true;
        return document.Remove(family.Rows.First(block => block.Id == rowId));
    }

    private static BehaviorFamily RequireFamily(PresetDocument document, string ruleId) =>
        Find(document, ruleId) ?? throw new InvalidOperationException("This imported rule uses a structure that must be edited in its original form.");

    private static BehaviorFamily EnsureTemplate(PresetDocument document, BehaviorFamily family)
    {
        if (family.IsTemplate) return family;
        var rule = family.Definition;
        var body = EscapeLiteral(rule.Value, document, [], out _);
        var reservedNames = document.Blocks.Select(block => block.Key)
            .Concat(document.Blocks.SelectMany(block => CallNames(block.Value))).ToHashSet(StringComparer.Ordinal);
        var definition = document.Add(BlockKind.Template, rule.Name, rule.Context);
        var stem = definition.Key;
        var suffix = 2;
        while (reservedNames.Contains(definition.Key)) definition.Key = stem + suffix++;
        definition.Value = body;
        rule.Value = PresetValue.Scalar(TemplateCall.Format(definition.Key,
            TemplateCall.FindParameters(definition.Value, document).Select(_ => PresetValue.UnsetArgument())));
        document.Blocks.Remove(definition);
        document.Blocks.Insert(document.Blocks.IndexOf(rule), definition);
        return RequireFamily(document, rule.Id);
    }

    private static IEnumerable<string> CallNames(PresetValue value)
    {
        if (value.Kind == ValueKind.Scalar && value.Tag != "tag:yaml.org,2002:str")
        {
            var open = value.Text.IndexOf('(');
            if (open > 0 && TemplateCall.IsName(value.Text[..open])) yield return value.Text[..open];
            if (TryCall(value, out _, out var arguments))
                foreach (var argument in arguments)
                    foreach (var name in CallNames(argument)) yield return name;
        }
        foreach (var child in value.Fields.Values.Concat(value.Items))
            foreach (var name in CallNames(child)) yield return name;
    }

    private static int ParameterIndex(BehaviorFamily family, string parameter)
    {
        for (var index = 0; index < family.Parameters.Count; index++)
            if (family.Parameters[index] == parameter) return index;
        throw new InvalidOperationException("This variation is no longer present.");
    }

    private static bool IsSelectorParameter(PresetDocument document, PresetBlock definition, string parameter)
    {
        var key = document.Kind == PresetKind.Addon ? "forms" : "owners";
        return document.Kind != PresetKind.Default && definition.Value.Get(key) is { Kind: ValueKind.Scalar } selector &&
            selector.Text == "$" + parameter && !definition.Value.Fields.Where(field => field.Key != key)
                .Any(field => TemplateCall.FindParameters(field.Value, document).Contains(parameter));
    }

    private static PresetValue CallValue(PresetDocument document, PresetBlock rule) =>
        document.Kind == PresetKind.Default && rule.Value.Kind == ValueKind.Mapping && rule.Value.Fields.Count == 1 &&
        rule.Value.Get("<<") is { } call && TryCall(call, out _, out _) ? call : rule.Value;

    private static bool TryCall(PresetValue value, out string name, out List<PresetValue> arguments)
    {
        name = "";
        arguments = [];
        return value.Kind == ValueKind.Scalar && value.Tag != "tag:yaml.org,2002:str" && TemplateCall.TryParse(value.Text, out name, out arguments);
    }

    private static bool TryIndex(string part, int count, out int index) =>
        int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0 && index < count;

    private static void SetValue(PresetValue root, IReadOnlyList<string> path, PresetValue value)
    {
        var parent = GetValue(root, path.Take(path.Count - 1).ToArray())!;
        if (parent.Kind == ValueKind.Mapping) parent.Set(path[^1], value);
        else if (parent.Kind == ValueKind.Sequence && TryIndex(path[^1], parent.Items.Count, out var index)) parent.Items[index] = value;
        else throw new InvalidOperationException("This setting is no longer present.");
    }

    private static void PadCalls(PresetDocument document, string name, int count)
    {
        foreach (var block in document.Blocks) Visit(block.Value);
        bool Visit(PresetValue node)
        {
            var changed = false;
            if (TryCall(node, out var called, out var arguments) && document.Blocks.Any(block => block.Kind == BlockKind.Template && block.Key == called))
            {
                foreach (var argument in arguments) changed |= Visit(argument);
                if (called == name && arguments.Count < count)
                {
                    while (arguments.Count < count) arguments.Add(PresetValue.UnsetArgument());
                    changed = true;
                }
                if (changed) node.Text = TemplateCall.Format(called, arguments);
            }
            else foreach (var child in node.Fields.Values.Concat(node.Items)) changed |= Visit(child);
            return changed;
        }
    }

    private static bool RewriteCalls(PresetValue node, string name, IReadOnlyList<string> before, IReadOnlyList<string> after,
        PresetValue original, string parameter, PresetDocument document, bool materialize)
    {
        var changed = false;
        if (TryCall(node, out var called, out var arguments) && document.Blocks.Any(block => block.Kind == BlockKind.Template && block.Key == called))
        {
            foreach (var argument in arguments) changed |= RewriteCalls(argument, name, before, after, original, parameter, document, materialize);
            if (called == name && arguments.Count <= before.Count)
            {
                var values = before.Select((key, index) => (key, value: index < arguments.Count ? arguments[index] : PresetValue.UnsetArgument()))
                    .ToDictionary(item => item.key, item => item.value, StringComparer.Ordinal);
                values[parameter] = Substitute(original, values, document, [], materialize, resolveAliases: true);
                arguments = after.Select(key => values.GetValueOrDefault(key) ?? PresetValue.UnsetArgument()).ToList();
                changed = true;
            }
            if (changed) node.Text = TemplateCall.Format(called, arguments);
        }
        else foreach (var child in node.Fields.Values.Concat(node.Items)) changed |= RewriteCalls(child, name, before, after, original, parameter, document, materialize);
        return changed;
    }

    private static PresetValue Substitute(PresetValue value, IReadOnlyDictionary<string, PresetValue> values,
        PresetDocument document, HashSet<string> activeAliases, bool materialize, bool resolveAliases = false)
    {
        if (value.Kind == ValueKind.Scalar && value.Text.StartsWith('$') && values.TryGetValue(value.Text[1..], out var replacement))
        {
            var bound = replacement.Clone();
            if (value.Anchor.Length > 0) bound.Anchor = value.Anchor;
            bound.Comments.AddRange(value.Comments);
            bound.TrailingComments.AddRange(value.TrailingComments);
            if (value.InlineComment.Length > 0) bound.InlineComment = value.InlineComment;
            return bound;
        }
        if (value.Kind == ValueKind.Alias)
        {
            var source = document.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value)).FirstOrDefault(node => node.Anchor == value.Text);
            if (source is null || !resolveAliases && !TemplateCall.FindParameters(source, document).Any(values.ContainsKey)) return value.Clone();
            if (!activeAliases.Add(value.Text)) throw new InvalidDataException("Shared settings refer to each other in a circle.");
            var resolved = Substitute(source, values, document, activeAliases, materialize, resolveAliases);
            foreach (var node in PresetDocument.Descendants(resolved)) node.Anchor = "";
            activeAliases.Remove(value.Text);
            return resolved;
        }
        var result = value.Clone();
        if (materialize && value.Kind == ValueKind.Scalar && value.Text.StartsWith("$$")) result.Text = value.Text[1..];
        else if (TryCall(value, out var name, out var arguments) && document.Blocks.Any(block => block.Kind == BlockKind.Template && block.Key == name))
            result.Text = TemplateCall.Format(name, arguments.Select(argument => Substitute(argument, values, document, activeAliases, materialize, resolveAliases)));
        else if (value.Kind == ValueKind.Mapping)
            foreach (var (key, child) in value.Fields) result.Set(key, Substitute(child, values, document, activeAliases, materialize, resolveAliases));
        else if (value.Kind == ValueKind.Sequence)
            for (var index = 0; index < value.Items.Count; index++) result.Items[index] = Substitute(value.Items[index], values, document, activeAliases, materialize, resolveAliases);
        return result;
    }

    private static PresetValue EscapeLiteral(PresetValue value, PresetDocument document, HashSet<string> activeAliases, out bool changed)
    {
        changed = false;
        if (value.Kind == ValueKind.Alias)
        {
            var source = document.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value)).FirstOrDefault(node => node.Anchor == value.Text);
            if (source is null) return value.Clone();
            if (!activeAliases.Add(value.Text)) throw new InvalidDataException("Shared settings refer to each other in a circle.");
            var resolved = EscapeLiteral(source, document, activeAliases, out changed);
            activeAliases.Remove(value.Text);
            if (!changed) return value.Clone();
            foreach (var node in PresetDocument.Descendants(resolved)) node.Anchor = "";
            return resolved;
        }
        var result = value.Clone();
        if (value.Kind == ValueKind.Scalar && value.Text.StartsWith('$'))
        {
            result.Text = "$" + value.Text;
            changed = true;
        }
        else if (TryCall(value, out var name, out var arguments) && document.Blocks.Any(block => block.Kind == BlockKind.Template && block.Key == name))
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                arguments[index] = EscapeLiteral(arguments[index], document, activeAliases, out var edited);
                changed |= edited;
            }
            if (changed) result.Text = TemplateCall.Format(name, arguments);
        }
        else if (value.Kind == ValueKind.Mapping)
            foreach (var (key, child) in value.Fields)
            {
                result.Set(key, EscapeLiteral(child, document, activeAliases, out var edited));
                changed |= edited;
            }
        else if (value.Kind == ValueKind.Sequence)
            for (var index = 0; index < value.Items.Count; index++)
            {
                result.Items[index] = EscapeLiteral(value.Items[index], document, activeAliases, out var edited);
                changed |= edited;
            }
        return result;
    }
}
