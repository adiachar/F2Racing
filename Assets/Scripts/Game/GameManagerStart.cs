// GameManagerStart.cs — the race start, another part of GameManager (see GameManager.cs):
//  * grid: real staggered slots (StartGrid.cs), the player in the slot chosen on the start menu ("Grid position")
//  * sequence: GridWait s on the grid with the engine revving (throttle builds revs, no drive), then five red lights
//    one second apart (gantry lamps via TracksideArt.SetStartLights and a HUD row), a random 0.2-3.0 s hold, lights out
//  * launch: at lights out launch control lets the clutch in over LaunchRamp s (drive = throttle x clutch); wheelspin
//    is the physics' own, so too much throttle with TC off spins the rears
//  * rivals react 0.18-0.35 s after lights out (better drivers quicker), then let their clutch in the same way
//  * jump start: the player's car more than 0.5 m from its slot before lights out = 5 s added to the race time
//    (the same time penalty a pit stop adds: pitLoss)
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
        if (!GridStart) return input;
        if (raceTime < Countdown) { gridThr = input.throttle; return new CarInput(input.steer, 0, 1); }
        double c = Clutch(raceTime - Countdown);
        return c >= 1 ? input : new CarInput(input.steer, input.throttle * c, input.brake);
    }

    // after the player's physics step: revs on the grid, the jump-start check, revs blending into the launch
    void StartAfterStep()
    {
        if (!GridStart) return;
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
    }

    // rival i's input this step: held until lights out plus his reaction time, then his launch clutch
    CarInput RivalStartInput(int i, AiDriver d)
    {
        if (raceTime < Countdown) return new CarInput(0, 0, 1);
        double t = raceTime - Countdown - (i < rivalReact.Count ? rivalReact[i] : 0.25);
        if (t < 0) return new CarInput(0, 0, 1);
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

    // HUD: a row of five red lights, top centre, until just after lights out
    static Texture2D startDot;
    void DrawStartLights()
    {
        if (!GridStart || raceTime >= Countdown + 1.0) return;
        if (startDot == null)
        {
            const int D = 64; startDot = new Texture2D(D, D, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[D * D];
            for (int y = 0; y < D; y++) for (int x = 0; x < D; x++)
            {
                float d = Mathf.Sqrt((x - D / 2f + 0.5f) * (x - D / 2f + 0.5f) + (y - D / 2f + 0.5f) * (y - D / 2f + 0.5f));
                px[y * D + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(D / 2f - 1 - d)));
            }
            startDot.SetPixels32(px); startDot.Apply(false);
        }
        int lit = LitLights();
        float r = S(34), gap = S(10), w = 5 * r + 4 * gap, x0 = (Screen.width - w) / 2, y0 = Screen.height * 0.16f;
        Fill(new Rect(x0 - S(10), y0 - S(10), w + S(20), r + S(20)), new Color(0, 0, 0, 0.55f));
        var o = GUI.color;
        for (int k = 0; k < 5; k++)
        {
            GUI.color = k < lit ? new Color(1f, 0.08f, 0.05f) : new Color(0.16f, 0.16f, 0.18f);
            GUI.DrawTexture(new Rect(x0 + k * (r + gap), y0, r, r), startDot);
        }
        GUI.color = o;
    }
}
