// DriverFeel.cs — the cues a driver gets from the car, taken from the physics state and nothing else:
//  * camera motion (cockpit, halo cam and chase): the head nods forward under braking and back under power,
//    is pushed and tilted to the outside by cornering force, shakes on kerbs / rough ground / when the floor hits
//    the road, buzzes finely at high speed, kicks a little on every gear change, and turns slightly into the corner.
//    Everything force-driven runs through critically damped springs, so nothing snaps or oscillates.
//  * the driver's head as the outside cameras see it (DriverModel): nods, tilts and looks with the same forces
//  * the steering wheel in the cockpit, turned by the real front-wheel angle through a fixed steering ratio,
//    and the shift lights on it
//  * what the tyre / wind / plank sounds in EngineAudio need (slip angle against the angle of peak grip, locked
//    fronts, floor on the road, slipstream)
// It only reads the car; the physics is untouched. Nothing here allocates after start-up.
// GameManager calls Step + Cockpit from its camera block, Head where it poses the car's visuals and FeedAudio from its
// audio block.
using FormulaSim;
using UnityEngine;

public class DriverFeel
{
    // One knob for all camera motion: 0 = rigid camera, 1 = as tuned, 2 = double. A setting can write it later.
    public static float Intensity = 1f;

    // ---- amplitudes (cockpit; the halo cam and the chase camera use a share of them, see Step) ----
    const float PitchPerG = 0.30f, PitchBrakeMax = 1.6f, PitchPowerMax = 0.6f; // deg per g: 5 g braking = 1.5 deg nose-down
    const float SurgePerG = 0.009f, SurgeFwdMax = 0.045f, SurgeBackMax = 0.015f; // m per g: head forward under braking
    const float SwayPerG = 0.004f, SwayMax = 0.02f;                             // m per g: head pushed to the outside
    const float RollPerG = 0.30f, RollMax = 1.5f;                               // deg per g: head tilted to the outside
    const float LookPerYaw = 4.6f, LookMax = 5f;                                // deg per rad/s of yaw rate: eyes into the corner
    const float ShakeY = 0.008f, ShakeX = 0.003f, ShakePitch = 0.22f, ShakeRoll = 0.15f; // at car.vib = 1 (m, m, deg, deg)
    const float BuzzY = 0.0005f, BuzzPitch = 0.015f, /* kept below ~1/3 px so distant objects don't shimmer */ BuzzFullSpeed = 90f;       // fine vibration at 324 km/h (m, deg); grows with speed squared
    const float ShiftKick = 9f, DownKick = 4f;                                  // deg/s given to the head on a gear change (about 0.17 / 0.08 deg nod)
    const float BottomShake = 0.5f;                                             // shake level while the floor is on the road
    // spring speeds (rad/s): about 3 Hz for the head, slower for the eyes, quick for the shake level
    const float WHead = 19f, WLook = 9f, WShake = 30f;

    // ---- the driver's head (DriverModel), in every view (the cockpit view hides it): the neck is slower than the eye
    // spring above and the head is heavier, so it lags a little; a few degrees at 4-5 g
    const float HeadNodPerG = 1.3f, HeadNodBrakeMax = 6f, HeadNodPowerMax = 2.5f; // deg per g: forward under braking, back under power
    const float HeadTiltPerG = 1.5f, HeadTiltMax = 6f;                          // deg per g: pushed over to the outside of the corner
    const float HeadLookPerYaw = 9f, HeadLookMax = 8f;                         // deg per rad/s of yaw rate: looks into the corner
    const float WNeck = 10f;
    public float headPitch, headYaw, headRoll; // degrees for the head bone: nod down +, look right +, tilt left +
    Spring hNod, hTilt, hLook;

    // the real wheel: half a turn each way at full lock (360 degrees lock to lock)
    const float WheelLockDeg = 180f;

    // ---- result of Step, for the camera ----
    public Vector3 offset;          // metres, in the car's frame (x right, y up, z forward): add to the eye point
    public float pitch, yaw, roll;  // degrees: cam.transform.Rotate(pitch, yaw, roll, Space.Self) after aiming the camera

    // critically damped spring, stepped exactly (stable at any frame time)
    struct Spring
    {
        public float x, v;
        public void Step(float target, float w, float dt)
        {
            float d = x - target, e = Mathf.Exp(-w * dt), t = (v + w * d) * dt;
            v = (v - w * t) * e; x = target + (d + t) * e;
        }
    }
    Spring sPitch, sRoll, sYaw, sSway, sSurge, sShake;
    float ph1, ph2, ph3, ph4;
    int lastGear = -1;
    CarModel bound; ShiftLights leds;

