// RaceHud.cs — the in-race HUD in the style of the F1 TV graphics and the onboard steering-wheel display, another
// part of GameManager (see GameManager.cs, OnGUI calls DrawRaceHud). IMGUI, no packages.
//  * timing tower (left): position, 3-letter code, gap to the leader or interval (tap to switch), tyre, stops,
//    fastest lap; compact (leader + the cars round you) or full (tap the arrow)
//  * timing panel (top centre): position / lap, lap clock, last and best, three sectors (purple / green / yellow),
//    live delta to your best lap; the lap time flashes at the line
//  * wheel display (bottom centre): shift lights, speed, gear, battery and MGU-K state (deploy / harvest /
//    superclip), X-mode; beside it the tyres and brakes (car diagram) and fuel
//  * broadcast banners: fastest lap, pit stop, track limits, jump start, yellow flag
// Sector and gap bookkeeping is in Sectors.cs. Styles, textures and number strings are made once (number strings
// are cached the first time each value is shown), so drawing allocates nothing frame to frame.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    // ---------------------------------------------------------------- look
    static readonly Color HudAccent = new Color(0.882f, 0.024f, 0f);          // #E10600
    static readonly Color HudPanel = new Color(0.04f, 0.04f, 0.06f, 0.78f), HudPanel2 = new Color(0.13f, 0.13f, 0.16f, 0.86f);
    static readonly Color HudPurple = new Color(0.66f, 0.25f, 0.95f), HudGreen = new Color(0.10f, 0.84f, 0.36f), HudYellow = new Color(1f, 0.84f, 0.08f);
    static readonly Color HudGrey = new Color(0.62f, 0.63f, 0.68f), HudBlue = new Color(0.25f, 0.62f, 1f), HudCyan = new Color(0.2f, 0.85f, 1f);
    static readonly Color[] SectorCols = { new Color(0.35f, 0.35f, 0.4f), new Color(1f, 0.84f, 0.08f), new Color(0.10f, 0.84f, 0.36f), new Color(0.66f, 0.25f, 0.95f) };
    static readonly string[] SetLetter = { "S", "M", "H", "I", "W" };
    static readonly Color[] SetCol = { new Color(0.92f, 0.12f, 0.12f), new Color(1f, 0.82f, 0.1f), new Color(0.95f, 0.95f, 0.95f), new Color(0.2f, 0.78f, 0.25f), new Color(0.15f, 0.48f, 0.98f) };
    static readonly string[] SectorNames = { "S1", "S2", "S3" }, SectorWords = { "SECTOR 1", "SECTOR 2", "SECTOR 3" };

    static Texture2D hudTri, hudRing, hudDisc; static Font hudFont; static bool hudFontTried;
    GUIStyle hs;

    // ---------------------------------------------------------------- timing state
    SectorTimer secTimer; Track secFor; readonly CarTiming myTime = new CarTiming(); CarTiming[] rivTime = new CarTiming[0]; DeltaTrace deltaTr;
    double hudPrevRace; int hudFrame = -1;
    bool towerFull, towerInterval;
    float lapFlashT, sectorHoldT; double lapFlashTime; int lapFlashCol;   // 0 deleted, 1 yellow, 2 green, 3 purple
    double fastest = double.NaN; int fastestBy = -1, lapsTimed;          // fastestBy: 0 = you, i + 1 = rival i
    bool hudCutSeen, hudJumpSeen, hudYellow; int hudPitSeen; float yellowCheckT;
    readonly int[] ord = new int[40]; readonly double[] prog = new double[40]; int ordN;
    readonly int[] showRanks = new int[40];

    // ---------------------------------------------------------------- banners
    readonly string[] bTitle = new string[4], bSub = new string[4]; readonly Color[] bCol = new Color[4]; int bCount; float bT;
    void Banner(string title, string sub, Color c)
    {
        if (bCount == bTitle.Length) { for (int i = 1; i < bCount; i++) { bTitle[i - 1] = bTitle[i]; bSub[i - 1] = bSub[i]; bCol[i - 1] = bCol[i]; } bCount--; }
        bTitle[bCount] = title; bSub[bCount] = sub; bCol[bCount] = c; bCount++;
        if (bCount == 1) bT = 0;
    }

    // ---------------------------------------------------------------- entry point (from OnGUI)
    void DrawRaceHud(float speed)
    {
        HudInit();
        HudTick();
        DrawTimingPanel();
        DrawTower();
        DrawWheel(speed);
        DrawBanners();
        DrawFps();
    }

    void HudInit()
    {
        if (!hudFontTried)
        {
            hudFontTried = true;
            try { hudFont = Font.CreateDynamicFontFromOSFont(new[] { "Roboto Condensed", "RobotoCondensed-Bold", "Arial Narrow", "Helvetica Neue Condensed Bold", "DIN Condensed", "Roboto", "Arial" }, 32); }
            catch { hudFont = null; }
        }
        if (hs == null)
        {
            hs = new GUIStyle(GUI.skin.label) { padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), wordWrap = false, clipping = TextClipping.Overflow, richText = false };
            if (hudFont != null) hs.font = hudFont;
            hs.normal.textColor = Color.white;
        }
        if (hudTri == null)
        {
            const int D = 32;
            hudTri = new Texture2D(D, D, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[D * D];
            for (int ty = 0; ty < D; ty++) for (int x = 0; x < D; x++)
            {
                float yG = D - 1 - ty; // GUI rows go down; filled: the lower-left half (below the top-left to bottom-right diagonal)
                px[ty * D + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(yG - x + 0.5f)));
            }
            hudTri.SetPixels32(px); hudTri.Apply(false);
            hudRing = Circle(64, 0.62f); hudDisc = Circle(64, 0f);
        }
    }
    static Texture2D Circle(int D, float inner)
    {
        var t = new Texture2D(D, D, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[D * D]; float R = D / 2f - 1;
        for (int y = 0; y < D; y++) for (int x = 0; x < D; x++)
        {
            float d = Mathf.Sqrt((x - D / 2f + 0.5f) * (x - D / 2f + 0.5f) + (y - D / 2f + 0.5f) * (y - D / 2f + 0.5f));
            float a = Mathf.Clamp01(R - d) * (inner > 0 ? Mathf.Clamp01(d - R * inner) : 1);
            px[y * D + x] = new Color32(255, 255, 255, (byte)(255 * a));
        }
        t.SetPixels32(px); t.Apply(false); return t;
    }

    // ---------------------------------------------------------------- drawing helpers
    void T(Rect r, string s, float size, TextAnchor a, Color c, bool bold = true)
    {
        if (string.IsNullOrEmpty(s)) return;
        hs.fontSize = Mathf.Max(6, (int)S(size)); hs.alignment = a; hs.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        var o = GUI.contentColor; GUI.contentColor = c; GUI.Label(r, s, hs); GUI.contentColor = o;
    }
    // kind: 0 lower-left, 1 lower-right, 2 upper-left, 3 upper-right half of r filled
    static readonly Rect[] TriUv = { new Rect(0, 0, 1, 1), new Rect(1, 0, -1, 1), new Rect(0, 1, 1, -1), new Rect(1, 1, -1, -1) };
    void Tri(Rect r, int kind, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTextureWithTexCoords(r, hudTri, TriUv[kind]); GUI.color = o; }
    // F1 TV panel: sharp corners, both ends slanted "/" (a parallelogram); the slants stick out of r
    void Para(Rect r, Color c, bool left = true, bool right = true)
    {
        float s = r.height * 0.36f;
        Fill(r, c);
        if (left) Tri(new Rect(r.x - s, r.y, s, r.height), 1, c);
        if (right) Tri(new Rect(r.xMax, r.y, s, r.height), 2, c);
    }
    void Tex(Rect r, Texture2D t, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, t); GUI.color = o; }
    void TyreIcon(Rect r, int set)
    {
        set = Mathf.Clamp(set, 0, SetLetter.Length - 1);
        Tex(r, hudRing, SetCol[set]);
        T(r, SetLetter[set], r.height / S(1) * 0.62f, TextAnchor.MiddleCenter, SetCol[set]);
    }
    static Color TempCol(double t, double opt, double win)
    {
        if (double.IsNaN(t)) return HudGrey;
        float k = (float)((t - opt) / win);
        if (k < -1) return HudBlue;
        if (k < -0.35f) return Color.Lerp(HudBlue, HudGreen, (k + 1) / 0.65f);
        if (k < 0.35f) return HudGreen;
        if (k < 1) return Color.Lerp(HudGreen, HudYellow, (k - 0.35f) / 0.65f);
        return Color.Lerp(HudYellow, new Color(1f, 0.2f, 0.15f), Mathf.Clamp01(k - 1));
    }

    // ---------------------------------------------------------------- once a frame: timing and events
    void HudTick()
    {
        if (hudFrame == Time.frameCount) return;
        hudFrame = Time.frameCount;
        float dt = Time.unscaledDeltaTime;
        if (lapFlashT > 0) lapFlashT -= dt;
        if (sectorHoldT > 0) sectorHoldT -= dt;
        if (bCount > 0 && !Paused) { bT += dt; if (bT > 2.8f) { for (int i = 1; i < bCount; i++) { bTitle[i - 1] = bTitle[i]; bSub[i - 1] = bSub[i]; bCol[i - 1] = bCol[i]; } bCount--; bT = 0; } }
        if (playground || track == null || idx < 0) return;
        if (secTimer == null || secFor != track || deltaTr == null || deltaTr.Length != track.N)
        {
            secTimer = new SectorTimer(track); secFor = track; deltaTr = new DeltaTrace(track.N);
            HudReset(false);
        }
        else if (raceTime < hudPrevRace - 0.25) HudReset(raceLaps == 0 && bestLap.HasValue); // a restart (practice keeps its best lap)
        hudPrevRace = raceTime;
        if (rivTime.Length != rivals.Count) { rivTime = new CarTiming[rivals.Count]; for (int i = 0; i < rivTime.Length; i++) rivTime[i] = new CarTiming(); }
        if (Paused) return;

        // you
        if (cut && !hudCutSeen && lapStart.HasValue)
        {
            bool recovered = flash == "Back on track" && flashT > 1.2f;
            Banner(recovered ? "LAP DELETED" : "TRACK LIMITS", recovered ? "CAR RECOVERED" : "LAP DELETED", HudAccent);
        }
        hudCutSeen = cut;
        myTime.invalid |= cut;
        secTimer.Update(myTime, idx, simTime, lapStart ?? double.NaN, lastLap ?? double.NaN, deltaTr);
        if (myTime.evSector == 2 || !double.IsNaN(myTime.evLap)) sectorHoldT = 4f;
        if (!double.IsNaN(myTime.evLap))
        {
            lapFlashT = 3.2f; lapFlashTime = myTime.evLap;
            lapFlashCol = !myTime.evLapValid ? 0 : myTime.evLapPb ? 2 : 1;
            if (myTime.evLapValid && (double.IsNaN(fastest) || myTime.evLap < fastest))
            {
                if (lapsTimed > 0 && rivals.Count > 0) Banner("FASTEST LAP", "YOU  " + Fmt(myTime.evLap), HudPurple);
                fastest = myTime.evLap; fastestBy = 0; lapFlashCol = 3;
            }
            if (myTime.evLapValid) lapsTimed++;
        }
        bool started = raceTime >= Countdown;
        if (started) myTime.Loop(pLap, idx, track.N, simTime);

        // rivals
        for (int i = 0; i < rivals.Count; i++)
        {
            var d = rivals[i]; var rt = rivTime[i];
            secTimer.Update(rt, d.idx, simTime, d.lapStart, d.lastLap, null);
            if (!double.IsNaN(rt.evLap))
            {
                if (double.IsNaN(fastest) || rt.evLap < fastest)
                {
                    if (lapsTimed > 0) Banner("FASTEST LAP", Code(d.who) + "  " + Fmt(rt.evLap), HudPurple);
                    fastest = rt.evLap; fastestBy = i + 1;
                }
                lapsTimed++;
            }
            if (started && d.idx >= 0) rt.Loop(d.lap, d.idx, track.N, simTime);
        }

        // running order (the same measure as Position())
        ordN = Mathf.Min(1 + rivals.Count, ord.Length);
        prog[0] = pLap + (double)idx / track.N - pitLoss / RefLap();
        for (int i = 1; i < ordN; i++) prog[i] = rivals[i - 1].progress;
        for (int i = 0; i < ordN; i++) ord[i] = i;
        for (int i = 1; i < ordN; i++) { int e = ord[i], j = i - 1; while (j >= 0 && prog[ord[j]] < prog[e]) { ord[j + 1] = ord[j]; j--; } ord[j + 1] = e; }

        // pit stop, jump start
        if (pitStops > hudPitSeen) Banner("PIT STOP", PitLoss.ToString("0.0") + " s  ·  " + Weather.TyreName(carSet).ToUpperInvariant(), HudAccent);
        hudPitSeen = pitStops;
        if (jumped && !hudJumpSeen) Banner("JUMP START", "+" + JumpPenalty.ToString("0") + " s TIME PENALTY", HudAccent);
        hudJumpSeen = jumped;

        // yellow flag: a stopped car up to 400 m ahead of you
        yellowCheckT -= dt;
        if (yellowCheckT <= 0)
        {
            yellowCheckT = 0.3f;
            bool y = false; int ySector = 0;
            if (raceTime > Countdown + 4)
                for (int i = 0; i < rivals.Count && !y; i++)
                {
                    var c = rivals[i].car; if (rivals[i].idx < 0) continue;
                    double ahead = ((rivals[i].idx - idx + track.N) % track.N) * track.ds;
                    if (c.vx * c.vx + c.vy * c.vy < 36 && ahead > 5 && ahead < 400) { y = true; ySector = secTimer.SectorOf(rivals[i].idx); }
                }
            if (y && !hudYellow) Banner("YELLOW FLAG", SectorWords[ySector], HudYellow);
            hudYellow = y;
        }
    }

    void HudReset(bool keepBest)
    {
        if (keepBest) { myTime.AbortLap(); if (deltaTr != null) deltaTr.Abort(); }
        else { myTime.Clear(); if (deltaTr != null) deltaTr.Clear(); if (secTimer != null) secTimer.ResetBests(); fastest = double.NaN; fastestBy = -1; lapsTimed = 0; }
        if (keepBest && fastestBy > 0) { fastest = myTime.bestLap; fastestBy = double.IsNaN(fastest) ? -1 : 0; }
        foreach (var r in rivTime) r.Clear();
        hudPitSeen = pitStops; hudJumpSeen = jumped; hudCutSeen = cut; hudYellow = false; bCount = 0; lapFlashT = sectorHoldT = 0;
    }

    CarTiming TimingOf(int e) => e == 0 ? myTime : rivTime[e - 1];
    double Penalty(int e) => e == 0 ? pitLoss : 0;
    int SetOf(int e) => e == 0 ? carSet : rivalSet;
    int StopsOf(int e) => e == 0 ? pitStops : 0;

    // gap of the car at rank r to the leader (or the car ahead): "+1.2", "+1 LAP", "" if not known yet
    string GapText(int r)
    {
        if (raceTime < Countdown + 0.5) return "";
        if (r == 0) return towerInterval ? "INTERVAL" : "LEADER";
        int a = towerInterval ? ord[r - 1] : ord[0], b = ord[r];
        double dp = prog[a] - prog[b];
        if (dp >= 1) return HudNum.LapsDown((int)dp);
        var tb = TimingOf(b); var ta = TimingOf(a);
        if (!tb.TimeAt(tb.lastKey, out double tB) || !ta.TimeAt(tb.lastKey, out double tA)) return "";
        double g = tB + Penalty(b) - tA - Penalty(a);
        return HudNum.Gap(g < 0 ? 0 : g);
    }

    // ---------------------------------------------------------------- driver codes from the invented names
    static Dictionary<string, string> codes;
    static string Code(DriverProfile who)
    {
        if (codes == null)
        {
            codes = new Dictionary<string, string>();
            var used = new HashSet<string> { "YOU" };
            foreach (var g in DriverProfile.Grid)
            {
                string sur = g.name; int dot = sur.LastIndexOf(' '); if (dot >= 0) sur = sur.Substring(dot + 1);
                var letters = new System.Text.StringBuilder();
                foreach (char ch in sur.ToUpperInvariant()) if (char.IsLetter(ch)) letters.Append(ch);
                string l = letters.ToString(); while (l.Length < 3) l += "X";
                string c = l.Substring(0, 3);
                for (int k = 3; used.Contains(c) && k < l.Length; k++) c = l.Substring(0, 2) + l[k];  // BERgman / BEGstrom
                if (used.Contains(c) && g.name.Length > 0) c = g.name.Substring(0, 1).ToUpperInvariant() + l.Substring(0, 2);
                used.Add(c); codes[g.name] = c;
            }
        }
        return codes.TryGetValue(who.name, out var s) ? s : "???";
    }

    // ---------------------------------------------------------------- timing tower
    readonly HudSlot lapTag = new HudSlot();
    void DrawTower()
    {
        if (!hudTower || playground || rivals.Count == 0 || ordN == 0 || quali != null) return; // qualifying draws its own list (Qualifying.cs)
        float x = S(10), y = S(6), w = S(150), rh = S(16), hh = S(18);
        float bottom = tilt ? BrakeRect.y - S(8) : Screen.height - S(48);
        // header: red lap tag, GAP / INT, collapse arrow
        Para(new Rect(x, y, S(66), hh), HudAccent, false, true);
        int lapShown = raceLaps > 0 ? Mathf.Clamp(pLap, 1, raceLaps) : lapNo;
        T(new Rect(x + S(5), y, S(64), hh), lapTag.Lap(lapShown, raceLaps), 10.5f, TextAnchor.MiddleLeft, Color.white);
        Fill(new Rect(x + S(76), y, w - S(76), hh), HudPanel);
        T(new Rect(x + S(80), y, S(40), hh), towerInterval ? "INT" : "GAP", 9.5f, TextAnchor.MiddleLeft, HudGrey);
        T(new Rect(x + w - S(20), y, S(18), hh), towerFull ? "▲" : "▼", 9, TextAnchor.MiddleCenter, Color.white);
        if (GUI.Button(new Rect(x + S(76), y, w - S(98), hh), GUIContent.none, GUIStyle.none)) towerInterval = !towerInterval;
        if (GUI.Button(new Rect(x + w - S(24), y - S(2), S(28), hh + S(4)), GUIContent.none, GUIStyle.none)) towerFull = !towerFull;
        y += hh + S(2);

        // which ranks: all that fit, else the top of the field and the cars round you
        int myRank = 0; for (int r = 0; r < ordN; r++) if (ord[r] == 0) { myRank = r; break; }
        int cap = towerFull ? Mathf.Max(4, (int)((bottom - y) / rh)) : 4;
        int n = 0;
        if (ordN <= cap) for (int r = 0; r < ordN; r++) showRanks[n++] = r;
        else
        {
            int lo = Mathf.Clamp(myRank - 1, 0, ordN - 3), top = cap - 3;
            if (myRank < cap - 1) { lo = cap; top = cap; }
            for (int r = 0; r < top; r++) showRanks[n++] = r;
            for (int r = lo; r < lo + 3 && r < ordN && n < cap; r++) if (r >= top) showRanks[n++] = r;
        }
        float y0 = y;
        for (int k = 0; k < n; k++)
        {
            int r = showRanks[k], e = ord[r];
            if (k > 0 && showRanks[k - 1] != r - 1) { Fill(new Rect(x, y, w, S(2)), HudAccent); y += S(3); }
            bool me = e == 0;
            Fill(new Rect(x, y, w, rh - S(1)), me ? new Color(HudAccent.r, HudAccent.g, HudAccent.b, 0.55f) : HudPanel);
            T(new Rect(x, y, S(18), rh), HudNum.Int(r + 1), 10.5f, TextAnchor.MiddleCenter, Color.white);
            Color team = me ? new Color(1f, 0.25f, 0.2f) : new Color(rivals[e - 1].who.r, rivals[e - 1].who.g, rivals[e - 1].who.b);
            Fill(new Rect(x + S(19), y + S(2), S(3), rh - S(5)), team);
            T(new Rect(x + S(25), y, S(34), rh), me ? "YOU" : Code(rivals[e - 1].who), 10.5f, TextAnchor.MiddleLeft, Color.white);
            T(new Rect(x + S(58), y, S(46), rh), GapText(r), r == 0 ? 7.5f : 9.5f, TextAnchor.MiddleRight, r == 0 ? HudGrey : Color.white, r != 0);
            TyreIcon(new Rect(x + S(108), y + S(2), rh - S(5), rh - S(5)), SetOf(e));
            int stops = StopsOf(e); if (stops > 0) T(new Rect(x + S(122), y, S(10), rh), HudNum.Int(stops), 8.5f, TextAnchor.MiddleCenter, HudGrey);
            if (fastestBy == e) { Fill(new Rect(x + S(134), y + S(2), S(14), rh - S(5)), HudPurple); T(new Rect(x + S(134), y + S(2), S(14), rh - S(5)), "FL", 6.5f, TextAnchor.MiddleCenter, Color.white); }
            y += rh;
        }
        if (GUI.Button(new Rect(x, y0, w, y - y0), GUIContent.none, GUIStyle.none)) towerInterval = !towerInterval; // tap the tower: gap / interval
    }

    // ---------------------------------------------------------------- timing panel (top centre)
    readonly HudSlot lastSlot = new HudSlot(), bestSlot = new HudSlot(), flashSlot = new HudSlot(), testSlot = new HudSlot();
    readonly HudSlot[] secSlot = { new HudSlot(), new HudSlot(), new HudSlot() };
    void DrawTimingPanel()
    {
        float w = S(300), x = Screen.width / 2f - w / 2, y = S(6), h = S(32);
        if (x + w + S(14) > Screen.width - S(246) || quali != null) y = S(44); // narrow screen: below the buttons; qualifying: below its clock
        if (playground)
        {
            Para(new Rect(x, y, w, S(40)), HudPanel);
            Fill(new Rect(x, y, S(4), S(40)), HudAccent);
            T(new Rect(x, y + S(2), w, S(18)), "TEST DATA (ours vs real F1)", 12, TextAnchor.MiddleCenter, Color.white);
            T(new Rect(x, y + S(20), w, S(18)), testSlot.Test(t100, topSpeed * (unitsIdx == 1 ? 0.621371 : 1), SpeedUnit), 10, TextAnchor.MiddleCenter, HudGrey, false);
            return;
        }
        // tag: position in a race, lap in practice
        Para(new Rect(x, y, S(52), h), HudAccent, true, false);
        Tri(new Rect(x + S(52), y, h * 0.36f, h), 2, HudAccent);
        if (rivals.Count > 0) T(new Rect(x, y, S(54), h), HudNum.Pos(Position()), 17, TextAnchor.MiddleCenter, Color.white);
        else T(new Rect(x, y, S(54), h), HudNum.LapShort(lapNo), 13, TextAnchor.MiddleCenter, Color.white);
        // lap clock (flashes the lap just done at the line)
        var clock = new Rect(x + S(62), y, S(128), h);
        Fill(new Rect(x + S(52) + h * 0.36f, y, w - S(52) - h * 0.36f, h), HudPanel);
        if (lapFlashT > 0)
        {
            bool on = lapFlashT > 2.4f ? Mathf.Repeat(lapFlashT * 4, 1) > 0.35f : true;
            Color c = lapFlashCol == 0 ? HudGrey : SectorCols[lapFlashCol];
            if (on) Fill(new Rect(clock.x - S(4), y + S(3), clock.width + S(4), h - S(6)), new Color(c.r * 0.35f, c.g * 0.35f, c.b * 0.35f, 0.9f));
            T(clock, flashSlot.Lap(lapFlashTime), 20, TextAnchor.MiddleCenter, lapFlashCol == 0 ? HudGrey : c);
            if (lapFlashCol == 0) T(new Rect(clock.x, y + h - S(9), clock.width, S(9)), "DELETED", 7, TextAnchor.MiddleCenter, HudAccent);
        }
        else T(clock, lapStart.HasValue ? HudNum.Clock(simTime - lapStart.Value) : "--:--.-", 20, TextAnchor.MiddleCenter, cut && lapStart.HasValue ? HudGrey : Color.white);
        // last / best
        float lx = x + S(194);
        T(new Rect(lx, y + S(3), S(30), S(13)), "LAST", 8, TextAnchor.MiddleLeft, HudGrey);
        T(new Rect(lx + S(28), y + S(3), S(74), S(13)), lastSlot.Lap(lastLap ?? double.NaN), 10.5f, TextAnchor.MiddleRight, Color.white);
        T(new Rect(lx, y + S(16), S(30), S(13)), "BEST", 8, TextAnchor.MiddleLeft, HudGrey);
        T(new Rect(lx + S(28), y + S(16), S(74), S(13)), bestSlot.Lap(bestLap ?? double.NaN), 10.5f, TextAnchor.MiddleRight, fastestBy == 0 ? HudPurple : HudGreen);
        y += h + S(2);

        // sectors
        if (hudSectors && secTimer != null)
        {
            bool hold = sectorHoldT > 0;
            float sw = (w - S(4)) / 3f, sh = S(18);
            int curSec = secTimer.SectorOf(Mathf.Max(idx, 0));
            for (int s = 0; s < 3; s++)
            {
                var r = new Rect(x + s * (sw + S(2)), y, sw, sh);
                double t = hold ? myTime.last[s] : myTime.cur[s]; int col = hold ? myTime.lastCol[s] : myTime.curCol[s];
                Fill(r, HudPanel);
                bool live = !hold && double.IsNaN(t) && s == curSec && lapStart.HasValue;
                Fill(new Rect(r.x, r.y, r.width, S(3)), double.IsNaN(t) ? (live ? new Color(1, 1, 1, 0.55f) : SectorCols[0]) : SectorCols[col]);
                T(new Rect(r.x + S(4), r.y + S(2), S(20), sh - S(2)), SectorNames[s], 8, TextAnchor.MiddleLeft, HudGrey);
                if (!double.IsNaN(t)) T(new Rect(r.x, r.y + S(2), r.width - S(5), sh - S(2)), secSlot[s].Sec(t), 10.5f, TextAnchor.MiddleRight, col >= 1 ? SectorCols[col] : Color.white);
            }
            y += sh + S(2);
        }
        // live delta to your best lap
        if (hudDelta && deltaTr != null && lapStart.HasValue && lapFlashT <= 0)
        {
            double dl = deltaTr.Delta(idx, simTime - lapStart.Value);
            if (!double.IsNaN(dl))
            {
                float dw = S(124), dx = Screen.width / 2f - dw / 2, dh = S(18);
                Color c = dl <= 0 ? HudGreen : new Color(1f, 0.22f, 0.18f);
                Fill(new Rect(dx, y, dw, dh), HudPanel);
                float half = dw / 2, k = Mathf.Clamp01(Mathf.Abs((float)dl)) * half;
                Fill(new Rect(dx + half - (dl <= 0 ? k : 0), y + dh - S(3), k, S(3)), c);
                Fill(new Rect(dx + half - S(0.5f), y + dh - S(5), S(1), S(5)), Color.white);
                T(new Rect(dx, y, dw, dh - S(2)), HudNum.Delta(dl), 11.5f, TextAnchor.MiddleCenter, c);
            }
        }
    }

    // ---------------------------------------------------------------- steering-wheel display (bottom centre)
    void DrawWheel(float speed)
    {
        float cw = S(196), ch = S(76), pod = S(92), gap = S(6);
        float cx = Screen.width / 2f - cw / 2, cy = Screen.height - ch - S(8);
        bool pods = hudInfo && Screen.width / 2f + cw / 2 + gap + pod + S(8) < (tilt ? Screen.width - S(104) : Screen.width - S(212))
                             && Screen.width / 2f - cw / 2 - gap - pod - S(8) > (tilt ? BrakeRect.xMax : S(0));
        // chips above: X-mode, slipstream, yellow flag
        float chipY = cy - S(17), chipX = cx;
        if (car.aeroX > 0.5) { Para(new Rect(chipX + S(4), chipY, S(52), S(14)), HudCyan); T(new Rect(chipX + S(4), chipY, S(52), S(14)), "X-MODE", 9, TextAnchor.MiddleCenter, Color.black); chipX += S(64); }
        if (car.tow > 0.3) { Para(new Rect(chipX + S(4), chipY, S(40), S(14)), HudPanel2); T(new Rect(chipX + S(4), chipY, S(40), S(14)), "TOW", 9, TextAnchor.MiddleCenter, HudCyan); chipX += S(52); }
        if (hudYellow) { Para(new Rect(chipX + S(4), chipY, S(58), S(14)), HudYellow); T(new Rect(chipX + S(4), chipY, S(58), S(14)), "YELLOW", 9, TextAnchor.MiddleCenter, Color.black); chipX += S(70); }
        if (boxArmed) { Para(new Rect(chipX + S(4), chipY, S(40), S(14)), HudAccent); T(new Rect(chipX + S(4), chipY, S(40), S(14)), "BOX", 9, TextAnchor.MiddleCenter, Color.white); }

        // centre screen
        Fill(new Rect(cx, cy, cw, ch), HudPanel);
        Fill(new Rect(cx, cy + ch - S(2), cw, S(2)), HudAccent);
        // shift lights: 5 green, 5 red, 5 blue; all blink at the limiter
        double red = car.cfg.redline, frac = (car.rpm - red * 0.62) / (red * 0.36);
        int lit = Mathf.Clamp(Mathf.CeilToInt((float)frac * 15), 0, 15);
        bool limiter = car.rpm > red * 0.985 && !car.reverse;
        bool blinkOn = Mathf.Repeat(Time.unscaledTime * 10, 1) > 0.5f;
        float ld = S(9), lg = (cw - S(16) - 15 * ld) / 14f;
        for (int i = 0; i < 15; i++)
        {
            Color c = i < 5 ? HudGreen : i < 10 ? new Color(1f, 0.15f, 0.1f) : new Color(0.35f, 0.4f, 1f);
            bool on = limiter ? blinkOn : i < lit;
            Tex(new Rect(cx + S(8) + i * (ld + lg), cy + S(4), ld, ld), hudDisc, on ? (limiter ? new Color(0.35f, 0.4f, 1f) : c) : new Color(1, 1, 1, 0.12f));
        }
        // speed (left), gear (middle), battery (right)
        float top = cy + S(16);
        T(new Rect(cx + S(4), top, S(66), S(30)), HudNum.Int(Mathf.RoundToInt(speed * (unitsIdx == 1 ? 2.23694f : 3.6f))), 25, TextAnchor.MiddleCenter, Color.white);
        T(new Rect(cx + S(4), top + S(27), S(66), S(12)), SpeedUnit, 8, TextAnchor.MiddleCenter, HudGrey);
        Fill(new Rect(cx + S(72), top, S(52), S(42)), new Color(0, 0, 0, 0.5f));
        T(new Rect(cx + S(72), top - S(1), S(52), S(44)), car.reverse ? "R" : car.gear <= 0 ? "N" : HudNum.Int(car.gear), 34, TextAnchor.MiddleCenter, limiter ? new Color(0.45f, 0.55f, 1f) : Color.white);
        float bx = cx + S(130), bw = S(58);
        float soc = double.IsNaN(car.ersEnergy) ? 1f : Mathf.Clamp01((float)(car.ersEnergy / car.cfg.ersCapacity));
        double kw = double.IsNaN(car.ersKw) ? 0 : car.ersKw;
        string mode; Color mc;
        if (kw > 5) { mode = "DEPLOY"; mc = HudBlue; }
        else if (kw < -5 && car.throttle > 0.5) { mode = "SUPERCLIP"; mc = new Color(1f, 0.6f, 0.1f); }
        else if (kw < -5) { mode = "HARVEST"; mc = HudGreen; }
        else { mode = "BATT"; mc = HudGrey; }
        T(new Rect(bx, top, bw, S(11)), "SOC", 7.5f, TextAnchor.MiddleLeft, HudGrey);
        T(new Rect(bx, top, bw, S(11)), HudNum.Pct(Mathf.RoundToInt(soc * 100)), 9.5f, TextAnchor.MiddleRight, Color.white);
        Fill(new Rect(bx, top + S(12), bw, S(7)), new Color(1, 1, 1, 0.14f));
        Fill(new Rect(bx, top + S(12), bw * soc, S(7)), soc < 0.15f ? new Color(1f, 0.25f, 0.2f) : mc == HudGrey ? new Color(0.8f, 0.85f, 0.9f) : mc);
        T(new Rect(bx, top + S(20), bw, S(12)), mode, 8.5f, TextAnchor.MiddleLeft, mc);
        if (mode != "BATT") T(new Rect(bx, top + S(31), bw, S(11)), HudNum.Kw(Mathf.RoundToInt((float)System.Math.Abs(kw))), 8, TextAnchor.MiddleLeft, mc, false);
        // aids
        float ay = cy + ch - S(14);
        T(new Rect(cx + S(6), ay, S(30), S(10)), "TC", 7.5f, TextAnchor.MiddleLeft, car.tcActive ? new Color(1f, 0.69f, 0.13f) : new Color(1, 1, 1, 0.3f));
        T(new Rect(cx + S(24), ay, S(30), S(10)), "ABS", 7.5f, TextAnchor.MiddleLeft, car.absActive ? new Color(1f, 0.69f, 0.13f) : new Color(1, 1, 1, 0.3f));
        T(new Rect(cx + S(48), ay, S(30), S(10)), "LOCK", 7.5f, TextAnchor.MiddleLeft, car.lockF || car.lockR ? new Color(1f, 0.3f, 0.23f) : new Color(1, 1, 1, 0.3f));
        if (car.spinR) T(new Rect(cx + S(130), ay, S(40), S(10)), "SPIN", 7.5f, TextAnchor.MiddleLeft, new Color(1f, 0.3f, 0.23f));
        if (!pods) return;

        // left pod: tyres (temperature colour, tread bar, carcass temp) and brakes on a little car
        var lp = new Rect(cx - gap - pod, cy, pod, ch);
        Para(lp, HudPanel, true, false);
        if (car.cfg.chassis && !double.IsNaN(car.tyreTF))
        {
            var cd = CompoundTable.Defs[Mathf.Clamp((int)car.cfg.compound, 0, CompoundTable.Defs.Length - 1)];
            float tw = S(15), th = S(22), mx = lp.x + pod / 2;
            float fy = cy + S(6), ry = cy + ch - th - S(8);
            Fill(new Rect(mx - S(4), fy + S(4), S(8), ry + th - fy - S(8)), new Color(1, 1, 1, 0.18f));     // chassis
            Fill(new Rect(mx - S(14), fy + th / 2 - S(1), S(28), S(2)), new Color(1, 1, 1, 0.18f));         // front axle
            Fill(new Rect(mx - S(14), ry + th / 2 - S(1), S(28), S(2)), new Color(1, 1, 1, 0.18f));         // rear axle
            for (int wh = 0; wh < 4; wh++)
            {
                bool front = wh < 2, left = (wh & 1) == 0;
                double t = front ? car.tyreTF : car.tyreTR, wear = front ? car.wearF : car.wearR;
                float wx = left ? mx - S(30) : mx + S(30) - tw, wy = front ? fy : ry;
                Fill(new Rect(wx, wy, tw, th), TempCol(t, cd.opt, cd.window));
                T(new Rect(wx - S(2), wy, tw + S(4), th), HudNum.Int(Mathf.RoundToInt((float)t)), 8, TextAnchor.MiddleCenter, Color.black);
                float tread = Mathf.Clamp01(1 - (float)wear), bxw = left ? wx - S(4) : wx + tw + S(1);
                Fill(new Rect(bxw, wy, S(3), th), new Color(1, 1, 1, 0.15f));
                Fill(new Rect(bxw, wy + th * (1 - tread), S(3), th * tread), tread < 0.3f ? new Color(1f, 0.3f, 0.2f) : Color.white);
            }
            if (!double.IsNaN(car.brakeTF))
            {
                Fill(new Rect(mx - S(12), fy + th + S(1), S(24), S(3)), TempCol(car.brakeTF, 650, 380));
                Fill(new Rect(mx - S(12), ry - S(4), S(24), S(3)), TempCol(car.brakeTR, 650, 380));
                T(new Rect(mx - S(20), fy + th + S(3), S(40), S(10)), HudNum.Int(Mathf.RoundToInt((float)car.brakeTF)), 7, TextAnchor.MiddleCenter, HudGrey);
                T(new Rect(mx - S(20), ry - S(14), S(40), S(10)), HudNum.Int(Mathf.RoundToInt((float)car.brakeTR)), 7, TextAnchor.MiddleCenter, HudGrey);
            }
            T(new Rect(lp.x - S(8), cy + ch - S(10), S(30), S(9)), "BRK", 6.5f, TextAnchor.MiddleLeft, HudGrey, false);
            T(new Rect(lp.xMax - S(32), cy + ch - S(10), S(30), S(9)), "TYRE", 6.5f, TextAnchor.MiddleRight, HudGrey, false);
        }
        // right pod: fuel and the tyre set
        var rp = new Rect(cx + cw + gap, cy, pod, ch);
        Para(rp, HudPanel, false, true);
        TyreIcon(new Rect(rp.x + S(6), cy + S(6), S(16), S(16)), carSet);
        T(new Rect(rp.x + S(26), cy + S(6), pod - S(28), S(16)), Weather.TyreName(carSet), 9, TextAnchor.MiddleLeft, Color.white);
        if (!double.IsNaN(car.fuel))
        {
            T(new Rect(rp.x + S(6), cy + S(25), S(40), S(12)), "FUEL", 7.5f, TextAnchor.MiddleLeft, HudGrey);
            T(new Rect(rp.x + S(6), cy + S(24), pod - S(10), S(16)), HudNum.Tenths(car.fuel), 13, TextAnchor.MiddleRight, Color.white);
            float ff = Mathf.Clamp01((float)(car.fuel / System.Math.Max(1, car.cfg.fuelStart)));
            Fill(new Rect(rp.x + S(6), cy + S(42), pod - S(12), S(4)), new Color(1, 1, 1, 0.14f));
            Fill(new Rect(rp.x + S(6), cy + S(42), (pod - S(12)) * ff, S(4)), ff < 0.1f ? new Color(1f, 0.3f, 0.2f) : new Color(0.85f, 0.88f, 0.92f));
            if (!double.IsNaN(car.fuelLastLap) && car.fuelLastLap > 0.05)
            {
                double laps = car.fuel / car.fuelLastLap; int toGo = raceLaps > 0 ? raceLaps - Mathf.Clamp(pLap, 1, raceLaps) + 1 : 0;
                T(new Rect(rp.x + S(6), cy + S(50), S(40), S(12)), "LAPS", 7.5f, TextAnchor.MiddleLeft, HudGrey);
                T(new Rect(rp.x + S(6), cy + S(49), pod - S(10), S(14)), HudNum.Tenths(laps), 11, TextAnchor.MiddleRight, raceLaps > 0 && laps < toGo ? new Color(1f, 0.3f, 0.2f) : HudGreen);
            }
        }
    }

    // ---------------------------------------------------------------- banners and messages
    void DrawBanners()
    {
        if (bCount > 0)
        {
            float a = Mathf.Clamp01(bT / 0.18f);
            float w = S(300), h = S(30), x = Screen.width / 2f - w / 2 - (1 - a) * (1 - a) * S(60), y = Screen.height * 0.33f;
            Color c = bCol[0];
            float tw = S(130);
            Para(new Rect(x, y, tw, h), c, true, false);
            Tri(new Rect(x + tw, y, h * 0.36f, h), 2, c);
            Para(new Rect(x + tw + h * 0.36f + S(2), y, w - tw - h * 0.36f - S(2), h), HudPanel, false, true);
            T(new Rect(x, y, tw + S(4), h), bTitle[0], 14, TextAnchor.MiddleCenter, c == HudYellow ? Color.black : Color.white);
            T(new Rect(x + tw + S(14), y, w - tw - S(14), h), bSub[0], 12, TextAnchor.MiddleLeft, Color.white);
        }
        // the game's own short messages, unless a banner or the lap flash already says it
        if (flashT > 0 && !string.IsNullOrEmpty(flash) && !Covered(flash))
        {
            float w = S(260), h = S(24), x = Screen.width / 2f - w / 2, y = Screen.height * 0.42f;
            Para(new Rect(x, y, w, h), HudPanel);
            Fill(new Rect(x, y, S(3), h), HudAccent);
            T(new Rect(x, y, w, h), flash, 12, TextAnchor.MiddleCenter, Color.white);
        }
    }
    static bool Covered(string s) =>
        s.StartsWith("Best lap", System.StringComparison.Ordinal) || s.StartsWith("Lap ", System.StringComparison.Ordinal) ||
        s.StartsWith("Pit stop", System.StringComparison.Ordinal) || s.StartsWith("JUMP START", System.StringComparison.Ordinal);

    readonly HudSlot fpsSlot = new HudSlot();
    void DrawFps()
    {
        if (!showFps) return;
        T(new Rect(Screen.width - S(250), S(40), S(110), S(12)), fpsSlot.Fps(Mathf.RoundToInt(fpsAvg), renderScale < 0.99f ? Mathf.RoundToInt(renderScale * 100) : 0), 8.5f, TextAnchor.MiddleLeft, HudGrey, false);
    }
}

