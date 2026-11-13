// Circuit.cs — the data layer of the game world. Pure C# (no UnityEngine): the game, the AI and the offline
// trainer all read the same data.
//
//  * SurfaceTable: every surface a tyre can touch (asphalt, painted lines, kerbs, astroturf, gravel, grass...)
//    with its grip, rolling drag and bumpiness.
//  * CompoundTable: the tyre compounds (Soft / Medium / Hard).
//  * CircuitDef + Circuits.All: one entry per circuit (shape, width, start line, heights, style, run-off).
//    Adding a circuit means adding one entry here; the world, physics and AI are built from it.
//  * WheelContact: works out which surface each of the four tyres is on, every physics tick.
using System;

namespace FormulaSim
{
    // ------------------------------------------------------------------ surfaces
    public enum SurfaceType : byte { Asphalt, Paint, Kerb, SausageKerb, Astroturf, Gravel, Grass, Concrete, RunoffAsphalt }

    public struct SurfaceDef
    {
        public string name;
        public double mu;    // peak grip relative to clean, dry track asphalt
        public double roll;  // extra rolling resistance, as a share of the weight on that tyre
        public double bump;  // m, height of the surface's bumps (kerb ridges, ruts) — shakes the car, costs grip
    }

    public static class SurfaceTable
    {
        // Grip is relative to dry track asphalt. Measured road values for passenger tyres (BASt, TRL and
        // road-marking studies): dry asphalt and concrete peak friction around 0.7-1.0, cobbles 0.53-0.85,
        // painted markings and kerb paint noticeably lower, grass and gravel far lower. Racing slicks keep the
        // same ratios roughly; the absolute grip level comes from CarConfig.mu.
        public static readonly SurfaceDef[] Defs =
        {
            new SurfaceDef { name = "Asphalt",        mu = 1.00, roll = 0.000, bump = 0.000 },
            new SurfaceDef { name = "Painted line",   mu = 0.88, roll = 0.000, bump = 0.000 },
            new SurfaceDef { name = "Kerb",           mu = 0.90, roll = 0.004, bump = 0.008 },
            new SurfaceDef { name = "Sausage kerb",   mu = 0.70, roll = 0.030, bump = 0.045 },
            new SurfaceDef { name = "Astroturf",      mu = 0.62, roll = 0.020, bump = 0.003 },
            new SurfaceDef { name = "Gravel",         mu = 0.45, roll = 0.550, bump = 0.012 }, // the car sinks in: ~0.5 g of drag
            new SurfaceDef { name = "Grass",          mu = 0.38, roll = 0.060, bump = 0.006 },
            new SurfaceDef { name = "Concrete",       mu = 0.85, roll = 0.002, bump = 0.002 },
            new SurfaceDef { name = "Run-off asphalt",mu = 0.93, roll = 0.000, bump = 0.001 },
        };
        public static SurfaceDef Get(SurfaceType t) => Defs[(int)t];
        public static bool OnTrack(SurfaceType t) => t == SurfaceType.Asphalt || t == SurfaceType.Paint;
    }

    // ------------------------------------------------------------------ tyre compounds
    public enum Compound { Soft, Medium, Hard }

    public struct CompoundDef
    {
        public string name;
        public double grip;    // peak grip relative to the soft
        public double opt;     // deg C where the compound works best
        public double window;  // deg C away from opt where grip has dropped by CarConfig.tyreLoss
        public double life;    // relative tread life (soft = 1)
    }

    public static class CompoundTable
    {
        // Softer compounds grip more and warm up sooner but wear faster; harder ones need more heat.
        // (Pirelli quotes softer compounds at roughly 0.5-1 s a lap quicker per step.)
        public static readonly CompoundDef[] Defs =
        {
            new CompoundDef { name = "Soft",   grip = 1.000, opt = 95,  window = 28, life = 1.0 },
            new CompoundDef { name = "Medium", grip = 0.986, opt = 105, window = 30, life = 1.7 },
            new CompoundDef { name = "Hard",   grip = 0.972, opt = 115, window = 32, life = 2.6 },
        };
        public static CompoundDef Get(Compound c) => Defs[(int)c];
    }

    // ------------------------------------------------------------------ circuits
    public enum CircuitStyle { Test, Street, Parkland }

