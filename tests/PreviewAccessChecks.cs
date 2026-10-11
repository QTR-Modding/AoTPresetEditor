using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AoTPresetEditor;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Assets;
using Mutagen.Bethesda.Assets.DI;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Pfim;

internal static class PreviewAccessChecks
{
    public static void Run()
    {
        var assets = new UnopenedAssets();
        var model = new FormRow("Fire", "FireModel", "Static", "Preview.esm", "Preview.esm", "0x800~Preview.esm")
        {
            Assets = assets, Models = [new("Model", [new("meshes/fire.nif", [])])]
        };
        var texture = new FormRow("Shader", "ShaderTexture", "EffectShader", "Preview.esm", "Preview.esm", "0x801~Preview.esm")
        {
            Assets = assets, Textures = [new("Fill", "textures/fill.dds")]
        };
        var sound = new FormRow("Marker", "SoundMarker", "SoundMarker", "Preview.esm", "Preview.esm", "0x802~Preview.esm")
        {
            Assets = assets, SoundFiles = ["sound/marker.wav"]
        };
        var unavailable = new FormRow("Location", "Location", "Location", "Preview.esm", "Preview.esm", "0x803~Preview.esm");
        FormRow[] forms = [model, texture, sound, unavailable];
        GroupMembers(forms);
        ListSelection(forms);
        ScalarSelection(forms);
        PickerEffectControls(assets);
        Require(assets.Opened == 0, "Selecting forms and showing preview controls must not load assets or play sounds automatically.");
        TextureLoading();
    }

    private static void TextureLoading()
    {
        foreach (var compressed in new[] { false, true })
        {
            var data = CubeFixture(compressed);
            var image = ModelPreview.LoadTexture(new TextureAssets(data), "textures/cube.dds", CancellationToken.None);
            var faceSize = compressed ? 4 : 2;
            Require(image.IsFrozen && image.PixelWidth == faceSize * 4 && image.PixelHeight == faceSize * 3,
                "Texture preview must decode every compressed or uncompressed cubemap face into a frozen cross.");
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            (int X, int Y, byte R, byte G, byte B)[] colors =
            [ (2, 1, 255, 0, 0), (0, 1, 0, 255, 0), (1, 0, 0, 0, 255),
              (1, 2, 0, 255, 255), (1, 1, 255, 0, 255), (3, 1, 255, 255, 0) ];
            foreach (var color in colors)
            {
                var offset = (color.Y * faceSize * image.PixelWidth + color.X * faceSize) * 4;
                Require(pixels[offset] == color.B && pixels[offset + 1] == color.G && pixels[offset + 2] == color.R,
                    "The cube cross must contain the distinct +X, -X, +Y, -Y, +Z, -Z faces in their correct positions.");
            }
            try
            {
                ModelPreview.LoadTexture(new TextureAssets(data[..^1]), "textures/cube.dds", CancellationToken.None);
                throw new InvalidOperationException("A truncated cube must not silently show incomplete faces.");
            }
            catch (InvalidDataException) { }
        }
        var partial = ModelPreview.LoadTexture(new TextureAssets(CubeFixture(false, partial: true)), "textures/partial.dds", CancellationToken.None);
        Require(partial.PixelWidth == 8 && partial.PixelHeight == 6, "A legacy cubemap with selected faces must still produce a complete cross layout.");
        var cancelled = new CancellationToken(true);
        try { ModelPreview.LoadTexture(new TextureAssets(CubeFixture(false)), "textures/cube.dds", cancelled); throw new InvalidOperationException("Cancelled texture loading must stop."); }
        catch (OperationCanceledException) { }
    }

