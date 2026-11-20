#ifndef F2_CARPAINT_COMMON
#define F2_CARPAINT_COMMON
// CarPaint common (CGINCLUDE): shared by CarPaint.shader and CarPaintFar.shader (one copy of the code; edit here).
    #include "UnityCG.cginc"
    #include "CarGhost.cginc"
    float _Ghost; // ghost fade 0..1 (CarGhost.cginc; plain uniform, set through a per-renderer MaterialPropertyBlock;
                  // read only in the F2_GHOST variant, which CarGhost.cs swaps in on a twin material while a car is a ghost)
    #define CARBON_BASE half3(0.022, 0.024, 0.028)
    // 2x2 twill from the object position: (brightness, highlight) multipliers; each tow is a little cylinder (brighter
    // along its crown) and catches the light more in one direction. fade 0 = plain (1, 1).
    float2 Weave(float3 op, float3 on, float fade)
    {
        float3 an = abs(on);
        float2 w = (an.x > max(an.y, an.z) ? op.zy : (an.y > an.z ? op.xz : op.xy)) * 300.0; // ~3.3 mm tows (was 6 mm: a coarse herringbone from the cockpit)
        float2 cell = floor(w), fr = frac(w);
        float sel = step(0.5, frac((cell.x + cell.y) * 0.25));
        float across = lerp(fr.y, fr.x, sel), prof = 4.0 * across * (1.0 - across);
        return float2(lerp(1.0, lerp(0.92, 1.04, prof) * lerp(0.97, 1.03, sel), fade), lerp(1.0, lerp(0.8, 1.2, sel) * prof * 0.8 + 0.6, fade)); // subtle: real twill reads as a shimmer in the clear coat, not as bricks
    }
    // how much weave to show: full while a tow covers 4+ pixels, gone below 2 (derivatives: call outside branches)
    float WeaveFade(float3 op) { float3 d = fwidth(op) * 300.0; return saturate(2.0 - 4.0 * max(d.x, max(d.y, d.z))); }
    // Highlight shoulder for the lit base colour (hue kept: the whole colour is scaled by one factor, so a white stays
    // white and never turns grey or tinted). The grade clips a neutral at ~0.6 (Monza), and sunlit white paint arrives
    // at 1.3-1.7: the whole engine cover was one flat 255. Unsaturated paint rolls off softly from 0.3 towards 0.58
    // (sunlit ~247, a flank turned away ~228, shade ~193); saturated paint (and 'satW', the driver's fabric) keeps the
    // existing roll-off on its strongest channel. All float (see the PRECISION RULE above).
    float3 PaintRoll(float3 df, float satW)
    {
        float mxc = max(df.r, max(df.g, df.b)), mnc = min(df.r, min(df.g, df.b));
        float satc = saturate((mxc - mnc) / max(mxc, 0.002));
        float rollS = 1.0 / (1.0 + 0.55 * max(mxc - 0.35, 0.0));
        float h = max(mxc - 0.3, 0.0);
        float rollW = (min(mxc, 0.3) + h / (1.0 + h * 3.57)) / max(mxc, 0.002); // knee 0.3, ceiling 0.3 + 1/3.57 = 0.58
        return df * lerp(rollW, rollS, max(satc * satc, satW));
    }
    // Livery areas from the car's own shape (object space: +z forward, nose tip at z 2.8, x across, ny = how much the
    // surface faces up). style 0: engine cover + sidepod tops + nose tip; 1: engine cover + the whole nose cone, sidepods
    // in the primary; 2: the whole sidepods + nose tip, engine cover in the primary; 3: a centre stripe down nose and
    // engine cover. Returns (secondary weight, accent weight); the accent is a thin line along the secondary's edge.
    float2 LiverySplit(float3 p, float ny, float style)
    {
        float ax = abs(p.x);
        float s1 = step(0.5, style) * step(style, 1.5), s2 = step(1.5, style) * step(style, 2.5), s3 = step(2.5, style);
        float s0 = 1.0 - saturate(s1 + s2 + s3);
        float side = step(0.30, ax) * step(-1.2, p.z) * step(p.z, 0.99);
        float sideSec = side * step(p.z, 0.95) * (s0 * step(0.55, ny) + s2 * step(0.2, ny));
        float sideAcc = side * step(0.42, ny) * (1.0 - sideSec) * lerp(1.0, step(ny, 0.56), s1 + s3);
        float eng = step(ax, 0.32) * step(-1.7, p.z) * step(p.z, -0.01);
        float engSec = eng * step(p.z, -0.05) * step(0.30, ny) * (1.0 - s2 - s3);
        float engAcc = eng * step(0.18, ny) * (1.0 - engSec) * lerp(1.0, step(ny, 0.32), s2) * (1.0 - s3);
        float noseT = lerp(2.45, 1.5, s1);
        float noseSec = step(noseT, p.z);
        float noseAcc = step(noseT - 0.05, p.z) * (1.0 - noseSec);
        float top = step(0.3, ny) * step(-1.7, p.z) * s3;
        float stripeSec = top * step(ax, 0.10), stripeAcc = top * step(ax, 0.15) * (1.0 - stripeSec);
        float sec = saturate(sideSec + engSec + noseSec + stripeSec);
        float acc = saturate(sideAcc + engAcc + noseAcc + stripeAcc) * (1.0 - sec);
        return float2(sec, acc);
    }
    // Colour-preserving clear coat. A plain white-ish add of the sky over a saturated paint turned the deep red of
    // "Scarlet Dart" pink wherever the coat reflects a lot (the sidepod and nose tops seen from the cockpit, at a
    // grazing angle, mirror the bright sky overhead). Real deep paint keeps its hue: the coat's dim, diffuse sky goes
    // back out through the pigment. So the reflection's luminance is kept but, for saturated paint, the dim part of it
    // takes the paint's hue; bright reflections (clouds, the horizon glare) and the true grazing silhouette stay a
    // neutral mirror. White, grey, black and carbon (no chroma) are unchanged. graze = (1 - N.V)^4.
    half3 KeepHue(half3 refl, half3 base, half graze)
    {
        float mx = max(base.r, max(base.g, base.b)), mn = min(base.r, min(base.g, base.b));
        float sat = saturate((mx - mn) / max(mx, 0.02));
        half lr = dot(refl, half3(0.2126, 0.7152, 0.0722));
        half3 hue = base / max(mx, 0.02);
        // near-neutral surfaces (carbon 0.022/0.024/0.028, greys: sat < 0.25) stay exactly neutral: no blue/violet cast
        float k = 0.5 * sat * sat * saturate(sat * 4.0 - 1.0) * (1.0 - graze) * saturate(1.5 - lr * 1.6); // (was 0.8: the sidepods seen from the cockpit lost every trace of the sky and read as flat red)
        return lerp(refl, hue * lr, (half)k);
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
            UNITY_DEFINE_INSTANCED_PROP(float4, _GhostV)   // x = ghost fade for an instanced rival (CarModel.SetGhost); 0 by default
        UNITY_INSTANCING_BUFFER_END(CarProps)
        #define MP(n) UNITY_ACCESS_INSTANCED_PROP(CarProps, n)
        // the ghost fade: the plain _Ghost (the player's driver, any MPB) or a rival's instanced one
        #define GHOST_G max(_Ghost, MP(_GhostV).x)
        float4 _TyreRad; float _Far;
        float MIs(float id, float k) { return step(abs(id - k), 0.5); }
        float MId(float a) { return floor(a * 255.0 + 0.5); }
        // the livery split of the body paint (the same areas as the per-part passes below)
        half3 MLivery(float3 p, float ny, float style, half3 c1, half3 c2, half3 c3)
        {
            float2 w = LiverySplit(p, ny, style);
            return lerp(lerp(c1, c2, w.x), c3, w.y);
        }
        // part IDs (CarModel.Id*): 0 body paint, 1 wings (secondary), 2 carbon, 3 metal, 4/5 tyre front/rear,
        // 6/7 rim + brake front/rear, 8 rear lights, 9 tyre stripe, 11 steering wheel; the driver (DriverModel): 12 suit
        // (livery primary), 13 suit trim (secondary), 14 / 15 helmet shell / design, 16 visor, 17 dark fabric, 18 glove
        // (dark grey), 19 glove panel (livery primary).
        // base colour and (clear coat, gloss, metal): the values the per-part materials had (CarModel.CreateFromAsset)
        void MMat(float id, out half3 base, out half3 cgm)
        {
            // (linear values: CarModel gives the per-part materials the same ones via Lin(); livery colours come in
            // through CarModel.LinV: converted from sRGB once, sent as a vector on the merged path)
            base = CARBON_BASE; cgm = half3(0.35, 0.88, 0.05);                                               // 2 carbon
            float k = MIs(id, 0); base = lerp(base, MP(_Color).rgb, k); cgm = lerp(cgm, half3(1.0, 0.95, 0.06), k);
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
            // gloves: dark grey suede-like fabric (palms, fingers) with team-colour panels (back of the hand, cuff band)
            k = MIs(id, 18); base = lerp(base, half3(0.034, 0.035, 0.038), k); cgm = lerp(cgm, half3(0.05, 0.32, 0.0), k);
            k = MIs(id, 19); base = lerp(base, MP(_Color).rgb * 0.75, k); cgm = lerp(cgm, half3(0.06, 0.3, 0.0), k);
        }
    #else
        #define GHOST_G _Ghost
    #endif
#endif
