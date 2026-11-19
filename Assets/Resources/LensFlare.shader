// Lens flare for phones (LensFlare.cs): ONE mesh of screen-space quads (bloom, dirt, anamorphic streak, ghosts), additive,
// no textures (the shapes are drawn with maths). Vertices are already in clip space (x, y); uv.xy = -1..1 inside the quad,
// uv.z = shape (0 soft disc, 1 ring-disc, 2 hexagon, 3 anamorphic streak, 4 lens dirt, 5 rainbow halo ring), vertex colour = colour x intensity.
Shader "F2/LensFlare"
{
    SubShader
    {
        Tags { "Queue"="Transparent+200" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Grade.cginc"

            struct appdata { float4 vertex : POSITION; float4 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                // flip with the projection (render into texture) the same way the camera does
                o.pos = float4(v.vertex.x, v.vertex.y * sign(UNITY_MATRIX_P._m11), UNITY_NEAR_CLIP_VALUE, 1);
                o.uv = v.uv.xyz;
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = i.uv.xy;
                float r = length(p), g;
                if (i.uv.z < 0.5)       { g = saturate(1.0 - r); g *= g * g; }                                   // soft bloom
                else if (i.uv.z < 1.5)  { g = smoothstep(1.0, 0.85, r) * (0.2 + 0.8 * smoothstep(0.5, 0.95, r)); } // ghost: bright rim
                else if (i.uv.z < 2.5)                                                                           // hexagon ghost
                {
                    float2 a = abs(p);
                    float h = max(a.x * 0.866 + a.y * 0.5, a.y);
                    g = smoothstep(1.0, 0.88, h) * (0.25 + 0.75 * smoothstep(0.55, 0.95, h));
                }
                else if (i.uv.z < 3.5)  { float x = 1.0 - abs(p.x), y = 1.0 - abs(p.y); g = x * x * x * y * y * y * y * y * y; } // streak
                else if (i.uv.z < 4.5)  { g = saturate(1.0 - r); g *= g * (0.55 + 0.45 * sin(p.x * 9.0 + p.y * 4.0) * sin(p.y * 7.0 - p.x * 3.0)); } // dirt
                else                                                                                             // rainbow halo ring
                {
                    float t = saturate((r - 0.82) / 0.16);                       // 0 inner edge .. 1 outer edge
                    g = smoothstep(0.0, 0.3, t) * smoothstep(1.0, 0.7, t);
                    float3 hue = saturate(float3(t * 2.0 - 0.6, 1.0 - abs(t * 2.0 - 1.0) * 1.4, 1.2 - t * 2.0) * 1.3);
                    return fixed4(F2GradeAdd(i.color.rgb * hue) * g, 0);
                }
                return fixed4(F2GradeAdd(i.color.rgb) * g, 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
