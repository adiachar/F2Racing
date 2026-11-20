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
// Realistic people (People2, Tools/People2/build.py; 'PPL2' packs, bound to the SAME 22-joint skeleton with the same rest
// orientations, so every clip, the IK and the pit-stop choreography drive them unchanged): textured from one atlas per
// pack, team colours through the same palette (People2.shader: texture alpha = how much a texel takes its region's
// palette colour), 3 LODs (SetLodAt):
//  * Resources/People2/people2.bytes (public, CC-BY-4.0, CREDITS.txt): "Driver" (race suit, gloves, HANS, helmet: the
//    player's driver in the menu studio and the pre-race walk) and "Avatar" (polo, chinos, cap: crew / engineers).
//  * Resources/People2/mixamo/mixamo.bytes (PRIVATE: Mixamo licence, not in the public repo, .gitignore; CI checks it out
//    from the private assets repo): crew bodies Ch08 / Ch13 (female) / Remy and the mocap clips MIdle, MWalk, MTalk,
//    MTalk2, MClap, MSitClap, MWave, MBriefcase. Without it those clip ids fall back to the nearest Quaternius clip
//    (Mocap = false) and the crew use the Avatar; without either pack everything is as before.
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
    public const string Credit = "Driver based on \"Race Car Driver - Low Poly\" by tomkart, crew avatar based on \"Free Avatar for iClone\" by Asfandyar Hesami (Sketchfab), CC BY 4.0. Modified: rigged, logos removed, recoloured.";
    static readonly List<int> crewM = new List<int>(), crewF = new List<int>();
    public const int GHelmet = 0, GHeadset = 1, GCap = 2, GBare = 3;
    public const int OCrew = 0, OEngineer = 1, OMechanic = 2, OMarshal = 3;

    internal static int[] Parent;
    internal sealed class Body
    {
        public float pscale; public Vector3[] t = new Vector3[NJ]; public Quaternion[] q = new Quaternion[NJ];
        public Matrix4x4[] bind = new Matrix4x4[NJ]; public Mesh[,] mesh;
        public Vector3 headFwd, headUp; // the head's forward / up in its own frame (from the rest pose)
        public Material mat;            // null: the palette material (People.shader); else its pack's textured one
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
        crewM.Clear(); crewF.Clear(); DriverBody = -1; int avatar = -1;
        for (int b = bs.Length; b < bl.Count; b++)
        {
            if (bl[b].name == "Driver") DriverBody = b;
            else if (bl[b].name == "Avatar") avatar = b;
            else (bl[b].female ? crewF : crewM).Add(b);
        }
        if (avatar >= 0) crewM.Add(avatar);
        bodies = bl.ToArray(); clips = all;
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
            r.ReadInt32(); if (r.ReadInt32() != NJ) return;
            var jpg = r.ReadBytes((int)r.ReadUInt32()); var png = r.ReadBytes((int)r.ReadUInt32());
            Material mat = null;
            var sh = Resources.Load<Shader>("People/People2");
            if (sh != null && sh.isSupported)
            {
                var tex = DecodeAtlas(jpg, png, path);
                if (tex != null) mat = new Material(sh) { name = "People2", mainTexture = tex, hideFlags = HideFlags.DontUnloadUnusedAsset };
            }
            int nb = r.ReadInt32(); var add = new List<Body>();
            for (int b = 0; b < nb; b++)
            {
                int ln = r.ReadByte(); string name = new string(r.ReadChars(ln)); int flags = r.ReadByte();
                var B = new Body { pscale = r.ReadSingle(), real = true, female = (flags & 1) != 0, name = name, mat = mat };
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
                    for (int l = 0; l < nl; l++) B.mesh[v, l] = ReadMesh2(r, B, name + "_" + v + "_" + l);
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
    static Texture2D DecodeAtlas(byte[] jpg, byte[] png, string name)
    {
        var tc = new Texture2D(2, 2, TextureFormat.RGBA32, false, false); var tm = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        if (!ImageConversion.LoadImage(tc, jpg, false) || !ImageConversion.LoadImage(tm, png, false) || tc.width != tm.width || tc.height != tm.height)
        { Object.Destroy(tc); Object.Destroy(tm); return null; }
        var pc = tc.GetPixels32(); var pa = tm.GetPixels32(); int w = tc.width, h = tc.height;
        for (int i = 0; i < pc.Length; i++) pc[i].a = pa[i].r;
        Object.Destroy(tc); Object.Destroy(tm);
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 1, hideFlags = HideFlags.DontUnloadUnusedAsset };
        t.SetPixels32(pc); t.Apply(true, false); t.Compress(true); t.Apply(false, true);
        return t;
    }

    static Mesh ReadMesh2(System.IO.BinaryReader r, Body B, string name)
    {
        int nv = r.ReadInt32(), nt = r.ReadInt32();
        var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var uv = new Vector2[nv]; var col = new Color32[nv]; var bw = new BoneWeight[nv]; var tri = new int[nt * 3];
        for (int i = 0; i < nv; i++) pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        for (int i = 0; i < nv; i++) nrm[i] = new Vector3(r.ReadSByte(), r.ReadSByte(), r.ReadSByte()).normalized;
        for (int i = 0; i < nv; i++) uv[i] = new Vector2(r.ReadUInt16() / 65535f, r.ReadUInt16() / 65535f);
        for (int i = 0; i < nv; i++) col[i] = new Color32(r.ReadByte(), 0, 0, 255);
        var bi = r.ReadBytes(nv * 4); var bwt = r.ReadBytes(nv * 4);
        for (int i = 0; i < nv; i++)
        {
            const float k = 1f / 255f;
            bw[i] = new BoneWeight { boneIndex0 = bi[i * 4], boneIndex1 = bi[i * 4 + 1], boneIndex2 = bi[i * 4 + 2], boneIndex3 = bi[i * 4 + 3],
                                     weight0 = bwt[i * 4] * k, weight1 = bwt[i * 4 + 1] * k, weight2 = bwt[i * 4 + 2] * k, weight3 = bwt[i * 4 + 3] * k };
        }
        for (int i = 0; i < tri.Length; i++) tri[i] = r.ReadUInt16();
        var m = new Mesh { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
        m.vertices = pos; m.normals = nrm; m.uv = uv; m.colors32 = col; m.boneWeights = bw; m.bindposes = B.bind; m.triangles = tri;
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
        int gear = -1, lod = -1; bool shown = true;
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
        public void SetLodAt(float dist) { SetLod(body.real ? (dist < LodNear ? 0 : dist < LodMid ? 1 : 2) : dist > 40f ? 1 : 0); }
        public void SetLod(int l)
        {
            l = Mathf.Clamp(l, 0, body.mesh.GetLength(1) - 1); if (l == lod) return; lod = l;
            smr.sharedMesh = body.mesh[Mathf.Clamp(gear, 0, body.mesh.GetLength(0) - 1), l];
            smr.quality = l == 0 ? SkinQuality.Bone4 : SkinQuality.Bone2;
            smr.localBounds = new Bounds(Vector3.zero, new Vector3(3f, 3f, 3f)); // (root bone space: around the pelvis, any posture)
        }
        public void Dress(int outfit, Livery l, int seed) { Palette(pal, outfit, l, seed); mpb.SetVectorArray("_Pal", pal); smr.SetPropertyBlock(mpb); }
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
    public static int CrewBodyOf(int seed, float femaleShare = 0.25f, bool overalls = false)
    {
        if (crewM.Count == 0 && crewF.Count == 0) return BodyOf(seed, femaleShare);
        var list = Hash(seed, 11) < femaleShare && crewF.Count > 0 ? crewF : crewM.Count > 0 ? crewM : crewF;
        int n = list.Count, k = (int)(Hash(seed, 13) * n) % n;
        for (int i = 0; i < n && overalls && bodies[list[k]].name == "Remy"; i++) k = (k + 1) % n;
        return list[k];
    }
    public static bool IsReal(int body) => bodies != null && body >= 0 && body < bodies.Length && bodies[body].real;
    public const float LodNear = 15f, LodMid = 45f;

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
