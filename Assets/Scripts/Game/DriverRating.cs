// DriverRating.cs — GT Sport / GT7-style Driver Rating (DR): grades E, D, C, B, A, A+ from DR points.
//  * Points and grades (as GT): E < 2,000, D 2,000, C 4,000, B 10,000, A 30,000, A+ 50,000 (max 99,999). A new
//    driver starts at 1,000 (E). Internally the points map to a continuous "level" L, linear inside each grade
//    (E 0-1, D 1-2, C 2-3, B 3-4, A 4-5, A+ 5 + every 20,000 points), so a grade takes about as many races at every
//    step even though the point spans grow.
//  * Rivals have a DR too: their level is set by the rival level (GameManager.DiffScale: Easy ~D, Medium ~C, Hard ~B,
//    Real ~A) and their own skill (pace 70 %, consistency 30 %; the stars about +0.6 grade, the backmarkers about -0.75).
//  * After every race (MenuProfile.RecordRace) the player's level moves like a multi-player Elo: against every rival
//    j, expected score E_j = 1 / (1 + 10^-(Lp - Lj ± g) / Scale), g = GridEdge / sqrt(laps) (+ when the player started
//    ahead of j on the grid, - when behind: starting at the back lowers what is expected), actual S_j = 1 when the
//    player finished ahead of j, else 0. dL = K * lenF * fieldF * sum(S_j - E_j) / n, clamped to -MaxLoss..+MaxGain.
//    Beating a higher-rated rival (E small) gains the most; losing to a lower-rated one costs the most.
//    lenF = 0.6 (1 lap) .. 1 (5+ laps); fieldF = 0.5 (1-2 rivals) .. 1 (10+ rivals).
//  * A race quit or restarted after lights out (a DNF; GameManagerProgress.ProgressAbandon) counts as last behind
//    everyone, minus QuitCost more. Free practice, qualifying, time trials (no rivals) and the playground give nothing.
//  * Tuning (an offline simulation of this formula): a good player on Hard (P2-P4 from the back, 5 laps, 10 rivals) reaches B in ~9-12
//    races and settles high B / low A; dominating Medium tops out in B; A+ needs a player who keeps winning against
//    the Real-pace field.
// Nothing here allocates (strings: NextLabel / Grades built once; the rest is arithmetic).
using UnityEngine;

public static class DriverRating
{
    public const int Start = 1000, Max = 99999;
    public static readonly string[] Grades = { "E", "D", "C", "B", "A", "A+" };
    public static readonly int[] Floor = { 0, 2000, 4000, 10000, 30000, 50000 };
    const float TopSpan = 20000f;   // points per level above A+

    // the Elo: points per race, the curve, the grid's weight, a quit's extra cost, per-race limits (all in levels)
    public const float K = 0.4f, Scale = 1.0f, GridEdge = 0.4f, QuitCost = 0.08f, MaxGain = 0.6f, MaxLoss = 0.45f;

    // "Next  A  ·  30,000" under the grade line (built once); the top grade: "Top grade"
    public static readonly string[] NextLabel = MakeNext();
    static string[] MakeNext()
    {
        var a = new string[Grades.Length];
        for (int g = 0; g < a.Length; g++) a[g] = g + 1 < Grades.Length ? "Next  " + Grades[g + 1] + "  ·  " + Pts(Floor[g + 1]) : "Top grade";
        return a;
    }

    public static int GradeOf(float pts) { int g = 0; while (g < 5 && pts >= Floor[g + 1]) g++; return g; }
    // share of the way through the grade to the next one (1 at A+)
    public static float FracOf(float pts) { int g = GradeOf(pts); return g >= 5 ? 1f : Mathf.Clamp01((pts - Floor[g]) / (Floor[g + 1] - Floor[g])); }
    // E D quiet ink, C B A ink, A+ the red accent
    public static Color GradeColor(int g) => g >= 5 ? UiKit.Red : g >= 2 ? UiKit.Ink : UiKit.InkSub;

    public static float LevelOf(float pts)
    {
        if (pts >= Floor[5]) return 5f + (pts - Floor[5]) / TopSpan;
        int g = GradeOf(pts);
        return g + Mathf.Clamp01((pts - Floor[g]) / (Floor[g + 1] - Floor[g]));
    }
    public static float PointsOf(float lv)
    {
        lv = Mathf.Max(0f, lv);
        if (lv >= 5f) return Floor[5] + (lv - 5f) * TopSpan;
        int g = Mathf.Min(4, (int)lv);
        return Floor[g] + (Floor[g + 1] - Floor[g]) * (lv - g);
    }

    // a rival's DR: the rival level (GameManager.DiffScale 0.82 Easy .. 1.0 Real) and his own skill
    public static int RivalPoints(double diffScale, double pace, double consistency)
    {
        float skill = 0.7f * (float)pace + 0.3f * (float)consistency;
        float lv = 0.8f + ((float)diffScale - 0.82f) / 0.18f * 3.2f + (skill - 0.7f) * 2f;
        return Mathf.RoundToInt(PointsOf(Mathf.Clamp(lv, 0.05f, 6f)));
    }

    // the player's expected score against one rival (0..1)
    public static float Expect(float playerLevel, int rivalPts, bool startedAhead, int laps)
    {
        float g = GridEdge / Mathf.Sqrt(Mathf.Max(1, laps));
        float d = playerLevel - LevelOf(rivalPts) + (startedAhead ? g : -g);
        return 1f / (1f + Mathf.Pow(10f, -d / Scale));
    }

    // the level change from sum(S - E) over n rivals
    public static float Delta(float sum, int n, int laps, bool quit)
    {
        if (n < 1) return 0f;
        float lenF = 0.6f + 0.4f * Mathf.Clamp01((laps - 1) / 4f), fieldF = Mathf.Clamp(0.4f + 0.06f * n, 0.5f, 1f);
        float d = K * lenF * fieldF * sum / n;
        if (quit) d -= QuitCost;
        return Mathf.Clamp(d, -MaxLoss, MaxGain);
    }

    // a finished race: rows in finishing order (MenuUI.Row: player, dr = DR points, grid = slot). false = not rated
    // (no rivals, or rows without DR: a session that is not a race). beaten / avgPts for the results line.
    public static bool Race(int playerPts, MenuUI.Row[] rows, int laps, out float dLevel, out int beaten, out int avgPts)
    {
        dLevel = 0f; beaten = 0; avgPts = 0;
        if (rows == null || rows.Length < 2) return false;
        int me = -1; double avg = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].player) { me = i; continue; }
            if (rows[i].dr <= 0) return false;
            avg += LevelOf(rows[i].dr);
        }
        if (me < 0) return false;
        int n = rows.Length - 1;
        float lp = LevelOf(playerPts), sum = 0f;
        for (int i = 0; i < rows.Length; i++)
        {
            if (i == me) continue;
            float s = i > me ? 1f : 0f;
            sum += s - Expect(lp, rows[i].dr, rows[me].grid < rows[i].grid, laps);
        }
        beaten = n - me;
        avgPts = Mathf.RoundToInt(PointsOf((float)(avg / n)));
        dLevel = Delta(sum, n, laps, false);
        return true;
    }

    // the new points after a level change
    public static int Apply(int pts, float dLevel) => Mathf.Clamp(Mathf.RoundToInt(PointsOf(LevelOf(pts) + dLevel)), 0, Max);

    // "12,450"
    public static string Pts(int p) => p.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