    public class CircuitDef
    {
        public string id, name;
        public double[,] lonlat;         // centreline (lon, lat); null = the built-in test track
        public double[,] metric;         // or: centreline in metres with widths (TUM format: x, y, w_right, w_left)
        public double width = 12;        // m
        public double startAt;           // m along the raw data where the real start/finish line is
        public double curveSigma;        // m, curvature spreading for a coarse outline (see Track.SmoothedCurvature)
        public double[,] curveFit;       // per corner {distance from start (m), curvature window (m)}, fitted offline
        public double[,] realCorners;    // per corner {distance from start (m), real minimum speed (km/h)}; the fit target
        public double[,] elev;           // {distance from the start line, height}; null = flat
        public double[,] bank;           // banked corners {from s, to s, degrees} (see Track.SetBanking); null = none
        public CircuitStyle style = CircuitStyle.Parkland;
        public double barrier = 9.3;     // m from the track edge to the barriers
        public double realPole;          // s, real pole lap for comparison (0 = none)
        // water: the track from waterFrom to waterTo (m from the start line) runs along a quay; the water lies on the
        // side towards (waterX, waterY) (track coordinates) and stretches out to sea
        public double waterFrom, waterTo, waterX, waterY;
        // tunnel: covered from tunnelFrom to tunnelTo (m from the start line)
        public double tunnelFrom, tunnelTo;
        public double forest = 0.3;      // 0 = open land, 1 = dense forest round the circuit (Spa)

        public Track Build()
        {
            var t = metric != null ? Track.LocalFit(Track.Build(TrackDataTum.Centre(metric), TrackDataTum.MeanWidth(metric)), curveFit)
                  : lonlat == null ? Track.Build(null, width) : Track.FromLonLat(lonlat, width, startAt, elev, curveSigma, curveFit);
            if (metric != null && elev != null) t.SetElevation(elev);
            if (metric != null && Track.VariableWidth) t.halfW = TrackDataTum.HalfWidths(metric, t);
            if (bank != null) t.SetBanking(bank);
            t.def = this;
            Layout(t);
            return t;
        }

        // kerbs and run-off for each side of each track point
        void Layout(Track t)
        {
            int N = t.N;
            t.kerbL = new float[N]; t.kerbR = new float[N];
            t.runL = new SurfaceType[N]; t.runR = new SurfaceType[N];
            var baseRun = style == CircuitStyle.Street ? SurfaceType.Concrete : SurfaceType.Grass;
            var curvy = new bool[N];
            for (int i = 0; i < N; i++) curvy[i] = Math.Abs(t.pts[i].curv) > 1.0 / 280;
            for (int i = 0; i < N; i++)
            {
                bool on = false;
                for (int j = -8; j <= 8 && !on; j++) on = curvy[(i + j + N) % N];
                t.kerbL[i] = t.kerbR[i] = on ? (float)Track.KerbWidth : 0f;
                t.runL[i] = t.runR[i] = baseRun;
            }
            if (style != CircuitStyle.Parkland) return;
            // gravel traps on the outside of slower corners, from a little before the apex to well after it
            int before = (int)Math.Round(20 / t.ds), after = (int)Math.Round(90 / t.ds);
            for (int i = 0; i < N; i++)
            {
                double c = t.pts[i].curv;
                if (Math.Abs(c) < 1.0 / 160) continue;
                var arr = c > 0 ? t.runR : t.runL; // left-hand corner: the outside is on the right
                for (int k = -before; k <= after; k++) arr[((i + k) % N + N) % N] = SurfaceType.Gravel;
            }
        }
    }

