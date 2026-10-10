using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using SharpDX.D3DCompiler;

namespace AoTPresetEditor;

public static class ModelPreviewParticleShader
{
    private const int MaximumTextureDimension = 1024;
    private const int PaletteRegions = 2;
    private const int BytesPerPixel = 4;

    public static (TextureModel Texture, int Bytes, bool Reduced) PackPalette(BitmapSource source,
        BitmapSource palette, long availableBytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var originalWidth = Math.Max(source.PixelWidth, palette.PixelWidth);
        var originalHeight = Math.Max(source.PixelHeight, palette.PixelHeight);
        var reduction = Math.Min(1d, (double)MaximumTextureDimension / Math.Max(originalWidth, originalHeight));
        var width = Math.Max(1, (int)(originalWidth * reduction));
        var height = Math.Max(1, (int)(originalHeight * reduction));
        var bytes = checked(width * height * PaletteRegions * BytesPerPixel);
        if (bytes > availableBytes) throw new InvalidDataException("The particle palette exceeds the preview texture memory limit.");
        var pixels = new byte[bytes];
        CopyRegion(source, 0); CopyRegion(palette, height);
        return (new TextureModel(pixels, SharpDX.DXGI.Format.B8G8R8A8_UNorm, width, height * PaletteRegions), bytes, reduction < 1);

        void CopyRegion(BitmapSource bitmap, int row)
        {
            token.ThrowIfCancellationRequested();
            if (bitmap.PixelWidth != width || bitmap.PixelHeight != height)
                bitmap = new TransformedBitmap(bitmap, new ScaleTransform((double)width / bitmap.PixelWidth, (double)height / bitmap.PixelHeight));
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(pixels, width * BytesPerPixel, row * width * BytesPerPixel);
        }
    }

    public static DefaultEffectsManager CreateEffectsManager()
    {
        var manager = new DefaultEffectsManager();
        try
        {
            // Helix 3.1.2's image billboard pass ignores vertex color. Keep its geometry and
            // transparency passes, replacing their pixel shaders so tint, fades and palettes work.
            var technique = manager[DefaultRenderTechniqueNames.BillboardText]!.Description;
            manager.RemoveTechnique(technique.Name);
            foreach (var pass in technique.PassDescriptions!)
            {
                var entry = pass.Name switch
                {
                    DefaultPassNames.Default => "main",
                    DefaultPassNames.OITPass => "weighted",
                    DefaultPassNames.OITDepthPeeling => "depthPeel",
                    DefaultPassNames.OITDepthPeelingInit => "depthInit",
                    _ => null
                };
                if (entry is null) continue;
                using var compiled = ShaderBytecode.Compile(Shader, entry, "ps_5_0", ShaderFlags.OptimizationLevel3);
                var shader = new HelixToolkit.SharpDX.Shaders.ShaderDescription("AoTParticles_" + entry, ShaderStage.Pixel, new ShaderReflector(), compiled.Bytecode.Data);
                pass.ShaderList = pass.ShaderList!.Select(existing => existing.ShaderType == ShaderStage.Pixel ? shader : existing).ToArray();
            }
            manager.AddTechnique(technique);
            return manager;
        }
        catch { manager.Dispose(); throw; }
    }

