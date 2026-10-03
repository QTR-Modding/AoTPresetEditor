using AoTPresetEditor;
using YamlDotNet.Core;

internal static class BehaviorFamilyChecks
{
    public static void Run()
    {
        var document = PresetDocument.Parse("formsLists:\n- forms: [FoodBeef]\n  transformers:\n  - duration: 0\n    finalFormEditorID: FoodBeefCooked\n- forms: [FoodApple]\n", "FOOD", PresetKind.Addon);
        var originalRules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        var ruleId = originalRules[0].Id;
        var parameter = BehaviorFamily.MakeVariable(document, ruleId, ["transformers", "0", "duration"]);
        var family = BehaviorFamily.Find(document, ruleId)!;
        Require(family.IsTemplate && family.Rows.Count == 1 && family.Definition.Id != ruleId, "Varying a field must retain the original rule and create its common behavior.");
        Require(BehaviorFamily.GetArgument(document, ruleId, parameter).Text == "0", "Varying zero must preserve zero.");
        Require(document.Blocks.Where(block => block.Kind == BlockKind.Rule).Select(block => block.Id).SequenceEqual(originalRules.Select(block => block.Id)), "Creating a behavior changed rule priority.");
        var duplicate = BehaviorFamily.DuplicateUse(document, ruleId);
        Require(duplicate.Id != ruleId && BehaviorFamily.Find(document, duplicate.Id)!.Definition.Id == family.Definition.Id, "Duplicating a use must retain its common behavior and allocate a new rule ID.");
        BehaviorFamily.SetArgument(document, duplicate.Id, parameter, PresetValue.Scalar("4"));
        Require(BehaviorFamily.GetArgument(document, ruleId, parameter).Text == "0", "Changing one use changed another use.");
        var added = BehaviorFamily.AddUse(document, ruleId);
        Require(BehaviorFamily.GetArgument(document, added.Id, parameter).IsUnsetArgument, "New uses require a value instead of guessing null or zero.");
        Require(document.Blocks.Where(block => block.Kind == BlockKind.Rule).Last().Id == originalRules[1].Id, "Adding uses moved an unrelated rule.");
        Require(BehaviorFamily.RemoveUse(document, added.Id) && BehaviorFamily.RemoveUse(document, duplicate.Id) && !BehaviorFamily.RemoveUse(document, ruleId), "Removing a use must retain the final use.");

        document = PresetDocument.Parse("""
            templates:
              cook:
                forms: $raw
                transformers:
                - duration: $hours
                  finalFormEditorID: $cooked
                duration: $hours
              wrapped:
                <<: cook($raw, $hours, $cooked)
            formsLists:
            - cook(Beef, 6, CookedBeef)
            - forms: Apple
            - cook(Venison, 9, CookedVenison)
            - wrapped(Rabbit, 3, CookedRabbit)
            """, "FOOD", PresetKind.Addon);
        var rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        ruleId = rules[0].Id;
        var templateId = document.Blocks.First(block => block.Key == "cook").Id;
        parameter = BehaviorFamily.MakeVariable(document, ruleId, ["transformers"]);
        family = BehaviorFamily.Find(document, ruleId)!;
        Require(family.Definition.Id == templateId && family.Definition.Key == "cook", "Imported definitions must retain their IDs and names.");
        Require(family.Rows.Select(block => block.Id).SequenceEqual([rules[0].Id, rules[2].Id]), "A behavior must identify matching uses without including intervening rules.");
        Require(BehaviorFamily.GetArgument(document, rules[2].Id, parameter).Items[0].Get("duration")!.Text == "9" &&
            BehaviorFamily.GetArgument(document, rules[2].Id, parameter).Items[0].Get("finalFormEditorID")!.Text == "CookedVenison", "Varying a collection lost that use's existing parameter values.");
        Require(BehaviorFamily.GetArgument(document, rules[2].Id, "hours").Text == "9", "A parameter used outside the varied collection lost its value.");
        var wrapper = document.Blocks.First(block => block.Key == "wrapped");
        Require(TemplateCall.TryParse(wrapper.Value.Get("<<")!.Text, out _, out var wrappedArguments) && wrappedArguments[1].Kind == ValueKind.Sequence, "Nested uses must receive the previous collection as their new argument.");
        Require(BehaviorFamily.GetArgument(document, rules[3].Id, "cooked").Text == "CookedRabbit" && BehaviorFamily.GetArgument(document, rules[3].Id, "hours").Text == "3", "Enclosing parameter order must remain bound by name.");
        BehaviorFamily.SameForAll(document, ruleId, "hours", PresetValue.Scalar("0"));
        Require(!BehaviorFamily.Find(document, ruleId)!.Parameters.Contains("hours") && family.Definition.Value.Get("duration")!.Text == "0", "Making a parameter common must replace it and remove its argument.");
        Require(BehaviorFamily.GetArgument(document, rules[2].Id, parameter).Items[0].Get("duration")!.Text == "9", "Making a separate setting common must preserve independent collection values.");

        document = PresetDocument.Parse("templates:\n  repeat:\n    forms: $value\n    nested: [$value, {0: $value}]\nformsLists:\n- repeat(null)\n- repeat()\n", "FOOD", PresetKind.Addon);
        rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        Require(BehaviorFamily.Find(document, rules[1].Id) is not null && BehaviorFamily.GetArgument(document, rules[1].Id, "value").IsUnsetArgument, "Missing arguments must remain accessible and distinct from explicit null.");
        Require(BehaviorFamily.GetArgument(document, rules[0].Id, "value").IsNull && !BehaviorFamily.GetArgument(document, rules[0].Id, "value").IsUnsetArgument, "Explicit null became an unset value.");
        var emptyText = new PresetValue { Kind = ValueKind.Scalar, Text = "", Style = ScalarStyle.DoubleQuoted };
        BehaviorFamily.SameForAll(document, rules[0].Id, "value", emptyText);
        family = BehaviorFamily.Find(document, rules[0].Id)!;
        Require(family.Parameters.Count == 0 && !family.Definition.Value.Get("forms")!.IsNull &&
            !BehaviorFamily.GetValue(family.Definition.Value, ["nested", "1", "0"])!.IsNull, "Repeated parameters and numeric mapping keys must retain explicitly empty text.");
        var unchanged = document.ToYaml();
        BehaviorFamily.Find(document, rules[0].Id);
        Require(document.ToYaml() == unchanged, "Inspecting a behavior must not modify its authored representation.");

        document = PresetDocument.Parse("stages:\n- no: 0\n  name: Fresh\n  duration: 1\nfinalFormEditorID: Rotten\n", "FOOD", PresetKind.Default);
        ruleId = document.Blocks.Single(block => block.Kind == BlockKind.Rule).Id;
        parameter = BehaviorFamily.MakeVariable(document, ruleId, ["stages", "0", "duration"]);
        document = PresetDocument.Parse(document.ToYaml(), "FOOD", PresetKind.Default);
        family = BehaviorFamily.Find(document, ruleId)!;
        Require(family.IsTemplate && family.Parameters.SequenceEqual([parameter]) && BehaviorFamily.GetArgument(document, ruleId, parameter).Text == "1",
            "Reopening a default preset must retain its common definition and variations through the root merge call.");
        BehaviorFamily.SetArgument(document, ruleId, parameter, PresetValue.Scalar("4"));
        Require(family.Rows.Single().Value.Kind == ValueKind.Mapping && family.Rows.Single().Value.Fields.Count == 1 &&
            BehaviorFamily.GetArgument(document, ruleId, parameter).Text == "4", "Editing a default variation must retain its imported root merge representation.");
        BehaviorFamily.SameForAll(document, ruleId, parameter, PresetValue.Scalar("6"));
        Require(BehaviorFamily.Find(document, ruleId)!.Parameters.Count == 0 && BehaviorFamily.GetValue(family.Definition.Value, ["stages", "0", "duration"])!.Text == "6",
            "Making a reopened default variation common must update its definition.");
        family.Rows.Single().Value.Set("finalFormEditorID", PresetValue.Scalar("AnotherResult"));
        Require(BehaviorFamily.Find(document, ruleId) is { IsTemplate: false }, "A default merge with explicit overrides must remain its own authored behavior.");
        CommonEditsWithMissingArguments();
        LiteralAndAliasValues();
    }

