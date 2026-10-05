using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class SaveShortcutChecks
{
    public static void Run()
    {
        var placementPath = Path.Combine(Path.GetTempPath(), "AoT-save-shortcut-" + Guid.NewGuid().ToString("N") + ".json");
        var main = new MainWindow(null, placementPath)
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false
        };
        var workspace = Field<PresetWorkspace>(main, "Workspace");
        try
        {
            var binding = main.InputBindings.OfType<KeyBinding>().Single(item => item.Key == Key.S && item.Modifiers == ModifierKeys.Control);
            var command = ApplicationCommands.Save;
            var save = Field<Button>(workspace, "save");
            Require(binding.Command == command && ReferenceEquals(binding.CommandTarget, workspace) &&
                save.Command == command && ReferenceEquals(save.CommandTarget, workspace),
                "Ctrl+S and Save to mod must use the same routed command and workspace target, even when focus is outside the editor.");
            Require(!command.CanExecute(null, workspace), "An empty or hidden workspace must not offer Save to mod.");

            var document = PresetDocument.Parse("""
                formsLists:
                - forms: FoodBeef
                  transformers:
                  - FormEditorID: FrostSalts
                    duration: 0.1
                    finalFormEditorID: FoodBeefCooked
                """, "FOOD", PresetKind.Addon);
            var project = new PresetProject { Name = "Save shortcut" };
            var entry = project.Add(document, project.Name);
            SetField(workspace, "project", project); SetField(workspace, "activeEntry", entry);
            SetField(workspace, "document", document);
            Validate(workspace, entry);
            Invoke(workspace, "RenderBlocks", document.Blocks.Single().Id); Invoke(workspace, "SelectBlock");
            Invoke(main, "ShowEditor");
            main.Show(); main.UpdateLayout(); Drain();
            var editor = Field<BehaviorEditorControl>(workspace, "editor");
            var name = Field<TextBox>(editor, "name");
            Require(save.IsVisible && command.CanExecute(null, workspace) && command.CanExecute(null, name),
                "A valid visible preset must allow Save from both the window shortcut target and a nested text input.");

            name.Text = "An edit awaiting validation";
            Require(!save.IsVisible && !command.CanExecute(null, workspace) && Field<Button>(workspace, "draftSave").IsVisible,
                "Ctrl+S must follow Save to mod availability and must not become Save draft while an edit awaits validation.");
            Invoke(workspace, "CommitEditor");
            Field<DispatcherTimer>(workspace, "editTimer").Stop();
            Validate(workspace, entry); Drain();
            Require(save.IsVisible && save.IsEnabled && command.CanExecute(null, name) &&
                entry.Document.Blocks.Single().Name == "An edit awaiting validation",
                "Finishing validation must restore both save actions for the captured edit without requiring another input event.");

            var tabs = Field<TabControl>(main, "MainTabs");
            tabs.SelectedIndex = 1; main.UpdateLayout();
            Require(!command.CanExecute(null, workspace), "Ctrl+S must not export a hidden editor from Game data.");
            Field<TabItem>(main, "HelpTab").Content = new TextBlock { Text = "Wiki page" };
            tabs.SelectedIndex = 2; main.UpdateLayout();
            Require(!command.CanExecute(null, workspace), "Ctrl+S must not export a hidden editor from Help.");
            tabs.SelectedIndex = 0; main.UpdateLayout();
            Require(command.CanExecute(null, workspace), "Returning to the visible valid preset must restore Ctrl+S.");
            Invoke(main, "ShowPresetBrowser"); main.UpdateLayout();
            Require(!command.CanExecute(null, workspace), "Back to presets must disable the hidden editor's save shortcut.");
            Invoke(main, "ShowEditor"); main.UpdateLayout();
            workspace.IsEnabled = false;
            Require(!command.CanExecute(null, workspace), "A disabled workspace must not accept the save shortcut.");
        }
        finally
        {
            Invoke(workspace, "ResetEditor"); SetField(workspace, "dirty", false);
            main.Close(); if (File.Exists(placementPath)) File.Delete(placementPath);
        }
    }

    private static void Validate(PresetWorkspace workspace, PresetProjectEntry entry)
    {
        var result = PresetValidation.Check(entry.Document, CancellationToken.None).GetAwaiter().GetResult();
        Require(result.IsValid, "The save-shortcut fixture must pass the normal AoT validation.");
        Field<Dictionary<string, PresetValidationResult>>(workspace, "validations")[entry.Id] = result;
        Invoke(workspace, "UpdateActions");
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Invoke(object owner, string name, params object?[] arguments) => owner.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object value) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
