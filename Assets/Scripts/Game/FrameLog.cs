// FrameLog.cs — on-device frame-time diagnostics (Settings > GRAPHICS > "Performance graph (debug)", off by default;
// PlayerPrefs "frameLog"). Written for the "same spot, different laps: 60 to 20 fps" report: Docs/PERF-INVESTIGATION.md.
//
// Top left, always while on:
//  * a graph of the last ~5 s of frames: one bar per frame, as wide as the frame lasted and as tall as its time
//    (0-50 ms; lines at the lock's interval, 33 and 50 ms). Green = on time (<= 1.25 x the lock's interval),
//    yellow = up to 2x, red = longer. Markers on top: magenta = a garbage collection finished in that frame, orange =
//    the phone's thermal / clock read (Thermal.cs), blue = a team radio message was mixed and uploaded (TeamRadio.cs);
//    at the bottom: cyan = more than 8 physics steps in the frame; white = the physics time guard dropped time.
//    Under it a strip of the managed memory allocated in each frame (0-8 KB, purple).
//  * five lines (refreshed twice a second): lock, display Hz and whether 120 Hz is confirmed, fps; frame ms p50 / p95 /
//    max over the last 10 s, late frames; Unity's FrameTimingManager main / render thread / GPU ms and the main thread's
//    wait for present; physics ms and steps per frame; GC collections, allocation per frame and per second, heap size,
//    long frames (> 50 ms) and what was going on in them; the phone's thermal status, headroom, battery and SoC
//    temperature, CPU and GPU clocks (Thermal.cs); render scale; draw calls and triangles.
// On the race Results screen: the last 2 minutes of racing (menus, pauses, the intro and replays left out) in rows of
// 20 s, oldest first, one column per number above plus "idle" (share of frames with no finger on the screen), for a
// screenshot. Rows restart with each session.
// Cost: nothing when off (no component exists). When on: NO managed allocation at all. The text is NOT drawn with
// IMGUI labels (Unity 6's IMGUI copies every label's string on every draw and builds new TextCore meshes, ~1-2 KB,
// whenever a label's text changes: the old overlay's own text was ~100 KB/s of the garbage it measured). It is written
// with a small bitmap font into a texture (twice a second) and drawn as one image. The graph is GL quads in OnGUI's
// repaint, ~0.1-0.2 ms CPU.
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
    readonly float[] gMs = new float[G], gKb = new float[G]; readonly byte[] gFl = new byte[G]; int gHead, gCount;
    const byte FlagGc = 1, FlagSteps = 2, FlagDrop = 4, FlagTherm = 8, FlagRadio = 16;
    const int W = 1500;                                  // 10 s window ring (120 fps + margin)
    const int Bins = 400; const float BinMs = 0.25f;      // histogram: 0.25 ms bins to 100 ms, then one overflow bin
    readonly float[] wMs = new float[W]; int wHead, wCount; double wSum; readonly int[] wHist = new int[Bins + 1];
    static int Bin(float ms) { int b = (int)(ms / BinMs); return b < 0 ? 0 : b > Bins ? Bins : b; }
    const float SpikeMs = 50f;                           // a "long frame" for the spike count
    byte prevFlags;

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
        public int spk, sG, sR, sT, sP, sU;              // frames > SpikeMs and what was going on in them
        public double ms, main, rend, gpu, phys, t0; public long alloc; public float maxMs, head = float.NaN, bat = float.NaN, soc = float.NaN, cpu = float.NaN, gpuMhz = float.NaN, hz = float.NaN, res;
        public readonly int[] hist = new int[Bins + 1];
        public void Clear() { frames = late = gc = maxSteps = timN = touchless = 0; spk = sG = sR = sT = sP = sU = 0; therm = -1; lockMin = 0; ms = main = rend = gpu = phys = 0; alloc = 0; maxMs = 0; head = bat = soc = cpu = gpuMhz = hz = float.NaN; Array.Clear(hist, 0, hist.Length); }
    }
    readonly Row[] rows = new Row[Rows + 1]; int rowHead, rowCount; Row cur; double raceClock;

    void ResetRows() { rowHead = rowCount = 0; cur.Clear(); cur.t0 = 0; raceClock = 0; sumDirty = true; }

    void Awake()
    {
        useGUILayout = false;
        for (int i = 0; i < rows.Length; i++) rows[i] = new Row();
        cur = new Row();
        rDraw = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        rTris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        rReserved = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");
        ftmOn = FrameTimingManager.IsFeatureEnabled();
        device = SystemInfo.deviceModel + " | " + SystemInfo.graphicsDeviceName + " | " + SystemInfo.graphicsDeviceType + " | " + SystemInfo.processorCount
               + " cores | Unity " + Application.unityVersion + " | app " + Application.version + " | incremental GC " + (UnityEngine.Scripting.GarbageCollector.isIncremental ? "on" : "OFF")
               + " | frame timing " + (ftmOn ? "on" : "OFF");
        for (int i = 0; i < line.Length; i++) line[i] = new TextLine(LineCols);
        for (int i = 0; i < sumLine.Length; i++) sumLine[i] = new TextLine(SumCols);
        lineTex = new BitText(LineCols, line.Length);
        sumTex = new BitText(SumCols, sumLine.Length);
    }
    void OnDestroy()
    {
        rDraw.Dispose(); rTris.Dispose(); rReserved.Dispose(); if (inst == this) inst = null;
        if (lineTex != null) lineTex.Release();
        if (sumTex != null) sumTex.Release();
    }

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
        int frame = Time.frameCount;
        bool therm = Thermal.ReadFrame == frame, radio = TeamRadio.PlayFrame == frame;
        byte flags = (byte)((gcHit ? FlagGc : 0) | (Steps > 8 ? FlagSteps : 0) | (drop ? FlagDrop : 0) | (therm ? FlagTherm : 0) | (radio ? FlagRadio : 0));

        // graph ring
        gMs[gHead] = ms; gKb[gHead] = alloc / 1024f; gFl[gHead] = flags;
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

        // a long frame's time (unscaledDeltaTime) is mostly the previous frame's work: look at both frames' events
        byte why = (byte)(flags | prevFlags); prevFlags = flags;
        if (Racing) RowAdd(ms, tim, main, rend, gpu, alloc, gcHit ? 1 : 0, why);

        float now = Time.unscaledTime;
        if (now >= nextText) { nextText = now + 0.5f; Text(); aN = aTimN = aStepsMax = 0; aMs = aMain = aRend = aGpu = aWait = aPhys = aSteps = 0; aAlloc = 0; }
        if (ResultsUp && (sumDirty || now >= nextSum)) { nextSum = now + 1f; sumDirty = false; SummaryText(); }
        else if (!ResultsUp) sumDirty = true;
    }
    void Pop()
    {
        int t = (wHead - wCount + W) % W; float o = wMs[t];
        wSum -= o; wHist[Bin(o)]--; wCount--;
    }

    void RowAdd(float ms, bool tim, float main, float rend, float gpu, long alloc, int gc, byte why)
    {
        var r = cur;
        if (r.frames == 0) { r.t0 = raceClock; r.lockMin = Lock; }
        r.frames++; r.ms += ms; if (ms > r.maxMs) r.maxMs = ms; r.hist[Bin(ms)]++;
        if (ms > 1250f / Mathf.Max(1, Lock)) r.late++;
        if (ms > SpikeMs)
        {
            r.spk++;
            if ((why & FlagGc) != 0) r.sG++; else if ((why & FlagRadio) != 0) r.sR++; else if ((why & FlagTherm) != 0) r.sT++;
            else if ((why & (FlagSteps | FlagDrop)) != 0) r.sP++; else r.sU++;
        }
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

    // ---------------------------------------------------------------- text, written in place into bitmap textures
    const int LineCols = 120, SumCols = 180, CW = 7; // summary: CW characters per column
    readonly TextLine[] line = new TextLine[5];
    readonly TextLine[] sumLine = new TextLine[3 + Rows + 1];
    BitText lineTex, sumTex; float nextSum; bool sumDirty = true;
    string device; static Material glMat;

    static float Pct(int[] hist, int n, float p)
    {
        if (n <= 0) return float.NaN;
        int want = Mathf.Max(1, Mathf.CeilToInt(n * p)), acc = 0;
        for (int b = 0; b <= Bins; b++) { acc += hist[b]; if (acc >= want) return (b + 0.5f) * BinMs; }
        return Bins * BinMs;
    }
    float WinMax() { float m = 0; for (int k = 0; k < wCount; k++) { float v = wMs[(wHead - 1 - k + W) % W]; if (v > m) m = v; } return m; }
    static char YesNo(int s) => s == Thermal.Yes ? 'Y' : s == Thermal.No ? 'N' : '?';

    void Text()
    {
        float n = Mathf.Max(1, aN), tn = Mathf.Max(1, aTimN);
        float fps = aMs > 0 ? (float)(aN * 1000.0 / aMs) : 0f;
        var a = line[0].Clear();
        a.I(Lock).T(" LOCK  ").F(Thermal.Hz, 0).T(" Hz  120:").C(YesNo(Thermal.Can120)).T("  |  ").F(fps, 1).T(" fps  |  ms p50 ").F(Pct(wHist, wCount, 0.5f), 1).T("  p95 ").F(Pct(wHist, wCount, 0.95f), 1)
         .T("  max ").F(WinMax(), 1).T(" (10 s)  |  late ").F(LateShare(), 0).T("%").End();
        var b = line[1].Clear();
        b.T("main ").F(aTimN > 0 ? (float)(aMain / tn) : float.NaN, 1).T("  rend ").F(aTimN > 0 ? (float)(aRend / tn) : float.NaN, 1).T("  gpu ").F(aTimN > 0 && aGpu > 0 ? (float)(aGpu / tn) : float.NaN, 1)
         .T("  wait ").F(aTimN > 0 ? (float)(aWait / tn) : float.NaN, 1).T("  |  phys ").F((float)(aPhys / n), 2).T(" ms  ").F((float)(aSteps / n), 1).T(" st (max ").I(aStepsMax).T(")");
        if (SpiralDrops > 0) b.T("  drops ").I(SpiralDrops);
        b.End();
        var c = line[2].Clear();
        c.T("GC ").I(gcTotal).T("  alloc ").F(aAlloc / n / 1024f, 2).T(" KB/f ").F(aMs > 0 ? (float)(aAlloc / 1024.0 / (aMs * 0.001)) : 0f, 0).T(" KB/s  heap ").F(heapUsed / 1048576f, 0);
        if (rReserved.Valid && rReserved.LastValue > 0) c.T("/").F(rReserved.LastValue / 1048576f, 0);
        c.T(" MB  |  >50ms ").I(cur.spk).T(" (gc ").I(cur.sG).T(" radio ").I(cur.sR).T(" therm ").I(cur.sT).T(" phys ").I(cur.sP).T(" ? ").I(cur.sU).T(")  |  read ")
         .F(Thermal.LastReadMs, 1).T("/").F(Thermal.MaxReadMs, 0).T(" ms").End();
        var d = line[3].Clear();
        d.T("therm ");
        if (Thermal.Status >= 0) d.I(Thermal.Status); else d.C('-');
        d.T("  head ").F(Thermal.Headroom, 2).T("  |  bat ").F(Thermal.BatteryC, 1).T(" C  soc ").F(Thermal.SocC, 0).T(" C  |  cpu ").F(Thermal.CpuGHz, 2).T("/").F(Thermal.CpuMaxGHz, 2)
         .T(" GHz  gpu ").F(Thermal.GpuMHz, 0).T(" MHz  |  radio ").F(TeamRadio.LastPlayMs, 1).T(" ms").End();
        var e = line[4].Clear();
        e.T("res ").F(RenderScale.Scale * 100f, 0).T("% x ").F(Res * 100f, 0).T("%  |  draws ").I(aDraws).T("  tris ").I(aTris / 1000).T("k  |  gfx ").I(Gfx);
        if (PerfStats.Show) e.T("  |  STATS ON");
        e.End();
        for (int i = 0; i < line.Length; i++) lineTex.Write(i, line[i]);
        lineTex.Upload();
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
        float h = Screen.height, sw = Screen.width;
        float x0 = Mathf.Round(h * 0.02f), y0 = Mathf.Round(h * 0.02f);
        float gw = Mathf.Round(h * 0.75f), gh = Mathf.Round(h * 0.11f), ah = Mathf.Round(h * 0.035f);
        float k = TextScale(h, sw - 2 * x0, lineTex.PixelWidth);
        float tw = lineTex.PixelWidth * k, th = lineTex.PixelHeight * k;
        Box(new Rect(x0 - 4, y0 - 4, Mathf.Max(gw, tw) + 8, gh + ah + th + 14), 0.6f);
        Graph(new Rect(x0, y0, gw, gh), new Rect(x0, y0 + gh + 3, gw, ah));
        lineTex.Draw(new Rect(x0, y0 + gh + ah + 8, tw, th), k);
        if (ResultsUp) Summary(h, sw);
    }
    // the bitmap font's scale on screen: about 1/36 of the screen height per line, never wider than the room
    static float TextScale(float h, float room, int px)
    {
        float k = Mathf.Max(1f, Mathf.Round(h / 400f));
        if (px * k > room) k = Mathf.Max(0.5f, room / px);
        return k;
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
    static readonly Color cTherm = new Color(1f, 0.55f, 0.1f, 1f), cRadio = new Color(0.3f, 0.5f, 1f, 1f), cAlloc = new Color(0.75f, 0.45f, 1f, 0.95f), cBack = new Color(1f, 1f, 1f, 0.06f);

    void Graph(Rect r, Rect al)
    {
        var m = GlMat; if (m == null) return;
        const float MaxMs = 50f, SpanMs = 5000f, MaxKb = 8f;
        float target = 1000f / Mathf.Max(1, Lock), pxMs = r.width / SpanMs, k = r.height / MaxMs, ka = al.height / MaxKb;
        m.SetPass(0);
        GL.PushMatrix(); GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
        GL.Begin(GL.QUADS);
        GL.Color(cBack); Quad(r.xMin, r.yMin, r.xMax, r.yMax); Quad(al.xMin, al.yMin, al.xMax, al.yMax);
        float x = r.xMax, t = 0f;
        for (int i = 0; i < gCount && t < SpanMs; i++)
        {
            int j = (gHead - 1 - i + G) % G; float ms = gMs[j], w = Mathf.Min(ms, SpanMs - t) * pxMs;
            float bh = Mathf.Min(ms, MaxMs) * k, xl = Mathf.Max(r.xMin, x - Mathf.Max(w, 1f)), xm = Mathf.Max(x, xl + 3f);
            GL.Color(ms <= target * 1.25f ? cOk : ms <= target * 2f ? cSlow : cBad);
            Quad(xl, r.yMax - bh, x, r.yMax);
            float kb = gKb[j];
            if (kb > 0f) { GL.Color(cAlloc); Quad(xl, al.yMax - Mathf.Min(kb, MaxKb) * ka, x, al.yMax); }
            byte f = gFl[j];
            if ((f & FlagGc) != 0) { GL.Color(cGc); Quad(xl, r.yMin, xm, r.yMin + 5f); }
            if ((f & FlagTherm) != 0) { GL.Color(cTherm); Quad(xl, r.yMin + 6f, xm, r.yMin + 11f); }
            if ((f & FlagRadio) != 0) { GL.Color(cRadio); Quad(xl, r.yMin + 12f, xm, r.yMin + 17f); }
            if ((f & FlagSteps) != 0) { GL.Color(cSteps); Quad(xl, r.yMax - 4f, xm, r.yMax); }
            if ((f & FlagDrop) != 0) { GL.Color(Color.white); Quad(xl, r.yMax - 10f, xm, r.yMax - 5f); }
            x -= w; t += ms;
        }
        // reference lines: the lock's interval, 33.3 and 50 ms; 4 KB in the allocation strip
        GL.Color(cTarget); HLine(r, r.yMax - Mathf.Min(target, MaxMs) * k);
        GL.Color(cLine); HLine(r, r.yMax - 33.33f * k); HLine(r, r.yMin); HLine(al, al.yMax - 4f * ka);
        GL.End(); GL.PopMatrix();
    }
    static void HLine(Rect r, float y) => Quad(r.xMin, y - 0.5f, r.xMax, y + 0.5f);

    // ---------------------------------------------------------------- the results-screen summary
    static readonly string[] Head = { "t", "fps", "p50", "p95", "max", "late%", "main", "rend", "gpu", "phys", "st", "GC", "KB/s", "lock", "Hz", "therm", "head", "bat C", "soc C", "cpu", "gpuMHz", "idle%", "res%", ">50ms" };
    void Summary(float h, float sw)
    {
        int n = 3 + rowCount + (cur.frames > 0 ? 1 : 0);
        float k = TextScale(h, sw * 0.96f, sumTex.PixelWidth);
        float w = sumTex.PixelWidth * k, lh = BitText.LH * k;
        float x0 = Mathf.Round((sw - w) * 0.5f), y0 = Mathf.Round(h * 0.30f);
        Box(new Rect(x0 - 8, y0 - 6, w + 16, lh * n + 12), 0.82f);
        sumTex.Draw(new Rect(x0, y0, w, sumTex.PixelHeight * k), k);
    }
    void SummaryText()
    {
        var t0 = sumLine[0].Clear();
        t0.T("Last 2 min of racing, rows of 20 s (oldest first; * = still running). 120 Hz: ").C(YesNo(Thermal.Can120))
          .T(". >50ms = long frames (g gc, r radio, t thermal read, p physics, ? other). Screenshot this.").End();
        sumLine[1].Clear().T(device).End();
        var hd = sumLine[2].Clear();
        for (int c = 0; c < Head.Length; c++) { hd.Pad(c * CW); hd.T(Head[c]); }
        hd.End();
        int n = rowCount + (cur.frames > 0 ? 1 : 0);
        for (int i = 0; i < Rows + 1; i++)
        {
            var L = sumLine[3 + i].Clear();
            if (i < n) { bool live = i == rowCount; FillRow(L, live ? cur : rows[(rowHead - rowCount + i + rows.Length) % rows.Length], live); }
            L.End();
        }
        for (int i = 0; i < sumLine.Length; i++) sumTex.Write(i, sumLine[i]);
        sumTex.Upload();
    }
    void FillRow(TextLine L, Row r, bool live)
    {
        float fr = Mathf.Max(1, r.frames), tn = Mathf.Max(1, r.timN), sec = (float)Math.Max(r.ms * 0.001, 1e-3);
        int c = 0; int mm = (int)(r.t0 / 60), ss = (int)(r.t0 % 60);
        L.Pad(c++ * CW).I(mm).C(':'); if (ss < 10) L.C('0'); L.I(ss); if (live) L.C('*');
        L.Pad(c++ * CW).F(r.frames / sec, 1);
        L.Pad(c++ * CW).F(Pct(r.hist, r.frames, 0.5f), 1);
        L.Pad(c++ * CW).F(Pct(r.hist, r.frames, 0.95f), 1);
        L.Pad(c++ * CW).F(r.maxMs, 0);
        L.Pad(c++ * CW).F(100f * r.late / fr, 0);
        L.Pad(c++ * CW).F(r.timN > 0 ? (float)(r.main / tn) : float.NaN, 1);
        L.Pad(c++ * CW).F(r.timN > 0 ? (float)(r.rend / tn) : float.NaN, 1);
        L.Pad(c++ * CW).F(r.timN > 0 && r.gpu > 0 ? (float)(r.gpu / tn) : float.NaN, 1);
        L.Pad(c++ * CW).F((float)(r.phys / fr), 2);
        L.Pad(c++ * CW).I(r.maxSteps);
        L.Pad(c++ * CW).I(r.gc);
        L.Pad(c++ * CW).F(r.alloc / 1024f / sec, 1);
        L.Pad(c++ * CW).I(r.lockMin);
        L.Pad(c++ * CW).F(r.hz, 0);
        L.Pad(c++ * CW); if (r.therm >= 0) L.I(r.therm); else L.C('-');
        L.Pad(c++ * CW).F(r.head, 2);
        L.Pad(c++ * CW).F(r.bat, 1);
        L.Pad(c++ * CW).F(r.soc, 0);
        L.Pad(c++ * CW).F(r.cpu, 2);
        L.Pad(c++ * CW).F(r.gpuMhz, 0);
        L.Pad(c++ * CW).F(100f * r.touchless / fr, 0);
        L.Pad(c++ * CW).F(r.res * 100f, 0).C(r.gfx == 1 ? 'L' : r.gfx == 2 ? 'M' : 'H');
        L.Pad(c++ * CW).I(r.spk);
        if (r.spk > 0)
        {
            L.C(' ');
            if (r.sG > 0) L.C('g').I(r.sG);
            if (r.sR > 0) L.C('r').I(r.sR);
            if (r.sT > 0) L.C('t').I(r.sT);
            if (r.sP > 0) L.C('p').I(r.sP);
            if (r.sU > 0) L.C('?').I(r.sU);
        }
    }
}

// A line of text written in place into a string made once: the string object never changes, its characters do
// (written through a Span over the string's own memory). Unused room at the end is spaces. Only for text that is NOT
// drawn with IMGUI labels (UiKit.Text caches a GUIContent per string object and would keep showing the old text):
// FrameLog draws it through BitText.
public sealed class TextLine
{
    public readonly string s; readonly int cap; int n;
    public TextLine(int capacity) { cap = capacity; s = new string(' ', capacity); }
    public int Length => n;
    Span<char> Buf => MemoryMarshal.AsMemory(s.AsMemory()).Span;
    public TextLine Clear() { n = 0; return this; }
    public TextLine C(char ch) { if (n < cap) Buf[n++] = ch; return this; }
    // spaces up to column col (at least one space when the text already reaches it)
    public TextLine Pad(int col)
    {
        if (n > 0 && n >= col) return C(' ');
        var b = Buf;
        while (n < col && n < cap) b[n++] = ' ';
        return this;
    }
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

// Text drawn without IMGUI's text system: a fixed 6 x 11 bitmap font (Pillow's built-in default bitmap font, HPND
// licence; ASCII 32-126) written into a texture, which is drawn as one image. Writing and uploading allocate nothing.
public sealed class BitText
{
    public const int GW = 6, GH = 11, LH = 12;
    readonly int cols, rows, w, h; readonly Color32[] px; readonly Texture2D tex; bool dirty;
    public int PixelWidth => w; public int PixelHeight => h;
    static byte[] font;
    static readonly Color32 On = new Color32(255, 255, 255, 255), Off = new Color32(255, 255, 255, 0);

    public BitText(int cols, int rows)
    {
        this.cols = cols; this.rows = rows; w = cols * GW; h = rows * LH;
        px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = Off;
        tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        dirty = true; Upload();
        if (font == null)
        {
            font = new byte[FontHex.Length / 2];
            for (int i = 0; i < font.Length; i++) font[i] = (byte)(Hex(FontHex[2 * i]) * 16 + Hex(FontHex[2 * i + 1]));
        }
    }
    static int Hex(char c) => c <= '9' ? c - '0' : c - 'a' + 10;
    public void Release() { if (tex != null) UnityEngine.Object.Destroy(tex); }

    // row r (0 = top) shows t's characters (cut at the width)
    public void Write(int r, TextLine t)
    {
        if (r < 0 || r >= rows) return;
        string s = t.s; int n = Math.Min(s.Length, cols);
        for (int i = 0; i < cols; i++) Glyph(r, i, i < n ? s[i] : ' ');
        dirty = true;
    }
    void Glyph(int r, int col, char ch)
    {
        int g = ch == '·' ? '.' - 32 : ch < 32 || ch > 126 ? 0 : ch - 32;
        int x0 = col * GW, top = r * LH;
        for (int y = 0; y < LH; y++)
        {
            int bits = y < GH ? font[g * GH + y] : 0;
            int row = (h - 1 - (top + y)) * w + x0; // texture row 0 is the bottom
            for (int x = 0; x < GW; x++) px[row + x] = (bits >> (GW - 1 - x) & 1) != 0 ? On : Off;
        }
    }
    public void Upload()
    {
        if (!dirty || tex == null) return;
        dirty = false; tex.SetPixels32(px); tex.Apply(false);
    }
    // drawn k times its pixel size (sharp at whole numbers)
    public void Draw(Rect r, float k)
    {
        var fm = Mathf.Abs(k - Mathf.Round(k)) < 0.01f ? FilterMode.Point : FilterMode.Bilinear;
        if (tex.filterMode != fm) tex.filterMode = fm;
        GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
    }

    const string FontHex =
        "000000000000000000000000000018181818001800000000001414140000000000000014143e14143e14140000081e323c1e06363c08000000382a3c081e2a0e00000000001c30183e2c3e000000000c08100000000000000000040818181818080400000010080c0c0c0c081000" +
        "0000083c1824000000000000000008083e080800000000000000000000000c081000000000003e00000000000000000000000000180000000002020404080810100000001c36363636361c000000000c3c0c0c0c0c3f000000001c36060c18363e000000001c36061c06361c0000" +
        "0000060e16363f0606000000003e303c3606263c000000001c36303c36361c000000003e36060c0c1818000000001c36361c36361c000000001c36361e06361c0000000000000018000018000000000000001800001810200000000c1830180c000000000000003c003c00000000" +
        "000000180c060c180000000000001c260c180018000000001c32262a2a27301c000000003c1c143e363700000000003c363c36363c00000000001e363030361c00000000003c363636363c00000000003e303c30363e00000000003e303c30303800000000001c36303e361e0000" +
        "00000037363e36363700000000003c181818183c00000000001e0c0c2c2c3800000000003634383c363b000000000038303030363e00000000002236363e2a2a0000000000373a3a36363200000000001c363636361c00000000003c36363c303800000000001c363636361c0600" +
        "0000003c36363c363b00000000001e323c0e263c00000000003e1a1818183c000000000037363636361c00000000003736141c1c0800000000002b2a2a3e1c140000000000331e0c0c1e33000000000033331e0c0c1e00000000003e360c18363e000000001c1818181818181c00" +
        "000020201010080804040000001c0c0c0c0c0c0c1c000000081c36000000000000000000000000000000003f0000180804000000000000000000001c361e363f0000000030303c3636363c0000000000001c3630361c000000000e061e3636361f0000000000001c363e301e0000" +
        "00000e183e1818183e0000000000001b3636361e063c000030303c36363636000000000c003c0c0c0c3f000000000c003c0c0c0c0c0c3800003030363c383c37000000003c0c0c0c0c0c3f0000000000003c3e2a2a2a0000000000002c363636360000000000001c3636361c0000" +
        "000000003c3636363c3038000000001b3636361e060f00000000371d18183c0000000000001e381e073e0000000018183e18181b0e000000000000363636361f00000000000036361c1c080000000000002b2a3e1e140000000000003b1e0c1e37000000000000373636141c1830" +
        "000000003e2c18363e00000000060c0c180c0c0c0600000000080808080808080000003018180c1818183000000000001a2c0000000000";
}
