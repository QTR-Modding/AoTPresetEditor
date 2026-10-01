using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace AoTPresetEditor;

public static class PresetYaml
{
    private const int MaximumDepth = 128;

    public static PresetValue Parse(string yaml)
    {
        var parser = new Parser(new Scanner(new StringReader(yaml), skipComments: false));
        var events = new List<ParsingEvent>();
        while (parser.MoveNext()) if (parser.Current is { } current) events.Add(current);
        var reader = new ValueReader(events);
        return reader.ReadDocument();
    }

    private sealed class ValueReader(List<ParsingEvent> events)
    {
        private int position;
        private ParsingEvent? Current => position < events.Count ? events[position] : null;
        private List<string> ReadComments()
        {
            List<string> comments = [];
            while (Current is Comment comment) { comments.Add(comment.Value); position++; }
            return comments;
        }
        public PresetValue ReadDocument()
        {
            if (Current is StreamStart) position++;
            var comments = ReadComments();
            if (Current is StreamEnd or null) { var empty = PresetValue.Map(); empty.Comments.AddRange(comments); return empty; }
            if (Current is not DocumentStart) throw new InvalidDataException("Expected a YAML document.");
            position++;
            var value = ReadValue(0);
            value.Comments.InsertRange(0, comments);
            value.TrailingComments.AddRange(ReadComments());
            if (Current is DocumentEnd) position++;
            value.TrailingComments.AddRange(ReadComments());
            if (Current is not (StreamEnd or null)) throw new InvalidDataException("A preset file must contain one YAML document.");
            return value;
        }
        private PresetValue ReadValue(int depth)
        {
            if (depth > MaximumDepth) throw new InvalidDataException("YAML nesting is too deep.");
            var comments = ReadComments();
            PresetValue value;
            switch (Current)
            {
                case Scalar scalar:
                    value = PresetValue.Scalar(scalar.Value);
                    value.Style = scalar.Style;
                    value.Anchor = scalar.Anchor.IsEmpty ? "" : scalar.Anchor.Value;
                    value.Tag = scalar.Tag.IsEmpty ? "" : scalar.Tag.Value;
                    position++;
                    break;
                case AnchorAlias alias:
                    value = PresetValue.Alias(alias.Value.Value);
                    position++;
                    break;
                case MappingStart mapping:
                    value = PresetValue.Map();
                    value.Anchor = mapping.Anchor.IsEmpty ? "" : mapping.Anchor.Value;
                    value.Tag = mapping.Tag.IsEmpty ? "" : mapping.Tag.Value;
                    value.Flow = mapping.Style == MappingStyle.Flow;
                    position++;
                    while (true)
                    {
                        var beforeKey = ReadComments();
                        if (Current is MappingEnd)
                        {
                            value.TrailingComments.AddRange(beforeKey);
                            position++;
                            break;
                        }
                        var key = ReadValue(depth + 1);
                        if (key.Kind != ValueKind.Scalar || key.Anchor.Length > 0 || key.Tag.Length > 0)
                            throw new InvalidDataException("Preset setting names must be plain text keys.");
                        var child = ReadValue(depth + 1);
                        child.Comments.InsertRange(0, beforeKey.Concat(key.Comments));
                        if (!value.Fields.TryAdd(key.Text, child)) throw new InvalidDataException($"Duplicate setting '{key.Text}'.");
                    }
                    break;
                case SequenceStart sequence:
                    value = PresetValue.List();
                    value.Anchor = sequence.Anchor.IsEmpty ? "" : sequence.Anchor.Value;
                    value.Tag = sequence.Tag.IsEmpty ? "" : sequence.Tag.Value;
                    value.Flow = sequence.Style == SequenceStyle.Flow;
                    position++;
                    while (true)
                    {
                        var beforeItem = ReadComments();
                        if (Current is SequenceEnd)
                        {
                            value.TrailingComments.AddRange(beforeItem);
                            position++;
                            break;
                        }
                        var child = ReadValue(depth + 1);
                        child.Comments.InsertRange(0, beforeItem);
                        value.Items.Add(child);
                    }
                    break;
                default: throw new InvalidDataException("Expected a YAML setting value.");
            }
            value.Comments.InsertRange(0, comments);
            return value;
        }
    }

