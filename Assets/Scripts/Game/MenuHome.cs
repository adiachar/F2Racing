// MenuHome.cs — the Home page of the front end (MenuUI, Page.Start) in the "Paper" look (UiKit.cs), over the 3D studio
// (MenuStudio.cs: the driver standing on paper in the middle; drag across the middle to turn him). Layout (the
// "Redesign 1 · Paper" mockup, 844 x 390 units):
//  * masthead: "APEX WORLD SERIES" and the nav (PLAY underlined: Home is where play starts), a rule under it
//  * left: the driver's number as the giant red italic figure, his name, "country · level · safety" and a thin ink XP
//    line; the whole block opens the Driver page
//  * centre: the 3D driver (no 2D drawn over him)
//  * right: the next race: "NEXT · QUICK RACE", the circuit (or the championship round), length · laps · pole; a tap
//    changes it (the Circuit step, or the season hub in a championship)
//  * under the bottom rule: two text links for the other modes (Championship / Race weekend / Quick race, each with a
//    grey sub-line) and the black RACE = the last mode and circuit, straight to the Setup step; the licence credits.
// Strings are built when what they show changes, not per frame.
using FormulaSim;
using UnityEngine;

public sealed partial class MenuUI
{
    const string KeyHome = "home";

    int homeKey = int.MinValue; string homeNext, homeName, homeSub; bool homeSeasonTried;
    readonly int[] homeLinks = new int[2]; readonly string[] homeLinkSub = new string[2];

    // the home page's strings: rebuilt when the mode, circuit, race length, season or profile changes
    void HomeStrings(int map)
    {
        MenuProfile.Load();
        if (!homeSeasonTried) { homeSeasonTried = true; if (!Season.Active && Season.HasSave) Season.Load(); } // once: not a file check per frame
        var sd = Season.Data;
        int key = mode * 1000 + map + lapsIdx * 100000 + (sd != null ? (sd.round + 1) * 1000000 + sd.results.Count * 37 : 0)
                + cards[Mathf.Clamp(map, 0, cards.Length - 1)].length.Length * 3;
        if (key == homeKey && homeNext != null) return;
        homeKey = key;

        homeNext = "Next  ·  " + ModeNames[mode];
        bool running = Season.Active && !Season.Over;
        if (mode == ModeChampionship)
        {
            if (running)
            {
                homeName = Circuits.All[Season.CircuitIndex(sd.round)].name;
                homeSub = "Round " + (sd.round + 1) + " of " + Season.Rounds + "  ·  " + Season.GpNames[sd.round] + "  ·  " + lapsText[lapsIdx];
            }
            else { homeName = Season.Over ? "Season complete" : "New season"; homeSub = Season.Over ? "Start a new one" : "Pick your team"; }
        }
        else
        {
            var c = cards[Mathf.Clamp(map, 0, cards.Length - 1)];
            homeName = c.name;
            homeSub = (c.length != "—" ? c.length + "  ·  " : "") + (map == playgroundIdx ? "Free driving" : lapsText[lapsIdx] + (c.pole != "—" ? "  ·  pole " + c.pole : ""));
        }

        // the two links: the modes that RACE does not start
        int k = 0;
        for (int m = ModeChampionship; m >= ModeQuick && k < 2; m--) if (m != mode) homeLinks[k++] = m;
        for (int i = 0; i < 2; i++)
        {
            int m = homeLinks[i];
            homeLinkSub[i] = m == ModeChampionship ? (running ? "Round " + (sd.round + 1) + " of " + Season.Rounds + "  ·  P" + Season.Rank(0) : Season.Over ? "Season complete" : "New season")
                           : m == ModeWeekend ? "Quali + race" : "Straight to the grid";
        }
    }

