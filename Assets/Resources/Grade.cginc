// Grade.cginc — colour grading and filmic tone mapping done at the end of every world shader, so the picture gets
// a "broadcast" look with no full-screen pass (a post stack costs a whole extra read + write of the screen on a phone).
// The project renders in linear colour space: the input is linear light (baked light can go well above 1 in the sun),
// the output is linear 0..1 and the hardware converts it to sRGB.
//
// Steps (about 20 ALU ops per pixel, no texture reads, no pow / exp / log):
//   1. white balance + exposure:   c *= _GradeTint.rgb * _GradeExposure
//   2. filmic tone curve:          ACES-like rational fit (Narkowicz), with a slightly softer toe; highlights roll off
//                                  instead of clipping, mid-grey stays where it was at exposure 1
//   3. saturation:                 around Rec.709 luma
//   4. contrast + lift/gamma/gain: in a perceptual space (square root of linear), pivot at mid-grey:
//        p = (p - 0.45) * _GradeContrast + 0.45;  p = p * gain + lift * (1 - p);  p += gamma * p * (1 - p)
//      (gamma is a cheap mid-tone bend: + brightens the mids of that channel, - darkens them)
// All values are globals set by Sky.cs (per circuit, time of day and weather); Grade.cs puts neutral values in at start
// so nothing is ever black because a global was not set yet.
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
    // x (a x + b) / (x (c x + d) + e); denominator >= 0.14, so always safe on half precision
    x = max(x, (half3)0.0);
    return saturate((x * (2.51 * x + 0.05)) / (x * (2.43 * x + 0.59) + 0.14));
}

#if defined(F2_GRADE_LOW)
// Low graphics level (global keyword, GameManagerPerf.cs; only the shaders drawn on Low compile this variant):
// white balance, exposure, tone curve and saturation; no contrast / lift / gamma / gain (no sqrt, ~12 fewer ops)
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
    c = F2Tone(c);
    half l = dot(c, half3(0.2126, 0.7152, 0.0722));
    c = max(lerp((half3)l, c, _GradeSat), (half3)0.0);
    half3 p = sqrt(c);
    p = saturate((p - 0.45) * _GradeContrast + 0.45);
    p = p * _GradeGain.rgb + _GradeLift.rgb * (1.0 - p);
    p = saturate(p + _GradeGamma.rgb * p * (1.0 - p));
    return p * p;
}
#endif

// for additive light (glare, sparks): only the white balance and exposure, so adding stays adding
inline half3 F2GradeAdd(half3 c) { return c * _GradeTint.rgb * min(_GradeExposure, 1.5); }

#endif
