// MenuProgress.cs — the driver's progress on the results page (MenuUI.DrawResults, right of the timing table, past a
// quiet rule), in the "Paper" look (UiKit.cs), from MenuProfile.Last (MenuDriver.cs; rules in Progression.cs):
//  * EXPERIENCE: "+410 XP" (ExtraBold ink), the level line (ink on its track) filling from the old XP to the new
//    (0.6 s after the page opens, 1.4 s), a "LEVEL UP" tag popping in when it passes a level, the biggest three XP
//    sources under it
//  * SAFETY RATING: the grade letter (green S A B, ink C, amber D E), old -> new score, the change as a tag (white on
//    green up, white on amber down, ink when unchanged), up to three reasons with their values (green good, amber bad)
// Every string is built once when the race is recorded; the line and the level shown are the only things animated.
// The progress REVEAL (after the post-race sequence, PostRace.cs: OpenResults(true)): a full-screen Paper page before the
// results table. Left: the giant position, WINNER / PODIUM / IN THE POINTS, the championship points counting up (in a
// season). Middle: +XP counting up, the level line filling (a red flash, a burst and a LEVEL UP stamp when it passes a
// level, the level names switching), the XP sources one by one. Right: the safety grade (it flips with a pop, a GRADE UP
// / GRADE DOWN stamp, a shake when it drops), the score counting from the old to the new value, the reasons one by one.
// ~4.6 s; a tap finishes the animation, the next one (or Continue) opens the results table. Every string (count-up
// steps included) is built once when the reveal opens; the animation is a function of the reveal clock (shot mode can
// freeze it: RevealSeek).
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

    // ================================================================== data for the post-race sequence (PostRace.cs)
    internal int ResultCount => results != null ? results.Length : 0;
    internal Row ResultRow(int i) => results[i];
    internal string ResultGap(int i) => gaps != null && i >= 0 && i < gaps.Length ? gaps[i] : null;
    internal string ResultTime => resTimeStr;

    // ================================================================== the progress reveal
    bool reveal; float revealAt, revealFixed = -1f;
    const float RevealDone = 4.6f, RevealShotOff = 30f;
    string rvWord, rvOf; string[] rvXp, rvSr, rvPts; int rvSrMin, rvPtsN; float rvCross = -1f, rvXpW, rvSlashX;
    readonly string[] rvSrcLab = new string[3], rvSrcVal = new string[3]; int rvSrcN;
    const float RvFlip = 2.95f;

    // the results page; with the reveal first when there is a race outcome to show
    public void OpenResults(bool withReveal)
    {
        page = Page.Results; navDir = 1; GUIUtility.keyboardControl = 0;
        reveal = withReveal && MenuProfile.Last != null && results != null && results.Length > 0;
        revealAt = Time.unscaledTime; revealFixed = -1f;
        if (reveal) PrepareReveal();
    }
    // shot mode: the reveal frozen s seconds in (from RevealShotOff on: the results table)
    public void RevealSeek(float s)
    {
        if (!reveal) return;
        if (s >= RevealShotOff) { reveal = false; return; }
        revealFixed = Mathf.Max(0f, s);
    }

    void PrepareReveal()
    {
        var o = MenuProfile.Last;
        int n = results.Length;
        rvWord = n < 2 ? "Finished" : resPos == 1 ? "Winner" : resPos <= 3 ? "Podium" : resPos <= 10 ? "In the points" : "Finished";
        rvWord = UiKit.Caps(rvWord);
        rvOf = (n > 1 ? "of " + n : "Time trial") + "  \u00b7  " + resTimeStr;
        // XP: 32 steps of the count-up; when the line passes into the next level (the reveal clock)
        rvXp = new string[33];
        for (int i = 0; i <= 32; i++) rvXp[i] = "+" + Mathf.RoundToInt(o.xpGain * i / 32f);
        rvXpW = UiKit.Width(rvXp[32], UiKit.HeavyItalic, 44f);
        rvCross = -1f;
        if (o.levelAfter > o.levelBefore && o.xpAfter > o.xpBefore)
        {
            float q = (Progression.StartOf(o.levelBefore + 1) - o.xpBefore) / (float)(o.xpAfter - o.xpBefore), lo = 0f, hi = 1f;
            for (int k = 0; k < 24; k++) { float mid = (lo + hi) * 0.5f; if (UiKit.EaseInOut(mid) < q) lo = mid; else hi = mid; }
            rvCross = 0.7f + 1.6f * (lo + hi) * 0.5f;
        }
        // the sources: "Position +250  ·  Finish +100" -> label / value
        rvSrcN = 0;
        if (!string.IsNullOrEmpty(o.xpDetail))
            foreach (var part in o.xpDetail.Split(new[] { "  \u00b7  " }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                if (rvSrcN >= 3) break;
                int at = part.LastIndexOf(" +");
                rvSrcLab[rvSrcN] = at > 0 ? part.Substring(0, at) : part; rvSrcVal[rvSrcN] = at > 0 ? part.Substring(at + 1) + " XP" : ""; rvSrcN++;
            }
        // safety: every whole score from the old to the new one
        int a = Mathf.RoundToInt(o.srBefore), b = Mathf.RoundToInt(o.srAfter);
        rvSrMin = Mathf.Min(a, b); rvSr = new string[Mathf.Abs(b - a) + 1];
        for (int i = 0; i < rvSr.Length; i++) rvSr[i] = (rvSrMin + i).ToString();
        rvSlashX = UiKit.Width("100", UiKit.Heavy, 26f);
        // championship points (a season round): count up to the number in the "+18 PTS" tag
        if (!ReferenceEquals(noteParsed, resultNote)) ParseNote();
        rvPtsN = 0; rvPts = null;
        if (notePts != null)
        {
            int v = 0; foreach (char ch in notePts) { if (ch >= '0' && ch <= '9') v = v * 10 + (ch - '0'); else if (v > 0) break; }
            rvPtsN = Mathf.Clamp(v, 0, 200); rvPts = new string[rvPtsN + 1];
            for (int i = 0; i <= rvPtsN; i++) rvPts[i] = "+" + i + " PTS";
        }
    }

    static float In(float rc, float at, float dur = 0.35f) => UiKit.EaseOut((rc - at) / dur);

    Choice DrawReveal()
    {
        var o = MenuProfile.Last;
        if (o == null || rvXp == null) { reveal = false; return Choice.None; }
        float rc = revealFixed >= 0f ? revealFixed : Time.unscaledTime - revealAt;
        UiKit.PaperPage();
        var f = UiKit.PaperFrame(72f);
        UiKit.Masthead(f, UiKit.NavNone, false, resHead);
        float x0 = f.x0, W = f.w, top = f.top, bot = f.foot - U(10);
        float colL = x0, wL = W * 0.27f, colM = x0 + W * 0.31f, wM = W * 0.34f, colR = x0 + W * 0.70f, wR = W * 0.30f;
        UiKit.PushAlpha(In(rc, 0.3f, 0.5f));
        UiKit.VHair(colM - W * 0.02f, top, bot, UiKit.PaperTrack); UiKit.VHair(colR - W * 0.02f, top, bot, UiKit.PaperTrack);
        UiKit.PopAlpha();

        // ---- left: the position, the word, the championship
        float e = In(rc, 0f, 0.45f);
        UiKit.PushAlpha(e);
        UiKit.BigNumber(UiKit.Rise(new Rect(colL - U(3), top - U(8), wL + U(40), U(92)), e, 18f), resPosStr, 96f, UiKit.Red);
        UiKit.PopAlpha();
        e = In(rc, 0.15f);
        UiKit.PushAlpha(e);
        UiKit.Text(UiKit.Rise(new Rect(colL, top + U(84), wL, U(24)), e, 10f), rvWord, UiKit.Heavy, UiKit.Fit(rvWord, UiKit.Heavy, 17f, wL, 10f), UiKit.Ink);
        UiKit.Text(UiKit.Rise(new Rect(colL, top + U(107), wL, U(18)), e, 10f), rvOf, UiKit.Body, UiKit.Fit(rvOf, UiKit.Body, 11f, wL, 7f), UiKit.InkSub);
        UiKit.PopAlpha();
        if (rvPts != null)
        {
            float ec = In(rc, 3.3f);
            UiKit.PushAlpha(ec);
            float cy = top + U(140);
            UiKit.Kicker(new Rect(colL, cy, wL, U(14)), "Championship", 8.5f);
            int pi = Mathf.Clamp(Mathf.RoundToInt(UiKit.EaseOut((rc - 3.4f) / 0.9f) * rvPtsN), 0, rvPtsN);
            float pw = UiKit.StampWidth(rvPts[rvPtsN], 13f);
            var pr = UiKit.Scale(new Rect(colL, cy + U(17), pw, U(24)), 1f + 0.18f * (1f - UiKit.EaseOut((rc - 4.3f) / 0.3f)) * (rc >= 4.3f ? 1f : 0f));
            UiKit.Stamp(pr, rvPts[pi], UiKit.Ink, Color.white, 13f);
            if (noteChamp != null) UiKit.Text(new Rect(colL, cy + U(45), wL, U(16)), noteChamp, UiKit.Body, UiKit.Fit(noteChamp, UiKit.Body, 10f, wL, 7f), UiKit.InkSub);
            UiKit.PopAlpha();
        }

        // ---- middle: experience
        e = In(rc, 0.35f);
        UiKit.PushAlpha(e);
        float my = top;
        float kb = UiKit.EaseInOut((rc - 0.7f) / 1.6f);
        float xpShown = o.xpBefore + (o.xpAfter - o.xpBefore) * kb;
        int lvl = Progression.LevelOf((int)xpShown); float frac = Progression.FracOf(xpShown);
        bool crossed = rvCross >= 0f && rc >= rvCross;
        float sinceCross = crossed ? rc - rvCross : -1f;
        UiKit.Kicker(new Rect(colM, my, wM, U(14)), "Experience", 8.5f);
        float lp = crossed ? 1f + 0.35f * (1f - UiKit.EaseOut(sinceCross / 0.35f)) : 1f;
        var lr = UiKit.Scale(new Rect(colM + wM - U(80), my, U(80), U(14)), lp);
        UiKit.Text(lr, Progression.LevelNames[lvl], UiKit.Heavy, 10.5f, crossed ? UiKit.Red : UiKit.Ink, TextAnchor.MiddleRight);
        // +XP counting up, "XP" after it
        int xi = Mathf.Clamp(Mathf.RoundToInt(UiKit.EaseOut((rc - 0.5f) / 1.3f) * 32f), 0, 32);
        UiKit.Text(new Rect(colM - U(4), my + U(12), rvXpW + U(4), U(52)), rvXp[xi], UiKit.HeavyItalic, 44f, UiKit.Ink, TextAnchor.MiddleRight);
        UiKit.Text(new Rect(colM + rvXpW + U(6), my + U(25), U(40), U(30)), "XP", UiKit.Heavy, 15f, UiKit.Ink);
        // the level line
        float by = my + U(72), bh = Mathf.Max(3f, Mathf.Round(U(6f)));
        UiKit.Rect(new Rect(colM, by, wM, bh), UiKit.PaperTrack);
        bool flash = crossed && sinceCross < 0.45f;
        if (frac > 0.001f) UiKit.Rect(new Rect(colM, by, Mathf.Max(bh, wM * Mathf.Clamp01(frac)), bh), flash ? UiKit.Red : UiKit.Ink);
        // the moving end of the line glows while it fills
        if (kb > 0.001f && kb < 0.999f) UiKit.Rect(new Rect(colM + wM * frac - U(2), by - U(2), U(4), bh + U(4)), UiKit.A(UiKit.Red, 0.85f), 2f);
        UiKit.Text(new Rect(colM, by + bh + U(3), wM * 0.5f, U(14)), Progression.LevelNames[lvl], UiKit.Body, 9f, UiKit.InkSub);
        if (lvl < Progression.MaxLevel) UiKit.Text(new Rect(colM + wM * 0.5f, by + bh + U(3), wM * 0.5f, U(14)), Progression.LevelNames[lvl + 1], UiKit.Body, 9f, UiKit.InkSub, TextAnchor.MiddleRight);
        if (crossed)
        {
            float tw = UiKit.StampWidth("LEVEL UP", 10f);
            var c = new Vector2(colM + wM - tw * 0.5f, by - U(15));
            Burst(c, sinceCross / 0.8f, UiKit.Red);
            var tag = UiKit.Scale(new Rect(c.x - tw * 0.5f, c.y - U(9), tw, U(18)), 1f + 0.55f * (1f - UiKit.EaseOut(sinceCross / 0.35f)));
            UiKit.Stamp(tag, "LEVEL UP", UiKit.Red, Color.white, 10f);
        }
        UiKit.PopAlpha();
        // where it came from, one line at a time
        float sy = by + bh + U(24);
        for (int i = 0; i < rvSrcN; i++)
        {
            float es = In(rc, 0.9f + 0.2f * i);
            if (es <= 0.001f) break;
            UiKit.PushAlpha(es);
            var r = UiKit.Slide(new Rect(colM, sy, wM, U(16)), es, -10f);
            UiKit.Text(r, rvSrcLab[i], UiKit.Body, 10.5f, UiKit.InkSub);
            UiKit.Text(r, rvSrcVal[i], UiKit.Heavy, 10.5f, UiKit.Ink, TextAnchor.MiddleRight);
            UiKit.Hair(colM, colM + wM, sy + U(16), UiKit.PaperTrack);
            UiKit.PopAlpha();
            sy += U(18);
        }
        if (kb >= 0.999f) { UiKit.PushAlpha(In(rc, 2.35f)); UiKit.Text(new Rect(colM, sy + U(2), wM, U(14)), o.xpAfterStr, UiKit.Body, 9f, UiKit.InkMuted); UiKit.PopAlpha(); }

        // ---- right: the safety rating
        e = In(rc, 2.2f);
        if (e > 0.001f)
        {
            UiKit.PushAlpha(e);
            float ry = top;
            bool flipped = rc >= RvFlip, changed = o.gradeAfter != o.gradeBefore, better = o.gradeAfter < o.gradeBefore;
            float sinceFlip = rc - RvFlip;
            UiKit.Kicker(new Rect(colR, ry, wR, U(14)), "Safety rating", 8.5f);
            if (flipped && changed)
            {
                string gt = better ? "GRADE UP" : "GRADE DOWN"; float gw = UiKit.StampWidth(gt, 8.5f);
                var gr = UiKit.Scale(new Rect(colR + wR - gw, ry - U(1), gw, U(16)), 1f + 0.4f * (1f - UiKit.EaseOut(sinceFlip / 0.3f)));
                UiKit.Stamp(gr, gt, better ? UiKit.GoodInk : UiKit.BadInk, Color.white, 8.5f);
            }
            int gShown = flipped ? o.gradeAfter : o.gradeBefore;
            float pop = flipped && changed ? 1f + 0.45f * (1f - UiKit.EaseOut(sinceFlip / 0.35f)) : 1f;
            float shake = flipped && changed && !better ? Mathf.Sin(sinceFlip * 55f) * U(4f) * Mathf.Clamp01(1f - sinceFlip / 0.45f) : 0f;
            var gl = UiKit.Scale(new Rect(colR + shake, ry + U(14), U(46), U(56)), pop);
            UiKit.Text(gl, Progression.Grades[gShown], UiKit.Heavy, 50f, UiKit.PaperGrade(gShown), TextAnchor.MiddleCenter);
            if (flipped && changed && better) Burst(gl.center, sinceFlip / 0.7f, UiKit.GoodInk);
            // the score counting
            float sk = UiKit.EaseInOut((rc - 2.5f) / 0.9f);
            int sv = Mathf.RoundToInt(Mathf.Lerp(o.srBefore, o.srAfter, sk)); int si = Mathf.Clamp(sv - rvSrMin, 0, rvSr.Length - 1);
            float sx = colR + U(56);
            UiKit.Text(new Rect(sx, ry + U(16), rvSlashX + U(4), U(32)), rvSr[si], UiKit.Heavy, 26f, UiKit.Ink, TextAnchor.MiddleRight);
            UiKit.Text(new Rect(sx + rvSlashX + U(8), ry + U(22), U(50), U(24)), "/ 100", UiKit.Body, 10f, UiKit.InkSub);
            if (sk >= 0.999f)
            {
                Color dc = o.srDelta > 0 ? UiKit.GoodInk : o.srDelta < 0 ? UiKit.BadInk : UiKit.Ink;
                float dw = UiKit.StampWidth(o.srDeltaStr, 9f);
                UiKit.Stamp(UiKit.Scale(new Rect(sx, ry + U(50), dw, U(17)), 1f + 0.3f * (1f - UiKit.EaseOut((rc - 3.4f) / 0.25f))), o.srDeltaStr, dc, Color.white, 9f);
            }
            // why
            float ry2 = ry + U(80);
            if (o.reasons == 0) { UiKit.PushAlpha(In(rc, 3.0f)); UiKit.Text(new Rect(colR, ry2, wR, U(16)), "No change", UiKit.Body, 10f, UiKit.InkSub); UiKit.PopAlpha(); }
            for (int i = 0; i < o.reasons; i++)
            {
                float er = In(rc, 3.0f + 0.2f * i);
                if (er <= 0.001f) break;
                UiKit.PushAlpha(er);
                var r = UiKit.Slide(new Rect(colR, ry2, wR, U(16)), er, -10f);
                UiKit.Text(r, o.reason[i], UiKit.Body, 10f, UiKit.InkSub);
                UiKit.Text(r, o.reasonVal[i], UiKit.Heavy, 10f, o.reasonGood[i] ? UiKit.GoodInk : UiKit.BadInk, TextAnchor.MiddleRight);
                UiKit.Hair(colR, colR + wR, ry2 + U(16), UiKit.PaperTrack);
                UiKit.PopAlpha();
                ry2 += U(18);
            }
            UiKit.PopAlpha();
        }

        // ---- the footer: a hint, Continue
        Choice ch = Choice.None; bool go = false;
        float ef = In(rc, 0.8f);
        UiKit.PushAlpha(ef);
        UiKit.Hair(x0, f.x1, f.foot);
        UiKit.Kicker(new Rect(x0, f.actY - U(10), W * 0.5f, U(20)), rc < RevealDone ? "Tap to skip" : "Tap to continue", 9f);
        string prim = "Continue"; float bw = UiKit.BlackButtonWidth(prim);
        if (UiKit.BlackButton(new Rect(f.x1 - bw, f.actY - U(24), bw, U(48)), prim)) go = true;
        UiKit.PopAlpha();
        // a tap anywhere: the animation to its end, then on to the table
        if (!go && UiKit.Press(new Rect(0, 0, Screen.width, Screen.height), true, out _))
        {
            if (rc < RevealDone) { if (revealFixed >= 0f) revealFixed = RevealDone; else revealAt = Time.unscaledTime - RevealDone; }
            else go = true;
        }
        if (go) { reveal = false; GUIUtility.hotControl = 0; }
        return ch;
    }

    // a burst of rays and a ring round c (p: 0..1 through it)
    static void Burst(Vector2 c, float p, Color col)
    {
        if (p <= 0f || p >= 1f) return;
        float a = 1f - p, k = UiKit.EaseOut(p), r0 = U(12) + U(30) * k, len = U(12) * (1f - p) + U(2);
        var m = GUI.matrix;
        for (int i = 0; i < 12; i++)
        {
            GUIUtility.RotateAroundPivot(30f, c);
            UiKit.Rect(new Rect(c.x + r0, c.y - U(1.2f), len, U(2.4f)), UiKit.A(col, a), 1f);
        }
        GUI.matrix = m;
        float rr = U(10) + U(36) * k;
        UiKit.Outline(new Rect(c.x - rr, c.y - rr, 2f * rr, 2f * rr), UiKit.A(col, a * 0.7f), 999f, 1.2f);
    }
}
