// Career.cs — the career's goals (Season.cs keeps the save; Story.cs tells the story; SeasonUI.cs shows both).
//  * Round goals: two per round, scripted per calendar round (finish in the top N, qualify in the top N, finish ahead of
//    a named driver: the nemesis, your teammate or the rival team's lead driver). Positional targets adapt to the
//    player's form (the average finish of the last three rounds, never easier than the script). They are frozen when
//    the round is first shown (Season.Data.goals), judged when the race is recorded (+50 XP each, x the difficulty).
//  * Season goals: beat the rival team in the teams' table (the nearest stronger team, not the nemesis' team); finish in
//    the top three (junior tiers: promotion) or win the world title (Premier); beat Raske in a race.
// Runs on the menu only (strings are made when goals are frozen or the hub refreshes), never in a race.
using System.Collections.Generic;
using UnityEngine;

public static class Career
{
    public const int GoalFinish = 0, GoalQuali = 1, GoalAhead = 2;
    // season goal states
    public const int Open = 0, OnCourse = 1, Done = 2, Failed = 3;

    // ------------------------------------------------------------ people
    public static int TeammateId()
    {
        var d = Season.Data; if (d == null) return -1;
        for (int id = 1; id < Season.RosterCount; id++) if (Season.TeamOf(id) == d.team) return id;
        return -1;
    }
    // the fastest driver of team t (roster id, -1 = none)
    public static int LeadOf(int t)
    {
        int best = -1;
        for (int id = 1; id < Season.RosterCount; id++) if (Season.TeamOf(id) == t && (best < 0 || Season.Pace(id) > Season.Pace(best))) best = id;
        return best;
    }
    public static string TeamName(int t) => t >= 0 && t < Season.Teams.Count ? Season.Teams[t] : "";

    // the team to beat: the nearest stronger team by driver pace (never the nemesis' team), else the nearest weaker one
    public static int PickRivalTeam()
    {
        var d = Season.Data; if (d == null) return -1;
        int n = Season.Teams.Count; var str = new double[n];
        for (int id = 0; id < Season.RosterCount; id++) str[Season.TeamOf(id)] += Season.Pace(id);
        int nem = Season.TeamOf(Season.RivalId), best = -1; double bd = double.MaxValue;
        for (int t = 0; t < n; t++) { if (t == d.team || t == nem) continue; double dd = str[t] - str[d.team]; if (dd >= 0 && dd < bd) { bd = dd; best = t; } }
        if (best < 0) for (int t = 0; t < n; t++) { if (t == d.team || t == nem) continue; double dd = str[d.team] - str[t]; if (dd >= 0 && dd < bd) { bd = dd; best = t; } }
        return best;
    }

    // ------------------------------------------------------------ round goals
    // the player's average finish over the last three rounds (99 = none yet)
    static float RecentAverage()
    {
        var d = Season.Data; int n = 0; float sum = 0;
        for (int r = d.results.Count - 1; r >= 0 && n < 3; r--, n++)
        {
            int p = Season.FinishOf(r); if (d.results[r].dnf || p <= 0) p = d.results[r].order != null ? d.results[r].order.Length : 22;
            sum += p;
        }
        return n > 0 ? sum / n : 99f;
    }
    static int Adapt(int baseN)
    {
        float avg = RecentAverage();
        return avg >= 98f ? baseN : Mathf.Clamp(Mathf.Min(baseN, Mathf.CeilToInt(avg)), 1, baseN);
    }
    // within reach of the title before the last round
    public static bool TitleFight()
    {
        var d = Season.Data; if (d == null) return false;
        var st = Season.Standings();
        return d.points[st[0]] - d.points[0] <= 25;
    }

    static CareerGoal Finish(int n) => new CareerGoal { kind = GoalFinish, n = n };
    static CareerGoal Quali(int n) => new CareerGoal { kind = GoalQuali, n = n };
    static CareerGoal Ahead(int who) => new CareerGoal { kind = GoalAhead, who = who };

