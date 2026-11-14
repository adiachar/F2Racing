// Physics check: (1) replays the scenarios and compares with golden.csv from the JS version,
// (2) re-runs the calibration against the published FIA F2 figures.
using System.Globalization;
using System.Text.Json;
using FormulaSim;

const double H = 1.0 / 240;
// VARWIDTH=1: build the measured circuits with their per-point widths (Track.VariableWidth)
if (Environment.GetEnvironmentVariable("VARWIDTH") == "1") Track.VariableWidth = true;
if (Environment.GetEnvironmentVariable("MAPMAX") != null) RacingLine.MapMax = double.Parse(Environment.GetEnvironmentVariable("MAPMAX"), CultureInfo.InvariantCulture);
Track[] Circuits() => FormulaSim.Circuits.All.Select(d => d.Build()).ToArray();
string[] CircuitNames = FormulaSim.Circuits.All.Select(c => c.name).ToArray();

// ---- damage check: dotnet run -- damage ----
// Steady corner at fixed steer and speed with and without damage: lateral g, yaw rate, aero balance (front wing
// damage must understeer = less yaw for the same steer); then a straight line with hands off to show the pull.
if (args.Length > 0 && args[0] == "damage")
{
    (double g, double yaw, double bal, double down, double drag) Corner(Action<FormulaCar> hurt, double kmh, double steer)
    {
        var car = new FormulaCar(new CarConfig { damage = true, fuel = false }) { vx = kmh / 3.6, gear = 6 };
        hurt(car);
        double v0 = kmh / 3.6, gs = 0, ys = 0; int n = 0;
        for (int i = 0; i < 240 * 6; i++)
        {
            double v = Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
            car.Step(new CarInput(steer, Math.Clamp(0.5 + (v0 - v) * 0.5, 0, 1), Math.Clamp((v - v0) * 0.3, 0, 1)), H);
            if (i > 240 * 4) { gs += Math.Abs(car.ay) / 9.81; ys += Math.Abs(car.r); n++; }
        }
        return (gs / n, ys / n * 180 / Math.PI, car.aeroBalance, car.downforce, car.dragForce);
    }
    void Row(string name, Action<FormulaCar> hurt)
    {
        var c = Corner(hurt, 200, 0.10);
        // hands-off straight line at 250 km/h for 2 s: sideways drift
        var car = new FormulaCar(new CarConfig { damage = true, fuel = false }) { vx = 250 / 3.6, gear = 7 }; hurt(car);
        double y0 = car.y;
        for (int i = 0; i < 480; i++) car.Step(new CarInput(0, 0.8, 0), H);
        Console.WriteLine($"  {name,-30} 200 km/h, fixed steer: {c.g:F2} g, yaw {c.yaw:F2} deg/s, balance {c.bal * 100:F1}% front, downforce {c.down / 1000:F1} kN, drag {c.drag / 1000:F2} kN | hands off 2 s at 250: drift {car.y - y0:+0.00;-0.00} m (+ = left), grip left {car.DamageGrip(200 / 3.6) * 100:F0}%");
    }
    Console.WriteLine("Damage (cfg.damage = true):");
    Row("none", c => { });
    Row("front wing left endplate (0.4)", c => c.damage.wingFL = 0.4);
    Row("front wing both sides gone", c => { c.damage.wingFL = 1; c.damage.wingFR = 1; });
    Row("rear wing (1.0)", c => c.damage.wingRear = 1);
    Row("floor (0.5, ~50 points)", c => c.damage.floor = 0.5);
    Row("front-left suspension (1.0)", c => c.damage.suspFL = 1);
    Row("rear-right suspension (1.0)", c => c.damage.suspRR = 1);
    Row("puncture front-right", c => c.damage.punctFR = 1);
    // impacts: what a hit does
    var t = new FormulaCar(new CarConfig { damage = true });
    t.ApplyImpact(6, 2.6, 0.2); Console.WriteLine($"  impact 6 m/s nose (22 km/h): wing L {t.damage.wingFL:F2} R {t.damage.wingFR:F2}");
    t = new FormulaCar(new CarConfig { damage = true }); t.ApplyImpact(12, 1.0, 0.9, true);
    Console.WriteLine($"  wall 12 m/s front-left: wing L {t.damage.wingFL:F2}, susp FL {t.damage.suspFL:F2}, floor {t.damage.floor:F2}, flat {t.damage.Punctured}");
    return;
}

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

// ---- centre line radius and ideal speed every 20 m: dotnet run -- profile <circuit id> ----
if (args.Length > 1 && args[0] == "profile")
{
    var def = FormulaSim.Circuits.All.First(d => d.id == args[1]); var t = def.Build(); var line = RacingLine.Compute(t, new CarConfig(), 1.3, 0.97, 0.95);
    int step = (int)Math.Round(20 / t.ds);
    for (int i = 0; i < t.N; i += step)
        Console.WriteLine($"{t.pts[i].s,6:F0} m  radius {(Math.Abs(t.pts[i].curv) > 1e-5 ? 1 / t.pts[i].curv : 99999),8:F0}  line {(Math.Abs(line.curv[i]) > 1e-5 ? 1 / line.curv[i] : 99999),8:F0}  {line.speed[i] * 3.6,4:F0} km/h{(t.bank != null && t.bank[i] != 0 ? $"  bank {t.bank[i] * 180 / Math.PI:F1}" : "")}");
    return;
}

// ---- corner speed minima of the ideal lap: dotnet run -- corners [circuit id] ----
// Lists every local speed minimum (s, km/h) and, where the circuit has real corner speeds, the comparison.
if (args.Length > 0 && args[0] == "corners")
{
    var inv = CultureInfo.InvariantCulture;
    foreach (var def in FormulaSim.Circuits.All.Where(d => d.realPole > 0 && (args.Length < 2 || d.id == args[1])))
    {
        var t = def.Build(); var line = RacingLine.Compute(t, new CarConfig(), 1.3, 0.97, 0.95);
        var mins = new List<string>(); int w = (int)Math.Round(40 / t.ds);
        for (int i = 0; i < t.N; i++)
        {
            double v = line.speed[i]; bool isMin = true;
            for (int j = -w; j <= w && isMin; j++) if (j != 0 && line.speed[((i + j) % t.N + t.N) % t.N] < v - 1e-9) isMin = false;
            if (isMin && line.speed[(i - 1 + t.N) % t.N] > v && v < line.speed.Max() - 3) mins.Add($"{t.pts[i].s:F0}:{v * 3.6:F0}");
        }
        Console.WriteLine($"{def.name} lap {line.lapTime:F2} (pole {def.realPole:F2}, {(line.lapTime / def.realPole - 1) * 100:+0.0;-0.0}%) minima s:km/h {string.Join(" ", mins)}");
        if (def.realCorners != null)
        {
            var rc = def.realCorners;
            for (int c = 0; c < rc.GetLength(0); c++)
            {
                double mn = 1e9, at = 0; double win = Math.Min(45, rc.GetLength(1) > 2 ? rc[c, 2] : 45);
                for (int i = 0; i < t.N; i++) if (Math.Abs(t.pts[i].s - rc[c, 0]) < win && line.speed[i] < mn) { mn = line.speed[i]; at = t.pts[i].s; }
                Console.WriteLine($"   corner at {rc[c, 0],5:F0}: real {rc[c, 1],3:F0}, ideal {mn * 3.6,3:F0} km/h at {at:F0} m ({(mn * 3.6 / rc[c, 1] - 1) * 100:+0;-0}%)");
            }
        }
    }
    return;
}

