// SeasonUI.cs — the championship pages and the qualifying results, drawn with IMGUI through MenuUI (UiKit look):
//  * Season hub: the next Grand Prix as the page title, your standing in a card (team colour, position, points,
//    teammate), driver / team standings and the calendar (segmented tabs), "Qualifying", "Skip qualifying" (start
//    from the menu's grid position), "New season" (asks once more before it wipes the save), "Back".
//  * Team pick: a new season starts with the player choosing one of the invented teams from a grid of team cards;
//    shows the teammate and who moves to the free seat.
//  * Qualifying results: your position large, pole time, the classification with gaps, "Start race" (grid = this
//    order) or "Menu".
// Tables and strings are rebuilt only when the season changes (a race recorded, a new season), not every frame.
using FormulaSim;
using UnityEngine;

public sealed class SeasonUI
{
    readonly MenuUI m;
    public Qualifying lastQuali;
    int tab;                 // hub tables: 0 drivers, 1 teams, 2 calendar
    bool picking, confirmNew; int pickTeam;
    static readonly string[] TabNames = { "Drivers", "Teams", "Calendar" };

    public SeasonUI(MenuUI menu) { m = menu; }

    public void ShowQuali(Qualifying q) { lastQuali = q; }

    public void Open()
    {
        if (!Season.Active) Season.Load();
        picking = !Season.Active; confirmNew = false;
        if (Season.Active) pickTeam = Season.Data.team;
        m.page = MenuUI.Page.Season;
    }

    static float U(float v) => UiKit.U(v);

    // ---------------------------------------------------------------- shared page parts
    // page frame: backdrop and the content box inside the safe area; returns x0, x1, top and the buttons' y
    struct Frame { public float x0, x1, w, top, by, bh; }
    static Frame Page()
    {
        UiKit.Backdrop(1f);
        var s = UiKit.Safe; var f = new Frame();
        f.x0 = s.x + U(22); f.x1 = s.xMax - U(22); f.w = f.x1 - f.x0; f.top = s.y + U(14);
        f.bh = U(46); f.by = s.yMax - U(14) - f.bh;
        return f;
    }

    // eyebrow (red caps), big title (Black), one line under it
    static void Header(string scr, Frame f, float w, string eyebrow, string title, string sub)
    {
        float e0 = UiKit.Enter(scr, 0), e1 = UiKit.Enter(scr, 1), e2 = UiKit.Enter(scr, 2);
        UiKit.PushAlpha(e0); UiKit.Eyebrow(new Rect(f.x0, f.top, w, U(16)), eyebrow, UiKit.AccentHi, 9.5f); UiKit.PopAlpha();
        UiKit.PushAlpha(e1);
        UiKit.Text(UiKit.Rise(new Rect(f.x0 - U(1), f.top + U(12), w, U(42)), e1, 12f), title, UiKit.Black, UiKit.Fit(title, UiKit.Black, 34f, w, 20f), UiKit.TextCol);
        UiKit.PopAlpha();
        if (sub != null) { UiKit.PushAlpha(e2); UiKit.Text(new Rect(f.x0, f.top + U(54), w, U(18)), sub, UiKit.Semi, UiKit.Fit(sub, UiKit.Semi, 12f, w, 9f), UiKit.Sub); UiKit.PopAlpha(); }
    }

    // a button in the bottom-right row: slot 0 = right-most; count = how many share the row
    static bool Btn(Frame f, int slot, int count, string label, UiKit.Look look, float size = 12.5f)
    {
        float gap = U(10), bw = Mathf.Min(U(172), (f.w - (count - 1) * gap) / count);
        var r = new Rect(f.x1 - (slot + 1) * bw - slot * gap, f.by, bw, f.bh);
        return UiKit.Button(r, label, look, true, look == UiKit.Look.Primary ? 14f : size);
    }

