using System.IO;
using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

namespace AoTPresetEditor;

public sealed record ModelPreviewAnimationClip(string Name, double Duration, CycleType Cycle);
public sealed record ModelPreviewAnimationFrame(int MeshIndex, Matrix4x4 Transform, bool Visible,
    float? Alpha, Vector3? EmissiveColor, float? EmissiveMultiplier, Vector2? UVOffset, Vector2? UVScale);

public sealed class ModelPreviewAnimation
{
    private readonly List<Part> parts = [];
    private readonly List<Clip> clips = [];
    private readonly List<ModelPreviewAnimationClip> descriptions = [];
    public IReadOnlyList<ModelPreviewAnimationClip> Clips => descriptions;

    internal sealed record Timing(float Start, float Stop, float Frequency, float Phase, CycleType Cycle, bool Backwards)
    {
        public double Duration => Math.Abs(Frequency) > 0 ? (Stop - Start) / Math.Abs(Frequency) : 0;
        public float At(double seconds, bool fromStart = false)
        {
            var time = (fromStart ? Start : 0) + seconds * Frequency + Phase;
            var length = Stop - Start;
            if (length <= 0) return Start;
            if (time < Start || time > Stop)
            {
                if (Cycle == CycleType.CYCLE_CLAMP) time = Math.Clamp(time, Start, Stop);
                else
                {
                    var position = (time - Start) / length;
                    var iteration = Math.Floor(position);
                    var offset = (position - iteration) * length;
                    time = Cycle == CycleType.CYCLE_REVERSE && Math.Abs(iteration % 2) == 1 ? Stop - offset : Start + offset;
                }
            }
            return (float)(Backwards ? Stop - (time - Start) : time);
        }
    }

    private sealed record Track(Action<Pose, float> Apply, Timing Timing);
    private sealed record Clip(Part Part, Timing Timing, List<Track> Tracks);
    private sealed record Binding(INiShape Shape, int MeshIndex, Matrix4x4 InverseWorld, Matrix4x4 Attachment, NiAVObject[] Ancestors, INiShader? Shader);
    private sealed class Part
    {
        public required NifFile Nif;
        public readonly List<Binding> Bindings = [];
        public readonly List<Track> Direct = [];
    }
    private sealed class Pose
    {
        public readonly Dictionary<NiAVObject, Matrix4x4> Transforms = [];
        public readonly Dictionary<NiAVObject, bool> Visibility = [];
        public readonly Dictionary<INiShader, ShaderPose> Materials = [];
    }
    private sealed class ShaderPose
    {
        public float Alpha;
        public Vector3 EmissiveColor;
        public float EmissiveMultiplier;
        public bool EmissionEnabled;
        public Vector2 UVOffset;
        public Vector2 UVScale;
    }

