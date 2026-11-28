// Foliage.cginc — the tree shading shared by Foliage.shader (merged card trees and model trees) and FoliageInst.shader
// (GPU-instanced model trees and shrubs, ModelInstancing.cs). Moved here unchanged from Foliage.shader so both draw
// exactly the same pixels; see Foliage.shader for what each step does.
// F2_FOLIAGE_INST (defined by FoliageInst.shader before the include): v2f also carries the instance's LOD cross-fade.
#ifndef F2_FOLIAGE_INCLUDED
#define F2_FOLIAGE_INCLUDED
#include "UnityCG.cginc"
#include "Grade.cginc"

struct v2f
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    half4 color : TEXCOORD1; // rgb baked light x tint, a = back-light
    UNITY_FOG_COORDS(2)
#if defined(F2_FOLIAGE_INST)
    float fade : TEXCOORD3;  // LOD cross-fade (ModelInstancing): 1 = whole, f = the first f of the dither, -f = the last f
#endif
};
sampler2D _MainTex;
float4 _MainTex_TexelSize;
fixed _Cutoff;
float4 _Wind, _SunDir, _Trans;

// how much the view looks into the sun (0 .. strength): a narrow lobe round the sun's direction
half BackLight(float3 wp)
{
    half b = saturate(dot(normalize(wp - _WorldSpaceCameraPos.xyz), _SunDir.xyz) * 1.4 - 0.4);
    return b * b * _Trans.w;
}

// world position wp (unswayed), baked colour col (stored halved), sway weight / phase, sway on (1) or off (0)
void FoliageVert(float4 wp, float3 col, float swayW, float phase, float sway, inout v2f o)
{
    // colour drift inside a crown, in ~2.5 m patches of the (unswayed) position: yellower and bluer clumps, a
    // little lighter and darker, so a crown is not one flat green (per vertex: no cost per pixel)
    float hv = frac(sin(dot(floor(wp.xyz * 0.4), float3(12.9898, 78.233, 37.719))) * 43758.5453);
    half3 drift = lerp(half3(0.93, 0.99, 1.08), half3(1.09, 1.04, 0.86), (half)hv) * (half)(0.92 + 0.16 * frac(hv * 7.31));
    // two sines at unrelated rates per tree (phase), plus a slow gust travelling across the land
    float t = _Time.y * _Wind.w + phase;
    float gust = 0.6 + 0.4 * sin(_Time.y * 0.35 + wp.x * 0.013 + wp.z * 0.009);
    float s = (sin(t) + 0.45 * sin(t * 2.31 + 1.7)) * gust * swayW * _Wind.z * sway;
    wp.xz += _Wind.xy * s;
    o.pos = mul(UNITY_MATRIX_VP, wp);
    o.color = half4(col * 2.0 * lerp(half3(1, 1, 1), drift, (half)sway), BackLight(wp.xyz)); // stored light 0.5 = full brightness (LightBake)
    UNITY_TRANSFER_FOG(o, o.pos);
}

// lit leaf colour: baked light, plus the light through the leaves when looking towards the sun (the brighter,
// outer leaves of the painted crown are the thin ones: they glow, the dark interior does not)
half3 Shade (fixed4 t, half4 col)
{
    half thin = saturate(dot(t.rgb, half3(0.3, 0.59, 0.11)) * 7.0 - 0.15);
    return t.rgb * (col.rgb + _Trans.rgb * (col.a * thin));
}

// Medium / High pixel: alpha to coverage with the mid-distance cut-off (see Foliage.shader)
half4 FoliageFragHi(v2f i)
{
    float tpp = max(fwidth(i.uv.x) * _MainTex_TexelSize.z, fwidth(i.uv.y) * _MainTex_TexelSize.w); // texels per pixel
    // near crowns (about 1-2 texels per pixel: the LOD0 leaf clumps up close) read +0.6 mip: the painted leaves' texel
    // noise sparkled light/dark and shimmered at speed. Fades out by 3 texels per pixel, so the mid / far crowns (and
    // their cut-off below) are as before. Same single read. (No negative bias: cut-outs would crawl.)
    fixed4 t = tex2Dbias(_MainTex, float4(i.uv, 0.0, 0.6 * saturate(3.0 - tpp)));
    // mid distance: the mips round a crown off into a smooth blob. Raise the cut-off where the (mipped)
    // painted crown is dark - the shaded gaps between leaf clumps - and lower it on the lit clumps, once the
    // texture is minified: the rim breaks into lobes and the gaps open to the sky. Tied to the texture (no
    // crawling), no extra read; the solid interior (alpha near 1) is untouched. Floats: no compares.
    float mid = saturate(tpp - 1.0) * saturate(3.0 - tpp * 0.08); // from ~2 texels per pixel, gone at the far LODs
    float cut = _Cutoff;
    // beyond ~140 m (view depth = 1 / the fragment's pos.w) the cards are a few pixels: no mid-distance cut-off shaping there
    // (a uniform-ish branch: depth varies smoothly across a quad; no derivatives inside it)
    if (i.pos.w > 0.0072)
    {
        float lum = saturate(dot((float3)t.rgb, float3(0.3, 0.59, 0.11)) * 4.0);
        cut += (0.62 - lum) * 0.34 * mid;
    }
    // alpha sharpened to a one-pixel ramp centred on the cut-off: the coverage of each MSAA sample
    float a = (t.a - cut) / max(fwidth((float)t.a), 0.0001) + 0.5; // (float: clip() is a compare)
    clip(a);
    half4 c = half4(Shade(t, i.color), saturate(a));
    UNITY_APPLY_FOG(i.fogCoord, c);
    c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
    return c;
}

// Low pixel: positive mip bias, hard cut-out
half4 FoliageFragLow(v2f i)
{
    fixed4 t = tex2Dbias(_MainTex, float4(i.uv, 0, 0.5));
    clip(t.a - _Cutoff);
    half4 c = half4(Shade(t, i.color), 1);
    UNITY_APPLY_FOG(i.fogCoord, c);
    c.rgb = F2Grade(c.rgb);
    return c;
}
#endif
