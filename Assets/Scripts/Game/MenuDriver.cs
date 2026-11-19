// MenuDriver.cs — the Driver page of the front end (MenuUI), and the player's profile (MenuProfile).
//  * the 3D driver on the left (MenuStudio.cs; drag to turn him), one panel on the right: name (a text field; the
//    phone's keyboard opens), number 1-99, country, level with its XP bar (cyan), safety rating S A B C D E with the
//    score (S A B green, C neutral, D E amber) and career stats (races, wins, podiums, poles).
// MenuProfile keeps the profile in PlayerPrefs: drvName, drvNum, drvCountry, drvRaces, drvWins, drvPodiums,
// drvPoles, drvXp (total), drvSr (safety rating 0-100). Races, XP and the safety rating are recorded when the results
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
    public static float XpFrac, Sr = Progression.SrStart;
    public static string Safety => Progression.Grades[Grade];   // the grade letter
    public static RaceProgress Pending;  // GameManager (FinishRace): the race just finished, applied by RecordRace
    public static RaceOutcome Last;      // the last race's XP and safety change (results page)
    public static bool PolePending;      // pole in the qualifying just run: its XP comes with the race from pole

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
        Build();
    }
    static void Build()
    {
        NumStr = Num.ToString();
        Level = Progression.LevelOf(Xp); XpFrac = Progression.FracOf(Xp);
        LevelStr = Progression.LevelNames[Level];
        XpStr = Level >= Progression.MaxLevel ? "Max level" : (Xp - Progression.StartOf(Level)) + " / " + Progression.Need(Level) + " XP";
        Grade = Progression.GradeOf(Sr); SrStr = Mathf.RoundToInt(Sr).ToString();
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
    // the race (Pending: laps, overtakes, clean laps; Progression.cs has the numbers) and the safety rating
    public static void RecordRace(int pos, int cars, bool fastest)
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
        Last = o;
    }

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

    public static void RecordPole()
    {
        Load();
        Poles++; PolePending = true;
        PlayerPrefs.SetInt("drvPoles", Poles);
        Build();
    }

    // debug (Settings > Game > Reset progress): XP, level, safety rating and career stats back to new
    public static void ResetProgress()
    {
        Load();
        Xp = 0; Sr = Progression.SrStart; Races = Wins = Podiums = Poles = 0; Last = null; Pending = null; PolePending = false;
        Save();
    }

    static void Save()
    {
        PlayerPrefs.SetInt("drvRaces", Races); PlayerPrefs.SetInt("drvWins", Wins); PlayerPrefs.SetInt("drvPodiums", Podiums);
        PlayerPrefs.SetInt("drvPoles", Poles); PlayerPrefs.SetInt("drvXp", Xp); PlayerPrefs.SetFloat("drvSr", Sr);
        PlayerPrefs.Save();
        Build();
    }
}

public sealed partial class MenuUI
{
    const string KeyDriver = "driver";
    static readonly Color sideShade = new Color(0.03f, 0.035f, 0.045f, 0.80f);
    GUIStyle nameStyle; int nameStyleH = -1; string nameTyped;

    // shared page header: back button, caption and title (Bold; no colour)
    void PageHeader(float x0, float top, string caption, string title, string scr)
    {
        if (UiKit.IconButton(new Rect(x0, top, U(44), U(44)), UiKit.IconBack)) Back();
        float e = UiKit.Enter(scr, 0);
        UiKit.PushAlpha(e);
        UiKit.Eyebrow(new Rect(x0 + U(58), top + U(3), U(240), U(14)), caption, UiKit.Faint, 8.5f);
        UiKit.Text(new Rect(x0 + U(57), top + U(14), U(260), U(30)), title, UiKit.Bold, 22f, UiKit.TextCol);
        UiKit.PopAlpha();
    }

