// Story.cs — the career's story: short full-screen cards between rounds (SeasonUI.DrawCard draws them, Paper style).
//  The promise: Teo Varga, your late mentor, lost the world title twenty years ago when Viktor Raske put him in the wall.
//  He went home, built a kart track and taught you. Last winter, from a hospital bed: "Bring it home, kid." You promised.
//  Viktor Raske: seven-time champion, Argent, cold and precise, going for an eighth. His son Nico races the junior
//  tiers. Mia Varga (10), Teo's granddaughter, sends you messages. Lena Brandt is your team principal.
//  Beats: the prologue and the contract, a chapter card + one voice + the weekend's goals before every round, the result
//  with the goals ticked + a reaction after it, and the season's end (promoted / the rivalry continues / "Promise kept").
//  Docs/CAREER.md has the whole script. Cards are built once per event (never per frame).
//  Each card also names a short 3D scene (StoryCard.scene, StoryFilm.cs): with Settings > GAME "Story" = Film the
//  career page plays a run of such cards as one real-time film (the game's own circuit, cars and people) with the
//  card's lines as captions; the goals cards stay cards. 0 = no scene (always a card).
using System.Collections.Generic;

public sealed class StoryCard
{
    public const int Chapter = 0, Message = 1, Quote = 2, Goals = 3, Memory = 4;
    public const int Nobody = 0, Teo = 1, Mia = 2, Boss = 3, Viktor = 4, Nico = 5;
    // the 3D scene the film shows for this card (StoryFilm.cs); SNone = a card only (the goals)
    public const int SNone = 0, SCrash = 1, SAftermath = 2, SHelmet = 3, SRival = 4, SPitLane = 5, SBriefing = 6, SMia = 7, SMiaCheer = 8,
                     SEstablish = 9, SRivalLeaves = 10, SMemory = 11, SDebrief = 12, SCelebrate = 13, SCount = 14;
    public int kind, who, scene;
    public string kicker, title, body, figure, footer;
    public CareerGoal[] goals;   // Goals cards
    public bool judged;          // Goals: show met / missed
}

public static class Story
{
    public const string BossName = "Lena Brandt", MiaName = "Mia Varga", TeoName = "Teo Varga";
    static readonly string[] Figures = { "01", "02", "03", "04", "05", "06", "07", "08", "09", "10" };

    static StoryCard C(string kicker, string title, string body, string figure = null) => new StoryCard { kind = StoryCard.Chapter, kicker = kicker, title = title, body = body, figure = figure, scene = StoryCard.SEstablish };
    static StoryCard Msg(int who, string title, string body = null)
    {
        string k = who == StoryCard.Mia ? "Message  ·  " + MiaName + ", 10" : "Message  ·  " + BossName + ", team principal";
        return new StoryCard { kind = StoryCard.Message, who = who, kicker = k, title = Q(title), body = body, scene = who == StoryCard.Mia ? StoryCard.SMia : StoryCard.SBriefing };
    }
    static StoryCard Rival(string title, string body = null, string kicker = null)
    {
        bool v = Season.Viktor;
        return new StoryCard { kind = StoryCard.Quote, who = v ? StoryCard.Viktor : StoryCard.Nico, title = Q(title), body = body, scene = StoryCard.SRival,
                               kicker = kicker ?? (v ? "Viktor Raske  ·  seven-time champion" : "Nico Raske  ·  Argent junior") };
    }
    static StoryCard Memory(string kicker, string title, string body) => new StoryCard { kind = StoryCard.Memory, who = StoryCard.Teo, kicker = kicker, title = title, body = body, scene = StoryCard.SMemory };
    static StoryCard S(StoryCard c, int scene) { c.scene = scene; return c; } // a card with another scene than its kind's
    static string Q(string s) => "“" + s + "”";
    static string Pick(string[] a, int k) => a[((k % a.Length) + a.Length) % a.Length];

    static string TeamName => Season.Data != null ? Season.Teams[Season.Data.team] : "";
    static string RivalTeam => Season.Data != null ? Career.TeamName(Season.Data.rivalTeam) : "";
    static string Fill(string s) => s.Replace("{RF}", Season.RivalFull).Replace("{RS}", Season.RivalShort).Replace("{RT}", RivalTeam).Replace("{T}", TeamName);

