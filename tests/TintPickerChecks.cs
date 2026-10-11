using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class TintPickerChecks
{
    public static void Run()
    {
        Encoding();
        YamlRoundTrip();
        PickerEditing();
        EditorIntegration();
    }

    private static void Encoding()
    {
        foreach (var (text, expected, clear) in new (string, Color, bool)[]
        {
            ("FF0000", Colors.Red, false), ("0xFF000080", Color.FromArgb(128, 255, 0, 0), false),
            ("00FF0080", Color.FromRgb(255, 0, 128), false), ("0", Colors.Black, true),
            ("#000000", Colors.Black, false), ("#00000000", Color.FromArgb(0, 0, 0, 0), false),
            ("#00FF0080", Color.FromArgb(128, 0, 255, 0), false), ("#01020304", Color.FromArgb(4, 1, 2, 3), false)
        })
        {
            Require(TintPickerWindow.TryRead(text, out var actual, out var noTint) && actual == expected && noTint == clear,
                "Tint decoding changed the meaning of " + text + ".");
        }
        foreach (var text in new[] { "", "#fff", "#1234567", "#123456789", "#0x123456", "#GG0000", "100000000", "$tint" })
            Require(!TintPickerWindow.TryRead(text, out _, out _), "Invalid tint was accepted: " + text);
        foreach (var color in new[] { Colors.Black, Colors.Red, Color.FromArgb(128, 0, 255, 0), Color.FromArgb(0, 0, 0, 0) })
            Require(TintPickerWindow.TryRead(TintPickerWindow.Encode(color), out var decoded, out var clear) && !clear && decoded == color,
                "The picker must encode black and opacity without changing channels or removing tint.");
    }

    private static void YamlRoundTrip()
    {
        var document = PresetDocument.Parse("""
            ownerLists:
            - owners: FoodBeef
              stages:
              - no: 0
                name: ""
                duration: 1
              finalFormEditorID: FoodCooked
            """, "FOOD", PresetKind.Custom);
        var stage = document.Blocks.Single().Value.Get("stages")!.Items.Single();
        var field = PresetSchema.GetFields("stage", PresetKind.Custom).Single(candidate => candidate.Key == "color");
        foreach (var text in new[] { "#000000", "#00000000", "#00FF0080", "#123456", "FF000080", "0" })
        {
            stage.Set("color", PresetValue.Scalar(text));
            Require(PresetSchema.ValidateField(stage.Get("color")!, field, "stage", "FOOD", PresetKind.Custom).Count == 0,
                "The schema rejected a supported tint: " + text);
            var yaml = document.ToYaml();
            var reopened = PresetDocument.Parse(yaml, "FOOD", PresetKind.Custom);
            Require(reopened.Blocks.Single().Value.Get("stages")!.Items.Single().Get("color")!.Text == text,
                "A # tint became a YAML comment or changed during save/reopen.");
            var expanded = PresetYaml.Parse(PresetCatalog.Expand(yaml, CancellationToken.None).GetAwaiter().GetResult());
            Require(expanded.Get("ownerLists")!.Items.Single().Get("stages")!.Items.Single().Get("color")!.Text == text,
                "Native YAML expansion changed tint " + text + ".");
        }
        foreach (var text in new[] { "#fff", "#0x123456", "#1234567", "#GG0000" })
            Require(PresetSchema.ValidateField(PresetValue.Scalar(text), field, "stage", "FOOD", PresetKind.Custom).Count == 1,
                "The schema accepted invalid explicit color " + text + ".");
    }

    private static void PickerEditing()
    {
        var entry = new TextBox { Text = "FF000080" };
        var choose = TintPickerWindow.CreateButton(entry);
        Require(AutomationProperties.GetName(choose) == "Choose tint", "The swatch needs an accessible action name.");
        OpenPicker(choose, dialog =>
        {
            Require(Channel(dialog, "Opacity").Value == 128, "Opening legacy RGBA must preserve its opacity.");
            Click(dialog, "Green");
            Require(Channel(dialog, "Opacity").Value == 128, "Choosing RGB must preserve the existing opacity.");
            Require(entry.Text == "FF000080", "Color preview must not change the preset before Apply.");
            Click(dialog, "Cancel");
        });
        Require(entry.Text == "FF000080", "Cancel must preserve the original field exactly.");
        OpenPicker(choose, dialog => { Click(dialog, "Green"); Click(dialog, "Apply"); });
        Require(entry.Text == "#00FF0080", "Apply must commit the selected RGB and preserved opacity.");
        OpenPicker(choose, dialog =>
        {
            Channel(dialog, "Red").Value = 0; Channel(dialog, "Green").Value = 0; Channel(dialog, "Blue").Value = 0;
            Channel(dialog, "Opacity").Value = byte.MaxValue; Click(dialog, "Apply");
        });
        Require(entry.Text == "#000000", "Opaque black must stay distinct from No tint.");
        OpenPicker(choose, dialog =>
        {
            Descendants<CheckBox>(dialog).Single().IsChecked = true; Click(dialog, "Apply");
        });
        Require(entry.Text == "0" && choose.ToolTip.ToString()!.StartsWith("No tint"), "No tint must write explicit zero and identify it in the swatch.");
        entry.Text = "";
        Require(choose.ToolTip.ToString()!.StartsWith("Tint not set"), "An unset tint must remain distinct from explicit No tint.");
        entry.Text = "not a color";
        Require(choose.ToolTip.ToString()!.StartsWith("Invalid tint"), "Invalid text must not appear to be a valid color.");
    }

    private static void EditorIntegration()
    {
        foreach (var context in new[] { "stage", "modulator", "transformer" })
        {
            var field = PresetSchema.GetFields(context, PresetKind.Custom).Single(candidate => candidate.Key == "color");
            var editor = new ValueEditorControl(PresetDocument.Create("FOOD", PresetKind.Custom), [], PresetValue.Scalar("FF000080"),
                context, "Tint", field: field);
            var choose = Descendants<Button>(editor).Single(button => AutomationProperties.GetName(button) == "Choose tint");
            OpenPicker(choose, dialog => { Click(dialog, "Blue"); Click(dialog, "Apply"); });
            editor.Capture();
            Require(editor.Value.Text == "#0000FF80", "The " + context + " tint picker did not commit through its real field editor.");
        }
        var document = PresetDocument.Parse("""
            templates:
              colored:
                owners: FoodBeef
                stages:
                - {no: 0, name: '', duration: 1, color: $tint}
                finalFormEditorID: FoodCooked
            ownerLists:
            - colored(FF000080)
            - colored(00FF0080)
            """, "FOOD", PresetKind.Custom);
        var rule = document.Blocks.First(block => block.Kind == BlockKind.Rule);
        var behavior = new BehaviorEditorControl(document, [], rule.Id);
        Descendants<TabControl>(behavior).Single().SelectedIndex = 1;
        var table = Descendants<DataGrid>(behavior).Single();
        table.SelectedItem = table.Items[1];
        table.CurrentCell = new DataGridCellInfo(table.Items[1], table.Columns.Single(column => Equals(column.Header, "tint")));
        var swatch = Descendants<Button>(behavior).Single(button => AutomationProperties.GetName(button) == "Choose tint");
        OpenPicker(swatch, dialog => { Click(dialog, "Black"); Click(dialog, "Apply"); });
        behavior.Capture();
        var expandedRows = PresetYaml.Parse(PresetCatalog.Expand(behavior.Document.ToYaml(), CancellationToken.None).GetAwaiter().GetResult())
            .Get("ownerLists")!.Items;
        Require(expandedRows[0].Get("stages")!.Items.Single().Get("color")!.Text == "FF000080" &&
                expandedRows[1].Get("stages")!.Items.Single().Get("color")!.Text == "#000000",
            "The Variables tint picker must change only the selected row and preserve # through template expansion.");
    }

    private static Slider Channel(DependencyObject dialog, string name) =>
        Descendants<Slider>(dialog).Single(slider => AutomationProperties.GetName(slider) == name);

    private static void Click(DependencyObject dialog, string name) => Descendants<Button>(dialog)
        .Single(button => Equals(button.Content, name) || AutomationProperties.GetName(button) == name)
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void OpenPicker(Button choose, Action<TintPickerWindow> edit)
    {
        Exception? failure = null;
        var response = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var dialog = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                .OfType<TintPickerWindow>().Single();
            try { dialog.Opacity = 0; dialog.ShowInTaskbar = false; edit(dialog); }
            catch (Exception error) { failure = error; if (dialog.IsVisible) dialog.DialogResult = false; }
        }));
        try { choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        finally { response.Abort(); }
        if (failure is not null) throw failure;
        Require(response.Status == DispatcherOperationStatus.Completed, "The tint swatch did not open its picker.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
