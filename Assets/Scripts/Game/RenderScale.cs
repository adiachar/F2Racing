// RenderScale.cs — the base render resolution on phones (Docs/PERF-ARCHITECTURE.md, "Render resolution").
//
// The problem: Unity on Android renders at the panel's native size unless told otherwise. A Redmi Note 12 Pro is
// 2400 x 1080 = 2.6 Mpx: 25% more pixels than a 1080p TV, on a 6.7" screen, for a Mali-G68 MC4 (4 shader cores).
// Every pixel pays the road / ground / grade shader, 4x MSAA, aniso and the alpha-tested foliage. Real Racing 3 and
// the other 60 fps mobile racers draw the 3D at 0.6-0.8 of native on such phones and let the display scale it.
//
// What: at start-up (before the first frame and before GameManager reads the screen size), on Android only, the
// screen buffer is set to a pixel budget: about 1.3 Mpx (2400 x 1080 -> 1700 x 765, 0.71 of native; 1080 x 2340 ->
// 0.72; 1600 x 720 phones stay at 100%), never below 0.6 of native. Screen.SetResolution on Android sizes the
// window surface and the display hardware (the compositor) upscales it to the panel for free: no extra pass, no
// intermediate texture, no extra memory, and every shader pays ~half the pixels. The HUD lays itself out from
// Screen.height, so it keeps its size and place (text is a little softer). Touch positions follow the scaled size.
// Everything else that scales the picture composes with this: the "Resolution scale" setting and Auto's one
// resolution step (GameManagerPerf: FixedResolution on GLES multiplies the size it finds; ScalableBufferManager on
// Vulkan scales the buffer it finds).
//
// Setting (PlayerPrefs "renderBase", percent of native; 0 = Auto as above): PerfLab cycles it (Auto / 100 / 85 / 70 /
// 60) for A/B tests; a Settings row is requested from the lead account (Docs/COORDINATION.md). A change made in a session
// is applied right away, at the next frame.
using UnityEngine;

public static class RenderScale
{
    public const float PixelBudget = 1.30e6f, MinScale = 0.6f;
    public static int NativeW, NativeH;     // the panel's own size, read once before anything changed it
    public static float Scale = 1f;         // the base scale in use
    public static int Pct                   // the setting: 0 = Auto, else percent of native
    {
        get => PlayerPrefs.GetInt("renderBase", 0);
        set { PlayerPrefs.SetInt("renderBase", value); Apply(); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        ReadNative();
        Apply();
#if UNITY_ANDROID && !UNITY_EDITOR
        // a size set before the window surface exists can be dropped: check it once the first frames are out
        var go = new GameObject("RenderScale"); Object.DontDestroyOnLoad(go); go.hideFlags = HideFlags.HideInHierarchy;
        go.AddComponent<RenderScaleCheck>();
#endif
    }
    public static int WantW, WantH; // the size Apply asked for (landscape)

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
#if UNITY_ANDROID && !UNITY_EDITOR
        ReadNative();
        if (NativeW <= 0 || NativeH <= 0) return;
        int pct = Pct;
        float s = pct <= 0 ? AutoScale(NativeW, NativeH) : Mathf.Clamp(pct, 40, 100) * 0.01f;
        Scale = s;
        int w = Mathf.RoundToInt(NativeW * s) & ~1, h = Mathf.RoundToInt(NativeH * s) & ~1;
        WantW = w; WantH = h;
        bool land = Screen.width >= Screen.height;
        if (land) Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
        else Screen.SetResolution(h, w, FullScreenMode.FullScreenWindow);
        Debug.Log($"RenderScale: native {NativeW}x{NativeH}, base scale {s:0.00} ({(pct <= 0 ? "Auto" : pct + "%")}) -> {w}x{h}");
#endif
    }
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
        Destroy(gameObject);
    }
}
