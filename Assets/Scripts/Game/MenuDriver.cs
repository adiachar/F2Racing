// MenuDriver.cs — the Driver page of the front end (MenuUI), and the player's profile (MenuProfile).
//  * "Paper" look (UiKit.cs), the masthead with DRIVER underlined. Left: the same driver block as Home (the giant red
//    italic number, name, country · level · safety, the ink XP line); centre: the 3D driver on paper (MenuStudio.cs;
//    drag to turn him); right: the editable profile as label / value rows: name (a text field on an ink rule; the
//    phone's keyboard opens), number 1-99 and country (‹ value ›), level with its XP line, then GT-style the two
//    ratings side by side, worst to best: driver rating E D C B A A+ with its points, safety rating E D C B A S with
//    its score (the current grade underlined in red). Under the bottom rule: Back (a link) and the career stats.
//  * the driver block (Home and Driver) ends in two rating badges: DR and SR, the letter and a thin line to the next
//    grade (RatingBadge).
// MenuProfile keeps the profile in PlayerPrefs: drvName, drvNum, drvCountry, drvRaces, drvWins, drvPodiums,
// drvPoles, drvXp (total), drvSr (safety rating 0-100), drvDr (driver rating points, DriverRating.cs). Races, XP and
// the ratings are recorded when the results
// page opens (MenuUI.ShowResults, with the race's tally from GameManagerProgress.cs; rules in Progression.cs), poles
// when qualifying ends (SeasonUI.ShowQuali; their XP comes with the race). The strings shown are rebuilt when a value
// changes.
using FormulaSim;
using UnityEngine;

public static class MenuProfile
{
    public static readonly string[] Countries =
    {
        "—", "Argentina", "Australia", "Austria", "Belgium", "Brazil", "Canada", "China", "Denmark", "Finland", "France",
        "Germany", "Great Britain", "Ireland", "Italy", "Japan", "Mexico", "Monaco", "Netherlands", "New Zealand", "Norway",
        "Poland", "Portugal", "South Africa", "Spain", "Sweden", "Switzerland", "Thailand", "United States",
    };
    static bool loaded;
    public static string Name = "Driver", NumStr = "1", LevelStr = "Level 1", XpStr = "0 / 250 XP", SrStr = "70";
    public static string RacesStr = "0", WinsStr = "0", PodiumsStr = "0", PolesStr = "0";
    public static int Num = 1, Country, Races, Wins, Podiums, Poles, Xp, Level = 1, Grade = 1;
    public static float XpFrac, Sr = Progression.SrStart, SrFrac;
    // driver rating (DriverRating.cs): points, grade (0 E .. 5 A+), share of the way to the next grade, "12,450"
    public static int Dr = DriverRating.Start, DrGrade;
    public static float DrFrac;
    public static string DrStr = "1,000";
    public static string Safety => Progression.Grades[Grade];   // the grade letter
    public static RaceProgress Pending;  // GameManager (FinishRace): the race just finished, applied by RecordRace
    public static RaceOutcome Last;      // the last race's XP and safety change (results page)
    public static bool PolePending;      // pole in the qualifying just run: its XP comes with the race from pole
    public static float XpMult = 1f;     // the difficulty's XP multiplier (CareerDifficulty.cs; GameManager sets it at session start)

    public static void Load()
    {
        if (loaded) return; loaded = true;
        Name = PlayerPrefs.GetString("drvName", "Driver");
        if (string.IsNullOrEmpty(Name)) Name = "Driver";
        Num = Mathf.Clamp(PlayerPrefs.GetInt("drvNum", 1), 1, 99);
        Country = Mathf.Clamp(PlayerPrefs.GetInt("drvCountry", 0), 0, Countries.Length - 1);
        Races = PlayerPrefs.GetInt("drvRaces", 0); Wins = PlayerPrefs.GetInt("drvWins", 0);
        Podiums = PlayerPrefs.GetInt("drvPodiums", 0); Poles = PlayerPrefs.GetInt("drvPoles", 0); Xp = Mathf.Max(0, PlayerPrefs.GetInt("drvXp", 0));
        Sr = Mathf.Clamp(PlayerPrefs.GetFloat("drvSr", Progression.SrStart), 0f, 100f);
        Dr = Mathf.Clamp(PlayerPrefs.GetInt("drvDr", DriverRating.Start), 0, DriverRating.Max);
        Build();
    }
    static void Build()
    {
        NumStr = Num.ToString();
        Level = Progression.LevelOf(Xp); XpFrac = Progression.FracOf(Xp);
        LevelStr = Progression.LevelNames[Level];
        XpStr = Level >= Progression.MaxLevel ? "Max level" : (Xp - Progression.StartOf(Level)) + " / " + Progression.Need(Level) + " XP";
        Grade = Progression.GradeOf(Sr); SrStr = Mathf.RoundToInt(Sr).ToString(); SrFrac = Progression.GradeFrac(Sr);
        DrGrade = DriverRating.GradeOf(Dr); DrFrac = DriverRating.FracOf(Dr); DrStr = DriverRating.Pts(Dr);
        RacesStr = Races.ToString(); WinsStr = Wins.ToString(); PodiumsStr = Podiums.ToString(); PolesStr = Poles.ToString();
    }

