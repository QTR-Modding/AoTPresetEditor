using System.Numerics;
using AoTPresetEditor;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

internal static class ModelParticleChecks
{
    public static void Run()
    {
        var (nif, shape) = Fixture();
        var messages = new HashSet<string>();
        var system = ModelPreviewParticleSystem.Create(nif, shape, Matrix4x4.Identity, messages)
            ?? throw new InvalidOperationException(string.Join("; ", messages));
        Require(system.Clips.Single().Name == "Loop" && system.MaximumParticleCount == 11,
            "A particle clip must retain its name and reserve enough capacity for the authored birth rate and lifetime.");
        var first = system.Evaluate("Loop", .55);
        Require(first.Count == 5 && first.All(particle => particle.Color.W == .5f),
            "Particles must follow the authored birth rate and shader opacity.");
        Require(first.All(particle => Vector3.Distance(particle.Position, system.BoundsCenter) + particle.Radius <= system.BoundsRadius + .001f),
            "The emitter's framing bounds must contain live particles and their radius.");
        Require(system.Evaluate("Loop", .55).SequenceEqual(first), "Seeking to the same time must reproduce the same particle state.");
        Require(system.Evaluate("Loop", 2.55).Count == 10, "Particles born near the end of the preceding cycle must survive a loop boundary.");
        Require(system.Evaluate("Missing", 1).Count == 0, "An unrelated composite clip must not start another emitter's animation.");
        var (shortNif, shortShape) = Fixture(1);
        shortNif.Blocks.OfType<NiControllerSequence>().Single().StopTime = .1f;
        var shortLoop = ModelPreviewParticleSystem.Create(shortNif, shortShape, Matrix4x4.Identity, messages)!;
        Require(shortLoop.Evaluate("Loop", 1.5).Count == 1,
            "Fractional emissions must accumulate across short loops rather than disappearing at each cycle boundary.");

        var clip = nif.Blocks.OfType<NiControllerSequence>().Single();
        clip.CycleType = CycleType.CYCLE_REVERSE;
        system = ModelPreviewParticleSystem.Create(nif, shape, Matrix4x4.Identity, messages)!;
        Require(system.Evaluate("Loop", .55).SequenceEqual(system.Evaluate("Loop", 1.45)),
            "Reverse playback must revisit the same particle state at the reflected time.");
        clip.CycleType = CycleType.CYCLE_CLAMP;
        system = ModelPreviewParticleSystem.Create(nif, shape, Matrix4x4.Identity, messages)!;
        Require(system.Evaluate("Loop", 1).SequenceEqual(system.Evaluate("Loop", 8)), "Clamp clips must retain their terminal state.");

        var colors = new BSPSysSimpleColorModifier { Active = true, Name = new("Color"),
            FadeInPercent = .2f, FadeOutPercent = .8f, Color1EndPercent = 0, Color1StartPercent = .2f,
            Color2EndPercent = .8f, Color2StartPercent = 1, Colors = [new(1, 0, 0, 0), new(0, 1, 0, 1), new(0, 0, 1, 0)] };
        shape.Modifiers.AddBlockRef(nif.AddBlock(colors));
        system = ModelPreviewParticleSystem.Create(nif, shape, Matrix4x4.Identity, messages)!;
        var colored = system.Evaluate("Loop", .55);
        Require(colored.Any(particle => particle.Color.Y > .9f && particle.Color.X < .1f && particle.Color.W == .5f),
            "The particle color envelope must reach the middle authored color independently of its alpha envelope.");
        using var textureStream = new System.IO.MemoryStream();
        var billboards = new ModelPreviewParticleBillboards(new HelixToolkit.SharpDX.TextureModel(textureStream));
        billboards.Update(colored);
        billboards.DrawTexture(null!);
        Require(billboards.BillboardVertices.Count == colored.Count &&
            Math.Abs(billboards.BillboardVertices[0].Foreground.Alpha - colored[0].Color.W) < .0001f,
            "Particle opacity must reach the actual billboard GPU vertices.");

        WorldSpaceBirths();
        Forces();
        AnimatedForces();
        var gravity = new NiPSysGravityModifier { Active = true, Name = new("Gravity"), Decay = 1 };
        shape.Modifiers.AddBlockRef(nif.AddBlock(gravity));
        messages.Clear();
        Require(ModelPreviewParticleSystem.Create(nif, shape, Matrix4x4.Identity, messages) is null &&
            messages.Any(message => message.Contains("zero decay")),
            "Gravity decay must remain explicit until its native falloff is supported.");
        var (largeNif, largeShape) = Fixture(3000);
        messages.Clear();
        Require(ModelPreviewParticleSystem.Create(largeNif, largeShape, Matrix4x4.Identity, messages) is null &&
            messages.Any(message => message.Contains("limit")), "The particle budget must reject an oversized emitter before playback.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { ModelPreviewParticleSystem.Create(largeNif, largeShape, Matrix4x4.Identity, messages, cancelled.Token); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Particle preparation must honor cancellation.");
    }

