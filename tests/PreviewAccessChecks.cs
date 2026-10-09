using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AoTPresetEditor;
using Mutagen.Bethesda.Assets;
using Mutagen.Bethesda.Assets.DI;
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
