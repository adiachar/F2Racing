// RaceHud.cs — the in-race HUD, in the style of the 2025/26 F1 TV graphics and a modern onboard dash. Another part of
// GameManager (see GameManager.cs: OnGUI calls HudLayout, the buttons, then DrawRaceHud). IMGUI, drawn with the shared
// design system (UiKit.cs: Titillium Web, palette, rounded glass), no packages.
//  * timing tower (top left): position, team stripe, 3-letter code, gap to the leader or interval (tap the rows),
//    tyre, stops, fastest lap. Rows glide to their new place when positions change and flash green / red.
//    The chevron cycles compact (leader + the cars round you) -> full -> collapsed (just you)
//  * race card (top right): position P3/20, lap, lap clock, live delta to your best lap, three mini-sectors
//    (purple / green / yellow, with the time), last and best lap; the lap time flashes at the line
//  * dash (bottom centre): one slim glass bar: shift-light strip (green -> red -> blue), speed, big gear, battery
//    with deploy / harvest state, tyre quad (temperature colour, tread fill). Beside it, when there is room,
//    tyre / brake temperatures and fuel. Above it: X-MODE, TOW, YELLOW, BOX chips
//  * BOOST and OVERTAKE buttons (right, above the brake pedal): boost = full MGU-K power while held; Overtake Mode
//    (2026, replaces DRS) lights up when armed at a detection point, a tap uses it, the ring is the energy left
//  * local mini-map (GameManager.cs: DrawMiniMap) under the race card
//  * banners (top centre): fastest lap, pit stop, track limits, jump start, flags; they slide in and out
// Sector and gap bookkeeping is in Sectors.cs. Number strings come from tables filled once (HudNum) or are rebuilt only
// when their value changes (HudSlot); text widths are measured once per value, so drawing allocates nothing per frame.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    // ---------------------------------------------------------------- look (UiKit palette + HUD colours)
    static readonly Color HudAccent = new Color(0.882f, 0.024f, 0f);           // #E10600 (UiKit.Accent)
    static readonly Color HudPanel = new Color(0.045f, 0.05f, 0.068f, 0.84f), HudPanel2 = new Color(0.12f, 0.13f, 0.16f, 0.88f);
    static readonly Color HudPurple = new Color(0.66f, 0.33f, 0.97f), HudGreen = new Color(0.13f, 0.77f, 0.37f), HudYellow = new Color(0.98f, 0.80f, 0.08f);
    static readonly Color HudGrey = new Color(0.62f, 0.66f, 0.72f), HudBlue = new Color(0.25f, 0.55f, 1f), HudCyan = new Color(0f, 0.82f, 1f);
    static readonly Color HudRed = new Color(1f, 0.26f, 0.22f), HudAmber = new Color(1f, 0.66f, 0.12f), HudFaint = new Color(0.42f, 0.45f, 0.51f);
    static readonly Color[] SectorCols = { new Color(0.35f, 0.35f, 0.4f), new Color(0.98f, 0.80f, 0.08f), new Color(0.13f, 0.77f, 0.37f), new Color(0.66f, 0.33f, 0.97f) };
    static readonly string[] SetLetter = { "S", "M", "H", "I", "W" };
    static readonly Color[] SetCol = { new Color(0.93f, 0.16f, 0.16f), new Color(1f, 0.82f, 0.1f), new Color(0.95f, 0.95f, 0.95f), new Color(0.2f, 0.78f, 0.25f), new Color(0.15f, 0.48f, 0.98f) };
    static readonly string[] SectorWords = { "SECTOR 1", "SECTOR 2", "SECTOR 3" };
    // glass: a vertical gradient (cached texture: colours never change) and a hairline
    static readonly Color GlassTop = new Color(0.085f, 0.095f, 0.122f, 0.80f), GlassBot = new Color(0.03f, 0.035f, 0.05f, 0.86f), GlassLine = new Color(1f, 1f, 1f, 0.09f);
    static readonly Color MeLeft = new Color(0.882f, 0.024f, 0f, 0.85f), MeRight = new Color(0.882f, 0.024f, 0f, 0.18f);
    static readonly Color DimSeg = new Color(1f, 1f, 1f, 0.09f), Hair = new Color(1f, 1f, 1f, 0.07f);

    static Texture2D hudTri, icoGear, icoCam, icoBack;

    // ---------------------------------------------------------------- layout (once per OnGUI, from GameManager.OnGUI)
    float hudL, hudT, hudR, hudB; Rect cardRect, mapRect; float btnRight;

    // ---------------------------------------------------------------- timing state
    SectorTimer secTimer; Track secFor; readonly CarTiming myTime = new CarTiming(); CarTiming[] rivTime = new CarTiming[0]; DeltaTrace deltaTr;
    double hudPrevRace; int hudFrame = -1;
    bool towerFull, towerMin, towerInterval;
    float lapFlashT, sectorHoldT; double lapFlashTime; int lapFlashCol;   // 0 deleted, 1 yellow, 2 green, 3 purple
    double fastest = double.NaN; int fastestBy = -1, lapsTimed;          // fastestBy: 0 = you, i + 1 = rival i
    bool hudCutSeen, hudJumpSeen, hudYellow; int hudPitSeen; float yellowCheckT;
    readonly int[] ord = new int[40]; readonly double[] prog = new double[40]; int ordN;
    readonly int[] showRanks = new int[40];
    // tower animation per car (index = 0 you, i + 1 rival i): row y (units, from the first row), last rank, gain/loss flash
    readonly float[] rowY = new float[40], rowFx = new float[40]; readonly int[] rowSeen = new int[40], rowRank = new int[40];

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
        DrawRaceCard();
        DrawTower();
        DrawWheel(speed);
        DrawErsButtons();
        DrawBanners();
        DrawRcHud(); // VSC delta, safety car and blue-flag read-outs (GameManagerRaceControl.cs)
        DrawFps();
    }

    // where everything goes this frame: the safe area, the race card (top right), the buttons left of it, the map below
    void HudLayout()
    {
        var sa = UiKit.Safe;
        hudL = sa.x + S(8); hudT = sa.y + S(6); hudR = sa.xMax - S(8); hudB = sa.yMax - S(8);
        float cw = playground ? S(236) : S(150);
        float ch = playground ? S(42) : hudSectors ? S(85) : S(70);
        cardRect = new Rect(Mathf.Round(hudR - cw), hudT, cw, ch);
        btnRight = cardRect.x - S(8);
        float mw = S(84), mh = S(92); // local mini-map: taller than wide (300 m ahead, 150 m behind); ends above the gas pedal
        mapRect = new Rect(Mathf.Round(hudR - mw), cardRect.yMax + S(6), mw, mh);
    }

    void HudInit()
    {
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
        }
        if (icoGear == null) { icoGear = MakeIcon(0); icoCam = MakeIcon(1); icoBack = MakeIcon(2); }
    }

    // ---------------------------------------------------------------- icons, drawn once into small alpha textures
    // 0 settings cog, 1 camera, 2 circular arrow (restart / back on track). Signed distances, 1-pixel anti-aliasing.
    static Texture2D MakeIcon(int kind)
    {
        const int D = 64;
        var t = new Texture2D(D, D, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[D * D];
        for (int y = 0; y < D; y++) for (int x = 0; x < D; x++)
        {
            float u = (x + 0.5f) / D - 0.5f, v = (y + 0.5f) / D - 0.5f; // v up (texture row 0 is the bottom)
            float r = Mathf.Sqrt(u * u + v * v), a;
            if (kind == 0)
            {
                float ang = Mathf.Atan2(v, u), tooth = Mathf.Clamp01(Mathf.Cos(ang * 8f) * 2.4f + 0.5f);
                float outer = 0.345f + 0.11f * tooth * tooth * (3f - 2f * tooth);
                a = Mathf.Clamp01((outer - r) * D + 0.5f) * Mathf.Clamp01((r - 0.14f) * D + 0.5f);
            }
            else if (kind == 1)
            {
                float body = Mathf.Clamp01(-BoxSd(u, v + 0.035f, 0.42f, 0.25f, 0.075f) * D + 0.5f);
                float bump = Mathf.Clamp01(-BoxSd(u + 0.13f, v - 0.25f, 0.11f, 0.07f, 0.03f) * D + 0.5f);
                float lr = Mathf.Sqrt(u * u + (v + 0.035f) * (v + 0.035f));
                float ring = Mathf.Clamp01((0.185f - lr) * D + 0.5f) * Mathf.Clamp01((lr - 0.115f) * D + 0.5f);
                a = Mathf.Max(body, bump) * (1f - ring);
            }
            else
            {
                // a ring from 90 deg (top) clockwise round to 25 deg, with the arrow head at the top pointing right
                float ang = Mathf.Atan2(v, u) * Mathf.Rad2Deg; if (ang < 0) ang += 360f;
                float ringA = Mathf.Clamp01((0.355f - r) * D + 0.5f) * Mathf.Clamp01((r - 0.235f) * D + 0.5f);
                float outside = ang >= 25f && ang <= 90f ? -Mathf.Min(ang - 25f, 90f - ang) : Mathf.Min(Mathf.Abs(Mathf.DeltaAngle(ang, 25f)), Mathf.Abs(Mathf.DeltaAngle(ang, 90f)));
                ringA *= Mathf.Clamp01(outside * Mathf.Deg2Rad * r * D + 0.5f);
                float head = Mathf.Clamp01(TriSd(u, v, -0.02f, 0.445f, -0.02f, 0.145f, 0.17f, 0.295f) * D + 0.5f);
                a = Mathf.Max(ringA, head);
            }
            px[y * D + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(a)));
        }
        t.SetPixels32(px); t.Apply(false);
        return t;
    }
    // signed distance to a rounded box centred at the origin (half size hx, hy, corner radius rad); < 0 inside
    static float BoxSd(float x, float y, float hx, float hy, float rad)
    {
        float qx = Mathf.Abs(x) - (hx - rad), qy = Mathf.Abs(y) - (hy - rad);
        float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
        return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - rad;
    }
    // distance inside a triangle (a, b, c in either winding); > 0 inside
    static float TriSd(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float s = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax) >= 0 ? 1f : -1f;
        return Mathf.Min(Edge(px, py, ax, ay, bx, by) * s, Mathf.Min(Edge(px, py, bx, by, cx, cy) * s, Edge(px, py, cx, cy, ax, ay) * s));
    }
    static float Edge(float px, float py, float ax, float ay, float bx, float by)
    {
        float ex = bx - ax, ey = by - ay, l = Mathf.Max(1e-5f, Mathf.Sqrt(ex * ex + ey * ey));
        return (ex * (py - ay) - ey * (px - ax)) / l;
    }

    // ---------------------------------------------------------------- drawing helpers
    // text in layout units (same unit as S): bold = Titillium Bold, else SemiBold
    void T(Rect r, string s, float size, TextAnchor a, Color c, bool bold = true)
    {
        if (string.IsNullOrEmpty(s)) return;
        UiKit.Text(r, s, bold ? UiKit.Bold : UiKit.Semi, size, c, a);
    }
    static void TF(Rect r, string s, Font f, float size, Color c, TextAnchor a = TextAnchor.MiddleLeft)
    {
        if (string.IsNullOrEmpty(s)) return;
        UiKit.Text(r, s, f, size, c, a);
    }
    // rounded rect with its own radius per corner (layout units): top-left, top-right, bottom-right, bottom-left
    void RR(Rect r, Color c, float tl, float tr, float br, float bl)
    {
        if (c.a <= 0.001f || r.width <= 0 || r.height <= 0) return;
        float m = Mathf.Min(r.width, r.height) * 0.5f;
        GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c * GUI.color, new Vector4(0f, 0f, 0f, 0f),
            new Vector4(Mathf.Min(S(tl), m), Mathf.Min(S(tr), m), Mathf.Min(S(br), m), Mathf.Min(S(bl), m)));
    }
    // HUD glass: gradient body and a hairline, two draws
    static void Glass(Rect r, float radius)
    {
        UiKit.Gradient(r, GlassTop, GlassBot, radius);
        UiKit.Outline(r, GlassLine, radius, 0.6f);
    }
    static void Disc(Rect r, Color c) => UiKit.Rect(r, c, 999f);
    static Color Fade(float a) { var o = GUI.color; GUI.color = new Color(o.r, o.g, o.b, o.a * a); return o; }
    // F1 TV style block (kept for GameManagerRaceControl.cs): a flat colour block, rounded on the ends asked for
    void Para(Rect r, Color c, bool left = true, bool right = true)
    {
        float k = 6f;
        RR(r, c, left ? k : 0, right ? k : 0, right ? k : 0, left ? k : 0);
    }
    // kind: 0 lower-left, 1 lower-right, 2 upper-left, 3 upper-right half of r filled
    static readonly Rect[] TriUv = { new Rect(0, 0, 1, 1), new Rect(1, 0, -1, 1), new Rect(0, 1, 1, -1), new Rect(1, 1, -1, -1) };
    void Tri(Rect r, int kind, Color c) { var o = GUI.color; GUI.color = c * o; GUI.DrawTextureWithTexCoords(r, hudTri, TriUv[kind]); GUI.color = o; }
    void Tex(Rect r, Texture2D t, Color c) { if (t == null) return; var o = GUI.color; GUI.color = c * o; GUI.DrawTexture(r, t); GUI.color = o; }
    // a small chevron: down (v) or up (^)
    void Chevron(Rect r, bool down, Color c)
    {
        float h = r.width * 0.5f;
        var a = new Rect(r.x, r.center.y - h * 0.5f, h, h); var b = new Rect(r.x + h, r.center.y - h * 0.5f, h, h);
        if (down) { Tri(a, 3, c); Tri(b, 2, c); } else { Tri(a, 1, c); Tri(b, 0, c); }
    }
    // tyre compound: a thin ring in the compound colour with its letter
    void TyreIcon(Rect r, int set)
    {
        set = Mathf.Clamp(set, 0, SetLetter.Length - 1);
        UiKit.Outline(r, SetCol[set], 999f, 1.1f);
        TF(r, SetLetter[set], UiKit.Bold, r.height / S(1) * 0.56f, SetCol[set], TextAnchor.MiddleCenter);
    }
    static Color TempCol(double t, double opt, double win)
    {
        if (double.IsNaN(t)) return HudGrey;
        float k = (float)((t - opt) / win);
        if (k < -1) return HudBlue;
        if (k < -0.35f) return Color.Lerp(HudBlue, HudGreen, (k + 1) / 0.65f);
        if (k < 0.35f) return HudGreen;
        if (k < 1) return Color.Lerp(HudGreen, HudYellow, (k - 0.35f) / 0.65f);
        return Color.Lerp(HudYellow, HudRed, Mathf.Clamp01(k - 1));
    }

    // a touch button: fires on release inside, reports how pressed it looks (0..1). Same handling as UiKit.Button.
    static bool HudButton(Rect r, out float press)
    {
        int id = GUIUtility.GetControlID(FocusType.Passive, r);
        var e = Event.current; bool clicked = false;
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; e.Use(); }
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == id) e.Use();
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == id)
                {
                    GUIUtility.hotControl = 0;
                    if (r.Contains(e.mousePosition)) clicked = true;
                    e.Use();
                }
                break;
        }
        bool down = GUIUtility.hotControl == id && r.Contains(e.mousePosition);
        press = UiKit.Anim(id, down ? 1f : 0f, 20f);
        return clicked;
    }
    // round glass icon button (the hit area is a little bigger than the disc)
    bool RoundBtn(Rect r, Texture2D icon, bool on = false)
    {
        var hit = new Rect(r.x - S(3), r.y - S(3), r.width + S(6), r.height + S(6));
        bool c = HudButton(hit, out float p);
        var rr = UiKit.Scale(r, 1f - 0.07f * p);
        Disc(rr, Color.Lerp(on ? new Color(0.55f, 0.03f, 0f, 0.85f) : new Color(0.05f, 0.06f, 0.08f, 0.66f), new Color(0.18f, 0.19f, 0.23f, 0.88f), p));
        UiKit.Outline(rr, new Color(1f, 1f, 1f, 0.13f + 0.15f * p), 999f, 0.6f);
        Tex(UiKit.Scale(rr, 0.56f), icon, new Color(1f, 1f, 1f, 0.92f));
        return c;
    }
    // glass pill button with an optional icon and an accent colour
    bool PillBtn(Rect r, string label, Texture2D icon, Color accent, bool filled = false, float size = 8f)
    {
        bool c = HudButton(r, out float p);
        var rr = UiKit.Scale(r, 1f - 0.04f * p);
        if (filled) Disc(rr, Color.Lerp(accent, Color.white, 0.12f * p));
        else
        {
            Disc(rr, Color.Lerp(new Color(0.05f, 0.06f, 0.08f, 0.78f), new Color(0.16f, 0.17f, 0.21f, 0.9f), p));
            UiKit.Outline(rr, new Color(accent.r, accent.g, accent.b, 0.55f + 0.3f * p), 999f, 0.8f);
        }
        float ix = 0;
        if (icon != null) { float s = rr.height * 0.5f; ix = s + S(4); Tex(new Rect(rr.x + rr.height * 0.42f, rr.center.y - s * 0.5f, s, s), icon, filled ? Color.white : accent); }
        TF(new Rect(rr.x + ix * 0.5f, rr.y, rr.width, rr.height), label, UiKit.Bold, size, Color.white, TextAnchor.MiddleCenter);
        return c;
    }
    // small status chip (X-MODE, TOW...)
    void Chip(Rect r, string s, Color bg, Color fg)
    {
        Disc(r, bg);
        TF(r, s, UiKit.Bold, 6.5f, fg, TextAnchor.MiddleCenter);
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
        if (pitStops > hudPitSeen) Banner("PIT STOP", (PitLaneOn ? pitStopLen.ToString("0.0") + " s STOP" : "+" + pitStopLen.ToString("0.0") + " s") + "  ·  " + Weather.TyreName(carSet).ToUpperInvariant(), HudAccent);
        hudPitSeen = pitStops;
        if (jumped && !hudJumpSeen) Banner("JUMP START", "+" + JumpPenalty.ToString("0") + " s TIME PENALTY", HudAccent);
        hudJumpSeen = jumped;
        // Overtake Mode armed at a detection point
        if (car.otArmed && !otBannerSeen) Banner("OVERTAKE AVAILABLE", "TAP OVERTAKE  ·  +0.5 MJ", HudPurple);
        otBannerSeen = car.otArmed;

        // yellow flag: a stopped car up to 400 m ahead of you
        yellowCheckT -= dt;
        if (yellowCheckT <= 0)
        {
            yellowCheckT = 0.3f;
            bool y = false; int ySector = 0;
            if (raceTime > Countdown + 4)
                for (int i = 0; i < rivals.Count && !y; i++)
                {
                    var c = rivals[i].car; if (rivals[i].idx < 0 || rivals[i].InPit) continue; // (a car in its pit box is not stopped on track)
                    double ahead = ((rivals[i].idx - idx + track.N) % track.N) * track.ds;
                    if (c.vx * c.vx + c.vy * c.vy < 36 && ahead > 5 && ahead < 400) { y = true; ySector = secTimer.SectorOf(rivals[i].idx); }
                }
            if (y && !hudYellow) Banner("YELLOW FLAG", SectorWords[ySector], HudYellow);
            hudYellow = y;
        }
        RcHudTick(); // safety car, VSC, green flag, track-limits strikes, penalties (GameManagerRaceControl.cs)
    }

    void HudReset(bool keepBest)
    {
        if (keepBest) { myTime.AbortLap(); if (deltaTr != null) deltaTr.Abort(); }
        else { myTime.Clear(); if (deltaTr != null) deltaTr.Clear(); if (secTimer != null) secTimer.ResetBests(); fastest = double.NaN; fastestBy = -1; lapsTimed = 0; }
        if (keepBest && fastestBy > 0) { fastest = myTime.bestLap; fastestBy = double.IsNaN(fastest) ? -1 : 0; }
        foreach (var r in rivTime) r.Clear();
        hudPitSeen = pitStops; hudJumpSeen = jumped; hudCutSeen = cut; hudYellow = false; bCount = 0; lapFlashT = sectorHoldT = 0;
        for (int i = 0; i < rowSeen.Length; i++) { rowSeen[i] = -100; rowFx[i] = 0; }
    }

    CarTiming TimingOf(int e) => e == 0 ? myTime : rivTime[e - 1];
    double Penalty(int e) => e == 0 ? pitLoss : 0;
    int SetOf(int e) => e == 0 ? carSet : rivals[e - 1].tyreSet;
    int StopsOf(int e) => e == 0 ? pitStops : rivals[e - 1].pitStops;

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
        return who != null && codes.TryGetValue(who.name, out var s) ? s : "???";
    }

    // ---------------------------------------------------------------- timing tower (top left)
    readonly HudSlot lapTag = new HudSlot();
    void DrawTower()
    {
        if (!hudTower || playground || rivals.Count == 0 || ordN == 0 || quali != null) return; // qualifying draws its own list (Qualifying.cs)
        float x = hudL, y = hudT, w = S(136), rh = S(15), hh = S(19);
        float bottom = tilt ? BrakeRect.y - S(8) : Screen.height - S(48);

        // which ranks: all that fit, else the top of the field and the cars round you; collapsed: just you
        int myRank = 0; for (int r = 0; r < ordN; r++) if (ord[r] == 0) { myRank = r; break; }
        int n = 0;
        if (towerMin) showRanks[n++] = myRank;
        else
        {
            int cap = towerFull ? Mathf.Max(4, (int)((bottom - y - hh) / rh)) : 5;
            if (ordN <= cap) for (int r = 0; r < ordN; r++) showRanks[n++] = r;
            else
            {
                int top = cap - 3, lo = Mathf.Clamp(myRank - 1, 0, ordN - 3);
                if (myRank < cap - 1) { lo = cap; top = cap; }
                for (int r = 0; r < top; r++) showRanks[n++] = r;
                for (int r = lo; r < lo + 3 && r < ordN && n < cap; r++) if (r >= top) showRanks[n++] = r;
            }
        }
        int breaks = 0; for (int k = 1; k < n; k++) if (showRanks[k - 1] != showRanks[k] - 1) breaks++;
        float rowsTop = y + hh + S(2), total = hh + S(4) + n * rh + breaks * S(5);

        // panel and header: red lap tag, GAP / INT, chevron
        Glass(new Rect(x, y, w, total), 8f);
        float tagW = S(62);
        RR(new Rect(x, y, tagW, hh), HudAccent, 8, 0, 0, 0);
        int lapShown = raceLaps > 0 ? Mathf.Clamp(pLap, 1, raceLaps) : lapNo;
        TF(new Rect(x, y, tagW, hh), lapTag.Lap(lapShown, raceLaps), UiKit.Bold, 8f, Color.white, TextAnchor.MiddleCenter);
        TF(new Rect(x + tagW + S(7), y, S(50), hh), towerInterval ? "INTERVAL" : "GAP", UiKit.Semi, 6.5f, HudGrey);
        Chevron(new Rect(x + w - S(15), y + hh * 0.5f - S(3), S(8), S(6)), !towerFull || towerMin, new Color(1f, 1f, 1f, 0.8f));
        UiKit.Rect(new Rect(x, y + hh, w, Mathf.Max(1f, S(0.5f))), Hair);
        if (GUI.Button(new Rect(x + tagW, y, w - tagW - S(26), hh), GUIContent.none, GUIStyle.none)) towerInterval = !towerInterval;
        if (GUI.Button(new Rect(x + w - S(28), y - S(4), S(32), hh + S(8)), GUIContent.none, GUIStyle.none))
        {
            if (towerMin) { towerMin = false; towerFull = false; } else if (towerFull) towerMin = true; else towerFull = true; // compact -> full -> collapsed
        }

        // rows (each glides to its place; a gain / loss flashes the row green / red)
        bool rep = Event.current.type == EventType.Repaint; float dt = Time.unscaledDeltaTime;
        float ty = 0; int frame = Time.frameCount;
        for (int k = 0; k < n; k++)
        {
            int r = showRanks[k], e = ord[r];
            if (k > 0 && showRanks[k - 1] != r - 1)
            {
                UiKit.Rect(new Rect(x + S(8), rowsTop + ty + S(2), w - S(16), Mathf.Max(1f, S(0.6f))), new Color(1f, 1f, 1f, 0.14f));
                ty += S(5);
            }
            if (e >= rowY.Length) { ty += rh; continue; }
            float tyU = ty / S(1);
            if (rep)
            {
                if (frame - rowSeen[e] > 3) { rowY[e] = tyU; rowFx[e] = 0; }
                else
                {
                    rowY[e] += (tyU - rowY[e]) * Mathf.Min(1f, dt * 12f);
                    if (rowRank[e] != r) rowFx[e] = r < rowRank[e] ? 1f : -1f;
                }
                rowRank[e] = r; rowSeen[e] = frame;
                rowFx[e] = Mathf.MoveTowards(rowFx[e], 0f, dt * 0.45f);
            }
            float ry = rowsTop + S(rowY[e]);
            DrawTowerRow(x, ry, w, rh, r, e);
            ty += rh;
        }
        if (GUI.Button(new Rect(x, rowsTop, w, total - hh), GUIContent.none, GUIStyle.none)) towerInterval = !towerInterval; // tap the rows: gap / interval
    }

    void DrawTowerRow(float x, float y, float w, float rh, int r, int e)
    {
        bool me = e == 0;
        var row = new Rect(x + S(1), y, w - S(2), rh);
        if (me) UiKit.Gradient(row, MeLeft, MeRight, 0f, true);
        float fx = rowFx[e];
        if (fx > 0.01f) UiKit.Rect(row, new Color(HudGreen.r, HudGreen.g, HudGreen.b, 0.38f * fx));
        else if (fx < -0.01f) UiKit.Rect(row, new Color(HudRed.r, HudRed.g, HudRed.b, -0.38f * fx));
        TF(new Rect(x, y, S(18), rh), HudNum.Int(r + 1), UiKit.Bold, 8.5f, Color.white, TextAnchor.MiddleCenter);
        Color team = me ? new Color(1f, 0.25f, 0.2f) : new Color(rivals[e - 1].who.r, rivals[e - 1].who.g, rivals[e - 1].who.b);
        UiKit.Rect(new Rect(x + S(19), y + S(3.5f), S(2.2f), rh - S(7)), team, 1f);
        TF(new Rect(x + S(25), y, S(34), rh), me ? "YOU" : Code(rivals[e - 1].who), UiKit.Bold, 8.5f, Color.white);
        string gap = GapText(r);
        if (r == 0) TF(new Rect(x + S(56), y, S(48), rh), gap, UiKit.Semi, 6.2f, me ? Color.white : HudGrey, TextAnchor.MiddleRight);
        else TF(new Rect(x + S(56), y, S(48), rh), gap, UiKit.Semi, 7.8f, Color.white, TextAnchor.MiddleRight);
        TyreIcon(new Rect(x + S(108), y + S(2.5f), rh - S(5), rh - S(5)), SetOf(e));
        if (fastestBy == e)
        {
            var fl = new Rect(x + S(120), y + S(3), S(13), rh - S(6));
            UiKit.Rect(fl, HudPurple, 2f);
            TF(fl, "FL", UiKit.Bold, 5.2f, Color.white, TextAnchor.MiddleCenter);
        }
        else
        {
            int stops = StopsOf(e);
            if (stops > 0) TF(new Rect(x + S(120), y, S(12), rh), HudNum.Int(stops), UiKit.Semi, 6.5f, HudGrey, TextAnchor.MiddleCenter);
        }
    }

    // ---------------------------------------------------------------- race card (top right)
    readonly HudSlot lastSlot = new HudSlot(), bestSlot = new HudSlot(), flashSlot = new HudSlot(), testSlot = new HudSlot(), lapFrac = new HudSlot();
    readonly HudSlot[] secSlot = { new HudSlot(), new HudSlot(), new HudSlot() };
    readonly float[] posW = new float[41]; int posWH;
    float PosWidth(int p)
    {
        if (posWH != Screen.height) { System.Array.Clear(posW, 0, posW.Length); posWH = Screen.height; }
        if (p < 0 || p >= posW.Length) return S(30);
        if (posW[p] <= 0) posW[p] = UiKit.Width(HudNum.Pos(p), UiKit.Black, 21f);
        return posW[p];
    }

    void DrawRaceCard()
    {
        var c = cardRect; float pad = S(9), x = c.x + pad, iw = c.width - 2 * pad, y = c.y;
        Glass(c, 10f);
        if (playground)
        {
            UiKit.Rect(new Rect(c.x, c.y + S(8), S(2.5f), c.height - S(16)), HudAccent, 1f);
            TF(new Rect(x, y + S(5), iw, S(14)), "TEST DATA", UiKit.Bold, 8f, Color.white);
            TF(new Rect(x, y + S(5), iw, S(14)), "OURS VS REAL", UiKit.Semi, 6f, HudFaint, TextAnchor.MiddleRight);
            TF(new Rect(x, y + S(21), iw, S(14)), testSlot.Test(t100, topSpeed * (unitsIdx == 1 ? 0.621371 : 1), SpeedUnit), UiKit.Semi, 7.2f, HudGrey);
            return;
        }

        // row A: position (or lap in practice) and lap counter
        int pos = 0, of = 0;
        if (quali != null) { of = qList.Count; for (int i = 0; i < qList.Count; i++) if (qList[i].Player) { pos = i + 1; break; } }
        else if (rivals.Count > 0) { pos = Position(); of = rivals.Count + 1; }
        if (pos > 0)
        {
            var pr = new Rect(x, y + S(3), S(70), S(28));
            TF(pr, HudNum.Pos(pos), UiKit.Black, 21f, Color.white);
            TF(new Rect(x + PosWidth(pos) + S(1), y + S(8), S(30), S(22)), HudNum.Of(of), UiKit.Semi, 9.5f, HudFaint);
        }
        bool race = raceLaps > 0;
        if (pos > 0)
        {
            int lapShown = race ? Mathf.Clamp(pLap, 1, raceLaps) : lapNo;
            TF(new Rect(x, y + S(5), iw, S(9)), "LAP", UiKit.Semi, 6f, HudFaint, TextAnchor.MiddleRight);
            TF(new Rect(x, y + S(12), iw, S(18)), lapFrac.Frac(lapShown, race ? raceLaps : 0), UiKit.Bold, 12f, Color.white, TextAnchor.MiddleRight);
        }
        else
        {
            // alone on track: the lap counter is the big number
            TF(new Rect(x, y + S(5), S(40), S(9)), "LAP", UiKit.Semi, 6f, HudFaint);
            TF(new Rect(x, y + S(11), S(80), S(21)), race ? lapFrac.Frac(Mathf.Clamp(pLap, 1, raceLaps), raceLaps) : HudNum.Int(lapNo), UiKit.Black, 18f, Color.white);
            TF(new Rect(x, y + S(5), iw, S(9)), race ? "RACE" : "PRACTICE", UiKit.Semi, 6f, HudFaint, TextAnchor.MiddleRight);
        }
        UiKit.Rect(new Rect(x, y + S(33), iw, Mathf.Max(1f, S(0.5f))), Hair);

        // row B: lap clock (the lap just done flashes at the line) and the live delta
        var clock = new Rect(x, y + S(35), S(90), S(17));
        if (lapFlashT > 0)
        {
            bool on = lapFlashT > 2.4f ? Mathf.Repeat(lapFlashT * 4, 1) > 0.35f : true;
            Color fc = lapFlashCol == 0 ? HudGrey : SectorCols[lapFlashCol];
            if (on) UiKit.Rect(new Rect(clock.x - S(4), clock.y + S(1), S(76), clock.height - S(2)), new Color(fc.r, fc.g, fc.b, 0.22f), 4f);
            TF(clock, flashSlot.Lap(lapFlashTime), UiKit.Bold, 12.5f, lapFlashCol == 0 ? HudGrey : fc);
            if (lapFlashCol == 0) TF(new Rect(x, clock.y, iw, clock.height), "DELETED", UiKit.Bold, 6.5f, HudRed, TextAnchor.MiddleRight);
        }
        else
        {
            bool invalid = cut && lapStart.HasValue;
            TF(clock, lapStart.HasValue ? HudNum.Clock(simTime - lapStart.Value) : "--:--.-", UiKit.Bold, 12.5f, invalid ? HudGrey : Color.white);
            bool deltaShown = false;
            if (hudDelta && deltaTr != null && lapStart.HasValue)
            {
                double dl = deltaTr.Delta(idx, simTime - lapStart.Value);
                if (!double.IsNaN(dl))
                {
                    SteeringWheel.SetDelta(dl); // the same delta on the steering wheel's display
                    Color dc = dl <= 0 ? HudGreen : HudRed;
                    var dr = new Rect(x + iw - S(46), clock.y + S(2), S(46), clock.height - S(4));
                    UiKit.Rect(dr, new Color(dc.r, dc.g, dc.b, 0.20f), 4f);
                    TF(dr, HudNum.Delta(dl), UiKit.Bold, 8.5f, dc, TextAnchor.MiddleCenter);
                    deltaShown = true;
                }
            }
            if (invalid && !deltaShown) TF(new Rect(x, clock.y, iw, clock.height), "INVALID", UiKit.Bold, 6.5f, HudRed, TextAnchor.MiddleRight);
        }
        float yy = y + S(55);

        // row C: mini-sectors (the lap just done is held for a few seconds after the line)
        if (hudSectors)
        {
            bool hold = sectorHoldT > 0;
            float g = S(3), sw = (iw - 2 * g) / 3f, sh = S(11);
            int curSec = secTimer != null ? secTimer.SectorOf(Mathf.Max(idx, 0)) : 0;
            float pulse = 0.18f + 0.14f * Mathf.Sin(Time.unscaledTime * 6f);
            for (int s = 0; s < 3; s++)
            {
                var r = new Rect(x + s * (sw + g), yy, sw, sh);
                double t = double.NaN; int col = 0;
                if (secTimer != null) { t = hold ? myTime.last[s] : myTime.cur[s]; col = hold ? myTime.lastCol[s] : myTime.curCol[s]; }
                if (double.IsNaN(t))
                {
                    bool live = !hold && s == curSec && lapStart.HasValue;
                    UiKit.Rect(r, live ? new Color(1f, 1f, 1f, pulse) : DimSeg, 3f);
                }
                else
                {
                    Color sc = col >= 1 ? SectorCols[col] : new Color(0.55f, 0.58f, 0.64f);
                    UiKit.Rect(r, sc, 3f);
                    TF(r, secSlot[s].Sec(t), UiKit.Bold, 6.4f, col == 3 ? Color.white : new Color(0.05f, 0.05f, 0.06f), TextAnchor.MiddleCenter);
                }
            }
            yy += sh + S(4);
        }
        // row D: last and best
        float half = iw * 0.5f;
        TF(new Rect(x, yy, S(24), S(10)), "LAST", UiKit.Semi, 5.6f, HudFaint);
        // LAST: grey when the lap was invalid (still timed), purple = the session's fastest, green = your best, else white
        Color lc = !lastValid ? HudGrey : lastLap.HasValue && bestLap.HasValue && lastLap.Value == bestLap.Value ? (fastestBy == 0 ? HudPurple : HudGreen) : Color.white;
        TF(new Rect(x, yy, half - S(3), S(10)), lastSlot.Lap(lastLap ?? double.NaN), UiKit.Semi, 7f, lc, TextAnchor.MiddleRight);
        if (!lastValid && lastLap.HasValue) UiKit.Rect(new Rect(x + half - S(3) - S(34), yy + S(5), S(34), Mathf.Max(1f, S(0.8f))), HudGrey, 0f); // struck through
        TF(new Rect(x + half + S(3), yy, S(24), S(10)), "BEST", UiKit.Semi, 5.6f, HudFaint);
        TF(new Rect(x + half, yy, half, S(10)), bestSlot.Lap(bestLap ?? double.NaN), UiKit.Semi, 7f, fastestBy == 0 ? HudPurple : HudGreen, TextAnchor.MiddleRight);
    }

    // ---------------------------------------------------------------- dash (bottom centre)
    void DrawWheel(float speed)
    {
        float dw = S(250), dh = S(52);
        var dash = new Rect(Mathf.Round(Screen.width * 0.5f - dw * 0.5f), Mathf.Round(hudB - dh), dw, dh);
        float dx = dash.x, dy = dash.y;
        float wingW = S(70), gap = S(5);
        float rightLimit = tilt ? GasRect.x : BrakeRect.x, leftLimit = tilt ? BrakeRect.xMax : hudL + S(160);
        bool wings = hudInfo && dash.xMax + gap + wingW + S(8) < rightLimit && dash.x - gap - wingW - S(8) > leftLimit;

        // chips above: X-mode, slipstream, yellow flag, box
        float chipY = dy - S(17), chipH = S(13), cx = dx + S(4);
        if (car.aeroX > 0.5) { Chip(new Rect(cx, chipY, S(40), chipH), "X-MODE", HudCyan, Color.black); cx += S(44); }
        if (car.tow > 0.3) { Chip(new Rect(cx, chipY, S(30), chipH), "TOW", HudPanel2, HudCyan); cx += S(34); }
        if (hudYellow) { Chip(new Rect(cx, chipY, S(42), chipH), "YELLOW", HudYellow, Color.black); cx += S(46); }
        if (boxArmed) Chip(new Rect(cx, chipY, S(30), chipH), "BOX", HudAccent, Color.white);
        if (hudInfo && !wings && !double.IsNaN(car.fuel))
        {
            TF(new Rect(dx, chipY, dw - S(6), chipH), HudNum.Tenths(car.fuel), UiKit.Bold, 7.5f, Color.white, TextAnchor.MiddleRight);
            TF(new Rect(dx, chipY, dw - S(28), chipH), "FUEL", UiKit.Semi, 6f, HudFaint, TextAnchor.MiddleRight);
        }

        Glass(dash, 12f);
        // shift lights: 5 green, 5 red, 5 blue; all blink blue at the limiter
        double red = car.cfg.redline, frac = (car.rpm - red * 0.62) / (red * 0.36);
        int lit = Mathf.Clamp(Mathf.CeilToInt((float)frac * 15), 0, 15);
        bool limiter = car.rpm > red * 0.985 && !car.reverse;
        bool blinkOn = Mathf.Repeat(Time.unscaledTime * 10, 1) > 0.5f;
        float sx0 = dx + S(12), sg = S(2.4f), segW = (dw - S(24) - 14 * sg) / 15f, segH = S(3.6f);
        for (int i = 0; i < 15; i++)
        {
            Color c = i < 5 ? HudGreen : i < 10 ? HudRed : HudBlue;
            bool on = limiter ? blinkOn : i < lit;
            UiKit.Rect(new Rect(sx0 + i * (segW + sg), dy + S(5), segW, segH), on ? (limiter ? HudBlue : c) : DimSeg, 2f);
        }

        // speed (left)
        TF(new Rect(dx + S(8), dy + S(11), S(66), S(27)), HudNum.Int(Mathf.RoundToInt(speed * (unitsIdx == 1 ? 2.23694f : 3.6f))), UiKit.Black, 23f, Color.white, TextAnchor.MiddleRight);
        TF(new Rect(dx + S(8), dy + S(37), S(66), S(9)), unitsIdx == 1 ? "MPH" : "KM/H", UiKit.Semi, 5.8f, HudFaint, TextAnchor.MiddleRight);
        Color off = new Color(1f, 1f, 1f, 0.16f);
        TF(new Rect(dx + S(11), dy + S(37), S(14), S(9)), "TC", UiKit.Bold, 5.8f, car.tcActive ? HudAmber : off);
        TF(new Rect(dx + S(22), dy + S(37), S(16), S(9)), "ABS", UiKit.Bold, 5.8f, car.absActive ? HudAmber : off);
        if (car.spinR) TF(new Rect(dx + S(37), dy + S(37), S(18), S(9)), "SPIN", UiKit.Bold, 5.8f, HudRed);
        else if (car.lockF || car.lockR) TF(new Rect(dx + S(37), dy + S(37), S(18), S(9)), "LOCK", UiKit.Bold, 5.8f, HudRed);

        // gear (middle)
        var gb = new Rect(dx + S(80), dy + S(12), S(42), S(36));
        UiKit.Rect(gb, new Color(0f, 0f, 0f, 0.36f), 8f);
        UiKit.Outline(gb, limiter ? new Color(HudBlue.r, HudBlue.g, HudBlue.b, 0.8f) : new Color(1f, 1f, 1f, 0.07f), 8f, 0.7f);
        TF(new Rect(gb.x, gb.y - S(1.5f), gb.width, gb.height), car.reverse ? "R" : car.gear <= 0 ? "N" : HudNum.Int(car.gear), UiKit.Black, 29f, limiter ? new Color(0.5f, 0.62f, 1f) : Color.white, TextAnchor.MiddleCenter);

        // battery (right of the gear)
        float bx = dx + S(130), bw = S(62);
        float soc = double.IsNaN(car.ersEnergy) ? 1f : Mathf.Clamp01((float)(car.ersEnergy / car.cfg.ersCapacity));
        double kw = double.IsNaN(car.ersKw) ? 0 : car.ersKw;
        string mode; Color mc;
        if (car.otActive && kw > 5) { mode = "OVERTAKE"; mc = HudPurple; }
        else if (car.boostActive && kw > 5) { mode = "BOOST"; mc = HudCyan; }
        else if (kw > 5) { mode = "DEPLOY"; mc = HudBlue; }
        else if (kw < -5 && car.throttle > 0.5) { mode = "SUPERCLIP"; mc = HudAmber; }
        else if (kw < -5) { mode = "HARVEST"; mc = HudGreen; }
        else { mode = ""; mc = HudGrey; }
        TF(new Rect(bx, dy + S(12), bw, S(9)), "ERS", UiKit.Semi, 5.8f, HudFaint);
        TF(new Rect(bx, dy + S(12), bw, S(9)), HudNum.Pct(Mathf.RoundToInt(soc * 100)), UiKit.Bold, 7.2f, Color.white, TextAnchor.MiddleRight);
        var bar = new Rect(bx, dy + S(23), bw, S(5));
        UiKit.Rect(bar, DimSeg, 2.5f);
        Color fill = soc < 0.15f ? HudRed : mode.Length == 0 ? new Color(0.82f, 0.86f, 0.92f) : mc;
        if (soc > 0.005f) UiKit.Rect(new Rect(bar.x, bar.y, Mathf.Max(bar.height, bw * soc), bar.height), fill, 2.5f);
        if (mode.Length > 0)
        {
            TF(new Rect(bx, dy + S(31), bw, S(9)), mode, UiKit.Bold, 6.2f, mc);
            TF(new Rect(bx, dy + S(31), bw, S(9)), HudNum.Kw(Mathf.RoundToInt((float)System.Math.Abs(kw))), UiKit.Semi, 5.8f, HudGrey, TextAnchor.MiddleRight);
        }

        // tyre quad (temperature colour, the fill is the tread left) and the compound
        var cd = CompoundTable.Defs[Mathf.Clamp((int)car.cfg.compound, 0, CompoundTable.Defs.Length - 1)];
        bool temps = car.cfg.chassis && !double.IsNaN(car.tyreTF);
        float tw = S(9), th = S(14), lx = dx + S(201), rx = dx + S(231), fy = dy + S(12), ry = dy + S(33);
        for (int wh = 0; wh < 4; wh++)
        {
            bool front = wh < 2, left = (wh & 1) == 0;
            var tr = new Rect(left ? lx : rx, front ? fy : ry, tw, th);
            Color tc = temps ? TempCol(front ? car.tyreTF : car.tyreTR, cd.opt, cd.window) : HudGrey;
            float tread = temps ? Mathf.Clamp01(1 - (float)(front ? car.wearF : car.wearR)) : 1f;
            UiKit.Rect(tr, new Color(tc.r, tc.g, tc.b, 0.26f), 3f);
            if (tread > 0.02f) UiKit.Rect(new Rect(tr.x, tr.yMax - th * tread, tw, th * tread), tread < 0.3f ? HudRed : tc, 3f);
        }
        TyreIcon(new Rect(dx + S(214.5f), dy + S(23.5f), S(13), S(13)), carSet);
        if (!wings) return;

        // left wing: tyre temperatures and tread, brake temperatures
        var lw = new Rect(dx - gap - wingW, dy, wingW, dh);
        Glass(lw, 12f);
        if (temps)
        {
            float bw2 = S(15), bh2 = S(18), mx = lw.center.x;
            for (int wh = 0; wh < 4; wh++)
            {
                bool front = wh < 2, left = (wh & 1) == 0;
                double t = front ? car.tyreTF : car.tyreTR, wear = front ? car.wearF : car.wearR;
                var br = new Rect(left ? lw.x + S(8) : lw.xMax - S(8) - bw2, front ? dy + S(6) : dy + S(28), bw2, bh2);
                UiKit.Rect(br, TempCol(t, cd.opt, cd.window), 3.5f);
                TF(br, HudNum.Int(Mathf.RoundToInt((float)t)), UiKit.Bold, 6.2f, new Color(0.04f, 0.04f, 0.05f), TextAnchor.MiddleCenter);
                float tread = Mathf.Clamp01(1 - (float)wear), wx = left ? br.x - S(4) : br.xMax + S(2);
                UiKit.Rect(new Rect(wx, br.y, S(2), bh2), DimSeg, 1f);
                UiKit.Rect(new Rect(wx, br.yMax - bh2 * tread, S(2), bh2 * tread), tread < 0.3f ? HudRed : Color.white, 1f);
            }
            if (!double.IsNaN(car.brakeTF))
            {
                Color fc = TempCol(car.brakeTF, 650, 380), rc = TempCol(car.brakeTR, 650, 380);
                UiKit.Rect(new Rect(mx - S(8), dy + S(9), S(16), S(2)), fc, 1f);
                TF(new Rect(mx - S(12), dy + S(12), S(24), S(9)), HudNum.Int(Mathf.RoundToInt((float)car.brakeTF)), UiKit.Semi, 5.6f, fc, TextAnchor.MiddleCenter);
                UiKit.Rect(new Rect(mx - S(8), dy + S(42), S(16), S(2)), rc, 1f);
                TF(new Rect(mx - S(12), dy + S(32), S(24), S(9)), HudNum.Int(Mathf.RoundToInt((float)car.brakeTR)), UiKit.Semi, 5.6f, rc, TextAnchor.MiddleCenter);
                TF(new Rect(mx - S(12), dy + S(22), S(24), S(8)), "BRK", UiKit.Semi, 4.8f, HudFaint, TextAnchor.MiddleCenter);
            }
        }
        else TF(lw, "TYRES", UiKit.Semi, 6f, HudFaint, TextAnchor.MiddleCenter);

        // right wing: fuel, laps it lasts, tyre set
        var rw = new Rect(dash.xMax + gap, dy, wingW, dh);
        Glass(rw, 12f);
        float px0 = rw.x + S(8), pw = wingW - S(16);
        if (!double.IsNaN(car.fuel))
        {
            TF(new Rect(px0, dy + S(5), pw, S(12)), "FUEL", UiKit.Semi, 5.8f, HudFaint);
            TF(new Rect(px0, dy + S(4), pw, S(14)), HudNum.Tenths(car.fuel), UiKit.Bold, 10f, Color.white, TextAnchor.MiddleRight);
            float ff = Mathf.Clamp01((float)(car.fuel / System.Math.Max(1, car.cfg.fuelStart)));
            UiKit.Rect(new Rect(px0, dy + S(19), pw, S(3)), DimSeg, 1.5f);
            if (ff > 0.01f) UiKit.Rect(new Rect(px0, dy + S(19), Mathf.Max(S(3), pw * ff), S(3)), ff < 0.1f ? HudRed : new Color(0.85f, 0.88f, 0.92f), 1.5f);
            if (!double.IsNaN(car.fuelLastLap) && car.fuelLastLap > 0.05)
            {
                double laps = car.fuel / car.fuelLastLap; int toGo = raceLaps > 0 ? raceLaps - Mathf.Clamp(pLap, 1, raceLaps) + 1 : 0;
                TF(new Rect(px0, dy + S(25), pw, S(11)), "LAPS", UiKit.Semi, 5.8f, HudFaint);
                TF(new Rect(px0, dy + S(25), pw, S(12)), HudNum.Tenths(laps), UiKit.Bold, 8.5f, raceLaps > 0 && laps < toGo ? HudRed : HudGreen, TextAnchor.MiddleRight);
            }
        }
        TF(new Rect(px0, dy + S(38), pw, S(9)), Weather.TyreName(carSet), UiKit.Semi, 5.8f, SetCol[Mathf.Clamp(carSet, 0, SetCol.Length - 1)]);
    }

    // ---------------------------------------------------------------- Overtake Mode and boost (FormulaCar.otOn / boostOn)
    // Overtake Mode (2026, replaces DRS): FormulaSim.OvertakeRule arms a car that crosses a detection point within 1 s of
    // the car ahead; armed, a tap on OVERTAKE deploys the lap's extra 0.5 MJ with full power to 337 km/h (tap again to
    // stop and save the rest; it also stops on the brakes and at the next detection point). BOOST: full MGU-K power from
    // the battery, whatever the energy map says, while held (charge above 5 %). Keys: Shift / B boost, O / Q overtake.
    // The buttons read the touches themselves (like the pedals): IMGUI only follows one finger, and the right thumb is
    // usually on the gas.
    OvertakeRule otRule; RacingLine otLine; double otRace;
    bool otLatch, otBannerSeen; int boostFinger = -1;
    bool ErsButtonsShown => car != null && car.cfg.ersPower > 0 && !replaying && !menuOpen && (ui == null || !ui.Showing);
    // OVERTAKE right of BOOST, both above the brake pedal (left of the gas pedal, below the mini-map)
    Rect OvertakeRect
    {
        get
        {
            float d = S(44), x = Mathf.Min(GasRect.x - S(10) - d, UiKit.Safe.xMax - S(8) - d);
            return new Rect(Mathf.Round(x), Mathf.Round(Mathf.Max(UiKit.Safe.y + S(4), Screen.height - S(136) - d)), d, d);
        }
    }
    Rect BoostRect { get { var o = OvertakeRect; return new Rect(o.x - o.width - S(8), o.y, o.width, o.height); } }

    // 60 times a second (StepRivals): every car past the detection points
    void OvertakeTick()
    {
        if (track == null || line == null || playground || car == null) return;
        if (otRule == null || otRule.track != track || otLine != line) { otRule = new OvertakeRule(track, line); otLine = line; }
        if (raceTime < otRace - 0.5) otRule.Reset(); // a restart
        otRace = raceTime;
        bool race = raceLaps > 0 && quali == null && (raceCtl == null || !raceCtl.NoOvertaking);
        otRule.Update(car, pRun != null ? -1 : idx, simTime, race && pLap >= 2);
        for (int i = 0; i < rivals.Count; i++) { var d = rivals[i]; otRule.Update(d.car, d.InPit ? -1 : d.idx, simTime, race && d.lap >= 2); }
    }

    // once a frame, before the physics steps (Update): the two buttons into the player's car
    void ErsButtonsInput()
    {
        if (car == null) return;
        bool boost = false, tap = false;
        if (ErsButtonsShown && !Paused)
        {
            float m = S(4);
            var b = BoostRect; var o = OvertakeRect;
            var bHit = new Rect(b.x - m, b.y - m, b.width + 2 * m, b.height + 2 * m);
            var oHit = new Rect(o.x - m, o.y - m, o.width + 2 * m, o.height + 2 * m);
            bool held = false;
            for (int ti = 0, tn = Input.touchCount; ti < tn; ti++)
            {
                var t = Input.GetTouch(ti);
                var p = new Vector2(t.position.x, Screen.height - t.position.y);
                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                if (t.phase == TouchPhase.Began) { if (bHit.Contains(p)) boostFinger = t.fingerId; else if (oHit.Contains(p)) tap = true; }
                if (t.fingerId == boostFinger && !ended) held = true;
            }
            if (!held) boostFinger = -1;
            boost = held;
            if (Input.touchCount == 0)
            {
                var mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                if (Input.GetMouseButton(0) && bHit.Contains(mp)) boost = true;
                if (Input.GetMouseButtonDown(0) && oHit.Contains(mp)) tap = true;
            }
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.B)) boost = true;
            if (Input.GetKeyDown(KeyCode.O) || Input.GetKeyDown(KeyCode.Q)) tap = true;
        }
        else boostFinger = -1;
        if (tap)
        {
            if (otLatch) otLatch = false;
            else if (car.otArmed && car.OtLeft > 1) otLatch = true;
            else Flash(car.otArmed ? "Overtake energy used" : "Overtake not available");
        }
        if (!car.otArmed || car.OtLeft <= 0 || car.brake > 0.3) otLatch = false;
        car.otOn = otLatch; car.boostOn = boost;
    }

    void DrawErsButtons()
    {
        if (!ErsButtonsShown) return;
        float t = Time.unscaledTime, pulse = 0.5f + 0.5f * Mathf.Sin(t * 6f);
        Color glass = new Color(0.05f, 0.06f, 0.08f, 0.66f), edge = new Color(1f, 1f, 1f, 0.16f);

        // BOOST: glass; blue while it deploys, dim when the battery is too low
        var b = BoostRect;
        float soc = double.IsNaN(car.ersEnergy) ? 1f : Mathf.Clamp01((float)(car.ersEnergy / car.cfg.ersCapacity));
        bool bOk = soc > car.cfg.boostMinSoc, bOn = car.boostOn;
        var bb = UiKit.Scale(b, bOn ? 0.93f : 1f);
        if (car.boostActive) UiKit.Rect(UiKit.Scale(bb, 1.16f), new Color(HudBlue.r, HudBlue.g, HudBlue.b, 0.22f), 999f);
        Disc(bb, car.boostActive ? new Color(HudBlue.r * 0.8f, HudBlue.g * 0.8f, HudBlue.b * 0.8f, 0.9f) : bOn ? new Color(0.18f, 0.19f, 0.23f, 0.88f) : glass);
        UiKit.Outline(bb, bOk ? new Color(HudBlue.r, HudBlue.g, HudBlue.b, 0.55f) : edge, 999f, 0.8f);
        TF(new Rect(bb.x, bb.y - S(3), bb.width, bb.height), "BOOST", UiKit.Bold, 7f, bOk ? Color.white : HudFaint, TextAnchor.MiddleCenter);
        TF(new Rect(bb.x, bb.y + S(7), bb.width, bb.height), HudNum.Pct(Mathf.RoundToInt(soc * 100)), UiKit.Semi, 5.6f, bOk ? HudGrey : HudRed, TextAnchor.MiddleCenter);

        // OVERTAKE: grey until armed, then pulsing purple / cyan; solid cyan while it deploys; the ring = energy left
        var o = OvertakeRect;
        bool armed = car.otArmed, on = car.otOn, act = car.otActive;
        var ob = UiKit.Scale(o, on ? 0.93f : 1f);
        Color hot = Color.Lerp(HudPurple, HudCyan, pulse);
        if (armed) UiKit.Rect(UiKit.Scale(ob, 1.22f), new Color(hot.r, hot.g, hot.b, on ? 0.30f : 0.14f + 0.16f * pulse), 999f);
        Color fill = !armed ? glass : act ? new Color(HudCyan.r * 0.85f, HudCyan.g * 0.85f, HudCyan.b * 0.85f, 0.92f) : on ? new Color(HudPurple.r, HudPurple.g, HudPurple.b, 0.9f) : new Color(hot.r * 0.7f, hot.g * 0.7f, hot.b * 0.7f, 0.85f);
        Disc(ob, fill);
        UiKit.Outline(ob, armed ? new Color(1f, 1f, 1f, 0.45f) : edge, 999f, 0.8f);
        Color tc = armed ? (act ? new Color(0.03f, 0.04f, 0.06f) : Color.white) : HudFaint;
        TF(new Rect(ob.x, ob.y - S(3), ob.width, ob.height), "OVERTAKE", UiKit.Bold, 5.6f, tc, TextAnchor.MiddleCenter);
        TF(new Rect(ob.x, ob.y + S(7), ob.width, ob.height), HudNum.Pct(Mathf.RoundToInt((float)car.OtFrac * 100)), UiKit.Semi, 5.6f, armed ? tc : HudFaint, TextAnchor.MiddleCenter);
        // energy ring (GL, clipped to nothing: the whole screen)
        if (GlBegin(new Rect(0, 0, Screen.width, Screen.height)))
        {
            float r0 = o.width * 0.5f + S(2.2f), r1 = r0 + S(1.7f);
            GlRing(o.center.x, o.center.y, r0, r1, 1f, new Color(1f, 1f, 1f, 0.10f));
            float f = (float)car.OtFrac;
            if (f > 0.002f) GlRing(o.center.x, o.center.y, r0, r1, f, armed ? (act ? HudCyan : HudPurple) : new Color(0.55f, 0.58f, 0.64f, 0.7f));
            GlEnd();
        }
    }

    // ---------------------------------------------------------------- GL drawing in OnGUI (mini-map, energy ring)
    // Triangles in GUI pixels (y down), clipped to a rectangle on the CPU (Sutherland-Hodgman), so nothing spills out
    // of a panel. Repaint only; scratch arrays are fixed, nothing is allocated per frame.
    static Material glMat;
    static Material GlMat
    {
        get
        {
            if (glMat == null)
            {
                var sh = Shader.Find("Hidden/Internal-Colored");
                if (sh == null || !sh.isSupported) sh = Shader.Find("Sprites/Default");
                if (sh == null) return null;
                glMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                if (glMat.HasProperty("_SrcBlend")) glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (glMat.HasProperty("_DstBlend")) glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (glMat.HasProperty("_Cull")) glMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                if (glMat.HasProperty("_ZWrite")) glMat.SetInt("_ZWrite", 0);
                if (glMat.HasProperty("_ZTest")) glMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
            return glMat;
        }
    }
    float glX0, glY0, glX1, glY1; Color glCol = Color.white;
    static readonly float[] glAx = new float[16], glAy = new float[16], glBx = new float[16], glBy = new float[16];
    bool GlBegin(Rect clip)
    {
        if (Event.current.type != EventType.Repaint) return false;
        var m = GlMat; if (m == null) return false;
        glX0 = clip.xMin; glY0 = clip.yMin; glX1 = clip.xMax; glY1 = clip.yMax;
        m.SetPass(0);
        GL.PushMatrix(); GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
        GL.Begin(GL.TRIANGLES);
        return true;
    }
    static void GlEnd() { GL.End(); GL.PopMatrix(); }
    void GlColor(Color c) { glCol = c; glCol.a *= GUI.color.a; }
    // convex polygon in glAx / glAy (n points) -> clipped -> a triangle fan
    void GlEmit(int n)
    {
        n = ClipEdge(glAx, glAy, n, glBx, glBy, 0, glX0); if (n < 3) return;
        n = ClipEdge(glBx, glBy, n, glAx, glAy, 1, glX1); if (n < 3) return;
        n = ClipEdge(glAx, glAy, n, glBx, glBy, 2, glY0); if (n < 3) return;
        n = ClipEdge(glBx, glBy, n, glAx, glAy, 3, glY1); if (n < 3) return;
        GL.Color(glCol);
        for (int i = 1; i + 1 < n; i++)
        {
            GL.Vertex3(glAx[0], glAy[0], 0f); GL.Vertex3(glAx[i], glAy[i], 0f); GL.Vertex3(glAx[i + 1], glAy[i + 1], 0f);
        }
    }
    // one clip edge: side 0 x >= v, 1 x <= v, 2 y >= v, 3 y <= v
    static int ClipEdge(float[] ix, float[] iy, int n, float[] ox, float[] oy, int side, float v)
    {
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            float ax = ix[i], ay = iy[i], bx = ix[j], by = iy[j];
            float da = side == 0 ? ax - v : side == 1 ? v - ax : side == 2 ? ay - v : v - ay;
            float db = side == 0 ? bx - v : side == 1 ? v - bx : side == 2 ? by - v : v - by;
            if (da >= 0f && m < ox.Length) { ox[m] = ax; oy[m] = ay; m++; }
            if ((da >= 0f) != (db >= 0f) && m < ox.Length)
            {
                float k = da / (da - db);
                ox[m] = ax + (bx - ax) * k; oy[m] = ay + (by - ay) * k; m++;
            }
        }
        return m;
    }
    void GlQuad(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy)
    {
        glAx[0] = ax; glAy[0] = ay; glAx[1] = bx; glAy[1] = by; glAx[2] = cx; glAy[2] = cy; glAx[3] = dx; glAy[3] = dy;
        GlEmit(4);
    }
    void GlTri(float ax, float ay, float bx, float by, float cx, float cy)
    {
        glAx[0] = ax; glAy[0] = ay; glAx[1] = bx; glAy[1] = by; glAx[2] = cx; glAy[2] = cy;
        GlEmit(3);
    }
    // a line w pixels wide
    void GlLine(float ax, float ay, float bx, float by, float w)
    {
        float dx = bx - ax, dy = by - ay, l = Mathf.Sqrt(dx * dx + dy * dy);
        if (l < 1e-3f) return;
        float nx = -dy / l * w * 0.5f, ny = dx / l * w * 0.5f;
        GlQuad(ax + nx, ay + ny, bx + nx, by + ny, bx - nx, by - ny, ax - nx, ay - ny);
    }
    // a disc (12-gon)
    void GlDisc(float cx, float cy, float r)
    {
        const int K = 12;
        for (int i = 0; i < K; i++) { float a = i * (Mathf.PI * 2f / K); glAx[i] = cx + Mathf.Cos(a) * r; glAy[i] = cy + Mathf.Sin(a) * r; }
        GlEmit(K);
    }
    // a ring from 12 o'clock clockwise over frac of the turn, between radii r0 and r1
    void GlRing(float cx, float cy, float r0, float r1, float frac, Color c)
    {
        GlColor(c);
        const int K = 48;
        int n = Mathf.Max(1, Mathf.CeilToInt(K * Mathf.Clamp01(frac)));
        float a0 = -Mathf.PI * 0.5f, span = Mathf.PI * 2f * Mathf.Clamp01(frac);
        for (int i = 0; i < n; i++)
        {
            float aa = a0 + span * i / n, ab = a0 + span * (i + 1) / n;
            float ca = Mathf.Cos(aa), sa = Mathf.Sin(aa), cb = Mathf.Cos(ab), sb = Mathf.Sin(ab);
            GlQuad(cx + ca * r0, cy + sa * r0, cx + ca * r1, cy + sa * r1, cx + cb * r1, cy + sb * r1, cx + cb * r0, cy + sb * r0);
        }
    }

    // ---------------------------------------------------------------- banners and messages
    object bKeyT, bKeyS; float bTw, bSw; int bKeyH;
    object fKey; float fW; int fKeyH;
    // pit lane: a banner at the entry and the exit, a "PIT LIMITER" chip while the limiter holds the car
    void PitHudEnter() => Banner("PIT LIMITER", "80 KM/H  ·  BOX FOR " + Weather.TyreName(NextSet).ToUpperInvariant(), HudYellow);
    void PitHudExit() => Banner("PIT EXIT", "BACK ON TRACK  ·  " + Weather.TyreName(carSet).ToUpperInvariant(), HudGreen);
    void DrawPitLimiter()
    {
        if (pRun == null || pRun.frozen) return;
        bool lim = pRun.InLimiter || pRun.phase >= 1;
        string t = pRun.phase == 1 ? "IN THE BOX" : lim ? "PIT LIMITER  80" : "PIT ENTRY";
        float w = S(118), h = S(18), x = Mathf.Round(Screen.width * 0.5f - w * 0.5f), y = hudT + S(78);
        bool blink = lim && pRun.phase != 1 && Mathf.Repeat(Time.unscaledTime * 1.6f, 1f) < 0.5f;
        Chip(new Rect(x, y, w, h), t, blink ? new Color(0.95f, 0.78f, 0.1f, 0.95f) : HudPanel, blink ? new Color(0.05f, 0.05f, 0.06f) : HudYellow);
    }
    void DrawBanners()
    {
        DrawPitLimiter();
        if (bCount > 0)
        {
            string title = bTitle[0], sub = bSub[0];
            if (!ReferenceEquals(bKeyT, title) || !ReferenceEquals(bKeyS, sub) || bKeyH != Screen.height)
            {
                bKeyT = title; bKeyS = sub; bKeyH = Screen.height; // measured once per banner (CalcSize allocates)
                bTw = UiKit.Width(title, UiKit.Black, 10f); bSw = string.IsNullOrEmpty(sub) ? 0 : UiKit.Width(sub, UiKit.Semi, 8.5f);
            }
            float k = UiKit.EaseOut(bT / 0.35f) * (1f - (bT > 2.45f ? UiKit.EaseOut((bT - 2.45f) / 0.35f) : 0f));
            Color c = bCol[0];
            float h = S(28), tw = Mathf.Max(S(64), bTw + S(22)), sw = bSw > 0 ? bSw + S(26) : 0, w = tw + sw;
            float x = Mathf.Round(Screen.width * 0.5f - w * 0.5f), y = hudT + S(44) - (1f - k) * S(22);
            var old = Fade(k);
            RR(new Rect(x, y, tw, h), c, 8, sw > 0 ? 0 : 8, sw > 0 ? 0 : 8, 8);
            TF(new Rect(x, y, tw, h), title, UiKit.Black, 10f, c == HudYellow ? new Color(0.05f, 0.05f, 0.06f) : Color.white, TextAnchor.MiddleCenter);
            if (sw > 0)
            {
                var body = new Rect(x + tw, y, sw, h);
                RR(body, HudPanel, 0, 8, 8, 0);
                TF(new Rect(body.x + S(13), y, sw, h), sub, UiKit.Semi, 8.5f, Color.white);
            }
            float life = Mathf.Clamp01(1f - bT / 2.8f);
            UiKit.Rect(new Rect(x + S(8), y + h - S(2), (w - S(16)) * life, S(1.2f)), new Color(c.r, c.g, c.b, 0.9f));
            GUI.color = old;
        }
        // the game's own short messages, unless a banner or the lap flash already says it
        if (flashT > 0 && !string.IsNullOrEmpty(flash) && !Covered(flash))
        {
            if (!ReferenceEquals(fKey, flash) || fKeyH != Screen.height) { fKey = flash; fKeyH = Screen.height; fW = UiKit.Width(flash, UiKit.Semi, 9.5f); }
            float a = Mathf.Clamp01(flashT / 0.3f) * Mathf.Clamp01((1.4f - flashT) / 0.12f);
            float w = fW + S(36), h = S(24), x = Mathf.Round(Screen.width * 0.5f - w * 0.5f), y = Screen.height * 0.40f + (1f - a) * S(6);
            var old = Fade(a);
            var r = new Rect(x, y, w, h);
            Disc(r, HudPanel);
            UiKit.Outline(r, GlassLine, 999f, 0.6f);
            Disc(new Rect(x + S(11), y + h * 0.5f - S(2.5f), S(5), S(5)), HudAccent);
            TF(new Rect(x + S(22), y, w - S(28), h), flash, UiKit.Semi, 9.5f, Color.white, TextAnchor.MiddleCenter);
            GUI.color = old;
        }
    }
    static bool Covered(string s) =>
        s.StartsWith("Best lap", System.StringComparison.Ordinal) || s.StartsWith("Lap ", System.StringComparison.Ordinal) ||
        s.StartsWith("Pit stop", System.StringComparison.Ordinal) || s.StartsWith("JUMP START", System.StringComparison.Ordinal);

    readonly HudSlot fpsSlot = new HudSlot();
    void DrawFps()
    {
        if (!showFps) return;
        TF(new Rect(btnRight - S(150), hudT + S(35), S(150), S(10)), fpsSlot.Fps(Mathf.RoundToInt(fpsAvg), renderScale < 0.99f ? Mathf.RoundToInt(renderScale * 100) : 0), UiKit.Semi, 6.2f, HudFaint, TextAnchor.MiddleRight);
    }
}

