// Road.cginc — shared code of Road.shader (both levels). Define ROAD_HQ before including for the Medium/High path.
// Weather: the global _Wet (Weather.cs: 0 dry, 0.5 damp, 1 wet; set with Shader.SetGlobalFloat, not a material
// property) darkens the surface and mirrors the sky in it with a water Fresnel term. Damp: the racing line has dried.
// Wet: standing water in the low, off-line patches (HQ only). At _Wet = 0 every wet term is zero.
//
// Variety without textures (pure maths, box-filtered so nothing shimmers far away): the lap is laid in paving sections
// of ~113 m, each a shade apart, with a sealed joint across the road between them; some sections carry a rectangular
// soft-edged, barely darker resurfaced patch (both levels) and a wandering tar snake (HQ).
// HQ also reads _F2RoadFine (global, EnvTex.RoadFine, set by RoadSurface.cs), 1.4 m per tile, close up only:
//   R aggregate albedo, G polish of the stone tops, BA stone slope (0.5 = flat). The slope tilts the normal of the sun
//   sheen, so single stones glint near the car; all four channels are 0.5 = neutral and the mips average them away.
// Sharp sun shadows (RoadShadow.cs, both levels, one extra read): when the mask is built the vertex colour holds the
// sky/ambient light only and its alpha the bake's sun visibility; the sun comes back as _F2SunLin x N.L x alpha x mask.
// The mask (_F2RoadShadow, R8) is in track space: U across (_F2RoadShadowP.w either side of the centreline), V along,
// the lap cut into strips laid side by side; read with gradients of the continuous coordinates (no seam at a strip
// change). Without a mask _F2SunLin = 0 and the vertex colour is the whole baked light, as before.
#ifndef F2_ROAD_INCLUDED
#define F2_ROAD_INCLUDED
#include "UnityCG.cginc"
#include "Grade.cginc"

struct appdata
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float2 uv2 : TEXCOORD2;
    float2 uv3 : TEXCOORD3;   // edge dirt (left, right) 0..1: gravel stones / dust / dragged-on dirt (WorldBuilder.EdgeDirt)
    float4 color : COLOR;
};
struct v2f
{
    float4 pos : SV_POSITION;
    float4 uv : TEXCOORD0;    // lateral m, along m, macro uv
    float4 misc : TEXCOORD1;  // x: lateral distance to the racing line (m) or kerb across, y: rubber amount (or -1 = kerb), z: detail fade, w: bake's sun visibility (vertex alpha)
    half3 color : TEXCOORD2;  // baked light (sky/ambient only when the shadow mask is on)
    half3 sunc : TEXCOORD6;   // full sun on this vertex (_F2SunLin x N.L); 0 without the mask
    UNITY_FOG_COORDS(3)
    float2 dirt : TEXCOORD7;  // edge dirt (left, right)
#ifdef ROAD_HQ
    float3 view : TEXCOORD4;  // surface -> camera (world)
    half3 n : TEXCOORD5;
#else
    half fres : TEXCOORD4;    // Low: Fresnel (1 - N.V)^5 per vertex, for the wet sky reflection
#endif
};

sampler2D _MainTex;
#ifdef ROAD_HQ
sampler2D _F2RoadFine; // global (RoadSurface.cs)
#endif
fixed4 _Asphalt, _Dust, _Paint, _KerbRed, _SunCol;
sampler2D _F2RoadShadow; // global (RoadShadow.cs)
float4 _F2RoadShadowP;   // x: strips per metre along, y: 1 / strips, z: 1 / width covered (m), w: half width (m)
half4 _F2SunLin;         // the sun's linear colour x intensity, as baked (0: no mask, the vertex colour is all the light)
float _HalfWidth, _MipBias;
float4 _Grid, _SunDir;
float4 _Loops;           // x: timing-loop pitch (m), y / z: the sector ends (m along)
half _Wet; // global (Weather.cs)

