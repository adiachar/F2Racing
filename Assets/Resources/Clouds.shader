// Cloud layer + horizon haze for phones (see Sky.cs). Drawn on a sphere round the camera just inside the far
// plane, after all opaque geometry, so the depth test rejects every pixel that is not sky (early-z) and it
// needs no fog. Per pixel: the view ray is projected onto a flat cloud deck (perspective-correct, clouds converge at the
// horizon), two reads of one tiling noise texture (big shapes + detail, scrolling at different speeds), a
// coverage threshold, thick = darker bases, a bright rim towards the sun, and a band of fog colour at the
// horizon so the sky always meets the fogged hills in exactly the fog colour.
// Medium / High (not F2_GRADE_LOW) add two reads: a very slow regional density (clear patches and clumped fields of
// cumulus instead of an even scatter) and the deck a little towards the sun, so each cloud is lit on its sun side and
// shaded on the far side (density falling towards the sun = a lit face), with a soft silver lining; plus a broad
// forward-scatter glow round the sun and a pale lift low down on the sun's side, added under the clouds (premultiplied
// alpha: an add with alpha 0). Low keeps the original two reads.
// The noise texture holds the puffs in A and a smooth broad field in R (the rain deck), so both come from one read.
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
            #pragma multi_compile __ F2_GRADE_LOW // Low graphics level: cheap grade (Grade.cginc)
            #include "UnityCG.cginc"
            #include "Grade.cginc"

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
                float2 p = d.xz / (max(h, 0.0) + 0.1); // (0.06 smeared the low clouds into long wisps)
                float2 uv1 = p * _Scale + frac(_Time.x * _Wind.xy);
                float2 uv2 = p * (_Scale * 3.1) + float2(0.3, 0.7) + frac(_Time.x * _Wind.zw);
                half n2 = tex2D(_MainTex, uv2).a;
                half4 t1 = tex2D(_MainTex, uv1);
                half n = t1.a * 0.72 + n2 * 0.28;
                // a closing (rain) deck: the smooth broad field in R (same read) replaces the puffs: one even deck with
                // slow darker bands, no swirls
                half ovc = saturate((_Cover - 0.72) * 5.0);
                n = lerp(n, t1.r * 0.85 + n2 * 0.15, ovc);
                half band = smoothstep(0.4, 0.8, t1.r) * ovc;
                half sd = saturate(dot(d, _SunDir.xyz));
            #if defined(F2_GRADE_LOW)
                half thr = 1.0 - _Cover;
                half a = smoothstep(thr - 0.08, thr + 0.22, n);
                half thick = saturate((n - thr) * 2.2) * 0.85;
                half3 col = lerp(_LitCol.rgb, _ShadeCol.rgb, smoothstep(0.15, 0.6, thick)); // flat, even base
                col = lerp(col, _ShadeCol.rgb * 0.82, band);
                half rim = sd * sd; rim *= rim; rim *= rim; // ^8
                col += _SunCol.rgb * (rim * (1.0 - thick) * 0.6);
                half3 glow = (half3)0.0;
            #else
                // regional density: fields of cloud and clear gaps (a quarter-speed drift, ~5x the big shapes)
                half region = tex2D(_MainTex, p * (_Scale * 0.19) + float2(0.61, 0.27) + frac(_Time.x * _Wind.xy * 0.25)).a;
                half thr = 1.0 - saturate(_Cover + (region - 0.5) * 0.5);
                half a = smoothstep(thr - 0.06, thr + 0.16, n); // defined puff edges
                half thick = saturate((n - thr) * 2.2);
                // the deck a step towards the sun: thinner there = this side faces the sun
                float2 sdir = _SunDir.xz / max(length(_SunDir.xz), 0.05);
                half ns = tex2D(_MainTex, uv1 + sdir * 0.035).a * 0.72 + n2 * 0.28;
                half lit = lerp(saturate(0.5 + (n - ns) * 5.0), 0.5, ovc);
                // fair-weather cumulus seen from below: a flat, even grey base over the thick core (a plateau, not a
                // gradient), bright thin edges, the sun side lit
                half base = smoothstep(0.15, 0.55, thick);
                half shade = saturate(base * 0.72 + (0.55 - lit) * 0.8);
                half3 col = lerp(_LitCol.rgb, _ShadeCol.rgb, shade);
                col = lerp(col, _ShadeCol.rgb * 0.82, band);
                // silver lining on thin edges towards the sun (^8), a softer forward-scatter sheen (^2) through thin cloud
                half s2 = sd * sd; half rim = s2 * s2; rim *= rim;
                col += _SunCol.rgb * ((rim * 0.7 + s2 * 0.12) * (1.0 - thick));
                // sky glow round the sun and a pale lift low on the sun's side (added under the clouds)
                half low = saturate(1.0 - h * 3.0);
                half3 glow = _SunCol.rgb * (rim * 0.10 + s2 * 0.05 * low);
            #endif
                // far clouds take on the haze colour, and fade out before the horizon
                col = lerp(col, unity_FogColor.rgb, (1.0 - smoothstep(0.0, 0.3, h)) * 0.65);
                half ca = a * smoothstep(0.0, 0.16, h);

                // horizon haze: fog colour at (and below) the horizon, thinning upwards
                half hz = saturate(1.0 - h / _HazeHeight);
                hz = max(hz * hz * _Haze, saturate(-h * 25.0));

                half k = ca * (1.0 - hz);
                // graded like the world (after the haze, so the horizon meets the graded fog of the hills)
                half aOut = k + hz;
                half3 rgb = (col * k + unity_FogColor.rgb * hz) / max(aOut, 0.001);
                return fixed4(F2Grade(rgb) * aOut + F2GradeAdd(glow) * (1.0 - aOut), aOut);
            }
            ENDCG
        }
    }
    Fallback Off
}
