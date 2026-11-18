// MenuDriver.cs — the Driver and Garage pages of the front end (MenuUI), and the player's profile (MenuProfile).
//  * Driver: the 3D driver on the left (MenuStudio.cs; drag to turn him), a panel on the right: name (a text field;
//    the phone's keyboard opens), number 1-99, country, level with its XP bar, safety rating S A B C D E (A for now),
//    career stats (races, wins, podiums, poles) and Suit / Helmet / Gloves / Face (coming soon).
//  * Garage: the car on the right (drag to turn it), a panel on the left: car class and livery (saved as "class" by
//    GameManager and "livery"; GameManager puts the livery on the car: Choice.Livery), and a coming-soon note for
//    the car setup and a livery editor.
// MenuProfile keeps the profile in PlayerPrefs: drvName, drvNum, drvCountry, drvRaces, drvWins, drvPodiums,
// drvPoles, drvXp. Races are counted when the results page opens (MenuUI.ShowResults), poles when qualifying ends
// (SeasonUI.ShowQuali). The strings shown are rebuilt when a value changes.
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
    public const string Safety = "A";               // safety rating: a placeholder until incidents are scored
    public const int XpPerLevel = 1000;
    static bool loaded;
    public static string Name = "Driver", NumStr = "1", LevelStr = "LEVEL 1", XpStr = "0 / 1000 XP";
    public static string RacesStr = "0", WinsStr = "0", PodiumsStr = "0", PolesStr = "0";
    public static int Num = 1, Country, Races, Wins, Podiums, Poles, Xp;
    public static float XpFrac;

    public static void Load()
    {
        if (loaded) return; loaded = true;
        Name = PlayerPrefs.GetString("drvName", "Driver");
        if (string.IsNullOrEmpty(Name)) Name = "Driver";
        Num = Mathf.Clamp(PlayerPrefs.GetInt("drvNum", 1), 1, 99);
        Country = Mathf.Clamp(PlayerPrefs.GetInt("drvCountry", 0), 0, Countries.Length - 1);
        Races = PlayerPrefs.GetInt("drvRaces", 0); Wins = PlayerPrefs.GetInt("drvWins", 0);
        Podiums = PlayerPrefs.GetInt("drvPodiums", 0); Poles = PlayerPrefs.GetInt("drvPoles", 0); Xp = PlayerPrefs.GetInt("drvXp", 0);
        Build();
    }
    static void Build()
    {
        NumStr = Num.ToString();
        int lvl = 1 + Xp / XpPerLevel, inLvl = Xp % XpPerLevel;
        LevelStr = "Level " + lvl; XpStr = inLvl + " / " + XpPerLevel + " XP"; XpFrac = inLvl / (float)XpPerLevel;
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

    // a race finished (pos 1-based of cars): stats and XP (a finish, places beaten, a win / podium bonus)
    public static void RecordRace(int pos, int cars)
    {
        Load();
        Races++;
        bool field = cars > 1;
        if (field && pos == 1) Wins++;
        if (field && pos <= 3) Podiums++;
        Xp += 100 + (field ? Mathf.Max(0, cars - pos) * 10 : 0) + (field && pos == 1 ? 150 : field && pos <= 3 ? 60 : 0);
        PlayerPrefs.SetInt("drvRaces", Races); PlayerPrefs.SetInt("drvWins", Wins); PlayerPrefs.SetInt("drvPodiums", Podiums); PlayerPrefs.SetInt("drvXp", Xp);
        Build();
    }
    public static void RecordPole()
    {
        Load();
        Poles++; Xp += 80;
        PlayerPrefs.SetInt("drvPoles", Poles); PlayerPrefs.SetInt("drvXp", Xp);
        Build();
    }
}

public sealed partial class MenuUI
{
    const string KeyDriver = "driver", KeyGarage = "garage";
    static readonly string[] SafetyGrades = { "S", "A", "B", "C", "D", "E" };
    static readonly Color sideShade = new Color(0f, 0f, 0f, 0.72f);
    GUIStyle nameStyle; int nameStyleH = -1; string nameTyped;

