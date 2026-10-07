using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class UnifiedBehaviorChecks
{
    public static void Run()
    {
        AddSections();
        ChangeAppliesTo();
        AppliesToVariables();
        AppliesToAvailability();
        IncompleteDefaultSelection();
        ReferencedAppliesTo();
        WorkspaceSplit();
        InheritedSections();
        OverrideInheritedStages();
        SelectorTemplateConversion();
        SelectorAnnotations();
        AnchoredImportedRule();
        ParameterizedInheritance();
        AliasedInheritedStages();
    }

    private static void AddSections()
    {
        var document = PresetDocument.Parse("""
            formsLists:
            - forms: 0x65C99~Skyrim.esm
            """, "FOOD", PresetKind.Addon);
        var id = document.Blocks.Single().Id;
        var editor = new BehaviorEditorControl(document, [], id);
        Require(Button(editor, "Add stages") is not null && Button(editor, "Add transformation") is not null &&
            Button(editor, "Add time modifier") is not null && !Descendants<TextBlock>(editor).Any(text => text.Text == "Final item"),
            "A new behavior must offer all three features without showing an unrelated required final item.");
        Click(Button(editor, "Add transformation"));
        editor.Capture();
        Require(BehaviorFamily.Find(editor.Document, id)!.Definition.Value.Get("transformers")?.Items.Count == 1 &&
            ButtonOrDefault(editor, "Add transformation") is null,
            "The transformation action must create its first entry and leave one configured section rather than a duplicate add action.");
        Click(Button(editor, "Add time modifier"));
        Click(Button(editor, "Add stages"));
        editor.Capture();
        var value = BehaviorFamily.Find(editor.Document, id)!.Definition.Value;
        Require(editor.Document.Kind == PresetKind.Custom && value.Get("owners")?.Text == "0x65C99~Skyrim.esm" &&
            value.Get("forms") is null && value.Get("stages")?.Items.Single().Get("no")?.Text == "0" &&
            value.Get("transformers")?.Items.Count == 1 && value.Get("timeModulators")?.Items.Count == 1,
            "Adding stages must convert the selected-item selector while preserving transformations and time modifiers already entered.");
        var stages = StageSection(editor);
        Require(stages is { IsExpanded: true } && Descendants<TextBlock>(stages).Any(text => text.Text == "Final item") &&
            Descendants<TextBlock>(editor).Count(text => text.Text == "Final item") == 1,
            "Newly added stages must open for editing and contain the final item exactly once.");
        stages.IsExpanded = false;
        editor.Capture();
        value = BehaviorFamily.Find(editor.Document, id)!.Definition.Value;
        Require(value.Get("stages")?.Items.Count == 1 && value.Get("finalFormEditorID") is not null,
            "Collapsing Stages must preserve the stages and their final item.");
        Require(ButtonOrDefault(editor, "Add stages") is null && document.Kind == PresetKind.Addon &&
            document.Blocks.Single().Value.Fields.Count == 1,
            "Configured stages must replace their add action, and isolated editor changes must not mutate the original document.");

        // Exercise the conversion used after accepting Remove's native confirmation without sending native UI input.
        Invoke(editor, "ChangeKind", PresetKind.Addon);
        Invoke(editor, "Rebuild", false, null, null);
        editor.Capture();
        value = BehaviorFamily.Find(editor.Document, id)!.Definition.Value;
        Require(editor.Document.Kind == PresetKind.Addon && value.Get("stages") is null && value.Get("finalFormEditorID") is null &&
            value.Get("transformers")?.Items.Count == 1 && value.Get("timeModulators")?.Items.Count == 1 &&
            ButtonOrDefault(editor, "Add stages") is not null,
            "Removing the stage feature must retain the other configured sections and restore the Add stages action.");
    }

    private static readonly FormRow[] AppliesToForms =
    [
        new("Raw Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C99~Skyrim.esm", ItemCategories.FOOD),
        new("Venison", "FoodVenison", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C9E~Skyrim.esm", ItemCategories.FOOD),
        new("Sword", "IronSword", "Weapon", "Skyrim.esm", "Skyrim.esm", "0x12EB7~Skyrim.esm", ItemCategories.WEAP)
    ];

    private static void ChangeAppliesTo()
    {
        var document = PresetDocument.Parse("""
            shared: &shared
              containers: 0x7
              stages: [{no: 0, name: Fresh, duration: 7}]
              finalFormEditorID: 0x721E8~Skyrim.esm
            ownerLists:
            - <<: *shared
              owners: 0x65C99~Skyrim.esm
            """, "FOOD", PresetKind.Custom);
        var original = document.ToDraft();
        var id = document.Blocks.Single(block => block.Kind == BlockKind.Rule).Id;
        var editor = new BehaviorEditorControl(document, AppliesToForms, id);
        Require(!Descendants<TextBlock>(editor).Any(text => text.Text == "Scope") &&
            !Descendants<ComboBox>(editor).Any(combo => AutomationProperties.GetName(combo) == "Behavior scope"),
            "Applies to must offer the affected-item choice without a separate Scope control.");
        Click(AppliesButton(editor, "All food"));
        editor.Capture();
        var value = BehaviorFamily.Find(editor.Document, editor.RuleId)!.Definition.Value;
        Require(editor.Document.Kind == PresetKind.Default && value.Get("forms") is null && value.Get("owners") is null &&
            value.Get("stages") is null && value.Get("<<") is { Kind: ValueKind.Alias } &&
            BehaviorFamily.GetSetting(editor.Document, editor.RuleId, "stages")?.Items.Single().Get("duration")?.Text == "7",
            "All food must use the inherited stages and retain shared settings without inventing a local stage override.");
        var appliesTo = DefaultAppliesCard(editor);
        Require(Descendants<TextBlock>(appliesTo).Select(text => text.Text).SequenceEqual(["Applies to", "All food"]) &&
            Descendants<Button>(appliesTo).Any(button => AutomationProperties.GetName(button) == "Help for Applies to" &&
                AutomationProperties.GetHelpText(button).Length > 0) &&
            !Descendants<Button>(editor).Any(button => Equals(button.Content, "All food")),
            "The default's Applies to card must show its selection and put precedence help behind the info control without a duplicate All action.");
        var beforePicker = editor.Document.ToDraft();
        var changes = 0;
        editor.KindChangeRequested += (edited, ruleId, kind) =>
        {
            changes++;
            var project = new PresetProject(); project.Add(edited, "Affected items");
            return project.ChangeBehaviorKind(ruleId, kind).Document;
        };
        ChooseAffectedItems(editor, accept: false, () => Require(changes == 0 && editor.Document.ToDraft() == beforePicker,
            "Opening the form picker must leave the default project unchanged until forms are accepted."));
        Require(changes == 0 && editor.Document.ToDraft() == beforePicker,
            "Cancelling the affected-item picker must preserve the default and all its settings.");
        ChooseAffectedItems(editor, accept: true, () => Require(changes == 0 && editor.Document.ToDraft() == beforePicker,
            "Selecting forms must not commit a conversion before Choose is pressed."));
        editor.Capture();
        value = BehaviorFamily.Find(editor.Document, editor.RuleId)!.Definition.Value;
        Require(changes == 1 && editor.Document.Kind == PresetKind.Custom && value.Get("owners") is { Kind: ValueKind.Sequence } owners &&
            owners.Items.Select(item => item.Text).SequenceEqual(AppliesToForms.Take(2).Select(form => form.Identifier)) &&
            value.Get("<<") is { Kind: ValueKind.Alias } &&
            BehaviorFamily.GetSetting(editor.Document, editor.RuleId, "stages")?.Items.Single().Get("duration")?.Text == "7" &&
            document.ToDraft() == original,
            "Accepting multiple forms must convert once, retain the inherited settings, and leave the original document untouched.");
    }

    private static void AppliesToVariables()
    {
        var document = PresetDocument.Parse("""
            templates:
              aging:
                owners: $item
                stages: [{no: 0, name: Fresh, duration: $hours}]
                finalFormEditorID: $result
            ownerLists:
            - aging(0x65C99~Skyrim.esm, 7, 0x721E8~Skyrim.esm)
            """, "FOOD", PresetKind.Custom);
        var editor = new BehaviorEditorControl(document, AppliesToForms, document.Blocks.Last().Id);
        Require(AppliesButton(editor, "All food") is not null, "A variable Applies to selector must offer All for its one-row behavior.");
        Field<TabControl>(editor, "tabs").SelectedIndex = 1;
        var table = Field<DataGrid>(editor, "table");
        table.SelectedItem = table.Items[0]; table.CurrentCell = new DataGridCellInfo(table.Items[0], table.Columns[1]);
        Require(!Descendants<Button>(Field<ValueEditorControl>(editor, "cellEditor")).Any(button => button.Content is string text && text.StartsWith("All ", StringComparison.Ordinal)),
            "A Variables cell's form selector must not offer a whole-behavior All action.");
        Field<TabControl>(editor, "tabs").SelectedIndex = 0;
        Click(AppliesButton(editor, "All food"));
        editor.Capture();
        var family = BehaviorFamily.Find(editor.Document, editor.RuleId)!;
        Require(editor.Document.Kind == PresetKind.Default && family.Rows.Count == 1 && family.Parameters.SequenceEqual(["hours", "result"]) &&
            BehaviorFamily.GetArgument(editor.Document, editor.RuleId, "hours").Text == "7" &&
            BehaviorFamily.GetArgument(editor.Document, editor.RuleId, "result").Text == "0x721E8~Skyrim.esm",
            "All must remove only the selector variable while preserving the single row's stage and result variables.");
        ChooseAffectedItems(editor, accept: true, () => { });
        editor.Capture();
        family = BehaviorFamily.Find(editor.Document, editor.RuleId)!;
        Require(editor.Document.Kind == PresetKind.Custom && family.Rows.Count == 1 && family.Parameters.SequenceEqual(["hours", "result"]) &&
            BehaviorFamily.GetArgument(editor.Document, editor.RuleId, "hours").Text == "7" &&
            BehaviorFamily.GetArgument(editor.Document, editor.RuleId, "result").Text == "0x721E8~Skyrim.esm",
            "Choosing individual forms again must keep the existing stage and result variables and their values.");
        var another = document.Clone();
        BehaviorFamily.AddUse(another, another.Blocks.Last().Id);
        var multiple = new BehaviorEditorControl(another, AppliesToForms, another.Blocks.Last().Id);
        Require(!Descendants<Button>(multiple).Any(button => Equals(button.Content, "All food")),
            "A behavior with multiple Variables rows cannot become one default and must not offer All.");
    }

    private static void AppliesToAvailability()
    {
        foreach (var (category, label) in new[] { ("FOOD", "food"), ("WEAP", "weapons"), ("MISC", "miscellaneous items") })
        {
            var document = PresetDocument.Parse("ownerLists: [{owners: [], stages: [{no: 0, name: Fresh, duration: 1}], finalFormEditorID: Result}]", category, PresetKind.Custom);
            var editor = new BehaviorEditorControl(document, [], document.Blocks.Single().Id);
            editor.Capture();
            Require(editor.Document.Kind == PresetKind.Custom && BehaviorFamily.Find(editor.Document, editor.RuleId)!.Definition.Value.Get("owners") is { Kind: ValueKind.Sequence, Items.Count: 0 },
                "An empty affected-item selection must remain selected-item behavior, never silently become a default.");
            var before = editor.Document.ToDraft();
            var requested = false;
            editor.KindChangeRequested += (_, _, kind) => { requested = kind == PresetKind.Default; return null; };
            Click(AppliesButton(editor, "All " + label));
            Require(requested && editor.Document.Kind == PresetKind.Custom && editor.Document.ToDraft() == before,
                "The category-specific All action must preserve the selected behavior when its conversion callback cancels.");
        }
        var unstaged = PresetDocument.Parse("formsLists: [{forms: FoodBeef}]", "FOOD", PresetKind.Addon);
        var withoutStages = new BehaviorEditorControl(unstaged, [], unstaged.Blocks.Single().Id);
        Require(!Descendants<Button>(withoutStages).Any(button => button.Content is string text && text.StartsWith("All ", StringComparison.Ordinal)),
            "A triggered behavior without stages must not offer a default-stage selection.");
        var staged = PresetDocument.Parse("ownerLists: [{owners: [FoodBeef], stages: [{no: 0, name: Fresh, duration: 1}], finalFormEditorID: Result, containers: [Barrel]}]", "FOOD", PresetKind.Custom);
        var nested = new BehaviorEditorControl(staged, [], staged.Blocks.Single().Id);
        Require(Descendants<Button>(nested).Count(button => Equals(button.Content, "All food")) == 1 &&
            Descendants<ValueEditorControl>(nested).Where(control => Field<PresetField?>(control, "field")?.Key == "containers")
                .All(control => !Descendants<Button>(control).Any(button => Equals(button.Content, "All food"))),
            "Only the behavior's Applies to selector may offer All; nested form selectors must retain their own selection controls.");
    }

    private static void IncompleteDefaultSelection()
    {
        var document = PresetDocument.Parse("transformers: [{FormEditorID: Fire, duration: 0.25, finalFormEditorID: CookedBeef}]", "FOOD", PresetKind.Default);
        var transformation = PresetYaml.Write(document.Blocks.Single().Value.Get("transformers")!);
        var editor = new BehaviorEditorControl(document, AppliesToForms, document.Blocks.Single().Id);
        editor.Capture();
        var before = editor.Document.ToDraft();
        Require(Descendants<TextBlock>(DefaultAppliesCard(editor)).Any(text => text.Text == "All food") &&
            BehaviorFamily.GetSetting(editor.Document, editor.RuleId, "stages") is null,
            "An imported default without stages must retain its All selection and offer Choose forms without adding stages.");
        ChooseAffectedItems(editor, accept: false, () => Require(editor.Document.ToDraft() == before,
            "Opening an incomplete default's picker must preserve the original behavior until acceptance."));
        Require(editor.Document.ToDraft() == before, "Cancelling an incomplete default's picker must preserve its kind and transformation.");
        ChooseAffectedItems(editor, accept: true, () => Require(editor.Document.ToDraft() == before,
            "Selecting items for an incomplete default must wait for Choose before changing its kind."));
        editor.Capture();
        var value = BehaviorFamily.Find(editor.Document, editor.RuleId)!.Definition.Value;
        Require(editor.Document.Kind == PresetKind.Addon && value.Get("forms") is { Kind: ValueKind.Sequence } forms &&
            forms.Items.Select(item => item.Text).SequenceEqual(AppliesToForms.Take(2).Select(form => form.Identifier)) &&
            value.Get("owners") is null && value.Get("stages") is null &&
            PresetYaml.Write(value.Get("transformers")!) == transformation &&
            !Descendants<Button>(editor).Any(button => Equals(button.Content, "All food")),
            "Choosing forms for a default without stages must retain its transformation in an addon and hide the unavailable All action.");
    }
    private static void ReferencedAppliesTo()
    {
        const string stages = "stages: [{no: 0, name: Fresh, duration: 7}]\n  finalFormEditorID: CookedBeef";
        foreach (var source in new[]
        {
            "selection: &selection [FoodBeef]\nownerLists:\n- owners: *selection\n  " + stages,
            "templates:\n  selectors: [FoodBeef]\nownerLists:\n- owners: selectors()\n  " + stages,
            "shared: &shared\n  owners: FoodBeef\n  " + stages + "\nownerLists:\n- <<: *shared"
        })
        {
            var document = PresetDocument.Parse(source, "FOOD", PresetKind.Custom);
            var original = document.ToDraft();
            var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
            var editor = new BehaviorEditorControl(document, AppliesToForms, rule.Id);
            var beforeStages = PresetYaml.Write(BehaviorFamily.GetSetting(editor.Document, editor.RuleId, "stages")!);
            Require(Descendants<Button>(editor).Count(button => Equals(button.Content, "All food")) == 1,
                "Aliased, template-generated, and inherited Applies to selections must each offer one All action.");
            Click(AppliesButton(editor, "All food"));
            editor.Capture();
            Require(editor.Document.Kind == PresetKind.Default &&
                BehaviorFamily.GetSetting(editor.Document, editor.RuleId, "owners") is null &&
                PresetYaml.Write(BehaviorFamily.GetSetting(editor.Document, editor.RuleId, "stages")!) == beforeStages &&
                BehaviorFamily.GetSetting(editor.Document, editor.RuleId, "finalFormEditorID")?.Text == "CookedBeef" &&
                document.ToDraft() == original,
                "All must clear a referenced or inherited selection while retaining its stage values, final item, and original imported document.");
        }
        foreach (var selection in new[] { "!!str 'selectors()'", "missingSelectors()" })
        {
            var source = "templates:\n  selectors: [FoodBeef]\nownerLists:\n- owners: " + selection + "\n  " + stages;
            var document = PresetDocument.Parse(source, "FOOD", PresetKind.Custom);
            var editor = new BehaviorEditorControl(document, AppliesToForms, document.Blocks.Last().Id);
            Require(Descendants<Button>(editor).Count(button => Equals(button.Content, "All food")) == 1,
                "Literal and unresolved call-shaped selector text must not receive duplicate All actions from reference and scalar rendering.");
        }
    }
    private static Border DefaultAppliesCard(BehaviorEditorControl editor) =>
        Field<StackPanel>(Field<ValueEditorControl>(editor, "commonEditor"), "content").Children.OfType<Border>()
            .Single(card => Descendants<TextBlock>(card).Any(text => text.Text == "Applies to"));

    private static Button AppliesButton(BehaviorEditorControl editor, string text) =>
        Descendants<Button>(editor.Document.Kind == PresetKind.Default && text == "Choose forms..." ? DefaultAppliesCard(editor) : editor)
            .Single(button => Equals(button.Content, text));

    private static void ChooseAffectedItems(BehaviorEditorControl editor, bool accept, Action beforeChoose)
    {
        var host = new Window { Content = editor, Width = 1000, Height = 750, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        Exception? failure = null;
        try
        {
            host.Show();
            var response = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var picker = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                    .OfType<FormPickerWindow>().Single(window => window.Owner == host);
                try
                {
                    picker.Opacity = 0; picker.ShowInTaskbar = false;
                    var choices = Field<DataGrid>(picker, "results");
                    Require(choices.SelectionMode == DataGridSelectionMode.Extended && choices.Items.Cast<FormRow>().SequenceEqual(AppliesToForms.Take(2)),
                        "The affected-item picker must allow multiple forms from the current item category only.");
                    Require(Field<Button>(picker, "choose").Visibility != Visibility.Visible,
                        "An empty picker selection must not offer acceptance or create an empty custom behavior.");
                    if (accept) foreach (var form in choices.Items.Cast<FormRow>().ToArray()) choices.SelectedItems.Add(form);
                    beforeChoose();
                    if (accept) Click(Field<Button>(picker, "choose"));
                    else picker.DialogResult = false;
                }
                catch (Exception error) { failure = error; picker.DialogResult = false; }
            }));
            try { Click(AppliesButton(editor, "Choose forms...")); }
            finally { response.Abort(); }
            if (failure is not null) throw failure;
            Require(response.Status == DispatcherOperationStatus.Completed, "Choose forms must open its owned affected-item picker.");
        }
        finally { host.Content = null; host.Close(); }
    }
    private static void WorkspaceSplit()
    {
        var document = PresetDocument.Parse("""
            heat: &heat
              FormEditorID: Fire
              duration: 0.06
            templates:
              cook:
                forms: $raw
                transformers:
                - <<: *heat
                  finalFormEditorID: $cooked
            formsLists:
            - cook(0x65C99~Skyrim.esm, 0x721E8~Skyrim.esm)
            - cook(0x65C9E~Skyrim.esm, 0x722C2~Skyrim.esm)
            - forms: 0x64B41~Skyrim.esm
              transformers:
              - <<: *heat
                finalFormEditorID: 0x64B3A~Skyrim.esm
            """, "FOOD", PresetKind.Addon);
        var firstId = document.Blocks.First(block => block.Kind == BlockKind.Rule).Id;
        var otherId = document.Blocks.Last().Id;
        var source = document.ToDraft();
        var project = new PresetProject { Name = "Cooking" };
        var entry = project.Add(document, "Cooking", "opened.yml", "hash");
        var workspace = new PresetWorkspace();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(workspace.Dispatcher));
        SetField(workspace, "project", project); SetField(workspace, "activeEntry", entry);
        SetField(workspace, "document", document); SetField(workspace, "filename", project.Name);
        try
        {
            Invoke(workspace, "RenderBlocks", firstId); Invoke(workspace, "SelectBlock");
            var editor = Field<BehaviorEditorControl>(workspace, "editor");
            Click(Button(editor, "Add stages"));
            Invoke(workspace, "CommitEditor");
            var edited = Field<PresetProject>(workspace, "project");
            var staged = edited.FindByBlockId(firstId)!;
            var remaining = edited.FindByBlockId(otherId)!;
            Require(edited.Entries.Count == 2 && staged != remaining && staged.Document.Kind == PresetKind.Custom &&
                remaining.Document.Kind == PresetKind.Addon && Field<PresetProjectEntry>(workspace, "activeEntry") == staged,
                "The actual workspace kind-change callback must split a family and adopt the new active output.");
            var family = BehaviorFamily.Find(staged.Document, firstId)!;
            Require(family.Rows.Count == 2 && family.Parameters.SequenceEqual(["raw", "cooked"]) &&
                BehaviorFamily.GetArgument(staged.Document, firstId, "raw").Text == "0x65C99~Skyrim.esm" &&
                BehaviorFamily.GetArgument(staged.Document, family.Rows[1].Id, "cooked").Text == "0x722C2~Skyrim.esm",
                "Adding stages through the editor must preserve all existing variable rows and column bindings.");
            Require(remaining.OpenedPath == "opened.yml" && staged.OpenedPath is null && document.ToDraft() == source &&
                staged.Document.ToYaml().Contains("*heat") && remaining.Document.ToYaml().Contains("*heat"),
                "Workspace adoption must preserve source tracking and dependencies without mutating the original imported document.");
            var tabs = Field<TabControl>(editor, "tabs"); tabs.SelectedIndex = 1;
            var table = Field<DataGrid>(editor, "table");
            Require(table.Items.Count == 2 && Equals(table.Columns[1].Header, "raw") && Equals(table.Columns[2].Header, "cooked"),
                "Variables must remain available with their original rows and names after the output split.");
            tabs.SelectedIndex = 0;
            Invoke(workspace, "RenderBlocks", firstId); Invoke(workspace, "SelectBlock");
            Require(ReferenceEquals(editor, Field<BehaviorEditorControl>(workspace, "editor")),
                "Refreshing the workspace after conversion must retain the active behavior editor.");
        }
        finally
        {
            Invoke(workspace, "ResetEditor");
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void InheritedSections()
    {
        var document = PresetDocument.Parse("""
            settings: &settings
              transformers:
              - FormEditorID: Fire
                duration: 1
                finalFormEditorID: 0x721E8~Skyrim.esm
            formsLists:
            - <<: *settings
              forms: 0x65C99~Skyrim.esm
            """, "FOOD", PresetKind.Addon);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        Require(BehaviorFamily.GetSetting(document, rule.Id, "transformers")?.Items.Count == 1,
            "Inherited configured sections must be discoverable before asynchronous native expansion completes.");
        var editor = new BehaviorEditorControl(document, [], rule.Id);
        Require(ButtonOrDefault(editor, "Add transformation") is null,
            "An inherited transformation must be shown as configured rather than offered as a missing feature.");
        var empty = PresetValue.List(); rule.Value.Set("transformers", empty);
        Require(ReferenceEquals(BehaviorFamily.GetSetting(document, rule.Id, "transformers"), empty),
            "An explicit empty override must take precedence over inherited transformations.");
        var cleared = new BehaviorEditorControl(document, [], rule.Id);
        Require(ButtonOrDefault(cleared, "Add transformation") is not null &&
            !Descendants<TextBlock>(cleared).Any(text => text.Text == "Transformations (0)"),
            "An empty optional override must present the add action without a redundant empty configured section.");
    }

    private static void SelectorTemplateConversion()
    {
        var document = PresetDocument.Parse("""
            templates:
              pick: $item
              cook:
                forms: pick($raw)
                transformers:
                - FormEditorID: Fire
                  duration: 1
                  finalFormEditorID: $cooked
            formsLists:
            - cook(MeatGroup, 0x721E8~Skyrim.esm)
            """, "FOOD", PresetKind.Addon);
        var ruleId = document.Blocks.Single(block => block.Kind == BlockKind.Rule).Id;
        var project = new PresetProject(); project.Add(document, "Helper selection");
        Require(project.BehaviorSelectors(ruleId).Any(value => value.Text == "MeatGroup"),
            "Selector preflight must discover groups passed through a selector template helper.");
        var converted = project.ChangeBehaviorKind(ruleId, PresetKind.Custom, value =>
        {
            if (value.Text != "MeatGroup") return value;
            var list = PresetValue.List(); list.Items.Add(PresetValue.Scalar("0x65C99~Skyrim.esm")); return list;
        });
        var family = BehaviorFamily.Find(converted.Document, ruleId)!;
        Require(family.Definition.Value.Get("owners")?.Text == "pick($raw)" &&
            BehaviorFamily.GetArgument(converted.Document, ruleId, "raw").Text == "0x65C99~Skyrim.esm" &&
            BehaviorFamily.GetArgument(converted.Document, ruleId, "cooked").Text == "0x721E8~Skyrim.esm",
            "Approved group conversion must update the bound selector while keeping helper calls and unrelated result variables.");
    }

    private static void OverrideInheritedStages()
    {
        var document = PresetDocument.Parse("""
            shared: &shared
              stages:
              - no: 0
                name: ""
                duration: 7
              finalFormEditorID: 0x721E8~Skyrim.esm
            ownerLists:
            - <<: *shared
              owners: 0x65C99~Skyrim.esm
            """, "FOOD", PresetKind.Custom);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var shared = document.Blocks.Single(block => block.Kind == BlockKind.Shared);
        var before = PresetYaml.Write(shared.Value);
        var editor = new BehaviorEditorControl(document, [], rule.Id);
        var header = (DependencyObject)StageSection(editor).Header;
        Click(Descendants<Button>(header).Single(button => Equals(button.Content, "Override here")));
        editor.Capture();
        var copied = BehaviorFamily.Find(editor.Document, rule.Id)!.Definition.Value.Get("stages");
        Require(copied?.Items.Single().Get("duration")?.Text == "7" &&
            PresetYaml.Write(editor.Document.Blocks.Single(block => block.Id == shared.Id).Value) == before &&
            rule.Value.Get("stages") is null,
            "The inherited Stages Override here action must create an editable local copy without changing the shared definition or original document.");
    }

    private static void SelectorAnnotations()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Addon);
        var rule = document.Add(BlockKind.Rule, "Cooking", "AddonRule");
        var selector = PresetValue.Scalar("MeatGroup");
        selector.Anchor = "selection";
        selector.Comments.Add("Selected meats"); selector.InlineComment = "Keep this explanation";
        selector.TrailingComments.Add("Selection notes");
        rule.Value.Set("forms", selector);
        rule.Value.Set("containers", PresetValue.Alias("selection"));
        var project = new PresetProject(); project.Add(document, "Annotated");
        var converted = project.ChangeBehaviorKind(rule.Id, PresetKind.Custom, _ => PresetValue.Scalar("0x65C99~Skyrim.esm"));
        var changed = BehaviorFamily.Find(converted.Document, rule.Id)!.Definition.Value.Get("owners")!;
        Require(changed.Text == "0x65C99~Skyrim.esm" && changed.Anchor == "selection" &&
            changed.Comments.SequenceEqual(selector.Comments) && changed.TrailingComments.SequenceEqual(selector.TrailingComments) &&
            changed.InlineComment == selector.InlineComment && converted.Document.ToYaml().Contains("*selection"),
            "Replacing a concrete selector must retain its shared name and all comment positions.");
        var unchanged = project.ChangeBehaviorKind(rule.Id, PresetKind.Addon, value => value);
        var preserved = BehaviorFamily.Find(unchanged.Document, rule.Id)!.Definition.Value.Get("forms")!;
        Require(preserved.Comments.Count == 1 && preserved.TrailingComments.Count == 1 && preserved.InlineComment == selector.InlineComment,
            "Returning an unchanged selector from the adapter must not duplicate its annotations.");
    }

    private static void AnchoredImportedRule()
    {
        var document = PresetDocument.Parse("""
            formsLists:
            - &cooking
              forms: 0x65C99~Skyrim.esm
              transformers:
              - FormEditorID: Fire
                duration: 0.05
                finalFormEditorID: 0x721E8~Skyrim.esm
            """, "FOOD", PresetKind.Addon);
        var original = document.ToYaml();
        var editor = new BehaviorEditorControl(document, [], document.Blocks.Single().Id);
        editor.Capture();
        Require(editor.Document.ToYaml() == original &&
            !Descendants<ComboBox>(editor).Any(combo => AutomationProperties.GetName(combo) == "Behavior scope") &&
            ButtonOrDefault(editor, "Add stages") is null,
            "An anchored imported rule must retain its existing editing path and exact YAML without unsupported conversion controls.");
    }

    private static void ParameterizedInheritance()
    {
        var document = PresetDocument.Parse("""
            templates:
              shared:
                stages:
                - no: 0
                  name: ""
                  duration: $hours
                finalFormEditorID: 0x721E8~Skyrim.esm
                transformers:
                - FormEditorID: Fire
                  duration: $cooking
                  finalFormEditorID: 0x721E8~Skyrim.esm
            ownerLists:
            - <<: shared(7, 0.25)
              owners: 0x65C99~Skyrim.esm
            """, "FOOD", PresetKind.Custom);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var definition = document.Blocks.Single(block => block.Kind == BlockKind.Template);
        var original = PresetYaml.Write(definition.Value);
        Require(BehaviorFamily.GetSetting(document, rule.Id, "stages")?.Items.Single().Get("duration")?.Text == "7" &&
            BehaviorFamily.GetSetting(document, rule.Id, "transformers")?.Items.Single().Get("duration")?.Text == "0.25",
            "Inherited settings must bind the current template call's arguments before offering local overrides.");
        var editor = new BehaviorEditorControl(document, [], rule.Id);
        Click(Descendants<Button>((DependencyObject)StageSection(editor).Header).Single(button => Equals(button.Content, "Override here")));
        editor.Capture();
        Require(BehaviorFamily.Find(editor.Document, rule.Id)!.Definition.Value.Get("stages")?.Items.Single().Get("duration")?.Text == "7" &&
            PresetYaml.Write(editor.Document.Blocks.Single(block => block.Id == definition.Id).Value) == original,
            "Overriding parameterized inherited stages must copy the bound duration without introducing an unbound variable or modifying the template.");
        var transformations = Descendants<Expander>(editor).Single(expander => expander.Header is DependencyObject header &&
            Descendants<TextBlock>(header).Any(text => text.Text.StartsWith("Transformations", StringComparison.Ordinal)));
        Click(Descendants<Button>((DependencyObject)transformations.Header).Single(button => Equals(button.Content, "Override here")));
        editor.Capture();
        Require(BehaviorFamily.Find(editor.Document, rule.Id)!.Definition.Value.Get("transformers")?.Items.Single().Get("duration")?.Text == "0.25",
            "Overriding parameterized inherited transformations must retain the actual bound setting too.");
    }

    private static void AliasedInheritedStages()
    {
        var document = PresetDocument.Parse("""
            stageList: &stageList
            - no: 0
              name: ""
              duration: 9
            shared: &shared
              stages: *stageList
              finalFormEditorID: 0x721E8~Skyrim.esm
            ownerLists:
            - <<: *shared
              owners: 0x65C99~Skyrim.esm
            """, "FOOD", PresetKind.Custom);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var editor = new BehaviorEditorControl(document, [], rule.Id);
        Click(Descendants<Button>((DependencyObject)StageSection(editor).Header).Single(button => Equals(button.Content, "Override here")));
        editor.Capture();
        var stages = BehaviorFamily.Find(editor.Document, rule.Id)!.Definition.Value.Get("stages");
        Require(stages is { Kind: ValueKind.Sequence, Anchor.Length: 0 } && stages.Items.Single().Get("duration")?.Text == "9" &&
            editor.Document.Blocks.Single(block => block.Key == "stageList").Value.Anchor == "stageList",
            "Overriding an inherited stage-list alias must copy editable values without duplicating the shared anchor.");
        _ = editor.Document.ToYaml();
    }

    private static Expander StageSection(BehaviorEditorControl editor) => Descendants<Expander>(editor)
        .Single(expander => expander.Header is DependencyObject header &&
            Descendants<TextBlock>(header).Any(text => text.Text.StartsWith("Stages", StringComparison.Ordinal)));

    private static Button? ButtonOrDefault(BehaviorEditorControl owner, string text) =>
        Field<StackPanel>(Field<ValueEditorControl>(owner, "commonEditor"), "content").Children.OfType<WrapPanel>()
            .SelectMany(panel => panel.Children.OfType<Button>()).SingleOrDefault(button => Equals(button.Content, text));
    private static Button Button(BehaviorEditorControl owner, string text) => ButtonOrDefault(owner, text)
        ?? throw new InvalidOperationException("Missing button: " + text);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Invoke(object owner, string name, params object?[] arguments) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
