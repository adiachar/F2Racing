// WorldBuilder.cs — builds the circuit and the playground pad from code (no scene editing needed).
// Physics (x, y) maps to Unity (x, height, y). Track left normal (nx, ny) -> Unity (nx, 0, ny).
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public static class WorldBuilder
{
    public const float Kerb = 1.4f;
    // playground: 800 x 480 m pad south of the circuit (same place as the web version)
    public static Rect Pad = new Rect(250 - 400, -560 - 240, 800, 480); // placed next to each circuit in Build
    public static Vector2 PadStart => new Vector2(Pad.xMin + 40, Pad.center.y);

    static Material baseMat;
    // true when Road.shader draws the road (asphalt, rubber, lines, kerbs, grid); Scenery.Rubber then adds no overlay
    public static bool RoadShaded;
    // graphics level the world is built for: 1 Low, 2 Medium, 3 High (GameManager's "gfx" setting; Auto = Medium,
    // or Low once Auto has stepped down to it). Set in Build from LiveGfx.
    public static int Gfx = 2;
    // The level the game is running at now. GameManager.SetGraphics (start, every settings change, Auto's step down)
    // leaves a signature no Unity default quality level has: shadowDistance 55 (Low/Medium) or 90 (High), anisotropic
    // filtering Disable on Low and ForceEnable on Medium/High. That live state wins over PlayerPrefs "gfx", which
    // StartSession only saves after it has built the world (so a level just changed in the menu is not there yet).
    // Before the first SetGraphics (GameManager.Start's own build) the pref is still what Start loaded, so it is used
    // (DebugShots writes the shot level into it first). Auto (0) without the signature = Medium.
    public static int LiveGfx
    {
        get
        {
            float sd = QualitySettings.shadowDistance;
            if (sd == 55f || sd == 90f)
            {
                var af = QualitySettings.anisotropicFiltering;
                if (af == AnisotropicFiltering.Disable) return 1;
                if (af == AnisotropicFiltering.ForceEnable) return sd == 90f ? 3 : 2;
            }
            int s = PlayerPrefs.GetInt("gfx", 0);
            return s == 0 ? 2 : Mathf.Clamp(s, 1, 3);
        }
    }
    // the world on screen was built for another level than the game now runs at (GameManager.LoadMap can rebuild on it)
    public static bool Stale => LiveGfx != Gfx;
    // the baked vertex lighting will run (same test as LightBake.Begin): vertex-colour tints and the fence shader work
    public static bool BakeReady;
    // ---------------------------------------------------------------- hills
    // Height of the ground at a point (0 everywhere on a flat circuit). The road, scenery and cars all use it.
    public static HeightField Height;
    public static float H(double x, double y) => Height == null ? 0f : (float)Height.Sample(x, y);
    // Bridges (HeightField: one leg over another, Suzuka). H is the top surface; where the two legs overlap, things
    // that belong to one of them take its own level: HOn for something built at track point i, HAlong for something
    // facing along (dx, dy) (a car). Without bridges both are H.
    public static float HOn(int i, double x, double y) => Height == null ? 0f : (float)Height.SampleOn(x, y, i);
    public static float HAlong(double x, double y, double dx, double dy) => Height == null ? 0f : (float)Height.SampleAlong(x, y, dx, dy);
    // under (or on) a bridge deck: trackside dressing keeps out (the bridge has its own parapets)
    public static bool UnderBridge(float x, float z) => Height != null && Height.HasBridges && Height.Sample(x, z) - Height.Ground(x, z) > 1.0;
    // what Drape's H misses for a vertex of a track-following mesh built at track point i (0 away from bridges)
    static float Dk(int i, double x, double y) => Height == null || !Height.HasBridges ? 0f : (float)(Height.SampleOn(x, y, i) - Height.Sample(x, y));

    // Lay everything under "root" onto the hills: meshes built in world coordinates get every vertex lifted,
    // placed objects (trees, cones) are lifted as a whole.
    public static void Drape(GameObject root)
    {
        if (Height == null) return;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            var tr = mf.transform;
            if (mf.name == "Terrain" || mf.name == "Harbour" || mf.name.StartsWith("Env")) continue; // built at their final height already
            if (tr.localToWorldMatrix.isIdentity)
            {
                var mesh = mf.sharedMesh; if (mesh == null) continue;
                var v = mesh.vertices;
                for (int i = 0; i < v.Length; i++) v[i].y += H(v[i].x, v[i].z);
                mesh.vertices = v; if (mf.name != "Trees") mesh.RecalculateNormals(); mesh.RecalculateBounds(); // (tree cards carry hand-made normals)
            }
            else { var p = tr.position; p.y += H(p.x, p.z); tr.position = p; }
        }
    }

    static Material diffuseMat; static bool diffuseTried;

    public static Material Mat(Color c, Texture2D tex = null, float smooth = 0.1f, float metal = 0f)
    {
        if (baseMat == null) baseMat = Resources.Load<Material>("Base");
        // matt surfaces (road, grass, walls, buildings) use the simple diffuse shader: it looks the same there and
        // costs a fraction of the full shader on a phone. Shiny things (car paint, metal) keep the full one.
        if (!diffuseTried) { diffuseTried = true; diffuseMat = Resources.Load<Material>("Diffuse"); }
        if (diffuseMat != null && smooth <= 0.3f && metal <= 0f)
        {
            var d = new Material(diffuseMat) { color = c };
            if (tex != null) d.mainTexture = tex;
            return d;
        }
        var m = new Material(baseMat) { color = c };
        if (tex != null) m.mainTexture = tex;
        m.SetFloat("_Glossiness", smooth);
        m.SetFloat("_Metallic", metal);
        return m;
    }

    // self-lit material (brake lights, start lights); falls back to a plain one if the build has no Glow material
    static Material glowMat;
    public static Material Glow(Color c, Color emission)
    {
        if (glowMat == null) glowMat = Resources.Load<Material>("Glow");
        if (glowMat == null) return Mat(c, null, 0.9f);
        var m = new Material(glowMat) { color = c };
        m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", emission);
        return m;
    }

    // ---------------------------------------------------------------- procedural textures
    static float Hash(int x, int y, int s)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + s * 2147483;
            h = (h ^ (int)((uint)h >> 13)) * 1274126177;
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296f;
        }
    }
    static float Fbm(float u, float v, int cells, int oct, int s)
    {
        float sum = 0, amp = 1, norm = 0; int c = cells;
        for (int o = 0; o < oct; o++)
        {
            float fx = u * c, fy = v * c; int i = Mathf.FloorToInt(fx), j = Mathf.FloorToInt(fy);
            float a = fx - i, b = fy - j; a = a * a * (3 - 2 * a); b = b * b * (3 - 2 * b);
            int i0 = ((i % c) + c) % c, j0 = ((j % c) + c) % c, i1 = (i0 + 1) % c, j1 = (j0 + 1) % c;
            float h00 = Hash(i0, j0, s + o), h10 = Hash(i1, j0, s + o), h01 = Hash(i0, j1, s + o), h11 = Hash(i1, j1, s + o);
            sum += amp * Mathf.Lerp(Mathf.Lerp(h00, h10, a), Mathf.Lerp(h01, h11, a), b);
            norm += amp; amp *= 0.5f; c *= 2;
        }
        return sum / norm;
    }
    public static Texture2D Tex(int w, int h, System.Func<float, float, int, int, Color32> f)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = f((float)x / w, (float)y / h, x, y);
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }
    static byte B(float v) => (byte)Mathf.Clamp(v, 0, 255);

    // tyre: dark rubber, slightly lighter worn tread, yellow sidewall band (generic compound marking)
    public static Texture2D TyreTex() => Tex(64, 4, (u, v, x, y) =>
    {
        bool band = (u > 0.07f && u < 0.13f) || (u > 0.87f && u < 0.93f);
        bool tread = u > 0.3f && u < 0.7f;
        return band ? new Color32(235, 200, 40, 255) : tread ? new Color32(30, 30, 32, 255) : new Color32(16, 16, 18, 255);
    });

    static Material shadowMat;
    public static Material ShadowMat()
    {
        if (shadowMat != null) return shadowMat;
        shadowMat = new Material(Resources.Load<Material>("Shadow"));
        shadowMat.mainTexture = Tex(64, 128, (u, v, x, y) =>
        {
            float dx = (u - 0.5f) * 2, dy = (v - 0.5f) * 2, d = Mathf.Sqrt(dx * dx * 1.1f + dy * dy * 0.9f);
            float a = Mathf.Clamp01(1 - d); a = a * a * (3 - 2 * a);
            return new Color32(0, 0, 0, (byte)(a * 200));
        });
        shadowMat.mainTexture.wrapMode = TextureWrapMode.Clamp;
        return shadowMat;
    }

    public static Texture2D AsphaltTex() => Tex(256, 256, (u, v, x, y) =>
    {
        float c = 54 + Fbm(u, v, 8, 3, 21) * 16 + Fbm(u, v, 64, 2, 5) * 18;
        float r = Hash(x, y, 77);
        if (r > 0.985f) c += 55; else if (r > 0.95f) c += 22; else if (r < 0.04f) c -= 18;
        return new Color32(B(c), B(c + 1), B(c + 4), 255);
    });
    // Road.shader's asphalt, four grey layers in one tiling texture (all tile seamlessly):
    //  R = aggregate grain (fine stones, pores), read at 5 m per tile
    //  G = metre-scale blotches, B = soft resurfaced patches (read again at ~43 m per tile, to hide the repeat)
    //  A = bright stones that catch the sun
    public static Texture2D RoadTex() => Tex(256, 256, (u, v, x, y) =>
    {
        float f = Fbm(u, v, 64, 2, 5), h = Hash(x, y, 77), h2 = Hash(x >> 1, y >> 1, 91);
        float g = 0.5f + (f - 0.5f) * 0.9f, stone = 0;
        if (h2 > 0.93f) { g += 0.22f + 0.1f * h; stone = 0.6f + 0.4f * h; }
        else if (h > 0.985f) { g += 0.3f; stone = 1; }
        else if (h < 0.05f) g -= 0.22f;
        float blot = 0.5f + (Fbm(u, v, 4, 4, 21) - 0.5f) * 2.4f;
        float pf = Fbm(u, v, 2, 3, 33), patch = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.55f, 0.62f, pf));
        return new Color32(B(g * 255), B(blot * 255), B(patch * 255), B(stone * 255));
    });
    // grass (or whatever covers the ground: dry grass, sand, paving) in the circuit's ground colour (Environment.cs)
    public static Texture2D GrassTex() => GrassTex(new Color(62 / 255f, 112 / 255f, 44 / 255f));
    public static Texture2D GrassTex(Color c) => Tex(256, 256, (u, v, x, y) =>
    {
        float r = Hash(x, y, 31);
        float k = 0.75f + Fbm(u, v, 6, 4, 9) * 0.35f + Fbm(u, v, 64, 2, 13) * 0.2f + (r > 0.9f ? 0.12f : r < 0.08f ? -0.12f : 0);
        return new Color32(B(c.r * 255 * k), B(c.g * 255 * k), B(c.b * 255 * k), 255);
    });
    // (astroturf, gravel and the other scenery surfaces: EnvTex.cs)
    static Texture2D KerbTex() => Tex(32, 64, (u, v, x, y) =>
    {
        bool red = v < 0.5f; float edge = Mathf.Min(u, 1 - u), lip = Mathf.Clamp(edge * 9, 0.55f, 1);
        float k = lip * (0.85f + Fbm(u, v, 8, 2, 7) * 0.2f);
        return red ? new Color32(B(205 * k), B(32 * k), B(38 * k), 255) : new Color32(B(238 * k), B(238 * k), B(234 * k), 255);
    });

    // ---------------------------------------------------------------- meshes
    static Vector3 W(double x, double y, float h = 0) => new Vector3((float)x, h, (float)y);
    // the same for a vertex of the mesh along track point i (on its own leg's level where two legs cross: Dk)
    static Vector3 WT(int i, double x, double y, float h) => new Vector3((float)x, h + Dk(i, x, y), (float)y);

    // strip along the track between lateral offsets a and b (+ = left), i0..i1 inclusive
    static void Strip(Track t, int i0, int i1, float a, float b, float h, float vScale, List<Vector3> v, List<Vector2> uv, List<int> tri, float hOuter = float.NaN)
    {
        float left = Mathf.Max(a, b), right = Mathf.Min(a, b);
        float hl = a >= b ? h : (float.IsNaN(hOuter) ? h : hOuter), hr = a >= b ? (float.IsNaN(hOuter) ? h : hOuter) : h;
        int start = v.Count;
        for (int i = i0; i <= i1; i++)
        {
            var p = t.pts[i % t.N];
            v.Add(WT(i, p.x + p.nx * left, p.y + p.ny * left, hl));
            v.Add(WT(i, p.x + p.nx * right, p.y + p.ny * right, hr));
            float vv = (float)(i * t.ds) / vScale; uv.Add(new Vector2(0, vv)); uv.Add(new Vector2(1, vv));
            if (i < i1)
            {
                int k = start + (i - i0) * 2; // k = left, k+1 = right, k+2 = next left, k+3 = next right
                tri.Add(k); tri.Add(k + 2); tri.Add(k + 1);
                tri.Add(k + 2); tri.Add(k + 3); tri.Add(k + 1);
            }
        }
    }
    public static GameObject MeshObj(string name, List<Vector3> v, List<Vector2> uv, List<int> tri, Material m, bool shadows = true, List<Vector2> uv2 = null)
    {
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(v); mesh.SetUVs(0, uv); if (uv2 != null) mesh.SetUVs(2, uv2); // (channel 2: static batching may treat channel 1 as lightmap UVs)
        mesh.SetTriangles(tri, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        var go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = shadows;
        return go;
    }

    // Dirt at the road's edges, per track point: (left, right) 0..1 (Road.shader uv3). Stones and dust thrown onto the
    // asphalt and the kerbs beside the gravel traps (strongest where a trap ends: cars rejoining drag it back on), and
    // grass dirt and dust dragged on along the outside of the corner exits, where cars run wide; a faint base elsewhere
    // that drifts along the lap, so no stretch of edge is perfectly clean.
    static Vector2[] EdgeDirt(Track t)
    {
        int N = t.N; float ds = (float)t.ds;
        var outp = new Vector2[N];
        var src = new float[2, N];
        bool gravel = t.runL != null && t.runR != null;
        for (int i = 0; i < N; i++)
        {
            if (gravel)
            {
                src[0, i] = t.runL[i] == SurfaceType.Gravel ? 0.75f : 0f;
                src[1, i] = t.runR[i] == SurfaceType.Gravel ? 0.75f : 0f;
            }
            // the outside of a corner (left-hand bend, curv > 0: the outside is on the right)
            float k = (float)t.pts[i].curv, w = Mathf.Clamp01((Mathf.Abs(k) - 1f / 400f) * 90f) * 0.55f;
            int o = k > 0 ? 1 : 0;
            if (w <= 0f) continue;
            int after = Mathf.RoundToInt(70f / ds);
            for (int j = Mathf.RoundToInt(10f / ds); j <= after; j++)
            {
                int q = (i + j) % N; float f = w * (1f - (float)j / (after + 1));
                if (f > src[o, q]) src[o, q] = f;
            }
        }
        // spread along the lap (gravel: ~25 m either way, a little longer downstream where it is dragged back on)
        int back = Mathf.Max(1, Mathf.RoundToInt(18f / ds)), fwd = Mathf.Max(1, Mathf.RoundToInt(30f / ds));
        for (int s = 0; s < 2; s++)
            for (int i = 0; i < N; i++)
            {
                float m = 0f;
                for (int j = -fwd; j <= back; j++)
                {
                    float sv = src[s, ((i + j) % N + N) % N]; if (sv <= m) continue;
                    float f = j < 0 ? 1f + (float)j / (fwd + 1) : 1f - (float)j / (back + 1);
                    m = Mathf.Max(m, sv * f);
                }
                float b = 0.1f + 0.08f * Mathf.Sin(i * ds * 0.011f + s * 2.1f) + 0.05f * Mathf.Sin(i * ds * 0.037f + s * 5.3f);
                if (s == 0) outp[i].x = Mathf.Clamp01(Mathf.Max(m, b)); else outp[i].y = Mathf.Clamp01(Mathf.Max(m, b));
            }
        return outp;
    }

    // Kerb and run-off styles per side (0 = left, 1 = right) and track point, from the corner each point belongs to (the
    // tightest point within 40 m): style 0 flat (gentle bends: low, slight bevel), 1 rounded corner kerb, 2 stepped
    // (outside of medium corners, R 45-250 m: Lesmo, Parabolica, Ascari exits); wear 0..1 (how much tyres ride it:
    // apexes and exits); exit 0..1 (outside of a corner exit: saw-tooth ridges on the kerb, tyre marks over the
    // run-off); sausage (inside of the chicanes, R < 45 m, near the apex: yellow sausage kerb behind the kerb); paint
    // (outside of corners: the 2 m strip behind the kerb is green / blue painted asphalt, not astroturf). Visual only:
    // the physical widths (Track.SurfaceAt) are unchanged.
    struct KerbInfo { public int style; public float wear, exit; public bool sausage, paint; }
    static KerbInfo[,] KerbLayout(Track t)
    {
        int N = t.N; float ds = (float)t.ds; int W = Mathf.Max(1, Mathf.RoundToInt(40f / ds));
        var info = new KerbInfo[2, N];
        bool park = t.def != null && t.def.style == CircuitStyle.Parkland;
        for (int i = 0; i < N; i++)
        {
            int best = 0; double bc = 0;
            for (int k = -W; k <= W; k++)
            {
                double c = System.Math.Abs(t.pts[((i + k) % N + N) % N].curv);
                if (c > bc + 1e-9 || (c > bc - 1e-9 && System.Math.Abs(k) < System.Math.Abs(best))) { bc = c; best = k; }
            }
            int apex = ((i + best) % N + N) % N;
            float R = bc > 1e-6 ? (float)(1.0 / bc) : 1e6f, after = -best * ds;
            int insideS = t.pts[apex].curv > 0 ? 0 : 1; // left-hand bend: the inside is on the left
            for (int s = 0; s < 2; s++)
            {
                var ki = new KerbInfo();
                if (R > 250f) { ki.style = 0; ki.wear = 0.2f; }
                else if (s == insideS)
                {
                    ki.style = 1; ki.wear = Mathf.Lerp(0.95f, 0.45f, Mathf.Clamp01(Mathf.Abs(after) / 40f));
                    ki.sausage = park && R < 45f && Mathf.Abs(after) < 20f;
                }
                else
                {
                    ki.exit = after > 0f ? Mathf.Clamp01(after / 8f) * Mathf.Clamp01((40f - after) / 15f) : 0f;
                    ki.style = R >= 45f && after > -6f ? 2 : 1;
                    ki.wear = 0.3f + 0.6f * ki.exit;
                    ki.paint = park && after > -15f;
                }
                info[s, i] = ki;
            }
        }
        return info;
    }

    const float SeaBed = -4f;
    // Ground.shader / Foliage.shader draw the land and the trees (need the baked lighting, like Road.shader)
    public static bool GroundShaded; public static Shader FoliageShader;

    // The verges: a band of ground each side of the track, from behind the kerb / astroturf / gravel out past the
    // barrier, on the ground material (so it meets the terrain without a seam) but with uv2 = (metres from the
    // centreline, metres along) for the mowing stripes and the worn strip at the barrier. Columns at the barrier line
    // let the bake shade its foot. Cut back wherever it would reach another part of the circuit or fold inside a bend.
    static void BuildVerge(Track t, Material grass, float wallLat, Transform root)
    {
        float Wd = (float)t.width; var env = Environment.Cur;
        bool park = t.def != null && t.def.style == CircuitStyle.Parkland;
        float gOut = park ? Wd / 2 + (float)t.def.barrier - 0.4f : 0f;
        float outerMax = Mathf.Min(wallLat + 16f, Wd / 2 + Mathf.Max(env.flatTo - 2f, 12f));
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var uv2 = new List<Vector2>(); var tri = new List<int>(); var tint = new List<Color>();
        const int K = 5; var lat = new float[K];
        int step = Mathf.Max(1, Mathf.RoundToInt(2f / (float)t.ds)), rows = (t.N + step - 1) / step; // a row every ~2 m (4 m showed straight segments in tight bends)
        for (int side = 1; side >= -1; side -= 2)
        {
            int start = v.Count;
            for (int r = 0; r <= rows; r++)
            {
                int i = r == rows ? t.N : r * step;
                int ii = i % t.N; var p = t.pts[ii];
                float inner = Wd / 2 + (side > 0 ? t.kerbL[ii] : t.kerbR[ii]) + (park ? 2f : 0f);
                if (park && (side > 0 ? t.runL[ii] : t.runR[ii]) == SurfaceType.Gravel) inner = Mathf.Max(inner, gOut);
                inner -= 0.05f; // tucked under the strip in front
                float outer = outerMax;
                for (int k = 0; k < 5; k++)
                {
                    double ox = p.x + p.nx * outer * side, oy = p.y + p.ny * outer * side;
                    double mx = p.x + p.nx * (inner + outer) * 0.5 * side, my = p.y + p.ny * (inner + outer) * 0.5 * side;
                    if (t.Nearest(ox, oy).dist > outer - 0.6 && t.Nearest(mx, my).dist > (inner + outer) * 0.5 - 0.6) break;
                    outer = inner + (outer - inner) * 0.55f;
                }
                outer = Mathf.Max(outer, inner + 0.3f);
                lat[0] = inner; lat[1] = (inner + wallLat - 1.2f) * 0.5f; lat[2] = wallLat - 1.2f; lat[3] = wallLat + 0.9f; lat[4] = outer;
                for (int c = 1; c < K; c++) lat[c] = Mathf.Clamp(lat[c], lat[c - 1] + 0.02f, Mathf.Max(outer, lat[c - 1] + 0.02f));
                // on a bridge deck the verge ends at the parapet
                if (Height != null && Height.OnDeck(ii)) { float dw = (float)Height.DeckHalf - 0.3f; for (int c = 0; c < K; c++) lat[c] = Mathf.Min(lat[c], dw - (K - 1 - c) * 0.02f); }
                float along = (float)(i * t.ds);
                for (int c = 0; c < K; c++)
                {
                    float x = (float)(p.x + p.nx * lat[c] * side), z = (float)(p.y + p.ny * lat[c] * side);
                    v.Add(new Vector3(x, 0.012f + Dk(ii, x, z), z)); uv.Add(new Vector2(x, z)); uv2.Add(new Vector2(lat[c], along));
                    tint.Add(Environment.GroundTint(x, z, Environment.Lift(x, z)));
                }
                if (r < rows)
                    for (int c = 0; c < K - 1; c++)
                    {
                        int a = start + r * K + c, b = a + K; // a: this row, b: next row; a + 1 lies further out
                        // keep the faces pointing up whichever side of the track this is (the left normal is +lateral)
                        if (side > 0) { tri.Add(a + 1); tri.Add(b + 1); tri.Add(a); tri.Add(b + 1); tri.Add(b); tri.Add(a); }
                        else { tri.Add(a); tri.Add(b); tri.Add(a + 1); tri.Add(b); tri.Add(b + 1); tri.Add(a + 1); }
                    }
            }
        }
        var go = MeshObj("Verge", v, uv, tri, grass, true, uv2); go.transform.SetParent(root);
        BakeTint.Add(go.GetComponent<MeshFilter>().sharedMesh, tint.ToArray());
    }
    static bool Street(Track t) => t.def != null ? t.def.style == CircuitStyle.Street : t.width < 10.0;

    // Bridges (HeightField.OnDeck: Suzuka's upper leg over the lower one): a concrete slab under the deck, as wide as
    // the barriers, with a parapet along each edge at the barrier line, and its ends buried in the embankments.
    // Built at its final height (the vertices hold that minus H, which Drape adds back).
    static void BuildBridges(Track t, Transform root)
    {
        if (Height == null || !Height.HasBridges) return;
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
        float wl = (float)Traffic.WallOffset(t), wo = wl + 0.45f, top = 1.05f, under = -1.5f;
        // the cross-section, left (+) to right: parapet inner face, its top, the slab's side, its underside, and back up
        var sec = new[] { new Vector2(wl, -0.05f), new Vector2(wl, top), new Vector2(wo, top), new Vector2(wo, under),
                          new Vector2(-wo, under), new Vector2(-wo, top), new Vector2(-wl, top), new Vector2(-wl, -0.05f) };
        int N = t.N, ext = Mathf.Max(1, Mathf.RoundToInt(6f / (float)t.ds));
        for (int a = 0; a < N; a++)
        {
            if (!Height.OnDeck(a) || Height.OnDeck(a - 1)) continue;
            int b = a; while (b - a < N && Height.OnDeck(b + 1)) b++;
            int i0 = a - ext, i1 = b + ext; // (a little into the embankments)
            Vector3 Pt(int i, Vector2 q)
            {
                var p = t.pts[((i % N) + N) % N]; double x = p.x + p.nx * q.x, y = p.y + p.ny * q.x;
                int on = Mathf.Clamp(i, a, b); // (beyond the deck's ends: its end plane)
                return new Vector3((float)x, (float)Height.SampleOn(x, y, on) + q.y - H(x, y), (float)y);
            }
            for (int c = 0; c + 1 < sec.Length; c++)
                for (int i = i0; i < i1; i++)
                {
                    Vector3 p00 = Pt(i, sec[c]), p01 = Pt(i, sec[c + 1]), p10 = Pt(i + 1, sec[c]), p11 = Pt(i + 1, sec[c + 1]);
                    float u0 = (float)(i * t.ds) / 4f, u1 = (float)((i + 1) * t.ds) / 4f, w0 = (sec[c].x + sec[c].y) / 4f, w1 = (sec[c + 1].x + sec[c + 1].y) / 4f;
                    // both faces (a few hundred triangles): seen from the road above and from the road below
                    for (int f = 0; f < 2; f++)
                    {
                        int k = v.Count;
                        v.Add(p00); v.Add(p01); v.Add(p10); v.Add(p11);
                        uv.Add(new Vector2(u0, w0)); uv.Add(new Vector2(u0, w1)); uv.Add(new Vector2(u1, w0)); uv.Add(new Vector2(u1, w1));
                        if (f == 0) { tri.Add(k); tri.Add(k + 1); tri.Add(k + 2); tri.Add(k + 2); tri.Add(k + 1); tri.Add(k + 3); }
                        else { tri.Add(k); tri.Add(k + 2); tri.Add(k + 1); tri.Add(k + 2); tri.Add(k + 3); tri.Add(k + 1); }
                    }
                }
            a = b;
        }
        if (v.Count > 0) MeshObj("Bridge", v, uv, tri, Mat(new Color(0.80f, 0.79f, 0.76f), EnvTex.Stone(), 0.1f)).transform.SetParent(root);
    }

    public static GameObject Build(Track t)
    {
        var root = new GameObject("World").transform;
        Gfx = LiveGfx; // the level actually chosen now, not the last saved one
        Debug.Log("WorldBuilder: building " + (t.def != null ? t.def.id : "?") + " for graphics level " + Gfx); // (once per build; the shooter's console.log shows it)
        { var bs = Resources.Load<Shader>("Baked"); BakeReady = bs != null && bs.isSupported; }
        BakeTint.Clear();
        float Wd = (float)t.width;
        var bb = t.Bounds();
        float cx = (float)(bb.minX + bb.maxX) / 2, cy = (float)(bb.minY + bb.maxY) / 2;
        float ext = (float)System.Math.Max(bb.maxX - bb.minX, bb.maxY - bb.minY);
        // test-drive pad sits just south of whichever circuit is loaded
        Pad = new Rect(cx - 400, (float)bb.minY - 300 - 480, 800, 480);

        // sky, light, fog: procedural skybox (sun, atmosphere) tuned per circuit, fog matched to its horizon (Sky.cs)
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        // (afternoon values; Sky.Setup sets the sun and the sky / equator ambient per time of day, matched to the bake)
        RenderSettings.ambientSkyColor = new Color(0.69f, 0.76f, 0.86f);
        RenderSettings.ambientEquatorColor = new Color(0.62f, 0.64f, 0.64f);
        Environment.Prepare(t); var env = Environment.Cur; // the look of the land round this circuit (Environment.cs)
        RenderSettings.ambientGroundColor = Color.Lerp(new Color(0.3f, 0.35f, 0.25f), env.grass, 0.5f);
        var sunGo = new GameObject("Sun"); sunGo.transform.SetParent(root);
        var sun = sunGo.AddComponent<Light>(); sun.type = LightType.Directional; sun.color = new Color(1f, 0.95f, 0.86f); sun.intensity = 1.45f;
        sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.8f; sun.shadowBias = 0.03f; sun.shadowNormalBias = 0.3f;
        sunGo.transform.rotation = Quaternion.Euler(48, -35, 0);
        var skyLook = Sky.Setup(t, Street(t), sun);

        // ground: Ground.shader (world-space detail + a macro layer, mowing stripes, barrier wear, gravel ruts) on the
        // baked light; uv0 is the world position in metres everywhere, so the open land, the verges and the far sheet
        // share one material and meet without a seam. Without the baked shaders: the plain diffuse look as before.
        var groundSh = Resources.Load<Shader>("Ground"); var foliageSh = Resources.Load<Shader>("Foliage");
        GroundShaded = BakeReady && groundSh != null && groundSh.isSupported;
        FoliageShader = BakeReady && foliageSh != null && foliageSh.isSupported ? foliageSh : null;
        float wallLat = (float)Traffic.WallOffset(t);
        bool green = EnvTex.IsGreen(env.grass);
        // (a touch brighter and warmer than the palette: lawns in full sun read light and yellow-green, Real Racing style)
        var grassTex = EnvTex.GroundDetail(green ? new Color(env.grass.r * 1.12f, env.grass.g * 1.08f, env.grass.b * 0.95f).linear : env.grass.linear);
        var grass = GroundShaded ? new Material(groundSh) { mainTexture = grassTex } : Mat(Color.white, grassTex);
        float gsize = Mathf.Max(4400, ext + 3000);
        grass.mainTextureScale = new Vector2(1f / 4.5f, 1f / 4.5f);
        if (GroundShaded)
        {
            grass.SetTexture("_MacroTex", EnvTex.Macro());
            // (vectors, not colours: multipliers above 1 must not go through the sRGB -> linear conversion)
            grass.SetVector("_DryTint", green ? new Vector4(1.22f, 1.10f, 0.70f, 0.38f) : new Vector4(1.08f, 1.04f, 0.95f, 0.25f));
            float stripeFrom = Wd / 2 + Kerb + (t.def != null && t.def.style == CircuitStyle.Parkland ? 2f : 0f);
            grass.SetVector("_Stripe", new Vector4(stripeFrom, wallLat + 7f, 3.2f, green && !Street(t) ? 0.16f : 0f));
            grass.SetVector("_Wall", new Vector4(wallLat, 0.16f, 0, 0));
            grass.SetFloat("_Clumps", green ? 1f : 0f); // (EnvTex.GroundDetail's alpha: clumps and patches)
        }
        Height = t.h != null || t.bank != null ? new HeightField(t) : null; // banked corners tilt the road (HeightField)
        Waterfront.Prepare(t);
        // the real surroundings (Resources/World/<Name>.bytes: terrain, land use, buildings, roads, trees), if this
        // circuit has them; they then replace the fake hills, town rows and generic trees (RealWorld.cs)
        RealWorld.Load(t);
        if (RealWorld.Active)
        {
            // the flat sheet only round the real terrain (a frame), just below the level of its edge
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            var rg = RealWorld.Region; float yy = RealWorld.SheetLevel, o = 2f;
            float ox0 = Mathf.Min(cx - gsize / 2, rg.xMin - 1500), ox1 = Mathf.Max(cx + gsize / 2, rg.xMax + 1500);
            float oz0 = Mathf.Min(cy - 400 - gsize / 2, rg.yMin - 1500), oz1 = Mathf.Max(cy - 400 + gsize / 2, rg.yMax + 1500);
            void Sheet(float x0, float z0, float x1, float z1)
            {
                if (x1 <= x0 || z1 <= z0) return;
                int k = v.Count;
                v.Add(new Vector3(x0, yy, z0)); v.Add(new Vector3(x1, yy, z0)); v.Add(new Vector3(x0, yy, z1)); v.Add(new Vector3(x1, yy, z1));
                uv.Add(new Vector2(x0, z0)); uv.Add(new Vector2(x1, z0)); uv.Add(new Vector2(x0, z1)); uv.Add(new Vector2(x1, z1));
                tri.Add(k); tri.Add(k + 2); tri.Add(k + 1); tri.Add(k + 1); tri.Add(k + 2); tri.Add(k + 3);
            }
            Sheet(ox0, oz0, ox1, rg.yMin + o); Sheet(ox0, rg.yMax - o, ox1, oz1);
            Sheet(ox0, rg.yMin + o, rg.xMin + o, rg.yMax - o); Sheet(rg.xMax - o, rg.yMin + o, ox1, rg.yMax - o);
            MeshObj("EnvGroundFrame", v, uv, tri, grass).transform.SetParent(root); // ("Env": built at its final height, not draped)
            if (Waterfront.HasWater) Waterfront.BuildWater(root, 1.0f);
        }
        else
        {
            // ground: one big flat sheet; the fine grid round the track that follows the hills is made further down
            // (BuildTerrain), once the trackside buildings are known
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            float gx0 = cx - gsize / 2, gy0 = cy - 400 - gsize / 2, yy = Waterfront.HasWater ? SeaBed : Height == null ? -0.05f : -0.35f - (float)Height.bankDrop;
            for (int j = 0; j <= 1; j++) for (int i = 0; i <= 1; i++)
            {
                float px = gx0 + gsize * i, py = gy0 + gsize * j;
                v.Add(new Vector3(px, yy, py)); uv.Add(new Vector2(px, py));
            }
            tri.Add(0); tri.Add(2); tri.Add(1); tri.Add(1); tri.Add(2); tri.Add(3);
            MeshObj("Ground", v, uv, tri, grass).transform.SetParent(root);
            if (Waterfront.HasWater) Waterfront.BuildWater(root, 1.0f); // about 3 m below the quay
        }
        // the land round the track: a grid that follows the circuit's hills (never above a road) plus the environment's
        // own relief away from the track (Environment.Lift), coloured in patches through the vertex tint
        void BuildTerrain()
        {
            if (RealWorld.Active) { RealWorld.Prepare(t); return; } // real terrain heights (meshes made after Drape)
            if (Height == null) return;
            float m = 400, w = (float)(bb.maxX - bb.minX) + 2 * m, hg = (float)(bb.maxY - bb.minY) + 2 * m, cell = Mathf.Max(Gfx <= 1 ? 12f : 8f, ext / 180f);
            float x0 = (float)bb.minX - m, y0 = (float)bb.minY - m; int nx = Mathf.CeilToInt(w / cell), ny = Mathf.CeilToInt(hg / cell);
            Environment.BuildField(t, x0, y0, w, hg);
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>(); var tint = new List<Color>();
            for (int j = 0; j <= ny; j++) for (int i = 0; i <= nx; i++)
            {
                float px = x0 + w * i / nx, py = y0 + hg * j / ny;
                // the open land is kept below any road within reach of this grid cell, so it can never bury one
                float lift = Environment.Lift(px, py);
                float hy = (float)Height.Lowest(px, py, w / nx * 1.1f + 2f) - 0.12f + Environment.Surface(px, py, lift);
                // the harbour: the ground drops to the sea bed beyond the quay
                if (Waterfront.InWater(px, py)) hy = SeaBed;
                v.Add(new Vector3(px, hy, py)); uv.Add(new Vector2(px, py)); tint.Add(Environment.GroundTint(px, py, lift));
                if (i < nx && j < ny) { int k = j * (nx + 1) + i; tri.Add(k); tri.Add(k + nx + 1); tri.Add(k + 1); tri.Add(k + 1); tri.Add(k + nx + 1); tri.Add(k + nx + 2); }
            }
            var go = MeshObj("Terrain", v, uv, tri, grass); go.transform.SetParent(root);
            BakeTint.Add(go.GetComponent<MeshFilter>().sharedMesh, tint.ToArray());
        }

        // every run of points where cond(i) holds, as (first, last) with last exclusive
        void Runs(System.Func<int, bool> cond, System.Action<int, int> emit)
        {
            int k = 0;
            while (k < t.N)
            {
                if (!cond(k)) { k++; continue; }
                int j = k; while (j < t.N && cond(j)) j++;
                emit(k, j); k = j;
            }
        }

        // road
        var asphalt = Mat(Color.white, AsphaltTex(), 0.15f);
        // Road.shader draws asphalt, rubber, lines, kerbs and grid in one pass on the baked vertex light. It needs the
        // baked lighting (the same test LightBake.Begin makes); otherwise the road is built the old way below.
        var roadSh = Resources.Load<Shader>("Road"); var bakedSh = Resources.Load<Shader>("Baked");
        RoadShaded = roadSh != null && roadSh.isSupported && bakedSh != null && bakedSh.isSupported;
        MeshRenderer roadR = null; Material roadMat = null;
        if (RoadShaded)
        {
            // the racing line the rubber follows (the same line GameManager draws), and how much rubber each part
            // of it gets: more in corners, where tyres slide, than on the straights
            var rl = RacingLine.Build(t);
            var rub = new float[t.N];
            {
                const int Win = 10; double sum = 0;
                for (int k = -Win; k <= Win; k++) sum += System.Math.Abs(rl.curv[((k % t.N) + t.N) % t.N]);
                for (int i = 0; i < t.N; i++)
                {
                    rub[i] = 0.55f + 0.45f * Mathf.Clamp01((float)(sum / (2 * Win + 1)) * 45f);
                    sum += System.Math.Abs(rl.curv[(i + Win + 1) % t.N]) - System.Math.Abs(rl.curv[((i - Win) % t.N + t.N) % t.N]);
                }
            }
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var uv2 = new List<Vector2>(); var tri = new List<int>();
            var uv3 = new List<Vector2>(); var edgeDirt = EdgeDirt(t);
            // uv0 = (lateral m, + = left; distance along m), uv2 = (racing-line offset m, rubber 0..1), uv3 = edge dirt
            // (left, right) 0..1; see Road.shader
            const int K = 6;
            for (int i = 0; i <= t.N; i++)
            {
                var p = t.pts[i % t.N]; float d = (float)(i * t.ds);
                var line = new Vector2((float)rl.off[i % t.N], rub[i % t.N]);
                for (int c = 0; c <= K; c++)
                {
                    float lat = Wd / 2 - Wd * c / K;
                    v.Add(WT(i, p.x + p.nx * lat, p.y + p.ny * lat, 0.03f)); uv.Add(new Vector2(lat, d)); uv2.Add(line); uv3.Add(edgeDirt[i % t.N]);
                    if (i < t.N && c < K) { int k = i * (K + 1) + c; tri.Add(k); tri.Add(k + K + 1); tri.Add(k + 1); tri.Add(k + K + 1); tri.Add(k + K + 2); tri.Add(k + 1); }
                }
            }
            // kerbs in the same mesh (uv2.y < -0.5 marks them; uv2.x = 0 at the track edge .. 1 at the outer lip).
            // uv2.y = -(1 + type + 0.45 x wear): type 0 kerb, 1 stepped kerb, 2 sausage; wear 0..1 (rubber, chips).
            // uv3 on a kerb vertex: its own side's edge dirt as on the road, the other component = the saw-tooth ridge
            // amount (outside of corner exits). Profiles across (the baked and the sun light change across them):
            //   flat (gentle bends): a low slab with a slight bevel at both edges;
            //   rounded (corners): a rounded nose, a top rising gently to the raised outer lip, a rounded fall;
            //   stepped (outside of medium corners): a low flat inner tier, a step, a raised outer tier;
            //   sausage (inside of the chicanes): a tall rounded hump 0.5 m wide just behind the kerb, on the turf.
            var kl = KerbLayout(t);
            float[] kfFlat = { 0f, 0.04f, 0.3f, 0.7f, 0.95f, 1f }, khFlat = { 0.037f, 0.05f, 0.052f, 0.054f, 0.052f, 0.04f };
            float[] kfRound = { 0f, 0.07f, 0.25f, 0.6f, 0.86f, 1f }, khRound = { 0.04f, 0.062f, 0.072f, 0.085f, 0.09f, 0.068f };
            float[] kfStep = { 0f, 0.04f, 0.42f, 0.47f, 0.9f, 1f }, khStep = { 0.037f, 0.05f, 0.053f, 0.088f, 0.095f, 0.07f };
            float[] kfSaus = { 0f, 0.15f, 0.5f, 0.85f, 1f }, khSaus = { 0.045f, 0.11f, 0.14f, 0.11f, 0.045f };
            void KerbRun(int i0, int i1, float side, float latIn, float latOut, float[] kf, float[] kh, int type)
            {
                int KP = kf.Length, s = side > 0 ? 0 : 1;
                float inner = side * latIn, outer = side * latOut;
                float left = Mathf.Max(inner, outer), right = Mathf.Min(inner, outer);
                int start = v.Count;
                for (int i = i0; i <= i1; i++)
                {
                    var p = t.pts[i % t.N]; float d = (float)(i * t.ds);
                    var ki = kl[s, i % t.N]; var ed = edgeDirt[i % t.N];
                    var kd = s == 0 ? new Vector2(ed.x, type == 2 ? 0f : ki.exit) : new Vector2(type == 2 ? 0f : ki.exit, ed.y);
                    var code = new Vector2(0, -(1f + type + 0.45f * ki.wear));
                    // columns from left to right: on the left-hand kerb (side > 0) the outer lip is at the left
                    for (int c = 0; c < KP; c++)
                    {
                        int pc = side > 0 ? KP - 1 - c : c; float across = kf[pc], g = side > 0 ? 1f - across : across;
                        float lat = left + (right - left) * g; code.x = across;
                        v.Add(WT(i, p.x + p.nx * lat, p.y + p.ny * lat, kh[pc])); uv.Add(new Vector2(lat, d)); uv2.Add(code); uv3.Add(kd);
                    }
                    if (i < i1)
                        for (int c = 0; c + 1 < KP; c++)
                        {
                            int k = start + (i - i0) * KP + c;
                            tri.Add(k); tri.Add(k + KP); tri.Add(k + 1);
                            tri.Add(k + KP); tri.Add(k + KP + 1); tri.Add(k + 1);
                        }
                }
            }
            // one piece per stretch of the same style (pieces meet at a shared track point, as real kerb sections do)
            for (int ks = 0; ks < 2; ks++)
            {
                int ksd = ks; float kside = ks == 0 ? 1f : -1f; var kwArr = ks == 0 ? t.kerbL : t.kerbR;
                for (int kst = 0; kst < 3; kst++)
                {
                    int sty = kst;
                    Runs(i => kwArr[i] > 0 && kl[ksd, i].style == sty, (k, j) => KerbRun(k, j, kside, Wd / 2, Wd / 2 + Kerb,
                        sty == 0 ? kfFlat : sty == 1 ? kfRound : kfStep, sty == 0 ? khFlat : sty == 1 ? khRound : khStep, sty == 2 ? 1 : 0));
                }
                Runs(i => kwArr[i] > 0 && kl[ksd, i].sausage, (k, j) => KerbRun(k, j, kside, Wd / 2 + Kerb + 0.12f, Wd / 2 + Kerb + 0.62f, kfSaus, khSaus, 2));
            }

            // baked like any other surface first (LightBake only lights the plain materials), Road.shader after
            var roadGo = MeshObj("Road", v, uv, tri, asphalt, true, uv2); roadGo.transform.SetParent(root);
            roadGo.GetComponent<MeshFilter>().sharedMesh.SetUVs(3, uv3);
            roadR = roadGo.GetComponent<MeshRenderer>();
            roadMat = new Material(roadSh) { mainTexture = RoadTex() };
            roadMat.SetFloat("_HalfWidth", Wd / 2);
            // grid slots as GameManager places the cars: slot k at track point N - 14 - 4k, 22 slots, 1.8 m off centre
            roadMat.SetVector("_Grid", new Vector4((float)((t.N - 14) * t.ds), (float)(4 * t.ds), 22, 1.8f));
            // timing loops (Sectors.cs: CarTiming.Loops evenly round the lap) and the two sector ends, as sealed saw cuts
            SectorMap.Ends(t.def, out double sf1, out double sf2);
            float lapM = (float)(t.N * t.ds);
            roadMat.SetVector("_Loops", new Vector4(lapM / CarTiming.Loops, (float)sf1 * lapM, (float)sf2 * lapM, 0f));
            roadMat.SetVector("_SunDir", -sunGo.transform.forward);
            roadMat.SetColor("_SunCol", sun.color * sun.intensity);
            // sunlit asphalt is a clean mid grey, not black (sRGB 0.38 -> about 0.5 on screen in the sun after the bake)
            roadMat.SetColor("_Asphalt", Street(t) ? new Color(0.38f, 0.38f, 0.39f) : new Color(0.38f, 0.38f, 0.385f)); // (0.365 -> 0.38, near neutral: chase asphalt measured 140 sRGB after the grade fix, target 150-160; base a touch lighter now that the dry sheen and the edge dust no longer bleach it: ~150 sRGB in the sun)
            roadMat.SetColor("_Dust", new Color(0.52f, 0.50f, 0.47f));
            RoadSurface.Attach(root.gameObject, roadSh);
        }
        else
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            // several columns across the width, so baked shadows can fall part-way across the road
            const int K = 6;
            for (int i = 0; i <= t.N; i++)
            {
                var p = t.pts[i % t.N];
                for (int c = 0; c <= K; c++)
                {
                    float lat = Wd / 2 - Wd * c / K;
                    v.Add(WT(i, p.x + p.nx * lat, p.y + p.ny * lat, 0.03f)); uv.Add(new Vector2((float)c / K * Wd / 4, (float)(i * t.ds) / 8));
                    if (i < t.N && c < K) { int k = i * (K + 1) + c; tri.Add(k); tri.Add(k + K + 1); tri.Add(k + 1); tri.Add(k + K + 1); tri.Add(k + K + 2); tri.Add(k + 1); }
                }
            }
            MeshObj("Road", v, uv, tri, asphalt).transform.SetParent(root);
        }
        // white edge lines, start/finish line and grid boxes as geometry (Road.shader paints them itself)
        if (!RoadShaded)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            Strip(t, 0, t.N, Wd / 2 - 0.15f, Wd / 2 - 0.4f, 0.045f, 4, v, uv, tri);
            Strip(t, 0, t.N, -Wd / 2 + 0.4f, -Wd / 2 + 0.15f, 0.045f, 4, v, uv, tri);
            Strip(t, 0, 1, Wd / 2, -Wd / 2, 0.05f, 4, v, uv, tri); // start/finish line
            // grid boxes: one per starting slot (8 m apart, staggered left and right), as the cars are placed
            for (int k = 0; k < 22; k++)
            {
                int i = ((t.N - 15 - k * 4 + 1) % t.N + t.N) % t.N; float side = (k % 2 == 0 ? 1 : -1) * 1.8f;
                var p = t.pts[i]; int b0 = v.Count;
                // a 2 m bar across the slot, 0.3 m deep, with short side marks reaching back towards the car
                foreach (var (a0, a1, l0, l1) in new[] { (-0.15f, 0.15f, side - 1f, side + 1f), (-1.2f, 0.15f, side + 0.85f, side + 1f), (-1.2f, 0.15f, side - 1f, side - 0.85f) })
                {
                    int q = v.Count;
                    v.Add(W(p.x + p.tx * a0 + p.nx * l1, p.y + p.ty * a0 + p.ny * l1, 0.05f));
                    v.Add(W(p.x + p.tx * a0 + p.nx * l0, p.y + p.ty * a0 + p.ny * l0, 0.05f));
                    v.Add(W(p.x + p.tx * a1 + p.nx * l1, p.y + p.ty * a1 + p.ny * l1, 0.05f));
                    v.Add(W(p.x + p.tx * a1 + p.nx * l0, p.y + p.ty * a1 + p.ny * l0, 0.05f));
                    for (int u = 0; u < 4; u++) uv.Add(Vector2.zero);
                    tri.Add(q); tri.Add(q + 2); tri.Add(q + 1); tri.Add(q + 2); tri.Add(q + 3); tri.Add(q + 1);
                }
            }
            MeshObj("Lines", v, uv, tri, Mat(new Color(0.95f, 0.95f, 0.93f))).transform.SetParent(root);
        }
        // kerbs, astroturf and gravel traps, from the circuit's layout (the same data the tyres read)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            if (!RoadShaded) // (Road.shader draws the kerbs as part of the road)
            {
                Runs(i => t.kerbL[i] > 0, (k, j) => Strip(t, k, j, Wd / 2, Wd / 2 + Kerb, 0.05f, 3, v, uv, tri, 0.09f));
                Runs(i => t.kerbR[i] > 0, (k, j) => Strip(t, k, j, -Wd / 2, -(Wd / 2 + Kerb), 0.05f, 3, v, uv, tri, 0.09f));
                MeshObj("Kerbs", v, uv, tri, Mat(Color.white, KerbTex())).transform.SetParent(root);
            }
            if (t.def != null && t.def.style == CircuitStyle.Parkland)
            {
                // a 2 m strip behind the kerb (or the track edge) all the way round (physically astroturf, Track.SurfaceAt):
                // astroturf, or green / blue painted asphalt run-off on the outside of the corners (Monza). With
                // Ground.shader (_Turf mode): uv0 = uv2 = (metres from the centreline, metres along), uv3 = (the strip's
                // inner edge m, paint 0/1 + 0.45 x tyre marks of cars running wide over it); each 1-point segment has
                // its own vertices, so a turf / paint change is a crisp line across
                v = new List<Vector3>(); uv = new List<Vector2>(); tri = new List<int>();
                var uv2t = new List<Vector2>(); var uv3t = new List<Vector2>(); var rk = KerbLayout(t);
                Runs(i => true, (k, j) =>
                {
                    for (int i = k; i < j; i++)
                    {
                        for (int ts = 0; ts < 2; ts++)
                        {
                            float sd = ts == 0 ? 1f : -1f, a = Wd / 2 + (ts == 0 ? t.kerbL[i] : t.kerbR[i]), b = a + 2f;
                            int first = v.Count;
                            Strip(t, i, i + 1, sd * a, sd * b, 0.035f, 4, v, uv, tri);
                            if (!GroundShaded) continue;
                            float paint = rk[ts, i].paint ? 1f : 0f;
                            for (int q = first; q < v.Count; q++)
                            {
                                bool lv = (q - first) % 2 == 0; // Strip: (left, right) per point; left = larger lateral
                                float lat = sd > 0 ? (lv ? b : a) : (lv ? a : b);
                                int pi = i + (q - first) / 2; float along = (float)(pi * t.ds);
                                uv[q] = new Vector2(lat, along); uv2t.Add(new Vector2(lat, along));
                                uv3t.Add(new Vector2(a, paint + 0.45f * rk[ts, pi % t.N].exit));
                            }
                        }
                    }
                });
                if (GroundShaded)
                {
                    var tm = new Material(groundSh) { mainTexture = EnvTex.Turf() };
                    tm.SetTexture("_MacroTex", EnvTex.Macro());
                    tm.SetVector("_DryTint", new Vector4(1, 1, 1, 0));
                    tm.SetFloat("_Turf", 1f);
                    tm.mainTextureScale = new Vector2(1f / 2f, 1f / 4f); // one tile = 2 m across x 4 m along (EnvTex.Turf)
                    var tgo = MeshObj("Astroturf", v, uv, tri, tm, true, uv2t); tgo.transform.SetParent(root);
                    tgo.GetComponent<MeshFilter>().sharedMesh.SetUVs(3, uv3t);
                }
                else MeshObj("Astroturf", v, uv, tri, Mat(Color.white, EnvTex.Turf())).transform.SetParent(root); // (the turf colour is in the texture)
                // gravel traps out to the barrier: Ground.shader with the stone texture in world space; uv2 = (metres from
                // the centreline, metres along) for the churned inner edge and the ruts of cars that went off
                v = new List<Vector3>(); uv = new List<Vector2>(); tri = new List<int>(); var uv2g = new List<Vector2>();
                float gIn = Wd / 2 + Kerb + 2f, gOut = Wd / 2 + (float)t.def.barrier - 0.4f;
                void GStrip(int k, int j, float side)
                {
                    int first = v.Count;
                    Strip(t, k, j, side * gIn, side * gOut, 0.025f, 6, v, uv, tri);
                    for (int q = first; q < v.Count; q++)
                    {
                        bool leftVert = (q - first) % 2 == 0; // Strip adds (left, right) per point; left = larger lateral
                        float lat = side > 0 ? (leftVert ? gOut : gIn) : (leftVert ? gIn : gOut);
                        uv2g.Add(new Vector2(lat, (float)((k + (q - first) / 2) * t.ds)));
                        uv[q] = new Vector2(v[q].x, v[q].z);
                    }
                }
                Runs(i => t.runL[i] == SurfaceType.Gravel, (k, j) => GStrip(k, System.Math.Min(j, t.N), 1));
                Runs(i => t.runR[i] == SurfaceType.Gravel, (k, j) => GStrip(k, System.Math.Min(j, t.N), -1));
                Material gm;
                if (GroundShaded)
                {
                    gm = new Material(groundSh) { mainTexture = EnvTex.Gravel() };
                    gm.SetTexture("_MacroTex", EnvTex.Macro());
                    gm.SetVector("_DryTint", new Vector4(1, 1, 1, 0));
                    gm.SetVector("_Wall", new Vector4(wallLat, 0.1f, 0, 0));
                    gm.SetVector("_Ruts", new Vector4(gIn, 1f, 0, 0));
                }
                else gm = Mat(Color.white, EnvTex.Gravel());
                gm.mainTextureScale = new Vector2(1f / 2.5f, 1f / 2.5f);
                MeshObj("Gravel", v, uv, tri, gm, true, uv2g).transform.SetParent(root);
            }
        }
        if (Street(t))
        {
            // street circuit: paved shoulders out to the walls, and (on hills) the retaining wall below the street
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            float edge = Wd / 2 + 3.3f;
            Strip(t, 0, t.N, Wd / 2, edge, 0.028f, 6, v, uv, tri);
            Strip(t, 0, t.N, -Wd / 2, -edge, 0.028f, 6, v, uv, tri);
            MeshObj("Pavement", v, uv, tri, Mat(Color.white, EnvTex.Paving(), 0.1f)).transform.SetParent(root);
            if (t.h != null)
            {
                v = new List<Vector3>(); uv = new List<Vector2>(); tri = new List<int>();
                for (int side = -1; side <= 1; side += 2)
                {
                    int start = v.Count;
                    for (int i = 0; i <= t.N; i++)
                    {
                        var p = t.pts[i % t.N];
                        var top = W(p.x + p.nx * edge * side, p.y + p.ny * edge * side, 0.03f);
                        v.Add(top); v.Add(top + Vector3.down * 18f);
                        float u = (float)(i * t.ds) / 4f; uv.Add(new Vector2(u, 0)); uv.Add(new Vector2(u, 4.5f));
                        if (i < t.N)
                        {
                            int k = start + i * 2;
                            tri.Add(k); tri.Add(k + 1); tri.Add(k + 2); tri.Add(k + 2); tri.Add(k + 1); tri.Add(k + 3);
                            tri.Add(k); tri.Add(k + 2); tri.Add(k + 1); tri.Add(k + 2); tri.Add(k + 3); tri.Add(k + 1);
                        }
                    }
                }
                MeshObj("RetainingWalls", v, uv, tri, Mat(new Color(0.96f, 0.94f, 0.90f), EnvTex.Stone(), 0.1f)).transform.SetParent(root);
            }
        }
        Waterfront.BuildTunnel(t, root);
        BuildBridges(t, root);
        Trackside.Build(t, root, Street(t));
        BuildTerrain();
        if (GroundShaded && !Street(t)) BuildVerge(t, grass, wallLat, root);
        Scenery.Build(t, root, Street(t)); // street circuit (Monaco, 9 m wide) -> walls; others -> guardrails
        // landmarks, hillside blocks, yachts (Environment.cs); the blocks go with the fences, out of the baked shadow map
        if (Trackside.Late == null) Trackside.Late = new GameObject("TracksideLate");
        Environment.Props(t, root, Trackside.Late.transform);

        // playground pad + skidpad rings + distance boards + slalom cones
        {
            var pad = GameObject.CreatePrimitive(PrimitiveType.Quad); Object.Destroy(pad.GetComponent<Collider>());
            pad.name = "Playground"; pad.transform.SetParent(root);
            pad.transform.position = new Vector3(Pad.center.x, 0.03f, Pad.center.y); pad.transform.rotation = Quaternion.Euler(90, 0, 0);
            pad.transform.localScale = new Vector3(Pad.width, Pad.height, 1);
            var pm = Mat(Color.white, AsphaltTex(), 0.15f); pm.mainTextureScale = new Vector2(Pad.width / 8, Pad.height / 8);
            pad.GetComponent<MeshRenderer>().sharedMaterial = pm;
            var paint = Mat(new Color(0.95f, 0.95f, 0.93f));
            var sk = new Vector2(Pad.xMax - 110, Pad.center.y);
            foreach (float rad in new[] { 30f, 50f }) Ring(root, sk, rad, paint);
            Box(root, new Vector3(PadStart.x, 0.04f, PadStart.y), new Vector3(0.6f, 0.01f, 20), paint);
            for (int d = 100; d <= 560; d += 10)
                Box(root, new Vector3(PadStart.x + d, 0.04f, Pad.center.y - 14), new Vector3(d % 50 == 0 ? 0.5f : 0.2f, 0.01f, d % 50 == 0 ? 10 : 4), paint);
            var cone = Mat(new Color(1f, 0.48f, 0.1f));
            for (int i = 0; i < 30; i++)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cylinder); Object.Destroy(c.GetComponent<Collider>());
                c.transform.SetParent(root); c.transform.position = new Vector3(Pad.center.x - 300 + i * 15, 0.375f, Pad.center.y + 120);
                c.transform.localScale = new Vector3(0.45f, 0.375f, 0.45f); c.GetComponent<MeshRenderer>().sharedMaterial = cone;
            }
        }

        // trees: one mesh of crossed, alpha-tested cards (Foliage.shader), so thousands cost one draw call. Kind, mix and density come
        // from the circuit's environment: spruce belts at Spa, tall park trees at Monza, palms and pines at Monaco...
        Environment.Trees(t, root, ext);
        Drape(root.gameObject);
        var late = Trackside.Late; // fences and ground decals: lit by the bake, but kept out of its shadow map
        if (late != null) Drape(late);
        // the real surroundings' meshes, at their final heights (after Drape), lit by the bake below like the rest
        RealWorld.Build(t, root);
        // lighting baked into the vertices (LightBake.cs); if the phone can't run that shader, the sun and
        // real-time shadows are used as before
        if (LightBake.Begin(t, -sunGo.transform.forward))
        {
            LightBake.AddOccluders(root.gameObject);
            if (late != null) late.transform.SetParent(root, false);
            LightBake.Apply(root.gameObject);
            sun.shadows = LightShadows.None; sun.cullingMask = ~(1 << LightBake.CarLayer);
            var carSunGo = new GameObject("CarSun"); carSunGo.transform.SetParent(root); carSunGo.transform.rotation = sunGo.transform.rotation;
            var cs2 = carSunGo.AddComponent<Light>(); cs2.type = LightType.Directional; cs2.color = sun.color; cs2.intensity = sun.intensity * Sky.CarSunK; // (GameManager drives it from Sky.CarSunShare)
            cs2.shadows = LightShadows.None; cs2.cullingMask = 1 << LightBake.CarLayer;
            LightBake.CarSun = cs2;
        }
        if (late != null && late.transform.parent != root) late.transform.SetParent(root, false);
        Trackside.Late = null;
        // ambient occlusion, dirt and variety tints on top of the baked light, then the fence shader (before batching)
        BakeTint.Apply(LightBake.Active);
        Trackside.AfterBake(root.gameObject);
        // the road is lit now (vertex colours): hand it to Road.shader
        if (roadR != null) { if (LightBake.Active) roadR.sharedMaterial = roadMat; else RoadShaded = false; }
        if (roadR != null && LightBake.Active) RoadShadow.Build(t, root.gameObject, roadR, sun); // sharp sun shadows on the road (before batching)
        EnvSurface.Attach(root.gameObject, GroundShaded ? groundSh : null, FoliageShader, ModelInstancing.ShaderFor(root.gameObject)); // Low: LOD 150 (mip bias, no sway)
        SectorVisibility.Prepare(t, root.gameObject); // cut circuit-wide meshes into tiles, per-sector view sets (SectorVisibility.cs)
        StaticBatchingUtility.Combine(root.gameObject);

        // one reflection capture of sky + ground, rendered once: cheap, and makes the paint look real
        QualitySettings.realtimeReflectionProbes = true;
        var probeGo = new GameObject("SkyReflection"); probeGo.transform.SetParent(root); probeGo.transform.position = new Vector3(cx, 2, cy);
        var probe = probeGo.AddComponent<ReflectionProbe>();
        probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
        probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
        probe.size = new Vector3(5000, 2000, 5000); probe.resolution = 128; probe.hdr = false;
        // the bright procedural sky behind the clouds, at full strength (the paint reflects a clear blue sky, not grey)
        probe.clearFlags = UnityEngine.Rendering.ReflectionProbeClearFlags.Skybox; probe.intensity = 1f;
        // sky only (skybox + the cloud/haze layer): grass reflected in the glossy paint showed as green patches
        probe.cullingMask = 1 << Sky.Layer; probe.farClipPlane = 3000;
        // clouds + sun glare (camera-following, so made after static batching); renders the probe once, clouds included
        Sky.Create(root, skyLook, -sunGo.transform.forward, probe);
        // trackside reflections for the car paint: small world cubes along the lap, baked once (Medium / High; CarEnv.cs)
        CarEnv.Attach(root, t);
        // what the car paint reflects below the horizon (the road), and full reflections unless in shade
        Shader.SetGlobalColor("_GroundRefl", env.groundRefl);
        Shader.SetGlobalFloat("_CarLit", 1f);
        return root.gameObject;
    }

    static void Box(Transform root, Vector3 pos, Vector3 size, Material m)
    {
        var b = GameObject.CreatePrimitive(PrimitiveType.Cube); Object.Destroy(b.GetComponent<Collider>());
        b.transform.SetParent(root); b.transform.position = pos; b.transform.localScale = size;
        var r = b.GetComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }
    static void Ring(Transform root, Vector2 c, float rad, Material m)
    {
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
        int seg = 160;
        for (int i = 0; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2 / seg;
            v.Add(new Vector3(c.x + Mathf.Cos(a) * (rad + 0.2f), 0.045f, c.y + Mathf.Sin(a) * (rad + 0.2f)));
            v.Add(new Vector3(c.x + Mathf.Cos(a) * (rad - 0.2f), 0.045f, c.y + Mathf.Sin(a) * (rad - 0.2f)));
            uv.Add(Vector2.zero); uv.Add(Vector2.one);
            if (i < seg) { int k = i * 2; tri.Add(k); tri.Add(k + 1); tri.Add(k + 2); tri.Add(k + 2); tri.Add(k + 1); tri.Add(k + 3); }
        }
        MeshObj("Ring", v, uv, tri, m).transform.SetParent(root);
    }
}
