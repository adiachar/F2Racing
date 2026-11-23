// People.cs — rigged, animated low-poly people for the pits (crew, engineers, mechanics, people walking in the lane).
// Source: Quaternius' Universal Base Characters (male / female) and Universal Animation Library, CC0 (Assets/Resources/
// People/CREDITS.txt), converted offline by Tools/People/convert.py into Resources/People/people.bytes:
//  * 2 bodies x 4 head gears (crew helmet with visor, engineer headset with boom mic, cap, bare head) x 2 LODs
//    (~1.2-1.3k and ~0.3k triangles), skinned to a 22-joint skeleton; every vertex carries a region index (overall,
//    flank panels, yoke, sleeves, gloves, boots, knee pads, face, hair, helmet, visor...). The meshes are shared by
//    everybody; colours come per person from a 24-entry palette in a MaterialPropertyBlock (People.shader).
//  * clips (24 fps, joint rotations + pelvis position): idle, walk, jog, sprint, carry, kneel-and-work, crouch, push,
//    standing with a headset, arms folded, talking, sitting, sitting talking, celebrating, "yes", reaching.
// Runtime: a Rig = a GameObject with a SkinnedMeshRenderer and 22 bone Transforms (no Animator): clips are sampled and
// blended in code (PoseBuf), then the arms can be pulled onto targets with two-bone IK (wheel gun, tyre, jack handle)
// and the head turned towards a point. Callers pick the LOD (LOD1 beyond ~40 m) and stop animating beyond ~120 m.
// Blobs: soft contact shadows under a group of people, one dynamic mesh (PeopleShadow.shader).
// Realistic people (People2, Tools/People2/build.py + people3.py; 'PPL2' packs, bound to the SAME 22-joint skeleton with
// the same rest orientations, so every clip, the IK and the pit-stop choreography drive them unchanged): textured from one
// atlas per pack, team colours through the same palette (People2.shader; pack v2: texture alpha 0.5 = the region's
// palette colour, 1 = the accent region's, vertex colour G), 3 LODs (SetLodAt; the drivers 4: a hero LOD first):
//  * Resources/People2/people2.bytes + people2_atlas.png (public, CC0, Adi's asset pack Art/People3): "Driver"
//    (driver_01: seamed suit, gloves, racing boots, closed helmet; 19k-tri hero LOD + ~4k / 1.5k / 0.5k: the player's
//    driver in the menu studio, the pre-race walk and the podium) and "Driver2" (driver_02, the rivals on the podium).
//  * Resources/People2/people3.bytes + people3_atlas.png (public, MIT: Microsoft Rocketbox, Art/People3/LICENSE_rocketbox.md;
//    the crew helmet is tomkart's, CC BY): 4 engineers "Eng1-4" (team shirt, own headset; variant 0 = the crew helmet:
//    the stop crew and the garage crew, CrewBodyOf; EngineerBodyOf), 3 marshals "Marshal1-3" (MarshalBodyOf),
//    ~4k / 1.5k / 0.55k tris (as the crew before). Resources/People2/people3p.bytes + _atlas.png (the same licences,
//    loaded only for the podium: PreparePodium / SpawnPodiumCast): 2 officials, a photographer, a TV presenter, 16 fans
//    "Fan1-16" (FanBodyOf), ~5k / 1.5k / 0.55k tris. Hair cards (Rocketbox) = a second sub-mesh on the first two LODs,
//    drawn with People2Hair.shader (alpha clip, own material: the bodies' opaque pass keeps early-Z).
//  * Resources/People2/mixamo/mixamo.bytes (PRIVATE: Mixamo licence, not in the public repo, .gitignore; CI checks it out
//    from the private assets repo): crew bodies Ch08 / Ch13 (female) / Remy and the mocap clips MIdle, MWalk, MTalk,
//    MTalk2, MClap, MSitClap, MWave, MBriefcase. Without it those clip ids fall back to the nearest Quaternius clip
//    (Mocap = false) and the crew are the Rocketbox engineers only; without any pack everything is as before.
//    (No "Cheer" mocap in the uploads: ActCheer plays the Quaternius Celebrate clip.)
//
// PODIUM CEREMONY API (for PostRace.cs; nothing is drawn until it is called; all of it is allocation-free per frame):
//   People.PreparePodium();   // optional, ahead (loads the podium people; else the first SpawnPodiumCast does)
//   var cast = People.SpawnPodiumCast(podiumPos, facing, teamOfTop3, playerOnPodium, playerPos [, fans = 14,
//                                     driverSpots = null, parent = null, playerLivery = null]); // null: no people data
//     podiumPos   = world point on the ground under the front-centre of the top step; facing = the way the drivers face
//                   (towards the crowd / TV camera). Local frame: +z = facing, +x = the drivers' right, y up.
//     driverSpots = 3 local points P1, P2, P3 (y = the top of each step); default PodiumSpots (P1 0/1.05/0, P2 +1.7/0.75,
//                   P3 -1.7/0.5). teamOfTop3[i] = Livery.Teams index of the driver in P(i+1) (the player's own entry is
//                   ignored: he gets playerLivery, default Livery.Player[Livery.PlayerIndex] (in a season pass the team's livery,
//                   as PreRace does), and the yellow PlayerHelmet); playerPos 1..3.
//   cast.drivers[0..2] (P1..P3; drivers are the pack drivers in team suits; the player = driver_01, rivals driver_02 / _01),
//   cast.player (or null), cast.official1 / official2 / photographer / presenter, cast.fans (list), cast.all.
//   Each is a CastMember: .rig (People.Rig: Place / LookAt / Show / SetLodAt...), .Place(worldPos, yaw), and
//     cast.Play(member, People.ActIdle | ActClap | ActCheer | ActWave | ActTalk | ActFist [, fade s]) or cast.PlayAll(list, act);
//     member.raise 0..1  (right arm up over the head, e.g. lifting the trophy), member.hold 0..1 (right hand in front of
//     the chest, holding it), member.aim 0..1 (both hands at the face: the photographer's shot), member.mic 0..1 (right
//     hand at the mouth: the presenter), member.lookAt / look (head turn to a world point), member.speed (clip rate);
//     member.prop: a Transform kept in the right palm every Tick (upright, facing the member's forward): parent the
//     trophy (its grip at the origin) to it.
//   cast.Tick(Time.deltaTime, camera.position) every frame (clips, cross-fades, IK, LODs by distance, prop transforms);
//   cast.LodDistanceScale (1; 0.5 = finer LODs for a close shot); cast.Show(bool); cast.Destroy() when the ceremony ends. Defaults: drivers idle, officials clap, photographer aims,
//   presenter talks, fans cheer / clap / wave (staggered) and look at P1.
using System.Collections.Generic;
using UnityEngine;

