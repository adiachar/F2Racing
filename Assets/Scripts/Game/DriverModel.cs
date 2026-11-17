// DriverModel.cs — the driver in every car, built from code (no assets, no textures):
//  * helmet (glossy shell in two design colours, dark visor), balaclava neck, carbon HANS collar, racing suit
//    (team colour, a trim stripe along the sleeves), gloves with the hands closed round the steering wheel's side grips
//    at "9:15". Smooth low-poly: ~2.0k triangles for the player, ~0.7k for a near rival, a 64-triangle helmet far away.
//  * one mesh, one material: CarPaint.shader's _MERGED path, the part ID per vertex (colour A: suit, trim, helmet A/B,
//    visor, dark fabric, carbon) and the baked AO (colour R), so every material of the driver is one draw call.
//  * player: a SkinnedMeshRenderer with 8 bones (torso, head, upper arm / forearm / hand each side). Every frame the
//    hands are put on the grips of the steering wheel as it turns and each arm follows by a 2-bone IK (shoulder ->
//    elbow -> wrist, the elbow bent outward and down); the head takes the nod / tilt / look from DriverFeel.Head.
//    The cockpit view hides the head (head bone scaled to zero: no clipping, no extra renderer); the gloves and
//    forearms stay on the wheel in front of the camera.
//  * rivals: the same mesh at less detail, posed once with the hands on the (fixed) wheel and merged into the car's
//    body mesh (CarModel.BuildRivalSet): no extra draw call, no per-frame cost. The far car keeps only the helmet.
// Size: real metres in the "driver frame": the car body's axes (x right, y up, z forward) times RootScale (the body is
// built at F2 size and scaled up at the car's root). Fitted to the cockpit camera's eye point, GameManager's body
// (0, 0.76, 0.30) = (0, 0.824, 0.325) here: 0.82 m above the floor plane, with the helmet round it (the camera is
// not moved). The geometry class uses only maths types, so CarModel can bake it and it can be built off-line.
using System.Collections.Generic;
using UnityEngine;

public static class DriverGeo
{
    public const float K = 3.40f / 3.135f; // = CarModel.RootScale
    // part IDs (CarPaint _MERGED, after CarModel.Id* 0..11)
    public const byte IdCarbon = 2, IdSuit = 12, IdTrim = 13, IdHelmA = 14, IdHelmB = 15, IdVisor = 16, IdDark = 17;
    public const int Torso = 0, Head = 1, UpperL = 2, ForeL = 3, HandL = 4, UpperR = 5, ForeR = 6, HandR = 7, Bones = 8;

    // ---- the seated driver (driver frame, metres)
    public static readonly Vector3 Eye = new Vector3(0f, 0.824f, 0.325f);           // the cockpit camera (for reference)
    public static readonly Vector3 HeadPivot = new Vector3(0f, 0.745f, 0.215f);     // top of the neck: the head turns here
    public static readonly Vector3 HelmetCentre = new Vector3(0f, 0.835f, 0.262f);  // eyes 6 cm ahead of it, 1 cm below
    const float HelmX = 0.122f, HelmUp = 0.142f, HelmDown = 0.13f, HelmFront = 0.142f, HelmBack = 0.168f; // 31 x 24 x 27 cm
    static readonly Vector3 ShoulderL = new Vector3(-0.18f, 0.605f, 0.245f);       // shoulder joint (reclined seat, shoulders rolled forward)
    const float UpperLen = 0.29f, ForeLen = 0.265f;                                 // shoulder-elbow, elbow-wrist
    static readonly Vector3 PoleL = new Vector3(-0.55f, -0.83f, -0.1f);            // the elbow bends out and down
    // the steering wheel when the car mesh gives none: pivot and tilt of car.bytes' "steer_-16.34" part (body frame)
    public static readonly Vector3 WheelPivotBody = new Vector3(0f, 0.6039f, 0.7995f);
    public const float WheelPitch = -16.34f;
    // the left hand on the left grip, in the wheel's own frame (x right, y up, the driver at -z) in metres: the grip
    // sits in the closed fist, palm on its outer side, fingers round its back, the wrist behind and outside it. The
    // right hand is the mirror image. The grip is the player's wheel model's (WheelGeo.GripL, measured on its rubber
    // grip by Tools/Wheel/convert.py: 125 mm out from the centre; the car model's own wheel had it at 117 mm, too little
    // to tell on a rival), so the wrist follows from it.
    static readonly Vector3 HandFwdL = new Vector3(0.1f, 0f, 1f);
    static readonly Vector3 GripInHand = new Vector3(0.029f, 0.015f, 0.093f);        // grip axis in the hand's frame (x palm side)
    static readonly Vector3 WristL = WristFor(WheelGeo.GripL);                        // (-0.163, -0.021, -0.123)
    // the wrist that puts GripInHand on the grip point g with the wheel at rest (the hand's frame: forward HandFwdL, up y)
    static Vector3 WristFor(Vector3 g)
    {
        Vector3 z = HandFwdL.normalized, x = Vector3.Cross(Vector3.up, z).normalized;
        return g - (x * GripInHand.x + Vector3.up * GripInHand.y + z * GripInHand.z);
    }

