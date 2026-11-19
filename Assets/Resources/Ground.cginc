// Ground.cginc — shared code of Ground.shader (grass, terrain, verges, gravel traps). Lighting is baked into the vertex
// colours (LightBake.cs) like Baked.shader. Two texture reads per pixel:
//   _MainTex  detail (photo grass / gravel, CC0, see Textures/CREDITS.txt), world space, a few metres per tile; Medium/High read it a second time at ~3.4x the scale, turned, and average the two
//   _MacroTex linear data at ~100 m per tile, turned 37 degrees: R brightness drift, G dry patches, B clumps
// Mesh data: uv0 = world (x, z) in metres; uv2 = (distance from the track centreline m, distance along the lap m) on
// verges and gravel traps, (0, 0) on the open land, where the track-relative terms below are all zero.
// Third read (verges, gravel, run-offs only): the sun-shadow mask _F2RoadShadow (RoadShadow.cs), see the end of frag.
// Track-relative terms (pure maths, no reads): mowing stripes along the track, worn darker grass at the barrier,
// tyre ruts in the gravel near its inner edge. Define GROUND_HQ for Medium/High (ruts; Low keeps stripes).
// Gravel (_Ruts.y > 0): damp darker stones along the inner edge and at the barrier (both levels); HQ: raked ridges along
// the trap, single stones picked out near the camera, ruts along it and diagonal ones where cars went off.
// Run-off strip (_Turf = 1, the 2 m strip behind the kerbs; uv3 = (strip inner edge m, paint 0/1 + 0.45 x exit marks)):
// astroturf (the fibre texture, worn soil at its outer edge) or green / blue painted asphalt run-off (coarse paint over
// the aggregate in the detail alpha); HQ: tyre marks of cars running wide crossing it at a shallow angle.
// No extra reads: everything here is maths on the reads above. (float-only rule for masks and compares.)
#ifndef F2_GROUND_INCLUDED
#define F2_GROUND_INCLUDED
#include "UnityCG.cginc"
#include "Grade.cginc"

struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
    float2 uv2 : TEXCOORD2;
    float2 uv3 : TEXCOORD3;  // run-off strip only (_Turf): inner edge m, paint + 0.45 x marks; (0, 0) elsewhere
    float4 color : COLOR;
};
struct v2f
{
    float4 pos : SV_POSITION;
    float4 uv : TEXCOORD0;   // detail uv, macro uv
    float2 trk : TEXCOORD1;  // lateral m, along m
    half3 color : TEXCOORD2; // baked light
    half sideSun : TEXCOORD4; // vertex alpha (LightBake): side of the track (>0.5 = left) + the sun's share of the baked light
    float2 run : TEXCOORD5;   // run-off strip (uv3)
    UNITY_FOG_COORDS(3)
};

sampler2D _MainTex, _MacroTex;
float4 _MainTex_ST;
// 0..1 hash of a float (no sin)
float GHash(float n)
{
    float h = frac(n * 0.1031);
    h *= h + 33.33;
    h *= h + h;
    return frac(h);
}
// box-filtered coverage of the band a..b along x for a pixel w wide
float GBand(float x, float a, float b, float w)
{
    return saturate((x - a) / w + 0.5) - saturate((x - b) / w + 0.5);
}
float _MacroScale, _MipBias;
fixed4 _DryTint;      // rgb multiplier of dry patches, a = how much
float4 _Stripe;       // from (m), to (m), stripe width (m), strength
float4 _Wall;         // barrier line (m), darkening, -, -
float4 _Ruts;         // gravel inner edge (m), strength, -, -
float _Clumps;        // grass only (1): the detail's alpha as clumps / tufts and lush or thin patches (HQ), 0 = off
float _Turf;          // run-off strip behind the kerbs (1): astroturf / painted run-off, 0 = off
fixed4 _RunGreen, _RunBlue; // the run-off paints
sampler2D _F2RoadShadow; // global (RoadShadow.cs): sun shadows in track space, here continued onto verges and run-offs
float4 _F2RoadShadowP;   // x: strips per metre along, y: 1 / strips, z: across scale of the fine zone (1/m), w: outer half width (m)
float4 _F2RoadShadowQ;   // the coarser outer zone: x: where it starts (m), y: its across scale (1/m), z: its first column (0..0.5)

v2f vert (appdata v)
{
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex);
    float2 m = v.uv * _MacroScale;
    o.uv = float4(TRANSFORM_TEX(v.uv, _MainTex), m.x * 0.8 - m.y * 0.6, m.x * 0.6 + m.y * 0.8);
    o.trk = v.uv2;
    o.color = v.color.rgb * 2.0; // stored light 0.5 = full brightness (LightBake)
    o.sideSun = v.color.a;
    o.run = v.uv3;
    UNITY_TRANSFER_FOG(o, o.pos);
    return o;
}

