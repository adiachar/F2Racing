// Cloud layer + horizon haze for phones (see Sky.cs). Drawn on a sphere round the camera just inside the far
// plane, after all opaque geometry, so the depth test rejects every pixel that is not sky (early-z) and it
// needs no fog. Per pixel: the view ray is projected onto a flat cloud deck (perspective-correct, clouds converge at the
// horizon), two reads of one tiling noise texture (big shapes + detail, scrolling at different speeds), a
// coverage threshold, thick = darker bases, a bright rim towards the sun, and a band of fog colour at the
// horizon so the sky always meets the fogged hills in exactly the fog colour.
// Output is premultiplied alpha (Blend One OneMinusSrcAlpha).
Shader "F2/Clouds"
{
    Properties
    {
        _MainTex ("Noise (A)", 2D) = "white" {}
        _Cover ("Cover", Range(0, 1)) = 0.4
        _Scale ("Deck scale", Float) = 0.22
        _Wind ("Wind (xy big, zw detail)", Vector) = (0.03, 0.012, 0.05, 0.02)
        _LitCol ("Lit colour", Color) = (1, 0.99, 0.96, 1)
        _ShadeCol ("Base colour", Color) = (0.55, 0.58, 0.64, 1)
        _SunDir ("Sun direction", Vector) = (0, 1, 0, 0)
        _SunCol ("Sun colour", Color) = (1, 0.95, 0.85, 1)
        _Haze ("Horizon haze", Range(0, 1)) = 0.85
        _HazeHeight ("Haze height", Range(0.01, 0.5)) = 0.12
    }
    SubShader
    {
        Tags { "Queue"="Transparent-100" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Skybox" }
        LOD 100
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Scale;
            float4 _Wind;
            half _Cover, _Haze, _HazeHeight;
            half4 _LitCol, _ShadeCol, _SunCol;
            float4 _SunDir;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                float4 wp = mul(unity_ObjectToWorld, v.vertex);
                o.pos = mul(UNITY_MATRIX_VP, wp);
                o.dir = wp.xyz - _WorldSpaceCameraPos.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float h = d.y;
                // cloud deck: ray / height (offset keeps the horizon finite; clouds have faded out there)
                float2 p = d.xz / (max(h, 0.0) + 0.06);
                float2 uv1 = p * _Scale + frac(_Time.x * _Wind.xy);
                float2 uv2 = p * (_Scale * 3.1) + float2(0.3, 0.7) + frac(_Time.x * _Wind.zw);
                half n = tex2D(_MainTex, uv1).a * 0.72 + tex2D(_MainTex, uv2).a * 0.28;

                half thr = 1.0 - _Cover;
                half a = smoothstep(thr - 0.08, thr + 0.22, n);
                half thick = saturate((n - thr) * 2.2) * 0.85;
                half3 col = lerp(_LitCol.rgb, _ShadeCol.rgb, thick);
                // thin edges light up towards the sun
                half sd = saturate(dot(d, _SunDir.xyz));
                sd *= sd; sd *= sd; sd *= sd; // ^8
                col += _SunCol.rgb * (sd * (1.0 - thick) * 0.6);
                // far clouds take on the haze colour, and fade out before the horizon
                col = lerp(col, unity_FogColor.rgb, (1.0 - smoothstep(0.0, 0.3, h)) * 0.65);
                half ca = a * smoothstep(0.0, 0.16, h);

                // horizon haze: fog colour at (and below) the horizon, thinning upwards
                half hz = saturate(1.0 - h / _HazeHeight);
                hz = max(hz * hz * _Haze, saturate(-h * 25.0));

                half k = ca * (1.0 - hz);
                return fixed4(col * k + unity_FogColor.rgb * hz, k + hz);
            }
            ENDCG
        }
    }
    Fallback Off
}
