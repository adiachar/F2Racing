// Soft contact shadows under the people (People.Blobs): one dynamic mesh of ground quads per group, multiplied onto
// the ground (uv -1..1 across the quad, vertex alpha = strength). No textures, no comparisons (GLES3 / Vulkan rule).
Shader "F2/PeopleShadow"
{
    Properties { }
    SubShader
    {
        Tags { "Queue"="Geometry+20" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Blend DstColor Zero
            ZWrite Off
            Offset -1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float a : TEXCOORD1; UNITY_FOG_COORDS(2) };
            v2f vert (appdata v)
            {
                v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; o.a = v.color.a;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                float r = saturate(1.0 - dot(i.uv, i.uv));
                float k = r * r * i.a;
                fixed4 c = fixed4(1.0 - 0.6 * k, 1.0 - 0.58 * k, 1.0 - 0.55 * k, 1);
                UNITY_APPLY_FOG_COLOR(i.fogCoord, c, fixed4(1, 1, 1, 1));
                return c;
            }
            ENDCG
        }
    }
}
