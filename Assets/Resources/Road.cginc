// Road.cginc — shared code of Road.shader (both levels). Define ROAD_HQ before including for the Medium/High path.
// Weather: the global _Wet (Weather.cs: 0 dry, 0.5 damp, 1 wet; set with Shader.SetGlobalFloat, not a material
// property) darkens the surface and mirrors the sky in it with a water Fresnel term. Damp: the racing line has dried.
// Wet: standing water in the low, off-line patches (HQ only). At _Wet = 0 every wet term is zero.
//
// The look is Real Racing 3's: clean, calm, dark-grey asphalt. At 300 km/h and ~35 fps the road moves ~2.5 m per
// frame, so any contrast between ~5 cm and ~5 m wide strobes and "crawls" under the car. So:
//   - tonal variation is long and soft: _F2RoadMacro (EnvTex.RoadMacro, global) read stretched 48 m across x 192 m
//     along, plus ~113 m paving sections a shade apart (blended over 12 m) and soft resurfaced patches;
//   - the rubbered racing line, its streaks and the tyre marks all run along the track (still under forward motion),
//     with soft edges; darker where the line carries more rubber (corners and the braking zones into them);
//   - fine grain (HQ only) comes from _F2RoadDetail (EnvTex.RoadDetail, global, 1.6 m per tile, R8), whose hand-made
//     mips fade it to flat grey once a pixel spans ~5 cm; it is also faded with distance and with the camera's speed
//     (_F2RoadMotion, RoadSurface.cs), as motion blur would. No per-pixel normals, no glints: the sun sheen is a broad,
//     smooth lobe off the geometric normal, so nothing sparkles.
//   - crisp paint and kerbs: box-filtered maths in metres (fwidth), sharp near, never aliased far away.
// Sharp sun shadows (RoadShadow.cs, both levels, one extra read): when the mask is built the vertex colour holds the
// sky/ambient light only and its alpha the bake's sun visibility; the sun comes back as _F2SunLin x N.L x alpha x mask.
// The mask (_F2RoadShadow, R8) is in track space: U across (_F2RoadShadowP.w either side of the centreline), V along,
// the lap cut into strips laid side by side; read with gradients of the continuous coordinates (no seam at a strip
// change). Without a mask _F2SunLin = 0 and the vertex colour is the whole baked light, as before.
// (float-only rule: no step() or compares on half values; FXC miscompiles them for GLES3 / Vulkan.)
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
    float4 uv : TEXCOORD0;    // lateral m, along m, macro uv (48 m across x 192 m along)
    float4 misc : TEXCOORD1;  // x: lateral distance to the racing line (m) or kerb across, y: rubber amount (or -1 = kerb), z: grain fade (distance x speed), w: bake's sun visibility (vertex alpha)
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

sampler2D _F2RoadMacro;  // global (RoadSurface.cs): R tone drift, G mottle, B resurfaced patches
#ifdef ROAD_HQ
sampler2D _F2RoadDetail; // global (RoadSurface.cs): R fine grain, 0.5 = neutral, fades out down the mips
#endif
fixed4 _Asphalt, _Dust, _Paint, _KerbRed, _KerbYellow, _SunCol;
sampler2D _F2RoadShadow; // global (RoadShadow.cs)
float4 _F2RoadShadowP;   // x: strips per metre along, y: 1 / strips, z: 1 / width covered (m), w: half width (m)
half4 _F2SunLin;         // the sun's linear colour x intensity, as baked (0: no mask, the vertex colour is all the light)
float _F2RoadMotion;     // global (RoadSurface.cs): 0 at walking pace .. 1 above ~200 km/h (camera speed)
float _HalfWidth;
float4 _Grid, _SunDir;
float4 _Loops;           // x: timing-loop pitch (m), y / z: the sector ends (m along)
half _Wet; // global (Weather.cs)

