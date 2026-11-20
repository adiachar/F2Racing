// CarLod.cs — distance levels of detail for the rivals' merged meshes (another part of CarModel, see CarModel.cs
// CreateMergedRival). Drawing only: nothing here touches the physics or where a car is.
//
// Why: the rivals were the biggest triangle load of a race frame. A far rival (car_far) is ~25k triangles drawn and a
// near one (car_lod) ~56k, whatever its distance: at 150 m a car is ~25 px long at 756p, so that is ~100 triangles per
// pixel. Measured with the WebGL shooter (PERF_STATS, Monza s=300, 12 rivals): cars 580k of the frame's 873k triangles.
// Tile GPUs (Mali) bin every triangle and write its varyings to memory; micro-triangles also shade 2x2 quads that
// are mostly empty. Real Racing 3 class games draw a far car in a few thousand triangles.
//
// What: the merged meshes are simplified once at load by vertex clustering (a grid of `cell` model units, ~1.085 m
// per unit), kept apart per part ID (vertex colour alpha: paint, wing, carbon, tyre ...) and per facing (6 normal
// buckets, so the two sides of a thin wing and the two-sided twins stay apart). The cluster position is the average of
// the part's vertices in the cell (shared by every facing, so no cracks inside a part), its normal the average per
// facing, its colour the average AO / disc mask. A LODGroup on the car switches by screen height (so the zoomed TV
// camera keeps the full car, as before), with the cell under ~0.6 px at the switch distance at 756p:
//   car_lod (near rivals, the safety car): full mesh to 36 m, then a 3 cm grid (~2x fewer triangles; GameManager
//                                          already swaps to car_far at 52-60 m)
//   car_far (far rivals):                  full mesh to 75 m, a 6 cm grid (~3x fewer) to 170 m, a 12 cm grid (~6x) beyond
// The wheels of a near rival keep spinning and steering at every level (their own simplified meshes on the same pivots).
// A car with a split (damaged) wing shows its full meshes at every distance (RivalLodsFull). The simplified meshes are
// built once per model and level (cached), shared by every rival, with the same shared material and property block
// (livery, brake glow ...), so GPU instancing still draws all rivals of one level in one call.
using System.Collections.Generic;
using UnityEngine;

public partial class CarModel
{
    // PerfLab "car LOD" A/B switch: false = every rival always draws its full meshes (as before)
    public static bool RivalLodOn = true;
    static readonly List<CarModel> lodCars = new List<CarModel>();
    public static void SetRivalLod(bool on)
    {
        RivalLodOn = on;
        for (int i = lodCars.Count - 1; i >= 0; i--)
        {
            var c = lodCars[i]; if (c == null) { lodCars.RemoveAt(i); continue; }
            if (c.lodGroup != null) c.lodGroup.ForceLOD(on && !c.mSplit ? -1 : 0);
        }
    }

