using System.IO;
using System.Numerics;
using AoTPresetEditor;
using NiflySharp;
using NiflySharp.Bitfields;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

internal static class ModelAnimationChecks
{
    public static void Run()
    {
        SequenceMaterials();
        DirectTiming();
        ParentTransform();
        SerializedRotation();
        UnsupportedKeys();
    }

    private static void SequenceMaterials()
    {
        var (nif, shape, shader) = Fixture();
        var controller = new BSEffectShaderPropertyFloatController
        {
            Target = Pointer(nif, shader), ControlledVariable = EffectShaderControlledVariable.UOffset,
            Flags = new TimeControllerFlags { Active = true, ManagerControlled = true }
        };
        var controllerRef = Reference<NiTimeController>(nif, controller);
        var scroll = new NiFloatInterpolator { Data = Reference(nif, new NiFloatData { Data = new()
        {
            Interpolation = KeyType.QUADRATIC_KEY,
            Keys = [new() { Time = 0, Value = 0, Backward = -1 }, new() { Time = 2, Value = -1, Forward = -1 }]
        } }) };
        var alpha = new BSEffectShaderPropertyFloatController
        {
            Target = Pointer(nif, shader), ControlledVariable = EffectShaderControlledVariable.AlphaTransparency
        };
        var visible = new NiVisController { Target = Pointer(nif, shape) };
        var sequence = new NiControllerSequence
        {
            Name = new("Scroll"), StartTime = 0, StopTime = 2, Frequency = 1, CycleType = CycleType.CYCLE_LOOP,
            ControlledBlocks =
            [
                new() { Controller = controllerRef, Interpolator = Reference<NiInterpolator>(nif, scroll) },
                new() { Controller = Reference<NiTimeController>(nif, alpha), Interpolator = Reference<NiInterpolator>(nif, new NiFloatInterpolator
                    { Data = Reference(nif, new NiFloatData { Data = FloatKeys(KeyType.LINEAR_KEY, 1, 0) }) }) },
                new() { Controller = Reference<NiTimeController>(nif, visible), Interpolator = Reference<NiInterpolator>(nif, new NiBoolInterpolator
                    { Data = Reference(nif, new NiBoolData { Data = new() { Interpolation = KeyType.CONST_KEY,
                        Keys = [new() { Time = 0, Value = 0 }, new() { Time = 1, Value = 1 }, new() { Time = 2, Value = 0 }] } }) }) }
            ]
        };
        nif.AddBlock(sequence);
        nif.AddBlock(new NiControllerSequence
        {
            Name = new("Hold"), StartTime = 3, StopTime = 5, Frequency = 2, CycleType = CycleType.CYCLE_CLAMP,
            ControlledBlocks = [new() { Controller = controllerRef, Interpolator = Reference<NiInterpolator>(nif, new NiFloatInterpolator { Value = .25f }) }]
        });
        var animation = Animate(nif, shape);
        Require(animation.Clips.Count == 2 && animation.Clips[1].Duration == 1, "Named clips retain their authored timing and frequency.");
        var start = animation.Evaluate(0, 0).Single();
        var middle = animation.Evaluate(0, 1).Single();
        Require(Near(start.UVOffset!.Value.X, 0) && Near(middle.UVOffset!.Value.X, -.5f) && Near(middle.Alpha!.Value, .5f),
            "Quadratic tangents and linear alpha tracks must affect the bound shader.");
        Require(!start.Visible && middle.Visible && !animation.Evaluate(0, 2).Single().Visible,
            "Visibility is a step at the authored key time.");
        Require(Near(animation.Evaluate(0, 3).Single().UVOffset!.Value.X, -.5f), "Looping clips wrap after their last frame.");
        var hold = animation.Evaluate(1, 0).Single();
        Require(Near(hold.UVOffset!.Value.X, .25f) && Near(hold.Alpha!.Value, .8f) && hold.Visible,
            "Switching clips restores fields absent from the new clip to their authored base values.");
        var withoutClip = animation.Evaluate(null, 1).Single();
        Require(Near(withoutClip.UVOffset!.Value.X, .1f) && Near(withoutClip.Alpha!.Value, .8f) && withoutClip.Visible,
            "Selecting an unrelated particle clip must restore mesh values instead of leaving the previous mesh clip's pose.");
        Require(Near(shader.UVOffset.U, .1f) && Near(shader.BaseColor.A, .8f), "Evaluation must not mutate the NIF's authored values.");
    }

