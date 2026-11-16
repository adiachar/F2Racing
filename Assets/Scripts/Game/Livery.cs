// Livery.cs — F1-style liveries: a deep primary body colour, a secondary colour on large areas (engine cover, sidepods,
// nose cone or a centre stripe, chosen by `style`), a thin accent line along their edge, and the wings in the secondary.
// CarModel.SetLivery puts one on a car; the split itself is done in CarPaint.shader from the car's own shape (no
// textures). Colours are sRGB, deliberately deep and saturated (mid-tones wash out under the sun, the sky reflection
// of the clear coat and the grade's tone curve), and black / carbon is used as a real colour. All liveries and team
// names here are invented.
using UnityEngine;

public struct Livery
{
    public string name; public Color primary, secondary, accent; public int style; // style 0..3: which areas the secondary takes (CarPaint LiverySplit)
    public Livery(string name, Color primary, Color secondary, Color accent, int style = 0) { this.name = name; this.primary = primary; this.secondary = secondary; this.accent = accent; this.style = style; }

    static Color C(float r, float g, float b) => new Color(r, g, b);

    // the player's choice (Settings > Livery, saved as "livery"); 0 is the car's original red and white
    public static readonly Livery[] Player =
    {
        new Livery("Scarlet Dart",  C(0.70f, 0.02f, 0.04f), C(0.94f, 0.94f, 0.95f), C(0.03f, 0.03f, 0.04f), 0),
        new Livery("Teal Comet",    C(0.00f, 0.42f, 0.46f), C(0.03f, 0.04f, 0.05f), C(1.00f, 0.42f, 0.04f), 1),
        new Livery("Midnight Gold", C(0.025f, 0.025f, 0.03f), C(0.82f, 0.58f, 0.10f), C(0.95f, 0.95f, 0.95f), 3),
        new Livery("Glacier",       C(0.04f, 0.45f, 0.95f), C(0.97f, 0.97f, 0.98f), C(0.02f, 0.06f, 0.30f), 2),
        new Livery("Volt",          C(0.36f, 0.75f, 0.02f), C(0.03f, 0.03f, 0.04f), C(0.95f, 0.95f, 0.95f), 1),
        new Livery("Dusk Violet",   C(0.24f, 0.05f, 0.45f), C(1.00f, 0.45f, 0.05f), C(0.03f, 0.03f, 0.04f), 2),
    };
    public static int PlayerIndex { get => Mathf.Clamp(PlayerPrefs.GetInt("livery", 0), 0, Player.Length - 1); set => PlayerPrefs.SetInt("livery", value); }

    // rival teams, in the order the teams first appear in the grid (AiDriver.DriverProfile.Grid, Season.Teams):
    // Argent, Midnight Comet, Vermilion, Solstice, Blue Works, Emerald, Glacier, Quadrant, Meridian, Comet Academy, Harbour
    public static readonly Livery[] Teams =
    {
        new Livery("Argent",         C(0.035f, 0.04f, 0.05f), C(0.62f, 0.65f, 0.68f), C(0.00f, 0.68f, 0.62f), 1), // carbon black, silver nose and cover, teal line
        new Livery("Midnight Comet", C(0.02f, 0.04f, 0.17f),  C(0.78f, 0.04f, 0.09f), C(1.00f, 0.78f, 0.04f), 1), // dark navy, red cover and nose, yellow line
        new Livery("Vermilion",      C(0.66f, 0.02f, 0.04f),  C(0.94f, 0.94f, 0.95f), C(0.03f, 0.03f, 0.04f), 0), // deep scarlet, white cover and pods, black line
        new Livery("Solstice",       C(1.00f, 0.36f, 0.00f),  C(0.02f, 0.04f, 0.12f), C(0.05f, 0.30f, 0.90f), 2), // papaya, navy-black pods, blue line
        new Livery("Blue Works",     C(0.02f, 0.10f, 0.50f),  C(0.94f, 0.94f, 0.95f), C(0.82f, 0.03f, 0.08f), 3), // royal blue, white centre stripe, red edges
        new Livery("Emerald",        C(0.00f, 0.24f, 0.14f),  C(0.55f, 0.92f, 0.02f), C(0.02f, 0.02f, 0.03f), 0), // racing green, lime cover and pods
        new Livery("Glacier",        C(0.03f, 0.50f, 0.98f),  C(0.95f, 0.08f, 0.50f), C(0.97f, 0.97f, 0.98f), 2), // bright blue, hot pink pods
        new Livery("Quadrant",       C(0.03f, 0.03f, 0.035f), C(0.80f, 0.56f, 0.08f), C(1.00f, 0.80f, 0.30f), 0), // black and gold
        new Livery("Meridian",       C(0.90f, 0.92f, 0.95f),  C(0.02f, 0.08f, 0.42f), C(0.80f, 0.03f, 0.08f), 1), // white, deep blue cover and nose
        new Livery("Comet Academy",  C(0.00f, 0.46f, 0.48f),  C(0.03f, 0.04f, 0.05f), C(0.72f, 0.75f, 0.78f), 0), // teal, black cover and pods, silver line
        new Livery("Harbour",        C(0.26f, 0.04f, 0.42f),  C(0.98f, 0.88f, 0.02f), C(0.03f, 0.03f, 0.04f), 2), // violet, yellow pods, black line
    };

    // the team's main colour as shown in the UI (mini-map, results): the livery's primary, but for the white team its secondary
    public static Color TeamColour(int teamIndex)
    {
        var l = Teams[Mathf.Clamp(teamIndex, 0, Teams.Length - 1)];
        float mx = Mathf.Max(l.primary.r, Mathf.Max(l.primary.g, l.primary.b)), mn = Mathf.Min(l.primary.r, Mathf.Min(l.primary.g, l.primary.b));
        return mx > 0.5f && (mx - mn) < 0.15f * mx ? l.secondary : l.primary;
    }

    // livery for a rival: teamIndex = position of the team in the grid's list of teams. `primary` (the grid's team colour)
    // is only used for a team beyond the table: a deepened, more saturated version of it with a black secondary.
    public static Livery ForTeam(int teamIndex, Color primary)
    {
        if (teamIndex >= 0 && teamIndex < Teams.Length) return Teams[teamIndex];
        Color.RGBToHSV(primary, out float h, out float sat, out float v);
        var deep = Color.HSVToRGB(h, Mathf.Max(sat, 0.85f), Mathf.Min(v, 0.7f));
        return new Livery("Team " + (teamIndex + 1), deep, C(0.04f, 0.04f, 0.05f), C(0.94f, 0.94f, 0.95f), teamIndex & 3);
    }
}
