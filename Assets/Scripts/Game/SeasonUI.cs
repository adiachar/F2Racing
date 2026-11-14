// SeasonUI.cs — the championship pages and the qualifying results, drawn with IMGUI through MenuUI (same styles):
//  * Season hub: the next round, driver / team standings and the calendar (tabs), "Qualifying", "Skip qualifying"
//    (start from the menu's grid position), "New season" (asks once more before it wipes the save), "Back".
//  * Team pick: a new season starts with the player choosing one of the invented teams; shows the teammate and
//    who moves to the free seat.
//  * Qualifying results: the classification with gaps, "Start race" (grid = this order) or "Menu".
using FormulaSim;
using UnityEngine;

public sealed class SeasonUI
{
    readonly MenuUI m;
    public Qualifying lastQuali;
    int tab;                 // hub tables: 0 drivers, 1 teams, 2 calendar
    bool picking, confirmNew; int pickTeam;

    public SeasonUI(MenuUI menu) { m = menu; }

    public void ShowQuali(Qualifying q) { lastQuali = q; }

    public void Open()
    {
        if (!Season.Active) Season.Load();
        picking = !Season.Active; confirmNew = false;
        if (Season.Active) pickTeam = Season.Data.team;
        m.page = MenuUI.Page.Season;
    }

    static float S(float v) => MenuUI.S(v);
    static void Fill(Rect r, Color c) => MenuUI.Fill(r, c);

    Rect Panel(string head)
    {
        float sw = Screen.width, sh = Screen.height;
        Fill(new Rect(0, 0, sw, sh), new Color(0, 0, 0, 0.5f));
        float pw = Mathf.Min(sw - S(32), S(780)), px = (sw - pw) / 2;
        var panel = new Rect(px, S(20), pw, S(350));
        Fill(panel, MenuUI.PanelCol);
        Fill(new Rect(panel.x, panel.y, panel.width, S(3)), MenuUI.Accent);
        GUI.Label(new Rect(panel.x + S(16), panel.y + S(12), pw - S(32), S(16)), head, m.caption);
        return panel;
    }

    void BigLine(Rect panel, string big, string rest)
    {
        var ot = m.title.fontSize; var oa = m.title.alignment;
        m.title.fontSize = (int)S(30); m.title.alignment = TextAnchor.MiddleLeft;
        GUI.Label(new Rect(panel.x + S(16), panel.y + S(28), S(140), S(40)), big, m.title);
        m.title.fontSize = ot; m.title.alignment = oa;
        var ol = m.label.fontSize; m.label.fontSize = (int)S(13); m.label.richText = true;
        GUI.Label(new Rect(panel.x + S(16) + S(110), panel.y + S(30), panel.width - S(150), S(36)), rest, m.label);
        m.label.fontSize = ol;
    }

    // a row of buttons right-aligned at the bottom; returns the index tapped (-1 none). Last = primary.
    int Buttons(Rect panel, params string[] names)
    {
        float pad = S(16), tw = panel.width - 2 * pad, gap = S(10);
        int n = names.Length;
        float bw = Mathf.Min(S(170), (tw - (n - 1) * gap) / n), by = panel.yMax - pad - S(48);
        int hit = -1;
        for (int i = 0; i < n; i++)
        {
            var r = new Rect(panel.xMax - pad - (n - i) * bw - (n - 1 - i) * gap, by, bw, S(48));
            if (names[i] == null) continue;
            if (GUI.Button(r, names[i], i == n - 1 ? m.primary : m.secondary)) hit = i;
        }
        return hit;
    }

