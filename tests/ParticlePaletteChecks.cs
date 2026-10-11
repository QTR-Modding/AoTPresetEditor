using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AoTPresetEditor;

internal static class ParticlePaletteChecks
{
    public static void Run()
    {
        const int width = 4, height = 4, bytesPerPixel = 4;
        uint[] sourcePixels = Enumerable.Range(0, width * height).Select(index => 0xFF000000u | (uint)index).ToArray();
        uint[] palettePixels = Enumerable.Range(0, width * height).Select(index => 0x80000000u | (uint)(index << 16)).ToArray();
        var source = Bitmap(width, height, sourcePixels);
        var palette = Bitmap(width, height, palettePixels);
        var packed = ModelPreviewParticleShader.PackPalette(source, palette, sourcePixels.Length * bytesPerPixel * 2);
        var info = packed.Texture.TextureInfoLoader.Load(packed.Texture.Guid);
        Require(info.Width == width && info.Height == height * 2 && packed.Bytes == width * height * bytesPerPixel * 2 && !packed.Reduced,
            "The packed texture must preserve the source atlas's dimensions and reserve a separate palette region.");
        var pixels = info.TextureRaw!;
        for (var index = 0; index < sourcePixels.Length; index++)
        {
            Require(BitConverter.ToUInt32(pixels, index * bytesPerPixel) == sourcePixels[index],
                "All source atlas cells must retain their authored pixels and alpha.");
            Require(BitConverter.ToUInt32(pixels, (sourcePixels.Length + index) * bytesPerPixel) == palettePixels[index],
                "Palette color and alpha must retain their authored values independently of source pixels.");
        }
        Expect<InvalidDataException>(() => ModelPreviewParticleShader.PackPalette(source, palette, packed.Bytes - 1),
            "Packed palette allocation must honor the remaining scene texture budget.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Expect<OperationCanceledException>(() => ModelPreviewParticleShader.PackPalette(source, palette, packed.Bytes, cancellation.Token),
            "Palette packing must honor cancellation.");
        var large = Bitmap(2048, 1, new uint[2048]);
        var reduced = ModelPreviewParticleShader.PackPalette(large, palette, 1024 * 1024 * 8);
        var reducedInfo = reduced.Texture.TextureInfoLoader.Load(reduced.Texture.Guid);
        Require(reduced.Reduced && reducedInfo.Width <= 1024 && reduced.Bytes <= 1024 * 1024 * 8,
            "Oversized palette textures must be explicitly marked as reduced and stay within the allocation bound.");
    }

    private static BitmapSource Bitmap(int width, int height, uint[] pixels)
        => BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
