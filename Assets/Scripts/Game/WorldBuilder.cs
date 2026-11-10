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
    public static Material Mat(Color c, Texture2D tex = null, float smooth = 0.1f, float metal = 0f)
    {
        if (baseMat == null) baseMat = Resources.Load<Material>("Base");
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
    public static Texture2D GrassTex() => Tex(256, 256, (u, v, x, y) =>
    {
        float r = Hash(x, y, 31);
        float k = 0.75f + Fbm(u, v, 6, 4, 9) * 0.35f + Fbm(u, v, 64, 2, 13) * 0.2f + (r > 0.9f ? 0.12f : r < 0.08f ? -0.12f : 0);
        return new Color32(B(62 * k), B(112 * k), B(44 * k), 255);
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
    public static GameObject MeshObj(string name, List<Vector3> v, List<Vector2> uv, List<int> tri, Material m, bool shadows = true)
    {
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(tri, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        var go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = shadows;
        return go;
    }

    public static GameObject Build(Track t)
    {
        var root = new GameObject("World").transform;
        float Wd = (float)t.width;
        var bb = t.Bounds();
        float cx = (float)(bb.minX + bb.maxX) / 2, cy = (float)(bb.minY + bb.maxY) / 2;
        float ext = (float)System.Math.Max(bb.maxX - bb.minX, bb.maxY - bb.minY);
        // test-drive pad sits just south of whichever circuit is loaded
        Pad = new Rect(cx - 400, (float)bb.minY - 300 - 480, 800, 480);

        // sky, light, fog: procedural skybox (sun, atmosphere), fog matched to its horizon
        var skyMat = Resources.Load<Material>("Sky");
        if (skyMat != null)
        {
            skyMat = new Material(skyMat);
            skyMat.SetFloat("_SunSize", 0.035f); skyMat.SetFloat("_SunSizeConvergence", 6f);
            skyMat.SetFloat("_AtmosphereThickness", 0.85f); skyMat.SetFloat("_Exposure", 1.25f);
            skyMat.SetColor("_SkyTint", new Color(0.5f, 0.6f, 0.75f)); skyMat.SetColor("_GroundColor", new Color(0.38f, 0.42f, 0.36f));
            RenderSettings.skybox = skyMat;
        }
        var sky = new Color(0.72f, 0.80f, 0.88f);
        RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear; RenderSettings.fogColor = sky;
        RenderSettings.fogStartDistance = 300; RenderSettings.fogEndDistance = 1800;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.62f, 0.72f, 0.85f);
        RenderSettings.ambientEquatorColor = new Color(0.55f, 0.6f, 0.6f);
        RenderSettings.ambientGroundColor = new Color(0.3f, 0.35f, 0.25f);
        var sunGo = new GameObject("Sun"); sunGo.transform.SetParent(root);
        var sun = sunGo.AddComponent<Light>(); sun.type = LightType.Directional; sun.color = new Color(1f, 0.95f, 0.86f); sun.intensity = 1.25f;
        sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.8f; sun.shadowBias = 0.03f; sun.shadowNormalBias = 0.3f;
        sunGo.transform.rotation = Quaternion.Euler(48, -35, 0);

        // ground
        var grassTex = GrassTex();
        var grass = Mat(Color.white, grassTex);
        float gsize = Mathf.Max(4400, ext + 3000);
        grass.mainTextureScale = new Vector2(gsize / 10, gsize / 10);
        var g = GameObject.CreatePrimitive(PrimitiveType.Quad); Object.Destroy(g.GetComponent<Collider>());
        g.name = "Ground"; g.transform.SetParent(root);
        g.transform.position = new Vector3(cx, -0.05f, cy - 400); g.transform.rotation = Quaternion.Euler(90, 0, 0); g.transform.localScale = new Vector3(gsize, gsize, 1);
        g.GetComponent<MeshRenderer>().sharedMaterial = grass;

        // road
        var asphalt = Mat(Color.white, AsphaltTex(), 0.15f);
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            Strip(t, 0, t.N, Wd / 2, -Wd / 2, 0.03f, 8, v, uv, tri);
            for (int i = 0; i < uv.Count; i++) uv[i] = new Vector2(uv[i].x * Wd / 4, uv[i].y);
            MeshObj("Road", v, uv, tri, asphalt).transform.SetParent(root);
        }
        // white edge lines
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            Strip(t, 0, t.N, Wd / 2 - 0.15f, Wd / 2 - 0.4f, 0.045f, 4, v, uv, tri);
            Strip(t, 0, t.N, -Wd / 2 + 0.4f, -Wd / 2 + 0.15f, 0.045f, 4, v, uv, tri);
            Strip(t, 0, 1, Wd / 2, -Wd / 2, 0.05f, 4, v, uv, tri); // start/finish line
            MeshObj("Lines", v, uv, tri, Mat(new Color(0.95f, 0.95f, 0.93f))).transform.SetParent(root);
        }
        // kerbs on corners (outer lip slightly raised)
        {
            var curvy = new bool[t.N];
            for (int i = 0; i < t.N; i++) curvy[i] = System.Math.Abs(t.pts[i].curv) > 1.0 / 280;
            var on = new bool[t.N];
            for (int i = 0; i < t.N; i++) for (int j = -8; j <= 8; j++) if (curvy[(i + j + t.N) % t.N]) { on[i] = true; break; }
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            int k = 0;
            while (k < t.N)
            {
                if (!on[k]) { k++; continue; }
                int j = k; while (j < t.N && on[j]) j++;
                Strip(t, k, j, Wd / 2, Wd / 2 + Kerb, 0.05f, 3, v, uv, tri, 0.09f);
                Strip(t, k, j, -Wd / 2, -(Wd / 2 + Kerb), 0.05f, 3, v, uv, tri, 0.09f);
                k = j;
            }
            MeshObj("Kerbs", v, uv, tri, Mat(Color.white, KerbTex())).transform.SetParent(root);
        }
        Trackside.Build(t, root, t.width < 10.0);
        Scenery.Build(t, root, t.width < 10.0); // street circuit (Monaco, 9 m wide) -> walls; others -> guardrails

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

        // simple trees (instanced-friendly: shared materials so they batch)
        {
            var leaf = Mat(new Color(0.19f, 0.42f, 0.21f)); var trunk = Mat(new Color(0.36f, 0.25f, 0.19f));
            var rnd = new System.Random(7); int placed = 0, tries = 0;
            int want = Mathf.Clamp((int)(ext / 6), 200, 450);
            while (placed < want && tries++ < want * 25)
            {
                double x = bb.minX - 350 + rnd.NextDouble() * (bb.maxX - bb.minX + 700), y = bb.minY - 250 + rnd.NextDouble() * (bb.maxY - bb.minY + 600);
                if (Pad.Contains(new Vector2((float)x, (float)y))) continue;
                if (t.Nearest(x, y).dist < 26) continue;
                float s = 0.7f + (float)rnd.NextDouble() * 0.8f;
                var tr = GameObject.CreatePrimitive(PrimitiveType.Cylinder); Object.Destroy(tr.GetComponent<Collider>());
                tr.transform.SetParent(root); tr.transform.position = new Vector3((float)x, 1.2f * s, (float)y); tr.transform.localScale = new Vector3(0.5f * s, 1.2f * s, 0.5f * s);
                tr.GetComponent<MeshRenderer>().sharedMaterial = trunk;
                var lf = GameObject.CreatePrimitive(PrimitiveType.Sphere); Object.Destroy(lf.GetComponent<Collider>());
                lf.transform.SetParent(root); lf.transform.position = new Vector3((float)x, 4.2f * s, (float)y); lf.transform.localScale = new Vector3(4.2f * s, 5.5f * s, 4.2f * s);
                lf.GetComponent<MeshRenderer>().sharedMaterial = leaf;
                placed++;
            }
        }
        StaticBatchingUtility.Combine(root.gameObject);

        // one reflection capture of sky + ground, rendered once: cheap, and makes the paint look real
        QualitySettings.realtimeReflectionProbes = true;
        var probeGo = new GameObject("SkyReflection"); probeGo.transform.SetParent(root); probeGo.transform.position = new Vector3(cx, 2, cy);
        var probe = probeGo.AddComponent<ReflectionProbe>();
        probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
        probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
        probe.size = new Vector3(5000, 2000, 5000); probe.resolution = 128; probe.hdr = false;
        probe.cullingMask = 0; probe.farClipPlane = 3000; // sky only: grass reflected in the glossy paint showed as green patches
        probe.RenderProbe();
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