    static void Head(Rect r, string s, TextAnchor a = TextAnchor.MiddleLeft) => UiKit.Eyebrow(r, s, UiKit.Faint, 8f, a);

    // ---------------------------------------------------------------- season cache
    int cacheKey = int.MinValue;
    int[] st, ts; int rank;
    string[] drvPts, drvMove, drvTeam, teamPts, teamDrivers, calGp, calCircuit, calRes;
    Color[] moveCol;
    string hubEyebrow, hubTitle, hubSub, statPos, statPts, statTeam, statMate;

    void Refresh()
    {
        var d = Season.Data;
        int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(d) ^ (d.round * 7919) ^ (d.results.Count * 104729) ^ (m.lapsIdx * 131) ^ (m.qualiIdx * 1031) ^ (d.team * 17);
        if (key == cacheKey && st != null) return;
        cacheKey = key;
        bool over = Season.Over;
        st = Season.Standings(); ts = Season.TeamStandings(); rank = System.Array.IndexOf(st, 0) + 1;
        int n = st.Length;
        drvPts = new string[n]; drvMove = new string[n]; drvTeam = new string[n]; moveCol = new Color[n];
        for (int i = 0; i < n; i++)
        {
            int id = st[i];
            drvPts[i] = d.points[id].ToString();
            drvTeam[i] = Season.Teams[Season.TeamOf(id)];
            int prev = d.prevRank != null && id < d.prevRank.Length ? d.prevRank[id] : 0;
            if (prev > 0 && d.results.Count >= 2 && prev != i + 1)
            {
                bool up = prev > i + 1;
                drvMove[i] = up ? "+" + (prev - i - 1) : "-" + (i + 1 - prev);
                moveCol[i] = up ? UiKit.Good : new Color(1f, 0.38f, 0.36f);
            }
        }
        teamPts = new string[ts.Length]; teamDrivers = new string[ts.Length];
        for (int i = 0; i < ts.Length; i++)
        {
            int t = ts[i]; teamPts[i] = Season.TeamPoints(t).ToString();
            var sb = new System.Text.StringBuilder();
            for (int id = 0; id < Season.RosterCount; id++) if (Season.TeamOf(id) == t) { if (sb.Length > 0) sb.Append("   "); sb.Append(Season.Name(id)).Append("  ").Append(d.points[id]); }
            teamDrivers[i] = sb.ToString();
        }
        int R = Season.Rounds;
        calGp = new string[R]; calCircuit = new string[R]; calRes = new string[R];
        for (int r = 0; r < R; r++)
        {
            calGp[r] = Season.GpNames[r]; calCircuit[r] = Circuits.All[Season.CircuitIndex(r)].name;
            if (r < d.results.Count) { int me = System.Array.IndexOf(d.results[r].order, 0); calRes[r] = Season.Winner(r) + "  ·  you P" + (me + 1); }
        }
        hubEyebrow = over ? $"Championship {Season.Year}  ·  season complete" : $"Championship {Season.Year}  ·  round {d.round + 1} / {R}";
        hubTitle = over ? "SEASON COMPLETE" : Season.GpNames[d.round].ToUpperInvariant() + " GRAND PRIX";
        hubSub = over ? "Champion: " + Season.Name(st[0]) + "  ·  all " + R + " rounds done"
                      : Circuits.All[Season.CircuitIndex(d.round)].name + "  ·  full grid of " + Season.RosterCount + "  ·  " + MenuUI.LapCounts[m.lapsIdx] + " laps  ·  qualifying " + MenuUI.QualiMinutes[m.qualiIdx] + " min";
        statPos = "P" + rank; statPts = d.points[0] + " PTS"; statTeam = Season.Teams[d.team];
        int mate = Season.TeammateFor(d.team);
        statMate = "Teammate  " + Season.Name(mate > 0 ? mate : 0);
    }

