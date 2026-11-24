// AiDriver.cs — computer-controlled drivers. Pure C# (no UnityEngine), so the same code runs in the game
// and in the headless trainer (Tools/PhysicsCheck, "train"), which drives thousands of laps per minute.
//
// How a driver works:
//  * every rival drives the SAME physics as the player (FormulaCar), with pedals and a steering wheel;
//  * it follows the racing line (pure-pursuit steering) and the speed profile (when to brake, how fast in corners);
//  * skill = how much of the tyre grip and braking the driver dares to use, how steady he is lap after lap,
//    and how often he brakes too late. That is what separates real drivers too.
//  * the control gains below were found by the trainer (an evolution search over full simulated laps).
//  * race craft (AiRacecraft.cs): what he does about the other cars (and the player): follows without running into
//    them, leaves room side by side, attacks, defends. With nobody near it changes nothing.
using System;
using System.Collections.Generic;

namespace FormulaSim
{
    // controller gains, found by the trainer (see Tools/PhysicsCheck/Program.cs, "train")
    public class AiTuning
    {
        public double look0 = 5.0;     // m, steering look-ahead at standstill
        public double lookV = 0.20;    // s, extra look-ahead per m/s
        public double preview = 0.12;  // s, how far ahead the speed target is read
        public double kThr = 0.40;     // throttle per m/s below target
        public double kBrk = 0.45;     // brake per m/s above target
        public double grip = 0.90;     // share of tyre grip used by a perfect driver
        public double brakeUse = 0.85; // share of braking used by a perfect driver

        public double[] ToArray() => new[] { look0, lookV, preview, kThr, kBrk, grip, brakeUse };
        public static AiTuning From(double[] a) => new AiTuning { look0 = a[0], lookV = a[1], preview = a[2], kThr = a[3], kBrk = a[4], grip = a[5], brakeUse = a[6] };

        // trained values per circuit (index: 0 test track, 1 Monaco, 2 Spa)
        public static AiTuning For(int circuit) => circuit >= 0 && circuit < Trained.Length ? From(Trained[circuit]) : new AiTuning();
        public static readonly double[][] Trained =
        {
            /*TRAINED*/
            new[] { 4.480, 0.135, 0.112, 0.385, 0.144, 0.984, 0.789 },
            new[] { 6.888, 0.166, 0.109, 0.723, 0.485, 0.931, 0.891 },
            new[] { 4.286, 0.161, 0.082, 0.560, 0.180, 0.923, 0.882 },
            new[] { 4.882, 0.175, 0.106, 0.601, 0.321, 0.966, 0.838 },
            new[] { 5.651, 0.193, 0.053, 0.669, 0.160, 0.924, 0.873 },
            new[] { 5.958, 0.165, 0.112, 0.614, 0.242, 0.931, 0.874 },
            new[] { 5.879, 0.158, 0.103, 0.884, 0.238, 0.948, 0.880 },
            new[] { 7.495, 0.217, 0.040, 0.695, 0.302, 0.899, 0.931 },
            new[] { 3.257, 0.195, 0.082, 0.507, 0.321, 0.927, 0.795 },
            new[] { 6.208, 0.156, 0.130, 0.753, 0.156, 0.951, 0.936 },
            new[] { 2.384, 0.128, 0.091, 0.599, 0.146, 0.902, 0.896 },
            /*END*/
        };
    }

    // A fictional driver. The spread of ability follows a real grid: a few stars, a strong midfield, some backmarkers.
    public class DriverProfile
    {
        public string name, team;
        public double pace;        // 0..1 raw speed (grip and braking he dares to use)
        public double consistency; // 0..1 how steady from lap to lap, how rarely he makes mistakes
        public double aggression;  // 0..1 how close he follows and how readily he pulls out to pass
        public float r, g, b;      // team colour

        static DriverProfile D(string n, string t, double p, double c, double a, float r, float g, float b) =>
            new DriverProfile { name = n, team = t, pace = p, consistency = c, aggression = a, r = r, g = g, b = b };
        // invented names and teams (no real drivers, teams or liveries)
        public static readonly DriverProfile[] Grid =
        {
            D("K. Arden",    "Argent",  1.00, 0.95, 0.70, 0.75f, 0.78f, 0.80f),
            D("M. Voss",     "Midnight Comet",    0.99, 0.97, 0.95, 0.10f, 0.13f, 0.35f),
            D("L. Hartwell", "Vermilion",         0.96, 0.96, 0.75, 0.75f, 0.05f, 0.07f),
            D("N. Lindqvist","Solstice",        0.95, 0.93, 0.60, 1.00f, 0.50f, 0.05f),
            D("G. Rowe",     "Argent",  0.93, 0.94, 0.65, 0.75f, 0.78f, 0.80f),
            D("O. Pascoe",   "Solstice",        0.92, 0.90, 0.55, 1.00f, 0.50f, 0.05f),
            D("I. Haddad",   "Midnight Comet",    0.88, 0.82, 0.80, 0.10f, 0.13f, 0.35f),
            D("C. Salazar-Rey","Blue Works",    0.85, 0.92, 0.60, 0.05f, 0.30f, 0.75f),
            D("A. Albrecht", "Blue Works",    0.83, 0.90, 0.50, 0.05f, 0.30f, 0.75f),
            D("F. Alvaro",   "Emerald",       0.82, 0.96, 0.85, 0.02f, 0.40f, 0.30f),
            D("P. Garnier",  "Glacier",   0.78, 0.88, 0.65, 0.20f, 0.50f, 0.95f),
            D("N. Holz",     "Quadrant",    0.75, 0.93, 0.45, 0.55f, 0.57f, 0.60f),
            D("E. Okafor",   "Meridian",         0.72, 0.85, 0.70, 0.90f, 0.90f, 0.92f),
            D("Y. Tanaka",   "Comet Academy",   0.68, 0.80, 0.75, 0.25f, 0.35f, 0.85f),
            D("O. Bergman",  "Meridian",         0.65, 0.78, 0.80, 0.90f, 0.90f, 0.92f),
            D("G. Bastos",   "Quadrant",    0.60, 0.80, 0.55, 0.55f, 0.57f, 0.60f),
            D("F. Colombo",  "Glacier",   0.52, 0.70, 0.75, 0.20f, 0.50f, 0.95f),
            D("A. Lindahl",  "Comet Academy",   0.48, 0.72, 0.60, 0.25f, 0.35f, 0.85f),
            D("S. Paredes",  "Harbour",       0.40, 0.84, 0.50, 0.85f, 0.85f, 0.20f),
            D("V. Bergstrom","Harbour",       0.33, 0.86, 0.35, 0.85f, 0.85f, 0.20f),
            D("L. Strand",   "Emerald",       0.15, 0.62, 0.55, 0.02f, 0.40f, 0.30f),
        };
    }