    public sealed class Pose
    {
        public readonly Vector3[] p = new Vector3[Bones];
        public readonly Quaternion[] r = new Quaternion[Bones];
    }

    // the steering wheel's pivot and rotation in the driver frame at its rest angle (from the mesh's pivot and tilt, body frame)
    public static void RestWheel(Vector3 pivotBody, float pitch, out Vector3 pos, out Quaternion rot)
    {
        pos = pivotBody * K; rot = Quaternion.Euler(pitch, 0f, 0f);
    }

    // the whole pose: torso still, head turned by (pitch: nod down +, yaw: look right +, roll: tilt left +) degrees,
    // both hands on the wheel (pivot and rotation in the driver frame) with the arms solved to reach them
    public static void Solve(Pose pose, Vector3 wheelPos, Quaternion wheelRot, float pitch, float yaw, float roll)
    {
        pose.p[Torso] = Vector3.zero; pose.r[Torso] = Quaternion.identity;
        pose.p[Head] = HeadPivot; pose.r[Head] = Quaternion.Euler(pitch, yaw, roll);
        Arm(pose, 0, wheelPos, wheelRot);
        Arm(pose, 1, wheelPos, wheelRot);
    }

    static Vector3 Mir(Vector3 v, int side) => side == 0 ? v : new Vector3(-v.x, v.y, v.z);

    // 2-bone IK: shoulder fixed, wrist on the grip; the elbow on the circle of solutions, towards the pole (out and down)
    static void Arm(Pose pose, int side, Vector3 wp, Quaternion wr)
    {
        int bu = side == 0 ? UpperL : UpperR, bf = bu + 1, bh = bu + 2;
        Quaternion hand = wr * Quaternion.LookRotation(Mir(HandFwdL, side), Vector3.up);
        Vector3 s = Mir(ShoulderL, side), w = wp + wr * Mir(WristL, side);
        Vector3 d = w - s; float dist = d.magnitude;
        Vector3 ax = dist > 1e-4f ? d / dist : Vector3.forward;
        dist = Mathf.Clamp(dist, 0.12f, (UpperLen + ForeLen) * 0.999f); // out of reach: the wrist stops short of the grip
        w = s + ax * dist;
        float x = (UpperLen * UpperLen - ForeLen * ForeLen + dist * dist) / (2f * dist);
        float h = Mathf.Sqrt(Mathf.Max(0f, UpperLen * UpperLen - x * x));
        Vector3 pole = Mir(PoleL, side), bend = pole - ax * Vector3.Dot(pole, ax);
        bend = bend.sqrMagnitude > 1e-8f ? bend.normalized : Vector3.down;
        Vector3 e = s + ax * x + bend * h;
        // forearm roll halfway between the upper arm's and the hand's (the forearm twists along its length, so the
        // wrist never shows the whole turn of the wheel at once)
        Vector3 hu = hand * Vector3.up, fu = Vector3.up + hu;
        if (fu.sqrMagnitude < 0.01f) fu = hand * (side == 0 ? Vector3.left : Vector3.right);
        pose.p[bu] = s; pose.r[bu] = Quaternion.LookRotation(e - s, Vector3.up);
        pose.p[bf] = e; pose.r[bf] = Quaternion.LookRotation(w - e, fu.normalized);
        pose.p[bh] = w; pose.r[bh] = hand;
    }

