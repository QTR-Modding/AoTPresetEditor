using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

namespace AoTPresetEditor;

public sealed record ModelPreviewParticle(Vector3 Position, float Radius, float Rotation, Vector4 Color, Vector4 UVRect);

public sealed class ModelPreviewParticleSystem
{
    private const int ParticleLimit = 2048;
    private const double SampleStep = 1.0 / 120;
    private const double MaximumClipSeconds = 120;
    private readonly NifFile nif;
    private readonly NiParticleSystem shape;
    private readonly NiPSysVolumeEmitter emitter;
    private readonly BSPSysSimpleColorModifier? colors;
    private readonly BSPSysScaleModifier? scale;
    private readonly NiPSysRotationModifier? rotation;
    private readonly Matrix4x4 emitterWorld;
    private readonly float worldScale;
    private readonly BSEffectShaderProperty shader;
    private readonly Vector4[] textureRegions;
    private readonly List<Clip> clips = [];
    private readonly ModelPreviewAnimation animation = new();

    public string Name => shape.Name?.ToString() ?? "Particles";
    public IReadOnlyList<ModelPreviewAnimationClip> Clips { get; private set; } = [];
    public Vector3 BoundsCenter { get; private set; }
    public float BoundsRadius { get; private set; }
    public int MaximumParticleCount { get; private set; }

    private sealed record Birth(double Time, float Life, Vector3 Position, Vector3 Velocity, float Radius,
        float Rotation, float RotationSpeed, Vector4 Region);
    private sealed record EmissionSample(double Time, double Total);
    private sealed record Clip(ModelPreviewAnimationClip Description, ModelPreviewAnimation.Timing Timing,
        List<Action<Parameters, float>> Tracks, List<EmissionSample> Emission, float MaximumLife);
    private sealed class Parameters
    {
        public float Rate;
        public float Life;
        public float Radius;
        public float Speed;
        public float Declination;
        public float PlanarAngle;
        public bool Active;
    }

    private ModelPreviewParticleSystem(NifFile nif, NiParticleSystem shape, NiPSysVolumeEmitter emitter,
        Matrix4x4 initialWorld, BSEffectShaderProperty shader)
    {
        this.nif = nif; this.shape = shape; this.emitter = emitter; this.shader = shader;
        if (new[] { emitter.LifeSpanVariation, emitter.SpeedVariation, emitter.RadiusVariation, emitter.DeclinationVariation,
                emitter.PlanarAngleVariation }.Any(value => !float.IsFinite(value)))
            throw new NotSupportedException("Particle parameter variations must be finite.");
        var modifiers = shape.Modifiers.GetBlocks(nif).Where(modifier => modifier.Active == true).ToArray();
        colors = modifiers.OfType<BSPSysSimpleColorModifier>().SingleOrDefault();
        scale = modifiers.OfType<BSPSysScaleModifier>().SingleOrDefault();
        rotation = modifiers.OfType<NiPSysRotationModifier>().SingleOrDefault();
        var localWorld = WorldTransform(nif, shape);
        if (!Matrix4x4.Invert(localWorld, out var inverse)) throw new NotSupportedException("The particle system has zero scale.");
        var attachment = inverse * initialWorld;
        var sourceNode = nif.GetBlock(emitter.EmitterObject) as NiAVObject
            ?? throw new NotSupportedException("The emitter node is missing.");
        emitterWorld = WorldTransform(nif, sourceNode) * attachment;
        worldScale = new Vector3(initialWorld.M11, initialWorld.M12, initialWorld.M13).Length();
        var data = nif.GetBlock(shape.Data_NiBlockRef_NPSD) as NiPSysData;
        textureRegions = data?.SubtextureOffsets?.ToArray() ?? [];
        if (textureRegions.Length == 0) textureRegions = [new(0, 1, 0, 1)];
        BoundsCenter = Vector3.Transform(Vector3.Zero, emitterWorld);
    }