    private static void DirectTiming()
    {
        var (nif, shape, shader) = Fixture();
        nif.AddBlock(new BSEffectShaderPropertyFloatController
        {
            Target = Pointer(nif, shader), ControlledVariable = EffectShaderControlledVariable.AlphaTransparency,
            StartTime = 2, StopTime = 4, Frequency = 2, Phase = 0,
            Flags = new TimeControllerFlags { Active = true, CycleType = CycleType.CYCLE_CLAMP },
            Interpolator = Reference<NiInterpolator>(nif, new NiFloatInterpolator { Data = Reference(nif, new NiFloatData
            { Data = new() { Interpolation = KeyType.LINEAR_KEY, Keys = [new() { Time = 2, Value = 0 }, new() { Time = 4, Value = 1 }] } }) })
        });
        var animation = Animate(nif, shape);
        Require(Near(animation.Evaluate(0, 0).Single().Alpha!.Value, 0) && Near(animation.Evaluate(0, 1).Single().Alpha!.Value, 0) &&
            Near(animation.Evaluate(0, 1.5).Single().Alpha!.Value, .5f), "Direct controller time uses frequency and phase, without adding start time twice.");
        var controller = nif.Blocks.OfType<BSEffectShaderPropertyFloatController>().Single();
        controller.Flags.CycleType = CycleType.CYCLE_REVERSE;
        animation = Animate(nif, shape);
        Require(Near(animation.Evaluate(0, 2.5).Single().Alpha!.Value, .5f), "Reverse cycles must reflect after the stop time.");
        controller.Flags.PlayBackwards = true;
        controller.Flags.CycleType = CycleType.CYCLE_CLAMP;
        animation = Animate(nif, shape);
        Require(Near(animation.Evaluate(0, 1).Single().Alpha!.Value, 1), "Backward playback starts at the last authored value.");
    }

    private static void ParentTransform()
    {
        var (nif, shape, _) = Fixture();
        shape.Translation = new(2, 0, 0);
        var parent = new NiNode { Name = new("Parent"), Translation = new(10, 0, 0), Scale = 1,
            Children = new([Reference<NiAVObject>(nif, shape)]) };
        nif.AddBlock(parent);
        nif.AddBlock(new NiTransformController
        {
            Target = Pointer(nif, parent), StartTime = 0, StopTime = 2,
            Flags = new TimeControllerFlags { Active = true, CycleType = CycleType.CYCLE_CLAMP },
            Interpolator = Reference<NiInterpolator>(nif, new NiTransformInterpolator
            {
                Transform = new() { Translation = new(-float.MaxValue), Rotation = new(-float.MaxValue, -float.MaxValue, -float.MaxValue, -float.MaxValue), Scale = -float.MaxValue },
                Data = Reference(nif, new NiTransformData { Translations = new()
                {
                    Interpolation = KeyType.LINEAR_KEY, Keys = [new() { Time = 0, Value = new(10, 0, 0) }, new() { Time = 2, Value = new(14, 0, 0) }]
                } })
            })
        });
        var animation = new ModelPreviewAnimation();
        animation.AddPart(nif, [(shape, 0, Matrix4x4.CreateTranslation(12, 0, 0))], new HashSet<string>());
        Require(animation.Evaluate(0, 0).Single().Transform == Matrix4x4.Identity,
            "An unchanged parent transform must produce an identity delta for baked geometry.");
        var point = Vector3.Transform(new(12, 0, 0), animation.Evaluate(0, 1).Single().Transform);
        Require(Vector3.Distance(point, new(14, 0, 0)) < .001f, "Animated ancestor movement must apply exactly once to world-space geometry.");
        var (attachedNif, attachedShape, _) = Fixture();
        attachedShape.Translation = new(3, 0, 0);
        var attachment = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(100, 20, 0);
        animation.AddPart(attachedNif, [(attachedShape, 1, Matrix4x4.CreateTranslation(3, 0, 0) * attachment)], new HashSet<string>());
        var attachedFrame = animation.Evaluate(0, 1).Single(frame => frame.MeshIndex == 1);
        var attachedPoint = new Vector3(100, 23, 0);
        Require(Vector3.Distance(Vector3.Transform(attachedPoint, attachedFrame.Transform), attachedPoint) < .001f,
            "Playing another part's clip must preserve a static mesh's external skeleton attachment.");
    }