    // ---------------------------------------------------------------- season hub
    public MenuUI.Choice DrawHub(ref int map)
    {
        if (picking || !Season.Active) { DrawPick(); return MenuUI.Choice.None; }
        Refresh();
        var d = Season.Data; bool over = Season.Over;
        const string scr = "season";
        var f = Page();

        // your standing (top right)
        float cw = U(250), ch = U(72);
        var card = new Rect(f.x1 - cw, f.top + U(2), cw, ch);
        float ec = UiKit.Enter(scr, 2, 0.04f, 0.45f);
        UiKit.PushAlpha(ec);
        card = UiKit.Slide(card, ec, 20f);
        UiKit.Panel(card, 14f);
        UiKit.Bar(new Rect(card.x + U(10), card.y + U(12), U(4), ch - U(24)), Season.TeamColour(d.team));
        UiKit.Text(new Rect(card.x + U(22), card.y, U(90), ch), statPos, UiKit.Black, 34f, UiKit.TextCol);
        float tx = card.x + U(22) + UiKit.Width(statPos, UiKit.Black, 34f) + U(12), tw = card.xMax - U(10) - tx;
        UiKit.Tag(new Rect(tx, card.y + U(12), UiKit.TagWidth(statPts, 9.5f), U(18)), statPts, UiKit.Accent, Color.white, 9.5f);
        UiKit.Text(new Rect(tx, card.y + U(32), tw, U(16)), statTeam, UiKit.Bold, UiKit.Fit(statTeam, UiKit.Bold, 12f, tw, 9f), UiKit.TextCol);
        UiKit.Text(new Rect(tx, card.y + U(47), tw, U(14)), statMate, UiKit.Regular, UiKit.Fit(statMate, UiKit.Regular, 9.5f, tw, 8f), UiKit.Sub);
        UiKit.PopAlpha();

        Header(scr, f, f.w - cw - U(20), hubEyebrow, hubTitle, hubSub);

        // tabs and the table
        float ty = f.top + U(84);
        float et = UiKit.Enter(scr, 3);
        UiKit.PushAlpha(et);
        tab = UiKit.Segmented(new Rect(f.x0, ty, Mathf.Min(U(330), f.w), U(34)), TabNames, tab, false, true, 11f);
        UiKit.PopAlpha();
        var panel = new Rect(f.x0, ty + U(42), f.w, f.by - U(10) - (ty + U(42)));
        float ep = UiKit.Enter(scr, 4, 0.04f, 0.45f);
        UiKit.PushAlpha(ep);
        panel = UiKit.Rise(panel, ep, 12f);
        UiKit.Panel(panel, 14f);
        var area = new Rect(panel.x + U(10), panel.y + U(6), panel.width - U(20), panel.height - U(10));
        if (tab == 0) DriverTable(area); else if (tab == 1) TeamTable(area); else CalendarTable(area);
        UiKit.PopAlpha();

        // buttons (right to left; the last action = primary)
        float eb = UiKit.Enter(scr, 6);
        UiKit.PushAlpha(eb);
        int hit = -1;
        if (over)
        {
            if (Btn(f, 0, 2, confirmNew ? "Confirm new" : "New season", confirmNew ? UiKit.Look.Armed : UiKit.Look.Primary)) hit = 1;
            if (Btn(f, 1, 2, "Back", UiKit.Look.Ghost)) hit = 0;
        }
        else
        {
            if (Btn(f, 0, 4, "Qualifying", UiKit.Look.Primary)) hit = 3;
            if (Btn(f, 1, 4, "Skip qualifying", UiKit.Look.Secondary, 11.5f)) hit = 2;
            if (Btn(f, 2, 4, confirmNew ? "Confirm new" : "New season", confirmNew ? UiKit.Look.Armed : UiKit.Look.Ghost, 11.5f)) hit = 1;
            if (Btn(f, 3, 4, "Back", UiKit.Look.Ghost)) hit = 0;
        }
        UiKit.PopAlpha();
        if (hit == 0) { confirmNew = false; m.page = MenuUI.Page.Start; }
        else if (hit == 1) { if (confirmNew) { picking = true; confirmNew = false; } else confirmNew = true; }
        else if (hit == 2 || hit == 3)
        {
            confirmNew = false;
            map = Season.CircuitIndex(d.round);
            m.BeginLoading(false, hit == 3);
        }
        return MenuUI.Choice.None;
    }

