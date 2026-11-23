// GameManagerPerf.cs — performance helpers, another part of GameManager (see Docs/PERFORMANCE.md):
//  * the player's car in two levels of detail: the full model (car.bytes, ~190k triangles drawn) in the cockpit and
//    halo views and on High; the lighter one (car_lod.bytes, ~65k) in the chase, TV and replay views on Low/Medium.
//    The light model's simplified sidepods break up seen from the driver's seat, so the cockpit always keeps the full car.
//  * rivals far from both the camera and the player are stepped at 120 Hz instead of 240 Hz (Tools/PhysicsCheck:
//    COARSE=1 dotnet run -c Release -- race checks this: same resets, lap times within a few hundredths)
//  * per-level cuts that the other files read: CarModel.LowDetail, the car paint's cheap SubShader, the sun glare,
//    the cheap colour grade (F2_GRADE_LOW)
//  * steady frame pacing (FramePacing): the frame rate is always one the screen divides evenly (120 Hz: 60 / 40 / 30,
//    90 Hz: 45 / 30, 60 Hz: 60 / 30). Auto starts at 60 and, when a 3 s window can't hold it, drops to the next rate and
//    stays there for the session (one-way: no bouncing). Only then the graphics level (Auto), and last one resolution step
//  * the resolution is never hunted mid-race: at most one step down per session, never back up until the next setting
//    change. Dynamic resolution only where the API supports it (Vulkan, Metal); on OpenGL ES the screen buffer is
//    resized instead. Floor 75%; MSAA per level (4x Medium/High, 2x Low)
//  * frame-time investigation (Docs/PERF-INVESTIGATION.md): Auto steps the rate down when the phone reports heat
//    (Thermal.cs) for two reads in a row, or when two 3 s windows in a row miss the lock; its graphics-level step is
//    held to the next session start (a level change mid-race switched keywords, shader LODs and MSAA: a lap of
//    first-use hitches); 40 fps only on a panel that really runs at 120 Hz (asked for while 40 is wanted, read back,
//    else 60 or 30 only); the physics spiral guard's limits; the overlay's per-frame inputs (FrameLog.cs)
//  * round 3 (Adi: the fps sinks during a race; opening and closing Settings, changing nothing, brings it back): the race
//    keeps the state a pause re-applied, continuously. The panel is held at a mode the lock divides (Thermal.WantLock;
//    re-asserted when it drifts and on every resume), the CPU governor gets a performance hint every frame (Thermal.Hint,
//    no touch boost needed), a 3 s window that misses the lock while the CPU and GPU had time to spare is a pacing /
//    refresh-rate problem, not load (re-assert, no step down), and Auto steps back UP after a calm spell (a probe, with
//    a doubling wait when it fails). Auto's resolution step is undone at the next session start (it used to ratchet).
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    // The HUD and menus use only GUI.* (no GUILayout), so Unity can skip the extra Layout pass of OnGUI every frame
    // (OnGUI otherwise runs at least twice a frame, Layout + Repaint, plus once per touch event).
    void Awake() { useGUILayout = false; }

    // ---------------------------------------------------------------- player car level of detail
    const string FullCarRes = "Car/car", LiteCarRes = "Car/car_lod";
    CarModel carFull, carLite; bool lodReady, lodOff;

    bool WantFullCar(int level) => level >= 3 || (!replaying && (camMode == 2 || camMode == 3));

    // the model the current view needs, built the first time it is needed (the full car takes a moment to build:
    // on Low/Medium it is only built once the cockpit or halo view is chosen)
    CarModel MakePlayerCar(bool full) => full ? CarModel.CreateFromAsset(FullCarRes) : CarModel.CreateFromAsset(LiteCarRes, null, false, null, true);

    // the zoomed TV camera (and replays) can show a car far away big: no distance cull there
    float carCullAt; int carCullTv = -1;
    void ApplyCarCull()
    {
        int tv = camMode == TvMode || replaying ? 1 : 0;
        if (cam == null || tv == carCullTv) return;
        carCullTv = tv;
        for (int k = 0; k < carCull.Length; k++) carCull[k] = 0;
        carCull[LightBake.CarLayer] = tv == 1 ? 0f : carCullAt;
        cam.layerCullDistances = carCull;
    }

    // once a frame, before anything uses `model`
    void PickPlayerModel()
    {
        ApplyCarCull();
        if (model == null || lodOff) return;
        if (!lodReady)
        {
            lodReady = true;
            if (model.Res == FullCarRes) carFull = model; else if (model.Res == LiteCarRes) carLite = model;
            else { lodOff = true; return; } // the code-built stand-in car: nothing to swap
        }
        bool full = WantFullCar(gfxLevel);
        var want = full ? carFull : carLite;
        if (want == null)
        {
            want = MakePlayerCar(full);
            if (want == null) { if (!full) lodOff = true; return; } // no LOD asset: keep the full car
            if (full) carFull = want; else carLite = want;
        }
        if (want == model) return;
        want.CopyLook(model); // livery (player or team) and the tyre stripe
        want.transform.SetPositionAndRotation(model.transform.position, model.transform.rotation);
        want.gameObject.SetActive(true); model.gameObject.SetActive(false);
        model = want; // DriverFeel re-attaches the shift lights to the new steering wheel by itself
    }

    // ---------------------------------------------------------------- rivals at 120 Hz when nobody can see it
    // A rival more than CoarseGap metres along the track from the player and outside the camera's detail range
    // (rivalNear, about 30 m), from one minute after the start (the lap-1 pack stays at 240 Hz), skips every other 240 Hz tick and then steps 2 ticks at once. While it waits its state
    // is one tick old, so it is drawn one tick further forward (RivalLag). It returns to 240 Hz with one catch-up step.
    const double CoarseGap = 150;
    double[] rivalLag = new double[0];

    // true: this tick's step for rival i is done here (or skipped)
    bool RivalCoarse(int i, AiDriver d)
    {
        if (rivalLag.Length != rivals.Count) rivalLag = new double[rivals.Count];
        bool lagging = rivalLag[i] > 0;
        bool coarse = !replaying && raceTime > Countdown + 60 && i < rivalNear.Count && !rivalNear[i]
                      && idx >= 0 && d.idx >= 0 && !playground && TrackGap(idx, d.idx) > CoarseGap;
        if (!coarse)
        {
            if (!lagging) return false;
            d.car.Step(RivalStartInput(i, d), 2 * H); rivalLag[i] = 0; // catch up, then 240 Hz again
            return true;
        }
        if (lagging) { d.car.Step(RivalStartInput(i, d), 2 * H); rivalLag[i] = 0; }
        else rivalLag[i] = H;
        return true;
    }
    double RivalLag(int i) => i < rivalLag.Length ? rivalLag[i] : 0;
    double TrackGap(int a, int b) { int g = System.Math.Abs(a - b); g = System.Math.Min(g, track.N - g); return g * track.ds; }

    // ---------------------------------------------------------------- per graphics level (ApplyGraphics)
    static Shader paintShaderLod; static bool paintShaderTried; readonly float[] carCull = new float[32];
    void ApplyPerfLevel(int level)
    {
        bool low = level <= 1;
        CarModel.LowDetail = low;                 // no wheel-face quads, no sparks / tyre smoke
        Sky.NoGlare = low;                        // no additive glare quad (up to a quarter of the screen)
        if (low) Shader.EnableKeyword("F2_GRADE_LOW"); else Shader.DisableKeyword("F2_GRADE_LOW"); // Grade.cginc: tone curve + saturation only
        if (!paintShaderTried) { paintShaderTried = true; paintShaderLod = Resources.Load<Shader>("CarPaint"); }
        if (paintShaderLod != null) paintShaderLod.maximumLOD = low ? 100 : 200; // CarPaint: per-vertex SubShader (LOD 100) on Low
        // cars (their own layer) are not drawn beyond this distance: ~35 draw calls each and only a few pixels long
        // there (a car 450 m away is ~10 px at 720p). 0 = the far clip plane.
        carCullAt = low ? 450f : level == 2 ? 800f : 0f; carCullTv = -1; ApplyCarCull();
    }

    // ---------------------------------------------------------------- dynamic resolution
    // ScalableBufferManager only works on Vulkan / Metal / D3D12. On OpenGL ES (many low-end phones, and Vulkan is
    // blocked on some) it silently does nothing, so AutoQuality must not wait for it, and the resolution setting
    // sets a smaller screen buffer instead (the HUD scales with Screen.height, so its layout stays the same).
    static int dynRes = -1;
    static bool DynResWorks
    {
        get
        {
            if (dynRes < 0)
            {
                var t = SystemInfo.graphicsDeviceType;
                dynRes = t == UnityEngine.Rendering.GraphicsDeviceType.Vulkan || t == UnityEngine.Rendering.GraphicsDeviceType.Metal
                      || t == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 ? 1 : 0;
            }
            return dynRes == 1;
        }
    }
    // Below about 75% the picture goes visibly blocky and every edge and thin pole swims, so the resolution never goes
    // below that (or below the "Resolution scale" setting, if the player chose less).
    float DynResMin => Mathf.Min(0.75f, ResMax);
    bool ShadowsLive => gfxLevel >= 2 && shadowsOn && !autoNoShadows && !LightBake.Active;

    // MSAA samples per graphics level (QualitySettings clamps to what the GPU supports). Tile GPUs resolve MSAA on
    // the chip, so 4x costs roughly 5-15% of the GPU frame and 2x less; a lower resolution looks far worse.
    static int MsaaFor(int level) => level <= 1 ? 2 : 4;

    // the 3D picture's share of the screen resolution. Dynamic resolution is switched on only while it is under 100%
    // (at 100% the camera draws straight into the screen buffer, no scaled target and no upscale pass).
    float renderScale = 1f;
    void SetRenderScale(float s)
    {
        renderScale = s;
        if (DynResWorks) { if (cam != null) cam.allowDynamicResolution = s < 0.999f; ScalableBufferManager.ResizeBuffers(s, s); }
        else FixedResolution(s);
    }

    int nativeW, nativeH; float fixedScale = 1f;
    void FixedResolution(float k)
    {
        if (nativeW == 0) { nativeW = RenderScale.NativeW > 0 ? Mathf.RoundToInt(RenderScale.NativeW * RenderScale.Scale) : Screen.width; nativeH = RenderScale.NativeH > 0 ? Mathf.RoundToInt(RenderScale.NativeH * RenderScale.Scale) : Screen.height; } // the base render scale (RenderScale.cs) is the 100% size // before any change: the panel's own size
        if (Mathf.Approximately(k, fixedScale)) return;
        fixedScale = k;
        Screen.SetResolution(Mathf.RoundToInt(nativeW * k), Mathf.RoundToInt(nativeH * k), true);
    }

    // ---------------------------------------------------------------- steady frame pacing
    // An uneven rate judders: 33 fps on a 60 / 120 Hz panel is a mix of 16.7, 25 and 33 ms frames, so the car and the
    // world move in uneven steps. A rate the panel divides evenly shows every frame for the same number of refreshes.
    // Unity on Android rounds Application.targetFrameRate DOWN to a divisor of the panel's current rate, and with
    // Optimized Frame Pacing (BuildScript) the display is asked for a mode the target divides (40 -> 120 Hz).
    static int[] fpsRungs;                           // the steady rates this screen can show, highest first
    static int[] FpsRungs => fpsRungs ??= MakeFpsRungs();
    static int[] MakeFpsRungs()
    {
        bool hz120 = false, hz90 = false, hz60 = false;
        try
        {
            AddHz(Screen.currentResolution.refreshRateRatio.value, ref hz120, ref hz90, ref hz60);
            var modes = Screen.resolutions; // on Android: the display's modes, with every refresh rate it offers
            for (int i = 0; i < modes.Length; i++) AddHz(modes[i].refreshRateRatio.value, ref hz120, ref hz90, ref hz60);
        }
        catch (System.Exception) { }
        bool r60 = hz60 || hz120 || !hz90, r40 = hz120, r45 = hz90 && !hz120;
        int n = 1 + (r60 ? 1 : 0) + (r40 ? 1 : 0) + (r45 ? 1 : 0), k = 0;
        var r = new int[n];
        if (r60) r[k++] = 60;
        if (r45) r[k++] = 45;
        if (r40) r[k++] = 40;
        r[k] = 30;
        return r;
    }
    static void AddHz(double hz, ref bool h120, ref bool h90, ref bool h60)
    {
        if (hz > 117.5 && hz < 122.5) h120 = true; else if (hz > 87.5 && hz < 92.5) h90 = true; else if (hz > 57.5 && hz < 62.5) h60 = true;
    }

    int fpsLock = 60, fpsRung;                       // the rate asked for (Application.targetFrameRate) and its place in FpsRungs
    static int[] fpsMem;                             // Auto: the rung each circuit settled on in this run (0 = none yet)
    float fpsWinT, fpsSettle; int fpsWinN, fpsWinLate; // the 3 s measuring window; seconds to wait before measuring
    double fpsWinBusy; int fpsWinFt; bool fpsWinGpu;  // the window's frame timing: sum of max(main, render, GPU) ms, frames with it, a GPU timer seen
    float fpsCalm, fpsUpWait = UpWait0, fpsProbeHeld; bool fpsProbe, fpsWasPaused; // Auto's step back up (below)
    const float UpWait0 = 45f, UpWaitMax = 480f, ProbeHold = 30f;
    int fpsBad, heatReads, heatSeen, fpsWinShort;                 // failing windows in a row; thermal reads in a row that said "hot"
    bool fpsHolding = true;                          // the last full window held the lock
    bool resStepped;                                 // this session's one resolution step is used

    // a session starts or the setting changed: Auto starts at 60 (or where this circuit settled before in this run).
    // Adi's Redmi Note 12 Pro (Mali-G68, 20 Nov): at 60 the GPU needs 12.5-14.5 ms at 71% x 75% and holds 56-60 fps;
    // the old "start Mali at 40" rule ran on a panel MIUI keeps at 60 Hz, where 40 can never be even (late 100%).
    // "40 fps" in Settings needs the panel at 120 Hz: without it (Thermal.Can120 == No) it runs at a steady 30.
    void FpsStart()
    {
        var r = FpsRungs;
        if (fpsIdx == 0)
        {
            int m = fpsMem != null && mapIdx >= 0 && mapIdx < fpsMem.Length ? fpsMem[mapIdx] : 0;
            fpsRung = Mathf.Clamp(m, 0, r.Length - 1);
            if (r[fpsRung] == 40 && Thermal.Can120 == Thermal.No) fpsRung = r.Length - 1;
        }
        else
        {
            int want = fpsIdx == 1 ? 60 : fpsIdx == 2 ? 40 : 30;
            fpsRung = r.Length - 1;
            for (int i = 0; i < r.Length; i++) if (r[i] <= want) { fpsRung = i; break; } // what Unity would round it to anyway
            if (r[fpsRung] == 40 && Thermal.Can120 == Thermal.No) fpsRung = r.Length - 1; // 40 needs 120 Hz: steady 30
        }
        fpsLock = r[fpsRung]; Application.targetFrameRate = fpsLock;
        fpsAvg = fpsLock; fpsHolding = true; resStepped = false; fpsBad = 0; heatReads = 0;
        fpsCalm = 0f; fpsProbe = false; fpsUpWait = UpWait0;
        if (renderScale < ResMax - 0.001f) SetRenderScale(ResMax); // Auto's resolution step held for one session only (it ratcheted down race after race)
        FpsWindowReset(3f);
        FpsNamesFor120();
        FrameLog.Mark('F');
    }
    // the Frame rate setting's "40" says when this screen cannot show it
    static void FpsNamesFor120() { FpsNames[2] = Thermal.Can120 == Thermal.No ? "40 (needs 120 Hz)" : "40 fps"; }

    // once a frame (GameManager.Update): the display mode follows the lock. In a session (racing or paused in it) the
    // panel is held at the slowest mode the lock divides (Thermal.WantLock: 60 / 30 fps -> 60 Hz, 40 -> 120 Hz), so
    // MIUI's idle refresh drop (no touches with tilt steering) cannot switch it under the game; the menu pages
    // (MenuStudio caps them at 30) give the choice back to the system, which saves battery. A 40 lock on a panel that
    // stays at 60 Hz (read back after ~2 s, or seen in the frame times) falls to a steady 30 at once.
    void DisplayModeTick()
    {
        Thermal.WantLock(ui != null && ui.Showing && !replaying ? 0 : fpsLock);
        if (fpsLock == 40 && Thermal.Can120 == Thermal.No) FpsNo120();
        FrameTimesTick();
    }

    // Unity's frame timing (FrameTimingManager, a few frames late), read once a frame for the frame pacing, the
    // performance hint and the overlay (FrameLog.Ft*). 0 / invalid where the phone gives no timer.
    static readonly FrameTiming[] ftBuf = new FrameTiming[1]; static int ftOn = -1;
    void FrameTimesTick()
    {
        if (ftOn < 0) ftOn = FrameTimingManager.IsFeatureEnabled() ? 1 : 0;
        FrameLog.FtValid = false;
        if (ftOn == 0) return;
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, ftBuf) <= 0) return;
        float main = (float)ftBuf[0].cpuMainThreadFrameTime, rend = (float)ftBuf[0].cpuRenderThreadFrameTime;
        if (!(main > 0f)) return;
        FrameLog.FtValid = true; FrameLog.FtMain = main; FrameLog.FtRend = rend;
        FrameLog.FtGpu = (float)ftBuf[0].gpuFrameTime; FrameLog.FtWait = (float)ftBuf[0].cpuMainThreadPresentWaitTime;
        // the CPU governor's hint: this frame's CPU work against the lock's frame time (racing only; menus idle)
        if (!Paused && !replaying) Thermal.Hint((long)(Mathf.Max(main, rend) * 1e6f), (long)(1e9f / Mathf.Max(1, fpsLock)));
    }
    void FpsNo120()
    {
        var r = FpsRungs;
        FpsNamesFor120();
        fpsRung = r.Length - 1; fpsLock = r[fpsRung];
        if (Application.targetFrameRate == 40) Application.targetFrameRate = fpsLock; // (not in the menus' own 30 cap)
        if (fpsIdx == 0 && mapIdx >= 0 && mapIdx < MapNames.Length) { if (fpsMem == null) fpsMem = new int[MapNames.Length]; fpsMem[mapIdx] = Mathf.Max(fpsMem[mapIdx], fpsRung); }
        fpsAvg = fpsLock; fpsHolding = true; fpsBad = 0; FpsWindowReset(1.5f);
        Flash("Screen stays at 60 Hz: steady 30 fps");
    }
    // a graphics setting changed: every circuit may hold a higher rate now
    void FpsForget() { fpsMem = null; }
    void FpsWindowReset(float settle) { fpsWinT = 0; fpsWinN = 0; fpsWinLate = 0; fpsWinShort = 0; fpsWinBusy = 0; fpsWinFt = 0; fpsWinGpu = false; fpsSettle = Mathf.Max(fpsSettle, settle); }

    // once a frame (GameManager.Update, AutoQuality)
    void FramePacing()
    {
        float dt = Time.unscaledDeltaTime;
        fpsAvg += (1f / Mathf.Max(dt, 0.001f) - fpsAvg) * 0.05f; // the number shown (RaceHud.DrawFps)
        bool paused = Paused || replaying;
        if (fpsWasPaused && !paused) Thermal.Reassert(); // back from a pause: the display mode asked for again
        fpsWasPaused = paused;
        if (paused) { FpsWindowReset(2f); return; } // (menus: capped at 30 by MenuStudio.cs)
        // 0. the phone reports heat (Thermal.cs: Android thermal status MODERATE or more, or headroom >= 0.9, near the
        //    SEVERE level) on two reads in a row (~4 s: the headroom forecast is noisy): take the next steady rate now,
        //    before the clocks drop and the rate collapses (a 60 that throttles ends at 20-30 with judder). At most
        //    once every 30 s; one-way for the session.
        if (Thermal.Reads != heatSeen) { heatSeen = Thermal.Reads; heatReads = Thermal.Hot ? heatReads + 1 : 0; }
        if (fpsIdx == 0 && heatReads >= 2 && fpsRung < FpsRungs.Length - 1 && Time.unscaledTime >= fpsHeatT)
        {
            fpsHeatT = Time.unscaledTime + 30f; heatReads = 0; FpsStepDown(true);
            return;
        }
        if (fpsSettle > 0) { fpsSettle -= Mathf.Min(dt, 0.1f); return; }       // (the loading frame's long dt does not count)
        fpsWinT += dt; fpsWinN++;
        if (dt > 1.25f / fpsLock) fpsWinLate++; // shown for one refresh too many (60 on 120 Hz: 25 ms instead of 16.7)
        else if (dt < 0.75f / fpsLock) fpsWinShort++; // shown early: a 40 lock on a 60 Hz panel alternates 16.7 / 33.3 ms
        if (FrameLog.FtValid) { fpsWinBusy += Mathf.Max(FrameLog.FtMain, Mathf.Max(FrameLog.FtRend, FrameLog.FtGpu)); fpsWinFt++; if (FrameLog.FtGpu > 0f) fpsWinGpu = true; }
        if (fpsWinT < 3f) return;
        float avg = fpsWinN / fpsWinT, late = fpsWinLate / (float)fpsWinN;
        float busy = fpsWinFt > 0 && fpsWinGpu ? (float)(fpsWinBusy / fpsWinFt) : -1f; // ms of real work per frame (-1: unknown)
        FpsWindowReset(0f);
        // a 40 lock that the panel cannot show: on 60 Hz Unity rounds 40 down to 30 (every frame 33 ms: Adi's "late
        // 100%") or the frames alternate 16.7 / 33.3 ms. -> steady 30 at once, and no 40 for the rest of the app run.
        // (A GPU too slow for 40 on a panel that does read 120 Hz is the normal step below.)
        if (fpsLock == 40 && late >= 0.4f && (fpsWinShort >= fpsWinN * 0.2f || !(Thermal.Hz >= 100f))) { Thermal.MarkNo120(); FpsNo120(); return; }
        // holding: late <= ~5% (one hitch, a GC or a sound load, is ~1 late frame in 180 and never counts). Failing:
        // late > 8% or the average under 92% of the lock, in two windows in a row (6 s): only then a step, so a single
        // bad window (a crash, a pack of cars) does not cost the session its frame rate
        bool bad = avg < fpsLock * 0.92f || late > 0.08f;
        fpsHolding = !bad;
        // a missed window while the CPU threads and the GPU had a quarter of the frame to spare is not load: the frames
        // were held back (the panel's refresh changed under the game, the frame pacing). Ask for the display mode again
        // and keep the rate: before, two such windows cost Auto its 60 for the rest of the session (one-way)
        if (bad && busy >= 0f && busy < 0.75f * 1000f / fpsLock)
        {
            FrameLog.PacingFixes++; FrameLog.Mark('P'); Thermal.Reassert();
            fpsBad = 0; fpsCalm = 0f; return;
        }
        fpsBad = bad ? fpsBad + 1 : 0;
        if (!bad) FpsUpTick();
        else fpsCalm = 0f;
        if (fpsBad < 2) return;
        fpsBad = 0;
        // 1. Auto frame rate: the next steady rate (until a calm spell lets it try the higher one again: FpsUpTick)
        var r = FpsRungs;
        if (fpsIdx == 0 && fpsRung < r.Length - 1) { FpsStepDown(false); return; }
        // 2. Auto graphics: the live shadows, then a level (each one-way), from the NEXT session start
        //    (AutoGfxAtSessionStart). Changing it mid-race switched the F2_GRADE_LOW keyword, the Road / CarPaint shader
        //    LODs, anisotropic filtering and MSAA 4x -> 2x at once: every new shader variant and pipeline state is
        //    created the first time it is drawn, so the next lap hitched at new places (Docs/PERF-INVESTIGATION.md).
        if (gfxIdx == 0 && gfxLevel > 1 && !autoGfxPending)
        {
            autoGfxPending = true; Flash("Lower graphics from the next session");
            FpsWindowReset(1.5f);
            return;
        }
        // 3. last: one resolution step per session (never back up mid-race, so the picture never shimmers between sizes)
        if (DynResWorks && !resStepped && renderScale > DynResMin + 0.01f)
        {
            resStepped = true; SetRenderScale(Mathf.Max(DynResMin, renderScale - 0.15f)); FpsWindowReset(1.5f); FrameLog.Mark('R');
        }
    }
    // Auto's step back UP: after a calm spell at a lower rate (UpWait0 s of holding windows, the phone not warm), try the
    // next rate again for ProbeHold s. If it holds, it stays (and this circuit's memory is lowered); if it fails, the
    // normal step down brings the rate back and the next wait doubles (up to 8 min). So one bad patch (a pacing glitch, a
    // lost touch boost, a warm minute) no longer costs a whole race its frame rate.
    void FpsUpTick()
    {
        if (fpsIdx != 0) return;
        bool cool = !Thermal.Hot && !(Thermal.Headroom >= 0.8f);
        if (fpsProbe)
        {
            fpsProbeHeld += 3f;
            if (fpsProbeHeld >= ProbeHold)
            {
                fpsProbe = false; fpsUpWait = UpWait0; fpsCalm = 0f;
                if (fpsMem != null && mapIdx >= 0 && mapIdx < fpsMem.Length) fpsMem[mapIdx] = fpsRung;
            }
            return;
        }
        if (fpsRung == 0 || !cool) { fpsCalm = 0f; return; }
        fpsCalm += 3f;
        if (fpsCalm < fpsUpWait) return;
        var r = FpsRungs; int up = fpsRung - 1;
        if (r[up] == 40 && Thermal.Can120 == Thermal.No && up > 0) up--; // 40 needs 120 Hz
        if (r[up] == 40 && Thermal.Can120 == Thermal.No) { fpsCalm = 0f; return; }
        fpsRung = up; fpsLock = r[fpsRung]; Application.targetFrameRate = fpsLock;
        fpsProbe = true; fpsProbeHeld = 0f; fpsCalm = 0f; fpsBad = 0; fpsAvg = fpsLock; fpsHolding = true;
        FrameLog.UpProbes++; FrameLog.Mark('U');
        FpsWindowReset(1.5f);
    }

    float fpsHeatT;                                  // Auto's next allowed step for heat (Time.unscaledTime)
    void FpsStepDown(bool hot)
    {
        var r = FpsRungs;
        bool probeFailed = fpsProbe;
        if (fpsProbe) { fpsProbe = false; fpsUpWait = Mathf.Min(UpWaitMax, fpsUpWait * 2f); }
        fpsCalm = 0f;
        FrameLog.Mark('D');
        fpsRung++;
        if (r[fpsRung] == 40 && Thermal.Can120 == Thermal.No && fpsRung < r.Length - 1) fpsRung++; // 40 needs 120 Hz
        fpsLock = r[fpsRung]; Application.targetFrameRate = fpsLock; // (a 40 asks for 120 Hz: DisplayModeTick, read back after ~2 s)
        if (mapIdx >= 0 && mapIdx < MapNames.Length) { if (fpsMem == null) fpsMem = new int[MapNames.Length]; fpsMem[mapIdx] = Mathf.Max(fpsMem[mapIdx], fpsRung); }
        fpsAvg = fpsLock; fpsHolding = true; fpsBad = 0; FpsWindowReset(1.5f);
        if (!probeFailed) Flash(hot ? FpsFlashHot(fpsLock) : FpsFlash(fpsLock)); // (a failed try at the higher rate goes back quietly)
    }
    static string FpsFlash(int f) => f == 45 ? "Steady 45 fps for smoother play" : f == 40 ? "Steady 40 fps for smoother play" : "Steady 30 fps for smoother play";
    static string FpsFlashHot(int f) => f == 45 ? "Phone is warm: steady 45 fps" : f == 40 ? "Phone is warm: steady 40 fps" : "Phone is warm: steady 30 fps";

    // Auto's graphics step (FramePacing 2.), applied when the next session starts, before its world is built
    bool autoGfxPending;
    void AutoGfxAtSessionStart()
    {
        if (!autoGfxPending) return;
        autoGfxPending = false;
        if (gfxIdx != 0 || gfxLevel <= 1) return; // the player chose a level meanwhile
        if (ShadowsLive) { autoNoShadows = true; SetGraphics(gfxLevel); } else SetGraphics(gfxLevel - 1);
        FpsForget(); // the lower level may hold a higher rate: every circuit is measured again
    }

    // ---------------------------------------------------------------- physics spiral guard (GameManager.Update)
    // the step loop stops early only after SpiralMinSteps steps AND more than SpiralMs of wall time in one frame
    const int SpiralMinSteps = 8; const double SpiralMs = 20;
    static readonly long SpiralTicks = (long)(System.Diagnostics.Stopwatch.Frequency * SpiralMs / 1000.0);

    // ---------------------------------------------------------------- the frame-time overlay's inputs (FrameLog.cs)
    int flGfxSeen = -1; bool flMenuSeen;
    void FrameLogFeed()
    {
        FrameLog.Physics(0, 0); // (set again after the step loop when it runs this frame)
        FrameLog.Racing = !Paused && !replaying && intro == null;
        FrameLog.ResultsUp = ui != null && ui.page == MenuUI.Page.Results && !replaying && !menuOpen;
        FrameLog.Lock = fpsLock; FrameLog.Res = renderScale; FrameLog.Gfx = gfxLevel;
        // events for the results table: S = the settings were opened in a session, G = the graphics level changed
        if (menuOpen != flMenuSeen) { flMenuSeen = menuOpen; if (menuOpen && (ui == null || !ui.Showing)) FrameLog.Mark('S'); }
        if (gfxLevel != flGfxSeen) { if (flGfxSeen >= 0) FrameLog.Mark('G'); flGfxSeen = gfxLevel; }
        // what grows in a race (round 3): replay poses, rivals with a damaged wing (their LODs are off: CarLod.cs)
        FrameLog.Replay = recCount;
        int dmg = 0; for (int i = 0; i < rivals.Count; i++) if (rivals[i].car.damage.WingsDamaged) dmg++;
        FrameLog.Damaged = dmg;
    }

    // ---------------------------------------------------------------- small no-garbage strings
    readonly string[] replayCaps = new string[121];
    string ReplayCaption(float secondsLeft)
    {
        int s = Mathf.Clamp(Mathf.RoundToInt(secondsLeft), 0, replayCaps.Length - 1);
        return replayCaps[s] ??= s + " s"; // the "REPLAY" label is drawn beside it (GameManager.OnGUI)
    }
}
