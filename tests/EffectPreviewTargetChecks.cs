using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class EffectPreviewTargetChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AoTEffectTargetChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var assets = GameAssets.FromLoadOrder(folder, [], []);
            FormRow Item(string name, ItemCategories category = ItemCategories.FOOD) =>
                new(name, name, "Ingestible", "Targets.esm", "Targets.esm", name, category, IsBoundObject: true)
                { Assets = assets, Models = [new("Model", [new("Meshes/item.nif", [])])] };
            var raw = Item("Raw"); var fish = Item("Fish"); var cooked = Item("Cooked"); var other = Item("Other", ItemCategories.POSN);
            var noModel = Item("NoModel") with { Models = [] };
            var art = new FormRow("Art", "Art", "ArtObject", "Targets.esm", "Targets.esm", "Art")
                { Assets = assets, Models = [new("Art", [new("Meshes/art.nif", [])])] };
            FormRow[] forms = [raw, fish, cooked, other, noModel, art];
            StageAndSourceControls(forms, raw, cooked, noModel);
            CurrentItemTargets(forms, raw, fish, cooked);
            VariableRowControls(forms, raw, fish, cooked);
            VariableCollectionControls(forms, raw, fish);
            MultipleTargetPicker(forms, raw, fish);
            DefaultPreviewChoice(forms, raw);
            DefaultVariableStageItem(forms, raw);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void StageAndSourceControls(FormRow[] forms, FormRow raw, FormRow cooked, FormRow noModel)
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var stage = PresetYaml.Parse("{no: 1, name: '', duration: 1, art_object: Art}");
        var editor = new ValueEditorControl(document, forms, stage, "stage", "Stage");
        var effect = Child(editor, "art_object"); var preview = Preview(effect);
        Require(preview.Visibility == Visibility.Visible && !preview.IsEnabled &&
            Equals(preview.ToolTip, "Select an item to preview this effect."),
            "An effect with no selected item must retain a disabled Preview on item button with a reason.");
        editor.UpdateSourceForms([raw]);
        Require(preview.IsEnabled && Targets(effect).SequenceEqual([raw]), "Selecting source items must immediately enable their effect preview.");
        editor.UpdateSourceForms([noModel]);
        Require(!preview.IsEnabled && Equals(preview.ToolTip, "The selected item has no model to preview."),
            "A selected item without a model must give a different reason from no selection.");
        editor.UpdateSourceForms([raw]);
        var property = Card(editor, "FormEditorID");
        Click(Descendants<Button>(property).Single(button => Equals(button.Content, "Set")));
        var input = Descendants<TextBox>(Child(editor, "FormEditorID")).Single();
        input.Text = cooked.Identifier;
        Require(Targets(effect).SequenceEqual([cooked]) && preview.IsEnabled,
            "An explicit Stage item must replace the source model as the effect target immediately.");
        input.Text = "MissingItem";
        Require(Targets(effect).Count == 0 && !preview.IsEnabled,
            "An unresolved explicit Stage item must not silently fall back to the original source model.");
        input.Text = "";
        Require(Targets(effect).SequenceEqual([raw]), "Clearing the Stage item must restore the source model.");
        var effectInput = Descendants<TextBox>(effect).Single(); effectInput.Text = "";
        Require(!preview.IsEnabled && Equals(preview.ToolTip, "Select an effect to preview."), "Clearing the effect must disable its preview.");
        effectInput.Text = "Art";
        Require(preview.IsEnabled, "Selecting the effect again must restore its preview without rebuilding the field.");
        input.Text = cooked.Identifier;
        Descendants<TextBox>(Child(editor, "no")).Single().Text = "0";
        Require(Targets(effect).SequenceEqual([raw]), "An imported Stage 0 replacement is unused by AoT and must not change its preview target model.");
    }

    private static void CurrentItemTargets(FormRow[] forms, FormRow raw, FormRow fish, FormRow cooked)
    {
        var document = PresetDocument.Parse("""
            ownerLists:
            - owners: [Raw, Fish]
              stages:
              - {no: 0, name: '', duration: 1}
              - {no: 1, name: '', duration: 1, FormEditorID: Cooked}
              finalFormEditorID: Other
              transformers:
              - {FormEditorID: Art, finalFormEditorID: Other, duration: 1, art_object: Art}
              timeModulators:
              - {FormEditorID: Other, magnitude: 0, allowed_stages: 1, effect_shader: Shader}
            """, "FOOD", PresetKind.Custom);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        Require(PathTargets(document, forms, rule.Value, ["transformers", "0", "art_object"]).ToHashSet().SetEquals([raw, fish, cooked]),
            "Transformation effects must use current source/stage models, never their trigger or result model.");
        Require(PathTargets(document, forms, rule.Value, ["timeModulators", "0", "effect_shader"]).SequenceEqual([cooked]),
            "A time modifier restricted to one stage must preview that stage's explicit item model.");
        var group = new FormGroupDefinition("Meat", [raw.Identifier, fish.Identifier]);
        rule.Value.Set("owners", PresetValue.Scalar("Meat"));
        var presentation = new PresetPresentation(document, forms, [group]);
        Require(PathTargets(document, forms, rule.Value, ["stages", "0", "art_object"], presentation).ToHashSet().SetEquals([raw, fish]),
            "An affected form group must offer its actual source members.");
        rule.Value.Set("owners", PresetValue.Scalar("Raw"));
        Require(PathTargets(document, forms, rule.Value, ["stages", "0", "art_object"]).SequenceEqual([raw]),
            "Changing Applies to must change the target candidates without cached selections.");
    }

    private static void VariableRowControls(FormRow[] forms, FormRow raw, FormRow fish, FormRow cooked)
    {
        var document = PresetDocument.Parse("""
            templates:
              stage:
                no: 1
                name: ''
                duration: 1
                FormEditorID: $replacement
                art_object: $visual
              rules:
                owners: $source
                stages:
                - {no: 0, name: '', duration: 1}
                - stage($replacement, $visual)
                finalFormEditorID: Other
            ownerLists:
            - rules(Raw, Cooked, Art)
            - rules(Fish, '', Art)
            """, "FOOD", PresetKind.Custom);
        var rows = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        var editor = new BehaviorEditorControl(document, forms, rows[0].Id);
        var tabs = Private<TabControl>(editor, "tabs"); tabs.SelectedIndex = 1;
        var parameter = BehaviorFamily.Find(editor.Document, rows[0].Id)!.Parameters.ToList().IndexOf("visual");
        SelectCell(editor, 0, parameter);
        var first = Private<ValueEditorControl>(editor, "cellEditor");
        Require(Targets(first).SequenceEqual([cooked]) && Preview(first).IsEnabled,
            "A scalar effect variable must use its own row's resolved Stage item through nested template calls.");
        SelectCell(editor, 1, parameter);
        var second = Private<ValueEditorControl>(editor, "cellEditor");
        Require(Targets(second).SequenceEqual([fish]) && Preview(second).IsEnabled,
            "Another variable row must use its own Applies to when its Stage item is unset.");
        var replacement = BehaviorFamily.Find(editor.Document, rows[0].Id)!.Parameters.ToList().IndexOf("replacement");
        SelectCell(editor, 1, replacement);
        Descendants<TextBox>(Private<ValueEditorControl>(editor, "cellEditor")).Single().Text = raw.Identifier;
        SelectCell(editor, 1, parameter);
        Require(Targets(Private<ValueEditorControl>(editor, "cellEditor")).SequenceEqual([raw]),
            "Editing a row's replacement variable must update its effect target rather than retaining the former row context.");
    }

    private static void VariableCollectionControls(FormRow[] forms, FormRow raw, FormRow fish)
    {
        var document = PresetDocument.Parse("""
            templates:
              effect:
                FormEditorID: Other
                finalFormEditorID: Cooked
                duration: 1
                art_object: $visual
              rules:
                forms: $source
                transformers: $effects
            formsLists:
            - rules(Raw, [effect(Art)])
            - rules(Fish, [effect(Art)])
            """, "FOOD", PresetKind.Addon);
        var rows = document.Blocks.Where(block => block.Kind == BlockKind.Rule).ToArray();
        var editor = new BehaviorEditorControl(document, forms, rows[0].Id);
        Private<TabControl>(editor, "tabs").SelectedIndex = 1;
        var parameter = BehaviorFamily.Find(editor.Document, rows[0].Id)!.Parameters.ToList().IndexOf("effects");
        SelectCell(editor, 0, parameter);
        var first = Private<ValueEditorControl>(editor, "cellEditor");
        var effect = Descendants<ValueEditorControl>(first).Single(child => Private<PresetField?>(child, "field")?.FormTypes?.SequenceEqual(["ArtObject"]) == true);
        Require(Targets(effect).SequenceEqual([raw]),
            "An effect nested in a variable transformation list and template call must retain the row's original target item.");
        SelectCell(editor, 1, parameter);
        var second = Private<ValueEditorControl>(editor, "cellEditor");
        effect = Descendants<ValueEditorControl>(second).Single(child => Private<PresetField?>(child, "field")?.FormTypes?.SequenceEqual(["ArtObject"]) == true);
        Require(Targets(effect).SequenceEqual([fish]), "Changing variable list rows must update nested effect target context.");
    }

    private static void MultipleTargetPicker(FormRow[] forms, FormRow raw, FormRow fish)
    {
        var stage = PresetYaml.Parse("{no: 1, name: '', duration: 1, art_object: Art}");
        var editor = new ValueEditorControl(PresetDocument.Create("FOOD", PresetKind.Custom), forms, stage,
            "stage", "Stage", sourceForms: [raw, fish]);
        var host = Host(editor);
        var preview = Preview(Child(editor, "art_object"));
        Exception? failure = null;
        var answered = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var picker = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                .OfType<FormPickerWindow>().Single(window => window.Title == "Choose an item to preview this effect on");
            try
            {
                picker.Opacity = 0; picker.ShowInTaskbar = false;
                var table = Descendants<DataGrid>(picker).Single();
                Require(table.Items.Cast<FormRow>().ToHashSet().SetEquals([raw, fish]),
                    "Multiple affected items must open a picker limited to those target models, rather than choose the first automatically.");
            }
            catch (Exception error) { failure = error; }
            finally { picker.DialogResult = false; }
        }));
        try { Click(preview); }
        finally { answered.Abort(); host.Close(); }
        if (failure is not null) throw failure;
        Require(answered.Status == DispatcherOperationStatus.Completed, "The actual target picker must be reachable from Preview on item.");
    }

    private static void DefaultPreviewChoice(FormRow[] forms, FormRow raw)
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Default);
        var editor = new ValueEditorControl(document, forms, PresetYaml.Parse("{no: 1, name: '', duration: 1, art_object: Art}"), "stage", "Stage");
        var host = Host(editor);
        var effect = Child(editor, "art_object");
        var preview = Preview(effect);
        var choose = Descendants<Button>(effect).Single(button => Equals(button.Content, "Choose preview item..."));
        var before = PresetYaml.Write(editor.Value);
        Require(!preview.IsEnabled && choose.Visibility == Visibility.Visible, "All-item scope must require choosing a concrete representative model before preview.");
        Exception? failure = null;
        var answered = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var picker = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual).OfType<FormPickerWindow>().Single();
            try
            {
                picker.Opacity = 0; picker.ShowInTaskbar = false;
                var table = Descendants<DataGrid>(picker).Single();
                Require(table.Items.Cast<FormRow>().All(form => form.Categories == ItemCategories.FOOD && ModelPreviewWindow.CanPreview(form)),
                    "A representative preview item must be an actual model in the preset's category.");
                table.SelectedItem = raw;
                Click(Descendants<Button>(picker).Single(button => Equals(button.Content, "Choose")));
            }
            catch (Exception error) { failure = error; picker.DialogResult = false; }
        }));
        try { Click(choose); }
        finally { answered.Abort(); host.Close(); }
        if (failure is not null) throw failure;
        Require(answered.Status == DispatcherOperationStatus.Completed && preview.IsEnabled && PresetYaml.Write(editor.Value) == before,
            "Choosing a representative model must enable preview without editing any preset settings.");
        editor.Document.Category = "POSN";
        editor.UpdatePresentation(new PresetPresentation(editor.Document, forms));
        Require(!preview.IsEnabled, "Changing the preset category must invalidate a previous representative preview item.");
    }

    private static void DefaultVariableStageItem(FormRow[] forms, FormRow raw)
    {
        var document = PresetDocument.Parse("""
            stages:
            - {no: 0, name: '', duration: 1}
            - {no: 1, name: '', duration: 1, FormEditorID: MissingItem, art_object: Art}
            finalFormEditorID: Cooked
            """, "FOOD", PresetKind.Default);
        var rule = document.Blocks.Single(block => block.Kind == BlockKind.Rule);
        var parameter = BehaviorFamily.MakeVariable(document, rule.Id, ["stages", "1", "art_object"], "visual");
        var editor = new BehaviorEditorControl(document, forms, rule.Id);
        Private<TabControl>(editor, "tabs").SelectedIndex = 1; SelectCell(editor, 0, 0);
        var cell = Private<ValueEditorControl>(editor, "cellEditor");
        var choose = Descendants<Button>(cell).Single(button => Equals(button.Content, "Choose preview item..."));
        typeof(ValueEditorControl).GetField("previewItem", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cell, raw);
        cell.UpdatePresentation(new PresetPresentation(editor.Document, forms));
        Require(!Preview(cell).IsEnabled && Targets(cell).Count == 0 && choose.Visibility == Visibility.Collapsed,
            "A scalar default effect variable with an invalid explicit Stage item must not fall back to an unrelated representative model.");
        var family = BehaviorFamily.Find(document, rule.Id)!;
        BehaviorFamily.GetValue(family.Definition.Value, ["stages", "1"])!.Remove("FormEditorID");
        editor = new BehaviorEditorControl(document, forms, rule.Id);
        Private<TabControl>(editor, "tabs").SelectedIndex = 1; SelectCell(editor, 0, family.Parameters.ToList().IndexOf(parameter));
        cell = Private<ValueEditorControl>(editor, "cellEditor");
        Require(!Preview(cell).IsEnabled && Descendants<Button>(cell).Single(button => Equals(button.Content, "Choose preview item...")).Visibility == Visibility.Visible,
            "A scalar default effect variable on a generated stage must still offer an explicit representative model choice.");
    }

    private static IReadOnlyList<FormRow> PathTargets(PresetDocument document, FormRow[] forms, PresetValue rule,
        string[] path, PresetPresentation? presentation = null) => (IReadOnlyList<FormRow>)typeof(ValueEditorControl).Assembly
        .GetType("AoTPresetEditor.EffectPreviewTargets")!.GetMethod("ForPath", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [document, presentation ?? new PresetPresentation(document, forms), rule, path, forms])!;
    private static IReadOnlyList<FormRow> Targets(ValueEditorControl editor) => (IReadOnlyList<FormRow>)typeof(ValueEditorControl)
        .GetMethod("PreviewTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null)!;
    private static ValueEditorControl Child(ValueEditorControl editor, string key) => Descendants<ValueEditorControl>(editor)
        .Single(child => Private<PresetField?>(child, "field")?.Key == key);
    private static Border Card(ValueEditorControl editor, string key)
    {
        var view = Private<System.Collections.IDictionary>(editor, "fields")[key]!;
        return (Border)view.GetType().GetProperty("Target")!.GetValue(view)!;
    }
    private static Button Preview(DependencyObject editor) => Descendants<Button>(editor).Single(button => Equals(button.Content, "Preview on item..."));
    private static Window Host(FrameworkElement editor)
    {
        var window = new Window { Content = editor, Width = 1000, Height = 700, Left = -12000, Top = -12000,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); return window;
    }
    private static void SelectCell(BehaviorEditorControl editor, int row, int parameter)
    {
        var table = Private<DataGrid>(editor, "table"); table.SelectedItem = table.Items[row];
        table.CurrentCell = new DataGridCellInfo(table.Items[row], table.Columns[parameter + 1]);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Private<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