    public static class Circuits
    {
        public static readonly CircuitDef Test = new CircuitDef { id = "test", name = "Test track", width = 14, style = CircuitStyle.Parkland };
        public static readonly CircuitDef Monaco = new CircuitDef
        {
            id = "monaco", name = "Monaco", lonlat = TrackData.Monaco, width = TrackData.MonacoWidth, startAt = TrackData.MonacoStart,
            elev = TrackData.MonacoElev, style = CircuitStyle.Street, barrier = 2.65, realPole = 72.051, forest = 0.1,
            waterFrom = 1540, waterTo = 2890, waterX = 400, waterY = -100,   // Port Hercule and the sea
            tunnelFrom = 1560, tunnelTo = 1880,                               // the tunnel under the hotel
            // real minimum corner speeds (km/h) from published corner guides (F1 Chronicle, Honda, circuit guides);
            // the outline is too coarse (a point every 20 m) to give the true bend shapes, so each bend's curvature
            // spread is fitted offline (dotnet run -- fitgeo) until the ideal lap matches these speeds
            realCorners = new double[,] { { 168, 115 }, { 770, 150 }, { 862, 130 }, { 1084, 80 }, { 1216, 47 }, { 1310, 80 }, { 1398, 85 },
                                          { 2056, 75 }, { 2338, 160 }, { 2492, 205 }, { 2688, 105 }, { 2886, 70 }, { 2974, 80 } },
            curveSigma = 4,
            curveFit = new double[,] { { 168, 14.5 }, { 770, 10.6 }, { 862, 18.7 }, { 1084, 14.5 }, { 1216, 0.1 }, { 1310, 10.4 }, { 1398, 12.7 }, { 2056, 4.6 }, { 2338, 13.2 }, { 2492, 12.5 }, { 2688, 8.2 }, { 2886, 11.9 }, { 2974, 2.7 } }, /*CURVEFIT:monaco*/
        };
        public static readonly CircuitDef Spa = new CircuitDef
        {
            id = "spa", name = "Spa", metric = TrackDataTum.Spa, style = CircuitStyle.Parkland, realPole = 104.361, forest = 1,
            // Height profile: measured (TrackElev). 102 m between the highest point (just before Les Combes) and the
            // lowest (after Stavelot, before Paul Frere); La Source 56 m, Eau Rouge 24 m, top of Raidillon 47 m, the
            // steepest stretch 14 % up Raidillon; Les Combes to Stavelot is all downhill (up to 8 %).
            elev = TrackElev.Spa,
            realCorners = new double[,] { { 404, 65, 60 }, { 2432, 140, 35 }, { 2508, 150, 35 }, { 2662, 175, 60 }, { 3300, 155, 60 }, { 3838, 255, 80 },
                                          { 4518, 185, 60 }, { 4664, 170, 60 }, { 4959, 165, 60 }, { 6753, 72, 40 } },
            curveFit = new double[,] { { 404, 7.0, 60 }, { 2432, 10.8, 35 }, { 2508, 19.2, 35 }, { 2662, 24.4, 60 }, { 3300, 19.2, 60 }, { 3838, 30.9, 80 }, { 4518, 32.8, 60 }, { 4664, 23.0, 60 }, { 4959, 27.2, 60 }, { 6753, 10.8, 40 } }, /*CURVEFIT:spa*/
        };
        // more circuits from the TUM racetrack database (OpenStreetMap centre lines, measured widths).
        // realPole: 2026 F1 qualifying pole (dry Q3; checked against the timing archive). elev: measured height
        // profiles (TrackElev in TrackDataTum.cs: the F1 timing feed's car heights on the 2026 pole laps); the corner
        // distances and heights in the comments below are the official turn positions laid on these centre lines.
        // A new piece of track: rows from+1 .. to-1 of a TUM centre line are replaced by a smooth cubic (Hermite) curve
        // that leaves row from and joins row to along the old directions; a row about every 5 m, widths blended.
        static double[,] Bypass(double[,] d, int from, int to)
        {
            int n = d.GetLength(0);
            double Dir(int i, int k) { double dx = d[i + 1, 0] - d[i - 1, 0], dy = d[i + 1, 1] - d[i - 1, 1], l = Math.Sqrt(dx * dx + dy * dy); return (k == 0 ? dx : dy) / l; }
            double x0 = d[from, 0], y0 = d[from, 1], x1 = d[to, 0], y1 = d[to, 1];
            double chord = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            double t0x = Dir(from, 0) * chord, t0y = Dir(from, 1) * chord, t1x = Dir(to, 0) * chord, t1y = Dir(to, 1) * chord;
            (double x, double y) H(double u)
            {
                double h00 = 2 * u * u * u - 3 * u * u + 1, h10 = u * u * u - 2 * u * u + u, h01 = -2 * u * u * u + 3 * u * u, h11 = u * u * u - u * u;
                return (h00 * x0 + h10 * t0x + h01 * x1 + h11 * t1x, h00 * y0 + h10 * t0y + h01 * y1 + h11 * t1y);
            }
            const int M = 2000; var cum = new double[M + 1]; var prev = H(0);
            for (int k = 1; k <= M; k++) { var q = H((double)k / M); cum[k] = cum[k - 1] + Math.Sqrt((q.x - prev.x) * (q.x - prev.x) + (q.y - prev.y) * (q.y - prev.y)); prev = q; }
            int m = Math.Max(2, (int)Math.Round(cum[M] / 5));
            var rows = new System.Collections.Generic.List<double[]>();
            for (int i = 0; i <= from; i++) rows.Add(new[] { d[i, 0], d[i, 1], d[i, 2], d[i, 3] });
            for (int j = 1, k = 0; j < m; j++)
            {
                double target = cum[M] * j / m; while (cum[k + 1] < target) k++;
                var q = H((k + (target - cum[k]) / Math.Max(1e-9, cum[k + 1] - cum[k])) / M); double w = (double)j / m;
                rows.Add(new[] { q.x, q.y, d[from, 2] + (d[to, 2] - d[from, 2]) * w, d[from, 3] + (d[to, 3] - d[from, 3]) * w });
            }
            for (int i = to; i < n; i++) rows.Add(new[] { d[i, 0], d[i, 1], d[i, 2], d[i, 3] });
            var r = new double[rows.Count, 4];
            for (int i = 0; i < rows.Count; i++) for (int c = 0; c < 4; c++) r[i, c] = rows[i][c];
            return r;
        }
        // A corrected piece of track: rows from+1 .. to-1 of a TUM centre line are replaced by a smooth curve (centripetal
        // Catmull-Rom spline) through the given points (x, y in the centre line's own coordinates), leaving row from
        // and joining row to along the old directions; a row about every 5 m, widths blended.
        static double[,] Reroute(double[,] d, int from, int to, double[,] via)
        {
            int n = d.GetLength(0), nv = via.GetLength(0);
            var c = new System.Collections.Generic.List<(double x, double y)> { (d[from - 3, 0], d[from - 3, 1]), (d[from, 0], d[from, 1]) };
            for (int i = 0; i < nv; i++) c.Add((via[i, 0], via[i, 1]));
            c.Add((d[to, 0], d[to, 1])); c.Add((d[to + 3, 0], d[to + 3, 1]));
            var fine = new System.Collections.Generic.List<(double x, double y)>();
            for (int k = 1; k + 2 < c.Count; k++)
            {
                var (p0, p1, p2, p3) = (c[k - 1], c[k], c[k + 1], c[k + 2]);
                double Kn((double x, double y) a, (double x, double y) b) => Math.Sqrt(Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y)));
                double t0 = 0, t1 = t0 + Kn(p0, p1), t2 = t1 + Kn(p1, p2), t3 = t2 + Kn(p2, p3);
                for (int j = 0; j < 200; j++)
                {
                    double t = t1 + (t2 - t1) * j / 200;
                    (double x, double y) L((double x, double y) a, (double x, double y) b, double ta, double tb) => (a.x + (b.x - a.x) * (t - ta) / (tb - ta), a.y + (b.y - a.y) * (t - ta) / (tb - ta));
                    var a1 = L(p0, p1, t0, t1); var a2 = L(p1, p2, t1, t2); var a3 = L(p2, p3, t2, t3);
                    var b1 = L(a1, a2, t0, t2); var b2 = L(a2, a3, t1, t3);
                    fine.Add(L(b1, b2, t1, t2));
                }
            }
            fine.Add((d[to, 0], d[to, 1]));
            int M = fine.Count - 1; var cum = new double[M + 1];
            for (int k = 1; k <= M; k++) cum[k] = cum[k - 1] + Math.Sqrt((fine[k].x - fine[k - 1].x) * (fine[k].x - fine[k - 1].x) + (fine[k].y - fine[k - 1].y) * (fine[k].y - fine[k - 1].y));
            int m = Math.Max(2, (int)Math.Round(cum[M] / 5));
            var rows = new System.Collections.Generic.List<double[]>();
            for (int i = 0; i <= from; i++) rows.Add(new[] { d[i, 0], d[i, 1], d[i, 2], d[i, 3] });
            for (int j = 1, k = 0; j < m; j++)
            {
                double target = cum[M] * j / m; while (cum[k + 1] < target) k++;
                double u = (target - cum[k]) / Math.Max(1e-9, cum[k + 1] - cum[k]), w = (double)j / m;
                rows.Add(new[] { fine[k].x + (fine[k + 1].x - fine[k].x) * u, fine[k].y + (fine[k + 1].y - fine[k].y) * u, d[from, 2] + (d[to, 2] - d[from, 2]) * w, d[from, 3] + (d[to, 3] - d[from, 3]) * w });
            }
            for (int i = to; i < n; i++) rows.Add(new[] { d[i, 0], d[i, 1], d[i, 2], d[i, 3] });
            var r = new double[rows.Count, 4];
            for (int i = 0; i < rows.Count; i++) for (int q = 0; q < 4; q++) r[i, q] = rows[i][q];
            return r;
        }
        // The same centre line begun k rows later (moves the start/finish line k x 5 m along the lap).
        static double[,] StartAtRow(double[,] d, int k)
        {
            int n = d.GetLength(0); var r = new double[n, 4];
            for (int i = 0; i < n; i++) for (int q = 0; q < 4; q++) r[i, q] = d[(i + k) % n, q];
            return r;
        }
        // The same centre line with its x and y multiplied by f (widths unchanged): brings the lap length to the official one.
        static double[,] Scaled(double[,] d, double f)
        {
            int n = d.GetLength(0); var r = new double[n, 4];
            for (int i = 0; i < n; i++) { r[i, 0] = d[i, 0] * f; r[i, 1] = d[i, 1] * f; r[i, 2] = d[i, 2]; r[i, 3] = d[i, 3]; }
            return r;
        }
        // Corner shapes: the TUM centre lines make many bends far tighter than the real road (Copse 193 km/h on the
        // ideal lap, Loop 47). Each circuit lists real minimum speeds for its main corners (realCorners: ESTIMATES
        // for a 2026 car from 2024-25 pole-lap speed traces and corner guides, rounded to 5 km/h, not measured), and
        // dotnet run -- fitgeo <id> [max move m] fits each bend's curvature window (Track.LocalFit) to them, with no
        // bend allowed to move more than 6 m from the measured centre line.
        // real: realCorners {s, km/h, reach m}; fit: the fitted curveFit {s, window m, reach m} (dotnet run -- fitgeo <id>)
        static CircuitDef Tum(string id, string name, double[,] d, double forest, double pole = 0, double[,] elev = null, double[,] real = null, double[,] fit = null, double[,] bank = null) =>
            new CircuitDef { id = id, name = name, metric = d, style = CircuitStyle.Parkland, forest = forest, realPole = pole, elev = elev, realCorners = real, curveFit = fit, bank = bank };
        // Monza (royal park): pole 1:21.786 Gasly 2026. 12.4 m range: a steady gentle climb from Parabolica (the lowest
        // point) up the start straight and through Curva Grande to the highest point before Roggia, down through the
        // Lesmos to the dip at the Serraglio underpass (2.4 % down, 1.6 % up), then gently down from Ascari to Parabolica.
        // Corners: Rettifilo 925-988, Curva Grande 1506, Roggia 2156-2210, Lesmo 2570 / 2888, Ascari 3959-4155, Parabolica 5244 m.
        public static readonly CircuitDef Monza = Tum("monza", "Monza", TrackDataTum.Monza, 0.8, 81.786,
            TrackElev.Monza,
            new double[,] { { 932, 72, 60 }, { 2146, 110, 60 }, { 2544, 195, 80 }, { 2882, 175, 80 }, { 3952, 200, 60 }, { 4142, 210, 90 }, { 5200, 200, 120 } },
            new double[,] { { 932, 7.5, 60 }, { 2146, 8.9, 60 }, { 2544, 34.7, 80 }, { 2882, 21.1, 80 }, { 3952, 22.0, 60 }, { 4142, 9.8, 90 }, { 5200, 39.8, 120 } } /*CURVEFIT:monza*/);
        // Silverstone: pole 1:28.111 Antonelli 2026. 11.2 m range: lowest on the start straight just after Club, rising
        // to the Village/Loop/Aintree plateau, a shallow dip at Brooklands/Woodcote, highest before Maggotts; down from
        // Chapel along Hangar Straight (with a 2 % rise into Stowe), then the main drop (3.5 %) out of Stowe to Vale.
        // Corners: Abbey 459, Village 876, Loop 1045, Brooklands 1978, Luffield 2204, Copse 3091, Maggotts/Becketts
        // 3648-4034, Stowe 5046, Vale 5499, Club 5586-5738 m.
        public static readonly CircuitDef Silverstone = Tum("silverstone", "Silverstone", TrackDataTum.Silverstone, 0.15, 88.111,
            TrackElev.Silverstone,
            new double[,] { { 396, 280, 80 }, { 892, 120, 60 }, { 1042, 75, 60 }, { 1248, 205, 60 }, { 1998, 125, 90 }, { 2182, 115, 60 },
                            { 3138, 280, 80 }, { 3722, 280, 60 }, { 3888, 230, 60 }, { 4020, 220, 60 }, { 5079, 240, 80 }, { 5513, 115, 40 }, { 5595, 130, 40 } },
            new double[,] { { 396, 28.6, 80 }, { 892, 21.6, 60 }, { 1042, 18.8, 60 }, { 1248, 27.7, 60 }, { 1998, 21.1, 90 }, { 2182, 2.3, 60 }, { 3138, 14.5, 80 }, { 3722, 26.2, 60 }, { 3888, 0.0, 60 }, { 4020, 32.8, 60 }, { 5079, 37.5, 80 }, { 5513, 19.2, 40 }, { 5595, 20.2, 40 } } /*CURVEFIT:silverstone*/);
        // Suzuka: pole 1:28.778 Antonelli 2026. 40.1 m range: the start straight runs downhill to the lowest point
        // just after T1; the Esses and Dunlop climb 30 m (up to 7 %), a dip through the Degners (under the bridge), up
        // to the hairpin and on to the highest point at Spoon; 7.5 % down out of Spoon, level across the bridge (the
        // upper leg at 4910 m is 6.4 m above the lower leg at 2550 m), up to the chicane and down again to the line.
        // Corners: T1 728, T2 845, Esses 1092-1539, Dunlop 1859, Degner 2275 / 2430, hairpin 2905, Spoon 3802-3961,
        // 130R 5005, chicane 5402-5476 m.
        public static readonly CircuitDef Suzuka = Tum("suzuka", "Suzuka", TrackDataTum.Suzuka, 0.5, 88.778,
            TrackElev.Suzuka,
            new double[,] { { 874, 150, 60 }, { 1116, 215, 50 }, { 1218, 190, 50 }, { 1398, 205, 60 }, { 1598, 195, 60 }, { 1738, 220, 60 }, { 2300, 215, 60 }, { 2458, 140, 60 }, { 3800, 205, 50 }, { 3952, 175, 60 } },
            new double[,] { { 874, 29.5, 60 }, { 1116, 14.1, 50 }, { 1218, 31.4, 50 }, { 1398, 24.4, 60 }, { 1598, 34.2, 60 }, { 1738, 34.7, 60 }, { 2300, 10.8, 60 }, { 2458, 20.2, 60 }, { 3800, 19.2, 50 }, { 3952, 30.5, 60 } } /*CURVEFIT:suzuka*/);
        // Austin (COTA): pole 1:34.035 Antonelli 2026. 29.5 m range (measured 30.3 m; formula1.com 30.9 m): lowest
        // point between T19 and T20, a gentle rise along the start straight, then the climb to the T1 crest (26 m in
        // 250 m, 12.4 % at the steepest) and straight back down (11 %) to T2; a second rise to T9 (23 m), down to the
        // T11 hairpin and along the back straight to the low stadium section.
        // Corners: T1 663, Esses 1180-1505, T9 1986, T10 2198, T11 2586, T12 3790, T15 4306, T19 5055, T20 5375 m.
        public static readonly CircuitDef Austin = Tum("austin", "Austin", TrackDataTum.Austin, 0.1, 94.035,
            TrackElev.Austin,
            new double[,] { { 666, 62, 60 }, { 1270, 235, 45 }, { 1362, 205, 45 }, { 1586, 215, 60 }, { 1724, 150, 60 }, { 1916, 165, 30 }, { 1978, 150, 30 }, { 2186, 250, 60 }, { 2594, 62, 60 }, { 3782, 72, 60 }, { 4018, 90, 45 }, { 4110, 125, 45 }, { 4296, 60, 60 }, { 5049, 150, 60 }, { 5363, 80, 60 } },
            new double[,] { { 666, 14.1, 60 }, { 1270, 4.2, 45 }, { 1362, 22.0, 45 }, { 1586, 0.0, 60 }, { 1724, 19.2, 60 }, { 1916, 30.9, 30 }, { 1978, 9.8, 30 }, { 2186, 29.5, 60 }, { 2594, 13.1, 60 }, { 3782, 15.0, 60 }, { 4018, 21.1, 45 }, { 4110, 7.5, 45 }, { 4296, 11.7, 60 }, { 5049, 8.9, 60 }, { 5363, 15.9, 60 } } /*CURVEFIT:austin*/);
        // Hungaroring: pole 1:17.207 Norris 2026. 34.7 m range: highest on the start straight, downhill through T1
        // and T2 (7 % at the steepest, after T2) to the lowest point 150 m after T3, then a steady climb (5.7 % into T4)
        // back up through the middle sector, a dip between T11 and T13, and up out of T14.
        // Corners: T1 655, T2 1160, T3 1345, T4 1826, T5 2065, T6/7 2408-2453, T8/9 2618-2758, T11 3150, T12 3547, T13 3813, T14 4104 m.
        public static readonly CircuitDef Budapest = Tum("budapest", "Hungaroring", TrackDataTum.Budapest, 0.3, 77.207,
            TrackElev.Budapest,
            new double[,] { { 642, 85, 60 }, { 1344, 155, 60 }, { 1820, 185, 60 }, { 2042, 160, 60 }, { 2386, 95, 60 }, { 2622, 160, 50 }, { 2730, 135, 50 }, { 3106, 205, 60 }, { 3536, 100, 60 }, { 3822, 105, 60 }, { 4090, 155, 60 } },
            new double[,] { { 642, 20.6, 60 }, { 1344, 6.6, 60 }, { 1820, 17.3, 60 }, { 2042, 30.5, 60 }, { 2386, 6.1, 60 }, { 2622, 19.7, 50 }, { 2730, 23.4, 50 }, { 3106, 32.8, 60 }, { 3536, 11.2, 60 }, { 3822, 27.7, 60 }, { 4090, 35.6, 60 } } /*CURVEFIT:budapest*/);
        // Barcelona: pole 1:14.679 Russell 2026. 29.5 m range: lowest point at T1, up through T3 to T4 (21 m), down to
        // T6 (6 % in the T5 braking zone), the 6 % climb from T7 over the Campsa crest (T9), a dip at T10, and up to
        // the highest point 120 m after T12, then downhill all the way to T1.
        // Layout: the TUM centre line is the 2007-2020 one. The 2023+ layout (the 2026 pole) differs in two places,
        // both rebuilt here: (1) T10 (La Caixa), since 2021 a wider, earlier left-hander: the real racing line ran up to
        // 25 m outside the old hairpin; rerouted through points of the present OpenStreetMap centre line (the f1-circuits
        // dataset), which the 2026 pole laps follow within 3 m. (2) T13-T14: one long right-hander instead of the
        // T13 / chicane / T16 (Bypass). Lap length 4654 m (official 4657 m).
        // Corners: T1 844, T3 1185, T4 1762, T5 2195, T7 2551, T9 2910, T10 3510, T12 3764, T13 4043, T14 4359 m.
        static readonly double[,] CatalunyaT10 = { { 0.4, 212.8 }, { 1.5, 222.6 }, { -1.5, 233.5 }, { -6.5, 242.2 }, { -13.8, 249.4 }, { -24.3, 257.6 }, { -41.5, 265.3 }, { -52.1, 268.0 },
                                                   { -66.8, 270.1 }, { -79.4, 270.8 }, { -92.2, 270.1 }, { -106.1, 266.7 }, { -125.5, 257.5 }, { -138.8, 250.0 } };
        public static readonly CircuitDef Catalunya = Tum("catalunya", "Barcelona", Reroute(Bypass(TrackDataTum.Catalunya, 798, 849), 688, 738, CatalunyaT10), 0.2, 74.679,
            TrackElev.Catalunya,
            new double[,] { { 838, 125, 50 }, { 948, 165, 50 }, { 1168, 240, 80 }, { 1714, 160, 60 }, { 2146, 105, 60 }, { 2556, 150, 60 }, { 2902, 230, 60 }, { 3510, 110, 60 }, { 3759, 150, 60 } },
            new double[,] { { 838, 14.5, 50 }, { 948, 19.7, 50 }, { 1168, 39.8, 80 }, { 1714, 29.5, 60 }, { 2146, 28.1, 60 }, { 2556, 24.4, 60 }, { 2902, 36.1, 60 }, { 3510, 21.6, 60 }, { 3759, 32.8, 60 } } /*CURVEFIT:catalunya*/);
        // Red Bull Ring: pole 1:06.113 Russell 2026. 63.2 m range (circuit: 65 m, max incline 12 %, decline 9.3 %,
        // redbullring.com): lowest point on the start-finish straight, 15 m up by T1, 30 m by the T2 kink, the steepest
        // climb (11.7 %) into Remus (T3), highest point 80 m after it; down to T4 (35 m), level to T6, 8.8 % down after
        // T6 to T7, a small rise to T9, then down through T10 to the line.
        // Corners: T1 463, T3 1402, T4 2225, T6 2737, T7 3051, T9 3790, T10 3994 m.
        public static readonly CircuitDef Spielberg = Tum("spielberg", "Red Bull Ring", TrackDataTum.Spielberg, 0.5, 66.113,
            TrackElev.Spielberg,
            new double[,] { { 448, 95, 60 }, { 1396, 70, 60 }, { 2208, 110, 60 }, { 2738, 185, 80 }, { 3034, 190, 90 }, { 3786, 225, 60 }, { 3986, 140, 80 } },
            new double[,] { { 448, 7.5, 60 }, { 1396, 15.9, 60 }, { 2208, 21.6, 60 }, { 2738, 37.5, 80 }, { 3034, 36.1, 90 }, { 3786, 30.9, 60 }, { 3986, 15.0, 80 } } /*CURVEFIT:spielberg*/);
        // Zandvoort: pole 1:11.163 Norris 2026. 8.6 m range (measured 9.0 m; published 8.9 m): Tarzan and Hugenholtz
        // low, a 6 % climb out of Hugenholtz to the Hunserug (T4), a dip at T5, the highest point between T6 and
        // Scheivlak, low through the back section, and a 6 m hump before the last corner.
        // Banked: Hugenholtz T3 and Arie Luyendyk T14, up to 19 and 18 deg at the outer edge (progressive): the racing
        // line runs on the lower part, so about half of that is used here.
        // Layout: the TUM centre line is the pre-2020 circuit (4307 m; its Hugenholtz hairpin reached 13 m further out
        // than today's, and the 2026 racing line ran 12 m off it). Rebuilt here: Hugenholtz rerouted through points of
        // the present OpenStreetMap centre line (the f1-circuits dataset; the 2026 pole laps follow it within 5 m), the
        // start line moved 40 m on to the real timing line (it sat 57 m early), and the remaining 0.7 % of excess
        // length scaled out: 4259 m, the official length.
        static readonly double[,] ZandvoortT3 = { { 33.8, -15.5 }, { 30.6, -30.0 }, { 30.4, -43.0 }, { 36.0, -56.1 }, { 46.0, -65.4 }, { 58.3, -70.1 } };
        public static readonly CircuitDef Zandvoort = Tum("zandvoort", "Zandvoort", Scaled(StartAtRow(Reroute(TrackDataTum.Zandvoort, 166, 196, ZandvoortT3), 8), 0.9930), 0.2, 71.163, elev: TrackElev.Zandvoort,
            real: new double[,] { { 377, 115, 60 }, { 697, 160, 60 }, { 845, 130, 50 }, { 1684, 235, 80 }, { 2022, 160, 60 }, { 2265, 105, 60 }, { 2533, 135, 60 }, { 3091, 115, 30 }, { 3185, 95, 30 }, { 3520, 165, 60 } },
            fit: new double[,] { { 377, 39.4, 60 }, { 697, 25.8, 60 }, { 845, 32.8, 50 }, { 1684, 39.8, 80 }, { 2022, 11.2, 60 }, { 2265, 20.6, 60 }, { 2533, 30.5, 60 }, { 3091, 16.9, 30 }, { 3185, 0.0, 30 }, { 3520, 29.1, 60 } } /*CURVEFIT:zandvoort*/,
            bank: new double[,] { { 795, 905, 9.5 }, { 3585, 3905, 9 } });
        // order matters: the AI tuning table (AiTuning.Trained) uses the same order
        public static readonly CircuitDef[] All = { Test, Monaco, Spa, Monza, Silverstone, Suzuka, Austin, Budapest, Catalunya, Spielberg, Zandvoort };
    }

    // ------------------------------------------------------------------ tyre contact
    public static class WheelContact
    {
        // Set the surface under each tyre (0 FL, 1 FR, 2 RL, 3 RR) from the car's nearest track point and
        // lateral offset. rubber: 0..1 how rubbered-in the circuit is (grip on the asphalt rises with it).
        // marbles: 0..1 how much worn-off rubber lies off the line (builds up through a race as the tyres wear);
        // off the line it adds to the "dirty side" grip loss, up to 4 % + 10 % = 14 % with marbles at 1 (est.).
        public static void Update(Track t, RacingLine line, FormulaCar car, int idx, double lat, double rubber, double marbles = 0)
        {
            if (idx < 0 || t.kerbL == null) return;
            var c = car.cfg; var p = t.pts[idx];
            // banking under the car, as the car sees it (driving the wrong way round, the road leans the other way)
            car.bank = t.bank == null ? 0 : t.bank[idx] * (Math.Cos(car.heading) * p.tx + Math.Sin(car.heading) * p.ty >= 0 ? 1 : -1);
            double bw = c.wheelbase * c.frontWeight, aw = c.wheelbase - bw; // CG to rear, CG to front
            double ch = Math.Cos(car.heading), sh = Math.Sin(car.heading);
            for (int w = 0; w < 4; w++)
            {
                double along = w < 2 ? aw : -bw, side = (w % 2 == 0 ? 1 : -1) * c.track / 2; // + side = left
                double dx = ch * along - sh * side, dy = sh * along + ch * side;
                double wl = lat + dx * p.nx + dy * p.ny;
                int wi = ((idx + (int)Math.Round((dx * p.tx + dy * p.ty) / t.ds)) % t.N + t.N) % t.N;
                var type = t.SurfaceAt(wi, wl);
                var def = SurfaceTable.Get(type);
                double mu = def.mu;
                if (SurfaceTable.OnTrack(type))
                {
                    // the rubbered-in racing line grips best; the session's rubber build-up lifts the whole track
                    mu *= 0.975 + 0.025 * rubber;
                    if (line != null) { double off = Math.Abs(wl - line.off[wi]), u = Math.Min(1, Math.Max(0, (off - 1.5) / 2.5)); mu *= 1 - (0.04 + 0.10 * marbles) * u * u * (3 - 2 * u); }
                }
                car.SetWheelSurface(w, type, mu, def.roll, def.bump);
            }
        }
    }
}
