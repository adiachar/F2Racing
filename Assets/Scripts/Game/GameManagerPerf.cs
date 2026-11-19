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
    bool fpsHolding = true;                          // the last full window held the lock
    bool resStepped;                                 // this session's one resolution step is used

    // a session starts or the setting changed: Auto starts at 60 (or where this circuit settled before in this run)
    void FpsStart()
    {
        var r = FpsRungs;
        if (fpsIdx == 0)
        {
            int m = fpsMem != null && mapIdx >= 0 && mapIdx < fpsMem.Length ? fpsMem[mapIdx] : 0;
            fpsRung = Mathf.Clamp(m, 0, r.Length - 1);
        }
        else
        {
            int want = fpsIdx == 1 ? 60 : fpsIdx == 2 ? 40 : 30;
            fpsRung = r.Length - 1;
            for (int i = 0; i < r.Length; i++) if (r[i] <= want) { fpsRung = i; break; } // what Unity would round it to anyway
        }
        fpsLock = r[fpsRung]; Application.targetFrameRate = fpsLock;
        fpsAvg = fpsLock; fpsHolding = true; resStepped = false;
        FpsWindowReset(3f);
    }
    // a graphics setting changed: every circuit may hold a higher rate now
    void FpsForget() { fpsMem = null; }
    void FpsWindowReset(float settle) { fpsWinT = 0; fpsWinN = 0; fpsWinLate = 0; fpsSettle = Mathf.Max(fpsSettle, settle); }

    // once a frame (GameManager.Update, AutoQuality)
    void FramePacing()
    {
        float dt = Time.unscaledDeltaTime;
        fpsAvg += (1f / Mathf.Max(dt, 0.001f) - fpsAvg) * 0.05f; // the number shown (RaceHud.DrawFps)
        if (Paused || replaying) { FpsWindowReset(2f); return; } // (menus: capped at 30 by MenuStudio.cs)
        if (fpsSettle > 0) { fpsSettle -= Mathf.Min(dt, 0.1f); return; }       // (the loading frame's long dt does not count)
        fpsWinT += dt; fpsWinN++;
        if (dt > 1.25f / fpsLock) fpsWinLate++; // shown for one refresh too many (60 on 120 Hz: 25 ms instead of 16.7)
        if (fpsWinT < 3f) return;
        float avg = fpsWinN / fpsWinT, late = fpsWinLate / (float)fpsWinN;
        // one hitch (a GC, a sound load) is ~1 late frame in 180 and barely moves the average: it never counts
        fpsHolding = avg >= fpsLock * 0.92f && late <= 0.1f;
        FpsWindowReset(0f);
        if (fpsHolding) return;
        // 1. Auto frame rate: the next steady rate, for the rest of the session
        var r = FpsRungs;
        if (fpsIdx == 0 && fpsRung < r.Length - 1)
        {
            fpsRung++; fpsLock = r[fpsRung]; Application.targetFrameRate = fpsLock;
            if (mapIdx >= 0 && mapIdx < MapNames.Length) { if (fpsMem == null) fpsMem = new int[MapNames.Length]; fpsMem[mapIdx] = Mathf.Max(fpsMem[mapIdx], fpsRung); }
            fpsAvg = fpsLock; fpsHolding = true; FpsWindowReset(1.5f);
            Flash(FpsFlash(fpsLock));
            return;
        }
        // 2. Auto graphics: the live shadows, then a level (each one-way)
        if (gfxIdx == 0 && gfxLevel > 1)
        {
            if (ShadowsLive) { autoNoShadows = true; SetGraphics(gfxLevel); Flash("Shadows off to keep it smooth"); }
            else { SetGraphics(gfxLevel - 1); Flash("Graphics lowered to keep it smooth"); }
            FpsWindowReset(1.5f);
            return;
        }
        // 3. last: one resolution step per session (never back up mid-race, so the picture never shimmers between sizes)
        if (DynResWorks && !resStepped && renderScale > DynResMin + 0.01f)
        {
            resStepped = true; SetRenderScale(Mathf.Max(DynResMin, renderScale - 0.15f)); FpsWindowReset(1.5f);
        }
    }
    static string FpsFlash(int f) => f == 45 ? "Steady 45 fps for smoother play" : f == 40 ? "Steady 40 fps for smoother play" : "Steady 30 fps for smoother play";

    // ---------------------------------------------------------------- small no-garbage strings
    readonly string[] replayCaps = new string[121];
    string ReplayCaption(float secondsLeft)
    {
        int s = Mathf.Clamp(Mathf.RoundToInt(secondsLeft), 0, replayCaps.Length - 1);
        return replayCaps[s] ??= s + " s"; // the "REPLAY" label is drawn beside it (GameManager.OnGUI)
    }
}