    // ------------------------------------------------------------ the start
    public static List<StoryCard> Prologue()
    {
        return new List<StoryCard>
        {
            S(C("Prologue", "Twenty years ago, Teo Varga was one corner from the world title.", "Viktor Raske dived for a gap that wasn't there. Teo hit the wall. Raske took the trophy."), StoryCard.SCrash),
            S(Memory(TeoName + "  ·  your mentor", "He never raced at the top again.", "He came home, built a kart track by the harbour, and found a kid who always wanted one more lap. You."), StoryCard.SAftermath),
            S(Memory("Last winter", Q("Bring it home, kid."), "His old helmet is in your bag now. You promised."), StoryCard.SHelmet),
            new StoryCard { kind = StoryCard.Quote, who = StoryCard.Viktor, kicker = "Viktor Raske  ·  seven-time champion", title = Q("Teo Varga? I don't remember him."), body = "Thirteen seasons at the top. Cold, precise, unbeaten. He wants an eighth title.", scene = StoryCard.SRival },
            S(Season.Active && Season.Viktor
                ? C("Premier class", "The world title starts here.", "Ten rounds. One trophy. Viktor Raske wants his eighth.")
                : C("Rookie tier", "Every champion starts somewhere.", "Ten rounds. The top three move up. Raske's son Nico is on the grid, with his father's elbows."), StoryCard.SPitLane),
        };
    }
    // after the contract is signed
    public static List<StoryCard> Welcome()
    {
        return new List<StoryCard>
        {
            Msg(StoryCard.Boss, "Welcome to " + TeamName + ". We're not here to make up the numbers.", "Season goal: beat " + RivalTeam + " in the teams' table."),
            Msg(StoryCard.Mia, "Grandpa said you brake later than anyone. Prove it!!", "Teo's granddaughter. She has never missed one of your races."),
        };
    }
    // the first card of a new season (season 2 on)
    public static StoryCard SeasonStart()
    {
        var d = Season.Data;
        string tier = Season.TierNames[Season.Tier];
        var last = Season.LastSeason;
        string title = last != null && last.promoted ? (Season.Viktor ? "The Premier class." : tier + " tier.")
                     : last != null && last.champion && Season.Viktor ? "Number one on the nose." : "Back to work.";
        string body = Season.Viktor && last != null && last.promoted ? "The world title. " + TeamName + " against Argent. Viktor Raske against you."
                    : "New season, new target: beat " + RivalTeam + " in the teams' table.";
        return C("Season " + d.seasonNo + "  ·  " + tier + " tier", title, body);
    }

    // ------------------------------------------------------------ before a round
    static readonly string[] RoundTitle =
    {
        "Suzuka. Your first race.", "Monaco. No room for mistakes.", "Barcelona. Nowhere to hide.", "Austria. A short lap, long straights.",
        "Britain. Fast, cold, flat out.", "Spa. Teo's favourite.", "Hungary. Tight, hot, relentless.", "Zandvoort. Banked corners, orange smoke.",
        "Monza. The temple of speed.", "Austin. The last round.",
    };
    static readonly string[] RoundBody =
    {
        "Lena's orders: bring the car home. Mia has set three alarms for the start.",
        "Qualifying is the race here. Teo won in these streets once, in the rain.",
        "Every team knows this lap by heart. Your teammate wants to prove he is still number one.",
        "{RT} brought a new floor. Lena wants them behind you on Sunday.",
        "Halfway. The paddock has started to say your name the right way.",
        "‘Eau Rouge is a question,’ he used to say. ‘Answer it flat.’ {RF} is watching your laps.",
        "Nowhere to pass. Saturday decides Sunday.",
        "Mia's whole class is watching on the big screen at school.",
        "{RS} won here last year by eleven seconds. Lena: ‘Take his slipstream. Then take his place.’",
        "One more chance to show them who you are.",
    };