    // typed text as it is: an empty name is kept while typing and shown as "Driver"
    public static void SetName(string s)
    {
        Load();
        if (s == null) s = "";
        PlayerPrefs.SetString("drvName", s);
        Name = s.Length == 0 ? "Driver" : s;
    }
    public static string Typed => PlayerPrefs.GetString("drvName", "Driver");
    public static void SetNum(int n) { Load(); Num = Mathf.Clamp(n, 1, 99); PlayerPrefs.SetInt("drvNum", Num); Build(); }
    public static void SetCountry(int c) { Load(); Country = Mathf.Clamp(c, 0, Countries.Length - 1); PlayerPrefs.SetInt("drvCountry", Country); }

    // a race finished (pos 1-based of cars; fastest = the player set the race's fastest lap): career stats, XP from
    // the race (Pending: laps, overtakes, clean laps; Progression.cs has the numbers), the safety rating and the
    // driver rating (rows: the result in finishing order with each car's DR and grid slot; laps: the race length)
    public static void RecordRace(int pos, int cars, bool fastest, MenuUI.Row[] rows = null, int raceLaps = 1)
    {
        Load();
        var rp = Pending; Pending = null;
        Races++;
        bool field = cars > 1;
        if (field && pos == 1) Wins++;
        if (field && pos <= 3) Podiums++;
        var o = new RaceOutcome { xpBefore = Xp, levelBefore = Level, srBefore = Sr, gradeBefore = Grade };

        // ---- XP: (label, value) sources, the biggest three are listed
        int laps = rp != null ? rp.laps : 0, ot = rp != null ? Mathf.Min(rp.overtakes, Progression.MaxOvertakes) : 0, cl = rp != null ? rp.cleanLaps : 0;
        int xPos = field ? Mathf.Max(0, cars - pos) * Progression.XpPerPlace + (pos == 1 ? Progression.XpWin : pos <= 3 ? Progression.XpPodium : 0) : 0;
        bool pole = PolePending && field && rp != null && rp.fromPole; PolePending = false;
        var names = new[] { "Finish", "Position", "Laps", "Overtakes", "Clean laps", "Pole", "Fastest lap" };
        var vals = new[] { Progression.XpFinish, xPos, laps * Progression.XpPerLap, ot * Progression.XpOvertake, cl * Progression.XpCleanLap,
                           pole ? Progression.XpPole : 0, field && fastest ? Progression.XpFastest : 0 };
        int gain = 0; foreach (int v in vals) gain += v;
        int raw = gain; gain = Progression.WithDifficulty(gain, XpMult); // harder difficulty = more XP
        Xp += gain;
        o.xpGain = gain; o.xpAfter = Xp; o.xpGainStr = "+" + gain + " XP";
        var sb = new System.Text.StringBuilder();
        var used = new bool[vals.Length];
        for (int k = 0; k < 3; k++)
        {
            int best = -1; for (int i = 0; i < vals.Length; i++) if (!used[i] && vals[i] > 0 && (best < 0 || vals[i] > vals[best])) best = i;
            if (best < 0) break;
            used[best] = true;
            if (sb.Length > 0) sb.Append("  \u00b7  ");
            sb.Append(names[best]).Append(" +").Append(vals[best]);
        }
        if (gain != raw) { if (sb.Length > 0) sb.Append("  \u00b7  "); sb.Append(Difficulty.XpLabel(XpMult)); }
        o.xpDetail = sb.ToString();

        // ---- safety rating: the race's own tally (a clean-race pull at the flag is in it already)
        if (rp != null) Sr = Mathf.Clamp(rp.sr, 0f, 100f);
        o.srAfter = Sr;
        Save();
        o.levelAfter = Level; o.gradeAfter = Grade;
        o.xpAfterStr = XpStr;
        o.srBeforeStr = Mathf.RoundToInt(o.srBefore).ToString(); o.srAfterStr = SrStr;
        o.srDelta = Mathf.RoundToInt(o.srAfter) - Mathf.RoundToInt(o.srBefore);
        o.srDeltaStr = o.srDelta > 0 ? "+" + o.srDelta : o.srDelta < 0 ? "\u2013" + (-o.srDelta) : "\u00b10";
        Reasons(o, rp);
        RateRace(o, rows, raceLaps);
        Last = o;
    }