    void Reset()
    {
        sPitch = sRoll = sYaw = sSway = sSurge = sShake = new Spring();
        hNod = hTilt = hLook = new Spring(); headPitch = headYaw = headRoll = 0;
        offset = Vector3.zero; pitch = yaw = roll = 0; lastGear = -1;
    }

    // view: 0 = chase cameras (rotation only, smaller), 1 = cockpit (driver's head), 2 = halo cam (fixed to the car:
    // little head movement, most of the shake). gLon / gLat: the game's smoothed accelerations, m/s^2
    // (+ = speeding up, + = towards the left). dt = 0 (paused) leaves everything as it was.
    public void Step(FormulaCar car, float gLon, float gLat, float speed, float dt, int view)
    {
        if (dt <= 0) return;
        if (dt > 0.05f) dt = 0.05f;
        if (float.IsNaN(gLon) || float.IsNaN(gLat) || float.IsNaN(speed)) return;
        float lonG = gLon / 9.81f, latG = gLat / 9.81f;
        float head = view == 1 ? 1f : view == 2 ? 0.4f : 0f;      // how much of the driver's head movement this camera shows
        float rot = view == 1 ? 1f : view == 2 ? 0.4f : 0.6f;     // roll / look-into-corner share
        float shk = view == 1 ? 1f : view == 2 ? 0.7f : 0.45f;    // shake share

        // ---- forces -> head ----
        float moving = Mathf.Clamp01(speed / 8f);
        sPitch.Step(Mathf.Clamp(-lonG * PitchPerG, -PitchPowerMax, PitchBrakeMax) * head, WHead, dt);
        sSurge.Step(Mathf.Clamp(-lonG * SurgePerG, -SurgeBackMax, SurgeFwdMax) * head, WHead, dt);
        sSway.Step(Mathf.Clamp(latG * SwayPerG, -SwayMax, SwayMax) * head, WHead, dt);
        sRoll.Step(Mathf.Clamp(-latG * RollPerG, -RollMax, RollMax) * rot, WHead, dt);
        float yawRate = (float)car.r; if (float.IsNaN(yawRate)) yawRate = 0;
        sYaw.Step(Mathf.Clamp(-yawRate * LookPerYaw, -LookMax, LookMax) * rot * moving, WLook, dt);

        // ---- gear change: the drive cuts for a moment and the head nods ----
        int gear = car.reverse ? -1 : car.gear;
        if (lastGear > 0 && gear > 0 && gear != lastGear)
            sPitch.v += (gear > lastGear ? ShiftKick * Mathf.Clamp01((float)car.throttle) : DownKick) * (view == 0 ? 0.4f : view == 2 ? 0.6f : 1f);
        lastGear = gear;

        // ---- shake: kerbs and rough ground (car.vib), the floor on the road, and a fine buzz that grows with speed ----
        float vib = (float)car.vib; if (float.IsNaN(vib)) vib = 0;
        float shakeT = Mathf.Max(vib * Mathf.Min(1, speed / 20f), car.bottoming && speed > 15 ? BottomShake : 0);
        sShake.Step(shakeT, WShake, dt);
        float shake = sShake.x * shk, buzz = Mathf.Clamp01(speed / BuzzFullSpeed); buzz *= buzz * shk;
        // smooth pseudo-noise: a few sines at unrelated rates (no tables, no Perlin lookups)
        ph1 += dt * 13.1f; if (ph1 > 1) ph1 -= 1; ph2 += dt * 21.7f; if (ph2 > 1) ph2 -= 1;
        ph3 += dt * 17.3f; if (ph3 > 1) ph3 -= 1; ph4 += dt * 26.3f; if (ph4 > 1) ph4 -= 1;
        const float Tau = 6.2831853f;
        float nA = Mathf.Sin(ph1 * Tau) * 0.6f + Mathf.Sin(ph2 * Tau) * 0.4f;
        float nB = Mathf.Sin(ph3 * Tau) * 0.7f + Mathf.Sin(ph2 * Tau + 1.7f) * 0.3f;
        float nC = Mathf.Sin(ph4 * Tau) * 0.6f + Mathf.Sin(ph3 * Tau + 0.9f) * 0.4f;

        float k = Mathf.Max(0, Intensity);
        // the chase cameras take no position offset: they follow the car through their own smoothing
        float pos = view == 0 ? 0 : k;
        offset = new Vector3((sSway.x + shake * ShakeX * nB) * pos, (shake * ShakeY * nA + buzz * BuzzY * nC) * pos, sSurge.x * pos);
        pitch = (sPitch.x + shake * ShakePitch * nB + buzz * BuzzPitch * nA) * k;
        roll = (sRoll.x + shake * ShakeRoll * nC) * k;
        yaw = sYaw.x * k;
    }

