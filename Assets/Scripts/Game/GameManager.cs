// GameManager.cs — runs the game: 240 Hz physics, touch/tilt/keyboard input, cameras, HUD (IMGUI, no packages),
// laps and auto-recover. Created automatically at startup (see Boot), so the scene can stay empty.
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
    double frontSpin;

    // settings (saved)
    TcMode tc; bool tilt, playground, invertTilt, sound = true; int camMode;
    // aero balance (share of downforce on the front axle): the main real-world setup knob for under/oversteer
    static readonly float[] Balances = { 0.45f, 0.48f, 0.50f, 0.53f }; int balIdx;
    EngineAudio audioEngine;
    static readonly string[] CamNames = { "Chase", "Far chase", "Cockpit" };

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
        QualitySettings.shadows = ShadowQuality.All; QualitySettings.shadowDistance = 70; QualitySettings.shadowCascades = 1;
        QualitySettings.antiAliasing = 2;

        tc = (TcMode)PlayerPrefs.GetInt("tc", (int)TcMode.Low);
        tilt = PlayerPrefs.GetInt("tilt", 0) == 1; invertTilt = PlayerPrefs.GetInt("tiltInv", 0) == 1;
        playground = PlayerPrefs.GetInt("pg", 0) == 1; camMode = PlayerPrefs.GetInt("cam", 0); sound = PlayerPrefs.GetInt("snd", 1) == 1; balIdx = Mathf.Clamp(PlayerPrefs.GetInt("bal", 0), 0, Balances.Length - 1);

        track = Track.Build();
        WorldBuilder.Build(track);
        car = new FormulaCar(new CarConfig { tc = tc, aeroFront = Balances[balIdx], steerSpeed = 12, steerReturn = 16 });
        model = CarModel.CreateFromAsset() ?? CarModel.Create(car.cfg.wheelbase, car.cfg.frontWeight, car.cfg.wheelRadius);
        var camGo = new GameObject("Camera"); cam = camGo.AddComponent<Camera>(); camGo.tag = "MainCamera";
        cam.nearClipPlane = 0.2f; cam.farClipPlane = 2500; cam.fieldOfView = 62;
        cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor; cam.backgroundColor = RenderSettings.fogColor;
        camGo.AddComponent<AudioListener>();
        audioEngine = camGo.AddComponent<EngineAudio>(); audioEngine.muted = !sound;
        ResetCar();
    }

    void Save()
    {
        PlayerPrefs.SetInt("tc", (int)tc); PlayerPrefs.SetInt("tilt", tilt ? 1 : 0); PlayerPrefs.SetInt("tiltInv", invertTilt ? 1 : 0);
        PlayerPrefs.SetInt("pg", playground ? 1 : 0); PlayerPrefs.SetInt("cam", camMode); PlayerPrefs.SetInt("snd", sound ? 1 : 0); PlayerPrefs.SetInt("bal", balIdx); PlayerPrefs.Save();
    }

    void ResetCar()
    {
        car = new FormulaCar(new CarConfig { tc = tc, aeroFront = Balances[balIdx], steerSpeed = 12, steerReturn = 16 });
        if (playground) { car.x = WorldBuilder.PadStart.x - 15; car.y = WorldBuilder.PadStart.y; car.heading = 0; }
        else { var p = track.pts[track.N - 30]; car.x = p.x; car.y = p.y; car.heading = System.Math.Atan2(p.ty, p.tx); }
        idx = prevIdx = -1; lapStart = null; cut = false; offTime = wrongTime = 0; t100 = null; launchT = -1; topSpeed = 0;
        Flash(playground ? "Playground" : "Track");
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

    float tiltCenter, tiltSmooth;
    float TiltRaw()
    {
        // phone held sideways like a steering wheel: rotation angle of the screen in its own plane, from gravity
        // Input.acceleration is in the phone's portrait axes (x right, y up), pointing along gravity.
        // Landscape-left (top of phone on the left): turning the phone clockwise = steer right
        //   gives acc = (-cos t, -sin t)  ->  t = atan2(-y, -x)
        // Landscape-right: acc = (cos t, sin t)  ->  t = atan2(y, x)
        var a = Input.acceleration;
        var o = Screen.orientation;
        if (o != ScreenOrientation.LandscapeLeft && o != ScreenOrientation.LandscapeRight)
            o = Input.deviceOrientation == DeviceOrientation.LandscapeRight ? ScreenOrientation.LandscapeRight : ScreenOrientation.LandscapeLeft;
        float deg = o == ScreenOrientation.LandscapeRight ? Mathf.Atan2(a.y, a.x) * Mathf.Rad2Deg : Mathf.Atan2(-a.y, -a.x) * Mathf.Rad2Deg;
        return invertTilt ? -deg : deg;
    }
    float TiltSteer()
    {
        tiltSmooth += (TiltRaw() - tiltCenter - tiltSmooth) * Mathf.Min(1, Time.deltaTime * 20);
        return TiltToSteer();
    }
    float TiltToSteer()
    {
        float dz = 1.5f, a = Mathf.Abs(tiltSmooth); // 1.5 deg dead zone, full lock at ~26 deg
        return a < dz ? 0 : Mathf.Clamp(Mathf.Sign(tiltSmooth) * (a - dz) / 25f, -1, 1);
    }

    // ---------------------------------------------------------------- loop
    void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.1f);
        var input = ReadInput(dt);
        acc += dt;
        while (acc >= H)
        {
            SetSurface();
            car.Step(input, H);
            simTime += H; acc -= H;
            frontSpin += car.vx / car.cfg.wheelRadius * H;
            if (playground) TestBench();
        }
        if (!playground) LapsAndRecover(dt);
        model.Apply(car.x, car.y, car.heading, car.delta, frontSpin, car.wheelSpin, car.axLong, car.ay);
        audioEngine.rpm = (float)car.rpm; audioEngine.throttle = (float)car.throttle;
        audioEngine.speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        audioEngine.slide = car.surfaceMu < 0.9 ? 0 : Mathf.Clamp01(((float)System.Math.Max(car.useF, car.useR) - 1f) * 12f + (car.lockF || car.lockR || car.spinR ? 0.6f : 0f));
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
            prevIdx = idx < 0 ? n.idx : idx; idx = n.idx;
            double lat = System.Math.Abs(n.lateral), hw = track.width / 2;
            surface = lat <= hw ? 0 : lat <= hw + WorldBuilder.Kerb ? 1 : 2;
            if (surface == 2 && lat > hw + WorldBuilder.Kerb + 0.9) cut = true;
        }
        var s = surface == 0 ? Surfaces.Asphalt : surface == 1 ? Surfaces.Kerb : Surfaces.Grass;
        car.surfaceMu = s.mu; car.surfaceDrag = s.drag;
    }

    void LapsAndRecover(float dt)
    {
        if (idx < 0) return;
        var p = track.pts[idx];
        double lat = System.Math.Abs(track.Nearest(car.x, car.y, idx).lateral);
        double hdiff = System.Math.Abs(System.Math.Atan2(System.Math.Sin(car.heading - System.Math.Atan2(p.ty, p.tx)), System.Math.Cos(car.heading - System.Math.Atan2(p.ty, p.tx))));
        offTime = surface == 2 && lat > track.width / 2 + WorldBuilder.Kerb + 0.9 ? offTime + dt : 0;
        wrongTime = hdiff > 1.75 && !car.reverse ? wrongTime + dt : 0;
        if (offTime > 0.6 || wrongTime > 1.2) Recover();
        if (prevIdx > track.N * 0.8 && idx < track.N * 0.2)
        {
            if (lapStart.HasValue)
            {
                lastLap = simTime - lapStart.Value;
                if (!cut && (!bestLap.HasValue || lastLap < bestLap)) { bestLap = lastLap; Flash("Best lap! " + Fmt(bestLap)); }
                else Flash((cut ? "Lap invalid  " : "Lap ") + Fmt(lastLap));
            }
            lapStart = simTime; lapNo++; cut = false;
        }
        else if (prevIdx < track.N * 0.2 && idx > track.N * 0.8) lapStart = null;
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
    Vector3 camVel;
    void LateUpdate()
    {
        var tr = model.transform;
        float speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        Vector3 pos, look; float fov;
        float lonG = (float)car.axLong / 9.81f;
        if (camMode == 2)
        {
            pos = tr.TransformPoint(new Vector3(0, 0.82f, 0.05f - lonG * 0.04f));
            look = tr.TransformPoint(new Vector3(0, 0.5f + lonG * 0.3f, 30));
            fov = 72 + Mathf.Min(speed, 90) * 0.06f;
            cam.transform.position = pos;
        }
        else
        {
            float back = camMode == 0 ? 6.2f : 10f, up = camMode == 0 ? 2f : 3.4f;
            back += Mathf.Clamp(lonG * 0.35f, -0.9f, 0.5f);
            pos = tr.TransformPoint(new Vector3(0, up, -back));
            look = tr.TransformPoint(new Vector3(0, 0.7f, 6));
            fov = 62 + Mathf.Min(speed, 90) * 0.1f;
            cam.transform.position = Vector3.SmoothDamp(cam.transform.position, pos, ref camVel, 0.06f);
        }
        cam.transform.LookAt(look);
        if (tilt && camMode == 2) cam.transform.Rotate(0, 0, -TiltRaw() + tiltCenter, Space.Self);
        cam.fieldOfView += (fov - cam.fieldOfView) * Mathf.Min(1, Time.deltaTime * 4);
        model.helmet.gameObject.SetActive(camMode != 2);
    }

    // ---------------------------------------------------------------- HUD
    GUIStyle big, small, btn, center;
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

        // top-right buttons
        float bw = S(60), bh = S(30), x0 = Screen.width - S(8);
        string[] labels = { sound ? "Sound" : "Muted", "Cam", "Reset", "TC " + tc, $"Aero {Balances[balIdx] * 100:0}%", tilt ? "Tilt" : "Drag", playground ? "Playgr." : "Track" };
        for (int i = 0; i < labels.Length; i++)
        {
            var r = new Rect(x0 - (labels.Length - i) * (bw + S(4)), S(8), bw, bh);
            if (GUI.Button(r, labels[i], btn))
            {
                if (i == 0) { sound = !sound; audioEngine.muted = !sound; Flash(sound ? "Sound on" : "Sound off"); }
                if (i == 1) { camMode = (camMode + 1) % CamNames.Length; Flash(CamNames[camMode]); }
                if (i == 2) ResetCar();
                if (i == 3) { tc = (TcMode)(((int)tc + 1) % 3); car.cfg.tc = tc; Flash("Traction control " + tc); }
                if (i == 4)
                {
                    balIdx = (balIdx + 1) % Balances.Length; car.cfg.aeroFront = Balances[balIdx];
                    Flash(balIdx == 0 ? "Aero 45% front: stable (understeer)" : balIdx == 3 ? "Aero 53% front: very loose (oversteer)" : $"Aero {Balances[balIdx] * 100:0}% front: more rotation");
                }
                if (i == 5) { tilt = !tilt; tiltCenter = 0; Flash(tilt ? "Tilt steering (level = straight)" : "Drag steering"); }
                if (i == 6) { playground = !playground; ResetCar(); }
                Save();
            }
        }
        if (tilt && GUI.Button(new Rect(S(8), S(106), S(72), bh), "Invert", btn)) { invertTilt = !invertTilt; Save(); }
        if (tilt && GUI.Button(new Rect(S(86), S(106), S(72), bh), "Center", btn)) { tiltCenter = TiltRaw(); Flash("Straight = how you hold it now"); }

        // top-centre: lap time or test data
        string top, sub;
        if (playground) { top = "TEST DATA (ours vs real F2)"; sub = $"0-100 {(t100.HasValue ? t100.Value.ToString("0.00") + "s" : "—")} (2.9)  ·  top {topSpeed:0} km/h"; }
        else { top = lapStart.HasValue ? Fmt(simTime - lapStart.Value) : "--:--.---"; sub = $"Lap {lapNo}  ·  Last {Fmt(lastLap)}  ·  Best {Fmt(bestLap)}"; }
        float py = S(44);
        Panel(new Rect(Screen.width / 2 - S(150), py, S(300), S(44)));
        center.fontSize = (int)S(18); GUI.Label(new Rect(Screen.width / 2 - S(150), py, S(300), S(26)), top, center);
        center.fontSize = (int)S(11); GUI.Label(new Rect(Screen.width / 2 - S(150), py + S(22), S(300), S(20)), sub, center);

        // dash
        var dash = new Rect(Screen.width / 2 - S(95), Screen.height - S(80), S(190), S(70));
        Panel(dash);
        GUI.Label(new Rect(dash.x, dash.y + S(2), dash.width * 0.72f, S(40)), $"{speed * 3.6f:0}<size={(int)S(12)}> km/h</size>", big);
        var gearStyle = new GUIStyle(big); gearStyle.normal.textColor = new Color(1f, 0.82f, 0.25f);
        gearStyle.fontSize = (int)S(24);
        GUI.Label(new Rect(dash.x + dash.width * 0.7f, dash.y + S(2), dash.width * 0.3f, S(40)), car.reverse ? "R" : car.gear.ToString(), gearStyle);
        float rpmFrac = Mathf.Clamp01((float)((car.rpm - car.cfg.idleRpm) / (car.cfg.redline - car.cfg.idleRpm)));
        Fill(new Rect(dash.x + S(10), dash.y + S(44), dash.width - S(20), S(6)), new Color(1, 1, 1, 0.2f));
        Fill(new Rect(dash.x + S(10), dash.y + S(44), (dash.width - S(20)) * rpmFrac, S(6)), Color.Lerp(new Color(0.24f, 0.86f, 0.52f), new Color(1f, 0.3f, 0.23f), rpmFrac));
        center.fontSize = (int)S(10);
        string lights = (car.tcActive ? "<color=#ffb020>TC</color>" : "TC") + "   " + (car.absActive ? "<color=#ffb020>ABS</color>" : "ABS") + "   " +
                        (car.lockF || car.lockR ? "<color=#ff4b3a>LOCK</color>" : "LOCK") + "   " + (car.spinR ? "<color=#ff4b3a>SPIN</color>" : "SPIN");
        GUI.Label(new Rect(dash.x, dash.y + S(50), dash.width, S(18)), lights, center);

        // telemetry
        double beta = System.Math.Atan2(car.vy, System.Math.Max(System.Math.Abs(car.vx), 1)) * 57.3;
        Panel(new Rect(S(8), S(8), S(150), tilt ? S(92) : S(76)));
        GUI.Label(new Rect(S(14), S(10), S(150), S(92)),
            $"Front grip {car.useF * 100:0}%\nRear grip {car.useR * 100:0}%\nRear spin {car.slipRatioR * 100:0}%\nSlide {beta:0.0}°\nG {System.Math.Sqrt(car.ax * car.ax + car.ay * car.ay) / 9.81:0.0}" +
            (tilt ? $"\nTilt {(tiltSmooth < -0.5f ? "L" : tiltSmooth > 0.5f ? "R" : "")} {Mathf.Abs(tiltSmooth):0}° → steer {Mathf.Abs(TiltToSteer()) * 100:0}%" : ""), small);

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
