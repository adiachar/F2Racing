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
        _Veil ("Distance veil (fences; needs MSAA)", Range(0,1)) = 0
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
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed _Cutoff;
            float4 _MainTex_TexelSize;
            float _Veil;
            float _F2Msaa; // global, 1 while MSAA is on (TracksideLod.cs); unset = 0

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _Color * 2.0;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                float al = c.a;
                float a = (al - _Cutoff) / max(fwidth(al), 0.0001) + 0.5;
                // fences far away (several texels per pixel): the wire mesh averages out, so its mean alpha goes out as
                // MSAA coverage: a light veil over what is behind, not an opaque sheet and not a hard pop to nothing.
                // (floats only: no compares on half values, see CarPaint)
                float2 tx = i.uv * _MainTex_TexelSize.zw;
                float2 dx = ddx(tx), dy = ddy(tx);
                float rho = max(dot(dx, dx), dot(dy, dy));
                float veil = saturate((rho - 1.5) * 0.4) * _Veil * _F2Msaa;
                a = lerp(a, al, veil);
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
