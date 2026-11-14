// Track.cs — circuit centreline (centripetal Catmull-Rom, resampled every 2 m), port of the web track.js.
// Same coordinate frame as FormulaCar: x/y plane, left normal (nx, ny), curvature + = left turn.
using System;
using System.Collections.Generic;

namespace FormulaSim
{
    public struct TrackPoint { public double x, y, s, tx, ty, nx, ny, curv; }

    public class Track
    {
        public static readonly double[,] DefaultPoints = {
            { 0, 0 }, { 300, 0 }, { 500, 0 }, { 620, 30 }, { 660, 120 }, { 620, 200 }, { 520, 230 },
            { 430, 280 }, { 420, 380 }, { 480, 460 }, { 600, 480 }, { 700, 560 }, { 680, 680 },
            { 560, 720 }, { 400, 700 }, { 260, 640 }, { 200, 560 }, { 120, 520 }, { 0, 540 },
            { -120, 500 }, { -180, 400 }, { -160, 300 }, { -220, 200 }, { -200, 80 }, { -120, 10 },
        };

        public TrackPoint[] pts;
        public int N;
        public double length, ds, width;
        // Variable width (measured TUM circuits only): half-width of every point (m), smoothed; null = width / 2
        // everywhere. OFF by default: with it on, the race test had more AI resets (14 -> 18 on the TUM circuits,
        // Zandvoort 0 -> 4), and the game's wall line, off-track timer and road mesh still use the single width.
        // Used by SurfaceAt, the racing-line margin and the AI's room only. PhysicsCheck: VARWIDTH=1 turns it on.
        public static bool VariableWidth = false;
        public double[] halfW;
        public double HalfWidth(int i) => halfW == null ? width / 2 : halfW[i];
        // track point of starting slot k (0 = pole): slots 8 m apart (4 points), pole CircuitDef.gridOffset metres before
        // the timing line (negative: past it); without a circuit 30 m, as before
        public int GridIndex(int k) => (((N - (int)Math.Round((def != null ? def.gridOffset : 30) / ds) - 4 * k) % N) + N) % N;

        // layout, filled in by CircuitDef (null for a bare track): kerb width and what lies beyond it, per side
        public const double KerbWidth = 1.4;
        public CircuitDef def;
        public float[] kerbL, kerbR;
        public SurfaceType[] runL, runR;

        // the surface at track point i, lateral offset lat (+ = left)
        public SurfaceType SurfaceAt(int i, double lat)
        {
            double hw = HalfWidth(i), a = Math.Abs(lat);
            if (a <= hw - 0.4) return SurfaceType.Asphalt;
            if (a <= hw - 0.15) return SurfaceType.Paint;          // white edge line
            if (a <= hw) return SurfaceType.Asphalt;
            if (kerbL == null) return a <= hw + KerbWidth ? SurfaceType.Kerb : SurfaceType.Grass;
            bool left = lat > 0;
            double kw = left ? kerbL[i] : kerbR[i];
            if (a <= hw + kw) return SurfaceType.Kerb;
            var run = left ? runL[i] : runR[i];
            if (def != null && def.style == CircuitStyle.Parkland && a <= hw + kw + 2.0) return SurfaceType.Astroturf; // strip behind the kerb
            if (run == SurfaceType.Gravel && a < hw + KerbWidth + 2.0) return SurfaceType.Grass; // gravel traps start behind the kerb line
            return run;
        }