// Number strings for the HUD. Values that change every frame come from tables filled the first time a value is
// shown; values that change rarely (lap and sector times) are kept per place on screen and rebuilt only when they change.
static class HudNum
{
    static readonly string[] ints = new string[4001], tenths = new string[10000], gaps = new string[10000], deltas = new string[4001], clocks = new string[12000], pcts = new string[101], kws = new string[1001], poss = new string[41], laps = new string[200], lapsDown = new string[100];
    public static string Int(int v) => v >= 0 && v < ints.Length ? ints[v] ??= v.ToString() : v.ToString();
    public static string Tenths(double v) { int k = (int)System.Math.Round(v * 10); return k >= 0 && k < tenths.Length ? tenths[k] ??= (k / 10) + "." + (k % 10) : v.ToString("0.0"); }
    public static string Gap(double v) { int k = (int)System.Math.Round(v * 10); return k >= 0 && k < gaps.Length ? gaps[k] ??= "+" + (k / 10) + "." + (k % 10) : "+" + v.ToString("0"); }
    public static string Delta(double v)
    {
        int k = (int)System.Math.Round(v * 100); if (k > 2000) k = 2000; if (k < -2000) k = -2000;
        int a = k < 0 ? -k : k;
        return deltas[k + 2000] ??= (k < 0 ? "-" : "+") + (a / 100) + "." + (a % 100).ToString("00");
    }
    public static string Clock(double t)
    {
        int k = (int)System.Math.Floor(t * 10); if (k < 0) k = 0;
        if (k >= clocks.Length) return "--:--.-";
        return clocks[k] ??= (k / 600) + ":" + (k / 10 % 60).ToString("00") + "." + (k % 10);
    }
    public static string Pct(int v) { v = v < 0 ? 0 : v > 100 ? 100 : v; return pcts[v] ??= v + "%"; }
    public static string Kw(int v) { v = v < 0 ? 0 : v > 1000 ? 1000 : v; return kws[v] ??= v + " kW"; }
    public static string Pos(int p) => p >= 0 && p < poss.Length ? poss[p] ??= "P" + p : "P" + p;
    public static string LapShort(int n) => n >= 0 && n < laps.Length ? laps[n] ??= "L" + n : "L" + n;
    public static string LapsDown(int n) => n > 0 && n < lapsDown.Length ? lapsDown[n] ??= "+" + n + (n == 1 ? " LAP" : " LAPS") : "+LAPS";
}