    // the driver rating: an Elo against the field (DriverRating.Race); not rated without rivals
    static void RateRace(RaceOutcome o, MenuUI.Row[] rows, int laps)
    {
        o.drBefore = o.drAfter = Dr; o.drGradeBefore = o.drGradeAfter = DrGrade;
        if (!DriverRating.Race(Dr, rows, laps, out float d, out int beaten, out int avg)) return;
        o.drRated = true;
        Dr = DriverRating.Apply(Dr, d);
        Save();
        o.drAfter = Dr; o.drGradeAfter = DrGrade;
        o.drBeforeStr = DriverRating.Pts(o.drBefore); o.drAfterStr = DrStr;
        o.drDelta = o.drAfter - o.drBefore;
        o.drDeltaStr = o.drDelta > 0 ? "+" + DriverRating.Pts(o.drDelta) : o.drDelta < 0 ? "\u2013" + DriverRating.Pts(-o.drDelta) : "\u00b10";
        int n = rows.Length - 1;
        o.drNote = (beaten >= n ? "Beat all " + n : "Beat " + beaten + " of " + n) + "  \u00b7  field " + DriverRating.Grades[DriverRating.GradeOf(avg)];
    }

    // a race quit or restarted after lights out (GameManagerProgress.ProgressAbandon): DR as last, minus a bit more
    public static void CommitQuit(float dLevel)
    {
        Load();
        if (dLevel >= 0f) return;
        Dr = DriverRating.Apply(Dr, dLevel);
        Save();
    }

    // shot mode (DebugShots.cs dr=): the driver rating before the made-up finish
    public static void SetDr(int pts) { Load(); Dr = Mathf.Clamp(pts, 0, DriverRating.Max); Save(); }

    // up to three reasons for the safety change, the biggest first
    static void Reasons(RaceOutcome o, RaceProgress rp)
    {
        o.reasons = 0;
        if (rp == null) return;
        string X(string s, int n) => n > 1 ? s + "  \u00d7" + n : s;
        var lab = new[] { X("Clean sectors", rp.cleanSectors), "Clean race", X("Contact", rp.contacts), X("Ramming", rp.rams), X("Corner cutting", rp.cuts), X("Off track", rp.offTracks), X("Barrier", rp.barriers) };
        var val = new[] { rp.gainSectors, rp.gainClean, -rp.lossContact, -rp.lossRam, -rp.lossCut, -rp.lossOff, -rp.lossBarrier };
        var used = new bool[val.Length];
        for (int k = 0; k < 3; k++)
        {
            int best = -1;
            for (int i = 0; i < val.Length; i++) if (!used[i] && Mathf.Abs(val[i]) >= 0.05f && (best < 0 || Mathf.Abs(val[i]) > Mathf.Abs(val[best]))) best = i;
            if (best < 0) break;
            used[best] = true;
            o.reason[o.reasons] = lab[best]; o.reasonVal[o.reasons] = Progression.Signed(val[best]); o.reasonGood[o.reasons] = val[best] > 0; o.reasons++;
        }
    }

    // an unfinished race (restart, leave): its safety penalties still count, its clean driving does not
    public static void CommitLoss(float loss)
    {
        Load();
        if (loss <= 0f) return;
        Sr = Mathf.Clamp(Sr - loss, 0f, 100f);
        Save();
    }

    // bonus XP outside a race result (career goals, Season.Record)
    public static void AddXp(int xp)
    {
        Load();
        if (xp <= 0) return;
        Xp += xp;
        Save();
    }

    public static void RecordPole()
    {
        Load();
        Poles++; PolePending = true;
        PlayerPrefs.SetInt("drvPoles", Poles);
        Build();
    }

