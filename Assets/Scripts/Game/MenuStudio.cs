// MenuStudio.cs — the 3D behind the front end (MenuUI): a dark studio garage with the player's driver standing alone
// in the middle, lit like a showroom. Only the driver: no car (fewer objects, cheaper).
//  * Where: far below the circuit (Origin), on its own layer (Layer 30, unused by the game). Only the studio camera
//    draws that layer, and while the studio exists the game's main camera is switched off (the circuit is not
//    rendered at all behind the menu). Every light that exists is told to skip the layer, so the track's sun does not
//    reach the stage.
//  * Driver: one of the rigged CC0 people (People.cs: Quaternius, crew helmet with a dark visor) in a race suit in the
//    livery colours, standing relaxed: idle clip with the arms hanging naturally (IK), chest and shoulders relaxed,
//    a normal stance; small breathing, weight shift and head movement, and on Home every 6-12 s a calm gesture
//    (adjust the gloves, a hand to the visor, a glance aside, fold the arms, nod).
//  * Light: a key light (real directional light, studio layer only); fill, rim and floor bounce baked into custom
//    spherical harmonics given to the driver (People.shader reads ShadeSH9). The set (floor pool, curved back wall,
//    LED strips, a red line at the wall base, an overhead softbox) is ONE vertex-coloured mesh (Baked.shader): one
//    draw call. No reflection probe, no render textures. Fog is switched off only while the studio camera renders.
//  * Cost rules: everything is created in Create() when a menu page needs it and destroyed in Release() (MenuUI calls
//    it in BeginLoading; GameManager.StartSession calls it again to be sure): GameObjects, the set mesh and material,
//    the camera and light, the driver rig and its shadow mesh; the main camera is switched back on and the frame rate
//    the menu capped (MenuFps) is given back. Nothing here has an Update: MenuUI calls Tick() once a frame while a
//    menu page is up, never in a race.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class MenuStudio
{
    public const int Layer = 30;
    public const int MenuFps = 30;
    public const int ShotHome = 0, ShotDriver = 1, ShotHidden = 3;
    static readonly Vector3 Origin = new Vector3(0f, -1500f, 0f);
    // stage positions (studio frame, metres): the driver at the centre facing the camera (-z); the set centred behind him
    static readonly Vector3 DriverAt = Vector3.zero, PoolAt = new Vector3(0f, 0f, 1.2f);
    static readonly Color HelmetCol = new Color(1f, 0.82f, 0.18f); // the player's helmet (as CarModel's driver)

    GameObject root; Camera cam, mainCam; Transform driverPivot;
    People.Rig rig; People.Blobs blob; SkinnedMeshRenderer rigSmr; MaterialPropertyBlock rigBlock;
    readonly Vector4[] pal = new Vector4[People.NReg];
    readonly List<Object> made = new List<Object>();          // meshes / materials made here: destroyed in Release
    readonly SphericalHarmonicsL2[] sh = new SphericalHarmonicsL2[1];
    readonly People.PoseBuf buf = new People.PoseBuf(), buf2 = new People.PoseBuf();
    Camera.CameraCallback preCb, postCb;
    int savedFps = -1; bool fogWas, fogOff;
    Vector3 camPos, camLook; float camFov; bool camSet;
    float driverYaw, spinVel, dragLastX, dragLastT; bool dragging;
    int gKind = -1, gLast = -1; float gT, gLen, nextG;          // the current gesture (-1 none), its time and length
    int calfR = -1;                                            // the right calf bone (People has no constant for it)

    public bool Alive => root != null;

    // ------------------------------------------------------------------ create / release
    public void Create(Livery liv)
    {
        if (root != null) return;
        root = new GameObject("MenuStudio");
        root.transform.position = Origin;
        // the track's lights (sun, car sun...) must not light the stage
        var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
        for (int i = 0; i < lights.Length; i++) lights[i].cullingMask &= ~(1 << Layer);
        BuildLighting();
        BuildSet();
        driverPivot = Child("DriverPivot", DriverAt, 0f);
        if (People.Ready) BuildDriver(liv);
        BuildCamera();
        SetLayer(root.transform);
        mainCam = Camera.main;
        if (mainCam != null && mainCam != cam) mainCam.enabled = false; // the circuit is not drawn behind the menu
        if (preCb == null) { preCb = OnPre; postCb = OnPost; }
        Camera.onPreRender += preCb; Camera.onPostRender += postCb;
        nextG = Time.unscaledTime + 4f; gKind = -1; camSet = false; driverYaw = spinVel = 0f; dragging = false;
    }

    public void Release()
    {
        if (root == null) return;
        if (preCb != null) { Camera.onPreRender -= preCb; Camera.onPostRender -= postCb; }
        if (fogOff) { RenderSettings.fog = fogWas; fogOff = false; }
        if (blob != null) blob.Destroy();
        blob = null; rig = null; rigSmr = null; rigBlock = null;
        Object.Destroy(root); // camera, key light, driver rig, set
        root = null; cam = null; driverPivot = null;
        for (int i = 0; i < made.Count; i++) if (made[i] != null) Object.Destroy(made[i]);
        made.Clear();
        if (mainCam != null) mainCam.enabled = true;
        mainCam = null;
        if (savedFps > 0) Application.targetFrameRate = savedFps;
        savedFps = -1; dragging = false;
    }

    // the livery changed (Setup): the suit colours
    public void Restyle(Livery liv) { if (root != null && rig != null) Dress(liv); }

    void OnPre(Camera c) { if (c != cam || c == null) return; fogWas = RenderSettings.fog; fogOff = true; RenderSettings.fog = false; }
    void OnPost(Camera c) { if (c != cam || c == null || !fogOff) return; RenderSettings.fog = fogWas; fogOff = false; }

    Transform Child(string name, Vector3 at, float yaw)
    {
        var t = new GameObject(name).transform; t.SetParent(root.transform, false);
        t.localPosition = at; t.localRotation = Quaternion.Euler(0f, yaw, 0f);
        return t;
    }

    static void SetLayer(Transform t)
    {
        var all = t.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++) all[i].gameObject.layer = Layer;
    }

    void BuildLighting()
    {
        // key: warm, high on the camera's left
        var kg = new GameObject("Key"); kg.transform.SetParent(root.transform, false);
        kg.transform.rotation = Quaternion.LookRotation(new Vector3(0.5f, -0.62f, 0.6f));
        var key = kg.AddComponent<Light>();
        key.type = LightType.Directional; key.color = new Color(1f, 0.95f, 0.88f); key.intensity = 1.15f;
        key.shadows = LightShadows.None; key.cullingMask = 1 << Layer; key.renderMode = LightRenderMode.ForcePixel;
        // fill (cool, camera right), rim (from behind, above), floor bounce and a dark garage ambient
        var s = new SphericalHarmonicsL2();
        s.Clear();
        s.AddAmbientLight(new Color(0.030f, 0.034f, 0.042f));
        s.AddDirectionalLight(new Vector3(0.85f, 0.25f, -0.45f).normalized, new Color(0.62f, 0.72f, 0.95f), 0.42f);
        s.AddDirectionalLight(new Vector3(-0.35f, 0.55f, 0.75f).normalized, new Color(1f, 0.97f, 0.92f), 0.85f);
        s.AddDirectionalLight(Vector3.down, new Color(0.5f, 0.5f, 0.55f), 0.06f);
        sh[0] = s;
    }

    void BuildCamera()
    {
        var cg = new GameObject("StudioCamera"); cg.transform.SetParent(root.transform, false);
        cam = cg.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.018f, 0.02f, 0.026f);
        cam.cullingMask = 1 << Layer; cam.depth = 10; cam.nearClipPlane = 0.1f; cam.farClipPlane = 60f; cam.fieldOfView = 30f;
        cam.allowHDR = false; cam.allowMSAA = true; cam.useOcclusionCulling = false; cam.allowDynamicResolution = false;
        cg.transform.position = Origin + new Vector3(0f, 1.08f, -5.5f);
    }

    // ------------------------------------------------------------------ the set: one mesh, vertex colours (Baked.shader: light 0.5 = full)
    void BuildSet()
    {
        var v = new List<Vector3>(1400); var c = new List<Color>(1400); var t = new List<int>(9000);
        // floor: a disc round the pool, a soft lit pool under the driver
        float[] rings = { 0f, 0.7f, 1.4f, 2.1f, 2.8f, 3.6f, 4.5f, 5.6f, 7f, 9f, 12f, 16f };
        const int seg = 56;
        for (int r = 0; r < rings.Length; r++)
            for (int s = 0; s < seg; s++)
            {
                float a = s * Mathf.PI * 2f / seg;
                var p = PoolAt + new Vector3(Mathf.Cos(a) * rings[r], 0f, Mathf.Sin(a) * rings[r]);
                v.Add(p); c.Add(FloorCol(p));
            }
        for (int r = 0; r < rings.Length - 1; r++)
            for (int s = 0; s < seg; s++)
            {
                int s1 = (s + 1) % seg;
                Quad(t, r * seg + s, r * seg + s1, (r + 1) * seg + s1, (r + 1) * seg + s);
            }
        // curved back wall (cyclorama), behind the stage as the camera sees it
        float[] hs = { 0f, 0.3f, 1.2f, 2.6f, 4.6f, 8f };
        float[] ks = { 0.012f, 0.009f, 0.007f, 0.0055f, 0.004f, 0.0025f };
        const int cols = 44; const float R = 13f, a0 = -100f, a1 = 100f;
        int wb = v.Count;
        for (int h = 0; h < hs.Length; h++)
            for (int i = 0; i <= cols; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)cols) * Mathf.Deg2Rad;
                v.Add(PoolAt + new Vector3(Mathf.Sin(a) * R, hs[h], Mathf.Cos(a) * R));
                float k = ks[h]; c.Add(new Color(k, k * 1.03f, k * 1.12f, 1f));
            }
        for (int h = 0; h < hs.Length - 1; h++)
            for (int i = 0; i < cols; i++)
            {
                int q = wb + h * (cols + 1) + i;
                Quad(t, q, q + 1, q + cols + 2, q + cols + 1);
            }
        // the red line at the wall base and a white strip high across the middle
        Arc(v, c, t, R - 0.05f, a0, a1, cols, 0.05f, 0.11f, new Color(0.42f, 0.01f, 0f, 1f));
        Arc(v, c, t, R - 0.05f, -40f, 40f, 16, 4.45f, 4.55f, new Color(0.40f, 0.42f, 0.46f, 1f));
        // vertical LED bars
        float[] bars = { -64f, -36f, 36f, 64f };
        for (int b = 0; b < bars.Length; b++)
        {
            float a = bars[b] * Mathf.Deg2Rad;
            var n = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)); var tg = new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a)) * 0.06f;
            var p = PoolAt + n * (R - 0.1f);
            AddQuad(v, c, t, p - tg + Vector3.up * 0.45f, p + tg + Vector3.up * 0.45f, p + tg + Vector3.up * 4.0f, p - tg + Vector3.up * 4.0f, new Color(0.46f, 0.48f, 0.52f, 1f));
        }
        // overhead softbox
        var sb = PoolAt + Vector3.up * 5f;
        AddQuad(v, c, t, sb + new Vector3(-2.5f, 0, -1.3f), sb + new Vector3(2.5f, 0, -1.3f), sb + new Vector3(2.5f, 0, 1.3f), sb + new Vector3(-2.5f, 0, 1.3f), new Color(0.6f, 0.6f, 0.62f, 1f));

        var mesh = new Mesh { name = "MenuStudioSet" };
        mesh.SetVertices(v); mesh.SetColors(c); mesh.SetTriangles(t, 0); mesh.RecalculateBounds();
        made.Add(mesh);
        var shd = Resources.Load<Shader>("Baked"); if (shd == null || !shd.isSupported) shd = Shader.Find("Sprites/Default");
        var mat = new Material(shd) { name = "MenuStudioSet", mainTexture = Texture2D.whiteTexture, color = Color.white };
        made.Add(mat);
        var go = new GameObject("Set"); go.transform.SetParent(root.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off; mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    static Color FloorCol(Vector3 p)
    {
        float d1 = (p - DriverAt).magnitude / 2.2f;
        float b = 0.0035f + 0.022f * Mathf.Exp(-d1 * d1);
        return new Color(b, b * 1.03f, b * 1.12f, 1f);
    }
    // a quad seen from both sides (the set is looked at from inside and from the probe)
    static void Quad(List<int> t, int a, int b, int c, int d)
    {
        t.Add(a); t.Add(b); t.Add(c); t.Add(a); t.Add(c); t.Add(d);
        t.Add(a); t.Add(c); t.Add(b); t.Add(a); t.Add(d); t.Add(c);
    }
    static void AddQuad(List<Vector3> v, List<Color> c, List<int> t, Vector3 a, Vector3 b, Vector3 d2, Vector3 d, Color col)
    {
        int o = v.Count; v.Add(a); v.Add(b); v.Add(d2); v.Add(d);
        for (int i = 0; i < 4; i++) c.Add(col);
        Quad(t, o, o + 1, o + 2, o + 3);
    }
    static void Arc(List<Vector3> v, List<Color> c, List<int> t, float r, float a0, float a1, int n, float y0, float y1, Color col)
    {
        int o = v.Count;
        for (int i = 0; i <= n; i++)
        {
            float a = Mathf.Lerp(a0, a1, i / (float)n) * Mathf.Deg2Rad;
            var p = PoolAt + new Vector3(Mathf.Sin(a) * r, 0f, Mathf.Cos(a) * r);
            v.Add(p + Vector3.up * y0); v.Add(p + Vector3.up * y1); c.Add(col); c.Add(col);
        }
        for (int i = 0; i < n; i++) { int q = o + i * 2; Quad(t, q, q + 2, q + 3, q + 1); }
    }

    // ------------------------------------------------------------------ driver
    void BuildDriver(Livery liv)
    {
        rig = new People.Rig(driverPivot, 0, People.GHelmet, "MenuDriver");
        rig.tr.localPosition = Vector3.zero; rig.tr.localRotation = Quaternion.Euler(0f, 180f, 0f); rig.tr.localScale = Vector3.one;
        rigSmr = rig.go.GetComponent<SkinnedMeshRenderer>();
        if (rigSmr != null) rigSmr.lightProbeUsage = LightProbeUsage.CustomProvided;
        calfR = -1;
        for (int j = 0; j < People.NJ; j++) if (People.Parent[j] == People.JThighR) { calfR = j; break; }
        rigBlock = new MaterialPropertyBlock();
        Dress(liv);
        People.Sample(People.Idle, 0f, buf); rig.Apply(buf);
        blob = new People.Blobs(root.transform, 1, "MenuDriverShadow");
    }

    static float Lum(Color c) => 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;
    void Pal(int reg, Color c, float gloss) { var l = c.linear; pal[reg] = new Vector4(l.r, l.g, l.b, gloss); }

    // a race suit in the livery colours, gloves and boots dark, the player's yellow helmet with a top in the team colour
    void Dress(Livery l)
    {
        if (rigSmr == null) return;
        Color prim = l.primary, sec = l.secondary, dark = new Color(0.05f, 0.05f, 0.06f);
        Color trim = Mathf.Abs(Lum(sec) - Lum(prim)) < 0.12f ? (Lum(prim) > 0.5f ? dark : new Color(0.9f, 0.9f, 0.91f)) : sec;
        for (int i = 0; i < People.NReg; i++) Pal(i, prim, 0.15f);
        Pal(People.RSide, trim, 0.15f); Pal(People.RYoke, trim, 0.15f); Pal(People.RStripe, trim, 0.15f);
        Pal(People.RGlove, Color.Lerp(trim, dark, 0.35f), 0.2f); Pal(People.RShin, prim * 0.85f, 0.15f); Pal(People.RKnee, prim, 0.15f);
        Pal(People.RBoot, dark, 0.35f); Pal(People.RBelt, dark, 0.3f); Pal(People.RNeck, new Color(0.08f, 0.08f, 0.09f), 0.1f);
        Pal(People.RFace, new Color(0.86f, 0.68f, 0.56f), 0.18f); Pal(People.RHair, new Color(0.20f, 0.13f, 0.08f), 0.1f);
        Pal(People.RHelmet, HelmetCol, 0.8f); Pal(People.RHelmetTop, prim, 0.8f); Pal(People.RVisor, new Color(0.02f, 0.025f, 0.04f), 1f);
        rigBlock.Clear();
        rigBlock.SetVectorArray("_Pal", pal);
        rigBlock.CopySHCoefficientArraysFrom(sh);
        rigSmr.SetPropertyBlock(rigBlock);
    }

    // ------------------------------------------------------------------ per frame (menu pages only)
    // the turntable: drag sideways in area to turn the driver, with a little fling
    public void Spin(Rect area)
    {
        if (root == null) return;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        var e = Event.current;
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (e.button == 0 && area.Contains(e.mousePosition))
                {
                    GUIUtility.hotControl = id; dragging = true; spinVel = 0f;
                    dragLastX = e.mousePosition.x; dragLastT = Time.realtimeSinceStartup; e.Use();
                }
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == id)
                {
                    float deg = -(e.mousePosition.x - dragLastX) / Mathf.Max(1f, Screen.height) * 220f;
                    driverYaw += deg;
                    float now = Time.realtimeSinceStartup, ddt = Mathf.Max(1f / 120f, now - dragLastT);
                    spinVel = Mathf.Lerp(spinVel, deg / ddt, 0.5f);
                    dragLastX = e.mousePosition.x; dragLastT = now; e.Use();
                }
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; dragging = false; spinVel = Mathf.Clamp(spinVel, -360f, 360f); e.Use(); }
                break;
        }
    }

    // once a frame (Repaint) while a menu page is up. gestures: the Home page (the driver's random gestures)
    public void Tick(int shot, bool gestures)
    {
        if (root == null) return;
        if (Application.targetFrameRate != MenuFps) { savedFps = Application.targetFrameRate; Application.targetFrameRate = MenuFps; }
        bool show = shot != ShotHidden;
        int mask = show ? 1 << Layer : 0;
        if (cam.cullingMask != mask) cam.cullingMask = mask; // hidden: the camera only clears (the page covers it)
        if (!show) return;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f), t = Time.unscaledTime;
        if (!dragging && Mathf.Abs(spinVel) > 0.05f)
        {
            driverYaw += spinVel * dt;
            spinVel *= Mathf.Exp(-2.4f * dt);
        }
        driverPivot.localRotation = Quaternion.Euler(0f, driverYaw, 0f);
        if (rig != null) Animate(t, dt, gestures);
        Frame(shot, dt);
    }

    static float Env(float t, float len) => Mathf.SmoothStep(0f, 1f, t / 0.5f) * (1f - Mathf.SmoothStep(0f, 1f, (t - (len - 0.6f)) / 0.6f));
    static void Turn(Transform b, float deg, Vector3 axis) { b.rotation = Quaternion.AngleAxis(deg, axis) * b.rotation; }

    // calm gestures only (no shoulder rolls or anything that reads as squaring up): adjust the gloves, a hand to the
    // visor, a glance aside, arms folded, a nod
    const int GGloves = 0, GVisor = 1, GGlance = 2, GFold = 3, GNod = 4, GCount = 5;

    void Animate(float t, float dt, bool gestures)
    {
        // a new gesture every 6-12 s (Home only); one that has started always plays to its end
        if (gKind < 0 && gestures && t >= nextG)
        {
            int k = Random.Range(0, GCount); if (k == gLast) k = (k + 1) % GCount;
            gKind = gLast = k; gT = 0f; gLen = k == GGlance ? 2.6f : k == GFold ? 4f : 3.2f;
        }
        float gw = 0f;
        if (gKind >= 0)
        {
            gT += dt; gw = Env(gT, gLen);
            if (gT >= gLen) { gKind = -1; gw = 0f; nextG = t + Random.Range(6f, 12f); }
        }
        if (!gestures && gKind < 0) nextG = Mathf.Max(nextG, t + 3f);

        People.Sample(People.Idle, t, buf);
        if (gKind == GFold) { People.Sample(People.FoldArms, gT + 0.4f, buf2); People.Blend(buf, buf2, gw, buf); }
        else if (gKind == GNod) { People.Sample(People.Yes, gT, buf2); People.Blend(buf, buf2, gw * 0.7f, buf); }
        rig.Apply(buf);

        var tr = rig.tr; Vector3 right = tr.right, fwd = tr.forward, up = tr.up;
        var b = rig.b;
        // relaxed posture over the clip: chest a touch forward (no puffed chest), shoulders dropped, feet a little
        // closer together (a normal stance, not a wide one)
        Turn(b[People.JSpine3], 2.5f, right);
        Turn(b[People.JClavL], 3f, fwd); Turn(b[People.JClavR], -3f, fwd);
        Turn(b[People.JThighL], 3.5f, fwd); Turn(b[People.JCalfL], -3.5f, fwd);
        Turn(b[People.JThighR], -3.5f, fwd); if (calfR >= 0) Turn(b[calfR], 3.5f, fwd);
        // breathing (~4.2 s): small
        float br = Mathf.Sin(t * 1.5f);
        Turn(b[People.JSpine3], -0.5f * br, right);
        Turn(b[People.JClavL], -0.5f * br, fwd); Turn(b[People.JClavR], 0.5f * br, fwd);
        // weight from one foot to the other (slow, uneven)
        float ws = (Mathf.Sin(t * 0.7f) + 0.35f * Mathf.Sin(t * 1.9f + 1.3f)) * 0.75f;
        b[People.JPelvis].position += right * (0.010f * ws);
        Turn(b[People.JPelvis], 1.3f * ws, fwd); Turn(b[People.JSpine1], -1.1f * ws, fwd);
        // arms hang naturally at his sides (two-bone IK; the clip's arms are held wide), except while folded
        float hang = gKind == GFold ? 1f - gw : 1f;
        Hang(true, hang, t, right, fwd, up);
        Hang(false, hang, t, right, fwd, up);
        if (gKind == GGloves && gw > 0.001f)
        {
            var lt = rig.Pelvis + fwd * 0.28f + up * 0.12f - right * 0.04f;
            rig.Arm(true, lt, gw, -fwd - right * 0.3f);
            var rt = rig.Palm(true) + right * 0.05f + up * 0.02f + fwd * (0.015f * Mathf.Sin(gT * 8f));
            rig.Arm(false, rt, gw, -fwd + right * 0.3f);
        }
        else if (gKind == GVisor && gw > 0.001f)
        {
            var hp = b[People.JHead].position;
            rig.Arm(false, hp + fwd * 0.17f + right * 0.05f - up * 0.02f, gw, right - up * 0.6f);
        }
        // the head: a slow wander ahead, or a glance aside (to his left, as if at the pit wall)
        var head = b[People.JHead].position;
        var look = head + fwd * 3f + right * (Mathf.Sin(t * 0.23f) * 0.6f + Mathf.Sin(t * 0.61f) * 0.2f) + up * (Mathf.Sin(t * 0.17f) * 0.18f - 0.08f);
        rig.LookAt(look, 0.5f);
        if (gKind == GGlance) rig.LookAt(head - right * 3f + fwd * 1.2f - up * 0.1f, gw);
        // soft contact shadow under him (local to the studio root)
        if (blob != null)
        {
            var pel = rig.Pelvis - Origin; pel.y = 0f;
            blob.Begin(); blob.Set(0, pel, fwd, 0.45f, 0.8f, 0.75f); blob.Flush(); blob.Show(true);
        }
    }

    // one arm hanging relaxed: the palm a little below the hip, just outside the thigh, elbow slightly bent and back
    void Hang(bool left, float w, float t, Vector3 right, Vector3 fwd, Vector3 up)
    {
        if (w <= 0.001f) return;
        var b = rig.b;
        Transform ua = b[left ? People.JUpL : People.JUpR], la = b[left ? People.JLoL : People.JLoR], hd = b[left ? People.JHandL : People.JHandR];
        float l1 = (la.position - ua.position).magnitude, l2 = (hd.position - la.position).magnitude;
        float side = left ? -1f : 1f, sway = Mathf.Sin(t * 0.9f + (left ? 0f : 1.7f)) * 0.01f;
        var palm = ua.position - up * ((l1 + l2) * 0.92f + 0.06f) + right * (side * 0.05f) + fwd * (0.04f + sway);
        rig.Arm(left, palm, w, -fwd + right * (side * 0.35f));
    }

    // the camera for the page: Home = the driver in the middle, Driver = the driver on the left
    void Frame(int shot, float dt)
    {
        Vector3 subject, pos; float fov, fx;
        if (shot == ShotDriver) { subject = new Vector3(0f, 0.9f, 0f); pos = new Vector3(0.35f, 1.05f, -5.0f); fov = 30f; fx = 0.25f; }
        else { subject = new Vector3(0f, 0.86f, 0f); pos = new Vector3(0f, 1.08f, -5.5f); fov = 30f; fx = 0.5f; }
        subject += Origin; pos += Origin;
        float aspect = Screen.height > 0 ? Screen.width / (float)Screen.height : 1.78f;
        var dir = subject - pos; float dist = dir.magnitude;
        var side = Vector3.Cross(Vector3.up, dir).normalized;
        float tanH = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * aspect;
        var look = subject + side * ((0.5f - fx) * 2f * tanH * dist); // the subject at fx across the screen
        if (!camSet) { camPos = pos; camLook = look; camFov = fov; camSet = true; }
        else
        {
            float k = 1f - Mathf.Exp(-4.5f * dt);
            camPos = Vector3.Lerp(camPos, pos, k); camLook = Vector3.Lerp(camLook, look, k); camFov = Mathf.Lerp(camFov, fov, k);
        }
        cam.transform.position = camPos; cam.transform.LookAt(camLook); cam.fieldOfView = camFov;
    }
}
