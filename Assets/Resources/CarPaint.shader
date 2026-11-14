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
//  _MERGED  rival cars (CarModel.CreateMergedRival): the whole car in one mesh (car_far) or body + 4 wheels (car_lod),
//           ONE material shared by every rival. Vertex colour A = part ID (see MMat below), R = baked AO with its
//           strength already applied, G = brake-disc mask. Everything that differs per car (livery, stripe, brake
//           glow, dust, tyre wear, rear-light state) comes from a MaterialPropertyBlock, declared as GPU-instanced
//           properties (multi_compile_instancing): on GLES3/Vulkan/Metal equal meshes of all rivals draw as one
//           instanced call; on GLES2 (no instancing) the same properties are plain uniforms and each renderer is one
//           draw call with the shared material (no material switch between cars).
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
        _TyreRad ("Merged: tyre radius front, rear", Vector) = (0.33, 0.33, 0, 0)
        _Far ("Merged: far model (no carbon weave, no tyre sheen)", Float) = 0
    }
    CGINCLUDE
    #include "UnityCG.cginc"
    #if defined(_MERGED)
        UNITY_INSTANCING_BUFFER_START(CarProps)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _LivA)     // livery primary
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _LivB)     // livery secondary (wings, split areas)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _LivC)     // livery accent; a = 1 split on
            UNITY_DEFINE_INSTANCED_PROP(half4, _GlowF)     // brake-disc glow, front axle
            UNITY_DEFINE_INSTANCED_PROP(half4, _GlowR)     // rear axle
            UNITY_DEFINE_INSTANCED_PROP(half4, _LightCol)  // rear / rain light emission (brake, wet, harvest blink)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _StripeCol)// tyre compound stripe
            UNITY_DEFINE_INSTANCED_PROP(half4, _Axle)      // brake dust front, rear
            UNITY_DEFINE_INSTANCED_PROP(half4, _TyreWD)    // tyre wear front, dirt front, wear rear, dirt rear
        UNITY_INSTANCING_BUFFER_END(CarProps)
        #define MP(n) UNITY_ACCESS_INSTANCED_PROP(CarProps, n)
        half4 _TyreRad; half _Far;
        half MIs(half id, half k) { return step(abs(id - k), 0.5); }
        half MId(half a) { return floor(a * 255.0 + 0.5); }
        // the livery split of the body paint (the same areas as the per-part passes below)
        half3 MLivery(float3 p, half ny, half3 c1, half3 c2, half3 c3)
        {
            float ax = abs(p.x);
            half side = step(0.30, ax) * step(-1.2, p.z) * step(p.z, 0.99);
            half sideSec = side * step(p.z, 0.95) * step(0.55, ny);
            half sideAcc = side * step(0.42, ny) * (1.0 - sideSec);
            half eng = step(ax, 0.32) * step(-1.7, p.z) * step(p.z, -0.01);
            half engSec = eng * step(p.z, -0.05) * step(0.30, ny);
            half engAcc = eng * step(0.18, ny) * (1.0 - engSec);
            half noseSec = step(2.45, p.z);
            half noseAcc = step(2.40, p.z) * (1.0 - noseSec);
            half sec = saturate(sideSec + engSec + noseSec);
            half acc = saturate(sideAcc + engAcc + noseAcc) * (1.0 - sec);
            return lerp(lerp(c1, c2, sec), c3, acc);
        }
        // part IDs (CarModel.Id*): 0 body paint, 1 wings (secondary), 2 carbon, 3 metal, 4/5 tyre front/rear,
        // 6/7 rim + brake front/rear, 8 rear lights, 9 tyre stripe, 10 helmet, 11 steering wheel.
        // base colour and (clear coat, gloss, metal): the values the per-part materials had (CarModel.CreateFromAsset)
        void MMat(half id, out half3 base, out half3 cgm)
        {
            base = half3(0.06, 0.065, 0.075); cgm = half3(0.55, 0.7, 0.1);                                    // 2 carbon
            half k = MIs(id, 0); base = lerp(base, MP(_LivA).rgb, k); cgm = lerp(cgm, half3(1.0, 0.92, 0.15), k);
            k = MIs(id, 1); base = lerp(base, MP(_LivB).rgb, k); cgm = lerp(cgm, half3(1.0, 0.9, 0.05), k);
            k = MIs(id, 3); base = lerp(base, half3(0.55, 0.57, 0.6), k); cgm = lerp(cgm, half3(0.3, 0.8, 0.9), k);
            k = step(3.5, id) * step(id, 5.5); base = lerp(base, half3(0.085, 0.085, 0.09), k); cgm = lerp(cgm, half3(0.22, 0.38, 0.0), k);
            k = saturate(step(5.5, id) * step(id, 7.5) + MIs(id, 11)); base = lerp(base, half3(0.2, 0.21, 0.23), k); cgm = lerp(cgm, half3(0.3, 0.75, 0.85), k);
            k = MIs(id, 8); base = lerp(base, half3(1.0, 0.15, 0.12), k); cgm = lerp(cgm, half3(0.5, 0.8, 0.0), k);
            k = MIs(id, 9); base = lerp(base, MP(_StripeCol).rgb, k); cgm = lerp(cgm, half3(0.25, 0.5, 0.0), k);
            k = MIs(id, 10); base = lerp(base, half3(1.0, 0.82, 0.18), k); cgm = lerp(cgm, half3(1.0, 0.93, 0.1), k);
        }
        // helmet visor / stripe from the body-space position (the helmet sphere sits at (0, 0.72, 0.05), scale 0.26/0.28/0.3)
        half MVisor(float3 op, out half stripe)
        {
            float3 p = (op - float3(0.0, 0.72, 0.05)) / float3(0.26, 0.28, 0.3);
            half visor = saturate((p.z - 0.12) * 25.0) * saturate((p.y + 0.06) * 40.0) * saturate((0.16 - p.y) * 40.0);
            stripe = (1.0 - visor) * step(abs(p.x), 0.045) * step(0.0, p.y);
            return visor;
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
            #pragma multi_compile_fog
            #pragma multi_compile_local __ _CARBON _TYRE _WHEEL _HELMET _MERGED
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Grade.cginc"
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
                half coatK = _Coat, metal = _Metal, specK = 1.0, glossAdd = 0.0, gloss0 = _Gloss;
                half3 emis = 0, envTint = 1;
                #if defined(_MERGED)
                {
                    // one mesh, many parts: the part ID picks the material (see MMat)
                    half id = MId(i.col.a); half3 cgm; MMat(id, base, cgm);
                    coatK = cgm.x; gloss0 = cgm.y; metal = cgm.z; ao = i.col.r;
                    half3 on = normalize(i.on);
                    half body = MIs(id, 0) * step(0.5, MP(_LivC).a);
                    base = lerp(base, MLivery(i.op, on.y, MP(_LivA).rgb, MP(_LivB).rgb, MP(_LivC).rgb), body);
                    // carbon weave (carbon parts, the wings' undersides), only near the camera
                    half near = (1.0 - _Far) * saturate(1.6 - length(_WorldSpaceCameraPos.xyz - i.wp) * 0.27);
                    half cw = saturate(MIs(id, 2) + MIs(id, 1) * smoothstep(-0.25, -0.6, on.y));
                    if (cw > 0.0)
                    {
                        half3 an = abs(on);
                        float2 w = (an.x > max(an.y, an.z) ? i.op.zy : (an.y > an.z ? i.op.xz : i.op.xy)) * 140.0;
                        float2 cell = floor(w), fr = frac(w);
                        half sel = step(0.5, frac((cell.x + cell.y) * 0.25));
                        half across = lerp(fr.y, fr.x, sel), prof = 4.0 * across * (1.0 - across);
                        half3 cb = lerp(base, half3(0.06, 0.065, 0.075), MIs(id, 1));
                        cb *= lerp(1.0, lerp(0.72, 1.18, prof) * lerp(0.9, 1.1, sel), near);
                        base = lerp(base, cb, cw);
                        coatK = lerp(coatK, 0.6, cw);
                        specK = lerp(1.0, lerp(1.0, lerp(0.55, 1.45, sel) * prof + 0.3, near), cw);
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
                        base *= 1.0 + 0.45 * band;
                        base = lerp(base, half3(0.17, 0.155, 0.14), tread * saturate(0.35 * wear + 0.6 * dirt));
                        coatK *= lerp(1.0 + 0.6 * band, 1.7 * (1.0 - 0.75 * wear) * (1.0 - 0.85 * dirt), tread);
                        glossAdd = -0.15 * tread * wear;
                    }
                    // rims and brakes: dust and disc glow, per axle
                    half hub = step(5.5, id) * step(id, 7.5);
                    half dust = lerp(MP(_Axle).y, MP(_Axle).x, front) * hub, disc = i.col.g * hub;
                    base = lerp(base * (1.0 - 0.55 * dust) + half3(0.012, 0.009, 0.006) * dust, half3(0.1, 0.1, 0.105), disc);
                    coatK *= 1.0 - 0.6 * dust; metal *= 1.0 - 0.5 * dust; glossAdd += -0.25 * dust;
                    emis = lerp(MP(_GlowR).rgb, MP(_GlowF).rgb, front) * disc + MP(_LightCol).rgb * MIs(id, 8);
                    // helmet
                    half hs; half visor = MVisor(i.op, hs) * MIs(id, 10); hs *= MIs(id, 10);
                    base = lerp(lerp(base, half3(0.08, 0.08, 0.1), hs), half3(0.015, 0.015, 0.02), visor);
                    coatK = lerp(coatK, 1.0, visor); metal = lerp(metal, 0.9, visor); specK *= 1.0 + visor;
                    envTint = lerp(half3(1, 1, 1), half3(1.0, 0.72, 0.38), visor);
                }
                #elif defined(_CARBON)
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
                half gloss = saturate(gloss0 + 0.08 * _Wet + glossAdd);
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
                c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
                return c;
            }
            ENDCG
        }
    }
    // Low graphics level (GameManagerPerf.cs sets this shader's maximumLOD to 100): sun, sky, clear coat and the sun
    // highlight are worked out per vertex (no reflection-probe read, no per-pixel pow); per pixel only the livery
    // split, the baked AO, the helmet visor and the brake dust / disc glow. Carbon weave and tyre sheen are left out.
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
            #pragma multi_compile_fog
            #pragma multi_compile __ F2_GRADE_LOW
            #pragma multi_compile_local __ _CARBON _TYRE _WHEEL _HELMET _MERGED
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Grade.cginc"
            #include "Lighting.cginc"

            fixed4 _Color, _Color2, _Color3;
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
                #endif
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                half3 N = UnityObjectToWorldNormal(v.normal);
                half3 V = normalize(_WorldSpaceCameraPos.xyz - wp);
                half3 L = normalize(_WorldSpaceLightPos0.xyz);
                half ndl = saturate(dot(N, L));
                half3 R = reflect(-V, N);
                half3 env = ShadeSH9(half4(R, 1)) * 0.9;
                env = lerp(env, _GroundRefl.rgb, 1.0 - smoothstep(-0.12, 0.02, R.y));
                env *= lerp(0.35, 1.0, _CarLit);
                half x = 1.0 - saturate(dot(N, V)), x2 = x * x;
                half coat = saturate((0.04 + 0.96 * x2 * x2 * x) * coat0 * (1.0 + 0.35 * _Wet) + 0.03 * _Wet);
                half gloss = saturate(gloss0 + 0.08 * _Wet);
                half nh = saturate(dot(N, normalize(L + V)));
                half spec = pow(nh, lerp(16.0, 128.0, gloss)) * ndl * lerp(0.5, 3.0, gloss);
                o.diff = (ndl * _LightColor0.rgb + ShadeSH9(half4(N, 1))) * (1.0 - metal0 * 0.6) * (1.0 - 0.12 * _Wet) * (1.0 - coat);
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
                half3 base = _Color.rgb;
                if (_Livery > 0.5)
                {
                    // the same areas as the full pass
                    float3 p = i.op; half ny = i.ny; float ax = abs(p.x);
                    half side = step(0.30, ax) * step(-1.2, p.z) * step(p.z, 0.99);
                    half sideSec = side * step(p.z, 0.95) * step(0.55, ny);
                    half sideAcc = side * step(0.42, ny) * (1.0 - sideSec);
                    half eng = step(ax, 0.32) * step(-1.7, p.z) * step(p.z, -0.01);
                    half engSec = eng * step(p.z, -0.05) * step(0.30, ny);
                    half engAcc = eng * step(0.18, ny) * (1.0 - engSec);
                    half noseSec = step(2.45, p.z);
                    half noseAcc = step(2.40, p.z) * (1.0 - noseSec);
                    half sec = saturate(sideSec + engSec + noseSec);
                    half acc = saturate(sideAcc + engAcc + noseAcc) * (1.0 - sec);
                    base = lerp(lerp(base, _Color2.rgb, sec), _Color3.rgb, acc);
                }
                half3 emis = 0;
                half ao = lerp(1.0, i.col.r, _AO);
                #if defined(_MERGED)
                    half id = MId(i.col.a); base = i.mbase; ao = i.col.r;
                    base = lerp(base, MLivery(i.op, i.ny, MP(_LivA).rgb, MP(_LivB).rgb, MP(_LivC).rgb), MIs(id, 0) * step(0.5, MP(_LivC).a));
                    half front = step(id, 4.5) + MIs(id, 6), hub = step(5.5, id) * step(id, 7.5);
                    half dust = lerp(MP(_Axle).y, MP(_Axle).x, front) * hub, disc = i.col.g * hub;
                    base = lerp(base * (1.0 - 0.55 * dust), half3(0.1, 0.1, 0.105), disc);
                    emis = lerp(MP(_GlowR).rgb, MP(_GlowF).rgb, front) * disc + MP(_LightCol).rgb * MIs(id, 8);
                    half hs; half visor = MVisor(i.op, hs) * MIs(id, 10); hs *= MIs(id, 10);
                    base = lerp(lerp(base, half3(0.08, 0.08, 0.1), hs), half3(0.3, 0.22, 0.12), visor);
                #elif defined(_WHEEL)
                    half disc = i.col.g;
                    base = lerp(base * (1.0 - 0.55 * _Dust), half3(0.1, 0.1, 0.105), disc);
                    emis = _Glow.rgb * disc;
                #elif defined(_HELMET)
                    float3 hp = i.op;
                    half visor = saturate((hp.z - 0.12) * 25.0) * saturate((hp.y + 0.06) * 40.0) * saturate((0.16 - hp.y) * 40.0);
                    half stripe = (1.0 - visor) * step(abs(hp.x), 0.045) * step(0.0, hp.y);
                    base = lerp(lerp(base, _Color3.rgb, stripe), half3(0.3, 0.22, 0.12), visor);
                #endif
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
