// GameManagerSettings.cs — the settings half of GameManager (a partial class; the component itself is GameManager.cs):
//  * the settings the tabbed settings screen added, with their PlayerPrefs keys, loading and saving
//  * the rows of the settings screen (SettingsUI.cs draws them): CONTROLS, ASSISTS, CAR SETUP, CAMERA, GRAPHICS,
//    AUDIO, GAME
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
    int horizonMode = 1;                                 // 0 off, 1 cockpit only, 2 all views
    // graphics
    int fpsIdx = 1; float resPct = 100; bool shadowsOn = true, showFps = true;
    // audio (percent)
    float volMaster = 100, volEngine = 100, volFx = 100, volTyres = 100;
    // game
    int unitsIdx; bool hudInfo = true, hudMap = true;

    float steerGain => steerGainPct * 0.01f;
    float FpsTarget => fpsIdx == 0 ? 30f : 60f;
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
        gasMode = LoadI("gasMode", 0, 1); brakeMode = LoadI("brakeMode", 0, 1);
        brakeGainPct = LoadF("brakeGain", 100, 50, 150); brakeCurve = LoadF("brakeCurve", 1, 0.5f, 2.5f);
        steerAssist = LoadI("steerAssist", 0, 2); brakeAssist = LoadI("brakeAssist", 0, 2);
        aeroBal = LoadOld("balF", "bal", OldBalances, 0, 44, 54); brakeBiasPct = LoadOld("brakeBiasF", "brakeBias", OldBiases, 2, 50, 65);
        brakeMigPct = LoadF("brakeMig", 0, 0, 6); engBrakePct = LoadF("engBrake", 100, 50, 150);
        diffPowerPct = LoadF("diffPower", 15, 5, 40); diffCoastPct = LoadF("diffCoast", 25, 5, 50);
        rideFmm = LoadF("rideF", 0, -5, 10); rideRmm = LoadF("rideR", 0, -10, 15);
        fovChase = LoadF("fovChase", 62, 50, 80); fovCockpit = LoadF("fovCockpit", 55, 45, 80);
        seatUpCm = LoadF("seatUp", 0, -8, 8); seatFwdCm = LoadF("seatFwd", 0, -10, 10); camShakePct = LoadF("camShake", 100, 0, 200);
        horizonMode = LoadI("horizon", 1, 2);
        fpsIdx = LoadI("fps", 1, 1); resPct = LoadF("resScale", 100, 50, 100); shadowsOn = PlayerPrefs.GetInt("shadows", 1) == 1; showFps = PlayerPrefs.GetInt("showFps", 1) == 1;
        volMaster = LoadF("volMaster", 100, 0, 100); volEngine = LoadF("volEngine", 100, 0, 100); volFx = LoadF("volFx", 100, 0, 100); volTyres = LoadF("volTyres", 100, 0, 100);
        unitsIdx = LoadI("units", 0, 1); hudInfo = PlayerPrefs.GetInt("hudInfo", 1) == 1; hudMap = PlayerPrefs.GetInt("hudMap", 1) == 1;
    }

    void SaveSettings()
    {
        PlayerPrefs.SetFloat("steerGain", steerGainPct); PlayerPrefs.SetFloat("steerCurve", steerCurve); PlayerPrefs.SetFloat("steerLock", steerLockPct);
        PlayerPrefs.SetFloat("steerRate", steerRate); PlayerPrefs.SetFloat("steerRet", steerRet);
        PlayerPrefs.SetFloat("tiltDzF", tiltDz); PlayerPrefs.SetInt("tiltDz", Nearest(OldDeadZones, tiltDz));
        PlayerPrefs.SetFloat("tiltRangeF", tiltRange); PlayerPrefs.SetInt("tiltRange", Nearest(OldTiltRanges, tiltRange));
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
        PlayerPrefs.SetInt("horizon", horizonMode);
        PlayerPrefs.SetInt("fps", fpsIdx); PlayerPrefs.SetFloat("resScale", resPct); PlayerPrefs.SetInt("shadows", shadowsOn ? 1 : 0); PlayerPrefs.SetInt("showFps", showFps ? 1 : 0);
        PlayerPrefs.SetFloat("volMaster", volMaster); PlayerPrefs.SetFloat("volEngine", volEngine); PlayerPrefs.SetFloat("volFx", volFx); PlayerPrefs.SetFloat("volTyres", volTyres);
        PlayerPrefs.SetInt("units", unitsIdx); PlayerPrefs.SetInt("hudInfo", hudInfo ? 1 : 0); PlayerPrefs.SetInt("hudMap", hudMap ? 1 : 0);
    }

    // ---------------------------------------------------------------- applying
    // Set-up values into a car's config. Ride heights and steering lock are offsets from the class's own design.
    void ApplySetup(CarConfig c)
    {
        var b = BaseCfg(cfgClass);
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
    void ApplyFrameRate() { Application.targetFrameRate = (int)FpsTarget; fpsAvg = FpsTarget; scaleT = 0; slowT = 0; }
    void ApplyResolution()
    {
        float ns = ResMax; scaleT = 0;
        if (!Mathf.Approximately(ns, renderScale)) { renderScale = ns; ScalableBufferManager.ResizeBuffers(ns, ns); }
    }
    void ApplyDisplay() { ApplyFrameRate(); ApplyResolution(); }

    // ---------------------------------------------------------------- steering and brake shaping, driver aids
    // touch and tilt steering: the sensitivity is applied where the input is measured (so full lock stays
    // reachable); this bends the response: exponent 1 = linear, higher = finer near the centre
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
        if (rivals.Count > 0 && raceTime < Countdown) return 0;
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
        con.Choice("Tilt smoothing", "Steadies a shaky hand. More smoothing = a little more delay.", SmoothNames, 1, () => smoothIdx, v => smoothIdx = v).LockedWhen(tiltOnly);
        con.Choice("Throttle pedal", "Analog: higher on the pedal = more throttle. On / Off: any touch = full.", pedal, 0, () => gasMode, v => gasMode = v);
        con.Choice("Brake pedal", "Analog: higher on the pedal = harder braking. On / Off: any touch = full.", pedal, 0, () => brakeMode, v => brakeMode = v);
        con.Slider("Brake sensitivity", "Braking for the same press. Below 100% a full press brakes less.", 50, 150, 5, 100, () => brakeGainPct, v => brakeGainPct = v, Pct);
        con.Slider("Brake curve", "1.0 is linear. Higher = gentler at the start of the pedal.", 0.5f, 2.5f, 0.05f, 1f, () => brakeCurve, v => brakeCurve = v, v => v.ToString("0.00"));

        // ---- ASSISTS
        var asst = new SettingsUI.Tab("ASSISTS");
        asst.Choice("Traction control", "Cuts power when the rear wheels spin. Real F1 has none.", offLowHigh, (int)TcMode.Low, () => (int)tc, v => { tc = (TcMode)v; if (car != null) car.cfg.tc = tc; });
        asst.Toggle("ABS brakes", "Stops the wheels locking under braking. Real F1 has none.", true, () => absOn, v => { absOn = v; if (car != null) car.cfg.abs = absOn; });
        asst.Choice("Steering assist", "Steers into a slide for you when the rear steps out.", offLowHigh, 0, () => steerAssist, v => steerAssist = v);
        asst.Choice("Braking assist", "Brakes for corners by the racing line's speed. Low brakes later and softer.", offLowHigh, 0, () => brakeAssist, v => brakeAssist = v)
            .LockedWhen(() => mapIdx == PlaygroundIdx ? "Circuits only: the playground has no racing line." : null);
        asst.Choice("Racing line", "The ideal line on the road: green = go, red = brake.", new[] { "Hidden", "Shown" }, 0, () => showLine ? 1 : 0,
            v => { showLine = v == 1; if (lineGo != null) lineGo.SetActive(showLine && !playground); });

        // ---- CAR SETUP
        var setup = new SettingsUI.Tab("CAR SETUP");
        setup.Choice("Car", "In a session this restarts it with the new car.", CarConfig.ClassNames, 0, () => classIdx, v => { classIdx = v; if (InRace) { BuildLine(); ResetCar(); } }).NoReset();
        var tyreNames = new string[CompoundTable.Defs.Length]; for (int i = 0; i < tyreNames.Length; i++) tyreNames[i] = CompoundTable.Defs[i].name;
        setup.Choice("Tyre compound", "Soft grips most and wears fastest. Fitted at the next restart.", tyreNames, 0, () => compIdx, v => compIdx = v);
        setup.Slider("Aero balance", "Downforce on the front. More = sharper turn-in, looser rear.", 44, 54, 0.5f, 45, () => aeroBal, v => { aeroBal = v; SetupChanged(); }, v => $"{v:0.0}% F")
            .LockedWhen(() => classIdx == 0 ? null : "F1 2026 car only.");
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
        camTab.Choice("Horizon lock", "With tilt steering: turns the picture against the phone's tilt.", new[] { "Off", "Cockpit", "All views" }, 1, () => horizonMode, v => horizonMode = v);

        // ---- GRAPHICS
        var gfx = new SettingsUI.Tab("GRAPHICS");
        gfx.Choice("Graphics level", "Auto starts at Medium and steps down if the phone cannot keep up.", GfxNames, 0, () => gfxIdx, v => { gfxIdx = v; ApplyGraphics(gfxIdx == 0 ? 2 : gfxIdx); });
        gfx.Choice("Frame rate", "30 fps saves battery and heat; 60 fps is smoother.", new[] { "30 fps", "60 fps" }, 1, () => fpsIdx, v => { fpsIdx = v; ApplyFrameRate(); });
        gfx.Slider("Resolution scale", "Sharpness of the 3D picture (the HUD stays sharp). Lower = faster.", 50, 100, 5, 100, () => resPct, v => { resPct = v; ApplyResolution(); }, Pct);
        gfx.Toggle("Shadows", "Live shadows from cars and scenery. Off = faster.", true, () => shadowsOn, v => { shadowsOn = v; ApplyGraphics(gfxLevel); })
            .LockedWhen(() => LightBake.Active ? "This circuit has its shadows baked in: nothing to switch." : gfxLevel <= 1 ? "Shadows are off at the Low graphics level." : null);
        gfx.Toggle("Show FPS", "Frames per second in the top-left corner.", true, () => showFps, v => showFps = v);

        // ---- AUDIO
        var aud = new SettingsUI.Tab("AUDIO");
        aud.Toggle("Sound", "Everything on or off.", true, () => sound, v => { sound = v; if (audioEngine != null) audioEngine.muted = !sound; });
        aud.Slider("Master volume", "All game sound.", 0, 100, 5, 100, () => volMaster, v => { volMaster = v; ApplyAudio(); }, Pct);
        aud.Slider("Engine volume", "Engine, turbo and the hybrid motor.", 0, 100, 5, 100, () => volEngine, v => { volEngine = v; ApplyAudio(); }, Pct);
        aud.Slider("Tyre volume", "Scrub, squeal, lock-ups and wheelspin.", 0, 100, 5, 100, () => volTyres, v => { volTyres = v; ApplyAudio(); }, Pct);
        aud.Slider("Effects volume", "Wind, kerbs, gravel and grass.", 0, 100, 5, 100, () => volFx, v => { volFx = v; ApplyAudio(); }, Pct);

        // ---- GAME
        var game = new SettingsUI.Tab("GAME");
        game.Choice("Track", "In a session this loads the circuit and restarts.", MapNames, 0, () => mapIdx, v => { mapIdx = v; if (InRace) { LoadMap(); ResetCar(); } }).NoReset();
        var rivalNames = new string[RivalCounts.Length]; for (int i = 0; i < rivalNames.Length; i++) rivalNames[i] = RivalCounts[i] == 0 ? "None" : RivalCounts[i].ToString();
        game.Choice("Rivals", "Cars to race against. In a race this restarts it.", rivalNames, 1, () => rivalIdx, v => { rivalIdx = v; if (InRace) ResetCar(); }).NoReset();
        game.Choice("Rival level", "How fast the rivals drive. In a race this restarts it.", DiffNames, 0, () => diffIdx, v => { diffIdx = v; if (InRace) ResetCar(); }).NoReset();
        game.Choice("Speed units", "For the speedometer.", new[] { "km/h", "mph" }, 0, () => unitsIdx, v => unitsIdx = v);
        game.Choice("Driver read-out", "Grip, tyres, brakes and fuel in the top-left corner.", hideShow, 1, () => hudInfo ? 1 : 0, v => hudInfo = v == 1);
        game.Choice("Mini-map", "The circuit map in the top-right corner.", hideShow, 1, () => hudMap ? 1 : 0, v => hudMap = v == 1);
        game.Toggle("Vibration", "Kerbs, rough ground and wall hits shake the phone.", true, () => Haptics.enabled, v => { Haptics.enabled = v; if (v) Haptics.Pulse(40, 160); });
        var liveryNames = new string[Livery.Player.Length]; for (int i = 0; i < liveryNames.Length; i++) liveryNames[i] = Livery.Player[i].name;
        game.Choice("Livery", "Your car's colours.", liveryNames, 0, () => liveryIdx, v => { liveryIdx = v; if (model != null) model.SetLivery(Livery.Player[liveryIdx]); }).NoReset();
        game.Button("Restart", "Start this session again from the grid.", () => "Restart session", () => { ResetCar(); CloseSettings(); }).HiddenWhen(() => !InRace);
        game.Button("Leave", "End this session and go to the start menu.", () => "Back to menu", () => { CloseSettings(); ui.page = MenuUI.Page.Start; }).HiddenWhen(() => !InRace);

        return new List<SettingsUI.Tab> { con, asst, setup, camTab, gfx, aud, game };
    }
}