// Number strings for the HUD. Values that change every frame come from tables filled the first time a value is
// shown; values that change rarely (lap and sector times) are kept per place on screen and rebuilt only when they change.
static class HudNum
{
    static readonly string[] ints = new string[4001], tenths = new string[10000], gaps = new string[10000], deltas = new string[4001], clocks = new string[12000], pcts = new string[101], kws = new string[1001], poss = new string[41], laps = new string[200], lapsDown = new string[100], ofs = new string[41];
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
    public static string Of(int n) => n >= 0 && n < ofs.Length ? ofs[n] ??= "/" + n : "/" + n;
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
    // "4/10" (or "4" without a race distance)
    public string Frac(int a, int b)
    {
        long k = a * 10000L + b + (1L << 40);
        if (k != key) { key = k; s = b > 0 ? a + "/" + b : a.ToString(); }
        return s;
    }
    public string Fps(int fps, int res)
    {
        long k = fps * 1000L + res;
        if (k != key) { key = k; s = fps + " FPS" + (res > 0 ? "  ·  " + res + "% RES" : ""); }
        return s;
    }
    public string Test(double? t100, double top, string unit)
    {
        long k = (t100.HasValue ? (long)System.Math.Round(t100.Value * 100) : 99999) * 100000L + (long)System.Math.Round(top) * 10 + (unit == "mph" ? 1 : 0);
        if (k != key) { key = k; s = "0-100 " + (t100.HasValue ? t100.Value.ToString("0.00") + " s" : "—") + "  (real 2.2-2.6)  ·  top " + top.ToString("0") + " " + unit; }
        return s;
    }
}
