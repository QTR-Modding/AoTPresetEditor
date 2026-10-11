using System.IO;
using System.Numerics;
using AoTPresetEditor;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

internal static class ArtEffectPreviewChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AoTArtEffectPreview", Guid.NewGuid().ToString("N"));
        var targetFolder = Path.Combine(folder, "target");
        var artFolder = Path.Combine(folder, "art");
        foreach (var root in new[] { targetFolder, artFolder })
        { Directory.CreateDirectory(Path.Combine(root, "meshes")); Directory.CreateDirectory(Path.Combine(root, "textures")); }
        try
        {
            WriteTexture(Path.Combine(targetFolder, "textures", "shared.dds"), 0xFFFF0000);
            WriteTexture(Path.Combine(artFolder, "textures", "shared.dds"), 0xFF00FF00);
            var (target, targetRoot, _) = Model("Target", 10);
            var socket = new NiNode { Name = new("Socket"), Translation = new(5, 0, 0), Scale = 1 };
            targetRoot.Children.AddBlockRef(target.AddBlock(socket));
            Animate(target, targetRoot, "Target animation", 10, 20);
            Save(target, targetFolder);

            var (art, artRoot, artShape) = Model("Art", 100);
            var branch = new NiNode { Name = new("Effect"), Translation = new(2, 0, 0), Scale = 1, ExtraDataList = new() };
            branch.ExtraDataList.AddBlockRef(art.AddBlock(new NiStringsExtraData { Name = new("AttachT"), Data = [new("NamedNode&Socket")] }));
            artRoot.Children.Clear();
            branch.Children.AddBlockRef(art.GetBlockIndex(artShape, out var index) ? index : throw new InvalidOperationException());
            artRoot.Children.AddBlockRef(art.AddBlock(branch));
            Animate(art, artRoot, "Detached parent animation", 100, 200);
            Animate(art, branch, "Effect animation", 2, 4);
            const uint hiddenFlag = 1;
            artRoot.Flags_ui |= hiddenFlag;
            Save(art, artFolder);
            var targetAssets = new GameAssets(targetFolder, []);
            var artAssets = new GameAssets(artFolder, []);
            var targetSource = new ModelPreviewSource("Target", [new("Target.nif", [])]);
            var artSource = new ModelPreviewSource("Art", [new("Art.nif", [])]);
            var composed = ModelPreview.Load(targetSource, targetAssets, artSource, artAssets, CancellationToken.None);
            Require(composed.Meshes.Count == 2 && composed.Meshes[0].Geometry.Positions![0] == new Vector3(10, 0, 0) &&
                composed.Meshes[1].Geometry.Positions![0] == new Vector3(17, 0, 0),
                "Named art attachments must use the target node and preserve the effect's local transform and authored scale.");
            Require(composed.Clips.Count == 1 && composed.Clips[0].Name == "Effect animation", "Target clips must not replace the art object's animation. " +
                string.Join(", ", composed.Clips.Select(clip => clip.Name)) + " " + composed.Details);
            Require(composed.Meshes[1].Visible, "A named attachment must not inherit visibility from its detached original parent.");
            var frame = composed.Animation!.Evaluate(0, 1).Single();
            Require(frame.MeshIndex == 1 && Vector3.Distance(Vector3.Transform(new(17, 0, 0), frame.Transform), new(18, 0, 0)) < .001f,
                "Art animation must visibly move only the appended effect mesh, without shifting the target or losing its attachment. " + frame);
            var targetTexture = composed.Meshes[0].Material.DiffuseMap!.TextureInfoLoader.Load(composed.Meshes[0].Material.DiffuseMap!.Guid).TextureRaw;
            var artTexture = composed.Meshes[1].Material.DiffuseMap!.TextureInfoLoader.Load(composed.Meshes[1].Material.DiffuseMap!.Guid).TextureRaw;
            Require(targetTexture![2] == 255 && targetTexture[1] == 0 && artTexture![1] == 255 && artTexture[2] == 0,
                "Same-named textures from distinct target and art asset providers must retain their own pixels.");

            branch.ExtraDataList.Clear();
            artRoot.Flags_ui &= ~hiddenFlag;
            Save(art, artFolder);
            var atOrigin = ModelPreview.Load(targetSource, targetAssets, artSource, artAssets, CancellationToken.None);
            Require(atOrigin.Meshes[1].Geometry.Positions![0] == new Vector3(102, 0, 0),
                "Effects without named attachments must retain their authored coordinates at the item's model origin.");
            var noSkeleton = ModelPreview.Load(targetSource with { Appearance = new(null, null, "Missing skeleton.nif") }, targetAssets,
                artSource, artAssets, CancellationToken.None);
            Require(noSkeleton.Meshes.Count == 2 && noSkeleton.Details.Contains("Missing skeleton.nif"),
                "An unavailable attachment skeleton must not prevent an effect that needs only the item's origin, and must be reported.");

            branch.ExtraDataList.AddBlockRef(art.AddBlock(new NiStringsExtraData { Name = new("AttachT"), Data = [new("NamedNode&Missing")] }));
            Save(art, artFolder);
            var atRoot = ModelPreview.Load(targetSource, targetAssets, artSource, artAssets, CancellationToken.None);
            Require(atRoot.Meshes[1].Geometry.Positions![0] == new Vector3(12, 0, 0),
                "A missing named node must use the target's actual root transform and preserve the attached branch's local transform.");
            frame = atRoot.Animation!.Evaluate(0, 1).Single();
            Require(atRoot.Clips.Count == 1 && Vector3.Distance(Vector3.Transform(new(12, 0, 0), frame.Transform), new(13, 0, 0)) < .001f,
                "Root fallback must retain the attached branch's animation and exclude its detached original parent's animation.");
            var (skeleton, skeletonRoot, _) = Model("Skeleton", 30);
            skeletonRoot.Scale = 2;
            Save(skeleton, targetFolder);
            var onActor = ModelPreview.Load(targetSource with { Appearance = new(null, null, "Skeleton.nif") }, targetAssets,
                artSource, artAssets, CancellationToken.None);
            Require(onActor.Meshes[1].Geometry.Positions![0] == new Vector3(34, 0, 0) &&
                onActor.Meshes[1].Geometry.Positions![1] == new Vector3(36, 0, 0),
                "An actor's missing named node must use the skeleton root, including its authored translation and scale.");
            var attachment = branch.ExtraDataList.GetBlocks(art).OfType<NiStringsExtraData>().Single();
            attachment.Data = [new("NamedNode&Target shape")];
            Save(art, artFolder);
            Unavailable(() => ModelPreview.Load(targetSource, targetAssets, artSource, artAssets, CancellationToken.None), "not a node");
            targetRoot.Children.AddBlockRef(target.AddBlock(new NiNode { Name = new("Socket"), Translation = new(9, 0, 0), Scale = 1 }));
            Save(target, targetFolder);
            attachment.Data = [new("NamedNode&Socket")];
            Save(art, artFolder);
            Unavailable(() => ModelPreview.Load(targetSource, targetAssets, artSource, artAssets, CancellationToken.None), "ambiguous");
            branch.ExtraDataList.Clear();
            branch.ExtraDataList.AddBlockRef(art.AddBlock(new NiStringsExtraData { Name = new("AttachT"), Data = [new("HavokGeometry")] }));
            Save(art, artFolder);
            Unavailable(() => ModelPreview.Load(targetSource, targetAssets, artSource, artAssets, CancellationToken.None), "attachment technique");
            Require(ModelPreview.Load(artSource, artAssets, CancellationToken.None).Meshes.Count == 1,
                "Standalone art inspection must remain available when its target attachment technique is unsupported.");
            try { ModelPreview.Load(targetSource, targetAssets, artSource, artAssets, new CancellationToken(true)); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(folder, true); }
        ParticleAttachmentBoundary();
    }

    private static void ParticleAttachmentBoundary()
    {
        var nif = new NifFile(NiVersion.GetSSE());
        var root = new NiNode { Name = new("Original parent"), Translation = new(100, 0, 0), Scale = 1 }; nif.AddBlock(root);
        var branch = new NiNode { Name = new("Attached branch"), Translation = new(2, 0, 0), Scale = 1 };
        root.Children.AddBlockRef(nif.AddBlock(branch));
        foreach (var (node, start, stop) in new[] { (root, 100f, 200f), (branch, 2f, 4f) })
        {
            Require(nif.GetBlockIndex(node, out var nodeIndex), "The particle branch must belong to its NIF.");
            node.Controller = new(nif.AddBlock(new NiTransformController
            {
                Target = new(nodeIndex), StartTime = 0, StopTime = 1, Frequency = 1,
                Flags = new() { Active = true, CycleType = CycleType.CYCLE_CLAMP },
                Interpolator = TransformInterpolator(nif, start, stop, 1)
            }));
        }
        var particle = new NiParticleSystem { Name = new("Particles"), Scale = 1, WorldSpace = false,
            ShaderPropertyRef = new(nif.AddBlock(new BSEffectShaderProperty { BaseColor = new(1, 1, 1, 1), BaseColorScale = 1,
                UVScale = new(1, 1) })) };
        var particleIndex = nif.AddBlock(particle); branch.Children.AddBlockRef(particleIndex);
        Require(nif.GetBlockIndex(branch, out var branchIndex), "The particle emitter must belong to its NIF.");
        particle.Modifiers = new([new(nif.AddBlock(new NiPSysBoxEmitter { Name = new("Emitter"), Active = true,
            LifeSpan = 1, InitialRadius = .5f, InitialColor = new(1, 1, 1, 1), EmitterObject = new(branchIndex) }))]);
        var controllerIndex = nif.AddBlock(new NiPSysEmitterCtlr { Target = new(particleIndex), ModifierName = new("Emitter") });
        nif.AddBlock(new NiControllerSequence { Name = new("Loop"), StartTime = 0, StopTime = 1, Frequency = 1,
            CycleType = CycleType.CYCLE_LOOP, ControlledBlocks =
            [new() { Controller = new(controllerIndex), InterpolatorID = new("BirthRate"), Interpolator = new(nif.AddBlock(new NiFloatInterpolator { Value = 10 })) },
                new() { Controller = new(controllerIndex), InterpolatorID = new("EmitterActive"), Interpolator = new(nif.AddBlock(new NiBoolInterpolator { Value = true })) }] });
        var messages = new HashSet<string>();
        var system = ModelPreviewParticleSystem.Create(nif, particle, Matrix4x4.CreateTranslation(17, 0, 0), messages,
            attachmentRoot: branch) ?? throw new InvalidOperationException(string.Join(" ", messages));
        var frame = system.Evaluate("Loop", .55);
        Require(frame.Count > 0 && frame.All(p => Math.Abs(p.Position.X - 18.1f) < .001f),
            "Attached particles must preserve their own branch motion and ignore animation above the detached attachment boundary. " +
            string.Join(", ", frame.Select(p => p.Position.X)));
    }

    private static (NifFile Nif, NiNode Root, BSTriShape Shape) Model(string name, float offset)
    {
        var version = NiVersion.GetSSE();
        var nif = new NifFile(version);
        var root = new NiNode { Name = new(name), Translation = new(offset, 0, 0), Scale = 1 }; nif.AddBlock(root);
        var shape = new BSTriShape(version, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [new Triangle(0, 1, 2)],
            [new TexCoord(0, 0), new TexCoord(1, 0), new TexCoord(0, 1)], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ])
        { Name = new(name + " shape"), IsSkinned = false, Scale = 1 };
        root.Children.AddBlockRef(nif.AddBlock(shape));
        shape.ShaderPropertyRef = new(nif.AddBlock(new BSLightingShaderProperty
        {
            Type = NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK,
            TextureSetRef = new(nif.AddBlock(new BSShaderTextureSet { Textures = [new("shared.dds")] })), Alpha = 1
        }));
        return (nif, root, shape);
    }

    private static void Animate(NifFile nif, NiNode node, string name, float start, float stop)
    {
        Require(nif.GetBlockIndex(node, out var nodeIndex), "The controlled attachment node must belong to the model.");
        var controller = new NiTransformController { Target = new(nodeIndex), Flags = new() { Active = true, ManagerControlled = true } };
        var sequence = new NiControllerSequence
        {
            Name = new(name), NumControlledBlocks = 1, StartTime = 0, StopTime = 2, Frequency = 1, CycleType = CycleType.CYCLE_LOOP,
            ControlledBlocks = [new() { Controller = new(nif.AddBlock(controller)), Interpolator = TransformInterpolator(nif, start, stop, 2) }]
        };
        var manager = new NiControllerManager { ControllerSequences = new() };
        manager.ControllerSequences.AddBlockRef(nif.AddBlock(sequence));
        node.Controller = new(nif.AddBlock(manager));
    }

    private static NiBlockRef<NiInterpolator> TransformInterpolator(NifFile nif, float start, float stop, float duration)
        => new(nif.AddBlock(new NiTransformInterpolator
        {
            Transform = new() { Translation = new(-float.MaxValue), Rotation = new(-float.MaxValue, -float.MaxValue, -float.MaxValue, -float.MaxValue), Scale = -float.MaxValue },
            Data = new(nif.AddBlock(new NiTransformData { Translations = new() { NumKeys = 2, Interpolation = KeyType.LINEAR_KEY,
                Keys = [new() { Time = 0, Value = new(start, 0, 0) }, new() { Time = duration, Value = new(stop, 0, 0) }] } }))
        }));

    private static void Save(NifFile nif, string folder) => Require(nif.Save(Path.Combine(folder, "meshes", nif.Blocks.OfType<NiNode>().First().Name.String + ".nif")) == 0, "The art composition fixture must serialize.");

    private static void Unavailable(Func<ModelPreviewResult> load, string explanation)
    {
        try { load(); throw new InvalidOperationException("An unsupported attachment was silently placed at the item origin."); }
        catch (InvalidDataException error) { Require(error.Message.Contains(explanation, StringComparison.Ordinal), "The unavailable preview must identify its attachment limitation."); }
    }

    private static void WriteTexture(string path, uint color)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x20534444u); writer.Write(124u); writer.Write(0x100Fu); writer.Write(1u); writer.Write(1u);
        writer.Write(4u); writer.Write(0u); writer.Write(0u);
        for (var index = 0; index < 11; index++) writer.Write(0u);
        writer.Write(32u); writer.Write(0x41u); writer.Write(0u); writer.Write(32u);
        writer.Write(0x00FF0000u); writer.Write(0x0000FF00u); writer.Write(0x000000FFu); writer.Write(0xFF000000u);
        writer.Write(0x1000u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(color);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
