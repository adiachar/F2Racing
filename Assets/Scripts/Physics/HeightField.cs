// HeightField.cs — the ground height everywhere around a circuit that has hills, from the heights of the track
// points. One function, Sample(x, y), is the single source of truth: the road, the scenery, the cars and the
// physics (slope) all read it, so everything sits on the same surface. Pure C# (no UnityEngine).
//
// On and beside the road (within "Shoulder" of the centreline) the height is exactly the track's own height at
// that point along the lap, so the road is level across its width even where another part of the circuit runs
// close by at a different level (Monaco's hairpin). Further out it blends into a smooth interpolated terrain.
// On a banked corner (Track.bank) the cross-section is tilted about the centreline: the height at lateral offset
// lat (+ = left) is h - lat * tan(bank), the same angle the physics uses, so the wheels sit on the visible road.
//
// The land beside the road (run-off, barriers, stands: out to Band m from the centreline) is held at the height of
// the road's edge, then eases (smoothstep) into the interpolated terrain over a width that grows with the height
// difference, so the banks stay below ~31 deg. Where two legs of the circuit run close at different levels the
// ground climbs from one to the other between their shoulders. The plain interpolation alone let other legs of the
// lap (100-300 m away, tens of metres higher or lower) pull the ground beside the road up or down by metres.
//
// Bridges: where one leg crosses over another (Suzuka) the upper leg's points over the lower road are a deck. The
// ground (terrain, Ground) ignores the deck, so the lower road runs through at its own level and the land climbs
// to the upper leg's embankments beyond the deck's ends. Sample (what the cars, the physics and the dressing
// read) is the top surface: the deck where the upper road is, the ground elsewhere. A thing that knows which track
// point it belongs to (the road meshes: SampleOn) or which way it faces (a car: SampleAlong) gets its own leg's
// height inside the crossing, where the two overlap.
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
        public const int GridVersion = 2; // 2: the road-edge band beside the track, bridges
        readonly double[] th, tb; // heights of the track points (zeros on a flat circuit), tan(bank) (null = none)
        public readonly double baseHeight, Shoulder;
        // most the banking lowers any point of the road or shoulder below its centreline (0 = no banking)
        public readonly double bankDrop;
        // the land beside the road is level with its edge out to Band m from the centreline (the run-off, the barrier
        // and the stands behind it), then eases into the terrain over at most BlendMax m
        public readonly double Band;
        const double BlendMax = 90, BlendMin = 10, BankSlope = 2.5; // blend width >= 2.5 x height difference: <= ~31 deg
        // bridges: deck[i] = track point i is on a bridge over another leg (null = no bridges); the deck reaches
        // DeckHalf m either side of the centreline (the barrier line, plus a little)
        readonly bool[] deck;
        public readonly double DeckHalf;
        public bool HasBridges => deck != null;
        public bool OnDeck(int i) => deck != null && deck[((i % t.N) + t.N) % t.N];

        public HeightField(Track track)
        {
            t = track; Shoulder = t.width / 2 + 3.4;
            th = t.h ?? new double[t.N];
            if (t.bank != null)
            {
                tb = new double[t.N];
                for (int i = 0; i < t.N; i++) { tb[i] = Math.Tan(t.bank[i]); bankDrop = Math.Max(bankDrop, Math.Abs(tb[i]) * (Shoulder + 5)); }
            }
            Band = t.width / 2 + (t.def != null ? t.def.barrier : 9.3) + 8;
            DeckHalf = Traffic.WallOffset(t) + 0.6;
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
            deck = FindBridges();
            int step = Math.Max(1, (int)Math.Round(8 / t.ds)), m = (t.N + step - 1) / step;
            var px = new double[m]; var py = new double[m]; var ph = new double[m];
            // per sampled point: tangent, left normal, tan(bank), slope along (rise per m), track index, on a deck
            var ptx = new double[m]; var pty = new double[m]; var ptb = new double[m]; var psl = new double[m]; var pk = new int[m]; var pd = new bool[m];
            double lo = double.MaxValue;
            for (int i = 0; i < m; i++)
            {
                int k = i * step; var p = t.pts[k]; px[i] = p.x; py[i] = p.y; ph[i] = th[k]; lo = Math.Min(lo, ph[i]);
                ptx[i] = p.tx; pty[i] = p.ty; ptb[i] = tb != null ? tb[k] : 0; pk[i] = k; pd[i] = deck != null && deck[k];
                psl[i] = (th[(k + 1) % t.N] - th[(k - 1 + t.N) % t.N]) / (2 * t.ds);
            }
            baseHeight = lo;
            string key = GridLoad != null || GridSave != null ? CacheKey(px, py, ph, pd) : null;
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
            // the sampled ground points (not on a deck) in 48 m cells, for the search for another leg near a cell
            const double CB = 48, R2 = 220, R2Fade = 80; // (another leg further than R2 m: ignored, faded out from R2 - R2Fade)
            int cx0 = (int)Math.Floor(bb.minX / CB), cy0 = (int)Math.Floor(bb.minY / CB), cnx = (int)Math.Floor(bb.maxX / CB) - cx0 + 1, cny = (int)Math.Floor(bb.maxY / CB) - cy0 + 1;
            var cStart = new int[cnx * cny + 1]; var cIdx = new int[m];
            for (int k = 0; k < m; k++) if (!pd[k]) cStart[((int)Math.Floor(py[k] / CB) - cy0) * cnx + (int)Math.Floor(px[k] / CB) - cx0 + 1]++;
            for (int c = 0; c < cnx * cny; c++) cStart[c + 1] += cStart[c];
            { var fill = (int[])cStart.Clone(); for (int k = 0; k < m; k++) if (!pd[k]) cIdx[fill[((int)Math.Floor(py[k] / CB) - cy0) * cnx + (int)Math.Floor(px[k] / CB) - cx0]++] = k; }
            var gg = g; float flo = (float)lo; int nxL = nx; double x0L = x0, y0L = y0, S0 = Shoulder, band = Band, lap = t.length, dsL = t.ds, holdMax = Math.Max(0, Band - 5 - Shoulder);
            // height of the land at the edge of sampled point k's shoulder, level with the road there (beside a bank:
            // the shoulder's outer edge), and the distance d from that point
            double Edge(int k, double x, double y, out double d)
            {
                double dx = x - px[k], dy = y - py[k]; d = Math.Sqrt(dx * dx + dy * dy);
                double al = dx * ptx[k] + dy * pty[k], lat = -dx * pty[k] + dy * ptx[k]; // (left normal = (-ty, tx))
                return ph[k] + psl[k] * Math.Max(-step * dsL / 2, Math.Min(step * dsL / 2, al)) - Math.Max(-S0, Math.Min(S0, lat)) * ptb[k];
            }
            void Row(int j)
            {
                double y = y0L + j * Cell;
                for (int i = 0; i < nxL; i++)
                {
                    if (far[(j / B) * bxN + i / B]) { gg[j * nxL + i] = flo; continue; }
                    double x = x0L + i * Cell, sw = 0, sh = 0, dmin = double.MaxValue; int k1 = -1;
                    for (int k = 0; k < m; k++)
                    {
                        if (pd[k]) continue; // (a bridge deck is not ground)
                        double dx = px[k] - x, dy = py[k] - y, d2 = dx * dx + dy * dy;
                        if (d2 < dmin) { dmin = d2; k1 = k; }
                        double w = 1 / ((d2 + 9) * (d2 + 9));
                        sw += w; sh += w * ph[k];
                    }
                    // far from the circuit the land settles to the lowest level (flat ground for the test pad)
                    double dn = Math.Sqrt(dmin);
                    double u = Math.Min(1, Math.Max(0, (dn - 120) / 260)); u = u * u * (3 - 2 * u);
                    double natural = sh / sw * (1 - u) + lo * u;
                    if (k1 < 0 || dn >= band + BlendMax) { gg[j * nxL + i] = (float)natural; continue; }
                    // near the road: level with the nearest road edge; between two legs, from one edge to the other
                    double he = Edge(k1, x, y, out double d1);
                    double sepMin = 2 * d1 + 16, dm2 = R2 * R2; int k2 = -1;
                    int qx0 = Math.Max(0, (int)Math.Floor((x - R2) / CB) - cx0), qx1 = Math.Min(cnx - 1, (int)Math.Floor((x + R2) / CB) - cx0);
                    int qy0 = Math.Max(0, (int)Math.Floor((y - R2) / CB) - cy0), qy1 = Math.Min(cny - 1, (int)Math.Floor((y + R2) / CB) - cy0);
                    for (int qy = qy0; qy <= qy1; qy++)
                        for (int qx = qx0; qx <= qx1; qx++)
                            for (int q = cStart[qy * cnx + qx], qe = cStart[qy * cnx + qx + 1]; q < qe; q++)
                            {
                                int k = cIdx[q];
                                double sep = Math.Abs(pk[k] - pk[k1]) * dsL; sep = Math.Min(sep, lap - sep);
                                if (sep <= sepMin) continue; // (the same stretch of road)
                                double dx = px[k] - x, dy = py[k] - y, d2 = dx * dx + dy * dy;
                                if (d2 < dm2) { dm2 = d2; k2 = k; }
                            }
                    if (k2 >= 0)
                    {
                        // a smooth step across the gap between the two shoulders (a + b): each leg keeps its level out to
                        // hold m past its shoulder (at most to Band - 5: the barriers and what stands behind them) where
                        // the gap leaves room for a bank of <= ~31 deg between; symmetric, the same seen from either leg
                        double he2 = Edge(k2, x, y, out double d2), a = Math.Max(0, d1 - S0), b = Math.Max(0, d2 - S0), gap = a + b;
                        double need = Math.Max(3, BankSlope * Math.Abs(he2 - he)), hold = Math.Max(0, Math.Min(holdMax, (gap - need) / 2));
                        double z = gap - 2 * hold > 1e-6 ? Math.Min(1, Math.Max(0, (a - hold) / (gap - 2 * hold))) : 0.5;
                        double f = Math.Min(1, Math.Max(0, (R2 - d2) / R2Fade)); // (only past Band + BlendMax: no effect on the symmetry)
                        he += (he2 - he) * z * z * (3 - 2 * z) * f * f * (3 - 2 * f);
                    }
                    double bw = Math.Min(BlendMax, Math.Max(BlendMin, BankSlope * Math.Abs(natural - he)));
                    double v = Math.Min(1, Math.Max(0, (d1 - band) / bw)); v = v * v * (3 - 2 * v);
                    gg[j * nxL + i] = (float)(he + (natural - he) * v);
                }
            }
            FormulaSim.Par.For(0, ny, Row);
            GridSave?.Invoke(key, g);
        }

        // Bridges: where the centrelines of two legs cross with the upper one at least MinRise above the lower, the
        // upper leg's points within the span of the lower road (its shoulders plus room for the banks under the ends:
        // Shoulder + 4 + 2.2 x the height difference) are a deck. null = no bridges.
        bool[] FindBridges()
        {
            if (t.h == null) return null;
            const double MinRise = 3;
            bool[] dk = null; int N = t.N; var p = t.pts;
            for (int i = 0; i < N; i++)
            {
                int i1 = (i + 1) % N;
                long kxi = (long)Math.Floor(p[i].x / Bucket) - bx0, kyi = (long)Math.Floor(p[i].y / Bucket) - by0;
                for (long ky = Math.Max(0, kyi - 1); ky <= Math.Min(bny - 1, kyi + 1); ky++)
                for (long kx = Math.Max(0, kxi - 1); kx <= Math.Min(bnx - 1, kxi + 1); kx++)
                for (int q0 = bStart[ky * bnx + kx], qe0 = bStart[ky * bnx + kx + 1]; q0 < qe0; q0++)
                {
                    int k = bIdx[q0];
                    double sep = Math.Abs(k - i) * t.ds; sep = Math.Min(sep, t.length - sep);
                    if (sep < 60) continue;
                    double ex = p[k].x - p[i].x, ey = p[k].y - p[i].y;
                    if (ex * ex + ey * ey > 400) continue; // (the segments are ~2 m long)
                    int k1 = (k + 1) % N;
                    // do segments i-i1 and k-k1 cross?
                    double ax = p[i1].x - p[i].x, ay = p[i1].y - p[i].y, bx = p[k1].x - p[k].x, by = p[k1].y - p[k].y;
                    double den = ax * by - ay * bx; if (Math.Abs(den) < 1e-9) continue;
                    double ua = (ex * by - ey * bx) / den, ub = (ex * ay - ey * ax) / den;
                    if (ua < 0 || ua >= 1 || ub < 0 || ub >= 1) continue;
                    double rise = th[i] - th[k];
                    if (rise < MinRise) continue; // (found again from the upper leg's side)
                    double span = Shoulder + 4 + 2.2 * rise;
                    double ToLower(int j)
                    {
                        double best = double.MaxValue;
                        for (int q = -100; q <= 100; q++) { var r = p[((k + q) % N + N) % N]; double dx = r.x - p[j].x, dy = r.y - p[j].y; best = Math.Min(best, dx * dx + dy * dy); }
                        return Math.Sqrt(best);
                    }
                    if (dk == null) dk = new bool[N];
                    for (int dir = -1; dir <= 1; dir += 2)
                        for (int q = dir < 0 ? 0 : 1; q < N / 4; q++)
                        {
                            int j = ((i + dir * q) % N + N) % N;
                            if (ToLower(j) > span) break;
                            dk[j] = true;
                        }
                }
            }
            return dk;
        }

        // what the grid depends on: the sampled points, their heights and the grid's size (FNV-1a over the bits)
        string CacheKey(double[] px, double[] py, double[] ph, bool[] pd)
        {
            ulong h = 14695981039346656037UL;
            void Mix(double v) { h ^= (ulong)BitConverter.DoubleToInt64Bits(v); h *= 1099511628211UL; }
            Mix(x0); Mix(y0); Mix(nx); Mix(ny); Mix(px.Length); Mix(Band); Mix(Shoulder);
            for (int k = 0; k < px.Length; k++) { Mix(px[k]); Mix(py[k]); Mix(ph[k]); Mix(pd[k] ? 1 : 0); }
            if (tb != null) for (int k = 0; k < t.N; k += 4) Mix(tb[k]);
            return $"hf{GridVersion}_{(t.def != null ? t.def.id : "t")}_{h:x16}";
        }


        double Terrain(double x, double y)
        {
            double fx = (x - x0) / Cell, fy = (y - y0) / Cell;
            if (fx < 0 || fy < 0 || fx >= nx - 1 || fy >= ny - 1) return baseHeight;
            int i = (int)fx, j = (int)fy; double a = fx - i, b = fy - j; int k = j * nx + i;
            return (g[k] * (1 - a) + g[k + 1] * a) * (1 - b) + (g[k + nx] * (1 - a) + g[k + nx + 1] * a) * b;
        }

        // the top surface: the road (or a bridge deck) where there is road, else the ground (see the header)
        public double Sample(double x, double y) => Core(x, y, true);
        // the ground: as Sample, but under a bridge the land (and the road that runs under it), not the deck
        public double Ground(double x, double y) => deck == null ? Core(x, y, true) : Core(x, y, false);

        // the surface for something that belongs to track point i (the road, kerbs and verges built along the lap):
        // on a deck its own road plane, elsewhere the ground. Without bridges the same as Sample.
        public double SampleOn(double x, double y, int i)
        {
            if (deck == null) return Core(x, y, true);
            i = ((i % t.N) + t.N) % t.N;
            return deck[i] ? RoadAt(i, x, y) : Core(x, y, false);
        }

        // the surface under a car heading along (dx, dy): inside a crossing the leg it drives along. Without bridges
        // the same as Sample.
        public double SampleAlong(double x, double y, double dx, double dy)
        {
            if (deck == null) return Core(x, y, true);
            int best = -1; double bd = double.MaxValue, need = 0.5 * Math.Sqrt(dx * dx + dy * dy);
            var pts = t.pts;
            for (int oy = -1; oy <= 1; oy++)
            {
                long ky = (long)Math.Floor((y + oy * Bucket) / Bucket) - by0;
                if (ky < 0 || ky >= bny) continue;
                for (int ox = -1; ox <= 1; ox++)
                {
                    long kx = (long)Math.Floor((x + ox * Bucket) / Bucket) - bx0;
                    if (kx < 0 || kx >= bnx) continue;
                    int b = (int)ky * bnx + (int)kx;
                    for (int q = bStart[b], qe = bStart[b + 1]; q < qe; q++)
                    {
                        int pi = bIdx[q]; var p = pts[pi];
                        if (Math.Abs(p.tx * dx + p.ty * dy) < need) continue;
                        double d = (p.x - x) * (p.x - x) + (p.y - y) * (p.y - y);
                        if (d < bd) { bd = d; best = pi; }
                    }
                }
            }
            if (best < 0) return Core(x, y, true);
            return deck[best] && bd <= DeckHalf * DeckHalf ? RoadAt(best, x, y) : Core(x, y, false);
        }

        // the road surface of track point best at (x, y): its height, interpolated towards the next point along, tilted
        // by the banking
        double RoadAt(int best, double x, double y)
        {
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
            return road;
        }

        double Core(double x, double y, bool top)
        {
            // nearest track point, from the 3 x 3 buckets around (covers everything within 16 m); the nearest that is
            // not on a deck as well
            int best = -1, bestG = -1; double bd = double.MaxValue, bdG = double.MaxValue;
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
                        if (d < bdG && (deck == null || !deck[pi])) { bdG = d; bestG = pi; }
                    }
                }
            }
            // on a bridge: its deck out to the barriers
            if (top && best >= 0 && best != bestG && bd <= DeckHalf * DeckHalf) return RoadAt(best, x, y);
            double blendEnd = Shoulder + 5;
            if (bestG < 0 || bdG >= blendEnd * blendEnd) return Terrain(x, y);
            double road = RoadAt(bestG, x, y);
            double dist = Math.Sqrt(bdG);
            if (dist <= Shoulder) return road;
            double u = (dist - Shoulder) / 5; u = u * u * (3 - 2 * u);
            return road * (1 - u) + Terrain(x, y) * u;
        }

        // lowest ground within r of a point: the open land must never rise above a road that passes close by
        // (the ground: a bridge deck is not land)
        public double Lowest(double x, double y, double r)
        {
            double lo = Ground(x, y);
            for (int k = 0; k < 8; k++) { double a = k * Math.PI / 4; lo = Math.Min(lo, Ground(x + Math.Cos(a) * r, y + Math.Sin(a) * r)); lo = Math.Min(lo, Ground(x + Math.Cos(a) * r * 0.5, y + Math.Sin(a) * r * 0.5)); }
            return lo;
        }

        // slope of the ground in the direction of travel (rise per metre; + = uphill)
        public double Grade(double x, double y, double heading)
        {
            double c = Math.Cos(heading) * 1.7, s = Math.Sin(heading) * 1.7;
            return (Sample(x + c, y + s) - Sample(x - c, y - s)) / 3.4;
        }
        // the same for a car: at a bridge, along the leg it drives on (without bridges exactly Grade)
        public double GradeAlong(double x, double y, double heading)
        {
            if (deck == null) return Grade(x, y, heading);
            double ch = Math.Cos(heading), sh = Math.Sin(heading), c = ch * 1.7, s = sh * 1.7;
            return (SampleAlong(x + c, y + s, ch, sh) - SampleAlong(x - c, y - s, ch, sh)) / 3.4;
        }
    }
}
