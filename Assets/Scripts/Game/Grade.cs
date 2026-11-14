// Grade.cs — the colour grade of the picture (Assets/Resources/Grade.cginc does the work at the end of every world
// shader; this side picks the values and puts them in shader globals). One grade per circuit (its light and air:
// Monaco warm, Spa cool grey-green...), changed by the time of day and by the weather (wet = cooler, flatter).
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
    // keeps mid-grey where it was with the tone curve on (the curve alone would brighten the mids)
    public const float BaseExposure = 0.78f;

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
            case "monaco":      // warm Mediterranean: golden light, rich colour, deep blue shadows off the sea
                return G(1.02f, 1.12f, 1.08f, V(1.05f, 1.00f, 0.92f), V(0.000f, 0.006f, 0.016f), V(0.04f, 0.02f, -0.02f), V(1.02f, 1.00f, 0.97f));
            case "spa":         // Ardennes: cool, grey-green, soft
                return G(0.98f, 0.86f, 0.97f, V(0.95f, 0.99f, 1.02f), V(0.006f, 0.012f, 0.010f), V(-0.02f, 0.03f, 0.00f), V(0.97f, 1.00f, 1.00f));
            case "austin":      // Texas: hot, dusty, bleached highlights, warm lifted blacks
                return G(1.06f, 0.94f, 1.10f, V(1.07f, 1.00f, 0.88f), V(0.022f, 0.016f, 0.006f), V(0.03f, 0.01f, -0.04f), V(1.00f, 0.99f, 0.95f));
            case "silverstone": // English summer: neutral grey, gentle
                return G(1.00f, 0.92f, 1.00f, V(0.99f, 1.00f, 1.01f), V(0.006f, 0.007f, 0.008f), V(0.00f, 0.00f, 0.00f), V(1.00f, 1.00f, 1.00f));
            case "zandvoort":   // North Sea dunes: bright, airy, cool-clean
                return G(1.10f, 1.04f, 1.02f, V(0.98f, 1.00f, 1.03f), V(0.010f, 0.012f, 0.016f), V(0.02f, 0.02f, 0.03f), V(1.00f, 1.00f, 1.01f));
            case "monza":       // Po valley: warm haze, soft contrast
                return G(1.02f, 1.00f, 0.97f, V(1.04f, 1.00f, 0.93f), V(0.016f, 0.012f, 0.006f), V(0.02f, 0.01f, -0.02f), V(1.00f, 0.99f, 0.97f));
            case "suzuka":      // crisp, slightly cool, vivid greens
                return G(1.00f, 1.08f, 1.06f, V(0.99f, 1.00f, 1.00f), V(0.002f, 0.004f, 0.008f), V(0.00f, 0.03f, 0.00f), V(1.00f, 1.00f, 1.00f));
            case "budapest":    // summer heat: warm, hazy
                return G(1.04f, 0.98f, 1.03f, V(1.05f, 1.00f, 0.90f), V(0.014f, 0.010f, 0.004f), V(0.03f, 0.01f, -0.02f), V(1.00f, 0.99f, 0.96f));
            case "catalunya":   // clear Mediterranean: warm, saturated, punchy
                return G(1.03f, 1.10f, 1.08f, V(1.03f, 1.00f, 0.94f), V(0.002f, 0.004f, 0.010f), V(0.02f, 0.01f, -0.01f), V(1.01f, 1.00f, 0.98f));
            case "spielberg":   // Alps: clean air, cool, high contrast
                return G(1.03f, 1.10f, 1.10f, V(0.98f, 1.00f, 1.03f), V(0.000f, 0.003f, 0.008f), V(0.00f, 0.02f, 0.02f), V(1.00f, 1.00f, 1.01f));
            default:
                return G(1.00f, 1.04f, 1.04f, V(1.00f, 1.00f, 1.00f), V(0.004f, 0.005f, 0.007f), V(0.00f, 0.00f, 0.00f), V(1.00f, 1.00f, 1.00f));
        }
    }

    static Vector3 Mul(Vector3 a, Vector3 b) => Vector3.Scale(a, b);

    // time of day on top of the circuit's grade
    public static GradeLook At(GradeLook g, TimeOfDay.Kind tod)
    {
        switch (tod)
        {
            case TimeOfDay.Kind.Morning: // cool, fresh, a little softer
                g.tint = Mul(g.tint, V(0.98f, 1.00f, 1.04f)); g.contrast -= 0.02f; g.lift += V(0.000f, 0.002f, 0.006f);
                break;
            case TimeOfDay.Kind.Evening: // golden hour: warm, rich, a touch more contrast, blue-violet shadows
                g.tint = Mul(g.tint, V(1.10f, 0.98f, 0.84f)); g.sat += 0.05f; g.contrast += 0.04f; g.exposure *= 1.06f;
                g.lift += V(0.004f, 0.000f, 0.012f); g.gain = Mul(g.gain, V(1.02f, 0.99f, 0.95f));
                break;
            case TimeOfDay.Kind.Night:   // floodlit: neutral-cool white light, deep blacks, punchy
                g.tint = Mul(g.tint, V(0.97f, 0.99f, 1.04f)); g.sat *= 0.95f; g.contrast += 0.06f; g.exposure *= 1.25f;
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
        Shader.SetGlobalFloat(idSat, g.sat);
        Shader.SetGlobalFloat(idCon, g.contrast);
        Shader.SetGlobalVector(idTint, new Vector4(g.tint.x, g.tint.y, g.tint.z, 1));
        Shader.SetGlobalVector(idLift, new Vector4(g.lift.x, g.lift.y, g.lift.z, 0));
        Shader.SetGlobalVector(idGamma, new Vector4(g.gamma.x, g.gamma.y, g.gamma.z, 0));
        Shader.SetGlobalVector(idGain, new Vector4(g.gain.x, g.gain.y, g.gain.z, 1));
    }

    // the same maths on the CPU (for a solid-colour camera background, which no shader draws), sRGB colour in and out
    static float Tone(float x) { x = Mathf.Max(x, 0); return Mathf.Clamp01(x * (2.51f * x + 0.05f) / (x * (2.43f * x + 0.59f) + 0.14f)); }
    public static Color Apply(Color srgb)
    {
        var g = Last; var c = srgb.linear; float e = g.exposure * BaseExposure;
        float r = Tone(c.r * g.tint.x * e), gg = Tone(c.g * g.tint.y * e), b = Tone(c.b * g.tint.z * e);
        float l = 0.2126f * r + 0.7152f * gg + 0.0722f * b;
        r = Mathf.Max(0, l + (r - l) * g.sat); gg = Mathf.Max(0, l + (gg - l) * g.sat); b = Mathf.Max(0, l + (b - l) * g.sat);
        float P(float v, float lift, float gam, float gain)
        {
            float p = Mathf.Clamp01((Mathf.Sqrt(v) - 0.45f) * g.contrast + 0.45f);
            p = p * gain + lift * (1 - p); p = Mathf.Clamp01(p + gam * p * (1 - p));
            return p * p;
        }
        return new Color(P(r, g.lift.x, g.gamma.x, g.gain.x), P(gg, g.lift.y, g.gamma.y, g.gain.y), P(b, g.lift.z, g.gamma.z, g.gain.z), srgb.a).gamma;
    }
}