    static readonly string[] Ord = { "", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "21", "22", "23", "24", "25", "26", "27", "28", "29", "30" };
    static string O(int i) => i < Ord.Length ? Ord[i] : i.ToString();

    void DriverTable(Rect a)
    {
        int n = st.Length, cols = 2, per = (n + 1) / 2;
        float gap = U(18), cw = (a.width - gap) / cols, rh = Mathf.Min(U(22), (a.height - U(16)) / per), fs = Mathf.Clamp(rh / U(1) * 0.58f, 9f, 11.5f);
        for (int c = 0; c < cols; c++)
        {
            float x = a.x + c * (cw + gap);
            Head(new Rect(x + U(6), a.y, cw * 0.1f, U(14)), "Pos");
            Head(new Rect(x + cw * 0.17f, a.y, cw * 0.4f, U(14)), "Driver");
            Head(new Rect(x + cw * 0.52f, a.y, cw * 0.3f, U(14)), "Team");
            Head(new Rect(x + cw * 0.80f, a.y, cw * 0.18f, U(14)), "Pts", TextAnchor.MiddleRight);
        }
        for (int i = 0; i < n; i++)
        {
            int id = st[i]; bool me = id == 0;
            float x = a.x + (i / per) * (cw + gap), y = a.y + U(16) + (i % per) * rh;
            UiKit.RowBg(new Rect(x, y + U(1), cw, rh - U(2)), i, me);
            UiKit.Text(new Rect(x + U(8), y, cw * 0.06f, rh), O(i + 1), UiKit.Bold, fs, i < 3 ? UiKit.TextCol : UiKit.Sub);
            if (drvMove[i] != null) UiKit.Text(new Rect(x + cw * 0.075f, y, cw * 0.07f, rh), drvMove[i], UiKit.Bold, fs * 0.8f, moveCol[i]);
            UiKit.Bar(new Rect(x + cw * 0.14f, y + rh * 0.24f, U(3), rh * 0.52f), Season.Colour(id));
            UiKit.Text(new Rect(x + cw * 0.17f, y, cw * 0.35f, rh), Season.Name(id), me ? UiKit.Bold : UiKit.Semi, fs, UiKit.TextCol);
            UiKit.Text(new Rect(x + cw * 0.52f, y, cw * 0.28f, rh), drvTeam[i], UiKit.Regular, fs * 0.92f, UiKit.Sub);
            UiKit.Text(new Rect(x + cw * 0.80f, y, cw * 0.18f, rh), drvPts[i], UiKit.Bold, fs, UiKit.TextCol, TextAnchor.MiddleRight);
        }
    }

