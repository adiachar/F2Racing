// PitGarage (PitGarage.cs): the team garages, gantries, box markings and pit-wall stands. ONE shared mesh per level of
// detail (detailed / simple / closed shutter) is drawn at every bay, GPU-instanced; what makes a garage a team's is per
// instance: the team's primary / secondary / accent colours and the row of its name in the atlas.
//  * vertex colour = the baked light (and the colour of the fixed parts), stored half (0.5 = full), as Baked.shader
//  * uv1 = (wP, wS, wA, name): how much of the team's primary, secondary, accent multiplies this vertex (team parts are
//    stored white), and 1 on the name boards, whose uv.y moves down by _Team.w rows of the name atlas
//  * _TeamP / _TeamS / _TeamA rgb = the colours (linear), _TeamP.w = the name row's uv offset (row * 16 / 512)
// Fog and the colour grade as every world shader. No comparisons at all (GLES3 / Vulkan: never on half).
Shader "F2/PitGarage"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap nolodfade
            #pragma multi_compile __ F2_GRADE_LOW
            #pragma multi_compile __ FOG_LINEAR
            #include "UnityCG.cginc"
            #include "Grade.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 team : TEXCOORD1;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : TEXCOORD1;
                UNITY_FOG_COORDS(2)
            };

            sampler2D _MainTex;
            UNITY_INSTANCING_BUFFER_START(PitProps)
                UNITY_DEFINE_INSTANCED_PROP(float4, _TeamP) // float4 only (half members in an instanced buffer break on some mobile drivers)
                UNITY_DEFINE_INSTANCED_PROP(float4, _TeamS)
                UNITY_DEFINE_INSTANCED_PROP(float4, _TeamA)
            UNITY_INSTANCING_BUFFER_END(PitProps)

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                float4 tp = UNITY_ACCESS_INSTANCED_PROP(PitProps, _TeamP);
                float3 ts = UNITY_ACCESS_INSTANCED_PROP(PitProps, _TeamS).rgb;
                float3 ta = UNITY_ACCESS_INSTANCED_PROP(PitProps, _TeamA).rgb;
                o.pos = UnityObjectToClipPos(v.vertex);
                float w = saturate(v.team.x + v.team.y + v.team.z);
                float3 tint = (1.0 - w) + v.team.x * tp.rgb + v.team.y * ts + v.team.z * ta;
                o.uv = v.uv - float2(0.0, v.team.w * tp.w);
                o.color = half4(v.color.rgb * tint * 2.0, 1.0); // stored light 0.5 = full brightness
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                c.rgb *= i.color.rgb;
                c.a = 1.0;
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb);
                return c;
            }
            ENDCG
        }
    }
    Fallback "Legacy Shaders/Diffuse"
}
