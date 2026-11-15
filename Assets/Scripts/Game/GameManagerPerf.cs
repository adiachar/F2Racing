// GameManagerPerf.cs — performance helpers, another part of GameManager (see Docs/PERFORMANCE.md):
//  * the player's car in two levels of detail: the full model (car.bytes, ~190k triangles drawn) in the cockpit and
//    halo views and on High; the lighter one (car_lod.bytes, ~65k) in the chase, TV and replay views on Low/Medium.
//    The light model's simplified sidepods break up seen from the driver's seat, so the cockpit always keeps the full car.
//  * rivals far from both the camera and the player are stepped at 120 Hz instead of 240 Hz (Tools/PhysicsCheck:
//    COARSE=1 dotnet run -c Release -- race checks this: same resets, lap times within a few hundredths)
//  * per-level cuts that the other files read: CarModel.LowDetail, the car paint's cheap SubShader, the sun glare,
//    the cheap colour grade (F2_GRADE_LOW)
//  * dynamic resolution only where the graphics API supports it (Vulkan, Metal); on OpenGL ES the resolution
//    setting changes the screen buffer size instead. Floor 75%; MSAA per level (4x Medium/High, 2x Low)
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
    // Below about 75% the picture goes visibly blocky and every edge and thin pole swims, so dynamic resolution stops
    // there (or at the "Resolution scale" setting, if the player chose less). The old floor was 60%.
    float DynResMin => Mathf.Min(0.75f, ResMax);
    // Auto's first resolution steps stop at 85%; the shadows go next, then the rest of the way to DynResMin.
    bool ShadowsLive => gfxLevel >= 2 && shadowsOn && !autoNoShadows && !LightBake.Active;
    float AutoResFloor => gfxIdx == 0 && ShadowsLive ? Mathf.Max(DynResMin, Mathf.Min(0.85f, ResMax)) : DynResMin;

    // MSAA samples per graphics level (QualitySettings clamps to what the GPU supports). Tile GPUs resolve MSAA on
    // the chip, so 4x costs roughly 5-15% of the GPU frame and 2x less; a lower resolution looks far worse.
    static int MsaaFor(int level) => level <= 1 ? 2 : 4;

    int nativeW, nativeH; float fixedScale = 1f;
    void FixedResolution(float k)
    {
        if (nativeW == 0) { nativeW = Screen.width; nativeH = Screen.height; } // before any change: the panel's own size
        if (Mathf.Approximately(k, fixedScale)) return;
        fixedScale = k;
        Screen.SetResolution(Mathf.RoundToInt(nativeW * k), Mathf.RoundToInt(nativeH * k), true);
    }

    // ---------------------------------------------------------------- small no-garbage strings
    readonly string[] replayCaps = new string[121];
    string ReplayCaption(float secondsLeft)
    {
        int s = Mathf.Clamp(Mathf.RoundToInt(secondsLeft), 0, replayCaps.Length - 1);
        return replayCaps[s] ??= "<b>REPLAY</b>  " + s + "s";
    }
}