    void TeamTable(Rect a)
    {
        int n = ts.Length, myTeam = Season.Data.team;
        float rh = Mathf.Min(U(24), (a.height - U(16)) / n), w = a.width, fs = Mathf.Clamp(rh / U(1) * 0.55f, 9f, 12f);
        Head(new Rect(a.x + U(6), a.y, w * 0.06f, U(14)), "Pos");
        Head(new Rect(a.x + w * 0.08f, a.y, w * 0.3f, U(14)), "Team");
        Head(new Rect(a.x + w * 0.36f, a.y, w * 0.5f, U(14)), "Drivers");
        Head(new Rect(a.x + w * 0.86f, a.y, w * 0.13f, U(14)), "Pts", TextAnchor.MiddleRight);
        for (int i = 0; i < n; i++)
        {
            int t = ts[i]; float y = a.y + U(16) + i * rh; bool me = t == myTeam;
            UiKit.RowBg(new Rect(a.x, y + U(1), w, rh - U(2)), i, me);
            UiKit.Text(new Rect(a.x + U(8), y, w * 0.05f, rh), O(i + 1), UiKit.Bold, fs, i < 3 ? UiKit.TextCol : UiKit.Sub);
            UiKit.Bar(new Rect(a.x + w * 0.062f, y + rh * 0.24f, U(3), rh * 0.52f), Season.TeamColour(t));
            UiKit.Text(new Rect(a.x + w * 0.08f, y, w * 0.28f, rh), Season.Teams[t], me ? UiKit.Bold : UiKit.Semi, fs, UiKit.TextCol);
            UiKit.Text(new Rect(a.x + w * 0.36f, y, w * 0.5f, rh), teamDrivers[i], UiKit.Regular, fs * 0.9f, UiKit.Sub);
            UiKit.Text(new Rect(a.x + w * 0.86f, y, w * 0.13f, rh), teamPts[i], UiKit.Bold, fs, UiKit.TextCol, TextAnchor.MiddleRight);
        }
    }

    void CalendarTable(Rect a)
    {
        var d = Season.Data; int n = Season.Rounds, per = (n + 1) / 2;
        float gap = U(18), cw = (a.width - gap) / 2, rh = Mathf.Min(U(32), (a.height - U(16)) / per), fs = Mathf.Clamp(rh / U(1) * 0.42f, 9f, 12f);
        for (int c = 0; c < 2; c++)
        {
            float x = a.x + c * (cw + gap);
            Head(new Rect(x + U(6), a.y, cw * 0.1f, U(14)), "Rnd");
            Head(new Rect(x + cw * 0.12f, a.y, cw * 0.5f, U(14)), "Grand Prix");
            Head(new Rect(x + cw * 0.55f, a.y, cw * 0.43f, U(14)), "Winner / you", TextAnchor.MiddleRight);
        }
        for (int r = 0; r < n; r++)
        {
            float x = a.x + (r / per) * (cw + gap), y = a.y + U(16) + (r % per) * rh;
            bool next = r == d.round, done = r < d.results.Count;
            UiKit.RowBg(new Rect(x, y + U(1), cw, rh - U(2)), r, next);
            UiKit.Text(new Rect(x + U(8), y, cw * 0.08f, rh), O(r + 1), UiKit.Bold, fs, done ? UiKit.Faint : UiKit.TextCol);
            float gw = UiKit.Width(calGp[r], next ? UiKit.Bold : UiKit.Semi, fs);
            UiKit.Text(new Rect(x + cw * 0.12f, y, cw * 0.5f, rh), calGp[r], next ? UiKit.Bold : UiKit.Semi, fs, done ? UiKit.Sub : UiKit.TextCol);
            UiKit.Text(new Rect(x + cw * 0.12f + gw + U(8), y, cw * 0.3f, rh), calCircuit[r], UiKit.Regular, fs * 0.85f, UiKit.Faint);
            var rr = new Rect(x + cw * 0.55f, y, cw * 0.43f, rh);
            if (done) UiKit.Text(rr, calRes[r], UiKit.Semi, fs * 0.92f, UiKit.Sub, TextAnchor.MiddleRight);
            else if (next) { float w = UiKit.TagWidth("NEXT", 8.5f); UiKit.Tag(new Rect(rr.xMax - w, y + rh * 0.5f - U(8), w, U(16)), "NEXT", UiKit.Accent, Color.white, 8.5f); }
        }
    }