    public void AddPart(NifFile nif, IReadOnlyList<(INiShape Shape, int MeshIndex, Matrix4x4 InitialWorld)> bindings, ISet<string> messages)
    {
        var part = new Part { Nif = nif };
        foreach (var (shape, index, initialWorld) in bindings)
        {
            var ancestors = new List<NiAVObject>();
            var visited = new HashSet<NiAVObject>();
            for (var node = shape as NiAVObject; node is not null; node = nif.GetParentNode(node))
            {
                if (!visited.Add(node)) throw new InvalidDataException("The animation node hierarchy contains a cycle.");
                ancestors.Add(node);
            }
            var localWorld = Matrix4x4.Identity;
            foreach (var node in ancestors) localWorld *= LocalTransform(node);
            var attachment = Matrix4x4.Identity;
            if (!Matrix4x4.Invert(initialWorld, out var inverse) || !Matrix4x4.Invert(localWorld, out var inverseLocal))
            {
                messages.Add("Animation cannot move a mesh whose initial transform has zero scale.");
                inverse = Matrix4x4.Identity;
                ancestors.Clear();
            }
            else attachment = inverseLocal * initialWorld;
            part.Bindings.Add(new(shape, index, inverse, attachment, ancestors.ToArray(), nif.GetShader(shape)));
        }
        parts.Add(part);
        foreach (var sequence in nif.Blocks.OfType<NiControllerSequence>())
        {
            var timing = ValidTiming(sequence.StartTime, sequence.StopTime, sequence.Frequency, sequence.Phase,
                sequence.CycleType, sequence.PlayBackwards.GetValueOrDefault());
            if (timing is null) continue;
            var tracks = new List<Track>();
            foreach (var controlled in sequence.ControlledBlocks ?? [])
            {
                var controller = nif.GetBlock(controlled.Controller) as NiTimeController;
                var target = controller is NiMultiTargetTransformController
                    ? FindNode(nif, controlled.NodeName?.ToString(), messages)
                    : nif.GetBlock(controller?.Target) as NiObjectNET;
                AddTrack(part, controller, target, nif.GetBlock(controlled.Interpolator) as NiInterpolator, timing, tracks, messages);
            }
            if (tracks.Count == 0) continue;
            clips.Add(new(part, timing, tracks));
            descriptions.Add(new(sequence.Name?.ToString() ?? "Animation", timing.Duration, timing.Cycle));
        }
        foreach (var controller in nif.Blocks.OfType<NiSingleInterpController>())
        {
            if (!controller.Flags.Active || controller.Flags.ManagerControlled) continue;
            var timing = ValidTiming(controller.StartTime, controller.StopTime, controller.Frequency, controller.Phase,
                controller.Flags.CycleType, controller.Flags.PlayBackwards);
            if (timing is null) continue;
            AddTrack(part, controller, nif.GetBlock(controller.Target) as NiObjectNET,
                nif.GetBlock(controller.Interpolator) as NiInterpolator, timing, part.Direct, messages);
        }
        if (part.Direct.Count > 0 && !clips.Any(clip => clip.Part == part))
        {
            var duration = part.Direct.Max(track => Math.Abs(track.Timing.Frequency) > 0
                ? Math.Max(0, (track.Timing.Stop - track.Timing.Phase) / Math.Abs(track.Timing.Frequency)) : 0);
            var timing = new Timing(0, (float)duration, 1, 0, CycleType.CYCLE_LOOP, false);
            clips.Add(new(part, timing, []));
            descriptions.Add(new("Animation", duration, timing.Cycle));
        }
        if (nif.Blocks.Any(block => block is BSLagBoneController)) messages.Add("Motion-dependent attachment trails are not simulated.");
    }

    public IReadOnlyList<ModelPreviewAnimationFrame> Evaluate(int? clipIndex, double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        var frames = new List<ModelPreviewAnimationFrame>();
        foreach (var part in parts)
        {
            var pose = new Pose();
            foreach (var binding in part.Bindings)
                if (binding.Shader is { } shader && !pose.Materials.ContainsKey(shader)) pose.Materials.Add(shader, InitialShader(shader));
            foreach (var track in part.Direct) track.Apply(pose, track.Timing.At(seconds));
            var partClipIndex = clipIndex is { } selected
                ? Enumerable.Range(0, clips.Count).FirstOrDefault(index => clips[index].Part == part &&
                    descriptions[index].Name == descriptions[selected].Name, -1) : -1;
            if (partClipIndex >= 0)
            {
                var partClip = clips[partClipIndex];
                foreach (var track in partClip.Tracks) track.Apply(pose, partClip.Timing.At(seconds, fromStart: true));
            }
            foreach (var binding in part.Bindings)
            {
                var world = Matrix4x4.Identity;
                var visible = true;
                foreach (var node in binding.Ancestors)
                {
                    world *= pose.Transforms.GetValueOrDefault(node, LocalTransform(node));
                    visible &= pose.Visibility.GetValueOrDefault(node, (node.Flags_ui & 1) == 0);
                }
                var delta = binding.Shape.IsSkinned || binding.Shape.HasSkinInstance || binding.Ancestors.Length == 0
                    ? Matrix4x4.Identity : binding.InverseWorld * world * binding.Attachment;
                var material = binding.Shader is { } shader ? pose.Materials.GetValueOrDefault(shader) : null;
                frames.Add(new(binding.MeshIndex, delta, visible, material?.Alpha,
                    material is { EmissionEnabled: true } ? material.EmissiveColor : Vector3.Zero,
                    material is { EmissionEnabled: true } ? material.EmissiveMultiplier : 0, material?.UVOffset, material?.UVScale));
            }
        }
        return frames;
    }

    private static Timing? ValidTiming(float start, float stop, float frequency, float phase, CycleType cycle, bool backwards) =>
        float.IsFinite(start) && float.IsFinite(stop) && float.IsFinite(frequency) && float.IsFinite(phase) && stop >= start &&
        start != float.MaxValue && stop != -float.MaxValue ? new(start, stop, frequency, phase, cycle, backwards) : null;

    private static NiAVObject? FindNode(NifFile nif, string? name, ISet<string> messages)
    {
        var matches = nif.Blocks.OfType<NiAVObject>().Where(node => node.Name?.ToString() == name).Take(2).ToArray();
        if (matches.Length == 1) return matches[0];
        messages.Add($"Animation target '{name}' could not be resolved uniquely.");
        return null;
    }

