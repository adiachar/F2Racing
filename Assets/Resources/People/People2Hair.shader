// People2Hair (People.cs): the hair cards of the realistic people (Rocketbox fans / officials / marshals: People2 pack
// v2, the second sub-mesh of a body's mesh; only the near LODs have it). Same atlas as People2.shader; in the card
// islands its alpha is the hair's coverage: alpha clip at 0.5, both sides drawn (the cards are single planes), no tint.
// Lit per vertex like People2 (the cars' sun with a little wrap from either side of the card, the sky's SH, a faint sheen),
// fog and the colour grade. Separate material, so the clip() never touches the bodies' opaque pass (Mali early-Z).
// Precision: no comparisons, no step() (GLES3 / Vulkan rule); the fragment maths in float.
Shader "F2/People2Hair"
{
    Properties { _MainTex ("Atlas", 2D) = "white" {} }
    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile __ F2_GRADE_LOW
            #include "UnityCG.cginc"
            #include "../Grade.cginc"
            #include "Lighting.cginc"

            sampler2D _MainTex;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f
            {
                float4 pos : SV_POSITION; float2 uv : TEXCOORD0;
                float3 lit : TEXCOORD1; float3 add : TEXCOORD2;
                UNITY_FOG_COORDS(3)
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                float3 n = normalize(UnityObjectToWorldNormal(v.normal));
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float3 V = normalize(_WorldSpaceCameraPos.xyz - wp);
                n = dot(n, V) >= 0.0 ? n : -n; // (the side facing the camera; float maths)
                float diff = saturate((dot(n, L) + 0.35) / 1.35);
                float3 amb = ShadeSH9(float4(n, 1.0));
                float fr = 1.0 - saturate(dot(n, V)); fr *= fr;
                o.lit = diff * _LightColor0.rgb * 0.9 + amb * 1.1;
                o.add = fr * 0.05 * amb;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float4 t = tex2D(_MainTex, i.uv);
                clip(t.a - 0.5);
                fixed4 c = fixed4(t.rgb * i.lit + i.add, 1);
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb);
                return c;
            }
            ENDCG
        }
    }
    Fallback "Legacy Shaders/Transparent/Cutout/VertexLit"
}