    // ---------------------------------------------------------------- team pick (new season)
    int pickInfoFor = -1; string pickInfo, pickEyebrow; string[] teamLine1, teamLine2;
    void DrawPick()
    {
        const string scr = "pick";
        int nt = Season.Teams.Count; pickTeam = Mathf.Clamp(pickTeam, 0, nt - 1);
        if (teamLine1 == null || teamLine1.Length != nt)
        {
            teamLine1 = new string[nt]; teamLine2 = new string[nt];
            for (int t = 0; t < nt; t++)
            {
                var ids = Season.GridDriversOf(t);
                teamLine1[t] = ids.Count > 0 ? Season.Name(ids[0]) : "";
                teamLine2[t] = ids.Count > 1 ? Season.Name(ids[1]) : "Open seat";
            }
            pickEyebrow = $"New championship {Season.Year}  ·  {Season.Rounds} rounds";
        }
        if (pickInfoFor != pickTeam)
        {
            pickInfoFor = pickTeam;
            int mate = Season.TeammateFor(pickTeam), moved = Season.DisplacedFor(pickTeam);
            pickInfo = Season.Teams[pickTeam] + "  ·  " + (mate > 0 ? $"teammate {Season.Name(mate)} (pace {DriverProfile.Grid[mate - 1].pace:0.00})" : "no teammate");
            if (moved > 0) pickInfo += $"  ·  {Season.Name(moved)} moves to {Season.Teams[Mathf.Max(0, Season.OpenTeam())]}";
        }
        var f = Page();
        Header(scr, f, f.w, pickEyebrow, "PICK YOUR TEAM", pickInfo);

        // a grid of team cards; tap one to pick it
        int cols = Mathf.Max(1, (nt + 1) / 2), rows = (nt + cols - 1) / cols;
        var a = new Rect(f.x0, f.top + U(84), f.w, f.by - U(12) - (f.top + U(84)));
        float gap = U(8), cw = (a.width - (cols - 1) * gap) / cols, ch = Mathf.Min(U(110), (a.height - (rows - 1) * gap) / rows);
        for (int t = 0; t < nt; t++)
        {
            int c = t % cols, r = t / cols;
            var cr = new Rect(a.x + c * (cw + gap), a.y + r * (ch + gap), cw, ch);
            float e = UiKit.Enter(scr, 3 + c + r, 0.035f);
            UiKit.PushAlpha(e);
            cr = UiKit.Rise(cr, e, 10f);
            if (UiKit.Press(cr, true, out float p)) pickTeam = t;
            bool on = t == pickTeam;
            float s = UiKit.Smooth(0x7E00 + t, on ? 1f : 0f, 12f);
            var dr = UiKit.Scale(cr, 1f - 0.03f * p);
            if (s > 0.02f) UiKit.Shadow(dr, 14f, 8f, 0.35f * s, 3f);
            UiKit.Rect(dr, Color.Lerp(new Color(0.09f, 0.10f, 0.13f, 0.85f), new Color(0.15f, 0.16f, 0.21f, 0.96f), s), 14f);
            UiKit.Outline(dr, Color.Lerp(new Color(1, 1, 1, 0.07f), UiKit.Accent, s), 14f, 0.7f + 0.6f * s);
            var col = Season.TeamColour(t);
            UiKit.Bar(new Rect(dr.x + U(12), dr.y + U(12), dr.width - U(24), U(4)), col);
            UiKit.Glow(new Rect(dr.x - dr.width * 0.2f, dr.y - dr.height * 0.6f, dr.width * 1.4f, dr.height * 1.2f), UiKit.A(col, 0.10f + 0.12f * s));
            float px = dr.x + U(12), w = dr.width - U(24);
            UiKit.Text(new Rect(px, dr.y + U(22), w, U(20)), Season.Teams[t], UiKit.Bold, UiKit.Fit(Season.Teams[t], UiKit.Bold, 13.5f, w, 9f), UiKit.TextCol);
            UiKit.Text(new Rect(px, dr.yMax - U(36), w, U(14)), teamLine1[t], UiKit.Regular, UiKit.Fit(teamLine1[t], UiKit.Regular, 10f, w, 8f), UiKit.Sub);
            UiKit.Text(new Rect(px, dr.yMax - U(22), w, U(14)), teamLine2[t], UiKit.Regular, UiKit.Fit(teamLine2[t], UiKit.Regular, 10f, w, 8f), UiKit.Sub);
            UiKit.PopAlpha();
        }

        float eb = UiKit.Enter(scr, 6);
        UiKit.PushAlpha(eb);
        int hit = -1;
        if (Btn(f, 0, 2, "Start season", UiKit.Look.Primary)) hit = 1;
        if (Btn(f, 1, 2, "Back", UiKit.Look.Ghost)) hit = 0;
        UiKit.PopAlpha();
        if (hit == 0) { picking = false; if (!Season.Active) m.page = MenuUI.Page.Start; }
        else if (hit == 1) { Season.New(pickTeam); picking = false; tab = 0; cacheKey = int.MinValue; }
    }

