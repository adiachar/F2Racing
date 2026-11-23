// MenuPlay.cs — the Play flow of the front end (MenuUI, Page.Play) in the "Paper" look (UiKit.cs): one screen per
// step under the masthead (PLAY underlined), a step line (1 MODE · 2 CIRCUIT · 3 SETUP; done steps can be tapped to go
// back), and the action row under the bottom rule: "Back" as a text link on the left (and Android back / Escape),
// other actions as links beside it, the black button on the right. Red marks only the selected state.
//  * Mode: three columns: Quick race, Race weekend (practice, qualifying, race), Championship = the career (SeasonUI.cs);
//    the chosen one has a red rule over it. The career skips the Circuit step (its calendar decides); its Setup shows the
//    tier's car and the difficulty (CareerDifficulty.cs) instead of the rival level, and no grid choice (qualifying).
//  * Circuit: the circuit (type, big name, country) and its data rows on the left; the track map drawn as an ink line
//    with a red start mark on the right, ‹ › beside it, a tap / swipe on the map = next / previous; page dots under it.
//  * Setup: the session options as a clean 3 x 3 grid of label / value rows with − + text buttons: car, livery,
//    rivals, rival level, grid, race length, qualifying, weather, time of day; then Start (race / weekend /
//    championship hub), Practice and, in a race weekend, Skip qualifying.
// The content slides in from the side it was entered from (MenuUI.BeginNav); the masthead and the action row stay.
using FormulaSim;
using UnityEngine;

public sealed partial class MenuUI
{
    public const int StepMode = 0, StepCircuit = 1, StepSetup = 2;
    public int step;
    const string KeyMode = "mode", KeyCircuit = "circuit", KeySetup = "setup";
    static readonly string[] StepNum = { "1", "2", "3" }, StepName = { "Mode", "Circuit", "Setup" };
    static readonly string[] ModeDesc =
    {
        "Pick a circuit, set the field and go straight to the grid.",
        "Practice, then a timed qualifying session that sets the grid, then the race.",
        "Your career: a rookie, a promise, a nemesis. Ten rounds a season, from the Rookie tier to the world title.",
    };
    string champStatus = "";
    int ctaKey = -1; string ctaSub;
    int setupKey = -1; string setupSub;

    // the championship card's status line (read when the Mode step opens, not per frame: it may load the save)
    void ModeStatus()
    {
        if (!Season.Active && Season.HasSave) Season.Load();
        champStatus = !Season.Active ? "New career  ·  contract offers"
                    : Season.Over ? "Season " + Season.Data.seasonNo + " complete  ·  next season"
                    : "Round " + (Season.Data.round + 1) + " of " + Season.Rounds + "  ·  " + Season.GpNames[Season.Data.round] + "  ·  " + Season.TierNames[Season.Tier] + " tier";
    }

    Choice DrawPlay(ref int map, ref int cls, ref int riv, ref int diff, Track loaded, int loadedFor)
    {
        UiKit.PaperPage();
        var f = Masthead(UiKit.NavPlay);
        bool champ = mode == ModeChampionship;
        StepIndicator(f.x0, f.top - U(2), champ);

        float cTop = f.top + U(34), bottom = f.foot - U(12);
        var ch = Choice.None;
        BeginNav();
        if (step == StepMode) DrawModeStep(f.x0, f.x1, cTop, bottom);
        else if (step == StepCircuit) DrawCircuitStep(ref map, loaded, loadedFor, f.x0, f.x1, cTop, bottom);
        else ch = DrawSetupStep(ref map, ref cls, ref riv, ref diff, f.x0, f.x1, cTop, bottom);
        EndNav();

        // ---- the action row: Back (a link) on the left, the step's own actions
        UiKit.Hair(f.x0, f.x1, f.foot);
        string backSub = step == StepMode ? "Home" : step == StepCircuit ? "Mode" : champ ? "Mode" : "Circuit";
        if (UiKit.Link(new Rect(f.x0, f.actY - U(22), U(84), U(44)), "\u2039  Back", backSub)) Back();
        if (step == StepSetup) ch = SetupActions(f, map, riv, ch);
        else if (UiKit.BlackButton(NextRect(f), "Next")) GoPlay(step == StepMode && champ ? StepSetup : step == StepMode ? StepCircuit : StepSetup, 1);
        return ch;
    }

