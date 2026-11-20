// People2 (People.cs, Tools/People2): the realistic people (race driver, crew, engineers): textured from one atlas per
// pack (_MainTex: rgb colour, a = tint amount). Where a > 0 the rgb holds a detail grey (0.5 linear = the garment's own
// average brightness) and the colour comes per person from _Pal[region] (vertex colour R = region index, the same
// 24-entry palette as People.shader, MaterialPropertyBlock), so a team's overalls / suit / cap recolour for free; where
// a = 0 the texture shows as it is (faces, hands, hair, shoes, visor). Lit per vertex as People.shader: the cars' sun
// with a little wrap, the sky's SH (ShadeSH9; the menu studio gives its own), a soft highlight on glossy regions
// (_Pal.a: helmet shell, visor) and a sky reflection on them. Fog and the colour grade as every world shader.
// Precision: no comparisons, no step() (GLES3 / Vulkan rule); the fragment maths in float.
Shader "F2/People2"
{
    Properties { _MainTex ("Atlas", 2D) = "white" {} }
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

            sampler2D _MainTex;
            float4 _Pal[24];

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f
            {
                float4 pos : SV_POSITION; float2 uv : TEXCOORD0;
                float3 pal : TEXCOORD1; float3 lit : TEXCOORD2; float3 add : TEXCOORD3;
                UNITY_FOG_COORDS(4)
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                float4 pal = _Pal[(int)(v.color.r * 255.0 + 0.5)];
                float3 n = normalize(UnityObjectToWorldNormal(v.normal));
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float3 V = normalize(_WorldSpaceCameraPos.xyz - wp);
                float nl = dot(n, L);
                float diff = saturate((nl + 0.25) / 1.25);
                float3 amb = ShadeSH9(float4(n, 1.0));
                float gloss = pal.a;
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(n, H)), 8.0 + 56.0 * gloss) * gloss * saturate(nl * 4.0) * (0.25 + 1.25 * gloss);
                float fr = 1.0 - saturate(dot(n, V)); fr *= fr;
                float3 refl = ShadeSH9(float4(reflect(-V, n), 1.0)) * (gloss * gloss) * (0.15 + 0.6 * fr);
                o.pal = pal.rgb;
                o.lit = diff * _LightColor0.rgb + amb * 1.1;
                o.add = spec * _LightColor0.rgb + refl + fr * 0.06 * amb; // (+ a faint sheen at grazing angles: cloth, skin)
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float4 t = tex2D(_MainTex, i.uv);
                float3 alb = lerp(t.rgb, i.pal * t.rgb * 2.0, t.a);
                fixed4 c = fixed4(alb * i.lit + i.add, 1);
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb);
                return c;
            }
            ENDCG
        }
    }
    Fallback "Legacy Shaders/VertexLit"
}