    // The driver's head under the car's forces, for DriverModel (CarModel.PoseDriver). Runs in every view, replays and
    // the TV camera included; car = null (a replay: the car's state is not the one shown) eases the head back to centre.
    // gLon / gLat as in Step; dt = 0 (paused) holds it.
    public void Head(FormulaCar car, float gLon, float gLat, float dt)
    {
        if (dt <= 0) return;
        if (dt > 0.05f) dt = 0.05f;
        float lonG = 0, latG = 0, yawRate = 0;
        if (car != null && !float.IsNaN(gLon) && !float.IsNaN(gLat))
        {
            lonG = gLon / 9.81f; latG = gLat / 9.81f;
            yawRate = (float)car.r; if (float.IsNaN(yawRate)) yawRate = 0;
            float moving = Mathf.Clamp01((float)System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy) / 8f);
            yawRate *= moving;
        }
        hNod.Step(Mathf.Clamp(-lonG * HeadNodPerG, -HeadNodPowerMax, HeadNodBrakeMax), WNeck, dt);
        hTilt.Step(Mathf.Clamp(-latG * HeadTiltPerG, -HeadTiltMax, HeadTiltMax), WNeck, dt);
        hLook.Step(Mathf.Clamp(-yawRate * HeadLookPerYaw, -HeadLookMax, HeadLookMax), WNeck * 0.6f, dt);
        headPitch = hNod.x; headRoll = hTilt.x; headYaw = hLook.x;
    }

    // Steering wheel and shift lights of the player's car. The wheel shows the real front-wheel angle times a fixed
    // ratio (full lock = half a turn), so it always agrees with the front tyres seen over the nose, and at speed,
    // where the car uses only a few degrees of lock, the hands barely move, as in a real onboard.
    // delta: the front-wheel angle to show (rad); GameManager passes it drawn between the last two physics steps, like
    // the car itself; NaN = the car's latest step.
    public void Cockpit(CarModel model, FormulaCar car, float time, double delta = double.NaN)
    {
        if (model == null) return;
        if (model != bound)
        {
            bound = model; Reset();
            if (leds != null) leds.Dispose();
            leds = ShiftLights.Attach(model.SteerWheel);
        }
        float lockDeg = Mathf.Max(1f, (float)car.cfg.maxLockDeg);
        float deg = (float)((double.IsNaN(delta) ? car.delta : delta) * 57.29578) * (WheelLockDeg / lockDeg);
        if (!float.IsNaN(deg)) model.SetSteerDeg(Mathf.Clamp(deg, -WheelLockDeg, WheelLockDeg));
        if (leds != null) leds.Set((float)car.rpm, (float)car.cfg.redline, time);
    }

    // Grip, wind and floor cues for the sound. Slip angles are given as a share of the angle where the tyre
    // grips best (which grows with load, as in the physics), so 1 means "at the limit" on either axle.
    public static void FeedAudio(EngineAudio a, FormulaCar car, float speed)
    {
        var c = car.cfg;
        float peak0 = 1.7f / Mathf.Max(1f, (float)c.tireB);                  // rad at the static load (about 6.7 deg)
        float nF = Mathf.Max(1f, (float)(c.mass * 9.81 * c.frontWeight)), nR = Mathf.Max(1f, (float)(c.mass * 9.81 * (1 - c.frontWeight)));
        float grow = (float)c.slipGrowth;
        float pF = peak0 * Mathf.Pow(Mathf.Clamp((float)car.fzF / nF, 0.2f, 6f), grow), pR = peak0 * Mathf.Pow(Mathf.Clamp((float)car.fzR / nR, 0.2f, 6f), grow);
        bool moving = speed > 6;
        float sf = moving ? Mathf.Abs((float)car.alphaF) / pF : 0, sr = moving ? Mathf.Abs((float)car.alphaR) / pR : 0;
        a.slipF = float.IsNaN(sf) ? 0 : Mathf.Min(sf, 3f); a.slipR = float.IsNaN(sr) ? 0 : Mathf.Min(sr, 3f);
        // fronts: fully locked, or held on the edge of locking by the ABS (a lighter chirp)
        a.lockFront = car.brake > 0.05 ? (car.lockF ? 1f : car.absActive && car.brake > 0.2 ? 0.3f : 0f) : 0f;
        // floor on the road: the ride height has a 3 mm stop; the plank starts to touch a few mm above it
        float ride = double.IsNaN(car.rideF) || double.IsNaN(car.rideR) ? 1f : (float)System.Math.Min(car.rideF, car.rideR);
        a.scrape = car.bottoming ? 1f : Mathf.Clamp01((0.006f - ride) / 0.003f);
        a.tow = double.IsNaN(car.tow) ? 0f : (float)car.tow;
    }
}

