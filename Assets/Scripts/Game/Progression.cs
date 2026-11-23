// Progression.cs — Phase 2 driver progression (offline, kept in PlayerPrefs by MenuProfile, MenuDriver.cs):
//  * Safety rating (SR) 0-100, grade S >= 90, A >= 75, B >= 60, C >= 45, D >= 30, else E. A new driver starts at 70.
//    It is an exponential moving average over the race distance: every clean sector pulls the rating a small share
//    of the way to 100 (SectorPull, about 2 % of the gap per clean lap) and a clean race a little more (CleanRacePull);
//    incidents take fixed points off (Pen*). So recent driving weighs most, a driver earns a bad race back with clean
//    ones, and the climb gets slower near the top. Offline races count with OfflineWeight; online (later) with more.
//    Incidents (GameManagerProgress.cs, from the game's own state; physics untouched): off track with all four
//    wheels (more when time was gained), a barrier hit, a contact the player CAUSED (rear-ending, moving across into
//    a car alongside), ramming (a hard or repeated contact). Being hit costs the victim nothing.
//  * XP per race: a finish, laps, places beaten, win / podium, overtakes, clean laps, pole, fastest lap. Level curve:
//    level L -> L + 1 needs 250 + 70 (L - 1)^1.15 XP (rounded to 10): 250, 320, 410 ... level 2 after one or two
//    races (~400 XP each), ~1100 XP per level at 10, ~3000 at 25; capped at level 100 (~650k XP in all).
//    The race's XP is multiplied by the difficulty (CareerDifficulty.cs: Easy x0.8 ... Master x2; WithDifficulty).
// RaceProgress: one race's live tally (GameManagerProgress.cs). RaceOutcome: what the results page shows (strings
// built once). Nothing here allocates per frame.
using UnityEngine;

public static class Progression
{
    public const int MaxLevel = 100;
    public const float SrStart = 70f;
    public const float OfflineWeight = 1f, OnlineWeight = 1.5f;   // online races (future) move the rating more

    // ---- safety rating (offline points; x weight)
    public const float SectorPull = 0.0067f;      // a clean sector: SR += k (100 - SR)  (~2 % of the gap a lap)
    public const float CleanRacePull = 0.03f;     // the whole race clean, at the flag
    public const float PenOffTrack = 0.7f;        // all four wheels off
    public const float PenCut = 2f;               // ... and time gained (a cut chicane / corner)
    public const float PenBarrier = 1f;           // a barrier hit (not right after being hit by a car)
    public const float PenLight = 1.5f;           // a light contact the player caused (closing 0.8-2.5 m/s)
    public const float PenContact = 4f;           // a contact the player caused (2.5-6 m/s)
    public const float PenRam = 8f;               // ramming: 6+ m/s, or a repeated caused contact

    // ---- contact judging (shared with the race penalties)
    public const double TouchMin = 0.8, ContactMin = 2.5, RamMin = 6.0;            // closing speed, m/s
    public const double PenaltyMin5 = 3.5, PenaltyMin10 = 7.0;                      // race penalties: clear cases only

    // ---- XP
    public const int XpFinish = 100, XpPerLap = 20, XpPerPlace = 15, XpWin = 150, XpPodium = 60;
    public const int XpOvertake = 25, MaxOvertakes = 10, XpCleanLap = 15, XpPole = 80, XpFastest = 50;

    // XP scaled by the difficulty's multiplier (harder = more XP)
    public static int WithDifficulty(int xp, float mult) => Mathf.RoundToInt(xp * Mathf.Clamp(mult, 0.5f, 3f));

    public static readonly string[] Grades = { "S", "A", "B", "C", "D", "E" };
    public static int GradeOf(float sr) => sr >= 90f ? 0 : sr >= 75f ? 1 : sr >= 60f ? 2 : sr >= 45f ? 3 : sr >= 30f ? 4 : 5;
    // S A B green (good), C neutral, D E amber (warning)
    public static Color GradeColor(int g) => g <= 2 ? UiKit.Good : g == 3 ? UiKit.TextCol : UiKit.Warn;

    // ---- level curve: cum[L] = total XP at the start of level L (cum[1] = 0)
    static int[] cum;
    public static readonly string[] LevelNames = MakeLevelNames();
    static string[] MakeLevelNames() { var a = new string[MaxLevel + 1]; for (int i = 0; i <= MaxLevel; i++) a[i] = "Level " + i; return a; }
    static void Build()
    {
        if (cum != null) return;
        cum = new int[MaxLevel + 2];
        for (int l = 1; l <= MaxLevel; l++) cum[l + 1] = cum[l] + (l < MaxLevel ? Need(l) : 0);
    }
    // XP from level l to l + 1
    public static int Need(int l) => l >= MaxLevel ? 0 : Mathf.RoundToInt((250f + 70f * Mathf.Pow(l - 1, 1.15f)) / 10f) * 10;
    public static int LevelOf(int xp)
    {
        Build();
        int l = 1; while (l < MaxLevel && xp >= cum[l + 1]) l++;
        return l;
    }
    public static int StartOf(int level) { Build(); return cum[Mathf.Clamp(level, 1, MaxLevel)]; }
    // share of the way through the current level (1 at the cap)
    public static float FracOf(int xp)
    {
        int l = LevelOf(xp); if (l >= MaxLevel) return 1f;
        return Mathf.Clamp01((xp - cum[l]) / (float)Need(l));
    }
    public static float FracOf(float xp)
    {
        int l = LevelOf((int)xp); if (l >= MaxLevel) return 1f;
        return Mathf.Clamp01((xp - cum[l]) / Need(l));
    }

    // "+3", "-0.6", "0" (an en dash for minus: the UI font has it)
    public static string Signed(float d)
    {
        float a = Mathf.Abs(d);
        if (a < 0.05f) return "0";
        string s = a < 0.95f ? a.ToString("0.0") : Mathf.RoundToInt(a).ToString();
        return (d > 0 ? "+" : "–") + s;
    }
}

// one race's live tally (GameManagerProgress.cs): the working safety rating and the race's counters
public sealed class RaceProgress
{
    public readonly float weight, srStart;
    public float sr;
    public int cleanSectors, cleanLaps, laps, overtakes, offTracks, cuts, barriers, contacts, rams, hitsTaken;
    public float gainSectors, gainClean, lossOff, lossCut, lossBarrier, lossContact, lossRam;
    public bool clean = true, fromPole;

    public RaceProgress(float sr, float weight) { srStart = this.sr = Mathf.Clamp(sr, 0f, 100f); this.weight = weight; }
    // an incident: fixed points off; returns the points taken
    public float Penalise(float pts) { float d = Mathf.Min(sr, pts * weight); sr -= d; clean = false; return d; }
    // clean driving: a share of the way to 100
    public float Pull(float k) { float d = Mathf.Clamp01(k * weight) * (100f - sr); sr += d; return d; }
    public float Losses => lossOff + lossCut + lossBarrier + lossContact + lossRam;
}

// what the results page shows of one race (MenuProfile.RecordRace): built once
public sealed class RaceOutcome
{
    public int xpGain, xpBefore, xpAfter, levelBefore, levelAfter;
    public float srBefore, srAfter; public int gradeBefore, gradeAfter, srDelta;
    public string xpGainStr, xpDetail, xpAfterStr, srBeforeStr, srAfterStr, srDeltaStr;
    public readonly string[] reason = new string[3], reasonVal = new string[3];
    public readonly bool[] reasonGood = new bool[3];
    public int reasons;
    public bool LevelUp => levelAfter > levelBefore;
}
