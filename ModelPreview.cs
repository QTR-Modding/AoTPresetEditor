using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Mutagen.Bethesda.Assets;
using Mutagen.Bethesda.Assets.DI;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using Pfim;

namespace AoTPresetEditor;

public sealed record ModelPreviewResult(Model3DGroup Model, string Details);

public static class ModelPreview
{
    private const int MaxModelBytes = 64 * 1024 * 1024;
    private const int MaxTextureBytes = 32 * 1024 * 1024;
    private const long MaxTexturePixels = 16 * 1024 * 1024;
    private const int MaxDecodedTextureBytes = 128 * 1024 * 1024;
    private const int MaxVertices = 1_000_000;
    private const int MaxTriangles = 1_000_000;
    private const int MaxDetailMessages = 6;
    private const int ReadBufferBytes = 64 * 1024;
    private const double BitmapDpi = 96;

    public static ModelPreviewResult Load(FormRow form, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assets = form.Assets ?? throw new InvalidOperationException("Game assets are not available. Reload game data first.");
        if (string.IsNullOrWhiteSpace(form.ModelPath)) throw new InvalidOperationException("This form has no model file.");
        using var stream = ReadAsset(assets, AssetPath(form.ModelPath, "meshes"), MaxModelBytes, cancellationToken);
        var nif = new NifFile();
        if (nif.Load(stream) != 0 || !nif.Valid) throw new InvalidDataException("The model file could not be read as a supported NIF.");
        cancellationToken.ThrowIfCancellationRequested();
        var model = new Model3DGroup();
        var messages = new HashSet<string>();
        var vertexCount = 0;
        var triangleCount = 0;
        var decodedBytes = 0L;
        var shapeIndex = 0;
        var usedReplacements = new HashSet<int>();
        foreach (var shape in SceneShapes(nif, cancellationToken))
        {
            var index = shapeIndex++;
            cancellationToken.ThrowIfCancellationRequested();
            if (shape is NiParticleSystem) { messages.Add("Particle systems are omitted."); continue; }
            if (shape.IsSkinned || shape.HasSkinInstance) { messages.Add("Skinned geometry is omitted."); continue; }
            var vertices = shape is BSTriShape bs ? bs.VertexPositions : shape.GeometryData?.Vertices;
            if (vertices is null || vertices.Count == 0 || shape.Triangles.Count == 0) continue;
            vertexCount = checked(vertexCount + vertices.Count);
            triangleCount = checked(triangleCount + shape.Triangles.Count);
            if (vertexCount > MaxVertices || triangleCount > MaxTriangles)
                throw new InvalidDataException("This model exceeds the preview geometry limit.");
            var shader = nif.GetShader(shape);
            var lighting = shader as BSLightingShaderProperty;
            var mesh = BuildMesh(nif, shape, vertices, lighting, cancellationToken);
            Brush brush = new SolidColorBrush(Colors.LightGray);
            if (lighting is not null)
            {
                var replacements = form.ModelAlternateTextures.Where(x => x.Index == index).ToList();
                string? texture = nif.GetBlock<BSShaderTextureSet>(lighting.TextureSetRef)?.Textures.FirstOrDefault()?.Content;
                if (replacements.Count > 0 && !lighting.IsTypeSkinTint)
                {
                    usedReplacements.Add(index);
                    if (replacements.Count != 1 || replacements[0].DiffusePath is null)
                    {
                        texture = null;
                        messages.Add($"Alternate texture for shape {index} could not be resolved; showing neutral geometry.");
                    }
                    else texture = replacements[0].DiffusePath;
                }
                else if (replacements.Count > 0) usedReplacements.Add(index);
                var alpha = nif.GetBlock(shape.AlphaPropertyRef) ?? shape.Properties?.References
                    .Select(x => nif.GetBlock<NiAlphaProperty>(x.Index)).FirstOrDefault(x => x is not null);
                var textureAlpha = alpha is not null && (alpha.Flags.AlphaBlend || alpha.Flags.AlphaTest);
                if (alpha is not null && alpha.Flags.AlphaTest) messages.Add("Alpha testing is approximated with transparency.");
                if (alpha is not null && alpha.Flags.AlphaBlend) messages.Add("Transparency uses standard blending; Skyrim blend modes and sorting can differ.");
                if (!string.IsNullOrWhiteSpace(texture) && mesh.TextureCoordinates.Count == vertices.Count)
                {
                    try
                    {
                        var bitmap = ReadTexture(assets, AssetPath(texture, "textures"), textureAlpha, ref decodedBytes, cancellationToken);
                        brush = new ImageBrush(bitmap)
                        {
                            Viewport = new Rect(0, 0, 1, 1), ViewportUnits = BrushMappingMode.Absolute,
                            TileMode = TileMode.Tile, Stretch = Stretch.Fill
                        };
                    }
                    catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
                    {
                        messages.Add($"Texture {texture}: {error.Message} Showing neutral geometry.");
                    }
                }
                else if (string.IsNullOrWhiteSpace(texture)) messages.Add("Some shapes have no diffuse texture; showing neutral geometry.");
                else messages.Add("Some shapes have no usable texture coordinates; showing neutral geometry.");
                if (!float.IsFinite(lighting.Alpha)) throw new InvalidDataException("The model contains an invalid material opacity.");
                brush.Opacity = Math.Clamp(lighting.Alpha, 0, 1);
            }
            else messages.Add("Unsupported materials are shown as neutral geometry.");
            if (shape.HasVertexColors) messages.Add("Vertex colors are not simulated.");
            var material = new DiffuseMaterial(brush);
            model.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = shader?.DoubleSided == true ? material : null });
        }
        if (form.ModelAlternateTextures.Any(x => !usedReplacements.Contains(x.Index)))
            messages.Add("Some alternate textures target geometry that is not shown.");
        if (nif.HasUnknownBlocks) messages.Add("The NIF contains unsupported blocks.");
        cancellationToken.ThrowIfCancellationRequested();
        if (model.Children.Count == 0)
            throw new InvalidDataException("This model has no supported static triangle geometry to preview. " + string.Join(" ", messages));
        model.Freeze();
        var details = "Static model and textures. Game lighting, animation, and effects are not shown.";
        if (messages.Count > 0) details += "\n" + string.Join("\n", messages.Take(MaxDetailMessages));
        if (messages.Count > MaxDetailMessages) details += $"\n{messages.Count - MaxDetailMessages} more texture or material limitations.";
        return new(model, details);
    }

    private static IEnumerable<INiShape> SceneShapes(NifFile nif, CancellationToken cancellationToken)
    {
        // Match NIF scene-tree order: alternate texture indices include particle systems.
        var pending = new Stack<INiObject>();
        var visited = new HashSet<INiObject>(ReferenceEqualityComparer.Instance);
        if (nif.GetRootNode() is { } root) pending.Push(root);
        else if (nif.Blocks.FirstOrDefault() is { } block) pending.Push(block);
        while (pending.TryPop(out var block))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(block)) continue;
            if (block is INiShape shape) yield return shape;
            foreach (var reference in block.References.Reverse())
                if (nif.GetBlock<INiObject>(reference.Index) is { } child) pending.Push(child);
        }
    }

    private static MeshGeometry3D BuildMesh(NifFile nif, INiShape shape, List<System.Numerics.Vector3> vertices,
        BSLightingShaderProperty? shader, CancellationToken cancellationToken)
    {
        var transform = ShapeTransform(nif, shape);
        var mesh = new MeshGeometry3D();
        foreach (var vertex in vertices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = transform.Transform(new Point3D(vertex.X, vertex.Y, vertex.Z));
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
                throw new InvalidDataException("The model contains invalid vertex coordinates.");
            mesh.Positions.Add(point);
        }
        foreach (var triangle in shape.Triangles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (triangle.V1 >= vertices.Count || triangle.V2 >= vertices.Count || triangle.V3 >= vertices.Count)
                throw new InvalidDataException("The model contains an invalid triangle index.");
            mesh.TriangleIndices.Add(triangle.V1); mesh.TriangleIndices.Add(triangle.V2); mesh.TriangleIndices.Add(triangle.V3);
        }
        var normals = shape is BSTriShape bs ? bs.Normals : shape.GeometryData?.Normals;
        if (normals?.Count == vertices.Count)
            foreach (var normal in normals)
            {
                var direction = transform.Transform(new Vector3D(normal.X, normal.Y, normal.Z));
                if (!double.IsFinite(direction.LengthSquared) || direction.LengthSquared == 0) { mesh.Normals.Clear(); break; }
                direction.Normalize(); mesh.Normals.Add(direction);
            }
        var coordinates = shape is BSTriShape bsShape ? bsShape.UVs : shape.GeometryData?.UVSets;
        if (coordinates?.Count == vertices.Count)
            foreach (var uv in coordinates)
            {
                var u = uv.U * (shader?.UVScale.U ?? 1) + (shader?.UVOffset.U ?? 0);
                var v = uv.V * (shader?.UVScale.V ?? 1) + (shader?.UVOffset.V ?? 0);
                if (!float.IsFinite(u) || !float.IsFinite(v)) throw new InvalidDataException("The model contains invalid texture coordinates.");
                mesh.TextureCoordinates.Add(new Point(u, v));
            }
        return mesh;
    }

    private static Matrix3D ShapeTransform(NifFile nif, INiShape shape)
    {
        var transform = Transform(shape.Rotation, shape.Translation, shape.Scale);
        var visited = new HashSet<INiObject>(ReferenceEqualityComparer.Instance) { shape };
        for (var parent = nif.GetParentNode(shape); parent is not null; parent = nif.GetParentNode(parent))
        {
            if (!visited.Add(parent)) throw new InvalidDataException("The model contains a cycle in its node hierarchy.");
            transform.Append(Transform(parent.Rotation, parent.Translation, parent.Scale));
        }
        return transform;
    }

    private static Matrix3D Transform(Matrix33 rotation, System.Numerics.Vector3 translation, float scale) => new(
        rotation.M11 * scale, rotation.M21 * scale, rotation.M31 * scale, 0,
        rotation.M12 * scale, rotation.M22 * scale, rotation.M32 * scale, 0,
        rotation.M13 * scale, rotation.M23 * scale, rotation.M33 * scale, 0,
        translation.X, translation.Y, translation.Z, 1);

    private static string AssetPath(string path, string directory)
    {
        path = path.Trim().Replace('/', '\\');
        if (Path.IsPathRooted(path) || path.Split('\\').Contains(".."))
            throw new InvalidDataException("Model assets must use paths relative to the game Data folder.");
        return path.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase) ? path : directory + "\\" + path;
    }

    private static MemoryStream ReadAsset(IAssetProvider assets, string path, int byteLimit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relative = new DataRelativePath(path);
        if (assets.TryGetSize(relative, out var size) && size > byteLimit)
            throw new InvalidDataException($"{path} exceeds the preview file size limit.");
        if (!assets.TryGetStream(relative, out var source) || source is null) throw new FileNotFoundException($"Asset not found: {path}");
        using (source)
        {
            var result = new MemoryStream();
            try
            {
                var buffer = new byte[ReadBufferBytes];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = source.Read(buffer);
                    if (count == 0) break;
                    if (result.Length + count > byteLimit) throw new InvalidDataException($"{path} exceeds the preview file size limit.");
                    result.Write(buffer, 0, count);
                }
                cancellationToken.ThrowIfCancellationRequested();
                result.Position = 0;
                return result;
            }
            catch { result.Dispose(); throw; }
        }
    }

    private static BitmapSource ReadTexture(IAssetProvider assets, string path, bool useAlpha, ref long decodedBytes, CancellationToken cancellationToken)
    {
        using var stream = ReadAsset(assets, path, MaxTextureBytes, cancellationToken);
        var header = new DdsHeader(stream);
        if (header.Width == 0 || header.Height == 0 || (long)header.Width * header.Height > MaxTexturePixels || header.Depth > 1)
            throw new InvalidDataException("DDS dimensions exceed the preview limit.");
        stream.Position = 0;
        using var image = Pfimage.FromStream(stream, new PfimConfig(allocator: new TextureAllocator(MaxDecodedTextureBytes - decodedBytes)));
        cancellationToken.ThrowIfCancellationRequested();
        decodedBytes += (long)image.Stride * image.Height;
        if (decodedBytes > MaxDecodedTextureBytes) throw new InvalidDataException("The preview texture memory limit was reached.");
        var format = image.Format switch
        {
            ImageFormat.Rgba32 => PixelFormats.Bgra32,
            ImageFormat.Rgb24 => PixelFormats.Bgr24,
            ImageFormat.R5g6b5 => PixelFormats.Bgr565,
            ImageFormat.R5g5b5 => PixelFormats.Bgr555,
            ImageFormat.Rgb8 => PixelFormats.Gray8,
            _ => throw new NotSupportedException($"DDS pixel format {image.Format} is not supported by this preview.")
        };
        BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height, BitmapDpi, BitmapDpi, format, null, image.Data, image.Stride);
        if (!useAlpha && format == PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class TextureAllocator(long remainingBytes) : IImageAllocator
    {
        public byte[] Rent(int size)
        {
            if (size < 0 || size > remainingBytes) throw new InvalidDataException("The preview texture memory limit was reached.");
            remainingBytes -= size;
            return new byte[size];
        }
        public void Return(byte[] data) => remainingBytes += data.Length;
    }
}
