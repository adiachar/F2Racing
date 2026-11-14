// Qualifying.cs — the qualifying session of a race weekend, and the weekend's session flow (part of GameManager).
//  * Qualifying: a timed session (MenuUI.QualiMinutes, game time). The player drives alone on track with a few
//    rivals running for show; every rival's timing is simulated: runs of out lap + two flying laps, back to the
//    garage, out again; the last run times its flying lap to start just before the flag. A lap's time is the
//    driver's own AI speed profile at this circuit (RacingLine.SpeedProfile with his pace, as AiDriver builds it)
//    with a spread from his consistency, a rare mistake (slow or deleted lap) and track evolution through the session.
//  * Chequered flag: a lap started before it still counts (the player's too). "END" fast-forwards the session.
//  * Weekend flow: Qualifying -> results -> race from that grid; or skip qualifying and start from the menu's grid
//    position (rivals ordered by pace). Championship rounds use the whole season field (Season.cs).
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public sealed class Qualifying
{
    public sealed class Entry
    {
        public DriverProfile who;            // null = the player
        public bool Player => who == null;
        public string name, team; public Color colour;
        public double best = double.NaN, last = double.NaN, shownAt = -100;
        public bool lastPb, lastDeleted; public int laps;
        // simulated running (rivals)
        public double baseLap, lapEndAt = double.PositiveInfinity, curLap; public bool curDeleted; public int runLaps;
    }

    public readonly List<Entry> entries = new List<Entry>();
    public readonly double length; public double clock; public bool flag;
    public readonly string circuit;
    readonly System.Random rng;

    public Qualifying(double length, string circuit, int seed) { this.length = length; this.circuit = circuit; rng = new System.Random(seed); }

    public double Left => System.Math.Max(0, length - clock);

    public void AddPlayer(string name, string team, Color c) => entries.Add(new Entry { who = null, name = name, team = team, colour = c });

    public void AddRival(DriverProfile who, double baseLap)
    {
        var e = new Entry { who = who, name = who.name, team = who.team, colour = new Color(who.r, who.g, who.b), baseLap = baseLap };
        entries.Add(e);
        double leave = length * (0.03 + 0.4 * rng.NextDouble()); // out of the garage some time in the first part
        StartLap(e, leave + baseLap * 1.25);
    }

    double Gauss() { double u = 1 - rng.NextDouble(), v = rng.NextDouble(); return System.Math.Sqrt(-2 * System.Math.Log(u)) * System.Math.Cos(2 * System.Math.PI * v); }

    // a flying lap starting at "at" (no lap starts after the flag)
    void StartLap(Entry e, double at)
    {
        if (at >= length) { e.lapEndAt = double.PositiveInfinity; return; }
        double cons = Mathf.Clamp01((float)e.who.consistency);
        double spread = 0.0015 + 0.009 * (1 - cons);
        double evo = 1 - 0.006 * System.Math.Min(1, at / length); // more rubber, faster track
        double t = e.baseLap * evo * (1 + spread * Gauss());
        e.curDeleted = false;
        if (rng.NextDouble() < 0.03 + 0.22 * (1 - cons))
        {
            if (rng.NextDouble() < 0.4) e.curDeleted = true;              // track limits
            else t += e.baseLap * (0.01 + 0.04 * rng.NextDouble());       // a mistake
        }
        e.curLap = t; e.lapEndAt = at + t;
    }

    void Complete(Entry e)
    {
        e.laps++; e.last = e.curLap; e.lastDeleted = e.curDeleted; e.shownAt = e.lapEndAt;
        e.lastPb = !e.curDeleted && (double.IsNaN(e.best) || e.curLap < e.best);
        if (e.lastPb) e.best = e.curLap;
    }

    void Advance(double until)
    {
        foreach (var e in entries)
        {
            if (e.Player) continue;
            int guard = 0;
            while (e.lapEndAt <= until && guard++ < 50)
            {
                Complete(e); e.runLaps++;
                double end = e.lapEndAt;
                if (e.runLaps % 2 == 1) StartLap(e, end); // second flying lap straight on
                else
                {
                    // in lap, time in the garage, out lap; the last run is timed to start its lap just before the flag
                    double inOut = e.baseLap * 2.55, next = end + inOut + 60 + 90 * rng.NextDouble(), earliest = end + inOut + 30;
                    if (next >= length && earliest < length) next = System.Math.Max(earliest, length - 2 - 15 * rng.NextDouble());
                    StartLap(e, next);
                }
            }
        }
    }

    public void Tick(double dt) { if (dt > 0) { clock += dt; Advance(clock); } }

    public void SetPlayerBest(double? best)
    {
        foreach (var e in entries)
        {
            if (!e.Player || !best.HasValue) continue;
            if (double.IsNaN(e.best) || best.Value < e.best - 1e-9) { e.best = best.Value; e.last = best.Value; e.lastPb = true; e.shownAt = clock; e.laps++; }
        }
    }

    // the flag has fallen and the player is in: every lap still running completes, the session is over
    public void Finish() { if (clock < length) clock = length; Advance(double.PositiveInfinity); }

    public double Fastest { get { double b = double.NaN; foreach (var e in entries) if (!double.IsNaN(e.best) && (double.IsNaN(b) || e.best < b)) b = e.best; return b; } }

    // the classification: fastest first; drivers without a time at the back (rivals by pace, then the player)
    public List<Entry> Classified()
    {
        var l = new List<Entry>(entries);
        l.Sort((a, b) =>
        {
            bool na = double.IsNaN(a.best), nb = double.IsNaN(b.best);
            if (na != nb) return na ? 1 : -1;
            if (!na) return a.best.CompareTo(b.best);
            if (a.Player != b.Player) return a.Player ? 1 : -1;
            return a.baseLap.CompareTo(b.baseLap);
        });
        return l;
    }

    public static string Fmt(double t)
    {
        if (double.IsNaN(t) || double.IsInfinity(t) || t <= 0) return "—";
        int m = (int)(t / 60); return $"{m}:{t - m * 60:00.000}";
    }
    public static string Clock(double t) { int s = Mathf.CeilToInt((float)t); return $"{s / 60}:{s % 60:00}"; }
}

