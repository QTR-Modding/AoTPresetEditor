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
using HelixToolkit.SharpDX.Model;
using Gpu = HelixToolkit.SharpDX;
using Numerics = System.Numerics;
using GpuColor = HelixToolkit.Maths.Color4;

namespace AoTPresetEditor;

public sealed record ModelPreviewMesh(Gpu.MeshGeometry3D Geometry, PhongMaterialCore Material, bool DoubleSided, bool Transparent,
    IReadOnlyList<GpuColor>? VertexColors = null, bool Visible = true);
public sealed record ModelPreviewParticleMesh(ModelPreviewParticleSystem System, Gpu.TextureModel Texture);
public sealed record ModelPreviewResult(IReadOnlyList<ModelPreviewMesh> Meshes, string Details,
    ModelPreviewAnimation? Animation = null, Gpu.TextureModel? EnvironmentMap = null,
    IReadOnlyList<ModelPreviewParticleMesh>? Particles = null)
{
    public IReadOnlyList<ModelPreviewAnimationClip> Clips => (Animation?.Clips ?? [])
        .Concat(Particles?.SelectMany(particle => particle.System.Clips) ?? [])
        .GroupBy(clip => clip.Name).Select(group => group.First() with { Duration = group.Max(clip => clip.Duration) }).ToArray();
}

public static class ModelPreview
{
    private const int MaxModelBytes = 64 * 1024 * 1024;
    private const int MaxTextureBytes = 32 * 1024 * 1024;
    private const long MaxTexturePixels = 16 * 1024 * 1024;
    private const int MaxDecodedTextureBytes = 256 * 1024 * 1024;
    private const int MaxVertices = 1_000_000;
    private const int MaxTriangles = 1_000_000;
    private const int MaxDetailMessages = 6;
    private const int ReadBufferBytes = 64 * 1024;
    private const double BitmapDpi = 96;

    public static ModelPreviewResult Load(ModelPreviewSource source, IAssetProvider assets, CancellationToken cancellationToken)
        => LoadScene(source, assets, null, null, cancellationToken);

    public static ModelPreviewResult Load(ModelPreviewSource target, IAssetProvider targetAssets,
        ModelPreviewSource art, IAssetProvider artAssets, CancellationToken cancellationToken)
        => LoadScene(target, targetAssets, art, artAssets, cancellationToken);

