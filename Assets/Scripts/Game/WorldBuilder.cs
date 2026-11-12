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
    // or Low once Auto has stepped down to it, which GameManager shows by switching anisotropic filtering off)
    public static int Gfx = 2;
    // the baked vertex lighting will run (same test as LightBake.Begin): vertex-colour tints and the fence shader work
    public static bool BakeReady;
    // ---------------------------------------------------------------- hills
    // Height of the ground at a point (0 everywhere on a flat circuit). The road, scenery and cars all use it.
    public static HeightField Height;
    public static float H(double x, double y) => Height == null ? 0f : (float)Height.Sample(x, y);

    // Lay everything under "root" onto the hills: meshes built in world coordinates get every vertex lifted,
    // placed objects (trees, cones) are lifted as a whole.
    public static void Drape(GameObject root)
    {
        if (Height == null) return;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            var tr = mf.transform;
            if (mf.name == "Terrain" || mf.name == "Harbour") continue; // built at their final height already
            if (tr.localToWorldMatrix.isIdentity)
            {
                var mesh = mf.sharedMesh; if (mesh == null) continue;
                var v = mesh.vertices;
                for (int i = 0; i < v.Length; i++) v[i].y += H(v[i].x, v[i].z);
                mesh.vertices = v; mesh.RecalculateNormals(); mesh.RecalculateBounds();
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
    public static Texture2D GrassTex() => Tex(256, 256, (u, v, x, y) =>
    {
        float r = Hash(x, y, 31);
        float k = 0.75f + Fbm(u, v, 6, 4, 9) * 0.35f + Fbm(u, v, 64, 2, 13) * 0.2f + (r > 0.9f ? 0.12f : r < 0.08f ? -0.12f : 0);
        return new Color32(B(62 * k), B(112 * k), B(44 * k), 255);
    });
    // astroturf: green with the darker painted stripes run-off areas have
    static Texture2D TurfTex() => Tex(64, 64, (u, v, x, y) =>
    {
        bool stripe = ((int)(v * 4)) % 2 == 0;
        float k = (stripe ? 0.82f : 1f) * (0.9f + Fbm(u, v, 16, 2, 41) * 0.15f) * (0.95f + Hash(x, y, 43) * 0.08f);
        return new Color32(B(58 * k), B(128 * k), B(70 * k), 255);
    });
    // gravel trap: pale stones of mixed size
    static Texture2D GravelTex() => Tex(128, 128, (u, v, x, y) =>
    {
        float r = Hash(x, y, 59), stones = Fbm(u, v, 32, 3, 61);
        float c = 150 + stones * 60 + (r > 0.85f ? 28 : r < 0.12f ? -34 : 0);
        return new Color32(B(c * 1.02f), B(c * 0.95f), B(c * 0.84f), 255);
    });
    static Texture2D KerbTex() => Tex(32, 64, (u, v, x, y) =>
    {
        bool red = v < 0.5f; float edge = Mathf.Min(u, 1 - u), lip = Mathf.Clamp(edge * 9, 0.55f, 1);
        float k = lip * (0.85f + Fbm(u, v, 8, 2, 7) * 0.2f);
        return red ? new Color32(B(205 * k), B(32 * k), B(38 * k), 255) : new Color32(B(238 * k), B(238 * k), B(234 * k), 255);
    });

    // ---------------------------------------------------------------- meshes
    static Vector3 W(double x, double y, float h = 0) => new Vector3((float)x, h, (float)y);

    // strip along the track between lateral offsets a and b (+ = left), i0..i1 inclusive
    static void Strip(Track t, int i0, int i1, float a, float b, float h, float vScale, List<Vector3> v, List<Vector2> uv, List<int> tri, float hOuter = float.NaN)
    {
        float left = Mathf.Max(a, b), right = Mathf.Min(a, b);
        float hl = a >= b ? h : (float.IsNaN(hOuter) ? h : hOuter), hr = a >= b ? (float.IsNaN(hOuter) ? h : hOuter) : h;
        int start = v.Count;
        for (int i = i0; i <= i1; i++)
        {
            var p = t.pts[i % t.N];
            v.Add(W(p.x + p.nx * left, p.y + p.ny * left, hl));
            v.Add(W(p.x + p.nx * right, p.y + p.ny * right, hr));
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

    const float SeaBed = -4f;
    static bool Street(Track t) => t.def != null ? t.def.style == CircuitStyle.Street : t.width < 10.0;

    public static GameObject Build(Track t)
    {
        var root = new GameObject("World").transform;
        int gfxSetting = PlayerPrefs.GetInt("gfx", 0);
        Gfx = gfxSetting == 0 ? (QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable ? 1 : 2) : Mathf.Clamp(gfxSetting, 1, 3);
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
        RenderSettings.ambientSkyColor = new Color(0.62f, 0.72f, 0.85f);
        RenderSettings.ambientEquatorColor = new Color(0.55f, 0.6f, 0.6f);
        RenderSettings.ambientGroundColor = new Color(0.3f, 0.35f, 0.25f);
        var sunGo = new GameObject("Sun"); sunGo.transform.SetParent(root);
        var sun = sunGo.AddComponent<Light>(); sun.type = LightType.Directional; sun.color = new Color(1f, 0.95f, 0.86f); sun.intensity = 1.25f;
        sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.8f; sun.shadowBias = 0.03f; sun.shadowNormalBias = 0.3f;
        sunGo.transform.rotation = Quaternion.Euler(48, -35, 0);
        var skyLook = Sky.Setup(t, Street(t), sun);

        // ground
        var grassTex = GrassTex();
        var grass = Mat(Color.white, grassTex);
        float gsize = Mathf.Max(4400, ext + 3000);
        grass.mainTextureScale = new Vector2(gsize / 10, gsize / 10);
        Height = t.h != null || t.bank != null ? new HeightField(t) : null; // banked corners tilt the road (HeightField)
        Waterfront.Prepare(t);
        {
            // ground: one big flat sheet, plus (on a hilly circuit) a fine grid around the track that follows the hills
            void Sheet(string name, float x0, float y0, float w, float hgt, int nx, int ny, float yy)
            {
                var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
                for (int j = 0; j <= ny; j++) for (int i = 0; i <= nx; i++)
                {
                    float px = x0 + w * i / nx, py = y0 + hgt * j / ny;
                    // the open land is kept below any road within reach of this grid cell, so it can never bury one
                    float hy = name == "Terrain" ? (float)Height.Lowest(px, py, w / nx * 1.1f + 2f) + yy : yy;
                    // the harbour: the ground drops to the sea bed beyond the quay
                    if (name == "Terrain" && Waterfront.InWater(px, py)) hy = SeaBed;
                    v.Add(new Vector3(px, hy, py)); uv.Add(new Vector2(px / gsize, py / gsize));
                    if (i < nx && j < ny) { int k = j * (nx + 1) + i; tri.Add(k); tri.Add(k + nx + 1); tri.Add(k + 1); tri.Add(k + 1); tri.Add(k + nx + 1); tri.Add(k + nx + 2); }
                }
                MeshObj(name, v, uv, tri, grass).transform.SetParent(root);
            }
            Sheet("Ground", cx - gsize / 2, cy - 400 - gsize / 2, gsize, gsize, 1, 1, Waterfront.HasWater ? SeaBed : Height == null ? -0.05f : -0.35f - (float)Height.bankDrop);
            if (Waterfront.HasWater) Waterfront.BuildWater(root, 1.0f); // about 3 m below the quay
            if (Height != null)
            {
                float m = 400, w = (float)(bb.maxX - bb.minX) + 2 * m, hg = (float)(bb.maxY - bb.minY) + 2 * m, cell = Mathf.Max(8f, ext / 180f);
                Sheet("Terrain", (float)bb.minX - m, (float)bb.minY - m, w, hg, Mathf.CeilToInt(w / cell), Mathf.CeilToInt(hg / cell), -0.12f);
            }
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
                    rub[i] = 0.45f + 0.55f * Mathf.Clamp01((float)(sum / (2 * Win + 1)) * 45f);
                    sum += System.Math.Abs(rl.curv[(i + Win + 1) % t.N]) - System.Math.Abs(rl.curv[((i - Win) % t.N + t.N) % t.N]);
                }
            }
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var uv2 = new List<Vector2>(); var tri = new List<int>();
            // uv0 = (lateral m, + = left; distance along m), uv2 = (racing-line offset m, rubber 0..1); see Road.shader
            const int K = 6;
            for (int i = 0; i <= t.N; i++)
            {
                var p = t.pts[i % t.N]; float d = (float)(i * t.ds);
                var line = new Vector2((float)rl.off[i % t.N], rub[i % t.N]);
                for (int c = 0; c <= K; c++)
                {
                    float lat = Wd / 2 - Wd * c / K;
                    v.Add(W(p.x + p.nx * lat, p.y + p.ny * lat, 0.03f)); uv.Add(new Vector2(lat, d)); uv2.Add(line);
                    if (i < t.N && c < K) { int k = i * (K + 1) + c; tri.Add(k); tri.Add(k + K + 1); tri.Add(k + 1); tri.Add(k + K + 1); tri.Add(k + K + 2); tri.Add(k + 1); }
                }
            }
            // kerbs in the same mesh (uv2.y = -1 marks them; uv2.x = 0 at the track edge .. 1 at the raised outer lip)
            void KerbRun(int i0, int i1, float side)
            {
                float inner = side * Wd / 2, outer = side * (Wd / 2 + Kerb);
                float left = Mathf.Max(inner, outer), right = Mathf.Min(inner, outer);
                float hl = side > 0 ? 0.09f : 0.05f, hr = side > 0 ? 0.05f : 0.09f, al = side > 0 ? 1f : 0f;
                int start = v.Count;
                for (int i = i0; i <= i1; i++)
                {
                    var p = t.pts[i % t.N]; float d = (float)(i * t.ds);
                    v.Add(W(p.x + p.nx * left, p.y + p.ny * left, hl)); uv.Add(new Vector2(left, d)); uv2.Add(new Vector2(al, -1));
                    v.Add(W(p.x + p.nx * right, p.y + p.ny * right, hr)); uv.Add(new Vector2(right, d)); uv2.Add(new Vector2(1 - al, -1));
                    if (i < i1)
                    {
                        int k = start + (i - i0) * 2;
                        tri.Add(k); tri.Add(k + 2); tri.Add(k + 1);
                        tri.Add(k + 2); tri.Add(k + 3); tri.Add(k + 1);
                    }
                }
            }
            Runs(i => t.kerbL[i] > 0, (k, j) => KerbRun(k, j, 1));
            Runs(i => t.kerbR[i] > 0, (k, j) => KerbRun(k, j, -1));
            // baked like any other surface first (LightBake only lights the plain materials), Road.shader after
            var roadGo = MeshObj("Road", v, uv, tri, asphalt, true, uv2); roadGo.transform.SetParent(root);
            roadR = roadGo.GetComponent<MeshRenderer>();
            roadMat = new Material(roadSh) { mainTexture = RoadTex() };
            roadMat.SetFloat("_HalfWidth", Wd / 2);
            // grid slots as GameManager places the cars: slot k at track point N - 14 - 4k, 22 slots, 1.8 m off centre
            roadMat.SetVector("_Grid", new Vector4((float)((t.N - 14) * t.ds), (float)(4 * t.ds), 22, 1.8f));
            roadMat.SetVector("_SunDir", -sunGo.transform.forward);
            roadMat.SetColor("_SunCol", sun.color * sun.intensity);
            if (Street(t)) roadMat.SetColor("_Asphalt", new Color(0.30f, 0.30f, 0.31f)); // town streets: older, greyer surface
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
                    v.Add(W(p.x + p.nx * lat, p.y + p.ny * lat, 0.03f)); uv.Add(new Vector2((float)c / K * Wd / 4, (float)(i * t.ds) / 8));
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
                // a 2 m astroturf strip behind the kerb (or the track edge) all the way round
                v = new List<Vector3>(); uv = new List<Vector2>(); tri = new List<int>();
                Runs(i => true, (k, j) =>
                {
                    for (int i = k; i < j; i++)
                    {
                        float kl = t.kerbL[i], kr = t.kerbR[i];
                        Strip(t, i, i + 1, Wd / 2 + kl, Wd / 2 + kl + 2f, 0.035f, 4, v, uv, tri);
                        Strip(t, i, i + 1, -(Wd / 2 + kr), -(Wd / 2 + kr + 2f), 0.035f, 4, v, uv, tri);
                    }
                });
                MeshObj("Astroturf", v, uv, tri, Mat(Color.white, TurfTex())).transform.SetParent(root);
                // gravel traps out to the barrier
                v = new List<Vector3>(); uv = new List<Vector2>(); tri = new List<int>();
                float gOut = Wd / 2 + (float)t.def.barrier - 0.4f;
                Runs(i => t.runL[i] == SurfaceType.Gravel, (k, j) => Strip(t, k, System.Math.Min(j, t.N), Wd / 2 + Kerb + 2f, gOut, 0.025f, 6, v, uv, tri));
                Runs(i => t.runR[i] == SurfaceType.Gravel, (k, j) => Strip(t, k, System.Math.Min(j, t.N), -(Wd / 2 + Kerb + 2f), -gOut, 0.025f, 6, v, uv, tri));
                for (int i = 0; i < uv.Count; i++) uv[i] = new Vector2(uv[i].x * (gOut - Wd / 2 - Kerb - 2f) / 6f, uv[i].y);
                MeshObj("Gravel", v, uv, tri, Mat(Color.white, GravelTex())).transform.SetParent(root);
            }
        }
        if (Street(t))
        {
            // street circuit: paved shoulders out to the walls, and (on hills) the retaining wall below the street
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            float edge = Wd / 2 + 3.3f;
            Strip(t, 0, t.N, Wd / 2, edge, 0.028f, 6, v, uv, tri);
            Strip(t, 0, t.N, -Wd / 2, -edge, 0.028f, 6, v, uv, tri);
            MeshObj("Pavement", v, uv, tri, Mat(new Color(0.62f, 0.62f, 0.64f), AsphaltTex(), 0.1f)).transform.SetParent(root);
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
                MeshObj("RetainingWalls", v, uv, tri, Mat(new Color(0.72f, 0.69f, 0.63f), AsphaltTex(), 0.1f)).transform.SetParent(root);
            }
        }
        Waterfront.BuildTunnel(t, root);
        Trackside.Build(t, root, Street(t));
        Scenery.Build(t, root, Street(t)); // street circuit (Monaco, 9 m wide) -> walls; others -> guardrails

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

        // trees: built straight into two combined meshes (crowns, trunks) so thousands cost two draw calls.
        // A forest circuit (Spa) gets dense belts of spruce; others get scattered broad-leaf trees.
        {
            float dens = t.def != null ? (float)t.def.forest : 0.3f;
            bool conifer = dens > 0.6f;
            var crown = new List<Vector3>(); var cuv = new List<Vector2>(); var ctri = new List<int>();
            var trunkV = new List<Vector3>(); var tuv = new List<Vector2>(); var ttri = new List<int>();
            var rnd = new System.Random(7); int placed = 0, tries = 0;
            int want = Mathf.Clamp((int)(t.length * 0.4f * dens + ext / 6), 200, 2500);
            if (Gfx <= 1) want = want * 3 / 5; // Low: fewer trees
            var ctint = new List<Color>(); // per-tree shade variety, multiplied into the baked light (BakeTint)
            float near = 26f, far = conifer ? 260f : 600f;
            // a ring of n points at height y with radius r around (x, z)
            void Ring(List<Vector3> v, float x, float z, float y, float r, int n, float rot)
            { for (int k = 0; k < n; k++) { float a2 = rot + k * Mathf.PI * 2 / n; v.Add(new Vector3(x + Mathf.Cos(a2) * r, y, z + Mathf.Sin(a2) * r)); cuv.Add(Vector2.zero); } }
            void Cone(float x, float z, float y0, float y1, float r, int n, float rot)
            {
                int b0 = crown.Count; Ring(crown, x, z, y0, r, n, rot); int tip = crown.Count; crown.Add(new Vector3(x, y1, z)); cuv.Add(Vector2.zero);
                for (int k = 0; k < n; k++) { ctri.Add(b0 + k); ctri.Add(tip); ctri.Add(b0 + (k + 1) % n); }
            }
            while (placed < want && tries++ < want * 30)
            {
                double x = bb.minX - far * 0.6 + rnd.NextDouble() * (bb.maxX - bb.minX + far * 1.2), y = bb.minY - far * 0.6 + rnd.NextDouble() * (bb.maxY - bb.minY + far * 1.2);
                if (Pad.Contains(new Vector2((float)x, (float)y)) || Waterfront.InWater((float)x, (float)y) || Trackside.Blocked((float)x, (float)y, 5f)) continue;
                double dist = t.Nearest(x, y).dist;
                if (dist < near + (t.def != null ? t.def.barrier : 0) || dist > far) continue;
                float sz = 0.7f + (float)rnd.NextDouble() * 0.8f, rot = (float)rnd.NextDouble() * 6.28f, fx = (float)x, fz = (float)y;
                if (conifer)
                {
                    // spruce: three stacked cones
                    float hgt = 14f * sz, r0 = 3.2f * sz;
                    Cone(fx, fz, 1.5f * sz, hgt * 0.55f, r0, 7, rot);
                    Cone(fx, fz, hgt * 0.35f, hgt * 0.8f, r0 * 0.75f, 7, rot + 0.4f);
                    Cone(fx, fz, hgt * 0.6f, hgt, r0 * 0.5f, 6, rot + 0.8f);
                }
                else
                {
                    // broad-leaf: a squashed double cone (eight faces) on a trunk
                    float crY = 4.6f * sz, rr = 2.6f * sz; int b0 = crown.Count;
                    Ring(crown, fx, fz, crY, rr, 6, rot);
                    crown.Add(new Vector3(fx, crY + 3.2f * sz, fz)); crown.Add(new Vector3(fx, crY - 2.4f * sz, fz)); cuv.Add(Vector2.zero); cuv.Add(Vector2.zero);
                    for (int k = 0; k < 6; k++) { int a1 = b0 + k, a2 = b0 + (k + 1) % 6; ctri.Add(a1); ctri.Add(b0 + 6); ctri.Add(a2); ctri.Add(a1); ctri.Add(a2); ctri.Add(b0 + 7); }
                }
                {
                    float sh = 0.82f + (float)rnd.NextDouble() * 0.3f, warm = (float)rnd.NextDouble() * 0.12f;
                    var tc = new Color(sh * (1f + warm), sh, sh * (1f - warm * 0.5f), 1f);
                    while (ctint.Count < crown.Count) ctint.Add(tc);
                }
                // trunk: a thin three-sided post
                int tb = trunkV.Count; float tr2 = 0.25f * sz, th2 = conifer ? 2f * sz : 3f * sz;
                for (int k = 0; k < 3; k++) { float a2 = rot + k * 2.094f; trunkV.Add(new Vector3(fx + Mathf.Cos(a2) * tr2, 0, fz + Mathf.Sin(a2) * tr2)); trunkV.Add(new Vector3(fx + Mathf.Cos(a2) * tr2, th2, fz + Mathf.Sin(a2) * tr2)); tuv.Add(Vector2.zero); tuv.Add(Vector2.zero); }
                for (int k = 0; k < 3; k++) { int a1 = tb + k * 2, a2 = tb + ((k + 1) % 3) * 2; ttri.Add(a1); ttri.Add(a1 + 1); ttri.Add(a2); ttri.Add(a2); ttri.Add(a1 + 1); ttri.Add(a2 + 1); }
                placed++;
            }
            var leafCol = conifer ? new Color(0.12f, 0.27f, 0.15f) : new Color(0.19f, 0.42f, 0.21f);
            var treesGo = MeshObj("Trees", crown, cuv, ctri, Mat(leafCol)); treesGo.transform.SetParent(root);
            BakeTint.Add(treesGo.GetComponent<MeshFilter>().sharedMesh, ctint.ToArray());
            MeshObj("Trunks", trunkV, tuv, ttri, Mat(new Color(0.36f, 0.25f, 0.19f))).transform.SetParent(root);
        }
        Drape(root.gameObject);
        var late = Trackside.Late; // fences and ground decals: lit by the bake, but kept out of its shadow map
        if (late != null) Drape(late);
        // lighting baked into the vertices (LightBake.cs); if the phone can't run that shader, the sun and
        // real-time shadows are used as before
        if (LightBake.Begin(t, -sunGo.transform.forward))
        {
            LightBake.AddOccluders(root.gameObject);
            if (late != null) late.transform.SetParent(root, false);
            LightBake.Apply(root.gameObject);
            sun.shadows = LightShadows.None; sun.cullingMask = ~(1 << LightBake.CarLayer);
            var carSunGo = new GameObject("CarSun"); carSunGo.transform.SetParent(root); carSunGo.transform.rotation = sunGo.transform.rotation;
            var cs2 = carSunGo.AddComponent<Light>(); cs2.type = LightType.Directional; cs2.color = sun.color; cs2.intensity = sun.intensity;
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
        StaticBatchingUtility.Combine(root.gameObject);

        // one reflection capture of sky + ground, rendered once: cheap, and makes the paint look real
        QualitySettings.realtimeReflectionProbes = true;
        var probeGo = new GameObject("SkyReflection"); probeGo.transform.SetParent(root); probeGo.transform.position = new Vector3(cx, 2, cy);
        var probe = probeGo.AddComponent<ReflectionProbe>();
        probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
        probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
        probe.size = new Vector3(5000, 2000, 5000); probe.resolution = 128; probe.hdr = false;
        // sky only (skybox + the cloud/haze layer): grass reflected in the glossy paint showed as green patches
        probe.cullingMask = 1 << Sky.Layer; probe.farClipPlane = 3000;
        // clouds + sun glare (camera-following, so made after static batching); renders the probe once, clouds included
        Sky.Create(root, skyLook, -sunGo.transform.forward, probe);
        // what the car paint reflects below the horizon (the road), and full reflections unless in shade
        Shader.SetGlobalColor("_GroundRefl", Street(t) ? new Color(0.15f, 0.15f, 0.16f) : new Color(0.13f, 0.16f, 0.12f));
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
