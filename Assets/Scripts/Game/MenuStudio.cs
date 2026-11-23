// MenuStudio.cs — the 3D behind the front end (MenuUI): the player's driver standing alone on seamless paper, in soft
// daylight, so he sits on the front end's "Paper" page (UiKit.cs). Only the driver: no car, no set (cheap).
//  * Where: far below the circuit (Origin), on its own layer (Layer 30, unused by the game). Only the studio camera
//    draws that layer, and while the studio exists the game's main camera is switched off (the circuit is not
//    rendered at all behind the menu). Every light that exists is told to skip the layer, so the track's sun does not
//    reach the stage.
//  * Backdrop: the studio camera clears to the page colour (UiKit.Paper, #F3F1EC), so the 2D paper of the page and the
//    3D background are the same colour with no seam. There is no floor or wall geometry: the only thing under the
//    driver is a soft contact shadow (People.Blobs, multiplied onto the paper: about the mockup's 18% ellipse).
//  * Driver: People.DriverBody (People2: the pack driver driver_01, CC0: seamed suit, gloves, racing boots, closed
//    helmet; the full 19k-tri hero LOD) or,
//    without that pack, one of the rigged CC0 people (Quaternius, crew helmet with a dark visor), in the livery colours,
//    standing relaxed: idle clip (the mocap idle when the private Mixamo pack is there) with the arms hanging naturally
//    (IK), chest and shoulders relaxed, a normal stance; small breathing, weight shift and head movement, and on Home
//    every 6-12 s a calm gesture (adjust the gloves, a hand to the visor, a glance aside, fold the arms, nod; with mocap
//    also a wave to the camera and a talking gesture).
//  * Light: soft daylight. A key light (real directional light, studio layer only, neutral-warm, from high front left)
//    and custom spherical harmonics given to the driver (People.shader reads ShadeSH9): a bright sky from above, a
//    soft fill from the camera's right, a warm bounce off the paper from below and a gentle rim. No reflection probe,
//    no render textures. Fog is switched off only while the studio camera renders.
//  * Cost rules: everything is created in Create() when a menu page needs it and destroyed in Release() (MenuUI calls
//    it in BeginLoading; GameManager.StartSession calls it again to be sure): GameObjects, the camera and light, the
//    driver rig and its shadow mesh; the main camera is switched back on and the frame rate the menu capped (MenuFps)
//    is given back. Nothing here has an Update: MenuUI calls Tick() once a frame while a menu page is up, never in a
//    race.
using UnityEngine;
using UnityEngine.Rendering;

public sealed class MenuStudio
{
    public const int Layer = 30;
    public const int MenuFps = 30;
    public const int ShotHome = 0, ShotDriver = 1, ShotHidden = 3;
    static readonly Vector3 Origin = new Vector3(0f, -1500f, 0f);
    // stage position (studio frame, metres): the driver at the centre facing the camera (-z)
    static readonly Vector3 DriverAt = Vector3.zero;
    static readonly Color HelmetCol = new Color(1f, 0.82f, 0.18f); // the player's helmet (as CarModel's driver)

