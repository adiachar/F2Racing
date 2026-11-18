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
        readonly double x0, y0; readonly int nx, ny; float[] g;
        readonly Track t;
        // track points by 16 m bucket, as a flat grid (CSR: bucket b holds bIdx[bStart[b] .. bStart[b + 1]), in the
        // order of the points): the same buckets and the same order as the old Dictionary<long, List<int>>, so
        // Sample gives bit-identical heights, without 9 hash lookups per sample (the physics calls it every tick)
        readonly long bx0, by0; readonly int bnx, bny; readonly int[] bStart, bIdx;
        // CPU: the terrain grid is the slow part of a load (every 5 m cell weighs every 8 m track point: ~200 M
        // operations at a 7 km circuit). Rows run in parallel (pure math, no Unity API), and blocks of cells that are
        // certainly more than 380 m from the circuit (where the formula gives exactly the base level) are filled
        // directly. Both give the same numbers as the plain loop. GridCache (set by the game) stores the grid per
        // circuit on disk, so the next load of the same circuit skips it.
        public static Func<string, int, float[]> GridLoad; public static Action<string, float[]> GridSave;
        public const int GridVersion = 1;
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
            {
                long mnx = long.MaxValue, mny = long.MaxValue, mxx = long.MinValue, mxy = long.MinValue;
                var kx = new long[t.N]; var ky = new long[t.N];
                for (int i = 0; i < t.N; i++)
                {
                    kx[i] = (long)Math.Floor(t.pts[i].x / Bucket); ky[i] = (long)Math.Floor(t.pts[i].y / Bucket);
                    mnx = Math.Min(mnx, kx[i]); mny = Math.Min(mny, ky[i]); mxx = Math.Max(mxx, kx[i]); mxy = Math.Max(mxy, ky[i]);
                }
                bx0 = mnx; by0 = mny; bnx = (int)(mxx - mnx + 1); bny = (int)(mxy - mny + 1);
                bStart = new int[bnx * bny + 1]; bIdx = new int[t.N];
                for (int i = 0; i < t.N; i++) bStart[(int)(ky[i] - by0) * bnx + (int)(kx[i] - bx0) + 1]++;
                for (int b = 0; b < bnx * bny; b++) bStart[b + 1] += bStart[b];
                var fill = (int[])bStart.Clone();
                for (int i = 0; i < t.N; i++) bIdx[fill[(int)(ky[i] - by0) * bnx + (int)(kx[i] - bx0)]++] = i;
            }
            int step = Math.Max(1, (int)Math.Round(8 / t.ds)), m = (t.N + step - 1) / step;
            var px = new double[m]; var py = new double[m]; var ph = new double[m];
            double lo = double.MaxValue;
            for (int i = 0; i < m; i++) { var p = t.pts[i * step]; px[i] = p.x; py[i] = p.y; ph[i] = th[i * step]; lo = Math.Min(lo, ph[i]); }
            baseHeight = lo;
            string key = GridLoad != null || GridSave != null ? CacheKey(px, py, ph) : null;
            var cached = GridLoad?.Invoke(key, nx * ny);
            if (cached != null && cached.Length == nx * ny) { g = cached; return; }
            // blocks of 8 x 8 cells whose nearest point is surely beyond 382 m: u = 1 there, so the cell is exactly lo
            const int B = 8; int bxN = (nx + B - 1) / B, byN = (ny + B - 1) / B; var far = new bool[bxN * byN];
            double half = Math.Sqrt(2) * B * Cell / 2 + 1, farD = 382 + half;
            for (int bj = 0; bj < byN; bj++) for (int bi = 0; bi < bxN; bi++)
            {
                double cx = x0 + (bi * B + (B - 1) / 2.0) * Cell, cy = y0 + (bj * B + (B - 1) / 2.0) * Cell, dm = double.MaxValue;
                for (int k = 0; k < m; k++) { double dx = px[k] - cx, dy = py[k] - cy, d2 = dx * dx + dy * dy; if (d2 < dm) dm = d2; }
                far[bj * bxN + bi] = dm > farD * farD;
            }
            var gg = g; float flo = (float)lo; int nxL = nx; double x0L = x0, y0L = y0;
            void Row(int j)
            {
                double y = y0L + j * Cell;
                for (int i = 0; i < nxL; i++)
                {
                    if (far[(j / B) * bxN + i / B]) { gg[j * nxL + i] = flo; continue; }
                    double x = x0L + i * Cell, sw = 0, sh = 0, dmin = double.MaxValue;
                    for (int k = 0; k < m; k++)
                    {
                        double dx = px[k] - x, dy = py[k] - y, d2 = dx * dx + dy * dy;
                        if (d2 < dmin) dmin = d2;
                        double w = 1 / ((d2 + 9) * (d2 + 9));
                        sw += w; sh += w * ph[k];
                    }
                    // far from the circuit the land settles to the lowest level (flat ground for the test pad)
                    double u = Math.Min(1, Math.Max(0, (Math.Sqrt(dmin) - 120) / 260)); u = u * u * (3 - 2 * u);
                    gg[j * nxL + i] = (float)(sh / sw * (1 - u) + lo * u);
                }
            }
            System.Threading.Tasks.Parallel.For(0, ny, Row);
            GridSave?.Invoke(key, g);
        }

        // what the grid depends on: the sampled points, their heights and the grid's size (FNV-1a over the bits)
        string CacheKey(double[] px, double[] py, double[] ph)
        {
            ulong h = 14695981039346656037UL;
            void Mix(double v) { h ^= (ulong)BitConverter.DoubleToInt64Bits(v); h *= 1099511628211UL; }
            Mix(x0); Mix(y0); Mix(nx); Mix(ny); Mix(px.Length);
            for (int k = 0; k < px.Length; k++) { Mix(px[k]); Mix(py[k]); Mix(ph[k]); }
            return $"hf{GridVersion}_{(t.def != null ? t.def.id : "t")}_{h:x16}";
        }


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
            var pts = t.pts;
            for (int dy = -1; dy <= 1; dy++)
            {
                long ky = (long)Math.Floor((y + dy * Bucket) / Bucket) - by0;
                if (ky < 0 || ky >= bny) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    long kx = (long)Math.Floor((x + dx * Bucket) / Bucket) - bx0;
                    if (kx < 0 || kx >= bnx) continue;
                    int b = (int)ky * bnx + (int)kx;
                    for (int q = bStart[b], qe = bStart[b + 1]; q < qe; q++)
                    {
                        int pi = bIdx[q]; var p = pts[pi]; double d = (p.x - x) * (p.x - x) + (p.y - y) * (p.y - y);
                        if (d < bd) { bd = d; best = pi; }
                    }
                }
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