    // debug (Settings > Game > Reset progress): XP, level, both ratings and career stats back to new
    public static void ResetProgress()
    {
        Load();
        Xp = 0; Sr = Progression.SrStart; Dr = DriverRating.Start; Races = Wins = Podiums = Poles = 0; Last = null; Pending = null; PolePending = false;
        Save();
    }

    static void Save()
    {
        PlayerPrefs.SetInt("drvRaces", Races); PlayerPrefs.SetInt("drvWins", Wins); PlayerPrefs.SetInt("drvPodiums", Podiums);
        PlayerPrefs.SetInt("drvPoles", Poles); PlayerPrefs.SetInt("drvXp", Xp); PlayerPrefs.SetFloat("drvSr", Sr);
        PlayerPrefs.SetInt("drvDr", Dr);
        PlayerPrefs.Save();
        Build();
    }
}

public sealed partial class MenuUI
{
    const string KeyDriver = "driver";
    GUIStyle nameStyle; int nameStyleH = -1; string nameTyped;

    // "India  ·  Level 14": rebuilt when the country or level changes (the ratings are the badges under the XP line)
    int idKey = int.MinValue; string idLine;
    string IdLine()
    {
        MenuProfile.Load();
        int key = MenuProfile.Country * 7919 + MenuProfile.Level * 104729;
        if (key != idKey || idLine == null)
        {
            idKey = key;
            idLine = (MenuProfile.Country > 0 ? MenuProfile.Countries[MenuProfile.Country] + "  ·  " : "") + MenuProfile.LevelStr;
        }
        return idLine;
    }

    // a rating badge (GT's DR / SR): the grade letter, the caption with the value right of it, a thin ink line to
    // the next grade
    static void RatingBadge(Rect r, string cap, string letter, Color lc, string val, float frac)
    {
        float lw = U(30), tx = r.x + lw, tw = r.width - lw;
        UiKit.Text(new Rect(r.x, r.y, lw, r.height), letter, UiKit.Heavy, 20f, lc);
        UiKit.Kicker(new Rect(tx, r.y + U(3), tw, U(12)), cap, 8f);
        UiKit.Text(new Rect(tx, r.y + U(3), tw, U(12)), val, UiKit.Heavy, 9f, UiKit.Ink, TextAnchor.MiddleRight);
        UiKit.Meter(new Rect(tx, r.y + U(19), tw, Mathf.Max(2f, Mathf.Round(U(2f)))), frac);
    }

    // the driver block (Home and Driver): the giant red italic number, the name, the id line, the XP line, the DR and
    // SR badges. tap: the block is a button (Home -> Driver page). Returns true when tapped.
    bool DriverBlock(Rect block, float y0, bool tap)
    {
        float pd = 0f;
        bool hit = tap && UiKit.Press(block, true, out pd);
        float lw = block.width;
        UiKit.BigNumber(new Rect(block.x - U(3), y0 + U(72), lw + U(40), U(120)), MenuProfile.NumStr, 150f, UiKit.Red);
        string nm = UiKit.Upper(MenuProfile.Name), il = IdLine();
        UiKit.Text(new Rect(block.x + U(2), y0 + U(197), lw, U(28)), nm, UiKit.Heavy, UiKit.Fit(nm, UiKit.Heavy, 22f, lw, 13f), Color.Lerp(UiKit.Ink, UiKit.Red, pd));
        UiKit.Text(new Rect(block.x + U(2), y0 + U(223), lw, U(16)), il, UiKit.Body, UiKit.Fit(il, UiKit.Body, 12f, lw, 8.5f), UiKit.InkSub);
        float mw = Mathf.Min(U(230), lw);
        UiKit.Meter(new Rect(block.x + U(2), y0 + U(246), mw, Mathf.Max(2f, Mathf.Round(U(3f)))), MenuProfile.XpFrac);
        // DR and SR, GT-style, side by side
        float bw = (mw - U(16)) * 0.5f, by = y0 + U(258);
        RatingBadge(new Rect(block.x + U(2), by, bw, U(26)), "DR", DriverRating.Grades[MenuProfile.DrGrade], DriverRating.GradeColor(MenuProfile.DrGrade), MenuProfile.DrStr, MenuProfile.DrFrac);
        RatingBadge(new Rect(block.x + U(18) + bw, by, bw, U(26)), "SR", MenuProfile.Safety, UiKit.PaperGrade(MenuProfile.Grade), MenuProfile.SrStr, MenuProfile.SrFrac);
        return hit;
    }