    public partial class AiDriver
    {
        public readonly FormulaCar car;
        public readonly DriverProfile who;
        public int idx = -1;              // nearest track point
        public double lateral;            // m from the centreline (+ = left)
        public int lap; public double lapStart = double.NaN, lastLap = double.NaN, bestLap = double.NaN;
        public double progress;           // laps + fraction, for race positions
        public double offset;             // current sideways offset from the racing line (traffic)
        public bool recovered;
        public double target;             // the speed target of the last Think (m/s), for diagnostics
        // pit stops for damage: a heavily damaged car boxes at the next crossing of the line for a new wing (and
        // tyres). The time lost is taken off its race distance in progress, as for the player's stops.
        public const double PitLoss = 22, WingChange = 8;   // s: pit lane and stop (as GameManager.PitLoss), + nose change
        public int pitStops; public double pitLoss; public bool boxing;
        // race craft (RaceControl.cs). All optional: with rc and strat null the driver races exactly as before.
        public RaceControl rc; public int slot = -1;   // flags, VSC / safety car, blue flags (slot = this car in rc.cars)
        public TyreStrategy strat; public int raceLaps; // tyre plan for a race of raceLaps laps
        public int tyreSet; public int setSerial;       // set on the car (TyreSets numbering), +1 at every change of set
        public double paceScale = 1;                    // the game's grip factor of the set fitted against the set planned for
        public int boxReason;                           // 0 plan, 1 damage, 2 cheap stop under the safety car, 3 weather
        bool scChecked;
        // the pit lane (PitLane.cs, set by the game): with it a stop is driven down the lane into the team's box (pitBay)
        // instead of the instant stop at the line; without it (the headless tools) everything is as before
        public PitLane pitLane; public int pitBay = -1; public PitRun pit; int pitLapAfter;
        public bool InPit => pit != null;
        // the formation lap (set by the game; formation false = everything exactly as before): no lap is counted, no pit
        // entry, no passing, no ERS buttons, never "stuck" while it waits in the queue. formCap caps the speed (m/s: the
        // pace, the gap to the car ahead in grid order, the stop on its grid slot); formLat (m from the centreline, + = left)
        // pulls the aim point towards its grid slot's side by weight formLatW (0..1); formWeave (m) weaves on the straights
        public bool formation; public double formCap = double.MaxValue, formLat, formLatW, formWeave;
        // the set the next stop will most likely fit (for the crew's tyres)
        public int PredictedSet => strat != null && strat.next + 1 < strat.sets.Count ? strat.sets[strat.next + 1] : tyreSet;

        // Plan the race (call once, after the constructor): returns the start set and fits its compound
        public int PlanRace(int laps, int wet, int seed)
        {
            string cid = track.def != null ? track.def.id : "test";
            raceLaps = laps; strat = new TyreStrategy(seed, cid);
            // the game's "Tyre wear" setting scales the wear (CarConfig.tyreLife): the plan starts from that rate
            double rate = car.cfg.tyreLife != 1 || TyreRules.Strong ? TyreStrategy.BaseSoftRate(cid) / Math.Max(1e-3, car.cfg.tyreLife) * (TyreRules.Strong ? TyreRules.PlanWear : 1) : 0;
            tyreSet = strat.Plan(laps, wet, cid, RefLap, rate);
            TyreRules.Fit(car.cfg, tyreSet); setSerial++;
            return tyreSet;
        }
        public void SetWeather(int wet) { if (strat != null) strat.WeatherChanged(wet, lap); }

        readonly Track track; readonly RacingLine line; readonly AiTuning tune;
        readonly double[] speed;          // this driver's own target speeds
        readonly double[] speedFull;      // the same on a full tank (null when the car starts in qualifying trim)
        readonly double[] deploy;         // this driver's MGU-K deploy map (RacingLine.deploy for his own profile), or null
        readonly Random rng;
        double offsetTarget, lapPace = 1, mistakeT, stuckT, offT, speedCap = double.MaxValue;
        bool wasBraking; int prevIdx = -1;
        CarInput input;
        // Overtake Mode and boost (FormulaCar.otOn / boostOn). Own dice, so the driving's random sequence (lap pace,
        // mistakes) is the same as without them. aheadGap / behindGap: m along the track to the nearest car (this Think).
        readonly Random ersRng;
        bool otSave, otWasArmed, boostWant, wasStraight;
        double aheadGap, behindGap;

        // difficulty scales every rival (1 = real pace)
        public AiDriver(Track track, RacingLine line, CarConfig cfg, DriverProfile who, AiTuning tune, double difficulty = 1, int seed = 1)
        {
            this.track = track; this.line = line; this.who = who; this.tune = tune;
            rng = new Random(seed * 7919 + 13); ersRng = new Random(seed * 104729 + 7); craftRng = new Random(seed * 92821 + 101);
            var c = cfg.Clone(); c.tc = TcMode.High; c.abs = true; c.autoShift = true; c.steerSpeed = 14; c.steerReturn = 16;
            car = new FormulaCar(c); car.SeedDamage(seed);
            double grip = tune.grip * difficulty * (0.86 + 0.14 * who.pace), brake = tune.brakeUse * difficulty * (0.84 + 0.16 * who.pace);
            // SpeedProfile stores its result in the line object; keep a private copy and leave the line as it was
            var keepS = line.speed; var keepB = line.braking; var keepP = line.phase; var keepC = line.vCorner; double keepT = line.lapTime;
            var keepD = line.deploy; var keepE = line.soc;
            if (c.fuel && c.fuelStart != c.fuelRef)
            {
                // a race start with more fuel than the reference trim: one profile for the full tank, one for empty,
                // and Think blends them by the fuel left (the heavy car brakes earlier and corners a little slower)
                speedFull = (double[])line.SpeedProfile(c.WithFuel(c.fuelStart), grip, brake).Clone();
                speed = (double[])line.SpeedProfile(c.WithFuel(0), grip, brake).Clone();
            }
            else speed = (double[])line.SpeedProfile(c, grip, brake).Clone();
            deploy = line.deploy != null ? (double[])line.deploy.Clone() : null;
            line.speed = keepS; line.braking = keepB; line.phase = keepP; line.vCorner = keepC; line.lapTime = keepT; line.deploy = keepD; line.soc = keepE;
            // a late-braking dive (race craft only) brakes nearer the car's real limit instead
            // of his everyday share (up to 98 %), more of the gap for the bold (never less than his own profile)
            diveK = Math.Max(1, Math.Min(DiveK0 + DiveKA * who.aggression, 0.98 / Math.Max(0.5, brake)));
            FitGrip();   // race craft: the grip this driver uses (AiRacecraft.cs; changes nothing when he drives alone)
        }

