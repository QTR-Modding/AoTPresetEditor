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
