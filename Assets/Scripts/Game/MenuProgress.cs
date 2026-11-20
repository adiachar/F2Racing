// MenuProgress.cs — the driver's progress on the results page (MenuUI.DrawResults, right of the timing table, past a
// quiet rule), in the "Paper" look (UiKit.cs), from MenuProfile.Last (MenuDriver.cs; rules in Progression.cs):
//  * EXPERIENCE: "+410 XP" (ExtraBold ink), the level line (ink on its track) filling from the old XP to the new
//    (0.6 s after the page opens, 1.4 s), a "LEVEL UP" tag popping in when it passes a level, the biggest three XP
//    sources under it
//  * SAFETY RATING: the grade letter (green S A B, ink C, amber D E), old -> new score, the change as a tag (white on
//    green up, white on amber down, ink when unchanged), up to three reasons with their values (green good, amber bad)
// Every string is built once when the race is recorded; the line and the level shown are the only things animated.
using UnityEngine;

public sealed partial class MenuUI
{
    RaceOutcome lvlUpFor; float lvlUpAt;

    void DrawProgressPanel(Rect r, string scr)
    {
        var o = MenuProfile.Last; if (o == null) return;
        float e = UiKit.Enter(scr, 6, 0.04f, 0.45f);
        UiKit.PushAlpha(e);
        r = UiKit.Rise(r, e, 12f);
        float x = r.x, w = r.width, y = r.y, bottom = r.yMax;

        // ---- experience: the line fills from the old total to the new one
        float k = UiKit.EaseInOut((UiKit.Opened(scr) - 0.6f) / 1.4f);
        float xpShown = o.xpBefore + (o.xpAfter - o.xpBefore) * k;
        int lvl = Progression.LevelOf((int)xpShown); float frac = Progression.FracOf(xpShown);
        bool up = lvl > o.levelBefore;
        if (up && !ReferenceEquals(lvlUpFor, o)) { lvlUpFor = o; lvlUpAt = Time.unscaledTime; }
        UiKit.Kicker(new Rect(x, y, w, U(14)), "Experience", 8.5f);
        UiKit.Text(new Rect(x, y, w, U(14)), Progression.LevelNames[lvl], UiKit.Heavy, 10f, up ? UiKit.Ink : UiKit.InkSub, TextAnchor.MiddleRight);
        y += U(15);
        UiKit.Text(new Rect(x, y, w, U(26)), o.xpGainStr, UiKit.Heavy, 20f, UiKit.Ink);
        if (up)
        {
            float p = Mathf.Clamp01((Time.unscaledTime - lvlUpAt) / 0.4f), tw = UiKit.StampWidth("LEVEL UP", 8.5f);
            var tag = UiKit.Scale(new Rect(x + w - tw, y + U(4), tw, U(17)), 1f + 0.3f * (1f - UiKit.EaseOut(p)));
            UiKit.Stamp(tag, "LEVEL UP", UiKit.Ink, Color.white, 8.5f);
        }
        else if (k >= 1f) UiKit.Text(new Rect(x, y, w, U(26)), o.xpAfterStr, UiKit.Body, 9f, UiKit.InkMuted, TextAnchor.MiddleRight);
        y += U(27);
        UiKit.Meter(new Rect(x, y, w, Mathf.Max(2f, Mathf.Round(U(3f)))), frac);
        y += U(8);
        if (!string.IsNullOrEmpty(o.xpDetail)) UiKit.Text(new Rect(x, y, w, U(14)), o.xpDetail, UiKit.Body, UiKit.Fit(o.xpDetail, UiKit.Body, 9f, w, 6.5f), UiKit.InkSub);
        y += U(17);
        UiKit.Hair(x, x + w, y, UiKit.PaperTrack);
        y += U(7);

        // ---- safety rating
        UiKit.Kicker(new Rect(x, y, w, U(14)), "Safety rating", 8.5f);
        if (o.gradeAfter != o.gradeBefore)
        {
            bool better = o.gradeAfter < o.gradeBefore;
            UiKit.Text(new Rect(x, y, w, U(14)), better ? "Grade up" : "Grade down", UiKit.Heavy, 9f, better ? UiKit.GoodInk : UiKit.BadInk, TextAnchor.MiddleRight);
        }
        y += U(16);
        Color gc = UiKit.PaperGrade(o.gradeAfter);
        UiKit.Text(new Rect(x, y, U(28), U(28)), Progression.Grades[o.gradeAfter], UiKit.Heavy, 24f, gc);
        float nx = x + U(32), bw = UiKit.Width(o.srBeforeStr, UiKit.Body, 12f);
        UiKit.Text(new Rect(nx, y, bw + U(2), U(28)), o.srBeforeStr, UiKit.Body, 12f, UiKit.InkSub);
        UiKit.Icon(new Rect(nx + bw + U(5), y + U(9), U(10), U(10)), UiKit.IconNext, UiKit.InkSub);
        UiKit.Text(new Rect(nx + bw + U(19), y, U(44), U(28)), o.srAfterStr, UiKit.Heavy, 16f, UiKit.Ink);
        Color dc = o.srDelta > 0 ? UiKit.GoodInk : o.srDelta < 0 ? UiKit.BadInk : UiKit.Ink;
        float dw = UiKit.StampWidth(o.srDeltaStr, 9f);
        UiKit.Stamp(new Rect(x + w - dw, y + U(5), dw, U(18)), o.srDeltaStr, dc, Color.white, 9f);
        y += U(30);
        // why: up to three lines, the biggest first
        if (o.reasons == 0 && y + U(14) <= bottom) UiKit.Text(new Rect(x, y, w, U(14)), "No change", UiKit.Body, 9.5f, UiKit.InkSub);
        for (int i = 0; i < o.reasons && y + U(14) <= bottom; i++)
        {
            UiKit.Text(new Rect(x, y, w, U(14)), o.reason[i], UiKit.Body, 9.5f, UiKit.InkSub);
            UiKit.Text(new Rect(x, y, w, U(14)), o.reasonVal[i], UiKit.Heavy, 9.5f, o.reasonGood[i] ? UiKit.GoodInk : UiKit.BadInk, TextAnchor.MiddleRight);
            y += U(15);
        }
        UiKit.PopAlpha();
    }
}