    // ---------------------------------------------------------------- qualifying results
    Qualifying qFor; System.Collections.Generic.List<Qualifying.Entry> qList;
    string[] qTime, qGap; string qHead, qPos, qOf, qBest, qPole, qPoleName; int qMe;

    void CacheQuali(Qualifying q)
    {
        qFor = q; qList = q.Classified(); double pole = q.Fastest;
        int n = qList.Count; qTime = new string[n]; qGap = new string[n];
        qMe = qList.FindIndex(e => e.Player);
        for (int i = 0; i < n; i++)
        {
            var e = qList[i];
            qTime[i] = Qualifying.Fmt(e.best);
            qGap[i] = double.IsNaN(e.best) ? "no time" : i == 0 ? "Pole" : $"+{e.best - pole:0.000}";
        }
        qHead = $"Qualifying  ·  {q.circuit}  ·  {Qualifying.Clock(q.length)}";
        qPos = "P" + (qMe + 1); qOf = "of " + n + "  ·  the race grid is set from this order";
        qBest = Qualifying.Fmt(qMe >= 0 ? qList[qMe].best : double.NaN);
        qPole = n > 0 && !double.IsNaN(pole) ? Qualifying.Fmt(pole) : "—"; qPoleName = n > 0 ? qList[0].name : "";
    }