    private static void AddTrack(Part part, NiTimeController? controller, NiObjectNET? target, NiInterpolator? interpolator,
        Timing timing, List<Track> tracks, ISet<string> messages)
    {
        if (controller is null || target is null || interpolator is null) return;
        try
        {
            if (target is NiAVObject node && controller is NiTransformController or NiMultiTargetTransformController)
            {
                var affected = part.Bindings.Where(binding => binding.Ancestors.Contains(node)).ToArray();
                if (affected.Length == 0) return;
                if (affected.Any(binding => binding.Shape.IsSkinned || binding.Shape.HasSkinInstance))
                    messages.Add("Animated skin deformation is not included; skinned meshes keep their loaded pose.");
                if (affected.All(binding => binding.Shape.IsSkinned || binding.Shape.HasSkinInstance)) return;
                var sample = TransformSampler(part.Nif, interpolator, node);
                tracks.Add(new((pose, time) => pose.Transforms[node] = sample(time), timing));
            }
            else if (target is NiAVObject visibleNode && controller is NiVisController)
            {
                if (!part.Bindings.Any(binding => binding.Ancestors.Contains(visibleNode))) return;
                var sample = BoolSampler(part.Nif, interpolator);
                tracks.Add(new((pose, time) => pose.Visibility[visibleNode] = sample(time), timing));
            }
            else if (target is INiShader shader && part.Bindings.Any(binding => ReferenceEquals(binding.Shader, shader)))
            {
                if (controller is BSEffectShaderPropertyColorController { ControlledColor: EffectShaderControlledColor.EmissiveColor } or
                    BSLightingShaderPropertyColorController { ControlledColor: LightingShaderControlledColor.EmissiveColor })
                {
                    var sample = VectorSampler(part.Nif, interpolator);
                    tracks.Add(new((pose, time) => SetEmissionColor(pose.Materials[shader], sample(time)), timing));
                }
                else
                {
                    var assign = MaterialSetter(controller);
                    var sample = FloatSampler(part.Nif, interpolator);
                    tracks.Add(new((pose, time) => assign(pose.Materials[shader], sample(time)), timing));
                }
            }
        }
        catch (NotSupportedException error) { messages.Add($"{controller.GetType().Name}: {error.Message}"); }
    }

    private static Action<ShaderPose, float> MaterialSetter(NiTimeController controller) => controller switch
    {
        BSEffectShaderPropertyFloatController { ControlledVariable: EffectShaderControlledVariable.AlphaTransparency } or
        BSLightingShaderPropertyFloatController { ControlledVariable: LightingShaderControlledFloat.Alpha } => (pose, value) => pose.Alpha = value,
        BSEffectShaderPropertyFloatController { ControlledVariable: EffectShaderControlledVariable.EmissiveMultiple } or
        BSLightingShaderPropertyFloatController { ControlledVariable: LightingShaderControlledFloat.EmissiveMultiple } => (pose, value) =>
            { pose.EmissiveMultiplier = value; pose.EmissionEnabled = true; },
        BSEffectShaderPropertyFloatController { ControlledVariable: EffectShaderControlledVariable.UOffset } or
        BSLightingShaderPropertyFloatController { ControlledVariable: LightingShaderControlledFloat.UOffset } => (pose, value) => pose.UVOffset.X = value,
        BSEffectShaderPropertyFloatController { ControlledVariable: EffectShaderControlledVariable.VOffset } or
        BSLightingShaderPropertyFloatController { ControlledVariable: LightingShaderControlledFloat.VOffset } => (pose, value) => pose.UVOffset.Y = value,
        BSEffectShaderPropertyFloatController { ControlledVariable: EffectShaderControlledVariable.UScale } or
        BSLightingShaderPropertyFloatController { ControlledVariable: LightingShaderControlledFloat.UScale } => (pose, value) => pose.UVScale.X = value,
        BSEffectShaderPropertyFloatController { ControlledVariable: EffectShaderControlledVariable.VScale } or
        BSLightingShaderPropertyFloatController { ControlledVariable: LightingShaderControlledFloat.VScale } => (pose, value) => pose.UVScale.Y = value,
        _ => throw new NotSupportedException("This animated material property is not supported.")
    };

