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
        BehaviorFamily.ClearVariable(document, ruleId, "hours");
        Require(!BehaviorFamily.Find(document, ruleId)!.Parameters.Contains("hours") && family.Definition.Value.Get("duration") is null, "Clearing a parameter must remove its field and argument.");
        Require(BehaviorFamily.GetArgument(document, rules[2].Id, parameter).Items[0].Get("duration")!.Text == "9", "Clearing a separate setting must preserve independent collection values.");

        document = PresetDocument.Parse("templates:\n  repeat:\n    forms: $value\n    nested: [$value, {0: $value}]\nformsLists:\n- repeat(null)\n- repeat()\n", "FOOD", PresetKind.Addon);
        rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        Require(BehaviorFamily.Find(document, rules[1].Id) is not null && BehaviorFamily.GetArgument(document, rules[1].Id, "value").IsUnsetArgument, "Missing arguments must remain accessible and distinct from explicit null.");
        Require(BehaviorFamily.GetArgument(document, rules[0].Id, "value").IsNull && !BehaviorFamily.GetArgument(document, rules[0].Id, "value").IsUnsetArgument, "Explicit null became an unset value.");
        var emptyText = new PresetValue { Kind = ValueKind.Scalar, Text = "", Style = ScalarStyle.DoubleQuoted };
        BehaviorFamily.SetArgument(document, rules[0].Id, "value", emptyText);
        Require(BehaviorFamily.GetArgument(document, rules[0].Id, "value") is { IsNull: false, Text: "" },
            "An explicitly empty text argument must remain distinct from null and unset.");
        BehaviorFamily.ClearVariable(document, rules[0].Id, "value");
        family = BehaviorFamily.Find(document, rules[0].Id)!;
        Require(family.Parameters.Count == 0 && family.Definition.Value.Get("forms") is null &&
            family.Definition.Value.Get("nested")!.Items.Count == 1 &&
            BehaviorFamily.GetValue(family.Definition.Value, ["nested", "0"])!.Fields.Count == 0,
            "Clearing repeated parameters must remove their fields and sequence entries, including numeric mapping keys.");
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
        var overridden = document.Clone();
        overridden.Blocks.Single(block => block.Kind == BlockKind.Rule).Value.Set("finalFormEditorID", PresetValue.Scalar("AnotherResult"));
        Require(BehaviorFamily.Find(overridden, ruleId) is { IsTemplate: false }, "A default merge with explicit overrides must remain its own authored behavior.");
        BehaviorFamily.ClearVariable(document, ruleId, parameter);
        family = BehaviorFamily.Find(document, ruleId)!;
        Require(!family.IsTemplate && BehaviorFamily.GetValue(family.Definition.Value, ["stages", "0", "duration"]) is null,
            "Clearing a reopened default's final variable must unwrap its rule and remove the setting.");
        var common = family.Definition.Value.Clone();
        BehaviorFamily.GetValue(common, ["stages", "0"])!.Set("duration", PresetValue.Scalar("6"));
        BehaviorFamily.SetCommon(document, ruleId, common);
        Require(BehaviorFamily.GetValue(family.Definition.Value, ["stages", "0", "duration"])!.Text == "6",
            "A cleared default setting must accept a newly entered common value.");
        CommonEditsWithMissingArguments();
        LiteralAndAliasValues();
        NamedVariables();
        ClearVariables();
        SafeNormalization();
        CompactSelectors();
    }

    private static void NamedVariables()
    {
        var document = PresetDocument.Parse("formsLists:\n- forms: [Beef]\n  containers: []\n", "FOOD", PresetKind.Addon);
        var rule = document.Blocks.Single();
        Require(BehaviorFamily.MakeVariable(document, rule.Id, ["forms"], "ingredient") == "ingredient" &&
            BehaviorFamily.GetArgument(document, rule.Id, "ingredient") is { Kind: ValueKind.Scalar, Text: "Beef" },
            "Named owner variables must preserve the selected item as a scalar argument.");
        var unchanged = document.ToDraft();
        foreach (var invalid in new[] { "ingredient", "", "$time", "cook time", "time()" })
        {
            try { BehaviorFamily.MakeVariable(document, rule.Id, ["containers"], invalid); }
            catch (InvalidOperationException)
            {
                Require(document.ToDraft() == unchanged, "Rejecting a variable name must leave the behavior unchanged.");
                continue;
            }
            throw new InvalidOperationException("A duplicate or invalid variable name was accepted.");
        }
        BehaviorFamily.MakeVariable(document, rule.Id, ["transformers"], "changes", PresetValue.List());
        Require(BehaviorFamily.GetArgument(document, rule.Id, "changes") is { Kind: ValueKind.Sequence, Items.Count: 0 },
            "An absent field must accept the supplied initial value without replacing it with null.");
        BehaviorFamily.MakeVariable(document, rule.Id, ["newSetting"], "unfilled");
        Require(BehaviorFamily.GetArgument(document, rule.Id, "unfilled").IsUnsetArgument,
            "A variable created without an initial value must remain unfilled.");
    }

    private static void ClearVariables()
    {
        var document = PresetDocument.Parse("""
            templates:
              cook:
                forms: $item
                transformers:
                - FormEditorID: Fire
                  finalFormEditorID: Result
                  duration: $hours # preserve this note
                timeModulators:
                - FormEditorID: Cold
                  magnitude: $hours
            formsLists:
            - cook(Beef, 2)
            - cook(Venison, 9)
            """, "FOOD", PresetKind.Addon);
        var rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        var selected = BehaviorFamily.ClearVariable(document, rules[1].Id, "hours");
        var family = BehaviorFamily.Find(document, selected)!;
        Require(selected == rules[1].Id && family.Parameters.SequenceEqual(["item"]) &&
            family.Definition.Value.Get("transformers")!.Items[0].Get("duration") is null &&
            family.Definition.Value.Get("timeModulators")!.Items[0].Get("magnitude") is null,
            "Unchecking a variable must clear every corresponding field and preserve remaining columns.");
        Require(BehaviorFamily.GetArgument(document, rules[0].Id, "item").Text == "Beef" &&
            BehaviorFamily.GetArgument(document, rules[1].Id, "item").Text == "Venison" && document.ToYaml().Contains("preserve this note"),
            "Clearing a variable must retain other arguments and field comments.");

        document = PresetDocument.Parse("""
            shared: &shared
              duration: $hours
              FormEditorID: Fire
            templates:
              change:
                finalFormEditorID: $result
                duration: $time
              cook:
                forms: Beef
                transformers:
                - change(Result, $hours)
                - <<: *shared
                  finalFormEditorID: OtherResult
            formsLists:
            - cook(2)
            """, "FOOD", PresetKind.Addon);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        selected = BehaviorFamily.ClearVariable(document, rule.Id, "hours");
        family = BehaviorFamily.Find(document, selected)!;
        Require(family.Parameters.Count == 0 && !TemplateCall.FindParameters(family.Definition.Value, document).Contains("hours") &&
            family.Definition.Value.Get("transformers")!.Items[0].Get("duration") is null,
            "Nested template and alias references must clear their affected fields locally.");
        Require(document.Blocks.Single(block => block.Key == "shared").Value.Get("duration")!.Text == "$hours" &&
            document.Blocks.Single(block => block.Key == "change").Value.Get("duration")!.Text == "$time",
            "Clearing one behavior must preserve unrelated shared definitions.");

        document = PresetDocument.Parse("""
            shared: &shared
              duration: $hours
              kept: &kept {value: 1}
              alsoKept: *kept
            templates:
              cook:
                forms: Beef
                details: *shared
            formsLists:
            - cook(2)
            """, "FOOD", PresetKind.Addon);
        rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        selected = BehaviorFamily.ClearVariable(document, rule.Id, "hours");
        var expanded = PresetDocument.Parse(Expand(document), "FOOD", PresetKind.Addon);
        var details = expanded.Blocks.Single(block => block.Kind == BlockKind.Rule).Value.Get("details")!;
        Require(details.Get("duration") is null && details.Get("kept")!.Get("value")!.Text == "1" &&
            details.Get("alsoKept")!.Get("value")!.Text == "1", "Clearing an aliased field must keep nested anchor references resolvable.");
    }

    private static void SafeNormalization()
    {
        var document = PresetDocument.Parse("formsLists:\n- forms: Beef\n  containers: [Chest]\n", "FOOD", PresetKind.Addon);
        var rule = document.Blocks.Single(); rule.Name = "Food";
        BehaviorFamily.MakeVariable(document, rule.Id, ["containers"], "storage");
        var firstId = rule.Id;
        var selectedId = BehaviorFamily.DuplicateUse(document, firstId).Id;
        BehaviorFamily.DuplicateUse(document, selectedId);
        BehaviorFamily.DuplicateUse(document, firstId);
        var resultId = BehaviorFamily.ClearVariable(document, selectedId, "storage");
        Require(resultId == firstId && document.Blocks.Count == 1 && document.Blocks[0] is { Kind: BlockKind.Rule, Name: "Food" } &&
            document.Blocks[0].Value.Get("containers") is null && document.Blocks[0].Value.Get("forms")!.Text == "Beef",
            "Clearing the final variable must replace redundant adjacent calls with one ordinary rule without retaining a row value.");

        const string common = "templates:\n  fixed:\n    forms: Beef\n    note: $$literal\nformsLists:\n";
        document = PresetDocument.Parse(common + "- fixed()\n- fixed()\n- fixed()\n- fixed()\n", "FOOD", PresetKind.Addon);
        var rows = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        resultId = BehaviorFamily.Normalize(document, rows[^1].Id);
        Require(resultId == rows[0].Id && document.Blocks.Count == 1 && document.Blocks[0].Value.Get("note")!.Text == "$literal",
            "A proven redundant parameterless batch must normalize while preserving literal template text.");

        document = PresetDocument.Parse("shared: 1\n# templates section note\n" + common + "- fixed()\n- fixed()\n", "FOOD", PresetKind.Addon);
        rule = document.Blocks.First(block => block.Kind == BlockKind.Rule);
        BehaviorFamily.Normalize(document, rule.Id);
        Require(document.ToYaml().Contains("# templates section note"),
            "Removing the final template must preserve comments attached to its section.");
        BehaviorFamily.MakeVariable(document, rule.Id, ["forms"], "item");
        Require(document.ToYaml().Split("# templates section note").Length == 2,
            "Creating a new template after normalization must not duplicate the retained section comment.");

        foreach (var calls in new[] { "- fixed() # first\n- fixed()\n", "- &first fixed()\n- fixed()\n", "- fixed()\n- forms: Apple\n- fixed()\n" })
        {
            document = PresetDocument.Parse(common + calls, "FOOD", PresetKind.Addon);
            rule = document.Blocks.First(block => block.Kind == BlockKind.Rule);
            var before = document.ToDraft();
            Require(BehaviorFamily.Normalize(document, rule.Id) == rule.Id && document.ToDraft() == before,
                "Normalization must preserve annotated, anchored, or interleaved calls.");
        }
        document = PresetDocument.Parse("templates:\n  fixed:\n    forms: Beef\n  outer:\n    <<: fixed()\nformsLists:\n- fixed()\n- fixed()\n- outer()\n", "FOOD", PresetKind.Addon);
        rule = document.Blocks.First(block => block.Kind == BlockKind.Rule);
        var unchanged = document.ToDraft(); BehaviorFamily.Normalize(document, rule.Id);
        Require(document.ToDraft() == unchanged, "A template used outside its direct rows must remain shared.");
        document = PresetDocument.Parse("formsLists:\n- forms: Beef\n- forms: Beef\n", "FOOD", PresetKind.Addon);
        rule = document.Blocks[0]; unchanged = document.ToDraft(); BehaviorFamily.Normalize(document, rule.Id);
        Require(document.ToDraft() == unchanged, "Ordinary imported duplicate rules must not be deduplicated.");
        document = PresetDocument.Parse("templates:\n  fixed:\n    finalFormEditorID: Original\n<<: fixed()\nfinalFormEditorID: Override\n", "FOOD", PresetKind.Default);
        rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule); unchanged = document.ToDraft();
        BehaviorFamily.Normalize(document, rule.Id);
        Require(document.ToDraft() == unchanged, "A default merge's inline override must never be discarded by normalization.");
    }

    private static void CompactSelectors()
    {
        var document = PresetDocument.Parse("""
            ownerLists:
            - owners: [Beef]
              stages: [{no: 0, name: Fresh, duration: 1}]
              containers: [Chest]
              finalFormEditorID: Result
            - owners: [Beef, Venison]
            - owners: []
            - owners: &items [Apple]
            """, "FOOD", PresetKind.Custom);
        var exported = PresetDocument.Parse(document.ToYaml(), "FOOD", PresetKind.Custom);
        var rows = exported.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        Require(rows[0].Value.Get("owners") is { Kind: ValueKind.Scalar, Text: "Beef" } &&
            rows[0].Value.Get("stages")!.Kind == ValueKind.Sequence && rows[0].Value.Get("containers")!.Kind == ValueKind.Sequence &&
            rows.Skip(1).All(row => row.Value.Get("owners")!.Kind == ValueKind.Sequence),
            "Only safe one-owner selectors may become scalars; arbitrary, empty, multiple, and anchored lists must remain lists.");
        document = PresetDocument.Parse("templates:\n  both:\n    forms: $items\n    extra: $items\nformsLists:\n- both([Beef])\n", "FOOD", PresetKind.Addon);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var selected = PresetValue.List(); selected.Items.Add(PresetValue.Scalar("Venison"));
        BehaviorFamily.SetArgument(document, rule.Id, "items", selected);
        Require(BehaviorFamily.GetArgument(document, rule.Id, "items").Kind == ValueKind.Sequence,
            "A selector parameter reused by a different field must retain its value shape.");
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
        BehaviorFamily.ClearVariable(document, rule.Id, nameParameter);
        var common = BehaviorFamily.Find(document, rule.Id)!.Definition.Value.Clone();
        BehaviorFamily.GetValue(common, ["stages", "0"])!.Set("name", PresetValue.Scalar("$$Fresh"));
        BehaviorFamily.SetCommon(document, rule.Id, common);
        var restored = PresetDocument.Parse(Expand(document), "FOOD", PresetKind.Custom);
        var restoredRules = restored.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        Require(restoredRules.Length == 2 && restoredRules.All(block => block.Value.Get("stages")!.Items[0].Get("name")!.Text == "$Fresh" &&
            block.Value.Get("stages")!.Items[0].Get("duration")!.Text == "2") && !BehaviorFamily.Find(document, rule.Id)!.Parameters.Contains("Fresh"),
            "A new common dollar-prefixed literal must preserve text and nested uses without adding a parameter.");

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
