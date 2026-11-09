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
    double frontSpin, px, py, ph, pd;

    // settings (saved)
    TcMode tc; bool tilt, playground, invertTilt, sound = true; int camMode;
    static readonly string[] MapNames = { "Test track", "Monaco", "Spa", "Playground" };
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
        QualitySettings.shadows = ShadowQuality.All; QualitySettings.shadowDistance = 70; QualitySettings.shadowCascades = 1;
        QualitySettings.antiAliasing = 2;

        tc = (TcMode)PlayerPrefs.GetInt("tc", (int)TcMode.Low);
        tilt = PlayerPrefs.GetInt("tilt", 0) == 1; invertTilt = PlayerPrefs.GetInt("tiltInv", 0) == 1;
        mapIdx = Mathf.Clamp(PlayerPrefs.GetInt("map", 0), 0, MapNames.Length - 1); playground = mapIdx == 3; camMode = PlayerPrefs.GetInt("cam", 0); sound = PlayerPrefs.GetInt("snd", 1) == 1; balIdx = Mathf.Clamp(PlayerPrefs.GetInt("bal", 0), 0, Balances.Length - 1); showLine = PlayerPrefs.GetInt("line", 0) == 1;

        LoadMap();
        car = new FormulaCar(new CarConfig { tc = tc, aeroFront = Balances[balIdx], steerSpeed = 12, steerReturn = 16 });
        model = CarModel.CreateFromAsset() ?? CarModel.Create(car.cfg.wheelbase, car.cfg.frontWeight, car.cfg.wheelRadius);
        var camGo = new GameObject("Camera"); cam = camGo.AddComponent<Camera>(); camGo.tag = "MainCamera";
        cam.nearClipPlane = 0.2f; cam.farClipPlane = 2500; cam.fieldOfView = 62;
        cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor; cam.backgroundColor = RenderSettings.fogColor;
        camGo.AddComponent<AudioListener>();
        audioEngine = camGo.AddComponent<EngineAudio>(); audioEngine.muted = !sound;
        ResetCar();
    }

    int builtFor = -1;
    void LoadMap()
    {
        playground = mapIdx == 3;
        int circuit = playground ? (builtFor >= 0 ? builtFor : 0) : mapIdx;
        if (circuit != builtFor || world == null)
        {
            if (world != null) Destroy(world);
            track = circuit == 1 ? Track.FromLonLat(TrackData.Monaco, TrackData.MonacoWidth)
                  : circuit == 2 ? Track.FromLonLat(TrackData.Spa, TrackData.SpaWidth)
                  : Track.Build();
            world = WorldBuilder.Build(track);
            BuildLine();
            BuildMiniMap();
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
        Fill(new Rect(cx - S(4), cy - S(4), S(8), S(8)), new Color(1f, 0.25f, 0.2f));
    }

    // racing line overlay (off by default; "Line" button)
    RacingLine line; GameObject lineGo; bool showLine;
    void BuildLine()
    {
        if (lineGo != null) Destroy(lineGo);
        line = RacingLine.Compute(track, new CarConfig { aeroFront = Balances[balIdx] });
        lineGo = RacingLineView.Build(track, line, world.transform);
        lineGo.SetActive(showLine && !playground);
    }

    void Save()
    {
        PlayerPrefs.SetInt("tc", (int)tc); PlayerPrefs.SetInt("tilt", tilt ? 1 : 0); PlayerPrefs.SetInt("tiltInv", invertTilt ? 1 : 0);
        PlayerPrefs.SetInt("map", mapIdx); PlayerPrefs.SetInt("cam", camMode); PlayerPrefs.SetInt("snd", sound ? 1 : 0); PlayerPrefs.SetInt("bal", balIdx); PlayerPrefs.SetInt("line", showLine ? 1 : 0); PlayerPrefs.Save();
    }

    void ResetCar()
    {
        car = new FormulaCar(new CarConfig { tc = tc, aeroFront = Balances[balIdx], steerSpeed = 12, steerReturn = 16 });
        if (playground) { car.x = WorldBuilder.PadStart.x - 15; car.y = WorldBuilder.PadStart.y; car.heading = 0; }
        else { var p = track.pts[track.N - 30]; car.x = p.x; car.y = p.y; car.heading = System.Math.Atan2(p.ty, p.tx); }
        idx = prevIdx = -1; lapStart = null; cut = false; offTime = wrongTime = 0; t100 = null; launchT = -1; topSpeed = 0;
        Flash(MapNames[mapIdx]);
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

    float tiltCenter, tiltSmooth, tiltAxis = float.NaN, tiltAxisT, tiltConf;
    // Steering-wheel angle of the phone held sideways, from gravity only.
    // Gravity's direction in the screen plane is measured against a LOCKED "down" axis (one of +x,-x,+y,-y,
    // whichever the OS reports for landscape). The lock only moves after gravity has sat clearly on another
    // axis for 0.6 s, so holding a big tilt never flips the steering the other way.
    // When the phone lies flat, gravity is almost perpendicular to the screen and the angle is pure noise:
    // tiltConf fades steering to zero there instead of letting it jitter.
    float TiltRaw()
    {
        var a = Input.acceleration;
        float inPlane = Mathf.Sqrt(a.x * a.x + a.y * a.y);
        tiltConf = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.3f, 0.6f, inPlane));
        if (inPlane < 0.2f) return 0;
        float th = Mathf.Atan2(a.y, a.x) * Mathf.Rad2Deg;
        float nearest = Mathf.Round(th / 90f) * 90f;
        if (float.IsNaN(tiltAxis)) tiltAxis = nearest;
        bool clearlyOther = Mathf.Abs(Mathf.DeltaAngle(th, nearest)) < 25f && Mathf.Abs(Mathf.DeltaAngle(nearest, tiltAxis)) > 1f && inPlane > 0.6f;
        if (clearlyOther) { tiltAxisT += Time.unscaledDeltaTime; if (tiltAxisT > 0.6f) { tiltAxis = nearest; tiltAxisT = 0; } }
        else tiltAxisT = 0;
        float deg = Mathf.Clamp(Mathf.DeltaAngle(tiltAxis, th), -80, 80);
        return invertTilt ? -deg : deg;
    }
    float TiltSteer()
    {
        float target = (TiltRaw() - tiltCenter) * tiltConf;
        // smoothing: ~0.08 s, plus a tiny hysteresis band that swallows sensor noise
        float k = Mathf.Min(1, Time.deltaTime * 12);
        float d = target - tiltSmooth;
        if (Mathf.Abs(d) < 0.25f) d = 0;
        tiltSmooth += d * k;
        return TiltToSteer();
    }
    float TiltToSteer()
    {
        float dz = 2f, a = Mathf.Abs(tiltSmooth); // 2 deg dead zone, full lock at ~27 deg
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
            px = car.x; py = car.y; ph = car.heading; pd = car.delta;
            car.Step(input, H);
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
        audioEngine.rpm = (float)car.rpm; audioEngine.throttle = (float)car.throttle;
        audioEngine.speed = (float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        audioEngine.slide = Mathf.Max((float)car.useF, (float)car.useR, car.lockF || car.lockR || car.spinR ? 1.1f : 0f); audioEngine.onAsphalt = surface == 0;
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
    Vector3 camVel; float gLon, gLat, visF, visR;
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
            pos = tr.TransformPoint(halo ? new Vector3(0, 1.02f, 0.12f - lonG * 0.03f) : new Vector3(0, 0.66f, 0.28f - lonG * 0.03f));
            look = tr.TransformPoint(new Vector3(0, (halo ? 0.55f : 0.5f) + lonG * 0.2f, 30));
            fov = (halo ? 66 : 70) + Mathf.Min(speed, 90) * 0.06f;
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
        if (tilt && camMode == 2) cam.transform.Rotate(0, 0, -tiltSmooth * 0.6f, Space.Self);
        cam.fieldOfView += (fov - cam.fieldOfView) * Mathf.Min(1, Time.deltaTime * 4);
        model.helmet.gameObject.SetActive(camMode != 2);
        if (model.cockpitFloor) model.cockpitFloor.SetActive(camMode == 2);
        cam.nearClipPlane = camMode >= 2 ? 0.05f : 0.3f;
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
        float bw = S(62), bh = S(30), x0 = Screen.width - S(8);
        string[] labels = { sound ? "Sound" : "Muted", "Cam", "Reset", "TC " + tc, $"Aero {Balances[balIdx] * 100:0}%", tilt ? "Tilt" : "Drag", MapNames[mapIdx], showLine ? "Line" : "No line" };
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
                if (i == 7) { showLine = !showLine; if (lineGo != null) lineGo.SetActive(showLine && !playground); Flash(showLine ? "Racing line on" : "Racing line off"); }
                if (i == 6) { mapIdx = (mapIdx + 1) % MapNames.Length; LoadMap(); ResetCar(); Flash(MapNames[mapIdx]); }
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

        DrawMiniMap();

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
