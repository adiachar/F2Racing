// Outer face of a wheel, drawn as one quad (2 triangles) over the modelled rim (player car only; CarModel).
//  * a 2026-style wheel cover over the centre: satin disc with five shallow ribs and the wheel nut,
//  * at speed (_Blur 0..1) the spoke zone turns into a spin smear: the spokes' average colour with fine
//    concentric streaks, so fast wheels stop strobing; the brake glow (_Glow) shows through it as a ring.
//  * both are shaded as curved surfaces although the quad is flat: the normal leans with the position on the face
//    (the cover a shallow dome, the rim a dish), so the sky reflection (probe) and the sun highlight slide across
//    them as the car moves; the rim is metal (reflection tinted by its colour), the cover a lacquered dark disc.
// The quad turns with the wheel; the smear is round, so it never shows the turn. uv: -1..1 across the rim face.
Shader "F2/CarWheelFace"
{
    Properties
    {
        _Color ("Rim colour", Color) = (0.51, 0.52, 0.54, 1)
        _Cover ("Cover colour", Color) = (0.07, 0.075, 0.085, 1)
        _Blur ("Spin blur", Range(0, 1)) = 0
        _Glow ("Brake glow", Color) = (0, 0, 0, 0)
        _Dust ("Brake dust", Range(0, 1)) = 0
        _CoverR ("Cover radius (share of the face)", Range(0, 1)) = 0.5
        _Ghost ("Ghost fade (penalty, CarGhost.cs)", Range(0, 1)) = 0
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
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile_local __ F2_GHOST // ghost twin material only (CarGhost.cs)
            #include "UnityCG.cginc"
            #include "Grade.cginc"
            #include "Lighting.cginc"
            #include "CarGhost.cginc"

            float _Ghost;
            fixed4 _Color, _Cover; half4 _Glow; half _Blur, _Dust, _CoverR;
            half4 _GroundRefl; half _CarLit;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half3 amb : TEXCOORD1; UNITY_FOG_COORDS(2) half3 n : TEXCOORD3; float3 wp : TEXCOORD4; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy * 2.0 - 1.0;
                o.n = UnityObjectToWorldNormal(v.normal);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.amb = ShadeSH9(half4(o.n, 1));
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
                half dustK = 1.0 - 0.5 * _Dust;
                half3 sc = _Color.rgb * streak * dustK;
                half ring = smoothstep(0.55, 0.7, r) * (1.0 - smoothstep(0.85, 0.93, r)); // where the disc shows through
                half smearA = _Blur * 0.88 * (1.0 - smoothstep(0.96, 1.0, r));
                // curved-surface normal: uv.x runs along the quad's local z, uv.y along its local y
                half3 n0 = normalize(i.n);
                half3 tz = UnityObjectToWorldDir(float3(0, 0, 1)), ty = UnityObjectToWorldDir(float3(0, 1, 0));
                half3 N = normalize(n0 + (tz * i.uv.x + ty * i.uv.y) * lerp(-0.35, 0.3, cover));
                half3 V = normalize(_WorldSpaceCameraPos.xyz - i.wp);
                half3 L = normalize(_WorldSpaceLightPos0.xyz);
                half ndl = saturate(dot(N, L));
                half3 R = reflect(-V, N);
                half3 env = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, R, lerp(2.5, 1.0, cover)), unity_SpecCube0_HDR);
                half3 envSH = ShadeSH9(half4(R, 1)) * 0.9;
                env = max(env, envSH);
                half grL = dot(envSH, half3(0.2126, 0.7152, 0.0722));
                half3 gr = max(_GroundRefl.rgb, half3(grL, grL, grL)); // (as CarPaint)
                env = lerp(env, gr, 1.0 - smoothstep(-0.2, 0.015, R.y));
                env *= lerp(0.35, 1.0, _CarLit);
                float nh = saturate(dot((float3)N, (float3)normalize(L + V)));
                half3 sun = _LightColor0.rgb;
                half3 light = ndl * sun + i.amb;
                // rim (metal): little diffuse, the sky tinted by the metal, a soft highlight (spread by the smear)
                half3 rimC = sc * light * 0.35 + env * sc * 0.85 + (half)pow(nh, 80.0) * ndl * 2.5 * sc * sun * dustK;
                // cover (lacquered): its own colour, a faint Fresnel sky reflection and a crisper highlight
                half x = 1.0 - saturate(dot(N, V)), x2 = x * x;
                half fc = 0.05 + 0.95 * x2 * x2;
                half3 covC = cc * light * (1.0 - fc) + env * fc + (half)pow(nh, 300.0) * ndl * 3.0 * sun;
                half3 col = lerp(rimC, covC, cover);
                col += _Glow.rgb * ring * 0.6 * _Blur * (1.0 - cover);
                half alpha = max(cover, smearA);
                #if defined(F2_GHOST)
                F2GhostClip(i.pos.xy, _Ghost); col = (half3)F2GhostTint((float3)col, _Ghost, (float)x2); // ghost (CarGhost.cginc)
                #endif
                fixed4 c = fixed4(col, alpha);
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
                return c;
            }
            ENDCG
        }
    }
}
