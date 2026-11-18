// MenuHome.cs — the Home page of the front end (MenuUI, Page.Start), over the 3D studio (MenuStudio.cs: the driver
// standing in the middle, the car behind him; drag across the middle to turn him).
//  * top left: the driver card (number, name, level with its XP line, safety rating) -> Driver page
//  * top right: Inbox (placeholder) and Settings (round icons)
//  * left: OFFLINE (-> Play: mode, circuit, setup) and ONLINE (greyed, coming later); right: DRIVER and GARAGE
//  * bottom centre: one big red RACE = the last mode and circuit, straight to the Setup step
//  * bottom left: the daily challenge (a circuit and a lap time to beat; tap = set it up); the licence credits under it
// Strings are built when what they show changes (mode, circuit, profile, day), not per frame.
using FormulaSim;
using UnityEngine;

public sealed partial class MenuUI
{
    const string KeyHome = "home";
    static readonly Color shadeTop = new Color(0f, 0f, 0f, 0.55f), shadeBottom = new Color(0f, 0f, 0f, 0.78f), clear0 = new Color(0f, 0f, 0f, 0f);
    static readonly Color shadeSide = new Color(0f, 0f, 0f, 0.5f);
    static readonly Color badgeTop = new Color(1f, 0.16f, 0.09f, 1f), badgeBottom = new Color(0.78f, 0.02f, 0f, 1f);

    int raceSubKey = -1; string raceSub;
    int dailyDay = -1, dailyMap = -1; float dailyCheckT = -100f; string dailyText;

