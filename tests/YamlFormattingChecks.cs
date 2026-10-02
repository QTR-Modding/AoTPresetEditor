using AoTPresetEditor;

internal static class YamlFormattingChecks
{
    public static void Run()
    {
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
