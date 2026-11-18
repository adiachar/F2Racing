// PitGarage.cs — every team's garage along the pit lane, its pit box and its pit-wall stand across the lane, and the idle
// crews standing at every team's box. Trackside.cs builds the building shell (glass upper floor, roof, canopy, pillars) and
// leaves each bay's front open; this fills it like a real modern F1 pit box:
//  * white / light-grey panelled walls, grey epoxy floor, a ceiling of light panels and light strips; the team colour as
//    accents (wall stripe, LED line, floor band, beam cover, the team's name on the header, the back wall and the stand)
//  * the overhead gantry over the box: arms from the header, a beam with hose reels, air lines, two hanging timing screens,
//    a name board on the beam and the release light (PitCrew hangs its own, live one over it during a stop)
//  * tyre sets in blankets (black covers with a team band) on trolleys and lined up at the garage front, blanketed stacks,
//    a wheel rack, roll cabinets and a tool board, quick-lift jacks parked, wheel guns laid ready with their hoses to wall
//    reels, a fire extinguisher, a spare front wing, the engineers' desk with screens, a lollipop on its stand
//  * on the lane: the team's colour inside the white box lines and its name painted between the box and the garage
//  * the pit-wall stand: raised platform, desk with a bank of screens, stools, roof with a name fascia
//  * people: an idle crew in team fire suits and helmets at every team's box (6 people), engineers in headsets at the desk
//    and on the stand; the full animated crew (PitCrew.cs) replaces the idle one at a box while that box has a stop on.
//    Around the player's box the engineers and a mechanic are animated rigs (PeoplePits.cs), plus people walking.
// Duplicated, not rebuilt per bay: ONE mesh per level of detail (detailed / simple / closed shutter for the bays no team
// uses) is shared by every bay and drawn GPU-instanced (PitGarage.shader); per bay only the team colours and the row of its
// name in the atlas change (MaterialPropertyBlock, instanced properties). The idle crew is the same: the rigged people
// (People.cs) posed once and baked into one static mesh per LOD (no skinning, no animation), shared by every team's box,
// coloured per team by its palette (one draw call per box near the camera). Cost on a phone: the garages are 2-3
// instanced draws in all (detailed within ~36-50 m of the camera, simple beyond, closed shutters); the crews one draw per
// box within ~160 m (LOD1, ~2k triangles, beyond 28 m), the engineers one more (Medium and up). Nothing per frame but
// a distance check per bay (no allocations).
// Teams: every team of the grid has its box from the start (Livery.Teams by team index, PitGarage.TeamBay: the bays
// nearest the start line, the player's first), whatever the session; PitCrew.Init / SetTeams confirms the race's teams.
// The holder lives OUTSIDE the world root (a link object under the root destroys it with the world), so the light bake
// and the static batcher leave it alone. Each bay instance sits on the ground plane under its bay (fitted to WorldBuilder.H).
using System.Collections.Generic;
using UnityEngine;

public class PitGarage : MonoBehaviour
{
    // ---------------------------------------------------------------- state (of the live link; an older world's link only cleans up)
    GameObject owned; readonly List<Object> ownedAssets = new List<Object>();
    static PitGarage live;
    static Material mat; static Texture2D atlas;
    static readonly List<TSpot> tspots = new List<TSpot>(); // template spots recorded while the detailed mesh is built

    int gfx, hsI = 1; int[] order; Livery[] liv; string[] names; bool[] team, anim;
    Mesh tplFine, tplSimple, tplClosed; Mesh[] crewMesh = new Mesh[2], staffMesh = new Mesh[2];
    MeshFilter[] gMf; MeshRenderer[] gMr; MeshFilter[] cMf, sMf; MeshRenderer[] cMr, sMr; Vector3[] bayPos; Matrix4x4[] bayM;
    int[] gLod, cLod, sLod; MaterialPropertyBlock mpb; readonly Vector4[] pal = new Vector4[People.NReg];
    PeoplePits people; float fineDist = 40f;
    readonly List<TSpot> spotsL = new List<TSpot>();

    struct TSpot { public int kind, seed, pose; public Vector3 pos, fwd; } // bay frame (before the handedness flip)

    void OnDestroy()
    {
        if (owned != null) Destroy(owned);
        foreach (var o in ownedAssets) if (o != null) Destroy(o);
        ownedAssets.Clear(); owned = null;
        if (live == this) live = null;
    }

    // invented team names: a team's livery name, the player's livery name
    static readonly string[] Brands = { "ARKON", "VELTRA", "NOVAK", "HALCYON", "KESTREL", "ORBIS", "TALON", "ZEPHYR", "MERIDIAN", "STRATOS", "VORTEX", "AURELIA" };
    public static string Brand(int team) { int k = ((team % Brands.Length) + Brands.Length) % Brands.Length; return Brands[k] + " RACING"; }
    public static string PlayerBrand(Livery l) { string s = (l.name ?? "TEAM").ToUpperInvariant(); return s.Length <= 13 ? s + " RACING" : s; }
    public static string TeamName(Livery l) => PlayerBrand(l);

    // bays sorted by distance from the start line (the player's box first); PitCrew uses the same order
    public static int[] BayOrder(int N)
    {
        var bays = Trackside.Bays; int n = bays.Count;
        var idx = new List<int>(); for (int i = 0; i < n; i++) idx.Add(i);
        int D(int i) => Mathf.Min(bays[i].idx, N - bays[i].idx);
        idx.Sort((a, b) => { int c = D(a).CompareTo(D(b)); return c != 0 ? c : a.CompareTo(b); });
        return idx.ToArray();
    }
    // the box of rival team `team` (team index of the grid); the player's is order[0]
    public static int TeamBay(int[] order, int team)
    {
        int n = order.Length; return n > 1 ? order[1 + ((team % (n - 1)) + (n - 1)) % (n - 1)] : order[0];
    }

    static Livery Neutral() => new Livery(null, new Color(0.24f, 0.25f, 0.28f), new Color(0.86f, 0.87f, 0.88f), new Color(0.55f, 0.58f, 0.62f));

    // from Trackside.Build, once the bays exist: every grid team in its box, the player's box in the player's livery
    public static void Build(int N, Transform root, int gfxLevel)
    {
        var bays = Trackside.Bays; int n = bays.Count;
        if (n == 0 || root == null) return;
        var link = new GameObject("PitGarageLink"); link.transform.SetParent(root, false);
        var pg = link.AddComponent<PitGarage>(); live = pg;
        pg.Init(N, gfxLevel);
    }

    // the session's teams (PitCrew.Init): l[b] and nm[b] per bay, nm[b] == null = keep what the bay has
    public static void SetTeams(Livery[] l, string[] nm)
    {
        var pg = live;
        if (pg == null || pg.liv == null || l == null || nm == null || l.Length != pg.liv.Length || nm.Length != pg.liv.Length) return;
        bool changed = false;
        for (int b = 0; b < pg.liv.Length; b++)
        {
            if (nm[b] == null) continue;
            if (pg.team[b] && pg.names[b] == nm[b] && pg.liv[b].primary == l[b].primary && pg.liv[b].secondary == l[b].secondary && pg.liv[b].accent == l[b].accent) continue;
            pg.liv[b] = l[b]; pg.names[b] = nm[b]; pg.team[b] = true; changed = true;
        }
        if (changed) pg.ApplyTeams();
    }

    void Init(int N, int gfxLevel)
    {
        var bays = Trackside.Bays; int n = bays.Count;
        gfx = gfxLevel; order = BayOrder(N); fineDist = gfx >= 3 ? 50f : 36f;
        liv = new Livery[n]; names = new string[n]; team = new bool[n]; anim = new bool[n];
        for (int b = 0; b < n; b++) liv[b] = Neutral();
        var pl = Livery.Player[Livery.PlayerIndex]; int pb = order[0];
        liv[pb] = pl; names[pb] = PlayerBrand(pl); team[pb] = true;
        for (int t = 0; t < Livery.Teams.Length; t++) { int b = TeamBay(order, t); if (!team[b]) { liv[b] = Livery.Teams[t]; names[b] = TeamName(Livery.Teams[t]); team[b] = true; } }
        owned = new GameObject("PitGarages");
        mpb = new MaterialPropertyBlock();
        // the handedness of the bay frame (x along the lane, z to the garage): one pit side per circuit
        var b0 = bays[pb]; hsI = Vector3.Dot(Vector3.Cross(Vector3.up, b0.side), b0.fwd) >= 0f ? 1 : -1;
        float HW = HalfWidth(pb) - 0.08f, gd = b0.garageDist;
        // the shared meshes
        tspots.Clear();
        if (gfx >= 2) tplFine = Template(0, HW, gd);
        spotsL.Clear(); spotsL.AddRange(tspots);
        tplSimple = Template(1, HW, gd); tplClosed = Template(2, HW, gd);
        // the bays: one instance each, on the ground plane under it
        gMf = new MeshFilter[n]; gMr = new MeshRenderer[n]; gLod = new int[n]; bayPos = new Vector3[n]; bayM = new Matrix4x4[n];
        for (int b = 0; b < n; b++)
        {
            bayM[b] = BayMatrix(b, gd); bayPos[b] = bayM[b].MultiplyPoint3x4(new Vector3(0, 1.5f, gd * 0.5f));
            var go = new GameObject("PitGarage_" + b); go.transform.SetParent(owned.transform, false);
            go.transform.SetPositionAndRotation(bayM[b].GetColumn(3), bayM[b].rotation);
            gMf[b] = go.AddComponent<MeshFilter>(); gMr[b] = go.AddComponent<MeshRenderer>();
            gMr[b].sharedMaterial = Mat(); gMr[b].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; gMr[b].receiveShadows = false;
            gMr[b].lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; gMr[b].reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            gLod[b] = -1;
        }
        // the idle crews (baked people), Medium and up
        if (gfx >= 2 && People.Ready) BakeCrews();
        ApplyTeams();
        LateUpdate(); // levels of detail right away (shot mode, the first frame)
    }