// ---- fit corner shapes to real corner speeds: dotnet run -- fitgeo [circuit id, default monaco] ----
// For each corner in CircuitDef.realCorners {s, km/h [, reach m]} find the curvature window (Track.SmoothedCurvature)
// that makes the ideal lap's minimum speed there match the real one; writes the table back into Circuit.cs.
if (args.Length > 0 && args[0] == "fitgeo")
{
    string fid = args.Length > 1 ? args[1] : "monaco";
    var def = FormulaSim.Circuits.All.First(d => d.id == fid); var real = def.realCorners; int nc = real.GetLength(0);
    bool hasReach = real.GetLength(1) > 2; double sigMax = def.metric != null ? 120 : 24;
    var sig = new double[nc]; for (int c = 0; c < nc; c++) sig[c] = def.curveSigma;
    double[,] Table()
    {
        var f = new double[nc, hasReach ? 3 : 2];
        for (int c = 0; c < nc; c++) { f[c, 0] = real[c, 0]; f[c, 1] = sig[c]; if (hasReach) f[c, 2] = real[c, 2]; }
        return f;
    }
    Track Make() => def.metric != null ? Track.LocalFit(Track.Build(TrackDataTum.Centre(def.metric), def.width), Table())
                                       : Track.FromLonLat(def.lonlat, def.width, def.startAt, def.elev, def.curveSigma, Table());
    double maxMove = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 5;   // m, how far a corner may move
    var rawT = def.metric != null ? Track.Build(TrackDataTum.Centre(def.metric), def.width) : null;
    double[] moved = new double[nc]; Track loose = null;
    double[] Ratios()
    {
        var t = Make();
        if (def.metric != null && def.elev != null) t.SetElevation(def.elev);
        if (def.bank != null) t.SetBanking(def.bank);
        var line = RacingLine.Compute(t, new CarConfig(), 1.3, 0.97, 0.95);
        var r = new double[nc];
        for (int c = 0; c < nc; c++)
        {
            double mn = 1e9, win = Math.Min(45, hasReach ? real[c, 2] : 45);
            for (int i = 0; i < t.N; i++) if (Math.Abs(t.pts[i].s - real[c, 0]) < win) mn = Math.Min(mn, line.speed[i]);
            r[c] = mn * 3.6 / real[c, 1];
            // the TUM centre lines are measured: the fitted bend may not wander more than maxMove from them
            moved[c] = 0;
            if (rawT != null)
            {
                if (c == 0) loose = Track.LocalFit(rawT, Table(), false); // movement is measured before the lap is scaled back to length
                double span = (hasReach ? real[c, 2] : 60) + 3 * sig[c];
                for (int i = 0; i < loose.N; i++) if (Math.Abs(loose.pts[i].s - real[c, 0]) <= span) moved[c] = Math.Max(moved[c], rawT.Nearest(loose.pts[i].x, loose.pts[i].y).dist);
            }
        }
        return r;
    }
    for (int pass = 0; pass < 3; pass++)
        for (int c = 0; c < nc; c++)
        {
            double lo = 0, hi = sigMax;
            for (int it = 0; it < 8; it++) { sig[c] = (lo + hi) / 2; if (Ratios()[c] < 1 && moved[c] <= maxMove) lo = sig[c]; else hi = sig[c]; }
            sig[c] = lo;
            if (sig[c] < 0.5) sig[c] = 0;
        }
    var rr = Ratios(); double se = 0; foreach (var x in rr) se += Math.Log(x) * Math.Log(x);
    Console.WriteLine($"fitted: RMS corner speed error {Math.Sqrt(se / nc) * 100:F1}%; ratios {string.Join(" ", rr.Select(x => x.ToString("F2", CultureInfo.InvariantCulture)))}");
    if (rawT != null) Console.WriteLine($"moved (m): {string.Join(" ", moved.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)))}");
    if (def.metric != null)
    {
        // how far the fitted centre line moved away from the measured one (nearest-point distance)
        var raw = rawT; var fit = Make();
        double mv = 0, mvAt = 0;
        foreach (var p in fit.pts) { var n = raw.Nearest(p.x, p.y); if (n.dist > mv) { mv = n.dist; mvAt = p.s; } }
        Console.WriteLine($"length {raw.length:F0} -> {fit.length:F0} m; centre line moved at most {mv:F1} m (at {mvAt:F0} m)");
    }
    string rows = string.Join(", ", Enumerable.Range(0, nc).Select(c => "{ " + real[c, 0].ToString(CultureInfo.InvariantCulture) + ", " + sig[c].ToString("F1", CultureInfo.InvariantCulture)
        + (hasReach ? ", " + real[c, 2].ToString(CultureInfo.InvariantCulture) : "") + " }"));
    var path = Path.Combine(AppContext.BaseDirectory.Split("/bin/")[0], "../../Assets/Scripts/Physics/Circuit.cs");
    var src = File.ReadAllText(path); string marker = "/*CURVEFIT:" + fid + "*/";
    int b = src.IndexOf(marker);
    if (b < 0) Console.WriteLine($"no {marker} marker in Circuit.cs; table not written");
    else
    {
        int a = src.LastIndexOf("new double[,]", b), close = src.LastIndexOf('}', b);
        File.WriteAllText(path, src[..a] + "new double[,] { " + rows + " }" + src[(close + 1)..]);
    }
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
    // dotnet run -- train <circuit id>: retrain only that circuit, keep the others' gains
    string only = args.Length > 1 ? args[1] : null;
    for (int ti = 0; ti < tracks.Length; ti++)
    {
        if (only != null && FormulaSim.Circuits.All[ti].id != only) { result.Add(AiTuning.Trained[ti]); continue; }
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
        // back off a little from the knife edge so the gains also hold up in traffic (3 % of grip: with turbo lag and
        // tyre relaxation in the car, 1.5 % left about twice as many resets in the race test)
        best = (double[])best.Clone(); best[5] *= 0.97; best[6] *= 0.97;
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

// ---- diagnostics: dotnet run -- curv <id> <from m> <to m>: raw vs fitted centre-line curvature, line, speeds ----
if (args.Length > 3 && args[0] == "curv")
{
    var inv = CultureInfo.InvariantCulture;
    int ti = Array.FindIndex(FormulaSim.Circuits.All, d => d.id == args[1]); var def = FormulaSim.Circuits.All[ti];
    double s0 = double.Parse(args[2], inv), s1 = double.Parse(args[3], inv);
    var t = def.Build(); var rawT = def.metric != null ? Track.Build(TrackDataTum.Centre(def.metric), TrackDataTum.MeanWidth(def.metric)) : t;
    var line = RacingLine.Compute(t, new CarConfig());
    var tu = AiTuning.For(ti); var ai = (double[])line.SpeedProfile(new CarConfig(), tu.grip, tu.brakeUse).Clone(); line = RacingLine.Compute(t, new CarConfig());
    for (int i = 0; i < t.N; i++) if (t.pts[i].s >= s0 && t.pts[i].s <= s1 && i % 2 == 0)
        Console.WriteLine($"{t.pts[i].s,6:F0}  raw k {rawT.pts[Math.Min(i, rawT.N - 1)].curv * 1000,7:F2}  fit k {t.pts[i].curv * 1000,7:F2}  line k {line.curv[i] * 1000,7:F2}  off {line.off[i],5:F1}  ai {ai[i] * 3.6,4:F0}");
    return;
}

// ---- diagnostics: dotnet run -- kinks [id]: curvature spikes the corner fit leaves at the edges of its windows ----
if (args.Length > 0 && args[0] == "kinks")
{
    foreach (var def in FormulaSim.Circuits.All.Where(d => d.metric != null && d.curveFit != null && (args.Length < 2 || d.id == args[1])))
    {
        var t = def.Build(); var raw = Track.Build(TrackDataTum.Centre(def.metric), TrackDataTum.MeanWidth(def.metric)); var f = def.curveFit;
        var outp = new List<string>();
        for (int c = 0; c < f.GetLength(0); c++)
            foreach (var edge in new[] { f[c, 0] - f[c, 2], f[c, 0] + f[c, 2] })
            {
                // the fitted bend's peak |k| within 20 m of the edge, against the smoothest of the two neighbours 20-40 m away
                double pk = 0, at = 0, nb = 1e9;
                for (int i = 0; i < t.N; i++)
                {
                    double d = t.pts[i].s - edge, k = Math.Abs(t.pts[i].curv);
                    if (Math.Abs(d) <= 20 && k > pk) { pk = k; at = t.pts[i].s; }
                }
                double l = 0, r = 0;
                for (int i = 0; i < t.N; i++) { double d = t.pts[i].s - at; if (d >= -40 && d <= -20) l = Math.Max(l, Math.Abs(t.pts[i].curv)); if (d >= 20 && d <= 40) r = Math.Max(r, Math.Abs(t.pts[i].curv)); }
                nb = Math.Max(l, r);
                if (pk > 1.0 / 400 && pk > 1.2 * nb) outp.Add($"{at:F0} m (edge of {f[c, 0]}): R {1 / pk:F0} m vs {1 / Math.Max(1e-6, nb):F0} m around");
            }
        // how much of the lap the racing line spends pressed against its limit (where any traffic offset hits the aim clamp)
        double AtEdge(Track tt) { var l = RacingLine.Build(tt); double lim = tt.width / 2 - 1.3; return 100.0 * l.off.Count(o => Math.Abs(o) > lim - 0.05) / tt.N; }
        Console.WriteLine($"{def.name}: line at its limit {AtEdge(raw):F0}% of the lap unfitted, {AtEdge(t):F0}% fitted; {(outp.Count == 0 ? "no edge kinks" : string.Join("; ", outp))}");
    }
    return;
}

// ---- diagnostics: dotnet run -- trace <id>: the race test on one circuit; for every reset, the car's state from
// about 0.5 s before it started to slide (body slip over 5 deg) until shortly after ("ofs" = its traffic offset) ----
if (args.Length > 1 && args[0] == "trace")
{
    int ti = Array.FindIndex(FormulaSim.Circuits.All, d => d.id == args[1]);
    var t = FormulaSim.Circuits.All[ti].Build(); var line = RacingLine.Compute(t, new CarConfig());
    var ds = new List<AiDriver>(); int n = DriverProfile.Grid.Length; var hf = t.h != null ? new HeightField(t) : null;
    for (int k = 0; k < n; k++) { var d = new AiDriver(t, line, new CarConfig(), DriverProfile.Grid[k], AiTuning.For(ti), 1, k + 1); d.Place(t.GridIndex(k), (k % 2 == 0 ? 1 : -1) * 1.8); ds.Add(d); } // the game's grid (CircuitDef.gridOffset)
    var hist = new List<string>[n]; for (int k = 0; k < n; k++) hist[k] = new List<string>();
    double time = 0; int steps = 0;
    while (time < 6 * 130 && ds.Min(d => d.lap) < 5)
    {
        if (steps++ % 4 == 0)
            for (int k = 0; k < n; k++)
            {
                var d = ds[k]; var c = d.car;
                double vb = Math.Sqrt(c.vx * c.vx + c.vy * c.vy), slip = Math.Atan2(c.vy, Math.Max(0.1, c.vx)) * 180 / Math.PI;
                d.Think(4 * H, time, ds, null, -1, 0);
                if (d.idx >= 0) { hist[k].Add($"      {time,6:F2}s yaw {c.r,5:F2} delta {c.delta * 180 / Math.PI,5:F1} lock {c.lockAngle * 180 / Math.PI,4:F1} s {t.pts[d.idx].s,5:F0} v {vb * 3.6,4:F0} tgt {d.target * 3.6,4:F0} line {line.speed[d.idx] * 3.6,4:F0} lat {d.lateral,5:F1} lineoff {line.off[d.idx] + d.offset,5:F1} ofs {d.offset,5:F1} slip {slip,5:F1} steer {d.LastInput.steer,5:F2} thr {d.LastInput.throttle:F2} brk {d.LastInput.brake:F2} lap {d.lap} grip {c.gripF:F3}/{c.gripR:F3} T {c.tyreTF:F0}/{c.tyreTR:F0} brakeT {c.brakeTF:F0}/{c.brakeTR:F0} bias {c.biasNow:F2}"); if (hist[k].Count > 300) hist[k].RemoveAt(0); }
                if (d.recovered) { Console.WriteLine($"RESET {d.who.name} (pace {d.who.pace:F2}) at {t.pts[d.idx].s:F0} m, t {time:F1}"); { int on = hist[k].FindIndex(h => { var w = h.Split("slip")[1].Trim().Split(' ')[0]; return Math.Abs(double.Parse(w, CultureInfo.InvariantCulture)) > 5; }); if (on < 0) on = hist[k].Count - 1; for (int q = Math.Max(0, on - 30); q < Math.Min(hist[k].Count, on + 12); q += 2) Console.WriteLine(hist[k][q]); } hist[k].Clear(); }
            }
        if (steps % 4 == 1) for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) Traffic.Collide(ds[i].car, ds[j].car);
        foreach (var d in ds) { if (steps % 4 == 1) WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); if (hf != null && steps % 4 == 1) d.car.grade = hf.Grade(d.car.x, d.car.y, d.car.heading); d.car.Step(d.LastInput, H); }
        time += H;
    }
    return;
}

