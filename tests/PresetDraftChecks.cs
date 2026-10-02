using System.IO;
using System.Text.Json;
using AoTPresetEditor;

internal static class PresetDraftChecks
{
    public static void Run()
    {
        var document = PresetDocument.Parse("""
            # Reusable defaults
            heat: &heat
              FormEditorID: Fire
              duration: 0.06 # cooking time
            # Template definitions
            templates:
              cook:
                forms: $raw
                transformers:
                - <<: *heat
                  finalFormEditorID: $cooked
            # Actual food rules
            formsLists:
            - cook(FoodBeef, FoodBeefCooked) # beef
            # End of preset
            """, "FOOD", PresetKind.Addon);
        document.Blocks.Single(block => block.Kind == BlockKind.Rule).Name = "Cook beef";
        document.PendingGroups.Add(new("Fires", ["Fire", "UnknownOptionalFire"]));
        var before = document.ToYaml();
        var restored = PresetDocument.FromDraft(document.ToDraft());
        Require(restored.ToYaml() == before, "Draft reopening must preserve names, IDs, definitions, ordering, and comments.");
        Require(restored.PendingGroups.Single().Members.SequenceEqual(["Fire", "UnknownOptionalFire"]),
            "Drafts must preserve new form groups without exporting them to a mod.");
        restored.PendingGroups.Clear();
        Require(document.PendingGroups.Count == 1, "Draft round trips must not retain shared mutable collections.");

        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        rule.Value = PresetValue.Map();
        rule.Value.Set("forms", PresetValue.UnsetArgument());
        rule.Value.Set("<<", PresetValue.Alias("notCreatedYet"));
        rule.Value.Set("transformers", PresetValue.List());
        rule.Value.Get("transformers")!.Items.Add(PresetValue.Scalar("missingTemplate($result)"));
        rule.Value.Comments.Add("Work in progress");
        ExpectInvalid(() => document.ToYaml());
        restored = PresetDocument.FromDraft(document.ToDraft());
        var unfinished = restored.Blocks.Single(block => block.Kind == BlockKind.Rule);
        Require(unfinished.Value.Get("forms")?.IsUnsetArgument == true && unfinished.Value.Get("<<") is { Kind: ValueKind.Alias, Text: "notCreatedYet" },
            "A draft must keep unresolved aliases and unset arguments even when YAML export is invalid.");
        Require(unfinished.Value.Get("transformers")?.Items[0].Text == "missingTemplate($result)" &&
            unfinished.Value.Comments.Contains("Work in progress"), "Unfinished calls and their comments must survive draft reopening.");
        ExpectInvalid(() => PresetDocument.FromDraft("{\"Version\":999}"));
        ExpectInvalid(() => PresetDocument.FromDraft("not a draft"));
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException) { return; }
        throw new InvalidOperationException("An invalid input was unexpectedly accepted.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
