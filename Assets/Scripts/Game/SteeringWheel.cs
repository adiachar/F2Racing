// SteeringWheel.cs — the player's steering wheel in the cockpit. The wheel is a real F1 wheel model (Resources/Wheel:
// wheel.bytes + atlas.bytes from Tools/Wheel/convert.py; "Steering wheel model by dhimasdc9 (CC BY 4.0)", see
// Resources/Wheel/CREDITS.txt): ~12.9k triangles, one draw call, SteeringWheel.shader with the model's albedo atlas.
// Our live 128 x 64 display is mapped onto the model's screen, its 15 shift lights are lit from _Led, its upper rear
// paddles flick on gear changes. Only if that file is missing (or fails to load) the built wheel below stands in:
// a 2026-style formula wheel (~2.6k triangles, ONE draw call, ONE material: SteeringWheel.shader):
//  * carbon body 280 x 168 mm with a flat top and bottom, shoulders over the grips and a foot under them, rounded edges
//  * two contoured rubber / alcantara grips (dark, matte, thicker where the palm is) where the driver's gloves hold it
//    at 9:15 (DriverGeo puts the hands on WheelGeo.GripL / its mirror)
//  * a raised gloss-black display housing: a colour LCD (128 x 64 texture drawn on the CPU, ~15 Hz, only while the
//    wheel is on screen) with the gear big in the middle, speed, live delta, ERS charge / mode and an rpm bar; above it
//    15 shift LEDs (green -> red -> blue, all flash blue at the shift point) lit in the shader from one uniform
//  * coloured push buttons and four rotary knobs (knurled, with an index line and tick marks; no lettering)
//  * carbon shift paddles behind (their tips show past the grips) that flick on every gear change (vertex shader:
//    one uniform, no extra renderer) + clutch paddles, a quick-release hub and the column stub
// Per vertex: colour rgb = base colour (linear), a = baked occlusion; uv0 = display UV / LED number / paddle hinge;
// uv1 = (clear coat, gloss, metal, kind); uv2.x = paddle side (-1 left, +1 right, 0 none), uv2.y = 1: albedo from the
// atlas at uv3 (the model only).
// Built in metres in the wheel's own frame (x right, y up, the driver looks at its -z face) and scaled into the car's
// body frame (the car's root is scaled by DriverGeo.K). Rivals keep the merged model's simple wheel.
// WheelGeo and WheelDash use only maths types, so they can be built and checked off-line (F2_DRIVER_HARNESS).
using System.Collections.Generic;
using UnityEngine;

public static class WheelGeo
{
    // ---- layout (metres, wheel frame)
    public const float FaceZ = -0.040f, BackZ = -0.008f;               // body: front (driver side) and back faces
    const float HousZ = FaceZ - 0.005f;                                 // display housing face, 5 mm proud
    public static readonly Vector2 ScreenMin = new Vector2(-0.050f, 0.010f), ScreenMax = new Vector2(0.050f, 0.060f); // 100 x 50 mm (2:1, as the texture)
    public const int LedCount = 15;
    const float LedY = 0.0668f, LedPitch = 0.0071f;
    // the left grip's axis where the left glove closes round it (the right one is the mirror image): DriverGeo.WristL.
    // Measured on the model's rubber grip by Tools/Wheel/convert.py (the built fallback's grips are put there too).
    public static readonly Vector3 GripL = new Vector3(-0.1246f, -0.0057f, -0.0333f);
    const float GripBotY = -0.066f, GripTopY = 0.054f;
    // paddles: hinge (x, z) of the right one; the left is the mirror
    const float PadHingeX = 0.036f, PadZ0 = -0.0058f, PadZ1 = -0.0025f;

    public const float KPlain = 0f, KCarbon = 1f, KFabric = 2f, KScreen = 3f, KLed = 4f;
    public struct Mat
    {
        public Color c; public Vector4 p;
        public Mat(float r, float g, float b, float coat, float gloss, float metal, float kind) { c = new Color(r, g, b, 1f); p = new Vector4(coat, gloss, metal, kind); }
    }
    // linear colours; (clear coat, gloss, metal) as CarPaint reads them
    static readonly Mat Carbon = new Mat(0.022f, 0.024f, 0.028f, 0.45f, 0.86f, 0.05f, KCarbon);
    static readonly Mat Rubber = new Mat(0.017f, 0.017f, 0.019f, 0.05f, 0.28f, 0f, KFabric);
    static readonly Mat GlossBlack = new Mat(0.008f, 0.008f, 0.010f, 0.7f, 0.93f, 0f, KPlain);
    static readonly Mat Screen = new Mat(0.003f, 0.003f, 0.004f, 1f, 0.97f, 0f, KScreen);
    static readonly Mat Led = new Mat(0.01f, 0.01f, 0.012f, 0.8f, 0.95f, 0f, KLed);
    static readonly Mat Knob = new Mat(0.05f, 0.05f, 0.055f, 0.3f, 0.62f, 0.65f, KPlain);
    static readonly Mat KnobCap = new Mat(0.02f, 0.02f, 0.022f, 0.35f, 0.7f, 0.2f, KPlain);
    static readonly Mat Mark = new Mat(0.55f, 0.56f, 0.58f, 0.2f, 0.5f, 0f, KPlain);
    static readonly Mat Metal = new Mat(0.30f, 0.30f, 0.32f, 0.3f, 0.78f, 0.9f, KPlain);
    static readonly Mat Anodised = new Mat(0.45f, 0.03f, 0.025f, 0.35f, 0.72f, 0.6f, KPlain);
    static Mat Button(float r, float g, float b) => new Mat(r, g, b, 0.3f, 0.66f, 0f, KPlain);

