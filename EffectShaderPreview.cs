using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using Mutagen.Bethesda.Assets.DI;
using Mutagen.Bethesda.Skyrim;
using NiflySharp.Enums;
using DrawingColor = System.Drawing.Color;

namespace AoTPresetEditor;

public sealed record EffectShaderOverlay(int TargetMeshIndex, MeshGeometry3D Geometry, DiffuseMaterialCore Material, bool IsEdge);

public sealed class EffectShaderPreview
{
    private const int MaximumParticles = 2048;
    private const int MaximumPaletteTextureSize = 256;
    private const int ParticlePaletteSteps = 16;
    private const int MaximumShaderTextureBytes = 64 * 1024 * 1024;
    private const double PersistentPreviewSeconds = 5;
    private const double MaximumPreviewSeconds = 120;
    private const float DegreesToRadians = MathF.PI / 180;
    private readonly IEffectShaderGetter shader;
    private readonly List<EffectShaderOverlay> overlays = [];
    private readonly List<SurfaceTriangle> surface = [];
    private readonly PixelTexture? fillTexture;
    private readonly PixelTexture? fillPalette;
    private readonly bool particlePalette;
    private readonly double surfaceArea;
    private Vector4? lastFillColor;
    private float? lastFillAlpha;

    public IReadOnlyList<EffectShaderOverlay> Overlays => overlays;
    public TextureModel? ParticleTexture { get; }
    public double Duration { get; }
    public BoundingBox Bounds { get; private set; }
    public CycleType Cycle => CycleType.CYCLE_LOOP;
    public string Details { get; }

    private sealed record SurfaceTriangle(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, double Area);
    private sealed record PixelTexture(byte[] Pixels, int Width, int Height)
    {
        public TextureModel Model() => new(Pixels, SharpDX.DXGI.Format.B8G8R8A8_UNorm, Width, Height);

        public Vector4 Sample(float x, float y)
        {
            var u = Math.Clamp(x, 0, 1) * (Width - 1); var v = Math.Clamp(y, 0, 1) * (Height - 1);
            var left = (int)u; var top = (int)v;
            Vector4 Pixel(int px, int py)
            {
                var index = (py * Width + px) * 4;
                return new(Pixels[index + 2] / 255f, Pixels[index + 1] / 255f, Pixels[index] / 255f, Pixels[index + 3] / 255f);
            }
            return Vector4.Lerp(Vector4.Lerp(Pixel(left, top), Pixel(Math.Min(left + 1, Width - 1), top), u - left),
                Vector4.Lerp(Pixel(left, Math.Min(top + 1, Height - 1)), Pixel(Math.Min(left + 1, Width - 1), Math.Min(top + 1, Height - 1)), u - left), v - top);
        }
    }

    public static EffectShaderPreview Load(IEffectShaderGetter shader, ModelPreviewResult target, IAssetProvider assets,
        CancellationToken token) => new(shader, target, assets, token);