    // shared page header: back button, eyebrow and title
    void PageHeader(float x0, float top, string eyebrow, string title, string scr)
    {
        if (UiKit.IconButton(new Rect(x0, top, U(44), U(44)), UiKit.IconBack)) Back();
        float e = UiKit.Enter(scr, 0);
        UiKit.PushAlpha(e);
        UiKit.Eyebrow(new Rect(x0 + U(56), top + U(2), U(240), U(14)), eyebrow, UiKit.AccentHi, 9f);
        UiKit.Text(new Rect(x0 + U(55), top + U(12), U(260), U(32)), title, UiKit.Black, 24f, UiKit.TextCol);
        UiKit.PopAlpha();
    }

    // ================================================================== driver
    void DrawDriverPage()
    {
        MenuProfile.Load();
        float sw = Screen.width, sh = Screen.height;
        var safe = UiKit.Safe;
        float M = U(20), x0 = safe.x + M, x1 = safe.xMax - M, W = x1 - x0, top = safe.y + U(12), bottom = safe.yMax - U(12);
        float PW = Mathf.Clamp(W * 0.56f, U(360), U(480));
        UiKit.Gradient(new Rect(x1 - PW - U(120), 0, sw - (x1 - PW) + U(120), sh), clear0, sideShade, 0f, true);
        UiKit.Gradient(new Rect(0, 0, sw, sh * 0.25f), shadeTop, clear0);
        studio.Spin(new Rect(x0, top + U(56), x1 - PW - U(12) - x0, bottom - top - U(56)), false);
        PageHeader(x0, top, "Profile", "DRIVER", KeyDriver);

        var panel = new Rect(x1 - PW, top, PW, bottom - top);
        float ep = UiKit.Enter(KeyDriver, 1, 0.04f, 0.45f);
        UiKit.PushAlpha(ep);
        panel = UiKit.Slide(panel, ep, 24f);
        UiKit.Panel(panel, 16f);
        float pad = U(14), ix = panel.x + pad, iw = panel.width - 2 * pad, y = panel.y + U(12);

        // ---- name / number / country
        float nw = iw * 0.44f, numW = iw * 0.22f, cw = iw - nw - numW - U(16);
        UiKit.Eyebrow(new Rect(ix, y, nw, U(14)), "Name", UiKit.Faint, 8f);
        UiKit.Eyebrow(new Rect(ix + nw + U(8), y, numW, U(14)), "Number", UiKit.Faint, 8f);
        UiKit.Eyebrow(new Rect(ix + nw + numW + U(16), y, cw, U(14)), "Country", UiKit.Faint, 8f);
        y += U(16);
        NameField(new Rect(ix, y, nw, U(44)));
        int num = UiKit.Stepper(new Rect(ix + nw + U(8), y, numW, U(44)), MenuProfile.NumStr, MenuProfile.Num - 1, 99) + 1;
        if (num != MenuProfile.Num) MenuProfile.SetNum(num);
        string cn = MenuProfile.Countries[MenuProfile.Country];
        int ci = UiKit.Stepper(new Rect(ix + nw + numW + U(16), y, cw, U(44)), cn, MenuProfile.Country, MenuProfile.Countries.Length);
        if (ci != MenuProfile.Country) MenuProfile.SetCountry(ci);
        y += U(54);

        // ---- level and XP
        UiKit.Text(new Rect(ix, y, U(120), U(24)), UiKit.Upper(MenuProfile.LevelStr), UiKit.Black, 16f, UiKit.TextCol);
        UiKit.Text(new Rect(ix, y, iw, U(24)), MenuProfile.XpStr, UiKit.Semi, 10.5f, UiKit.Sub, TextAnchor.MiddleRight);
        var bar = new Rect(ix, y + U(27), iw, Mathf.Max(3f, U(5)));
        UiKit.Rect(bar, new Color(1, 1, 1, 0.08f), 3f);
        if (MenuProfile.XpFrac > 0.001f) UiKit.Gradient(new Rect(bar.x, bar.y, Mathf.Max(bar.height, bar.width * MenuProfile.XpFrac), bar.height), badgeTop, badgeBottom, 3f, true);
        y += U(42);

        // ---- safety rating
        UiKit.Eyebrow(new Rect(ix, y, iw, U(14)), "Safety rating", UiKit.Faint, 8f);
        UiKit.Text(new Rect(ix, y, iw, U(14)), "Clean racing raises it", UiKit.Regular, 9f, UiKit.Faint, TextAnchor.MiddleRight);
        y += U(17);
        float gw = (iw - 5 * U(6)) / 6f;
        for (int i = 0; i < SafetyGrades.Length; i++)
        {
            var g = new Rect(ix + i * (gw + U(6)), y, gw, U(28));
            bool on = SafetyGrades[i] == MenuProfile.Safety;
            if (on) { UiKit.Rect(g, UiKit.A(UiKit.Good, 0.22f), 8f); UiKit.Outline(g, UiKit.Good, 8f, 1f); }
            else UiKit.Rect(g, new Color(1, 1, 1, 0.05f), 8f);
            UiKit.Text(g, SafetyGrades[i], UiKit.Black, 13f, on ? UiKit.TextCol : UiKit.Faint, TextAnchor.MiddleCenter);
        }
        y += U(38);

        // ---- career
        float sw4 = (iw - 3 * U(8)) / 4f, shh = U(52);
        StatTile(new Rect(ix, y, sw4, shh), "Races", MenuProfile.RacesStr, UiKit.TextCol);
        StatTile(new Rect(ix + (sw4 + U(8)), y, sw4, shh), "Wins", MenuProfile.WinsStr, UiKit.Gold);
        StatTile(new Rect(ix + 2 * (sw4 + U(8)), y, sw4, shh), "Podiums", MenuProfile.PodiumsStr, UiKit.TextCol);
        StatTile(new Rect(ix + 3 * (sw4 + U(8)), y, sw4, shh), "Poles", MenuProfile.PolesStr, UiKit.Purple);
        y += shh + U(10);

        // ---- look (placeholders)
        float lh = Mathf.Max(U(42), Mathf.Min(U(46), panel.yMax - pad - (y + U(16))));
        UiKit.Eyebrow(new Rect(ix, y, iw, U(14)), "Customise look", UiKit.Faint, 8f);
        UiKit.Text(new Rect(ix, y, iw, U(14)), "Coming soon", UiKit.Regular, 9f, UiKit.Faint, TextAnchor.MiddleRight);
        y += U(16);
        float lw = (iw - 3 * U(8)) / 4f;
        if (UiKit.Button(new Rect(ix, y, lw, lh), "Suit", UiKit.Look.Ghost, true, 11.5f)) Toast("Suit designs  ·  coming soon");
        if (UiKit.Button(new Rect(ix + (lw + U(8)), y, lw, lh), "Helmet", UiKit.Look.Ghost, true, 11.5f)) Toast("Helmet designs  ·  coming soon");
        if (UiKit.Button(new Rect(ix + 2 * (lw + U(8)), y, lw, lh), "Gloves", UiKit.Look.Ghost, true, 11.5f)) Toast("Gloves  ·  coming soon");
        if (UiKit.Button(new Rect(ix + 3 * (lw + U(8)), y, lw, lh), "Face", UiKit.Look.Ghost, true, 11.5f)) Toast("Faces  ·  coming soon");
        UiKit.PopAlpha();
    }