    public sealed class Data
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Color> c = new List<Color>();
        public readonly List<Vector2> uv0 = new List<Vector2>(), uv2 = new List<Vector2>();
        public readonly List<Vector4> uv1 = new List<Vector4>();
        public readonly List<int> t = new List<int>();
        public float scale = 1f; // metres -> mesh units
    }

    static int V(Data d, Vector3 p, Vector3 n, Mat m, float ao, Vector2 uv = default, float pad = 0f)
    {
        d.v.Add(p * d.scale); d.n.Add(n.normalized);
        var c = m.c; c.a = Mathf.Clamp01(ao); d.c.Add(c);
        d.uv0.Add(pad != 0f ? uv * d.scale : uv); d.uv1.Add(m.p); d.uv2.Add(new Vector2(pad, 0f));
        return d.v.Count - 1;
    }
    // a triangle facing 'want' (Unity: visible from the side cross(b - a, c - a) points to)
    static void Tri(Data d, int a, int b, int c, Vector3 want)
    {
        var cr = Vector3.Cross(d.v[b] - d.v[a], d.v[c] - d.v[a]);
        if (cr.sqrMagnitude < 1e-20f) return;
        if (Vector3.Dot(cr, want) >= 0f) { d.t.Add(a); d.t.Add(b); d.t.Add(c); } else { d.t.Add(a); d.t.Add(c); d.t.Add(b); }
    }
    static void Quad(Data d, int a, int b, int c, int e) // a-b-c-e round the quad, facing its vertices' normals
    {
        Vector3 w = d.n[a] + d.n[b] + d.n[c] + d.n[e];
        Tri(d, a, b, c, w); Tri(d, a, c, e, w);
    }

    // ---- 2D outlines: corners rounded with the given radii (arcs every ~22 deg), counter-clockwise
    static List<Vector2> Rounded(Vector2[] P, float[] R, float step = 22.5f)
    {
        var o = new List<Vector2>(); int n = P.Length;
        for (int i = 0; i < n; i++)
        {
            Vector2 p = P[i], a = P[(i + n - 1) % n], b = P[(i + 1) % n];
            Vector2 d1 = (a - p).normalized, d2 = (b - p).normalized;
            float half = Mathf.Acos(Mathf.Clamp(Vector2.Dot(d1, d2), -1f, 1f)) * 0.5f;
            if (R[i] <= 0f || half > 1.55f) { o.Add(p); continue; }
            float r = R[i], tl = r / Mathf.Tan(half);
            Vector2 s = p + d1 * tl, e = p + d2 * tl, c = p + (d1 + d2).normalized * (r / Mathf.Sin(half));
            float a0 = Mathf.Atan2(s.y - c.y, s.x - c.x) * Mathf.Rad2Deg, a1 = Mathf.Atan2(e.y - c.y, e.x - c.x) * Mathf.Rad2Deg;
            float da = Mathf.DeltaAngle(a0, a1);
            int k = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(da) / step));
            for (int j = 0; j <= k; j++) { float ang = (a0 + da * j / k) * Mathf.Deg2Rad; o.Add(c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r); }
        }
        if (Area(o) < 0f) o.Reverse();
        return o;
    }
    static float Area(List<Vector2> P) { float a = 0f; for (int i = 0; i < P.Count; i++) { var p = P[i]; var q = P[(i + 1) % P.Count]; a += p.x * q.y - q.x * p.y; } return a * 0.5f; }
    static List<Vector2> RoundRect(float x0, float y0, float x1, float y1, float r) =>
        Rounded(new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) }, new[] { r, r, r, r });

    // ear clipping of a simple counter-clockwise polygon
    static List<int> EarClip(List<Vector2> P)
    {
        var idx = new List<int>(); for (int i = 0; i < P.Count; i++) idx.Add(i);
        var tris = new List<int>(); int guard = 0;
        while (idx.Count > 3 && guard++ < 4096)
        {
            int best = -1; float bestQ = -1f;
            for (int i = 0; i < idx.Count; i++)
            {
                int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                Vector2 a = P[ia], b = P[ib], c = P[ic];
                float cr = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                if (cr <= 1e-12f) continue; // reflex corner
                bool inside = false;
                for (int j = 0; j < idx.Count && !inside; j++)
                {
                    int q = idx[j]; if (q == ia || q == ib || q == ic) continue;
                    inside = InTri(P[q], a, b, c);
                }
                if (inside) continue;
                // the fattest ear first (fewer slivers on the long straight edges)
                float l = (a - b).sqrMagnitude + (b - c).sqrMagnitude + (c - a).sqrMagnitude, qual = cr / Mathf.Max(l, 1e-12f);
                if (qual > bestQ) { bestQ = qual; best = i; }
            }
            if (best < 0) break;
            tris.Add(idx[(best + idx.Count - 1) % idx.Count]); tris.Add(idx[best]); tris.Add(idx[(best + 1) % idx.Count]);
            idx.RemoveAt(best);
        }
        if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
        return tris;
    }
    static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
        float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
        float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
        bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(neg && pos);
    }

    // A slab from a counter-clockwise outline between zf (front, driver side) and zb, its edge rounded with radius bev
    // on the front (and on the back when backBev > 0). Caps where asked. pad != 0: a paddle (hinge in uv0).
    static void Slab(Data d, List<Vector2> o, float zf, float zb, float bev, float backBev, Mat side, Mat front, Mat back,
                     bool capF, bool capB, float aoSide = 0.85f, float pad = 0f, Vector2 hinge = default)
    {
        int n = o.Count; var nrm = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 a = o[(i + n - 1) % n], p = o[i], b = o[(i + 1) % n];
            Vector2 e1 = (p - a).normalized, e2 = (b - p).normalized;
            Vector2 n1 = new Vector2(e1.y, -e1.x), n2 = new Vector2(e2.y, -e2.x);
            nrm[i] = (n1 + n2).normalized;
        }
        // profile: (angle from the face normal, front?)
        var rings = new List<(float phi, bool fr)>();
        if (bev > 0f) { rings.Add((0f, true)); rings.Add((45f, true)); }
        rings.Add((90f, true)); rings.Add((90f, false));
        if (backBev > 0f) { rings.Add((45f, false)); rings.Add((0f, false)); }
        int R = rings.Count, b0 = d.v.Count;
        for (int r = 0; r < R; r++)
        {
            float ph = rings[r].phi * Mathf.Deg2Rad, bv = rings[r].fr ? bev : backBev, sgn = rings[r].fr ? -1f : 1f;
            float z = rings[r].fr ? zf + bv * (1f - Mathf.Cos(ph)) : zb - bv * (1f - Mathf.Cos(ph));
            for (int i = 0; i < n; i++)
            {
                Vector2 q = o[i] - nrm[i] * (bv * (1f - Mathf.Sin(ph)));
                var nn = new Vector3(nrm[i].x * Mathf.Sin(ph), nrm[i].y * Mathf.Sin(ph), sgn * Mathf.Cos(ph));
                float ao = rings[r].fr ? Mathf.Lerp(1f, aoSide, Mathf.Sin(ph)) : aoSide * 0.7f;
                V(d, new Vector3(q.x, q.y, z), nn, side, ao, hinge, pad);
            }
        }
        for (int r = 0; r < R - 1; r++)
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Quad(d, b0 + r * n + i, b0 + (r + 1) * n + i, b0 + (r + 1) * n + j, b0 + r * n + j);
            }
        if (capF) Cap(d, o, nrm, bev, zf, -1f, front, 1f, pad, hinge);
        if (capB) Cap(d, o, nrm, backBev, zb, 1f, back, 0.45f, pad, hinge);
    }
    static void Cap(Data d, List<Vector2> o, Vector2[] nrm, float inset, float z, float sgn, Mat m, float ao, float pad, Vector2 hinge)
    {
        var q = new List<Vector2>(o.Count);
        for (int i = 0; i < o.Count; i++) q.Add(o[i] - nrm[i] * inset);
        int b0 = d.v.Count; var nn = new Vector3(0f, 0f, sgn);
        for (int i = 0; i < q.Count; i++) V(d, new Vector3(q[i].x, q[i].y, z), nn, m, ao, hinge, pad);
        var tr = EarClip(q);
        for (int i = 0; i < tr.Count; i += 3) Tri(d, b0 + tr[i], b0 + tr[i + 1], b0 + tr[i + 2], nn);
    }

    // a round push button / knob on the front face (axis -z): side wall from inside the face, a domed cap
    static void Button(Data d, float x, float y, float r, float h, int segs, Mat side, Mat cap, bool knurl = false)
    {
        float z0 = FaceZ + 0.001f, z1 = FaceZ - h, zc = z1 - 0.0012f;
        int b0 = d.v.Count;
        for (int ring = 0; ring < 3; ring++)
            for (int i = 0; i < segs; i++)
            {
                float a = i * 6.2831853f / segs, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                float rr = knurl && ring < 2 && (i & 1) == 1 ? r * 0.93f : r;
                if (ring == 2) rr = r * 0.8f;
                var rad = new Vector3(ca, sa, 0f);
                Vector3 p = new Vector3(x + ca * rr, y + sa * rr, ring == 0 ? z0 : ring == 1 ? z1 : z1 - 0.0008f);
                Vector3 nn = ring == 0 ? rad : ring == 1 ? (rad + new Vector3(0, 0, -0.8f)) : (rad * 0.35f + new Vector3(0, 0, -1f));
                V(d, p, nn, ring == 2 ? cap : side, ring == 0 ? 0.5f : 1f);
            }
        for (int i = 0; i < segs; i++) { int j = (i + 1) % segs; Quad(d, b0 + i, b0 + segs + i, b0 + segs + j, b0 + j); }
        // cap: its own copy of the top edge in the cap colour (so the side colour does not bleed in), the inner ring, the centre
        int e0 = d.v.Count;
        for (int i = 0; i < segs; i++) { var p = d.v[b0 + segs + i] / d.scale; var nn = d.n[b0 + segs + i]; V(d, p, nn, cap, 1f); }
        int cc = V(d, new Vector3(x, y, zc), new Vector3(0, 0, -1f), cap, 1f);
        for (int i = 0; i < segs; i++)
        {
            int j = (i + 1) % segs;
            Quad(d, e0 + i, b0 + 2 * segs + i, b0 + 2 * segs + j, e0 + j);
            Tri(d, cc, b0 + 2 * segs + i, b0 + 2 * segs + j, new Vector3(0, 0, -1f));
        }
    }

    // a flat mark on the front face (tick, index line): a quad from p0 to p1, w wide, at z
    static void Mark2(Data d, Vector2 p0, Vector2 p1, float w, float z, Mat m)
    {
        Vector2 t = (p1 - p0).normalized, s = new Vector2(-t.y, t.x) * (w * 0.5f);
        var nn = new Vector3(0, 0, -1f);
        int a = V(d, new Vector3(p0.x - s.x, p0.y - s.y, z), nn, m, 1f), b = V(d, new Vector3(p1.x - s.x, p1.y - s.y, z), nn, m, 1f);
        int c = V(d, new Vector3(p1.x + s.x, p1.y + s.y, z), nn, m, 1f), e = V(d, new Vector3(p0.x + s.x, p0.y + s.y, z), nn, m, 1f);
        Quad(d, a, b, c, e);
    }

    // rotary: knurled anodised knob, a white index line on its cap pointing to 'dir' degrees, tick marks round it
    static void Rotary(Data d, float x, float y, float r, float dirDeg, int ticks)
    {
        Button(d, x, y, r, 0.0075f, 14, Knob, KnobCap, true);
        float a = dirDeg * Mathf.Deg2Rad; var u = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        Mark2(d, new Vector2(x, y) + u * (r * 0.25f), new Vector2(x, y) + u * (r * 0.78f), 0.0014f, FaceZ - 0.0075f - 0.0016f, Mark);
        for (int i = 0; i < ticks; i++)
        {
            float ta = (-135f + 270f * i / (ticks - 1) + 90f) * Mathf.Deg2Rad; var tu = new Vector2(Mathf.Cos(ta), Mathf.Sin(ta));
            Mark2(d, new Vector2(x, y) + tu * (r + 0.0028f), new Vector2(x, y) + tu * (r + 0.0052f), 0.0011f, FaceZ - 0.0003f, Mark);
        }
    }

    // a cylinder along z (hub, column)
    static void Cyl(Data d, float r, float z0, float z1, int segs, Mat m, float ao, bool cap0, bool cap1, float y = 0f)
    {
        int b0 = d.v.Count;
        for (int ring = 0; ring < 2; ring++)
            for (int i = 0; i < segs; i++)
            {
                float a = i * 6.2831853f / segs; var rad = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                V(d, new Vector3(rad.x * r, y + rad.y * r, ring == 0 ? z0 : z1), rad, m, ao);
            }
        for (int i = 0; i < segs; i++) { int j = (i + 1) % segs; Quad(d, b0 + i, b0 + segs + i, b0 + segs + j, b0 + j); }
        for (int k = 0; k < 2; k++)
        {
            if (k == 0 ? !cap0 : !cap1) continue;
            float z = k == 0 ? z0 : z1; var nn = new Vector3(0, 0, k == 0 ? -1f : 1f);
            int c0 = d.v.Count, cc = V(d, new Vector3(0, y, z), nn, m, ao);
            for (int i = 0; i < segs; i++) { float a = i * 6.2831853f / segs; V(d, new Vector3(Mathf.Cos(a) * r, y + Mathf.Sin(a) * r, z), nn, m, ao); }
            for (int i = 0; i < segs; i++) Tri(d, cc, c0 + 1 + i, c0 + 1 + (i + 1) % segs, nn);
        }
    }

    // grip: superelliptic rings along y, thicker where the palm is, bowed out a little; sx = -1 left, +1 right
    static void Grip(Data d, float sx)
    {
        float[] ts = { 0f, 0.06f, 0.15f, 0.27f, 0.4f, 0.52f, 0.64f, 0.76f, 0.86f, 0.94f, 1f };
        const int cols = 14; const float E = 2.4f;
        int rows = ts.Length, b0 = d.v.Count;
        float cx0 = -GripL.x - 0.0025f; // the axis bows 2.5 mm out at mid height (through GripL at the hand)
        for (int r = 0; r < rows; r++)
        {
            float t = ts[r], y = Mathf.Lerp(GripBotY, GripTopY, t), s = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(Mathf.PI * t)), 0.6f);
            float cx = cx0 + 0.0025f * Mathf.Max(0f, Mathf.Sin(Mathf.PI * t));
            float a = 0.0135f + 0.0032f * s, b = 0.016f + 0.0045f * s;   // half width (x), half depth (z): ~33 x 41 mm at the hand
            float flare = 0.0018f * Mathf.Clamp01(1f - Mathf.Abs(t - 0.12f) / 0.1f); // a small flare under the hand
            a += flare; b += flare;
            for (int c = 0; c < cols; c++)
            {
                float ang = c * 6.2831853f / cols, ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                float px = Mathf.Sign(ca) * Mathf.Pow(Mathf.Abs(ca), 2f / E), pz = Mathf.Sign(sa) * Mathf.Pow(Mathf.Abs(sa), 2f / E);
                // palm swell on the outer back quarter (where the palm and fingers press)
                float swell = 1f + 0.08f * s * Mathf.Clamp01(px) * Mathf.Clamp01(0.3f + pz);
                var p = new Vector3(sx * (cx + a * px * swell), y, GripL.z + b * pz * swell);
                float gx = Mathf.Sign(px) * Mathf.Pow(Mathf.Abs(px), E - 1f) / a, gz = Mathf.Sign(pz) * Mathf.Pow(Mathf.Abs(pz), E - 1f) / b;
                var nn = new Vector3(sx * gx, 0f, gz).normalized;
                float ao = Mathf.Lerp(0.62f, 0.95f, Mathf.Clamp01(0.5f + 0.5f * px)) * Mathf.Lerp(0.7f, 1f, s);
                V(d, p, nn, Rubber, ao);
            }
        }
        for (int r = 0; r < rows - 1; r++)
            for (int c = 0; c < cols; c++)
            {
                int j = (c + 1) % cols;
                Quad(d, b0 + r * cols + c, b0 + (r + 1) * cols + c, b0 + (r + 1) * cols + j, b0 + r * cols + j);
            }
        // end caps (inside the body's shoulder and foot)
        for (int k = 0; k < 2; k++)
        {
            int row = k == 0 ? 0 : rows - 1; var nn = new Vector3(0, k == 0 ? -1f : 1f, 0);
            Vector3 ctr = Vector3.zero; for (int c = 0; c < cols; c++) ctr += d.v[b0 + row * cols + c] / d.scale; ctr /= cols;
            int c0 = d.v.Count, cc = V(d, ctr, nn, Rubber, 0.6f);
            for (int c = 0; c < cols; c++) V(d, d.v[b0 + row * cols + c] / d.scale, nn, Rubber, 0.6f);
            for (int c = 0; c < cols; c++) Tri(d, cc, c0 + 1 + c, c0 + 1 + (c + 1) % cols, nn);
        }
    }

    static List<Vector2> Mirror(List<Vector2> o) { var m = new List<Vector2>(o.Count); for (int i = o.Count - 1; i >= 0; i--) m.Add(new Vector2(-o[i].x, o[i].y)); return m; }

    // the whole wheel; scale: metres -> mesh units
    public static Data Build(float scale)
    {
        var d = new Data { scale = scale };
        // body outline (right half, clockwise from the top centre; mirrored): flat top, shoulder over the grip, the
        // waist inside the grip, a foot under it, flat bottom
        var half = new[] {
            new Vector2(0.140f, 0.080f), new Vector2(0.140f, 0.051f), new Vector2(0.110f, 0.043f), new Vector2(0.110f, -0.056f),
            new Vector2(0.131f, -0.062f), new Vector2(0.133f, -0.077f), new Vector2(0.116f, -0.088f) };
        var hr = new[] { 0.016f, 0.008f, 0.006f, 0.006f, 0.006f, 0.008f, 0.012f };
        var P = new List<Vector2>(); var Rr = new List<float>();
        for (int i = 0; i < half.Length; i++) { P.Add(half[i]); Rr.Add(hr[i]); }
        for (int i = half.Length - 1; i >= 0; i--) { P.Add(new Vector2(-half[i].x, half[i].y)); Rr.Add(hr[i]); }
        var body = Rounded(P.ToArray(), Rr.ToArray());
        Slab(d, body, FaceZ, BackZ, 0.004f, 0.003f, Carbon, Carbon, Carbon, true, true, 0.8f);

        // display housing (gloss black, 5 mm proud), the screen and the shift-light row in it
        Slab(d, RoundRect(-0.061f, 0.003f, 0.061f, 0.0745f, 0.008f), HousZ, FaceZ + 0.002f, 0.0022f, 0f, GlossBlack, GlossBlack, GlossBlack, true, false, 0.9f);
        {
            var nn = new Vector3(0, 0, -1f); float z = HousZ - 0.0004f;
            int a = V(d, new Vector3(ScreenMin.x, ScreenMin.y, z), nn, Screen, 1f, new Vector2(0, 0));
            int b = V(d, new Vector3(ScreenMax.x, ScreenMin.y, z), nn, Screen, 1f, new Vector2(1, 0));
            int c = V(d, new Vector3(ScreenMax.x, ScreenMax.y, z), nn, Screen, 1f, new Vector2(1, 1));
            int e = V(d, new Vector3(ScreenMin.x, ScreenMax.y, z), nn, Screen, 1f, new Vector2(0, 1));
            Quad(d, a, b, c, e);
            for (int i = 0; i < LedCount; i++)
            {
                float x = (i - (LedCount - 1) * 0.5f) * LedPitch; const float hw = 0.0025f, hh = 0.0016f;
                var uv = new Vector2(i, 0f);
                int l0 = V(d, new Vector3(x - hw, LedY - hh, z), nn, Led, 1f, uv), l1 = V(d, new Vector3(x + hw, LedY - hh, z), nn, Led, 1f, uv);
                int l2 = V(d, new Vector3(x + hw, LedY + hh, z), nn, Led, 1f, uv), l3 = V(d, new Vector3(x - hw, LedY + hh, z), nn, Led, 1f, uv);
                Quad(d, l0, l1, l2, l3);
            }
        }

        // push buttons: a column each side of the display, a row under it, one on each shoulder
        Mat red = Button(0.75f, 0.035f, 0.03f), blue = Button(0.03f, 0.11f, 0.65f), yellow = Button(0.8f, 0.55f, 0.02f),
            green = Button(0.04f, 0.45f, 0.09f), orange = Button(0.85f, 0.22f, 0.015f), white = Button(0.62f, 0.63f, 0.65f),
            purple = Button(0.22f, 0.05f, 0.45f), cyan = Button(0.02f, 0.4f, 0.55f), dark = Button(0.03f, 0.03f, 0.034f);
        Button(d, -0.080f, 0.058f, 0.0058f, 0.0045f, 10, dark, blue);
        Button(d, -0.080f, 0.035f, 0.0058f, 0.0045f, 10, dark, yellow);
        Button(d, -0.080f, 0.012f, 0.0058f, 0.0045f, 10, dark, green);
        Button(d, 0.080f, 0.058f, 0.0058f, 0.0045f, 10, dark, red);
        Button(d, 0.080f, 0.035f, 0.0058f, 0.0045f, 10, dark, orange);
        Button(d, 0.080f, 0.012f, 0.0058f, 0.0045f, 10, dark, white);
        Button(d, -0.040f, -0.011f, 0.0052f, 0.004f, 10, dark, purple);
        Button(d, -0.014f, -0.011f, 0.0052f, 0.004f, 10, dark, dark);
        Button(d, 0.014f, -0.011f, 0.0052f, 0.004f, 10, dark, dark);
        Button(d, 0.040f, -0.011f, 0.0052f, 0.004f, 10, dark, cyan);
        Button(d, -0.100f, 0.066f, 0.0048f, 0.004f, 10, dark, white);
        Button(d, 0.100f, 0.066f, 0.0048f, 0.004f, 10, dark, blue);
        // rotaries: two in the middle, two in the lower corners
        Rotary(d, -0.028f, -0.046f, 0.0092f, 120f, 7);
        Rotary(d, 0.028f, -0.046f, 0.0092f, 60f, 7);
        Rotary(d, -0.076f, -0.040f, 0.0082f, 90f, 5);
        Rotary(d, 0.076f, -0.040f, 0.0082f, 150f, 5);

        // grips
        Grip(d, -1f); Grip(d, 1f);

        // shift paddles (behind, tips past the grips; flick in the shader) and the clutch paddles under them
        var padR = Rounded(new[] { new Vector2(PadHingeX, 0.015f), new Vector2(0.118f, 0.009f), new Vector2(0.153f, 0.013f),
                                   new Vector2(0.155f, 0.044f), new Vector2(0.118f, 0.050f), new Vector2(PadHingeX, 0.040f) },
                           new[] { 0f, 0.01f, 0.006f, 0.006f, 0.01f, 0f }, 30f);
        Slab(d, padR, PadZ0, PadZ1, 0f, 0f, Carbon, Carbon, Carbon, true, true, 0.6f, 1f, new Vector2(PadHingeX, (PadZ0 + PadZ1) * 0.5f));
        Slab(d, Mirror(padR), PadZ0, PadZ1, 0f, 0f, Carbon, Carbon, Carbon, true, true, 0.6f, -1f, new Vector2(-PadHingeX, (PadZ0 + PadZ1) * 0.5f));
        var cl = Rounded(new[] { new Vector2(0.040f, -0.041f), new Vector2(0.140f, -0.046f), new Vector2(0.146f, -0.033f),
                                 new Vector2(0.138f, -0.021f), new Vector2(0.040f, -0.025f) },
                         new[] { 0f, 0.006f, 0.005f, 0.006f, 0f }, 30f);
        Slab(d, cl, PadZ0, PadZ1, 0f, 0f, Carbon, Carbon, Carbon, true, true, 0.55f);
        Slab(d, Mirror(cl), PadZ0, PadZ1, 0f, 0f, Carbon, Carbon, Carbon, true, true, 0.55f);

        // quick-release hub, anodised collar, column stub (into the dash)
        Cyl(d, 0.027f, BackZ - 0.002f, BackZ + 0.022f, 14, Metal, 0.55f, false, false, -0.004f);
        Cyl(d, 0.021f, BackZ + 0.022f, BackZ + 0.036f, 14, Anodised, 0.55f, true, false, -0.004f);
        Cyl(d, 0.015f, BackZ + 0.036f, BackZ + 0.11f, 10, Metal, 0.45f, false, true, -0.004f);
        return d;
    }
}

