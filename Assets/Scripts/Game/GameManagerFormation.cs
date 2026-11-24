// GameManagerFormation.cs — the formation lap before a race start (Settings > Race start: Full), another part of
// GameManager (a partial class; the lap itself is the nested Formation class). It follows the garage / pit-exit
// cinematic (PreRace.cs) and ends with every car in its grid slot and the normal start: GridWait, five red lights.
//  * Every car drives one lap in grid order from its slot. The pace: 58% of the racing line's speed, 160 km/h at most,
//    gentle braking / accelerating ramps (a table per track point, built once). Each car follows the car ahead of it in
//    grid order at a speed gap (8.6 m + 0.5 s, i.e. a rolling start out of the slots), no passing; over the last 320 m
//    it brakes to a stop on its own slot (3.2 m/s^2) and its aim point moves to the slot's side of the grid.
//  * Rivals: their own AiDriver (steering, race craft) with formation = true and a speed cap (AiDriver.formation /
//    formCap / formLat / formWeave: decision code only; solo pace unchanged). They weave on the straights.
//  * The player drives himself (his own physics, tyres warming with the existing heat model; weaving is up to him),
//    with a 172 km/h limiter and no boost / Overtake Mode. An autopilot (pure pursuit on the racing line + the same
//    speed caps) drives the car out of the slot (until GAS or BRAKE is pressed) and brings it to a stop on its slot over
//    the last 240 m. Passing the car ahead shows "NO OVERTAKING" (the formation keeps the grid order: drop back; the AI
//    drives round a car that has slowed right down); falling far behind shows "CATCH UP".
//  * Nothing is timed or judged: the race clock stays at 0 (StartAfterStep), no lap is counted (LineCheck, AiDriver),
//    no track-limit strikes, contacts not judged, no safety-rating change (all of them wait for lights out).
//  * Cameras: two TV shots as the field pulls away (low at the front of the grid on the player's car, then high behind
//    him), the player's own camera for the lap, a low TV shot from the front of the grid as he comes back to his slot.
//  * The end: every car stopped on (or near) its slot, or 40 s after the leader stopped, or a time-out: a short dip to
//    black, every car snapped exactly onto its slot (tyre temperatures kept), lap state reset, the player's camera,
//    then the lights. "SKIP TO START" (top centre) does the same at once.
//  * Shot mode (DebugShots intro=t past the cinematic): the cars posed along the lap as a function of t (no physics).
// Cost: the pace table (one double per track point) once per race start; per physics step the player's caps, per AI
// think (60 Hz) each rival's caps. Nothing allocates per frame (texts are made once).
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    Formation form;
    float formShotBase = -1f;    // shot mode: the intro time where the formation lap starts (PreRace.Total)
    bool FormOn => form != null;

    // after the Full cinematic (PreRace.End), or straight away when it could not run
    void FormBegin()
    {
        FormAbort();
        if (!GridStart || qualiSession || track == null || line == null || car == null || model == null || cam == null || track.N < 8) return;
        try { form = new Formation(this); }
        catch (System.Exception e) { Debug.LogWarning("Formation: " + e.Message); var f = form; form = null; if (f != null) f.Finish(false); }
    }
    // a new start, a restart, an error: dropped at once (the cars stay where they are; ResetCar places them anyway).
    // snap: every car back onto its slot (shot mode, going back to the cinematic)
    void FormAbort(bool snap = false) { var f = form; form = null; if (f != null) f.Finish(snap); }
    // the end of the lap (under black): every car onto its slot, then the lights
    void FormDone() { var f = form; form = null; if (f != null) f.Finish(true); introPostFade = Formation.Dip; }

    // physics step (StartInput): the player's input on the formation lap
    CarInput FormInput(CarInput input)
    {
        try { return form.PlayerStep(input); }
        catch (System.Exception e) { Debug.LogWarning("Formation: " + e.Message); FormAbort(); return input; }
    }
    // physics step (StepRivals): the rivals on the formation lap
    void FormRivals()
    {
        try { form.RivalsStep(); }
        catch (System.Exception e) { Debug.LogWarning("Formation: " + e.Message); FormAbort(); }
    }
    // LateUpdate: true = a formation TV shot placed the camera
    bool FormCamera()
    {
        if (form == null) return false;
        if (ui != null && ui.Showing && !shotFreeze) { FormAbort(); return false; } // left the race (Settings > Leave)
        try { return form.Frame(); }
        catch (System.Exception e) { Debug.LogWarning("Formation: " + e.Message); FormAbort(); return false; }
    }
    // OnGUI before the HUD: true = a TV shot (captions, no HUD)
    bool FormGui()
    {
        if (form == null) return false;
        try { return form.Gui(); } catch (System.Exception e) { Debug.LogWarning("Formation GUI: " + e.Message); return false; }
    }
    // OnGUI after the HUD (the player's own camera): banner, hint, skip, the dip at the end
    void FormHud()
    {
        if (form == null) return;
        try { form.Hud(); } catch (System.Exception e) { Debug.LogWarning("Formation HUD: " + e.Message); }
    }

    sealed class Formation
    {
        public const float Dip = 0.35f;
        const double PaceMax = 160 / 3.6, PaceShare = 0.58, PaceBrake = 3.0, PaceAccel = 3.5, PlayerMax = 172 / 3.6;
        const double Gap0 = 8.6, GapV = 0.5, GapK = 0.8;      // follow: wanted gap Gap0 + GapV v (m), speed + (gap - wanted) GapK
        const double StopA = 3.2, StopZone = 320, AutoDist = 240, LatZone = 300;
        const float ShotA = 3.2f, ShotB = 3.6f, TvEnd = ShotA + ShotB, LeaderWait = 40f, AllHold = 0.8f;
        const float ShotEnd = 30f, ShotParked = 36f;          // shot mode: the end shot from, every car parked from (s)

        readonly GameManager g;
        readonly Track tk; readonly int N, M, pSlot; int userCam; // userCam: the player's view (he may change it on the lap)
        readonly int[] rivalAt;                  // slot -> rival index (-1 = the player)
        readonly AiDriver[] drv;                 // slot -> its driver (null = the player)
        readonly double[] sS, sLat, sX, sY, sH;  // slot: signed lap distance of the car centre, side, pose
        readonly double[] trav, vNow, stopT;     // m driven along the lap, speed, s stopped
        readonly int[] lastI; readonly bool[] parked;
        readonly double[] pace;                  // formation pace per track point (m/s)
        double t, maxT; float leaderParked = -1f, allParked = -1f;
        bool auto = true, autoEnd, ending, tvOn; float endT, shotT;
        int warn;                                // 0 none, 1 passed the car ahead, 2 far behind
        // texts (made once)
        readonly string tTitle, tSub, tSkip, tAutoGo, tAutoStop, tNoPass, tCatch, cEye, cTitle, cSub, eEye, eTitle, eSub;
        float wKey = -1f, wTitle, wSub, wSkip; readonly float[] wChip = new float[5];

        bool Shot => g.shotIntro;
        float Clock => Shot ? shotT : (float)t;

        public Formation(GameManager owner)
        {
            g = owner; tk = g.track; N = tk.N;
            M = g.rivals.Count + 1; pSlot = Mathf.Clamp(g.playerSlot, 0, M - 1);
            userCam = g.camMode == TvMode ? TvMode : Mathf.Clamp(g.camMode, 0, 3);
            rivalAt = new int[M]; drv = new AiDriver[M];
            sS = new double[M]; sLat = new double[M]; sX = new double[M]; sY = new double[M]; sH = new double[M];
            trav = new double[M]; vNow = new double[M]; stopT = new double[M]; lastI = new int[M]; parked = new bool[M];
            double L = tk.length;
            for (int s = 0; s < M; s++)
            {
                int k = s == pSlot ? -1 : s < pSlot ? s : s - 1;
                rivalAt[s] = k; drv[s] = k >= 0 && k < g.rivals.Count ? g.rivals[k] : null;
                double ss = StartGrid.SlotS(tk, s); ss -= System.Math.Floor(ss / L) * L; if (ss > L * 0.5) ss -= L;
                sS[s] = ss; sLat[s] = StartGrid.SlotLat(tk, s);
                StartGrid.Pose(tk, s, out sX[s], out sY[s], out sH[s], out lastI[s]);
            }
            // the pace table: a share of the racing line's speed (wet: less), capped, with gentle ramps either way
            pace = new double[N];
            var ln = g.line; bool ok = ln != null && ln.speed != null && ln.N == N;
            double wet = System.Math.Sqrt(Mathf.Clamp01((float)Weather.TrackFactor(g.carSet, 1)));
            for (int i = 0; i < N; i++) pace[i] = ok ? System.Math.Min(PaceMax, System.Math.Max(9, PaceShare * ln.speed[i] * wet)) : 30;
            double ds = tk.ds;
            for (int r = 0; r < 2; r++) for (int i = N - 1; i >= 0; i--) { int j = (i + 1) % N; pace[i] = System.Math.Min(pace[i], System.Math.Sqrt(pace[j] * pace[j] + 2 * PaceBrake * ds)); }
            for (int r = 0; r < 2; r++) for (int i = 0; i < N; i++) { int j = (i + 1) % N; pace[j] = System.Math.Min(pace[j], System.Math.Sqrt(pace[i] * pace[i] + 2 * PaceAccel * ds)); }
            double avg = 0; for (int i = 0; i < N; i++) avg += pace[i]; avg = System.Math.Max(10, avg / N);
            maxT = 60 + 2.2 * L / avg; // a time-out (the lap takes about L / avg + the start and the stop)
            // texts
            tTitle = "FORMATION LAP"; tSub = "KEEP YOUR POSITION  ·  WARM YOUR TYRES"; tSkip = "SKIP TO START";
            tAutoGo = "AUTO  ·  PRESS GAS TO DRIVE"; tAutoStop = "AUTO  ·  STOPPING ON YOUR GRID SLOT"; tCatch = "CATCH UP WITH THE FIELD";
            tNoPass = pSlot > 0 && drv[pSlot - 1] != null ? "NO OVERTAKING  ·  LET " + Code(drv[pSlot - 1].who) + " BY" : "NO OVERTAKING";
            cEye = UiKit.Caps("Race start"); cTitle = "FORMATION LAP"; cSub = "KEEP YOUR POSITION  ·  WARM YOUR TYRES";
            eEye = UiKit.Caps("Formation lap"); eTitle = "BACK TO THE GRID"; eSub = "EVERY CAR TO ITS GRID SLOT";
            g.ClearRecording();
            for (int s = 0; s < M; s++) if (drv[s] != null) { var d = drv[s]; d.formation = true; d.formCap = 0; d.formLat = sLat[s]; d.formLatW = 1; d.formWeave = 0; }
        }

        // ---------------------------------------------------------------- the lap
        double Pos(int s) => sS[s] + trav[s];
        // m to go to its slot: along the lap, exact (along the slot's heading) over the last metres
        double Rem(int s, FormulaCar c)
        {
            double rem = tk.length - trav[s];
            if (trav[s] > tk.length * 0.5 && rem < 30) rem = (sX[s] - c.x) * System.Math.Cos(sH[s]) + (sY[s] - c.y) * System.Math.Sin(sH[s]);
            return rem;
        }
        void Advance(int s, int i)
        {
            if (i < 0) return;
            int d = i - lastI[s]; if (d > N / 2) d -= N; else if (d < -N / 2) d += N;
            trav[s] += d * tk.ds; lastI[s] = i;
        }
        // the speed this car may do now: the pace, the car ahead in grid order, its own slot at the end
        double Cap(int s, int i, double v, double rem)
        {
            if (parked[s]) return 0;
            double cap = pace[(i + 2) % N];
            if (s > 0)
            {
                int p = s - 1; double gap = Pos(p) - Pos(s), want = Gap0 + GapV * v;
                if (rem < StopZone && trav[s] > tk.length * 0.5) { double wEnd = sS[p] - sS[s] - 1.0; want = wEnd + (want - wEnd) * Mathf.Clamp01((float)(rem / StopZone)); }
                if (gap > want + 25) cap *= 1.12; // catching up
                cap = System.Math.Min(cap, System.Math.Max(0, vNow[p] + (gap - want) * GapK));
            }
            if (trav[s] > tk.length * 0.5) cap = System.Math.Min(cap, rem <= 0 ? 0 : System.Math.Sqrt(2 * StopA * System.Math.Max(0, rem - 0.1)));
            return cap;
        }
        // stopped on (or near) its slot at the end of the lap
        void Park(int s, double v, double rem, double dt)
        {
            if (parked[s] || trav[s] < tk.length * 0.5) return;
            stopT[s] = v < 0.35 ? stopT[s] + dt : 0;
            if (v < 0.35 && (rem < 1.6 || (rem < 14 && stopT[s] > 2.0))) parked[s] = true;
        }
        // the slot's side of the grid: leaving it (first 40 m) and coming back (last LatZone m)
        double LatW(int s, double rem)
        {
            if (trav[s] < 40) return 1 - Mathf.Clamp01((float)(trav[s] / 40));
            if (trav[s] > tk.length * 0.5 && rem < LatZone) return Smooth01((float)((LatZone - rem) / 220));
            return 0;
        }

        // physics step: the player (time, his caps, the autopilot or his own input under the limiter)
        public CarInput PlayerStep(CarInput input)
        {
            t += H;
            var c = g.car; int s = pSlot;
            Advance(s, g.idx);
            double v = System.Math.Sqrt(c.vx * c.vx + c.vy * c.vy); vNow[s] = v;
            double rem = Rem(s, c);
            Park(s, v, rem, H);
            c.boostOn = false; c.otOn = false;
            // the autopilot: out of the slot until the player takes over after the TV shots, and over the last metres
            if (!autoEnd && trav[s] > tk.length * 0.5 && rem < AutoDist) { autoEnd = true; auto = true; }
            if (auto && !autoEnd && t > TvEnd && (g.pedalGas > 0.1f || g.pedalBrake > 0.1f)) auto = false;
            // warnings
            warn = 0;
            if (!auto && s > 0) { double ahead = Pos(s) - Pos(s - 1); warn = ahead > 2 ? 1 : ahead < -160 ? 2 : 0; }
            Ends();
            if (auto || parked[s] || ending)
            {
                c.gridHold = true; // (no reverse at rest)
                if (parked[s] || g.idx < 0) return new CarInput(0, 0, 1);
                return Autopilot(Cap(s, g.idx, v, rem), sLat[s], LatW(s, rem), v);
            }
            c.gridHold = false;
            double k = (PlayerMax - v) / (PlayerMax * 0.04 + 0.5); // the formation limiter: the throttle fades out over the last ~3 km/h
            if (k < 1) input.throttle *= k > 0 ? k : 0;
            return input;
        }
        // pure pursuit on the racing line (towards a lateral offset by weight latW), pedals to the speed cap, gently
        CarInput Autopilot(double cap, double lat, double latW, double v)
        {
            var c = g.car; int i = g.idx;
            double Ld = 5 + 0.22 * v; int ia = (i + System.Math.Max(1, (int)System.Math.Round(Ld / tk.ds))) % N;
            double room = tk.HalfWidth(ia) - 1.0;
            double off = System.Math.Max(-room, System.Math.Min(room, g.line.off[ia]));
            if (latW > 0) off += (System.Math.Max(-room, System.Math.Min(room, lat)) - off) * System.Math.Min(1, latW);
            var pa = tk.pts[ia];
            double tx = pa.x + pa.nx * off - c.x, ty = pa.y + pa.ny * off - c.y;
            double ch = System.Math.Cos(c.heading), sh = System.Math.Sin(c.heading);
            double fx = ch * tx + sh * ty, fy = -sh * tx + ch * ty;
            double d2 = System.Math.Max(1.0, fx * fx + fy * fy);
            double delta = System.Math.Atan(c.cfg.wheelbase * 2 * fy / d2);
            double lockNow = c.lockAngle > 1e-4 ? c.lockAngle : c.cfg.maxLockDeg * System.Math.PI / 180;
            double steer = System.Math.Max(-1, System.Math.Min(1, -delta / lockNow));
            double err = cap - v;
            double thr = System.Math.Max(0, System.Math.Min(v < 12 ? 0.35 : 0.6, err * 0.35)), brk = System.Math.Max(0, System.Math.Min(1, -err * 0.45));
            if (cap < 0.05 && v < 0.6) brk = 1;
            if (brk > 0.02) thr = 0;
            return new CarInput(steer, thr, brk);
        }

        // physics step (instead of StepRivals' race code): think at 60 Hz with the formation caps, step every car
        public void RivalsStep()
        {
            var rv = g.rivals;
            if (g.aiTick++ % 4 == 0)
            {
                for (int s = 0; s < M; s++)
                {
                    var d = drv[s]; if (d == null) continue;
                    var c = d.car; double v = System.Math.Sqrt(c.vx * c.vx + c.vy * c.vy); vNow[s] = v;
                    double rem = Rem(s, c);
                    Park(s, v, rem, 4 * H);
                    int i = d.idx >= 0 ? d.idx : lastI[s];
                    d.formCap = Cap(s, i, v, rem);
                    d.formLat = sLat[s]; d.formLatW = LatW(s, rem);
                    bool straight = g.line != null && i >= 0 && i < g.line.N && System.Math.Abs(g.line.curv[i]) < 0.0025;
                    double wv = straight && v > 18 && rem > 450 && t > TvEnd ? 0.9 * System.Math.Sin(t * 2.3 + s * 1.9) : 0;
                    d.formWeave += (wv - d.formWeave) * 0.1;
                }
                for (int k = 0; k < rv.Count; k++)
                {
                    var d = rv[k];
                    d.Think(4 * H, g.simTime, rv, g.PsAiSees(), g.idx, g.playerLat);
                    var rc = d.car; rc.grade = WorldBuilder.Height != null ? WorldBuilder.Height.GradeAlong(rc.x, rc.y, rc.heading) : 0;
                    if (d.idx >= 0) Traffic.Wall(rc, g.track.pts[d.idx], d.lateral, g.wallAt);
                }
                for (int s = 0; s < M; s++) if (drv[s] != null) Advance(s, drv[s].idx);
                for (int i = 0; i < rv.Count; i++)
                {
                    for (int j = i + 1; j < rv.Count; j++) g.RivalContact(i, j); // (not judged before lights out)
                    if (g.pRun == null) g.PlayerContact(i);
                }
            }
            for (int s = 0; s < M; s++)
            {
                var d = drv[s]; if (d == null) continue;
                if (g.aiTick % 4 == 1) { WheelContact.Update(g.track, g.line, d.car, d.idx, d.lateral, g.rubber, g.marbles); Weather.Apply(g.track, g.line, d.car, d.idx, d.lateral, d.tyreSet); }
                var li = d.LastInput; double v = vNow[s];
                if (parked[s]) { d.car.gridHold = true; d.car.Step(new CarInput(0, 0, 1), H); }
                else { d.car.gridHold = v < 0.5 && li.throttle < 0.05; d.car.Step(li, H); }
            }
        }

        // the end: every car parked (a moment later), the leader parked for long enough, a time-out
        void Ends()
        {
            if (ending) return;
            float tf = (float)t;
            bool all = true; for (int s = 0; s < M; s++) if (!parked[s]) { all = false; break; }
            if (parked[0] && leaderParked < 0f) leaderParked = tf;
            if (all && allParked < 0f) allParked = tf;
            if ((allParked >= 0f && tf - allParked > AllHold) || (leaderParked >= 0f && tf - leaderParked > LeaderWait) || t > maxT) Skip();
        }
        void Skip() { if (!ending) { ending = true; endT = 0f; } }

        // ---------------------------------------------------------------- cameras (LateUpdate)
        // false = the game's own camera (the player's view)
        public bool Frame()
        {
            if (g.model == null || g.cam == null || g.car == null) { g.FormAbort(); return false; }
            if (Shot) { shotT = Mathf.Max(0f, g.shotIntroT - g.formShotBase); ShotPose(shotT); }
            else if (ending && !g.Paused)
            {
                endT += Mathf.Min(Time.unscaledDeltaTime, 0.05f);
                if (endT >= Dip) { g.FormDone(); return false; }
            }
            float tc = Clock;
            int shot = tc < ShotA ? 0 : tc < TvEnd ? 1 : EndShot ? 2 : -1;
            if (shot < 0)
            {
                if (tvOn) Handback(); else userCam = g.camMode;
                return false;
            }
            if (!tvOn) { tvOn = true; g.camMode = TvMode; }
            Vector3 pos; Quaternion rot; float fov;
            Vector3 cp = g.model.transform.position;
            if (shot == 0)
            {
                // the field pulls away: low at the track edge 75 m ahead of pole, long lens on the player's car
                Vector3 p = TrackAt(sS[0] + 75, out _, out Vector3 n, out int i);
                float side = sLat[0] >= 0 ? 1f : -1f, edge = Mathf.Min((float)tk.HalfWidth(i) + 1.0f, (float)g.wallAt - 0.6f);
                pos = p + n * (side * edge) + Vector3.up * 0.8f;
                Vector3 subj = cp + Vector3.up * 0.5f; float dist = Mathf.Max(5f, (subj - pos).magnitude);
                fov = Fit(Mathf.Clamp(2f * Mathf.Atan(6.5f / dist) * Mathf.Rad2Deg, 5f, 40f));
                rot = Quaternion.LookRotation(subj - pos, Vector3.up) * Shake(0.06f, 0.5f, 3.7f, tc);
            }
            else if (shot == 1)
            {
                // high behind the player's car, the field streaming away ahead of him
                float e = Smooth01((tc - ShotA) / ShotB);
                Vector3 f = Flat(g.model.transform.forward), r = Vector3.Cross(Vector3.up, f);
                pos = cp - f * (30f - 4f * e) + r * (6f - 3f * e) + Vector3.up * (16f + 2f * e);
                pos = Above(pos, 6f);
                fov = Fit(48f);
                rot = Quaternion.LookRotation(cp + f * 40f - pos, Vector3.up) * Shake(0.15f, 0.4f, 8.1f, tc);
            }
            else
            {
                // back to the grid: low from the front of the grid, long lens on the player's car coming to its slot
                Vector3 p = TrackAt(sS[0] + 38, out _, out Vector3 n, out int i);
                float side = sLat[pSlot] >= 0 ? 1f : -1f, edge = Mathf.Min((float)tk.HalfWidth(i) - 0.6f, (float)g.wallAt - 0.6f);
                pos = p + n * (side * edge) + Vector3.up * 0.7f;
                Vector3 subj = cp + Vector3.up * 0.5f; float dist = Mathf.Max(5f, (subj - pos).magnitude);
                fov = Fit(Mathf.Clamp(2f * Mathf.Atan(5f / dist) * Mathf.Rad2Deg, 4f, 40f));
                rot = Quaternion.LookRotation(subj - pos, Vector3.up) * Shake(0.05f, 0.5f, 12.3f, tc);
            }
            g.cam.transform.SetPositionAndRotation(pos, rot);
            g.cam.fieldOfView = fov; g.cam.nearClipPlane = ChaseNear;
            g.camVel = Vector3.zero;
            g.CockpitBits(false); // seen from outside: helmet and halo, the driver's head and hands
            return true;
        }
        bool EndShot => Shot ? shotT >= ShotEnd && shotT < ShotParked : autoEnd;

        // the player's own camera back (a clean cut: no chase swoop)
        void Handback()
        {
            tvOn = false;
            g.camMode = userCam; g.tvWasOn = true; g.camVel = Vector3.zero; g.tvCur = -1; g.tvCarIdx = -1;
            if (userCam == 2) g.cam.fieldOfView = g.fovCockpit;
            else if (userCam == 3) g.cam.fieldOfView = g.fovCockpit + 11f + 0.06f * g.haloBackPct;
            else if (userCam != TvMode) g.cam.fieldOfView = Fit(g.fovChase);
        }

        // ---------------------------------------------------------------- the end: everything back
        // snap = the lap is over (the cars onto their slots, lap state reset); false = dropped (a new start, an error)
        public void Finish(bool snap)
        {
            try
            {
                for (int s = 0; s < M; s++)
                {
                    var d = drv[s]; if (d == null) continue;
                    d.formation = false; d.formCap = double.MaxValue; d.formLatW = 0; d.formWeave = 0;
                    if (!snap || !g.rivals.Contains(d)) continue;
                    StartGrid.Pose(tk, s, out double x, out double y, out double h, out int i);
                    d.Place(i, sLat[s]); d.car.x = x; d.car.y = y; d.car.heading = h; d.car.delta = 0;
                    d.lapStart = d.lastLap = d.bestLap = double.NaN; d.car.gridHold = true;
                }
                if (tvOn) Handback(); else g.camMode = userCam;
                if (snap && g.car != null && g.track == tk)
                {
                    g.ParkOnGrid();
                    g.lapStart = null; g.pLap = 0; g.lapNo = 1; g.cut = false; g.lastValid = true; g.offTime = g.wrongTime = 0;
                    g.gridThr = 0; g.gridRpm = System.Math.Max(g.car.cfg.idleRpm, g.car.rpm); g.jumped = false; g.raceTime = 0;
                    g.steerReset = true; g.HudReset(false); g.ClearRecording();
                    g.tvWasOn = true; g.camVel = Vector3.zero;
                }
            }
            catch (System.Exception e) { Debug.LogWarning("Formation finish: " + e.Message); }
        }

        // ---------------------------------------------------------------- shot mode: the cars posed along the lap at tf
        void ShotPose(float tf)
        {
            double L = tk.length;
            for (int s = 0; s < M; s++)
            {
                double along, lat, v; int i;
                if (tf < TvEnd)
                {
                    double go = System.Math.Max(0, tf - 0.55 * s), tr = 0.9 * go * go; v = 1.8 * go;
                    along = sS[s] + tr; i = Idx(along);
                    lat = sLat[s] + (g.line.off[i] - sLat[s]) * Smooth01((float)(tr / 40));
                }
                else if (tf < ShotEnd)
                {
                    v = 36; along = sS[0] + 650 + 36 * (tf - TvEnd) - s * 26; i = Idx(along);
                    lat = g.line.off[i] + 0.8 * System.Math.Sin(tf * 2.3 + s * 1.9);
                }
                else
                {
                    double dt = System.Math.Max(0, 34.5 + 0.35 * s - tf), rem = System.Math.Min(400, 0.5 * StopA * dt * dt); v = StopA * dt;
                    along = sS[s] + L - rem; i = Idx(along);
                    lat = g.line.off[i] + (sLat[s] - g.line.off[i]) * Smooth01((float)((LatZone - rem) / 220));
                }
                double room = tk.HalfWidth(i) - 1.0; lat = System.Math.Max(-room, System.Math.Min(room, lat));
                int a = (i + N - 1) % N, b = (i + 1) % N;
                double head = System.Math.Atan2(g.line.y[b] - g.line.y[a], g.line.x[b] - g.line.x[a]);
                var d = drv[s];
                if (d != null) { d.Place(i, lat, v); d.car.heading = head; d.car.gridHold = false; continue; }
                var c = g.car; var p = tk.pts[i];
                c.x = p.x + p.nx * lat; c.y = p.y + p.ny * lat; c.heading = head; c.vx = v; c.vy = 0; c.r = 0; c.delta = 0;
                g.SnapDrawn();
            }
            auto = true; autoEnd = tf >= ShotEnd; warn = 0;
        }
        int Idx(double along) { int i = (int)System.Math.Round(along / tk.ds); return ((i % N) + N) % N; }

        // ---------------------------------------------------------------- overlays
        // TV shots: letterbox, a lower third, SKIP; true = no HUD. The player's own view: false (Hud draws over the HUD).
        public bool Gui()
        {
            if (!tvOn) return false;
            var et = Event.current.type;
            float W = Screen.width, Hs = Screen.height, lb = UiKit.U(18f);
            g.HudInit(); Measure();
            if (et == EventType.Repaint)
            {
                UiKit.Rect(new Rect(0, 0, W, lb), Color.black); UiKit.Rect(new Rect(0, Hs - lb, W, lb), Color.black);
                bool end = EndShot; float tc = Clock;
                float a = end ? 1f : Mathf.Clamp01((tc - 0.3f) / 0.4f) * Mathf.Clamp01((TvEnd - 0.15f - tc) / 0.3f);
                if (a > 0.01f) Lower(end ? eEye : cEye, end ? eTitle : cTitle, end ? eSub : cSub, lb, a);
            }
            SkipButton(Hs - lb - UiKit.U(12) - UiKit.U(24), true);
            if (et == EventType.Repaint) Black();
            return true;
        }
        // the player's own view: the formation banner, a hint, SKIP and the dip at the end
        public void Hud()
        {
            if (tvOn) return;
            var et = Event.current.type;
            Measure();
            float y = g.hudT + g.S(2);
            if (et == EventType.Repaint)
            {
                // banner: red block + glass, top centre
                float h = g.S(24), tw = wTitle + g.S(20), sw = wSub + g.S(24), w = tw + sw;
                float x = Mathf.Round(Screen.width * 0.5f - w * 0.5f);
                g.RR(new Rect(x, y, tw, h), HudAccent, 8, 0, 0, 8);
                TF(new Rect(x, y, tw, h), tTitle, UiKit.Black, 9.5f, Color.white, TextAnchor.MiddleCenter);
                g.RR(new Rect(x + tw, y, sw, h), HudPanel, 0, 8, 8, 0);
                TF(new Rect(x + tw + g.S(12), y, sw, h), tSub, UiKit.Semi, 7.5f, Color.white);
                // hint / warning chip
                int ci = autoEnd ? 1 : auto ? 0 : warn == 1 ? 2 : warn == 2 ? 3 : -1;
                if (ci >= 0)
                {
                    string ct = ci == 0 ? tAutoGo : ci == 1 ? tAutoStop : ci == 2 ? tNoPass : tCatch;
                    bool hot = ci == 2 && Mathf.Repeat(Time.unscaledTime * 1.6f, 1f) < 0.55f;
                    float cw = wChip[ci] + g.S(26), chh = g.S(18), cx = Mathf.Round(Screen.width * 0.5f - cw * 0.5f), cy = y + h + g.S(36);
                    Color bg = ci == 2 ? (hot ? HudYellow : HudPanel) : ci == 3 ? HudPanel : new Color(0.03f, 0.04f, 0.06f, 0.82f);
                    Color fg = ci == 2 ? (hot ? new Color(0.05f, 0.05f, 0.06f) : HudYellow) : ci == 3 ? HudAmber : HudCyan;
                    Disc(new Rect(cx, cy, cw, chh), bg);
                    TF(new Rect(cx, cy, cw, chh), ct, UiKit.Bold, 6.8f, fg, TextAnchor.MiddleCenter);
                }
            }
            if (!ending) SkipButton(y + g.S(28), false);
            if (et == EventType.Repaint) Black();
        }
        void SkipButton(float y, bool tv)
        {
            if (ending && !Shot) return;
            float w = wSkip + g.S(30), h = g.S(22);
            var r = tv ? new Rect(UiKit.Safe.xMax - UiKit.U(16) - w, y, w, h) : new Rect(Mathf.Round(Screen.width * 0.5f - w * 0.5f), y, w, h);
            if (g.PillBtn(r, tSkip, null, HudAccent, false, 7.5f) && !Shot) Skip();
        }
        void Black()
        {
            if (!ending) return;
            float a = Mathf.Clamp01(endT / Dip);
            if (a > 0.004f) UiKit.Rect(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, a));
        }
        // text widths, once per screen size (measuring allocates)
        void Measure()
        {
            if (wKey == Screen.height) return;
            wKey = Screen.height;
            wTitle = UiKit.Width(tTitle, UiKit.Black, 9.5f); wSub = UiKit.Width(tSub, UiKit.Semi, 7.5f); wSkip = UiKit.Width(tSkip, UiKit.Bold, 7.5f);
            wChip[0] = UiKit.Width(tAutoGo, UiKit.Bold, 6.8f); wChip[1] = UiKit.Width(tAutoStop, UiKit.Bold, 6.8f);
            wChip[2] = UiKit.Width(tNoPass, UiKit.Bold, 6.8f); wChip[3] = UiKit.Width(tCatch, UiKit.Bold, 6.8f);
            wChip[4] = Mathf.Max(Mathf.Max(UiKit.Width(cTitle, UiKit.Black, 12.5f), UiKit.Width(cSub, UiKit.Semi, 7.6f)), Mathf.Max(UiKit.Width(eTitle, UiKit.Black, 12.5f), UiKit.Width(eSub, UiKit.Semi, 7.6f)));
        }
        // the F1-TV lower third, bottom left (eyebrow, title bar, sub bar)
        void Lower(string eye, string title, string sub, float lb, float a)
        {
            var safe = UiKit.Safe;
            float pad = UiKit.U(10), h1 = UiKit.U(27), h2 = UiKit.U(17), eh = UiKit.U(13), gap = UiKit.U(3);
            float w = wChip[4] + 2f * pad + UiKit.U(4);
            float x = safe.x + UiKit.U(18), y0 = safe.yMax - lb - UiKit.U(16) - (eh + gap + h1 + h2);
            UiKit.PushAlpha(a);
            UiKit.Rect(new Rect(x, y0 + UiKit.U(2), UiKit.U(3), eh - UiKit.U(4)), UiKit.AccentHi);
            UiKit.TextShadow(new Rect(x + UiKit.U(7), y0, w, eh), eye, UiKit.Bold, 6.6f, new Color(1f, 1f, 1f, 0.92f));
            float y1 = y0 + eh + gap;
            UiKit.Rect(new Rect(x, y1, w, h1), new Color(0.035f, 0.04f, 0.055f, 0.9f));
            UiKit.Rect(new Rect(x, y1, UiKit.U(3), h1 + h2), UiKit.Accent);
            UiKit.Text(new Rect(x + pad, y1, w - pad, h1), title, UiKit.Black, 12.5f, Color.white);
            UiKit.Rect(new Rect(x, y1 + h1, w, h2), new Color(0.13f, 0.14f, 0.17f, 0.88f));
            UiKit.Text(new Rect(x + pad, y1 + h1, w - pad, h2), sub, UiKit.Semi, 7.6f, new Color(0.86f, 0.88f, 0.92f, 1f));
            UiKit.PopAlpha();
        }

        // ---------------------------------------------------------------- helpers
        Vector3 TrackAt(double s, out Vector3 tangent, out Vector3 normal, out int idx)
        {
            double fi = s / tk.ds;
            int i0 = (int)System.Math.Floor(fi); float a = (float)(fi - i0);
            i0 = ((i0 % N) + N) % N; int i1 = (i0 + 1) % N;
            var p = tk.pts[i0]; var q = tk.pts[i1];
            float x = (float)(p.x + (q.x - p.x) * a), z = (float)(p.y + (q.y - p.y) * a);
            tangent = Flat(new Vector3((float)(p.tx + (q.tx - p.tx) * a), 0f, (float)(p.ty + (q.ty - p.ty) * a)));
            normal = Flat(new Vector3((float)(p.nx + (q.nx - p.nx) * a), 0f, (float)(p.ny + (q.ny - p.ny) * a)));
            idx = a < 0.5f ? i0 : i1;
            return new Vector3(x, WorldBuilder.H(x, z), z);
        }
        float Fit(float fov)
        {
            float asp = Mathf.Max(0.5f, g.cam.aspect);
            return asp > 16f / 9f ? 2f * Mathf.Rad2Deg * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * (16f / 9f) / asp) : fov;
        }
        static Quaternion Shake(float amp, float hz, float seed, float time)
        {
            float tt = time * hz;
            return Quaternion.Euler((Mathf.PerlinNoise(tt, seed) - 0.5f) * 2f * amp, (Mathf.PerlinNoise(seed + 13.1f, tt * 0.9f) - 0.5f) * 2.4f * amp, (Mathf.PerlinNoise(tt * 0.7f, seed + 31.7f) - 0.5f) * 1.2f * amp);
        }
        static Vector3 Above(Vector3 p, float clear) { if (WorldBuilder.Height != null) p.y = Mathf.Max(p.y, WorldBuilder.H(p.x, p.z) + clear); return p; }
        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward; }
        static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
    }
}
