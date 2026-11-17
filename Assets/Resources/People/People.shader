// People (People.cs): the rigged pit crew, engineers and mechanics. One shared mesh per body / head gear / LOD (vertex
// colour R = the region index: overall, flank panel, yoke, gloves, boots, face, helmet, visor...); the colours come per
// person from _Pal (MaterialPropertyBlock: rgb linear colour, a = gloss), so re-dressing a person for another team is
// free. Lit per vertex by the cars' sun (People are on the car layer, LightBake.CarSun) with a little wrap and the
// sky's SH, a soft sun highlight on glossy parts (helmet shell, visor) and a sky reflection on the visor. Fog and the
// colour grade as every world shader. Precision: no comparisons at all (GLES3 / Vulkan rule: never on half).
Shader "F2/People"
{
    Properties { }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile __ F2_GRADE_LOW
            #include "UnityCG.cginc"
            #include "../Grade.cginc"
            #include "Lighting.cginc"

            float4 _Pal[24];

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; half3 col : TEXCOORD0; UNITY_FOG_COORDS(1) };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float4 pal = _Pal[(int)(v.color.r * 255.0 + 0.5)];
                float3 n = normalize(UnityObjectToWorldNormal(v.normal));
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float3 V = normalize(_WorldSpaceCameraPos.xyz - wp);
                float nl = dot(n, L);
                float diff = saturate((nl + 0.3) / 1.3);
                float3 amb = ShadeSH9(float4(n, 1.0));
                float gloss = pal.a;
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(n, H)), 8.0 + 56.0 * gloss) * gloss * saturate(nl * 4.0) * (0.25 + 1.25 * gloss);
                float fr = 1.0 - saturate(dot(n, V)); fr *= fr;
                float3 refl = ShadeSH9(float4(reflect(-V, n), 1.0)) * (gloss * gloss) * (0.15 + 0.6 * fr);
                o.col = pal.rgb * (diff * _LightColor0.rgb + amb * 1.1) + spec * _LightColor0.rgb + refl;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = fixed4(i.col, 1);
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb);
                return c;
            }
            ENDCG
        }
    }
    Fallback "Legacy Shaders/VertexLit"
}
