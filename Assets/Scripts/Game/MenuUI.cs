// MenuUI.cs — the front end, drawn with IMGUI (UiKit.cs design system) from GameManager.OnGUI:
//  * Start menu: a hero layout. Left: the circuit (type, big name, country), data chips (length, turns, real pole)
//    and a large anti-aliased track map (start/finish, direction, sector splits); below it a swipeable carousel of
//    circuit cards. Right: the game mode (segmented), the session options as compact pills (car, rivals, level,
//    grid, laps, qualifying, weather, time of day) and the big red primary action. Settings = round icon, top right.
//  * Loading: centred circuit name, a thin red shimmer and a tip; drawn for one frame before the (slow) build.
//  * Results: your position large, points / championship tags, a podium for the top 3 with team colours, and a
//    timing table (best lap purple for the fastest). "Replay" (last 60 s, TV cameras) / "Menu" / "Race again".
// A plain C# class: GameManager owns the game settings and hands them in by ref; this class draws, keeps the page
// that is up and the race-length setting. The game is paused while any page is showing (GameManager checks Showing).
// Layout units: 390 units = screen height (UiKit.U). Touch targets are 42+ units; layouts keep to Screen.safeArea
// and fit 16:9 to 21:9. Strings shown every frame are built once (on a change), not per frame.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public sealed class MenuUI
{
    public enum Page { None, Start, Loading, Results, Season, QualiResult }
    public enum Choice { None, RaceAgain, Replay, Settings, StartRace }

    // game mode (saved as "gameMode"): a quick race, a race weekend (qualifying + race), or the championship (SeasonUI.cs)
    public const int ModeQuick = 0, ModeWeekend = 1, ModeChampionship = 2;
    public static readonly string[] ModeNames = { "Quick race", "Race weekend", "Championship" };
    public int mode;
    public static readonly int[] QualiMinutes = { 5, 8, 12, 18 }; // qualifying length, game minutes (saved as "qualiLen")
    public int qualiIdx = 1;
    public bool quali;                                     // the session the loading page is starting is qualifying
    public readonly SeasonUI season;
    public string resultNote;                              // GameManager: grid -> finish, fastest lap, points (results page)
    public bool seasonNext;                                // results of a championship round: "Next round" instead of "Race again"

    public Page page = Page.Start;
    public bool Showing => page != Page.None;
    public bool practice;                                  // the session the loading page is starting
    public static readonly int[] LapCounts = { 1, 3, 5, 10 };
    public int lapsIdx;                                    // race length, saved as "laps"
    // where the player starts a race: 0 Back, 1 Random, 2 Pole, 3.. = P2, P3, ... (saved as "gridPos")
    public static readonly string[] GridNames = { "Back", "Random", "Pole", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "P9", "P10", "P11", "P12", "P13", "P14", "P15", "P16" };
    public int gridIdx;
    public void SetGrid(int v) { gridIdx = Mathf.Clamp(v, 0, GridNames.Length - 1); PlayerPrefs.SetInt("gridPos", gridIdx); }
    public bool canReplay;                                 // GameManager: a recording of the race's end is there to play

    // one car in the results; GameManager fills these when the player takes the flag
    public struct Row { public string name; public Color colour; public bool player; public double progress, best; }

    // the menu colours, shared with other screens (kept for callers; the design system is UiKit)
    public static readonly Color Accent = UiKit.Accent;      // #E10600
    public static readonly Color PanelCol = new Color(0.035f, 0.04f, 0.055f, 0.9f);
    public static readonly Color Dim = new Color(1, 1, 1, 0.6f);

    readonly string[] mapNames; readonly int playgroundIdx; readonly int[] rivalCounts; readonly string[] diffNames;
    readonly MapPreview[] previews; readonly double[] lengths;   // per circuit, built when first needed
    int loadShownFrame = -1;

    // ---- per-circuit text, built once
    sealed class CardInfo { public string upper, name, number, country, eyebrow, corners, pole, length = "—"; }
    readonly CardInfo[] cards;
    // option value strings (no string building per frame)
    readonly string[] rivText, lapsText, qualiText;
    static readonly string[] Ord = MakeOrd(40);
    static string[] MakeOrd(int n) { var a = new string[n + 1]; for (int i = 0; i <= n; i++) a[i] = i.ToString(); return a; }
    static readonly string[] ModeHint = { "Straight to the grid", "Practice  ·  qualifying  ·  race", null };

    static readonly string[] Tips =
    {
        "Brake in a straight line, then ease off the pedal as you turn in.",
        "Purple is the fastest time of the session; green is your personal best.",
        "Soft tyres grip the most but wear the fastest.",
        "On a damp track a drying line forms: stay on it for grip.",
        "In a race weekend, qualifying sets your place on the grid.",
        "Settings  ·  Assists: traction control, ABS, steering and braking help, racing line.",
        "Drag a thumb or tilt the phone to steer: change it in Settings  ·  Controls.",
    };
    int tipIdx;

    // results
    Row[] results; string[] gaps, bests; int fastestRow = -1; int resLaps, resPos, resStops; double resTime, resPenalty;
    string resHead, resPosStr, resOfStr, resTimeStr, resBestStr, resStopsStr, resPenStr; bool resPlayerFastest;
    string noteParsed; string noteLine, notePts, noteChamp;   // resultNote split for display

    public MenuUI(string[] mapNames, int playgroundIdx, int[] rivalCounts, string[] diffNames)
    {
        this.mapNames = mapNames; this.playgroundIdx = playgroundIdx; this.rivalCounts = rivalCounts; this.diffNames = diffNames;
        previews = new MapPreview[mapNames.Length]; lengths = new double[mapNames.Length];
        lapsIdx = Mathf.Clamp(PlayerPrefs.GetInt("laps", 1), 0, LapCounts.Length - 1);
        gridIdx = Mathf.Clamp(PlayerPrefs.GetInt("gridPos", 0), 0, GridNames.Length - 1);
        mode = Mathf.Clamp(PlayerPrefs.GetInt("gameMode", 0), 0, ModeNames.Length - 1);
        qualiIdx = Mathf.Clamp(PlayerPrefs.GetInt("qualiLen", 1), 0, QualiMinutes.Length - 1);
        season = new SeasonUI(this);

        rivText = new string[rivalCounts.Length];
        for (int i = 0; i < rivalCounts.Length; i++) rivText[i] = rivalCounts[i] == 0 ? "None" : rivalCounts[i] + " cars";
        lapsText = new string[LapCounts.Length];
        for (int i = 0; i < LapCounts.Length; i++) lapsText[i] = LapCounts[i] + (LapCounts[i] == 1 ? " lap" : " laps");
        qualiText = new string[QualiMinutes.Length];
        for (int i = 0; i < QualiMinutes.Length; i++) qualiText[i] = QualiMinutes[i] + " min";
        ModeHint[2] = Season.Rounds + " rounds  ·  full grid  ·  points";

        cards = new CardInfo[mapNames.Length];
        for (int i = 0; i < mapNames.Length; i++)
        {
            var c = new CardInfo { name = mapNames[i], upper = mapNames[i].ToUpperInvariant(), number = (i + 1).ToString("00") };
            string total = mapNames.Length.ToString("00");
            if (i == playgroundIdx || i >= Circuits.All.Length)
            {
                c.country = "Open test area"; c.eyebrow = "Free driving  ·  " + c.number + " / " + total; c.corners = "—"; c.pole = "—"; c.length = "Open";
            }
            else
            {
                var def = Circuits.All[i];
                c.country = Country(def.id);
                string type = def.style == CircuitStyle.Street ? "Street circuit" : def.style == CircuitStyle.Test ? "Test track" : "Permanent circuit";
                c.eyebrow = type + "  ·  " + c.number + " / " + total;
                int t = Turns(def.id); c.corners = t > 0 ? t.ToString() : "—";
                c.pole = def.realPole > 0 ? Fmt(def.realPole) : "—";
            }
            cards[i] = c;
        }
    }

    // facts the track data does not carry (official figures)
    static string Country(string id) => id switch
    {
        "monaco" => "Monaco", "spa" => "Belgium", "monza" => "Italy", "silverstone" => "Great Britain", "suzuka" => "Japan",
        "austin" => "United States", "budapest" => "Hungary", "catalunya" => "Spain", "spielberg" => "Austria", "zandvoort" => "Netherlands",
        _ => "Test circuit",
    };
    static int Turns(string id) => id switch
    {
        "monaco" => 19, "spa" => 19, "monza" => 11, "silverstone" => 18, "suzuka" => 18, "austin" => 20, "budapest" => 14,
        "catalunya" => 14, "spielberg" => 10, "zandvoort" => 14, _ => 0,
    };

    // true once the "Loading…" frame has been on screen: GameManager.Update then builds the circuit
    public bool LoadDue => page == Page.Loading && loadShownFrame >= 0 && Time.frameCount > loadShownFrame + 1;

    public void BeginLoading(bool practiceSession, bool qualifying = false)
    {
        practice = practiceSession; quali = qualifying; loadShownFrame = -1; page = Page.Loading;
        tipIdx = (tipIdx + 1 + (int)(Time.realtimeSinceStartup * 7f)) % Tips.Length;
    }

    public void ShowResults(Row[] rows, string circuit, int laps, double raceTime, double refLap, int pitStops = 0, double penalty = 0)
    {
        var list = new List<Row>(rows);
        list.Sort((a, b) => b.progress.CompareTo(a.progress));
        results = list.ToArray(); gaps = new string[results.Length]; bests = new string[results.Length]; resultNote = null; seasonNext = false;
        fastestRow = -1; for (int i = 0; i < results.Length; i++) if (results[i].best > 0 && !double.IsNaN(results[i].best) && (fastestRow < 0 || results[i].best < results[fastestRow].best)) fastestRow = i;
        resLaps = laps; resTime = raceTime; resPos = 1; resPlayerFastest = false; resStops = pitStops; resPenalty = penalty;
        double lead = results.Length > 0 ? results[0].progress : 0, myBest = double.NaN;
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].player) { resPos = i + 1; myBest = results[i].best; resPlayerFastest = i == fastestRow; }
            double behind = lead - results[i].progress;
            int lapsDown = (int)System.Math.Floor(behind);
            gaps[i] = i == 0 ? "Winner" : lapsDown >= 1 ? $"+{lapsDown} lap{(lapsDown > 1 ? "s" : "")}" : $"+{behind * refLap:0.0}s";
            bests[i] = Fmt(results[i].best);
        }
        resHead = $"Race result  ·  {circuit}  ·  {laps} lap{(laps > 1 ? "s" : "")}";
        resPosStr = "P" + resPos; resOfStr = results.Length > 1 ? "of " + results.Length : "time trial";
        resTimeStr = Fmt(resTime); resBestStr = Fmt(myBest);
        resStopsStr = resStops > 0 ? resStops.ToString() : null;
        resPenStr = resPenalty > 0 ? $"+{resPenalty:0} s" : null;
        noteParsed = null; noteLine = notePts = noteChamp = null;
        page = Page.Results;
    }

    // ------------------------------------------------------------------ drawing
    internal static float S(float v) => v * Screen.height / 390f;
    static float U(float v) => UiKit.U(v);
    internal static void Fill(Rect r, Color c) => UiKit.Rect(r, c);

    // Draws the page that is up. loaded / loadedFor: the circuit GameManager has built now (its preview needs no rebuild).
    public Choice Draw(ref int map, ref int cls, ref int riv, ref int diff, Track loaded, int loadedFor)
    {
        if (page == Page.Start) { if (DrawStart(ref map, ref cls, ref riv, ref diff, loaded, loadedFor)) return Choice.Settings; }
        else if (page == Page.Loading) DrawLoading(map);
        else if (page == Page.Results) return DrawResults();
        else if (page == Page.Season) return season.DrawHub(ref map);
        else if (page == Page.QualiResult) return season.DrawQuali();
        return Choice.None;
    }

    // ================================================================== start menu
    int lastMap = -1, mapDir = 1; const int KeyMap = 0x5A17;
    int ctaKey = -1; string ctaSub, ctaQualiSub;

    // returns true when the settings icon is tapped (GameManager then shows the settings screen over the menu)
    bool DrawStart(ref int map, ref int cls, ref int riv, ref int diff, Track loaded, int loadedFor)
    {
        UiKit.Backdrop(1f);
        var safe = UiKit.Safe;
        float M = U(22), x0 = safe.x + M, x1 = safe.xMax - M, W = x1 - x0;
        float top = safe.y + U(14), bottom = safe.yMax - U(26);
        float R = Mathf.Clamp(W * 0.42f, U(300), U(380)), gap = U(18), L = Mathf.Max(U(200), W - R - gap), px = x1 - R;
        float bodyY = top + U(52);
        bool champ = mode == ModeChampionship;

        // ---- top bar: brand, mode, settings
        float e = UiKit.Enter("start", 0);
        UiKit.PushAlpha(e);
        UiKit.Bar(new Rect(x0, top + U(10), U(4), U(22)), UiKit.Accent);
        UiKit.Text(new Rect(x0 + U(12), top + U(4), L, U(34)), Brand, UiKit.Black, 17f, UiKit.TextCol);
        int nm = UiKit.Segmented(new Rect(px, top + U(2), R - U(54), U(40)), ModeNames, mode, false, true, 11.5f);
        if (nm != mode) { mode = nm; PlayerPrefs.SetInt("gameMode", mode); champ = mode == ModeChampionship; }
        bool openSettings = UiKit.IconButton(new Rect(x1 - U(44), top, U(44), U(44)), UiKit.IconSettings);
        UiKit.PopAlpha();

        // ---- circuit carousel (handled before the hero so a tap there changes the hero this frame)
        var car = new Rect(x0, bottom - U(58), L, U(58));
        map = Carousel(car, map);
        bool pg = map == playgroundIdx;
        if (map != lastMap) { UiKit.Mark(KeyMap); mapDir = lastMap < 0 || map > lastMap ? 1 : -1; lastMap = map; carCentre = true; }
        float mt = UiKit.EaseOut(UiKit.Since(KeyMap) / 0.4f);
        var info = cards[map];

        // ---- hero: eyebrow, name, country
        float e1 = UiKit.Enter("start", 1), e2 = UiKit.Enter("start", 2), e3 = UiKit.Enter("start", 3);
        UiKit.PushAlpha(e1 * mt);
        UiKit.Eyebrow(UiKit.Slide(new Rect(x0, bodyY, L, U(14)), mt, 10f * mapDir), info.eyebrow, UiKit.AccentHi, 9.5f);
        UiKit.PopAlpha();
        float ns = UiKit.Fit(info.upper, UiKit.Black, 46f, L, 24f);
        UiKit.PushAlpha(e2 * mt);
        UiKit.Text(UiKit.Slide(UiKit.Rise(new Rect(x0, bodyY + U(12), L, U(52)), e2, 12f), mt, 18f * mapDir), info.upper, UiKit.Black, ns, UiKit.TextCol);
        UiKit.PopAlpha();
        UiKit.PushAlpha(e3 * mt);
        UiKit.Text(UiKit.Slide(new Rect(x0, bodyY + U(62), L, U(18)), mt, 12f * mapDir), info.country, UiKit.Semi, 13.5f, UiKit.Sub);
        UiKit.PopAlpha();

        // ---- map (built first: it measures the length for the chips)
        var mapR = new Rect(x0 + U(126), bodyY + U(88), L - U(126), car.y - U(12) - (bodyY + U(88)));
        var pv = pg ? null : Preview(map, loaded, loadedFor);
        float e5 = UiKit.Enter("start", 5, 0.04f, 0.6f);
        UiKit.PushAlpha(e5 * (0.35f + 0.65f * mt));
        if (pv != null) DrawMap(UiKit.Scale(mapR, 0.96f + 0.04f * mt), pv, true);
        else if (pg) DrawTestArea(mapR);
        UiKit.PopAlpha();

        // ---- data chips (a column left of the map)
        float cy = bodyY + U(92), chW = U(114), chH = U(40);
        for (int i = 0; i < 3; i++)
        {
            float ec = UiKit.Enter("start", 4 + i);
            UiKit.PushAlpha(ec);
            var r = UiKit.Rise(new Rect(x0, cy + i * (chH + U(7)), chW, chH), ec, 8f);
            if (pg)
            {
                if (i == 0) UiKit.Chip(r, "Timing", "Off", UiKit.TextCol);
                else if (i == 1) UiKit.Chip(r, "Drive", "Free", UiKit.TextCol);
                else UiKit.Chip(r, "Tests", "0-100 · Vmax", UiKit.TextCol, 11.5f);
            }
            else if (i == 0) UiKit.Chip(r, "Length", info.length, UiKit.TextCol);
            else if (i == 1) UiKit.Chip(r, "Turns", info.corners, UiKit.TextCol);
            else UiKit.Chip(r, "Target pole", info.pole, UiKit.TextCol);
            UiKit.PopAlpha();
        }

        // ---- session panel
        var panel = new Rect(px, bodyY, R, bottom - bodyY);
        float ep = UiKit.Enter("start", 2, 0.04f, 0.45f);
        UiKit.PushAlpha(ep);
        panel = UiKit.Slide(panel, ep, 26f);
        UiKit.Panel(panel, 16f);
        float pad = U(14), ix = panel.x + pad, iw = panel.width - 2 * pad;
        UiKit.Eyebrow(new Rect(ix, panel.y + U(9), iw, U(16)), "Session", UiKit.Sub);
        UiKit.Text(new Rect(ix, panel.y + U(9), iw, U(16)), ModeHint[mode], UiKit.Regular, 9.5f, UiKit.Faint, TextAnchor.MiddleRight);

        float pillH = U(42), pg2 = U(6), colW = (iw - U(8)) / 2, gy = panel.y + U(32);
        for (int i = 0; i < 8; i++)
        {
            int col = i / 4, row = i % 4;
            var r = new Rect(ix + col * (colW + U(8)), gy + row * (pillH + pg2), colW, pillH);
            float ei = UiKit.Enter("start", 4 + row + col);
            UiKit.PushAlpha(ei);
            r = UiKit.Rise(r, ei, 8f);
            switch (i)
            {
                case 0: cls = UiKit.Pill(r, "Car", CarConfig.ClassNames[cls], cls, CarConfig.ClassNames.Length); break;
                case 1: { int n = UiKit.Pill(r, "Rivals", champ ? "Full grid" : rivText[riv], riv, rivalCounts.Length, !pg && !champ); riv = n; break; }
                case 2: diff = UiKit.Pill(r, "Rival level", diffNames[diff], diff, diffNames.Length, !pg); break;
                case 3: { int n = UiKit.Pill(r, "Grid", GridNames[gridIdx], gridIdx, GridNames.Length, !pg); if (n != gridIdx) SetGrid(n); break; }
                case 4: { int n = UiKit.Pill(r, "Race length", lapsText[lapsIdx], lapsIdx, LapCounts.Length, !pg); if (n != lapsIdx) { lapsIdx = n; PlayerPrefs.SetInt("laps", lapsIdx); } break; }
                case 5: { int n = UiKit.Pill(r, "Qualifying", qualiText[qualiIdx], qualiIdx, QualiMinutes.Length, !pg && mode != ModeQuick); if (n != qualiIdx) { qualiIdx = n; PlayerPrefs.SetInt("qualiLen", qualiIdx); } break; }
                case 6: Weather.Index = UiKit.Pill(r, "Weather", Weather.Names[Weather.Index], Weather.Index, Weather.Names.Length); break; // applied and saved at the start of the session
                case 7:
                {
                    // Night on street circuits only (elsewhere it shows and uses Evening); saved at once, used by the next build
                    bool street = !pg && map < Circuits.All.Length && Circuits.All[map].style == CircuitStyle.Street;
                    int cur = (int)TimeOfDay.For(street), n = UiKit.Pill(r, "Time of day", TimeOfDay.Names[cur], cur, TimeOfDay.Count(street));
                    if (n != cur) TimeOfDay.Set(n, false);
                    break;
                }
            }
            UiKit.PopAlpha();
        }

        // ---- actions
        int key = lapsIdx * 1000 + riv * 10 + qualiIdx * 100000 + (champ ? 7 : 0);
        if (key != ctaKey)
        {
            ctaKey = key;
            ctaSub = champ ? "Standings  ·  next round" : lapsText[lapsIdx] + "  ·  " + (rivalCounts[riv] == 0 ? "solo" : rivalCounts[riv] + " rivals");
            ctaQualiSub = qualiText[qualiIdx] + " session, then race";
        }
        float bh = U(52), by = panel.yMax - pad - bh;
        float eb = UiKit.Enter("start", 9);
        UiKit.PushAlpha(eb);
        by += U(10) * (1f - eb);
        if (mode == ModeWeekend && !pg)
        {
            // race weekend: practice, or qualifying -> race, or straight to the race from the chosen grid position
            float w1 = iw * 0.27f, w2 = iw * 0.27f, w3 = iw - w1 - w2 - U(12);
            if (UiKit.Button(new Rect(ix, by, w1, bh), "Practice", UiKit.Look.Ghost, true, 11.5f)) BeginLoading(true);
            if (UiKit.Button(new Rect(ix + w1 + U(6), by, w2, bh), "Skip quali", UiKit.Look.Secondary, true, 11.5f)) BeginLoading(false);
            if (UiKit.Button(new Rect(ix + w1 + w2 + U(12), by, w3, bh), "Qualifying", UiKit.Look.Primary, true, 16f, ctaQualiSub)) BeginLoading(false, true);
        }
        else
        {
            float w1 = iw * 0.34f, w2 = iw - w1 - U(8);
            if (UiKit.Button(new Rect(ix, by, w1, bh), pg ? "Drive" : "Practice", UiKit.Look.Secondary, true, 12.5f)) BeginLoading(true);
            bool can = !pg || champ;
            if (UiKit.Button(new Rect(ix + w1 + U(8), by, w2, bh), champ ? "Championship" : "Race", UiKit.Look.Primary, can, champ ? 15f : 18f, pg && !champ ? "Not on the test area" : ctaSub))
            { if (champ) season.Open(); else BeginLoading(false); }
        }
        UiKit.PopAlpha();
        UiKit.PopAlpha(); // panel

        // licence credits for the car and steering-wheel models (CC BY): always on the start menu
        UiKit.Text(new Rect(x0, safe.yMax - U(29), W, U(12)), CarModel.CarCredit, UiKit.Regular, 7.5f, UiKit.A(UiKit.Faint, 0.85f * e));
        UiKit.Text(new Rect(x0, safe.yMax - U(18), W, U(12)), CarModel.WheelCredit + "  \u00b7  " + RealWorld.Attribution, UiKit.Regular, 7.5f, UiKit.A(UiKit.Faint, 0.85f * e));

        BuildThumbnails(loaded, loadedFor);
        return openSettings;
    }

    static string brand;
    static string Brand => brand ??= Application.productName.ToUpperInvariant();

    // ---- circuit carousel: swipe sideways (with a fling), tap a card to pick it; never fires on a swipe
    float carScroll, carTarget, carVel, carPressX, carPressScroll, carLastX, carLastT; bool carDrag, carCentre = true, carInit;
    int Carousel(Rect a, int map)
    {
        int n = mapNames.Length;
        float cw = U(146), cg = U(8), step = cw + cg, content = n * step - cg, maxS = Mathf.Max(0, content - a.width);
        int id = GUIUtility.GetControlID(FocusType.Passive);
        var e = Event.current;
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (e.button == 0 && a.Contains(e.mousePosition))
                {
                    GUIUtility.hotControl = id; carDrag = false; carPressX = carLastX = e.mousePosition.x; carPressScroll = carScroll;
                    carVel = 0; carLastT = Time.realtimeSinceStartup; e.Use();
                }
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == id)
                {
                    float dx = e.mousePosition.x - carPressX;
                    if (!carDrag && Mathf.Abs(dx) > U(8)) carDrag = true;
                    if (carDrag)
                    {
                        float s = carPressScroll - dx;
                        if (s < 0) s *= 0.35f; else if (s > maxS) s = maxS + (s - maxS) * 0.35f; // rubber band at the ends
                        carScroll = carTarget = s;
                        float now = Time.realtimeSinceStartup, dt = Mathf.Max(1f / 120f, now - carLastT);
                        carVel = Mathf.Lerp(carVel, -(e.mousePosition.x - carLastX) / dt, 0.6f);
                        carLastX = e.mousePosition.x; carLastT = now;
                    }
                    e.Use();
                }
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == id)
                {
                    GUIUtility.hotControl = 0;
                    if (!carDrag)
                    {
                        float lx = e.mousePosition.x - a.x + carScroll; int k = Mathf.FloorToInt(lx / step);
                        if (a.Contains(e.mousePosition) && k >= 0 && k < n && lx - k * step <= cw) map = k;
                    }
                    else carTarget = carScroll + Mathf.Clamp(carVel, -U(3000), U(3000)) * 0.25f;
                    carDrag = false; e.Use();
                }
                break;
            case EventType.ScrollWheel:
                if (a.Contains(e.mousePosition)) { carTarget += (Mathf.Abs(e.delta.x) > Mathf.Abs(e.delta.y) ? e.delta.x : e.delta.y) * U(26); e.Use(); }
                break;
        }
        if (carCentre) { carTarget = Mathf.Clamp(map * step - (a.width - cw) * 0.5f, 0, maxS); carCentre = false; if (!carInit) { carScroll = carTarget; carInit = true; } }
        if (e.type == EventType.Repaint && GUIUtility.hotControl != id)
        {
            carTarget = Mathf.Clamp(carTarget, 0, maxS);
            carScroll = Mathf.Lerp(carScroll, carTarget, 1f - Mathf.Exp(-11f * Time.unscaledDeltaTime));
        }

        // cards (clipped to the column; they fade as they leave it)
        var g = new Rect(a.x, a.y - U(8), a.width, a.height + U(16));
        GUI.BeginGroup(g);
        float ch = a.height;
        for (int k = 0; k < n; k++)
        {
            float x = k * step - carScroll;
            if (x > g.width || x + cw < 0) continue;
            float vis = Mathf.Clamp01(Mathf.Min(x + cw * 0.5f, g.width - x - cw * 0.5f) / (cw * 0.5f));
            float ent = UiKit.Enter("start", 6 + Mathf.Max(0, k - Mathf.FloorToInt(carScroll / step)));
            DrawCard(new Rect(x, U(8) + U(10) * (1f - ent), cw, ch), k, k == map, vis * ent);
        }
        GUI.EndGroup();
        // a hint of more to the sides
        if (carScroll > 1f) UiKit.Icon(new Rect(a.x - U(17), a.center.y - U(6), U(12), U(12)), UiKit.IconBack, UiKit.Faint);
        if (carScroll < maxS - 1f) UiKit.Icon(new Rect(a.xMax + U(4), a.center.y - U(6), U(12), U(12)), UiKit.IconNext, UiKit.Faint);
        return map;
    }

    static readonly Color cardCol = new Color(0.09f, 0.10f, 0.13f, 0.82f), cardSel = new Color(0.15f, 0.16f, 0.20f, 0.95f);
    void DrawCard(Rect r, int k, bool sel, float alpha)
    {
        if (alpha <= 0.01f) return;
        float s = UiKit.Smooth(0x6C00 + k, sel ? 1f : 0f, 12f);
        UiKit.PushAlpha(alpha);
        if (s > 0.02f) UiKit.Shadow(r, 14f, 6f, 0.3f * s, 2f);
        UiKit.Rect(r, Color.Lerp(cardCol, cardSel, s), 14f);
        UiKit.Outline(r, Color.Lerp(new Color(1, 1, 1, 0.07f), UiKit.Accent, s), 14f, 0.7f + 0.5f * s);
        var c = cards[k];
        var pv = k < previews.Length ? previews[k] : null;
        float thumb = pv != null ? U(44) : 0f, tx = r.x + U(12), tw = r.width - U(22) - thumb;
        UiKit.Text(new Rect(tx, r.y + U(6), tw, U(14)), c.number, UiKit.Black, 10f, Color.Lerp(UiKit.Faint, UiKit.AccentHi, s));
        UiKit.Text(new Rect(tx, r.y + U(19), tw, U(18)), c.name, UiKit.Bold, UiKit.Fit(c.name, UiKit.Bold, 12.5f, tw, 9.5f), Color.Lerp(UiKit.Sub, UiKit.TextCol, s));
        UiKit.Text(new Rect(tx, r.y + U(36), tw, U(14)), c.country, UiKit.Regular, 9f, UiKit.Faint);
        if (pv != null)
        {
            UiKit.PushAlpha(0.45f + 0.55f * s);
            DrawMap(new Rect(r.xMax - thumb - U(6), r.y + U(6), thumb, r.height - U(12)), pv, false);
            UiKit.PopAlpha();
        }
        UiKit.PopAlpha();
    }

    // previews of the other circuits for the cards: one every few frames once the menu is idle (each is a circuit
    // build, ~10-50 ms), so the entry animation stays smooth
    int thumbFrame;
    void BuildThumbnails(Track loaded, int loadedFor)
    {
        if (Event.current.type != EventType.Repaint || GUIUtility.hotControl != 0 || UiKit.Opened("start") < 1.2f || Time.frameCount - thumbFrame < 15) return;
        for (int k = 0; k < previews.Length && k < Circuits.All.Length; k++)
            if (previews[k] == null && k != playgroundIdx) { Preview(k, loaded, loadedFor); thumbFrame = Time.frameCount; return; }
    }

    void DrawTestArea(Rect r)
    {
        UiKit.Rect(r, new Color(1, 1, 1, 0.025f), 16f);
        UiKit.Outline(r, new Color(1, 1, 1, 0.08f), 16f, 0.7f);
        for (int i = 1; i < 6; i++) UiKit.Rect(new Rect(r.x + r.width * i / 6f, r.y + U(10), Mathf.Max(1f, U(0.5f)), r.height - U(20)), new Color(1, 1, 1, 0.04f));
        for (int i = 1; i < 4; i++) UiKit.Rect(new Rect(r.x + U(10), r.y + r.height * i / 4f, r.width - U(20), Mathf.Max(1f, U(0.5f))), new Color(1, 1, 1, 0.04f));
        UiKit.Eyebrow(new Rect(r.x, r.center.y - U(16), r.width, U(16)), "Open test area", UiKit.Sub, 10f, TextAnchor.MiddleCenter);
        UiKit.Text(new Rect(r.x, r.center.y + U(2), r.width, U(16)), "Free driving, 0-100 and top speed", UiKit.Regular, 10.5f, UiKit.Faint, TextAnchor.MiddleCenter);
    }

    // ------------------------------------------------------------------ track map
    // The outline is rendered once per circuit into a texture with mipmaps: a soft glow, a dark casing and a crisp
    // anti-aliased white line, north up. Start/finish, the direction arrow and the sector splits are drawn on top
    // with IMGUI so they stay sharp at any size.
    sealed class MapPreview { public Texture2D tex; public Vector2 start, s1, s2, dir; public float aspect; }
    static float[] bufGlow, bufCase, bufLine;

    MapPreview Preview(int map, Track loaded, int loadedFor)
    {
        if (map < 0 || map >= Circuits.All.Length || map == playgroundIdx) return null;
        if (previews[map] != null) return previews[map];
        var t = loaded != null && loadedFor == map ? loaded : Circuits.All[map].Build();
        lengths[map] = t.length; cards[map].length = (t.length / 1000.0).ToString("0.000") + " km";
        int W = Screen.height >= 1000 ? 512 : 384, H = W * 5 / 8, n = W * H;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var q in t.pts) { minX = System.Math.Min(minX, q.x); maxX = System.Math.Max(maxX, q.x); minY = System.Math.Min(minY, q.y); maxY = System.Math.Max(maxY, q.y); }
        float margin = W * 0.07f;
        float k = Mathf.Min((W - 2 * margin) / Mathf.Max(1f, (float)(maxX - minX)), (H - 2 * margin) / Mathf.Max(1f, (float)(maxY - minY)));
        float ox = W * 0.5f - (float)(minX + maxX) * 0.5f * k, oy = H * 0.5f - (float)(minY + maxY) * 0.5f * k;
        float PxX(double x) => ox + (float)x * k;
        float PxY(double y) => oy + (float)y * k;   // texture rows go up: north up

        if (bufGlow == null || bufGlow.Length < n) { bufGlow = new float[n]; bufCase = new float[n]; bufLine = new float[n]; }
        else { System.Array.Clear(bufGlow, 0, n); System.Array.Clear(bufCase, 0, n); System.Array.Clear(bufLine, 0, n); }
        float lineR = W / 170f, caseR = lineR + W / 260f + 0.5f, glowR = lineR * 5f;
        void Stamp(float fx, float fy)
        {
            int r = Mathf.CeilToInt(glowR) + 1, cx = Mathf.RoundToInt(fx), cy = Mathf.RoundToInt(fy);
            for (int dy = -r; dy <= r; dy++)
            {
                int iy = cy + dy; if (iy < 0 || iy >= H) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int ix = cx + dx; if (ix < 0 || ix >= W) continue;
                    float ddx = ix - fx, ddy = iy - fy, d = Mathf.Sqrt(ddx * ddx + ddy * ddy);
                    if (d > glowR) continue;
                    int i = iy * W + ix;
                    float gl = 1f - d / glowR; gl *= gl;
                    if (gl > bufGlow[i]) bufGlow[i] = gl;
                    float cs = Mathf.Clamp01(caseR + 0.5f - d); if (cs > bufCase[i]) bufCase[i] = cs;
                    float ln = Mathf.Clamp01(lineR + 0.5f - d); if (ln > bufLine[i]) bufLine[i] = ln;
                }
            }
        }
        for (int i = 0; i < t.N; i++)
        {
            // fill in between points so the line has no gaps at any texture size
            var a = t.pts[i]; var b = t.pts[(i + 1) % t.N];
            float ax = PxX(a.x), ay = PxY(a.y), bx = PxX(b.x), by = PxY(b.y);
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(Mathf.Abs(bx - ax), Mathf.Abs(by - ay))));
            for (int s = 0; s < steps; s++) Stamp(ax + (bx - ax) * s / steps, ay + (by - ay) * s / steps);
        }
        var px = new Color32[n];
        for (int i = 0; i < n; i++)
        {
            // premultiplied "over": glow, then the dark casing, then the white line
            float ga = 0.13f * bufGlow[i], ca = 0.62f * bufCase[i], la = bufLine[i];
            float r = ga, aa = ga;                       // white glow
            r = r * (1f - ca); aa = ca + aa * (1f - ca); // black casing
            r = la + r * (1f - la); aa = la + aa * (1f - la);
            float c = aa > 0.001f ? Mathf.Clamp01(r / aa) : 1f;
            byte cb = (byte)(c * 255f);
            px[i] = new Color32(cb, cb, (byte)Mathf.Min(255, cb + 3), (byte)(Mathf.Clamp01(aa) * 255f));
        }
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        tex.SetPixels32(px); tex.Apply(true, true);

        Vector2 Uv(TrackPoint p) => new Vector2(PxX(p.x) / W, 1f - PxY(p.y) / H);
        SectorMap.Ends(Circuits.All[map], out double f1, out double f2);
        var pvw = new MapPreview { tex = tex, aspect = W / (float)H, start = Uv(t.pts[0]) };
        pvw.s1 = Uv(t.pts[Mathf.Clamp((int)(f1 * t.N), 0, t.N - 1)]);
        pvw.s2 = Uv(t.pts[Mathf.Clamp((int)(f2 * t.N), 0, t.N - 1)]);
        var ahead = t.pts[Mathf.Min(t.N - 1, 6)];
        var d0 = new Vector2(PxX(ahead.x) - PxX(t.pts[0].x), -(PxY(ahead.y) - PxY(t.pts[0].y)));
        pvw.dir = d0.sqrMagnitude > 1e-6f ? d0.normalized : Vector2.right;
        previews[map] = pvw;
        return pvw;
    }

    static Rect FitAspect(Rect area, float aspect)
    {
        float w = area.width, h = w / aspect;
        if (h > area.height) { h = area.height; w = h * aspect; }
        return new Rect(area.center.x - w * 0.5f, area.center.y - h * 0.5f, w, h);
    }

    static void DrawMap(Rect area, MapPreview pv, bool overlays)
    {
        var dr = FitAspect(area, pv.aspect);
        GUI.DrawTexture(dr, pv.tex, ScaleMode.StretchToFill, true);
        if (!overlays) return;
        Vector2 P(Vector2 uv) => new Vector2(dr.x + uv.x * dr.width, dr.y + uv.y * dr.height);
        // sector splits: a small ring and the sector that starts there
        SectorMark(P(pv.s1), dr.center, "S2");
        SectorMark(P(pv.s2), dr.center, "S3");
        // start / finish: red dot in a white ring, and an arrow for the direction of travel
        var s = P(pv.start);
        UiKit.Glow(new Rect(s.x - U(16), s.y - U(16), U(32), U(32)), UiKit.A(UiKit.Accent, 0.6f));
        UiKit.Dot(s, 5f, Color.white); UiKit.Dot(s, 3.4f, UiKit.Accent);
        var m = GUI.matrix;
        var ap = s + pv.dir * U(14);
        GUIUtility.RotateAroundPivot(Mathf.Atan2(pv.dir.y, pv.dir.x) * Mathf.Rad2Deg, ap);
        UiKit.Icon(new Rect(ap.x - U(6), ap.y - U(6), U(12), U(12)), UiKit.IconNext, Color.white);
        GUI.matrix = m;
    }

    static void SectorMark(Vector2 p, Vector2 centre, string label)
    {
        UiKit.Dot(p, 3.6f, new Color(0.05f, 0.06f, 0.08f, 1f)); UiKit.Dot(p, 2.2f, UiKit.Sub);
        var o = p - centre; o = o.sqrMagnitude > 1f ? o.normalized : Vector2.up;
        var c = p + o * U(13);
        UiKit.Text(new Rect(c.x - U(12), c.y - U(7), U(24), U(14)), label, UiKit.Bold, 8.5f, UiKit.Sub, TextAnchor.MiddleCenter);
    }

    // ================================================================== loading
    void DrawLoading(int map)
    {
        float sw = Screen.width, sh = Screen.height;
        UiKit.Rect(new Rect(0, 0, sw, sh), UiKit.Ink);
        UiKit.Backdrop(1f);
        var info = cards[Mathf.Clamp(map, 0, cards.Length - 1)];
        float cy = sh * 0.46f;
        var pv = map >= 0 && map < previews.Length ? previews[map] : null; // only when already built: no extra build here
        if (pv != null) { UiKit.PushAlpha(0.16f); DrawMap(new Rect(sw * 0.5f - sh * 0.55f, cy - sh * 0.36f, sh * 1.1f, sh * 0.72f), pv, false); UiKit.PopAlpha(); }
        string kind = quali ? "Loading  ·  qualifying" : practice ? (map == playgroundIdx ? "Loading  ·  test area" : "Loading  ·  practice") : "Loading  ·  race";
        UiKit.Eyebrow(new Rect(0, cy - U(54), sw, U(16)), kind, UiKit.AccentHi, 10f, TextAnchor.MiddleCenter);
        UiKit.Text(new Rect(0, cy - U(40), sw, U(56)), info.upper, UiKit.Black, UiKit.Fit(info.upper, UiKit.Black, 50f, sw * 0.8f, 26f), UiKit.TextCol, TextAnchor.MiddleCenter);
        UiKit.Text(new Rect(0, cy + U(14), sw, U(20)), info.country, UiKit.Semi, 13.5f, UiKit.Sub, TextAnchor.MiddleCenter);
        // thin progress track with a moving shimmer
        float bw = U(220), bx = sw * 0.5f - bw * 0.5f, byy = cy + U(48), bh = Mathf.Max(2f, U(2.5f));
        UiKit.Rect(new Rect(bx, byy, bw, bh), new Color(1, 1, 1, 0.08f), 2f);
        float ph = Mathf.Repeat(Time.unscaledTime * 0.9f, 1f), seg = 0.32f;
        float a0 = Mathf.Clamp01(ph * (1f + seg) - seg), a1 = Mathf.Clamp01(ph * (1f + seg));
        if (a1 > a0) UiKit.Rect(new Rect(bx + bw * a0, byy, bw * (a1 - a0), bh), UiKit.Accent, 2f);
        // tip
        string tip = Tips[tipIdx];
        float tw = UiKit.Width(tip, UiKit.Semi, 11f), tagW = UiKit.TagWidth("TIP"), total = tagW + U(10) + tw, tx = sw * 0.5f - total * 0.5f, ty = sh - U(58);
        UiKit.Tag(new Rect(tx, ty + U(2), tagW, U(18)), "TIP", new Color(1, 1, 1, 0.10f), UiKit.Sub);
        UiKit.Text(new Rect(tx + tagW + U(10), ty, tw + U(4), U(22)), tip, UiKit.Semi, 11f, UiKit.Sub);
        // the frame has really been drawn: from the next-but-one frame on, the heavy build may start
        if (Event.current.type == EventType.Repaint && loadShownFrame < 0) loadShownFrame = Time.frameCount;
    }

    // ================================================================== results
    void ParseNote()
    {
        noteParsed = resultNote; noteLine = notePts = noteChamp = null;
        if (string.IsNullOrEmpty(resultNote)) return;
        var line = new System.Text.StringBuilder();
        foreach (var raw in resultNote.Split(new[] { "  ·  " }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            string p = raw.Replace("<b>", "").Replace("</b>", "").Replace(" → ", " to ").Trim();
            if (p.EndsWith(" pts")) notePts = p.ToUpperInvariant();
            else if (p.StartsWith("Championship")) noteChamp = p.ToUpperInvariant();
            else if (p.StartsWith("Fastest lap")) continue; // shown in the table (purple)
            else { if (line.Length > 0) line.Append("  ·  "); line.Append(p); }
        }
        noteLine = line.Length > 0 ? line.ToString() : null;
    }

    Choice DrawResults()
    {
        if (!ReferenceEquals(noteParsed, resultNote)) ParseNote();
        UiKit.Backdrop(1f);
        var safe = UiKit.Safe;
        float M = U(22), x0 = safe.x + M, x1 = safe.xMax - M, W = x1 - x0, top = safe.y + U(14);
        int n = results.Length;
        const string scr = "results";

        // ---- podium (right of the header)
        float pw = U(104), pgap = U(8), podW = 3 * pw + 2 * pgap, podBase = top + U(118);
        float headW = W - podW - U(20);
        for (int slot = 0; slot < 3; slot++)
        {
            int pos = slot == 0 ? 1 : slot == 1 ? 0 : 2;     // P2 left, P1 middle, P3 right
            if (pos >= n) continue;
            float h = pos == 0 ? U(112) : pos == 1 ? U(96) : U(84);
            float ep = UiKit.Enter(scr, 3 + (pos == 0 ? 2 : pos == 1 ? 1 : 0), 0.07f, 0.45f);
            var r = new Rect(x1 - podW + slot * (pw + pgap), podBase - h, pw, h);
            r = UiKit.Rise(r, ep, 18f);
            UiKit.PushAlpha(ep);
            Podium(r, pos);
            UiKit.PopAlpha();
        }

        // ---- header: eyebrow, big position, tags, note
        float e0 = UiKit.Enter(scr, 0), e1 = UiKit.Enter(scr, 1), e2 = UiKit.Enter(scr, 2);
        UiKit.PushAlpha(e0);
        UiKit.Eyebrow(new Rect(x0, top, headW, U(16)), resHead, UiKit.AccentHi, 9.5f);
        UiKit.PopAlpha();
        UiKit.PushAlpha(e1);
        var big = UiKit.Rise(new Rect(x0 - U(2), top + U(10), U(160), U(66)), e1, 14f);
        UiKit.Text(big, resPosStr, UiKit.Black, 62f, UiKit.TextCol);
        float bx = x0 + UiKit.Width(resPosStr, UiKit.Black, 62f) + U(12);
        float tagX = bx;
        if (notePts != null) { float w = UiKit.TagWidth(notePts, 10f); UiKit.Tag(new Rect(tagX, top + U(24), w, U(20)), notePts, UiKit.Accent, Color.white, 10f); tagX += w + U(6); }
        if (noteChamp != null) { float w = UiKit.TagWidth(noteChamp, 9f); UiKit.Tag(new Rect(tagX, top + U(24), w, U(20)), noteChamp, new Color(1, 1, 1, 0.12f), UiKit.TextCol, 9f); tagX += w + U(6); }
        float ly = notePts != null || noteChamp != null ? top + U(48) : top + U(30);
        UiKit.Text(new Rect(bx, ly, x0 + headW - bx, U(18)), resOfStr, UiKit.Bold, 13f, UiKit.Sub);
        if (noteLine != null) UiKit.Text(new Rect(bx, ly + U(16), x0 + headW - bx, U(16)), noteLine, UiKit.Regular, UiKit.Fit(noteLine, UiKit.Regular, 10.5f, x0 + headW - bx, 8f), UiKit.Faint);
        UiKit.PopAlpha();

        // data chips
        UiKit.PushAlpha(e2);
        float cx = x0, cyy = UiKit.Rise(new Rect(0, top + U(82), 0, 0), e2, 8f).y, chH = U(36);
        cx += ChipAt(cx, cyy, chH, "Race time", resTimeStr, UiKit.TextCol);
        cx += ChipAt(cx, cyy, chH, resPlayerFastest ? "Fastest lap" : "Best lap", resBestStr, resPlayerFastest ? UiKit.Purple : UiKit.TextCol);
        if (resStopsStr != null) cx += ChipAt(cx, cyy, chH, "Pit stops", resStopsStr, UiKit.TextCol);
        if (resPenStr != null) cx += ChipAt(cx, cyy, chH, "Jump start", resPenStr, UiKit.Warn);
        UiKit.PopAlpha();

        // ---- timing table
        float bh = U(46), by = safe.yMax - U(14) - bh;
        var tp = new Rect(x0, top + U(128), W, by - U(10) - (top + U(128)));
        float et = UiKit.Enter(scr, 6, 0.04f, 0.45f);
        UiKit.PushAlpha(et);
        tp = UiKit.Rise(tp, et, 12f);
        UiKit.Panel(tp, 14f);
        int cols = n > 8 ? 2 : 1, perCol = (n + cols - 1) / cols;
        float pad = U(10), tx = tp.x + pad, tw = tp.width - 2 * pad, colGap = U(18);
        if (cols == 1) tw = Mathf.Min(tw, U(560));
        float cw = (tw - (cols - 1) * colGap) / cols, headY = tp.y + U(6);
        float tableY = headY + U(16), rh = Mathf.Min(U(24), (tp.yMax - pad * 0.6f - tableY) / Mathf.Max(1, perCol));
        for (int c = 0; c < cols; c++)
        {
            float x = tx + c * (cw + colGap);
            UiKit.Eyebrow(new Rect(x + U(6), headY, cw * 0.1f, U(14)), "Pos", UiKit.Faint, 8f);
            UiKit.Eyebrow(new Rect(x + cw * 0.15f, headY, cw * 0.4f, U(14)), "Driver", UiKit.Faint, 8f);
            UiKit.Eyebrow(new Rect(x + cw * 0.55f, headY, cw * 0.22f, U(14)), "Best lap", UiKit.Faint, 8f);
            UiKit.Eyebrow(new Rect(x + cw * 0.76f, headY, cw * 0.22f, U(14)), "Gap", UiKit.Faint, 8f, TextAnchor.MiddleRight);
        }
        float fs = Mathf.Clamp(rh / U(1) * 0.52f, 9.5f, 12f);
        for (int i = 0; i < n; i++)
        {
            var r = results[i];
            float x = tx + (i / perCol) * (cw + colGap), y = tableY + (i % perCol) * rh;
            float ei = UiKit.Enter(scr, 7 + i % perCol, 0.025f);
            UiKit.PushAlpha(ei);
            UiKit.RowBg(new Rect(x, y + U(1), cw, rh - U(2)), i, r.player);
            var f = r.player ? UiKit.Bold : UiKit.Semi;
            UiKit.Text(new Rect(x + U(8), y, cw * 0.08f, rh), Ord[Mathf.Min(i + 1, Ord.Length - 1)], UiKit.Bold, fs, i < 3 ? UiKit.TextCol : UiKit.Sub);
            UiKit.Bar(new Rect(x + cw * 0.115f, y + rh * 0.24f, U(3), rh * 0.52f), r.colour);
            UiKit.Text(new Rect(x + cw * 0.15f, y, cw * 0.40f, rh), r.name, f, fs, UiKit.TextCol);
            bool fl = i == fastestRow;
            if (fl) UiKit.Dot(new Vector2(x + cw * 0.55f - U(6), y + rh * 0.5f), 2.2f, UiKit.Purple);
            UiKit.Text(new Rect(x + cw * 0.55f, y, cw * 0.22f, rh), bests[i], fl ? UiKit.Bold : UiKit.Semi, fs, fl ? UiKit.Purple : UiKit.Sub);
            UiKit.Text(new Rect(x + cw * 0.76f, y, cw * 0.22f, rh), gaps[i], UiKit.Semi, fs, i == 0 ? UiKit.TextCol : UiKit.Sub, TextAnchor.MiddleRight);
            UiKit.PopAlpha();
        }
        UiKit.PopAlpha();

        // ---- buttons
        float eb = UiKit.Enter(scr, 9);
        UiKit.PushAlpha(eb);
        int nb = canReplay ? 3 : 2;
        float bw = Mathf.Min(U(168), (W - (nb - 1) * U(10)) / nb), bxr = x1;
        Choice ch = Choice.None;
        if (seasonNext) { if (UiKit.Button(new Rect(bxr - bw, by, bw, bh), Season.Over ? "Standings" : "Next round", UiKit.Look.Primary, true, 15f)) season.Open(); }
        else if (UiKit.Button(new Rect(bxr - bw, by, bw, bh), "Race again", UiKit.Look.Primary, true, 15f)) ch = Choice.RaceAgain;
        if (UiKit.Button(new Rect(bxr - 2 * bw - U(10), by, bw, bh), "Menu", UiKit.Look.Secondary, true, 12.5f)) page = Page.Start;
        if (canReplay && UiKit.Button(new Rect(bxr - 3 * bw - 2 * U(10), by, bw, bh), "Replay", UiKit.Look.Ghost, true, 12.5f)) ch = Choice.Replay;
        UiKit.PopAlpha();
        return ch;
    }

    static float ChipAt(float x, float y, float h, string cap, string val, Color col)
    {
        float w = UiKit.ChipWidth(cap, val, 13f);
        UiKit.Chip(new Rect(x, y, w, h), cap, val, col, 13f);
        return w + U(8);
    }

    void Podium(Rect r, int pos)
    {
        var row = results[pos];
        UiKit.Panel(r, 14f, 1f, true);
        if (row.player) UiKit.Outline(r, UiKit.Accent, 14f, 1.2f);
        UiKit.Bar(new Rect(r.x + U(10), r.y + U(8), r.width - U(20), U(3)), row.colour);
        Color medal = pos == 0 ? UiKit.Gold : pos == 1 ? UiKit.Silver : UiKit.Bronze;
        float px = r.x + U(10), w = r.width - U(20);
        UiKit.Text(new Rect(px, r.y + U(14), w, U(30)), Ord[pos + 1], UiKit.Black, 26f, medal);
        UiKit.Text(new Rect(px, r.yMax - U(38), w, U(18)), row.name, UiKit.Bold, UiKit.Fit(row.name, UiKit.Bold, 12.5f, w, 9f), UiKit.TextCol);
        UiKit.Text(new Rect(px, r.yMax - U(21), w, U(14)), pos == 0 ? resTimeStr : gaps[pos], UiKit.Semi, 10f, UiKit.Sub);
    }

    internal static string Fmt(double t)
    {
        if (double.IsNaN(t) || double.IsInfinity(t) || t <= 0) return "—";
        int m = (int)(t / 60); return $"{m}:{t - m * 60:00.000}";
    }
}
