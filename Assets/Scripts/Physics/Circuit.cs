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
            // Height profile (m, relative): ESTIMATE from published facts, not surveyed: 102 m between the highest
            // point (Les Combes) and the lowest (Stavelot), Eau Rouge to Raidillon climbs ~35 m at up to 17-18 %.
            elev = new double[,] { { 0, 52 }, { 400, 50 }, { 700, 30 }, { 950, 12 }, { 1150, 40 }, { 1300, 50 }, { 2000, 88 },
                                   { 2350, 100 }, { 2700, 82 }, { 3150, 64 }, { 3800, 48 }, { 4500, 30 }, { 4950, 2 }, { 5500, 12 },
                                   { 6100, 30 }, { 6700, 46 } },
            realCorners = new double[,] { { 404, 65, 60 }, { 2432, 140, 35 }, { 2508, 150, 35 }, { 2662, 175, 60 }, { 3300, 155, 60 }, { 3838, 255, 80 },
                                          { 4518, 185, 60 }, { 4664, 170, 60 }, { 4959, 165, 60 }, { 6753, 72, 40 } },
            curveFit = new double[,] { { 404, 7.0, 60 }, { 2432, 10.8, 35 }, { 2508, 19.2, 35 }, { 2662, 24.4, 60 }, { 3300, 19.2, 60 }, { 3838, 30.9, 80 }, { 4518, 32.8, 60 }, { 4664, 23.0, 60 }, { 4959, 27.2, 60 }, { 6753, 10.8, 40 } }, /*CURVEFIT:spa*/
        };
        // more circuits from the TUM racetrack database (OpenStreetMap centre lines, measured widths).
        // realPole: 2026 F1 qualifying pole (dry Q3). elev: height profiles are ESTIMATES, not surveyed: total
        // height range and the position of the high/low points come from published figures (formula1.com "Highs and
        // lows - which F1 track has the most elevation changes", 2016, unless noted); the corner distances come from
        // the ideal-line speed minima on these centre lines; everything in between is interpolated by eye.
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
        // Corner shapes: the TUM centre lines make many bends far tighter than the real road (Copse 193 km/h on the
        // ideal lap, Loop 47). Each circuit lists real minimum speeds for its main corners (realCorners: ESTIMATES
        // for a 2026 car from 2024-25 pole-lap speed traces and corner guides, rounded to 5 km/h, not measured), and
        // dotnet run -- fitgeo <id> [max move m] fits each bend's curvature window (Track.LocalFit) to them, with no
        // bend allowed to move more than 6 m from the measured centre line.
        // real: realCorners {s, km/h, reach m}; fit: the fitted curveFit {s, window m, reach m} (dotnet run -- fitgeo <id>)
        static CircuitDef Tum(string id, string name, double[,] d, double forest, double pole = 0, double[,] elev = null, double[,] real = null, double[,] fit = null, double[,] bank = null) =>
            new CircuitDef { id = id, name = name, metric = d, style = CircuitStyle.Parkland, forest = forest, realPole = pole, elev = elev, realCorners = real, curveFit = fit, bank = bank };
        // Monza (royal park): pole 1:21.786 Gasly 2026. 12.8 m range: main dip on the drag between Lesmo 2 and
        // Ascari (under the old banking), downhill run to Parabolica, slight incline up the start straight.
        // Corners: Rettifilo 932, Roggia 2146, Lesmo 2544 / 2882, Ascari 3952-4142, Parabolica 5227 m.
        public static readonly CircuitDef Monza = Tum("monza", "Monza", TrackDataTum.Monza, 0.8, 81.786,
            new double[,] { { 0, 6 }, { 932, 8 }, { 1600, 10 }, { 2146, 11.5 }, { 2544, 12.8 }, { 2882, 11 }, { 3400, 0 },
                            { 3952, 7 }, { 4142, 8 }, { 5227, 3 } },
            new double[,] { { 932, 72, 60 }, { 2146, 110, 60 }, { 2544, 195, 80 }, { 2882, 175, 80 }, { 3952, 200, 60 }, { 4142, 210, 90 }, { 5200, 200, 120 } },
            new double[,] { { 932, 7.5, 60 }, { 2146, 8.9, 60 }, { 2544, 34.7, 80 }, { 2882, 21.1, 80 }, { 3952, 22.0, 60 }, { 4142, 9.8, 90 }, { 5200, 39.8, 120 } } /*CURVEFIT:monza*/);
        // Silverstone: pole 1:28.111 Antonelli 2026. 11.3 m range: the main drop is out of Stowe down to Vale/Club;
        // otherwise near flat, rising gently along Hangar Straight. Corners: Village 892, Loop 1042, Brooklands 1998,
        // Luffield 2182, Copse 3138, Maggotts/Becketts 3722-4020, Stowe 5079, Vale 5513, Club 5595 m.
        public static readonly CircuitDef Silverstone = Tum("silverstone", "Silverstone", TrackDataTum.Silverstone, 0.15, 88.111,
            new double[,] { { 0, 3 }, { 400, 4 }, { 1000, 5 }, { 2000, 5 }, { 3138, 6 }, { 3900, 7 }, { 4300, 8 }, { 4700, 10 },
                            { 5079, 11.3 }, { 5513, 0.5 }, { 5595, 0 }, { 5800, 2 } },
            new double[,] { { 396, 280, 80 }, { 892, 120, 60 }, { 1042, 75, 60 }, { 1248, 205, 60 }, { 1998, 125, 90 }, { 2182, 115, 60 },
                            { 3138, 280, 80 }, { 3722, 280, 60 }, { 3888, 230, 60 }, { 4020, 220, 60 }, { 5079, 240, 80 }, { 5513, 115, 40 }, { 5595, 130, 40 } },
            new double[,] { { 396, 28.6, 80 }, { 892, 21.6, 60 }, { 1042, 18.8, 60 }, { 1248, 27.7, 60 }, { 1998, 21.1, 90 }, { 2182, 2.3, 60 }, { 3138, 14.5, 80 }, { 3722, 26.2, 60 }, { 3888, 0.0, 60 }, { 4020, 32.8, 60 }, { 5079, 37.5, 80 }, { 5513, 19.2, 40 }, { 5595, 20.2, 40 } } /*CURVEFIT:silverstone*/);
        // Suzuka: pole 1:28.778 Antonelli 2026. 40.4 m range: low point between T1 and T2 (the track falls from the
        // chicane all the way down), the Esses and Dunlop climb, high point just past Spoon, undulating run to 130R,
        // which crosses over the Degner-hairpin leg on the bridge (crossing at 2550 / 4924 m: upper leg kept higher).
        // Corners: T2 874, Esses 1116-1738, Degner 2300 / 2458, hairpin 2920, Spoon 3814-3952, chicane 5389 m.
        public static readonly CircuitDef Suzuka = Tum("suzuka", "Suzuka", TrackDataTum.Suzuka, 0.5, 88.778,
            new double[,] { { 0, 8 }, { 750, 0 }, { 1116, 6 }, { 1398, 11 }, { 1738, 17 }, { 2000, 22 }, { 2300, 21 }, { 2458, 17 },
                            { 2550, 15 }, { 2920, 13 }, { 3400, 24 }, { 3814, 37 }, { 4050, 40.4 }, { 4400, 36 }, { 4700, 33 },
                            { 4924, 26 }, { 5389, 16 } },
            new double[,] { { 874, 150, 60 }, { 1116, 215, 50 }, { 1218, 190, 50 }, { 1398, 205, 60 }, { 1598, 195, 60 }, { 1738, 220, 60 }, { 2300, 215, 60 }, { 2458, 140, 60 }, { 3800, 205, 50 }, { 3952, 175, 60 } },
            new double[,] { { 874, 29.5, 60 }, { 1116, 14.1, 50 }, { 1218, 31.4, 50 }, { 1398, 24.4, 60 }, { 1598, 34.2, 60 }, { 1738, 34.7, 60 }, { 2300, 10.8, 60 }, { 2458, 20.2, 60 }, { 3800, 19.2, 50 }, { 3952, 30.5, 60 } } /*CURVEFIT:suzuka*/);
        // Austin (COTA): pole 1:34.035 Antonelli 2026. 30.9 m range (formula1.com; COTA quotes 133 ft / 40.5 m):
        // lowest point between T19 and T20, then the short steep climb (~11 %, f1chronicle.com) to the T1 crest;
        // downhill through T2 and the Esses, the back straight, then low through the stadium section.
        // Corners: T1 666, Esses 1270-1724, T9 1978, T10 2186, T11 2594, T12 3782, T15 4296, T19 5049, T20 5363 m.
        public static readonly CircuitDef Austin = Tum("austin", "Austin", TrackDataTum.Austin, 0.1, 94.035,
            new double[,] { { 0, 3 }, { 330, 8 }, { 640, 30.9 }, { 800, 24 }, { 1270, 21 }, { 1724, 19 }, { 1978, 17 }, { 2186, 15 },
                            { 2594, 12 }, { 3782, 6 }, { 4296, 4 }, { 4520, 4 }, { 5049, 2 }, { 5250, 0 }, { 5363, 1 } },
            new double[,] { { 666, 62, 60 }, { 1270, 235, 45 }, { 1362, 205, 45 }, { 1586, 215, 60 }, { 1724, 150, 60 }, { 1916, 165, 30 }, { 1978, 150, 30 }, { 2186, 250, 60 }, { 2594, 62, 60 }, { 3782, 72, 60 }, { 4018, 90, 45 }, { 4110, 125, 45 }, { 4296, 60, 60 }, { 5049, 150, 60 }, { 5363, 80, 60 } },
            new double[,] { { 666, 14.1, 60 }, { 1270, 4.2, 45 }, { 1362, 22.0, 45 }, { 1586, 0.0, 60 }, { 1724, 19.2, 60 }, { 1916, 30.9, 30 }, { 1978, 9.8, 30 }, { 2186, 29.5, 60 }, { 2594, 13.1, 60 }, { 3782, 15.0, 60 }, { 4018, 21.1, 45 }, { 4110, 7.5, 45 }, { 4296, 11.7, 60 }, { 5049, 8.9, 60 }, { 5363, 15.9, 60 } } /*CURVEFIT:austin*/);
        // Hungaroring: pole 1:17.207 Norris 2026. 34.7 m range: downhill from the start line until T3 (low point),
        // then a steady climb back to the high point on the start-finish straight.
        // Corners: T1 642, T2 1156, T3 1344, T4 1820, T5 2032, T6 2386, T8/9 2622-2730, T11 3106, T12 3536, T14 4128 m.
        public static readonly CircuitDef Budapest = Tum("budapest", "Hungaroring", TrackDataTum.Budapest, 0.3, 77.207,
            new double[,] { { 0, 34.7 }, { 642, 20 }, { 1156, 8 }, { 1344, 0 }, { 1820, 8 }, { 2032, 10 }, { 2386, 14 }, { 2622, 17 },
                            { 3106, 22 }, { 3536, 26 }, { 3822, 29 }, { 4128, 32 } },
            new double[,] { { 642, 85, 60 }, { 1344, 155, 60 }, { 1820, 185, 60 }, { 2042, 160, 60 }, { 2386, 95, 60 }, { 2622, 160, 50 }, { 2730, 135, 50 }, { 3106, 205, 60 }, { 3536, 100, 60 }, { 3822, 105, 60 }, { 4090, 155, 60 } },
            new double[,] { { 642, 20.6, 60 }, { 1344, 6.6, 60 }, { 1820, 17.3, 60 }, { 2042, 30.5, 60 }, { 2386, 6.1, 60 }, { 2622, 19.7, 50 }, { 2730, 23.4, 50 }, { 3106, 32.8, 60 }, { 3536, 11.2, 60 }, { 3822, 27.7, 60 }, { 4090, 35.6, 60 } } /*CURVEFIT:budapest*/);
        // Barcelona: pole 1:14.679 Russell 2026. 29.6 m range: high point out of T12, dropping nearly 30 m to the low
        // point at T1; Campsa (T9) is a blind uphill crest, T5 a downhill braking zone. The TUM centre line has the
        // 2007-2022 T13 / T14-T15 chicane / T16; the 2023+ layout (the 2026 pole) runs one long right-hander from the
        // end of the T12 straight onto the start straight: rebuilt here (Bypass) as a smooth curve between the two
        // straights. That takes 20 m off the lap (real: 4.675 -> 4.657 km in 2023, -18 m). The old T10 (La Caixa)
        // is still the pre-2021 hairpin, a little tighter than the one raced now.
        // Corners: T1 838, T3 1168, T4 1714, T5 2146, T7 2556, T9 2902, T10 3502, T12 3736, T13-T14 ~4100 m.
        public static readonly CircuitDef Catalunya = Tum("catalunya", "Barcelona", Bypass(TrackDataTum.Catalunya, 798, 849), 0.2, 74.679,
            new double[,] { { 0, 9 }, { 838, 0 }, { 948, 1 }, { 1168, 6 }, { 1714, 5 }, { 2146, 2 }, { 2556, 12 }, { 2902, 22 },
                            { 3502, 14 }, { 3620, 18 }, { 3736, 26 }, { 3850, 29.6 }, { 4000, 26 }, { 4128, 22 } },
            new double[,] { { 838, 125, 50 }, { 948, 165, 50 }, { 1168, 240, 80 }, { 1714, 160, 60 }, { 2146, 105, 60 }, { 2556, 150, 60 }, { 2902, 230, 60 }, { 3502, 85, 60 }, { 3620, 150, 50 }, { 3736, 150, 60 } },
            new double[,] { { 838, 15.5, 50 }, { 948, 19.7, 50 }, { 1168, 39.4, 80 }, { 1714, 29.5, 60 }, { 2146, 27.7, 60 }, { 2556, 24.4, 60 }, { 2902, 36.6, 60 }, { 3502, 20.2, 60 }, { 3620, 3.8, 50 }, { 3736, 31.4, 60 } } /*CURVEFIT:catalunya*/);
        // Red Bull Ring: pole 1:06.113 Russell 2026. 63.5 m range (circuit: 65 m, max incline 12 %, decline 9.3 %,
        // redbullring.com): lowest point on the start-finish straight, ~63 m climbed by Remus (T3), the steepest
        // section right after T1-T2; down through T4-T6, a dip then uphill at T7, downhill again to T9/T10.
        // Corners: T1 448, T3 1396, T4 2208, T6 2738, T7 3034, T9 3786, T10 3986 m.
        public static readonly CircuitDef Spielberg = Tum("spielberg", "Red Bull Ring", TrackDataTum.Spielberg, 0.5, 66.113,
            new double[,] { { 0, 2 }, { 200, 8 }, { 448, 22 }, { 900, 52 }, { 1320, 63.5 }, { 1396, 63 }, { 1800, 50 }, { 2208, 36 },
                            { 2500, 28 }, { 2738, 22 }, { 2900, 16 }, { 3034, 19 }, { 3400, 15 }, { 3786, 10 }, { 3986, 4 }, { 4250, 0 } },
            new double[,] { { 448, 95, 60 }, { 1396, 70, 60 }, { 2208, 110, 60 }, { 2738, 185, 80 }, { 3034, 190, 90 }, { 3786, 225, 60 }, { 3986, 140, 80 } },
            new double[,] { { 448, 7.5, 60 }, { 1396, 15.9, 60 }, { 2208, 21.6, 60 }, { 2738, 37.5, 80 }, { 3034, 36.1, 90 }, { 3786, 30.9, 60 }, { 3986, 15.0, 80 } } /*CURVEFIT:spielberg*/);
        // Zandvoort: pole 1:11.163 Norris 2026. Flat: no published height range found (only Hunserug T4 being "one of
        // the highest points"), so no profile yet. Banked: Hugenholtz T3 and Arie Luyendyk T14, up to 19 and 18 deg at the
        // outer edge (progressive): the racing line runs on the lower part, so about half of that is used here.
        // This centre line is 4317 m vs the official 4259 m.
        public static readonly CircuitDef Zandvoort = Tum("zandvoort", "Zandvoort", TrackDataTum.Zandvoort, 0.2, 71.163, elev: null,
            real: new double[,] { { 420, 115, 60 }, { 742, 160, 60 }, { 872, 125, 35 }, { 942, 135, 35 }, { 1764, 235, 80 }, { 2104, 160, 60 }, { 2349, 105, 60 }, { 2619, 135, 60 }, { 3181, 80, 30 }, { 3245, 110, 30 }, { 3613, 165, 60 } },
            fit: new double[,] { { 420, 39.4, 60 }, { 742, 21.6, 60 }, { 872, 26.7, 35 }, { 942, 26.7, 35 }, { 1764, 39.8, 80 }, { 2104, 12.7, 60 }, { 2349, 21.6, 60 }, { 2619, 30.5, 60 }, { 3181, 8.4, 30 }, { 3245, 25.3, 30 }, { 3613, 29.1, 60 } } /*CURVEFIT:zandvoort*/,
            bank: new double[,] { { 830, 990, 9.5 }, { 3680, 4000, 9 } });
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