public static class People
{
    // ---------------------------------------------------------------- data layout (Tools/People/convert.py)
    public const int NJ = 22, NReg = 24;
    public const int JPelvis = 0, JSpine1 = 1, JSpine2 = 2, JSpine3 = 3, JNeck = 4, JHead = 5, JClavL = 6, JUpL = 7, JLoL = 8, JHandL = 9,
                     JClavR = 10, JUpR = 11, JLoR = 12, JHandR = 13, JThighL = 14, JCalfL = 15, JFootL = 16, JThighR = 18;
    public const int RSuit = 0, RSide = 1, RYoke = 2, RSleeve = 3, RForearm = 4, RGlove = 5, RThigh = 6, RStripe = 7, RShin = 8, RBoot = 9, RBelt = 10,
                     RNeck = 11, RFace = 12, RHair = 13, RHelmet = 14, RHelmetTop = 15, RVisor = 16, RHeadset = 17, RCap = 18, RCapBrim = 19, RKnee = 20;
    public const int Idle = 0, Walk = 1, Jog = 2, Carry = 3, Kneel = 4, Crouch = 5, Push = 6, Headset = 7, FoldArms = 8, Talk = 9, Sit = 10, SitTalk = 11,
                     Celebrate = 12, Yes = 13, Interact = 14, Sprint = 15, NClips = 16;
    // mocap clips (the private Mixamo pack; else the fallback below)
    public const int MIdle = 16, MWalk = 17, MTalk = 18, MTalk2 = 19, MClap = 20, MSitClap = 21, MWave = 22, MBriefcase = 23, NAllClips = 24;
    static readonly string[] MNames = { "MIdle", "MWalk", "MTalk", "MTalk2", "MClap", "MSitClap", "MWave", "MBriefcase" };
    static readonly int[] MFallback = { Idle, Walk, Talk, Headset, Celebrate, SitTalk, Celebrate, Idle };
    public static bool Mocap { get; private set; }            // the Mixamo clips are there (not their fallbacks)
    public static int WalkClip => Mocap ? MWalk : Walk;
    public static float WalkStride { get; private set; } = 1.66f; // m per cycle of WalkClip (feet planted)
    public static int DriverBody { get; private set; } = -1;   // the race-suited driver (People2), -1 = none
    // licence credit for the People2 public pack (CC BY 4.0; Resources/People2/CREDITS.txt), for the in-game credits
    public const string Credit = "People: Microsoft Rocketbox avatars, (c) 2020 Microsoft, MIT licence. Crew helmet / cap from \"Race Car Driver - Low Poly\" by tomkart and \"Free Avatar for iClone\" by Asfandyar Hesami (Sketchfab), CC BY 4.0. Modified.";
    // the MIT notice the Rocketbox avatars require in the game's credits / third-party notices (Art/People3/LICENSE_rocketbox.md)
    public const string RocketboxLicence = "MIT License. Copyright (c) 2020 Microsoft. Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.";
    public static readonly Color PlayerHelmet = new Color(1f, 0.82f, 0.18f); // (as CarModel / MenuStudio / PreRace)
    public static int Driver2Body { get; private set; } = -1;  // the second pack driver (rivals on the podium), -1 = none
    static readonly List<int> crewM = new List<int>(), crewF = new List<int>();
    static readonly List<int> engM = new List<int>(), engF = new List<int>(), fanL = new List<int>(), marshalL = new List<int>(), officialL = new List<int>();
    static int photoBody = -1, presenterBody = -1;
    public const int GHelmet = 0, GHeadset = 1, GCap = 2, GBare = 3;
    public const int OCrew = 0, OEngineer = 1, OMechanic = 2, OMarshal = 3;

    internal static int[] Parent;
    internal sealed class Body
    {
        public float pscale; public Vector3[] t = new Vector3[NJ]; public Quaternion[] q = new Quaternion[NJ];
        public Matrix4x4[] bind = new Matrix4x4[NJ]; public Mesh[,] mesh;
        public Vector3 headFwd, headUp; // the head's forward / up in its own frame (from the rest pose)
        public Material mat;            // null: the palette material (People.shader); else its pack's textured one
        public Material[] mats1, mats2; // (one material / + the pack's hair-card material for meshes with a second sub-mesh)
        public bool real, female; public string name = "";
    }
    internal sealed class ClipData { public string name; public float fps, len, stride; public int n; public bool loop; public Quaternion[] q; public Vector3[] p; }
    internal static Body[] bodies; internal static ClipData[] clips;
    static bool tried;
    static Material mat, shadowMat;

    public static bool Ready { get { if (!tried) { tried = true; try { Load(); } catch (System.Exception e) { Debug.LogWarning("People: " + e.Message); bodies = null; } } return bodies != null && clips != null; } }