    private static void WorldSpaceBirths()
    {
        var (nif, shape) = Fixture();
        var source = nif.Blocks.OfType<NiNode>().Single();
        var parent = new NiNode { Name = new("Moving root"), Scale = 1,
            Children = new([Reference<NiAVObject>(nif, shape), Reference<NiAVObject>(nif, source)]) };
        nif.AddBlock(parent);
        AddMotion(nif, parent, new NiTransformData { Translations = new()
        {
            Interpolation = KeyType.LINEAR_KEY, Keys = [new() { Time = 0, Value = Vector3.Zero }, new() { Time = 1, Value = new(10, 0, 0) }]
        } }, 1);
        shape.WorldSpace = true;
        var world = Prepare(nif, shape).Evaluate("Loop", .55);
        Require(Math.Abs(world.First().Position.X - 1) < .001f && Math.Abs(world.Last().Position.X - 5) < .001f,
            "World-space particles must retain the emitter position at birth while newer particles use its animated position.");
        shape.WorldSpace = false;
        Require(Prepare(nif, shape).Evaluate("Loop", .55).All(particle => Math.Abs(particle.Position.X - 5.5f) < .001f),
            "Local-space particles must follow their system's current transform.");
        shape.WorldSpace = true; shape.Scale = 3; source.Scale = 2;
        // The independent emitter's relative scale changes placement, but cannot multiply velocity or base radius.
        parent.Children.Clear();
        var scaled = Prepare(nif, shape, Matrix4x4.CreateScale(3)).Evaluate("Loop", .55).First();
        Require(Math.Abs(scaled.Position.Z - 1.35f) < .001f && Math.Abs(scaled.Radius - 1.5f) < .001f,
            "World-space velocity and base radius must retain system scale once, independently of emitter scale.");
    }

    private static void Forces()
    {
        var (nif, shape) = Fixture();
        var emitter = nif.Blocks.OfType<NiPSysBoxEmitter>().Single();
        emitter.Speed = 0;
        var node = new NiNode { Name = new("Gravity node"), Scale = 1 };
        nif.AddBlock(node);
        var gravity = new NiPSysGravityModifier { Name = new("Gravity"), Active = true,
            GravityObject = Pointer<NiAVObject>(nif, node), GravityAxis = Vector3.UnitY, Strength = 10,
            ForceType = ForceType.FORCE_PLANAR, WorldAligned = true };
        shape.Modifiers.AddBlockRef(nif.AddBlock(gravity));
        var system = Prepare(nif, shape);
        var first = system.Evaluate("Loop", .55).First();
        Require(first.Position.Y > 1.6f && first.Position.Y < 1.7f,
            "Planar gravity must apply the native 1.6 acceleration factor before the semi-implicit position update.");
        gravity.GravityAxis = Vector3.Zero;
        Require(Prepare(nif, shape).Evaluate("Loop", .55).All(particle => particle.Position == Vector3.Zero),
            "A zero gravity axis must produce no directional acceleration.");
        gravity.GravityAxis = Vector3.UnitY * 1e-7f; node.Scale = 1000;
        Require(Prepare(nif, shape).Evaluate("Loop", .55).All(particle => particle.Position == Vector3.Zero),
            "The native small-axis threshold must be applied before irrelevant force-node scale can magnify it.");
        gravity.GravityAxis = Vector3.UnitY; node.Scale = 1;
        shape.WorldSpace = true; shape.Scale = .08f;
        var small = Prepare(nif, shape, Matrix4x4.CreateScale(.08f)).Evaluate("Loop", .55).First();
        Require(Vector3.Distance(small.Position, first.Position * .08f) < .0001f,
            "World-space gravity must retain uniform system scale instead of treating the system as full identity.");
        shape.Scale = 1; emitter.Speed = 10; gravity.Active = false;
        var drag = new NiPSysDragModifier { Name = new("Drag"), Active = true, DragObject = Pointer<NiAVObject>(nif, node),
            DragAxis = Vector3.UnitZ, Percentage = .02f, Range = float.MaxValue, RangeFalloff = float.MaxValue };
        shape.Modifiers.AddBlockRef(nif.AddBlock(drag));
        Require(Prepare(nif, shape).Evaluate("Loop", .55).First().Position.Z is > 3.8f and < 4.1f,
            "Drag must attenuate only the projected velocity with the native linear per-update rate.");
        drag.Range = 1; drag.RangeFalloff = 2;
        nif.Blocks.OfType<NiNode>().First().Translation = new(0, 0, 10);
        Require(Math.Abs(Prepare(nif, shape).Evaluate("Loop", .55).First().Position.Z - 14.5f) < .001f,
            "Particles beyond the drag falloff must retain their velocity.");

        emitter.Speed = 0; gravity.Active = true; drag.Active = false; gravity.GravityAxis = Vector3.UnitX;
        var parent = new NiNode { Name = new("Rotated root"), Scale = 1,
            Rotation = new() { M11 = 0, M12 = 1, M21 = -1, M22 = 0, M33 = 1 },
            Children = new([Reference<NiAVObject>(nif, shape), Reference<NiAVObject>(nif, node),
                Reference<NiAVObject>(nif, nif.Blocks.OfType<NiNode>().First())]) };
        nif.AddBlock(parent);
        var initial = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        var aligned = Prepare(nif, shape, initial).Evaluate("Loop", .55).First();
        gravity.WorldAligned = false;
        var relative = Prepare(nif, shape, initial).Evaluate("Loop", .55).First();
        Require(aligned.Position.X > 1.6f && Math.Abs(aligned.Position.Y) < .001f &&
            relative.Position.Y > 1.6f && Math.Abs(relative.Position.X) < .001f,
            "World-aligned gravity uses the force node's local rotation; ordinary gravity uses its system-relative world rotation.");
    }