public partial class GameManager
{
    // ---------------------------------------------------------------- weekend session flow (hooks in StartSession,
    // SetupRivals, ChooseGridSlot, Update, OnGUI and FinishRace)
    Qualifying quali;                    // the running qualifying session (null = none)
    bool qualiSession;                   // the session being started / running is qualifying
    int sessionMode;                     // MenuUI mode of the running weekend: 0 quick race, 1 weekend, 2 championship
    List<DriverProfile> raceField;       // rivals in grid order for the race (null = the quick race's spread of the grid)
    int gridSlotOverride = -1;           // the player's slot from qualifying (-1 = the start menu's "Grid position")
    bool pendingGrid;                    // the next race start uses raceField / gridSlotOverride from qualifying
    int qFlagLap = -1; double qPrevTime;
    const int QualiOnTrack = 5;          // rivals driven on track during qualifying (for show)

    bool InSeasonRound => sessionMode == MenuUI.ModeChampionship && Season.Active && !Season.Over;

    // StartSession, before the circuit is built
    void PrepareSession()
    {
        sessionMode = ui.mode;
        qualiSession = ui.quali && !playground;
        quali = null;
        if (sessionMode == MenuUI.ModeChampionship && Season.Active && !Season.Over) mapIdx = Season.CircuitIndex(Season.Data.round);
        if (qualiSession) raceLaps = 0;
        else if (raceLaps > 0)
        {
            if (!pendingGrid) { raceField = sessionMode == MenuUI.ModeQuick ? null : PaceOrder(WeekendField()); gridSlotOverride = -1; }
            pendingGrid = false;
        }
        ui.quali = false;
    }

    // StartSession, after the car is reset: the qualifying timing and the player's livery
    void SessionStarted()
    {
        if (model != null)
            model.SetLivery(InSeasonRound ? Livery.ForTeam(Season.Data.team, Season.TeamColour(Season.Data.team)) : Livery.Player[liveryIdx]);
        if (!qualiSession || line == null) { qualiSession = false; return; }
        bestLap = lastLap = null; lapNo = 1;
        quali = new Qualifying(MenuUI.QualiMinutes[ui.qualiIdx] * 60.0, MapNames[mapIdx], System.Environment.TickCount);
        quali.AddPlayer(Season.PlayerName, InSeasonRound ? Season.Teams[Season.Data.team] : "", InSeasonRound ? Season.TeamColour(Season.Data.team) : new Color(1f, 0.25f, 0.2f));
        foreach (var who in WeekendField()) quali.AddRival(who, BaseLap(who));
        qFlagLap = -1; qPrevTime = raceTime;
        Flash($"Qualifying  ·  {MenuUI.QualiMinutes[ui.qualiIdx]} min");
    }

    // the rivals of this weekend
    List<DriverProfile> WeekendField()
    {
        if (InSeasonRound) return Season.Field();
        var l = new List<DriverProfile>(); var grid = DriverProfile.Grid;
        int count = RivalCounts[rivalIdx];
        for (int k = 0; k < count; k++) l.Add(grid[count == 1 ? 0 : Mathf.RoundToInt(k * (grid.Length - 1) / (float)(count - 1))]);
        return l;
    }
    static List<DriverProfile> PaceOrder(List<DriverProfile> l) { var o = new List<DriverProfile>(l); o.Sort((a, b) => b.pace.CompareTo(a.pace)); return o; }