    GameObject root; Camera cam, mainCam; Transform driverPivot;
    People.Rig rig; People.Blobs blob; SkinnedMeshRenderer rigSmr; MaterialPropertyBlock rigBlock;
    readonly Vector4[] pal = new Vector4[People.NReg];
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
        Object.Destroy(root); // camera, key light, driver rig
        root = null; cam = null; driverPivot = null;
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
        // key: soft daylight, neutral-warm, high on the camera's left
        var kg = new GameObject("Key"); kg.transform.SetParent(root.transform, false);
        kg.transform.rotation = Quaternion.LookRotation(new Vector3(0.45f, -0.70f, 0.55f));
        var key = kg.AddComponent<Light>();
        key.type = LightType.Directional; key.color = new Color(1f, 0.97f, 0.92f); key.intensity = 0.85f;
        key.shadows = LightShadows.None; key.cullingMask = 1 << Layer; key.renderMode = LightRenderMode.ForcePixel;
        // a bright open sky from above, a soft fill from the camera's right, the paper's warm bounce from below, a gentle
        // rim from behind: a daylight studio, no dark sides
        var s = new SphericalHarmonicsL2();
        s.Clear();
        s.AddAmbientLight(new Color(0.20f, 0.20f, 0.21f));
        s.AddDirectionalLight(Vector3.up, new Color(0.92f, 0.95f, 1f), 0.42f);
        s.AddDirectionalLight(new Vector3(0.85f, 0.25f, -0.45f).normalized, new Color(1f, 0.98f, 0.95f), 0.40f);
        s.AddDirectionalLight(Vector3.down, new Color(0.95f, 0.92f, 0.86f), 0.22f);
        s.AddDirectionalLight(new Vector3(-0.35f, 0.55f, 0.75f).normalized, new Color(1f, 0.98f, 0.95f), 0.35f);
        sh[0] = s;
    }

    void BuildCamera()
    {
        var cg = new GameObject("StudioCamera"); cg.transform.SetParent(root.transform, false);
        cam = cg.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = UiKit.Paper; // the page colour: no seam between 2D and 3D
        cam.cullingMask = 1 << Layer; cam.depth = 10; cam.nearClipPlane = 0.1f; cam.farClipPlane = 60f; cam.fieldOfView = 30f;
        cam.allowHDR = false; cam.allowMSAA = true; cam.useOcclusionCulling = false; cam.allowDynamicResolution = false;
        cg.transform.position = Origin + new Vector3(0f, 1.0f, -6.0f);
    }

    // ------------------------------------------------------------------ driver
    void BuildDriver(Livery liv)
    {
        rig = new People.Rig(driverPivot, People.DriverBody >= 0 ? People.DriverBody : 0, People.GHelmet, "MenuDriver");
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

    // a race suit in the livery colours, gloves and boots dark, the player's yellow helmet with a top in the team colour
    void Dress(Livery l)
    {
        if (rigSmr == null) return;
        People.DriverPalette(pal, l, HelmetCol); // (suit in the team colour, trim on the stripes / belt / cuffs, yellow helmet)
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
    // visor, a glance aside, arms folded, a nod; with mocap (People.Mocap) also a wave and a talking gesture
    const int GGloves = 0, GVisor = 1, GGlance = 2, GFold = 3, GNod = 4, GWave = 5, GTalk = 6, GCount = 5, GCountMocap = 7;

    void Animate(float t, float dt, bool gestures)
    {
        // a new gesture every 6-12 s (Home only); one that has started always plays to its end
        if (gKind < 0 && gestures && t >= nextG)
        {
            int n = People.Mocap ? GCountMocap : GCount;
            int k = Random.Range(0, n); if (k == gLast) k = (k + 1) % n;
            gKind = gLast = k; gT = 0f; gLen = k == GGlance ? 2.6f : k == GFold ? 4f : k == GWave ? Mathf.Min(3.2f, People.Length(People.MWave)) : k == GTalk ? 3.8f : 3.2f;
        }
        float gw = 0f;
        if (gKind >= 0)
        {
            gT += dt; gw = Env(gT, gLen);
            if (gT >= gLen) { gKind = -1; gw = 0f; nextG = t + Random.Range(6f, 12f); }
        }
        if (!gestures && gKind < 0) nextG = Mathf.Max(nextG, t + 3f);

        bool mocap = People.Mocap; float pw = mocap ? 0.3f : 1f; // (the mocap idle stands, breathes and shifts by itself)
        People.Sample(People.MIdle, t, buf);
        if (gKind == GFold) { People.Sample(People.FoldArms, gT + 0.4f, buf2); People.Blend(buf, buf2, gw, buf); }
        else if (gKind == GNod) { People.Sample(People.Yes, gT, buf2); People.Blend(buf, buf2, gw * 0.7f, buf); }
        else if (gKind == GWave) { People.Sample(People.MWave, gT, buf2); People.Blend(buf, buf2, gw, buf); }
        else if (gKind == GTalk) { People.Sample(People.MTalk, gT + 0.5f, buf2); People.Blend(buf, buf2, gw * 0.85f, buf); }
        rig.Apply(buf);

        var tr = rig.tr; Vector3 right = tr.right, fwd = tr.forward, up = tr.up;
        var b = rig.b;
        // relaxed posture over the clip: chest a touch forward (no puffed chest), shoulders dropped, feet a little
        // closer together (a normal stance, not a wide one)
        Turn(b[People.JSpine3], 2.5f * pw, right);
        Turn(b[People.JClavL], 3f * pw, fwd); Turn(b[People.JClavR], -3f * pw, fwd);
        Turn(b[People.JThighL], 3.5f * pw, fwd); Turn(b[People.JCalfL], -3.5f * pw, fwd);
        Turn(b[People.JThighR], -3.5f * pw, fwd); if (calfR >= 0) Turn(b[calfR], 3.5f * pw, fwd);
        // breathing (~4.2 s): small
        float br = Mathf.Sin(t * 1.5f) * pw;
        Turn(b[People.JSpine3], -0.5f * br, right);
        Turn(b[People.JClavL], -0.5f * br, fwd); Turn(b[People.JClavR], 0.5f * br, fwd);
        // weight from one foot to the other (slow, uneven)
        float ws = (Mathf.Sin(t * 0.7f) + 0.35f * Mathf.Sin(t * 1.9f + 1.3f)) * 0.75f * pw;
        b[People.JPelvis].position += right * (0.010f * ws);
        Turn(b[People.JPelvis], 1.3f * ws, fwd); Turn(b[People.JSpine1], -1.1f * ws, fwd);
        // arms hang naturally at his sides (two-bone IK; the clip's arms are held wide), except while folded
        float hang = (gKind == GFold || gKind == GWave || gKind == GTalk ? 1f - gw : 1f) * (mocap ? 0.35f : 1f);
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
        // soft contact shadow under him on the paper (local to the studio root; the ellipse stays level to the camera)
        if (blob != null)
        {
            var pel = rig.Pelvis - Origin; pel.y = 0f;
            blob.Begin(); blob.Set(0, pel, Vector3.forward, 0.30f, 2.2f, 0.62f); blob.Flush(); blob.Show(true); // a wide, soft ellipse (~18% darker at its centre)
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

    // the camera for the page: the driver standing in the middle of the page, his feet just above the bottom rule and
    // his helmet under the top rule (Home and Driver frame him the same way, so going between them does not move him)
    void Frame(int shot, float dt)
    {
        Vector3 subject, pos; float fov, fx;
        if (shot == ShotDriver) { subject = new Vector3(0f, 0.80f, 0f); pos = new Vector3(0.12f, 1.0f, -6.0f); fov = 30f; fx = 0.5f; }
        else { subject = new Vector3(0f, 0.80f, 0f); pos = new Vector3(0f, 1.0f, -6.0f); fov = 30f; fx = 0.5f; }
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
