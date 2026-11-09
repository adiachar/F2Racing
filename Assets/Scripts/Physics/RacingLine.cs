// RacingLine.cs — optimal-ish racing line + target speed profile, port of the web racingline.js.
// 1) Line: inside the track edges, minimise curvature (wide entry, clip the apex, wide exit).
// 2) Speeds: from the car's own grip, downforce, braking and engine, so the line's
//    colours are honest — if it says green, the car really can go faster there.
// Pure C# (System.Math only), same frame as Track: x/y plane, offset + = left of the centreline.
using System;

namespace FormulaSim
{
    public class RacingLine
    {
        const double G = 9.81, RHO = 1.225;

        public int N;
        public double[] off;     // lateral offset from the centreline (m, + = left)
        public double[] x, y;    // line position
        public double[] curv;    // smoothed Menger curvature of the line (1/m, + = left)
        public double[] ds;      // length of the segment i -> i+1 (m)
        public double[] vCorner; // pure cornering limit (m/s), before acceleration / braking limits
        public double[] speed;   // target speed (m/s)
        public bool[] braking;   // the target speed falls towards the next point (brake here)
        public sbyte[] phase;    // +1 accelerate (green), 0 at the cornering limit / coast (yellow), -1 brake (red)
        public double lapTime;   // s, integrated over the speed profile

        public static RacingLine Compute(Track t, CarConfig cfg, double margin = 1.3, double safety = 0.82)
        {
            var rl = Build(t, margin);
            rl.SpeedProfile(cfg, safety);
            return rl;
        }

        // ---------------------------------------------------------------- line (buildRacingLine)
        public static RacingLine Build(Track track, double margin = 1.3)
        {
            var pts = track.pts; int N = track.N;
            double lim = track.width / 2 - margin;
            var off = new double[N];
            int w(int i) => ((i % N) + N) % N;
            double px(int i) { int k = w(i); return pts[k].x + pts[k].nx * off[k]; }
            double py(int i) { int k = w(i); return pts[k].y + pts[k].ny * off[k]; }
            // Minimise curvature (squared second difference) with Gauss-Seidel + clamping to the track.
            // Solve coarse-to-fine so the whole-corner shape converges quickly.
            foreach (int s in new[] { 32, 16, 8, 4, 2, 1 })
            {
                int iters = s >= 8 ? 1500 : 600;
                for (int it = 0; it < iters; it++)
                {
                    for (int i = 0; i < N; i += s)
                    {
                        double tx = (-px(i - 2 * s) + 4 * px(i - s) + 4 * px(i + s) - px(i + 2 * s)) / 6;
                        double ty = (-py(i - 2 * s) + 4 * py(i - s) + 4 * py(i + s) - py(i + 2 * s)) / 6;
                        double o = (tx - pts[i].x) * pts[i].nx + (ty - pts[i].y) * pts[i].ny;
                        off[i] += (Math.Max(-lim, Math.Min(lim, o)) - off[i]) * 0.7;
                    }
                }
                if (s > 1) for (int i = 0; i < N; i += s) // interpolate the points in between for the next level
                {
                    int j = w(i + s);
                    for (int k = 1; k < s && i + k < N; k++) off[i + k] = off[i] + (off[j] - off[i]) * ((double)k / s);
                }
            }
            var rl = new RacingLine
            {
                N = N, off = off, x = new double[N], y = new double[N], curv = new double[N], ds = new double[N],
            };
            for (int i = 0; i < N; i++) { rl.x[i] = px(i); rl.y[i] = py(i); }
            // curvature + segment lengths along the line
            var raw = new double[N];
            for (int i = 0; i < N; i++)
            {
                int p = w(i - 1), r = w(i + 1);
                double qx = rl.x[i], qy = rl.y[i], ppx = rl.x[p], ppy = rl.y[p], rx = rl.x[r], ry = rl.y[r];
                double a = Hypot(qx - ppx, qy - ppy), b = Hypot(rx - qx, ry - qy), c = Hypot(rx - ppx, ry - ppy);
                double cross = (qx - ppx) * (ry - qy) - (qy - ppy) * (rx - qx);
                raw[i] = (2 * cross) / Math.Max(1e-6, a * b * c); // Menger curvature, + = left
                rl.ds[i] = b;
            }
            // light smoothing of curvature (removes sampling noise)
            for (int i = 0; i < N; i++) { double s = 0; for (int j = -2; j <= 2; j++) s += raw[w(i + j)]; rl.curv[i] = s / 5; }
            return rl;
        }

