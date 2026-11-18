// MenuPlay.cs — the Play flow of the front end (MenuUI, Page.Play): one screen per step, a step indicator
// (1 MODE · 2 CIRCUIT · 3 SETUP; done steps can be tapped to go back), Back top-left (and Android back / Escape),
// Next bottom-right (red: the action). Red is only on the action and the selected state.
//  * Mode: three cards: Quick race, Race weekend (practice, qualifying, race), Championship (SeasonUI.cs). The
//    championship skips the Circuit step (its calendar decides).
//  * Circuit: the circuit hero (type, big name, country), data chips, the large track map and the swipeable carousel.
//  * Setup: the session options as a 3 x 3 grid of tiles (label, value, - / +): car, livery, rivals, rival level,
//    grid, race length, qualifying, weather, time of day; then Start (race / weekend / championship hub), Practice
//    and, in a race weekend, Skip qualifying.
// The content slides in from the side it was entered from (MenuUI.BeginNav); the header stays.
using FormulaSim;
using UnityEngine;

public sealed partial class MenuUI
{
    public const int StepMode = 0, StepCircuit = 1, StepSetup = 2;
    public int step;
    const string KeyMode = "mode", KeyCircuit = "circuit", KeySetup = "setup";
    static readonly string[] StepNum = { "1", "2", "3" }, StepName = { "Mode", "Circuit", "Setup" };
    static readonly string[] ModeTitle = { "QUICK RACE", "RACE WEEKEND", "CHAMPIONSHIP" };
    static readonly string[] ModeDesc =
    {
        "Pick a circuit, set the field and go straight to the grid.",
        "Practice, then a timed qualifying session that sets the grid, then the race.",
        "A season with a team: every round on the calendar, points and standings.",
    };
    string champStatus = "";
    int ctaKey = -1; string ctaSub, ctaQualiSub;
    int setupKey = -1; string setupSub;

    // the championship card's status line (read when the Mode step opens, not per frame: it may load the save)
    void ModeStatus()
    {
        if (!Season.Active && Season.HasSave) Season.Load();
        champStatus = !Season.Active ? "New season  ·  pick your team"
                    : Season.Over ? "Season complete  ·  start a new one"
                    : "Next  ·  " + Season.RoundName(Season.Data.round) + "  ·  " + Season.Teams[Season.Data.team];
    }

    Choice DrawPlay(ref int map, ref int cls, ref int riv, ref int diff, Track loaded, int loadedFor)
    {
        Plain();
        var safe = UiKit.Safe;
        float M = U(24), x0 = safe.x + M, x1 = safe.xMax - M, top = safe.y + U(14), bottom = safe.yMax - U(16);
        bool champ = mode == ModeChampionship;

        // ---- header: back, steps, what is being set up
        if (UiKit.IconButton(new Rect(x0, top, U(44), U(44)), UiKit.IconBack)) Back();
        StepIndicator(safe.center.x, top, champ);

        float cTop = top + U(62);
        var ch = Choice.None;
        BeginNav();
        if (step == StepMode) DrawModeStep(x0, x1, cTop, bottom);
        else if (step == StepCircuit) DrawCircuitStep(ref map, loaded, loadedFor, x0, x1, cTop, bottom);
        else ch = DrawSetupStep(ref map, ref cls, ref riv, ref diff, x0, x1, cTop, bottom);
        EndNav();
        return ch;
    }

    static Rect NextRect(float x1, float bottom) => new Rect(x1 - U(156), bottom - U(50), U(156), U(50));