    // a rival's clean qualifying lap here: his own speed profile on this line, as AiDriver builds it, plus a little
    // for the driving itself (the AI never quite matches its profile)
    double BaseLap(DriverProfile who)
    {
        int set = Weather.BestSet();
        var rc = CarConfig.ForClass(classIdx); rc.compound = Weather.PhysicsCompound(set);
        var tune = AiTuning.For(builtFor); tune.grip *= Weather.TrackFactor(set, 1);
        double diff = DiffScale[diffIdx];
        double grip = tune.grip * diff * (0.86 + 0.14 * who.pace), brake = tune.brakeUse * diff * (0.84 + 0.16 * who.pace);
        var keepS = line.speed; var keepB = line.braking; var keepP = line.phase; var keepC = line.vCorner; double keepT = line.lapTime;
        var keepD = line.deploy; var keepE = line.soc;
        line.SpeedProfile(rc, grip, brake);
        double t = line.lapTime;
        line.speed = keepS; line.braking = keepB; line.phase = keepP; line.vCorner = keepC; line.lapTime = keepT; line.deploy = keepD; line.soc = keepE;
        return (t > 0 ? t : RefLap()) * 1.012;
    }

    // SetupRivals: the field for this session (null = the quick race's own picks). May change the car count.
    List<DriverProfile> SessionField(ref int count)
    {
        if (playground || line == null) return null;
        if (qualiSession)
        {
            var f = WeekendField(); var l = new List<DriverProfile>();
            int n = Mathf.Min(QualiOnTrack, f.Count);
            for (int k = 0; k < n; k++) l.Add(f[n == 1 ? 0 : Mathf.RoundToInt(k * (f.Count - 1) / (float)(n - 1))]);
            count = l.Count; return l;
        }
        if (raceLaps > 0 && raceField != null) { count = Mathf.Min(raceField.Count, StartGrid.Slots - 1); return raceField; }
        return null;
    }

    // SetupRivals, after the cars are made: in qualifying the show cars are spread round the lap, already rolling
    void AfterSetupRivals()
    {
        if (!qualiSession || track == null || line == null) return;
        int n = rivals.Count;
        for (int k = 0; k < n; k++)
        {
            int i = (track.N - 30 + (k + 1) * track.N / (n + 1)) % track.N;
            rivals[k].Place(i, line.off[i], System.Math.Min(line.speed[i], 22));
        }
        rivalReact.Clear(); for (int k = 0; k < n; k++) rivalReact.Add(0); // rolling already: no start reaction
    }

    // ChooseGridSlot: the slot qualifying gave the player (-1 = the menu's choice)
    int GridSlotOverride(int cars) => raceLaps > 0 && raceField != null && gridSlotOverride >= 0 ? Mathf.Min(gridSlotOverride, cars - 1) : -1;

    // Update, every frame: the qualifying clock (game time) and the flag
    void QualiUpdate()
    {
        if (quali == null) return;
        double d = raceTime - qPrevTime; qPrevTime = raceTime;
        if (Paused || d <= 0) return;
        quali.SetPlayerBest(bestLap);
        quali.Tick(d);
        if (!quali.flag && quali.clock >= quali.length)
        {
            quali.flag = true; qFlagLap = lapStart.HasValue ? lapNo : -1;
            Flash(qFlagLap >= 0 ? "Chequered flag: finish your lap" : "Chequered flag");
        }
        if (quali.flag && (qFlagLap < 0 || lapNo != qFlagLap || !lapStart.HasValue)) EndQuali();
    }

    void EndQuali()
    {
        if (quali == null) return;
        quali.SetPlayerBest(bestLap);
        quali.Finish();
        ui.season.ShowQuali(quali);
        ui.page = MenuUI.Page.QualiResult;
        quali = null;
    }

    // "START RACE" on the qualifying results: the grid in classified order
    void RaceFromQuali()
    {
        var q = ui.season.lastQuali; if (q == null) return;
        var order = q.Classified();
        raceField = new List<DriverProfile>(); gridSlotOverride = 0;
        for (int i = 0; i < order.Count; i++) { if (order[i].Player) gridSlotOverride = i; else raceField.Add(order[i].who); }
        pendingGrid = true;
        ui.BeginLoading(false);
    }

    // OnGUI: menu choices of the weekend flow
    void SessionChoice(MenuUI.Choice c)
    {
        if (c == MenuUI.Choice.StartRace) RaceFromQuali();
    }

