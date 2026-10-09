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

    public static ModelPreviewResult Load(ModelPreviewSource source, IAssetProvider assets, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = new Model3DGroup();
        var messages = new HashSet<string>();
        var budget = new PreviewBudget();
        foreach (var part in source.Parts)
        {
            try { LoadPart(part, assets, model, budget, messages, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
            { messages.Add($"{part.Path}: {error.Message}"); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (model.Children.Count == 0)
            throw new InvalidDataException("No geometry could be shown. " + string.Join(" ", messages.Take(MaxDetailMessages)));
        // Transparent surfaces must follow opaque geometry or their depth writes hide surfaces behind their clear pixels.
        var ordered = model.Children.OrderBy(HasTransparency).ToArray();
        model.Children.Clear();
        foreach (var geometry in ordered) model.Children.Add(geometry);
        model.Freeze();
        var details = "Static model and textures. Game lighting, animation, and live effects are not reproduced.";
        if (source.Note.Length > 0) details += "\n" + source.Note;
        if (messages.Count > 0) details += "\n" + string.Join("\n", messages.Take(MaxDetailMessages));
        if (messages.Count > MaxDetailMessages) details += $"\n{messages.Count - MaxDetailMessages} more texture or material limitations.";
        return new(model, details);
    }

    private static bool HasTransparency(Model3D model)
    {
        var brush = ((GeometryModel3D)model).Material switch
        {
            DiffuseMaterial diffuse => diffuse.Brush,
            EmissiveMaterial emissive => emissive.Brush,
            _ => null
        };
        return brush?.Opacity < 1 || brush is SolidColorBrush { Color.A: < byte.MaxValue } ||
            brush is ImageBrush { ImageSource: BitmapSource bitmap } && bitmap.Format == PixelFormats.Bgra32;
    }

    public static BitmapSource LoadTexture(IAssetProvider assets, string path, CancellationToken cancellationToken)
    {
        using var stream = ReadAsset(assets, AssetPath(path, "textures"), MaxTextureBytes, cancellationToken);
        if (IsCubeTexture(stream)) return ReadCubeTexture(stream, cancellationToken);
        var decodedBytes = 0L;
        return ReadTexture(stream, true, ref decodedBytes, cancellationToken);
    }

    private static bool IsCubeTexture(MemoryStream stream)
    {
        const uint cubeMap = 0x200, volume = 0x200000, dx10TextureCube = 0x4;
        stream.Position = 0;
        var header = new DdsHeader(stream);
        if (header.Width == 0 || header.Height == 0 || (long)header.Width * header.Height > MaxTexturePixels)
            throw new InvalidDataException("DDS dimensions exceed the preview limit.");
        if (header.Depth > 1 || (header.Caps2 & volume) != 0)
            throw new NotSupportedException("Volume textures cannot be shown as a flat image.");
        var cube = (header.Caps2 & cubeMap) != 0;
        if (header.PixelFormat.FourCC == CompressionAlgorithm.DX10)
        {
            var extended = new DdsHeaderDxt10(stream);
            if (extended.ArraySize != 1) throw new NotSupportedException("Texture arrays are not supported by this preview.");
            if (extended.ResourceDimension == D3D10ResourceDimension.D3D10_RESOURCE_DIMENSION_TEXTURE3D)
                throw new NotSupportedException("Volume textures cannot be shown as a flat image.");
            cube |= (extended.MiscFlag & dx10TextureCube) != 0;
        }
        stream.Position = 0;
        return cube;
    }

    private static BitmapSource ReadCubeTexture(MemoryStream stream, CancellationToken cancellationToken)
    {
        const int columns = 4, rows = 3, bytesPerPixel = 4;
        const uint firstFace = 0x400, allFaces = 0xFC00;
        var header = new DdsHeader(stream);
        if (header.PixelFormat.FourCC == CompressionAlgorithm.DX10) _ = new DdsHeaderDxt10(stream);
        var payloadStart = checked((int)stream.Position);
        var width = checked((int)header.Width); var height = checked((int)header.Height);
        if (width != height) throw new InvalidDataException("Cubemap faces must be square.");
        var crossPixels = (long)width * columns * height * rows;
        if (crossPixels > MaxTexturePixels) throw new InvalidDataException("This cubemap exceeds the preview image size limit.");
        var stride = checked(width * bytesPerPixel);
        var pixels = new byte[checked(stride * height)];
        var config = new PfimConfig(decompress: false,
            allocator: new TextureAllocator(MaxDecodedTextureBytes - crossPixels * bytesPerPixel - pixels.Length * 2L));
        stream.Position = 0;
        using var decoder = Dds.Create(stream, config);
        var faceBytes = checked((int)(stream.Position - payloadStart));
        var faces = decoder.Header10 is null ? header.Caps2 & allFaces : allFaces;
        if (faces == 0 || faceBytes <= 0) throw new InvalidDataException("The cubemap has no face data.");
        var faceCount = System.Numerics.BitOperations.PopCount(faces);
        if (payloadStart + (long)faceBytes * faceCount > stream.Length)
            throw new InvalidDataException("The cubemap is missing face data.");
        var cross = new WriteableBitmap(width * columns, height * rows, BitmapDpi, BitmapDpi, PixelFormats.Bgra32, null);
        (int X, int Y)[] positions = [(2, 1), (0, 1), (1, 0), (1, 2), (1, 1), (3, 1)];
        var offset = payloadStart;
        for (var face = 0; face < positions.Length; face++)
        {
            if ((faces & (firstFace << face)) == 0) continue;
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = offset;
            byte[]? data = null;
            Dds? uncompressed = null;
            try
            {
                if (decoder is CompressedDds compressed) data = compressed.DataDecode(stream, config);
                else
                {
                    // Pfim exposes per-face decoding only for compressed DDS. Replay its original header unchanged.
                    using var source = new MemoryStream(payloadStart + faceBytes);
                    source.Write(stream.GetBuffer(), 0, payloadStart);
                    source.Write(stream.GetBuffer(), offset, faceBytes);
                    source.Position = 0;
                    uncompressed = Dds.Create(source, config);
                    data = uncompressed.Data;
                }
                var image = uncompressed ?? decoder;
                var bitmap = BitmapSource.Create(width, height, BitmapDpi, BitmapDpi, TexturePixelFormat(image.Format), null, data, image.Stride);
                var rgba = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                rgba.CopyPixels(pixels, stride, 0);
                cross.WritePixels(new Int32Rect(positions[face].X * width, positions[face].Y * height, width, height), pixels, stride, 0);
            }
            finally
            {
                if (uncompressed is not null) uncompressed.Dispose();
                else if (data is not null) config.Allocator.Return(data);
            }
            offset += faceBytes;
        }
        cancellationToken.ThrowIfCancellationRequested();
        cross.Freeze();
        return cross;
    }

    private sealed class PreviewBudget
    {
        public int Vertices;
        public int Triangles;
        public long TextureBytes;
    }

    private static void LoadPart(ModelPreviewPart part, IAssetProvider assets, Model3DGroup model, PreviewBudget budget,
        HashSet<string> messages, CancellationToken cancellationToken)
    {
        using var stream = ReadAsset(assets, AssetPath(part.Path, "meshes"), MaxModelBytes, cancellationToken);
        var nif = new NifFile();
        if (nif.Load(stream) != 0 || !nif.Valid) throw new InvalidDataException("The model file could not be read as a supported NIF.");
        cancellationToken.ThrowIfCancellationRequested();
        var initialShapes = model.Children.Count;
        var shapeIndex = 0;
        var usedReplacements = new HashSet<int>();
        foreach (var shape in SceneShapes(nif, cancellationToken))
        {
            var index = shapeIndex++;
            cancellationToken.ThrowIfCancellationRequested();
            if (shape is NiParticleSystem) { messages.Add("Particle systems are omitted."); continue; }
            if (shape.IsSkinned || shape.HasSkinInstance)
            {
                var skin = nif.GetBlock<NiSkinInstance>(shape.SkinInstanceRef);
                if (skin is null || nif.GetBlock(skin.Data) is null) { messages.Add("Some skinned geometry has no usable bind transform and is omitted."); continue; }
                messages.Add("Skinned models are shown in their original unanimated pose.");
            }
            var vertices = shape is BSTriShape bs ? bs.VertexPositions : shape.GeometryData?.Vertices;
            if (vertices is null || vertices.Count == 0 || shape.Triangles.Count == 0) continue;
            budget.Vertices = checked(budget.Vertices + vertices.Count);
            budget.Triangles = checked(budget.Triangles + shape.Triangles.Count);
            if (budget.Vertices > MaxVertices || budget.Triangles > MaxTriangles)
                throw new InvalidDataException("This model exceeds the preview geometry limit.");
            var shader = nif.GetShader(shape);
            var lighting = shader as BSLightingShaderProperty;
            var effect = shader as BSEffectShaderProperty;
            var mesh = BuildMesh(nif, shape, vertices, shader, cancellationToken);
            Brush brush = new SolidColorBrush(Colors.LightGray);
            if (lighting is not null || effect is not null)
            {
                var replacements = part.AlternateTextures.Where(x => x.Index == index).ToList();
                string? texture = effect is not null ? effect.SourceTexture?.Content :
                    nif.GetBlock<BSShaderTextureSet>(lighting!.TextureSetRef)?.Textures.FirstOrDefault()?.Content;
                if (lighting?.IsTypeSkinTint == true && part.DiffusePath is not null) texture = part.DiffusePath;
                if (replacements.Count > 0 && lighting is not null && !lighting.IsTypeSkinTint)
                {
                    usedReplacements.Add(index);
                    if (replacements.Count != 1 || replacements[0].DiffusePath is null)
                    {
                        texture = null;
                        messages.Add($"Alternate texture for shape {index} could not be resolved; showing neutral geometry.");
                    }
                    else texture = replacements[0].DiffusePath;
                }
                else if (replacements.Count > 0 && lighting is not null) usedReplacements.Add(index);
                Color4? tint = null;
                if (effect is not null)
                {
                    tint = new Color4(effect.BaseColor.R * effect.BaseColorScale, effect.BaseColor.G * effect.BaseColorScale,
                        effect.BaseColor.B * effect.BaseColorScale, effect.BaseColor.A);
                    var color = tint.Value;
                    if (!float.IsFinite(color.R) || !float.IsFinite(color.G) || !float.IsFinite(color.B))
                        throw new InvalidDataException("The model contains an invalid material color.");
                    brush = new SolidColorBrush(Color.FromScRgb(1, Math.Clamp(color.R, 0, 1), Math.Clamp(color.G, 0, 1), Math.Clamp(color.B, 0, 1)));
                    messages.Add("Effect surfaces show their texture and color; particle motion and game shading are not reproduced.");
                    if (effect.HasGreyscaleToPaletteColor || effect.HasGreyscaleToPaletteAlpha)
                        messages.Add("Effect palette colors are not reproduced.");
                }
                var alpha = nif.GetBlock(shape.AlphaPropertyRef) ?? shape.Properties?.References
                    .Select(x => nif.GetBlock<NiAlphaProperty>(x.Index)).FirstOrDefault(x => x is not null);
                var textureAlpha = alpha is not null && (alpha.Flags.AlphaBlend || alpha.Flags.AlphaTest);
                if (alpha is not null && alpha.Flags.AlphaTest) messages.Add("Alpha testing is approximated with transparency.");
                if (alpha is not null && alpha.Flags.AlphaBlend) messages.Add("Transparency uses standard blending; Skyrim blend modes and sorting can differ.");
                if (!string.IsNullOrWhiteSpace(texture) && mesh.TextureCoordinates.Count == vertices.Count)
                {
                    try
                    {
                        var bitmap = ReadTexture(assets, AssetPath(texture, "textures"), textureAlpha, ref budget.TextureBytes, cancellationToken, tint);
                        brush = new ImageBrush(bitmap)
                        {
                            Viewport = new Rect(0, 0, 1, 1), ViewportUnits = BrushMappingMode.Absolute,
                            TileMode = TileMode.Tile, Stretch = Stretch.Fill
                        };
                    }
                    catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
                    {
                        messages.Add($"Texture {texture}: {error.Message} Showing geometry without that texture.");
                    }
                }
                else if (string.IsNullOrWhiteSpace(texture) && effect is null) messages.Add("Some shapes have no diffuse texture; showing neutral geometry.");
                else if (!string.IsNullOrWhiteSpace(texture)) messages.Add("Some shapes have no usable texture coordinates; showing neutral geometry.");
                var opacity = lighting?.Alpha ?? effect!.BaseColor.A;
                if (!float.IsFinite(opacity)) throw new InvalidDataException("The model contains an invalid material opacity.");
                brush.Opacity = Math.Clamp(opacity, 0, 1);
            }
            else messages.Add("Unsupported materials are shown as neutral geometry.");
            if (shape.HasVertexColors) messages.Add("Vertex colors are not simulated.");
            Material material = effect is not null ? new EmissiveMaterial(brush) : new DiffuseMaterial(brush);
            model.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = shader?.DoubleSided == true ? material : null });
        }
        if (part.AlternateTextures.Any(x => !usedReplacements.Contains(x.Index)))
            messages.Add("Some alternate textures target geometry that is not shown.");
        if (nif.HasUnknownBlocks) messages.Add("The NIF contains unsupported blocks.");
        if (model.Children.Count == initialShapes) messages.Add($"{part.Path}: no supported triangle geometry.");
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
        INiShader? shader, CancellationToken cancellationToken)
    {
        var transform = shape.IsSkinned || shape.HasSkinInstance ? Matrix3D.Identity : ShapeTransform(nif, shape);
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
        if (shape.IsSkinned || shape.HasSkinInstance) ApplySkinning(nif, shape, vertices, normals, mesh, cancellationToken);
        return mesh;
    }

    private static void ApplySkinning(NifFile nif, INiShape shape, List<System.Numerics.Vector3> vertices,
        List<System.Numerics.Vector3>? normals, MeshGeometry3D mesh, CancellationToken cancellationToken)
    {
        var instance = nif.GetBlock<NiSkinInstance>(shape.SkinInstanceRef);
        var data = nif.GetBlock(instance?.Data) ?? throw new InvalidDataException("The model has no usable skin data.");
        var bones = instance!.Bones.Indices.ToArray();
        if (bones.Length != data.BoneList.Count) throw new InvalidDataException("The model's skin bones and transforms do not match.");
        var transforms = new Matrix3D[bones.Length];
        for (var index = 0; index < bones.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bone = nif.GetBlock<NiNode>(bones[index]) ?? throw new InvalidDataException("A skin bone could not be found in the model.");
            var bind = data.BoneList[index].SkinTransform;
            var transform = Transform(bind.Rotation, bind.Translation, bind.Scale);
            var visited = new HashSet<INiObject>(ReferenceEqualityComparer.Instance);
            for (var node = bone; node is not null && nif.GetBlockIndex(node, out var nodeIndex) && nodeIndex != instance.SkeletonRoot.Index;
                node = nif.GetParentNode(node))
            {
                if (!visited.Add(node)) throw new InvalidDataException("The model contains a cycle in its skeleton.");
                transform.Append(Transform(node.Rotation, node.Translation, node.Scale));
            }
            transforms[index] = transform;
        }
        var positions = new Vector3D[vertices.Count];
        var directions = normals?.Count == vertices.Count ? new Vector3D[vertices.Count] : null;
        var totalWeights = new double[vertices.Count];
        foreach (var (vertex, bone, weight) in SkinWeights(nif, shape, instance, data))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (weight == 0) continue;
            if (vertex < 0 || vertex >= vertices.Count || bone < 0 || bone >= transforms.Length || !float.IsFinite(weight) || weight < 0)
                throw new InvalidDataException("The model contains an invalid skin weight.");
            var original = vertices[vertex];
            positions[vertex] += (Vector3D)transforms[bone].Transform(new Point3D(original.X, original.Y, original.Z)) * weight;
            if (directions is not null)
            {
                var normal = normals![vertex];
                directions[vertex] += transforms[bone].Transform(new Vector3D(normal.X, normal.Y, normal.Z)) * weight;
            }
            totalWeights[vertex] += weight;
        }
        for (var index = 0; index < vertices.Count; index++)
        {
            if (totalWeights[index] == 0) throw new InvalidDataException("The model contains vertices without usable skin weights.");
            var position = positions[index] / totalWeights[index];
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
                throw new InvalidDataException("The model contains invalid skinned coordinates.");
            mesh.Positions[index] = (Point3D)position;
        }
        mesh.Normals.Clear();
        if (directions is not null)
            foreach (var normal in directions)
            {
                var direction = normal;
                if (!double.IsFinite(direction.LengthSquared) || direction.LengthSquared == 0) { mesh.Normals.Clear(); break; }
                direction.Normalize(); mesh.Normals.Add(direction);
            }
    }

    private static IEnumerable<(int Vertex, int Bone, float Weight)> SkinWeights(NifFile nif, INiShape shape,
        NiSkinInstance instance, NiSkinData data)
    {
        if (shape is BSTriShape bs && bs.VertexDataSSE?.Count == shape.VertexCount)
        {
            for (var vertex = 0; vertex < bs.VertexDataSSE.Count; vertex++)
            {
                var entry = bs.VertexDataSSE[vertex];
                for (var influence = 0; influence < BoneWeights4.Length; influence++)
                    yield return (vertex, entry.BoneIndices[influence], (float)entry.BoneWeights[influence]);
            }
        }
        else if (shape is BSTriShape other && other.VertexData?.Count == shape.VertexCount)
        {
            for (var vertex = 0; vertex < other.VertexData.Count; vertex++)
            {
                var entry = other.VertexData[vertex];
                for (var influence = 0; influence < BoneWeights4.Length; influence++)
                    yield return (vertex, entry.BoneIndices[influence], (float)entry.BoneWeights[influence]);
            }
        }
        else if (nif.GetBlock(instance.SkinPartition) is { Partitions.Count: > 0 } partitions)
        {
            var visited = new HashSet<int>();
            foreach (var partition in partitions.Partitions)
                for (var local = 0; local < partition.NumVertices; local++)
                {
                    var vertex = partition.HasVertexMap.GetValueOrDefault() ? partition.VertexMap[local] : local;
                    if (!visited.Add(vertex)) continue;
                    for (var influence = 0; influence < partition.NumWeightsPerVertex; influence++)
                    {
                        var offset = checked(local * partition.NumWeightsPerVertex + influence);
                        yield return (vertex, partition.Bones[partition.BoneIndices[offset]], partition.VertexWeights[offset]);
                    }
                }
        }
        else
            for (var bone = 0; bone < data.BoneList.Count; bone++)
                foreach (var weight in data.BoneList[bone].VertexWeights) yield return (weight.Index, bone, weight.Weight);
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
        rotation.M11 * scale, rotation.M12 * scale, rotation.M13 * scale, 0,
        rotation.M21 * scale, rotation.M22 * scale, rotation.M23 * scale, 0,
        rotation.M31 * scale, rotation.M32 * scale, rotation.M33 * scale, 0,
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

    private static BitmapSource ReadTexture(IAssetProvider assets, string path, bool useAlpha, ref long decodedBytes, CancellationToken cancellationToken, Color4? tint = null)
    {
        using var stream = ReadAsset(assets, path, MaxTextureBytes, cancellationToken);
        return ReadTexture(stream, useAlpha, ref decodedBytes, cancellationToken, tint);
    }

    private static BitmapSource ReadTexture(MemoryStream stream, bool useAlpha, ref long decodedBytes, CancellationToken cancellationToken, Color4? tint = null)
    {
        if (IsCubeTexture(stream)) throw new NotSupportedException("A cubemap cannot be used as a flat model texture.");
        using var image = Pfimage.FromStream(stream, new PfimConfig(allocator: new TextureAllocator(MaxDecodedTextureBytes - decodedBytes)));
        cancellationToken.ThrowIfCancellationRequested();
        const int maximumBytesPerPixel = 4;
        decodedBytes += (long)image.Width * image.Height * maximumBytesPerPixel;
        if (decodedBytes > MaxDecodedTextureBytes) throw new InvalidDataException("The preview texture memory limit was reached.");
        var format = TexturePixelFormat(image.Format);
        BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height, BitmapDpi, BitmapDpi, format, null, image.Data, image.Stride);
        if (!useAlpha && format == PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
        if (tint is { } color)
        {
            var rgba = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            const int bytesPerPixel = 4;
            var stride = checked(image.Width * bytesPerPixel);
            var pixels = new byte[checked(stride * image.Height)];
            rgba.CopyPixels(pixels, stride, 0);
            for (var row = 0; row < image.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var offset = row * stride; offset < (row + 1) * stride; offset += bytesPerPixel)
                {
                    pixels[offset] = (byte)Math.Clamp(pixels[offset] * color.B, 0, byte.MaxValue);
                    pixels[offset + 1] = (byte)Math.Clamp(pixels[offset + 1] * color.G, 0, byte.MaxValue);
                    pixels[offset + 2] = (byte)Math.Clamp(pixels[offset + 2] * color.R, 0, byte.MaxValue);
                }
            }
            bitmap = BitmapSource.Create(image.Width, image.Height, BitmapDpi, BitmapDpi, PixelFormats.Bgra32, null, pixels, stride);
        }
        bitmap.Freeze();
        return bitmap;
    }

    private static PixelFormat TexturePixelFormat(ImageFormat format) => format switch
    {
        ImageFormat.Rgba32 => PixelFormats.Bgra32,
        ImageFormat.Rgb24 => PixelFormats.Bgr24,
        ImageFormat.R5g6b5 => PixelFormats.Bgr565,
        ImageFormat.R5g5b5 => PixelFormats.Bgr555,
        ImageFormat.Rgb8 => PixelFormats.Gray8,
        _ => throw new NotSupportedException($"DDS pixel format {format} is not supported by this preview.")
    };

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
