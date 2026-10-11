using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class BehaviorWorkspaceChecks
{
    public static void Run()
    {
        var cooking = PresetDocument.Parse("""
            # Cooking keeps the shared fire setup
            heat: &heat
              FormEditorID: Fire
            formsLists:
            - forms: FoodBeef
              transformers:
              - <<: *heat
                finalFormEditorID: FoodBeefCooked
                duration: 0.06
            """, "FOOD", PresetKind.Addon);
        cooking.PendingGroups.Add(new("Fires", ["Fire"]));
        var aging = PresetDocument.Parse("""
            ownerLists:
            - owners: IngredientA
              stages:
              - no: 0
                name: Fresh
                duration: 1
              finalFormEditorID: IngredientB
            """, "INGR", PresetKind.Custom);
        var project = new PresetProject { Name = "Cooking and aging" };
        var first = project.Add(cooking, "Cooking");
        var second = project.Add(aging, "Aging");
        var cookingId = cooking.Blocks.Single(block => block.Kind == BlockKind.Rule).Id;
        var agingId = aging.Blocks.Single().Id;
        var originalCooking = cooking.ToDraft();
        var originalAging = aging.ToDraft();
        var workspace = new PresetWorkspace();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(workspace.Dispatcher));
        SetField(workspace, "project", project);
        SetField(workspace, "activeEntry", first);
        SetField(workspace, "document", cooking);
        SetField(workspace, "filename", project.Name);
        try
        {
            Invoke(workspace, "RenderBlocks", cookingId);
            Invoke(workspace, "SelectBlock");
            var list = Field<ListBox>(workspace, "blocks");
            Require(list.Items.Count == 2, "The overview must show behaviors from every document and category.");
            var firstEditor = Field<BehaviorEditorControl>(workspace, "editor");
            Field<TextBox>(firstEditor, "name").Text = "Cook beef slowly";
            Duration(firstEditor).Text = "0.12";
            Require(Field<bool>(workspace, "editorDirty") && cooking.ToDraft() == originalCooking,
                "Unfinished behavior edits must remain isolated until the workspace captures them.");
            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, agingId));
            Require(Field<PresetProjectEntry>(workspace, "activeEntry") == second &&
                Field<PresetDocument>(workspace, "document") == second.Document,
                "Switching category and behavior type must activate the selected document.");
            var capturedCooking = first.Document.Blocks.Single(block => block.Kind == BlockKind.Rule);
            Require(capturedCooking.Name == "Cook beef slowly" &&
                capturedCooking.Value.Get("transformers")!.Items.Single().Get("duration")!.Text == "0.12" &&
                first.Document.PendingGroups.Single().Name == "Fires" &&
                first.Document.Blocks.Single(block => block.Kind == BlockKind.Shared).Value.Anchor == "heat",
                "Switching documents must capture the name and nested values while preserving shared settings and companion groups.");
            Require(second.Document.ToDraft() == originalAging && cooking.ToDraft() == originalCooking,
                "Capturing one behavior must leave the other document and the original imported document unchanged.");

            var secondEditor = Field<BehaviorEditorControl>(workspace, "editor");
            Field<TextBox>(secondEditor, "name").Text = "Ingredients age slowly";
            Duration(secondEditor).Text = "3.5";
            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, cookingId));
            Require(second.Document.Blocks.Single().Name == "Ingredients age slowly" &&
                second.Document.Blocks.Single().Value.Get("stages")!.Items.Single().Get("duration")!.Text == "3.5",
                "Switching back must capture edits in the second generated file.");
            var reopened = Field<BehaviorEditorControl>(workspace, "editor");
            Require(reopened.BlockName == "Cook beef slowly" && Duration(reopened).Text == "0.12" &&
                Field<PresetProjectEntry>(workspace, "activeEntry") == first && Field<bool>(workspace, "dirty"),
                "Returning to an edited behavior must restore its latest values and retain project unsaved state.");
            var draft = PresetProject.FromDraft(project.ToDraft());
            Require(draft.Entries[0].Document.ToDraft() == first.Document.ToDraft() &&
                draft.Entries[1].Document.ToDraft() == second.Document.ToDraft(),
                "A project snapshot must retain captured edits from every visited behavior.");
            Field<TextBox>(reopened, "name").Text = "Name entered before a canceled dialog";
            Invoke(workspace, "CommitEditor");
            Require(!Field<bool>(workspace, "editorDirty") && Field<DispatcherTimer>(workspace, "editTimer").IsEnabled,
                "Capturing an edit before a canceled dialog must leave validation scheduled.");
            Invoke(workspace, "CommitEditor");
            Require(Field<DispatcherTimer>(workspace, "editTimer").IsEnabled,
                "A second dialog with no further edits must not cancel pending validation.");
            Invoke(workspace, "Refresh", cookingId);
            Require(!Field<DispatcherTimer>(workspace, "editTimer").IsEnabled &&
                ReferenceEquals(reopened, Field<BehaviorEditorControl>(workspace, "editor")),
                "Beginning validation must consume the queued check without replacing the current editor.");
            Field<TextBox>(reopened, "name").Text = "Latest name while validation is running";
            Require(Field<bool>(workspace, "editorDirty") && Field<DispatcherTimer>(workspace, "editTimer").IsEnabled &&
                ReferenceEquals(reopened, Field<BehaviorEditorControl>(workspace, "editor")) &&
                reopened.BlockName == "Latest name while validation is running",
                "An edit made during validation must stay in its existing control and queue a fresh check.");
            Invoke(workspace, "CommitEditor");
            Require(first.Document.Blocks.Single(block => block.Kind == BlockKind.Rule).Name == "Latest name while validation is running",
                "Capturing again must preserve the latest input instead of restoring an earlier validation snapshot.");
            Reset(workspace, accept: false);
            Require(Field<PresetProject>(workspace, "project") == project && project.Entries.Count == 2 &&
                Field<BehaviorEditorControl>(workspace, "editor") == reopened,
                "Canceling Reset must preserve the whole project and unfinished editor.");
            SetField(workspace, "exportRoot", "A previous mod folder");
            SetField(workspace, "draftPath", "A previous draft file");
            Reset(workspace, accept: true);
            Require(Field<PresetProject>(workspace, "project").Entries.Count == 0 &&
                Field<PresetProject>(workspace, "project").Name == project.Name &&
                Field<object?>(workspace, "editor") is null && Field<object?>(workspace, "document") is null &&
                Field<object?>(workspace, "activeEntry") is null && Field<object?>(workspace, "exportRoot") is null &&
                Field<object?>(workspace, "draftPath") is null && Field<ListBox>(workspace, "blocks").Items.Count == 0 &&
                Field<bool>(workspace, "dirty") && !Field<DispatcherTimer>(workspace, "editTimer").IsEnabled,
                "Reset must clear all documents, pending groups, editor state, and previous save targets while keeping the preset name.");
            Require(project.Entries.Count == 2 && first.Document.PendingGroups.Count == 1,
                "Reset must start a new empty project without mutating the old imported documents.");
        }
        finally
        {
            Invoke(workspace, "ResetEditor");
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        EmptyBehaviorWorkspace();
        IssueNavigationAndRemoval();
        BehaviorMovement();
        SharedPendingGroups();
    }

    private static void SharedPendingGroups()
    {
        FormRow[] forms =
        [
            new("Raw Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C99~Skyrim.esm", ItemCategories.FOOD, true, true),
            new("Cooked Beef", "FoodBeefCooked", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x721E8~Skyrim.esm", ItemCategories.FOOD, true, true)
        ];
        var project = new PresetProject();
        var owner = project.Add(PresetDocument.Parse("formsLists: [{forms: FoodBeef}]", "FOOD", PresetKind.Addon), "Group author");
        var consumer = project.Add(PresetDocument.Parse("""
            ownerLists:
            - owners: SharedFood
              stages:
              - {no: 0, name: Fresh, duration: 1}
              - {no: 1, name: Old, duration: 1, FormEditorID: FoodBeef}
              finalFormEditorID: FoodBeefCooked
            """, "FOOD", PresetKind.Custom), "Group user");
        var ownerId = owner.Document.Blocks.Single().Id;
        var consumerId = consumer.Document.Blocks.Single().Id;
        var workspace = new PresetWorkspace();
        SetField(workspace, "project", project); SetField(workspace, "activeEntry", consumer);
        SetField(workspace, "document", consumer.Document); SetField(workspace, "forms", forms);
        try
        {
            RefreshWorkspace(workspace, consumerId);
            var validations = Field<Dictionary<string, PresetValidationResult>>(workspace, "validations");
            Require(validations[consumer.Id].IsValid, "An unresolved owner must not be treated as a known source before its group exists.");
            var list = Field<ListBox>(workspace, "blocks");
            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, ownerId));
            var author = Field<BehaviorEditorControl>(workspace, "editor");
            Field<ValueEditorControl>(author, "commonEditor").Document.PendingGroups.Add(new("SharedFood", ["FoodBeef"]));
            Invoke(author, "NotifyChanged"); Invoke(workspace, "CommitEditor");
            Require(validations.Count == 0,
                "Creating a pending group must invalidate cached validation in every document that could use its name.");

            RefreshWorkspace(workspace, consumerId);
            Require(Field<Dictionary<string, PresetValidationResult>>(workspace, "validations")[consumer.Id].Errors.Values
                .SelectMany(errors => errors).Any(error => error.Contains("a later stage cannot reuse the source item")),
                "Validation must resolve a pending group from another document and reject reusing its member as a later stage.");
            var editor = Field<BehaviorEditorControl>(workspace, "editor");
            var common = Field<ValueEditorControl>(editor, "commonEditor");
            var appliesTo = Descendants<ValueEditorControl>(common).Single(control => Field<PresetField?>(control, "field")?.Key == "owners");
            var chooseGroup = Descendants<Button>(appliesTo).Single(button => Equals(button.Content, "Choose form group..."));
            Exception? failure = null;
            var response = workspace.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var picker = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                    .OfType<GroupPickerWindow>().Single();
                try
                {
                    var group = Field<ListBox>(picker, "groupList").Items.Cast<FormGroupDefinition>().Single(item => item.Name == "SharedFood");
                    Require(group.Members.SequenceEqual(["FoodBeef"]),
                        "A different document's chooser must offer the newly authored group's current members.");
                }
                catch (Exception error) { failure = error; }
                finally { picker.DialogResult = false; }
            }));
            try { chooseGroup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            finally { response.Abort(); }
            if (failure is not null) throw failure;
            Require(response.Status == DispatcherOperationStatus.Completed, "The form-group action must open the actual group chooser.");

            Invoke(workspace, "ChangeBehaviorKind", consumer.Document, consumerId, PresetKind.Addon);
            var addon = Field<PresetProject>(workspace, "project").FindByBlockId(consumerId)!.Document;
            Require(addon.Blocks.Single().Value.Get("forms")?.Text == "SharedFood",
                "Removing stages must retain a group defined in another document without expanding it or asking for individual items.");
            Invoke(workspace, "ChangeBehaviorKind", addon, consumerId, PresetKind.Custom);
            var custom = Field<PresetProject>(workspace, "project").FindByBlockId(consumerId)!.Document;
            Require(custom.Blocks.Single().Value.Get("owners")?.Text == "SharedFood",
                "Adding stages must retain the same cross-document group reference.");
        }
        finally { Invoke(workspace, "ResetEditor"); }
    }

    private static void IssueNavigationAndRemoval()
    {
        var project = new PresetProject { Name = "Issue locations" };
        var empty = project.Add(PresetDocument.Parse("ownerLists: []", "FOOD", PresetKind.Custom),
            "Empty stages", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AoT-empty-stages.yml"));
        var workspace = new PresetWorkspace();
        SetField(workspace, "project", project); SetField(workspace, "activeEntry", empty);
        SetField(workspace, "document", empty.Document);
        try
        {
            RefreshWorkspace(workspace);
            var list = Field<ListBox>(workspace, "blocks");
            var details = Field<ContentControl>(workspace, "details");
            Require(Equals(((ListBoxItem)list.SelectedItem).Tag, empty.Id) &&
                Descendants<TextBlock>(details).Any(text => text.Text == empty.OpenedPath) &&
                Descendants<Button>(details).Any(button => Equals(button.Content, "Remove file on save")),
                "First validation of an empty imported file must show its selected file issue and source path, not leave Welcome visible.");
            Require(!Field<Button>(workspace, "delete").IsEnabled && !Field<Button>(workspace, "up").IsEnabled &&
                !Field<Button>(workspace, "down").IsEnabled,
                "A file issue is not a behavior and must not enable deletion or movement.");
            var issues = Field<StackPanel>(workspace, "issueList");
            issues.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Descendants<TextBlock>(details).Any(text => text.Text == empty.OpenedPath),
                "Clicking a file issue already selected in the sidebar must still open its details.");

            var cooking = project.Add(PresetDocument.Parse("""
                heat: &heat
                  FormEditorID: Fire
                  finalFormEditorID: FoodBeefCooked
                  duration: -1
                formsLists:
                - forms: FoodBeef
                  transformers:
                  - FormEditorID: Frost
                    finalFormEditorID: FoodBeefCooked
                    duration: 1
                  - *heat
                """, "FOOD", PresetKind.Addon), "Cooking");
            var stages = project.Add(PresetDocument.Parse("""
                ownerLists:
                - owners: IngredientA
                  stages:
                  - no: 0
                    duration: invalid
                  finalFormEditorID: IngredientB
                """, "INGR", PresetKind.Custom), "Stages");
            var stageBlock = stages.Document.Blocks.Single(); stageBlock.Name = "Ingredient stages";
            RefreshWorkspace(workspace);
            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, cooking.Document.Blocks.Single(block => block.Kind == BlockKind.Rule).Id));
            var sharedIssue = issues.Children.OfType<Button>().Single(button => Descendants<TextBlock>(button)
                .Any(text => text.Text == "heat → Duration (game hours)"));
            Exception? dialogFailure = null;
            workspace.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var view = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                    .OfType<ValueEditorWindow>().Single();
                try
                {
                    Require(view.Value.Get("duration")?.Text == "-1" && view.Value.Anchor == "heat",
                        "A shared-setting issue must open its definition, not the first transformation's similarly named field.");
                    var duration = Descendants<ValueEditorControl>(view.Editor).Single(control => Field<PresetField?>(control, "field")?.Key == "duration");
                    Descendants<TextBox>(duration).Single().Text = "2";
                    Descendants<Button>(view).Single(button => Equals(button.Content, "Apply")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception failure) { dialogFailure = failure; view.DialogResult = false; }
            }));
            sharedIssue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (dialogFailure is not null) throw dialogFailure;
            RefreshWorkspace(workspace);
            Require(cooking.Document.Blocks.Single(block => block.Kind == BlockKind.Shared).Value.Get("duration")?.Text == "2" &&
                cooking.Document.Blocks.Single(block => block.Kind == BlockKind.Rule).Value.Get("transformers")!.Items[0].Get("duration")?.Text == "1",
                "Correcting a shared definition must preserve the unrelated transformation and its reference.");
            var issue = issues.Children.OfType<Button>().Single(button => Descendants<TextBlock>(button)
                .Any(text => text.Text == "Ingredient stages → Stages → Stage 0 → Duration (game hours)"));
            issue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var editor = Field<BehaviorEditorControl>(workspace, "editor");
            Require(Field<PresetProjectEntry>(workspace, "activeEntry") == stages &&
                Duration(editor).Text == "invalid" &&
                Descendants<Expander>(editor).Where(expander => Descendants<TextBox>(expander).Contains(Duration(editor))).All(expander => expander.IsExpanded),
                "A setting issue must select the affected behavior and reveal its nested stage field.");

            Invoke(workspace, "ResetEditor"); project.RemoveBehavior(stageBlock.Id);
            RefreshWorkspace(workspace);
            Require(!project.Entries.Contains(stages) && !Field<Dictionary<string, PresetValidationResult>>(workspace, "validations").ContainsKey(stages.Id) &&
                issues.Children.OfType<Button>().Count() == 1,
                "Deleting the final unsaved behavior must remove its file and stale issues while retaining a genuine imported-file issue.");
            var fileIssue = issues.Children.OfType<Button>().Single();
            fileIssue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Equals(((ListBoxItem)list.SelectedItem).Tag, empty.Id),
                "A file issue must remain reachable from another selected behavior.");
            Descendants<Button>(details).Single(button => Equals(button.Content, "Remove file on save"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RefreshWorkspace(workspace);
            Require(empty.PendingRemoval && Field<Button>(workspace, "save").Visibility == Visibility.Visible &&
                !issues.Children.OfType<Button>().Any() &&
                Field<TextBlock>(workspace, "state").Text.Contains("1 file to remove on save"),
                "Explicitly removing an empty imported file must preserve its source tracking, unblock saving, and show the pending removal.");

            Invoke(workspace, "ResetEditor");
            project.RemoveBehavior(cooking.Document.Blocks.Single(block => block.Kind == BlockKind.Rule).Id);
            RefreshWorkspace(workspace);
            Require(Field<Button>(workspace, "save").Visibility == Visibility.Visible &&
                Field<Button>(workspace, "yaml").Visibility == Visibility.Collapsed && list.Items.Count == 0,
                "Removing every behavior must allow the tracked file removal to be saved without offering empty YAML.");
        }
        finally { Invoke(workspace, "ResetEditor"); }
    }

    private static void RefreshWorkspace(PresetWorkspace workspace, string? selection = null)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(workspace.Dispatcher));
        try
        {
            var task = (Task)typeof(PresetWorkspace).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(workspace, [selection])!;
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => workspace.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void BehaviorMovement()
    {
        var document = PresetDocument.Parse("""
            templates:
              A:
                forms: $raw
                transformers:
                - FormEditorID: Fire
                  finalFormEditorID: $cooked
                  duration: 1
            formsLists:
            - A(RawA1, CookedA1)
            - A(RawA2, CookedA2)
            - forms: RawB
              transformers:
              - FormEditorID: Fire
                finalFormEditorID: CookedB
                duration: 2
            """, "FOOD", PresetKind.Addon);
        var original = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        var a1 = original[0]; var a2 = original[1]; var b = original[2];
        var project = new PresetProject { Name = "Movement" };
        var entry = project.Add(document, "Movement");
        var other = project.Add(PresetDocument.Parse("""
            formsLists:
            - forms: IngredientA
              timeModulators:
              - FormEditorID: Ice
                magnitude: 0
            """, "INGR", PresetKind.Addon), "Other document");
        var workspace = new PresetWorkspace();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(workspace.Dispatcher));
        SetField(workspace, "project", project); SetField(workspace, "activeEntry", entry);
        SetField(workspace, "document", document); SetField(workspace, "filename", project.Name);
        try
        {
            RefreshWorkspace(workspace, b.Id);
            var list = Field<ListBox>(workspace, "blocks");
            Require(Field<Button>(workspace, "up").IsEnabled && !Field<Button>(workspace, "down").IsEnabled,
                "The last behavior in a document must not move into the next document shown in the sidebar.");
            Move("up", b.Id);
            Require(Order().SequenceEqual([b.Id, a1.Id, a2.Id]) &&
                new PresetPresentation(document, []).Behaviors().Select(block => block.Id).SequenceEqual([b.Id, a1.Id]),
                "Moving B above two-row A must move above the whole behavior, not split A's rows or leave the sidebar order unchanged.");
            Require(!Field<Button>(workspace, "up").IsEnabled && Field<Button>(workspace, "down").IsEnabled,
                "Moving to the first behavior must update the arrow boundaries.");
            Move("down", b.Id);
            Require(Order().SequenceEqual([a1.Id, a2.Id, b.Id]), "Moving B back down must restore both rows of A before B.");

            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, a1.Id));
            Move("down", a1.Id);
            Require(Order().SequenceEqual([b.Id, a1.Id, a2.Id]), "Moving a two-row behavior down must move both rows together.");

            Invoke(workspace, "ResetEditor");
            var c = document.Add(BlockKind.Rule, "C", "AddonRule"); c.Value = b.Value.Clone(); c.Value.Set("forms", PresetValue.Scalar("RawC"));
            document.Blocks.Remove(a1); document.Blocks.Insert(document.Blocks.IndexOf(b), a1);
            Require(Order().SequenceEqual([a1.Id, b.Id, a2.Id, c.Id]), "The imported-order fixture must interleave A's rows around B.");
            RefreshWorkspace(workspace, a1.Id);
            Move("down", a1.Id);
            Require(Order().SequenceEqual([b.Id, a1.Id, a2.Id, c.Id]),
                "Moving an interleaved family must keep its rows together and preserve all other behaviors' relative order.");

            Invoke(workspace, "ResetEditor");
            document.Blocks.Remove(c); document.Blocks.Insert(document.Blocks.IndexOf(a2), c);
            Require(Order().SequenceEqual([b.Id, a1.Id, c.Id, a2.Id]), "The target-family fixture must place C between A's rows.");
            RefreshWorkspace(workspace, b.Id);
            Move("down", b.Id);
            Require(Order().SequenceEqual([a1.Id, a2.Id, b.Id, c.Id]) &&
                new PresetPresentation(document, []).Behaviors().Select(block => block.Id).SequenceEqual([a1.Id, b.Id, c.Id]),
                "Moving B past interleaved A must swap adjacent behaviors without jumping past C in the sidebar.");
            Require(BehaviorFamily.GetArgument(document, a1.Id, "raw").Text == "RawA1" &&
                BehaviorFamily.GetArgument(document, a1.Id, "cooked").Text == "CookedA1" &&
                BehaviorFamily.GetArgument(document, a2.Id, "raw").Text == "RawA2" &&
                BehaviorFamily.GetArgument(document, a2.Id, "cooked").Text == "CookedA2" &&
                b.Value.Get("transformers")!.Items.Single().Get("duration")!.Text == "2" && other.Document.Blocks.Count == 1,
                "Behavior movement must preserve each row's arguments, shared settings, and other documents.");

            string[] Order() => document.Blocks.Where(block => block.Kind == BlockKind.Rule).Select(block => block.Id).ToArray();
            void Move(string buttonName, string selectedId)
            {
                var button = Field<Button>(workspace, buttonName);
                Require(button.IsEnabled, "The requested behavior movement must be available.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var selection = ((ListBoxItem)list.SelectedItem).Tag as string;
                RefreshWorkspace(workspace, selection);
                Require(selection == selectedId && Equals(((ListBoxItem)list.SelectedItem).Tag, selectedId),
                    "Moving a behavior must keep that behavior selected before and after validation.");
            }
        }
        finally
        {
            Invoke(workspace, "ResetEditor");
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void EmptyBehaviorWorkspace()
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
        var project = new PresetProject { Name = "Empty rows" };
        var entry = project.Add(document, "Meat");
        var firstId = document.Blocks.First(block => block.Kind == BlockKind.Rule).Id;
        var definitionId = document.Blocks.Single(block => block.Key == "meat").Id;
        var workspace = new PresetWorkspace();
        SetField(workspace, "project", project); SetField(workspace, "activeEntry", entry);
        SetField(workspace, "document", document); SetField(workspace, "filename", project.Name);
        try
        {
            Invoke(workspace, "RenderBlocks", firstId); Invoke(workspace, "SelectBlock");
            var editor = Field<BehaviorEditorControl>(workspace, "editor");
            Field<TabControl>(editor, "tabs").SelectedIndex = 1;
            Field<DataGrid>(editor, "table").SelectAll();
            Invoke(editor, "RemoveRows"); Invoke(workspace, "CommitEditor");
            Invoke(workspace, "RenderBlocks", definitionId); Invoke(workspace, "SelectBlock");
            var list = Field<ListBox>(workspace, "blocks");
            Require(list.Items.Count == 1 && Equals(((ListBoxItem)list.SelectedItem).Tag, definitionId) &&
                ReferenceEquals(editor, Field<BehaviorEditorControl>(workspace, "editor")) &&
                Field<DataGrid>(editor, "table").Items.Count == 0,
                "Deleting all variable rows must preserve the active behavior and empty table during workspace refresh.");
            Require(Field<StackPanel>(workspace, "blockActions").Visibility == Visibility.Visible &&
                Field<Button>(workspace, "delete").IsEnabled && !Field<Button>(workspace, "up").IsEnabled &&
                !Field<Button>(workspace, "down").IsEnabled,
                "An empty behavior must remain selectable and deletable without offering rule-priority movement.");

            var restored = PresetProject.FromDraft(project.ToDraft());
            Invoke(workspace, "ResetEditor");
            SetField(workspace, "project", restored); SetField(workspace, "activeEntry", restored.Entries[0]);
            SetField(workspace, "document", restored.Entries[0].Document);
            Invoke(workspace, "RenderBlocks", definitionId); Invoke(workspace, "SelectBlock");
            var reopened = Field<BehaviorEditorControl>(workspace, "editor");
            Require(reopened.RuleId == definitionId && Field<DataGrid>(reopened, "table").Items.Count == 0 &&
                Field<TabControl>(reopened, "tabs").Items.Count == 2,
                "Reopening a draft must retain Settings and Variables for the empty behavior.");
            Field<TabControl>(reopened, "tabs").SelectedIndex = 1;
            Invoke(reopened, "AddRow"); Invoke(workspace, "CommitEditor");
            Invoke(workspace, "RenderBlocks", reopened.RuleId); Invoke(workspace, "SelectBlock");
            Require(list.Items.Count == 1 && ReferenceEquals(reopened, Field<BehaviorEditorControl>(workspace, "editor")) &&
                Field<DataGrid>(reopened, "table").Items.Count == 1,
                "Adding a row after draft reopening must reuse the retained behavior and keep the same editor.");
        }
        finally { Invoke(workspace, "ResetEditor"); }
    }

    private static void Reset(PresetWorkspace workspace, bool accept)
    {
        Exception? failure = null;
        workspace.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var dialog = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                .OfType<Window>().Single(window => window.Title == "Reset preset");
            try
            {
                var button = Descendants<Button>(dialog).Single(button => Equals(button.Content, accept ? "Clear preset" : "Cancel"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error) { failure = error; dialog.DialogResult = false; }
        }));
        Invoke(workspace, "ResetPreset");
        if (failure is not null) throw failure;
    }

    private static TextBox Duration(BehaviorEditorControl editor)
    {
        var field = Descendants<ValueEditorControl>(editor).Single(control => Field<PresetField?>(control, "field")?.Key == "duration");
        return Descendants<TextBox>(field).Single();
    }

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
    private static void Invoke(object owner, string name, params object?[] arguments) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
