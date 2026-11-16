// PitCrew.cs — an animated pit stop with a real-looking F1 pit crew. One crew (18 crew members, two engineers in headsets
// and two mechanics watching from the garage door; their tools and four tyre pairs) is built once per circuit and re-used for
// every stop; it is only drawn (and only animated) while a stop is on and within ~150 m of the camera.
//
//  * People: procedural low-poly humanoids (~550 triangles): tapered smooth-shaded limbs and torso, pelvis, neck, hands,
//    shoes with soles. Crew wear helmets (team colour lower shell, secondary top, dark visor, chin bar) and overalls in the
//    team colours (primary body, secondary yoke and upper arms, dark flank panels, darker lower legs, knee pads, gloves);
//    engineers wear team shirts, dark trousers and headsets with a boom mic; mechanics caps or bare heads. Sizes, skin,
//    hair and a slight colour tint vary per person. Colours come from the team's Livery (never all red).
//  * Animation: per-part transforms (13 rigid parts per person, no skeleton): two-bone IK for the legs (feet planted on
//    the ground, kneeling with the back toe tucked) and the arms (hands on the gun, the tyre, the jack handle, the car;
//    the elbow bends the way the pose's own arm would). Choreography = keys per role with Hermite paths (no stop-start at
//    pass-through keys), eased poses with anticipation (weight back before the dash) and overshoot-and-settle (dropping
//    into the kneel), staggered start/finish per person, a jog cycle with bob, stride, arm swing and torso counter-twist
//    from the distance covered (back-pedal when stepping back), head look-at (the incoming car, the wheel nut, the jack, the
//    departing car), idle breathing, weight shifts and glances, gun buzz at nut off / on, and a celebration (arm raised,
//    fist pumps) after the release. Everything is a function of the stop clock (frozen shots still show a pose).
//  * Stop (~2.4 s from the car stopping to the release, PitLane.Service): front jack in under the nose as the car stops,
//    rear jack in from the side, car up 8 cm (0.15-0.45 s), guns on, old wheels off (0.72 s) and carried away, new wheels
//    on (1.06 s, compound band), guns tightened, gunners stand and signal, jacks down (1.7-1.95 s), release light green
//    (2.12 s), car away (2.4 s), jacks swung clear, crew celebrate. The crew works ON THE REAL CAR (the player's model or
//    the rival's model in view): its clock is the run's (PitRun.Clock), the car is lifted (LateUpdate), its wheels are
//    hidden while off, and the compound band changes when the new wheels go on. A longer stop (wing change) holds at Hold.
//  * Whole crew = 2 draw calls (people, props: tyres, wheel guns with hoses to the gantry reels, quick-lift jacks, the
//    release light on the gantry, release handset, extinguisher); each batch is one mesh whose vertices are written in
//    world space every frame (a part is a range of vertices; hidden = collapsed). The garages are PitGarage.cs.
//  * GameManager glue (partial class at the bottom): PitCrewSetup (after SetupRivals: the lane for the rivals), PitCrewTick (the
//    crew's own Update calls it: crew waiting while BOX is armed or a car is coming in, the stop for the player and for rivals
//    within ~150 m of the camera), the player's pit-lane drive (PlayerPitEntry / PlayerPitStep), ShotPit (shot mode, pit=1).
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public class PitCrew : MonoBehaviour
{
    // ---------------------------------------------------------------- timeline (s; 0 = the car has stopped)
    const float A = -1.3f;                 // the crew is shown 1.3 s before the car stops
    const float Hold = 1.55f;              // wheels on, guns off, jacks still up: the crew wait here when the stop takes longer
    const float Release = 2.4f, Depart = 1.7f, LiftH = 0.08f;
    const float TOff = 0.72f, TOn = 1.06f, TDrop = 1.55f, TGreen = 2.12f; // old wheels off, new wheels on, old wheels down, light green
    const float WheelR = 0.36f;
    const float T0 = A - 0.3f, T1 = Release + Depart + 0.3f, TabDt = 0.02f; // jog phase table

    // ---------------------------------------------------------------- body (m; person space: x right, y up, z forward, origin on the ground)
    const float HipW = 0.095f, Thigh = 0.45f, Shin = 0.44f, AnkleH = 0.085f, WaistY = 0.12f, ShX = 0.205f, ShY = 0.40f, NeckY = 0.46f, UA = 0.29f, FA = 0.30f;
    const int PartsPer = 13; // 0 pelvis, 1 chest, 2 head, 3/4 upper arm L/R, 5/6 forearm + hand L/R, 7/8 thigh, 9/10 shin, 11/12 foot
    const int KCrew = 0, KEng = 1, KMech = 2;

    // ---------------------------------------------------------------- palette slots
    const int SPrim = 0, SSec = 1, SAcc = 2, SDark = 3, SBoot = 4, SVisor = 5, SGlove = 6, SSkin = 7, SHair = 10, SSet = 12, STrous = 13, SWhite = 14, SKnee = 15, SPrimDk = 16, PplPal = 17;
    const int SRubber = 0, SRim = 1, SOldBand = 2, SNewBand = 3, SMetal = 4, SGun = 5, SBlack = 6, SHose = 7, SJack = 8, SJack2 = 9, SLampR = 10, SLampG = 11, SExt = 12, PropPal = 13;

    // ---------------------------------------------------------------- poses
    internal struct Pose
    {
        // pelvis height, torso lean (fwd +), pelvis pitch / roll, chest twist, pelvis side shift; feet: half spread, forward
        // offsets, lifts, toe pitch; arms: shoulder swing (fwd +), abduction, elbow bend; head pitch (down +) and yaw (deg)
        public float h, lean, pitch, roll, twist, sx, sp, fl, fr, yl, yr, tl, tr, sl, sr, al, ar, el, er, hp, hy;
        public static Pose Lerp(in Pose a, in Pose b, float t)
        {
            float L(float x, float y) => x + (y - x) * t;
            return new Pose { h = L(a.h, b.h), lean = L(a.lean, b.lean), pitch = L(a.pitch, b.pitch), roll = L(a.roll, b.roll), twist = L(a.twist, b.twist), sx = L(a.sx, b.sx),
                              sp = L(a.sp, b.sp), fl = L(a.fl, b.fl), fr = L(a.fr, b.fr), yl = L(a.yl, b.yl), yr = L(a.yr, b.yr), tl = L(a.tl, b.tl), tr = L(a.tr, b.tr),
                              sl = L(a.sl, b.sl), sr = L(a.sr, b.sr), al = L(a.al, b.al), ar = L(a.ar, b.ar), el = L(a.el, b.el), er = L(a.er, b.er), hp = L(a.hp, b.hp), hy = L(a.hy, b.hy) };
        }
    }
    static readonly Pose
        Stand = new Pose { h = 0.935f, lean = 3, sp = 0.12f, fl = 0.03f, fr = -0.02f, sl = 6, sr = 6, al = 9, ar = 9, el = 14, er = 14 },
        Rest = new Pose { h = 0.925f, lean = 2, roll = 3, sx = 0.02f, sp = 0.13f, fl = 0.09f, fr = -0.05f, sl = 4, sr = 7, al = 8, ar = 10, el = 12, er = 18 },
        Ready = new Pose { h = 0.80f, lean = 24, pitch = 12, sp = 0.17f, fl = 0.24f, fr = -0.2f, tr = 12, sl = 38, sr = 38, al = 12, ar = 12, el = 55, er = 55 },
        ReadyBack = new Pose { h = 0.83f, lean = 14, pitch = 6, sp = 0.17f, fl = 0.2f, fr = -0.27f, tr = 18, sl = 22, sr = 22, al = 14, ar = 14, el = 60, er = 60 },
        Kneel = new Pose { h = 0.50f, lean = 38, pitch = 16, sp = 0.15f, fl = 0.36f, fr = -0.40f, yr = 0.07f, tr = 55, sl = 60, sr = 60, al = 12, ar = 12, el = 35, er = 35, hp = 10 },
        Low = new Pose { h = 0.52f, lean = 42, pitch = 20, sp = 0.24f, fl = 0.16f, fr = -0.10f, sl = 75, sr = 75, al = 15, ar = 15, el = 20, er = 20, hp = 10 },
        Carry = new Pose { h = 0.90f, lean = -5, sp = 0.14f, fl = 0.06f, fr = -0.06f, sl = 45, sr = 45, al = 20, ar = 20, el = 75, er = 75, hp = 5 },
        Brace0 = new Pose { h = 0.86f, lean = 12, pitch = 5, sp = 0.16f, fl = 0.30f, fr = -0.28f, sl = 55, sr = 55, al = 10, ar = 10, el = 25, er = 25 },
        Brace = new Pose { h = 0.66f, lean = 20, pitch = 4, sp = 0.18f, fl = 0.36f, fr = -0.36f, tr = 20, sl = 60, sr = 60, al = 10, ar = 10, el = 10, er = 10 },
        HoldCar = new Pose { h = 0.80f, lean = 42, pitch = 16, sp = 0.2f, fl = 0.22f, fr = -0.18f, sl = 75, sr = 75, al = 12, ar = 12, el = 15, er = 15, hp = 10 },
        Signal = new Pose { h = 0.93f, lean = 0, sp = 0.13f, fl = 0.05f, fr = -0.04f, sl = 172, al = 12, el = 8, sr = 20, ar = 10, er = 35 },
        Point = new Pose { h = 0.93f, lean = 2, sp = 0.13f, fl = 0.06f, fr = -0.04f, sl = 15, al = 10, el = 60, sr = 95, ar = 40, er = 5 },
        Cheer = new Pose { h = 0.945f, lean = -4, sp = 0.14f, fl = 0.06f, fr = -0.05f, sl = 160, sr = 162, al = 28, ar = 28, el = 35, er = 30, hp = -10 },
        Fist = new Pose { h = 0.935f, lean = -2, sp = 0.14f, fl = 0.06f, fr = -0.05f, sl = 150, al = 30, el = 100, sr = 10, ar = 10, er = 25 },
        Crossed = new Pose { h = 0.925f, lean = 1, roll = -2, sx = -0.015f, sp = 0.14f, fl = 0.06f, fr = -0.04f, sl = 25, sr = 25, al = 30, ar = 30, el = 110, er = 110 },
        Hips = new Pose { h = 0.93f, lean = 0, roll = 3, sx = 0.02f, sp = 0.15f, fl = 0.08f, fr = -0.06f, sl = -10, sr = -10, al = 55, ar = 55, el = 90, er = 90 },
        Seated = new Pose { h = 0.66f, lean = 8, sp = 0.14f, fl = 0.40f, fr = 0.34f, yl = 0.30f, yr = 0.30f, sl = 40, sr = 40, al = 10, ar = 10, el = 80, er = 80 };

    struct Key { public float t, u, f, a, w, mu, mf; public int e; public Pose p; } // u = towards the garage, f = along the car, a = facing (0 = nose, 90 = garage), w = hands on; e = ease into it (0 smooth, 1 overshoot, 2 anticipation)
    static Key K(float t, float u, float f, float a, in Pose p, float w = 0f, int e = 0) => new Key { t = t, u = u, f = f, a = a, p = p, w = w, e = e };

    const int Gun = 0, Off = 1, On = 2, FJack = 3, RJack = 4, Helper = 5, Rel = 6, Ext = 7, Eng = 8, Watch = 9;
    sealed class Person
    {
        public int role, corner, side, kind, part0, seed, cheer;
        public float scale, delay, gunW;
        public Key[] keys; public float[] phase;
        public Vector3 dropSpot, dropAx, ground, handL, handR, tyre, tyreAx, tip, pivot, hdir, gunAt; public float yaw; public Quaternion foreR;
    }
    const int NPeople = 22;

    // ---------------------------------------------------------------- a batch: many parts in one mesh, vertices written in world space
    internal sealed class Batch
    {
        readonly List<Vector3> bv = new List<Vector3>(); readonly List<float> tint = new List<float>(); readonly List<byte> slot = new List<byte>(); readonly List<int> tri = new List<int>();
        readonly List<int> pStart = new List<int>();
        public Color[] pal; public float tmul = 1f, glow = 0f;
        Vector3[] bva; public Vector3[] wv; int[] ps, pc; bool[] shown; public Color32[] cols; public Mesh mesh; public GameObject go; MeshRenderer mr;
        public Batch(int palSize) { pal = new Color[palSize]; for (int i = 0; i < palSize; i++) pal[i] = Color.gray; }
        public int Parts => pStart.Count;
        public int NewPart() { pStart.Add(bv.Count); return pStart.Count - 1; }

        static float Sh(Vector3 n) => Mathf.Lerp(0.55f, 1f, n.y * 0.5f + 0.5f) * (0.9f + 0.1f * n.z);
        int V(Vector3 p, int sl, float t) { bv.Add(p); slot.Add((byte)sl); tint.Add((glow > 0f ? glow : t) * tmul); return bv.Count - 1; }
        // a triangle of existing vertices, wound to face n (Unity: front = Cross(b - a, c - a))
        void T(int a, int b, int c, Vector3 n)
        {
            if (Vector3.Dot(Vector3.Cross(bv[b] - bv[a], bv[c] - bv[a]), n) < 0f) { int x = b; b = c; c = x; }
            tri.Add(a); tri.Add(b); tri.Add(c);
        }
        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 n, int sl, float t) { int i = V(a, sl, t), j = V(b, sl, t), k = V(c, sl, t); T(i, j, k, n); }
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, int sl, float t) { Tri(a, b, c, n, sl, t); Tri(a, c, d, n, sl, t); }

        static readonly Vector3[] BN = { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
        static readonly Vector3[] BA = { Vector3.right, Vector3.right, Vector3.forward, Vector3.forward, Vector3.right, Vector3.right };
        static readonly Vector3[] BB = { Vector3.forward, Vector3.forward, Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        public void Box(Vector3 c, Vector3 size, int sl, Quaternion q)
        {
            Vector3 h = size * 0.5f;
            for (int f = 0; f < 6; f++)
            {
                Vector3 cen = Vector3.Scale(BN[f], h), ea = Vector3.Scale(BA[f], h), eb = Vector3.Scale(BB[f], h);
                Vector3 n = q * BN[f];
                Quad(c + q * (cen - ea - eb), c + q * (cen + ea - eb), c + q * (cen + ea + eb), c + q * (cen - ea + eb), n, sl, Sh(n));
            }
        }
        // a smooth-shaded tapered prism between two horizontal elliptic rings (c0 above c1), optional flat caps
        public void Prism(Vector3 c0, float r0x, float r0z, Vector3 c1, float r1x, float r1z, int n, int sl, bool cap0 = true, bool cap1 = true)
        {
            int k0 = bv.Count; Vector3 ax = (c0 - c1).normalized;
            float slope = ((r0x + r0z) - (r1x + r1z)) * 0.5f / Mathf.Max(0.01f, (c0 - c1).magnitude);
            for (int r = 0; r < 2; r++)
                for (int i = 0; i < n; i++)
                {
                    float a = (i + 0.5f) * 2f * Mathf.PI / n, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
                    float rx = r == 0 ? r0x : r1x, rz = r == 0 ? r0z : r1z;
                    Vector3 nr = new Vector3(cs / Mathf.Max(rx, 1e-3f), 0, sn / Mathf.Max(rz, 1e-3f)).normalized - ax * slope;
                    V((r == 0 ? c0 : c1) + new Vector3(cs * rx, 0, sn * rz), sl, Sh(nr.normalized));
                }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n; float am = (i + 1f) * 2f * Mathf.PI / n;
                Vector3 o = new Vector3(Mathf.Cos(am), 0, Mathf.Sin(am));
                T(k0 + i, k0 + j, k0 + n + j, o); T(k0 + i, k0 + n + j, k0 + n + i, o);
            }
            if (cap0) Cap(c0, r0x, r0z, n, sl, ax);
            if (cap1) Cap(c1, r1x, r1z, n, sl, -ax);
        }
        void Cap(Vector3 c, float rx, float rz, int n, int sl, Vector3 nrm)
        {
            float t = Sh(nrm); int k = V(c, sl, t), r0 = bv.Count;
            for (int i = 0; i < n; i++) { float a = (i + 0.5f) * 2f * Mathf.PI / n; V(c + new Vector3(Mathf.Cos(a) * rx, 0, Mathf.Sin(a) * rz), sl, t); }
            for (int i = 0; i < n; i++) T(k, r0 + i, r0 + (i + 1) % n, nrm);
        }
        // a smooth body of revolution about local Y (rings (y, r), z scaled by sz), bottom cap optional; r = 0 closes the top
        public void Lathe(Vector3 c, float[] ys, float[] rs, int n, int sl, float sz = 1f, bool bottom = true)
        {
            int m = ys.Length, k0 = bv.Count;
            for (int r = 0; r < m; r++)
            {
                int ra = Mathf.Max(r - 1, 0), rb = Mathf.Min(r + 1, m - 1);
                float dr = (rs[rb] - rs[ra]) / Mathf.Max(1e-3f, ys[rb] - ys[ra]);
                for (int i = 0; i < n; i++)
                {
                    float a = i * 2f * Mathf.PI / n, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
                    V(c + new Vector3(cs * rs[r], ys[r], sn * rs[r] * sz), sl, Sh(new Vector3(cs, -dr, sn).normalized));
                }
            }
            for (int r = 0; r + 1 < m; r++)
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n, a0 = k0 + r * n + i, a1 = k0 + r * n + j, b0 = a0 + n, b1 = a1 + n;
                    float am = (i + 0.5f) * 2f * Mathf.PI / n; Vector3 o = new Vector3(Mathf.Cos(am), 0, Mathf.Sin(am) * sz);
                    if (rs[r + 1] < 1e-3f) T(a0, a1, b0, o + Vector3.up);
                    else { T(a0, a1, b1, o); T(a0, b1, b0, o); }
                }
            if (bottom && rs[0] > 1e-3f)
            {
                int k = V(c + Vector3.up * ys[0], sl, 0.55f);
                for (int i = 0; i < n; i++) T(k, k0 + i, k0 + (i + 1) % n, Vector3.down);
            }
        }
        // a cylinder along local X (rotated by q): side slot, outer ring of the caps (down to rIn) and the inner disc
        public void Cyl(Vector3 c, Quaternion q, float r, float len, int n, int sSide, int sOuter, int sInner, float rIn)
        {
            float hx = len * 0.5f;
            for (int k = 0; k < n; k++)
            {
                float a0 = 2f * Mathf.PI * k / n, a1 = 2f * Mathf.PI * (k + 1) / n, am = (a0 + a1) * 0.5f;
                Vector3 n0 = new Vector3(0, Mathf.Cos(a0), Mathf.Sin(a0)), n1 = new Vector3(0, Mathf.Cos(a1), Mathf.Sin(a1)), nm = q * new Vector3(0, Mathf.Cos(am), Mathf.Sin(am));
                Quad(c + q * (n0 * r + new Vector3(-hx, 0, 0)), c + q * (n1 * r + new Vector3(-hx, 0, 0)), c + q * (n1 * r + new Vector3(hx, 0, 0)), c + q * (n0 * r + new Vector3(hx, 0, 0)), nm, sSide, Sh(nm));
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector3 nc = q * new Vector3(s, 0, 0); float tt = Sh(nc);
                    Vector3 o = new Vector3(s * hx, 0, 0);
                    Quad(c + q * (n0 * r + o), c + q * (n1 * r + o), c + q * (n1 * rIn + o), c + q * (n0 * rIn + o), nc, sOuter, tt);
                    Tri(c + q * o, c + q * (n0 * rIn + o), c + q * (n1 * rIn + o), nc, sInner, tt);
                }
            }
        }

        public void Prepare()
        {
            bva = bv.ToArray(); wv = new Vector3[bva.Length]; cols = new Color32[bva.Length]; shown = new bool[Parts]; ps = new int[Parts]; pc = new int[Parts];
            for (int i = 0; i < Parts; i++) { ps[i] = pStart[i]; pc[i] = (i + 1 < Parts ? pStart[i + 1] : bva.Length) - pStart[i]; }
        }
        public void Finish(Material mat, Transform parent, string name)
        {
            Prepare();
            mesh = new Mesh { name = name }; mesh.MarkDynamic();
            mesh.vertices = wv; mesh.triangles = tri.ToArray(); mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100f);
            go = new GameObject(name); go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            Recolour();
        }
        public void Recolour(int part = -1)
        {
            int a = part < 0 ? 0 : ps[part], b = part < 0 ? bva.Length : ps[part] + pc[part];
            for (int i = a; i < b; i++)
            {
                Color c = pal[slot[i]].linear; float k = 0.5f * tint[i]; // the world shader doubles the vertex colour (stored light 0.5 = full)
                cols[i] = new Color32((byte)(Mathf.Clamp01(c.r * k) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.g * k) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.b * k) * 255f + 0.5f), 255);
            }
            if (mesh != null) mesh.colors32 = cols;
        }
        public void Set(int p, in Matrix4x4 m)
        {
            shown[p] = true;
            for (int i = ps[p], e = ps[p] + pc[p]; i < e; i++) wv[i] = m.MultiplyPoint3x4(bva[i]);
        }
        public void Hide(int p)
        {
            if (!shown[p]) return; shown[p] = false;
            for (int i = ps[p], e = ps[p] + pc[p]; i < e; i++) wv[i] = Vector3.zero;
        }
        public void HideAll() { for (int i = 0; i < Parts; i++) Hide(i); }
        public void Flush(Vector3 centre) { mesh.vertices = wv; mesh.bounds = new Bounds(centre, Vector3.one * 60f); }
        public void Show(bool on) { if (go.activeSelf != on) go.SetActive(on); }
        // the posed vertices and their colours, appended to another mesh (static people in the garages)
        public void Export(List<Vector3> v, List<Vector2> uv, Vector2 u, List<Color32> c, List<int> t)
        {
            int b = v.Count;
            for (int i = 0; i < wv.Length; i++) { v.Add(wv[i]); uv.Add(u); c.Add(cols[i]); }
            for (int i = 0; i < tri.Count; i++) t.Add(tri[i] + b);
        }
    }

    // ---------------------------------------------------------------- state
    enum St { Off, Wait, Run }
    St st; int owner = -99, bay = -1; float extra, curS = A;
    Batch ppl, props;
    System.Func<CarModel> target; System.Func<float> clock; CarModel curModel;
    Transform[] wheelRoots = new Transform[4]; readonly bool[] wheelWas = new bool[4];
    int[] order;
    Vector3 centre, fwd, side; float gs, garageDist;
    Matrix4x4 F;
    readonly float[] wu = new float[4], wf = new float[4]; float frontEnd, rearEnd;
    readonly Person[] crew = new Person[NPeople];
    Livery liv; Color oldC, newC; bool lightGreen, wheelsOff, newStripe, visible = true;
    public System.Action onTick;

    public bool Busy => st == St.Run;
    public bool Waiting(int who) => st == St.Wait && owner == who;
    public bool Owns(int who) => st != St.Off && owner == who;
    public int PlayerBay => order[0];
    public int TeamBay(int team) { int n = order.Length; return n > 1 ? order[1 + ((team % (n - 1)) + (n - 1)) % (n - 1)] : order[0]; }
    public Vector3 BayPos(int b) => Trackside.Bays[b].centre;

    // ---------------------------------------------------------------- creation
    static Material pitMat;
    static Material Mat()
    {
        if (pitMat != null) return pitMat;
        var sh = Resources.Load<Shader>("Baked"); if (sh == null || !sh.isSupported) sh = Shader.Find("Sprites/Default");
        pitMat = new Material(sh) { mainTexture = Texture2D.whiteTexture, color = Color.white };
        return pitMat;
    }

    // trackN = points on the track; player = the player's livery; teams = rival liveries by team index (garage colours)
    public static PitCrew Create(int trackN, Livery player, Dictionary<int, Livery> teams)
    {
        if (Trackside.Bays.Count == 0) return null;
        var go = new GameObject("PitCrew");
        var c = go.AddComponent<PitCrew>(); c.Init(trackN, player, teams);
        return c;
    }

    static float Rnd(int a, int b) { float x = Mathf.Sin(a * 12.9898f + b * 78.233f) * 43758.5453f; return x - Mathf.Floor(x); }
    static Color C(float r, float g, float b) => new Color(r, g, b);

    void Init(int N, Livery player, Dictionary<int, Livery> teams)
    {
        var bays = Trackside.Bays; int n = bays.Count;
        order = PitGarage.BayOrder(N);
        // the garages: the player's box and the rival teams' boxes in their colours and (invented) names
        var gl = new Livery[n]; var gn = new string[n];
        gl[PlayerBay] = player; gn[PlayerBay] = PitGarage.PlayerBrand(player);
        if (teams != null) foreach (var kv in teams) { int b = TeamBay(kv.Key); if (gn[b] == null) { gl[b] = kv.Value; gn[b] = PitGarage.Brand(kv.Key); } }
        PitGarage.SetTeams(gl, gn);
        var mat = Mat();
        ppl = new Batch(PplPal);
        for (int i = 0; i < NPeople; i++)
        {
            int kind = i < 18 ? KCrew : i < 20 ? KEng : KMech;
            var p = new Person { part0 = ppl.Parts, kind = kind, seed = i * 13 + 5, scale = 0.95f + 0.1f * Rnd(i, 7), delay = 0.09f * Rnd(i, 8) };
            p.cheer = (int)(Rnd(i, 9) * 2.999f);
            crew[i] = p; BuildBody(ppl, kind, p.seed);
        }
        ppl.tmul = 1f;
        ppl.Finish(mat, transform, "PitPeople");
        props = new Batch(PropPal); BuildProps(props); props.Finish(mat, transform, "PitProps");
        ppl.Show(false); props.Show(false);
        st = St.Off;
    }

    // ---------------------------------------------------------------- the body (one person = PartsPer parts in batch b)
    internal static void BuildBody(Batch b, int kind, int seed)
    {
        b.tmul = 0.93f + 0.12f * Rnd(seed, 1);
        int skin = SSkin + Mathf.Min(2, (int)(Rnd(seed, 2) * 3f)), hair = SHair + (Rnd(seed, 3) < 0.7f ? 0 : 1);
        bool eng = kind == KEng, crew = kind == KCrew, cap = kind == KMech && Rnd(seed, 4) < 0.6f;
        int legs = eng ? STrous : SPrim, shin = eng ? STrous : SPrimDk;
        var id = Quaternion.identity;
        // 0 pelvis (origin: the hip joints' centre), belt
        b.NewPart();
        b.Prism(new Vector3(0, 0.12f, 0), 0.15f, 0.10f, new Vector3(0, -0.06f, 0), 0.165f, 0.11f, 8, legs);
        b.Prism(new Vector3(0, 0.14f, 0), 0.158f, 0.108f, new Vector3(0, 0.085f, 0), 0.16f, 0.11f, 8, SDark, false, false);
        b.Box(new Vector3(0, -0.085f, 0.0f), new Vector3(0.13f, 0.06f, 0.13f), legs, id);
        // 1 chest (origin: the waist): lower body, upper yoke, accent line, flank panels; engineers: shirt and collar
        b.NewPart();
        b.Prism(new Vector3(0, 0.25f, 0), 0.178f, 0.118f, new Vector3(0, 0f, 0), 0.148f, 0.102f, 8, SPrim, false, true);
        b.Prism(new Vector3(0, 0.44f, 0), 0.15f, 0.095f, new Vector3(0, 0.25f, 0), 0.186f, 0.118f, 8, eng ? SPrim : SSec, true, false);
        if (!eng)
        {
            b.Prism(new Vector3(0, 0.262f, 0), 0.19f, 0.122f, new Vector3(0, 0.238f, 0), 0.186f, 0.12f, 8, SAcc, false, false);
            for (int s = -1; s <= 1; s += 2) b.Box(new Vector3(s * 0.158f, 0.13f, 0), new Vector3(0.03f, 0.2f, 0.15f), SDark, id);
        }
        else b.Prism(new Vector3(0, 0.47f, 0), 0.075f, 0.065f, new Vector3(0, 0.42f, 0), 0.11f, 0.08f, 8, SSec, false, false);
        // 2 head (origin: the base of the neck)
        b.NewPart();
        b.Prism(new Vector3(0, 0.1f, 0), 0.05f, 0.05f, new Vector3(0, -0.02f, 0), 0.056f, 0.056f, 6, crew ? SDark : skin, false, false);
        if (crew)
        {
            Vector3 hc = new Vector3(0, 0.17f, 0.005f);
            b.Lathe(hc, new[] { -0.13f, -0.09f, -0.01f }, new[] { 0.105f, 0.125f, 0.135f }, 8, SPrim, 1.12f, true);
            b.Lathe(hc, new[] { -0.01f, 0.07f, 0.115f, 0.138f }, new[] { 0.135f, 0.122f, 0.08f, 0f }, 8, SSec, 1.12f, false);
            // visor: a band round the front
            for (int k = 0; k < 4; k++)
            {
                float a0 = Mathf.Lerp(-55f, 55f, k / 4f) * Mathf.Deg2Rad, a1 = Mathf.Lerp(-55f, 55f, (k + 1) / 4f) * Mathf.Deg2Rad;
                Vector3 d0 = new Vector3(Mathf.Sin(a0) * 0.139f, 0, Mathf.Cos(a0) * 0.139f * 1.12f), d1 = new Vector3(Mathf.Sin(a1) * 0.139f, 0, Mathf.Cos(a1) * 0.139f * 1.12f);
                Vector3 nn = (d0 + d1).normalized;
                b.glow = 1.15f;
                b.Quad(hc + d0 + Vector3.up * -0.035f, hc + d1 + Vector3.up * -0.035f, hc + d1 + Vector3.up * 0.045f, hc + d0 + Vector3.up * 0.045f, nn, SVisor, 1f);
                b.glow = 0f;
            }
            b.Box(hc + new Vector3(0, -0.085f, 0.125f), new Vector3(0.13f, 0.05f, 0.05f), SDark, id);
        }
        else
        {
            Vector3 hc = new Vector3(0, 0.16f, 0);
            b.Lathe(hc, new[] { -0.12f, -0.08f, -0.01f, 0.06f, 0.10f, 0.12f }, new[] { 0.055f, 0.085f, 0.093f, 0.09f, 0.06f, 0f }, 8, skin, 1.1f);
            b.Box(hc + new Vector3(0, -0.005f, 0.105f), new Vector3(0.028f, 0.045f, 0.03f), skin, id);
            if (cap)
            {
                b.Lathe(hc + new Vector3(0, 0, -0.005f), new[] { 0.02f, 0.07f, 0.11f, 0.13f }, new[] { 0.103f, 0.1f, 0.066f, 0f }, 8, SPrim, 1.12f, false);
                b.Box(hc + new Vector3(0, 0.03f, 0.12f), new Vector3(0.16f, 0.012f, 0.1f), SDark, Quaternion.Euler(-8f, 0, 0));
            }
            else b.Lathe(hc + new Vector3(0, 0, -0.012f), new[] { 0.0f, 0.06f, 0.105f, 0.128f }, new[] { 0.099f, 0.097f, 0.066f, 0f }, 8, hair, 1.12f, false);
            if (eng)
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    b.Box(hc + new Vector3(s * 0.1f, 0.0f, 0), new Vector3(0.035f, 0.075f, 0.07f), SSet, id);
                    b.Box(hc + new Vector3(s * 0.1f, 0.075f, 0), new Vector3(0.015f, 0.09f, 0.025f), SSet, id);
                }
                b.Box(hc + new Vector3(0, 0.128f, 0), new Vector3(0.2f, 0.02f, 0.03f), SSet, id);
                b.Box(hc + new Vector3(-0.085f, -0.055f, 0.075f), new Vector3(0.014f, 0.014f, 0.12f), SSet, Quaternion.Euler(0, 22f, 0));
            }
        }
        // 3/4 upper arms (origin: the shoulder, bone along -y)
        for (int s = 0; s < 2; s++)
        {
            b.NewPart();
            if (eng) { b.Prism(new Vector3(0, 0.03f, 0), 0.062f, 0.062f, new Vector3(0, -0.13f, 0), 0.056f, 0.056f, 6, SPrim); b.Prism(new Vector3(0, -0.13f, 0), 0.045f, 0.045f, new Vector3(0, -0.285f, 0), 0.04f, 0.04f, 6, skin, false, true); }
            else b.Prism(new Vector3(0, 0.03f, 0), 0.058f, 0.058f, new Vector3(0, -0.285f, 0), 0.046f, 0.046f, 6, SSec);
        }
        // 5/6 forearms with the hand (origin: the elbow)
        for (int s = 0; s < 2; s++)
        {
            b.NewPart();
            b.Prism(new Vector3(0, 0.01f, 0), 0.046f, 0.046f, new Vector3(0, -0.235f, 0), 0.036f, 0.036f, 6, eng ? skin : SPrim, false, false);
            if (!eng) b.Prism(new Vector3(0, -0.205f, 0), 0.042f, 0.042f, new Vector3(0, -0.245f, 0), 0.04f, 0.04f, 6, SGlove, false, true);
            b.Box(new Vector3(0, -0.295f, 0.004f), new Vector3(0.075f, 0.11f, 0.042f), eng ? skin : SGlove, id);
        }
        // 7/8 thighs (origin: the hip joint)
        for (int s = 0; s < 2; s++) { b.NewPart(); b.Prism(new Vector3(0, 0.04f, 0), 0.088f, 0.094f, new Vector3(0, -0.44f, 0), 0.06f, 0.066f, 8, legs, false, false); }
        // 9/10 shins (origin: the knee), knee pads
        for (int s = 0; s < 2; s++)
        {
            b.NewPart(); b.Prism(new Vector3(0, 0.02f, 0), 0.062f, 0.066f, new Vector3(0, -0.41f, 0), 0.045f, 0.05f, 6, shin, true, false);
            if (crew) b.Box(new Vector3(0, -0.04f, 0.055f), new Vector3(0.1f, 0.13f, 0.03f), SKnee, id);
        }
        // 11/12 shoes (origin: the ankle), soles
        for (int s = 0; s < 2; s++)
        {
            b.NewPart();
            b.Box(new Vector3(0, -0.035f, 0.06f), new Vector3(0.1f, 0.08f, 0.26f), SBoot, id);
            b.Box(new Vector3(0, -0.082f, 0.062f), new Vector3(0.106f, 0.02f, 0.27f), crew ? SSec : SWhite, id);
        }
        b.tmul = 1f;
    }

    static void PeoplePal(Color[] p, Livery l)
    {
        p[SPrim] = l.primary; p[SSec] = l.secondary; p[SAcc] = l.accent; p[SDark] = C(0.07f, 0.07f, 0.08f); p[SBoot] = C(0.06f, 0.06f, 0.07f);
        p[SVisor] = C(0.05f, 0.07f, 0.11f); p[SGlove] = C(0.09f, 0.09f, 0.10f);
        p[SSkin] = C(0.82f, 0.63f, 0.50f); p[SSkin + 1] = C(0.64f, 0.46f, 0.34f); p[SSkin + 2] = C(0.40f, 0.28f, 0.20f);
        p[SHair] = C(0.10f, 0.07f, 0.05f); p[SHair + 1] = C(0.36f, 0.26f, 0.16f); p[SSet] = C(0.04f, 0.04f, 0.05f); p[STrous] = C(0.10f, 0.11f, 0.14f);
        p[SWhite] = C(0.92f, 0.92f, 0.93f); p[SKnee] = C(0.16f, 0.16f, 0.18f);
        p[SPrimDk] = Color.Lerp(l.primary * 0.45f, C(0.08f, 0.08f, 0.09f), 0.3f);
    }
    static Color GunCol(Livery l) { var a = l.accent; float m = Mathf.Max(a.r, Mathf.Max(a.g, a.b)); return m > 0.3f ? a : l.secondary; }

    // a posed static person for PitGarage (pose 0 seated at a desk, 1 arms crossed, 2 hands on hips); root = world ground point
    internal static void StaticPerson(int kind, Livery l, int seed, Matrix4x4 root, int pose, List<Vector3> v, List<Vector2> uv, Vector2 uvW, List<Color32> c, List<int> t)
    {
        var b = new Batch(PplPal); BuildBody(b, kind, seed); b.Prepare(); PeoplePal(b.pal, l); b.Recolour();
        Pose q; Vector3 tl, tr;
        switch (pose)
        {
            case 0: q = Seated; tl = new Vector3(-0.17f, 0.81f, 0.42f); tr = new Vector3(0.19f, 0.80f, 0.40f); break;
            case 1: q = Crossed; tl = new Vector3(0.10f, 1.22f, 0.18f); tr = new Vector3(-0.10f, 1.19f, 0.20f); break;
            default: q = Hips; tl = new Vector3(-0.2f, 0.99f, 0f); tr = new Vector3(0.2f, 0.99f, 0f); break;
        }
        q.hy = (Rnd(seed, 5) - 0.5f) * 50f; q.twist += (Rnd(seed, 6) - 0.5f) * 10f; q.hp += (Rnd(seed, 7) - 0.3f) * 15f;
        PoseBody(b, 0, root, q, tl, 1f, tr, 1f, out _, out _, out _);
        b.Export(v, uv, uvW, c, t);
    }

    // ---------------------------------------------------------------- posing (per-part transforms, two-bone IK)
    static void TwoBone(Vector3 a, Vector3 t, float l1, float l2, Vector3 pole, out Vector3 mid, out Vector3 end)
    {
        Vector3 d = t - a; float len = d.magnitude;
        Vector3 dn = len > 1e-5f ? d / len : Vector3.down;
        float dist = Mathf.Clamp(len, Mathf.Abs(l1 - l2) + 0.03f, l1 + l2 - 0.002f);
        float ca = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f), sa = Mathf.Sqrt(1f - ca * ca);
        Vector3 pp = pole - dn * Vector3.Dot(pole, dn);
        if (pp.sqrMagnitude < 1e-8f) pp = Vector3.Cross(dn, Vector3.right);
        pp.Normalize();
        mid = a + dn * (l1 * ca) + pp * (l1 * sa); end = a + dn * dist;
    }
    // a limb's rotation: local -y along dir, local +z towards hint (blended with alt so it never flips)
    static Quaternion Limb(Vector3 dir, Vector3 hint, Vector3 alt)
    {
        if (dir.sqrMagnitude < 1e-10f) return Quaternion.identity;
        Vector3 y = -dir.normalized;
        Vector3 z = (hint - y * Vector3.Dot(hint, y)) + 0.35f * (alt - y * Vector3.Dot(alt, y));
        if (z.sqrMagnitude < 1e-8f) z = Vector3.Cross(y, Vector3.right);
        return Quaternion.LookRotation(z.normalized, y);
    }
    // pose one body: FR = batch space * root (ground point, facing, scale); hand targets tL / tR in root space with weights;
    // returns the hands (root space) and the right forearm's rotation (root space)
    internal static void PoseBody(Batch b, int p0, Matrix4x4 FR, in Pose q, Vector3 tL, float wL, Vector3 tR, float wR, out Vector3 hL, out Vector3 hR, out Quaternion fR)
    {
        Quaternion qP = Quaternion.Euler(q.pitch, 0, q.roll);
        Vector3 pP = new Vector3(q.sx, q.h, 0);
        Quaternion qC = qP * Quaternion.Euler(q.lean - q.pitch, q.twist, 0);
        Vector3 pC = pP + qP * new Vector3(0, WaistY, 0);
        Quaternion qH = qC * Quaternion.Euler(q.hp - 0.7f * q.lean, q.hy, 0);
        Vector3 pH = pC + qC * new Vector3(0, NeckY, 0);
        b.Set(p0, FR * Matrix4x4.TRS(pP, qP, Vector3.one));
        b.Set(p0 + 1, FR * Matrix4x4.TRS(pC, qC, Vector3.one));
        b.Set(p0 + 2, FR * Matrix4x4.TRS(pH, qH, Vector3.one));
        for (int s = 0; s < 2; s++)
        {
            float sg = s == 0 ? -1f : 1f;
            Vector3 hip = pP + qP * new Vector3(sg * HipW, 0, 0);
            Vector3 ank = new Vector3(sg * q.sp, AnkleH + (s == 0 ? q.yl : q.yr), s == 0 ? q.fl : q.fr);
            Vector3 pole = new Vector3(sg * 0.15f, 0.1f, 1f);
            TwoBone(hip, ank, Thigh, Shin, pole, out Vector3 knee, out Vector3 an);
            b.Set(p0 + 7 + s, FR * Matrix4x4.TRS(hip, Limb(knee - hip, pole, Vector3.forward), Vector3.one));
            b.Set(p0 + 9 + s, FR * Matrix4x4.TRS(knee, Limb(an - knee, pole, Vector3.forward), Vector3.one));
            b.Set(p0 + 11 + s, FR * Matrix4x4.TRS(an, Quaternion.Euler(s == 0 ? q.tl : q.tr, sg * 6f, 0), Vector3.one));
        }
        hL = hR = Vector3.zero; fR = Quaternion.identity;
        Vector3 cf = qC * Vector3.forward, cu = qC * Vector3.up;
        for (int s = 0; s < 2; s++)
        {
            float sg = s == 0 ? -1f : 1f, sh = s == 0 ? q.sl : q.sr, ab = s == 0 ? q.al : q.ar, el = s == 0 ? q.el : q.er, w = s == 0 ? wL : wR;
            Vector3 S = pC + qC * new Vector3(sg * ShX, ShY, 0);
            Quaternion qu = qC * Quaternion.Euler(-sh, 0, sg * ab), qf = qu * Quaternion.Euler(-el, 0, 0);
            Vector3 eFK = S + qu * new Vector3(0, -UA, 0), hFK = eFK + qf * new Vector3(0, -FA, 0);
            Vector3 tg = w > 0.001f ? Vector3.Lerp(hFK, s == 0 ? tL : tR, Mathf.Clamp01(w)) : hFK;
            Vector3 pole = (eFK - (S + hFK) * 0.5f) + qC * new Vector3(sg * 0.06f, -0.02f, -0.08f);
            TwoBone(S, tg, UA, FA, pole, out Vector3 e, out Vector3 h);
            Quaternion q2 = Limb(h - e, cf, cu);
            b.Set(p0 + 3 + s, FR * Matrix4x4.TRS(S, Limb(e - S, cf, cu), Vector3.one));
            b.Set(p0 + 5 + s, FR * Matrix4x4.TRS(e, q2, Vector3.one));
            if (s == 0) hL = h; else { hR = h; fR = q2; }
        }
    }

    // ---------------------------------------------------------------- props
    const int HoseSeg = 6, POld = 0, PNew = 4, PGun = 8, PHose = 12, PJack = 12 + 4 * HoseSeg, PLight = PJack + 6, PHand = PLight + 1, PExt = PLight + 2;
    static void BuildProps(Batch b)
    {
        var id = Quaternion.identity;
        for (int i = 0; i < 4; i++) { b.NewPart(); b.Cyl(Vector3.zero, id, WheelR, 0.40f, 14, SRubber, SOldBand, SRim, 0.22f); }   // old tyres (compound band of the old set)
        for (int i = 0; i < 4; i++) { b.NewPart(); b.Cyl(Vector3.zero, id, WheelR, 0.40f, 14, SRubber, SNewBand, SRim, 0.22f); }   // new tyres
        for (int i = 0; i < 4; i++) // wheel guns: body along +z to the socket, pistol grip under the back, front handle on top
        {
            b.NewPart();
            b.Box(new Vector3(0, 0, 0.10f), new Vector3(0.11f, 0.12f, 0.30f), SGun, id);
            b.Box(new Vector3(0, -0.10f, -0.01f), new Vector3(0.05f, 0.13f, 0.06f), SBlack, id);
            b.Box(new Vector3(0, 0.09f, 0.16f), new Vector3(0.04f, 0.07f, 0.10f), SBlack, id);
            b.Cyl(new Vector3(0, 0, 0.29f), Quaternion.Euler(0, 90, 0), 0.04f, 0.08f, 8, SMetal, SMetal, SBlack, 0.02f);
        }
        for (int i = 0; i < 4 * HoseSeg; i++) { b.NewPart(); b.Box(new Vector3(0, 0, 0.5f), new Vector3(0.035f, 0.035f, 1.02f), SHose, id); } // hose segments (scaled in z)
        for (int j = 0; j < 2; j++)
        {
            b.NewPart(); b.Box(new Vector3(0, 0.08f, 0), new Vector3(0.30f, 0.10f, 0.36f), SJack, id);                       // base with castors
            for (int k = 0; k < 4; k++) b.Box(new Vector3((k & 1) * 0.26f - 0.13f, 0.03f, (k >> 1) * 0.3f - 0.15f), new Vector3(0.05f, 0.06f, 0.06f), SBlack, id);
            b.NewPart(); b.Box(new Vector3(0, 0.07f, 0), new Vector3(0.08f, 0.14f, 0.08f), SMetal, id); b.Box(new Vector3(0, 0.15f, 0), new Vector3(0.36f, 0.04f, 0.1f), SJack2, id); // column, cradle
            b.NewPart(); b.Box(new Vector3(0, 0, 0.55f), new Vector3(0.04f, 0.04f, 1.1f), SMetal, id); b.Box(new Vector3(0, 0, 1.1f), new Vector3(0.34f, 0.045f, 0.045f), SBlack, id); // handle, grip bar
        }
        b.NewPart(); // release light on the gantry: housing, hood, red and green lamps facing the driver (-z)
        b.Box(Vector3.zero, new Vector3(0.44f, 0.22f, 0.12f), SBlack, id); b.Box(new Vector3(0, 0.12f, -0.08f), new Vector3(0.44f, 0.02f, 0.08f), SBlack, id);
        b.glow = 2f; b.Box(new Vector3(-0.1f, 0, -0.062f), new Vector3(0.13f, 0.13f, 0.01f), SLampR, id); b.Box(new Vector3(0.1f, 0, -0.062f), new Vector3(0.13f, 0.13f, 0.01f), SLampG, id); b.glow = 0f;
        b.NewPart(); b.Box(Vector3.zero, new Vector3(0.06f, 0.13f, 0.04f), SBlack, id); b.glow = 1.8f; b.Box(new Vector3(0, 0.067f, 0), new Vector3(0.03f, 0.01f, 0.03f), SLampG, id); b.glow = 0f; // release handset
        b.NewPart(); b.Cyl(new Vector3(0, -0.24f, 0.05f), Quaternion.Euler(0, 0, 90), 0.085f, 0.45f, 8, SExt, SMetal, SMetal, 0.03f); b.Box(new Vector3(0, 0.0f, 0.05f), new Vector3(0.05f, 0.06f, 0.08f), SBlack, id); // extinguisher
    }

    void OnDestroy() { RestoreWheels(); }

    // ---------------------------------------------------------------- public control
    // crew standing ready in a bay (the player armed BOX, or a car is coming in); car = the car's model, if known (its wheel
    // positions place the crew)
    public void Wait(int who, int bayIdx, Livery l, Color oldTyre, Color newTyre, System.Func<CarModel> car = null)
    {
        if (st == St.Run) return;
        if (st == St.Wait && owner == who && bay == bayIdx) return;
        Prepare(who, bayIdx, l, oldTyre, newTyre, car); st = St.Wait; extra = 0; curS = A; ApplyFrame(A);
    }
    public void CancelWait(int who) { if (st == St.Wait && owner == who) Stop(); }
    // the stop on the real car: clk = the car's pit clock (s to the stop, negative, then s since it); extraHold = seconds the
    // stop takes beyond the normal one (crew hold position). A frozen clock freezes the crew (shot mode).
    public bool Begin(int who, int bayIdx, Livery l, Color oldTyre, Color newTyre, float extraHold, System.Func<CarModel> car, System.Func<float> clk)
    {
        if (st == St.Run) return owner == who;
        if (!(st == St.Wait && owner == who && bay == bayIdx)) Prepare(who, bayIdx, l, oldTyre, newTyre, car);
        else { target = car; AcquireWheels(car != null ? car() : null); }
        st = St.Run; extra = Mathf.Clamp(extraHold, 0f, 10f); clock = clk; curS = clk != null ? clk() : 0f;
        return true;
    }
    public void Stop()
    {
        RestoreWheels();
        st = St.Off; owner = -99; target = null; clock = null; curModel = null;
        if (ppl != null) { ppl.HideAll(); props.HideAll(); ppl.Show(false); props.Show(false); }
    }

    // ---------------------------------------------------------------- set up one stop
    void Prepare(int who, int bayIdx, Livery l, Color oldTyre, Color newTyre, System.Func<CarModel> car)
    {
        RestoreWheels();
        owner = who; bay = bayIdx; liv = l; oldC = oldTyre; newC = newTyre; lightGreen = false; wheelsOff = false; newStripe = false;
        var bb = Trackside.Bays[bayIdx]; fwd = bb.fwd; side = bb.side; garageDist = bb.garageDist;
        float gy = WorldBuilder.H(bb.centre.x, bb.centre.z); centre = new Vector3(bb.centre.x, gy, bb.centre.z);
        Vector3 right = Vector3.Cross(Vector3.up, fwd); gs = Vector3.Dot(side, right) >= 0f ? 1f : -1f;
        F = Matrix4x4.TRS(centre, Quaternion.LookRotation(fwd, Vector3.up), Vector3.one);
        // palettes
        PeoplePal(ppl.pal, l);
        var pp = props.pal;
        pp[SRubber] = C(0.05f, 0.05f, 0.055f); pp[SRim] = C(0.58f, 0.60f, 0.64f); pp[SOldBand] = oldTyre; pp[SNewBand] = newTyre;
        pp[SMetal] = C(0.45f, 0.46f, 0.50f); pp[SGun] = GunCol(l); pp[SBlack] = C(0.04f, 0.04f, 0.045f); pp[SHose] = C(0.07f, 0.07f, 0.08f);
        pp[SJack] = l.primary; pp[SJack2] = C(0.12f, 0.12f, 0.13f); pp[SLampR] = C(1f, 0.08f, 0.06f); pp[SLampG] = C(0.04f, 0.12f, 0.06f); pp[SExt] = C(0.78f, 0.06f, 0.05f);
        // the real car: its wheels, measured in its own frame, place the crew
        target = car; MeasureWheels(car != null ? car() : null);
        // the crew
        int n = 0;
        for (int c = 0; c < 4; c++) { Person p = crew[n++]; p.role = Gun; p.corner = c; p.keys = GunKeys(c, p); }
        for (int c = 0; c < 4; c++) { Person p = crew[n++]; p.role = Off; p.corner = c; p.keys = OffKeys(c, p); }
        for (int c = 0; c < 4; c++) { Person p = crew[n++]; p.role = On; p.corner = c; p.keys = OnKeys(c, p); }
        crew[n].role = FJack; crew[n++].keys = null; crew[n].role = RJack; crew[n++].keys = null;
        for (int s = -1; s <= 1; s += 2) { Person p = crew[n++]; p.role = Helper; p.side = s; p.keys = HelperKeys(s, p); }
        { Person p = crew[n++]; p.role = Rel; float ar = Mathf.Atan2(-2.0f, -(frontEnd + 0.8f)) * Mathf.Rad2Deg; p.keys = Still(p, 2.0f, frontEnd + 0.8f, ar, Stand, (1.95f, Stand), (2.14f, Point), (2.6f, Point), (2.95f, Fist)); }
        { Person p = crew[n++]; p.role = Ext; p.keys = Still(p, 2.5f, rearEnd - 0.4f, -80f, Rest); }
        float gd = garageDist;
        { Person p = crew[n++]; p.role = Eng; p.keys = Still(p, gd + 1.0f, -1.0f, -105f, Rest, (2.6f, Rest), (2.9f, Fist)); }
        { Person p = crew[n++]; p.role = Eng; p.keys = Still(p, gd + 0.9f, 1.2f, -78f, Rest); }
        { Person p = crew[n++]; p.role = Watch; p.side = 0; p.keys = Still(p, gd + 0.5f, -2.4f, -70f, Crossed, (2.55f, Crossed), (2.85f, Cheer)); }
        { Person p = crew[n++]; p.role = Watch; p.side = 1; p.keys = Still(p, gd + 0.5f, 2.4f, -112f, Hips, (2.6f, Hips), (2.9f, Fist)); }
        foreach (var p in crew)
        {
            if (p.keys != null) Tangents(p.keys);
            BuildPhase(p);
            if (p.role == Off) { var k = Eval(p.keys, TDrop); var yq = Quaternion.Euler(0, KeyYaw(k.a), 0); p.dropSpot = new Vector3(gs * k.u, WheelR, k.f) + yq * new Vector3(0, 0, 0.5f); p.dropAx = yq * Vector3.forward; }
        }
        ppl.Recolour(); props.Recolour();
    }

    // the wheel hubs of a car model (corner order 0 FL, 1 FR, 2 RL, 3 RR of the model) in its own frame (x right, z forward)
    static int Hubs(CarModel m, Transform[] wr, Vector3[] loc)
    {
        if (m == null) return 0;
        wr[0] = m.frontPivots[0]; wr[1] = m.frontPivots[1];
        wr[2] = m.rearSpin[0] != null ? m.rearSpin[0].parent : null; wr[3] = m.rearSpin[1] != null ? m.rearSpin[1].parent : null;
        int got = 0;
        for (int i = 0; i < 4; i++) if (wr[i] != null) { loc[i] = m.transform.InverseTransformPoint(wr[i].position); got++; }
        return got;
    }
    // the wheels the crew takes off (corners: 0 front garage side, 1 front lane side, 2 rear garage side, 3 rear lane side)
    void AcquireWheels(CarModel m)
    {
        RestoreWheels(); curModel = m; wheelsOff = false;
        var wr = new Transform[4]; var loc = new Vector3[4];
        if (Hubs(m, wr, loc) < 4) return;
        for (int i = 0; i < 4; i++) { bool garage = gs * loc[i].x > 0f; int c = (i < 2 ? 0 : 2) + (garage ? 0 : 1); wheelRoots[c] = wr[i]; }
        for (int c = 0; c < 4; c++) if (wheelRoots[c] == null) { for (int k = 0; k < 4; k++) wheelRoots[k] = null; return; }
        for (int c = 0; c < 4; c++) wheelWas[c] = wheelRoots[c].gameObject.activeSelf;
    }
    void RestoreWheels()
    {
        for (int c = 0; c < 4; c++) { if (wheelRoots[c] != null && wheelsOff) wheelRoots[c].gameObject.SetActive(wheelWas[c]); wheelRoots[c] = null; }
        wheelsOff = false;
    }

    void MeasureWheels(CarModel m)
    {
        var wr = new Transform[4]; var loc = new Vector3[4];
        bool all = Hubs(m, wr, loc) == 4;
        // crew frame: x = towards the garage, z = along the car (the car stands on the box centre, along the bay)
        float fF = 1.7f, fR = -1.7f, half = 0.8f;
        if (all) { fF = (loc[0].z + loc[1].z) * 0.5f; fR = (loc[2].z + loc[3].z) * 0.5f; half = (Mathf.Abs(loc[0].x) + Mathf.Abs(loc[1].x) + Mathf.Abs(loc[2].x) + Mathf.Abs(loc[3].x)) * 0.25f; }
        if (fF < fR || Mathf.Abs(fF - fR) < 2f || half < 0.5f || half > 1.2f) { fF = 1.7f; fR = -1.7f; half = 0.8f; }
        for (int c = 0; c < 4; c++) { wu[c] = half; wf[c] = c < 2 ? fF : fR; }
        frontEnd = fF + 1.2f; rearEnd = fR - 0.95f;
        AcquireWheels(m);
    }

    // ---------------------------------------------------------------- the choreography (keys per role; times in s, 0 = car stopped)
    float KeyYaw(float a) => Mathf.Atan2(gs * Mathf.Sin(a * Mathf.Deg2Rad), Mathf.Cos(a * Mathf.Deg2Rad)) * Mathf.Rad2Deg;
    static Pose After(Person p) => p.cheer == 2 ? Cheer : p.cheer == 1 ? Fist : Rest; // after the release: some cheer, some pump a fist

    Key[] GunKeys(int c, Person p)
    {
        float su = (c & 1) == 0 ? 1f : -1f, sf = c < 2 ? 1f : -1f, w = wu[c], f = wf[c], a = -90f * su;
        float U(float d) => su * (w + d);
        return new[]
        {
            K(A, U(2.0f), f + sf * 0.55f, a, Ready), K(-0.62f, U(2.0f), f + sf * 0.55f, a, Ready),
            K(-0.46f, U(2.12f), f + sf * 0.6f, a, ReadyBack, 0f, 2),                       // weight back...
            K(-0.12f, U(0.8f), f + sf * 0.4f, a, Kneel, 0.75f, 1),                          // ...and down onto one knee at the wheel
            K(0.04f, U(0.8f), f + sf * 0.4f, a, Kneel, 1f), K(Hold, U(0.8f), f + sf * 0.4f, a, Kneel, 1f),
            K(1.72f, U(1.25f), f + sf * 0.5f, a, Signal, 0f, 1), K(2.05f, U(1.3f), f + sf * 0.5f, a, Signal), // up, arm raised: done
            K(2.4f, U(1.95f), f + sf * 0.75f, a, Stand), K(2.8f, U(2.0f), f + sf * 0.8f, a, After(p), 0f, 1), K(3.6f, U(2.0f), f + sf * 0.8f, a, Rest),
        };
    }
    Key[] OffKeys(int c, Person p)
    {
        float su = (c & 1) == 0 ? 1f : -1f, sf = c < 2 ? 1f : -1f, w = wu[c], f = wf[c], a = -90f * su, ad = 90f * su;
        float U(float d) => su * (w + d);
        return new[]
        {
            K(A, U(1.7f), f - sf * 0.85f, a, Ready), K(0.1f, U(1.7f), f - sf * 0.85f, a, Ready),
            K(0.24f, U(1.8f), f - sf * 0.9f, a, ReadyBack, 0f, 2),
            K(0.5f, U(0.58f), f - sf * 0.12f, a, Low, 0f, 1), K(TOff, U(0.55f), f - sf * 0.06f, a, Low),
            K(0.88f, U(1.25f), f - sf * 0.15f, a, Carry), K(1.05f, U(1.9f), f - sf * 0.45f, ad, Carry), K(1.36f, U(2.85f), f - sf * 1.45f, ad, Carry),
            K(1.46f, U(2.9f), f - sf * 1.5f, ad, Low, 0f, 1), K(TDrop, U(2.9f), f - sf * 1.5f, ad, Low),
            K(1.9f, U(2.95f), f - sf * 1.5f, ad, Stand), K(2.3f, U(3.0f), f - sf * 1.5f, a, Rest),
            K(2.85f, U(3.0f), f - sf * 1.5f, a, After(p), 0f, 1), K(3.6f, U(3.0f), f - sf * 1.5f, a, Rest),
        };
    }
    Key[] OnKeys(int c, Person p)
    {
        float su = (c & 1) == 0 ? 1f : -1f, sf = c < 2 ? 1f : -1f, w = wu[c], f = wf[c], a = -90f * su;
        float U(float d) => su * (w + d);
        return new[]
        {
            K(A, U(3.0f), f + sf * 1.4f, a, Carry, 1f), K(0.26f, U(3.0f), f + sf * 1.4f, a, Carry, 1f),
            K(0.62f, U(1.6f), f + sf * 0.4f, a, Carry, 1f), K(0.88f, U(0.62f), f + sf * 0.05f, a, Low, 1f),
            K(TOn, U(0.56f), f, a, Low, 1f), K(1.24f, U(1.2f), f + sf * 0.3f, a, Ready, 0f, 1), K(1.6f, U(1.75f), f + sf * 0.8f, a, Rest),
            K(2.85f, U(1.75f), f + sf * 0.8f, a, After(p), 0f, 1), K(3.6f, U(1.75f), f + sf * 0.8f, a, Rest),
        };
    }
    // the side stabilisers: hands on the car's sides at the cockpit while it is in the air
    Key[] HelperKeys(int s, Person p)
    {
        float a = -90f * s;
        return new[]
        {
            K(A, s * 2.4f, -0.3f, a, Ready), K(-0.5f, s * 2.4f, -0.3f, a, Ready),
            K(-0.05f, s * 1.25f, -0.25f, a, HoldCar, 0.8f, 1), K(0.1f, s * 1.25f, -0.25f, a, HoldCar, 1f), K(2.05f, s * 1.25f, -0.25f, a, HoldCar, 1f),
            K(2.3f, s * 2.0f, -0.45f, a, Stand), K(2.8f, s * 2.0f, -0.45f, a, After(p), 0f, 1), K(3.6f, s * 2.0f, -0.45f, a, Rest),
        };
    }
    // somebody standing in one place: pose changes at given times
    static Key[] Still(Person p, float u, float f, float a, in Pose p0, params (float t, Pose q)[] ch)
    {
        var ks = new List<Key> { K(A, u, f, a, p0) };
        foreach (var c in ch) ks.Add(K(c.t, u, f, a, c.q, 0f, c.q.sl > 100f || c.q.sr > 90f ? 1 : 0));
        ks.Add(K(4.2f, u, f, a, ks[ks.Count - 1].p));
        return ks.ToArray();
    }

    // Hermite tangents: pass-through keys keep their speed, holds and reversals stop
    static void Tangents(Key[] ks)
    {
        for (int i = 0; i < ks.Length; i++)
        {
            ks[i].mu = ks[i].mf = 0f;
            if (i == 0 || i == ks.Length - 1) continue;
            float du0 = ks[i].u - ks[i - 1].u, df0 = ks[i].f - ks[i - 1].f, du1 = ks[i + 1].u - ks[i].u, df1 = ks[i + 1].f - ks[i].f;
            if (du0 * du0 + df0 * df0 < 0.0025f || du1 * du1 + df1 * df1 < 0.0025f || du0 * du1 + df0 * df1 <= 0f) continue;
            float dt = ks[i + 1].t - ks[i - 1].t;
            ks[i].mu = 0.85f * (ks[i + 1].u - ks[i - 1].u) / dt; ks[i].mf = 0.85f * (ks[i + 1].f - ks[i - 1].f) / dt;
        }
    }
    static float Herm(float p0, float m0, float p1, float m1, float x, float h)
    {
        float x2 = x * x, x3 = x2 * x;
        return (2f * x3 - 3f * x2 + 1f) * p0 + (x3 - 2f * x2 + x) * h * m0 + (-2f * x3 + 3f * x2) * p1 + (x3 - x2) * h * m1;
    }
    static float Smooth(float x) => x <= 0f ? 0f : x >= 1f ? 1f : x * x * (3f - 2f * x);
    static float Smoother(float x) => x <= 0f ? 0f : x >= 1f ? 1f : x * x * x * (x * (6f * x - 15f) + 10f);
    static float BackOut(float x) { x = Mathf.Clamp01(x) - 1f; return 1f + 1.9f * x * x * x + 0.9f * x * x; } // a little past, then settle
    static float BackIn(float x) { x = Mathf.Clamp01(x); return 1.9f * x * x * x - 0.9f * x * x; }           // a little back first
    static int Seg(Key[] ks, float s)
    {
        int n = ks.Length; if (s <= ks[0].t) return -1; if (s >= ks[n - 1].t) return n - 1;
        for (int i = 0; i + 1 < n; i++) if (s < ks[i + 1].t) return i;
        return n - 1;
    }
    static Key Eval(Key[] ks, float s)
    {
        int i = Seg(ks, s);
        if (i < 0 || i >= ks.Length - 1) return ks[i < 0 ? 0 : ks.Length - 1];
        Key a = ks[i], b = ks[i + 1]; float h = b.t - a.t, x = (s - a.t) / h, sm = Smooth(x);
        float e = b.e == 1 ? BackOut(x) : b.e == 2 ? BackIn(x) : Smoother(x);
        return new Key { t = s, u = Herm(a.u, a.mu, b.u, b.mu, x, h), f = Herm(a.f, a.mf, b.f, b.mf, x, h), a = Mathf.LerpAngle(a.a, b.a, sm), w = Mathf.Lerp(a.w, b.w, sm), p = Pose.Lerp(a.p, b.p, e) };
    }
    static Vector2 EvalPos(Key[] ks, float s)
    {
        int i = Seg(ks, s);
        if (i < 0 || i >= ks.Length - 1) { var k = ks[i < 0 ? 0 : ks.Length - 1]; return new Vector2(k.u, k.f); }
        Key a = ks[i], b = ks[i + 1]; float h = b.t - a.t, x = (s - a.t) / h;
        return new Vector2(Herm(a.u, a.mu, b.u, b.mu, x, h), Herm(a.f, a.mf, b.f, b.mf, x, h));
    }
    // staggered timing: each person starts the approach and finishes the work a little apart (not during the service itself)
    static float SEff(Person p, float s) => s - p.delay * (1f - Smooth((s + 0.45f) / 0.25f) + Smooth((s - 1.62f) / 0.25f));

    static float Lift(float s) => LiftH * (Smooth((s - 0.15f) / 0.3f) - Smooth((s - 1.7f) / 0.25f));
    Vector3 WheelPos(int c, float lift) { float su = (c & 1) == 0 ? 1f : -1f; return new Vector3(gs * su * wu[c], WheelR + lift, wf[c]); }

    // the jack men follow their jack (computed, not keyed): in under the car end, the handle down as the car goes up, swung
    // clear after the drop (front: diagonally to the garage side, out of the car's way; rear: straight back)
    void Jack(Person p, float s, out Vector3 ground, out float yaw, out Vector3 tip, out Vector3 pivot, out Vector3 hd, out float hold)
    {
        float lf = Lift(s) / LiftH, ret = Smooth((s - 1.97f) / 0.4f);
        Vector3 d;
        if (p.role == FJack)
        {
            float slide = 1f - Smooth((s + 0.5f) / 0.42f);
            pivot = new Vector3(gs * 1.7f * ret, 0, frontEnd - 0.35f + 1.6f * slide + 1.1f * ret);
            d = new Vector3(gs * 0.8f * ret, 0, 1f).normalized;
        }
        else
        {
            float eng = Smooth((s + 0.2f) / 0.38f);
            pivot = Vector3.Lerp(new Vector3(gs * 1.5f, 0, rearEnd - 1.1f), new Vector3(0, 0, rearEnd + 0.35f), eng) + new Vector3(0, 0, -1.3f * ret);
            d = Vector3.Lerp(new Vector3(gs * 0.7f, 0, -0.7f), new Vector3(0, 0, -1f), eng).normalized;
        }
        hold = 1f - Smooth((s - 2.3f) / 0.25f);
        float ty = Mathf.Lerp(Mathf.Lerp(0.95f, 0.62f, lf), 0.16f, 1f - hold);
        float th = Mathf.Asin(Mathf.Clamp((ty - 0.12f) / 1.1f, -0.9f, 0.9f));
        tip = pivot + d * (1.1f * Mathf.Cos(th)) + Vector3.up * ty;
        ground = new Vector3(tip.x, 0, tip.z) + d * (0.32f + 0.4f * (1f - hold));
        yaw = Mathf.Atan2(-d.x, -d.z) * Mathf.Rad2Deg; hd = d;
    }

    Key KeyAt(Person p, float se)
    {
        if (p.role == FJack || p.role == RJack)
        {
            Jack(p, se, out Vector3 g, out float yaw, out _, out _, out _, out float hold);
            Pose q = Pose.Lerp(Brace0, Brace, Lift(se) / LiftH);
            float ch = Smooth((se - 2.45f) / 0.35f); if (ch > 0f) q = Pose.Lerp(q, After(p), p.cheer == 0 ? ch : BackOut(Mathf.Clamp01((se - 2.45f) / 0.35f)));
            return new Key { t = se, u = g.x, f = g.z, a = yaw, w = hold, p = q };
        }
        var k = Eval(p.keys, se); k.u *= gs; k.a = KeyYaw(k.a); return k;
    }
    Vector2 PosAt(Person p, float se)
    {
        if (p.role == FJack || p.role == RJack) { Jack(p, se, out Vector3 g, out _, out _, out _, out _, out _); return new Vector2(g.x, g.z); }
        var v = EvalPos(p.keys, se); v.x *= gs; return v;
    }
    // the jog phase from the distance covered (a pure function of the stop clock)
    void BuildPhase(Person p)
    {
        int n = Mathf.CeilToInt((T1 - T0) / TabDt) + 1; if (p.phase == null || p.phase.Length != n) p.phase = new float[n];
        Vector2 prev = PosAt(p, SEff(p, T0)); float ph = p.seed * 0.7f; p.phase[0] = ph;
        for (int i = 1; i < n; i++)
        {
            Vector2 cur = PosAt(p, SEff(p, T0 + i * TabDt)); float d = (cur - prev).magnitude, v = d / TabDt;
            ph += d / Mathf.Lerp(1.1f, 2.2f, Mathf.Clamp01((v - 1f) / 3f)) * 2f * Mathf.PI; // stride pair: 1.1 m walking .. 2.2 m running
            p.phase[i] = ph; prev = cur;
        }
    }
    static float Phase(Person p, float s)
    {
        float x = (s - T0) / TabDt; int i = Mathf.Clamp((int)x, 0, p.phase.Length - 2);
        return Mathf.Lerp(p.phase[i], p.phase[i + 1], Mathf.Clamp01(x - i));
    }
    // a jog / walk cycle (back = back-pedalling)
    static Pose Loco(float ph, float v, bool back)
    {
        float sn = Mathf.Sin(ph), cs = Mathf.Cos(ph), run = Mathf.Clamp01((v - 1.2f) / 2f), dir = back ? -1f : 1f;
        float amp = Mathf.Min(0.36f, 0.14f + 0.08f * v) * (back ? 0.6f : 1f), lift = 0.05f + 0.1f * run, sw = 20f + 22f * run;
        var q = Stand;
        q.fl = amp * sn * dir; q.fr = -amp * sn * dir;
        q.yl = lift * Mathf.Max(0f, cs * dir); q.yr = lift * Mathf.Max(0f, -cs * dir);
        q.tl = 20f * Mathf.Max(0f, -sn * dir) * run; q.tr = 20f * Mathf.Max(0f, sn * dir) * run;
        q.h = 0.915f - 0.04f * run + (0.012f + 0.02f * run) * Mathf.Cos(2f * ph);
        q.lean = back ? -2f : 5f + 9f * run; q.pitch = 3f * run; q.twist = 7f * sn * dir; q.roll = 2f * cs;
        q.sl = -sw * sn * dir; q.sr = sw * sn * dir; q.el = q.er = 25f + 55f * run; q.al = q.ar = 10f;
        return q;
    }

    // what a person looks at (crew space)
    Vector3 LookAt(Person p, float s, float lift)
    {
        if (st != St.Run || s < -0.15f)
        {
            float d = st == St.Run ? Mathf.Min(0f, s) * 7f : -14f; // the car coming in from behind, out of the fast lane
            return new Vector3(-gs * 3f * Mathf.Clamp01(-d / 12f), 0.75f, d - 1f);
        }
        if (s > Release + 0.1f) { float x = s - Release; return new Vector3(-gs * Mathf.Min(5f, 1.8f * x * x), 0.7f, frontEnd + 1.5f + 5f * x * x); } // and leaving
        switch (p.role)
        {
            case Gun: case Off: case On: return WheelPos(p.corner, lift);
            case FJack: case RJack: return p.pivot + Vector3.up * 0.3f;
            case Rel: return s > 1.85f ? new Vector3(0, 0.9f, 0.2f) : new Vector3(gs * 0.4f * Mathf.Sin(s * 3.1f), 0.45f, 1.7f * Mathf.Sin(s * 2.3f + 0.6f));
            case Helper: return new Vector3(0, 0.9f, s > 1.9f ? frontEnd + 1.5f : 0.1f);
            default: return new Vector3(0, 0.75f, 0);
        }
    }

    // ---------------------------------------------------------------- per frame
    void Update()
    {
        onTick?.Invoke();
        if (st == St.Off) return;
        var cam = Camera.main;
        bool vis = cam == null || (cam.transform.position - centre).sqrMagnitude < 150f * 150f; // animated only near the camera
        if (st == St.Run)
        {
            float sr = clock != null ? clock() : curS;
            float s = sr < Hold ? sr : sr < Hold + extra ? Hold : sr - extra;
            curS = s;
            if (s > Release + Depart) { Stop(); return; }
            SetVisible(vis);
            if (vis) ApplyFrame(s);
        }
        else // waiting
        {
            SetVisible(vis);
            if (vis) ApplyFrame(A);
        }
    }

    // after the game has placed the cars: the real car on the jacks, its wheels off while the crew changes them
    void LateUpdate()
    {
        if (st != St.Run || target == null) return;
        var m = target(); if (m == null) return;
        if (m != curModel) AcquireWheels(m); // the model changed (level of detail): take its wheels
        float s = curS, lift = Lift(s);
        if (lift > 0f) m.transform.position += Vector3.up * lift;
        bool off = s >= TOff && s < TOn;
        if (off != wheelsOff && wheelRoots[0] != null)
        {
            wheelsOff = off;
            for (int c = 0; c < 4; c++) if (wheelRoots[c] != null) wheelRoots[c].gameObject.SetActive(off ? false : wheelWas[c]);
        }
        if (s >= TOn && !newStripe) { newStripe = true; m.SetStripe(newC); }
    }

    void SetVisible(bool v) { visible = v; ppl.Show(v); props.Show(v); }

    void ApplyFrame(float s)
    {
        float lift = st == St.Run ? Lift(s) : 0f, rt = Time.time;
        for (int i = 0; i < crew.Length; i++) Animate(crew[i], s, lift, rt);
        PlaceProps(s, lift);
        ppl.Flush(centre); props.Flush(centre);
    }

    void Animate(Person p, float s, float lift, float rt)
    {
        float se = SEff(p, s);
        Key k = KeyAt(p, se);
        // speed and heading of the move (from the path itself, so a frozen clock still shows the stride)
        Vector2 pa = PosAt(p, se - 0.04f), pb = PosAt(p, se + 0.04f), vel = (pb - pa) / 0.08f;
        float speed = vel.magnitude;
        float loco = Mathf.Clamp01((speed - 0.3f) / 0.9f) * Mathf.Clamp01((k.p.h - 0.6f) / 0.25f); // no jogging out of a kneel / squat
        float yaw = k.a; bool back = false;
        if (speed > 0.25f)
        {
            float my = Mathf.Atan2(vel.x, vel.y) * Mathf.Rad2Deg, d = Mathf.DeltaAngle(k.a, my);
            if (Mathf.Abs(d) > 115f) back = true; else yaw = k.a + d * loco;
        }
        Pose q = k.p;
        if (loco > 0f) q = Pose.Lerp(q, Loco(Phase(p, s), speed, back), loco);
        // idle life: breathing, weight shifts, a slow twist
        float sd = p.seed * 1.37f, idle = 1f - loco, stand = Mathf.Clamp01((q.h - 0.8f) / 0.1f) * idle;
        q.lean += 1.1f * Mathf.Sin(rt * 1.9f + sd) * idle;
        q.sx += 0.014f * Mathf.Sin(rt * 0.53f + sd) * stand; q.roll += 1.8f * Mathf.Sin(rt * 0.53f + sd) * stand; q.twist += 4f * Mathf.Sin(rt * 0.31f + sd * 2.1f) * stand;
        // celebrating: pump the raised arms
        if (st == St.Run && s > Release - 0.1f)
        {
            float pm = Mathf.Sin(rt * 8.5f + sd);
            if (q.sl > 110f) { q.sl += 12f * pm; q.el += 22f * pm; }
            if (q.sr > 110f) { q.sr += 12f * pm; q.er += 22f * pm; }
        }
        Vector3 g = new Vector3(k.u, 0, k.f); Quaternion yq = Quaternion.Euler(0, yaw, 0);
        p.ground = g; p.yaw = yaw;
        if (p.role == FJack || p.role == RJack) Jack(p, se, out _, out _, out p.tip, out p.pivot, out p.hdir, out _);
        // head: look at what matters now, with a little wandering when idle
        {
            Vector3 lt = LookAt(p, s, lift) - (g + Vector3.up * (1.62f * p.scale * q.h / 0.935f));
            float wy = Mathf.Atan2(lt.x, lt.z) * Mathf.Rad2Deg, rel = Mathf.Clamp(Mathf.DeltaAngle(yaw + q.twist, wy), -75f, 75f);
            float dn = Mathf.Atan2(-lt.y, new Vector2(lt.x, lt.z).magnitude) * Mathf.Rad2Deg, lw = 0.8f;
            q.hy = Mathf.Lerp(q.hy, rel, lw) + 22f * (Mathf.PerlinNoise(rt * 0.27f, p.seed * 0.71f) - 0.5f) * (0.3f + stand);
            q.hp = Mathf.Lerp(q.hp, Mathf.Clamp(dn - 0.3f * q.lean, -35f, 45f), lw * 0.8f);
        }
        // hands (root space)
        Matrix4x4 R = Matrix4x4.TRS(g, yq, Vector3.one * p.scale), inv = R.inverse;
        Vector3 Lo(Vector3 x) => inv.MultiplyPoint3x4(x);
        Vector3 tl = Vector3.zero, tr = Vector3.zero, rx = yq * Vector3.right, fw = yq * Vector3.forward; float wl = 0f, wr = 0f;
        switch (p.role)
        {
            case Gun:
            {
                // both hands on the gun at the nut; withdrawn while the wheels change; buzzing at nut off / on
                float su = (p.corner & 1) == 0 ? 1f : -1f; Vector3 outv = new Vector3(gs * su, 0, 0), nut = WheelPos(p.corner, lift) + outv * 0.2f;
                float wd = Smooth((s - (TOff - 0.08f)) / 0.08f) * (1f - Smooth((s - (TOn - 0.12f)) / 0.1f));
                float buzz = st == St.Run && ((s > 0.06f && s < 0.32f) || (s > 1.1f && s < 1.42f)) ? 0.007f * Mathf.Sin(rt * 230f) : 0f;
                Vector3 g0 = nut + outv * (0.3f * wd) + Vector3.up * (0.07f * wd + buzz);
                tr = Lo(g0 + outv * 0.33f - Vector3.up * 0.10f); tl = Lo(g0 + outv * 0.17f + Vector3.up * 0.05f); wl = wr = k.w;
                p.gunW = k.w; p.gunAt = g0; break;
            }
            case Off:
            {
                Vector3 wp = WheelPos(p.corner, lift), car = Vector3.right, carry = g + yq * new Vector3(0, 1.02f, 0.36f) * p.scale;
                if (Vector3.Dot(car, fw) < 0f) car = -car;
                float k1 = Smooth((s - TOff) / 0.25f), kd = Smooth((s - (TDrop - 0.12f)) / 0.12f);
                p.tyre = s < TOff ? wp : Vector3.Lerp(Vector3.Lerp(wp, carry, k1), p.dropSpot, kd);
                p.tyreAx = kd > 0f ? Vector3.Slerp(fw, p.dropAx, kd) : Vector3.Slerp(car, fw, k1);
                float w = Smooth((s - 0.42f) / 0.15f) * (1f - Smooth((s - 1.47f) / 0.1f));
                tl = Lo(p.tyre - rx * 0.3f + Vector3.up * 0.05f); tr = Lo(p.tyre + rx * 0.3f + Vector3.up * 0.05f); wl = wr = w; break;
            }
            case On:
            {
                Vector3 wp = WheelPos(p.corner, lift), car = Vector3.right, carry = g + yq * new Vector3(0, 1.02f, 0.36f) * p.scale;
                if (Vector3.Dot(car, fw) < 0f) car = -car;
                float k1 = Smooth((s - 0.62f) / 0.36f);
                p.tyre = Vector3.Lerp(carry, wp, k1); p.tyreAx = Vector3.Slerp(fw, car, k1);
                float w = k.w * (1f - Smooth((s - TOn) / 0.1f));
                tl = Lo(p.tyre - rx * 0.3f + Vector3.up * 0.05f); tr = Lo(p.tyre + rx * 0.3f + Vector3.up * 0.05f); wl = wr = w; break;
            }
            case FJack: case RJack:
                tl = Lo(p.tip - rx * 0.13f); tr = Lo(p.tip + rx * 0.13f); wl = wr = k.w; break;
            case Helper:
            {
                float sx = gs * p.side; Vector3 h1 = new Vector3(sx * 0.64f, 0.62f + lift, -0.05f), h2 = new Vector3(sx * 0.64f, 0.62f + lift, -0.45f);
                if (Vector3.Dot(h1 - h2, rx) > 0f) { var x = h1; h1 = h2; h2 = x; }
                tl = Lo(h1); tr = Lo(h2); wl = wr = k.w; break;
            }
            case Rel: tl = new Vector3(-0.04f, 1.12f, 0.30f); wl = 1f; break; // the release handset at the chest
            case Ext: tr = new Vector3(0.22f, 0.88f, 0.08f); wr = 1f; break;  // the extinguisher at the side
            case Eng:
            {
                float ear = Mathf.Clamp01(Mathf.Sin(rt * 0.23f + p.seed) * 2.5f - 1.0f) * (q.sl < 100f ? 1f : 0f); // a hand to the headset now and then
                tr = new Vector3(0.12f, 1.64f, 0.02f); wr = ear; break;
            }
            case Watch:
            {
                float wv = Mathf.Clamp01(1f - (q.sl > 100f || q.sr > 100f ? 1f : 0f) - Smooth((s - 2.5f) / 0.3f) * (st == St.Run ? 1f : 0f));
                if (p.side == 0) { tl = new Vector3(0.10f, 1.22f, 0.18f); tr = new Vector3(-0.10f, 1.19f, 0.20f); }
                else { tl = new Vector3(-0.2f, 0.99f, 0f); tr = new Vector3(0.2f, 0.99f, 0f); }
                wl = wr = wv; break;
            }
        }
        PoseBody(ppl, p.part0, F * R, q, tl, wl, tr, wr, out Vector3 hl, out Vector3 hr, out Quaternion fr);
        p.handL = R.MultiplyPoint3x4(hl); p.handR = R.MultiplyPoint3x4(hr); p.foreR = yq * fr;
    }

    void PlaceProps(float s, float lift)
    {
        var F2 = F;
        for (int i = 0; i < crew.Length; i++)
        {
            var p = crew[i];
            switch (p.role)
            {
                case Gun:
                {
                    // the gun in the right hand: socket to the nut while engaged, else pointing where the hand points
                    float su = (p.corner & 1) == 0 ? 1f : -1f; Vector3 toNut = new Vector3(-gs * su, 0, 0), hf = p.foreR * Vector3.forward;
                    Vector3 dir = Vector3.Slerp(hf.normalized, toNut, Mathf.Clamp01(p.gunW)); if (dir.sqrMagnitude < 1e-6f) dir = toNut;
                    Quaternion gq = Quaternion.LookRotation(dir.normalized, Vector3.up);
                    Vector3 gp = p.handR + gq * new Vector3(0, 0.10f, 0.01f);
                    props.Set(PGun + p.corner, F2 * Matrix4x4.TRS(gp, gq, Vector3.one));
                    // its air hose up to the reel on the gantry (bay frame (+-2.0 / +-1.4, 3.39, 0.6))
                    Vector3 h0 = gp + gq * new Vector3(0, -0.16f, -0.03f), sf = new Vector3(0, 0, p.corner < 2 ? 1f : -1f);
                    Vector3 h3 = new Vector3(gs * 0.6f, 3.39f, sf.z * (su > 0f ? 2.0f : 1.4f));
                    Vector3 h1 = h0 + Vector3.down * 0.35f + toNut * -0.25f, h2 = h3 + Vector3.down * 1.4f + (new Vector3(h0.x, 0, h0.z) - new Vector3(h3.x, 0, h3.z)) * 0.35f;
                    Vector3 prev = h0;
                    for (int k = 1; k <= HoseSeg; k++)
                    {
                        float t = k / (float)HoseSeg, u = 1f - t;
                        Vector3 pt = u * u * u * h0 + 3f * u * u * t * h1 + 3f * u * t * t * h2 + t * t * t * h3, d = pt - prev;
                        props.Set(PHose + p.corner * HoseSeg + k - 1, F2 * Matrix4x4.TRS(prev, Quaternion.LookRotation(d.sqrMagnitude > 1e-8f ? d : Vector3.up, Mathf.Abs(d.normalized.y) > 0.95f ? Vector3.right : Vector3.up), new Vector3(1f, 1f, d.magnitude)));
                        prev = pt;
                    }
                    break;
                }
                case Off:
                    if (s < TOff) props.Hide(POld + p.corner);
                    else props.Set(POld + p.corner, F2 * Matrix4x4.TRS(p.tyre, Quaternion.FromToRotation(Vector3.right, p.tyreAx), Vector3.one));
                    break;
                case On:
                    if (s >= TOn) props.Hide(PNew + p.corner);
                    else props.Set(PNew + p.corner, F2 * Matrix4x4.TRS(p.tyre, Quaternion.FromToRotation(Vector3.right, p.tyreAx), Vector3.one));
                    break;
                case FJack: case RJack:
                {
                    int j = (p.role == FJack ? 0 : 1) * 3; Quaternion jq = Quaternion.LookRotation(p.hdir, Vector3.up);
                    props.Set(PJack + j, F2 * Matrix4x4.TRS(p.pivot, jq, Vector3.one));
                    props.Set(PJack + j + 1, F2 * Matrix4x4.TRS(p.pivot + Vector3.up * (Lift(s) * (st == St.Run ? 1f : 0f)), jq, Vector3.one));
                    Vector3 hb = p.pivot + Vector3.up * 0.12f, hdv = p.tip - hb;
                    props.Set(PJack + j + 2, F2 * Matrix4x4.TRS(hb, Quaternion.LookRotation(hdv.sqrMagnitude > 1e-6f ? hdv : p.hdir, Vector3.up), new Vector3(1f, 1f, hdv.magnitude / 1.1f)));
                    break;
                }
                case Rel: props.Set(PHand, F2 * Matrix4x4.TRS(p.handL + Vector3.up * 0.02f, Quaternion.Euler(0, p.yaw, 0) * Quaternion.Euler(-30f, 0, 0), Vector3.one)); break;
                case Ext: props.Set(PExt, F2 * Matrix4x4.TRS(p.handR, Quaternion.Euler(0, p.yaw, 0), Vector3.one)); break;
            }
        }
        // the release light hanging from the gantry: red while the crew works, green from the release call
        props.Set(PLight, F2 * Matrix4x4.TRS(new Vector3(gs * 0.6f, 2.95f, 3.3f), Quaternion.identity, Vector3.one));
        bool green = st == St.Run && s >= TGreen && s < Release + 1.2f;
        if (green != lightGreen)
        {
            lightGreen = green;
            props.pal[SLampR] = green ? C(0.15f, 0.02f, 0.02f) : C(1f, 0.08f, 0.06f); props.pal[SLampG] = green ? C(0.1f, 1f, 0.3f) : C(0.04f, 0.12f, 0.06f);
            props.Recolour(PLight); props.Recolour(PHand);
        }
    }
}