    static void StatTile(Rect r, string caption, string value, Color vc)
    {
        UiKit.Rect(r, new Color(1, 1, 1, 0.05f), 10f);
        UiKit.Outline(r, new Color(1, 1, 1, 0.07f), 10f, 0.7f);
        UiKit.Text(new Rect(r.x + U(10), r.y + U(4), r.width - U(14), U(28)), value, UiKit.Black, UiKit.Fit(value, UiKit.Black, 20f, r.width - U(14), 12f), vc);
        UiKit.Eyebrow(new Rect(r.x + U(10), r.yMax - U(19), r.width - U(12), U(14)), caption, UiKit.Faint, 7.5f);
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

    // ================================================================== garage
    Choice DrawGarage(ref int cls)
    {
        float sw = Screen.width, sh = Screen.height;
        var safe = UiKit.Safe;
        float M = U(20), x0 = safe.x + M, x1 = safe.xMax - M, W = x1 - x0, top = safe.y + U(12), bottom = safe.yMax - U(12);
        float PW = Mathf.Clamp(W * 0.36f, U(250), U(320));
        UiKit.Gradient(new Rect(0, 0, x0 + PW + U(140), sh), sideShade, clear0, 0f, true);
        UiKit.Gradient(new Rect(0, 0, sw, sh * 0.25f), shadeTop, clear0);
        studio.Spin(new Rect(x0 + PW + U(12), top + U(56), x1 - (x0 + PW + U(12)), bottom - top - U(56)), true);
        PageHeader(x0, top, "Your car", "GARAGE", KeyGarage);

        var ch = Choice.None;
        float y = top + U(60), th = U(82);
        cls = SetupTile(new Rect(x0, y, PW, th), "Car class", CarConfig.ClassNames[cls], cls, CarConfig.ClassNames.Length, true, 20, UiKit.Enter(KeyGarage, 1));
        y += th + U(10);
        bool inSeason = mode == ModeChampionship && Season.Active && !Season.Over;
        int li = Livery.PlayerIndex;
        float el = UiKit.Enter(KeyGarage, 2);
        int nl = SetupTile(new Rect(x0, y, PW, th), "Livery", Livery.Player[li].name, li, Livery.Player.Length, true, 21, el);
        // the colours of the livery under its name
        UiKit.PushAlpha(el);
        var lv = Livery.Player[li];
        float sx = x0 + PW * 0.5f - U(25), sy = UiKit.Rise(new Rect(0, y + th - U(13), 0, 0), el, 8f).y;
        UiKit.Rect(new Rect(sx, sy, U(22), U(6)), lv.primary, 3f);
        UiKit.Rect(new Rect(sx + U(26), sy, U(12), U(6)), lv.secondary, 3f);
        UiKit.Rect(new Rect(sx + U(42), sy, U(8), U(6)), lv.accent, 3f);
        UiKit.PopAlpha();
        if (nl != li) { Livery.PlayerIndex = nl; restyleDue = true; ch = Choice.Livery; }
        y += th + U(10);

        // ---- coming soon
        float en = UiKit.Enter(KeyGarage, 3);
        UiKit.PushAlpha(en);
        var note = UiKit.Rise(new Rect(x0, y, PW, Mathf.Min(U(92), bottom - y)), en, 8f);
        UiKit.Rect(note, new Color(1, 1, 1, 0.035f), 14f);
        UiKit.Outline(note, new Color(1, 1, 1, 0.07f), 14f, 0.7f);
        UiKit.Eyebrow(new Rect(note.x + U(12), note.y + U(10), note.width - U(24), U(14)), "Coming soon", UiKit.Warn, 8.5f);
        UiKit.Text(new Rect(note.x + U(12), note.y + U(26), note.width - U(24), note.height - U(30)),
            inSeason ? "Car setup and a livery editor. Championship rounds use your team's colours." : "Car setup, tuning and a livery editor.",
            UiKit.Regular, 10.5f, UiKit.Sub, TextAnchor.UpperLeft, true);
        UiKit.PopAlpha();
        return ch;
    }
}
