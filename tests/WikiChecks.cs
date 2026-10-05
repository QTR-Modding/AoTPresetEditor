using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class WikiChecks
{
    public static void Run()
    {
        var placementPath = Path.Combine(Path.GetTempPath(), "AoT-wiki-" + Guid.NewGuid().ToString("N") + ".json");
        var main = new MainWindow(null, placementPath);
        try
        {
            var tabs = Field<TabControl>(main, "MainTabs");
            var help = Field<TabItem>(main, "HelpTab");
            var workspace = Field<PresetWorkspace>(main, "Workspace");
            var presets = tabs.Items[0];
            var document = PresetDocument.Create("FOOD", PresetKind.Custom);
            var project = new PresetProject { Name = "Unfinished preset" };
            var entry = project.Add(document, project.Name);
            SetField(workspace, "project", project);
            SetField(workspace, "activeEntry", entry);
            SetField(workspace, "document", document);
            var snapshot = project.ToDraft();
            Require(tabs.Items.Cast<TabItem>().Select(tab => tab.Header).SequenceEqual(new[] { "Presets", "Game data", "Help" }),
                "Help must be beside Presets and Game data.");
            Require(help.Content is null, "Opening the editor must not initialize its wiki browser.");
            tabs.SelectedItem = help;
            Require(help.Content is WikiView, "Opening Help must create the embedded wiki view.");
            var wiki = (WikiView)help.Content!;
            Require(Field<object?>(wiki, "browser") is null,
                "Creating the Help view must not launch a browser before it is displayed.");
            tabs.SelectedItem = presets;
            Require(ReferenceEquals(workspace, Field<PresetWorkspace>(main, "Workspace")) &&
                ReferenceEquals(document, Field<PresetDocument>(workspace, "document")) && project.ToDraft() == snapshot,
                "Returning from Help must preserve the current preset and editor state.");
            tabs.SelectedItem = help;
            Require(ReferenceEquals(wiki, help.Content), "Returning to Help must retain the wiki page rather than create another browser.");
        }
        finally { main.Close(); if (File.Exists(placementPath)) File.Delete(placementPath); }
        PendingValidationSurvivesNavigation();
    }

    private static void PendingValidationSurvivesNavigation()
    {
        var placementPath = Path.Combine(Path.GetTempPath(), "AoT-wiki-navigation-" + Guid.NewGuid().ToString("N") + ".json");
        var main = new MainWindow(null, placementPath)
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false
        };
        var workspace = Field<PresetWorkspace>(main, "Workspace");
        try
        {
            var document = PresetDocument.Parse("""
                ownerLists:
                - owners: FoodBeef
                  stages: [{no: 0, duration: 1}]
                  finalFormEditorID: FoodBeefCooked
                """, "FOOD", PresetKind.Custom);
            var project = new PresetProject { Name = "Navigation" };
            var entry = project.Add(document, project.Name);
            SetField(workspace, "project", project); SetField(workspace, "activeEntry", entry);
            SetField(workspace, "document", document);
            Invoke(workspace, "RenderBlocks", document.Blocks.Single().Id); Invoke(workspace, "SelectBlock");
            Invoke(main, "ShowEditor");
            var help = Field<TabItem>(main, "HelpTab");
            // Exercise the real tab lifecycle without starting a network browser in this check.
            help.Content = new TextBlock { Text = "Wiki page" };
            main.Show(); main.UpdateLayout(); Drain();
            var editor = Field<BehaviorEditorControl>(workspace, "editor");
            var tabs = Field<TabControl>(main, "MainTabs");
            var timer = Field<DispatcherTimer>(workspace, "editTimer");
            Field<TextBox>(editor, "name").Text = "Edited before reading Help";
            Require(timer.IsEnabled && Field<bool>(workspace, "editorDirty"),
                "The navigation fixture must have an edit waiting for validation.");
            tabs.SelectedItem = help; Drain();
            Require(!workspace.IsLoaded && !timer.IsEnabled,
                "Switching to Help must exercise the workspace unload that cancels its queued validation.");
            tabs.SelectedIndex = 0; Drain();
            Require(workspace.IsLoaded && timer.IsEnabled && ReferenceEquals(editor, Field<BehaviorEditorControl>(workspace, "editor")),
                "Returning from Help must resume pending validation without replacing the editor.");

            Invoke(workspace, "CommitEditor");
            tabs.SelectedIndex = 1; Drain(); tabs.SelectedIndex = 0; Drain();
            Require(!Field<bool>(workspace, "editorDirty") && timer.IsEnabled,
                "Returning to Presets must also resume a captured document whose validation was canceled.");
        }
        finally
        {
            Invoke(workspace, "ResetEditor"); SetField(workspace, "dirty", false);
            main.Close(); if (File.Exists(placementPath)) File.Delete(placementPath);
        }
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Invoke(object value, string name, params object?[] args) => value.GetType().GetMethod(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(value)!;
    private static void SetField(object value, string name, object fieldValue) => value.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, fieldValue);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
