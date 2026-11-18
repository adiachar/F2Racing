// SectorVisibility.cs — track-sector visibility for the static world (the way console racing games do it: the lap is
// cut into short sectors and, at load, each sector gets the list of world pieces that can be seen from anywhere a
// camera can be inside it; at run time only the current sector's list is drawn). Called once per world build by
// WorldBuilder.Build, right before static batching. Three steps:
//
//  1. Split. A world mesh that spans most of the circuit (the card trees "Trees", EnvProps, EnvBlocks, Town walls and
//     roofs, the procedural Terrain, RealWorld's far tree lines, land chunks over 600 m) is one renderer, so Unity's
//     frustum culling can never drop any of it: every vertex of it is shaded every frame, behind the camera too. Such
//     a mesh is cut into tiles of 256 m or more (at least ~600 triangles each) by triangle centre; every vertex
//     attribute is kept as it is (format and dimension), so the look is identical. The road ("Road") stays whole.
//     Meshes under a LODGroup (RealWorld's culled blocks) or with scripts / children on them are left alone.
//  2. View sets. The lap is cut into 50 m sectors. A sector's camera region is its centreline (plus 15 m either way)
//     widened to half the track width + 30 m each side (the pit lane, chase and far-chase offsets) and from 3 m below
//     to 9 m above the road. A world renderer is in the sector's set unless, from EVERY point of that region:
//       * it is too small to matter: its own height (the tallest thing in it, measured per 16 m cell, not its
//         bounds) would cover less than PxMin pixels at 1080p and a 45 degree field of view (the narrowest cockpit
//         setting), with the dry haze taken off its contrast (never more than x4). PxMin = 0.5 px High, 0.8 Medium,
//         1.3 Low. Ground-like layers (terrain, road, verges, water, far tree lines, flat ribbons) are never culled
//         by size: they are what the horizon is made of.
//       * or it is hidden behind the terrain. Only if the terrain has 15 m of relief: from 9 eye points per sector
//         (3 along x 3 across, at the top of the region: on a height field a higher eye sees more) a horizon profile
//         (256 directions x 64 steps of 36 m over a lower bound of the terrain on a 24 m grid, 2 m margin) is built,
//         and the top face of the renderer's bounds (up to 6 x 6 points) is tested against it, the two neighbouring
//         directions included. Pieces nearer than 200 m are never occlusion-culled.
//     Sets are bitsets (renderers / 8 bytes per sector: ~30 KB at Monza).
//  3. Run time (LateUpdate): the camera's nearest centreline point (Track.Nearest with a hint: ~120 points) picks
//     the sector; on a change only the renderers whose state changes are touched (Renderer.forceRenderingOff, so the
//     enabled flags TracksideLod and the LODGroups own are left alone). Outside every region (the test pad, TV and
//     replay cameras far off the track or zoomed under 44 degrees, the start menu's high views) everything is drawn,
//     exactly as before.
// PerfStats.cs reads the per-renderer numbers kept here (triangles, alpha-tested card area) for the debug overlay.
// Instanced items (ModelInstancing.cs: GPU-instanced model trees and shrubs, no renderers): their world boxes go through
// the same size and terrain tests into a second set of bitsets; ModelInstancing asks ExtraOn(k) for the current sector.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;
using UnityEngine.Rendering;

public class SectorVisibility : MonoBehaviour
{
    public static SectorVisibility Cur;
    public static bool Enabled = true;      // PerfStats can switch it off for an A/B count

    // ---------------------------------------------------------------- tuning
    const float SectorLen = 50f, AlongMargin = 15f, LatExtra = 30f, EyeUp = 9f, EyeDown = 3f;
    const float SplitMin = 600f, TileMin = 256f; const int TileTris = 600, SplitTrisMin = 2000;
    const float OccNear = 200f, Relief = 15f, OccCell = 24f, OccMargin = 2f;
    const int Az = 256, Steps = 64; const float StepLen = 36f;
    const float FovRef = 45f, FogStart = 480f, FogEnd = 2100f; // the narrowest cockpit view; the widest dry haze of any circuit
    static float PxMin(int gfx) => gfx >= 3 ? 0.5f : gfx == 2 ? 0.8f : 1.3f;
    static readonly string[] GroundNames = { "Ground", "EnvGroundFrame", "Terrain", "EnvRealLand", "Verge", "Gravel", "Astroturf", "Pavement", "Road", "Lines", "Kerbs",
                                             "Harbour", "EnvRealWater", "EnvRealRoads", "RetainingWalls", "Tunnel", "EnvBackdrop", "EnvFarTrees" };

