// Grade.cs — the colour grade of the picture (Assets/Resources/Grade.cginc does the work at the end of every world
// shader; this side picks the values and puts them in shader globals). One grade per circuit (its light and air:
// Monaco warm, Spa fresh green, Austin hot...), changed by the time of day and by the weather (wet = cooler, flatter).
// Every dry circuit is a bright, sunny race day (Real Racing look): the looks only nudge colour, never grey it out.
// Values go in with SetGlobalVector / SetGlobalFloat (not SetGlobalColor: in linear colour space Unity would
// convert a Color from sRGB and exaggerate the tints).
using UnityEngine;

public struct GradeLook
{
    public float exposure, sat, contrast;  // 1 = neutral
    public Vector3 tint;                   // white balance multipliers, 1 = neutral
    public Vector3 lift, gamma, gain;      // 0 / 0 / 1 = neutral (see Grade.cginc)
}

public static class Grade
{
    // global punch on top of every circuit look: richer colour (saturation works in the perceptual space, so 1.08 there
    // is about 1.16 in linear), a little more contrast, much less lifted (hazy) black
    public const float PunchSat = 1.08f, PunchCon = 1.08f, LiftKeep = 0.35f;
    // a bright, sunny broadcast look: sunlit asphalt (sRGB 0.38) reads as a clean mid grey, ~0.51 on screen. The bake now
    // has a stronger, warmer sun and a dimmer sky fill (LightBake / Sky.SunAt), so this is a little higher than before.
    public const float BaseExposure = 1.18f;

    static readonly int idExp = Shader.PropertyToID("_GradeExposure"), idSat = Shader.PropertyToID("_GradeSat"),
        idTint = Shader.PropertyToID("_GradeTint"), idCon = Shader.PropertyToID("_GradeContrast"),
        idLift = Shader.PropertyToID("_GradeLift"), idGamma = Shader.PropertyToID("_GradeGamma"), idGain = Shader.PropertyToID("_GradeGain");

    public static GradeLook Neutral => new GradeLook { exposure = 1, sat = 1, contrast = 1, tint = Vector3.one, lift = Vector3.zero, gamma = Vector3.zero, gain = Vector3.one };
    public static GradeLook Last = Neutral;

    // neutral values before anything is drawn (an unset global is 0, which would be black)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init() => Push(Neutral);

    static GradeLook G(float exp, float sat, float con, Vector3 tint, Vector3 lift, Vector3 gamma, Vector3 gain) =>
        new GradeLook { exposure = exp, sat = sat, contrast = con, tint = tint, lift = lift, gamma = gamma, gain = gain };
    static Vector3 V(float r, float g, float b) => new Vector3(r, g, b);