    private EffectShaderPreview(IEffectShaderGetter shader, ModelPreviewResult target, IAssetProvider assets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        this.shader = shader;
        ValidateValues(shader);
        var limits = new List<string> { "Playback repeats for this preview. Alpha blending approximates Skyrim's authored blend modes." };
        var paletteFill = Has(EffectShader.Flag.MembraneGrayscaleColor | EffectShader.Flag.MembraneGrayscaleAlpha);
        if (!Has(EffectShader.Flag.NoMembraneShader))
        {
            fillTexture = LoadPixels(shader.FillTexture?.DataRelativePath.Path, assets, paletteFill, limits, token);
            if (paletteFill) fillPalette = LoadPixels(shader.MembranePaletteTexture?.DataRelativePath.Path, assets, false, limits, token);
            var fillSupported = (string.IsNullOrWhiteSpace(shader.FillTexture?.DataRelativePath.Path) || fillTexture is not null)
                && (!paletteFill || fillTexture is not null && fillPalette is not null);
            if (!fillSupported) limits.Add("An authored membrane texture is unavailable; its fill is not shown.");
            if (fillTexture is not null && !paletteFill && Has(EffectShader.Flag.TextureEffectIgnoreAlpha))
                for (var pixel = 3; pixel < fillTexture.Pixels.Length; pixel += 4) fillTexture.Pixels[pixel] = byte.MaxValue;
            for (var index = 0; index < target.Meshes.Count; index++)
            {
                var mesh = target.Meshes[index];
                if (!mesh.Visible) continue;
                if (fillSupported && (CanShowEnvelope(shader.FillAlphaFadeInTime, shader.FillFullAlphaTime, shader.FillFadeOutTime,
                    shader.FillFullAlphaRatio, shader.FillPersistentAlphaRatio, shader.FillAlphaPulseAmplitude, shader.FillAlphaPulseFrequency)
                    || Has(EffectShader.Flag.MembraneGrayscaleAlpha)))
                    overlays.Add(new(index, Geometry(mesh.Geometry), new()
                    {
                        DiffuseMap = fillTexture?.Model(), DiffuseColor = new Color4(1, 1, 1, 0),
                        EnableUnLit = !Has(EffectShader.Flag.TextureEffectLighting)
                    }, false));
                if (CanShowEnvelope(shader.EdgeEffectAlphaFadeInTime, shader.EdgeEffectFullAlphaTime, shader.EdgeEffectAlphaFadeOutTime,
                    shader.EdgeEffectFullAlphaRatio, shader.EdgeEffectPersistentAlphaRatio, shader.EdgeEffectAlphaPulseAmplitude,
                    shader.EdgeEffectAlphaPulseFrequency) && !Has(EffectShader.Flag.EdgeEffectInverse))
                    overlays.Add(new(index, Geometry(mesh.Geometry), new() { EnableUnLit = true, VertexColorBlendingFactor = 1 }, true));
            }
            if (overlays.Any(overlay => overlay.IsEdge)) limits.Add("Rim lighting is approximated at mesh vertices.");
            if (paletteFill) limits.Add("The fill palette is sampled at reduced texture resolution; fill and rim are composited separately.");
            if (Has(EffectShader.Flag.EdgeEffectInverse)) limits.Add("Subtractive rim lighting is not shown.");
        }
        if (!Has(EffectShader.Flag.NoParticleShader))
        {
            var paletteParticle = Has(EffectShader.Flag.ParticleGrayscaleColor | EffectShader.Flag.ParticleGrayscaleAlpha);
            var texture = LoadPixels(shader.ParticleShaderTexture?.DataRelativePath.Path, assets, paletteParticle, limits, token);
            var palette = paletteParticle ? LoadPixels(shader.ParticlePaletteTexture?.DataRelativePath.Path, assets, false, limits, token) : null;
            if (texture is not null && (!paletteParticle || palette is not null) && MaximumLifetime > 0 &&
                (shader.ParticleFullBirthRatio > 0 || shader.ParticlePeristentCount > 0) &&
                (shader.ParticleScaleKey1 > 0 || shader.ParticleScaleKey2 > 0) && CanShowParticleAlpha())
            {
                particlePalette = paletteParticle;
                ParticleTexture = paletteParticle ? ParticlePalette(texture, palette!, token).Model() : texture.Model();
                surfaceArea = BuildSurface(target, token);
                SetParticleBounds();
                limits.Add("Particles sample the item's static triangle surface; Skyrim's emitter placement and density can differ.");
                if (particlePalette) limits.Add($"Particle palettes use {ParticlePaletteSteps} samples across each particle's life.");
                if (shader.ParticleFullBirthRatio * MaximumLifetime > MaximumParticles || shader.ParticlePeristentCount * MaximumLifetime > MaximumParticles)
                    limits.Add($"The preview shows at most {MaximumParticles} particles.");
            }
            else if (paletteParticle && texture is not null && palette is null) limits.Add("The particle palette texture is missing; its particles are not shown.");
            if (Has(EffectShader.Flag.ParticleAnimated)) limits.Add("Animated particle atlas timing is not supported; authored atlas frames are selected at birth.");
        }
        if (!string.IsNullOrWhiteSpace(shader.HolesTexture?.DataRelativePath.Path)) limits.Add("Holes and object dissolution are not shown.");
        if (!shader.AddonModels.FormKey.IsNull) limits.Add("Debris addon models are not emitted.");
        if (shader.ExplosionWindSpeed != 0) limits.Add("Explosion wind is not simulated.");
        if (Has(EffectShader.Flag.TextureEffectProjectUVs)) limits.Add("Projected texture coordinates are not supported; model UVs are used.");
        if (Has(EffectShader.Flag.AffectSkinOnly | EffectShader.Flag.TextureEffectNoWeapons | EffectShader.Flag.UseBloodGeometry))
            limits.Add("Skin, weapon, and blood-geometry restrictions are not simulated.");
        if (!Has(EffectShader.Flag.IgnoreBaseGeometryAlpha)) limits.Add("The base model's texture alpha does not mask the effect.");
        if (shader.MembraneZTest is EffectShader.ZTest.AlwaysShow or EffectShader.ZTest.GreaterThan or EffectShader.ZTest.GreaterThanOrEqualTo ||
            shader.ParticleZTest is EffectShader.ZTest.AlwaysShow or EffectShader.ZTest.GreaterThan or EffectShader.ZTest.GreaterThanOrEqualTo)
            limits.Add("Through-object depth tests are not simulated.");
        var transition = new[] { shader.FillAlphaFadeInTime + shader.FillFullAlphaTime + shader.FillFadeOutTime,
            shader.EdgeEffectAlphaFadeInTime + shader.EdgeEffectFullAlphaTime + shader.EdgeEffectAlphaFadeOutTime,
            shader.ParticleBirthRampUpTime + shader.ParticleFullBirthTime + shader.ParticleBirthRampDownTime,
            shader.FillColorKey1Time, shader.FillColorKey2Time, shader.FillColorKey3Time }.Max();
        var persistent = shader.FillPersistentAlphaRatio > 0 || shader.EdgeEffectPersistentAlphaRatio > 0 || shader.ParticlePeristentCount > 0;
        var duration = transition + MaximumLifetime + (persistent ? PersistentPreviewSeconds : 0);
        Duration = Math.Clamp(duration, 1, MaximumPreviewSeconds);
        if (duration > MaximumPreviewSeconds) limits.Add($"Playback shows the first {MaximumPreviewSeconds} seconds before repeating.");
        Details = string.Join("\n", limits.Distinct());
        if (overlays.Count == 0 && (ParticleTexture is null || surfaceArea <= 0 ||
            shader.ParticleScaleKey1 <= 0 && shader.ParticleScaleKey2 <= 0))
            throw new InvalidDataException("This effect shader has no supported visible effect on the selected item. " + Details);
    }