// ---- energy / brakes / tyres audit of the trained AI's flying lap: dotnet run -- audit [circuit ids] ----
// ERS deployed and harvested (by source), seconds flat out on an empty battery, end-of-straight speeds (peak on the
// straight / speed at the brake point), peak braking g, peak brake temperatures, average tyre temperatures.
if (args.Length > 0 && args[0] == "audit")
{
    var ids = args.Length > 1 ? args.Skip(1).ToArray() : new[] { "monaco", "spa", "monza" };
    {   // no ABS from 200 km/h: which axle locks first as the brake balance moves forward
        var outp = new List<string>();
        for (double bb = 0.50; bb <= 0.645; bb += 0.02)
        {
            string first = "none"; double atV = 0;
            for (double pedal = 0.2; pedal <= 1.001 && first == "none"; pedal += 0.02)
            {
                var b2 = new FormulaCar(new CarConfig { tc = TcMode.High, abs = false, brakeBias = bb }) { vx = 200 / 3.6, gear = 6 }; int n = 0;
                while (b2.vx > 15 && n++ < 240 * 6) { b2.Step(new CarInput(0, 0, pedal), H); if (b2.lockR) { first = "R"; atV = b2.vx * 3.6; break; } if (b2.lockF) { first = "F"; atV = b2.vx * 3.6; break; } }
            }
            outp.Add($"{bb * 100:F0}%:{first}@{atV:F0}");
        }
        Console.WriteLine("  first lock (no ABS, 200 km/h, pedal raised): " + string.Join(" ", outp));
    }
    foreach (var e0 in new[] { 4e6, 2e6, 1e6, 0 })
    {   // flat out from 250 km/h on a straight: distance to 300 / 320 / 330 / 340 km/h with this much in the battery
        var c = new FormulaCar(new CarConfig { tc = TcMode.High }) { vx = 250 / 3.6, gear = 6, ersEnergy = e0, deployMap = 2 }; double x0 = c.x, tt = 0; var marks = new List<string>(); double nx = 300;
        while (tt < 40 && nx <= 340) { c.Step(new CarInput(0, 1, 0), H); tt += H; if (c.vx * 3.6 >= nx) { marks.Add($"{nx:F0}:{c.x - x0:F0}m"); nx += nx < 320 ? 20 : 10; } }
        Console.WriteLine($"  from 250 km/h with {e0 / 1e6:F0} MJ: {string.Join(" ", marks)}; after {tt:F0} s {c.vx * 3.6:F0} km/h, {c.x - x0:F0} m, battery {c.ersEnergy / 1e6:F2} MJ");
    }
    foreach (var id in ids)
    {
        int ti = Array.FindIndex(FormulaSim.Circuits.All, d => d.id == id); if (ti < 0) continue;
        var t = FormulaSim.Circuits.All[ti].Build(); var line = RacingLine.Compute(t, new CarConfig());
        var ace = new DriverProfile { name = "ace", pace = 1, consistency = 1, aggression = 0.5 };
        var tuA = AiTuning.For(ti); if (Environment.GetEnvironmentVariable("AUDITBRAKE") != null) tuA.brakeUse = double.Parse(Environment.GetEnvironmentVariable("AUDITBRAKE"), CultureInfo.InvariantCulture);
        var d = new AiDriver(t, line, new CarConfig(), ace, tuA); var hf = t.h != null ? new HeightField(t) : null;
        d.Place(t.N - 30, line.off[t.N - 30]);
        double time = 0; int steps = 0; double dep = 0, hB = 0, hC = 0, hL = 0, hP = 0, flat = 0, empty = 0, peakG = 0, bF = 0, bR = 0, tF = 0, tR = 0, nT = 0, bot = 0, eMin = 9e9;
        var ends = new List<string>(); double vPk = 0; bool onStraight = false; double flatT = 0;
        while (d.lap < 3 && time < 600)
        {
            if (steps++ % 4 == 0) { d.Think(4 * H, time, null, null, -1, 0); WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); if (hf != null) d.car.grade = hf.Grade(d.car.x, d.car.y, d.car.heading); }
            var c = d.car; c.Step(d.LastInput, H); time += H;
            if (d.lap != 2) continue;
            double v = Math.Sqrt(c.vx * c.vx + c.vy * c.vy) * 3.6, kw = c.ersKw;
            if (Environment.GetEnvironmentVariable("AUDITTRACE") == "1" && steps % (c.brake > 0 ? 12 : 60) == 0) Console.WriteLine($"   {t.pts[Math.Max(0, d.idx)].s,5:F0} m {v,4:F0} km/h tgt {d.target * 3.6,4:F0} thr {c.throttle:F2} brk {c.brake:F2} g{c.gear} rpm {c.rpm,6:F0} aero {c.aeroX:F1} ers {kw,5:F0} kW batt {c.ersEnergy / 1e6:F2} map {c.deployMap:F2} ax {c.ax / 9.81,5:F2}");
            if (kw > 0) dep += kw * 1000 * H; else if (kw < 0) { double j = -kw * 1000 * H; if (c.brake > 0) hB += j; else if (c.throttle > 0.9) hC += j; else if (c.throttle < 0.05) hL += j; else hP += j; }
            if (c.throttle > 0.95) { flat += H; if (c.ersEnergy <= 1) empty += H; flatT += H; }
            if (c.brake < 0.3) { vPk = Math.Max(vPk, v); if (c.throttle > 0.95) onStraight = flatT > 4; }
            else if (c.brake > 0.05 && onStraight) { ends.Add($"{vPk:F0}/{v:F0}"); onStraight = false; vPk = 0; flatT = 0; }
            else if (c.brake > 0.05) { vPk = 0; flatT = 0; }
            if (c.brake > 0.3) peakG = Math.Max(peakG, -c.ax / 9.81);
            bF = Math.Max(bF, c.brakeTF); bR = Math.Max(bR, c.brakeTR); tF += c.tyreTF; tR += c.tyreTR; nT++; if (c.bottoming) bot += H; eMin = Math.Min(eMin, c.ersEnergy);
        }
        if (Environment.GetEnvironmentVariable("AUDITTRACE") == "2") { var pl = RacingLine.Compute(t, new CarConfig(), 1.3, 0.97, 0.95); for (int i = 0; i < t.N; i += 25) Console.WriteLine($"   plan {t.pts[i].s,5:F0} m {pl.speed[i] * 3.6,4:F0} km/h soc {pl.soc[i]:F2} map {pl.deploy[i]:F1} phase {pl.phase[i]}"); }
        Console.WriteLine($"{id}: lap {d.lastLap:F2} s; ERS deployed {dep / 1e6:F2} MJ, harvested {(hB + hC + hL + hP) / 1e6:F2} MJ (brakes {hB / 1e6:F2}, superclip {hC / 1e6:F2}, partial {hP / 1e6:F2}, lift {hL / 1e6:F2}); battery low {eMin / 1e6:F2} MJ, empty {empty:F1} of {flat:F1} s flat out");
        Console.WriteLine($"   straights peak/brake-point km/h: {string.Join(" ", ends)}; peak braking {peakG:F2} g; brakes peak F {bF:F0} R {bR:F0} C; tyres average F {tF / nT:F0} R {tR / nT:F0} C; bottoming {bot:F1} s");
    }
    return;
}

