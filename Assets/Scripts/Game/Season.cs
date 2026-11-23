// Season.cs — the career: a saved championship season with a team, tiers, round progress and the story's state.
//  * Calendar: the game's 10 real circuits in the real 2026 calendar order (Suzuka, Monaco, Barcelona, Red Bull Ring,
//    Silverstone, Spa, Hungaroring, Zandvoort, Monza, Austin). Looked up by circuit id in Circuits.All. No other circuit
//    is raced in a career.
//  * Tiers: Rookie -> Challenger -> Premier (the Apex World Series car classes, CarConfig.ClassNames). A career starts
//    in the Rookie tier; the top three of a junior tier's championship move up at the season's end. Premier is the
//    world title (the story's promise, Story.cs).
//  * Roster: id 0 = the player, id k = DriverProfile.Grid[k - 1] (invented drivers and teams). The player signs for a
//    midfield team; if it already has two drivers, its slower one moves to the team with a free seat. Roster id 1 (the
//    grid's fastest, Argent) is the story's nemesis under another name: Nico Raske in the junior tiers, his father
//    Viktor Raske (seven-time champion) in Premier (Profile / Name; same pace, same AI).
//  * Points: 25-18-15-12-10-8-6-4-2-1 for the top ten of each round's race finish.
//  * Round progress (stage), saved after every session so nothing can be retried by leaving or killing the app:
//    0 nothing run yet; 1 qualifying started (left or killed = no time: start from the back); 2 the grid is set
//    (qualiGrid); 3 the race started (left / killed: a retirement on Hard+, a new start on Easy / Medium).
//  * Goals: two per round (frozen when the round is shown) and three per season (Career.cs). +50 XP per round goal met.
//  * Saved as JSON (JsonUtility) in Application.persistentDataPath/season.json and mirrored in PlayerPrefs "seasonJson"
//    (WebGL keeps PlayerPrefs reliably). Version 1 saves (the old championship) are taken over as a Premier season.
using System;
using System.Collections.Generic;
using System.IO;
using FormulaSim;
using UnityEngine;

[Serializable]
public class SeasonRound
{
    public int circuit;      // index in Circuits.All
    public int[] order;      // roster ids in finishing order
    public int[] grid;       // roster ids in grid order
    public int fastest = -1; // roster id with the fastest lap
    public int quali;        // the player's qualifying position (1-based; 0 = no time / skipped)
    public int diff = -1;    // the difficulty it was raced on (Difficulty)
    public int goals, goalCount; // round goals met (bit mask) of goalCount
    public bool dnf;         // the player retired (left the race on Hard+)
}

[Serializable]
public class CareerGoal
{
    public int kind, n, who;  // Career.GoalFinish / GoalQuali / GoalAhead: n = position, who = roster id
    public string text;
    public bool done;
}

[Serializable]
public class CareerSeason
{
    public int seasonNo, tier, team, rank, points;
    public bool champion, promoted;
}

[Serializable]
public class SeasonData
{
    public int version = 2;
    public int team;              // the player's team (index in Season.Teams)
    public int displaced = -1;    // roster id of the driver who moved to the free seat (-1 = none)
    public int round;             // next round, 0-based; Season.Rounds = season over
    public int[] points;          // per roster id
    public int[] prevRank;        // championship position per roster id before the last round (1-based; 0 = none yet)
    public List<SeasonRound> results = new List<SeasonRound>();
    // ---- career (version 2)
    public int tier;              // 0 Rookie, 1 Challenger, 2 Premier
    public int seasonNo = 1;
    public int rivalTeam = -1;    // the team to beat in the teams' table
    public int stage;             // Season.StageNone / StageQuali / StageGrid / StageRace
    public int[] qualiGrid;       // roster ids in grid order once set (qualifying, or from the back)
    public int qualiPos;          // the player's qualifying position (0 = no time)
    public bool raceRestarts = true; // the race running may be started again (Difficulty at its start)
    public List<CareerGoal> goals = new List<CareerGoal>(); // this round's goals
    public int goalsFor = -1;     // the round those goals are for
    public bool prologue;         // the prologue and welcome have been shown
    public int storyPre = -1;     // the last round whose pre-round story was shown
    public int storyPost;         // results whose reaction was shown
    public bool endShown;         // the season-end story was shown
    public int bonusXp;           // XP from goals this season
    public List<CareerSeason> history = new List<CareerSeason>();
}

