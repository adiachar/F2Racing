// Physics check: (1) replays the scenarios and compares with golden.csv from the JS version,
// (2) re-runs the calibration against the published FIA F2 figures.
using System.Globalization;
using System.Text.Json;
using FormulaSim;

const double H = 1.0 / 240;
Track[] Circuits() => new[] { Track.Build(), TrackData.MonacoTrack(), Track.FromLonLat(TrackData.Spa, TrackData.SpaWidth) };
string[] CircuitNames = { "Test track", "Monaco", "Spa" };

// ---- AI trainer: dotnet run -- train ----
// Evolution search (cross-entropy method) over the controller gains. Every candidate drives full laps in the
// real physics; the score is the flying-lap time, and leaving the track disqualifies it.
if (args.Length > 0 && args[0] == "train")
{
    var ace = new DriverProfile { name = "ace", pace = 1, consistency = 1, aggression = 0.5 };
    double Eval(Track t, RacingLine line, double[] p, out double worstLat)
    {
        worstLat = 0;
        var d = new AiDriver(t, line, new CarConfig(), ace, AiTuning.From(p));
        var hf = t.h != null ? new HeightField(t) : null;
        d.Place(t.N - 30, line.off[t.N - 30]);
        double time = 0, limit = t.width / 2 + 0.3; int steps = 0;
        while (time < 400)
        {
            if (steps++ % 4 == 0)
            {
                d.Think(4 * H, time, null, null, -1, 0);
                if (d.recovered) return 5000 - d.progress * 1000;
                if (time > 3) worstLat = Math.Max(worstLat, Math.Abs(d.lateral));
                if (Math.Abs(d.lateral) > limit) return 5000 - d.progress * 1000;
                if (d.lap >= 3) return d.lastLap;
            }
            var near = d.lateral; double hw = t.width / 2;
            d.car.surfaceMu = Math.Abs(near) <= hw ? 1.0 : Surfaces.Kerb.mu; d.car.surfaceDrag = Math.Abs(near) <= hw ? 0 : Surfaces.Kerb.drag;
            if (hf != null && steps % 4 == 1) d.car.grade = hf.Grade(d.car.x, d.car.y, d.car.heading);
            d.car.Step(d.LastInput, H); time += H;
        }
        return 6000;
    }
    double[] lo = { 2, 0.05, 0.0, 0.1, 0.1, 0.70, 0.60 }, hi = { 12, 0.5, 0.4, 1.5, 1.5, 1.05, 1.0 };
    var result = new List<double[]>(); var tracks = Circuits(); var swAll = System.Diagnostics.Stopwatch.StartNew(); long laps = 0;
    for (int ti = 0; ti < tracks.Length; ti++)
    {
        var t = tracks[ti]; var line = RacingLine.Compute(t, new CarConfig());
        var rnd = new Random(42 + ti);
        double[] mean = new AiTuning().ToArray(), sig = { 1.5, 0.06, 0.05, 0.15, 0.15, 0.05, 0.06 };
        double[] best = mean; double bestCost = double.MaxValue;
        const int Pop = 40, Elite = 8, Gens = 16;
        for (int gen = 0; gen < Gens; gen++)
        {
            var pop = new double[Pop][]; var cost = new double[Pop];
            for (int k = 0; k < Pop; k++)
            {
                pop[k] = new double[mean.Length];
                for (int j = 0; j < mean.Length; j++)
                {
                    double u1 = 1 - rnd.NextDouble(), u2 = rnd.NextDouble(), n = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                    pop[k][j] = Math.Clamp(k == 0 ? mean[j] : mean[j] + sig[j] * n, lo[j], hi[j]);
                }
            }
            Parallel.For(0, Pop, k => { cost[k] = Eval(t, line, pop[k], out _); });
            laps += Pop * 3;
            var order = Enumerable.Range(0, Pop).OrderBy(k => cost[k]).ToArray();
            if (cost[order[0]] < bestCost) { bestCost = cost[order[0]]; best = pop[order[0]]; }
            for (int j = 0; j < mean.Length; j++)
            {
                double m = 0; for (int e = 0; e < Elite; e++) m += pop[order[e]][j]; m /= Elite;
                double var = 0; for (int e = 0; e < Elite; e++) var += (pop[order[e]][j] - m) * (pop[order[e]][j] - m);
                mean[j] = m; sig[j] = Math.Max(Math.Sqrt(var / Elite), sig[j] * 0.35);
            }
            Console.WriteLine($"{CircuitNames[ti]} gen {gen + 1}: best lap {(bestCost < 1000 ? bestCost.ToString("F2") + " s" : "off track")}");
        }
        // back off a little from the knife edge so the gains also hold up in traffic
        best = (double[])best.Clone(); best[5] *= 0.985; best[6] *= 0.97;
        double lapT = Eval(t, line, best, out double wl);
        Console.WriteLine($"{CircuitNames[ti]}: trained lap {lapT:F2} s (max {wl:F2} m from the centre, track half-width {t.width / 2:F1}) gains {string.Join(", ", best.Select(b => b.ToString("F3", CultureInfo.InvariantCulture)))}");
        result.Add(best);
    }
    Console.WriteLine($"Training: {laps} laps driven in {swAll.Elapsed.TotalSeconds:F0} s");
    var path = Path.Combine(AppContext.BaseDirectory.Split("/bin/")[0], "../../Assets/Scripts/Physics/AiDriver.cs");
    var src = File.ReadAllText(path); int a = src.IndexOf("/*TRAINED*/"), b = src.IndexOf("/*END*/");
    var rows = string.Join("", result.Select(r => "\n            new[] { " + string.Join(", ", r.Select(x => x.ToString("F3", CultureInfo.InvariantCulture))) + " },"));
    File.WriteAllText(path, src[..(a + 11)] + rows + "\n            " + src[b..]);
    return;
}

