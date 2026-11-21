// RenderScale.cs — the base render resolution on phones (Docs/PERF-ARCHITECTURE.md, "Render resolution").
//
// The problem: Unity on Android renders at the panel's native size unless told otherwise. A Redmi Note 12 Pro is
// 2400 x 1080 = 2.6 Mpx: 25% more pixels than a 1080p TV, on a 6.7" screen, for a Mali-G68 MC4 (4 shader cores).
// Every pixel pays the road / ground / grade shader, 4x MSAA, aniso and the alpha-tested foliage. Real Racing 3 and
// the other 60 fps mobile racers draw the 3D at 0.6-0.8 of native on such phones.
//
// The base scale: about 1.3 Mpx (2400 x 1080 -> 1700 x 765, 0.71 of native; 1080 x 2340 -> 0.72; 1600 x 720 phones
// stay at 100%), never below 0.6 of native. Setting (PlayerPrefs "renderBase", percent of native; 0 = Auto as above):
// PerfLab cycles it (Auto / 100 / 85 / 70 / 60) for A/B tests. A change made in a session applies at the next frame.
//
// Two ways to get the 3D drawn at that scale (PlayerPrefs "renderHud", PerfLab "HUD native / HUD scaled"):
//  * HUD native (default, Docs/PERFORMANCE.md "Clarity"): the screen stays at the panel's size. The race camera draws into an
//    off-screen target of the scaled size (4x / 2x MSAA kept on the chip: memoryless samples and depth), and one
//    full-screen pass (Upscale.shader: "sharp bilinear" + a 5-tap contrast-adaptive sharpen, RenderScale.Cas) stretches it to the screen; the HUD and
//    menus (IMGUI, drawn after every camera) then land at native resolution, so text and icons are crisp. Cost on a
//    Mali-G68: ~0.8-1.2 ms GPU (a 1.3 Mpx read + a 2.6 Mpx write once a frame); the 3D costs the same as before.
//  * HUD scaled (the old way): Screen.SetResolution shrinks the window surface and the display compositor stretches
//    everything, HUD included, for free (no extra pass), so text is a little soft.
// Everything else that scales the picture composes with this: Auto's resolution step on Vulkan
// (ScalableBufferManager: read here and folded into the off-screen size) and, on GLES, GameManagerPerf.FixedResolution
// (it shrinks the screen; with HUD native it should set RenderScale.Extra instead: request in Docs/COORDINATION.md).
//
// Adaptive scale (Auto only): while racing, the GPU time (FrameTimingManager) is averaged over 10 s windows against the
// frame-rate lock's budget. At the next session start (the menu hands the 3D back, behind the loading screen; never
// mid-race) the scale goes up 0.05 if every window of the last session stayed under 60% of the budget (at least 30 s
// measured, phone not hot), down 0.05 if one went over 85% or the phone got hot. Range: the Auto base .. 0.85 of
// native. Saved in PlayerPrefs "renderAdapt". GPU time 0 / missing (WebGL, drivers without timers): no change.
using UnityEngine;
using UnityEngine.Rendering;