    static Rect NextRect(UiKit.Frame f) { float w = Mathf.Max(U(150), UiKit.BlackButtonWidth("Next")); return new Rect(f.x1 - w, f.actY - U(25), w, U(50)); }

    // 1 MODE   2 CIRCUIT   3 SETUP: the current step ExtraBold ink, red underline; done steps grey (tap = back to it)
    void StepIndicator(float x, float y, bool champ)
    {
        float gap = U(26);
        for (int i = 0; i < 3; i++)
        {
            bool active = i == step, done = i < step, skipped = champ && i == StepCircuit;
            string nm = UiKit.Caps(StepName[i]);
            float nw = UiKit.Width(StepNum[i], UiKit.Heavy, 10.5f), lw = UiKit.Width(nm, active ? UiKit.Heavy : UiKit.Medium, 10.5f), w = nw + U(8) + lw;
            var r = new Rect(x, y, w, U(20));
            bool hit = UiKit.Press(new Rect(x - U(8), y - U(12), w + U(16), U(44)), done && !skipped, out float p);
            if (hit && done && !skipped) GoPlay(i, -1);
            UiKit.PushAlpha(skipped ? 0.35f : 1f);
            Color c = Color.Lerp(active ? UiKit.Ink : done ? UiKit.InkSub : UiKit.InkMuted, UiKit.Red, p);
            UiKit.Text(new Rect(x, y, nw + U(2), r.height), StepNum[i], UiKit.Heavy, 10.5f, c);
            UiKit.Text(new Rect(x + nw + U(8), y, lw + U(2), r.height), nm, active ? UiKit.Heavy : UiKit.Medium, 10.5f, c);
            if (active) UiKit.Rect(new Rect(x, r.yMax + U(1), w, Mathf.Max(2f, Mathf.Round(U(2f)))), UiKit.Red);
            UiKit.PopAlpha();
            x += w + gap;
        }
    }

    // ================================================================== 1 mode
    void DrawModeStep(float x0, float x1, float cTop, float bottom)
    {
        float W = x1 - x0, gap = U(24), cw = (W - 2 * gap) / 3f;
        for (int i = 0; i < 3; i++)
            if (ModeCard(new Rect(x0 + i * (cw + gap), cTop, cw, bottom - cTop), i) && mode != i) { mode = i; PlayerPrefs.SetInt("gameMode", mode); }
    }

    static readonly string[] ModeNum = { "01", "02", "03" };
    // a mode column: a rule over it (red and thick when chosen), number, title, description, the session
    bool ModeCard(Rect r, int i)
    {
        float e = UiKit.Enter(KeyMode, 1 + i, 0.05f, 0.4f);
        r = UiKit.Rise(r, e, 10f);
        UiKit.PushAlpha(e);
        bool hit = UiKit.Press(r, true, out float p);
        float s = UiKit.Smooth(0x6D00 + i, mode == i ? 1f : 0f, 12f);
        if (s > 0.01f || p > 0.01f) UiKit.Rect(r, UiKit.A(UiKit.PaperTint, Mathf.Max(s, p)));
        UiKit.Hair(r.x, r.xMax, r.y, UiKit.Ink);
        if (s > 0.01f) UiKit.Rect(new Rect(r.x, r.y, r.width * s, Mathf.Max(3f, Mathf.Round(U(3f)))), UiKit.Red);
        float px = r.x + U(12), w = r.width - U(24);
        UiKit.Text(new Rect(px, r.y + U(12), w, U(16)), ModeNum[i], UiKit.Medium, 10.5f, UiKit.InkSub);
        UiKit.Text(new Rect(px, r.y + U(28), w, U(30)), ModeNames[i], UiKit.Heavy, UiKit.Fit(ModeNames[i], UiKit.Heavy, 22f, w, 14f), UiKit.Ink);
        UiKit.Text(new Rect(px, r.y + U(64), w, U(64)), ModeDesc[i], UiKit.Body, 11.5f, UiKit.InkSub, TextAnchor.UpperLeft, true);
        if (i == ModeChampionship) UiKit.Text(new Rect(px, r.yMax - U(64), w, U(16)), champStatus, UiKit.Medium, UiKit.Fit(champStatus, UiKit.Medium, 10.5f, w, 7.5f), UiKit.Ink);
        UiKit.Kicker(new Rect(px, r.yMax - U(42), w, U(14)), "Session", 8.5f);
        UiKit.Text(new Rect(px, r.yMax - U(28), w, U(18)), ModeHint[i], UiKit.Medium, UiKit.Fit(ModeHint[i], UiKit.Medium, 11f, w, 8f), UiKit.Ink);
        UiKit.PopAlpha();
        return hit;
    }