// ---- race test: dotnet run -- race ----
if (args.Length > 0 && args[0] == "race")
{
    var tracks = Circuits();
    for (int ti = 0; ti < tracks.Length; ti++)
    {
        var t = tracks[ti]; var line = RacingLine.Compute(t, new CarConfig());
        var ds = new List<AiDriver>(); int n = DriverProfile.Grid.Length;
        var hf = t.h != null ? new HeightField(t) : null;
        for (int k = 0; k < n; k++) { var d = new AiDriver(t, line, new CarConfig(), DriverProfile.Grid[k], AiTuning.For(ti), 1, k + 1); d.Place(t.N - 15 - k * 4, (k % 2 == 0 ? 1 : -1) * 1.8); ds.Add(d); }
        double time = 0; int steps = 0, recov = 0, hits = 0; var rec = new int[n];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (time < 6 * 130 && ds.Min(d => d.lap) < 5)
        {
            if (steps++ % 4 == 0)
            {
                for (int k = 0; k < n; k++) { ds[k].Think(4 * H, time, ds, null, -1, 0); if (ds[k].recovered) { recov++; rec[k]++; } }
                for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) if (Traffic.Collide(ds[i].car, ds[j].car)) hits++;
            }
            foreach (var d in ds) { double hw = t.width / 2, al = Math.Abs(d.lateral); d.car.surfaceMu = al <= hw ? 1 : al <= hw + 1.4 ? Surfaces.Kerb.mu : Surfaces.Grass.mu; d.car.surfaceDrag = al <= hw ? 0 : al <= hw + 1.4 ? Surfaces.Kerb.drag : Surfaces.Grass.drag; if (hf != null && steps % 4 == 1) d.car.grade = hf.Grade(d.car.x, d.car.y, d.car.heading); d.car.Step(d.LastInput, H); }
            time += H;
        }
        Console.WriteLine($"{CircuitNames[ti]}: {n} cars, {time:F0} s raced in {sw.Elapsed.TotalSeconds:F1} s, {recov} resets, {hits} contact ticks");
        int pos = 1;
        foreach (var d in ds.OrderByDescending(d => d.progress))
            Console.WriteLine($"  P{pos++,-2} {d.who.name,-13} pace {d.who.pace:F2} best {d.bestLap:F2} resets {rec[ds.IndexOf(d)]}");
    }
    return;
}

