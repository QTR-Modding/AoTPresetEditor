using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AoTPresetEditor;

internal static class PresetOpenChecks
{
    private static readonly string[] Categories = ["FOOD", "INGR"];
    private const string Addon = "formsLists: [{forms: FoodBeef, transformers: [{FormEditorID: Fire, duration: 1, finalFormEditorID: FoodBeefCooked}]}]";
    private const string Custom = "ownerLists: [{owners: IngredientA, stages: [{no: 0, name: Fresh, duration: 1}], finalFormEditorID: IngredientB}]";
    private const string Default = "stages: [{no: 0, name: Fresh, duration: 1}]\nfinalFormEditorID: FoodBeefCooked";

    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AoTPresetEditor-open-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var folder = Path.Combine(root, "Mixed");
            var addon = Write(folder, "mod/SKSE/Plugins/AlchemyOfTime/food/addon/INGR/Cooking.YAML", Addon);
            var custom = Write(folder, "mod/SKSE/Plugins/AlchemyOfTime/INGR/custom/Aging.yml", Custom);
            var defaults = Write(folder, "extracted/AoT_defaultFOOD.YAML", Default);
            var extra = Write(root, "Other/FOOD/addon/Extra.YML", Addon);
            var draft = Write(folder, "Unfinished.aotdraft", PresetDocument.Create("FOOD", PresetKind.Addon).ToDraft());
            Write(folder, "README.txt", "Not a preset");
            Write(folder, "Cooking.yml.aot-backup", Addon);
            Write(folder, "Cooking.yaml.bak", Addon);
            var files = PresetCatalog.DiscoverSelection([folder], Categories);
            Require(files.Count == 3 && files.Select(file => file.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals([addon, custom, defaults]), "Opening a folder must recursively include YAML extensions regardless of case, while ignoring drafts, backups, and unrelated files.");
            var overlaps = PresetCatalog.DiscoverSelection([addon, folder, Path.GetDirectoryName(addon)!, extra, addon.ToUpperInvariant()], Categories);
            Require(overlaps.Count == 4 && overlaps[0].FullPath == addon && overlaps.Select(file => file.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase).Count == 4,
                "Multiple folders and files must deduplicate overlapping paths without losing the first selected file.");
            Require(files.Single(file => file.FullPath == addon) is { Category: "FOOD", Kind: PresetKind.Addon } &&
                files.Single(file => file.FullPath == custom) is { Category: "INGR", Kind: PresetKind.Custom } &&
                files.Single(file => file.FullPath == defaults).Category == "FOOD",
                "Nested category directories and a standalone default filename must identify each file's item type.");
            Require(PresetCatalog.DiscoverSelection([draft], Categories).Single().FullPath == draft,
                "An explicitly selected editor draft must remain available.");
            Reject(() => PresetCatalog.DiscoverSelection([draft, addon], Categories));
            Reject(() => PresetCatalog.DiscoverSelection([draft, folder], Categories));
            var ignoredOnly = Path.Combine(root, "No presets");
            Write(ignoredOnly, "Empty.aotdraft", "{}");
            Write(ignoredOnly, "Old.yml.aot-backup", Addon);
            Reject(() => PresetCatalog.DiscoverSelection([ignoredOnly], Categories));
            Workspace(root, files, addon, extra);
        }
        finally
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Require(Path.GetDirectoryName(root) == parent && Path.GetFileName(root).StartsWith("AoTPresetEditor-open-", StringComparison.Ordinal),
                "Only the unique temporary fixture directory may be removed.");
            Directory.Delete(root, true);
        }
    }

    private static void Workspace(string root, IReadOnlyList<PresetFile> files, string singlePath, string extraPath)
    {
        var workspace = new PresetWorkspace();
        var host = new Window { Content = workspace, Width = 1000, Height = 750, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(workspace.Dispatcher));
        try
        {
            host.Show();
            Require(Open(workspace, files, false), "A folder's mixed files must open successfully.");
            var project = Field<PresetProject>(workspace, "project");
            Require(project.Name == "Mixed presets" && project.Entries.Count == 3 &&
                project.Entries.Select(entry => (entry.Document.Category, entry.Document.Kind)).ToHashSet()
                    .SetEquals([("FOOD", PresetKind.Addon), ("INGR", PresetKind.Custom), ("FOOD", PresetKind.Default)]),
                "Addon, custom, and default files for different item types must open in one project.");
            foreach (var file in files)
            {
                var entry = project.Entries.Single(entry => entry.OpenedPath == file.FullPath);
                Require(entry.FileName == Path.GetFileNameWithoutExtension(file.FullPath) && entry.OpenedHash ==
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(file.FullPath)))),
                    "Opening several files must retain every original basename, source path, and source-change hash.");
            }
            var source = project.ToDraft();
            var unknown = Write(root, "Unknown/Unplaced.yaml", Addon);
            var cancelledFiles = PresetCatalog.DiscoverSelection([singlePath, unknown], Categories);
            var cancelled = false;
            var response = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            response.Tick += (_, _) =>
            {
                var dialog = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(item => item.RootVisual)
                    .OfType<Window>().SingleOrDefault(window => window.Owner == host && window.Title == "Which items does this file affect?");
                if (dialog is null) return;
                cancelled = true;
                dialog.Opacity = 0;
                dialog.DialogResult = false;
            };
            response.Start();
            try { Require(!Open(workspace, cancelledFiles, false), "Cancelling the unknown item's category must cancel the whole open."); }
            finally { response.Stop(); }
            Require(cancelled && ReferenceEquals(Field<PresetProject>(workspace, "project"), project) && project.ToDraft() == source,
                "Cancelling after a known file was parsed must leave the original project unchanged.");

            var malformed = Write(root, "Broken/INGR/custom/Broken.yaml", "ownerLists: [");
            ExpectOpenFailure(host, workspace, PresetCatalog.DiscoverSelection([singlePath, malformed], Categories), false);
            Require(ReferenceEquals(Field<PresetProject>(workspace, "project"), project) && project.ToDraft() == source,
                "A malformed later file must not replace the project with the files parsed before it.");
            var collision = Write(root, "Collision/FOOD/AoT_defaultFOOD.yml", Default);
            ExpectOpenFailure(host, workspace, PresetCatalog.DiscoverSelection([extraPath, collision], Categories), true);
            Require(ReferenceEquals(Field<PresetProject>(workspace, "project"), project) && project.ToDraft() == source,
                "A later default collision must not partially import earlier files into the original project.");

            var draft = Write(root, "Saved.aotdraft", source);
            Wait((Task)Invoke(workspace, "OpenDraft", draft)!);
            Require(Field<PresetProject>(workspace, "project").ToDraft() == source && Field<string>(workspace, "draftPath") == draft,
                "The ordinary draft path must preserve all documents and their source tracking.");
            Require(Wait(workspace.Open(files.Single(file => file.FullPath == singlePath))),
                "The existing single-file entry point must still open a preset.");
            project = Field<PresetProject>(workspace, "project");
            Require(project.Entries.Count == 1 && project.Name == "Cooking" && project.Entries.Single().OpenedPath == singlePath &&
                Field<string?>(workspace, "draftPath") is null,
                "Opening one file must replace the project, use its basename, and clear the previous draft path.");
            Require(Open(workspace, PresetCatalog.DiscoverSelection([extraPath], Categories), true), "A selected file must import into the open project.");
            project = Field<PresetProject>(workspace, "project");
            Require(project.Entries.Count == 2 && project.Name == "Cooking" && Field<bool>(workspace, "dirty") &&
                Field<PresetProjectEntry>(workspace, "activeEntry").OpenedPath == extraPath &&
                ReferenceEquals(Field<PresetDocument>(workspace, "document"), project.Entries.Single(entry => entry.OpenedPath == extraPath).Document),
                "Importing must preserve the project name and select the newly imported behavior in its actual project entry.");
        }
        finally
        {
            Invoke(workspace, "ResetEditor");
            host.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void ExpectOpenFailure(Window host, PresetWorkspace workspace, IReadOnlyList<PresetFile> files, bool import)
    {
        const uint ownerWindow = 4;
        const uint clickButtonMessage = 0x00F5;
        var owner = new WindowInteropHelper(host).Handle;
        var dismissed = false;
        var response = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        response.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (window, parameter) =>
        {
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (GetWindow(window, ownerWindow) != owner || title.ToString() != "Cannot open preset") return true;
            EnumChildWindows(window, (child, childParameter) =>
            {
                var className = new StringBuilder(256);
                GetClassName(child, className, className.Capacity);
                if (className.ToString() != "Button") return true;
                dismissed = PostMessage(child, clickButtonMessage, 0, 0);
                return !dismissed;
            }, 0);
            return false;
        }, 0);
        response.Start();
        try { Require(!Open(workspace, files, import), "Invalid or conflicting files must fail to open."); }
        finally { response.Stop(); }
        Require(dismissed, "A failed open must report its error in this test workspace's own message box.");
    }

    private static bool Open(PresetWorkspace workspace, IReadOnlyList<PresetFile> files, bool import) =>
        Wait((Task<bool>)Invoke(workspace, "OpenPresets", files, import, "Mixed presets")!);
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static string Write(string root, string relative, string source)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
        return path;
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("The invalid file selection was accepted.");
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static object? Invoke(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