    // ---------------------------------------------------------------- state (also read by PerfStats)
    public MeshRenderer[] Rs = new MeshRenderer[0];
    public int[] Tris = new int[0];
    public bool[] Alpha = new bool[0];
    public Bounds[] Box = new Bounds[0];
    public float[] Reach = new float[0];    // size / haze limit (m); +inf for ground layers
    public int[] CardStart, CardCount; public Vector4[] Cards; // alpha-tested area per 12 m cluster: xyz centre, w area (m^2)
    public int Sectors, Sector = -1, Hidden, SplitFrom, SplitInto, Gfx; public bool Bypass = true, Occlusion; public float BuildMs, ReliefM;
    public long TrisAll; public int AvgSet;

    Track t; float[] trackY; int sectorPts; ulong[] sets; int words; bool[] off; int hint = -1, applied = -2;
    // instanced items (ModelInstancing): world boxes, size reach, per-sector bitsets
    Bounds[] xBox; float[] xReach; ulong[] xSets; int xWords;
    public int ExtraCount => xBox != null ? xBox.Length : 0;

    // ================================================================ build
    public static void Prepare(Track track, GameObject root)
    {
        if (track == null || root == null || track.N < 8) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sv = root.AddComponent<SectorVisibility>();
        sv.t = track; sv.Gfx = WorldBuilder.Gfx;
        var mi = ModelInstancing.ForRoot(root); sv.xBox = mi != null ? mi.Boxes : null;
        try
        {
            sv.SplitAll(root);
            sv.Collect(root);
            sv.BuildSets();
        }
        catch (System.Exception e) { Debug.LogError("SectorVisibility: " + e); sv.sets = null; }
        sv.BuildMs = (float)sw.Elapsed.TotalMilliseconds;
        Cur = sv;
        PerfStats.Ensure();
        Debug.Log($"SectorVisibility {track.def?.id}: {sv.Rs.Length} renderers ({sv.SplitFrom} big meshes cut into {sv.SplitInto}), {sv.Sectors} sectors, average set {sv.AvgSet}, " +
                  $"relief {sv.ReliefM:0} m, occlusion {(sv.Occlusion ? "on" : "off")}, {sv.BuildMs:0} ms");
    }

    void OnDestroy() { if (Cur == this) Cur = null; }

    static bool IsGround(string n)
    {
        for (int i = 0; i < GroundNames.Length; i++) if (n == GroundNames[i]) return true;
        return false;
    }
    static bool Skip(GameObject go) => go.layer == Sky.Layer || go.layer == LightBake.CarLayer;

    // ---------------------------------------------------------------- 1. split the circuit-wide meshes
    void SplitAll(GameObject root)
    {
        var list = root.GetComponentsInChildren<MeshRenderer>(true);
        foreach (var r in list)
        {
            if (r == null || Skip(r.gameObject) || r.name == "Road") continue;
            var mf = r.GetComponent<MeshFilter>(); if (mf == null) continue;
            var m = mf.sharedMesh;
            if (m == null || !m.isReadable || m.subMeshCount != 1 || m.GetTopology(0) != MeshTopology.Triangles) continue;
            var b = m.bounds; if (Mathf.Max(b.size.x, b.size.z) < SplitMin) continue;
            if (m.GetIndexCount(0) / 3 < SplitTrisMin) continue;
            if (r.transform.childCount > 0 || r.GetComponents<Component>().Length != 3 || r.GetComponentInParent<LODGroup>() != null) continue;
            int n = Split(r, mf, m);
            if (n > 1) { SplitFrom++; SplitInto += n; }
        }
    }