    void StepIndicator(float cx, float y, bool champ)
    {
        float itemW = U(104), lineW = U(22), total = 3 * itemW + 2 * lineW, x = cx - total * 0.5f;
        for (int i = 0; i < 3; i++)
        {
            bool active = i == step, done = i < step, skipped = champ && i == StepCircuit;
            var r = new Rect(x, y, itemW, U(44));
            if (UiKit.Press(r, done && !skipped, out float p) && done && !skipped) GoPlay(i, -1);
            UiKit.PushAlpha(skipped ? 0.4f : 1f);
            var c = new Vector2(r.x + U(14), r.center.y);
            var cr = new Rect(c.x - U(11), c.y - U(11), U(22), U(22));
            if (active) UiKit.Rect(cr, UiKit.Accent, 999f);
            else if (done) UiKit.Rect(cr, new Color(0.92f, 0.93f, 0.95f, 0.92f - 0.2f * p), 999f);
            else UiKit.Outline(cr, new Color(1, 1, 1, 0.22f), 999f, 0.9f);
            UiKit.Text(cr, StepNum[i], UiKit.Bold, 10.5f, done && !active ? UiKit.Ink : active ? Color.white : UiKit.Faint, TextAnchor.MiddleCenter);
            UiKit.Text(new Rect(r.x + U(31), r.y, itemW - U(31), r.height), UiKit.Caps(StepName[i]), UiKit.Bold, 9.5f, active ? UiKit.TextCol : done ? UiKit.Sub : UiKit.Faint);
            UiKit.PopAlpha();
            if (i < 2) UiKit.Rect(new Rect(r.xMax - U(4), r.center.y - Mathf.Max(0.5f, U(0.5f)), lineW, Mathf.Max(1f, U(1f))), i < step ? new Color(1, 1, 1, 0.35f) : new Color(1, 1, 1, 0.12f));
            x += itemW + lineW;
        }
    }

    // ================================================================== 1 mode
    void DrawModeStep(float x0, float x1, float cTop, float bottom)
    {
        float W = x1 - x0, gap = U(14), cw = (W - 2 * gap) / 3f;
        float cy = cTop, ch = bottom - U(70) - cy;
        for (int i = 0; i < 3; i++)
            if (ModeCard(new Rect(x0 + i * (cw + gap), cy, cw, ch), i) && mode != i) { mode = i; PlayerPrefs.SetInt("gameMode", mode); }
        float en = UiKit.Enter(KeyMode, 5);
        UiKit.PushAlpha(en);
        if (UiKit.Button(NextRect(x1, bottom), "Next", UiKit.Look.Primary, true, 15f)) GoPlay(mode == ModeChampionship ? StepSetup : StepCircuit, 1);
        UiKit.PopAlpha();
    }

    // a mode card: title, one line of description, the session; selected = a red hairline (the selected state)
    bool ModeCard(Rect r, int i)
    {
        float e = UiKit.Enter(KeyMode, 1 + i, 0.05f, 0.4f);
        r = UiKit.Rise(r, e, 12f);
        UiKit.PushAlpha(e);
        bool hit = UiKit.Press(r, true, out float p);
        var c = UiKit.Scale(r, 1f - 0.025f * p);
        float s = UiKit.Smooth(0x6D00 + i, mode == i ? 1f : 0f, 12f);
        UiKit.Shadow(c, 16f, 6f, 0.18f + 0.14f * s, 2f);
        UiKit.Rect(c, Color.Lerp(UiKit.Surface, UiKit.SurfaceHi, s), 16f);
        UiKit.Outline(c, Color.Lerp(UiKit.Line, UiKit.Accent, s), 16f, 0.7f + 0.5f * s);
        float px = c.x + U(18), w = c.width - U(36);
        UiKit.Text(new Rect(px, c.y + U(20), w, U(30)), ModeTitle[i], UiKit.Bold, UiKit.Fit(ModeTitle[i], UiKit.Bold, 18f, w, 12f), UiKit.TextCol);
        UiKit.Text(new Rect(px, c.y + U(56), w, U(80)), ModeDesc[i], UiKit.Regular, 11f, UiKit.Sub, TextAnchor.UpperLeft, true);
        if (i == ModeChampionship) UiKit.Text(new Rect(px, c.yMax - U(62), w, U(16)), champStatus, UiKit.Regular, UiKit.Fit(champStatus, UiKit.Regular, 10f, w, 7.5f), UiKit.Info);
        UiKit.Eyebrow(new Rect(px, c.yMax - U(42), w, U(14)), "Session", UiKit.Faint, 8f);
        UiKit.Text(new Rect(px, c.yMax - U(29), w, U(18)), ModeHint[i], UiKit.Regular, UiKit.Fit(ModeHint[i], UiKit.Regular, 11f, w, 8f), UiKit.TextCol);
        UiKit.PopAlpha();
        return hit;
    }

