// MenuUI.cs — the front end, drawn with IMGUI in the "Paper" look (UiKit.cs) from GameManager.OnGUI. Every menu page
// has the same frame: the masthead ("APEX WORLD SERIES", nav PLAY / DRIVER / SETTINGS with the active one underlined in
// red) over a 1 px ink rule, the content, and a rule above the action row (text links left, the black button right).
//  * Home (Page.Start, MenuHome.cs): the giant red italic driver number and the driver, the 3D driver on paper in the
//    middle (MenuStudio.cs), the next race on the right, Championship / Race weekend links and RACE (the last mode and
//    circuit, straight to Setup); the licence credits.
//  * Play (MenuPlay.cs): 1 Mode · 2 Circuit · 3 Setup, one screen each, Back as a link (and Android back / Escape),
//    the black button bottom-right. Championship skips the circuit (its calendar decides) and opens the season hub
//    (SeasonUI.cs).
//  * Driver (MenuDriver.cs): the driver profile (name, number, country, level, safety rating, stats).
//  * Loading: the circuit name centred, a thin ink progress line and a tip; drawn for one frame before the (slow)
//    build. The menu's 3D is released before it and the unused assets are purged on it (once).
//  * Results: your position as the giant red italic number, points / championship tags, the race data, the timing
//    table (the fastest lap tagged FL, time penalties "+5s" as bad) and beside it the driver's progress
//    (MenuProgress.cs: +XP, the XP line filling, level up, the safety rating change and why).
//    "Replay" (last 60 s, TV cameras) / "Menu" (Home) / "Race again".
// A plain C# class: GameManager owns the game settings and hands them in by ref; this class draws, keeps the page
// that is up and the race-length setting. The game is paused while any page is showing (GameManager checks Showing).
// Layout units: 390 units = screen height (UiKit.U). Touch targets are 44+ units; layouts keep to Screen.safeArea
// and fit 16:9 to 21:9. Strings shown every frame are built once (on a change), not per frame.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public sealed partial class MenuUI
{
    // Start = the Home page. Play = the Mode / Circuit / Setup flow (step).
    public enum Page { None, Start, Loading, Results, Season, QualiResult, Play, Driver }
    // Livery: Setup changed Livery.PlayerIndex (GameManager puts it on the car)
    public enum Choice { None, RaceAgain, Replay, Settings, StartRace, Livery }

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
    // penalty: time penalties (s), already taken off progress (GameManagerProgress.cs); shown as "+5s"
    public struct Row { public string name; public Color colour; public bool player; public double progress, best, penalty; }

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
    Row[] results; string[] gaps, bests, pens; int fastestRow = -1; int resLaps, resPos, resStops; double resTime, resPenalty;
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
        GUIUtility.keyboardControl = 0;
        ReleaseStudio(); purged = false; // the menu's 3D goes before the circuit is built; the loading page purges once
    }

    // the menu's 3D (MenuStudio.cs) destroyed now; also called by GameManager.StartSession
    public void ReleaseStudio() { if (studio.Alive) { studio.Release(); releaseFrame = Time.frameCount; } }

    public void ShowResults(Row[] rows, string circuit, int laps, double raceTime, double refLap, int pitStops = 0, double penalty = 0)
    {
        var list = new List<Row>(rows);
        list.Sort((a, b) => b.progress.CompareTo(a.progress));
        results = list.ToArray(); gaps = new string[results.Length]; bests = new string[results.Length]; pens = new string[results.Length]; resultNote = null; seasonNext = false;
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
            pens[i] = results[i].penalty > 0 ? $"+{results[i].penalty:0}s" : null;
        }
        resHead = $"Race result  ·  {circuit}  ·  {laps} lap{(laps > 1 ? "s" : "")}";
        resPosStr = "P" + resPos; resOfStr = results.Length > 1 ? "of " + results.Length : "time trial";
        resTimeStr = Fmt(resTime); resBestStr = Fmt(myBest);
        resStopsStr = resStops > 0 ? resStops.ToString() : null;
        resPenStr = resPenalty > 0 ? $"+{resPenalty:0} s" : null;
        noteParsed = null; noteLine = notePts = noteChamp = null;
        MenuProfile.RecordRace(resPos, results.Length, resPlayerFastest); // career stats, XP and safety rating (MenuDriver.cs)
        page = Page.Results; reveal = false; // (the post-race sequence, PostRace.cs, hides this page and reopens it with the progress reveal: OpenResults)
    }

    // ------------------------------------------------------------------ drawing
    internal static float S(float v) => v * Screen.height / 390f;
    static float U(float v) => UiKit.U(v);
    internal static void Fill(Rect r, Color c) => UiKit.Rect(r, c);

    // ---- the menu's 3D studio, navigation, Android back
    public CarModel playerCar;                             // GameManager sets it (kept for its hook; the studio shows no car)
    readonly MenuStudio studio = new MenuStudio();
    int releaseFrame = -1, drawFrame = -10; bool purged = true, escDue, restyleDue;
    int navDir = 1; const int KeyNav = 0x5A20;

    static bool StudioPage(Page p) => p == Page.Start || p == Page.Play || p == Page.Driver || p == Page.Season;
    // the livery the driver's suit shows: the team's in a championship, else the player's own
    Livery PlayerLivery()
    {
        if (mode == ModeChampionship && Season.Active && !Season.Over) return Livery.ForTeam(Season.Data.team, Season.TeamColour(Season.Data.team));
        return Livery.Player[Livery.PlayerIndex];
    }

    public void GoHome() { page = Page.Start; navDir = -1; GUIUtility.keyboardControl = 0; UiKit.Mark(KeyNav); }

    // ---- the masthead nav (PLAY / DRIVER / SETTINGS; the wordmark = Home)
    Choice navChoice;
    // the page frame and masthead of a menu page; a tap on the nav acts at once (Settings is handed to GameManager)
    internal UiKit.Frame Masthead(int active, float footH = 82f)
    {
        var f = UiKit.PaperFrame(footH);
        Nav(UiKit.Masthead(f, active));
        return f;
    }
    void Nav(int hit)
    {
        switch (hit)
        {
            case UiKit.NavHome: if (page != Page.Start) GoHome(); break;
            case UiKit.NavPlay: if (page != Page.Play || step != StepMode) GoPlay(StepMode, page == Page.Play ? -1 : 1); break;
            case UiKit.NavDriver: if (page != Page.Driver) GoPage(Page.Driver); break;
            case UiKit.NavSettings: navChoice = Choice.Settings; break;
        }
    }
    public void GoPlay(int toStep, int dir)
    {
        if (toStep == StepMode && (page != Page.Play || step != StepMode)) ModeStatus();
        page = Page.Play; step = toStep; navDir = dir; UiKit.Mark(KeyNav);
    }
    void GoPage(Page p) { page = p; navDir = 1; GUIUtility.keyboardControl = 0; UiKit.Mark(KeyNav); }

    // Android back / Escape: one page or step back
    void Back()
    {
        switch (page)
        {
            case Page.Play:
                if (step == StepSetup) GoPlay(mode == ModeChampionship ? StepMode : StepCircuit, -1);
                else if (step == StepCircuit) GoPlay(StepMode, -1);
                else GoHome();
                break;
            case Page.Driver: case Page.Results: GoHome(); break;
            case Page.Season: season.Back(); break; // story cards, contract, full tables, then Home (SeasonUI.cs)
        }
    }

    // content of a page or step slides in from the side it was entered from (header and backdrop stay)
    Matrix4x4 navMatrix;
    void BeginNav()
    {
        float t = UiKit.EaseOut(UiKit.Since(KeyNav) / 0.3f);
        navMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Translate(new Vector3(U(36) * navDir * (1f - t), 0f, 0f)) * navMatrix;
        UiKit.PushAlpha(0.15f + 0.85f * t);
    }
    void EndNav() { UiKit.PopAlpha(); GUI.matrix = navMatrix; }

    // Draws the page that is up. loaded / loadedFor: the circuit GameManager has built now (its preview needs no rebuild).
    public Choice Draw(ref int map, ref int cls, ref int riv, ref int diff, Track loaded, int loadedFor)
    {
        var ev = Event.current;
        int f = Time.frameCount;
        if (f != drawFrame)
        {
            // the back key, once a frame; not on the frame the settings screen (drawn instead of this) closed with it
            escDue = Input.GetKeyDown(KeyCode.Escape) && f - drawFrame <= 1;
            drawFrame = f;
        }
        // the studio lives while a menu page that can show it is up
        if (StudioPage(page)) { if (!studio.Alive) studio.Create(PlayerLivery()); }
        else ReleaseStudio();
        if (ev.type == EventType.Repaint && studio.Alive)
        {
            if (restyleDue) { restyleDue = false; studio.Restyle(PlayerLivery()); }
            int shot = page == Page.Start ? MenuStudio.ShotHome : page == Page.Driver ? MenuStudio.ShotDriver : MenuStudio.ShotHidden;
            studio.Tick(shot, page == Page.Start);
        }
        if (escDue) { escDue = false; Back(); }
        // a nav item tapped on the settings page (drawn instead of this one): take it now
        navChoice = Choice.None;
        if (UiKit.NavRequest != UiKit.NavNone) { int nr = UiKit.NavRequest; UiKit.NavRequest = UiKit.NavNone; if (StudioPage(page)) Nav(nr); }

        var ch = Choice.None;
        switch (page)
        {
            case Page.Start: ch = DrawHome(ref map); break;
            case Page.Play: ch = DrawPlay(ref map, ref cls, ref riv, ref diff, loaded, loadedFor); break;
            case Page.Driver: DrawDriverPage(); break;
            case Page.Loading: DrawLoading(map); break;
            case Page.Results: ch = DrawResults(); break;
            case Page.Season: ch = season.DrawHub(ref map); break;
            case Page.QualiResult: ch = season.DrawQuali(); break;
        }
        return ch != Choice.None ? ch : navChoice;
    }

    // ================================================================== track map (the Circuit step)
    int lastMap = -1, mapDir = 1, mapStepDir; const int KeyMap = 0x5A17;

    // the neighbours of the circuit on screen (map - 1, map + 1) built ahead, one every few frames when the page is idle
    // (each is a circuit build, ~10-50 ms), so pressing < or > shows its map at once
    int prefetchFrame;
    void PrefetchNeighbours(int map, Track loaded, int loadedFor)
    {
        if (Event.current.type != EventType.Repaint || GUIUtility.hotControl != 0 || UiKit.Opened(KeyCircuit) < 0.8f || Time.frameCount - prefetchFrame < 15) return;
        int n = mapNames.Length;
        for (int d = -1; d <= 1; d += 2)
        {
            int k = ((map + d) % n + n) % n;
            if (k < previews.Length && k < Circuits.All.Length && k != playgroundIdx && previews[k] == null) { Preview(k, loaded, loadedFor); prefetchFrame = Time.frameCount; return; }
        }
    }

    void DrawTestArea(Rect r)
    {
        UiKit.Outline(r, UiKit.Ink, 0f, 0.7f);
        for (int i = 1; i < 6; i++) UiKit.VHair(r.x + r.width * i / 6f, r.y + U(10), r.yMax - U(10), UiKit.PaperTrack);
        for (int i = 1; i < 4; i++) UiKit.Hair(r.x + U(10), r.xMax - U(10), r.y + r.height * i / 4f, UiKit.PaperTrack);
        UiKit.Text(new Rect(r.x, r.center.y - U(9), r.width, U(18)), "Free driving, 0-100 and top speed", UiKit.Body, 11f, UiKit.InkSub, TextAnchor.MiddleCenter);
    }

    // ------------------------------------------------------------------ track map
    // The outline is rendered once per circuit into a texture with mipmaps: a crisp anti-aliased ink line (#111111,
    // coverage in alpha) on nothing, north up, so it sits on the paper page. The red start mark, the direction arrow and
    // the sector splits are drawn on top with IMGUI so they stay sharp at any size.
    sealed class MapPreview { public Texture2D tex; public Vector2 start, s1, s2, dir; public float aspect; }
    static float[] bufLine;

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

        if (bufLine == null || bufLine.Length < n) bufLine = new float[n];
        else System.Array.Clear(bufLine, 0, n);
        float lineR = W / 230f;
        void Stamp(float fx, float fy)
        {
            int r = Mathf.CeilToInt(lineR) + 2, cx = Mathf.RoundToInt(fx), cy = Mathf.RoundToInt(fy);
            for (int dy = -r; dy <= r; dy++)
            {
                int iy = cy + dy; if (iy < 0 || iy >= H) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int ix = cx + dx; if (ix < 0 || ix >= W) continue;
                    float ddx = ix - fx, ddy = iy - fy, d = Mathf.Sqrt(ddx * ddx + ddy * ddy);
                    int i = iy * W + ix;
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
        for (int i = 0; i < n; i++) px[i] = new Color32(17, 17, 17, (byte)(Mathf.Clamp01(bufLine[i]) * 255f)); // ink, coverage in alpha
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
        // sector splits: a small ink dot on a paper ring, and the sector that starts there
        SectorMark(P(pv.s1), dr.center, "S2");
        SectorMark(P(pv.s2), dr.center, "S3");
        // start / finish: the red mark (a square on a paper ring), an ink arrow for the direction of travel
        var s = P(pv.start);
        UiKit.Dot(s, 6.5f, UiKit.Paper);
        float q = Mathf.Round(U(8f));
        UiKit.Rect(new Rect(Mathf.Round(s.x - q * 0.5f), Mathf.Round(s.y - q * 0.5f), q, q), UiKit.Red);
        var m = GUI.matrix;
        var ap = s + pv.dir * U(15);
        GUIUtility.RotateAroundPivot(Mathf.Atan2(pv.dir.y, pv.dir.x) * Mathf.Rad2Deg, ap);
        UiKit.Icon(new Rect(ap.x - U(6), ap.y - U(6), U(12), U(12)), UiKit.IconNext, UiKit.Ink);
        GUI.matrix = m;
    }

    static void SectorMark(Vector2 p, Vector2 centre, string label)
    {
        UiKit.Dot(p, 4f, UiKit.Paper); UiKit.Dot(p, 2.4f, UiKit.Ink);
        var o = p - centre; o = o.sqrMagnitude > 1f ? o.normalized : Vector2.up;
        var c = p + o * U(13);
        UiKit.Text(new Rect(c.x - U(12), c.y - U(7), U(24), U(14)), label, UiKit.Medium, 8.5f, UiKit.InkSub, TextAnchor.MiddleCenter);
    }

    // ================================================================== loading
    void DrawLoading(int map)
    {
        float sw = Screen.width, sh = Screen.height;
        UiKit.PaperPage();
        var f = UiKit.PaperFrame(72f);
        var info = cards[Mathf.Clamp(map, 0, cards.Length - 1)];
        string kind = quali ? "Loading  ·  qualifying" : practice ? (map == playgroundIdx ? "Loading  ·  test area" : "Loading  ·  practice") : "Loading  ·  race";
        UiKit.Masthead(f, UiKit.NavNone, false, kind);
        float cy = (f.head + f.foot) * 0.5f;
        var pv = map >= 0 && map < previews.Length ? previews[map] : null; // only when already built: no extra build here
        if (pv != null) { UiKit.PushAlpha(0.10f); DrawMap(new Rect(sw * 0.5f - sh * 0.55f, f.head + U(12), sh * 1.1f, f.foot - f.head - U(24)), pv, false); UiKit.PopAlpha(); }
        UiKit.Text(new Rect(f.x0, cy - U(42), f.w, U(56)), info.upper, UiKit.Heavy, UiKit.Fit(info.upper, UiKit.Heavy, 44f, f.w * 0.9f, 24f), UiKit.Ink, TextAnchor.MiddleCenter);
        UiKit.Text(new Rect(f.x0, cy + U(12), f.w, U(20)), info.country, UiKit.Body, 13.5f, UiKit.InkSub, TextAnchor.MiddleCenter);
        // a thin ink segment running along a track
        float bw = U(220), bx = sw * 0.5f - bw * 0.5f, byy = cy + U(46), bh = Mathf.Max(2f, Mathf.Round(U(2f)));
        UiKit.Rect(new Rect(bx, byy, bw, bh), UiKit.PaperTrack);
        float ph = Mathf.Repeat(Time.unscaledTime * 0.9f, 1f), seg = 0.32f;
        float a0 = Mathf.Clamp01(ph * (1f + seg) - seg), a1 = Mathf.Clamp01(ph * (1f + seg));
        if (a1 > a0) UiKit.Rect(new Rect(bx + bw * a0, byy, bw * (a1 - a0), bh), UiKit.Ink);
        // the rule above the footer, and a tip in it
        UiKit.Hair(f.x0, f.x1, f.foot);
        string tip = Tips[tipIdx];
        float ts = UiKit.Fit(tip, UiKit.Body, 11.5f, f.w - U(60), 8f);
        float tw = UiKit.Width(tip, UiKit.Body, ts), tagW = UiKit.StampWidth("TIP", 8.5f), total = tagW + U(10) + tw, tx = sw * 0.5f - total * 0.5f;
        UiKit.Stamp(new Rect(tx, f.actY - U(8), tagW, U(16)), "TIP", UiKit.Ink, Color.white, 8.5f);
        UiKit.Text(new Rect(tx + tagW + U(10), f.actY - U(11), tw + U(4), U(22)), tip, UiKit.Body, ts, UiKit.InkSub);
        // the frame has really been drawn: from the next-but-one frame on, the heavy build may start
        if (Event.current.type == EventType.Repaint && loadShownFrame < 0) loadShownFrame = Time.frameCount;
        // once per loading page, after the studio's objects are really gone (Destroy runs at the end of a frame):
        // free what the menu used before the circuit is built
        if (!purged && Event.current.type == EventType.Repaint && Time.frameCount > releaseFrame)
        {
            purged = true;
            Resources.UnloadUnusedAssets(); System.GC.Collect();
        }
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
        if (reveal) return DrawReveal(); // the progress reveal first (MenuProgress.cs)
        if (!ReferenceEquals(noteParsed, resultNote)) ParseNote();
        UiKit.PaperPage();
        var f = UiKit.PaperFrame(72f);
        UiKit.Masthead(f, UiKit.NavNone, false, resHead);
        float x0 = f.x0, x1 = f.x1, W = f.w, top = f.top;
        int n = results.Length;
        const string scr = "results";

        // ---- the race data, right-aligned on the header line (race time, best / fastest lap, stops, penalties)
        float e1 = UiKit.Enter(scr, 1), e2 = UiKit.Enter(scr, 2);
        UiKit.PushAlpha(e2);
        float dx = x1, dy = UiKit.Rise(new Rect(0, top + U(4), 0, 0), e2, 8f).y;
        if (resPenStr != null) dx = DatumLeft(dx, dy, "Penalties", resPenStr, UiKit.BadInk);
        if (resStopsStr != null) dx = DatumLeft(dx, dy, "Pit stops", resStopsStr, UiKit.Ink);
        dx = DatumLeft(dx, dy, resPlayerFastest ? "Fastest lap" : "Best lap", resBestStr, UiKit.Ink);
        dx = DatumLeft(dx, dy, "Race time", resTimeStr, UiKit.Ink);
        UiKit.PopAlpha();

        // ---- the position: the giant red italic number; beside it "of 20", the points / championship tags, the note
        UiKit.PushAlpha(e1);
        const float ps = 78f;
        var big = UiKit.Rise(new Rect(x0 - U(3), top - U(6), U(220), U(72)), e1, 14f);
        UiKit.BigNumber(big, resPosStr, ps, UiKit.Red);
        float bx = x0 + UiKit.Width(resPosStr, UiKit.HeavyItalic, ps) + U(14), bw = Mathf.Max(U(60), dx - U(24) - bx);
        UiKit.Text(new Rect(bx, top + U(2), bw, U(20)), resOfStr, UiKit.Heavy, 15f, UiKit.Ink);
        float tagX = bx, ty = top + U(25);
        if (notePts != null) { float w = UiKit.StampWidth(notePts, 9f); UiKit.Stamp(new Rect(tagX, ty, w, U(17)), notePts, UiKit.Ink, Color.white, 9f); tagX += w + U(6); }
        if (noteChamp != null) { float w = UiKit.StampWidth(noteChamp, 9f); UiKit.Stamp(new Rect(tagX, ty, w, U(17)), noteChamp, UiKit.Ink, Color.white, 9f); }
        float ly = notePts != null || noteChamp != null ? top + U(46) : top + U(24);
        if (noteLine != null) UiKit.Text(new Rect(bx, ly, bw, U(16)), noteLine, UiKit.Body, UiKit.Fit(noteLine, UiKit.Body, 11f, bw, 8f), UiKit.InkSub);
        UiKit.PopAlpha();

        // ---- timing table (left) and the driver's progress (right, MenuProgress.cs), a hairline between them
        float tTop = top + U(74), tBottom = f.foot - U(8);
        float progW = MenuProfile.Last != null ? Mathf.Clamp(W * 0.3f, U(190), U(230)) : 0f;
        var tp = new Rect(x0, tTop, W - (progW > 0f ? progW + U(24) : 0f), tBottom - tTop);
        if (progW > 0f)
        {
            UiKit.VHair(x1 - progW - U(12), tTop, tBottom, UiKit.PaperTrack);
            DrawProgressPanel(new Rect(x1 - progW, tTop, progW, tBottom - tTop), scr);
        }
        float et = UiKit.Enter(scr, 6, 0.04f, 0.45f);
        UiKit.PushAlpha(et);
        tp = UiKit.Rise(tp, et, 12f);
        int cols = n > 8 ? 2 : 1, perCol = (n + cols - 1) / cols;
        float colGap = U(22), tw = tp.width;
        if (cols == 1) tw = Mathf.Min(tw, U(560));
        float cw = (tw - (cols - 1) * colGap) / cols, headY = tp.y;
        float tableY = headY + U(16), rh = Mathf.Min(U(24), (tp.yMax - tableY) / Mathf.Max(1, perCol));
        for (int c = 0; c < cols; c++)
        {
            float x = tp.x + c * (cw + colGap);
            UiKit.Kicker(new Rect(x + U(8), headY, cw * 0.1f, U(14)), "Pos", 8.5f);
            UiKit.Kicker(new Rect(x + cw * 0.15f, headY, cw * 0.4f, U(14)), "Driver", 8.5f);
            UiKit.Kicker(new Rect(x + cw * 0.55f, headY, cw * 0.22f, U(14)), "Best lap", 8.5f);
            UiKit.Kicker(new Rect(x + cw * 0.76f, headY, cw * 0.22f, U(14)), "Gap", 8.5f, TextAnchor.MiddleRight);
            UiKit.Hair(x, x + cw, tableY - U(1), UiKit.Ink);
        }
        float fs = Mathf.Clamp(rh / U(1) * 0.5f, 9.5f, 12f);
        for (int i = 0; i < n; i++)
        {
            var r = results[i];
            float x = tp.x + (i / perCol) * (cw + colGap), y = tableY + (i % perCol) * rh;
            float ei = UiKit.Enter(scr, 7 + i % perCol, 0.025f);
            UiKit.PushAlpha(ei);
            UiKit.PaperRow(new Rect(x, y, cw, rh), r.player);
            UiKit.Text(new Rect(x + U(8), y, cw * 0.08f, rh), Ord[Mathf.Min(i + 1, Ord.Length - 1)], UiKit.Heavy, fs, i < 3 ? UiKit.Ink : UiKit.InkSub);
            UiKit.Rect(new Rect(x + cw * 0.115f, y + rh * 0.26f, Mathf.Max(2f, Mathf.Round(U(2.5f))), rh * 0.48f), r.colour); // team colour
            UiKit.Text(new Rect(x + cw * 0.15f, y, cw * 0.40f, rh), r.name, r.player ? UiKit.Heavy : UiKit.Medium, fs, UiKit.Ink);
            if (pens[i] != null) UiKit.Text(new Rect(x + cw * 0.15f, y, cw * 0.40f - U(4), rh), pens[i], UiKit.Heavy, fs * 0.85f, UiKit.BadInk, TextAnchor.MiddleRight); // time penalty
            bool fl = i == fastestRow;
            UiKit.Text(new Rect(x + cw * 0.55f, y, cw * 0.22f, rh), bests[i], fl ? UiKit.Heavy : UiKit.Body, fs, fl ? UiKit.Ink : UiKit.InkSub);
            if (fl)
            {
                float sx = x + cw * 0.55f + UiKit.Width(bests[i], UiKit.Heavy, fs) + U(5), sw2 = UiKit.StampWidth("FL", 7f);
                UiKit.Stamp(new Rect(sx, y + rh * 0.5f - U(6.5f), sw2, U(13)), "FL", UiKit.Ink, Color.white, 7f); // fastest lap
            }
            UiKit.Text(new Rect(x + cw * 0.76f, y, cw * 0.22f, rh), gaps[i], i == 0 ? UiKit.Medium : UiKit.Body, fs, i == 0 ? UiKit.Ink : UiKit.InkSub, TextAnchor.MiddleRight);
            UiKit.PopAlpha();
        }
        UiKit.PopAlpha();

        // ---- actions: links left (Replay, Menu), the black button right
        float eb = UiKit.Enter(scr, 9);
        UiKit.PushAlpha(eb);
        UiKit.Hair(x0, x1, f.foot);
        Choice ch = Choice.None;
        string prim = seasonNext ? (Season.Over ? "Standings" : "Next round") : "Race again";
        float pw = UiKit.BlackButtonWidth(prim);
        var pr = new Rect(x1 - pw, f.actY - U(24), pw, U(48));
        if (UiKit.BlackButton(pr, prim)) { if (seasonNext) season.Open(); else ch = Choice.RaceAgain; }
        float lx = x0;
        if (UiKit.Link(new Rect(lx, f.actY - U(22), U(110), U(44)), "Menu", "Home")) GoHome();
        lx += U(110) + U(16);
        if (canReplay && UiKit.Link(new Rect(lx, f.actY - U(22), U(150), U(44)), "Replay", "Last 60 s  ·  TV cameras")) ch = Choice.Replay;
        UiKit.PopAlpha();
        return ch;
    }

    // a caption / value pair placed to the left of x (right-aligned data on a line); returns its left edge minus the gap
    static float DatumLeft(float x, float y, string cap, string val, Color col)
    {
        float w = UiKit.DatumWidth(cap, val, 16f);
        UiKit.Datum(new Rect(x - w, y, w, U(36)), cap, val, col, 16f, TextAnchor.MiddleRight);
        return x - w - U(26);
    }

    internal static string Fmt(double t)
    {
        if (double.IsNaN(t) || double.IsInfinity(t) || t <= 0) return "—";
        int m = (int)(t / 60); return $"{m}:{t - m * 60:00.000}";
    }
}