    static void Load()
    {
        var ta = Resources.Load<TextAsset>("People/people"); if (ta == null) return;
        var r = new System.IO.BinaryReader(new System.IO.MemoryStream(ta.bytes));
        if (new string(r.ReadChars(4)) != "PPL1") return;
        int nj = r.ReadInt32(); if (nj != NJ) return;
        Parent = new int[NJ]; for (int j = 0; j < NJ; j++) Parent[j] = r.ReadInt32();
        int nb = r.ReadInt32(); var bs = new Body[nb];
        for (int b = 0; b < nb; b++)
        {
            var B = new Body { pscale = r.ReadSingle() };
            for (int j = 0; j < NJ; j++) { B.t[j] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); B.q[j] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
            for (int j = 0; j < NJ; j++)
            {
                var m = new Matrix4x4();
                for (int k = 0; k < 16; k++) m[k / 4, k % 4] = r.ReadSingle();
                B.bind[j] = m;
            }
            HeadAxes(B);
            int nv = r.ReadInt32(); B.mesh = new Mesh[nv, 2];
            for (int v = 0; v < nv; v++)
                for (int l = 0; l < 2; l++) B.mesh[v, l] = ReadMesh(r, B, $"Person{b}_{v}_{l}");
            bs[b] = B;
        }
        int nc = r.ReadInt32(); var cs = new ClipData[nc];
        for (int c = 0; c < nc; c++)
        {
            int ln = r.ReadByte(); var C = new ClipData { name = new string(r.ReadChars(ln)), fps = r.ReadSingle(), n = r.ReadInt32(), loop = r.ReadByte() != 0 };
            C.q = new Quaternion[C.n * NJ]; C.p = new Vector3[C.n];
            const float k = 1f / 32767f;
            for (int i = 0; i < C.q.Length; i++) C.q[i] = new Quaternion(r.ReadInt16() * k, r.ReadInt16() * k, r.ReadInt16() * k, r.ReadInt16() * k);
            for (int i = 0; i < C.n; i++) C.p[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            C.len = (C.loop ? C.n : C.n - 1) / C.fps;
            cs[c] = C;
        }
        if (nc < NClips) return;
        // the realistic packs (each optional; a broken one is skipped, the old people stay)
        var bl = new List<Body>(bs); var cl = new List<ClipData>(cs);
        LoadPack("People2/people2", bl, cl);
        LoadPack("People2/mixamo/mixamo", bl, cl);
        LoadPack("People2/people3", bl, cl);
        // the mocap clip ids: the pack's clip, else the fallback (the same data)
        var all = new ClipData[NAllClips]; bool mocap = true;
        for (int c = 0; c < NClips; c++) all[c] = cs[c];
        for (int m = 0; m < MNames.Length; m++)
        {
            ClipData f = null;
            for (int c = NClips; c < cl.Count; c++) if (cl[c].name == MNames[m]) { f = cl[c]; break; }
            if (f == null) { mocap = false; f = cs[MFallback[m]]; }
            all[NClips + m] = f;
        }
        Mocap = mocap; WalkStride = mocap && all[MWalk].stride > 0.3f ? all[MWalk].stride : 1.66f;
        crewM.Clear(); crewF.Clear(); engM.Clear(); engF.Clear(); fanL.Clear(); marshalL.Clear(); officialL.Clear();
        DriverBody = -1; Driver2Body = -1; photoBody = -1; presenterBody = -1; podiumLoaded = false;
        int avatar = Classify(bl, bs.Length);
        if (avatar >= 0 && engM.Count + engF.Count == 0) crewM.Add(avatar);
        bodies = bl.ToArray(); clips = all;
    }

    // the realistic bodies from index `from` into the role lists (by name); returns the Avatar's index or -1
    static int Classify(List<Body> bl, int from)
    {
        int avatar = -1;
        for (int b = from; b < bl.Count; b++)
        {
            string n = bl[b].name;
            if (n == "Driver") DriverBody = b;
            else if (n == "Driver2") Driver2Body = b;
            else if (n == "Avatar") avatar = b;
            else if (n.StartsWith("Eng", System.StringComparison.Ordinal)) (bl[b].female ? engF : engM).Add(b);
            else if (n.StartsWith("Fan", System.StringComparison.Ordinal)) fanL.Add(b);
            else if (n.StartsWith("Marshal", System.StringComparison.Ordinal)) marshalL.Add(b);
            else if (n.StartsWith("Official", System.StringComparison.Ordinal)) officialL.Add(b);
            else if (n == "Photographer") photoBody = b;
            else if (n == "Presenter") presenterBody = b;
            else (bl[b].female ? crewF : crewM).Add(b);
        }
        return avatar;
    }

    // The podium people (Resources/People2/people3p: officials, photographer, TV presenter, 16 fans; ~4 MB + a 2048 x 1024
    // atlas) are not loaded with the rest (a race never needs them): on first use (SpawnPodiumCast), or call this behind a
    // loading screen / at the flag to keep the ~0.1-0.3 s load out of the ceremony. False without the people data.
    static bool podiumLoaded;
    public static bool PreparePodium()
    {
        if (!Ready) return false;
        if (podiumLoaded) return true;
        podiumLoaded = true;
        var bl = new List<Body>(bodies); var cl = new List<ClipData>(); int n0 = bl.Count;
        LoadPack("People2/people3p", bl, cl);
        Classify(bl, n0); bodies = bl.ToArray();
        return true;
    }

    static void HeadAxes(Body B)
    {
        // the head's axes: world (model) forward / up seen from the head joint at rest
        Quaternion hw = Quaternion.identity; { int j = JHead; var chain = new List<int>(); while (j >= 0) { chain.Add(j); j = Parent[j]; } for (int k = chain.Count - 1; k >= 0; k--) hw = hw * B.q[chain[k]]; }
        B.headFwd = Quaternion.Inverse(hw) * Vector3.forward; B.headUp = Quaternion.Inverse(hw) * Vector3.up;
    }

    // ---------------------------------------------------------------- People2 packs (Tools/People2/build.py, 'PPL2')
    static void LoadPack(string path, List<Body> bl, List<ClipData> cl)
    {
        var ta = Resources.Load<TextAsset>(path); if (ta == null) return;
        try
        {
            var r = new System.IO.BinaryReader(new System.IO.MemoryStream(ta.bytes));
            if (new string(r.ReadChars(4)) != "PPL2") return;
            int ver = r.ReadInt32(); if (r.ReadInt32() != NJ || ver > 2) return;
            var jpg = r.ReadBytes((int)r.ReadUInt32()); var png = r.ReadBytes((int)r.ReadUInt32());
            Material mat = null, hair = null;
            var sh = Resources.Load<Shader>("People/People2");
            if (sh != null && sh.isSupported)
            {
                // the atlas: in the pack (JPEG colour + PNG tint), or (empty blocks) the imported <pack>_atlas.png (ASTC)
                var tex = jpg.Length == 0 ? LoadAtlas(path + "_atlas") : DecodeAtlas(jpg, png, path, ver < 2);
                if (tex != null)
                {
                    mat = new Material(sh) { name = "People2", mainTexture = tex, hideFlags = HideFlags.DontUnloadUnusedAsset };
                    var hs = Resources.Load<Shader>("People/People2Hair");
                    if (hs != null && hs.isSupported) hair = new Material(hs) { name = "People2Hair", mainTexture = tex, hideFlags = HideFlags.DontUnloadUnusedAsset };
                }
            }
            var mats1 = new Material[] { mat }; var mats2 = hair != null ? new Material[] { mat, hair } : mats1;
            int nb = r.ReadInt32(); var add = new List<Body>();
            for (int b = 0; b < nb; b++)
            {
                int ln = r.ReadByte(); string name = new string(r.ReadChars(ln)); int flags = r.ReadByte();
                var B = new Body { pscale = r.ReadSingle(), real = true, female = (flags & 1) != 0, name = name, mat = mat, mats1 = mats1, mats2 = mats2 };
                r.ReadSingle(); // (height)
                for (int j = 0; j < NJ; j++) { B.t[j] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); B.q[j] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
                for (int j = 0; j < NJ; j++)
                {
                    var m = new Matrix4x4();
                    for (int k = 0; k < 16; k++) m[k / 4, k % 4] = r.ReadSingle();
                    B.bind[j] = m;
                }
                HeadAxes(B);
                int nv = r.ReadInt32(), nl = r.ReadInt32(); B.mesh = new Mesh[nv, nl];
                for (int v = 0; v < nv; v++)
                    for (int l = 0; l < nl; l++) B.mesh[v, l] = ReadMesh2(r, B, name + "_" + v + "_" + l, ver);
                add.Add(B);
            }
            int nc = r.ReadInt32(); var ca = new List<ClipData>();
            for (int c = 0; c < nc; c++)
            {
                int ln = r.ReadByte(); var C = new ClipData { name = new string(r.ReadChars(ln)), fps = r.ReadSingle(), n = r.ReadInt32(), loop = r.ReadByte() != 0, stride = r.ReadSingle() };
                C.q = new Quaternion[C.n * NJ]; C.p = new Vector3[C.n];
                const float k = 1f / 32767f;
                for (int i = 0; i < C.q.Length; i++) C.q[i] = new Quaternion(r.ReadInt16() * k, r.ReadInt16() * k, r.ReadInt16() * k, r.ReadInt16() * k);
                for (int i = 0; i < C.n; i++) C.p[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                C.len = (C.loop ? C.n : C.n - 1) / C.fps;
                ca.Add(C);
            }
            bl.AddRange(add); cl.AddRange(ca);
        }
        catch (System.Exception e) { Debug.LogWarning("People2 " + path + ": " + e.Message); }
        finally { Resources.UnloadAsset(ta); }
    }

    // colour from the JPEG, tint amount from the PNG (as ModelLib.DecodeAtlas), GPU-compressed with mips
    // (v1 packs: alpha = tint amount; v2's encoding is half of it for the main colour, see People2.shader)
    static Texture2D DecodeAtlas(byte[] jpg, byte[] png, string name, bool v1)
    {
        var tc = new Texture2D(2, 2, TextureFormat.RGBA32, false, false); var tm = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        if (!ImageConversion.LoadImage(tc, jpg, false) || !ImageConversion.LoadImage(tm, png, false) || tc.width != tm.width || tc.height != tm.height)
        { Object.Destroy(tc); Object.Destroy(tm); return null; }
        var pc = tc.GetPixels32(); var pa = tm.GetPixels32(); int w = tc.width, h = tc.height;
        if (v1) for (int i = 0; i < pc.Length; i++) pc[i].a = (byte)(pa[i].r >> 1);
        else for (int i = 0; i < pc.Length; i++) pc[i].a = pa[i].r;
        Object.Destroy(tc); Object.Destroy(tm);
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 1, hideFlags = HideFlags.DontUnloadUnusedAsset };
        t.SetPixels32(pc); t.Apply(true, false); t.Compress(true); t.Apply(false, true);
        return t;
    }

    // an imported atlas (Resources/<pack>_atlas.png: Unity's own compression, mips; nothing decoded at runtime)
    static Texture2D LoadAtlas(string path)
    {
        var t = Resources.Load<Texture2D>(path); if (t == null) return null;
        t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Trilinear; t.anisoLevel = 1;
        return t;
    }

    // v2: + hair-card triangles (second sub-mesh) and an accent region per vertex (vertex colour G)
    static Mesh ReadMesh2(System.IO.BinaryReader r, Body B, string name, int ver)
    {
        int nv = r.ReadInt32(), nt = r.ReadInt32(), nh = ver >= 2 ? r.ReadInt32() : 0;
        var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var uv = new Vector2[nv]; var col = new Color32[nv]; var bw = new BoneWeight[nv]; var tri = new int[nt * 3];
        for (int i = 0; i < nv; i++) pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        for (int i = 0; i < nv; i++) nrm[i] = new Vector3(r.ReadSByte(), r.ReadSByte(), r.ReadSByte()).normalized;
        for (int i = 0; i < nv; i++) uv[i] = new Vector2(r.ReadUInt16() / 65535f, r.ReadUInt16() / 65535f);
        for (int i = 0; i < nv; i++) { byte reg = r.ReadByte(); col[i] = new Color32(reg, reg, 0, 255); }
        if (ver >= 2) for (int i = 0; i < nv; i++) col[i].g = r.ReadByte();
        var bi = r.ReadBytes(nv * 4); var bwt = r.ReadBytes(nv * 4);
        for (int i = 0; i < nv; i++)
        {
            const float k = 1f / 255f;
            bw[i] = new BoneWeight { boneIndex0 = bi[i * 4], boneIndex1 = bi[i * 4 + 1], boneIndex2 = bi[i * 4 + 2], boneIndex3 = bi[i * 4 + 3],
                                     weight0 = bwt[i * 4] * k, weight1 = bwt[i * 4 + 1] * k, weight2 = bwt[i * 4 + 2] * k, weight3 = bwt[i * 4 + 3] * k };
        }
        for (int i = 0; i < tri.Length; i++) tri[i] = r.ReadUInt16();
        int[] trh = null;
        if (nh > 0) { trh = new int[nh * 3]; for (int i = 0; i < trh.Length; i++) trh[i] = r.ReadUInt16(); }
        var m = new Mesh { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
        m.vertices = pos; m.normals = nrm; m.uv = uv; m.colors32 = col; m.boneWeights = bw; m.bindposes = B.bind;
        if (trh == null) m.triangles = tri;
        else { m.subMeshCount = 2; m.SetTriangles(tri, 0, false); m.SetTriangles(trh, 1, false); }
        m.bounds = new Bounds(new Vector3(0, 0.95f, 0), new Vector3(2.4f, 2.2f, 2.4f));
        m.UploadMeshData(false);
        return m;
    }

    static Mesh ReadMesh(System.IO.BinaryReader r, Body B, string name)
    {
        int nv = r.ReadInt32(), nt = r.ReadInt32();
        var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var col = new Color32[nv]; var bw = new BoneWeight[nv]; var tri = new int[nt * 3];
        for (int i = 0; i < nv; i++) pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        for (int i = 0; i < nv; i++) nrm[i] = new Vector3(r.ReadSByte(), r.ReadSByte(), r.ReadSByte()).normalized;
        for (int i = 0; i < nv; i++) col[i] = new Color32(r.ReadByte(), 0, 0, 255);
        var bi = r.ReadBytes(nv * 4); var bwt = r.ReadBytes(nv * 4);
        for (int i = 0; i < nv; i++)
        {
            const float k = 1f / 255f;
            bw[i] = new BoneWeight { boneIndex0 = bi[i * 4], boneIndex1 = bi[i * 4 + 1], boneIndex2 = bi[i * 4 + 2], boneIndex3 = bi[i * 4 + 3],
                                     weight0 = bwt[i * 4] * k, weight1 = bwt[i * 4 + 1] * k, weight2 = bwt[i * 4 + 2] * k, weight3 = bwt[i * 4 + 3] * k };
        }
        for (int i = 0; i < tri.Length; i++) tri[i] = r.ReadUInt16();
        var m = new Mesh { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset }; // shared by every rig, for the whole session
        m.vertices = pos; m.normals = nrm; m.colors32 = col; m.boneWeights = bw; m.bindposes = B.bind; m.triangles = tri;
        m.bounds = new Bounds(new Vector3(0, 0.95f, 0), new Vector3(2.4f, 2.2f, 2.4f));
        m.UploadMeshData(false);
        return m;
    }

    internal static Material Mat()
    {
        if (mat != null) return mat;
        var sh = Resources.Load<Shader>("People/People"); if (sh == null || !sh.isSupported) sh = Shader.Find("Legacy Shaders/VertexLit");
        mat = new Material(sh) { name = "People", hideFlags = HideFlags.DontUnloadUnusedAsset };
        return mat;
    }
    internal static Material ShadowMat()
    {
        if (shadowMat != null) return shadowMat;
        var sh = Resources.Load<Shader>("People/PeopleShadow"); if (sh == null || !sh.isSupported) return null;
        shadowMat = new Material(sh) { name = "PeopleShadow", hideFlags = HideFlags.DontUnloadUnusedAsset };
        return shadowMat;
    }

    // ---------------------------------------------------------------- poses
    public sealed class PoseBuf { public readonly Quaternion[] q = new Quaternion[NJ]; public Vector3 p; }

    // a clip at time t (s; loops wrap, one-shots hold their ends)
    public static void Sample(int clip, float t, PoseBuf o)
    {
        var C = clips[Mathf.Clamp(clip, 0, clips.Length - 1)];
        float f = t * C.fps;
        int a, b; float u;
        if (C.loop) { f %= C.n; if (f < 0f) f += C.n; a = (int)f; u = f - a; b = a + 1 >= C.n ? 0 : a + 1; }
        else { f = Mathf.Clamp(f, 0f, C.n - 1.001f); a = (int)f; u = f - a; b = Mathf.Min(a + 1, C.n - 1); }
        int ia = a * NJ, ib = b * NJ;
        for (int j = 0; j < NJ; j++) o.q[j] = NLerp(C.q[ia + j], C.q[ib + j], u);
        o.p = Vector3.LerpUnclamped(C.p[a], C.p[b], u);
    }
    public static float Length(int clip) => clips[clip].len;

    // CPU: animation rate by distance (sampling a clip and writing 22 bone transforms is the cost): every frame within
    // 30 m, every 2nd frame to 60 m, every 4th to AnimFar (a person there is under ~25 px tall at 720p); beyond it the
    // last pose holds. `slot` staggers the people so the work spreads evenly over the frames.
    public const float AnimFar = 120f;
    public static bool AnimDue(float dist, int frame, int slot)
    {
        if (dist < 30f) return true;
        if (dist < 60f) return ((frame + slot) & 1) == 0;
        if (dist < AnimFar) return ((frame + slot) & 3) == 0;
        return false;
    }
    // o = a .. b by w (o may be a or b)
    public static void Blend(PoseBuf a, PoseBuf b, float w, PoseBuf o)
    {
        if (w <= 0.0005f) { if (o != a) { System.Array.Copy(a.q, o.q, NJ); o.p = a.p; } return; }
        if (w >= 0.9995f) { if (o != b) { System.Array.Copy(b.q, o.q, NJ); o.p = b.p; } return; }
        for (int j = 0; j < NJ; j++) o.q[j] = NLerp(a.q[j], b.q[j], w);
        o.p = Vector3.LerpUnclamped(a.p, b.p, w);
    }
    static Quaternion NLerp(Quaternion a, Quaternion b, float t)
    {
        float d = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w, s = d < 0f ? -t : t, r = 1f - t;
        float x = a.x * r + b.x * s, y = a.y * r + b.y * s, z = a.z * r + b.z * s, w = a.w * r + b.w * s;
        float m = Mathf.Sqrt(x * x + y * y + z * z + w * w); if (m < 1e-6f) return a;
        m = 1f / m; return new Quaternion(x * m, y * m, z * m, w * m);
    }

    // two-bone IK in a plane: from a to the target t with bone lengths l1, l2, the middle joint bent towards pole
    public static void TwoBone(Vector3 a, Vector3 t, float l1, float l2, Vector3 pole, out Vector3 mid, out Vector3 end)
    {
        Vector3 d = t - a; float len = d.magnitude;
        Vector3 dn = len > 1e-5f ? d / len : Vector3.down;
        float dist = Mathf.Clamp(len, Mathf.Abs(l1 - l2) + 0.02f, l1 + l2 - 0.003f);
        float ca = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f), sa = Mathf.Sqrt(1f - ca * ca);
        Vector3 pp = pole - dn * Vector3.Dot(pole, dn);
        if (pp.sqrMagnitude < 1e-8f) pp = Vector3.Cross(dn, Vector3.right);
        pp.Normalize();
        mid = a + dn * (l1 * ca) + pp * (l1 * sa); end = a + dn * dist;
    }

    // ---------------------------------------------------------------- dressing
    static Color C(float r, float g, float b) => new Color(r, g, b);
    static float Hash(int a, int b) { float x = Mathf.Sin(a * 12.9898f + b * 78.233f) * 43758.5453f; return x - Mathf.Floor(x); }
    static readonly Color[] Skins = { C(0.86f, 0.68f, 0.56f), C(0.78f, 0.58f, 0.45f), C(0.66f, 0.48f, 0.35f), C(0.50f, 0.35f, 0.24f), C(0.36f, 0.25f, 0.17f), C(0.92f, 0.76f, 0.64f) };
    static readonly Color[] Hairs = { C(0.08f, 0.06f, 0.05f), C(0.20f, 0.13f, 0.08f), C(0.38f, 0.26f, 0.15f), C(0.62f, 0.48f, 0.28f), C(0.45f, 0.43f, 0.42f) };
    static float Lum(Color c) => 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;

    // the palette of one person: outfit (OCrew overalls + helmet, OEngineer team shirt + dark trousers, OMechanic overalls,
    // OMarshal orange overalls), team livery, seed (skin tone, hair, small variations)
    public static void Palette(Vector4[] pal, int outfit, Livery l, int seed)
    {
        Color prim = l.primary, sec = l.secondary, dark = C(0.06f, 0.06f, 0.07f), white = C(0.9f, 0.9f, 0.91f);
        if (outfit == OMarshal) { prim = C(1f, 0.42f, 0.05f); sec = C(0.95f, 0.95f, 0.95f); }
        // the flank panels: white on a dark or saturated overall, black on a light one
        Color panel = Lum(prim) > 0.55f ? C(0.08f, 0.08f, 0.09f) : white;
        Color skin = Skins[(int)(Hash(seed, 1) * Skins.Length) % Skins.Length], hair = Hairs[(int)(Hash(seed, 2) * Hairs.Length) % Hairs.Length];
        if (skin.r < 0.55f && hair.r > 0.3f) hair = Hairs[0];
        float tint = 0.94f + 0.08f * Hash(seed, 4);
        Color primT = prim * tint, primDk = Color.Lerp(prim * 0.55f, dark, 0.25f);
        var c = new Color[NReg]; var g = new float[NReg];
        for (int i = 0; i < NReg; i++) { c[i] = primT; g[i] = 0.12f; }
        c[RSide] = panel; c[RYoke] = sec; c[RSleeve] = primT; c[RForearm] = primT; c[RGlove] = C(0.07f, 0.07f, 0.08f);
        c[RThigh] = primT; c[RStripe] = panel; c[RShin] = primDk; c[RBoot] = C(0.05f, 0.05f, 0.06f); c[RBelt] = dark; c[RKnee] = C(0.12f, 0.12f, 0.13f);
        c[RNeck] = dark; c[RFace] = skin; c[RHair] = hair;
        c[RHelmet] = prim; c[RHelmetTop] = sec; c[RVisor] = C(0.02f, 0.025f, 0.04f); c[RHeadset] = C(0.05f, 0.05f, 0.055f);
        c[RCap] = prim; c[RCapBrim] = Lum(sec) > 0.8f ? dark : sec;
        g[RHelmet] = g[RHelmetTop] = 0.7f; g[RVisor] = 1f; g[RHeadset] = 0.45f; g[RFace] = 0.18f; g[RBoot] = 0.35f; g[RBelt] = 0.3f;
        switch (outfit)
        {
            case OCrew: break;
            case OEngineer:
            {
                Color trousers = Hash(seed, 5) < 0.5f ? C(0.07f, 0.08f, 0.11f) : C(0.12f, 0.12f, 0.13f);
                c[RSide] = primT * 0.9f; c[RForearm] = skin; c[RGlove] = skin; c[RNeck] = skin; c[RBelt] = C(0.05f, 0.05f, 0.05f);
                c[RThigh] = c[RStripe] = c[RShin] = c[RKnee] = trousers; c[RBoot] = Hash(seed, 6) < 0.5f ? C(0.06f, 0.06f, 0.06f) : C(0.85f, 0.85f, 0.86f);
                g[RBoot] = 0.2f;
                break;
            }
            case OMechanic: c[RNeck] = skin; if (Hash(seed, 7) < 0.4f) { c[RGlove] = skin; } break;
            case OMarshal: c[RNeck] = skin; c[RGlove] = skin; c[RShin] = prim * 0.8f; break;
        }
        for (int i = 0; i < NReg; i++) { var lc = c[i].linear; pal[i] = new Vector4(lc.r, lc.g, lc.b, g[i]); }
    }

    // ---------------------------------------------------------------- one person
    public sealed class Rig
    {
        public readonly GameObject go; public readonly Transform tr; public readonly Transform[] b = new Transform[NJ];
        readonly SkinnedMeshRenderer smr; readonly Body body; readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        readonly Vector4[] pal = new Vector4[NReg];
        int gear = -1, lod = -1; bool shown = true; Material[] curMats;
        public float scale = 1f;
        public int Lod => lod;
        public bool Shown => shown;

        public Rig(Transform parent, int bodyIdx, int gear, string name)
        {
            body = bodies[Mathf.Clamp(bodyIdx, 0, bodies.Length - 1)];
            go = new GameObject(name); tr = go.transform; tr.SetParent(parent, false);
            go.layer = LightBake.CarLayer; // lit by the cars' sun, like the car they work on
            for (int j = 0; j < NJ; j++)
            {
                var t = new GameObject(j == JHead ? "Head" : "b" + j).transform;
                t.SetParent(Parent[j] < 0 ? tr : b[Parent[j]], false);
                t.localPosition = body.t[j]; t.localRotation = body.q[j];
                b[j] = t;
            }
            smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.bones = b; smr.rootBone = b[JPelvis]; smr.sharedMaterial = body.mat != null ? body.mat : Mat();
            smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; smr.receiveShadows = false;
            smr.updateWhenOffscreen = false; smr.skinnedMotionVectors = false;
            smr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; smr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            this.gear = gear; SetLod(0);
        }
        public void SetGear(int g) { if (g == gear) return; gear = g; int l = lod; lod = -1; SetLod(l); }
        public bool Real => body.real;
        // the LOD for a camera distance (m): realistic bodies 3 levels (~4k / 1.5k / 0.5k triangles), the old ones 2
        // (bodies with 4 LODs, the pack drivers: the full hero mesh within LodHero, then the three as the others)
        public void SetLodAt(float dist)
        {
            if (!body.real) { SetLod(dist > 40f ? 1 : 0); return; }
            int l = dist < LodNear ? 0 : dist < LodMid ? 1 : 2;
            if (body.mesh.GetLength(1) >= 4) l = dist < LodHero ? 0 : l + 1;
            SetLod(l);
        }
        public int LodCount => body.mesh.GetLength(1);
        public void SetLod(int l)
        {
            l = Mathf.Clamp(l, 0, body.mesh.GetLength(1) - 1); if (l == lod) return; lod = l;
            var ms = body.mesh[Mathf.Clamp(gear, 0, body.mesh.GetLength(0) - 1), l];
            smr.sharedMesh = ms;
            if (body.mat != null) { var want = ms.subMeshCount > 1 ? body.mats2 : body.mats1; if (want != curMats) { curMats = want; smr.sharedMaterials = want; } }
            smr.quality = l == 0 ? SkinQuality.Bone4 : SkinQuality.Bone2;
            smr.localBounds = new Bounds(Vector3.zero, new Vector3(3f, 3f, 3f)); // (root bone space: around the pelvis, any posture)
        }
        public void Dress(int outfit, Livery l, int seed) { Palette(pal, outfit, l, seed); mpb.SetVectorArray("_Pal", pal); smr.SetPropertyBlock(mpb); }
        // a race driver's suit in a livery (as MenuStudio / PreRace): team colour, a contrasting trim on the accent (stripes,
        // belt, cuffs), the helmet in `helmet` with the team colour as its second colour, dark visor
        public void DressDriver(Livery l, Color helmet) { DriverPalette(pal, l, helmet); mpb.SetVectorArray("_Pal", pal); smr.SetPropertyBlock(mpb); }
        public void Show(bool on) { if (on != shown) { shown = on; go.SetActive(on); } }
        public void Place(Vector3 pos, float yaw) { tr.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0)); tr.localScale = Vector3.one * scale; }

