using AoTPresetEditor;

internal static class YamlFormattingChecks
{
    public static void Run()
    {
        CompactLists();
        CompactListReferences();
        const string source = """
            # Shared cooking settings.
            cooking: &cooking
              duration: 0.06 # Game hours
              sound: Crack

            # Portable heat only affects world objects.
            portableHeat: &portableHeat
              <<: *cooking
              containers: Fires

            templates:
              cookFood:
                forms: $raw
                transformers:
                - <<: *cooking
                  finalFormEditorID: $cooked

            # Each pair maps raw food to cooked food.
            formsLists:
            - cookFood(00065C99, 000721E8) # Raw Beef -> Cooked Beef
            - cookFood(FoodVenison, FoodVenisonCooked) # Venison -> Venison Chop
            """;
        var value = PresetYaml.Parse(source);
        var output = PresetYaml.Write(value);
        Require(output.Contains("duration: 0.06 # Game hours"), "A scalar comment moved away from its setting.");
        Require(output.Contains("cookFood(00065C99, 000721E8) # Raw Beef -> Cooked Beef"), "A template-call comment moved away from its rule.");
        Require(output.Contains(Environment.NewLine + "# Portable heat"), "A section comment acquired the previous block's indentation.");
        Require(output.Contains(Environment.NewLine + "# Each pair"), "A rule-list comment acquired the previous template's indentation.");
        Require(output.Contains(Environment.NewLine + Environment.NewLine + "templates:"), "Named sections need breathing room.");
        Require(PresetYaml.Write(PresetYaml.Parse(output)) == output, "Repeated formatted exports are unstable.");
        Require(value.Clone().Get("formsLists")!.Items[0].InlineComment == "Raw Beef -> Cooked Beef", "Cloning lost an inline comment.");

        foreach (var yaml in new[]
        {
            "owners: [Soup, Stew] # Keep this list\nvalue: 0\n",
            "literal: |\n  templates:\n    # This is text\n  formsLists:\n    - still text\nsettings:\n  value: 1\n",
            "map: {first: 1, second: 2} # Keep this map\nlast: 3\n",
            "list:\n- name: Soup\n  duration: 1\n# Keep next section\nnext:\n  name: Stew\n",
            "a: 'value # text'\nb: \"a:b\"\nc: false # Keep false\n"
        })
        {
            var once = PresetYaml.Write(PresetYaml.Parse(yaml));
            var twice = PresetYaml.Write(PresetYaml.Parse(once));
            Require(once == twice, "Comments or formatting changed on a second export: " + yaml);
            var before = PresetYaml.Parse(yaml);
            var after = PresetYaml.Parse(once);
            Require(Semantics(before) == Semantics(after), "Formatting changed a YAML value: " + yaml);
        }
        var inline = PresetYaml.Write(PresetYaml.Parse("owners: [Soup, Stew] # Keep this list\nvalue: 0\n"));
        Require(inline.Contains("[Soup, Stew] # Keep this list"), "A flow-list comment moved.");
        Require(TemplateCall.TryParse(TemplateCall.Format("cook", [PresetValue.Scalar("a,b"), PresetYaml.Parse("{x: 1}")]), out _, out var arguments) && arguments.Count == 2,
            "Section formatting changed template arguments.");
    }

    private static void CompactLists()
    {
        const string source = """
            ownerLists:
            - owners:
              - RawBeef
              - Venison
              containers:
              - 0x7
              stages:
              - no: 0
                name: ""
                duration: 1
              finalFormEditorID: SpicedBeef
              transformers:
              - FormEditorID:
                - FrostSalts
                finalFormEditorID: SpicedBeef
                duration: 0.1
                allowed_stages:
                - 0
                containers:
                - Barrel
                - Chest
              timeModulators:
              - FormEditorID:
                - FireSalts
                - FrostSalts
                - FireSalts
                magnitude: 0
                allowed_stages:
                - 0
                - 2
                containers: []
            """;
        var document = PresetDocument.Parse(source, "FOOD", PresetKind.Custom);
        var draft = document.ToDraft();
        var output = document.ToYaml();
        Require(output.Contains("owners: [RawBeef, Venison]"), "Multiple owners must use an inline list.");
        Require(output.Contains("containers: 0x7") && output.Contains("FormEditorID: FrostSalts") && output.Contains("allowed_stages: 0"),
            "Supported singleton form and stage-number lists must export as scalars, including zero.");
        Require(output.Contains("containers: [Barrel, Chest]") && output.Contains("FormEditorID: [FireSalts, FrostSalts, FireSalts]") &&
            output.Contains("allowed_stages: [0, 2]") && output.Contains("containers: []"),
            "Compact lists must retain value order, duplicates, and empty lists.");
        var rule = PresetYaml.Parse(output).Get("ownerLists")!.Items.Single();
        Require(!rule.Get("stages")!.Flow && !rule.Get("transformers")!.Flow && !rule.Get("timeModulators")!.Flow,
            "Structured settings must remain block lists.");
        Require(document.ToDraft() == draft, "Export formatting mutated the editable document.");
        Require(PresetDocument.Parse(output, "FOOD", PresetKind.Custom).ToYaml() == output, "Compact export must be stable after reopening.");

        var defaults = PresetDocument.Parse("containers: [0x7]\nstages: []\n", "FOOD", PresetKind.Default).ToYaml();
        Require(defaults.Contains("containers: 0x7") && defaults.Contains("stages: []"), "Default preset lists need the same formatting rules.");
        var addon = PresetDocument.Parse("formsLists:\n- forms: [RawBeef]\n", "FOOD", PresetKind.Addon).ToYaml();
        Require(addon.Contains("forms: RawBeef"), "A single selected add-on item must remain a scalar.");
        var annotated = PresetDocument.Parse("formsLists:\n- forms: [RawBeef] # Selected item\n", "FOOD", PresetKind.Addon).ToYaml();
        Require(annotated.Contains("forms: RawBeef # Selected item"), "A singleton list's inline comment must stay with its scalar value.");
    }