    Choice DrawHome(ref int map)
    {
        // no page fill: the studio camera clears to the paper colour and the driver stands in the middle
        var f = Masthead(UiKit.NavPlay);
        HomeStrings(map);
        float y0 = f.safe.y, x0 = f.x0, x1 = f.x1;
        var ch = Choice.None;

        // ---- left: the driver (giant red italic number, name, id line, XP line) -> Driver page
        float lw = Mathf.Min(U(250), f.w * 0.3f);
        float e0 = UiKit.Enter(KeyHome, 0, 0.05f, 0.45f);
        UiKit.PushAlpha(e0);
        var block = UiKit.Slide(new Rect(x0, y0 + U(66), lw, U(194)), e0, -10f);
        bool hitDriver = DriverBlock(block, y0, true);
        UiKit.PopAlpha();
        if (hitDriver) GoPage(Page.Driver);

        // ---- right: the next race
        float rw = Mathf.Min(U(220), f.w * 0.27f);
        float e1 = UiKit.Enter(KeyHome, 1, 0.05f, 0.45f);
        UiKit.PushAlpha(e1);
        var card = UiKit.Slide(new Rect(x1 - rw, y0 + U(76), rw, U(120)), e1, 10f);
        bool hitNext = UiKit.Press(card, true, out float pn);
        UiKit.Kicker(new Rect(card.x, y0 + U(82), rw, U(14)), homeNext, UiKit.Fit(UiKit.Caps(homeNext), UiKit.Body, 10.5f, rw, 7.5f), TextAnchor.MiddleRight);
        float nameH = UiKit.Height(homeName, UiKit.Heavy, 26f, rw);
        UiKit.Text(new Rect(card.x, y0 + U(99), rw, nameH), homeName, UiKit.Heavy, 26f, Color.Lerp(UiKit.Ink, UiKit.Red, pn), TextAnchor.UpperRight, true);
        UiKit.Text(new Rect(card.x, y0 + U(103) + nameH, rw, U(16)), homeSub, UiKit.Body, UiKit.Fit(homeSub, UiKit.Body, 12f, rw, 8.5f), UiKit.InkSub, TextAnchor.MiddleRight);
        UiKit.PopAlpha();
        if (hitNext) { if (mode == ModeChampionship) season.Open(); else GoPlay(StepCircuit, 1); }

        // ---- drag across the middle: turn the driver
        studio.Spin(new Rect(block.xMax + U(8), f.head + U(4), card.x - U(8) - block.xMax - U(8), f.foot - U(4) - f.head - U(4)));

        // ---- the action row: links for the other two modes, the black RACE (the last mode and circuit, straight to Setup)
        float ea = UiKit.Enter(KeyHome, 3, 0.04f, 0.45f);
        UiKit.PushAlpha(ea);
        UiKit.Hair(x0, x1, f.foot);
        float raceW = UiKit.BlackButtonWidth("Race", 17f), lx = x0;
        var race = new Rect(x1 - raceW, f.actY - U(25), raceW, U(50));
        for (int i = 0; i < 2; i++)
        {
            int m = homeLinks[i];
            float w = UiKit.LinkWidth(ModeNames[m], homeLinkSub[i]) + U(8);
            if (UiKit.Link(new Rect(lx, f.actY - U(22), w, U(44)), ModeNames[m], homeLinkSub[i]))
            {
                mode = m; PlayerPrefs.SetInt("gameMode", mode);
                if (m == ModeChampionship) season.Open(); else GoPlay(StepCircuit, 1);
            }
            lx += w + U(20);
        }
        if (UiKit.BlackButton(race, "Race", 17f)) GoPlay(StepSetup, 1);
        UiKit.PopAlpha();

        // licence credits for the car and steering-wheel models (CC BY): always on the home page, at the very bottom
        float cwid = race.x - U(16) - x0, cy = f.bottom - U(11);
        UiKit.Text(new Rect(x0, cy - U(16), cwid, U(8)), People.Credit, UiKit.Body, UiKit.Fit(People.Credit, UiKit.Body, 6.5f, cwid, 5f), UiKit.A(UiKit.InkMuted, e0));
        UiKit.Text(new Rect(x0, cy - U(8), cwid, U(8)), CarModel.CarCredit, UiKit.Body, UiKit.Fit(CarModel.CarCredit, UiKit.Body, 6.5f, cwid, 5f), UiKit.A(UiKit.InkMuted, e0));
        UiKit.Text(new Rect(x0, cy, cwid, U(8)), CreditLine, UiKit.Body, UiKit.Fit(CreditLine, UiKit.Body, 6.5f, cwid, 5f), UiKit.A(UiKit.InkMuted, e0));
        return ch;
    }
    const string CreditLine = CarModel.WheelCredit + "  \u00b7  " + RealWorld.Attribution;
}