    public static List<StoryCard> PreRound()
    {
        var d = Season.Data; int r = d.round; var l = new List<StoryCard>();
        if (r == 0 && d.seasonNo > 1) l.Add(SeasonStart());
        string kicker = "Round " + (r + 1) + " of " + Season.Rounds + "  ·  " + Season.GpNames[r] + "  ·  " + Season.TierNames[Season.Tier] + " tier";
        string title = RoundTitle[r], body = Fill(RoundBody[r]);
        if (r == 0 && d.seasonNo > 1) { title = "Suzuka. A new season starts here."; body = "Lena's orders: start the way you mean to finish."; }
        if (r == Season.Rounds - 1 && Career.TitleFight())
        {
            var st = Season.Standings(); int gap = d.points[st[0]] - d.points[0];
            title = "Austin. Everything comes down to this.";
            body = gap <= 0 ? "You lead the championship. One race. Teo's helmet is in the garage." : gap + " points behind. One race. Teo's helmet is in the garage.";
        }
        l.Add(C(kicker, title, body, Figures[r]));
        bool v = Season.Viktor;
        switch (r)
        {
            case 0: l.Add(Msg(StoryCard.Boss, "Clean race, clean car. The rest will come.")); break;
            case 1: l.Add(Rival(v ? "Monaco forgives nobody. Least of all rookies." : "Nice helmet. Shame about the driver.")); break;
            case 2: l.Add(Msg(StoryCard.Mia, "Grandpa's café has your poster up now. The baker says it's crooked. It's not.")); break;
            case 3: l.Add(Msg(StoryCard.Boss, RivalTeam + " are the team to beat this year. Start today.")); break;
            case 4: l.Add(Rival(v ? "He is fast. Fast is not enough." : "My father says rookies learn the hard way. I say they don't learn at all.")); break;
            case 5: l.Add(Memory("A memory", Q("Flat through Eau Rouge, kid. Your heart lifts before the car does."), "Teo, the first time he showed you an onboard lap of Spa.")); break;
            case 6: l.Add(Msg(StoryCard.Boss, "Track position is everything here. Nail qualifying.")); break;
            case 7: l.Add(Msg(StoryCard.Mia, "My teacher said I can do my project on you. I'm calling it ‘The Promise’.")); break;
            case 8: l.Add(Rival(v ? "I have seven trophies. Give me a reason to remember your name." : "Monza is about courage. You'll need to borrow some.")); break;
            default: l.Add(Msg(StoryCard.Mia, "Whatever happens, Grandpa would be proud. (But WIN.)")); break;
        }
        if (d.goalsFor != r) Career.FreezeGoals();
        l.Add(new StoryCard { kind = StoryCard.Goals, kicker = "This weekend  ·  " + Season.GpNames[r], title = "Your goals", goals = d.goals.ToArray(), footer = "+" + Season.XpPerGoal + " XP each" });
        return l;
    }

    // ------------------------------------------------------------ after a round
    static readonly string[] MiaWin = { "YOU WON!!! I screamed so loud the dog hid under the bed.", "Grandpa always said you'd do it. I didn't cry. (I cried.)", "The whole café stood on the chairs. Mum says that's not allowed. Nobody listened." };
    static readonly string[] MiaPodium = { "YOU WERE ON THE BIG SCREEN!! Everyone at the café cheered.", "A podium!! I put a gold sticker on the calendar. Grandpa would've put two.", "Mum let me stay up for the podium. Worth it." };
    static readonly string[] ViktorBeaten = { "Enjoy it. It will not happen twice.", "Interesting. That is all. Interesting.", "Fine. Now I remember the name." };
    static readonly string[] NicoBeaten = { "Lucky. Next time I won't leave the door open.", "My car was broken. Write that down.", "Don't get used to the view of my mirrors." };
    static readonly string[] BossGood = { "That's the drive we signed you for.", "Points on the board. The factory is buzzing.", "Solid. Now let's find another tenth." };
    static readonly string[] BossBad = { "Reset. Debrief at eight. We go again.", "Bad days teach the most. Learn fast.", "Not our weekend. The next one is." };
    static readonly string[] ViktorTaunt = { "Rookies learn the hard way.", "Teo's boy? Then he knows how this ends.", "I don't race the midfield. I lap it." };
    static readonly string[] NicoTaunt = { "Did you see me? No? I was too far ahead.", "Dad says hi. From the podium.", "Keep trying. It's cute." };

