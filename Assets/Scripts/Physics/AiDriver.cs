// AiDriver.cs — computer-controlled drivers. Pure C# (no UnityEngine), so the same code runs in the game
// and in the headless trainer (Tools/PhysicsCheck, "train"), which drives thousands of laps per minute.
//
// How a driver works:
//  * every rival drives the SAME physics as the player (FormulaCar), with pedals and a steering wheel;
//  * it follows the racing line (pure-pursuit steering) and the speed profile (when to brake, how fast in corners);
//  * skill = how much of the tyre grip and braking the driver dares to use, how steady he is lap after lap,
//    and how often he brakes too late. That is what separates real drivers too.
//  * the control gains below were found by the trainer (an evolution search over full simulated laps).
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
            new[] { 4.542, 0.060, 0.135, 0.495, 0.102, 0.908, 0.847 },
            new[] { 5.902, 0.101, 0.175, 0.789, 0.560, 0.936, 0.956 },
            new[] { 5.168, 0.158, 0.185, 0.687, 0.701, 0.930, 0.741 },
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
            D("K. Arden",    "Silver Arrow",  1.00, 0.95, 0.70, 0.75f, 0.78f, 0.80f),
            D("M. Voss",     "Night Bull",    0.99, 0.97, 0.95, 0.10f, 0.13f, 0.35f),
            D("L. Hartwell", "Rosso",         0.96, 0.96, 0.75, 0.75f, 0.05f, 0.07f),
            D("N. Lindqvist","Papaya",        0.95, 0.93, 0.60, 1.00f, 0.50f, 0.05f),
            D("G. Rowe",     "Silver Arrow",  0.93, 0.94, 0.65, 0.75f, 0.78f, 0.80f),
            D("O. Pascoe",   "Papaya",        0.92, 0.90, 0.55, 1.00f, 0.50f, 0.05f),
            D("I. Haddad",   "Night Bull",    0.88, 0.82, 0.80, 0.10f, 0.13f, 0.35f),
            D("C. Sainz-Rey","Blue Works",    0.85, 0.92, 0.60, 0.05f, 0.30f, 0.75f),
            D("A. Albrecht", "Blue Works",    0.83, 0.90, 0.50, 0.05f, 0.30f, 0.75f),
            D("F. Alvaro",   "Emerald",       0.82, 0.96, 0.85, 0.02f, 0.40f, 0.30f),
            D("P. Garnier",  "Alpine Blue",   0.78, 0.88, 0.65, 0.20f, 0.50f, 0.95f),
            D("N. Holz",     "Four Rings",    0.75, 0.93, 0.45, 0.55f, 0.57f, 0.60f),
            D("E. Okafor",   "Stars",         0.72, 0.85, 0.70, 0.90f, 0.90f, 0.92f),
            D("Y. Tanaka",   "Junior Bull",   0.68, 0.80, 0.75, 0.25f, 0.35f, 0.85f),
            D("O. Bergman",  "Stars",         0.65, 0.78, 0.80, 0.90f, 0.90f, 0.92f),
            D("G. Bastos",   "Four Rings",    0.60, 0.80, 0.55, 0.55f, 0.57f, 0.60f),
            D("F. Colombo",  "Alpine Blue",   0.52, 0.70, 0.75, 0.20f, 0.50f, 0.95f),
            D("A. Lindahl",  "Junior Bull",   0.48, 0.72, 0.60, 0.25f, 0.35f, 0.85f),
            D("S. Paredes",  "Liberty",       0.40, 0.84, 0.50, 0.85f, 0.85f, 0.20f),
            D("V. Bergstrom","Liberty",       0.33, 0.86, 0.35, 0.85f, 0.85f, 0.20f),
            D("L. Strand",   "Emerald",       0.15, 0.62, 0.55, 0.02f, 0.40f, 0.30f),
        };
    }

    public class AiDriver
    {
        public readonly FormulaCar car;
        public readonly DriverProfile who;
        public int idx = -1;              // nearest track point
        public double lateral;            // m from the centreline (+ = left)
        public int lap; public double lapStart = double.NaN, lastLap = double.NaN, bestLap = double.NaN;
        public double progress;           // laps + fraction, for race positions
        public double offset;             // current sideways offset from the racing line (traffic)
        public bool recovered;

        readonly Track track; readonly RacingLine line; readonly AiTuning tune;
        readonly double[] speed;          // this driver's own target speeds
        readonly Random rng;
        double offsetTarget, lapPace = 1, mistakeT, stuckT, offT, speedCap = double.MaxValue;
        bool wasBraking; int prevIdx = -1;
        CarInput input;

        // difficulty scales every rival (1 = real pace)
        public AiDriver(Track track, RacingLine line, CarConfig cfg, DriverProfile who, AiTuning tune, double difficulty = 1, int seed = 1)
        {
            this.track = track; this.line = line; this.who = who; this.tune = tune;
            rng = new Random(seed * 7919 + 13);
            var c = cfg.Clone(); c.tc = TcMode.High; c.abs = true; c.autoShift = true; c.steerSpeed = 14; c.steerReturn = 16;
            car = new FormulaCar(c);
            double grip = tune.grip * difficulty * (0.86 + 0.14 * who.pace), brake = tune.brakeUse * difficulty * (0.84 + 0.16 * who.pace);
            // SpeedProfile stores its result in the line object; keep a private copy and leave the line as it was
            var keepS = line.speed; var keepB = line.braking; var keepP = line.phase; var keepC = line.vCorner; double keepT = line.lapTime;
            speed = (double[])line.SpeedProfile(c, grip, brake).Clone();
            line.speed = keepS; line.braking = keepB; line.phase = keepP; line.vCorner = keepC; line.lapTime = keepT;
        }

        public void Place(int trackIdx, double lateralOffset, double v = 0)
        {
            int N = track.N; trackIdx = ((trackIdx % N) + N) % N;
            var p = track.pts[trackIdx];
            car.x = p.x + p.nx * lateralOffset; car.y = p.y + p.ny * lateralOffset; car.heading = Math.Atan2(p.ty, p.tx);
            car.vx = v; car.vy = 0; car.r = 0; car.gear = 1; car.wR = double.NaN;
            idx = prevIdx = trackIdx; offset = offsetTarget = lateralOffset - line.off[trackIdx];
        }

        // Decide pedals and steering. Call about 60 times a second; then step the car with Input.
        public CarInput Think(double dt, double simTime, IList<AiDriver> others, FormulaCar player, int playerIdx, double playerLateral)
        {
            int N = track.N;
            var near = track.Nearest(car.x, car.y, idx);
            prevIdx = idx < 0 ? near.idx : idx; idx = near.idx; lateral = near.lateral;
            double v = Math.Sqrt(car.vx * car.vx + car.vy * car.vy);

            // laps
            if (prevIdx > N * 0.8 && idx < N * 0.2)
            {
                if (!double.IsNaN(lapStart)) { lastLap = simTime - lapStart; if (double.IsNaN(bestLap) || lastLap < bestLap) bestLap = lastLap; }
                lapStart = simTime; lap++; car.NewLap();
                lapPace = 1 + (rng.NextDouble() - 0.5) * 0.02 * (1 - who.consistency); // good laps and bad laps
            }
            else if (prevIdx < N * 0.2 && idx > N * 0.8) lap--;
            progress = lap + (double)idx / N;

            // ---- traffic: look for a car close ahead or alongside ----
            speedCap = double.MaxValue; bool alongside = false; double want = 0; bool blocked = false;
            // overtaking needs a straight: no pulling out where the line is curving
            bool straight = Math.Abs(line.curv[idx]) < 0.004 && Math.Abs(line.curv[(idx + 15) % N]) < 0.006;
            void See(int oIdx, double oLat, double oV)
            {
                int d = oIdx - idx; if (d > N / 2) d -= N; else if (d < -N / 2) d += N;
                double gap = d * track.ds, side = oLat - lateral;
                if (gap > -5.5 && gap < 5.5 && Math.Abs(side) < 3.2) { alongside = true; if (Math.Abs(side) < 2.3) want += side > 0 ? -0.8 : 0.8; }
                if (gap <= 1.5 || Math.Abs(side) > 2.2) return;
                double closing = v - oV;
                // keep a gap that grows with speed; the bolder the driver, the closer he sits
                double safe = 6.5 + (0.16 - 0.07 * who.aggression) * v + Math.Max(0, closing) * 0.6;
                if (gap < safe) speedCap = Math.Min(speedCap, oV - (safe - gap) * 0.6);
                if (straight && gap < safe + 12 && closing > -0.5)
                {
                    blocked = true;
                    double left = track.width / 2 - oLat, right = track.width / 2 + oLat; // pull out to the side with more room
                    want += (left > right ? 1 : -1) * 2.4;
                }
            }
            if (others != null) for (int i = 0; i < others.Count; i++) { var o = others[i]; if (o != this && o.idx >= 0) See(o.idx, o.lateral, Math.Sqrt(o.car.vx * o.car.vx + o.car.vy * o.car.vy)); }
            if (player != null && playerIdx >= 0) See(playerIdx, playerLateral, Math.Sqrt(player.vx * player.vx + player.vy * player.vy));
            if (blocked || want != 0) offsetTarget = Clamp(offsetTarget + want * dt * 2.5, -3.2, 3.2);
            else if (!alongside) offsetTarget *= Math.Max(0, 1 - dt * 0.8);
            offset += Clamp(offsetTarget - offset, -2.2 * dt, 2.2 * dt);

            // ---- steering: aim at a point on the line ahead (pure pursuit) ----
            double Ld = tune.look0 + tune.lookV * v;
            int ia = (idx + Math.Max(1, (int)Math.Round(Ld / track.ds))) % N;
            double room = track.width / 2 - 1.0;
            double offA = Clamp(line.off[ia] + offset, -room, room);
            var pa = track.pts[ia];
            double tx = pa.x + pa.nx * offA - car.x, ty = pa.y + pa.ny * offA - car.y;
            double ch = Math.Cos(car.heading), sh = Math.Sin(car.heading);
            double fx = ch * tx + sh * ty, fy = -sh * tx + ch * ty; // target in the car frame (fy + = left)
            double d2 = Math.Max(1.0, fx * fx + fy * fy);
            double delta = Math.Atan(car.cfg.wheelbase * 2 * fy / d2);
            double lockNow = car.lockAngle > 1e-4 ? car.lockAngle : car.cfg.maxLockDeg * Math.PI / 180;
            double steer = Clamp(-delta / lockNow, -1, 1);

            // ---- speed: follow this driver's own speed profile ----
            int ip = (idx + (int)Math.Round(tune.preview * v / track.ds)) % N;
            bool brakingZone = speed[(ip + 1) % N] < speed[ip] - 0.05;
            if (brakingZone && !wasBraking && rng.NextDouble() < 0.10 * (1 - who.consistency)) mistakeT = 0.9; // brakes too late now and then
            wasBraking = brakingZone;
            if (mistakeT > 0) mistakeT -= dt;
            double vt = Math.Min(speed[ip], speed[idx] + 3) * lapPace * (mistakeT > 0 ? 1.07 : 1);
            if (Math.Abs(offset) > 0.8) vt *= 0.985;      // off the ideal line: a little slower
            vt = Math.Min(vt, speedCap);
            double err = vt - v;
            double thr = Clamp(err * tune.kThr, 0, 1), brk = Clamp(-err * tune.kBrk, 0, 1);
            if (brk > 0.02) thr = 0;

            // ---- stuck, spun or far off the track: put the car back on the line ----
            var pt = track.pts[idx];
            double hd = Math.Atan2(Math.Sin(car.heading - Math.Atan2(pt.ty, pt.tx)), Math.Cos(car.heading - Math.Atan2(pt.ty, pt.tx)));
            stuckT = (v < 2.5 && simTime > 4) || Math.Abs(hd) > 1.6 ? stuckT + dt : 0;
            offT = Math.Abs(lateral) > track.width / 2 + 3.0 ? offT + dt : 0;
            recovered = false;
            if (stuckT > 2.0 || offT > 1.2)
            {
                double keep = Math.Min(speed[idx] * 0.6, 30);
                Place(idx, line.off[idx], keep);
                int g = 1; while (g < car.cfg.gearTopSpeeds.Length && keep * 3.6 > car.cfg.gearTopSpeeds[g - 1] * 0.8) g++;
                car.gear = g; stuckT = offT = 0; offset = offsetTarget = 0; recovered = true;
            }
            input = new CarInput(steer, thr, brk);
            return input;
        }

        public CarInput LastInput => input;
        static double Clamp(double v, double a, double b) => v < a ? a : v > b ? b : v;
    }

    // Car-to-car contact: each car is two circles (front and rear). Touching cars are pushed apart and
    // exchange speed along the line of contact, like a firm nudge.
    public static class Traffic
    {
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
}
