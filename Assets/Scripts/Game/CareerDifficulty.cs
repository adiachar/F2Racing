// CareerDifficulty.cs — GRID Autosport-style difficulty presets (Settings > ASSISTS "Difficulty", the career hub and the
// career's contract page). A preset sets the rival level, damage, the racing line, the braking / steering assists, ABS,
// traction control, whether a career race may be restarted, and the XP multiplier. Custom keeps whatever the player set
// (any change to one of those settings turns a preset into Custom). Docs/CAREER.md has the table.
//  * Quick race / weekend: the preset's damage. Career rounds: damage is always on (Visual on Easy / Medium, Full from
//    Hard; Custom: the player's setting, at least Visual) — GameManager.DamageNow.
//  * Career restarts: never in qualifying; in a race on Easy / Medium (Custom: "Career restarts"), not from Hard.
//  * XP: Easy x0.8, Medium x1, Hard x1.25, Pro x1.5, Master x2; Custom from the rival level and the aids (0.6-2).
// Saved in PlayerPrefs "difficulty" (default Custom: an existing player's settings are left as they are) and
// "diffRestarts". GameManager applies a preset when Version changes (GameManagerSettings.SyncDifficulty).
using UnityEngine;

public static class Difficulty
{
    public const int Easy = 0, Medium = 1, Hard = 2, Pro = 3, Master = 4, Custom = 5;
    public static readonly string[] Names = { "Easy", "Medium", "Hard", "Pro", "Master", "Custom" };

    // per preset, Easy..Master
    public static readonly int[] Ai = { 0, 1, 2, 3, 3 };              // GameManager.DiffNames: Easy, Medium, Hard, Real
    public static readonly int[] Damage = { 0, 1, 2, 2, 2 };          // quick race / weekend: 0 off, 1 visual only, 2 full
    public static readonly int[] Line = { 2, 2, 1, 0, 0 };            // RacingLineView: 0 off, 1 braking only, 2 full
    public static readonly int[] BrakeAssist = { 2, 1, 0, 0, 0 };     // 0 off, 1 low, 2 high
    public static readonly int[] SteerAssist = { 2, 1, 0, 0, 0 };
    public static readonly bool[] Abs = { true, true, true, false, false };
    public static readonly int[] Tc = { 2, 1, 1, 1, 0 };              // TcMode: 0 off, 1 low, 2 high
    public static readonly bool[] Restarts = { true, true, false, false, false };
    public static readonly float[] Xp = { 0.8f, 1f, 1.25f, 1.5f, 2f };

    // one line per preset for the contract page and the hub (built once)
    public static readonly string[] Summary =
    {
        "Slow rivals  ·  all assists  ·  full racing line  ·  restarts  ·  XP ×0.8",
        "Medium rivals  ·  light assists  ·  full line  ·  restarts  ·  XP ×1",
        "Fast rivals  ·  ABS, low TC  ·  braking line  ·  no restarts  ·  full damage  ·  XP ×1.25",
        "Real-pace rivals  ·  low TC only  ·  no line  ·  no restarts  ·  full damage  ·  XP ×1.5",
        "Real-pace rivals  ·  no aids at all  ·  no restarts  ·  full damage  ·  XP ×2",
        "Your own settings  ·  XP from the rival level and the aids",
    };

    static int cur = -1;
    public static int Version;   // +1 at every change (GameManager re-applies the preset)
    public static int Current
    {
        get { if (cur < 0) cur = Mathf.Clamp(PlayerPrefs.GetInt("difficulty", Custom), 0, Custom); return cur; }
    }
    public static void Set(int d)
    {
        d = Mathf.Clamp(d, 0, Custom);
        if (d == Current) return;
        cur = d; PlayerPrefs.SetInt("difficulty", d); Version++;
    }
    public static string Name => Names[Current];

    public static bool CustomRestarts
    {
        get => PlayerPrefs.GetInt("diffRestarts", 1) == 1;
        set => PlayerPrefs.SetInt("diffRestarts", value ? 1 : 0);
    }
    // a career race may be restarted (qualifying never)
    public static bool RestartsAllowed => Current == Custom ? CustomRestarts : Restarts[Current];

    // the damage of a career round: always on
    public static int CareerDamage(int setting) => Current == Custom ? Mathf.Max(1, setting) : Current <= Medium ? 1 : 2;

    // do the settings still match preset d?
    public static bool Matches(int d, int ai, int damage, int line, int brake, int steer, bool abs, int tc)
    {
        if (d < 0 || d >= Custom) return true;
        return ai == Ai[d] && damage == Damage[d] && line == Line[d] && brake == BrakeAssist[d] && steer == SteerAssist[d] && abs == Abs[d] && tc == Tc[d];
    }

    // the menu's rival level changed (MenuPlay): a preset with another level becomes Custom
    public static void RivalLevelChanged(int ai) { int d = Current; if (d < Custom && Ai[d] != ai) Set(Custom); }

    // XP multiplier: the preset's, or for Custom from the rival level and the aids (rounded to 0.05, 0.6-2)
    public static float XpMult(int ai, int line, int brake, int steer, bool abs, int tc)
    {
        int d = Current;
        if (d < Custom) return Xp[d];
        float m = Xp[Mathf.Clamp(ai, 0, 3)];
        m += line == 0 ? 0.15f : line == 1 ? 0.05f : 0f;
        m += abs ? 0f : 0.1f;
        m += tc == 0 ? 0.1f : tc == 2 ? -0.05f : 0f;
        m -= 0.05f * brake + 0.05f * steer;
        return Mathf.Clamp(Mathf.Round(m * 20f) / 20f, 0.6f, 2f);
    }
    public static float LastXp = 1f;   // the multiplier GameManager worked out last (the hub shows it for Custom)

    static float labelFor = -1f; static string label;
    // "XP ×1.25" (cached for the last value)
    public static string XpLabel(float m)
    {
        if (label == null || !Mathf.Approximately(m, labelFor)) { labelFor = m; label = "XP ×" + m.ToString("0.##"); }
        return label;
    }
}