    // the team colours and names into the atlas and the instances; the crews' palettes; the animated people
    void ApplyTeams()
    {
        int n = liv.Length;
        var row = new int[n]; for (int b = 0; b < n; b++) row[b] = -1;
        int r = 0; foreach (int b in order) if (team[b] && names[b] != null && r < NameRows - 1) row[b] = r++;
        DrawAtlas(row, names);
        Mat().mainTexture = atlas;
        for (int b = 0; b < n; b++)
        {
            var L = liv[b]; int rw = row[b] < 0 ? NameRows - 1 : row[b];
            Color p = L.primary.linear, s = L.secondary.linear, a = Accent(L).linear;
            mpb.Clear();
            mpb.SetVector("_TeamP", new Vector4(p.r, p.g, p.b, (NameRows - 1 - rw) * 16f / AtlasH));
            mpb.SetVector("_TeamS", new Vector4(s.r, s.g, s.b, 0f));
            mpb.SetVector("_TeamA", new Vector4(a.r, a.g, a.b, 0f));
            gMr[b].SetPropertyBlock(mpb);
            gLod[b] = -1; // re-pick the mesh (a bay may have become a team's)
        }
        // the animated engineers and mechanic at the player's box and two either side (Medium and up), people walking
        int pb = order[0];
        for (int b = 0; b < n; b++) anim[b] = gfx >= 2 && team[b] && Mathf.Abs(b - pb) <= 2;
        if (people != null) { Destroy(people.gameObject); people = null; }
        var spots = new List<PeoplePits.Spot>();
        for (int b = 0; b < n; b++)
        {
            if (!anim[b]) continue;
            foreach (var s in spotsL)
                spots.Add(new PeoplePits.Spot { kind = s.kind, seed = b * 7 + 3 + s.seed, pose = s.pose, liv = liv[b], pos = bayM[b].MultiplyPoint3x4(GB.Flip(s.pos, hsI)), fwd = bayM[b].MultiplyVector(GB.Flip(s.fwd, hsI)) });
        }
        if (gfx >= 2) people = PeoplePits.Create(owned.transform, spots, 4, order, liv);
        // the idle crews' colours
        if (cMr != null)
            for (int b = 0; b < n; b++)
            {
                if (cMr[b] != null) { People.Palette(pal, People.OCrew, liv[b], b * 17 + 5); mpb.Clear(); mpb.SetVectorArray("_Pal", pal); cMr[b].SetPropertyBlock(mpb); cLod[b] = -2; }
                if (sMr[b] != null) { People.Palette(pal, People.OEngineer, liv[b], b * 17 + 11); mpb.Clear(); mpb.SetVectorArray("_Pal", pal); sMr[b].SetPropertyBlock(mpb); sLod[b] = -2; }
            }
    }

