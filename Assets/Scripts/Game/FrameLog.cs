// FrameLog.cs — on-device frame-time diagnostics (Settings > GRAPHICS > "Performance graph (debug)", off by default;
// PlayerPrefs "frameLog"). Written for the "same spot, different laps: 60 to 20 fps" report: Docs/PERF-INVESTIGATION.md.
//
// Top left, always while on:
//  * a graph of the last ~5 s of frames: one bar per frame, as wide as the frame lasted and as tall as its time
//    (0-50 ms; lines at the lock's interval, 33 and 50 ms). Green = on time (<= 1.25 x the lock's interval),
//    yellow = up to 2x, red = longer. Markers: magenta on top = a garbage collection finished in that frame; cyan at
//    the bottom = more than 8 physics steps in the frame; white = the physics time guard dropped simulated time.
//  * three lines (refreshed twice a second): lock, display Hz, fps; frame ms p50 / p95 / max over the last 10 s, late
//    frames; Unity's FrameTimingManager main / render thread / GPU ms and the main thread's wait for present; physics ms
//    and steps per frame; GC collections, estimated allocation per frame (GC heap growth between frames with no
//    collection), heap size; the phone's thermal status, headroom, battery and SoC temperature, CPU and GPU clocks
//    (Thermal.cs); render scale; draw calls and triangles.
// On the race Results screen: the last 2 minutes of racing (menus, pauses, the intro and replays left out) in rows of
// 20 s, oldest first, one column per number above plus "idle" (share of frames with no finger on the screen), for a
// screenshot. Rows restart with each session.
// Cost: nothing when off (no component exists). When on: no allocation per frame (all buffers made once; the text is
// written into preallocated strings in place), ~0.1-0.2 ms CPU for the graph (GL immediate quads in OnGUI's repaint).
using System;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;

[DefaultExecutionOrder(32100)] // after PerfLabLate (32000): this frame's game work is done when LateUpdate samples
public class FrameLog : MonoBehaviour
{
    // ---------------------------------------------------------------- the setting
    static bool show; static int showRead = -1; static FrameLog inst;
    public static bool Show
    {
        get { if (showRead < 0) { showRead = 1; show = PlayerPrefs.GetInt("frameLog", 0) == 1; } return show; }
        set
        {
            show = value; showRead = 1; PlayerPrefs.SetInt("frameLog", value ? 1 : 0);
            if (value) Ensure(); else if (inst != null) { Destroy(inst.gameObject); inst = null; }
        }
    }
    public static void Ensure()
    {
        if (inst != null || !Show) return;
        var go = new GameObject("FrameLog"); DontDestroyOnLoad(go);
        inst = go.AddComponent<FrameLog>();
    }

    // ---------------------------------------------------------------- fed by GameManager every frame (cheap stores)
    public static int Steps; public static float PhysMs; public static int SpiralDrops;
    public static bool Racing, ResultsUp; public static int Lock = 60, Gfx = 2; public static float Res = 1f;
    static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public static void Physics(int steps, long ticks) { Steps = steps; PhysMs = (float)(ticks * TickMs); }
    public static void SessionStart() { if (inst != null) inst.ResetRows(); }

    // ---------------------------------------------------------------- per-frame samples
    const int G = 720;                                   // graph ring: 5 s at 120 fps + margin
    readonly float[] gMs = new float[G]; readonly byte[] gFl = new byte[G]; int gHead, gCount;
    const byte FlagGc = 1, FlagSteps = 2, FlagDrop = 4;
    const int W = 1500;                                  // 10 s window ring (120 fps + margin)
    const int Bins = 400; const float BinMs = 0.25f;      // histogram: 0.25 ms bins to 100 ms, then one overflow bin
    readonly float[] wMs = new float[W]; int wHead, wCount; double wSum; readonly int[] wHist = new int[Bins + 1];
    static int Bin(float ms) { int b = (int)(ms / BinMs); return b < 0 ? 0 : b > Bins ? Bins : b; }

    // 0.5 s window sums
    int aN, aTimN, aStepsMax, aDraws; double aMs, aMain, aRend, aGpu, aWait, aPhys, aSteps; long aAlloc, aTris;
    float nextText; int lastGc = -1, gcTotal, dropsSeen; long lastUsed = -1, heapUsed;
    static readonly FrameTiming[] ft = new FrameTiming[1]; bool ftmOn;
    ProfilerRecorder rDraw, rTris, rReserved;