    private static ShaderPose InitialShader(INiShader shader) => shader switch
    {
        BSEffectShaderProperty effect => new() { Alpha = effect.BaseColor.A, EmissionEnabled = true,
            EmissiveColor = new(effect.BaseColor.R, effect.BaseColor.G, effect.BaseColor.B), EmissiveMultiplier = effect.BaseColorScale,
            UVOffset = new(effect.UVOffset.U, effect.UVOffset.V), UVScale = new(effect.UVScale.U, effect.UVScale.V) },
        BSLightingShaderProperty lighting => new() { Alpha = lighting.Alpha, EmissionEnabled = lighting.Emissive || lighting.HasGlowmap,
            EmissiveColor = new(lighting.EmissiveColor.R, lighting.EmissiveColor.G, lighting.EmissiveColor.B), EmissiveMultiplier = lighting.EmissiveMultiple,
            UVOffset = new(lighting.UVOffset.U, lighting.UVOffset.V), UVScale = new(lighting.UVScale.U, lighting.UVScale.V) },
        _ => new() { Alpha = 1, UVScale = Vector2.One }
    };

    private static void SetEmissionColor(ShaderPose pose, Vector3 color)
    {
        pose.EmissiveColor = color;
        pose.EmissionEnabled = true;
    }

    internal static Func<float, float> FloatSampler(NifFile nif, NiInterpolator interpolator)
    {
        if (interpolator is not NiFloatInterpolator value) throw Unsupported(interpolator);
        if (nif.GetBlock(value.Data) is { } data) return ScalarKeys(data.Data, value.Value);
        if (!ValidValue(value.Value)) throw new NotSupportedException("The interpolator has no value or keyframes.");
        return _ => value.Value;
    }

    private static Func<float, Vector3> VectorSampler(NifFile nif, NiInterpolator interpolator)
    {
        if (interpolator is not NiPoint3Interpolator value) throw Unsupported(interpolator);
        if (nif.GetBlock(value.Data) is { } data) return VectorKeys(data.Data, value.Value);
        if (!ValidValue(value.Value.X) || !ValidValue(value.Value.Y) || !ValidValue(value.Value.Z)) throw new NotSupportedException("The interpolator has no value or keyframes.");
        return _ => value.Value;
    }

    internal static Func<float, bool> BoolSampler(NifFile nif, NiInterpolator interpolator)
    {
        if (interpolator is not NiBoolInterpolator value) throw Unsupported(interpolator);
        var keys = nif.GetBlock(value.Data)?.Data.Keys;
        if (keys is not { Count: > 0 })
        {
            if (value.Value is not { } constant) throw new NotSupportedException("The interpolator has no value or keyframes.");
            return _ => constant;
        }
        ValidateTimes(keys.Select(key => key.Time));
        return time => keys[KeyIndex(keys.Count, index => keys[index].Time, time)].Value != 0;
    }

    private static Func<float, Matrix4x4> TransformSampler(NifFile nif, NiInterpolator interpolator, NiAVObject node)
    {
        if (interpolator is not NiTransformInterpolator value) throw Unsupported(interpolator);
        var data = nif.GetBlock(value.Data);
        var translation = value.Transform.Translation;
        if (!ValidValue(translation.X) || !ValidValue(translation.Y) || !ValidValue(translation.Z)) translation = node.Translation;
        var scale = ValidValue(value.Transform.Scale) ? value.Transform.Scale : node.Scale;
        var rotation = value.Transform.Rotation;
        var matrix = RotationMatrix(node.Rotation);
        if (ValidValue(rotation.X) && ValidValue(rotation.Y) && ValidValue(rotation.Z) && ValidValue(rotation.W) && rotation.LengthSquared() > 0)
            matrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        var positions = data is null ? _ => translation : VectorKeys(data.Translations, translation);
        var scales = data is null ? _ => scale : ScalarKeys(data.Scales, scale);
        Func<float, Matrix4x4> rotations = _ => matrix;
        if (data is { RotationType: KeyType.XYZ_ROTATION_KEY, XYZRotations.Length: 3 })
        {
            var x = ScalarKeys(data.XYZRotations[0], 0); var y = ScalarKeys(data.XYZRotations[1], 0); var z = ScalarKeys(data.XYZRotations[2], 0);
            rotations = time => Matrix4x4.CreateRotationX(x(time)) * Matrix4x4.CreateRotationY(y(time)) * Matrix4x4.CreateRotationZ(z(time));
        }
        else if (data?.QuaternionKeys is { Count: > 0 } keys)
        {
            if (data.RotationType is not (KeyType.LINEAR_KEY or KeyType.CONST_KEY))
                throw new NotSupportedException($"Quaternion interpolation {data.RotationType} is not supported.");
            ValidateTimes(keys.Select(key => key.Time));
            rotations = time => QuaternionMatrix(keys, data.RotationType, time);
        }
        return time => Matrix4x4.CreateScale(scales(time)) * rotations(time) * Matrix4x4.CreateTranslation(positions(time));
    }

