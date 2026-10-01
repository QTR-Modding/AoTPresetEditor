using AoTPresetEditor;

internal static class DetachedTemplateChecks
{
    public static void Run()
    {
        const string yaml = "templates:\n  cook:\n    forms: $raw\n    duration: $hours\nformsLists:\n- cook(FoodBeef, 6)\n";
        var document = PresetDocument.Parse(yaml, "FOOD", PresetKind.Addon);
        var previous = document.Clone();
        var template = document.Blocks.Single(block => block.Kind == BlockKind.Template);
        var fields = template.Value.Fields.Reverse().ToArray();
        template.Value.Fields.Clear();
        foreach (var entry in fields) template.Value.Set(entry.Key, entry.Value);
        document.RebindTemplateCalls(previous);
        var committed = document.ToYaml();
        var detached = PresetYaml.Parse("calls:\n- cook(FoodVenison, 9)\nliteral: !!str 'cook(FoodVenison, 9)'\n");
        document.RebindValue(detached, previous);
        var call = detached.Get("calls")!.Items[0];
        Require(TemplateCall.TryParse(call.Text, out _, out var arguments) && arguments[0].Text == "9" && arguments[1].Text == "FoodVenison", "A detached editor's arguments were reinterpreted.");
        Require(detached.Get("literal")!.Text == "cook(FoodVenison, 9)", "An explicitly literal string was rebound.");
        Require(document.ToYaml() == committed, "Rebinding a detached editor rebound the document's existing calls twice.");
        Require(TemplateCall.TryParse(document.Blocks.Single(block => block.Kind == BlockKind.Rule).Value.Text, out _, out arguments) && arguments[0].Text == "6", "The document's already-rebound argument order changed.");

        const string nested = "templates:\n  inner:\n    first: $a\n    second: $b\n  outer:\n    x: inner($a, $b)\nformsLists:\n- outer(1, 2)\n";
        document = PresetDocument.Parse(nested, "FOOD", PresetKind.Addon);
        previous = document.Clone();
        document.Blocks.Single(block => block.Key == "inner").Value.Remove("first");
        document.RebindTemplateCalls(previous);
        committed = document.ToYaml();
        detached = PresetValue.Scalar("outer('inner(1, 2)', 'inner(3, 4)')");
        document.RebindValue(detached, previous);
        Require(TemplateCall.TryParse(detached.Text, out _, out arguments) && arguments.Count == 1 &&
            TemplateCall.TryParse(arguments[0].Text, out _, out var inner) && inner.Count == 1 && inner[0].Text == "4", "Nested detached calls did not use final template signatures.");
        Require(document.ToYaml() == committed, "Detached nested rebinding mutated stored document blocks.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