    // this round's two goals, made once and saved
    public static void FreezeGoals()
    {
        var d = Season.Data; if (d == null || Season.Over) return;
        int mate = TeammateId(), lead = LeadOf(d.rivalTeam), nem = Season.RivalId;
        if (lead <= 0 || lead == mate) lead = nem;
        CareerGoal a, b;
        switch (d.round)
        {
            case 0: a = Finish(Adapt(10)); b = Ahead(mate); break;
            case 1: a = Quali(Adapt(10)); b = Finish(Adapt(10)); break;
            case 2: a = Ahead(mate); b = Finish(Adapt(8)); break;
            case 3: a = Ahead(lead); b = Quali(Adapt(10)); break;
            case 4: a = Finish(Adapt(8)); b = Ahead(mate); break;
            case 5: a = Ahead(nem); b = Finish(Adapt(8)); break;
            case 6: a = Quali(Adapt(8)); b = Ahead(lead); break;
            case 7: a = Finish(Adapt(6)); b = Ahead(mate); break;
            case 8: a = Ahead(nem); b = Quali(Adapt(6)); break;
            default: a = TitleFight() ? Finish(1) : Finish(Adapt(5)); b = Ahead(nem); break;
        }
        if (b.kind == GoalAhead && a.kind == GoalAhead && a.who == b.who) b = Finish(Adapt(8));
        if (a.kind == GoalAhead && a.who <= 0) a = Finish(Adapt(10));
        if (b.kind == GoalAhead && b.who <= 0) b = Finish(Adapt(8));
        a.text = Text(a, mate); b.text = Text(b, mate);
        d.goals.Clear(); d.goals.Add(a); d.goals.Add(b);
        d.goalsFor = d.round;
        Season.Save();
    }

    static string Text(CareerGoal g, int mate)
    {
        switch (g.kind)
        {
            case GoalFinish: return g.n <= 1 ? "Win the race" : g.n == 3 ? "Finish on the podium" : g.n >= 10 ? "Score points (top 10)" : "Finish in the top " + g.n;
            case GoalQuali: return g.n <= 1 ? "Take pole position" : "Qualify in the top " + g.n;
            default:
                if (g.who == Season.RivalId) return "Finish ahead of " + Season.RivalShort;
                if (g.who == mate) return "Beat your teammate " + Season.Name(g.who);
                return "Beat " + TeamName(Season.TeamOf(g.who)) + "'s " + Season.Name(g.who);
        }
    }

    // judged on the race result (order: roster ids, finishing order) and the player's qualifying position
    public static bool Met(CareerGoal g, int[] order, int qualiPos)
    {
        int me = System.Array.IndexOf(order, 0) + 1;
        switch (g.kind)
        {
            case GoalFinish: return me > 0 && me <= g.n;
            case GoalQuali: return qualiPos > 0 && qualiPos <= g.n;
            default: { int him = System.Array.IndexOf(order, g.who) + 1; return me > 0 && (him <= 0 || me < him); }
        }
    }

    // ------------------------------------------------------------ season goals (3)
    public const int SeasonGoalCount = 3;
    public static void SeasonGoals(string[] text, int[] state)
    {
        var d = Season.Data; if (d == null) return;
        bool over = Season.Over, any = d.results.Count > 0;
        // 1. the teams' table
        bool ahead = Season.TeamPoints(d.team) > Season.TeamPoints(d.rivalTeam);
        text[0] = "Beat " + TeamName(d.rivalTeam) + " in the teams' table";
        state[0] = over ? (ahead ? Done : Failed) : any && ahead ? OnCourse : Open;
        // 2. promotion / the title
        int rank = Season.Rank(0);
        if (d.tier < Season.TierPremier)
        {
            text[1] = "Top " + Season.PromoteTop + " in the championship: move up to " + Season.TierNames[d.tier + 1];
            state[1] = over ? (rank <= Season.PromoteTop ? Done : Failed) : any && rank <= Season.PromoteTop ? OnCourse : Open;
        }
        else
        {
            text[1] = "Win the world title";
            state[1] = over ? (rank == 1 ? Done : Failed) : any && rank == 1 ? OnCourse : Open;
        }
        // 3. the nemesis, once
        bool beat = false;
        for (int r = 0; r < d.results.Count; r++)
        {
            if (d.results[r].dnf) continue;
            int me = Season.FinishOf(r), him = Season.FinishOf(r, Season.RivalId);
            if (me > 0 && (him <= 0 || me < him)) { beat = true; break; }
        }
        text[2] = "Beat " + Season.RivalShort + " in a race";
        state[2] = beat ? Done : over ? Failed : Open;
    }

    // points between the player and the nemesis (+ = the player ahead)
    public static int RivalGap() { var d = Season.Data; return d == null ? 0 : d.points[0] - d.points[Season.RivalId]; }
}