public static class RenderScale
{
    public const float PixelBudget = 1.30e6f, MinScale = 0.6f, MaxAdaptScale = 0.85f, AdaptStep = 0.05f;
    public static int NativeW, NativeH;     // the panel's own size, read once before anything changed it
    public static float Scale = 1f;         // the base scale in use
    public static float Extra = 1f;         // a further factor for the 3D only (HUD native; GLES resolution setting)
    public static float Sharp = 0.5f;       // Upscale.shader's sharp-bilinear amount
    public static float Cas = 0.6f;         // Upscale.shader's contrast-adaptive sharpen (0 = off: the one-read pass; Low: off)
    public static int Pct                   // the setting: 0 = Auto, else percent of native
    {
        get => PlayerPrefs.GetInt("renderBase", 0);
        set { PlayerPrefs.SetInt("renderBase", value); Apply(); }
    }
    // the 3D at the scaled size, the HUD at native (see the top); false = the old Screen.SetResolution way
    public static bool HudNative
    {
        get => Supported && PlayerPrefs.GetInt("renderHud", 1) == 1;
        set { PlayerPrefs.SetInt("renderHud", value ? 1 : 0); Apply(); }
    }
    // the adaptive scale's bonus over the Auto base (0 .. MaxAdaptScale - base)
    public static float Adapt
    {
        get => PlayerPrefs.GetFloat("renderAdapt", 0f);
        set { PlayerPrefs.SetFloat("renderAdapt", Mathf.Max(0f, value)); Apply(); }
    }
    static float testScale; // WebGL shot mode only (URL rs=0.71): the HUD-native path at that scale, for screenshots
    public static bool TestMode => testScale > 0f;
#if UNITY_ANDROID && !UNITY_EDITOR
    static bool Supported => true;
#else
    static bool Supported => testScale > 0f;
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        var q = DebugShots.Query();
        float rs = DebugShots.GetF(q, "rs", 0f);
        if (rs > 0.2f && rs < 1f) testScale = rs;
        Sharp = Mathf.Clamp01(DebugShots.GetF(q, "rsharp", Sharp));
        Cas = Mathf.Clamp01(DebugShots.GetF(q, "rcas", Cas));
#endif
        ReadNative();
        Apply();
        if (!Supported) return;
        var go = new GameObject("RenderScale"); Object.DontDestroyOnLoad(go); go.hideFlags = HideFlags.HideInHierarchy;
        go.AddComponent<RenderScaleRT>();
#if UNITY_ANDROID && !UNITY_EDITOR
        // a size set before the window surface exists can be dropped: check it once the first frames are out
        go.AddComponent<RenderScaleCheck>();
#endif
    }
    public static int WantW, WantH; // the screen size Apply asked for (landscape)
    static bool shrunk;             // the screen was set below native by Apply

    static void ReadNative()
    {
        if (NativeW > 0) return;
        int w = Screen.width, h = Screen.height;
        try { var cr = Screen.currentResolution; if (cr.width * cr.height > w * h) { w = cr.width; h = cr.height; } } catch (System.Exception) { }
        NativeW = Mathf.Max(w, h); NativeH = Mathf.Min(w, h); // the game is landscape only
    }

    public static float AutoScale(int w, int h)
    {
        float px = (float)w * h;
        return px <= PixelBudget ? 1f : Mathf.Max(MinScale, Mathf.Sqrt(PixelBudget / px));
    }

    public static void Apply()
    {
        ReadNative();
        if (NativeW <= 0 || NativeH <= 0) return;
        if (testScale > 0f) { Scale = testScale; WantW = NativeW; WantH = NativeH; return; }
#if UNITY_ANDROID && !UNITY_EDITOR
        int pct = Pct;
        float a = AutoScale(NativeW, NativeH);
        float s = pct <= 0 ? (a < 1f ? Mathf.Clamp(a + Adapt, a, Mathf.Max(a, MaxAdaptScale)) : 1f) : Mathf.Clamp(pct, 40, 100) * 0.01f;
        Scale = s;
        bool land = Screen.width >= Screen.height;
        if (HudNative)
        {
            // the screen stays native; RenderScaleRT draws the 3D at Scale. Undo our own earlier shrink only.
            WantW = NativeW; WantH = NativeH;
            if (shrunk)
            {
                shrunk = false;
                if (land) Screen.SetResolution(NativeW, NativeH, FullScreenMode.FullScreenWindow);
                else Screen.SetResolution(NativeH, NativeW, FullScreenMode.FullScreenWindow);
            }
            Debug.Log($"RenderScale: native {NativeW}x{NativeH}, 3D scale {s:0.00} ({(pct <= 0 ? "Auto" : pct + "%")}), HUD native");
            return;
        }
        int w = Mathf.RoundToInt(NativeW * s) & ~1, h = Mathf.RoundToInt(NativeH * s) & ~1;
        WantW = w; WantH = h; shrunk = s < 0.999f;
        if (land) Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
        else Screen.SetResolution(h, w, FullScreenMode.FullScreenWindow);
        Debug.Log($"RenderScale: native {NativeW}x{NativeH}, base scale {s:0.00} ({(pct <= 0 ? "Auto" : pct + "%")}) -> {w}x{h}");
#endif
    }

    // adaptive scale, decided at a session start from the last session's 10 s GPU windows (see the top)
    internal static void Decide(int windows, float worstRatio, bool hot)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (Pct != 0 || windows <= 0) return;
        float a = AutoScale(NativeW, NativeH);
        if (a >= 1f) return;
        float was = Adapt, now = was;
        if (hot || worstRatio > 0.85f) now = was - AdaptStep;
        else if (windows >= 3 && worstRatio < 0.60f) now = was + AdaptStep;
        now = Mathf.Clamp(now, 0f, Mathf.Max(0f, MaxAdaptScale - a));
        if (Mathf.Abs(now - was) > 0.001f) { Adapt = now; Debug.Log($"RenderScale: adaptive {was:0.00} -> {now:0.00} (worst GPU window {worstRatio * 100f:0}% of the budget, {windows} windows{(hot ? ", hot" : "")})"); }
