using System.IO;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AoTPresetEditor;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Assets;
using Mutagen.Bethesda.Assets.DI;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

internal static class ModelPreviewChecks
{
    public static void Run()
    {
        var form = CatalogMetadata();
        var folder = Path.Combine(Path.GetTempPath(), "AoTModelPreviewChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var assets = GameAssets.FromLoadOrder(folder, [], []);
            PreviewControls(form with { Assets = assets });
            LoadingAndCancellation(form with { Assets = assets });
        }
        finally { Directory.Delete(folder, true); }
    }

    private static FormRow CatalogMetadata()
    {
        var original = new SkyrimMod(ModKey.FromNameAndExtension("Models.esm"), SkyrimRelease.SkyrimSE);
        var texture = original.TextureSets.AddNew(); texture.Diffuse = @"old\diffuse.dds";
        var deleted = original.TextureSets.AddNew(); deleted.Diffuse = @"old\removed.dds";
        var noDiffuse = original.TextureSets.AddNew();
        var item = original.Ingestibles.AddNew(); item.EditorID = "PreviewItem";
        item.Model = new Model
        {
            File = @"clutter\source.nif", AlternateTextures =
            [
                new() { Name = "Shape", Index = 3, NewTexture = new FormLink<ITextureSetGetter>(texture.FormKey) },
                new() { Name = "Deleted", Index = 4, NewTexture = new FormLink<ITextureSetGetter>(deleted.FormKey) },
                new() { Name = "Missing", Index = 5, NewTexture = new FormLink<ITextureSetGetter>(new FormKey(original.ModKey, 0xABCDE)) },
                new() { Name = "Empty", Index = 6, NewTexture = new FormLink<ITextureSetGetter>(noDiffuse.FormKey) }
            ]
        };
        var armor = original.Armors.AddNew();
        armor.WorldModel = new GenderedItem<ArmorModel?>(
            new ArmorModel { Model = new Model { File = @"armor\male.nif" } },
            new ArmorModel { Model = new Model { File = @"armor\female.nif" } });
        var femaleOnly = original.Armors.AddNew();
        femaleOnly.WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model() },
            new ArmorModel { Model = new Model { File = @"armor\female.nif" } });
        var art = original.ArtObjects.AddNew(); art.Model = new Model { File = @"effects\art.nif" };
        var book = original.Books.AddNew(); book.Model = new Model { File = @"clutter\book.nif" };
        var ordinaryItems = new IMajorRecord[]
        {
            original.Ingredients.AddNew(), original.Weapons.AddNew(), original.Scrolls.AddNew(), original.SoulGems.AddNew(),
            original.MiscItems.AddNew(), original.Ammunitions.AddNew(), original.Keys.AddNew(), original.AlchemicalApparatuses.AddNew()
        };
        foreach (var record in ordinaryItems) ((IModeled)record).Model = new Model { File = @"clutter\item.nif" };
        var patch = new SkyrimMod(ModKey.FromNameAndExtension("ModelPatch.esp"), SkyrimRelease.SkyrimSE);
        patch.Ingestibles.GetOrAddAsOverride(item).Model!.File = @"clutter\winner.nif";
        patch.TextureSets.GetOrAddAsOverride(texture).Diffuse = @"new\diffuse.dds";
        patch.TextureSets.GetOrAddAsOverride(deleted).IsDeleted = true;
        patch.Books.GetOrAddAsOverride(book).Model = null;
        var rows = new Dictionary<FormKey, FormRow>();
        var add = typeof(FormCatalog).GetMethod("AddRecords", BindingFlags.Static | BindingFlags.NonPublic)!;
        add.Invoke(null, [original, original.ModKey.FileName.String, rows]);
        add.Invoke(null, [patch, patch.ModKey.FileName.String, rows]);
        using var cache = new ISkyrimModGetter[] { original, patch }.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
        typeof(FormCatalog).GetMethod("ResolveModelTextures", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [rows, cache]);
        Require(rows[item.FormKey].ModelPath == @"Meshes\clutter\winner.nif" && rows[item.FormKey].Winner == "ModelPatch.esp",
            "Model preview must use the winning override's model path, relative to Data.");
        Require(rows[armor.FormKey].ModelPath == @"Meshes\armor\male.nif" && rows[femaleOnly.FormKey].ModelPath == @"Meshes\armor\female.nif",
            "Armor preview must use the male world model and fall back to the female world model when needed.");
        Require(rows[art.FormKey].ModelPath.Length == 0 && rows[book.FormKey].ModelPath.Length == 0,
            "Art effects and models removed by a winning override must not get item model previews.");
        Require(ordinaryItems.All(item => rows[item.FormKey].ModelPath == @"Meshes\clutter\item.nif"),
            "Ordinary item model types must retain their model paths.");
        var replacements = rows[item.FormKey].ModelAlternateTextures;
        Require(replacements[0] is { Name: "Shape", Index: 3, DiffusePath: @"Textures\new\diffuse.dds" },
            "Alternate texture metadata must retain the shape name/index and resolve the winning diffuse texture.");
        Require(replacements[1].DiffusePath is null && replacements[2].DiffusePath is null && replacements[3].DiffusePath == "",
            "Deleted, missing, and explicitly empty diffuse textures must remain distinguishable.");
        return rows[item.FormKey];
    }

    private static void PreviewControls(FormRow model)
    {
        var unavailable = new FormRow("Art effect", "Art", "ArtObject", "Models.esm", "Models.esm", "0x900~Models.esm");
        Require(ModelPreviewWindow.CanPreview(model) && !ModelPreviewWindow.CanPreview(unavailable),
            "Only forms with a supported model and its asset provider may offer model preview.");
        var picker = new FormPickerWindow([model, unavailable]);
        try
        {
            var preview = PreviewButton(picker);
            var results = Descendants<DataGrid>(picker).Single();
            Require(preview.Visibility == Visibility.Visible && !preview.IsEnabled, "The picker must wait for a model selection before enabling preview.");
            results.SelectedItem = model;
            Require(preview.IsEnabled, "Selecting an item model must enable preview.");
            results.SelectedItem = unavailable;
            Require(!preview.IsEnabled, "Selecting an unsupported form must disable preview.");
        }
        finally { picker.Close(); }
        var effectsPicker = new FormPickerWindow([unavailable]);
        try { Require(PreviewButton(effectsPicker).Visibility == Visibility.Collapsed, "An effects-only picker must not display an irrelevant model button."); }
        finally { effectsPicker.Close(); }
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var field = PresetSchema.GetFields("transformer", PresetKind.Custom).Single(candidate => candidate.Key == "finalFormEditorID");
        var editor = new ValueEditorControl(document, [model], PresetValue.Scalar(model.Identifier), "Value", "Result item", field: field);
        var scalarPreview = PreviewButton(editor); var input = Descendants<TextBox>(editor).Single();
        Require(scalarPreview.Visibility == Visibility.Visible && scalarPreview.IsEnabled, "A resolved item field must offer model preview.");
        input.Text = "UnknownItem";
        Require(scalarPreview.Visibility == Visibility.Collapsed, "An unresolved field must hide model preview.");
        input.Text = model.EditorId;
        Require(scalarPreview.Visibility == Visibility.Visible && scalarPreview.IsEnabled, "Entering a known EditorID must restore model preview.");
        var owners = PresetSchema.GetFields("rule", PresetKind.Custom).Single(candidate => candidate.Key == "owners");
        var list = PresetValue.List(); list.Items.Add(PresetValue.Scalar(model.Identifier));
        var listEditor = new ValueEditorControl(document, [model], list, "owners", "Applies to", field: owners);
        Descendants<Button>(listEditor).Single(button => Equals(button.Content, "Edit entry")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(PreviewButton(listEditor).IsEnabled && PreviewButton(listEditor).Visibility == Visibility.Visible,
            "The first scalar entry in an item list must also offer model preview.");
    }

    private static Button PreviewButton(DependencyObject parent) =>
        Descendants<Button>(parent).Single(button => Equals(button.Content, "Preview model..."));

    private static void LoadingAndCancellation(FormRow model)
    {
        var previous = SynchronizationContext.Current;
        var load = typeof(ModelPreviewWindow).GetMethod("LoadModel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var missing = new ModelPreviewWindow(model);
        using var blocked = new BlockedAssets();
        var cancelled = new ModelPreviewWindow(model with { Assets = blocked });
        try
        {
            var missingContext = new LoadContext(); SynchronizationContext.SetSynchronizationContext(missingContext);
            load.Invoke(missing, [missing, new RoutedEventArgs()]); Complete(missingContext.Completed.Task);
            Require(Field<TextBlock>(missing, "status").Text.StartsWith("Cannot preview this model:") && !Field<Button>(missing, "reset").IsEnabled,
                "Missing assets must produce an inline explanation and leave Close available.");
            Require(Descendants<Button>(missing).Single(button => Equals(button.Content, "Close")).IsEnabled,
                "A missing model must not trap the user in its preview window.");
            var cancelledContext = new LoadContext(); SynchronizationContext.SetSynchronizationContext(cancelledContext);
            load.Invoke(cancelled, [cancelled, new RoutedEventArgs()]);
            Require(blocked.Started.Wait(TimeSpan.FromSeconds(3)), "Model loading must acquire its asset asynchronously.");
            cancelled.Close(); blocked.Release.Set(); Complete(cancelledContext.Completed.Task);
            Require(Field<TextBlock>(cancelled, "status").Text == "Loading model..." && !Field<Button>(cancelled, "reset").IsEnabled,
                "Closing during loading must prevent a late error or successful preview from updating the closed window.");
        }
        finally
        {
            blocked.Release.Set(); missing.Close(); cancelled.Close(); SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void Complete(Task task)
    {
        var completion = task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!completion.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            completion.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        completion.GetAwaiter().GetResult();
    }

    private sealed class LoadContext : SynchronizationContext
    {
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state) => dispatcher.BeginInvoke(() => callback(state));
        public override void OperationCompleted() => Completed.TrySetResult();
    }

    private sealed class BlockedAssets : IAssetProvider, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool Exists(DataRelativePath path) => true;
        public bool TryGetSize(DataRelativePath path, out uint size) { size = 1; return true; }
        public bool TryGetStream(DataRelativePath path, [NotNullWhen(true)] out Stream? stream)
        {
            Started.Set(); Release.Wait(TimeSpan.FromSeconds(3)); stream = new MemoryStream([0]); return true;
        }
        public void Dispose() { Started.Dispose(); Release.Dispose(); }
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

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