    // ------------------------------------------------------------------ mesh
    public sealed class Data
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Color32> c = new List<Color32>();
        public readonly List<int> t = new List<int>();
        public readonly List<int> b0 = new List<int>(), b1 = new List<int>();
        public readonly List<float> w1 = new List<float>(); // weight of b1 (b0 gets the rest)
    }

    // detail: 2 player, 1 near rival, 0 far rival (helmet only). Built in the given (bind) pose, in the driver frame.
    public static Data Build(int detail, Pose bind)
    {
        var d = new Data();
        Helmet(d, detail, bind);
        if (detail == 0) return d;
        bool hi = detail >= 2;
        TorsoMesh(d, hi ? 12 : 8);
        Neck(d, hi ? 8 : 6);
        Hans(d, hi ? 8 : 6, hi);
        for (int s = 0; s < 2; s++) { ArmMesh(d, s, hi, bind); HandMesh(d, s, hi, bind); }
        return d;
    }

    // baked light: deep in the cockpit is darker, undersides a little darker
    static byte Ao(Vector3 p, Vector3 n)
    {
        float a = Mathf.Lerp(0.42f, 1f, Mathf.Clamp01((p.y - 0.36f) / 0.3f)) * (0.8f + 0.2f * Mathf.Clamp01(n.y + 0.6f));
        return (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(a));
    }

    // A grid of rows x cols points (row-major; wrap: the last column joins the first), one part ID per cell. Vertices are
    // shared only between cells of the same ID, so the colour borders are crisp while the normals stay smooth. Each
    // row has its own bone weights. Triangles face the way the given normals point.
    static void Sheet(Data d, Vector3[] P, Vector3[] N, int rows, int cols, bool wrap, System.Func<int, int, byte> id,
                      int[] rb0, int[] rb1, float[] rw1)
    {
        var map = new Dictionary<int, int>();
        int Vtx(int r, int c, byte k)
        {
            int key = (r * cols + c) * 32 + k;
            if (map.TryGetValue(key, out int i)) return i;
            i = d.v.Count; var p = P[r * cols + c]; var n = N[r * cols + c];
            d.v.Add(p); d.n.Add(n); d.c.Add(new Color32(Ao(p, n), 0, 0, k));
            d.b0.Add(rb0[r]); d.b1.Add(rb1[r]); d.w1.Add(rw1[r]);
            map[key] = i; return i;
        }
        int cells = wrap ? cols : cols - 1;
        for (int r = 0; r < rows - 1; r++)
            for (int c = 0; c < cells; c++)
            {
                byte k = id(r, c); int c1 = (c + 1) % cols;
                int i00 = r * cols + c, i10 = (r + 1) * cols + c, i11 = (r + 1) * cols + c1, i01 = r * cols + c1;
                Tri(d, P, N, i00, i10, i11, () => Vtx(r, c, k), () => Vtx(r + 1, c, k), () => Vtx(r + 1, c1, k));
                Tri(d, P, N, i00, i11, i01, () => Vtx(r, c, k), () => Vtx(r + 1, c1, k), () => Vtx(r, c1, k));
            }
    }
    static void Tri(Data d, Vector3[] P, Vector3[] N, int a, int b, int c, System.Func<int> va, System.Func<int> vb, System.Func<int> vc)
    {
        var cr = Vector3.Cross(P[b] - P[a], P[c] - P[a]);
        if (cr.sqrMagnitude < 1e-12f) return; // degenerate (the helmet's poles)
        int ia = va(), ib = vb(), ic = vc();
        if (Vector3.Dot(cr, N[a] + N[b] + N[c]) >= 0f) { d.t.Add(ia); d.t.Add(ib); d.t.Add(ic); }
        else { d.t.Add(ia); d.t.Add(ic); d.t.Add(ib); }
    }
    static int[] Fill(int n, int v) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }
    static float[] FillF(int n, float v) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

    // A tube along a path (local points, mapped by pos + rot * p): elliptical rings (half sizes rx across, ry along
    // 'up' = the hint made square to the path), ring centres raised by lift along that up. Caps close the ends.
    static void Tube(Data d, Vector3[] path, float[] rx, float[] ry, float[] lift, Vector3 hint, int cols,
                     Vector3 pos, Quaternion rot, System.Func<int, int, byte> id, int[] rb0, int[] rb1, float[] rw1,
                     bool capStart, bool capEnd)
    {
        int rows = path.Length; var P = new Vector3[rows * cols]; var N = new Vector3[rows * cols];
        var T = new Vector3[rows]; var X = new Vector3[rows]; var U = new Vector3[rows];
        for (int r = 0; r < rows; r++)
        {
            var t = (path[Mathf.Min(r + 1, rows - 1)] - path[Mathf.Max(r - 1, 0)]).normalized;
            var x = Vector3.Cross(hint, t); x = x.sqrMagnitude > 1e-8f ? x.normalized : Vector3.right;
            T[r] = t; X[r] = x; U[r] = Vector3.Cross(t, x);
            for (int c = 0; c < cols; c++)
            {
                float a = c * (6.2831853f / cols), ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                var p = path[r] + U[r] * lift[r] + X[r] * (rx[r] * ca) + U[r] * (ry[r] * sa);
                var n = (X[r] * (ca / rx[r]) + U[r] * (sa / ry[r])).normalized;
                P[r * cols + c] = pos + rot * p; N[r * cols + c] = rot * n;
            }
        }
        Sheet(d, P, N, rows, cols, true, id, rb0, rb1, rw1);
        if (capStart) Cap(d, P, 0, cols, pos + rot * (path[0] + U[0] * lift[0]), rot * -T[0], id(0, 0), rb0[0], rb1[0], rw1[0]);
        if (capEnd) Cap(d, P, rows - 1, cols, pos + rot * (path[rows - 1] + U[rows - 1] * lift[rows - 1]), rot * T[rows - 1], id(rows - 2, 0), rb0[rows - 1], rb1[rows - 1], rw1[rows - 1]);
    }
    static void Cap(Data d, Vector3[] P, int row, int cols, Vector3 centre, Vector3 n, byte k, int b0, int b1, float w1)
    {
        int c0 = d.v.Count;
        d.v.Add(centre); d.n.Add(n); d.c.Add(new Color32(Ao(centre, n), 0, 0, k)); d.b0.Add(b0); d.b1.Add(b1); d.w1.Add(w1);
        for (int c = 0; c < cols; c++)
        {
            var p = P[row * cols + c]; d.v.Add(p); d.n.Add(n); d.c.Add(new Color32(Ao(p, n), 0, 0, k)); d.b0.Add(b0); d.b1.Add(b1); d.w1.Add(w1);
        }
        for (int c = 0; c < cols; c++)
        {
            int a = c0 + 1 + c, b = c0 + 1 + (c + 1) % cols;
            var cr = Vector3.Cross(d.v[a] - centre, d.v[b] - centre);
            if (Vector3.Dot(cr, n) >= 0f) { d.t.Add(c0); d.t.Add(a); d.t.Add(b); } else { d.t.Add(c0); d.t.Add(b); d.t.Add(a); }
        }
    }

    // ---- helmet: an egg (longer behind, fuller on top) on a grid whose poles are at the temples, so the borders of the
    // design (visor opening, brow band, centre stripe over the top) run along grid lines and stay clean
    static readonly float[][] HelmRows = {
        new[] { -1f, -0.8f, -0.24f, 0.24f, 0.8f, 1f },
        new[] { -1f, -0.88f, -0.8f, -0.5f, -0.24f, 0f, 0.24f, 0.5f, 0.8f, 0.88f, 1f },
        new[] { -1f, -0.96f, -0.88f, -0.8f, -0.66f, -0.5f, -0.36f, -0.24f, -0.12f, 0f, 0.12f, 0.24f, 0.36f, 0.5f, 0.66f, 0.8f, 0.88f, 0.96f, 1f } };
    // degrees round the x axis: 0 front, 90 top, 180 back (the last column joins the first, -90 = 270 underneath)
    static readonly float[][] HelmCols = {
        new[] { -90f, -16f, 16f, 30f, 90f, 150f, 190f, 240f },
        new[] { -90f, -50f, -16f, 0f, 16f, 30f, 62f, 94f, 126f, 158f, 190f, 222f, 256f },
        new[] { -90f, -70f, -52f, -34f, -16f, 0f, 16f, 30f, 46f, 62f, 78f, 94f, 110f, 126f, 142f, 158f, 174f, 190f, 206f, 222f, 240f, 256f } };
    static void Helmet(Data d, int detail, Pose bind)
    {
        var rs = HelmRows[detail]; var cs = HelmCols[detail]; int rows = rs.Length, cols = cs.Length;
        var P = new Vector3[rows * cols]; var N = new Vector3[rows * cols];
        Vector3 hp = bind.p[Head]; Quaternion hr = bind.r[Head]; Vector3 off = HelmetCentre - HeadPivot;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                float s = rs[r], ph = cs[c] * Mathf.Deg2Rad, rho = Mathf.Sqrt(Mathf.Max(0f, 1f - s * s));
                float ux = s, uy = rho * Mathf.Sin(ph), uz = rho * Mathf.Cos(ph);
                float ry = uy > 0 ? HelmUp : HelmDown, rz = uz > 0 ? HelmFront : HelmBack;
                // a slightly squarer chin bar, and the shell drawn in a little at the lower back towards the neck
                float chin = Mathf.Clamp01(-uy * 2.5f) * Mathf.Clamp01(uz * 2f) * (1f - s * s);
                float nape = Mathf.Clamp01(-uy * 2f) * Mathf.Clamp01(-uz * 2f);
                var p = new Vector3(ux * HelmX * (1f - 0.08f * nape), uy * ry, uz * rz * (1f + 0.06f * chin));
                var n = new Vector3(p.x / (HelmX * HelmX), p.y / (ry * ry), p.z / (rz * rz)).normalized;
                P[r * cols + c] = hp + hr * (off + p); N[r * cols + c] = hr * n;
            }
        byte Id(int r, int c)
        {
            float s = Mathf.Abs(0.5f * (rs[r] + rs[r + 1]));
            float a0 = cs[c], a1 = c + 1 < cols ? cs[c + 1] : cs[0] + 360f, a = 0.5f * (a0 + a1);
            if (s < 0.8f && a > -16f && a < 16f) return IdVisor;
            if (s < 0.88f && a > 16f && a < 30f) return IdHelmB;    // brow band over the visor
            if (s < 0.24f && a > 30f && a < 190f) return IdHelmB;   // centre stripe over the top
            return IdHelmA;
        }
        Sheet(d, P, N, rows, cols, true, Id, Fill(rows, Head), Fill(rows, Head), FillF(rows, 0f));
    }

    // ---- torso: superelliptic sections along a reclined spine (pelvis low and forward, shoulders back and up)
    static void TorsoMesh(Data d, int cols)
    {
        var sp = new[] { new Vector3(0, 0.30f, 0.50f), new Vector3(0, 0.42f, 0.405f), new Vector3(0, 0.53f, 0.315f),
                         new Vector3(0, 0.60f, 0.25f), new Vector3(0, 0.642f, 0.205f), new Vector3(0, 0.668f, 0.182f) };
        var hw = new[] { 0.165f, 0.16f, 0.175f, 0.205f, 0.17f, 0.075f };
        var hd = new[] { 0.11f, 0.105f, 0.11f, 0.095f, 0.075f, 0.058f };
        int rows = sp.Length; var P = new Vector3[rows * cols]; var N = new Vector3[rows * cols];
        const float E = 2.6f;
        for (int r = 0; r < rows; r++)
        {
            var t = (sp[Mathf.Min(r + 1, rows - 1)] - sp[Mathf.Max(r - 1, 0)]).normalized;
            var X = Vector3.right; var D = Vector3.Cross(X, t).normalized; // chest side: forward and up
            if (D.z < 0) D = -D;
            for (int c = 0; c < cols; c++)
            {
                float a = c * (6.2831853f / cols), ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                float px = Mathf.Sign(ca) * Mathf.Pow(Mathf.Abs(ca), 2f / E), py = Mathf.Sign(sa) * Mathf.Pow(Mathf.Abs(sa), 2f / E);
                P[r * cols + c] = sp[r] + X * (hw[r] * px) + D * (hd[r] * py);
                float gx = Mathf.Sign(px) * Mathf.Pow(Mathf.Abs(px), E - 1f) / hw[r], gy = Mathf.Sign(py) * Mathf.Pow(Mathf.Abs(py), E - 1f) / hd[r];
                N[r * cols + c] = (X * gx + D * gy).normalized;
            }
        }
        Sheet(d, P, N, rows, cols, true, (r, c) => IdSuit, Fill(rows, Torso), Fill(rows, Torso), FillF(rows, 0f));
        Cap(d, P, rows - 1, cols, sp[rows - 1], (sp[rows - 1] - sp[rows - 2]).normalized, IdSuit, Torso, Torso, 0f);
    }

    // ---- neck (balaclava): from the collar into the helmet, bending with the head
    static void Neck(Data d, int cols)
    {
        var path = new[] { new Vector3(0, 0.655f, 0.172f), new Vector3(0, 0.72f, 0.2f), new Vector3(0, 0.785f, 0.235f) };
        var r = new[] { 0.056f, 0.054f, 0.052f };
        Tube(d, path, r, r, new float[3], Vector3.forward, cols, Vector3.zero, Quaternion.identity, (a, b) => IdDark,
             new[] { Torso, Torso, Head }, new[] { Torso, Head, Head }, new[] { 0f, 0.5f, 0f }, false, false);
    }

    // ---- HANS: the carbon yoke round the back of the neck, its arms lying forward on the chest; the collar rises behind
    static void Hans(Data d, int cols, bool hi)
    {
        var half = new[] { new Vector3(-0.105f, 0.585f, 0.31f), new Vector3(-0.115f, 0.622f, 0.258f), new Vector3(-0.108f, 0.655f, 0.2f),
                           new Vector3(-0.075f, 0.683f, 0.137f), new Vector3(0f, 0.70f, 0.105f) };
        var hh = new[] { 0.012f, 0.013f, 0.016f, 0.024f, 0.032f };
        int m = hi ? half.Length : 3; int[] pick = hi ? new[] { 0, 1, 2, 3, 4 } : new[] { 0, 2, 4 };
        int n = m * 2 - 1; var path = new Vector3[n]; var ry = new float[n]; var rx = new float[n]; var lift = new float[n];
        for (int i = 0; i < m; i++)
        {
            var p = half[pick[i]]; path[i] = p; path[n - 1 - i] = new Vector3(-p.x, p.y, p.z);
            ry[i] = ry[n - 1 - i] = hh[pick[i]]; rx[i] = rx[n - 1 - i] = 0.026f; lift[i] = lift[n - 1 - i] = hh[pick[i]] - 0.01f;
        }
        Tube(d, path, rx, ry, lift, Vector3.up, cols, Vector3.zero, Quaternion.identity, (a, b) => IdCarbon,
             Fill(n, Torso), Fill(n, Torso), FillF(n, 0f), true, true);
    }

    // ---- arms: upper arm and forearm tubes in their bones' frames (+z along the bone), the joint rings shared between
    // the two bones; the glove's gauntlet over the end of the sleeve; a trim stripe along the top of the sleeve
    static void ArmMesh(Data d, int side, bool hi, Pose bind)
    {
        int bu = side == 0 ? UpperL : UpperR, bf = bu + 1, bh = bu + 2, cols = hi ? 10 : 6;
        System.Func<int, int, byte> sleeve = (r, c) =>
        {
            if (!hi) return IdSuit;
            float a = (c + 0.5f) * 360f / cols; // angle round the bone from its x axis: 90 = up
            return Mathf.Abs(a - 90f) < 20f ? IdTrim : IdSuit;
        };
        {
            var z = hi ? new[] { -0.04f, 0f, 0.09f, 0.18f, 0.25f, UpperLen } : new[] { -0.04f, 0.12f, UpperLen };
            var rr = hi ? new[] { 0.05f, 0.056f, 0.054f, 0.049f, 0.046f, 0.044f } : new[] { 0.05f, 0.053f, 0.044f };
            var w = hi ? new[] { 0f, 0f, 0f, 0f, 0.15f, 0.5f } : new[] { 0f, 0f, 0.5f };
            var path = new Vector3[z.Length]; var ry = new float[z.Length];
            for (int i = 0; i < z.Length; i++) { path[i] = new Vector3(0, 0, z[i]); ry[i] = rr[i] * 0.92f; }
            Tube(d, path, rr, ry, new float[z.Length], Vector3.up, cols, bind.p[bu], bind.r[bu], sleeve,
                 Fill(z.Length, bu), Fill(z.Length, bf), w, false, false);
        }
        {
            // rows: elbow .. sleeve end | gauntlet lip .. wrist; the cells from the lip on are glove
            var z = hi ? new[] { 0f, 0.04f, 0.12f, 0.175f, 0.18f, 0.225f, 0.255f, ForeLen } : new[] { 0f, 0.12f, 0.175f, 0.18f, ForeLen };
            var rr = hi ? new[] { 0.044f, 0.046f, 0.043f, 0.038f, 0.045f, 0.041f, 0.035f, 0.033f } : new[] { 0.044f, 0.043f, 0.038f, 0.045f, 0.034f };
            var wU = hi ? new[] { 0.5f, 0.15f, 0f, 0f, 0f, 0f, 0f, 0f } : new[] { 0.5f, 0f, 0f, 0f, 0f };
            var wH = hi ? new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0.2f, 0.5f } : new[] { 0f, 0f, 0f, 0f, 0.5f };
            int n = z.Length, lip = hi ? 3 : 2;
            var path = new Vector3[n]; var ry = new float[n]; var b0 = new int[n]; var b1 = new int[n]; var w1 = new float[n];
            for (int i = 0; i < n; i++)
            {
                path[i] = new Vector3(0, 0, z[i]); ry[i] = rr[i] * 0.9f; b0[i] = bf;
                if (wU[i] > 0) { b1[i] = bu; w1[i] = wU[i]; } else if (wH[i] > 0) { b1[i] = bh; w1[i] = wH[i]; } else { b1[i] = bf; w1[i] = 0f; }
            }
            Tube(d, path, rr, ry, new float[n], Vector3.up, cols, bind.p[bf], bind.r[bf], (r, c) => r >= lip ? IdTrim : sleeve(r, c),
                 b0, b1, w1, false, true);
        }
    }

    // ---- hand (glove): back of the hand from the wrist to the knuckles, the four fingers as one curled band round the
    // grip, the thumb closing the fist behind it. Built for the left hand (x = palm side) and mirrored for the right.
    static void HandMesh(Data d, int side, bool hi, Pose bind)
    {
        int bh = side == 0 ? HandL : HandR, cols = hi ? 8 : 6;
        Vector3 hp = bind.p[bh]; Quaternion hr = bind.r[bh];
        float mx = side == 0 ? 1f : -1f;
        Vector3 M(Vector3 v) => new Vector3(v.x * mx, v.y, v.z);
        System.Func<int, int, byte> glove = (r, c) => IdTrim;
        // back of the hand / palm
        {
            var path = hi ? new[] { new Vector3(0, 0, -0.02f), new Vector3(0.001f, 0, 0.02f), new Vector3(0.003f, 0, 0.055f), new Vector3(0.005f, 0, 0.08f) }
                          : new[] { new Vector3(0, 0, -0.02f), new Vector3(0.003f, 0, 0.05f), new Vector3(0.005f, 0, 0.08f) };
            var rx = hi ? new[] { 0.02f, 0.02f, 0.018f, 0.015f } : new[] { 0.02f, 0.018f, 0.015f };
            var ry = hi ? new[] { 0.03f, 0.04f, 0.044f, 0.044f } : new[] { 0.03f, 0.044f, 0.044f };
            for (int i = 0; i < path.Length; i++) path[i] = M(path[i]);
            int n = path.Length;
            Tube(d, path, rx, ry, new float[n], Vector3.up, cols, hp, hr, glove, Fill(n, bh), Fill(n, bh), FillF(n, 0f), false, false);
        }
        // fingers: an arc round the grip axis, from the knuckles round its front to its inner side
        {
            int n = hi ? 9 : 5; var path = new Vector3[n]; var rx = new float[n]; var ry = new float[n];
            for (int i = 0; i < n; i++)
            {
                float b = Mathf.Lerp(205f, -50f, i / (float)(n - 1)) * Mathf.Deg2Rad;
                path[i] = M(GripInHand + new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b)) * 0.027f - new Vector3(0, 0.015f, 0));
                rx[i] = Mathf.Lerp(0.012f, 0.009f, i / (float)(n - 1)); ry[i] = Mathf.Lerp(0.044f, 0.04f, i / (float)(n - 1));
            }
            Tube(d, path, rx, ry, new float[n], Vector3.up, cols, hp, hr, glove, Fill(n, bh), Fill(n, bh), FillF(n, 0f), false, true);
        }
        if (!hi) return;
        // thumb: from the base of the palm round the back of the grip to meet the fingertips
        {
            var path = new[] { M(new Vector3(0.012f, 0.03f, 0.022f)), M(new Vector3(0.03f, 0.04f, 0.05f)), M(new Vector3(0.048f, 0.042f, 0.066f)), M(new Vector3(0.06f, 0.04f, 0.08f)) };
            var r = new[] { 0.014f, 0.013f, 0.011f, 0.01f };
            Tube(d, path, r, r, new float[4], Vector3.forward, 6, hp, hr, glove, Fill(4, bh), Fill(4, bh), FillF(4, 0f), false, true);
        }
    }
}

