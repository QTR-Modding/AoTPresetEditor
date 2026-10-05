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
        rule.Value.Get("<<") is { } call ? call : rule.Value;

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
