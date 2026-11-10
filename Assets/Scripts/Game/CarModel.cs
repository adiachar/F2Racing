// CarModel.cs — formula car built from code: lofted (smooth, shaped) bodywork, layered wings, halo,
// suspension arms, proper tyres and rims, driver helmet. Forward = +z, right = +x, origin at CG on the ground.
// Dimensions follow the F2 car (length ~5.2 m, width 1.9 m, wheelbase 3.135 m).
// Draw calls are kept low: every part sharing a material is merged into one mesh.
using System.Collections.Generic;
using UnityEngine;

public class CarModel : MonoBehaviour
{
    public Transform body;
    public GameObject cockpitFloor;
    public Transform[] frontPivots = new Transform[2], frontSpin = new Transform[2], rearSpin = new Transform[2];
    public Transform helmet;

    // ------------------------------------------------------------------ mesh building helpers
    class MeshBuf
    {
        public readonly List<Vector3> v = new List<Vector3>(); public readonly List<Vector3> n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>(); public readonly List<int> t = new List<int>();
        public void Add(Mesh m, Matrix4x4 xf)
        {
            int o = v.Count; var nm = xf.inverse.transpose;
            foreach (var p in m.vertices) v.Add(xf.MultiplyPoint3x4(p));
            foreach (var q in m.normals) n.Add(nm.MultiplyVector(q).normalized);
            var u = m.uv; for (int i = 0; i < m.vertexCount; i++) uv.Add(i < u.Length ? u[i] : Vector2.zero);
            foreach (var i in m.triangles) t.Add(o + i);
        }
        public Mesh Build()
        {
            var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(t, 0); m.RecalculateBounds();
            return m;
        }
    }

    // Section of a lofted body: z = position along the car, w = half width, y0/y1 = bottom/top, r = roundness (2 = ellipse, 6 = boxy)
    struct Sec { public float z, w, y0, y1, r; public Sec(float z, float w, float y0, float y1, float r = 3.5f) { this.z = z; this.w = w; this.y0 = y0; this.y1 = y1; this.r = r; } }