    private static void CompactListReferences()
    {
        const string source = """
            selected: &selected [RawBeef]
            sharedHeat: &heat
              FormEditorID:
              - FireSalts
              - FrostSalts
              duration: 0.1
              containers:
              - Barrel # Keep the barrel explanation
              - Chest
            templates:
              cooking:
                forms: [$item]
                transformers:
                - <<: *heat
                  finalFormEditorID: $result
                  allowed_stages: &allowed [0]
                  containers: !!seq [Barrel]
              selectors: [RawBeef]
            formsLists:
            - cooking(RawBeef, SpicedBeef)
            - forms: [selectors()]
              transformers:
              - FormEditorID: [*selected]
                finalFormEditorID: SpicedBeef
                duration: 0.1
                allowed_stages: *allowed
            """;
        var document = PresetDocument.Parse(source, "FOOD", PresetKind.Addon);
        var output = document.ToYaml();
        var root = PresetYaml.Parse(output);
        var template = root.Get("templates")!.Get("cooking")!;
        var transformer = template.Get("transformers")!.Items.Single();
        Require(template.Get("forms")!.Kind == ValueKind.Sequence && template.Get("forms")!.Items.Single().Text == "$item",
            "A variable inside a singleton list must not become the whole list parameter.");
        Require(transformer.Get("allowed_stages") is { Kind: ValueKind.Sequence, Anchor: "allowed" } &&
            transformer.Get("containers") is { Kind: ValueKind.Sequence, Tag: "tag:yaml.org,2002:seq" },
            "Anchored or explicitly tagged singleton lists must retain their type.");
        Require(root.Get("selected")!.Kind == ValueKind.Sequence && root.Get("formsLists")!.Items[1].Get("forms")!.Kind == ValueKind.Sequence,
            "Shared values and template calls must retain their collection shape.");
        Require(!root.Get("formsLists")!.Flow && !root.Get("sharedHeat")!.Get("containers")!.Flow &&
            output.Contains("# Keep the barrel explanation") && output.Contains("<<: *heat") && output.Contains("allowed_stages: *allowed"),
            "Keep rule calls multiline and preserve comments, anchors, and aliases.");
        Require(output.Contains("FormEditorID: [FireSalts, FrostSalts]"), "Shared trigger settings need compact form lists too.");
        Require(PresetDocument.Parse(output, "FOOD", PresetKind.Addon).ToYaml() == output, "Annotated compact output must be stable after reopening.");
        foreach (var setting in new[]
        {
            "FormEditorID: [FireSalts, FrostSalts] # Shared trigger list\n",
            "FormEditorID:\n- 'first\n\n  second'\n- FireSalts\n",
            "FormEditorID:\n- FireSalts # Fire\n- FrostSalts # Frost\n"
        })
        {
            var annotated = PresetDocument.Create("FOOD", PresetKind.Addon);
            annotated.Add(BlockKind.Shared, "Annotated", "transformer").Value = PresetYaml.Parse(setting);
            var formatted = annotated.ToYaml();
            Require(Semantics(PresetYaml.Parse(setting)) == Semantics(PresetYaml.Parse(formatted).Get("Annotated")!),
                "Compact export changed a commented or multiline scalar.");
            Require(PresetDocument.Parse(formatted, "FOOD", PresetKind.Addon).ToYaml() == formatted,
                "Annotated scalar formatting must be stable after reopening.");
        }
        var partial = PresetDocument.Parse("""
            templates:
              stageOverride:
                stages:
                - duration: $hours
                  FormEditorID: [FireSalts]
            ownerLists: []
            """, "FOOD", PresetKind.Custom).ToYaml();
        Require(PresetYaml.Parse(partial).Get("templates")!.Get("stageOverride")!.Get("stages")!.Items.Single().Get("FormEditorID")!.Kind == ValueKind.Sequence,
            "Known stage context must be retained for partial template entries; export must not disguise an invalid stage-item list as a trigger.");
        var before = PresetCatalog.Expand(source, CancellationToken.None).GetAwaiter().GetResult();
        var after = PresetCatalog.Expand(output, CancellationToken.None).GetAwaiter().GetResult();
        Require(Semantics(PresetYaml.Parse(before)) == Semantics(PresetYaml.Parse(after)),
            "Formatting changed native template or alias expansion.");
    }

    private static string Semantics(PresetValue value) => value.Kind switch
    {
        ValueKind.Mapping => "{" + string.Join(',', value.Fields.Select(field => field.Key + ":" + Semantics(field.Value))) + "}",
        ValueKind.Sequence => "[" + string.Join(',', value.Items.Select(Semantics)) + "]",
        _ => value.Kind + ":" + value.Tag + ":" + value.Text
    };
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
