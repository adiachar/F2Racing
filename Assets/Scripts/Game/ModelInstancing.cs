// ModelInstancing.cs — GPU instancing for the real model trees and shrubs (ModelLib.cs, Medium / High, circuits with
// EnvDef.realModels: Monza's park and the procedural parkland circuits).
//
// Without it every tree's vertices are copied into merged 400 m meshes (ModelLib.Put): about 3.9k vertices per LOD 0 tree,
// 1k per LOD 1 tree, 400 per shrub, 56 bytes each (position, normal, uv0, uv2, float colour), plus the CPU copy (the
// meshes stay readable) and the static batch. With it there is ONE mesh per model and LOD (5 species x 2 LODs, one
// mesh with the five species' impostors, 2 shrubs: 13 meshes, ~25k vertices in all), and each tree is a matrix and a
// float4 drawn with Graphics.DrawMeshInstanced. What changes per frame is what is drawn, never how it looks:
//  * the light: LightBake bakes every instance vertex exactly as it baked the merged vertex (same world position and
//    normal, the instance's own triangles in the roof map, the leaf / wood tint of BakeTint multiplied in), and the
//    result goes into one RGBA half-float texture (2048 wide) that FoliageInst.shader reads per vertex;
//  * per-instance culling: the instance's box in SectorVisibility's 50 m sector sets (size / terrain tests, the same as
//    a renderer's) and the camera frustum;
//  * per-instance LOD by camera distance with the existing distances (ModelLib.Lod0Dist 48 m High / 36 m Medium,
//    Lod1Dist 100 m): full mesh within Lod0Dist, LOD 1 to Lod1Dist, the two-card impostor beyond, each switch a dithered
//    cross-fade over the 8 / 12 m BEYOND the distance (so within it the finer mesh is always whole), and never finer than
//    the LOD the build gave the tree (the vertex budgets stay what they were). A zoomed lens (TV cameras) scales the
//    distance down. Before, a tree kept its build LOD at every distance (a LOD 0 tree 1 km away was still 2.3k triangles).
// The impostor-only trees (static LOD 2, ~8 vertices each) and the lamps stay in ModelLib's merged meshes: instancing
// an 8-vertex mesh costs more per instance than the vertices themselves.
// Used only where SystemInfo.supportsInstancing, vertex texture reads and RGBA half textures work (GLES3, Vulkan,
// Metal, WebGL2) and FoliageInst.shader is supported; otherwise (or with the shot parameter inst=0) ModelLib merges
// everything exactly as before. No allocation per frame: the matrix / property arrays and blocks are made at load.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DefaultExecutionOrder(100)] // after SectorVisibility.LateUpdate: this frame's sector
public class ModelInstancing : MonoBehaviour
{
    public static bool Allowed = true;                 // shot parameter inst=0: the merged path, for A/B shots
    public static ModelInstancing Cur;
    public static int Draws, Shown, Count; public static long Tris; // last frame (PerfStats)
    public static int LastCount, LastMeshes;           // last build (ModelLib's log line)

    const int TexW = 2048, MaxBatch = 1023;
    const float Fade0 = 8f, Fade1 = 12f, FovRef = 45f; // cross-fade widths beyond Lod0Dist / Lod1Dist (m)
    static readonly int InstId = Shader.PropertyToID("_Inst"), BakeId = Shader.PropertyToID("_BakeTex");

    // ================================================================ build queue (ModelLib.Put -> Flush)
    struct Pend { public ModelLib.Model m; public int lod; public Vector3 pos; public float yaw, sxz, sy, shade, sway, phase; public Color tint; }
    static readonly List<Pend> queue = new List<Pend>();
    public static int QueueCount => queue.Count;
    public static void ResetQueue() => queue.Clear();
    public static void Queue(ModelLib.Model m, int lod, Vector3 pos, float yaw, float sxz, float sy, Color tint, float shade, float sway, float phase)
        => queue.Add(new Pend { m = m, lod = lod, pos = pos, yaw = yaw, sxz = sxz, sy = sy, tint = tint, shade = shade, sway = sway, phase = phase });

    static Shader shader; static bool shaderTried;
    static Shader Sh { get { if (!shaderTried) { shaderTried = true; shader = Resources.Load<Shader>("FoliageInst"); } return shader; } }