        public void Place(int trackIdx, double lateralOffset, double v = 0)
        {
            int N = track.N; trackIdx = ((trackIdx % N) + N) % N;
            var p = track.pts[trackIdx];
            car.x = p.x + p.nx * lateralOffset; car.y = p.y + p.ny * lateralOffset; car.heading = Math.Atan2(p.ty, p.tx);
            car.vx = v; car.vy = 0; car.r = 0; car.gear = 1; car.wR = double.NaN;
            idx = prevIdx = trackIdx; offset = offsetTarget = lateralOffset - line.off[trackIdx];
            // a grid slot past the timing line (CircuitDef.gridOffset < 0) is already on lap 1: the car will not cross
            // the line at the start, so it counts as if it had (keeps race positions right across a grid that spans the line)
            lap = trackIdx < N / 2 ? 1 : 0; progress = lap + (double)trackIdx / N;
        }

        // Decide pedals and steering. Call about 60 times a second; then step the car with Input.
        public CarInput Think(double dt, double simTime, IList<AiDriver> others, FormulaCar player, int playerIdx, double playerLateral)
        {
            int N = track.N;
            if (pit != null) { recovered = false; car.otOn = car.boostOn = false; input = new CarInput(0, 0, 0); return input; } // in the pit lane: PitStep drives the car
            var near = track.Nearest(car.x, car.y, idx);
            prevIdx = idx < 0 ? near.idx : idx; idx = near.idx; lateral = near.lateral;
            double v = Math.Sqrt(car.vx * car.vx + car.vy * car.vy);

            // laps (none on the formation lap: the game puts the car back in its grid slot after it)
            if (formation) { }
            else if (prevIdx > N * 0.8 && idx < N * 0.2)
            {
                if (!double.IsNaN(lapStart)) { lastLap = simTime - lapStart; if (double.IsNaN(bestLap) || lastLap < bestLap) bestLap = lastLap; }
                lapStart = simTime; lap++; car.NewLap();
                if (boxing && pitLane == null)
                {
                    pitLoss += (rc != null ? rc.PitLoss(PitLoss) : PitLoss) + (car.damage.WingsDamaged ? WingChange : 0);
                    Service(lap);
                }
                if (strat != null && raceLaps > 0 && !boxing && strat.BoxThisLap(lap, Math.Max(car.wearF, car.wearR))) { boxing = true; boxReason = 0; }
                lapPace = 1 + (rng.NextDouble() - 0.5) * 0.02 * (1 - who.consistency); // good laps and bad laps
            }
            else if (prevIdx < N * 0.2 && idx > N * 0.8) lap--;
            if (car.cfg.damage && car.damage.NeedsPit && !boxing) { boxing = true; boxReason = 1; }
            // race control: no overtaking under VSC / SC, blue flags, a cheap stop under the safety car
            bool noPass = formation, blueFlag = false;
            if (rc != null)
            {
                noPass = noPass || rc.NoOvertaking; blueFlag = rc.Blue(slot);
                if (rc.ScOnTrack) { if (!scChecked && strat != null && !boxing) { scChecked = true; if (strat.CheapStop(lap, Math.Max(car.wearF, car.wearR))) { boxing = true; boxReason = 2; } } }
                else scChecked = false;
            }
            // boxing, at the pit entry (and not on the last lap): into the pit lane
            if (boxing && !formation && pitLane != null && raceLaps > 0 && lap >= 1 && lap < raceLaps && prevIdx >= 0 && v > 3)
            {
                double u0 = PitLane.RelS(track, prevIdx), u1 = PitLane.RelS(track, idx);
                if (u0 < pitLane.sEntry && u1 >= pitLane.sEntry && u1 - u0 < 30)
                {
                    pit = new PitRun(pitLane, pitBay, this, u1, v, lateral, car.damage.WingsDamaged ? WingChange : 0);
                    pitLapAfter = lap + 1;
                    progress = lap + (double)idx / N - pitLoss / RefLap;
                    recovered = false; input = new CarInput(0, 0, 0); return input;
                }
            }
            progress = lap + (double)idx / N - pitLoss / RefLap;
            if (deploy != null) car.deployMap = deploy[idx];   // the same energy rule as the player's car, with its own map

            // ---- traffic: every car around, the player too (race craft: AiRacecraft.cs) ----
            speedCap = double.MaxValue; aheadGap = behindGap = 1e9;
            // overtaking needs a straight: no pulling out where the line is curving
            bool straight = Math.Abs(line.curv[idx]) < 0.004 && Math.Abs(line.curv[(idx + 15) % N]) < 0.006;
            if (!Racecraft(dt, v, straight, noPass, blueFlag, others, player, playerIdx, playerLateral, simTime))
            {
                // nobody within reach: back to the racing line.
                // The aim point is clamped to the track (room, below), and the racing line already runs within 0.3 m of
                // that clamp at most apexes. A sideways offset (traffic) carried into a bend therefore pins the aim point
                // to the track edge, whose bends are much tighter than the line's: at racing-line speed the car snaps
                // round or runs wide. So the offset may only use the room the line leaves over the next 1.5 s (40 m at
                // least) and is wound back in time before the bend, keeping the path a parallel copy of the line.
                // (In traffic the race craft drives an off-line lane at the speed its own curvature allows: PathCap.)
                offsetTarget *= Math.Max(0, 1 - dt * 0.8);
                WindowClamp(v);
                offset += Clamp(offsetTarget - offset, -2.2 * dt, 2.2 * dt);
            }

            // ---- steering: aim at a point on the line ahead (pure pursuit) ----
            double Ld = tune.look0 + tune.lookV * v;
            int ia = (idx + Math.Max(1, (int)Math.Round(Ld / track.ds))) % N;
            double room = track.HalfWidth(ia) - 1.0;
            double offA = Clamp(line.off[ia] + offset, -room, room);
            if (bounds > 0) offA = ApplyBounds(ia, offA, Ld / Math.Max(5, v), room);   // leave room for the cars beside
            if (formation) { if (formLatW > 0) offA += (Clamp(formLat, -room, room) - offA) * Math.Min(1, formLatW); offA = Clamp(offA + formWeave, -room, room); }
            var pa = track.pts[ia];
            double tx = pa.x + pa.nx * offA - car.x, ty = pa.y + pa.ny * offA - car.y;
            double ch = Math.Cos(car.heading), sh = Math.Sin(car.heading);
            double fx = ch * tx + sh * ty, fy = -sh * tx + ch * ty; // target in the car frame (fy + = left)
            double d2 = Math.Max(1.0, fx * fx + fy * fy);
            double delta = Math.Atan(car.cfg.wheelbase * 2 * fy / d2);
            double lockNow = car.lockAngle > 1e-4 ? car.lockAngle : car.cfg.maxLockDeg * Math.PI / 180;
            double steer = Clamp(-delta / lockNow, -1, 1);

            // ---- speed: follow this driver's own speed profile ----
            double dmgScale = 1;   // damage: share of the profile's speed the car can carry now (below)
            int ip = (idx + (int)Math.Round(tune.preview * v / track.ds)) % N;
            double V(int i) => speedFull == null ? speed[i] : speed[i] + (speedFull[i] - speed[i]) * (double.IsNaN(car.fuel) ? 1 : Clamp(car.fuel / car.cfg.fuelStart, 0, 1));
            bool brakingZone = V((ip + 1) % N) < V(ip) - 0.05;
            if (brakingZone && !wasBraking && rng.NextDouble() < 0.10 * (1 - who.consistency)) mistakeT = 0.9; // brakes too late now and then
            wasBraking = brakingZone;
            if (mistakeT > 0) mistakeT -= dt;
            double vt = Math.Min(V(ip), V(idx) + 3) * lapPace * (mistakeT > 0 ? 1.07 : 1);
            // a late-braking dive (race craft, AiRacecraft.cs): the braking zone at the car's real limit
            if (diveOn && brakingZone) vt = Math.Max(vt, Math.Min(DiveSpeed(ip), DiveSpeed(idx) + 3) * lapPace);
            if (Math.Abs(offset) > 0.8 && !diveOn) vt *= 0.985;   // off the ideal line: a little slower (not in a dive: that is the point of it)
            // off the line in a bend (traffic only: alone the offset is 0): no running ahead of the profile out of the bend,
            // and with the car already sliding, no more speed until it grips (an off-line lane is not what the profile is for)
            double slideHold = double.MaxValue;
            if (Math.Abs(offset) > 0.3 && Math.Abs(line.curv[idx]) > 0.004)
            {
                vt = Math.Min(vt, (diveOn ? DiveSpeed(idx) : V(idx)) * lapPace);
                // hold the speed it has (applied after the grip factors below: scaling the held speed again every think
                // braked the car down to a crawl while it slid)
                if (Math.Abs(Math.Atan2(car.vy, Math.Max(5, car.vx))) > 0.045) slideHold = v;
            }
            // drive to the grip the tyres have now (cold, overheated or worn tyres). Without downforce corner speed goes
            // with grip^(1/2); with it the load itself grows with speed, so corner speed reacts more strongly:
            // m v^2 k = mu (W + D v^2)^e gives v ~ mu^(1 / (2 - 2 e s)), s = the downforce share of the load: about
            // mu^0.6 at 100 km/h, mu^0.8 at 200, mu^1.1 at 300 (with a plain sqrt the AI overdrove fast bends on cold tyres).
            {
                double down = 0.5 * 1.225 * car.cfg.clA * vt * vt, share = down / (down + car.cfg.mass * 9.81);
                vt *= Math.Pow(Clamp(Math.Min(car.gripF, car.gripR), 0.8, 1.0), 1 / (2 - 2 * car.cfg.loadExp * share));
                // dirty air (the game's tow: downforce lost right behind another car): a bend is taken that much slower
                if (car.tow > 0 && Math.Max(Math.Abs(line.curv[ip]), Math.Abs(line.curv[idx])) > 0.003) vt *= Math.Pow(1 - car.cfg.towDown * car.tow * share, 1 / (2 - 2 * car.cfg.loadExp * share));
                // damage (lost downforce, bent suspension, a flat tyre): the same rule, from the grip the damage leaves
                if (car.cfg.damage && car.damage.Any) { dmgScale = Math.Pow(Clamp(car.DamageGrip(vt), 0.3, 1.0), 1 / (2 - 2 * car.cfg.loadExp * share)); vt *= dmgScale; }
            }
            vt = Math.Min(vt, slideHold);
            double vtFree = vt;
            vt = Math.Min(vt, Math.Min(speedCap, pathCap));   // traffic and the own lane's curvature (both off when alone)
            held = vt < vtFree - 0.3;
            bool capped = false;   // held below its own speed by race control: no feed-forward braking, no flat-out rule
            if (paceScale != 1) vt *= paceScale;
            if (rc != null)
            {
                double cap = rc.Cap(slot, Math.Min(V(ip), V(idx) + 3) * dmgScale);
                if (blueFlag && straight) cap = Math.Min(cap, vt * 0.9);   // blue flag: lift on the straight and let him by
                if (cap < vt) { vt = cap; capped = true; }
            }
            if (formation && formCap < vt) { vt = Math.Max(0, formCap); capped = true; }   // the formation lap's pace / queue / grid slot
            target = vt;
            double err = vt - v;
            double thr = Clamp(err * tune.kThr, 0, 1), brk = Clamp(-err * tune.kBrk, 0, 1);
            if (brakingZone && !capped && car.cfg.brakeFullGrip && car.cfg.brakeForce > 0)
            {
                // feed-forward: the pedal the profile's deceleration needs (less drag), corrected by the speed error, so
                // the car brakes as hard as its profile says (5 g and more at speed) instead of only on an error
                int iq = (ip + 1) % N; double dsq = Math.Max(0.1, track.ds);
                double vb0 = diveOn ? DiveSpeed(ip) : V(ip), vb1 = diveOn ? DiveSpeed(iq) : V(iq);   // a dive brakes harder, later
                double aReq = Math.Max(0, (vb0 * vb0 - vb1 * vb1) / (2 * dsq)) * lapPace * lapPace * (mistakeT > 0 ? 0.85 : 1) * dmgScale * dmgScale;
                double ff = (car.massNow > 0 ? car.massNow : car.cfg.mass) * aReq - 0.5 * 1.225 * car.cfg.cdA * v * v;
                brk = Clamp(ff / car.cfg.brakeForce - err * tune.kBrk, 0, 1);
            }
            if (brk > 0.02) thr = 0;
            // held back by a car in the lane (or a tight off-line lane): brake for it, not only on the profile's error
            if (held && v > vt + 0.3) { brk = Math.Max(brk, Clamp((v - vt) * 0.3, 0, 1)); thr = 0; }
            ErsButtons(v, (straight || (ersAttack && line.phase[idx] > 0)) && !noPass && !blueFlag && !capped, brakingZone);   // attacking: from the exit
            if (!formation && (car.otOn || car.boostOn || car.tow > 0.15 || (seen > 0 && (mode == 1 || prepPass) && !noPass && !blueFlag)) && !held && !brakingZone && v > 40 && Math.Abs(car.r * v) < 6)
            {
                // the extra power (or the slipstream's lower drag) takes the car over its own speed profile: stay flat
                // while the profile does not fall within the next 0.6 s (then the normal control sheds the extra speed
                // before the brake point). Pulling out of the tow to pass, the speed it carried is the pass: no lifting
                // back to the profile when the tow is gone (race craft only)
                bool clear = true; int nL = (int)Math.Round(v * 0.6 / track.ds); double vIp = V(ip);
                for (int k = 0; k <= nL && clear; k += 4) clear = V((ip + k) % N) >= vIp - 0.5;
                if (clear) { thr = 1; brk = 0; }
            }
            // on a straight, below target, with the line accelerating here and ahead: flat out (a feathered 90-97 %
            // throttle keeps the 2026 wings half shut and spends the battery at part throttle)
            if (!capped && !held && err > -1.5 && brk <= 0.02 && v > 40 && Math.Abs(car.r * v) < 6 && line.phase[idx] > 0)
            {
                bool flat = true; int nF = (int)Math.Round(v / track.ds);   // the next second: a straight being accelerated on
                for (int k = 0; k <= nF && flat; k += 4) { int j = (idx + k) % N; flat = line.phase[j] > 0 && v * v * Math.Abs(line.curv[j]) < 6; }
                if (flat) thr = 1;
            }
            // superclip zone: stay flat while the MGU-K slows the car (the profile already has the speed it loses)
            if (!capped && !held && deploy != null && deploy[idx] < 0 && err > -5 && Math.Abs(car.r * v) < 6) { thr = 1; brk = 0; }
            // trail-braking stability: braking or lifting deep in a long bend shifts load off the rear, and the hot,
            // fully loaded rear tyres step out (the car yaws faster than the line turns). Ease off the brake and hold
            // a whiff of throttle (cancels engine braking and the MGU-K's lift drag) until the rear grips again.
            if (v > 20 && err < 0)
            {
                // only in a bend that keeps turning the same way (in esses the yaw rate leads the line's curvature) and
                // only with the body sliding out (slip angle past what a gripping car shows; at turn-in the car yaws
                // ahead of the line under it, but does not slide)
                double kL = line.curv[idx], kA = line.curv[(idx + (int)Math.Round(0.5 * v / track.ds)) % N];
                double yawExcess = car.r * Math.Sign(kL) - Math.Abs(v * kL), beta = -Math.Atan2(car.vy, Math.Max(5, car.vx)) * Math.Sign(kL) * 180 / Math.PI;
                if (Math.Abs(kL) > 0.003 && kA * Math.Sign(kL) > 0.5 * Math.Abs(kL) && yawExcess > 0.05 && beta > 3.2)
                {
                    double ease = Clamp((beta - 3.2) / 1.3, 0, 1);
                    brk *= 1 - ease; thr = brk > 0.02 ? 0 : 0.12 * ease;
                }
            }

            // ---- slide catch: with the tail stepping out, steer into the slide and damp the yaw ----
            // body slip angle bet (+ = the car moves to the left of where it points); tail out = moving towards the
            // outside of the way it yaws. A driver points the front wheels along the way the car is going and takes
            // off the lock the excess yaw needs; pure pursuit alone keeps steering into the bend as the tail swings.
            if (v > 12)
            {
                double bet = Math.Atan2(car.vy, Math.Max(5, car.vx)), sr = Math.Sign(car.r);
                double tailOut = -bet * sr, yawEx = Math.Abs(car.r) - Math.Abs(v * line.curv[idx]);
                double catchK = Clamp((tailOut - 0.05) / 0.05, 0, 1);   // from 3 deg, full at 6 deg
                if (catchK > 0 && yawEx > 0)
                {
                    double dCatch = bet - 0.08 * sr * yawEx;           // along the velocity, less the excess yaw
                    double dNow = -steer * lockNow;
                    if (dCatch * sr < dNow * sr) steer = Clamp(-(dNow + (dCatch - dNow) * catchK) / lockNow, -1, 1);
                }
            }

            // ---- stuck, spun or far off the track: put the car back on the line ----
            var pt = track.pts[idx];
            double hd = Math.Atan2(Math.Sin(car.heading - Math.Atan2(pt.ty, pt.tx)), Math.Cos(car.heading - Math.Atan2(pt.ty, pt.tx)));
            stuckT = (v < 2.5 && simTime > 4 && !formation && !(rc != null && rc.InQueue(slot))) || Math.Abs(hd) > 1.6 ? stuckT + dt : 0; // queued behind the safety car (or in the formation queue) is not stuck
            offT = Math.Abs(lateral) > track.HalfWidth(idx) + 3.0 ? offT + dt : 0;
            recovered = false;
            if (stuckT > 2.0 || offT > 1.2)
            {
                double keep = Math.Min(V(idx) * 0.6, 30);
                // a damaged car is put back slower, and well inside the track (never against the barrier); a bent car
                // pulls to one side, so it gets room on the side it pulls to
                double offR = line.off[idx];
                if (car.cfg.damage && car.damage.Any)
                {
                    keep *= Math.Sqrt(Clamp(car.DamageGrip(keep), 0.3, 1));
                    double roomR = Math.Max(0, track.HalfWidth(idx) - 2.0);
                    offR = Clamp(offR - Math.Sign(car.SteerOffsetDeg) * 0.5, -roomR, roomR);
                }
                int keepLap = lap; Place(idx, offR, keep); lap = keepLap; progress = lap + (double)idx / N - pitLoss / RefLap; // a reset is not a new grid slot: the lap count stays
                int g = 1; while (g < car.cfg.gearTopSpeeds.Length && keep * 3.6 > car.cfg.gearTopSpeeds[g - 1] * 0.8) g++;
                car.gear = g; stuckT = offT = 0; offset = offsetTarget = 0; recovered = true; mode = 0; atkRef = null; heldT = 0; diveOn = false;
            }
            input = new CarInput(steer, thr, brk);
            return input;
        }

