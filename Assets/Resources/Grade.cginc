// Grade.cginc — colour grading and tone mapping done at the end of every world shader, so the picture gets
// a bright, clean "broadcast" look with no full-screen pass (a post stack costs a whole extra read + write of the screen
// on a phone). The project renders in linear colour space: the input is linear light (baked light can go well above 1
// in the sun), the output is linear 0..1 and the hardware converts it to sRGB.
//
// Steps (about 20 ALU ops per pixel, no texture reads, no pow / exp / log):
//   1. white balance + exposure:   c *= _GradeTint.rgb * _GradeExposure
//   2. tone curve:                 linear up to 0.8 (no crushed shadows, no dimmed mid-tones), soft shoulder above
//   3. saturation:                 in the perceptual space (square root of linear), around Rec.709 luma: richer colour
//                                  without pushing the small channels of strong colours to zero (red paint, grass)
//   4. contrast + lift/gamma/gain: in the same perceptual space, pivot at mid-grey:
//        p = (p - 0.45) * _GradeContrast + 0.45;  p = p * gain + lift * (1 - p);  p += gamma * p * (1 - p)
//      (gamma is a cheap mid-tone bend: + brightens the mids of that channel, - darkens them)
// All values are globals set by Sky.cs (per circuit, time of day and weather); Grade.cs puts neutral values in at start
// so nothing is ever black because a global was not set yet. Grade.Apply (C#) mirrors this maths exactly.
#ifndef F2_GRADE_INCLUDED
#define F2_GRADE_INCLUDED

half  _GradeExposure;   // 1 = neutral (the base exposure that keeps mid-grey in place is folded in by Grade.cs)
half  _GradeSat;        // 1 = neutral
half4 _GradeTint;       // white balance (rgb multipliers, 1 = neutral)
half  _GradeContrast;   // 1 = neutral
half4 _GradeLift;       // rgb, 0 = neutral (raises the blacks towards this colour)
half4 _GradeGamma;      // rgb, 0 = neutral (mid-tone bend, about -0.3..0.3)
half4 _GradeGain;       // rgb, 1 = neutral

inline half3 F2Tone(half3 x)
{
    // bright, clean "Real Racing" curve: exactly linear up to 0.8 (no crushed shadows, no dimmed mid-tones), then a soft
    // shoulder so sunlit highlights above 1 roll off to white instead of clipping: 1.0 -> 0.90, 1.5 -> 0.96, 3 -> 0.98
    x = max(x, (half3)0.0);
    half3 h = max(x - 0.8, (half3)0.0);
    return min(x, 0.8 + h / (1.0 + h * 5.0));
}

#if defined(F2_GRADE_LOW)
// Low graphics level (global keyword, GameManagerPerf.cs; only the shaders drawn on Low compile this variant):
// white balance, exposure, tone curve and saturation (in linear: a little stronger than the perceptual one above, which
// stands in for the missing contrast); no contrast / lift / gamma / gain (no sqrt, ~12 fewer ops)
inline half3 F2Grade(half3 c)
{
    c = F2Tone(c * _GradeTint.rgb * _GradeExposure);
    half l = dot(c, half3(0.2126, 0.7152, 0.0722));
    return max(lerp((half3)l, c, _GradeSat), (half3)0.0);
}
#else
inline half3 F2Grade(half3 c)
{
    c *= _GradeTint.rgb * _GradeExposure;
    half3 p = sqrt(F2Tone(c)); // F2Tone output is >= 0
    half l = dot(p, half3(0.2126, 0.7152, 0.0722));
    p = lerp((half3)l, p, _GradeSat); // may dip below 0 for strong colours: the saturate below clamps it
    p = saturate((p - 0.45) * _GradeContrast + 0.45);
    p = p * _GradeGain.rgb + _GradeLift.rgb * (1.0 - p);
    p = saturate(p + _GradeGamma.rgb * p * (1.0 - p));
    return p * p;
}
#endif

// for additive light (glare, sparks): only the white balance and exposure, so adding stays adding
inline half3 F2GradeAdd(half3 c) { return c * _GradeTint.rgb * min(_GradeExposure, 1.5); }

// ---- aerial perspective (haze), shared by every world shader through the fog macros ---------------------------------
// Unity's linear fog is kept (same factor), then three things are added in the VERTEX shader only (nothing per pixel):
//   * the fog colour leans towards a warm in-scatter colour when the view ray points at the sun (pow 8 lobe)
//   * a little exponential haze with distance, thinning with height above the ground (denser low and near the ground)
//   * the fog value now carries its own colour (rgb) and the keep factor (a), so one float4 interpolator replaces float1
// Globals from Sky.cs (all 0 = plain Unity fog):  _F2HazeSun.xyz = direction to the sun, .w = in-scatter strength 0..1;
// _F2HazeCol.rgb = warm in-scatter colour (linear); _F2HazeP = (density 1/m, height falloff 1/m, haze strength, 0).
// The sky itself (skybox, clouds) has no fog, so the zenith stays a clean blue.
float4 _F2HazeSun;
half4  _F2HazeCol;
float4 _F2HazeP;

#if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
float4 F2Haze(float4 clip)
{
    UNITY_CALC_FOG_FACTOR_RAW(UNITY_Z_0_FAR_FROM_CLIPSPACE(clip.z));
    float keep = saturate(unityFogFactor);
    // exact view-space position from clip space (valid for vertices behind the camera too), then the world ray
    float3 pv = float3(clip.x / UNITY_MATRIX_P._m00, clip.y / UNITY_MATRIX_P._m11, -clip.w);
    float d = length(pv);
    float3 wd = mul(pv, (float3x3)UNITY_MATRIX_V) / max(d, 1e-3);
    float s = saturate(dot(wd, _F2HazeSun.xyz)); s *= s; s *= s; s *= s; s *= _F2HazeSun.w;
    float rel = max(wd.y * d * 0.5 + 3.0, 0.0);            // mid-ray height above the track (camera is ~3 m up)
    float amt = (1.0 - exp2(-d * _F2HazeP.x)) * exp2(-rel * _F2HazeP.y) * _F2HazeP.z * (1.0 + s);
    return float4(lerp(unity_FogColor.rgb, _F2HazeCol.rgb, s), keep * (1.0 - saturate(amt)));
}
#undef UNITY_FOG_COORDS
#undef UNITY_TRANSFER_FOG
#undef UNITY_APPLY_FOG
#define UNITY_FOG_COORDS(idx) float4 fogCoord : TEXCOORD##idx;
#define UNITY_TRANSFER_FOG(o,outpos) o.fogCoord = F2Haze(outpos)
#define UNITY_APPLY_FOG(coord,col) col.rgb = lerp((coord).rgb, (col).rgb, saturate((coord).a))
#endif

#endif