    // per circuit (Track.def.id), dry
    public static GradeLook For(string id)
    {
        switch (id)
        {
            case "monaco":      // warm Mediterranean: golden light, rich colour, clean blue shadows off the sea
                return G(1.02f, 1.06f, 1.06f, V(1.05f, 1.00f, 0.93f), V(0.000f, 0.004f, 0.010f), V(0.03f, 0.01f, -0.02f), V(1.01f, 1.00f, 0.98f));
            case "spa":         // Ardennes on a sunny day: fresh, green, neutral light
                return G(1.00f, 1.00f, 1.03f, V(1.00f, 1.00f, 0.99f), V(0.002f, 0.004f, 0.004f), V(-0.01f, 0.03f, 0.00f), V(1.00f, 1.00f, 1.00f));
            case "austin":      // Texas: hot, warm, bright
                return G(1.03f, 1.00f, 1.06f, V(1.06f, 1.00f, 0.91f), V(0.008f, 0.006f, 0.002f), V(0.02f, 0.01f, -0.03f), V(1.00f, 0.99f, 0.96f));
            case "silverstone": // English summer sun: neutral, clean, green
                return G(1.00f, 1.00f, 1.03f, V(1.02f, 1.00f, 0.97f), V(0.002f, 0.003f, 0.004f), V(0.00f, 0.01f, 0.00f), V(1.00f, 1.00f, 1.00f));
            case "zandvoort":   // North Sea dunes: bright, airy, clean
                return G(1.04f, 1.04f, 1.03f, V(1.00f, 1.00f, 1.00f), V(0.004f, 0.006f, 0.010f), V(0.02f, 0.02f, 0.02f), V(1.00f, 1.00f, 1.01f));
            case "monza":       // Po valley summer: the warmth comes from the sun (Sky.SunAt), the grade only white-balances it.
                // The bake's sunlit asphalt is warm (linear R/B ~1.17), so the tint leans a little cool (green stays in
                // the middle: never magenta) and a mid-tone lift (gamma) brightens the road without clipping the white
                // car: asphalt ~143,140,140 neutral (was 139,129,126), white car ~245,245,248 (was 245,235,239 pink).
                // RR3 reference: docs/reference/rr3-colour-targets.md
                return G(1.36f, 0.85f, 1.02f, V(0.97f, 1.00f, 1.07f), V(0.004f, 0.004f, 0.004f), V(0.07f, 0.07f, 0.07f), V(1.00f, 1.00f, 1.00f));
            case "suzuka":      // crisp, vivid greens
                return G(1.00f, 1.06f, 1.05f, V(1.01f, 1.00f, 0.98f), V(0.002f, 0.004f, 0.008f), V(0.00f, 0.03f, 0.00f), V(1.00f, 1.00f, 1.00f));
            case "budapest":    // summer heat: warm
                return G(1.02f, 1.02f, 1.04f, V(1.05f, 1.00f, 0.92f), V(0.006f, 0.005f, 0.002f), V(0.02f, 0.01f, -0.02f), V(1.00f, 0.99f, 0.97f));
            case "catalunya":   // clear Mediterranean: warm, saturated, punchy
                return G(1.02f, 1.06f, 1.06f, V(1.04f, 1.00f, 0.94f), V(0.002f, 0.004f, 0.008f), V(0.02f, 0.01f, -0.01f), V(1.01f, 1.00f, 0.98f));
            case "spielberg":   // Alps: clean air, high contrast
                return G(1.02f, 1.06f, 1.07f, V(1.00f, 1.00f, 1.00f), V(0.000f, 0.003f, 0.008f), V(0.00f, 0.02f, 0.02f), V(1.00f, 1.00f, 1.01f));
            default:            // sunny, a touch warm
                return G(1.00f, 1.02f, 1.04f, V(1.03f, 1.00f, 0.96f), V(0.002f, 0.003f, 0.005f), V(0.00f, 0.00f, 0.00f), V(1.00f, 1.00f, 1.00f));
        }
    }

    static Vector3 Mul(Vector3 a, Vector3 b) => Vector3.Scale(a, b);

    // time of day on top of the circuit's grade
    public static GradeLook At(GradeLook g, TimeOfDay.Kind tod)
    {
        switch (tod)
        {
            case TimeOfDay.Kind.Morning: // cool, fresh, a little softer; opened up for the low sun (less of it on the ground)
                g.tint = Mul(g.tint, V(0.98f, 1.00f, 1.04f)); g.contrast -= 0.02f; g.exposure *= 1.06f; g.lift += V(0.000f, 0.002f, 0.006f);
                break;
            case TimeOfDay.Kind.Evening: // golden hour: warm, a touch more contrast. The orange comes from the low sun; the
                // tint keeps green in the middle (a red-up / green-down tint turned the blue sky fill in the shade purple and
                // the tyres rust) and the lift stays near-neutral (a blue lift made the shadows violet)
                g.tint = Mul(g.tint, V(1.05f, 1.00f, 0.88f)); g.contrast += 0.04f; g.exposure *= 1.10f;
                g.lift += V(0.002f, 0.002f, 0.003f); g.gain = Mul(g.gain, V(1.01f, 1.00f, 0.98f));
                break;
            case TimeOfDay.Kind.Night:   // floodlit: neutral-cool white light, deep blacks, punchy
                g.tint = Mul(g.tint, V(0.97f, 0.99f, 1.04f)); g.sat *= 0.95f; g.contrast += 0.06f; g.exposure *= 1.12f; // = the old 1.06 x 1.25 night exposure
                g.lift = V(0.000f, 0.003f, 0.010f); g.gamma += V(0.02f, 0.02f, 0.04f);
                break;
        }
        return g;
    }