    public void Evaluate(double seconds, Vector3 cameraPosition)
    {
        var time = PreviewTime(seconds);
        var fillAlpha = Envelope(time, shader.FillAlphaFadeInTime, shader.FillFullAlphaTime, shader.FillFadeOutTime,
            shader.FillFullAlphaRatio, shader.FillPersistentAlphaRatio, shader.FillAlphaPulseAmplitude, shader.FillAlphaPulseFrequency);
        var edgeAlpha = Envelope(time, shader.EdgeEffectAlphaFadeInTime, shader.EdgeEffectFullAlphaTime, shader.EdgeEffectAlphaFadeOutTime,
            shader.EdgeEffectFullAlphaRatio, shader.EdgeEffectPersistentAlphaRatio, shader.EdgeEffectAlphaPulseAmplitude, shader.EdgeEffectAlphaPulseFrequency);
        var fillColor = FillColor(time);
        TextureModel? paletteTexture = null;
        if (fillTexture is not null && fillPalette is not null && (lastFillColor != fillColor || lastFillAlpha != fillAlpha))
        {
            paletteTexture = ApplyPalette(fillTexture, fillPalette, Has(EffectShader.Flag.MembraneGrayscaleColor),
                Has(EffectShader.Flag.MembraneGrayscaleAlpha), fillColor, fillAlpha, shader.ColorScale,
                Has(EffectShader.Flag.TextureEffectIgnoreAlpha)).Model();
            lastFillColor = fillColor; lastFillAlpha = fillAlpha;
        }
        foreach (var overlay in overlays)
        {
            if (!overlay.IsEdge)
            {
                if (paletteTexture is not null) overlay.Material.DiffuseMap = paletteTexture;
                var mapped = fillPalette is not null;
                var color = mapped ? new Vector4(1, 1, 1, 1) : fillTexture is null ? fillColor : new Vector4(fillColor.W, fillColor.W, fillColor.W, 1);
                color.W = mapped ? 1 : fillAlpha;
                overlay.Material.DiffuseColor = ToColor(color);
                overlay.Material.UVTransform = new UVTransform(0, new(shader.FillTextureScaleU, shader.FillTextureScaleV),
                    new((float)(time * shader.FillTextureAnimationSpeedU % 1), (float)(time * shader.FillTextureAnimationSpeedV % 1)));
            }
            else
            {
                var positions = overlay.Geometry.Positions!; var normals = overlay.Geometry.Normals!;
                var colors = overlay.Geometry.Colors ??= new(Enumerable.Repeat(new Color4(), positions.Count));
                var color = Color(shader.EdgeEffectColor);
                for (var index = 0; index < colors.Count; index++)
                {
                    var direction = cameraPosition - positions[index];
                    var normal = normals[index];
                    var cosine = direction.LengthSquared() > 0 ? Vector3.Dot(normal, Vector3.Normalize(direction)) : 1;
                    var rim = MathF.Pow(1 - Math.Clamp(cosine, 0, 1), Math.Max(shader.EdgeEffectFallOff, 0));
                    colors[index] = new(color.X, color.Y, color.Z, Math.Clamp(rim * edgeAlpha, 0, 1));
                }
                overlay.Geometry.UpdateColors();
            }
        }
    }