// ---- tyre strategy plans: dotnet run -- race strat (no driving: the plans the rivals make, per race length) ----
if (args.Length > 1 && args[0] == "race" && args[1] == "strat")
{
    var tracks = Circuits();
    foreach (var id in new[] { "spa", "monaco", "monza" })
    {
        int ti = Array.FindIndex(FormulaSim.Circuits.All, d => d.id == id); var t = tracks[ti]; var line = RacingLine.Compute(t, new CarConfig());
        foreach (int laps in new[] { 3, 8, 12, 22, 44, 78 })
            foreach (int wet in new[] { 0, 2 })
            {
                if (wet == 2 && laps != 22) continue;
                var plans = new List<string>(); var stops = new int[4];
                for (int k = 0; k < 10; k++) { var st = new TyreStrategy(k + 1); st.Plan(laps, wet, id, line.lapTime); plans.Add(st.Describe()); stops[Math.Min(3, st.StopsPlanned)]++; }
                Console.WriteLine($"{id,-7} {laps,2} laps {(wet == 2 ? "WET" : "dry")}: stops 0/1/2/3+ = {stops[0]}/{stops[1]}/{stops[2]}/{stops[3]}  e.g. {string.Join(" | ", plans.Take(4))}");
            }
    }
    // a driven check: Spa, 24 laps, 8 cars, plans and live wear
    {
        int ti = Array.FindIndex(FormulaSim.Circuits.All, d => d.id == "spa"); var t = tracks[ti]; var line = RacingLine.Compute(t, new CarConfig());
        int n = 8, laps = 24; var ds = new List<AiDriver>(); var rcs = new RaceControl(t, line, 5) { totalLaps = laps }; rcs.Init(n);
        for (int k = 0; k < n; k++) { var d = new AiDriver(t, line, new CarConfig { fuelStart = 0.23 * t.length / 1000 * (laps + 0.5) + 2 }, DriverProfile.Grid[k * 2], AiTuning.For(ti), 1, k + 1); d.Place(t.GridIndex(k), (k % 2 == 0 ? 1 : -1) * 1.8); d.rc = rcs; d.slot = k; d.PlanRace(laps, 0, k + 1); ds.Add(d); }
        var plan0 = ds.Select(d => d.strat.Describe()).ToArray();
        var log = ds.Select(_ => new List<string>()).ToArray(); var setSeen = ds.Select(d => d.setSerial).ToArray();
        double time = 0; int steps = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
        while (time < laps * 140 && ds.Min(d => d.lap) <= laps)
        {
            if (steps++ % 4 == 0)
            {
                for (int k = 0; k < n; k++)
                {
                    double wr = Math.Max(ds[k].car.wearF, ds[k].car.wearR); int lp = ds[k].lap;
                    ds[k].Think(4 * H, time, ds, null, -1, 0);
                    if (ds[k].setSerial != setSeen[k]) { setSeen[k] = ds[k].setSerial; log[k].Add($"L{lp}:{TyreSets.Letter[ds[k].tyreSet]}(worn {wr * 100:F0}%)"); }
                }
                for (int k = 0; k < n; k++) rcs.Feed(k, ds[k]); rcs.Update(4 * H, time, time > 2);
                for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) Traffic.Collide(ds[i].car, ds[j].car);
            }
            foreach (var d in ds) { if (steps % 4 == 1) WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); d.car.Step(d.LastInput, H); }
            time += H;
        }
        Console.WriteLine($"Spa {laps} laps driven ({time:F0} s in {sw.Elapsed.TotalSeconds:F0} s):");
        for (int k = 0; k < n; k++) Console.WriteLine($"  {ds[k].who.name,-13} plan {plan0[k],-22} stops: {string.Join(" ", log[k])}  wear at the flag {Math.Max(ds[k].car.wearF, ds[k].car.wearR) * 100:F0}%");
    }
    return;
}

