// ModelLib.cs — real, ready-made models (Poly Haven, CC0; see Resources/Models/CREDITS.txt) cut down for phones by
// Tools/Models/build_models.py: trees, shrubs and street lamps as small meshes that share ONE 2048 x 2048 atlas.
//
// Files (Resources/Models): models.bytes (meshes, format documented in build_models.py) and atlas.bytes (a JPEG for the
// colour and a PNG for the alpha, joined here). Trees have three LODs: 0 (<= 2.5k tris: a trimmed trunk and about 850
// leaf clumps, each a quad painted from the model's own leaves), 1 (<= 600 tris) and 2 (two crossed impostor cards, four
// triangles). Shrubs have one LOD, props (lamps, park benches and bins) one. Monza (RealWorld) plants its own park mix
// (PickPark: oak, robinia, plane, hornbeam-like, Scots pine; Tools/Models/build_park.py), the parkland circuits the old one.
//
// Use: Begin() once per world build (false = models not usable: Low graphics, no bake, files missing: keep the card
// trees), then TryTree / TryShrub / AddProp for every instance, then Flush(root) once, before the light bake. Instances
// are appended into merged meshes (a chunk per 400 m block and per 64k vertices, 16-bit indices); trees use
// Foliage.shader (alpha to coverage on Medium/High, sway, back-light), props the diffuse material the bake turns into
// Baked.shader. Per-instance tint (leaf colour, shade) rides in the vertex colour through BakeTint like the card trees.
// Vertex budgets per graphics level keep the cost at a handful of draw calls (static batching merges them by material).
// GPU instancing (ModelInstancing.cs, where the device can): the LOD 0 / LOD 1 trees and the shrubs are not merged but
// drawn as instances of one shared mesh per model and LOD, each switching LOD by its camera distance (the impostor-only
// trees and the lamps stay merged: a few vertices each). Budgets, placement and random numbers are the same either way.
using System.Collections.Generic;
using UnityEngine;
using FormulaSim;

public static class ModelLib
{
    public sealed class Lod { public Vector3[] v, n; public Vector2[] uv; public byte[] ao; public int[] tri; }
    public sealed class Model
    {
        public string name; public int kind; // 0 tree, 1 shrub, 2 prop
        public float height, radius; public Color leaf; // leaf: mean colour of the painted clumps (linear)
        public Lod[] lods;
    }

    static Dictionary<string, Model> models; static Texture2D atlas; static bool tried;
    static string[] treeNames, shrubNames;

    // ---------------------------------------------------------------- loading
    public static bool Load()
    {
        if (tried) return models != null && atlas != null;
        tried = true;
        try
        {
            var mb = Resources.Load<TextAsset>("Models/models"); var ab = Resources.Load<TextAsset>("Models/atlas");
            if (mb == null || ab == null) return false;
            models = Parse(mb.bytes); atlas = DecodeAtlas(ab.bytes);
            var tn = new List<string>(); var sn = new List<string>();
            foreach (var kv in models) { if (kv.Value.kind == 0) tn.Add(kv.Key); else if (kv.Value.kind == 1) sn.Add(kv.Key); }
            tn.Sort(); sn.Sort(); treeNames = tn.ToArray(); shrubNames = sn.ToArray();
        }
        catch (System.Exception e) { Debug.LogWarning("ModelLib: " + e.Message); models = null; atlas = null; }
        return models != null && atlas != null;
    }

