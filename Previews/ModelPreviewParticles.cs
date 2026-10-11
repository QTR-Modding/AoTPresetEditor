using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

namespace AoTPresetEditor;

public sealed record ModelPreviewParticle(Vector3 Position, float Radius, float Rotation, Vector4 Color, Vector4 UVRect,
    float ColorScale = 1);

public sealed class ModelPreviewParticleSystem
{
    private const int ParticleLimit = 2048;
    private const double SampleStep = 1.0 / 120;
    private const double MaximumClipSeconds = 120;
    private const int ForceUpdateLimit = 500_000;
    private const float GravityAcceleration = 1.6f;
    private const float TurbulenceAcceleration = 500;
    private const float DragRate = 30.0000286f;
    private const float MinimumGravityAxisLength = 1e-6f;
    private readonly NifFile nif;
    private readonly NiParticleSystem shape;
    private readonly NiPSysVolumeEmitter emitter;
    private readonly BSPSysSimpleColorModifier? colors;
    private readonly BSPSysScaleModifier? scale;
    private readonly NiPSysRotationModifier? rotation;
    private readonly NodeMotion systemMotion;
    private readonly NodeMotion emitterMotion;
    private readonly List<Force> forces = [];
    private readonly BSEffectShaderProperty shader;
    private readonly Vector4[] textureRegions;
    private readonly List<Clip> clips = [];
    private readonly ModelPreviewAnimation animation = new();

    public string Name => shape.Name?.ToString() ?? "Particles";
    public IReadOnlyList<ModelPreviewAnimationClip> Clips { get; private set; } = [];
    public Vector3 BoundsCenter { get; private set; }
    public float BoundsRadius { get; private set; }
    public int MaximumParticleCount { get; private set; }

