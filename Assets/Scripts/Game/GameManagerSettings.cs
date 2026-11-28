// GameManagerSettings.cs — the settings half of GameManager (a partial class; the component itself is GameManager.cs):
//  * the settings the tabbed settings screen added, with their PlayerPrefs keys, loading and saving
//  * the rows of the settings screen (SettingsUI.cs draws them): CONTROLS, ASSISTS (the Difficulty preset first:
//    CareerDifficulty.cs; changing an aid, the rival level or damage by hand makes it Custom), CAR SETUP, CAMERA,
//    GRAPHICS, AUDIO, GAME (rows that would restart a career round are locked during one: CareerSession.cs)
//  * what the settings do: steering and brake curves, steering assist, braking assist, car set-up values written
//    into the car's CarConfig, frame rate, resolution and volumes
// Old settings keep their PlayerPrefs keys. Four of them were a position in a short list ("bal", "brakeBias",
// "tiltDz", "tiltRange") and are sliders now: the value is saved under a new key ("balF"...), read from the old
// key when the new one is not there yet, and the old key is still written (nearest list entry).
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    // ---------------------------------------------------------------- values (defaults = the game as it was)
    // controls
    const float SteerRate = 30; // steering speed is always the fastest (33 ms); no setting
    float steerGainPct = 90, steerCurve = 1, steerLockPct = 100, steerRate = SteerRate, steerRet = 18;
    float tiltDz = 0.2f, tiltRange = 35;                 // degrees
    int gasMode, brakeMode;                              // 0 analog (height on the pedal), 1 on/off
    float brakeGainPct = 100, brakeCurve = 1;
    // assists
    int steerAssist = 1, brakeAssist = 1;                // 0 off, 1 low, 2 high
    // car set-up
    float aeroBal = 45, brakeBiasPct = 57, brakeMigPct, engBrakePct = 100, diffPowerPct = 15, diffCoastPct = 25, rideFmm, rideRmm;
    float gripPct = 100, rearGripPct = 100, downPct = 100; bool handTest; // handling test (Adi, Nov 24): player's car only; 100 = the real car
    // camera
    float fovChase = 62, fovCockpit = 55, seatUpCm, seatFwdCm, camShakePct = 100;
    internal float haloBackPct = 50;                    // halo cam "helmet in view" 0-100 (GameManager.cs camera; DebugShots haloback=)
    bool haloInCockpit = true;                           // cockpit view draws the halo (hoop + pillar); false hides all of it
    int horizonMode = 0;                                 // Tilt lock: 0 off, 1 cockpit views, 2 all views
    // graphics
    // frame rate: 0 Auto (60, or a steady 40 / 30 if the phone can't hold it: GameManagerPerf.cs), 1 60, 2 40, 3 30 fps.
    // Saved as "fpsMode"; the old "fps" key (0 = 30, 1 = 60) is still written and read once when "fpsMode" is missing.
    int fpsIdx; float resPct = 100; bool shadowsOn = true, showFps = true;
    static readonly string[] FpsNames = { "Auto", "60 fps", "40 fps", "30 fps" };
    // audio (percent)
    float volMaster = 100, volEngine = 100, volFx = 100, volTyres = 100;
    // team radio (the radio itself is TeamRadio.cs): 0 Full, 1 Key info, 2 Off; PlayerPrefs "teamRadio"
    int teamRadioMode;
    static readonly string[] TeamRadioNames = { "Full", "Key info", "Off" };
    // game
    int unitsIdx; bool hudInfo = true, hudMap = true, hudTower = true, hudSectors = true, hudDelta = true;

    float steerGain => steerGainPct * 0.01f;
    float FpsTarget => fpsLock;                           // the steady rate asked for now (GameManagerPerf.cs)
    float ResMax => resPct * 0.01f;                      // most of the screen resolution the 3D picture may use
    float ResMin => Mathf.Min(0.6f, ResMax);             // dynamic resolution never goes below this
    string SpeedUnit => unitsIdx == 1 ? "mph" : "km/h";
    bool InRace => ui == null || !ui.Showing;            // settings opened from a session (not from the start menu)

    // the short lists the four old settings stepped through (their saved value is a position in these)
    static readonly float[] OldBalances = { 45, 48, 50, 53 }, OldBiases = { 53, 55, 57, 59, 61 }, OldDeadZones = { 0, 0.5f, 1, 2, 3 }, OldTiltRanges = { 12, 16, 20, 25, 30 };

    int cfgClass;                                        // the car class the player's car was built for
    static CarConfig[] baseCfg;                          // each class as designed (never changed), for set-up offsets
    static CarConfig BaseCfg(int k)
    {
        if (baseCfg == null) { baseCfg = new CarConfig[CarConfig.ClassNames.Length]; for (int i = 0; i < baseCfg.Length; i++) baseCfg[i] = CarConfig.ForClass(i); }
        return baseCfg[Mathf.Clamp(k, 0, baseCfg.Length - 1)];
    }

    // ---------------------------------------------------------------- load / save
    static float LoadF(string key, float def, float min, float max) => Mathf.Clamp(PlayerPrefs.GetFloat(key, def), min, max);
    int damageMode = 1; // 0 off, 1 visual only, 2 full
    int flagsMode = 1;  // flags and safety car: 0 off, 1 on (GameManagerRaceControl.cs)
    int raceDistIdx;    // race length: 0 = the laps on the start menu, else a share of a Grand Prix distance (RaceDistPct)
    static int LoadI(string key, int def, int max) => Mathf.Clamp(PlayerPrefs.GetInt(key, def), 0, max);
    static float LoadOld(string key, string oldKey, float[] list, int defIdx, float min, float max) =>
        Mathf.Clamp(PlayerPrefs.HasKey(key) ? PlayerPrefs.GetFloat(key) : list[Mathf.Clamp(PlayerPrefs.GetInt(oldKey, defIdx), 0, list.Length - 1)], min, max);
    static int Nearest(float[] list, float v)
    {
        int best = 0;
        for (int i = 1; i < list.Length; i++) if (Mathf.Abs(list[i] - v) < Mathf.Abs(list[best] - v)) best = i;
        return best;
    }

    // ---- new defaults, applied ONCE to existing players (Nov 28): everything else they set is kept
    const int SettingsVer = 1;
    void MigrateSettings()
    {
        if (PlayerPrefs.GetInt("setVer", 0) >= SettingsVer) return;
        PlayerPrefs.SetInt("setVer", SettingsVer);
        PlayerPrefs.SetFloat("steerLock", 100); PlayerPrefs.SetInt("speedSteer", 0);
        PlayerPrefs.SetFloat("tiltDzF", 0.2f); PlayerPrefs.SetInt("tiltDz", Nearest(OldDeadZones, 0.2f));
        PlayerPrefs.SetFloat("tiltRangeF", 35); PlayerPrefs.SetInt("tiltRange", Nearest(OldTiltRanges, 35));
        PlayerPrefs.SetFloat("steerGain", 90); PlayerPrefs.SetInt("tiltRT", 1);
        PlayerPrefs.SetFloat("haloBack", 50);
        PlayerPrefs.SetInt("aeroPct", 60); PlayerPrefs.SetInt("tyreWear", TyreRules.Auto);
        if (PlayerPrefs.GetInt("difficulty", Difficulty.Custom) == Difficulty.Custom) // a preset keeps its own assists
        { PlayerPrefs.SetInt("tc", (int)TcMode.High); PlayerPrefs.SetInt("steerAssist", 1); PlayerPrefs.SetInt("brakeAssist", 1); }
        if (PlayerPrefs.GetInt("gfx", 0) == 0) PlayerPrefs.SetInt("gfx", DefaultGfx());
    }
    // the graphics level picked once from the GPU (no "Auto" any more): Mali-G5x / G6x and Adreno 6xx class = Medium
    static int DefaultGfx()
    {
        string g = SystemInfo.graphicsDeviceName ?? "";
        if (g.Contains("Mali-4") || g.Contains("Mali-T") || g.Contains("Mali-G3") || g.Contains("Adreno (TM) 3") || g.Contains("Adreno (TM) 4") || g.Contains("Adreno (TM) 5") || g.Contains("PowerVR")) return 1;
        if (g.Contains("Mali-G5") || g.Contains("Mali-G6") || g.Contains("Adreno (TM) 6")) return 2;
        return 3;
    }

    void LoadSettings()
    {
        steerGainPct = LoadF("steerGain", 90, 10, 100); steerCurve = LoadF("steerCurve", 1, 1, 2.5f); steerLockPct = LoadF("steerLock", 100, 50, 100);
        steerRate = SteerRate; steerRet = LoadF("steerRet", 18, 4, 30);
        tiltDz = LoadOld("tiltDzF", "tiltDz", OldDeadZones, 1, 0, 3); tiltRange = LoadOld("tiltRangeF", "tiltRange", OldTiltRanges, 2, 10, 45);
        tiltRealtime = PlayerPrefs.GetInt("tiltRT", 1) == 1; // Real-time tilt response by default
        gasMode = LoadI("gasMode", 0, 1); brakeMode = LoadI("brakeMode", 0, 1);
        brakeGainPct = LoadF("brakeGain", 100, 50, 150); brakeCurve = LoadF("brakeCurve", 1, 0.5f, 2.5f);
        steerAssist = LoadI("steerAssist", 1, 2); brakeAssist = LoadI("brakeAssist", 1, 2);
        aeroBal = LoadOld("balF", "bal", OldBalances, 0, 44, 54); brakeBiasPct = LoadOld("brakeBiasF", "brakeBias", OldBiases, 2, 50, 65);
        brakeMigPct = LoadF("brakeMig", 0, 0, 6); engBrakePct = LoadF("engBrake", 100, 50, 150);
        diffPowerPct = LoadF("diffPower", 15, 5, 40); diffCoastPct = LoadF("diffCoast", 25, 5, 50);
        rideFmm = LoadF("rideF", 0, -5, 10); rideRmm = LoadF("rideR", 0, -10, 15);
        gripPct = LoadF("tstGrip", 100, 80, 110); rearGripPct = LoadF("tstRear", 100, 80, 110); downPct = LoadF("tstDown", 100, 60, 110); handTest = PlayerPrefs.GetInt("tstOn", 0) == 1;
        fovChase = LoadF("fovChase", 62, 50, 80); fovCockpit = LoadF("fovCockpit", 55, 45, 80);
        seatUpCm = LoadF("seatUp", 0, -8, 8); seatFwdCm = LoadF("seatFwd", 0, -10, 10); camShakePct = LoadF("camShake", 100, 0, 200); haloBackPct = LoadF("haloBack", 50, 0, 100);
        horizonMode = LoadI("tiltLock", 0, 2);
        haloInCockpit = PlayerPrefs.GetInt("haloCockpit", 1) == 1;
        damageMode = LoadI("damage", 1, 2);
        flagsMode = LoadI("flags", 1, 1); raceDistIdx = LoadI("raceDist", 0, RaceDistNames.Length - 1);
        fpsIdx = PlayerPrefs.HasKey("fpsMode") ? LoadI("fpsMode", 0, 3) : PlayerPrefs.GetInt("fps", 1) == 0 ? 3 : 0; resPct = LoadF("resScale", 100, 50, 100); shadowsOn = PlayerPrefs.GetInt("shadows", 1) == 1; showFps = PlayerPrefs.GetInt("showFps", 1) == 1;
        teamRadioMode = LoadI("teamRadio", 0, 2);
        volMaster = LoadF("volMaster", 100, 0, 100); volEngine = LoadF("volEngine", 100, 0, 100); volFx = LoadF("volFx", 100, 0, 100); volTyres = LoadF("volTyres", 100, 0, 100);
        unitsIdx = LoadI("units", 0, 1); hudInfo = PlayerPrefs.GetInt("hudInfo", 1) == 1; hudMap = PlayerPrefs.GetInt("hudMap", 1) == 1;
        hudTower = PlayerPrefs.GetInt("hudTower", 1) == 1; hudSectors = PlayerPrefs.GetInt("hudSectors", 1) == 1; hudDelta = PlayerPrefs.GetInt("hudDelta", 1) == 1;
        preRaceIdx = LoadI("preRace", 0, PreRaceNames.Length - 1); // race start: Full / Short / Off (PreRace.cs; the old "Race intro" On / Short / Off, same values)
    }

    void SaveSettings()
    {
        PlayerPrefs.SetFloat("steerGain", steerGainPct); PlayerPrefs.SetFloat("steerCurve", steerCurve); PlayerPrefs.SetFloat("steerLock", steerLockPct);
        PlayerPrefs.SetFloat("steerRet", steerRet);
        PlayerPrefs.SetFloat("tiltDzF", tiltDz); PlayerPrefs.SetInt("tiltDz", Nearest(OldDeadZones, tiltDz));
        PlayerPrefs.SetFloat("tiltRangeF", tiltRange); PlayerPrefs.SetInt("tiltRange", Nearest(OldTiltRanges, tiltRange));
        PlayerPrefs.SetInt("tiltRT", tiltRealtime ? 1 : 0);
        PlayerPrefs.SetInt("gasMode", gasMode); PlayerPrefs.SetInt("brakeMode", brakeMode);
        PlayerPrefs.SetFloat("brakeGain", brakeGainPct); PlayerPrefs.SetFloat("brakeCurve", brakeCurve);
        PlayerPrefs.SetInt("steerAssist", steerAssist); PlayerPrefs.SetInt("brakeAssist", brakeAssist);
        PlayerPrefs.SetFloat("balF", aeroBal); PlayerPrefs.SetInt("bal", Nearest(OldBalances, aeroBal));
        PlayerPrefs.SetFloat("brakeBiasF", brakeBiasPct); PlayerPrefs.SetInt("brakeBias", Nearest(OldBiases, brakeBiasPct));
        PlayerPrefs.SetFloat("brakeMig", brakeMigPct); PlayerPrefs.SetFloat("engBrake", engBrakePct);
        PlayerPrefs.SetFloat("diffPower", diffPowerPct); PlayerPrefs.SetFloat("diffCoast", diffCoastPct);
        PlayerPrefs.SetFloat("rideF", rideFmm); PlayerPrefs.SetFloat("rideR", rideRmm);
        PlayerPrefs.SetFloat("tstGrip", gripPct); PlayerPrefs.SetFloat("tstRear", rearGripPct); PlayerPrefs.SetFloat("tstDown", downPct); PlayerPrefs.SetInt("tstOn", handTest ? 1 : 0);
        PlayerPrefs.SetFloat("fovChase", fovChase); PlayerPrefs.SetFloat("fovCockpit", fovCockpit);
        PlayerPrefs.SetFloat("seatUp", seatUpCm); PlayerPrefs.SetFloat("seatFwd", seatFwdCm); PlayerPrefs.SetFloat("camShake", camShakePct); PlayerPrefs.SetFloat("haloBack", haloBackPct);
        PlayerPrefs.SetInt("tiltLock", horizonMode);
        PlayerPrefs.SetInt("haloCockpit", haloInCockpit ? 1 : 0);
        PlayerPrefs.SetInt("damage", damageMode);
        PlayerPrefs.SetInt("flags", flagsMode); PlayerPrefs.SetInt("raceDist", raceDistIdx);
        PlayerPrefs.SetInt("fpsMode", fpsIdx); PlayerPrefs.SetInt("fps", fpsIdx == 3 ? 0 : 1); PlayerPrefs.SetFloat("resScale", resPct); PlayerPrefs.SetInt("shadows", shadowsOn ? 1 : 0); PlayerPrefs.SetInt("showFps", showFps ? 1 : 0);
        PlayerPrefs.SetInt("teamRadio", teamRadioMode);
        PlayerPrefs.SetFloat("volMaster", volMaster); PlayerPrefs.SetFloat("volEngine", volEngine); PlayerPrefs.SetFloat("volFx", volFx); PlayerPrefs.SetFloat("volTyres", volTyres);
        PlayerPrefs.SetInt("units", unitsIdx); PlayerPrefs.SetInt("hudInfo", hudInfo ? 1 : 0); PlayerPrefs.SetInt("hudMap", hudMap ? 1 : 0);
        PlayerPrefs.SetInt("hudTower", hudTower ? 1 : 0); PlayerPrefs.SetInt("hudSectors", hudSectors ? 1 : 0); PlayerPrefs.SetInt("hudDelta", hudDelta ? 1 : 0);
        PlayerPrefs.SetInt("preRace", preRaceIdx);
    }

    // ---------------------------------------------------------------- applying
    // Set-up values into a car's config. Ride heights and steering lock are offsets from the class's own design.
    void ApplySetup(CarConfig c)
    {
        var b = BaseCfg(cfgClass);
        int dm = DamageNow; c.damage = dm == 2; CarModel.damageVisuals = dm > 0; // a career round always has damage (CareerSession.cs)
        c.steerSpeed = steerRate; c.steerReturn = steerRet; c.maxLockDeg = b.maxLockDeg * steerLockPct * 0.01;
        if (cfgClass == 0) c.aeroFront = aeroBal / 100.0; // the F1 car only, as before
        c.brakeBias = brakeBiasPct / 100.0; c.brakeMigration = brakeMigPct / 100.0; c.engineBrake = engBrakePct / 100.0;
        c.diffPower = diffPowerPct / 100.0; c.diffCoast = diffCoastPct / 100.0;
        c.rideF0 = b.rideF0 + rideFmm / 1000.0; c.rideR0 = b.rideR0 + rideRmm / 1000.0;
        // handling test sliders (config only, the physics code is untouched; 100 % = the real car)
        // (Off = exactly the original car, whatever the sliders say)
        float tg = handTest ? gripPct : 100f, tr = handTest ? rearGripPct : 100f, td = handTest ? downPct : 100f;
        c.mu = b.mu * tg * 0.01; c.rearGrip = b.rearGrip * tr * 0.01;
        c.clA = b.clA * td * 0.01 * AeroPct * 0.01; c.clAX = b.clAX * td * 0.01 * AeroPct * 0.01; // x Settings > Downforce (all cars)
    }
    // ---- Settings > Downforce: every car's aero (clA, clAX) x AeroPct; pref "aeroPct" (Adi, Nov 25: 60 % by default)
    internal static readonly string[] AeroNames = { "Fun 60%", "70%", "80%", "Real 100%" };
    static readonly int[] AeroVals = { 60, 70, 80, 100 };
    int aeroIdxCache = -1, lineAero = -1;
    int AeroIdx
    {
        get => 0; // fixed at Fun 60% for every car and every player (no setting): the code path stays, the value is forced
        set { }
    }
    int AeroPct => AeroVals[AeroIdx];
    void ScaleAero(CarConfig c) { double k = AeroPct * 0.01; c.clA *= k; c.clAX *= k; }
    void SetupChanged() { if (car != null) ApplySetup(car.cfg); } // takes effect at once, also in a race

    void ApplyAudio()
    {
        if (audioEngine == null) return;
        audioEngine.masterVol = volMaster * 0.01f; audioEngine.engineVol = volEngine * 0.01f; audioEngine.fxVol = volFx * 0.01f; audioEngine.tyreVol = volTyres * 0.01f;
    }
    void ApplyFrameRate() => FpsStart(); // a steady rate the screen divides evenly (GameManagerPerf.cs)
    void ApplyResolution()
    {
        float ns = ResMax; resStepped = false;
        if (!Mathf.Approximately(ns, renderScale)) SetRenderScale(ns); // GLES: a smaller screen buffer (GameManagerPerf.cs)
    }
    void ApplyDisplay() { ApplyFrameRate(); ApplyResolution(); }

    // ---------------------------------------------------------------- steering and brake shaping, driver aids
    // touch steering: the sensitivity is applied where the input is measured (so full lock stays reachable); this
    // bends the response: exponent 1 = linear, higher = finer near the centre. Tilt has its own bounded version
    // (GameManager.TiltCurve) so the curve never steepens the edges of the tilt.
    float ShapeSteer(float s) => steerCurve <= 1.001f ? s : Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), steerCurve);

    // brake pedal: sensitivity (below 100% a full press no longer gives full braking), then the curve
    float ShapeBrake(float b)
    {
        b = Mathf.Clamp01(b * brakeGainPct * 0.01f);
        return Mathf.Abs(brakeCurve - 1f) < 0.001f ? b : Mathf.Pow(b, brakeCurve);
    }

    // steering assist: when the car slides (its nose points away from where it is going), steer into the slide
    // for the driver. Normal cornering has a few degrees of slide angle, so the first 2.3 degrees are left alone.
    float AssistSteer(float steer)
    {
        if (steerAssist == 0 || car == null || car.reverse) return steer;
        float v = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        if (v < 8f) return steer;
        float beta = (float)System.Math.Atan2(car.vy, System.Math.Max(System.Math.Abs(car.vx), 1));
        float over = Mathf.Sign(beta) * Mathf.Max(0, Mathf.Abs(beta) - 0.04f);
        float k = (steerAssist == 1 ? 0.35f : 0.7f) * Mathf.Clamp01((v - 8f) / 10f);
        return Mathf.Clamp(steer - Mathf.Clamp(over / 0.10f, -1f, 1f) * k, -1f, 1f); // countersteer = the sign opposite to the slide
    }

    // braking assist: the racing line knows how fast the car can be at every point of the lap (braking zones
    // included). If the car is faster than that just ahead, brake. Low lets the car run 10% over and brakes at
    // 70% at most; High keeps to the line's speed with full brakes.
    float AssistBrake()
    {
        if (brakeAssist == 0 || playground || car == null || car.reverse || line == null || line.speed == null || track == null || idx < 0 || line.N != track.N) return 0;
        if (GridStart && raceTime < Countdown) return 0;
        double v = car.vx; if (v < 8) return 0;
        int ahead = (int)(v * 0.3 / track.ds);
        double vt = double.MaxValue;
        for (int k = 0; k <= ahead; k++) vt = System.Math.Min(vt, line.speed[(idx + k) % track.N]);
        vt *= brakeAssist == 1 ? 1.10 : 1.02;
        return Mathf.Clamp01((float)((v - vt) / 4.0)) * (brakeAssist == 1 ? 0.7f : 1f);
    }

    // ---------------------------------------------------------------- the settings screen
    SettingsUI settingsUi; float centreSetT = -10;

    void DrawSettings()
    {
        SyncDifficulty(); // a preset chosen on the career hub / menu shows here at once
        if (settingsUi == null) settingsUi = new SettingsUI(BuildSettings(), Save);
        if (settingsUi.Draw(InRace ? "game paused" : "")) CloseSettings();
    }
    void CloseSettings() { menuOpen = false; ApplyScreenLock(); Save(); }

    static readonly string[] GfxChoice = { "Low", "Medium", "High" };
    static string Pct(float v) => $"{v:0}%";
    static string Signed(float v, string fmt, string unit) => (v > 0.0001f ? "+" : "") + v.ToString(fmt) + unit;

    List<SettingsUI.Tab> BuildSettings()
    {
        string[] offLowHigh = { "Off", "Low", "High" }, hideShow = { "Hide", "Show" }, pedal = { "Analog", "On / Off" };
        System.Func<string> tiltOnly = () => tilt ? null : "Used with tilt steering only.";

        // ---- CONTROLS
        var con = new SettingsUI.Tab("CONTROLS");
        con.Choice("Steering", "Drag a thumb on the left of the screen, or tilt the phone like a wheel.", new[] { "Drag", "Tilt" }, 0,
            () => tilt ? 1 : 0, v => { tilt = v == 1; tiltCenter = 0; ApplyScreenLock(); });
        con.Slider("Steering sensitivity", "How far the wheel turns for the same drag or tilt.", 10, 100, 5, 90, () => steerGainPct, v => steerGainPct = v, Pct);
        con.Slider("Steering curve", "1.0 is linear. Higher = finer control near the centre, same full lock.", 1f, 2.5f, 0.05f, 1f, () => steerCurve, v => steerCurve = v, v => v.ToString("0.00"));
        con.Slider("Maximum steering lock", "Limits how far the front wheels can turn.", 50, 100, 1, 100, () => steerLockPct, v => { steerLockPct = v; SetupChanged(); },
            v => $"{v:0}% · {BaseCfg(classIdx).maxLockDeg * v * 0.01:0}°");
        con.Slider("Return to centre", "How fast the wheel comes back when you let go: time from full lock.", 4, 30, 1, 18, () => steerRet, v => { steerRet = v; SetupChanged(); }, v => $"{1000f / v:0} ms");
        con.Choice("Steering at speed", "Less steering lock as the speed rises, for a calmer car on the straights.", new[] { "Off", "Quick", "Normal", "Calm" }, 0,
            () => speedSteerIdx, v => { speedSteerIdx = v; if (car != null) car.cfg.speedSens = SpeedSteer[speedSteerIdx]; });
        con.Choice("Tilt direction", "Swap left and right if the car steers the wrong way.", new[] { "Normal", "Inverted" }, 0, () => invertTilt ? 1 : 0, v => invertTilt = v == 1).LockedWhen(tiltOnly);
        con.Button("Tilt centre", "Hold the phone how you like, then tap.",
            () => Time.realtimeSinceStartup - centreSetT < 1.5f ? "Centre set" : "Set straight now", () => { tiltCenter = TiltRaw(); centreSetT = Time.realtimeSinceStartup; }).LockedWhen(tiltOnly);
        con.Slider("Tilt dead zone", "Small tilt that is ignored.", 0, 3, 0.1f, 0.2f, () => tiltDz, v => tiltDz = v, v => $"{v:0.0}°").LockedWhen(tiltOnly);
        con.Slider("Tilt for full lock", "How far to tilt for full steering. Smaller = more sensitive.", 10, 45, 1, 35, () => tiltRange, v => tiltRange = v, v => $"{v:0}°").LockedWhen(tiltOnly);
        con.Choice("Tilt response", "Real-time steers the instant the phone turns. Smooth filters a shaky hand.",
            new[] { "Real-time", "Smooth" }, 0, () => tiltRealtime ? 0 : 1, v => tiltRealtime = v == 0).LockedWhen(tiltOnly);
        con.Choice("Tilt smoothing", "Steadies a shaky hand. More smoothing = a little more delay.", SmoothNames, 1, () => smoothIdx, v => smoothIdx = v)
            .LockedWhen(() => !tilt ? "Used with tilt steering only." : tiltRealtime ? "Used with the Smooth tilt response only." : null);
        con.Choice("Throttle pedal", "Analog: higher on the pedal = more throttle. On / Off: any touch = full.", pedal, 0, () => gasMode, v => gasMode = v);
        con.Choice("Brake pedal", "Analog: higher on the pedal = harder braking. On / Off: any touch = full.", pedal, 0, () => brakeMode, v => brakeMode = v);
        con.Slider("Brake sensitivity", "Braking for the same press. Below 100% a full press brakes less.", 50, 150, 5, 100, () => brakeGainPct, v => brakeGainPct = v, Pct);
        con.Slider("Brake curve", "1.0 is linear. Higher = gentler at the start of the pedal.", 0.5f, 2.5f, 0.05f, 1f, () => brakeCurve, v => brakeCurve = v, v => v.ToString("0.00"));

        // ---- ASSISTS
        var asst = new SettingsUI.Tab("ASSISTS");
        asst.Choice("Difficulty", "Sets rivals, damage and assists. Change one yourself and it becomes Custom.",
            Difficulty.Names, Difficulty.Custom, () => Difficulty.Current, v => { Difficulty.Set(v); SyncDifficulty(); }).NoReset()
            .LockedWhen(() => InRace && InSeasonRound ? "Change it on the career hub." : null);
        asst.Choice("Career restarts", "May a career race be restarted?", new[] { "Off", "On" }, 1,
            () => Difficulty.RestartsAllowed ? 1 : 0, v => Difficulty.CustomRestarts = v == 1).NoReset()
            .HiddenWhen(() => Difficulty.Current != Difficulty.Custom)
            .LockedWhen(() => InRace && InSeasonRound ? "Change it between sessions." : null);
        asst.Choice("Traction control", "Cuts power when the rear wheels spin.", offLowHigh, (int)TcMode.High, () => (int)tc, v => { tc = (TcMode)v; if (car != null) car.cfg.tc = tc; Tweaked(); });
        asst.Toggle("ABS brakes", "Stops the wheels locking under braking.", true, () => absOn, v => { absOn = v; if (car != null) car.cfg.abs = absOn; Tweaked(); });
        asst.Choice("Steering assist", "Helps catch a slide.", offLowHigh, 1, () => steerAssist, v => { steerAssist = v; Tweaked(); });
        asst.Choice("Braking assist", "Brakes for corners for you.", offLowHigh, 1, () => brakeAssist, v => { brakeAssist = v; Tweaked(); })
            .LockedWhen(() => mapIdx == PlaygroundIdx ? "Circuits only: the playground has no racing line." : null);
        asst.Choice("Racing line", "The ideal line: green go, yellow brake soon, red brake.", new[] { "Off", "Braking only", "Full" }, RacingLineView.Full, () => lineMode,
            v => { lineMode = Mathf.Clamp(v, 0, 2); if (lineGo != null) lineGo.SetActive(showLine && !playground); Tweaked(); });

        // ---- CAR SETUP
        var setup = new SettingsUI.Tab("CAR SETUP");
        setup.Choice("Car", "In a session this restarts it with the new car.", CarConfig.ClassNames, 0, () => classIdx, v => { classIdx = v; if (InRace) { BuildLine(); ResetCar(); } }).NoReset().LockedWhen(CareerLock);
        setup.Choice("Tyre compound", "Soft grips most, wears fastest. Inters for damp, Wets for rain. In a race you pick it on the grid before the start; in practice it is fitted at the next restart.", Weather.TyreNames(), 0,
            () => tyreSet, v => { tyreSet = v; compIdx = (int)Weather.PhysicsCompound(v); })
            .LockedWhen(() => InRace && (raceLaps > 0 || qualiSession) ? "Choose at the start or in the pits." : null); // no swap mid-session (TyrePit.cs: the start card, the pit chips)
        setup.Slider("Aero balance", "Downforce on the front. More = sharper turn-in, looser rear.", 44, 54, 0.5f, 45, () => aeroBal, v => { aeroBal = v; SetupChanged(); }, v => $"{v:0.0}% F")
            .LockedWhen(() => classIdx == 0 ? null : "Apex Premier car only.");
        setup.Slider("Brake bias", "Braking on the front. Less = the car turns in more, rears lock sooner.", 50, 65, 0.5f, 57, () => brakeBiasPct, v => { brakeBiasPct = v; SetupChanged(); }, v => $"{v:0.0}% F");
        setup.Slider("Brake migration", "Moves the bias rearwards as you ease off the pedal. Helps turn-in.", 0, 6, 0.5f, 0, () => brakeMigPct, v => { brakeMigPct = v; SetupChanged(); }, v => $"{v:0.0}%");
        setup.Slider("Engine braking", "Rear braking off the throttle. More = more rotation, less stable.", 50, 150, 5, 100, () => engBrakePct, v => { engBrakePct = v; SetupChanged(); }, Pct);
        setup.Slider("Diff lock, on throttle", "More = better drive out of corners, but the car pushes wide or snaps.", 5, 40, 1, 15, () => diffPowerPct, v => { diffPowerPct = v; SetupChanged(); }, Pct);
        setup.Slider("Diff lock, off throttle", "More = steadier into corners, less willing to turn.", 5, 50, 1, 25, () => diffCoastPct, v => { diffCoastPct = v; SetupChanged(); }, Pct);
        setup.Slider("Front ride height", "Lower = more downforce, but the floor can hit the road.", -5, 10, 1, 0, () => rideFmm, v => { rideFmm = v; SetupChanged(); },
            v => $"{BaseCfg(classIdx).rideF0 * 1000 + v:0} mm");
        setup.Slider("Rear ride height", "Lower = more downforce and less drag-free rake. Too low bottoms out.", -10, 15, 1, 0, () => rideRmm, v => { rideRmm = v; SetupChanged(); },
            v => $"{BaseCfg(classIdx).rideR0 * 1000 + v:0} mm");
        // handling test (your car only; tell Claude the numbers that feel real)
        setup.Toggle("Handling test", "Off = the real car. On = the Test sliders change your car.", false, () => handTest, v => { handTest = v; SetupChanged(); });
        setup.Slider("Test: tyre grip", "Handling test, your car only. Grip of all four tyres at every speed. 100% = the real car.", 80, 110, 1, 100, () => gripPct, v => { gripPct = v; SetupChanged(); }, Pct).LockedWhen(() => handTest ? null : "Turn Handling test on first.");
        setup.Slider("Test: rear grip", "Handling test. Less = the rear steps out sooner.", 80, 110, 1, 100, () => rearGripPct, v => { rearGripPct = v; SetupChanged(); }, Pct).LockedWhen(() => handTest ? null : "Turn Handling test on first.");
        setup.Slider("Test: downforce", "Handling test, your car only. 100% = no change.", 60, 110, 1, 100, () => downPct, v => { downPct = v; SetupChanged(); }, Pct).LockedWhen(() => handTest ? null : "Turn Handling test on first.");

        // ---- CAMERA
        var camTab = new SettingsUI.Tab("CAMERA");
        camTab.Choice("Camera", "Also on the Cam button while driving.", new[] { "Chase", "Far", "Cockpit", "Halo", "TV" }, 0, () => camMode, v => camMode = v);
        camTab.Slider("Chase field of view", "Wider shows more and feels faster. For both chase cameras.", 50, 80, 1, 62, () => fovChase, v => fovChase = v, v => $"{v:0}°");
        camTab.Slider("Cockpit field of view", "Wider shows more of the car. The halo camera widens with it.", 45, 80, 1, 55, () => fovCockpit, v => fovCockpit = v, v => $"{v:0}°");
        camTab.Slider("Cockpit seat height", "Raises or lowers the driver's eyes.", -8, 8, 0.5f, 0, () => seatUpCm, v => seatUpCm = v, v => Signed(v, "0.0", " cm"));
        camTab.Slider("Cockpit seat position", "Moves the driver's eyes forward (+) or back (-).", -10, 10, 0.5f, 0, () => seatFwdCm, v => seatFwdCm = v, v => Signed(v, "0.0", " cm"));
        camTab.Slider("Camera shake", "How much the camera moves with braking, cornering, kerbs and bumps.", 0, 200, 10, 100, () => camShakePct, v => camShakePct = v, Pct);
        camTab.Slider("Halo cam: helmet in view", "Moves the halo camera back so your helmet shows at the bottom.", 0, 100, 10, 50, () => haloBackPct, v => haloBackPct = v, Pct);
        camTab.Choice("Halo in cockpit", "Show the halo in the cockpit view, as a real driver sees it, or hide all of it.", new[] { "Hide", "Show" }, 1, () => haloInCockpit ? 1 : 0, v => haloInCockpit = v == 1);
        camTab.Choice("Lens flare", "Sun flare (off on Low graphics).", new[] { "Off", "On" }, 1, () => PlayerPrefs.GetInt(LensFlare.PrefKey, 1), v => PlayerPrefs.SetInt(LensFlare.PrefKey, v));
        camTab.Toggle("Look back button", "LOOK BACK on the left: hold it to see behind you, let go to look ahead (not in the TV view). Key: E.", true, () => LookBackBtn, v => LookBackBtn = v); // GameManagerLookBack.cs
        camTab.Choice("Tilt lock", "Keeps the picture level with your eyes when you tilt the phone, in real time.", new[] { "Off", "Cockpit", "All views" }, 0, () => horizonMode, v => horizonMode = v);

        // ---- GRAPHICS
        var gfx = new SettingsUI.Tab("GRAPHICS");
        gfx.Choice("Graphics level", "Lower is faster. High looks best.", GfxChoice, 1, () => Mathf.Max(0, gfxIdx - 1), v => { gfxIdx = v + 1; FpsForget(); ApplyGraphics(gfxIdx); });
        gfx.Choice("Frame rate", "Auto keeps a steady rate. 30 saves battery.",
            FpsNames, 0, () => fpsIdx, v => { fpsIdx = v; FpsForget(); ApplyFrameRate(); });
        // the base 3D render size (RenderScale.cs): Auto picks ~1.3 Mpx on Android; the Resolution scale below is on top of it
        int[] rsVals = { 0, 100, 85, 70 };
        string[] rsNames = { "Auto (" + Mathf.RoundToInt(RenderScale.AutoScale(RenderScale.NativeW, RenderScale.NativeH) * 100) + "%)", "100%", "85%", "70%" };
        gfx.Choice("3D resolution", "Size of the 3D picture. Auto suits most phones.", rsNames, 0,
            () => System.Math.Max(0, System.Array.IndexOf(rsVals, RenderScale.Pct)), v => { RenderScale.Pct = rsVals[v]; FpsForget(); });
        gfx.Slider("Resolution scale", "Sharpness of the 3D picture (the HUD stays sharp). Lower = faster.", 50, 100, 5, 100, () => resPct, v => { resPct = v; FpsForget(); ApplyResolution(); }, Pct);
        gfx.Toggle("Shadows", "Live shadows from cars and scenery. Off = faster.", true, () => shadowsOn, v => { shadowsOn = v; FpsForget(); ApplyGraphics(gfxLevel); })
            .LockedWhen(() => LightBake.Active ? "This circuit has its shadows baked in: nothing to switch." : gfxLevel <= 1 ? "Shadows are off at the Low graphics level." : null);
        gfx.Toggle("Show FPS", "Frame rate, top right.", true, () => showFps, v => showFps = v);
        gfx.Toggle("Render stats (debug)", "For testing only.", false, () => PerfStats.Show, v => PerfStats.Show = v);
        gfx.Toggle("Performance graph (debug)", "For testing only.", false, () => FrameLog.Show, v => FrameLog.Show = v);

        // ---- AUDIO
        var aud = new SettingsUI.Tab("AUDIO");
        aud.Toggle("Sound", "Everything on or off.", true, () => sound, v => { sound = v; if (audioEngine != null) audioEngine.muted = !sound; });
        aud.Slider("Master volume", "All game sound.", 0, 100, 5, 100, () => volMaster, v => { volMaster = v; ApplyAudio(); }, Pct);
        aud.Slider("Engine volume", "Engine, turbo and the hybrid motor.", 0, 100, 5, 100, () => volEngine, v => { volEngine = v; ApplyAudio(); }, Pct);
        aud.Slider("Tyre volume", "Scrub, squeal, lock-ups and wheelspin.", 0, 100, 5, 100, () => volTyres, v => { volTyres = v; ApplyAudio(); }, Pct);
        aud.Slider("Effects volume", "Wind, kerbs, gravel and grass.", 0, 100, 5, 100, () => volFx, v => { volFx = v; ApplyAudio(); }, Pct);
        aud.Choice("Team radio", "Your race engineer: Full, Key info or Off.",
            TeamRadioNames, 0, () => teamRadioMode, v => { teamRadioMode = Mathf.Clamp(v, 0, 2); PlayerPrefs.SetInt("teamRadio", teamRadioMode); });

        // ---- GAME
        var game = new SettingsUI.Tab("GAME");
        game.Choice("Speed units", "For the speedometer.", new[] { "km/h", "mph" }, 0, () => unitsIdx, v => unitsIdx = v);
        game.Choice("Driver read-out", "Tyres, brakes and fuel beside the steering-wheel display.", hideShow, 1, () => hudInfo ? 1 : 0, v => hudInfo = v == 1);
        game.Choice("Map", "Top right: radar, full map or both.", MapModeNames, MapBoth,
            () => MapMode, v => MapMode = v); // (Radar.cs; PlayerPrefs "hudMapMode2")
        game.Toggle("Timing tower", "Order, gaps and tyres on the left in a race. Tap it for gap / interval, the arrow for all cars.", true, () => hudTower, v => hudTower = v);
        game.Toggle("Sector times", "Three sectors under the lap time: purple = fastest of all, green = your best, yellow = slower.", true, () => hudSectors, v => hudSectors = v);
        game.Toggle("Live delta", "Time up or down on your best lap, live.", true, () => hudDelta, v => hudDelta = v);
        game.Choice("Damage", "Visual only: parts fly off. Full: damage hurts the car.", new[] { "Off", "Visual only", "Full" }, 1, () => InRace && InSeasonRound ? DamageNow : damageMode, v => { damageMode = v; SetupChanged(); Tweaked(); })
            .LockedWhen(() => InRace && InSeasonRound ? "A career round always has damage: Visual on Easy and Medium, Full from Hard." : null);
        game.Choice("Flags & safety car", "Yellow flags, safety car and penalties.", new[] { "Off", "On" }, 1, () => flagsMode, v => flagsMode = v);
        game.Choice("Race start", "Full: garage, pit exit and formation lap. Short: grid only. Off: straight to the lights.",
            PreRaceNames, 0, () => preRaceIdx, v => preRaceIdx = v);
        game.Choice("Post-race", "Full: cool-down lap and podium. Short: podium only. Off: straight to results.",
            PostRaceNames, 0, () => PostRaceMode, v => PostRaceMode = v);
        game.Choice("Story", "Career story: Film, Cards or Off.",
            StoryFilms.ModeNames, StoryFilms.ModeFilm, () => StoryFilms.Mode, v => StoryFilms.Mode = v); // (StoryFilm.cs)
        game.Choice("Race distance", "Menu laps, or a share of a full Grand Prix.", RaceDistNames, 0, () => raceDistIdx, v => raceDistIdx = v);
        game.Toggle("Vibration", "Kerbs, rough ground and wall hits shake the phone.", true, () => Haptics.enabled, v => { Haptics.enabled = v; if (v) Haptics.Pulse(40, 160); });
        var liveryNames = new string[Livery.Player.Length]; for (int i = 0; i < liveryNames.Length; i++) liveryNames[i] = Livery.Player[i].name;
        game.Choice("Livery", "Your car's colours.", liveryNames, 0, () => liveryIdx, v => { liveryIdx = v; if (model != null) model.SetLivery(Livery.Player[liveryIdx]); }).NoReset();
        game.Button("Restart", "Start this session again from the grid.", () => "Restart session", () => { ResetCar(); CloseSettings(); }).HiddenWhen(() => !InRace || !RestartOk);
        game.Button("Leave", "End this session and go to the start menu. Career: leaving qualifying = no time (back of the grid); leaving a race on Hard or above = retired.", () => "Back to menu", () => { CloseSettings(); ui.page = MenuUI.Page.Start; }).HiddenWhen(() => !InRace);
        game.Button("Reset progress (debug)", "Level, XP, safety rating and career stats back to a new driver. Tap twice. For testing only.", ProgressResetLabel, ProgressResetTap).HiddenWhen(() => InRace);

        return new List<SettingsUI.Tab> { con, asst, setup, camTab, gfx, aud, game };
    }
}