    static readonly List<Vector3> sV = new List<Vector3>(), sN = new List<Vector3>();
    static readonly List<Vector4> sT = new List<Vector4>();
    static readonly List<Color> sC = new List<Color>(); static readonly List<Color32> sC32 = new List<Color32>();
    static readonly List<Vector4>[] sUv = { new List<Vector4>(), new List<Vector4>(), new List<Vector4>(), new List<Vector4>() };
    static readonly List<int> sTri = new List<int>();

    static int Split(MeshRenderer r, MeshFilter mf, Mesh m)
    {
        var v = m.vertices; var tri = m.triangles; int nt = tri.Length / 3; var b = m.bounds;
        int maxTiles = Mathf.Max(1, nt / TileTris);
        float tile = Mathf.Max(TileMin, Mathf.Sqrt(Mathf.Max(1f, b.size.x * b.size.z) / maxTiles));
        int tx = Mathf.Max(1, Mathf.CeilToInt(b.size.x / tile)), tz = Mathf.Max(1, Mathf.CeilToInt(b.size.z / tile));
        if (tx * tz < 2) return 0;
        var of = new int[nt]; var cnt = new int[tx * tz];
        for (int k = 0; k < nt; k++)
        {
            Vector3 c = (v[tri[3 * k]] + v[tri[3 * k + 1]] + v[tri[3 * k + 2]]) * (1f / 3f);
            int ix = Mathf.Clamp((int)((c.x - b.min.x) / tile), 0, tx - 1), iz = Mathf.Clamp((int)((c.z - b.min.z) / tile), 0, tz - 1);
            of[k] = iz * tx + ix; cnt[of[k]]++;
        }
        int used = 0; for (int i = 0; i < cnt.Length; i++) if (cnt[i] > 0) used++;
        if (used < 2) return 0;

        // every attribute, in its own dimension and format
        var nrm = m.HasVertexAttribute(VertexAttribute.Normal) ? m.normals : null;
        var tan = m.HasVertexAttribute(VertexAttribute.Tangent) ? m.tangents : null;
        bool hasCol = m.HasVertexAttribute(VertexAttribute.Color);
        bool col32 = hasCol && m.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8;
        var cF = hasCol && !col32 ? m.colors : null; var cB = col32 ? m.colors32 : null;
        var uvAll = new List<Vector4>[4]; var dim = new int[4];
        for (int ch = 0; ch < 4; ch++)
        {
            var at = VertexAttribute.TexCoord0 + ch;
            if (!m.HasVertexAttribute(at)) continue;
            dim[ch] = m.GetVertexAttributeDimension(at); uvAll[ch] = new List<Vector4>(v.Length); m.GetUVs(ch, uvAll[ch]);
        }
        string name = m.name;
        var map = new int[v.Length]; var stamp = new int[v.Length]; for (int i = 0; i < stamp.Length; i++) stamp[i] = -1;
        var tr = r.transform; bool first = true; int made = 0;
        for (int tIdx = 0; tIdx < cnt.Length; tIdx++)
        {
            if (cnt[tIdx] == 0) continue;
            sV.Clear(); sN.Clear(); sT.Clear(); sC.Clear(); sC32.Clear(); sTri.Clear();
            for (int ch = 0; ch < 4; ch++) sUv[ch].Clear();
            for (int k = 0; k < nt; k++)
            {
                if (of[k] != tIdx) continue;
                for (int e = 0; e < 3; e++)
                {
                    int vi = tri[3 * k + e];
                    if (stamp[vi] != tIdx)
                    {
                        stamp[vi] = tIdx; map[vi] = sV.Count; sV.Add(v[vi]);
                        if (nrm != null) sN.Add(nrm[vi]);
                        if (tan != null) sT.Add(tan[vi]);
                        if (cF != null) sC.Add(cF[vi]);
                        if (cB != null) sC32.Add(cB[vi]);
                        for (int ch = 0; ch < 4; ch++) if (uvAll[ch] != null) sUv[ch].Add(uvAll[ch][vi]);
                    }
                    sTri.Add(map[vi]);
                }
            }
            Mesh dst;
            if (first) { dst = m; dst.Clear(); }
            else dst = new Mesh { name = name };
            dst.indexFormat = sV.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            dst.SetVertices(sV);
            if (nrm != null) dst.SetNormals(sN);
            if (tan != null) dst.SetTangents(sT);
            if (cF != null) dst.SetColors(sC);
            if (cB != null) dst.SetColors(sC32);
            for (int ch = 0; ch < 4; ch++) if (uvAll[ch] != null) SetUv(dst, ch, dim[ch], sUv[ch]);
            dst.SetTriangles(sTri, 0, true);
            if (!first)
            {
                var go = new GameObject(r.gameObject.name); go.layer = r.gameObject.layer;
                go.transform.SetParent(tr.parent, false);
                go.transform.localPosition = tr.localPosition; go.transform.localRotation = tr.localRotation; go.transform.localScale = tr.localScale;
                go.AddComponent<MeshFilter>().sharedMesh = dst;
                var nr = go.AddComponent<MeshRenderer>();
                nr.sharedMaterials = r.sharedMaterials; nr.shadowCastingMode = r.shadowCastingMode; nr.receiveShadows = r.receiveShadows;
                nr.lightProbeUsage = r.lightProbeUsage; nr.reflectionProbeUsage = r.reflectionProbeUsage; nr.enabled = r.enabled;
                go.SetActive(r.gameObject.activeSelf);
            }
            first = false; made++;
        }
        return made;
    }
    static readonly List<Vector2> u2 = new List<Vector2>(); static readonly List<Vector3> u3 = new List<Vector3>();
    static void SetUv(Mesh m, int ch, int dim, List<Vector4> src)
    {
        if (dim <= 2) { u2.Clear(); for (int i = 0; i < src.Count; i++) u2.Add(src[i]); m.SetUVs(ch, u2); }
        else if (dim == 3) { u3.Clear(); for (int i = 0; i < src.Count; i++) u3.Add(src[i]); m.SetUVs(ch, u3); }
        else m.SetUVs(ch, src);
    }

