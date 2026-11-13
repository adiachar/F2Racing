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
            new[] { 4.823, 0.082, 0.199, 0.544, 0.146, 0.989, 0.800 },
            new[] { 9.612, 0.114, 0.171, 0.867, 0.242, 0.935, 0.907 },
            new[] { 6.177, 0.160, 0.122, 0.683, 0.101, 0.905, 0.774 },
            new[] { 5.175, 0.142, 0.113, 0.501, 0.313, 0.970, 0.784 },
            new[] { 5.142, 0.166, 0.149, 0.670, 0.125, 0.918, 0.813 },
            new[] { 4.741, 0.132, 0.106, 0.669, 0.369, 0.913, 0.787 },
            new[] { 5.478, 0.105, 0.131, 0.757, 0.263, 0.957, 0.719 },
            new[] { 5.294, 0.128, 0.195, 0.618, 0.103, 0.911, 0.912 },
            new[] { 3.399, 0.224, 0.173, 0.565, 0.127, 0.918, 0.788 },
            new[] { 4.640, 0.061, 0.163, 0.809, 0.206, 0.942, 0.818 },
            new[] { 3.610, 0.141, 0.095, 0.820, 0.237, 0.913, 0.769 },
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
            D("C. Sainz-Rey","Blue Works",    0.85, 0.92, 0.60, 0.05f, 0.30f, 0.75f),
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
        public double target;             // the speed target of the last Think (m/s), for diagnostics

        readonly Track track; readonly RacingLine line; readonly AiTuning tune;
        readonly double[] speed;          // this driver's own target speeds
        readonly double[] speedFull;      // the same on a full tank (null when the car starts in qualifying trim)
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
            if (c.fuel && c.fuelStart != c.fuelRef)
            {
                // a race start with more fuel than the reference trim: one profile for the full tank, one for empty,
                // and Think blends them by the fuel left (the heavy car brakes earlier and corners a little slower)
                speedFull = (double[])line.SpeedProfile(c.WithFuel(c.fuelStart), grip, brake).Clone();
                speed = (double[])line.SpeedProfile(c.WithFuel(0), grip, brake).Clone();
            }
            else speed = (double[])line.SpeedProfile(c, grip, brake).Clone();
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
                    double left = track.HalfWidth(oIdx) - oLat, right = track.HalfWidth(oIdx) + oLat; // pull out to the side with more room
                    want += (left > right ? 1 : -1) * 2.4;
                }
            }
            if (others != null) for (int i = 0; i < others.Count; i++) { var o = others[i]; if (o != this && o.idx >= 0) See(o.idx, o.lateral, Math.Sqrt(o.car.vx * o.car.vx + o.car.vy * o.car.vy)); }
            if (player != null && playerIdx >= 0) See(playerIdx, playerLateral, Math.Sqrt(player.vx * player.vx + player.vy * player.vy));
            if (blocked || want != 0) offsetTarget = Clamp(offsetTarget + want * dt * 2.5, -3.2, 3.2);
            else if (!alongside) offsetTarget *= Math.Max(0, 1 - dt * 0.8);
            // The aim point is clamped to the track (room, below), and the racing line already runs within 0.3 m of
            // that clamp at most apexes. A sideways offset (traffic) carried into a bend therefore pins the aim point
            // to the track edge, whose bends are much tighter than the line's: at racing-line speed the car snaps
            // round or runs wide. So the offset may only use the room the line leaves over the next 1.5 s (40 m at
            // least) and is wound back in time before the bend, keeping the path a parallel copy of the line.
            {
                double roomA = track.width / 2 - 1.0, maxL = 3.2, maxR = 3.2;
                int nA = (int)Math.Round(Math.Max(40, v * 1.5) / track.ds);
                for (int k = 0; k <= nA; k += 2) { int j = (idx + k) % N; if (track.halfW != null) roomA = track.halfW[j] - 1.0; maxL = Math.Min(maxL, roomA - line.off[j]); maxR = Math.Min(maxR, roomA + line.off[j]); }
                offsetTarget = Clamp(offsetTarget, -Math.Max(0, maxR), Math.Max(0, maxL));
            }
            offset += Clamp(offsetTarget - offset, -2.2 * dt, 2.2 * dt);

            // ---- steering: aim at a point on the line ahead (pure pursuit) ----
            double Ld = tune.look0 + tune.lookV * v;
            int ia = (idx + Math.Max(1, (int)Math.Round(Ld / track.ds))) % N;
            double room = track.HalfWidth(ia) - 1.0;
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
            double V(int i) => speedFull == null ? speed[i] : speed[i] + (speedFull[i] - speed[i]) * (double.IsNaN(car.fuel) ? 1 : Clamp(car.fuel / car.cfg.fuelStart, 0, 1));
            bool brakingZone = V((ip + 1) % N) < V(ip) - 0.05;
            if (brakingZone && !wasBraking && rng.NextDouble() < 0.10 * (1 - who.consistency)) mistakeT = 0.9; // brakes too late now and then
            wasBraking = brakingZone;
            if (mistakeT > 0) mistakeT -= dt;
            double vt = Math.Min(V(ip), V(idx) + 3) * lapPace * (mistakeT > 0 ? 1.07 : 1);
            if (Math.Abs(offset) > 0.8) vt *= 0.985;      // off the ideal line: a little slower
            // drive to the grip the tyres have now (cold, overheated or worn tyres). Without downforce corner speed goes
            // with grip^(1/2); with it the load itself grows with speed, so corner speed reacts more strongly:
            // m v^2 k = mu (W + D v^2)^e gives v ~ mu^(1 / (2 - 2 e s)), s = the downforce share of the load: about
            // mu^0.6 at 100 km/h, mu^0.8 at 200, mu^1.1 at 300 (with a plain sqrt the AI overdrove fast bends on cold tyres).
            {
                double down = 0.5 * 1.225 * car.cfg.clA * vt * vt, share = down / (down + car.cfg.mass * 9.81);
                vt *= Math.Pow(Clamp(Math.Min(car.gripF, car.gripR), 0.8, 1.0), 1 / (2 - 2 * car.cfg.loadExp * share));
            }
            vt = Math.Min(vt, speedCap);
            target = vt;
            double err = vt - v;
            double thr = Clamp(err * tune.kThr, 0, 1), brk = Clamp(-err * tune.kBrk, 0, 1);
            if (brk > 0.02) thr = 0;
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

            // ---- stuck, spun or far off the track: put the car back on the line ----
            var pt = track.pts[idx];
            double hd = Math.Atan2(Math.Sin(car.heading - Math.Atan2(pt.ty, pt.tx)), Math.Cos(car.heading - Math.Atan2(pt.ty, pt.tx)));
            stuckT = (v < 2.5 && simTime > 4) || Math.Abs(hd) > 1.6 ? stuckT + dt : 0;
            offT = Math.Abs(lateral) > track.HalfWidth(idx) + 3.0 ? offT + dt : 0;
            recovered = false;
            if (stuckT > 2.0 || offT > 1.2)
            {
                double keep = Math.Min(V(idx) * 0.6, 30);
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