    public IReadOnlyList<ModelPreviewParticle> EvaluateParticles(double seconds, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var time = PreviewTime(seconds);
        if (ParticleTexture is null || surfaceArea <= 0 || MaximumLifetime <= 0) return [];
        var first = (long)Math.Floor(Births(Math.Max(0, time - MaximumLifetime))) + 1;
        var last = (long)Math.Floor(Births(time));
        first = Math.Max(first, last - MaximumParticles + 1);
        var particles = new List<ModelPreviewParticle>();
        for (var index = first; index <= last; index++)
        {
            token.ThrowIfCancellationRequested();
            var low = Math.Max(0, time - MaximumLifetime); var high = time;
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var middle = (low + high) / 2;
                if (Births(middle) < index) low = middle; else high = middle;
            }
            var random = new Random(unchecked((int)index));
            var life = shader.ParticleLifetime + Symmetric(random) * shader.ParticleLifetimePlusMinus;
            var age = (float)(time - (low + high) / 2);
            if (life <= 0 || age < 0 || age >= life) continue;
            var triangle = SurfaceAt(random.NextDouble() * surfaceArea);
            var u = MathF.Sqrt(random.NextSingle()); var v = random.NextSingle();
            var origin = triangle.A * (1 - u) + triangle.B * (u * (1 - v)) + triangle.C * (u * v);
            origin += triangle.Normal * (shader.BirthPositionOffset + Symmetric(random) * shader.BirthPositionOffsetRangePlusMinus);
            var velocity = triangle.Normal * (shader.ParticleInitialSpeedAlongNormal + Symmetric(random) * shader.ParticleInitialSpeedAlongNormalPlusMinus)
                + new Vector3(shader.ParticleInitialVelocity1, shader.ParticleInitialVelocity2, shader.ParticleInitialVelocity3);
            var acceleration = triangle.Normal * shader.ParticleAccelerationAlongNormal
                + new Vector3(shader.ParticleAcceleration1, shader.ParticleAcceleration2, shader.ParticleAcceleration3);
            var position = origin + velocity * age + acceleration * (age * age / 2);
            var progress = Math.Clamp(age / life, 0, 1);
            var scale = ParticleScale(progress);
            var color = particlePalette ? Vector4.One : ParticleColor(progress);
            var rotation = (shader.ParticleInitialRotationDegree + Symmetric(random) * shader.ParticleInitialRotationDegreePlusMinus
                + age * (shader.ParticleRotationSpeedDegreePerSec + Symmetric(random) * shader.ParticleRotationSpeedDegreePerSecPlusMinus)) * DegreesToRadians;
            var columns = Math.Max(shader.TextureCountU, 1); var rows = Math.Max(shader.TextureCountV, 1);
            var frame = (uint)random.Next((int)(columns * rows));
            var slice = particlePalette ? Math.Clamp((int)MathF.Round(progress * (ParticlePaletteSteps - 1)), 0, ParticlePaletteSteps - 1) : 0;
            var slices = particlePalette ? ParticlePaletteSteps : 1;
            var uv = new Vector4((float)(frame % columns) / columns, ((float)(frame / columns) / rows + slice) / slices,
                (float)(frame % columns + 1) / columns, ((float)(frame / columns + 1) / rows + slice) / slices);
            particles.Add(new(position, Math.Max(scale, 0) / 2, rotation, color, uv));
        }
        return particles;
    }

    private double MaximumLifetime => Math.Max(0, shader.ParticleLifetime + Math.Abs(shader.ParticleLifetimePlusMinus));
    private bool Has(EffectShader.Flag flag) => (shader.Flags & flag) != 0;
    private double PreviewTime(double seconds) => double.IsFinite(seconds) ? Math.Max(0, seconds) % Duration : 0;

    private static MeshGeometry3D Geometry(MeshGeometry3D source) => new()
    {
        Positions = source.Positions, Normals = source.Normals, Indices = source.Indices, TextureCoordinates = source.TextureCoordinates
    };

    private double BuildSurface(ModelPreviewResult target, CancellationToken token)
    {
        var area = 0d;
        foreach (var mesh in target.Meshes.Where(mesh => mesh.Visible))
        {
            var geometry = mesh.Geometry;
            var positions = geometry.Positions!; var indices = geometry.Indices!;
            for (var index = 0; index < indices.Count; index += 3)
            {
                token.ThrowIfCancellationRequested();
                var a = positions[indices[index]];
                var b = positions[indices[index + 1]]; var c = positions[indices[index + 2]];
                var normal = Vector3.Cross(b - a, c - a); var size = normal.Length();
                if (size <= 0) continue;
                area += size / 2;
                surface.Add(new(a, b, c, normal / size, area));
            }
        }
        return area;
    }

    private void SetParticleBounds()
    {
        if (surface.Count == 0) return;
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        var spriteReach = Math.Max(Math.Max(shader.ParticleScaleKey1, shader.ParticleScaleKey2), 0) / 2d * Math.Sqrt(2);
        var velocity = new Vector3(shader.ParticleInitialVelocity1, shader.ParticleInitialVelocity2, shader.ParticleInitialVelocity3);
        var acceleration = new Vector3(shader.ParticleAcceleration1, shader.ParticleAcceleration2, shader.ParticleAcceleration3);
        var offsetRange = Math.Abs(shader.BirthPositionOffsetRangePlusMinus);
        var speedRange = Math.Abs(shader.ParticleInitialSpeedAlongNormalPlusMinus);
        foreach (var triangle in surface)
        {
            var normal = triangle.Normal;
            var start1 = normal * (shader.BirthPositionOffset - offsetRange); var start2 = normal * (shader.BirthPositionOffset + offsetRange);
            var speed1 = velocity + normal * (shader.ParticleInitialSpeedAlongNormal - speedRange);
            var speed2 = velocity + normal * (shader.ParticleInitialSpeedAlongNormal + speedRange);
            var speedMinimum = Vector3.Min(speed1, speed2); var speedMaximum = Vector3.Max(speed1, speed2);
            var force = acceleration + normal * shader.ParticleAccelerationAlongNormal;
            var startMinimum = Vector3.Min(triangle.A, Vector3.Min(triangle.B, triangle.C)) + Vector3.Min(start1, start2);
            var startMaximum = Vector3.Max(triangle.A, Vector3.Max(triangle.B, triangle.C)) + Vector3.Max(start1, start2);
            var low = new Vector3(
                BoundCoordinate(startMinimum.X + MotionExtremum(speedMinimum.X, force.X, minimum: true) - spriteReach),
                BoundCoordinate(startMinimum.Y + MotionExtremum(speedMinimum.Y, force.Y, minimum: true) - spriteReach),
                BoundCoordinate(startMinimum.Z + MotionExtremum(speedMinimum.Z, force.Z, minimum: true) - spriteReach));
            var high = new Vector3(
                BoundCoordinate(startMaximum.X + MotionExtremum(speedMaximum.X, force.X, minimum: false) + spriteReach),
                BoundCoordinate(startMaximum.Y + MotionExtremum(speedMaximum.Y, force.Y, minimum: false) + spriteReach),
                BoundCoordinate(startMaximum.Z + MotionExtremum(speedMaximum.Z, force.Z, minimum: false) + spriteReach));
            minimum = Vector3.Min(minimum, low); maximum = Vector3.Max(maximum, high);
        }
        Bounds = new(minimum, maximum);
    }

    private double MotionExtremum(float velocity, float acceleration, bool minimum)
    {
        var end = velocity * MaximumLifetime + acceleration * MaximumLifetime * MaximumLifetime / 2;
        var result = minimum ? Math.Min(0, end) : Math.Max(0, end);
        var turningTime = acceleration == 0 ? -1 : -(double)velocity / acceleration;
        if (turningTime > 0 && turningTime < MaximumLifetime)
        {
            var turning = velocity * turningTime + acceleration * turningTime * turningTime / 2;
            result = minimum ? Math.Min(result, turning) : Math.Max(result, turning);
        }
        return result;
    }

    private static float BoundCoordinate(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > float.MaxValue)
            throw new InvalidDataException("The particle motion exceeds the preview bounds range.");
        return (float)value;
    }

    private SurfaceTriangle SurfaceAt(double area)
    {
        var low = 0; var high = surface.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (surface[middle].Area < area) low = middle + 1; else high = middle;
        }
        return surface[low];
    }

    private double Births(double seconds)
    {
        var up = Math.Max(shader.ParticleBirthRampUpTime, 0); var full = Math.Max(shader.ParticleFullBirthTime, 0);
        var down = Math.Max(shader.ParticleBirthRampDownTime, 0); var rate = Math.Max(shader.ParticleFullBirthRatio, 0);
        var persistent = Math.Max(shader.ParticlePeristentCount, 0);
        var total = 0d;
        var t = Math.Min(seconds, up);
        if (up > 0) total += rate * t * t / (2 * up);
        seconds = Math.Max(0, seconds - up); t = Math.Min(seconds, full); total += rate * t;
        seconds = Math.Max(0, seconds - full); t = Math.Min(seconds, down);
        if (down > 0) total += rate * t + (persistent - rate) * t * t / (2 * down);
        return total + persistent * Math.Max(0, seconds - down);
    }

    private Vector4 FillColor(double seconds)
    {
        var color = Keys(seconds, [shader.FillColorKey1Time, shader.FillColorKey2Time, shader.FillColorKey3Time],
            [Color(shader.FillColorKey1) * shader.FillColorKey1Scale, Color(shader.FillColorKey2) * shader.FillColorKey2Scale,
                Color(shader.FillColorKey3) * shader.FillColorKey3Scale], false);
        color.W = Keys(seconds, [shader.FillColorKey1Time, shader.FillColorKey2Time, shader.FillColorKey3Time],
            [new(shader.FillColorKey1Scale), new(shader.FillColorKey2Scale), new(shader.FillColorKey3Scale)], false).X;
        return color;
    }

    private Vector4 ParticleColor(float progress) => Keys(progress, [shader.ColorKey1Time, shader.ColorKey2Time, shader.ColorKey3Time],
        [WithAlpha(Color(shader.ColorKey1), shader.ColorKey1Alpha), WithAlpha(Color(shader.ColorKey2), shader.ColorKey2Alpha),
            WithAlpha(Color(shader.ColorKey3), shader.ColorKey3Alpha)], true);

    private bool CanShowParticleAlpha()
    {
        if (Has(EffectShader.Flag.ParticleGrayscaleAlpha)) return true;
        if (shader.ColorKey1Time == shader.ColorKey2Time && shader.ColorKey2Time == shader.ColorKey3Time)
            return shader.ColorKey1Alpha > 0;
        return shader.ColorKey1Alpha > 0 || shader.ColorKey2Alpha > 0 || shader.ColorKey3Alpha > 0 ||
            shader.ColorKey1Time > 0 || shader.ColorKey3Time < 1;
    }

    private float ParticleScale(float progress) => Keys(progress, [0, shader.ParticleScaleKey1Time, shader.ParticleScaleKey2Time, 1],
        [Vector4.Zero, new(shader.ParticleScaleKey1), new(shader.ParticleScaleKey2), Vector4.Zero], false).X;

    private static Vector4 Keys(double time, float[] times, Vector4[] values, bool rawOutside)
    {
        if (times.All(value => value == times[0])) return values[0];
        if (time <= times[0]) return rawOutside && times[0] > 0 ? Vector4.Lerp(Vector4.One, values[0], (float)(time / times[0])) : values[0];
        for (var index = 1; index < times.Length; index++)
            if (time <= times[index]) return Vector4.Lerp(values[index - 1], values[index], Fraction((float)time, times[index - 1], times[index]));
        return rawOutside && times[^1] < 1 ? Vector4.Lerp(values[^1], Vector4.One, Fraction((float)time, times[^1], 1)) : values[^1];
    }

    private static float Envelope(double time, float fadeIn, float fullTime, float fadeOut, float full, float persistent, float amplitude, float frequency)
    {
        var alpha = time < fadeIn ? full * Fraction((float)time, 0, fadeIn) : time < fadeIn + fullTime ? full :
            float.Lerp(full, persistent, Fraction((float)time, fadeIn + fullTime, fadeIn + fullTime + fadeOut));
        return Math.Clamp(alpha + amplitude * (float)Math.Sin(time * Math.Tau * frequency), 0, 1);
    }

    private static bool CanShowEnvelope(float fadeIn, float fullTime, float fadeOut, float full, float persistent, float amplitude, float frequency)
        => persistent > 0 || full > 0 && (fadeIn > 0 || fullTime > 0 || fadeOut > 0) || amplitude != 0 && frequency != 0;

    private PixelTexture ParticlePalette(PixelTexture source, PixelTexture palette, CancellationToken token)
    {
        var height = checked(source.Height * ParticlePaletteSteps);
        var pixels = new byte[checked(source.Width * height * 4)];
        for (var step = 0; step < ParticlePaletteSteps; step++)
        {
            token.ThrowIfCancellationRequested();
            var color = ParticleColor((float)step / (ParticlePaletteSteps - 1));
            var mapped = ApplyPalette(source, palette, Has(EffectShader.Flag.ParticleGrayscaleColor), Has(EffectShader.Flag.ParticleGrayscaleAlpha),
                color, color.W, shader.ColorScale, false);
            Buffer.BlockCopy(mapped.Pixels, 0, pixels, step * mapped.Pixels.Length, mapped.Pixels.Length);
        }
        return new(pixels, source.Width, height);
    }

    private static PixelTexture ApplyPalette(PixelTexture source, PixelTexture palette, bool grayscaleColor, bool grayscaleAlpha,
        Vector4 color, float alpha, float scale, bool ignoreAlpha)
    {
        var pixels = new byte[source.Pixels.Length];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            // CommunityShaders Effect.hlsl: palette X uses green for color, alpha for opacity;
            // palette Y uses membrane PropertyColor.r or particle vertex color.r, and evaluated alpha.
            var grayColor = source.Pixels[index + 1] / 255f; var grayAlpha = source.Pixels[index + 3] / 255f;
            var rgb = grayscaleColor ? palette.Sample(grayColor, color.X) :
                new Vector4(source.Pixels[index + 2] / 255f * color.X, grayColor * color.Y, source.Pixels[index] / 255f * color.Z, 1);
            var opacity = grayscaleAlpha ? palette.Sample(grayAlpha, alpha).W : alpha * (ignoreAlpha ? 1 : grayAlpha);
            pixels[index] = Byte(rgb.Z * scale); pixels[index + 1] = Byte(rgb.Y * scale); pixels[index + 2] = Byte(rgb.X * scale); pixels[index + 3] = Byte(opacity);
        }
        return new(pixels, source.Width, source.Height);
    }

    private static PixelTexture? LoadPixels(string? path, IAssetProvider assets, bool reduced, List<string> limits, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            BitmapSource bitmap = ModelPreview.LoadTexture(assets, path, token);
            if (reduced && Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) > MaximumPaletteTextureSize)
            {
                var scale = (double)MaximumPaletteTextureSize / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
                bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            }
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var length = checked(converted.PixelWidth * converted.PixelHeight * 4);
            if (length > MaximumShaderTextureBytes) throw new InvalidDataException("The shader texture exceeds the preview memory limit.");
            var pixels = new byte[length]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            return new(pixels, converted.PixelWidth, converted.PixelHeight);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { limits.Add($"{path}: {error.Message}"); return null; }
    }

    private static void ValidateValues(IEffectShaderGetter shader)
    {
        foreach (var property in typeof(IEffectShaderGetter).GetProperties().Where(property => property.PropertyType == typeof(float)))
            if (!float.IsFinite((float)property.GetValue(shader)!)) throw new InvalidDataException($"{property.Name} is not finite.");
        if ((ulong)Math.Max(shader.TextureCountU, 1) * Math.Max(shader.TextureCountV, 1) > int.MaxValue)
            throw new InvalidDataException("The particle atlas has too many frames.");
        if (Math.Max(shader.ParticleFullBirthRatio, shader.ParticlePeristentCount) * MaximumPreviewSeconds > long.MaxValue)
            throw new InvalidDataException("The particle emission rate exceeds the preview range.");
    }

    private static Vector4 Color(DrawingColor color) => new(color.R / 255f, color.G / 255f, color.B / 255f, 1);
    private static Vector4 WithAlpha(Vector4 color, float alpha) { color.W = alpha; return color; }
    private static Color4 ToColor(Vector4 color) => new(color.X, color.Y, color.Z, color.W);
    private static byte Byte(float value) => (byte)MathF.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);
    private static float Fraction(float value, float start, float end) => end <= start ? 1 : Math.Clamp((value - start) / (end - start), 0, 1);
    private static float Symmetric(Random random) => random.NextSingle() * 2 - 1;
}