    // ================================================================== driver
    void DrawDriverPage()
    {
        MenuProfile.Load();
        float sw = Screen.width, sh = Screen.height;
        var safe = UiKit.Safe;
        float M = U(24), x0 = safe.x + M, x1 = safe.xMax - M, W = x1 - x0, top = safe.y + U(16), bottom = safe.yMax - U(16);
        float PW = Mathf.Clamp(W * 0.54f, U(350), U(460));
        UiKit.Gradient(new Rect(x1 - PW - U(120), 0, sw - (x1 - PW) + U(120), sh), clear0, sideShade, 0f, true);
        UiKit.Gradient(new Rect(0, 0, sw, sh * 0.25f), shadeTop, clear0);
        studio.Spin(new Rect(x0, top + U(56), x1 - PW - U(12) - x0, bottom - top - U(56)));
        PageHeader(x0, top, "Profile", "DRIVER", KeyDriver);

        var panel = new Rect(x1 - PW, top, PW, bottom - top);
        float ep = UiKit.Enter(KeyDriver, 1, 0.04f, 0.45f);
        UiKit.PushAlpha(ep);
        panel = UiKit.Slide(panel, ep, 24f);
        UiKit.Panel(panel, 16f);
        float pad = U(18), ix = panel.x + pad, iw = panel.width - 2 * pad, y = panel.y + U(16);

        // ---- name / number / country
        float nw = iw * 0.44f, numW = iw * 0.22f, cw = iw - nw - numW - U(16);
        UiKit.Eyebrow(new Rect(ix, y, nw, U(14)), "Name", UiKit.Faint, 8f);
        UiKit.Eyebrow(new Rect(ix + nw + U(8), y, numW, U(14)), "Number", UiKit.Faint, 8f);
        UiKit.Eyebrow(new Rect(ix + nw + numW + U(16), y, cw, U(14)), "Country", UiKit.Faint, 8f);
        y += U(17);
        NameField(new Rect(ix, y, nw, U(44)));
        int num = UiKit.Stepper(new Rect(ix + nw + U(8), y, numW, U(44)), MenuProfile.NumStr, MenuProfile.Num - 1, 99) + 1;
        if (num != MenuProfile.Num) MenuProfile.SetNum(num);
        string cn = MenuProfile.Countries[MenuProfile.Country];
        int ci = UiKit.Stepper(new Rect(ix + nw + numW + U(16), y, cw, U(44)), cn, MenuProfile.Country, MenuProfile.Countries.Length);
        if (ci != MenuProfile.Country) MenuProfile.SetCountry(ci);
        y += U(44) + U(22);

        // ---- level and XP (cyan: progress)
        UiKit.Text(new Rect(ix, y, iw * 0.5f, U(22)), MenuProfile.LevelStr, UiKit.Bold, 15f, UiKit.TextCol);
        UiKit.Text(new Rect(ix, y, iw, U(22)), MenuProfile.XpStr, UiKit.Regular, 10.5f, UiKit.Sub, TextAnchor.MiddleRight);
        var bar = new Rect(ix, y + U(26), iw, Mathf.Max(3f, U(4)));
        UiKit.Rect(bar, new Color(1, 1, 1, 0.08f), 2f);
        if (MenuProfile.XpFrac > 0.001f) UiKit.Rect(new Rect(bar.x, bar.y, Mathf.Max(bar.height, bar.width * MenuProfile.XpFrac), bar.height), UiKit.Info, 2f);
        y += U(30) + U(22);

        // ---- safety rating (green: good)
        UiKit.Eyebrow(new Rect(ix, y, iw, U(14)), "Safety rating", UiKit.Faint, 8f);
        Color gc = Progression.GradeColor(MenuProfile.Grade);
        UiKit.Text(new Rect(ix, y - U(2), iw, U(18)), MenuProfile.SrStr, UiKit.Bold, 12f, gc, TextAnchor.MiddleRight);
        y += U(17);
        float gw = (iw - 5 * U(6)) / 6f;
        for (int i = 0; i < Progression.Grades.Length; i++)
        {
            var g = new Rect(ix + i * (gw + U(6)), y, gw, U(30));
            bool on = i == MenuProfile.Grade;
            UiKit.Rect(g, new Color(1, 1, 1, on ? 0.06f : 0.03f), 8f);
            UiKit.Outline(g, on ? gc : UiKit.Line, 8f, on ? 1f : 0.7f);
            UiKit.Text(g, Progression.Grades[i], UiKit.Bold, 12.5f, on ? gc : UiKit.Faint, TextAnchor.MiddleCenter);
        }
        y += U(30) + U(22);

        // ---- career
        UiKit.Eyebrow(new Rect(ix, y, iw, U(14)), "Career", UiKit.Faint, 8f);
        y += U(17);
        float sw4 = (iw - 3 * U(8)) / 4f, shh = Mathf.Min(U(52), panel.yMax - U(16) - y);
        StatTile(new Rect(ix, y, sw4, shh), "Races", MenuProfile.RacesStr);
        StatTile(new Rect(ix + (sw4 + U(8)), y, sw4, shh), "Wins", MenuProfile.WinsStr);
        StatTile(new Rect(ix + 2 * (sw4 + U(8)), y, sw4, shh), "Podiums", MenuProfile.PodiumsStr);
        StatTile(new Rect(ix + 3 * (sw4 + U(8)), y, sw4, shh), "Poles", MenuProfile.PolesStr);
        UiKit.PopAlpha();
    }