    private sealed record Birth(long Number, double Time, float Life, Vector3 Position, Vector3 Velocity, float Radius,
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

    private sealed record ModifierTrack(string? Clip, ModelPreviewAnimation.Timing Timing,
        Func<float, bool>? Active, Func<float, float>? Strength);
    private sealed record Force(NiPSysModifier Modifier, NodeMotion Motion, List<ModifierTrack> Tracks);
    private sealed record ForceFrame(Vector3 Axis, Vector3 Origin, float Strength, float Turbulence,
        float Percentage, float Range, float Falloff, bool Active);
    private sealed class NodeMotion
    {
        private sealed record Track(string? Clip, NiAVObject Node, ModelPreviewAnimation.Timing Timing,
            Func<float, Matrix4x4> Sample);
        private readonly NiAVObject[] ancestors;
        private readonly List<Track> tracks = [];
        private readonly Matrix4x4 attachment;

        public NodeMotion(NifFile nif, NiAVObject node, NiAVObject? boundary, Matrix4x4 attachment)
        {
            this.attachment = attachment;
            ancestors = Ancestors(nif, node, boundary);
            foreach (var controller in nif.Blocks.OfType<NiTransformController>())
            {
                if (!controller.Flags.Active || controller.Flags.ManagerControlled ||
                    nif.GetBlock(controller.Target) is not NiAVObject target || !ancestors.Contains(target)) continue;
                var interpolator = nif.GetBlock(controller.Interpolator) as NiInterpolator
                    ?? throw new NotSupportedException("A particle node transform interpolator is missing.");
                tracks.Add(new(null, target, Timing(controller), ModelPreviewAnimation.TransformSampler(nif, interpolator, target)));
            }
            foreach (var sequence in nif.Blocks.OfType<NiControllerSequence>())
            foreach (var block in sequence.ControlledBlocks)
            {
                if (nif.GetBlock(block.Interpolator) is not NiTransformInterpolator interpolator) continue;
                var controller = nif.GetBlock(block.Controller) as NiTimeController;
                var candidates = controller is NiMultiTargetTransformController
                    ? ancestors.Where(target => target.Name?.ToString() == block.NodeName?.ToString()).ToArray()
                    : ancestors.Where(target => ReferenceEquals(nif.GetBlock(controller?.Target), target)).ToArray();
                if (candidates.Length > 1) throw new NotSupportedException("A particle animation target is ambiguous.");
                if (candidates.Length == 1)
                    tracks.Add(new(sequence.Name?.ToString() ?? "Animation", candidates[0], Timing(sequence),
                        ModelPreviewAnimation.TransformSampler(nif, interpolator, candidates[0])));
            }
        }

        public Matrix4x4 Local(string? clip, double seconds, NiAVObject node)
        {
            var result = ModelPreviewAnimation.LocalTransform(node);
            foreach (var track in tracks)
                if (ReferenceEquals(track.Node, node) && (track.Clip is null || track.Clip == clip))
                    result = track.Sample(track.Timing.At(seconds, fromStart: track.Clip is not null));
            return result;
        }

        public Matrix4x4 World(string? clip, double seconds)
        {
            var result = Matrix4x4.Identity;
            foreach (var node in ancestors) result *= Local(clip, seconds, node);
            return result * attachment;
        }

        public Matrix4x4 Local(string? clip, double seconds) => Local(clip, seconds, ancestors[0]);

        public (float Reach, float Scale, float MinimumScale) Envelope(CancellationToken token)
        {
            var reach = 0f; var worldScale = 1f; var minimumScale = 1f;
            foreach (var node in ancestors)
            {
                var transform = ModelPreviewAnimation.LocalTransform(node);
                var distance = transform.Translation.Length(); var scale = UniformScale(transform); var minimum = scale;
                foreach (var track in tracks.Where(track => ReferenceEquals(track.Node, node)))
                {
                    if (!double.IsFinite(track.Timing.Duration) || track.Timing.Duration > MaximumClipSeconds)
                        throw new NotSupportedException("A particle node animation exceeds the preview timeline limit.");
                    for (var time = 0.0; time <= track.Timing.Duration; time += SampleStep)
                    {
                        token.ThrowIfCancellationRequested();
                        transform = track.Sample(track.Timing.At(time, fromStart: true));
                        distance = Math.Max(distance, transform.Translation.Length());
                        scale = Math.Max(scale, UniformScale(transform));
                        minimum = Math.Min(minimum, UniformScale(transform));
                    }
                }
                reach = reach * scale + distance;
                worldScale *= scale;
                minimumScale *= minimum;
            }
            var attachmentScale = UniformScale(attachment);
            return (reach * attachmentScale, worldScale * attachmentScale, minimumScale * attachmentScale);
        }
    }

    private ModelPreviewParticleSystem(NifFile nif, NiParticleSystem shape, NiPSysVolumeEmitter emitter,
        Matrix4x4 initialWorld, BSEffectShaderProperty shader, NiAVObject? attachmentRoot)
    {
        this.nif = nif; this.shape = shape; this.emitter = emitter; this.shader = shader;
        if (new[] { emitter.LifeSpanVariation, emitter.SpeedVariation, emitter.RadiusVariation, emitter.DeclinationVariation,
                emitter.PlanarAngleVariation }.Any(value => !float.IsFinite(value)))
            throw new NotSupportedException("Particle parameter variations must be finite.");
        var modifiers = shape.Modifiers.GetBlocks(nif).Where(modifier => modifier.Active == true).ToArray();
        colors = modifiers.OfType<BSPSysSimpleColorModifier>().SingleOrDefault();
        scale = modifiers.OfType<BSPSysScaleModifier>().SingleOrDefault();
        rotation = modifiers.OfType<NiPSysRotationModifier>().SingleOrDefault();
        var localWorld = WorldTransform(nif, shape, attachmentRoot);
        if (!Matrix4x4.Invert(localWorld, out var inverse)) throw new NotSupportedException("The particle system has zero scale.");
        var attachment = inverse * initialWorld;
        var sourceNode = nif.GetBlock(emitter.EmitterObject) as NiAVObject
            ?? throw new NotSupportedException("The emitter node is missing.");
        systemMotion = new(nif, shape, attachmentRoot, attachment);
        emitterMotion = new(nif, sourceNode, attachmentRoot, attachment);
        var data = nif.GetBlock(shape.Data_NiBlockRef_NPSD) as NiPSysData;
        textureRegions = data?.SubtextureOffsets?.ToArray() ?? [];
        if (textureRegions.Length == 0) textureRegions = [new(0, 1, 0, 1)];
        BoundsCenter = attachment.Translation;
        foreach (var modifier in shape.Modifiers.GetBlocks(nif))
        {
            NiAVObject? forceNode = modifier switch
            {
                NiPSysGravityModifier gravity => nif.GetBlock(gravity.GravityObject) as NiAVObject,
                NiPSysDragModifier drag => nif.GetBlock(drag.DragObject) as NiAVObject,
                _ => null
            };
            if (modifier is not (NiPSysGravityModifier or NiPSysDragModifier)) continue;
            if (forceNode is null) throw new NotSupportedException($"{modifier.Name}: the force node is missing.");
            forces.Add(new(modifier, new(nif, forceNode, attachmentRoot, attachment), []));
        }
        BuildForceTracks();
    }

    public static ModelPreviewParticleSystem? Create(NifFile nif, NiParticleSystem shape, Matrix4x4 initialWorld,
        ISet<string> messages, CancellationToken token = default, NiAVObject? attachmentRoot = null)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var modifiers = shape.Modifiers.GetBlocks(nif).ToArray();
            var emitters = modifiers.OfType<NiPSysEmitter>().ToArray();
            if (emitters.Length != 1 || emitters[0] is not (NiPSysBoxEmitter or NiPSysSphereEmitter or NiPSysCylinderEmitter))
                throw new NotSupportedException("Only a single box, sphere or cylinder emitter can be previewed.");
            foreach (var modifier in modifiers.Where(modifier => modifier.Active == true))
                if (modifier is not (NiPSysAgeDeathModifier or BSPSysLODModifier or NiPSysBoxEmitter or
                    NiPSysSphereEmitter or NiPSysCylinderEmitter or NiPSysSpawnModifier or BSPSysSimpleColorModifier or
                    BSPSysScaleModifier or NiPSysRotationModifier or NiPSysPositionModifier or NiPSysBoundUpdateModifier or
                    NiPSysGravityModifier or NiPSysDragModifier))
                    throw new NotSupportedException($"{modifier.GetType().Name} is not simulated.");
            foreach (var gravity in modifiers.OfType<NiPSysGravityModifier>())
                if (gravity.ForceType != ForceType.FORCE_PLANAR || gravity.Decay != 0)
                    throw new NotSupportedException("Only planar particle gravity with zero decay is simulated.");
                else if (new[] { gravity.Strength, gravity.Turbulence, gravity.TurbulenceScale }.Any(value => !float.IsFinite(value)) ||
                    gravity.Turbulence < 0 || gravity.TurbulenceScale < 0)
                    throw new NotSupportedException("Particle gravity parameters must be finite and turbulence cannot be negative.");
            foreach (var drag in modifiers.OfType<NiPSysDragModifier>())
                if (new[] { drag.Percentage, drag.Range, drag.RangeFalloff }.Any(value => !float.IsFinite(value)) ||
                    drag.Percentage < 0 || drag.Range < 0 || drag.RangeFalloff < drag.Range)
                    throw new NotSupportedException("Particle drag parameters must be finite with a nonnegative percentage and ordered ranges.");
            if (modifiers.OfType<NiPSysAgeDeathModifier>().Any(modifier => modifier.SpawnonDeath == true) ||
                modifiers.OfType<NiPSysSpawnModifier>().Any(modifier => modifier.NumSpawnGenerations != 0))
                throw new NotSupportedException("Spawning particles from other particles is not simulated.");
            if (nif.GetShader(shape) is not BSEffectShaderProperty shader)
                throw new NotSupportedException("This particle material is not supported.");
            if (modifiers.OfType<BSPSysSimpleColorModifier>().Any(color => color.Colors?.Length != 3))
                throw new NotSupportedException("The particle color modifier needs three colors.");
            var result = new ModelPreviewParticleSystem(nif, shape, (NiPSysVolumeEmitter)emitters[0], initialWorld, shader, attachmentRoot);
            result.animation.AddPart(nif, [(shape, 0, initialWorld)], messages,
                attachmentRoot is null ? null : new Dictionary<INiShape, NiAVObject> { [shape] = attachmentRoot });
            result.BuildClips(token);
            if (result.clips.Count == 0) throw new NotSupportedException("The emitter has no supported birth-rate animation.");
            result.SampleForces(result.clips[0].Description.Name, 0);
            result.Clips = result.clips.Select(clip => clip.Description).ToArray();
            if (modifiers.Any(modifier => modifier is BSPSysLODModifier))
                messages.Add("Particle previews use full detail; distance-based particle reduction is not applied.");
            if (result.forces.Count > 0)
                messages.Add("Particle forces use deterministic 120 Hz simulation; turbulence and motion can differ from Skyrim's frame-dependent updates.");
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
                    !ReferenceEquals(nif.GetBlock(controller.Target), shape) ||
                    controller.ModifierName?.ToString() != emitter.Name?.ToString()) continue;
                AddTrack(controller, nif.GetBlock(block.Interpolator) as NiInterpolator,
                    block.InterpolatorID?.ToString(), tracks);
            }
            if (tracks.Count == 0) continue;
            var timing = Timing(sequence);
            AddClip(sequence.Name?.ToString() ?? "Animation", timing, tracks, token);
        }
        if (clips.Count != 0) return;
        var direct = new List<Action<Parameters, float>>();
        var stop = 0f;
        foreach (var controller in nif.Blocks.OfType<NiPSysModifierCtlr>())
        {
            if (!ReferenceEquals(nif.GetBlock(controller.Target), shape) || !controller.Flags.Active || controller.Flags.ManagerControlled ||
                controller.ModifierName?.ToString() != emitter.Name?.ToString()) continue;
            var tracks = new List<Action<Parameters, float>>();
            AddTrack(controller, nif.GetBlock(controller.Interpolator) as NiInterpolator, "BirthRate", tracks);
            if (controller is NiPSysEmitterCtlr birth)
                AddTrack(controller, nif.GetBlock(birth.VisibilityInterpolator) as NiInterpolator, "EmitterActive", tracks);
            var timing = Timing(controller);
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

    private void BuildForceTracks()
    {
        void Add(NiPSysModifierCtlr controller, NiInterpolator? interpolator, string? clip, ModelPreviewAnimation.Timing timing)
        {
            var force = forces.SingleOrDefault(force => force.Modifier.Name?.ToString() == controller.ModifierName?.ToString());
            if (force is null) return;
            if (interpolator is null) throw new NotSupportedException("An animated particle force interpolator is missing.");
            if (controller is NiPSysModifierActiveCtlr)
                force.Tracks.Add(new(clip, timing, ModelPreviewAnimation.BoolSampler(nif, interpolator), null));
            else if (controller is NiPSysGravityStrengthCtlr && force.Modifier is NiPSysGravityModifier)
                force.Tracks.Add(new(clip, timing, null, ModelPreviewAnimation.FloatSampler(nif, interpolator)));
            else throw new NotSupportedException($"{controller.GetType().Name} is not simulated.");
        }
        foreach (var controller in nif.Blocks.OfType<NiPSysModifierCtlr>())
            if (ReferenceEquals(nif.GetBlock(controller.Target), shape) && controller.Flags.Active && !controller.Flags.ManagerControlled)
                Add(controller, nif.GetBlock(controller.Interpolator) as NiInterpolator, null, Timing(controller));
        foreach (var sequence in nif.Blocks.OfType<NiControllerSequence>())
        foreach (var block in sequence.ControlledBlocks)
            if (nif.GetBlock(block.Controller) is NiPSysModifierCtlr controller && ReferenceEquals(nif.GetBlock(controller.Target), shape))
                Add(controller, nif.GetBlock(block.Interpolator) as NiInterpolator, sequence.Name?.ToString() ?? "Animation", Timing(sequence));
    }

    private static ModelPreviewAnimation.Timing Timing(NiTimeController controller) => new(controller.StartTime, controller.StopTime,
        controller.Frequency, controller.Phase, controller.Flags.CycleType, controller.Flags.PlayBackwards);
    private static ModelPreviewAnimation.Timing Timing(NiControllerSequence sequence) => new(sequence.StartTime, sequence.StopTime,
        sequence.Frequency, sequence.Phase, sequence.CycleType, sequence.PlayBackwards.GetValueOrDefault());

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
        if (capacity * Math.Ceiling(maxLife / SampleStep) * forces.Count > ForceUpdateLimit)
            throw new NotSupportedException("The emitter exceeds the particle force simulation budget.");
        MaximumParticleCount = Math.Max(MaximumParticleCount, (int)capacity);
        var placementRadius = emitter switch
        {
            NiPSysBoxEmitter box => new Vector3(box.Width, box.Height, box.Depth).Length() / 2,
            NiPSysSphereEmitter sphere => sphere.Radius,
            NiPSysCylinderEmitter cylinder => MathF.Sqrt(cylinder.Radius * cylinder.Radius + cylinder.Height * cylinder.Height / 4),
            _ => 0
        };
        var largestScale = Math.Max(1, scale?.Scales?.DefaultIfEmpty(1).Max() ?? 1);
        var sourceEnvelope = emitterMotion.Envelope(token);
        var systemEnvelope = systemMotion.Envelope(token);
        var acceleration = MaximumAcceleration(token);
        var simulationReach = maxSpeed * maxLife + acceleration * maxLife * (maxLife / 2 + (float)SampleStep);
        var birthReach = sourceEnvelope.Reach + placementRadius * sourceEnvelope.Scale;
        // Rotation can change throughout independent controller cycles; the force magnitude bound covers every axis pose.
        var scaleRatio = systemEnvelope.Scale / systemEnvelope.MinimumScale;
        var bound = shape.WorldSpace == true
            ? birthReach * scaleRatio + BoundsCenter.Length() * (scaleRatio - 1)
            : (birthReach + systemEnvelope.Reach) * scaleRatio + systemEnvelope.Reach;
        bound +=
            (simulationReach + maxRadius * largestScale) * systemEnvelope.Scale;
        if (!float.IsFinite(bound)) throw new NotSupportedException("The particle trajectory exceeds finite preview bounds.");
        BoundsRadius = Math.Max(BoundsRadius, bound);
        clips.Add(new(new(name, timing.Duration, timing.Cycle), timing, tracks, emission, maxLife));
    }

    private float MaximumAcceleration(CancellationToken token)
    {
        var acceleration = 0f;
        foreach (var force in forces)
        {
            if (force.Modifier is not NiPSysGravityModifier gravity) continue;
            var strength = Math.Abs(gravity.Strength);
            foreach (var track in force.Tracks.Where(track => track.Strength is not null))
            {
                if (!double.IsFinite(track.Timing.Duration) || track.Timing.Duration > MaximumClipSeconds)
                    throw new NotSupportedException("A particle force animation exceeds the preview timeline limit.");
                for (var time = 0.0; time <= track.Timing.Duration; time += SampleStep)
                {
                    token.ThrowIfCancellationRequested();
                    var value = track.Strength!(track.Timing.At(time, fromStart: true));
                    if (!float.IsFinite(value)) throw new NotSupportedException("Animated particle gravity must be finite.");
                    strength = Math.Max(strength, Math.Abs(value));
                }
            }
            acceleration += strength * GravityAcceleration + MathF.Sqrt(3) * gravity.Turbulence * gravity.TurbulenceScale * TurbulenceAcceleration;
        }
        return acceleration;
    }

    private Birth CreateBirth(long number, double time, Parameters values, Random random, string? clip)
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
        if (!Matrix4x4.Invert(SimulationWorld(clip, time), out var inverse))
            throw new NotSupportedException("The particle system has zero scale.");
        var relative = emitterMotion.World(clip, time) * inverse;
        return new(number, time, life, Vector3.Transform(position, relative), Vector3.TransformNormal(velocity, relative) / UniformScale(relative),
            radius, angle, angularSpeed, textureRegions[random.Next(textureRegions.Length)]);
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
        var material = animation.Evaluate(animationIndex >= 0 ? animationIndex : null, seconds).Single();
        if (!material.Visible) return [];
        var tint = material.EmissiveColor ?? new Vector3(shader.BaseColor.R, shader.BaseColor.G, shader.BaseColor.B);
        var colorScale = material.EmissiveMultiplier ?? shader.BaseColorScale;
        var alpha = material.Alpha ?? shader.BaseColor.A;
        var uvScale = material.UVScale ?? new Vector2(shader.UVScale.U, shader.UVScale.V);
        var uvOffset = material.UVOffset ?? new Vector2(shader.UVOffset.U, shader.UVOffset.V);
        var particles = new List<ModelPreviewParticle>();
        var physicalTime = clip.Timing.Cycle == CycleType.CYCLE_LOOP ? seconds : time;
        var renderWorld = SimulationWorld(clipName, physicalTime);
        var radiusScale = UniformScale(renderWorld);
        var forceFrames = new Dictionary<long, ForceFrame[]>();
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
            var birth = CreateBirth(number, localTime + cycle * duration, values, new Random(unchecked((int)(number ^ number >> 32))), clipName);
            var age = physicalTime - birth.Time;
            if (age < 0 || age >= birth.Life) continue;
            var progress = (float)(age / birth.Life);
            var color = ParticleColor(progress) * new Vector4(tint, alpha);
            var radius = birth.Radius * ParticleScale(progress) * radiusScale;
            var region = birth.Region;
            // NIF subtexture rectangles are left, width, top, height.
            var uv = new Vector4(region.X, region.Z, region.X + region.Y, region.Z + region.W);
            uv = uv * new Vector4(uvScale, uvScale.X, uvScale.Y) + new Vector4(uvOffset, uvOffset.X, uvOffset.Y);
            var position = Simulate(birth, physicalTime, clipName, forceFrames, token);
            particles.Add(new(Vector3.Transform(position, renderWorld), radius,
                birth.Rotation + birth.RotationSpeed * (float)age, color, uv, colorScale));
            if (particles.Count > ParticleLimit) throw new NotSupportedException("The live particle count exceeds the preview limit.");
        }
        return particles;
    }

    private Matrix4x4 SimulationWorld(string? clip, double seconds)
    {
        var world = systemMotion.World(clip, seconds);
        return shape.WorldSpace == true ? Matrix4x4.CreateScale(UniformScale(world)) : world;
    }

    private Vector3 Simulate(Birth birth, double stop, string? clip, Dictionary<long, ForceFrame[]> frames, CancellationToken token)
    {
        if (forces.Count == 0) return birth.Position + birth.Velocity * (float)(stop - birth.Time);
        var position = birth.Position; var velocity = birth.Velocity;
        var time = birth.Time;
        if (stop / SampleStep >= long.MaxValue) throw new NotSupportedException("The elapsed particle simulation exceeds the preview timeline limit.");
        while (time < stop)
        {
            token.ThrowIfCancellationRequested();
            var step = (long)Math.Floor(time / SampleStep) + 1;
            var end = Math.Min(stop, step * SampleStep);
            if (end <= time) { step++; end = Math.Min(stop, step * SampleStep); }
            var dt = (float)(end - time);
            if (!frames.TryGetValue(step, out var states))
            {
                states = SampleForces(clip, step * SampleStep);
                frames.Add(step, states);
            }
            for (var index = 0; index < states.Length; index++)
            {
                var force = states[index];
                if (!force.Active) continue;
                if (forces[index].Modifier is NiPSysGravityModifier)
                {
                    var acceleration = force.Axis * force.Strength * GravityAcceleration;
                    if (force.Turbulence > 0)
                        acceleration += Turbulence(birth.Number, step, index) * force.Turbulence * TurbulenceAcceleration;
                    velocity += acceleration * dt;
                }
                else
                {
                    var distance = Vector3.Distance(position, force.Origin);
                    var range = distance <= force.Range ? 1 : distance >= force.Falloff ? 0 :
                        1 - (distance - force.Range) / (force.Falloff - force.Range);
                    velocity -= force.Axis * Vector3.Dot(velocity, force.Axis) * Math.Min(1, DragRate * force.Percentage * dt * range);
                }
            }
            position += velocity * dt;
            time = end;
        }
        return position;
    }

    private ForceFrame[] SampleForces(string? clip, double seconds)
    {
        if (!Matrix4x4.Invert(SimulationWorld(clip, seconds), out var inverse))
            throw new NotSupportedException("The particle system has zero scale.");
        return forces.Select(force =>
        {
            var active = force.Modifier.Active == true;
            var strength = force.Modifier is NiPSysGravityModifier gravity ? gravity.Strength : 0;
            foreach (var track in force.Tracks)
            {
                if (track.Clip is not null && track.Clip != clip) continue;
                var time = track.Timing.At(seconds, fromStart: track.Clip is not null);
                if (track.Active is not null) active = track.Active(time);
                if (track.Strength is not null) strength = track.Strength(time);
            }
            if (!float.IsFinite(strength)) throw new NotSupportedException("Animated particle gravity must be finite.");
            var relative = force.Motion.World(clip, seconds) * inverse;
            var direction = force.Modifier switch
            {
                NiPSysGravityModifier value => value.GravityAxis,
                NiPSysDragModifier value => value.DragAxis,
                _ => Vector3.Zero
            };
            if (!float.IsFinite(direction.LengthSquared())) throw new NotSupportedException("A particle force axis is invalid.");
            var transform = force.Modifier is NiPSysGravityModifier { WorldAligned: true }
                ? force.Motion.Local(clip, seconds) : relative;
            var axis = Vector3.TransformNormal(direction, transform);
            if (!float.IsFinite(axis.LengthSquared())) throw new NotSupportedException("An animated particle force axis is invalid.");
            var minimumLength = force.Modifier is NiPSysGravityModifier ? MinimumGravityAxisLength : 0;
            axis = direction.Length() <= minimumLength || axis.LengthSquared() == 0 ? Vector3.Zero : Vector3.Normalize(axis);
            return force.Modifier switch
            {
                NiPSysGravityModifier value => new ForceFrame(axis, relative.Translation, strength,
                    value.Turbulence * value.TurbulenceScale, 0, 0, 0, active),
                NiPSysDragModifier value => new ForceFrame(axis, relative.Translation, 0, 0,
                    value.Percentage, value.Range, value.RangeFalloff, active),
                _ => throw new NotSupportedException("The particle force is not supported.")
            };
        }).ToArray();
    }

    private static Vector3 Turbulence(long particle, long step, int force)
    {
        var seed = unchecked((ulong)particle * 0x9E3779B97F4A7C15UL ^ (ulong)step ^ (uint)force);
        float Component()
        {
            seed ^= seed >> 12; seed ^= seed << 25; seed ^= seed >> 27;
            return (float)((seed * 0x2545F4914F6CDD1DUL >> 40) / (double)(1 << 24) * 2 - 1);
        }
        return new(Component(), Component(), Component());
    }

    private static float UniformScale(Matrix4x4 transform)
    {
        var scale = new Vector3(transform.M11, transform.M12, transform.M13).Length();
        if (!float.IsFinite(scale) || scale <= 0) throw new NotSupportedException("Particle node scale must be finite and positive.");
        return scale;
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
    private static Matrix4x4 WorldTransform(NifFile nif, NiAVObject node, NiAVObject? boundary)
    {
        var world = Matrix4x4.Identity;
        foreach (var current in Ancestors(nif, node, boundary)) world *= ModelPreviewAnimation.LocalTransform(current);
        return world;
    }

    private static NiAVObject[] Ancestors(NifFile nif, NiAVObject node, NiAVObject? boundary)
    {
        var nodes = new List<NiAVObject>();
        for (NiAVObject? current = node; current is not null; current = nif.GetParentNode(current))
        {
            if (nodes.Contains(current)) throw new NotSupportedException("The particle node hierarchy contains a cycle.");
            nodes.Add(current);
            if (ReferenceEquals(current, boundary)) return nodes.ToArray();
        }
        if (boundary is not null) throw new NotSupportedException("A particle force or emitter node is outside the attached effect branch.");
        return nodes.ToArray();
    }
}

public sealed class ModelPreviewParticleBillboards(HelixToolkit.SharpDX.TextureModel texture,
    bool paletteColor = false, bool paletteAlpha = false, byte textureClampMode = (byte)TexClampMode.WRAP_S_WRAP_T)
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
            vertex.Background = new HelixToolkit.Maths.Color4(particles[index].ColorScale,
                paletteColor ? 1 : 0, paletteAlpha ? 1 : 0, textureClampMode);
            BillboardVertices[index] = vertex;
        }
    }
}