    // ---------------------------------------------------------------- per-renderer facts
    void Collect(GameObject root)
    {
        var all = root.GetComponentsInChildren<MeshRenderer>(true);
        var rs = new List<MeshRenderer>(all.Length);
        foreach (var r in all) if (r != null && !Skip(r.gameObject) && r.GetComponent<MeshFilter>() != null && r.GetComponent<MeshFilter>().sharedMesh != null) rs.Add(r);
        Rs = rs.ToArray(); int n = Rs.Length;
        Tris = new int[n]; Alpha = new bool[n]; Box = new Bounds[n]; Reach = new float[n]; off = new bool[n];
        CardStart = new int[n]; CardCount = new int[n];
        var cards = new List<Vector4>();
        float k = Mathf.Clamp(Screen.height, 720, 1440) / (2f * Mathf.Tan(FovRef * 0.5f * Mathf.Deg2Rad)), px = PxMin(Gfx);
        TrisAll = 0;
        for (int i = 0; i < n; i++)
        {
            var r = Rs[i]; var mf = r.GetComponent<MeshFilter>(); var m = mf.sharedMesh;
            int tc = 0; for (int s = 0; s < m.subMeshCount; s++) tc += (int)(m.GetIndexCount(s) / 3);
            Tris[i] = tc; TrisAll += tc;
            Box[i] = WorldBox(m.bounds, r.transform.localToWorldMatrix);
            var mat = r.sharedMaterial;
            Alpha[i] = mat != null && ((mat.renderQueue >= 2450 && mat.renderQueue < 3000) || (mat.shader != null && (mat.shader.name.Contains("Foliage") || mat.shader.name.Contains("Cutout"))));
            CardStart[i] = cards.Count;
            if (Alpha[i] && m.isReadable) CardArea(m, r.transform.localToWorldMatrix, cards);
            CardCount[i] = cards.Count - CardStart[i];
            if (IsGround(r.name)) { Reach[i] = float.PositiveInfinity; continue; }
            float thick = m.isReadable ? Thickness(m, r.transform.localToWorldMatrix, Box[i]) : Box[i].size.y;
            float flat = Mathf.Max(Box[i].size.x, Box[i].size.z);
            if (thick < 0.6f && flat > 20f) { Reach[i] = float.PositiveInfinity; continue; } // flat ribbons and decals: part of the ground
            if (thick > 25f) { Reach[i] = float.PositiveInfinity; continue; }
            Reach[i] = SizeReach(k * Mathf.Max(thick, 0.1f), px);
        }
        Cards = cards.ToArray();
        // instanced items: one tree or shrub each, its height is the box's (the same rules as a renderer's)
        int xn = ExtraCount; xReach = new float[xn];
        for (int i = 0; i < xn; i++)
        {
            float thick = xBox[i].size.y;
            xReach[i] = thick > 25f ? float.PositiveInfinity : SizeReach(k * Mathf.Max(thick, 0.1f), px);
        }
    }

