using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
        SavePaths();
        SaveOpenedPreset();
    }

    private static void SavePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "AoT-save-paths-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "SKSE", "Plugins", "AlchemyOfTime", "FOOD", "custom", "nested", "meat.yaml");
        var project = new PresetProject { Name = "Meat" };
        var entry = project.Add(PresetDocument.Parse("""
            ownerLists:
            - owners: FoodBeef
              stages: [{no: 0, name: "", duration: 1}]
              finalFormEditorID: FoodBeefCooked
              transformers: [{FormEditorID: Fire, duration: 1, finalFormEditorID: FoodBeefCooked}]
            """, "FOOD", PresetKind.Custom), "meat", source, "source hash");
        Require(project.GetSaveFolder() == root && project.GetSavePath(entry, root) == source,
            "An opened preset must retain its containing mod folder, nested path and .yaml extension for normal Save.");
        var draft = PresetProject.FromDraft(project.ToDraft());
        Require(draft.GetSaveFolder() == root && draft.GetSavePath(draft.Entries.Single(), root) == source,
            "Reopening an editor draft must retain the original Save destination.");
        var other = Path.Combine(Path.GetTempPath(), "AoT-other-save-" + Guid.NewGuid().ToString("N"));
        Require(project.GetSavePath(entry, other) == project.GetExportPath(entry, other),
            "Choosing another destination must export there instead of overwriting the original file.");
        var newEntry = project.Add(PresetDocument.Parse("formsLists: [{forms: FoodBeef, timeModulators: [{FormEditorID: Ice, magnitude: 0}]}]",
            "FOOD", PresetKind.Addon), "Cooling");
        Require(project.GetSaveFolder() == root && project.GetSavePath(newEntry, root) == project.GetExportPath(newEntry, root),
            "A new behavior file must use the existing preset's mod folder and its correct category directory.");
        project.ChangeBehaviorKind(entry.Document.Blocks.Single().Id, PresetKind.Addon);
        Require(project.GetSavePath(entry, root) == project.GetExportPath(entry, root) &&
            project.GetSavePath(entry, root) != source && entry.OpenedPath == source,
            "Changing an opened file's behavior kind must choose its new directory while retaining the source for retirement.");
        project.Add(PresetDocument.Parse("formsLists: [{forms: FoodBeef, timeModulators: [{FormEditorID: Ice, magnitude: 0}]}]",
            "FOOD", PresetKind.Addon), "External", Path.Combine(other, "SKSE", "Plugins", "AlchemyOfTime", "FOOD", "addon", "external.yml"));
        Require(project.GetSaveFolder() is null,
            "Importing files from different mod folders must not silently choose one destination for the combined preset.");
        var plain = new PresetProject();
        var plainPath = Path.Combine(root, "loose.yml");
        var loose = plain.Add(PresetDocument.Parse("formsLists: [{forms: FoodBeef}]", "FOOD", PresetKind.Addon), "loose", plainPath);
        Require(plain.GetSaveFolder() == root && plain.GetSavePath(loose, root) == plainPath,
            "A standalone opened YAML file must save back to its original location.");
        var driveRoot = Path.GetPathRoot(root)!;
        var drive = new PresetProject();
        var drivePath = Path.Combine(driveRoot, "AoT-drive-save-" + Guid.NewGuid().ToString("N") + ".yml");
        var driveEntry = drive.Add(loose.Document.Clone(), "Drive file", drivePath);
        Require(drive.GetSaveFolder() == driveRoot && drive.GetSavePath(driveEntry, driveRoot) == drivePath,
            "A source at the drive root must stay inside that root without adding a second directory separator.");
        Require(new PresetProject().GetSaveFolder() is null,
            "A new preset without a saved source must still require a destination.");
    }

    private static void SaveOpenedPreset()
    {
        var root = Path.Combine(Path.GetTempPath(), "AoT-save-opened-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "SKSE", "Plugins", "AlchemyOfTime", "FOOD", "addon", "nested", "cooking.yml");
        var placementPath = Path.Combine(root, "placement.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, """
            formsLists:
            - forms: FoodBeef
              transformers:
              - FormEditorID: Fire
                duration: 1
                finalFormEditorID: FoodBeefCooked
            """);
        var main = new MainWindow(null, placementPath)
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false
        };
        var workspace = Field<PresetWorkspace>(main, "Workspace");
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        try
        {
            main.Show();
            Require(Wait(workspace.Open(PresetCatalog.Discover(root).Single())),
                "The existing-preset Save fixture must open through the startup preset-list path.");
            main.UpdateLayout(); Drain();
            var project = Field<PresetProject>(workspace, "project");
            var entry = project.Entries.Single();
            SetBehaviorName(workspace, entry, "Saved to original");
            SaveWithoutDialog(main, workspace);
            Require(entry.OpenedPath == source && File.ReadAllText(source).Contains("Saved to original") &&
                Directory.EnumerateFiles(root, "*.yml", SearchOption.AllDirectories).SequenceEqual([source]),
                "Ctrl+S must update the original nested preset without creating a second canonical-path copy.");
            Require(Field<TextBlock>(workspace, "state").Text.StartsWith("✓ Saved to mod") &&
                Field<TextBlock>(workspace, "state").Text.Contains("1 warning"),
                "The completed save must retain its success feedback after refreshing the editor: " + Field<TextBlock>(workspace, "state").Text);
            SetBehaviorName(workspace, entry, "Saved again");
            Require(Field<TextBlock>(workspace, "state").Text.Contains("Unsaved changes"),
                "Editing after a successful save must replace the success feedback with unsaved state.");
            SaveWithoutDialog(main, workspace);
            Require(entry.OpenedPath == source && File.ReadAllText(source).Contains("Saved again") &&
                !Directory.EnumerateFiles(root, "*.aot-*", SearchOption.AllDirectories).Any(),
                "Subsequent Save must reuse the original destination and refresh the source change guard.");
            var beforeSaveAs = project.ToDraft();
            var beforeSaveAsBytes = File.ReadAllBytes(source);
            var saveAs = Field<MenuItem>(workspace, "fileSaveAs");
            Require(saveAs.IsEnabled, "The valid opened preset must offer File > Save to mod as.");
            CheckSaveDialogs(main, () => saveAs.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)),
                "Choose the destination mod folder");
            Require(project.ToDraft() == beforeSaveAs && File.ReadAllBytes(source).AsSpan().SequenceEqual(beforeSaveAsBytes) &&
                !Field<bool>(workspace, "dirty"),
                "Canceling the explicit Save to mod as folder choice must preserve source files, paths, hashes and saved state.");

            SetBehaviorName(workspace, entry, "Unsaved local edit");
            var beforeConflict = project.ToDraft();
            var externalSource = File.ReadAllText(source) + "\n# Edited outside the preset editor\n";
            File.WriteAllText(source, externalSource);
            var conflict = CheckSaveDialogs(main, () => ApplicationCommands.Save.Execute(null, workspace), "Cannot save preset");
            Require(conflict.Contains("A source file changed after you opened it: " + source) &&
                File.ReadAllText(source) == externalSource && project.ToDraft() == beforeConflict && Field<bool>(workspace, "dirty") &&
                !Directory.EnumerateFiles(root, "*.aot-*", SearchOption.AllDirectories).Any(),
                "Save must report a changed original file and preserve both its external edits and the user's unsaved preset.");
        }
        finally
        {
            Invoke(workspace, "ResetEditor"); SetField(workspace, "dirty", false);
            main.Close(); SynchronizationContext.SetSynchronizationContext(previousContext);
            Directory.Delete(root, true);
        }
    }

    private static void SetBehaviorName(PresetWorkspace workspace, PresetProjectEntry entry, string name)
    {
        Field<TextBox>(Field<BehaviorEditorControl>(workspace, "editor"), "name").Text = name;
        Invoke(workspace, "CommitEditor"); Field<DispatcherTimer>(workspace, "editTimer").Stop();
        Validate(workspace, entry); Drain();
    }

    private static void SaveWithoutDialog(Window host, PresetWorkspace workspace)
    {
        Require(ApplicationCommands.Save.CanExecute(null, workspace), "The opened valid preset must offer the normal Save command.");
        CheckSaveDialogs(host, () => ApplicationCommands.Save.Execute(null, workspace));
        Drain();
    }

    private static string CheckSaveDialogs(Window host, Action action, string? expectedDialog = null)
    {
        const uint ownerWindow = 4, closeWindowMessage = 0x0010;
        var owner = new WindowInteropHelper(host).Handle;
        var titles = new List<string>();
        var contents = new List<string>();
        var response = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        response.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (window, parameter) =>
        {
            if (GetWindow(window, ownerWindow) != owner) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            titles.Add(title.ToString());
            EnumChildWindows(window, (child, childParameter) =>
            {
                var text = new StringBuilder(4096);
                GetWindowText(child, text, text.Capacity);
                if (text.Length > 0) contents.Add(text.ToString());
                return true;
            }, 0);
            PostMessage(window, closeWindowMessage, 0, 0);
            return true;
        }, 0);
        response.Start();
        try { action(); }
        finally { response.Stop(); }
        Require(expectedDialog is null ? titles.Count == 0 : titles.Count > 0 && titles.All(title => title == expectedDialog),
            "The save action must show only its expected dialog (" + (expectedDialog ?? "none") + "): " + string.Join(", ", titles));
        return string.Join("\n", contents);
    }

    private static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    private static void Validate(PresetWorkspace workspace, PresetProjectEntry entry)
    {
        var result = Wait(PresetValidation.Check(entry.Document, CancellationToken.None));
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
    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
