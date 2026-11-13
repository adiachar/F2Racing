// MenuUI.cs — the front end, drawn with IMGUI from GameManager.OnGUI:
//  * Start menu: circuit picker with a track-outline preview, car class, rivals, rival level, race length,
//    "Free practice" and "Race", and a "Settings" button (the settings screen itself is SettingsUI.cs).
//  * Loading: one plain frame on screen before the (slow) circuit build starts.
//  * Results: finishing order, best laps and gaps, "Race again" / "Menu" / "Replay" (last 60 s, TV cameras).
// A plain C# class: GameManager owns the game settings and hands them in by ref; this class draws, keeps the page
// that is up and the race-length setting. The game is paused while any page is showing (GameManager checks Showing).
// Layout units are the HUD's: 390 units = screen height of a landscape phone (see S). Touch targets are 44+ units.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public sealed class MenuUI
{
    public enum Page { None, Start, Loading, Results }
    public enum Choice { None, RaceAgain, Replay, Settings }

    public Page page = Page.Start;
    public bool Showing => page != Page.None;
    public bool practice;                                  // the session the loading page is starting
    public static readonly int[] LapCounts = { 1, 3, 5, 10 };
    public int lapsIdx;                                    // race length, saved as "laps"
    public bool canReplay;                                 // GameManager: a recording of the race's end is there to play

    // one car in the results; GameManager fills these when the player takes the flag
    public struct Row { public string name; public Color colour; public bool player; public double progress, best; }

    // the menu colours, shared with the settings screen (SettingsUI.cs)
    public static readonly Color Accent = new Color(0.882f, 0.024f, 0f);      // #E10600
    public static readonly Color PanelCol = new Color(0.035f, 0.04f, 0.055f, 0.9f);
    public static readonly Color Dim = new Color(1, 1, 1, 0.6f);

    readonly string[] mapNames; readonly int playgroundIdx; readonly int[] rivalCounts; readonly string[] diffNames;
    readonly Texture2D[] previews; readonly double[] lengths;   // per circuit, built when first shown
    int loadShownFrame = -1;

    // results
    Row[] results; string[] gaps; string resCircuit; int resLaps, resPos, resStops; double resTime;

    public MenuUI(string[] mapNames, int playgroundIdx, int[] rivalCounts, string[] diffNames)
    {
        this.mapNames = mapNames; this.playgroundIdx = playgroundIdx; this.rivalCounts = rivalCounts; this.diffNames = diffNames;
        previews = new Texture2D[mapNames.Length]; lengths = new double[mapNames.Length];
        lapsIdx = Mathf.Clamp(PlayerPrefs.GetInt("laps", 1), 0, LapCounts.Length - 1);
    }

    // true once the "Loading…" frame has been on screen: GameManager.Update then builds the circuit
    public bool LoadDue => page == Page.Loading && loadShownFrame >= 0 && Time.frameCount > loadShownFrame + 1;

    void BeginLoading(bool practiceSession) { practice = practiceSession; loadShownFrame = -1; page = Page.Loading; }

    public void ShowResults(Row[] rows, string circuit, int laps, double raceTime, double refLap, int pitStops = 0)
    {
        var list = new List<Row>(rows);
        list.Sort((a, b) => b.progress.CompareTo(a.progress));
        results = list.ToArray(); gaps = new string[results.Length];
        resCircuit = circuit; resLaps = laps; resTime = raceTime; resPos = 1; resStops = pitStops;
        double lead = results.Length > 0 ? results[0].progress : 0;
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].player) resPos = i + 1;
            double behind = lead - results[i].progress;
            int lapsDown = (int)System.Math.Floor(behind);
            gaps[i] = i == 0 ? "Winner" : lapsDown >= 1 ? $"+{lapsDown} lap{(lapsDown > 1 ? "s" : "")}" : $"+{behind * refLap:0.0}s";
        }
        page = Page.Results;
    }

    // ------------------------------------------------------------------ drawing
    static float S(float v) => v * Screen.height / 390f;
    GUIStyle title, caption, label, value, small, arrow, primary, secondary, cell;
    static Texture2D Tex(Color c)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        t.SetPixel(0, 0, c); t.Apply(false); return t;
    }
    static GUIStyle ButtonStyle(Color normal, Color pressed)
    {
        var s = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
        s.normal.background = s.hover.background = s.focused.background = Tex(normal);
        s.active.background = Tex(pressed);
        s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = Color.white;
        return s;
    }
    void Styles()
    {
        if (title == null)
        {
            title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerLeft, wordWrap = false };
            caption = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false, richText = true };
            label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, wordWrap = false };
            value = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
            small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true, richText = true };
            cell = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            title.normal.textColor = label.normal.textColor = value.normal.textColor = cell.normal.textColor = Color.white;
            caption.normal.textColor = small.normal.textColor = Dim;
            arrow = ButtonStyle(new Color(1, 1, 1, 0.10f), new Color(1, 1, 1, 0.28f));
            secondary = ButtonStyle(new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.28f));
            primary = ButtonStyle(Accent, new Color(0.62f, 0.02f, 0f));
        }
        title.fontSize = (int)S(30); caption.fontSize = (int)S(11); label.fontSize = (int)S(15); value.fontSize = (int)S(15);
        small.fontSize = (int)S(11); cell.fontSize = (int)S(12); arrow.fontSize = (int)S(26); secondary.fontSize = (int)S(15); primary.fontSize = (int)S(17);
    }
    static void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }

    // Draws the page that is up. loaded / loadedFor: the circuit GameManager has built now (its preview needs no rebuild).
    public Choice Draw(ref int map, ref int cls, ref int riv, ref int diff, Track loaded, int loadedFor)
    {
        Styles();
        if (page == Page.Start) { if (DrawStart(ref map, ref cls, ref riv, ref diff, loaded, loadedFor)) return Choice.Settings; }
        else if (page == Page.Loading) DrawLoading(mapNames[map]);
        else if (page == Page.Results) return DrawResults();
        return Choice.None;
    }

    // "‹ value ›" with square arrow buttons; returns the new index
    int Stepper(Rect r, int index, int count, string text)
    {
        float b = r.height;
        if (GUI.Button(new Rect(r.x, r.y, b, b), "<", arrow)) index = (index - 1 + count) % count;
        Fill(new Rect(r.x + b + S(2), r.y, r.width - 2 * b - S(4), b), new Color(1, 1, 1, 0.05f));
        GUI.Label(new Rect(r.x + b + S(4), r.y, r.width - 2 * b - S(8), b), text, value);
        if (GUI.Button(new Rect(r.xMax - b, r.y, b, b), ">", arrow)) index = (index + 1) % count;
        return index;
    }

    // returns true when "SETTINGS" is tapped (GameManager then shows the settings screen over the menu)
    bool DrawStart(ref int map, ref int cls, ref int riv, ref int diff, Track loaded, int loadedFor)
    {
        float sw = Screen.width, sh = Screen.height;
        Fill(new Rect(0, 0, sw, sh), new Color(0, 0, 0, 0.45f));
        float pw = Mathf.Min(sw - S(32), S(800)), px = (sw - pw) / 2;
        GUI.Label(new Rect(px, S(8), pw, S(42)), Application.productName.ToUpperInvariant(), title);
        Fill(new Rect(px, S(53), S(44), S(3)), Accent);
        bool openSettings = GUI.Button(new Rect(px + pw - S(140), S(10), S(140), S(46)), "SETTINGS", secondary);
        var panel = new Rect(px, S(66), pw, S(330));
        Fill(panel, PanelCol);
        float pad = S(16), leftW = pw * 0.42f;
        bool pg = map == playgroundIdx;

        // left: circuit
        float lx = panel.x + pad, ly = panel.y + pad, lw = leftW - pad * 1.5f;
        GUI.Label(new Rect(lx, ly, lw, S(16)), "CIRCUIT", caption);
        map = Stepper(new Rect(lx, ly + S(20), lw, S(48)), map, mapNames.Length, mapNames[map]);
        pg = map == playgroundIdx;
        float top = ly + S(76), size = Mathf.Max(S(40), Mathf.Min(lw, panel.yMax - pad - S(34) - top));
        var pr = new Rect(lx + (lw - size) / 2, top, size, size);
        Fill(pr, new Color(1, 1, 1, 0.04f));
        var tex = pg ? null : Preview(map, loaded, loadedFor);
        if (tex != null) GUI.DrawTexture(pr, tex, ScaleMode.ScaleToFit);
        else GUI.Label(pr, "Open test area\nfree driving, 0-100 and top speed", small);
        GUI.Label(new Rect(lx, pr.yMax + S(2), lw, S(32)), Info(map), small);
        Fill(new Rect(panel.x + leftW, panel.y + pad, S(1), panel.height - 2 * pad), new Color(1, 1, 1, 0.08f));

        // right: session
        float rx = panel.x + leftW + pad, rw = panel.xMax - pad - rx, ry = ly + S(20);
        GUI.Label(new Rect(rx, ly, rw, S(16)), "SESSION", caption);
        float rowH = S(40), rowGap = S(4), labW = rw * 0.36f;
        string[] labels = { "Car", "Rivals", "Rival level", "Race length", "Weather" };
        for (int i = 0; i < labels.Length; i++)
        {
            float y = ry + i * (rowH + rowGap);
            GUI.enabled = i == 0 || i == 4 || !pg; // the playground has no race
            GUI.Label(new Rect(rx, y, labW, rowH), labels[i], label);
            var sr = new Rect(rx + labW, y, rw - labW, rowH);
            if (i == 0) cls = Stepper(sr, cls, CarConfig.ClassNames.Length, CarConfig.ClassNames[cls]);
            if (i == 1) riv = Stepper(sr, riv, rivalCounts.Length, rivalCounts[riv] == 0 ? "None" : rivalCounts[riv] + " cars");
            if (i == 2) diff = Stepper(sr, diff, diffNames.Length, diffNames[diff]);
            if (i == 3)
            {
                int n = Stepper(sr, lapsIdx, LapCounts.Length, LapCounts[lapsIdx] + (LapCounts[lapsIdx] == 1 ? " lap" : " laps"));
                if (n != lapsIdx) { lapsIdx = n; PlayerPrefs.SetInt("laps", lapsIdx); }
            }
            if (i == 4) Weather.Index = Stepper(sr, Weather.Index, Weather.Names.Length, Weather.Names[Weather.Index]); // applied and saved at the start of the session
        }
        GUI.enabled = true;
        float by = panel.yMax - pad - S(54), bw = (rw - S(10)) / 2;
        if (GUI.Button(new Rect(rx, by, bw, S(54)), pg ? "DRIVE" : "FREE PRACTICE", secondary)) BeginLoading(true);
        GUI.enabled = !pg;
        if (GUI.Button(new Rect(rx + bw + S(10), by, bw, S(54)), "RACE", primary)) BeginLoading(false);
        GUI.enabled = true;

        // licence credit for the car model (CC BY): always on the start menu
        {
            var o = small.alignment; small.alignment = TextAnchor.MiddleLeft; small.fontSize = (int)S(9); small.wordWrap = false;
            GUI.Label(new Rect(px, sh - S(14), pw, S(14)), CarModel.Credit, small);
            small.alignment = o; small.fontSize = (int)S(11); small.wordWrap = true;
        }
        return openSettings;
    }

    string Info(int map)
    {
        if (map == playgroundIdx || map >= Circuits.All.Length) return "No lap timing";
        var def = Circuits.All[map];
        string s = (lengths[map] > 0 ? $"{lengths[map] / 1000:0.00} km  ·  " : "") + (def.style == CircuitStyle.Street ? "Street circuit" : def.style == CircuitStyle.Test ? "Test track" : "Permanent circuit");
        if (def.realPole > 0) s += "\nReal pole " + Fmt(def.realPole);
        return s;
    }

    // circuit outline drawn into a small texture (north up, start/finish line as a red dot); cached per circuit
    const int PV = 192;
    Texture2D Preview(int map, Track loaded, int loadedFor)
    {
        if (map < 0 || map >= Circuits.All.Length) return null;
        if (previews[map] != null) return previews[map];
        var t = loaded != null && loadedFor == map ? loaded : Circuits.All[map].Build();
        lengths[map] = t.length;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var q in t.pts) { minX = System.Math.Min(minX, q.x); maxX = System.Math.Max(maxX, q.x); minY = System.Math.Min(minY, q.y); maxY = System.Math.Max(maxY, q.y); }
        float span = (float)System.Math.Max(maxX - minX, maxY - minY), margin = 10;
        float k = (PV - 2 * margin) / Mathf.Max(span, 1f);
        float ox = (float)(minX + maxX) / 2 - span / 2, oy = (float)(minY + maxY) / 2 - span / 2;
        var alpha = new float[PV * PV]; var red = new float[PV * PV];
        void Dot(float fx, float fy, float radius, float[] into)
        {
            int r = Mathf.CeilToInt(radius) + 1, cx = Mathf.RoundToInt(fx), cy = Mathf.RoundToInt(fy);
            for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++)
            {
                int ix = cx + dx, iy = cy + dy;
                if (ix < 0 || iy < 0 || ix >= PV || iy >= PV) continue;
                float d = Mathf.Sqrt((ix - fx) * (ix - fx) + (iy - fy) * (iy - fy));
                into[iy * PV + ix] = Mathf.Max(into[iy * PV + ix], Mathf.Clamp01(radius + 0.5f - d));
            }
        }
        float PxX(double x) => margin + ((float)x - ox) * k;
        float PxY(double y) => margin + ((float)y - oy) * k;
        for (int i = 0; i < t.N; i++)
        {
            // fill in between points so the line has no gaps at any texture size
            var a = t.pts[i]; var b = t.pts[(i + 1) % t.N];
            float ax = PxX(a.x), ay = PxY(a.y), bx = PxX(b.x), by = PxY(b.y);
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(Mathf.Abs(bx - ax), Mathf.Abs(by - ay))));
            for (int s = 0; s < steps; s++) Dot(ax + (bx - ax) * s / steps, ay + (by - ay) * s / steps, 2.2f, alpha);
        }
        Dot(PxX(t.pts[0].x), PxY(t.pts[0].y), 5f, red);
        var px = new Color32[PV * PV];
        for (int i = 0; i < px.Length; i++)
        {
            float w = alpha[i], rr = red[i];
            var c = Color.Lerp(new Color(1, 1, 1, w), new Color(Accent.r, Accent.g, Accent.b, 1), rr);
            px[i] = c;
        }
        var tex = new Texture2D(PV, PV, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        tex.SetPixels32(px); tex.Apply(false);
        previews[map] = tex;
        return tex;
    }

    void DrawLoading(string circuit)
    {
        float sw = Screen.width, sh = Screen.height;
        Fill(new Rect(0, 0, sw, sh), new Color(0.02f, 0.025f, 0.035f, 0.94f));
        var o = caption.alignment; caption.alignment = TextAnchor.MiddleCenter;
        GUI.Label(new Rect(0, sh / 2 - S(46), sw, S(18)), "LOADING", caption);
        caption.alignment = o;
        var t = title.alignment; title.alignment = TextAnchor.MiddleCenter;
        GUI.Label(new Rect(0, sh / 2 - S(28), sw, S(44)), circuit + "…", title);
        title.alignment = t;
        Fill(new Rect(sw / 2 - S(22), sh / 2 + S(22), S(44), S(3)), Accent);
        // the frame has really been drawn: from the next-but-one frame on, the heavy build may start
        if (Event.current.type == EventType.Repaint && loadShownFrame < 0) loadShownFrame = Time.frameCount;
    }

    Choice DrawResults()
    {
        float sw = Screen.width, sh = Screen.height;
        Fill(new Rect(0, 0, sw, sh), new Color(0, 0, 0, 0.5f));
        float pw = Mathf.Min(sw - S(32), S(780)), px = (sw - pw) / 2, pad = S(16);
        var panel = new Rect(px, S(20), pw, S(350));
        Fill(panel, PanelCol);
        Fill(new Rect(panel.x, panel.y, panel.width, S(3)), Accent);
        int n = results.Length;

        // header: position, circuit, race time, best lap
        GUI.Label(new Rect(panel.x + pad, panel.y + S(12), pw, S(16)), $"RACE RESULT  ·  {resCircuit.ToUpperInvariant()}  ·  {resLaps} LAP{(resLaps > 1 ? "S" : "")}", caption);
        var ot = title.fontSize; title.fontSize = (int)S(40); title.alignment = TextAnchor.MiddleLeft;
        GUI.Label(new Rect(panel.x + pad, panel.y + S(30), S(110), S(48)), "P" + resPos, title);
        title.fontSize = ot; title.alignment = TextAnchor.LowerLeft;
        label.fontSize = (int)S(13);
        GUI.Label(new Rect(panel.x + pad + S(96), panel.y + S(40), S(200), S(30)), n > 1 ? $"of {n}" : "time trial", label);
        double myBest = double.NaN; foreach (var r in results) if (r.player) myBest = r.best;
        var ol = label.alignment; label.alignment = TextAnchor.MiddleRight;
        GUI.Label(new Rect(panel.xMax - pad - S(340), panel.y + S(32), S(340), S(22)), "Race time  <b>" + Fmt(resTime) + "</b>" + (resStops > 0 ? $"  ({resStops} stop{(resStops > 1 ? "s" : "")})" : ""), Rich(label));
        GUI.Label(new Rect(panel.xMax - pad - S(260), panel.y + S(54), S(260), S(22)), "Best lap  <b>" + Fmt(myBest) + "</b>", Rich(label));
        label.alignment = ol; label.fontSize = (int)S(15);

        // table: one column up to 8 cars, two beyond
        int cols = n > 8 ? 2 : 1, perCol = (n + cols - 1) / cols;
        float tx = panel.x + pad, tw = pw - 2 * pad, colGap = S(14), cw = (tw - (cols - 1) * colGap) / cols;
        float headY = panel.y + S(88), tableY = headY + S(18), tableH = panel.yMax - pad - S(50) - S(12) - tableY;
        float rh = Mathf.Min(S(28), tableH / Mathf.Max(1, perCol));
        for (int c = 0; c < cols; c++)
        {
            float x = tx + c * (cw + colGap);
            GUI.Label(new Rect(x + cw * 0.02f, headY, cw * 0.1f, S(16)), "POS", caption);
            GUI.Label(new Rect(x + cw * 0.14f, headY, cw * 0.4f, S(16)), "DRIVER", caption);
            GUI.Label(new Rect(x + cw * 0.58f, headY, cw * 0.22f, S(16)), "BEST LAP", caption);
            var oc = caption.alignment; caption.alignment = TextAnchor.MiddleRight;
            GUI.Label(new Rect(x + cw * 0.78f, headY, cw * 0.2f, S(16)), "GAP", caption);
            caption.alignment = oc;
        }
        for (int i = 0; i < n; i++)
        {
            var r = results[i];
            float x = tx + (i / perCol) * (cw + colGap), y = tableY + (i % perCol) * rh;
            Fill(new Rect(x, y + S(1), cw, rh - S(2)), r.player ? new Color(Accent.r, Accent.g, Accent.b, 0.45f) : new Color(1, 1, 1, i % 2 == 0 ? 0.05f : 0.02f));
            cell.fontStyle = r.player ? FontStyle.Bold : FontStyle.Normal;
            GUI.Label(new Rect(x + cw * 0.02f, y, cw * 0.1f, rh), (i + 1).ToString(), cell);
            Fill(new Rect(x + cw * 0.11f, y + rh * 0.25f, S(4), rh * 0.5f), r.player ? Color.white : r.colour);
            GUI.Label(new Rect(x + cw * 0.14f, y, cw * 0.44f, rh), r.name, cell);
            GUI.Label(new Rect(x + cw * 0.58f, y, cw * 0.22f, rh), Fmt(r.best), cell);
            cell.alignment = TextAnchor.MiddleRight;
            GUI.Label(new Rect(x + cw * 0.78f, y, cw * 0.2f, rh), gaps[i], cell);
            cell.alignment = TextAnchor.MiddleLeft;
        }
        cell.fontStyle = FontStyle.Normal;

        // buttons
        int nb = canReplay ? 3 : 2;
        float bw = Mathf.Min(S(190), (tw - (nb - 1) * S(10)) / nb), by = panel.yMax - pad - S(50);
        if (canReplay && GUI.Button(new Rect(panel.xMax - pad - 3 * bw - 2 * S(10), by, bw, S(50)), "REPLAY", secondary)) return Choice.Replay;
        if (GUI.Button(new Rect(panel.xMax - pad - 2 * bw - S(10), by, bw, S(50)), "MENU", secondary)) page = Page.Start;
        if (GUI.Button(new Rect(panel.xMax - pad - bw, by, bw, S(50)), "RACE AGAIN", primary)) return Choice.RaceAgain;
        return Choice.None;
    }
    static GUIStyle Rich(GUIStyle s) { s.richText = true; return s; }

    static string Fmt(double t)
    {
        if (double.IsNaN(t) || double.IsInfinity(t) || t <= 0) return "—";
        int m = (int)(t / 60); return $"{m}:{t - m * 60:00.000}";
    }
}