        // the stop itself: new set (the strategy's), wings, punctures. lapNow = the lap starting at the line of this stop.
        void Service(int lapNow)
        {
            boxing = false; pitStops++;
            car.RepairWings(); car.FixPunctures();
            car.wearF = car.wearR = 0; car.dirtF = car.dirtR = 0; car.tyreTF = car.tyreTR = car.cfg.tyreStart; car.tyreSF = car.tyreSR = double.NaN;
            if (strat != null) { tyreSet = strat.OnStop(lapNow, boxReason); TyreRules.Fit(car.cfg, tyreSet); setSerial++; }
            boxReason = 0;
        }

        // In the pit lane: call every physics step instead of car.Step. The car follows its PitRun (no time is added: the
        // lane costs what it costs); laps, progress and the line crossing go on from the lane's track distance.
        public void PitStep(double dt, double simTime)
        {
            var r = pit; if (r == null) return;
            int N = track.N, before = idx;
            r.Step(dt); r.ApplyTo(car);
            prevIdx = before < 0 ? r.idx : before; idx = r.idx; lateral = r.lateral;
            if (prevIdx > N * 0.8 && idx < N * 0.2)
            {
                if (!double.IsNaN(lapStart)) { lastLap = simTime - lapStart; if (double.IsNaN(bestLap) || lastLap < bestLap) bestLap = lastLap; }
                lapStart = simTime; lap++; car.NewLap();
                if (strat != null && raceLaps > 0 && !boxing && strat.BoxThisLap(lap, Math.Max(car.wearF, car.wearR))) { boxing = true; boxReason = 0; }
                lapPace = 1 + (rng.NextDouble() - 0.5) * 0.02 * (1 - who.consistency);
            }
            if (r.TakeService()) Service(pitLapAfter);
            progress = lap + (double)idx / N - pitLoss / RefLap;
            if (r.Done)
            {
                // back on the track at pit-lane speed: merge from the edge towards the racing line
                r.HandBack(car); pit = null;
                offset = offsetTarget = lateral - line.off[idx]; stuckT = offT = mistakeT = 0; wasBraking = false;
            }
        }

