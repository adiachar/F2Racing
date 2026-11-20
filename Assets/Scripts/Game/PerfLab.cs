// PerfLab.cs — on-device performance measurement and one-tap A/B switches (debug only; nothing runs unless the
// "Render stats (debug)" overlay is on: PerfStats.Show). See Docs/PERF-ARCHITECTURE.md, "Finding the culprit on a phone".
//
// Timings (shown by PerfStats in its overlay, averaged over the last ~0.5 s):
//  * Unity's frame timing (FrameTimingManager through its ProfilerRecorder counters, which switch it on even in a
//    release player): CPU frame, main thread, render thread and GPU time in ms. GPU time is 0 / "n/a" where the driver
//    gives no timer (WebGL, some GLES drivers). Reading the counters costs nothing measurable.
//  * our own: the time spent in script Update (bracketed by two helper components that run first and last: the 240 Hz
//    physics, AI and car models are most of it), in OnGUI (all IMGUI: HUD, menus) and, once the lead account adds the
//    two calls (Docs/COORDINATION.md), exactly in the physics step loop and in GameManager.OnGUI. Stopwatch ticks, no
//    allocation.
// Switches (each one tap; a second tap restores): trees (card and model trees, shrubs), fences (alpha-tested
// fences / nets), grade (cheap colour grade), HUD (needs the lead's one-line hook), particles, rivals (not drawn),
// rival LOD (full meshes at every distance, the old way), shadows (shadow maps), world (every static world renderer:
// what is left is cars + sky + HUD), sky (clouds + glare), MSAA (4 / 2 / off), render scale (Auto / 100 / 85 / 70 / 60 %).
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;

public static class PerfLab
{
    // ---------------------------------------------------------------- switches
    public static bool TreesOff, FencesOff, GradeLow, HudOff, ParticlesOff, RivalsOff, RivalLodOff, ShadowsOff, WorldOff, SkyOff;
    public static readonly string[] Names = { "Trees", "Fences", "Grade", "HUD", "Particles", "Rivals", "Car LOD", "Shadows", "World", "Sky", "MSAA", "Res", "HUD res" };

    public static bool Get(int i)
    {
        switch (i)
        {
            case 0: return !TreesOff; case 1: return !FencesOff; case 2: return !GradeLow; case 3: return !HudOff; case 4: return !ParticlesOff;
            case 5: return !RivalsOff; case 6: return !RivalLodOff; case 7: return !ShadowsOff; case 8: return !WorldOff; case 9: return !SkyOff;
            default: return true;
        }
    }
    static readonly string[] msaaCaps = { "MSAA off", "MSAA 2x", "MSAA 4x", "MSAA 8x" };
    static readonly string[] resCaps = { "Res Auto", "Res 100%", "Res 85%", "Res 70%", "Res 60%" };
    static readonly int[] resSteps = { 0, 100, 85, 70, 60 };
    public static string Caption(int i)
    {
        if (i == 10) { int a = QualitySettings.antiAliasing; return msaaCaps[a >= 8 ? 3 : a >= 4 ? 2 : a >= 2 ? 1 : 0]; }
        if (i == 12) return RenderScale.HudNative ? "HUD native" : "HUD scaled"; // RenderScale.cs: 3D off-screen + upscale, or the whole screen scaled
        if (i == 11) { int p = RenderScale.Pct; for (int k = 0; k < resSteps.Length; k++) if (resSteps[k] == p) return resCaps[k]; return "Res " + p + "%"; }
        return Names[i];
    }