// The display: 128 x 64 RGBA, drawn on the CPU into a pixel array (rows top-down here, flipped on write: a Texture2D's
// row 0 is the bottom). A static frame is drawn once; each update copies it and draws the numbers with a small vector
// stroke font (anti-aliased line segments), so any size stays smooth. Allocates nothing per update.
public sealed class WheelDash
{
    public const int W = 128, H = 64;
    public readonly Color32[] px = new Color32[W * H];
    readonly Color32[] bg = new Color32[W * H], gearLayer = new Color32[W * H]; // the frame; the frame + the gear
    readonly char[] num = new char[8];
    char shownGear; Color32 shownGearCol;

    static readonly Color32 Bg = new Color32(5, 6, 9, 255), Frame = new Color32(34, 38, 48, 255), Faint = new Color32(120, 128, 142, 255),
        White = new Color32(240, 242, 246, 255), Green = new Color32(60, 230, 95, 255), Red = new Color32(255, 70, 58, 255),
        Blue = new Color32(80, 140, 255, 255), Amber = new Color32(255, 172, 30, 255), Idle = new Color32(196, 202, 212, 255),
        Dim = new Color32(26, 29, 36, 255);

    // strokes of each glyph on a 4 x 6 grid (y down): polylines as point lists, NaN separates polylines
    const float X = float.NaN;
    static readonly Dictionary<char, float[]> Glyphs = new Dictionary<char, float[]> {
        { '0', new float[] { 0,0, 4,0, 4,6, 0,6, 0,0 } },
        { '1', new float[] { 0.8f,1.2f, 2.2f,0, 2.2f,6 } },
        { '2', new float[] { 0,0, 4,0, 4,3, 0,3, 0,6, 4,6 } },
        { '3', new float[] { 0,0, 4,0, 4,6, 0,6, X,X, 1,3, 4,3 } },
        { '4', new float[] { 0,0, 0,3.6f, 4,3.6f, X,X, 3.2f,0, 3.2f,6 } },
        { '5', new float[] { 4,0, 0,0, 0,3, 4,3, 4,6, 0,6 } },
        { '6', new float[] { 4,0, 0,0, 0,6, 4,6, 4,3, 0,3 } },
        { '7', new float[] { 0,0, 4,0, 4,1.2f, 1.6f,6 } },
        { '8', new float[] { 0,0, 4,0, 4,6, 0,6, 0,0, X,X, 0,3, 4,3 } },
        { '9', new float[] { 4,3, 0,3, 0,0, 4,0, 4,6, 0,6 } },
        { 'N', new float[] { 0,6, 0,0, 4,6, 4,0 } },
        { 'R', new float[] { 0,6, 0,0, 4,0, 4,3, 0,3, X,X, 1.8f,3, 4,6 } },
        { 'K', new float[] { 0,0, 0,6, X,X, 4,0, 0,3.6f, X,X, 1.4f,2.6f, 4,6 } },
        { 'M', new float[] { 0,6, 0,0, 2,3, 4,0, 4,6 } },
        { 'H', new float[] { 0,0, 0,6, X,X, 4,0, 4,6, X,X, 0,3, 4,3 } },
        { 'P', new float[] { 0,6, 0,0, 4,0, 4,3, 0,3 } },
        { 'E', new float[] { 4,0, 0,0, 0,6, 4,6, X,X, 0,3, 3,3 } },
        { 'S', new float[] { 4,0, 0,0, 0,3, 4,3, 4,6, 0,6 } },
        { 'W', new float[] { 0,0, 0.8f,6, 2,2.6f, 3.2f,6, 4,0 } },
        { '+', new float[] { 2,1.4f, 2,4.6f, X,X, 0.4f,3, 3.6f,3 } },
        { '-', new float[] { 0.4f,3, 3.6f,3 } },
        { '.', new float[] { 0.6f,6, 0.6f,6 } },
        { '%', new float[] { 0,6, 4,0, X,X, 0.3f,0.6f, 0.3f,0.6f, X,X, 3.7f,5.4f, 3.7f,5.4f } },
    };

