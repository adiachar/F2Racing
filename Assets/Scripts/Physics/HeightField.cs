// HeightField.cs — the ground height everywhere around a circuit that has hills, from the heights of the track
// points. One function, Sample(x, y), is the single source of truth: the road, the scenery, the cars and the
// physics (slope) all read it, so everything sits on the same surface. Pure C# (no UnityEngine).
//
// On and beside the road (within "Shoulder" of the centreline) the height is exactly the track's own height at
// that point along the lap, so the road is level across its width even where another part of the circuit runs
// close by at a different level (Monaco's hairpin). Further out it blends into a smooth interpolated terrain.
// On a banked corner (Track.bank) the cross-section is tilted about the centreline: the height at lateral offset
// lat (+ = left) is h - lat * tan(bank), the same angle the physics uses, so the wheels sit on the visible road.
using System;
using System.Collections.Generic;

namespace FormulaSim
{
    public class HeightField
    {
        const double Cell = 5, Bucket = 16;
        readonly double x0, y0; readonly int nx, ny; readonly float[] g;
        readonly Track t; readonly Dictionary<long, List<int>> hash = new Dictionary<long, List<int>>();
        readonly double[] th, tb; // heights of the track points (zeros on a flat circuit), tan(bank) (null = none)
        public readonly double baseHeight, Shoulder;
        // most the banking lowers any point of the road or shoulder below its centreline (0 = no banking)
        public readonly double bankDrop;

        public HeightField(Track track)
        {
            t = track; Shoulder = t.width / 2 + 3.4;
            th = t.h ?? new double[t.N];
            if (t.bank != null)
            {
                tb = new double[t.N];
                for (int i = 0; i < t.N; i++) { tb[i] = Math.Tan(t.bank[i]); bankDrop = Math.Max(bankDrop, Math.Abs(tb[i]) * (Shoulder + 5)); }
            }
            var bb = t.Bounds(); const double Margin = 420;
            x0 = bb.minX - Margin; y0 = bb.minY - Margin;
            nx = (int)Math.Ceiling((bb.maxX - bb.minX + 2 * Margin) / Cell) + 1; ny = (int)Math.Ceiling((bb.maxY - bb.minY + 2 * Margin) / Cell) + 1;
            g = new float[nx * ny];
            for (int i = 0; i < t.N; i++)
            {
                long key = Key(t.pts[i].x, t.pts[i].y);
                if (!hash.TryGetValue(key, out var l)) hash[key] = l = new List<int>();
                l.Add(i);
            }
            int step = Math.Max(1, (int)Math.Round(8 / t.ds)), m = (t.N + step - 1) / step;
            var px = new double[m]; var py = new double[m]; var ph = new double[m];
            double lo = double.MaxValue;
            for (int i = 0; i < m; i++) { var p = t.pts[i * step]; px[i] = p.x; py[i] = p.y; ph[i] = th[i * step]; lo = Math.Min(lo, ph[i]); }
            baseHeight = lo;
            for (int j = 0; j < ny; j++)
            {
                double y = y0 + j * Cell;
                for (int i = 0; i < nx; i++)
                {
                    double x = x0 + i * Cell, sw = 0, sh = 0, dmin = double.MaxValue;
                    for (int k = 0; k < m; k++)
                    {
                        double dx = px[k] - x, dy = py[k] - y, d2 = dx * dx + dy * dy;
                        if (d2 < dmin) dmin = d2;
                        double w = 1 / ((d2 + 9) * (d2 + 9));
                        sw += w; sh += w * ph[k];
                    }
                    // far from the circuit the land settles to the lowest level (flat ground for the test pad)
                    double u = Math.Min(1, Math.Max(0, (Math.Sqrt(dmin) - 120) / 260)); u = u * u * (3 - 2 * u);
                    g[j * nx + i] = (float)(sh / sw * (1 - u) + lo * u);
                }
            }
        }

        static long Key(double x, double y) => ((long)Math.Floor(x / Bucket) << 32) ^ ((long)Math.Floor(y / Bucket) & 0xffffffffL);

        double Terrain(double x, double y)
        {
            double fx = (x - x0) / Cell, fy = (y - y0) / Cell;
            if (fx < 0 || fy < 0 || fx >= nx - 1 || fy >= ny - 1) return baseHeight;
            int i = (int)fx, j = (int)fy; double a = fx - i, b = fy - j; int k = j * nx + i;
            return (g[k] * (1 - a) + g[k + 1] * a) * (1 - b) + (g[k + nx] * (1 - a) + g[k + nx + 1] * a) * b;
        }

        public double Sample(double x, double y)
        {
            // nearest track point, from the 3 x 3 buckets around (covers everything within 16 m)
            int best = -1; double bd = double.MaxValue;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                if (hash.TryGetValue(Key(x + dx * Bucket, y + dy * Bucket), out var l))
                    for (int q = 0; q < l.Count; q++)
                    {
                        var p = t.pts[l[q]]; double d = (p.x - x) * (p.x - x) + (p.y - y) * (p.y - y);
                        if (d < bd) { bd = d; best = l[q]; }
                    }
            double blendEnd = Shoulder + 5;
            if (best < 0 || bd >= blendEnd * blendEnd) return Terrain(x, y);
            var pb = t.pts[best];
            double along = (x - pb.x) * pb.tx + (y - pb.y) * pb.ty;
            int nb = along >= 0 ? (best + 1) % t.N : (best - 1 + t.N) % t.N;
            double fa = Math.Min(1, Math.Abs(along) / t.ds);
            double road = th[best] + (th[nb] - th[best]) * fa;
            if (tb != null)
            {
                double lat = (x - pb.x) * pb.nx + (y - pb.y) * pb.ny;
                road -= lat * (tb[best] + (tb[nb] - tb[best]) * fa);
            }
            double dist = Math.Sqrt(bd);
            if (dist <= Shoulder) return road;
            double u = (dist - Shoulder) / 5; u = u * u * (3 - 2 * u);
            return road * (1 - u) + Terrain(x, y) * u;
        }

        // lowest ground within r of a point: the open land must never rise above a road that passes close by
        public double Lowest(double x, double y, double r)
        {
            double lo = Sample(x, y);
            for (int k = 0; k < 8; k++) { double a = k * Math.PI / 4; lo = Math.Min(lo, Sample(x + Math.Cos(a) * r, y + Math.Sin(a) * r)); lo = Math.Min(lo, Sample(x + Math.Cos(a) * r * 0.5, y + Math.Sin(a) * r * 0.5)); }
            return lo;
        }

        // slope of the ground in the direction of travel (rise per metre; + = uphill)
        public double Grade(double x, double y, double heading)
        {
            double c = Math.Cos(heading) * 1.7, s = Math.Sin(heading) * 1.7;
            return (Sample(x + c, y + s) - Sample(x - c, y - s)) / 3.4;
        }
    }
}
