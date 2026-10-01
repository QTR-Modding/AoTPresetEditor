using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AoTPresetEditor;

internal static class EditorInteractionChecks
{
    private static readonly FormRow[] Forms =
    [
        new("Raw Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Gourmet.esp", "0x65C99~Skyrim.esm"),
        new("Venison", "FoodVenison", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x669A2~Skyrim.esm"),
        new("Leather Armor", "ArmorLeather", "Armor", "Skyrim.esm", "Skyrim.esm", "0x3619E~Skyrim.esm")
    ];

    public static void Run()
    {
        PickerFiltering();
        ParametersAndArguments();
        ListEditing();
        DraftIsolation();
        OverviewActions();
    }

    private static void PickerFiltering()
    {
        var multi = new FormPickerWindow(Forms, ["Ingestible"], true);
        var grid = Field<DataGrid>(multi, "results");
        Require(grid.Items.Count == 2, "Picker must respect the field's allowed types.");
        grid.SelectedItems.Add(Forms[0]);
        Search(multi, "Venison");
        Require(grid.Items.Count == 1 && grid.SelectedItems.Count == 0, "Search must hide nonmatching rows.");
        grid.SelectedItems.Add(Forms[1]);
        Search(multi, "");
        Require(grid.SelectedItems.Count == 2, "Selections must survive changes to the search filter.");
        Field<ComboBox>(multi, "plugins").SelectedItem = "Gourmet.esp";
        Invoke(multi, "FilterForms");
        Require(grid.Items.Count == 1, "Plugin filtering must include winning overrides.");
        multi.Close();

        var single = new FormPickerWindow(Forms);
        grid = Field<DataGrid>(single, "results");
        grid.SelectedItem = Forms[0];
        Search(single, "Beef");
        Require(Equals(grid.SelectedItem, Forms[0]), "A matching single selection must survive filtering.");
        Search(single, "Venison");
        Require(grid.SelectedItem is null && !Field<Button>(single, "choose").IsEnabled, "A hidden single selection must be cleared.");
        single.Close();

        const int matchCount = 1100;
        var many = Enumerable.Range(0, matchCount).Select(index => Forms[0] with { EditorId = $"Beef{index}" }).ToArray();
        var limited = new FormPickerWindow(many);
        Require(Field<DataGrid>(limited, "results").Items.Count == 1000, "Large searches must limit rendered rows.");
        Require(Field<TextBlock>(limited, "count").Text.Contains(matchCount.ToString("N0")), "Limited results must still report every match.");
        limited.Close();
    }

    private static void ParametersAndArguments()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var boolean = new PresetField("crafting_allowed", "Allow crafting", FieldKind.Boolean, "", DefaultText: "false");
        var parameter = new ValueEditorWindow(document, Forms, PresetValue.Scalar("$enabled"), "Value", "Flag", true, boolean);
        Field<Action>(parameter, "capture")();
        Require(parameter.Value.Text == "$enabled", "A Boolean parameter must not become false when its editor is applied.");
        Require(!Field<StackPanel>(parameter, "content").Children.OfType<CheckBox>().Any(), "Parameters need parameter controls, not Boolean controls.");
        parameter.Close();

        var body = PresetValue.Map();
        body.Set("duration", PresetValue.Scalar("$duration"));
        body.Set("sound", PresetValue.Scalar("$sound"));
        document.Blocks.Add(new PresetBlock { Name = "Sound setup", Key = "sound", Kind = BlockKind.Template, Context = "Value", Value = body });
        var call = new ValueEditorWindow(document, Forms, PresetValue.Scalar("sound(2)"), "Value", "Call");
        Require(Field<StackPanel>(call, "content").Children.OfType<TextBlock>().Any(text => text.Text.Contains("1 arguments")),
            "Opening a call with missing arguments must preserve its warning.");
        Require(call.Value.Text == "sound(2)", "Opening a call must not silently add arguments.");
        call.Close();
    }

    private static void ListEditing()
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var values = PresetValue.List();
        values.Items.AddRange([PresetValue.Scalar("a"), PresetValue.Scalar("b"), PresetValue.Scalar("c")]);
        var order = new ValueEditorWindow(document, Forms, values, "Values", "Values");
        var content = Field<StackPanel>(order, "content");
        var list = content.Children.OfType<ListBox>().Single();
        list.SelectedIndex = 0;
        var down = content.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(button => Equals(button.Content, "Move down"));
        Click(down); Click(down);
        Require(order.Value.Items[2].Text == "a" && list.SelectedIndex == 2, "Repeated moves must continue moving the selected entry.");
        order.Close();