    // ================================================================== 2 circuit
    void DrawCircuitStep(ref int map, Track loaded, int loadedFor, float x0, float x1, float cTop, float bottom)
    {
        float W = x1 - x0;
        var next = NextRect(x1, bottom);
        var car = new Rect(x0 + U(14), bottom - U(58), next.x - U(30) - x0 - U(14), U(58));
        map = Carousel(car, map);   // before the hero, so a tap there changes the hero this frame
        bool pg = map == playgroundIdx;
        if (map != lastMap) { UiKit.Mark(KeyMap); mapDir = lastMap < 0 || map > lastMap ? 1 : -1; lastMap = map; carCentre = true; }
        float mt = UiKit.EaseOut(UiKit.Since(KeyMap) / 0.4f);
        var info = cards[map];
        float L = W * 0.44f, bodyY = cTop;

        // ---- hero: eyebrow, name, country
        float e1 = UiKit.Enter(KeyCircuit, 1), e2 = UiKit.Enter(KeyCircuit, 2), e3 = UiKit.Enter(KeyCircuit, 3);
        UiKit.PushAlpha(e1 * mt);
        UiKit.Eyebrow(UiKit.Slide(new Rect(x0, bodyY, L, U(14)), mt, 10f * mapDir), info.eyebrow, UiKit.Faint, 9f);
        UiKit.PopAlpha();
        float ns = UiKit.Fit(info.upper, UiKit.Bold, 40f, L, 20f);
        UiKit.PushAlpha(e2 * mt);
        UiKit.Text(UiKit.Slide(UiKit.Rise(new Rect(x0, bodyY + U(12), L, U(52)), e2, 12f), mt, 18f * mapDir), info.upper, UiKit.Bold, ns, UiKit.TextCol);
        UiKit.PopAlpha();
        UiKit.PushAlpha(e3 * mt);
        UiKit.Text(UiKit.Slide(new Rect(x0, bodyY + U(62), L, U(18)), mt, 12f * mapDir), info.country, UiKit.Regular, 13f, UiKit.Sub);
        UiKit.PopAlpha();

        // ---- map (built first: it measures the length for the chips)
        var mapR = new Rect(x0 + L, bodyY - U(4), W - L, car.y - U(14) - (bodyY - U(4)));
        var pv = pg ? null : Preview(map, loaded, loadedFor);
        float e5 = UiKit.Enter(KeyCircuit, 5, 0.04f, 0.6f);
        UiKit.PushAlpha(e5 * (0.35f + 0.65f * mt));
        if (pv != null) DrawMap(UiKit.Scale(mapR, 0.96f + 0.04f * mt), pv, true);
        else if (pg) DrawTestArea(mapR);
        UiKit.PopAlpha();

        // ---- data chips (a column under the name)
        float cy = bodyY + U(90), chW = U(126), chH = U(40);
        for (int i = 0; i < 3; i++)
        {
            float ec = UiKit.Enter(KeyCircuit, 4 + i);
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

        float en = UiKit.Enter(KeyCircuit, 7);
        UiKit.PushAlpha(en);
        if (UiKit.Button(next, "Next", UiKit.Look.Primary, true, 15f)) GoPlay(StepSetup, 1);
        UiKit.PopAlpha();
        BuildThumbnails(loaded, loadedFor);
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
            setupSub = champ ? "The calendar picks the circuit  ·  full grid  ·  points"
                     : cards[Mathf.Clamp(map, 0, cards.Length - 1)].name + "  ·  " + cards[Mathf.Clamp(map, 0, cards.Length - 1)].country;
        }
        float e1 = UiKit.Enter(KeySetup, 0);
        UiKit.PushAlpha(e1);
        UiKit.Text(UiKit.Rise(new Rect(x0, cTop - U(4), W, U(30)), e1, 10f), ModeTitle[mode], UiKit.Bold, 20f, UiKit.TextCol);
        UiKit.Text(new Rect(x0, cTop + U(24), W, U(16)), setupSub, UiKit.Regular, UiKit.Fit(setupSub, UiKit.Regular, 11f, W, 8.5f), UiKit.Sub);
        UiKit.PopAlpha();

        // ---- 3 x 3 tiles
        float bh = U(52), by = bottom - bh, tg = U(10), gy = cTop + U(50);
        float th = Mathf.Min(U(60), (by - U(16) - gy - 2 * tg) / 3f), tw = (W - 2 * tg) / 3f;
        for (int i = 0; i < 9; i++)
        {
            int col = i % 3, row = i / 3;
            var r = new Rect(x0 + col * (tw + tg), gy + row * (th + tg), tw, th);
            float ei = UiKit.Enter(KeySetup, 1 + col + row);
            switch (i)
            {
                case 0: cls = SetupTile(r, "Car", CarConfig.ClassNames[cls], cls, CarConfig.ClassNames.Length, true, 0, ei); break;
                case 1:
                {
                    // the player's colours (a championship round uses the team's)
                    int li = Livery.PlayerIndex;
                    int n = SetupTile(r, "Livery", champ ? TeamColours[0] : Livery.Player[li].name, li, Livery.Player.Length, !champ, 8, ei);
                    if (n != li) { Livery.PlayerIndex = n; restyleDue = true; ch = Choice.Livery; }
                    break;
                }
                case 2: riv = SetupTile(r, "Rivals", champ ? "Full grid" : rivText[riv], riv, rivalCounts.Length, !pg && !champ, 1, ei); break;
                case 3: diff = SetupTile(r, "Rival level", diffNames[diff], diff, diffNames.Length, !pg, 2, ei); break;
                case 4: { int n = SetupTile(r, "Grid", GridNames[gridIdx], gridIdx, GridNames.Length, !pg, 3, ei); if (n != gridIdx) SetGrid(n); break; }
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

        // ---- actions (right to left; the main one is red)
        int key = lapsIdx * 1000 + riv * 10 + qualiIdx * 100000 + (champ ? 7 : 0);
        if (key != ctaKey)
        {
            ctaKey = key;
            ctaSub = champ ? "Standings  ·  next round" : lapsText[lapsIdx] + "  ·  " + (rivalCounts[riv] == 0 ? "solo" : rivalCounts[riv] + " rivals");
            ctaQualiSub = qualiText[qualiIdx] + " qualifying, then race";
        }
        float pw = Mathf.Min(U(230), W * 0.36f), sw2 = U(124), g = U(10);
        var prim = new Rect(x1 - pw, by, pw, bh);
        float eb = UiKit.Enter(KeySetup, 6);
        UiKit.PushAlpha(eb);
        if (pg)
        {
            if (UiKit.Button(prim, "Drive", UiKit.Look.Primary, true, 18f, "Free driving  ·  0-100  ·  top speed")) BeginLoading(true);
        }
        else if (mode == ModeWeekend)
        {
            if (UiKit.Button(prim, "Start weekend", UiKit.Look.Primary, true, 16f, ctaQualiSub)) BeginLoading(false, true);
            if (UiKit.Button(new Rect(prim.x - g - sw2, by, sw2, bh), "Skip quali", UiKit.Look.Ghost, true, 11.5f)) BeginLoading(false);
            if (UiKit.Button(new Rect(prim.x - 2 * (g + sw2), by, sw2, bh), "Practice", UiKit.Look.Ghost, true, 11.5f)) BeginLoading(true);
        }
        else
        {
            if (UiKit.Button(prim, champ ? "Championship" : "Start race", UiKit.Look.Primary, true, champ ? 16f : 18f, ctaSub))
            { if (champ) season.Open(); else BeginLoading(false); }
            if (UiKit.Button(new Rect(prim.x - g - sw2, by, sw2, bh), "Practice", UiKit.Look.Ghost, true, 11.5f)) BeginLoading(true);
        }
        UiKit.PopAlpha();
        return ch;
    }

    // ---- a setup tile, one row: caption over the value on the left, ( - ) ( + ) on the right; tapping the value
    // steps forward too. key: a stable number per tile (value slide). e: entry animation 0..1. Returns the new index.
    static readonly System.Collections.Generic.Dictionary<int, float> tileDir = new System.Collections.Generic.Dictionary<int, float>();
    int SetupTile(Rect r, string caption, string value, int index, int count, bool enabled, int key, float e, bool wrap = true)
    {
        r = UiKit.Rise(r, e, 8f);
        UiKit.PushAlpha(e * (enabled ? 1f : 0.4f));
        UiKit.Rect(r, UiKit.Surface, 12f);
        UiKit.Outline(r, UiKit.Line, 12f, 0.7f);
        float bw = U(44), bh = Mathf.Max(U(42), r.height - U(8));
        var plus = new Rect(r.xMax - U(4) - bw, r.center.y - bh * 0.5f, bw, bh);
        var minus = new Rect(plus.x - bw, plus.y, bw, bh);
        var body = new Rect(r.x, r.y, minus.x - r.x, r.height);
        int old = index; float dirNow = 0f; int k = 0x5E700 + key;
        if (UiKit.Press(body, enabled && count > 1, out float pb)) { index = wrap ? (index + 1) % count : Mathf.Min(count - 1, index + 1); dirNow = 1f; }
        if (pb > 0.01f) UiKit.Rect(body, new Color(1, 1, 1, 0.05f * pb), 12f);
        if (StepBtn(minus, false, enabled && (wrap || index > 0))) { index = wrap ? (index - 1 + count) % count : Mathf.Max(0, index - 1); dirNow = -1f; }
        if (StepBtn(plus, true, enabled && (wrap || index < count - 1))) { index = wrap ? (index + 1) % count : Mathf.Min(count - 1, index + 1); dirNow = 1f; }
        if (index != old) { UiKit.Mark(k); tileDir[k] = dirNow; }
        float px = r.x + U(14), vw = body.width - U(18);
        UiKit.Eyebrow(new Rect(px, r.y + r.height * 0.16f, vw, U(13)), caption, UiKit.Faint, 7.8f);
        float t = UiKit.EaseOut(UiKit.Since(k) / 0.22f), dir = tileDir.TryGetValue(k, out float sd) ? sd : 1f;
        UiKit.PushAlpha(0.25f + 0.75f * t);
        UiKit.Text(new Rect(px + U(8) * dir * (1f - t), r.y + r.height * 0.40f, vw, r.height * 0.46f), value, UiKit.Bold, UiKit.Fit(value, UiKit.Bold, 13.5f, vw, 9.5f), UiKit.TextCol);
        UiKit.PopAlpha();
        UiKit.PopAlpha();
        return index;
    }

    // a round - / + chip; the whole rect is the touch target
    static bool StepBtn(Rect r, bool plus, bool enabled)
    {
        bool hit = UiKit.Press(r, enabled, out float p);
        float d = Mathf.Min(r.width, r.height) * 0.72f;
        var chip = new Rect(r.center.x - d * 0.5f, r.center.y - d * 0.5f, d, d);
        UiKit.PushAlpha(enabled ? 1f : 0.3f);
        var cr = UiKit.Scale(chip, 1f - 0.08f * p);
        UiKit.Rect(cr, new Color(1, 1, 1, 0.07f + 0.16f * p), 999f);
        UiKit.Outline(cr, new Color(1, 1, 1, 0.10f), 999f, 0.7f);
        float w = d * 0.38f, t = Mathf.Max(1.5f, d * 0.075f);
        UiKit.Rect(new Rect(cr.center.x - w * 0.5f, cr.center.y - t * 0.5f, w, t), UiKit.TextCol, 99f);
        if (plus) UiKit.Rect(new Rect(cr.center.x - t * 0.5f, cr.center.y - w * 0.5f, t, w), UiKit.TextCol, 99f);
        UiKit.PopAlpha();
        return hit;
    }
}