        static (double, double) Catmull((double x, double y) p0, (double x, double y) p1, (double x, double y) p2, (double x, double y) p3, double t)
        {
            double D((double x, double y) a, (double x, double y) b) { double d = Math.Sqrt(Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y))); return d == 0 ? 1e-4 : d; }
            double t0 = 0, t1 = D(p0, p1), t2 = t1 + D(p1, p2), t3 = t2 + D(p2, p3);
            double tt = t1 + (t2 - t1) * t;
            (double, double) L((double x, double y) a, (double x, double y) b, double ta, double tb) { double w = (tt - ta) / (tb - ta); return (a.x + (b.x - a.x) * w, a.y + (b.y - a.y) * w); }
            var A1 = L(p0, p1, t0, t1); var A2 = L(p1, p2, t1, t2); var A3 = L(p2, p3, t2, t3);
            var B1 = L(A1, A2, t0, t2); var B2 = L(A2, A3, t1, t3);
            return L(B1, B2, t1, t2);
        }

        public static Track Build(double[,] points = null, double width = 14, double step = 2)
        {
            points ??= DefaultPoints;
            int n = points.GetLength(0);
            var dense = new List<(double x, double y)>();
            for (int i = 0; i < n; i++)
            {
                (double, double) P(int k) { k = ((k % n) + n) % n; return (points[k, 0], points[k, 1]); }
                var p1 = P(i); var p2 = P(i + 1);
                int m = Math.Max(8, (int)Math.Ceiling(Math.Sqrt((p2.Item1 - p1.Item1) * (p2.Item1 - p1.Item1) + (p2.Item2 - p1.Item2) * (p2.Item2 - p1.Item2)) / 0.5));
                for (int j = 0; j < m; j++) dense.Add(Catmull(P(i - 1), p1, p2, P(i + 2), (double)j / m));
            }
            var cum = new double[dense.Count + 1];
            for (int i = 1; i <= dense.Count; i++)
            {
                var a = dense[i - 1]; var b = dense[i % dense.Count];
                cum[i] = cum[i - 1] + Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
            }
            double total = cum[dense.Count];
            int N = (int)Math.Round(total / step);
            double ds = total / N;
            var pts = new TrackPoint[N];
            int k2 = 0;
            for (int i = 0; i < N; i++)
            {
                double s = i * ds;
                while (cum[k2 + 1] < s) k2++;
                var a = dense[k2]; var b = dense[(k2 + 1) % dense.Count];
                double den = cum[k2 + 1] - cum[k2]; double w = (s - cum[k2]) / (den == 0 ? 1 : den);
                pts[i] = new TrackPoint { x = a.x + (b.x - a.x) * w, y = a.y + (b.y - a.y) * w, s = s };
            }
            for (int i = 0; i < N; i++)
            {
                var p = pts[(i - 1 + N) % N]; var q = pts[(i + 1) % N];
                double tx = q.x - p.x, ty = q.y - p.y, l = Math.Sqrt(tx * tx + ty * ty);
                pts[i].tx = tx / l; pts[i].ty = ty / l; pts[i].nx = -pts[i].ty; pts[i].ny = pts[i].tx;
            }
            var raw = new double[N];
            for (int i = 0; i < N; i++)
            {
                var p = pts[(i - 1 + N) % N]; var q = pts[(i + 1) % N];
                double da = Math.Atan2(q.ty, q.tx) - Math.Atan2(p.ty, p.tx);
                while (da > Math.PI) da -= 2 * Math.PI;
                while (da < -Math.PI) da += 2 * Math.PI;
                raw[i] = da / (2 * ds);
            }
            for (int i = 0; i < N; i++) { double s = 0; for (int j = -3; j <= 3; j++) s += raw[(i + j + N) % N]; pts[i].curv = s / 7; }
            return new Track { pts = pts, N = N, length = total, ds = ds, width = width };
        }

        // Real circuit from (longitude, latitude) points: local metres (equirectangular around the centre,
        // accurate to a few cm over a few km), x = east, y = north. The start/finish line is the first point.
        // startAt: distance (m) along the raw data at which the real start/finish line lies (the data may begin anywhere).
        // elev: optional {distance from the start line (m), height (m)} keys; gives every point a height (h).
        // Spread each bend's curvature over a Gaussian window of sigma metres (same total turn, loop kept closed).
        // A coarse outline (one point every 20 m) squeezes a bend into a single vertex, which makes corners far
        // tighter than the real road; real roads change curvature gradually.
        public double[,] SmoothedCurvature(double sigma) => SmoothedCurvature(_ => sigma);
        // sigmaAt(s): the window can differ along the lap (each corner fitted on its own; see CircuitDef.curveFit)
        public double[,] SmoothedCurvature(Func<double, double> sigmaAt)
        {
            var th = new double[N + 1]; th[0] = Math.Atan2(pts[0].ty, pts[0].tx);
            var k = new double[N];
            for (int i = 0; i < N; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % N];
                double d = Math.Atan2(b.ty, b.tx) - Math.Atan2(a.ty, a.tx);
                while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
                k[i] = d; // turn per step
            }
            // spread each point's turn over its neighbours (scatter form: the total turn is kept exactly)
            var ksm = new double[N];
            for (int i = 0; i < N; i++)
            {
                double sg = sigmaAt(pts[i].s);
                if (sg < 0.5) { ksm[i] += k[i]; continue; }
                int w = (int)Math.Ceiling(3 * sg / ds); double ks = 0;
                for (int j = -w; j <= w; j++) ks += Math.Exp(-0.5 * (j * ds / sg) * (j * ds / sg));
                for (int j = -w; j <= w; j++) ksm[((i + j) % N + N) % N] += k[i] * Math.Exp(-0.5 * (j * ds / sg) * (j * ds / sg)) / ks;
            }
            var P = new double[N, 2]; double x = pts[0].x, y = pts[0].y, h = th[0];
            for (int i = 0; i < N; i++) { P[i, 0] = x; P[i, 1] = y; x += Math.Cos(h + ksm[i] / 2) * ds; y += Math.Sin(h + ksm[i] / 2) * ds; h += ksm[i]; }
            double ex = x - pts[0].x, ey = y - pts[0].y; // closure error, spread along the lap
            for (int i = 0; i < N; i++) { P[i, 0] -= ex * i / N; P[i, 1] -= ey * i / N; }
            return P;
        }

        public static Track FromLonLat(double[,] lonlat, double width, double startAt = 0, double[,] elev = null, double curveSigma = 0, double[,] curveFit = null)
        {
            int n = lonlat.GetLength(0);
            double lon0 = 0, lat0 = 0;
            for (int i = 0; i < n; i++) { lon0 += lonlat[i, 0]; lat0 += lonlat[i, 1]; }
            lon0 /= n; lat0 /= n;
            const double R = 6371000;
            double kx = Math.PI / 180 * R * Math.Cos(lat0 * Math.PI / 180), ky = Math.PI / 180 * R;
            var raw = new double[n, 2];
            for (int i = 0; i < n; i++) { raw[i, 0] = (lonlat[i, 0] - lon0) * kx; raw[i, 1] = (lonlat[i, 1] - lat0) * ky; }
            // begin at the raw point nearest to the start line
            int first = 0;
            if (startAt > 0)
            {
                double acc = 0, best = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (Math.Abs(acc - startAt) < best) { best = Math.Abs(acc - startAt); first = i; }
                    int j = (i + 1) % n; acc += Math.Sqrt((raw[j, 0] - raw[i, 0]) * (raw[j, 0] - raw[i, 0]) + (raw[j, 1] - raw[i, 1]) * (raw[j, 1] - raw[i, 1]));
                }
            }
            // put the start/finish line at the origin
            double ox = raw[first, 0], oy = raw[first, 1];
            var pts = new double[n, 2];
            for (int i = 0; i < n; i++) { int k = (i + first) % n; pts[i, 0] = raw[k, 0] - ox; pts[i, 1] = raw[k, 1] - oy; }
            var t = Smoothed(Build(pts, width), curveSigma, curveFit);
            if (elev != null) t.SetElevation(elev);
            return t;
        }

        // the track rebuilt with each bend's curvature spread out (see SmoothedCurvature); unchanged without a fit
        public static Track Smoothed(Track t, double curveSigma, double[,] curveFit)
        {
            if (curveSigma <= 0 && curveFit == null) return t;
            return Build(t.SmoothedCurvature(sAt => SigmaAt(sAt, curveSigma, curveFit)), t.width);
        }

        // Measured centre lines (TUM): spread each listed bend's curvature over a Gaussian window (fit rows {s, sigma,
        // reach}: the turn of every point within reach of s is spread over +-3 sigma), one bend at a time and only
        // inside that bend's own stretch of track: the stretch keeps its segment lengths (so the track length), its
        // total turn, and both ends, so the rest of the circuit does not move at all.
        public static Track LocalFit(Track t, double[,] fit, bool keepLength = true)
        {
            if (fit == null) return t;
            int N = t.N; double ds = t.ds;
            var px = new double[N]; var py = new double[N];
            for (int i = 0; i < N; i++) { px[i] = t.pts[i].x; py[i] = t.pts[i].y; }
            int W(int i) => ((i % N) + N) % N;
            for (int c = 0; c < fit.GetLength(0); c++)
            {
                double sg = fit[c, 1], reach = fit.GetLength(1) > 2 ? fit[c, 2] : 60;
                if (sg < 0.5) continue;
                int ic = (int)Math.Round(fit[c, 0] / ds), r = (int)Math.Ceiling(reach / ds), g = (int)Math.Ceiling(3 * sg / ds);
                int i0 = ic - r - g - 2, m = 2 * (r + g + 2);            // the stretch: points i0 .. i0 + m
                if (m >= N / 2) continue;
                var len = new double[m]; var hd = new double[m];
                for (int j = 0; j < m; j++)
                {
                    double dx = px[W(i0 + j + 1)] - px[W(i0 + j)], dy = py[W(i0 + j + 1)] - py[W(i0 + j)];
                    len[j] = Math.Sqrt(dx * dx + dy * dy); hd[j] = Math.Atan2(dy, dx);
                }
                var turn = new double[m];                                 // turn at point j (between segment j-1 and j)
                for (int j = 1; j < m; j++)
                {
                    double d = hd[j] - hd[j - 1];
                    while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
                    double s = (j - (ic - i0)) * ds;
                    if (Math.Abs(s) > reach) { turn[j] += d; continue; }
                    double ks = 0;
                    for (int q = -g; q <= g; q++) ks += Math.Exp(-0.5 * (q * ds / sg) * (q * ds / sg));
                    for (int q = -g; q <= g; q++) turn[Math.Min(m - 1, Math.Max(1, j + q))] += d * Math.Exp(-0.5 * (q * ds / sg) * (q * ds / sg)) / ks;
                }
                // integrate the new stretch from its first point, then pull its far end back onto the old one
                var qx = new double[m + 1]; var qy = new double[m + 1];
                double x = px[W(i0)], y = py[W(i0)], h = hd[0];
                for (int j = 0; j < m; j++) { if (j > 0) h += turn[j]; qx[j] = x; qy[j] = y; x += Math.Cos(h) * len[j]; y += Math.Sin(h) * len[j]; }
                qx[m] = x; qy[m] = y;
                double ex = qx[m] - px[W(i0 + m)], ey = qy[m] - py[W(i0 + m)];
                for (int j = 1; j < m; j++) { double u = (double)j / m; u = u * u * (3 - 2 * u); px[W(i0 + j)] = qx[j] - ex * u; py[W(i0 + j)] = qy[j] - ey * u; }
            }
            var P = new double[N, 2];
            for (int i = 0; i < N; i++) { P[i, 0] = px[i]; P[i, 1] = py[i]; }
            // a rounder bend cuts in a little and the lap gets shorter: scale the circuit (about its middle) back to
            // the measured length; that moves nothing by more than a few metres
            if (!keepLength) return Build(P, t.width);
            double k = t.length / Build(P, t.width).length, cx = 0, cy = 0;
            for (int i = 0; i < N; i++) { cx += px[i] / N; cy += py[i] / N; }
            for (int i = 0; i < N; i++) { P[i, 0] = cx + (px[i] - cx) * k; P[i, 1] = cy + (py[i] - cy) * k; }
            return Build(P, t.width);
        }

        // the curvature window at distance s: the fitted value of the corner within its reach (third column, m;
        // 60 m when there is none), otherwise the default
        public static double SigmaAt(double s, double sigma0, double[,] fit)
        {
            if (fit == null) return sigma0;
            bool reach = fit.GetLength(1) > 2;
            for (int i = 0; i < fit.GetLength(0); i++) if (Math.Abs(fit[i, 0] - s) <= (reach ? fit[i, 2] : 60)) return fit[i, 1];
            return sigma0;
        }

        // banking of every point (rad, + = the road rises to the right of the direction of travel, so it leans into
        // a left-hand bend); null = no banked corners
        public double[] bank;
        // keys {from s, to s, degrees}: each stretch leans into its own bend (the side follows the stretch's total
        // turn), easing in and out over 25 m at the ends
        public void SetBanking(double[,] keys)
        {
            bank = new double[N];
            for (int k = 0; k < keys.GetLength(0); k++)
            {
                double s0 = keys[k, 0], s1 = keys[k, 1], turn = 0;
                for (int i = 0; i < N; i++) if (pts[i].s >= s0 && pts[i].s <= s1) turn += pts[i].curv * ds;
                double b = Math.Sign(turn) * keys[k, 2] * Math.PI / 180;
                for (int i = 0; i < N; i++)
                {
                    double s = pts[i].s; if (s < s0 || s > s1) continue;
                    double u = Math.Min(1, Math.Min(s - s0, s1 - s) / 25); u = u * u * (3 - 2 * u);
                    bank[i] = b * u;
                }
            }
        }

        public double[] h; // height of every point (m); null = flat circuit
        public void SetElevation(double[,] keys)
        {
            int m = keys.GetLength(0); h = new double[N];
            for (int i = 0; i < N; i++)
            {
                double s = pts[i].s; int k = 0;
                while (k < m - 1 && keys[k + 1, 0] <= s) k++;
                double s0 = keys[k, 0], h0 = keys[k, 1], s1 = k + 1 < m ? keys[k + 1, 0] : length + keys[0, 0], h1 = k + 1 < m ? keys[k + 1, 1] : keys[0, 1];
                if (s < keys[0, 0]) { s0 = keys[m - 1, 0] - length; h0 = keys[m - 1, 1]; s1 = keys[0, 0]; h1 = keys[0, 1]; }
                double u = Math.Min(1, Math.Max(0, (s - s0) / Math.Max(1e-6, s1 - s0))); u = u * u * (3 - 2 * u);
                h[i] = h0 + (h1 - h0) * u;
            }
            // round off the joins (about 40 m)
            for (int pass = 0; pass < 3; pass++)
            {
                var o = new double[N]; int w = (int)Math.Round(20 / ds);
                for (int i = 0; i < N; i++) { double sum = 0; for (int j = -w; j <= w; j++) sum += h[((i + j) % N + N) % N]; o[i] = sum / (2 * w + 1); }
                h = o;
            }
        }

        public (double minX, double minY, double maxX, double maxY) Bounds()
        {
            double a = double.MaxValue, b = double.MaxValue, c = double.MinValue, d = double.MinValue;
            foreach (var p in pts) { a = Math.Min(a, p.x); b = Math.Min(b, p.y); c = Math.Max(c, p.x); d = Math.Max(d, p.y); }
            return (a, b, c, d);
        }

        // nearest centreline sample; hint = previous index for a fast local search
        public (int idx, double lateral, double dist) Nearest(double x, double y, int hint = -1)
        {
            int best = -1; double bd = double.MaxValue;
            void Scan(int i) { int k = ((i % N) + N) % N; var p = pts[k]; double d = (p.x - x) * (p.x - x) + (p.y - y) * (p.y - y); if (d < bd) { bd = d; best = k; } }
            if (hint < 0) for (int i = 0; i < N; i++) Scan(i);
            else for (int i = hint - 60; i <= hint + 60; i++) Scan(i);
            if (hint >= 0 && bd > 60 * 60) return Nearest(x, y, -1);
            var pb = pts[best];
            return (best, (x - pb.x) * pb.nx + (y - pb.y) * pb.ny, Math.Sqrt(bd));
        }
    }
}
