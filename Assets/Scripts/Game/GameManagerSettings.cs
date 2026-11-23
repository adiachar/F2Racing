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
    float steerGainPct = 100, steerCurve = 1, steerLockPct = 100, steerRate = 14, steerRet = 18;
    float tiltDz = 0.5f, tiltRange = 20;                 // degrees
    int gasMode, brakeMode;                              // 0 analog (height on the pedal), 1 on/off
    float brakeGainPct = 100, brakeCurve = 1;
    // assists
    int steerAssist, brakeAssist;                        // 0 off, 1 low, 2 high
    // car set-up
    float aeroBal = 45, brakeBiasPct = 57, brakeMigPct, engBrakePct = 100, diffPowerPct = 15, diffCoastPct = 25, rideFmm, rideRmm;
    // camera
    float fovChase = 62, fovCockpit = 55, seatUpCm, seatFwdCm, camShakePct = 100;
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

    void LoadSettings()
    {
        steerGainPct = LoadF("steerGain", 100, 50, 200); steerCurve = LoadF("steerCurve", 1, 1, 2.5f); steerLockPct = LoadF("steerLock", 100, 50, 100);
        steerRate = LoadF("steerRate", 14, 4, 30); steerRet = LoadF("steerRet", 18, 4, 30);
        tiltDz = LoadOld("tiltDzF", "tiltDz", OldDeadZones, 1, 0, 3); tiltRange = LoadOld("tiltRangeF", "tiltRange", OldTiltRanges, 2, 10, 35);
        tiltRealtime = PlayerPrefs.GetInt("tiltRT", 1) == 1; // Real-time tilt response by default
        gasMode = LoadI("gasMode", 0, 1); brakeMode = LoadI("brakeMode", 0, 1);
        brakeGainPct = LoadF("brakeGain", 100, 50, 150); brakeCurve = LoadF("brakeCurve", 1, 0.5f, 2.5f);
        steerAssist = LoadI("steerAssist", 0, 2); brakeAssist = LoadI("brakeAssist", 0, 2);
        aeroBal = LoadOld("balF", "bal", OldBalances, 0, 44, 54); brakeBiasPct = LoadOld("brakeBiasF", "brakeBias", OldBiases, 2, 50, 65);
        brakeMigPct = LoadF("brakeMig", 0, 0, 6); engBrakePct = LoadF("engBrake", 100, 50, 150);
        diffPowerPct = LoadF("diffPower", 15, 5, 40); diffCoastPct = LoadF("diffCoast", 25, 5, 50);
        rideFmm = LoadF("rideF", 0, -5, 10); rideRmm = LoadF("rideR", 0, -10, 15);
        fovChase = LoadF("fovChase", 62, 50, 80); fovCockpit = LoadF("fovCockpit", 55, 45, 80);
        seatUpCm = LoadF("seatUp", 0, -8, 8); seatFwdCm = LoadF("seatFwd", 0, -10, 10); camShakePct = LoadF("camShake", 100, 0, 200);
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
        PlayerPrefs.SetFloat("steerRate", steerRate); PlayerPrefs.SetFloat("steerRet", steerRet);
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
        PlayerPrefs.SetFloat("fovChase", fovChase); PlayerPrefs.SetFloat("fovCockpit", fovCockpit);
        PlayerPrefs.SetFloat("seatUp", seatUpCm); PlayerPrefs.SetFloat("seatFwd", seatFwdCm); PlayerPrefs.SetFloat("camShake", camShakePct);
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
    }
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
        con.Slider("Steering sensitivity", "How far the wheel turns for the same drag or tilt.", 50, 200, 5, 100, () => steerGainPct, v => steerGainPct = v, Pct);
        con.Slider("Steering curve", "1.0 is linear. Higher = finer control near the centre, same full lock.", 1f, 2.5f, 0.05f, 1f, () => steerCurve, v => steerCurve = v, v => v.ToString("0.00"));
        con.Slider("Maximum steering lock", "Limits how far the front wheels can turn.", 50, 100, 1, 100, () => steerLockPct, v => { steerLockPct = v; SetupChanged(); },
            v => $"{v:0}% · {BaseCfg(classIdx).maxLockDeg * v * 0.01:0}°");
        con.Slider("Steering speed", "How fast the wheel turns: time from straight to full lock.", 4, 30, 1, 14, () => steerRate, v => { steerRate = v; SetupChanged(); }, v => $"{1000f / v:0} ms");
        con.Slider("Return to centre", "How fast the wheel comes back when you let go: time from full lock.", 4, 30, 1, 18, () => steerRet, v => { steerRet = v; SetupChanged(); }, v => $"{1000f / v:0} ms");
        con.Choice("Steering at speed", "Less steering lock as the speed rises, for a calmer car on the straights.", new[] { "Off", "Quick", "Normal", "Calm" }, 2,
            () => speedSteerIdx, v => { speedSteerIdx = v; if (car != null) car.cfg.speedSens = SpeedSteer[speedSteerIdx]; });
        con.Choice("Tilt direction", "Swap left and right if the car steers the wrong way.", new[] { "Normal", "Inverted" }, 0, () => invertTilt ? 1 : 0, v => invertTilt = v == 1).LockedWhen(tiltOnly);
        con.Button("Tilt centre", "Hold the phone the way you want \"straight\" to be, then tap.",
            () => Time.realtimeSinceStartup - centreSetT < 1.5f ? "Centre set" : "Set straight now", () => { tiltCenter = TiltRaw(); centreSetT = Time.realtimeSinceStartup; }).LockedWhen(tiltOnly);
        con.Slider("Tilt dead zone", "Tilt around the centre that is ignored, so the car runs straight.", 0, 3, 0.1f, 0.5f, () => tiltDz, v => tiltDz = v, v => $"{v:0.0}°").LockedWhen(tiltOnly);
        con.Slider("Tilt for full lock", "How far to tilt for full steering. Smaller = more sensitive.", 10, 35, 1, 20, () => tiltRange, v => tiltRange = v, v => $"{v:0}°").LockedWhen(tiltOnly);
        con.Choice("Tilt response", "Real-time: the car steers the instant the phone turns; only a still hand's tremor is filtered. Smooth: filtered (Tilt smoothing).",
            new[] { "Real-time", "Smooth" }, 0, () => tiltRealtime ? 0 : 1, v => tiltRealtime = v == 0).LockedWhen(tiltOnly);
        con.Choice("Tilt smoothing", "Steadies a shaky hand. More smoothing = a little more delay.", SmoothNames, 1, () => smoothIdx, v => smoothIdx = v)
            .LockedWhen(() => !tilt ? "Used with tilt steering only." : tiltRealtime ? "Used with the Smooth tilt response only." : null);
        con.Choice("Throttle pedal", "Analog: higher on the pedal = more throttle. On / Off: any touch = full.", pedal, 0, () => gasMode, v => gasMode = v);
        con.Choice("Brake pedal", "Analog: higher on the pedal = harder braking. On / Off: any touch = full.", pedal, 0, () => brakeMode, v => brakeMode = v);
        con.Slider("Brake sensitivity", "Braking for the same press. Below 100% a full press brakes less.", 50, 150, 5, 100, () => brakeGainPct, v => brakeGainPct = v, Pct);
        con.Slider("Brake curve", "1.0 is linear. Higher = gentler at the start of the pedal.", 0.5f, 2.5f, 0.05f, 1f, () => brakeCurve, v => brakeCurve = v, v => v.ToString("0.00"));

        // ---- ASSISTS
        var asst = new SettingsUI.Tab("ASSISTS");
        asst.Choice("Difficulty", "Like GRID: Easy, Medium, Hard, Pro and Master set the rival level, damage, these assists and the racing line, and whether a career race can be restarted (Easy and Medium only; qualifying never). Harder = more XP: Easy \u00d70.8, Medium \u00d71, Hard \u00d71.25, Pro \u00d71.5, Master \u00d72. Change any of them yourself and it becomes Custom.",
            Difficulty.Names, Difficulty.Custom, () => Difficulty.Current, v => { Difficulty.Set(v); SyncDifficulty(); }).NoReset()
            .LockedWhen(() => InRace && InSeasonRound ? "Change it on the career hub between sessions." : null);
        asst.Choice("Career restarts", "Custom difficulty: may a career race be restarted (never qualifying)? The presets decide it themselves.", new[] { "Off", "On" }, 1,
            () => Difficulty.RestartsAllowed ? 1 : 0, v => Difficulty.CustomRestarts = v == 1).NoReset()
            .LockedWhen(() => Difficulty.Current != Difficulty.Custom ? "Set by the difficulty preset." : InRace && InSeasonRound ? "Change it between sessions." : null);
        asst.Choice("Traction control", "Cuts power when the rear wheels spin. Not allowed in top-tier racing.", offLowHigh, (int)TcMode.Low, () => (int)tc, v => { tc = (TcMode)v; if (car != null) car.cfg.tc = tc; Tweaked(); });
        asst.Toggle("ABS brakes", "Stops the wheels locking under braking. Not allowed in top-tier racing.", true, () => absOn, v => { absOn = v; if (car != null) car.cfg.abs = absOn; Tweaked(); });
        asst.Choice("Steering assist", "Steers into a slide for you when the rear steps out.", offLowHigh, 0, () => steerAssist, v => { steerAssist = v; Tweaked(); });
        asst.Choice("Braking assist", "Brakes for corners by the racing line's speed. Low brakes later and softer.", offLowHigh, 0, () => brakeAssist, v => { brakeAssist = v; Tweaked(); })
            .LockedWhen(() => mapIdx == PlaygroundIdx ? "Circuits only: the playground has no racing line." : null);
        asst.Choice("Racing line", "Chevrons on the ideal line, coloured live for your speed: green = no braking needed, yellow = brake soon, red = brake here.", new[] { "Off", "Braking only", "Full" }, RacingLineView.Full, () => lineMode,
            v => { lineMode = Mathf.Clamp(v, 0, 2); if (lineGo != null) lineGo.SetActive(showLine && !playground); Tweaked(); });

        // ---- CAR SETUP
        var setup = new SettingsUI.Tab("CAR SETUP");
        setup.Choice("Car", "In a session this restarts it with the new car.", CarConfig.ClassNames, 0, () => classIdx, v => { classIdx = v; if (InRace) { BuildLine(); ResetCar(); } }).NoReset().LockedWhen(CareerLock);
        setup.Choice("Tyre compound", "Soft grips most, wears fastest. Inters for damp, Wets for rain. Fitted at the next restart.", Weather.TyreNames(), 0,
            () => tyreSet, v => { tyreSet = v; compIdx = (int)Weather.PhysicsCompound(v); });
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

        // ---- CAMERA
        var camTab = new SettingsUI.Tab("CAMERA");
        camTab.Choice("Camera", "Also on the Cam button while driving.", new[] { "Chase", "Far", "Cockpit", "Halo", "TV" }, 0, () => camMode, v => camMode = v);
        camTab.Slider("Chase field of view", "Wider shows more and feels faster. For both chase cameras.", 50, 80, 1, 62, () => fovChase, v => fovChase = v, v => $"{v:0}°");
        camTab.Slider("Cockpit field of view", "Wider shows more of the car. The halo camera widens with it.", 45, 80, 1, 55, () => fovCockpit, v => fovCockpit = v, v => $"{v:0}°");
        camTab.Slider("Cockpit seat height", "Raises or lowers the driver's eyes.", -8, 8, 0.5f, 0, () => seatUpCm, v => seatUpCm = v, v => Signed(v, "0.0", " cm"));
        camTab.Slider("Cockpit seat position", "Moves the driver's eyes forward (+) or back (-).", -10, 10, 0.5f, 0, () => seatFwdCm, v => seatFwdCm = v, v => Signed(v, "0.0", " cm"));
        camTab.Slider("Camera shake", "How much the camera moves with braking, cornering, kerbs and bumps.", 0, 200, 10, 100, () => camShakePct, v => camShakePct = v, Pct);
        camTab.Choice("Halo in cockpit", "Show the halo in the cockpit view, as a real driver sees it, or hide all of it.", new[] { "Hide", "Show" }, 1, () => haloInCockpit ? 1 : 0, v => haloInCockpit = v == 1);
        camTab.Choice("Lens flare", "Sun flare with ghosts and an anamorphic streak when you look toward the sun (off on Low graphics).", new[] { "Off", "On" }, 1, () => PlayerPrefs.GetInt(LensFlare.PrefKey, 1), v => PlayerPrefs.SetInt(LensFlare.PrefKey, v));
        camTab.Choice("Tilt lock", "Keeps the picture level with your eyes when you tilt the phone, in real time.", new[] { "Off", "Cockpit", "All views" }, 0, () => horizonMode, v => horizonMode = v);

        // ---- GRAPHICS
        var gfx = new SettingsUI.Tab("GRAPHICS");
        gfx.Choice("Graphics level", "Auto starts at Medium and steps down if the phone cannot keep up.", GfxNames, 0, () => gfxIdx, v => { gfxIdx = v; FpsForget(); ApplyGraphics(gfxIdx == 0 ? 2 : gfxIdx); });
        gfx.Choice("Frame rate", "Always a steady rate the screen divides evenly. Auto: 60 fps, a steady 30 if the phone can't hold 60 or gets hot (40 only on a screen that really runs at 120 Hz). 40 needs 120 Hz: on a 60 Hz screen it runs at 30. 30 saves battery and heat.",
            FpsNames, 0, () => fpsIdx, v => { fpsIdx = v; FpsForget(); ApplyFrameRate(); });
        // the base 3D render size (RenderScale.cs): Auto picks ~1.3 Mpx on Android; the Resolution scale below is on top of it
        int[] rsVals = { 0, 100, 85, 70 };
        string[] rsNames = { "Auto (" + Mathf.RoundToInt(RenderScale.AutoScale(RenderScale.NativeW, RenderScale.NativeH) * 100) + "%)", "100%", "85%", "70%" };
        gfx.Choice("3D resolution", "Base size of the 3D picture against the screen (the HUD stays sharp). Auto suits most phones.", rsNames, 0,
            () => System.Math.Max(0, System.Array.IndexOf(rsVals, RenderScale.Pct)), v => { RenderScale.Pct = rsVals[v]; FpsForget(); });
        gfx.Slider("Resolution scale", "Sharpness of the 3D picture (the HUD stays sharp). Lower = faster.", 50, 100, 5, 100, () => resPct, v => { resPct = v; FpsForget(); ApplyResolution(); }, Pct);
        gfx.Toggle("Shadows", "Live shadows from cars and scenery. Off = faster.", true, () => shadowsOn, v => { shadowsOn = v; FpsForget(); ApplyGraphics(gfxLevel); })
            .LockedWhen(() => LightBake.Active ? "This circuit has its shadows baked in: nothing to switch." : gfxLevel <= 1 ? "Shadows are off at the Low graphics level." : null);
        gfx.Toggle("Show FPS", "The frame-rate lock (\"40 FPS LOCK\"; the measured rate beside it while it doesn't hold) and the resolution share when it is lowered, top right under the buttons.", true, () => showFps, v => showFps = v);
        gfx.Toggle("Render stats (debug)", "Draw calls, triangles, culling sectors, card overdraw and memory, top left. For testing only.", false, () => PerfStats.Show, v => PerfStats.Show = v);
        gfx.Toggle("Performance graph (debug)", "Frame times of the last 5 s, CPU / GPU ms, garbage collections, physics, phone heat and clocks, top left; a 2-minute table on the race results. For testing only.", false, () => FrameLog.Show, v => FrameLog.Show = v);

        // ---- AUDIO
        var aud = new SettingsUI.Tab("AUDIO");
        aud.Toggle("Sound", "Everything on or off.", true, () => sound, v => { sound = v; if (audioEngine != null) audioEngine.muted = !sound; });
        aud.Slider("Master volume", "All game sound.", 0, 100, 5, 100, () => volMaster, v => { volMaster = v; ApplyAudio(); }, Pct);
        aud.Slider("Engine volume", "Engine, turbo and the hybrid motor.", 0, 100, 5, 100, () => volEngine, v => { volEngine = v; ApplyAudio(); }, Pct);
        aud.Slider("Tyre volume", "Scrub, squeal, lock-ups and wheelspin.", 0, 100, 5, 100, () => volTyres, v => { volTyres = v; ApplyAudio(); }, Pct);
        aud.Slider("Effects volume", "Wind, kerbs, gravel and grass.", 0, 100, 5, 100, () => volFx, v => { volFx = v; ApplyAudio(); }, Pct);
        aud.Choice("Team radio", "Your race engineer. Full: gaps, tyres, pace and strategy. Key info: only what matters (pit calls, flags, damage, Overtake Mode). Off: silent.",
            TeamRadioNames, 0, () => teamRadioMode, v => { teamRadioMode = Mathf.Clamp(v, 0, 2); PlayerPrefs.SetInt("teamRadio", teamRadioMode); });

        // ---- GAME
        var game = new SettingsUI.Tab("GAME");
        game.Choice("Track", "In a session this loads the circuit and restarts.", MapNames, 0, () => mapIdx, v => { mapIdx = v; if (InRace) { LoadMap(); ResetCar(); } }).NoReset().LockedWhen(CareerLock);
        var rivalNames = new string[RivalCounts.Length]; for (int i = 0; i < rivalNames.Length; i++) rivalNames[i] = RivalCounts[i] == 0 ? "None" : RivalCounts[i].ToString();
        game.Choice("Rivals", "Cars to race against. In a race this restarts it.", rivalNames, 1, () => rivalIdx, v => { rivalIdx = v; if (InRace) ResetCar(); }).NoReset().LockedWhen(CareerLock);
        game.Choice("Rival level", "How fast the rivals drive. In a race this restarts it.", DiffNames, 0, () => diffIdx, v => { diffIdx = v; Tweaked(); if (InRace) ResetCar(); }).NoReset().LockedWhen(CareerLock);
        game.Choice("Grid position", "Where you start a race: last, random, pole or a set place. In a race this restarts it.", MenuUI.GridNames, 0, () => ui.gridIdx, v => { ui.SetGrid(v); if (InRace) ResetCar(); }).NoReset().LockedWhen(CareerLock);
        game.Choice("Weather", "Damp: a drying line, less grip off it. Wet: rain, spray, much less grip. Rivals fit the right tyres at the next restart.", Weather.Names, 0,
            () => Weather.Index, v => { Weather.Index = v; ApplyWeather(); });
        game.Choice("Time of day", "Sun height and colour, sky and shadows. Night is for street circuits (floodlit); elsewhere it is evening. In a session this reloads the circuit.", TimeOfDay.Names, TimeOfDay.Default,
            () => TimeOfDay.Index, v => { TimeOfDay.Set(v, InRace); if (InRace) { LoadMap(); ResetCar(); } }).NoReset().LockedWhen(CareerLock);
        game.Choice("Speed units", "For the speedometer.", new[] { "km/h", "mph" }, 0, () => unitsIdx, v => unitsIdx = v);
        game.Choice("Driver read-out", "Tyres, brakes and fuel beside the steering-wheel display.", hideShow, 1, () => hudInfo ? 1 : 0, v => hudInfo = v == 1);
        game.Choice("Mini-map", "Local: the track round your car (300 m ahead), turned to your heading, with the cars near you. Top right.", new[] { "Local", "Off" }, 0,
            () => hudMap ? 0 : 1, v => hudMap = v == 0);
        game.Toggle("Timing tower", "Order, gaps and tyres on the left in a race. Tap it for gap / interval, the arrow for all cars.", true, () => hudTower, v => hudTower = v);
        game.Toggle("Sector times", "Three sectors under the lap time: purple = fastest of all, green = your best, yellow = slower.", true, () => hudSectors, v => hudSectors = v);
        game.Toggle("Live delta", "Time up or down on your best lap, live.", true, () => hudDelta, v => hudDelta = v);
        game.Choice("Damage", "Off: none. Visual only: parts break and fly off, the car drives the same. Full: lost downforce, bent steering, punctures; BOX fits a new wing (+8 s). Rivals follow at the next restart.", new[] { "Off", "Visual only", "Full" }, 1, () => InRace && InSeasonRound ? DamageNow : damageMode, v => { damageMode = v; SetupChanged(); Tweaked(); })
            .LockedWhen(() => InRace && InSeasonRound ? "A career round always has damage: Visual on Easy and Medium, Full from Hard." : null);
        game.Choice("Flags & safety car", "On: yellow flags, Virtual Safety Car and Safety Car after crashes, blue flags and penalties: track limits (3 warnings, then +5 s), +5 / +10 s for causing a collision, +5 s for a place gained off track and not given back. Fitted at the next restart.", new[] { "Off", "On" }, 1, () => flagsMode, v => flagsMode = v);
        game.Choice("Race start", "Full: like F1. You walk to your car in the garage, leave the pit lane, then a formation lap to warm your tyres (keep your place; SKIP TO START ends it), then the lights. Short: engine start on the grid, then the lights. Off: straight to the lights. A tap skips the film. From the next race.",
            PreRaceNames, 0, () => preRaceIdx, v => preRaceIdx = v);
        game.Choice("Post-race", "Full: after the flag a slow-motion finish, a cool-down lap (the car drives itself, TV cameras, the classification), the podium with the cup and champagne, then your XP, level and safety rating. Short: the finish, the podium and your progress. Off: straight to the results. A tap skips ahead.",
            PostRaceNames, 0, () => PostRaceMode, v => PostRaceMode = v);
        game.Choice("Race distance", "Menu laps: the laps chosen on the start menu. Or a share of a full race weekend distance (305 km): the rivals plan real one- and two-stop strategies from 10 laps. From the next race.", RaceDistNames, 0, () => raceDistIdx, v => raceDistIdx = v);
        game.Toggle("Vibration", "Kerbs, rough ground and wall hits shake the phone.", true, () => Haptics.enabled, v => { Haptics.enabled = v; if (v) Haptics.Pulse(40, 160); });
        var liveryNames = new string[Livery.Player.Length]; for (int i = 0; i < liveryNames.Length; i++) liveryNames[i] = Livery.Player[i].name;
        game.Choice("Livery", "Your car's colours.", liveryNames, 0, () => liveryIdx, v => { liveryIdx = v; if (model != null) model.SetLivery(Livery.Player[liveryIdx]); }).NoReset();
        game.Button("Restart", "Start this session again from the grid.", () => "Restart session", () => { ResetCar(); CloseSettings(); }).HiddenWhen(() => !InRace || !RestartOk);
        game.Button("Leave", "End this session and go to the start menu. Career: leaving qualifying = no time (back of the grid); leaving a race on Hard or above = retired.", () => "Back to menu", () => { CloseSettings(); ui.page = MenuUI.Page.Start; }).HiddenWhen(() => !InRace);
        game.Button("Reset progress (debug)", "Level, XP, safety rating and career stats back to a new driver. Tap twice. For testing only.", ProgressResetLabel, ProgressResetTap).HiddenWhen(() => InRace);

        return new List<SettingsUI.Tab> { con, asst, setup, camTab, gfx, aud, game };
    }
}