        var scalar = PresetValue.Scalar("1");
        scalar.Anchor = "sharedNumber";
        scalar.Comments.Add("A shared value.");
        scalar.TrailingComments.Add("End of shared value.");
        var conversion = new ValueEditorWindow(document, Forms, scalar, "Value", "Shared number");
        Click(Field<StackPanel>(conversion, "content").Children.OfType<Button>().Single(button => Equals(button.Content, "Use a list of values...")));
        Require(conversion.Value.Kind == ValueKind.Sequence && conversion.Value.Anchor == "sharedNumber", "Converting to a list must retain the shared definition.");
        Require(conversion.Value.Items.Single().Anchor.Length == 0, "The wrapped scalar must not duplicate the shared anchor.");
        Require(conversion.Value.Comments.Count == 1 && conversion.Value.Items.Single().Comments.Count == 0 && conversion.Value.TrailingComments.Count == 1,
            "Converting to a list must keep comments once, on the shared definition.");
        var block = document.Add(BlockKind.Shared, "Shared number", "Values");
        block.Value = conversion.Value;
        Require(document.ToYaml().Contains("&sharedNumber"), "The converted shared list must serialize successfully.");
        conversion.Close();

        foreach (var context in new[] { "Rules", "DefaultRules", "CustomRules", "AddonRules" })
        {
            var initial = ValueEditorWindow.NewValue(context);
            Require(initial.Kind == ValueKind.Sequence, $"{context} must start as a list.");
            var editor = new ValueEditorWindow(document, Forms, initial, context, "Rules");
            Require(((PresetValue)Invoke(editor, "NewListItem")!).Kind == ValueKind.Mapping, $"{context} must add a settings block.");
            editor.Close();
        }
    }

    private static void DraftIsolation()
    {
        var original = PresetDocument.Create("FOOD", PresetKind.Custom);
        var shared = original.Add(BlockKind.Shared, "Shared", "Value");
        shared.Value.Text = "1"; shared.Value.Kind = ValueKind.Scalar;
        var outer = new ValueEditorWindow(original, Forms, PresetValue.Alias(shared.Value.Anchor), "Value", "Use shared");
        var child = new ValueEditorWindow(outer.Document, Forms, shared.Value, "Value", "Nested editor");
        child.Document.Blocks[0].Value.Text = "2";
        child.Value.Text = "3";
        child.Close();
        Require(outer.Document.Blocks[0].Value.Text == "1", "Canceling a child editor must leave the parent's definitions unchanged.");
        outer.Document.Blocks[0].Value.Text = "4";
        outer.Close();
        Require(original.Blocks[0].Value.Text == "1", "Canceling a block editor must leave the preset unchanged.");
    }

    private static void OverviewActions()
    {
        var workspace = new PresetWorkspace();
        var actions = Field<StackPanel>(workspace, "addActions");
        Require(!actions.IsEnabled, "An empty workspace must not offer block editing.");
        var document = PresetDocument.Create("FOOD", PresetKind.Default);
        SetField(workspace, "document", document);
        Invoke(workspace, "UpdateActions");
        Require(((Button)actions.Children[0]).IsEnabled, "An empty default preset must allow its first rule.");
        document.Add(BlockKind.Rule, "Default", "DefaultRule");
        Invoke(workspace, "UpdateActions");
        Require(!((Button)actions.Children[0]).IsEnabled, "A default preset must not offer a second rule.");
        document = PresetDocument.Create("FOOD", PresetKind.Addon);
        document.Blocks.Add(new PresetBlock { Kind = BlockKind.Rule, Key = "formsLists", Value = PresetValue.Alias("sharedRules") });
        SetField(workspace, "document", document);
        Invoke(workspace, "UpdateActions");
        Require(!((Button)actions.Children[0]).IsEnabled, "A preset with a shared rule list must not offer a crashing Add rule action.");
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);
    private static object? Invoke(object owner, string name) => owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null);
    private static void Search(object window, string text) { Field<TextBox>(window, "search").Text = text; Invoke(window, "FilterForms"); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