        // the pose into the bones (pelvis height from the clip, scaled to this body)
        public void Apply(PoseBuf p)
        {
            for (int j = 1; j < NJ; j++) b[j].localRotation = p.q[j];
            b[JPelvis].localRotation = p.q[0];
            b[JPelvis].localPosition = p.p * body.pscale;
        }
        // an arm onto a world point (the palm), weight w; pole = which way the elbow should point (world, optional)
        public void Arm(bool left, Vector3 target, float w, Vector3 pole = default)
        {
            if (w <= 0.001f) return;
            Transform U = b[left ? JUpL : JUpR], L = b[left ? JLoL : JLoR], H = b[left ? JHandL : JHandR];
            Vector3 a = U.position, e = L.position, h = H.position;
            Vector3 fd = h - e; float l2 = fd.magnitude; if (l2 < 1e-4f) return;
            Vector3 palm = (fd / l2) * (0.075f * scale);
            Vector3 t = Vector3.Lerp(h, target - palm, Mathf.Clamp01(w));
            float l1 = (e - a).magnitude;
            Vector3 bend = e - (a + h) * 0.5f; if (bend.sqrMagnitude < 1e-6f) bend = -tr.forward * 0.1f;
            Vector3 pl = bend.normalized + (pole.sqrMagnitude > 1e-6f ? pole.normalized * 0.8f : Vector3.zero) + (left ? -tr.right : tr.right) * 0.25f - tr.up * 0.15f;
            TwoBone(a, t, l1, l2, pl, out Vector3 mid, out Vector3 end);
            U.rotation = Quaternion.FromToRotation(e - a, mid - a) * U.rotation;
            Vector3 e2 = L.position, h2 = H.position;
            L.rotation = Quaternion.FromToRotation(h2 - e2, end - e2) * L.rotation;
        }
        // turn the neck and head towards a world point (limited to +-75 deg left / right, +-40 up / down)
        public void LookAt(Vector3 target, float w)
        {
            if (w <= 0.001f) return;
            Transform N = b[JNeck], H = b[JHead];
            Vector3 d = tr.InverseTransformDirection(target - H.position); if (d.sqrMagnitude < 1e-4f) return;
            float yaw = Mathf.Clamp(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, -75f, 75f), pit = Mathf.Clamp(Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, -45f, 40f);
            Vector3 want = tr.TransformDirection(Quaternion.Euler(-pit, yaw, 0) * Vector3.forward);
            Vector3 cur = H.rotation * body.headFwd;
            Quaternion full = Quaternion.FromToRotation(cur, want);
            Quaternion part = Quaternion.Slerp(Quaternion.identity, full, 0.4f * w);
            N.rotation = part * N.rotation;
            cur = H.rotation * body.headFwd;
            H.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(cur, want), w) * H.rotation;
        }
        public Vector3 Palm(bool left)
        {
            Transform L = b[left ? JLoL : JLoR], H = b[left ? JHandL : JHandR];
            Vector3 h = H.position, d = h - L.position;
            return h + d.normalized * (0.075f * scale);
        }
        public Vector3 Pelvis => b[JPelvis].position;
        public Vector3 ForearmDir(bool left) { Vector3 d = b[left ? JHandL : JHandR].position - b[left ? JLoL : JLoR].position; return d.sqrMagnitude > 1e-8f ? d.normalized : tr.forward; }
        public void Destroy() { if (go != null) Object.Destroy(go); }
    }

    // a random-ish body / height for a person (BodyOf: the old Quaternius bodies, used by the baked far crews)
    public static int BodyOf(int seed, float femaleShare = 0.25f) => Hash(seed, 11) < femaleShare ? 1 : 0;
    public static float HeightOf(int seed, int body)
    {
        if (IsReal(body)) return 0.965f + 0.065f * Hash(seed, 12); // (People2 bodies have their real height)
        return body == 1 ? 0.905f + 0.07f * Hash(seed, 12) : 0.94f + 0.09f * Hash(seed, 12); // ~1.60-1.72 m / ~1.70-1.88 m
    }
    // a realistic body when there is one (People2: the Mixamo crew, the Avatar), else BodyOf; overalls: only bodies in
    // long trousers (the stop crew: not Remy's shorts)
    // The Rocketbox engineers first (team shirts; their variant 0 = the crew helmet, so GHelmet gives the stop crew helmets);
    // the stop crew (overalls): only the engineers in trousers; others mix in the Mixamo crew (40%) when that pack is there.
    public static int CrewBodyOf(int seed, float femaleShare = 0.25f, bool overalls = false)
    {
        if (engM.Count + engF.Count > 0 && (overalls || crewM.Count + crewF.Count == 0 || Hash(seed, 14) < 0.6f))
        {
            var el = !overalls && Hash(seed, 11) < femaleShare && engF.Count > 0 ? engF : engM.Count > 0 ? engM : engF;
            return el[(int)(Hash(seed, 13) * el.Count) % el.Count];
        }
        if (crewM.Count == 0 && crewF.Count == 0) return BodyOf(seed, femaleShare);
        var list = Hash(seed, 11) < femaleShare && crewF.Count > 0 ? crewF : crewM.Count > 0 ? crewM : crewF;
        int n = list.Count, k = (int)(Hash(seed, 13) * n) % n;
        for (int i = 0; i < n && overalls && bodies[list[k]].name == "Remy"; i++) k = (k + 1) % n;
        return list[k];
    }
    // the Rocketbox marshals (orange overalls) / fans / an engineer for a seed; -1 when there are none (MarshalBodyOf: the
    // crew body then). EngineerBodyOf: for PitGarage's baked far crews (falls back to BodyOf, the Quaternius bodies).
    public static int MarshalBodyOf(int seed) => marshalL.Count > 0 ? marshalL[(int)(Hash(seed, 15) * marshalL.Count) % marshalL.Count] : CrewBodyOf(seed, 0.2f);
    public static int FanBodyOf(int seed) => fanL.Count > 0 ? fanL[(int)(Hash(seed, 16) * fanL.Count) % fanL.Count] : -1;
    public static int EngineerBodyOf(int seed, float femaleShare = 0.25f)
    {
        if (engM.Count + engF.Count == 0) return BodyOf(seed, femaleShare);
        var el = Hash(seed, 11) < femaleShare && engF.Count > 0 ? engF : engM.Count > 0 ? engM : engF;
        return el[(int)(Hash(seed, 13) * el.Count) % el.Count];
    }
    public static int FanCount => fanL.Count;
    // the material a body draws with (its pack's textured People2 material, else the palette one): for baked meshes
    public static Material MatOf(int body) => bodies != null && body >= 0 && body < bodies.Length && bodies[body].mat != null ? bodies[body].mat : Mat();
    public static bool IsReal(int body) => bodies != null && body >= 0 && body < bodies.Length && bodies[body].real;
    public const float LodNear = 15f, LodMid = 45f, LodHero = 9f;

    static void P(Vector4[] pal, int i, Color c, float gloss) { var lc = c.linear; pal[i] = new Vector4(lc.r, lc.g, lc.b, gloss); }
    public static void DriverPalette(Vector4[] pal, Livery l, Color helmet)
    {
        Color prim = l.primary, sec = l.secondary, dark = C(0.05f, 0.05f, 0.06f);
        Color trim = Mathf.Abs(Lum(sec) - Lum(prim)) < 0.12f ? (Lum(prim) > 0.5f ? dark : C(0.9f, 0.9f, 0.91f)) : sec;
        for (int i = 0; i < NReg; i++) P(pal, i, prim, 0.15f);
        P(pal, RSide, trim, 0.15f); P(pal, RYoke, trim, 0.15f); P(pal, RStripe, trim, 0.15f);
        P(pal, RGlove, Color.Lerp(trim, dark, 0.35f), 0.2f); P(pal, RShin, prim * 0.85f, 0.15f); P(pal, RKnee, prim, 0.15f);
        P(pal, RBoot, dark, 0.35f); P(pal, RBelt, dark, 0.3f); P(pal, RNeck, C(0.08f, 0.08f, 0.09f), 0.1f);
        P(pal, RFace, C(0.86f, 0.68f, 0.56f), 0.18f); P(pal, RHair, C(0.20f, 0.13f, 0.08f), 0.1f);
        P(pal, RHelmet, helmet, 0.8f); P(pal, RHelmetTop, prim, 0.8f); P(pal, RVisor, C(0.02f, 0.025f, 0.04f), 1f);
    }

    // ---------------------------------------------------------------- podium ceremony (API at the top of the file)
    public const int ActIdle = 0, ActClap = 1, ActCheer = 2, ActWave = 3, ActTalk = 4, ActFist = 5;
    public const int RoleDriver = 0, RoleOfficial = 1, RolePhotographer = 2, RolePresenter = 3, RoleFan = 4;
    public static int ActClip(int act)
    {
        switch (act)
        {
            case ActClap: return MClap;      // (no mocap pack: Celebrate)
            case ActCheer: return Celebrate; // (no Cheer mocap in the uploads)
            case ActWave: return MWave;
            case ActTalk: return MTalk;
            case ActFist: return Yes;
            default: return MIdle;
        }
    }
    // P1, P2, P3 on the steps (podium frame: +z = facing, +x = the drivers' right; y = the step's top)
    public static readonly Vector3[] PodiumSpots = { new Vector3(0f, 1.05f, 0f), new Vector3(1.7f, 0.75f, 0.05f), new Vector3(-1.7f, 0.5f, 0.05f) };

    public sealed class CastMember
    {
        public readonly Rig rig; public readonly int role; public readonly Transform prop;
        public float raise, hold, aim, mic, look, speed = 1f; public Vector3 lookAt;
        internal int act = ActIdle, prevAct = -1, slot; internal float t, prevT, fade = 1f, fadeLen = 0.35f;
        internal Vector3 pos; internal float yaw; internal bool moved;
        public int Act => act;
        internal CastMember(Rig r, int role, Transform propParent, int slot)
        {
            rig = r; this.role = role; this.slot = slot;
            prop = new GameObject("Prop").transform; prop.SetParent(propParent, false);
        }
        public void Place(Vector3 world, float yawDeg) { pos = world; yaw = yawDeg; rig.Place(world, yawDeg); moved = true; }
        public Vector3 Position => pos;
    }

    public sealed class PodiumCast
    {
        public readonly GameObject root;
        public readonly CastMember[] drivers = new CastMember[3];
        public readonly CastMember player;
        public CastMember official1, official2, photographer, presenter;
        public readonly List<CastMember> fans = new List<CastMember>(), all = new List<CastMember>();
        readonly Blobs blobs; readonly PoseBuf pa = new PoseBuf(), pb = new PoseBuf(); int frame; bool shown = true;
        public float LodDistanceScale = 1f; // (< 1: finer LODs for a close TV shot, e.g. 0.5 keeps the drivers' hero mesh to 18 m)

        internal PodiumCast(Vector3 podiumPos, Quaternion facing, int[] teams, bool playerOn, int playerPos, int nFans, Vector3[] spots, Transform parent, Livery? playerLiv)
        {
            root = new GameObject("PodiumCast"); root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(podiumPos, facing);
            float fy = facing.eulerAngles.y; Vector3 fwd = facing * Vector3.forward;
            if (spots == null || spots.Length < 3) spots = PodiumSpots;
            int pp = playerOn ? Mathf.Clamp(playerPos, 1, 3) - 1 : -1, rival = 0;
            Livery pl = playerLiv ?? Livery.Player[Livery.PlayerIndex];
            for (int i = 0; i < 3; i++)
            {
                bool me = i == pp;
                int body = me || Driver2Body < 0 ? DriverBody : (rival++ & 1) == 0 ? Driver2Body : DriverBody;
                if (body < 0) body = 0;
                var r = new Rig(root.transform, body, GHelmet, me ? "PodiumPlayer" : "PodiumDriver" + (i + 1));
                Livery l = me ? pl : Livery.ForTeam(teams != null && i < teams.Length ? teams[i] : i, C(0.5f, 0.5f, 0.5f));
                r.DressDriver(l, me ? PlayerHelmet : RivalHelmet(l));
                var m = Add(r, RoleDriver);
                m.Place(podiumPos + facing * spots[i], fy);
                m.lookAt = podiumPos + fwd * 8f + Vector3.up * 1.6f; m.look = 0.5f;
                drivers[i] = m; if (me) player = m;
            }
            // staff (ground level beside / in front of the podium; re-place them with CastMember.Place)
            Vector3 P1 = drivers[0].pos + Vector3.up * 1.6f;
            official1 = Staff(officialL.Count > 0 ? officialL[0] : EngineerBodyOf(7), RoleOfficial, podiumPos + facing * new Vector3(3.3f, 0f, -0.3f), fy - 70f, ActClap, P1);
            official2 = Staff(officialL.Count > 1 ? officialL[1] : EngineerBodyOf(8), RoleOfficial, podiumPos + facing * new Vector3(-3.3f, 0f, -0.3f), fy + 70f, ActClap, P1);
            photographer = Staff(photoBody >= 0 ? photoBody : EngineerBodyOf(9), RolePhotographer, podiumPos + facing * new Vector3(2.1f, 0f, 3.7f), fy + 200f, ActIdle, P1);
            photographer.aim = 1f;
            presenter = Staff(presenterBody >= 0 ? presenterBody : EngineerBodyOf(10), RolePresenter, podiumPos + facing * new Vector3(-3.8f, 0f, 2.3f), fy + 25f, ActTalk, podiumPos + fwd * 8f + Vector3.up * 1.5f);
            presenter.mic = 0.85f;
            // the crowd: rows in front, facing the podium, looking at the winner
            int nf = Mathf.Max(0, nFans);
            for (int i = 0; i < nf; i++)
            {
                int seed = 4100 + i * 29, body = fanL.Count > 0 ? fanL[(i * 7 + (int)(Hash(seed, 3) * 3f)) % fanL.Count] : CrewBodyOf(seed, 0.4f);
                int row = i / 6, col = i % 6;
                float x = (col - 2.5f) * 1.25f + (Hash(seed, 1) - 0.5f) * 0.5f + (row & 1) * 0.6f, z = 6.2f + row * 1.15f + (Hash(seed, 2) - 0.5f) * 0.4f;
                var r = new Rig(root.transform, body, GHeadset, "Fan" + i);
                r.Dress(OEngineer, Livery.Player[0], seed);
                var m = Add(r, RoleFan);
                m.Place(podiumPos + facing * new Vector3(x, 0f, z), fy + 180f + (Hash(seed, 4) - 0.5f) * 30f - x * 3f);
                m.lookAt = P1; m.look = 0.6f; m.speed = 0.92f + 0.16f * Hash(seed, 5);
                int a = (int)(Hash(seed, 6) * 3f); m.act = a == 0 ? ActCheer : a == 1 ? ActClap : ActWave; m.t = Hash(seed, 7) * 3f;
                fans.Add(m);
            }
            blobs = new Blobs(root.transform, all.Count, "PodiumCastShadows");
            Shadows();
        }
        void Shadows()
        {
            for (int i = 0; i < all.Count; i++) { all[i].moved = false; blobs.Set(i, all[i].pos, Quaternion.Euler(0f, all[i].yaw, 0f) * Vector3.forward, 0.34f, 0.8f, 0.5f); }
            blobs.Flush();
        }
        CastMember Add(Rig r, int role) { var m = new CastMember(r, role, root.transform, all.Count); all.Add(m); return m; }
        CastMember Staff(int body, int role, Vector3 pos, float yaw, int act, Vector3 look)
        {
            var r = new Rig(root.transform, body, GHeadset, role == RoleOfficial ? "Official" : role == RolePhotographer ? "Photographer" : "Presenter");
            r.Dress(OEngineer, Livery.Player[0], all.Count * 13 + 3);
            var m = Add(r, role); m.Place(pos, yaw); m.act = act; m.lookAt = look; m.look = 0.7f;
            return m;
        }
        static Color RivalHelmet(Livery l) => Lum(l.secondary) > 0.45f ? l.secondary : Lum(l.accent) > 0.45f ? l.accent : C(0.92f, 0.92f, 0.93f);

        // a new act for one member (cross-fade over `fade` s; the same act again: nothing)
        public void Play(CastMember m, int act, float fade = 0.35f)
        {
            if (m == null || m.act == act) return;
            m.prevAct = m.act; m.prevT = m.t; m.act = act; m.t = 0f; m.fade = 0f; m.fadeLen = Mathf.Max(0.01f, fade);
        }
        // the same act for a group, each start a little apart (stagger s)
        public void PlayAll(List<CastMember> list, int act, float stagger = 0.4f)
        {
            for (int i = 0; i < list.Count; i++) { var m = list[i]; if (m.act == act) continue; Play(m, act); m.t = -stagger * Hash(m.slot, 9); }
        }
        public void Show(bool on)
        {
            if (on == shown) return; shown = on;
            for (int i = 0; i < all.Count; i++) all[i].rig.Show(on);
            blobs.Show(on);
        }

        // every frame: clips (cross-fades), arm IK (trophy up / held, camera, microphone), head turns, LODs, the props
        public void Tick(float dt, Vector3 cam)
        {
            if (!shown) return;
            frame++;
            bool moved = false;
            for (int i = 0; i < all.Count; i++) moved |= all[i].moved;
            if (moved) Shadows();
            for (int i = 0; i < all.Count; i++)
            {
                var m = all[i]; var r = m.rig;
                m.t += dt * m.speed; if (m.fade < m.fadeLen) { m.fade += dt; m.prevT += dt * m.speed; }
                float dist = Vector3.Distance(cam, m.pos) * LodDistanceScale;
                r.SetLodAt(dist);
                if (!AnimDue(dist, frame, m.slot)) continue;
                SampleAct(m.act, m.t, pa);
                if (m.prevAct >= 0 && m.fade < m.fadeLen) { SampleAct(m.prevAct, m.prevT, pb); Blend(pb, pa, Smooth(m.fade / m.fadeLen), pa); }
                r.Apply(pa);
                Transform T = r.tr; Vector3 f = T.forward, rt = T.right, up = Vector3.up;
                Vector3 head = r.b[JHead].position;
                if (m.aim > 0.001f)
                {
                    r.Arm(false, head + f * 0.17f + rt * 0.05f - up * 0.03f, m.aim, rt - f * 0.3f);
                    r.Arm(true, head + f * 0.26f - rt * 0.02f - up * 0.08f, m.aim, -rt - up * 0.5f);
                }
                else if (m.mic > 0.001f) r.Arm(false, head + f * 0.13f + rt * 0.02f - up * 0.09f, m.mic, rt - up * 0.6f);
                else if (m.raise > 0.001f || m.hold > 0.001f)
                {
                    Vector3 sh = r.b[JUpR].position;
                    Vector3 hold = sh + f * 0.36f - up * 0.24f - rt * 0.1f, high = sh + up * 0.62f * r.scale + f * 0.1f + rt * 0.08f;
                    float w = Mathf.Max(m.raise, m.hold);
                    r.Arm(false, Vector3.Lerp(hold, high, Smooth(m.raise)), w, Vector3.Lerp(-up + rt * 0.5f, rt - f * 0.4f, m.raise));
                }
                if (m.look > 0.001f) r.LookAt(m.lookAt, m.look);
                // the prop in the right palm: upright, facing the member's forward (tilted a little with a raised arm)
                Vector3 palm = r.Palm(false);
                m.prop.SetPositionAndRotation(palm, Quaternion.LookRotation(f, Vector3.Lerp(up, r.ForearmDir(false), 0.15f * m.raise)));
            }
        }
        static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        static void SampleAct(int act, float t, PoseBuf o)
        {
            int c = ActClip(act); float len = Length(c);
            if (t < 0f) t = 0f;
            if (!clips[c].loop) t %= len + 0.5f; // (one-shot clips repeat after a short hold)
            Sample(c, t, o);
        }
        public void Destroy()
        {
            for (int i = 0; i < all.Count; i++) all[i].rig.Destroy();
            blobs.Destroy();
            if (root != null) Object.Destroy(root);
        }
    }

    // the podium ceremony's people (API at the top of the file); null when the people data is missing
    public static PodiumCast SpawnPodiumCast(Vector3 podiumPos, Quaternion facing, int[] teamOfTop3, bool playerOnPodium, int playerPos,
                                             int fans = 14, Vector3[] driverSpots = null, Transform parent = null, Livery? playerLivery = null)
    {
        if (!PreparePodium()) return null;
        return new PodiumCast(podiumPos, facing, teamOfTop3, playerOnPodium, playerPos, fans, driverSpots, parent, playerLivery);
    }

    // ---------------------------------------------------------------- contact shadows
    public sealed class Blobs
    {
        readonly Mesh mesh; readonly Vector3[] v; readonly Color32[] c; readonly GameObject go; readonly int n;
        Vector3 lo, hi; bool any;
        public Blobs(Transform parent, int count, string name)
        {
            n = count; v = new Vector3[n * 4]; c = new Color32[n * 4];
            var uv = new Vector2[n * 4]; var t = new int[n * 6];
            for (int i = 0; i < n; i++)
            {
                uv[i * 4] = new Vector2(-1, -1); uv[i * 4 + 1] = new Vector2(-1, 1); uv[i * 4 + 2] = new Vector2(1, 1); uv[i * 4 + 3] = new Vector2(1, -1);
                t[i * 6] = i * 4; t[i * 6 + 1] = i * 4 + 1; t[i * 6 + 2] = i * 4 + 2; t[i * 6 + 3] = i * 4; t[i * 6 + 4] = i * 4 + 2; t[i * 6 + 5] = i * 4 + 3;
            }
            mesh = new Mesh { name = name }; mesh.MarkDynamic();
            mesh.vertices = v; mesh.uv = uv; mesh.colors32 = c; mesh.triangles = t;
            go = new GameObject(name); go.transform.SetParent(parent, false);
            var m = ShadowMat();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            if (m == null) go.SetActive(false);
            Begin();
        }
        public void Begin() { any = false; for (int i = 0; i < v.Length; i++) { v[i] = Vector3.zero; c[i].a = 0; } }
        // one soft shadow at ground point p (y = ground), radius r along the facing f and r * across across it, strength a
        public void Set(int i, Vector3 p, Vector3 f, float r, float across, float a)
        {
            if (i < 0 || i >= n) return;
            f.y = 0; if (f.sqrMagnitude < 1e-6f) f = Vector3.forward; f.Normalize();
            Vector3 s = new Vector3(f.z, 0, -f.x) * (r * across); f *= r; p.y += 0.02f;
            v[i * 4] = p - s - f; v[i * 4 + 1] = p - s + f; v[i * 4 + 2] = p + s + f; v[i * 4 + 3] = p + s - f;
            byte al = (byte)(Mathf.Clamp01(a) * 255f);
            for (int k = 0; k < 4; k++) c[i * 4 + k] = new Color32(255, 255, 255, al);
            if (!any) { lo = hi = p; any = true; } else { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        }
        public void Flush()
        {
            mesh.vertices = v; mesh.colors32 = c;
            mesh.bounds = any ? new Bounds((lo + hi) * 0.5f, hi - lo + Vector3.one * 3f) : new Bounds(Vector3.zero, Vector3.one);
        }
        public void Show(bool on) { if (go.activeSelf != on && ShadowMat() != null) go.SetActive(on); }
        public void Destroy() { if (mesh != null) Object.Destroy(mesh); if (go != null) Object.Destroy(go); }
    }
}
