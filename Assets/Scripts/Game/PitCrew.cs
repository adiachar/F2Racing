// PitCrew.cs — an animated pit stop with a pit crew. One crew (18 low-poly people, their tools and four tyre pairs) is built
// once per circuit and re-used for every stop; it is only drawn (and only animated) while a stop is on and near the camera.
//
//  * The people are procedural humanoids (~600 vertices, ~260 triangles each, one shared shape): a pelvis, torso, helmet, visor,
//    two-bone arms and legs, posed by rotating the parts (joint angles from a few keyframed poses per role, hands placed by a
//    two-bone IK on the tool or the tyre they hold, a run cycle when they move). No skeleton, no animator.
//  * Whole crew = 3 draw calls: people, props (tyres, wheel guns, jacks, lollipop, extinguisher) and the garage panels. Each batch
//    is one mesh whose vertices are written in world space every frame (a part is a range of vertices; hidden = collapsed).
//  * The stop is a fixed choreography (service ~2.4 s from the car stopping to the release, like a real F1 stop): jacks in and up
//    (car +8 cm), guns on, nuts off, old wheels off and carried away, new wheels carried in and on (compound colour band), guns
//    on, off, jacks down, lollipop up, car away. The stop is real: the car drives the pit lane (PitLane.cs, a PitRun per car) and
//    stops in its box; the crew works ON THE REAL CAR (the player's model or the rival's model in view): its clock is the run's
//    (s to the stop / since the stop), the car is lifted 8 cm (after the game has placed it: LateUpdate), its wheels are hidden
//    while off, and the compound band changes when the new wheels go on. If the stop is longer (wing change), the crew hold
//    position with the wheels on until it is over.
//  * Pit box lines are in Trackside.cs (Trackside.Bays); the team colour panels on the garage fronts are made here.
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
    const float TOff = 0.72f, TOn = 1.06f, TDrop = 1.55f; // old wheels off the car, new wheels on, old wheels put down
    const float WheelR = 0.36f;

    // ---------------------------------------------------------------- palette slots
    const int SOverall = 0, SHelmet = 1, SGlove = 2, SBoot = 3, SAccent = 4, SVisor = 5, SRubber = 6, SRim = 7, SOldBand = 8, SMetal = 9,
              SGun = 10, SRed = 11, SLolli = 12, SYellow = 13, SBlack = 14, SNewBand = 15;

    // ---------------------------------------------------------------- poses
    struct Pose
    {
        public float lean, hl, hr, kl, kr, sl, sr, el, er; // degrees, forward-positive (knee: bend)
        public static Pose Lerp(Pose a, Pose b, float t)
        {
            return new Pose { lean = Mathf.Lerp(a.lean, b.lean, t), hl = Mathf.Lerp(a.hl, b.hl, t), hr = Mathf.Lerp(a.hr, b.hr, t), kl = Mathf.Lerp(a.kl, b.kl, t), kr = Mathf.Lerp(a.kr, b.kr, t),
                              sl = Mathf.Lerp(a.sl, b.sl, t), sr = Mathf.Lerp(a.sr, b.sr, t), el = Mathf.Lerp(a.el, b.el, t), er = Mathf.Lerp(a.er, b.er, t) };
        }
    }
    static Pose PS(float lean, float hl, float hr, float kl, float kr, float sl, float sr, float el, float er) => new Pose { lean = lean, hl = hl, hr = hr, kl = kl, kr = kr, sl = sl, sr = sr, el = el, er = er };
    static readonly Pose Stand = PS(2, 2, 2, 4, 4, 3, 3, 10, 10), Ready = PS(14, 24, 18, 44, 36, 25, 25, 55, 55), Crouch = PS(30, 70, 50, 120, 100, 70, 70, 25, 25),
                         Low = PS(36, 80, 70, 140, 130, 80, 80, 20, 20), Carry = PS(-4, 8, 8, 16, 16, 40, 40, 60, 60), Signal = PS(0, 3, 3, 6, 6, 8, 168, 10, 12);

    struct Key { public float t, u, f, a, w; public Pose p; } // u = sideways towards the garage, f = along the car, a = facing (0 = nose, 90 = garage, deg), w = hands on the tool
    static Key K(float t, float u, float f, float a, Pose p, float w = 0f) => new Key { t = t, u = u, f = f, a = a, p = p, w = w };

    const int Gun = 0, Off = 1, On = 2, FJack = 3, RJack = 4, Helper = 5, Lolli = 6, Ext = 7;
    sealed class Person
    {
        public int role, corner, side, part0;   // part0 = first part of the person in the people batch (16 parts)
        public Key[] keys;
        public Vector3 pos, prevH, tip, dropSpot, lastMove; public float yaw, phase, runAmt, speed; public bool init;
        public Matrix4x4 foreR;
    }
    const int PartsPerPerson = 16;

    // ---------------------------------------------------------------- a batch: many parts in one mesh, vertices written in world space
    sealed class Batch
    {
        public readonly List<Vector3> bv = new List<Vector3>(); readonly List<float> tint = new List<float>(); readonly List<byte> slot = new List<byte>(); readonly List<int> tri = new List<int>();
        readonly List<int> pStart = new List<int>();
        public Color[] pal; Vector3[] bva, wv; int[] ps, pc; bool[] shown; Color32[] cols; public Mesh mesh; public GameObject go; MeshRenderer mr;
        public Batch(int palSize) { pal = new Color[palSize]; for (int i = 0; i < palSize; i++) pal[i] = Color.magenta; }
        public int Parts => pStart.Count;
        public int NewPart() { pStart.Add(bv.Count); return pStart.Count - 1; }

        static float Sh(Vector3 n) => Mathf.Lerp(0.5f, 1f, n.y * 0.5f + 0.5f) * (0.9f + 0.1f * n.z);
        // one triangle / quad, wound to face the outward normal n (Unity: front = Cross(b - a, c - a))
        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 n, int sl, float t)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) < 0f) { var x = b; b = c; c = x; }
            int k = bv.Count; bv.Add(a); bv.Add(b); bv.Add(c);
            for (int i = 0; i < 3; i++) { tint.Add(t); slot.Add((byte)sl); }
            tri.Add(k); tri.Add(k + 1); tri.Add(k + 2);
        }
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, int sl, float t) { Tri(a, b, c, n, sl, t); Tri(a, c, d, n, sl, t); }
        public void WorldQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, int sl) { Quad(a, b, c, d, n, sl, 1f); }

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
        // a body of revolution about local Y: rings (y, r), closed at the bottom
        public void Lathe(Vector3 c, float[] ys, float[] rs, int n, int sl)
        {
            for (int i = 0; i + 1 < ys.Length; i++)
                for (int k = 0; k < n; k++)
                {
                    float a0 = 2f * Mathf.PI * k / n, a1 = 2f * Mathf.PI * (k + 1) / n, am = (a0 + a1) * 0.5f;
                    Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)), nm = new Vector3(Mathf.Cos(am), 0.4f, Mathf.Sin(am)).normalized;
                    float t = Mathf.Lerp(0.62f, 1f, (i + 0.5f) / (ys.Length - 1));
                    Vector3 p00 = c + d0 * rs[i] + Vector3.up * ys[i], p10 = c + d1 * rs[i] + Vector3.up * ys[i], p11 = c + d1 * rs[i + 1] + Vector3.up * ys[i + 1], p01 = c + d0 * rs[i + 1] + Vector3.up * ys[i + 1];
                    if (rs[i + 1] < 0.002f) Tri(p00, p10, p11, nm, sl, t); else Quad(p00, p10, p11, p01, nm, sl, t);
                }
            for (int k = 0; k < n; k++)
            {
                float a0 = 2f * Mathf.PI * k / n, a1 = 2f * Mathf.PI * (k + 1) / n;
                Tri(c + Vector3.up * ys[0], c + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * rs[0] + Vector3.up * ys[0], c + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * rs[0] + Vector3.up * ys[0], Vector3.down, sl, 0.6f);
            }
        }

        public void Finish(Material mat, Transform parent, string name)
        {
            bva = bv.ToArray(); wv = new Vector3[bva.Length]; cols = new Color32[bva.Length]; shown = new bool[Parts]; ps = new int[Parts]; pc = new int[Parts];
            for (int i = 0; i < Parts; i++) { ps[i] = pStart[i]; pc[i] = (i + 1 < Parts ? pStart[i + 1] : bva.Length) - pStart[i]; }
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
            mesh.colors32 = cols;
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
    }

    // ---------------------------------------------------------------- state
    enum St { Off, Wait, Run }
    St st; int owner = -99, bay = -1; float extra, curS = A;
    Batch ppl, props, panels;
    System.Func<CarModel> target; System.Func<float> clock; CarModel curModel;
    Transform[] wheelRoots = new Transform[4]; readonly bool[] wheelWas = new bool[4];
    int[] order; Color[] bayCol, bayAcc;
    Vector3 centre, fwd, side; float gs, garageDist;
    Matrix4x4 F;
    readonly float[] wu = new float[4], wf = new float[4]; float frontEnd, rearEnd;
    readonly Person[] crew = new Person[18];
    Livery liv; Color oldC, newC; bool lolliGreen, wheelsOff, newStripe, visible = true;
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

    // trackN = points on the track; player = the player's livery; teams = rival liveries by team index (for the garage panels)
    public static PitCrew Create(int trackN, Livery player, Dictionary<int, Livery> teams)
    {
        if (Trackside.Bays.Count == 0) return null;
        var go = new GameObject("PitCrew");
        var c = go.AddComponent<PitCrew>(); c.Init(trackN, player, teams);
        return c;
    }

    void Init(int N, Livery player, Dictionary<int, Livery> teams)
    {
        var bays = Trackside.Bays; int n = bays.Count;
        var idx = new List<int>(); for (int i = 0; i < n; i++) idx.Add(i);
        System.Func<int, int> d = i => Mathf.Min(bays[i].idx, N - bays[i].idx);
        idx.Sort((a, b) => d(a).CompareTo(d(b))); order = idx.ToArray();
        bayCol = new Color[n]; bayAcc = new Color[n];
        for (int i = 0; i < n; i++) { bayCol[i] = new Color(0.17f, 0.18f, 0.21f); bayAcc[i] = new Color(0.55f, 0.57f, 0.62f); }
        bayCol[PlayerBay] = player.primary; bayAcc[PlayerBay] = player.accent;
        if (teams != null) foreach (var kv in teams) { int b = TeamBay(kv.Key); if (b != PlayerBay) { bayCol[b] = kv.Value.primary; bayAcc[b] = kv.Value.accent; } }
        var mat = Mat();
        // garage panels: a team colour backdrop on the garage front of every bay, with an accent band along the top and a sill
        panels = new Batch(2 * n);
        for (int b = 0; b < n; b++)
        {
            var bb = bays[b]; panels.pal[2 * b] = bayCol[b]; panels.pal[2 * b + 1] = bayAcc[b];
            float gy = WorldBuilder.H(bb.centre.x, bb.centre.z);
            Vector3 o = bb.centre + bb.side * (bb.garageDist - 0.09f) + Vector3.up * gy, f = bb.fwd * 3.1f, nrm = -bb.side;
            panels.NewPart();
            panels.WorldQuad(o - f + Vector3.up * 0.35f, o + f + Vector3.up * 0.35f, o + f + Vector3.up * 3.4f, o - f + Vector3.up * 3.4f, nrm, 2 * b);
            panels.WorldQuad(o - f + Vector3.up * 3.4f, o + f + Vector3.up * 3.4f, o + f + Vector3.up * 3.75f, o - f + Vector3.up * 3.75f, nrm, 2 * b + 1);
            panels.WorldQuad(o - f + Vector3.up * 0.2f, o + f + Vector3.up * 0.2f, o + f + Vector3.up * 0.35f, o - f + Vector3.up * 0.35f, nrm, 2 * b + 1);
        }
        panels.Finish(mat, transform, "PitPanels");
        for (int p = 0; p < panels.Parts; p++) panels.Set(p, Matrix4x4.identity);
        panels.Flush(bays[PlayerBay].centre); panels.Recolour();
        // people: 18 of one shape
        ppl = new Batch(16);
        for (int i = 0; i < crew.Length; i++) { crew[i] = new Person { part0 = i * PartsPerPerson }; BuildPerson(ppl); }
        ppl.Finish(mat, transform, "PitPeople");
        // props
        props = new Batch(16);
        BuildProps(props); props.Finish(mat, transform, "PitProps");
        ppl.Show(false); props.Show(false);
        for (int i = 0; i < 16; i++) { ppl.pal[i] = props.pal[i] = Color.gray; }
        st = St.Off;
    }

    static void BuildPerson(Batch b)
    {
        var id = Quaternion.identity;
        b.NewPart(); b.Box(new Vector3(0, 0.02f, 0), new Vector3(0.30f, 0.18f, 0.20f), SOverall, id);                      // 0 pelvis
        b.NewPart(); b.Box(new Vector3(0, 0.30f, 0), new Vector3(0.38f, 0.50f, 0.22f), SOverall, id);                       // 1 torso
        b.Box(new Vector3(0, 0.12f, 0), new Vector3(0.39f, 0.06f, 0.23f), SAccent, id);                                      //   belt
        b.NewPart(); b.Lathe(Vector3.zero, new[] { -0.11f, -0.07f, 0.02f, 0.09f, 0.13f, 0.14f }, new[] { 0.075f, 0.11f, 0.125f, 0.105f, 0.05f, 0f }, 8, SHelmet); // 2 helmet
        b.NewPart(); b.Box(new Vector3(0, 0.01f, 0.105f), new Vector3(0.17f, 0.05f, 0.05f), SVisor, id);                      // 3 visor
        for (int s = 0; s < 2; s++) { b.NewPart(); b.Box(new Vector3(0, -0.15f, 0), new Vector3(0.10f, 0.30f, 0.10f), SOverall, id); }   // 4,5 upper arms
        for (int s = 0; s < 2; s++) { b.NewPart(); b.Box(new Vector3(0, -0.14f, 0), new Vector3(0.085f, 0.28f, 0.085f), SOverall, id); } // 6,7 forearms
        for (int s = 0; s < 2; s++) { b.NewPart(); b.Box(new Vector3(0, -0.33f, 0), new Vector3(0.09f, 0.09f, 0.10f), SGlove, id); }      // 8,9 hands
        for (int s = 0; s < 2; s++) { b.NewPart(); b.Box(new Vector3(0, -0.21f, 0), new Vector3(0.15f, 0.42f, 0.16f), SOverall, id); }   // 10,11 thighs
        for (int s = 0; s < 2; s++) { b.NewPart(); b.Box(new Vector3(0, -0.21f, 0), new Vector3(0.12f, 0.42f, 0.13f), SOverall, id); }   // 12,13 shins
        for (int s = 0; s < 2; s++) { b.NewPart(); b.Box(new Vector3(0, -0.04f, 0.06f), new Vector3(0.12f, 0.08f, 0.26f), SBoot, id); }  // 14,15 boots
    }

    const int POld = 0, PNew = 4, PGun = 8, PJackBase = 12, PJackCol = 14, PHandle = 16, PPole = 18, PDisc = 19, PExt = 20;
    static void BuildProps(Batch b)
    {
        var id = Quaternion.identity;
        for (int i = 0; i < 4; i++) { b.NewPart(); b.Cyl(Vector3.zero, id, WheelR, 0.42f, 12, SRubber, SOldBand, SRim, 0.22f); }   // old tyres (compound band of the old set)
        for (int i = 0; i < 4; i++) { b.NewPart(); b.Cyl(Vector3.zero, id, WheelR, 0.42f, 12, SRubber, SNewBand, SRim, 0.22f); }   // new tyres
        for (int i = 0; i < 4; i++)
        {
            b.NewPart(); b.Box(Vector3.zero, new Vector3(0.12f, 0.14f, 0.30f), SGun, id); b.Box(new Vector3(0, -0.11f, -0.06f), new Vector3(0.05f, 0.12f, 0.06f), SBlack, id);
            b.Box(new Vector3(0, 0, 0.18f), new Vector3(0.07f, 0.07f, 0.07f), SMetal, id);                                         // wheel guns
        }
        for (int i = 0; i < 2; i++) { b.NewPart(); b.Box(new Vector3(0, 0.05f, 0), new Vector3(0.30f, 0.10f, 0.34f), SYellow, id); }   // jack bases
        for (int i = 0; i < 2; i++) { b.NewPart(); b.Box(new Vector3(0, 0.5f, 0), new Vector3(0.08f, 1f, 0.08f), SMetal, id); }          // jack columns (scaled in y)
        for (int i = 0; i < 2; i++) { b.NewPart(); b.Box(new Vector3(0, 0, 0.55f), new Vector3(0.04f, 0.04f, 1.1f), SMetal, id); b.Box(new Vector3(0, 0, 1.1f), new Vector3(0.34f, 0.04f, 0.04f), SMetal, id); } // handles
        b.NewPart(); b.Box(new Vector3(0, 0.875f, 0), new Vector3(0.03f, 1.75f, 0.03f), SMetal, id);                                  // lollipop pole
        b.NewPart(); b.Cyl(new Vector3(0, 1.78f, 0), Quaternion.Euler(0, 90, 0), 0.27f, 0.03f, 12, SMetal, SLolli, SLolli, 0.02f);     // lollipop board
        b.NewPart(); b.Cyl(Vector3.zero, Quaternion.Euler(0, 0, 90), 0.08f, 0.45f, 8, SRed, SMetal, SMetal, 0.03f);                    // extinguisher
    }

    void OnDestroy() { RestoreWheels(); }

    // ---------------------------------------------------------------- public control
    // crew standing ready in a bay (the player armed BOX, or a car is coming in); car = the car's model, if known (its wheel
    // positions place the crew)
    public void Wait(int who, int bayIdx, Livery l, Color oldTyre, Color newTyre, System.Func<CarModel> car = null)
    {
        if (st == St.Run) return;
        if (st == St.Wait && owner == who && bay == bayIdx) return;
        Prepare(who, bayIdx, l, oldTyre, newTyre, car); st = St.Wait; extra = 0; curS = A; ApplyFrame(A, 0f);
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
        owner = who; bay = bayIdx; liv = l; oldC = oldTyre; newC = newTyre; lolliGreen = false; wheelsOff = false; newStripe = false;
        var bb = Trackside.Bays[bayIdx]; fwd = bb.fwd; side = bb.side; garageDist = bb.garageDist;
        float gy = WorldBuilder.H(bb.centre.x, bb.centre.z); centre = new Vector3(bb.centre.x, gy, bb.centre.z);
        Vector3 right = Vector3.Cross(Vector3.up, fwd); gs = Vector3.Dot(side, right) >= 0f ? 1f : -1f;
        F = Matrix4x4.TRS(centre, Quaternion.LookRotation(fwd, Vector3.up), Vector3.one);
        // palettes
        foreach (var bt in new[] { ppl, props })
        {
            var p = bt.pal;
            p[SOverall] = l.primary; p[SHelmet] = l.secondary; p[SGlove] = new Color(0.10f, 0.10f, 0.11f); p[SBoot] = new Color(0.05f, 0.05f, 0.06f); p[SAccent] = l.accent;
            p[SVisor] = new Color(0.03f, 0.04f, 0.06f); p[SRubber] = new Color(0.05f, 0.05f, 0.055f); p[SRim] = new Color(0.62f, 0.64f, 0.68f); p[SOldBand] = oldTyre;
            p[SMetal] = new Color(0.42f, 0.43f, 0.46f); p[SGun] = l.accent; p[SRed] = new Color(0.8f, 0.06f, 0.05f); p[SLolli] = new Color(0.85f, 0.05f, 0.05f);
            p[SYellow] = new Color(0.9f, 0.72f, 0.08f); p[SBlack] = new Color(0.04f, 0.04f, 0.045f); p[SNewBand] = newTyre;
        }
        props.pal[SLolli] = new Color(0.85f, 0.05f, 0.05f);
        // the real car: its wheels, measured in its own frame, place the crew
        target = car; MeasureWheels(car != null ? car() : null);
        // the crew
        int n = 0;
        for (int c = 0; c < 4; c++) { Person p = crew[n++]; p.role = Gun; p.corner = c; p.keys = GunKeys(c); }
        for (int c = 0; c < 4; c++) { Person p = crew[n++]; p.role = Off; p.corner = c; p.keys = OffKeys(c); }
        for (int c = 0; c < 4; c++) { Person p = crew[n++]; p.role = On; p.corner = c; p.keys = OnKeys(c); }
        crew[n].role = FJack; crew[n++].keys = null; crew[n].role = RJack; crew[n++].keys = null;
        for (int s = -1; s <= 1; s += 2) { Person p = crew[n++]; p.role = Helper; p.side = s; p.keys = HelperKeys(s); }
        { Person p = crew[n++]; p.role = Lolli; p.keys = new[] { K(A, 0, frontEnd + 2.6f, 180, Stand), K(3.5f, 0, frontEnd + 2.6f, 180, Stand) }; }
        { Person p = crew[n++]; p.role = Ext; p.keys = new[] { K(A, 2.4f, rearEnd - 0.4f, -90, Ready), K(3.5f, 2.4f, rearEnd - 0.4f, -90, Ready) }; }
        foreach (var p in crew)
        {
            p.init = false; p.runAmt = 0; p.speed = 0; p.phase = 0;
            if (p.role == Off && p.keys != null) { var k = Eval(p.keys, TDrop); p.dropSpot = new Vector3(gs * k.u, WheelR, k.f) + Quaternion.Euler(0, KeyYaw(k.a), 0) * new Vector3(0, 0, 0.45f); }
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

    Key[] GunKeys(int c)
    {
        float su = (c & 1) == 0 ? 1f : -1f, sf = c < 2 ? 1f : -1f, w = wu[c], f = wf[c], a = -90f * su;
        float U(float d) => su * (w + d);
        return new[]
        {
            K(A, U(1.8f), f + sf * 0.6f, a, Ready), K(-0.5f, U(1.8f), f + sf * 0.6f, a, Ready),
            K(-0.1f, U(0.95f), f + sf * 0.4f, a, Low), K(0.1f, U(0.95f), f + sf * 0.4f, a, Low, 1f), K(0.62f, U(0.95f), f + sf * 0.4f, a, Low, 1f),
            K(0.74f, U(1.15f), f + sf * 0.55f, a, Ready), K(0.95f, U(1.15f), f + sf * 0.55f, a, Ready),
            K(1.02f, U(0.95f), f + sf * 0.4f, a, Low), K(1.1f, U(0.95f), f + sf * 0.4f, a, Low, 1f), K(1.55f, U(0.95f), f + sf * 0.4f, a, Low, 1f),
            K(1.68f, U(1.3f), f + sf * 0.5f, a, Signal), K(2.0f, U(1.3f), f + sf * 0.5f, a, Signal), K(2.3f, U(1.6f), f + sf * 0.6f, a, Stand), K(3.5f, U(1.6f), f + sf * 0.6f, a, Stand),
        };
    }
    Key[] OffKeys(int c)
    {
        float su = (c & 1) == 0 ? 1f : -1f, sf = c < 2 ? 1f : -1f, w = wu[c], f = wf[c], a = -90f * su, ad = 90f * su;
        float U(float d) => su * (w + d);
        return new[]
        {
            K(A, U(1.6f), f - sf * 0.9f, a, Ready), K(0.25f, U(1.6f), f - sf * 0.9f, a, Ready),
            K(0.5f, U(1.0f), f - sf * 0.15f, a, Crouch), K(0.72f, U(0.95f), f - sf * 0.05f, a, Crouch),
            K(1.0f, U(1.9f), f - sf * 0.3f, ad, Carry), K(1.35f, U(2.9f), f - sf * 1.5f, ad, Carry),
            K(1.45f, U(2.9f), f - sf * 1.5f, ad, Crouch), K(TDrop, U(2.9f), f - sf * 1.5f, ad, Crouch),
            K(1.9f, U(3.3f), f - sf * 1.5f, ad, Stand), K(3.5f, U(3.3f), f - sf * 1.5f, ad, Stand),
        };
    }
    Key[] OnKeys(int c)
    {
        float su = (c & 1) == 0 ? 1f : -1f, sf = c < 2 ? 1f : -1f, w = wu[c], f = wf[c], a = -90f * su;
        float U(float d) => su * (w + d);
        return new[]
        {
            K(A, U(3.0f), f + sf * 1.5f, a, Carry, 1f), K(0.3f, U(3.0f), f + sf * 1.5f, a, Carry, 1f),
            K(0.6f, U(1.7f), f + sf * 0.25f, a, Carry, 1f), K(0.85f, U(0.95f), f - sf * 0.02f, a, Carry, 1f),
            K(1.06f, U(0.95f), f - sf * 0.02f, a, Crouch, 1f), K(1.25f, U(1.6f), f + sf * 0.7f, a, Stand), K(3.5f, U(1.6f), f + sf * 0.7f, a, Stand),
        };
    }
    Key[] HelperKeys(int s)
    {
        float a = 180f - 25f * s;
        return new[]
        {
            K(A, s * 2.0f, frontEnd + 1.0f, 180f, Stand), K(-0.4f, s * 2.0f, frontEnd + 1.0f, 180f, Stand),
            K(-0.05f, s * 1.2f, frontEnd + 0.55f, a, Crouch), K(0.1f, s * 1.2f, frontEnd + 0.55f, a, Crouch, 1f), K(1.7f, s * 1.2f, frontEnd + 0.55f, a, Crouch, 1f),
            K(1.9f, s * 1.5f, frontEnd + 0.9f, 180f, Stand), K(3.5f, s * 1.5f, frontEnd + 0.9f, 180f, Stand),
        };
    }

    static float Smooth(float x) => x <= 0f ? 0f : x >= 1f ? 1f : x * x * (3f - 2f * x);
    static Key Eval(Key[] ks, float s)
    {
        if (s <= ks[0].t) return ks[0];
        int n = ks.Length; if (s >= ks[n - 1].t) return ks[n - 1];
        for (int i = 0; i + 1 < n; i++)
            if (s < ks[i + 1].t)
            {
                float f = Smooth((s - ks[i].t) / Mathf.Max(1e-4f, ks[i + 1].t - ks[i].t)); Key a = ks[i], b = ks[i + 1];
                return new Key { t = s, u = Mathf.Lerp(a.u, b.u, f), f = Mathf.Lerp(a.f, b.f, f), a = Mathf.LerpAngle(a.a, b.a, f), w = Mathf.Lerp(a.w, b.w, f), p = Pose.Lerp(a.p, b.p, f) };
            }
        return ks[n - 1];
    }

    static float Lift(float s) => LiftH * (Smooth((s - 0.15f) / 0.3f) - Smooth((s - 1.7f) / 0.25f));
    Vector3 WheelPos(int c, float lift) { float su = (c & 1) == 0 ? 1f : -1f; return new Vector3(gs * su * wu[c], WheelR + lift, wf[c]); }

    // the jack men follow the handle of their jack (computed, not keyed): sg = +1 front, -1 rear
    Key JackKey(Person p, float s, float lift)
    {
        float sg = p.role == FJack ? 1f : -1f, lf = lift / LiftH;
        float slide = Mathf.Max(1f - Smooth((s + 0.45f) / 0.4f), Smooth((s - 1.95f) / 0.3f)) * 1.5f;
        float pivotF = (sg > 0 ? frontEnd - 0.35f : rearEnd + 0.35f) + sg * slide;
        float tipY = Mathf.Lerp(0.95f, 0.5f, lf), th = Mathf.Asin(Mathf.Clamp((tipY - 0.12f) / 1.1f, -0.9f, 0.9f));
        float tipF = pivotF + sg * 1.1f * Mathf.Cos(th);
        p.tip = new Vector3(0, tipY, tipF);
        var k = K(s, 0, tipF + sg * 0.45f, sg > 0 ? 180f : 0f, Pose.Lerp(Ready, Crouch, lf));
        k.w = Smooth((s + 0.9f) / 0.3f) * (1f - Smooth((s - 2.3f) / 0.2f));
        return k;
    }

    // ---------------------------------------------------------------- per frame
    void Update()
    {
        onTick?.Invoke();
        if (st == St.Off) return;
        float dt = Time.deltaTime;
        var cam = Camera.main;
        bool vis = cam == null || (cam.transform.position - centre).sqrMagnitude < 230f * 230f;
        if (st == St.Run)
        {
            float sr = clock != null ? clock() : curS, prev = curS;
            float s = sr < Hold ? sr : sr < Hold + extra ? Hold : sr - extra;
            curS = s;
            if (s > Release + Depart) { Stop(); return; }
            SetVisible(vis);
            if (vis) ApplyFrame(s, Mathf.Abs(s - prev) < 1e-5f ? 0f : dt);
        }
        else // waiting
        {
            SetVisible(vis);
            if (vis) ApplyFrame(A, dt);
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

    void ApplyFrame(float s, float dt)
    {
        float lift = st == St.Run ? Lift(s) : 0f;
        // people and what they hold
        for (int i = 0; i < crew.Length; i++)
        {
            var p = crew[i];
            Key k = (p.role == FJack || p.role == RJack) ? JackKey(p, s, lift) : Eval(p.keys, s);
            Solve(p, k, s, lift, dt);
        }
        PlaceProps(s, lift);
        ppl.Flush(centre); props.Flush(centre);
    }

    Vector3 Chest(Person p)
    {
        Quaternion r = Quaternion.Euler(0, p.yaw, 0);
        return new Vector3(p.pos.x, p.pos.y + 0.12f, p.pos.z) + r * new Vector3(0, 0, 0.42f);
    }
    Vector3 OffTyre(Person p, float s, float lift)
    {
        Vector3 wp = WheelPos(p.corner, lift); if (s < TOff) return wp;
        Vector3 ch = Chest(p);
        if (s < TDrop - 0.15f) return Vector3.Lerp(wp, ch, Smooth((s - TOff) / 0.3f));
        if (s < TDrop) return Vector3.Lerp(ch, p.dropSpot, Smooth((s - (TDrop - 0.15f)) / 0.15f));
        return p.dropSpot;
    }
    Vector3 OnTyre(Person p, float s, float lift)
    {
        Vector3 ch = Chest(p); if (s < 0.8f) return ch;
        Vector3 wp = WheelPos(p.corner, lift); return s < 1.0f ? Vector3.Lerp(ch, wp, Smooth((s - 0.8f) / 0.2f)) : wp;
    }

    void Solve(Person p, Key k, float s, float lift, float dt)
    {
        // pose with a run cycle when the person moves
        Pose pose = k.p; Vector3 hp = new Vector3(gs * k.u, 0, k.f);
        float speed = p.speed;
        if (!p.init) { p.init = true; p.prevH = hp; p.yaw = KeyYaw(k.a); speed = 0; p.runAmt = 0; }
        else if (dt > 1e-4f) { Vector3 v = hp - p.prevH; float sp = v.magnitude / dt; speed = Mathf.Lerp(p.speed, sp, 0.5f); if (v.sqrMagnitude > 1e-8f) p.lastMove = v; p.prevH = hp; p.phase += sp * dt * 3.9f; }
        p.speed = speed;
        float run = Mathf.Clamp01((speed - 0.4f) / 1.2f); p.runAmt = run;
        float yawKey = KeyYaw(k.a), yawT = yawKey;
        // facing: along the walk when moving (blend), as keyed when working
        if (run > 0.05f && p.lastMove.sqrMagnitude > 1e-6f) yawT = Mathf.LerpAngle(yawKey, Mathf.Atan2(p.lastMove.x, p.lastMove.z) * Mathf.Rad2Deg, run);
        p.yaw = dt > 1e-4f ? Mathf.MoveTowardsAngle(p.yaw, yawT, 540f * dt) : yawT;
        if (run > 0f)
        {
            float sn = Mathf.Sin(p.phase), sn2 = Mathf.Sin(p.phase + Mathf.PI);
            pose.hl = Mathf.Lerp(pose.hl, 38f * sn, run); pose.hr = Mathf.Lerp(pose.hr, 38f * sn2, run);
            pose.kl = Mathf.Lerp(pose.kl, 12f + 55f * Mathf.Max(0f, Mathf.Sin(p.phase + 1.7f)), run); pose.kr = Mathf.Lerp(pose.kr, 12f + 55f * Mathf.Max(0f, Mathf.Sin(p.phase + 1.7f + Mathf.PI)), run);
            pose.sl = Mathf.Lerp(pose.sl, 35f * sn2 + 10f, run); pose.sr = Mathf.Lerp(pose.sr, 35f * sn + 10f, run);
            pose.el = Mathf.Lerp(pose.el, 80f, run); pose.er = Mathf.Lerp(pose.er, 80f, run);
        }
        const float L1 = 0.42f;
        float hL = 0.10f + L1 * Mathf.Cos(pose.hl * Mathf.Deg2Rad) + L1 * Mathf.Cos((pose.hl - pose.kl) * Mathf.Deg2Rad);
        float hR = 0.10f + L1 * Mathf.Cos(pose.hr * Mathf.Deg2Rad) + L1 * Mathf.Cos((pose.hr - pose.kr) * Mathf.Deg2Rad);
        p.pos = new Vector3(hp.x, Mathf.Max(hL, hR), hp.z);
        Quaternion rot = Quaternion.Euler(0, p.yaw, 0);
        Matrix4x4 root = Matrix4x4.TRS(p.pos, rot, Vector3.one);
        Matrix4x4 Tm = root * Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(pose.lean, 0, 0), Vector3.one);

        // hand targets (crew-local) by role
        Vector3 tR = Vector3.zero, tL = Vector3.zero; float wR = 0f, wL = 0f;
        Vector3 rx = rot * Vector3.right;
        switch (p.role)
        {
            case Gun:
            {
                Vector3 outv = new Vector3(gs * ((p.corner & 1) == 0 ? 1f : -1f), 0, 0), nut = WheelPos(p.corner, lift) + outv * 0.20f;
                tR = nut + outv * 0.34f; tL = nut + outv * 0.56f + Vector3.up * 0.10f; wR = wL = k.w; break;
            }
            case Off:
            {
                Vector3 c = OffTyre(p, s, lift); float w = Smooth((s - 0.45f) / 0.25f) * (1f - Smooth((s - 1.38f) / 0.14f));
                tR = c + rx * 0.31f; tL = c - rx * 0.31f; wR = wL = w; break;
            }
            case On:
            {
                Vector3 c = OnTyre(p, s, lift); float w = k.w * (1f - Smooth((s - TOn) / 0.12f));
                tR = c + rx * 0.31f; tL = c - rx * 0.31f; wR = wL = w; break;
            }
            case FJack:
            case RJack:
                tR = p.tip + rx * 0.13f; tL = p.tip - rx * 0.13f; wR = wL = k.w; break;
            case Helper:
            {
                float sw = p.side; Vector3 ep = new Vector3(gs * sw * 0.93f, 0.20f + lift, frontEnd - 0.05f);
                float jig = (s > 0.3f && s < 1.5f) ? Mathf.Sin(s * 14f) * 0.012f : 0f;
                tR = ep + new Vector3(0, jig, 0); tL = ep + new Vector3(0, 0.04f - jig, -0.22f); wR = wL = k.w; break;
            }
            case Lolli:
            {
                float r = Smooth((s - 2.05f) / 0.2f);
                tR = LolliBase(p, s) + Vector3.up * (1.05f + 0.5f * r); wR = 1f; break;
            }
        }
        // arms (two-bone IK towards the targets, blended with the pose) and legs
        Matrix4x4 inv = Tm.inverse; int b0 = p.part0;
        var Fm = F;
        ppl.Set(b0 + 0, Fm * root);
        ppl.Set(b0 + 1, Fm * Tm);
        Matrix4x4 Mh = Tm * Matrix4x4.TRS(new Vector3(0, 0.64f, 0.02f), Quaternion.identity, Vector3.one);
        ppl.Set(b0 + 2, Fm * Mh); ppl.Set(b0 + 3, Fm * Mh);
        for (int a = 0; a < 2; a++)
        {
            float sgn = a == 0 ? -1f : 1f, sh = a == 0 ? pose.sl : pose.sr, el = a == 0 ? pose.el : pose.er, w = a == 0 ? wL : wR; Vector3 tg = a == 0 ? tL : tR;
            Matrix4x4 Mf = Arm(Tm, inv, sgn, sh, el, tg, w, out Matrix4x4 Mu);
            ppl.Set(b0 + 4 + a, Fm * Mu); ppl.Set(b0 + 6 + a, Fm * Mf); ppl.Set(b0 + 8 + a, Fm * Mf);
            if (a == 1) p.foreR = Mf;
        }
        for (int a = 0; a < 2; a++)
        {
            float sgn = a == 0 ? -1f : 1f, hip = a == 0 ? pose.hl : pose.hr, kn = a == 0 ? pose.kl : pose.kr;
            Matrix4x4 Mth = root * Matrix4x4.TRS(new Vector3(sgn * 0.09f, -0.02f, 0), Quaternion.Euler(-hip, 0, 0), Vector3.one);
            Matrix4x4 Msh = Mth * Matrix4x4.TRS(new Vector3(0, -0.42f, 0), Quaternion.Euler(kn, 0, 0), Vector3.one);
            ppl.Set(b0 + 10 + a, Fm * Mth); ppl.Set(b0 + 12 + a, Fm * Msh);
            Vector3 ankle = Msh.MultiplyPoint3x4(new Vector3(0, -0.42f, 0));
            ppl.Set(b0 + 14 + a, Fm * Matrix4x4.TRS(ankle, rot, Vector3.one));
        }
    }

    // two-bone arm: shoulder at the torso, targets blend from the pose's hand position to the IK target; returns the forearm frame
    Matrix4x4 Arm(Matrix4x4 Tm, Matrix4x4 inv, float sgn, float sh, float el, Vector3 tgtLocal, float w, out Matrix4x4 Mu)
    {
        const float l1 = 0.30f, l2 = 0.34f;
        Vector3 S = new Vector3(0.235f * sgn, 0.50f, 0f);
        Vector3 fk = S + Quaternion.Euler(-sh, 0, 0) * (new Vector3(0, -l1, 0) + Quaternion.Euler(-el, 0, 0) * new Vector3(0, -l2, 0));
        Vector3 tgt = w > 0.001f ? Vector3.Lerp(fk, inv.MultiplyPoint3x4(tgtLocal), w) : fk;
        Vector3 d = tgt - S; float dist = Mathf.Clamp(d.magnitude, 0.12f, l1 + l2 - 0.005f); Vector3 dh = d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.down;
        float cosA = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f), ang = Mathf.Acos(cosA);
        Vector3 pole = new Vector3(sgn * 0.45f, -1f, -0.35f);
        Vector3 ax = Vector3.Cross(dh, pole); if (ax.sqrMagnitude < 1e-6f) ax = Vector3.right; ax.Normalize();
        Vector3 upper = dh * Mathf.Cos(ang) + Vector3.Cross(ax, dh) * Mathf.Sin(ang);
        Vector3 elbow = S + upper * l1, hand = S + dh * dist;
        Vector3 fore = (hand - elbow); fore = fore.sqrMagnitude > 1e-8f ? fore.normalized : Vector3.down;
        Mu = Tm * Matrix4x4.TRS(S, Quaternion.FromToRotation(Vector3.down, upper), Vector3.one);
        return Tm * Matrix4x4.TRS(elbow, Quaternion.FromToRotation(Vector3.down, fore), Vector3.one);
    }

    Vector3 LolliBase(Person p, float s)
    {
        Quaternion r = Quaternion.Euler(0, p.yaw, 0); Vector3 b = new Vector3(p.pos.x, 0, p.pos.z) + r * new Vector3(0.28f, 0, 0.42f);
        b.y = 0.5f * Smooth((s - 2.05f) / 0.2f); return b;
    }

    void PlaceProps(float s, float lift)
    {
        var F2 = F; Person lolli = null, ext = null; int fj = -1, rj = -1;
        for (int i = 0; i < crew.Length; i++)
        {
            var p = crew[i];
            switch (p.role)
            {
                case Gun:
                {
                    Matrix4x4 g = p.foreR * Matrix4x4.TRS(new Vector3(0, -0.30f, 0), Quaternion.Euler(90, 0, 0), Vector3.one) * Matrix4x4.TRS(new Vector3(0, 0, 0.10f), Quaternion.identity, Vector3.one);
                    props.Set(PGun + p.corner, F2 * g); break;
                }
                case Off:
                {
                    int c = p.corner;
                    if (s < TOff) props.Hide(POld + c);
                    else props.Set(POld + c, F2 * Matrix4x4.TRS(OffTyre(p, s, lift), Quaternion.Euler(0, p.yaw + 90f, 0), Vector3.one));
                    break;
                }
                case On:
                {
                    int c = p.corner;
                    if (s >= TOn) props.Hide(PNew + c);
                    else props.Set(PNew + c, F2 * Matrix4x4.TRS(OnTyre(p, s, lift), Quaternion.Euler(0, p.yaw + 90f, 0), Vector3.one));
                    break;
                }
                case FJack: fj = i; break;
                case RJack: rj = i; break;
                case Lolli: lolli = p; break;
                case Ext: ext = p; break;
            }
        }
        for (int j = 0; j < 2; j++)
        {
            var p = crew[j == 0 ? fj : rj]; float sg = j == 0 ? 1f : -1f;
            float pivotF = p.tip.z - sg * 1.1f * Mathf.Cos(Mathf.Asin(Mathf.Clamp((p.tip.y - 0.12f) / 1.1f, -0.9f, 0.9f)));
            props.Set(PJackBase + j, F2 * Matrix4x4.TRS(new Vector3(0, 0, pivotF), Quaternion.identity, Vector3.one));
            props.Set(PJackCol + j, F2 * Matrix4x4.TRS(new Vector3(0, 0.10f, pivotF), Quaternion.identity, new Vector3(1f, 0.05f + lift, 1f)));
            Vector3 dir = (p.tip - new Vector3(0, 0.12f, pivotF)).normalized;
            props.Set(PHandle + j, F2 * Matrix4x4.TRS(new Vector3(0, 0.12f, pivotF), Quaternion.LookRotation(dir, Vector3.up), Vector3.one));
        }
        if (lolli != null)
        {
            Vector3 b = LolliBase(lolli, s); float r = Smooth((s - 2.05f) / 0.2f);
            Matrix4x4 m = F2 * Matrix4x4.TRS(b, Quaternion.Euler(0, lolli.yaw, 0) * Quaternion.Euler(-25f * r, 0, 0), Vector3.one);
            props.Set(PPole, m); props.Set(PDisc, m);
            bool green = s >= 2.12f;
            if (green != lolliGreen) { lolliGreen = green; props.pal[SLolli] = green ? new Color(0.1f, 0.8f, 0.25f) : new Color(0.85f, 0.05f, 0.05f); props.Recolour(PDisc); }
        }
        if (ext != null) props.Set(PExt, F2 * ext.foreR * Matrix4x4.TRS(new Vector3(0, -0.33f, 0.06f), Quaternion.identity, Vector3.one));
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
