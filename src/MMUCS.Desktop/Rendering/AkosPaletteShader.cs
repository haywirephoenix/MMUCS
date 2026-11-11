using System;
using MMUCS.Core.Diagnostics;
using SkiaSharp;

namespace MMUCS.Desktop.Rendering;

/// <summary>
/// SkSL port of the legacy akos.gdshader. The "data" image encodes, per pixel:
///   R = global palette index / 255, G = 1 for shadow indices (1..7), A = 0 for transparent.
/// The shader resolves the colour through a 256x1 palette strip, applying shadow/alpha options,
/// with optional manual bilinear filtering in *decoded* colour space (filtering indices directly
/// would blend unrelated palette entries).
/// </summary>
public static class AkosPaletteShader
{
    private const string Sksl = @"
uniform shader data;
uniform shader palette;
uniform float2 texSize;
uniform float alphaEnabled;
uniform float shadowColorEnabled;
uniform float shadowAmount;
uniform float linearFilter;

half4 decodeAt(float2 ip) {
    ip = clamp(ip, float2(0.0), texSize - 1.0);
    half4 d = data.eval(ip + 0.5);
    if (d.a < 0.5) {
        return alphaEnabled > 0.5 ? half4(0.0) : half4(0.0, 0.0, 0.0, 1.0);
    }
    float idx = floor(d.r * 255.0 + 0.5);
    bool isShadow = d.g > 0.5;
    half3 base = palette.eval(float2(idx + 0.5, 0.5)).rgb;
    half3 rgb = (shadowColorEnabled > 0.5 && isShadow) ? half3(0.0) : base;
    half a = (alphaEnabled > 0.5 && shadowColorEnabled > 0.5 && isShadow) ? half(shadowAmount) : half(1.0);
    return half4(rgb * a, a); // premultiplied
}

half4 main(float2 p) {
    if (linearFilter > 0.5) {
        float2 sp = p - 0.5;
        float2 i = floor(sp);
        float2 f = fract(sp);
        half4 c00 = decodeAt(i);
        half4 c10 = decodeAt(i + float2(1.0, 0.0));
        half4 c01 = decodeAt(i + float2(0.0, 1.0));
        half4 c11 = decodeAt(i + float2(1.0, 1.0));
        return mix(mix(c00, c10, half(f.x)), mix(c01, c11, half(f.x)), half(f.y));
    }
    return decodeAt(floor(p));
}";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() =>
    {
        var effect = SKRuntimeEffect.CreateShader(Sksl, out var errors);
        if (effect == null) Log.Error($"AKOS SkSL compile failed: {errors}");
        return effect;
    });

    public static bool IsAvailable => Effect.Value != null;

    public readonly record struct Options(bool AlphaEnabled, bool ShadowColorEnabled, float ShadowAmount, bool LinearFilter);

    public static SKShader Create(SKImage dataImage, SKImage paletteStrip, in Options o)
    {
        var effect = Effect.Value;
        if (effect == null || dataImage == null || paletteStrip == null) return null;

        var nearest = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        var builder = new SKRuntimeShaderBuilder(effect);
        builder.Children["data"] = dataImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest);
        builder.Children["palette"] = paletteStrip.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest);
        builder.Uniforms["texSize"] = new[] { (float)dataImage.Width, (float)dataImage.Height };
        builder.Uniforms["alphaEnabled"] = o.AlphaEnabled ? 1f : 0f;
        builder.Uniforms["shadowColorEnabled"] = o.ShadowColorEnabled ? 1f : 0f;
        builder.Uniforms["shadowAmount"] = o.ShadowAmount;
        builder.Uniforms["linearFilter"] = o.LinearFilter ? 1f : 0f;
        return builder.Build();
    }
}