    static void StatTile(Rect r, string caption, string value)
    {
        UiKit.Rect(r, new Color(1, 1, 1, 0.035f), 10f);
        UiKit.Outline(r, UiKit.Line, 10f, 0.7f);
        UiKit.Text(new Rect(r.x + U(10), r.y + U(4), r.width - U(14), U(28)), value, UiKit.Bold, UiKit.Fit(value, UiKit.Bold, 19f, r.width - U(14), 12f), UiKit.TextCol);
        UiKit.Text(new Rect(r.x + U(10), r.yMax - U(19), r.width - U(12), U(14)), caption, UiKit.Regular, 9.5f, UiKit.Sub);
    }

    // the name: an IMGUI text field (the on-screen keyboard opens on a phone) styled like the other controls
    void NameField(Rect r)
    {
        int px = Mathf.RoundToInt(U(12));
        if (nameStyle == null || nameStyleH != Screen.height)
        {
            nameStyleH = Screen.height;
            nameStyle = new GUIStyle(GUI.skin.textField)
            {
                font = UiKit.Bold, fontSize = Mathf.Max(8, Mathf.RoundToInt(U(15))), alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(px, px, 0, 0), margin = new RectOffset(0, 0, 0, 0), border = new RectOffset(0, 0, 0, 0),
                clipping = TextClipping.Clip, wordWrap = false,
            };
            nameStyle.normal.background = null; nameStyle.hover.background = null; nameStyle.focused.background = null; nameStyle.active.background = null;
            nameStyle.onNormal.background = null; nameStyle.onHover.background = null; nameStyle.onFocused.background = null; nameStyle.onActive.background = null;
            nameStyle.normal.textColor = nameStyle.hover.textColor = nameStyle.focused.textColor = nameStyle.active.textColor = UiKit.TextCol;
            GUI.skin.settings.cursorColor = UiKit.Accent;
            GUI.skin.settings.selectionColor = UiKit.A(UiKit.Accent, 0.4f);
        }
        if (nameTyped == null) nameTyped = MenuProfile.Typed;
        bool focused = GUIUtility.keyboardControl != 0; // the only keyboard control on the page (no per-frame string)
        UiKit.Rect(r, new Color(1, 1, 1, focused ? 0.09f : 0.055f), 10f);
        UiKit.Outline(r, focused ? UiKit.Accent : UiKit.Line, 10f, focused ? 1.1f : 0.7f);
        string s = GUI.TextField(r, nameTyped, 16, nameStyle);
        if (s != nameTyped) { nameTyped = s; MenuProfile.SetName(s.Trim()); }
        if (!focused && nameTyped.Length == 0) UiKit.Text(new Rect(r.x + px, r.y, r.width - px, r.height), "Driver", UiKit.Bold, 15f, UiKit.Faint);
    }
}