#endif
    }
}

// HUD native: the race camera draws into a scaled off-screen target, one pass stretches it to the native screen
// (Upscale.shader), and IMGUI draws on top at native resolution. Per frame: a few compares, no allocation. Also feeds
// the adaptive scale (GPU time while racing).
public class RenderScaleRT : MonoBehaviour
{
    Camera main, blit; RenderTexture rt; Material mat; CommandBuffer cb;
    int rtAA; bool wasActive; static readonly int SharpId = Shader.PropertyToID("_F2UpSharp"), CasId = Shader.PropertyToID("_F2UpCas");
    // adaptive scale: the current 10 s window and the session's worst
    readonly FrameTiming[] ft = new FrameTiming[1];
    float winSum, winT, worst; int winN, wins; bool hot;

    void LateUpdate()
    {
        if (main == null) main = Camera.main; // cached by Unity, only while we have none (the race camera lives all game)
        bool act = main != null && main.isActiveAndEnabled && (main.targetTexture == null || main.targetTexture == rt);
        if (main != null && main.isActiveAndEnabled && !wasActive) SessionStart(); // the menu handed the 3D back
        wasActive = main != null && main.isActiveAndEnabled;
        Sample();

        bool want = act && RenderScale.HudNative && (mat != null || MakeMat());
        int w = 0, h = 0;
        if (want)
        {
            int sw = Mathf.Max(Screen.width, Screen.height), sh = Mathf.Min(Screen.width, Screen.height);
            float k = RenderScale.Scale * RenderScale.Extra * Mathf.Clamp01(ScalableBufferManager.widthScaleFactor);
            int nw = RenderScale.TestMode ? sw : RenderScale.NativeW, nh = RenderScale.TestMode ? sh : RenderScale.NativeH;
            w = Mathf.Min(sw, Mathf.RoundToInt(nw * k)) & ~1;
            h = Mathf.Min(sh, Mathf.RoundToInt(nh * k)) & ~1;
            if (w >= sw * 0.97f || w < 64 || h < 64) want = false; // no gain: the camera draws straight into the screen
            if (Screen.width < Screen.height) { int t = w; w = h; h = t; }
        }
        if (want)
        {
            int aa = Mathf.Max(1, QualitySettings.antiAliasing);
            if (rt == null || rt.width != w || rt.height != h || rtAA != aa) Make(w, h, aa);
            if (main.targetTexture != rt) main.targetTexture = rt;
            mat.SetFloat(SharpId, RenderScale.Sharp);
            // the sharpen's 4 reads only on Medium / High (Low = anisotropic filtering off, GameManager.SetGraphics)
            mat.SetFloat(CasId, QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable ? 0f : RenderScale.Cas);
            if (!blit.enabled) blit.enabled = true;
        }
        else
        {
            if (main != null && rt != null && main.targetTexture == rt) main.targetTexture = null;
            if (blit != null && blit.enabled) blit.enabled = false;
        }
    }