public static class Season
{
    public const string Year = "2026";
    public static readonly string[] CalendarIds = { "suzuka", "monaco", "catalunya", "spielberg", "silverstone", "spa", "budapest", "zandvoort", "monza", "austin" };
    public static readonly string[] GpNames = { "Japan", "Monaco", "Spain", "Austria", "Britain", "Belgium", "Hungary", "Netherlands", "Italy", "United States" };
    public static readonly string[] GpCodes = { "JPN", "MON", "ESP", "AUT", "GBR", "BEL", "HUN", "NED", "ITA", "USA" };
    public static readonly int[] Points = { 25, 18, 15, 12, 10, 8, 6, 4, 2, 1 };
    public static int Rounds => CalendarIds.Length;
    public const string PlayerName = "You";
    public const int StageNone = 0, StageQuali = 1, StageGrid = 2, StageRace = 3;

    // tiers: the Apex World Series car classes (CarConfig.ClassNames: 0 Premier, 1 Challenger, 2 Rookie)
    public static readonly string[] TierNames = { "Rookie", "Challenger", "Premier" };
    public const int TierPremier = 2, PromoteTop = 3;
    public static int Tier => Data != null ? Mathf.Clamp(Data.tier, 0, TierPremier) : TierPremier;
    public static int TierClass => Mathf.Clamp(TierPremier - Tier, 0, CarConfig.ClassNames.Length - 1);

    public static SeasonData Data;
    public static bool Active => Data != null;
    public static bool Over => Data != null && Data.round >= Rounds;

    public static int CircuitIndex(int round)
    {
        round = Mathf.Clamp(round, 0, Rounds - 1);
        for (int i = 0; i < Circuits.All.Length; i++) if (Circuits.All[i].id == CalendarIds[round]) return i;
        return 0;
    }
    public static string RoundName(int round) => $"R{round + 1}  {GpNames[Mathf.Clamp(round, 0, Rounds - 1)]}";

    // ------------------------------------------------------------ the nemesis (roster id 1)
    public const int RivalId = 1;
    public const string NicoName = "N. Raske", ViktorName = "V. Raske", RivalCode = "RAS";
    public static bool Viktor => Tier >= TierPremier;
    public static string RivalName => Viktor ? ViktorName : NicoName;
    public static string RivalFull => Viktor ? "Viktor Raske" : "Nico Raske";
    public static string RivalShort => Viktor ? "Raske" : "Nico Raske";
    // the HUD's three-letter code for a name the grid does not have (RaceHud.Code)
    public static string CodeOf(DriverProfile p) => p != null && (p.name == NicoName || p.name == ViktorName) ? RivalCode : "???";

    // ------------------------------------------------------------ teams and roster
    static List<string> teams;
    public static List<string> Teams
    {
        get
        {
            if (teams == null) { teams = new List<string>(); foreach (var g in DriverProfile.Grid) if (!teams.Contains(g.team)) teams.Add(g.team); }
            return teams;
        }
    }
    public static int RosterCount => DriverProfile.Grid.Length + 1;
    public static int TeamIndexOf(string team) => Mathf.Max(0, Teams.IndexOf(team));
    public static Color TeamColour(int t)
    {
        // the same colour the car wears (Livery), so the mini-map, tower and results match the cars
        string name = t >= 0 && t < Teams.Count ? Teams[t] : null;
        for (int i = 0; i < Livery.Teams.Length; i++) if (Livery.Teams[i].name == name) return Livery.TeamColour(i);
        foreach (var g in DriverProfile.Grid) if (g.team == name) return new Color(g.r, g.g, g.b);
        return Color.white;
    }
    // the team with fewer than two drivers in the built-in grid (-1 = none)
    public static int OpenTeam()
    {
        for (int t = 0; t < Teams.Count; t++) { int n = 0; foreach (var g in DriverProfile.Grid) if (g.team == Teams[t]) n++; if (n < 2) return t; }
        return -1;
    }
    // the built-in drivers of team t (roster ids)
    public static List<int> GridDriversOf(int t)
    {
        var l = new List<int>();
        for (int i = 0; i < DriverProfile.Grid.Length; i++) if (DriverProfile.Grid[i].team == Teams[t]) l.Add(i + 1);
        return l;
    }
    // who would be displaced if the player joined team t (-1 = nobody)
    public static int DisplacedFor(int t)
    {
        var l = GridDriversOf(t);
        if (l.Count < 2 || OpenTeam() < 0 || OpenTeam() == t) return -1;
        int worst = l[0]; foreach (var id in l) if (DriverProfile.Grid[id - 1].pace < DriverProfile.Grid[worst - 1].pace) worst = id;
        return worst;
    }
    public static int TeammateFor(int t)
    {
        int d = DisplacedFor(t);
        foreach (var id in GridDriversOf(t)) if (id != d) return id;
        return -1;
    }
    // a midfield team: the teams a rookie gets offers from (best driver's pace 0.6-0.9; not the nemesis' team)
    public static bool Midfield(int t)
    {
        double best = 0; bool rival = false;
        foreach (var id in GridDriversOf(t)) { best = Math.Max(best, DriverProfile.Grid[id - 1].pace); if (id == RivalId) rival = true; }
        return !rival && best >= 0.6 && best <= 0.9;
    }