    // the polylines as plain segments (x0, y0, x1, y1), made once
    static Dictionary<char, float[]> segs;
    static Dictionary<char, float[]> Segs()
    {
        if (segs != null) return segs;
        segs = new Dictionary<char, float[]>();
        foreach (var kv in Glyphs)
        {
            var g = kv.Value; var l = new List<float>();
            for (int i = 0; i + 3 < g.Length; i += 2)
                if (!float.IsNaN(g[i]) && !float.IsNaN(g[i + 2])) { l.Add(g[i]); l.Add(g[i + 1]); l.Add(g[i + 2]); l.Add(g[i + 3]); }
            segs[kv.Key] = l.ToArray();
        }
        return segs;
    }

    public WheelDash()
    {
        for (int i = 0; i < bg.Length; i++) bg[i] = Bg;
        System.Array.Copy(bg, px, px.Length);
        // panel frames: left (speed, delta, rpm), the gear in the middle, right (ERS)
        RoundBox(44.5f, 2.5f, 83.5f, 61.5f, 5f, Frame, false);
        Text("ERS", 91f, 4f, 0.9f, 0.6f, Faint);
        System.Array.Copy(px, bg, px.Length);
    }

    // speed in whole units; delta in s (NaN: none); soc 0..1; ers: 0 idle, 1 deploy, 2 harvest, 3 superclip; kw >= 0;
    // rpm 0..1 of the limiter; flash: at the shift point (the gear turns blue with the LEDs)
    public void Draw(int gear, bool reverse, int speed, bool mph, double delta, float soc, int ers, int kw, float rpm, bool flash)
    {
        // gear (the biggest glyph: drawn only when it changes, into its own layer over the frame)
        char g = reverse ? 'R' : gear <= 0 ? 'N' : (char)('0' + Mathf.Clamp(gear, 0, 9));
        Color32 gc = flash ? Blue : reverse ? Amber : gear <= 0 ? Green : White;
        if (g != shownGear || gc.r != shownGearCol.r || gc.g != shownGearCol.g || gc.b != shownGearCol.b)
        {
            shownGear = g; shownGearCol = gc;
            System.Array.Copy(bg, px, px.Length);
            Glyph(g, 64f - 16f, 8f, 8f, 2.7f, gc);
            System.Array.Copy(px, gearLayer, px.Length);
        }
        else System.Array.Copy(gearLayer, px, px.Length);
        // speed (right-aligned) and its unit
        RightInt(speed, 38f, 5f, 2.1f, 1.05f, White);
        Text(mph ? "MPH" : "KMH", 24.4f, 21.5f, 0.85f, 0.55f, Faint);
        // live delta to the best lap
        if (double.IsNaN(delta)) { Fill(3, 31, 38, 46, Dim); Text("-.--", 9f, 34.6f, 1.3f, 0.8f, Faint); }
        else
        {
            bool up = delta > 0; double a = System.Math.Min(System.Math.Abs(delta), 9.99);
            int cs = (int)System.Math.Round(a * 100.0);
            Color32 dc = up ? Red : Green;
            Fill(3, 31, 38, 46, up ? new Color32(62, 14, 12, 255) : new Color32(10, 52, 22, 255));
            num[0] = up ? '+' : '-'; num[1] = (char)('0' + cs / 100); num[2] = '.'; num[3] = (char)('0' + cs / 10 % 10); num[4] = (char)('0' + cs % 10);
            float w = Width(num, 5, 1.3f), x = 20.5f - w * 0.5f;
            for (int i = 0; i < 5; i++) x += Glyph(num[i], x, 34.6f, 1.3f, 0.8f, dc);
        }
        // rpm bar
        Fill(3, 52, 38, 56, Dim);
        int rw = Mathf.RoundToInt(Mathf.Clamp01(rpm) * 35f);
        if (rw > 0) Fill(3, 52, 3 + rw, 56, rpm > 0.94f ? (flash ? Blue : Red) : rpm > 0.85f ? Green : Idle);
        // ERS: charge %, battery bar coloured by the mode, MGU-K power
        int pct = Mathf.RoundToInt(Mathf.Clamp01(soc) * 100f);
        RightInt(pct, 117f, 13f, 2.0f, 1.0f, White);
        Glyph('%', 119.5f, 19f, 0.9f, 0.55f, Faint);
        Color32 mc = soc < 0.15f ? Red : ers == 1 ? Blue : ers == 2 ? Green : ers == 3 ? Amber : Idle;
        RoundBox(89.5f, 31.5f, 124.5f, 40.5f, 1.5f, Frame, false);
        int bw = Mathf.RoundToInt(Mathf.Clamp01(soc) * 33f);
        if (bw > 0) Fill(91, 33, 91 + bw, 39, mc);
        if (ers != 0 && kw > 0)
        {
            RightInt(kw, 111.5f, 46f, 1.25f, 0.75f, mc);
            Text("KW", 115.5f, 49f, 0.75f, 0.5f, Faint);
            Glyph(ers == 1 ? '+' : '-', 87.5f, 46f, 1.25f, 0.75f, mc);
        }
    }

