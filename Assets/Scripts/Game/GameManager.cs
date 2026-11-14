// GameManager.cs — runs the game: 240 Hz physics, touch/tilt/keyboard input, cameras, HUD (IMGUI, no packages),
// laps and auto-recover. Created automatically at startup (see Boot), so the scene can stay empty.
// The settings (new fields, load/save, the rows of the settings screen, input shaping and driver aids) are in
// GameManagerSettings.cs, the other half of this class; the settings screen itself is SettingsUI.cs.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public partial class GameManager : MonoBehaviour
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
    EngineAudio audioEngine;
    static readonly string[] CamNames = { "Chase", "Far chase", "Cockpit", "Halo cam", "TV" };
    const int TvMode = 4; // trackside TV cameras (see TvCamera)
    int liveryIdx; // the player's livery (Livery.Player), saved as "livery"

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
        mapIdx = Mathf.Clamp(PlayerPrefs.GetInt("map", 0), 0, MapNames.Length - 1); playground = mapIdx == PlaygroundIdx; camMode = Mathf.Clamp(PlayerPrefs.GetInt("cam", 0), 0, CamNames.Length - 1); sound = PlayerPrefs.GetInt("snd", 1) == 1; showLine = PlayerPrefs.GetInt("line", 0) == 1; absOn = PlayerPrefs.GetInt("abs", 1) == 1; classIdx = Mathf.Clamp(PlayerPrefs.GetInt("class", 0), 0, CarConfig.ClassNames.Length - 1); compIdx = Mathf.Clamp(PlayerPrefs.GetInt("tyres", 0), 0, CompoundTable.Defs.Length - 1); gfxIdx = Mathf.Clamp(PlayerPrefs.GetInt("gfx", 0), 0, GfxNames.Length - 1);
        LoadSettings(); // everything the settings screen added (GameManagerSettings.cs)
        Weather.Load(); // before the world is built: the sky and fog are made for it
        tyreSet = Mathf.Clamp(PlayerPrefs.GetInt("tyreSet", compIdx), 0, Weather.Sets - 1); carSet = tyreSet;
        smoothIdx = Mathf.Clamp(PlayerPrefs.GetInt("tiltSmooth", 1), 0, SmoothCut.Length - 1); speedSteerIdx = Mathf.Clamp(PlayerPrefs.GetInt("speedSteer", 2), 0, SpeedSteer.Length - 1);
        ApplyScreenLock();
        Haptics.enabled = PlayerPrefs.GetInt("haptics", 1) == 1;
        liveryIdx = Livery.PlayerIndex;
        rivalIdx = Mathf.Clamp(PlayerPrefs.GetInt("rivals", 1), 0, RivalCounts.Length - 1); diffIdx = Mathf.Clamp(PlayerPrefs.GetInt("diff", 0), 0, DiffNames.Length - 1);
        ui = new MenuUI(MapNames, PlaygroundIdx, RivalCounts, DiffNames); // the start menu is up (game paused) until a session starts

        LoadMap();
        car = new FormulaCar(PlayerConfig());
        model = CarModel.CreateFromAsset() ?? CarModel.Create(car.cfg.wheelbase, car.cfg.frontWeight, car.cfg.wheelRadius);
        model.SetLivery(Livery.Player[liveryIdx]);
        var camGo = new GameObject("Camera"); cam = camGo.AddComponent<Camera>(); camGo.tag = "MainCamera"; cam.allowDynamicResolution = true;
        cam.nearClipPlane = 0.2f; cam.farClipPlane = 2500; cam.fieldOfView = 62;
        cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor; cam.backgroundColor = RenderSettings.fogColor;
        camGo.AddComponent<AudioListener>();
        audioEngine = camGo.AddComponent<EngineAudio>(); audioEngine.muted = !sound;
        weatherFx = WeatherFx.Create(cam);
        ApplyAudio(); ApplyDisplay();
        ApplyGraphics(gfxIdx == 0 ? 2 : gfxIdx);
        ResetCar();
    }

    int compIdx; // tyre compound (physics; follows tyreSet)
    int tyreSet, carSet, rivalSet; // tyre set chosen / on the player's car now / on the rivals (Weather: 0-2 slicks, 3 inter, 4 wet)
    WeatherFx weatherFx;
    // weather changed (start menu or settings): sky, fog, sun, glare and the road / paint globals at once
    void ApplyWeather() { Weather.PushGlobals(); Sky.SetWeather(Weather.Wetness); }
    int builtFor = -1; double wallAt = 1e9; float hitT; bool menuOpen, absOn = true;
    void LoadMap()
    {
        playground = mapIdx == PlaygroundIdx;
        int circuit = playground ? (builtFor >= 0 ? builtFor : 0) : mapIdx;
        if (circuit != builtFor || world == null)
        {
            if (world != null) Destroy(world);
            track = Circuits.All[circuit].Build();
            rubber = 0; marbles = 0; // a fresh, "green" track
            var wr = new System.Random(circuit * 7919 + System.DateTime.Now.DayOfYear);
            double ws = wr.NextDouble() * 6, wd = wr.NextDouble() * 2 * System.Math.PI; // steady breeze 0-6 m/s for this session
            windX = ws * System.Math.Cos(wd); windY = ws * System.Math.Sin(wd);
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
        QualitySettings.shadows = level <= 1 || LightBake.Active || !shadowsOn ? ShadowQuality.Disable : ShadowQuality.All; // baked lighting needs no shadow maps
        QualitySettings.shadowCascades = level >= 3 ? 2 : 1;
        QualitySettings.shadowResolution = level >= 3 ? ShadowResolution.High : ShadowResolution.Medium;
        QualitySettings.shadowDistance = level >= 3 ? 90 : 55;
        QualitySettings.anisotropicFiltering = level <= 1 ? AnisotropicFiltering.Disable : AnisotropicFiltering.ForceEnable;
        if (cam != null) cam.farClipPlane = level <= 1 ? 1500 : 2500;
    }
    // Dynamic resolution: when the phone can't hold ~50 fps the 3D picture is drawn at a lower resolution
    // (down to 60%) and scaled up; the HUD is drawn afterwards at full resolution, so text stays sharp.
    // Works where the graphics API supports it (Vulkan, Metal); elsewhere it does nothing.
    // The "Resolution scale" setting is the most it may use (ResMax); "about 50 fps" is 83% of the frame-rate target.
    float renderScale = 1f, scaleT;
    void DynamicResolution()
    {
        if (Paused || Time.realtimeSinceStartup < 6f) return;
        if (fpsAvg < FpsTarget * 0.83f) scaleT -= Time.unscaledDeltaTime; else if (fpsAvg > FpsTarget * 0.96f) scaleT += Time.unscaledDeltaTime; else scaleT = 0;
        float ns = renderScale;
        if (scaleT < -1.5f) { ns = Mathf.Max(ResMin, renderScale - 0.1f); scaleT = 0; }
        else if (scaleT > 6f) { ns = Mathf.Min(ResMax, renderScale + 0.05f); scaleT = 0; }
        if (!Mathf.Approximately(ns, renderScale)) { renderScale = ns; ScalableBufferManager.ResizeBuffers(ns, ns); }
    }
    void AutoQuality()
    {
        float fps = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.001f);
        fpsAvg += (fps - fpsAvg) * 0.05f;
        DynamicResolution();
        if (renderScale > ResMin + 0.05f) { slowT = 0; return; } // first lower the resolution, then the effects
        if (gfxIdx != 0 || Paused || Time.realtimeSinceStartup < 6f || gfxLevel <= 1) return;
        slowT = fpsAvg < FpsTarget * 0.83f ? slowT + Time.unscaledDeltaTime : 0;
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
        var lc = CarConfig.ForClass(classIdx); lc.aeroFront = aeroBal / 100.0;
        line = RacingLine.Compute(track, lc); lineFor = classIdx;
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
        PlayerPrefs.SetInt("map", mapIdx); PlayerPrefs.SetInt("cam", camMode); PlayerPrefs.SetInt("snd", sound ? 1 : 0); PlayerPrefs.SetInt("line", showLine ? 1 : 0); PlayerPrefs.SetInt("abs", absOn ? 1 : 0); PlayerPrefs.SetInt("class", classIdx); PlayerPrefs.SetInt("tyres", compIdx); PlayerPrefs.SetInt("gfx", gfxIdx); PlayerPrefs.SetInt("tiltSmooth", smoothIdx); PlayerPrefs.SetInt("speedSteer", speedSteerIdx); PlayerPrefs.SetInt("rivals", rivalIdx); PlayerPrefs.SetInt("diff", diffIdx); PlayerPrefs.SetInt("haptics", Haptics.enabled ? 1 : 0); Livery.PlayerIndex = liveryIdx;
        PlayerPrefs.SetInt("tyreSet", tyreSet); Weather.Save();
        SaveSettings(); // also writes the old "bal", "tiltDz", "tiltRange" and "brakeBias" keys
        PlayerPrefs.Save();
    }

    void ResetCar()
    {
        car = new FormulaCar(PlayerConfig());
        car.windX = windX; car.windY = windY;
        carSet = tyreSet;
        if (model != null) model.SetStripe(Weather.Stripe(carSet));
        SetupRivals();
        if (playground) { car.x = WorldBuilder.PadStart.x - 15; car.y = WorldBuilder.PadStart.y; car.heading = 0; }
        else if (GridStart) PlacePlayerOnGrid(); // a race: the slot chosen on the start menu (GameManagerStart.cs)
        else
        {
            var p = track.pts[track.N - 30]; // practice: alone, on the centre of the straight
            car.x = p.x; car.y = p.y; car.heading = System.Math.Atan2(p.ty, p.tx);
        }
        raceTime = 0; pLap = 0; aiTick = 0; boxArmed = false; pitLoss = 0; pitStops = 0;
        if (raceLaps > 0) { bestLap = lastLap = null; lapNo = 1; } // a race keeps its own lap records
        idx = prevIdx = -1; lapStart = null; cut = false; offTime = wrongTime = 0; t100 = null; launchT = -1; topSpeed = 0;
        Flash(MapNames[mapIdx]);
        StartReset(); // lights, launch, jump-start check (GameManagerStart.cs)
    }

    // ---------------------------------------------------------------- rivals
    static readonly int[] RivalCounts = { 0, 5, 10, 15 }; int rivalIdx;
    static readonly string[] DiffNames = { "Easy", "Medium", "Hard", "Real" };
    static readonly double[] DiffScale = { 0.82, 0.89, 0.95, 1.0 }; int diffIdx;
    readonly List<AiDriver> rivals = new List<AiDriver>(); readonly List<CarModel> rivalModels = new List<CarModel>(); readonly List<float> rivalSpin = new List<float>();
    readonly List<CarModel> rivalFar = new List<CarModel>(); readonly List<bool> rivalNear = new List<bool>();
    double raceTime; int pLap, aiTick; double playerLat;

    void SetupRivals()
    {
        foreach (var m in rivalModels) if (m != null) Destroy(m.gameObject);
        foreach (var m in rivalFar) if (m != null) Destroy(m.gameObject);
        rivals.Clear(); rivalModels.Clear(); rivalSpin.Clear(); rivalFar.Clear(); rivalNear.Clear();
        int count = playground || line == null || raceLaps == 0 ? 0 : RivalCounts[rivalIdx]; // free practice: alone
        var grid = DriverProfile.Grid;
        rivalSet = Weather.BestSet(); // the rivals start on the right tyres for the weather
        ChooseGridSlot(count + 1);
        for (int k = 0; k < count; k++)
        {
            // spread the picks over the whole field: stars, midfield and backmarkers
            var who = grid[count == 1 ? 0 : Mathf.RoundToInt(k * (grid.Length - 1) / (float)(count - 1))];
            var rc = CarConfig.ForClass(classIdx); rc.fuelStart = RaceFuel();
            rc.compound = Weather.PhysicsCompound(rivalSet);
            // the speed profile's grip share multiplies the tyre mu directly (RacingLine.SpeedProfile), so the wet
            // factor of their tyres on the racing line slows corners and braking just as the grip loss does
            var tune = AiTuning.For(builtFor); tune.grip *= Weather.TrackFactor(rivalSet, 1);
            var d = new AiDriver(track, line, rc, who, tune, DiffScale[diffIdx], k + 1);
            PlaceRival(d, k); // the k-th grid slot the player does not take (GameManagerStart.cs)
            d.car.windX = windX; d.car.windY = windY;
            rivals.Add(d); rivalSpin.Add(0);
            var col = new Color(who.r, who.g, who.b);
            var liv = Livery.ForTeam(TeamIndex(who), col); // the team's two-tone livery, primary = team colour
            var near = CarModel.CreateFromAsset("Car/car_lod", col, true) ?? CarModel.Create(d.car.cfg.wheelbase, d.car.cfg.frontWeight, d.car.cfg.wheelRadius);
            near.SetLivery(liv); rivalModels.Add(near);
            var far = CarModel.CreateFromAsset("Car/car_far", col, true); if (far != null) { far.SetLivery(liv); far.gameObject.SetActive(false); }
            rivalFar.Add(far); rivalNear.Add(true);
        }
        ClearRecording();
    }

    // position of a driver's team in the grid's list of teams (teams in the order they first appear)
    static int TeamIndex(DriverProfile who)
    {
        var teams = new List<string>();
        foreach (var g in DriverProfile.Grid)
        {
            if (!teams.Contains(g.team)) teams.Add(g.team);
            if (g.team == who.team) return teams.IndexOf(g.team);
        }
        return 0;
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
            marbles = System.Math.Min(1, marbles + dist / (track.length * 400));
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
            if (aiTick % 4 == 1) { WheelContact.Update(track, line, d.car, d.idx, d.lateral, rubber, marbles); Weather.Apply(track, line, d.car, d.idx, d.lateral, rivalSet); }
            d.car.Step(RivalStartInput(i, d), H); // held until lights out + reaction time, then the launch clutch
        }
    }

    int Position()
    {
        double mine = pLap + (idx < 0 ? 0 : (double)idx / track.N) - pitLoss / RefLap(); int pos = 1;
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
            if (t.fingerId == gasFinger) { if (!ended) { gasT = gasMode == 1 ? 1f : PedalAmount(GasRect, p); gasSeen = true; } continue; }
            if (t.fingerId == brakeFinger) { if (!ended) { brkT = brakeMode == 1 ? 1f : PedalAmount(BrakeRect, p); brakeSeen = true; } continue; }
            if (!tilt && p.x < Screen.width * 0.42f && p.y > S(60))
            {
                if (t.phase == TouchPhase.Began || steerFinger < 0) { steerFinger = t.fingerId; steerAnchorX = p.x; }
                if (t.fingerId == steerFinger && !ended) { dragSteer = Mathf.Clamp((p.x - steerAnchorX) / (Screen.width * 0.11f) * steerGain, -1, 1); steerHeld = true; }
            }
        }
        if (!gasSeen) gasFinger = -1;
        if (!brakeSeen) brakeFinger = -1;
        if (!steerHeld) { steerFinger = -1; dragSteer = 0; }
        // keyboard / editor
        float kSteer = (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A) ? 1 : 0);
        if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W)) gasT = 1;
        if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) brkT = 1;
        // settings: brake sensitivity and curve, then the braking assist (it brakes for the corner and lifts the throttle)
        brkT = ShapeBrake(brkT);
        float autoBrake = AssistBrake();
        if (autoBrake > brkT) brkT = autoBrake;
        if (autoBrake > 0.05f) gasT = 0;
        // pedals ramp like a real foot
        pedalGas = gasT > pedalGas ? Mathf.Min(gasT, pedalGas + 6 * dt) : Mathf.Max(gasT, pedalGas - 10 * dt);
        pedalBrake = brkT > pedalBrake ? Mathf.Min(brkT, pedalBrake + 8 * dt) : Mathf.Max(brkT, pedalBrake - 12 * dt);
        float steer = steerHeld ? dragSteer : kSteer;
        if (tilt && !steerHeld && kSteer == 0) steer = ShapeSteer(TiltSteer());
        else if (steerHeld) steer = ShapeSteer(steer);       // the keyboard is on/off: no curve
        steer = AssistSteer(steer);
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
        float dz = tiltDz, range = Mathf.Max(tiltRange, dz + 1f), a = Mathf.Abs(tiltSmooth);
        return a <= dz ? 0 : Mathf.Clamp(Mathf.Sign(tiltSmooth) * (a - dz) / (range - dz) * steerGain, -1, 1);
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
        c.tc = tc; c.abs = absOn;
        compIdx = (int)Weather.PhysicsCompound(tyreSet);
        c.speedSens = SpeedSteer[speedSteerIdx]; c.compound = (Compound)compIdx; c.fuelStart = RaceFuel();
        cfgClass = classIdx; ApplySetup(c); // steering rates and lock, aero balance, brakes, differential, ride heights
        return c;
    }

    // ---------------------------------------------------------------- loop
    void Update()
    {
        if (ui.LoadDue) StartSession();
        if (menuOpen && Input.GetKeyDown(KeyCode.Escape)) CloseSettings(); // Android "back"
        float dt = Mathf.Min(Time.deltaTime, 0.1f);
        if (replaying) { ReplayStep(dt); if (flashT > 0) flashT -= dt; return; } // the race is over: only the recording moves
        if (Paused) dt = 0; // the settings menu and the front end pause the game
        if (hitT > 0) hitT -= dt;
        var input = ReadInput(dt);
        acc += dt;
        double wallDv = 0; // biggest speed change from a barrier hit this frame (m/s), for the haptics
        while (acc >= H)
        {
            SetSurface();
            px = car.x; py = car.y; ph = car.heading; pd = car.delta;
            if (WorldBuilder.Height != null) car.grade = playground ? 0 : WorldBuilder.Height.Grade(car.x, car.y, car.heading);
            car.deployMap = !playground && line != null && line.deploy != null && idx >= 0 && idx < line.deploy.Length ? line.deploy[idx] : 1; // same energy strategy as the AI
            car.Step(StartInput(input), H); // the start: revs only on the grid, launch clutch after lights out
            StartAfterStep();
            double preVx = car.vx, preVy = car.vy; // Wall does not turn the car, so the body-frame change is the real one
            if (!playground && idx >= 0 && Traffic.Wall(car, track.pts[idx], playerLat, wallAt))
            {
                if (hitT <= 0) Flash("Barrier!"); hitT = 1.5f;
                double dvx = car.vx - preVx, dvy = car.vy - preVy;
                wallDv = System.Math.Max(wallDv, System.Math.Sqrt(dvx * dvx + dvy * dvy));
            }
            if (rivals.Count > 0) StepRivals();
            raceTime += H;
            simTime += H; acc -= H;
            frontSpin += car.vx / car.cfg.wheelRadius * H;
            if (playground) TestBench();
        }
        if (!Paused) Rumble(wallDv);
        if (!playground) LapsAndRecover(dt);
        if (raceLaps > 0 && !playground && pLap > raceLaps && !ui.Showing) FinishRace(); // pLap 1 = crossing the line from the grid
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
            LightBake.CarSun.intensity = 1.25f * (0.12f + 0.88f * carLit) * Sky.SunShare;
            Shader.SetGlobalFloat("_CarLit", carLit);
        }
        model.SetBrake(car.brake > 0.05); model.SetSteer((float)car.steerIn);
        model.Feed(car, dt, surface == 0);
        for (int i = 0; i < rivals.Count; i++)
        {
            var c = rivals[i].car;
            rivalSpin[i] = Mathf.Repeat(rivalSpin[i] + Mathf.Clamp((float)(c.vx / c.cfg.wheelRadius) * dt, -0.5f, 0.5f), Mathf.PI * 2);
            // close rivals use the detailed model, distant ones the light one
            // (a zoomed-in TV camera sees far cars big: their distance counts as shorter by the zoom)
            float ddx = (float)c.x - cam.transform.position.x, ddz = (float)c.y - cam.transform.position.z, zoom = camMode == TvMode ? Mathf.Clamp(cam.fieldOfView / 62f, 0.05f, 1f) : 1f;
            bool near = (ddx * ddx + ddz * ddz) * zoom * zoom < (rivalNear[i] ? 34f * 34f : 28f * 28f) || rivalFar[i] == null;
            if (near != rivalNear[i]) { rivalNear[i] = near; rivalModels[i].gameObject.SetActive(near); if (rivalFar[i] != null) rivalFar[i].gameObject.SetActive(!near); }
            var rm = near ? rivalModels[i] : rivalFar[i];
            // drawn at the same in-between moment as the player's car, or they would shake against it
            double back = (1 - acc / H) * H, cch = System.Math.Cos(c.heading), csh = System.Math.Sin(c.heading);
            rm.Apply(c.x - (c.vx * cch - c.vy * csh) * back, c.y - (c.vx * csh + c.vy * cch) * back, c.heading - c.r * back, c.delta, rivalSpin[i], rivalSpin[i], 0, 0);
            rm.SetBrake(c.brake > 0.05);
            rm.Feed(c, dt);
        }
        if (!Paused && raceLaps > 0 && !playground) Record(dt, rx, ry, rh, rd);
        UpdateSkids();
        if (weatherFx != null)
        {
            weatherFx.Begin(gfxLevel, Paused ? 0f : (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy));
            weatherFx.Spray(model.transform, (float)car.vx);
            for (int i = 0; i < rivals.Count; i++) if (rivalNear[i]) weatherFx.Spray(rivalModels[i].transform, (float)rivals[i].car.vx);
            weatherFx.End();
        }
        AutoQuality();
        audioEngine.rpm = (float)car.rpm; audioEngine.throttle = (float)car.throttle;
        audioEngine.speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        audioEngine.slide = Mathf.Max((float)car.useF, (float)car.useR); audioEngine.onAsphalt = surface != 2; audioEngine.onKerb = car.vib > 0.3;
        audioEngine.carClass = classIdx; audioEngine.wet = Weather.Wetness;
        audioEngine.ersKw = double.IsNaN(car.ersKw) ? 0f : (float)car.ersKw;
        audioEngine.surface = surface != 2 ? surface
            : (car.wType[0] == SurfaceType.Gravel || car.wType[1] == SurfaceType.Gravel || car.wType[2] == SurfaceType.Gravel || car.wType[3] == SurfaceType.Gravel) ? 3 : 2;
        audioEngine.redline = (float)car.cfg.redline; audioEngine.shifting = car.shiftTimer > 0;
        StartAudio();
        audioEngine.wheelspin = car.throttle > 0.05 ? Mathf.Clamp01(((float)car.slipRatioR - 0.14f) / 0.4f) : 0;
        audioEngine.lockup = car.lockR ? 1 : car.brake > 0.05 ? Mathf.Clamp01((-(float)car.slipRatioR - 0.2f) / 0.4f) : 0; // rears; the fronts have their own squeal
        DriverFeel.FeedAudio(audioEngine, car, audioEngine.speed); // slip angles against the limit, front lock-up, plank scrape, slipstream
        if (Paused) { audioEngine.throttle = 0; audioEngine.speed = 0; audioEngine.slide = 0; }
        if (flashT > 0) flashT -= dt;
    }

    // vibration: a wall hit by how much speed it took, otherwise the kerb / rough-surface shake (car.vib)
    void Rumble(double wallDv)
    {
        if (wallDv > 1.5) { Haptics.Pulse(Mathf.Clamp(25 + (int)(wallDv * 5), 30, 140), Mathf.Clamp(90 + (int)(wallDv * 12), 90, 255)); return; }
        double v = System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        double shake = car.vib * System.Math.Min(1, v / 15);
        if (shake > 0.25) Haptics.Pulse(12 + (int)(shake * 22), 50 + (int)(shake * 170));
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
            WheelContact.Update(track, line, car, idx, n.lateral, rubber, marbles);
            Weather.Apply(track, line, car, idx, n.lateral, carSet); // wet grip, after the surface grip (game side only)
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
        car.surfaceMu = s.mu * Weather.PadFactor(surface, carSet); car.surfaceDrag = s.drag;
    }

    // rubber laid down on the circuit this session (0 = green track, 1 = fully rubbered-in): grows with the
    // distance all cars have driven; about 25 car-laps to rubber in fully
    double rubber, marbles, windX, windY;
    // race fuel: about 0.23 kg per km (fits 2026 burn at Monaco, Spa, Monza) for the laps plus a small reserve; 15 kg = qualifying trim
    double RaceFuel() => raceLaps > 0 && track != null ? System.Math.Min(70, 0.23 * track.length / 1000 * (raceLaps + 0.5) + 2) : 15;

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
            if (boxArmed) { boxArmed = false; if (raceLaps > 0 && pLap <= raceLaps) PitStop(); }
        }
        else if (prevIdx < track.N * 0.2 && idx > track.N * 0.8) { lapStart = null; pLap--; }
    }

    // ---------------------------------------------------------------- pit stop (races only)
    // "BOX" arms a stop; on the next crossing of the line the car gets fresh tyres of the next compound and the
    // stop's time loss is added to the player's race time (and taken off the race distance for the order).
    // The car is not stopped on track: rivals would run into it at the line.
    const double PitLoss = 22;
    bool boxArmed; double pitLoss; int pitStops;
    void PitStop()
    {
        carSet = NextSet; var next = Weather.PhysicsCompound(carSet);
        car.cfg.compound = next;
        car.wearF = car.wearR = 0; car.dirtF = car.dirtR = 0;
        car.tyreTF = car.tyreTR = car.cfg.tyreStart;       // straight off the blankets
        car.tyreSF = car.tyreSR = double.NaN;               // the surface temperature re-starts from the carcass
        if (model != null) model.SetStripe(Weather.Stripe(carSet));
        pitLoss += PitLoss; pitStops++;
        Flash($"Pit stop: {Weather.TyreName(carSet)}  +{PitLoss:0}s");
    }
    // the next tyre set: the one for the weather first (a wet race boxes for wets), then round the rest
    int NextSet => Weather.BestSet() != carSet ? Weather.BestSet() : (carSet + 1) % Weather.Sets;
    // a lap time in seconds, to turn the pit time loss into race distance
    double RefLap() => bestLap ?? lastLap ?? (line != null && line.lapTime > 0 ? line.lapTime : 90);

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
    readonly DriverFeel feel = new DriverFeel(); // head / camera motion from the car's forces, steering wheel, shift lights (DriverFeel.cs)
    void LateUpdate()
    {
        var tr = model.transform;
        if (camMode == TvMode || replaying)
        {
            TvCamera(tr, Time.deltaTime);
            CockpitBits(false); // seen from outside: helmet and halo pillar shown, cockpit floor hidden, as in chase
            cam.nearClipPlane = 0.5f;
            return;
        }
        float speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        Vector3 pos, look; float fov;
        float lonG = gLon / 9.81f;
        DriverFeel.Intensity = camShakePct * 0.01f; // the camera-shake setting scales all of the camera's force motion
        feel.Cockpit(model, car, Time.time); // steering wheel by the real front-wheel angle, shift lights
        feel.Step(car, gLon, gLat, speed, Paused ? 0 : Time.deltaTime, camMode == 2 ? 1 : camMode == 3 ? 2 : 0);
        if (camMode == 2 || camMode == 3)
        {
            // 2 = cockpit (driver's eyes, low behind the halo); 3 = halo cam (just above the driver's head)
            bool halo = camMode == 3;
            // settings: the cockpit eye point can be moved up/down and forward/back a few cm (the view angle stays)
            float seatY = seatUpCm * 0.01f, seatZ = seatFwdCm * 0.01f;
            // the head's movement under braking, cornering, kerbs and speed comes from DriverFeel (springs, small amplitudes)
            pos = tr.TransformPoint((halo ? new Vector3(0, 1.02f, 0.12f) : new Vector3(0, 0.76f + seatY, 0.30f + seatZ)) + feel.offset);
            look = tr.TransformPoint(new Vector3(0, halo ? 0.55f : 0.76f + seatY, 30)); // cockpit: 2.8 deg down, fitted to a real F1 onboard frame
            fov = halo ? fovCockpit + 11 : fovCockpit; // default 55 (halo 66): fitted to a real F1 onboard frame (eye, angle and view solved from its halo, tyre and wheel positions); no widening with speed
            cam.transform.position = pos;
        }
        else
        {
            float back = camMode == 0 ? 6.2f : 10f, up = camMode == 0 ? 2f : 3.4f;
            back += Mathf.Clamp(lonG * 0.35f, -0.9f, 0.5f);
            pos = tr.TransformPoint(new Vector3(0, up, -back));
            look = tr.TransformPoint(new Vector3(0, 0.7f, 6));
            fov = fovChase; // a setting (default 62), fixed: widening the view with speed exaggerated how fast the car felt
            if (tvWasOn) { cam.transform.position = pos; camVel = Vector3.zero; } // back from the TV camera: no swoop across the circuit
            cam.transform.position = Vector3.SmoothDamp(cam.transform.position, pos, ref camVel, 0.06f);
        }
        cam.transform.LookAt(look);
        cam.transform.Rotate(feel.pitch, feel.yaw, feel.roll, Space.Self); // nod, look into the corner, head roll and shake
        // horizon lock (a setting): the picture turns against the phone's tilt; in the cockpit only, in every view, or not at all
        if (tilt && (horizonMode == 2 || (horizonMode == 1 && camMode == 2))) cam.transform.Rotate(0, 0, -tiltSmooth * 0.6f, Space.Self);
        cam.fieldOfView += (fov - cam.fieldOfView) * Mathf.Min(1, Time.deltaTime * 4);
        CockpitBits(camMode == 2); tvWasOn = false;
        cam.nearClipPlane = camMode == 2 || camMode == 3 ? 0.05f : 0.3f;
    }
    // helmet and halo pillar are hidden from the driver's eyes; the dark cockpit floor is only shown there
    void CockpitBits(bool cockpit)
    {
        model.helmet.gameObject.SetActive(!cockpit);
        if (model.haloPillar != null) model.haloPillar.SetActive(!cockpit);
        if (model.cockpitFloor) model.cockpitFloor.SetActive(cockpit);
    }

    // ---------------------------------------------------------------- TV camera (camMode 4, and replays)
    // Fixed trackside cameras, placed automatically about every 250 m: 25-40 m beyond the track edge (on the outside
    // of the bend, clear of other parts of the circuit), 4-8 m up. The camera in use is the first one the car has
    // not yet passed (the nearest one ahead, or one it is just passing); 15 m after the car passes it, the picture
    // cuts to the next. The view follows the car with a smoothed look-at and zooms so the car stays the same size.
    readonly List<Vector3> tvPts = new List<Vector3>(); readonly List<int> tvAt = new List<int>(); // camera position, its track point
    int tvFor = -2, tvCur = -1, tvCarIdx = -1; bool tvFree; // tvFree: no circuit (playground): one camera leap-frogs ahead of the car
    Vector3 tvPos, tvLook, tvLookVel, tvPrevCar; float tvFov = 30; int tvLastFrame = -10; bool tvWasOn;

    void BuildTvCams()
    {
        tvPts.Clear(); tvAt.Clear(); tvCur = -1; tvCarIdx = -1; tvFor = playground ? -1 : builtFor;
        if (track == null || playground) return;
        var rnd = new System.Random(builtFor * 131 + 7);
        int count = Mathf.Max(3, Mathf.RoundToInt((float)(track.length / 250))), every = track.N / count, span = Mathf.Max(1, (int)(40 / track.ds));
        double hw = track.width / 2;
        for (int c = 0; c < count; c++)
        {
            int at = c * every; var tp = track.pts[at];
            // curv > 0 = a left-hander, whose outside is to the right (-normal); on straights alternate sides
            double cv = 0; for (int j = -span; j <= span; j++) cv += track.pts[((at + j) % track.N + track.N) % track.N].curv;
            cv /= 2 * span + 1;
            double side = System.Math.Abs(cv) > 0.002 ? (cv > 0 ? -1 : 1) : (c % 2 == 0 ? 1 : -1);
            double off = hw + 25 + rnd.NextDouble() * 15; float up = 4 + (float)rnd.NextDouble() * 4;
            // keep clear of other parts of the circuit (hairpins, parallel straights): other side, then the minimum offset
            bool ok = false; double cx = 0, cy = 0;
            for (int t = 0; t < 4 && !ok; t++)
            {
                double sd = t % 2 == 0 ? side : -side, o = t < 2 ? off : hw + 25;
                cx = tp.x + tp.nx * sd * o; cy = tp.y + tp.ny * sd * o;
                ok = track.Nearest(cx, cy).dist > hw + 20;
            }
            if (!ok) continue;
            tvPts.Add(new Vector3((float)cx, WorldBuilder.H(cx, cy) + up, (float)cy)); tvAt.Add(at);
        }
    }

    void TvCamera(Transform tr, float dt)
    {
        int want = playground ? -1 : builtFor;
        if (tvFor != want) BuildTvCams();
        Vector3 target = tr.position + Vector3.up * 0.5f;
        int prev = tvCur; bool wasFree = tvFree;
        tvFree = tvPts.Count == 0;
        if (!tvFree)
        {
            tvCarIdx = track.Nearest(target.x, target.z, tvCarIdx).idx;
            // the first camera along the track that the car has not passed by more than 15 m
            int best = 0, bestAhead = int.MaxValue, back = Mathf.RoundToInt(15f / (float)track.ds);
            for (int k = 0; k < tvPts.Count; k++)
            {
                int ahead = ((tvAt[k] - (tvCarIdx - back)) % track.N + track.N) % track.N;
                if (ahead < bestAhead) { bestAhead = ahead; best = k; }
            }
            tvCur = best; tvPos = tvPts[tvCur];
        }
        else
        {
            // no circuit: put a camera ahead of the car and to the side; move it on once the car is past or far
            Vector3 fwd = tr.forward; fwd.y = 0; fwd.Normalize();
            if (!wasFree || Vector3.Dot(target - tvPos, fwd) > 15f || (target - tvPos).sqrMagnitude > 200f * 200f)
            {
                Vector3 p = target + fwd * 90f + Vector3.Cross(Vector3.up, fwd) * (tvCur % 2 == 0 ? 30f : -30f);
                p.y = WorldBuilder.H(p.x, p.z) + 5f; tvPos = p; tvCur = tvCur < 0 ? 0 : tvCur + 1;
            }
        }
        bool fresh = Time.frameCount > tvLastFrame + 1; tvLastFrame = Time.frameCount; tvWasOn = true; // just switched to TV
        bool tvCut = fresh || tvCur != prev || tvFree != wasFree;
        // aim a little ahead of the car (by the smoothing lag), so the smoothed view does not trail behind it
        const float Smooth = 0.12f;
        Vector3 vel = dt > 0 && !fresh ? (target - tvPrevCar) / dt : Vector3.zero; tvPrevCar = target;
        if (vel.sqrMagnitude > 120f * 120f) vel = Vector3.zero; // a reset or recover, not motion
        Vector3 aim = target + vel * Smooth;
        if (tvCut) { tvLook = aim; tvLookVel = Vector3.zero; }
        else if (dt > 0) tvLook = Vector3.SmoothDamp(tvLook, aim, ref tvLookVel, Smooth, Mathf.Infinity, dt);
        // zoom: about 9 m of picture height at the car, whatever its distance
        float dist = Mathf.Max(1f, (tvLook - tvPos).magnitude);
        float want2 = Mathf.Clamp(2f * Mathf.Atan(4.5f / dist) * Mathf.Rad2Deg, 3f, 55f);
        tvFov = tvCut ? want2 : tvFov + (want2 - tvFov) * Mathf.Min(1, dt * 3);
        cam.transform.position = tvPos;
        cam.transform.LookAt(tvLook);
        cam.fieldOfView = tvFov;
        camVel = Vector3.zero;
    }

    // ---------------------------------------------------------------- replay: the last 60 s of the race, 30 poses a second
    // A ring buffer of what CarModel.Apply needs (position, heading, steer, wheel turn, body lean) for the player
    // and every rival. "Replay" on the results page plays it back through the TV camera.
    struct RecPose { public float x, y, h, d, sf, sr, lon, lat; public bool brake; }
    const int RecHz = 30, RecCap = RecHz * 60;
    readonly RecPose[] recMe = new RecPose[RecCap]; RecPose[][] recRiv = new RecPose[0][];
    int recHead, recCount; float recAcc, replayT; bool replaying;

    void ClearRecording()
    {
        recHead = recCount = 0; recAcc = 0;
        if (recRiv.Length != rivals.Count) { recRiv = new RecPose[rivals.Count][]; for (int i = 0; i < recRiv.Length; i++) recRiv[i] = new RecPose[RecCap]; }
    }

    void Record(float dt, double x, double y, double h, double d)
    {
        recAcc += dt;
        if (recAcc < 1f / RecHz) return;
        recAcc = Mathf.Min(recAcc - 1f / RecHz, 1f / RecHz); // a long frame records one pose, it does not catch up
        recMe[recHead] = new RecPose { x = (float)x, y = (float)y, h = (float)h, d = (float)d, sf = visF, sr = visR, lon = gLon, lat = gLat, brake = car.brake > 0.05 };
        for (int i = 0; i < rivals.Count && i < recRiv.Length; i++)
        {
            var c = rivals[i].car;
            recRiv[i][recHead] = new RecPose { x = (float)c.x, y = (float)c.y, h = (float)c.heading, d = (float)c.delta, sf = rivalSpin[i], sr = rivalSpin[i], brake = c.brake > 0.05 };
        }
        recHead = (recHead + 1) % RecCap; recCount = Mathf.Min(recCount + 1, RecCap);
    }

    bool CanReplay => recCount > RecHz && !playground;

    void StartReplay()
    {
        if (!CanReplay) return;
        replaying = true; replayT = 0; tvCarIdx = -1; tvCur = -1;
        ui.page = MenuUI.Page.None;
        // replay with the detailed rival models (the zoomed TV view sees them close up)
        for (int i = 0; i < rivals.Count; i++) { rivalNear[i] = true; rivalModels[i].gameObject.SetActive(true); if (rivalFar[i] != null) rivalFar[i].gameObject.SetActive(false); }
        ReplayStep(0);
    }

    void StopReplay() { replaying = false; ui.page = MenuUI.Page.Results; } // the frozen race is drawn again from the cars' own state

    void ReplayStep(float dt)
    {
        replayT += dt;
        float f = replayT * RecHz; int i0 = (int)f;
        if (i0 >= recCount - 1) { StopReplay(); return; }
        float a = f - i0;
        int k0 = (recHead - recCount + i0 + 2 * RecCap) % RecCap, k1 = (k0 + 1) % RecCap;
        void Show(CarModel m, RecPose p, RecPose q)
        {
            double dh = System.Math.Atan2(System.Math.Sin(q.h - p.h), System.Math.Cos(q.h - p.h));
            m.Apply(p.x + (q.x - p.x) * a, p.y + (q.y - p.y) * a, p.h + dh * a, p.d + (q.d - p.d) * a, p.sf, p.sr, p.lon + (q.lon - p.lon) * a, p.lat + (q.lat - p.lat) * a);
            m.SetBrake(p.brake);
        }
        Show(model, recMe[k0], recMe[k1]);
        for (int i = 0; i < rivals.Count && i < recRiv.Length; i++) Show(rivalModels[i], recRiv[i][k0], recRiv[i][k1]);
    }

    // ---------------------------------------------------------------- front end (MenuUI.cs): start menu, loading, results
    MenuUI ui; int raceLaps; int lineFor = -1; // raceLaps 0 = free practice (no rivals, no finish)
    bool Paused => menuOpen || (ui != null && ui.Showing);

    // called once the menu's "Loading…" frame is on screen: build the chosen circuit and start the session
    void StartSession()
    {
        raceLaps = ui.practice ? 0 : MenuUI.LapCounts[ui.lapsIdx];
        if (lineFor != classIdx && world != null) { Destroy(world); world = null; } // new class: rebuild with its racing line
        LoadMap(); ApplyWeather(); // the menu may have changed the weather
        menuOpen = false; ApplyScreenLock();
        ResetCar(); Save();
        acc = 0; ui.page = MenuUI.Page.None;
    }

    // the player has done raceLaps: freeze the race and rank every car by distance covered
    void FinishRace()
    {
        var rows = new MenuUI.Row[rivals.Count + 1];
        rows[0] = new MenuUI.Row { name = "You", colour = new Color(1f, 0.25f, 0.2f), player = true, progress = pLap + (double)System.Math.Max(idx, 0) / track.N - pitLoss / RefLap(), best = bestLap ?? double.NaN };
        for (int i = 0; i < rivals.Count; i++)
        {
            var w = rivals[i].who;
            rows[i + 1] = new MenuUI.Row { name = w.name, colour = new Color(w.r, w.g, w.b), progress = rivals[i].progress, best = rivals[i].bestLap };
        }
        double refLap = RefLap(); // turns lap fractions into seconds
        ui.ShowResults(rows, MapNames[mapIdx], raceLaps, raceTime - (GridStart ? Countdown : 0) + pitLoss, refLap, pitStops, jumped ? JumpPenalty : 0);
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
        if (menuOpen && !replaying) { DrawSettings(); return; } // the settings screen covers everything (race paused, or over the start menu)
        if (ui.Showing)
        {
            ui.canReplay = CanReplay;
            var choice = ui.Draw(ref mapIdx, ref classIdx, ref rivalIdx, ref diffIdx, track, builtFor);
            if (choice == MenuUI.Choice.RaceAgain) { ResetCar(); ui.page = MenuUI.Page.None; }
            else if (choice == MenuUI.Choice.Replay) StartReplay();
            else if (choice == MenuUI.Choice.Settings) menuOpen = true;
            return;
        }
        if (replaying)
        {
            // replay: a small caption and a way out, nothing else on screen
            float total = (recCount - 1) / (float)RecHz;
            center.fontSize = (int)S(13);
            Panel(new Rect(S(8), S(8), S(170), S(30)));
            GUI.Label(new Rect(S(8), S(8), S(170), S(30)), $"<b>REPLAY</b>  {Mathf.Max(0, total - replayT):0}s", center);
            if (GUI.Button(new Rect(Screen.width - S(8) - S(110), S(8), S(110), S(30)), "End replay", btn)) StopReplay();
            return;
        }
        float speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);

        // top-right: Settings, Camera, Restart. Everything else lives in the settings screen.
        float bh = S(30), bw = S(74), x0 = Screen.width - S(8);
        if (GUI.Button(new Rect(x0 - 3 * (bw + S(4)), S(8), bw, bh), "Settings", btn)) menuOpen = true;
        if (GUI.Button(new Rect(x0 - 2 * (bw + S(4)), S(8), bw, bh), "Cam", btn)) { camMode = (camMode + 1) % CamNames.Length; Flash(CamNames[camMode]); Save(); }
        if (GUI.Button(new Rect(x0 - 1 * (bw + S(4)), S(8), bw, bh), "Restart", btn)) { ResetCar(); menuOpen = false; }
        // pit stop: arm it during a race (not on the last lap); it happens when the car next crosses the line
        if (!menuOpen && !playground && raceLaps > 0 && pLap < raceLaps)
        {
            var boxR = new Rect(Screen.width - S(8) - bw, S(162), bw, bh);
            if (GUI.Button(boxR, boxArmed ? "BOXING" : "BOX", btn)) { boxArmed = !boxArmed; Flash(boxArmed ? $"Box this lap: {Weather.TyreName(NextSet)}" : "Box cancelled"); }
        }
        if (!menuOpen && !playground && (surface == 2 || hitT > 0) && GUI.Button(new Rect(Screen.width / 2 - S(60), Screen.height * 0.5f, S(120), bh), "Back on track", btn)) Recover();
        // top-centre: lap time or test data
        string top, sub;
        if (playground) { top = "TEST DATA (ours vs real F1)"; sub = $"0-100 {(t100.HasValue ? t100.Value.ToString("0.00") + "s" : "—")} (real 2.2-2.6)  ·  top {topSpeed * (unitsIdx == 1 ? 0.621371 : 1):0} {SpeedUnit}"; }
        else { top = lapStart.HasValue ? Fmt(simTime - lapStart.Value) : "--:--.---"; sub = (rivals.Count > 0 ? $"P{Position()}/{rivals.Count + 1}  ·  " : "") + (raceLaps > 0 ? $"Lap {Mathf.Clamp(pLap, 1, raceLaps)}/{raceLaps}" : $"Lap {lapNo}") + $"  ·  Last {Fmt(lastLap)}  ·  Best {Fmt(bestLap)}"; }
        float py = S(44);
        Panel(new Rect(Screen.width / 2 - S(150), py, S(300), S(44)));
        center.fontSize = (int)S(18); GUI.Label(new Rect(Screen.width / 2 - S(150), py, S(300), S(26)), top, center);
        center.fontSize = (int)S(11); GUI.Label(new Rect(Screen.width / 2 - S(150), py + S(22), S(300), S(20)), sub, center);

        // dash
        var dash = new Rect(Screen.width / 2 - S(95), Screen.height - S(80), S(190), S(70));
        Panel(dash);
        GUI.Label(new Rect(dash.x, dash.y + S(2), dash.width * 0.72f, S(40)), $"{speed * (unitsIdx == 1 ? 2.23694f : 3.6f):0}<size={(int)S(12)}> {SpeedUnit}</size>", big);
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
        string fpsText = showFps ? $"{fpsAvg:0} fps" + (renderScale < 0.99f ? $" ({renderScale * 100:0}% res)" : "") : "";
        if (!hudInfo) { if (showFps) { Panel(new Rect(S(8), S(8), S(110), S(20))); GUI.Label(new Rect(S(14), S(10), S(110), S(18)), fpsText, small); } }
        else
        {
        Panel(new Rect(S(8), S(8), S(178), tilt ? S(190) : S(174)));
        GUI.Label(new Rect(S(14), S(10), S(178), S(190)),
            (showFps ? fpsText + "\n" : "") + $"Front grip {car.useF * 100:0}%\nRear grip {car.useR * 100:0}%\nRear spin {car.slipRatioR * 100:0}%\nSlide {beta:0.0}°\nG {System.Math.Sqrt(car.ax * car.ax + car.ay * car.ay) / 9.81:0.0}   Brake bias {car.cfg.brakeBias * 100:0}%" +
            (car.cfg.chassis && !double.IsNaN(car.tyreTF) ? $"\n{Weather.TyreName(carSet)} {car.tyreTF:0}° / {car.tyreTR:0}°, worn {car.wearF * 100:0} / {car.wearR * 100:0}%\nBrakes {car.brakeTF:0}° / {car.brakeTR:0}°" + (double.IsNaN(car.fuel) ? "" : $"\nFuel {car.fuel:0.0} kg ({car.fuelLastLap:0.00}/lap)  Tyres {car.tyrePF:0.0}/{car.tyrePR:0.0} psi") + $"\nRide {car.rideF * 1000:0} / {car.rideR * 1000:0} mm\nAero {car.downforce / 9.81:0} kg, {car.aeroBalance * 100:0}% front" + (car.tow > 0.05 ? $"\nSlipstream {car.tow * 100:0}%" : "") : "") +
            (tilt ? $"\nTilt {(tiltSmooth < -0.5f ? "L" : tiltSmooth > 0.5f ? "R" : "")} {Mathf.Abs(tiltSmooth):0}° → steer {Mathf.Abs(TiltToSteer()) * 100:0}%" : ""), small);
        }

        if (hudMap) DrawMiniMap();
        DrawStartLights(); // the five red lights (GameManagerStart.cs)

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