    public static int TeamOf(int id)
    {
        if (Data == null) return id > 0 ? TeamIndexOf(DriverProfile.Grid[id - 1].team) : 0;
        if (id == 0) return Data.team;
        if (id == Data.displaced) return Mathf.Max(0, OpenTeam());
        return TeamIndexOf(DriverProfile.Grid[id - 1].team);
    }
    public static string Name(int id) => id == 0 ? PlayerName : id == RivalId && Data != null ? RivalName : DriverProfile.Grid[id - 1].name;
    public static Color Colour(int id) => id == 0 ? (Data != null ? TeamColour(Data.team) : Color.white) : TeamColour(TeamOf(id));
    public static int IdOf(DriverProfile p)
    {
        if (p == null) return 0;
        if (p.name == NicoName || p.name == ViktorName) return RivalId;
        for (int i = 0; i < DriverProfile.Grid.Length; i++) if (DriverProfile.Grid[i].name == p.name) return i + 1;
        return -1;
    }

    // the rival's profile as he races this season (the displaced driver in his new team's colours; the nemesis' name)
    public static DriverProfile Profile(int id)
    {
        var g = DriverProfile.Grid[id - 1];
        if (Data == null) return g;
        if (id == RivalId) return new DriverProfile { name = RivalName, team = g.team, pace = g.pace, consistency = g.consistency, aggression = g.aggression, r = g.r, g = g.g, b = g.b };
        if (id != Data.displaced) return g;
        int t = Mathf.Max(0, OpenTeam()); var c = TeamColour(t);
        return new DriverProfile { name = g.name, team = Teams[t], pace = g.pace, consistency = g.consistency, aggression = g.aggression, r = c.r, g = c.g, b = c.b };
    }
    // every rival of the season (roster order)
    public static List<DriverProfile> Field()
    {
        var l = new List<DriverProfile>();
        for (int id = 1; id < RosterCount; id++) l.Add(Profile(id));
        return l;
    }
    public static double Pace(int id) => id <= 0 ? 0.8 : DriverProfile.Grid[id - 1].pace;   // the player counts as a good midfielder

    // ------------------------------------------------------------ standings
    static int Count(int id, int pos)
    {
        int n = 0;
        foreach (var r in Data.results) if (r.order != null && pos < r.order.Length && r.order[pos] == id) n++;
        return n;
    }
    // roster ids, champion first: points, then wins, seconds, ... (the real tie-break)
    public static int[] Standings()
    {
        var ids = new List<int>(); for (int i = 0; i < RosterCount; i++) ids.Add(i);
        if (Data == null) return ids.ToArray();
        ids.Sort((a, b) =>
        {
            int c = Data.points[b].CompareTo(Data.points[a]); if (c != 0) return c;
            for (int p = 0; p < RosterCount; p++) { c = Count(b, p).CompareTo(Count(a, p)); if (c != 0) return c; }
            return a.CompareTo(b);
        });
        return ids.ToArray();
    }
    public static int Rank(int id) { var s = Standings(); return Array.IndexOf(s, id) + 1; }
    public static int TeamPoints(int t)
    {
        int p = 0; if (Data == null) return 0;
        for (int id = 0; id < RosterCount; id++) if (TeamOf(id) == t) p += Data.points[id];
        return p;
    }
    public static int[] TeamStandings()
    {
        var ts = new List<int>(); for (int t = 0; t < Teams.Count; t++) ts.Add(t);
        ts.Sort((a, b) => { int c = TeamPoints(b).CompareTo(TeamPoints(a)); return c != 0 ? c : a.CompareTo(b); });
        return ts.ToArray();
    }
    public static string Winner(int round)
    {
        if (Data == null || round < 0 || round >= Data.results.Count) return null;
        var r = Data.results[round];
        return r.order != null && r.order.Length > 0 ? Name(r.order[0]) : null;
    }
    // the player's finishing position in a recorded round (1-based; 0 = not there)
    public static int FinishOf(int round, int id = 0)
    {
        if (Data == null || round < 0 || round >= Data.results.Count || Data.results[round].order == null) return 0;
        return Array.IndexOf(Data.results[round].order, id) + 1;
    }