v2f vert (appdata v)
{
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex);
    float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
    // detail tiles every 5 m; the macro layer every ~43 m, turned ~37 degrees so the two never line up
    float2 m = v.uv * (1.0 / 43.0);
    o.uv = float4(v.uv.x, v.uv.y, m.x * 0.8 - m.y * 0.6, m.x * 0.6 + m.y * 0.8);
    bool kerb = v.uv2.y < -0.5;
    o.misc.x = kerb ? v.uv2.x : v.uv.x - v.uv2.x;
    o.misc.y = v.uv2.y;
    float3 toCam = _WorldSpaceCameraPos - wp;
    float dist = length(toCam);
    o.misc.z = saturate((95.0 - dist) / 70.0); // full grain up to 25 m, gone by 95 m (the mips have averaged it anyway)
    o.color = v.color.rgb * 2.0;               // stored light 0.5 = full brightness (LightBake)
    half3 nw = UnityObjectToWorldNormal(v.normal);
    o.sunc = _F2SunLin.rgb * saturate(dot(nw, _SunDir.xyz));
    o.misc.w = v.color.a;                      // the bake's sun visibility (RoadShadow; 1 without the mask)
    o.dirt = v.uv3;
    UNITY_TRANSFER_FOG(o, o.pos);
#ifdef ROAD_HQ
    o.view = toCam;
    o.n = nw;
#else
    half fv = 1.0 - saturate(dot(toCam / max(dist, 1e-3), nw));
    half fv2 = fv * fv; o.fres = fv2 * fv2 * fv;
#endif
    return o;
}

// box-filtered coverage of the band a..b along x, for a pixel w wide (same units): crisp, never aliased
half Band(float x, float a, float b, float w)
{
    return (half)(saturate((x - a) / w + 0.5) - saturate((x - b) / w + 0.5));
}
// 0..1 hash of a float (no sin: stays exact on mobile GPUs)
float Hash1(float n)
{
    float h = frac(n * 0.1031);
    h *= h + 33.33;
    h *= h + h;
    return frac(h);
}

// small things scattered on a ~6 cm grid (gravel stones, rubber marbles): coverage 0..1 of a round grain in this cell,
// present in "density" of the cells. cell = position in cells (along wrapped to keep the hash exact); w = cells per pixel
half Grains(float2 cell, float density, float seed, float w)
{
    float2 id = floor(cell), f = frac(cell) - 0.5;
    float h = Hash1(id.y * 0.917 + Hash1(id.x * 1.37 + seed) * 431.0 + seed * 17.0);
    float h2 = Hash1(h * 97.3 + seed);
    float2 o = (float2(h2, frac(h2 * 7.31)) - 0.5) * 0.3;          // off the cell centre
    float r = 0.18 + 0.17 * frac(h * 13.7);
    float on = h < density ? 1.0 : 0.0;
    return (half)(saturate((r - length(f - o)) / max(w, 0.02) + 0.5) * on);
}

#ifdef ROAD_HQ
// one car's pair of tyre marks (wheels ~1.6 m apart): x = metres from the racing line; the pair has its own offset
// (within +-spread/2) per stretch of len metres, wanders a little, and fades in and out along the stretch (zero at its
// ends, so the offset can jump there); some stretches have none. Box-filtered with the pixel width w (m).
half TyrePair(float x, float along, float len, float seed, float spread, float w)
{
    float c = floor(along / len), f = along / len - c;
    float h = Hash1(c * 7.13 + seed * 11.0), h2 = Hash1(c * 3.71 + seed * 5.3);
    float on = saturate(sin(f * 3.14159) * 3.0 - 0.5) * (h2 > 0.3 ? 1.0 : 0.0);
    float o = (h - 0.5) * spread + 0.15 * sin(along * 0.047 + seed * 4.0);
    float xa = abs(x - o), mw = 0.14 + 0.12 * h2;
    return Band(xa, 0.8 - mw * 0.5, 0.8 + mw * 0.5, w) * (half)(on * (0.55 + 0.45 * h2));
}
#endif