    // ---- drawing helpers
    void Put(int x, int y, Color32 c, float k)
    {
        if ((uint)x >= W || (uint)y >= H || k <= 0f) return;
        int i = (H - 1 - y) * W + x; var o = px[i];
        if (k >= 1f) { px[i] = c; return; }
        px[i] = new Color32((byte)(o.r + (c.r - o.r) * k), (byte)(o.g + (c.g - o.g) * k), (byte)(o.b + (c.b - o.b) * k), 255);
    }
    void Fill(int x0, int y0, int x1, int y1, Color32 c) { for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) Put(x, y, c, 1f); }
    // rounded box (outline 1 px, or filled), anti-aliased
    void RoundBox(float x0, float y0, float x1, float y1, float r, Color32 c, bool fill)
    {
        float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f, hx = (x1 - x0) * 0.5f - r, hy = (y1 - y0) * 0.5f - r;
        for (int y = (int)y0 - 1; y <= (int)y1 + 1; y++)
            for (int x = (int)x0 - 1; x <= (int)x1 + 1; x++)
            {
                float qx = Mathf.Max(Mathf.Abs(x + 0.5f - cx) - hx, 0f), qy = Mathf.Max(Mathf.Abs(y + 0.5f - cy) - hy, 0f);
                float dd = Mathf.Sqrt(qx * qx + qy * qy) - r;
                float k = fill ? Mathf.Clamp01(0.5f - dd) : Mathf.Clamp01(1f - Mathf.Abs(dd));
                Put(x, y, c, k);
            }
    }
    static float Adv(char ch, float s) => (ch == '.' ? 2.0f : ch == '1' ? 4.6f : 5.6f) * s;
    static float Width(char[] t, int n, float s) { float w = 0f; for (int i = 0; i < n; i++) w += Adv(t[i], s); return w - 1.6f * s; }
    void Text(string t, float x, float y, float s, float hw, Color32 c) { for (int i = 0; i < t.Length; i++) x += Glyph(t[i], x, y, s, hw, c); }
    // an integer right-aligned at xr; returns the left edge
    float RightInt(int v, float xr, float y, float s, float hw, Color32 c)
    {
        v = Mathf.Max(0, v); int n = 0;
        do { num[n++] = (char)('0' + v % 10); v /= 10; } while (v > 0 && n < 6);
        float x = xr + 1.6f * s; // (the advance includes the gap after the glyph)
        for (int i = 0; i < n; i++) { x -= Adv(num[i], s); Glyph(num[i], x, y, s, hw, c); }
        return x;
    }
    // one glyph, top-left at (x, y), s pixels per grid unit, hw = half the stroke width; returns the advance
    float Glyph(char ch, float x, float y, float s, float hw, Color32 c)
    {
        if (!Segs().TryGetValue(ch, out var g)) return Adv(ch, s);
        if (ch == '1') x -= 0.4f * s;
        int xa = Mathf.FloorToInt(x - hw - 1f), xb = Mathf.CeilToInt(x + 4f * s + hw + 1f);
        int ya = Mathf.FloorToInt(y - hw - 1f), yb = Mathf.CeilToInt(y + 6f * s + hw + 1f);
        for (int py = ya; py <= yb; py++)
            for (int pxl = xa; pxl <= xb; pxl++)
            {
                float qx = pxl + 0.5f, qy = py + 0.5f, best = 1e9f;
                for (int i = 0; i < g.Length; i += 4)
                {
                    float ax = x + g[i] * s, ay = y + g[i + 1] * s, bx = x + g[i + 2] * s, by = y + g[i + 3] * s;
                    float dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                    float t = l2 > 1e-6f ? Mathf.Clamp01(((qx - ax) * dx + (qy - ay) * dy) / l2) : 0f;
                    float ex = ax + dx * t - qx, ey = ay + dy * t - qy, dd = ex * ex + ey * ey;
                    if (dd < best) best = dd;
                }
                Put(pxl, py, c, Mathf.Clamp01(hw + 0.5f - Mathf.Sqrt(best)));
            }
        return Adv(ch, s);
    }
}