    private static void CommonEditsWithMissingArguments()
    {
        var document = PresetDocument.Parse("templates:\n  partial:\n    first: $a\n    second: $b\n    third: $c\nformsLists:\n- partial(1)\n", "FOOD", PresetKind.Addon);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var value = BehaviorFamily.Find(document, rule.Id)!.Definition.Value.Clone(); value.Remove("first");
        BehaviorFamily.SetCommon(document, rule.Id, value);
        Require(BehaviorFamily.GetArgument(document, rule.Id, "b").IsUnsetArgument && BehaviorFamily.GetArgument(document, rule.Id, "c").IsUnsetArgument,
            "Removing a common parameter must not shift a supplied value into an originally missing argument.");
    }

    private static void LiteralAndAliasValues()
    {
        var document = PresetDocument.Parse("""
            hours: &hours 2
            templates:
              age:
                owners: Beef
                stages:
                - no: 0
                  name: $$Fresh
                  duration: *hours
                finalFormEditorID: Rotten
              wrapper:
                <<: age()
            ownerLists:
            - age()
            - wrapper()
            """, "FOOD", PresetKind.Custom);
        var rule = document.Blocks.First(block => block.Kind == BlockKind.Rule);
        var before = Expand(document);
        var nameParameter = BehaviorFamily.MakeVariable(document, rule.Id, ["stages", "0", "name"]);
        Require(BehaviorFamily.GetArgument(document, rule.Id, nameParameter).Text == "$Fresh" && Expand(document) == before,
            "Moving an escaped template literal into direct and nested uses must retain its native value.");
        var durationParameter = BehaviorFamily.MakeVariable(document, rule.Id, ["stages", "0", "duration"]);
        Require(BehaviorFamily.GetArgument(document, rule.Id, durationParameter).Text == "2" && Expand(document) == before,
            "A varied alias must become an argument value instead of an unresolved anchor inside call text.");
        Require(document.Blocks.Single(block => block.Kind == BlockKind.Shared).Value.Anchor == "hours",
            "Materializing a varied alias must preserve its shared definition.");
        BehaviorFamily.SameForAll(document, rule.Id, nameParameter, PresetValue.Scalar("$Fresh"));
        Require(Expand(document) == before && !BehaviorFamily.Find(document, rule.Id)!.Parameters.Contains("Fresh"),
            "Making a dollar-prefixed value common must retain literal text without adding a parameter.");

        document = PresetDocument.Parse("""
            ownerLists:
            - owners: Beef
              stages:
              - no: 0
                name: $Fresh
                duration: 1
              finalFormEditorID: Rotten
            """, "FOOD", PresetKind.Custom);
        rule = document.Blocks.Single(); before = Expand(document);
        BehaviorFamily.MakeVariable(document, rule.Id, ["stages", "0", "duration"]);
        Require(BehaviorFamily.Find(document, rule.Id)!.Parameters.Count == 1 && Expand(document) == before,
            "Creating a common template must not interpret an ordinary rule's literal dollar-prefixed text as a new parameter.");

        foreach (var label in new[] { "Rule_1()", "echo(Rule_1())" })
        {
            document = PresetDocument.Parse("templates:\n  echo: $text\nownerLists:\n- owners: Beef\n  stages:\n  - no: 0\n    name: " +
                label + "\n    duration: 1\n  finalFormEditorID: Rotten\n", "FOOD", PresetKind.Custom);
            rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule); before = Expand(document);
            BehaviorFamily.MakeVariable(document, rule.Id, ["stages", "0", "duration"]);
            Require(BehaviorFamily.Find(document, rule.Id)!.Definition.Key != "Rule_1" && Expand(document) == before,
                "Automatically naming a common template must not turn existing literal call-shaped labels, including nested arguments, into calls.");
        }
    }

    private static string Expand(PresetDocument document) => PresetCatalog.Expand(document.ToYaml(), CancellationToken.None).GetAwaiter().GetResult();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
