// CareerSession.cs — the career's hooks in the game loop (part of GameManager; the career itself: Season.cs, Career.cs,
// Story.cs, SeasonUI.cs) and the difficulty presets applied to the settings (CareerDifficulty.cs).
//  * PrepareSession (Qualifying.cs) -> CareerSession: the tier's car class (the player's own class comes back for the
//    next non-career session unless he changed it), the difficulty synced, the XP multiplier, and the round's progress
//    saved: qualifying started (no retries: no restart button / restart setting; leaving = no time), the race started
//    (restarts only where the difficulty allows them: Easy / Medium, or Custom with "Career restarts").
//  * CareerGrid: a race of a round starts from the saved grid (qualifying, or from the back without it).
//  * DamageNow: a career round always has damage (Visual on Easy / Medium, Full from Hard); otherwise the setting.
//  * Difficulty: SyncDifficulty applies a preset when it changed (hub, contract page, menu, settings); Tweaked turns a
//    preset into Custom when one of its settings is changed by hand.
// Session-start only: nothing here runs per frame in a race (RestartOk / DamageNow are plain reads).
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    bool careerRestarts = true;           // the session running may be restarted (a career: set by CareerSession)
    int diffSeen = -1;                    // Difficulty.Version applied last

    // the restart button and the Restart setting are offered
    bool RestartOk => !InSeasonRound || careerRestarts;
    // damage for this session: always on in a career round
    int DamageNow => InSeasonRound ? Difficulty.CareerDamage(damageMode) : damageMode;
    // settings rows that would restart or change a career round are locked during one
    string CareerLock() => InRace && InSeasonRound ? "Set by the career during a round." : null;

    // PrepareSession, after the session's mode and circuit are known
    void CareerSession()
    {
        SyncDifficulty();
        bool career = InSeasonRound;
        if (career)
        {
            int tierCls = Season.TierClass;
            if (!PlayerPrefs.HasKey("classBeforeCareer")) PlayerPrefs.SetInt("classBeforeCareer", classIdx);
            PlayerPrefs.SetInt("careerClass", tierCls);
            classIdx = tierCls;
        }
        else if (PlayerPrefs.HasKey("classBeforeCareer"))
        {
            // back to the player's own car, unless he picked another one since
            if (classIdx == PlayerPrefs.GetInt("careerClass", -1)) classIdx = Mathf.Clamp(PlayerPrefs.GetInt("classBeforeCareer", classIdx), 0, CarConfig.ClassNames.Length - 1);
            PlayerPrefs.DeleteKey("classBeforeCareer"); PlayerPrefs.DeleteKey("careerClass");
        }
        careerRestarts = true;
        if (!career || playground) return;
        if (qualiSession) { careerRestarts = false; Season.QualiStarted(); }
        else if (raceLaps > 0) { careerRestarts = Difficulty.RestartsAllowed; Season.RaceStarted(careerRestarts); }
    }

    // PrepareSession, a race of a career round (not from the qualifying results): the saved grid
    void CareerGrid()
    {
        var g = Season.Data.qualiGrid;
        if (g == null || g.Length < 2) { Season.GridFromBack(); g = Season.Data.qualiGrid; }
        raceField = new System.Collections.Generic.List<DriverProfile>(g.Length); gridSlotOverride = g.Length - 1;
        for (int i = 0; i < g.Length; i++)
        {
            if (g[i] == 0) gridSlotOverride = i;
            else if (g[i] > 0 && g[i] < Season.RosterCount) raceField.Add(Season.Profile(g[i]));
        }
    }

    // ---------------------------------------------------------------- difficulty presets
    void SyncDifficulty()
    {
        if (diffSeen != Difficulty.Version)
        {
            diffSeen = Difficulty.Version;
            int d = Difficulty.Current;
            if (d < Difficulty.Custom) ApplyPreset(d);
        }
        UpdateXpMult();
    }

    void ApplyPreset(int d)
    {
        diffIdx = Mathf.Clamp(Difficulty.Ai[d], 0, DiffNames.Length - 1);
        damageMode = Difficulty.Damage[d];
        lineMode = Difficulty.Line[d];
        brakeAssist = Difficulty.BrakeAssist[d]; steerAssist = Difficulty.SteerAssist[d];
        absOn = Difficulty.Abs[d]; tc = (TcMode)Difficulty.Tc[d];
        if (car != null) { car.cfg.tc = tc; car.cfg.abs = absOn; SetupChanged(); }
        if (lineGo != null) lineGo.SetActive(showLine && !playground);
    }

    // a setting of the presets changed by hand: the preset becomes Custom if it no longer matches
    void Tweaked()
    {
        int d = Difficulty.Current;
        if (d < Difficulty.Custom && !Difficulty.Matches(d, diffIdx, damageMode, lineMode, brakeAssist, steerAssist, absOn, (int)tc))
        {
            Difficulty.Set(Difficulty.Custom); diffSeen = Difficulty.Version;
        }
        UpdateXpMult();
    }

    void UpdateXpMult()
    {
        float x = Difficulty.XpMult(diffIdx, lineMode, brakeAssist, steerAssist, absOn, (int)tc);
        MenuProfile.XpMult = x; Difficulty.LastXp = x;
    }
}