    // PerfLab "rivals" A/B switch: the rival cars (and the safety car) are not drawn at all
    public static void SetRivalsHidden(bool hide)
    {
        foreach (var c in FindObjectsByType<CarModel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (c == null || !c.rival) continue;
            foreach (var r in c.GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = hide;
        }
    }

    LODGroup lodGroup;
    // RR3-style: the simplified levels draw with F2/CarPaintFar (CarPaint's per-vertex pass, any graphics level): a far
    // car is a few hundred pixels, so per-pixel probe reads and pows there bought nothing. One clone per rival set
    // (same keywords and values, so instancing still batches every rival of a level). PerfLab "car LOD" off = full.
    public static bool RivalFarLite = true;
    static Shader farShader; static bool farTried;
    static readonly Dictionary<Material, Material> farMats = new Dictionary<Material, Material>();
    static Material FarMat(RivalSet set)
    {
        var src = set.mat;
        if (!RivalFarLite || src == null) return src;
        if (farMats.TryGetValue(src, out var fm) && fm != null) return fm;
        if (!farTried) { farTried = true; farShader = Resources.Load<Shader>("CarPaintFar"); }
        if (farShader == null || !farShader.isSupported) return src;
        fm = new Material(src) { name = src.name + " far", shader = farShader, enableInstancing = true };
        fm.EnableKeyword("_MERGED");
        farMats[src] = fm;
        return fm;
    }
    static readonly Dictionary<Mesh, Mesh>[] lodCache = { new Dictionary<Mesh, Mesh>(), new Dictionary<Mesh, Mesh>() };
    const float Tan31 = 0.6009f; // tan(62 degrees / 2): the chase camera's lens, which the distances below are for

    void AddRivalLods()
    {
        if (!merged || rset == null || blockR.Count == 0) return;
        float[] cells = rset.far ? new[] { 0.06f, 0.12f } : new[] { 0.03f };
        float[] dist = rset.far ? new[] { 75f, 170f } : new[] { 36f };
        var levels = new List<Renderer>[cells.Length + 1];
        levels[0] = new List<Renderer>(blockR);
        int baseN = blockR.Count;
        for (int k = 0; k < cells.Length; k++)
        {
            levels[k + 1] = new List<Renderer>(baseN);
            for (int p = 0; p < baseN; p++)
            {
                var r0 = blockR[p]; var mf = r0.GetComponent<MeshFilter>();
                var src = mf != null ? mf.sharedMesh : null; if (src == null) continue;
                var cache = lodCache[k];
                if (!cache.TryGetValue(src, out var dm)) { dm = Simplify(src, cells[k]); cache[src] = dm; }
                if (dm == null) { levels[k + 1].Add(r0); continue; } // could not simplify: the full mesh stays at this level
                var go = new GameObject(r0.gameObject.name + "_lod" + (k + 1));
                // a child of the full part: it follows whatever moves or squashes that part (spin, steer, a flat tyre's scale)
                go.transform.SetParent(r0.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = dm;
                var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = FarMat(rset);
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                levels[k + 1].Add(r); blockR.Add(r);
            }
        }
        lodGroup = gameObject.AddComponent<LODGroup>();
        lodGroup.fadeMode = LODFadeMode.None;
        var lods = new LOD[levels.Length];
        for (int k = 0; k < levels.Length; k++) lods[k] = new LOD(0.5f - 0.1f * k, levels[k].ToArray()); // (placeholder heights, descending; set below from the bounds)
        lodGroup.SetLODs(lods);
        lodGroup.RecalculateBounds();
        // screen height at distance d: size / (2 d tan(fov / 2)), times the LOD bias (Unity scales by it)
        float size = lodGroup.size * Mathf.Max(1e-4f, transform.lossyScale.x), bias = Mathf.Max(0.05f, QualitySettings.lodBias);
        for (int k = 0; k < levels.Length; k++)
        {
            float d = k < dist.Length ? dist[k] : 20000f; // the last level is never culled by the LODGroup (the layer cull distance does that)
            lods[k].screenRelativeTransitionHeight = Mathf.Min(0.99f - 0.01f * k, size * bias / (2f * d * Tan31));
        }
        lodGroup.SetLODs(lods);
        lodCars.Add(this);
        if (!RivalLodOn) lodGroup.ForceLOD(0);
    }

    // a wing split off (damage): only the full meshes know about it, so they are shown at every distance
    void RivalLodsFull(bool full) { if (lodGroup != null) lodGroup.ForceLOD(full || !RivalLodOn ? 0 : -1); }
    void OnDestroy() { lodCars.Remove(this); }

    // vertex clustering, per part ID and facing (see the top of the file). Needs a readable mesh (the merged rival meshes are).
    static readonly Dictionary<long, int> posKey = new Dictionary<long, int>(), vtxKey = new Dictionary<long, int>();
    static Mesh Simplify(Mesh src, float cell)
    {
        if (src == null || !src.isReadable) return null;
        var v = src.vertices; var n = src.normals; var c = src.colors32; var tri = src.triangles;
        int nv = v.Length; if (nv == 0 || tri.Length == 0 || n.Length != nv || c.Length != nv) return null;
        posKey.Clear(); vtxKey.Clear();
        var pSum = new List<Vector3>(nv / 3); var pCnt = new List<int>(nv / 3);
        var oPos = new List<int>(nv / 2); var oN = new List<Vector3>(nv / 2); var oR = new List<float>(nv / 2); var oG = new List<float>(nv / 2);
        var oA = new List<byte>(nv / 2); var oCnt = new List<int>(nv / 2);
        var remap = new int[nv];
        float inv = 1f / cell;
        for (int i = 0; i < nv; i++)
        {
            var p = v[i];
            long qx = Mathf.FloorToInt(p.x * inv) + 2048, qy = Mathf.FloorToInt(p.y * inv) + 2048, qz = Mathf.FloorToInt(p.z * inv) + 2048;
            byte id = c[i].a;
            long pk = (((qx & 4095) << 36) | ((qy & 4095) << 24) | ((qz & 4095) << 12)) << 8 | id;
            if (!posKey.TryGetValue(pk, out int pi)) { pi = pSum.Count; posKey[pk] = pi; pSum.Add(Vector3.zero); pCnt.Add(0); }
            pSum[pi] += p; pCnt[pi]++;
            var nn = n[i]; float ax = Mathf.Abs(nn.x), ay = Mathf.Abs(nn.y), az = Mathf.Abs(nn.z);
            int dir = ax >= ay && ax >= az ? (nn.x >= 0 ? 0 : 1) : ay >= az ? (nn.y >= 0 ? 2 : 3) : (nn.z >= 0 ? 4 : 5);
            long vk = (long)pi * 8 + dir;
            if (!vtxKey.TryGetValue(vk, out int vi))
            {
                vi = oPos.Count; vtxKey[vk] = vi;
                oPos.Add(pi); oN.Add(Vector3.zero); oR.Add(0f); oG.Add(0f); oA.Add(id); oCnt.Add(0);
            }
            oN[vi] += nn; oR[vi] += c[i].r; oG[vi] += c[i].g; oCnt[vi]++;
            remap[i] = vi;
        }
        int m = oPos.Count;
        if (m >= 65000 || m >= nv * 0.9f) return null; // no real saving
        var ov = new Vector3[m]; var on = new Vector3[m]; var oc = new Color32[m];
        for (int k = 0; k < m; k++)
        {
            int pi = oPos[k]; ov[k] = pSum[pi] / pCnt[pi];
            var nn = oN[k]; on[k] = nn.sqrMagnitude > 1e-12f ? nn.normalized : Vector3.up;
            float w = 1f / oCnt[k];
            oc[k] = new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(oR[k] * w), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(oG[k] * w), 0, 255), 0, oA[k]);
        }
        var ot = new List<int>(tri.Length / 2); var seen = new HashSet<long>();
        for (int k = 0; k + 2 < tri.Length; k += 3)
        {
            int a = remap[tri[k]], b = remap[tri[k + 1]], d = remap[tri[k + 2]];
            if (a == b || b == d || a == d) continue;
            // the same three clusters in the same winding once (rotations of one triangle are the same triangle)
            int r0 = a, r1 = b, r2 = d;
            if (r1 < r0 && r1 < r2) { r0 = b; r1 = d; r2 = a; } else if (r2 < r0 && r2 < r1) { r0 = d; r1 = a; r2 = b; }
            long key = ((long)r0 << 32) | ((long)r1 << 16) | (long)r2;
            if (!seen.Add(key)) continue;
            ot.Add(a); ot.Add(b); ot.Add(d);
        }
        if (ot.Count == 0) return null;
        var mesh = new Mesh { name = src.name + "|lod" + cell.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) };
        mesh.vertices = ov; mesh.normals = on; mesh.colors32 = oc; mesh.SetTriangles(ot, 0); mesh.RecalculateBounds();
        mesh.UploadMeshData(true); // never split or read again: no CPU copy
        return mesh;
    }
}