// one string on screen that changes now and then: rebuilt only when its value changes
class HudSlot
{
    long key = long.MinValue; string s = "";
    public string Lap(double t)
    {
        long k = double.IsNaN(t) ? -1 : (long)System.Math.Round(t * 1000);
        if (k != key) { key = k; s = k < 0 ? "--:--.---" : (k / 60000) + ":" + (k / 1000 % 60).ToString("00") + "." + (k % 1000).ToString("000"); }
        return s;
    }
    public string Sec(double t)
    {
        long k = (long)System.Math.Round(t * 1000);
        if (k != key) { key = k; s = k >= 60000 ? (k / 60000) + ":" + (k / 1000 % 60).ToString("00") + "." + (k % 1000).ToString("000") : (k / 1000) + "." + (k % 1000).ToString("000"); }
        return s;
    }
    public string Lap(int lap, int of)
    {
        long k = lap * 10000L + of;
        if (k != key) { key = k; s = of > 0 ? "LAP " + lap + "/" + of : "LAP " + lap; }
        return s;
    }
    public string Fps(int fps, int res)
    {
        long k = fps * 1000L + res;
        if (k != key) { key = k; s = fps + " fps" + (res > 0 ? "  (" + res + "% res)" : ""); }
        return s;
    }
    public string Test(double? t100, double top, string unit)
    {
        long k = (t100.HasValue ? (long)System.Math.Round(t100.Value * 100) : 99999) * 100000L + (long)System.Math.Round(top) * 10 + (unit == "mph" ? 1 : 0);
        if (k != key) { key = k; s = "0-100 " + (t100.HasValue ? t100.Value.ToString("0.00") + "s" : "—") + " (real 2.2-2.6)  ·  top " + top.ToString("0") + " " + unit; }
        return s;
    }
}
