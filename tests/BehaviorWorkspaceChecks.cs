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

    private static void RefreshWorkspace(PresetWorkspace workspace)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(workspace.Dispatcher));
        try
        {
            var task = (Task)typeof(PresetWorkspace).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(workspace, [null])!;
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
                Field<MenuItem>(workspace, "up").Visibility == Visibility.Collapsed &&
                Field<MenuItem>(workspace, "down").Visibility == Visibility.Collapsed,
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
