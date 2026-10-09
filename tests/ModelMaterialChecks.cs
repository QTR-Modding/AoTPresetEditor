using System.IO;
using System.Numerics;
using AoTPresetEditor;
using Mutagen.Bethesda.Assets.DI;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;
using GpuColor = HelixToolkit.Maths.Color4;

internal static class ModelMaterialChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AoTModelMaterials", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "meshes"));
        Directory.CreateDirectory(Path.Combine(folder, "textures"));
        try
        {
            WriteTexture(Path.Combine(folder, "textures", "diffuse.dds"), 0x204080C0);
            WriteTexture(Path.Combine(folder, "textures", "normal.dds"), 0x40FF8080);
            WriteTexture(Path.Combine(folder, "textures", "glow.dds"), 0xFFFFFFFF);
            var version = NiVersion.GetSSE();
            var nif = new NifFile(version);
            var root = new NiNode(); nif.AddBlock(root);
            var shape = new BSTriShape(version, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [new Triangle(0, 1, 2)], [new TexCoord(0, 0), new TexCoord(1, 0), new TexCoord(0, 1)],
                [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ]) { IsSkinned = false };
            shape.SetVertexColors([new Color4(.5f, .25f, 1, .5f), new Color4(1, 1, 1, 1), new Color4(1, 1, 1, 1)]);
            root.Children.AddBlockRef(nif.AddBlock(shape));
            var textures = new BSShaderTextureSet { Textures = [new NiString4("diffuse.dds"), new NiString4("normal.dds"), new NiString4("glow.dds")] };
            var shader = new BSLightingShaderProperty
            {
                Type = NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK,
                TextureSetRef = new(nif.AddBlock(textures)), Alpha = .5f,
                UVScale = new TexCoord(2, 3), UVOffset = new TexCoord(.25f, .5f),
                HasVertexColors = true, HasVertexAlpha = true, HasSpecular = true, HasGlowmap = true, Emissive = true,
                Glossiness = 32, SpecularStrength = .75f, SpecularColor = new Color3(1, .5f, .25f),
                EmissiveColor = new Color4(.2f, .3f, .4f, 1), EmissiveMultiple = 2
            };
            shape.ShaderPropertyRef = new(nif.AddBlock(shader));
            Require(nif.Save(Path.Combine(folder, "meshes", "material.nif")) == 0, "The material fixture must serialize as a NIF.");
            IAssetProvider assets = new GameAssets(folder, []);
            var result = ModelPreview.Load(new("Materials", [new("material.nif", [])]), assets, CancellationToken.None);
            var mesh = result.Meshes.Single();
            Require(mesh.Geometry.Positions?.Count == 3 && mesh.Geometry.TriangleIndices?.Count == 3, "The model must retain its triangle geometry.");
            Require(mesh.Material.DiffuseMap is null && mesh.Material.DiffuseAlphaMap is not null && mesh.VertexColors?.Count == 3,
                "Authored vertex colors must multiply the diffuse texture through the stock shader's post-color alpha map.");
            Require(mesh.Material.NormalMap is not null && mesh.Material.SpecularColorMap is not null && mesh.Material.EmissiveMap is not null,
                $"Normal, specular-alpha and glow textures must reach their corresponding GPU material inputs. Normal={mesh.Material.NormalMap is not null}, specular={mesh.Material.SpecularColorMap is not null}, glow={mesh.Material.EmissiveMap is not null}. {result.Details}");
            Require(mesh.Material.UVTransform.Scaling == new Vector2(2, 3) && mesh.Material.UVTransform.Translation == new Vector2(.25f, .5f)
                && mesh.Geometry.TextureCoordinates?[1] == Vector2.UnitX, "UV transforms must be applied once in the material and remain animatable.");
            var normal = mesh.Material.NormalMap!.TextureInfoLoader.Load(mesh.Material.NormalMap.Guid).TextureRaw;
            var specular = mesh.Material.SpecularColorMap!.TextureInfoLoader.Load(mesh.Material.SpecularColorMap.Guid).TextureRaw;
            Require(normal![0] == 128 && normal[1] == 128 && normal[2] == 255 && normal[3] == 255,
                "Tangent normal RGB must remain unchanged and its specular alpha must not become transparency.");
            Require(specular![0] == 64 && specular[1] == 64 && specular[2] == 64 && specular[3] == 255,
                "The normal texture's alpha must become an opaque grayscale specular mask.");
            ModelPreview.UpdateVertexTint(mesh, new GpuColor(0, 0, 0, 0));
            ModelPreview.UpdateVertexTint(mesh, new GpuColor(.2f, .4f, .6f, .8f));
            var color = mesh.Geometry.Colors![0];
            Require(Math.Abs(color.Red - mesh.VertexColors![0].Red * .2f) < .001f && Math.Abs(color.Alpha - mesh.VertexColors[0].Alpha * .8f) < .001f,
                "Animation must restore vertex tint and alpha from authored colors after a zero-opacity frame.");
            var duplicate = ModelPreview.Load(new("Repeated parts", [new("material.nif", []), new("material.nif", [])]), assets, CancellationToken.None);
            Require(ReferenceEquals(duplicate.Meshes[0].Material.NormalMap, duplicate.Meshes[1].Material.NormalMap),
                "Repeated armor parts must reuse their decoded textures within one preview's memory budget.");
            var skeleton = new NifFile(version);
            var skeletonRoot = new NiNode { Translation = new Vector3(1, 2, 3) }; skeleton.AddBlock(skeletonRoot);
            var attachment = new NiNode { Name = new("SHIELD"), Translation = new Vector3(10, 20, 30) };
            skeletonRoot.Children.AddBlockRef(skeleton.AddBlock(attachment));
            Require(skeleton.Save(Path.Combine(folder, "meshes", "skeleton.nif")) == 0, "The attachment fixture must serialize.");
            var attached = ModelPreview.Load(new("Attached", [new("material.nif", [], AttachmentNode: "SHIELD")],
                Appearance: new(null, null, "skeleton.nif")), assets, CancellationToken.None);
            Require(attached.Meshes.Single().Geometry.Positions![0] == new Vector3(11, 22, 33),
                "Rigid equipment must use its named skeleton attachment and all ancestor transforms.");
            shader.ShaderType_SK_FO4 = BSLightingShaderType.SkinTint;
            Require(nif.Save(Path.Combine(folder, "meshes", "skin.nif")) == 0, "The skin fixture must serialize.");
            var skin = ModelPreview.Load(new("Skin", [new("skin.nif", [])], Appearance: new(new Vector3(.2f, .4f, .6f), null)), assets, CancellationToken.None);
            Require(skin.Meshes.Single().Material.DiffuseColor.Red == .2f && skin.Meshes.Single().Material.DiffuseColor.Blue == .6f,
                "An NPC's skin tint must reach skin materials without tinting unrelated materials.");
            // Nifly writes M11,M21,M31 first; the serialized NIF rows encode a positive quarter-turn about Z.
            shape.Rotation = new Matrix33 { M11 = 0, M12 = 1, M21 = -1, M22 = 0 };
            Require(nif.Save(Path.Combine(folder, "meshes", "rotation.nif")) == 0, "The rotation fixture must serialize.");
            var rotated = ModelPreview.Load(new("Rotation", [new("rotation.nif", [])]), assets, CancellationToken.None);
            Require(rotated.Meshes.Single().Geometry.Positions![1] == Vector3.UnitY,
                "A serialized positive quarter-turn about Z must rotate positive X to positive Y.");
            const uint hiddenFlag = 1;
            root.Flags_ui |= hiddenFlag;
            Require(nif.Save(Path.Combine(folder, "meshes", "hidden.nif")) == 0, "The hidden-node fixture must serialize.");
            var hidden = ModelPreview.Load(new("Hidden", [new("hidden.nif", [])]), assets, CancellationToken.None);
            Require(!hidden.Meshes.Single().Visible, "Authored hidden geometry must remain available to animation without being shown initially.");
            try { ModelPreview.Load(new("Materials", [new("material.nif", [])]), assets, new CancellationToken(true)); throw new InvalidOperationException("Canceled model loading continued."); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void WriteTexture(string path, uint color)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x20534444u); writer.Write(124u); writer.Write(0x100Fu);
        writer.Write(1u); writer.Write(1u); writer.Write(4u); writer.Write(0u); writer.Write(1u);
        for (var i = 0; i < 11; i++) writer.Write(0u);
        writer.Write(32u); writer.Write(0x41u); writer.Write(0u); writer.Write(32u);
        foreach (var mask in new uint[] { 0xFF0000, 0xFF00, 0xFF, 0xFF000000 }) writer.Write(mask);
        writer.Write(0x1000u); for (var i = 0; i < 4; i++) writer.Write(0u);
        writer.Write(color);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