    // ================================================================== driver
    void DrawDriverPage()
    {
        MenuProfile.Load();
        // no page fill: the studio camera clears to the paper colour and the driver stands in the middle
        var f = Masthead(UiKit.NavDriver);
        float y0 = f.safe.y, x0 = f.x0, x1 = f.x1, W = f.w;

        // ---- left: the driver block (as Home)
        float lw = Mathf.Min(U(250), W * 0.3f);
        float e0 = UiKit.Enter(KeyDriver, 0, 0.05f, 0.45f);
        UiKit.PushAlpha(e0);
        var block = UiKit.Slide(new Rect(x0, y0 + U(66), lw, U(222)), e0, -10f);
        DriverBlock(block, y0, false);
        UiKit.PopAlpha();

        // ---- right: the profile as label / value rows
        float PW = Mathf.Clamp(W * 0.36f, U(250), U(320));
        float ep = UiKit.Enter(KeyDriver, 1, 0.04f, 0.45f);
        UiKit.PushAlpha(ep);
        var col = UiKit.Slide(new Rect(x1 - PW, f.top, PW, f.foot - U(10) - f.top), ep, 16f);
        float ix = col.x, iw = col.width, y = col.y;
        studio.Spin(new Rect(block.xMax + U(8), f.head + U(4), ix - U(16) - block.xMax, f.foot - U(4) - f.head - U(4)));

        // name and number on one row, country under them (every ‹ › is a 44-unit target)
        float numW = Mathf.Min(U(140), iw * 0.45f), nameW = iw - numW - U(20);
        UiKit.Kicker(new Rect(ix, y, nameW, U(13)), "Name", 8.5f);
        UiKit.Kicker(new Rect(ix + nameW + U(20), y, numW, U(13)), "Number", 8.5f);
        NameField(new Rect(ix, y + U(13), nameW, U(44)));
        int num = UiKit.Stepper(new Rect(ix + nameW + U(20), y + U(13), numW, U(44)), MenuProfile.NumStr, MenuProfile.Num - 1, 99) + 1;
        if (num != MenuProfile.Num) MenuProfile.SetNum(num);
        y += U(66);
        UiKit.Kicker(new Rect(ix, y, iw, U(13)), "Country", 8.5f);
        string cn = MenuProfile.Countries[MenuProfile.Country];
        int ci = UiKit.Stepper(new Rect(ix, y + U(13), iw, U(44)), cn, MenuProfile.Country, MenuProfile.Countries.Length);
        if (ci != MenuProfile.Country) MenuProfile.SetCountry(ci);
        y += U(68);

        // level and XP (an ink line)
        UiKit.Text(new Rect(ix, y, iw * 0.5f, U(20)), MenuProfile.LevelStr, UiKit.Heavy, 15f, UiKit.Ink);
        UiKit.Text(new Rect(ix, y, iw, U(20)), MenuProfile.XpStr, UiKit.Body, 11f, UiKit.InkSub, TextAnchor.MiddleRight);
        UiKit.Meter(new Rect(ix, y + U(24), iw, Mathf.Max(2f, Mathf.Round(U(3f)))), MenuProfile.XpFrac);
        y += U(40);

        // the two ratings, GT-style, worst to best, the current grade underlined in red: DR (points) and SR (score)
        float half = (iw - U(20)) * 0.5f;
        Ladder(ix, y, half, "Driver rating", MenuProfile.DrStr, DriverRating.Grades, MenuProfile.DrGrade, DriverRating.GradeColor(MenuProfile.DrGrade));
        Ladder(ix + half + U(20), y, half, "Safety rating", MenuProfile.SrStr, Progression.GradesUp, 5 - MenuProfile.Grade, UiKit.PaperGrade(MenuProfile.Grade));
        UiKit.Hair(ix, ix + iw, y + U(50), UiKit.PaperTrack);
        UiKit.PopAlpha();

        // ---- the action row: Back, and the career stats on the right
        UiKit.Hair(x0, x1, f.foot);
        if (UiKit.Link(new Rect(x0, f.actY - U(22), U(84), U(44)), "\u2039  Back", "Home")) Back();
        float sx = x1;
        sx = StatLeft(sx, f.actY, "Poles", MenuProfile.PolesStr);
        sx = StatLeft(sx, f.actY, "Podiums", MenuProfile.PodiumsStr);
        sx = StatLeft(sx, f.actY, "Wins", MenuProfile.WinsStr);
        StatLeft(sx, f.actY, "Races", MenuProfile.RacesStr);
    }