    private static void AnimatedForces()
    {
        var (nif, shape) = Fixture();
        var emitter = nif.Blocks.OfType<NiPSysBoxEmitter>().Single();
        emitter.Speed = 0; emitter.LifeSpan = .2f;
        var node = new NiNode { Name = new("Animated wind"), Scale = 1 };
        nif.AddBlock(node);
        var gravity = new NiPSysGravityModifier { Name = new("Wind"), Active = true, GravityObject = Pointer<NiAVObject>(nif, node),
            GravityAxis = Vector3.UnitX, Strength = 10, ForceType = ForceType.FORCE_PLANAR, WorldAligned = true,
            Turbulence = 1, TurbulenceScale = 0 };
        shape.Modifiers.AddBlockRef(nif.AddBlock(gravity));
        AddMotion(nif, node, new NiTransformData { QuaternionKeys =
            [new() { Time = 0, Value = Quaternion.Identity }, new() { Time = 4, Value = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) }],
            RotationType = KeyType.LINEAR_KEY }, 4);
        var system = Prepare(nif, shape);
        var early = system.Evaluate("Loop", .55).First(); var later = system.Evaluate("Loop", 2.55); var late = later.First();
        Require(late.Position.Y > early.Position.Y * 2 && late.Position.X < early.Position.X,
            "Force-node controllers must keep their own absolute playback period across shorter emitter loops.");
        Require(system.Evaluate("Loop", 2.55).SequenceEqual(system.Evaluate("Loop", 2.55)),
            "Seeking must reproduce animated-force trajectories exactly.");
        for (var time = .1; time < 20; time += .1)
            Require(system.Evaluate("Loop", time).All(particle =>
                Vector3.Distance(particle.Position, system.BoundsCenter) + particle.Radius <= system.BoundsRadius + .001f),
                "Framing must contain force trajectories after independent controller cycles drift apart.");

