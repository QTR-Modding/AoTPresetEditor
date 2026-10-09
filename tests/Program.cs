using System.IO;
using System.Diagnostics;
using System.Text;
using AoTPresetEditor;
using YamlDotNet.Core;

internal static class Program
{
    private static string helper = "";
    private static int passed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tests -- <AoTYaml.exe> [preset directory]");
            return 1;
        }
        helper = Path.GetFullPath(args[0]);
        var localHelper = Path.Combine(AppContext.BaseDirectory, "AoTYaml.exe");
        if (!helper.Equals(localHelper, StringComparison.OrdinalIgnoreCase)) File.Copy(helper, localHelper, true);
        try
        {
            Check("Template and anchor round trip", TemplateRoundTrip);
            Check("Empty, null, zero and false values", ScalarSemantics);
            Check("Inline flow comments", FlowComments);
            Check("YAML comment placement and section formatting", YamlFormattingChecks.Run);
            Check("YAML preview highlighting and folding", YamlPreviewChecks.Run);
            Check("Names and clone isolation", NamesAndClone);
            Check("Unfinished preset drafts", PresetDraftChecks.Run);
            Check("Preset summaries and contextual pickers", PresetPresentationChecks.Run);
            Check("Forward authoring and rule priority", ForwardAuthoring);
            Check("Default root merge", DefaultMerge);
            Check("AoT schema boundary values", SchemaBoundaries);
            Check("Whole rule list references", WholeRuleList);
            Check("Missing and circular aliases", InvalidAliases);
            Check("Template arguments and parameter order", TemplateArguments);
            Check("Template edits preserve argument meaning", RebindArguments);
            Check("Behavior variables and ordered uses", BehaviorFamilyChecks.Run);
            Check("Detached template argument binding", DetachedTemplateChecks.Run);
            Check("New optional arguments require a choice", UnsetArguments);
            Check("Duplicate keys and multiple documents", InvalidYaml);
            Check("Editor interactions", EditorInteractionChecks.Run);
            Check("Behavior editing and variation controls", BehaviorEditorChecks.Run);
            Check("Behavior workspace and mixed editing", BehaviorWorkspaceChecks.Run);
            Check("Save to mod keyboard shortcut", SaveShortcutChecks.Run);
            Check("Form group files and draft isolation", FormGroupChecks.Run);
            Check("Form applicability", FormSelectionChecks.Run);
            Check("Tint picker and color encoding", TintPickerChecks.Run);
            Check("Sound playback controls and asset selection", SoundPreviewChecks.Run);
            Check("Form model metadata and preview controls", ModelPreviewChecks.Run);
            Check("Preview access in lists and groups", PreviewAccessChecks.Run);
            Check("Preset and companion export rollback", PresetExportChecks.Run);
            Check("Mixed behavior projects and export rollback", PresetProjectChecks.Run);
            Check("Folder and multiple preset opening", PresetOpenChecks.Run);
            Check("Stage dependency warnings", StageAvailabilityChecks.Run);
            Check("Unified behavior editing", UnifiedBehaviorChecks.Run);
            Check("Template field constraints and group dialog drafts", SchemaPickerChecks.Run);
            Check("Appearance preference persistence", AppearanceSettingsChecks.Run);
            Check("Embedded wiki navigation preserves editing", WikiChecks.Run);
            Check("Enter in creation dialogs", CreationDialogChecks.Run);
            if (args.Length > 1) Check("Installed preset round trips", () => PresetDirectory(args[1]));
            Check("Live editor themes and preview cancellation", ThemeChecks.Run);
            Console.WriteLine($"Passed {passed} checks.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(string name, Action action)
    {
        action();
        passed++;
        Console.WriteLine("PASS " + name);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or YamlException) { return; }
        throw new Exception("An invalid document was accepted.");
    }

    private static void TemplateRoundTrip()
    {
        const string yaml = """
            # Shared cooking settings.
            cooking: &cooking
              FormEditorID: qAoTFireMain
              duration: 0.06
              sound: 0x1~quantAoTBBQ.esp
            portable: &portable
              <<: *cooking
              FormEditorID: qAoTFireInv
              containers: qAoTFireInv
            templates:
              cookFood:
                forms: $raw
                transformers:
                - <<: *cooking
                  finalFormEditorID: $cooked
                - <<: *portable
                  finalFormEditorID: $cooked
            formsLists:
            - cookFood(00065C99, 000721E8) # Beef
            - cookFood(00065C9A, 0007224C) # Goat
            """;
        var document = PresetDocument.Parse(yaml, "FOOD", PresetKind.Addon);
        var output = document.ToYaml();
        Require(Equivalent(yaml, output), "Template expansion changed.");
        Require(output.Contains("&cooking") && output.Contains("*cooking") && output.Contains("cookFood(00065C99"), "Reusable structure was flattened.");
        Require(PresetDocument.Parse(output, "FOOD", PresetKind.Addon).ToYaml() == output, "Repeated exports are unstable.");
    }
    private static void ScalarSemantics()
    {
        const string yaml = "absent:\nnull: null\ntext: \"null\"\nzero: 0\nfalse: false\nlist: []\nmap: {}\nliteral: !!str 'call(a)'\n";
        var value = PresetYaml.Parse(yaml);
        Require(value.Get("absent")!.IsNull && !value.Get("text")!.IsNull, "Null and text are confused.");
        Require(Equivalent(yaml, PresetYaml.Write(value)), "Scalar types changed on export.");
        ExpandedStringFormatting();
    }
    private static void ExpandedStringFormatting()
    {
        var output = Expand("""
            templates:
              cook: {item: $raw}
              identity: $value
            stages:
            - no: 0
              name: ""
              duration: 1
            quoted_null: "null"
            null_value: null
            quoted_bool: "false"
            bool_value: false
            quoted_number: "12"
            number_value: 12
            quoted_zeros: "001"
            canonical_text: !!str ordinary
            multiline: |-
              first
              second
            escapes: "quote: \"yes\"; path: C:\\test"
            dollar: identity("$literal")
            "<<": "literal merge key"
            explicit_number: !!int 3
            custom: !author text
            custom_list: !author [one, two]
            literal_call: !!str 'cook(raw)'
            unknown_call: "later(raw)"
            incomplete_call: !!str 'later('
            known_call: "cook(raw)"
            """);
        Require(output.Contains("name: \"\""), "An empty expanded stage name must be written as name: \"\".");
        var value = PresetYaml.Parse(output);
        var stageName = value.Get("stages")!.Items.Single().Get("name")!;
        Require(stageName.Text.Length == 0 &&
            stageName.Style == ScalarStyle.DoubleQuoted && stageName.Tag.Length == 0 && !stageName.IsNull,
            "An empty expanded stage name must be an ordinary quoted string without an explicit type tag.");
        foreach (var (key, expected) in new[] { ("quoted_null", "null"), ("quoted_bool", "false"),
            ("quoted_number", "12"), ("quoted_zeros", "001"), ("canonical_text", "ordinary"),
            ("multiline", "first\nsecond"), ("escapes", "quote: \"yes\"; path: C:\\test") })
        {
            var scalar = value.Get(key)!;
            Require(scalar.Text == expected && scalar.Style == ScalarStyle.DoubleQuoted && scalar.Tag.Length == 0 && !scalar.IsNull,
                "Expanded string formatting must preserve the value and string type of " + key + " without a redundant tag.");
        }
        Require(value.Get("null_value")!.IsNull && value.Get("bool_value") is { Text: "false", Style: ScalarStyle.Plain, Tag.Length: 0 } &&
            value.Get("number_value") is { Text: "12", Style: ScalarStyle.Plain, Tag.Length: 0 },
            "Quoting an expanded string must not change the following implicit null, boolean, or number.");
        Require(value.Get("dollar")!.Text == "$literal" && value.Get("<<")!.Text == "literal merge key" &&
            value.Get("known_call")!.Get("item")!.Text == "raw",
            "Formatting must preserve substituted dollar text, quoted merge keys, and expansion of known quoted calls.");
        Require(value.Get("explicit_number")!.Tag == "tag:yaml.org,2002:int" &&
            value.Get("custom")!.Tag == "!author" && value.Get("custom_list")!.Tag == "!author",
            "Expanded output must retain explicit non-string and custom tags.");
        foreach (var key in new[] { "literal_call", "unknown_call", "incomplete_call" })
            Require(value.Get(key)!.Tag == "tag:yaml.org,2002:str",
                "Call-shaped expanded strings must retain their existing literal protection: " + key);
        var repeated = PresetYaml.Parse(Expand("templates:\n  cook: {rewritten: yes}\n  later: {rewritten: yes}\n" + output));
        Require(repeated.Get("literal_call")!.Text == "cook(raw)" && repeated.Get("unknown_call")!.Text == "later(raw)" &&
            repeated.Get("incomplete_call")!.Text == "later(",
            "Literal and previously unknown call-shaped strings must stay literal when matching templates are later introduced.");
    }
    private static void FlowComments()
    {
        const string yaml = "owners: [Soup, Stew] # keep this explanation\nvalue: 0\n";
        var output = PresetYaml.Write(PresetYaml.Parse(yaml));
        Require(Equivalent(yaml, output) && output.Contains("keep this explanation"), "Flow-list comments were lost.");
        Require(PresetYaml.Write(PresetYaml.Parse("# comment only\n")).Contains("comment only"), "A comment-only file lost its contents.");
    }
    private static void NamesAndClone()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        document.Add(BlockKind.Shared, "Shared sound settings", "Transformer");
        document.Add(BlockKind.Template, "My template", "CustomRule");
        document.Add(BlockKind.Rule, "Venison changes", "CustomRule");
        var saved = PresetDocument.Parse(document.ToYaml(), "FOOD", PresetKind.Custom);
        Require(saved.Blocks.Select(block => block.Name).SequenceEqual(document.Blocks.Select(block => block.Name)), "Block names were lost.");
        var copy = document.Clone();
        copy.Blocks[0].Value.Set("duration", PresetValue.Scalar("6"));
        Require(document.Blocks[0].Value.Get("duration") is null, "A clone changed the original.");
    }
    private static void ForwardAuthoring()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var first = document.Add(BlockKind.Rule, "First", "CustomRule");
        var second = document.Add(BlockKind.Rule, "Second", "CustomRule");
        var shared = document.Add(BlockKind.Shared, "Stages added later", "Stages");
        shared.Value = PresetValue.List();
        shared.Value.Anchor = shared.Key;
        first.Value.Set("owners", PresetValue.Scalar("first"));
        first.Value.Set("stages", PresetValue.Alias(shared.Key));
        second.Value.Set("owners", PresetValue.Scalar("second"));
        var output = PresetYaml.Parse(Expand(document.ToYaml()));
        Require(output.Get("ownerLists")!.Items.Select(item => item.Get("owners")!.Text).SequenceEqual(["first", "second"]), "Rule priority changed.");
        Require(output.Get("ownerLists")!.Items[0].Get("stages")!.Kind == ValueKind.Sequence, "A later shared block was not resolved.");
    }
    private static void DefaultMerge()
    {
        const string yaml = "settings: &settings\n  stages: []\n<<: *settings\n";
        var document = PresetDocument.Parse(yaml, "FOOD", PresetKind.Default);
        Require(document.Blocks.Count(block => block.Kind == BlockKind.Rule) == 1 && Equivalent(yaml, document.ToYaml()), "The default rule merge was lost.");
        var authored = PresetDocument.Create("FOOD", PresetKind.Default);
        var rule = authored.Add(BlockKind.Rule, "Use settings", "DefaultRule");
        var shared = authored.Add(BlockKind.Shared, "settings", "DefaultRule");
        shared.Value.Set("stages", PresetValue.List());
        rule.Value = PresetValue.Alias(shared.Key);
        Require(Equivalent(yaml, authored.ToYaml()), "A whole default-rule reference was not emitted as a merge.");
        var template = authored.Add(BlockKind.Template, "defaultSettings", "DefaultRule");
        template.Value.Set("stages", PresetValue.List());
        rule.Value = PresetValue.Scalar(template.Key + "()");
        Require(Equivalent(yaml, authored.ToYaml()), "A whole default-rule template call was not emitted as a merge.");
    }
    private static void SchemaBoundaries()
    {
        var named = PresetYaml.Parse("no: 0\nname: null\nduration: 1\n");
        Require(PresetSchema.Validate(named, "stage", "FOOD").Count == 0, "An explicit null stage name was rejected.");
        named.Remove("name");
        Require(PresetSchema.Validate(named, "stage", "FOOD").Count > 0, "A missing stage name was accepted.");
        var rule = PresetYaml.Parse("owners: FoodBeef\nstages:\n- no: 0\n  name: null\n  duration: 1\nfinalFormEditorID: FoodBeef\ntransformers:\n- FormEditorID: fire\n  finalFormEditorID: FoodBeef\n  duration: 1\n  allowed_stages: [0]\n");
        Require(PresetSchema.Validate(rule, "CustomRule", "FOOD").Count == 0, "Stage zero was rejected.");
        rule.Get("transformers")!.Items[0].Get("allowed_stages")!.Items[0].Text = "1";
        Require(PresetSchema.Validate(rule, "CustomRule", "FOOD").Count > 0, "An undefined stage was accepted.");
        var derivative = PresetYaml.Parse("no: 1\nname: Changed\nduration: 1\n");
        rule.Remove("transformers");
        rule.Get("stages")!.Items.Add(derivative);
        Require(PresetSchema.Validate(rule, "CustomRule", "FOOD").Count == 0 && PresetSchema.Validate(rule, "CustomRule", "BOOK").Count > 0, "Dynamic item category restrictions changed.");
        var effect = PresetYaml.Parse("FormEditorID: null\n");
        Require(PresetSchema.Validate(effect, "effect", "FOOD").Count == 0, "Clearing an effect was rejected.");
        effect.Set("FormEditorID", PresetValue.Scalar("Effect"));
        Require(PresetSchema.Validate(effect, "effect", "FOOD").Count > 0, "A real effect without magnitude was accepted.");
        effect.Set("FormEditorID", PresetValue.Scalar("0x0"));
        effect.Set("magnitude", PresetValue.Scalar("unused"));
        Require(PresetSchema.Validate(effect, "effect", "FOOD").Count == 0, "A zero effect ID must ignore the numerical fields, as AoT does.");
        derivative.Set("FormEditorID", PresetValue.Scalar("0x0"));
        Require(PresetSchema.Validate(derivative, "stage", "FOOD").Count > 0, "An explicit zero stage ID must fail even when the item could be generated.");
        rule.Set("finalFormEditorID", PresetValue.Scalar("0"));
        Require(PresetSchema.Validate(rule, "CustomRule", "FOOD").Any(error => error.Contains("Final item")), "A required result cannot be form zero.");
    }
    private static void WholeRuleList()
    {
        const string yaml = "rules: &rules\n- owners: FoodBeef\n  stages: []\nownerLists: *rules\n";
        var document = PresetDocument.Parse(yaml, "FOOD", PresetKind.Custom);
        Require(document.Blocks.Single(block => block.Kind == BlockKind.Rule).Context == "Rules", "A whole list became an ordinary rule.");
        Require(Equivalent(yaml, document.ToYaml()), "Whole-list alias changed.");
        Reject(() => document.Add(BlockKind.Rule, "Extra", "CustomRule"));
        const string call = "templates:\n  rules:\n  - owners: $food\n    stages: []\nownerLists: rules(FoodBeef)\n";
        Require(Equivalent(call, PresetDocument.Parse(call, "FOOD", PresetKind.Custom).ToYaml()), "Whole-list template call changed.");
    }
    private static void InvalidAliases()
    {
        foreach (var yaml in new[] { "a: *missing\n", "a: &a {x: *a}\n", "a: &a {x: *b}\nb: &b {x: *a}\n" })
            Reject(() => PresetDocument.Parse(yaml, "FOOD", PresetKind.Custom).ToYaml());
    }
    private static void TemplateArguments()
    {
        Require(TemplateCall.TryParse("hello([a,b], {a: 2}, 'x,y')", out var name, out var args) && args.Count == 3, "YAML arguments did not parse.");
        Require(TemplateCall.TryParse(TemplateCall.Format(name, args), out _, out var restored) && restored.Count == 3, "Formatted arguments changed.");
        var template = PresetYaml.Parse("a: $first\nb: $second\nc: $first\nd: $$literal\ne: other($third)\n");
        Require(TemplateCall.FindParameters(template).SequenceEqual(["first", "second", "third"]), "Parameter order changed.");
        var document = PresetDocument.Parse("settings:\n  duration: &duration $hours\ntemplates:\n  effect:\n    duration: *duration\n", "FOOD", PresetKind.Custom);
        Require(TemplateCall.FindParameters(document.Blocks.Single(block => block.Kind == BlockKind.Template).Value, document).SequenceEqual(["hours"]), "A parameter in a nested anchor was missed.");
    }
    private static void RebindArguments()
    {
        const string yaml = "templates:\n  cook:\n    forms: $raw\n    duration: $hours\nformsLists:\n- cook(FoodBeef, 6)\n";
        var document = PresetDocument.Parse(yaml, "FOOD", PresetKind.Addon);
        var previous = document.Clone();
        var template = document.Blocks.Single(block => block.Kind == BlockKind.Template);
        var ordered = template.Value.Fields.Reverse().ToArray();
        template.Value.Fields.Clear();
        foreach (var entry in ordered) template.Value.Set(entry.Key, entry.Value);
        document.RebindTemplateCalls(previous);
        var call = document.Blocks.Single(block => block.Kind == BlockKind.Rule).Value;
        Require(TemplateCall.TryParse(call.Text, out _, out var reordered) && reordered[0].Text == "6" && reordered[1].Text == "FoodBeef", "Reordered parameters reinterpreted arguments.");
        previous = document.Clone();
        template.Value.Set("sound", PresetValue.Scalar("$sound"));
        document.RebindTemplateCalls(previous);
        Require(TemplateCall.TryParse(call.Text, out _, out var added) && added.Count == 3 && added[2].IsUnsetArgument, "A new parameter did not leave an unset argument.");
        previous = document.Clone();
        call.Text = "cook(FoodBeef)";
        template.Value.Remove("sound");
        document.RebindTemplateCalls(previous);
        Require(call.Text == "cook(FoodBeef)", "A malformed call was silently repaired.");
        const string nested = "templates:\n  inner:\n    first: $a\n    second: $b\n  outer:\n    x: inner($a, $b)\nformsLists:\n- outer(1, 2)\n";
        document = PresetDocument.Parse(nested, "FOOD", PresetKind.Addon);
        previous = document.Clone();
        document.Blocks.Single(block => block.Key == "inner").Value.Remove("first");
        document.RebindTemplateCalls(previous);
        call = document.Blocks.Single(block => block.Kind == BlockKind.Rule).Value;
        Require(TemplateCall.TryParse(call.Text, out _, out var nestedArgs) && nestedArgs.Count == 1 && nestedArgs[0].Text == "2", "Nested template signature changes lost their argument binding.");
    }
    private static void UnsetArguments()
    {
        const string yaml = "templates:\n  cook:\n    forms: $raw\n    transformers:\n    - FormEditorID: fire\n      finalFormEditorID: FoodBeefCooked\n      duration: 1\nformsLists:\n- cook(FoodBeef)\n";
        var document = PresetDocument.Parse(yaml, "FOOD", PresetKind.Addon);
        Require(PresetValidation.Check(document, CancellationToken.None).GetAwaiter().GetResult().IsValid, "The original rule is invalid.");
        var previous = document.Clone();
        document.Blocks.Single(block => block.Kind == BlockKind.Template).Value.Get("transformers")!.Items[0].Set("sound", PresetValue.Scalar("$sound"));
        document.RebindTemplateCalls(previous);
        Require(!PresetValidation.Check(document, CancellationToken.None).GetAwaiter().GetResult().IsValid, "A newly added optional argument silently accepted null.");
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        Require(TemplateCall.TryParse(rule.Value.Text, out var name, out var arguments) && arguments[1].IsUnsetArgument, "The unset marker was lost.");
        arguments[1] = PresetValue.Scalar("null");
        rule.Value.Text = TemplateCall.Format(name, arguments);
        Require(PresetValidation.Check(document, CancellationToken.None).GetAwaiter().GetResult().IsValid, "An explicitly chosen null argument was rejected.");
    }
    private static void InvalidYaml()
    {
        Reject(() => PresetYaml.Parse("a: 1\na: 2\n"));
        Reject(() => PresetYaml.Parse("a: 1\n---\nb: 2\n"));
    }
    private static void PresetDirectory(string folder)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.yml", SearchOption.AllDirectories))
        {
            var yaml = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(yaml)) continue;
            var parts = Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar);
            var kind = parts.Contains("addon") ? PresetKind.Addon : parts.Contains("custom") ? PresetKind.Custom : PresetKind.Default;
            var document = PresetDocument.Parse(yaml, "FOOD", kind);
            Require(Equivalent(yaml, document.ToYaml()), "Changed preset: " + file);
            count++;
        }
        Console.WriteLine($"Compared {count} presets.");
    }
    private static bool Equivalent(string left, string right) => Canonical(PresetYaml.Parse(Expand(left))) == Canonical(PresetYaml.Parse(Expand(right)));
    private static string Canonical(PresetValue value) => value.Kind switch
    {
        ValueKind.Mapping => "{" + string.Join(",", value.Fields.OrderBy(field => field.Key, StringComparer.Ordinal).Select(field => field.Key + ":" + Canonical(field.Value))) + "}",
        ValueKind.Sequence => "[" + string.Join(",", value.Items.Select(Canonical)) + "]",
        _ => value.IsNull ? "null" : value.Tag + ":" + value.Text
    };
    private static string Expand(string yaml)
    {
        using var process = Process.Start(new ProcessStartInfo(helper)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        }) ?? throw new IOException("Could not start QTR YAML helper.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(yaml);
        process.StandardInput.Close();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidDataException(error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }
}