    private static void SerializedRotation()
    {
        var nif = new NifFile(NiVersion.GetSSE());
        var shape = new BSTriShape
        {
            Name = new("Rotated"), Scale = 1, IsSkinned = false,
            Rotation = new() { M11 = 0, M12 = 1, M21 = -1, M22 = 0, M33 = 1 }
        };
        nif.AddBlock(shape);
        shape.Controller = Reference<NiTimeController>(nif, new NiTransformController
        {
            Target = Pointer(nif, shape), StartTime = 0, StopTime = 1, Frequency = 1,
            Flags = new TimeControllerFlags { Active = true, CycleType = CycleType.CYCLE_CLAMP },
            Interpolator = Reference<NiInterpolator>(nif, new NiTransformInterpolator
            {
                Transform = new() { Translation = Vector3.Zero, Scale = 1,
                    Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) }
            })
        });
        using var bytes = new MemoryStream();
        Require(nif.Save(bytes) == 0, "The rotation fixture must serialize successfully.");
        bytes.Position = 0;
        var reloaded = new NifFile(bytes);
        var loaded = reloaded.Blocks.OfType<BSTriShape>().Single();
        var rotation = loaded.Rotation;
        var initial = new Matrix4x4(rotation.M11, rotation.M12, rotation.M13, 0,
            rotation.M21, rotation.M22, rotation.M23, 0, rotation.M31, rotation.M32, rotation.M33, 0, 0, 0, 0, 1);
        var point = Vector3.Transform(Vector3.UnitX, initial);
        Require(Vector3.Distance(point, Vector3.UnitY) < .001f, "The serialized Matrix33 must retain its authored +90-degree Z rotation.");
        var animation = new ModelPreviewAnimation();
        animation.AddPart(reloaded, [(loaded, 0, initial)], new HashSet<string>());
        var animated = Vector3.Transform(point, animation.Evaluate(0, 0).Single().Transform);
        Require(Vector3.Distance(animated, point) < .001f,
            "A constant quaternion matching the loaded rotation must not change the mesh's orientation when playback begins.");
    }

    private static void UnsupportedKeys()
    {
        var (nif, shape, shader) = Fixture();
        nif.AddBlock(new BSEffectShaderPropertyFloatController
        {
            Target = Pointer(nif, shader), ControlledVariable = EffectShaderControlledVariable.AlphaTransparency,
            StartTime = 0, StopTime = 2, Flags = new TimeControllerFlags { Active = true },
            Interpolator = Reference<NiInterpolator>(nif, new NiFloatInterpolator { Data = Reference(nif, new NiFloatData { Data = FloatKeys(KeyType.TBC_KEY, 0, 1) }) })
        });
        var animation = new ModelPreviewAnimation(); var messages = new HashSet<string>();
        animation.AddPart(nif, [(shape, 0, Matrix4x4.Identity)], messages);
        Require(animation.Clips.Count == 0 && messages.Any(message => message.Contains("TBC_KEY")),
            "Unsupported curves must be reported, not silently evaluated as linear curves.");
    }

    private static (NifFile Nif, BSTriShape Shape, BSEffectShaderProperty Shader) Fixture()
    {
        var nif = new NifFile(NiVersion.GetSSE());
        var shader = new BSEffectShaderProperty { BaseColor = new(1, .5f, .25f, .8f), BaseColorScale = 2,
            UVOffset = new() { U = .1f, V = .2f }, UVScale = new() { U = 1, V = 1 } };
        var shape = new BSTriShape { Name = new("Mesh"), Scale = 1, IsSkinned = false, ShaderPropertyRef = Reference<BSShaderProperty>(nif, shader) };
        nif.AddBlock(shape);
        return (nif, shape, shader);
    }
    private static ModelPreviewAnimation Animate(NifFile nif, INiShape shape)
    {
        var animation = new ModelPreviewAnimation(); var messages = new HashSet<string>();
        animation.AddPart(nif, [(shape, 0, Matrix4x4.Identity)], messages);
        Require(messages.Count == 0, string.Join("; ", messages));
        return animation;
    }
    private static KeyGroup<float> FloatKeys(KeyType type, float first, float second) => new()
    { Interpolation = type, Keys = [new() { Time = 0, Value = first }, new() { Time = 2, Value = second }] };
    private static NiBlockRef<T> Reference<T>(NifFile nif, T value) where T : INiObject => new(nif.GetBlockIndex(value, out var index) ? index : nif.AddBlock(value));
    private static NiBlockPtr<NiObjectNET> Pointer(NifFile nif, NiObjectNET value) => new(nif.GetBlockIndex(value, out var index) ? index : nif.AddBlock(value));
    private static bool Near(float value, float expected) => Math.Abs(value - expected) < .0001f;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
