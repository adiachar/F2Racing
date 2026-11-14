// Season.cs — championship mode: the calendar, the roster, points and standings, saved progress.
//  * Calendar: the game's 10 real circuits in the real 2026 calendar order (Suzuka, Monaco, Barcelona, Red Bull Ring,
//    Silverstone, Spa, Hungaroring, Zandvoort, Monza, Austin). Looked up by circuit id in Circuits.All.
//  * Roster: id 0 = the player, id k = DriverProfile.Grid[k - 1] (invented drivers and teams). The player picks a team;
//    if it already has two drivers, its slower one moves to the team with a free seat, so every team runs two cars.
//  * Points: 25-18-15-12-10-8-6-4-2-1 for the top ten of each round's real race finish.
//  * Saved as JSON in Application.persistentDataPath/season.json (JsonUtility).
using System;
using System.Collections.Generic;
using System.IO;
using FormulaSim;
using UnityEngine;

[Serializable]
public class SeasonRound
{
    public int circuit;     // index in Circuits.All
    public int[] order;     // roster ids in finishing order
    public int[] grid;      // roster ids in grid order
    public int fastest = -1; // roster id with the fastest lap
}

[Serializable]
public class SeasonData
{
    public int version = 1;
    public int team;              // the player's team (index in Season.Teams)
    public int displaced = -1;    // roster id of the driver who moved to the free seat (-1 = none)
    public int round;             // next round, 0-based; Season.Rounds = season over
    public int[] points;          // per roster id
    public int[] prevRank;        // championship position per roster id before the last round (1-based; 0 = none yet)
    public List<SeasonRound> results = new List<SeasonRound>();
}

public static class Season
{
    public const string Year = "2026";
    public static readonly string[] CalendarIds = { "suzuka", "monaco", "catalunya", "spielberg", "silverstone", "spa", "budapest", "zandvoort", "monza", "austin" };
    public static readonly string[] GpNames = { "Japan", "Monaco", "Barcelona-Catalunya", "Austria", "Britain", "Belgium", "Hungary", "Netherlands", "Italy", "United States" };
    public static readonly int[] Points = { 25, 18, 15, 12, 10, 8, 6, 4, 2, 1 };
    public static int Rounds => CalendarIds.Length;
    public const string PlayerName = "You";

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
        foreach (var g in DriverProfile.Grid) if (g.team == Teams[t]) return new Color(g.r, g.g, g.b);
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

    public static int TeamOf(int id)
    {
        if (Data == null) return id > 0 ? TeamIndexOf(DriverProfile.Grid[id - 1].team) : 0;
        if (id == 0) return Data.team;
        if (id == Data.displaced) return Mathf.Max(0, OpenTeam());
        return TeamIndexOf(DriverProfile.Grid[id - 1].team);
    }
    public static string Name(int id) => id == 0 ? PlayerName : DriverProfile.Grid[id - 1].name;
    public static Color Colour(int id) => id == 0 ? (Data != null ? TeamColour(Data.team) : Color.white) : TeamColour(TeamOf(id));
    public static int IdOf(DriverProfile p)
    {
        if (p == null) return 0;
        for (int i = 0; i < DriverProfile.Grid.Length; i++) if (DriverProfile.Grid[i].name == p.name) return i + 1;
        return -1;
    }

    // the rival's profile as he races this season (the displaced driver in his new team's colours)
    public static DriverProfile Profile(int id)
    {
        var g = DriverProfile.Grid[id - 1];
        if (Data == null || id != Data.displaced) return g;
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

    // ------------------------------------------------------------ progress
    public struct Outcome { public int pointsGained, rankBefore, rankAfter; }

    // a finished race of the current round: order and grid are roster ids. Returns the player's gains.
    public static Outcome Record(int[] order, int[] grid, int fastest)
    {
        var o = new Outcome();
        if (Data == null || Over) return o;
        var before = Standings();
        if (Data.prevRank == null || Data.prevRank.Length != RosterCount) Data.prevRank = new int[RosterCount];
        for (int i = 0; i < before.Length; i++) Data.prevRank[before[i]] = i + 1;
        o.rankBefore = Data.results.Count == 0 ? 0 : Data.prevRank[0];
        for (int p = 0; p < order.Length && p < Points.Length; p++)
        {
            if (order[p] < 0 || order[p] >= RosterCount) continue;
            Data.points[order[p]] += Points[p];
            if (order[p] == 0) o.pointsGained = Points[p];
        }
        Data.results.Add(new SeasonRound { circuit = CircuitIndex(Data.round), order = order, grid = grid, fastest = fastest });
        Data.round++;
        o.rankAfter = Rank(0);
        Save();
        return o;
    }

    public static void New(int team)
    {
        Data = new SeasonData { team = Mathf.Clamp(team, 0, Teams.Count - 1), round = 0, points = new int[RosterCount], prevRank = new int[RosterCount] };
        Data.displaced = DisplacedFor(Data.team);
        Save();
    }

    static string FilePath => Path.Combine(Application.persistentDataPath, "season.json");
    public static bool HasSave => File.Exists(FilePath);
    public static void Save()
    {
        if (Data == null) return;
        try { File.WriteAllText(FilePath, JsonUtility.ToJson(Data)); }
        catch (Exception e) { Debug.LogWarning("Season save failed: " + e.Message); }
    }
    public static bool Load()
    {
        try
        {
            if (!HasSave) return false;
            var d = JsonUtility.FromJson<SeasonData>(File.ReadAllText(FilePath));
            if (d == null || d.points == null || d.points.Length != RosterCount) return false; // a save from another roster
            if (d.results == null) d.results = new List<SeasonRound>();
            d.team = Mathf.Clamp(d.team, 0, Teams.Count - 1);
            Data = d; return true;
        }
        catch (Exception e) { Debug.LogWarning("Season load failed: " + e.Message); return false; }
    }
}