    // ---------------------------------------------------------------- 2-minute summary: 6 rows of 20 s + the current one
    const float RowSec = 20f; const int Rows = 6;
    sealed class Row
    {
        public int frames, late, gc, maxSteps, timN, touchless, therm = -1, lockMin, gfx;
        public double ms, main, rend, gpu, phys, t0; public long alloc; public float maxMs, head = float.NaN, bat = float.NaN, soc = float.NaN, cpu = float.NaN, gpuMhz = float.NaN, hz = float.NaN, res;
        public readonly int[] hist = new int[Bins + 1];
        public void Clear() { frames = late = gc = maxSteps = timN = touchless = 0; therm = -1; lockMin = 0; ms = main = rend = gpu = phys = 0; alloc = 0; maxMs = 0; head = bat = soc = cpu = gpuMhz = hz = float.NaN; Array.Clear(hist, 0, hist.Length); }
    }
    readonly Row[] rows = new Row[Rows + 1]; int rowHead, rowCount; Row cur; double raceClock;

    void ResetRows() { rowHead = rowCount = 0; cur.Clear(); cur.t0 = 0; raceClock = 0; }

    void Awake()
    {
        useGUILayout = false;
        for (int i = 0; i < rows.Length; i++) rows[i] = new Row();
        cur = new Row();
        rDraw = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        rTris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        rReserved = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");
        ftmOn = FrameTimingManager.IsFeatureEnabled();
        device = SystemInfo.deviceModel + "  ·  " + SystemInfo.graphicsDeviceName + "  ·  " + SystemInfo.graphicsDeviceType + "  ·  " + SystemInfo.processorCount
               + " cores  ·  Unity " + Application.unityVersion + "  ·  app " + Application.version + "  ·  incremental GC " + (UnityEngine.Scripting.GarbageCollector.isIncremental ? "on" : "OFF")
               + "  ·  frame timing " + (ftmOn ? "on" : "OFF");
        for (int i = 0; i < line.Length; i++) line[i] = new TextLine(200);
        for (int r = 0; r < cell.GetLength(0); r++) for (int c = 0; c < cell.GetLength(1); c++) cell[r, c] = new TextLine(8);
    }
    void OnDestroy() { rDraw.Dispose(); rTris.Dispose(); rReserved.Dispose(); if (inst == this) inst = null; }

