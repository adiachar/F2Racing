// Ground.cginc — shared code of Ground.shader (grass, terrain, verges, gravel traps). Lighting is baked into the vertex
// colours (LightBake.cs) like Baked.shader. Two texture reads per pixel:
//   _MainTex  detail (grass blades / gravel stones), world space, a few metres per tile
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
    UNITY_FOG_COORDS(3)
};

sampler2D _MainTex, _MacroTex;
float4 _MainTex_ST;
float _MacroScale, _MipBias;
fixed4 _DryTint;      // rgb multiplier of dry patches, a = how much
float4 _Stripe;       // from (m), to (m), stripe width (m), strength
float4 _Wall;         // barrier line (m), darkening, -, -
float4 _Ruts;         // gravel inner edge (m), strength, -, -

v2f vert (appdata v)
{
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex);
    float2 m = v.uv * _MacroScale;
    o.uv = float4(TRANSFORM_TEX(v.uv, _MainTex), m.x * 0.8 - m.y * 0.6, m.x * 0.6 + m.y * 0.8);
    o.trk = v.uv2;
    o.color = v.color.rgb * 2.0; // stored light 0.5 = full brightness (LightBake)
    UNITY_TRANSFER_FOG(o, o.pos);
    return o;
}

fixed4 frag (v2f i) : SV_Target
{
    float lat = i.trk.x;
    float wl = max(fwidth(lat), 1e-3); // outside any branch
#ifdef GROUND_HQ
    half4 d = tex2Dbias(_MainTex, float4(i.uv.xy, 0, max(_MipBias, 0.0))); // never sharper than the mips: no crawling grass
#else
    half4 d = tex2Dbias(_MainTex, float4(i.uv.xy, 0, 0.6)); // Low: blurrier mips, kinder to the texture cache
#endif
    half4 mac = tex2D(_MacroTex, i.uv.zw);
    half3 col = d.rgb * (0.80 + 0.40 * mac.r);
    col = lerp(col, col * _DryTint.rgb, mac.g * _DryTint.a);

    // mowing stripes: bands along the track, light / dark as the mower went up and down; box-filtered so they never
    // shimmer, and faded where a band is thinner than a couple of pixels
    half band = (half)saturate((lat - _Stripe.x) * 0.7) * (half)saturate((_Stripe.y - lat) * 0.25);
    float ph = lat / _Stripe.z, wp = wl / _Stripe.z;
    half sq = (half)saturate((abs(frac(ph) - 0.5) * 2.0 - 0.5) / max(2.0 * wp, 1e-3) + 0.5);
    half fadeFar = (half)saturate(1.5 - wp * 3.0);
    col *= 1.0 + _Stripe.w * band * fadeFar * (sq * 2.0 - 1.0) * (0.75 + 0.5 * mac.b);

    // worn, darker strip along the barrier (mowers and marshals cannot reach under it)
    half nb = (half)saturate(1.0 - abs(lat - _Wall.x) * 0.6);
    col *= 1.0 - _Wall.y * nb * nb;

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

    col *= i.color;
    fixed4 c = fixed4(col, 1);
    UNITY_APPLY_FOG(i.fogCoord, c);
    c.rgb = F2Grade(c.rgb); // colour grade + tone curve (Grade.cginc)
    return c;
}
#endif
