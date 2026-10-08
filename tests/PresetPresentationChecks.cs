using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class PresetPresentationChecks
{
    private static readonly FormRow[] Forms =
    [
        new("Raw Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C99~Skyrim.esm", ItemCategories.FOOD),
        new("Cooked Beef", "FoodBeefCooked", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x721E8~Skyrim.esm", ItemCategories.FOOD),
        new("Venison", "FoodVenison", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x669A2~Skyrim.esm", ItemCategories.FOOD),
        new("Venison Chop", "FoodVenisonCooked", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x722BD~Skyrim.esm", ItemCategories.FOOD),
        new("Crack", "IceCrack", "SoundDescriptor", "Sounds.esp", "Sounds.esp", "0x800~Sounds.esp")
    ];

    public static void Run()
    {
        EmptyBehaviorPresentation();
        OwnerGroups();
        GroupChoiceCompatibility();
        GroupMemberSorting();
        var document = PresetDocument.Parse("""
            cooking: &cooking
              FormEditorID: Fire
              duration: 0.06
            portableHeat: &portableHeat
              <<: *cooking
              FormEditorID: Torch
            templates:
              cookFood:
                forms: $raw
                transformers:
                - <<: *cooking
                  finalFormEditorID: $cooked
                - <<: *portableHeat
                  finalFormEditorID: $cooked
            formsLists:
            - cookFood(00065C99, 000721E8)
            - cookFood(FoodVenison, FoodVenisonCooked)
            """, "FOOD", PresetKind.Addon);
        var source = document.ToYaml();
        var expanded = PresetCatalog.Expand(source, CancellationToken.None).GetAwaiter().GetResult();
        var presentation = new PresetPresentation(document, Forms, expandedYaml: expanded);
        var rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        Require(presentation.RuleSummary(rules[0]) == "Raw Beef → Cooked Beef · 2 transformations",
            "The overview must describe the real transformation instead of repeating the template name.");
        Require(presentation.RuleSummary(rules[1]) == "Venison → Venison Chop · 2 transformations",
            "Repeated uses of a template must be distinguishable.");
        Require(presentation.EffectiveValue(rules[0])?.Get("transformers")?.Items[1].Get("duration")?.Text == "0.06",
            "The presentation must use native expansion for inherited fields.");
        Require(presentation.FormLabel("0x000800~sounds.esp") == "Crack" && presentation.FormLabel("00065C99") == "Raw Beef",
            "Equivalent supported identifiers should resolve to readable names.");
        Require(presentation.FormLabel("01000800") == "01000800", "Do not guess the load order for full form IDs.");
        Require(presentation.FormLabel("FoodBeef", includeIdentifier: true) == "Raw Beef (FoodBeef)",
            "Exact authored identifiers must remain available beside names.");
        var cooking = document.Blocks.Single(block => block.Key == "cooking");
        Require(presentation.Uses(cooking).Count == 4 && presentation.Uses(cooking).Contains(rules[1]),
            "Shared-setting impact must include rules that reach the setting through a template.");
        Require(presentation.ReferenceLabel(rules[0].Value) == "Template: cookFood" &&
            new PresetPresentation(document, Forms).RuleSummary(rules[0]).Contains("Raw Beef, Cooked Beef"),
            "An incomplete document must still show useful template arguments before expansion succeeds.");
        Require(document.ToYaml() == source, "Presentation must not mutate the authored preset.");

        var effect = PresetValue.Map();
        effect.Set("magnitude", PresetValue.Scalar("25"));
        effect.Set("duration", PresetValue.Scalar("60"));
        foreach (var identifier in new string?[] { null, "null", "", "0", "0x0", "00000000" })
        {
            if (identifier is null) effect.Remove("FormEditorID");
            else effect.Set("FormEditorID", PresetValue.Scalar(identifier));
            Require(presentation.Summary(effect, "effect") == "Zeroes the existing effect slot",
                "A missing or zero magic effect must describe the existing slot and hide ignored magnitude and duration.");
        }
        effect.Set("FormEditorID", PresetValue.Scalar("RestoreHealth"));
        Require(presentation.Summary(effect, "effect") == "RestoreHealth · Magnitude 25 · 60 seconds",
            "A selected magic effect must retain its magnitude and duration in the summary.");

        var group = new FormGroupDefinition("Mixed", ["00065C99", "0x000800~Sounds.esp", "UnknownOptionalForm"]);
        var picker = new GroupPickerWindow([group], Forms, new(Category: "FOOD"), purpose: "Choose affected item group");
        Field<ListBox>(picker, "groupList").SelectedItem = group;
        var rows = Field<DataGrid>(picker, "members").Items.Cast<object>().ToArray();
        var statuses = rows.Select(row => row.GetType().GetProperty("Status")!.GetValue(row)).ToArray();
        Require(statuses.SequenceEqual(["Available", "Not applicable", "Not found"]),
            "Every group member must show whether it exists and fits this field.");
        Require(rows.Select(row => row.GetType().GetProperty("Identifier")!.GetValue(row)).SequenceEqual(group.Members),
            "Group previews must preserve unresolved identifiers and their spelling.");
        Require(picker.Title == "Choose affected item group", "A group picker must keep the editing task in its heading.");
        picker.Close();
        var formPicker = new FormPickerWindow(Forms, selection: new(Category: "FOOD"), purpose: "Choose result item");
        Require(formPicker.Title == "Choose result item" && Field<DataGrid>(formPicker, "results").Items.Count == 4,
            "Contextual form selection must preserve applicability filtering.");
        formPicker.Close();
    }

    private static void GroupMemberSorting()
    {
        var group = new FormGroupDefinition("Order", ["0x100~Skyrim.esm", "0x7~Skyrim.esm", "0x10~Skyrim.esm"]);
        var picker = new GroupPickerWindow([group], []);
        Field<ListBox>(picker, "groupList").SelectedItem = group;
        var grid = Field<DataGrid>(picker, "members");
        var identifierColumn = grid.Columns.Single(column => Equals(column.Header, "Identifier"));
        grid.Items.SortDescriptions.Add(new SortDescription(identifierColumn.SortMemberPath, ListSortDirection.Ascending));
        string[] SortedIdentifiers() => grid.Items.Cast<object>().Select(row => (string)row.GetType().GetProperty("Identifier")!.GetValue(row)!).ToArray();
        Require(SortedIdentifiers().SequenceEqual(["0x7~Skyrim.esm", "0x10~Skyrim.esm", "0x100~Skyrim.esm"]),
            "The group member Identifier column must sort hexadecimal IDs numerically.");
        grid.Items.SortDescriptions.Clear();
        grid.Items.SortDescriptions.Add(new SortDescription(identifierColumn.SortMemberPath, ListSortDirection.Descending));
        Require(SortedIdentifiers().SequenceEqual(["0x100~Skyrim.esm", "0x10~Skyrim.esm", "0x7~Skyrim.esm"]) &&
            group.Members.SequenceEqual(["0x100~Skyrim.esm", "0x7~Skyrim.esm", "0x10~Skyrim.esm"]),
            "Descending display sorting must not reorder the authored group definition.");
        picker.Close();
        var editor = new GroupEditorWindow([], [], initialMembers: group.Members);
        var editorGrid = Field<DataGrid>(editor, "members");
        editorGrid.Items.SortDescriptions.Add(new SortDescription(editorGrid.Columns.Single(column => Equals(column.Header, "Identifier")).SortMemberPath,
            ListSortDirection.Ascending));
        Require((string)editorGrid.Items[0].GetType().GetProperty("Identifier")!.GetValue(editorGrid.Items[0])! == "0x7~Skyrim.esm",
            "The group creation preview must use the same numeric Identifier ordering.");
        editor.Close();
    }

    private static void GroupChoiceCompatibility()
    {
        FormGroupDefinition[] groups =
        [
            new("WrongType", ["IceCrack"]),
            new("MixedTypes", ["IceCrack", "FoodBeef"]),
            new("OptionalMembers", ["IceCrack", "UnknownOptionalForm"]),
            new("Empty", [])
        ];
        var picker = new GroupPickerWindow(groups, Forms, new(Category: "FOOD"));
        var list = Field<ListBox>(picker, "groupList");
        list.SelectedItem = groups[0];
        var choose = Field<Button>(picker, "choose");
        Require(choose.Visibility == Visibility.Visible && !choose.IsEnabled && list.Items.Count == groups.Length &&
            Field<TextBlock>(picker, "description").Text.Contains("Choose a group with applicable members"),
            "A wholly incompatible group must remain inspectable while its Choose action explains why it is disabled.");
        choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Invoke(picker, "Choose");
        Require(picker.SelectedGroup is null && picker.CreatedGroups.Count == 0,
            "The action shared by Choose, Enter, and double-click must reject a wholly incompatible selected group.");
        picker.Close();

        foreach (var group in groups.Skip(1))
        {
            var allowed = new GroupPickerWindow(groups, Forms, new(Category: "FOOD"));
            Field<ListBox>(allowed, "groupList").SelectedItem = group;
            Require(Field<Button>(allowed, "choose").IsEnabled,
                "Mixed, unresolved, and empty groups must retain their existing selectable semantics.");
            Require(AcceptDialog(allowed, () => Field<Button>(allowed, "choose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))) == true &&
                allowed.SelectedGroup == group && allowed.SelectedGroup.Members.SequenceEqual(group.Members),
                "Choosing a usable group must preserve every authored member, including incompatible and optional members.");
        }

        var acoustic = new FormRow("Acoustic", "TestAcoustic", "AcousticSpace", "Skyrim.esm", "Skyrim.esm", "0x900~Skyrim.esm", IsBoundObject: true);
        var acousticGroup = new FormGroupDefinition("AcousticGroup", [acoustic.Identifier]);
        var bound = new FormSelection(BoundObjectOnly: true);
        var acousticPicker = new GroupPickerWindow([acousticGroup], [acoustic], bound);
        Field<ListBox>(acousticPicker, "groupList").SelectedItem = acousticGroup;
        Require(bound.Matches(acoustic) && !Field<Button>(acousticPicker, "choose").IsEnabled,
            "Group applicability must use picker restrictions, not merely AoT's broad form acceptance.");
        acousticPicker.Close();

        foreach (var group in groups)
        {
            var editor = new GroupEditorWindow([], Forms, new(Category: "FOOD"), initialMembers: group.Members);
            Field<TextBox>(editor, "name").Text = group.Name;
            var create = Field<Button>(editor, "create");
            if (group == groups[0])
            {
                Require(!create.IsEnabled && Field<TextBlock>(editor, "status").Text.Contains("No members can be used"),
                    "Prefilled incompatible members must explain why the group cannot be created for this field.");
                create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Invoke(editor, "Create");
                Require(editor.Group is null, "Disabled creation must also be guarded in the action itself.");
                var grid = Field<DataGrid>(editor, "members");
                grid.SelectedItem = grid.Items[0];
                Invoke(editor, "RemoveSelected");
                Require(create.IsEnabled, "Removing all incompatible members must restore the permitted empty-group state.");
                editor.Close();
            }
            else
            {
                Require(create.IsEnabled && AcceptDialog(editor, () => create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))) == true &&
                    editor.Group?.Members.SequenceEqual(group.Members) == true,
                    "Creation must retain mixed, optional, and empty group members without silently filtering the definition.");
            }
        }
    }

    private static bool? AcceptDialog(Window dialog, Action accept)
    {
        dialog.WindowStyle = WindowStyle.None; dialog.AllowsTransparency = true;
        dialog.Opacity = 0; dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
        Exception? failure = null;
        var action = dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                accept();
                if (dialog.IsVisible) throw new InvalidOperationException("The selection action did not accept the dialog.");
            }
            catch (Exception error) { failure = error; dialog.DialogResult = false; }
        }));
        try
        {
            var result = dialog.ShowDialog();
            if (failure is not null) throw failure;
            return result;
        }
        finally { action.Abort(); dialog.Close(); }
    }

    private static void OwnerGroups()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        document.PendingGroups.Add(new("Meat", ["00065C99", "FoodVenison", "UnknownOptionalForm"]));
        var catalog = new PresetPresentation(document, Forms,
        [
            new("Meat", ["FoodBeefCooked"]),
            new("FoodBeef", ["FoodVenison"]),
            new("Raw Beef", []),
            new("Venison", ["UnknownOptionalForm"]),
            new("Nested", ["Meat"])
        ]);
        Require(catalog.ResolveGroup("Meat") == document.PendingGroups[0] && catalog.ResolveGroup("meat") is null,
            "Pending groups must override the same installed name, and group lookup must retain AoT's exact case.");
        Require(catalog.MatchesOwner("Meat", Forms[0]) && catalog.MatchesOwner("Meat", Forms[2]) &&
            !catalog.MatchesOwner("Meat", Forms[1]), "Owners must match the group's known members rather than an installed group's replaced contents.");
        Require(catalog.MatchesOwner("FoodBeef", Forms[2]) && !catalog.MatchesOwner("FoodBeef", Forms[0]) &&
            catalog.ResolveForm("FoodBeef", allowGroups: true) is null,
            "An exact group name must take precedence over a colliding individual form identifier.");
        Require(catalog.MatchesOwner("foodbeef", Forms[0]) && catalog.ResolveForm("foodbeef", allowGroups: true) == Forms[0],
            "A differently cased selector must retain ordinary form lookup when it does not name a group exactly.");
        Require(!catalog.MatchesOwner("Raw Beef", Forms[0]) && !catalog.MatchesOwner("Venison", Forms[2]) &&
            catalog.MatchesOwner("raw beef", Forms[0]),
            "Empty and nonmatching groups must not fall back to name words; a differently cased name may still use word matching.");
        Require(!catalog.MatchesOwner("Nested", Forms[0]), "Group members are form identifiers, not recursive group references.");
        foreach (var key in new[] { "owners", "forms" })
        {
            var rule = PresetValue.Map(); rule.Set(key, PresetValue.Scalar("Meat"));
            Require(catalog.SourceForms(rule).SequenceEqual([Forms[0], Forms[2]]),
                "Both custom and addon sources must resolve known group members while retaining unresolved members in the authored group.");
            rule.Set(key, PresetValue.Scalar("Raw Beef"));
            Require(catalog.SourceForms(rule).Count == 0, "An empty group must remain empty when deriving source-item restrictions.");
        }
        Require(document.PendingGroups[0].Members.Last() == "UnknownOptionalForm",
            "Inspecting group sources must not rewrite or discard unresolved members.");

        var staged = PresetDocument.Parse("""
            templates:
              aging:
                owners: $items
                stages:
                - {no: 0, name: Fresh, duration: 1}
                - {no: 1, name: Old, duration: 1, FormEditorID: FoodVenison}
                finalFormEditorID: FoodBeefCooked
            ownerLists:
            - aging(Meat)
            """, "FOOD", PresetKind.Custom);
        staged.PendingGroups.Add(document.PendingGroups[0]);
        var stagedCatalog = new PresetPresentation(staged, Forms.Select(form => form with { IsBoundObject = true }).ToArray());
        var validation = PresetValidation.Check(staged, CancellationToken.None, stagedCatalog).GetAwaiter().GetResult();
        Require(PresetDocument.Parse(validation.Expanded, "FOOD", PresetKind.Custom).Blocks.Single(block => block.Kind == BlockKind.Rule)
            .Value.Get("owners")?.Text == "Meat" && validation.Errors.Values.SelectMany(errors => errors)
            .Any(error => error.Contains("a later stage cannot reuse the source item")),
            "Native expansion must retain variable group names while stage validation checks their known source members.");
    }

    private static void EmptyBehaviorPresentation()
    {
        var document = PresetDocument.Parse("""
            templates:
              unusedSettings:
                containers: $containers
              meat:
                owners: $raw
                stages:
                - no: 0
                  duration: 1
                finalFormEditorID: $cooked
            ownerLists:
            - meat(FoodBeef, FoodBeefCooked)
            - meat(FoodVenison, FoodVenisonCooked)
            """, "FOOD", PresetKind.Custom);
        Require(new PresetPresentation(document, Forms).Behaviors().Count == 1,
            "The behavior list must group template uses without exposing unrelated unused templates.");
        foreach (var rule in document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray())
            BehaviorFamily.RemoveUse(document, rule.Id);
        var definition = document.Blocks.Single(block => block.Key == "meat");
        foreach (var restored in new[] { document, document.Clone(), PresetDocument.FromDraft(document.ToDraft()),
            PresetDocument.Parse(document.ToYaml(), document.Category, document.Kind) })
        {
            var presentation = new PresetPresentation(restored, Forms);
            var behavior = presentation.Behaviors().Single();
            Require(behavior.Id == definition.Id && behavior.IsBehavior && presentation.RuleSummary(behavior, true) == "No rows",
                "An intentionally empty behavior must remain visible after cloning, drafting, and YAML reopening.");
            Require(!restored.Blocks.Single(block => block.Key == "unusedSettings").IsBehavior,
                "Preserving an empty behavior must not classify an imported helper template as a behavior.");
        }
        var row = BehaviorFamily.AddUse(document, definition.Id);
        Require(new PresetPresentation(document, Forms).Behaviors().Single().Id == row.Id,
            "Adding a new row to the retained settings must show one behavior, not the definition and row separately.");
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Invoke(object owner, string name) => owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
