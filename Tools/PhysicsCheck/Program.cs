// Physics check: (1) replays the scenarios and compares with golden.csv from the JS version,
// (2) re-runs the calibration against the published FIA F2 figures.
using System.Globalization;
using System.Text.Json;
using FormulaSim;

const double H = 1.0 / 240;
Track[] Circuits() => FormulaSim.Circuits.All.Select(d => d.Build()).ToArray();
string[] CircuitNames = FormulaSim.Circuits.All.Select(c => c.name).ToArray();

// ---- AI trainer: dotnet run -- train ----
// Evolution search (cross-entropy method) over the controller gains. Every candidate drives full laps in the
// real physics; the score is the flying-lap time, and leaving the track disqualifies it.
// ---- physics lab: dotnet run -- lab <outdir> ----
// Standard vehicle-dynamics tests; writes CSV files that Tools/PhysicsCheck/plot_lab.py turns into charts.
if (args.Length > 0 && args[0] == "lab")
{
    string labDir = args.Length > 1 ? args[1] : "lab"; Directory.CreateDirectory(labDir);
    var inv = CultureInfo.InvariantCulture; const double g = 9.81, rho = 1.225;
    string F(double v) => v.ToString("G6", inv);
    var cfg = new CarConfig();
    // 1) tyre: lateral force coefficient vs slip angle at several loads (the Pacejka "magic formula")
    using (var w = new StreamWriter(Path.Combine(labDir, "tyre.csv")))
    {
        w.WriteLine("alpha_deg,load_ratio,mu_y");
        foreach (var lr in new[] { 0.5, 1.0, 2.0, 3.0 })
            for (double a = 0; a <= 15.001; a += 0.25)
                w.WriteLine($"{F(a)},{F(lr)},{F(cfg.mu * Math.Pow(lr, cfg.loadExp - 1) * FormulaCar.Magic(cfg, a * Math.PI / 180, lr))}");
    }
    // grip model as a formula: total tyre force available at speed v (both axles, straight line)
    double GripN(CarConfig c, double v)
    {
        double down = 0.5 * rho * v * v * c.clA, fz0F = c.mass * g * c.frontWeight, fz0R = c.mass * g * (1 - c.frontWeight);
        double fF = fz0F + down * c.aeroFront, fR = fz0R + down * (1 - c.aeroFront);
        return c.mu * (fz0F * Math.Pow(fF / fz0F, c.loadExp) + c.rearGrip * fz0R * Math.Pow(fR / fz0R, c.loadExp));
    }
    // 2) performance envelope vs speed: simulated and the formula
    using (var w = new StreamWriter(Path.Combine(labDir, "envelope.csv")))
    {
        w.WriteLine("kmh,kind,sim_g,formula_g");
        // braking from 345 km/h to rest, sampled every 5 km/h
        var bc = new FormulaCar(new CarConfig { tc = TcMode.High }) { vx = 345 / 3.6, gear = 8 }; double next = 335;
        for (int i = 0; i < 240 * 10 && bc.vx > 3; i++)
        {
            bc.Step(new CarInput(0, 0, 1), H);
            double k = bc.vx * 3.6;
            if (k <= next && i > 30) { double v = bc.vx; double fg = (Math.Min(GripN(bc.cfg, v), bc.cfg.brakeForce) + 0.5 * rho * v * v * bc.cfg.cdA + bc.cfg.rollRes * bc.cfg.mass * g) / bc.cfg.mass / g; w.WriteLine($"{F(k)},brake,{F(-bc.ax / g)},{F(fg)}"); next -= 5; }
        }
        // acceleration from rest, full throttle
        var ac = new FormulaCar(new CarConfig { tc = TcMode.High }); next = 5;
        for (int i = 0; i < 240 * 60; i++)
        {
            ac.Step(new CarInput(0, 1, 0), H);
            double k = ac.vx * 3.6;
            if (k >= next)
            {
                double v = ac.vx, p = 0;
                for (double rpm = ac.cfg.idleRpm; rpm <= ac.cfg.redline; rpm += 50) p = Math.Max(p, FormulaCar.TorqueAt(ac.cfg, rpm) * rpm * Math.PI / 30);
                double push = (p + (ac.ersEnergy > 0 ? FormulaCar.ErsPowerAt(ac.cfg, v) : 0)) * ac.cfg.driveEff / Math.Max(v, 1);
                double traction = GripN(ac.cfg, v) * (1 - ac.cfg.frontWeight) * 1.08;
                double cd = ac.aeroX > 0.5 ? ac.cfg.cdAX : ac.cfg.cdA;
                double fg = (Math.Min(push, traction) - 0.5 * rho * v * v * cd - ac.cfg.rollRes * ac.cfg.mass * g) / ac.cfg.mass / g;
                w.WriteLine($"{F(k)},accel,{F(ac.ax / g)},{F(fg)}"); next += 5;
            }
        }
        // steady cornering: highest lateral g held for 2 s at each speed
        for (int kmh = 60; kmh <= 320; kmh += 20)
        {
            double best = 0;
            for (double st = 0.04; st <= 1.0; st += 0.03)
            {
                var c2 = new FormulaCar(new CarConfig { tc = TcMode.High }) { vx = kmh / 3.6, gear = 5 };
                bool ok = true; double sum = 0; int n = 0;
                for (int i = 0; i < 240 * 3; i++)
                {
                    double sp = Math.Sqrt(c2.vx * c2.vx + c2.vy * c2.vy);
                    c2.Step(new CarInput(st, Math.Clamp(0.3 + (kmh / 3.6 - sp) * 0.5, 0, 1), 0), H);
                    if (Math.Abs(Math.Atan2(c2.vy, c2.vx)) > 0.25) { ok = false; break; }
                    if (i > 240 * 2) { sum += Math.Abs(c2.ay) / g; n++; }
                }
                if (ok && n > 0) best = Math.Max(best, sum / n);
            }
            double v = kmh / 3.6;
            w.WriteLine($"{kmh},corner,{F(best)},{F(GripN(cfg, v) / cfg.mass / g)}");
        }
    }
    // 3) handling: understeer gradient from a slow steering ramp at constant speed; step-steer yaw response
    using (var w = new StreamWriter(Path.Combine(labDir, "handling.csv")))
    {
        w.WriteLine("test,kmh,x,y");
        foreach (var kmh in new[] { 100, 200, 280 })
        {
            var c3 = new FormulaCar(new CarConfig { tc = TcMode.High }) { vx = kmh / 3.6, gear = 5 };
            double L = c3.cfg.wheelbase;
            for (int i = 0; i < 240 * 12; i++)
            {
                double st = Math.Min(1, i / (240.0 * 10)) * 0.9, sp = Math.Sqrt(c3.vx * c3.vx + c3.vy * c3.vy);
                c3.Step(new CarInput(st, Math.Clamp(0.3 + (kmh / 3.6 - sp) * 0.5, 0, 1), 0), H);
                if (Math.Abs(Math.Atan2(c3.vy, c3.vx)) > 0.2) break;
                if (i % 24 == 0 && Math.Abs(c3.ay) > 0.2)
                {
                    double R = sp * sp / Math.Abs(c3.ay), dAck = L / R;     // Ackermann angle: what a neutral car would need
                    w.WriteLine($"ramp,{kmh},{F(Math.Abs(c3.ay) / g)},{F((Math.Abs(c3.delta) - dAck) * 180 / Math.PI)}");
                }
            }
        }
        foreach (var kmh in new[] { 100, 200 })
        {
            var c4 = new FormulaCar(new CarConfig { tc = TcMode.High, steerSpeed = 1000 }) { vx = kmh / 3.6, gear = 5 };
            for (int i = 0; i < 240 * 2; i++)
            {
                double sp = Math.Sqrt(c4.vx * c4.vx + c4.vy * c4.vy);
                c4.Step(new CarInput(i >= 24 ? 0.12 : 0, Math.Clamp(0.3 + (kmh / 3.6 - sp) * 0.5, 0, 1), 0), H);
                if (i % 2 == 0) w.WriteLine($"step,{kmh},{F(i * H - 0.1)},{F(Math.Abs(c4.r) * 180 / Math.PI)}");
            }
        }
    }
    // 4) a flying lap and a 25-lap stint at Spa by the trained AI, against the ideal speed profile
    {
        var t = FormulaSim.Circuits.Spa.Build(); var line = RacingLine.Compute(t, new CarConfig());
        var ace = new DriverProfile { name = "ace", pace = 1, consistency = 1, aggression = 0.5 };
        var d = new AiDriver(t, line, new CarConfig(), ace, AiTuning.For(2));
        d.Place(t.N - 30, line.off[t.N - 30]);
        using var wl = new StreamWriter(Path.Combine(labDir, "lap.csv"));
        using var ws = new StreamWriter(Path.Combine(labDir, "stint.csv"));
        wl.WriteLine("s,kmh,ideal_kmh,throttle,brake,gear,lat_g,lon_g");
        ws.WriteLine("lap,time,tyre_f,tyre_r,wear_f,wear_r");
        double time = 0; int steps = 0, lastLap = 0; double sumTF = 0, sumTR = 0; int nT = 0;
        while (d.lap < 27 && time < 4000)
        {
            if (steps++ % 4 == 0) { d.Think(4 * H, time, null, null, -1, 0); WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); }
            d.car.Step(d.LastInput, H); time += H;
            if (steps % 24 == 0) { sumTF += d.car.tyreTF; sumTR += d.car.tyreTR; nT++; }
            if (d.lap == 2 && steps % 12 == 0 && d.idx >= 0)
                wl.WriteLine($"{F(t.pts[d.idx].s)},{F(Math.Sqrt(d.car.vx * d.car.vx + d.car.vy * d.car.vy) * 3.6)},{F(line.speed[d.idx] * 3.6)},{F(d.car.throttle)},{F(d.car.brake)},{d.car.gear},{F(d.car.ay / g)},{F(d.car.ax / g)}");
            if (d.lap != lastLap)
            {
                if (lastLap >= 1 && !double.IsNaN(d.lastLap)) ws.WriteLine($"{lastLap},{F(d.lastLap)},{F(sumTF / Math.Max(1, nT))},{F(sumTR / Math.Max(1, nT))},{F(d.car.wearF)},{F(d.car.wearR)}");
                lastLap = d.lap; sumTF = sumTR = 0; nT = 0;
            }
        }
    }
    // 5) Monaco corner speeds: real, coarse outline as published, outline with fitted corner shapes
    using (var w = new StreamWriter(Path.Combine(labDir, "corners.csv")))
    {
        var def = FormulaSim.Circuits.Monaco; var real = def.realCorners;
        string[] names = { "Ste Devote", "Massenet", "Casino", "Mirabeau", "Hairpin", "L. Mirabeau", "Portier", "Chicane", "Tabac", "Pool 1", "Pool 2", "Rascasse", "Noghes" };
        var raw = Track.FromLonLat(def.lonlat, def.width, def.startAt, def.elev); var fit = def.Build();
        var lr = RacingLine.Compute(raw, new CarConfig(), 1.3, 0.97, 0.95); var lf = RacingLine.Compute(fit, new CarConfig(), 1.3, 0.97, 0.95);
        double Min(Track t, RacingLine l, double s0) { double mn = 1e9; for (int i = 0; i < t.N; i++) if (Math.Abs(t.pts[i].s - s0) < 45) mn = Math.Min(mn, l.speed[i]); return mn * 3.6; }
        w.WriteLine("corner,real,raw,fitted");
        for (int c = 0; c < real.GetLength(0); c++) w.WriteLine($"{names[c]},{F(real[c, 1])},{F(Min(raw, lr, real[c, 0]))},{F(Min(fit, lf, real[c, 0]))}");
        w.WriteLine($"LAP,{F(def.realPole)},{F(lr.lapTime)},{F(lf.lapTime)}");
    }
    Console.WriteLine("lab data written to " + labDir);
    return;
}