    // the bay's frame on the ground: origin = the box centre on the ground plane fitted under the bay, x along the lane
    // (the viewer's right looking at the garage; the template is mirrored to match), z towards the garage
    static Matrix4x4 BayMatrix(int b, float gd)
    {
        var bb = Trackside.Bays[b];
        Vector3 c = bb.centre; c.y = 0f; Vector3 side = bb.side; side.y = 0f; side = side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, side).normalized;
        float h0 = WorldBuilder.H(c.x, c.z);
        float sx = (WorldBuilder.H(c.x + right.x * 4f, c.z + right.z * 4f) - WorldBuilder.H(c.x - right.x * 4f, c.z - right.z * 4f)) / 8f;
        Vector3 zf = c + side * (gd + 6f), zb = c - side * 8f;
        float sz = (WorldBuilder.H(zf.x, zf.z) - WorldBuilder.H(zb.x, zb.z)) / (gd + 14f);
        sx = Mathf.Clamp(sx, -0.15f, 0.15f); sz = Mathf.Clamp(sz, -0.15f, 0.15f);
        // the plane through the box centre with those slopes; the height at the centre from the fit of the three
        Vector3 X = (right + Vector3.up * sx).normalized, Z = (side + Vector3.up * sz).normalized, U = Vector3.Cross(Z, X).normalized;
        return Matrix4x4.TRS(new Vector3(c.x, h0, c.z), Quaternion.LookRotation(Z, U), Vector3.one);
    }

    // ---------------------------------------------------------------- per frame: levels of detail, the crews by distance
    void LateUpdate()
    {
        if (live != this || gMf == null) return;
        var cam = Camera.main; if (cam == null) return;
        Vector3 cp = cam.transform.position; int ab = PitCrew.ActiveBay;
        float f2 = fineDist * fineDist;
        for (int b = 0; b < gMf.Length; b++)
        {
            float d2 = (bayPos[b] - cp).sqrMagnitude;
            int want = !team[b] ? 2 : tplFine != null && d2 < f2 ? 0 : 1;
            if (want != gLod[b]) { gLod[b] = want; gMf[b].sharedMesh = want == 0 ? tplFine : want == 1 ? tplSimple : tplClosed; }
            if (cMf == null) continue;
            // the idle crew: hidden where the animated crew stands (a stop on, or waiting for a car), far away, or no team
            int wc = team[b] && b != ab && d2 < 160f * 160f ? (d2 < 28f * 28f ? 0 : 1) : -1;
            if (wc != cLod[b] && cMf[b] != null) { cLod[b] = wc; if (wc >= 0) cMf[b].sharedMesh = crewMesh[wc]; cMr[b].enabled = wc >= 0; }
            if (sMf[b] != null)
            {
                int ws = team[b] && !anim[b] && d2 < 120f * 120f ? (d2 < 28f * 28f ? 0 : 1) : -1;
                if (ws != sLod[b]) { sLod[b] = ws; if (ws >= 0) sMf[b].sharedMesh = staffMesh[ws]; sMr[b].enabled = ws >= 0; }
            }
        }
    }

    static Material Mat()
    {
        if (mat != null) return mat;
        var sh = Resources.Load<Shader>("PitGarage"); if (sh == null || !sh.isSupported) sh = Shader.Find("Sprites/Default");
        mat = new Material(sh) { name = "PitGarage", enableInstancing = true };
        return mat;
    }

    Mesh Emit(GB g, string name)
    {
        var mesh = new Mesh { name = name };
        if (g.v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(g.v); mesh.SetUVs(0, g.uv); mesh.SetUVs(1, g.tw); mesh.SetColors(g.c); mesh.SetTriangles(g.t, 0);
        mesh.RecalculateBounds(); mesh.UploadMeshData(true);
        ownedAssets.Add(mesh);
        return mesh;
    }

    static float HalfWidth(int b)
    {
        var bays = Trackside.Bays; float d = 8f;
        if (bays.Count > 1) { int o = b + 1 < bays.Count ? b + 1 : b - 1; d = Vector3.Distance(bays[b].centre, bays[o].centre); }
        return Mathf.Clamp(d * 0.5f, 3f, 6f);
    }

    // kind 0 detailed, 1 simple, 2 closed shutter (no team)
    Mesh Template(int kind, float HW, float gd)
    {
        var g = new GB { hs = hsI };
        if (kind == 0) { Detailed(g, HW, gd); Lane(g, gd, true); }
        else if (kind == 1) { Simple(g, HW, gd, gfx >= 2); Lane(g, gd, false); }
        else Closed(g, HW, gd);
        return Emit(g, kind == 0 ? "PitGarageFine" : kind == 1 ? "PitGarageSimple" : "PitGarageClosed");
    }

    // ---------------------------------------------------------------- the idle crews (rigged people posed once, baked)
    // lane crew: in fire suits and helmets, at the garage front between the box and the garage, facing the lane or the
    // incoming cars; ready crouches, folded arms, talking
    struct Who { public int body, gear, clip, clip2; public float mix, t, scale; public Vector3 pos; public float yaw; }
    static readonly float[,] CrewAt = { // x, z from the garage front (- = towards the lane), facing x, z, clip, clip2, mix
        { -2.9f, -1.25f, 0.25f, -1f, People.Crouch, People.Idle, 0.45f },
        { -1.9f, -1.50f, -0.35f, -1f, People.Idle, -1, 0f },
        { -0.7f, -1.20f, 0.30f, -1f, People.FoldArms, -1, 0f },
        { 0.5f, -1.45f, -0.65f, -1f, People.Talk, -1, 0f },
        { 1.7f, -1.25f, -1f, -0.45f, People.Idle, -1, 0f },
        { 2.8f, -1.40f, -0.2f, -1f, People.Crouch, People.Idle, 0.55f },
    };

    void BakeCrews()
    {
        int n = liv.Length; float gd = Trackside.Bays[order[0]].garageDist;
        var crew = new List<Who>(); var staff = new List<Who>();
        for (int i = 0; i < CrewAt.GetLength(0); i++)
        {
            int seed = 300 + i * 37, body = People.BodyOf(seed, 0.1f);
            Vector3 p = GB.Flip(new Vector3(CrewAt[i, 0], 0f, gd + CrewAt[i, 1]), hsI), f = GB.Flip(new Vector3(CrewAt[i, 2], 0f, CrewAt[i, 3]), hsI);
            crew.Add(new Who { body = body, gear = People.GHelmet, clip = (int)CrewAt[i, 4], clip2 = (int)CrewAt[i, 5], mix = CrewAt[i, 6], t = seed * 0.731f,
                               scale = People.HeightOf(seed, body), pos = p, yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg });
        }
        // the engineers of the detailed garage (desk, pit-wall stand): seated, headsets (the mechanic stays animated-only)
        foreach (var s in spotsL)
        {
            if (s.kind != 1) continue;
            int seed = 500 + s.seed * 13, body = People.BodyOf(seed, 0.35f); float sc = People.HeightOf(seed, body);
            Vector3 f = GB.Flip(s.fwd, hsI); f.y = 0f; f.Normalize();
            Vector3 p = GB.Flip(s.pos, hsI) + f * (0.3f * sc) + Vector3.up * 0.17f; // (seated: the pelvis over the stool, as PeoplePits)
            staff.Add(new Who { body = body, gear = People.GHeadset, clip = People.Sit, clip2 = -1, t = seed * 0.731f, scale = sc, pos = p, yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg });
        }
        for (int l = 0; l < 2; l++) { crewMesh[l] = Bake(crew, l, "PitIdleCrew" + l); staffMesh[l] = staff.Count > 0 ? Bake(staff, l, "PitIdleStaff" + l) : null; }
        cMf = new MeshFilter[n]; cMr = new MeshRenderer[n]; sMf = new MeshFilter[n]; sMr = new MeshRenderer[n]; cLod = new int[n]; sLod = new int[n];
        for (int b = 0; b < n; b++)
        {
            cLod[b] = sLod[b] = -2;
            if (!team[b] && !CanBeTeam(b)) continue;
            Make(b, "PitIdleCrew_", crewMesh[0], out cMf[b], out cMr[b]);
            if (staffMesh[0] != null) Make(b, "PitIdleStaff_", staffMesh[0], out sMf[b], out sMr[b]);
        }
    }
    // a bay that may get a team later (SetTeams): the bays the team mapping can reach
    bool CanBeTeam(int b) { for (int k = 0; k < order.Length && k <= 24; k++) if (order[k] == b) return true; return false; }

    void Make(int b, string name, Mesh m, out MeshFilter mf, out MeshRenderer mr)
    {
        var go = new GameObject(name + b); go.layer = LightBake.CarLayer; // lit by the cars' sun, like the rigged people
        go.transform.SetParent(owned.transform, false); go.transform.SetPositionAndRotation(bayM[b].GetColumn(3), bayM[b].rotation);
        mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = m;
        mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = People.Mat();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        mr.enabled = false;
    }

    // people posed (a clip, or two mixed) on a temporary rig, skinned once on the CPU (SkinnedMeshRenderer.BakeMesh) and
    // merged into one static mesh in the bay frame; vertex colour R = the region (People.shader's palette index)
    Mesh Bake(List<Who> who, int lod, string name)
    {
        var V = new List<Vector3>(); var Nn = new List<Vector3>(); var Cc = new List<Color32>(); var T = new List<int>();
        var tmp = new Mesh(); var pa = new People.PoseBuf(); var pb = new People.PoseBuf();
        foreach (var w in who)
        {
            var rig = new People.Rig(null, w.body, w.gear, "PitBake") { scale = w.scale };
            rig.SetLod(lod); rig.Place(Vector3.zero, 0f);
            People.Sample(w.clip, w.t, pa);
            if (w.clip2 >= 0 && w.mix > 0.001f) { People.Sample(w.clip2, w.t, pb); People.Blend(pa, pb, w.mix, pa); }
            rig.Apply(pa);
            var smr = rig.go.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
            {
                smr.BakeMesh(tmp, true);
                var cols = smr.sharedMesh.colors32; var vs = tmp.vertices; var ns = tmp.normals; var ts = tmp.triangles;
                if (cols.Length == vs.Length && ns.Length == vs.Length)
                {
                    var q = Quaternion.Euler(0f, w.yaw, 0f); int b0 = V.Count;
                    for (int i = 0; i < vs.Length; i++) { V.Add(w.pos + q * vs[i]); Nn.Add(q * ns[i]); Cc.Add(cols[i]); }
                    for (int i = 0; i < ts.Length; i++) T.Add(b0 + ts[i]);
                }
            }
            rig.go.SetActive(false); rig.Destroy();
        }
        Destroy(tmp);
        var m = new Mesh { name = name };
        if (V.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.SetVertices(V); m.SetNormals(Nn); m.SetColors(Cc); m.SetTriangles(T, 0);
        m.RecalculateBounds(); m.UploadMeshData(true);
        ownedAssets.Add(m);
        return m;
    }

    // ---------------------------------------------------------------- palette
    static Color C(float r, float g, float b) => new Color(r, g, b);
    static readonly Color WallW = C(0.90f, 0.91f, 0.92f), Floor = C(0.52f, 0.53f, 0.55f), Ceil = C(0.80f, 0.81f, 0.83f), Graph = C(0.20f, 0.21f, 0.23f),
                          Dark = C(0.11f, 0.11f, 0.12f), Black = C(0.04f, 0.04f, 0.045f), Steel = C(0.56f, 0.57f, 0.60f), Blanket = C(0.06f, 0.06f, 0.065f),
                          Rubber = C(0.05f, 0.05f, 0.055f), ExtRed = C(0.78f, 0.06f, 0.05f), AirYellow = C(0.95f, 0.75f, 0.10f), Kick = C(0.30f, 0.31f, 0.33f),
                          Light = C(1f, 0.99f, 0.96f);
    // the team's colours: markers, not colours (GB stores the vertex white and the slot in uv1; PitGarage.shader multiplies
    // the instance's colour in)
    static readonly Color TP = new Color(-1f, 0f, 0f, 1f), TS = new Color(-2f, 0f, 0f, 1f), TA = new Color(-3f, 0f, 0f, 1f);
    static Color Accent(Livery L) { var a = L.accent; float m = Mathf.Max(a.r, Mathf.Max(a.g, a.b)); return m < 0.25f ? L.secondary : a; }

    // ---------------------------------------------------------------- a detailed bay (bay frame: x along the lane, z towards the garage, y up)
    const float DZ = 8.8f, CH = 4.4f;
    static void Detailed(GB g, float HW, float gd)
    {
        float zF = gd, zB = gd + DZ;
        Color P = TP, S = TS, Ac = TA;
        int seed = 0;
        Interior(g, HW, zF, zB, P, Ac, true);
        Gantry(g, HW, zF, P, Ac, true);
        g.light = 1f;
        // tyre trolleys with blanketed sets (front corners), blanketed stacks behind them
        TyreTrolley(g, new Vector3(-HW + 0.62f, 0, zF + 1.25f), P);
        TyreTrolley(g, new Vector3(HW - 0.62f, 0, zF + 1.25f), P);
        for (int s = -1; s <= 1; s += 2)
            for (int k = 0; k < 3; k++)
            {
                Vector3 c = new Vector3(s * (HW - 0.48f), 0.02f + k * 0.37f, zF + 3.25f);
                g.CylV(c, 0.37f, 0.36f, 12, Blanket, Blanket, GB.Cell(6));
                g.CylV(c + Vector3.up * 0.12f, 0.375f, 0.12f, 12, P, P, GB.White, false);
            }
        // wheel rack (back left): bare wheels standing, faces to the opening
        {
            float a0 = -HW + 0.15f, a1 = a0 + 2.5f, z = zB - 0.38f;
            for (int k = 0; k < 4; k++) g.Box(new Vector3(Mathf.Lerp(a0, a1, k / 3f), 0.9f, z), new Vector3(0.03f, 0.9f, 0.3f), Steel);
            for (int k = 0; k < 2; k++) g.Box(new Vector3((a0 + a1) * 0.5f, 0.06f + k * 0.86f, z), new Vector3((a1 - a0) * 0.5f, 0.025f, 0.3f), Graph);
            for (int k = 0; k < 2; k++) for (int j = 0; j < 3; j++)
                    g.Wheel(new Vector3(a0 + 0.42f + j * 0.83f, 0.085f + k * 0.86f + 0.36f, z), 90f, 0.36f, 0.36f, Rubber, C(0.10f, 0.10f, 0.11f), Steel);
        }
        // roll cabinets (back right), worktop, tool board above
        {
            float a1 = HW - 0.15f;
            for (int k = 0; k < 3; k++)
            {
                float ca = a1 - 0.4f - k * 0.82f; Vector3 c = new Vector3(ca, 0.52f, zB - 0.34f);
                g.Box(c, new Vector3(0.39f, 0.5f, 0.28f), Graph);
                for (int d = 0; d < 4; d++)
                {
                    float y = 0.2f + d * 0.21f, z = zB - 0.625f;
                    g.Quad(new Vector3(ca - 0.36f, y, z), new Vector3(ca + 0.36f, y, z), new Vector3(ca + 0.36f, y + 0.17f, z), new Vector3(ca - 0.36f, y + 0.17f, z), Vector3.back, C(0.30f, 0.31f, 0.33f), GB.White);
                    g.Quad(new Vector3(ca - 0.2f, y + 0.12f, z - 0.005f), new Vector3(ca + 0.2f, y + 0.12f, z - 0.005f), new Vector3(ca + 0.2f, y + 0.15f, z - 0.005f), new Vector3(ca - 0.2f, y + 0.15f, z - 0.005f), Vector3.back, Steel, GB.White);
                }
                g.Quad(new Vector3(ca - 0.39f, 0.94f, zB - 0.626f), new Vector3(ca + 0.39f, 0.94f, zB - 0.626f), new Vector3(ca + 0.39f, 1.0f, zB - 0.626f), new Vector3(ca - 0.39f, 1.0f, zB - 0.626f), Vector3.back, P, GB.White);
            }
            g.Box(new Vector3(a1 - 1.23f, 1.04f, zB - 0.34f), new Vector3(1.24f, 0.025f, 0.3f), Black);
            float tz = zB - 0.03f;
            g.Quad(new Vector3(a1 - 2.4f, 1.35f, tz), new Vector3(a1 - 0.05f, 1.35f, tz), new Vector3(a1 - 0.05f, 2.25f, tz), new Vector3(a1 - 2.4f, 2.25f, tz), Vector3.back, Graph, GB.Cell(7));
            for (int k = 0; k < 7; k++)
            {
                float ta = a1 - 2.2f + k * 0.33f, th = 0.18f + 0.1f * ((k * 5) % 3);
                g.Box(new Vector3(ta, 1.8f, tz - 0.03f), new Vector3(0.025f, th * 0.5f, 0.02f), k % 3 == 0 ? Ac : Steel);
            }
        }
        // engineers' desk (back middle): screens to the opening, engineers on stools with their backs to the lane
        {
            float zd = zB - 1.5f;
            g.Box(new Vector3(0, 0.37f, zd), new Vector3(1.25f, 0.37f, 0.3f), Graph);
            g.Box(new Vector3(0, 0.76f, zd), new Vector3(1.3f, 0.02f, 0.34f), Black);
            for (int k = -1; k <= 1; k++) g.Screen(new Vector3(k * 0.78f, 1.06f, zd + 0.1f), 0.32f, 0.2f, Vector3.back, (k & 1) == 0 ? 4 : 5, true);
            for (int k = -1; k <= 1; k += 2)
            {
                Vector3 st = new Vector3(k * 0.55f, 0, zd - 0.85f);
                g.CylV(st, 0.03f, 0.6f, 6, Steel, Steel, GB.White); g.CylV(st + Vector3.up * 0.6f, 0.17f, 0.05f, 8, Black, Black, GB.White);
                g.Person(1, seed + k, st, Vector3.forward, 0);
            }
        }
        // a mechanic watching from the cabinets (arms crossed)
        g.Person(2, seed + 5, new Vector3(HW - 1.5f, 0, zB - 1.25f), new Vector3(-0.35f, 0, -1f), 1);
        // spare front wing on its stand (left wall), tool trolley (right)
        {
            Vector3 c = new Vector3(-HW + 0.35f, 0, zF + 5.2f);
            g.Box(c + new Vector3(0, 0.03f, 0), new Vector3(0.22f, 0.03f, 0.7f), Graph);
            g.Box(c + new Vector3(0, 0.3f, 0), new Vector3(0.02f, 0.27f, 0.02f), Steel);
            g.Box(c + new Vector3(0, 0.62f, 0), new Vector3(0.05f, 0.08f, 0.85f), P);
            g.Box(c + new Vector3(0.01f, 0.76f, 0), new Vector3(0.04f, 0.06f, 0.8f), Black);
            for (int s = -1; s <= 1; s += 2) g.Box(c + new Vector3(0, 0.66f, s * 0.86f), new Vector3(0.09f, 0.16f, 0.015f), Black);
            Vector3 t = new Vector3(HW - 0.45f, 0, zF + 5.0f);
            g.Box(t + new Vector3(0, 0.48f, 0), new Vector3(0.28f, 0.4f, 0.22f), Graph);
            g.Box(t + new Vector3(0, 0.9f, 0), new Vector3(0.3f, 0.02f, 0.24f), Black);
            g.Box(t + new Vector3(0.05f, 0.95f, 0.02f), new Vector3(0.12f, 0.03f, 0.04f), Ac);
            for (int k = 0; k < 4; k++) g.Box(t + new Vector3((k & 1) * 0.4f - 0.2f, 0.04f, (k >> 1) * 0.36f - 0.18f), new Vector3(0.03f, 0.04f, 0.03f), Black);
        }
        // quick-lift jacks parked along the front, wheel guns laid ready with hoses to the wall reels, extinguisher
        for (int s = -1; s <= 1; s += 2)
        {
            Vector3 jb = new Vector3(s * 1.7f, 0, zF + 0.45f);
            g.Box(jb + new Vector3(0, 0.08f, 0), new Vector3(0.18f, 0.07f, 0.22f), S);
            g.Box(jb + new Vector3(0, 0.2f, 0), new Vector3(0.1f, 0.05f, 0.1f), Black);
            g.Seg(jb + new Vector3(0, 0.12f, 0.15f), jb + new Vector3(0, 0.22f, 1.45f), 0.045f, Black);
            g.Seg(jb + new Vector3(-0.18f, 0.22f, 1.45f), jb + new Vector3(0.18f, 0.22f, 1.45f), 0.04f, Black);
            Vector3 gp = new Vector3(s * 0.75f, 0.06f, zF + 0.4f);
            g.Box(gp, new Vector3(0.07f, 0.06f, 0.16f), Ac, s * 20f);
            g.Box(gp + new Vector3(0, -0.01f, -0.16f), new Vector3(0.025f, 0.035f, 0.05f), Steel, s * 20f);
            Vector3 reel = new Vector3(s * (HW - 0.12f), 2.3f, zF + 0.9f);
            g.Wheel(reel, 0f, 0.26f, 0.14f, Black, Steel, Graph);
            Vector3 h0 = gp + new Vector3(-s * 0.06f, 0, 0.16f), h1 = new Vector3(s * 1.6f, 0.03f, zF + 0.95f), h2 = new Vector3(s * (HW - 0.2f), 0.03f, zF + 0.9f);
            g.Seg(h0, h1, 0.035f, Black); g.Seg(h1, h2, 0.035f, Black); g.Seg(h2, reel + new Vector3(-s * 0.1f, -0.25f, 0), 0.035f, Black);
        }
        {
            Vector3 e = new Vector3(-HW + 0.28f, 0, zF + 0.3f);
            g.CylV(e, 0.09f, 0.55f, 8, ExtRed, ExtRed, GB.White);
            g.Box(e + new Vector3(0, 0.61f, 0), new Vector3(0.03f, 0.05f, 0.05f), Black);
            g.Seg(e + new Vector3(0.03f, 0.62f, 0.04f), e + new Vector3(0.1f, 0.25f, 0.11f), 0.02f, Black);
            g.Box(e + new Vector3(-0.05f, 1.1f, 0), new Vector3(0.01f, 0.12f, 0.12f), ExtRed, 0f, 1.3f);
        }
        PitWallStand(g, gd, P, S, true, seed);
    }

    // walls, floor, ceiling, header of one bay
    static void Interior(GB g, float HW, float zF, float zB, Color P, Color Ac, bool fine)
    {
        g.light = 1f;
        Vector4 wc = fine ? GB.Cell(1) : GB.White, fc = GB.Cell(2);
        // floor: epoxy tiles, the team band along the front, a painted car outline
        int na = fine ? 4 : 1, nz = fine ? 4 : 1;
        for (int i = 0; i < na; i++) for (int j = 0; j < nz; j++)
            {
                float a0 = Mathf.Lerp(-HW, HW, i / (float)na), a1 = Mathf.Lerp(-HW, HW, (i + 1f) / na), z0 = Mathf.Lerp(zF, zB, j / (float)nz), z1 = Mathf.Lerp(zF, zB, (j + 1f) / nz);
                g.Quad(new Vector3(a0, 0.04f, z0), new Vector3(a1, 0.04f, z0), new Vector3(a1, 0.04f, z1), new Vector3(a0, 0.04f, z1), Vector3.up, Floor, fc);
            }
        g.Quad(new Vector3(-HW, 0.045f, zF + 0.12f), new Vector3(HW, 0.045f, zF + 0.12f), new Vector3(HW, 0.045f, zF + 0.3f), new Vector3(-HW, 0.045f, zF + 0.3f), Vector3.up, P, GB.White);
        if (fine)
        {
            float oa = 1.05f, o0 = zF + 1.0f, o1 = zF + 6.3f, lw = 0.05f; Color lc = C(0.80f, 0.81f, 0.82f);
            g.Quad(new Vector3(-oa, 0.045f, o0), new Vector3(-oa + lw, 0.045f, o0), new Vector3(-oa + lw, 0.045f, o1), new Vector3(-oa, 0.045f, o1), Vector3.up, lc, GB.White);
            g.Quad(new Vector3(oa - lw, 0.045f, o0), new Vector3(oa, 0.045f, o0), new Vector3(oa, 0.045f, o1), new Vector3(oa - lw, 0.045f, o1), Vector3.up, lc, GB.White);
            g.Quad(new Vector3(-oa, 0.045f, o1 - lw), new Vector3(oa, 0.045f, o1 - lw), new Vector3(oa, 0.045f, o1), new Vector3(-oa, 0.045f, o1), Vector3.up, lc, GB.White);
        }
        // side walls and back wall: panels, kick strip, team stripe, LED line
        int pz = fine ? 4 : 1, py = fine ? 2 : 1;
        for (int s = -1; s <= 1; s += 2)
        {
            float a = s * HW; Vector3 n = new Vector3(-s, 0, 0);
            for (int i = 0; i < pz; i++) for (int j = 0; j < py; j++)
                {
                    float z0 = Mathf.Lerp(zF, zB, i / (float)pz), z1 = Mathf.Lerp(zF, zB, (i + 1f) / pz), y0 = Mathf.Lerp(0.25f, CH, j / (float)py), y1 = Mathf.Lerp(0.25f, CH, (j + 1f) / py);
                    g.Quad(new Vector3(a, y0, z0), new Vector3(a, y0, z1), new Vector3(a, y1, z1), new Vector3(a, y1, z0), n, WallW, wc);
                }
            g.Quad(new Vector3(a, 0, zF), new Vector3(a, 0, zB), new Vector3(a, 0.25f, zB), new Vector3(a, 0.25f, zF), n, Kick, GB.White);
            float ai = a - s * 0.01f;
            g.Quad(new Vector3(ai, 1.15f, zF), new Vector3(ai, 1.15f, zB), new Vector3(ai, 1.3f, zB), new Vector3(ai, 1.3f, zF), n, P, GB.White, 1.05f);
            g.Quad(new Vector3(ai, 3.92f, zF), new Vector3(ai, 3.92f, zB), new Vector3(ai, 3.97f, zB), new Vector3(ai, 3.97f, zF), n, Ac, GB.White, 1.8f);
        }
        {
            Vector3 n = Vector3.back; int pa = fine ? 4 : 1;
            for (int i = 0; i < pa; i++) for (int j = 0; j < py; j++)
                {
                    float a0 = Mathf.Lerp(-HW, HW, i / (float)pa), a1 = Mathf.Lerp(-HW, HW, (i + 1f) / pa), y0 = Mathf.Lerp(0.25f, CH, j / (float)py), y1 = Mathf.Lerp(0.25f, CH, (j + 1f) / py);
                    g.Quad(new Vector3(a0, y0, zB), new Vector3(a1, y0, zB), new Vector3(a1, y1, zB), new Vector3(a0, y1, zB), n, WallW, wc);
                }
            g.Quad(new Vector3(-HW, 0, zB), new Vector3(HW, 0, zB), new Vector3(HW, 0.25f, zB), new Vector3(-HW, 0.25f, zB), n, Kick, GB.White);
            float zi = zB - 0.01f;
            g.Quad(new Vector3(-HW, 1.15f, zi), new Vector3(HW, 1.15f, zi), new Vector3(HW, 1.3f, zi), new Vector3(-HW, 1.3f, zi), n, P, GB.White, 1.05f);
            g.Quad(new Vector3(-HW, 3.92f, zi), new Vector3(HW, 3.92f, zi), new Vector3(HW, 3.97f, zi), new Vector3(-HW, 3.97f, zi), n, Ac, GB.White, 1.8f);
            // name board (white letters on black) with a team-colour underline, a big timing screen beside it
            float bw = 2.1f, by = 3.0f, bz = zB - 0.02f;
            g.Box(new Vector3(0, by + 0.13f, zB - 0.03f), new Vector3(bw + 0.08f, 0.2f, 0.02f), Black);
            g.NameQuad(new Vector3(-bw, by, bz - 0.02f), new Vector3(bw, by, bz - 0.02f), new Vector3(bw, by + 0.2625f, bz - 0.02f), new Vector3(-bw, by + 0.2625f, bz - 0.02f), n, 1.6f);
            g.Quad(new Vector3(-bw, by - 0.1f, bz - 0.025f), new Vector3(bw, by - 0.1f, bz - 0.025f), new Vector3(bw, by - 0.05f, bz - 0.025f), new Vector3(-bw, by - 0.05f, bz - 0.025f), n, P, GB.White, 1.2f);
            if (fine) g.Screen(new Vector3(0, 2.25f, zB - 0.06f), 0.75f, 0.42f, n, 4, false);
        }
        // ceiling: panels and three light strips; the roller door rolled up behind the header
        {
            Vector3 n = Vector3.down; int ca = fine ? 2 : 1;
            for (int i = 0; i < ca; i++)
            {
                float a0 = Mathf.Lerp(-HW, HW, i / (float)ca), a1 = Mathf.Lerp(-HW, HW, (i + 1f) / ca);
                g.Quad(new Vector3(a0, CH, zF), new Vector3(a1, CH, zF), new Vector3(a1, CH, zB), new Vector3(a0, CH, zB), n, Ceil, GB.White);
            }
            for (int k = -1; k <= 1; k++)
            {
                if (!fine && k != 0) continue;
                float a = k * HW * 0.55f;
                g.Quad(new Vector3(a - 0.16f, CH - 0.03f, zF + 0.6f), new Vector3(a + 0.16f, CH - 0.03f, zF + 0.6f), new Vector3(a + 0.16f, CH - 0.03f, zB - 0.4f), new Vector3(a - 0.16f, CH - 0.03f, zB - 0.4f), n, Light, GB.Cell(3), 1.95f);
            }
            if (fine) g.Box(new Vector3(0, CH - 0.2f, zF + 0.3f), new Vector3(HW, 0.18f, 0.2f), Graph);
        }
        // header over the opening: white fascia, team stripe and the name; jambs
        {
            g.light = 0.95f; Vector3 n = Vector3.back; float z = zF - 0.02f, w = HW + 0.1f;
            g.Quad(new Vector3(-w, 3.95f, z), new Vector3(w, 3.95f, z), new Vector3(w, 4.52f, z), new Vector3(-w, 4.52f, z), n, WallW, GB.White);
            g.Quad(new Vector3(-w, 3.95f, z), new Vector3(w, 3.95f, z), new Vector3(w, 3.95f, zF + 0.4f), new Vector3(-w, 3.95f, zF + 0.4f), Vector3.down, WallW, GB.White);
            g.Quad(new Vector3(-w, 3.95f, z - 0.01f), new Vector3(w, 3.95f, z - 0.01f), new Vector3(w, 4.06f, z - 0.01f), new Vector3(-w, 4.06f, z - 0.01f), n, P, GB.White, 1.1f);
            g.Quad(new Vector3(-1.95f, 4.12f, z - 0.01f), new Vector3(1.95f, 4.12f, z - 0.01f), new Vector3(1.95f, 4.48f, z - 0.01f), new Vector3(-1.95f, 4.48f, z - 0.01f), n, Black, GB.White);
            g.NameQuad(new Vector3(-1.84f, 4.185f, z - 0.02f), new Vector3(1.84f, 4.185f, z - 0.02f), new Vector3(1.84f, 4.415f, z - 0.02f), new Vector3(-1.84f, 4.415f, z - 0.02f), n, 1.6f);
            for (int s = -1; s <= 1; s += 2) g.Box(new Vector3(s * (HW - 0.02f), 1.975f, zF + 0.1f), new Vector3(0.1f, 1.975f, 0.14f), WallW);
            g.light = 1f;
        }
    }

    // the gantry over the box: arms from the header, a beam (team cover, LED underside), hose reels, air lines, two hanging
    // screens, the release-light bracket at x 3.3 (PitCrew hangs the light at bay (3.3, 2.95, 0.6))
    static void Gantry(GB g, float HW, float zF, Color P, Color Ac, bool fine)
    {
        g.light = 0.95f;
        float zb = 0.6f, yb = 3.75f, ea = Mathf.Min(3.6f, HW - 0.2f);
        for (int s = -1; s <= 1; s += 2)
        {
            g.Box(new Vector3(s * ea, 3.88f, (zF + zb) * 0.5f), new Vector3(0.07f, 0.07f, (zF - zb) * 0.5f), Graph);
            if (fine) g.Seg(new Vector3(s * ea, 3.88f, zF - 0.9f), new Vector3(s * ea, 4.4f, zF - 0.02f), 0.05f, Graph);
        }
        g.Box(new Vector3(0, yb, zb), new Vector3(ea + 0.1f, 0.11f, 0.11f), Graph);
        g.Quad(new Vector3(-ea - 0.1f, yb - 0.09f, zb - 0.115f), new Vector3(ea + 0.1f, yb - 0.09f, zb - 0.115f), new Vector3(ea + 0.1f, yb + 0.09f, zb - 0.115f), new Vector3(-ea - 0.1f, yb + 0.09f, zb - 0.115f), Vector3.back, P, GB.White, 1.05f);
        g.Quad(new Vector3(-ea, yb - 0.115f, zb - 0.04f), new Vector3(ea, yb - 0.115f, zb - 0.04f), new Vector3(ea, yb - 0.115f, zb + 0.04f), new Vector3(-ea, yb - 0.115f, zb + 0.04f), Vector3.down, Ac, GB.White, 1.7f);
        // the team's name board on top of the beam (to the lane) and the release light at its end (dark: PitCrew hangs the
        // live one, a little larger, over it during a stop)
        g.Box(new Vector3(0, yb + 0.27f, zb), new Vector3(1.9f, 0.15f, 0.03f), Black);
        g.NameQuad(new Vector3(-1.8f, yb + 0.16f, zb - 0.035f), new Vector3(1.8f, yb + 0.16f, zb - 0.035f), new Vector3(1.8f, yb + 0.385f, zb - 0.035f), new Vector3(-1.8f, yb + 0.385f, zb - 0.035f), Vector3.back, 1.5f);
        g.Box(new Vector3(3.3f, 2.95f, zb), new Vector3(0.05f, 0.12f, 0.24f), Black);
        for (int k = -1; k <= 1; k += 2)
        {
            float lz = zb + k * 0.12f, lx = 3.3f - 0.052f;
            g.Quad(new Vector3(lx, 2.88f, lz - 0.075f), new Vector3(lx, 2.88f, lz + 0.075f), new Vector3(lx, 3.03f, lz + 0.075f), new Vector3(lx, 3.03f, lz - 0.075f), Vector3.left, k < 0 ? C(0.55f, 0.05f, 0.04f) : C(0.03f, 0.10f, 0.05f), GB.White, 1.3f);
        }
        if (!fine) return;
        float[] reels = { -2.0f, -1.4f, 1.4f, 2.0f };
        foreach (float ra in reels) { g.Wheel(new Vector3(ra, yb - 0.24f, zb), 0f, 0.12f, 0.09f, Black, Steel, Graph); g.Seg(new Vector3(ra, yb - 0.12f, zb), new Vector3(ra, yb - 0.03f, zb), 0.03f, Steel); }
        for (int s = -1; s <= 1; s += 2)
        {
            g.Seg(new Vector3(s * 2.9f, yb + 0.12f, zb + 0.05f), new Vector3(s * 2.9f, 4.35f, zF - 0.05f), 0.03f, s < 0 ? AirYellow : Black);
            g.Seg(new Vector3(s * 3.05f, yb + 0.12f, zb + 0.05f), new Vector3(s * 3.05f, 4.35f, zF - 0.05f), 0.03f, Black);
        }
        g.Seg(new Vector3(-ea, yb + 0.13f, zb + 0.07f), new Vector3(ea, yb + 0.13f, zb + 0.07f), 0.035f, AirYellow);
        foreach (float ma in new[] { -2.7f, 0.3f })
        {
            g.Seg(new Vector3(ma - 0.2f, yb - 0.11f, zb), new Vector3(ma - 0.2f, 3.5f, zb), 0.02f, Steel);
            g.Seg(new Vector3(ma + 0.2f, yb - 0.11f, zb), new Vector3(ma + 0.2f, 3.5f, zb), 0.02f, Steel);
            g.Box(new Vector3(ma, 3.27f, zb), new Vector3(0.42f, 0.25f, 0.035f), Black);
            g.Screen(new Vector3(ma, 3.27f, zb - 0.037f), 0.38f, 0.21f, Vector3.back, 4, false);
            g.Screen(new Vector3(ma, 3.27f, zb + 0.037f), 0.38f, 0.21f, Vector3.forward, 5, false);
        }
        g.Seg(new Vector3(3.3f, yb - 0.11f, zb), new Vector3(3.3f, 3.08f, zb), 0.03f, Steel);
        g.light = 1f;
    }

    static void TyreTrolley(GB g, Vector3 c, Color P)
    {
        g.Box(c + new Vector3(0, 0.15f, 0), new Vector3(0.3f, 0.03f, 0.84f), Steel);
        g.Box(c + new Vector3(0, 0.96f, 0), new Vector3(0.3f, 0.02f, 0.84f), Steel);
        for (int k = 0; k < 4; k++)
        {
            Vector3 q = c + new Vector3((k & 1) * 0.56f - 0.28f, 0, (k >> 1) * 1.6f - 0.8f);
            g.Box(q + new Vector3(0, 0.98f, 0), new Vector3(0.02f, 0.88f, 0.02f), Steel);
            g.Box(q + new Vector3(0, 0.05f, 0), new Vector3(0.035f, 0.05f, 0.035f), Black);
        }
        Vector3 box = c + new Vector3(0, 1.98f, 0);
        g.Box(box, new Vector3(0.16f, 0.1f, 0.26f), Graph);
        g.Quad(box + new Vector3(-0.161f, -0.02f, -0.12f), box + new Vector3(-0.161f, -0.02f, -0.04f), box + new Vector3(-0.161f, 0.04f, -0.04f), box + new Vector3(-0.161f, 0.04f, -0.12f), Vector3.left, C(1f, 0.15f, 0.1f), GB.White, 1.9f);
        g.Quad(box + new Vector3(0.161f, -0.02f, -0.12f), box + new Vector3(0.161f, -0.02f, -0.04f), box + new Vector3(0.161f, 0.04f, -0.04f), box + new Vector3(0.161f, 0.04f, -0.12f), Vector3.right, C(0.2f, 1f, 0.3f), GB.White, 1.9f);
        for (int lv = 0; lv < 2; lv++) for (int k = -1; k <= 1; k += 2)
            {
                Vector3 t = c + new Vector3(0, 0.55f + lv * 0.81f, k * 0.4f);
                g.Wheel(t, 0f, 0.375f, 0.4f, Blanket, Blanket, Blanket, GB.Cell(6));
                g.Wheel(t, 0f, 0.38f, 0.13f, P, P, P, GB.White, false);
                g.Seg(t + new Vector3(0.12f, 0.36f, 0), box + new Vector3(0.1f, -0.1f, k * 0.15f), 0.018f, Black);
            }
    }

    static void PitWallStand(GB g, float gd, Color P, Color S, bool fine, int seed)
    {
        float zl = gd - 12f, z0 = zl + 0.12f, z1 = zl + 1.68f, zc = (z0 + z1) * 0.5f, hz = (z1 - z0) * 0.5f, py = 0.54f, ha = 2.7f;
        g.light = 0.95f;
        g.Box(new Vector3(0, py * 0.5f, zc), new Vector3(ha, py * 0.5f, hz), Graph);
        g.Quad(new Vector3(-ha, 0.3f, z1 + 0.005f), new Vector3(ha, 0.3f, z1 + 0.005f), new Vector3(ha, 0.42f, z1 + 0.005f), new Vector3(-ha, 0.42f, z1 + 0.005f), Vector3.forward, P, GB.White, 1.05f);
        // desk and screens (facing the engineers, i.e. the pit lane)
        g.Box(new Vector3(0, py + 0.39f, zl + 0.38f), new Vector3(ha - 0.2f, 0.39f, 0.2f), Black);
        if (fine)
        {
            for (int k = 0; k < 4; k++) { float a = -1.8f + k * 1.2f; g.Box(new Vector3(a, py + 1.06f, zl + 0.33f), new Vector3(0.3f, 0.19f, 0.03f), Black); g.Screen(new Vector3(a, py + 1.06f, zl + 0.362f), 0.27f, 0.165f, Vector3.forward, 4 + (k & 1), false); }
            for (int k = -1; k <= 1; k += 2) { float a = k * 1.2f; g.Box(new Vector3(a, py + 1.52f, zl + 0.33f), new Vector3(0.4f, 0.24f, 0.03f), Black); g.Screen(new Vector3(a, py + 1.52f, zl + 0.362f), 0.37f, 0.21f, Vector3.forward, 5 - (k > 0 ? 1 : 0), false); }
            g.Seg(new Vector3(-ha + 0.1f, py + 0.5f, z1 - 0.05f), new Vector3(ha - 0.1f, py + 0.5f, z1 - 0.05f), 0.04f, Steel);
            for (int k = -1; k <= 1; k++)
            {
                Vector3 st = new Vector3(k * 1.2f, py, zl + 1.0f);
                g.CylV(st, 0.03f, 0.6f, 6, Steel, Steel, GB.White); g.CylV(st + Vector3.up * 0.6f, 0.17f, 0.05f, 8, Black, Black, GB.White);
                g.Person(1, seed + 11 + k, st, Vector3.back, 0);
            }
        }
        else g.Screen(new Vector3(0, py + 1.06f, zl + 0.6f), 1.6f, 0.2f, Vector3.forward, 4, false);
        // roof on posts, name fascia to the pit lane
        float ry = 2.85f;
        for (int k = 0; k < 4; k++) { float a = (k & 1) == 0 ? -ha + 0.06f : ha - 0.06f, z = (k >> 1) == 0 ? z0 + 0.05f : z1 - 0.05f; g.Box(new Vector3(a, (py + ry) * 0.5f, z), new Vector3(0.04f, (ry - py) * 0.5f, 0.04f), Steel); }
        g.Box(new Vector3(0, ry, zc - 0.15f), new Vector3(ha + 0.15f, 0.06f, hz + 0.3f), WallW);
        float fz = zc + hz + 0.16f;
        g.Quad(new Vector3(-ha - 0.15f, ry - 0.32f, fz), new Vector3(ha + 0.15f, ry - 0.32f, fz), new Vector3(ha + 0.15f, ry + 0.06f, fz), new Vector3(-ha - 0.15f, ry + 0.06f, fz), Vector3.forward, WallW, GB.White);
        g.Quad(new Vector3(-ha - 0.15f, ry - 0.32f, fz + 0.005f), new Vector3(ha + 0.15f, ry - 0.32f, fz + 0.005f), new Vector3(ha + 0.15f, ry - 0.24f, fz + 0.005f), new Vector3(-ha - 0.15f, ry - 0.24f, fz + 0.005f), Vector3.forward, P, GB.White, 1.1f);
        g.NameQuad(new Vector3(-1.6f, ry - 0.2f, fz + 0.005f), new Vector3(1.6f, ry - 0.2f, fz + 0.005f), new Vector3(1.6f, ry, fz + 0.005f), new Vector3(-1.6f, ry, fz + 0.005f), Vector3.forward, 1.5f);
        g.light = 1f;
    }

    // ---------------------------------------------------------------- a simple bay (beyond ~40 m of the camera)
    static void Simple(GB g, float HW, float gd, bool stand)
    {
        float zF = gd, zB = gd + DZ;
        Color P = TP;
        Interior(g, HW, zF, zB, P, TA, false);
        Gantry(g, HW, zF, P, TA, false);
        g.light = 1f;
        for (int s = -1; s <= 1; s += 2)
        {
            Vector3 c = new Vector3(s * (HW - 0.62f), 0.95f, zF + 1.25f);
            g.Box(c, new Vector3(0.3f, 0.82f, 0.8f), Blanket);
            g.Box(c, new Vector3(0.31f, 0.06f, 0.81f), P);
        }
        g.Box(new Vector3(HW - 1.4f, 0.52f, zB - 0.34f), new Vector3(1.24f, 0.52f, 0.28f), Graph);
        g.Box(new Vector3(0, 0.38f, zB - 1.5f), new Vector3(1.25f, 0.38f, 0.3f), Graph);
        g.Screen(new Vector3(0, 1.0f, zB - 1.45f), 1.0f, 0.18f, Vector3.back, 4, true);
        if (stand) PitWallStand(g, gd, P, TS, false, 0);
    }

    // ---------------------------------------------------------------- a bay no team uses: roller shutter down, plain header
    static void Closed(GB g, float HW, float gd)
    {
        float z = gd + 0.12f, w = HW + 0.1f; Vector3 n = Vector3.back;
        g.light = 0.92f;
        g.Quad(new Vector3(-HW, 0, z), new Vector3(HW, 0, z), new Vector3(HW, 3.95f, z), new Vector3(-HW, 3.95f, z), n, C(0.78f, 0.79f, 0.80f), GB.Cell(8));
        g.Quad(new Vector3(-HW, 0.0f, z - 0.01f), new Vector3(HW, 0.0f, z - 0.01f), new Vector3(HW, 0.12f, z - 0.01f), new Vector3(-HW, 0.12f, z - 0.01f), n, Kick, GB.White);
        g.Quad(new Vector3(-w, 3.95f, gd - 0.02f), new Vector3(w, 3.95f, gd - 0.02f), new Vector3(w, 4.52f, gd - 0.02f), new Vector3(-w, 4.52f, gd - 0.02f), n, WallW, GB.White);
        g.Quad(new Vector3(-w, 3.95f, gd - 0.03f), new Vector3(w, 3.95f, gd - 0.03f), new Vector3(w, 4.03f, gd - 0.03f), new Vector3(-w, 4.03f, gd - 0.03f), n, C(0.55f, 0.57f, 0.60f), GB.White);
        for (int s = -1; s <= 1; s += 2) g.Box(new Vector3(s * (HW - 0.02f), 1.975f, gd + 0.06f), new Vector3(0.1f, 1.975f, 0.08f), WallW);
        g.light = 1f;
    }

    // ---------------------------------------------------------------- the team's box on the lane (both levels of detail)
    // the team colour just inside the white box lines (Trackside.cs), the name painted between the box and the garage, a
    // lollipop on its stand at the front of the box, four tyre sets in blankets lined up at the garage front
    static void Lane(GB g, float gd, bool fine)
    {
        const float y = 0.058f, hf = 2.9f, hs = 1.35f, w = 0.14f;
        g.light = 0.86f;
        void Strip(float x0, float x1, float z0, float z1, Color c) => g.Quad(new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x1, y, z1), new Vector3(x0, y, z1), Vector3.up, c, GB.White);
        Strip(-hf, hf, -hs, -hs + w, TP); Strip(-hf, hf, hs - w, hs, TP);
        Strip(-hf, -hf + w, -hs + w, hs - w, TP); Strip(hf - w, hf, -hs + w, hs - w, TP);
        Strip(1.55f, 1.75f, -0.45f, 0.45f, TA); // the front axle mark, team accent
        // the name, read from the lane (dark plate, white letters)
        g.NameQuad(new Vector3(-2.4f, y, 1.82f), new Vector3(2.4f, y, 1.82f), new Vector3(2.4f, y, 2.12f), new Vector3(-2.4f, y, 2.12f), Vector3.up, 1.1f, Vector3.forward);
        g.light = 1f;
        // lollipop: weighted base, pole, round board (team ring, white face) facing along the lane
        {
            Vector3 lb = new Vector3(3.75f, 0, gd - 0.35f);
            g.CylV(lb, 0.16f, 0.05f, 8, Black, Black, GB.White);
            g.Box(lb + new Vector3(0, 0.82f, 0), new Vector3(0.018f, 0.78f, 0.018f), Steel);
            if (fine) g.Wheel(lb + new Vector3(0, 1.62f, 0), 0f, 0.27f, 0.025f, TP, TP, C(0.95f, 0.95f, 0.95f));
            else g.Box(lb + new Vector3(0, 1.62f, 0), new Vector3(0.02f, 0.25f, 0.25f), TP);
        }
        // tyre sets at the garage front
        for (int k = 0; k < 4; k++)
        {
            float x = (k < 2 ? -1f : 1f) * (k % 2 == 0 ? 2.45f : 1.45f);
            Vector3 c = new Vector3(x, 0.36f, gd - 0.42f);
            if (fine) { g.Wheel(c, 0f, 0.36f, 0.38f, Blanket, Blanket, Blanket, GB.Cell(6)); g.Wheel(c, 0f, 0.365f, 0.12f, TP, TP, TP, GB.White, false); }
            else { g.Box(c, new Vector3(0.19f, 0.36f, 0.36f), Blanket); g.Box(c, new Vector3(0.06f, 0.37f, 0.37f), TP); }
        }
    }

    // ---------------------------------------------------------------- the atlas
    // 256 x 512: cells of 64 x 64 in the top 192 rows (12 cells), the team names in the rows below (NameRows of 16 px;
    // the last row stays blank: the template's name boards point at it, PitGarage.shader moves them down to the team's)
    const int AtlasH = 512, NameRows = 20;
    static void DrawAtlas(int[] row, string[] names)
    {
        if (atlas == null) atlas = new Texture2D(256, AtlasH, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 2, name = "PitGarageAtlas" };
        var px = new Color32[256 * AtlasH];
        void Cell(int k, System.Func<int, int, Color32> f)
        {
            int x0 = (k % 4) * 64, y0 = AtlasH - 64 - (k / 4) * 64;
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) px[(y0 + y) * 256 + x0 + x] = f(x, y);
        }
        Color32 G(int v) { byte b = (byte)Mathf.Clamp(v, 0, 255); return new Color32(b, b, b, 255); }
        Cell(0, (x, y) => G(255));
        Cell(1, (x, y) => (x < 5 || x > 58 || y < 5 || y > 58) ? G(178) : (x < 7 || y > 56) ? G(250) : G(234 - y / 16 + (int)(Hash(x, y, 1) * 4)));
        Cell(2, (x, y) => G(150 + (int)((Hash(x, y, 2) - 0.5f) * 14f) + (int)((Hash(x / 8, y / 8, 3) - 0.5f) * 8f)));
        Cell(3, (x, y) => (x % 16 == 0 || y % 16 == 0) ? G(222) : G(255));
        Cell(4, (x, y) =>
        {
            int r = (y - 4) / 5, ry = (y - 4) % 5;
            if (y < 4 || y > 60 || ry > 2 || x < 3 || x > 60) return new Color32(10, 14, 24, 255);
            float h = Hash(r, 0, 4); int w2 = 26 + (int)(h * 18);
            if (x < 7) return new Color32(220, 220, 225, 255);
            if (x >= 9 && x < 21) return (x % 4 == 3) ? new Color32(10, 14, 24, 255) : new Color32(235, 235, 240, 255);
            if (x >= 24 && x < w2) { int t = (int)(Hash(r, 1, 5) * 4); return t == 0 ? new Color32(170, 60, 230, 255) : t == 1 ? new Color32(60, 220, 90, 255) : t == 2 ? new Color32(250, 220, 60, 255) : new Color32(230, 230, 235, 255); }
            if (x >= 48 && x < 58 && Hash(r, 2, 6) > 0.4f) return new Color32(200, 200, 205, 255);
            return new Color32(10, 14, 24, 255);
        });
        Cell(5, (x, y) =>
        {
            for (int k = 0; k < 3; k++)
            {
                float cy = 32f + 13f * Mathf.Sin(x * (0.11f + k * 0.05f) + k * 2f) * (1f - k * 0.25f) + (k - 1) * 6f;
                if (Mathf.Abs(y - cy) < 1.3f) return k == 0 ? new Color32(60, 210, 240, 255) : k == 1 ? new Color32(250, 210, 60, 255) : new Color32(240, 80, 160, 255);
            }
            return (x % 10 == 0 || y % 10 == 0) ? new Color32(30, 40, 58, 255) : new Color32(9, 13, 22, 255);
        });
        Cell(6, (x, y) => ((x + y) % 12 == 0 || (x - y + 64) % 12 == 0) ? G(52) : G(22 + (int)(Hash(x, y, 7) * 6)));
        Cell(7, (x, y) => (y % 6 < 2) ? G(30) : G(70));
        Cell(8, (x, y) => (y % 8 == 0) ? G(150) : (y % 8 == 1) ? G(238) : G(212 + (int)(Hash(x, y, 9) * 6) - (y % 8) * 2)); // roller shutter slats
        // name rows: white letters on near-black, 5 x 7 font doubled (10 x 14), centred
        for (int i = 0; i < NameRows * 16 * 256; i++) px[i] = new Color32(20, 20, 23, 255);
        for (int b = 0; b < row.Length; b++)
        {
            if (row[b] < 0 || names[b] == null) continue;
            string s = names[b]; int len = Mathf.Min(s.Length, 21), x0 = (256 - len * 12) / 2, y0 = row[b] * 16 + 1;
            for (int ci = 0; ci < len; ci++)
            {
                var gl = Glyph(s[ci]); if (gl == null) continue;
                for (int gy = 0; gy < 7; gy++) for (int gx = 0; gx < 5; gx++)
                    {
                        if (gl[gy][gx] != '#') continue;
                        for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                            {
                                int X = x0 + ci * 12 + 1 + gx * 2 + dx, Y = y0 + (6 - gy) * 2 + dy;
                                if (X >= 0 && X < 256 && Y >= 0 && Y < NameRows * 16) px[Y * 256 + X] = new Color32(245, 245, 245, 255);
                            }
                    }
            }
        }
        atlas.SetPixels32(px); atlas.Apply(true);
    }
    static float Hash(int x, int y, int s)
    {
        unchecked { int h = x * 374761393 + y * 668265263 + s * 2147483; h = (h ^ (int)((uint)h >> 13)) * 1274126177; return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296f; }
    }
    static string[] Glyph(char ch)
    {
        switch (ch)
        {
            case 'A': return new[] { " ### ", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" };
            case 'B': return new[] { "#### ", "#   #", "#   #", "#### ", "#   #", "#   #", "#### " };
            case 'C': return new[] { " ### ", "#   #", "#    ", "#    ", "#    ", "#   #", " ### " };
            case 'D': return new[] { "#### ", "#   #", "#   #", "#   #", "#   #", "#   #", "#### " };
            case 'E': return new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#####" };
            case 'F': return new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#    " };
            case 'G': return new[] { " ### ", "#   #", "#    ", "# ###", "#   #", "#   #", " ### " };
            case 'H': return new[] { "#   #", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" };
            case 'I': return new[] { " ### ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### " };
            case 'J': return new[] { "  ###", "   # ", "   # ", "   # ", "   # ", "#  # ", " ##  " };
            case 'K': return new[] { "#   #", "#  # ", "# #  ", "##   ", "# #  ", "#  # ", "#   #" };
            case 'L': return new[] { "#    ", "#    ", "#    ", "#    ", "#    ", "#    ", "#####" };
            case 'M': return new[] { "#   #", "## ##", "# # #", "# # #", "#   #", "#   #", "#   #" };
            case 'N': return new[] { "#   #", "##  #", "# # #", "#  ##", "#   #", "#   #", "#   #" };
            case 'O': return new[] { " ### ", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " };
            case 'P': return new[] { "#### ", "#   #", "#   #", "#### ", "#    ", "#    ", "#    " };
            case 'Q': return new[] { " ### ", "#   #", "#   #", "#   #", "# # #", "#  # ", " ## #" };
            case 'R': return new[] { "#### ", "#   #", "#   #", "#### ", "# #  ", "#  # ", "#   #" };
            case 'S': return new[] { " ####", "#    ", "#    ", " ### ", "    #", "    #", "#### " };
            case 'T': return new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  " };
            case 'U': return new[] { "#   #", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " };
            case 'V': return new[] { "#   #", "#   #", "#   #", "#   #", "#   #", " # # ", "  #  " };
            case 'W': return new[] { "#   #", "#   #", "#   #", "# # #", "# # #", "## ##", "#   #" };
            case 'X': return new[] { "#   #", "#   #", " # # ", "  #  ", " # # ", "#   #", "#   #" };
            case 'Y': return new[] { "#   #", "#   #", " # # ", "  #  ", "  #  ", "  #  ", "  #  " };
            case 'Z': return new[] { "#####", "    #", "   # ", "  #  ", " #   ", "#    ", "#####" };
            default: return null;
        }
    }

    // ---------------------------------------------------------------- mesh builder in a bay frame (x along the lane, z to the garage)
    sealed class GB
    {
        public readonly List<Vector3> v = new List<Vector3>(); public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<Color32> c = new List<Color32>(); public readonly List<int> t = new List<int>();
        public readonly List<Vector4> tw = new List<Vector4>(); // uv1: team colour weights (primary, secondary, accent), name board flag
        public int hs = 1; public float light = 1f;

        const float AH = AtlasH;
        public static Vector4 Cell(int k) { int x0 = (k % 4) * 64, y0 = AtlasH - 64 - (k / 4) * 64; return new Vector4((x0 + 3) / 256f, (y0 + 3) / AH, (x0 + 61) / 256f, (y0 + 61) / AH); }
        public static readonly Vector4 White = new Vector4(8 / 256f, (AtlasH - 56) / AH, 56 / 256f, (AtlasH - 8) / AH);
        static readonly Vector4 NameTpl = new Vector4(0.5f / 256f, ((NameRows - 1) * 16 + 0.5f) / AH, 255.5f / 256f, ((NameRows - 1) * 16 + 15.5f) / AH);

        // the template is built in the bay frame (x along the lane, z to the garage) mirrored in x where the lane runs to
        // the viewer's left (hs -1), so the instance's frame is never mirrored
        public static Vector3 Flip(Vector3 l, int s) => new Vector3(l.x * s, l.y, l.z);
        public Vector3 W(Vector3 l) => Flip(l, hs);
        Vector3 Dir(Vector3 l) => Flip(l, hs);
        bool nameOn;
        static Vector4 Slot(ref Color col)
        {
            if (col.r > -0.5f) return Vector4.zero;
            int s = (int)(-col.r + 0.5f); col = Color.white;
            return s == 1 ? new Vector4(1, 0, 0, 0) : s == 2 ? new Vector4(0, 1, 0, 0) : new Vector4(0, 0, 1, 0);
        }
        static byte B(float x) => (byte)(Mathf.Clamp01(x) * 255f + 0.5f);
        static Color32 Col(Color g, float k) { var l = g.linear; k *= 0.5f; return new Color32(B(l.r * k), B(l.g * k), B(l.b * k), 255); } // the shader doubles it
        float Shade(Vector3 n) => light * (0.70f + 0.30f * (n.y * 0.5f + 0.5f)) * (0.95f + 0.05f * n.x - 0.04f * Mathf.Abs(n.z));

        public void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Vector3 n, Color col, Vector4 cell, float glow = 0f)
        {
            Vector4 sw = Slot(ref col); if (nameOn) sw.w = 1f;
            Color32 k = Col(col, glow > 0f ? glow : Shade(n));
            Vector3 wa = W(a), wb = W(b), wc = W(cc), wd = W(d);
            int i = v.Count; v.Add(wa); v.Add(wb); v.Add(wc); v.Add(wd);
            uv.Add(new Vector2(cell.x, cell.y)); uv.Add(new Vector2(cell.z, cell.y)); uv.Add(new Vector2(cell.z, cell.w)); uv.Add(new Vector2(cell.x, cell.w));
            c.Add(k); c.Add(k); c.Add(k); c.Add(k); tw.Add(sw); tw.Add(sw); tw.Add(sw); tw.Add(sw);
            if (Vector3.Dot(Vector3.Cross(wb - wa, wc - wa), Dir(n)) >= 0f) { t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3); }
            else { t.Add(i); t.Add(i + 2); t.Add(i + 1); t.Add(i); t.Add(i + 3); t.Add(i + 2); }
        }
        void Tri(Vector3 a, Vector3 b, Vector3 cc, Vector3 n, Color col, Vector2 u, float glow = 0f)
        {
            Vector4 sw = Slot(ref col);
            Color32 k = Col(col, glow > 0f ? glow : Shade(n));
            Vector3 wa = W(a), wb = W(b), wc = W(cc);
            int i = v.Count; v.Add(wa); v.Add(wb); v.Add(wc); uv.Add(u); uv.Add(u); uv.Add(u); c.Add(k); c.Add(k); c.Add(k); tw.Add(sw); tw.Add(sw); tw.Add(sw);
            if (Vector3.Dot(Vector3.Cross(wb - wa, wc - wa), Dir(n)) >= 0f) { t.Add(i); t.Add(i + 1); t.Add(i + 2); } else { t.Add(i); t.Add(i + 2); t.Add(i + 1); }
        }
        // box: centre, half sizes (x, y, z in the bay frame, turned by yaw degrees about up)
        public void Box(Vector3 cen, Vector3 h, Color col, float yaw = 0f, float glow = 0f)
        {
            float cs = Mathf.Cos(yaw * Mathf.Deg2Rad), sn = Mathf.Sin(yaw * Mathf.Deg2Rad);
            Vector3 ra = new Vector3(cs, 0, sn) * h.x, rz = new Vector3(-sn, 0, cs) * h.z, ry = Vector3.up * h.y;
            Face(cen, ra, ry, rz, col, glow); Face(cen, -ra, ry, rz, col, glow);
            Face(cen, ry, ra, rz, col, glow);
            Face(cen, rz, ra, ry, col, glow); Face(cen, -rz, ra, ry, col, glow);
            if (cen.y - h.y > 0.1f) Face(cen, -ry, ra, rz, col, glow);
        }
        void Face(Vector3 cen, Vector3 n, Vector3 e1, Vector3 e2, Color col, float glow)
        {
            Vector3 f = cen + n; Quad(f - e1 - e2, f + e1 - e2, f + e1 + e2, f - e1 + e2, n.normalized, col, White, glow);
        }
        // a thin box along a segment (hoses, cables, braces)
        public void Seg(Vector3 p0, Vector3 p1, float th, Color col)
        {
            Vector3 d = p1 - p0; if (d.sqrMagnitude < 1e-6f) return;
            Vector3 dn = d.normalized, e1 = Vector3.Cross(dn, Vector3.up); if (e1.sqrMagnitude < 1e-4f) e1 = Vector3.Cross(dn, Vector3.right);
            e1 = e1.normalized * (th * 0.5f); Vector3 e2 = Vector3.Cross(e1, dn).normalized * (th * 0.5f);
            for (int k = 0; k < 4; k++)
            {
                Vector3 a = k == 0 ? e1 : k == 1 ? e2 : k == 2 ? -e1 : -e2, bq = k == 0 ? e2 : k == 1 ? -e1 : k == 2 ? -e2 : e1;
                Quad(p0 + a - bq, p1 + a - bq, p1 + a + bq, p0 + a + bq, a.normalized, col, White);
            }
        }
        // upright cylinder: base centre, radius, height
        public void CylV(Vector3 b, float r, float hgt, int n, Color side, Color top, Vector4 cell, bool cap = true)
        {
            Vector2 tu = new Vector2((cell.x + cell.z) * 0.5f, (cell.y + cell.w) * 0.5f);
            for (int k = 0; k < n; k++)
            {
                float a0 = 2f * Mathf.PI * k / n, a1 = 2f * Mathf.PI * (k + 1) / n, am = (a0 + a1) * 0.5f;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * r, d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * r, nm = new Vector3(Mathf.Cos(am), 0, Mathf.Sin(am));
                float u0 = Mathf.Lerp(cell.x, cell.z, k / (float)n), u1 = Mathf.Lerp(cell.x, cell.z, (k + 1f) / n);
                Quad(b + d0, b + d1, b + d1 + Vector3.up * hgt, b + d0 + Vector3.up * hgt, nm, side, new Vector4(u0, cell.y, u1, cell.w));
                if (cap) Tri(b + Vector3.up * hgt, b + d0 + Vector3.up * hgt, b + d1 + Vector3.up * hgt, Vector3.up, top, tu);
            }
        }
        // a wheel / tyre / reel on a horizontal axis (yaw 0 = along x, 90 = along z): tread, sidewalls down to the hub
        public void Wheel(Vector3 cen, float yaw, float r, float w, Color tread, Color wall, Color hub, Vector4? cell = null, bool caps = true)
        {
            Vector4 ce = cell ?? White; Vector2 cu = new Vector2((ce.x + ce.z) * 0.5f, (ce.y + ce.w) * 0.5f), wu = new Vector2((White.x + White.z) * 0.5f, (White.y + White.w) * 0.5f);
            float cs = Mathf.Cos(yaw * Mathf.Deg2Rad), sn = Mathf.Sin(yaw * Mathf.Deg2Rad);
            Vector3 ax = new Vector3(cs, 0, sn), p1 = new Vector3(-sn, 0, cs), hw = ax * (w * 0.5f);
            const int n = 12; float rIn = r * 0.55f;
            for (int k = 0; k < n; k++)
            {
                float a0 = 2f * Mathf.PI * k / n, a1 = 2f * Mathf.PI * (k + 1) / n, am = (a0 + a1) * 0.5f;
                Vector3 d0 = (p1 * Mathf.Cos(a0) + Vector3.up * Mathf.Sin(a0)), d1 = (p1 * Mathf.Cos(a1) + Vector3.up * Mathf.Sin(a1)), dm = (p1 * Mathf.Cos(am) + Vector3.up * Mathf.Sin(am));
                float u0 = Mathf.Lerp(ce.x, ce.z, k / (float)n), u1 = Mathf.Lerp(ce.x, ce.z, (k + 1f) / n);
                Quad(cen - hw + d0 * r, cen - hw + d1 * r, cen + hw + d1 * r, cen + hw + d0 * r, dm, tread, new Vector4(u0, ce.y, u1, ce.w));
                if (!caps) continue;
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector3 o = cen + hw * s, nn = ax * s;
                    Quad(o + d0 * r, o + d1 * r, o + d1 * rIn, o + d0 * rIn, nn, wall, new Vector4(cu.x, cu.y, cu.x, cu.y));
                    Tri(o, o + d0 * rIn, o + d1 * rIn, nn, hub, wu);
                }
            }
        }
        // a screen: glowing face (atlas cell 4 or 5), centre c, half sizes, facing n; framed = a thin black bezel behind
        public void Screen(Vector3 cen, float hw, float hh, Vector3 n, int cellK, bool framed)
        {
            Vector3 side = Vector3.Cross(Vector3.up, n).normalized;
            if (framed) Box(cen - n * 0.03f, new Vector3(Mathf.Abs(side.x) * (hw + 0.03f) + Mathf.Abs(n.x) * 0.025f, hh + 0.03f, Mathf.Abs(side.z) * (hw + 0.03f) + Mathf.Abs(n.z) * 0.025f), Black);
            Quad(cen - side * hw - Vector3.up * hh, cen + side * hw - Vector3.up * hh, cen + side * hw + Vector3.up * hh, cen - side * hw + Vector3.up * hh, n, Color.white, Cell(cellK), 1.5f);
        }
        // a team name board: the template's (blank) name row; the letters run to the right of a viewer looking along `look`
        // (bay frame; default: at the face, against its normal n)
        public void NameQuad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Vector3 n, float glow, Vector3 look = default)
        {
            Vector3 lk = look.sqrMagnitude > 1e-6f ? look : -n;
            Vector3 right = Vector3.Cross(Vector3.up, Dir(lk));
            Vector4 cell = NameTpl;
            if (Vector3.Dot(W(b) - W(a), right) < 0f) { float x = cell.x; cell.x = cell.z; cell.z = x; }
            nameOn = true; Quad(a, b, cc, d, n, Color.white, cell, glow); nameOn = false;
        }
        // a person (People.cs rig): kind 1 engineer with headset, 2 mechanic; position and facing in the bay frame; pose 0
        // seated at a desk (pos = the stool's foot), 1 arms crossed, 2 talking, 3 working. Recorded with the template: the
        // animated bays get them as PeoplePits spots, the others the engineers baked into the idle staff mesh
        public void Person(int kind, int seed, Vector3 pos, Vector3 facing, int pose)
        {
            Vector3 fd = facing; fd.y = 0; if (fd.sqrMagnitude < 1e-6f) fd = Vector3.right;
            tspots.Add(new TSpot { kind = kind, seed = seed, pose = pose, pos = pos, fwd = fd.normalized });
        }
    }
}
