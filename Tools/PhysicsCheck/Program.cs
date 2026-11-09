// Physics check: (1) replays the scenarios and compares with golden.csv from the JS version,
// (2) re-runs the calibration against the published FIA F2 figures.
using System.Globalization;
using System.Text.Json;
using FormulaSim;

const double H = 1.0 / 240;
var dir = AppContext.BaseDirectory.Split("/bin/")[0];
var sc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "scenarios.json"))).RootElement;
var golden = File.ReadAllLines(Path.Combine(dir, "golden.csv")).Select(l => l.Split(',')).ToList();
int row = 0; double worstPos = 0, worstVel = 0; string worstAt = "";
var perScenario = new Dictionary<string, (double pos, double vel)>();
foreach (var s in sc.EnumerateArray())
{
    var cfg = new CarConfig();
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

// calibration vs FIA F2 (same method as the web version's test/calibrate.mjs)
{
    const double g = 9.81;
    var car = new FormulaCar(new CarConfig { tc = TcMode.High }); double t = 0, t100 = 0, t200 = 0;
    while (t < 60) { car.Step(new CarInput(0, 1, 0), H); t += H; double k = car.vx * 3.6; if (t100 == 0 && k >= 100) t100 = t; if (t200 == 0 && k >= 200) t200 = t; }
    double top = car.vx * 3.6;
    var bc = new FormulaCar { vx = 300 / 3.6, gear = 6 }; double peakB = 0;
    for (int i = 0; i < 240 * 1.5; i++) { bc.Step(new CarInput(0, 0, 1), H); peakB = Math.Min(peakB, bc.ax / g); }
    var lat = new List<string>();
    foreach (var kmh in new[] { 100, 180, 250, 290 })
    {
        double best = 0;
        for (double st = 0.05; st <= 1.0; st += 0.05)
        {
            var c2 = new FormulaCar(new CarConfig { tc = TcMode.High }) { vx = kmh / 3.6, gear = 4 };
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
    Console.WriteLine($"Calibration: 0-100 {t100:F2}s (FIA 2.90) | 0-200 {t200:F2}s (FIA 6.60) | top {top:F0} km/h | braking {peakB:F2}g (FIA -3.5) | lateral {string.Join(" ", lat)}g (FIA max 3.9)");
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
    var cfg = new CarConfig();
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
    foreach (var (nm, t) in new[] { ("Test track", Track.Build()), ("Monaco", Track.FromLonLat(TrackData.Monaco, TrackData.MonacoWidth)), ("Spa", Track.FromLonLat(TrackData.Spa, TrackData.SpaWidth)) })
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = RacingLine.Compute(t, cfg);
        double ms = sw.Elapsed.TotalMilliseconds;
        double vmin = r.speed.Min() * 3.6, vmax = r.speed.Max() * 3.6, offMax = r.off.Max(o => Math.Abs(o));
        int nb = r.phase.Count(p => p < 0), nc = r.phase.Count(p => p == 0), na = r.phase.Count(p => p > 0);
        var lt = TimeSpan.FromSeconds(r.lapTime);
        Console.WriteLine($"{nm,-10} line: lap {(int)lt.TotalMinutes}:{lt.Seconds:00}.{lt.Milliseconds:000}, speed {vmin:F0}-{vmax:F0} km/h, max offset {offMax:F2} m (limit {t.width / 2 - 1.3:F2}), " +
                          $"brake/coast/throttle {100.0 * nb / r.N:F0}/{100.0 * nc / r.N:F0}/{100.0 * na / r.N:F0}%, {ms:F0} ms");
    }
}