    // ================================================================== 2 circuit
    // Left: the circuit (type, the name once as the title, country) and its data chips. Right: the big track map with
    // < and > beside it, a tap on the map = the next circuit, a swipe = next / previous; page dots under it.
    float mapPressX;
    int[] mapOrder; int realCount;   // the cycle: the real circuits first (one dot each), then test track(s) and playground

    void BuildOrder()
    {
        int n = mapNames.Length;
        mapOrder = new int[n]; int k = 0;
        for (int i = 0; i < n; i++) if (i != playgroundIdx && i < Circuits.All.Length && Circuits.All[i].style != CircuitStyle.Test) mapOrder[k++] = i;
        realCount = k;
        for (int i = 0; i < n; i++) if (i != playgroundIdx && (i >= Circuits.All.Length || Circuits.All[i].style == CircuitStyle.Test)) mapOrder[k++] = i;
        if (playgroundIdx >= 0 && playgroundIdx < n) mapOrder[k++] = playgroundIdx;
    }
    int MapStep(int map, int d)
    {
        int n = mapOrder.Length, pos = System.Array.IndexOf(mapOrder, map);
        if (pos < 0) pos = 0;
        return mapOrder[((pos + d) % n + n) % n];
    }

    void DrawCircuitStep(ref int map, Track loaded, int loadedFor, float x0, float x1, float cTop, float bottom)
    {
        float W = x1 - x0, L = W * 0.38f, bodyY = cTop;
        if (mapOrder == null) BuildOrder();
        int n = mapOrder.Length;
        float bw = U(44), mapTop = bodyY - U(4), mapBottom = bottom - U(16);
        var area = new Rect(x0 + L + U(12), mapTop, W - L - U(12), mapBottom - mapTop);
        var prevB = new Rect(area.x, area.center.y - bw * 0.5f, bw, bw);
        var nextB = new Rect(area.xMax - bw, prevB.y, bw, bw);
        var mapR = new Rect(prevB.xMax + U(8), area.y, nextB.x - U(8) - prevB.xMax - U(8), area.height);

        // ---- input first, so a change shows this frame
        float ea = UiKit.Enter(KeyCircuit, 5);
        UiKit.PushAlpha(ea);
        if (UiKit.Glyph(prevB, UiKit.Prev, 32f)) { map = MapStep(map, -1); mapStepDir = -1; }
        if (UiKit.Glyph(nextB, UiKit.Next, 32f)) { map = MapStep(map, 1); mapStepDir = 1; }
        UiKit.PopAlpha();
        int sw = MapSwipe(mapR);
        if (sw != 0) { map = MapStep(map, sw); mapStepDir = sw; }

        bool pg = map == playgroundIdx;
        if (map != lastMap)
        {
            UiKit.Mark(KeyMap);
            mapDir = mapStepDir != 0 ? mapStepDir : lastMap < 0 || map > lastMap ? 1 : -1;
            mapStepDir = 0; lastMap = map;
        }
        float mt = UiKit.EaseOut(UiKit.Since(KeyMap) / 0.4f);
        var info = cards[map];

        // ---- the circuit: kicker (type, n / total), the name (once), country
        float e1 = UiKit.Enter(KeyCircuit, 1), e2 = UiKit.Enter(KeyCircuit, 2), e3 = UiKit.Enter(KeyCircuit, 3);
        UiKit.PushAlpha(e1 * mt);
        UiKit.Kicker(UiKit.Slide(new Rect(x0, bodyY, L, U(14)), mt, 10f * mapDir), info.eyebrow, UiKit.Fit(UiKit.Caps(info.eyebrow), UiKit.Body, 10f, L, 7f));
        UiKit.PopAlpha();
        float ns = UiKit.Fit(info.name, UiKit.Heavy, 36f, L - U(8), 20f);
        UiKit.PushAlpha(e2 * mt);
        UiKit.Text(UiKit.Slide(UiKit.Rise(new Rect(x0 - U(1), bodyY + U(14), L - U(8), U(48)), e2, 12f), mt, 18f * mapDir), info.name, UiKit.Heavy, ns, UiKit.Ink);
        UiKit.PopAlpha();
        UiKit.PushAlpha(e3 * mt);
        UiKit.Text(UiKit.Slide(new Rect(x0, bodyY + U(60), L, U(18)), mt, 12f * mapDir), info.country, UiKit.Body, 13f, UiKit.InkSub);
        UiKit.PopAlpha();

        // ---- the map (built first: it measures the length for the data rows)
        var pv = pg ? null : Preview(map, loaded, loadedFor);
        float e5 = UiKit.Enter(KeyCircuit, 4, 0.04f, 0.6f);
        UiKit.PushAlpha(e5 * (0.35f + 0.65f * mt));
        var mr = UiKit.Slide(UiKit.Scale(mapR, 0.96f + 0.04f * mt), mt, 16f * mapDir);
        if (pv != null) DrawMap(mr, pv, true);
        else if (pg) DrawTestArea(mr);
        UiKit.PopAlpha();

        // ---- page dots: one round dot per real circuit, then a small square for the test track / playground
        float dg = U(11), extra = n - realCount, dw = (n - 1) * dg + (extra > 0 ? U(8) : 0f), dx = mapR.center.x - dw * 0.5f, dy = mapBottom + U(8);
        UiKit.PushAlpha(ea);
        for (int k = 0; k < n; k++)
        {
            bool on = mapOrder[k] == map;
            var col = on ? UiKit.Ink : UiKit.PaperTrack;
            float x = dx + k * dg + (k >= realCount ? U(8) : 0f);
            if (k < realCount) UiKit.Dot(new Vector2(x, dy), on ? 3.2f : 2.6f, col);
            else { float h = on ? U(2.8f) : U(2.3f); UiKit.Rect(new Rect(x - h, dy - h, 2f * h, 2f * h), col); }
        }
        UiKit.PopAlpha();

        // ---- data rows under the name: label left, value right, a quiet rule under each
        float ry = bodyY + U(96), rh = U(32), rw = Mathf.Min(L - U(8), U(260));
        for (int i = 0; i < 3; i++)
        {
            float ec = UiKit.Enter(KeyCircuit, 4 + i);
            UiKit.PushAlpha(ec);
            var r = UiKit.Rise(new Rect(x0, ry + i * rh, rw, rh), ec, 8f);
            string cap, val;
            if (pg) { cap = i == 0 ? "Timing" : i == 1 ? "Drive" : "Tests"; val = i == 0 ? "Off" : i == 1 ? "Free" : "0-100  ·  Vmax"; }
            else { cap = i == 0 ? "Length" : i == 1 ? "Turns" : "Target pole"; val = i == 0 ? info.length : i == 1 ? info.corners : info.pole; }
            if (i == 0) UiKit.Hair(r.x, r.xMax, r.y, UiKit.Ink);
            UiKit.Kicker(new Rect(r.x, r.y, r.width * 0.5f, r.height), cap, 9.5f);
            UiKit.Text(new Rect(r.x, r.y, r.width, r.height), val, UiKit.Heavy, 14f, UiKit.Ink, TextAnchor.MiddleRight);
            UiKit.Hair(r.x, r.xMax, r.yMax - UiKit.HairPx, UiKit.PaperTrack);
            UiKit.PopAlpha();
        }
        PrefetchNeighbours(map, loaded, loadedFor);
    }