    private static Matrix4x4 QuaternionMatrix(List<QuatKey<Quaternion>> keys, KeyType type, float time)
    {
        var index = KeyIndex(keys.Count, index => keys[index].Time, time);
        var rotation = keys[index].Value;
        if (index < keys.Count - 1 && type != KeyType.CONST_KEY)
        {
            var factor = Math.Clamp((time - keys[index].Time) / (keys[index + 1].Time - keys[index].Time), 0, 1);
            rotation = Quaternion.Slerp(rotation, keys[index + 1].Value, factor);
        }
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
    }

    private static Func<float, float> ScalarKeys(KeyGroup<float> group, float fallback)
    {
        if (group.Keys is not { Count: > 0 } keys) return _ => fallback;
        ValidateKeys(group.Interpolation, keys.Select(key => key.Time));
        return time =>
        {
            var index = KeyIndex(keys.Count, index => keys[index].Time, time);
            var first = keys[index];
            if (index == keys.Count - 1 || group.Interpolation == KeyType.CONST_KEY) return first.Value;
            var second = keys[index + 1];
            var x = Math.Clamp((time - first.Time) / (second.Time - first.Time), 0, 1);
            if (group.Interpolation == KeyType.LINEAR_KEY) return float.Lerp(first.Value, second.Value, x);
            var basis = HermiteBasis(x);
            return first.Value * basis.X + second.Value * basis.Y + first.Backward * basis.Z + second.Forward * basis.W;
        };
    }

    private static Func<float, Vector3> VectorKeys(KeyGroup<Vector3> group, Vector3 fallback)
    {
        if (group.Keys is not { Count: > 0 } keys) return _ => fallback;
        ValidateKeys(group.Interpolation, keys.Select(key => key.Time));
        return time =>
        {
            var index = KeyIndex(keys.Count, index => keys[index].Time, time);
            var first = keys[index];
            if (index == keys.Count - 1 || group.Interpolation == KeyType.CONST_KEY) return first.Value;
            var second = keys[index + 1];
            var x = Math.Clamp((time - first.Time) / (second.Time - first.Time), 0, 1);
            if (group.Interpolation == KeyType.LINEAR_KEY) return Vector3.Lerp(first.Value, second.Value, x);
            var basis = HermiteBasis(x);
            return first.Value * basis.X + second.Value * basis.Y + first.Backward * basis.Z + second.Forward * basis.W;
        };
    }

    private static Vector4 HermiteBasis(float x)
    {
        var square = x * x; var cube = square * x;
        return new(2 * cube - 3 * square + 1, 3 * square - 2 * cube, cube - 2 * square + x, cube - square);
    }

    private static int KeyIndex(int count, Func<int, float> keyTime, float time)
    {
        var low = 0; var high = count - 1;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (keyTime(middle) <= time) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    private static void ValidateKeys(KeyType type, IEnumerable<float> times)
    {
        if (type is not (KeyType.LINEAR_KEY or KeyType.QUADRATIC_KEY or KeyType.CONST_KEY))
            throw new NotSupportedException($"Interpolation {type} is not supported.");
        ValidateTimes(times);
    }
    private static void ValidateTimes(IEnumerable<float> times)
    {
        var previous = float.NegativeInfinity;
        foreach (var time in times)
        {
            if (!float.IsFinite(time) || time <= previous) throw new NotSupportedException("Keyframe times must be finite and strictly increasing.");
            previous = time;
        }
    }
    private static bool ValidValue(float value) => float.IsFinite(value) && value != -float.MaxValue;
    private static NotSupportedException Unsupported(NiInterpolator interpolator) => new($"Interpolator {interpolator.GetType().Name} is not supported.");
    internal static Matrix4x4 LocalTransform(NiAVObject node) => Matrix4x4.CreateScale(node.Scale) * RotationMatrix(node.Rotation) * Matrix4x4.CreateTranslation(node.Translation);
    private static Matrix4x4 RotationMatrix(Matrix33 rotation) => new(rotation.M11, rotation.M12, rotation.M13, 0,
        rotation.M21, rotation.M22, rotation.M23, 0, rotation.M31, rotation.M32, rotation.M33, 0, 0, 0, 0, 1);
}