#if !F2_DRIVER_HARNESS
// The player's driver: skinned mesh, posed every frame (CarModel.PoseDriver from GameManager, after the steering wheel
// is turned; by itself late in the frame when nothing else posed it, e.g. in menus).
[DefaultExecutionOrder(100)]
public class DriverModel : MonoBehaviour
{
    static Mesh playerMesh; static Matrix4x4[] playerBind;
    Transform wheel, helmetStandIn; readonly Transform[] bones = new Transform[DriverGeo.Bones];
    Vector3 restWheelPos; Quaternion restWheelRot;
    readonly DriverGeo.Pose pose = new DriverGeo.Pose();
    Material mat; int posedFrame = -1; float hPitch, hYaw, hRoll; bool headHidden;

    // body: the car's body transform (driver frame = body frame x RootScale); wheel: the steering wheel (null: none
    // turns, the hands stay at the rest grip). Returns null when the paint shader is missing (the caller keeps a helmet).
    public static DriverModel Attach(Transform body, Transform wheel, Vector3 wheelPivotBody, float wheelPitch, Shader paint, out Transform helmetStandIn)
    {
        helmetStandIn = null;
        if (paint == null) return null;
        var go = new GameObject("Driver"); go.transform.SetParent(body, false);
        go.transform.localScale = Vector3.one / DriverGeo.K; // real size inside the scaled-up car
        var dm = go.AddComponent<DriverModel>();
        dm.wheel = wheel;
        DriverGeo.RestWheel(wheelPivotBody, wheelPitch, out dm.restWheelPos, out dm.restWheelRot);
        var bind = new DriverGeo.Pose();
        DriverGeo.Solve(bind, dm.restWheelPos, dm.restWheelRot, 0, 0, 0);
        if (playerMesh == null)
        {
            var d = DriverGeo.Build(2, bind);
            playerMesh = new Mesh { name = "Driver" };
            playerMesh.SetVertices(d.v); playerMesh.SetNormals(d.n); playerMesh.SetColors(d.c); playerMesh.SetTriangles(d.t, 0);
            var bw = new BoneWeight[d.v.Count];
            for (int i = 0; i < bw.Length; i++)
                bw[i] = new BoneWeight { boneIndex0 = d.b0[i], weight0 = 1f - d.w1[i], boneIndex1 = d.b1[i], weight1 = d.w1[i] };
            playerMesh.boneWeights = bw;
            playerBind = new Matrix4x4[DriverGeo.Bones];
            for (int b = 0; b < DriverGeo.Bones; b++) playerBind[b] = Matrix4x4.TRS(bind.p[b], bind.r[b], Vector3.one).inverse;
            playerMesh.bindposes = playerBind;
            playerMesh.RecalculateBounds();
        }
        string[] names = { "Torso", "Head", "UpperArmL", "ForearmL", "HandL", "UpperArmR", "ForearmR", "HandR" };
        for (int b = 0; b < DriverGeo.Bones; b++)
        {
            var t = new GameObject(names[b]).transform; t.SetParent(go.transform, false);
            t.localPosition = bind.p[b]; t.localRotation = bind.r[b]; dm.bones[b] = t;
        }
        // GameManager hides "the helmet" in the cockpit view: this stand-in carries that switch to the head bone
        helmetStandIn = new GameObject("Helmet").transform; helmetStandIn.SetParent(dm.bones[DriverGeo.Head], false);
        helmetStandIn.localPosition = DriverGeo.HelmetCentre - DriverGeo.HeadPivot;
        dm.helmetStandIn = helmetStandIn;
        // one material for every part: CarPaint's merged path picks each part's look by the ID in the vertex colour
        dm.mat = new Material(paint) { name = "Driver", enableInstancing = false };
        dm.mat.EnableKeyword("_MERGED");
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = playerMesh; smr.bones = dm.bones; smr.rootBone = dm.bones[DriverGeo.Torso];
        smr.quality = SkinQuality.Bone2; smr.updateWhenOffscreen = false;
        smr.localBounds = new Bounds(new Vector3(0f, 0.68f, 0.45f), new Vector3(0.8f, 0.8f, 1.1f));
        smr.sharedMaterial = dm.mat;
        dm.SetColours(new Color(0.78f, 0.05f, 0.07f), new Color(0.93f, 0.93f, 0.94f), new Color(1f, 0.82f, 0.18f), new Color(0.78f, 0.05f, 0.07f));
        return dm;
    }

