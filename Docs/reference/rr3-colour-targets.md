# RR3 colour targets vs F2Racing (Monza, afternoon, sun elevation 48 deg)

On-screen sRGB 0-255, predicted with the offline model (/tmp/gradeeval: bake + F2Grade + Unity procedural sky).

| Surface | RR3 target | Before | After |
|---|---|---|---|
| Sunlit asphalt | 150-160, 147-156, 151-160 | 135,130,124 | 150,145,150 |
| Shadowed asphalt | 45-55% of sunlit | 71,77,86 (0.72 of sun) / ratio 0.59 | 82,87,103 (ratio 0.59) |
| Sunlit grass | 121-140, 130-138, 64-90 | 102,158,34 | 130,138,78 |
| White line/kerb | ~235-245 | 255,253,248 | 255,255,255 (clips; albedo not tuned) |
| Red kerb | ~200,40,35 | 255,20,8 | 251,36,28 (albedo too saturated, not tuned) |
| Sky 45 deg up | 110-145, 165-196, 230-252 | 88,122,200 | ~108,162,234 |
| Sky ~25 deg | paler | ~147,196,255 (15 deg) | ~141,204,255 |

## Changes
- Grade.cs, monza look: exposure 1.01->1.22, sat 1.02->0.85, contrast 1.03->1.04, tint (1.04,1,0.94)->(1.08,1,1.05), gain (1,0.99,0.97)->(1,1.01,1.01).
- Sky.cs, monza: tint (0.42,0.54,0.78)->(0.30,0.58,0.80), atmosphere 1.0->1.25, exposure 1.95->2.2, fog (0.74,0.79,0.86)->(0.76,0.84,0.93).
- EnvTex.cs: GrassTint (1.09,0.80,1.25) on every green grass mean (all tracks moved from neon green to olive).
- Not changed: sun intensity, LightBake, WorldBuilder, Grade.cginc, other track looks, night/rain paths.

## Open items
- Shadow ratio stays ~0.59 (sRGB luma); lowering needs a dimmer global sky fill in LightBake/Sky.SunAt.
- Kerb red / line white albedos live outside the owned files.
- Prediction only; no device capture to verify.