    void Cell(Rect r, string s, bool bold = false, TextAnchor a = TextAnchor.MiddleLeft)
    {
        m.cell.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal; m.cell.alignment = a; m.cell.richText = true;
        GUI.Label(r, s, m.cell);
        m.cell.fontStyle = FontStyle.Normal; m.cell.alignment = TextAnchor.MiddleLeft;
    }
    void Head(Rect r, string s, TextAnchor a = TextAnchor.MiddleLeft) { var o = m.caption.alignment; m.caption.alignment = a; GUI.Label(r, s, m.caption); m.caption.alignment = o; }
    void RowBg(Rect r, bool player, int i) => Fill(r, player ? new Color(MenuUI.Accent.r, MenuUI.Accent.g, MenuUI.Accent.b, 0.45f) : new Color(1, 1, 1, i % 2 == 0 ? 0.05f : 0.02f));

    // ---------------------------------------------------------------- season hub
    public MenuUI.Choice DrawHub(ref int map)
    {
        if (picking || !Season.Active) { DrawPick(); return MenuUI.Choice.None; }
        var d = Season.Data;
        bool over = Season.Over;
        string head = over ? $"CHAMPIONSHIP {Season.Year}  ·  SEASON COMPLETE" : $"CHAMPIONSHIP {Season.Year}  ·  ROUND {d.round + 1}/{Season.Rounds}  ·  {Season.GpNames[d.round].ToUpperInvariant()}  ·  {Circuits.All[Season.CircuitIndex(d.round)].name.ToUpperInvariant()}";
        var panel = Panel(head);
        int rank = Season.Rank(0); var st = Season.Standings();
        string champ = over ? $"Champion: <b>{Season.Name(st[0])}</b>  ·  " : "";
        BigLine(panel, "P" + rank, champ + $"<b>{d.points[0]} pts</b>  ·  {Season.Teams[d.team]}  ·  teammate {Season.Name(Season.TeammateFor(d.team) > 0 ? Season.TeammateFor(d.team) : 0)}\n<color=#ffffff99>{(over ? "All " + Season.Rounds + " rounds done" : "Full grid of " + Season.RosterCount + " cars  ·  " + MenuUI.LapCounts[m.lapsIdx] + " laps  ·  qualifying " + MenuUI.QualiMinutes[m.qualiIdx] + " min")}</color>");

        // tabs
        float pad = S(16), tx = panel.x + pad, tw = panel.width - 2 * pad, ty = panel.y + S(74);
        string[] tabs = { "DRIVERS", "TEAMS", "CALENDAR" };
        float tbw = S(120);
        for (int i = 0; i < 3; i++)
            if (GUI.Button(new Rect(tx + i * (tbw + S(6)), ty, tbw, S(28)), tabs[i], i == tab ? m.primary : m.secondary)) tab = i;
        var area = new Rect(tx, ty + S(34), tw, panel.yMax - pad - S(48) - S(8) - (ty + S(34)));
        if (tab == 0) DriverTable(area); else if (tab == 1) TeamTable(area); else CalendarTable(area);

        int hit = over ? Buttons(panel, "BACK", confirmNew ? "CONFIRM NEW" : "NEW SEASON")
                       : Buttons(panel, "BACK", confirmNew ? "CONFIRM NEW" : "NEW SEASON", "SKIP QUALIFYING", "QUALIFYING");
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

    void DriverTable(Rect a)
    {
        var st = Season.Standings(); var d = Season.Data;
        int n = st.Length, cols = 2, per = (n + 1) / 2;
        float gap = S(14), cw = (a.width - gap) / cols, rh = Mathf.Min(S(20), (a.height - S(16)) / per);
        for (int c = 0; c < cols; c++)
        {
            float x = a.x + c * (cw + gap);
            Head(new Rect(x + cw * 0.02f, a.y, cw * 0.1f, S(14)), "POS");
            Head(new Rect(x + cw * 0.14f, a.y, cw * 0.4f, S(14)), "DRIVER");
            Head(new Rect(x + cw * 0.5f, a.y, cw * 0.3f, S(14)), "TEAM");
            Head(new Rect(x + cw * 0.78f, a.y, cw * 0.2f, S(14)), "PTS", TextAnchor.MiddleRight);
        }
        for (int i = 0; i < n; i++)
        {
            int id = st[i];
            float x = a.x + (i / per) * (cw + gap), y = a.y + S(16) + (i % per) * rh;
            RowBg(new Rect(x, y + S(1), cw, rh - S(2)), id == 0, i);
            int prev = d.prevRank != null && id < d.prevRank.Length ? d.prevRank[id] : 0;
            string mv = prev <= 0 || d.results.Count < 2 ? "" : prev > i + 1 ? $" <color=#60ff80>+{prev - i - 1}</color>" : prev < i + 1 ? $" <color=#ff7070>-{i + 1 - prev}</color>" : "";
            Cell(new Rect(x + cw * 0.02f, y, cw * 0.12f, rh), (i + 1) + mv, id == 0);
            Fill(new Rect(x + cw * 0.12f, y + rh * 0.25f, S(4), rh * 0.5f), Season.Colour(id));
            Cell(new Rect(x + cw * 0.15f, y, cw * 0.35f, rh), Season.Name(id), id == 0);
            Cell(new Rect(x + cw * 0.5f, y, cw * 0.3f, rh), "<color=#ffffffaa>" + Season.Teams[Season.TeamOf(id)] + "</color>");
            Cell(new Rect(x + cw * 0.78f, y, cw * 0.2f, rh), d.points[id].ToString(), true, TextAnchor.MiddleRight);
        }
    }

    void TeamTable(Rect a)
    {
        var ts = Season.TeamStandings(); var d = Season.Data;
        int n = ts.Length;
        float rh = Mathf.Min(S(22), (a.height - S(16)) / n), w = a.width;
        Head(new Rect(a.x + w * 0.01f, a.y, w * 0.06f, S(14)), "POS");
        Head(new Rect(a.x + w * 0.08f, a.y, w * 0.3f, S(14)), "TEAM");
        Head(new Rect(a.x + w * 0.36f, a.y, w * 0.5f, S(14)), "DRIVERS");
        Head(new Rect(a.x + w * 0.86f, a.y, w * 0.13f, S(14)), "PTS", TextAnchor.MiddleRight);
        for (int i = 0; i < n; i++)
        {
            int t = ts[i]; float y = a.y + S(16) + i * rh;
            RowBg(new Rect(a.x, y + S(1), w, rh - S(2)), t == d.team, i);
            Cell(new Rect(a.x + w * 0.01f, y, w * 0.06f, rh), (i + 1).ToString(), t == d.team);
            Fill(new Rect(a.x + w * 0.065f, y + rh * 0.25f, S(4), rh * 0.5f), Season.TeamColour(t));
            Cell(new Rect(a.x + w * 0.08f, y, w * 0.28f, rh), Season.Teams[t], t == d.team);
            string ds = "";
            for (int id = 0; id < Season.RosterCount; id++) if (Season.TeamOf(id) == t) ds += (ds.Length > 0 ? ",  " : "") + Season.Name(id) + $" ({d.points[id]})";
            Cell(new Rect(a.x + w * 0.36f, y, w * 0.5f, rh), "<color=#ffffffaa>" + ds + "</color>");
            Cell(new Rect(a.x + w * 0.86f, y, w * 0.13f, rh), Season.TeamPoints(t).ToString(), true, TextAnchor.MiddleRight);
        }
    }

    void CalendarTable(Rect a)
    {
        var d = Season.Data; int n = Season.Rounds, per = (n + 1) / 2;
        float gap = S(14), cw = (a.width - gap) / 2, rh = Mathf.Min(S(30), (a.height - S(16)) / per);
        for (int c = 0; c < 2; c++)
        {
            float x = a.x + c * (cw + gap);
            Head(new Rect(x + cw * 0.02f, a.y, cw * 0.1f, S(14)), "RND");
            Head(new Rect(x + cw * 0.12f, a.y, cw * 0.5f, S(14)), "GRAND PRIX");
            Head(new Rect(x + cw * 0.6f, a.y, cw * 0.38f, S(14)), "WINNER / YOU", TextAnchor.MiddleRight);
        }
        for (int r = 0; r < n; r++)
        {
            float x = a.x + (r / per) * (cw + gap), y = a.y + S(16) + (r % per) * rh;
            bool next = r == d.round;
            RowBg(new Rect(x, y + S(1), cw, rh - S(2)), next, r);
            Cell(new Rect(x + cw * 0.02f, y, cw * 0.1f, rh), (r + 1).ToString(), next);
            Cell(new Rect(x + cw * 0.12f, y, cw * 0.5f, rh), $"{Season.GpNames[r]}  <color=#ffffff88>{Circuits.All[Season.CircuitIndex(r)].name}</color>", next);
            string res = "";
            if (r < d.results.Count)
            {
                var rr = d.results[r]; int me = System.Array.IndexOf(rr.order, 0);
                res = $"{Season.Winner(r)}  ·  you P{me + 1}";
            }
            else if (next) res = "NEXT";
            Cell(new Rect(x + cw * 0.6f, y, cw * 0.38f, rh), res, next, TextAnchor.MiddleRight);
        }
    }

    // ---------------------------------------------------------------- team pick (new season)
    void DrawPick()
    {
        var panel = Panel($"NEW CHAMPIONSHIP {Season.Year}  ·  PICK YOUR TEAM");
        int nt = Season.Teams.Count; pickTeam = Mathf.Clamp(pickTeam, 0, nt - 1);
        float pad = S(16), x = panel.x + pad, w = panel.width - 2 * pad, y = panel.y + S(36);
        float sw = Mathf.Min(S(360), w);
        pickTeam = m.Stepper(new Rect(x, y, sw, S(44)), pickTeam, nt, Season.Teams[pickTeam]);
        Fill(new Rect(x + sw + S(12), y + S(6), S(8), S(32)), Season.TeamColour(pickTeam));
        int mate = Season.TeammateFor(pickTeam), moved = Season.DisplacedFor(pickTeam);
        string info = mate > 0 ? $"Teammate: <b>{Season.Name(mate)}</b>  (pace {DriverProfile.Grid[mate - 1].pace:0.00})" : "No teammate";
        if (moved > 0) info += $"\n{Season.Name(moved)} moves to {Season.Teams[Mathf.Max(0, Season.OpenTeam())]}";
        var ol = m.label.fontSize; m.label.fontSize = (int)S(13); m.label.richText = true; m.label.wordWrap = true;
        GUI.Label(new Rect(x + sw + S(28), y - S(2), w - sw - S(28), S(48)), info, m.label);
        m.label.fontSize = ol; m.label.wordWrap = false;

        // every team and its drivers, the picked one highlighted (tap a row to pick it)
        var a = new Rect(x, y + S(54), w, panel.yMax - pad - S(48) - S(8) - (y + S(54)));
        int per = (nt + 1) / 2; float gap = S(14), cw = (a.width - gap) / 2, rh = Mathf.Min(S(26), a.height / per);
        for (int t = 0; t < nt; t++)
        {
            float rx = a.x + (t / per) * (cw + gap), ry = a.y + (t % per) * rh;
            var r = new Rect(rx, ry + S(1), cw, rh - S(2));
            RowBg(r, t == pickTeam, t);
            Fill(new Rect(rx + S(4), ry + rh * 0.25f, S(4), rh * 0.5f), Season.TeamColour(t));
            string ds = ""; foreach (var id in Season.GridDriversOf(t)) ds += (ds.Length > 0 ? ", " : "") + Season.Name(id);
            Cell(new Rect(rx + S(12), ry, cw * 0.4f, rh), Season.Teams[t], t == pickTeam);
            Cell(new Rect(rx + S(12) + cw * 0.4f, ry, cw * 0.6f - S(14), rh), "<color=#ffffffaa>" + ds + "</color>");
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) pickTeam = t;
        }

        int hit = Buttons(panel, "BACK", "START SEASON");
        if (hit == 0) { picking = false; if (!Season.Active) m.page = MenuUI.Page.Start; }
        else if (hit == 1) { Season.New(pickTeam); picking = false; tab = 0; }
    }

