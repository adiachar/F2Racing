// Ghost car (live penalties, GameManager PenaltyServe.cs; CarGhost.cs sets it): a float _Ghost 0..1 per renderer
// through a MaterialPropertyBlock. Drawn as a dithered screen-door fade in the shader's own opaque pass (no
// transparency, no extra pass): a stable 4x4 Bayer pattern in screen pixels drops 0.55 x _Ghost of the pixels
// (about half at 1), plus a slight cool tint and a rim lift so the car reads as "not really there".
// Only in the F2_GHOST variant (multi_compile_local): CarGhost.cs swaps the car to twin materials with the keyword
// while it is a ghost, so the normal variant has no clip() (Mali keeps early-Z / hidden-surface removal for it).
// All float (PRECISION RULE, CarPaint.shader).
#ifndef F2_CAR_GHOST
#define F2_CAR_GHOST

// 4x4 Bayer threshold (0.5 .. 15.5) / 16 from the pixel position (SV_POSITION.xy in the fragment). No table, no
// integer ops: two 2x2 Bayer levels, xor done as a + b - 2ab on 0/1 floats.
float F2Bayer4(float2 sp)
{
    float2 p = fmod(floor(sp), 4.0);
    float2 lo = fmod(p, 2.0), hi = floor(p * 0.5);
    float xl = lo.x + lo.y - 2.0 * lo.x * lo.y, xh = hi.x + hi.y - 2.0 * hi.x * hi.y;
    return (4.0 * (2.0 * xl + lo.y) + 2.0 * xh + hi.y + 0.5) * (1.0 / 16.0);
}
// drops the pixel when its threshold is under 0.55 g (g > 0 only: at 0 nothing would drop anyway)
void F2GhostClip(float2 sp, float g) { clip(F2Bayer4(sp) - 0.8 * g); } // 0.8: low opacity (Adi), was 0.55
// linear colour before fog / grade: a little cooler and paler, and a soft blue-white lift towards the silhouette.
// graze: (1 - N.V)^2 or similar, 0 facing the camera .. 1 at the edge
float3 F2GhostTint(float3 col, float g, float graze)
{
    float l = dot(col, float3(0.2126, 0.7152, 0.0722));
    float3 cool = lerp(col, float3(0.84, 0.95, 1.12) * (l * 0.85 + 0.03), 0.4 * g);
    return cool + float3(0.10, 0.16, 0.24) * (graze * 0.8 + 0.08) * g;
}
#endif