fixed4 frag (v2f i) : SV_Target
{
    float lat = i.trk.x;
    float wl = max(fwidth(lat), 1e-3); // outside any branch
    float along = i.trk.y;
    float wa = max(fwidth(along), 1e-3);
#ifdef GROUND_HQ
    half4 d = tex2Dbias(_MainTex, float4(i.uv.xy, 0, max(_MipBias, 0.0))); // never sharper than the mips: no crawling grass
    // second, larger and turned copy of the same photo detail, averaged in: the 4.5 m tile never shows a repeat
    float2 u2 = float2(i.uv.x * 0.8 - i.uv.y * 0.6, i.uv.x * 0.6 + i.uv.y * 0.8) * 0.29;
    half4 d2 = tex2Dbias(_MainTex, float4(u2, 0, max(_MipBias, 0.0)));
    half3 d0 = d.rgb;                                    // the sharp read alone (gravel stones near the camera)
    d.rgb = lerp(d.rgb, d2.rgb, 0.4 - 0.25 * _Turf);    // (turf: keep the fibres)
    // grass: the alpha holds clumps (~0.75 m at this tile, ~2.6 m patches at the larger one); 0.5 = neutral
    half tuft = (half)lerp(0.5, d.a, _Clumps), lush = (half)lerp(0.5, d2.a, _Clumps);
#else
    half4 d = tex2Dbias(_MainTex, float4(i.uv.xy, 0, 0.6)); // Low: blurrier mips, kinder to the texture cache
#endif
    half4 mac = tex2D(_MacroTex, i.uv.zw);
    half3 col = d.rgb * (0.80 + 0.40 * mac.r);
    col = lerp(col, col * _DryTint.rgb, mac.g * _DryTint.a);
#ifdef GROUND_HQ
    // clumps: the turf is lumpy (darker hollows between tufts), lush darker blue-green patches and thin, paler ones
    col *= 0.9 + 0.2 * tuft;
    col *= lerp(half3(1.0, 1.0, 1.0), half3(0.80, 0.87, 0.84), saturate((lush - 0.56) * 3.5));
    col *= lerp(half3(1.0, 1.0, 1.0), half3(1.10, 1.06, 0.88), saturate((0.40 - lush) * 3.5));
#endif

    // ---- the 2 m run-off strip behind the kerbs: astroturf or painted asphalt (uniform branch)
    UNITY_BRANCH
    if (_Turf > 0.5)
    {
        float rel = lat - i.run.x;                                  // metres out from the strip's inner edge, 0..2
        float paintF = i.run.y > 0.95 ? 1.0 : 0.0;
        float marksA = saturate((i.run.y - paintF) * (1.0 / 0.45));
#ifdef GROUND_HQ
        half agg = (half)(lerp(d.a, d2.a, 0.35) - 0.5);           // aggregate under the paint, signed
#else
        half agg = (half)(d.a - 0.5);
#endif
        // paint: green next to the kerb, a blue band outside it; coarse (thick and thin over the stones), a soft
        // long drift in tone, and lighter where the aggregate pokes through
        float blueF = saturate((rel - 1.25) / wl + 0.5);
        half3 pc = lerp(_RunGreen.rgb, _RunBlue.rgb, (half)blueF);
        pc *= (0.9 + 0.2 * mac.r) * (1.0 + 0.55 * agg);
        pc = lerp(pc, half3(0.16, 0.16, 0.15), (half)saturate(agg * 3.0 - 0.6) * 0.35);
        // turf: the fibre texture (rgb), a drift in tone
        half3 tc = col;
        col = lerp(tc, pc, (half)paintF);
        // outer edge: soil and worn grass creep onto it in patches (and dust over the paint); inner edge: rubber dust
        float wob = 0.18 * sin(along * 0.31 + mac.b * 5.0) + 0.1 * sin(along * 1.7);
        half soilE = (half)saturate((rel - 1.62 - wob) * 3.0) * (half)saturate(mac.b * 1.6 + 0.1);
        col = lerp(col, half3(0.085, 0.065, 0.04) * (0.8 + 0.4 * mac.r), soilE * (0.75 - 0.35 * (half)paintF));
        col *= 1.0 - 0.25 * (half)saturate(1.0 - rel * 3.0);
#ifdef GROUND_HQ
        // tyre marks of cars running wide at corner exits: pairs crossing the strip at a shallow angle (~1 m outwards
        // over 7 m), soft-edged; on turf they show as flattened, darker fibres
        float sm = along - rel * 7.0, wsm = wa + 7.0 * wl + 0.35;
        float cell = floor(sm / 29.0);
        float h1 = GHash(cell * 5.13 + i.run.x), h2 = GHash(cell * 2.71 + 3.0);
        float xm = sm - cell * 29.0 - 2.0 - 10.0 * h1;
        float pair = GBand(xm, 0.0, 1.5, wsm) + GBand(xm, 11.2, 12.7, wsm);
        float tm = pair * (h2 > 0.3 ? 1.0 : 0.0) * marksA * saturate(rel * 2.0) * (0.6 + 0.4 * h2);
        col *= 1.0 - (half)tm * (0.42 - 0.12 * (half)paintF);
#endif
    }

    // mowing stripes: bands along the track, light / dark as the mower went up and down; box-filtered so they never
    // shimmer, and faded where a band is thinner than a couple of pixels
    half band = (half)saturate((lat - _Stripe.x) * 0.7) * (half)saturate((_Stripe.y - lat) * 0.25);
#ifdef GROUND_HQ
    // the mower's lines wander a little, and the contrast comes and goes along the lap (grass leaning one way or
    // the other, cut on different days)
    float ph = (lat + 0.22 * sin(i.trk.y * 0.037) + 0.08 * sin(i.trk.y * 0.13)) / _Stripe.z, wp = wl / _Stripe.z;
    half vary = (half)(0.55 + 0.45 * sin(i.trk.y * 0.0091 + lat * 0.05));
#else
    float ph = lat / _Stripe.z, wp = wl / _Stripe.z;
    half vary = 1.0;
#endif
    half sq = (half)saturate((abs(frac(ph) - 0.5) * 2.0 - 0.5) / max(2.0 * wp, 1e-3) + 0.5);
    half fadeFar = (half)saturate(1.5 - wp * 3.0);
    col *= 1.0 + _Stripe.w * band * fadeFar * vary * (sq * 2.0 - 1.0) * (0.75 + 0.5 * mac.b);

    // worn, darker strip along the barrier (mowers and marshals cannot reach under it)
    half nb = (half)saturate(1.0 - abs(lat - _Wall.x) * 0.6);
    col *= 1.0 - _Wall.y * nb * nb;
#ifdef GROUND_HQ
    // grass only: long, clumpy grass with dry tips at the barrier foot, and bare dusty soil between the tufts along
    // the verge's inner edge (wheels cut it, marshals walk it)
    half longG = (half)saturate(1.2 - abs(lat - _Wall.x) * 0.8) * (half)_Clumps;
    col *= lerp(half3(1.0, 1.0, 1.0), (0.62 + 0.75 * tuft) * half3(1.08, 1.0, 0.78), longG * 0.85);
    // where the run-off meets the grass: worn, patchy (wider in places along the lap), soil between the tufts
    half wpatch = (half)saturate(0.55 + 0.6 * sin(along * 0.043 + mac.b * 6.0) + 0.3 * sin(along * 0.17));
    half worn = (half)saturate(1.0 - abs(lat - _Stripe.x - 0.2) * (1.6 - 0.7 * wpatch)) * saturate((0.66 - tuft) * 3.0) * (half)saturate(_Stripe.w * 20.0) * (half)_Clumps;
    col = lerp(col, half3(0.20, 0.17, 0.12) * (0.8 + 0.4 * mac.r), worn * (0.4 + 0.3 * wpatch));
#else
    // Low: the worn edge as a mean darkening only
    col *= 1.0 - 0.12 * (half)saturate(1.0 - abs(lat - _Stripe.x - 0.2) * 1.3) * (half)saturate(_Stripe.w * 20.0) * (half)_Clumps;
#endif

#ifdef GROUND_HQ
    // gravel: churned darker stones near the inner edge, and the ruts of cars that went off, in patches
    float g = lat - _Ruts.x;
    half near = (half)saturate(1.0 - g * 0.22) * (half)step(0.0, g);
    float o = g - 2.2 - 1.6 * sin(i.trk.y * 0.045 + mac.r * 6.0);
    // a 0.14 m rut; once a pixel is wider it keeps its average darkness (thinner contrast), not its width in pixels
    float rw = max(0.14, wl);
    half rut = (half)(saturate(1.0 - abs(abs(o) - 0.8) / rw) * (0.14 / rw)) * (half)saturate((mac.b - 0.42) * 6.0);
    col *= 1.0 - _Ruts.y * (0.12 * near + 0.32 * rut * near);
    // ruts where cars went off: a pair dug in diagonally from the inner edge (~1 m outwards over 5 m), a lighter rim of
    // thrown-up stones beside each; in patches, fading over the first ~8 m of the trap
    float sd = along - g * 5.0, sdw = wa + 5.0 * wl + 0.12;
    float dc = floor(sd / 41.0);
    float dh = GHash(dc * 3.17 + 1.0), dh2 = GHash(dc * 7.3 + 2.0);
    float dx = sd - dc * 41.0 - 3.0 - 20.0 * dh;
    float dig = GBand(dx, 0.0, 1.0, sdw) + GBand(dx, 8.0, 9.0, sdw);
    float rim = GBand(dx, -0.7, 0.0, sdw) + GBand(dx, 1.0, 1.7, sdw) + GBand(dx, 7.3, 8.0, sdw) + GBand(dx, 9.0, 9.7, sdw);
    float offW = (dh2 > 0.45 ? 1.0 : 0.0) * saturate(1.0 - g * 0.12) * (g > 0.0 ? 1.0 : 0.0) * saturate(_Ruts.y);
    col *= 1.0 - (half)(offW * (0.3 * dig - 0.1 * rim));
    // raked ridges along the trap (~0.4 m apart, wandering a little): soft light / shade, gone once a pixel spans ~15 cm
    // (and where the stones are churned near the edge or in the ruts)
    float rk = abs(frac((g + 0.07 * sin(along * 0.19)) * 2.5) - 0.5) * 2.0;
    float rakeF = saturate(1.4 - wl * 9.0) * (g > 0.0 ? 1.0 : 0.0) * saturate(_Ruts.y) * (1.0 - (float)near * 0.8);
    col *= 1.0 + (half)((rk - 0.5) * 0.16 * rakeF);
    // single stones near the camera: the sharp read against the averaged one, up to ~4 cm per pixel
    half l0 = dot(d0, half3(0.3, 0.59, 0.11)), lm = dot(d.rgb, half3(0.3, 0.59, 0.11));
    float stoneF = saturate(1.5 - wl * 30.0) * (g > 0.0 ? 1.0 : 0.0) * saturate(_Ruts.y);
    col *= 1.0 + (half)stoneF * 0.6 * (l0 - lm) / (lm + 0.04);
#endif
    // gravel depth (both levels): damp, darker stones along the inner edge (sprinklers, shade of the turf) and at the
    // barrier foot, wandering along the trap
    {
        float gg = lat - _Ruts.x;
        float inT = (gg > 0.0 ? 1.0 : 0.0) * saturate(_Ruts.y);
        float damp = saturate(1.0 - (gg - 0.3 * sin(along * 0.05)) * 0.7) + saturate(1.0 - abs(lat - _Wall.x) * 0.5) * 0.7;
        col *= 1.0 - (half)(inT * saturate(damp) * 0.22);
    }

    // sun shadows beyond the road (verges, grass, gravel, run-offs: uv2 holds the distance from the centreline there): the
    // road's mask, out to 40 m past the track edge (fine texels near the track, coarser beyond _F2RoadShadowQ.x), signed by
    // the side (vertex alpha), takes away the sun's share of the baked light where it says shadow. One read.
    if (lat > 0.5 && _F2RoadShadowP.z > 0.0)
    {
        float ss = i.sideSun; // (compare in float: a half compare breaks the min-precision bytecode)
        bool left = ss > 0.5;
        half sf = (half)saturate(left ? (ss - 0.55) * 2.222 : ss * 2.222);
        float gu = (lat < _F2RoadShadowQ.x || _F2RoadShadowQ.y <= 0.0) ? lat * _F2RoadShadowP.z : _F2RoadShadowQ.z + (lat - _F2RoadShadowQ.x) * _F2RoadShadowQ.y;
        float2 sp = float2((left ? gu : -gu) + 0.5, i.trk.y * _F2RoadShadowP.x);
        float2 sgx = ddx(sp), sgy = ddy(sp);
        float strip = floor(sp.y);
        float2 suv = float2((strip + saturate(sp.x)) * _F2RoadShadowP.y, sp.y - strip);
        float2 gsc = float2(_F2RoadShadowP.y, 1.0) * 1.5;
        half shade = tex2Dgrad(_F2RoadShadow, suv, sgx * gsc, sgy * gsc).r;
        half edge = (half)saturate((_F2RoadShadowP.w - lat) * 0.5); // fades out at the mask's outer edge
        col *= 1.0 - sf * (1.0 - shade) * edge;
    }

    col *= i.color;
    fixed4 c = fixed4(col, 1);
    UNITY_APPLY_FOG(i.fogCoord, c);
    c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
    return c;
}
#endif