    // ---------------------------------------------------------------- qualifying results
    public MenuUI.Choice DrawQuali()
    {
        var q = lastQuali;
        if (q == null) { m.page = MenuUI.Page.Start; return MenuUI.Choice.None; }
        var panel = Panel($"QUALIFYING  ·  {q.circuit.ToUpperInvariant()}  ·  {Qualifying.Clock(q.length)}");
        var list = q.Classified(); double pole = q.Fastest;
        int me = list.FindIndex(e => e.Player);
        var pe = me >= 0 ? list[me] : null;
        BigLine(panel, "P" + (me + 1), $"of {list.Count}  ·  your best <b>{Qualifying.Fmt(pe != null ? pe.best : double.NaN)}</b>" +
            (list.Count > 0 && !double.IsNaN(pole) ? $"  ·  pole {list[0].name} {Qualifying.Fmt(pole)}" : "") +
            "\n<color=#ffffff99>The race grid is set from this order</color>");

        float pad = S(16), tx = panel.x + pad, tw = panel.width - 2 * pad, ty = panel.y + S(78);
        int n = list.Count, cols = n > 11 ? 2 : 1, per = (n + cols - 1) / Mathf.Max(1, cols);
        float gap = S(14), cw = (tw - (cols - 1) * gap) / cols;
        float th = panel.yMax - pad - S(48) - S(8) - ty - S(16), rh = Mathf.Min(S(24), th / Mathf.Max(1, per));
        for (int c = 0; c < cols; c++)
        {
            float x = tx + c * (cw + gap);
            Head(new Rect(x + cw * 0.02f, ty, cw * 0.1f, S(14)), "POS");
            Head(new Rect(x + cw * 0.14f, ty, cw * 0.4f, S(14)), "DRIVER");
            Head(new Rect(x + cw * 0.56f, ty, cw * 0.22f, S(14)), "TIME");
            Head(new Rect(x + cw * 0.78f, ty, cw * 0.2f, S(14)), "GAP", TextAnchor.MiddleRight);
        }
        for (int i = 0; i < n; i++)
        {
            var e = list[i];
            float x = tx + (i / per) * (cw + gap), y = ty + S(16) + (i % per) * rh;
            RowBg(new Rect(x, y + S(1), cw, rh - S(2)), e.Player, i);
            Cell(new Rect(x + cw * 0.02f, y, cw * 0.1f, rh), (i + 1).ToString(), e.Player);
            Fill(new Rect(x + cw * 0.11f, y + rh * 0.25f, S(4), rh * 0.5f), e.colour);
            Cell(new Rect(x + cw * 0.14f, y, cw * 0.42f, rh), e.name, e.Player);
            Cell(new Rect(x + cw * 0.56f, y, cw * 0.22f, rh), i == 0 && !double.IsNaN(e.best) ? $"<color=#c070ff>{Qualifying.Fmt(e.best)}</color>" : Qualifying.Fmt(e.best));
            string gapS = double.IsNaN(e.best) ? "no time" : i == 0 ? "Pole" : $"+{e.best - pole:0.000}";
            Cell(new Rect(x + cw * 0.78f, y, cw * 0.2f, rh), gapS, false, TextAnchor.MiddleRight);
        }

        int hit = Buttons(panel, "MENU", "START RACE");
        if (hit == 0) { if (m.mode == MenuUI.ModeChampionship) Open(); else m.page = MenuUI.Page.Start; }
        else if (hit == 1) return MenuUI.Choice.StartRace;
        return MenuUI.Choice.None;
    }
}