    // sRGB colours: suit (livery primary), trim and gloves (secondary), helmet shell and helmet design. Sent as vectors
    // in linear (the merged path reads them as is), as CarModel does for the rivals.
    public void SetColours(Color suit, Color trim, Color helmA, Color helmB)
    {
        if (mat == null) return;
        mat.SetVector("_Color", Lin(suit)); mat.SetVector("_LivB", Lin(trim)); mat.SetVector("_LivC", Vector4.zero);
        mat.SetVector("_HelmA", Lin(helmA)); mat.SetVector("_HelmB", Lin(helmB));
        mat.SetVector("_Axle", Vector4.zero); mat.SetVector("_TyreWD", Vector4.zero);
        mat.SetVector("_GlowF", Vector4.zero); mat.SetVector("_GlowR", Vector4.zero); mat.SetVector("_LightCol", Vector4.zero);
    }
    static Vector4 Lin(Color c) { c = c.linear; return new Vector4(c.r, c.g, c.b, 1f); }

    // head angles in degrees (DriverFeel.headPitch / headYaw / headRoll); the hands follow the steering wheel as it is now
    public void Pose(float pitch, float yaw, float roll)
    {
        hPitch = pitch; hYaw = yaw; hRoll = roll; posedFrame = Time.frameCount;
        Vector3 wp = restWheelPos; Quaternion wr = restWheelRot;
        if (wheel != null)
        {
            var root = transform;
            wp = root.InverseTransformPoint(wheel.position);
            wr = Quaternion.Inverse(root.rotation) * wheel.rotation;
        }
        DriverGeo.Solve(pose, wp, wr, pitch, yaw, roll);
        for (int b = 1; b < DriverGeo.Bones; b++) bones[b].SetLocalPositionAndRotation(pose.p[b], pose.r[b]);
        // the cockpit view (GameManager switches the helmet off): the head shrinks to its pivot behind the eye point
        bool hide = helmetStandIn != null && !helmetStandIn.gameObject.activeSelf;
        if (hide != headHidden) { headHidden = hide; bones[DriverGeo.Head].localScale = hide ? Vector3.zero : Vector3.one; }
    }

    void LateUpdate() { if (posedFrame != Time.frameCount) Pose(hPitch, hYaw, hRoll); }
    void OnDestroy() { if (mat != null) Destroy(mat); }
}
#endif