    static Dictionary<string, Model> Parse(byte[] data)
    {
        var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
        if (r.ReadByte() != 'F' || r.ReadByte() != '2' || r.ReadByte() != 'M' || r.ReadByte() != 'D') throw new System.Exception("bad models.bytes");
        r.ReadUInt16(); int count = r.ReadUInt16();
        var d = new Dictionary<string, Model>();
        for (int mi = 0; mi < count; mi++)
        {
            var m = new Model();
            int nl = r.ReadByte(); m.name = System.Text.Encoding.UTF8.GetString(r.ReadBytes(nl));
            m.kind = r.ReadByte(); int lods = r.ReadByte(); r.ReadUInt16();
            m.height = r.ReadSingle(); m.radius = r.ReadSingle();
            m.leaf = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), 1f);
            var bmin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); var bsz = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            m.lods = new Lod[lods];
            for (int li = 0; li < lods; li++)
            {
                int nv = (int)r.ReadUInt32(), nt = (int)r.ReadUInt32();
                var l = new Lod { v = new Vector3[nv], n = new Vector3[nv], uv = new Vector2[nv], ao = new byte[nv], tri = new int[nt * 3] };
                for (int i = 0; i < nv; i++)
                {
                    float x = r.ReadInt16(), y = r.ReadInt16(), z = r.ReadInt16();
                    l.v[i] = new Vector3(bmin.x + (x + 32768f) / 65535f * bsz.x, bmin.y + (y + 32768f) / 65535f * bsz.y, bmin.z + (z + 32768f) / 65535f * bsz.z);
                    float nx = (sbyte)r.ReadByte(), ny = (sbyte)r.ReadByte(), nz = (sbyte)r.ReadByte();
                    l.n[i] = new Vector3(nx, ny, nz).normalized;
                    l.ao[i] = r.ReadByte();
                    l.uv[i] = new Vector2(r.ReadUInt16() / 65535f, r.ReadUInt16() / 65535f);
                }
                for (int i = 0; i < nt * 3; i++) l.tri[i] = r.ReadUInt16();
                m.lods[li] = l;
            }
            d[m.name] = m;
        }
        return d;
    }

    // colour from a JPEG, alpha from a PNG (ImageConversion, like EnvTex.LoadPhoto), joined into one RGBA texture with mips
    static Texture2D DecodeAtlas(byte[] data)
    {
        var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
        if (r.ReadByte() != 'F' || r.ReadByte() != '2' || r.ReadByte() != 'A' || r.ReadByte() != 'T') throw new System.Exception("bad atlas.bytes");
        var jpg = r.ReadBytes((int)r.ReadUInt32()); var png = r.ReadBytes((int)r.ReadUInt32());
        var tc = new Texture2D(2, 2, TextureFormat.RGBA32, false, false); var ta = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        if (!ImageConversion.LoadImage(tc, jpg, false) || !ImageConversion.LoadImage(ta, png, false)) { Object.Destroy(tc); Object.Destroy(ta); throw new System.Exception("atlas decode"); }
        var pc = tc.GetPixels32(); var pa = ta.GetPixels32(); int w = tc.width, h = tc.height;
        for (int i = 0; i < pc.Length; i++) pc[i].a = pa[i].r;
        Object.Destroy(tc); Object.Destroy(ta);
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { name = "ModelAtlas", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        t.SetPixels32(pc); t.Apply(true, false); t.Compress(true); t.Apply(false, true); // GPU-compressed (DXT/ETC2): ~5 MB instead of ~22 MB
        return t;
    }

    // ---------------------------------------------------------------- instances -> merged meshes
    sealed class Buf
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>(), uv2 = new List<Vector2>();
        public readonly List<Color> col = new List<Color>();
        public readonly List<int> tri = new List<int>();
    }
    static readonly Dictionary<long, List<Buf>> bufs = new Dictionary<long, List<Buf>>();
    static bool active, propsDone, inst; static int gfxLevel;
    // vertex budgets (this build): LOD0 trees, LOD1 trees, impostors, shrubs, props
    static readonly int[] used = new int[5]; static int[] cap = new int[5];
    public static int TreeCount, ShrubCount, PropCount, Vertices, DrawMeshes;
    public static readonly int[] LodCount = new int[3];

    // every model tree of this build: foot (x, y, z) and height in w, and its crown width (RoadShadow.cs draws their
    // shadows as dappled crowns; the meshes themselves, leaf quads and impostor cards, are not projected)
    public static readonly List<Vector4> TreeFeet = new List<Vector4>(); public static readonly List<float> TreeWidth = new List<float>();
    public static void Reset() { ModelInstancing.ResetQueue(); inst = false; TreeFeet.Clear(); TreeWidth.Clear(); bufs.Clear(); active = false; propsDone = false; for (int i = 0; i < used.Length; i++) used[i] = 0; TreeCount = ShrubCount = PropCount = Vertices = DrawMeshes = 0; LodCount[0] = LodCount[1] = LodCount[2] = 0; }

    // Medium and High only, with the baked lighting and the foliage shader; false = keep the card trees
    public static bool Begin(int gfx)
    {
        if (active) return true;
        if (gfx < 2 || !WorldBuilder.BakeReady || WorldBuilder.FoliageShader == null || !Load()) return false;
        gfxLevel = gfx; active = true;
        inst = ModelInstancing.Usable(); // decided once per build, before the first instance
        bool hi = gfx >= 3;
        cap = hi ? new[] { 90000, 100000, 16000, 24000, 30000 } : new[] { 54000, 62000, 10000, 14000, 16000 };
        park = RealWorld.Active && models.ContainsKey("park_oak") && models.ContainsKey("park_plane") && models.ContainsKey("park_robinia") && models.ContainsKey("park_pine");
        return true;
    }
    public static bool Active => active;

    // distance to the track (m) up to which trees are real models, and where each LOD ends
    public const float Reach = 150f;
    public static float Lod0Dist => gfxLevel >= 3 ? 48f : 36f;
    public static float Lod1Dist => 100f;

    static Buf Room(int kind, float x, float z, int need)
    {
        float cs = kind == 2 ? 1200f : 400f; // props (lamps, benches, bins): a few hundred vertices each, few chunks = few draws
        long key = ((long)kind << 40) ^ ((long)(Mathf.FloorToInt(x / cs) + 4096) << 20) ^ (long)(Mathf.FloorToInt(z / cs) + 4096);
        if (!bufs.TryGetValue(key, out var l)) bufs[key] = l = new List<Buf>();
        if (l.Count == 0 || l[l.Count - 1].v.Count + need > 60000) l.Add(new Buf());
        return l[l.Count - 1];
    }

    // wood (shade tint) or leaves (leaf tint) by atlas row: the bark strips, lamp tiles and the park props' palette lie
    // in v 0.25..0.5; leaf clumps and impostor cards above (v >= 0.5) and, for the park species, below (v < 0.25)
    public static bool IsWood(Vector2 uv) => uv.y < 0.5f && uv.y >= 0.2505f;

    static float Hash(uint a) { a ^= a >> 16; a *= 0x7feb352du; a ^= a >> 15; a *= 0x846ca68bu; a ^= a >> 16; return (a & 0xffffff) / 16777216f; }

    // one instance: yaw about y, scale (sxz, sy), tint on the leaves (vertices on the clump / impostor tiles), shade on wood
    static void Put(Model m, int lod, int budget, Vector3 pos, float yaw, float sxz, float sy, Color leafTint, float shade, float sway, float phase)
    {
        var l = m.lods[lod];
        if (inst && m.kind != 2 && lod < 2)
        {
            // instanced (ModelInstancing.cs): the instance is kept, its vertices are not copied; the budgets count the same
            ModelInstancing.Queue(m, lod, pos, yaw, sxz, sy, leafTint, shade, sway, phase);
            used[budget] += l.v.Length; Vertices += l.v.Length;
            return;
        }
        float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
        var b = Room(m.kind, pos.x, pos.z, l.v.Length);
        int o = b.v.Count; float invH = 1f / Mathf.Max(0.01f, m.height);
        for (int i = 0; i < l.v.Length; i++)
        {
            var q = l.v[i]; var n = l.n[i];
            b.v.Add(new Vector3(pos.x + (q.x * c + q.z * s) * sxz, pos.y + q.y * sy, pos.z + (-q.x * s + q.z * c) * sxz));
            b.n.Add(new Vector3(n.x * c + n.z * s, n.y, -n.x * s + n.z * c));
            b.uv.Add(l.uv[i]);
            float hh = Mathf.Clamp01(q.y * invH);
            b.uv2.Add(new Vector2(sway * hh * hh, phase));
            float ao = l.ao[i] / 255f;
            bool wood = IsWood(l.uv[i]);
            b.col.Add(wood ? new Color(ao * shade, ao * shade, ao * shade, 1f) : new Color(ao * leafTint.r, ao * leafTint.g, ao * leafTint.b, 1f));
        }
        for (int i = 0; i < l.tri.Length; i++) b.tri.Add(o + l.tri[i]);
        used[budget] += l.v.Length; Vertices += l.v.Length;
    }


    // a real tree standing at foot (final world height): species mix and size, LOD by distance to the track edge. false =
    // out of budget or out of reach (the caller plants its card tree)
    public static bool TryTree(Vector3 foot, float trackDist, Color leafLin, System.Random rnd)
    {
        if (!active || trackDist > Reach || treeNames == null || treeNames.Length == 0) return false;
        int lod = trackDist <= Lod0Dist ? 0 : trackDist <= Lod1Dist ? 1 : 2;
        // out of budget: step down a LOD (never up)
        var m = park ? PickPark(foot, rnd) : Pick(rnd);
        for (; lod <= 2; lod++)
        {
            if (lod >= m.lods.Length) return false;
            if (used[lod] + m.lods[lod].v.Length <= cap[lod]) break;
        }
        if (lod > 2) return false;
        // a real wood has storeys: about a third are young or understorey trees (7-12 m) under and between the tall ones
        // (14-26 m), so the tree line has a ragged top and depth instead of one height
        float hr = (float)rnd.NextDouble();
        float h = hr < 0.32f ? 7f + 5f * (hr / 0.32f) : 14f + 12f * ((hr - 0.32f) / 0.68f);
        if (park && m.name == "park_plane") h = Mathf.Max(h, 18f + 10f * hr); // the park's planes are the old giants
        else if (park && m.name == "park_pine") h = Mathf.Max(h, 13f + 8f * hr); // pines: never understorey
        if (trackDist < 22f) h = Mathf.Min(h, 20f); // no giant right at the fence
        float sy = h / m.height, sxz = sy * (0.9f + 0.25f * (float)rnd.NextDouble());
        float yaw = (float)rnd.NextDouble() * 6.2832f, phase = (float)rnd.NextDouble() * 6.2832f;
        var tint = LeafTint(m, leafLin, rnd);
        if (park && m.name == "park_pine") tint = new Color(tint.r * 0.78f, tint.g * 0.86f, tint.b * 0.98f, 1f); // Scots pine: darker, bluer needles
        float shade = 0.7f + 0.3f * (float)rnd.NextDouble();
        Put(m, lod, lod, foot, yaw, sxz, sy, tint, shade, 1f, phase);
        TreeFeet.Add(new Vector4(foot.x, foot.y, foot.z, h)); TreeWidth.Add(Mathf.Clamp(2.5f * m.radius * sxz, 0.4f * h, 0.9f * h));
        TreeCount++; LodCount[lod]++;
        return true;
    }

    // weights (the parkland circuits): tree_small_02 35 %, island trees 40 % together, jacaranda 25 %
    static Model Pick(System.Random rnd)
    {
        float r = (float)rnd.NextDouble();
        string n = r < 0.35f ? "tree_small_02" : r < 0.50f ? "island_tree_01" : r < 0.65f ? "island_tree_02" : r < 0.75f ? "island_tree_03" : "jacaranda_tree";
        if (models.TryGetValue(n, out var m)) return m;
        return models[treeNames[rnd.Next(treeNames.Length)]];
    }

    // Monza (the real park, RealWorld): Quercus robur (farnia) dominant, Robinia / ash, London plane, hornbeam-like small
    // trees (tree_small_02), Scots pine. Woods grow in stands: a ~70 m patch has a dominant species that takes about half
    // of its trees, the rest by the park's shares. Five species, as before: the instanced draws stay the same.
    static bool park;
    static readonly string[] ParkNames = { "park_oak", "park_robinia", "park_plane", "tree_small_02", "park_pine" };
    static readonly float[] ParkShare = { 0.36f, 0.24f, 0.16f, 0.16f, 0.08f };
    static int ParkIndex(float r)
    {
        for (int i = 0; i < ParkShare.Length; i++) { r -= ParkShare[i]; if (r < 0f) return i; }
        return 0;
    }
    static Model PickPark(Vector3 foot, System.Random rnd)
    {
        uint cx = (uint)(Mathf.FloorToInt(foot.x / 70f) + 65536), cz = (uint)(Mathf.FloorToInt(foot.z / 70f) + 65536);
        int dom = ParkIndex(Hash(cx * 73856093u ^ cz * 19349663u ^ 0x5bd1e995u));
        float r = (float)rnd.NextDouble();
        int idx = r < 0.5f ? dom : ParkIndex((r - 0.5f) / 0.5f);
        return models.TryGetValue(ParkNames[idx], out var m) ? m : Pick(rnd);
    }

    // the species' leaf colour relative to the colour painted in its clump tiles, with a shade and the usual hue families
    // (olive, yellow-green, dark blue-green, as in a real park)
    static Color LeafTint(Model m, Color leafLin, System.Random rnd)
    {
        float sh = 0.86f + (float)rnd.NextDouble() * 0.28f, h = (float)rnd.NextDouble(), kr = 1, kg = 1, kb = 1;
        if (h < 0.2f) { kr = 1.14f; kg = 0.97f; kb = 0.78f; }
        else if (h < 0.38f) { kr = 1.2f; kg = 1.1f; kb = 0.72f; }
        else if (h < 0.55f) { kr = 0.8f; kg = 0.86f; kb = 1.0f; }
        var p = m.leaf; if (p.g <= 0.005f) p = new Color(0.1f, 0.2f, 0.1f);
        return new Color(Mathf.Clamp(leafLin.r / Mathf.Max(0.004f, p.r), 0.4f, 2.2f) * sh * kr, Mathf.Clamp(leafLin.g / Mathf.Max(0.004f, p.g), 0.4f, 2.2f) * sh * kg,
                         Mathf.Clamp(leafLin.b / Mathf.Max(0.004f, p.b), 0.4f, 2.2f) * sh * kb, 1f);
    }

    // a shrub of the given height (m) with its foot at foot; false = out of budget
    public static bool TryShrub(Vector3 foot, float height, float yaw, Color leafLin, System.Random rnd)
    {
        if (!active || shrubNames == null || shrubNames.Length == 0) return false;
        var m = models[shrubNames[rnd.Next(shrubNames.Length)]];
        if (used[3] + m.lods[0].v.Length > cap[3]) return false;
        float sy = height / m.height, sxz = sy * (0.85f + 0.3f * (float)rnd.NextDouble());
        Put(m, 0, 3, foot, yaw, sxz, sy, LeafTint(m, leafLin, rnd), 1f, 0.5f, (float)rnd.NextDouble() * 6.2832f);
        ShrubCount++;
        return true;
    }

    // a prop (street lamp) of the given height; false = unknown or out of budget
    public static bool AddProp(string name, Vector3 foot, float yaw, float height)
    {
        if (!active || !models.TryGetValue(name, out var m) || m.kind != 2) return false;
        if (used[4] + m.lods[0].v.Length > cap[4]) return false;
        float s = height / m.height;
        Put(m, 0, 4, foot, yaw, s, s, Color.white, 1f, 0f, 0f);
        PropCount++;
        return true;
    }

    // ---------------------------------------------------------------- meshes
    // Once per build, after the last instance and before the light bake. The meshes are named "Env..." (WorldBuilder.Drape
    // leaves them alone: they are at their final height and keep their bent normals).
    public static void Flush(Transform parent)
    {
        if (!active || (bufs.Count == 0 && ModelInstancing.QueueCount == 0)) { bufs.Clear(); ModelInstancing.ResetQueue(); return; }
        Material fm = Environment.FoliageMaterial(); fm.mainTexture = atlas; fm.name = "ModelFoliage";
        Material pm = WorldBuilder.Mat(Color.white, atlas, 0.1f); pm.name = "ModelProps";
        var holder = new GameObject("Models").transform; holder.SetParent(parent, false);
        ModelInstancing.Flush(holder, fm, Lod0Dist, Lod1Dist, ModelsByKind(0), ModelsByKind(1));
        foreach (var kv in bufs)
        {
            int kind = (int)(kv.Key >> 40);
            foreach (var b in kv.Value)
            {
                if (b.tri.Count == 0) continue;
                var mesh = new Mesh { name = kind == 2 ? "ModelProps" : "ModelTrees" };
                mesh.indexFormat = b.v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(b.v); mesh.SetNormals(b.n); mesh.SetUVs(0, b.uv);
                if (kind != 2) mesh.SetUVs(2, b.uv2); // (channel 2: static batching may use channel 1 for lightmaps)
                mesh.SetTriangles(b.tri, 0, true);
                var go = new GameObject(kind == 2 ? "EnvModelProps" : "EnvModelTrees"); go.transform.SetParent(holder, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = kind == 2 ? pm : fm;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = true;
                BakeTint.Add(mesh, b.col.ToArray());
                DrawMeshes++;
            }
        }
        bufs.Clear();
        Debug.Log("ModelLib: " + (inst ? "instanced " + ModelInstancing.LastCount + " (" + ModelInstancing.LastMeshes + " shared meshes), " : "") + TreeCount + " trees (LOD0 " + LodCount[0] + ", LOD1 " + LodCount[1] + ", impostor " + LodCount[2] + "), " + ShrubCount + " shrubs, " + PropCount + " props, "
                  + Vertices + " vertices (trees " + (used[0] + used[1] + used[2]) + ", shrubs " + used[3] + ", props " + used[4] + "), " + DrawMeshes + " meshes");
    }

    // the loaded models of one kind (0 trees, 1 shrubs), in name order (stable species indices for ModelInstancing)
    static Model[] ModelsByKind(int kind)
    {
        var names = kind == 0 ? treeNames : shrubNames; if (names == null) return new Model[0];
        var r = new Model[names.Length]; for (int i = 0; i < names.Length; i++) r[i] = models[names[i]];
        return r;
    }

    // shrubs along the fences and lamps in the paddock (Scenery.cs): once per build, while the ground is known
    public static void PlaceProps(Track t)
    {
        if (!active || propsDone) return;
        propsDone = true;
        Scenery.ModelProps(t);
    }
}