    public static void Toggle(int i)
    {
        switch (i)
        {
            case 0: TreesOff = !TreesOff; ModelInstancing.Off = TreesOff; Collect(); break;
            case 1: FencesOff = !FencesOff; Collect(); break;
            case 2: GradeLow = !GradeLow; if (GradeLow) Shader.EnableKeyword("F2_GRADE_LOW"); else Shader.DisableKeyword("F2_GRADE_LOW"); break;
            case 3: HudOff = !HudOff; break;
            case 4: ParticlesOff = !ParticlesOff; Collect(); break;
            case 5: RivalsOff = !RivalsOff; CarModel.SetRivalsHidden(RivalsOff); break;
            case 6: RivalLodOff = !RivalLodOff; CarModel.SetRivalLod(!RivalLodOff); break;
            case 7: ShadowsOff = !ShadowsOff; if (ShadowsOff) { shadowsWas = QualitySettings.shadows; QualitySettings.shadows = ShadowQuality.Disable; } else QualitySettings.shadows = shadowsWas; break;
            case 8: WorldOff = !WorldOff; Collect(); break;
            case 9: SkyOff = !SkyOff; Collect(); break;
            case 10: { int a = QualitySettings.antiAliasing; QualitySettings.antiAliasing = a >= 4 ? 2 : a >= 2 ? 1 : 4; break; }
            case 12: RenderScale.HudNative = !RenderScale.HudNative; break;
            case 11:
            {
                int p = RenderScale.Pct, k = 0;
                for (int j = 0; j < resSteps.Length; j++) if (resSteps[j] == p) k = j;
                RenderScale.Pct = resSteps[(k + 1) % resSteps.Length];
                break;
            }
        }
    }
    static ShadowQuality shadowsWas = ShadowQuality.All;