v2f vert (appdata v)
{
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex);
    float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
    // macro layer stretched along the track: every tone change is a long drift, never a blotch flicking past
    o.uv = float4(v.uv.x, v.uv.y, v.uv.x * (1.0 / 48.0), v.uv.y * (1.0 / 192.0));
    bool kerb = v.uv2.y < -0.5;
    o.misc.x = kerb ? v.uv2.x : v.uv.x - v.uv2.x;
    o.misc.y = v.uv2.y;
    float3 toCam = _WorldSpaceCameraPos - wp;
    float dist = length(toCam);
    // fine grain: full up to 8 m, gone by 40 m (the texture's mips have flattened it by then anyway); at speed it is
    // held down to a quarter, as the eye (or motion blur) would smear it
    o.misc.z = saturate((70.0 - dist) / 55.0) * (1.0 - 0.45 * saturate(_F2RoadMotion));
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

#ifdef ROAD_HQ
// one car's pair of tyre marks (wheels ~1.6 m apart): x = metres from the racing line; the pair has its own offset
// (within +-spread/2) per stretch of len metres, wanders a little, and fades in and out slowly along the stretch (zero
// at its ends, so the offset can jump there); some stretches have none. Soft-edged (at least ~6 cm of blur) and
// running along the track, so they stay still under the car at any speed.
half TyrePair(float x, float along, float len, float seed, float spread, float w)
{
    float c = floor(along / len), f = along / len - c;
    float h = Hash1(c * 7.13 + seed * 11.0), h2 = Hash1(c * 3.71 + seed * 5.3);
    float on = saturate(sin(f * 3.14159) * 1.6 - 0.25) * (h2 > 0.3 ? 1.0 : 0.0);
    float o = (h - 0.5) * spread + 0.15 * sin(along * 0.047 + seed * 4.0);
    float xa = abs(x - o), mw = 0.16 + 0.12 * h2;
    return Band(xa, 0.8 - mw * 0.5, 0.8 + mw * 0.5, w + 0.06) * (half)(on * (0.55 + 0.45 * h2));
}
#endif