    // FinishRace, after the results are shown: places gained, fastest lap, and the championship
    void RaceFinished(MenuUI.Row[] rows)
    {
        int n = rows.Length; if (n == 0) return;
        var order = new List<int>(); for (int i = 0; i < n; i++) order.Add(i);
        order.Sort((a, b) => rows[b].progress.CompareTo(rows[a].progress));
        int finish = order.IndexOf(0) + 1, start = playerSlot + 1;
        int fl = -1; for (int i = 0; i < n; i++) if (!double.IsNaN(rows[i].best) && rows[i].best > 0 && (fl < 0 || rows[i].best < rows[fl].best)) fl = i;
        string note = "";
        if (n > 1)
        {
            int g = start - finish;
            note = $"Grid P{start} → P{finish} ({(g > 0 ? "+" + g + " places" : g < 0 ? g + " places" : "no change")})";
            if (fl >= 0) note += $"  ·  Fastest lap {rows[fl].name} {Qualifying.Fmt(rows[fl].best)}";
        }
        ui.seasonNext = false;
        if (InSeasonRound && mapIdx == Season.CircuitIndex(Season.Data.round))
        {
            // roster ids: row 0 = the player, row i + 1 = rivals[i]
            int Id(int row) => row == 0 ? 0 : Season.IdOf(rivals[row - 1].who);
            var ids = new int[n]; for (int p = 0; p < n; p++) ids[p] = Id(order[p]);
            var grid = new int[n];
            for (int s = 0, k = 0; s < n; s++) grid[s] = s == playerSlot ? 0 : (k < rivals.Count ? Season.IdOf(rivals[k++].who) : -1);
            var o = Season.Record(ids, grid, fl >= 0 ? Id(fl) : -1);
            string ch = o.rankBefore == 0 ? $"Championship P{o.rankAfter}" : $"Championship P{o.rankAfter} ({(o.rankBefore > o.rankAfter ? "up " + (o.rankBefore - o.rankAfter) : o.rankBefore < o.rankAfter ? "down " + (o.rankAfter - o.rankBefore) : "same")})";
            note += $"  ·  <b>+{o.pointsGained} pts</b>  ·  {ch}";
            ui.seasonNext = true;
        }
        ui.resultNote = note;
    }

    // HUD during qualifying: session clock, END, and live timing
    void DrawQualiHud()
    {
        if (quali == null) return;
        float w = S(200), x = Screen.width / 2 - w / 2, y = S(8), h = S(30);
        Panel(new Rect(x, y, w, h));
        center.fontSize = (int)S(14);
        GUI.Label(new Rect(x, y, w, h), quali.flag ? "<b>QUALIFYING</b>  ·  FLAG" : $"<b>QUALIFYING</b>  ·  {Qualifying.Clock(quali.Left)}", center);
        if (GUI.Button(new Rect(x + w + S(4), y, S(56), h), "END", btn)) { EndQuali(); return; }

        var list = quali.Classified(); double best = quali.Fastest;
        float lx = S(8), ly = hudInfo ? S(190) : S(40), lw = S(176), rh = S(13);
        int rows = Mathf.Min(Mathf.Max((int)((Screen.height - S(210) - ly) / rh), 3), list.Count);
        int me = list.FindIndex(e => e.Player);
        bool meShown = me < rows;
        int show = meShown ? rows : rows - 1;
        Panel(new Rect(lx, ly, lw, rh * (show + (meShown ? 0 : 1)) + S(6)));
        int old = small.fontSize; small.fontSize = (int)S(10);
        for (int r = 0; r < show + (meShown ? 0 : 1); r++)
        {
            int i = r < show ? r : me;
            var e = list[i]; float yy = ly + S(3) + r * rh;
            bool fresh = quali.clock - e.shownAt < 4;
            if (e.Player) Fill(new Rect(lx, yy, lw, rh), new Color(MenuUI.Accent.r, MenuUI.Accent.g, MenuUI.Accent.b, 0.35f));
            Fill(new Rect(lx + S(22), yy + S(2), S(3), rh - S(4)), e.colour);
            string time = double.IsNaN(e.best) ? "no time" : i == 0 ? Qualifying.Fmt(e.best) : $"+{e.best - best:0.000}";
            string col = fresh ? (e.lastDeleted ? "#ff6060" : !double.IsNaN(e.best) && e.best <= best + 1e-9 && e.lastPb ? "#c070ff" : e.lastPb ? "#60ff80" : "#ffe060") : "#ffffff";
            if (fresh && e.lastDeleted) time = "deleted";
            GUI.Label(new Rect(lx + S(4), yy - S(1), S(20), rh + S(2)), (i + 1).ToString(), small);
            GUI.Label(new Rect(lx + S(28), yy - S(1), S(90), rh + S(2)), e.name, small);
            GUI.Label(new Rect(lx + S(112), yy - S(1), S(64), rh + S(2)), $"<color={col}>{time}</color>", small);
        }
        small.fontSize = old;
    }
}
