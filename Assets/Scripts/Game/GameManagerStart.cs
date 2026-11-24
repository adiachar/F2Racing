// GameManagerStart.cs — the race start, another part of GameManager (see GameManager.cs):
//  * grid: real staggered slots (StartGrid.cs), the player in the slot chosen on the start menu ("Grid position")
//  * sequence: GridWait s on the grid with the engine revving (throttle builds revs, no drive), then five red lights
//    one second apart (gantry lamps via TracksideArt.SetStartLights and a HUD row), a random 0.2-3.0 s hold, lights out
//  * launch: at lights out launch control lets the clutch in over LaunchRamp s (drive = throttle x clutch); wheelspin
//    is the physics' own, so too much throttle with TC off spins the rears
//  * rivals react 0.18-0.35 s after lights out (better drivers quicker), then let their clutch in the same way
//  * every car held on the grid has FormulaCar.gridHold set: the brakes hold it (static friction) and holding the
//    brake at rest does not select reverse (it did after 0.6 s: all cars reversed off the grid before lights out)
//  * jump start: the player's car more than 0.5 m from its slot before lights out = 5 s added to the race time
//    (the same time penalty a pit stop adds: pitLoss)
//  * a pre-race cinematic (PreRace.cs) may run first: while it does the race clock stays at 0 (every car held on its
//    brakes in its slot, no lights) and the grid revs come from its engine start; while it shows the player's car in
//    its pit box / the pit lane that car is posed, not simulated (PreRaceRevs)
//  * then (Race start: Full) the formation lap (GameManagerFormation.cs): the clock still at 0, the cars drive one lap
//    in grid order and stop on their slots; then GridWait and the lights as above
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    const double GridWait = 2.0, LaunchRamp = 0.35, JumpPenalty = 5, JumpDist = 0.5;
    double Countdown = 3;          // race time of lights out (set per start in StartReset)
    int playerSlot;                // 0 = pole
    double gridX, gridY, gridThr, gridRpm; bool jumped;
    readonly System.Collections.Generic.List<double> rivalReact = new System.Collections.Generic.List<double>();
    // a race (rivals or not) starts from the grid with the lights; practice and the playground do not
    bool GridStart => raceLaps > 0 && !playground && track != null;

    // which slot the player takes in a field of "cars" (called by SetupRivals before the rivals are placed)
    void ChooseGridSlot(int cars)
    {
        rivalReact.Clear();
        int g = ui != null ? ui.gridIdx : 0;
        cars = Mathf.Clamp(cars, 1, StartGrid.Slots);
        playerSlot = g == 0 ? cars - 1 : g == 1 ? UnityEngine.Random.Range(0, cars) : g == 2 ? 0 : Mathf.Min(g - 2, cars - 1);
        int q = GridSlotOverride(cars); if (q >= 0) playerSlot = q; // the qualifying result (Qualifying.cs)
    }

    // rival k (0-based) takes the k-th free slot; also rolls his reaction time
    void PlaceRival(AiDriver d, int k)
    {
        int slot = k < playerSlot ? k : k + 1;
        StartGrid.Pose(track, slot, out double x, out double y, out double h, out int i);
        d.Place(i, StartGrid.SlotLat(track, slot));
        d.car.x = x; d.car.y = y; d.car.heading = h;
        double skill = Mathf.Clamp01((float)((d.who.pace - 0.85) / 0.15)) * 0.6 + Mathf.Clamp01((float)d.who.consistency) * 0.4;
        double react = 0.35 - 0.17 * skill + UnityEngine.Random.Range(-0.03f, 0.03f);
        rivalReact.Add(System.Math.Max(0.18, System.Math.Min(0.35, react)));
    }

    void PlacePlayerOnGrid()
    {
        StartGrid.Pose(track, playerSlot, out double x, out double y, out double h, out int i);
        car.x = x; car.y = y; car.heading = h;
    }

    // end of ResetCar: a new start
    void StartReset()
    {
        PreRaceAbort(); FormAbort(); // a new start drops a running pre-race intro (PreRace.cs) or formation lap (GameManagerFormation.cs)
        jumped = false; gridThr = 0;
        gridX = car.x; gridY = car.y; gridRpm = car.cfg.idleRpm;
        Countdown = GridStart ? GridWait + 4 + UnityEngine.Random.Range(0.2f, 3.0f) : 0;
        if (track != null && world != null)
        {
            StartGrid.Paint(world, track);
            TracksideArt.SetStartLights(0, track, world.transform);
        }
    }

    // red lights lit now (0-5); 0 after lights out
    int LitLights() => !GridStart || raceTime >= Countdown ? 0 : Mathf.Clamp((int)System.Math.Floor(raceTime - GridWait) + 1, 0, 5);

    static double Clutch(double t) => t <= 0 ? 0 : t >= LaunchRamp ? 1 : t / LaunchRamp;

    // the input the physics gets this step: held on the brakes before lights out, the launch clutch just after
    CarInput StartInput(CarInput input)
    {
        car.gridHold = GridStart && raceTime < Countdown; // on the brakes without selecting reverse (FormulaCar.gridHold)
        if (!GridStart) return input;
        if (form != null) return FormInput(input); // the formation lap: the player drives (GameManagerFormation.cs)
        if (PreRaceHold) { gridThr = PreRaceThrottle; return new CarInput(0, 0, 1); } // pre-race intro: held, its engine start revs (PreRace.cs)
        if (raceTime < Countdown) { gridThr = input.throttle; return new CarInput(input.steer, 0, 1); }
        double c = Clutch(raceTime - Countdown);
        return c >= 1 ? input : new CarInput(input.steer, input.throttle * c, input.brake);
    }

    // after the player's physics step: revs on the grid, the jump-start check, revs blending into the launch
    void StartAfterStep()
    {
        if (!GridStart) return;
        if (form != null) { raceTime = -H; return; } // the formation lap: the race clock held at 0, no grid revs, no jump start
        double t = raceTime - Countdown;
        if (t < 0)
        {
            double idle = car.cfg.idleRpm, target = idle + gridThr * (car.cfg.redline * 0.9 - idle);
            gridRpm += (target - gridRpm) * System.Math.Min(1, H * (target > gridRpm ? 9 : 4));
            car.rpm = gridRpm;
            double dx = car.x - gridX, dy = car.y - gridY;
            if (!jumped && dx * dx + dy * dy > JumpDist * JumpDist)
            {
                jumped = true; pitLoss += JumpPenalty;
                Flash("JUMP START: 5 s penalty");
            }
        }
        else if (t < LaunchRamp)
        {
            double c = Clutch(t);
            car.rpm = System.Math.Max(car.rpm, gridRpm * (1 - c) + car.rpm * c); // the clutch pulls the revs down gradually
        }
        TracksideArt.SetStartLights(LitLights());
        if (PreRaceHold) raceTime = -H; // the pre-race intro holds the race clock at 0 (+H after this step): no lights yet
    }

    // (physics step instead of the player's) the pre-race cinematic poses the car in its pit box / the pit lane: the race
    // clock held at 0, the engine-start revs while it stands (driving, the lane's own revs: PitRun.ApplyTo)
    void PreRaceRevs()
    {
        raceTime = -H;
        if (car.vx > 0.5) { gridRpm = car.rpm; return; }
        gridThr = PreRaceThrottle;
        double idle = car.cfg.idleRpm, target = idle + gridThr * (car.cfg.redline * 0.9 - idle);
        gridRpm += (target - gridRpm) * System.Math.Min(1, H * (target > gridRpm ? 9 : 4));
        car.rpm = gridRpm;
    }

    // the player's car onto its grid slot at rest (after the pit lane, after the formation lap)
    void ParkOnGrid()
    {
        PlacePlayerOnGrid();
        car.vx = car.vy = car.r = 0; car.delta = 0; car.wR = double.NaN; car.gear = 1; car.throttle = 0; car.axLong = 0;
        idx = prevIdx = -1; lineIdx = -1; lineT = double.NaN;
        gridX = car.x; gridY = car.y; jumped = false;
        SnapDrawn();
    }

    // the car was moved by hand: drawn there at once, no blend from the last physics step
    void SnapDrawn()
    {
        px = car.x; py = car.y; ph = car.heading; pd = car.delta; psi = car.steerIn;
        if (model != null) model.Apply(car.x, car.y, car.heading, car.delta, visF, visR, gLon, gLat);
    }

    // rival i's input this step: held until lights out plus his reaction time, then his launch clutch
    CarInput RivalStartInput(int i, AiDriver d)
    {
        double t = raceTime - Countdown - (i < rivalReact.Count ? rivalReact[i] : 0.25);
        d.car.gridHold = t < 0; // held on the brakes (no reverse) until lights out + reaction time
        if (d.car.gridHold)
        {
            // the slope under the slot (StepRivals only updates it once the race runs), so the brakes hold against it
            d.car.grade = WorldBuilder.Height != null ? WorldBuilder.Height.GradeAlong(d.car.x, d.car.y, d.car.heading, d.idx) : 0;
            return new CarInput(0, 0, 1);
        }
        var li = d.LastInput; double c = Clutch(t);
        return c >= 1 ? li : new CarInput(li.steer, li.throttle * c, li.brake);
    }

    // engine sound on the grid: the driver's throttle, not the (zero) drive the physics got
    void StartAudio()
    {
        if (!GridStart || raceTime >= Countdown + LaunchRamp || audioEngine == null) return;
        audioEngine.throttle = Mathf.Max(audioEngine.throttle, (float)gridThr);
        audioEngine.rpm = (float)car.rpm;
    }

    // HUD: five big red lights on a dark gantry plate, top centre. Each lamp eases on over 0.12 s as its second comes;
    // at lights out all go dark at once and the plate fades away within a second.
    void DrawStartLights()
    {
        if (!GridStart || raceTime >= Countdown + 1.0) return;
        HudInit();
        float t = (float)raceTime, after = (float)(raceTime - Countdown);
        float enter = UiKit.EaseOut(t / 0.45f), fade = after > 0 ? 1f - UiKit.EaseOut(after / 0.9f) : 1f, a = enter * fade;
        if (a <= 0.01f) return;
        int lit = LitLights();
        float pod = S(40), pg = S(9), pad = S(11), lamp = S(26);
        float w = 5 * pod + 4 * pg + 2 * pad, h = S(94);
        float x0 = Mathf.Round((Screen.width - w) * 0.5f), y0 = Mathf.Round(hudT + Screen.height * 0.11f - (1f - enter) * S(18));
        var old = GUI.color; GUI.color = new Color(1f, 1f, 1f, a) * old;
        var plate = new Rect(x0, y0, w, h);
        UiKit.Shadow(plate, 14f, 10f, 0.4f);
        UiKit.Gradient(plate, new Color(0.11f, 0.115f, 0.13f, 0.96f), new Color(0.035f, 0.038f, 0.045f, 0.96f), 14f);
        UiKit.Outline(plate, new Color(1f, 1f, 1f, 0.10f), 14f, 0.7f);
        Color unlit = new Color(0.075f, 0.075f, 0.09f, 1f), redOn = new Color(1f, 0.07f, 0.04f, 1f);
        for (int k = 0; k < 5; k++)
        {
            var pr = new Rect(x0 + pad + k * (pod + pg), y0 + pad, pod, h - 2 * pad);
            UiKit.Rect(pr, new Color(0.015f, 0.015f, 0.02f, 1f), 10f);
            UiKit.Outline(pr, new Color(1f, 1f, 1f, 0.06f), 10f, 0.6f);
            float b = k < lit ? Mathf.Clamp01(((float)(raceTime - GridWait) - k) / 0.12f) : 0f;
            for (int j = 0; j < 2; j++)
            {
                var lr = new Rect(pr.center.x - lamp * 0.5f, pr.y + S(6) + j * (lamp + S(8)), lamp, lamp);
                if (b > 0.01f) UiKit.Rect(UiKit.Scale(lr, 1.6f), new Color(1f, 0.05f, 0.02f, 0.22f * b), 999f); // glow
                UiKit.Rect(lr, Color.Lerp(unlit, redOn, b), 999f);
                if (b > 0.01f) UiKit.Rect(new Rect(lr.x + lamp * 0.22f, lr.y + lamp * 0.16f, lamp * 0.36f, lamp * 0.28f), new Color(1f, 0.75f, 0.65f, 0.45f * b), 999f); // highlight
                else UiKit.Outline(lr, new Color(1f, 1f, 1f, 0.07f), 999f, 0.6f);
            }
        }
        GUI.color = old;
    }
}