// The converted wheel model (Resources/Wheel/wheel.bytes, written by Tools/Wheel/convert.py), in the same frame and
// vertex layout as WheelGeo.Data (plus the atlas UV): "F2WH", version, vertex and triangle counts, the screen rect and
// the measured grip (unused here: WheelGeo.GripL), then per vertex 52 bytes, then u16 / u32 indices.
public static class WheelModel
{
    public sealed class Data
    {
        public Vector3[] v, n; public Color[] c; public Vector2[] uv0, uv2, uv3; public Vector4[] uv1; public int[] t;
    }
    public static Data Parse(byte[] b, float scale)
    {
        var r = new System.IO.BinaryReader(new System.IO.MemoryStream(b));
        if (r.ReadByte() != 'F' || r.ReadByte() != '2' || r.ReadByte() != 'W' || r.ReadByte() != 'H') return null;
        if (r.ReadUInt32() != 1) return null;
        int nv = (int)r.ReadUInt32(), nt = (int)r.ReadUInt32();
        if (nv <= 0 || nt <= 0 || b.Length < 44 + nv * 52 + nt * 3 * (nv < 65536 ? 2 : 4)) return null;
        for (int i = 0; i < 7; i++) r.ReadSingle();
        var d = new Data { v = new Vector3[nv], n = new Vector3[nv], c = new Color[nv], uv0 = new Vector2[nv], uv1 = new Vector4[nv], uv2 = new Vector2[nv], uv3 = new Vector2[nv], t = new int[nt * 3] };
        for (int i = 0; i < nv; i++)
        {
            d.v[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) * scale;
            d.n[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float cr = r.ReadByte() / 255f, cg = r.ReadByte() / 255f, cb = r.ReadByte() / 255f, ao = r.ReadByte() / 255f;
            d.c[i] = new Color(Mathf.Pow(cr, 2.2f), Mathf.Pow(cg, 2.2f), Mathf.Pow(cb, 2.2f), ao); // stored gamma-encoded
            var u0 = new Vector2(r.ReadSingle(), r.ReadSingle());
            float coat = r.ReadByte() / 255f, gloss = r.ReadByte() / 255f, metal = r.ReadByte() / 255f, kind = r.ReadByte();
            float pad = (sbyte)r.ReadByte(), tex = r.ReadByte(); r.ReadByte(); r.ReadByte();
            d.uv0[i] = pad != 0f ? u0 * scale : u0; // a paddle's hinge is a position
            d.uv1[i] = new Vector4(coat, gloss, metal, kind); d.uv2[i] = new Vector2(pad, tex);
            d.uv3[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        }
        bool small = nv < 65536;
        for (int i = 0; i < nt * 3; i++) { d.t[i] = small ? r.ReadUInt16() : (int)r.ReadUInt32(); if ((uint)d.t[i] >= (uint)nv) return null; }
        return d;
    }
}

#if !F2_DRIVER_HARNESS
// The component on the player's steering wheel (CarModel.CreateFromAsset puts it on the "steer_" part, whose transform
// SetSteer turns): owns the material and the display texture; Feed (from DriverFeel.Cockpit, every frame) drives the
// shift LEDs and the paddles every frame and redraws the display ~15 times a second while it is on screen.
public class SteeringWheel : MonoBehaviour
{
    static Mesh shared; static Shader shader; static bool tried; static Texture2D atlas;
    static readonly int LedId = Shader.PropertyToID("_Led"), PaddleId = Shader.PropertyToID("_Paddle"), DashId = Shader.PropertyToID("_Dash"), AlbedoId = Shader.PropertyToID("_Albedo");
    // shift lights: as ShiftLights (DriverFeel.cs): shares of the rev limit (the gearbox shifts at 97.5 %)
    const float From = 0.85f, Full = 0.965f, FlashAt = 0.972f, FlashHz = 12f;
    const float Hz = 15f, FlickTime = 0.16f, FlickRad = 4f * Mathf.Deg2Rad;

    public Mesh SharedMesh => shared;
    public Material Mat { get; private set; }
    Texture2D tex; WheelDash dash; MeshRenderer rend;
    int ledState = -1, lastGear = int.MinValue, unitsCheck; bool mph; long shownKey = -1;
    float nextDraw, flickL = -10f, flickR = -10f, shownL, shownR;

    // the live delta to the best lap, from the HUD (RaceHud.cs) while it shows one
    static double liveDelta = double.NaN; static float liveDeltaAt = -10f;
    public static void SetDelta(double d) { liveDelta = d; liveDeltaAt = Time.unscaledTime; }

    // go: the steering-wheel part (its transform is the wheel's frame); null when the shader is missing / unsupported
    // (the caller keeps the model's own wheel)
    public static SteeringWheel Attach(GameObject go)
    {
        if (!tried) { tried = true; shader = Resources.Load<Shader>("SteeringWheel"); if (shader != null && !shader.isSupported) shader = null; }
        if (shader == null || go == null) return null;
        if (shared == null)
        {
            shared = LoadModel(1f / DriverGeo.K); // the real wheel model; the built one only if it is missing
            if (shared == null)
            {
                var d = WheelGeo.Build(1f / DriverGeo.K); // metres -> the car body's units (the root is scaled by K)
                shared = new Mesh { name = "SteeringWheel" };
                shared.SetVertices(d.v); shared.SetNormals(d.n); shared.SetColors(d.c);
                shared.SetUVs(0, d.uv0); shared.SetUVs(1, d.uv1); shared.SetUVs(2, d.uv2);
                shared.SetTriangles(d.t, 0); shared.RecalculateBounds();
            }
            // the paddles move a little in the shader: room for them in the bounds
            var b = shared.bounds; b.Expand(0.02f); shared.bounds = b;
        }
        var sw = go.AddComponent<SteeringWheel>();
        sw.dash = new WheelDash();
        sw.tex = new Texture2D(WheelDash.W, WheelDash.H, TextureFormat.RGBA32, false) { name = "WheelDash", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 2 };
        sw.Mat = new Material(shader) { name = "SteeringWheel" };
        sw.Mat.SetTexture(DashId, sw.tex);
        if (atlas != null) sw.Mat.SetTexture(AlbedoId, atlas);
        sw.Mat.SetVector(LedId, Vector4.zero); sw.Mat.SetVector(PaddleId, Vector4.zero);
        sw.mph = PlayerPrefs.GetInt("units", 0) == 1;
        sw.dash.Draw(1, false, 0, sw.mph, double.NaN, 1f, 0, 0, 0f, false);
        sw.tex.SetPixels32(sw.dash.px); sw.tex.Apply(false, false);
        return sw;
    }
    // Resources/Wheel: the model's mesh and its albedo atlas (PNG / JPEG bytes, ImageConversion like EnvTex.LoadPhoto);
    // null if either is missing or broken (silently: the built wheel stands in)
    static Mesh LoadModel(float scale)
    {
        try
        {
            var mb = Resources.Load<TextAsset>("Wheel/wheel"); var ab = Resources.Load<TextAsset>("Wheel/atlas");
            if (mb == null || ab == null) return null;
            var d = WheelModel.Parse(mb.bytes, scale);
            Resources.UnloadAsset(mb);
            if (d == null) { Resources.UnloadAsset(ab); return null; }
            var tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            bool ok = ImageConversion.LoadImage(tmp, ab.bytes, false);
            Resources.UnloadAsset(ab);
            if (!ok) { Object.Destroy(tmp); return null; }
            var px = tmp.GetPixels32(); int w = tmp.width, h = tmp.height; Object.Destroy(tmp);
            atlas = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { name = "WheelAtlas", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            atlas.SetPixels32(px); atlas.Apply(true, false); atlas.Compress(true); atlas.Apply(false, true);
            var m = new Mesh { name = "SteeringWheelModel" };
            if (d.v.Length >= 65536) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(d.v); m.SetNormals(d.n); m.SetColors(d.c);
            m.SetUVs(0, d.uv0); m.SetUVs(1, d.uv1); m.SetUVs(2, d.uv2); m.SetUVs(3, d.uv3);
            m.SetTriangles(d.t, 0); m.RecalculateBounds();
            return m;
        }
        catch (System.Exception) { atlas = null; return null; }
    }

    // CarModel adds the MeshRenderer after Attach: no shadow (the cockpit is in the car's own shade anyway)
    public void Bind(MeshRenderer r)
    {
        rend = r; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
    }

    // every frame (DriverFeel.Cockpit): shift LEDs, paddle flicks on gear changes, the display at ~15 Hz
    public void Feed(FormulaSim.FormulaCar car, float time)
    {
        if (car == null || Mat == null) return;
        float f = car.cfg.redline > 0 ? (float)(car.rpm / car.cfg.redline) : 0f;
        if (float.IsNaN(f)) f = 0f;
        bool flash = f >= FlashAt;
        int state = flash ? (((int)(time * FlashHz * 2f) & 1) == 0 ? 16 : 17) : Mathf.Clamp(Mathf.RoundToInt((f - From) / (Full - From) * 15f), 0, 15);
        if (state != ledState)
        {
            ledState = state;
            Mat.SetVector(LedId, state == 16 ? new Vector4(15f, 1f, 0f, 0f) : state == 17 ? new Vector4(0f, 2f, 0f, 0f) : new Vector4(state, 0f, 0f, 0f));
        }
        int g = car.reverse ? -1 : car.gear;
        if (g != lastGear)
        {
            if (lastGear != int.MinValue) { if (g > lastGear) flickR = time; else flickL = time; }
            lastGear = g;
        }
        float aL = Flick(time - flickL), aR = Flick(time - flickR);
        if (aL != shownL || aR != shownR) { shownL = aL; shownR = aR; Mat.SetVector(PaddleId, new Vector4(aL, aR, 0f, 0f)); }

        if (time < nextDraw && time >= nextDraw - 1f) return;
        if (rend != null && !rend.isVisible) return;
        nextDraw = time + 1f / Hz;
        if (++unitsCheck >= 30) { unitsCheck = 0; bool m = PlayerPrefs.GetInt("units", 0) == 1; if (m != mph) { mph = m; shownKey = -1; } }
        float speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy) * (mph ? 2.23694f : 3.6f);
        float soc = double.IsNaN(car.ersEnergy) || car.cfg.ersCapacity <= 0 ? 1f : Mathf.Clamp01((float)(car.ersEnergy / car.cfg.ersCapacity));
        double kw = double.IsNaN(car.ersKw) ? 0 : car.ersKw;
        int ers = kw > 5 ? 1 : kw < -5 ? (car.throttle > 0.5 ? 3 : 2) : 0;
        double delta = Time.unscaledTime - liveDeltaAt < 0.75f ? liveDelta : double.NaN;
        int spd = Mathf.RoundToInt(float.IsNaN(speed) ? 0f : speed), kwi = Mathf.RoundToInt((float)System.Math.Abs(kw));
        int dcs = double.IsNaN(delta) ? int.MinValue : (int)System.Math.Round(System.Math.Max(-9.99, System.Math.Min(9.99, delta)) * 100.0);
        long key = ((((((long)(car.reverse ? 9 : car.gear & 15) * 1000 + spd) * 20001 + (dcs == int.MinValue ? 20000 : dcs + 10000)) * 101
                   + Mathf.RoundToInt(soc * 100f)) * 4 + ers) * 1000 + kwi) * 41 + Mathf.RoundToInt(Mathf.Clamp01(f) * 35f) + (flash && state == 16 ? 1L << 62 : 0L);
        if (key == shownKey) return; // nothing on the display changed
        shownKey = key;
        dash.Draw(car.gear, car.reverse, spd, mph, delta, soc, ers, kwi, f, flash && state == 16);
        tex.SetPixels32(dash.px); tex.Apply(false, false);
    }
    // the paddle pulled towards the driver and let go: up in ~50 ms, back in ~110 ms
    static float Flick(float t)
    {
        if (t < 0f || t > FlickTime) return 0f;
        float k = t < 0.05f ? t / 0.05f : 1f - (t - 0.05f) / (FlickTime - 0.05f);
        return FlickRad * Mathf.SmoothStep(0f, 1f, k);
    }

    void OnDestroy()
    {
        if (tex != null) Destroy(tex);
        if (Mat != null) Destroy(Mat);
    }
}
#endif