        static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

        // ---------------------------------------------------------------- speeds (speedProfile)
        // Target speed (m/s) at every point of the line for this car setup.
        // Uses the same tyre law as the physics (max force ~ load^0.8 per axle, more load from downforce).
        public double[] SpeedProfile(CarConfig c, double safety = 0.82)
        {
            double m = c.mass, e = c.loadExp;
            double kq = 0.5 * RHO;
            double fz0F = m * G * c.frontWeight, fz0R = m * G * (1 - c.frontWeight);
            // total tyre grip (N) at speed v
            double grip(double v)
            {
                double down = kq * v * v * c.clA;
                double fF = fz0F + down * c.aeroFront, fR = fz0R + down * (1 - c.aeroFront);
                return safety * c.mu * (fz0F * Math.Pow(fF / fz0F, e) + c.rearGrip * fz0R * Math.Pow(fR / fz0R, e));
            }
            double pMax = 0;
            for (double rpm = c.idleRpm; rpm <= c.redline; rpm += 100) pMax = Math.Max(pMax, FormulaCar.TorqueAt(c, rpm) * rpm * 2 * Math.PI / 60);
            pMax *= c.driveEff;
            double vTop = c.gearTopSpeeds[c.gearTopSpeeds.Length - 1] / 3.6;
            // 1) cornering limit: largest v with m v^2 k <= grip(v)
            var v = new double[N];
            for (int i = 0; i < N; i++)
            {
                double kap = Math.Abs(curv[i]);
                if (m * vTop * vTop * kap <= grip(vTop)) { v[i] = vTop; continue; }
                double lo = 0, hi = vTop;
                for (int it = 0; it < 40; it++) { double mid = (lo + hi) / 2; if (m * mid * mid * kap <= grip(mid)) lo = mid; else hi = mid; }
                v[i] = lo;
            }
            vCorner = (double[])v.Clone();
            // 2) acceleration limit (forward, two passes so it wraps) and 3) braking limit (backward)
            for (int pass = 0; pass < 2; pass++) for (int i = 0; i < N; i++)
            {
                int j = (i + 1) % N; double vi = v[i], q = kq * vi * vi;
                // friction circle: grip used for cornering isn't available for accelerating; only the rear drives
                double total = grip(vi), lat = m * vi * vi * Math.Abs(curv[i]);
                double spare = Math.Sqrt(Math.Max(0, total * total - lat * lat)) * (1 - c.frontWeight);
                double a = (Math.Min(spare, pMax / Math.Max(vi, 5)) - q * c.cdA) / m;
                v[j] = Math.Min(v[j], Math.Sqrt(Math.Max(0, vi * vi + 2 * Math.Max(a, 0) * ds[i])));
            }
            for (int pass = 0; pass < 2; pass++) for (int i = N - 1; i >= 0; i--)
            {
                int j = (i + 1) % N; double vj = v[j], q = kq * vj * vj;
                double total = grip(vj), lat = m * vj * vj * Math.Abs(curv[j]);
                double spare = Math.Sqrt(Math.Max(0, total * total - lat * lat));
                double a = (Math.Min(spare, c.brakeForce) + q * c.cdA) / m * 0.8; // keep some braking in reserve
                v[i] = Math.Min(v[i], Math.Sqrt(vj * vj + 2 * a * ds[i]));
            }
            speed = v;

            // per-point phase for the static overlay (not in the JS, which colours against the live speed):
            // red = speed falls to the next point, yellow = running at the cornering limit, green = speeding up / flat out
            braking = new bool[N]; phase = new sbyte[N]; lapTime = 0;
            for (int i = 0; i < N; i++)
            {
                int j = (i + 1) % N;
                if (v[j] < v[i] - 0.05) { braking[i] = true; phase[i] = -1; }
                else if (vCorner[i] < vTop && v[i] >= vCorner[i] * 0.97) phase[i] = 0;
                else phase[i] = 1;
                lapTime += ds[i] / Math.Max(0.1, (v[i] + v[j]) / 2);
            }
            return v;
        }
    }
}