    // ------------------------------------------------------------ round progress (saved after each session)
    public static void QualiStarted()
    {
        if (Data == null || Over) return;
        Data.stage = StageQuali; Data.qualiGrid = null; Data.qualiPos = 0; Save();
    }
    // the grid from qualifying (roster ids in grid order); pos = the player's position, 0 = no time
    public static void SetGrid(int[] ids, int pos)
    {
        if (Data == null || Over) return;
        Data.qualiGrid = ids; Data.qualiPos = pos; Data.stage = StageGrid; Save();
    }
    // no qualifying (skipped, left, killed): the field by pace and the player last
    public static void GridFromBack()
    {
        if (Data == null) return;
        var ids = new List<int>(); for (int id = 1; id < RosterCount; id++) ids.Add(id);
        ids.Sort((a, b) => { int c = Pace(b).CompareTo(Pace(a)); return c != 0 ? c : a.CompareTo(b); });
        ids.Add(0);
        SetGrid(ids.ToArray(), 0);
    }
    public static void RaceStarted(bool restarts)
    {
        if (Data == null || Over) return;
        if (Data.qualiGrid == null || Data.qualiGrid.Length < 2) GridFromBack();
        Data.stage = StageRace; Data.raceRestarts = restarts; Save();
    }
    // the hub opens: a session left behind is settled. Returns true when the player was retired from a race.
    public static bool ResolvePending()
    {
        if (Data == null || Over) return false;
        if (Data.stage == StageQuali) { GridFromBack(); return false; }       // qualifying left: no time
        if (Data.stage == StageRace)
        {
            if (Data.raceRestarts) { Data.stage = StageGrid; Save(); return false; } // Easy / Medium: race again
            RecordDnf(); return true;
        }
        return false;
    }
    // a race left on Hard+: the field finishes in grid order, the player retired (no points)
    static void RecordDnf()
    {
        var g = Data.qualiGrid; if (g == null || g.Length < 2) { GridFromBack(); g = Data.qualiGrid; }
        var order = new List<int>(); foreach (int id in g) if (id != 0) order.Add(id);
        order.Add(0);
        Record(order.ToArray(), (int[])g.Clone(), -1, true);
    }

    // ------------------------------------------------------------ progress
    public struct Outcome { public int pointsGained, rankBefore, rankAfter, goalsMet, goalCount, bonusXp; }
    public const int XpPerGoal = 50;
    public static Outcome LastOutcome;

    // a finished race of the current round: order and grid are roster ids. Returns the player's gains.
    public static Outcome Record(int[] order, int[] grid, int fastest, bool dnf = false)
    {
        var o = new Outcome();
        if (Data == null || Over) return o;
        var before = Standings();
        if (Data.prevRank == null || Data.prevRank.Length != RosterCount) Data.prevRank = new int[RosterCount];
        for (int i = 0; i < before.Length; i++) Data.prevRank[before[i]] = i + 1;
        o.rankBefore = Data.results.Count == 0 ? 0 : Data.prevRank[0];
        for (int p = 0; p < order.Length && p < Points.Length; p++)
        {
            if (order[p] < 0 || order[p] >= RosterCount || (dnf && order[p] == 0)) continue;
            Data.points[order[p]] += Points[p];
            if (order[p] == 0) o.pointsGained = Points[p];
        }
        var r = new SeasonRound { circuit = CircuitIndex(Data.round), order = order, grid = grid, fastest = fastest, quali = Data.qualiPos, diff = Difficulty.Current, dnf = dnf };
        // the round's goals (frozen on the hub; made now if the hub never showed them)
        if (Data.goalsFor != Data.round) Career.FreezeGoals();
        int mask = 0, met = 0;
        for (int i = 0; i < Data.goals.Count; i++)
        {
            var gl = Data.goals[i];
            gl.done = !dnf && Career.Met(gl, order, Data.qualiPos);
            if (gl.done) { mask |= 1 << i; met++; }
        }
        r.goals = mask; r.goalCount = Data.goals.Count;
        o.goalsMet = met; o.goalCount = Data.goals.Count;
        o.bonusXp = Mathf.RoundToInt(met * XpPerGoal * MenuProfile.XpMult);
        if (o.bonusXp > 0) { MenuProfile.AddXp(o.bonusXp); Data.bonusXp += o.bonusXp; }
        Data.results.Add(r);
        Data.round++;
        Data.stage = StageNone; Data.qualiGrid = null; Data.qualiPos = 0;
        o.rankAfter = Rank(0);
        LastOutcome = o;
        Save();
        return o;
    }