// ---- race test: dotnet run -- race [fuel | sc] ("fuel": start on a 70 kg race fuel load instead of qualifying trim;
// "sc": force a safety car on lap 2 and check the queue, no overtaking and the restart). RC=on: race control and
// tyre strategy on in the plain race test too (default off: the legacy race test).
if (args.Length > 0 && args[0] == "race")
{
    bool scTest = args.Length > 1 && args[1] == "sc";
    bool rcOn = scTest || (Environment.GetEnvironmentVariable("RC") ?? "").ToLowerInvariant() == "on";
    int scPass = 0, scRuns = 0;
    var tracks = Circuits(); double raceFuel = args.Length > 1 && args[1] == "fuel" ? 70 : new CarConfig().fuelStart;
    var only = Environment.GetEnvironmentVariable("RACEONLY")?.Split(',') ?? (scTest ? new[] { "test", "monaco", "spa", "monza", "silverstone" } : null);   // RACEONLY=monaco,monza: just these circuits
    // DAMAGE=full: damage physics on (CarConfig.damage), as the game's "Damage: Full". Default off (the legacy race test).
    bool dmgFull = (Environment.GetEnvironmentVariable("DAMAGE") ?? "").ToLowerInvariant() == "full";
    Console.WriteLine($"race test, damage {(dmgFull ? "FULL" : "off")}, race control {(rcOn ? "on" : "off")}{(scTest ? ", forced safety car" : "")}");
    int totalResets = 0, totalStops = 0, totalHits = 0;
    bool coarse = Environment.GetEnvironmentVariable("COARSE") == "1";
    static bool Coarse(Track t, AiDriver me, AiDriver d)
    {
        if (me.idx < 0 || d.idx < 0) return false;
        int g = Math.Abs(d.idx - me.idx); g = Math.Min(g, t.N - g);
        return g * t.ds > 150;
    }
    if (coarse) Console.WriteLine("COARSE: cars more than 150 m from car 0 stepped at 120 Hz");
    for (int ti = 0; ti < tracks.Length; ti++)
    {
        if (only != null && !only.Contains(FormulaSim.Circuits.All[ti].id)) continue;
        var t = tracks[ti]; var line = RacingLine.Compute(t, new CarConfig());
        var ds = new List<AiDriver>(); int n = DriverProfile.Grid.Length;
        var hf = t.h != null ? new HeightField(t) : null;
        for (int k = 0; k < n; k++) { var d = new AiDriver(t, line, new CarConfig { fuelStart = raceFuel, damage = dmgFull }, DriverProfile.Grid[k], AiTuning.For(ti), 1, k + 1); d.Place(t.GridIndex(k), (k % 2 == 0 ? 1 : -1) * 1.8); ds.Add(d); } // the game's grid (CircuitDef.gridOffset)
        RaceControl rcx = null;
        if (rcOn) { rcx = new RaceControl(t, line, ti + 1) { totalLaps = scTest ? 12 : 5 }; rcx.Init(n); for (int k = 0; k < n; k++) { ds[k].rc = rcx; ds[k].slot = k; ds[k].PlanRace(rcx.totalLaps, 0, k + 1); } }
        bool forced = false; double greenAt = -1, deployAt = -1; int greenLap = 0; double formedGap = double.NaN, spreadIn = double.NaN; string orderRestart = null, orderGreen = null; int ovAtGreen = -1, scLapsRun = 0; double leadV10 = double.NaN;
        string Order() => string.Join(",", Enumerable.Range(0, n).OrderByDescending(k => ds[k].lap + (double)ds[k].idx / t.N).Select(k => k.ToString()));
        double time = 0; int steps = 0, recov = 0, hits = 0; var rec = new int[n]; var where = new List<string>();
        double tMin = 999, tMaxF = 0, tMaxR = 0, tSumF = 0, tSumR = 0; int tN = 0;
        var lagged = new bool[n]; // COARSE: car k is one 240 Hz tick behind
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (scTest ? time < 3000 && (greenAt < 0 || ds.Max(d => d.lap) <= greenLap) : time < 6 * 130 && ds.Min(d => d.lap) < 5)
        {
            if (steps++ % 4 == 0)
            {
                for (int k = 0; k < n; k++) { ds[k].Think(4 * H, time, ds, null, -1, 0); if (ds[k].recovered) { recov++; rec[k]++; var dm = ds[k].car.damage; where.Add($"{(ds[k].idx >= 0 ? t.pts[ds[k].idx].s : -1):F0}{(ds[k].lap <= 1 ? "*" : "")}{(dm.Any ? $"[d fw {dm.wingFL:F1}/{dm.wingFR:F1} rw {dm.wingRear:F1} su {dm.suspFL:F1}/{dm.suspFR:F1}/{dm.suspRL:F1}/{dm.suspRR:F1} fl {dm.floor:F1}{(dm.Punctured ? " flat" : "")}]" : "")}"); } }
                if (rcx != null)
                {
                    var before = rcx.flag;
                    for (int k = 0; k < n; k++) rcx.Feed(k, ds[k]);
                    rcx.Update(4 * H, time, time > 2);
                    if (scTest)
                    {
                        if (!forced && ds.Max(d => d.lap) >= 2) { forced = true; rcx.ForceSc(); deployAt = time; }
                        if (before == FormulaSim.Flag.Sc && rcx.flag == FormulaSim.Flag.ScIn) { formedGap = rcx.maxQueueGapS; scLapsRun = rcx.scLapsDone; }
                        if (Environment.GetEnvironmentVariable("SCDEBUG") == "1" && rcx.ScOnTrack && steps % 2400 == 1) Console.WriteLine($"   t {time:F0} sc laps {rcx.scLapsDone} gaps " + string.Join(" ", Enumerable.Range(0, n).OrderBy(k => rcx.rank[k]).Select(k => $"{k}:{rcx.gapAhead[k]:F0}/{ds[k].car.vx * 3.6:F0}")));
                        if (before == FormulaSim.Flag.ScIn && rcx.flag == FormulaSim.Flag.Restart) { orderRestart = Order(); spreadIn = rcx.queueSpread; }
                        if (before == FormulaSim.Flag.Restart && rcx.flag == FormulaSim.Flag.Green && greenAt < 0) { greenAt = time; greenLap = ds.Max(d => d.lap); orderGreen = Order(); ovAtGreen = rcx.overtakes; }
                        if (greenAt > 0 && double.IsNaN(leadV10) && time > greenAt + 10) leadV10 = ds.Max(d => d.car.vx);
                    }
                }
                for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) if (Traffic.Collide(ds[i].car, ds[j].car)) hits++;
            }
            if (steps % 240 == 0) { var c0 = ds[0].car; tMin = Math.Min(tMin, Math.Min(c0.tyreTF, c0.tyreTR)); tMaxF = Math.Max(tMaxF, c0.tyreTF); tMaxR = Math.Max(tMaxR, c0.tyreTR); if (time > 60) { tSumF += c0.tyreTF; tSumR += c0.tyreTR; tN++; } }
            for (int k = 0; k < n; k++)
            {
                var d = ds[k];
                if (steps % 4 == 1) WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); if (hf != null && steps % 4 == 1) d.car.grade = hf.Grade(d.car.x, d.car.y, d.car.heading);
                // COARSE=1: the game's far-rival saving (GameManagerPerf.cs) with car 0 standing in for the player and camera
                // (as RivalCoarse: skip a tick, then 2 ticks at once; a car leaving 120 Hz while a tick behind catches up)
                bool cs = coarse && k > 0 && time > 60 && Coarse(t, ds[0], d);
                if (lagged[k]) { d.car.Step(d.LastInput, 2 * H); lagged[k] = false; }
                else if (cs) lagged[k] = true;
                else d.car.Step(d.LastInput, H);
            }
            time += H;
        }
        Console.WriteLine($"  tyres after the race (leader): front {ds[0].car.tyreTF:F0} C, rear {ds[0].car.tyreTR:F0} C; ride at the end F {ds[0].car.rideF * 1000:F0} mm R {ds[0].car.rideR * 1000:F0} mm; over the race: average F {tSumF / Math.Max(1, tN):F0} R {tSumR / Math.Max(1, tN):F0}, peak F {tMaxF:F0} R {tMaxR:F0}; wear after {ds[0].lap} laps F {ds[0].car.wearF * 100:F1}% R {ds[0].car.wearR * 100:F1}%; fuel {ds[0].car.fuel:F1} kg left, {ds[0].car.fuelLastLap:F2} kg last lap");
        Console.WriteLine($"{CircuitNames[ti]}: {n} cars, {time:F0} s raced in {sw.Elapsed.TotalSeconds:F1} s, {recov} resets, {hits} contact ticks");
        Console.WriteLine($"  resets at (m from the line, * = first racing lap): {string.Join(" ", where)}");
        if (rcx != null) Console.WriteLine($"  race control: {rcx.incidents} incidents, {rcx.vscCount} VSC, {rcx.scCount} SC, flag now {rcx.flag}, tyres {string.Join(" ", ds.Select(d => TyreSets.Letter[d.tyreSet]))}");
        if (scTest)
        {
            bool ok = greenAt > 0 && ovAtGreen == 0 && formedGap < 1.2 && orderRestart == orderGreen && leadV10 > 40;
            scRuns++; if (ok) scPass++;
            Console.WriteLine($"  SC: out at {deployAt:F0} s, {scLapsRun + 1} laps behind it, queue formed (biggest gap {formedGap:F2} s, {spreadIn:F0} m long), green at {greenAt:F0} s; overtakes while neutralised {ovAtGreen}; order kept to the line {(orderRestart == orderGreen ? "yes" : "NO")}; leader {leadV10 * 3.6:F0} km/h 10 s after the restart -> {(ok ? "PASS" : "FAIL")}");
        }
        {
            int dmgCars = ds.Count(d => d.car.damage.Any), stops = ds.Sum(d => d.pitStops), imp = ds.Sum(d => d.car.damage.hits);
            double fw = ds.Max(d => Math.Max(d.car.damage.wingFL, d.car.damage.wingFR)), su = ds.Max(d => Math.Max(Math.Max(d.car.damage.suspFL, d.car.damage.suspFR), Math.Max(d.car.damage.suspRL, d.car.damage.suspRR)));
            Console.WriteLine($"  damage: {imp} impacts, {dmgCars} cars damaged at the end, worst front wing {fw:F2}, worst suspension {su:F2}, floor {ds.Max(d => d.car.damage.floor):F2}, {ds.Count(d => d.car.damage.Punctured)} flat tyres now, {stops} pit stops, {ds.Sum(d => d.car.damage.punctures)} punctures ({ds.Sum(d => d.car.damage.kerbPunctures)} from kerbs)");
            totalResets += recov; totalStops += stops; totalHits += imp;
        }
        int pos = 1;
        foreach (var d in ds.OrderByDescending(d => d.progress))
            Console.WriteLine($"  P{pos++,-2} {d.who.name,-13} pace {d.who.pace:F2} best {d.bestLap:F2} resets {rec[ds.IndexOf(d)]}");
    }
    if (scTest) Console.WriteLine($"SAFETY CAR: {scPass} of {scRuns} circuits PASS");
    Console.WriteLine($"TOTAL (damage {(dmgFull ? "full" : "off")}, race control {(rcOn ? "on" : "off")}): {totalResets} resets, {totalHits} damaging impacts, {totalStops} pit stops");
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
    // standing start, full throttle: times to 100 / 200 / 300 km/h and the top speed
    (double t100, double t200, double t300, double top) Acc(CarConfig cc)
    {
        var car = new FormulaCar(cc); double t = 0, t100 = 0, t200 = 0, t300 = 0, top = 0;
        while (t < 60) { car.Step(new CarInput(0, 1, 0), H); t += H; double k = car.vx * 3.6; top = Math.Max(top, k); if (t100 == 0 && k >= 100) t100 = t; if (t200 == 0 && k >= 200) t200 = t; if (t300 == 0 && k >= 300) t300 = t; }
        return (t100, t200, t300, top);
    }
    // real standing starts are race starts: full tank, no MGU-K below 50 km/h, no traction control (TC Low is the
    // nearest to a driver feathering the throttle; High = perfect traction, Off = foot flat with wheelspin)
    var race = Acc(new CarConfig { fuelStart = 70 }); var raceHi = Acc(new CarConfig { fuelStart = 70, tc = TcMode.High }); var raceOff = Acc(new CarConfig { fuelStart = 70, tc = TcMode.Off });
    var (t100, t200, t300, top) = Acc(F1());
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
                c2.Step(new CarInput(st, sp < kmh / 3.6 ? 1.0 : 0.2, 0), H); // full throttle below the target: with the MGU-K faded out, 60 % no longer holds 280 km/h at 4-5 g
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
    Console.WriteLine($"  race start (70 kg fuel, TC Low): 0-100 {race.t100:F2}s (real 2026 W17 / MCL40: 2.8-2.9) | 0-200 {race.t200:F2}s (4.7-4.9) | 100-200 {race.t200 - race.t100:F2}s (2.0-2.1) | 0-300 {race.t300:F2}s (~9.9) | 200-300 {race.t300 - race.t200:F2}s (~5) | top {race.top:F0} km/h (344-347)");
    Console.WriteLine($"    same start, TC High (perfect traction): {raceHi.t100:F2} / {raceHi.t200:F2} / {raceHi.t300:F2}s; TC Off (foot flat): {raceOff.t100:F2} / {raceOff.t200:F2} / {raceOff.t300:F2}s");
    Console.WriteLine($"  qualifying trim (15 kg), TC High: 0-100 {t100:F2}s | 0-200 {t200:F2}s | 0-300 {t300:F2}s | top {top:F0} km/h, engine only {noK.vx * 3.6:F0}");
    foreach (var (k, real) in new[] { (1, "real F2: 0-100 2.9, 0-200 6.6"), (2, "real F3: 0-100 3.1, 0-200 7.8") })
    {
        var lo = CarConfig.ForClass(k); var hi = CarConfig.ForClass(k); hi.tc = TcMode.High; var a = Acc(lo); var b = Acc(hi);
        Console.WriteLine($"  {CarConfig.ClassNames[k]}: 0-100 {a.t100:F2}s, 0-200 {a.t200:F2}s (TC Low); {b.t100:F2} / {b.t200:F2}s (TC High); top {a.top:F0} km/h ({real})");
    }
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

    // ---- race detail checks: fuel, tyre surface, brake bias, wind ----
    Console.WriteLine("Race detail vs real figures:");
    { // fuel: burn per lap by the AI on a full tank, and the lap-time cost of weight on the ideal lap
        var ace = new DriverProfile { name = "ace", pace = 1, consistency = 1, aggression = 0.5 };
        var rows = new List<string>();
        foreach (var (ti, raceLaps) in new[] { (1, 78), (2, 44), (3, 53) })
        {
            var def = FormulaSim.Circuits.All[ti]; var t2 = def.Build(); var line = RacingLine.Compute(t2, new CarConfig());
            var d = new AiDriver(t2, line, new CarConfig { fuelStart = 70 }, ace, AiTuning.For(ti));
            var hf = t2.h != null ? new HeightField(t2) : null;
            double Drive(AiDriver dd, int laps)
            {
                double tt = 0; int st = 0;
                dd.Place(t2.N - 30, line.off[t2.N - 30]);
                while (dd.lap < laps && tt < 600)
                {
                    if (st++ % 4 == 0) { dd.Think(4 * H, tt, null, null, -1, 0); WheelContact.Update(t2, line, dd.car, dd.idx, dd.lateral, 1); if (hf != null) dd.car.grade = hf.Grade(dd.car.x, dd.car.y, dd.car.heading); }
                    dd.car.Step(dd.LastInput, H); tt += H;
                }
                return dd.lastLap;
            }
            Drive(d, 4);
            double light = Drive(new AiDriver(t2, line, new CarConfig { fuelStart = 15, fuelFlow = 0 }, ace, AiTuning.For(ti)), 3);
            double full = Drive(new AiDriver(t2, line, new CarConfig { fuelStart = 65, fuelFlow = 0 }, ace, AiTuning.For(ti)), 3);
            var c15 = new CarConfig(); double solver = (RacingLine.Compute(t2, c15.WithFuel(65), 1.3, 0.97, 0.95).lapTime - RacingLine.Compute(t2, c15, 1.3, 0.97, 0.95).lapTime) / 50;
            rows.Add($"{def.name} {d.car.fuelLastLap:F2} kg/lap (real 70 kg / {raceLaps} laps = {70.0 / raceLaps:F2}); 15 vs 65 kg: AI {light:F2} vs {full:F2} s = {(full - light) / 50:F3} s/kg, ideal lap {solver:F3} s/kg");
        }
        Console.WriteLine($"  fuel (real weight cost 0.03-0.04 s/kg):");
        foreach (var r in rows) Console.WriteLine("    " + r);
    }
    { // tyre surface: a locked front wheel (no ABS) spikes the surface temperature; grip comes back once it soaks in
        var lk = new FormulaCar(new CarConfig { tc = TcMode.High, abs = false }) { vx = 200 / 3.6, gear = 6, tyreTF = 95, tyreTR = 95 };
        for (int i = 0; i < 240; i++) lk.Step(new CarInput(0, 0.4, 0), H);
        double g0 = lk.gripF, s0 = lk.tyreSF;
        for (int i = 0; i < 120; i++) lk.Step(new CarInput(0, 0, 1), H);         // 0.5 s locked
        double gLock = lk.gripF, sLock = lk.tyreSF, cLock = lk.tyreTF;
        for (int i = 0; i < 240 * 2; i++) lk.Step(new CarInput(0, 0.3, 0), H);
        Console.WriteLine($"  lock-up 0.5 s at 200 km/h: front surface {s0:F0} -> {sLock:F0} C (carcass {cLock:F0} C), grip {g0:F3} -> {gLock:F3}, 2 s later surface {lk.tyreSF:F0} C grip {lk.gripF:F3}; pressure {lk.tyrePF:F1}/{lk.tyrePR:F1} psi at {lk.tyreTF:F0}/{lk.tyreTR:F0} C");
    }
    { // brake bias, no ABS, 200 km/h: raise a steady pedal until a wheel locks; which axle goes first, and is the car stable
        var parts = new List<string>();
        foreach (var (bb, eb) in new[] { (0.50, 1.0), (0.57, 1.0), (0.57, 3.0), (0.64, 1.0) })
        {
            string first = "none"; double okPedal = 0; double yaw = 0, eff = 0;
            for (double pedal = 0.20; pedal <= 1.001 && first == "none"; pedal += 0.02)
            {
                var bc2 = new FormulaCar(new CarConfig { tc = TcMode.High, abs = false, brakeBias = bb, engineBrake = eb }) { vx = 200 / 3.6, gear = 6 }; int n = 0; double y = 0;
                while (bc2.vx > 15 && n++ < 240 * 6)
                {
                    bc2.Step(new CarInput(0, 0, pedal), H); y = Math.Max(y, Math.Abs(bc2.heading));
                    if (bc2.lockR) { first = "rears"; yaw = y; break; }
                    if (bc2.lockF) { first = "fronts"; yaw = y; break; }
                    if (n == 120) eff = bc2.biasNow;
                }
                if (first == "none") okPedal = pedal;
            }
            parts.Add($"{bb * 100:F0}%{(eb != 1 ? " EB x3" : "")}: {(first == "rears" ? "REARS" : first)} lock above {okPedal * 100:F0}% pedal, road bias {eff * 100:F0}%");
        }
        Console.WriteLine($"  brake bias, no ABS from 200 km/h: {string.Join(" | ", parts)}");
    }
    { // wind: top speed with a 20 km/h head / tail wind; drift on a 250 km/h straight in a 30 km/h crosswind
        double Top(double wx) { var w = new FormulaCar(F1()) { windX = wx }; for (int i = 0; i < 240 * 60; i++) w.Step(new CarInput(0, 1, 0), H); return w.vx * 3.6; }
        var cw = new FormulaCar(F1()) { vx = 250 / 3.6, gear = 7, windY = -30 / 3.6 }; var cw0 = new FormulaCar(F1()) { vx = 250 / 3.6, gear = 7 };
        for (int i = 0; i < 240; i++) { cw.Step(new CarInput(0, 0.75, 0), H); cw0.Step(new CarInput(0, 0.75, 0), H); }
        Console.WriteLine($"  wind: top speed still air {Top(0):F0}, 20 km/h headwind {Top(-20 / 3.6):F0}, tailwind {Top(20 / 3.6):F0} km/h; 30 km/h crosswind at 250 km/h: drift {cw.y - cw0.y:F2} m and heading {cw.heading * 180 / Math.PI:F2} deg after 1 s, downforce {cw.downforce / cw0.downforce * 100 - 100:F1}%");
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