    // the renderers a switch hides: found when it is tapped and re-asserted every frame while it is on (TracksideLod and
    // the weather effects switch some of them themselves); restored to what they were when it is tapped again
    sealed class Hid { public readonly List<Renderer> r = new List<Renderer>(); public readonly List<bool> was = new List<bool>(); }
    static readonly Hid hidTrees = new Hid(), hidFences = new Hid(), hidParts = new Hid(), hidWorld = new Hid(), hidSky = new Hid();
    static void Collect()
    {
        Fill(hidTrees, TreesOff, 0); Fill(hidFences, FencesOff, 1); Fill(hidParts, ParticlesOff, 2); Fill(hidWorld, WorldOff, 3); Fill(hidSky, SkyOff, 4);
    }
    static readonly Dictionary<Renderer, bool> orig = new Dictionary<Renderer, bool>(); // enabled before any switch hid it
    static readonly Hid[] all = { hidTrees, hidFences, hidParts, hidWorld, hidSky };
    static void Fill(Hid h, bool on, int kind)
    {
        if (!on)
        {
            h.r.Clear(); h.was.Clear();
            foreach (var kv in new List<KeyValuePair<Renderer, bool>>(orig))
            {
                var r = kv.Key; bool still = false;
                if (r != null) for (int k = 0; k < all.Length && !still; k++) still = all[k].r.Contains(r);
                if (still) continue;
                if (r != null) r.enabled = kv.Value;
                orig.Remove(r);
            }
            return;
        }
        if (h.r.Count > 0 && kind != 2) return; // (particle systems come and go: those are looked up again)
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (h.r.Contains(r) || !Wants(r, kind)) continue;
            h.r.Add(r); h.was.Add(r.enabled);
            if (!orig.ContainsKey(r)) orig[r] = r.enabled;
        }
    }
    static bool Wants(Renderer r, int kind)
    {
        if (kind == 2) return r is ParticleSystemRenderer;
        if (kind == 4) return r.gameObject.layer == Sky.Layer;
        var sv = SectorVisibility.Cur;
        bool world = sv != null && r is MeshRenderer && r.transform.IsChildOf(sv.transform) && r.gameObject.layer != LightBake.CarLayer && r.gameObject.layer != Sky.Layer;
        if (kind == 3) return world;
        if (!world) return false;
        var m = r.sharedMaterial; var sh = m != null ? m.shader : null; if (sh == null) return false;
        string n = sh.name;
        if (kind == 0) return n.Contains("Foliage");
        return n.Contains("Cutout") || (m.renderQueue >= 2450 && m.renderQueue < 3000 && !n.Contains("Foliage"));
    }
    static void Hold(Hid h) { for (int i = 0; i < h.r.Count; i++) { var r = h.r[i]; if (r != null && r.enabled) r.enabled = false; } }
    static float partsRefresh;

    // once a frame, last (PerfLabLate.LateUpdate)
    internal static void Frame()
    {
        if (TreesOff) Hold(hidTrees);
        if (FencesOff) Hold(hidFences);
        if (WorldOff) Hold(hidWorld);
        if (SkyOff) Hold(hidSky);
        if (ParticlesOff)
        {
            if (Time.unscaledTime > partsRefresh) { partsRefresh = Time.unscaledTime + 1f; Fill(hidParts, true, 2); }
            Hold(hidParts);
        }
    }

    // ---------------------------------------------------------------- timings
    static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    static long updT0, guiT0, physT0, gmGuiT0;
    static long updAcc, guiAcc, physAcc, gmGuiAcc; static int frames; static bool exactPhys, exactGui;
    public static float UpdateMs, GuiMs, PhysMs, GmGuiMs, CpuMs, MainMs, RenderMs, GpuMs; // averages of the last window
    static float winT;

    // the lead's exact brackets (GameManager.Update physics loop, GameManager.OnGUI): see Docs/COORDINATION.md
    public static void PhysBegin() { physT0 = Stopwatch.GetTimestamp(); }
    public static void PhysEnd() { physAcc += Stopwatch.GetTimestamp() - physT0; exactPhys = true; }
    public static void GuiBegin() { gmGuiT0 = Stopwatch.GetTimestamp(); }
    public static void GuiEnd() { gmGuiAcc += Stopwatch.GetTimestamp() - gmGuiT0; exactGui = true; }
    public static bool HasPhys => exactPhys;
    public static bool HasGmGui => exactGui;

    internal static void UpdateBegin() { updT0 = Stopwatch.GetTimestamp(); }
    internal static void UpdateEnd() { updAcc += Stopwatch.GetTimestamp() - updT0; }
    internal static void GuiFirst() { guiT0 = Stopwatch.GetTimestamp(); }
    internal static void GuiLast() { guiAcc += Stopwatch.GetTimestamp() - guiT0; }

    static ProfilerRecorder rCpu, rMain, rRender, rGpu; static bool recOn;
    internal static void StartRecorders()
    {
        if (recOn) return; recOn = true;
        // these counters switch the FrameTimingManager on, also in a release player (Unity 6 manual, "Frame Timing Manager")
        rCpu = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Total Frame Time", 15);
        rMain = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time", 15);
        rRender = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Render Thread Frame Time", 15);
        rGpu = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GPU Frame Time", 15);
    }
    internal static void StopRecorders()
    {
        if (!recOn) return; recOn = false;
        rCpu.Dispose(); rMain.Dispose(); rRender.Dispose(); rGpu.Dispose();
    }
    static float Avg(ProfilerRecorder r)
    {
        if (!r.Valid) return -1f;
        int n = r.Count; if (n == 0) return -1f;
        double s = 0; int k = 0;
        for (int i = 0; i < n; i++) { long v = r.GetSample(i).Value; if (v > 0) { s += v; k++; } }
        return k > 0 ? (float)(s / k * 1e-6) : 0f; // ns -> ms
    }
    static readonly FrameTiming[] ft = new FrameTiming[1];

    // once a frame, last
    internal static void EndFrame()
    {
        frames++; winT += Time.unscaledDeltaTime;
        if (winT < 0.5f) return;
        float k = (float)(TickMs / Mathf.Max(1, frames));
        UpdateMs = updAcc * k; GuiMs = guiAcc * k; PhysMs = physAcc * k; GmGuiMs = gmGuiAcc * k;
        updAcc = guiAcc = physAcc = gmGuiAcc = 0; frames = 0; winT = 0f;
        CpuMs = Avg(rCpu); MainMs = Avg(rMain); RenderMs = Avg(rRender); GpuMs = Avg(rGpu);
        if (CpuMs <= 0f || GpuMs < 0f)
        {
            // the counters are missing on this player: ask the FrameTimingManager itself (needs "Frame Timing Stats" in
            // a release build; 4 frames late)
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, ft) > 0)
            {
                CpuMs = (float)ft[0].cpuFrameTime; MainMs = (float)ft[0].cpuMainThreadFrameTime;
                RenderMs = (float)ft[0].cpuRenderThreadFrameTime; GpuMs = (float)ft[0].gpuFrameTime;
            }
        }
    }
}

// first and last in every frame's Update / OnGUI order (the window between them is the game's own scripts)
[DefaultExecutionOrder(-32000)]
public class PerfLabEarly : MonoBehaviour
{
    void Update() { PerfLab.UpdateBegin(); }
    void OnGUI() { PerfLab.GuiFirst(); }
}
[DefaultExecutionOrder(32000)]
public class PerfLabLate : MonoBehaviour
{
    void Update() { PerfLab.UpdateEnd(); }
    void LateUpdate() { PerfLab.Frame(); PerfLab.EndFrame(); }
    void OnGUI() { PerfLab.GuiLast(); }
}