var dir = AppContext.BaseDirectory.Split("/bin/")[0];
var sc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "scenarios.json"))).RootElement;
var golden = File.ReadAllLines(Path.Combine(dir, "golden.csv")).Select(l => l.Split(',')).ToList();
int row = 0; double worstPos = 0, worstVel = 0; string worstAt = "";
var perScenario = new Dictionary<string, (double pos, double vel)>();
foreach (var s in sc.EnumerateArray())
{
    var cfg = CarConfig.LegacyF2();
    if (s.GetProperty("cfg").TryGetProperty("tc", out var tc)) cfg.tc = tc.GetString() switch { "off" => TcMode.Off, "high" => TcMode.High, _ => TcMode.Low };
    if (s.GetProperty("cfg").TryGetProperty("abs", out var ab)) cfg.abs = ab.GetBoolean();
    var car = new FormulaCar(cfg) { vx = s.GetProperty("vx0").GetDouble(), gear = s.GetProperty("gear0").GetInt32() };
    string name = s.GetProperty("name").GetString();
    int n = 0; double sp = 0, sv = 0;
    foreach (var seg in s.GetProperty("segs").EnumerateArray())
    {
        var v = seg.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        int steps = (int)Math.Round(v[0] / H);
        for (int i = 0; i < steps; i++)
        {
            double f = (double)i / steps;
            car.Step(new CarInput(v[1] + (v[4] - v[1]) * f, v[2] + (v[5] - v[2]) * f, v[3] + (v[6] - v[3]) * f), H);
            if (++n % 12 == 0)
            {
                var g = golden[row++];
                double P(int k) => double.Parse(g[k], CultureInfo.InvariantCulture);
                double dp = Math.Max(Math.Abs(car.x - P(2)), Math.Abs(car.y - P(3)));
                double dv = Math.Max(Math.Abs(car.vx - P(5)), Math.Abs(car.vy - P(6)));
                sp = Math.Max(sp, dp); sv = Math.Max(sv, dv);
                if (dp > worstPos) { worstPos = dp; worstAt = $"{name} step {n}"; }
                worstVel = Math.Max(worstVel, dv);
            }
        }
    }
    perScenario[name] = (sp, sv);
}
Console.WriteLine($"JS vs C# over {row} samples:");
foreach (var (k, v) in perScenario) Console.WriteLine($"  {k,-18} max position diff {v.pos:E2} m, max speed diff {v.vel:E2} m/s");
Console.WriteLine($"  worst {worstPos:E2} m at {worstAt}; {(worstPos < 1e-3 ? "MATCH" : "DIFFERENT")}");

// ---- 2026 F1 car against real-world figures ----
{
    const double g = 9.81;
    CarConfig F1() => new CarConfig { tc = TcMode.High };
    var car = new FormulaCar(F1()); double t = 0, t100 = 0, t200 = 0, t300 = 0, top = 0;
    while (t < 60) { car.Step(new CarInput(0, 1, 0), H); t += H; double k = car.vx * 3.6; top = Math.Max(top, k); if (t100 == 0 && k >= 100) t100 = t; if (t200 == 0 && k >= 200) t200 = t; if (t300 == 0 && k >= 300) t300 = t; }
    var c0 = new CarConfig { tc = TcMode.High, ersPower = 0 }; var noK = new FormulaCar(c0); for (int i = 0; i < 240 * 60; i++) noK.Step(new CarInput(0, 1, 0), H);
    var bc = new FormulaCar(F1()) { vx = 320 / 3.6, gear = 8 }; double peakB = 0;
    for (int i = 0; i < 240 * 2; i++) { bc.Step(new CarInput(0, 0, 1), H); if (i > 60) peakB = Math.Min(peakB, bc.ax / g); }
    double Stop(double kmh) { var b2 = new FormulaCar(F1()) { vx = kmh / 3.6, gear = 3 }; double x0 = b2.x; int n = 0; while (b2.vx > 0.3 && n++ < 240 * 20) b2.Step(new CarInput(0, 0, 1), H); return b2.x - x0; }
    var lat = new List<string>();
    foreach (var kmh in new[] { 80, 150, 220, 280 })
    {
        double best = 0;
        for (double st = 0.05; st <= 1.0; st += 0.05)
        {
            var c2 = new FormulaCar(F1()) { vx = kmh / 3.6, gear = 5 };
            bool ok = true; double sum = 0; int n = 0;
            for (int i = 0; i < 240 * 3; i++)
            {
                double sp = Math.Sqrt(c2.vx * c2.vx + c2.vy * c2.vy);
                c2.Step(new CarInput(st, sp < kmh / 3.6 ? 0.6 : 0.2, 0), H);
                if (Math.Abs(Math.Atan2(c2.vy, c2.vx)) > 0.3) { ok = false; break; }
                if (i > 240 * 2) { sum += Math.Abs(c2.ay) / g; n++; }
            }
            if (ok && n > 0) best = Math.Max(best, sum / n);
        }
        lat.Add($"{kmh}:{best:F2}");
    }
    foreach (var mode in new[] { TcMode.Low, TcMode.Off })
    {
        var l = new FormulaCar(new CarConfig { tc = mode }); double tt = 0, a100 = 0, a200 = 0;
        while (tt < 12 && a200 == 0) { l.Step(new CarInput(0, 1, 0), H); tt += H; if (a100 == 0 && l.vx * 3.6 >= 100) a100 = tt; if (l.vx * 3.6 >= 200) a200 = tt; }
        Console.WriteLine($"  launch TC {mode}: 0-100 {a100:F2}s, 0-200 {a200:F2}s");
    }
    Console.WriteLine("F1 2026 car vs real figures:");
    Console.WriteLine($"  0-100 {t100:F2}s (perfect traction; real 2.2-2.6 with wheelspin) | 0-200 {t200:F2}s (2025 cars ~4.4-4.6) | 0-300 {t300:F2}s | top {top:F0} km/h, engine only {noK.vx * 3.6:F0} (real speed traps 325-345)");
    Console.WriteLine($"  braking from 320: peak {peakB:F2} g (real 5-6) | 200-0 in {Stop(200):F0} m | 100-0 in {Stop(100):F0} m (real 15-17)");
    Console.WriteLine($"  cornering g at km/h {string.Join(" ", lat)} (real: ~2 slow, 4-5 fast)");
    foreach (var (nm, t2, real) in new[] { ("Monaco", TrackData.MonacoTrack(), 72.051), ("Spa", Track.FromLonLat(TrackData.Spa, TrackData.SpaWidth), 104.361) })
    {
        var r = RacingLine.Compute(t2, new CarConfig(), 1.3, 0.97, 0.95);
        Console.WriteLine($"  {nm}: ideal lap {r.lapTime:F2}s, real 2026 pole {real:F2}s ({(r.lapTime / real - 1) * 100:+0.0;-0.0}%), speed {r.speed.Min() * 3.6:F0}-{r.speed.Max() * 3.6:F0} km/h");
    }
}