    // a rating's grades in a row (worst first), the current one (on) in its colour and underlined in red; the caption
    // and the value on the line above
    static void Ladder(float x, float y, float w, string cap, string val, string[] grades, int on, Color gc)
    {
        float vw = UiKit.Width(val, UiKit.Heavy, 12f);
        UiKit.Kicker(new Rect(x, y, w - vw - U(4), U(14)), cap, UiKit.Fit(UiKit.Caps(cap), UiKit.Body, 8.5f, w - vw - U(6), 6.5f));
        UiKit.Text(new Rect(x, y - U(2), w, U(18)), val, UiKit.Heavy, 12f, gc, TextAnchor.MiddleRight);
        y += U(16);
        float gw = w / grades.Length;
        for (int i = 0; i < grades.Length; i++)
        {
            var g = new Rect(x + i * gw, y, gw, U(28));
            bool cur = i == on;
            UiKit.Text(g, grades[i], cur ? UiKit.Heavy : UiKit.Medium, cur ? 15f : 11.5f, cur ? gc : UiKit.InkMuted, TextAnchor.MiddleCenter);
            if (cur) UiKit.Rect(new Rect(g.center.x - U(8), g.yMax, U(16), Mathf.Max(2f, Mathf.Round(U(2f)))), UiKit.Red);
        }
    }

    // shot mode (DebugShots.cs menu=home|driver): open a menu page
    internal void ShotPage(bool driver) { if (driver) GoPage(Page.Driver); else GoHome(); }
    internal void ShotSetup(bool more) { mode = ModeQuick; GoPlay(StepSetup, 1); setupMore = more; } // DebugShots menu=setup more=0|1

    // a career number (caption over a big value) placed to the left of x, centred on y; returns the next x
    static float StatLeft(float x, float y, string caption, string value)
    {
        float w = Mathf.Max(U(48), UiKit.DatumWidth(caption, value, 20f));
        UiKit.Datum(new Rect(x - w, y - U(20), w, U(40)), caption, value, UiKit.Ink, 20f, TextAnchor.MiddleRight);
        return x - w - U(28);
    }

    // the name: an IMGUI text field (the on-screen keyboard opens on a phone) on an ink rule (red while typing)
    void NameField(Rect r)
    {
        if (nameStyle == null || nameStyleH != Screen.height)
        {
            nameStyleH = Screen.height;
            nameStyle = new GUIStyle(GUI.skin.textField)
            {
                font = UiKit.Heavy, fontSize = Mathf.Max(8, Mathf.RoundToInt(U(16))), alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), border = new RectOffset(0, 0, 0, 0),
                clipping = TextClipping.Clip, wordWrap = false,
            };
            nameStyle.normal.background = null; nameStyle.hover.background = null; nameStyle.focused.background = null; nameStyle.active.background = null;
            nameStyle.onNormal.background = null; nameStyle.onHover.background = null; nameStyle.onFocused.background = null; nameStyle.onActive.background = null;
            nameStyle.normal.textColor = nameStyle.hover.textColor = nameStyle.focused.textColor = nameStyle.active.textColor = UiKit.Ink;
            GUI.skin.settings.cursorColor = UiKit.Red;
            GUI.skin.settings.selectionColor = UiKit.A(UiKit.Red, 0.25f);
        }
        if (nameTyped == null) nameTyped = MenuProfile.Typed;
        bool focused = GUIUtility.keyboardControl != 0; // the only keyboard control on the page (no per-frame string)
        if (focused) UiKit.Rect(r, UiKit.PaperTint);
        string s = GUI.TextField(r, nameTyped, 16, nameStyle);
        if (s != nameTyped) { nameTyped = s; MenuProfile.SetName(s.Trim()); }
        if (!focused && nameTyped.Length == 0) UiKit.Text(r, "Driver", UiKit.Heavy, 16f, UiKit.InkMuted);
        float th = focused ? Mathf.Max(2f, Mathf.Round(U(2f))) : UiKit.HairPx;
        UiKit.Rect(new Rect(r.x, r.yMax - th, r.width, th), focused ? UiKit.Red : UiKit.Ink);
    }
}