        gravity.TurbulenceScale = .5f;
        system = Prepare(nif, shape);
        var noisy = system.Evaluate("Loop", 2.55);
        Require(noisy.SequenceEqual(system.Evaluate("Loop", 2.55)) && !noisy.SequenceEqual(later),
            "The disclosed turbulence approximation must evolve deterministically rather than silently omitting turbulence.");
        gravity.TurbulenceScale = 0;
        node.Controller = Reference<NiTimeController>(nif, new NiPSysModifierActiveCtlr
        {
            Target = Pointer<NiObjectNET>(nif, shape), ModifierName = new("Wind"), StartTime = 0, StopTime = 1, Frequency = 1,
            Flags = new() { Active = true, CycleType = CycleType.CYCLE_CLAMP },
            Interpolator = Reference<NiInterpolator>(nif, new NiBoolTimelineInterpolator { Data = Reference(nif, new NiBoolData
            { Data = new() { Interpolation = KeyType.CONST_KEY, Keys = [new() { Time = 0, Value = 0 }, new() { Time = .5f, Value = 1 }] } } ) })
        });
        system = Prepare(nif, shape);
        var gated = system.Evaluate("Loop", .55).First();
        Require(gated.Position.Length() < early.Position.Length() / 2 && gated.Position.Length() > 0,
            "Modifier activation tracks must gate force updates throughout a particle's lifetime.");
        Require(system.Evaluate("Loop", 2.55).SequenceEqual(later),
            "A clamped activation track must leave later-loop forces active.");
    }

    private static ModelPreviewParticleSystem Prepare(NifFile nif, NiParticleSystem shape, Matrix4x4? initial = null)
    {
        var messages = new HashSet<string>();
        return ModelPreviewParticleSystem.Create(nif, shape, initial ?? Matrix4x4.Identity, messages)
            ?? throw new InvalidOperationException(string.Join("; ", messages));
    }

    private static void AddMotion(NifFile nif, NiAVObject node, NiTransformData data, float stop)
    {
        node.Controller = Reference<NiTimeController>(nif, new NiTransformController
        {
            Target = Pointer<NiObjectNET>(nif, node), StartTime = 0, StopTime = stop, Frequency = 1,
            Flags = new() { Active = true, CycleType = CycleType.CYCLE_CLAMP },
            Interpolator = Reference<NiInterpolator>(nif, new NiTransformInterpolator
            {
                Transform = new() { Translation = new(-float.MaxValue), Rotation = new(-float.MaxValue, -float.MaxValue, -float.MaxValue, -float.MaxValue), Scale = -float.MaxValue },
                Data = Reference(nif, data)
            })
        });
    }

    private static NiBlockPtr<T> Pointer<T>(NifFile nif, NiObjectNET node) where T : NiObjectNET =>
        new(nif.GetBlockIndex(node, out var index) ? index : throw new InvalidOperationException());

    private static (NifFile, NiParticleSystem) Fixture(float rate = 10)
    {
        var nif = new NifFile(NiVersion.GetSSE());
        var shader = new BSEffectShaderProperty { BaseColor = new(1, 1, 1, .5f), BaseColorScale = 1,
            UVScale = new() { U = 1, V = 1 } };
        var shape = new NiParticleSystem { Name = new("Cloud"), Scale = 1, WorldSpace = false,
            ShaderPropertyRef = Reference<BSShaderProperty>(nif, shader) };
        nif.AddBlock(shape);
        var source = new NiNode { Name = new("Emitter node"), Scale = 1 };
        nif.AddBlock(source);
        var emitter = new NiPSysBoxEmitter { Name = new("Emitter"), Active = true, LifeSpan = 1,
            InitialRadius = .5f, InitialColor = new(1, 1, 1, 1), Speed = 1,
            EmitterObject = new(nif.GetBlockIndex(source, out var sourceIndex) ? sourceIndex : throw new InvalidOperationException()) };
        shape.Modifiers = new([Reference<NiPSysModifier>(nif, emitter),
            Reference<NiPSysModifier>(nif, new NiPSysAgeDeathModifier { Active = true }),
            Reference<NiPSysModifier>(nif, new NiPSysPositionModifier { Active = true })]);
        var controller = new NiPSysEmitterCtlr { Target = new(nif.GetBlockIndex(shape, out var index) ? index : throw new InvalidOperationException()),
            ModifierName = new("Emitter") };
        var controllerRef = Reference<NiTimeController>(nif, controller);
        nif.AddBlock(new NiControllerSequence { Name = new("Loop"), StartTime = 0, StopTime = 1, Frequency = 1,
            CycleType = CycleType.CYCLE_LOOP,
            ControlledBlocks = [new() { NodeName = new("Cloud"), Controller = controllerRef,
                InterpolatorID = new("BirthRate"), Interpolator = Reference<NiInterpolator>(nif, new NiFloatInterpolator { Value = rate }) },
                new() { NodeName = new("Cloud"), Controller = controllerRef, InterpolatorID = new("EmitterActive"),
                    Interpolator = Reference<NiInterpolator>(nif, new NiBoolInterpolator { Value = true }) }] });
        return (nif, shape);
    }

    private static NiBlockRef<T> Reference<T>(NifFile nif, T value) where T : INiObject =>
        new(nif.GetBlockIndex(value, out var index) ? index : nif.AddBlock(value));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