    public MenuUI.Choice DrawQuali()
    {
        var q = lastQuali;
        if (q == null) { m.page = MenuUI.Page.Start; return MenuUI.Choice.None; }
        if (!ReferenceEquals(q, qFor) || qList == null) CacheQuali(q);
        const string scr = "quali";
        var f = Page();
        float e0 = UiKit.Enter(scr, 0), e1 = UiKit.Enter(scr, 1), e2 = UiKit.Enter(scr, 2);
        UiKit.PushAlpha(e0); UiKit.Eyebrow(new Rect(f.x0, f.top, f.w, U(16)), qHead, UiKit.AccentHi, 9.5f); UiKit.PopAlpha();
        UiKit.PushAlpha(e1);
        UiKit.Text(UiKit.Rise(new Rect(f.x0 - U(2), f.top + U(10), U(160), U(66)), e1, 14f), qPos, UiKit.Black, 62f, UiKit.TextCol);
        float bx = f.x0 + UiKit.Width(qPos, UiKit.Black, 62f) + U(12);
        UiKit.Text(new Rect(bx, f.top + U(30), f.w * 0.5f, U(18)), qOf, UiKit.Bold, 12.5f, UiKit.Sub);
        UiKit.PopAlpha();

        // chips: your best, pole (purple)
        UiKit.PushAlpha(e2);
        float cx = f.x0, cy = f.top + U(82), chH = U(36);
        float w1 = UiKit.ChipWidth("Your best", qBest); UiKit.Chip(new Rect(cx, cy, w1, chH), "Your best", qBest, qMe == 0 ? UiKit.Purple : UiKit.TextCol); cx += w1 + U(8);
        string poleCap = "Pole  ·  " + qPoleName;
        float w2 = UiKit.ChipWidth(poleCap, qPole); UiKit.Chip(new Rect(cx, cy, w2, chH), poleCap, qPole, UiKit.Purple);
        UiKit.PopAlpha();

        // classification
        var tp = new Rect(f.x0, f.top + U(128), f.w, f.by - U(10) - (f.top + U(128)));
        float et = UiKit.Enter(scr, 4, 0.04f, 0.45f);
        UiKit.PushAlpha(et);
        tp = UiKit.Rise(tp, et, 12f);
        UiKit.Panel(tp, 14f);
        int n = qList.Count, cols = n > 9 ? 2 : 1, per = (n + cols - 1) / Mathf.Max(1, cols);
        float pad = U(10), tx = tp.x + pad, tw = tp.width - 2 * pad, gap = U(18);
        if (cols == 1) tw = Mathf.Min(tw, U(560));
        float cw = (tw - (cols - 1) * gap) / cols, ty = tp.y + U(6);
        float rh = Mathf.Min(U(24), (tp.yMax - pad * 0.6f - ty - U(16)) / Mathf.Max(1, per)), fs = Mathf.Clamp(rh / U(1) * 0.55f, 9f, 12f);
        for (int c = 0; c < cols; c++)
        {
            float x = tx + c * (cw + gap);
            Head(new Rect(x + U(6), ty, cw * 0.1f, U(14)), "Pos");
            Head(new Rect(x + cw * 0.15f, ty, cw * 0.4f, U(14)), "Driver");
            Head(new Rect(x + cw * 0.55f, ty, cw * 0.22f, U(14)), "Time");
            Head(new Rect(x + cw * 0.76f, ty, cw * 0.22f, U(14)), "Gap", TextAnchor.MiddleRight);
        }
        for (int i = 0; i < n; i++)
        {
            var e = qList[i];
            float x = tx + (i / per) * (cw + gap), y = ty + U(16) + (i % per) * rh;
            UiKit.RowBg(new Rect(x, y + U(1), cw, rh - U(2)), i, e.Player);
            UiKit.Text(new Rect(x + U(8), y, cw * 0.08f, rh), O(i + 1), UiKit.Bold, fs, i < 3 ? UiKit.TextCol : UiKit.Sub);
            UiKit.Bar(new Rect(x + cw * 0.115f, y + rh * 0.24f, U(3), rh * 0.52f), e.colour);
            UiKit.Text(new Rect(x + cw * 0.15f, y, cw * 0.40f, rh), e.name, e.Player ? UiKit.Bold : UiKit.Semi, fs, UiKit.TextCol);
            bool pole = i == 0 && !double.IsNaN(e.best);
            if (pole) UiKit.Dot(new Vector2(x + cw * 0.55f - U(6), y + rh * 0.5f), 2.2f, UiKit.Purple);
            UiKit.Text(new Rect(x + cw * 0.55f, y, cw * 0.22f, rh), qTime[i], pole ? UiKit.Bold : UiKit.Semi, fs, pole ? UiKit.Purple : UiKit.Sub);
            UiKit.Text(new Rect(x + cw * 0.76f, y, cw * 0.22f, rh), qGap[i], UiKit.Semi, fs, UiKit.Sub, TextAnchor.MiddleRight);
        }
        UiKit.PopAlpha();

        float eb = UiKit.Enter(scr, 6);
        UiKit.PushAlpha(eb);
        int hit = -1;
        if (Btn(f, 0, 2, "Start race", UiKit.Look.Primary)) hit = 1;
        if (Btn(f, 1, 2, "Menu", UiKit.Look.Secondary)) hit = 0;
        UiKit.PopAlpha();
        if (hit == 0) { if (m.mode == MenuUI.ModeChampionship) Open(); else m.page = MenuUI.Page.Start; }
        else if (hit == 1) return MenuUI.Choice.StartRace;
        return MenuUI.Choice.None;
    }
}