    private const string Shader = """
        struct Input {
            float4 p : SV_POSITION; float4 foreground : COLOR; float4 background : COLOR1;
            noperspective float2 t : TEXCOORD; float4 vEye : POSITION0;
        };
        cbuffer cbPointLineModel : register(b4) {
            float4x4 mWorld; bool bHasInstances; bool bHasInstanceParams; float2 padding1;
            float4 pfParams; float4 pColor; bool fixedSize; bool3 pbParams;
            bool enableDistanceFading; float fadeNearDistance; float fadeFarDistance; bool bHasTexture;
            float pTextureScale; float pAlphaThreshold; bool pEnableBlending; float pBlendingFactor;
        };
        cbuffer cbTransforms : register(b0) {
            float4x4 mView; float4x4 mProjection; float4x4 mViewProjection;
            float4 vFrustum; float4 vViewport; float4 vResolution;
            float3 vEyePos; bool SSAOEnabled; float SSAOBias; float SSAOIntensity; float TimeStamp; bool IsPerspective;
            float OITPower; float OITSlope; int OITWeightMode; float DpiScale;
        };
        Texture2D billboardTexture : register(t0);
        SamplerState samplerBillboard : register(s7);
        Texture2D<float2> tDepthBlender : register(t100);

        float4 main(Input input) : SV_Target {
            uint width, height; billboardTexture.GetDimensions(width, height);
            float2 halfTexel = 0.5 / float2(width, height);
            if ((int)pfParams.x != 4) {
                float value = billboardTexture.Sample(samplerBillboard, input.t).r;
                float4 text = lerp(input.background, input.foreground, value);
                clip(text.a - 0.1); return text;
            }
            bool colorPalette = input.background.y != 0;
            bool alphaPalette = input.background.z != 0;
            bool palette = colorPalette || alphaPalette;
            uint clampMode = (uint)input.background.w;
            float2 uv = float2((clampMode & 2) ? frac(input.t.x) : saturate(input.t.x),
                              (clampMode & 1) ? frac(input.t.y) : saturate(input.t.y));
            float sourceHeight = palette ? 0.5 : 1;
            uv.y *= sourceHeight;
            uv = clamp(uv, halfTexel, float2(1, sourceHeight) - halfTexel);
            float4 source = billboardTexture.Sample(samplerBillboard, uv);
            float3 rgb = source.rgb * input.foreground.rgb;
            float alpha = source.a * input.foreground.a;
            // Skyrim Effect.hlsl: green selects palette color, source alpha selects
            // palette alpha; unscaled BaseColor*vertex color supplies each lookup's Y.
            if (colorPalette) {
                float2 lookup = float2(saturate(source.g), saturate(input.foreground.r));
                lookup.y = 0.5 + lookup.y * 0.5;
                lookup = clamp(lookup, float2(halfTexel.x, 0.5 + halfTexel.y), 1 - halfTexel);
                rgb = billboardTexture.Sample(samplerBillboard, lookup).rgb;
            }
            if (alphaPalette) {
                float2 lookup = float2(saturate(source.a), saturate(input.foreground.a));
                lookup.y = 0.5 + lookup.y * 0.5;
                lookup = clamp(lookup, float2(halfTexel.x, 0.5 + halfTexel.y), 1 - halfTexel);
                alpha = billboardTexture.Sample(samplerBillboard, lookup).a;
            }
            clip(alpha <= 0 ? -1 : 1);
            return float4(rgb * input.background.x, alpha);
        }
        float4 depthInit(Input input) : SV_Target {
            main(input); return float4(-input.p.z, input.p.z, 0, 0);
        }
        struct Peeling { float2 Depths : SV_Target0; float4 FrontColor : SV_Target1; float4 BackColor : SV_Target2; };
        Peeling depthPeel(Input input) {
            float4 color = main(input);
            Peeling output = (Peeling)0;
            output.Depths = tDepthBlender.Load(int3(input.p.xy, 0));
            float nearDepth = -output.Depths.x; float farDepth = output.Depths.y;
            if (input.p.z < nearDepth || input.p.z > farDepth) {
                if (all(output.Depths == -1)) discard;
                output.Depths = -1; return output;
            }
            if (input.p.z > nearDepth && input.p.z < farDepth) {
                output.Depths = float2(-input.p.z, input.p.z); return output;
            }
            output.Depths = -1;
            if (input.p.z == nearDepth) { color.rgb *= color.a; output.FrontColor = color; }
            else output.BackColor = color;
            return output;
        }
        struct Weighted { float4 color : SV_Target0; float4 alpha : SV_Target1; };
        Weighted weighted(Input input) {
            float4 color = main(input); float weight = 1; float z = input.vEye.w - vFrustum.z;
            if (OITWeightMode == 0) weight = max(0.01, min(3000, 100 / (0.00001 + pow(abs(z) / 5, abs(OITPower)) + pow(abs(z) / 200, abs(OITPower) * 2))));
            else if (OITWeightMode == 1) weight = max(0.01, min(3000, 100 / (0.00001 + pow(abs(z) / 10, abs(OITPower)) + pow(abs(z) / 200, abs(OITPower) * 2))));
            else if (OITWeightMode == 2) weight = max(0.01, min(3000, 0.3 / (0.00001 + pow(abs(z) / 200, abs(OITPower)))));
            else if (OITWeightMode == 3) weight = max(0.01, 3000 * pow(clamp(1 - input.p.z * max(OITSlope, 1), 0, 1), abs(OITPower)));
            Weighted output = (Weighted)0;
            output.color = float4(color.rgb * color.a, color.a) * (color.a * weight);
            output.alpha.a = color.a; return output;
        }
        """;
}