// ==================================================================== GameManager glue
public partial class GameManager
{
    PitCrew pitCrew; int[] pitTeam; Livery[] pitLiv; int pitWaitRival = -1;
    PitRun pRun, pCrewRun; PitRun[] rivalCrewRun; double pitStopLen; readonly object playerPitKey = new object();
    static PitLane Lane => Trackside.Lane;
    // the pit lane is driven in races on circuits with a pit building (otherwise the old instant stop at the line)
    bool PitLaneOn => Lane != null && track != null && Lane.track == track && raceLaps > 0 && !playground;
    bool InPitLane => pRun != null;
    CarModel PlayerPitModel() => model;
    CarModel RivalPitModel(int i) => i < rivalModels.Count ? (rivalNear[i] || i >= rivalFar.Count || rivalFar[i] == null ? rivalModels[i] : rivalFar[i]) : null;

    // once per session, after SetupRivals: the crew for this circuit's pit lane (none on a circuit without one, or without a
    // race), the rivals' boxes
    void PitCrewSetup()
    {
        if (pitCrew != null) { Destroy(pitCrew.gameObject); pitCrew = null; }
        pRun = null; pCrewRun = null; pitWaitRival = -1;
        pitTeam = new int[rivals.Count]; pitLiv = new Livery[rivals.Count]; rivalCrewRun = new PitRun[rivals.Count];
        if (Lane != null) Lane.Reset();
        foreach (var d in rivals) { d.pitLane = null; d.pitBay = -1; d.pit = null; }
        if (playground || raceLaps <= 0 || track == null || Trackside.Bays.Count == 0) return;
        var teams = new Dictionary<int, Livery>();
        for (int i = 0; i < rivals.Count; i++)
        {
            var d = rivals[i]; var col = new Color(d.who.r, d.who.g, d.who.b); int ti = TeamIndex(d.who);
            pitTeam[i] = ti; pitLiv[i] = Livery.ForTeam(ti, col); if (!teams.ContainsKey(ti)) teams[ti] = pitLiv[i];
        }
        pitCrew = PitCrew.Create(track.N, Livery.Player[Livery.PlayerIndex], teams);
        if (pitCrew != null) pitCrew.onTick = PitCrewTick;
        if (PitLaneOn) for (int i = 0; i < rivals.Count; i++) { rivals[i].pitLane = Lane; rivals[i].pitBay = pitCrew != null ? pitCrew.TeamBay(pitTeam[i]) : -1; }
    }

