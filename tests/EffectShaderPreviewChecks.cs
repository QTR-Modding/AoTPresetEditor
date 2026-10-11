using System.IO;
using System.Numerics;
using System.Reflection;
using AoTPresetEditor;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Assets.DI;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using DrawingColor = System.Drawing.Color;

internal static class EffectShaderPreviewChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AoTEffectShaderPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "textures"));
        try
        {
            WriteTexture(Path.Combine(folder, "textures", "fill.dds"), [0x8000FF00u, 0x8000FF00u, 0x8000FF00u, 0x8000FF00u]);
            WriteTexture(Path.Combine(folder, "textures", "palette.dds"), [0x00000000u, 0x00FF0000u, 0xFF0000FFu, 0xFF00FF00u]);
            WriteTexture(Path.Combine(folder, "textures", "opaque-palette.dds"), [0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu]);
            var assets = new GameAssets(folder, []);
            CatalogSnapshot(folder);
            Membrane(assets);
            Palettes(assets);
            Particles(assets);
            UnavailableAndCancellation(assets);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void CatalogSnapshot(string folder)
    {
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Shaders.esp"), SkyrimRelease.SkyrimSE);
        var shader = mod.EffectShaders.AddNew(); shader.FillTexture = "fill.dds"; shader.FillAlphaFadeInTime = 2;
        var path = Path.Combine(folder, mod.ModKey.FileName.String); mod.WriteToBinary(path);
        var rows = new Dictionary<FormKey, FormRow>();
        using (var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE))
            typeof(FormCatalog).GetMethod("AddRecords", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [overlay, overlay.ModKey.FileName.String, rows]);
        var snapshot = rows[shader.FormKey].EffectShader;
        Require(snapshot?.FillAlphaFadeInTime == 2 && snapshot.FillTexture?.DataRelativePath.Path.EndsWith("fill.dds") == true,
            "Effect shader metadata must remain readable after the catalog's plugin overlay is disposed.");
        shader.FillAlphaFadeInTime = 9;
        Require(snapshot!.FillAlphaFadeInTime == 2, "A loaded shader preview must retain its own authored record snapshot.");
    }

    private static void Membrane(IAssetProvider assets)
    {
        var shader = Shader(); shader.Flags = EffectShader.Flag.NoParticleShader;
        shader.FillAlphaFadeInTime = 1; shader.FillFullAlphaTime = 1; shader.FillFadeOutTime = 1;
        shader.FillFullAlphaRatio = 1; shader.FillPersistentAlphaRatio = .25f;
        shader.FillColorKey1 = DrawingColor.Red; shader.FillColorKey2 = DrawingColor.Blue; shader.FillColorKey3 = DrawingColor.Blue;
        shader.FillColorKey1Time = 0; shader.FillColorKey2Time = 1; shader.FillColorKey3Time = 2;
        shader.FillTextureAnimationSpeedU = .5f; shader.FillTextureAnimationSpeedV = -.25f;
        shader.FillTextureScaleU = 2; shader.FillTextureScaleV = 3;
        var target = Target(); var preview = EffectShaderPreview.Load(shader, target, assets, CancellationToken.None);
        var material = preview.Overlays.Single().Material;
        preview.Evaluate(.5, new(0, 0, 10));
        Require(Near(material.DiffuseColor.Alpha, .5f) && Near(material.DiffuseColor.Red, .5f) && Near(material.DiffuseColor.Blue, .5f),
            "Membrane opacity and authored color keys must visibly interpolate during fade-in.");
        Require(material.UVTransform.Scaling == new Vector2(2, 3) && material.UVTransform.Translation == new Vector2(.25f, -.125f),
            "Authored membrane UV scales and signed scroll speeds must reach the rendered material.");
        preview.Evaluate(2.5, new(0, 0, 10)); Require(Near(material.DiffuseColor.Alpha, .625f), "Fade-out must approach persistent opacity.");
        preview.Evaluate(0, new(0, 0, 10)); preview.Evaluate(1.5, new(0, 0, 10));
        Require(Near(material.DiffuseColor.Alpha, 1) && Near(material.DiffuseColor.Blue, 1), "Seeking through zero opacity must restore the authored fill.");
        Require(target.Meshes[0].Geometry.Colors is null, "Shader playback must not overwrite the item's own vertex colors.");
        shader.FillAlphaPulseAmplitude = .1f; shader.FillAlphaPulseFrequency = 1;
        var pulse = EffectShaderPreview.Load(shader, target, assets, CancellationToken.None);
        pulse.Evaluate(3.25, new(0, 0, 10)); var first = pulse.Overlays[0].Material.DiffuseColor.Alpha;
        pulse.Evaluate(3.75, new(0, 0, 10)); Require(first > pulse.Overlays[0].Material.DiffuseColor.Alpha, "Authored alpha pulse must animate persistent fill.");
        shader.FillFullAlphaRatio = shader.FillPersistentAlphaRatio = shader.FillAlphaPulseAmplitude = 0;
        shader.EdgeEffectFullAlphaRatio = 1; shader.EdgeEffectFullAlphaTime = 2; shader.EdgeEffectFallOff = 2; shader.EdgeEffectColor = DrawingColor.Green;
        var edge = EffectShaderPreview.Load(shader, target, assets, CancellationToken.None);
        edge.Evaluate(.5, new(0, 0, 10)); var facing = edge.Overlays.Single().Geometry.Colors![0].Alpha;
        edge.Evaluate(.5, new(10, 0, 0));
        Require(facing == 0 && edge.Overlays.Single().Geometry.Colors![0].Alpha > .9f && edge.Details.Contains("mesh vertices"),
            "The supported rim must respond to viewing direction and disclose its vertex approximation.");
        edge.Evaluate(.5, new(0, 0, -10));
        Require(edge.Overlays.Single().Geometry.Colors![0].Alpha > .9f,
            "Back-facing rim calculations must use signed normal/view direction, matching the authored shader equation.");
    }

    private static void Palettes(IAssetProvider assets)
    {
        var shader = Shader(); shader.Flags = EffectShader.Flag.NoParticleShader | EffectShader.Flag.MembraneGrayscaleColor | EffectShader.Flag.MembraneGrayscaleAlpha;
        shader.FillTexture = "fill.dds"; shader.MembranePaletteTexture = "palette.dds";
        shader.FillAlphaFadeInTime = 1; shader.FillFullAlphaTime = 2; shader.FillFullAlphaRatio = 1;
        var preview = EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None);
        preview.Evaluate(.5, new(0, 0, 10)); var pixels = Pixels(preview.Overlays.Single().Material.DiffuseMap!);
        Require(pixels[2] == 255 && pixels[1] == 0 && pixels[0] == 0 && Math.Abs(pixels[3] - 128) <= 1,
            "Membrane color lookup must use texture green/key red, while opacity uses texture alpha/evaluated alpha.");
        preview.Evaluate(1.5, new(0, 0, 10)); Require(Pixels(preview.Overlays.Single().Material.DiffuseMap!)[3] == 255,
            "The membrane palette's opacity row must follow animated alpha rather than a fixed guessed row.");
        shader.Flags = EffectShader.Flag.NoParticleShader | EffectShader.Flag.TextureEffectIgnoreAlpha;
        preview = EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None);
        Require(Pixels(preview.Overlays.Single().Material.DiffuseMap!)[3] == 255, "Ignore texture alpha must also work without a palette.");
        shader = ParticleShader(); shader.Flags |= EffectShader.Flag.ParticleGrayscaleColor | EffectShader.Flag.ParticleGrayscaleAlpha;
        shader.ParticlePaletteTexture = "palette.dds"; shader.ColorKey1 = shader.ColorKey2 = shader.ColorKey3 = DrawingColor.Black;
        shader.ColorKey1Alpha = shader.ColorKey2Alpha = shader.ColorKey3Alpha = .5f;
        preview = EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None);
        pixels = Pixels(preview.ParticleTexture!);
        Require(pixels[2] == 255 && pixels[1] == 0 && Math.Abs(pixels[3] - 128) <= 1,
            "Particle color and alpha palettes must use their separate authored channels and life keys.");
    }

    private static void Particles(IAssetProvider assets)
    {
        var shader = ParticleShader(); shader.ParticleInitialVelocity3 = 2; shader.ParticleAcceleration3 = 2;
        shader.ParticleRotationSpeedDegreePerSec = 90;
        var preview = EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None);
        var first = preview.EvaluateParticles(.65); var later = preview.EvaluateParticles(.75);
        Require(first.Count == 6 && later.Count == 7 && preview.EvaluateParticles(.65).SequenceEqual(first),
            "Authored particle birth rate must produce deterministic emission when seeking the playback slider.");
        Require(later[0].Position.Z > first[0].Position.Z && later[0].Radius > first[0].Radius && later[0].Rotation > first[0].Rotation,
            "Particle velocity, acceleration, scale and rotation keys must visibly animate emitted particles.");
        Require(first.All(particle => particle.Color.X > particle.Color.Z && particle.Color.W > 0 && Contains(preview.Bounds, particle)),
            "Particle colors must follow their life keys and authored bounds must include every billboard corner.");
        Require(preview.Bounds.Maximum.Z > preview.Bounds.Maximum.X && preview.Bounds.Minimum.Z > -4,
            "Particle framing must follow the authored upward trajectory instead of adding its whole travel distance to every axis.");
        shader.ParticleAcceleration3 = -2;
        var turning = EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None);
        Require(Near(turning.Bounds.Maximum.Z, 1 + 2 * MathF.Sqrt(2)),
            "Particle framing must include an interior trajectory turning point, not only its start and end positions.");
        shader.ParticleBirthRampUpTime = 2; shader.ParticleFullBirthRatio = 8;
        var ramp = EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None);
        Require(ramp.EvaluateParticles(1).Count == 2, "Birth ramp-up must integrate the increasing emission rate.");
        shader.ParticleBirthRampUpTime = 0; shader.ParticleFullBirthRatio = 10000;
        var capped = EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None);
        Require(capped.EvaluateParticles(1).Count <= 2048 && capped.Details.Contains("2048"), "Particle resource limits must be enforced and disclosed.");
    }

    private static void UnavailableAndCancellation(IAssetProvider assets)
    {
        var shader = Shader(); shader.Flags = EffectShader.Flag.NoMembraneShader | EffectShader.Flag.NoParticleShader;
        Unavailable(() => EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None));
        shader.Flags = EffectShader.Flag.NoParticleShader; shader.FillFullAlphaRatio = 1; shader.FillTexture = "missing.dds";
        Unavailable(() => EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None));
        shader.FillTexture = "fill.dds"; shader.Flags |= EffectShader.Flag.MembraneGrayscaleColor; shader.MembranePaletteTexture = "missing.dds";
        Unavailable(() => EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None));
        var noSize = ParticleShader(); noSize.ParticleScaleKey1 = noSize.ParticleScaleKey2 = 0;
        Unavailable(() => EffectShaderPreview.Load(noSize, Target(), assets, CancellationToken.None));
        var noAlpha = ParticleShader(); noAlpha.ColorKey1Alpha = noAlpha.ColorKey2Alpha = noAlpha.ColorKey3Alpha = 0;
        Unavailable(() => EffectShaderPreview.Load(noAlpha, Target(), assets, CancellationToken.None));
        noAlpha.ColorKey1Time = .1f;
        var rawInterval = EffectShaderPreview.Load(noAlpha, Target(), assets, CancellationToken.None);
        Require(rawInterval.EvaluateParticles(.65).Any(particle => particle.Color.W > 0),
            "Zero-alpha keys must preserve a visible raw-texture interval before the first life key.");
        noAlpha.ColorKey1Time = 0; noAlpha.Flags |= EffectShader.Flag.ParticleGrayscaleAlpha; noAlpha.ParticlePaletteTexture = "opaque-palette.dds";
        var particlePalette = EffectShaderPreview.Load(noAlpha, Target(), assets, CancellationToken.None);
        Require(Pixels(particlePalette.ParticleTexture!)[3] == 255, "A particle alpha palette can remain opaque with all life-key alphas zero.");
        var noDuration = Shader(); noDuration.Flags = EffectShader.Flag.NoParticleShader; noDuration.FillFullAlphaRatio = 1;
        Unavailable(() => EffectShaderPreview.Load(noDuration, Target(), assets, CancellationToken.None));
        noDuration.FillFullAlphaRatio = 0; noDuration.EdgeEffectFullAlphaRatio = 1;
        Unavailable(() => EffectShaderPreview.Load(noDuration, Target(), assets, CancellationToken.None));
        noDuration.EdgeEffectFullAlphaRatio = 0; noDuration.FillAlphaPulseAmplitude = .5f;
        Unavailable(() => EffectShaderPreview.Load(noDuration, Target(), assets, CancellationToken.None));
        noDuration.FillAlphaPulseFrequency = 1;
        var pulse = EffectShaderPreview.Load(noDuration, Target(), assets, CancellationToken.None); pulse.Evaluate(.25, new(0, 0, 10));
        Require(pulse.Overlays.Single().Material.DiffuseColor.Alpha > 0, "An active alpha pulse must remain available without a full-opacity phase.");
        noDuration.FillAlphaPulseAmplitude = 0; noDuration.FillTexture = "fill.dds"; noDuration.MembranePaletteTexture = "opaque-palette.dds";
        noDuration.Flags |= EffectShader.Flag.MembraneGrayscaleAlpha;
        var fillPalette = EffectShaderPreview.Load(noDuration, Target(), assets, CancellationToken.None); fillPalette.Evaluate(.5, new(0, 0, 10));
        Require(Pixels(fillPalette.Overlays.Single().Material.DiffuseMap!)[3] == 255,
            "A membrane alpha palette can remain opaque when its entire authored alpha envelope is zero.");
        shader.FillAlphaFadeInTime = float.NaN;
        try { EffectShaderPreview.Load(shader, Target(), assets, CancellationToken.None); throw new InvalidOperationException("Nonfinite shader metadata was accepted."); }
        catch (InvalidDataException) { }
        try { EffectShaderPreview.Load(Shader(), Target(), assets, new CancellationToken(true)); throw new InvalidOperationException("Shader cancellation was ignored."); }
        catch (OperationCanceledException) { }
    }

    private static EffectShader Shader()
    {
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Fixture.esp"), SkyrimRelease.SkyrimSE);
        var shader = mod.EffectShaders.AddNew(); shader.FillColorKey1Scale = shader.FillColorKey2Scale = shader.FillColorKey3Scale = shader.ColorScale = 1;
        shader.FillTextureScaleU = shader.FillTextureScaleV = 1;
        return shader;
    }

    private static EffectShader ParticleShader()
    {
        var shader = Shader(); shader.Flags = EffectShader.Flag.NoMembraneShader; shader.ParticleShaderTexture = "fill.dds";
        shader.ParticleFullBirthTime = 10; shader.ParticleFullBirthRatio = 10; shader.ParticleLifetime = 2;
        shader.ParticleScaleKey1 = 2; shader.ParticleScaleKey2 = 4; shader.ParticleScaleKey1Time = .25f; shader.ParticleScaleKey2Time = 1;
        shader.ColorKey1 = DrawingColor.Red; shader.ColorKey2 = DrawingColor.Purple; shader.ColorKey3 = DrawingColor.Blue;
        shader.ColorKey1Alpha = shader.ColorKey2Alpha = shader.ColorKey3Alpha = 1;
        shader.ColorKey1Time = 0; shader.ColorKey2Time = .5f; shader.ColorKey3Time = 1;
        return shader;
    }

    private static ModelPreviewResult Target() => new([new(new MeshGeometry3D
    {
        Positions = new([Vector3.Zero, Vector3.UnitX, Vector3.UnitY]), Normals = new([Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ]),
        Indices = new([0, 1, 2]), TextureCoordinates = new([Vector2.Zero, Vector2.UnitX, Vector2.UnitY])
    }, new PhongMaterialCore(), false, false)], "Target");

    private static byte[] Pixels(TextureModel texture) => texture.TextureInfoLoader.Load(texture.Guid).TextureRaw!;
    private static bool Contains(HelixToolkit.Maths.BoundingBox bounds, ModelPreviewParticle particle)
    {
        var minimum = particle.Position - new Vector3(particle.Radius * MathF.Sqrt(2));
        var maximum = particle.Position + new Vector3(particle.Radius * MathF.Sqrt(2));
        return Vector3.Max(minimum, bounds.Minimum) == minimum && Vector3.Min(maximum, bounds.Maximum) == maximum;
    }
    private static bool Near(float first, float second) => Math.Abs(first - second) < .001f;
    private static void Unavailable(Func<EffectShaderPreview> load)
    {
        try { load(); throw new InvalidOperationException("An unavailable effect silently displayed only its target."); }
        catch (InvalidDataException error) { Require(error.Message.Contains("no supported visible effect"), "An unavailable effect must explain why it cannot be shown."); }
    }

    private static void WriteTexture(string path, uint[] colors)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x20534444u); writer.Write(124u); writer.Write(0x100Fu); writer.Write(2u); writer.Write(2u);
        writer.Write(8u); writer.Write(0u); writer.Write(0u);
        for (var index = 0; index < 11; index++) writer.Write(0u);
        writer.Write(32u); writer.Write(0x41u); writer.Write(0u); writer.Write(32u);
        writer.Write(0x00FF0000u); writer.Write(0x0000FF00u); writer.Write(0x000000FFu); writer.Write(0xFF000000u);
        writer.Write(0x1000u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        foreach (var color in colors) writer.Write(color);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
