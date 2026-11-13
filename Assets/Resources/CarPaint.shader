// Car paint for phones: one pass, no textures.
//  * base colour lit by the car's sun and the sky (spherical harmonics),
//  * a clear coat that reflects the sky probe, strongest at grazing angles (Fresnel),
//  * below the horizon the coat reflects the road instead of the sky, which gives the body its horizon line,
//  * a tight sun highlight, and a metallic tint for metal parts.
// _Wet (global, Weather.cs: 0 dry .. 1 wet): a water film on the body: slightly darker, deeper base, a glossier and
// stronger clear coat. No extra cost when dry beyond a few multiplies.
// _CarLit (global, 0..1) dims reflections when the car is in a baked shadow (tunnel, under buildings).
// Livery (_Livery = 1, body paint only): a second colour on the engine cover, the sidepod tops and the nose tip, with a
// thin accent line along its edge. The areas are found from the car's own shape in object space (position and which
// way the surface faces), so the imported body needs no UVs, mask texture or extra mesh data.
// Detail (car.bytes F2M2 vertex colours): R = baked ambient occlusion (_AO = strength), G = brake-disc mask.
// One keyword per kind of part (set by CarModel, all variants always compiled), nothing extra for the body paint:
//  _CARBON  procedural 2x2 twill weave from object position, fading out beyond ~6 m (no aliasing far away);
//           with _Under = 1 only down-facing surfaces become carbon (wing undersides of painted parts)
//  _TYRE    wheel frame (x = axle): tread sheen that dulls with _Wear and _Dirt, subtle moulded sidewall bands
//  _WHEEL   rims, discs, calipers: brake dust darkening (_Dust) and disc glow (_Glow, from the brake temperature)
//  _HELMET  Unity sphere: a mirrored visor band and a thin accent stripe (_Color3) over the top
Shader "F2/CarPaint"
{
    Properties
    {
        _Color ("Color", Color) = (0.8, 0.05, 0.07, 1)
        _Coat ("Clear coat", Range(0, 1)) = 1
        _Gloss ("Gloss", Range(0, 1)) = 0.9
        _Metal ("Metal", Range(0, 1)) = 0.2
        _Color2 ("Livery secondary", Color) = (0.93, 0.93, 0.94, 1)
        _Color3 ("Livery accent", Color) = (0.1, 0.1, 0.11, 1)
        _Livery ("Livery split (0 off, 1 on)", Float) = 0
        _AO ("Baked AO strength (vertex R)", Range(0, 1)) = 0
        _Under ("Carbon only underneath", Float) = 0
        _Wear ("Tyre wear", Range(0, 1)) = 0
        _Dirt ("Tyre dirt", Range(0, 1)) = 0
        _TyreR ("Tyre radius (object units)", Float) = 0.33
        _Dust ("Brake dust", Range(0, 1)) = 0
        _Glow ("Brake disc glow", Color) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_local __ _CARBON _TYRE _WHEEL _HELMET
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            fixed4 _Color, _Color2, _Color3;
            half _Livery;
            half _AO, _Under, _Wear, _Dirt, _TyreR, _Dust;
            half4 _Glow;
            half _Coat, _Gloss, _Metal;
            half _CarLit;
            half _Wet;
            half4 _GroundRefl;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wp : TEXCOORD0;
                half3 n : TEXCOORD1;
                half3 amb : TEXCOORD2;
                UNITY_FOG_COORDS(3)
                float3 op : TEXCOORD4; // object-space position and normal, for the livery split
                half3 on : TEXCOORD5;
                half4 col : COLOR;       // baked AO (r), disc mask (g)
                half3 envSH : TEXCOORD6; // sky light along the reflection: stands in when there is no reflection probe
            };

            v2f vert (appdata_full v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.n = UnityObjectToWorldNormal(v.normal);
                o.amb = ShadeSH9(half4(o.n, 1));
                o.op = v.vertex.xyz; o.on = v.normal;
                o.col = v.color;
                o.envSH = ShadeSH9(half4(reflect(-normalize(_WorldSpaceCameraPos.xyz - o.wp), o.n), 1));
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                half3 N = normalize(i.n);
                half3 V = normalize(_WorldSpaceCameraPos.xyz - i.wp);
                half3 L = normalize(_WorldSpaceLightPos0.xyz);
                half ndl = saturate(dot(N, L));
                half3 base = _Color.rgb;
                if (_Livery > 0.5)
                {
                    // car space: +z forward (nose tip at z 2.8), +y up, x across; ny = how much the surface faces up
                    float3 p = i.op; half ny = normalize(i.on).y; float ax = abs(p.x);
                    // sidepods: the part beside the cockpit, from the inlets back
                    half side = step(0.30, ax) * step(-1.2, p.z) * step(p.z, 0.99);
                    half sideSec = side * step(p.z, 0.95) * step(0.55, ny);
                    half sideAcc = side * step(0.42, ny) * (1.0 - sideSec);
                    // engine cover: the spine behind the driver (rear-wing endplates are wider and stay primary)
                    half eng = step(ax, 0.32) * step(-1.7, p.z) * step(p.z, -0.01);
                    half engSec = eng * step(p.z, -0.05) * step(0.30, ny);
                    half engAcc = eng * step(0.18, ny) * (1.0 - engSec);
                    // nose tip, in the front wing's colour
                    half noseSec = step(2.45, p.z);
                    half noseAcc = step(2.40, p.z) * (1.0 - noseSec);
                    half sec = saturate(sideSec + engSec + noseSec);
                    half acc = saturate(sideAcc + engAcc + noseAcc) * (1.0 - sec);
                    base = lerp(lerp(base, _Color2.rgb, sec), _Color3.rgb, acc);
                }
                half ao = lerp(1.0, i.col.r, _AO);
                half coatK = _Coat, metal = _Metal, specK = 1.0, glossAdd = 0.0;
                half3 emis = 0, envTint = 1;
                #if defined(_CARBON)
                {
                    // 2x2 twill: tows ~7 mm wide, alternating direction on a diagonal staircase; each tow is a
                    // little cylinder (brighter along its crown) and catches the light more in one direction
                    half3 an = abs(i.on);
                    float2 w = (an.x > max(an.y, an.z) ? i.op.zy : (an.y > an.z ? i.op.xz : i.op.xy)) * 140.0;
                    float2 cell = floor(w), fr = frac(w);
                    half sel = step(0.5, frac((cell.x + cell.y) * 0.25));
                    half across = lerp(fr.y, fr.x, sel), prof = 4.0 * across * (1.0 - across);
                    half fade = saturate(1.6 - length(_WorldSpaceCameraPos.xyz - i.wp) * 0.27);
                    half mask = _Under > 0.5 ? smoothstep(-0.25, -0.6, normalize(i.on).y) : 1.0;
                    half3 cb = _Under > 0.5 ? half3(0.06, 0.065, 0.075) : base;
                    cb *= lerp(1.0, lerp(0.72, 1.18, prof) * lerp(0.9, 1.1, sel), fade);
                    base = lerp(base, cb, mask);
                    coatK = lerp(coatK, 0.6, mask);
                    specK = lerp(1.0, lerp(1.0, lerp(0.55, 1.45, sel) * prof + 0.3, fade), mask);
                }
                #elif defined(_TYRE)
                {
                    float r = length(i.op.yz) / _TyreR;
                    half tread = 1.0 - smoothstep(0.35, 0.8, abs(normalize(i.on).x));
                    // moulded bands on the sidewall (no lettering): two thin rings, a touch lighter and satin
                    half band = (smoothstep(0.655, 0.665, r) - smoothstep(0.69, 0.70, r)) + (smoothstep(0.925, 0.935, r) - smoothstep(0.95, 0.96, r));
                    band *= 1.0 - tread;
                    base *= 1.0 + 0.45 * band;
                    // worn tread dulls and greys, dirt (grass, gravel dust) browns it and kills the sheen
                    base = lerp(base, half3(0.17, 0.155, 0.14), tread * saturate(0.35 * _Wear + 0.6 * _Dirt));
                    coatK *= lerp(1.0 + 0.6 * band, 1.7 * (1.0 - 0.75 * _Wear) * (1.0 - 0.85 * _Dirt), tread);
                    glossAdd = -0.15 * tread * _Wear;
                }
                #elif defined(_WHEEL)
                {
                    half disc = i.col.g;
                    base = lerp(base * (1.0 - 0.55 * _Dust) + half3(0.012, 0.009, 0.006) * _Dust, half3(0.1, 0.1, 0.105), disc);
                    coatK *= 1.0 - 0.6 * _Dust; metal *= 1.0 - 0.5 * _Dust; glossAdd = -0.25 * _Dust;
                    emis = _Glow.rgb * disc;
                }
                #elif defined(_HELMET)
                {
                    // Unity sphere, radius 0.5, +z forward: visor band at the front, accent stripe over the top
                    float3 p = i.op;
                    half visor = saturate((p.z - 0.12) * 25.0) * saturate((p.y + 0.06) * 40.0) * saturate((0.16 - p.y) * 40.0);
                    half stripe = (1.0 - visor) * step(abs(p.x), 0.045) * step(0.0, p.y);
                    base = lerp(lerp(base, _Color3.rgb, stripe), half3(0.015, 0.015, 0.02), visor);
                    coatK = lerp(coatK, 1.0, visor); metal = lerp(metal, 0.9, visor); specK = 1.0 + visor;
                    envTint = lerp(half3(1, 1, 1), half3(1.0, 0.72, 0.38), visor); // gold-tinted mirror visor
                }
                #endif
                half gloss = saturate(_Gloss + 0.08 * _Wet + glossAdd);
                half3 diff = base * (ndl * _LightColor0.rgb + i.amb) * (1.0 - metal * 0.6) * (1.0 - 0.12 * _Wet) * ao;
                half3 R = reflect(-V, N);
                half4 envRaw = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, R, (1.0 - gloss) * 6.0);
                half3 env = DecodeHDR(envRaw, unity_SpecCube0_HDR);
                // the sky reflection probe when there is one; without it (black cube) the sky light along R
                env = max(env, i.envSH * 0.9);
                env = lerp(env, _GroundRefl.rgb, 1.0 - smoothstep(-0.12, 0.02, R.y));
                env *= lerp(0.35, 1.0, _CarLit) * (0.3 + 0.7 * ao) * envTint;
                half nv = saturate(dot(N, V));
                half f = 0.04 + 0.96 * pow(1.0 - nv, 5.0);
                half coat = saturate(f * coatK * (1.0 + 0.35 * _Wet) + 0.03 * _Wet);
                half3 H = normalize(L + V);
                float nh = saturate(dot((float3)N, (float3)H));
                half spec = pow(nh, lerp(16.0, 512.0, gloss)) * ndl * lerp(0.5, 6.0, gloss);
                // a second, much tighter sun glint on glossy paint (the crisp hot spot of a polished clear coat)
                spec += (half)pow(nh, 2048.0) * ndl * 14.0 * saturate((gloss - 0.8) * 6.0);
                spec *= specK * ao;
                half3 col = diff * (1.0 - coat) + env * (coat + metal * base) + spec * _LightColor0.rgb * (coatK + metal) + emis;
                fixed4 c = fixed4(col, 1.0);
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
    Fallback "Legacy Shaders/VertexLit"
}