        // Overtake Mode: used when armed on a straight to attack the car ahead, or (now and then, the calmer drivers)
        // saved to defend against a car close behind. Boost: now and then on a straight with a car close ahead or behind
        // and charge to spare, decided once per straight.
        void ErsButtons(double v, bool racing, bool brakingZone)
        {
            var c = car.cfg;
            if (c.ersPower <= 0 || formation || (rc != null && rc.NoOvertaking)) { car.otOn = car.boostOn = false; otWasArmed = car.otArmed; wasStraight = false; return; }
            bool onStraight = racing && !brakingZone && v > 40 && line.phase != null && line.phase[idx] > 0;
            if (car.otArmed && !otWasArmed) otSave = ersRng.NextDouble() < 0.3 * (1 - who.aggression);
            otWasArmed = car.otArmed;
            // attack: with the race craft lining up or making a pass, or from the exit onto the straight that leads to the
            // place he will pass, within 1 s (ersAttack, AiRacecraft.cs): the lap's allowance is saved for that straight,
            // not spent following 100 m back or on a straight that ends in a fast bend. Overtake Mode only spends the
            // allowance where it gives more than the car's own map, so on from the exit costs nothing until it counts.
            bool attack = seen > 0 ? mode == 1 || prepPass || ersAttack : aheadGap < 30 + 0.8 * v, defend = behindGap < 25;
            car.otOn = car.otArmed && onStraight && car.OtLeft > 0 && (otSave ? defend : attack || defend);
            double soc = double.IsNaN(car.ersEnergy) ? 1 : car.ersEnergy / c.ersCapacity;
            if (onStraight && !wasStraight)
                boostWant = ((aheadGap < 30 && seen == 0) || behindGap < 20) && soc > 0.4 && ersRng.NextDouble() < 0.25 + 0.4 * who.aggression;   // attacking: ersPush
            wasStraight = onStraight;
            if (!onStraight || soc < 0.25 || (aheadGap > 60 && behindGap > 40)) boostWant = false;
            // the push for the pass: the last seconds of the attack straight, close behind or alongside (where the car
            // ahead clips or its map ramps down), with charge to spare
            car.boostOn = boostWant || (ersPush && onStraight && soc > 0.35);
        }

