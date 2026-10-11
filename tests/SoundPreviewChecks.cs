using System.IO;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AoTPresetEditor;
using Mutagen.Bethesda.Assets;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Mutagen.Bethesda.Assets.DI;

internal static class SoundPreviewChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AoTPresetEditor-sound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            const string sample = @"sound\fx\preview.wav";
            WriteArchive(Path.Combine(folder, "Base.bsa"), sample, [1]);
            WriteArchive(Path.Combine(folder, "First.bsa"), sample, [2]);
            WriteArchive(Path.Combine(folder, "Last.bsa"), sample, [3]);
            WriteArchive(Path.Combine(folder, "Last - Textures.bsa"), @"sound\fx\extra.wav", [4]);
            WriteArchive(Path.Combine(folder, "Last - Sounds.bsa"), sample, [8]);
            WriteArchive(Path.Combine(folder, "Disabled.bsa"), sample, [5]);
            var assets = GameAssets.FromLoadOrder(folder,
                [ModKey.FromNameAndExtension("First.esp"), ModKey.FromNameAndExtension("Last.esp")], ["Base.bsa"]);
            Require(Read(assets, sample).SequenceEqual(new byte[] { 3 }), "The last enabled plugin archive must override base and earlier plugin archives.");
            Require(Read(assets, @"SOUND\FX\EXTRA.WAV").SequenceEqual(new byte[] { 4 }), "Skyrim SE texture archives and case-insensitive paths must be supported.");
            var reversed = GameAssets.FromLoadOrder(folder,
                [ModKey.FromNameAndExtension("Last.esp"), ModKey.FromNameAndExtension("First.esp")], ["Base.bsa"]);
            Require(Read(reversed, sample).SequenceEqual(new byte[] { 2 }), "Changing plugin order must change the winning archived asset.");
            var explicitlyListed = GameAssets.FromLoadOrder(folder, [], ["Base.bsa", "Last - Sounds.bsa"]);
            Require(Read(explicitlyListed, sample).SequenceEqual(new byte[] { 8 }), "Arbitrary archive names must load only when explicitly INI-listed.");
            var baseOnly = GameAssets.FromLoadOrder(folder, [], ["Base.bsa"]);
            Require(Read(baseOnly, sample).SequenceEqual(new byte[] { 1 }), "Disabled plugin archives must not override INI archives.");
            var loose = Path.Combine(folder, sample);
            Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
            File.WriteAllBytes(loose, [6, 7]);
            Require(Read(assets, sample).SequenceEqual(new byte[] { 6, 7 }), "Loose MO2 Data assets must override every BSA.");
            Require(assets.TryGetSize(new DataRelativePath(sample), out var size) && size == 2, "Asset sizes must describe the same winning file as playback.");
            Require(!assets.TryGetStream(new DataRelativePath(@"sound\missing.wav"), out _), "Missing assets must remain missing.");
            File.Delete(loose);
            Require(Read(assets, sample).SequenceEqual(new byte[] { 3 }), "Removing a loose file must reveal the archive winner without stale audio caching.");

            var preview = new SoundPreview();
            preview.SetSamples(assets, [sample]);
            var panel = (StackPanel)preview.Content;
            var controls = (WrapPanel)panel.Children[0];
            var choices = (ComboBox)controls.Children[0];
            var play = (Button)controls.Children[1];
            var stop = (Button)controls.Children[2];
            Require(play.IsEnabled && !stop.IsEnabled && choices.Visibility == Visibility.Collapsed, "A sound with one sample must offer Play without autoplay or a redundant selector.");
            preview.SetSamples(assets, [sample, @"sound\fx\extra.wav"]);
            Require(choices.Visibility == Visibility.Visible && choices.Items.Count == 2 && !stop.IsEnabled, "Multiple samples must be individually selectable without autoplay.");
            preview.SetSamples(assets, []);
            Require(!play.IsEnabled && !stop.IsEnabled, "A descriptor with no samples cannot play stale audio from the previous selection.");
            preview.Stop();
            CatalogAndEditors(assets);
            CancellationAndErrors(assets);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void CatalogAndEditors(GameAssets assets)
    {
        var original = new SkyrimMod(ModKey.FromNameAndExtension("Sounds.esp"), SkyrimRelease.SkyrimSE);
        var sound = original.SoundDescriptors.AddNew(); sound.EditorID = "SampleSound";
        sound.SoundFiles.Add(new AssetLink<SkyrimSoundAssetType>(@"Data\Sound\FX\preview.wav"));
        var records = new Dictionary<FormKey, FormRow>();
        var addRecords = typeof(FormCatalog).GetMethod("AddRecords", BindingFlags.NonPublic | BindingFlags.Static)!;
        addRecords.Invoke(null, [original, "Sounds.esp", records]);
        Require(records[sound.FormKey].SoundFiles.SequenceEqual(new[] { @"Sound\FX\preview.wav" }), "Catalog sample paths must be Data-relative, including the sound base folder.");
        var patch = new SkyrimMod(ModKey.FromNameAndExtension("SoundPatch.esp"), SkyrimRelease.SkyrimSE);
        var changed = new SoundDescriptor(sound.FormKey, SkyrimRelease.SkyrimSE) { EditorID = "SampleSound" };
        changed.SoundFiles.Add(new AssetLink<SkyrimSoundAssetType>(@"Data\Sound\FX\extra.wav"));
        patch.SoundDescriptors.Add(changed);
        addRecords.Invoke(null, [patch, "SoundPatch.esp", records]);
        var winner = records[sound.FormKey] with { Assets = assets };
        Require(winner.Winner == "SoundPatch.esp" && winner.SoundFiles.SequenceEqual(new[] { @"Sound\FX\extra.wav" }), "Sound preview must use the winning override's samples.");
        var other = winner with { EditorId = "OtherSound", Identifier = "0x801~Sounds.esp", SoundFiles = [@"sound\fx\preview.wav"] };
        var item = new FormRow("Item", "Item", "Ingestible", "Sounds.esp", "Sounds.esp", "0x802~Sounds.esp");
        var picker = new FormPickerWindow([winner, other, item]);
        try
        {
            var grid = Field<DataGrid>(picker, "results");
            var preview = Field<SoundPreview>(picker, "soundPreview");
            grid.SelectedItem = winner;
            Require(preview.Visibility == Visibility.Visible && Field<Button>(preview, "play").IsEnabled && !Field<Button>(preview, "stop").IsEnabled,
                "Selecting a sound must offer preview without playing automatically.");
            Require((string)Field<ComboBox>(preview, "samples").SelectedItem == winner.SoundFiles[0], "The picker preview must use the selected sound.");
            grid.SelectedItem = other;
            Require((string)Field<ComboBox>(preview, "samples").SelectedItem == other.SoundFiles[0], "Changing the selected sound must replace the sample selection.");
            grid.UnselectAll();
            Require(preview.Visibility == Visibility.Collapsed && !Field<Button>(preview, "play").IsEnabled, "Deselecting the sound must hide and clear its preview.");
            grid.SelectedItem = item;
            Require(preview.Visibility == Visibility.Collapsed, "Unrelated forms must not show sound controls.");
        }
        finally { picker.Close(); }
        var field = PresetSchema.GetFields("stage", PresetKind.Custom).Single(field => field.Key == "sound");
        var editor = new ValueEditorControl(new PresetDocument { Category = "FOOD", Kind = PresetKind.Custom }, [winner, other, item],
            PresetValue.Scalar(winner.EditorId), "Value", field.Label, field: field);
        var soundPreview = Descendants<SoundPreview>(editor).Single();
        var entry = Descendants<TextBox>(editor).Single();
        Require(soundPreview.Visibility == Visibility.Visible, "A selected Sound field must show its sample preview.");
        entry.Text = other.Identifier;
        Require((string)Field<ComboBox>(soundPreview, "samples").SelectedItem == other.SoundFiles[0], "Editing a Sound field's identifier must update the sample preview.");
        entry.Text = "UnknownSound";
        Require(soundPreview.Visibility == Visibility.Collapsed && !Field<Button>(soundPreview, "play").IsEnabled, "An unresolved identifier must not retain the previous sound.");
    }

    private static void CancellationAndErrors(GameAssets assets)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            using var blocked = new BlockedAssets();
            var preview = new SoundPreview();
            preview.SetSamples(blocked, [@"sound\pending.wav"]);
            var pending = (Task)typeof(SoundPreview).GetMethod("PlaySelected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(preview, null)!;
            Require(blocked.Started.Wait(TimeSpan.FromSeconds(3)), "The sound load must start asynchronously.");
            preview.SetSamples(assets, []);
            blocked.Release.Set();
            Complete(pending);
            Require(!Field<Button>(preview, "stop").IsEnabled && Field<TextBlock>(preview, "status").Text == "This sound has no audio samples.",
                "A cancelled load must not play or replace the next selection's status.");
            preview.SetSamples(assets, [@"sound\missing.wav"]);
            var missing = (Task)typeof(SoundPreview).GetMethod("PlaySelected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(preview, null)!;
            Complete(missing);
            Require(Field<TextBlock>(preview, "status").Text.Contains("not found") && Field<Button>(preview, "play").IsEnabled && !Field<Button>(preview, "stop").IsEnabled,
                "Missing samples must report an actionable error and leave preview usable.");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void Complete(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private sealed class BlockedAssets : IAssetProvider, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool Exists(DataRelativePath path) => true;
        public bool TryGetSize(DataRelativePath path, out uint size) { size = 1; return true; }
        public bool TryGetStream(DataRelativePath path, [NotNullWhen(true)] out Stream? stream)
        {
            Started.Set(); Release.Wait(TimeSpan.FromSeconds(3));
            stream = new MemoryStream([0]); return true;
        }
        public void Dispose() { Started.Dispose(); Release.Dispose(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    public static void Playback(string sampleFile)
    {
        using var stream = File.OpenRead(sampleFile);
        using var playback = new SoundPlayback(stream);
        playback.Play(volume: 0);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (playback.SamplesPlayed == 0 && elapsed.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(10);
        Require(playback.SamplesPlayed > 0, "XAudio2 must actually consume the sample, not merely accept a file path.");
        playback.Dispose();
        Require(!playback.IsPlaying, "Stopping must destroy the playing voice and release its buffer.");
    }

    private static byte[] Read(GameAssets assets, string path)
    {
        Require(assets.TryGetStream(new DataRelativePath(path), out var stream), "Expected asset: " + path);
        using (stream)
        using (var copy = new MemoryStream()) { stream!.CopyTo(copy); return copy.ToArray(); }
    }

    private static void WriteArchive(string path, string assetPath, byte[] payload)
    {
        var folder = Encoding.UTF8.GetBytes(Path.GetDirectoryName(assetPath) + '\0');
        var name = Encoding.UTF8.GetBytes(Path.GetFileName(assetPath) + '\0');
        const uint headerSize = 36;
        const uint folderRecordSize = 24;
        const uint fileRecordSize = 16;
        var dataOffset = headerSize + folderRecordSize + 1 + folder.Length + fileRecordSize + name.Length;
        using var output = new BinaryWriter(File.Create(path));
        output.Write(new byte[] { (byte)'B', (byte)'S', (byte)'A', 0 });
        output.Write(105u); output.Write(headerSize); output.Write(3u);
        output.Write(1u); output.Write(1u); output.Write((uint)folder.Length); output.Write((uint)name.Length); output.Write(0u);
        output.Write(0UL); output.Write(1u); output.Write(0u); output.Write((ulong)(headerSize + folderRecordSize + name.Length));
        output.Write((byte)folder.Length); output.Write(folder);
        output.Write(0UL); output.Write((uint)payload.Length); output.Write((uint)dataOffset);
        output.Write(name); output.Write(payload);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
