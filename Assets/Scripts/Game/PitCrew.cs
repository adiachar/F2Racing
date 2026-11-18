// PitCrew.cs — an animated pit stop with a real F1-style pit crew. One crew (18 crew members, two engineers in headsets
// and two mechanics watching from the garage door; their tools and four tyre pairs) is built once per circuit and re-used for
// every stop; it is only drawn (and only animated) while a stop is on and within ~120 m of the camera.
//
//  * People: rigged low-poly humans (People.cs: Quaternius' CC0 Universal Base Characters, ~1.2-1.3k triangles, LOD1
//    ~0.3k beyond 40 m), skinned to 22 joints and animated from baked clips (Universal Animation Library, CC0). Crew wear
//    team-colour fireproof overalls with white / black flank panels and leg stripes, a contrasting yoke, knee pads,
//    gloves and full-face helmets with dark visors and a top stripe; engineers team shirts, dark trousers and headsets
//    with a boom mic; mechanics overalls with caps or bare heads. Male / female bodies, heights, skin tones and hair vary
//    per person. Colours come from the team's Livery (a palette per person, People.shader).
//  * Animation: per role, keys (position on the box floor, facing, a pose) with Hermite paths (no stop-start at
//    pass-through keys), eased with anticipation (weight back before the dash) and overshoot-and-settle. A pose is a
//    clip, or two mixed (half-crouched ready = crouch + idle): idle, crouch, kneel-and-work (wheel gun), push / brace
//    (jacks, side stabilisers), celebrate; engineers stand with a hand on the headset. Moving, the walk / jog / sprint
//    cycle is blended in, its phase from the distance covered (no foot sliding, back-pedalling plays the walk backwards).
//    Two-bone IK puts the hands on the gun at the nut, on the tyre, on the jack handle and on the car, raises an arm to
//    signal "done" and both in the celebration (fist pumps); the head turns to what matters (the incoming car, the nut,
//    the jack, the departing car). Staggered start / finish per person. Everything is a function of the stop clock
//    (frozen shots still show a pose) apart from the idle clips' own life.
//  * Stop (~2.4 s from the car stopping to the release, PitLane.Service): front jack in under the nose as the car stops,
//    rear jack in from the side, car up 8 cm (0.15-0.45 s), guns on, old wheels off (0.72 s) and carried away, new wheels
//    on (1.06 s, compound band), guns tightened, gunners stand and signal, jacks down (1.7-1.95 s), release light green
//    (2.12 s), car away (2.4 s), jacks swung clear, crew celebrate. The crew works ON THE REAL CAR (the player's model or
//    the rival's model in view): its clock is the run's (PitRun.Clock), the car is lifted (LateUpdate), its wheels are
//    hidden while off, and the compound band changes when the new wheels go on. A longer stop (wing change) holds at Hold.
//  * Props = 1 draw call (tyres, wheel guns with hoses to the gantry reels, quick-lift jacks, the release light on the
//    gantry, release handset, extinguisher): one mesh whose vertices are written in world space every frame (a part is a
//    range of vertices; hidden = collapsed). People: one skinned mesh each, soft contact shadows in one mesh. The garages
//    and their people are PitGarage.cs / PeoplePits.cs.
//  * GameManager glue (partial class at the bottom): PitCrewSetup (after SetupRivals: the lane for the rivals), PitCrewTick (the
//    crew's own Update calls it: crew waiting while BOX is armed or a car is coming in, the stop for the player and for rivals
//    within ~150 m of the camera), the player's pit-lane drive (PlayerPitEntry / PlayerPitStep), the cinematic camera of the
//    player's own stop (PitCamera), ShotPit (shot mode, pit=1).
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
    internal const float ReleaseT = Release, GreenT = TGreen;

    const int KCrew = 0, KEng = 1, KMech = 2;

    // ---------------------------------------------------------------- props palette slots
    const int SRubber = 0, SRim = 1, SOldBand = 2, SNewBand = 3, SMetal = 4, SGun = 5, SBlack = 6, SHose = 7, SJack = 8, SJack2 = 9, SLampR = 10, SLampG = 11, SExt = 12, PropPal = 13;

    // ---------------------------------------------------------------- poses: a clip (c), optionally mixed with a second (c2 by m), held at
    // ct (>= 0) or playing; h = how upright (gates the walk / jog); rl / rr = left / right arm raised overhead; pt = right arm pointing
    internal struct Pose { public int c, c2; public float m, ct, h, rl, rr, pt; }
    static Pose P(int c, float h, int c2 = -1, float m = 0f, float ct = -1f, float rl = 0f, float rr = 0f, float pt = 0f) => new Pose { c = c, c2 = c2, m = m, ct = ct, h = h, rl = rl, rr = rr, pt = pt };
    static readonly Pose
        Stand = P(People.Idle, 0.935f),
        Rest = P(People.Idle, 0.925f),
        Ready = P(People.Crouch, 0.80f, People.Idle, 0.45f),
        ReadyBack = P(People.Crouch, 0.83f, People.Idle, 0.6f),
        Kneel = P(People.Kneel, 0.50f),
        Low = P(People.Crouch, 0.52f),
        Carry = P(People.Idle, 0.90f),
        Brace0 = P(People.Push, 0.86f, People.Idle, 0.5f),
        Brace = P(People.Push, 0.66f),
        HoldCar = P(People.Push, 0.80f, People.Crouch, 0.35f),
        Signal = P(People.Idle, 0.93f, rl: 1f),
        Point = P(People.Idle, 0.93f, pt: 1f),
        Cheer = P(People.Celebrate, 0.945f, rl: 1f, rr: 1f),
        Fist = P(People.Celebrate, 0.935f, People.Idle, 0.4f, rl: 1f),
        Crossed = P(People.FoldArms, 0.925f),
        Hips = P(People.Talk, 0.93f);

    // u = towards the garage, f = along the car, a = facing (0 = nose, 90 = garage), w = hands on; e = ease into it (0 smooth,
    // 1 overshoot, 2 anticipation). Evaluated: p .. q2 by x (the eased blend between the two keys around the time)
    struct Key { public float t, u, f, a, w, mu, mf, x; public int e; public Pose p, q2; }
    static Key K(float t, float u, float f, float a, in Pose p, float w = 0f, int e = 0) => new Key { t = t, u = u, f = f, a = a, p = p, q2 = p, w = w, e = e };

    const int Gun = 0, Off = 1, On = 2, FJack = 3, RJack = 4, Helper = 5, Rel = 6, Ext = 7, Eng = 8, Watch = 9;
    sealed class Person
    {
        public int role, corner, side, kind, seed, cheer, body, gear, outfit;
        public float scale, delay, gunW;
        public Key[] keys; public float[] phase;
        public People.Rig rig;
        public Vector3 dropSpot, dropAx, ground, handL, handR, tyre, tyreAx, tip, pivot, hdir, gunAt, foreDir; public float yaw;
    }
    const int NPeople = 22;

    // ---------------------------------------------------------------- a batch: many parts in one mesh, vertices written in world space (the props)
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
    }

    // ---------------------------------------------------------------- state
    enum St { Off, Wait, Run }
    St st; int owner = -99, bay = -1; float extra, curS = A;
    Batch props; GameObject pplRoot; People.Blobs blobs; bool rigged;
    readonly People.PoseBuf pa = new People.PoseBuf(), pb = new People.PoseBuf(), pc = new People.PoseBuf();
    System.Func<CarModel> target; System.Func<float> clock; CarModel curModel;
    Transform[] wheelRoots = new Transform[4]; readonly bool[] wheelWas = new bool[4];
    int[] order;
    Vector3 centre, fwd, side; float gs, garageDist, fYaw;
    Matrix4x4 F, Fi;
    readonly float[] wu = new float[4], wf = new float[4]; float frontEnd, rearEnd;
    readonly Person[] crew = new Person[NPeople];
    Livery liv; Color oldC, newC; bool lightGreen, wheelsOff, newStripe, visible = true;
    public System.Action onTick;
    static PitCrew live;

    public bool Busy => st == St.Run;
    public bool Waiting(int who) => st == St.Wait && owner == who;
    public bool Owns(int who) => st != St.Off && owner == who;
    public int PlayerBay => order[0];
    public int TeamBay(int team) => PitGarage.TeamBay(order, team);
    public Vector3 BayPos(int b) => Trackside.Bays[b].centre;
    // the bay where a crew stands or works now (-1: none); PeoplePits' walkers keep clear of it
    public static int ActiveBay => live != null && live.st != St.Off ? live.bay : -1;
    // the stop's clock (s since the car stopped, frozen at Hold while a longer stop holds) and the crew frame, for the pit camera
    internal float StopClock => curS;
    internal bool Running => st == St.Run;
    internal Vector3 BoxCentre => centre;
    internal Vector3 BoxFwd => fwd;
    internal Vector3 BoxToGarage => side;
    internal float FrontEnd => frontEnd;
    internal float RearEnd => rearEnd;

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
        live = c;
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
        if (teams != null) foreach (var kv in teams) { int b = TeamBay(kv.Key); if (gn[b] == null) { gl[b] = kv.Value; gn[b] = PitGarage.TeamName(kv.Value); } }
        PitGarage.SetTeams(gl, gn);
        rigged = People.Ready;
        pplRoot = new GameObject("PitPeople"); pplRoot.transform.SetParent(transform, false);
        for (int i = 0; i < NPeople; i++)
        {
            int kind = i < 18 ? KCrew : i < 20 ? KEng : KMech;
            var p = new Person { kind = kind, seed = i * 13 + 5, delay = 0.09f * Rnd(i, 8) };
            p.cheer = (int)(Rnd(i, 9) * 2.999f);
            p.body = People.BodyOf(p.seed, kind == KEng ? 0.4f : kind == KMech ? 0.2f : 0.08f);
            p.scale = People.HeightOf(p.seed, p.body);
            p.gear = kind == KCrew ? People.GHelmet : kind == KEng ? People.GHeadset : Rnd(i, 4) < 0.6f ? People.GCap : People.GBare;
            p.outfit = kind == KCrew ? People.OCrew : kind == KEng ? People.OEngineer : People.OMechanic;
            if (rigged) p.rig = new People.Rig(pplRoot.transform, p.body, p.gear, "Crew" + i) { scale = p.scale };
            crew[i] = p;
        }
        if (rigged) blobs = new People.Blobs(pplRoot.transform, NPeople, "PitCrewShadows");
        props = new Batch(PropPal); BuildProps(props); props.Finish(Mat(), transform, "PitProps");
        pplRoot.SetActive(false); props.Show(false);
        st = St.Off;
    }

    static Color GunCol(Livery l) { var a = l.accent; float m = Mathf.Max(a.r, Mathf.Max(a.g, a.b)); return m > 0.3f ? a : l.secondary; }


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
        b.Box(Vector3.zero, new Vector3(0.52f, 0.26f, 0.12f), SBlack, id); b.Box(new Vector3(0, 0.14f, -0.09f), new Vector3(0.52f, 0.02f, 0.1f), SBlack, id);
        b.glow = 2.6f; b.Box(new Vector3(-0.12f, 0, -0.062f), new Vector3(0.17f, 0.17f, 0.01f), SLampR, id); b.Box(new Vector3(0.12f, 0, -0.062f), new Vector3(0.17f, 0.17f, 0.01f), SLampG, id);
        b.glow = 1.6f; b.Box(new Vector3(-0.12f, 0, 0.062f), new Vector3(0.12f, 0.12f, 0.01f), SLampR, id); b.Box(new Vector3(0.12f, 0, 0.062f), new Vector3(0.12f, 0.12f, 0.01f), SLampG, id); b.glow = 0f; // (and to the crew)
        b.NewPart(); b.Box(Vector3.zero, new Vector3(0.06f, 0.13f, 0.04f), SBlack, id); b.glow = 1.8f; b.Box(new Vector3(0, 0.067f, 0), new Vector3(0.03f, 0.01f, 0.03f), SLampG, id); b.glow = 0f; // release handset
        b.NewPart(); b.Cyl(new Vector3(0, -0.24f, 0.05f), Quaternion.Euler(0, 0, 90), 0.085f, 0.45f, 8, SExt, SMetal, SMetal, 0.03f); b.Box(new Vector3(0, 0.0f, 0.05f), new Vector3(0.05f, 0.06f, 0.08f), SBlack, id); // extinguisher
    }

    void OnDestroy() { RestoreWheels(); blobs?.Destroy(); if (live == this) live = null; }

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
        if (props != null) { props.HideAll(); props.Show(false); }
        if (pplRoot != null) pplRoot.SetActive(false);
    }

    // ---------------------------------------------------------------- set up one stop
    void Prepare(int who, int bayIdx, Livery l, Color oldTyre, Color newTyre, System.Func<CarModel> car)
    {
        RestoreWheels();
        owner = who; bay = bayIdx; liv = l; oldC = oldTyre; newC = newTyre; lightGreen = false; wheelsOff = false; newStripe = false;
        var bb = Trackside.Bays[bayIdx]; fwd = bb.fwd; side = bb.side; garageDist = bb.garageDist;
        float gy = WorldBuilder.H(bb.centre.x, bb.centre.z); centre = new Vector3(bb.centre.x, gy, bb.centre.z);
        Vector3 right = Vector3.Cross(Vector3.up, fwd); gs = Vector3.Dot(side, right) >= 0f ? 1f : -1f;
        F = Matrix4x4.TRS(centre, Quaternion.LookRotation(fwd, Vector3.up), Vector3.one); Fi = F.inverse; fYaw = Quaternion.LookRotation(fwd, Vector3.up).eulerAngles.y;
        // palettes
        foreach (var p in crew) p.rig?.Dress(p.outfit, l, p.seed);
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
        props.Recolour();
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
        foreach (var c in ch) ks.Add(K(c.t, u, f, a, c.q, 0f, c.q.rl > 0f || c.q.rr > 0f || c.q.pt > 0f ? 1 : 0));
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
        if (i < 0 || i >= ks.Length - 1) { var k = ks[i < 0 ? 0 : ks.Length - 1]; k.q2 = k.p; k.x = 0f; return k; }
        Key a = ks[i], b = ks[i + 1]; float h = b.t - a.t, x = (s - a.t) / h, sm = Smooth(x);
        float e = b.e == 1 ? BackOut(x) : b.e == 2 ? BackIn(x) : Smoother(x);
        return new Key { t = s, u = Herm(a.u, a.mu, b.u, b.mu, x, h), f = Herm(a.f, a.mf, b.f, b.mf, x, h), a = Mathf.LerpAngle(a.a, b.a, sm), w = Mathf.Lerp(a.w, b.w, sm), p = a.p, q2 = b.p, x = e };
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
        ground = new Vector3(tip.x, 0, tip.z) + d * (0.45f + 0.3f * (1f - hold)); // (the push lean: hands ~0.5 m ahead of the feet)
        yaw = Mathf.Atan2(-d.x, -d.z) * Mathf.Rad2Deg; hd = d;
    }

    Key KeyAt(Person p, float se)
    {
        if (p.role == FJack || p.role == RJack)
        {
            Jack(p, se, out Vector3 g, out float yaw, out _, out _, out _, out float hold);
            var kj = new Key { t = se, u = g.x, f = g.z, a = yaw, w = hold, p = Brace0, q2 = Brace, x = Lift(se) / LiftH };
            float ch = Smooth((se - 2.45f) / 0.35f);
            if (ch > 0f) { kj.p = Brace0; kj.q2 = After(p); kj.x = p.cheer == 0 ? ch : BackOut(Mathf.Clamp01((se - 2.45f) / 0.35f)); }
            return kj;
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
            ph += d / Mathf.Lerp(1.6f, 2.7f, Mathf.Clamp01((v - 1.2f) / 2.5f)) * 2f * Mathf.PI; // one clip cycle (stride pair): ~1.6 m walking .. 2.7 m running
            p.phase[i] = ph; prev = cur;
        }
    }
    static float Phase(Person p, float s)
    {
        float x = (s - T0) / TabDt; int i = Mathf.Clamp((int)x, 0, p.phase.Length - 2);
        return Mathf.Lerp(p.phase[i], p.phase[i + 1], Mathf.Clamp01(x - i));
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
        bool vis = cam == null || (cam.transform.position - centre).sqrMagnitude < 120f * 120f; // drawn and animated only near the camera
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

    void SetVisible(bool v) { visible = v; if (pplRoot.activeSelf != v) pplRoot.SetActive(v); props.Show(v); }

    void ApplyFrame(float s)
    {
        float lift = st == St.Run ? Lift(s) : 0f, rt = Time.time;
        var cam = Camera.main; Vector3 cp = cam != null ? cam.transform.position : centre;
        if (blobs != null) blobs.Begin();
        for (int i = 0; i < crew.Length; i++)
        {
            var p = crew[i]; Animate(p, s, lift, rt, cp);
            if (blobs != null && p.rig != null) { Vector3 pel = p.rig.Pelvis; pel.y = p.rig.tr.position.y; blobs.Set(i, pel, p.rig.tr.forward, 0.42f * p.scale, 0.85f, 0.75f); }
        }
        if (blobs != null) blobs.Flush();
        PlaceProps(s, lift);
        props.Flush(centre);
    }

    // the clip(s) of one pose at time t (engineers stand with a hand on their headset instead of the plain idle)
    void Clip(Person p, in Pose q, float t, People.PoseBuf o)
    {
        int c = q.c == People.Idle && p.kind == KEng ? People.Headset : q.c;
        People.Sample(c, q.ct >= 0f ? q.ct : t, o);
        if (q.c2 >= 0 && q.m > 0.001f) { People.Sample(q.c2, t, pc); People.Blend(o, pc, q.m, o); }
    }

    // one person at stop time s: place, facing and pose from the keys (or the jack), the walk / jog cycle when moving, the
    // hands on their targets (two-bone IK), an arm raised to signal or both in the celebration, the head on what matters
    void Animate(Person p, float s, float lift, float rt, Vector3 camPos)
    {
        float se = SEff(p, s);
        Key k = KeyAt(p, se);
        // speed and heading of the move (from the path itself, so a frozen clock still shows the stride)
        Vector2 va = PosAt(p, se - 0.04f), vb = PosAt(p, se + 0.04f), vel = (vb - va) / 0.08f;
        float speed = vel.magnitude, x = Mathf.Clamp01(k.x);
        float h = Mathf.Lerp(k.p.h, k.q2.h, x);
        float loco = Mathf.Clamp01((speed - 0.3f) / 0.9f) * Mathf.Clamp01((h - 0.6f) / 0.25f); // no jogging out of a kneel / squat
        float yaw = k.a; bool back = false;
        if (speed > 0.25f)
        {
            float my = Mathf.Atan2(vel.x, vel.y) * Mathf.Rad2Deg, d = Mathf.DeltaAngle(k.a, my);
            if (Mathf.Abs(d) > 115f) back = true; else yaw = k.a + d * loco;
        }
        Vector3 g = new Vector3(k.u, 0, k.f); Quaternion yq = Quaternion.Euler(0, yaw, 0);
        p.ground = g; p.yaw = yaw;
        if (p.role == FJack || p.role == RJack) Jack(p, se, out _, out _, out p.tip, out p.pivot, out p.hdir, out _);
        Matrix4x4 R = Matrix4x4.TRS(g, yq, Vector3.one * p.scale);
        Vector3 Ro(Vector3 v) => R.MultiplyPoint3x4(v);
        if (p.rig == null) { p.handL = Ro(new Vector3(-0.2f, 1.0f, 0.3f)); p.handR = Ro(new Vector3(0.2f, 1.0f, 0.3f)); p.foreDir = yq * Vector3.forward; return; }
        var r = p.rig;
        // the body: the two poses' clips mixed by the eased key blend, then the locomotion cycle
        float ct = rt + p.seed * 0.37f;
        Clip(p, k.p, ct, pa);
        if (x > 0.001f) { Clip(p, k.q2, ct, pb); People.Blend(pa, pb, x, pa); }
        if (loco > 0.001f)
        {
            int lc = speed > 4.6f ? People.Sprint : speed > 1.8f ? People.Jog : People.Walk;
            float u = Phase(p, s) / (2f * Mathf.PI); u -= Mathf.Floor(u); if (back) u = 1f - u;
            People.Sample(lc, u * People.Length(lc), pb);
            People.Blend(pa, pb, loco, pa);
        }
        Vector3 wg = F.MultiplyPoint3x4(g);
        r.SetLod((wg - camPos).sqrMagnitude > 40f * 40f ? 1 : 0);
        r.Place(wg, fYaw + yaw);
        r.Apply(pa);
        // hands (crew space)
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
                tr = g0 + outv * 0.33f - Vector3.up * 0.10f; tl = g0 + outv * 0.17f + Vector3.up * 0.05f; wl = wr = k.w;
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
                tl = p.tyre - rx * 0.3f + Vector3.up * 0.05f; tr = p.tyre + rx * 0.3f + Vector3.up * 0.05f; wl = wr = w; break;
            }
            case On:
            {
                Vector3 wp = WheelPos(p.corner, lift), car = Vector3.right, carry = g + yq * new Vector3(0, 1.02f, 0.36f) * p.scale;
                if (Vector3.Dot(car, fw) < 0f) car = -car;
                float k1 = Smooth((s - 0.62f) / 0.36f);
                p.tyre = Vector3.Lerp(carry, wp, k1); p.tyreAx = Vector3.Slerp(fw, car, k1);
                float w = k.w * (1f - Smooth((s - TOn) / 0.1f));
                tl = p.tyre - rx * 0.3f + Vector3.up * 0.05f; tr = p.tyre + rx * 0.3f + Vector3.up * 0.05f; wl = wr = w; break;
            }
            case FJack: case RJack:
                tl = p.tip - rx * 0.13f; tr = p.tip + rx * 0.13f; wl = wr = k.w; break;
            case Helper:
            {
                float sx = gs * p.side; Vector3 h1 = new Vector3(sx * 0.64f, 0.62f + lift, -0.05f), h2 = new Vector3(sx * 0.64f, 0.62f + lift, -0.45f);
                if (Vector3.Dot(h1 - h2, rx) > 0f) { var t = h1; h1 = h2; h2 = t; }
                tl = h1; tr = h2; wl = wr = k.w; break;
            }
            case Rel: tl = Ro(new Vector3(-0.06f, 1.12f, 0.28f)); wl = 1f; break; // the release handset at the chest
            case Ext: tr = Ro(new Vector3(0.24f, 0.86f, 0.06f)); wr = 1f; break; // the extinguisher at the side
        }
        // arms raised (signal "done", celebration with fist pumps after the release) and the release man pointing the car out
        float rl = Mathf.Lerp(k.p.rl, k.q2.rl, x), rr = Mathf.Lerp(k.p.rr, k.q2.rr, x), pt = Mathf.Lerp(k.p.pt, k.q2.pt, x);
        float pump = st == St.Run && s > Release - 0.1f ? Mathf.Sin(rt * 8.5f + p.seed * 1.37f) : 0f;
        if (rl > 0.001f) { Vector3 up = Ro(new Vector3(-0.2f, 2.0f + 0.07f * pump, 0.1f)); tl = wl > 0f ? Vector3.Lerp(tl, up, rl) : up; wl = Mathf.Max(wl, rl); }
        if (rr > 0.001f) { Vector3 up = Ro(new Vector3(0.2f, 2.0f - 0.07f * pump, 0.1f)); tr = wr > 0f ? Vector3.Lerp(tr, up, rr) : up; wr = Mathf.Max(wr, rr); }
        if (pt > 0.001f) { Vector3 po = Ro(new Vector3(0.42f, 1.42f, 0.62f)); tr = wr > 0f ? Vector3.Lerp(tr, po, pt) : po; wr = Mathf.Max(wr, pt); }
        r.Arm(true, F.MultiplyPoint3x4(tl), wl);
        r.Arm(false, F.MultiplyPoint3x4(tr), wr);
        // head: look at what matters now, wandering a little
        {
            Vector3 lt = LookAt(p, s, lift) + rx * (1.2f * (Mathf.PerlinNoise(rt * 0.27f, p.seed * 0.71f) - 0.5f));
            r.LookAt(F.MultiplyPoint3x4(lt), 0.85f);
        }
        p.handL = Fi.MultiplyPoint3x4(r.Palm(true)); p.handR = Fi.MultiplyPoint3x4(r.Palm(false)); p.foreDir = Fi.MultiplyVector(r.ForearmDir(false));
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
                    float su = (p.corner & 1) == 0 ? 1f : -1f; Vector3 toNut = new Vector3(-gs * su, 0, 0), hf = p.foreDir;
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

    // ---------------------------------------------------------------- the player's own stop on camera
    // (GameManager.LateUpdate, chase / cockpit / halo views; not TV, replays or frozen shots) from just before the car
    // stops to just after the release: a short cinematic of the box. Shot 1: low front-quarter from the lane side, the car
    // rolling in and the wheels coming off; shot 2 (from the new wheels going on): wider and higher from the lane side,
    // the whole crew, the drop and the release light, with a slow drift. Back to the player's view (a cut, no swoop) as
    // the car pulls away.
    bool pitCamOn;
    bool PitCamera()
    {
        var r = pRun; var pc = pitCrew;
        bool on = pc != null && r != null && !r.frozen && pCrewRun == r && pc.Owns(-1) && pc.Running && cam != null;
        float s = on ? pc.StopClock : 0f;
        if (on && (r.Clock < -1.0 || s > PitCrew.ReleaseT + 0.4f)) on = false;
        if (!on) { if (pitCamOn) { pitCamOn = false; tvWasOn = true; camVel = Vector3.zero; } return false; }
        pitCamOn = true;
        Vector3 c = pc.BoxCentre, f = pc.BoxFwd.normalized, g = pc.BoxToGarage; g.y = 0; g = g.sqrMagnitude > 1e-6f ? g.normalized : Vector3.Cross(f, Vector3.up);
        Vector3 pos, look; float fov;
        if (s < 1.0f)
        {
            float k = Mathf.Clamp01((s + 1.0f) / 2.0f);
            pos = c + f * (pc.FrontEnd + 2.9f - 0.5f * k) - g * (3.3f - 0.3f * k) + Vector3.up * 1.05f;
            look = c + f * (0.9f - 0.4f * k) + g * 0.35f + Vector3.up * 0.42f; fov = 44f;
        }
        else
        {
            float k = Mathf.Clamp01((s - 1.0f) / 1.8f);
            pos = c + f * (1.4f - 1.2f * k) - g * 6.4f + Vector3.up * (2.05f + 0.15f * k);
            look = c + f * (0.5f - 0.3f * k) + g * 0.9f + Vector3.up * 0.75f; fov = 50f;
        }
        float aspect = Mathf.Max(0.5f, cam.aspect);
        if (aspect > 16f / 9f) fov = 2f * Mathf.Rad2Deg * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * (16f / 9f) / aspect);
        cam.transform.position = pos; cam.transform.LookAt(look); cam.fieldOfView = fov; cam.nearClipPlane = ChaseNear;
        CockpitBits(false);
        return true;
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
