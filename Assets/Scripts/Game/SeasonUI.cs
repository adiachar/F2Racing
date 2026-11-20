// SeasonUI.cs — the championship pages and the qualifying results, drawn with IMGUI through MenuUI in the "Paper"
// look (UiKit.cs: paper page, masthead, ink rules, text links left and the black button right under the bottom rule):
//  * Season hub (opened from the Play flow's Setup step; PLAY underlined): the next Grand Prix as the page title, your
//    standing on the right (position as the red italic figure, points, team with its colour, teammate), driver / team
//    standings and the calendar (text tabs, the chosen one underlined in red) as ruled tables, the black "Qualifying",
//    and links "Skip qualifying" (start from the menu's grid position), "New season" (asks once more before it wipes
//    the save), "Back" (to Setup).
//  * Team pick: a new season starts with the player choosing one of the invented teams from a grid of ruled team
//    cells (the chosen one with a red rule); shows the teammate and who moves to the free seat.
//  * Qualifying results: your position as the giant red italic figure, your best and pole, the classification with
//    gaps, the black "Start race" (grid = this order) or the "Menu" link.
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

    public void ShowQuali(Qualifying q)
    {
        lastQuali = q;
        var l = q.Classified(); // once per session: a pole counts in the driver's career stats (MenuDriver.cs)
        if (l.Count > 1 && l[0].Player && !double.IsNaN(l[0].best)) MenuProfile.RecordPole();
    }

    public void Open()
    {
        if (!Season.Active) Season.Load();
        picking = !Season.Active; confirmNew = false;
        if (Season.Active) pickTeam = Season.Data.team;
        m.page = MenuUI.Page.Season;
    }

    static float U(float v) => UiKit.U(v);

    // ---------------------------------------------------------------- shared page parts
    // page frame: the paper page and the masthead (nav: the PLAY item underlined; false: the wordmark and a caption)
    UiKit.Frame Page(bool nav, string caption = null)
    {
        UiKit.PaperPage();
        if (nav) return m.Masthead(UiKit.NavPlay, 72f);
        var f = UiKit.PaperFrame(72f);
        UiKit.Masthead(f, UiKit.NavNone, false, caption);
        return f;
    }

    // kicker, big title (ExtraBold), one line under it
    static void Header(string scr, UiKit.Frame f, float w, string eyebrow, string title, string sub)
    {
        float e0 = UiKit.Enter(scr, 0), e1 = UiKit.Enter(scr, 1), e2 = UiKit.Enter(scr, 2);
        UiKit.PushAlpha(e0); UiKit.Kicker(new Rect(f.x0, f.top - U(4), w, U(14)), eyebrow, UiKit.Fit(UiKit.Caps(eyebrow), UiKit.Body, 10f, w, 7f)); UiKit.PopAlpha();
        UiKit.PushAlpha(e1);
        UiKit.Text(UiKit.Rise(new Rect(f.x0 - U(1), f.top + U(8), w, U(30)), e1, 10f), title, UiKit.Heavy, UiKit.Fit(title, UiKit.Heavy, 24f, w, 16f), UiKit.Ink);
        UiKit.PopAlpha();
        if (sub != null) { UiKit.PushAlpha(e2); UiKit.Text(new Rect(f.x0, f.top + U(37), w, U(16)), sub, UiKit.Body, UiKit.Fit(sub, UiKit.Body, 11f, w, 8f), UiKit.InkSub); UiKit.PopAlpha(); }
    }

    // the action row: the rule above it, the black button on the right; returns true when it is tapped
    static bool Primary(UiKit.Frame f, string label)
    {
        UiKit.Hair(f.x0, f.x1, f.foot);
        float w = Mathf.Max(U(170), UiKit.BlackButtonWidth(label));
        return UiKit.BlackButton(new Rect(f.x1 - w, f.actY - U(24), w, U(48)), label);
    }
    // a link in the action row at x; returns true when tapped, x moves on past it
    static bool LinkAt(UiKit.Frame f, ref float x, string title, string sub, bool armed = false)
    {
        float w = UiKit.LinkWidth(title, sub) + U(8);
        bool hit = UiKit.Link(new Rect(x, f.actY - U(22), w, U(44)), title, sub, true, 13f, TextAnchor.MiddleLeft, armed);
        x += w + U(22);
        return hit;
    }

    static void Head(Rect r, string s, TextAnchor a = TextAnchor.MiddleLeft) => UiKit.Kicker(r, s, 8.5f, a);
    // the ink rule under a table's column heads
    static void HeadRule(float x0, float x1, float y) => UiKit.Hair(x0, x1, y, UiKit.Ink);

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
                moveCol[i] = up ? UiKit.GoodInk : UiKit.InkSub; // gains good (green), drops quiet grey
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
        hubEyebrow = over ? $"Apex World Series {Season.Year}  ·  season complete" : $"Apex World Series {Season.Year}  ·  round {d.round + 1} / {R}";
        hubTitle = over ? "SEASON COMPLETE" : Season.GpNames[d.round].ToUpperInvariant() + " ROUND";
        hubSub = over ? "Champion: " + Season.Name(st[0]) + "  ·  all " + R + " rounds done"
                      : Circuits.All[Season.CircuitIndex(d.round)].name + "  ·  full grid of " + Season.RosterCount + "  ·  " + MenuUI.LapCounts[m.lapsIdx] + " laps  ·  qualifying " + MenuUI.QualiMinutes[m.qualiIdx] + " min";
        statPos = "P" + rank; statPts = d.points[0] + " pts"; statTeam = Season.Teams[d.team];
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
        var f = Page(true);

        // your standing (top right): the position as the red italic figure, points, team (its colour), teammate
        float cw = U(240);
        float ec = UiKit.Enter(scr, 2, 0.04f, 0.45f);
        UiKit.PushAlpha(ec);
        var card = UiKit.Slide(new Rect(f.x1 - cw, f.top - U(6), cw, U(58)), ec, 16f);
        float pw = UiKit.Width(statPos, UiKit.HeavyItalic, 46f), tx = card.xMax - U(130), tw = U(130);
        UiKit.BigNumber(new Rect(tx - U(12) - pw, card.y, pw + U(4), card.height), statPos, 46f, UiKit.Red);
        UiKit.Text(new Rect(tx, card.y + U(4), tw, U(18)), statPts, UiKit.Heavy, 15f, UiKit.Ink, TextAnchor.MiddleRight);
        float tmw = UiKit.Width(statTeam, UiKit.Medium, 11f);
        UiKit.Text(new Rect(tx, card.y + U(22), tw, U(16)), statTeam, UiKit.Medium, UiKit.Fit(statTeam, UiKit.Medium, 11f, tw - U(14), 8f), UiKit.Ink, TextAnchor.MiddleRight);
        float sq = Mathf.Round(U(7f));
        UiKit.Rect(new Rect(card.xMax - Mathf.Min(tmw, tw - U(14)) - U(6) - sq, card.y + U(30) - sq * 0.5f, sq, sq), Season.TeamColour(d.team));
        UiKit.Text(new Rect(tx - U(40), card.y + U(38), tw + U(40), U(14)), statMate, UiKit.Body, UiKit.Fit(statMate, UiKit.Body, 10f, tw + U(40), 7.5f), UiKit.InkSub, TextAnchor.MiddleRight);
        UiKit.PopAlpha();

        Header(scr, f, f.w - cw - U(24), hubEyebrow, hubTitle, hubSub);

        // tabs and the table
        float ty = f.top + U(58);
        float et = UiKit.Enter(scr, 3);
        UiKit.PushAlpha(et);
        tab = UiKit.Tabs(new Rect(f.x0, ty, f.w, U(22)), TabNames, tab, 12f);
        UiKit.PopAlpha();
        var area = new Rect(f.x0, ty + U(30), f.w, f.foot - U(6) - (ty + U(30)));
        float ep = UiKit.Enter(scr, 4, 0.04f, 0.45f);
        UiKit.PushAlpha(ep);
        area = UiKit.Rise(area, ep, 10f);
        if (tab == 0) DriverTable(area); else if (tab == 1) TeamTable(area); else CalendarTable(area);
        UiKit.PopAlpha();

        // the action row: links left (Back, New season, Skip qualifying), the black button right
        float eb = UiKit.Enter(scr, 6);
        UiKit.PushAlpha(eb);
        int hit = -1; float lx = f.x0;
        if (LinkAt(f, ref lx, "\u2039  Back", "Setup")) hit = 0;
        if (over)
        {
            if (Primary(f, confirmNew ? "Confirm new" : "New season")) hit = 1;
        }
        else
        {
            if (Primary(f, "Qualifying")) hit = 3;
            if (LinkAt(f, ref lx, "Skip qualifying", "Start from your grid setting")) hit = 2;
            if (LinkAt(f, ref lx, confirmNew ? "Tap again: new season" : "New season", confirmNew ? "This season is wiped" : "Start over with a team", confirmNew)) hit = 1;
        }
        UiKit.PopAlpha();
        if (hit == 0) { confirmNew = false; m.GoPlay(MenuUI.StepSetup, -1); }
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
        float gap = U(24), cw = (a.width - gap) / cols, rh = Mathf.Min(U(22), (a.height - U(16)) / per), fs = Mathf.Clamp(rh / U(1) * 0.62f, 8.5f, 11.5f);
        for (int c = 0; c < cols; c++)
        {
            float x = a.x + c * (cw + gap);
            Head(new Rect(x + U(8), a.y, cw * 0.1f, U(14)), "Pos");
            Head(new Rect(x + cw * 0.17f, a.y, cw * 0.4f, U(14)), "Driver");
            Head(new Rect(x + cw * 0.52f, a.y, cw * 0.3f, U(14)), "Team");
            Head(new Rect(x + cw * 0.80f, a.y, cw * 0.18f, U(14)), "Pts", TextAnchor.MiddleRight);
            HeadRule(x, x + cw, a.y + U(15));
        }
        for (int i = 0; i < n; i++)
        {
            int id = st[i]; bool me = id == 0;
            float x = a.x + (i / per) * (cw + gap), y = a.y + U(16) + (i % per) * rh;
            UiKit.PaperRow(new Rect(x, y, cw, rh), me);
            UiKit.Text(new Rect(x + U(8), y, cw * 0.06f, rh), O(i + 1), UiKit.Heavy, fs, i < 3 ? UiKit.Ink : UiKit.InkSub);
            if (drvMove[i] != null) UiKit.Text(new Rect(x + cw * 0.075f, y, cw * 0.07f, rh), drvMove[i], UiKit.Heavy, fs * 0.8f, moveCol[i]);
            UiKit.Rect(new Rect(x + cw * 0.14f, y + rh * 0.26f, Mathf.Max(2f, Mathf.Round(U(2.5f))), rh * 0.48f), Season.Colour(id));
            UiKit.Text(new Rect(x + cw * 0.17f, y, cw * 0.35f, rh), Season.Name(id), me ? UiKit.Heavy : UiKit.Medium, fs, UiKit.Ink);
            UiKit.Text(new Rect(x + cw * 0.52f, y, cw * 0.28f, rh), drvTeam[i], UiKit.Body, fs * 0.92f, UiKit.InkSub);
            UiKit.Text(new Rect(x + cw * 0.80f, y, cw * 0.18f, rh), drvPts[i], UiKit.Heavy, fs, UiKit.Ink, TextAnchor.MiddleRight);
        }
    }

    void TeamTable(Rect a)
    {
        int n = ts.Length, myTeam = Season.Data.team;
        float rh = Mathf.Min(U(24), (a.height - U(16)) / n), w = a.width, fs = Mathf.Clamp(rh / U(1) * 0.55f, 9f, 12f);
        Head(new Rect(a.x + U(8), a.y, w * 0.06f, U(14)), "Pos");
        Head(new Rect(a.x + w * 0.08f, a.y, w * 0.3f, U(14)), "Team");
        Head(new Rect(a.x + w * 0.36f, a.y, w * 0.5f, U(14)), "Drivers");
        Head(new Rect(a.x + w * 0.86f, a.y, w * 0.13f, U(14)), "Pts", TextAnchor.MiddleRight);
        HeadRule(a.x, a.xMax, a.y + U(15));
        for (int i = 0; i < n; i++)
        {
            int t = ts[i]; float y = a.y + U(16) + i * rh; bool me = t == myTeam;
            UiKit.PaperRow(new Rect(a.x, y, w, rh), me);
            UiKit.Text(new Rect(a.x + U(8), y, w * 0.05f, rh), O(i + 1), UiKit.Heavy, fs, i < 3 ? UiKit.Ink : UiKit.InkSub);
            UiKit.Rect(new Rect(a.x + w * 0.062f, y + rh * 0.26f, Mathf.Max(2f, Mathf.Round(U(2.5f))), rh * 0.48f), Season.TeamColour(t));
            UiKit.Text(new Rect(a.x + w * 0.08f, y, w * 0.28f, rh), Season.Teams[t], me ? UiKit.Heavy : UiKit.Medium, fs, UiKit.Ink);
            UiKit.Text(new Rect(a.x + w * 0.36f, y, w * 0.5f, rh), teamDrivers[i], UiKit.Body, fs * 0.9f, UiKit.InkSub);
            UiKit.Text(new Rect(a.x + w * 0.86f, y, w * 0.13f, rh), teamPts[i], UiKit.Heavy, fs, UiKit.Ink, TextAnchor.MiddleRight);
        }
    }

    void CalendarTable(Rect a)
    {
        var d = Season.Data; int n = Season.Rounds, per = (n + 1) / 2;
        float gap = U(24), cw = (a.width - gap) / 2, rh = Mathf.Min(U(32), (a.height - U(16)) / per), fs = Mathf.Clamp(rh / U(1) * 0.42f, 9f, 12f);
        for (int c = 0; c < 2; c++)
        {
            float x = a.x + c * (cw + gap);
            Head(new Rect(x + U(8), a.y, cw * 0.1f, U(14)), "Rnd");
            Head(new Rect(x + cw * 0.12f, a.y, cw * 0.5f, U(14)), "Round");
            Head(new Rect(x + cw * 0.55f, a.y, cw * 0.43f, U(14)), "Winner / you", TextAnchor.MiddleRight);
            HeadRule(x, x + cw, a.y + U(15));
        }
        for (int r = 0; r < n; r++)
        {
            float x = a.x + (r / per) * (cw + gap), y = a.y + U(16) + (r % per) * rh;
            bool next = r == d.round, done = r < d.results.Count;
            UiKit.PaperRow(new Rect(x, y, cw, rh), next);
            UiKit.Text(new Rect(x + U(8), y, cw * 0.08f, rh), O(r + 1), UiKit.Heavy, fs, done ? UiKit.InkSub : UiKit.Ink);
            float gw = UiKit.Width(calGp[r], next ? UiKit.Heavy : UiKit.Medium, fs);
            UiKit.Text(new Rect(x + cw * 0.12f, y, cw * 0.5f, rh), calGp[r], next ? UiKit.Heavy : UiKit.Medium, fs, done ? UiKit.InkSub : UiKit.Ink);
            UiKit.Text(new Rect(x + cw * 0.12f + gw + U(8), y, cw * 0.3f, rh), calCircuit[r], UiKit.Body, fs * 0.85f, UiKit.InkSub);
            var rr = new Rect(x + cw * 0.55f, y, cw * 0.43f, rh);
            if (done) UiKit.Text(rr, calRes[r], UiKit.Body, fs * 0.92f, UiKit.InkSub, TextAnchor.MiddleRight);
            else if (next) { float w = UiKit.StampWidth("NEXT", 8f); UiKit.Stamp(new Rect(rr.xMax - w, y + rh * 0.5f - U(7.5f), w, U(15)), "NEXT", UiKit.Ink, Color.white, 8f); }
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
            pickEyebrow = $"New Apex World Series {Season.Year}  ·  {Season.Rounds} rounds";
        }
        if (pickInfoFor != pickTeam)
        {
            pickInfoFor = pickTeam;
            int mate = Season.TeammateFor(pickTeam), moved = Season.DisplacedFor(pickTeam);
            pickInfo = Season.Teams[pickTeam] + "  ·  " + (mate > 0 ? $"teammate {Season.Name(mate)} (pace {DriverProfile.Grid[mate - 1].pace:0.00})" : "no teammate");
            if (moved > 0) pickInfo += $"  ·  {Season.Name(moved)} moves to {Season.Teams[Mathf.Max(0, Season.OpenTeam())]}";
        }
        var f = Page(true);
        Header(scr, f, f.w, pickEyebrow, "Pick your team", pickInfo);

        // a grid of ruled team cells; tap one to pick it (the chosen one: a red rule over it and a tint)
        int cols = Mathf.Max(1, (nt + 1) / 2), rows = (nt + cols - 1) / cols;
        var a = new Rect(f.x0, f.top + U(62), f.w, f.foot - U(10) - (f.top + U(62)));
        float gap = U(16), cw = (a.width - (cols - 1) * gap) / cols, ch = Mathf.Min(U(96), (a.height - (rows - 1) * U(10)) / rows);
        for (int t = 0; t < nt; t++)
        {
            int c = t % cols, r = t / cols;
            var cr = new Rect(a.x + c * (cw + gap), a.y + r * (ch + U(10)), cw, ch);
            float e = UiKit.Enter(scr, 3 + c + r, 0.035f);
            UiKit.PushAlpha(e);
            cr = UiKit.Rise(cr, e, 10f);
            if (UiKit.Press(cr, true, out float p)) pickTeam = t;
            bool on = t == pickTeam;
            float s = UiKit.Smooth(0x7E00 + t, on ? 1f : 0f, 12f);
            if (s > 0.01f || p > 0.01f) UiKit.Rect(cr, UiKit.A(UiKit.PaperTint, Mathf.Max(s, p)));
            UiKit.Hair(cr.x, cr.xMax, cr.y, UiKit.Ink);
            if (s > 0.01f) UiKit.Rect(new Rect(cr.x, cr.y, cr.width * s, Mathf.Max(3f, Mathf.Round(U(3f)))), UiKit.Red);
            float px = cr.x + U(10), w = cr.width - U(20);
            UiKit.Rect(new Rect(px, cr.y + U(14), U(18), Mathf.Max(3f, Mathf.Round(U(4f)))), Season.TeamColour(t)); // team colour
            UiKit.Text(new Rect(px, cr.y + U(22), w, U(22)), Season.Teams[t], UiKit.Heavy, UiKit.Fit(Season.Teams[t], UiKit.Heavy, 13.5f, w, 9f), UiKit.Ink);
            UiKit.Text(new Rect(px, cr.yMax - U(34), w, U(14)), teamLine1[t], UiKit.Body, UiKit.Fit(teamLine1[t], UiKit.Body, 10f, w, 8f), UiKit.InkSub);
            UiKit.Text(new Rect(px, cr.yMax - U(20), w, U(14)), teamLine2[t], UiKit.Body, UiKit.Fit(teamLine2[t], UiKit.Body, 10f, w, 8f), UiKit.InkSub);
            UiKit.PopAlpha();
        }

        float eb = UiKit.Enter(scr, 6);
        UiKit.PushAlpha(eb);
        int hit = -1; float lx = f.x0;
        if (Primary(f, "Start season")) hit = 1;
        if (LinkAt(f, ref lx, "\u2039  Back", Season.Active ? "Season hub" : "Setup")) hit = 0;
        UiKit.PopAlpha();
        if (hit == 0) { picking = false; if (!Season.Active) m.GoPlay(MenuUI.StepSetup, -1); }
        else if (hit == 1) { Season.New(pickTeam); picking = false; tab = 0; cacheKey = int.MinValue; }
    }

    // ---------------------------------------------------------------- qualifying results
    Qualifying qFor; System.Collections.Generic.List<Qualifying.Entry> qList;
    string[] qTime, qGap; string qHead, qPos, qOf, qBest, qPole, qPoleName, qPoleCap; int qMe;

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
        qPoleCap = "Pole  ·  " + qPoleName;
    }

    public MenuUI.Choice DrawQuali()
    {
        var q = lastQuali;
        if (q == null) { m.GoHome(); return MenuUI.Choice.None; }
        if (!ReferenceEquals(q, qFor) || qList == null) CacheQuali(q);
        const string scr = "quali";
        var f = Page(false, qHead);
        float e1 = UiKit.Enter(scr, 1), e2 = UiKit.Enter(scr, 2);

        // data: your best, pole (right-aligned on the header line)
        UiKit.PushAlpha(e2);
        float dx = f.x1, dy = UiKit.Rise(new Rect(0, f.top + U(4), 0, 0), e2, 8f).y;
        float w2 = UiKit.DatumWidth(qPoleCap, qPole, 16f); UiKit.Datum(new Rect(dx - w2, dy, w2, U(36)), qPoleCap, qPole, UiKit.Ink, 16f, TextAnchor.MiddleRight); dx -= w2 + U(26);
        float w1 = UiKit.DatumWidth("Your best", qBest, 16f); UiKit.Datum(new Rect(dx - w1, dy, w1, U(36)), "Your best", qBest, UiKit.Ink, 16f, TextAnchor.MiddleRight); dx -= w1 + U(26);
        UiKit.PopAlpha();

        // the position: the giant red italic figure, "of 20 · the race grid is set from this order"
        UiKit.PushAlpha(e1);
        const float ps = 78f;
        UiKit.BigNumber(UiKit.Rise(new Rect(f.x0 - U(3), f.top - U(6), U(220), U(72)), e1, 14f), qPos, ps, UiKit.Red);
        float bx = f.x0 + UiKit.Width(qPos, UiKit.HeavyItalic, ps) + U(14), bw = Mathf.Max(U(60), dx - bx);
        UiKit.Text(new Rect(bx, f.top + U(16), bw, U(20)), qOf, UiKit.Heavy, UiKit.Fit(qOf, UiKit.Heavy, 14f, bw, 9f), UiKit.Ink);
        UiKit.PopAlpha();

        // classification
        var tp = new Rect(f.x0, f.top + U(74), f.w, f.foot - U(8) - (f.top + U(74)));
        float et = UiKit.Enter(scr, 4, 0.04f, 0.45f);
        UiKit.PushAlpha(et);
        tp = UiKit.Rise(tp, et, 12f);
        int n = qList.Count, cols = n > 9 ? 2 : 1, per = (n + cols - 1) / Mathf.Max(1, cols);
        float tx = tp.x, tw = tp.width, gap = U(24);
        if (cols == 1) tw = Mathf.Min(tw, U(560));
        float cw = (tw - (cols - 1) * gap) / cols, ty = tp.y;
        float rh = Mathf.Min(U(24), (tp.yMax - ty - U(16)) / Mathf.Max(1, per)), fs = Mathf.Clamp(rh / U(1) * 0.55f, 9f, 12f);
        for (int c = 0; c < cols; c++)
        {
            float x = tx + c * (cw + gap);
            Head(new Rect(x + U(8), ty, cw * 0.1f, U(14)), "Pos");
            Head(new Rect(x + cw * 0.15f, ty, cw * 0.4f, U(14)), "Driver");
            Head(new Rect(x + cw * 0.55f, ty, cw * 0.22f, U(14)), "Time");
            Head(new Rect(x + cw * 0.76f, ty, cw * 0.22f, U(14)), "Gap", TextAnchor.MiddleRight);
            HeadRule(x, x + cw, ty + U(15));
        }
        for (int i = 0; i < n; i++)
        {
            var e = qList[i];
            float x = tx + (i / per) * (cw + gap), y = ty + U(16) + (i % per) * rh;
            UiKit.PaperRow(new Rect(x, y, cw, rh), e.Player);
            UiKit.Text(new Rect(x + U(8), y, cw * 0.08f, rh), O(i + 1), UiKit.Heavy, fs, i < 3 ? UiKit.Ink : UiKit.InkSub);
            UiKit.Rect(new Rect(x + cw * 0.115f, y + rh * 0.26f, Mathf.Max(2f, Mathf.Round(U(2.5f))), rh * 0.48f), e.colour);
            UiKit.Text(new Rect(x + cw * 0.15f, y, cw * 0.40f, rh), e.name, e.Player ? UiKit.Heavy : UiKit.Medium, fs, UiKit.Ink);
            bool pole = i == 0 && !double.IsNaN(e.best);
            UiKit.Text(new Rect(x + cw * 0.55f, y, cw * 0.22f, rh), qTime[i], pole ? UiKit.Heavy : UiKit.Body, fs, pole ? UiKit.Ink : UiKit.InkSub);
            UiKit.Text(new Rect(x + cw * 0.76f, y, cw * 0.22f, rh), qGap[i], pole ? UiKit.Medium : UiKit.Body, fs, pole ? UiKit.Ink : UiKit.InkSub, TextAnchor.MiddleRight);
        }
        UiKit.PopAlpha();

        float eb = UiKit.Enter(scr, 6);
        UiKit.PushAlpha(eb);
        int hit = -1; float lx = f.x0;
        if (Primary(f, "Start race")) hit = 1;
        if (LinkAt(f, ref lx, "Menu", m.mode == MenuUI.ModeChampionship ? "Season hub" : "Home")) hit = 0;
        UiKit.PopAlpha();
        if (hit == 0) { if (m.mode == MenuUI.ModeChampionship) Open(); else m.GoHome(); }
        else if (hit == 1) return MenuUI.Choice.StartRace;
        return MenuUI.Choice.None;
    }
}