    bool MakeMat()
    {
        var sh = Resources.Load<Shader>("Upscale");
        if (sh == null || !sh.isSupported) { enabled = false; return false; }
        mat = new Material(sh) { name = "F2Upscale" };
        var go = new GameObject("F2Upscale"); Object.DontDestroyOnLoad(go); go.hideFlags = HideFlags.HideInHierarchy;
        blit = go.AddComponent<Camera>();
        blit.cullingMask = 0; blit.clearFlags = CameraClearFlags.SolidColor; blit.backgroundColor = Color.black; // a clear: no load of the old screen on a tile GPU
        blit.depth = 90f; blit.orthographic = true; blit.nearClipPlane = 0.01f; blit.farClipPlane = 1f;
        blit.allowMSAA = false; blit.allowHDR = false; blit.useOcclusionCulling = false; blit.allowDynamicResolution = false;
        blit.renderingPath = RenderingPath.Forward; blit.depthTextureMode = DepthTextureMode.None;
        blit.enabled = false;
        cb = new CommandBuffer { name = "F2 upscale" };
        blit.AddCommandBuffer(CameraEvent.AfterEverything, cb);
        return true;
    }

    void Make(int w, int h, int aa)
    {
        var old = rt;
        rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            name = "F2_3D", antiAliasing = aa, useMipMap = false, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            // samples and depth stay in tile memory (Vulkan / Metal; ignored elsewhere): only the resolved colour is stored
            memorylessMode = aa > 1 ? RenderTextureMemoryless.Depth | RenderTextureMemoryless.MSAA : RenderTextureMemoryless.Depth,
        };
        rt.Create(); rtAA = aa;
        if (main.targetTexture == old) main.targetTexture = rt;
        cb.Clear();
        cb.Blit(rt, BuiltinRenderTextureType.CameraTarget, mat);
        if (old != null) { old.Release(); Destroy(old); }
        Debug.Log($"RenderScale: 3D target {w}x{h} {aa}x MSAA, screen {Screen.width}x{Screen.height}");
    }

    // ---- adaptive scale ----
    void Sample()
    {
        if (!FrameLog.Racing || !wasActive) return;
        if (Thermal.Hot) hot = true;
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, ft) < 1) return;
        float g = (float)ft[0].gpuFrameTime;
        if (!(g > 0f) || g > 500f) return;
        winSum += g; winN++; winT += Time.unscaledDeltaTime;
        if (winT < 10f) return;
        float budget = 1000f / Mathf.Max(20, FrameLog.Lock);
        float r = winSum / winN / budget;
        if (r > worst) worst = r;
        wins++; winSum = 0f; winT = 0f; winN = 0;
    }
    void SessionStart()
    {
        RenderScale.Decide(wins, worst, hot);
        wins = 0; worst = 0f; hot = false; winSum = 0f; winT = 0f; winN = 0;
    }

    void OnDestroy() { if (rt != null) { rt.Release(); Destroy(rt); } }
}

// a few frames after start-up: if the screen is still bigger than asked (the request came before the surface), ask again
// (twice at most), then go away. Nothing runs per frame after that.
public class RenderScaleCheck : MonoBehaviour
{
    int frames, tries;
    void Update()
    {
        if (++frames < 10) return;
        frames = 0;
        long have = (long)Screen.width * Screen.height, want = (long)RenderScale.WantW * RenderScale.WantH;
        if (want > 0 && have > want * 105 / 100 && tries < 2) { tries++; RenderScale.Apply(); return; }
        Destroy(this);
    }
}