    public static ModelPreviewParticleSystem? Create(NifFile nif, NiParticleSystem shape, Matrix4x4 initialWorld,
        ISet<string> messages, CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var modifiers = shape.Modifiers.GetBlocks(nif).ToArray();
            if (shape.WorldSpace == true) throw new NotSupportedException("World-space particle motion is not simulated.");
            var emitters = modifiers.OfType<NiPSysEmitter>().ToArray();
            if (emitters.Length != 1 || emitters[0] is not (NiPSysBoxEmitter or NiPSysSphereEmitter or NiPSysCylinderEmitter))
                throw new NotSupportedException("Only a single box, sphere or cylinder emitter can be previewed.");
            foreach (var modifier in modifiers.Where(modifier => modifier.Active == true))
                if (modifier is not (NiPSysAgeDeathModifier or BSPSysLODModifier or NiPSysBoxEmitter or
                    NiPSysSphereEmitter or NiPSysCylinderEmitter or NiPSysSpawnModifier or BSPSysSimpleColorModifier or
                    BSPSysScaleModifier or NiPSysRotationModifier or NiPSysPositionModifier or NiPSysBoundUpdateModifier))
                    throw new NotSupportedException($"{modifier.GetType().Name} is not simulated.");
            if (modifiers.OfType<NiPSysAgeDeathModifier>().Any(modifier => modifier.SpawnonDeath == true) ||
                modifiers.OfType<NiPSysSpawnModifier>().Any(modifier => modifier.NumSpawnGenerations != 0))
                throw new NotSupportedException("Spawning particles from other particles is not simulated.");
            if (nif.GetShader(shape) is not BSEffectShaderProperty shader)
                throw new NotSupportedException("This particle material is not supported.");
            if (shader.HasGreyscaleToPaletteColor || shader.HasGreyscaleToPaletteAlpha)
                throw new NotSupportedException("Particle palette lookup is not supported.");
            if (modifiers.OfType<BSPSysSimpleColorModifier>().Any(color => color.Colors?.Length != 3))
                throw new NotSupportedException("The particle color modifier needs three colors.");
            var result = new ModelPreviewParticleSystem(nif, shape, (NiPSysVolumeEmitter)emitters[0], initialWorld, shader);
            result.ValidateEmitterMotion();
            result.animation.AddPart(nif, [(shape, 0, initialWorld)], messages);
            result.BuildClips(token);
            if (result.clips.Count == 0) throw new NotSupportedException("The emitter has no supported birth-rate animation.");
            result.Clips = result.clips.Select(clip => clip.Description).ToArray();
            if (modifiers.Any(modifier => modifier is BSPSysLODModifier))
                messages.Add("Particle previews use full detail; distance-based particle reduction is not applied.");
            return result;
        }
        catch (NotSupportedException error)
        {
            messages.Add($"{shape.Name}: {error.Message}");
            return null;
        }
    }

    private void BuildClips(CancellationToken token)
    {
        foreach (var sequence in nif.Blocks.OfType<NiControllerSequence>())
        {
            var tracks = new List<Action<Parameters, float>>();
            foreach (var block in sequence.ControlledBlocks)
            {
                if (nif.GetBlock(block.Controller) is not NiPSysModifierCtlr controller ||
                    !ReferenceEquals(nif.GetBlock(controller.Target), shape)) continue;
                AddTrack(controller, nif.GetBlock(block.Interpolator) as NiInterpolator,
                    block.InterpolatorID?.ToString(), tracks);
            }
            if (tracks.Count == 0) continue;
            var timing = new ModelPreviewAnimation.Timing(sequence.StartTime, sequence.StopTime, sequence.Frequency, 0,
                sequence.CycleType, sequence.PlayBackwards.GetValueOrDefault());
            AddClip(sequence.Name?.ToString() ?? "Animation", timing, tracks, token);
        }
        if (clips.Count != 0) return;
        var direct = new List<Action<Parameters, float>>();
        var stop = 0f;
        foreach (var controller in nif.Blocks.OfType<NiPSysModifierCtlr>())
        {
            if (!ReferenceEquals(nif.GetBlock(controller.Target), shape) || !controller.Flags.Active || controller.Flags.ManagerControlled) continue;
            var tracks = new List<Action<Parameters, float>>();
            AddTrack(controller, nif.GetBlock(controller.Interpolator) as NiInterpolator, "BirthRate", tracks);
            if (controller is NiPSysEmitterCtlr birth)
                AddTrack(controller, nif.GetBlock(birth.VisibilityInterpolator) as NiInterpolator, "EmitterActive", tracks);
            var timing = new ModelPreviewAnimation.Timing(controller.StartTime, controller.StopTime, controller.Frequency,
                controller.Phase, controller.Flags.CycleType, controller.Flags.PlayBackwards);
            direct.Add((values, time) => { foreach (var track in tracks) track(values, timing.At(time)); });
            stop = Math.Max(stop, controller.StopTime);
        }
        if (direct.Count > 0) AddClip("Animation", new(0, stop, 1, 0, CycleType.CYCLE_LOOP, false), direct, token);
    }

    private void AddTrack(NiPSysModifierCtlr controller, NiInterpolator? interpolator, string? id,
        List<Action<Parameters, float>> tracks)
    {
        if (controller.ModifierName?.ToString() != emitter.Name?.ToString())
            throw new NotSupportedException($"Animated modifier {controller.ModifierName} is not supported.");
        if (interpolator is null) throw new NotSupportedException("An emitter interpolator is missing.");
        if (controller is NiPSysModifierActiveCtlr || controller is NiPSysEmitterCtlr && id == "EmitterActive")
        {
            var sample = ModelPreviewAnimation.BoolSampler(nif, interpolator);
            tracks.Add((values, time) => values.Active = sample(time));
            return;
        }
        var number = ModelPreviewAnimation.FloatSampler(nif, interpolator);
        Action<Parameters, float> assign = controller switch
        {
            NiPSysEmitterCtlr => (values, value) => values.Rate = value,
            NiPSysEmitterLifeSpanCtlr => (values, value) => values.Life = value,
            NiPSysEmitterInitialRadiusCtlr => (values, value) => values.Radius = value,
            NiPSysEmitterSpeedCtlr => (values, value) => values.Speed = value,
            NiPSysEmitterDeclinationCtlr => (values, value) => values.Declination = value,
            NiPSysEmitterPlanarAngleCtlr => (values, value) => values.PlanarAngle = value,
            _ => throw new NotSupportedException($"{controller.GetType().Name} is not simulated.")
        };
        tracks.Add((values, time) => assign(values, number(time)));
    }

    private Parameters Defaults() => new() { Life = emitter.LifeSpan, Radius = emitter.InitialRadius,
        Speed = emitter.Speed, Declination = emitter.Declination, PlanarAngle = emitter.PlanarAngle, Active = emitter.Active == true };

    private void AddClip(string name, ModelPreviewAnimation.Timing timing, List<Action<Parameters, float>> tracks, CancellationToken token)
    {
        if (!double.IsFinite(timing.Duration) || timing.Duration <= 0 || timing.Duration > MaximumClipSeconds || timing.Frequency <= 0)
            throw new NotSupportedException($"Particle clip {name} has an unsupported duration or frequency.");
        var emission = new List<EmissionSample> { new(0, 0) };
        var integratedBirths = 0.0;
        var compensation = 0.0;
        var maxLife = 0f; var maxSpeed = 0f; var maxRadius = 0f; var maxRate = 0f;
        for (var start = 0.0; start < timing.Duration; start += SampleStep)
        {
            token.ThrowIfCancellationRequested();
            var end = Math.Min(start + SampleStep, timing.Duration);
            var values = Defaults();
            foreach (var track in tracks) track(values, timing.At((start + end) / 2, fromStart: true));
            if (!float.IsFinite(values.Rate) || !float.IsFinite(values.Life) || !float.IsFinite(values.Radius) ||
                !float.IsFinite(values.Speed) || !float.IsFinite(values.Declination) || !float.IsFinite(values.PlanarAngle) ||
                values.Rate < 0 || values.Life <= 0 || values.Radius < 0)
                throw new NotSupportedException("Particle parameters must be finite with a positive lifespan.");
            if (values.Life + Math.Abs(emitter.LifeSpanVariation) > MaximumClipSeconds)
                throw new NotSupportedException("The particle lifespan exceeds the preview limit.");
            maxLife = Math.Max(maxLife, values.Life + Math.Abs(emitter.LifeSpanVariation));
            maxSpeed = Math.Max(maxSpeed, Math.Abs(values.Speed) + Math.Abs(emitter.SpeedVariation));
            maxRadius = Math.Max(maxRadius, values.Radius + Math.Abs(emitter.RadiusVariation));
            maxRate = Math.Max(maxRate, values.Rate);
            var amount = values.Active ? values.Rate * (end - start) : 0;
            var increment = amount - compensation;
            var total = integratedBirths + increment;
            compensation = (total - integratedBirths) - increment;
            integratedBirths = total;
            emission.Add(new(end, integratedBirths));
        }
        var capacity = Math.Ceiling(maxRate * maxLife) + 1;
        if (capacity > ParticleLimit) throw new NotSupportedException($"The emitter exceeds the {ParticleLimit}-particle preview limit.");
        MaximumParticleCount = Math.Max(MaximumParticleCount, (int)capacity);
        var placementRadius = emitter switch
        {
            NiPSysBoxEmitter box => new Vector3(box.Width, box.Height, box.Depth).Length() / 2,
            NiPSysSphereEmitter sphere => sphere.Radius,
            NiPSysCylinderEmitter cylinder => MathF.Sqrt(cylinder.Radius * cylinder.Radius + cylinder.Height * cylinder.Height / 4),
            _ => 0
        };
        var largestScale = Math.Max(1, scale?.Scales?.DefaultIfEmpty(1).Max() ?? 1);
        var localRadius = (placementRadius + maxSpeed * maxLife + maxRadius * largestScale) * worldScale;
        BoundsRadius = Math.Max(BoundsRadius, localRadius);
        clips.Add(new(new(name, timing.Duration, timing.Cycle), timing, tracks, emission, maxLife));
        var animationIndex = animation.Clips.ToList().FindIndex(item => item.Name == name);
        if (animationIndex >= 0)
        {
            for (var time = 0.0; time <= timing.Duration; time += SampleStep)
            {
                token.ThrowIfCancellationRequested();
                var frame = animation.Evaluate(animationIndex, time).Single();
                var radiusScale = new Vector3(frame.Transform.M11, frame.Transform.M12, frame.Transform.M13).Length();
                BoundsRadius = Math.Max(BoundsRadius,
                    Vector3.Distance(BoundsCenter, Vector3.Transform(BoundsCenter, frame.Transform)) + localRadius * radiusScale);
            }
        }
    }

    private Birth CreateBirth(double time, Parameters values, Random random)
    {
        var position = emitter switch
        {
            NiPSysBoxEmitter box => new Vector3(Symmetric(random) * box.Width / 2, Symmetric(random) * box.Height / 2, Symmetric(random) * box.Depth / 2),
            NiPSysSphereEmitter sphere => SpherePosition(random) * sphere.Radius,
            NiPSysCylinderEmitter cylinder => CylinderPosition(random, cylinder.Radius, cylinder.Height),
            _ => Vector3.Zero
        };
        var decline = values.Declination + Symmetric(random) * emitter.DeclinationVariation;
        var planar = values.PlanarAngle + Symmetric(random) * emitter.PlanarAngleVariation;
        var speed = values.Speed + Symmetric(random) * emitter.SpeedVariation;
        var velocity = new Vector3(MathF.Sin(decline) * MathF.Cos(planar), MathF.Sin(decline) * MathF.Sin(planar), MathF.Cos(decline)) * speed;
        var life = Math.Max(float.Epsilon, values.Life + Symmetric(random) * emitter.LifeSpanVariation);
        var radius = Math.Max(0, values.Radius + Symmetric(random) * emitter.RadiusVariation);
        var angle = rotation is null ? 0 : rotation.RotationAngle + Symmetric(random) * rotation.RotationAngleVariation;
        var angularSpeed = rotation is null ? 0 : rotation.RotationSpeed + Symmetric(random) * rotation.RotationSpeedVariation;
        if (rotation?.RandomRotSpeedSign == true && random.Next(2) == 1) angularSpeed = -angularSpeed;
        return new(time, life, Vector3.Transform(position, emitterWorld), Vector3.TransformNormal(velocity, emitterWorld),
            radius * worldScale, angle, angularSpeed, textureRegions[random.Next(textureRegions.Length)]);
    }

    public IReadOnlyList<ModelPreviewParticle> Evaluate(string? clipName, double seconds, CancellationToken token = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var clip = clips.FirstOrDefault(clip => clip.Description.Name == clipName);
        if (clip is null) return [];
        var duration = clip.Description.Duration;
        var time = clip.Timing.Cycle switch
        {
            CycleType.CYCLE_CLAMP => Math.Min(seconds, duration),
            CycleType.CYCLE_REVERSE => duration - Math.Abs(seconds % (2 * duration) - duration),
            _ => seconds % duration
        };
        var animationIndex = animation.Clips.ToList().FindIndex(item => item.Name == clipName);
        var material = animationIndex < 0 ? null : animation.Evaluate(animationIndex, seconds).Single();
        if (material?.Visible == false) return [];
        var tint = material?.EmissiveColor ?? new Vector3(shader.BaseColor.R, shader.BaseColor.G, shader.BaseColor.B);
        tint *= material?.EmissiveMultiplier ?? shader.BaseColorScale;
        var alpha = material?.Alpha ?? shader.BaseColor.A;
        var delta = material?.Transform ?? Matrix4x4.Identity;
        var radiusScale = new Vector3(delta.M11, delta.M12, delta.M13).Length();
        var uvScale = material?.UVScale ?? new Vector2(shader.UVScale.U, shader.UVScale.V);
        var uvOffset = material?.UVOffset ?? new Vector2(shader.UVOffset.U, shader.UVOffset.V);
        var particles = new List<ModelPreviewParticle>();
        var physicalTime = clip.Timing.Cycle == CycleType.CYCLE_LOOP ? seconds : time;
        var total = IntegratedEmission(clip, physicalTime);
        var oldest = IntegratedEmission(clip, Math.Max(0, physicalTime - clip.MaximumLife));
        if (total >= long.MaxValue) throw new NotSupportedException("The elapsed particle timeline exceeds the preview limit.");
        for (var number = (long)Math.Floor(oldest) + 1; number <= Math.BitIncrement(total); number++)
        {
            token.ThrowIfCancellationRequested();
            var perCycle = clip.Emission[^1].Total;
            var cycle = clip.Timing.Cycle == CycleType.CYCLE_LOOP ? Math.Max(0, Math.Ceiling(number / perCycle) - 1) : 0;
            var withinCycle = Math.Min(perCycle, number - cycle * perCycle);
            var next = FirstSample(clip.Emission, withinCycle, sample => sample.Total);
            var after = clip.Emission[next]; var before = clip.Emission[Math.Max(0, next - 1)];
            var localTime = after.Total <= before.Total ? after.Time :
                double.Lerp(before.Time, after.Time, (withinCycle - before.Total) / (after.Total - before.Total));
            var values = Defaults();
            foreach (var track in clip.Tracks) track(values, clip.Timing.At(localTime, fromStart: true));
            var birth = CreateBirth(localTime + cycle * duration, values, new Random(unchecked((int)(number ^ number >> 32))));
            var age = physicalTime - birth.Time;
            if (age < 0 || age >= birth.Life) continue;
            var progress = (float)(age / birth.Life);
            var color = ParticleColor(progress) * new Vector4(tint, alpha);
            var radius = birth.Radius * ParticleScale(progress) * radiusScale;
            var region = birth.Region;
            // NIF subtexture rectangles are left, width, top, height.
            var uv = new Vector4(region.X, region.Z, region.X + region.Y, region.Z + region.W);
            uv = uv * new Vector4(uvScale, uvScale.X, uvScale.Y) + new Vector4(uvOffset, uvOffset.X, uvOffset.Y);
            particles.Add(new(Vector3.Transform(birth.Position + birth.Velocity * (float)age, delta), radius,
                birth.Rotation + birth.RotationSpeed * (float)age, color, uv));
            if (particles.Count > ParticleLimit) throw new NotSupportedException("The live particle count exceeds the preview limit.");
        }
        return particles;
    }

    private static double IntegratedEmission(Clip clip, double time)
    {
        var duration = clip.Description.Duration;
        var cycles = clip.Timing.Cycle == CycleType.CYCLE_LOOP ? Math.Floor(time / duration) : 0;
        var localTime = clip.Timing.Cycle == CycleType.CYCLE_LOOP ? time % duration : Math.Min(time, duration);
        var next = FirstSample(clip.Emission, localTime, sample => sample.Time);
        var after = clip.Emission[next]; var before = clip.Emission[Math.Max(0, next - 1)];
        var total = after.Time <= before.Time ? after.Total :
            double.Lerp(before.Total, after.Total, (localTime - before.Time) / (after.Time - before.Time));
        return cycles * clip.Emission[^1].Total + total;
    }

    private static int FirstSample(List<EmissionSample> samples, double target, Func<EmissionSample, double> value)
    {
        var low = 0; var high = samples.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (value(samples[middle]) < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private Vector4 ParticleColor(float age)
    {
        if (colors is null) return new(emitter.InitialColor.R, emitter.InitialColor.G, emitter.InitialColor.B, emitter.InitialColor.A);
        static Vector4 Vector(Color4 color) => new(color.R, color.G, color.B, color.A);
        var first = Vector(colors.Colors[0]); var middle = Vector(colors.Colors[1]); var last = Vector(colors.Colors[2]);
        var color = age < colors.Color1StartPercent
            ? Vector4.Lerp(first, middle, Fraction(age, colors.Color1EndPercent, colors.Color1StartPercent))
            : Vector4.Lerp(middle, last, Fraction(age, colors.Color2EndPercent, colors.Color2StartPercent));
        color.W = age < colors.FadeInPercent
            ? float.Lerp(first.W, middle.W, Fraction(age, 0, colors.FadeInPercent))
            : float.Lerp(middle.W, last.W, Fraction(age, colors.FadeOutPercent, 1));
        return color;
    }

    private float ParticleScale(float age)
    {
        if (scale?.Scales is not { Count: > 0 } values) return 1;
        var position = Math.Clamp(age, 0, 1) * (values.Count - 1);
        var index = (int)position;
        return float.Lerp(values[index], values[Math.Min(index + 1, values.Count - 1)], position - index);
    }
    private static float Fraction(float value, float start, float end) => end <= start ? value >= end ? 1 : 0 : Math.Clamp((value - start) / (end - start), 0, 1);
    private static float Symmetric(Random random) => random.NextSingle() * 2 - 1;
    private static Vector3 CylinderPosition(Random random, float radius, float height)
    {
        var angle = random.NextSingle() * MathF.Tau;
        var distance = MathF.Sqrt(random.NextSingle()) * radius;
        return new(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, Symmetric(random) * height / 2);
    }
    private static Vector3 SpherePosition(Random random)
    {
        var z = Symmetric(random); var angle = random.NextSingle() * MathF.Tau;
        var radial = MathF.Sqrt(1 - z * z); var distance = MathF.Cbrt(random.NextSingle());
        return new Vector3(radial * MathF.Cos(angle), radial * MathF.Sin(angle), z) * distance;
    }
    private static Matrix4x4 WorldTransform(NifFile nif, NiAVObject node)
    {
        var world = Matrix4x4.Identity; var seen = new HashSet<NiAVObject>();
        for (NiAVObject? current = node; current is not null; current = nif.GetParentNode(current))
        {
            if (!seen.Add(current)) throw new NotSupportedException("The emitter hierarchy contains a cycle.");
            world *= ModelPreviewAnimation.LocalTransform(current);
        }
        return world;
    }

    private void ValidateEmitterMotion()
    {
        HashSet<NiAVObject> Ancestors(NiAVObject node)
        {
            var nodes = new HashSet<NiAVObject>();
            for (NiAVObject? current = node; current is not null && nodes.Add(current); current = nif.GetParentNode(current)) { }
            return nodes;
        }
        var exclusive = Ancestors(shape);
        exclusive.SymmetricExceptWith(Ancestors((NiAVObject)nif.GetBlock(emitter.EmitterObject)));
        var names = exclusive.Select(node => node.Name?.ToString()).ToHashSet();
        if (nif.Blocks.OfType<NiTransformController>().Any(controller => nif.GetBlock(controller.Target) is NiAVObject target && exclusive.Contains(target)) ||
            nif.Blocks.OfType<NiControllerSequence>().Any(sequence => sequence.ControlledBlocks.Any(block =>
                names.Contains(block.NodeName?.ToString()) && nif.GetBlock(block.Interpolator) is NiTransformInterpolator)))
            throw new NotSupportedException("Motion between the emitter and its particle system is not simulated.");
    }
}

public sealed class ModelPreviewParticleBillboards(HelixToolkit.SharpDX.TextureModel texture)
    : HelixToolkit.SharpDX.BillboardImage3D(texture)
{
    private IReadOnlyList<ModelPreviewParticle> particles = [];

    public void Update(IReadOnlyList<ModelPreviewParticle> frame)
    {
        particles = frame;
        ImageInfos.Clear();
        foreach (var particle in frame)
            ImageInfos.Add(new() { Position = particle.Position, Width = particle.Radius * 2, Height = particle.Radius * 2,
                Angle = particle.Rotation, UV_TopLeft = new(particle.UVRect.X, particle.UVRect.Y),
                UV_BottomRight = new(particle.UVRect.Z, particle.UVRect.W) });
        Invalidate();
    }

    protected override void OnUpdateTextureAndBillboardVertices(HelixToolkit.SharpDX.IDeviceResources resources)
    {
        base.OnUpdateTextureAndBillboardVertices(resources);
        for (var index = 0; index < BillboardVertices.Count; index++)
        {
            var vertex = BillboardVertices[index];
            var color = particles[index].Color;
            vertex.Foreground = new HelixToolkit.Maths.Color4(color.X, color.Y, color.Z, color.W);
            BillboardVertices[index] = vertex;
        }
    }
}