// track port check against the web track.js (point count, length, a few samples)
{
    var t = Track.Build();
    Console.WriteLine($"Track: N {t.N}, length {t.length:F3} m, p[100] ({t.pts[100].x:F4},{t.pts[100].y:F4}) curv {t.pts[100].curv:E4}, nearest(300,5) lateral {t.Nearest(300, 5).lateral:F4}");
}

// real circuits
foreach (var (nm, data, w) in new[] { ("Monaco", TrackData.Monaco, TrackData.MonacoWidth), ("Spa", TrackData.Spa, TrackData.SpaWidth) })
{
    var t = Track.FromLonLat(data, w); double minR = double.MaxValue; var bb = t.Bounds();
    foreach (var p in t.pts) if (Math.Abs(p.curv) > 1e-6) minR = Math.Min(minR, 1 / Math.Abs(p.curv));
    Console.WriteLine($"{nm}: length {t.length:F0} m, {t.N} points, tightest radius {minR:F1} m, size {bb.maxX - bb.minX:F0} x {bb.maxY - bb.minY:F0} m");
}

// racing line + speed profile (port of racingline.js): parity with the JS on the default track, then the real circuits
{
    var cfg = CarConfig.LegacyF2();
    var gpath = Path.Combine(dir, "racingline_golden.csv");
    var rl = RacingLine.Compute(Track.Build(), cfg);
    if (File.Exists(gpath))
    {
        var rows = File.ReadAllLines(gpath).Select(l => l.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray()).ToArray();
        double dOff = 0, dPos = 0, dV = 0;
        if (rows.Length != rl.N) Console.WriteLine($"Racing line: point count differs, JS {rows.Length} vs C# {rl.N}");
        else for (int i = 0; i < rl.N; i++)
        {
            dOff = Math.Max(dOff, Math.Abs(rl.off[i] - rows[i][0]));
            dPos = Math.Max(dPos, Math.Max(Math.Abs(rl.x[i] - rows[i][1]), Math.Abs(rl.y[i] - rows[i][2])));
            dV = Math.Max(dV, Math.Abs(rl.speed[i] - rows[i][3]));
        }
        Console.WriteLine($"Racing line JS vs C# (default track, {rows.Length} pts): max offset diff {dOff:E2} m, position {dPos:E2} m, speed {dV:E2} m/s; {(dOff < 1e-6 && dPos < 1e-6 && dV < 1e-4 ? "MATCH" : "DIFFERENT")}");
    }
    foreach (var (nm, t) in new[] { ("Test track", Track.Build()), ("Monaco", TrackData.MonacoTrack()), ("Spa", Track.FromLonLat(TrackData.Spa, TrackData.SpaWidth)) })
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = RacingLine.Compute(t, new CarConfig());
        double ms = sw.Elapsed.TotalMilliseconds;
        double vmin = r.speed.Min() * 3.6, vmax = r.speed.Max() * 3.6, offMax = r.off.Max(o => Math.Abs(o));
        int nb = r.phase.Count(p => p < 0), nc = r.phase.Count(p => p == 0), na = r.phase.Count(p => p > 0);
        var lt = TimeSpan.FromSeconds(r.lapTime);
        Console.WriteLine($"{nm,-10} line: lap {(int)lt.TotalMinutes}:{lt.Seconds:00}.{lt.Milliseconds:000}, speed {vmin:F0}-{vmax:F0} km/h, max offset {offMax:F2} m (limit {t.width / 2 - 1.3:F2}), " +
                          $"brake/coast/throttle {100.0 * nb / r.N:F0}/{100.0 * nc / r.N:F0}/{100.0 * na / r.N:F0}%, {ms:F0} ms");
    }
}
