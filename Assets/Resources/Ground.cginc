// Ground.cginc — shared code of Ground.shader (grass, terrain, verges, gravel traps). Lighting is baked into the vertex
// colours (LightBake.cs) like Baked.shader. Two texture reads per pixel:
//   _MainTex  detail (photo grass / gravel, CC0, see Textures/CREDITS.txt), world space, a few metres per tile; Medium/High read it a second time at ~3.4x the scale, turned, and average the two
//   _MacroTex linear data at ~100 m per tile, turned 37 degrees: R brightness drift, G dry patches, B clumps
// Mesh data: uv0 = world (x, z) in metres; uv2 = (distance from the track centreline m, distance along the lap m) on
// verges and gravel traps, (0, 0) on the open land, where the track-relative terms below are all zero.
// Track-relative terms (pure maths, no reads): mowing stripes along the track, worn darker grass at the barrier,
// tyre ruts in the gravel near its inner edge. Define GROUND_HQ for Medium/High (ruts; Low keeps stripes).
#ifndef F2_GROUND_INCLUDED
#define F2_GROUND_INCLUDED
#include "UnityCG.cginc"
#include "Grade.cginc"

struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
    float2 uv2 : TEXCOORD2;
    float4 color : COLOR;
};
struct v2f
{
    float4 pos : SV_POSITION;
    float4 uv : TEXCOORD0;   // detail uv, macro uv
    float2 trk : TEXCOORD1;  // lateral m, along m
    half3 color : TEXCOORD2; // baked light
    half sideSun : TEXCOORD4; // vertex alpha (LightBake): side of the track (>0.5 = left) + the sun's share of the baked light
    UNITY_FOG_COORDS(3)
};

sampler2D _MainTex, _MacroTex;
float4 _MainTex_ST;
float _MacroScale, _MipBias;
fixed4 _DryTint;      // rgb multiplier of dry patches, a = how much
float4 _Stripe;       // from (m), to (m), stripe width (m), strength
float4 _Wall;         // barrier line (m), darkening, -, -
float4 _Ruts;         // gravel inner edge (m), strength, -, -
float _Clumps;        // grass only (1): the detail's alpha as clumps / tufts and lush or thin patches (HQ), 0 = off
sampler2D _F2RoadShadow; // global (RoadShadow.cs): sun shadows in track space, here continued onto verges and run-offs
float4 _F2RoadShadowP;   // x: strips per metre along, y: 1 / strips, z: 1 / width covered (m), w: half width (m)

v2f vert (appdata v)
{
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex);
    float2 m = v.uv * _MacroScale;
    o.uv = float4(TRANSFORM_TEX(v.uv, _MainTex), m.x * 0.8 - m.y * 0.6, m.x * 0.6 + m.y * 0.8);
    o.trk = v.uv2;
    o.color = v.color.rgb * 2.0; // stored light 0.5 = full brightness (LightBake)
    o.sideSun = v.color.a;
    UNITY_TRANSFER_FOG(o, o.pos);
    return o;
}

fixed4 frag (v2f i) : SV_Target
{
    float lat = i.trk.x;
    float wl = max(fwidth(lat), 1e-3); // outside any branch
#ifdef GROUND_HQ
    half4 d = tex2Dbias(_MainTex, float4(i.uv.xy, 0, max(_MipBias, 0.0))); // never sharper than the mips: no crawling grass
    // second, larger and turned copy of the same photo detail, averaged in: the 4.5 m tile never shows a repeat
    float2 u2 = float2(i.uv.x * 0.8 - i.uv.y * 0.6, i.uv.x * 0.6 + i.uv.y * 0.8) * 0.29;
    half4 d2 = tex2Dbias(_MainTex, float4(u2, 0, max(_MipBias, 0.0)));
    d.rgb = lerp(d.rgb, d2.rgb, 0.4);
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
    half worn = (half)saturate(1.0 - abs(lat - _Stripe.x) * 1.3) * saturate((0.58 - tuft) * 3.0) * (half)saturate(_Stripe.w * 20.0) * (half)_Clumps;
    col = lerp(col, half3(0.20, 0.17, 0.12) * (0.8 + 0.4 * mac.r), worn * 0.5);
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
#endif

    // sun shadows beyond the road (verges / run-offs only: uv2 holds the distance from the centreline there): the road's
    // mask, signed by the side (vertex alpha), takes away the sun's share of the baked light where it says shadow
    if (lat > 0.5 && _F2RoadShadowP.z > 0.0)
    {
        float ss = i.sideSun; // (compare in float: a half compare breaks the min-precision bytecode)
        bool left = ss > 0.5;
        half sf = (half)saturate(left ? (ss - 0.55) * 2.222 : ss * 2.222);
        float2 sp = float2((left ? lat : -lat) * _F2RoadShadowP.z + 0.5, i.trk.y * _F2RoadShadowP.x);
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
