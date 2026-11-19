// MenuProgress.cs — the driver's progress on the results page (MenuUI.DrawResults, right of the timing table), from
// MenuProfile.Last (MenuDriver.cs; rules in Progression.cs):
//  * EXPERIENCE: "+410 XP" (cyan: progress), the level bar filling from the old XP to the new (0.6 s after the page
//    opens, 1.4 s), "LEVEL UP" popping in when it passes a level, the biggest three XP sources under it
//  * SAFETY RATING: grade badge, old -> new score, the change as a tag (green up, amber down), up to three reasons
// Every string is built once when the race is recorded; the bar and the level shown are the only things animated.
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
        UiKit.Panel(r, 14f);
        float pad = U(12), x = r.x + pad, w = r.width - 2 * pad, y = r.y + U(9), bottom = r.yMax - U(6);

        // ---- experience: the bar fills from the old total to the new one
        float k = UiKit.EaseInOut((UiKit.Opened(scr) - 0.6f) / 1.4f);
        float xpShown = o.xpBefore + (o.xpAfter - o.xpBefore) * k;
        int lvl = Progression.LevelOf((int)xpShown); float frac = Progression.FracOf(xpShown);
        bool up = lvl > o.levelBefore;
        if (up && !ReferenceEquals(lvlUpFor, o)) { lvlUpFor = o; lvlUpAt = Time.unscaledTime; }
        UiKit.Eyebrow(new Rect(x, y, w, U(14)), "Experience", UiKit.Faint, 8f);
        UiKit.Text(new Rect(x, y, w, U(14)), Progression.LevelNames[lvl], UiKit.Bold, 10f, up ? UiKit.Info : UiKit.Sub, TextAnchor.MiddleRight);
        y += U(15);
        UiKit.Text(new Rect(x, y, w, U(24)), o.xpGainStr, UiKit.Bold, 19f, UiKit.Info);
        if (up)
        {
            float p = Mathf.Clamp01((Time.unscaledTime - lvlUpAt) / 0.4f), tw = UiKit.TagWidth("LEVEL UP", 8.5f);
            var tag = UiKit.Scale(new Rect(x + w - tw, y + U(3), tw, U(18)), 1f + 0.3f * (1f - UiKit.EaseOut(p)));
            UiKit.Tag(tag, "LEVEL UP", UiKit.Info, UiKit.Base, 8.5f);
        }
        else if (k >= 1f) UiKit.Text(new Rect(x, y, w, U(24)), o.xpAfterStr, UiKit.Regular, 8.5f, UiKit.Faint, TextAnchor.MiddleRight);
        y += U(25);
        var bar = new Rect(x, y, w, Mathf.Max(3f, U(5)));
        UiKit.Rect(bar, new Color(1, 1, 1, 0.08f), 3f);
        if (frac > 0.001f) UiKit.Rect(new Rect(bar.x, bar.y, Mathf.Max(bar.height, bar.width * frac), bar.height), UiKit.Info, 3f);
        y += U(10);
        if (!string.IsNullOrEmpty(o.xpDetail)) UiKit.Text(new Rect(x, y, w, U(13)), o.xpDetail, UiKit.Regular, UiKit.Fit(o.xpDetail, UiKit.Regular, 8.5f, w, 6.5f), UiKit.Sub);
        y += U(15);
        UiKit.Rect(new Rect(x, y, w, Mathf.Max(1f, U(0.6f))), UiKit.Line);
        y += U(6);

        // ---- safety rating
        UiKit.Eyebrow(new Rect(x, y, w, U(14)), "Safety rating", UiKit.Faint, 8f);
        if (o.gradeAfter != o.gradeBefore)
            UiKit.Text(new Rect(x, y, w, U(14)), o.gradeAfter < o.gradeBefore ? "Grade up" : "Grade down", UiKit.Bold, 8.5f, o.gradeAfter < o.gradeBefore ? UiKit.Good : UiKit.Warn, TextAnchor.MiddleRight);
        y += U(15);
        Color gc = Progression.GradeColor(o.gradeAfter);
        var badge = new Rect(x, y, U(26), U(26));
        UiKit.Rect(badge, new Color(1, 1, 1, 0.06f), 7f); UiKit.Outline(badge, gc, 7f, 1f);
        UiKit.Text(badge, Progression.Grades[o.gradeAfter], UiKit.Bold, 13f, gc, TextAnchor.MiddleCenter);
        float nx = badge.xMax + U(10), bw = UiKit.Width(o.srBeforeStr, UiKit.Semi, 12f);
        UiKit.Text(new Rect(nx, y, bw + U(2), U(26)), o.srBeforeStr, UiKit.Semi, 12f, UiKit.Sub);
        UiKit.Icon(new Rect(nx + bw + U(5), y + U(8), U(10), U(10)), UiKit.IconNext, UiKit.Faint);
        UiKit.Text(new Rect(nx + bw + U(19), y, U(44), U(26)), o.srAfterStr, UiKit.Bold, 15f, UiKit.TextCol);
        Color dc = o.srDelta > 0 ? UiKit.Good : o.srDelta < 0 ? UiKit.Warn : UiKit.Faint;
        float dw = UiKit.TagWidth(o.srDeltaStr, 9f);
        UiKit.Tag(new Rect(x + w - dw, y + U(4), dw, U(18)), o.srDeltaStr, UiKit.A(dc, 0.16f), dc, 9f);
        y += U(29);
        // why: up to three lines, the biggest first
        if (o.reasons == 0 && y + U(13) <= bottom) UiKit.Text(new Rect(x, y, w, U(13)), "No change", UiKit.Regular, 9f, UiKit.Faint);
        for (int i = 0; i < o.reasons && y + U(13) <= bottom; i++)
        {
            UiKit.Text(new Rect(x, y, w, U(13)), o.reason[i], UiKit.Regular, 9f, UiKit.Sub);
            UiKit.Text(new Rect(x, y, w, U(13)), o.reasonVal[i], UiKit.Bold, 9f, o.reasonGood[i] ? UiKit.Good : UiKit.Warn, TextAnchor.MiddleRight);
            y += U(13);
        }
        UiKit.PopAlpha();
    }
}