    // every frame (from the crew's Update): the crew waits while BOX is armed or a car is coming in, and does the stop on the
    // player's car, or on a rival's within ~150 m of the camera (the player's stop has priority)
    void PitCrewTick()
    {
        if (pitCrew == null || cam == null || rivalCrewRun == null || rivals.Count != rivalCrewRun.Length) return;
        Vector3 cp = cam.transform.position;
        var pliv = Livery.Player[Livery.PlayerIndex];
        bool near = (pitCrew.BayPos(pitCrew.PlayerBay) - cp).sqrMagnitude < 250f * 250f, playerCrew = false;
        if (pRun != null && pRun.bay >= 0 && pRun != pCrewRun && !pRun.Done)
        {
            playerCrew = true;
            var r = pRun;
            if (pitCrew.Busy && !pitCrew.Owns(-1) && r.claimed && r.Clock > -2.5) pitCrew.Stop();
            if (r.claimed && r.Clock >= -1.3)
            { if (pitCrew.Begin(-1, r.bay, pliv, Weather.Stripe(carSet), Weather.Stripe(NextSet), (float)r.extraHold, PlayerPitModel, () => (float)r.Clock)) pCrewRun = r; }
            else pitCrew.Wait(-1, r.bay, pliv, Weather.Stripe(carSet), Weather.Stripe(NextSet), PlayerPitModel);
        }
        else if (pRun == null && boxArmed && near) { playerCrew = true; pitCrew.Wait(-1, pitCrew.PlayerBay, pliv, Weather.Stripe(carSet), Weather.Stripe(NextSet), PlayerPitModel); }
        else pitCrew.CancelWait(-1);
        for (int i = 0; i < rivals.Count; i++)
        {
            var d = rivals[i]; var r = d.pit;
            bool want = r != null && r.bay >= 0 && r != rivalCrewRun[i] && !playerCrew && (pitCrew.BayPos(r.bay) - cp).sqrMagnitude < 150f * 150f && !(pitCrew.Busy && !pitCrew.Owns(i));
            if (!want) { if (pitWaitRival == i) { pitCrew.CancelWait(i); pitWaitRival = -1; } continue; }
            int k = i; System.Func<CarModel> m = () => RivalPitModel(k);
            if (r.claimed && r.Clock >= -1.3 && r.Clock < 0.5)
            {
                if (pitCrew.Begin(i, r.bay, pitLiv[i], Weather.Stripe(d.tyreSet), Weather.Stripe(d.PredictedSet), (float)r.extraHold, m, () => (float)r.Clock)) rivalCrewRun[i] = r;
                if (pitWaitRival == i) pitWaitRival = -1;
            }
            else if (r.Clock < 0 && !pitCrew.Busy && (pitWaitRival < 0 || pitWaitRival == i))
            { pitCrew.Wait(i, r.bay, pitLiv[i], Weather.Stripe(d.tyreSet), Weather.Stripe(d.PredictedSet), m); pitWaitRival = i; }
        }
    }

