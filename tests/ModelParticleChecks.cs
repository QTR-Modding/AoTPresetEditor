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

        shape.Modifiers.AddBlockRef(nif.AddBlock(new NiPSysGravityModifier { Active = true, Name = new("Gravity") }));
        messages.Clear();
        Require(ModelPreviewParticleSystem.Create(nif, shape, Matrix4x4.Identity, messages) is null &&
            messages.Any(message => message.Contains(nameof(NiPSysGravityModifier))),
            "Unsupported forces must reject that system explicitly rather than inventing its motion.");
        var (largeNif, largeShape) = Fixture(3000);
        messages.Clear();
        Require(ModelPreviewParticleSystem.Create(largeNif, largeShape, Matrix4x4.Identity, messages) is null &&
            messages.Any(message => message.Contains("limit")), "The particle budget must reject an oversized emitter before playback.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { ModelPreviewParticleSystem.Create(largeNif, largeShape, Matrix4x4.Identity, messages, cancelled.Token); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Particle preparation must honor cancellation.");
    }

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

    private static NiBlockRef<T> Reference<T>(NifFile nif, T value) where T : INiObject => new(nif.AddBlock(value));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
