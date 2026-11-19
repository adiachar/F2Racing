// CarEnv.cs — trackside reflections for the car paint (gfx gap list item 6). The one sky probe (WorldBuilder) only sees
// the sky layer, so no tree line, stand or fence ever slid over the bodywork. Here, once at load, a handful of small
// world cubemaps are rendered along the lap (one every ~280 m, at most 22; 48 px faces, sRGB RGBA8 with box mips:
// 74 KB each, ~1.6 MB at Monza), each from 1.1 m above the centreline, the static world + sky + clouds, fog on, cars
// left out. Per frame the player's car (CarEnvTarget, added by CarModel) picks the nearest baked cube and hands it to
// CarPaint.shader as a global (_F2EnvCube, _F2EnvOn 1): that ONE cube read replaces the sky-probe read, so Medium costs
// no more per pixel than before. High cross-fades to the second-nearest cube over the middle of the gap between two
// bake points (_F2EnvCube2, _F2EnvMix > 0: one extra read, only while fading). Low never reads a cube (CarPaint's
// per-vertex SubShader) and nothing is baked there. Every car (rivals too) uses the player's cube: no per-car work.
// The bake: all cubes in the first LateUpdate after the build (a one-off load hitch, timed into BakeMs and logged), at
// a reduced LOD bias, without shadows and with a neutral colour grade (CarPaint grades the reflection itself: the world
// shaders' own grade would otherwise be applied twice). SectorVisibility's per-sector culling is lifted for the bake
// and put back. GPU-instanced model trees (ModelInstancing) are culled against the main camera and are not in the
// cubes (card trees, far tree lines, stands, fences, buildings are). A weather change (Sky.SetWeather) re-bakes one
// cube per frame, the old ones staying in use meanwhile. No allocation per frame.
using FormulaSim;
using UnityEngine;
using UnityEngine.Rendering;

[DefaultExecutionOrder(50)] // after SectorVisibility (0): its set for this frame is applied; before ModelInstancing (100) and Sky (1000)
public class CarEnv : MonoBehaviour
{
    public static Transform Target;         // the player's car (CarEnvTarget); null = the main camera
    public static CarEnv Cur;
    public static float BakeMs;             // time spent rendering the cubes (all of them, CPU side)
    public static int Count, Bytes;

    const int Size = 48, MaxCubes = 22, MinCubes = 8;
    const float Spacing = 280f, EyeUp = 1.1f, Far = 600f, BakeLodBias = 0.5f;
    static readonly int idOn = Shader.PropertyToID("_F2EnvOn"), idA = Shader.PropertyToID("_F2EnvCube"),
        idB = Shader.PropertyToID("_F2EnvCube2"), idMix = Shader.PropertyToID("_F2EnvMix");

    Vector3[] pos; RenderTexture[] cubes; bool[] ready; int[] order;
    int pending;                            // cubes in 'order' still to bake (from the end)
    bool first = true;
    RenderTexture tmp; Camera cam; bool copy;
    bool[] svOff; Renderer[] skyR; bool[] skyOff;
    int curA = -2, curB = -2; float curMix = -1f; bool high; int gfxAt = -1000;