    // farthest distance at which an object `kh` pixel-metres tall still shows PxMin pixels of contrast through the haze
    static float SizeReach(float kh, float px)
    {
        float lo = 1f, hi = 6000f;
        for (int it = 0; it < 24; it++)
        {
            float d = 0.5f * (lo + hi);
            float keep = Mathf.Max(0.25f, Mathf.Clamp01((FogEnd - 0.68f * d) / (FogEnd - FogStart)));
            if (kh / d * keep >= px) lo = d; else hi = d;
        }
        return lo;
    }

    static Bounds WorldBox(Bounds b, Matrix4x4 m)
    {
        if (m.isIdentity) return b;
        var w = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
        for (int i = 0; i < 8; i++)
            w.Encapsulate(m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z)));
        return w;
    }

    // the height of the tallest thing in the mesh: vertical extent per 16 m cell (not the bounds, which also hold the
    // slope of the land under a long chunk)
    static readonly Dictionary<long, Vector2> cellY = new Dictionary<long, Vector2>();
    static float Thickness(Mesh m, Matrix4x4 l2w, Bounds wb)
    {
        if (wb.size.y < 3f) return wb.size.y;
        var v = m.vertices; int step = Mathf.Max(1, v.Length / 40000); bool id = l2w.isIdentity;
        cellY.Clear(); float best = 0f;
        for (int i = 0; i < v.Length; i += step)
        {
            var p = id ? v[i] : l2w.MultiplyPoint3x4(v[i]);
            long key = ((long)Mathf.FloorToInt(p.x / 16f) << 32) ^ (uint)Mathf.FloorToInt(p.z / 16f);
            if (cellY.TryGetValue(key, out var mm)) { mm.x = Mathf.Min(mm.x, p.y); mm.y = Mathf.Max(mm.y, p.y); cellY[key] = mm; best = Mathf.Max(best, mm.y - mm.x); }
            else cellY[key] = new Vector2(p.y, p.y);
        }
        cellY.Clear();
        return best;
    }

    // alpha-tested triangles summed per 12 m cluster (PerfStats estimates the card overdraw from them)
    static readonly Dictionary<long, Vector4> clus = new Dictionary<long, Vector4>();
    static void CardArea(Mesh m, Matrix4x4 l2w, List<Vector4> outList)
    {
        var v = m.vertices; var tri = m.triangles; bool id = l2w.isIdentity; clus.Clear();
        for (int k = 0; k + 2 < tri.Length; k += 3)
        {
            Vector3 a = v[tri[k]], b = v[tri[k + 1]], c = v[tri[k + 2]];
            if (!id) { a = l2w.MultiplyPoint3x4(a); b = l2w.MultiplyPoint3x4(b); c = l2w.MultiplyPoint3x4(c); }
            float area = 0.5f * Vector3.Cross(b - a, c - a).magnitude; var ce = (a + b + c) * (1f / 3f);
            long key = ((long)Mathf.FloorToInt(ce.x / 12f) << 32) ^ (uint)Mathf.FloorToInt(ce.z / 12f);
            clus.TryGetValue(key, out var acc);
            clus[key] = new Vector4(acc.x + ce.x * area, acc.y + ce.y * area, acc.z + ce.z * area, acc.w + area);
        }
        foreach (var kv in clus) { var q = kv.Value; if (q.w > 0f) outList.Add(new Vector4(q.x / q.w, q.y / q.w, q.z / q.w, q.w)); }
        clus.Clear();
    }

    // ---------------------------------------------------------------- 2. the view set of every sector
    float[] occ; int onx, onz; float ox0, oz0;

    void BuildSets()
    {
        int N = t.N, n = Rs.Length;
        trackY = new float[N];
        for (int i = 0; i < N; i++) trackY[i] = WorldBuilder.H(t.pts[i].x, t.pts[i].y);
        sectorPts = Mathf.Max(1, Mathf.RoundToInt(SectorLen / (float)t.ds));
        Sectors = (N + sectorPts - 1) / sectorPts;
        words = (n + 63) >> 6;
        sets = new ulong[Sectors * words];
        int xn = ExtraCount; xWords = (xn + 63) >> 6; xSets = xn > 0 ? new ulong[Sectors * xWords] : null;
        BuildOccluder();
        int margin = Mathf.RoundToInt(AlongMargin / (float)t.ds);
        var prof = Occlusion ? new float[9 * Az * Steps] : null; var eyes = new Vector3[9];
        // the top face of every renderer's bounds, as points for the horizon test
        var tStart = new int[n + 1]; var tops = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            tStart[i] = tops.Count;
            if (!Occlusion) continue;
            var b = Box[i]; int nx = Mathf.Clamp(Mathf.CeilToInt(b.size.x / 48f), 1, 6), nz = Mathf.Clamp(Mathf.CeilToInt(b.size.z / 48f), 1, 6);
            for (int a = 0; a < nx; a++) for (int c = 0; c < nz; c++)
                tops.Add(new Vector3(b.min.x + (a + 0.5f) * b.size.x / nx, b.max.y, b.min.z + (c + 0.5f) * b.size.z / nz));
        }
        tStart[n] = tops.Count;
        var xStart = new int[xn + 1];
        for (int i = 0; i < xn; i++)
        {
            xStart[i] = tops.Count;
            if (!Occlusion) continue;
            var b = xBox[i]; int nx = Mathf.Clamp(Mathf.CeilToInt(b.size.x / 48f), 1, 6), nz = Mathf.Clamp(Mathf.CeilToInt(b.size.z / 48f), 1, 6);
            for (int a = 0; a < nx; a++) for (int c = 0; c < nz; c++)
                tops.Add(new Vector3(b.min.x + (a + 0.5f) * b.size.x / nx, b.max.y, b.min.z + (c + 0.5f) * b.size.z / nz));
        }
        xStart[xn] = tops.Count;
        long total = 0;
        for (int s = 0; s < Sectors; s++)
        {
            int i0 = s * sectorPts, i1 = Mathf.Min(N, i0 + sectorPts);
            // the camera region: an axis-aligned box round every centreline point of the sector (and the margin)
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); var hi = -lo;
            for (int i = i0 - margin; i < i1 + margin; i++)
            {
                int k = ((i % N) + N) % N; var p = t.pts[k]; float L = (float)t.HalfWidth(k) + LatExtra;
                for (int side = -1; side <= 1; side += 2)
                {
                    float x = (float)(p.x + p.nx * L * side), z = (float)(p.y + p.ny * L * side);
                    lo = Vector3.Min(lo, new Vector3(x, trackY[k] - EyeDown, z)); hi = Vector3.Max(hi, new Vector3(x, trackY[k] + EyeUp, z));
                }
            }
            if (Occlusion)
            {
                int e = 0;
                for (int a = 0; a < 3; a++)
                {
                    int k = a == 0 ? i0 : a == 1 ? (i0 + i1) / 2 : Mathf.Max(i0, i1 - 1); var p = t.pts[k]; float L = (float)t.HalfWidth(k) + LatExtra;
                    for (int side = -1; side <= 1; side++)
                    {
                        float x = (float)(p.x + p.nx * L * side), z = (float)(p.y + p.ny * L * side);
                        float y = Mathf.Max(trackY[k], OccAt(x, z)) + EyeUp;
                        eyes[e] = new Vector3(x, y, z); Horizon(eyes[e], prof, e * Az * Steps); e++;
                    }
                }
            }
            int baseW = s * words; int count = 0;
            for (int i = 0; i < n; i++)
            {
                var b = Box[i];
                float dx = Mathf.Max(0f, Mathf.Max(b.min.x - hi.x, lo.x - b.max.x)), dy = Mathf.Max(0f, Mathf.Max(b.min.y - hi.y, lo.y - b.max.y)), dz = Mathf.Max(0f, Mathf.Max(b.min.z - hi.z, lo.z - b.max.z));
                float d = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > Reach[i]) continue;
                if (Occlusion && d > OccNear && !SeenFromAny(eyes, prof, tops, tStart[i], tStart[i + 1])) continue;
                sets[baseW + (i >> 6)] |= 1UL << (i & 63); count++;
            }
            total += count;
            int xw = s * xWords;
            for (int i = 0; i < xn; i++)
            {
                var b = xBox[i];
                float dx = Mathf.Max(0f, Mathf.Max(b.min.x - hi.x, lo.x - b.max.x)), dy = Mathf.Max(0f, Mathf.Max(b.min.y - hi.y, lo.y - b.max.y)), dz = Mathf.Max(0f, Mathf.Max(b.min.z - hi.z, lo.z - b.max.z));
                float d = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > xReach[i]) continue;
                if (Occlusion && d > OccNear && !SeenFromAny(eyes, prof, tops, xStart[i], xStart[i + 1])) continue;
                xSets[xw + (i >> 6)] |= 1UL << (i & 63);
            }
        }
        AvgSet = Sectors > 0 ? (int)(total / Sectors) : 0;
        occ = null;
    }

    // a lower bound of the terrain height on a 24 m grid (from the terrain meshes); -inf where there is none
    void BuildOccluder()
    {
        Occlusion = false; ReliefM = 0f;
        var land = new List<int>();
        for (int i = 0; i < Rs.Length; i++) { string nm = Rs[i].name; if (nm == "Terrain" || nm == "EnvRealLand") land.Add(i); }
        if (land.Count == 0) return;
        var bb = Box[land[0]]; foreach (int i in land) bb.Encapsulate(Box[i]);
        ox0 = bb.min.x; oz0 = bb.min.z; onx = Mathf.CeilToInt(bb.size.x / OccCell) + 1; onz = Mathf.CeilToInt(bb.size.z / OccCell) + 1;
        if ((long)onx * onz > 4000000) return;
        occ = new float[onx * onz]; for (int i = 0; i < occ.Length; i++) occ[i] = float.PositiveInfinity;
        foreach (int i in land)
        {
            var m = Rs[i].GetComponent<MeshFilter>().sharedMesh; if (m == null || !m.isReadable) continue;
            var l2w = Rs[i].transform.localToWorldMatrix; bool id = l2w.isIdentity;
            var v = m.vertices; var tri = m.triangles;
            for (int k = 0; k + 2 < tri.Length; k += 3)
            {
                Vector3 a = v[tri[k]], b = v[tri[k + 1]], c = v[tri[k + 2]];
                if (!id) { a = l2w.MultiplyPoint3x4(a); b = l2w.MultiplyPoint3x4(b); c = l2w.MultiplyPoint3x4(c); }
                float ymin = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
                int ia = Mathf.Max(0, (int)((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - ox0) / OccCell)), ib = Mathf.Min(onx - 1, (int)((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - ox0) / OccCell));
                int ja = Mathf.Max(0, (int)((Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - oz0) / OccCell)), jb = Mathf.Min(onz - 1, (int)((Mathf.Max(a.z, Mathf.Max(b.z, c.z)) - oz0) / OccCell));
                for (int j = ja; j <= jb; j++) for (int q = ia; q <= ib; q++) { int o = j * onx + q; if (ymin < occ[o]) occ[o] = ymin; }
            }
        }
        float mn = float.MaxValue, mx = float.MinValue;
        for (int i = 0; i < occ.Length; i++)
        {
            if (float.IsPositiveInfinity(occ[i])) { occ[i] = float.NegativeInfinity; continue; }
            mn = Mathf.Min(mn, occ[i]); mx = Mathf.Max(mx, occ[i]);
        }
        ReliefM = mx > mn ? mx - mn : 0f;
        Occlusion = ReliefM >= Relief;
        if (!Occlusion) occ = null;
    }
    float OccAt(float x, float z)
    {
        if (occ == null) return float.NegativeInfinity;
        int i = (int)((x - ox0) / OccCell), j = (int)((z - oz0) / OccCell);
        if (i < 0 || j < 0 || i >= onx || j >= onz) return float.NegativeInfinity;
        return occ[j * onx + i];
    }
    // the steepest terrain slope seen so far along each direction, step by step (lower-bound terrain minus the margin)
    void Horizon(Vector3 e, float[] prof, int o)
    {
        for (int a = 0; a < Az; a++)
        {
            float ang = a * (2f * Mathf.PI / Az), cx = Mathf.Cos(ang), cz = Mathf.Sin(ang), best = float.NegativeInfinity;
            int row = o + a * Steps;
            for (int k = 0; k < Steps; k++)
            {
                float d = (k + 1) * StepLen;
                float h = OccAt(e.x + cx * d, e.z + cz * d) - OccMargin;
                float sl = (h - e.y) / d; if (sl > best) best = sl;
                prof[row + k] = best;
            }
        }
    }
    bool SeenFromAny(Vector3[] eyes, float[] prof, List<Vector3> tops, int a, int b)
    {
        for (int e = 0; e < 9; e++)
        {
            var E = eyes[e]; int o = e * Az * Steps;
            for (int q = a; q < b; q++)
            {
                var P = tops[q]; float dx = P.x - E.x, dz = P.z - E.z, d = Mathf.Sqrt(dx * dx + dz * dz);
                int kk = Mathf.FloorToInt((d - 40f) / StepLen) - 1; // the terrain up to 40 m short of the target
                if (kk < 0) return true;
                if (kk >= Steps) kk = Steps - 1;                       // (beyond the profile: as blocked as its end)
                float sl = (P.y - E.y) / d;
                int az = Mathf.RoundToInt(Mathf.Atan2(dz, dx) * (Az / (2f * Mathf.PI)));
                for (int w = -1; w <= 1; w++)
                {
                    int ai = ((az + w) % Az + Az) % Az;
                    if (sl > prof[o + ai * Steps + kk]) return true;
                }
            }
        }
        return false;
    }

    // ================================================================ 3. run time
    Camera cam;
    void LateUpdate()
    {
        if (sets == null) return;
        if (cam == null) { cam = Camera.main; if (cam == null) return; }
        int s = Enabled ? Pick(cam) : -1;
        Bypass = s < 0; Sector = s;
        if (s == applied) return;
        applied = s;
        int hidden = 0, w0 = s * words;
        for (int i = 0; i < Rs.Length; i++)
        {
            bool want = s < 0 || ((sets[w0 + (i >> 6)] >> (i & 63)) & 1UL) != 0;
            if (off[i] == want) { off[i] = !want; if (Rs[i] != null) Rs[i].forceRenderingOff = !want; }
            if (!want) hidden++;
        }
        Hidden = hidden;
    }

    // instanced item k in the set of the sector applied this frame (true outside every region, or without sets)
    public bool ExtraOn(int k)
    {
        if (xSets == null || sets == null || applied < 0 || k < 0 || k >= ExtraCount) return true;
        return ((xSets[applied * xWords + (k >> 6)] >> (k & 63)) & 1UL) != 0;
    }

    // the sector whose camera region holds the camera, or -1 (draw everything)
    int Pick(Camera c)
    {
        if (c.fieldOfView < FovRef - 1f) return -1; // zoomed TV lens: things look bigger than the sets assume
        var p = c.transform.position;
        var nr = t.Nearest(p.x, p.z, hint); hint = nr.idx;
        if (nr.idx < 0 || System.Math.Abs(nr.lateral) > t.HalfWidth(nr.idx) + LatExtra - 1.0) return -1;
        float y = trackY[nr.idx];
        if (p.y > y + EyeUp - 0.5f || p.y < y - EyeDown + 0.5f) return -1;
        return Mathf.Min(Sectors - 1, nr.idx / sectorPts);
    }
}
