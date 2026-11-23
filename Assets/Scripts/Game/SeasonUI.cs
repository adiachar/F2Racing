// SeasonUI.cs — the career pages and the qualifying results, drawn with IMGUI through MenuUI in the "Paper" look
// (UiKit.cs: paper page, masthead, ink rules, text links left and the black button right under the bottom rule):
//  * Story cards (Story.cs): full-screen, one at a time: a chapter (the giant red round number, big type, 1-3 lines),
//    a message (Mia, Lena) or a quote (Raske) beside a portrait silhouette, a memory of Teo (faded, his helmet), the
//    weekend's goals (boxes; after the race ticked). Tap anywhere or "Continue"; "Skip" ends the run of cards.
//  * Career hub (Home's RACE / Continue career, the results' "Next round"): career · season · tier · difficulty, the
//    round as the title, your standing on the right (position as the red italic figure, points, team, the gap to Raske),
//    the 10-round calendar strip (done rounds with your result, the next one red), then three columns: this round's
//    goals and the season's goals with boxes, the drivers' top five + you, the teams' top three + yours + the rival
//    team. The black button: Qualifying / Race (the grid is set) / Next season. Links: Back, Practice, Skip qualifying
//    (= start from the back), Difficulty (tap: the next preset), Setup (laps, weather). "All" opens the full tables
//    (drivers / teams / calendar) with "New career" (asks twice).
//  * Contract offers (a new career, after the prologue): the midfield teams as ruled cells and the difficulty presets
//    (Easy ... Master, Custom) with what each sets; "Sign" starts the career (Season.New) and its welcome.
//  * Qualifying results: your position as the giant red italic figure, your best and pole, the classification with
//    gaps, the black "Start race" (grid = this order) or the "Menu" link. In a career the grid is saved at once.
// Tables and strings are rebuilt only when the career changes (a session, a new season, the difficulty), not per frame.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public sealed class SeasonUI
{
    readonly MenuUI m;
    public Qualifying lastQuali;
    int tab;                 // full tables: 0 drivers, 1 teams, 2 calendar
    bool picking, confirmNew, full; int pickTeam, pickDiff;
    static readonly string[] TabNames = { "Drivers", "Teams", "Calendar" };
    List<StoryCard> cards; int cardIdx;

    public SeasonUI(MenuUI menu) { m = menu; }

    public void ShowQuali(Qualifying q)
    {
        lastQuali = q;
        var l = q.Classified(); // once per session: a pole counts in the driver's career stats (MenuDriver.cs)
        if (l.Count > 1 && l[0].Player && !double.IsNaN(l[0].best)) MenuProfile.RecordPole();
        // a career round: the grid is saved now (leaving from here keeps it; no second go at qualifying)
        if (m.mode == MenuUI.ModeChampionship && Season.Active && !Season.Over && Season.Data.stage == Season.StageQuali)
        {
            var ids = new List<int>(l.Count); int pos = 0;
            for (int i = 0; i < l.Count; i++)
            {
                int id = l[i].Player ? 0 : Season.IdOf(l[i].who);
                if (id < 0) continue;
                if (id == 0 && !double.IsNaN(l[i].best)) pos = ids.Count + 1;
                ids.Add(id);
            }
            Season.SetGrid(ids.ToArray(), pos);
        }
    }

    // the career: Home, Play, the results' "Next round". Settles a session left behind, then the story due, then the hub.
    public void Open()
    {
        if (!Season.Active) Season.Load();
        m.mode = MenuUI.ModeChampionship; PlayerPrefs.SetInt("gameMode", m.mode);
        confirmNew = false; full = false;
        m.page = MenuUI.Page.Season;
        if (!Season.Active) { StartNew(); return; }
        picking = false; pickTeam = Season.Data.team;
        BuildStory();
    }

    // Android back / Escape on the career page
    public void Back()
    {
        if (cards != null) { EndCards(); return; }
        if (picking && Season.Active) { picking = false; return; }
        if (full) { full = false; return; }
        m.GoHome();
    }

    void StartNew()
    {
        Season.Data = null; // the old save stays on disk until "Sign" (Back = it comes back on the next Open)
        cards = Story.Prologue(); cardIdx = 0; picking = true; confirmNew = false; full = false;
        pickTeam = -1; for (int t = 0; t < Season.Teams.Count && pickTeam < 0; t++) if (Season.Midfield(t)) pickTeam = t;
        pickDiff = Difficulty.Current; // Custom stays Custom: nothing the player set changes unless he picks a preset
    }

    void BuildStory()
    {
        var d = Season.Data; var l = new List<StoryCard>();
        Season.ResolvePending(); // qualifying left = no time; a race left on Hard+ = retired (Season.cs)
        if (!d.prologue) { l.AddRange(Story.Prologue()); l.AddRange(Story.Welcome()); d.prologue = true; }
        if (d.storyPost < d.results.Count) { l.AddRange(Story.PostRound(d.results.Count - 1)); d.storyPost = d.results.Count; }
        if (Season.Over) { if (!d.endShown) { l.AddRange(Story.SeasonEnd()); d.endShown = true; } }
        else if (d.storyPre < d.round) { l.AddRange(Story.PreRound()); d.storyPre = d.round; }
        else if (d.goalsFor != d.round) Career.FreezeGoals();
        Season.Save();
        cards = l.Count > 0 ? l : null; cardIdx = 0; cacheKey = int.MinValue;
    }
    void EndCards() { cards = null; cardIdx = 0; cacheKey = int.MinValue; }

    static float U(float v) => UiKit.U(v);

    // ---------------------------------------------------------------- shared page parts
    // page frame: the paper page and the masthead (nav: the PLAY item underlined; false: the wordmark and a caption)
    UiKit.Frame Page(bool nav, string caption = null)
    {
        UiKit.PaperPage();
        if (nav) return m.Masthead(UiKit.NavPlay, 72f);
        var f = UiKit.PaperFrame(72f);
        UiKit.Masthead(f, UiKit.NavNone, false, caption);
        return f;
    }

    // kicker, big title (ExtraBold), one line under it
    static void Header(string scr, UiKit.Frame f, float w, string eyebrow, string title, string sub)
    {
        float e0 = UiKit.Enter(scr, 0), e1 = UiKit.Enter(scr, 1), e2 = UiKit.Enter(scr, 2);
        UiKit.PushAlpha(e0); UiKit.Kicker(new Rect(f.x0, f.top - U(4), w, U(14)), eyebrow, UiKit.Fit(UiKit.Caps(eyebrow), UiKit.Body, 10f, w, 7f)); UiKit.PopAlpha();
        UiKit.PushAlpha(e1);
        UiKit.Text(UiKit.Rise(new Rect(f.x0 - U(1), f.top + U(8), w, U(30)), e1, 10f), title, UiKit.Heavy, UiKit.Fit(title, UiKit.Heavy, 24f, w, 16f), UiKit.Ink);
        UiKit.PopAlpha();
        if (sub != null) { UiKit.PushAlpha(e2); UiKit.Text(new Rect(f.x0, f.top + U(37), w, U(16)), sub, UiKit.Body, UiKit.Fit(sub, UiKit.Body, 11f, w, 8f), UiKit.InkSub); UiKit.PopAlpha(); }
    }

    // the action row: the rule above it, the black button on the right; returns true when it is tapped
    static bool Primary(UiKit.Frame f, string label)
    {
        UiKit.Hair(f.x0, f.x1, f.foot);
        float w = Mathf.Max(U(170), UiKit.BlackButtonWidth(label));
        return UiKit.BlackButton(new Rect(f.x1 - w, f.actY - U(24), w, U(48)), label);
    }
    // a link in the action row at x; returns true when tapped, x moves on past it
    static bool LinkAt(UiKit.Frame f, ref float x, string title, string sub, bool armed = false)
    {
        float w = UiKit.LinkWidth(title, sub) + U(8);
        bool hit = UiKit.Link(new Rect(x, f.actY - U(22), w, U(44)), title, sub, true, 13f, TextAnchor.MiddleLeft, armed);
        x += w + U(20);
        return hit;
    }

    static void Head(Rect r, string s, TextAnchor a = TextAnchor.MiddleLeft) => UiKit.Kicker(r, s, 8.5f, a);
    // the ink rule under a table's column heads
    static void HeadRule(float x0, float x1, float y) => UiKit.Hair(x0, x1, y, UiKit.Ink);

    // a goal's box: filled ink = met, filled grey = on course, outline = open, outline + slash = missed
    static void Box(Rect r, int state)
    {
        if (state == Career.Done) UiKit.Rect(r, UiKit.Ink);
        else if (state == Career.OnCourse) UiKit.Rect(r, UiKit.PaperTrack);
        else UiKit.Outline(r, state == Career.Failed ? UiKit.InkMuted : UiKit.Ink, 0f, 1f);
        if (state == Career.Done)
        {
            float k = r.width * 0.3f; UiKit.Rect(new Rect(r.center.x - k * 0.5f, r.center.y - k * 0.5f, k, k), UiKit.Red);
        }
        else if (state == Career.Failed) UiKit.Rect(new Rect(r.x + r.width * 0.2f, r.center.y - UiKit.HairPx * 0.5f, r.width * 0.6f, UiKit.HairPx), UiKit.InkMuted);
    }

    // ================================================================ story cards
    static readonly string[] CardKeys = MakeKeys(12);
    static string[] MakeKeys(int n) { var a = new string[n]; for (int i = 0; i < n; i++) a[i] = "card" + i; return a; }
    int sizeKey = -1; float titleSize, bodySize;
    string cardCaption; int cardCaptionFor = -1;

    void DrawCard()
    {
        var c = cards[Mathf.Clamp(cardIdx, 0, cards.Count - 1)];
        string scr = CardKeys[cardIdx % CardKeys.Length];
        if (cardCaptionFor != cards.Count * 100 + cardIdx)
        {
            cardCaptionFor = cards.Count * 100 + cardIdx;
            cardCaption = Season.Active ? "Career  ·  season " + Season.Data.seasonNo + "  ·  " + Season.TierNames[Season.Tier] + " tier  ·  " + (cardIdx + 1) + " / " + cards.Count
                                        : "Career  ·  " + (cardIdx + 1) + " / " + cards.Count;
        }
        var f = Page(false, cardCaption);
        float e0 = UiKit.Enter(scr, 0, 0.06f, 0.45f), e1 = UiKit.Enter(scr, 1, 0.06f, 0.5f), e2 = UiKit.Enter(scr, 3, 0.06f, 0.5f);

        // a tap anywhere on the card: the next one
        var content = new Rect(f.safe.x, f.head + U(2), f.safe.width, f.foot - f.head - U(4));
        bool next = UiKit.Press(content, true, out float _);

        bool portrait = c.kind == StoryCard.Message || c.kind == StoryCard.Quote || c.kind == StoryCard.Memory;
        float pw = portrait ? Mathf.Min(U(150), f.w * 0.22f) : 0f;
        float x = f.x0, w = f.w - (portrait ? pw + U(40) : 0f);
        float top = f.top + U(18), bottom = f.foot - U(16);

        if (c.kind == StoryCard.Goals) DrawGoalsCard(c, f, scr);
        else
        {
            // the giant red figure (round number, final position) on chapter cards
            if (c.figure != null)
            {
                const float fs = 120f;
                float fw = UiKit.Width(c.figure, UiKit.HeavyItalic, fs);
                UiKit.PushAlpha(e0);
                UiKit.BigNumber(UiKit.Rise(new Rect(x - U(4), top - U(6), fw + U(10), U(130)), e0, 16f), c.figure, fs, UiKit.Red);
                UiKit.PopAlpha();
                x += fw + U(26); w -= fw + U(26);
            }
            // sizes once per card and screen: the title as big as fits (up to 3 lines)
            Font tf = c.kind == StoryCard.Quote || c.kind == StoryCard.Memory && c.title.StartsWith("“", System.StringComparison.Ordinal) ? UiKit.HeavyItalic : UiKit.Heavy;
            int sk = cardIdx * 100000 + cards.Count * 1000 + Screen.height + Mathf.RoundToInt(w);
            float maxTitleH = (bottom - top) * (c.body != null ? 0.62f : 0.85f);
            if (sk != sizeKey)
            {
                sizeKey = sk; titleSize = 32f;
                while (titleSize > 16f && UiKit.Height(c.title, tf, titleSize, w) > maxTitleH) titleSize -= 2f;
                bodySize = 14f;
            }
            float th = UiKit.Height(c.title, tf, titleSize, w), bh = c.body != null ? UiKit.Height(c.body, UiKit.Body, bodySize, w) : 0f;
            float total = U(22) + th + (c.body != null ? U(14) + bh : 0f);
            float y = Mathf.Max(top, (top + bottom) * 0.5f - total * 0.5f);
            UiKit.PushAlpha(e0);
            UiKit.Kicker(new Rect(x, y, w, U(14)), c.kicker, c.kind == StoryCard.Chapter ? UiKit.Red : UiKit.InkSub, UiKit.Fit(UiKit.Caps(c.kicker), UiKit.Body, 10f, w, 7f));
            UiKit.PopAlpha();
            y += U(22);
            UiKit.PushAlpha(e1);
            UiKit.Text(UiKit.Rise(new Rect(x, y, w, th + U(4)), e1, 12f), c.title, tf, titleSize, c.kind == StoryCard.Memory ? UiKit.InkSub : UiKit.Ink, TextAnchor.UpperLeft, true);
            UiKit.PopAlpha();
            y += th + U(14);
            if (c.body != null)
            {
                UiKit.PushAlpha(e2);
                UiKit.Text(new Rect(x, y, w, bh + U(4)), c.body, UiKit.Body, bodySize, UiKit.InkSub, TextAnchor.UpperLeft, true);
                UiKit.PopAlpha();
            }
            if (portrait)
            {
                float ph = Mathf.Min(bottom - top - U(20), pw * 1.25f);
                var pr = new Rect(f.x1 - pw, (top + bottom) * 0.5f - ph * 0.5f - U(6), pw, ph);
                UiKit.PushAlpha(e1);
                Portrait(UiKit.Slide(pr, e1, 14f), c.who);
                UiKit.PopAlpha();
            }
        }

        // the action row: Skip (all the cards), dots for the run, Continue
        UiKit.Hair(f.x0, f.x1, f.foot);
        bool last = cardIdx >= cards.Count - 1;
        string label = last ? (picking ? "Contract offers" : Season.Over ? "Standings" : "To the hub") : "Continue";
        float bw = Mathf.Max(U(170), UiKit.BlackButtonWidth(label));
        if (UiKit.BlackButton(new Rect(f.x1 - bw, f.actY - U(24), bw, U(48)), label)) next = true;
        float lx = f.x0;
        bool skip = !last && LinkAt(f, ref lx, "Skip", "All the story cards");
        // progress: one dot per card, the current one red
        int n = cards.Count; float dg = U(12), dx = (f.x0 + f.x1) * 0.5f - (n - 1) * dg * 0.5f;
        for (int i = 0; i < n; i++) UiKit.Dot(new Vector2(dx + i * dg, f.actY), i == cardIdx ? 3.2f : 2.4f, i == cardIdx ? UiKit.Red : i < cardIdx ? UiKit.Ink : UiKit.PaperTrack);
        if (skip) EndCards();
        else if (next) { if (last) EndCards(); else cardIdx++; }
    }

    // the weekend's goals, or the result with them ticked
    void DrawGoalsCard(StoryCard c, UiKit.Frame f, string scr)
    {
        float e0 = UiKit.Enter(scr, 0, 0.06f, 0.45f), e1 = UiKit.Enter(scr, 1, 0.06f, 0.5f);
        float x = f.x0, w = f.w, top = f.top + U(14);
        if (c.figure != null)
        {
            const float fs = 110f;
            float fw = UiKit.Width(c.figure, UiKit.HeavyItalic, fs);
            UiKit.PushAlpha(e0);
            UiKit.BigNumber(UiKit.Rise(new Rect(x - U(4), top - U(8), fw + U(10), U(120)), e0, 16f), c.figure, fs, UiKit.Red);
            UiKit.PopAlpha();
            x += fw + U(30); w -= fw + U(30);
        }
        UiKit.PushAlpha(e0);
        UiKit.Kicker(new Rect(x, top, w, U(14)), c.kicker, UiKit.Red, UiKit.Fit(UiKit.Caps(c.kicker), UiKit.Body, 10f, w, 7f));
        UiKit.Text(UiKit.Rise(new Rect(x, top + U(18), w, U(34)), e0, 10f), c.title, UiKit.Heavy, UiKit.Fit(c.title, UiKit.Heavy, 28f, w, 16f), UiKit.Ink);
        UiKit.PopAlpha();
        float y = top + U(66), rh = U(40);
        UiKit.Hair(x, x + w, y - U(6), UiKit.Ink);
        if (c.goals != null)
            for (int i = 0; i < c.goals.Length; i++)
            {
                var g = c.goals[i]; if (g == null) continue;
                float ei = UiKit.Enter(scr, 2 + i, 0.08f, 0.45f);
                UiKit.PushAlpha(ei);
                var r = UiKit.Slide(new Rect(x, y + i * rh, w, rh), ei, 12f);
                float bs = Mathf.Round(U(16));
                Box(new Rect(r.x, r.center.y - bs * 0.5f, bs, bs), c.judged ? (g.done ? Career.Done : Career.Failed) : Career.Open);
                UiKit.Text(new Rect(r.x + bs + U(14), r.y, r.width - bs - U(90), r.height), g.text, UiKit.Heavy, UiKit.Fit(g.text, UiKit.Heavy, 17f, r.width - bs - U(100), 11f), c.judged && !g.done ? UiKit.InkSub : UiKit.Ink);
                if (c.judged) UiKit.Text(r, g.done ? "MET" : "MISSED", UiKit.Heavy, 10f, g.done ? UiKit.GoodInk : UiKit.InkMuted, TextAnchor.MiddleRight);
                UiKit.Hair(r.x, r.xMax, r.yMax - UiKit.HairPx, UiKit.PaperTrack);
                UiKit.PopAlpha();
            }
        if (c.footer != null)
        {
            UiKit.PushAlpha(e1);
            UiKit.Kicker(new Rect(x, y + 2 * rh + U(10), w, U(16)), c.footer, 10f);
            UiKit.PopAlpha();
        }
    }

    // a portrait plate: a head-and-shoulders silhouette on a tint, the speaker's name under it. Teo is a memory:
    // grey, in his old helmet. Mia is small, with a red hair band. Raske wears Argent's colour at the collar.
    static void Portrait(Rect r, int who)
    {
        UiKit.Rect(r, UiKit.PaperTint);
        UiKit.Hair(r.x, r.xMax, r.y, UiKit.Ink);
        Color ink = who == StoryCard.Teo ? UiKit.InkMuted : UiKit.Ink;
        float s = who == StoryCard.Mia ? 0.78f : 1f, cx = r.center.x, baseY = r.yMax;
        float headR = r.width * 0.17f * s, headY = baseY - r.height * 0.52f * s;
        float sw = r.width * 0.74f * s, sh = r.height * 0.36f * s;
        // shoulders (taller than shown: the plate's bottom cuts them square)
        UiKit.Rect(new Rect(cx - sw * 0.5f, baseY - sh, sw, sh + sh * 0.6f), ink, sw * 0.42f / U(1));
        UiKit.Rect(new Rect(cx - headR * 0.45f, headY + headR * 0.6f, headR * 0.9f, baseY - sh - headY), ink); // neck
        if (who == StoryCard.Teo)
        {
            // the helmet: a bigger shell, the visor as a paper band, a red stripe over the top
            float hr = headR * 1.3f;
            UiKit.Rect(new Rect(cx - hr, headY - hr, 2 * hr, 2 * hr * 0.98f), ink, hr / U(1));
            UiKit.Rect(new Rect(cx - hr * 0.78f, headY - hr * 0.1f, hr * 1.5f, hr * 0.38f), UiKit.Paper, hr * 0.12f / U(1));
            UiKit.Rect(new Rect(cx - hr * 0.12f, headY - hr, hr * 0.24f, hr * 0.85f), UiKit.Red);
        }
        else
        {
            UiKit.Rect(new Rect(cx - headR, headY - headR, 2 * headR, 2 * headR), ink, headR / U(1));
            if (who == StoryCard.Mia) UiKit.Rect(new Rect(cx - headR * 0.95f, headY - headR * 0.62f, headR * 1.9f, Mathf.Max(2f, headR * 0.2f)), UiKit.Red, 1f);
        }
        // the collar: the team's colour
        Color col = who == StoryCard.Viktor || who == StoryCard.Nico ? Season.Colour(Season.RivalId)
                  : who == StoryCard.Boss && Season.Active ? Season.TeamColour(Season.Data.team) : UiKit.PaperTrack;
        if (who != StoryCard.Teo && who != StoryCard.Mia) UiKit.Rect(new Rect(cx - sw * 0.16f, baseY - sh, sw * 0.32f, Mathf.Max(2f, sh * 0.14f)), col);
        // cut the plate's bottom square; the name under it
        UiKit.Rect(new Rect(r.x - U(2), baseY, r.width + U(4), sh), UiKit.Paper);
        UiKit.Hair(r.x, r.xMax, baseY, UiKit.Ink);
        string nm = who == StoryCard.Teo ? Story.TeoName : who == StoryCard.Mia ? Story.MiaName : who == StoryCard.Boss ? Story.BossName : who == StoryCard.Viktor ? "Viktor Raske" : "Nico Raske";
        UiKit.Kicker(new Rect(r.x, baseY + U(6), r.width, U(14)), nm, 8.5f, TextAnchor.MiddleCenter);
    }

    // ================================================================ the hub
    int cacheKey = int.MinValue;
    int[] st, ts; int rank;
    string[] drvPts, drvMove, drvTeam, teamPts, teamDrivers, calGp, calCircuit, calRes, calCode, calMine;
    Color[] moveCol;
    string hubEyebrow, hubTitle, hubSub, statPos, statPts, statTeam, statGap, diffSub;
    readonly List<int> colDrivers = new List<int>(8), colTeams = new List<int>(6);
    string[] colDrvPos = new string[8], colTeamPos = new string[6];
    readonly string[] sgText = new string[Career.SeasonGoalCount]; readonly int[] sgState = new int[Career.SeasonGoalCount];
    readonly int[] rgState = new int[4];

    void Refresh()
    {
        var d = Season.Data;
        int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(d) ^ (d.round * 7919) ^ (d.results.Count * 104729) ^ (m.lapsIdx * 131) ^ (m.qualiIdx * 1031)
                ^ (d.team * 17) ^ (d.stage * 524287) ^ (Difficulty.Current * 37) ^ (d.seasonNo * 999331) ^ Mathf.RoundToInt(Difficulty.LastXp * 100f) * 7;
        if (key == cacheKey && st != null) return;
        cacheKey = key;
        bool over = Season.Over;
        st = Season.Standings(); ts = Season.TeamStandings(); rank = System.Array.IndexOf(st, 0) + 1;
        int n = st.Length;
        drvPts = new string[n]; drvMove = new string[n]; drvTeam = new string[n]; moveCol = new Color[n];
        for (int i = 0; i < n; i++)
        {
            int id = st[i];
            drvPts[i] = d.points[id].ToString();
            drvTeam[i] = Season.Teams[Season.TeamOf(id)];
            int prev = d.prevRank != null && id < d.prevRank.Length ? d.prevRank[id] : 0;
            if (prev > 0 && d.results.Count >= 2 && prev != i + 1)
            {
                bool up = prev > i + 1;
                drvMove[i] = up ? "+" + (prev - i - 1) : "-" + (i + 1 - prev);
                moveCol[i] = up ? UiKit.GoodInk : UiKit.InkSub; // gains good (green), drops quiet grey
            }
        }
        teamPts = new string[ts.Length]; teamDrivers = new string[ts.Length];
        for (int i = 0; i < ts.Length; i++)
        {
            int t = ts[i]; teamPts[i] = Season.TeamPoints(t).ToString();
            var sb = new System.Text.StringBuilder();
            for (int id = 0; id < Season.RosterCount; id++) if (Season.TeamOf(id) == t) { if (sb.Length > 0) sb.Append("   "); sb.Append(Season.Name(id)).Append("  ").Append(d.points[id]); }
            teamDrivers[i] = sb.ToString();
        }
        int R = Season.Rounds;
        calGp = new string[R]; calCircuit = new string[R]; calRes = new string[R]; calCode = new string[R]; calMine = new string[R];
        for (int r = 0; r < R; r++)
        {
            calGp[r] = Season.GpNames[r]; calCircuit[r] = Circuits.All[Season.CircuitIndex(r)].name; calCode[r] = (r + 1) + "  " + Season.GpCodes[r];
            if (r < d.results.Count)
            {
                int me = Season.FinishOf(r); bool dnf = d.results[r].dnf;
                calRes[r] = Season.Winner(r) + "  ·  you " + (dnf ? "DNF" : "P" + me);
                calMine[r] = dnf ? "DNF" : "P" + me;
            }
        }
        string tier = Season.TierNames[Season.Tier] + " tier";
        hubEyebrow = "Career  ·  season " + d.seasonNo + "  ·  " + tier + "  ·  " + Difficulty.Name;
        hubTitle = over ? "SEASON COMPLETE" : "ROUND " + (d.round + 1) + " OF " + R + "  ·  " + Season.GpNames[d.round].ToUpperInvariant();
        string grid = d.stage == Season.StageGrid || d.stage == Season.StageRace ? (d.qualiPos > 0 ? "on the grid P" + d.qualiPos : "starting from the back") : "qualifying " + MenuUI.QualiMinutes[m.qualiIdx] + " min";
        hubSub = over ? "Champion: " + Season.Name(st[0]) + "  ·  you P" + rank + (d.tier < Season.TierPremier ? (rank <= Season.PromoteTop ? "  ·  promoted" : "  ·  top " + Season.PromoteTop + " move up") : "")
                      : Circuits.All[Season.CircuitIndex(d.round)].name + "  ·  " + CarConfig.ClassNames[Season.TierClass] + "  ·  " + MenuUI.LapCounts[m.lapsIdx] + " laps  ·  " + grid;
        statPos = d.results.Count > 0 ? "P" + rank : "P—"; statPts = d.points[0] + " pts"; statTeam = Season.Teams[d.team];
        int gap = Career.RivalGap();
        statGap = Season.RivalName + " P" + Season.Rank(Season.RivalId) + "  ·  " + (gap > 0 ? "you +" + gap : gap < 0 ? "you –" + (-gap) : "level") + " pts";
        diffSub = Difficulty.Name + "  ·  " + Difficulty.XpLabel(Difficulty.Current == Difficulty.Custom ? Difficulty.LastXp : Difficulty.Xp[Difficulty.Current]);

        // the columns: drivers' top five + you, teams' top three + yours + the rival team
        colDrivers.Clear(); for (int i = 0; i < n && colDrivers.Count < 5; i++) colDrivers.Add(i);
        if (rank > 5) colDrivers.Add(rank - 1);
        for (int i = 0; i < colDrivers.Count; i++) colDrvPos[i] = (colDrivers[i] + 1).ToString();
        colTeams.Clear(); for (int i = 0; i < ts.Length && colTeams.Count < 3; i++) colTeams.Add(i);
        int myT = System.Array.IndexOf(ts, d.team), rvT = System.Array.IndexOf(ts, d.rivalTeam);
        if (myT >= 3) colTeams.Add(myT);
        if (rvT >= 3 && rvT != myT) colTeams.Add(rvT);
        colTeams.Sort();
        for (int i = 0; i < colTeams.Count; i++) colTeamPos[i] = (colTeams[i] + 1).ToString();
        // goals
        if (!over && d.goalsFor != d.round) Career.FreezeGoals();
        Career.SeasonGoals(sgText, sgState);
        for (int i = 0; i < rgState.Length; i++) rgState[i] = Career.Open;
        if (!over)
            for (int i = 0; i < d.goals.Count && i < rgState.Length; i++)
                if (d.goals[i].kind == Career.GoalQuali && (d.stage == Season.StageGrid || d.stage == Season.StageRace))
                    rgState[i] = d.qualiPos > 0 && d.qualiPos <= d.goals[i].n ? Career.Done : Career.Failed; // known after qualifying
    }

    public MenuUI.Choice DrawHub(ref int map)
    {
        if (cards != null && cards.Count > 0) { DrawCard(); return MenuUI.Choice.None; }
        if (picking || !Season.Active) { DrawPick(); return MenuUI.Choice.None; }
        Refresh();
        if (full) { DrawFull(); return MenuUI.Choice.None; }
        var d = Season.Data; bool over = Season.Over;
        const string scr = "season";
        var f = Page(true);

        // your standing (top right): the position as the red italic figure, points, team (its colour), the gap to Raske
        float cw = U(250);
        float ec = UiKit.Enter(scr, 2, 0.04f, 0.45f);
        UiKit.PushAlpha(ec);
        var card = UiKit.Slide(new Rect(f.x1 - cw, f.top - U(6), cw, U(58)), ec, 16f);
        float pw = UiKit.Width(statPos, UiKit.HeavyItalic, 46f), tx = card.xMax - U(150), tw = U(150);
        UiKit.BigNumber(new Rect(tx - U(12) - pw, card.y, pw + U(4), card.height), statPos, 46f, UiKit.Red);
        UiKit.Text(new Rect(tx, card.y + U(4), tw, U(18)), statPts, UiKit.Heavy, 15f, UiKit.Ink, TextAnchor.MiddleRight);
        float tmw = UiKit.Width(statTeam, UiKit.Medium, 11f);
        UiKit.Text(new Rect(tx, card.y + U(22), tw, U(16)), statTeam, UiKit.Medium, UiKit.Fit(statTeam, UiKit.Medium, 11f, tw - U(14), 8f), UiKit.Ink, TextAnchor.MiddleRight);
        float sq = Mathf.Round(U(7f));
        UiKit.Rect(new Rect(card.xMax - Mathf.Min(tmw, tw - U(14)) - U(6) - sq, card.y + U(30) - sq * 0.5f, sq, sq), Season.TeamColour(d.team));
        UiKit.Text(new Rect(tx - U(60), card.y + U(38), tw + U(60), U(14)), statGap, UiKit.Body, UiKit.Fit(statGap, UiKit.Body, 10f, tw + U(60), 7.5f), UiKit.InkSub, TextAnchor.MiddleRight);
        UiKit.PopAlpha();

        Header(scr, f, f.w - cw - U(24), hubEyebrow, hubTitle, hubSub);

        // the calendar strip: ten cells, done ones with your result, the next one with a red rule
        float sy = f.top + U(60), sh = U(30), R = Season.Rounds, cg = U(4), cellW = (f.w - (R - 1) * cg) / R;
        float es = UiKit.Enter(scr, 3);
        UiKit.PushAlpha(es);
        for (int r = 0; r < Season.Rounds; r++)
        {
            float x = f.x0 + r * (cellW + cg);
            bool done = r < d.results.Count, next = !over && r == d.round;
            UiKit.Rect(new Rect(x, sy, cellW, next ? Mathf.Max(3f, Mathf.Round(U(3f))) : UiKit.HairPx), next ? UiKit.Red : done ? UiKit.Ink : UiKit.PaperTrack);
            UiKit.Text(new Rect(x, sy + U(4), cellW, U(13)), calCode[r], next ? UiKit.Heavy : UiKit.Medium, UiKit.Fit(calCode[r], UiKit.Medium, 9.5f, cellW, 6.5f), done || next ? UiKit.Ink : UiKit.InkMuted);
            if (done) UiKit.Text(new Rect(x, sy + U(16), cellW, U(13)), calMine[r], UiKit.Heavy, 9.5f, UiKit.InkSub);
            else if (next) UiKit.Text(new Rect(x, sy + U(16), cellW, U(13)), "NEXT", UiKit.Heavy, 8.5f, UiKit.Red);
        }
        UiKit.PopAlpha();

        // the three columns
        float cy = sy + sh + U(14), ch = f.foot - U(8) - cy, gap = U(22);
        float w1 = f.w * 0.37f, w2 = f.w * 0.33f, w3 = f.w - w1 - w2 - 2 * gap;
        float ep = UiKit.Enter(scr, 4, 0.04f, 0.45f);
        UiKit.PushAlpha(ep);
        GoalsColumn(new Rect(f.x0, cy, w1, ch), over);
        DriversColumn(new Rect(f.x0 + w1 + gap, cy, w2, ch));
        TeamsColumn(new Rect(f.x0 + w1 + w2 + 2 * gap, cy, w3, ch));
        UiKit.PopAlpha();

        // the action row
        float eb = UiKit.Enter(scr, 6);
        UiKit.PushAlpha(eb);
        int hit = -1; float lx = f.x0;
        string prim = over ? "Next season" : d.stage == Season.StageGrid ? "Race" : "Qualifying";
        if (Primary(f, prim)) hit = over ? 5 : d.stage == Season.StageGrid ? 4 : 3;
        if (LinkAt(f, ref lx, "‹  Back", "Home")) hit = 0;
        if (!over)
        {
            if (LinkAt(f, ref lx, "Practice", "Free practice")) hit = 1;
            if (d.stage == Season.StageNone && LinkAt(f, ref lx, "Skip qualifying", "Start from the back")) hit = 2;
        }
        if (LinkAt(f, ref lx, "Difficulty", diffSub)) hit = 6;
        if (!over && LinkAt(f, ref lx, "Setup", "Laps  ·  weather")) hit = 7;
        UiKit.PopAlpha();
        switch (hit)
        {
            case 0: m.GoHome(); break;
            case 1: map = Season.CircuitIndex(d.round); m.BeginLoading(true); break;
            case 2: Season.GridFromBack(); cacheKey = int.MinValue; map = Season.CircuitIndex(d.round); m.BeginLoading(false); break;
            case 3: map = Season.CircuitIndex(d.round); m.BeginLoading(false, true); break;
            case 4: map = Season.CircuitIndex(d.round); m.BeginLoading(false); break;
            case 5: Season.NextSeason(); BuildStory(); break;
            case 6: Difficulty.Set((Difficulty.Current + 1) % Difficulty.Names.Length); cacheKey = int.MinValue; break;
            case 7: m.GoPlay(MenuUI.StepSetup, 1); break;
        }
        return MenuUI.Choice.None;
    }

    // this round's goals, then the season's
    void GoalsColumn(Rect a, bool over)
    {
        var d = Season.Data;
        int rows = (over ? 0 : d.goals.Count) + Career.SeasonGoalCount;
        float rh = Mathf.Min(U(22), (a.height - U(44)) / Mathf.Max(1, rows)), bs = Mathf.Round(U(10)), fs = Mathf.Clamp(rh / U(1) * 0.5f, 8.5f, 11f);
        float y = a.y;
        if (!over)
        {
            Head(new Rect(a.x, y, a.width, U(14)), "This round"); HeadRule(a.x, a.xMax, y + U(15)); y += U(18);
            for (int i = 0; i < d.goals.Count; i++, y += rh)
            {
                Box(new Rect(a.x, y + rh * 0.5f - bs * 0.5f, bs, bs), i < rgState.Length ? rgState[i] : Career.Open);
                string t = d.goals[i].text;
                UiKit.Text(new Rect(a.x + bs + U(8), y, a.width - bs - U(8), rh), t, UiKit.Medium, UiKit.Fit(t, UiKit.Medium, fs, a.width - bs - U(10), 7f), UiKit.Ink);
            }
            y += U(6);
        }
        Head(new Rect(a.x, y, a.width, U(14)), "Season goals"); HeadRule(a.x, a.xMax, y + U(15)); y += U(18);
        for (int i = 0; i < Career.SeasonGoalCount; i++, y += rh)
        {
            Box(new Rect(a.x, y + rh * 0.5f - bs * 0.5f, bs, bs), sgState[i]);
            string t = sgText[i];
            UiKit.Text(new Rect(a.x + bs + U(8), y, a.width - bs - U(8), rh), t, UiKit.Medium, UiKit.Fit(t, UiKit.Medium, fs, a.width - bs - U(10), 7f), sgState[i] == Career.Failed ? UiKit.InkMuted : UiKit.Ink);
        }
    }

    void DriversColumn(Rect a)
    {
        Head(new Rect(a.x, a.y, a.width * 0.6f, U(14)), "Drivers");
        float aw = UiKit.LinkWidth("All", null, 10f) + U(16);
        if (UiKit.Link(new Rect(a.xMax - aw, a.y - U(14), aw, U(40)), "All ›", null, true, 10f, TextAnchor.MiddleRight)) { full = true; tab = 0; }
        HeadRule(a.x, a.xMax, a.y + U(15));
        int n = colDrivers.Count;
        float rh = Mathf.Min(U(20), (a.height - U(36)) / Mathf.Max(1, n)), fs = Mathf.Clamp(rh / U(1) * 0.55f, 8.5f, 11f), y = a.y + U(18);
        for (int k = 0; k < n; k++)
        {
            int i = colDrivers[k], id = st[i]; bool me = id == 0;
            if (k > 0 && colDrivers[k] != colDrivers[k - 1] + 1) UiKit.Hair(a.x + U(8), a.xMax - U(8), y + U(1), UiKit.PaperTrack);
            UiKit.PaperRow(new Rect(a.x, y, a.width, rh), me);
            UiKit.Text(new Rect(a.x + U(6), y, U(22), rh), colDrvPos[k], UiKit.Heavy, fs, i < 3 ? UiKit.Ink : UiKit.InkSub);
            UiKit.Rect(new Rect(a.x + U(30), y + rh * 0.26f, Mathf.Max(2f, Mathf.Round(U(2.5f))), rh * 0.48f), Season.Colour(id));
            string nm = Season.Name(id);
            UiKit.Text(new Rect(a.x + U(38), y, a.width - U(80), rh), nm, me || id == Season.RivalId ? UiKit.Heavy : UiKit.Medium, fs, id == Season.RivalId ? UiKit.Red : UiKit.Ink);
            UiKit.Text(new Rect(a.x, y, a.width - U(4), rh), drvPts[i], UiKit.Heavy, fs, UiKit.Ink, TextAnchor.MiddleRight);
            y += rh;
        }
    }

    void TeamsColumn(Rect a)
    {
        var d = Season.Data;
        Head(new Rect(a.x, a.y, a.width, U(14)), "Teams"); HeadRule(a.x, a.xMax, a.y + U(15));
        int n = colTeams.Count;
        float rh = Mathf.Min(U(20), (a.height - U(36)) / Mathf.Max(1, n)), fs = Mathf.Clamp(rh / U(1) * 0.55f, 8.5f, 11f), y = a.y + U(18);
        for (int k = 0; k < n; k++)
        {
            int i = colTeams[k], t = ts[i]; bool me = t == d.team, rv = t == d.rivalTeam;
            UiKit.PaperRow(new Rect(a.x, y, a.width, rh), me);
            UiKit.Text(new Rect(a.x + U(6), y, U(22), rh), colTeamPos[k], UiKit.Heavy, fs, i < 3 ? UiKit.Ink : UiKit.InkSub);
            UiKit.Rect(new Rect(a.x + U(30), y + rh * 0.26f, Mathf.Max(2f, Mathf.Round(U(2.5f))), rh * 0.48f), Season.TeamColour(t));
            string nm = Season.Teams[t];
            UiKit.Text(new Rect(a.x + U(38), y, a.width - U(80), rh), nm, me ? UiKit.Heavy : UiKit.Medium, UiKit.Fit(nm, UiKit.Medium, fs, a.width - U(84), 7f), UiKit.Ink);
            if (rv) { float tw = UiKit.Width(nm, UiKit.Medium, UiKit.Fit(nm, UiKit.Medium, fs, a.width - U(84), 7f)); UiKit.Text(new Rect(a.x + U(42) + tw, y, U(40), rh), "RIVAL", UiKit.Heavy, 7f, UiKit.Red); }
            UiKit.Text(new Rect(a.x, y, a.width - U(4), rh), teamPts[i], UiKit.Heavy, fs, UiKit.Ink, TextAnchor.MiddleRight);
            y += rh;
        }
    }

    // ---------------------------------------------------------------- the full tables (and New career)
    void DrawFull()
    {
        const string scr = "seasonfull";
        var f = Page(true);
        Header(scr, f, f.w, hubEyebrow, "STANDINGS", hubSub);
        float ty = f.top + U(58);
        float et = UiKit.Enter(scr, 3);
        UiKit.PushAlpha(et);
        tab = UiKit.Tabs(new Rect(f.x0, ty, f.w, U(22)), TabNames, tab, 12f);
        UiKit.PopAlpha();
        var area = new Rect(f.x0, ty + U(30), f.w, f.foot - U(6) - (ty + U(30)));
        float ep = UiKit.Enter(scr, 4, 0.04f, 0.45f);
        UiKit.PushAlpha(ep);
        area = UiKit.Rise(area, ep, 10f);
        if (tab == 0) DriverTable(area); else if (tab == 1) TeamTable(area); else CalendarTable(area);
        UiKit.PopAlpha();

        float eb = UiKit.Enter(scr, 6);
        UiKit.PushAlpha(eb);
        int hit = -1; float lx = f.x0;
        UiKit.Hair(f.x0, f.x1, f.foot);
        if (LinkAt(f, ref lx, "‹  Hub", "Career hub")) hit = 0;
        if (LinkAt(f, ref lx, confirmNew ? "Tap again: new career" : "New career", confirmNew ? "This career is wiped" : "Start over as a rookie", confirmNew)) hit = 1;
        UiKit.PopAlpha();
        if (hit == 0) { full = false; confirmNew = false; }
        else if (hit == 1) { if (confirmNew) StartNew(); else confirmNew = true; }
    }

    static readonly string[] Ord = { "", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "21", "22", "23", "24", "25", "26", "27", "28", "29", "30" };
    static string O(int i) => i < Ord.Length ? Ord[i] : i.ToString();

    void DriverTable(Rect a)
    {
        int n = st.Length, cols = 2, per = (n + 1) / 2;
        float gap = U(24), cw = (a.width - gap) / cols, rh = Mathf.Min(U(22), (a.height - U(16)) / per), fs = Mathf.Clamp(rh / U(1) * 0.62f, 8.5f, 11.5f);
        for (int c = 0; c < cols; c++)
        {
            float x = a.x + c * (cw + gap);
            Head(new Rect(x + U(8), a.y, cw * 0.1f, U(14)), "Pos");
            Head(new Rect(x + cw * 0.17f, a.y, cw * 0.4f, U(14)), "Driver");
            Head(new Rect(x + cw * 0.52f, a.y, cw * 0.3f, U(14)), "Team");
            Head(new Rect(x + cw * 0.80f, a.y, cw * 0.18f, U(14)), "Pts", TextAnchor.MiddleRight);
            HeadRule(x, x + cw, a.y + U(15));
        }
        for (int i = 0; i < n; i++)
        {
            int id = st[i]; bool me = id == 0;
            float x = a.x + (i / per) * (cw + gap), y = a.y + U(16) + (i % per) * rh;
            UiKit.PaperRow(new Rect(x, y, cw, rh), me);
            UiKit.Text(new Rect(x + U(8), y, cw * 0.06f, rh), O(i + 1), UiKit.Heavy, fs, i < 3 ? UiKit.Ink : UiKit.InkSub);
            if (drvMove[i] != null) UiKit.Text(new Rect(x + cw * 0.075f, y, cw * 0.07f, rh), drvMove[i], UiKit.Heavy, fs * 0.8f, moveCol[i]);
            UiKit.Rect(new Rect(x + cw * 0.14f, y + rh * 0.26f, Mathf.Max(2f, Mathf.Round(U(2.5f))), rh * 0.48f), Season.Colour(id));
            UiKit.Text(new Rect(x + cw * 0.17f, y, cw * 0.35f, rh), Season.Name(id), me ? UiKit.Heavy : UiKit.Medium, fs, id == Season.RivalId ? UiKit.Red : UiKit.Ink);
            UiKit.Text(new Rect(x + cw * 0.52f, y, cw * 0.28f, rh), drvTeam[i], UiKit.Body, fs * 0.92f, UiKit.InkSub);
            UiKit.Text(new Rect(x + cw * 0.80f, y, cw * 0.18f, rh), drvPts[i], UiKit.Heavy, fs, UiKit.Ink, TextAnchor.MiddleRight);
        }
    }

    void TeamTable(Rect a)
    {
        int n = ts.Length, myTeam = Season.Data.team;
        float rh = Mathf.Min(U(24), (a.height - U(16)) / n), w = a.width, fs = Mathf.Clamp(rh / U(1) * 0.55f, 9f, 12f);
        Head(new Rect(a.x + U(8), a.y, w * 0.06f, U(14)), "Pos");
        Head(new Rect(a.x + w * 0.08f, a.y, w * 0.3f, U(14)), "Team");
        Head(new Rect(a.x + w * 0.36f, a.y, w * 0.5f, U(14)), "Drivers");
        Head(new Rect(a.x + w * 0.86f, a.y, w * 0.13f, U(14)), "Pts", TextAnchor.MiddleRight);
        HeadRule(a.x, a.xMax, a.y + U(15));
        for (int i = 0; i < n; i++)
        {
            int t = ts[i]; float y = a.y + U(16) + i * rh; bool me = t == myTeam;
            UiKit.PaperRow(new Rect(a.x, y, w, rh), me);
            UiKit.Text(new Rect(a.x + U(8), y, w * 0.05f, rh), O(i + 1), UiKit.Heavy, fs, i < 3 ? UiKit.Ink : UiKit.InkSub);
            UiKit.Rect(new Rect(a.x + w * 0.062f, y + rh * 0.26f, Mathf.Max(2f, Mathf.Round(U(2.5f))), rh * 0.48f), Season.TeamColour(t));
            UiKit.Text(new Rect(a.x + w * 0.08f, y, w * 0.28f, rh), Season.Teams[t], me ? UiKit.Heavy : UiKit.Medium, fs, UiKit.Ink);
            UiKit.Text(new Rect(a.x + w * 0.36f, y, w * 0.5f, rh), teamDrivers[i], UiKit.Body, fs * 0.9f, UiKit.InkSub);
            UiKit.Text(new Rect(a.x + w * 0.86f, y, w * 0.13f, rh), teamPts[i], UiKit.Heavy, fs, UiKit.Ink, TextAnchor.MiddleRight);
        }
    }

    void CalendarTable(Rect a)
    {
        var d = Season.Data; int n = Season.Rounds, per = (n + 1) / 2;
        float gap = U(24), cw = (a.width - gap) / 2, rh = Mathf.Min(U(32), (a.height - U(16)) / per), fs = Mathf.Clamp(rh / U(1) * 0.42f, 9f, 12f);
        for (int c = 0; c < 2; c++)
        {
            float x = a.x + c * (cw + gap);
            Head(new Rect(x + U(8), a.y, cw * 0.1f, U(14)), "Rnd");
            Head(new Rect(x + cw * 0.12f, a.y, cw * 0.5f, U(14)), "Round");
            Head(new Rect(x + cw * 0.55f, a.y, cw * 0.43f, U(14)), "Winner / you", TextAnchor.MiddleRight);
            HeadRule(x, x + cw, a.y + U(15));
        }
        for (int r = 0; r < n; r++)
        {
            float x = a.x + (r / per) * (cw + gap), y = a.y + U(16) + (r % per) * rh;
            bool next = r == d.round, done = r < d.results.Count;
            UiKit.PaperRow(new Rect(x, y, cw, rh), next);
            UiKit.Text(new Rect(x + U(8), y, cw * 0.08f, rh), O(r + 1), UiKit.Heavy, fs, done ? UiKit.InkSub : UiKit.Ink);
            float gw = UiKit.Width(calGp[r], next ? UiKit.Heavy : UiKit.Medium, fs);
            UiKit.Text(new Rect(x + cw * 0.12f, y, cw * 0.5f, rh), calGp[r], next ? UiKit.Heavy : UiKit.Medium, fs, done ? UiKit.InkSub : UiKit.Ink);
            UiKit.Text(new Rect(x + cw * 0.12f + gw + U(8), y, cw * 0.3f, rh), calCircuit[r], UiKit.Body, fs * 0.85f, UiKit.InkSub);
            var rr = new Rect(x + cw * 0.55f, y, cw * 0.43f, rh);
            if (done) UiKit.Text(rr, calRes[r], UiKit.Body, fs * 0.92f, UiKit.InkSub, TextAnchor.MiddleRight);
            else if (next) { float w = UiKit.StampWidth("NEXT", 8f); UiKit.Stamp(new Rect(rr.xMax - w, y + rh * 0.5f - U(7.5f), w, U(15)), "NEXT", UiKit.Ink, Color.white, 8f); }
        }
    }

    // ---------------------------------------------------------------- contract offers (a new career)
    int pickInfoFor = -1; string pickInfo, pickEyebrow; string[] teamLine1, teamLine2; int[] offers;
    void DrawPick()
    {
        const string scr = "pick";
        int nt = Season.Teams.Count;
        if (offers == null)
        {
            var l = new List<int>(); for (int t = 0; t < nt; t++) if (Season.Midfield(t)) l.Add(t);
            if (l.Count == 0) for (int t = 0; t < nt; t++) l.Add(t);
            offers = l.ToArray();
            teamLine1 = new string[nt]; teamLine2 = new string[nt];
            for (int t = 0; t < nt; t++)
            {
                var ids = Season.GridDriversOf(t);
                teamLine1[t] = ids.Count > 0 ? Season.Name(ids[0]) : "";
                teamLine2[t] = ids.Count > 1 ? Season.Name(ids[1]) : "Open seat";
            }
            pickEyebrow = "New career  ·  Rookie tier  ·  " + Season.Rounds + " rounds  ·  contract offers";
        }
        if (System.Array.IndexOf(offers, pickTeam) < 0) pickTeam = offers[0];
        if (pickInfoFor != pickTeam)
        {
            pickInfoFor = pickTeam;
            int mate = Season.TeammateFor(pickTeam), moved = Season.DisplacedFor(pickTeam);
            pickInfo = Season.Teams[pickTeam] + "  ·  " + (mate > 0 ? "teammate " + Season.Name(mate) : "no teammate");
            if (moved > 0) pickInfo += "  ·  " + Season.Name(moved) + " moves to " + Season.Teams[Mathf.Max(0, Season.OpenTeam())];
        }
        var f = Page(true);
        Header(scr, f, f.w, pickEyebrow, "Pick your team", pickInfo);

        // the offers: a grid of ruled team cells; tap one to pick it (the chosen one: a red rule over it and a tint)
        int no = offers.Length, cols = Mathf.Max(1, (no + 1) / 2), rows = (no + cols - 1) / cols;
        float diffH = U(64);
        var a = new Rect(f.x0, f.top + U(60), f.w, f.foot - U(10) - diffH - (f.top + U(60)));
        float gap = U(16), cw = (a.width - (cols - 1) * gap) / cols, ch = Mathf.Min(U(80), (a.height - (rows - 1) * U(8)) / rows);
        for (int k = 0; k < no; k++)
        {
            int t = offers[k], c = k % cols, r = k / cols;
            var cr = new Rect(a.x + c * (cw + gap), a.y + r * (ch + U(8)), cw, ch);
            float e = UiKit.Enter(scr, 3 + c + r, 0.035f);
            UiKit.PushAlpha(e);
            cr = UiKit.Rise(cr, e, 10f);
            if (UiKit.Press(cr, true, out float p)) pickTeam = t;
            bool on = t == pickTeam;
            float s = UiKit.Smooth(0x7E00 + t, on ? 1f : 0f, 12f);
            if (s > 0.01f || p > 0.01f) UiKit.Rect(cr, UiKit.A(UiKit.PaperTint, Mathf.Max(s, p)));
            UiKit.Hair(cr.x, cr.xMax, cr.y, UiKit.Ink);
            if (s > 0.01f) UiKit.Rect(new Rect(cr.x, cr.y, cr.width * s, Mathf.Max(3f, Mathf.Round(U(3f)))), UiKit.Red);
            float px = cr.x + U(10), w = cr.width - U(20);
            UiKit.Rect(new Rect(px, cr.y + U(12), U(18), Mathf.Max(3f, Mathf.Round(U(4f)))), Season.TeamColour(t)); // team colour
            UiKit.Text(new Rect(px, cr.y + U(18), w, U(22)), Season.Teams[t], UiKit.Heavy, UiKit.Fit(Season.Teams[t], UiKit.Heavy, 13.5f, w, 9f), UiKit.Ink);
            UiKit.Text(new Rect(px, cr.yMax - U(30), w, U(13)), teamLine1[t], UiKit.Body, UiKit.Fit(teamLine1[t], UiKit.Body, 9.5f, w, 7.5f), UiKit.InkSub);
            UiKit.Text(new Rect(px, cr.yMax - U(17), w, U(13)), teamLine2[t], UiKit.Body, UiKit.Fit(teamLine2[t], UiKit.Body, 9.5f, w, 7.5f), UiKit.InkSub);
            UiKit.PopAlpha();
        }

        // the difficulty: the six presets as text tabs, what the chosen one sets under them
        float dy = f.foot - U(8) - diffH, ed = UiKit.Enter(scr, 5);
        UiKit.PushAlpha(ed);
        UiKit.Hair(f.x0, f.x1, dy + U(4), UiKit.PaperTrack);
        UiKit.Kicker(new Rect(f.x0, dy + U(10), U(90), U(22)), "Difficulty", 9f);
        pickDiff = UiKit.Tabs(new Rect(f.x0 + U(96), dy + U(10), f.w - U(96), U(22)), Difficulty.Names, pickDiff, 12f);
        string sum = Difficulty.Summary[Mathf.Clamp(pickDiff, 0, Difficulty.Summary.Length - 1)];
        UiKit.Text(new Rect(f.x0 + U(96), dy + U(38), f.w - U(96), U(16)), sum, UiKit.Body, UiKit.Fit(sum, UiKit.Body, 10.5f, f.w - U(96), 7.5f), UiKit.InkSub);
        UiKit.PopAlpha();

        float eb = UiKit.Enter(scr, 6);
        UiKit.PushAlpha(eb);
        int hit = -1; float lx = f.x0;
        if (Primary(f, "Sign")) hit = 1;
        if (LinkAt(f, ref lx, "‹  Back", Season.Active ? "Career hub" : "Home")) hit = 0;
        UiKit.PopAlpha();
        if (hit == 0) { picking = false; if (!Season.Active) m.GoHome(); }
        else if (hit == 1)
        {
            Difficulty.Set(pickDiff);
            Season.New(pickTeam); picking = false; tab = 0; cacheKey = int.MinValue;
            var l = Story.Welcome(); l.AddRange(Story.PreRound()); Season.Data.storyPre = 0; Season.Save();
            cards = l; cardIdx = 0;
        }
    }

    // ---------------------------------------------------------------- qualifying results
    Qualifying qFor; List<Qualifying.Entry> qList;
    string[] qTime, qGap; string qHead, qPos, qOf, qBest, qPole, qPoleName, qPoleCap; int qMe;

    void CacheQuali(Qualifying q)
    {
        qFor = q; qList = q.Classified(); double pole = q.Fastest;
        int n = qList.Count; qTime = new string[n]; qGap = new string[n];
        qMe = qList.FindIndex(e => e.Player);
        for (int i = 0; i < n; i++)
        {
            var e = qList[i];
            qTime[i] = Qualifying.Fmt(e.best);
            qGap[i] = double.IsNaN(e.best) ? "no time" : i == 0 ? "Pole" : $"+{e.best - pole:0.000}";
        }
        qHead = $"Qualifying  ·  {q.circuit}  ·  {Qualifying.Clock(q.length)}";
        qPos = "P" + (qMe + 1); qOf = "of " + n + "  ·  the race grid is set from this order";
        qBest = Qualifying.Fmt(qMe >= 0 ? qList[qMe].best : double.NaN);
        qPole = n > 0 && !double.IsNaN(pole) ? Qualifying.Fmt(pole) : "—"; qPoleName = n > 0 ? qList[0].name : "";
        qPoleCap = "Pole  ·  " + qPoleName;
    }

    public MenuUI.Choice DrawQuali()
    {
        var q = lastQuali;
        if (q == null) { m.GoHome(); return MenuUI.Choice.None; }
        if (!ReferenceEquals(q, qFor) || qList == null) CacheQuali(q);
        const string scr = "quali";
        var f = Page(false, qHead);
        float e1 = UiKit.Enter(scr, 1), e2 = UiKit.Enter(scr, 2);

        // data: your best, pole (right-aligned on the header line)
        UiKit.PushAlpha(e2);
        float dx = f.x1, dy = UiKit.Rise(new Rect(0, f.top + U(4), 0, 0), e2, 8f).y;
        float w2 = UiKit.DatumWidth(qPoleCap, qPole, 16f); UiKit.Datum(new Rect(dx - w2, dy, w2, U(36)), qPoleCap, qPole, UiKit.Ink, 16f, TextAnchor.MiddleRight); dx -= w2 + U(26);
        float w1 = UiKit.DatumWidth("Your best", qBest, 16f); UiKit.Datum(new Rect(dx - w1, dy, w1, U(36)), "Your best", qBest, UiKit.Ink, 16f, TextAnchor.MiddleRight); dx -= w1 + U(26);
        UiKit.PopAlpha();

        // the position: the giant red italic figure, "of 20 · the race grid is set from this order"
        UiKit.PushAlpha(e1);
        const float ps = 78f;
        UiKit.BigNumber(UiKit.Rise(new Rect(f.x0 - U(3), f.top - U(6), U(220), U(72)), e1, 14f), qPos, ps, UiKit.Red);
        float bx = f.x0 + UiKit.Width(qPos, UiKit.HeavyItalic, ps) + U(14), bw = Mathf.Max(U(60), dx - bx);
        UiKit.Text(new Rect(bx, f.top + U(16), bw, U(20)), qOf, UiKit.Heavy, UiKit.Fit(qOf, UiKit.Heavy, 14f, bw, 9f), UiKit.Ink);
        UiKit.PopAlpha();

        // classification
        var tp = new Rect(f.x0, f.top + U(74), f.w, f.foot - U(8) - (f.top + U(74)));
        float et = UiKit.Enter(scr, 4, 0.04f, 0.45f);
        UiKit.PushAlpha(et);
        tp = UiKit.Rise(tp, et, 12f);
        int n = qList.Count, cols = n > 9 ? 2 : 1, per = (n + cols - 1) / Mathf.Max(1, cols);
        float tx = tp.x, tw = tp.width, gap = U(24);
        if (cols == 1) tw = Mathf.Min(tw, U(560));
        float cw = (tw - (cols - 1) * gap) / cols, ty = tp.y;
        float rh = Mathf.Min(U(24), (tp.yMax - ty - U(16)) / Mathf.Max(1, per)), fs = Mathf.Clamp(rh / U(1) * 0.55f, 9f, 12f);
        for (int c = 0; c < cols; c++)
        {
            float x = tx + c * (cw + gap);
            Head(new Rect(x + U(8), ty, cw * 0.1f, U(14)), "Pos");
            Head(new Rect(x + cw * 0.15f, ty, cw * 0.4f, U(14)), "Driver");
            Head(new Rect(x + cw * 0.55f, ty, cw * 0.22f, U(14)), "Time");
            Head(new Rect(x + cw * 0.76f, ty, cw * 0.22f, U(14)), "Gap", TextAnchor.MiddleRight);
            HeadRule(x, x + cw, ty + U(15));
        }
        for (int i = 0; i < n; i++)
        {
            var e = qList[i];
            float x = tx + (i / per) * (cw + gap), y = ty + U(16) + (i % per) * rh;
            UiKit.PaperRow(new Rect(x, y, cw, rh), e.Player);
            UiKit.Text(new Rect(x + U(8), y, cw * 0.08f, rh), O(i + 1), UiKit.Heavy, fs, i < 3 ? UiKit.Ink : UiKit.InkSub);
            UiKit.Rect(new Rect(x + cw * 0.115f, y + rh * 0.26f, Mathf.Max(2f, Mathf.Round(U(2.5f))), rh * 0.48f), e.colour);
            UiKit.Text(new Rect(x + cw * 0.15f, y, cw * 0.40f, rh), e.name, e.Player ? UiKit.Heavy : UiKit.Medium, fs, UiKit.Ink);
            bool pole = i == 0 && !double.IsNaN(e.best);
            UiKit.Text(new Rect(x + cw * 0.55f, y, cw * 0.22f, rh), qTime[i], pole ? UiKit.Heavy : UiKit.Body, fs, pole ? UiKit.Ink : UiKit.InkSub);
            UiKit.Text(new Rect(x + cw * 0.76f, y, cw * 0.22f, rh), qGap[i], pole ? UiKit.Medium : UiKit.Body, fs, pole ? UiKit.Ink : UiKit.InkSub, TextAnchor.MiddleRight);
        }
        UiKit.PopAlpha();

        float eb = UiKit.Enter(scr, 6);
        UiKit.PushAlpha(eb);
        int hit = -1; float lx = f.x0;
        if (Primary(f, "Start race")) hit = 1;
        if (LinkAt(f, ref lx, "Menu", m.mode == MenuUI.ModeChampionship ? "Career hub  ·  the grid is kept" : "Home")) hit = 0;
        UiKit.PopAlpha();
        if (hit == 0) { if (m.mode == MenuUI.ModeChampionship) Open(); else m.GoHome(); }
        else if (hit == 1) return MenuUI.Choice.StartRace;
        return MenuUI.Choice.None;
    }
}