        public CarInput LastInput => input;
        double RefLap => line.lapTime > 0 ? line.lapTime : 90;
        static double Clamp(double v, double a, double b) => v < a ? a : v > b ? b : v;
    }

    // Car-to-car contact: each car is two circles (front and rear). Touching cars are pushed apart and
    // exchange speed along the line of contact, like a firm nudge.
    public static class Traffic
    {
        // Barriers: distance from the centreline to the inner face of the wall (street circuit) or guardrail.
        // Matches where Trackside.cs draws them.
        public static double WallOffset(Track t) => t.width / 2 + (t.def != null ? t.def.barrier : 1.4 + (t.width < 10 ? 1.25 : 7.9));

        // Hitting the barrier: the speed into the wall is lost (a little bounces back), scraping along it
        // costs speed through friction, and a hit with the nose swings the car round.
        public static bool Wall(FormulaCar c, TrackPoint p, double lateral, double wall)
        {
            const double Half = 0.95, Bounce = 0.2, Scrape = 0.45;
            double over = Math.Abs(lateral) - (wall - Half);
            if (over <= 0) return false;
            double sgn = lateral > 0 ? 1 : -1, nx = p.nx * sgn, ny = p.ny * sgn; // points out of the track
            c.x -= nx * over; c.y -= ny * over;
            double ch = Math.Cos(c.heading), sh = Math.Sin(c.heading);
            double wx = c.vx * ch - c.vy * sh, wy = c.vx * sh + c.vy * ch;
            double vn = wx * nx + wy * ny;
            if (vn <= 0) return true;
            double tx = -ny, ty = nx, vt = wx * tx + wy * ty;
            double J = (1 + Bounce) * vn;                         // impulse per unit mass
            // damage: the closing speed into the wall, landing on the side that faces it (the front corner when the
            // car is moving forwards into it, the rear corner when going backwards or hit tail first)
            {
                double ly = -sh * nx + ch * ny, lxn = ch * nx + sh * ny;
                double lx = c.vx >= 0 && lxn > -0.3 ? c.cfg.wheelbase * (1 - c.cfg.frontWeight) * (lxn > 0.7 ? 1.3 : 0.6) : -c.cfg.wheelbase * c.cfg.frontWeight * (lxn < -0.7 ? 1.3 : 0.6);
                c.ApplyImpact(vn, lx, ly > 0 ? 0.9 : -0.9, true);
            }
            double vt2 = vt - Math.Sign(vt) * Math.Min(Math.Abs(vt), Scrape * J);
            double vn2 = -Bounce * vn;
            wx = nx * vn2 + tx * vt2; wy = ny * vn2 + ty * vt2;
            c.vx = wx * ch + wy * sh; c.vy = -wx * sh + wy * ch;
            // the nose (or tail, in reverse) is what touches: the push acts ahead of the centre of gravity
            double arm = c.cfg.wheelbase * (1 - c.cfg.frontWeight) * (c.vx >= 0 ? 1 : -1);
            double cross = ch * -ny - sh * -nx;
            double dr = 0.35 * arm * J * c.cfg.mass / c.cfg.inertiaZ * cross;
            c.r = c.r * 0.6 + (dr < -3 ? -3 : dr > 3 ? 3 : dr);
            return true;
        }

