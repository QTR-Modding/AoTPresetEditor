using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class BehaviorEditorChecks
{
    public static void Run()
    {
        NestedVariationsAndCapture();
        VariableNameCancellation();
        PasteNamedColumns();
        ChooseFormPreservesCell();
        PickerSurvivesPendingWorkspaceEdit();
        DeleteSelectedRows();
        RepeatedParameters();
        CollectionVariations();
        StageApplicability();
        ValidationSources();
        IssueLocations();
        ReferencedIssueVariables();
        SourceFilters();
        BehaviorSummaries();
    }

    private static void NestedVariationsAndCapture()
    {
        var document = PresetDocument.Parse("""
            formsLists:
            - forms: FoodBeef
              transformers:
              - FormEditorID: Fire
                finalFormEditorID: CookedBeef
                duration: 2
            """, "FOOD", PresetKind.Addon);
        var rule = document.Blocks.Single(); rule.Name = "Cooking";
        var original = document.ToYaml();
        var editor = new BehaviorEditorControl(document, [], rule.Id);
        var common = Common(editor);
        var transformation = CollectionEntry(common, "transformers", 0);
        Input(transformation, "duration").Text = "3";
        MakeVariable(transformation, "duration", "cooking_hours");
        var family = BehaviorFamily.Find(editor.Document, editor.RuleId)!;
        var parameter = family.Parameters.Single();
        Require(BehaviorFamily.GetValue(family.Definition.Value, ["transformers", "0", "duration"])!.Text == "$" + parameter,
            "The nested field's actual Vary action must use its complete collection path.");
        Require(BehaviorFamily.GetArgument(editor.Document, rule.Id, parameter).Text == "3",
            "Varying a field must capture the current input first.");
        var tabs = Field<TabControl>(editor, "tabs");
        Require(tabs.SelectedIndex == 0 && Equals(((TabItem)tabs.Items[1]).Header, "Variables"),
            "Making a field variable must stay on Settings and expose the Variables tab.");
        Require(parameter == "cooking_hours" && Equals(Field<DataGrid>(editor, "table").Columns[1].Header, parameter),
            "The chosen variable name must label its column.");
        tabs.SelectedIndex = 1; SelectCell(editor, 0, 0);
        Click(Field<MenuItem>(editor, "duplicate"));
        var duplicateId = BehaviorFamily.Find(editor.Document, rule.Id)!.Rows[1].Id;
        CellInput(editor).Text = "7";
        SelectCell(editor, 0, 0);
        CellInput(editor).Text = "5";
        SelectCell(editor, 1, 0);
        Require(CellInput(editor).Text == "7", "Changing rows lost the previous row's input.");
        Click(Field<Button>(editor, "addRow"));
        var newId = BehaviorFamily.Find(editor.Document, rule.Id)!.Rows[2].Id;
        CellInput(editor).Text = "9";
        tabs.SelectedIndex = 0;
        Require(BehaviorFamily.GetArgument(editor.Document, newId, parameter).Text == "9",
            "Returning to Settings must capture the unfinished row without removing its variable.");
        Input(CollectionEntry(Common(editor), "transformers", 0), "finalFormEditorID").Text = "RoastedBeef";
        Field<TextBox>(editor, "name").Text = "Roasting";
        tabs.SelectedIndex = 1;
        SelectCell(editor, 1, 0);
        Require(CellInput(editor).Text == "7", "Editing common settings changed an existing row's variation.");
        tabs.SelectedIndex = 0; editor.Capture();
        family = BehaviorFamily.Find(editor.Document, rule.Id)!;
        Require(family.Definition.Name == "Roasting" && BehaviorFamily.GetValue(family.Definition.Value, ["transformers", "0", "finalFormEditorID"])!.Text == "RoastedBeef",
            "Switching tabs or capturing lost the common edit or behavior name.");
        Require(BehaviorFamily.GetArgument(editor.Document, rule.Id, parameter).Text == "5" &&
            BehaviorFamily.GetArgument(editor.Document, duplicateId, parameter).Text == "7" &&
            BehaviorFamily.GetArgument(editor.Document, newId, parameter).Text == "9", "The editor must capture each row independently.");
        Require(document.ToYaml() == original, "The behavior editor must keep the source document isolated until capture is adopted.");
    }

    private static void RepeatedParameters()
    {
        var document = PresetDocument.Parse("""
            templates:
              cook:
                forms: FoodBeef
                transformers:
                - FormEditorID: Fire
                  finalFormEditorID: CookedBeef
                  duration: $hours
                - FormEditorID: Oven
                  finalFormEditorID: CookedBeef
                  duration: $hours
            formsLists:
            - cook(2)
            - cook(4)
            """, "FOOD", PresetKind.Addon);
        var rule = document.Blocks.First(block => block.Kind == BlockKind.Rule);
        var editor = new BehaviorEditorControl(document, [], rule.Id);
        Field<TabControl>(editor, "tabs").SelectedIndex = 1;
        Require(Field<DataGrid>(editor, "table").Columns.Count == 2, "A repeated variable must occupy one column.");
        SelectCell(editor, 1, 0); CellInput(editor).Text = "6";
        SelectCell(editor, 0, 0); CellInput(editor).Text = "6";
        Field<TextBox>(editor, "name").Text = "Slow cooking";
        Field<TabControl>(editor, "tabs").SelectedIndex = 0;
        ClearVariable(CollectionEntry(Common(editor), "transformers", 0), "duration"); editor.Capture();
        var family = BehaviorFamily.Find(editor.Document, editor.RuleId)!;
        Require(!family.IsTemplate && family.Definition.Name == "Slow cooking" &&
            family.Definition.Value.Get("transformers")!.Items.All(item => item.Get("duration") is null),
            "Clearing a variable must remove every occurrence instead of retaining a row's value, while preserving the behavior name.");
        Require(family.Rows.Count == 1 && editor.Document.Blocks.Count == 1,
            "Clearing the last variable must simplify identical adjacent uses into one ordinary rule.");
        Require(Field<TabControl>(editor, "tabs").Items.Count == 1,
            "Removing the final variable must close the empty Variables tab.");
    }

    private static void VariableNameCancellation()
    {
        var document = PresetDocument.Parse("""
            formsLists:
            - forms: FoodBeef
              transformers:
              - {FormEditorID: Fire, finalFormEditorID: CookedBeef, duration: 2}
            """, "FOOD", PresetKind.Addon);
        var editor = new BehaviorEditorControl(document, [], document.Blocks.Single().Id);
        var transformation = CollectionEntry(Common(editor), "transformers", 0);
        Input(transformation, "duration").Text = "3"; editor.Capture();
        var before = editor.Document.ToYaml();
        foreach (var key in new[] { "duration", "sound" })
        {
            MakeVariable(transformation, key, null);
            Require(editor.Document.ToYaml() == before && !VariableChoice(transformation, key).IsChecked.GetValueOrDefault(),
                "Canceling Name variable must leave the field and checkbox unchanged, including an absent optional field.");
            Require(Field<TabControl>(editor, "tabs").Items.Count == 1 &&
                Field<TabControl>(editor, "tabs").SelectedIndex == 0,
                "Canceling variable creation must not create a template or a Variables tab.");
        }
        Require(Input(transformation, "duration").Text == "3" && transformation.Value.Get("sound") is null,
            "Naming cancellation must preserve unfinished input and must not enable an optional setting.");
    }

    private static void PasteNamedColumns()
    {
        var document = PresetDocument.Parse("""
            formsLists:
            - forms: FoodBeef
              transformers:
              - {FormEditorID: Fire, finalFormEditorID: CookedBeef, duration: 2}
            """, "FOOD", PresetKind.Addon);
        var editor = new BehaviorEditorControl(document, [], document.Blocks.Single().Id);
        MakeVariable(Common(editor), "forms", "raw_item");
        MakeVariable(CollectionEntry(Common(editor), "transformers", 0), "finalFormEditorID", "cooked_item");
        var tabs = Field<TabControl>(editor, "tabs"); tabs.SelectedIndex = 1; SelectCell(editor, 0, 0);
        var table = Field<DataGrid>(editor, "table");
        Require(table.Columns.Skip(1).Select(column => column.Header).SequenceEqual(new object[] { "raw_item", "cooked_item" }),
            "Each chosen variable name must appear once as a table column in parameter order.");
        PasteCells(editor, "FoodBeef\tCookedBeef\nFoodChicken\tGrilledChicken"); editor.Capture();
        var family = BehaviorFamily.Find(editor.Document, editor.RuleId)!;
        Require(family.Rows.Count == 2 &&
            BehaviorFamily.GetArgument(editor.Document, family.Rows[0].Id, "raw_item").Text == "FoodBeef" &&
            BehaviorFamily.GetArgument(editor.Document, family.Rows[1].Id, "raw_item").Text == "FoodChicken" &&
            BehaviorFamily.GetArgument(editor.Document, family.Rows[1].Id, "cooked_item").Text == "GrilledChicken",
            "Pasting two columns must fill their selected variables and add the missing row.");
        SelectCell(editor, 0, 1);
        PasteCells(editor, "RoastedBeef\nRoastedChicken"); editor.Capture();
        family = BehaviorFamily.Find(editor.Document, editor.RuleId)!;
        Require(BehaviorFamily.GetArgument(editor.Document, family.Rows[0].Id, "raw_item").Text == "FoodBeef" &&
            BehaviorFamily.GetArgument(editor.Document, family.Rows[0].Id, "cooked_item").Text == "RoastedBeef" &&
            BehaviorFamily.GetArgument(editor.Document, family.Rows[1].Id, "cooked_item").Text == "RoastedChicken",
            "Pasting into the second column must keep the first column untouched without a separate field selector.\n" + editor.Document.ToYaml());
        var before = editor.Document.ToDraft();
        try { PasteCells(editor, "Wrong\tExtra"); throw new InvalidOperationException("An overwide paste was accepted."); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException failure)
        {
            Require(failure.Message.Contains("2 columns", StringComparison.Ordinal) && failure.Message.Contains("Only 1 fit", StringComparison.Ordinal) &&
                failure.Message.Contains("cooked_item", StringComparison.Ordinal) && editor.Document.ToDraft() == before,
                "An overwide paste must identify the column mismatch and leave every value unchanged.");
        }
        var host = new Window { Content = editor, Width = 1000, Height = 800,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); SelectCell(editor, 1, 1);
            FocusGridCell(host, table, 1, 2);
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(host, Field<Button>(editor, "addRow"));
            Require(!table.CurrentCell.IsValid, "The paste test must leave the focused grid cell before invoking its action.");
            PasteCells(editor, "BakedChicken"); editor.Capture();
            family = BehaviorFamily.Find(editor.Document, editor.RuleId)!;
            Require(family.Rows.Count == 2 &&
                BehaviorFamily.GetArgument(editor.Document, family.Rows[0].Id, "raw_item").Text == "FoodBeef" &&
                BehaviorFamily.GetArgument(editor.Document, family.Rows[0].Id, "cooked_item").Text == "RoastedBeef" &&
                BehaviorFamily.GetArgument(editor.Document, family.Rows[1].Id, "raw_item").Text == "FoodChicken" &&
                BehaviorFamily.GetArgument(editor.Document, family.Rows[1].Id, "cooked_item").Text == "BakedChicken",
                "Pasting after focus leaves the grid must retain the chosen row and variable, without appending a row or changing other cells.");
        }
        finally { host.Close(); }
    }

    private static void PasteCells(BehaviorEditorControl editor, string text) =>
        typeof(BehaviorEditorControl).GetMethod("PasteCells", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, [text]);

    private static void ChooseFormPreservesCell()
    {
        var document = PresetDocument.Parse("""
            templates:
              cook:
                owners: $raw
                stages:
                - {no: 0, name: "", duration: 1}
                finalFormEditorID: $cooked
            ownerLists:
            - cook(RawBeef, null)
            """, "FOOD", PresetKind.Custom);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var cooked = new FormRow("Cooked Beef", "CookedBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x721E8~Skyrim.esm", ItemCategories.FOOD, IsBoundObject: true);
        var editor = new BehaviorEditorControl(document, [cooked], rule.Id);
        var host = new Window { Content = editor, Width = 1000, Height = 800,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false };
        Exception? failure = null;
        try
        {
            host.Show();
            Field<TabControl>(editor, "tabs").SelectedIndex = 1;
            SelectCell(editor, 0, 1); host.UpdateLayout();
            var table = Field<DataGrid>(editor, "table");
            FocusGridCell(host, table, 0, 2);
            Require(table.CurrentCell.Column == table.Columns[2], "The final-item cell must own logical focus before opening its picker.");
            var cell = Field<ValueEditorControl>(editor, "cellEditor");
            var button = Descendants<Button>(cell).Single(item => Equals(item.Content, "Choose form..."));
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(host, button);
            Require(!table.CurrentCell.IsValid, "Leaving the focused grid cell must exercise WPF's current-cell clearing path.");
            Require(ReferenceEquals(cell, Field<ValueEditorControl>(editor, "cellEditor")) &&
                Field<string>(editor, "cellRowId") == rule.Id && Field<string>(editor, "cellParameter") == "cooked",
                "Moving focus from a selected row to Choose form must preserve the selected cell and its button.");
            var response = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var picker = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                    .OfType<FormPickerWindow>().Single();
                try
                {
                    Require(Field<string>(editor, "cellRowId") == rule.Id && Field<string>(editor, "cellParameter") == "cooked",
                        "Opening the form picker must retain the selected row and variable.");
                    Field<DataGrid>(picker, "results").SelectedIndex = 0;
                    Click(Field<Button>(picker, "choose"));
                }
                catch (Exception error) { failure = error; picker.DialogResult = false; }
            }));
            try { Click(button); }
            finally { response.Abort(); }
            if (failure is not null) throw failure;
            Require(response.Status == DispatcherOperationStatus.Completed, "Choose form must open its modal picker.");
            editor.Capture();
            Require(Field<string>(editor, "cellRowId") == rule.Id &&
                BehaviorFamily.GetArgument(editor.Document, rule.Id, "cooked").Text == cooked.Identifier &&
                BehaviorFamily.GetArgument(editor.Document, rule.Id, "raw").Text == "RawBeef",
                "The picker must update the final item without replacing the source item or selected variable.");
        }
        finally { host.Close(); }
    }

    private static void DeleteSelectedRows()
    {
        var document = PresetDocument.Parse("""
            templates:
              cook:
                owners: $raw
                stages:
                - {no: 0, name: "", duration: 1}
                finalFormEditorID: CookedBeef
            ownerLists:
            - cook(RawBeef)
            - cook(RawChicken)
            - cook(RawVenison)
            """, "FOOD", PresetKind.Custom);
        var rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        var editor = new BehaviorEditorControl(document, [], rules[0].Id);
        var host = new Window { Content = editor };
        var source = HwndSource.FromHwnd(new WindowInteropHelper(host).EnsureHandle());
        try
        {
            Field<TabControl>(editor, "tabs").SelectedIndex = 1;
            SelectCell(editor, 0, 0);
            var table = Field<DataGrid>(editor, "table");
            Require(table.SelectionMode == DataGridSelectionMode.Extended && table.SelectionUnit == DataGridSelectionUnit.FullRow,
                "The Variables table must retain WPF's Shift range selection for whole rows.");
            table.SelectedItems.Add(table.Items[1]);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            table.RaiseEvent(key);
            Require(key.Handled && table.Items.Count == 1 && BehaviorFamily.Find(editor.Document, editor.RuleId)!.Rows.Single().Id == rules[2].Id,
                "Delete must remove the complete row selection and retain the unselected row.");
            var input = CellInput(editor);
            var back = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            input.RaiseEvent(back);
            Require(!back.Handled && table.Items.Count == 1, "Backspace in an input must remain text editing, not delete its row.");
            back = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            table.RaiseEvent(back);
            var family = BehaviorFamily.Find(editor.Document, editor.RuleId)!;
            Require(back.Handled && table.Items.Count == 0 && family.Rows.Count == 0 && family.Definition.IsBehavior &&
                family.Parameters.SequenceEqual(["raw"]) && family.Definition.Value.Get("finalFormEditorID")!.Text == "CookedBeef",
                "Backspace on the final selected row must leave an empty table and preserve the behavior's common settings and variables.");
            Require(Field<object?>(editor, "cellEditor") is null && Field<ContentControl>(editor, "cellSection").Visibility == Visibility.Collapsed,
                "Deleting every row must remove the obsolete selected-cell editor.");
            Click(Field<Button>(editor, "addRow"));
            family = BehaviorFamily.Find(editor.Document, editor.RuleId)!;
            Require(table.Items.Count == 1 && family.Rows.Count == 1 && BehaviorFamily.GetArgument(editor.Document, editor.RuleId, "raw").IsUnsetArgument,
                "Add row must remain usable after deleting all rows and create a new empty argument.");
        }
        finally { host.Close(); }
    }

    private static void PickerSurvivesPendingWorkspaceEdit()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var rule = document.Add(BlockKind.Rule, "Cooking", "CustomRule");
        rule.Value = ValueEditorWindow.NewValue("CustomRule");
        var project = new PresetProject { Name = "New preset" };
        var entry = project.Add(document, "Cooking");
        var cooked = new FormRow("Cooked Beef", "CookedBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x721E8~Skyrim.esm", ItemCategories.FOOD, IsBoundObject: true);
        var workspace = new PresetWorkspace();
        var host = new Window { Content = workspace, Width = 1100, Height = 850,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(workspace.Dispatcher));
        void Set(string name, object value) => typeof(PresetWorkspace).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, value);
        void Invoke(string name, params object?[] arguments) => typeof(PresetWorkspace).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(workspace, arguments);
        Set("project", project); Set("activeEntry", entry); Set("document", document); Set("forms", new[] { cooked });
        try
        {
            host.Show(); Invoke("RenderBlocks", rule.Id); Invoke("SelectBlock");
            var editor = Field<BehaviorEditorControl>(workspace, "editor");
            MakeVariable(Common(editor), "owners", "raw");
            MakeVariable(Common(editor), "finalFormEditorID", "cooked");
            Field<TabControl>(editor, "tabs").SelectedIndex = 1;
            Click(Field<Button>(editor, "addRow"));
            Click(Field<Button>(editor, "addRow"));
            SelectCell(editor, 2, 1);
            var rowId = Field<string>(editor, "cellRowId");
            var cell = Field<ValueEditorControl>(editor, "cellEditor");
            var button = Descendants<Button>(cell).Single(item => Equals(item.Content, "Choose form..."));
            host.UpdateLayout(); button.Focus();
            Require(Field<DispatcherTimer>(workspace, "editTimer").IsEnabled,
                "The interactive creation flow must leave validation pending before the picker is pressed.");
            var frame = new DispatcherFrame();
            var completed = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            completed.Tick += (_, _) => { completed.Stop(); frame.Continue = false; };
            completed.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { completed.Stop(); }
            Require(ReferenceEquals(editor, Field<BehaviorEditorControl>(workspace, "editor")) &&
                ReferenceEquals(cell, Field<ValueEditorControl>(editor, "cellEditor")) &&
                Field<string>(editor, "cellRowId") == rowId && Field<string>(editor, "cellParameter") == "cooked" && button.IsVisible,
                "Pending workspace validation must not replace the chosen cell or button after interactive variable and row creation.");
            Exception? failure = null;
            var response = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var picker = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                    .OfType<FormPickerWindow>().Single();
                try { Field<DataGrid>(picker, "results").SelectedIndex = 0; Click(Field<Button>(picker, "choose")); }
                catch (Exception error) { failure = error; picker.DialogResult = false; }
            }));
            try { Click(button); }
            finally { response.Abort(); }
            if (failure is not null) throw failure;
            Require(response.Status == DispatcherOperationStatus.Completed && Field<string>(editor, "cellRowId") == rowId &&
                BehaviorFamily.GetArgument(editor.Document, rowId, "cooked").Text == cooked.Identifier,
                "The picker must open and update the chosen row after pending workspace validation finishes.");
        }
        finally { Invoke("ResetEditor"); host.Close(); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void CollectionVariations()
    {
        var document = PresetDocument.Parse("""
            formsLists:
            - forms: FoodBeef
              transformers:
              - FormEditorID: Fire
                finalFormEditorID: CookedBeef
                duration: 2
            """, "FOOD", PresetKind.Addon);
        var rule = document.Blocks.Single();
        var editor = new BehaviorEditorControl(document, [], rule.Id);
        MakeVariable(Common(editor), "transformers", "heat_settings");
        Field<TabControl>(editor, "tabs").SelectedIndex = 1; SelectCell(editor, 0, 0);
        var cell = Field<ValueEditorControl>(editor, "cellEditor");
        Require(!Descendants<CheckBox>(cell).Any(box => Equals(box.Content, "Variable")),
            "A per-row collection editor must not offer an unconnected Variable checkbox inside that row.");
        var entry = Descendants<ValueEditorControl>(cell).First(control => control.Value.Kind == ValueKind.Mapping);
        Input(entry, "duration").Text = "8";
        Field<TabControl>(editor, "tabs").SelectedIndex = 0; editor.Capture();
        var parameter = BehaviorFamily.Find(editor.Document, rule.Id)!.Parameters.Single();
        Require(BehaviorFamily.GetArgument(editor.Document, rule.Id, parameter).Items[0].Get("duration")!.Text == "8",
            "Editing a field inside a varied collection must capture the collection argument.");
    }

    private static void StageApplicability()
    {
        var document = PresetDocument.Parse("""
            templates:
              age:
                owners: FoodBeef
                finalFormEditorID: SpoiledBeef
                stages:
                - no: 0
                  name: Fresh
                  duration: $hours
                  value: 4
            ownerLists:
            - age(4)
            """, "FOOD", PresetKind.Custom);
        var rule = document.Blocks.First(block => block.Kind == BlockKind.Rule);
        var editor = new BehaviorEditorControl(document, [], rule.Id);
        var stage = CollectionEntry(Common(editor), "stages", 0);
        Require(Card(stage, "value").Child is Expander { Header: "Unused: Value" } &&
            Card(stage, "weight").Visibility == Visibility.Collapsed && Card(stage, "mgeffect").Visibility == Visibility.Collapsed,
            "Behavior mode must retain existing stage-0 overrides as unused and hide absent overrides.");
        editor.Capture();
        Require(BehaviorFamily.GetValue(BehaviorFamily.Find(editor.Document, rule.Id)!.Definition.Value, ["stages", "0", "value"])!.Text == "4",
            "Hiding stage-0 overrides must not delete imported values.");
    }

    private static void ValidationSources()
    {
        var document = PresetDocument.Parse("""
            templates:
              cook:
                forms: $a
                transformers:
                - FormEditorID: Fire
                  finalFormEditorID: CookedBeef
                  duration: $d
            formsLists:
            - cook(FoodBeef, 2)
            - cook(FoodChicken, 4)
            """, "FOOD", PresetKind.Addon);
        var rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        var editor = new BehaviorEditorControl(document, [], rules[0].Id);
        Field<TabControl>(editor, "tabs").SelectedIndex = 1;
        SelectCell(editor, 1, 1); CellInput(editor).Text = "-1"; editor.Capture();
        var result = PresetValidation.Check(editor.Document, CancellationToken.None).GetAwaiter().GetResult();
        Require(result.Errors[rules[0].Id].Count == 0 && result.Errors[rules[1].Id].Any(error => error.Contains("Duration", StringComparison.OrdinalIgnoreCase)),
            "A row's invalid variation must be reported against that authored rule instead of its common definition or another row.");
        Field<TabControl>(editor, "tabs").SelectedIndex = 0;
        Require(editor.FocusIssue(result.Errors[rules[1].Id].First(), rules[1].Id) &&
            Field<TabControl>(editor, "tabs").SelectedIndex == 1 && Field<string>(editor, "cellRowId") == rules[1].Id &&
            Field<string>(editor, "cellParameter") == "d" && CellInput(editor).Text == "-1",
            "Following a duration issue must select its offending row and duration variable, even when source variable 'a' appears inside the error text.");
        Click(Field<Button>(editor, "addRow"));
        var added = BehaviorFamily.Find(editor.Document, editor.RuleId)!.Rows.Last();
        result = PresetValidation.Check(editor.Document, CancellationToken.None).GetAwaiter().GetResult();
        var missing = result.Errors[added.Id].Single(error => error.StartsWith("Fill the missing values", StringComparison.Ordinal));
        Field<TabControl>(editor, "tabs").SelectedIndex = 0;
        Require(!missing.Contains("template", StringComparison.OrdinalIgnoreCase) && editor.FocusIssue(missing, added.Id) &&
            Field<string>(editor, "cellRowId") == added.Id,
            "The plain-language missing-value issue must still open the missing cell in the affected row.");
    }

    private static void IssueLocations()
    {
        var stageDocument = PresetDocument.Parse("""
            ownerLists:
            - owners: FoodBeef
              stages:
              - {no: 0, name: Fresh, duration: 0}
              - {no: 3, name: Stale, duration: 1}
              finalFormEditorID: FoodCookedBeef
            """, "FOOD", PresetKind.Custom);
        var stageRule = stageDocument.Blocks.Single(); stageRule.Name = "Beef stages";
        Require(BehaviorEditorControl.IssueLocation(stageDocument, stageRule.Id, "Stages 1: Duration (game hours): duration must be greater than zero.") ==
            "Beef stages → Stages → Stage 0 → Duration (game hours)",
            "A stage issue must identify its actual stage number rather than its one-based list position.");
        Require(BehaviorEditorControl.IssueLocation(stageDocument, stageRule.Id, "Stage 3: this replacement item is already used by another stage.") ==
            "Beef stages → Stages → Stage 3 → Stage item",
            "A cross-stage validation issue must identify the affected stage item.");
        Require(BehaviorEditorControl.IssueLocation(stageDocument, stageRule.Id, "Final item: choose an item.") ==
            "Beef stages → Stages → Final item",
            "A final-item issue must include the section containing its input.");

        var document = PresetDocument.Parse("""
            templates:
              cook:
                forms: $raw
                transformers:
                - {FormEditorID: Fire, finalFormEditorID: CookedBeef, duration: $first}
                - {FormEditorID: Frost, finalFormEditorID: FrozenBeef, duration: $second}
                - {FormEditorID: Other, finalFormEditorID: BurntBeef, duration: -3}
            formsLists:
            - cook(FoodBeef, 1, 2)
            - cook(FoodChicken, 1, -1)
            """, "FOOD", PresetKind.Addon);
        var rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        var family = BehaviorFamily.Find(document, rules[0].Id)!; family.Definition.Name = "Cooking";
        var editor = new BehaviorEditorControl(document, [], rules[0].Id);
        var result = PresetValidation.Check(document, CancellationToken.None).GetAwaiter().GetResult();
        var variableError = result.Errors[rules[1].Id].Single(error => error.StartsWith("Transformations 2:", StringComparison.Ordinal));
        Require(BehaviorEditorControl.IssueLocation(document, rules[1].Id, variableError) == "Cooking → Variables → Row 2 → second",
            "The issue location must name the offending row and variable when several fields have the same label.");
        Require(editor.FocusIssue(variableError, rules[1].Id) && Field<string>(editor, "cellParameter") == "second" &&
            Field<string>(editor, "cellRowId") == rules[1].Id && CellInput(editor).Text == "-1",
            "A second transformation's duration issue must open its own variable, not the first duration column.");
        var commonError = result.Errors[rules[1].Id].Single(error => error.StartsWith("Transformations 3:", StringComparison.Ordinal));
        Require(editor.FocusIssue(commonError, rules[1].Id) && Field<TabControl>(editor, "tabs").SelectedIndex == 0 &&
            BehaviorEditorControl.IssueLocation(document, rules[1].Id, commonError) == "Cooking → Transformations → Transformation 3 → Duration (game hours)",
            "An invalid common setting must open Settings even when another field with the same label is variable.");
    }

    private static void ReferencedIssueVariables()
    {
        var document = PresetDocument.Parse("""
            templates:
              stages:
                owners: FoodBeef
                stages:
                - {no: 0, name: Fresh, duration: 1}
                - {no: $stage_number, name: Stale, duration: 1}
                finalFormEditorID: FoodCookedBeef
            ownerLists:
            - stages(1)
            - stages(0)
            """, "FOOD", PresetKind.Custom);
        var rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        var result = PresetValidation.Check(document, CancellationToken.None).GetAwaiter().GetResult();
        var presentation = new PresetPresentation(document, [], expandedYaml: result.Expanded);
        var issue = result.Errors[rules[1].Id].Single(error => error.StartsWith("Stage number 0 is used", StringComparison.Ordinal));
        var editor = new BehaviorEditorControl(document, [], rules[0].Id); editor.UpdatePresentation(presentation);
        Require(BehaviorEditorControl.IssueLocation(document, rules[1].Id, issue, presentation.EffectiveValue(rules[1])) ==
            "stages → Variables → Row 2 → stage_number" && editor.FocusIssue(issue, rules[1].Id) &&
            Field<string>(editor, "cellParameter") == "stage_number" && Field<string>(editor, "cellRowId") == rules[1].Id && CellInput(editor).Text == "0",
            "A duplicate stage number supplied by a variable must target its expanded row, not the common initial stage.");

        document = PresetDocument.Parse("""
            templates:
              transformation:
                FormEditorID: Fire
                finalFormEditorID: $item
                duration: $time
              cooking:
                forms: FoodBeef
                transformers:
                - {FormEditorID: Frost, finalFormEditorID: FrozenBeef, duration: 2}
                - transformation(CookedBeef, $hours)
            formsLists:
            - cooking(1)
            - cooking(-1)
            """, "FOOD", PresetKind.Addon);
        rules = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        result = PresetValidation.Check(document, CancellationToken.None).GetAwaiter().GetResult();
        presentation = new PresetPresentation(document, [], expandedYaml: result.Expanded);
        issue = result.Errors[rules[1].Id].Single(error => error.StartsWith("Transformations 2:", StringComparison.Ordinal));
        editor = new BehaviorEditorControl(document, [], rules[0].Id); editor.UpdatePresentation(presentation);
        Require(BehaviorEditorControl.IssueLocation(document, rules[1].Id, issue, presentation.EffectiveValue(rules[1])) ==
            "cooking → Variables → Row 2 → hours" && editor.FocusIssue(issue, rules[1].Id) &&
            Field<string>(editor, "cellParameter") == "hours" && Field<string>(editor, "cellRowId") == rules[1].Id && CellInput(editor).Text == "-1",
            "An invalid argument passed through a nested template must navigate to the outer behavior's actual variable row.");
    }

    private static void BehaviorSummaries()
    {
        var document = PresetDocument.Parse("""
            templates:
              cook:
                forms: $raw
                transformers:
                - FormEditorID: Fire
                  finalFormEditorID: $cooked
                  duration: $hours
            formsLists:
            - cook(Raw, Cooked, 2)
            """, "FOOD", PresetKind.Addon);
        var rule = document.Blocks.First(block => block.Kind == BlockKind.Rule);
        var editor = new BehaviorEditorControl(document, [], rule.Id);
        var labels = Descendants<TextBlock>(Common(editor)).Select(label => label.Text).ToArray();
        var transformations = Descendants<ValueEditorControl>(Common(editor)).Single(control => Field<PresetField?>(control, "field")?.Key == "transformers");
        var summaries = Field<ListBox>(transformations, "entryList").Items.Cast<ListBoxItem>().Select(item => ((TextBlock)item.Content).Text).ToArray();
        Require(summaries.Any(label => label.Contains("Produces Varies per item", StringComparison.Ordinal)) &&
            !labels.Any(label => label.Contains("$raw", StringComparison.Ordinal) || label.Contains("$cooked", StringComparison.Ordinal) || label.Contains("$hours", StringComparison.Ordinal)),
            "Behavior field feedback and collection summaries must describe variations without exposing parameter names.");
        var presentation = new PresetPresentation(document, []);
        var transformer = document.Blocks.First(block => block.Kind == BlockKind.Template).Value.Get("transformers")!.Items[0];
        Require(presentation.Summary(transformer, "transformer").Contains("$cooked", StringComparison.Ordinal) &&
            presentation.Summary(PresetValue.Scalar("$Fresh"), behaviorMode: true) == "$Fresh" &&
            presentation.Summary(PresetValue.Scalar("$$Fresh"), behaviorMode: true, parametersAllowed: true) == "$Fresh" &&
            presentation.Summary(PresetValue.Scalar("Template: $Fresh"), behaviorMode: true, parametersAllowed: true) == "Template: $Fresh",
            "Legacy summaries and literal dollar-prefixed text must retain their meaning.");
        Require(presentation.ReferenceLabel(rule.Value).StartsWith("Template:", StringComparison.Ordinal) &&
            presentation.ReferenceLabel(rule.Value, behaviorMode: true).StartsWith("Behavior:", StringComparison.Ordinal),
            "Technical reference labels must change only in behavior mode.");
    }

    private static void SourceFilters()
    {
        var document = PresetDocument.Parse("""
            templates:
              age:
                owners: Raw
                finalFormEditorID: Result
                stages:
                - no: 0
                  name: Fresh
                  duration: 1
                - no: 1
                  name: Aged
                  duration: 1
                  FormEditorID: $replacement
                - no: 2
                  name: Old
                  duration: 1
                  FormEditorID: Used
            ownerLists:
            - age(Current)
            """, "FOOD", PresetKind.Custom);
        FormRow Form(string id, string type = "Ingestible", ItemCategories category = ItemCategories.FOOD) =>
            new(id, id, type, "Test.esp", "Test.esp", "Test.esp|" + id, category, IsBoundObject: true);
        var source = Form("Raw"); var used = Form("Used"); var current = Form("Current"); var valid = Form("New");
        var wrongType = Form("WrongType", "Ingredient"); var wrongCategory = Form("WrongCategory", category: ItemCategories.POSN);
        var rule = document.Blocks.First(block => block.Kind == BlockKind.Rule);
        var editor = new BehaviorEditorControl(document, [source, used, current, valid, wrongType, wrongCategory], rule.Id);
        Field<TabControl>(editor, "tabs").SelectedIndex = 1; SelectCell(editor, 0, 0);
        var family = BehaviorFamily.Find(editor.Document, rule.Id)!;
        var descriptor = PresetSchema.GetParameterField(family.Definition, "replacement", editor.Document)!;
        var bulk = (FormSelection)typeof(BehaviorEditorControl).GetMethod("BulkFormSelection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editor, [descriptor, "replacement"])!;
        var cell = Field<ValueEditorControl>(editor, "cellEditor");
        var single = (FormSelection)typeof(ValueEditorControl).GetMethod("FormSelection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(cell, null)!;
        foreach (var selection in new[] { bulk, single })
            Require(selection.Matches(valid) && selection.Matches(current) && !selection.Matches(source) &&
                !selection.Matches(wrongType) && !selection.Matches(wrongCategory),
                "Single and bulk stage replacement choices must share source, form type, and category restrictions.");
        Require(!bulk.Matches(used), "Bulk stage replacement must exclude forms already used by other common stages.");
        var sourceField = PresetSchema.GetFields("rule", PresetKind.Custom).Single(field => field.Key == "owners");
        Require(FormSelection.ForField(sourceField, "FOOD", [source]).Matches(source),
            "Source-item fields must not inherit replacement-only source exclusions.");
    }

    private static ValueEditorControl Common(BehaviorEditorControl editor) => Field<ValueEditorControl>(editor, "commonEditor");
    private static ValueEditorControl CollectionEntry(ValueEditorControl editor, string key, int index)
    {
        var collection = Descendants<ValueEditorControl>(Card(editor, key)).First();
        Field<ListBox>(collection, "entryList").SelectedIndex = index;
        return Descendants<ValueEditorControl>(collection).First(control => control.Value.Kind == ValueKind.Mapping);
    }
    private static Border Card(ValueEditorControl editor, string key)
    {
        var view = Field<System.Collections.IDictionary>(editor, "fields")[key]!;
        return (Border)view.GetType().GetProperty("Target")!.GetValue(view)!;
    }
    private static TextBox Input(ValueEditorControl editor, string key) => Descendants<TextBox>(Card(editor, key)).Single();
    private static TextBox CellInput(BehaviorEditorControl editor) => Descendants<TextBox>(Field<ValueEditorControl>(editor, "cellEditor")).Single();
    internal static void MakeVariable(ValueEditorControl editor, string key, string? variableName)
    {
        var choice = VariableChoice(editor, key);
        if (Window.GetWindow(editor) is { } owner) new WindowInteropHelper(owner).EnsureHandle();
        var response = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var dialog = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                .OfType<Window>().Single(window => window.Title == "Name variable");
            if (variableName is not null)
                Descendants<TextBox>(dialog).Single(input => AutomationProperties.GetName(input) == "Variable name").Text = variableName;
            var button = Descendants<Button>(dialog).Single(button => Equals(button.Content, variableName is null ? "Cancel" : "Create"));
            Require(variableName is null || button.IsDefault, "Enter must invoke Create in the variable naming dialog.");
            Click(button);
        }));
        try { choice.IsChecked = true; choice.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); }
        finally { response.Abort(); }
        Require(response.Status == DispatcherOperationStatus.Completed, "The real Name variable dialog must receive the test response.");
    }
    private static CheckBox VariableChoice(ValueEditorControl editor, string key)
    {
        var view = Field<System.Collections.IDictionary>(editor, "fields")[key]!;
        var descriptor = (PresetField)view.GetType().GetProperty("Field")!.GetValue(view)!;
        return Descendants<CheckBox>(Card(editor, key))
            .Single(choice => AutomationProperties.GetName(choice) == "Variable: " + descriptor.Label);
    }
    private static void ClearVariable(ValueEditorControl editor, string key)
    {
        var choice = VariableChoice(editor, key); choice.IsChecked = false;
        choice.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
    }
    private static void SelectCell(BehaviorEditorControl editor, int row, int parameter)
    {
        var table = Field<DataGrid>(editor, "table"); table.SelectedItem = table.Items[row];
        table.CurrentCell = new DataGridCellInfo(table.Items[row], table.Columns[parameter + 1]);
    }
    private static void FocusGridCell(Window host, DataGrid table, int row, int column)
    {
        table.ScrollIntoView(table.Items[row], table.Columns[column]); host.UpdateLayout();
        DependencyObject selected = table.Columns[column].GetCellContent(table.Items[row]);
        while (selected is not DataGridCell) selected = VisualTreeHelper.GetParent(selected);
        FocusManager.SetFocusedElement(host, (DataGridCell)selected);
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
