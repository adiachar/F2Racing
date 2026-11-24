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
    double frontSpin, px, py, ph, pd, psi, renderDelta, renderSteer;
    readonly SteerFilter steerF = new SteerFilter(); bool steerReset = true; // smooth steering for the physics steps (SteerFilter.cs)

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
    double? lapStart, lastLap, bestLap; bool cut, lastValid = true; int lapNo = 1;
    double lineX, lineY, lineT = double.NaN; int lineIdx = -1; // the last timing-line check (LineCheck, every physics step)
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
        UnityEngine.Physics.simulationMode = SimulationMode.Script; // the game has its own physics: no PhysX step per FixedUpdate (no PhysX queries are used)
        QualitySettings.vSyncCount = 0; QualitySettings.shadowProjection = ShadowProjection.StableFit;

        tc = (TcMode)PlayerPrefs.GetInt("tc", (int)TcMode.Low);
        tilt = PlayerPrefs.GetInt("tilt", 0) == 1; invertTilt = PlayerPrefs.GetInt("tiltInv", 0) == 1;
        mapIdx = Mathf.Clamp(PlayerPrefs.GetInt("map", 0), 0, MapNames.Length - 1); playground = mapIdx == PlaygroundIdx; camMode = Mathf.Clamp(PlayerPrefs.GetInt("cam", 0), 0, CamNames.Length - 1); sound = PlayerPrefs.GetInt("snd", 1) == 1; lineMode = Mathf.Clamp(PlayerPrefs.GetInt("lineMode", RacingLineView.Full), 0, 2); absOn = PlayerPrefs.GetInt("abs", 1) == 1; classIdx = Mathf.Clamp(PlayerPrefs.GetInt("class", 0), 0, CarConfig.ClassNames.Length - 1); compIdx = Mathf.Clamp(PlayerPrefs.GetInt("tyres", 0), 0, CompoundTable.Defs.Length - 1); gfxIdx = Mathf.Clamp(PlayerPrefs.GetInt("gfx", 0), 0, GfxNames.Length - 1);
        LoadSettings(); // everything the settings screen added (GameManagerSettings.cs)
        Weather.Load(); // before the world is built: the sky and fog are made for it
        tyreSet = Mathf.Clamp(PlayerPrefs.GetInt("tyreSet", compIdx), 0, Weather.Sets - 1); carSet = tyreSet;
        smoothIdx = Mathf.Clamp(PlayerPrefs.GetInt("tiltSmooth", 1), 0, SmoothCut.Length - 1); speedSteerIdx = Mathf.Clamp(PlayerPrefs.GetInt("speedSteer", 2), 0, SpeedSteer.Length - 1);
        ApplyScreenLock();
        Haptics.enabled = PlayerPrefs.GetInt("haptics", 1) == 1;
        liveryIdx = Livery.PlayerIndex;
        rivalIdx = Mathf.Clamp(PlayerPrefs.GetInt("rivals", 1), 0, RivalCounts.Length - 1); diffIdx = Mathf.Clamp(PlayerPrefs.GetInt("diff", 0), 0, DiffNames.Length - 1);
        SyncDifficulty(); // a difficulty preset onto the settings, the XP multiplier (CareerSession.cs)
        ui = new MenuUI(MapNames, PlaygroundIdx, RivalCounts, DiffNames); // the start menu is up (game paused) until a session starts
        ShotsBoot(); // WebGL ?shot=1 only: automatic screenshots (DebugShots.cs); does nothing in normal play
        FrameLog.Ensure(); // Settings > GRAPHICS > Performance graph (debug): only made when that is on

        LoadMap();
        car = new FormulaCar(PlayerConfig());
        model = MakePlayerCar(WantFullCar(gfxIdx == 0 ? 2 : gfxIdx)) ?? CarModel.CreateFromAsset() ?? CarModel.Create(car.cfg.wheelbase, car.cfg.frontWeight, car.cfg.wheelRadius);
        model.SetLivery(Livery.Player[liveryIdx]);
        var camGo = new GameObject("Camera"); cam = camGo.AddComponent<Camera>(); camGo.tag = "MainCamera";
        // dynamic resolution only while the picture is actually scaled (SetRenderScale): at 100% the camera draws straight
        // into the screen buffer, with no scaled intermediate target and upscale pass
        cam.allowDynamicResolution = false;
        // no HDR buffer: every world, car and sky shader applies the colour grade and tone curve itself (Grade.cginc) and
        // writes the final 0..1 colour, and there is no post-processing. With HDR on, the built-in pipeline drew the whole
        // frame into an off-screen FP16 4x MSAA target (twice the tile memory per pixel on Mali: smaller tiles), resolved it
        // and copied it to the screen every frame. The picture is the same; additive glare and flares clamp at 1 either way.
        cam.allowHDR = false;
        cam.allowMSAA = true; // the built-in pipeline takes its MSAA from QualitySettings.antiAliasing (ApplyGraphics)
        cam.nearClipPlane = ChaseNear; cam.farClipPlane = 2500; cam.fieldOfView = 62;
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
        if (circuit != builtFor || world == null || WorldBuilder.Stale) // Stale: the graphics level changed since the last build
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

    // Local mini-map (the panel under the race card, RaceHud.cs: mapRect): only the track round the car, MmAhead m ahead
    // and MmBehind m behind, turned so the car's heading (filtered: no jitter) points up, the car low in the middle.
    // Built once per circuit: the centre line every ~4 m with its normal and half width; the pit lane and grey blocks
    // for the big buildings beside the track (grandstands, pit building: Trackside.Solids) as soon as they exist.
    // Drawn with GL triangles clipped to the panel on the CPU (RaceHud.cs: GlBegin); nothing is allocated per frame.
    const float MmAhead = 300f, MmBehind = 150f;
    float[] mmX, mmY, mmNx, mmNy, mmHw, mmLx, mmLy, mmRx, mmRy; bool[] mmVis; int mmN; Track mmFor;
    float[] mmPitX, mmPitY; int mmPitN; PitLane mmLane;
    float[] mmBlk; int mmBlkN, mmSolidsSeen = -1;
    float mmHx = float.NaN, mmHy, mmCx, mmCy;            // filtered heading (unit vector), the car's last place
    Color[] mmCol = new Color[0]; AiDriver[] mmOwner = new AiDriver[0];
    void BuildMiniMap()
    {
        int N = track.N, step = Mathf.Max(1, Mathf.RoundToInt(4f / (float)track.ds));
        mmN = N / step;
        mmX = new float[mmN]; mmY = new float[mmN]; mmNx = new float[mmN]; mmNy = new float[mmN]; mmHw = new float[mmN];
        mmLx = new float[mmN]; mmLy = new float[mmN]; mmRx = new float[mmN]; mmRy = new float[mmN]; mmVis = new bool[mmN];
        for (int k = 0; k < mmN; k++)
        {
            var p = track.pts[k * step];
            mmX[k] = (float)p.x; mmY[k] = (float)p.y; mmNx[k] = (float)p.nx; mmNy[k] = (float)p.ny; mmHw[k] = (float)track.HalfWidth(k * step);
        }
        mmFor = track; mmLane = null; mmPitN = 0; mmSolidsSeen = -1; mmBlkN = 0; mmHx = float.NaN;
    }
    // the pit lane's fast lane, entry to exit, every 4 m
    void BuildMmPit()
    {
        mmLane = Trackside.Lane; mmPitN = 0;
        if (mmLane == null || mmLane.track != track) return;
        int n = (int)((mmLane.sExit - mmLane.sEntry) / 4) + 1;
        if (mmPitX == null || mmPitX.Length < n) { mmPitX = new float[n]; mmPitY = new float[n]; }
        for (int i = 0; i < n; i++)
        {
            double u = mmLane.sEntry + i * 4.0;
            mmLane.Pos(u, mmLane.BaseLat(u), out double x, out double y);
            mmPitX[i] = (float)x; mmPitY[i] = (float)y;
        }
        mmPitN = n;
    }
    // big boxes beside the track (stands, pit garages, buildings): their four corners (world x / z = physics x / y)
    void BuildMmBlocks()
    {
        var sol = Trackside.Solids; mmSolidsSeen = sol.Count; mmBlkN = 0;
        const int Max = 192;
        if (mmBlk == null) mmBlk = new float[Max * 8];
        for (int i = 0; i < sol.Count && mmBlkN < Max; i++)
        {
            var b = sol[i];
            if (b.hl * b.hd < 20f || Mathf.Max(b.hl, b.hd) < 4f || Mathf.Min(b.hl, b.hd) < 2f) continue; // posts, boards, canopies, bridges
            float tx = b.tx * b.hl, tz = b.tz * b.hl, nx = -b.tz * b.hd, nz = b.tx * b.hd;
            int o = mmBlkN * 8;
            mmBlk[o] = b.cx + tx + nx; mmBlk[o + 1] = b.cz + tz + nz; mmBlk[o + 2] = b.cx - tx + nx; mmBlk[o + 3] = b.cz - tz + nz;
            mmBlk[o + 4] = b.cx - tx - nx; mmBlk[o + 5] = b.cz - tz - nz; mmBlk[o + 6] = b.cx + tx - nx; mmBlk[o + 7] = b.cz + tz - nz;
            mmBlkN++;
        }
    }
    void DrawMiniMap()
    {
        if (playground || track == null || car == null) return;
        if (mmFor != track || mmX == null) BuildMiniMap();
        var m = mapRect;                                     // no backdrop (Adi): just the track, under the race card, top right (RaceHud.cs: HudLayout)
        if (Event.current.type != EventType.Repaint) return;
        if (mmLane != Trackside.Lane) BuildMmPit();
        if (mmSolidsSeen != Trackside.Solids.Count) BuildMmBlocks();
        if (mmOwner.Length != rivals.Count) { mmOwner = new AiDriver[rivals.Count]; mmCol = new Color[rivals.Count]; }
        for (int i = 0; i < rivals.Count; i++)
            if (mmOwner[i] != rivals[i]) { mmOwner[i] = rivals[i]; mmCol[i] = Livery.TeamColour(TeamIndex(rivals[i].who)); }

        // heading: a unit vector eased towards the car's (frame-rate independent), snapped after a reset or a jump
        float ch = Mathf.Cos((float)car.heading), sh = Mathf.Sin((float)car.heading), px = (float)car.x, py = (float)car.y;
        if (float.IsNaN(mmHx) || (px - mmCx) * (px - mmCx) + (py - mmCy) * (py - mmCy) > 2500f) { mmHx = ch; mmHy = sh; }
        else
        {
            float e = 1f - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime, 0.1f) * 4f);
            float hx = mmHx + (ch - mmHx) * e, hy = mmHy + (sh - mmHy) * e, hl = Mathf.Sqrt(hx * hx + hy * hy);
            if (hl > 1e-3f) { mmHx = hx / hl; mmHy = hy / hl; } else { mmHx = ch; mmHy = sh; }
        }
        mmCx = px; mmCy = py;

        float pad = S(5);
        var inner = new Rect(m.x + pad, m.y + pad, m.width - 2 * pad, m.height - 2 * pad);
        float k = inner.height / (MmAhead + MmBehind), ox = inner.center.x, oy = inner.yMax - MmBehind * k;
        float fx = mmHx, fy = mmHy;
        // world -> panel: right of the car = +X, ahead = -Y
        // X = ox + ((x - px) fy - (y - py) fx) k, Y = oy - ((x - px) fx + (y - py) fy) k
        float qx = px + fx * (MmAhead - MmBehind) * 0.5f, qy = py + fy * (MmAhead - MmBehind) * 0.5f;
        float halfW = inner.width * 0.5f / k, halfL = (MmAhead + MmBehind) * 0.5f;
        float R = Mathf.Sqrt(halfW * halfW + halfL * halfL) + 25f, R2 = R * R;
        if (!GlBegin(inner)) return;

        // buildings
        GlColor(new Color(0.55f, 0.58f, 0.64f, 0.38f));
        for (int b = 0; b < mmBlkN; b++)
        {
            int o = b * 8; float cx = (mmBlk[o] + mmBlk[o + 4]) * 0.5f - qx, cy = (mmBlk[o + 1] + mmBlk[o + 5]) * 0.5f - qy;
            if (cx * cx + cy * cy > R2) continue;
            float ax = mmBlk[o] - px, ay = mmBlk[o + 1] - py, bx = mmBlk[o + 2] - px, by = mmBlk[o + 3] - py;
            float cx2 = mmBlk[o + 4] - px, cy2 = mmBlk[o + 5] - py, dx = mmBlk[o + 6] - px, dy = mmBlk[o + 7] - py;
            GlQuad(ox + (ax * fy - ay * fx) * k, oy - (ax * fx + ay * fy) * k, ox + (bx * fy - by * fx) * k, oy - (bx * fx + by * fy) * k,
                   ox + (cx2 * fy - cy2 * fx) * k, oy - (cx2 * fx + cy2 * fy) * k, ox + (dx * fy - dy * fx) * k, oy - (dx * fx + dy * fy) * k);
        }
        // pit lane
        GlColor(new Color(0.75f, 0.78f, 0.84f, 0.55f));
        float wPit = Mathf.Max(1f, S(1.1f));
        for (int i = 0; i + 1 < mmPitN; i++)
        {
            float ax = mmPitX[i] - qx, ay = mmPitY[i] - qy; if (ax * ax + ay * ay > R2) continue;
            ax = mmPitX[i] - px; ay = mmPitY[i] - py; float bx = mmPitX[i + 1] - px, by = mmPitY[i + 1] - py;
            GlLine(ox + (ax * fy - ay * fx) * k, oy - (ax * fx + ay * fy) * k, ox + (bx * fy - by * fx) * k, oy - (bx * fx + by * fy) * k, wPit);
        }
        // track: the edges of every point in range (at least ~4.4 units wide, so it reads on a phone)
        float minHw = S(2.2f) / k;
        for (int i = 0; i < mmN; i++)
        {
            float dx = mmX[i] - qx, dy = mmY[i] - qy;
            bool v = dx * dx + dy * dy < R2; mmVis[i] = v; if (!v) continue;
            float hw = Mathf.Max(mmHw[i], minHw);
            float lx = mmX[i] + mmNx[i] * hw - px, ly = mmY[i] + mmNy[i] * hw - py, rx = mmX[i] - mmNx[i] * hw - px, ry = mmY[i] - mmNy[i] * hw - py;
            mmLx[i] = ox + (lx * fy - ly * fx) * k; mmLy[i] = oy - (lx * fx + ly * fy) * k;
            mmRx[i] = ox + (rx * fy - ry * fx) * k; mmRy[i] = oy - (rx * fx + ry * fy) * k;
        }
        GlColor(new Color(0.20f, 0.22f, 0.27f, 0.92f));
        for (int i = 0; i < mmN; i++)
        {
            int j = i + 1 == mmN ? 0 : i + 1; if (!mmVis[i] || !mmVis[j]) continue;
            GlQuad(mmLx[i], mmLy[i], mmLx[j], mmLy[j], mmRx[j], mmRy[j], mmRx[i], mmRy[i]);
        }
        GlColor(new Color(0.92f, 0.94f, 0.97f, 0.85f));
        float wEdge = Mathf.Max(1f, S(0.8f));
        for (int i = 0; i < mmN; i++)
        {
            int j = i + 1 == mmN ? 0 : i + 1; if (!mmVis[i] || !mmVis[j]) continue;
            GlLine(mmLx[i], mmLy[i], mmLx[j], mmLy[j], wEdge);
            GlLine(mmRx[i], mmRy[i], mmRx[j], mmRy[j], wEdge);
        }
        if (mmVis[0]) { GlColor(Color.white); GlLine(mmLx[0], mmLy[0], mmRx[0], mmRy[0], Mathf.Max(1.5f, S(1.4f))); } // start / finish

        // rivals in team colours; the cars just ahead and behind in the running order get a white ring
        int ahead = -1, behind = -1;
        for (int r = 0; r < ordN; r++) if (ord[r] == 0) { if (r > 0) ahead = ord[r - 1] - 1; if (r + 1 < ordN) behind = ord[r + 1] - 1; break; }
        float rd = S(2.7f), rr = S(4.1f);
        for (int i = 0; i < rivals.Count; i++)
        {
            var c = rivals[i].car; float dx = (float)c.x - px, dy = (float)c.y - py;
            float sx = ox + (dx * fy - dy * fx) * k, sy = oy - (dx * fx + dy * fy) * k;
            if (sx < inner.xMin - rr || sx > inner.xMax + rr || sy < inner.yMin - rr || sy > inner.yMax + rr) continue;
            if (i == ahead || i == behind) { GlColor(i == ahead ? Color.white : new Color(1f, 1f, 1f, 0.7f)); GlDisc(sx, sy, rr); }
            GlColor(new Color(0.03f, 0.035f, 0.05f, 0.9f)); GlDisc(sx, sy, rd + S(0.6f));
            GlColor(mmCol[i]); GlDisc(sx, sy, rd);
        }
        // the player: an arrow pointing up, white outline, racing red
        float a = S(5.2f);
        GlColor(Color.white);
        GlTri(ox, oy - a * 1.25f, ox - a * 0.95f, oy + a, ox, oy + a * 0.45f); GlTri(ox, oy - a * 1.25f, ox, oy + a * 0.45f, ox + a * 0.95f, oy + a);
        a *= 0.62f;
        GlColor(new Color(1f, 0.16f, 0.1f));
        GlTri(ox, oy - a * 1.25f, ox - a * 0.95f, oy + a, ox, oy + a * 0.45f); GlTri(ox, oy - a * 1.25f, ox, oy + a * 0.45f, ox + a * 0.95f, oy + a);
        GlEnd();
    }

    // Graphics level (a setting). Auto starts at Medium and steps down if the phone cannot hold about 50 fps.
    static readonly string[] GfxNames = { "Auto", "Low", "Medium", "High" }; int gfxIdx; int gfxLevel = 2;
    float fpsAvg = 60;
    bool autoNoShadows; // Auto switched the live shadows off (a step before Low; any graphics setting change clears it)
    void ApplyGraphics(int level) { autoNoShadows = false; SetGraphics(level); }
    void SetGraphics(int level)
    {
        gfxLevel = level;
        // Anti-aliasing. This project uses the built-in render pipeline, where QualitySettings.antiAliasing IS the
        // camera's MSAA (BuildScript also puts 4x into every quality level, so the screen buffer starts with it).
        // MSAA is resolved on-chip on phone (tile) GPUs, so it costs little: 4x on Medium/High, 2x on Low. It smooths
        // the track edges, barriers and thin poles that otherwise step and flicker. Auto never turns it off.
        QualitySettings.antiAliasing = MsaaFor(level);
        if (cam != null) cam.allowMSAA = true;
        QualitySettings.shadows = level <= 1 || LightBake.Active || !shadowsOn || autoNoShadows ? ShadowQuality.Disable : ShadowQuality.All; // baked lighting needs no shadow maps
        QualitySettings.shadowCascades = level >= 3 ? 2 : 1;
        QualitySettings.shadowResolution = level >= 3 ? ShadowResolution.High : ShadowResolution.Medium;
        QualitySettings.shadowDistance = level >= 3 ? 90 : 55;
        // anisotropic filtering keeps the road and verges sharp at a low angle. Other files read "Disable" as "Low".
        // Medium forces at least 4x, High 8x; a texture that asks for more (its own anisoLevel) gets it, up to 16x.
        QualitySettings.anisotropicFiltering = level <= 1 ? AnisotropicFiltering.Disable : AnisotropicFiltering.Enable; // per texture: road/ground/kerbs ask for 4-8x themselves; forcing it on trees and people cost 1-3 ms
        Texture.SetGlobalAnisotropicFilteringLimits(level >= 3 ? 8 : 4, 16);
        QualitySettings.globalTextureMipmapLimit = 0; // full-size textures on every level (some default levels halve them)
        if (cam != null) cam.farClipPlane = level <= 1 ? 1500 : 2500;
        ApplyPerfLevel(level); // per-level cuts: car shader detail, wheel faces, car effects, glare, grade (GameManagerPerf.cs)
    }
    // Frame rate, then the graphics level, then (last) the resolution: FramePacing in GameManagerPerf.cs. The frame rate is
    // always a steady rate the screen divides evenly (60 / 40 / 30 on 120 Hz); every step is one-way within a session.
    void AutoQuality() => FramePacing();

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

    // racing-line guide on the road (RacingLineView.cs): lineMode 0 off, 1 braking only, 2 full (Settings > Assists)
    RacingLine line; GameObject lineGo; RacingLineView lineView; int lineMode = RacingLineView.Full;
    bool showLine { get => lineMode != RacingLineView.Off; set => lineMode = value ? (lineMode != RacingLineView.Off ? lineMode : RacingLineView.Full) : RacingLineView.Off; }
    void BuildLine()
    {
        if (lineView != null) lineView.Destroy(); else if (lineGo != null) Destroy(lineGo);
        var lc = CarConfig.ForClass(classIdx); lc.aeroFront = aeroBal / 100.0;
        line = RacingLine.Compute(track, lc); lineFor = classIdx;
        lineView = RacingLineView.Build(track, line, world.transform); lineGo = lineView != null ? lineView.go : null; // draped itself (vertex heights)
        var rubber = Scenery.Rubber(track, line, world.transform);
        var boards = Scenery.Boards(track, line, world.transform, (float)Traffic.WallOffset(track));
        // these are built after the world, so lay them on the hills here
        WorldBuilder.Drape(rubber); if (boards != null) { WorldBuilder.Drape(boards); LightBake.Apply(boards); }
        if (lineGo != null) lineGo.SetActive(showLine && !playground);
    }
    // once a frame, after the physics: the guide's window and colours (hidden in the pit lane and on the results screen)
    void LineFrame()
    {
        if (lineView == null || lineGo == null || !lineGo.activeSelf) return;
        bool on = !playground && pRun == null && (ui == null || !ui.Showing);
        int li = idx;
        if (li < 0 && shotFreeze && track != null) li = track.Nearest(car.x, car.y).idx; // screenshots: no physics step sets idx (colours for the shot's kmh=)
        lineView.Frame(on ? lineMode : RacingLineView.Off, li, car.x, car.y, System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy), Weather.TrackFactor(carSet, 1));
    }

    void Save()
    {
        PlayerPrefs.SetInt("tc", (int)tc); PlayerPrefs.SetInt("tilt", tilt ? 1 : 0); PlayerPrefs.SetInt("tiltInv", invertTilt ? 1 : 0);
        PlayerPrefs.SetInt("map", mapIdx); PlayerPrefs.SetInt("cam", camMode); PlayerPrefs.SetInt("snd", sound ? 1 : 0); PlayerPrefs.SetInt("lineMode", lineMode); PlayerPrefs.SetInt("abs", absOn ? 1 : 0); PlayerPrefs.SetInt("class", classIdx); PlayerPrefs.SetInt("tyres", compIdx); PlayerPrefs.SetInt("gfx", gfxIdx); PlayerPrefs.SetInt("tiltSmooth", smoothIdx); PlayerPrefs.SetInt("speedSteer", speedSteerIdx); PlayerPrefs.SetInt("rivals", rivalIdx); PlayerPrefs.SetInt("diff", diffIdx); PlayerPrefs.SetInt("haptics", Haptics.enabled ? 1 : 0); Livery.PlayerIndex = liveryIdx;
        PlayerPrefs.SetInt("tyreSet", tyreSet); Weather.Save();
        SaveSettings(); // also writes the old "bal", "tiltDz", "tiltRange" and "brakeBias" keys
        PlayerPrefs.Save();
    }

    void ResetCar()
    {
        car = new FormulaCar(PlayerConfig());
        car.windX = windX; car.windY = windY;
        steerReset = true; // a new car starts from the driver's hands, not from the last session's steering
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
        idx = prevIdx = -1; lineIdx = -1; lineT = double.NaN; lastValid = true; lapStart = null; cut = false; offTime = wrongTime = 0; t100 = null; launchT = -1; topSpeed = 0;
        Flash(MapNames[mapIdx]);
        StartReset(); // lights, launch, jump-start check (GameManagerStart.cs)
        ProgressBegin(); // safety rating, XP and race-rule tally of a race (GameManagerProgress.cs)
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
        if (shotCars >= 0 && !playground && line != null && raceLaps > 0) count = shotCars; // screenshot mode (DebugShots.cs)
        var field = SessionField(ref count); // weekend / championship / qualifying field in grid order, null = quick race (Qualifying.cs)
        var grid = DriverProfile.Grid;
        rivalSet = Weather.BestSet(); // the rivals start on the right tyres for the weather
        ChooseGridSlot(count + 1);
        for (int k = 0; k < count; k++)
        {
            // spread the picks over the whole field: stars, midfield and backmarkers
            var who = field != null ? field[k] : grid[count == 1 ? 0 : Mathf.RoundToInt(k * (grid.Length - 1) / (float)(count - 1))];
            var rc = CarConfig.ForClass(classIdx); rc.fuelStart = RaceFuel();
            rc.compound = Weather.PhysicsCompound(rivalSet); rc.damage = DamageNow == 2; // a career round always has damage (CareerSession.cs)
            TyreRulesFor(rc, rivalSet); // the session's tyre wear and temperature, as the player's (TyrePit.cs)
            // the speed profile's grip share multiplies the tyre mu directly (RacingLine.SpeedProfile), so the wet
            // factor of their tyres on the racing line slows corners and braking just as the grip loss does
            var tune = AiTuning.For(builtFor); tune.grip *= Weather.TrackFactor(rivalSet, 1);
            var d = new AiDriver(track, line, rc, who, tune, DiffScale[diffIdx], k + 1);
            RcPlan(d, k); // the rival's tyre strategy and start set (GameManagerRaceControl.cs)
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
        RcSetup(); // flags and safety car, rival tyre stripes (GameManagerRaceControl.cs)
        PitCrewSetup(); // the pit crew of this circuit (PitCrew.cs)
        AfterSetupRivals(); // qualifying: the show cars spread round the lap (Qualifying.cs)
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
        if (form != null) { FormRivals(); return; } // the formation lap: its own pace, the grid order, the slots (GameManagerFormation.cs)
        if (raceTime >= Countdown && aiTick++ % 4 == 0)
        {
            UpdateTow();
            OvertakeTick(); // Overtake Mode detection points (RaceHud.cs, FormulaSim.OvertakeRule)
            // rubber build-up from every car's distance
            double dist = System.Math.Abs(car.vx) * 4 * H; foreach (var r in rivals) dist += System.Math.Abs(r.car.vx) * 4 * H;
            rubber = System.Math.Min(1, rubber + dist / (track.length * 25));
            marbles = System.Math.Min(1, marbles + dist / (track.length * 400));
            for (int i = 0; i < rivals.Count; i++)
            {
                rivals[i].Think(4 * H, simTime, rivals, PsAiSees(), idx, playerLat); // PsAiSees: the player's car, or null while he is a penalty ghost (PenaltyServe.cs)
                if (rivals[i].InPit) continue; // driven along the pit lane (PitStep below)
                var rc = rivals[i].car; rc.grade = WorldBuilder.Height != null ? WorldBuilder.Height.GradeAlong(rc.x, rc.y, rc.heading, rivals[i].idx) : 0;
                if (rivals[i].idx >= 0) Traffic.Wall(rivals[i].car, track.pts[rivals[i].idx], rivals[i].lateral, wallAt);
            }
            RcStep(); // race control sees every car (GameManagerRaceControl.cs)
            PickRivalRates(); // physics level of detail for far rivals (below)
            for (int i = 0; i < rivals.Count; i++)
            {
                if (rivals[i].InPit) continue; // cars in the pit lane touch nobody
                for (int j = i + 1; j < rivals.Count; j++) if (!rivals[j].InPit) RivalContact(i, j); // = Traffic.Collide(rivals[i].car, rivals[j].car), judged (GameManagerProgress.cs)
                if (pRun == null) PlayerContact(i);                                                   // = Traffic.Collide(rivals[i].car, car), judged
            }
        }
        double hw = track.width / 2;
        for (int i = 0; i < rivals.Count; i++)
        {
            var d = rivals[i];
            if (d.InPit) { if (i < rivalLod.Length) rivalLod[i].Reset(); d.PitStep(H, simTime); continue; } // the pit lane (PitLane.cs)
            if (aiTick % 4 == 1) { WheelContact.Update(track, line, d.car, d.idx, d.lateral, rubber, marbles); Weather.Apply(track, line, d.car, d.idx, d.lateral, d.tyreSet); }
            if (i < rivalLod.Length && rivalLod[i].Tick(d.car, RivalStartInput(i, d), H, rivalRate[i])) continue; // far: 120 / 60 Hz
            d.car.Step(RivalStartInput(i, d), H); // held until lights out + reaction time, then the launch clutch
        }
    }

    // ---------------------------------------------------------------- rival physics level of detail (AiLod, AiDriver.cs)
    // CPU: from one minute after the start (the lap-1 pack stays at 240 Hz), a rival outside the camera's detail range
    // (rivalNear) runs at 120 Hz beyond 150 m along the track from the player, and at 60 Hz beyond 400 m when no other
    // car is within 60 m of it. Time stays exact: it steps all the ticks it skipped at once, and is drawn that much
    // further forward (RivalBehind). Back in range it catches up in one step. The player's car is never touched.
    // This replaces GameManagerPerf.RivalCoarse (120 Hz only), which is no longer called.
    // Checked offline: Tools/PhysicsCheck "race" with LOD=1 (same finishing order, best laps +-0.01 s on average).
    AiLod[] rivalLod = new AiLod[0]; int[] rivalRate = new int[0];
    void PickRivalRates()
    {
        if (rivalLod.Length != rivals.Count)
        {
            rivalLod = new AiLod[rivals.Count]; rivalRate = new int[rivals.Count];
            for (int i = 0; i < rivalLod.Length; i++) { rivalLod[i] = new AiLod(); rivalRate[i] = 1; }
        }
        for (int i = 0; i < rivals.Count; i++) if (rivalLod[i].owner != rivals[i]) { rivalLod[i].Reset(); rivalLod[i].owner = rivals[i]; } // a new session
        bool on = !replaying && !playground && raceTime > Countdown + 60 && idx >= 0;
        for (int i = 0; i < rivals.Count; i++)
            rivalRate[i] = on && i < rivalNear.Count && !rivalNear[i] && !rivals[i].InPit ? AiLod.WantFor(track, idx, rivals[i], rivals) : 1;
    }
    double RivalBehind(int i) => i < rivalLod.Length ? rivalLod[i].Behind(H) : 0;

    int Position()
    {
        double mine = pLap + (idx < 0 ? 0 : (double)idx / track.N) - pitLoss / RefLap(); int pos = 1;
        for (int k = 0; k < rivals.Count; k++) if (rivals[k].progress - RivalPen(k) / RefLap() > mine) pos++; // less a rival's time penalties
        return pos;
    }

    void Recover()
    {
        if (pRun != null) return; // in the pit lane
        var p = track.pts[(idx + 4) % track.N];
        double keep = System.Math.Max(System.Math.Min(System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy), 40), 22);
        var cfg = car.cfg; int gear = 1;
        while (gear < cfg.gearTopSpeeds.Length && keep * 3.6 > cfg.gearTopSpeeds[gear - 1] * 0.8) gear++;
        var dmg = car.damage;
        car = new FormulaCar(cfg) { x = p.x, y = p.y, heading = System.Math.Atan2(p.ty, p.tx), vx = keep, gear = gear };
        car.damage = dmg; // damage survives a recovery
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
        for (int ti = 0, tn = Input.touchCount; ti < tn; ti++) // GetTouch: Input.touches allocates an array every frame
        {
            var t = Input.GetTouch(ti);
            if (t.fingerId == lookFinger) continue; // holding LOOK BACK (GameManagerLookBack.cs)
            var p = new Vector2(t.position.x, Screen.height - t.position.y); // GUI space (y down)
            bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
            // a finger "owns" the pedal it first touched and keeps controlling it even if it slides off the box
            if (t.phase == TouchPhase.Began)
            {
                if (GasRect.Contains(p)) gasFinger = t.fingerId;
                else if (BrakeRect.Contains(p)) brakeFinger = t.fingerId;
            }
            if (t.fingerId == gasFinger) { if (!ended) { gasT = gasMode == 1 ? 1f : PedalAmount(GasRect, p); gasSeen = true; } continue; }
            if (t.fingerId == brakeFinger) { if (!ended) { brkT = brakeMode == 1 ? 1f : PedalAmount(BrakeRect, p, 1.5f); brakeSeen = true; } continue; }
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
        steerFromTilt = tilt && !steerHeld && kSteer == 0;
        if (steerFromTilt) steer = TiltSteer();            // its own smooth curve and soft full lock (TiltToSteer)
        else if (steerHeld) steer = ShapeSteer(steer);       // the keyboard is on/off: no curve
        steer = AssistSteer(steer);
        return new CarInput(steer, pedalGas, pedalBrake);
    }
    // low on the pedal = light, high = full. The bottom 10% is a soft zone (0%), the top 8% is full, and in between a
    // progressive curve gives fine control at small openings (before: any touch gave 20% and the bottom half was too strong)
    static float PedalAmount(Rect r, Vector2 p, float curve = 1.8f)
    {
        float x = Mathf.Clamp01(((r.yMax - p.y) / r.height - 0.10f) / 0.82f);
        return Mathf.Pow(x, curve);
    }

    // ---------------------------------------------------------------- tilt steering
    // The phone is the steering wheel: its turn angle is the direction of gravity within the screen plane.
    //  * Gravity comes from the gyroscope-fused sensor (Input.gyro.gravity) when the phone has a gyroscope: sensor
    //    fusion makes it clean and immediate, the gyro is asked for its fastest rate (5 ms) and the newest value is
    //    read every frame. The accelerometer is only the fallback (no gyroscope, or until the gyro delivers): its
    //    gravity is buried in hand shake and needed a heavy low-pass, which was most of the old tilt lag.
    //  * The angle is measured from a LOCKED "down" axis, so a big tilt can never flip the steering, and the screen
    //    is kept from auto-rotating while tilt steering is on (a rotating screen made the steering jump).
    //  * "Tilt response" (a setting):
    //    Real-time (default): no smoothing delay. A jitter gate only irons out the sub-0.4 degree tremor of a hand
    //      holding the phone still: its cut-off climbs with the 6th power of the gap between reading and output, so
    //      any real turn opens it at once (80 Hz, 2 ms). The angle is carried forward by the gyro's turn rate over the
    //      sample's age, and the physics steps get a straight ramp from the last frame's angle to this one: no
    //      staircase and no spring (SteerFilter.Spring off). Only the car's own steering rack speed comes after.
    //    Smooth: a "one euro" filter (Tilt smoothing setting: smooths hard when still, little when turning quickly)
    //      and SteerFilter's spring with a fast constant (TiltOmega).
    //    Every filter uses the exact exponential for its time step, so the feel is the same at 30, 60 or 120 fps.
    //  * Only when the phone lies almost flat is there too little gravity in the screen plane to measure; steering
    //    then fades out. It works normally from about 15 degrees of screen tilt upwards.
    // Simulated 10 degree step (60 fps, 240 Hz physics): steering command at 63% after 27 ms before (accelerometer +
    // one euro + spring), 7 ms now (Real-time, 200 Hz gyro; 15 ms with a 60 Hz sensor). Tremor of a still hand
    // (0.15 deg at 9 Hz): 0.07 deg left at the steering.
    float tiltCenter, tiltSmooth, tiltAxis = float.NaN, tiltAxisT, tiltConf, tiltRawDeg, tiltVel, tiltLastX, tiltSince, tiltLead; bool tiltInit;
    bool tiltRealtime = true;            // "Tilt response": Real-time (true) or Smooth (GameManagerSettings.cs)
    bool tiltGyro;                       // this frame's gravity came from the gyroscope
    bool steerFromTilt;                  // this frame's steering came from the tilt (ReadInput -> SteerFilter mode)
    float gravMatch = float.NaN;         // slow average of dot(gyro gravity, accelerometer): +1 same axes, -1 flipped, ~0 unusable
    float tiltRateVotes, tiltPrevX = float.NaN; // how the gyro's z rate turns the angle (sign), learnt from real turns
    static readonly string[] SmoothNames = { "Off", "Low", "Medium", "High" };
    static readonly float[] SmoothCut = { 0f, 12f, 6f, 3f }; int smoothIdx = 1; // Hz for the Smooth response; 0 = no filter
    const float GateBand = 0.4f, GateBandAccel = 1.2f, GateMinHz = 1f, GateMaxHz = 80f; // Real-time jitter gate (degrees, Hz)
    const float SensorLead = 0.0025f;    // s: average age of the newest gyro sample (half of the 5 ms interval)
    const double TiltOmega = 400;        // SteerFilter spring for Smooth tilt (rad/s; touch keeps SteerFilter.Omega)
    static readonly string[] SpeedSteerNames = { "Full lock always", "Quick", "Normal", "Calm" };
    static readonly double[] SpeedSteer = { 0.0, 0.25, 0.5, 1.25 }; int speedSteerIdx = 2;

    static float Lp(float hz, float dt) => 1f - Mathf.Exp(-2f * Mathf.PI * hz * dt); // exact one-pole step: frame-rate independent

    // gyroscope on (fastest rate) only while tilt steering is used
    void TiltSensors(bool on)
    {
        gravMatch = float.NaN; tiltRateVotes = 0; tiltPrevX = float.NaN; tiltGyro = false;
        Input.compensateSensors = true; // accelerometer and gyro both in screen axes (the locked "down" axis expects that)
        if (!SystemInfo.supportsGyroscope) return;
        var g = Input.gyro;
        g.enabled = on;
        if (on) g.updateInterval = 0.005f; // 200 Hz: the most Android 12+ gives without a special permission; Unity clamps to the device
    }
    // the gravity to steer by: gyro-fused when it is live and in the same axes as the accelerometer, else the accelerometer
    Vector3 TiltGravity()
    {
        Vector3 a = Input.acceleration;
        tiltGyro = false;
        if (!SystemInfo.supportsGyroscope || !Input.gyro.enabled) return a;
        Vector3 g = Input.gyro.gravity; float m = g.magnitude, am = a.magnitude;
        if (!(m > 0.05f)) return a;          // not delivering yet (or NaN)
        g /= m;                               // g units on some phones, m/s^2 on others
        if (am > 0.6f && am < 1.4f)
        {
            float d = Vector3.Dot(g, a / am);
            gravMatch = float.IsNaN(gravMatch) ? d : gravMatch + (d - gravMatch) * 0.05f;
        }
        if (float.IsNaN(gravMatch) || Mathf.Abs(gravMatch) < 0.8f) return a;
        tiltGyro = true;
        return gravMatch < 0 ? -g : g;
    }

    // tilt angle (deg) from one gravity sample; dt: time since the previous sample (the settings' "set straight now" reads the current one)
    float TiltRaw() => TiltRaw(TiltGravity(), 0);
    float TiltRaw(Vector3 a, float dt)
    {
        float inPlane = Mathf.Sqrt(a.x * a.x + a.y * a.y);
        tiltConf = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.12f, 0.26f, inPlane));
        if (inPlane < 0.1f) return tiltRawDeg; // lying flat: nothing to measure, keep the last value
        float th = Mathf.Atan2(a.y, a.x) * Mathf.Rad2Deg;
        float nearest = Mathf.Round(th / 90f) * 90f;
        if (float.IsNaN(tiltAxis)) { if (inPlane < 0.3f) return 0; tiltAxis = nearest; }
        bool clearlyOther = Mathf.Abs(Mathf.DeltaAngle(th, nearest)) < 20f && Mathf.Abs(Mathf.DeltaAngle(nearest, tiltAxis)) > 1f && inPlane > 0.35f;
        if (clearlyOther) { tiltAxisT += dt; if (tiltAxisT > 0.5f) { tiltAxis = nearest; tiltAxisT = 0; } }
        else tiltAxisT = 0;
        float deg = Mathf.Clamp(Mathf.DeltaAngle(tiltAxis, th), -85, 85);
        tiltRawDeg = invertTilt ? -deg : deg;
        return tiltRawDeg;
    }
    // one sample into the response filter. run: how long the filter runs on it (less than dt when frames without a
    // sample already ran it on the previous one)
    void TiltSample(Vector3 a, float dt, float run)
    {
        float x = (TiltRaw(a, dt) - tiltCenter) * tiltConf;
        if (!tiltInit) { tiltSmooth = tiltLastX = x; tiltVel = 0; tiltInit = true; }
        tiltVel += ((x - tiltLastX) / dt - tiltVel) * Lp(10f, dt); // the phone's turn rate (deg/s), lightly smoothed
        tiltLastX = x;
        TiltFilter(x, run);
    }
    // Real-time: the jitter gate (returns how open it is, 0..1). Smooth: the one euro filter.
    float TiltFilter(float x, float dt)
    {
        if (tiltRealtime)
        {
            float r = Mathf.Abs(x - tiltSmooth) / (tiltGyro ? GateBand : GateBandAccel);
            r *= r * r; r *= r; // ^6: shut for tremor, wide open for a turn
            float hz = Mathf.Min(GateMinHz + GateMaxHz * r, GateMaxHz);
            tiltSmooth += (x - tiltSmooth) * Lp(hz, dt);
            return Mathf.Min(r, 1f);
        }
        float cut = SmoothCut[smoothIdx];
        if (cut <= 0) tiltSmooth = x;
        else tiltSmooth += (x - tiltSmooth) * Lp(cut + 0.18f * Mathf.Abs(tiltVel), dt);
        return 1f;
    }
    // Real-time with the gyro: carry the angle forward by the gyro's turn rate over the newest sample's age. The rate's
    // sign in tilt degrees is learnt from real turns (it depends on the phone and the screen side), none until known.
    float TiltPredict(float x, float open)
    {
        float wz = Input.gyro.rotationRateUnbiased.z * Mathf.Rad2Deg;
        if (float.IsNaN(wz)) return 0;
        float xr = invertTilt ? -x : x; // learn on the uninverted angle, so the Tilt direction setting does not unlearn it
        if (!float.IsNaN(tiltPrevX)) { float d = xr - tiltPrevX; if (Mathf.Abs(d) > 0.3f && Mathf.Abs(wz) > 15f) tiltRateVotes = Mathf.Clamp(tiltRateVotes + (d * wz > 0 ? 1 : -1), -30, 30); }
        tiltPrevX = xr;
        if (Mathf.Abs(tiltRateVotes) < 10) return 0;
        float lead = Mathf.Sign(tiltRateVotes) * wz * SensorLead * tiltConf * (invertTilt ? -1 : 1);
        return Mathf.Clamp(lead, -1f, 1f) * open; // only while the gate is open: no lead on tremor
    }
    // Tilt lock: the phone's roll from the newest gyro-fused gravity, read in LateUpdate (after the steering read) so the
    // picture stays level to the eyes in real time. Pure: it changes no steering state. Uses the steering's centre
    // (the way the player holds the phone when straight); works with touch steering too (switches the gyro on).
    float lockRoll;
    float HorizonRoll()
    {
        if (!tilt && SystemInfo.supportsGyroscope && !Input.gyro.enabled) TiltSensors(true);
        Vector3 a = TiltGravity();
        float inPlane = Mathf.Sqrt(a.x * a.x + a.y * a.y);
        if (inPlane < 0.1f) return lockRoll; // lying flat: keep the last angle
        float th = Mathf.Atan2(a.y, a.x) * Mathf.Rad2Deg;
        float axis = float.IsNaN(tiltAxis) ? Mathf.Round(th / 90f) * 90f : tiltAxis;
        float deg = Mathf.Clamp(Mathf.DeltaAngle(axis, th), -85f, 85f);
        float centre = invertTilt ? -tiltCenter : tiltCenter; // tiltCenter is kept in the steering's (maybe inverted) sense
        float conf = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.12f, 0.26f, inPlane));
        lockRoll = (deg - centre) * conf;
        return lockRoll;
    }
    float TiltSteer()
    {
        float frameDt = Mathf.Clamp(Time.unscaledDeltaTime, 0.002f, 0.1f);
        Vector3 g = TiltGravity();
        tiltLead = 0;
        if (tiltGyro)
        {
            // the newest fused sample, once per frame (the sensor runs faster than the game: at most ~5 ms old)
            float x = (TiltRaw(g, frameDt) - tiltCenter) * tiltConf;
            if (!tiltInit) { tiltSmooth = tiltLastX = x; tiltVel = 0; tiltInit = true; }
            tiltVel += ((x - tiltLastX) / frameDt - tiltVel) * Lp(10f, frameDt);
            tiltLastX = x; tiltSince = 0;
            float open = TiltFilter(x, frameDt);
            if (tiltRealtime) tiltLead = TiltPredict(x, open);
            return TiltToSteer();
        }
        // accelerometer: every sample on its own time step (the sensor beats with the frame rate)
        int n = Input.accelerationEventCount;
        for (int i = 0; i < n; i++)
        {
            var e = Input.GetAccelerationEvent(i);
            float dt = Mathf.Clamp(e.deltaTime, 0.001f, 0.1f);
            TiltSample(e.acceleration, dt, Mathf.Max(dt - tiltSince, 0.0005f));
            tiltSince = 0;
        }
        if (n == 0)
        {
            tiltSince += frameDt;
            if (!tiltInit || tiltSince > 0.1f) { TiltSample(g, frameDt, frameDt); tiltSince = 0; } // no sample events (editor, some phones): read it per frame
            else TiltFilter(tiltLastX, frameDt); // between samples: keep running on the last one
        }
        return TiltToSteer();
    }
    // Tilt angle -> steering, smooth everywhere (no corner anywhere, so nothing snaps; before: a slope step at the dead
    // zone and a hard clamp at full lock, and the Steering curve was steepest right at the edge):
    //  * soft dead zone: up to twice the dead zone the response grows from zero slope (quadratic), then it is the
    //    angle minus the dead zone, as before;
    //  * sensitivity scales the angle; the Steering curve bends only the inner half (a power curve that runs on as a
    //    straight line), so a higher curve is finer at the centre but never steeper at the edges (TiltCurve);
    //  * soft full lock: straight up to 82.5% of the travel, then a quadratic knee that reaches full lock with zero
    //    slope exactly at "Tilt for full lock". The straight middle is 1.1x the old slope and the steepest part.
    const float TiltKnee = 0.825f, TiltCurveSplit = 0.5f;
    float TiltToSteer()
    {
        float t = tiltSmooth + tiltLead, a = Mathf.Abs(t);
        float dz = tiltDz, range = Mathf.Max(tiltRange, dz + 1f);
        float ad = a < 2f * dz ? a * a / (4f * dz) : a - dz;
        float z = TiltCurve(ad / (range - dz) * steerGain, steerCurve);
        const float k = TiltKnee;
        float y = z < k ? z : z < 1f ? z - (z - k) * (z - k) / (2f * (1f - k)) : (1f + k) * 0.5f;
        return Mathf.Sign(t) * y * 2f / (1f + k);
    }
    // the Steering curve for tilt: v^c up to half the travel, then the tangent line, still reaching 1 at v = 1
    // (steepest slope c / (0.5 + 0.5c) of linear: 1.43x at the maximum curve 2.5, and that is in the middle)
    static float TiltCurve(float v, float c)
    {
        if (c <= 1.001f) return v;
        const float p = TiltCurveSplit;
        float pc1 = Mathf.Pow(p, c - 1f), A = 1f / (pc1 * (p + c * (1f - p)));
        return v < p ? A * Mathf.Pow(v, c) : A * pc1 * p + c * A * pc1 * (v - p);
    }
    // the screen always auto-rotates between the two landscape sides, with tilt too: locking it read the side wrong on
    // some phones and flipped the picture. Tilt reads the angle about the screen's own axis, so it works either way up;
    // when the side changes the tilt axis is picked again (TiltOrientation, from Update)
    void ApplyScreenLock()
    {
        TiltSensors(tilt);
        Screen.autorotateToPortrait = Screen.autorotateToPortraitUpsideDown = false;
        Screen.autorotateToLandscapeLeft = Screen.autorotateToLandscapeRight = true;
        if (Screen.orientation != ScreenOrientation.AutoRotation) Screen.orientation = ScreenOrientation.AutoRotation;
        tiltAxis = float.NaN; tiltInit = false;
    }
    DeviceOrientation lastSide = DeviceOrientation.Unknown;
    void TiltOrientation()
    {
        var d = Input.deviceOrientation;
        if (d != DeviceOrientation.LandscapeLeft && d != DeviceOrientation.LandscapeRight) return;
        if (lastSide != DeviceOrientation.Unknown && d != lastSide) { tiltAxis = float.NaN; tiltInit = false; }
        lastSide = d;
    }
    int classIdx; // car class: 0 F1 2026, 1 Formula 2, 2 Formula 3
    CarConfig PlayerConfig()
    {
        var c = CarConfig.ForClass(classIdx);
        c.tc = tc; c.abs = absOn;
        compIdx = (int)Weather.PhysicsCompound(tyreSet);
        c.speedSens = SpeedSteer[speedSteerIdx]; c.compound = (Compound)compIdx; c.fuelStart = RaceFuel();
        cfgClass = classIdx; ApplySetup(c); // steering rates and lock, aero balance, brakes, differential, ride heights
        TyreRulesBegin(); TyreRulesFor(c, tyreSet); // Settings > Tyre wear: this session's wear and tyre temperature (TyrePit.cs)
        return c;
    }

    // ---------------------------------------------------------------- loop
    void Update()
    {
        if (ui.LoadDue) StartSession();
        Thermal.Tick(); // the phone's thermal state, clocks and refresh rate, read every 2 s (Thermal.cs): Auto frame rate, debug overlay
        DisplayModeTick(); // a 40 fps lock asks for the panel's 120 Hz mode, else 60 / 30 only (GameManagerPerf.cs)
        FrameLogFeed(); // the frame-time overlay's per-frame inputs (FrameLog.cs; cheap stores, also when it is off)
        PickPlayerModel(); // full car in the cockpit and on High, the lighter one otherwise (GameManagerPerf.cs)
        if (tilt) TiltOrientation(); // the screen turned to the other landscape side: pick the tilt axis again
        if (menuOpen && Input.GetKeyDown(KeyCode.Escape)) CloseSettings(); // Android "back"
        float dt = Mathf.Min(Time.deltaTime, 0.1f);
        if (replaying) { ReplayStep(dt); if (flashT > 0) flashT -= dt; return; } // the race is over: only the recording moves
        if (Paused || PostFreeze) dt = 0; // the settings menu and the front end pause the game; PostFreeze: the post-race film holds the race (PostRace.cs)
        if (hitT > 0) hitT -= dt;
        LookBackInput(); // the LOOK BACK button / key (GameManagerLookBack.cs), before ReadInput: its finger never steers
        var input = ReadInput(dt);
        ErsButtonsInput(); // BOOST and OVERTAKE buttons into the car (RaceHud.cs)
        acc += dt;
        // steering is read once per frame but the physics takes 4-8 steps per frame: ramp it smoothly across them
        // (SteerFilter.cs) instead of one jump per frame and then nothing (the wheel turned in small steps)
        // tilt: Real-time = a straight ramp between frames and no spring; Smooth = a fast spring; touch keeps the default
        steerF.Spring = !(steerFromTilt && tiltRealtime); steerF.omega = steerFromTilt ? TiltOmega : SteerFilter.Omega;
        if (steerReset) { steerF.Reset(input.steer); steerReset = false; }
        steerF.BeginFrame(input.steer, (int)(acc / H), H);
        double wallDv = 0; // biggest speed change from a barrier hit this frame (m/s), for the haptics
        PerfLab.PhysBegin();
        long physT0 = System.Diagnostics.Stopwatch.GetTimestamp(); int physSteps = 0;
        while (acc >= H)
        {
            px = car.x; py = car.y; ph = car.heading; pd = car.delta; psi = car.steerIn;
            input.steer = steerF.Step(H);
            if (PreRaceCarPosed) PreRaceRevs(); // the pre-race cinematic poses the car in its pit box / the pit lane: no physics step (PreRace.cs)
            else if (pRun != null) { PlayerPitStep(); LineCheck(simTime + H); } // in the pit lane: driven along the lane at the limiter (PitCrew.cs); idx is after the step
            else
            {
                SetSurface();
                LineCheck(simTime); // the timing line, every step (idx is before the step here)
                if (WorldBuilder.Height != null) car.grade = playground ? 0 : WorldBuilder.Height.GradeAlong(car.x, car.y, car.heading, idx);
                car.deployMap = !playground && line != null && line.deploy != null && idx >= 0 && idx < line.deploy.Length ? line.deploy[idx] : 1; // same energy strategy as the AI
                car.Step(PostInput(PsInput(StartInput(input))), H); // the start: revs only on the grid, launch clutch after lights out; PsInput: serving a live penalty (PenaltyServe.cs); PostInput: the cool-down lap's autopilot (PostRace.cs)
                StartAfterStep();
                double preVx = car.vx, preVy = car.vy; // Wall does not turn the car, so the body-frame change is the real one
                if (!playground && idx >= 0 && Traffic.Wall(car, track.pts[idx], playerLat, wallAt))
                {
                    if (hitT <= 0) Flash("Barrier!"); hitT = 1.5f;
                    double dvx = car.vx - preVx, dvy = car.vy - preVy;
                    wallDv = System.Math.Max(wallDv, System.Math.Sqrt(dvx * dvx + dvy * dvy));
                }
                PlayerPitEntry(); // BOX armed and passing the pit entry: into the pit lane
            }
            if (rivals.Count > 0) StepRivals();
            raceTime += H;
            simTime += H; acc -= H;
            frontSpin += car.vx / car.cfg.wheelRadius * H;
            if (playground) TestBench();
            // spiral guard (Docs/PERF-INVESTIGATION.md): only if the physics of ONE frame has already taken longer than
            // SpiralMs (never on a healthy phone: 22 cars cost ~0.03-0.3 ms a step), the rest of this frame's whole steps
            // are dropped (the game slows for that frame) instead of making the next frame longer still. The 240 Hz
            // step itself is unchanged.
            if (++physSteps >= SpiralMinSteps && acc >= H && System.Diagnostics.Stopwatch.GetTimestamp() - physT0 > SpiralTicks)
            {
                acc -= System.Math.Floor(acc / H) * H; FrameLog.SpiralDrops++;
                break;
            }
        }
        PerfLab.PhysEnd(); // timing only
        FrameLog.Physics(physSteps, System.Diagnostics.Stopwatch.GetTimestamp() - physT0);
        if (!Paused) Rumble(wallDv);
        if (!playground) LapsAndRecover(dt);
        QualiUpdate(); // qualifying clock and flag (Qualifying.cs)
        if (raceLaps > 0 && !playground && pLap > raceLaps && !ui.Showing && post == null) FinishRace(); // pLap 1 = crossing the line from the grid (post: the post-race film is on, PostRace.cs)
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
        if ((car.x - px) * (car.x - px) + (car.y - py) * (car.y - py) > 25) { px = car.x; py = car.y; ph = car.heading; pd = car.delta; psi = car.steerIn; } // reset / recover
        double al = acc / H, dh = System.Math.Atan2(System.Math.Sin(car.heading - ph), System.Math.Cos(car.heading - ph));
        double rx = px + (car.x - px) * al, ry = py + (car.y - py) * al, rh = ph + dh * al, rd = pd + (car.delta - pd) * al;
        renderDelta = rd; renderSteer = psi + (car.steerIn - psi) * al; // the steering wheel at the same moment as the car
        model.Apply(rx, ry, rh, rd, visF, visR, gLon, gLat);
        if (LightBake.Active && LightBake.CarSun != null)
        {
            // the cars' sun dims when the player's car is inside a baked shadow
            float lit = playground ? 1 : LightBake.Sun((float)car.x, (float)car.y, model.transform.position.y + 0.6f);
            carLit += (lit - carLit) * Mathf.Min(1, dt * 8);
            LightBake.CarSun.intensity = 1.25f * (0.12f + 0.88f * carLit) * Sky.CarSunShare; // (the cars' own sun level: Sky.SunAt carSun)
            Shader.SetGlobalFloat("_CarLit", carLit);
        }
        model.SetBrake(car.brake > 0.05); model.SetSteer((float)renderSteer);
        model.Feed(car, dt, surface == 0);
        for (int i = 0; i < rivals.Count; i++)
        {
            var c = rivals[i].car;
            rivalSpin[i] = Mathf.Repeat(rivalSpin[i] + Mathf.Clamp((float)(c.vx / c.cfg.wheelRadius) * dt, -0.5f, 0.5f), Mathf.PI * 2);
            // close rivals use the detailed model, distant ones the light one
            // (a zoomed-in TV camera sees far cars big: their distance counts as shorter by the zoom)
            float ddx = (float)c.x - cam.transform.position.x, ddz = (float)c.y - cam.transform.position.z, zoom = camMode == TvMode ? Mathf.Clamp(cam.fieldOfView / 62f, 0.05f, 1f) : 1f;
            bool near = (ddx * ddx + ddz * ddz) * zoom * zoom < (rivalNear[i] ? 60f * 60f : 52f * 52f) || rivalFar[i] == null;
            if (near != rivalNear[i]) { rivalNear[i] = near; rivalModels[i].gameObject.SetActive(near); if (rivalFar[i] != null) rivalFar[i].gameObject.SetActive(!near); }
            var rm = near ? rivalModels[i] : rivalFar[i];
            // drawn at the same in-between moment as the player's car, or they would shake against it
            double back = (1 - acc / H) * H - RivalBehind(i), cch = System.Math.Cos(c.heading), csh = System.Math.Sin(c.heading);
            rm.Apply(c.x - (c.vx * cch - c.vy * csh) * back, c.y - (c.vx * csh + c.vy * cch) * back, c.heading - c.r * back, c.delta, rivalSpin[i], rivalSpin[i], 0, 0);
            rm.SetBrake(c.brake > 0.05);
            rm.Feed(c, dt);
        }
        RcFrame(dt); // safety car model, flags, penalties, rival tyre changes (GameManagerRaceControl.cs)
        ProgressTick(dt); // safety rating events, clean laps, overtakes, race rules (GameManagerProgress.cs)
        if (!Paused && raceLaps > 0 && !playground && PostRecord) Record(dt, rx, ry, rh, rd); // (PostRecord: the replay ends just past the line)
        UpdateSkids();
        LineFrame(); // the racing-line guide (RacingLineView.cs)
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
        audioEngine.Feed(car, cam.transform, camMode == TvMode ? 2 : camMode == 2 || camMode == 3 ? 1 : 0, rivals, Paused); // tyre loads, shifts, boost, surfaces, view, rival cars (EngineAudio.cs)
        if (Paused) { audioEngine.throttle = 0; audioEngine.speed = 0; audioEngine.slide = 0; }
        audioEngine.muted = !sound || ui.Showing; // no engine on the menu pages (home, play, loading, results)
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
    }

    // The timing line, once per physics step (it used to be tested once a frame against prevIdx, but prevIdx is
    // updated every step, so only a crossing in a frame's LAST step counted: most crossings were missed, the lap
    // never started (LAST / BEST "--:--.---") and the player fell "+1 LAP" behind in the tower). Every completed lap
    // is timed and shown, an invalid one (track limits, recovery) too; only valid laps can be the best.
    void LineCheck(double t)
    {
        if (playground || track == null || form != null) return; // (no lap on the formation lap)
        double jx = car.x - lineX, jy = car.y - lineY; // a teleport (restart, shot mode): start again from here
        if (idx < 0 || lineIdx < 0 || double.IsNaN(lineT) || jx * jx + jy * jy > 50 * 50) { lineIdx = idx; lineX = car.x; lineY = car.y; lineT = t; return; }
        int dir = TimingLine.Cross(track, lineIdx, idx, lineX, lineY, lineT, car.x, car.y, t, out double at);
        lineIdx = idx; lineX = car.x; lineY = car.y; lineT = t;
        if (dir > 0)
        {
            if (lapStart.HasValue)
            {
                lastLap = at - lapStart.Value; lastValid = !cut;
                if (!cut && (!bestLap.HasValue || lastLap < bestLap)) { bestLap = lastLap; Flash("Best lap! " + Fmt(bestLap)); }
                else Flash((cut ? "Lap invalid  " : "Lap ") + Fmt(lastLap));
            }
            lapStart = at; lapNo++; pLap++; cut = false; car.NewLap();
            if (boxArmed && !PitLaneOn) { boxArmed = false; if (raceLaps > 0 && pLap <= raceLaps) PitStop(true); } // no pit lane: the old stop at the line
        }
        else if (dir < 0) { lapStart = null; pLap--; }
    }

    // ---------------------------------------------------------------- pit stop (races only)
    // "BOX" arms a stop; at the pit entry (~250 m before the line) the car is driven down the pit lane at the 80 km/h
    // limiter into its box, the crew changes the tyres on it and it is driven out to the exit (PitLane.cs; glue in
    // PitCrew.cs). The time lost is the drive itself. PitStop applies the service when the new wheels go on: the next
    // compound fresh off the blankets, a new wing, puncture repair. On a circuit without a pit lane (instant = true) the
    // stop happens at the line and its time loss is added as before.
    const double PitLoss = 22;
    bool boxArmed; double pitLoss; int pitStops;
    void PitStop(bool instant)
    {
        carSet = NextSet; var next = Weather.PhysicsCompound(carSet);
        car.cfg.compound = next; PitFit(); // the set's heat, the pit chips back to the auto pick (TyrePit.cs)
        car.wearF = car.wearR = 0; car.dirtF = car.dirtR = 0;
        car.tyreTF = car.tyreTR = car.cfg.tyreStart;       // straight off the blankets
        car.tyreSF = car.tyreSR = double.NaN;               // the surface temperature re-starts from the carcass
        if (model != null) model.SetStripe(Weather.Stripe(carSet));
        double wing = car.damage.WingsDamaged ? AiDriver.WingChange : 0; car.RepairWings(); car.FixPunctures();
        pitStops++;
        if (instant)
        {
            double loss = RcPitLoss(PitLoss); // cheaper under the safety car / VSC
            pitLoss += loss + wing; pitStopLen = loss + wing;
            Flash($"Pit stop: {Weather.TyreName(carSet)}  +{loss + wing:0}s");
        }
        else pitStopLen = pRun != null ? pRun.serviceLen : PitLane.Service;
    }
    // the next tyre set: the player's pick on the pit chips, else the one for the weather first, then round the rest (TyrePit.cs)
    int NextSet => pitChoice >= 0 ? pitChoice : AutoSet;
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
    Vector3 camVel, chaseDir; float gLon, gLat, visF, visR, carLit = 1;
    readonly DriverFeel feel = new DriverFeel(); // head / camera motion from the car's forces, steering wheel, shift lights (DriverFeel.cs)
    void LateUpdate()
    {
        if (PreRaceCamera()) return; // the pre-race intro has the camera (PreRace.cs)
        if (FormCamera()) return; // a TV shot of the formation lap (GameManagerFormation.cs)
        if (PostCamera()) return; // the post-race film: flag, cool-down lap, podium (PostRace.cs)
        var tr = model.transform;
        if (camMode == TvMode || replaying)
        {
            TvCamera(tr, Time.deltaTime);
            CockpitBits(false); // seen from outside: helmet and halo shown, cockpit floor hidden, as in chase
            cam.nearClipPlane = ChaseNear;
            return;
        }
        if (PitCamera()) return; // the player's own pit stop: a short cinematic of the box (PitCrew.cs)
        float speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        Vector3 pos, look; float fov;
        float lonG = gLon / 9.81f;
        DriverFeel.Intensity = camShakePct * 0.01f; // the camera-shake setting scales all of the camera's force motion
        feel.Cockpit(model, car, Time.time, renderDelta); // steering wheel by the real front-wheel angle (drawn between physics steps, like the car), shift lights
        feel.Step(car, gLon, gLat, speed, Paused ? 0 : Time.deltaTime, camMode == 2 ? 1 : camMode == 3 ? 2 : 0);
        if (camMode == 2 || camMode == 3)
        {
            // 2 = cockpit (driver's eyes, low behind the halo); 3 = halo cam (just above the driver's head)
            bool halo = camMode == 3;
            // settings: the cockpit eye point can be moved up/down and forward/back a few cm (the view angle stays)
            float seatY = seatUpCm * 0.01f, seatZ = seatFwdCm * 0.01f;
            // the head's movement under braking, cornering, kerbs and speed comes from DriverFeel (springs, small amplitudes)
            // halo cam "Helmet in view" setting (haloBackPct): 0 = just above the head; 100 = up on the airbox intake, tipped
            // down 15 deg (6 deg wider) so the top of the driver's helmet sits at the bottom of the screen (an F1 T-cam). In between it
            // slides along the same line (the intake pod behind the head hides the helmet from any point further back)
            float hb = haloBackPct * 0.01f;
            Vector3 eye = halo ? new Vector3(0, 1.02f + 0.18f * hb, 0.12f - 0.14f * hb) : new Vector3(0, 0.76f + seatY, 0.30f + seatZ);
            pos = tr.TransformPoint(eye + feel.offset);
            float lookY = halo ? eye.y - 30f * Mathf.Tan((0.9f + 14.1f * hb) * Mathf.Deg2Rad) : 0.76f + seatY;
            look = tr.TransformPoint(new Vector3(0, lookY, 30)); // cockpit: 2.8 deg down, fitted to a real F1 onboard frame
            fov = halo ? fovCockpit + 11 + 6f * hb : fovCockpit; // default 55 (halo 66): fitted to a real F1 onboard frame (eye, angle and view solved from its halo, tyre and wheel positions); no widening with speed
            cam.transform.position = pos;
        }
        else
        {
            float back = camMode == 0 ? 6.2f : 10f, up = camMode == 0 ? 2f : 3.4f;
            back += Mathf.Clamp(lonG * 0.35f, -0.9f, 0.5f);
            // the camera is tied to the car's (interpolated) position every frame; only the direction it looks from is
            // smoothed. The old world-position SmoothDamp let the camera fall ~5 m further back at 300 km/h and, with
            // uneven frame times, made the car shuffle back and forth on screen (the "laggy" chase view)
            Vector3 fwd = tr.forward;
            if (tvWasOn || chaseDir.sqrMagnitude < 0.5f) chaseDir = fwd;
            float kDir = 1f - Mathf.Exp(-Time.deltaTime / 0.11f); // ~0.11 s: swings round gently in a slide or spin
            chaseDir = Vector3.Slerp(chaseDir, fwd, kDir).normalized;
            Vector3 upV = tr.up, basePos = tr.position;
            pos = basePos + upV * up - chaseDir * back;
            look = basePos + upV * 0.7f + chaseDir * 6f;
            fov = fovChase; // a setting (default 62), fixed: widening the view with speed exaggerated how fast the car felt
            // the setting is the view on a 16:9 screen; on wider phones (19.5:9, 20:9, 21:9) keep that same side-to-side view
            // instead of stretching it, which made the road look narrow and far away
            float aspect = Mathf.Max(0.5f, cam.aspect);
            if (aspect > 16f / 9f) fov = 2f * Mathf.Rad2Deg * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * (16f / 9f) / aspect);
            cam.transform.position = pos; camVel = Vector3.zero;
        }
        cam.transform.LookAt(look);
        cam.transform.Rotate(feel.pitch, feel.yaw, feel.roll, Space.Self); // nod, look into the corner, head roll and shake
        // horizon lock (a setting): the picture turns against the phone's tilt; in the cockpit only, in every view, or not at all
        // Tilt lock (a setting): the picture turns fully against the phone's roll, read fresh here every frame (no filter)
        if (horizonMode == 2 || (horizonMode == 1 && (camMode == 2 || camMode == 3))) cam.transform.Rotate(0, 0, -HorizonRoll(), Space.Self);
        cam.fieldOfView += (fov - cam.fieldOfView) * Mathf.Min(1, Time.deltaTime * 4);
        CockpitBits(camMode == 2); tvWasOn = false;
        cam.nearClipPlane = camMode == 2 ? CockpitNear() : camMode == 3 ? HaloNear : ChaseNear;
        LookBackCamera(tr); // LOOK BACK held: the rear view instead (GameManagerLookBack.cs)
    }
    // Near clip plane. The depth buffer's precision far away goes with near / distance^2, so a tiny near plane makes
    // distant road, kerbs, lines and verges z-fight (shimmer and swap as the car moves). Each view uses the largest
    // near plane that cuts nothing of the car it looks through (measured on car.bytes, with the head movement and
    // the FOV / seat settings): chase and TV 0.5 m (was 0.3), halo cam 0.12 m (the halo is 0.2 m away at the widest
    // FOV; was 0.05), cockpit 0.085 m at the default view, down to 0.05 m at the widest FOV and screen (was 0.05).
    const float ChaseNear = 0.5f, HaloNear = 0.12f;
    float CockpitNear()
    {
        // the nearest bodywork in view is the tub side at the screen's left/right edge: it scales with the
        // horizontal half-width of the view (1.145 = 55 deg vertical FOV on a 20:9 screen)
        float halfW = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * Mathf.Max(1f, cam.aspect);
        return Mathf.Clamp(0.085f * 1.145f / Mathf.Max(halfW, 0.01f), 0.05f, 0.085f);
    }
    // the whole halo (hoop + pillar) stays in every view; only the "Halo in cockpit: Hide" setting removes it, and only in the cockpit view. The dark cockpit floor is only shown there
    void CockpitBits(bool cockpit)
    {
        model.helmet.gameObject.SetActive(!cockpit);
        bool showHalo = !(cockpit && !haloInCockpit);
        if (model.haloPillar != null) model.haloPillar.SetActive(showHalo);
        if (model.haloHoop != null) model.haloHoop.SetActive(showHalo);
        if (model.cockpitFloor) model.cockpitFloor.SetActive(cockpit);
        // the driver: head under the car's forces (DriverFeel), hands on the wheel as turned this frame (DriverModel)
        feel.Head(replaying ? null : car, gLon, gLat, Paused ? 0 : Time.deltaTime);
        model.PoseDriver(feel);
    }

    // ---------------------------------------------------------------- TV camera (camMode 4, and replays)
    // Fixed trackside cameras, placed automatically about every 250 m: 25-40 m beyond the track edge (on the outside
    // of the bend, clear of other parts of the circuit), 4-8 m up. The camera in use is the first one the car has
    // not yet passed (the nearest one ahead, or one it is just passing); 15 m after the car passes it, the picture
    // cuts to the next. The view follows the car with a smoothed look-at and zooms so the car stays the same size.
    readonly List<Vector3> tvPts = new List<Vector3>(); readonly List<int> tvAt = new List<int>(); // camera position, its track point
    int tvFor = -2, tvCur = -1, tvCarIdx = -1; bool tvFree; // tvFree: no circuit (playground): one camera leap-frogs ahead of the car
    Vector3 tvPos, tvLook, tvLookVel, tvPrevCar; float tvFov = 30; int tvLastFrame = -10; bool tvWasOn;

    // Candidate positions are tested against the solids of everything standing round the track (Trackside.Solids:
    // stands, pit building, screens, towers, cranes, masts, banners, bridges, map buildings and stands; plus the model
    // trees' crowns): the camera must not stand inside one, and its lines of sight to the road over the whole stretch it
    // films (from the previous camera to 15 m past this one) must not pass through one or through the ground. In v11 a
    // long lens looked straight into a banner bridge 25 m away (a yellow board filled the frame). If the first spot is
    // blocked: up to 48 m along the track either way, 3 or 6 m higher, the other side; else the least blocked.
    static readonly List<Trackside.Solid> tvNear = new List<Trackside.Solid>();
    // (shift along the track in 12 m steps, 3 m raises, other side?, cost), cheapest first
    struct TvCand { public int x, y, z; public float w; }
    static readonly TvCand[] TvCands = MakeTvCands();
    static TvCand[] MakeTvCands()
    {
        var l = new List<TvCand>();
        for (int sd = 0; sd < 2; sd++) for (int rz = 0; rz < 3; rz++) for (int sh = -4; sh <= 4; sh++)
            l.Add(new TvCand { x = sh, y = rz, z = sd, w = Mathf.Abs(sh) + rz * 1.3f + sd * 2.5f + (sh < 0 ? 0.1f : 0f) });
        l.Sort((a, b) => a.w.CompareTo(b.w));
        return l.ToArray();
    }
    void BuildTvCams()
    {
        tvPts.Clear(); tvAt.Clear(); tvCur = -1; tvCarIdx = -1; tvFor = playground ? -1 : builtFor;
        if (track == null || playground) return;
        var rnd = new System.Random(builtFor * 131 + 7);
        int count = Mathf.Max(3, Mathf.RoundToInt((float)(track.length / 250))), every = track.N / count, span = Mathf.Max(1, (int)(40 / track.ds));
        int N = track.N; float ds = (float)track.ds;
        double hw = track.width / 2;
        int W(int i) => ((i % N) + N) % N;
        // all solids with absolute heights (Trackside's are above the draped ground), and the model trees: trunk + crown
        var all = new List<Trackside.Solid>(Trackside.Solids.Count + ModelLib.TreeFeet.Count * 2);
        foreach (var so in Trackside.Solids)
        {
            var s2 = so;
            if (s2.rel) { float g = WorldBuilder.H(s2.cx, s2.cz); s2.y0 += g; s2.y1 += g; s2.rel = false; }
            all.Add(s2);
        }
        for (int k = 0; k < ModelLib.TreeFeet.Count; k++)
        {
            var f = ModelLib.TreeFeet[k]; float r = ModelLib.TreeWidth[k] * 0.38f;
            all.Add(new Trackside.Solid { cx = f.x, cz = f.z, tx = 1f, tz = 0f, hl = r, hd = r, y0 = f.y + f.w * 0.32f, y1 = f.y + f.w * 0.95f });
            all.Add(new Trackside.Solid { cx = f.x, cz = f.z, tx = 1f, tz = 0f, hl = 0.35f, hd = 0.35f, y0 = f.y - 0.5f, y1 = f.y + f.w * 0.35f });
        }
        double wall = Traffic.WallOffset(track); bool over = wall - hw > 4;
        int prevAt = -1, step12 = Mathf.Max(1, Mathf.RoundToInt(12f / ds));
        var tgt = new List<Vector3>();
        for (int c = 0; c < count; c++)
        {
            int at0 = c * every;
            // curv > 0 = a left-hander, whose outside is to the right (-normal); on straights alternate sides
            double cv = 0; for (int j = -span; j <= span; j++) cv += track.pts[W(at0 + j)].curv;
            cv /= 2 * span + 1;
            double side = System.Math.Abs(cv) > 0.002 ? (cv > 0 ? -1 : 1) : (c % 2 == 0 ? 1 : -1);
            double off = hw + 25 + rnd.NextDouble() * 15; float up = 4 + (float)rnd.NextDouble() * 4;
            // Circuits with a catch fence a few metres off the track (Trackside.cs: up to 4.8 m high at the barrier line,
            // leaning in): a camera 25-40 m out and 4-8 m up looked THROUGH the mesh (and, at the pits, stood inside the pit
            // building). Real TV cameras stand on platforms right behind the fence and shoot over it: 1.2 m behind the
            // barrier line, 6.6-8.2 m up, which clears the fence top for a car anywhere on the road.
            if (over) { off = wall + 1.2; up = 6.6f + (up - 4f) * 0.4f; }
            // what this camera films: from the previous camera (its cut point) to 15 m past this one, every ~12 m, the
            // car's height over the centreline
            int from = prevAt >= 0 ? prevAt : at0 - every, back = Mathf.RoundToInt(15f / ds);
            int len = W(at0 + 4 * step12 + back - (from - back));
            tgt.Clear();
            for (int j = 0; j <= len; j += step12)
            {
                var q = track.pts[W(from - back + j)];
                tgt.Add(new Vector3((float)q.x, WorldBuilder.HOn(W(from - back + j), q.x, q.y) + 0.9f, (float)q.y)); // (HOn: its own leg at a bridge, not the deck above)
            }
            // the solids anywhere near that stretch (bounding box of the targets, grown by the camera's offset)
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            foreach (var v in tgt) { x0 = Mathf.Min(x0, v.x); z0 = Mathf.Min(z0, v.z); x1 = Mathf.Max(x1, v.x); z1 = Mathf.Max(z1, v.z); }
            float grow = (float)off + 20f; x0 -= grow; z0 -= grow; x1 += grow; z1 += grow;
            tvNear.Clear();
            foreach (var so in all)
            {
                float e = Mathf.Max(so.hl, so.hd);
                if (so.cx + e >= x0 && so.cx - e <= x1 && so.cz + e >= z0 && so.cz - e <= z1) tvNear.Add(so);
            }
            // candidates in order of preference (TvCands); the first with every line clear wins, else the lowest
            // cost + 4 per blocked line
            float bestCost = float.MaxValue; Vector3 bestPos = Vector3.zero; int bestAt = -1;
            foreach (var cd in TvCands)
            {
                int shift = cd.x, rz = cd.y, sd = cd.z; float cost = cd.w;
                if (cost >= bestCost) break;
                int at = W(at0 + shift * step12);
                var tp = track.pts[at];
                double sdv = sd == 0 ? side : -side, o = off;
                double cx = tp.x + tp.nx * sdv * o, cy = tp.y + tp.ny * sdv * o;
                // clear of other parts of the circuit (hairpins, parallel straights)
                if (track.Nearest(cx, cy).dist <= (over ? wall - 0.5 : hw + 20))
                {
                    if (over) continue;
                    o = hw + 25; cx = tp.x + tp.nx * sdv * o; cy = tp.y + tp.ny * sdv * o;
                    if (track.Nearest(cx, cy).dist <= hw + 20) continue;
                }
                var pos = new Vector3((float)cx, WorldBuilder.H(cx, cy) + up + rz * 3f, (float)cy);
                bool inside = false;
                foreach (var so in tvNear) if (Trackside.Hits(so, pos, pos, 1.5f)) { inside = true; break; }
                if (inside) continue;
                int blocked = 0;
                foreach (var v in tgt)
                {
                    bool hit = false;
                    // the ground (hills, crests) at three points along the line
                    for (int u = 1; u <= 3 && !hit; u++) { var m = Vector3.Lerp(pos, v, u * 0.25f); hit = WorldBuilder.H(m.x, m.z) > m.y - 0.3f; }
                    // the last 2 m before the car are road (kerbs, barriers): only solids from the camera to there
                    var vEnd = Vector3.MoveTowards(v, pos, 2f);
                    if (!hit) foreach (var so in tvNear) if (Trackside.Hits(so, pos, vEnd, 0.2f)) { hit = true; break; }
                    if (hit && cost + ++blocked * 4f >= bestCost) break; // (no better than the best so far)
                }
                float total = cost + blocked * 4f;
                if (total < bestCost) { bestCost = total; bestPos = pos; bestAt = at; }
                if (blocked == 0) break;
            }
            if (bestAt < 0) continue;
            tvPts.Add(bestPos); tvAt.Add(bestAt); prevAt = bestAt;
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
    bool Paused => menuOpen || (ui != null && ui.Showing) || shotFreeze; // shotFreeze: screenshot mode (DebugShots.cs)

    // called once the menu's "Loading…" frame is on screen: build the chosen circuit and start the session
    void StartSession()
    {
        AutoGfxAtSessionStart(); // Auto's graphics step held back from the last session (GameManagerPerf.cs), before the world is built
        ui.ReleaseStudio(); ApplyFrameRate(); // the menu's 3D gone, the race frame rate back (MenuStudio.cs caps the menu)
        FrameLog.SessionStart(); // the debug overlay's 2-minute summary starts again
        raceLaps = ui.practice ? 0 : MenuUI.LapCounts[ui.lapsIdx];
        PrepareSession(); // weekend / championship: qualifying, circuit of the round, grid (Qualifying.cs)
        if (lineFor != classIdx && world != null) { Destroy(world); world = null; } // new class: rebuild with its racing line
        LoadMap(); ApplyWeather(); // the menu may have changed the weather
        if (raceLaps > 0) raceLaps = RaceDistanceLaps(raceLaps); // Settings > Race distance (GameManagerRaceControl.cs)
        menuOpen = false; ApplyScreenLock();
        ResetCar(); Save();
        SessionStarted(); // qualifying timing, team livery (Qualifying.cs)
        acc = 0; ui.page = MenuUI.Page.None;
        PreRaceBegin(); // the pre-race intro (PreRace.cs): the race clock and the lights wait for it
    }

    // the player has done raceLaps: freeze the race and rank every car by distance covered
    void FinishRace()
    {
        PostCapture(); // the player's look for the podium, before the championship is recorded (PostRace.cs)
        var rows = new MenuUI.Row[rivals.Count + 1];
        rows[0] = new MenuUI.Row { name = "You", colour = new Color(1f, 0.25f, 0.2f), player = true, progress = pLap + (double)System.Math.Max(idx, 0) / track.N - pitLoss / RefLap(), best = bestLap ?? double.NaN };
        for (int i = 0; i < rivals.Count; i++)
        {
            var w = rivals[i].who;
            rows[i + 1] = new MenuUI.Row { name = w.name, colour = new Color(w.r, w.g, w.b), progress = rivals[i].progress, best = rivals[i].bestLap };
        }
        double refLap = RefLap(); // turns lap fractions into seconds
        ProgressFinish(rows); // rival time penalties into the order; the race's XP and safety tally (GameManagerProgress.cs)
        ui.ShowResults(rows, MapNames[mapIdx], raceLaps, raceTime - (GridStart ? Countdown : 0) + pitLoss, refLap, pitStops, PlayerPenaltyTotal());
        PsResults(); // penalties served on track: the chip says "served" (PenaltyServe.cs)
        RaceFinished(rows); // places gained, fastest lap, championship points (Qualifying.cs)
        PostBegin(rows); // Settings > Post-race: the flag, cool-down lap and podium film before this page (PostRace.cs)
    }

    // ---------------------------------------------------------------- HUD
    // Drawing only: the touch logic (ReadInput, GasRect, BrakeRect) is above and does not depend on any of this.
    // Look: UiKit.cs; layout and the race read-outs: RaceHud.cs.
    Texture2D white;
    void OnGUI()
    {
        var et = Event.current.type; bool hud = !menuOpen && !ui.Showing;
        if (hud && et != EventType.Repaint && et != EventType.MouseDown && et != EventType.MouseUp) return; // the race HUD reacts to nothing else: every steering-thumb move was a full HUD pass
        if (hud && PerfLab.HudOff) return;
        PerfLab.GuiBegin(); OnGuiBody(); PerfLab.GuiEnd();
    }
    void OnGuiBody()
    {
        if (white == null) white = Texture2D.whiteTexture;
        if (menuOpen && !replaying) { DrawSettings(); return; } // the settings screen covers everything (race paused, or over the start menu)
        if (ui.Showing)
        {
            ui.canReplay = CanReplay; ui.playerCar = model; // (the menu's studio shows a copy of the car)
            var choice = ui.Draw(ref mapIdx, ref classIdx, ref rivalIdx, ref diffIdx, track, builtFor);
            if (choice == MenuUI.Choice.RaceAgain) { ResetCar(); ui.page = MenuUI.Page.None; }
            else if (choice == MenuUI.Choice.Replay) StartReplay();
            else if (choice == MenuUI.Choice.Settings) menuOpen = true;
            else if (choice == MenuUI.Choice.Livery) { liveryIdx = Livery.PlayerIndex; if (model != null) model.SetLivery(Livery.Player[liveryIdx]); } // Garage
            else if (choice != MenuUI.Choice.None) SessionChoice(choice); // weekend flow (Qualifying.cs)
            return;
        }
        if (PreRaceGui()) return; // the pre-race intro: captions instead of the HUD (PreRace.cs)
        if (FormGui()) return; // the formation lap's TV shots: captions instead of the HUD (GameManagerFormation.cs)
        if (PostGui()) return; // the post-race film's overlays instead of the HUD (PostRace.cs)
        if (ShotsHideHud) return; // screenshot mode: clean frames (DebugShots.cs)
        HudInit(); HudLayout();
        if (replaying)
        {
            // replay: a small caption and a way out, nothing else on screen
            float total = (recCount - 1) / (float)RecHz;
            var cap = new Rect(hudL, hudT, S(116), S(28));
            Panel(cap);
            bool dot = Mathf.Repeat(Time.unscaledTime, 1f) < 0.6f;
            Disc(new Rect(cap.x + S(11), cap.center.y - S(3), S(6), S(6)), dot ? HudAccent : new Color(0.4f, 0.05f, 0.03f));
            TF(new Rect(cap.x + S(23), cap.y, S(60), cap.height), "REPLAY", UiKit.Black, 9f, Color.white);
            TF(new Rect(cap.x, cap.y, cap.width - S(11), cap.height), ReplayCaption(Mathf.Max(0, total - replayT)), UiKit.Semi, 8f, HudGrey, TextAnchor.MiddleRight);
            if (PillBtn(new Rect(hudR - S(112), hudT, S(112), S(28)), "END REPLAY", null, HudAccent)) StopReplay();
            return;
        }
        DrawQualiHud(); // qualifying clock and live timing (Qualifying.cs)
        float speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);

        DrawTopButtons();
        if (!menuOpen && !playground && (surface == 2 || hitT > 0))
        {
            var bt = new Rect(Mathf.Round(Screen.width * 0.5f - S(72)), Screen.height * 0.5f, S(144), S(32));
            if (PillBtn(bt, "BACK ON TRACK", icoBack, HudCyan, false, 8.5f)) Recover();
        }
        // timing tower, race card and sectors, dash, banners (RaceHud.cs)
        DrawRaceHud(speed);

        DrawMapHud(); // Map setting: radar (Radar.cs), track map (DrawMiniMap) or off
        DrawLookBack(); // LOOK BACK button (GameManagerLookBack.cs)
        DrawStartLights(); // the five red lights (GameManagerStart.cs)

        // pedals (drawn inside their touch rects; the rects themselves are unchanged)
        Pedal(GasRect, pedalGas, new Color(0.13f, 0.86f, 0.45f), "GAS");
        Pedal(BrakeRect, pedalBrake, new Color(1f, 0.24f, 0.2f), "BRAKE");
        if (!tilt) DrawSteerHint();

        if (Time.realtimeSinceStartup < 8f)
            UiKit.Text(new Rect(hudL, hudB - S(86), Mathf.Max(S(120), Screen.width * 0.5f - S(140) - hudL), S(30)), CarModel.Credit, UiKit.Regular, 5.8f, new Color(1f, 1f, 1f, 0.55f), TextAnchor.LowerLeft, true);
        FormHud(); // the formation lap: banner, hint, skip, the dip at its end (GameManagerFormation.cs)
    }

    // top right, left of the race card: settings, camera, restart (round glass icons) and the BOX pill in a race
    void DrawTopButtons()
    {
        float b = S(30), g = S(7), y = hudT + S(1), x = btnRight - b;
        if (RestartOk && RoundBtn(new Rect(x, y, b, b), icoBack)) { ResetCar(); menuOpen = false; } // none in career qualifying / Hard+ races (CareerSession.cs)
        x -= b + g;
        if (RoundBtn(new Rect(x, y, b, b), icoCam)) { camMode = (camMode + 1) % CamNames.Length; Flash(CamNames[camMode]); Save(); }
        x -= b + g;
        if (RoundBtn(new Rect(x, y, b, b), icoGear)) menuOpen = true;
        // pit stop: arm it during a race (not on the last lap); it happens when the car next crosses the line
        if (!menuOpen && !playground && raceLaps > 0 && pLap < raceLaps && pRun == null && form == null)
        {
            float pw = S(58); x -= pw + g;
            bool armed = boxArmed, blink = armed && Mathf.Repeat(Time.unscaledTime * 2f, 1f) < 0.5f;
            if (PillBtn(new Rect(x, y, pw, b), armed ? "BOXING" : "BOX", null, armed && blink ? new Color(1f, 0.16f, 0.1f) : HudAccent, armed, 8.5f))
            { boxArmed = !boxArmed; if (!boxArmed) pitChoice = -1; Flash(boxArmed ? $"Box this lap: {Weather.TyreName(NextSet)}" : "Box cancelled"); }
        }
        DrawPitChips(); // the tyre set for the stop: S / M / H / I / W chips while boxing (TyrePit.cs)
    }

    // drag steering: a slim track bottom left; the dot follows your thumb
    void DrawSteerHint()
    {
        float w = S(130), x = hudL + S(10), y = hudB - S(8);
        bool held = steerFinger >= 0;
        float a = held ? 1f : 0.55f;
        var rail = new Rect(x, y, w, S(3));
        UiKit.Rect(rail, new Color(1f, 1f, 1f, 0.14f * a), 1.5f);
        UiKit.Rect(new Rect(x + w * 0.5f - S(0.5f), y - S(2), S(1), S(7)), new Color(1f, 1f, 1f, 0.3f * a));
        float s = held ? Mathf.Clamp(dragSteer, -1f, 1f) : 0f, px = x + w * 0.5f + s * w * 0.5f;
        if (held) UiKit.Rect(new Rect(Mathf.Min(px, x + w * 0.5f), y, Mathf.Abs(px - (x + w * 0.5f)), S(3)), new Color(HudCyan.r, HudCyan.g, HudCyan.b, 0.7f), 1.5f);
        Disc(new Rect(px - S(5), y + S(1.5f) - S(5), S(10), S(10)), held ? Color.white : new Color(1f, 1f, 1f, 0.45f));
        if (!held) TF(new Rect(x, y - S(15), w, S(10)), "DRAG TO STEER", UiKit.Semi, 6.2f, new Color(1f, 1f, 1f, 0.5f), TextAnchor.MiddleCenter);
    }

    void Panel(Rect r) => Glass(r, 10f);
    void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c * o; GUI.DrawTexture(r, white != null ? white : Texture2D.whiteTexture); GUI.color = o; }
    // a touch pedal: translucent rounded glass, analog fill from the bottom with a bright level line, glow when pressed
    void Pedal(Rect r, float v, Color c, string label)
    {
        v = Mathf.Clamp01(v);
        float rad = 16f;
        if (v > 0.02f) UiKit.Rect(new Rect(r.x - S(3), r.y - S(3), r.width + S(6), r.height + S(6)), new Color(c.r, c.g, c.b, 0.16f * v), rad + 3f); // glow
        UiKit.Rect(r, new Color(0.02f, 0.025f, 0.035f, 0.26f), rad);
        if (v > 0.01f)
        {
            float h = Mathf.Max(S(6), r.height * v);
            var f = new Rect(r.x, r.yMax - h, r.width, h);
            var old = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.35f + 0.4f * v) * old;
            UiKit.Gradient(f, new Color(c.r, c.g, c.b, 0.85f), new Color(c.r * 0.6f, c.g * 0.6f, c.b * 0.6f, 0.55f), rad);
            GUI.color = old;
            UiKit.Rect(new Rect(r.x + S(8), f.y + S(1), r.width - S(16), S(2)), new Color(1f, 1f, 1f, 0.75f * v), 1f); // level line
        }
        // no border (Adi): the fill, ribs and label show where the pedal is
        // grip ribs and the label
        float gx = r.x + r.width * 0.3f, gw = r.width * 0.4f;
        for (int i = 0; i < 3; i++) UiKit.Rect(new Rect(gx, r.y + S(14) + i * S(6), gw, S(1.6f)), new Color(1f, 1f, 1f, 0.16f), 1f);
        UiKit.TextShadow(new Rect(r.x, r.yMax - S(24), r.width, S(16)), label, UiKit.Bold, 8f, new Color(1f, 1f, 1f, 0.8f), TextAnchor.MiddleCenter);
    }
}