    // the map itself: a tap = the next circuit, a sideways swipe = next (to the left) / previous (to the right).
    // Returns the step (-1, 0, 1) on release.
    int MapSwipe(Rect r)
    {
        int id = GUIUtility.GetControlID(FocusType.Passive);
        var e = Event.current; int res = 0;
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (e.button == 0 && r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; mapPressX = e.mousePosition.x; e.Use(); }
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == id) e.Use();
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == id)
                {
                    GUIUtility.hotControl = 0;
                    float dx = e.mousePosition.x - mapPressX;
                    if (Mathf.Abs(dx) > U(24)) res = dx < 0 ? 1 : -1;
                    else if (r.Contains(e.mousePosition)) res = 1;
                    e.Use();
                }
                break;
        }
        return res;
    }

    // ================================================================== 3 setup
    static readonly string[] TeamColours = { "Team colours" };
    Choice DrawSetupStep(ref int map, ref int cls, ref int riv, ref int diff, float x0, float x1, float cTop, float bottom)
    {
        float W = x1 - x0;
        var ch = Choice.None;
        bool champ = mode == ModeChampionship, pg = map == playgroundIdx && !champ;
        int hk = mode * 1000 + map;
        if (hk != setupKey)
        {
            setupKey = hk;
            setupSub = champ ? "The calendar picks the circuit, the tier the car  ·  full grid  ·  points"
                     : cards[Mathf.Clamp(map, 0, cards.Length - 1)].name + "  ·  " + cards[Mathf.Clamp(map, 0, cards.Length - 1)].country;
        }
        int key = lapsIdx * 1000 + riv * 10 + qualiIdx * 100000 + (champ ? 7 : 0) + mode * 3;
        if (key != ctaKey)
        {
            ctaKey = key;
            ctaSub = champ ? "Career hub  ·  next round" : mode == ModeWeekend ? qualiText[qualiIdx] + " qualifying, then race" : lapsText[lapsIdx] + "  ·  " + (rivalCounts[riv] == 0 ? "solo" : rivalCounts[riv] + " rivals");
        }
        float e1 = UiKit.Enter(KeySetup, 0);
        UiKit.PushAlpha(e1);
        UiKit.Text(UiKit.Rise(new Rect(x0, cTop - U(6), W * 0.6f, U(28)), e1, 10f), ModeNames[mode], UiKit.Heavy, 22f, UiKit.Ink);
        UiKit.Text(new Rect(x0, cTop + U(20), W * 0.6f, U(16)), setupSub, UiKit.Body, UiKit.Fit(setupSub, UiKit.Body, 11.5f, W * 0.6f, 8.5f), UiKit.InkSub);
        string cs = pg ? "Free driving  ·  0-100  ·  top speed" : ctaSub;
        UiKit.Text(new Rect(x0 + W * 0.4f, cTop - U(6), W * 0.6f, U(28)), cs, UiKit.Medium, UiKit.Fit(cs, UiKit.Medium, 11.5f, W * 0.4f, 8f), UiKit.Ink, TextAnchor.MiddleRight);
        UiKit.PopAlpha();

        // ---- 3 x 3 rows: label / value, − +
        float gy = cTop + U(46), cg = U(24);
        float th = Mathf.Max(U(46), (bottom - gy) / 3f), tw = (W - 2 * cg) / 3f;
        UiKit.PushAlpha(e1);
        UiKit.Hair(x0, x1, gy, UiKit.Ink);
        UiKit.PopAlpha();
        for (int i = 0; i < 9; i++)
        {
            int col = i % 3, row = i / 3;
            var r = new Rect(x0 + col * (tw + cg), gy + row * th, tw, th);
            float ei = UiKit.Enter(KeySetup, 1 + col + row);
            switch (i)
            {
                case 0:
                    if (champ) SetupTile(r, "Car  ·  tier", CarConfig.ClassNames[Season.Active ? Season.TierClass : CarConfig.ClassNames.Length - 1], 0, 1, false, 0, ei); // the career's tier decides
                    else cls = SetupTile(r, "Car", CarConfig.ClassNames[cls], cls, CarConfig.ClassNames.Length, true, 0, ei);
                    break;
                case 1:
                {
                    // the player's colours (a championship round uses the team's)
                    int li = Livery.PlayerIndex;
                    int n = SetupTile(r, "Livery", champ ? TeamColours[0] : Livery.Player[li].name, li, Livery.Player.Length, !champ, 8, ei);
                    if (n != li) { Livery.PlayerIndex = n; restyleDue = true; ch = Choice.Livery; }
                    break;
                }
                case 2: riv = SetupTile(r, "Rivals", champ ? "Full grid" : rivText[riv], riv, rivalCounts.Length, !pg && !champ, 1, ei); break;
                case 3:
                    if (champ)
                    {
                        // the career's difficulty (a preset or Custom; GameManager applies it at the session start)
                        int dn = SetupTile(r, "Difficulty", Difficulty.Name, Difficulty.Current, Difficulty.Names.Length, true, 2, ei);
                        if (dn != Difficulty.Current) Difficulty.Set(dn);
                    }
                    else { int n = SetupTile(r, "Rival level", diffNames[diff], diff, diffNames.Length, !pg, 2, ei); if (n != diff) { diff = n; Difficulty.RivalLevelChanged(n); } }
                    break;
                case 4:
                    if (champ) SetupTile(r, "Grid", "Qualifying", 0, 1, false, 3, ei); // a career's grid comes from qualifying
                    else { int n = SetupTile(r, "Grid", GridNames[gridIdx], gridIdx, GridNames.Length, !pg, 3, ei); if (n != gridIdx) SetGrid(n); }
                    break;
                case 5: { int n = SetupTile(r, "Race length", lapsText[lapsIdx], lapsIdx, LapCounts.Length, !pg, 4, ei); if (n != lapsIdx) { lapsIdx = n; PlayerPrefs.SetInt("laps", lapsIdx); } break; }
                case 6: { int n = SetupTile(r, "Qualifying", qualiText[qualiIdx], qualiIdx, QualiMinutes.Length, !pg && mode != ModeQuick, 5, ei); if (n != qualiIdx) { qualiIdx = n; PlayerPrefs.SetInt("qualiLen", qualiIdx); } break; }
                case 7: Weather.Index = SetupTile(r, "Weather", Weather.Names[Weather.Index], Weather.Index, Weather.Names.Length, true, 6, ei); break; // applied and saved at the start of the session
                case 8:
                {
                    // Night on street circuits only (elsewhere it shows and uses Evening); saved at once, used by the next build
                    bool street = map != playgroundIdx && map < Circuits.All.Length && Circuits.All[map].style == CircuitStyle.Street;
                    int cur = (int)TimeOfDay.For(street), n = SetupTile(r, "Time of day", TimeOfDay.Names[cur], cur, TimeOfDay.Count(street), true, 7, ei);
                    if (n != cur) TimeOfDay.Set(n, false);
                    break;
                }
            }
        }
        return ch;
    }

    // the Setup step's action row (right of Back): Practice / Skip qualifying as links, the black Start on the right
    Choice SetupActions(UiKit.Frame f, int map, int riv, Choice ch)
    {
        bool champ = mode == ModeChampionship, pg = map == playgroundIdx && !champ;
        string prim = pg ? "Drive" : mode == ModeWeekend ? "Start weekend" : champ ? "Career hub" : "Start race";
        float pw = Mathf.Max(U(170), UiKit.BlackButtonWidth(prim));
        var pr = new Rect(f.x1 - pw, f.actY - U(25), pw, U(50));
        float lx = f.x0 + U(84) + U(24), ly = f.actY - U(22);
        if (pg)
        {
            if (UiKit.BlackButton(pr, prim)) BeginLoading(true);
        }
        else if (mode == ModeWeekend)
        {
            if (UiKit.BlackButton(pr, prim)) BeginLoading(false, true);
            float w1 = UiKit.LinkWidth("Practice", "Free practice") + U(8);
            if (UiKit.Link(new Rect(lx, ly, w1, U(44)), "Practice", "Free practice")) BeginLoading(true);
            lx += w1 + U(24);
            if (UiKit.Link(new Rect(lx, ly, UiKit.LinkWidth("Skip qualifying", "Race from the grid setting") + U(8), U(44)), "Skip qualifying", "Race from the grid setting")) BeginLoading(false);
        }
        else
        {
            if (UiKit.BlackButton(pr, prim)) { if (champ) season.Open(); else BeginLoading(false); }
            if (!champ && UiKit.Link(new Rect(lx, ly, UiKit.LinkWidth("Practice", "Free practice") + U(8), U(44)), "Practice", "Free practice")) BeginLoading(true);
        }
        return ch;
    }

    // ---- a setup row: caption over the value on the left, − + on the right; tapping the value steps forward too.
    // key: a stable number per row (value slide). e: entry animation 0..1. Returns the new index.
    static readonly System.Collections.Generic.Dictionary<int, float> tileDir = new System.Collections.Generic.Dictionary<int, float>();
    int SetupTile(Rect r, string caption, string value, int index, int count, bool enabled, int key, float e, bool wrap = true)
    {
        r = UiKit.Rise(r, e, 8f);
        UiKit.PushAlpha(e);
        float bw = U(44), bh = Mathf.Min(U(44), r.height);
        var plus = new Rect(r.xMax - bw, r.center.y - bh * 0.5f, bw, bh);
        var minus = new Rect(plus.x - bw, plus.y, bw, bh);
        var body = new Rect(r.x, r.y, minus.x - r.x, r.height);
        int old = index; float dirNow = 0f; int k = 0x5E700 + key;
        if (UiKit.Press(body, enabled && count > 1, out float pb)) { index = wrap ? (index + 1) % count : Mathf.Min(count - 1, index + 1); dirNow = 1f; }
        if (pb > 0.01f) UiKit.Rect(body, UiKit.A(UiKit.PaperTint, pb));
        if (UiKit.Glyph(minus, UiKit.Minus, 20f, enabled && (wrap || index > 0))) { index = wrap ? (index - 1 + count) % count : Mathf.Max(0, index - 1); dirNow = -1f; }
        if (UiKit.Glyph(plus, UiKit.Plus, 20f, enabled && (wrap || index < count - 1))) { index = wrap ? (index + 1) % count : Mathf.Min(count - 1, index + 1); dirNow = 1f; }
        if (index != old) { UiKit.Mark(k); tileDir[k] = dirNow; }
        UiKit.PushAlpha(enabled ? 1f : 0.4f);
        float vw = body.width - U(8), cy = r.center.y;
        UiKit.Kicker(new Rect(r.x, cy - U(17), vw, U(13)), caption, 8.5f);
        float t = UiKit.EaseOut(UiKit.Since(k) / 0.22f), dir = tileDir.TryGetValue(k, out float sd) ? sd : 1f;
        UiKit.PushAlpha(0.25f + 0.75f * t);
        UiKit.Text(new Rect(r.x + U(8) * dir * (1f - t), cy - U(4), vw, U(22)), value, UiKit.Heavy, UiKit.Fit(value, UiKit.Heavy, 15f, vw, 10f), UiKit.Ink);
        UiKit.PopAlpha();
        UiKit.PopAlpha();
        UiKit.Hair(r.x, r.xMax, r.yMax - UiKit.HairPx, UiKit.PaperTrack);
        UiKit.PopAlpha();
        return index;
    }
}