    // the round just recorded (index ri in the results)
    public static List<StoryCard> PostRound(int ri)
    {
        var d = Season.Data; var res = d.results[ri]; var l = new List<StoryCard>();
        int me = Season.FinishOf(ri), him = Season.FinishOf(ri, Season.RivalId);
        bool v = Season.Viktor, beat = !res.dnf && me > 0 && (him <= 0 || me < him);
        // the result and the goals, ticked
        var goals = new CareerGoal[res.goalCount];
        if (ri == d.results.Count - 1 && d.goals.Count == res.goalCount) for (int i = 0; i < goals.Length; i++) goals[i] = d.goals[i];
        int met = 0; for (int i = 0; i < 32 && i < res.goalCount; i++) if ((res.goals & (1 << i)) != 0) met++;
        string pts = me > 0 && me <= Season.Points.Length && !res.dnf ? "+" + Season.Points[me - 1] + " pts" : "No points";
        int xp = ri == d.results.Count - 1 && Season.LastOutcome.goalCount == res.goalCount ? Season.LastOutcome.bonusXp : UnityEngine.Mathf.RoundToInt(met * Season.XpPerGoal * MenuProfile.XpMult);
        l.Add(new StoryCard
        {
            kind = StoryCard.Goals, judged = true, goals = goals,
            kicker = "Round " + (ri + 1) + "  ·  " + Season.GpNames[ri] + "  ·  result",
            figure = res.dnf ? "DNF" : "P" + me,
            title = pts + "  ·  championship P" + Season.Rank(0),
            footer = met > 0 ? met + " of " + res.goalCount + " goals  ·  +" + xp + " XP" : "No goals met",
        });
        // one voice (two after a big day)
        if (res.dnf) l.Add(S(Msg(StoryCard.Boss, "You walked away from a race. We don't do that here.", "Retired  ·  no points."), StoryCard.SDebrief));
        else if (me == 1) { l.Add(S(Msg(StoryCard.Mia, Pick(MiaWin, ri)), StoryCard.SMiaCheer)); if (beat) l.Add(Rival(Pick(v ? ViktorBeaten : NicoBeaten, ri))); }
        else if (me <= 3) l.Add(S(Msg(StoryCard.Mia, Pick(MiaPodium, ri)), StoryCard.SMiaCheer));
        else if (beat) l.Add(Rival(Pick(v ? ViktorBeaten : NicoBeaten, ri)));
        else if (me <= 10) l.Add(Msg(StoryCard.Boss, Pick(BossGood, ri)));
        else if (him == 1) l.Add(S(Rival(Pick(v ? ViktorTaunt : NicoTaunt, ri)), StoryCard.SRivalLeaves));
        else { l.Add(S(Msg(StoryCard.Boss, Pick(BossBad, ri)), StoryCard.SDebrief)); if (ri % 2 == 0) l.Add(Msg(StoryCard.Mia, "Grandpa said the bad days are the ones that make you. Next one's yours.")); }
        return l;
    }

    // ------------------------------------------------------------ the season's end
    public static List<StoryCard> SeasonEnd()
    {
        var d = Season.Data; var l = new List<StoryCard>();
        int rank = Season.Rank(0), rr = Season.Rank(Season.RivalId);
        string kicker = "Season " + d.seasonNo + "  ·  " + Season.TierNames[Season.Tier] + " tier  ·  final standings";
        if (Season.Viktor && rank == 1)
        {
            bool again = Season.Titles > 0;
            l.Add(S(C(again ? "World champion again" : "World champion", "Promise kept.", "You cross the line, look up at the sky and say his name.", "1"), StoryCard.SCelebrate));
            l.Add(new StoryCard { kind = StoryCard.Quote, who = StoryCard.Viktor, kicker = "Viktor Raske  ·  behind the podium", title = Q("Teo Varga. I remember him now."), body = "He shakes your hand and holds it a moment too long.", scene = StoryCard.SRival });
            l.Add(S(Msg(StoryCard.Mia, "Grandpa's helmet is on the shelf next to your trophy. Mum says it can stay there forever."), StoryCard.SMiaCheer));
            l.Add(S(C(again ? "The crown, kept" : "The end of the beginning", again ? "Still number one." : "You brought it home.", "Next season you race with number 1 on the car. Raske wants it back."), StoryCard.SCelebrate));
        }
        else if (!Season.Viktor && rank <= Season.PromoteTop)
        {
            bool toPremier = Season.Tier + 1 >= Season.TierPremier;
            l.Add(S(C(kicker, toPremier ? "Promoted to Premier." : "Promoted to Challenger.", toPremier ? "The top class. The world title. And Viktor Raske." : "Faster cars, harder rivals. Nico Raske is coming with you.", "P" + rank), StoryCard.SCelebrate));
            if (toPremier) l.Add(new StoryCard { kind = StoryCard.Quote, who = StoryCard.Viktor, kicker = "Viktor Raske  ·  seven-time champion", title = Q("So Teo's boy made it. Let's see how long he stays."), scene = StoryCard.SRival });
            else l.Add(Rival("See you in Challenger. Bring a bigger mirror."));
            l.Add(S(Msg(StoryCard.Mia, toPremier ? "PREMIER!!! I told everyone at school. Twice." : "Challenger!! That sounds like a superhero. You're a superhero."), StoryCard.SMiaCheer));
        }
        else
        {
            l.Add(S(C(kicker, "The rivalry continues.", Season.RivalFull + " finished P" + rr + ". Lena: ‘Same goals, sharper knives. Next year.’", "P" + rank), StoryCard.SDebrief));
            l.Add(Msg(StoryCard.Mia, "Grandpa lost too, sometimes. He never stopped. Neither will you."));
        }
        return l;
    }
}
