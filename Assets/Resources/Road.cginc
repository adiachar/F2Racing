// Road.cginc — shared code of Road.shader (both levels). Define ROAD_HQ before including for the Medium/High path.
#ifndef F2_ROAD_INCLUDED
#define F2_ROAD_INCLUDED
#include "UnityCG.cginc"

struct appdata
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float2 uv2 : TEXCOORD2;
    float4 color : COLOR;
};
struct v2f
{
    float4 pos : SV_POSITION;
    float4 uv : TEXCOORD0;    // lateral m, along m, macro uv
    float4 misc : TEXCOORD1;  // x: lateral distance to the racing line (m) or kerb across, y: rubber amount (or -1 = kerb), z: detail fade, w: sun visibility
    half3 color : TEXCOORD2;  // baked light
    UNITY_FOG_COORDS(3)
#ifdef ROAD_HQ
    float3 view : TEXCOORD4;  // surface -> camera (world)
    half3 n : TEXCOORD5;
#endif
};

sampler2D _MainTex;
fixed4 _Asphalt, _Dust, _Paint, _KerbRed, _SunCol;
float _HalfWidth, _MipBias;
float4 _Grid, _SunDir;

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
    half3 lit = v.color.rgb * 2.0;             // stored light 0.5 = full brightness (LightBake)
    o.color = lit;
    // baked light is ~0.7 in shade and ~1.6 in the sun: how much sun reaches this vertex
    o.misc.w = saturate((dot(lit, half3(0.3, 0.59, 0.11)) - 0.85) * 1.6);
    UNITY_TRANSFER_FOG(o, o.pos);
#ifdef ROAD_HQ
    o.view = toCam;
    o.n = UnityObjectToWorldNormal(v.normal);
#endif
    return o;
}

// box-filtered coverage of the band a..b along x, for a pixel w wide (same units): crisp, never aliased
half Band(float x, float a, float b, float w)
{
    return (half)(saturate((x - a) / w + 0.5) - saturate((x - b) / w + 0.5));
}

fixed4 frag (v2f i) : SV_Target
{
    float lat = i.uv.x, along = i.uv.y;
    float wl = max(fwidth(lat), 1e-4), wa = max(fwidth(along), 1e-4); // pixel footprint in metres (outside any branch)
    bool kerb = i.misc.y < -0.5;

#ifdef ROAD_HQ
    half4 d = tex2Dbias(_MainTex, float4(i.uv.xy * 0.2, 0, _MipBias));
    half4 mac = tex2D(_MainTex, i.uv.zw);
    half blotch = mac.g, patch = mac.b;
#else
    half4 d = tex2D(_MainTex, i.uv.xy * 0.2);
    half blotch = d.g, patch = 0.0;
#endif
    half fade = i.misc.z;
    half grain = lerp(0.5, d.r, fade);

    // ---- asphalt: aggregate grain, metre-scale blotches, darker resurfaced patches
    half tone = (0.70 + 0.60 * grain) * (0.90 + 0.20 * blotch) * (0.93 + 0.14 * d.g) * (1.0 - 0.16 * patch);
    half3 col = _Asphalt.rgb * tone;

    // ---- rubber laid along the racing line: a broad band plus the two tyre tracks, patchy along its length
    half x = (half)i.misc.x;
    half broad = saturate(1.0 - x * x * 0.32);                 // ~1.8 m half width
    half tracks = saturate(1.0 - abs(abs(x) - 0.8) * 2.2);     // wheels ~1.6 m apart
    half rub = kerb ? 0.0 : saturate(i.misc.y) * broad * (0.55 + 0.45 * tracks) * (0.7 + 0.6 * blotch);
    col *= 1.0 - 0.42 * rub;

    // ---- dust off-line: lighter towards the edges where nobody drives
    half edge = smoothstep(_HalfWidth - 3.2, _HalfWidth - 0.3, abs(lat));
    col = lerp(col, _Dust.rgb * (0.8 + 0.4 * grain), edge * 0.45 * (1.0 - rub));

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
    paint *= kerb ? 0.0 : 1.0;
    // paint on rough asphalt: the grain shows through a little, and tyres wear it where the line crosses it
    half3 paintCol = _Paint.rgb * (0.86 + 0.18 * grain) * (1.0 - 0.3 * rub);
    col = lerp(col, paintCol, paint);

    // ---- kerbs: 1.5 m red / white blocks, crisp, with tyre-blackened inner edge and a darker outer lip
    half across = (half)i.misc.x;
    float tri = abs(frac(along * (1.0 / 3.0)) - 0.5) * 2.0;          // 0..1 triangle wave, period 3 m
    half red = (half)saturate((tri - 0.5) / (wa * (2.0 / 3.0)) + 0.5); // d(tri)/d(along) = 2/3
    half3 kc = lerp(_Paint.rgb, _KerbRed.rgb, red) * (0.86 + 0.2 * grain);
    kc *= 1.0 - 0.35 * saturate(1.0 - across * 3.0) * blotch;        // rubber where wheels ride the kerb
    kc *= 1.0 - 0.25 * smoothstep(0.82, 1.0, across);                 // outer lip
    col = kerb ? kc : col;

    col *= i.color;

#ifdef ROAD_HQ
    // ---- sheen: low sun glints off the asphalt (polished rubber and paint more), the sky at grazing angles
    float3 V = normalize(i.view);
    half3 N = normalize(i.n);
    half3 R = reflect(-_SunDir.xyz, N);
    half s = saturate(dot(V, R));
    // rough asphalt: a broad forward-scatter lobe (the road brightens looking towards the sun even when it is high)
    // plus a tight glint; s^32 by repeated squaring
    half broad = saturate(s * 1.6 - 0.6); broad *= broad;
    half s2 = s * s, s4 = s2 * s2, s8 = s4 * s4, s32 = s8 * s8; s32 *= s32;
    half gloss = 0.35 + 0.55 * rub + 0.4 * paint + (kerb ? 0.5 : 0.0) + 0.5 * d.a * fade; // stones sparkle near
    half3 spec = _SunCol.rgb * (broad * 0.45 + s32 * 0.6) * gloss * i.misc.w;
    half f = 1.0 - saturate(dot(V, N)); half f2 = f * f; half fres = f2 * f2 * f;
    col += spec + unity_FogColor.rgb * (fres * 0.22 * (0.6 + 0.4 * gloss));
#endif

    fixed4 c = fixed4(col, 1);
    UNITY_APPLY_FOG(i.fogCoord, c);
    return c;
}
#endif