    // weather: w 0 dry .. 1 wet. Overcast rain: cooler, flatter, a little less colour, greyer blacks
    public static GradeLook Wetted(GradeLook g, float w)
    {
        if (w <= 0f) return g;
        g.tint = Vector3.Lerp(g.tint, V(0.95f, 0.99f, 1.05f), 0.8f * w);
        g.sat *= 1f - 0.18f * w;
        g.contrast = Mathf.Lerp(g.contrast, 0.90f, 0.8f * w);
        g.exposure *= 1f + 0.10f * w;                 // the sun is mostly gone: open up a little
        g.lift = Vector3.Lerp(g.lift, V(0.014f, 0.016f, 0.020f), w);
        g.gamma = Vector3.Lerp(g.gamma, Vector3.zero, w);
        g.gain = Vector3.Lerp(g.gain, V(0.98f, 0.99f, 1.00f), w);
        return g;
    }

    public static void Push(GradeLook g)
    {
        Last = g;
        Shader.SetGlobalFloat(idExp, g.exposure * BaseExposure);
        Shader.SetGlobalFloat(idSat, g.sat * PunchSat);
        Shader.SetGlobalFloat(idCon, g.contrast * PunchCon);
        Shader.SetGlobalVector(idTint, new Vector4(g.tint.x, g.tint.y, g.tint.z, 1));
        Shader.SetGlobalVector(idLift, new Vector4(g.lift.x * LiftKeep, g.lift.y * LiftKeep, g.lift.z * LiftKeep, 0));
        Shader.SetGlobalVector(idGamma, new Vector4(g.gamma.x, g.gamma.y, g.gamma.z, 0));
        Shader.SetGlobalVector(idGain, new Vector4(g.gain.x, g.gain.y, g.gain.z, 1));
    }

    // the same maths on the CPU (for a solid-colour camera background, which no shader draws), sRGB colour in and out
    static float Tone(float x) { x = Mathf.Max(x, 0); float h = Mathf.Max(x - 0.8f, 0); return Mathf.Min(x, 0.8f + h / (1 + h * 5)); }
    public static Color Apply(Color srgb)
    {
        var g = Last; var c = srgb.linear; float e = g.exposure * BaseExposure;
        // perceptual space (square root of the tone-mapped linear value), saturation there, as Grade.cginc
        float r = Mathf.Sqrt(Tone(c.r * g.tint.x * e)), gg = Mathf.Sqrt(Tone(c.g * g.tint.y * e)), b = Mathf.Sqrt(Tone(c.b * g.tint.z * e));
        float l = 0.2126f * r + 0.7152f * gg + 0.0722f * b;
        float sa = g.sat * PunchSat; r = l + (r - l) * sa; gg = l + (gg - l) * sa; b = l + (b - l) * sa;
        float P(float v, float lift, float gam, float gain)
        {
            float p = Mathf.Clamp01((v - 0.45f) * g.contrast * PunchCon + 0.45f);
            p = p * gain + lift * LiftKeep * (1 - p); p = Mathf.Clamp01(p + gam * p * (1 - p));
            return p * p;
        }
        return new Color(P(r, g.lift.x, g.gamma.x, g.gain.x), P(gg, g.lift.y, g.gamma.y, g.gain.y), P(b, g.lift.z, g.gamma.z, g.gain.z), srgb.a).gamma;
    }
}
