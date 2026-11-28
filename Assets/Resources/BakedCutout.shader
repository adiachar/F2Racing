// See-through world surfaces (debris and catch fences) with lighting baked into the vertex colours, like Baked.shader:
// one texture read and an alpha test per pixel, both faces drawn, fog kept. Trackside.cs swaps fence materials to it
// after LightBake has lit them; the tree cards fall back to it (Foliage.shader). Edges: alpha to coverage (MSAA)
// on an alpha sharpened to a one-pixel ramp at the cut-off; without MSAA the clip keeps the hard cut-out.
Shader "F2/BakedCutout"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.5
        _Veil ("Distance veil (fences)", Range(0,1)) = 0
        _Mean ("Mean coverage of the wire mesh (fences)", Range(0,1)) = 0.45
    }
    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" "IgnoreProjector"="True" }
        LOD 100
        Cull Off
        AlphaToMask On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile __ F2_GRADE_LOW // Low graphics level: cheap grade (Grade.cginc)
            #pragma multi_compile __ FOG_LINEAR // the game only uses linear fog (Sky.cs): 2 variants, not 4 (multi_compile_fog)
            #include "UnityCG.cginc"
            #include "Grade.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : TEXCOORD1;
                UNITY_FOG_COORDS(2)
                float dist : TEXCOORD3; // view depth (m): the fence veil also grows with distance
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed _Cutoff;
            float4 _MainTex_TexelSize;
            float _Veil, _Mean;
            float _F2Msaa; // global, 1 while MSAA is on (TracksideLod.cs); unset = 0

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _Color * 2.0;
                o.dist = -UnityObjectToViewPos(v.vertex.xyz).z;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                float al = c.a;
                float a = (al - _Cutoff) / max(fwidth(al), 0.0001) + 0.5;
                // fences (_Veil 1): where the wire pattern gets near the pixel size (moire, crawl at speed) or far away
                // the mesh fades to one flat strip of its mean coverage (_Mean) in the wire's tint: with MSAA that is
                // the alpha-to-coverage output (an even veil), without it a fixed screen dither of the same mean (no
                // texture-locked pattern left to alias). Anisotropic: the longer screen axis of the texel footprint
                // (grazing TV views). Floats only (no half compares). The top cable is in the texture: the same.
                float2 tx = i.uv * _MainTex_TexelSize.zw;
                float2 dx = ddx(tx), dy = ddy(tx);
                float rho = max(dot(dx, dx), dot(dy, dy));                     // (texels per pixel)^2
                float veil = max(saturate((rho - 1.0) * 0.5), saturate((i.dist - 22.0) / 38.0)) * _Veil;
                float2 sp = i.pos.xy;
                float ign = frac(52.9829189 * frac(dot(sp, float2(0.06711056, 0.00583715))));
                float ign2 = frac(52.9829189 * frac(dot(sp.yx + float2(17.0, 5.0), float2(0.06711056, 0.00583715))));
                float aW = saturate(a);
                float aMs = lerp(aW, _Mean, veil);                               // MSAA: coverage
                float aNo = lerp(step(0.5, aW), step(ign, _Mean), step(ign2, veil)); // no MSAA: 0 / 1 per pixel
                float useMs = step(0.5, _F2Msaa) * step(0.5, _Veil);
                a = lerp(lerp(a, aNo, _Veil), aMs, useMs);
                clip(a - 0.02);
                c.a = saturate(a);
                c.rgb *= i.color.rgb;
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
