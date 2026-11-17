// RacingLine.shader — the racing-line guide on the road (RacingLineView.cs): a strip of forward-pointing chevrons
// over a faint fill, coloured per vertex (green / yellow / red, already in linear space) with the fade in alpha.
// Unlit, so it reads the same by day, at night and in the rain. Edges and chevrons are anti-aliased with fwidth;
// far away, where a chevron gets smaller than about two pixels, the pattern blends to its average (no shimmer).
// uv.x = -1..1 across the strip, uv.y = metres along the line. All maths in float (GLES3 / Vulkan safe).
Shader "F2/RacingLine"
{
    Properties
    {
        _Period ("Chevron period (m)", Float) = 3.2
        _Gain ("Brightness", Float) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-20" "RenderType" = "Transparent" "IgnoreProjector" = "True" "ForceNoShadowCasting" = "True" }
        Pass
        {
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha
            Offset -1, -2

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float _Period;
            float _Gain;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 col : COLOR;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.col = v.color;
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float x = i.uv.x;
                float ax = abs(x);
                float wx = max(fwidth(x), 0.0001);
                float side = saturate((1.0 - ax) / wx);                    // soft side edges

                // chevrons "^": the tip leads at the centre, the arms trail at the sides
                float p = i.uv.y / max(_Period, 0.1) + ax * 0.32;
                float f = frac(p);
                float wp = max(fwidth(p), 0.0001);
                float c = abs(f - 0.22);                                   // band f in 0 .. 0.44
                float band = saturate((0.22 - c) / wp + 0.5);
                float far = saturate(wp * 2.0 - 0.6);                      // sub-pixel chevrons: their average
                band = lerp(band, 0.44, far);

                // a thin bright core and edges keep the line readable where the chevrons blur
                float rim = saturate((ax - 0.78) / wx + 0.5) * 0.35;
                float a = saturate(0.28 + 0.72 * band + rim) * side * i.col.a;
                return float4(i.col.rgb * _Gain, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