    private static ModelPreviewResult LoadScene(ModelPreviewSource source, IAssetProvider assets,
        ModelPreviewSource? art, IAssetProvider? artAssets, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = new List<ModelPreviewMesh>();
        var particles = new List<ModelPreviewParticleMesh>();
        var messages = new HashSet<string>();
        var budget = new PreviewBudget();
        var animation = new ModelPreviewAnimation();
        var targetNodes = art is null ? null : new Dictionary<string, Matrix3D?>(StringComparer.OrdinalIgnoreCase);
        if (targetNodes is not null && source.Appearance?.SkeletonPath is { } skeleton)
        {
            try
            {
                using var stream = ReadAsset(assets, AssetPath(skeleton, "meshes"), MaxModelBytes, cancellationToken);
                var nif = new NifFile(stream);
                if (!nif.Valid) throw new InvalidDataException("The target's attachment skeleton could not be read.");
                AddAttachmentNodes(nif, Matrix3D.Identity, targetNodes, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
            { messages.Add($"{skeleton}: {error.Message}"); }
        }
        foreach (var part in source.Parts)
        {
            try { LoadPart(part, source.Appearance, assets, model, particles, budget, art is null ? animation : null, messages,
                cancellationToken, source.Appearance?.SkeletonPath is null ? targetNodes : null, includeParticles: art is null); }
            catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
            { messages.Add($"{part.Path}: {error.Message}"); }
        }
        if (art is not null)
        {
            if (model.Count == 0) throw new InvalidDataException("The selected item's model has no geometry to show. " + string.Join(" ", messages.Take(MaxDetailMessages)));
            var targetMeshes = model.Count;
            var targetParticles = particles.Count;
            foreach (var part in art.Parts)
            {
                try { LoadPart(part, art.Appearance, artAssets!, model, particles, budget, animation, messages, cancellationToken,
                    targetNodes, attachedEffect: true); }
                catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
                { messages.Add($"{part.Path}: {error.Message}"); }
            }
            if (model.Count == targetMeshes && particles.Count == targetParticles)
                throw new InvalidDataException("This art object cannot be previewed on the selected item. " + string.Join(" ", messages.Take(MaxDetailMessages)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (model.Count == 0 && particles.Count == 0)
            throw new InvalidDataException("No geometry could be shown. " + string.Join(" ", messages.Take(MaxDetailMessages)));
        var details = "Offline preview. Lighting and effects can differ from Skyrim.";
        if (source.Note.Length > 0) details += "\n" + source.Note;
        if (art is not null && art.Note.Length > 0) details += "\n" + art.Note;
        if (messages.Count > 0) details += "\n" + string.Join("\n", messages.Take(MaxDetailMessages));
        if (messages.Count > MaxDetailMessages) details += $"\n{messages.Count - MaxDetailMessages} more texture or material limitations.";
        return new(model, details, animation.Clips.Count > 0 ? animation : null, budget.EnvironmentMap, particles);
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
        public readonly Dictionary<(IAssetProvider Assets, string Path, bool Alpha, bool AlphaOnly, string? Detail, string? Tint), Gpu.TextureModel> Textures = [];
        public string? EnvironmentPath;
        public IAssetProvider? EnvironmentAssets;
        public Gpu.TextureModel? EnvironmentMap;
    }

    private static void LoadPart(ModelPreviewPart part, ModelPreviewAppearance? appearance, IAssetProvider assets,
        List<ModelPreviewMesh> model, List<ModelPreviewParticleMesh> particles, PreviewBudget budget, ModelPreviewAnimation? animation,
        HashSet<string> messages, CancellationToken cancellationToken, Dictionary<string, Matrix3D?>? targetNodes = null,
        bool attachedEffect = false, bool includeParticles = true)
    {
        using var stream = ReadAsset(assets, AssetPath(part.Path, "meshes"), MaxModelBytes, cancellationToken);
        var nif = new NifFile();
        if (nif.Load(stream) != 0 || !nif.Valid) throw new InvalidDataException("The model file could not be read as a supported NIF.");
        cancellationToken.ThrowIfCancellationRequested();
        var initialShapes = model.Count;
        var initialParticles = particles.Count;
        var bindings = new List<(INiShape Shape, int MeshIndex, Numerics.Matrix4x4 InitialWorld)>();
        var attachmentRoots = attachedEffect ? new Dictionary<INiShape, NiAVObject>() : null;
        var attachment = AttachmentTransform(part, appearance, assets, cancellationToken);
        if (targetNodes is not null && !attachedEffect) AddAttachmentNodes(nif, attachment, targetNodes, cancellationToken);
        var shapeIndex = 0;
        var usedReplacements = new HashSet<int>();
        foreach (var shape in SceneShapes(nif, cancellationToken))
        {
            var index = shapeIndex++;
            cancellationToken.ThrowIfCancellationRequested();
            if (part.ExcludedShapes?.Contains(shape.Name.String) == true) continue;
            NiAVObject? attachmentRoot = null;
            var shapeAttachment = attachedEffect ? EffectAttachment(nif, shape, targetNodes!, messages, cancellationToken, out attachmentRoot) : attachment;
            if (shapeAttachment is null) continue;
            if (attachmentRoot is not null) attachmentRoots!.Add(shape, attachmentRoot);
            if (attachedEffect && (shape.IsSkinned || shape.HasSkinInstance))
            { messages.Add("Skinned effect geometry cannot yet be attached to this item."); continue; }
            if (shape is NiParticleSystem system)
            {
                if (!includeParticles) continue;
                LoadParticles(nif, system, shapeAttachment.Value, assets, particles, budget, messages, cancellationToken, attachmentRoot);
                continue;
            }
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
            var mesh = BuildMesh(nif, shape, vertices, part, shapeAttachment.Value, cancellationToken);
            if (mesh.TriangleIndices.Count == 0) continue;
            var gpu = new Gpu.MeshGeometry3D
            {
                Positions = new(mesh.Positions.Select(p => new Numerics.Vector3((float)p.X, (float)p.Y, (float)p.Z))),
                Normals = new(mesh.Normals.Select(n => new Numerics.Vector3((float)n.X, (float)n.Y, (float)n.Z))),
                TextureCoordinates = new(mesh.TextureCoordinates.Select(uv => new Numerics.Vector2((float)uv.X, (float)uv.Y))),
                TriangleIndices = new(mesh.TriangleIndices)
            };
            if (gpu.Normals.Count != gpu.Positions.Count) gpu.UpdateNormals();
            gpu.UpdateBounds();
            var material = BuildMaterial(nif, shape, index, part, appearance, assets, budget, usedReplacements, messages, cancellationToken);
            IReadOnlyList<GpuColor>? vertexColors = null;
            if (shader is not null && (shader.HasVertexColors || shader.HasVertexAlpha))
            {
                var colors = shape is BSTriShape triangleShape ? triangleShape.VertexColors : shape.GeometryData?.VertexColors;
                if (colors?.Count == vertices.Count)
                {
                    vertexColors = colors.Select(c => new GpuColor(shader.HasVertexColors ? c.R : 1,
                        shader.HasVertexColors ? c.G : 1, shader.HasVertexColors ? c.B : 1, shader.HasVertexAlpha ? c.A : 1)).ToArray();
                    gpu.Colors = new(vertexColors.Select(c => c * material.DiffuseColor));
                    // Helix applies the alpha-map RGB after vertex-color selection; this preserves vertex color * texture.
                    material.DiffuseAlphaMap = material.DiffuseMap;
                    material.DiffuseMap = null;
                    material.VertexColorBlendingFactor = 1;
                }
            }
            var alpha = nif.GetBlock(shape.AlphaPropertyRef) ?? shape.Properties?.References
                .Select(x => nif.GetBlock<NiAlphaProperty>(x.Index)).FirstOrDefault(x => x is not null);
            var transparent = material.DiffuseColor.Alpha < 1 || shader?.HasVertexAlpha == true ||
                alpha is not null && (alpha.Flags.AlphaBlend || alpha.Flags.AlphaTest);
            var initialWorld = ShapeTransform(nif, shape);
            initialWorld.Append(shapeAttachment.Value);
            bindings.Add((shape, model.Count, shape.IsSkinned || shape.HasSkinInstance ? Numerics.Matrix4x4.Identity : ToNumerics(initialWorld)));
            model.Add(new(gpu, material, shader?.DoubleSided == true, transparent, vertexColors, ShapeVisible(nif, shape, attachmentRoot)));
        }
        if (part.AlternateTextures.Any(x => !usedReplacements.Contains(x.Index)))
            messages.Add("Some alternate textures target geometry that is not shown.");
        if (nif.HasUnknownBlocks) messages.Add("The NIF contains unsupported blocks.");
        if (model.Count == initialShapes && particles.Count == initialParticles && part.ExcludedPartitions is not { Count: > 0 } && part.ExcludedShapes is not { Count: > 0 })
            messages.Add($"{part.Path}: no supported triangle geometry.");
        animation?.AddPart(nif, bindings, messages, attachmentRoots);
    }

    private static void LoadParticles(NifFile nif, NiParticleSystem shape, Matrix3D attachment, IAssetProvider assets,
        List<ModelPreviewParticleMesh> particles, PreviewBudget budget, HashSet<string> messages, CancellationToken token,
        NiAVObject? attachmentRoot = null)
    {
        var world = ShapeTransform(nif, shape);
        world.Append(attachment);
        var system = ModelPreviewParticleSystem.Create(nif, shape, ToNumerics(world), messages, token, attachmentRoot);
        if (system is null) return;
        var shader = (BSEffectShaderProperty)nif.GetShader(shape)!;
        var path = shader.SourceTexture?.Content;
        if (string.IsNullOrWhiteSpace(path)) { messages.Add($"{system.Name}: the particle texture is missing."); return; }
        var texture = SurfaceTexture(assets, path, true, budget, messages, token);
        if (texture is null) return;
        const int verticesPerParticle = 4, trianglesPerParticle = 2;
        budget.Vertices = checked(budget.Vertices + system.MaximumParticleCount * verticesPerParticle);
        budget.Triangles = checked(budget.Triangles + system.MaximumParticleCount * trianglesPerParticle);
        if (budget.Vertices > MaxVertices || budget.Triangles > MaxTriangles)
            throw new InvalidDataException("This model exceeds the preview geometry limit.");
        particles.Add(new(system, texture));
    }

    private enum TextureSlot { Diffuse, Normal, Glow, Detail, Environment, EnvironmentMask, FaceTint, Specular }

    private static PhongMaterialCore BuildMaterial(NifFile nif, INiShape shape, int index, ModelPreviewPart part,
        ModelPreviewAppearance? appearance, IAssetProvider assets, PreviewBudget budget, HashSet<int> usedReplacements,
        HashSet<string> messages, CancellationToken token)
    {
        var material = new PhongMaterialCore
        {
            DiffuseColor = new GpuColor(1, 1, 1, 1), AmbientColor = new GpuColor(.2f, .2f, .2f, 1),
            SpecularColor = new GpuColor(0, 0, 0, 1), EmissiveColor = new GpuColor(0, 0, 0, 1), EnableAutoTangent = true
        };
        var shader = nif.GetShader(shape);
        var lighting = shader as BSLightingShaderProperty;
        var effect = shader as BSEffectShaderProperty;
        if (lighting is null && effect is null) { messages.Add("Unsupported materials are shown as neutral geometry."); return material; }
        material.UVTransform = new Gpu.UVTransform(0, shader!.UVScale.U, shader.UVScale.V, shader.UVOffset.U, shader.UVOffset.V);
        var slots = lighting is null ? [] : nif.GetBlock<BSShaderTextureSet>(lighting.TextureSetRef)?.Textures.Select(x => x.Content).ToArray() ?? [];
        string? Slot(TextureSlot slot) => slots.ElementAtOrDefault((int)slot);
        var diffuse = effect?.SourceTexture?.Content ?? Slot(TextureSlot.Diffuse);
        if (lighting?.IsTypeSkinTint == true && part.DiffusePath is not null) diffuse = part.DiffusePath;
        var replacements = part.AlternateTextures.Where(x => x.Index == index).ToArray();
        if (replacements.Length > 0 && lighting is not null)
        {
            usedReplacements.Add(index);
            if (!lighting.IsTypeSkinTint)
            {
                diffuse = replacements.Length == 1 ? replacements[0].DiffusePath : null;
                if (diffuse is null) messages.Add($"Alternate texture for shape {index} could not be resolved.");
            }
        }
        var alpha = nif.GetBlock(shape.AlphaPropertyRef) ?? shape.Properties?.References
            .Select(x => nif.GetBlock<NiAlphaProperty>(x.Index)).FirstOrDefault(x => x is not null);
        var textureAlpha = alpha is not null && (alpha.Flags.AlphaBlend || alpha.Flags.AlphaTest);
        if (alpha?.Flags.AlphaTest == true) messages.Add("Alpha testing is approximated with transparency.");
        if (alpha?.Flags.AlphaBlend == true) messages.Add("Transparency uses standard blending; Skyrim blend modes can differ.");
        var opacity = lighting?.Alpha ?? effect!.BaseColor.A;
        if (!float.IsFinite(opacity)) throw new InvalidDataException("The model contains an invalid material opacity.");
        material.DiffuseColor = new GpuColor(1, 1, 1, Math.Clamp(opacity, 0, 1));
        material.DiffuseMap = SurfaceTexture(assets, diffuse, textureAlpha, budget, messages, token,
            faceDetail: lighting?.IsTypeFaceTint == true ? Slot(TextureSlot.Detail) : null,
            faceTint: lighting?.IsTypeFaceTint == true ? Slot(TextureSlot.FaceTint) : null);
        if (lighting is not null)
        {
            Numerics.Vector3? tint = lighting.IsTypeSkinTint ? appearance?.SkinTint ?? new Numerics.Vector3(lighting.SkinTintColor.R, lighting.SkinTintColor.G, lighting.SkinTintColor.B) :
                lighting.IsTypeHairTint ? appearance?.HairTint ?? new Numerics.Vector3(lighting.HairTintColor.R, lighting.HairTintColor.G, lighting.HairTintColor.B) : null;
            if (tint is { } color) material.DiffuseColor = new GpuColor(color.X, color.Y, color.Z, material.DiffuseColor.Alpha);
            if (!lighting.ModelSpace) material.NormalMap = SurfaceTexture(assets, Slot(TextureSlot.Normal), false, budget, messages, token);
            else if (!string.IsNullOrWhiteSpace(Slot(TextureSlot.Normal))) messages.Add("Model-space normal maps are not yet reproduced.");
            if (lighting.HasSpecular)
            {
                var specular = lighting.SpecularColor;
                material.SpecularColor = new GpuColor(specular.R * lighting.SpecularStrength, specular.G * lighting.SpecularStrength,
                    specular.B * lighting.SpecularStrength, 1);
                material.SpecularShininess = lighting.Glossiness;
                material.SpecularColorMap = lighting.ModelSpace
                    ? SurfaceTexture(assets, Slot(TextureSlot.Specular), false, budget, messages, token)
                    : SurfaceTexture(assets, Slot(TextureSlot.Normal), true, budget, messages, token, alphaOnly: true);
            }
            if (lighting.Emissive || lighting.HasGlowmap)
            {
                var glow = lighting.EmissiveColor;
                material.EmissiveColor = new GpuColor(glow.R * lighting.EmissiveMultiple, glow.G * lighting.EmissiveMultiple,
                    glow.B * lighting.EmissiveMultiple, 1);
                if (lighting.HasGlowmap) material.EmissiveMap = SurfaceTexture(assets, Slot(TextureSlot.Glow), false, budget, messages, token);
            }
            if (lighting.HasEnvironmentMapping || lighting.HasEyeEnvironmentMapping)
            {
                var path = Slot(TextureSlot.Environment);
                if (!string.IsNullOrWhiteSpace(path) && EnvironmentTexture(assets, path, budget, messages, token))
                {
                    var strength = Math.Clamp(lighting.HasEyeEnvironmentMapping ? lighting.EyeCubemapScale : lighting.EnvironmentMapScale, 0, 1);
                    material.ReflectiveColor = new GpuColor(strength, strength, strength, 1);
                    material.RenderEnvironmentMap = true;
                    if (!string.IsNullOrWhiteSpace(Slot(TextureSlot.EnvironmentMask))) messages.Add("Reflection masks are not yet reproduced.");
                }
            }
        }
        else
        {
            var color = effect!.BaseColor;
            var scale = effect.BaseColorScale;
            material.EmissiveColor = new GpuColor(color.R * scale, color.G * scale, color.B * scale, 1);
            material.EmissiveMap = material.DiffuseMap;
            material.DiffuseColor = new GpuColor(0, 0, 0, material.DiffuseColor.Alpha);
            if (effect.HasGreyscaleToPaletteColor || effect.HasGreyscaleToPaletteAlpha) messages.Add("Effect palette colors are not yet reproduced.");
            if (effect.HasVertexColors) messages.Add("Vertex colors on glowing effect surfaces are approximate.");
        }
        var components = new[] { material.DiffuseColor.Red, material.DiffuseColor.Green, material.DiffuseColor.Blue,
            material.SpecularColor.Red, material.SpecularColor.Green, material.SpecularColor.Blue, material.SpecularShininess,
            material.EmissiveColor.Red, material.EmissiveColor.Green, material.EmissiveColor.Blue,
            material.ReflectiveColor.Red, material.UVTransform.Scaling.X, material.UVTransform.Scaling.Y,
            material.UVTransform.Translation.X, material.UVTransform.Translation.Y };
        if (components.Any(value => !float.IsFinite(value))) throw new InvalidDataException("The model contains invalid material values.");
        return material;
    }

    private static Gpu.TextureModel? SurfaceTexture(IAssetProvider assets, string? path, bool alpha, PreviewBudget budget,
        HashSet<string> messages, CancellationToken token, bool alphaOnly = false, string? faceDetail = null, string? faceTint = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = AssetPath(path, "textures");
            var key = (assets, path.ToUpperInvariant(), alpha, alphaOnly, faceDetail?.ToUpperInvariant(), faceTint?.ToUpperInvariant());
            if (budget.Textures.TryGetValue(key, out var cached)) return cached;
            var bitmap = ReadTexture(assets, path, alpha, ref budget.TextureBytes, token);
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            const int bytesPerPixel = 4;
            var stride = checked(bitmap.PixelWidth * bytesPerPixel);
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            var hasDetail = false;
            foreach (var maskPath in new[] { faceDetail, faceTint })
                if (!string.IsNullOrWhiteSpace(maskPath))
                {
                    try
                    {
                        OverlayFaceTexture(assets, maskPath, bitmap.PixelWidth, bitmap.PixelHeight, pixels, budget, token);
                        hasDetail |= maskPath == faceDetail;
                    }
                    catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
                    { messages.Add($"Face texture {maskPath}: {error.Message}"); }
                }
            if (hasDetail)
                for (var i = 0; i < pixels.Length; i += bytesPerPixel)
                    for (var channel = 0; channel < 3; channel++) pixels[i + channel] = (byte)Math.Min(pixels[i + channel] * 2, byte.MaxValue);
            if (alphaOnly)
                for (var i = 0; i < pixels.Length; i += bytesPerPixel)
                { pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3]; pixels[i + 3] = byte.MaxValue; }
            token.ThrowIfCancellationRequested();
            var texture = new Gpu.TextureModel(pixels, SharpDX.DXGI.Format.B8G8R8A8_UNorm, bitmap.PixelWidth, bitmap.PixelHeight);
            budget.Textures.Add(key, texture);
            return texture;
        }
        catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
        { messages.Add($"Texture {path}: {error.Message}"); return null; }
    }

    private static void OverlayFaceTexture(IAssetProvider assets, string path, int width, int height,
        byte[] pixels, PreviewBudget budget, CancellationToken token)
    {
        var mask = ReadTexture(assets, AssetPath(path, "textures"), false, ref budget.TextureBytes, token);
        var resized = new TransformedBitmap(mask, new ScaleTransform((double)width / mask.PixelWidth, (double)height / mask.PixelHeight));
        var maskRgba = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var maskPixels = new byte[pixels.Length];
        maskRgba.CopyPixels(maskPixels, stride, 0);
        for (var row = 0; row < height; row++)
        {
            token.ThrowIfCancellationRequested();
            for (var i = row * stride; i < (row + 1) * stride; i += bytesPerPixel)
                for (var channel = 0; channel < 3; channel++)
                {
                    var basis = pixels[i + channel] / (float)byte.MaxValue;
                    var blend = maskPixels[i + channel] / (float)byte.MaxValue;
                    var overlay = basis < .5f ? 2 * basis * blend : 1 - 2 * (1 - basis) * (1 - blend);
                    pixels[i + channel] = (byte)Math.Clamp(overlay * byte.MaxValue, 0, byte.MaxValue);
                }
        }
    }

    private static bool EnvironmentTexture(IAssetProvider assets, string path, PreviewBudget budget, HashSet<string> messages, CancellationToken token)
    {
        path = AssetPath(path, "textures");
        if (budget.EnvironmentPath is not null)
        {
            if (ReferenceEquals(budget.EnvironmentAssets, assets) && budget.EnvironmentPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
            messages.Add("Some surfaces use a different reflection map; their reflections are omitted."); return false;
        }
        try
        {
            var stream = ReadAsset(assets, path, MaxTextureBytes, token);
            try
            {
                if (!IsCubeTexture(stream)) throw new InvalidDataException("The reflection texture is not a cubemap.");
                var header = new DdsHeader(stream);
                budget.TextureBytes += checked((long)header.Width * header.Height * 6 * 4);
                if (budget.TextureBytes > MaxDecodedTextureBytes) throw new InvalidDataException("The preview texture memory limit was reached.");
                stream.Position = 0;
                budget.EnvironmentMap = new Gpu.TextureModel(stream, false);
                budget.EnvironmentPath = path;
                budget.EnvironmentAssets = assets;
                return true;
            }
            catch { stream.Dispose(); throw; }
        }
        catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
        { messages.Add($"Reflection texture {path}: {error.Message}"); return false; }
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
        ModelPreviewPart part, Matrix3D attachment, CancellationToken cancellationToken)
    {
        var transform = shape.IsSkinned || shape.HasSkinInstance ? Matrix3D.Identity : ShapeTransform(nif, shape);
        if (!shape.IsSkinned && !shape.HasSkinInstance) transform.Append(attachment);
        var mesh = new MeshGeometry3D();
        foreach (var vertex in vertices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = transform.Transform(new Point3D(vertex.X, vertex.Y, vertex.Z));
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
                throw new InvalidDataException("The model contains invalid vertex coordinates.");
            mesh.Positions.Add(point);
        }
        foreach (var triangle in VisibleTriangles(nif, shape, part.ExcludedPartitions))
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
                var u = uv.U;
                var v = uv.V;
                if (!float.IsFinite(u) || !float.IsFinite(v)) throw new InvalidDataException("The model contains invalid texture coordinates.");
                mesh.TextureCoordinates.Add(new Point(u, v));
            }
        if (shape.IsSkinned || shape.HasSkinInstance) ApplySkinning(nif, shape, vertices, normals, mesh, cancellationToken);
        return mesh;
    }

    private static IEnumerable<Triangle> VisibleTriangles(NifFile nif, INiShape shape, IReadOnlySet<int>? excluded)
    {
        if (excluded is null || excluded.Count == 0) return shape.Triangles;
        var skin = nif.GetBlock<BSDismemberSkinInstance>(shape.SkinInstanceRef);
        var partitions = nif.GetBlock(skin?.SkinPartition);
        if (skin is null || partitions is null) return shape.Triangles;
        if (skin.Partitions.Count != partitions.Partitions.Count) throw new InvalidDataException("The model's body partitions do not match its skin data.");
        var hidden = new HashSet<Triangle>();
        for (var i = 0; i < skin.Partitions.Count; i++)
        {
            if (!excluded.Contains((int)skin.Partitions[i].BodyPart)) continue;
            var partition = partitions.Partitions[i];
            if (partition.TrianglesCopy is null || partition.TrianglesCopy.Count == 0) partition.GenerateTrueTrianglesFromMappedTriangles();
            foreach (var entry in partition.TrianglesCopy ?? []) { var triangle = entry; triangle.Rotate(); hidden.Add(triangle); }
        }
        return shape.Triangles.Where(entry => { var triangle = entry; triangle.Rotate(); return !hidden.Contains(triangle); });
    }

    private static void AddAttachmentNodes(NifFile nif, Matrix3D attachment, Dictionary<string, Matrix3D?> nodes, CancellationToken token)
    {
        foreach (var node in nif.Blocks.OfType<NiNode>())
        {
            token.ThrowIfCancellationRequested();
            if (node.Name.String.Length == 0) continue;
            var world = NodeTransform(nif, node, token);
            world.Append(attachment);
            if (nodes.TryGetValue(node.Name.String, out var previous) && previous != world) nodes[node.Name.String] = null;
            else nodes[node.Name.String] = world;
        }
    }

    private static Matrix3D? EffectAttachment(NifFile nif, INiShape shape, IReadOnlyDictionary<string, Matrix3D?> nodes,
        HashSet<string> messages, CancellationToken token, out NiAVObject? attachmentRoot)
    {
        const string namedNode = "NamedNode&";
        attachmentRoot = null;
        var visited = new HashSet<NiAVObject>();
        for (var node = shape as NiAVObject; node is not null; node = nif.GetParentNode(node))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(node)) throw new InvalidDataException("The effect attachment hierarchy contains a cycle.");
            var data = node.ExtraDataList?.GetBlocks(nif).OfType<NiStringsExtraData>().FirstOrDefault(x => x.Name.String == "AttachT");
            if (data is null) continue;
            var instructions = data.Data?.Select(value => value.Content).ToArray() ?? [];
            if (instructions.Length == 1 && instructions[0].StartsWith(namedNode, StringComparison.Ordinal))
            {
                var name = instructions[0][namedNode.Length..];
                if (!nodes.TryGetValue(name, out var target) || target is null)
                { messages.Add($"The effect needs attachment node {name}, which is not available on this item."); return null; }
                var parent = nif.GetParentNode(node);
                var result = parent is null ? Matrix3D.Identity : NodeTransform(nif, parent, token);
                if (!result.HasInverse) throw new InvalidDataException("The effect attachment parent has zero scale.");
                result.Invert();
                result.Append(target.Value);
                attachmentRoot = node;
                return result;
            }
            messages.Add("This effect's attachment technique cannot yet be reproduced on the selected item.");
            return null;
        }
        return Matrix3D.Identity;
    }

    private static Matrix3D NodeTransform(NifFile nif, NiAVObject node, CancellationToken token)
    {
        var result = Matrix3D.Identity;
        var visited = new HashSet<NiAVObject>();
        for (; node is not null; node = nif.GetParentNode(node))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(node)) throw new InvalidDataException("The attachment node hierarchy contains a cycle.");
            result.Append(Transform(node.Rotation, node.Translation, node.Scale));
        }
        return result;
    }

    private static Matrix3D AttachmentTransform(ModelPreviewPart part, ModelPreviewAppearance? appearance, IAssetProvider assets, CancellationToken token)
    {
        if (part.AttachmentNode is null) return Matrix3D.Identity;
        if (appearance?.SkeletonPath is null) throw new InvalidDataException("The attachment has no skeleton model.");
        using var stream = ReadAsset(assets, AssetPath(appearance.SkeletonPath, "meshes"), MaxModelBytes, token);
        var nif = new NifFile(stream);
        var node = nif.Blocks.OfType<NiNode>().FirstOrDefault(x => x.Name.String.Equals(part.AttachmentNode, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Skeleton attachment {part.AttachmentNode} was not found.");
        var result = Matrix3D.Identity;
        var visited = new HashSet<INiObject>(ReferenceEqualityComparer.Instance);
        for (; node is not null; node = nif.GetParentNode(node))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(node)) throw new InvalidDataException("The attachment skeleton contains a cycle.");
            result.Append(Transform(node.Rotation, node.Translation, node.Scale));
        }
        return result;
    }

    public static void UpdateVertexTint(ModelPreviewMesh mesh, GpuColor tint)
    {
        if (mesh.VertexColors is null) return;
        mesh.Geometry.Colors = new(mesh.VertexColors.Select(color => color * tint));
        mesh.Geometry.UpdateColors();
    }

    private static Numerics.Matrix4x4 ToNumerics(Matrix3D matrix) => new(
        (float)matrix.M11, (float)matrix.M12, (float)matrix.M13, (float)matrix.M14,
        (float)matrix.M21, (float)matrix.M22, (float)matrix.M23, (float)matrix.M24,
        (float)matrix.M31, (float)matrix.M32, (float)matrix.M33, (float)matrix.M34,
        (float)matrix.OffsetX, (float)matrix.OffsetY, (float)matrix.OffsetZ, (float)matrix.M44);

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

    private static bool ShapeVisible(NifFile nif, INiShape shape, NiAVObject? attachmentRoot = null)
    {
        const uint hiddenFlag = 1;
        var visited = new HashSet<NiAVObject>();
        for (var node = shape as NiAVObject; node is not null; node = nif.GetParentNode(node))
        {
            if (!visited.Add(node)) throw new InvalidDataException("The model contains a cycle in its node hierarchy.");
            if ((node.Flags_ui & hiddenFlag) != 0) return false;
            if (ReferenceEquals(node, attachmentRoot)) break;
        }
        return true;
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
        if (path.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) path = path["data\\".Length..];
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
        return ReadTexture(stream, useAlpha, ref decodedBytes, cancellationToken);
    }

    private static BitmapSource ReadTexture(MemoryStream stream, bool useAlpha, ref long decodedBytes, CancellationToken cancellationToken)
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