fixed4 frag (v2f i) : SV_Target
{
    float lat = i.uv.x, along = i.uv.y;
    // pixel footprints (outside any branch): metres across / along the road, kerb "across" units
    float wl = max(fwidth(lat), 1e-4), wa = max(fwidth(along), 1e-4), wk = max(fwidth(i.misc.x), 1e-4);
    bool kerb = i.misc.y < -0.5;
    half kerbF = kerb ? 1.0 : 0.0;

    // ---- asphalt textures
#ifdef ROAD_HQ
    half4 d = tex2Dbias(_MainTex, float4(i.uv.xy * 0.2, 0, _MipBias));
    half4 mac = tex2D(_MainTex, i.uv.zw);
    // fine aggregate, 1.4 m per tile, turned ~23 degrees against the 5 m layer
    float2 fu = i.uv.xy * (1.0 / 1.4);
    half4 fn = tex2D(_F2RoadFine, float2(fu.x * 0.92 - fu.y * 0.39, fu.x * 0.39 + fu.y * 0.92));
    half blotch = mac.g, patch = mac.b;
#else
    half4 d = tex2D(_MainTex, i.uv.xy * 0.2);
    half blotch = d.g, patch = 0.0;
#endif
    half fade = i.misc.z;
    half grain = lerp(0.5, d.r, fade * fade);                    // grain only close up: mid / far asphalt is smooth

#ifdef ROAD_HQ
    half fineFade = saturate(fade * 2.5 - 1.5);                  // only the first ~15 m, gone by ~35 m
    // fine texels per pixel (1.4 m / 256): stone glints and slopes fade out once a pixel spans several stones, so
    // nothing crawls at grazing angles
    half fineTexels = (half)min(max(wl, wa) * (256.0 / 1.4), 1000.0);
    // ---- micro relief in the sun: the photo aggregate's slopes (BA) tilt the surface stone by stone, so the sunlit
    // asphalt near the car has real light and shade in it instead of a flat print. Linear (unnormalised) so the mean
    // brightness is unchanged; the same fade as the glints, none on the kerbs (painted, smooth)
    half relief = 0.55 * fineFade * saturate(1.5 - fineTexels * 0.125) * (1.0 - 0.7 * kerbF);
    half ndl0 = max(dot(normalize(i.n), (half3)_SunDir.xyz), 0.25);
    half sunRelief = clamp(1.0 + dot(half3(fn.b - 0.5, 0.0, fn.a - 0.5), (half3)_SunDir.xyz) * relief / ndl0, 0.5, 1.6);
#else
    half sunRelief = 1.0;
#endif

    // ---- light: ambient + sun x the sharp shadow mask (tree dapples, stands, bridges, fences)
    float2 sp = float2(lat * _F2RoadShadowP.z + 0.5, along * _F2RoadShadowP.x); // across 0..1, strips along
    float2 sgx = ddx(sp), sgy = ddy(sp);
    float strip = floor(sp.y);
    float2 suv = float2((strip + saturate(sp.x)) * _F2RoadShadowP.y, sp.y - strip);
#ifdef ROAD_HQ
    float2 gsc = float2(_F2RoadShadowP.y, 1.0) * 1.5; // a touch of blur: softer penumbrae, no crawling far away
#else
    float2 gsc = float2(_F2RoadShadowP.y, 1.0);
#endif
    half shade = tex2Dgrad(_F2RoadShadow, suv, sgx * gsc, sgy * gsc).r;
    half3 light = i.color + i.sunc * (i.misc.w * shade * sunRelief);
    // ~0.9 in shade, ~1.75 in the sun: how much sun reaches this point (sheen, wet glints)
    half sunVis = saturate((dot(light, half3(0.3, 0.59, 0.11)) - 1.0) * 1.5);

    // ---- paving sections: ~113 m each, a shade apart, a sealed joint across the road between them
    const float SecLen = 113.0;
    float secF = along / SecLen;
    float sec = floor(secF);
    float sa = along - sec * SecLen;                             // metres into this section
    float h1 = Hash1(sec + 0.5), h2 = Hash1(sec + 17.3), h3 = Hash1(sec + 41.7), h4 = Hash1(sec + 73.1);
    float jd = abs(frac(secF + 0.5) - 0.5) * SecLen;             // metres to the nearest joint
    half joint = Band(jd, -1.0, 0.03, wa);                       // 6 cm sealed joint
    // resurfaced patch (about half the sections): a rectangle of fresher, darker asphalt, often a lane wide
    float pa0 = 12.0 + h3 * 55.0, pa1 = pa0 + 6.0 + 22.0 * h4;
    float pc = (h1 * 2.0 - 1.0) * max(_HalfWidth - 2.5, 0.0), phw = 1.2 + 1.8 * Hash1(sec + 93.7);
    // (soft: the new mat blends into the old over ~1.5 m along and ~0.6 m across and is only a breath darker; a hard
    // box with a tar outline read as a mystery rectangle on the road from the cockpit. Floats only for the compares.)
    float hasPatchF = h2 > 0.5 ? 1.0 : 0.0;
    float patchA = smoothstep(pa0, pa0 + 1.5, sa) * (1.0 - smoothstep(pa1 - 1.5, pa1, sa));
    float patchL = smoothstep(pc - phw, pc - phw + 0.6, lat) * (1.0 - smoothstep(pc + phw - 0.6, pc + phw, lat));
    half inPatch = (half)(patchA * patchL * hasPatchF);

    // ---- asphalt: aggregate grain, metre-scale blotches, darker resurfaced patches
    half tone = (0.88 + 0.24 * grain) * (0.88 + 0.24 * blotch) * (0.97 + 0.06 * d.g) * (1.0 - 0.10 * patch);
    tone *= 1.0 + 0.07 * (half)(h1 - 0.5);                       // section shade
    tone *= 1.0 - 0.045 * inPatch;                                // fresh patch: darker binder
#ifdef ROAD_HQ
    tone *= 1.0 + 0.16 * (fn.r - 0.5) * fineFade;                // single stones and the dark binder between them
#endif
    half3 col = _Asphalt.rgb * tone;

    // ---- rubber laid along the racing line: a broad band plus the two tyre tracks, patchy along its length
    half x = (half)i.misc.x;
    half broad = saturate(1.0 - x * x * 0.32);                 // ~1.8 m half width
    half tracks = saturate(1.0 - abs(abs(x) - 0.8) * 2.2);     // wheels ~1.6 m apart
    half rub = kerb ? 0.0 : saturate(i.misc.y) * broad * (0.55 + 0.45 * tracks) * (0.7 + 0.6 * blotch);
#ifdef ROAD_HQ
    // streaks along the direction of travel (each tyre lays its own); faded once a pixel spans ~17 cm across
    float xs = i.misc.x;
    half streak = (half)(0.5 + 0.5 * sin(xs * 21.0 + sin(xs * 6.3 + along * 0.013) * 1.7));
    rub *= lerp(1.0, 0.65 + 0.7 * streak, (half)saturate(1.0 - wl * 6.0));
#endif
    col *= 1.0 - 0.48 * rub; // rubbered, not black: in the sun the line reads as a darker grey
#ifdef ROAD_HQ
    // ---- tyre marks: single cars' pairs of dark lines along and around the racing line (each pair its own offset per
    // stretch, wandering, fading in and out), more where the line carries more rubber (corners, braking zones)
    half marks = TyrePair(xs, along, 41.0, 1.0, 1.3, wl) + TyrePair(xs, along, 67.0, 2.0, 2.8, wl) + TyrePair(xs, along, 29.0, 3.0, 0.7, wl);
    col *= 1.0 - (half)saturate(marks) * (0.12 + 0.2 * saturate(i.misc.y)) * (0.6 + 0.6 * blotch) * (1.0 - kerbF);
#endif

    // ---- dust off-line: lighter towards the edges where nobody drives (less on fresh patches)
    half edge = smoothstep(_HalfWidth - 3.2, _HalfWidth - 0.3, abs(lat));
    // (a light veil only: at 0.45 the outer thirds of the road went chalk white in the sun)
    col = lerp(col, _Dust.rgb * (0.85 + 0.3 * grain), edge * 0.2 * (1.0 - rub) * (1.0 - 0.3 * inPatch));

    // ---- the outer edge of the mesh: the last ~1.5 pixels fade to a dark dirt tone, so the polygon edge against the
    // verge or the kerb has far less contrast to step (no hard, jagged line in bends without MSAA)
    half rimW = (half)min(1.5 * wl, 2.0); // (clamped: no half overflow at the horizon)
    col = lerp(col, _Dust.rgb * 0.45, 0.6 * (half)saturate((abs(lat) - (_HalfWidth - rimW)) / max(rimW, 1e-4)));
    // grime against the kerb / verge: the strip outside the white line is never swept by tyres (oil, rubber dust)
    col *= 1.0 - 0.16 * Band(abs(lat), _HalfWidth - 0.16, _HalfWidth + 1.0, wl) * (1.0 - kerbF);

    // ---- marbles: off the line in the corners (where the rubber is heaviest) worn-off pellets lie in a dark speckle;
    // single pellets close up (HQ), their mean darkening further away (and on Low)
    float ae = abs(lat);
    half marbZ = (half)(saturate((i.misc.y - 0.62) * 3.0) * saturate(abs(i.misc.x) - 2.2) * saturate((_HalfWidth - 0.3 - ae) * 2.0)) * (1.0 - kerbF);
    float2 gc = float2(lat, along - floor(along * (1.0 / 512.0)) * 512.0) * 16.0; // 6.25 cm cells
    float gw = max(wl, wa) * 16.0;                                               // cells per pixel
    half grainVis = (half)saturate(1.6 - gw * 1.2);                              // single grains only while > ~1 px
#ifdef ROAD_HQ
    half marb = Grains(gc, 0.3 * marbZ, 3.0, gw);
    col *= 1.0 - marbZ * (0.07 + 0.45 * lerp(0.04, marb, grainVis)) * (1.0 - 0.5 * inPatch);
#else
    col *= 1.0 - marbZ * 0.09;
#endif

    // ---- tar: the joints, the patch outline and a wandering crack-sealing "snake" (HQ); dark and glossy
    half tar = joint;
#ifdef ROAD_HQ
    float snakeLat = (h4 * 2.0 - 1.0) * (_HalfWidth - 1.5) + 0.35 * sin(sa * 0.21 + h1 * 6.28) + 0.12 * sin(sa * 0.83 + h2 * 6.28);
    half snakeOn = (half)saturate(sin(sa * 0.07 + h2 * 6.28) * 4.0 + 1.0) * (h3 > 0.45 ? 1.0 : 0.0); // in broken lengths
    float snakeX = lat - snakeLat;
    half snake = Band(snakeX, -0.025, 0.025, max(fwidth(snakeX), 1e-4)) * Band(sa, 4.0, SecLen - 4.0, wa) * snakeOn;
    tar = max(tar, snake);
    // longitudinal joint between two paver lanes (some sections): a sealed, slightly wavy line about a third of the way
    // in from one edge
    float pj = lat - (h3 > 0.5 ? 1.0 : -1.0) * _HalfWidth * 0.36 - 0.04 * sin(along * 0.11 + h1 * 6.28);
    tar = max(tar, Band(pj, -0.03, 0.03, wl) * (h2 < 0.55 ? 1.0 : 0.0) * Band(sa, 2.0, SecLen - 2.0, wa));
    // sealed transverse cracks: in one ~9 m stretch out of four, a wiggly tar line across part of the road
    float cc = floor(along * (1.0 / 9.0)), hc = Hash1(cc * 1.37 + 5.1), hd = Hash1(cc * 2.91 + 0.7);
    float cAt = (cc + 0.15 + 0.7 * hd) * 9.0;
    float cx = along - cAt - 0.22 * sin(lat * 1.3 + hc * 40.0) - 0.05 * sin(lat * 5.3 + hd * 20.0);
    float c0 = (hc * 2.0 - 1.0) * _HalfWidth, c1 = c0 + 2.0 + 5.0 * hd;
    tar = max(tar, Band(cx, -0.018, 0.018, wa + 0.35 * wl) * Band(lat, c0, c1, wl) * (hc > 0.74 ? 1.0 : 0.0));
#endif
    // timing loops: a pair of sealed saw cuts 0.6 m apart across the road, evenly round the lap and at the sector ends
    float lp = max(_Loops.x, 1.0);
    float la = min(abs(along - floor(along / lp + 0.5) * lp), min(abs(along - _Loops.y), abs(along - _Loops.z)));
    tar = max(tar, Band(abs(la - 0.3), -1.0, 0.02, wa) * Band(ae, -1.0, _HalfWidth - 0.2, wl) * 0.8);
    tar *= 1.0 - kerbF;
    col *= 1.0 - 0.42 * tar;

    // ---- painted lines: white edge lines 0.15..0.40 m in from each edge
    half paint = Band(abs(lat), _HalfWidth - 0.40, _HalfWidth - 0.15, wl);
    // start/finish line just past track point 0
    paint = max(paint, Band(along, 0.1, 1.0, wa));
    // grid slots (one every _Grid.y metres before the line, alternating left/right): a 2 m bar across the slot
    // with short side marks reaching back towards the car
    float gk = floor((_Grid.x - along) / _Grid.y + 0.5);
    UNITY_BRANCH
    if (gk >= 0.0 && gk < _Grid.z)
    {
        float a = along - (_Grid.x - gk * _Grid.y);           // metres past this slot's bar
        float side = (1.0 - 4.0 * frac(gk * 0.5)) * _Grid.w;  // even slots left, odd right
        float l = lat - side;
        half bar = Band(a, -0.15, 0.15, wa) * Band(l, -1.0, 1.0, wl);
        half arms = Band(a, -1.2, 0.15, wa) * (Band(l, 0.85, 1.0, wl) + Band(l, -1.0, -0.85, wl));
        paint = max(paint, max(bar, arms));
    }
    paint *= 1.0 - kerbF;
    // paint on rough asphalt: the grain shows through a little, and tyres wear it where the line crosses it
    half3 paintCol = _Paint.rgb * (0.86 + 0.18 * grain) * (1.0 - 0.3 * rub);
#ifdef ROAD_HQ
    paintCol *= 1.0 + 0.08 * (fn.r - 0.5) * fineFade;          // stone tops through the paint
#endif
    col = lerp(col, paintCol, paint);

    // ---- kerbs: 1.5 m red / white blocks, crisp, with tyre-blackened inner edge and a darker outer lip
    half across = (half)i.misc.x;
    float tri = abs(frac(along * (1.0 / 3.0)) - 0.5) * 2.0;          // 0..1 triangle wave, period 3 m
    half red = (half)saturate((tri - 0.5) / (wa * (2.0 / 3.0)) + 0.5); // d(tri)/d(along) = 2/3
    half3 kc = lerp(_Paint.rgb, _KerbRed.rgb, red) * (0.86 + 0.2 * grain);
    kc *= 1.0 - 0.35 * saturate(1.0 - across * 3.0) * blotch;        // rubber where wheels ride the kerb
    kc *= 1.0 - 0.25 * smoothstep(0.82, 1.0, across);                 // outer lip
    // raised look: a shadowed step against the asphalt, a lit rounded nose just above it, and a dark joint
    // between the blocks (all box-filtered, ~2-4 cm wide)
    kc *= 1.0 - 0.45 * Band(across, -1.0, 0.018, wk);
    kc *= 1.0 + 0.12 * Band(across, 0.025, 0.09, wk);
    float jb = abs(tri - 0.5) * 1.5;                                  // metres to the nearest colour change
    kc *= 1.0 - 0.35 * Band(jb, -1.0, 0.012, wa);
    // outer lip: the last ~1.5 pixels fade to dark dirt, as the road edge does (softer polygon edge against the verge)
    half lipW = (half)min(1.5 * wk, 0.5);
    kc = lerp(kc, _Dust.rgb * 0.4, 0.7 * (half)saturate((across - (1.0 - lipW)) / max(lipW, 1e-4)));
#ifdef ROAD_HQ
    kc *= 1.0 + 0.08 * (fn.r - 0.5) * fineFade;                       // grit in the paint
    // worn paint: scuffed patches where the grey base shows, most on the inner half where the wheels ride, and road
    // dirt thrown onto the outer part
    half kwear = saturate((d.g * 0.7 + d.r * 0.5 - 0.68 + 0.22 * saturate(1.0 - across * 1.6)) * 5.0);
    kc = lerp(kc, _Asphalt.rgb * (0.9 + 0.3 * grain), kwear * 0.55);
    kc = lerp(kc, _Dust.rgb * 0.75, saturate(across * 1.4 - 0.5) * 0.18 * (0.5 + blotch));
#endif
    col = kerb ? kc : col;

    // ---- edge dirt (uv3, the side this pixel is on): a dusty film reaching 0.5-1.6 m in from the edge (wandering),
    // over the kerb too, and gravel stones thrown onto it next to the traps and where cars drag it back on
    float sideL = lat > 0.0 ? 1.0 : 0.0;
    half ed = (half)lerp(i.dirt.y, i.dirt.x, sideL);
    float reach = 0.45 + 1.15 * ed + 0.3 * sin(along * 0.23 + sideL * 3.0) + 0.15 * sin(along * 0.71 + sideL);
    half eb = (half)saturate((ae - (_HalfWidth - reach)) / max(reach * 0.7, 0.2));
    eb *= eb;
    half3 soil = _Dust.rgb * half3(0.98, 0.9, 0.78) * (0.8 + 0.3 * grain);
    col = lerp(col, soil, eb * ed * (0.5 - 0.15 * kerbF) * (0.55 + 0.45 * blotch) * (1.0 - 0.6 * rub));
    half stoneP = saturate(eb * (ed - 0.25) * 0.75);
#ifdef ROAD_HQ
    half st = Grains(gc + 0.5, stoneP, 7.0, gw);
    half3 stoneC = half3(0.66, 0.62, 0.55) * (0.8 + 0.35 * (half)frac(dot(floor(gc + 0.5), float2(0.37, 0.61))));
    col = lerp(col, stoneC, lerp(stoneP * 0.12, st, grainVis));
#else
    col = lerp(col, half3(0.62, 0.58, 0.52), stoneP * 0.12);
#endif

    // ---- wet: damp already looks well wet off the line; in the damp the racing line has dried (none of that when wet)
    half wv = saturate(_Wet * 1.6);
    half dryLine = kerb ? 0.0 : broad * saturate(2.0 - 2.0 * _Wet);
    half wet = wv * (1.0 - 0.85 * dryLine);
#ifdef ROAD_HQ
    // standing water (wet only): in the low, resurfaced patches and dips of the macro layer, off the line and
    // towards the edges where the camber drains to
    half low = saturate((blotch + 0.6 * patch - 0.58) * 5.0) + edge * 0.5;
    half pool = kerb ? 0.0 : saturate(low * saturate(abs(x) * 0.8 - 1.0) * saturate(_Wet * 2.0 - 1.0));
#else
    half pool = 0.0;
#endif
    // wet asphalt is about half as bright; paint and kerbs (smooth, light) darken less; water darkens more still
    half darken = kerb ? 0.28 : 0.45 - 0.23 * paint;
    col *= (1.0 - wet * darken) * (1.0 - 0.3 * pool);

    col *= light;
    // the sky over the road is seen in the wet surface: as bright as the open sky, less in tunnels and deep shade
    half skyVis = saturate(dot(light, half3(0.3, 0.59, 0.11)) * 1.4);

#ifdef ROAD_HQ
    // ---- sheen: low sun glints off the asphalt (polished rubber and paint more), the sky at grazing angles
    float3 V = normalize(i.view);
    half3 N = normalize(i.n);
    // the stones' own slopes (close up): single stones catch the sun; paint, kerbs and tar fill the texture in
    half bump = 0.45 * fineFade * (1.0 - 0.6 * paint - 0.6 * kerbF) * (1.0 - tar) * saturate(1.5 - fineTexels * 0.125);
    half3 Nb = normalize(N + half3(fn.b - 0.5, 0.0, fn.a - 0.5) * bump);
    half3 R = reflect(-_SunDir.xyz, Nb);
    half s = saturate(dot(V, R));
    // rough asphalt: a broad forward-scatter lobe (the road brightens looking towards the sun even when it is high)
    // plus a tight glint; s^32 by repeated squaring
    half lobe = saturate(s * 1.6 - 0.6); lobe *= lobe;
    half s2 = s * s, s4 = s2 * s2, s8 = s4 * s4, s32 = s8 * s8; s32 *= s32;
    half s128 = s32 * s32; s128 *= s128;
    half gloss = 0.35 + 0.55 * rub + 0.4 * paint + 0.5 * kerbF + 0.22 * d.a * fade + 0.35 * tar // stones sparkle near
               + 0.2 * (fn.g - 0.5) * fineFade;
    // polished stone tops (G above 0.5) give tiny, sharp glints close to the car (gone past ~4 texels per pixel)
    half spark = 0.5 * saturate(fn.g * 2.0 - 1.0) * fineFade * (1.0 - kerbF) * (1.0 - paint) * saturate(2.0 - fineTexels * 0.25);
    // (dry sheen held down: lobe 0.55 and sparks 1.6 bleached the asphalt near the camera to white; RR3's sunlit
    // asphalt is a neutral mid grey, about 150-160 sRGB)
    half3 spec = _SunCol.rgb * ((lobe * 0.24 + s32 * 0.5) * gloss + s128 * spark * 0.7) * sunVis;
    half f = 1.0 - saturate(dot(V, N)); half f2 = f * f; half fres = f2 * f2 * f;
    // wet: a water film mirrors the sky (Schlick, F0 = 0.02 for water) and the sun in a sharp, bright glint
    // (s^128), dimmed by the overcast; rubber and puddles are the smoothest, so they shine most
    half wetG = saturate(wet * (0.75 + 0.25 * paint + 0.3 * rub + 0.3 * tar) + pool);
    spec += _SunCol.rgb * ((s32 * 0.8 + s128 * 3.0) * wetG * (1.0 - 0.7 * _Wet) * (0.3 + 0.7 * sunVis));
    col = lerp(col, unity_FogColor.rgb * skyVis, saturate((0.02 + 0.98 * fres) * wetG * 0.95));
    col += spec + unity_FogColor.rgb * (fres * 0.13 * (0.6 + 0.4 * gloss) * (1.0 - wetG));
#else
    // Low: the same sky mirror, with the Fresnel term from the vertex shader; no glint
    col = lerp(col, unity_FogColor.rgb * skyVis, saturate((0.02 + 0.98 * i.fres) * wet * 0.85));
#endif

    fixed4 c = fixed4(col, 1);
    UNITY_APPLY_FOG(i.fogCoord, c);
    c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
    return c;
}
#endif