        const double R = 0.95, FrontC = 1.55, RearC = -1.25;
        public static bool Collide(FormulaCar a, FormulaCar b)
        {
            double dx0 = b.x - a.x, dy0 = b.y - a.y;
            if (dx0 * dx0 + dy0 * dy0 > 49) return false;
            bool hit = false;
            double ca = Math.Cos(a.heading), sa = Math.Sin(a.heading), cb = Math.Cos(b.heading), sb = Math.Sin(b.heading);
            for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++)
            {
                double oa = i == 0 ? FrontC : RearC, ob = j == 0 ? FrontC : RearC;
                double ax = a.x + ca * oa, ay = a.y + sa * oa, bx = b.x + cb * ob, by = b.y + sb * ob;
                double dx = bx - ax, dy = by - ay, d = Math.Sqrt(dx * dx + dy * dy);
                if (d >= 2 * R || d < 1e-6) continue;
                hit = true;
                double nx = dx / d, ny = dy / d, pen = 2 * R - d;
                a.x -= nx * pen * 0.5; a.y -= ny * pen * 0.5; b.x += nx * pen * 0.5; b.y += ny * pen * 0.5;
                // world velocities
                double avx = a.vx * ca - a.vy * sa, avy = a.vx * sa + a.vy * ca, bvx = b.vx * cb - b.vy * sb, bvy = b.vx * sb + b.vy * cb;
                double rel = (bvx - avx) * nx + (bvy - avy) * ny;
                if (rel < 0)
                {
                    double jimp = -(1 + 0.15) * rel / 2; // equal masses
                    // damage: each car at its contact point (car frame), from the closing speed
                    double cxw = nx * R, cyw = ny * R;   // from a's circle centre towards b
                    a.ApplyImpact(-rel, oa + ca * cxw + sa * cyw, -sa * cxw + ca * cyw);
                    b.ApplyImpact(-rel, ob - (cb * cxw + sb * cyw), -(-sb * cxw + cb * cyw));
                    avx -= jimp * nx; avy -= jimp * ny; bvx += jimp * nx; bvy += jimp * ny;
                    a.vx = avx * ca + avy * sa; a.vy = -avx * sa + avy * ca; b.vx = bvx * cb + bvy * sb; b.vy = -bvx * sb + bvy * cb;
                    // a nudge off-centre also turns the cars a little
                    a.r -= jimp * (oa * (-sa * nx + ca * ny)) * a.cfg.mass / a.cfg.inertiaZ * 0.12;
                    b.r += jimp * (ob * (-sb * nx + cb * ny)) * b.cfg.mass / b.cfg.inertiaZ * 0.12;
                }
            }
            return hit;
        }
    }
    // 2026 Overtake Mode detection (replaces DRS). Detection points sit Before m ahead of the start of the longest
    // flat-out runs of the racing line (its accelerate phase; at most two a lap, a quarter of a lap apart at least).
    // A car that crosses a point within Window s of the last car to cross it (any car, lapped ones too, as with DRS)
    // is armed until the next point; the new lap's energy allowance is given at the first point after the line.
    // The game calls Update for every car about 60 times a second; the headless tools never do, so nothing is armed there.
    public sealed class OvertakeRule
    {
        public const double Window = 1.0, Before = 150;
        public readonly int[] points;
        public readonly Track track;
        readonly double[] lastT;

        public OvertakeRule(Track t, RacingLine line, int maxPoints = 2)
        {
            track = t;
            int N = t.N; var ph = line != null && line.phase != null && line.phase.Length == N ? line.phase : null;
            var found = new List<int>();
            if (ph != null)
            {
                // runs of accelerate phase, short gaps (< 40 m) bridged; start the scan where the line is not accelerating
                int s0 = -1; for (int i = 0; i < N && s0 < 0; i++) if (ph[i] <= 0) s0 = i;
                var runs = new List<(int start, int len)>();
                if (s0 >= 0)
                {
                    int bridge = (int)Math.Round(40 / t.ds), start = -1, end = -1;
                    for (int k = 1; k <= N; k++)
                    {
                        int i = (s0 + k) % N;
                        if (ph[i] > 0)
                        {
                            if (start < 0) start = k;
                            else if (k - end > bridge) { runs.Add((start, end - start + 1)); start = k; }
                            end = k;
                        }
                    }
                    if (start >= 0) runs.Add((start, end - start + 1));
                    runs.Sort((a, b) => b.len.CompareTo(a.len));
                    for (int r = 0; r < runs.Count && found.Count < maxPoints; r++)
                    {
                        if (r > 0 && runs[r].len < 0.45 * runs[0].len) break;
                        int p = (((s0 + runs[r].start - (int)Math.Round(Before / t.ds)) % N) + N) % N;
                        bool apart = true;
                        foreach (int q in found) { int d = Math.Abs(q - p); if (Math.Min(d, N - d) < N / 4) apart = false; }
                        if (apart) found.Add(p);
                    }
                }
            }
            if (found.Count == 0) found.Add(0);   // the line itself
            found.Sort();
            points = found.ToArray(); lastT = new double[points.Length];
            Reset();
        }

        public void Reset() { for (int i = 0; i < lastT.Length; i++) lastT[i] = double.NaN; }

        // the car is at track point idx at time t. allowed = false: never armed now (lap 1, safety car / VSC, pit lane).
        public void Update(FormulaCar car, int idx, double t, bool allowed)
        {
            int N = track.N, prev = car.otIdx; car.otIdx = idx;
            if (idx < 0) { car.otArmed = false; return; }
            if (prev < 0) return;
            int step = ((idx - prev) % N + N) % N;
            if (step == 0 || step > N / 2) return;    // standing still or going backwards
            for (int k = 0; k < points.Length; k++)
            {
                int d = ((points[k] - prev) % N + N) % N;
                if (d == 0 || d > step) continue;
                // crossed detection point k
                double gap = t - lastT[k];
                lastT[k] = t;
                if (car.otRefill || double.IsNaN(car.otEnergy)) car.OtRefill();
                car.otArmed = allowed && gap <= Window;   // NaN (nobody crossed yet) is not within the window
            }
            if (!allowed) car.otArmed = false;
        }
    }

    // Level of detail for a rival's physics (CPU). A rival far from the player (and away from the camera) is
    // stepped less often with a longer step, as GT / F1 games do for cars you cannot see: 120 Hz beyond Gap2 m along
    // the track, 60 Hz beyond Gap4 m when no other car is within Alone m of it. Time stays exact: the car is `lag`
    // 240 Hz ticks behind the race clock and the next step covers all of them, so it never gains or loses time; when it
    // comes back (closer, in view, near another car) it catches up in one step and runs at 240 Hz again.
    // The player's car is never stepped here. Checked with Tools/PhysicsCheck "race" (LOD=1) and "perf".
    public sealed class AiLod
    {
        public static double Gap2 = 150, Gap4 = 400, Alone = 60;   // m along the track
        public int lag;                                            // 240 Hz ticks behind the race clock
        public int rate = 1;                                       // ticks per step now (1, 2 or 4)
        public object owner;                                       // the driver this belongs to (the game resets on a new one)

        // rate a rival should run at: gap = metres along the track to the player (0 = keep 240 Hz),
        // nearest = metres along the track to the nearest other car
        public static int Want(double gap, double nearest) => gap > Gap4 && nearest > Alone ? 4 : gap > Gap2 ? 2 : 1;

        // one 240 Hz tick of the race clock; true = handled here (stepped with the longer step, or skipped)
        public bool Tick(FormulaCar car, CarInput input, double h, int want)
        {
            rate = want;
            if (want <= 1 && lag == 0) return false;               // the caller steps it at 240 Hz
            lag++;
            if (lag >= want) { car.Step(input, lag * h); lag = 0; }
            return true;
        }
        // the rate for rival `me` (60 Hz is enough to call it): gap to the player's track point and to the nearest other
        // car, both along the track (a car closer than Alone to another one stays at 120 Hz at most, so contacts
        // are resolved on fresh positions). playerIdx < 0 or me.idx < 0: 240 Hz.
        public static int WantFor(Track t, int playerIdx, AiDriver me, IList<AiDriver> cars)
        {
            if (playerIdx < 0 || me.idx < 0) return 1;
            double gap = Gap(t, playerIdx, me.idx);
            if (gap <= Gap2) return 1;
            double nearest = gap;
            if (gap > Gap4)
                for (int j = 0; j < cars.Count && nearest > Alone; j++)
                {
                    var o = cars[j]; if (o == me || o.idx < 0) continue;
                    double g = Gap(t, o.idx, me.idx); if (g < nearest) nearest = g;
                }
            return Want(gap, nearest);
        }
        public static int WantFor(Track t, int playerIdx, int k, IList<AiDriver> cars) => WantFor(t, playerIdx, cars[k], cars);
        static double Gap(Track t, int a, int b) { int g = Math.Abs(a - b); g = Math.Min(g, t.N - g); return g * t.ds; }

        // overload for the bench: gap only
        public bool Tick(FormulaCar car, CarInput input, double h, double gap, double nearest = 1e9) => Tick(car, input, h, Want(gap, nearest));

        public double Behind(double h) => lag * h;                 // s: the car's state is this old (drawn this far ahead)
        public void Reset() { lag = 0; rate = 1; }
    }
}
