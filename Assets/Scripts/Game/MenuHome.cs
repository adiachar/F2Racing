// MenuHome.cs — the Home page of the front end (MenuUI, Page.Start), over the 3D studio (MenuStudio.cs: the driver
// standing alone in the middle; drag across the middle to turn him). Minimal on purpose:
//  * top left: the driver card (number, name, level with its XP line) -> Driver page; top right: Settings
//  * left: OFFLINE (-> Play: mode, circuit, setup); right: DRIVER
//  * bottom centre: one big red RACE = the last mode and circuit, straight to the Setup step; licence credits under it
// Red is only on RACE; the XP line is the one info accent (cyan). Strings are built when what they show changes.
using FormulaSim;
using UnityEngine;

public sealed partial class MenuUI
{
    const string KeyHome = "home";
    static readonly Color shadeTop = new Color(0f, 0f, 0f, 0.45f), shadeBottom = new Color(0f, 0f, 0f, 0.70f), clear0 = new Color(0f, 0f, 0f, 0f);
    static readonly Color badgeCol = new Color(1f, 1f, 1f, 0.07f);
    // the plain page behind every 2D page of the front end: cool charcoal, top to bottom (no glows, no red)
    static readonly Color pageTop = new Color(0.086f, 0.094f, 0.112f, 0.97f), pageBottom = new Color(0.052f, 0.057f, 0.070f, 0.99f);
    internal static void Plain(float opacity = 1f)
    {
        UiKit.PushAlpha(opacity);
        UiKit.Gradient(new Rect(0, 0, Screen.width, Screen.height), pageTop, pageBottom);
        UiKit.PopAlpha();
    }

    int raceSubKey = -1; string raceSub;

    Choice DrawHome(ref int map)
    {
        float sw = Screen.width, sh = Screen.height;
        // a little shade top and bottom over the 3D so the UI reads (fixed colours: cached gradients)
        UiKit.Gradient(new Rect(0, 0, sw, sh * 0.28f), shadeTop, clear0);
        UiKit.Gradient(new Rect(0, sh * 0.62f, sw, sh * 0.38f), clear0, shadeBottom);

        var safe = UiKit.Safe;
        float M = U(24), x0 = safe.x + M, x1 = safe.xMax - M, W = x1 - x0, top = safe.y + U(16);
        float tileW = Mathf.Clamp(W * 0.22f, U(148), U(184)), tileH = U(64);
        float raceW = Mathf.Clamp(W * 0.34f, U(220), U(270)), raceH = U(60);
        var race = new Rect(safe.center.x - raceW * 0.5f, safe.yMax - U(34) - raceH, raceW, raceH);
        float midY = top + U(60) + (race.y - U(16) - (top + U(60))) * 0.5f;
        var left = new Rect(x0, midY - tileH * 0.5f, tileW, tileH);
        var right = new Rect(x1 - tileW, left.y, tileW, tileH);

        // drag across the middle: turn the driver
        studio.Spin(new Rect(left.xMax + U(8), top + U(62), right.x - left.xMax - U(16), race.y - U(8) - (top + U(62))));

        var ch = Choice.None;
        float e0 = UiKit.Enter(KeyHome, 0);
        UiKit.PushAlpha(e0);
        if (DriverCard(UiKit.Slide(new Rect(x0, top, U(220), U(56)), e0, -12f))) GoPage(Page.Driver);
        if (UiKit.IconButton(new Rect(x1 - U(44), top + U(6), U(44), U(44)), UiKit.IconSettings)) ch = Choice.Settings;
        UiKit.PopAlpha();

        if (HomeTile(left, "Play", "OFFLINE", 1, -1)) GoPlay(StepMode, 1);
        if (HomeTile(right, "Profile", "DRIVER", 1, 1)) GoPage(Page.Driver);

        // RACE: the last mode and circuit, straight to Setup
        string sub = RaceSub(map);
        float er = UiKit.Enter(KeyHome, 3, 0.04f, 0.45f);
        UiKit.PushAlpha(er);
        if (UiKit.Button(UiKit.Rise(race, er, 12f), "Race", UiKit.Look.Primary, true, 22f, sub, 14f)) GoPlay(StepSetup, 1);
        UiKit.PopAlpha();

        // licence credits for the car and steering-wheel models (CC BY): always on the home page
        UiKit.Text(new Rect(x0, safe.yMax - U(24), W, U(10)), CarModel.CarCredit, UiKit.Regular, 6.8f, UiKit.A(UiKit.Faint, 0.8f * e0));
        UiKit.Text(new Rect(x0, safe.yMax - U(14), W, U(10)), CarModel.WheelCredit + "  \u00b7  " + RealWorld.Attribution, UiKit.Regular, 6.8f, UiKit.A(UiKit.Faint, 0.8f * e0));
        return ch;
    }