fixed4 frag (v2f i) : SV_Target
{
    float lat = i.uv.x, along = i.uv.y;
    // pixel footprints (outside any branch): metres across / along the road, kerb "across" units
    float wl = max(fwidth(lat), 1e-4), wa = max(fwidth(along), 1e-4), wk = max(fwidth(i.misc.x), 1e-4);
    bool kerb = i.misc.y < -0.5;
    half kerbF = kerb ? 1.0 : 0.0;

    // ---- asphalt textures: the long macro drifts (both levels), the fine grain (HQ)
    half4 mac = tex2D(_F2RoadMacro, i.uv.zw);
    half drift = mac.r, blotch = mac.g, patch = mac.b;
#ifdef ROAD_HQ
    // fine aggregate, 1.6 m per tile, turned ~23 degrees against the road so no texel row lines up with it; signed,
    // mean 0, std ~0.15 at mip 0 and flat beyond ~5 cm per pixel (EnvTex.RoadDetail)
    float2 fu = i.uv.xy * (1.0 / 1.6);
    half grain = (tex2D(_F2RoadDetail, float2(fu.x * 0.92 - fu.y * 0.39, fu.x * 0.39 + fu.y * 0.92)).r - 0.502) * (half)i.misc.z;
    // coarse clusters (same texture, 7 m per tile, other angle): metre-wide lighter/darker aggregate and binder
    // areas that keep the surface reading as asphalt out to ~150 m; its mips flatten it smoothly, nothing strobes
    float2 fc = i.uv.xy * (1.0 / 7.0);
    half clus = (tex2D(_F2RoadDetail, float2(fc.x * 0.77 + fc.y * 0.64, -fc.x * 0.64 + fc.y * 0.77)).r - 0.502);
#else
    half grain = 0.0;
    half clus = 0.0;
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
    half3 light = i.color + i.sunc * (i.misc.w * shade);
    // ~0.9 in shade, ~1.75 in the sun: how much sun reaches this point (sheen, wet glints)
    half sunVis = saturate((dot(light, half3(0.3, 0.59, 0.11)) - 1.0) * 1.5);

    // ---- paving sections: ~113 m each, a shade apart, blended into the next over its last 12 m (no seam line)
    const float SecLen = 113.0;
    float secF = along / SecLen;
    float sec = floor(secF);
    float sa = along - sec * SecLen;                             // metres into this section
    float h1 = Hash1(sec + 0.5), h2 = Hash1(sec + 17.3), h3 = Hash1(sec + 41.7), h4 = Hash1(sec + 73.1);
    float hn = Hash1(sec + 1.5);                                 // the next section's shade
    float secShade = lerp(h1, hn, smoothstep(SecLen - 12.0, SecLen, sa));
    // resurfaced patch (about half the sections): a long, soft-edged stretch of fresher, darker asphalt, often a lane wide
    float pa0 = 8.0 + h3 * 40.0, pa1 = pa0 + 20.0 + 40.0 * h4;
    float pc = (h1 * 2.0 - 1.0) * max(_HalfWidth - 2.5, 0.0), phw = 1.4 + 1.8 * Hash1(sec + 93.7);
    float hasPatchF = h2 > 0.5 ? 1.0 : 0.0;
    float patchA = smoothstep(pa0, pa0 + 4.0, sa) * (1.0 - smoothstep(pa1 - 4.0, pa1, sa));
    float patchL = smoothstep(pc - phw, pc - phw + 1.0, lat) * (1.0 - smoothstep(pc + phw - 1.0, pc + phw, lat));
    half inPatch = (half)(patchA * patchL * hasPatchF);

    // ---- asphalt tone: long drifts (macro R), slow mottle (G), darker resurfaced areas (B and the section patches),
    // section shades, and the fine grain near the car
    half tone = (0.93 + 0.14 * drift) * (0.97 + 0.06 * blotch) * (1.0 - 0.08 * patch);
    tone *= 1.0 + 0.06 * (half)(secShade - 0.5);
    tone *= 1.0 - 0.05 * inPatch;
    tone *= 1.0 + 0.6 * grain;                                   // stones and binder, +-9 % close up, fading by 70 m
    tone *= 1.0 + 0.45 * clus;                                   // metre-scale clusters, +-6 %, to ~150 m
    half3 col = _Asphalt.rgb * tone;

    // ---- rubber laid along the racing line: a broad soft band plus the two tyre tracks; darker the more rubber the
    // line carries (corners and the braking into them), lighter on the straights
    half x = (half)i.misc.x;
    half rubAmt = (half)saturate(i.misc.y);
    half broad = saturate(1.0 - x * x * 0.3);                  // ~1.8 m half width
    half tracks = saturate(1.0 - abs(abs(x) - 0.8) * 2.0);     // wheels ~1.6 m apart
    half rub = kerb ? 0.0 : rubAmt * rubAmt * broad * (0.6 + 0.4 * tracks) * (0.75 + 0.5 * blotch);
#ifdef ROAD_HQ
    // soft streaks along the direction of travel (each tyre lays its own); faded once a pixel spans ~17 cm across
    float xs = i.misc.x;
    half streak = (half)(0.5 + 0.5 * sin(xs * 21.0 + sin(xs * 6.3 + along * 0.013) * 1.7));
    rub *= lerp(1.0, 0.7 + 0.6 * streak, (half)saturate(1.0 - wl * 5.0));
#endif
    col *= 1.0 - 0.58 * rub; // rubbered, not black: in the sun the line reads as a clearly darker grey
#ifdef ROAD_HQ
    // ---- tyre marks: single cars' pairs of soft dark lines along and around the racing line, more where the line
    // carries more rubber
    half marks = TyrePair(xs, along, 53.0, 1.0, 1.3, wl) + TyrePair(xs, along, 79.0, 2.0, 2.6, wl);
    col *= 1.0 - (half)saturate(marks) * (0.08 + 0.2 * rubAmt) * (0.7 + 0.6 * blotch) * (1.0 - kerbF);
    // braking zones (heavy rubber): short, dark lock-up streak pairs from flat-spotting fronts, along the line
    float bz = saturate(((float)rubAmt - 0.7) * 4.0);
    half lock = TyrePair(xs, along, 23.0, 3.0, 0.9, wl) * (half)bz;
    col *= 1.0 - (half)saturate(lock) * 0.3 * (1.0 - kerbF);
#endif

    // ---- dust off-line: lighter towards the edges where nobody drives (less on fresh patches)
    half edge = smoothstep(_HalfWidth - 3.2, _HalfWidth - 0.3, abs(lat));
    // (a light veil only: at 0.45 the outer thirds of the road went chalk white in the sun)
    col = lerp(col, _Dust.rgb * (1.0 + 0.3 * grain), edge * 0.2 * (1.0 - rub) * (1.0 - 0.3 * inPatch));

    // ---- the outer edge of the mesh: the last ~1.5 pixels fade to a dark dirt tone, so the polygon edge against the
    // verge or the kerb has far less contrast to step (no hard, jagged line in bends without MSAA)
    half rimW = (half)min(1.5 * wl, 2.0); // (clamped: no half overflow at the horizon)
    col = lerp(col, _Dust.rgb * 0.45, 0.6 * (half)saturate((abs(lat) - (_HalfWidth - rimW)) / max(rimW, 1e-4)));
    // grime against the kerb / verge: the strip outside the white line is never swept by tyres (oil, rubber dust)
    col *= 1.0 - 0.14 * Band(abs(lat), _HalfWidth - 0.16, _HalfWidth + 1.0, max(wl, 0.04)) * (1.0 - kerbF);

    // ---- marbles: off the line in the corners (where the rubber is heaviest) worn-off pellets darken the surface a
    // little (their mean only: single pellets boiled under the car at speed)
    float ae = abs(lat);
    half marbZ = (half)(saturate((i.misc.y - 0.62) * 3.0) * saturate(abs(i.misc.x) - 2.2) * saturate((_HalfWidth - 0.3 - ae) * 2.0)) * (1.0 - kerbF);
    col *= 1.0 - marbZ * 0.09 * (1.0 - 0.5 * inPatch);

    // ---- timing loops: a pair of sealed saw cuts 0.6 m apart across the road, evenly round the lap and at the sector
    // ends; faint (a dark line every few metres across the road is what made it flicker at speed)
    float lp = max(_Loops.x, 1.0);
    float la = min(abs(along - floor(along / lp + 0.5) * lp), min(abs(along - _Loops.y), abs(along - _Loops.z)));
    half tar = Band(abs(la - 0.3), -1.0, 0.025, wa) * Band(ae, -1.0, _HalfWidth - 0.2, wl) * 0.6 * (1.0 - kerbF);
    col *= 1.0 - 0.3 * tar;

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
    // paint on asphalt: a hint of the grain, and tyres wear it where the line crosses it
    half3 paintCol = _Paint.rgb * (0.95 + 0.2 * grain) * (1.0 - 0.3 * rub);
    col = lerp(col, paintCol, paint);

    // ---- kerbs (uv2.y = -(1 + type + 0.45 x wear): type 0 kerb, 1 stepped kerb, 2 sausage; uv3 = this side's dirt
    // and, in the other component, the saw-tooth ridge amount). Branched: the asphalt pixels pay nothing for this.
    UNITY_BRANCH
    if (i.misc.y < -0.5)
    {
        float kcode = -i.misc.y - 1.0;
        float kt = floor(kcode + 0.02);
        float kwear = saturate((kcode - kt) * (1.0 / 0.45));
        float isStep = (kt > 0.5 && kt < 1.5) ? 1.0 : 0.0;
        float isSaus = kt > 1.5 ? 1.0 : 0.0;
        float ac = i.misc.x;                                          // 0 at the track side .. 1 at the outer lip
        float kRidge = lat > 0.0 ? i.dirt.y : i.dirt.x;
        float kDirt = lat > 0.0 ? i.dirt.x : i.dirt.y;
        float wkm = wk * lerp(1.4, 0.5, isSaus);                      // pixel across, metres
        // fine detail (chips, specks, single serrations) only while a pixel is under ~2.5 cm, and near the camera and
        // slow (the road grain's distance x speed fade): never a single-pixel contrast
        float fine = saturate(1.6 - max(wa, wkm) * 45.0) * i.misc.z;
        // blocks: 1.5 m red / white (stepped kerb: 0.75 m on its raised outer tier); a sausage is yellow throughout
        float tierO = isStep * (ac > 0.445 ? 1.0 : 0.0);
        float per = lerp(3.0, 1.5, tierO);
        float tri = abs(frac(along / per) - 0.5) * 2.0;              // 0..1 triangle wave
        half red = (half)saturate((tri - 0.5) / (wa * (2.0 / per)) + 0.5);
        float jb = abs(tri - 0.5) * per * 0.5;                        // metres to the nearest colour change
        half3 kc = lerp(_Paint.rgb, _KerbRed.rgb, red);
        kc = lerp(kc, _KerbYellow.rgb, (half)isSaus);
        kc *= 0.96 + 0.2 * grain;
        // raised look: a shadowed step against the asphalt, a lit rounded nose just above it, a dark joint between the
        // blocks (all box-filtered, ~2-4 cm wide); stepped: shade at the foot of the step and a lit edge on top of it
        half notS = (half)(1.0 - isSaus);
        kc *= 1.0 - 0.45 * Band(ac, -1.0, 0.018, wk) * notS;
        kc *= 1.0 + 0.12 * Band(ac, 0.025, 0.09, wk) * notS;
        kc *= 1.0 - 0.3 * Band(jb, -1.0, 0.012, wa) * notS;
        kc *= 1.0 - 0.32 * Band(ac, 0.40, 0.445, wk) * (half)isStep;
        kc *= 1.0 + 0.14 * Band(ac, 0.47, 0.53, wk) * (half)isStep;
        // sausage: a lit crown, dark feet, serrated notches across its top every 0.25 m (box-filtered, fading to their
        // mean once a pixel spans ~1/4 of a tooth) and a joint between the 2 m pieces
        float sn = abs(frac(along * 4.0) - 0.5);                      // 0..0.5, a notch at 0.5
        half notch = Band(sn, 0.36, 1.0, wa * 4.0) * Band(ac, 0.12, 0.88, wk);
        notch = lerp(0.14 * Band(ac, 0.12, 0.88, wk), notch, (half)saturate(1.5 - wa * 6.0));
        half sj = Band(abs(frac(along * 0.5) - 0.5), 0.485, 1.0, wa * 0.5);
        kc *= 1.0 + (half)isSaus * (0.16 * Band(ac, 0.36, 0.64, wk) - 0.4 * (Band(ac, -1.0, 0.07, wk) + Band(ac, 0.93, 2.0, wk)) - 0.5 * notch - 0.5 * sj);
        // saw-tooth rumble ridges (outside of corner exits), 0.3 m pitch on the outer part: each ridge's long face
        // brightens towards its crest (it faces the cars coming), then a short dark drop; zero-mean shading, faded to
        // flat once a pixel spans ~1/5 of a ridge, so far away it is just the kerb's colour
        float rs = frac(along * (1.0 / 0.3));
        float rd = abs(rs - 0.5);                                     // the drop at the crest (rs -> 1 / 0)
        half drop = Band(rd, 0.41, 1.0, wa * (1.0 / 0.3));
        half ridgeM = (half)(kRidge * saturate((ac - 0.3) * 8.0) * (1.0 - isSaus) * (isStep > 0.5 ? tierO : 1.0) * saturate(1.6 - wa * 16.0));
        kc *= 1.0 + ridgeM * (0.14 * (half)(rs - 0.5) - 0.38 * (drop - 0.18));
#ifdef ROAD_HQ
        // rubber smeared on where tyres ride the kerb: heaviest on the inner half, in soft streaks along the track
        // (still under the car at speed), more at apexes and exits (wear)
        half wsk = (half)(0.5 + 0.5 * sin(ac * 17.0 + sin(along * 0.11 + ac * 3.0) * 1.3));
        half innerH = (half)saturate(1.0 - ac * 1.6);
        half smear = (half)kwear * (0.3 + 0.7 * innerH * innerH) * lerp(0.75, 0.45 + 0.8 * wsk, (half)saturate(1.0 - wkm * 6.0)) * (0.6 + 0.8 * blotch);
        kc *= 1.0 - 0.45 * saturate(smear) * (1.0 - 0.4 * (half)isSaus);
        // paint chipped off at the block edges (grey base showing), a ragged edge in ~6 cm cells across; near only
        float cell = floor(ac * 23.0);
        float blk = floor(along * 2.0 / per);
        float chipW = 0.012 + 0.08 * Hash1(cell * 7.31 + blk * 13.17) * Hash1(cell * 3.1 + blk * 5.7 + 2.0);
        half chip = Band(jb, -1.0, chipW, wa) * (half)(fine * (0.45 + 0.55 * kwear) * (1.0 - isSaus));
        kc = lerp(kc, half3(0.40, 0.39, 0.37) * (0.9 + 0.5 * grain), chip * 0.8);
        // dirt and gravel dust on the outer part: a soft film (more beside the traps and on exits), grit specks near
        half outerD = (half)saturate(ac * 2.6 - 1.45 + isSaus * 0.5);
        kc = lerp(kc, _Dust.rgb * (0.72 + 0.5 * grain), saturate(outerD * (0.15 + 0.55 * (half)kDirt) * (0.5 + blotch)) * 0.55);
        half spk = saturate((grain - 0.1) * 7.0) * (half)fine * outerD * (half)saturate(kDirt * 2.0 - 0.3);
        kc = lerp(kc, half3(0.60, 0.56, 0.50), spk * 0.55);
#else
        kc *= 1.0 - 0.3 * saturate(1.0 - (half)ac * 3.0) * blotch * (half)(0.4 + 0.6 * kwear); // rubber, its mean
        kc = lerp(kc, _Dust.rgb * 0.75, (half)saturate(ac * 2.6 - 1.45) * 0.2 * (0.5 + (half)kDirt));
#endif
        // outer lip (and a sausage's inner foot): the last ~1.5 pixels fade to dark dirt, as the road edge does
        // (softer polygon edge against the verge)
        half lipW = (half)min(1.5 * wk, 0.5);
        kc = lerp(kc, _Dust.rgb * 0.4, 0.7 * (half)saturate((ac - (1.0 - lipW)) / max(lipW, 1e-4)));
        kc = lerp(kc, _Dust.rgb * 0.4, 0.6 * (half)isSaus * (half)saturate((lipW - ac) / max(lipW, 1e-4)));
        col = kc;
    }

    // ---- edge dirt (uv3, the side this pixel is on): a dusty film reaching 0.5-1.6 m in from the edge (wandering),
    // over the kerb too, with a fine scatter of gravel thrown onto it next to the traps (its mean tone only)
    float sideL = lat > 0.0 ? 1.0 : 0.0;
    half ed = (half)lerp(i.dirt.y, i.dirt.x, sideL);
    float reach = 0.45 + 1.15 * ed + 0.3 * sin(along * 0.023 + sideL * 3.0) + 0.15 * sin(along * 0.071 + sideL);
    half eb = (half)saturate((ae - (_HalfWidth - reach)) / max(reach * 0.7, 0.2));
    eb *= eb;
    half3 soil = _Dust.rgb * half3(0.98, 0.9, 0.78) * (0.95 + 0.3 * grain);
    col = lerp(col, soil, eb * ed * (0.5 - 0.15 * kerbF) * (0.55 + 0.45 * blotch) * (1.0 - 0.6 * rub));
    half stoneP = saturate(eb * (ed - 0.25) * 0.75);
    col = lerp(col, half3(0.62, 0.58, 0.52), stoneP * 0.12);

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
    // ---- sheen: a soft, broad forward-scatter lobe off the geometric normal (the road brightens looking towards the
    // sun), smoother on rubber, paint and kerbs; the sky at grazing angles. No per-pixel normal, no glints.
    float3 V = normalize(i.view);
    half3 N = normalize(i.n);
    half3 R = reflect(-(half3)_SunDir.xyz, N);
    half s = saturate(dot((half3)V, R));
    half lobe = saturate(s * 1.6 - 0.6); lobe *= lobe;
    half s2 = s * s, s4 = s2 * s2, s8 = s4 * s4, s16 = s8 * s8, s32 = s16 * s16;
    half gloss = 0.35 + 0.55 * rub + 0.4 * paint + 0.12 * kerbF + 0.35 * tar; // (kerbs 0.5 -> 0.12: the white sheen turned the red blocks pink)
    // (dry sheen held down: RR3's sunlit asphalt is a neutral mid grey, about 150-160 sRGB)
    half3 spec = _SunCol.rgb * ((lobe * 0.24 + s16 * 0.28) * gloss * sunVis);
    half f = 1.0 - saturate(dot((half3)V, N)); half f2 = f * f; half fres = f2 * f2 * f;
    // wet: a water film mirrors the sky (Schlick, F0 = 0.02 for water) and the sun in a bright streak (s^128 off the
    // smooth geometric normal), dimmed by the overcast; rubber and puddles are the smoothest, so they shine most
    half wetG = saturate(wet * (0.75 + 0.25 * paint + 0.3 * rub + 0.3 * tar) + pool);
    half s128 = s32 * s32; s128 *= s128;
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