    void LateUpdate()
    {
        float ms = Time.unscaledDeltaTime * 1000f;
        // GC: a finished collection, and the heap's growth since last frame when there was none (= what was allocated,
        // a lower bound: incremental sweeping frees memory while the game allocates)
        int gcNow = GC.CollectionCount(0); long used = GC.GetTotalMemory(false);
        bool gcHit = lastGc >= 0 && gcNow != lastGc; if (gcHit) gcTotal += gcNow - lastGc;
        long alloc = !gcHit && lastUsed >= 0 && used > lastUsed ? used - lastUsed : 0;
        lastGc = gcNow; lastUsed = used; heapUsed = used;
        bool drop = SpiralDrops != dropsSeen; dropsSeen = SpiralDrops;

        // graph ring
        gMs[gHead] = ms; gFl[gHead] = (byte)((gcHit ? FlagGc : 0) | (Steps > 8 ? FlagSteps : 0) | (drop ? FlagDrop : 0));
        gHead = (gHead + 1) % G; if (gCount < G) gCount++;
        // 10 s window
        if (wCount == W) Pop();
        wMs[wHead] = ms; wHead = (wHead + 1) % W; wCount++; wSum += ms; wHist[Bin(ms)]++;
        while (wCount > 1 && wSum > 10000.0) Pop();

        // Unity's frame timing (a few frames late; 0 where the driver gives no GPU timer)
        float main = 0, rend = 0, gpu = 0, wait = 0; bool tim = false;
        if (ftmOn)
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, ft) > 0)
            {
                main = (float)ft[0].cpuMainThreadFrameTime; rend = (float)ft[0].cpuRenderThreadFrameTime; gpu = (float)ft[0].gpuFrameTime; wait = (float)ft[0].cpuMainThreadPresentWaitTime;
                tim = main > 0f;
            }
        }
        aN++; aMs += ms; aAlloc += alloc; aPhys += PhysMs; aSteps += Steps; if (Steps > aStepsMax) aStepsMax = Steps;
        if (tim) { aTimN++; aMain += main; aRend += rend; aGpu += gpu; aWait += wait; }
        if (rDraw.Valid) aDraws = (int)rDraw.LastValue;
        if (rTris.Valid) aTris = rTris.LastValue;

        if (Racing) RowAdd(ms, tim, main, rend, gpu, alloc, gcHit ? 1 : 0);

        if (Time.unscaledTime >= nextText) { nextText = Time.unscaledTime + 0.5f; Text(); aN = aTimN = aStepsMax = 0; aMs = aMain = aRend = aGpu = aWait = aPhys = aSteps = 0; aAlloc = 0; }
    }
    void Pop()
    {
        int t = (wHead - wCount + W) % W; float o = wMs[t];
        wSum -= o; wHist[Bin(o)]--; wCount--;
    }

    void RowAdd(float ms, bool tim, float main, float rend, float gpu, long alloc, int gc)
    {
        var r = cur;
        if (r.frames == 0) { r.t0 = raceClock; r.lockMin = Lock; }
        r.frames++; r.ms += ms; if (ms > r.maxMs) r.maxMs = ms; r.hist[Bin(ms)]++;
        if (ms > 1250f / Mathf.Max(1, Lock)) r.late++;
        r.gc += gc; r.alloc += alloc; r.phys += PhysMs; if (Steps > r.maxSteps) r.maxSteps = Steps;
        if (tim) { r.timN++; r.main += main; r.rend += rend; r.gpu += gpu; }
        if (Input.touchCount == 0) r.touchless++;
        if (Lock < r.lockMin) r.lockMin = Lock;
        if (Thermal.Status > r.therm) r.therm = Thermal.Status;
        r.head = Max(r.head, Thermal.Headroom); r.bat = Max(r.bat, Thermal.BatteryC); r.soc = Max(r.soc, Thermal.SocC);
        r.cpu = Min(r.cpu, Thermal.CpuGHz); r.gpuMhz = Min(r.gpuMhz, Thermal.GpuMHz); r.hz = Min(r.hz, Thermal.Hz);
        r.res = RenderScale.Scale * Res; r.gfx = Gfx;
        raceClock += ms * 0.001;
        if (r.ms >= RowSec * 1000.0) RowPush();
    }
    static float Max(float a, float b) => float.IsNaN(b) ? a : float.IsNaN(a) || b > a ? b : a;
    static float Min(float a, float b) => float.IsNaN(b) ? a : float.IsNaN(a) || b < a ? b : a;
    void RowPush()
    {
        // swap the finished row into the ring; the ring's oldest row object becomes the new current one
        var done = cur;
        cur = rows[rowHead]; rows[rowHead] = done; cur.Clear();
        rowHead = (rowHead + 1) % rows.Length; if (rowCount < rows.Length) rowCount++;
    }

    // ---------------------------------------------------------------- text, written in place (no allocation)
    readonly TextLine[] line = new TextLine[3];
    string device; GUIStyle style; static Material glMat;

    static float Pct(int[] hist, int n, float p)
    {
        if (n <= 0) return float.NaN;
        int want = Mathf.Max(1, Mathf.CeilToInt(n * p)), acc = 0;
        for (int b = 0; b <= Bins; b++) { acc += hist[b]; if (acc >= want) return (b + 0.5f) * BinMs; }
        return Bins * BinMs;
    }
    float WinMax() { float m = 0; for (int k = 0; k < wCount; k++) { float v = wMs[(wHead - 1 - k + W) % W]; if (v > m) m = v; } return m; }

    void Text()
    {
        float n = Mathf.Max(1, aN), tn = Mathf.Max(1, aTimN);
        float fps = aMs > 0 ? (float)(aN * 1000.0 / aMs) : 0f;
        var a = line[0].Clear();
        a.I(Lock).T(" LOCK  ").F(Thermal.Hz, 0).T(" Hz  |  ").F(fps, 1).T(" fps  |  ms p50 ").F(Pct(wHist, wCount, 0.5f), 1).T("  p95 ").F(Pct(wHist, wCount, 0.95f), 1)
         .T("  max ").F(WinMax(), 1).T("  (10 s)  |  late ").F(LateShare(), 0).T("%").End();
        var b = line[1].Clear();
        b.T("main ").F(aTimN > 0 ? (float)(aMain / tn) : float.NaN, 1).T("  rend ").F(aTimN > 0 ? (float)(aRend / tn) : float.NaN, 1).T("  gpu ").F(aTimN > 0 && aGpu > 0 ? (float)(aGpu / tn) : float.NaN, 1)
         .T("  wait ").F(aTimN > 0 ? (float)(aWait / tn) : float.NaN, 1).T("  |  phys ").F((float)(aPhys / n), 2).T(" ms  ").F((float)(aSteps / n), 1).T(" st (max ").I(aStepsMax).T(")");
        if (SpiralDrops > 0) b.T(" drops ").I(SpiralDrops);
        b.T("  |  GC ").I(gcTotal).T("  alloc ").F(aAlloc / n / 1024f, 2).T(" KB/f  heap ").F(heapUsed / 1048576f, 0);
        if (rReserved.Valid && rReserved.LastValue > 0) b.T("/").F(rReserved.LastValue / 1048576f, 0);
        b.T(" MB").End();
        var c = line[2].Clear();
        c.T("therm ");
        if (Thermal.Status >= 0) c.I(Thermal.Status); else c.C('-');
        c.T("  head ").F(Thermal.Headroom, 2).T("  |  bat ").F(Thermal.BatteryC, 1).T(" C  soc ").F(Thermal.SocC, 0).T(" C  |  cpu ").F(Thermal.CpuGHz, 2).T("/").F(Thermal.CpuMaxGHz, 2)
         .T(" GHz  gpu ").F(Thermal.GpuMHz, 0).T(" MHz  |  res ").F(RenderScale.Scale * 100f, 0).T("% x ").F(Res * 100f, 0).T("%  |  draws ").I(aDraws).T("  tris ").I(aTris / 1000).T("k  |  gfx ").I(Gfx);
        if (PerfStats.Show) c.T("  |  STATS ON");
        c.End();
    }
    float LateShare()
    {
        if (wCount == 0) return 0f;
        float lim = 1250f / Mathf.Max(1, Lock); int late = 0;
        for (int k = 0; k < wCount; k++) if (wMs[(wHead - 1 - k + W) % W] > lim) late++;
        return 100f * late / wCount;
    }

    // ---------------------------------------------------------------- drawing
    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        GUI.depth = -200;
        float h = Screen.height;
        if (style == null) style = new GUIStyle(GUI.skin.label) { wordWrap = false, richText = false, clipping = TextClipping.Overflow, alignment = TextAnchor.UpperLeft };
        int fs = Mathf.Max(10, Mathf.RoundToInt(h / 52f)); style.fontSize = fs; style.normal.textColor = Color.white;
        float lh = fs * 1.3f, x0 = Mathf.Round(h * 0.02f), y0 = Mathf.Round(h * 0.02f);
        float gw = Mathf.Round(h * 0.75f), gh = Mathf.Round(h * 0.11f), tw = fs * 60f;
        Box(new Rect(x0 - 4, y0 - 4, Mathf.Max(gw, tw) + 8, gh + 3 * lh + 12), 0.6f);
        Graph(new Rect(x0, y0, gw, gh));
        float ty = y0 + gh + 4;
        for (int i = 0; i < line.Length; i++) GUI.Label(new Rect(x0, ty + i * lh, tw, lh), line[i].s, style);
        if (ResultsUp) Summary(fs);
    }
    static void Box(Rect r, float a)
    {
        var c = GUI.color; GUI.color = new Color(0f, 0f, 0f, a); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = c;
    }

    static Material GlMat
    {
        get
        {
            if (glMat == null)
            {
                var sh = Shader.Find("Hidden/Internal-Colored"); if (sh == null) return null;
                glMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha); glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                glMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off); glMat.SetInt("_ZWrite", 0); glMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
            return glMat;
        }
    }
    static void Quad(float x0, float y0, float x1, float y1) { GL.Vertex3(x0, y0, 0); GL.Vertex3(x1, y0, 0); GL.Vertex3(x1, y1, 0); GL.Vertex3(x0, y1, 0); }
    static readonly Color cOk = new Color(0.25f, 0.85f, 0.35f, 0.95f), cSlow = new Color(1f, 0.82f, 0.15f, 0.95f), cBad = new Color(1f, 0.25f, 0.2f, 0.95f);
    static readonly Color cLine = new Color(1f, 1f, 1f, 0.35f), cTarget = new Color(0.4f, 1f, 0.5f, 0.6f), cGc = new Color(1f, 0.2f, 1f, 1f), cSteps = new Color(0.2f, 0.9f, 1f, 1f);

    void Graph(Rect r)
    {
        var m = GlMat; if (m == null) return;
        const float MaxMs = 50f, SpanMs = 5000f;
        float target = 1000f / Mathf.Max(1, Lock), pxMs = r.width / SpanMs, k = r.height / MaxMs;
        m.SetPass(0);
        GL.PushMatrix(); GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
        GL.Begin(GL.QUADS);
        GL.Color(new Color(1f, 1f, 1f, 0.06f)); Quad(r.xMin, r.yMin, r.xMax, r.yMax);
        float x = r.xMax, t = 0f;
        for (int i = 0; i < gCount && t < SpanMs; i++)
        {
            int j = (gHead - 1 - i + G) % G; float ms = gMs[j], w = Mathf.Min(ms, SpanMs - t) * pxMs;
            float bh = Mathf.Min(ms, MaxMs) * k, xl = Mathf.Max(r.xMin, x - Mathf.Max(w, 1f));
            GL.Color(ms <= target * 1.25f ? cOk : ms <= target * 2f ? cSlow : cBad);
            Quad(xl, r.yMax - bh, x, r.yMax);
            byte f = gFl[j];
            if ((f & FlagGc) != 0) { GL.Color(cGc); Quad(xl, r.yMin, Mathf.Max(x, xl + 3f), r.yMin + 5f); }
            if ((f & FlagSteps) != 0) { GL.Color(cSteps); Quad(xl, r.yMax - 4f, Mathf.Max(x, xl + 3f), r.yMax); }
            if ((f & FlagDrop) != 0) { GL.Color(Color.white); Quad(xl, r.yMin + 6f, Mathf.Max(x, xl + 3f), r.yMin + 11f); }
            x -= w; t += ms;
        }
        // reference lines: the lock's interval, 33.3 and 50 ms
        GL.Color(cTarget); HLine(r, r.yMax - Mathf.Min(target, MaxMs) * k);
        GL.Color(cLine); HLine(r, r.yMax - 33.33f * k); HLine(r, r.yMin);
        GL.End(); GL.PopMatrix();
    }
    static void HLine(Rect r, float y) => Quad(r.xMin, y - 0.5f, r.xMax, y + 0.5f);

    // ---------------------------------------------------------------- the results-screen summary
    static readonly string[] Head = { "t", "fps", "p50", "p95", "max", "late%", "main", "rend", "gpu", "phys", "st", "GC", "KB/s", "lock", "Hz", "therm", "head", "bat C", "soc C", "cpu", "gpu MHz", "idle%", "res%" };
    readonly TextLine[,] cell = new TextLine[Rows + 1, 23];
    void Summary(int fs)
    {
        int n = rowCount + (cur.frames > 0 ? 1 : 0);
        float lh = fs * 1.35f, cw = fs * 2.7f, w = cw * Head.Length + 16, x0 = Mathf.Round((Screen.width - w) * 0.5f), y0 = Mathf.Round(Screen.height * 0.30f);
        float hgt = lh * (n + 3) + 12;
        Box(new Rect(x0, y0, w, hgt), 0.82f);
        float x = x0 + 8, y = y0 + 6;
        GUI.Label(new Rect(x, y, w, lh), "Last 2 min of racing, rows of 20 s (oldest first; * = still running). Screenshot this.", style); y += lh;
        GUI.Label(new Rect(x, y, w, lh), device, style); y += lh;
        for (int c = 0; c < Head.Length; c++) GUI.Label(new Rect(x + c * cw, y, cw, lh), Head[c], style);
        y += lh;
        for (int i = 0; i < n; i++)
        {
            bool live = i == rowCount; Row r = live ? cur : rows[(rowHead - rowCount + i + rows.Length) % rows.Length];
            FillRow(i, r, live);
            for (int c = 0; c < Head.Length; c++) GUI.Label(new Rect(x + c * cw, y, cw, lh), cell[i, c].s, style);
            y += lh;
        }
    }
    void FillRow(int i, Row r, bool live)
    {
        float fr = Mathf.Max(1, r.frames), tn = Mathf.Max(1, r.timN), sec = (float)Math.Max(r.ms * 0.001, 1e-3);
        int c = 0; int mm = (int)(r.t0 / 60), ss = (int)(r.t0 % 60);
        var t = cell[i, c++].Clear().I(mm).C(':'); if (ss < 10) t.C('0'); t.I(ss); if (live) t.C('*'); t.End();
        cell[i, c++].Clear().F(r.frames / sec, 1).End();
        cell[i, c++].Clear().F(Pct(r.hist, r.frames, 0.5f), 1).End();
        cell[i, c++].Clear().F(Pct(r.hist, r.frames, 0.95f), 1).End();
        cell[i, c++].Clear().F(r.maxMs, 0).End();
        cell[i, c++].Clear().F(100f * r.late / fr, 0).End();
        cell[i, c++].Clear().F(r.timN > 0 ? (float)(r.main / tn) : float.NaN, 1).End();
        cell[i, c++].Clear().F(r.timN > 0 ? (float)(r.rend / tn) : float.NaN, 1).End();
        cell[i, c++].Clear().F(r.timN > 0 && r.gpu > 0 ? (float)(r.gpu / tn) : float.NaN, 1).End();
        cell[i, c++].Clear().F((float)(r.phys / fr), 2).End();
        cell[i, c++].Clear().I(r.maxSteps).End();
        cell[i, c++].Clear().I(r.gc).End();
        cell[i, c++].Clear().F(r.alloc / 1024f / sec, 1).End();
        cell[i, c++].Clear().I(r.lockMin).End();
        cell[i, c++].Clear().F(r.hz, 0).End();
        var th = cell[i, c++].Clear(); if (r.therm >= 0) th.I(r.therm); else th.C('-'); th.End();
        cell[i, c++].Clear().F(r.head, 2).End();
        cell[i, c++].Clear().F(r.bat, 1).End();
        cell[i, c++].Clear().F(r.soc, 0).End();
        cell[i, c++].Clear().F(r.cpu, 2).End();
        cell[i, c++].Clear().F(r.gpuMhz, 0).End();
        cell[i, c++].Clear().F(100f * r.touchless / fr, 0).End();
        cell[i, c++].Clear().F(r.res * 100f, 0).C(' ').C(r.gfx == 1 ? 'L' : r.gfx == 2 ? 'M' : 'H').End();
    }
}