    // a new career: Rookie tier, season 1
    public static void New(int team)
    {
        Data = new SeasonData { team = Mathf.Clamp(team, 0, Teams.Count - 1), round = 0, points = new int[RosterCount], prevRank = new int[RosterCount], tier = 0, seasonNo = 1 };
        Data.displaced = DisplacedFor(Data.team);
        Data.rivalTeam = Career.PickRivalTeam();
        Data.prologue = true;
        Save();
    }

    // the season is over: into the history, up a tier for the top three of a junior tier, a fresh season (same team)
    public static void NextSeason()
    {
        if (Data == null || !Over) return;
        int rank = Rank(0);
        bool promoted = Data.tier < TierPremier && rank <= PromoteTop;
        Data.history.Add(new CareerSeason { seasonNo = Data.seasonNo, tier = Data.tier, team = Data.team, rank = rank, points = Data.points[0], champion = rank == 1, promoted = promoted });
        if (promoted) Data.tier++;
        Data.seasonNo++;
        Data.round = 0; Data.points = new int[RosterCount]; Data.prevRank = new int[RosterCount]; Data.results.Clear();
        Data.stage = StageNone; Data.qualiGrid = null; Data.qualiPos = 0;
        Data.goals.Clear(); Data.goalsFor = -1; Data.storyPre = -1; Data.storyPost = 0; Data.endShown = false; Data.bonusXp = 0;
        Data.rivalTeam = Career.PickRivalTeam();
        Save();
    }
    public static CareerSeason LastSeason => Data != null && Data.history.Count > 0 ? Data.history[Data.history.Count - 1] : null;
    public static int Titles { get { int n = 0; if (Data != null) foreach (var h in Data.history) if (h.champion && h.tier == TierPremier) n++; return n; } }

    // ------------------------------------------------------------ save
    const string PrefKey = "seasonJson";
    static string FilePath => Path.Combine(Application.persistentDataPath, "season.json");
    public static bool HasSave
    {
        get
        {
            try { if (File.Exists(FilePath)) return true; } catch (Exception) { }
            return PlayerPrefs.HasKey(PrefKey);
        }
    }
    public static void Save()
    {
        if (Data == null) return;
        string json = JsonUtility.ToJson(Data);
        try { File.WriteAllText(FilePath, json); }
        catch (Exception e) { Debug.LogWarning("Season save failed: " + e.Message); }
        PlayerPrefs.SetString(PrefKey, json); PlayerPrefs.Save();
    }
    public static bool Load()
    {
        try
        {
            string json = null;
            try { if (File.Exists(FilePath)) json = File.ReadAllText(FilePath); } catch (Exception) { }
            if (string.IsNullOrEmpty(json)) json = PlayerPrefs.GetString(PrefKey, null);
            if (string.IsNullOrEmpty(json)) return false;
            var d = JsonUtility.FromJson<SeasonData>(json);
            if (d == null || d.points == null || d.points.Length != RosterCount) return false; // a save from another roster
            if (d.results == null) d.results = new List<SeasonRound>();
            if (d.goals == null) d.goals = new List<CareerGoal>();
            if (d.history == null) d.history = new List<CareerSeason>();
            d.team = Mathf.Clamp(d.team, 0, Teams.Count - 1);
            if (d.version < 2)
            {
                // the old championship: carried on as a Premier season of a career, the story from its start
                d.version = 2; d.tier = TierPremier; d.seasonNo = 1; d.stage = StageNone; d.prologue = false;
                d.storyPre = d.round - 1; d.storyPost = d.results.Count; d.goalsFor = -1;
            }
            d.tier = Mathf.Clamp(d.tier, 0, TierPremier);
            Data = d;
            if (Data.rivalTeam < 0 || Data.rivalTeam >= Teams.Count) Data.rivalTeam = Career.PickRivalTeam();
            return true;
        }
        catch (Exception e) { Debug.LogWarning("Season load failed: " + e.Message); return false; }
    }
}