    // "Quick race  ·  Monza": the last mode and circuit (RACE, the Play header); rebuilt only when they change
    string RaceSub(int map)
    {
        int key = mode * 1000 + map + (Season.Active ? (Season.Data.round + 1) * 100000 : 0);
        if (key != raceSubKey)
        {
            raceSubKey = key;
            if (mode == ModeChampionship) raceSub = ModeNames[mode] + "  ·  " + (Season.Active && !Season.Over ? Season.RoundName(Season.Data.round) : "your season");
            else raceSub = ModeNames[mode] + "  ·  " + cards[Mathf.Clamp(map, 0, cards.Length - 1)].name;
        }
        return raceSub;
    }

    // the driver card: number, name, level + XP line (cyan: progress). true = tapped
    bool DriverCard(Rect r)
    {
        MenuProfile.Load();
        bool hit = UiKit.Press(r, true, out float p);
        var c = UiKit.Scale(r, 1f - 0.03f * p);
        UiKit.Panel(c, 14f);
        var badge = new Rect(c.x + U(8), c.y + U(8), U(40), U(40));
        UiKit.Rect(badge, badgeCol, 10f); UiKit.Outline(badge, UiKit.Line, 10f, 0.7f);
        UiKit.Text(badge, MenuProfile.NumStr, UiKit.Bold, 18f, UiKit.TextCol, TextAnchor.MiddleCenter);
        float tx = badge.xMax + U(12), tw = c.xMax - U(14) - tx;
        UiKit.Text(new Rect(tx, c.y + U(7), tw, U(22)), MenuProfile.Name, UiKit.Bold, UiKit.Fit(MenuProfile.Name, UiKit.Bold, 15f, tw, 10f), UiKit.TextCol);
        UiKit.Text(new Rect(tx, c.y + U(27), tw, U(14)), MenuProfile.LevelStr, UiKit.Regular, 10f, UiKit.Sub);
        var bar = new Rect(tx, c.yMax - U(11), tw, Mathf.Max(2f, U(2.5f)));
        UiKit.Rect(bar, new Color(1, 1, 1, 0.08f), 2f);
        if (MenuProfile.XpFrac > 0.001f) UiKit.Rect(new Rect(bar.x, bar.y, Mathf.Max(bar.height, bar.width * MenuProfile.XpFrac), bar.height), UiKit.Info, 2f);
        return hit;
    }

    // a quiet glass tile: caption, title, chevron. slideDir: -1 enters from the left, 1 from the right
    bool HomeTile(Rect r, string caption, string title, int n, int slideDir)
    {
        float e = UiKit.Enter(KeyHome, n, 0.05f, 0.4f);
        r = UiKit.Slide(r, e, 16f * slideDir);
        UiKit.PushAlpha(e);
        bool hit = UiKit.Press(r, true, out float p);
        var c = UiKit.Scale(r, 1f - 0.03f * p);
        UiKit.Panel(c, 14f);
        if (p > 0.01f) UiKit.Rect(c, new Color(1, 1, 1, 0.06f * p), 14f);
        float px = c.x + U(16), tw = c.width - U(48);
        UiKit.Eyebrow(new Rect(px, c.y + U(12), tw, U(14)), caption, UiKit.Faint, 8f);
        UiKit.Text(new Rect(px, c.y + U(25), tw, U(28)), title, UiKit.Bold, UiKit.Fit(title, UiKit.Bold, 20f, tw, 13f), UiKit.TextCol);
        UiKit.Icon(new Rect(c.xMax - U(28), c.center.y - U(6), U(12), U(12)), UiKit.IconNext, UiKit.Sub);
        UiKit.PopAlpha();
        return hit;
    }
}
