// Car paint for phones: one pass, no textures. The look aims at the polished "showroom" cars of mobile racers:
//  * base colour lit by the car's sun (with a little wrap, so the flanks never go dead-dark) and the sky (SH),
//  * a clear coat that mirrors the sky probe almost unblurred on glossy paint; Fresnel F0 0.05 with an x^4 rise
//    (a touch broader than Schlick's x^5), so the sky reads on the flanks and not only at the silhouette,
//  * below the horizon the coat reflects the road instead of the sky, which gives the body its horizon line:
//    crisp on glossy paint, soft on rubber (the line's width follows the gloss),
//  * a sun highlight whose size follows the gloss (exp2 mapping: broad soft sheen on rubber, tight on metal, a
//    tight lobe plus a pin-point glint on clear coat), and a metallic tint for metal parts.
// _Wet (global, Weather.cs: 0 dry .. 1 wet): a water film on the body: slightly darker, deeper base, a glossier and
// stronger clear coat. No extra cost when dry beyond a few multiplies.
// _CarLit (global, 0..1) dims reflections when the car is in a baked shadow (tunnel, under buildings).
// Livery (_Livery = 1, body paint only): a second colour on the engine cover, the sidepod tops and the nose tip, with a
// thin accent line along its edge. The areas are found from the car's own shape in object space (position and which
// way the surface faces), so the imported body needs no UVs, mask texture or extra mesh data.
// Detail (car.bytes F2M2 vertex colours): R = baked ambient occlusion (_AO = strength), G = brake-disc mask.
// One keyword per kind of part (set by CarModel, all variants always compiled), nothing extra for the body paint:
//  _CARBON  procedural 2x2 twill weave (10 mm tows) from object position, faded out by its own pixel size
//           (fwidth) once a tow is narrower than ~3 pixels, so it never shimmers; lacquered (sharp, faint coat);
//           with _Under = 1 only down-facing surfaces become carbon (wing undersides of painted parts)
//  _TYRE    wheel frame (x = axle): tread sheen that dulls with _Wear and _Dirt, subtle moulded sidewall bands
//  _WHEEL   rims, discs, calipers: brake dust darkening (_Dust) and disc glow (_Glow, from the brake temperature)
//  _HELMET  Unity sphere: a mirrored visor band and a thin accent stripe (_Color3) over the top (fallback helmet only)
//  _MERGED  rival cars (CarModel.CreateMergedRival): the whole car in one mesh (car_far) or body + 4 wheels (car_lod),
//           ONE material shared by every rival. Vertex colour A = part ID (see MMat below), R = baked AO with its
//           strength already applied, G = brake-disc mask. Everything that differs per car (livery, stripe, brake
//           glow, dust, tyre wear, rear-light state) comes from a MaterialPropertyBlock, declared as GPU-instanced
//           properties (multi_compile_instancing): on GLES3/Vulkan/Metal equal meshes of all rivals draw as one
//           instanced call; on GLES2 (no instancing) the same properties are plain uniforms and each renderer is one
//           draw call with the shared material (no material switch between cars).
//           The driver (DriverModel.cs) uses this path too: merged into a rival's body, or as the player's own skinned
//           mesh with one non-instanced material (part IDs 12..17: matte suit and gloves, glossy helmet, dark visor).
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
        _Livery ("Livery split (0 off, 1 + style 0..3)", Float) = 0
        _AO ("Baked AO strength (vertex R)", Range(0, 1)) = 0
        _Under ("Carbon only underneath", Float) = 0
        _Wear ("Tyre wear", Range(0, 1)) = 0
        _Dirt ("Tyre dirt", Range(0, 1)) = 0
        _TyreR ("Tyre radius (object units)", Float) = 0.33
        _Dust ("Brake dust", Range(0, 1)) = 0
        _Glow ("Brake disc glow", Color) = (0, 0, 0, 0)
        _TyreRad ("Merged: tyre radius front, rear", Vector) = (0.33, 0.33, 0, 0)
        _Far ("Merged: far model (no carbon weave, no tyre sheen)", Float) = 0
        _Mirror ("Mirror glass where |x| > 0.45 (metal part)", Float) = 0
    }
    CGINCLUDE
    #include "UnityCG.cginc"
    #define CARBON_BASE half3(0.022, 0.024, 0.028)
    // 2x2 twill from the object position: (brightness, highlight) multipliers; each tow is a little cylinder (brighter
    // along its crown) and catches the light more in one direction. fade 0 = plain (1, 1).
    half2 Weave(float3 op, half3 on, half fade)
    {
        half3 an = abs(on);
        float2 w = (an.x > max(an.y, an.z) ? op.zy : (an.y > an.z ? op.xz : op.xy)) * 100.0;
        float2 cell = floor(w), fr = frac(w);
        half sel = step(0.5, frac((cell.x + cell.y) * 0.25));
        half across = lerp(fr.y, fr.x, sel), prof = 4.0 * across * (1.0 - across);
        return half2(lerp(1.0, lerp(0.6, 1.3, prof) * lerp(0.88, 1.12, sel), fade), lerp(1.0, lerp(0.55, 1.45, sel) * prof + 0.3, fade));
    }
    // how much weave to show: full while a tow covers 3+ pixels, gone below ~1.4 (derivatives: call outside branches)
    half WeaveFade(float3 op) { float3 d = fwidth(op) * 100.0; return saturate(1.75 - 2.5 * max(d.x, max(d.y, d.z))); }
    // Livery areas from the car's own shape (object space: +z forward, nose tip at z 2.8, x across, ny = how much the
    // surface faces up). style 0: engine cover + sidepod tops + nose tip; 1: engine cover + the whole nose cone, sidepods
    // in the primary; 2: the whole sidepods + nose tip, engine cover in the primary; 3: a centre stripe down nose and
    // engine cover. Returns (secondary weight, accent weight); the accent is a thin line along the secondary's edge.
    half2 LiverySplit(float3 p, half ny, half style)
    {
        float ax = abs(p.x);
        half s1 = step(0.5, style) * step(style, 1.5), s2 = step(1.5, style) * step(style, 2.5), s3 = step(2.5, style);
        half s0 = 1.0 - saturate(s1 + s2 + s3);
        half side = step(0.30, ax) * step(-1.2, p.z) * step(p.z, 0.99);
        half sideSec = side * step(p.z, 0.95) * (s0 * step(0.55, ny) + s2 * step(0.2, ny));
        half sideAcc = side * step(0.42, ny) * (1.0 - sideSec) * lerp(1.0, step(ny, 0.56), s1 + s3);
        half eng = step(ax, 0.32) * step(-1.7, p.z) * step(p.z, -0.01);
        half engSec = eng * step(p.z, -0.05) * step(0.30, ny) * (1.0 - s2 - s3);
        half engAcc = eng * step(0.18, ny) * (1.0 - engSec) * lerp(1.0, step(ny, 0.32), s2) * (1.0 - s3);
        half noseT = lerp(2.45, 1.5, s1);
        half noseSec = step(noseT, p.z);
        half noseAcc = step(noseT - 0.05, p.z) * (1.0 - noseSec);
        half top = step(0.3, ny) * step(-1.7, p.z) * s3;
        half stripeSec = top * step(ax, 0.10), stripeAcc = top * step(ax, 0.15) * (1.0 - stripeSec);
        half sec = saturate(sideSec + engSec + noseSec + stripeSec);
        half acc = saturate(sideAcc + engAcc + noseAcc + stripeAcc) * (1.0 - sec);
        return half2(sec, acc);
    }
    #if defined(_MERGED)
        UNITY_INSTANCING_BUFFER_START(CarProps)
            // float4 only: half / fixed members in an instanced constant buffer are laid out or read wrongly by some
            // mobile drivers. The livery primary is _Color itself (Unity's own instancing pattern), so any path that
            // ends up without the instanced data (a fallback shader, a stripped variant) still shows the team colour
            // and never the material's default red.
            UNITY_DEFINE_INSTANCED_PROP(float4, _Color)    // livery primary
            UNITY_DEFINE_INSTANCED_PROP(float4, _LivB)     // livery secondary (wings, split areas)
            UNITY_DEFINE_INSTANCED_PROP(float4, _LivC)     // livery accent; a = 1 split on
            UNITY_DEFINE_INSTANCED_PROP(float4, _GlowF)    // brake-disc glow, front axle
            UNITY_DEFINE_INSTANCED_PROP(float4, _GlowR)    // rear axle
            UNITY_DEFINE_INSTANCED_PROP(float4, _LightCol) // rear / rain light emission (brake, wet, harvest blink)
            UNITY_DEFINE_INSTANCED_PROP(float4, _StripeCol)// tyre compound stripe
            UNITY_DEFINE_INSTANCED_PROP(float4, _Axle)     // brake dust front, rear
            UNITY_DEFINE_INSTANCED_PROP(float4, _TyreWD)   // tyre wear front, dirt front, wear rear, dirt rear
            UNITY_DEFINE_INSTANCED_PROP(float4, _HelmA)    // driver's helmet: shell colour
            UNITY_DEFINE_INSTANCED_PROP(float4, _HelmB)    // driver's helmet: design colour (brow band, centre stripe)
        UNITY_INSTANCING_BUFFER_END(CarProps)
        #define MP(n) UNITY_ACCESS_INSTANCED_PROP(CarProps, n)
        half4 _TyreRad; half _Far;
        half MIs(half id, half k) { return step(abs(id - k), 0.5); }
        half MId(half a) { return floor(a * 255.0 + 0.5); }
        // the livery split of the body paint (the same areas as the per-part passes below)
        half3 MLivery(float3 p, half ny, half style, half3 c1, half3 c2, half3 c3)
        {
            half2 w = LiverySplit(p, ny, style);
            return lerp(lerp(c1, c2, w.x), c3, w.y);
        }
        // part IDs (CarModel.Id*): 0 body paint, 1 wings (secondary), 2 carbon, 3 metal, 4/5 tyre front/rear,
        // 6/7 rim + brake front/rear, 8 rear lights, 9 tyre stripe, 11 steering wheel; the driver (DriverModel): 12 suit
        // (livery primary), 13 suit trim and gloves (secondary), 14 / 15 helmet shell / design, 16 visor, 17 dark fabric.
        // base colour and (clear coat, gloss, metal): the values the per-part materials had (CarModel.CreateFromAsset)
        void MMat(half id, out half3 base, out half3 cgm)
        {
            // (linear values: CarModel gives the per-part materials the same ones via Lin(); livery colours come in
            // through CarModel.LinV: converted from sRGB once, sent as a vector on the merged path)
            base = CARBON_BASE; cgm = half3(0.35, 0.88, 0.05);                                               // 2 carbon
            half k = MIs(id, 0); base = lerp(base, MP(_Color).rgb, k); cgm = lerp(cgm, half3(1.0, 0.95, 0.06), k);
            k = MIs(id, 1); base = lerp(base, MP(_LivB).rgb, k); cgm = lerp(cgm, half3(1.0, 0.93, 0.03), k);
            k = MIs(id, 3); base = lerp(base, half3(0.45, 0.46, 0.5), k); cgm = lerp(cgm, half3(0.3, 0.85, 0.95), k);
            k = step(3.5, id) * step(id, 5.5); base = lerp(base, half3(0.015, 0.015, 0.016), k); cgm = lerp(cgm, half3(0.1, 0.35, 0.0), k);
            k = step(5.5, id) * step(id, 7.5); base = lerp(base, half3(0.22, 0.23, 0.245), k); cgm = lerp(cgm, half3(0.35, 0.82, 0.9), k);
            k = MIs(id, 11); base = lerp(base, half3(0.03, 0.03, 0.033), k); cgm = lerp(cgm, half3(0.5, 0.7, 0.1), k);
            k = MIs(id, 8); base = lerp(base, half3(1.0, 0.15, 0.12), k); cgm = lerp(cgm, half3(0.5, 0.8, 0.0), k);
            k = MIs(id, 9); base = lerp(base, MP(_StripeCol).rgb, k); cgm = lerp(cgm, half3(0.25, 0.5, 0.0), k);
            // the driver: fabric is matte (a faint broad sheen), the helmet a glossy clear coat, the visor dark smoked glass
            k = step(11.5, id) * step(id, 13.5); base = lerp(base, lerp(MP(_Color).rgb, MP(_LivB).rgb, MIs(id, 13)) * 0.8, k); cgm = lerp(cgm, half3(0.06, 0.3, 0.0), k);
            k = step(13.5, id) * step(id, 15.5); base = lerp(base, lerp(MP(_HelmA).rgb, MP(_HelmB).rgb, MIs(id, 15)), k); cgm = lerp(cgm, half3(1.0, 0.95, 0.08), k);
            k = MIs(id, 16); base = lerp(base, half3(0.012, 0.013, 0.017), k); cgm = lerp(cgm, half3(1.0, 0.97, 0.3), k);
            k = MIs(id, 17); base = lerp(base, half3(0.025, 0.025, 0.028), k); cgm = lerp(cgm, half3(0.08, 0.3, 0.0), k);
        }
    #endif
    ENDCG
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
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile_local __ _CARBON _TYRE _WHEEL _HELMET _MERGED
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Grade.cginc"
            #include "Lighting.cginc"

            #if !defined(_MERGED)
            fixed4 _Color;
            #endif
            fixed4 _Color2, _Color3;
            half _Livery, _Mirror;
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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert (appdata_full v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v); UNITY_TRANSFER_INSTANCE_ID(v, o);
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
                UNITY_SETUP_INSTANCE_ID(i);
                half3 N = normalize(i.n);
                half3 V = normalize(_WorldSpaceCameraPos.xyz - i.wp);
                half3 L = normalize(_WorldSpaceLightPos0.xyz);
                half nl = dot(N, L), ndl = saturate(nl);
                #if defined(_MERGED)
                half3 base = MP(_Color).rgb;
                #else
                half3 base = _Color.rgb;
                #endif
                if (_Livery > 0.5)
                {
                    half2 lw = LiverySplit(i.op, normalize(i.on).y, _Livery - 1.0); half sec = lw.x, acc = lw.y;
                    base = lerp(lerp(base, _Color2.rgb, sec), _Color3.rgb, acc);
                }
                half ao = lerp(1.0, i.col.r, _AO);
                half coatK = _Coat, metal = _Metal, specK = 1.0, glossAdd = 0.0, gloss0 = _Gloss;
                half3 emis = 0, envTint = 1;
                half visorK = 0.0;
                #if defined(_MERGED)
                {
                    // one mesh, many parts: the part ID picks the material (see MMat)
                    half id = MId(i.col.a); half3 cgm; MMat(id, base, cgm);
                    coatK = cgm.x; gloss0 = cgm.y; metal = cgm.z; ao = i.col.r;
                    half3 on = normalize(i.on);
                    half body = MIs(id, 0) * step(0.5, MP(_LivC).a);
                    base = lerp(base, MLivery(i.op, on.y, MP(_LivC).a - 1.0, MP(_Color).rgb, MP(_LivB).rgb, MP(_LivC).rgb), body);
                    // carbon weave (carbon parts, the wings' undersides) while it is big enough on screen
                    half near = (1.0 - _Far) * WeaveFade(i.op); // (derivatives: outside the branch)
                    half cw = saturate(MIs(id, 2) + MIs(id, 1) * smoothstep(-0.25, -0.6, on.y));
                    if (cw > 0.0)
                    {
                        half2 wv = Weave(i.op, on, near);
                        half3 cb = lerp(base, CARBON_BASE, MIs(id, 1)) * wv.x;
                        base = lerp(base, cb, cw);
                        coatK = lerp(coatK, 0.35, cw);
                        specK = lerp(1.0, wv.y, cw);
                    }
                    // tyres (LOD wheels are their own objects, so object space is the wheel frame; the far car skips it)
                    half front = step(id, 4.5) + MIs(id, 6);
                    half ty = step(3.5, id) * step(id, 5.5) * (1.0 - _Far);
                    if (ty > 0.0)
                    {
                        half4 wd = MP(_TyreWD); half wear = lerp(wd.z, wd.x, front), dirt = lerp(wd.w, wd.y, front);
                        float r = length(i.op.yz) / lerp(_TyreRad.y, _TyreRad.x, front);
                        half tread = 1.0 - smoothstep(0.35, 0.8, abs(on.x));
                        half band = (smoothstep(0.655, 0.665, r) - smoothstep(0.69, 0.70, r)) + (smoothstep(0.925, 0.935, r) - smoothstep(0.95, 0.96, r));
                        band *= 1.0 - tread;
                        base = lerp(base, half3(0.032, 0.032, 0.034), 0.7 * band); // (as _TYRE below)
                        base = lerp(base, half3(0.07, 0.064, 0.056), tread * saturate(0.35 * wear + 0.6 * dirt));
                        coatK *= lerp(1.0 + 0.3 * band, 1.15 * (1.0 - 0.75 * wear) * (1.0 - 0.85 * dirt), tread);
                        glossAdd = -0.15 * tread * wear;
                    }
                    // rims and brakes: dust and disc glow, per axle
                    half hub = step(5.5, id) * step(id, 7.5);
                    half dust = lerp(MP(_Axle).y, MP(_Axle).x, front) * hub, disc = i.col.g * hub;
                    base = lerp(base * (1.0 - 0.55 * dust) + half3(0.012, 0.009, 0.006) * dust, half3(0.06, 0.06, 0.065), disc);
                    coatK *= 1.0 - 0.6 * dust; metal *= 1.0 - 0.5 * dust; glossAdd += -0.25 * dust;
                    emis = lerp(MP(_GlowR).rgb, MP(_GlowF).rgb, front) * disc + MP(_LightCol).rgb * MIs(id, 8);
                    // the driver's visor: smoked glass shows a little more of the sky than a clear coat would
                    visorK = MIs(id, 16);
                }
                #elif defined(_CARBON)
                {
                    // 2x2 twill: tows 10 mm wide, alternating direction on a diagonal staircase (Weave above)
                    half3 on = normalize(i.on);
                    half2 wv = Weave(i.op, on, WeaveFade(i.op));
                    half mask = _Under > 0.5 ? smoothstep(-0.25, -0.6, on.y) : 1.0;
                    half3 cb = (_Under > 0.5 ? CARBON_BASE : base) * wv.x;
                    base = lerp(base, cb, mask);
                    coatK = lerp(coatK, 0.35, mask);
                    specK = lerp(1.0, wv.y, mask);
                }
                #elif defined(_TYRE)
                {
                    float r = length(i.op.yz) / _TyreR;
                    half tread = 1.0 - smoothstep(0.35, 0.8, abs(normalize(i.on).x));
                    // moulded bands on the sidewall (no lettering): two thin rings, a touch lighter and satin
                    half band = (smoothstep(0.655, 0.665, r) - smoothstep(0.69, 0.70, r)) + (smoothstep(0.925, 0.935, r) - smoothstep(0.95, 0.96, r));
                    band *= 1.0 - tread;
                    // dark matte rubber: the rings are a faint grey (no lettering), the tread only a soft sheen; no
                    // extra highlight gain (specK 2 made the shoulders read as light patches in the sun)
                    base = lerp(base, half3(0.032, 0.032, 0.034), 0.7 * band);
                    // worn tread dulls and greys, dirt (grass, gravel dust) browns it and kills the sheen
                    base = lerp(base, half3(0.07, 0.064, 0.056), tread * saturate(0.35 * _Wear + 0.6 * _Dirt));
                    coatK *= lerp(1.0 + 0.3 * band, 1.15 * (1.0 - 0.75 * _Wear) * (1.0 - 0.85 * _Dirt), tread);
                    glossAdd = -0.15 * tread * _Wear;
                }
                #elif defined(_WHEEL)
                {
                    half disc = i.col.g;
                    base = lerp(base * (1.0 - 0.55 * _Dust) + half3(0.012, 0.009, 0.006) * _Dust, half3(0.06, 0.06, 0.065), disc);
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
                // mirror glass (the metal part's faces out at the mirrors, |x| > 0.45; all other metal is near the
                // centre line): plain dark grey glass with a fixed share of sky reflection, no chrome, no render texture
                #if defined(_MERGED)
                half mir = MIs(MId(i.col.a), 3);
                #else
                half mir = _Mirror;
                #endif
                mir *= step(0.45, abs(i.op.x));
                half coatMin = 0.1 * visorK;
                if (mir > 0.5)
                {
                    base = half3(0.05, 0.052, 0.056); metal = 0.0; coatK = 0.6; gloss0 = 0.92; glossAdd = 0.0; specK = 0.5;
                    coatMin = 0.22;
                }
                half gloss = saturate(gloss0 + 0.08 * _Wet + glossAdd);
                // sun with a little wrap (a softer terminator: flanks turned from the sun stay readable) + sky light
                half wrapL = saturate((nl + 0.2) * (1.0 / 1.2));
                half3 diff = base * (wrapL * _LightColor0.rgb + i.amb) * (1.0 - metal * 0.85) * (1.0 - 0.12 * _Wet) * ao; // metal: mostly mirror
                half3 R = reflect(-V, N);
                // glossy paint reads the probe almost unblurred (mip ~0.25): the sky and clouds show as a mirror image
                half4 envRaw = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, R, (1.0 - gloss) * 5.0);
                half3 env = DecodeHDR(envRaw, unity_SpecCube0_HDR);
                // the sky reflection probe when there is one; without it (black cube) the sky light along R
                env = max(env, i.envSH * 0.9);
                // the road below the horizon: a crisp horizon line on glossy paint, a soft one on rubber. _GroundRefl
                // arrives through SetGlobalColor (sRGB -> linear: 0.15 becomes 0.02, a black road); the grey sky light
                // from below (SH along R) is the floor, about what sunlit asphalt sends back
                half grL = dot(i.envSH, half3(0.2126, 0.7152, 0.0722)) * 0.9;
                half3 gr = max(_GroundRefl.rgb, half3(grL, grL, grL));
                env = lerp(env, gr, 1.0 - smoothstep(lerp(-0.35, -0.05, gloss), 0.015, R.y));
                env *= lerp(0.35, 1.0, _CarLit) * (0.3 + 0.7 * ao) * envTint;
                env *= lerp(1.0, 0.65, step(0.9, coatK)); // paint's mirror coat adds less white: deep colours stay deep
                half x = 1.0 - saturate(dot(N, V)), x2 = x * x;
                half f = 0.05 + 0.95 * x2 * x2;
                half coat = max(saturate(f * coatK * (1.0 + 0.35 * _Wet) + 0.03 * _Wet), coatMin);
                half3 H = normalize(L + V);
                float nh = saturate(dot((float3)N, (float3)H));
                // highlight size from the gloss: 0.35 -> exponent 34 (rubber sheen), 0.82 -> 240 (metal), 0.95 -> 416 (paint)
                half sp = exp2(3.0 + 6.0 * gloss);
                half spec = (half)pow(nh, (float)sp) * ndl * min(sp * 0.015, 6.0);
                // a second, much tighter sun glint on glossy paint (the crisp hot spot of a polished clear coat)
                spec += (half)pow(nh, 2048.0) * ndl * 14.0 * saturate((gloss - 0.8) * 6.0);
                spec *= specK * ao;
                half3 col = diff * (1.0 - coat) + env * (coat + metal * base) + spec * _LightColor0.rgb * (coatK + metal) + emis;
                fixed4 c = fixed4(col, 1.0);
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
                return c;
            }
            ENDCG
        }
    }
    // Low graphics level (GameManagerPerf.cs sets this shader's maximumLOD to 100): sun, sky, clear coat and the sun
    // highlight are worked out per vertex (no reflection-probe read, no per-pixel pow); per pixel only the livery
    // split, the baked AO, the helmet visor (fallback helmet) and the brake dust / disc glow. Carbon weave and tyre sheen are left out.
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog
            #pragma multi_compile __ F2_GRADE_LOW
            #pragma multi_compile_local __ _CARBON _TYRE _WHEEL _HELMET _MERGED
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Grade.cginc"
            #include "Lighting.cginc"

            #if !defined(_MERGED)
            fixed4 _Color;
            #endif
            fixed4 _Color2, _Color3;
            half _Mirror;
            half _Livery, _AO, _Dust, _Coat, _Gloss, _Metal, _CarLit, _Wet;
            half4 _Glow, _GroundRefl;

            struct v2f
            {
                float4 pos : SV_POSITION;
                half3 diff : TEXCOORD0;   // light on the base colour (sun + sky, less what the coat reflects)
                half3 add : TEXCOORD1;    // coat reflection + sun highlight
                half3 envM : TEXCOORD2;   // metallic reflection (tinted by the base colour per pixel)
                float3 op : TEXCOORD3;    // object position, for the livery split and the helmet visor
                half ny : TEXCOORD4;      // object normal y
                half4 col : COLOR;        // baked AO (r), disc mask (g)
                UNITY_FOG_COORDS(5)
                #if defined(_MERGED)
                half3 mbase : TEXCOORD6;  // merged rival: the part's base colour (MMat)
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert (appdata_full v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v); UNITY_TRANSFER_INSTANCE_ID(v, o);
                half coat0 = _Coat, gloss0 = _Gloss, metal0 = _Metal;
                #if defined(_MERGED)
                    half3 cgm; MMat(MId(v.color.a), o.mbase, cgm); coat0 = cgm.x; gloss0 = cgm.y; metal0 = cgm.z;
                    half mir = MIs(MId(v.color.a), 3);
                #else
                    half mir = _Mirror;
                #endif
                mir *= step(0.45, abs(v.vertex.x)); // mirror glass (as the full pass)
                coat0 = lerp(coat0, 0.6, mir); gloss0 = lerp(gloss0, 0.92, mir); metal0 = lerp(metal0, 0.0, mir);
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                half3 N = UnityObjectToWorldNormal(v.normal);
                half3 V = normalize(_WorldSpaceCameraPos.xyz - wp);
                half3 L = normalize(_WorldSpaceLightPos0.xyz);
                half ndl = saturate(dot(N, L));
                half3 R = reflect(-V, N);
                half gloss = saturate(gloss0 + 0.08 * _Wet);
                half3 env = ShadeSH9(half4(R, 1)) * 0.9;
                half grL = dot(env, half3(0.2126, 0.7152, 0.0722));
                half3 gr = max(_GroundRefl.rgb, half3(grL, grL, grL)); // (as the full pass)
                env = lerp(env, gr, 1.0 - smoothstep(lerp(-0.35, -0.05, gloss), 0.015, R.y));
                env *= lerp(0.35, 1.0, _CarLit);
                env *= lerp(1.0, 0.65, step(0.9, coat0)); // (as the full pass)
                half x = 1.0 - saturate(dot(N, V)), x2 = x * x;
                half coat = max(saturate((0.05 + 0.95 * x2 * x2) * coat0 * (1.0 + 0.35 * _Wet) + 0.03 * _Wet), 0.22 * mir);
                half nh = saturate(dot(N, normalize(L + V)));
                half sp = min(exp2(3.0 + 6.0 * gloss), 128.0); // (per vertex: no tighter than the mesh can show)
                half spec = pow(nh, sp) * ndl * min(sp * 0.02, 3.0);
                half wrapL = saturate((dot(N, L) + 0.2) * (1.0 / 1.2));
                o.diff = (wrapL * _LightColor0.rgb + ShadeSH9(half4(N, 1))) * (1.0 - metal0 * 0.85) * (1.0 - 0.12 * _Wet) * (1.0 - coat);
                o.add = env * coat + spec * _LightColor0.rgb * (coat0 + metal0);
                o.envM = env * metal0;
                o.op = v.vertex.xyz; o.ny = v.normal.y;
                o.col = v.color;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                #if defined(_MERGED)
                half3 base = MP(_Color).rgb;
                #else
                half3 base = _Color.rgb;
                #endif
                if (_Livery > 0.5)
                {
                    half2 lw = LiverySplit(i.op, i.ny, _Livery - 1.0); half sec = lw.x, acc = lw.y;
                    base = lerp(lerp(base, _Color2.rgb, sec), _Color3.rgb, acc);
                }
                half3 emis = 0;
                half ao = lerp(1.0, i.col.r, _AO);
                #if defined(_MERGED)
                    half id = MId(i.col.a); base = i.mbase; ao = i.col.r;
                    base = lerp(base, MLivery(i.op, i.ny, MP(_LivC).a - 1.0, MP(_Color).rgb, MP(_LivB).rgb, MP(_LivC).rgb), MIs(id, 0) * step(0.5, MP(_LivC).a));
                    half front = step(id, 4.5) + MIs(id, 6), hub = step(5.5, id) * step(id, 7.5);
                    half dust = lerp(MP(_Axle).y, MP(_Axle).x, front) * hub, disc = i.col.g * hub;
                    base = lerp(base * (1.0 - 0.55 * dust), half3(0.06, 0.06, 0.065), disc);
                    emis = lerp(MP(_GlowR).rgb, MP(_GlowF).rgb, front) * disc + MP(_LightCol).rgb * MIs(id, 8);
                #elif defined(_WHEEL)
                    half disc = i.col.g;
                    base = lerp(base * (1.0 - 0.55 * _Dust), half3(0.06, 0.06, 0.065), disc);
                    emis = _Glow.rgb * disc;
                #elif defined(_HELMET)
                    float3 hp = i.op;
                    half visor = saturate((hp.z - 0.12) * 25.0) * saturate((hp.y + 0.06) * 40.0) * saturate((0.16 - hp.y) * 40.0);
                    half stripe = (1.0 - visor) * step(abs(hp.x), 0.045) * step(0.0, hp.y);
                    base = lerp(lerp(base, _Color3.rgb, stripe), half3(0.3, 0.22, 0.12), visor);
                #endif
                #if defined(_MERGED)
                half mirP = MIs(MId(i.col.a), 3);
                #else
                half mirP = _Mirror;
                #endif
                base = lerp(base, half3(0.05, 0.052, 0.056), mirP * step(0.45, abs(i.op.x))); // mirror glass
                half3 c3 = base * i.diff * ao + (i.add + i.envM * base) * (0.3 + 0.7 * ao) + emis;
                fixed4 c = fixed4(c3, 1.0);
                UNITY_APPLY_FOG(i.fogCoord, c);
                c.rgb = F2Grade(c.rgb);
                return c;
            }
            ENDCG
        }
    }
    Fallback "Legacy Shaders/VertexLit"
}