// A line of text written in place into a string made once: the string object never changes, its characters do
// (written through a Span over the string's own memory). IMGUI copies the text when it draws, so a label always
// shows the current characters. Unused room at the end is spaces.
public sealed class TextLine
{
    public readonly string s; readonly int cap; int n;
    public TextLine(int capacity) { cap = capacity; s = new string(' ', capacity); }
    Span<char> Buf => MemoryMarshal.AsMemory(s.AsMemory()).Span;
    public TextLine Clear() { n = 0; return this; }
    public TextLine C(char ch) { if (n < cap) Buf[n++] = ch; return this; }
    public TextLine T(string t)
    {
        var b = Buf;
        for (int i = 0; i < t.Length && n < cap; i++) b[n++] = t[i];
        return this;
    }
    public TextLine I(long v)
    {
        if (v < 0) { C('-'); v = v == long.MinValue ? long.MaxValue : -v; }
        Span<char> tmp = stackalloc char[20]; int k = 0;
        do { tmp[k++] = (char)('0' + (int)(v % 10)); v /= 10; } while (v > 0 && k < 20);
        var b = Buf;
        while (k > 0 && n < cap) b[n++] = tmp[--k];
        return this;
    }
    // fixed decimals (0-3); "-" for NaN / infinity
    public TextLine F(float v, int dec)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return C('-');
        if (v < 0f) { C('-'); v = -v; }
        long scale = dec <= 0 ? 1 : dec == 1 ? 10 : dec == 2 ? 100 : 1000;
        double q = Math.Round((double)v * scale);
        long qi = q > 9e15 ? 9_000_000_000_000_000L : (long)q;
        I(qi / scale);
        if (scale > 1)
        {
            C('.'); long f = qi % scale;
            for (long d = scale / 10; d > 0; d /= 10) C((char)('0' + (int)(f / d % 10)));
        }
        return this;
    }
    public void End() { var b = Buf; for (int i = n; i < cap; i++) b[i] = ' '; }
}
