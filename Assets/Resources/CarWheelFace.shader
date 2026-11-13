// Outer face of a wheel, drawn as one quad (2 triangles) over the modelled rim (player car only; CarModel).
//  * a 2026-style wheel cover over the centre: satin disc with five shallow ribs and the wheel nut,
//  * at speed (_Blur 0..1) the spoke zone turns into a spin smear: the spokes' average colour with fine
//    concentric streaks, so fast wheels stop strobing; the brake glow (_Glow) shows through it as a ring.
// The quad turns with the wheel; the smear is round, so it never shows the turn. uv: -1..1 across the rim face.
Shader "F2/CarWheelFace"
{
    Properties
    {
        _Color ("Rim colour", Color) = (0.2, 0.21, 0.23, 1)
        _Cover ("Cover colour", Color) = (0.07, 0.075, 0.085, 1)
        _Blur ("Spin blur", Range(0, 1)) = 0
        _Glow ("Brake glow", Color) = (0, 0, 0, 0)
        _Dust ("Brake dust", Range(0, 1)) = 0
        _CoverR ("Cover radius (share of the face)", Range(0, 1)) = 0.5
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            fixed4 _Color, _Cover; half4 _Glow; half _Blur, _Dust, _CoverR;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half3 light : TEXCOORD1; UNITY_FOG_COORDS(2) };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy * 2.0 - 1.0;
                half3 n = UnityObjectToWorldNormal(v.normal);
                o.light = saturate(dot(n, normalize(_WorldSpaceLightPos0.xyz))) * _LightColor0.rgb + ShadeSH9(half4(n, 1));
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float r = length(i.uv);
                if (r > 1.0) discard;
                half a = atan2(i.uv.y, i.uv.x);
                half cover = 1.0 - smoothstep(_CoverR - 0.02, _CoverR, r);
                // cover: five shallow ribs, a darker nut in the middle, a thin lip at its edge
                half rib = 0.9 + 0.1 * smoothstep(0.6, 0.95, cos(a * 5.0));
                half3 cc = _Cover.rgb * rib * lerp(0.55, 1.0, smoothstep(0.1, 0.14, r));
                cc += 0.05 * (smoothstep(_CoverR - 0.06, _CoverR - 0.03, r));
                // spin smear of the spoke zone: average rim colour with fine concentric streaks
                half streak = 0.85 + 0.15 * sin(r * 70.0) * sin(r * 23.0 + 1.3);
                half3 sc = _Color.rgb * 0.75 * streak * (1.0 - 0.5 * _Dust);
                half ring = smoothstep(0.55, 0.7, r) * (1.0 - smoothstep(0.85, 0.93, r)); // where the disc shows through
                half smearA = _Blur * 0.88 * (1.0 - smoothstep(0.96, 1.0, r));
                half3 col = lerp(sc, cc, cover) * i.light;
                col += _Glow.rgb * ring * 0.6 * _Blur * (1.0 - cover);
                half alpha = max(cover, smearA);
                fixed4 c = fixed4(col, alpha);
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
}
