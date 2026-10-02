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
        var comments = new List<Comment>();
        while (parser.MoveNext())
        {
            if (parser.Current is Comment comment) comments.Add(comment);
            else if (parser.Current is { } current) events.Add(current);
        }
        return new ValueReader(events, comments).ReadDocument();
    }

    private sealed class ValueReader(List<ParsingEvent> events, List<Comment> comments)
    {
        private int position;
        private readonly List<NodeSpan> spans = [];
        private ParsingEvent? Current => position < events.Count ? events[position] : null;
        private sealed class NodeSpan(PresetValue value, Mark start, Mark end)
        {
            public PresetValue Value { get; } = value;
            public Mark Start { get; set; } = start;
            public Mark End { get; } = end;
            public long Column { get; set; } = start.Column;
        }

        public PresetValue ReadDocument()
        {
            if (Current is StreamStart) position++;
            if (Current is StreamEnd or null)
            {
                var empty = PresetValue.Map();
                empty.Comments.AddRange(comments.Select(comment => comment.Value));
                return empty;
            }
            if (Current is not DocumentStart) throw new InvalidDataException("Expected a YAML document.");
            position++;
            var value = ReadValue(0);
            if (Current is DocumentEnd) position++;
            if (Current is not (StreamEnd or null)) throw new InvalidDataException("A preset file must contain one YAML document.");
            AttachComments(value);
            return value;
        }

        private PresetValue ReadValue(int depth)
        {
            if (depth > MaximumDepth) throw new InvalidDataException("YAML nesting is too deep.");
            var start = Current?.Start ?? Mark.Empty;
            var end = Current?.End ?? Mark.Empty;
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
                    while (Current is not MappingEnd)
                    {
                        if (Current is not Scalar key || !key.Anchor.IsEmpty || !key.Tag.IsEmpty)
                            throw new InvalidDataException("Preset setting names must be plain text keys.");
                        position++;
                        var child = ReadValue(depth + 1);
                        spans[^1].Start = key.Start;
                        spans[^1].Column = key.Start.Column;
                        if (!value.Fields.TryAdd(key.Value, child)) throw new InvalidDataException($"Duplicate setting '{key.Value}'.");
                    }
                    end = Current.End;
                    position++;
                    break;
                case SequenceStart sequence:
                    value = PresetValue.List();
                    value.Anchor = sequence.Anchor.IsEmpty ? "" : sequence.Anchor.Value;
                    value.Tag = sequence.Tag.IsEmpty ? "" : sequence.Tag.Value;
                    value.Flow = sequence.Style == SequenceStyle.Flow;
                    position++;
                    while (Current is not SequenceEnd)
                    {
                        value.Items.Add(ReadValue(depth + 1));
                        if (!value.Flow) spans[^1].Column = sequence.Start.Column;
                    }
                    end = Current.End;
                    position++;
                    break;
                default: throw new InvalidDataException("Expected a YAML setting value.");
            }
            spans.Add(new NodeSpan(value, start, end));
            return value;
        }

        private void AttachComments(PresetValue root)
        {
            foreach (var comment in comments)
            {
                if (comment.End.Index <= spans[^1].Start.Index)
                {
                    root.Comments.Add(comment.Value);
                    continue;
                }
                var previous = comment.IsInline ? spans
                    .Where(span => span.End.Line == comment.Start.Line && span.End.Index <= comment.Start.Index)
                    .OrderByDescending(span => span.End.Index).ThenBy(span => span.Start.Index).FirstOrDefault() : null;
                if (previous is not null)
                {
                    previous.Value.InlineComment = comment.Value;
                    continue;
                }
                var header = comment.IsInline ? spans.Where(span => span.Start.Line == comment.Start.Line &&
                    span.Start.Index <= comment.Start.Index && span.End.Index > comment.Start.Index)
                    .OrderByDescending(span => span.Start.Index).FirstOrDefault() : null;
                if (header is not null)
                {
                    header.Value.Comments.Add(comment.Value);
                    continue;
                }
                var next = spans.Where(span => span.Value != root && span.Start.Index >= comment.End.Index && span.Column <= comment.Start.Column)
                    .OrderBy(span => span.Start.Index).ThenBy(span => span.Column).FirstOrDefault();
                if (next is not null) next.Value.Comments.Add(comment.Value);
                else root.TrailingComments.Add(comment.Value);
            }
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
        return flow ? writer.ToString() : SpaceSections(writer.ToString());
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
        if (value.InlineComment.Length > 0) emitter.Emit(new Comment(value.InlineComment, true));
        if (value.Kind is ValueKind.Scalar or ValueKind.Alias) EmitComments(emitter, value.TrailingComments);
    }

    private static string SpaceSections(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var sections = new HashSet<int>();
        var parser = new Parser(new StringReader(yaml));
        var depth = 0;
        var isRootKey = true;
        var rootKeyLine = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case MappingStart or SequenceStart:
                    if (depth == 1)
                    {
                        var line = rootKeyLine;
                        while (line > 0 && lines[line - 1].StartsWith('#')) line--;
                        if (line > 0) sections.Add(line);
                        isRootKey = true;
                    }
                    depth++;
                    break;
                case MappingEnd or SequenceEnd:
                    depth--;
                    break;
                case Scalar scalar when depth == 1:
                    if (isRootKey) rootKeyLine = (int)scalar.Start.Line - 1;
                    isRootKey = !isRootKey;
                    break;
                case AnchorAlias when depth == 1:
                    isRootKey = true;
                    break;
            }
        }
        return string.Join(Environment.NewLine, lines.SelectMany((line, index) =>
            sections.Contains(index) && lines[index - 1].Length > 0 ? new[] { "", line } : new[] { line }));
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


