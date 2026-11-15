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
    public GameObject haloPillar; // hidden in the cockpit camera (a driver sees round it with two eyes)
    Transform steerWheel; float steerPitch;
    // turn the steering wheel in the cockpit: steer -1..1 (+ = right), about a third of a turn each way
    public void SetSteer(float steer) { if (steerWheel != null) steerWheel.localRotation = Quaternion.Euler(steerPitch, 0, -steer * 120f); }
    // the same, by the wheel's real angle in degrees (+ = turned left); used by DriverFeel with the real steering ratio
    public void SetSteerDeg(float deg) { if (steerWheel != null) steerWheel.localRotation = Quaternion.Euler(steerPitch, 0, deg); }
    public Transform SteerWheel => steerWheel; // null on the code-built fallback car (it has no steering wheel)
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
        cm.debrisSrc = carbon;
        cm.liveryBody = paint; cm.liverySecond = accent; // the code-built car: body = primary, accent parts = secondary
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
        foreach (var tr in root.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = LightBake.CarLayer; // cars have their own sun (LightBake)
        return cm;
    }

    // ------------------------------------------------------------------ imported car (Resources/Car/car.bytes)
    // Made by Tools/convert_car_model.py from "2026 Ferrari SF-26" by Tyler_Dave (Sketchfab, CC BY 4.0):
    // logos, decals, numbers and cockpit interior removed, reduced to ~53k triangles, own red/white paint.
    public const string CarCredit = "Car model based on \"2026 Ferrari SF-26\" by Tyler_Dave (Sketchfab), CC BY 4.0. Modified: logos removed, simplified, repainted.";
    // the credits line the menu and the race start show: the car model's licence and the map data's (ODbL / CDLA)
    public const string Credit = CarCredit + "  \u00b7  " + RealWorld.Attribution;
    static readonly System.Collections.Generic.Dictionary<string, Mesh[]> meshCache = new System.Collections.Generic.Dictionary<string, Mesh[]>();
    Material lightMat, stripeMat; bool braking;
    public void SetStripe(Color c) { stripeCol = c; hasStripe = true; if (stripeMat != null) stripeMat.color = c; if (merged) { mpb.SetColor(StripeId, c); Flush(); } }
    // performance (GameManagerPerf.cs): which asset this car came from (null = code-built), the last livery and
    // stripe set (so a swapped-in player LOD looks the same), and the Low graphics level (no wheel faces, no sparks/smoke)
    public string Res { get; private set; }
    public static bool LowDetail;
    Livery lastLivery; bool hasLivery, hasStripe, facesOff; Color stripeCol;
    public void CopyLook(CarModel from)
    {
        if (from == null) return;
        if (from.hasLivery) SetLivery(from.lastLivery);
        if (from.hasStripe) SetStripe(from.stripeCol);
    }

    // two-tone livery (Livery.cs): body = primary, split into secondary + accent line by CarPaint.shader; the wing
    // elements (the model's "white" parts: front wing, rear-wing flaps) take the secondary colour
    Material liveryBody, liverySecond;
    public void SetLivery(Livery l)
    {
        lastLivery = l; hasLivery = true;
        if (merged)
        {
            mpb.SetColor(LivAId, l.primary); mpb.SetColor(LivBId, l.secondary);
            var acc = l.accent; acc.a = 1f; mpb.SetColor(LivCId, acc); // a = 1: livery split on
            Flush(); return;
        }
        if (liveryBody != null)
        {
            liveryBody.color = l.primary;
            if (liveryBody.HasProperty("_Livery")) { liveryBody.SetFloat("_Livery", 1); liveryBody.SetColor("_Color2", l.secondary); liveryBody.SetColor("_Color3", l.accent); }
        }
        if (liverySecond != null) liverySecond.color = l.secondary;
    }
    // rear light glows when the driver brakes; the rain light and the wing-endplate lights blink in the wet
    // (SetRainLight) and while the MGU-K harvests (SetHarvest, or read from the car by Feed). Called every frame for
    // every car, replays included, so it also runs the light blink and, when Feed is not called (rivals without the
    // hook, replays), a simple brake-temperature estimate from the brake pedal for the disc glow.
    public void SetBrake(bool on)
    {
        braking = on;
        float dt = Time.deltaTime;
        if (Time.time - lastFeed > 0.5f && dt > 0)
        {
            // no real temperatures: heat while braking, cool towards ~250 C otherwise (close enough to look right)
            for (int k = 0; k < 2; k++) brakeT[k] += dt * ((on ? (k == 0 ? 520f : 420f) : 0f) - (brakeT[k] - 250f) * 0.45f);
            UpdateBrakeLook(dt);
        }
        UpdateLights();
    }
    bool rainLight, harvesting; int lightState = -1; static readonly int WetId = Shader.PropertyToID("_Wet");
    public void SetRainLight(bool on) { rainLight = on; UpdateLights(); }
    public void SetHarvest(bool on) { harvesting = on; UpdateLights(); }
    void UpdateLights()
    {
        if (lightMat == null && !merged) return;
        // wet: SetRainLight, or the global _Wet the weather sets for the shaders (no hook needed)
        bool wet = rainLight || Shader.GetGlobalFloat(WetId) > 0.3f;
        bool blink = (wet || harvesting) && Mathf.Repeat(Time.time * 4f, 1f) < 0.5f; // 4 Hz, like the real cars
        int st = braking || blink ? 2 : wet ? 1 : 0;
        if (st == lightState) return;
        lightState = st;
        var em = st == 2 ? new Color(3f, 0.25f, 0.2f) : st == 1 ? new Color(0.6f, 0.05f, 0.04f) : new Color(0.25f, 0.02f, 0.02f);
        if (merged) { mpb.SetColor(LightColId, em); mpbDirty = true; }
        else lightMat.SetColor("_EmissionColor", em);
    }

    // the car paint shader (CarPaint.shader); if a phone can't run it, the standard shader is used instead
    static Shader paintShader; static bool paintTried;
    static Shader PaintShader()
    {
        if (!paintTried) { paintTried = true; paintShader = Resources.Load<Shader>("CarPaint"); if (paintShader != null && !paintShader.isSupported) paintShader = null; }
        return paintShader;
    }
    // a colour given in linear (the value the shader should get): Material.color / SetColor take sRGB in this
    // linear-colour-space project and convert it, so hand them the sRGB equivalent
    static Color Lin(float r, float g, float b) => new Color(r, g, b).gamma;
    static Material Paint(Color c, float coat, float gloss, float metal, float stdSmooth, float stdMetal)
    {
        PaintShader();
        if (paintShader == null) return WorldBuilder.Mat(c, null, stdSmooth, stdMetal);
        var m = new Material(paintShader) { color = c };
        m.SetFloat("_Coat", coat); m.SetFloat("_Gloss", gloss); m.SetFloat("_Metal", metal);
        return m;
    }

    // res: "Car/car" (full detail, the player's car) or "Car/car_lod" (light copy for rivals); paint: body colour
    // the coloured compound stripe on both sidewalls: a clean ring at 79-86% of the tyre radius, laid just
    // outside the sidewall (found from the tyre's own vertices), turning with the wheel
    static readonly System.Collections.Generic.Dictionary<string, Mesh> stripeCache = new System.Collections.Generic.Dictionary<string, Mesh>();
    static void TyreStripes(Transform tyre, Vector3[] pos, int nv, Material m)
    {
        var mesh = StripeMesh(pos, nv); if (mesh == null) return;
        var go = new GameObject("Stripe"); go.transform.SetParent(tyre, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }
    static Mesh StripeMesh(Vector3[] pos, int nv)
    {
        float R = 0; for (int i = 0; i < nv; i++) R = Mathf.Max(R, new Vector2(pos[i].y, pos[i].z).magnitude);
        float r0 = 0.79f * R, r1 = 0.86f * R, xo = float.MinValue, xi = float.MaxValue;
        for (int i = 0; i < nv; i++)
        {
            float rr = new Vector2(pos[i].y, pos[i].z).magnitude;
            if (rr > r0 && rr < r1) { xo = Mathf.Max(xo, pos[i].x); xi = Mathf.Min(xi, pos[i].x); }
        }
        if (xo <= xi) return null;
        string key = R.ToString("F3") + xo.ToString("F3") + xi.ToString("F3");
        if (!stripeCache.TryGetValue(key, out var mesh))
        {
            const int Seg = 48;
            var v = new Vector3[(Seg + 1) * 4]; var nr = new Vector3[v.Length]; var t = new System.Collections.Generic.List<int>();
            for (int side = 0; side < 2; side++)
            {
                float x = side == 0 ? xo + 0.003f : xi - 0.003f; var n = new Vector3(side == 0 ? 1 : -1, 0, 0);
                int b = side * (Seg + 1) * 2;
                for (int k = 0; k <= Seg; k++)
                {
                    float a = k * Mathf.PI * 2 / Seg, c = Mathf.Cos(a), sn = Mathf.Sin(a);
                    v[b + k * 2] = new Vector3(x, c * r0, sn * r0); v[b + k * 2 + 1] = new Vector3(x, c * r1, sn * r1);
                    nr[b + k * 2] = n; nr[b + k * 2 + 1] = n;
                    if (k < Seg)
                    {
                        int q = b + k * 2;
                        if (side == 0) { t.Add(q); t.Add(q + 1); t.Add(q + 2); t.Add(q + 2); t.Add(q + 1); t.Add(q + 3); }
                        else { t.Add(q); t.Add(q + 2); t.Add(q + 1); t.Add(q + 2); t.Add(q + 3); t.Add(q + 1); }
                    }
                }
            }
            mesh = new Mesh { vertices = v, normals = nr }; mesh.SetTriangles(t, 0); mesh.RecalculateBounds();
            stripeCache[key] = mesh;
        }
        return mesh;
    }

    // Pirelli sidewall colours: Soft red, Medium yellow, Hard white
    public static Color StripeFor(FormulaSim.Compound c) => c == FormulaSim.Compound.Soft ? new Color(0.85f, 0.08f, 0.08f) : c == FormulaSim.Compound.Medium ? new Color(0.95f, 0.78f, 0.1f) : new Color(0.92f, 0.92f, 0.9f);

    // twinPaint: the body paint gets back faces too (the player's LOD: its simplified body has a few inward-facing
    // triangles that would show as holes up close; costs ~9k triangles, still a third of the full car)
    public static CarModel CreateFromAsset(string res = "Car/car", Color? paint = null, bool rival = false, Color? stripe = null, bool twinPaint = false)
    {
        if (rival && !twinPaint) { var mr = CreateMergedRival(res, paint, stripe); if (mr != null) return mr; } // few draw calls, shared material (below)
        var ta = Resources.Load<TextAsset>(res);
        if (ta == null) return null;
        var d = ta.bytes; int o = 0;
        if (res == "Car/car") Resources.UnloadAsset(ta); // .bytes is a copy: the full car's 6 MB need not stay loaded (the rivals' LODs are reloaded every race, so they stay)
        string cacheKey = twinPaint ? res + "|2s" : res;
        int I() { int v = System.BitConverter.ToInt32(d, o); o += 4; return v; }
        float F() { float v = System.BitConverter.ToSingle(d, o); o += 4; return v; }
        if (d.Length < 8 || d[0] != 'F' || d[1] != '2' || d[2] != 'M' || (d[3] != '1' && d[3] != '2')) return null;
        bool hasCol = d[3] == '2'; // F2M2: RGBA8 per vertex (R = baked AO, B = AO of the back face, G = brake-disc mask)
        o = 4; int n = I();
        // livery colours are sRGB (as picked); the rest are given in linear (Lin), the same values the merged rivals'
        // shader uses (CarPaint MMat), so a rival and the player's car look alike part for part
        var mats = new[] {
            Paint(paint ?? new Color(0.78f, 0.05f, 0.07f), 1f, 0.95f, 0.06f, 0.9f, 0.3f),    // 0 body paint: deep solid gloss
            Paint(new Color(0.93f, 0.93f, 0.94f), 1f, 0.93f, 0.03f, 0.88f, 0.15f),          // 1 white paint
            Paint(Lin(0.022f, 0.024f, 0.028f), 0.35f, 0.88f, 0.05f, 0.7f, 0.25f),            // 2 carbon (lacquered weave)
            Paint(Lin(0.45f, 0.46f, 0.5f), 0.3f, 0.85f, 0.95f, 0.85f, 0.9f),                 // 3 metal
            // 4 tyre rubber: near-black with a broad, soft sheen that brightens towards the rounded shoulders, so the
            // tyre reads as a thick rubber body rather than a flat black (or grey) shape
            Paint(Lin(0.015f, 0.015f, 0.016f), 0.1f, 0.35f, 0f, 0.25f, 0f),
            Paint(Lin(0.22f, 0.23f, 0.245f), 0.35f, 0.82f, 0.9f, 0.8f, 0.9f),                  // 5 rims / discs: gunmetal
            WorldBuilder.Glow(new Color(1f, 0.15f, 0.12f), new Color(0.25f, 0.02f, 0.02f)),  // 6 rear light
            Paint(stripe ?? StripeFor(FormulaSim.Compound.Soft), 0.25f, 0.5f, 0f, 0.3f, 0f),     // 7 tyre sidewall stripe
        };
        // detail variants of CarPaint.shader (one keyword each; see the shader). Front and rear tyres / hubs get their
        // own copies (wear, dirt, brake glow and dust differ per axle): every wheel part is its own renderer anyway,
        // so this adds no draw calls.
        Kind(mats[2], "_CARBON"); Kind(mats[1], "_CARBON"); mats[1].SetFloat("_Under", 1); // wing undersides: bare carbon
        if (mats[3].HasProperty("_Mirror")) mats[3].SetFloat("_Mirror", 1); // the metal part's faces at the mirrors: plain dark glass (no chrome, no render texture)
        var tyreF = Kind(mats[4], "_TYRE"); var tyreR = Kind(new Material(tyreF), "_TYRE");
        var hubF = Kind(mats[5], "_WHEEL"); var hubR = Kind(new Material(hubF), "_WHEEL");
        var steerMat = Paint(Lin(0.03f, 0.03f, 0.033f), 0.5f, 0.7f, 0.1f, 0.6f, 0.2f); // dark carbon rim (CarPaint MMat id 11)
        if (hasCol)
            foreach (var (mm, k) in new[] { (mats[0], 0.6f), (mats[1], 0.55f), (mats[2], 0.75f), (mats[3], 0.7f), (tyreF, 0.45f), (tyreR, 0.45f), (hubF, 0.7f), (hubR, 0.7f), (steerMat, 0.5f) })
                if (mm.HasProperty("_AO")) mm.SetFloat("_AO", k);
        meshCache.TryGetValue(cacheKey, out var cached); var made = cached == null ? new Mesh[n] : null;
        var root = new GameObject("Car");
        // the mesh was converted at Formula 2 size (3.135 m wheelbase); a 2026 F1 car is 3.40 m long between the axles and 1.9 m wide
        root.transform.localScale = Vector3.one * RootScale;
        var cm = root.AddComponent<CarModel>();
        cm.body = new GameObject("Body").transform; cm.body.SetParent(root.transform, false);
        var wheels = new System.Collections.Generic.Dictionary<string, Transform>();
        for (int p = 0; p < n; p++)
        {
            int len = I(); string name = System.Text.Encoding.UTF8.GetString(d, o, len); o += len;
            int mid = I(); var piv = new Vector3(F(), F(), F());
            if (name.StartsWith("wheel_")) piv.y = WheelY(name[6] == 'F'); // every tyre on the road (FitTyre)
            int nv = I(), ni = I();
            Vector3[] pos = null, nrm = null; int[] idx = null; Color32[] col = null;
            Mesh mesh;
            if (cached != null && !name.StartsWith("wheel_"))
            {
                // meshes already made (every rival after the first): skip the arrays instead of reading them again
                // (only the wheel parts' vertices are used below); saves ~2 MB of garbage per car at race start
                o += nv * 24 + (hasCol ? nv * 4 : 0) + ni * 4;
                mesh = cached[p];
            }
            else
            {
                pos = new Vector3[nv]; nrm = new Vector3[nv]; idx = new int[ni];
                for (int i = 0; i < nv; i++) pos[i] = new Vector3(F(), F(), F());
                for (int i = 0; i < nv; i++) nrm[i] = new Vector3(F(), F(), F());
                if (hasCol) { col = new Color32[nv]; for (int i = 0; i < nv; i++) { col[i] = new Color32(d[o], d[o + 1], d[o + 2], d[o + 3]); o += 4; } }
                for (int i = 0; i < ni; i++) idx[i] = I();
                if (name.EndsWith("_tyre")) FitTyre(pos, nrm, idx, name[6] == 'F');
                if (cached != null) mesh = cached[p];
                else
                {
                    // Thin parts (wings, floor, suspension, cockpit tub, mirrors) are single skins in the source model, and
                    // some face inwards: drawn one-sided they show as holes. Those get a twin of every triangle facing the
                    // other way. The painted body, tyres and rims are closed shapes and stay one-sided (half the work).
                    bool twoSided = TwoSided(mid, name) || (twinPaint && mid == 0);
                    Compact(ref pos, ref nrm, ref col, idx); nv = pos.Length; // some parts carry a whole shared vertex list (halo_pillar: 53k for 447 triangles)
                    if (!name.StartsWith("wheel_") && !name.StartsWith("steer_")) LowerBody(pos, nrm);
                    if (twoSided) Twin(ref pos, ref nrm, ref col, ref idx);
                    mesh = new Mesh { indexFormat = pos.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                    mesh.vertices = pos; mesh.normals = nrm; if (col != null) mesh.colors32 = col;
                    mesh.triangles = idx;
                    mesh.RecalculateBounds();
                    made[p] = mesh;
                }
            }
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
            if (name.StartsWith("steer_"))
            {
                // steering wheel: sits at its pivot, tilted back with the column; SetSteer turns it
                float.TryParse(name.Substring(6), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cm.steerPitch);
                go.transform.localPosition = piv; go.transform.localRotation = Quaternion.Euler(cm.steerPitch, 0, 0);
                cm.steerWheel = go.transform; mid = 5;
            }
            if (name == "halo_pillar") cm.haloPillar = go;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mat = mats[Mathf.Clamp(mid, 0, mats.Length - 1)];
            bool front = name.StartsWith("wheel_F");
            if (name.StartsWith("steer_")) mat = steerMat;
            else if (name.StartsWith("wheel_")) mat = name.EndsWith("_tyre") ? (front ? tyreF : tyreR) : (front ? hubF : hubR);
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
            if (name.EndsWith("_tyre"))
            {
                if (res != "Car/car_far") TyreStripes(go.transform, pos, nv, mats[7]); // far rivals (30 m+): the thin ring is under a pixel; saves 4 draw calls per car
                float tr = 0; for (int i = 0; i < nv; i++) tr = Mathf.Max(tr, new Vector2(pos[i].y, pos[i].z).magnitude);
                mat.SetFloat("_TyreR", tr); cm.tyreR = tr;
            }
            // 2026-style wheel cover + spin-blur face over the rim (player car only: 4 quads)
            if (name.EndsWith("_hub") && !rival) cm.WheelFace(go.transform.parent, pos, nv, piv.x, front);
            if (rival) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // rivals keep only the soft blob shadow
            // damage: the wings come off this part as separate pieces (front wing sides, endplates, rear wing)
            if (name == "carbon" || name == "white" || name == "paint_red") cm.SplitWings(go, cacheKey + "|" + name, mat, rival);
            if (mid == 2 && cm.debrisSrc == null) cm.debrisSrc = mat;
        }
        if (made != null) meshCache[cacheKey] = made;
        cm.Res = res;
        cm.lightMat = mats[6]; cm.stripeMat = mats[7]; cm.liveryBody = mats[0]; cm.liverySecond = mats[1];
        cm.tyreMat[0] = tyreF; cm.tyreMat[1] = tyreR; cm.hubMat[0] = hubF; cm.hubMat[1] = hubR; cm.rival = rival;
        // the model has no driver: add a helmet in the cockpit (hidden in cockpit view)
        {
            var h = GameObject.CreatePrimitive(PrimitiveType.Sphere); Destroy(h.GetComponent<Collider>());
            h.name = "Helmet"; h.transform.SetParent(cm.body, false);
            h.transform.localPosition = new Vector3(0, 0.72f, 0.05f); h.transform.localScale = new Vector3(0.26f, 0.28f, 0.3f);
            // glossy shell with a gold mirror visor and a thin dark stripe, all in the shader (no extra mesh or draw call)
            var hm = Kind(Paint(new Color(1f, 0.82f, 0.18f), 1f, 0.95f, 0.1f, 0.9f, 0.2f), "_HELMET");
            if (hm.HasProperty("_Color3")) hm.SetColor("_Color3", new Color(0.08f, 0.08f, 0.1f));
            h.GetComponent<MeshRenderer>().sharedMaterial = hm;
            cm.helmet = h.transform;
        }
        // (the model's own cockpit tub is drawn now, so the stand-in floor is no longer needed)
        var sh = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(sh.GetComponent<Collider>());
        sh.name = "ContactShadow"; sh.transform.SetParent(root.transform, false);
        sh.transform.localPosition = new Vector3(0, 0.035f, 0.3f); sh.transform.localRotation = Quaternion.Euler(90, 0, 0);
        sh.transform.localScale = new Vector3(2.4f, 5.6f, 1);
        // soft blob + a tighter, darker occlusion core under the floor + the four tyre contact patches, in one texture
        var sr = sh.GetComponent<MeshRenderer>(); sr.sharedMaterial = ContactShadowMat(); sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        foreach (var tr in root.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = LightBake.CarLayer; // cars have their own sun (LightBake)
        return cm;
    }

    // ------------------------------------------------------------------ rival cars: few draw calls, one shared material
    // Rivals (car_lod close by, car_far in the distance, the safety car) are not built part by part. The parts are
    // merged on the first rival and cached for all of them:
    //  * car_far: the WHOLE car in one mesh (wheels standing still: under ~20 px tall, nobody sees them turn or steer).
    //  * car_lod: one mesh for everything that does not move (body, wings, halo, mirrors, rear uprights, steering wheel,
    //    helmet) + per wheel one spinning mesh (tyre, rim, compound stripe) + on the fronts one steering-only mesh
    //    (caliper, upright). 7 renderers, all with the same mesh per slot on every rival.
    // Every vertex carries its part ID in the vertex colour's alpha (Id*, read by CarPaint.shader _MERGED); every car
    // shares ONE material, and what differs per car goes through a MaterialPropertyBlock with GPU-instanced properties.
    // The wing pieces for the damage are split off the merged body only when a wing is actually hit (MergedSplit).
    public const byte IdPaint = 0, IdWing = 1, IdCarbon = 2, IdMetal = 3, IdTyreF = 4, IdTyreR = 5, IdHubF = 6, IdHubR = 7,
        IdLight = 8, IdStripe = 9, IdHelmet = 10, IdSteer = 11;
    static readonly Vector3 HelmetPos = new Vector3(0, 0.72f, 0.05f), HelmetScale = new Vector3(0.26f, 0.28f, 0.3f);
    static readonly string[] WheelKeys = { "FL", "FR", "RL", "RR" };
    static readonly int LivAId = Shader.PropertyToID("_Color"), // livery primary: CarPaint _MERGED reads it as an instanced _Color
        LivBId = Shader.PropertyToID("_LivB"), LivCId = Shader.PropertyToID("_LivC"),
        GlowFId = Shader.PropertyToID("_GlowF"), GlowRId = Shader.PropertyToID("_GlowR"), LightColId = Shader.PropertyToID("_LightCol"),
        StripeId = Shader.PropertyToID("_StripeCol"), AxleId = Shader.PropertyToID("_Axle"), TyreWDId = Shader.PropertyToID("_TyreWD");
    sealed class RivalSet
    {
        public Mesh body; public readonly Mesh[] spin = new Mesh[4], steerOnly = new Mesh[4];
        public readonly Vector3[] piv = new Vector3[4]; public readonly float[] tyreR = { 0.33f, 0.33f };
        public Material mat; public bool far;
    }
    static readonly Dictionary<string, RivalSet> rivalCache = new Dictionary<string, RivalSet>();
    static Material rivalDebris;
    bool merged, mpbDirty, mSplit; MaterialPropertyBlock mpb; RivalSet rset; GameObject bodyGo; int blockBase;
    readonly List<Renderer> blockR = new List<Renderer>();

    sealed class MergeBuf
    {
        readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>(); readonly List<Color32> c = new List<Color32>(); readonly List<int> t = new List<int>();
        // aoK: the AO strength the part's own material had (baked into R, so the shader needs no per-part value)
        public void Add(Vector3[] pos, Vector3[] nrm, Color32[] col, int[] idx, Matrix4x4 xf, byte id, float aoK)
        {
            int o = v.Count; var nm = xf.inverse.transpose; bool hub = id == IdHubF || id == IdHubR;
            for (int i = 0; i < pos.Length; i++)
            {
                v.Add(xf.MultiplyPoint3x4(pos[i]));
                n.Add(nm.MultiplyVector(nrm != null && i < nrm.Length ? nrm[i] : Vector3.up).normalized);
                var cc = col != null && i < col.Length ? col[i] : new Color32(255, 0, 255, 255);
                c.Add(new Color32((byte)Mathf.RoundToInt(255f - aoK * (255 - cc.r)), hub ? cc.g : (byte)0, 0, id));
            }
            foreach (var i in idx) t.Add(o + i);
        }
        public int Count => v.Count;
        public Mesh Build(string name)
        {
            if (v.Count == 0) return null;
            var m = new Mesh { name = name, indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.SetVertices(v); m.SetNormals(n); m.SetColors(c); m.SetTriangles(t, 0); m.RecalculateBounds();
            return m;
        }
    }

    static RivalSet BuildRivalSet(string res)
    {
        var ta = Resources.Load<TextAsset>(res);
        if (ta == null) return null;
        var d = ta.bytes; Resources.UnloadAsset(ta); // the merged meshes are cached: the file is not read again
        if (d.Length < 8 || d[0] != 'F' || d[1] != '2' || d[2] != 'M' || (d[3] != '1' && d[3] != '2')) return null;
        bool hasCol = d[3] == '2'; int o = 4;
        int I() { int x = System.BitConverter.ToInt32(d, o); o += 4; return x; }
        float F() { float x = System.BitConverter.ToSingle(d, o); o += 4; return x; }
        int n = I();
        var set = new RivalSet { far = res.EndsWith("_far") };
        var body = new MergeBuf(); var spin = new MergeBuf[4]; var stat = new MergeBuf[4];
        for (int w = 0; w < 4; w++) { spin[w] = new MergeBuf(); stat[w] = new MergeBuf(); }
        for (int p = 0; p < n; p++)
        {
            int len = I(); string name = System.Text.Encoding.UTF8.GetString(d, o, len); o += len;
            int mid = I(); var piv = new Vector3(F(), F(), F());
            if (name.StartsWith("wheel_")) piv.y = WheelY(name[6] == 'F'); // as CreateFromAsset
            int nv = I(), ni = I();
            var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var idx = new int[ni]; Color32[] col = null;
            for (int i = 0; i < nv; i++) pos[i] = new Vector3(F(), F(), F());
            for (int i = 0; i < nv; i++) nrm[i] = new Vector3(F(), F(), F());
            if (hasCol) { col = new Color32[nv]; for (int i = 0; i < nv; i++) { col[i] = new Color32(d[o], d[o + 1], d[o + 2], d[o + 3]); o += 4; } }
            for (int i = 0; i < ni; i++) idx[i] = I();
            if (name.EndsWith("_tyre")) FitTyre(pos, nrm, idx, name[6] == 'F');
            Compact(ref pos, ref nrm, ref col, idx);
            if (!name.StartsWith("wheel_") && !name.StartsWith("steer_")) LowerBody(pos, nrm);
            if (TwoSided(mid, name)) Twin(ref pos, ref nrm, ref col, ref idx);
            var at = Matrix4x4.Translate(piv);
            if (name.StartsWith("wheel_"))
            {
                string key = name.Substring(6, 2); int w = System.Array.IndexOf(WheelKeys, key); if (w < 0) continue;
                bool front = key[0] == 'F', tyre = name.EndsWith("_tyre"), steerOnly = name.EndsWith("_hubstatic");
                byte id = tyre ? (front ? IdTyreF : IdTyreR) : (front ? IdHubF : IdHubR); float k = tyre ? 0.45f : 0.7f;
                set.piv[w] = piv;
                if (tyre) { float r = 0; foreach (var q in pos) r = Mathf.Max(r, new Vector2(q.y, q.z).magnitude); set.tyreR[front ? 0 : 1] = r; }
                if (set.far) body.Add(pos, nrm, col, idx, at, id, k);               // far: the wheels stand still in the one mesh
                else if (steerOnly && !front) body.Add(pos, nrm, col, idx, at, id, k); // rear uprights / calipers neither spin nor steer
                else if (steerOnly) stat[w].Add(pos, nrm, col, idx, Matrix4x4.identity, id, k);
                else
                {
                    spin[w].Add(pos, nrm, col, idx, Matrix4x4.identity, id, k);
                    if (tyre) { var sm = StripeMesh(pos, pos.Length); if (sm != null) spin[w].Add(sm.vertices, sm.normals, null, sm.triangles, Matrix4x4.identity, IdStripe, 0f); }
                }
            }
            else if (name.StartsWith("steer_"))
            {
                // rivals never turn it: baked in at its rest pose
                float.TryParse(name.Substring(6), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pitch);
                body.Add(pos, nrm, col, idx, Matrix4x4.TRS(piv, Quaternion.Euler(pitch, 0, 0), Vector3.one), IdSteer, 0.5f);
            }
            else
            {
                byte id = mid == 0 ? IdPaint : mid == 1 ? IdWing : mid == 3 ? IdMetal : mid == 6 ? IdLight : IdCarbon;
                float k = mid == 0 ? 0.6f : mid == 1 ? 0.55f : mid == 3 ? 0.7f : mid == 6 ? 0f : 0.75f;
                body.Add(pos, nrm, col, idx, at, id, k);
            }
        }
        // the driver's helmet (a Unity sphere, as on the player's car), in the body mesh
        sphere ??= prim(PrimitiveType.Sphere);
        body.Add(sphere.vertices, sphere.normals, null, sphere.triangles, Matrix4x4.TRS(HelmetPos, Quaternion.identity, HelmetScale), IdHelmet, 0f);
        set.body = body.Build(res + "|rival_body");
        for (int w = 0; w < 4; w++) { set.spin[w] = spin[w].Build(res + "|spin" + w); set.steerOnly[w] = stat[w].Build(res + "|steer" + w); }
        var m = new Material(paintShader) { name = "RivalPaint " + res, enableInstancing = true };
        m.EnableKeyword("_MERGED");
        m.SetColor("_Color", new Color(0.5f, 0.5f, 0.52f)); // (every car sets its own _Color; never the shader's default red)
        m.SetVector("_TyreRad", new Vector4(set.tyreR[0], set.tyreR[1], 0, 0)); m.SetFloat("_Far", set.far ? 1f : 0f);
        set.mat = m;
        return set;
    }

    static CarModel CreateMergedRival(string res, Color? paint, Color? stripe)
    {
        if (PaintShader() == null) return null; // the standard-shader fallback has no part IDs: build part by part
        if (!rivalCache.TryGetValue(res, out var set) || set == null || set.mat == null || set.body == null)
        {
            set = BuildRivalSet(res); if (set == null) return null; rivalCache[res] = set;
        }
        var root = new GameObject("Car");
        root.transform.localScale = Vector3.one * RootScale; // as CreateFromAsset
        var cm = root.AddComponent<CarModel>();
        cm.body = new GameObject("Body").transform; cm.body.SetParent(root.transform, false);
        cm.Res = res; cm.rival = true; cm.merged = true; cm.rset = set; cm.mpb = new MaterialPropertyBlock(); cm.tyreR = set.tyreR[0];
        cm.bodyGo = cm.MergedPart("rival_body", cm.body, set.body);
        if (!set.far)
            for (int w = 0; w < 4; w++)
            {
                string key = WheelKeys[w];
                var pivot = new GameObject("WheelPivot_" + key).transform; pivot.SetParent(root.transform, false); pivot.localPosition = set.piv[w];
                var spin = new GameObject("Spin").transform; spin.SetParent(pivot, false);
                if (set.spin[w] != null) cm.MergedPart("wheel_" + key + "_tyre", spin, set.spin[w]); // (name: DamageRefs finds the tyre for a flat)
                if (set.steerOnly[w] != null) cm.MergedPart("wheel_" + key + "_hubstatic", pivot, set.steerOnly[w]);
                if (w < 2) { cm.frontPivots[w] = pivot; cm.frontSpin[w] = spin; } else cm.rearSpin[w - 2] = spin;
            }
        // the helmet is in the body mesh; an empty stand-in keeps CarModel.helmet usable
        var h = new GameObject("Helmet").transform; h.SetParent(cm.body, false); h.localPosition = HelmetPos; cm.helmet = h;
        if (rivalDebris == null) { rivalDebris = Kind(Paint(Lin(0.022f, 0.024f, 0.028f), 0.35f, 0.88f, 0.05f, 0.7f, 0.25f), "_CARBON"); rivalDebris.name = "RivalDebris"; }
        cm.debrisSrc = rivalDebris;
        // per-car values: the old per-part materials' defaults until SetLivery / SetStripe / the brakes change them
        cm.mpb.SetColor(LivAId, paint ?? new Color(0.78f, 0.05f, 0.07f)); cm.mpb.SetColor(LivBId, new Color(0.93f, 0.93f, 0.94f));
        cm.mpb.SetColor(LivCId, new Color(0.1f, 0.1f, 0.11f, 0f)); // a = 0: no livery split until SetLivery
        cm.mpb.SetColor(StripeId, stripe ?? StripeFor(FormulaSim.Compound.Soft)); cm.mpb.SetColor(LightColId, new Color(0.25f, 0.02f, 0.02f));
        cm.mpb.SetColor(GlowFId, Color.black); cm.mpb.SetColor(GlowRId, Color.black);
        cm.mpb.SetVector(AxleId, Vector4.zero); cm.mpb.SetVector(TyreWDId, Vector4.zero);
        cm.lightState = 0;
        var sh = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(sh.GetComponent<Collider>());
        sh.name = "ContactShadow"; sh.transform.SetParent(root.transform, false);
        sh.transform.localPosition = new Vector3(0, 0.035f, 0.3f); sh.transform.localRotation = Quaternion.Euler(90, 0, 0);
        sh.transform.localScale = new Vector3(2.4f, 5.6f, 1);
        var sr = sh.GetComponent<MeshRenderer>(); sr.sharedMaterial = ContactShadowMat(); sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        foreach (var tr in root.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = LightBake.CarLayer;
        cm.blockBase = cm.blockR.Count;
        cm.Flush();
        return cm;
    }
    GameObject MergedPart(string name, Transform parent, Mesh mesh)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = rset.mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // rivals keep only the soft blob shadow
        blockR.Add(r);
        return go;
    }
    // the per-car values go to the renderers once a frame at most (LateUpdate), and right away when the car is shown
    void Flush()
    {
        mpbDirty = false;
        if (!merged) return;
        for (int i = 0; i < blockR.Count; i++) if (blockR[i] != null) blockR[i].SetPropertyBlock(mpb);
    }
    void LateUpdate() { if (mpbDirty) Flush(); }
    void OnEnable() { if (merged) Flush(); }
    // a wing is damaged: split the wing pieces off the merged body (+5 draw calls, only while it is damaged)
    void MergedSplit(bool want)
    {
        if (want == mSplit || bodyGo == null) return;
        mSplit = want;
        if (want)
        {
            SplitWings(bodyGo, Res + "|merged", rset.mat, true);
            for (int k = 0; k < dmgPiv.Length; k++)
                if (dmgPiv[k] != null) foreach (var r in dmgPiv[k].GetComponentsInChildren<Renderer>(true)) if (!blockR.Contains(r)) blockR.Add(r);
        }
        else
        {
            // repaired (pit stop, restart): back to the one body mesh
            bodyGo.GetComponent<MeshFilter>().sharedMesh = rset.body; bodyGo.GetComponent<MeshRenderer>().enabled = true;
            foreach (int k in new[] { 0, 1, 4 }) if (dmgPiv[k] != null) Destroy(dmgPiv[k].gameObject);
            for (int k = 0; k < dmgPiv.Length; k++) dmgPiv[k] = null;
            if (blockR.Count > blockBase) blockR.RemoveRange(blockBase, blockR.Count - blockBase);
        }
        Flush();
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

    // ---- fit to a 2026 car. The source model is an F2-size car, scaled up at the root by 3.40 / 3.135 (wheelbase).
    //  * tyres: 705 mm front / 710 mm rear across (the model's were 688 mm). The rubber is stretched outward from the
    //    bead, so the 18" rim keeps its size; the wheel centre goes to the new radius less a 4 mm squash, so every
    //    tyre sits on the road (the fronts floated 6 mm) and its bottom tucks into the contact shadow.
    //  * ride height: the model's floor sat ~13 cm off the road; a 2026 car runs ~5-7 cm. Only the lower body moves
    //    down (floor, nose, front wing, sidepod flanks, diffuser), fading out to nothing at DropY1, so the cockpit,
    //    halo, airbox and rear wing stay exactly where the cockpit camera was fitted. Near each wheel centre the drop
    //    fades out across x towards the upright (linearly in x, so the suspension arms stay straight and still meet
    //    the uprights, which move with the wheel).
    public const float RootScale = 3.40f / 3.135f;
    const float TyreSquash = 0.004f, Drop = 0.045f, DropY0 = 0.30f, DropY1 = 0.52f; // model units (x RootScale = m)
    static readonly Vector2[] AxleZY = { new Vector2(1.724f, 0.32f), new Vector2(-1.411f, 0.32f) };
    static float TyreTarget(bool front) => (front ? 0.705f : 0.710f) * 0.5f / RootScale;
    static float WheelY(bool front) => TyreTarget(front) - TyreSquash;
    // (the normals follow the stretch, so the shoulder keeps its soft roll instead of a lighting kink)
    static void FitTyre(Vector3[] pos, Vector3[] nrm, int[] idx, bool front)
    {
        float R = 0f, rb = float.MaxValue;
        foreach (int i in idx) { float r = new Vector2(pos[i].y, pos[i].z).magnitude; R = Mathf.Max(R, r); rb = Mathf.Min(rb, r); }
        float Rt = TyreTarget(front);
        if (R <= 0f || R >= Rt || rb >= R - 0.02f) return;
        float k = (Rt - rb) / (R - rb);
        for (int i = 0; i < pos.Length; i++)
        {
            float r = new Vector2(pos[i].y, pos[i].z).magnitude; if (r < rb || r < 1e-5f) continue;
            float s = (rb + (r - rb) * k) / r;
            if (nrm != null && i < nrm.Length)
            {
                // inverse transpose of the stretch: radial part / k, round-the-wheel part / s, axle part unchanged
                var n = nrm[i]; float cy = pos[i].y / r, cz = pos[i].z / r, nr = n.y * cy + n.z * cz;
                float ty = (n.y - nr * cy) / s, tz = (n.z - nr * cz) / s; nr /= k;
                nrm[i] = new Vector3(n.x, nr * cy + ty, nr * cz + tz).normalized;
            }
            pos[i].y *= s; pos[i].z *= s;
        }
    }
    static void LowerBody(Vector3[] pos, Vector3[] nrm)
    {
        const float band = DropY1 - DropY0;
        for (int i = 0; i < pos.Length; i++)
        {
            var p = pos[i];
            float fy = Mathf.Clamp01((DropY1 - p.y) / band);
            if (fy <= 0f) continue;
            float xs = Mathf.Clamp01((Mathf.Abs(p.x) - 0.20f) / 0.36f), keep = 0f;
            for (int a = 0; a < AxleZY.Length; a++)
            {
                float r = new Vector2(p.z - AxleZY[a].x, p.y - AxleZY[a].y).magnitude;
                keep = Mathf.Max(keep, Mathf.Clamp01((0.31f - r) / 0.06f) * xs);
            }
            float w = 1f - keep;
            pos[i].y = p.y - Drop * fy * w;
            // the band DropY0..DropY1 is stretched vertically: lean its normals to match
            if (p.y > DropY0 && nrm != null && i < nrm.Length) { var n = nrm[i]; n.y /= 1f + Drop / band * w; nrm[i] = n.normalized; }
        }
    }

    // tyres (mid 4) too: the source tyre has folded, inward-wound slivers along both shoulders; drawn one-sided they
    // are holes through which the sky showed as white patches on the tyre edge
    static bool TwoSided(int mid, string name) => mid == 1 || mid == 2 || mid == 3 || mid == 4 || mid == 6 || name.StartsWith("steer_");
    // a twin of every triangle facing the other way (thin single-skin parts)
    static void Twin(ref Vector3[] pos, ref Vector3[] nrm, ref Color32[] col, ref int[] idx)
    {
        int nv = pos.Length, ni = idx.Length;
        var pos2 = new Vector3[nv * 2]; var nrm2 = new Vector3[nv * 2]; var idx2 = new int[ni * 2];
        for (int i = 0; i < nv; i++) { pos2[i] = pos[i]; nrm2[i] = nrm[i]; pos2[nv + i] = pos[i]; nrm2[nv + i] = -nrm[i]; }
        if (col != null)
        {
            // the twin facing the other way takes the back face's AO (B) as its own (R)
            var col2 = new Color32[nv * 2];
            for (int i = 0; i < nv; i++) { col2[i] = col[i]; var c = col[i]; col2[nv + i] = new Color32(c.b, c.g, c.r, c.a); }
            col = col2;
        }
        for (int i = 0; i < ni; i += 3)
        {
            idx2[i] = idx[i]; idx2[i + 1] = idx[i + 1]; idx2[i + 2] = idx[i + 2];
            idx2[ni + i] = nv + idx[i]; idx2[ni + i + 1] = nv + idx[i + 2]; idx2[ni + i + 2] = nv + idx[i + 1];
        }
        pos = pos2; nrm = nrm2; idx = idx2;
    }

    // ------------------------------------------------------------------ detail: brakes, tyres, wheel faces, effects
    // drop vertices no triangle uses (only when it saves at least half: the shared vertex lists of the converter)
    static void Compact(ref Vector3[] pos, ref Vector3[] nrm, ref Color32[] col, int[] idx)
    {
        int nv = pos.Length; var map = new int[nv]; int used = 0;
        for (int i = 0; i < nv; i++) map[i] = -1;
        for (int i = 0; i < idx.Length; i++) if (map[idx[i]] < 0) map[idx[i]] = used++;
        if (used * 2 > nv) return;
        var p2 = new Vector3[used]; var n2 = new Vector3[used]; var c2 = col != null ? new Color32[used] : null;
        for (int i = 0; i < nv; i++) { int m = map[i]; if (m < 0) continue; p2[m] = pos[i]; n2[m] = nrm[i]; if (c2 != null) c2[m] = col[i]; }
        for (int i = 0; i < idx.Length; i++) idx[i] = map[idx[i]];
        pos = p2; nrm = n2; col = c2;
    }
    static Material Kind(Material m, string kw) { if (m != null && m.HasProperty("_AO")) m.EnableKeyword(kw); return m; }
    readonly Material[] tyreMat = new Material[2], hubMat = new Material[2], faceMat = new Material[2];
    readonly List<Renderer> faces = new List<Renderer>();
    readonly float[] brakeT = { 250f, 250f }, dust = new float[2], blur = new float[2], lastSpin = { float.NaN, float.NaN };
    float tyreR = 0.33f, lastFeed = -10f; bool rival;
    Vector3 lastPos, vel;

    // brake disc colour by temperature (deg C): nothing below ~500, dark red, then orange, bright orange near 1000
    public static Color BrakeGlow(float t)
    {
        float g = Mathf.Clamp01((t - 500f) / 500f);
        if (g <= 0) return Color.black;
        var c = g < 0.5f ? Color.Lerp(new Color(0.3f, 0.01f, 0f), new Color(1.3f, 0.14f, 0.01f), g * 2f)
                         : Color.Lerp(new Color(1.3f, 0.14f, 0.01f), new Color(2.6f, 1.0f, 0.18f), (g - 0.5f) * 2f);
        return c * Mathf.Clamp01(g * 6f);
    }

    static readonly int GlowId = Shader.PropertyToID("_Glow"), DustId = Shader.PropertyToID("_Dust"), WearId = Shader.PropertyToID("_Wear"),
        DirtId = Shader.PropertyToID("_Dirt"), BlurId = Shader.PropertyToID("_Blur");
    readonly Color[] glowSet = { new Color(-1, 0, 0), new Color(-1, 0, 0) }; readonly float[] dustSet = { -1f, -1f }, tyreSet = { -1f, -1f, -1f, -1f };
    void UpdateBrakeLook(float dt)
    {
        for (int k = 0; k < 2; k++)
        {
            // brake dust builds up slowly with hard (hot) braking; never quite black
            dust[k] = Mathf.Min(0.65f, dust[k] + dt * 0.004f * Mathf.Clamp01((brakeT[k] - 350f) / 650f));
            var glow = BrakeGlow(brakeT[k]);
            // material writes only when the value moved (a cold disc stays black for most of a lap; dust creeps)
            if (glow == glowSet[k] && Mathf.Abs(dust[k] - dustSet[k]) < 0.002f) continue;
            glowSet[k] = glow; dustSet[k] = dust[k];
            if (merged) { mpb.SetColor(k == 0 ? GlowFId : GlowRId, glow); mpb.SetVector(AxleId, new Vector4(dust[0], dust[1], 0, 0)); mpbDirty = true; continue; }
            if (hubMat[k] != null && hubMat[k].HasProperty(GlowId)) { hubMat[k].SetColor(GlowId, glow); hubMat[k].SetFloat(DustId, dust[k]); }
            if (faceMat[k] != null) { faceMat[k].SetColor(GlowId, glow); faceMat[k].SetFloat(DustId, dust[k]); }
        }
    }

    // Everything the car shows from its physics state, once per frame (GameManager; see the report for the hook):
    // real brake temperatures (glow, dust), tyre wear and dirt (tread sheen), ERS harvesting (blinking rear lights),
    // plank sparks when bottoming at speed and tyre smoke on lock-up / wheelspin (onTrack: on asphalt, not grass).
    public void Feed(FormulaSim.FormulaCar car, float dt, bool onTrack = true)
    {
        if (car == null) return;
        lastFeed = Time.time;
        if (!double.IsNaN(car.brakeTF)) { brakeT[0] = (float)car.brakeTF; brakeT[1] = (float)car.brakeTR; }
        if (dt > 0) UpdateBrakeLook(dt);
        float wF = (float)car.wearF, wR = (float)car.wearR, dF = double.IsNaN(car.dirtF) ? 0 : (float)car.dirtF, dR = double.IsNaN(car.dirtR) ? 0 : (float)car.dirtR;
        if (merged && (Mathf.Abs(wF - tyreSet[0]) > 0.002f || Mathf.Abs(dF - tyreSet[1]) > 0.002f || Mathf.Abs(wR - tyreSet[2]) > 0.002f || Mathf.Abs(dR - tyreSet[3]) > 0.002f))
        {
            tyreSet[0] = wF; tyreSet[1] = dF; tyreSet[2] = wR; tyreSet[3] = dR;
            mpb.SetVector(TyreWDId, new Vector4(wF, dF, wR, dR)); mpbDirty = true;
        }
        if (tyreMat[0] != null && (Mathf.Abs(wF - tyreSet[0]) > 0.002f || Mathf.Abs(dF - tyreSet[1]) > 0.002f || Mathf.Abs(wR - tyreSet[2]) > 0.002f || Mathf.Abs(dR - tyreSet[3]) > 0.002f) && tyreMat[0].HasProperty(WearId))
        {
            tyreSet[0] = wF; tyreSet[1] = dF; tyreSet[2] = wR; tyreSet[3] = dR;
            tyreMat[0].SetFloat(WearId, wF); tyreMat[0].SetFloat(DirtId, dF); tyreMat[1].SetFloat(WearId, wR); tyreMat[1].SetFloat(DirtId, dR);
        }
        float speed = Mathf.Sqrt((float)(car.vx * car.vx + car.vy * car.vy));
        harvesting = !double.IsNaN(car.ersKw) && car.ersKw < -5 && speed > 10f;
        UpdateLights();
        if (car.damage.Any || dmgShown || seenHits != car.damage.hits || dLifeAny) ShowDamage(car, dt, speed);
        if (rival || dt <= 0 || LowDetail) return; // Low: no sparks or smoke (alpha-blended particles near the camera)
        // sparks from the plank: titanium skid blocks scraping the road at speed
        if (car.bottoming && speed > 40f) Sparks(dt, speed);
        // tyre smoke where a tyre is locked or spinning on the asphalt
        if (onTrack && speed > 4f)
        {
            if (car.lockF) for (int i = 0; i < 2; i++) Smoke(frontPivots[i], dt, speed);
            if (car.lockR || car.spinR) for (int i = 0; i < 2; i++) Smoke(rearSpin[i] != null ? rearSpin[i].parent : null, dt, speed);
        }
    }

    // the wheel-face quad: a disc over the rim's outer face, found from the rim's own vertices (wheel frame, x = axle)
    void WheelFace(Transform spin, Vector3[] pos, int nv, float side, bool front)
    {
        var sh = Resources.Load<Shader>("CarWheelFace");
        if (sh == null || !sh.isSupported) return;
        float sg = side >= 0 ? 1f : -1f, xo = float.MinValue;
        for (int i = 0; i < nv; i++) xo = Mathf.Max(xo, pos[i].x * sg);
        float rr = 0; for (int i = 0; i < nv; i++) if (pos[i].x * sg > xo - 0.03f) rr = Mathf.Max(rr, new Vector2(pos[i].y, pos[i].z).magnitude);
        if (rr <= 0.05f) return;
        int k = front ? 0 : 1;
        if (faceMat[k] == null) { faceMat[k] = new Material(sh); faceMat[k].SetColor("_Color", Lin(0.22f, 0.23f, 0.245f)); faceMat[k].SetColor("_Cover", Lin(0.03f, 0.032f, 0.036f)); }
        float x = (xo + 0.004f) * sg;
        var m = new Mesh
        {
            vertices = new[] { new Vector3(x, -rr, -rr), new Vector3(x, rr, -rr), new Vector3(x, rr, rr), new Vector3(x, -rr, rr) },
            uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) },
            normals = new[] { Vector3.right * sg, Vector3.right * sg, Vector3.right * sg, Vector3.right * sg },
        };
        // facing outward on either side of the car
        m.triangles = sg > 0 ? new[] { 0, 1, 2, 0, 2, 3 } : new[] { 0, 2, 1, 0, 3, 2 };
        m.RecalculateBounds();
        var go = new GameObject("WheelFace"); go.transform.SetParent(spin, false);
        go.AddComponent<MeshFilter>().sharedMesh = m;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = faceMat[k];
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        faces.Add(r);
    }

    // contact shadow (one quad, one texture, no lighting): what makes the car sit ON the road. Four layers:
    //  * a broad soft blob (sky light blocked by the whole car),
    //  * a near-black core under the floor (5-7 cm above the road: almost no light gets under it),
    //  * an occlusion skirt fading ~35 cm out from the floor edge,
    //  * per tyre a black contact patch plus an occlusion halo in front of and behind it (the tyre's bulge
    //    shades the road where it meets it).
    static Material contactMat;
    static Material ContactShadowMat()
    {
        if (contactMat != null) return contactMat;
        var baseMat = Resources.Load<Material>("Shadow");
        if (baseMat == null) return contactMat = WorldBuilder.ShadowMat();
        contactMat = new Material(baseMat);
        // texel -> car space (model units): quad 2.4 m x 5.6 m centred at z = 0.3
        contactMat.mainTexture = WorldBuilder.Tex(96, 224, (u, v, px, py) =>
        {
            float dx = (u - 0.5f) * 2, dy = (v - 0.5f) * 2, d = Mathf.Sqrt(dx * dx * 1.1f + dy * dy * 0.9f);
            float soft = Mathf.Clamp01(1 - d); soft = soft * soft * (3 - 2 * soft) * 0.6f;
            float x = (u - 0.5f) * 2.4f, z = 0.3f + (v - 0.5f) * 5.6f;
            // floor plan: ~0.62 m half width beside the cockpit, tapering into the nose
            float hw = z > 1.1f ? Mathf.Lerp(0.55f, 0.12f, Mathf.Clamp01((z - 1.1f) / 1.5f)) : z < -1.5f ? Mathf.Lerp(0.55f, 0.3f, Mathf.Clamp01((-1.5f - z) / 0.6f)) : 0.62f;
            float ex = Mathf.Abs(x) - hw, ez = Mathf.Max(z - 2.7f, -2.15f - z);
            float out1 = Mathf.Sqrt(Mathf.Max(ex, 0) * Mathf.Max(ex, 0) + Mathf.Max(ez, 0) * Mathf.Max(ez, 0)) + Mathf.Min(Mathf.Max(ex, ez), 0);
            float core = Mathf.Clamp01(1 - (out1 + 0.08f) / 0.2f); core = core * (2 - core) * 0.88f;
            float skirt = Mathf.Clamp01(1 - out1 / 0.35f); skirt = skirt * skirt * 0.4f;
            float noseK = Mathf.Lerp(1f, 0.35f, Mathf.Clamp01((z - 1.2f) / 0.8f)); core *= noseK; skirt *= noseK; // the nose is high: softer
            float patch = 0;
            foreach (var (wx, wz, hx) in new[] { (0.71f, 1.72f, 0.13f), (-0.71f, 1.72f, 0.13f), (0.68f, -1.41f, 0.16f), (-0.68f, -1.41f, 0.16f) })
            {
                float ax = Mathf.Abs(x - wx) - hx, az = Mathf.Abs(z - wz);
                // the patch itself (~12 cm long), hard-edged
                float px2 = ax / 0.04f, pz2 = (az - 0.06f) / 0.05f;
                float q = Mathf.Clamp01(1 - Mathf.Max(px2, pz2));
                // occlusion halo: along the tyre ~40 cm each way, a little to the sides, strongest at the patch
                float hxz = Mathf.Max(ax, 0) / 0.1f, hzz = az / 0.42f, h = Mathf.Clamp01(1 - Mathf.Sqrt(hxz * hxz + hzz * hzz));
                patch = Mathf.Max(patch, Mathf.Max(q * 0.97f, h * h * 0.7f));
            }
            float a = 1 - (1 - soft) * (1 - core) * (1 - skirt) * (1 - patch);
            return new Color32(0, 0, 0, (byte)(Mathf.Clamp01(a) * 250));
        });
        contactMat.mainTexture.wrapMode = TextureWrapMode.Clamp;
        return contactMat;
    }

    // ---- particles: one spark system and one smoke system per player car, made on first use
    ParticleSystem sparks, smoke; float sparkAcc, smokeAcc; static Shader fxShader; static bool fxTried;
    ParticleSystem MakeFx(string name, bool additive, int max)
    {
        if (!fxTried) { fxTried = true; fxShader = Resources.Load<Shader>("CarFx"); if (fxShader != null && !fxShader.isSupported) fxShader = null; }
        if (fxShader == null) return null;
        var go = new GameObject(name); go.transform.SetParent(transform, false); go.layer = gameObject.layer;
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.playOnAwake = false; main.loop = true; main.maxParticles = max;
        main.simulationSpace = ParticleSystemSimulationSpace.World; main.gravityModifier = additive ? 1.3f : -0.03f;
        var em = ps.emission; em.rateOverTime = 0; em.enabled = true;
        var shp = ps.shape; shp.enabled = false;
        var col = ps.colorOverLifetime; col.enabled = true;
        var gr = new Gradient();
        gr.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(additive ? new Color(1f, 0.35f, 0.1f) : Color.white, 1) },
                   new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(additive ? 0.6f : 0.5f, 0.4f), new GradientAlphaKey(0, 1) });
        col.color = gr;
        if (!additive) { var sz = ps.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.35f, 1, 1f)); }
        var m = new Material(fxShader);
        m.SetFloat("_Src", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_Dst", (float)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
        m.SetFloat("_Soft", additive ? 0.8f : 1.6f);
        var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        if (additive) { r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.025f; r.lengthScale = 1f; }
        ps.Play();
        return ps;
    }
    void Sparks(float dt, float speed)
    {
        if (sparks == null && (sparks = MakeFx("PlankSparks", true, 48)) == null) return;
        sparkAcc += dt * Mathf.Lerp(40f, 110f, Mathf.InverseLerp(40f, 85f, speed));
        var ep = new ParticleSystem.EmitParams();
        for (; sparkAcc >= 1f; sparkAcc -= 1f)
        {
            // from the plank under the car, flung back and out with part of the car's speed
            ep.position = transform.TransformPoint(new Vector3(Random.Range(-0.15f, 0.15f), 0.02f, Random.Range(-1.3f, 0.5f)));
            ep.velocity = vel * Random.Range(0.45f, 0.75f) + transform.right * Random.Range(-2.5f, 2.5f) + Vector3.up * Random.Range(0.5f, 2.2f);
            ep.startSize = Random.Range(0.025f, 0.045f); ep.startLifetime = Random.Range(0.18f, 0.4f);
            ep.startColor = new Color(1f, Random.Range(0.75f, 0.95f), 0.5f, 1f);
            sparks.Emit(ep, 1);
        }
    }
    void Smoke(Transform wheel, float dt, float speed)
    {
        if (wheel == null) return;
        if (smoke == null && (smoke = MakeFx("TyreSmoke", false, 64)) == null) return;
        smokeAcc += dt * 14f;
        var ep = new ParticleSystem.EmitParams();
        for (; smokeAcc >= 1f; smokeAcc -= 1f)
        {
            ep.position = wheel.position - transform.up * (tyreR * transform.lossyScale.y * 0.85f) + Random.insideUnitSphere * 0.1f;
            ep.velocity = vel * 0.2f + Vector3.up * Random.Range(0.3f, 0.9f) + Random.insideUnitSphere * 0.5f;
            ep.startSize = Random.Range(0.7f, 1.1f); ep.startLifetime = Random.Range(1.0f, 1.7f);
            float g = Random.Range(0.78f, 0.9f); ep.startColor = new Color(g, g, g, 0.32f);
            ep.rotation = Random.Range(0f, 360f);
            smoke.Emit(ep, 1);
        }
    }

    // ------------------------------------------------------------------ damage (FormulaSim.Damage, see Feed)
    // The game sets this from the Damage setting: false = "Off" (nothing shown). "Visual only" and "Full" show it.
    public static bool damageVisuals = true;
    // wing pieces, split off the imported model's carbon / white / paint parts: 0 front wing left, 1 right,
    // 2 left endplate, 3 right endplate (children of the sides), 4 rear wing. Car frame: model -x = the car's left.
    readonly Transform[] dmgPiv = new Transform[5];
    static readonly Vector3[] DmgPivPos = { new Vector3(-0.14f, 0.195f, 2.08f), new Vector3(0.14f, 0.195f, 2.08f), new Vector3(-0.68f, 0.155f, 2.35f), new Vector3(0.68f, 0.155f, 2.35f), new Vector3(0f, 0.72f, -1.8f) }; // (front: lowered with the body, LowerBody)
    static readonly Dictionary<string, Mesh[]> splitCache = new Dictionary<string, Mesh[]>();
    Material debrisSrc;
    // wheels by physics index (0 FL, 1 FR, 2 RL, 3 RR), their rest positions and tyres; toe / camber shown (deg)
    readonly Transform[] wPiv = new Transform[4], wTyre = new Transform[4]; readonly Vector3[] wBase = new Vector3[4];
    readonly float[] wToe = new float[4], wCamber = new float[4];
    bool dmgRefs, wheelDmg, dmgShown; int seenHits = -1; float dmgSmokeAcc;

    static int WingRegion(Vector3 c)
    {
        if (c.z > 2.05f && c.y < 0.5f && Mathf.Abs(c.x) > 0.14f) return Mathf.Abs(c.x) > 0.68f ? (c.x < 0 ? 3 : 4) : (c.x < 0 ? 1 : 2);
        if (c.z < -1.75f && c.y > 0.5f) return 5;
        return 0;
    }
    static Mesh SubMesh(Vector3[] v, Vector3[] n, Color32[] col, int[] tri, List<int> tris)
    {
        var map = new int[v.Length]; for (int i = 0; i < map.Length; i++) map[i] = -1;
        var pv = new List<Vector3>(); var pn = new List<Vector3>(); var pc = col != null && col.Length == v.Length ? new List<Color32>() : null; var pt = new List<int>(tris.Count * 3);
        foreach (int t in tris)
            for (int k = 0; k < 3; k++)
            {
                int a = tri[t + k];
                if (map[a] < 0) { map[a] = pv.Count; pv.Add(v[a]); pn.Add(n[a]); if (pc != null) pc.Add(col[a]); }
                pt.Add(map[a]);
            }
        var m = new Mesh { indexFormat = pv.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
        m.SetVertices(pv); m.SetNormals(pn); if (pc != null) m.SetColors(pc); m.SetTriangles(pt, 0); m.RecalculateBounds();
        return m;
    }
    // split a part into the rest of the car + up to five wing pieces (made once per model and part, then cached)
    void SplitWings(GameObject go, string key, Material mat, bool isRival)
    {
        var mf = go.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) return;
        if (!splitCache.TryGetValue(key, out var parts))
        {
            var src = mf.sharedMesh; var v = src.vertices; var n = src.normals; var col = src.colors32; var tri = src.triangles;
            var lists = new List<int>[6]; for (int k = 0; k < 6; k++) lists[k] = new List<int>();
            bool ids = col != null && col.Length == v.Length; // merged rivals: wheel triangles (IDs 4..9) never go with a wing
            for (int t = 0; t < tri.Length; t += 3)
                lists[ids && col[tri[t]].a >= IdTyreF && col[tri[t]].a <= IdStripe ? 0 : WingRegion((v[tri[t]] + v[tri[t + 1]] + v[tri[t + 2]]) / 3f)].Add(t);
            parts = null;
            if (lists[1].Count + lists[2].Count + lists[3].Count + lists[4].Count + lists[5].Count > 0)
            {
                parts = new Mesh[6];
                for (int k = 0; k < 6; k++) if (lists[k].Count > 0) parts[k] = SubMesh(v, n, col, tri, lists[k]);
            }
            splitCache[key] = parts;
        }
        if (parts == null) return;
        mf.sharedMesh = parts[0];
        if (parts[0] == null) go.GetComponent<MeshRenderer>().enabled = false;
        for (int k = 1; k < 6; k++)
        {
            if (parts[k] == null) continue;
            var piv = DmgPivot(k - 1);
            var pg = new GameObject(go.name + "_dmg" + k); pg.transform.SetParent(piv, false); pg.transform.localPosition = -DmgPivPos[k - 1];
            pg.AddComponent<MeshFilter>().sharedMesh = parts[k];
            var pr = pg.AddComponent<MeshRenderer>(); pr.sharedMaterial = mat;
            if (isRival) pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pg.layer = go.layer;
        }
    }
    Transform DmgPivot(int k)
    {
        if (dmgPiv[k] != null) return dmgPiv[k];
        var t = new GameObject("Damage" + k).transform;
        if (k == 2 || k == 3) { var side = DmgPivot(k - 2); t.SetParent(side, false); t.localPosition = DmgPivPos[k] - DmgPivPos[k - 2]; }
        else { t.SetParent(body, false); t.localPosition = DmgPivPos[k]; }
        return dmgPiv[k] = t;
    }
    void DamageRefs()
    {
        dmgRefs = true;
        var piv = new Transform[4];
        for (int i = 0; i < 2; i++)
        {
            // model -x is the car's left (physics +y)
            var f = frontPivots[i]; if (f != null) piv[f.localPosition.x < 0 ? 0 : 1] = f;
            var r = rearSpin[i] != null ? rearSpin[i].parent : null; if (r != null) piv[r.localPosition.x < 0 ? 2 : 3] = r;
        }
        for (int w = 0; w < 4; w++)
        {
            wPiv[w] = piv[w]; if (piv[w] == null) continue;
            wBase[w] = piv[w].localPosition;
            foreach (var tr in piv[w].GetComponentsInChildren<Transform>(true)) if (tr.name == "Tyre" || tr.name.EndsWith("_tyre")) { wTyre[w] = tr; break; }
        }
    }

    // ---- debris: one small pool for all cars, simple ballistic pieces that bounce and settle (no physics engine)
    const int DebrisMax = 18;
    static Transform[] debris; static Vector3[] dVel = new Vector3[DebrisMax], dSpin = new Vector3[DebrisMax]; static float[] dLife = new float[DebrisMax];
    static int dNext, dFrame = -1;
    static bool dLifeAny { get { if (debris == null) return false; for (int i = 0; i < DebrisMax; i++) if (dLife[i] > 0) return true; return false; } }
    static void ThrowDebris(Vector3 at, Vector3 carVel, int count, Material m)
    {
        if (debris == null || debris[0] == null)
        {
            debris = new Transform[DebrisMax];
            for (int i = 0; i < DebrisMax; i++)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube); Destroy(g.GetComponent<Collider>()); g.name = "Debris";
                g.layer = LightBake.CarLayer;
                var r = g.GetComponent<MeshRenderer>(); r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (m != null) r.sharedMaterial = m;
                g.SetActive(false); debris[i] = g.transform; dLife[i] = 0;
            }
        }
        for (int c = 0; c < Mathf.Min(6, count); c++)
        {
            int i = dNext; dNext = (dNext + 1) % DebrisMax;
            var t = debris[i]; t.gameObject.SetActive(true);
            t.position = at + Random.insideUnitSphere * 0.25f; t.rotation = Random.rotation;
            t.localScale = new Vector3(Random.Range(0.08f, 0.3f), Random.Range(0.01f, 0.03f), Random.Range(0.05f, 0.18f));
            dVel[i] = carVel * Random.Range(0.4f, 0.8f) + Random.insideUnitSphere * 4f + Vector3.up * Random.Range(1.5f, 4.5f);
            dSpin[i] = Random.insideUnitSphere * 720f; dLife[i] = Random.Range(3f, 5f);
        }
    }
    static void StepDebris(float dt)
    {
        if (debris == null || debris[0] == null || dFrame == Time.frameCount || dt <= 0) return;
        dFrame = Time.frameCount;
        for (int i = 0; i < DebrisMax; i++)
        {
            if (dLife[i] <= 0) continue;
            var t = debris[i];
            dLife[i] -= dt;
            if (dLife[i] <= 0) { t.gameObject.SetActive(false); continue; }
            dVel[i] += UnityEngine.Physics.gravity * dt;
            // carbon pieces are light and flat: air slows them hard
            dVel[i] *= 1f / (1f + dt * (0.6f + 0.02f * dVel[i].magnitude));
            var p = t.position + dVel[i] * dt; float ground = WorldBuilder.H(p.x, p.z) + 0.01f;
            if (p.y < ground) { p.y = ground; dVel[i] = new Vector3(dVel[i].x * 0.6f, -dVel[i].y * 0.3f, dVel[i].z * 0.6f); dSpin[i] *= 0.6f; if (dVel[i].sqrMagnitude < 0.3f) dVel[i] = Vector3.zero; }
            t.position = p;
            if (dSpin[i].sqrMagnitude > 1f) t.rotation = Quaternion.Euler(dSpin[i] * dt) * t.rotation;
        }
    }

    // show the car's damage: wing pieces droop, twist and come off, bent wheels, flat tyres, debris, smoke
    void ShowDamage(FormulaSim.FormulaCar car, float dt, float speed)
    {
        if (!dmgRefs) DamageRefs();
        var d = damageVisuals ? car.damage : default(FormulaSim.Damage);
        dmgShown = d.Any;   // once clean again (repaired, restarted, setting off) one more pass puts every piece back
        if (merged) MergedSplit(d.WingsDamaged); // rivals: wing pieces split off only while a wing is damaged
        StepDebris(dt);
        // a new hit: debris from where it landed (model: -x = left, +z = ahead)
        if (seenHits < 0) seenHits = car.damage.hits;
        if (car.damage.hits != seenHits)
        {
            seenHits = car.damage.hits;
            if (damageVisuals && d.lastSize > 0.12)
                ThrowDebris(transform.TransformPoint(new Vector3(-(float)d.lastY, 0.25f, (float)d.lastX) / Mathf.Max(0.01f, transform.lossyScale.x)), vel, Mathf.Clamp(Mathf.RoundToInt((float)d.lastSize * 8f), 1, 6), debrisSrc);
        }
        // front wing sides (0 left, 1 right): droop at the tip, swept back; endplate gone from 0.4, the side from 0.85
        for (int k = 0; k < 2; k++)
        {
            float dm = (float)(k == 0 ? d.wingFL : d.wingFR), sx = k == 0 ? -1f : 1f;
            if (dmgPiv[k] != null)
            {
                dmgPiv[k].gameObject.SetActive(dm < 0.85f);
                dmgPiv[k].localPosition = DmgPivPos[k] + new Vector3(0, -0.03f * dm, -0.06f * dm);
                dmgPiv[k].localRotation = Quaternion.Euler(6f * dm, sx * 9f * dm, -sx * 14f * dm);
            }
            if (dmgPiv[k + 2] != null) { dmgPiv[k + 2].gameObject.SetActive(dm < 0.4f); dmgPiv[k + 2].localRotation = Quaternion.Euler(0, sx * 25f * dm, -sx * 20f * dm); }
        }
        if (dmgPiv[4] != null)
        {
            float dm = (float)d.wingRear;
            dmgPiv[4].gameObject.SetActive(dm < 0.9f);
            dmgPiv[4].localRotation = Quaternion.Euler(-12f * dm, 4f * dm, 9f * dm);
            dmgPiv[4].localPosition = DmgPivPos[4] + new Vector3(0, -0.05f * dm, 0);
        }
        // bent suspension: the wheel toes out and leans; a flat tyre sits on its rim
        wheelDmg = false;
        for (int w = 0; w < 4; w++)
        {
            float su = (float)d.Susp(w), sx = (w & 1) == 0 ? -1f : 1f, fl = Mathf.Min(1f, (float)d.Punct(w));
            wToe[w] = sx * 6f * su; wCamber[w] = sx * 7f * su;   // exaggerated against the physics' 1.5 deg so it reads
            if (su > 0) wheelDmg = true;
            if (wPiv[w] != null) wPiv[w].localPosition = wBase[w] + Vector3.down * (tyreR * 0.14f * fl);
            if (wTyre[w] != null) wTyre[w].localScale = fl > 0 ? new Vector3(1.06f, 1f - 0.14f * fl, 1f - 0.14f * fl) : Vector3.one;
        }
        if (!wheelDmg) for (int w = 2; w < 4; w++) if (wPiv[w] != null) wPiv[w].localRotation = Quaternion.identity; // (fronts: Apply)
        // smoke from a badly damaged car (broken floor or suspension rubbing, a shredding tyre)
        float heavy = Mathf.Max((float)d.floor - 0.4f, Mathf.Max(Mathf.Max((float)d.suspFL, (float)d.suspFR), Mathf.Max((float)d.suspRL, (float)d.suspRR)) - 0.6f);
        bool flat = d.Punctured;
        if (dt > 0 && (heavy > 0 || flat) && speed > 3f)
        {
            if (smoke == null && (smoke = MakeFx("TyreSmoke", false, 64)) == null) return;
            dmgSmokeAcc += dt * (flat ? 10f : 4f + 10f * heavy);
            var ep = new ParticleSystem.EmitParams();
            for (; dmgSmokeAcc >= 1f; dmgSmokeAcc -= 1f)
            {
                int w = flat ? (d.punctFL > 0 ? 0 : d.punctFR > 0 ? 1 : d.punctRL > 0 ? 2 : 3) : -1;
                ep.position = (w >= 0 && wPiv[w] != null ? wPiv[w].position : transform.TransformPoint(0, 0.3f, -1.2f)) + Random.insideUnitSphere * 0.15f;
                ep.velocity = vel * 0.15f + Vector3.up * Random.Range(0.4f, 1.0f) + Random.insideUnitSphere * 0.4f;
                ep.startSize = Random.Range(0.6f, 1.0f); ep.startLifetime = Random.Range(0.9f, 1.5f);
                float g = flat ? Random.Range(0.75f, 0.85f) : Random.Range(0.35f, 0.5f); ep.startColor = new Color(g, g, g, 0.3f);
                ep.rotation = Random.Range(0f, 360f);
                smoke.Emit(ep, 1);
            }
        }
    }

    // physics -> visuals. heading: rad CCW from +x (physics); delta: + = left; spin: wheel angle (rad)
    public void Apply(double x, double y, double heading, double delta, double frontSpinRad, double rearSpinRad, double axLong, double ay)
    {
        if (WorldBuilder.Height == null)
        {
            transform.position = new Vector3((float)x, 0, (float)y);
            transform.rotation = Quaternion.Euler(0, 90f - (float)(heading * Mathf.Rad2Deg), 0);
        }
        else
        {
            // on hills: sit on the ground and lean with it
            float ch = (float)System.Math.Cos(heading), sh = (float)System.Math.Sin(heading);
            float h0 = WorldBuilder.H(x, y);
            float sf = (WorldBuilder.H(x + ch * 1.7, y + sh * 1.7) - WorldBuilder.H(x - ch * 1.7, y - sh * 1.7)) / 3.4f;   // slope ahead
            float ss = (WorldBuilder.H(x + sh * 0.8, y - ch * 0.8) - WorldBuilder.H(x - sh * 0.8, y + ch * 0.8)) / 1.6f;   // slope to the right
            var fwd = new Vector3(ch, sf, sh); var right = new Vector3(sh, ss, -ch);
            transform.position = new Vector3((float)x, h0, (float)y);
            transform.rotation = Quaternion.LookRotation(fwd, Vector3.Cross(fwd, right));
        }
        // body pitch/roll from g-forces: nose dives under braking, rolls to the outside in corners
        body.localRotation = Quaternion.Euler(Mathf.Clamp((float)-axLong * 0.09f, -1.7f, 1.7f), 0, Mathf.Clamp((float)-ay * 0.07f, -1.7f, 1.7f));
        float steer = -(float)(delta * Mathf.Rad2Deg);
        if (!wheelDmg) foreach (var p in frontPivots) { if (p != null) p.localRotation = Quaternion.Euler(0, steer, 0); }
        else
            for (int w = 0; w < 4; w++)
                if (wPiv[w] != null) wPiv[w].localRotation = Quaternion.Euler(0, (w < 2 ? steer : 0) + wToe[w], wCamber[w]);
        foreach (var s in frontSpin) if (s != null) s.localRotation = Quaternion.Euler((float)(frontSpinRad * Mathf.Rad2Deg), 0, 0);
        foreach (var s in rearSpin) if (s != null) s.localRotation = Quaternion.Euler((float)(rearSpinRad * Mathf.Rad2Deg), 0, 0);
        float dt = Time.deltaTime;
        if (dt > 0)
        {
            vel = Vector3.Lerp(vel, (transform.position - lastPos) / dt, (transform.position - lastPos).sqrMagnitude > 100f ? 0f : Mathf.Min(1f, dt * 10f));
            lastPos = transform.position;
            // wheel blur from how far the wheel turns per frame (the eye sees the spokes strobe above ~0.3 rad per frame)
            if (faces.Count > 0)
            {
                // Low: the wheel faces (transparent quads over the rims) are off
                if (facesOff != LowDetail) { facesOff = LowDetail; foreach (var fr in faces) if (fr != null) fr.enabled = !facesOff; }
                for (int k = 0; k < 2 && !facesOff; k++)
                {
                    float ang = k == 0 ? (float)frontSpinRad : (float)rearSpinRad; // (no per-frame array)
                    float step = float.IsNaN(lastSpin[k]) ? 0 : Mathf.Abs(Mathf.DeltaAngle(lastSpin[k] * Mathf.Rad2Deg, ang * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
                    lastSpin[k] = ang;
                    blur[k] = Mathf.Lerp(blur[k], Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.1f, 0.38f, step)), Mathf.Min(1f, dt * 8f));
                    if (faceMat[k] != null) faceMat[k].SetFloat(BlurId, blur[k]);
                }
            }
        }
    }
}