    private static byte[] CubeFixture(bool compressed, bool partial = false)
    {
        const uint allCubeFaces = 0xFE00, positiveXCubeFace = 0x600;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x20534444u); writer.Write(124u);
        writer.Write((uint)(DdsFlags.Caps | DdsFlags.Height | DdsFlags.Width | DdsFlags.PixelFormat |
            (compressed ? DdsFlags.LinearSize : DdsFlags.Pitch)));
        writer.Write(compressed ? 4u : 2u); writer.Write(compressed ? 4u : 2u);
        writer.Write(8u); writer.Write(0u); writer.Write(1u);
        for (var reserved = 0; reserved < 11; reserved++) writer.Write(0u);
        writer.Write(32u);
        writer.Write((uint)(compressed ? DdsPixelFormatFlags.Fourcc : DdsPixelFormatFlags.Rgb | DdsPixelFormatFlags.AlphaPixels));
        writer.Write((uint)(compressed ? CompressionAlgorithm.D3DFMT_DXT1 : CompressionAlgorithm.None));
        writer.Write(compressed ? 0u : 32u);
        foreach (var mask in new uint[] { 0xFF0000, 0xFF00, 0xFF, 0xFF000000 }) writer.Write(compressed ? 0u : mask);
        writer.Write(0x1008u); writer.Write(partial ? positiveXCubeFace : allCubeFaces);
        writer.Write(0u); writer.Write(0u); writer.Write(0u);
        uint[] rgba = [0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFF00FFFF, 0xFFFF00FF, 0xFFFFFF00];
        ushort[] rgb565 = [0xF800, 0x07E0, 0x001F, 0x07FF, 0xF81F, 0xFFE0];
        for (var face = 0; face < (partial ? 1 : rgba.Length); face++)
        {
            if (compressed) { writer.Write(rgb565[face]); writer.Write((ushort)0); writer.Write(0u); }
            else for (var pixel = 0; pixel < 4; pixel++) writer.Write(rgba[face]);
        }
        return stream.ToArray();
    }

    private static void GroupMembers(FormRow[] forms)
    {
        var group = new FormGroupDefinition("PreviewGroup", [.. forms.Select(form => form.Identifier), "MissingForm"]);
        var picker = new GroupPickerWindow([group], forms);
        try
        {
            Descendants<ListBox>(picker).Single().SelectedItem = group;
            CheckMemberSelection(picker);
            Descendants<ListBox>(picker).Single().SelectedIndex = -1;
            Require(VisiblePreviews(picker).Count == 0, "Clearing the selected group must hide its member previews.");
        }
        finally { picker.Close(); }
        var editor = new GroupEditorWindow([], forms, initialMembers: group.Members);
        try
        {
            CheckMemberSelection(editor);
            var members = Descendants<DataGrid>(editor).Single();
            members.SelectedIndex = 0;
            Click(editor, "Remove selected");
            Require(VisiblePreviews(editor).Count == 0, "Removing the selected member must not leave its preview available.");
        }
        finally { editor.Close(); }
    }

    private static void CheckMemberSelection(DependencyObject window)
    {
        var members = Descendants<DataGrid>(window).Single();
        Require(VisiblePreviews(window).Count == 0, "Group windows must wait for a member selection before offering a preview.");
        members.SelectedIndex = 0;
        Require(VisiblePreviews(window).SequenceEqual(["Preview model..."]), "A selected model member must offer model preview directly.");
        members.SelectedIndex = 1;
        Require(VisiblePreviews(window).SequenceEqual(["Preview textures..."]), "A selected shader member must offer its textures and hide the previous model.");
        members.SelectedIndex = 2;
        Require(VisiblePreviews(window).SequenceEqual(["Sound"]), "A SoundMarker with resolved samples must offer playback in group windows.");
        var sound = Descendants<SoundPreview>(window).Single();
        Require(Private<ComboBox>(sound, "samples").SelectedItem?.ToString() == "sound/marker.wav", "The sound controls must use the selected member's samples.");
        members.SelectedIndex = 3;
        Require(VisiblePreviews(window).Count == 0, "A member without preview assets must hide all preview controls.");
        members.SelectedIndex = 4;
        Require(VisiblePreviews(window).Count == 0, "An unresolved group member must hide all preview controls.");
    }

    private static void ListSelection(FormRow[] forms)
    {
        var document = PresetDocument.Create("FOOD", PresetKind.Custom);
        var value = PresetValue.List();
        foreach (var form in forms) value.Items.Add(PresetValue.Scalar(form.Identifier));
        var field = new PresetField("FormEditorID", "Trigger", FieldKind.FormList, "");
        var editor = new ValueEditorControl(document, forms, value, "Value", "Trigger", field: field);
        var list = Descendants<ListBox>(editor).Single();
        Require(VisiblePreviews(editor).SequenceEqual(["Preview model..."]), "A selected form-list entry must offer direct preview before Edit entry is opened.");
        Click(editor, "Edit entry");
        Require(VisiblePreviews(editor).SequenceEqual(["Preview model..."]), "Opening the entry editor must leave one active model preview, not two.");
        var entry = Descendants<TextBox>(editor).Single();
        entry.Text = forms[1].Identifier;
        Require(VisiblePreviews(editor).SequenceEqual(["Preview textures..."]), "Changing an open entry to a shader must update the scalar preview controls.");
        Click(editor, "Hide entry");
        Require(VisiblePreviews(editor).SequenceEqual(["Preview textures..."]), "Closing the entry editor must restore direct preview for its edited form.");
        list.SelectedIndex = 2;
        Require(VisiblePreviews(editor).SequenceEqual(["Sound"]), "Selecting a sound entry must offer direct playback with its editor closed.");
        Click(editor, "Edit entry");
        Require(VisiblePreviews(editor).SequenceEqual(["Sound"]), "Opening a sound entry must leave only one active playback control.");
        Click(editor, "Hide entry");
        list.SelectedIndex = 3;
        Require(VisiblePreviews(editor).Count == 0, "Selecting an unpreviewable list entry must hide the previous previews.");
        list.SelectedIndex = 0;
        Click(editor, "Remove");
        Require(VisiblePreviews(editor).SequenceEqual(["Preview textures..."]), "Deleting an entry must refresh previews to the remaining selection.");
        list.SelectedIndex = -1;
        Require(VisiblePreviews(editor).Count == 0, "Clearing the list selection must hide all previews.");
    }

    private static void ScalarSelection(FormRow[] forms)
    {
        var field = new PresetField("FormEditorID", "Form", FieldKind.Form, "");
        var editor = new ValueEditorControl(PresetDocument.Create("FOOD", PresetKind.Custom), forms,
            PresetValue.Scalar(forms[2].Identifier), "Value", "Form", field: field);
        Require(VisiblePreviews(editor).SequenceEqual(["Sound"]), "Sound previews must follow resolved samples even when the field is not restricted to SoundDescriptor.");
        var input = Descendants<TextBox>(editor).Single();
        input.Text = forms[1].EditorId;
        Require(VisiblePreviews(editor).SequenceEqual(["Preview textures..."]), "Scalar fields must resolve texture previews through EditorIDs as well as numeric identifiers.");
        input.Text = "MissingForm";
        Require(VisiblePreviews(editor).Count == 0, "Unresolved scalar fields must hide preview actions.");
    }

    private static void PickerEffectControls(UnopenedAssets assets)
    {
        var target = new FormRow("Raw", "Raw", "Ingestible", "Preview.esm", "Preview.esm", "Raw", ItemCategories.FOOD,
            IsBoundObject: true) { Assets = assets, Models = [new("Model", [new("meshes/item.nif", [])])] };
        var noModel = target with { Name = "No model", EditorId = "NoModel", Identifier = "NoModel", Models = [] };
        var art = new FormRow("Art", "Art", "ArtObject", "Preview.esm", "Preview.esm", "Art")
            { Assets = assets, Models = [new("Art", [new("meshes/art.nif", [])])] };
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Preview.esm"), SkyrimRelease.SkyrimSE);
        var shader = new FormRow("Shader", "Shader", "EffectShader", "Preview.esm", "Preview.esm", "Shader")
            { Assets = assets, Textures = [new("Fill", "textures/fill.dds")], EffectShader = mod.EffectShaders.AddNew() };
        IReadOnlyList<FormRow> targets = [target];
        var picker = new FormPickerWindow([art, shader], effectPreviewTargets: () => targets, previewItemCandidates: () => []);
        try
        {
            var table = Descendants<DataGrid>(picker).Single();
            table.SelectedItem = art;
            var preview = EffectPreview(picker);
            Require(DisplayedWithin(preview, picker) && preview.IsEnabled && preview.ToolTip?.ToString()?.Contains("Raw") == true,
                "An art-object candidate must expose applied preview using the caller's current target before it is chosen.");
            table.SelectedItem = shader;
            Require(preview.IsEnabled && !ModelPreviewWindow.CanPreview(shader) &&
                Descendants<Button>(picker).Any(button => Equals(button.Content, "Preview textures...") && button.IsEnabled),
                "A shader without a standalone model must offer applied preview alongside its texture inspection.");
            targets = [];
            table.SelectedItem = art;
            Require(!preview.IsEnabled && Equals(preview.ToolTip, "Select an item to preview this effect."),
                "An effect picker without a current item must disable applied preview with a reason.");
            targets = [noModel]; table.SelectedItem = shader;
            Require(!preview.IsEnabled && Equals(preview.ToolTip, "The selected item has no model to preview."),
                "A selected item without a model must not be confused with an absent target.");
            table.UnselectAll();
            Require(!preview.IsEnabled, "Clearing the effect candidate must disable its applied preview.");
        }
        finally { picker.Close(); }
        AppliedListControls([art, shader, target], art, shader, target);
        StandalonePreviewChoice([art, shader, target], art, target);
        GroupEffectContext([art, shader, target], art, shader, target);
        StandaloneGroupChoice([art, shader, target], art, target);
    }

    private static void AppliedListControls(FormRow[] forms, FormRow art, FormRow shader, FormRow target)
    {
        var value = PresetValue.List(); value.Items.Add(PresetValue.Scalar(art.Identifier)); value.Items.Add(PresetValue.Scalar(shader.Identifier));
        var field = new PresetField("FormEditorID", "Trigger", FieldKind.FormList, "");
        var editor = new ValueEditorControl(PresetDocument.Create("FOOD", PresetKind.Custom), forms, value, "Value", "Trigger",
            field: field, sourceForms: [target]);
        IReadOnlyList<Button> Applied() => Descendants<Button>(editor)
            .Where(button => Equals(button.Content, "Preview on item...") && DisplayedWithin(button, editor) && button.IsEnabled).ToArray();
        Require(Applied().Count == 1 && Applied()[0].ToolTip?.ToString()?.Contains("Raw") == true,
            "A selected art-object list entry must offer applied preview directly with its editor closed.");
        Click(editor, "Edit entry");
        Require(Applied().Count == 1, "Opening an effect list entry must leave one active applied preview rather than duplicate actions.");
        Descendants<TextBox>(editor).Single().Text = shader.Identifier;
        Require(Applied().Count == 1, "Changing an open list entry to a shader without a model must retain its applied preview.");
        Click(editor, "Hide entry");
        Require(Applied().Count == 1 && Applied()[0].ToolTip?.ToString()?.Contains("Raw") == true,
            "Closing an edited shader list entry must restore its direct applied preview on the same current item.");
        Descendants<ListBox>(editor).Single().SelectedIndex = -1;
        Require(Applied().Count == 0, "Clearing list selection must hide its previously enabled applied preview.");
    }
    private static void StandalonePreviewChoice(FormRow[] forms, FormRow art, FormRow target)
    {
        var picker = new FormPickerWindow(forms, browseOnly: true);
        try
        {
            Descendants<DataGrid>(picker).Single().SelectedItem = art;
            var preview = EffectPreview(picker);
            var choose = Descendants<Button>(picker).Single(button => Equals(button.Content, "Choose preview item..."));
            Require(!preview.IsEnabled && DisplayedWithin(choose, picker),
                "Standalone browsing must require an explicit preview target rather than choose a catalog item automatically.");
            RespondToDialog<FormPickerWindow>(picker, choose, itemPicker =>
            {
                var table = Descendants<DataGrid>(itemPicker).Single();
                Require(table.Items.Cast<FormRow>().SequenceEqual([target]),
                    "Standalone target choices must contain actual bound-item models, excluding the art effect itself.");
                table.SelectedItem = target;
                Raise(Descendants<Button>(itemPicker).Single(button => Equals(button.Content, "Choose")));
            });
            Require(preview.IsEnabled && preview.ToolTip?.ToString()?.Contains("Raw") == true && picker.SelectedForms.Count == 0 &&
                (Descendants<DataGrid>(picker).Single().SelectedItem as FormRow) == art,
                "Choosing a standalone preview target must enable the highlighted effect without choosing a form or changing the browser selection.");
            Descendants<DataGrid>(picker).Single().SelectedItem = target;
            Require(VisiblePreviews(picker).Contains("Preview model..."), "Ordinary model previews must remain available after inspecting an applied effect.");
        }
        finally { picker.Close(); }
    }

    private static void GroupEffectContext(FormRow[] forms, FormRow art, FormRow shader, FormRow target)
    {
        var group = new FormGroupDefinition("Effects", [art.Identifier, shader.Identifier]);
        var picker = new GroupPickerWindow([group], forms, effectPreviewTargets: () => [target], previewItemCandidates: () => []);
        try
        {
            Descendants<ListBox>(picker).Single().SelectedItem = group;
            var members = Descendants<DataGrid>(picker).Single(); members.SelectedIndex = 1;
            Require(EffectPreview(picker).IsEnabled && EffectPreview(picker).ToolTip?.ToString()?.Contains("Raw") == true,
                "A selected shader group member must use the same current item as its caller.");
            RespondToDialog<GroupEditorWindow>(picker,
                Descendants<Button>(picker).Single(button => Equals(button.Content, "New group...")), editor =>
                {
                    RespondToDialog<FormPickerWindow>(editor,
                        Descendants<Button>(editor).Single(button => Equals(button.Content, "Add forms...")), nested =>
                        {
                            Descendants<DataGrid>(nested).Single().SelectedItem = art;
                            Require(EffectPreview(nested).IsEnabled && EffectPreview(nested).ToolTip?.ToString()?.Contains("Raw") == true,
                                "Nested group creation must pass the original current item into its member chooser.");
                            nested.DialogResult = false;
                        });
                    editor.DialogResult = false;
                });
            Require(picker.CreatedGroups.Count == 0 && picker.SelectedGroup is null,
                "Previewing candidates and cancelling nested group creation must not select or create a group.");
        }
        finally { picker.Close(); }
        var groupEditor = new GroupEditorWindow([], forms, initialMembers: group.Members,
            effectPreviewTargets: () => [target], previewItemCandidates: () => []);
        try
        {
            Descendants<DataGrid>(groupEditor).Single().SelectedIndex = 0;
            Require(EffectPreview(groupEditor).IsEnabled && EffectPreview(groupEditor).ToolTip?.ToString()?.Contains("Raw") == true,
                "An existing art-object member in the group editor must retain its caller's preview item.");
        }
        finally { groupEditor.Close(); }
    }

    private static void StandaloneGroupChoice(FormRow[] forms, FormRow art, FormRow target)
    {
        var group = new FormGroupDefinition("StandaloneEffects", [art.Identifier]);
        var picker = new GroupPickerWindow([group], forms);
        try
        {
            Descendants<ListBox>(picker).Single().SelectedItem = group;
            Descendants<DataGrid>(picker).Single().SelectedIndex = 0;
            Require(!EffectPreview(picker).IsEnabled, "A standalone effect group must not automatically use an arbitrary catalog model.");
            RespondToDialog<GroupEditorWindow>(picker,
                Descendants<Button>(picker).Single(button => Equals(button.Content, "New group...")), editor =>
                {
                    RespondToDialog<FormPickerWindow>(editor,
                        Descendants<Button>(editor).Single(button => Equals(button.Content, "Add forms...")), nested =>
                        {
                            Descendants<DataGrid>(nested).Single().SelectedItem = art;
                            RespondToDialog<FormPickerWindow>(nested,
                                Descendants<Button>(nested).Single(button => Equals(button.Content, "Choose preview item...")), itemPicker =>
                                {
                                    var table = Descendants<DataGrid>(itemPicker).Single();
                                    Require(table.Items.Cast<FormRow>().SequenceEqual([target]),
                                        "A nested standalone group chooser must retain bound-item model candidates.");
                                    table.SelectedItem = target;
                                    Raise(Descendants<Button>(itemPicker).Single(button => Equals(button.Content, "Choose")));
                                });
                            Require(EffectPreview(nested).IsEnabled && Descendants<Button>(nested)
                                .Single(button => Equals(button.Content, "Choose preview item...")).Visibility == Visibility.Visible,
                                "Choosing a representative item must enable preview while keeping the nested chooser available for changing that item.");
                            nested.DialogResult = false;
                        });
                    editor.DialogResult = false;
                });
            Require(EffectPreview(picker).IsEnabled && EffectPreview(picker).ToolTip?.ToString()?.Contains("Raw") == true &&
                Descendants<Button>(picker).Single(button => Equals(button.Content, "Choose preview item...")).Visibility == Visibility.Visible &&
                picker.CreatedGroups.Count == 0 && picker.SelectedGroup is null,
                "A preview item chosen in a nested group member picker must remain available to its parent without committing group edits.");
        }
        finally { picker.Close(); }
    }
    private static Button EffectPreview(DependencyObject parent) => Descendants<Button>(parent)
        .Single(button => Equals(button.Content, "Preview on item..."));

    private static void RespondToDialog<T>(Window owner, Button action, Action<T> respond) where T : Window
    {
        var shown = owner.IsVisible;
        if (!shown)
        {
            owner.Left = owner.Top = -12000; owner.Opacity = 0; owner.ShowInTaskbar = false; owner.ShowActivated = false;
            owner.Show();
        }
        Exception? failure = null;
        var response = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var dialog = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                .OfType<T>().Single(window => window.Owner == owner);
            try { dialog.Opacity = 0; dialog.ShowInTaskbar = false; respond(dialog); }
            catch (Exception error) { failure = error; dialog.DialogResult = false; }
        }));
        try { Raise(action); }
        finally { response.Abort(); if (!shown) owner.Hide(); }
        if (failure is not null) throw failure;
        Require(response.Status == DispatcherOperationStatus.Completed, "The actual owned preview chooser must be reachable from its button.");
    }

    private static void Raise(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static List<string> VisiblePreviews(DependencyObject root)
    {
        var result = Descendants<Button>(root)
            .Where(button => button.Content is "Preview model..." or "Preview textures..." && DisplayedWithin(button, root) && button.IsEnabled)
            .Select(button => (string)button.Content).ToList();
        result.AddRange(Descendants<SoundPreview>(root).Where(control => DisplayedWithin(control, root)).Select(_ => "Sound"));
        return result;
    }

    private static bool DisplayedWithin(DependencyObject element, DependencyObject root)
    {
        for (var current = element; current is not null && current != root; current = LogicalTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static void Click(DependencyObject parent, string label) =>
        Descendants<Button>(parent).Single(button => Equals(button.Content, label) && DisplayedWithin(button, parent))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static T Private<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class UnopenedAssets : IAssetProvider
    {
        public int Opened { get; private set; }
        public bool Exists(DataRelativePath path) => true;
        public bool TryGetSize(DataRelativePath path, out uint size) { size = 0; return true; }
        public bool TryGetStream(DataRelativePath path, [NotNullWhen(true)] out Stream? stream) { Opened++; stream = null; return false; }
    }

    private sealed class TextureAssets(byte[] bytes) : IAssetProvider
    {
        public bool Exists(DataRelativePath path) => true;
        public bool TryGetSize(DataRelativePath path, out uint size) { size = (uint)bytes.Length; return true; }
        public bool TryGetStream(DataRelativePath path, [NotNullWhen(true)] out Stream? stream) { stream = new MemoryStream(bytes); return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
