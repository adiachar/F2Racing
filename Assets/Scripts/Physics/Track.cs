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

        // layout, filled in by CircuitDef (null for a bare track): kerb width and what lies beyond it, per side
        public const double KerbWidth = 1.4;
        public CircuitDef def;
        public float[] kerbL, kerbR;
        public SurfaceType[] runL, runR;

        // the surface at track point i, lateral offset lat (+ = left)
        public SurfaceType SurfaceAt(int i, double lat)
        {
            double hw = width / 2, a = Math.Abs(lat);
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
        public static Track FromLonLat(double[,] lonlat, double width, double startAt = 0, double[,] elev = null)
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
            var t = Build(pts, width);
            if (elev != null) t.SetElevation(elev);
            return t;
        }

        public double[] h; // height of every point (m); null = flat circuit
        void SetElevation(double[,] keys)
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