    Choice DrawHome(ref int map)
    {
        float sw = Screen.width, sh = Screen.height;
        // shading over the 3D so the UI reads (fixed colours: the gradient textures are cached)
        UiKit.Gradient(new Rect(0, 0, sw, sh * 0.32f), shadeTop, clear0);
        UiKit.Gradient(new Rect(0, sh * 0.58f, sw, sh * 0.42f), clear0, shadeBottom);
        UiKit.Gradient(new Rect(0, 0, sw * 0.36f, sh), shadeSide, clear0, 0f, true);
        UiKit.Gradient(new Rect(sw * 0.64f, 0, sw * 0.36f, sh), clear0, shadeSide, 0f, true);

        var safe = UiKit.Safe;
        float M = U(20), x0 = safe.x + M, x1 = safe.xMax - M, W = x1 - x0, top = safe.y + U(12);
        float tileW = Mathf.Clamp(W * 0.24f, U(150), U(196)), tileH = U(74), tg = U(10);
        float raceW = Mathf.Clamp(W * 0.36f, U(220), U(280)), raceH = U(62);
        var race = new Rect(safe.center.x - raceW * 0.5f, safe.yMax - U(34) - raceH, raceW, raceH);
        float midY = top + U(64) + (race.y - U(12) - (top + U(64))) * 0.5f;
        var l0 = new Rect(x0, midY - tileH - tg * 0.5f, tileW, tileH); var l1 = new Rect(x0, midY + tg * 0.5f, tileW, tileH);
        var r0 = new Rect(x1 - tileW, l0.y, tileW, tileH); var r1 = new Rect(x1 - tileW, l1.y, tileW, tileH);

        // drag across the middle: turn the driver
        studio.Spin(new Rect(l0.xMax + U(8), top + U(66), r0.x - l0.xMax - U(16), race.y - U(8) - (top + U(66))), false);

        var ch = Choice.None;
        // ---- driver card
        float e0 = UiKit.Enter(KeyHome, 0);
        UiKit.PushAlpha(e0);
        if (DriverCard(UiKit.Slide(new Rect(x0, top, U(240), U(60)), e0, -12f))) GoPage(Page.Driver);
        UiKit.PopAlpha();

        // ---- inbox, settings
        float e1 = UiKit.Enter(KeyHome, 1);
        UiKit.PushAlpha(e1);
        if (UiKit.IconButton(new Rect(x1 - U(44), top, U(44), U(44)), UiKit.IconSettings)) ch = Choice.Settings;
        if (InboxButton(new Rect(x1 - U(96), top, U(44), U(44)))) Toast("Inbox  ·  coming soon");
        UiKit.PopAlpha();

        // ---- tiles
        if (HomeTile(l0, "Quick race · weekend · season", "OFFLINE", null, true, 2, -1)) GoPlay(StepMode, 1);
        HomeTile(l1, "Online racing", "ONLINE", "Coming later", false, 3, -1);
        if (HomeTile(r0, "Profile · stats · rating", "DRIVER", null, true, 2, 1)) GoPage(Page.Driver);
        if (HomeTile(r1, "Car class · livery", "GARAGE", null, true, 3, 1)) GoPage(Page.Garage);

        // ---- RACE: the last mode and circuit, straight to Setup
        string sub = RaceSub(map);
        float er = UiKit.Enter(KeyHome, 4, 0.04f, 0.45f);
        UiKit.PushAlpha(er);
        var rr = UiKit.Rise(race, er, 12f);
        UiKit.Glow(new Rect(rr.center.x - rr.width, rr.y - rr.height * 0.6f, rr.width * 2f, rr.height * 2.4f), new Color(UiKit.Accent.r, UiKit.Accent.g, UiKit.Accent.b, 0.16f));
        if (UiKit.Button(rr, "Race", UiKit.Look.Primary, true, 24f, sub, 14f)) GoPlay(StepSetup, 1);
        UiKit.PopAlpha();

        // ---- daily challenge (bottom left)
        Daily();
        if (dailyText != null)
        {
            float dw = Mathf.Max(U(120), race.x - U(16) - x0);
            var dr = new Rect(x0, race.y + U(8), dw, U(46));
            float ed = UiKit.Enter(KeyHome, 5);
            UiKit.PushAlpha(ed);
            if (UiKit.Press(dr, true, out float p) && dailyMap >= 0 && dailyMap < mapNames.Length)
            { map = dailyMap; mode = ModeQuick; PlayerPrefs.SetInt("gameMode", mode); GoPlay(StepSetup, 1); }
            if (p > 0.01f) UiKit.Rect(dr, new Color(1, 1, 1, 0.06f * p), 10f);
            UiKit.Bar(new Rect(dr.x, dr.y + U(9), U(3), U(28)), UiKit.Warn);
            UiKit.Eyebrow(new Rect(dr.x + U(10), dr.y + U(6), dw, U(14)), "Daily challenge", UiKit.Warn, 8.5f);
            UiKit.Text(new Rect(dr.x + U(10), dr.y + U(20), dw - U(12), U(20)), dailyText, UiKit.Semi, UiKit.Fit(dailyText, UiKit.Semi, 11.5f, dw - U(12), 8.5f), UiKit.TextCol);
            UiKit.PopAlpha();
        }

        // licence credits for the car and steering-wheel models (CC BY): always on the home page
        UiKit.Text(new Rect(x0, safe.yMax - U(25), W, U(11)), CarModel.CarCredit, UiKit.Regular, 7f, UiKit.A(UiKit.Faint, 0.85f * e0));
        UiKit.Text(new Rect(x0, safe.yMax - U(15), W, U(11)), CarModel.WheelCredit + "  ·  " + RealWorld.Attribution, UiKit.Regular, 7f, UiKit.A(UiKit.Faint, 0.85f * e0));
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

    // the driver card: number badge, name, level + XP line, safety rating. true = tapped
    bool DriverCard(Rect r)
    {
        MenuProfile.Load();
        bool hit = UiKit.Press(r, true, out float p);
        var c = UiKit.Scale(r, 1f - 0.03f * p);
        UiKit.Panel(c, 14f);
        var badge = new Rect(c.x + U(8), c.y + U(8), U(44), U(44));
        UiKit.Gradient(badge, badgeTop, badgeBottom, 10f);
        UiKit.Text(badge, MenuProfile.NumStr, UiKit.Black, 20f, Color.white, TextAnchor.MiddleCenter);
        float tx = badge.xMax + U(10), tw = c.xMax - U(48) - tx;
        UiKit.Text(new Rect(tx, c.y + U(7), tw, U(22)), MenuProfile.Name, UiKit.Bold, UiKit.Fit(MenuProfile.Name, UiKit.Bold, 15f, tw, 10f), UiKit.TextCol);
        UiKit.Eyebrow(new Rect(tx, c.y + U(28), tw, U(14)), MenuProfile.LevelStr, UiKit.Sub, 8.5f);
        // XP to the next level
        var bar = new Rect(tx, c.yMax - U(12), c.xMax - U(12) - tx, Mathf.Max(2f, U(3)));
        UiKit.Rect(bar, new Color(1, 1, 1, 0.10f), 2f);
        UiKit.Rect(new Rect(bar.x, bar.y, bar.width * MenuProfile.XpFrac, bar.height), UiKit.Accent, 2f);
        // safety rating
        var sr = new Rect(c.xMax - U(40), c.y + U(8), U(30), U(30));
        UiKit.Rect(sr, new Color(1, 1, 1, 0.07f), 8f); UiKit.Outline(sr, UiKit.Good, 8f, 1f);
        UiKit.Text(sr, MenuProfile.Safety, UiKit.Black, 15f, UiKit.Good, TextAnchor.MiddleCenter);
        UiKit.Text(new Rect(sr.x - U(6), sr.yMax, sr.width + U(12), U(10)), "SR", UiKit.Bold, 7f, UiKit.Faint, TextAnchor.MiddleCenter);
        return hit;
    }

    // a big glass tile; slideDir: -1 enters from the left, 1 from the right
    bool HomeTile(Rect r, string eyebrow, string title, string sub, bool enabled, int n, int slideDir)
    {
        float e = UiKit.Enter(KeyHome, n, 0.05f, 0.4f);
        r = UiKit.Slide(r, e, 18f * slideDir);
        UiKit.PushAlpha(e * (enabled ? 1f : 0.55f));
        bool hit = UiKit.Press(r, enabled, out float p);
        var c = UiKit.Scale(r, 1f - 0.03f * p);
        UiKit.Panel(c, 14f);
        if (p > 0.01f) UiKit.Rect(c, new Color(1, 1, 1, 0.06f * p), 14f);
        UiKit.Bar(new Rect(c.x + U(14), c.y + U(12), U(18), U(3)), enabled ? UiKit.Accent : UiKit.Faint);
        float tw = c.width - U(48);
        UiKit.Eyebrow(new Rect(c.x + U(14), c.y + U(18), c.width - U(20), U(14)), eyebrow, UiKit.Faint, 8f);
        UiKit.Text(new Rect(c.x + U(13), c.y + U(31), tw, U(30)), title, UiKit.Black, UiKit.Fit(title, UiKit.Black, 22f, tw, 14f), enabled ? UiKit.TextCol : UiKit.Sub);
        if (enabled) UiKit.Icon(new Rect(c.xMax - U(30), c.y + U(38), U(14), U(14)), UiKit.IconNext, UiKit.Sub);
        if (sub != null)
        {
            string tag = UiKit.Upper(sub); float w = UiKit.TagWidth(tag, 7.5f);
            UiKit.Tag(new Rect(c.xMax - U(10) - w, c.y + U(10), w, U(16)), tag, new Color(1, 1, 1, 0.10f), UiKit.Sub, 7.5f);
        }
        UiKit.PopAlpha();
        return hit;
    }

    // round glass button with an envelope (and a red dot: something new)
    static bool InboxButton(Rect r)
    {
        bool hit = UiKit.Press(r, true, out float p);
        var rr = UiKit.Scale(r, 1f - 0.06f * p);
        UiKit.Shadow(rr, 999f, 6f, 0.25f, 2f);
        UiKit.Rect(rr, Color.Lerp(new Color(0.12f, 0.13f, 0.17f, 0.88f), new Color(0.22f, 0.23f, 0.28f, 0.95f), p), 999f);
        UiKit.Outline(rr, new Color(1, 1, 1, 0.14f + 0.12f * p), 999f, 0.7f);
        Envelope(UiKit.Scale(rr, 0.5f), UiKit.TextCol);
        UiKit.Dot(new Vector2(rr.xMax - U(10), rr.y + U(10)), 3.6f, UiKit.Accent);
        return hit;
    }
    static void Envelope(Rect r, Color c)
    {
        float s = Mathf.Min(r.width, r.height), w = s * 0.92f, h = s * 0.64f, t = Mathf.Max(1.5f, s * 0.085f);
        var b = new Rect(r.center.x - w * 0.5f, r.center.y - h * 0.5f, w, h);
        UiKit.Outline(b, c, 2f, 1.2f);
        float dx = w * 0.5f, dy = h * 0.58f, len = Mathf.Sqrt(dx * dx + dy * dy), ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
        var m = GUI.matrix;
        GUIUtility.RotateAroundPivot(ang, new Vector2(b.x, b.y));
        UiKit.Rect(new Rect(b.x, b.y - t * 0.5f, len, t), c, 99f);
        GUI.matrix = m;
        GUIUtility.RotateAroundPivot(180f - ang, new Vector2(b.xMax, b.y));
        UiKit.Rect(new Rect(b.xMax, b.y - t * 0.5f, len, t), c, 99f);
        GUI.matrix = m;
    }

    // today's challenge: a circuit (by the day of the year) and a lap a little slower than the real pole
    void Daily()
    {
        if (Time.unscaledTime < dailyCheckT) return;
        dailyCheckT = Time.unscaledTime + 60f;
        int day = System.DateTime.Now.DayOfYear;
        if (day == dailyDay) return;
        dailyDay = day; dailyText = null; dailyMap = -1;
        int n = 0;
        for (int i = 0; i < Circuits.All.Length && i < mapNames.Length; i++) if (i != playgroundIdx && Circuits.All[i].realPole > 0) n++;
        if (n == 0) return;
        int pick = day % n;
        for (int i = 0; i < Circuits.All.Length && i < mapNames.Length; i++)
        {
            if (i == playgroundIdx || Circuits.All[i].realPole <= 0) continue;
            if (pick-- > 0) continue;
            dailyMap = i;
            dailyText = cards[i].name + "  ·  lap under " + Fmt(Circuits.All[i].realPole * 1.07);
            break;
        }
    }
}
