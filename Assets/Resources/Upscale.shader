// Upscale.shader — the 3D picture (drawn at the reduced render scale, RenderScale.cs) stretched to the native screen in
// one pass, so the HUD and menus can be drawn on top at the panel's own resolution (crisp text) while the 3D keeps
// its reduced pixel count.
//
// One texture read per screen pixel. "Sharp bilinear": the position between the four nearest source texels is pushed
// towards the nearer texel (a smoothstep on the fraction, _F2UpSharp of the way), so an edge spans less of the stretch
// than with plain bilinear (what the display compositor did before) without the jaggies of nearest-neighbour. At
// 0.7-0.85 scale (1.2-1.4x stretch) this reads as a crisper image at the same cost as a plain copy (~6 extra ALU).
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

            fixed4 frag (v2f_img i) : SV_Target
            {
                float2 t = i.uv * _MainTex_TexelSize.zw - 0.5;
                float2 b = floor(t);
                float2 f = t - b;
                float2 s = f * f * (3.0 - 2.0 * f);
                f = lerp(f, s, _F2UpSharp);
                return tex2D(_MainTex, (b + 0.5 + f) * _MainTex_TexelSize.xy);
            }
            ENDCG
        }
    }
    Fallback Off
}
