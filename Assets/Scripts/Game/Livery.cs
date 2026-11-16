// Livery.cs — two-tone car liveries: a primary body colour, a secondary colour (engine cover, sidepod tops, nose tip
// and wings) and a thin accent line between them. CarModel.SetLivery puts one on a car; the split itself is done in
// CarPaint.shader from the car's own shape (no textures). All liveries and team names here are invented.
using UnityEngine;

public struct Livery
{
    public string name; public Color primary, secondary, accent;
    public Livery(string name, Color primary, Color secondary, Color accent) { this.name = name; this.primary = primary; this.secondary = secondary; this.accent = accent; }

    static Color C(float r, float g, float b) => new Color(r, g, b);

    // the player's choice (Settings > Livery, saved as "livery"); 0 is the car's original red and white
    public static readonly Livery[] Player =
    {
        new Livery("Scarlet Dart",  C(0.78f, 0.05f, 0.07f), C(0.93f, 0.93f, 0.94f), C(0.10f, 0.10f, 0.11f)),
        new Livery("Teal Comet",    C(0.06f, 0.55f, 0.62f), C(0.07f, 0.08f, 0.10f), C(1.00f, 0.45f, 0.08f)),
        new Livery("Midnight Gold", C(0.05f, 0.05f, 0.07f), C(0.80f, 0.62f, 0.20f), C(0.95f, 0.95f, 0.95f)),
        new Livery("Glacier",       C(0.92f, 0.94f, 0.96f), C(0.30f, 0.65f, 0.95f), C(0.05f, 0.10f, 0.30f)),
        new Livery("Volt",          C(0.55f, 0.85f, 0.10f), C(0.06f, 0.06f, 0.07f), C(0.95f, 0.95f, 0.95f)),
        new Livery("Dusk Violet",   C(0.35f, 0.12f, 0.55f), C(1.00f, 0.50f, 0.15f), C(1.00f, 0.85f, 0.30f)),
    };
    public static int PlayerIndex { get => Mathf.Clamp(PlayerPrefs.GetInt("livery", 0), 0, Player.Length - 1); set => PlayerPrefs.SetInt("livery", value); }

    // rival teams: the primary is the team's own colour (so the mini-map and results match the car); the secondary
    // and accent come from this list, one entry per team in the order the teams first appear in the grid
    static readonly Color[,] TeamTrim =
    {
        { C(0.05f, 0.05f, 0.06f), C(0.00f, 0.80f, 0.70f) },
        { C(0.85f, 0.10f, 0.15f), C(1.00f, 0.80f, 0.10f) },
        { C(0.08f, 0.08f, 0.09f), C(1.00f, 0.85f, 0.10f) },
        { C(0.05f, 0.10f, 0.25f), C(0.40f, 0.75f, 1.00f) },
        { C(0.93f, 0.93f, 0.94f), C(0.90f, 0.10f, 0.12f) },
        { C(0.60f, 0.90f, 0.10f), C(0.95f, 0.95f, 0.95f) },
        { C(0.95f, 0.40f, 0.70f), C(0.95f, 0.95f, 0.95f) },
        { C(0.50f, 0.05f, 0.08f), C(0.95f, 0.95f, 0.95f) },
        { C(0.06f, 0.06f, 0.07f), C(0.00f, 0.75f, 0.70f) },
        { C(0.75f, 0.78f, 0.80f), C(1.00f, 0.80f, 0.10f) },
        { C(0.06f, 0.06f, 0.07f), C(0.90f, 0.12f, 0.10f) },
        { C(0.95f, 0.95f, 0.95f), C(0.10f, 0.10f, 0.12f) },
    };

    // livery for a rival: teamIndex = position of the team in the grid's list of teams, primary = team colour
    public static Livery ForTeam(int teamIndex, Color primary)
    {
        int k = ((teamIndex % TeamTrim.GetLength(0)) + TeamTrim.GetLength(0)) % TeamTrim.GetLength(0);
        Color sec = TeamTrim[k, 0], acc = TeamTrim[k, 1];
        // a white / silver / pale-grey team colour would make the rival look like the (white) player car: the car is
        // painted in the team's strong trim colour instead, with the pale colour as its secondary
        float mx = Mathf.Max(primary.r, Mathf.Max(primary.g, primary.b)), mn = Mathf.Min(primary.r, Mathf.Min(primary.g, primary.b));
        if (mx > 0.5f && (mx - mn) < 0.15f * mx)
        {
            // each pale team gets its OWN strong colour (taking the trim accent for all of them made every one teal)
            var strong = acc;
            switch (teamIndex) { case 0: strong = C(0.15f, 0.16f, 0.19f); acc = C(0.00f, 0.80f, 0.70f); break;   // silver team: graphite, silver, teal line
                                 case 7: strong = C(0.42f, 0.26f, 0.62f); acc = C(0.95f, 0.95f, 0.95f); break;   // grey team: violet
                                 case 8: strong = C(0.88f, 0.18f, 0.48f); break; }                                // white team: magenta
            float am = Mathf.Max(strong.r, Mathf.Max(strong.g, strong.b)), an = Mathf.Min(strong.r, Mathf.Min(strong.g, strong.b));
            if (teamIndex != 0 && teamIndex != 7 && teamIndex != 8 && am - an < 0.3f * am) strong = sec; // trim is pale too: fall back to the dark one
            return new Livery("Team " + (teamIndex + 1), strong, primary, (teamIndex == 0 || teamIndex == 7) ? acc : sec);
        }
        return new Livery("Team " + (teamIndex + 1), primary, sec, acc);
    }
}