// ---- fit corner shapes to real corner speeds: dotnet run -- fitgeo ----
if (args.Length > 0 && args[0] == "fitgeo")
{
    var def = FormulaSim.Circuits.Monaco; var real = def.realCorners; int nc = real.GetLength(0);
    var sig = new double[nc]; for (int c = 0; c < nc; c++) sig[c] = def.curveSigma;
    double[,] Table() { var f = new double[nc, 2]; for (int c = 0; c < nc; c++) { f[c, 0] = real[c, 0]; f[c, 1] = sig[c]; } return f; }
    double[] Ratios()
    {
        var t = Track.FromLonLat(def.lonlat, def.width, def.startAt, def.elev, def.curveSigma, Table());
        var line = RacingLine.Compute(t, new CarConfig(), 1.3, 0.97, 0.95);
        var r = new double[nc];
        for (int c = 0; c < nc; c++) { double mn = 1e9; for (int i = 0; i < t.N; i++) if (Math.Abs(t.pts[i].s - real[c, 0]) < 45) mn = Math.Min(mn, line.speed[i]); r[c] = mn * 3.6 / real[c, 1]; }
        return r;
    }
    for (int pass = 0; pass < 3; pass++)
        for (int c = 0; c < nc; c++)
        {
            double lo = 0, hi = 24;
            for (int it = 0; it < 7; it++) { sig[c] = (lo + hi) / 2; if (Ratios()[c] < 1) lo = sig[c]; else hi = sig[c]; }
            sig[c] = (lo + hi) / 2;
        }
    var rr = Ratios(); double se = 0; foreach (var x in rr) se += Math.Log(x) * Math.Log(x);
    Console.WriteLine($"fitted: RMS corner speed error {Math.Sqrt(se / nc) * 100:F1}%; ratios {string.Join(" ", rr.Select(x => x.ToString("F2", CultureInfo.InvariantCulture)))}");
    string rows = string.Join(", ", Enumerable.Range(0, nc).Select(c => "{ " + real[c, 0].ToString(CultureInfo.InvariantCulture) + ", " + sig[c].ToString("F1", CultureInfo.InvariantCulture) + " }"));
    var path = Path.Combine(AppContext.BaseDirectory.Split("/bin/")[0], "../../Assets/Scripts/Physics/Circuit.cs");
    var src = File.ReadAllText(path); int a = src.IndexOf("curveFit = "), b = src.IndexOf("/*CURVEFIT*/");
    File.WriteAllText(path, src[..a] + "curveFit = new double[,] { " + rows + " }, " + src[b..]);
    Console.WriteLine("curveFit = { " + rows + " }");
    return;
}

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
            if (steps % 4 == 1) WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1);
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
        double tMin = 999, tMaxF = 0, tMaxR = 0, tSumF = 0, tSumR = 0; int tN = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (time < 6 * 130 && ds.Min(d => d.lap) < 5)
        {
            if (steps++ % 4 == 0)
            {
                for (int k = 0; k < n; k++) { ds[k].Think(4 * H, time, ds, null, -1, 0); if (ds[k].recovered) { recov++; rec[k]++; } }
                for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) if (Traffic.Collide(ds[i].car, ds[j].car)) hits++;
            }
            if (steps % 240 == 0) { var c0 = ds[0].car; tMin = Math.Min(tMin, Math.Min(c0.tyreTF, c0.tyreTR)); tMaxF = Math.Max(tMaxF, c0.tyreTF); tMaxR = Math.Max(tMaxR, c0.tyreTR); if (time > 60) { tSumF += c0.tyreTF; tSumR += c0.tyreTR; tN++; } }
            foreach (var d in ds) { if (steps % 4 == 1) WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); if (hf != null && steps % 4 == 1) d.car.grade = hf.Grade(d.car.x, d.car.y, d.car.heading); d.car.Step(d.LastInput, H); }
            time += H;
        }
        Console.WriteLine($"  tyres after the race (leader): front {ds[0].car.tyreTF:F0} C, rear {ds[0].car.tyreTR:F0} C; ride at the end F {ds[0].car.rideF * 1000:F0} mm R {ds[0].car.rideR * 1000:F0} mm; over the race: average F {tSumF / Math.Max(1, tN):F0} R {tSumR / Math.Max(1, tN):F0}, peak F {tMaxF:F0} R {tMaxR:F0}; wear after {ds[0].lap} laps F {ds[0].car.wearF * 100:F1}% R {ds[0].car.wearR * 100:F1}%");
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
    { // brake temperature through a Monza turn 1 style stop (340 -> 80 km/h) and the straight after it
        var bt = new FormulaCar(F1()) { vx = 340 / 3.6, gear = 8 }; double t0 = bt.brakeTF; bt.Step(new CarInput(0, 0, 0), H); t0 = bt.brakeTF;
        while (bt.vx * 3.6 > 80) bt.Step(new CarInput(0, 0, 1), H);
        double peakF = bt.brakeTF, peakR = bt.brakeTR; for (int i = 0; i < 240 * 12; i++) bt.Step(new CarInput(0, 1, 0), H);
        var cold = new FormulaCar(F1() ) { vx = 200 / 3.6, gear = 6, brakeTF = 40, brakeTR = 40 }; double xc = cold.x; while (cold.vx > 0.3) cold.Step(new CarInput(0, 0, 1), H);
        var warm = new FormulaCar(F1()) { vx = 200 / 3.6, gear = 6 }; double xw = warm.x; while (warm.vx > 0.3) warm.Step(new CarInput(0, 0, 1), H);
        Console.WriteLine($"  brakes: {t0:F0} C -> front {peakF:F0} C, rear {peakR:F0} C after 340->80 km/h (Brembo: ~1000 C), {bt.brakeTF:F0} C after 12 s flat out; 200-0 cold brakes {cold.x - xc:F0} m vs warm {warm.x - xw:F0} m");
    }
    Console.WriteLine("F1 2026 car vs real figures:");
    Console.WriteLine($"  0-100 {t100:F2}s (perfect traction; real 2.2-2.6 with wheelspin) | 0-200 {t200:F2}s (2025 cars ~4.4-4.6) | 0-300 {t300:F2}s | top {top:F0} km/h, engine only {noK.vx * 3.6:F0} (real speed traps 325-345)");
    Console.WriteLine($"  braking from 320: peak {peakB:F2} g (real 5-6) | 200-0 in {Stop(200):F0} m | 100-0 in {Stop(100):F0} m (real 15-17)");
    Console.WriteLine($"  cornering g at km/h {string.Join(" ", lat)} (real: ~2 slow, 4-5 fast)");
    Console.WriteLine($"  {"circuit",-14} {"ideal",8} {"pole",8} {"error",7}  speed km/h  length  elev");
    var errs = new List<double>();
    foreach (var def in FormulaSim.Circuits.All.Where(d => d.realPole > 0))
    {
        var t2 = def.Build(); var r = RacingLine.Compute(t2, new CarConfig(), 1.3, 0.97, 0.95);
        double e = (r.lapTime / def.realPole - 1) * 100; errs.Add(e);
        string elevTxt = t2.h == null ? "flat" : $"{t2.h.Max() - t2.h.Min():F0} m";
        Console.WriteLine($"  {def.name,-14} {r.lapTime,8:F2} {def.realPole,8:F2} {e,6:+0.0;-0.0}%  {r.speed.Min() * 3.6,4:F0}-{r.speed.Max() * 3.6,3:F0}  {t2.length,6:F0}  {elevTxt}");
    }
    Console.WriteLine($"  mean error {errs.Average():+0.0;-0.0}%, spread (sd) {Math.Sqrt(errs.Select(x => (x - errs.Average()) * (x - errs.Average())).Average()):F1}%");
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
    foreach (var (nm, t) in new[] { ("Test track", FormulaSim.Circuits.Test.Build()), ("Monaco", FormulaSim.Circuits.Monaco.Build()), ("Spa", FormulaSim.Circuits.Spa.Build()) })
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