// Shift lights across the top of the steering wheel: 15 LEDs (5 green, 5 red, 5 blue) that fill from 85 % of the
// rev limit and all flash blue at the shift point. One small mesh, one draw call; the colours are only written when
// the number of lit LEDs changes.
public class ShiftLights
{
    const int N = 15;
    const float From = 0.85f, Full = 0.965f, FlashAt = 0.972f, FlashHz = 12f; // shares of the rev limit (the gearbox shifts at 97.5 %)
    static readonly Color32 Off = new Color32(16, 16, 18, 255), Green = new Color32(40, 235, 70, 255), Red = new Color32(255, 45, 35, 255), Blue = new Color32(70, 120, 255, 255);
    static Material mat; static bool tried;
    Mesh mesh; readonly Color32[] cols = new Color32[N * 4]; int shown = -1;

    // wheel: the steering wheel part (its own frame: x right, y up, the driver looks at its -z side). null = no lights.
    public static ShiftLights Attach(Transform wheel)
    {
        if (wheel == null) return null;
        if (!tried) { tried = true; var sh = Resources.Load<Shader>("ShiftLed"); if (sh != null && sh.isSupported) mat = new Material(sh); }
        if (mat == null) return null;
        var s = new ShiftLights();
        var v = new Vector3[N * 4]; var idx = new int[N * 6];
        for (int i = 0; i < N; i++)
        {
            // a row 13 cm wide just under the wheel's top edge, a few mm proud of its face (the face curves away at the ends)
            float x = (i - (N - 1) * 0.5f) * 0.009f, y = 0.074f, z = -0.031f - 0.005f * Mathf.Clamp01((Mathf.Abs(x) - 0.045f) / 0.02f);
            const float hw = 0.0034f, hh = 0.0026f;
            v[i * 4] = new Vector3(x - hw, y - hh, z); v[i * 4 + 1] = new Vector3(x - hw, y + hh, z);
            v[i * 4 + 2] = new Vector3(x + hw, y + hh, z); v[i * 4 + 3] = new Vector3(x + hw, y - hh, z);
            idx[i * 6] = i * 4; idx[i * 6 + 1] = i * 4 + 1; idx[i * 6 + 2] = i * 4 + 2;
            idx[i * 6 + 3] = i * 4; idx[i * 6 + 4] = i * 4 + 2; idx[i * 6 + 5] = i * 4 + 3;
        }
        for (int i = 0; i < s.cols.Length; i++) s.cols[i] = Off;
        s.mesh = new Mesh { name = "ShiftLights" };
        s.mesh.MarkDynamic();
        s.mesh.vertices = v; s.mesh.colors32 = s.cols; s.mesh.triangles = idx; s.mesh.RecalculateBounds();
        var go = new GameObject("ShiftLights"); go.transform.SetParent(wheel, false);
        go.AddComponent<MeshFilter>().sharedMesh = s.mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return s;
    }

    public void Dispose() { if (mesh != null) Object.Destroy(mesh); mesh = null; }

    public void Set(float rpm, float redline, float time)
    {
        if (mesh == null) return;
        float f = redline > 0 ? rpm / redline : 0;
        // state: 0..N LEDs lit, or the flash (N + 1 = all blue, N + 2 = all dark)
        int state = f >= FlashAt ? (((int)(time * FlashHz * 2) & 1) == 0 ? N + 1 : N + 2)
                  : Mathf.Clamp(Mathf.RoundToInt((f - From) / (Full - From) * N), 0, N);
        if (state == shown) return;
        shown = state;
        for (int i = 0; i < N; i++)
        {
            Color32 c = state == N + 1 ? Blue : state == N + 2 ? Off : i >= state ? Off : i < 5 ? Green : i < 10 ? Red : Blue;
            cols[i * 4] = c; cols[i * 4 + 1] = c; cols[i * 4 + 2] = c; cols[i * 4 + 3] = c;
        }
        mesh.colors32 = cols;
    }
}