    public static string Write(PresetValue value, bool flow = false)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var emitter = new Emitter(writer, new EmitterSettings().WithBestIndent(2).WithBestWidth(int.MaxValue));
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, true));
        EmitValue(emitter, value, flow, true, 0);
        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());
        return writer.ToString();
    }
    private static void EmitComments(IEmitter emitter, IEnumerable<string> comments)
    {
        foreach (var comment in comments) emitter.Emit(new Comment(comment, false));
    }
    private static void EmitValue(IEmitter emitter, PresetValue value, bool flow, bool comments, int depth)
    {
        if (depth > MaximumDepth) throw new InvalidDataException("YAML nesting is too deep.");
        if (comments) EmitComments(emitter, value.Comments);
        var anchor = value.Anchor.Length == 0 ? AnchorName.Empty : new AnchorName(value.Anchor);
        var tag = value.Tag.Length == 0 ? TagName.Empty : new TagName(value.Tag);
        switch (value.Kind)
        {
            case ValueKind.Scalar:
                emitter.Emit(new Scalar(anchor, tag, value.IsNull ? "null" : value.Text, value.Style, tag.IsEmpty, tag.IsEmpty));
                break;
            case ValueKind.Alias:
                emitter.Emit(new AnchorAlias(new AnchorName(value.Text)));
                break;
            case ValueKind.Mapping:
                emitter.Emit(new MappingStart(anchor, tag, tag.IsEmpty, flow || value.Flow ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var field in value.Fields)
                {
                    EmitComments(emitter, field.Value.Comments);
                    emitter.Emit(new Scalar(field.Key));
                    EmitValue(emitter, field.Value, flow, false, depth + 1);
                }
                EmitComments(emitter, value.TrailingComments);
                emitter.Emit(new MappingEnd());
                break;
            case ValueKind.Sequence:
                emitter.Emit(new SequenceStart(anchor, tag, tag.IsEmpty, flow || value.Flow ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in value.Items) EmitValue(emitter, item, flow, true, depth + 1);
                EmitComments(emitter, value.TrailingComments);
                emitter.Emit(new SequenceEnd());
                break;
        }
        if (value.Kind is ValueKind.Scalar or ValueKind.Alias) EmitComments(emitter, value.TrailingComments);
    }
}

public static class TemplateCall
{
    public static bool TryParse(string text, out string name, out List<PresetValue> arguments)
    {
        name = "";
        arguments = [];
        var open = text.IndexOf('(');
        if (open <= 0 || !text.EndsWith(')') || !IsName(text[..open])) return false;
        try
        {
            var sequence = PresetYaml.Parse("[" + text[(open + 1)..^1] + "]");
            if (sequence.Kind != ValueKind.Sequence) return false;
            name = text[..open];
            arguments = sequence.Items;
            return true;
        }
        catch (Exception exception) when (exception is YamlException or InvalidDataException) { return false; }
    }
    public static bool IsName(string name) => Regex.IsMatch(name, "\\A[a-zA-Z0-9_-]+\\z");
    public static string Format(string name, IEnumerable<PresetValue> arguments)
    {
        if (!IsName(name)) throw new InvalidDataException("Template names can contain letters, digits, underscores and hyphens.");
        var sequence = PresetValue.List();
        sequence.Items.AddRange(arguments);
        var text = PresetYaml.Write(sequence, flow: true).Trim();
        return name + "(" + text[1..^1] + ")";
    }
    public static IReadOnlyList<string> FindParameters(PresetValue body, PresetDocument? document = null)
    {
        List<string> parameters = [];
        var visited = new HashSet<PresetValue>(ReferenceEqualityComparer.Instance);
        Find(body);
        return parameters;
        void Find(PresetValue value)
        {
            if (!visited.Add(value)) return;
            if (value.Kind == ValueKind.Alias)
            {
                var source = document?.Blocks.SelectMany(block => PresetDocument.Descendants(block.Value)).FirstOrDefault(node => node.Anchor == value.Text);
                if (source is not null) Find(source);
            }
            else if (value.Kind == ValueKind.Scalar)
            {
                if (value.Tag != "tag:yaml.org,2002:str" && TryParse(value.Text, out var callName, out var args) &&
                    (document is null || document.Blocks.Any(block => block.Kind == BlockKind.Template && block.Key == callName)))
                    foreach (var arg in args) Find(arg);
                else if (value.Text.StartsWith('$') && !value.Text.StartsWith("$$") && IsName(value.Text[1..]) && !parameters.Contains(value.Text[1..]))
                    parameters.Add(value.Text[1..]);
            }
            else if (value.Kind == ValueKind.Sequence) foreach (var child in value.Items) Find(child);
            else foreach (var child in value.Fields.Values) Find(child);
        }
    }
}