    public static void Attach(Transform root, Track t)
    {
        Shader.SetGlobalFloat(idOn, 0f); Shader.SetGlobalFloat(idMix, 0f);
        if (WorldBuilder.Gfx <= 1 || t == null || t.N < 4) return; // Low: CarPaint's per-vertex pass reads no cube
        if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32)) return;
        var go = new GameObject("CarEnv"); go.transform.SetParent(root, false);
        var c = go.AddComponent<CarEnv>();
        int n = Mathf.Clamp(Mathf.RoundToInt((float)t.length / Spacing), MinCubes, MaxCubes);
        c.pos = new Vector3[n]; c.cubes = new RenderTexture[n]; c.ready = new bool[n]; c.order = new int[n];
        for (int i = 0; i < n; i++)
        {
            int k = (int)((long)i * t.N / n);
            double x = t.pts[k].x, y = t.pts[k].y;
            c.pos[i] = new Vector3((float)x, WorldBuilder.H(x, y) + EyeUp, (float)y);
        }
        c.copy = (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0;
        Count = n; Bytes = 0; BakeMs = 0f;
        Cur = c;
    }

    // Sky.SetWeather: the sky changed, so every cube is baked again (one per frame, nearest first)
    public static void Rebake() { if (Cur != null && !Cur.first) Cur.Queue(); }

    void Queue()
    {
        Vector3 p = Where();
        int n = pos.Length;
        for (int i = 0; i < n; i++) order[i] = i;
        // furthest first in the array: baking pops from the end, so the nearest cube is ready first (insertion sort, n <= 22)
        for (int i = 1; i < n; i++)
        {
            int v = order[i]; float dv = (pos[v] - p).sqrMagnitude; int j = i - 1;
            while (j >= 0 && (pos[order[j]] - p).sqrMagnitude < dv) { order[j + 1] = order[j]; j--; }
            order[j + 1] = v;
        }
        pending = n;
    }

    Vector3 Where()
    {
        if (Target != null && Target.gameObject.activeInHierarchy) return Target.position;
        var mc = Camera.main;
        return mc != null ? mc.transform.position : pos[0];
    }

    void LateUpdate()
    {
        if (pos == null) return;
        if (first) { first = false; Queue(); Bake(pos.Length); Debug.Log("CarEnv: " + pos.Length + " reflection cubes " + Size + " px, " + (Bytes / 1024) + " KB, bake " + BakeMs.ToString("0") + " ms" + (copy ? "" : " (no CopyTexture: direct)")); }
        else if (pending > 0) Bake(1);
        Pick();
    }

    void Pick()
    {
        if (Time.frameCount - gfxAt > 60) { gfxAt = Time.frameCount; high = WorldBuilder.LiveGfx >= 3; }
        Vector3 p = Where();
        int a = -1, b = -1; float da = float.MaxValue, db = float.MaxValue;
        for (int i = 0; i < pos.Length; i++)
        {
            if (!ready[i]) continue;
            float d = (pos[i] - p).sqrMagnitude;
            if (d < da) { db = da; b = a; da = d; a = i; }
            else if (d < db) { db = d; b = i; }
        }
        if (a < 0) { if (curA != -1) { curA = -1; Shader.SetGlobalFloat(idOn, 0f); } return; }
        float mix = 0f;
        if (high && b >= 0)
        {
            // share of the nearest gap covered: 0 at a bake point .. 0.5 halfway; fade only over the middle 40 %
            float sa = Mathf.Sqrt(da), sb = Mathf.Sqrt(db);
            float f = sa / Mathf.Max(sa + sb, 0.01f);
            mix = 0.5f * Mathf.Clamp01((f - 0.3f) * 5f);
        }
        if (a != curA) { if (curA < 0) Shader.SetGlobalFloat(idOn, 1f); curA = a; Shader.SetGlobalTexture(idA, cubes[a]); }
        if (mix > 0f && b != curB) { curB = b; Shader.SetGlobalTexture(idB, cubes[b]); }
        if (Mathf.Abs(mix - curMix) > 0.002f || (mix == 0f && curMix != 0f)) { curMix = mix; Shader.SetGlobalFloat(idMix, mix); }
    }

    // render up to 'count' cubes from the queue now
    void Bake(int count)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (cam == null)
        {
            var cg = new GameObject("CarEnvCam"); cg.transform.SetParent(transform, false);
            cam = cg.AddComponent<Camera>(); cam.enabled = false;
            cam.clearFlags = CameraClearFlags.Skybox; cam.renderingPath = RenderingPath.Forward;
            cam.cullingMask = ~((1 << LightBake.CarLayer) | (1 << 5) | (1 << MenuStudio.Layer)); // no cars / people, UI, menu set
            cam.nearClipPlane = 0.3f; cam.farClipPlane = Far;
            cam.allowHDR = false; cam.allowMSAA = false; cam.useOcclusionCulling = false;
        }
        if (copy && tmp == null)
        {
            tmp = new RenderTexture(Size, Size, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { dimension = TextureDimension.Cube, name = "CarEnvTmp" };
            tmp.Create();
        }
        // lift the per-sector culling (the bake points are anywhere on the lap), hide the camera-following sky quads
        var sv = SectorVisibility.Cur;
        int nr = sv != null && sv.Rs != null ? sv.Rs.Length : 0;
        if (svOff == null || svOff.Length != nr) svOff = new bool[nr];
        for (int i = 0; i < nr; i++) { var r = sv.Rs[i]; if (r != null) { svOff[i] = r.forceRenderingOff; r.forceRenderingOff = false; } }
        var dome = Sky.CloudDome;
        if (skyR == null && Sky.Current != null) { skyR = Sky.Current.GetComponentsInChildren<Renderer>(true); skyOff = new bool[skyR.Length]; }
        if (skyR != null) for (int i = 0; i < skyR.Length; i++) { var r = skyR[i]; if (r != null) { skyOff[i] = r.forceRenderingOff; if (dome == null || r.transform != dome) r.forceRenderingOff = true; } }
        Vector3 dPos = dome != null ? dome.position : Vector3.zero, dScale = dome != null ? dome.localScale : Vector3.one;
        if (dome != null) dome.localScale = Vector3.one * (Far * Sky.CloudFill);
        float lodBias = QualitySettings.lodBias; var shadows = QualitySettings.shadows;
        QualitySettings.lodBias = lodBias * BakeLodBias; QualitySettings.shadows = ShadowQuality.Disable;
        // the reflection is graded once, by CarPaint: neutral grade while the world is drawn into the cube
        Shader.SetGlobalFloat("_GradeExposure", 1f); Shader.SetGlobalFloat("_GradeSat", 1f); Shader.SetGlobalFloat("_GradeContrast", 1f);
        Shader.SetGlobalVector("_GradeTint", Vector4.one); Shader.SetGlobalVector("_GradeLift", Vector4.zero);
        Shader.SetGlobalVector("_GradeGamma", Vector4.zero); Shader.SetGlobalVector("_GradeGain", Vector4.one);

        for (int c = 0; c < count && pending > 0; c++)
        {
            int i = order[--pending];
            if (cubes[i] == null)
            {
                cubes[i] = new RenderTexture(Size, Size, copy ? 0 : 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { dimension = TextureDimension.Cube, useMipMap = true, autoGenerateMips = false, name = "CarEnv" + i, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
                cubes[i].Create();
                Bytes += Size * Size * 4 * 6 * 4 / 3;
            }
            cam.transform.position = pos[i];
            if (dome != null) dome.position = pos[i];
            if (copy)
            {
                cam.RenderToCubemap(tmp, 63);
                for (int f = 0; f < 6; f++) Graphics.CopyTexture(tmp, f, 0, cubes[i], f, 0);
            }
            else cam.RenderToCubemap(cubes[i], 63);
            cubes[i].GenerateMips();
            ready[i] = true;
        }

        Grade.Push(Grade.Last);
        QualitySettings.lodBias = lodBias; QualitySettings.shadows = shadows;
        if (dome != null) { dome.position = dPos; dome.localScale = dScale; }
        if (skyR != null) for (int i = 0; i < skyR.Length; i++) if (skyR[i] != null) skyR[i].forceRenderingOff = skyOff[i];
        for (int i = 0; i < nr; i++) if (sv.Rs[i] != null) sv.Rs[i].forceRenderingOff = svOff[i];
        if (pending == 0 && tmp != null) { tmp.Release(); Destroy(tmp); tmp = null; }
        BakeMs += (float)sw.Elapsed.TotalMilliseconds;
    }

    void OnDestroy()
    {
        if (Cur == this) { Cur = null; Shader.SetGlobalFloat(idOn, 0f); Shader.SetGlobalFloat(idMix, 0f); }
        if (tmp != null) { tmp.Release(); Destroy(tmp); }
        if (cubes != null) for (int i = 0; i < cubes.Length; i++) if (cubes[i] != null) { cubes[i].Release(); Destroy(cubes[i]); }
    }
}