    // Smooth tube through the sections (superellipse cross-sections), capped at both ends.
    static Mesh Loft(Sec[] s, float xOffset = 0, int ring = 20)
    {
        var v = new List<Vector3>(); var tri = new List<int>();
        for (int i = 0; i < s.Length; i++)
        {
            float cx = xOffset, cy = (s[i].y0 + s[i].y1) / 2, hh = (s[i].y1 - s[i].y0) / 2, e = 2f / s[i].r;
            for (int k = 0; k < ring; k++)
            {
                float a = k * Mathf.PI * 2 / ring, c = Mathf.Cos(a), sn = Mathf.Sin(a);
                v.Add(new Vector3(cx + s[i].w * Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), e), cy + hh * Mathf.Sign(sn) * Mathf.Pow(Mathf.Abs(sn), e), s[i].z));
            }
        }
        for (int i = 0; i < s.Length - 1; i++)
            for (int k = 0; k < ring; k++)
            {
                int a = i * ring + k, b = i * ring + (k + 1) % ring, c = a + ring, d = b + ring;
                // sections run from front (+z) to back: wind so normals point outward
                tri.Add(a); tri.Add(b); tri.Add(c); tri.Add(b); tri.Add(d); tri.Add(c);
            }
        // end caps
        int f = v.Count; v.Add(new Vector3(xOffset, (s[0].y0 + s[0].y1) / 2, s[0].z));
        int bk = v.Count; var L = s[s.Length - 1]; v.Add(new Vector3(xOffset, (L.y0 + L.y1) / 2, L.z));
        int last = (s.Length - 1) * ring;
        for (int k = 0; k < ring; k++) { int k1 = (k + 1) % ring; tri.Add(f); tri.Add(k1); tri.Add(k); tri.Add(bk); tri.Add(last + k); tri.Add(last + k1); }
        var m = new Mesh(); m.SetVertices(v); m.SetTriangles(tri, 0);
        // make sure normals face out whatever the section order produced
        m.RecalculateNormals();
        var nn = m.normals; var vv = m.vertices; float dot = 0; Vector3 ctr = m.bounds.center;
        for (int i = 0; i < vv.Length; i++) dot += Vector3.Dot(nn[i], vv[i] - ctr);
        if (dot < 0) { var tt = m.triangles; for (int i = 0; i < tt.Length; i += 3) { (tt[i + 1], tt[i + 2]) = (tt[i + 2], tt[i + 1]); } m.triangles = tt; m.RecalculateNormals(); }
        return m;
    }

    static Mesh prim(PrimitiveType t) { var g = GameObject.CreatePrimitive(t); var m = g.GetComponent<MeshFilter>().sharedMesh; Destroy(g); return m; }
    static Mesh cube, cyl, sphere;
    static Matrix4x4 TRS(Vector3 p, Vector3 euler, Vector3 s) => Matrix4x4.TRS(p, Quaternion.Euler(euler), s);
    // thin rod between two points (suspension arms, pushrods)
    static Matrix4x4 Rod(Vector3 a, Vector3 b, float d) => Matrix4x4.TRS((a + b) / 2, Quaternion.FromToRotation(Vector3.up, b - a), new Vector3(d, (b - a).magnitude / 2, d));

    // tyre: lathe around the x axis with rounded shoulders; rim: dished disc with spokes
    static Mesh Tyre(float R, float width, float rimR)
    {
        var prof = new List<Vector2>(); // (x across tread, radius)
        int sh = 6; float r = Mathf.Min(0.06f, width * 0.2f);
        prof.Add(new Vector2(-width / 2 + 0.01f, rimR));
        for (int i = 0; i <= sh; i++) { float a = Mathf.PI / 2 * i / sh; prof.Add(new Vector2(-width / 2 + r - r * Mathf.Cos(a), R - r + r * Mathf.Sin(a))); }
        for (int i = 0; i <= sh; i++) { float a = Mathf.PI / 2 * i / sh; prof.Add(new Vector2(width / 2 - r + r * Mathf.Sin(a), R - r + r * Mathf.Cos(a))); }
        prof.Add(new Vector2(width / 2 - 0.01f, rimR));
        int seg = 28; var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
        for (int i = 0; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2 / seg;
            for (int j = 0; j < prof.Count; j++) { v.Add(new Vector3(prof[j].x, Mathf.Cos(a) * prof[j].y, Mathf.Sin(a) * prof[j].y)); uv.Add(new Vector2((float)j / (prof.Count - 1), (float)i / seg)); }
        }
        int P = prof.Count;
        for (int i = 0; i < seg; i++) for (int j = 0; j < P - 1; j++)
            { int a = i * P + j, b = a + 1, c = a + P, d = c + 1; t.Add(a); t.Add(c); t.Add(b); t.Add(b); t.Add(c); t.Add(d); }
        var m = new Mesh(); m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0); m.RecalculateNormals(); return m;
    }

    // ------------------------------------------------------------------ car
    public static CarModel Create(double wheelbase, double frontWeight, double wheelRadius)
    {
        cube ??= prim(PrimitiveType.Cube); cyl ??= prim(PrimitiveType.Cylinder); sphere ??= prim(PrimitiveType.Sphere);
        var root = new GameObject("Car");
        var cm = root.AddComponent<CarModel>();
        cm.body = new GameObject("Body").transform; cm.body.SetParent(root.transform, false);
        float L = (float)wheelbase, b = L * (float)frontWeight, a = L - b, R = (float)wheelRadius;

        // materials: glossy metallic paint reflects the sky probe; carbon is satin; accents in orange
        var paint = WorldBuilder.Mat(new Color(0.06f, 0.55f, 0.62f), null, 0.86f, 0.45f);
        var accent = WorldBuilder.Mat(new Color(1f, 0.45f, 0.08f), null, 0.82f, 0.3f);
        var carbon = WorldBuilder.Mat(new Color(0.07f, 0.075f, 0.085f), null, 0.62f, 0.2f);
        var white = WorldBuilder.Mat(new Color(0.92f, 0.93f, 0.94f), null, 0.8f, 0.1f);
        var dark = WorldBuilder.Mat(new Color(0.02f, 0.02f, 0.025f), null, 0.9f, 0.4f);

        var P = new MeshBuf(); var A = new MeshBuf(); var C = new MeshBuf(); var Wt = new MeshBuf(); var D = new MeshBuf();

        // monocoque: nose tip -> cockpit -> engine cover -> gearbox
        P.Add(Loft(new[] {
            new Sec(a + 1.02f, 0.09f, 0.15f, 0.27f, 2.4f),
            new Sec(a + 0.65f, 0.14f, 0.13f, 0.36f, 2.8f),
            new Sec(a + 0.05f, 0.21f, 0.12f, 0.46f, 3.2f),
            new Sec(a - 0.55f, 0.29f, 0.10f, 0.56f, 3.6f),
            new Sec(0.55f, 0.35f, 0.08f, 0.62f, 4f),
            new Sec(0.0f, 0.37f, 0.08f, 0.64f, 4f),
            new Sec(-0.45f, 0.31f, 0.09f, 0.98f, 3.2f), // airbox behind the driver's head
            new Sec(-0.85f, 0.25f, 0.10f, 0.84f, 3f),
            new Sec(-1.35f, 0.20f, 0.11f, 0.62f, 3f),
            new Sec(-1.9f, 0.12f, 0.13f, 0.44f, 2.8f),
            new Sec(-2.25f, 0.07f, 0.16f, 0.34f, 2.5f),
        }), Matrix4x4.identity);
        // engine-cover fin (accent)
        A.Add(cube, TRS(new Vector3(0, 0.86f, -1.25f), new Vector3(-8, 0, 0), new Vector3(0.012f, 0.22f, 1.3f)));
        // sidepods with inlets
        foreach (float sx in new[] { 1f, -1f })
        {
            P.Add(Loft(new[] {
                new Sec(0.82f, 0.13f, 0.12f, 0.50f, 4f),
                new Sec(0.55f, 0.20f, 0.10f, 0.55f, 4f),
                new Sec(-0.2f, 0.21f, 0.10f, 0.50f, 3.6f),
                new Sec(-0.9f, 0.15f, 0.11f, 0.38f, 3f),
                new Sec(-1.45f, 0.06f, 0.12f, 0.26f, 2.5f),
            }, sx * 0.57f), Matrix4x4.identity);
            D.Add(cube, TRS(new Vector3(sx * 0.57f, 0.32f, 0.83f), Vector3.zero, new Vector3(0.22f, 0.32f, 0.02f))); // inlet mouth
            A.Add(cube, TRS(new Vector3(sx * 0.79f, 0.33f, 0.2f), Vector3.zero, new Vector3(0.006f, 0.12f, 0.9f))); // sidepod stripe
        }
        // floor with edges, diffuser
        C.Add(cube, TRS(new Vector3(0, 0.055f, -0.25f), Vector3.zero, new Vector3(1.42f, 0.02f, 2.6f)));
        C.Add(cube, TRS(new Vector3(0, 0.12f, -1.75f), new Vector3(-14, 0, 0), new Vector3(1.0f, 0.02f, 0.6f)));
        // halo: arc over the cockpit + centre pillar
        {
            int seg = 14; float hr = 0.31f, cy = 0.72f, cz = 0.33f;
            for (int i = 0; i < seg; i++)
            {
                float a0 = Mathf.Lerp(-100, 100, (float)i / seg) * Mathf.Deg2Rad, a1 = Mathf.Lerp(-100, 100, (float)(i + 1) / seg) * Mathf.Deg2Rad;
                var p0 = new Vector3(Mathf.Sin(a0) * hr, cy, cz + Mathf.Cos(a0) * hr - 0.05f); var p1 = new Vector3(Mathf.Sin(a1) * hr, cy, cz + Mathf.Cos(a1) * hr - 0.05f);
                C.Add(cyl, Rod(p0, p1, 0.045f));
            }
            C.Add(cyl, Rod(new Vector3(0, 0.58f, cz + 0.62f), new Vector3(0, cy, cz + 0.26f), 0.05f));
            C.Add(cyl, Rod(new Vector3(0.3f, 0.6f, cz - 0.08f), new Vector3(0.3f, cy, cz - 0.1f), 0.04f));
            C.Add(cyl, Rod(new Vector3(-0.3f, 0.6f, cz - 0.08f), new Vector3(-0.3f, cy, cz - 0.1f), 0.04f));
        }
        // mirrors
        foreach (float sx in new[] { 1f, -1f })
        {
            C.Add(cyl, Rod(new Vector3(sx * 0.3f, 0.55f, 0.75f), new Vector3(sx * 0.45f, 0.64f, 0.72f), 0.02f));
            P.Add(cube, TRS(new Vector3(sx * 0.47f, 0.64f, 0.72f), Vector3.zero, new Vector3(0.14f, 0.07f, 0.05f)));
        }
        // front wing: main plane + two flaps + endplates + pylons
        float fz = a + 0.9f;
        C.Add(cube, TRS(new Vector3(0, 0.085f, fz), new Vector3(-3, 0, 0), new Vector3(1.9f, 0.018f, 0.36f)));
        A.Add(cube, TRS(new Vector3(0.52f, 0.13f, fz - 0.12f), new Vector3(-16, 0, 0), new Vector3(0.82f, 0.012f, 0.18f)));
        A.Add(cube, TRS(new Vector3(-0.52f, 0.13f, fz - 0.12f), new Vector3(-16, 0, 0), new Vector3(0.82f, 0.012f, 0.18f)));
        P.Add(cube, TRS(new Vector3(0.52f, 0.18f, fz - 0.2f), new Vector3(-28, 0, 0), new Vector3(0.8f, 0.01f, 0.12f)));
        P.Add(cube, TRS(new Vector3(-0.52f, 0.18f, fz - 0.2f), new Vector3(-28, 0, 0), new Vector3(0.8f, 0.01f, 0.12f)));
        foreach (float sx in new[] { 0.95f, -0.95f }) C.Add(cube, TRS(new Vector3(sx, 0.16f, fz - 0.05f), Vector3.zero, new Vector3(0.02f, 0.2f, 0.55f)));
        foreach (float sx in new[] { 0.09f, -0.09f }) C.Add(cube, TRS(new Vector3(sx, 0.16f, fz + 0.02f), Vector3.zero, new Vector3(0.015f, 0.14f, 0.22f)));
        // rear wing: two elements + big endplates + beam wing + pillar
        float rz = -b - 0.75f;
        C.Add(cube, TRS(new Vector3(0, 0.84f, rz), new Vector3(-6, 0, 0), new Vector3(1.0f, 0.025f, 0.32f)));
        A.Add(cube, TRS(new Vector3(0, 0.93f, rz - 0.14f), new Vector3(-22, 0, 0), new Vector3(1.0f, 0.02f, 0.2f)));
        foreach (float sx in new[] { 0.51f, -0.51f }) P.Add(cube, TRS(new Vector3(sx, 0.78f, rz - 0.04f), Vector3.zero, new Vector3(0.025f, 0.46f, 0.6f)));
        C.Add(cube, TRS(new Vector3(0, 0.36f, rz + 0.08f), new Vector3(-10, 0, 0), new Vector3(0.95f, 0.02f, 0.2f)));
        C.Add(cube, TRS(new Vector3(0, 0.6f, rz + 0.2f), new Vector3(20, 0, 0), new Vector3(0.05f, 0.5f, 0.12f)));
        // rain light
        D.Add(cube, TRS(new Vector3(0, 0.3f, -2.3f), Vector3.zero, new Vector3(0.1f, 0.06f, 0.02f)));

        // suspension: double wishbones + pushrod to each wheel
        foreach (float sx in new[] { 1f, -1f })
            foreach (var (zc, chW) in new[] { (a, 0.2f), (-b, 0.24f) })
            {
                var hubLo = new Vector3(sx * 0.68f, 0.2f, zc); var hubHi = new Vector3(sx * 0.66f, 0.44f, zc);
                foreach (float dz in new[] { 0.22f, -0.22f })
                {
                    C.Add(cyl, Rod(new Vector3(sx * chW, 0.16f, zc + dz), hubLo, 0.028f));
                    C.Add(cyl, Rod(new Vector3(sx * chW, 0.4f, zc + dz), hubHi, 0.028f));
                }
                C.Add(cyl, Rod(hubLo + new Vector3(0, 0.02f, 0), new Vector3(sx * chW, 0.5f, zc + 0.05f), 0.024f));
            }

        void Part(MeshBuf buf, Material m, string name, bool shadows = true)
        {
            if (buf.v.Count == 0) return;
            var go = new GameObject(name); go.transform.SetParent(cm.body, false);
            go.AddComponent<MeshFilter>().sharedMesh = buf.Build();
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = m;
            r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        Part(P, paint, "Paint"); Part(A, accent, "Accent"); Part(C, carbon, "Carbon"); Part(D, dark, "Dark");

        // driver: helmet with visor
        {
            var h = new GameObject("Helmet"); h.transform.SetParent(cm.body, false);
            h.transform.localPosition = new Vector3(0, 0.74f, 0.12f);
            var hb = new MeshBuf();
            hb.Add(sphere, TRS(Vector3.zero, Vector3.zero, new Vector3(0.27f, 0.29f, 0.31f)));
            h.AddComponent<MeshFilter>().sharedMesh = hb.Build();
            h.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.Mat(new Color(1f, 0.82f, 0.18f), null, 0.9f, 0.2f);
            var vis = new GameObject("Visor"); vis.transform.SetParent(h.transform, false);
            var vb = new MeshBuf(); vb.Add(cube, TRS(new Vector3(0, 0.02f, 0.12f), new Vector3(-8, 0, 0), new Vector3(0.22f, 0.07f, 0.1f)));
            vis.AddComponent<MeshFilter>().sharedMesh = vb.Build(); vis.AddComponent<MeshRenderer>().sharedMaterial = dark;
            cm.helmet = h.transform;
        }

        // wheels: tyre (with coloured sidewall band) + rim + spokes
        var tyreMat = WorldBuilder.Mat(Color.white, WorldBuilder.TyreTex(), 0.25f);
        var rimMat = WorldBuilder.Mat(new Color(0.16f, 0.17f, 0.19f), null, 0.75f, 0.85f);
        Transform Wheel(float x, float z, float width, out Transform spin)
        {
            var pivot = new GameObject("WheelPivot").transform; pivot.SetParent(root.transform, false); pivot.localPosition = new Vector3(x, R, z);
            spin = new GameObject("Spin").transform; spin.SetParent(pivot, false);
            var tg = new GameObject("Tyre"); tg.transform.SetParent(spin, false);
            tg.AddComponent<MeshFilter>().sharedMesh = Tyre(R, width, R * 0.62f);
            var tr = tg.AddComponent<MeshRenderer>(); tr.sharedMaterial = tyreMat;
            var rb = new MeshBuf();
            rb.Add(cyl, TRS(Vector3.zero, new Vector3(0, 0, 90), new Vector3(R * 1.24f, width / 2 - 0.02f, R * 1.24f)));
            float side = Mathf.Sign(x) * (width / 2 - 0.015f);
            for (int k = 0; k < 6; k++) rb.Add(cube, TRS(new Vector3(side, 0, 0), new Vector3(k * 30, 0, 0), new Vector3(0.02f, R * 1.15f, 0.045f)));
            rb.Add(cyl, TRS(new Vector3(side, 0, 0), new Vector3(0, 0, 90), new Vector3(0.11f, 0.03f, 0.11f)));
            var rg = new GameObject("Rim"); rg.transform.SetParent(spin, false);
            rg.AddComponent<MeshFilter>().sharedMesh = rb.Build(); rg.AddComponent<MeshRenderer>().sharedMaterial = rimMat;
            return pivot;
        }
        cm.frontPivots[0] = Wheel(0.8f, a, 0.3f, out cm.frontSpin[0]);
        cm.frontPivots[1] = Wheel(-0.8f, a, 0.3f, out cm.frontSpin[1]);
        Wheel(0.78f, -b, 0.4f, out cm.rearSpin[0]);
        Wheel(-0.78f, -b, 0.4f, out cm.rearSpin[1]);

        // soft contact shadow under the car (always on, also when real-time shadows are off on low-end phones)
        var sh = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(sh.GetComponent<Collider>());
        sh.name = "ContactShadow"; sh.transform.SetParent(root.transform, false);
        sh.transform.localPosition = new Vector3(0, 0.035f, -0.15f); sh.transform.localRotation = Quaternion.Euler(90, 0, 0);
        sh.transform.localScale = new Vector3(2.5f, 5.6f, 1);
        var sr = sh.GetComponent<MeshRenderer>(); sr.sharedMaterial = WorldBuilder.ShadowMat(); sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return cm;
    }

    // ------------------------------------------------------------------ imported car (Resources/Car/car.bytes)
    // Made by Tools/convert_car_model.py from "2026 Ferrari SF-26" by Tyler_Dave (Sketchfab, CC BY 4.0):
    // logos, decals, numbers and cockpit interior removed, reduced to ~53k triangles, own red/white paint.
    public const string Credit = "Car model based on \"2026 Ferrari SF-26\" by Tyler_Dave (Sketchfab), CC BY 4.0. Modified: logos removed, simplified, repainted.";
    public static CarModel CreateFromAsset()
    {
        var ta = Resources.Load<TextAsset>("Car/car");
        if (ta == null) return null;
        var d = ta.bytes; int o = 0;
        int I() { int v = System.BitConverter.ToInt32(d, o); o += 4; return v; }
        float F() { float v = System.BitConverter.ToSingle(d, o); o += 4; return v; }
        if (d.Length < 8 || d[0] != 'F' || d[1] != '2' || d[2] != 'M' || d[3] != '1') return null;
        o = 4; int n = I();
        var mats = new[] {
            WorldBuilder.Mat(new Color(0.78f, 0.05f, 0.07f), null, 0.88f, 0.35f),   // 0 red paint
            WorldBuilder.Mat(new Color(0.93f, 0.93f, 0.94f), null, 0.86f, 0.15f),   // 1 white paint
            WorldBuilder.Mat(new Color(0.07f, 0.075f, 0.085f), null, 0.6f, 0.25f),  // 2 carbon
            WorldBuilder.Mat(new Color(0.55f, 0.57f, 0.6f), null, 0.8f, 0.9f),      // 3 metal
            WorldBuilder.Mat(new Color(0.045f, 0.045f, 0.05f), null, 0.22f),        // 4 tyre rubber
            WorldBuilder.Mat(new Color(0.2f, 0.21f, 0.23f), null, 0.75f, 0.85f),    // 5 rims / discs
            WorldBuilder.Mat(new Color(1f, 0.15f, 0.12f), null, 0.9f),              // 6 rain light
        };
        var root = new GameObject("Car");
        // the mesh was converted at Formula 2 size (3.135 m wheelbase); a 2026 F1 car is 3.40 m long between the axles and 1.9 m wide
        root.transform.localScale = Vector3.one * (3.40f / 3.135f);
        var cm = root.AddComponent<CarModel>();
        cm.body = new GameObject("Body").transform; cm.body.SetParent(root.transform, false);
        var wheels = new System.Collections.Generic.Dictionary<string, Transform>();
        for (int p = 0; p < n; p++)
        {
            int len = I(); string name = System.Text.Encoding.UTF8.GetString(d, o, len); o += len;
            int mid = I(); var piv = new Vector3(F(), F(), F());
            int nv = I(), ni = I();
            var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var idx = new int[ni];
            for (int i = 0; i < nv; i++) pos[i] = new Vector3(F(), F(), F());
            for (int i = 0; i < nv; i++) nrm[i] = new Vector3(F(), F(), F());
            for (int i = 0; i < ni; i++) idx[i] = I();
            var mesh = new Mesh { indexFormat = nv > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.vertices = pos; mesh.normals = nrm; mesh.triangles = idx; mesh.RecalculateBounds();
            var go = new GameObject(name);
            Transform parent = cm.body;
            if (name.StartsWith("wheel_"))
            {
                string key = name.Substring(6, 2); // FL, FR, RL, RR
                if (!wheels.TryGetValue(key, out var spin))
                {
                    var pivot = new GameObject("WheelPivot_" + key).transform; pivot.SetParent(root.transform, false); pivot.localPosition = piv;
                    spin = new GameObject("Spin").transform; spin.SetParent(pivot, false);
                    wheels[key] = spin;
                    if (key[0] == 'F') { int k = key[1] == 'L' ? 0 : 1; cm.frontPivots[k] = pivot; cm.frontSpin[k] = spin; }
                    else cm.rearSpin[key[1] == 'L' ? 0 : 1] = spin;
                }
                // calipers/uprights only steer; rim and tyre also spin
                parent = name.EndsWith("_hubstatic") ? spin.parent : spin;
            }
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mats[Mathf.Clamp(mid, 0, mats.Length - 1)];
        }
        // the model has no driver: add a helmet in the cockpit (hidden in cockpit view)
        {
            var h = GameObject.CreatePrimitive(PrimitiveType.Sphere); Destroy(h.GetComponent<Collider>());
            h.name = "Helmet"; h.transform.SetParent(cm.body, false);
            h.transform.localPosition = new Vector3(0, 0.72f, 0.05f); h.transform.localScale = new Vector3(0.26f, 0.28f, 0.3f);
            h.GetComponent<MeshRenderer>().sharedMaterial = WorldBuilder.Mat(new Color(1f, 0.82f, 0.18f), null, 0.9f, 0.2f);
            cm.helmet = h.transform;
        }
        cm.cockpitFloor = CockpitTub(cm.body);
        var sh = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(sh.GetComponent<Collider>());
        sh.name = "ContactShadow"; sh.transform.SetParent(root.transform, false);
        sh.transform.localPosition = new Vector3(0, 0.035f, 0.3f); sh.transform.localRotation = Quaternion.Euler(90, 0, 0);
        sh.transform.localScale = new Vector3(2.4f, 5.6f, 1);
        var sr = sh.GetComponent<MeshRenderer>(); sr.sharedMaterial = WorldBuilder.ShadowMat(); sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return cm;
    }

    // A dark, inward-facing tub (floor, side walls, front bulkhead) inside the cockpit. Only shown in the
    // cockpit camera, so the driver never sees through the model to the car's underside or the road.
    static GameObject CockpitTub(Transform body)
    {
        float x0 = -0.34f, x1 = 0.34f, y0 = 0.22f, y1 = 0.5f, z0 = -0.25f, z1 = 1.05f;
        var v = new[] {
            // floor (faces up)
            new Vector3(x0,y0,z0), new Vector3(x0,y0,z1), new Vector3(x1,y0,z1), new Vector3(x1,y0,z0),
            // left wall (faces +x, inward)
            new Vector3(x0,y0,z0), new Vector3(x0,y1,z0), new Vector3(x0,y1,z1), new Vector3(x0,y0,z1),
            // right wall (faces -x)
            new Vector3(x1,y0,z1), new Vector3(x1,y1,z1), new Vector3(x1,y1,z0), new Vector3(x1,y0,z0),
            // front bulkhead (faces -z, toward the driver)
            new Vector3(x0,y0,z1), new Vector3(x0,0.42f,z1), new Vector3(x1,0.42f,z1), new Vector3(x1,y0,z1),
        };
        var t = new int[24];
        for (int q = 0; q < 4; q++) { int b = q * 4, i = q * 6; t[i] = b; t[i + 1] = b + 1; t[i + 2] = b + 2; t[i + 3] = b; t[i + 4] = b + 2; t[i + 5] = b + 3; }
        var m = new Mesh { vertices = v, triangles = t }; m.RecalculateNormals(); m.RecalculateBounds();
        var go = new GameObject("CockpitTub"); go.transform.SetParent(body, false);
        go.AddComponent<MeshFilter>().sharedMesh = m;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = WorldBuilder.Mat(new Color(0.035f, 0.035f, 0.04f), null, 0.15f, 0f);
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.SetActive(false);
        return go;
    }

    // physics -> visuals. heading: rad CCW from +x (physics); delta: + = left; spin: wheel angle (rad)
    public void Apply(double x, double y, double heading, double delta, double frontSpinRad, double rearSpinRad, double axLong, double ay)
    {
        transform.position = new Vector3((float)x, 0, (float)y);
        transform.rotation = Quaternion.Euler(0, 90f - (float)(heading * Mathf.Rad2Deg), 0);
        // body pitch/roll from g-forces: nose dives under braking, rolls to the outside in corners
        body.localRotation = Quaternion.Euler(Mathf.Clamp((float)-axLong * 0.09f, -1.7f, 1.7f), 0, Mathf.Clamp((float)-ay * 0.07f, -1.7f, 1.7f));
        float steer = -(float)(delta * Mathf.Rad2Deg);
        foreach (var p in frontPivots) p.localRotation = Quaternion.Euler(0, steer, 0);
        foreach (var s in frontSpin) s.localRotation = Quaternion.Euler((float)(frontSpinRad * Mathf.Rad2Deg), 0, 0);
        foreach (var s in rearSpin) s.localRotation = Quaternion.Euler((float)(rearSpinRad * Mathf.Rad2Deg), 0, 0);
    }
}
