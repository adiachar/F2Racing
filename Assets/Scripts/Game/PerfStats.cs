// PerfStats.cs — debug-only render statistics overlay (Settings > GRAPHICS > "Render stats (debug)", or the WebGL shot
// mode parameter stats=1, which also logs one "PERF_STATS ..." console line per shot for Tools/PerfAudit/audit.py).
// Off by default; when off nothing here runs (no Update work, no OnGUI).
// What it shows (refreshed twice a second, memory every 5 s):
//  * the engine's own counters where the player has them (ProfilerRecorder: draw calls, batches, SetPass calls,
//    triangles; release players on most platforms report them, WebGL may not: then "n/a")
//  * our own counters, valid in every build: world renderers drawn now (Renderer.isVisible and not culled by the
//    sector sets) and their triangles; the same "before" (bounds in the camera frustum, ignoring the sector sets: what
//    Unity alone would draw, LOD culling aside); other renderers drawn (cars, people, sky); distinct materials and
//    shader variants (shader + keywords) among the drawn world renderers
//  * alpha-tested card overdraw: the area of the foliage / fence cards (SectorVisibility's 12 m clusters) projected
//    on the screen, in screens (1.0 = every pixel shaded once by a card), drawn and before
//  * sector: current sector / count, renderers hidden by the sets, average set size, the build time of the sets
//  * memory: textures (incl. runtime-generated: EnvTex atlases, photo textures, the model atlas, people, wheel), from
//    size, format and mips (x2 for the ones still readable: CPU copy), and meshes (vertex streams + indices, x2 readable)
using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public class PerfStats : MonoBehaviour
{
    static PerfStats inst;
    static bool show, log; static int showRead = -1;
    public static bool Show
    {
        get { if (showRead < 0) { showRead = 1; show = PlayerPrefs.GetInt("perfStats", 0) == 1; } return show; }
        set { show = value; showRead = 1; PlayerPrefs.SetInt("perfStats", value ? 1 : 0); if (value) Ensure(); }
    }

    // shot mode (DebugShots.Init): stats=1 shows the overlay and logs a line per shot
    internal static void FromQuery(Dictionary<string, string> q)
    {
        if (q == null || DebugShots.Get(q, "stats", "0") != "1") return;
        show = true; showRead = 1; log = true; Ensure();
    }
    internal static void ShotLog(string label)
    {
        if (!log || inst == null) return;
        inst.Refresh(true);
        Debug.Log("PERF_STATS " + label + " " + inst.Line());
    }

    public static void Ensure()
    {
        if (inst != null || !Show) return;
        var go = new GameObject("PerfStats"); DontDestroyOnLoad(go);
        inst = go.AddComponent<PerfStats>();
    }

    ProfilerRecorder rDraw, rBatch, rSetPass, rTris;
    void OnEnable()
    {
        rDraw = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        rBatch = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        rSetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        rTris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
    }
    void OnDisable() { rDraw.Dispose(); rBatch.Dispose(); rSetPass.Dispose(); rTris.Dispose(); }
    static string Rec(ProfilerRecorder r) => r.Valid && r.Count > 0 ? r.LastValue.ToString() : "n/a";

    // ---------------------------------------------------------------- counters
    int wDrawn, wBefore, wDraws, oDrawn, mats, variants; long wTris, wTrisBefore, oTris; float cardNow, cardBefore;
    float texMB, texGenMB, meshMB; int texCount, meshCount; float nextT, nextMem; string text = "";
    readonly Plane[] planes = new Plane[6];
    readonly HashSet<Material> matSet = new HashSet<Material>(); readonly HashSet<string> varSet = new HashSet<string>();
    readonly StringBuilder sb = new StringBuilder(512);

    void Update()
    {
        if (!Show) return;
        if (Time.unscaledTime >= nextT) Refresh(false);
    }

    void Refresh(bool full)
    {
        nextT = Time.unscaledTime + 0.5f;
        var cam = Camera.main; var sv = SectorVisibility.Cur;
        wDrawn = wBefore = wDraws = oDrawn = 0; wTris = wTrisBefore = oTris = 0; cardNow = cardBefore = 0f;
        matSet.Clear(); varSet.Clear();
        if (cam != null && sv != null)
        {
            GeometryUtility.CalculateFrustumPlanes(cam, planes);
            float f = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)), scr = (float)Screen.width * Screen.height;
            var cp = cam.transform.position; var fw = cam.transform.forward;
            for (int i = 0; i < sv.Rs.Length; i++)
            {
                var r = sv.Rs[i]; if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                bool inF = GeometryUtility.TestPlanesAABB(planes, sv.Box[i]);
                bool drawn = r.isVisible && !r.forceRenderingOff;
                bool before = drawn || (inF && r.forceRenderingOff);
                if (!before) continue;
                float card = 0f;
                if (sv.Alpha[i])
                    for (int c = sv.CardStart[i], e = c + sv.CardCount[i]; c < e; c++)
                    {
                        var q = sv.Cards[c]; var d = new Vector3(q.x, q.y, q.z) - cp; float z = Vector3.Dot(d, fw);
                        if (z < 1f || z > cam.farClipPlane) continue;
                        float px = f / z; card += 0.5f * q.w * px * px; // (cards at random angles: about half their area faces the camera)
                    }
                wBefore++; wTrisBefore += sv.Tris[i]; cardBefore += card;
                if (!drawn) continue;
                wDrawn++; wTris += sv.Tris[i]; cardNow += card;
                var ms = r.sharedMaterials; wDraws += ms.Length;
                foreach (var m in ms)
                {
                    if (m == null || !matSet.Add(m) || m.shader == null) continue;
                    varSet.Add(m.shader.name + "|" + string.Join(" ", m.shaderKeywords));
                }
            }
            cardNow /= scr; cardBefore /= scr;
        }
        mats = matSet.Count; variants = varSet.Count;
        // everything else that is drawn (cars, people, sky, HUD-free 3D)
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (!r.isVisible || r.forceRenderingOff || (sv != null && r is MeshRenderer && r.transform.IsChildOf(sv.transform))) continue;
            oDrawn++;
            Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : (r is MeshRenderer && !r.isPartOfStaticBatch ? r.GetComponent<MeshFilter>()?.sharedMesh : null);
            if (m != null) for (int s = 0; s < m.subMeshCount; s++) oTris += m.GetIndexCount(s) / 3;
        }
        if (full || Time.unscaledTime >= nextMem) { nextMem = Time.unscaledTime + 5f; Memory(); }
        text = Line().Replace("  ", "\n");
    }

    void Memory()
    {
        long tex = 0, gen = 0, mesh = 0; texCount = meshCount = 0;
        foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
        {
            long b = 0; int w = t.width, h = t.height; var fmt = t.graphicsFormat;
            uint bw = GraphicsFormatUtility.GetBlockWidth(fmt), bh = GraphicsFormatUtility.GetBlockHeight(fmt), bs = GraphicsFormatUtility.GetBlockSize(fmt);
            for (int m = 0; m < t.mipmapCount; m++) { b += (long)((Mathf.Max(1, w >> m) + bw - 1) / bw) * ((Mathf.Max(1, h >> m) + bh - 1) / bh) * bs; }
            if (t.isReadable) b *= 2;
            tex += b; texCount++;
            if (!IsAsset(t)) gen += b;
        }
        foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
        {
            long b = 0;
            for (int s = 0; s < m.vertexBufferCount; s++) b += (long)m.vertexCount * m.GetVertexBufferStride(s);
            long idx = 0; for (int s = 0; s < m.subMeshCount; s++) idx += m.GetIndexCount(s);
            b += idx * (m.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32 ? 4 : 2);
            if (m.isReadable) b *= 2;
            mesh += b; meshCount++;
        }
        texMB = tex / 1048576f; texGenMB = gen / 1048576f; meshMB = mesh / 1048576f;
    }
    // textures made at run time have no asset behind them (loaded assets have positive instance ids, run-time objects negative)
    static bool IsAsset(Object o) => o.GetInstanceID() > 0;

    string Line()
    {
        var sv = SectorVisibility.Cur;
        sb.Clear();
        sb.Append("gpu draws=").Append(Rec(rDraw)).Append(" batches=").Append(Rec(rBatch)).Append(" setpass=").Append(Rec(rSetPass)).Append(" tris=").Append(Rec(rTris));
        sb.Append("  world drawn=").Append(wDrawn).Append(" draws=").Append(wDraws).Append(" tris=").Append(wTris);
        sb.Append("  before drawn=").Append(wBefore).Append(" tris=").Append(wTrisBefore);
        sb.Append("  other drawn=").Append(oDrawn).Append(" tris=").Append(oTris);
        sb.Append("  inst draws=").Append(ModelInstancing.Draws).Append(" shown=").Append(ModelInstancing.Shown).Append('/').Append(ModelInstancing.Cur != null ? ModelInstancing.Count : 0).Append(" tris=").Append(ModelInstancing.Tris);
        sb.Append("  cards now=").Append(cardNow.ToString("0.00")).Append(" before=").Append(cardBefore.ToString("0.00"));
        sb.Append("  mats=").Append(mats).Append(" variants=").Append(variants);
        if (sv != null)
            sb.Append("  sector=").Append(sv.Bypass ? "all" : sv.Sector.ToString()).Append('/').Append(sv.Sectors).Append(" hidden=").Append(sv.Hidden).Append('/').Append(sv.Rs.Length)
              .Append(" avgset=").Append(sv.AvgSet).Append(" occ=").Append(sv.Occlusion ? 1 : 0).Append(" build=").Append(sv.BuildMs.ToString("0")).Append("ms split=").Append(sv.SplitFrom).Append("->").Append(sv.SplitInto);
        sb.Append("  tex=").Append(texMB.ToString("0.0")).Append("MB (runtime ").Append(texGenMB.ToString("0.0")).Append(") n=").Append(texCount);
        sb.Append(" mesh=").Append(meshMB.ToString("0.0")).Append("MB n=").Append(meshCount);
        sb.Append("  fps=").Append((1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f)).ToString("0"));
        return sb.ToString();
    }

    GUIStyle style;
    void OnGUI()
    {
        if (!Show || Event.current.type != EventType.Repaint) return;
        if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(11, Screen.height / 60), wordWrap = false };
        style.fontSize = Mathf.Max(11, Screen.height / 60);
        float w = Screen.height * 0.75f, h = style.fontSize * 1.35f * 12;
        GUI.depth = -100;
        var c = GUI.color; GUI.color = new Color(0, 0, 0, 0.55f); GUI.DrawTexture(new Rect(4, Screen.height * 0.18f, w, h), Texture2D.whiteTexture); GUI.color = c;
        GUI.Label(new Rect(10, Screen.height * 0.18f + 4, w - 12, h - 8), text, style);
    }
}