    // ---------------------------------------------------------------- the player's pit-lane drive
    // (physics step, on track) BOX armed and the car passing the pit entry: the pit lane takes over
    void PlayerPitEntry()
    {
        if (!boxArmed || !PitLaneOn || idx < 0 || prevIdx < 0) return;
        if (pLap >= raceLaps) { boxArmed = false; return; } // the last lap: no stop
        if (pLap < 1 || raceTime < Countdown) return;
        var lane = Lane;
        double u0 = PitLane.RelS(track, prevIdx), u1 = PitLane.RelS(track, idx);
        if (!(u0 < lane.sEntry && u1 >= lane.sEntry && u1 - u0 < 30) || car.reverse || car.vx < 3) return;
        boxArmed = false;
        pRun = new PitRun(lane, pitCrew != null ? pitCrew.PlayerBay : -1, playerPitKey, u1, System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy), playerLat,
                          car.damage.WingsDamaged ? AiDriver.WingChange : 0);
        PitHudEnter();
    }

    // (physics step, in the lane) the car follows the lane: the player's pedals do nothing, the limiter holds 80 km/h
    void PlayerPitStep()
    {
        var r = pRun;
        r.Step(H); r.ApplyTo(car);
        prevIdx = idx < 0 ? r.idx : idx; idx = r.idx; playerLat = r.lateral; surface = 0;
        if (r.TakeService()) PitStop(false); // the new wheels are on: tyres, repairs (GameManager.PitStop)
        if (r.Done)
        {
            r.HandBack(car); pRun = null;   // back on the track at pit-lane speed, the player drives again
            PitHudExit();
        }
    }

    // RaceControl sees no car in the pit lane (no stopped-car incident, no queue place, no overtake while in the lane)
    void PitRcMask()
    {
        if (raceCtl == null) return;
        for (int i = 0; i < rivals.Count && i < raceCtl.cars.Length; i++) if (rivals[i].InPit) raceCtl.cars[i].idx = -1;
        if (pRun != null && rcPlayerSlot >= 0 && rcPlayerSlot < raceCtl.cars.Length) raceCtl.cars[rcPlayerSlot].idx = -1;
    }

    // shot mode: no stop carried over into the next shot
    void PitShotClear()
    {
        if (pRun != null) { pRun.Cancel(); pRun = null; }
        pCrewRun = null;
        if (pitCrew != null) pitCrew.Stop();
    }

    // shot mode (DebugShots pit=1): the player's real car standing in its box, the crew mid-stop at service time s, frozen
    internal bool ShotPit(float s)
    {
        if (pitCrew == null || track == null || !PitLaneOn) return false;
        PitShotClear();
        var r = PitRun.InBox(Lane, pitCrew.PlayerBay, playerPitKey, s);
        pRun = r; pCrewRun = r;
        r.ApplyTo(car); car.vx = car.vy = 0; car.r = 0; car.delta = 0;
        px = car.x; py = car.y; ph = car.heading; pd = car.delta; psi = car.steerIn; acc = 0;
        idx = prevIdx = r.idx; playerLat = r.lateral; offTime = wrongTime = 0; camVel = Vector3.zero;
        return pitCrew.Begin(-1, r.bay, Livery.Player[Livery.PlayerIndex], Weather.Stripe(carSet), Weather.Stripe(NextSet), 0f, PlayerPitModel, () => (float)r.Clock);
    }
}