    // can this device draw the instanced trees (decided by ModelLib.Begin, once per build)
    public static bool Usable()
    {
        if (!Allowed || !SystemInfo.supportsInstancing || SystemInfo.graphicsShaderLevel < 30 || SystemInfo.maxTextureSize < TexW) return false;
        if (!SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf)) return false;
        var s = Sh; return s != null && s.isSupported;
    }
    internal static void FromQuery(Dictionary<string, string> q) { if (q != null && DebugShots.Get(q, "inst", "1") == "0") Allowed = false; }
    // the instancer of the world under root (null: none, or an older world's that is still being destroyed)
    public static ModelInstancing ForRoot(GameObject root) => Cur != null && root != null && Cur.transform.IsChildOf(root.transform) ? Cur : null;
    // the shader EnvSurface keeps in step with the graphics level (null when this world has no instances)
    public static Shader ShaderFor(GameObject root) => ForRoot(root) != null ? Sh : null;

    // ================================================================ per world
    sealed class Bucket
    {
        public Mesh mesh; public int tris; public Matrix4x4[] mats; public Vector4[] props; public int count, cap;
        public readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();
    }
    Bucket[] buckets; Material mat; Texture2D bakeTex;
    // per instance: the build's LOD (0 / 1; shrubs 0), the coarsest it may use, its mesh (bucket) per LOD, its first texel
    // in the bake texture per LOD, its matrix, foot, sway code (sway + 4 x species), phase
    int n; int[] lodMin, lodMax, bucketOf, texBase; Matrix4x4[] mtx; Vector3[] foot; float[] swayCode, phase;
    public Bounds[] Boxes;
    Pend[] src; int texels; float l0, l1; bool baked;
    readonly Plane[] planes = new Plane[6];
    SectorVisibility sv; bool svChecked;

    // ModelLib.Flush: meshes, material and per-instance data for everything queued this build
    public static void Flush(Transform holder, Material foliage, float lod0Dist, float lod1Dist, ModelLib.Model[] trees, ModelLib.Model[] shrubs)
    {
        LastCount = LastMeshes = 0;
        if (queue.Count == 0) return;
        var go = new GameObject("ModelsInstanced"); go.transform.SetParent(holder, false);
        var mi = go.AddComponent<ModelInstancing>();
        mi.Build(foliage, lod0Dist, lod1Dist, trees, shrubs);
        queue.Clear();
        Cur = mi;
        LastCount = mi.n; LastMeshes = mi.buckets.Length;
    }

    void OnDestroy()
    {
        if (Cur == this) Cur = null;
        if (buckets != null) foreach (var b in buckets) if (b != null && b.mesh != null) Destroy(b.mesh);
        if (mat != null) Destroy(mat);
        if (bakeTex != null) Destroy(bakeTex);
    }

    void Build(Material foliage, float lod0Dist, float lod1Dist, ModelLib.Model[] trees, ModelLib.Model[] shrubs)
    {
        l0 = lod0Dist; l1 = lod1Dist;
        mat = new Material(Sh) { name = "ModelFoliageInst", enableInstancing = true };
        mat.CopyPropertiesFromMaterial(foliage); // atlas, cut-off, wind, sun direction, back-light: as the merged trees
        mat.enableInstancing = true;
        // meshes: per tree species LOD 0 and LOD 1, one mesh with every species' impostor, per shrub model one
        var meshes = new List<Mesh>(); var mTris = new List<int>();
        int ns = trees.Length; var treeMesh = new int[ns * 2];
        for (int s = 0; s < ns; s++)
            for (int L = 0; L < 2; L++)
            {
                treeMesh[s * 2 + L] = -1;
                if (trees[s].lods.Length <= L) continue;
                treeMesh[s * 2 + L] = meshes.Count; meshes.Add(MakeMesh(trees[s].name + "_" + L, new[] { trees[s] }, L, -1)); mTris.Add(trees[s].lods[L].tri.Length / 3);
            }
        int imp = -1; int impTris = 4;
        { bool all = ns > 0; foreach (var m in trees) if (m.lods.Length < 3) all = false;
          if (all) { imp = meshes.Count; meshes.Add(MakeMesh("Impostors", trees, 2, 0)); impTris = trees[0].lods[2].tri.Length / 3; mTris.Add(impTris); } }
        var shrubMesh = new int[shrubs.Length];
        for (int s = 0; s < shrubs.Length; s++) { shrubMesh[s] = meshes.Count; meshes.Add(MakeMesh(shrubs[s].name, new[] { shrubs[s] }, 0, -1)); mTris.Add(shrubs[s].lods[0].tri.Length / 3); }
        foreach (var m in meshes) m.UploadMeshData(true); // GPU only: the bake reads the model data, not these

        // instances
        src = queue.ToArray(); n = src.Length;
        lodMin = new int[n]; lodMax = new int[n]; bucketOf = new int[n * 3]; texBase = new int[n * 3];
        mtx = new Matrix4x4[n]; foot = new Vector3[n]; swayCode = new float[n]; phase = new float[n]; Boxes = new Bounds[n];
        var use = new int[meshes.Count];
        texels = 0;
        for (int i = 0; i < n; i++)
        {
            var p = src[i]; var m = p.m;
            int species = m.kind == 0 ? System.Array.IndexOf(trees, m) : System.Array.IndexOf(shrubs, m);
            lodMin[i] = p.lod; lodMax[i] = p.lod;
            for (int L = 0; L < 3; L++) { bucketOf[i * 3 + L] = -1; texBase[i * 3 + L] = -1; }
            if (m.kind == 0 && species >= 0)
            {
                bucketOf[i * 3] = treeMesh[species * 2]; bucketOf[i * 3 + 1] = treeMesh[species * 2 + 1]; bucketOf[i * 3 + 2] = imp;
                for (int L = p.lod + 1; L < 3 && bucketOf[i * 3 + L] >= 0; L++) lodMax[i] = L;
            }
            else if (species >= 0) bucketOf[i * 3] = shrubMesh[species];
            if (bucketOf[i * 3 + p.lod] < 0) { lodMax[i] = lodMin[i] = p.lod; bucketOf[i * 3 + p.lod] = -1; } // (cannot happen: every model has its mesh)
            for (int L = lodMin[i]; L <= lodMax[i]; L++)
            {
                if (bucketOf[i * 3 + L] < 0) continue;
                use[bucketOf[i * 3 + L]]++; texBase[i * 3 + L] = texels; texels += m.lods[L].v.Length;
            }
            float c = Mathf.Cos(p.yaw), s = Mathf.Sin(p.yaw);
            // the rotation and scale ModelLib.Put applies: x' = (x c + z s) sxz, y' = y sy, z' = (-x s + z c) sxz
            var M = Matrix4x4.identity;
            M.m00 = c * p.sxz; M.m02 = s * p.sxz; M.m20 = -s * p.sxz; M.m22 = c * p.sxz; M.m11 = p.sy;
            M.m03 = p.pos.x; M.m13 = p.pos.y; M.m23 = p.pos.z;
            mtx[i] = M; foot[i] = p.pos;
            swayCode[i] = p.sway + 4f * Mathf.Max(0, species); phase[i] = p.phase;
            // box: every LOD it may show, plus the sway
            var b = new Bounds(p.pos, Vector3.zero);
            for (int L = lodMin[i]; L <= lodMax[i]; L++)
            {
                var v = m.lods[L].v; Vector3 lo = v[0], hi = v[0];
                for (int k = 1; k < v.Length; k++) { lo = Vector3.Min(lo, v[k]); hi = Vector3.Max(hi, v[k]); }
                for (int k = 0; k < 8; k++)
                    b.Encapsulate(M.MultiplyPoint3x4(new Vector3((k & 1) == 0 ? lo.x : hi.x, (k & 2) == 0 ? lo.y : hi.y, (k & 4) == 0 ? lo.z : hi.z)));
            }
            b.Expand(new Vector3(0.8f, 0f, 0.8f));
            Boxes[i] = b;
        }
        buckets = new Bucket[meshes.Count];
        for (int k = 0; k < meshes.Count; k++)
        {
            int cap = Mathf.Clamp(use[k], 1, MaxBatch);
            buckets[k] = new Bucket { mesh = meshes[k], tris = k == imp ? impTris : mTris[k], cap = cap, mats = new Matrix4x4[cap], props = new Vector4[cap] };
        }
    }

    // a shared mesh: model space; uv1 = (vertex index in its species block, sway weight for a sway of 1, species or -1)
    static Mesh MakeMesh(string name, ModelLib.Model[] ms, int lod, int speciesTag)
    {
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var uv1 = new List<Vector4>(); var tri = new List<int>();
        for (int s = 0; s < ms.Length; s++)
        {
            var m = ms[s]; var l = m.lods[lod]; int o = v.Count; float invH = 1f / Mathf.Max(0.01f, m.height);
            for (int i = 0; i < l.v.Length; i++)
            {
                v.Add(l.v[i]); uv.Add(l.uv[i]);
                float hh = Mathf.Clamp01(l.v[i].y * invH);
                uv1.Add(new Vector4(i, hh * hh, speciesTag < 0 ? -1f : s, 0f));
            }
            for (int i = 0; i < l.tri.Length; i++) tri.Add(o + l.tri[i]);
        }
        var mesh = new Mesh { name = "ModelInst_" + name };
        mesh.indexFormat = v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetUVs(1, uv1); mesh.SetTriangles(tri, 0, true);
        mesh.bounds = new Bounds(mesh.bounds.center, mesh.bounds.size + Vector3.one * 2f); // (the sway; not used for culling)
        return mesh;
    }

    // world vertex k of instance i at LOD L: exactly ModelLib.Put's arithmetic, so the bake sees the merged vertex
    static void World(in Pend p, ModelLib.Lod l, int k, out Vector3 w, out Vector3 nw)
    {
        float c = Mathf.Cos(p.yaw), s = Mathf.Sin(p.yaw);
        var q = l.v[k]; var nn = l.n[k];
        w = new Vector3(p.pos.x + (q.x * c + q.z * s) * p.sxz, p.pos.y + q.y * p.sy, p.pos.z + (-q.x * s + q.z * c) * p.sxz);
        nw = new Vector3(nn.x * c + nn.z * s, nn.y, -nn.x * s + nn.z * c);
    }

    // LightBake.AddOccluders: the triangles of each instance's build LOD (what the merged mesh held) into the roof map
    public void AddOccluders()
    {
        if (src == null) return;
        for (int i = 0; i < n; i++)
        {
            var p = src[i]; var l = p.m.lods[p.lod]; var t = l.tri;
            var wv = new Vector3[l.v.Length];
            for (int k = 0; k < wv.Length; k++) World(p, l, k, out wv[k], out _);
            for (int k = 0; k + 2 < t.Length; k += 3) LightBake.Occluder(wv[t[k]], wv[t[k + 1]], wv[t[k + 2]]);
        }
    }

    // LightBake.Apply (or the first frame, if the bake did not run): every instance vertex of every LOD it may show, lit
    // and tinted, into the bake texture. Returns the vertices baked.
    public int Bake()
    {
        if (baked || src == null) return 0;
        baked = true;
        var V = new Vector3[texels]; var N = new Vector3[texels]; var C = new Color[texels];
        for (int i = 0; i < n; i++)
            for (int L = lodMin[i]; L <= lodMax[i]; L++)
            {
                var l = src[i].m.lods[L]; int o = texBase[i * 3 + L]; if (o < 0) continue;
                for (int k = 0; k < l.v.Length; k++) World(src[i], l, k, out V[o + k], out N[o + k]);
            }
        if (LightBake.Active) LightBake.BakeFoliage(V, N, C);
        else for (int k = 0; k < C.Length; k++) C[k] = new Color(0.5f, 0.5f, 0.5f, 1f);
        int rows = Mathf.Max(1, (texels + TexW - 1) / TexW);
        var data = new ushort[TexW * rows * 4];
        for (int i = 0; i < n; i++)
        {
            var p = src[i];
            for (int L = lodMin[i]; L <= lodMax[i]; L++)
            {
                var l = p.m.lods[L]; int o = texBase[i * 3 + L]; if (o < 0) continue;
                for (int k = 0; k < l.v.Length; k++)
                {
                    // the tint ModelLib.Put stored and BakeTint.Apply multiplied into the baked light
                    float ao = l.ao[k] / 255f; bool wood = ModelLib.IsWood(l.uv[k]);
                    Color t = wood ? new Color(ao * p.shade, ao * p.shade, ao * p.shade, 1f) : new Color(ao * p.tint.r, ao * p.tint.g, ao * p.tint.b, 1f);
                    var c = C[o + k]; int d = (o + k) * 4;
                    data[d] = Mathf.FloatToHalf(c.r * t.r); data[d + 1] = Mathf.FloatToHalf(c.g * t.g); data[d + 2] = Mathf.FloatToHalf(c.b * t.b); data[d + 3] = Mathf.FloatToHalf(1f);
                }
            }
        }
        bakeTex = new Texture2D(TexW, rows, TextureFormat.RGBAHalf, false, true) { name = "ModelInstBake", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        bakeTex.SetPixelData(data, 0);
        bakeTex.Apply(false, true);
        mat.SetTexture(BakeId, bakeTex);
        src = null; // (the model data stays in ModelLib; nothing per instance is needed any more but the arrays above)
        return texels;
    }

    // ================================================================ per frame
    public static bool Off; // PerfLab A/B: draw no instanced trees / shrubs
    void LateUpdate()
    {
        Draws = 0; Shown = 0; Tris = 0; Count = n;
        if (n == 0 || buckets == null || Off) return; // (Off: PerfLab A/B switch, trees hidden)
        if (!baked) Bake();
        var cam = Camera.main; if (cam == null) return;
        if (!svChecked || sv == null) { sv = SectorVisibility.Cur; svChecked = true; if (sv != null && !transform.IsChildOf(sv.transform)) sv = null; }
        bool sets = sv != null && sv.ExtraCount == n;
        GeometryUtility.CalculateFrustumPlanes(cam, planes);
        Vector3 cp = cam.transform.position;
        // a zoomed lens shows a tree as big as a nearer one: LOD by the distance it looks (never coarser for a wide lens)
        float zoom = Mathf.Min(1f, Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(FovRef * 0.5f * Mathf.Deg2Rad));
        for (int i = 0; i < n; i++)
        {
            if (sets && !sv.ExtraOn(i)) continue;
            if (!GeometryUtility.TestPlanesAABB(planes, Boxes[i])) continue;
            Shown++;
            int lo = lodMin[i], hi = lodMax[i];
            if (lo == hi) { Add(i, lo, 1f); continue; }
            float dx = foot[i].x - cp.x, dz = foot[i].z - cp.z, d = Mathf.Sqrt(dx * dx + dz * dz) * zoom;
            int L; float f; // the finer LOD at this distance, and the coarser one's share in the cross-fade
            if (d <= l0) { L = 0; f = 0f; }
            else if (d < l0 + Fade0) { L = 0; f = (d - l0) / Fade0; }
            else if (d <= l1) { L = 1; f = 0f; }
            else if (d < l1 + Fade1) { L = 1; f = (d - l1) / Fade1; }
            else { L = 2; f = 0f; }
            if (L < lo) { L = lo; f = 0f; }
            if (L >= hi) { L = hi; f = 0f; }
            if (f <= 0f) Add(i, L, 1f);
            else { Add(i, L, -(1f - f)); Add(i, L + 1, f); }
        }
        for (int k = 0; k < buckets.Length; k++) if (buckets[k].count > 0) Submit(buckets[k]);
    }

    void Add(int i, int L, float fade)
    {
        int bi = bucketOf[i * 3 + L]; if (bi < 0) return;
        var b = buckets[bi];
        if (b.count == b.cap) Submit(b);
        b.mats[b.count] = mtx[i];
        b.props[b.count] = new Vector4(texBase[i * 3 + L], phase[i], swayCode[i], fade);
        b.count++;
    }

    void Submit(Bucket b)
    {
        b.mpb.SetVectorArray(InstId, b.props);
        Graphics.DrawMeshInstanced(b.mesh, 0, mat, b.mats, b.count, b.mpb, ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off);
        Draws++; Tris += (long)b.tris * b.count;
        b.count = 0;
    }
}
