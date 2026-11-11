// GameManager.cs — runs the game: 240 Hz physics, touch/tilt/keyboard input, cameras, HUD (IMGUI, no packages),
// laps and auto-recover. Created automatically at startup (see Boot), so the scene can stay empty.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (FindFirstObjectByType<GameManager>() == null) new GameObject("Game").AddComponent<GameManager>(); }

    const double H = 1.0 / 240;
    Track track;
    FormulaCar car;
    CarModel model;
    Camera cam;
    double acc, simTime;
    int surface, idx = -1, prevIdx = -1;
    double frontSpin, px, py, ph, pd;

    // settings (saved)
    TcMode tc; bool tilt, playground, invertTilt, sound = true; int camMode;
    // every circuit in Circuits.All, then the free-driving playground
    static readonly string[] MapNames = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(System.Linq.Enumerable.Select(Circuits.All, c => c.name), new[] { "Playground" }));
    static int PlaygroundIdx => Circuits.All.Length;
    int mapIdx; GameObject world;
    // aero balance (share of downforce on the front axle): the main real-world setup knob for under/oversteer
    static readonly float[] Balances = { 0.45f, 0.48f, 0.50f, 0.53f }; int balIdx;
    EngineAudio audioEngine;
    static readonly string[] CamNames = { "Chase", "Far chase", "Cockpit", "Halo cam" };

    // laps / recover / test bench
    double? lapStart, lastLap, bestLap; bool cut; int lapNo = 1;
    double offTime, wrongTime;
    double? t100; double launchT = -1, topSpeed;
    string flash; float flashT;

    void Start()
    {
        Application.targetFrameRate = 60;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Screen.orientation = ScreenOrientation.AutoRotation;
        Screen.autorotateToPortrait = Screen.autorotateToPortraitUpsideDown = false;
        Screen.autorotateToLandscapeLeft = Screen.autorotateToLandscapeRight = true;
        QualitySettings.vSyncCount = 0; QualitySettings.shadowProjection = ShadowProjection.StableFit;

        tc = (TcMode)PlayerPrefs.GetInt("tc", (int)TcMode.Low);
        tilt = PlayerPrefs.GetInt("tilt", 0) == 1; invertTilt = PlayerPrefs.GetInt("tiltInv", 0) == 1;
        mapIdx = Mathf.Clamp(PlayerPrefs.GetInt("map", 0), 0, MapNames.Length - 1); playground = mapIdx == PlaygroundIdx; camMode = PlayerPrefs.GetInt("cam", 0); sound = PlayerPrefs.GetInt("snd", 1) == 1; balIdx = Mathf.Clamp(PlayerPrefs.GetInt("bal", 0), 0, Balances.Length - 1); showLine = PlayerPrefs.GetInt("line", 0) == 1; absOn = PlayerPrefs.GetInt("abs", 1) == 1; classIdx = Mathf.Clamp(PlayerPrefs.GetInt("class", 0), 0, CarConfig.ClassNames.Length - 1); compIdx = Mathf.Clamp(PlayerPrefs.GetInt("tyres", 0), 0, CompoundTable.Defs.Length - 1); gfxIdx = Mathf.Clamp(PlayerPrefs.GetInt("gfx", 0), 0, GfxNames.Length - 1);
        dzIdx = Mathf.Clamp(PlayerPrefs.GetInt("tiltDz", 1), 0, DeadZones.Length - 1); rangeIdx = Mathf.Clamp(PlayerPrefs.GetInt("tiltRange", 2), 0, TiltRanges.Length - 1);
        smoothIdx = Mathf.Clamp(PlayerPrefs.GetInt("tiltSmooth", 1), 0, SmoothCut.Length - 1); speedSteerIdx = Mathf.Clamp(PlayerPrefs.GetInt("speedSteer", 2), 0, SpeedSteer.Length - 1);
        ApplyScreenLock();
        rivalIdx = Mathf.Clamp(PlayerPrefs.GetInt("rivals", 1), 0, RivalCounts.Length - 1); diffIdx = Mathf.Clamp(PlayerPrefs.GetInt("diff", 0), 0, DiffNames.Length - 1);

        LoadMap();
        car = new FormulaCar(PlayerConfig());
        model = CarModel.CreateFromAsset() ?? CarModel.Create(car.cfg.wheelbase, car.cfg.frontWeight, car.cfg.wheelRadius);
        var camGo = new GameObject("Camera"); cam = camGo.AddComponent<Camera>(); camGo.tag = "MainCamera"; cam.allowDynamicResolution = true;
        cam.nearClipPlane = 0.2f; cam.farClipPlane = 2500; cam.fieldOfView = 62;
        cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor; cam.backgroundColor = RenderSettings.fogColor;
        camGo.AddComponent<AudioListener>();
        audioEngine = camGo.AddComponent<EngineAudio>(); audioEngine.muted = !sound;
        ApplyGraphics(gfxIdx == 0 ? 2 : gfxIdx);
        ResetCar();
    }

    int compIdx; // tyre compound
    int builtFor = -1; double wallAt = 1e9; float hitT; bool menuOpen, absOn = true;
    void LoadMap()
    {
        playground = mapIdx == PlaygroundIdx;
        int circuit = playground ? (builtFor >= 0 ? builtFor : 0) : mapIdx;
        if (circuit != builtFor || world == null)
        {
            if (world != null) Destroy(world);
            track = Circuits.All[circuit].Build();
            rubber = 0; // a fresh, "green" track
            world = WorldBuilder.Build(track);
            wallAt = Traffic.WallOffset(track);
            BuildLine();
            BuildMiniMap();
            if (skids != null) skids.Clear();
            builtFor = circuit;
            bestLap = lastLap = null; lapNo = 1;
        }
        if (lineGo != null) lineGo.SetActive(showLine && !playground);
    }

    // mini-map: the circuit outline drawn once into a small texture; the car is a dot on top
    Texture2D miniTex; float mmMinX, mmMinY, mmScale; const int MM = 128;
    void BuildMiniMap()
    {
        if (miniTex == null) { miniTex = new Texture2D(MM, MM, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var q in track.pts) { minX = System.Math.Min(minX, q.x); maxX = System.Math.Max(maxX, q.x); minY = System.Math.Min(minY, q.y); maxY = System.Math.Max(maxY, q.y); }
        float span = (float)System.Math.Max(maxX - minX, maxY - minY);
        mmScale = (MM - 12) / span;
        mmMinX = (float)(minX + maxX) / 2 - span / 2; mmMinY = (float)(minY + maxY) / 2 - span / 2;
        var mmPx = new Color32[MM * MM];
        foreach (var q in track.pts)
        {
            int cx = 6 + (int)(((float)q.x - mmMinX) * mmScale), cy = 6 + (int)(((float)q.y - mmMinY) * mmScale);
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int ix = cx + dx, iy = cy + dy;
                if (ix >= 0 && iy >= 0 && ix < MM && iy < MM) mmPx[iy * MM + ix] = new Color32(255, 255, 255, 220);
            }
        }
        miniTex.SetPixels32(mmPx); miniTex.Apply(false);
    }
    void DrawMiniMap()
    {
        if (miniTex == null || playground) return;
        float size = S(110), x = Screen.width - size - S(8), y = S(46);
        Fill(new Rect(x - S(4), y - S(4), size + S(8), size + S(8)), new Color(0, 0, 0, 0.3f));
        // texture rows go bottom-up, GUI goes top-down: flip vertically (physics +y = north = up on screen)
        GUI.DrawTextureWithTexCoords(new Rect(x, y, size, size), miniTex, new Rect(0, 0, 1, 1));
        float k = size / MM;
        float cx = x + (6 + ((float)car.x - mmMinX) * mmScale) * k, cy = y + size - (6 + ((float)car.y - mmMinY) * mmScale) * k;
        var p0 = track.pts[0];
        float sx = x + (6 + ((float)p0.x - mmMinX) * mmScale) * k, sy = y + size - (6 + ((float)p0.y - mmMinY) * mmScale) * k;
        Fill(new Rect(sx - S(3), sy - S(3), S(6), S(6)), new Color(1f, 0.82f, 0.25f));
        foreach (var d in rivals)
        {
            float rx = x + (6 + ((float)d.car.x - mmMinX) * mmScale) * k, ry = y + size - (6 + ((float)d.car.y - mmMinY) * mmScale) * k;
            Fill(new Rect(rx - S(2.5f), ry - S(2.5f), S(5), S(5)), new Color(d.who.r, d.who.g, d.who.b));
        }
        Fill(new Rect(cx - S(4), cy - S(4), S(8), S(8)), new Color(1f, 0.25f, 0.2f));
    }

    // Graphics level (a setting). Auto starts at Medium and steps down if the phone cannot hold about 50 fps.
    static readonly string[] GfxNames = { "Auto", "Low", "Medium", "High" }; int gfxIdx; int gfxLevel = 2;
    float fpsAvg = 60, slowT;
    void ApplyGraphics(int level)
    {
        gfxLevel = level;
        QualitySettings.antiAliasing = level >= 3 ? 4 : level == 2 ? 2 : 0;
        QualitySettings.shadows = level <= 1 || LightBake.Active ? ShadowQuality.Disable : ShadowQuality.All; // baked lighting needs no shadow maps
        QualitySettings.shadowCascades = level >= 3 ? 2 : 1;
        QualitySettings.shadowResolution = level >= 3 ? ShadowResolution.High : ShadowResolution.Medium;
        QualitySettings.shadowDistance = level >= 3 ? 90 : 55;
        QualitySettings.anisotropicFiltering = level <= 1 ? AnisotropicFiltering.Disable : AnisotropicFiltering.ForceEnable;
        if (cam != null) cam.farClipPlane = level <= 1 ? 1500 : 2500;
    }
    // Dynamic resolution: when the phone can't hold ~50 fps the 3D picture is drawn at a lower resolution
    // (down to 60%) and scaled up; the HUD is drawn afterwards at full resolution, so text stays sharp.
    // Works where the graphics API supports it (Vulkan, Metal); elsewhere it does nothing.
    float renderScale = 1f, scaleT;
    void DynamicResolution()
    {
        if (menuOpen || Time.realtimeSinceStartup < 6f) return;
        if (fpsAvg < 50f) scaleT -= Time.unscaledDeltaTime; else if (fpsAvg > 58f) scaleT += Time.unscaledDeltaTime; else scaleT = 0;
        float ns = renderScale;
        if (scaleT < -1.5f) { ns = Mathf.Max(0.6f, renderScale - 0.1f); scaleT = 0; }
        else if (scaleT > 6f) { ns = Mathf.Min(1f, renderScale + 0.05f); scaleT = 0; }
        if (!Mathf.Approximately(ns, renderScale)) { renderScale = ns; ScalableBufferManager.ResizeBuffers(ns, ns); }
    }
    void AutoQuality()
    {
        float fps = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.001f);
        fpsAvg += (fps - fpsAvg) * 0.05f;
        DynamicResolution();
        if (renderScale > 0.65f) { slowT = 0; return; } // first lower the resolution, then the effects
        if (gfxIdx != 0 || menuOpen || Time.realtimeSinceStartup < 6f || gfxLevel <= 1) return;
        slowT = fpsAvg < 50f ? slowT + Time.unscaledDeltaTime : 0;
        if (slowT < 4f) return;
        slowT = 0; ApplyGraphics(gfxLevel - 1); Flash("Graphics lowered to keep it smooth");
    }

    SkidMarks skids;
    void UpdateSkids()
    {
        if (skids == null) skids = new SkidMarks();
        float v = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        bool asphalt = surface == 0 && v > 2f;
        double beta = System.Math.Abs(System.Math.Atan2(car.vy, System.Math.Max(System.Math.Abs(car.vx), 1)));
        bool rear = asphalt && (car.spinR || car.lockR || (car.useR > 0.98 && beta > 0.07));
        bool front = asphalt && (car.lockF || (car.useF > 0.99 && beta > 0.05));
        var right = model.transform.right;
        for (int i = 0; i < 2; i++)
        {
            if (model.frontPivots[i] != null) skids.Track(i, model.frontPivots[i].position, right, front);
            if (model.rearSpin[i] != null) skids.Track(2 + i, model.rearSpin[i].parent.position, right, rear);
        }
        skids.Upload();
    }

    // racing line overlay (off by default; "Line" button)
    RacingLine line; GameObject lineGo; bool showLine;
    void BuildLine()
    {
        if (lineGo != null) Destroy(lineGo);
        var lc = CarConfig.ForClass(classIdx); lc.aeroFront = Balances[balIdx];
        line = RacingLine.Compute(track, lc);
        lineGo = RacingLineView.Build(track, line, world.transform);
        var rubber = Scenery.Rubber(track, line, world.transform);
        var boards = Scenery.Boards(track, line, world.transform, (float)Traffic.WallOffset(track));
        // these are built after the world, so lay them on the hills here
        WorldBuilder.Drape(lineGo); WorldBuilder.Drape(rubber); if (boards != null) { WorldBuilder.Drape(boards); LightBake.Apply(boards); }
        lineGo.SetActive(showLine && !playground);
    }

    void Save()
    {
        PlayerPrefs.SetInt("tc", (int)tc); PlayerPrefs.SetInt("tilt", tilt ? 1 : 0); PlayerPrefs.SetInt("tiltInv", invertTilt ? 1 : 0);
        PlayerPrefs.SetInt("map", mapIdx); PlayerPrefs.SetInt("cam", camMode); PlayerPrefs.SetInt("snd", sound ? 1 : 0); PlayerPrefs.SetInt("bal", balIdx); PlayerPrefs.SetInt("line", showLine ? 1 : 0); PlayerPrefs.SetInt("abs", absOn ? 1 : 0); PlayerPrefs.SetInt("class", classIdx); PlayerPrefs.SetInt("tyres", compIdx); PlayerPrefs.SetInt("gfx", gfxIdx); PlayerPrefs.SetInt("tiltDz", dzIdx); PlayerPrefs.SetInt("tiltRange", rangeIdx); PlayerPrefs.SetInt("tiltSmooth", smoothIdx); PlayerPrefs.SetInt("speedSteer", speedSteerIdx); PlayerPrefs.SetInt("rivals", rivalIdx); PlayerPrefs.SetInt("diff", diffIdx); PlayerPrefs.Save();
    }

    void ResetCar()
    {
        car = new FormulaCar(PlayerConfig());
        if (model != null) model.SetStripe(CarModel.StripeFor((Compound)compIdx));
        SetupRivals();
        if (playground) { car.x = WorldBuilder.PadStart.x - 15; car.y = WorldBuilder.PadStart.y; car.heading = 0; }
        else
        {
            // the player starts from the last grid slot
            int k = rivals.Count; double side = rivals.Count == 0 ? 0 : (k % 2 == 0 ? 1 : -1) * 1.8;
            var p = track.pts[track.N - (rivals.Count == 0 ? 30 : 15 + k * 4)];
            car.x = p.x + p.nx * side; car.y = p.y + p.ny * side; car.heading = System.Math.Atan2(p.ty, p.tx);
        }
        raceTime = 0; pLap = 0; aiTick = 0;
        idx = prevIdx = -1; lapStart = null; cut = false; offTime = wrongTime = 0; t100 = null; launchT = -1; topSpeed = 0;
        Flash(MapNames[mapIdx]);
    }

    // ---------------------------------------------------------------- rivals
    static readonly int[] RivalCounts = { 0, 5, 10, 15 }; int rivalIdx;
    static readonly string[] DiffNames = { "Easy", "Medium", "Hard", "Real" };
    static readonly double[] DiffScale = { 0.82, 0.89, 0.95, 1.0 }; int diffIdx;
    readonly List<AiDriver> rivals = new List<AiDriver>(); readonly List<CarModel> rivalModels = new List<CarModel>(); readonly List<float> rivalSpin = new List<float>();
    readonly List<CarModel> rivalFar = new List<CarModel>(); readonly List<bool> rivalNear = new List<bool>();
    const double Countdown = 3; double raceTime; int pLap, aiTick; double playerLat;

    void SetupRivals()
    {
        foreach (var m in rivalModels) if (m != null) Destroy(m.gameObject);
        foreach (var m in rivalFar) if (m != null) Destroy(m.gameObject);
        rivals.Clear(); rivalModels.Clear(); rivalSpin.Clear(); rivalFar.Clear(); rivalNear.Clear();
        int count = playground || line == null ? 0 : RivalCounts[rivalIdx];
        var grid = DriverProfile.Grid;
        for (int k = 0; k < count; k++)
        {
            // spread the picks over the whole field: stars, midfield and backmarkers
            var who = grid[count == 1 ? 0 : Mathf.RoundToInt(k * (grid.Length - 1) / (float)(count - 1))];
            var d = new AiDriver(track, line, CarConfig.ForClass(classIdx), who, AiTuning.For(builtFor), DiffScale[diffIdx], k + 1);
            d.Place(track.N - 15 - k * 4, (k % 2 == 0 ? 1 : -1) * 1.8);
            rivals.Add(d); rivalSpin.Add(0);
            var col = new Color(who.r, who.g, who.b);
            rivalModels.Add(CarModel.CreateFromAsset("Car/car_lod", col, true) ?? CarModel.Create(d.car.cfg.wheelbase, d.car.cfg.frontWeight, d.car.cfg.wheelRadius));
            var far = CarModel.CreateFromAsset("Car/car_far", col, true); if (far != null) far.gameObject.SetActive(false);
            rivalFar.Add(far); rivalNear.Add(true);
        }
    }

    // one physics step for every rival; they think 60 times a second
    void StepRivals()
    {
        if (raceTime >= Countdown && aiTick++ % 4 == 0)
        {
            UpdateTow();
            // rubber build-up from every car's distance
            double dist = System.Math.Abs(car.vx) * 4 * H; foreach (var r in rivals) dist += System.Math.Abs(r.car.vx) * 4 * H;
            rubber = System.Math.Min(1, rubber + dist / (track.length * 25));
            for (int i = 0; i < rivals.Count; i++)
            {
                rivals[i].Think(4 * H, simTime, rivals, car, idx, playerLat);
                var rc = rivals[i].car; rc.grade = WorldBuilder.Height != null ? WorldBuilder.Height.Grade(rc.x, rc.y, rc.heading) : 0;
                if (rivals[i].idx >= 0) Traffic.Wall(rivals[i].car, track.pts[rivals[i].idx], rivals[i].lateral, wallAt);
            }
            for (int i = 0; i < rivals.Count; i++)
            {
                for (int j = i + 1; j < rivals.Count; j++) Traffic.Collide(rivals[i].car, rivals[j].car);
                Traffic.Collide(rivals[i].car, car);
            }
        }
        double hw = track.width / 2;
        for (int i = 0; i < rivals.Count; i++)
        {
            var d = rivals[i];
            if (aiTick % 4 == 1) WheelContact.Update(track, line, d.car, d.idx, d.lateral, rubber);
            d.car.Step(raceTime >= Countdown ? d.LastInput : new CarInput(0, 0, 1), H);
        }
    }

    int Position()
    {
        double mine = pLap + (idx < 0 ? 0 : (double)idx / track.N); int pos = 1;
        foreach (var d in rivals) if (d.progress > mine) pos++;
        return pos;
    }

    void Recover()
    {
        var p = track.pts[(idx + 4) % track.N];
        double keep = System.Math.Max(System.Math.Min(System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy), 40), 22);
        var cfg = car.cfg; int gear = 1;
        while (gear < cfg.gearTopSpeeds.Length && keep * 3.6 > cfg.gearTopSpeeds[gear - 1] * 0.8) gear++;
        car = new FormulaCar(cfg) { x = p.x, y = p.y, heading = System.Math.Atan2(p.ty, p.tx), vx = keep, gear = gear };
        offTime = wrongTime = 0; cut = true; Flash("Back on track");
    }

    void Flash(string s) { flash = s; flashT = 1.4f; }

    // ---------------------------------------------------------------- input
    int steerFinger = -1; float steerAnchorX; float dragSteer;
    float pedalGas, pedalBrake;
    Rect GasRect => new Rect(Screen.width - S(98), Screen.height - S(196), S(84), S(180));
    Rect BrakeRect => tilt ? new Rect(S(14), Screen.height - S(196), S(100), S(180)) : new Rect(Screen.width - S(204), Screen.height - S(126), S(96), S(110));
    float S(float v) => v * Screen.height / 390f; // layout units match the web version (390 px tall landscape phone)

    int gasFinger = -1, brakeFinger = -1;
    CarInput ReadInput(float dt)
    {
        float gasT = 0, brkT = 0; bool steerHeld = false; bool gasSeen = false, brakeSeen = false;
        foreach (var t in Input.touches)
        {
            var p = new Vector2(t.position.x, Screen.height - t.position.y); // GUI space (y down)
            bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
            // a finger "owns" the pedal it first touched and keeps controlling it even if it slides off the box
            if (t.phase == TouchPhase.Began)
            {
                if (GasRect.Contains(p)) gasFinger = t.fingerId;
                else if (BrakeRect.Contains(p)) brakeFinger = t.fingerId;
            }
            if (t.fingerId == gasFinger) { if (!ended) { gasT = PedalAmount(GasRect, p); gasSeen = true; } continue; }
            if (t.fingerId == brakeFinger) { if (!ended) { brkT = PedalAmount(BrakeRect, p); brakeSeen = true; } continue; }
            if (!tilt && p.x < Screen.width * 0.42f && p.y > S(60))
            {
                if (t.phase == TouchPhase.Began || steerFinger < 0) { steerFinger = t.fingerId; steerAnchorX = p.x; }
                if (t.fingerId == steerFinger && !ended) { dragSteer = Mathf.Clamp((p.x - steerAnchorX) / (Screen.width * 0.11f), -1, 1); steerHeld = true; }
            }
        }
        if (!gasSeen) gasFinger = -1;
        if (!brakeSeen) brakeFinger = -1;
        if (!steerHeld) { steerFinger = -1; dragSteer = 0; }
        // keyboard / editor
        float kSteer = (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A) ? 1 : 0);
        if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W)) gasT = 1;
        if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) brkT = 1;
        // pedals ramp like a real foot
        pedalGas = gasT > pedalGas ? Mathf.Min(gasT, pedalGas + 6 * dt) : Mathf.Max(gasT, pedalGas - 10 * dt);
        pedalBrake = brkT > pedalBrake ? Mathf.Min(brkT, pedalBrake + 8 * dt) : Mathf.Max(brkT, pedalBrake - 12 * dt);
        float steer = steerHeld ? dragSteer : kSteer;
        if (tilt && !steerHeld && kSteer == 0) steer = TiltSteer();
        return new CarInput(steer, pedalGas, pedalBrake);
    }
    static float PedalAmount(Rect r, Vector2 p) => Mathf.Clamp01(0.2f + 0.8f * (r.yMax - p.y) / r.height); // low on the pedal = light, high = full

    // ---------------------------------------------------------------- tilt steering
    // The phone is the steering wheel: its turn angle is the direction of gravity within the screen plane.
    //  * The angle is measured from a LOCKED "down" axis, so a big tilt can never flip the steering, and the screen
    //    is kept from auto-rotating while tilt steering is on (a rotating screen made the steering jump).
    //  * All sensor samples since the last frame are averaged, then smoothed with a "one euro" filter: it smooths
    //    hard when the phone is held still (no jitter) and hardly at all when it moves quickly (no lag).
    //  * Only when the phone lies almost flat is there too little gravity in the screen plane to measure; steering
    //    then fades out. It works normally from about 15 degrees of screen tilt upwards.
    // Dead zone, angle for full lock and smoothing are settings.
    float tiltCenter, tiltSmooth, tiltAxis = float.NaN, tiltAxisT, tiltConf, tiltRawDeg, tiltVel; bool tiltInit;
    static readonly float[] DeadZones = { 0f, 0.5f, 1f, 2f, 3f }; int dzIdx = 1;
    static readonly float[] TiltRanges = { 12f, 16f, 20f, 25f, 30f }; int rangeIdx = 2;
    static readonly string[] SmoothNames = { "Off", "Low", "Medium", "High" };
    static readonly float[] SmoothCut = { 60f, 6f, 3f, 1.5f }; int smoothIdx = 1;
    static readonly string[] SpeedSteerNames = { "Full lock always", "Quick", "Normal", "Calm" };
    static readonly double[] SpeedSteer = { 0.0, 0.25, 0.5, 1.25 }; int speedSteerIdx = 2;

    Vector3 Gravity()
    {
        // every accelerometer sample since the last frame (phones report 2-4 per frame), averaged
        Vector3 sum = Vector3.zero; float w = 0;
        int n = Input.accelerationEventCount;
        for (int i = 0; i < n; i++) { var e = Input.GetAccelerationEvent(i); float dtE = Mathf.Max(e.deltaTime, 1e-4f); sum += e.acceleration * dtE; w += dtE; }
        return w > 0 ? sum / w : Input.acceleration;
    }
    float TiltRaw()
    {
        var a = Gravity();
        float inPlane = Mathf.Sqrt(a.x * a.x + a.y * a.y);
        tiltConf = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.12f, 0.26f, inPlane));
        if (inPlane < 0.1f) return tiltRawDeg; // lying flat: nothing to measure, keep the last value
        float th = Mathf.Atan2(a.y, a.x) * Mathf.Rad2Deg;
        float nearest = Mathf.Round(th / 90f) * 90f;
        if (float.IsNaN(tiltAxis)) { if (inPlane < 0.3f) return 0; tiltAxis = nearest; }
        bool clearlyOther = Mathf.Abs(Mathf.DeltaAngle(th, nearest)) < 20f && Mathf.Abs(Mathf.DeltaAngle(nearest, tiltAxis)) > 1f && inPlane > 0.35f;
        if (clearlyOther) { tiltAxisT += Time.unscaledDeltaTime; if (tiltAxisT > 0.5f) { tiltAxis = nearest; tiltAxisT = 0; } }
        else tiltAxisT = 0;
        float deg = Mathf.Clamp(Mathf.DeltaAngle(tiltAxis, th), -85, 85);
        tiltRawDeg = invertTilt ? -deg : deg;
        return tiltRawDeg;
    }
    static float Alpha(float cutoffHz, float dt) => 1f / (1f + 1f / (2f * Mathf.PI * cutoffHz * dt));
    float TiltSteer()
    {
        float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.002f, 0.1f);
        float x = (TiltRaw() - tiltCenter) * tiltConf;
        if (!tiltInit) { tiltSmooth = x; tiltVel = 0; tiltInit = true; }
        // one euro filter: the faster the phone turns, the less it is smoothed
        float v = (x - tiltSmooth) / dt;
        tiltVel += (v - tiltVel) * Alpha(4f, dt);
        float cut = SmoothCut[smoothIdx] + 0.18f * Mathf.Abs(tiltVel);
        tiltSmooth += (x - tiltSmooth) * Alpha(cut, dt);
        return TiltToSteer();
    }
    float TiltToSteer()
    {
        float dz = DeadZones[dzIdx], range = TiltRanges[rangeIdx], a = Mathf.Abs(tiltSmooth);
        return a <= dz ? 0 : Mathf.Clamp(Mathf.Sign(tiltSmooth) * (a - dz) / (range - dz), -1, 1);
    }
    // while tilt steering is on the screen must not auto-rotate; with drag steering it may
    void ApplyScreenLock()
    {
        if (!tilt) { Screen.orientation = ScreenOrientation.AutoRotation; return; }
        // freeze the screen the way it is right now (never force a side: that could turn the picture upside down)
        var cur = Screen.orientation;
        if (cur == ScreenOrientation.LandscapeLeft || cur == ScreenOrientation.LandscapeRight) Screen.orientation = cur;
        tiltAxis = float.NaN; tiltInit = false;
    }
    int classIdx; // car class: 0 F1 2026, 1 Formula 2, 2 Formula 3
    CarConfig PlayerConfig()
    {
        var c = CarConfig.ForClass(classIdx);
        c.tc = tc; c.abs = absOn; c.aeroFront = classIdx == 0 ? Balances[balIdx] : c.aeroFront; c.steerSpeed = 14; c.steerReturn = 18;
        c.speedSens = SpeedSteer[speedSteerIdx]; c.compound = (Compound)compIdx;
        return c;
    }

    // ---------------------------------------------------------------- loop
    void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.1f);
        if (menuOpen) dt = 0; // the settings menu pauses the game
        if (hitT > 0) hitT -= dt;
        var input = ReadInput(dt);
        acc += dt;
        while (acc >= H)
        {
            SetSurface();
            px = car.x; py = car.y; ph = car.heading; pd = car.delta;
            if (WorldBuilder.Height != null) car.grade = playground ? 0 : WorldBuilder.Height.Grade(car.x, car.y, car.heading);
            if (rivals.Count > 0 && raceTime < Countdown) input = new CarInput(input.steer, 0, 1); // wait for the lights
            car.Step(input, H);
            if (!playground && idx >= 0 && Traffic.Wall(car, track.pts[idx], playerLat, wallAt)) { if (hitT <= 0) Flash("Barrier!"); hitT = 1.5f; }
            if (rivals.Count > 0) StepRivals();
            raceTime += H;
            simTime += H; acc -= H;
            frontSpin += car.vx / car.cfg.wheelRadius * H;
            if (playground) TestBench();
        }
        if (!playground) LapsAndRecover(dt);
        // g-forces for body pitch/roll and camera are low-passed, so tiny physics wobble never shakes the picture
        float kg = Mathf.Min(1, dt * 6);
        gLon += ((float)car.axLong - gLon) * kg; gLat += ((float)car.ay - gLat) * kg;
        if (car.vx * car.vx + car.vy * car.vy < 0.25) { gLon *= 1 - kg; gLat *= 1 - kg; }
        // visual wheel angle: cap the turn per frame so fast wheels never strobe backwards
        // (above ~0.5 rad per frame the eye sees the spokes stand still or run backwards, so the step is capped;
        // the wheels then read as "spinning fast" instead of wobbling)
        float wF = (float)(car.vx / car.cfg.wheelRadius), wRr = double.IsNaN(car.wR) ? wF : (float)car.wR;
        visF = Mathf.Repeat(visF + Mathf.Clamp(wF * dt, -0.5f, 0.5f), Mathf.PI * 2);
        visR = Mathf.Repeat(visR + Mathf.Clamp(wRr * dt, -0.5f, 0.5f), Mathf.PI * 2);
        // draw the car between the last two physics steps (frames don't line up with the 240 Hz steps;
        // without this the car moved 3, 4 or 5 steps per frame, a small rhythmic judder)
        if ((car.x - px) * (car.x - px) + (car.y - py) * (car.y - py) > 25) { px = car.x; py = car.y; ph = car.heading; pd = car.delta; } // reset / recover
        double al = acc / H, dh = System.Math.Atan2(System.Math.Sin(car.heading - ph), System.Math.Cos(car.heading - ph));
        double rx = px + (car.x - px) * al, ry = py + (car.y - py) * al, rh = ph + dh * al, rd = pd + (car.delta - pd) * al;
        model.Apply(rx, ry, rh, rd, visF, visR, gLon, gLat);
        if (LightBake.Active && LightBake.CarSun != null)
        {
            // the cars' sun dims when the player's car is inside a baked shadow
            float lit = playground ? 1 : LightBake.Sun((float)car.x, (float)car.y, model.transform.position.y + 0.6f);
            carLit += (lit - carLit) * Mathf.Min(1, dt * 8);
            LightBake.CarSun.intensity = 1.25f * (0.12f + 0.88f * carLit);
            Shader.SetGlobalFloat("_CarLit", carLit);
        }
        model.SetBrake(car.brake > 0.05); model.SetSteer((float)car.steerIn);
        for (int i = 0; i < rivals.Count; i++)
        {
            var c = rivals[i].car;
            rivalSpin[i] = Mathf.Repeat(rivalSpin[i] + Mathf.Clamp((float)(c.vx / c.cfg.wheelRadius) * dt, -0.5f, 0.5f), Mathf.PI * 2);
            // close rivals use the detailed model, distant ones the light one
            float ddx = (float)c.x - cam.transform.position.x, ddz = (float)c.y - cam.transform.position.z;
            bool near = ddx * ddx + ddz * ddz < (rivalNear[i] ? 34f * 34f : 28f * 28f) || rivalFar[i] == null;
            if (near != rivalNear[i]) { rivalNear[i] = near; rivalModels[i].gameObject.SetActive(near); if (rivalFar[i] != null) rivalFar[i].gameObject.SetActive(!near); }
            var rm = near ? rivalModels[i] : rivalFar[i];
            // drawn at the same in-between moment as the player's car, or they would shake against it
            double back = (1 - acc / H) * H, cch = System.Math.Cos(c.heading), csh = System.Math.Sin(c.heading);
            rm.Apply(c.x - (c.vx * cch - c.vy * csh) * back, c.y - (c.vx * csh + c.vy * cch) * back, c.heading - c.r * back, c.delta, rivalSpin[i], rivalSpin[i], 0, 0);
            rm.SetBrake(c.brake > 0.05);
        }
        UpdateSkids();
        AutoQuality();
        audioEngine.rpm = (float)car.rpm; audioEngine.throttle = (float)car.throttle;
        audioEngine.speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        audioEngine.slide = Mathf.Max((float)car.useF, (float)car.useR); audioEngine.onAsphalt = surface != 2; audioEngine.onKerb = car.vib > 0.3;
        audioEngine.redline = (float)car.cfg.redline; audioEngine.shifting = car.shiftTimer > 0;
        audioEngine.wheelspin = car.throttle > 0.05 ? Mathf.Clamp01(((float)car.slipRatioR - 0.14f) / 0.4f) : 0;
        audioEngine.lockup = car.lockF || car.lockR ? 1 : car.brake > 0.05 ? Mathf.Clamp01((-(float)car.slipRatioR - 0.2f) / 0.4f) : 0;
        if (menuOpen) { audioEngine.throttle = 0; audioEngine.speed = 0; audioEngine.slide = 0; }
        if (flashT > 0) flashT -= dt;
    }

    void SetSurface()
    {
        if (playground)
        {
            bool on = WorldBuilder.Pad.Contains(new Vector2((float)car.x, (float)car.y));
            surface = on ? 0 : 2;
        }
        else
        {
            var n = track.Nearest(car.x, car.y, idx);
            prevIdx = idx < 0 ? n.idx : idx; idx = n.idx; playerLat = n.lateral;
            double lat = System.Math.Abs(n.lateral), hw = track.width / 2;
            WheelContact.Update(track, line, car, idx, n.lateral, rubber);
            // 0 = all on the track, 1 = a tyre on a kerb, 2 = a tyre off the circuit
            int onTrack = 0, onKerb = 0;
            for (int w = 0; w < 4; w++)
            {
                var ty = car.wType[w];
                if (SurfaceTable.OnTrack(ty)) onTrack++; else if (ty == SurfaceType.Kerb || ty == SurfaceType.SausageKerb) onKerb++;
            }
            surface = onTrack == 4 ? 0 : onTrack + onKerb == 4 ? 1 : 2;
            if (onTrack + onKerb == 0) cut = true; // track limits: all four tyres beyond the kerbs
            return;
        }
        var s = surface == 0 ? Surfaces.Asphalt : surface == 1 ? Surfaces.Kerb : Surfaces.Grass;
        car.surfaceMu = s.mu; car.surfaceDrag = s.drag;
    }

    // rubber laid down on the circuit this session (0 = green track, 1 = fully rubbered-in): grows with the
    // distance all cars have driven; about 25 car-laps to rubber in fully
    double rubber;

    // Slipstream: how deep each car sits in the air of the car ahead (0..1). Full effect right behind, fading out
    // by 60 m back or 2.5 m to the side.
    void UpdateTow()
    {
        int n = rivals.Count; if (n == 0) { car.tow = 0; return; }
        int N = track.N;
        double Tow(int myIdx, double myLat, int skip)
        {
            double best = 0;
            for (int j = -1; j < n; j++)
            {
                if (j == skip) continue;
                int oi = j < 0 ? idx : rivals[j].idx; double ol = j < 0 ? playerLat : rivals[j].lateral;
                if (oi < 0) continue;
                int d = oi - myIdx; if (d > N / 2) d -= N; else if (d < -N / 2) d += N;
                double gap = d * track.ds; if (gap < 2 || gap > 60) continue;
                double side = System.Math.Abs(ol - myLat); if (side > 2.5) continue;
                best = System.Math.Max(best, (1 - gap / 60) * (1 - side / 2.5));
            }
            return best;
        }
        car.tow = idx < 0 || playground ? 0 : Tow(idx, playerLat, -1);
        for (int i = 0; i < n; i++) rivals[i].car.tow = rivals[i].idx < 0 ? 0 : Tow(rivals[i].idx, rivals[i].lateral, i);
    }

    void LapsAndRecover(float dt)
    {
        if (idx < 0) return;
        var p = track.pts[idx];
        double lat = System.Math.Abs(track.Nearest(car.x, car.y, idx).lateral);
        double hdiff = System.Math.Abs(System.Math.Atan2(System.Math.Sin(car.heading - System.Math.Atan2(p.ty, p.tx)), System.Math.Cos(car.heading - System.Math.Atan2(p.ty, p.tx))));
        offTime = surface == 2 && lat > track.width / 2 + WorldBuilder.Kerb + 0.9 ? offTime + dt : 0;
        wrongTime = hdiff > 1.75 && !car.reverse ? wrongTime + dt : 0;
        if (wrongTime > 1.2) Recover(); // off the track there are real barriers now; "Back on track" is a button
        if (prevIdx > track.N * 0.8 && idx < track.N * 0.2)
        {
            if (lapStart.HasValue)
            {
                lastLap = simTime - lapStart.Value;
                if (!cut && (!bestLap.HasValue || lastLap < bestLap)) { bestLap = lastLap; Flash("Best lap! " + Fmt(bestLap)); }
                else Flash((cut ? "Lap invalid  " : "Lap ") + Fmt(lastLap));
            }
            lapStart = simTime; lapNo++; pLap++; cut = false; car.NewLap();
        }
        else if (prevIdx < track.N * 0.2 && idx > track.N * 0.8) { lapStart = null; pLap--; }
    }

    void TestBench()
    {
        double v = car.vx * 3.6;
        if (v < 0.5 && car.throttle < 0.05) { launchT = -1; t100 = null; }
        else if (launchT < 0 && car.throttle > 0.5) launchT = simTime;
        if (launchT >= 0 && !t100.HasValue && v >= 100) t100 = simTime - launchT;
        topSpeed = System.Math.Max(topSpeed, v);
    }

    static string Fmt(double? t) { if (!t.HasValue) return "--:--.---"; int m = (int)(t.Value / 60); return $"{m}:{t.Value - m * 60:00.000}"; }

    // ---------------------------------------------------------------- camera
    Vector3 camVel; float gLon, gLat, visF, visR, carLit = 1;
    void LateUpdate()
    {
        var tr = model.transform;
        float speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        Vector3 pos, look; float fov;
        float lonG = gLon / 9.81f;
        if (camMode >= 2)
        {
            // 2 = cockpit (driver's eyes, low behind the halo); 3 = halo cam (just above the driver's head)
            bool halo = camMode == 3;
            pos = tr.TransformPoint(halo ? new Vector3(0, 1.02f, 0.12f - lonG * 0.03f) : new Vector3(0, 0.822f, 0.395f - lonG * 0.03f));
            look = tr.TransformPoint(new Vector3(0, (halo ? 0.55f : -0.63f) + lonG * 0.2f, 30)); // cockpit: 2.8 deg down, fitted to a real F1 onboard frame
            fov = halo ? 66 : 51; // cockpit: fitted to a real F1 onboard frame (eye, angle and view solved from its halo, tyre and wheel positions); no widening with speed
            // the surface shakes the driver's head: kerb ridges, gravel, grass
            float sh = (float)car.vib * Mathf.Min(1, speed / 20f) * (halo ? 0.006f : 0.009f);
            if (sh > 0) pos += tr.up * (Mathf.PerlinNoise(Time.time * 37f, 0.3f) - 0.5f) * 2 * sh + tr.right * (Mathf.PerlinNoise(0.7f, Time.time * 29f) - 0.5f) * sh;
            cam.transform.position = pos;
        }
        else
        {
            float back = camMode == 0 ? 6.2f : 10f, up = camMode == 0 ? 2f : 3.4f;
            back += Mathf.Clamp(lonG * 0.35f, -0.9f, 0.5f);
            pos = tr.TransformPoint(new Vector3(0, up, -back));
            look = tr.TransformPoint(new Vector3(0, 0.7f, 6));
            fov = 62; // fixed: widening the view with speed exaggerated how fast the car felt
            cam.transform.position = Vector3.SmoothDamp(cam.transform.position, pos, ref camVel, 0.06f);
        }
        cam.transform.LookAt(look);
        if (tilt && camMode == 2) cam.transform.Rotate(0, 0, -tiltSmooth * 0.6f, Space.Self);
        cam.fieldOfView += (fov - cam.fieldOfView) * Mathf.Min(1, Time.deltaTime * 4);
        model.helmet.gameObject.SetActive(camMode != 2);
        if (model.cockpitFloor) model.cockpitFloor.SetActive(camMode == 2);
        cam.nearClipPlane = camMode >= 2 ? 0.05f : 0.3f;
    }

    // ---------------------------------------------------------------- HUD
    GUIStyle big, small, btn, center, gearStyle;
    Texture2D white;
    void OnGUI()
    {
        if (big == null)
        {
            white = Texture2D.whiteTexture;
            big = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft };
            center = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            btn = new GUIStyle(GUI.skin.button);
            big.richText = small.richText = center.richText = true;
        }
        big.fontSize = (int)S(34); small.fontSize = (int)S(11); center.fontSize = (int)S(13); btn.fontSize = (int)S(12);
        float speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);

        // top-right: Settings, Camera, Restart. Everything else lives in the settings menu.
        float bh = S(30), bw = S(74), x0 = Screen.width - S(8);
        if (GUI.Button(new Rect(x0 - 3 * (bw + S(4)), S(8), bw, bh), menuOpen ? "Close" : "Settings", btn)) { menuOpen = !menuOpen; if (!menuOpen) ApplyScreenLock(); }
        if (GUI.Button(new Rect(x0 - 2 * (bw + S(4)), S(8), bw, bh), "Cam", btn)) { camMode = (camMode + 1) % CamNames.Length; Flash(CamNames[camMode]); Save(); }
        if (GUI.Button(new Rect(x0 - 1 * (bw + S(4)), S(8), bw, bh), "Restart", btn)) { ResetCar(); menuOpen = false; }
        if (!menuOpen && !playground && (surface == 2 || hitT > 0) && GUI.Button(new Rect(Screen.width / 2 - S(60), Screen.height * 0.5f, S(120), bh), "Back on track", btn)) Recover();
        if (menuOpen)
        {
            string[] names = { "Sound", "Traction control", "ABS brakes", "Aero balance", "Steering", "Tilt direction", "Tilt centre", "Track", "Racing line", "Rivals", "Rival level", "Camera", "Tilt dead zone", "Tilt for full lock", "Tilt smoothing", "Steering at speed", "Graphics", "Tyres", "Car" };
            string[] vals = { sound ? "On" : "Off", tc.ToString(), absOn ? "On" : "Off (real F1)", $"{Balances[balIdx] * 100:0}% front", tilt ? "Tilt the phone" : "Drag on screen",
                              invertTilt ? "Inverted" : "Normal", "Set straight now", MapNames[mapIdx], showLine ? "Shown" : "Hidden", RivalCounts[rivalIdx].ToString(), DiffNames[diffIdx], CamNames[camMode],
                              $"{DeadZones[dzIdx]:0.#}°", $"{TiltRanges[rangeIdx]:0}°", SmoothNames[smoothIdx], SpeedSteerNames[speedSteerIdx], gfxIdx == 0 ? "Auto (" + GfxNames[gfxLevel] + ")" : GfxNames[gfxIdx], CompoundTable.Defs[compIdx].name + " (fit on restart)", CarConfig.ClassNames[classIdx] };
            int rows = (names.Length + 1) / 2;
            float panW = Mathf.Min(Screen.width - S(40), S(600)), rh = S(28), panH = rows * rh + S(52);
            var panel = new Rect((Screen.width - panW) / 2, (Screen.height - panH) / 2 + S(10), panW, panH);
            Fill(panel, new Color(0.05f, 0.06f, 0.08f, 0.93f));
            center.fontSize = (int)S(15); GUI.Label(new Rect(panel.x, panel.y + S(6), panW, S(24)), "<b>SETTINGS</b>   (game paused)", center);
            float cw = panW / 2;
            for (int i = 0; i < names.Length; i++)
            {
                float cx = panel.x + (i % 2) * cw + S(10), cy = panel.y + S(38) + (i / 2) * rh;
                small.fontSize = (int)S(12); GUI.Label(new Rect(cx, cy + S(7), cw * 0.45f, rh), names[i], small); small.fontSize = (int)S(11);
                if (!GUI.Button(new Rect(cx + cw * 0.43f, cy + S(2), cw * 0.57f - S(20), rh - S(5)), vals[i], btn)) continue;
                if (i == 0) { sound = !sound; audioEngine.muted = !sound; }
                if (i == 1) { tc = (TcMode)(((int)tc + 1) % 3); car.cfg.tc = tc; }
                if (i == 2) { absOn = !absOn; car.cfg.abs = absOn; }
                if (i == 3) { balIdx = (balIdx + 1) % Balances.Length; car.cfg.aeroFront = Balances[balIdx]; }
                if (i == 4) { tilt = !tilt; tiltCenter = 0; ApplyScreenLock(); }
                if (i == 5) invertTilt = !invertTilt;
                if (i == 6) { tiltCenter = TiltRaw(); Flash("Straight = how you hold it now"); }
                if (i == 7) { mapIdx = (mapIdx + 1) % MapNames.Length; LoadMap(); ResetCar(); }
                if (i == 8) { showLine = !showLine; if (lineGo != null) lineGo.SetActive(showLine && !playground); }
                if (i == 9) { rivalIdx = (rivalIdx + 1) % RivalCounts.Length; ResetCar(); }
                if (i == 10) { diffIdx = (diffIdx + 1) % DiffNames.Length; ResetCar(); }
                if (i == 11) camMode = (camMode + 1) % CamNames.Length;
                if (i == 12) dzIdx = (dzIdx + 1) % DeadZones.Length;
                if (i == 13) rangeIdx = (rangeIdx + 1) % TiltRanges.Length;
                if (i == 14) smoothIdx = (smoothIdx + 1) % SmoothCut.Length;
                if (i == 18) { classIdx = (classIdx + 1) % CarConfig.ClassNames.Length; BuildLine(); ResetCar(); }
                if (i == 17) compIdx = (compIdx + 1) % CompoundTable.Defs.Length;
                if (i == 16) { gfxIdx = (gfxIdx + 1) % GfxNames.Length; ApplyGraphics(gfxIdx == 0 ? 2 : gfxIdx); }
                if (i == 15) { speedSteerIdx = (speedSteerIdx + 1) % SpeedSteer.Length; car.cfg.speedSens = SpeedSteer[speedSteerIdx]; }
                Save();
            }
        }

        // top-centre: lap time or test data
        string top, sub;
        if (playground) { top = "TEST DATA (ours vs real F1)"; sub = $"0-100 {(t100.HasValue ? t100.Value.ToString("0.00") + "s" : "—")} (real 2.2-2.6)  ·  top {topSpeed:0} km/h"; }
        else { top = lapStart.HasValue ? Fmt(simTime - lapStart.Value) : "--:--.---"; sub = (rivals.Count > 0 ? $"P{Position()}/{rivals.Count + 1}  ·  " : "") + $"Lap {lapNo}  ·  Last {Fmt(lastLap)}  ·  Best {Fmt(bestLap)}"; }
        float py = S(44);
        Panel(new Rect(Screen.width / 2 - S(150), py, S(300), S(44)));
        center.fontSize = (int)S(18); GUI.Label(new Rect(Screen.width / 2 - S(150), py, S(300), S(26)), top, center);
        center.fontSize = (int)S(11); GUI.Label(new Rect(Screen.width / 2 - S(150), py + S(22), S(300), S(20)), sub, center);

        // dash
        var dash = new Rect(Screen.width / 2 - S(95), Screen.height - S(80), S(190), S(70));
        Panel(dash);
        GUI.Label(new Rect(dash.x, dash.y + S(2), dash.width * 0.72f, S(40)), $"{speed * 3.6f:0}<size={(int)S(12)}> km/h</size>", big);
        if (gearStyle == null) { gearStyle = new GUIStyle(big); gearStyle.normal.textColor = new Color(1f, 0.82f, 0.25f); }
        gearStyle.fontSize = (int)S(24);
        GUI.Label(new Rect(dash.x + dash.width * 0.7f, dash.y + S(2), dash.width * 0.3f, S(40)), car.reverse ? "R" : car.gear.ToString(), gearStyle);
        float rpmFrac = Mathf.Clamp01((float)((car.rpm - car.cfg.idleRpm) / (car.cfg.redline - car.cfg.idleRpm)));
        Fill(new Rect(dash.x + S(10), dash.y + S(44), dash.width - S(20), S(6)), new Color(1, 1, 1, 0.2f));
        Fill(new Rect(dash.x + S(10), dash.y + S(44), (dash.width - S(20)) * rpmFrac, S(6)), Color.Lerp(new Color(0.24f, 0.86f, 0.52f), new Color(1f, 0.3f, 0.23f), rpmFrac));
        center.fontSize = (int)S(10);
        string lights = (car.tcActive ? "<color=#ffb020>TC</color>" : "TC") + "   " + (car.absActive ? "<color=#ffb020>ABS</color>" : "ABS") + "   " +
                        (car.lockF || car.lockR ? "<color=#ff4b3a>LOCK</color>" : "LOCK") + "   " + (car.spinR ? "<color=#ff4b3a>SPIN</color>" : "SPIN");
        // hybrid battery and active-aero state
        float batt = double.IsNaN(car.ersEnergy) ? 1f : (float)(car.ersEnergy / car.cfg.ersCapacity);
        Fill(new Rect(dash.x, dash.yMax + S(2), dash.width, S(5)), new Color(1, 1, 1, 0.2f));
        Fill(new Rect(dash.x, dash.yMax + S(2), dash.width * batt, S(5)), car.ersKw > 1 ? new Color(0.3f, 0.75f, 1f) : car.ersKw < -1 ? new Color(0.24f, 0.86f, 0.52f) : new Color(0.75f, 0.8f, 0.85f));
        lights += "   " + (car.aeroX > 0.5 ? "<color=#4cc3ff>WING</color>" : "WING");
        GUI.Label(new Rect(dash.x, dash.y + S(50), dash.width, S(18)), lights, center);

        // telemetry
        double beta = System.Math.Atan2(car.vy, System.Math.Max(System.Math.Abs(car.vx), 1)) * 57.3;
        Panel(new Rect(S(8), S(8), S(178), tilt ? S(190) : S(174)));
        GUI.Label(new Rect(S(14), S(10), S(178), S(190)),
            $"{fpsAvg:0} fps" + (renderScale < 0.99f ? $" ({renderScale * 100:0}% res)" : "") + $"\nFront grip {car.useF * 100:0}%\nRear grip {car.useR * 100:0}%\nRear spin {car.slipRatioR * 100:0}%\nSlide {beta:0.0}°\nG {System.Math.Sqrt(car.ax * car.ax + car.ay * car.ay) / 9.81:0.0}" +
            (car.cfg.chassis && !double.IsNaN(car.tyreTF) ? $"\n{CompoundTable.Get(car.cfg.compound).name} {car.tyreTF:0}° / {car.tyreTR:0}°, worn {car.wearF * 100:0} / {car.wearR * 100:0}%\nBrakes {car.brakeTF:0}° / {car.brakeTR:0}°\nRide {car.rideF * 1000:0} / {car.rideR * 1000:0} mm\nAero {car.downforce / 9.81:0} kg, {car.aeroBalance * 100:0}% front" + (car.tow > 0.05 ? $"\nSlipstream {car.tow * 100:0}%" : "") : "") +
            (tilt ? $"\nTilt {(tiltSmooth < -0.5f ? "L" : tiltSmooth > 0.5f ? "R" : "")} {Mathf.Abs(tiltSmooth):0}° → steer {Mathf.Abs(TiltToSteer()) * 100:0}%" : ""), small);

        DrawMiniMap();
        if (rivals.Count > 0 && raceTime < Countdown + 0.8)
        {
            center.fontSize = (int)S(54);
            GUI.Label(new Rect(0, Screen.height * 0.18f, Screen.width, S(70)), raceTime < Countdown ? Mathf.CeilToInt((float)(Countdown - raceTime)).ToString() : "GO!", center);
        }

        // pedals
        Pedal(GasRect, pedalGas, new Color(0.24f, 0.86f, 0.52f), "GAS");
        Pedal(BrakeRect, pedalBrake, new Color(1f, 0.3f, 0.23f), "BRAKE");
        if (!tilt) { center.fontSize = (int)S(11); GUI.Label(new Rect(S(20), Screen.height - S(40), Screen.width * 0.4f, S(20)), "Touch & drag to steer", center); }

        if (Time.realtimeSinceStartup < 8f) { small.fontSize = (int)S(9); GUI.Label(new Rect(S(8), Screen.height - S(16), Screen.width, S(14)), CarModel.Credit, small); small.fontSize = (int)S(11); }
        if (flashT > 0) { center.fontSize = (int)S(18); Panel(new Rect(Screen.width / 2 - S(140), Screen.height * 0.38f, S(280), S(34))); GUI.Label(new Rect(Screen.width / 2 - S(140), Screen.height * 0.38f, S(280), S(34)), flash, center); }
    }
    void Panel(Rect r) => Fill(r, new Color(0, 0, 0, 0.35f));
    void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = o; }
    void Pedal(Rect r, float v, Color c, string label)
    {
        Fill(r, new Color(c.r, c.g, c.b, 0.18f));
        Fill(new Rect(r.x, r.yMax - r.height * v, r.width, r.height * v), new Color(c.r, c.g, c.b, 0.55f));
        center.fontSize = (int)S(12); GUI.Label(new Rect(r.x, r.yMax - S(30), r.width, S(24)), label, center);
    }
}
