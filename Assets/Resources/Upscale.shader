// Upscale.shader — the 3D picture (drawn at the reduced render scale, RenderScale.cs) stretched to the native screen in
// one pass, so the HUD and menus can be drawn on top at the panel's own resolution (crisp text) while the 3D keeps
// its reduced pixel count.
//
// "Sharp bilinear": the position between the four nearest source texels is pushed towards the nearer texel (a
// smoothstep on the fraction, _F2UpSharp of the way), so an edge spans less of the stretch than with plain bilinear
// (what the display compositor did before) without the jaggies of nearest-neighbour.
//
// Contrast-adaptive sharpen (clarity round 2, _F2UpCas > 0): a 5-tap cross (the centre + one source texel up, down,
// left, right; 4 extra bilinear reads, all in the same 3x3 texels, so L1-cache hits) folded into the same pass, the
// idea of AMD's FidelityFX CAS: a negative-lobe filter whose weight falls where the local contrast is already high
// (min/max of the cross), so flat detail (distant asphalt, kerb stripes, tree edges, board text) gets back the
// contrast the 1.4x stretch and the MSAA resolve took, while strong edges do not ring or halo. Per channel, ~30 ALU.
// Cost on the Mali-G68 at 1700x765 -> 2400x1080: the pass is bound by its 10 MB screen write (~0.8-1 ms); the
// texture unit goes from ~0.2 to ~0.8 ms in parallel, so the extra is ~0.1-0.4 ms (Docs/PERFORMANCE.md "Clarity").
// _F2UpCas = 0 takes a uniform branch round the 4 reads (the old one-read pass); 1 = the lobe at -1/5.
// (float everywhere: the half-precision rule of Grade.cginc; no step(), no compares on half.)
Shader "Hidden/F2/Upscale"
{
    Properties
    {
        _MainTex ("3D picture", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize; // 1/w, 1/h, w, h
            float _F2UpSharp;          // 0 = plain bilinear .. 1 = full smoothstep (RenderScale.cs sets it)
            float _F2UpCas;            // 0 = off .. 1 = strongest contrast-adaptive sharpen (RenderScale.cs sets it)

            float4 frag (v2f_img i) : SV_Target
            {
                float2 t = i.uv * _MainTex_TexelSize.zw - 0.5;
                float2 b = floor(t);
                float2 f = t - b;
                float2 s = f * f * (3.0 - 2.0 * f);
                f = lerp(f, s, _F2UpSharp);
                float2 uv = (b + 0.5 + f) * _MainTex_TexelSize.xy;
                float3 c = tex2D(_MainTex, uv).rgb;
                if (_F2UpCas > 0.001) // uniform: the whole draw takes one side
                {
                    float2 dx = float2(_MainTex_TexelSize.x, 0.0);
                    float2 dy = float2(0.0, _MainTex_TexelSize.y);
                    float3 n = tex2D(_MainTex, uv - dy).rgb;
                    float3 so = tex2D(_MainTex, uv + dy).rgb;
                    float3 w = tex2D(_MainTex, uv - dx).rgb;
                    float3 e = tex2D(_MainTex, uv + dx).rgb;
                    float3 mn = min(c, min(min(n, so), min(w, e)));
                    float3 mx = max(c, max(max(n, so), max(w, e)));
                    // amplitude: 1 in flat or soft areas, 0 where the cross already spans black..white (no halo)
                    float3 amp = sqrt(saturate(min(mn, 1.0 - mx) / max(mx, 0.0001)));
                    float3 k = -amp * (0.2 * _F2UpCas); // the negative lobe: 0 .. -1/5 (AMD's CAS peak range is -1/8 .. -1/5)
                    c = saturate((c + k * (n + so + w + e)) / (1.0 + 4.0 * k));
                }
                return float4(c, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
