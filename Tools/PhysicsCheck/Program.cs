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

// ---- power oversteer check: dotnet run -- spin ----
// A steady corner on flat ground at a set speed and radius (steering held by a yaw-rate controller, throttle holding
// the speed), then FULL throttle with the steering frozen for 2.5 s: does the rear step out (body slip angle grows),
// and how fast. Per class, TC Off / Low, rear grip 100 / 94 / 88 %. Pure check, changes nothing.
if (args.Length > 0 && args[0] == "spin")
{
    foreach (int cls in new[] { 0 })
    foreach (var (kmh, glat) in new[] { (70.0, 1.6), (100.0, 1.8), (130.0, 2.0), (170.0, 2.2) })
    foreach (var tc in new[] { TcMode.Off, TcMode.Low })
    foreach (double rg in new[] { 1.00, 0.94, 0.88 })
    {
        var cfg = CarConfig.ForClass(cls); cfg.tc = tc; cfg.rearGrip *= rg;
        var car = new FormulaCar(cfg);
        double v0 = kmh / 3.6, R = v0 * v0 / (glat * 9.81);
        car.vx = v0; double steer = 0.1;
        for (int i = 0; i < 240 * 6; i++)
        {
            double v = Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
            steer = Math.Clamp(steer + 1.5 * (v / R - car.r) * H, -1, 1);
            double thr = Math.Clamp(0.3 + (v0 - v) * 0.6, 0, 1), brk = Math.Clamp((v - v0 - 1) * 0.3, 0, 1);
            car.Step(new CarInput(steer, thr, brk), H);
        }
        double b0 = Math.Atan2(car.vy, car.vx) * 180 / Math.PI, maxB = 0, tSpin = double.NaN, r0 = car.r; int g0 = car.gear;
        for (int i = 0; i < 240 * 25 / 10; i++)
        {
            car.Step(new CarInput(steer, 1, 0), H);
            double b = Math.Atan2(car.vy, car.vx) * 180 / Math.PI;
            if (Math.Abs(b) > Math.Abs(maxB)) maxB = b;
            if (double.IsNaN(tSpin) && Math.Abs(b - b0) > 12) tSpin = i * H;
        }
        double vEnd = Math.Sqrt(car.vx * car.vx + car.vy * car.vy) * 3.6;
        Console.WriteLine($"{CarConfig.ClassNames[cls],-15} {kmh,4:F0} km/h R {R,5:F0} m {glat:F1} g gear {g0} TC {tc,-4} rear {rg * 100,3:F0}%: slip {b0,6:F1} -> max {maxB,6:F1} deg, {(double.IsNaN(tSpin) ? "holds" : $"steps out >12 deg after {tSpin:F2} s")}, end {vEnd,4:F0} km/h");
    }
    return;
}

// ---- straight-line braking stability: dotnet run -- brakes ----
// Full brake on a straight from a speed, with a small disturbance (a 0.03 rad/s yaw kick, or a hint of steering as a
// touch / tilt player gives). Reports the heading change, the body slip and the time the front / rear locked or ABS
// worked. ABS on / off, downforce 100 / 60 %. Pure check, changes nothing.
if (args.Length > 0 && args[0] == "brakes")
{
    foreach (double kmh in new[] { 300.0, 200.0, 120.0 })
    foreach (double df in new[] { 1.0, 0.6 })
    foreach (bool abs in new[] { true, false })
    foreach (var (dname, r0, st) in new[] { ("yaw kick", 0.03, 0.0), ("steer 3%", 0.0, 0.03) })
    {
        var cfg = CarConfig.ForClass(0); cfg.abs = abs; cfg.clA *= df; cfg.clAX *= df;
        var car = new FormulaCar(cfg) { vx = kmh / 3.6, r = r0 };
        // settle 0.5 s at speed on a trickle of throttle, then brake
        for (int i = 0; i < 120; i++) car.Step(new CarInput(0, 0.6, 0), H);
        car.r = r0; double h0 = car.heading, maxB = 0, maxDh = 0; int lf = 0, lr = 0, ab = 0, n = 0; double tStop = 0;
        for (int i = 0; i < 240 * 8 && car.vx > 15 / 3.6; i++)
        {
            car.Step(new CarInput(st, 0, 1), H); n++;
            double b = Math.Abs(Math.Atan2(car.vy, Math.Max(0.1, car.vx))) * 180 / Math.PI; if (b > maxB) maxB = b;
            double dh = Math.Abs(car.heading - h0) * 180 / Math.PI; if (dh > maxDh) maxDh = dh;
            if (car.lockF) lf++; if (car.lockR) lr++; if (car.absActive) ab++; tStop = i * H;
        }
        Console.WriteLine($"{kmh,4:F0} km/h DF {df * 100,3:F0}% ABS {(abs ? "on " : "off")} {dname,-9}: heading change {maxDh,6:F1} deg, max slip {maxB,5:F1} deg, locked F {lf * 100.0 / n,4:F0}% R {lr * 100.0 / n,4:F0}%, ABS working {ab * 100.0 / n,4:F0}%, to 15 km/h in {tStop:F2} s{(maxB > 15 ? "  ** SPUN **" : maxB > 5 ? "  * slides *" : "")}");
    }
    return;
}

// ---- bridge check: dotnet run -- bridge [circuit id, default suzuka] ----
// Where the lap crosses itself (Suzuka's figure eight) the car's ground height (HeightField.SampleAlong: the car
// model's height, the physics grade) must stay on the leg the car drives along: under the bridge the lower road, on
// it the deck. (1) a scan of both legs through the crossing, every 0.5 m along and every metre across the road, at
// the car's centre and four wheels, heading along the leg: the worst gap to the leg's own road height; (2) an AI lap
// with the wheel heights per tick through the crossing (both ways) and every jump of more than 0.3 m between ticks.
if (args.Length > 0 && args[0] == "bridge")
{
    string id = args.Length > 1 ? args[1] : "suzuka";
    var def = FormulaSim.Circuits.All.First(d => d.id == id); var t = def.Build(); var hf = new HeightField(t);
    if (!hf.HasBridges) { Console.WriteLine($"{def.name}: no bridges"); return; }
    int N = t.N; var cfg = new CarConfig();
    double bw = cfg.wheelbase * cfg.frontWeight, aw = cfg.wheelbase - bw;
    // the leg's own road height at (x, y) near track point i (the height the car should get)
    double Own(int i, double x, double y)
    {
        var p = t.pts[i]; double al = (x - p.x) * p.tx + (y - p.y) * p.ty;
        int nb = al >= 0 ? (i + 1) % N : (i - 1 + N) % N; double fa = Math.Min(1, Math.Abs(al) / t.ds);
        return t.h[i] + (t.h[nb] - t.h[i]) * fa;
    }
    // the deck points and the lower leg's point under the middle of the deck
    var deckIdx = Enumerable.Range(0, N).Where(i => hf.OnDeck(i)).ToList();
    int up = deckIdx[deckIdx.Count / 2]; int lo = -1; double bd = double.MaxValue;
    for (int i = 0; i < N; i++) { double sep = Math.Abs(i - up) * t.ds; sep = Math.Min(sep, t.length - sep); if (sep < 100) continue; double d = Math.Pow(t.pts[i].x - t.pts[up].x, 2) + Math.Pow(t.pts[i].y - t.pts[up].y, 2); if (d < bd) { bd = d; lo = i; } }
    double ang = Math.Acos(Math.Abs(t.pts[up].tx * t.pts[lo].tx + t.pts[up].ty * t.pts[lo].ty)) * 180 / Math.PI;
    Console.WriteLine($"{def.name}: {deckIdx.Count} deck points ({t.pts[deckIdx[0]].s:F0}-{t.pts[deckIdx[^1]].s:F0} m), upper leg {t.pts[up].s:F0} m at {t.h[up]:F2} m, lower leg {t.pts[lo].s:F0} m at {t.h[lo]:F2} m, crossing angle {ang:F0} deg");
    // headings up to 25 deg off the leg's own direction (a car on the racing line, sliding, catching a slide)
    foreach (var (nm, c0) in new[] { ("lower leg (under the bridge)", lo), ("upper leg (over the bridge)", up) })
        foreach (bool hinted in new[] { false, true })
        {
            double worst = 0; string at = ""; int bad = 0, n = 0;
            for (double s = -60; s <= 60; s += 0.5)
                for (double lat = -t.width / 2 - 1; lat <= t.width / 2 + 1.001; lat += 1)
                    for (double yaw = -25; yaw <= 25.001; yaw += 5)
                    {
                        int i = ((c0 + (int)Math.Floor(s / t.ds)) % N + N) % N; double fr = s / t.ds - Math.Floor(s / t.ds); var p = t.pts[i]; var q = t.pts[(i + 1) % N];
                        double hd = Math.Atan2(p.ty, p.tx) + yaw * Math.PI / 180, ch = Math.Cos(hd), sh = Math.Sin(hd);
                        double cx = p.x + (q.x - p.x) * fr + p.nx * lat, cy = p.y + (q.y - p.y) * fr + p.ny * lat;
                        for (int w = -1; w < 4; w++)
                        {
                            double along = w < 0 ? 0 : w < 2 ? aw : -bw, side = w < 0 ? 0 : (w % 2 == 0 ? 1 : -1) * cfg.track / 2;
                            double x = cx + ch * along - sh * side, y = cy + sh * along + ch * side;
                            var nr = t.Nearest(x, y, c0); int leg = i;
                            double e = Math.Abs((hinted ? hf.SampleAlong(x, y, ch, sh, ref leg) : hf.SampleAlong(x, y, ch, sh)) - Own(nr.idx, x, y)); n++;
                            if (e > 0.25) bad++;
                            if (e > worst) { worst = e; at = $"{s:+0.0;-0.0} m along, lat {lat:+0;-0}, yaw {yaw:+0;-0} deg, wheel {w}"; }
                        }
                    }
            Console.WriteLine($"  scan {nm}, {(hinted ? "leg kept  " : "heading only")}: {bad} of {n} samples off their own road by > 0.25 m; worst {worst:F2} m{(worst > 0.25 ? $" ({at})" : "")}");
        }
    // an AI lap: wheel heights through the crossing, (a) the leg from the heading alone (skid marks), (b) with the
    // car's leg kept (the car model, the physics grade from the car's track index)
    var line = RacingLine.Compute(t, new CarConfig());
    bool verbose = Environment.GetEnvironmentVariable("BRIDGEV") == "1";
    foreach (bool hinted in new[] { false, true })
    {
        var ai = new AiDriver(t, line, new CarConfig(), new DriverProfile { name = "ace", pace = 1, consistency = 1, aggression = 0.5 }, AiTuning.For(Array.IndexOf(FormulaSim.Circuits.All, def)));
        ai.Place(t.N - 30, line.off[t.N - 30]);
        double tt = 0; int st = 0, jumps = 0; double maxJump = 0, maxGrade = 0; var prev = new double[4]; var legs = new[] { -1, -1, -1, -1 }; bool have = false;
        Console.WriteLine($"  AI lap, {(hinted ? "leg kept (car model, physics grade)" : "leg from the heading only")}:");
        while (ai.lap < 2 && tt < 300)
        {
            if (st++ % 4 == 0) { ai.Think(4 * H, tt, null, null, -1, 0); WheelContact.Update(t, line, ai.car, ai.idx, ai.lateral, 1); ai.car.grade = hinted ? hf.GradeAlong(ai.car.x, ai.car.y, ai.car.heading, ai.idx) : hf.GradeAlong(ai.car.x, ai.car.y, ai.car.heading); }
            ai.car.Step(ai.LastInput, H); tt += H;
            var c = ai.car; double ch = Math.Cos(c.heading), sh = Math.Sin(c.heading);
            var wh = new double[4];
            for (int w = 0; w < 4; w++)
            {
                double along = w < 2 ? aw : -bw, side = (w % 2 == 0 ? 1 : -1) * cfg.track / 2, x = c.x + ch * along - sh * side, y = c.y + sh * along + ch * side;
                wh[w] = hinted ? hf.SampleAlong(x, y, ch, sh, ref legs[w]) : hf.SampleAlong(x, y, ch, sh);
            }
            double sep1 = Math.Abs(ai.idx - lo) * t.ds, sep2 = Math.Abs(ai.idx - up) * t.ds;
            bool near = Math.Min(sep1, t.length - sep1) < 40 || Math.Min(sep2, t.length - sep2) < 40;
            if (near) maxGrade = Math.Max(maxGrade, Math.Abs(c.grade));
            if (have)
                for (int w = 0; w < 4; w++)
                {
                    double j = Math.Abs(wh[w] - prev[w]); maxJump = Math.Max(maxJump, j);
                    if (j > 0.3) { jumps++; if (jumps <= 12) Console.WriteLine($"    JUMP t {tt:F3} s at {t.pts[ai.idx].s:F0} m lat {ai.lateral:+0.0;-0.0} wheel {w}: {prev[w]:F2} -> {wh[w]:F2} m"); }
                }
            if (near && (verbose || st % 24 == 0)) Console.WriteLine($"    t {tt,7:F3} s {t.pts[ai.idx].s,5:F0} m lat {ai.lateral,5:+0.0;-0.0} own {t.h[ai.idx],6:F2}  wheels FL {wh[0],6:F2} FR {wh[1],6:F2} RL {wh[2],6:F2} RR {wh[3],6:F2}  grade {c.grade * 100,5:F1}%");
            Array.Copy(wh, prev, 4); have = true;
        }
        Console.WriteLine($"    -> {jumps} wheel-height jumps > 0.3 m between ticks (biggest step {maxJump:F3} m), steepest grade at the crossing {maxGrade * 100:F1}%, lap {ai.lastLap:F2} s");
    }
    return;
}

// ---- raised kerbs: dotnet run -- kerbs ----
// Per circuit: the kerbed corners (tightest point within 40 m, R < 280 m), the raised kerb stretches (Track.KerbLift,
// chosen in CircuitDef.Layout), the lift profile across a raised kerb, and an AI lap with and without them (lap time,
// share of the time a tyre rides a raised kerb, highest lift seen).
if (args.Length > 0 && args[0] == "kerbs")
{
    var prof = FormulaSim.Circuits.Suzuka.Build(); int pi = Array.FindIndex(prof.raiseL, r => r >= 1);
    Console.WriteLine($"lift across a raised kerb (left, full height), m from the road edge: " + string.Join(" ", new[] { -0.2, 0, 0.35, 0.7, 1.05, 1.4, 1.5 }.Select(u => $"{u:F2}:{prof.KerbLift(pi, prof.HalfWidth(pi) + u) * 100:F1}cm")));
    { // the car on it: the most steady cornering g at 150 km/h in a right-hander, the left (outside) tyres on a kerb lifted 0 / 2.5 / 5 cm
        var parts = new List<string>();
        foreach (double lift in new[] { 0.0, 0.025, 0.05 })
        {
            double best = 0, shake = 0;
            for (double stv = 0.05; stv <= 1.0; stv += 0.05)
            {
                var car = new FormulaCar(new CarConfig { tc = TcMode.High }) { vx = 150 / 3.6, gear = 5 }; double gs = 0, vb = 0; int n = 0; bool ok = true;
                for (int i = 0; i < 240 * 3; i++)
                {
                    for (int w = 0; w < 4; w++) car.SetWheelSurface(w, w % 2 == 0 ? SurfaceType.Kerb : SurfaceType.Asphalt, w % 2 == 0 ? 0.9 : 1, w % 2 == 0 ? 0.004 : 0, w % 2 == 0 ? 0.008 : 0);
                    car.wLift[0] = car.wLift[2] = lift;
                    double v = Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
                    car.Step(new CarInput(-stv, v < 150 / 3.6 ? 1.0 : 0.2, 0), H);
                    if (Math.Abs(Math.Atan2(car.vy, car.vx)) > 0.3) { ok = false; break; }
                    if (i > 240 * 2) { gs += Math.Abs(car.ay) / 9.81; vb += car.vib; n++; }
                }
                if (ok && n > 0 && gs / n > best) { best = gs / n; shake = vb / n; }
            }
            parts.Add($"{lift * 100:F1} cm: {best:F2} g, shake {shake:F2}");
        }
        Console.WriteLine($"most steady g at 150 km/h, the outside tyres on a kerb lifted: {string.Join(" | ", parts)}");
    }
    double sumDiff = 0;
    for (int ci = 0; ci < FormulaSim.Circuits.All.Length; ci++)
    {
        var def = FormulaSim.Circuits.All[ci]; var t = def.Build(); int N = t.N, W = (int)Math.Round(40 / t.ds);
        int kerbed = 0, slowMed = 0;
        for (int i = 0; i < N; i++)
        {
            double c = Math.Abs(t.pts[i].curv); if (c < 1.0 / 280) continue; bool peak = true;
            for (int k = -W; k <= W && peak; k++) { if (k == 0) continue; double c2 = Math.Abs(t.pts[((i + k) % N + N) % N].curv); if (c2 > c || (c2 == c && k < 0)) peak = false; }
            if (!peak) continue; kerbed++; if (1 / c >= CircuitDef.RaiseRmin && 1 / c <= CircuitDef.RaiseRmax) slowMed++;
        }
        var runs = new List<string>();
        if (t.raiseL != null)
            foreach (var (nm, a) in new[] { ("L", t.raiseL), ("R", t.raiseR) })
            {
                int start = Array.FindIndex(a, r => r <= 0); if (start < 0) start = 0;
                for (int q = 0; q < N; q++)
                {
                    int i = (start + q) % N; if (a[i] <= 0 || a[(i - 1 + N) % N] > 0) continue;
                    int j = i; while (a[(j + 1) % N] > 0) j = (j + 1) % N;
                    runs.Add($"{nm} {t.pts[i].s:F0}-{t.pts[j].s:F0}");
                }
            }
        // AI laps with and without the raised kerbs
        var line = RacingLine.Compute(t, new CarConfig());
        (double lap, double share, double top) Lap(bool raised)
        {
            var rl = t.raiseL; var rr = t.raiseR; if (!raised) { t.raiseL = null; t.raiseR = null; }
            var hf = t.h != null ? new HeightField(t) : null;
            var d = new AiDriver(t, line, new CarConfig(), new DriverProfile { name = "ace", pace = 1, consistency = 1, aggression = 0.5 }, AiTuning.For(ci));
            d.Place(N - 30, line.off[N - 30]); double tt = 0; int st = 0, on = 0, all = 0; double top = 0;
            while (d.lap < 3 && tt < 600)
            {
                if (st++ % 4 == 0) { d.Think(4 * H, tt, null, null, -1, 0); WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); if (hf != null) d.car.grade = hf.GradeAlong(d.car.x, d.car.y, d.car.heading, d.idx); }
                d.car.Step(d.LastInput, H); tt += H;
                if (d.lap >= 1) { all++; double m = d.car.wLift.Max(); if (m > 0) on++; top = Math.Max(top, m); }
            }
            t.raiseL = rl; t.raiseR = rr;
            return (d.lastLap, (double)on / Math.Max(1, all), top);
        }
        var a1 = Lap(false); var a2 = Lap(true); sumDiff += Math.Abs(a2.lap - a1.lap);
        Console.WriteLine($"{def.name}: {kerbed} kerbed corners ({t.kerbCorners}), {slowMed} medium/slow, {t.raisedCorners} raised ({100.0 * t.raisedCorners / Math.Max(1, kerbed):F0}%); stretches (side, m from the line): {string.Join(", ", runs)}");
        Console.WriteLine($"    AI lap flat kerbs {a1.lap:F3} s, raised {a2.lap:F3} s ({a2.lap - a1.lap:+0.000;-0.000}); a tyre on a raised kerb {a2.share * 100:F1}% of the time, highest lift {a2.top * 100:F1} cm");
    }
    Console.WriteLine($"sum of |lap change| {sumDiff:F3} s");
    return;
}

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

// ---- standstill check: dotnet run -- standstill [csv dir] ----
// The car at rest or creeping (0-3 km/h), throttle zero, brake on or off, the steering held / swept to full lock.
// A real car does not move or turn there (static friction holds it): only the front wheels turn. Prints how far
// the whole car moved and turned; with a directory, writes every step (position, yaw, velocities, wheel speed,
// slip angles, forces) to CSV. Then low-speed turning circles (5-15 km/h) as a check of the transition.
if (args.Length > 0 && args[0] == "standstill")
{
    var inv = CultureInfo.InvariantCulture;
    string csvDir = args.Length > 1 ? args[1] : null; if (csvDir != null) Directory.CreateDirectory(csvDir);
    // steering profiles over time (s): + = right
    var profiles = new (string name, Func<double, double> steer)[]
    {
        ("hold right", t => 1),
        ("hold left", t => -1),
        ("sweep R and back", t => t < 1.5 ? 1 : 0),
        ("saw L-R 1 Hz", t => Math.Sin(2 * Math.PI * t) > 0 ? 1 : -1),
    };
    double worstMove = 0, worstYaw = 0;
    foreach (var cls in new[] { 0, 1, 2 })
        foreach (var kmh in new[] { 0.0, 1.0, 3.0 })
            foreach (var brk in new[] { 0.0, 1.0 })
                foreach (var (pname, steer) in profiles)
                {
                    var car = new FormulaCar(CarConfig.ForClass(cls)) { vx = kmh / 3.6 };
                    double T = 4, x0 = car.x, y0 = car.y, maxVy = 0, maxR = 0, maxAy = 0, maxAF = 0, maxAR = 0, side = 0, yawAtStop = double.NaN;
                    // the brake is held 0.55 s (after 0.6 s at rest the brake pedal selects reverse, as designed), then released
                    StreamWriter w = csvDir != null && cls == 0 ? new StreamWriter(Path.Combine(csvDir, $"standstill_{kmh:F0}kmh_brake{brk:F0}_{pname.Replace(' ', '_')}.csv")) : null;
                    w?.WriteLine("t,x,y,heading_deg,vx,vy,r,delta_deg,steerIn,wR,alphaF_deg,alphaR_deg,ay,ax,Fy_total,useF,useR,reverse");
                    for (int i = 0; i < (int)(T / H); i++)
                    {
                        double t = i * H;
                        car.Step(new CarInput(steer(t), 0, t < 0.55 ? brk : 0), H);
                        // sideways = displacement across the car's starting heading (0): world y
                        maxVy = Math.Max(maxVy, Math.Abs(car.vy)); maxR = Math.Max(maxR, Math.Abs(car.r)); maxAy = Math.Max(maxAy, Math.Abs(car.ay));
                        maxAF = Math.Max(maxAF, Math.Abs(car.alphaF)); maxAR = Math.Max(maxAR, Math.Abs(car.alphaR));
                        if (double.IsNaN(yawAtStop) && Math.Abs(car.vx) < 0.01 && kmh > 0) yawAtStop = car.heading;
                        w?.WriteLine(string.Join(",", new[] { t, car.x, car.y, car.heading * 180 / Math.PI, car.vx, car.vy, car.r, car.delta * 180 / Math.PI, car.steerIn, car.wR, car.alphaF * 180 / Math.PI, car.alphaR * 180 / Math.PI, car.ay, car.ax, car.ay * car.massNow, car.useF, car.useR, car.reverse ? 1 : 0 }.Select(v => v.ToString("G6", inv))));
                    }
                    w?.Dispose();
                    side = car.y - y0;
                    double fwd = car.x - x0, yaw = car.heading * 180 / Math.PI;
                    // at rest: any movement is creep. Creeping in: the car may roll forward a little and turn by the steering
                    // geometry (kinematic arc); report sideways slip of the body (max |vy|) and the yaw after it stopped
                    double yawAfter = double.IsNaN(yawAtStop) ? double.NaN : (car.heading - yawAtStop) * 180 / Math.PI;
                    if (kmh == 0) { worstMove = Math.Max(worstMove, Math.Sqrt(fwd * fwd + side * side)); worstYaw = Math.Max(worstYaw, Math.Abs(yaw)); }
                    else if (!double.IsNaN(yawAfter)) worstYaw = Math.Max(worstYaw, Math.Abs(yawAfter));
                    Console.WriteLine($"  {CarConfig.ClassNames[cls],-9} {kmh,3:F0} km/h brake {brk:F0} {pname,-17}: fwd {fwd * 100,7:F1} cm, side {side * 100,7:F1} cm, yaw {yaw,6:F2} deg (after stop {yawAfter,6:F2}), max |vy| {maxVy,6:F3} m/s, |r| {maxR,6:F3} rad/s, |ay| {maxAy,5:F2} m/s2, |alphaF| {maxAF * 180 / Math.PI,5:F1}, |alphaR| {maxAR * 180 / Math.PI,5:F1} deg, end v {Math.Sqrt(car.vx * car.vx + car.vy * car.vy) * 100:F1} cm/s{(car.reverse ? " [reverse]" : "")}");
                }
    Console.WriteLine($"STANDSTILL worst: movement from rest {worstMove * 100:F2} cm, yaw {worstYaw:F3} deg");
    // a shove at low speed (a nudge from another car, a slow spin): the tyres must stop it like friction, without
    // springing back or ringing
    foreach (var (name, vx0, vy0, r0) in new[] { ("pushed sideways 1 m/s", 0.0, 1.0, 0.0), ("pushed sideways 3 m/s", 0.0, 3.0, 0.0), ("spinning 1 rad/s at rest", 0.0, 0.0, 1.0), ("2 m/s + 1.5 rad/s spin", 2.0, 0.0, 1.5) })
    {
        var car = new FormulaCar(new CarConfig()) { vx = vx0, vy = vy0, r = r0 };
        int flips = 0; double lastVy = vy0, tStop = double.NaN;
        for (int i = 0; i < 240 * 4; i++)
        {
            car.Step(new CarInput(0, 0, 0), H);
            if (Math.Sign(car.vy) != Math.Sign(lastVy) && Math.Abs(car.vy) > 0.01) { flips++; lastVy = car.vy; }
            if (double.IsNaN(tStop) && Math.Sqrt(car.vx * car.vx + car.vy * car.vy) < 0.02 && Math.Abs(car.r) < 0.01) tStop = i * H;
        }
        Console.WriteLine($"  {name,-25}: moved {Math.Sqrt(car.x * car.x + car.y * car.y) * 100,6:F1} cm, turned {car.heading * 180 / Math.PI,6:F2} deg, still after {tStop:F2} s, vy sign flips {flips}, end v {Math.Sqrt(car.vx * car.vx + car.vy * car.vy) * 100:F2} cm/s r {car.r:F4}");
    }
    // the transition: steady full-lock circles at walking to parking speeds (radius from v / r), held speed
    foreach (var kmh in new[] { 2.0, 5.0, 8.0, 10.0, 11.0, 12.0, 15.0, 20.0 })
    {
        var car = new FormulaCar(new CarConfig()) { vx = kmh / 3.6 };
        for (int i = 0; i < 240 * 4; i++) { double v = Math.Sqrt(car.vx * car.vx + car.vy * car.vy); car.Step(new CarInput(1, Math.Clamp((kmh / 3.6 - v) * 0.8, 0, 1), Math.Clamp((v - kmh / 3.6) * 0.8, 0, 1)), H); }
        double sp = Math.Sqrt(car.vx * car.vx + car.vy * car.vy);
        Console.WriteLine($"  full lock right at {kmh,4:F0} km/h: radius {(Math.Abs(car.r) > 1e-6 ? sp / Math.Abs(car.r) : 0),7:F3} m, slip angle body {Math.Atan2(car.vy, car.vx) * 180 / Math.PI,7:F3} deg, alphaF {car.alphaF * 180 / Math.PI,7:F3} alphaR {car.alphaR * 180 / Math.PI,7:F3}, x {car.x:F4} y {car.y:F4}");
    }
    return;
}

// ---- race start check: dotnet run -- start [csv dir] ----
// (1) Grid hold: every grid slot of every circuit (the game's StartGrid layout, slope from the HeightField), the car
// held as GameManagerStart holds it (brake 1, throttle 0, ~8 s; rivals the same plus their reaction time) and with no
// input at all. A car must not move: prints the worst roll (cm), whether reverse got selected, the slope.
// (2) Launch: from rest (grid clutch ramp and a plain stop) and from a rolling stop, full and part throttle: speed
// and the drive force over time, so a dead gap ("power drops, then kicks in") shows as a dip in the acceleration.
if (args.Length > 0 && args[0] == "start")
{
    var inv = CultureInfo.InvariantCulture;
    string csvDir = args.Length > 1 ? args[1] : null; if (csvDir != null) Directory.CreateDirectory(csvDir);
    const double LaunchRamp = 0.35;
    double Clutch(double t) => t <= 0 ? 0 : t >= LaunchRamp ? 1 : t / LaunchRamp;
    // the game's slot pose (StartGrid.Pose; Offset = CircuitDef.gridOffset like Track.GridIndex)
    (double x, double y, double h) SlotPose(Track t, int k, bool useDef)
    {
        double side = 1; int n = Math.Min(t.N, (int)(1500 / t.ds));
        for (int i = 0; i < n; i++) { double c = t.pts[i].curv; if (Math.Abs(c) > 1.0 / 300) { side = c > 0 ? -1 : 1; break; } }
        double off = useDef && t.def != null ? t.def.gridOffset : 0;   // the game: StartGrid.Offset = 0 for now
        double s = t.length - off - k * 8 - 2.5, f = s / t.ds;
        int i0 = (int)Math.Floor(f); double a = f - i0; i0 = ((i0 % t.N) + t.N) % t.N; int i1 = (i0 + 1) % t.N;
        var p = t.pts[i0]; var q = t.pts[i1]; double lat = (k % 2 == 0 ? 1 : -1) * side * 2.5;
        return (p.x + (q.x - p.x) * a + (p.nx + (q.nx - p.nx) * a) * lat, p.y + (q.y - p.y) * a + (p.ny + (q.ny - p.ny) * a) * lat, Math.Atan2(p.ty + (q.ty - p.ty) * a, p.tx + (q.tx - p.tx) * a));
    }
    // (1) grid hold
    Console.WriteLine("Grid hold (8 s on the brakes as GameManagerStart holds the cars, then lights out):");
    double worstHold = 0, worstFree = 0, worstSlope = 0; int reverses = 0;
    var tracks = Circuits();
    for (int ti = 0; ti < tracks.Length; ti++)
    {
        var t = tracks[ti]; var hf = t.h != null || t.bank != null ? new HeightField(t) : null;
        double tHold = 0, tFree = 0, maxG = 0; int rev = 0; string worstSlot = "";
        for (int k = 0; k < 44; k++)
        {
            var (x, y, hd) = SlotPose(t, k % 22, k >= 22);
            double gr = hf != null ? hf.Grade(x, y, hd) : 0; maxG = Math.Max(maxG, Math.Abs(gr));
            foreach (var held in new[] { true, false })
            {
                var car = new FormulaCar(new CarConfig { fuelStart = 70 }) { x = x, y = y, heading = hd };
                bool sawRev = false; double maxV = 0;
                for (int i = 0; i < (int)(8.0 / H); i++)
                {
                    car.grade = hf != null ? hf.Grade(car.x, car.y, car.heading) : 0;
                    car.gridHold = held;
                    car.Step(held ? new CarInput(0, 0, 1) : new CarInput(0, 0, 0), H);
                    sawRev |= car.reverse; maxV = Math.Max(maxV, Math.Abs(car.vx));
                }
                double d = Math.Sqrt((car.x - x) * (car.x - x) + (car.y - y) * (car.y - y));
                if (held) { if (d > tHold) worstSlot = $"slot {k % 22}{(k >= 22 ? " (gridOffset)" : "")} grade {gr * 100:+0.00;-0.00}%: {d * 100:F1} cm max {maxV:F2} m/s"; tHold = Math.Max(tHold, d); if (sawRev) rev++; }
                else tFree = Math.Max(tFree, d);
            }
        }
        worstHold = Math.Max(worstHold, tHold); worstFree = Math.Max(worstFree, tFree); worstSlope = Math.Max(worstSlope, maxG); reverses += rev;
        Console.WriteLine($"  {FormulaSim.Circuits.All[ti].name,-14} grid slope up to {maxG * 100:F2}%: held on brakes worst {tHold * 100:F2} cm ({worstSlot}), reverse selected in {rev}/44 slots (game layout + CircuitDef.gridOffset layout); no input worst {tFree * 100:F1} cm");
    }
    // steep test: the brakes must hold on any slope the tyres can (static friction)
    foreach (var gr in new[] { 0.05, 0.10, 0.20, -0.10 })
    {
        var car = new FormulaCar(new CarConfig()) { grade = gr };
        for (int i = 0; i < (int)(6.0 / H); i++) { car.gridHold = true; car.Step(new CarInput(0, 0, 1), H); }
        var car2 = new FormulaCar(new CarConfig()) { grade = gr };
        for (int i = 0; i < (int)(6.0 / H); i++) car2.Step(new CarInput(0, 0, i * H < 0.5 ? 1 : 0), H);
        Console.WriteLine($"  slope {gr * 100,4:F0}%: brake held 6 s -> moved {car.x * 100:F2} cm; brake 0.5 s then released -> {car2.x * 100:F0} cm");
    }
    Console.WriteLine($"GRID worst: held {worstHold * 100:F2} cm, reverse selected {reverses}x, steepest grid slope {worstSlope * 100:F2}%");

    // (2) launch
    Console.WriteLine("Launch (flat, race fuel 70 kg):");
    void Launch(string name, TcMode tc, double thrSet, bool gridClutch, double startKmh, double brakeHold)
    {
        var car = new FormulaCar(new CarConfig { fuelStart = 70, tc = tc }) { vx = startKmh / 3.6 };
        if (startKmh > 0) { car.gear = 2; car.standingStart = false; }
        StreamWriter w = csvDir != null ? new StreamWriter(Path.Combine(csvDir, $"launch_{new string(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray())}.csv")) : null;
        w?.WriteLine("t,kmh,wheel_kmh,kap,rpm,gear,boost,thr,fdrive,FxR,ax_g,ersKw,tc,reverse");
        double t = 0, tGo = double.NaN, t100 = double.NaN, minAx = 99, peakAx = 0, axPrev = 0; int dips = 0; double worstDrop = 0, dropAt = 0;
        var trace = new List<string>();
        bool going = false; double axMaxSoFar = 0, minAxMid = 99, kick = 0; var axHist = new Queue<double>();
        for (int i = 0; i < (int)(12.0 / H); i++, t += H)
        {
            // phase: brake to rest (rolling stop) or hold, then go
            double thr = 0, brk = 0;
            if (!going)
            {
                if (startKmh > 0 && car.vx > (brakeHold < 0 ? 5 / 3.6 : 0.4) && double.IsNaN(tGo)) brk = 1;
                else { if (double.IsNaN(tGo)) tGo = t + Math.Max(0, brakeHold); brk = t < tGo ? 1 : 0; if (t >= tGo) going = true; }
            }
            if (going)
            {
                double tg = t - tGo;
                thr = gridClutch ? thrSet * Clutch(tg) : thrSet;
            }
            car.Step(new CarInput(0, thr, brk), H);
            if (!going) continue;
            double tg2 = t - tGo, kmh = car.vx * 3.6, axg = car.ax / 9.81;
            if (double.IsNaN(t100) && kmh >= 100) t100 = tg2;
            // the acceleration should rise and then fall smoothly; a drop of more than 0.25 g below the best so far
            // before 100 km/h, that later recovers, is a power hole
            axHist.Enqueue(axg); if (axHist.Count > 24) axHist.Dequeue();   // 0.1 s window
            if (tg2 > 0.5 && kmh < 100) { kick = Math.Max(kick, axg - axHist.Min()); minAxMid = Math.Min(minAxMid, axg); }
            if (tg2 > 0.4 && kmh < 100) { axMaxSoFar = Math.Max(axMaxSoFar, axg); double drop = axMaxSoFar - axg; if (drop > worstDrop) { worstDrop = drop; dropAt = kmh; } }
            if (i % 12 == 0) w?.WriteLine(string.Join(",", new[] { tg2, kmh, car.wR * car.cfg.wheelRadius * 3.6, car.slipRatioR, car.rpm, car.gear, car.boost, thr, car.Fdrive, car.FxR, axg, car.ersKw, car.tcActive ? 1 : 0, car.reverse ? 1 : 0 }.Select(v => v.ToString("G5", inv))));
            if (Math.Abs(tg2 - Math.Round(tg2 / 0.25) * 0.25) < H / 2 && tg2 <= 3.01) trace.Add($"{kmh,3:F0}/{axg:F2}");
            if (tg2 > 6) break;
        }
        w?.Dispose();
        Console.WriteLine($"  {name,-34} 0-100 {t100,5:F2}s  min {minAxMid:F2} g, kick {kick:F2} g/0.1s | km/h/g every 0.25 s: {string.Join(" ", trace)}");
    }
    foreach (var tc in new[] { TcMode.Low, TcMode.High, TcMode.Off })
    {
        Launch($"grid clutch full TC {tc}", tc, 1.0, true, 0, 0.5);
        Launch($"stop then full TC {tc}", tc, 1.0, false, 0, 1.0);
    }
    Launch("stop then 60% TC Low", TcMode.Low, 0.6, false, 0, 1.0);
    Launch("stop then 30% TC Low", TcMode.Low, 0.3, false, 0, 1.0);
    Launch("stop (0.2 s) then full TC Low", TcMode.Low, 1.0, false, 0, 0.2);
    Launch("60 km/h, brake to rest, full", TcMode.Low, 1.0, false, 60, 0.3);
    Launch("60 km/h, brake to rest 2 s, full", TcMode.Low, 1.0, false, 60, 2.0);
    Launch("60 km/h, brake to rest, 50%", TcMode.Low, 0.5, false, 60, 0.3);
    Launch("60 km/h, brake to 5 km/h, full", TcMode.Low, 1.0, false, 60, -1);
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
        var ccf = new CarConfig(); // DF=0.6: downforce scaled (the in-game "Test: downforce" slider)
        if (Environment.GetEnvironmentVariable("DF") is string dfs) { double df = double.Parse(dfs, inv); ccf.clA *= df; ccf.clAX *= df; }
        var t = def.Build(); var line = RacingLine.Compute(t, ccf, 1.3, 0.97, 0.95);
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
            if (hf != null && steps % 4 == 1) d.car.grade = hf.GradeAlong(d.car.x, d.car.y, d.car.heading, d.idx);
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
        foreach (var d in ds) { if (steps % 4 == 1) WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); if (hf != null && steps % 4 == 1) d.car.grade = hf.GradeAlong(d.car.x, d.car.y, d.car.heading, d.idx); d.car.Step(d.LastInput, H); }
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
            if (steps++ % 4 == 0) { d.Think(4 * H, time, null, null, -1, 0); WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); if (hf != null) d.car.grade = hf.GradeAlong(d.car.x, d.car.y, d.car.heading, d.idx); }
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

// ---- compounds: dotnet run -c Release -- tyres [circuit] [wear mult] ----
// One AI car alone per compound on fixed fuel with the game's "Tyre wear" setting applied (TyreRules.Apply):
//  pace: the best of laps 2-3 on fresh tyres (no wear), the gap to the Soft;
//  out-lap: lap 1 from a rolling start on cold tyres (the blanket temperature) against the same lap on tyres at their
//  best temperature; then a stint at the wear multiplier until the tyres are gone: lap times, wear, carcass temperature
//  and the lowest grip factor of each lap (temperature x compound x wear).
if (args.Length > 0 && args[0] == "tyres")
{
    var inv = CultureInfo.InvariantCulture;
    string cid = args.Length > 1 ? args[1] : "suzuka";
    double mult = args.Length > 2 ? double.Parse(args[2], inv) : 6;
    int ti = Array.FindIndex(FormulaSim.Circuits.All, d => d.id == cid); var def = FormulaSim.Circuits.All[ti]; var t2 = def.Build();
    var line = RacingLine.Compute(t2, new CarConfig()); var hf = t2.h != null ? new HeightField(t2) : null;
    var ace = new DriverProfile { name = "ace", pace = 1, consistency = 1, aggression = 0.5 };
    TyreRules.Strong = true;
    // exploring: TLK=2 (temperature loss x), HS=0.39,0.45,0.51 (heat share S,M,H)
    if (System.Environment.GetEnvironmentVariable("TLK") is string tlk) TyreRules.TempLossK = double.Parse(tlk, inv);
    if (System.Environment.GetEnvironmentVariable("HC") is string hc) TyreRules.HeatCapK = double.Parse(hc, inv);
    if (System.Environment.GetEnvironmentVariable("HS") is string hs) { var v = hs.Split(','); for (int k = 0; k < 3; k++) TyreRules.HeatShare[k] = double.Parse(v[k], inv); }
    CarConfig Cfg(int s, double m, double start)
    {
        var c = new CarConfig { fuelStart = 30, fuelFlow = 0, compound = (Compound)s };
        TyreRules.Apply(c, m, true); TyreRules.Fit(c, s); if (!double.IsNaN(start)) c.tyreStart = start;
        return c;
    }
    // drive laps from a rolling start just before the line: (lap time, worse-axle wear, mean carcass C, lowest grip)
    List<(double t, double w, double tc, double g)> Drive(int s, double m, double start, int maxLaps)
    {
        var d = new AiDriver(t2, line, Cfg(s, m, start), ace, AiTuning.For(ti)); var res = new List<(double, double, double, double)>();
        int i0 = t2.N - 40; d.Place(i0, line.off[i0], line.speed[i0] * 0.7);
        double tt = 0, gMin = 2; int st = 0, lapSeen = d.lap;
        while (d.lap <= maxLaps && tt < maxLaps * 200)
        {
            if (st++ % 4 == 0)
            {
                d.Think(4 * H, tt, null, null, -1, 0); WheelContact.Update(t2, line, d.car, d.idx, d.lateral, 1); if (hf != null) d.car.grade = hf.Grade(d.car.x, d.car.y, d.car.heading);
                if (d.lap != lapSeen) { if (lapSeen >= 1) res.Add((d.lastLap, Math.Max(d.car.wearF, d.car.wearR), 0.5 * (d.car.tyreTF + d.car.tyreTR), gMin)); lapSeen = d.lap; gMin = 2; if (Math.Max(d.car.wearF, d.car.wearR) > 0.97) break; }
                if (d.lap >= 1) gMin = Math.Min(gMin, Math.Min(d.car.gripF, d.car.gripR));
            }
            d.car.Step(d.LastInput, H); tt += H;
        }
        return res;
    }
    var c0 = Cfg(0, mult, double.NaN);
    Console.WriteLine($"{def.name}: ideal lap {line.lapTime:F2} s, wear x{mult:0.#} (Soft {TyreStrategy.BaseSoftRate(cid) * mult * 100:F0}%/lap), tyreLoss {c0.tyreLoss:F3}, blanket {c0.tyreStart:F0} C");
    Console.WriteLine($"  set  pace (gap)        out-lap cold/warm (cost)  | stint at x{mult:0.#}: lap time / wear % / carcass C / min grip");
    double soft = 0;
    for (int s = 0; s < 3; s++)
    {
        var cmp = CompoundTable.Defs[s];
        var fresh = Drive(s, 1e-3, cmp.opt, 3); double pace = Math.Min(fresh[1].t, fresh[2].t); if (s == 0) soft = pace;
        var cold = Drive(s, 1e-3, double.NaN, 1); var warm = Drive(s, 1e-3, cmp.opt, 1);
        var run = Drive(s, mult, double.NaN, 40);
        if (System.Environment.GetEnvironmentVariable("TYREDEBUG") == "1") Console.WriteLine("      fresh: " + string.Join("  ", fresh.Select(r => $"{r.t:F2}/{r.tc:F0}/{r.g:F3}")));
        int cliff = run.FindIndex(r => r.w >= TyreStrategy.CliffWear) + 1;
        Console.WriteLine($"  {TyreSets.Letter[s]}   {pace,6:F2} ({pace - soft:+0.00;-0.00})   {cold[0].t,6:F2}/{warm[0].t,6:F2} ({cold[0].t - warm[0].t:+0.00})  | cliff L{cliff}: " +
            string.Join("  ", run.Select(r => $"{r.t:F1}/{r.w * 100:F0}/{r.tc:F0}/{r.g:F2}")));
    }
    return;
}

// ---- tyre strategy plans: dotnet run -- race strat (no driving: the plans the rivals make, per race length) ----
if (args.Length > 1 && args[0] == "race" && args[1] == "strat")
{
    var tracks = Circuits();
    // race strat <mult | auto>: the game's "Tyre wear" setting (TyreRules): plans for short races, then a driven 10-lap race
    if (args.Length > 2)
    {
        bool auto = args[2] == "auto"; double mx = auto ? 0 : double.Parse(args[2], CultureInfo.InvariantCulture);
        TyreRules.Strong = true;
        foreach (var id in new[] { "spa", "monaco", "monza", "suzuka" })
        {
            int ti = Array.FindIndex(FormulaSim.Circuits.All, d => d.id == id); var t = tracks[ti]; var line = RacingLine.Compute(t, new CarConfig());
            foreach (int laps in new[] { 3, 5, 8, 10, 15, 20 })
            {
                double m = auto ? TyreRules.Mult(TyreRules.Auto, laps, id) : mx;
                var plans = new List<string>(); var stops = new int[4];
                for (int k = 0; k < 10; k++) { var st = new TyreStrategy(k + 1, id); st.Plan(laps, 0, id, line.lapTime, TyreStrategy.BaseSoftRate(id) * m); plans.Add(st.Describe()); stops[Math.Min(3, st.StopsPlanned)]++; }
                Console.WriteLine($"{id,-7} {laps,2} laps x{m,4:F1} (S/M/H ~{TyreRules.LifeLaps(0, m, id):F0}/{TyreRules.LifeLaps(1, m, id):F0}/{TyreRules.LifeLaps(2, m, id):F0} laps): stops 0/1/2/3+ = {stops[0]}/{stops[1]}/{stops[2]}/{stops[3]}  e.g. {string.Join(" | ", plans.Take(5))}");
            }
        }
        foreach (var id in new[] { "monza", "suzuka" })
        {
            int ti = Array.FindIndex(FormulaSim.Circuits.All, d => d.id == id); var t = tracks[ti]; var line = RacingLine.Compute(t, new CarConfig());
            int n = 10, laps = 10; double m = auto ? TyreRules.Mult(TyreRules.Auto, laps, id) : mx;
            var ds = new List<AiDriver>(); var rcs = new RaceControl(t, line, 5) { totalLaps = laps }; rcs.Init(n);
            for (int k = 0; k < n; k++)
            {
                var cfg = new CarConfig { fuelStart = 0.23 * t.length / 1000 * (laps + 0.5) + 2 }; TyreRules.Apply(cfg, m, true);
                var d = new AiDriver(t, line, cfg, DriverProfile.Grid[k * 2], AiTuning.For(ti), 1, k + 1); d.Place(t.GridIndex(k), (k % 2 == 0 ? 1 : -1) * 1.8); d.rc = rcs; d.slot = k; d.PlanRace(laps, 0, k + 1); ds.Add(d);
            }
            var plan0 = ds.Select(d => d.strat.Describe()).ToArray();
            var log = ds.Select(_ => new List<string>()).ToArray(); var setSeen = ds.Select(d => d.setSerial).ToArray();
            double time = 0; int steps = 0;
            while (time < laps * 160 && ds.Min(d => d.lap) <= laps)
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
            Console.WriteLine($"{id} {laps} laps x{m:F1} driven ({time:F0} s), finishing order:");
            foreach (int k in Enumerable.Range(0, n).OrderByDescending(k => ds[k].progress))
                Console.WriteLine($"  {ds[k].who.name,-13} plan {plan0[k],-26} stops: {string.Join(" ", log[k]),-44} wear at the flag {Math.Max(ds[k].car.wearF, ds[k].car.wearR) * 100:F0}%");
        }
        return;
    }
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

// ---- racecraft (traffic) test: dotnet run -c Release -- traffic [scen | race] ----
// The "player" is an AiDriver that drives the racing line BLIND (it never looks at another car, as a careless
// player would): it never yields, brakes for nobody and turns in on whoever is there. The rivals see it as the game
// passes the player's car (Think's player / playerIdx / playerLateral).
//  scen: one rival against the player: (a) slower on the line ahead, (b) brakes early (same speed elsewhere),
//        (c) side by side, rival on either side; Monza, Spa, Monaco with three drivers (aggression 0.95 / 0.60 / 0.45).
//  race: 21 rivals + the player on the grid in P5 (slower than the top rivals), 5 laps, with the game's tow and Overtake
//        Mode rule (armed from lap 2).
// Contacts are counted as events (a pair starts touching) and blamed on the car that caused them: nose to tail, the car
// behind; side by side, the car moving across into the other one (track-frame lateral speed). Overtakes: a pair swaps
// order by more than 4 m along the race (a reset or a pit stop re-seeds the pair without counting).
// DAMAGE=full: damage on, as in the race test; RACEONLY=monza,spa: race on just these circuits.
if (args.Length > 0 && args[0] == "traffic")
{
    string what = args.Length > 1 ? args[1] : "all";
    bool dmgFull = (Environment.GetEnvironmentVariable("DAMAGE") ?? "").ToLowerInvariant() == "full";
    var only = Environment.GetEnvironmentVariable("RACEONLY")?.Split(',');
    var defs = FormulaSim.Circuits.All;
    double LatV(Track t, FormulaCar c, int i)
    {
        var p = t.pts[i]; double ch = Math.Cos(c.heading), sh = Math.Sin(c.heading);
        double wx = c.vx * ch - c.vy * sh, wy = c.vx * sh + c.vy * ch; return wx * p.nx + wy * p.ny;
    }
    // the race craft's state (by reflection, so this test also builds against the old AiDriver for the before / after)
    string Dbg(AiDriver d) => d.GetType().GetProperty("CraftDebug")?.GetValue(d) as string ?? "";
    int Cnt(AiDriver d, string f) => d.GetType().GetField(f)?.GetValue(d) is int i ? i : 0;
    TrafficTally Run(Track t, RacingLine line, List<AiDriver> ais, AiDriver pl, double tMax, int laps)
    {
        var tl = new TrafficTally(); var hf = t.h != null ? new HeightField(t) : null;
        int n = ais.Count, m = n + (pl != null ? 1 : 0), N = t.N;
        AiDriver C(int k) => k < n ? ais[k] : pl;
        var touching = new bool[m, m]; var order = new int[m, m]; var lastReset = new double[m]; var pitL = new double[m];
        for (int k = 0; k < m; k++) lastReset[k] = -99;
        var otr = new OvertakeRule(t, line); double wall = Traffic.WallOffset(t);
        var tows = new double[m]; var offNow = new bool[m];
        double time = 0; int steps = 0;
        while (time < tMax)
        {
            if (laps > 0) { int mx = 0; for (int k = 0; k < m; k++) mx = Math.Max(mx, C(k).lap); if (mx > laps) break; }
            if (steps++ % 4 == 0)
            {
                // the game's tow (GameManager.UpdateTow) and Overtake Mode detection (RaceHud.OvertakeTick, from lap 2)
                for (int k = 0; k < m; k++)
                {
                    var me = C(k); double best = 0; if (me.idx < 0) { tows[k] = 0; continue; }
                    for (int j = 0; j < m; j++)
                    {
                        if (j == k || C(j).idx < 0) continue;
                        int d = C(j).idx - me.idx; if (d > N / 2) d -= N; else if (d < -N / 2) d += N;
                        double gap = d * t.ds; if (gap < 2 || gap > 60) continue;
                        double side = Math.Abs(C(j).lateral - me.lateral); if (side > 2.5) continue;
                        best = Math.Max(best, (1 - gap / 60) * (1 - side / 2.5));
                    }
                    tows[k] = best;
                }
                for (int k = 0; k < m; k++) { C(k).car.tow = tows[k]; otr.Update(C(k).car, C(k).idx, time, C(k).lap >= 2); }
                if (pl != null) pl.Think(4 * H, time, null, null, -1, 0);
                for (int k = 0; k < n; k++) ais[k].Think(4 * H, time, ais, pl?.car, pl != null ? pl.idx : -1, pl != null ? pl.lateral : 0);
                if (Environment.GetEnvironmentVariable("TRACEDRV") != null && time > double.Parse(Environment.GetEnvironmentVariable("TFROM")) && time < double.Parse(Environment.GetEnvironmentVariable("TFROM")) + 3)
                    foreach (var q in ais) if (q.who.name == Environment.GetEnvironmentVariable("TRACEDRV"))
                        Console.WriteLine($"   t {time:F2} s {t.pts[q.idx].s:F0} lat {q.lateral:F2} hw {t.HalfWidth(q.idx):F1} lineoff {line.off[q.idx]:F2} curv {line.curv[q.idx]:F4} v {Math.Sqrt(q.car.vx * q.car.vx + q.car.vy * q.car.vy) * 3.6:F0} tgt {q.target * 3.6:F0} line {line.speed[q.idx] * 3.6:F0} thr {q.LastInput.throttle:F2} brk {q.LastInput.brake:F2} st {q.LastInput.steer:F2} tow {q.car.tow:F2} slip {Math.Atan2(q.car.vy, Math.Max(1, q.car.vx)) * 57.3:F1} {Dbg(q)}");
                if (Environment.GetEnvironmentVariable("RDEBUG") == "1" && pl != null && steps % 120 == 1)
                {
                    AiDriver bk = null; double bg = 1e9;
                    foreach (var q in ais) { int dg = pl.idx - q.idx; if (dg > N / 2) dg -= N; else if (dg < -N / 2) dg += N; if (dg > 0 && dg * t.ds < bg) { bg = dg * t.ds; bk = q; } }
                    if (bk != null) Console.WriteLine($"   t {time:F1} s {t.pts[pl.idx].s:F0} behind {bk.who.name} gap {bg:F0} lat {bk.lateral:F1}/{pl.lateral:F1} v {Math.Sqrt(bk.car.vx * bk.car.vx + bk.car.vy * bk.car.vy) * 3.6:F0}/{Math.Sqrt(pl.car.vx * pl.car.vx + pl.car.vy * pl.car.vy) * 3.6:F0} tow {bk.car.tow:F2} ot {bk.car.otArmed}/{bk.car.otOn} {Dbg(bk)} lap {bk.lap}");
                }
                if (Environment.GetEnvironmentVariable("TDEBUG") == "1" && pl != null && (steps % 60 == 1 || (time > double.Parse(Environment.GetEnvironmentVariable("TFROM") ?? "1e9") && time < double.Parse(Environment.GetEnvironmentVariable("TFROM") ?? "1e9") + 2.5))) { var a0 = ais[0]; int dg = pl.idx - a0.idx; if (dg > N / 2) dg -= N; else if (dg < -N / 2) dg += N; Console.WriteLine($"   t {time:F1} s {t.pts[a0.idx].s:F0} gap {dg * t.ds:F1} lat {a0.lateral:F1}/{pl.lateral:F1} v {Math.Sqrt(a0.car.vx * a0.car.vx + a0.car.vy * a0.car.vy) * 3.6:F0}/{Math.Sqrt(pl.car.vx * pl.car.vx + pl.car.vy * pl.car.vy) * 3.6:F0} tgt {a0.target * 3.6:F0} {Dbg(a0)} lap {a0.lap}/{pl.lap}"); }
                for (int k = 0; k < m; k++)
                {
                    var d = C(k);
                    if (d.recovered) { tl.resets++; lastReset[k] = time; if (k == n) tl.plResets++; }
                    if (d.idx >= 0) Traffic.Wall(d.car, t.pts[d.idx], d.lateral, wall);
                    if (k < n && d.idx >= 0 && Math.Abs(d.lateral) > t.HalfWidth(d.idx) + 0.95)
                    {
                        tl.aiOffT += 4 * H; // all four wheels past the edge
                        if (!offNow[k]) { tl.offEvents++; if (Environment.GetEnvironmentVariable("OFFLOG") == "1") Console.WriteLine($"      OFF {d.who.name} t {time:F1} s {t.pts[d.idx].s:F0} lat {d.lateral:F1} v {Math.Sqrt(d.car.vx * d.car.vx + d.car.vy * d.car.vy) * 3.6:F0} tgt {d.target * 3.6:F0} tow {d.car.tow:F2} {Dbg(d)}"); }
                        offNow[k] = true;
                    }
                    else if (k < n) offNow[k] = false;
                }
                for (int i = 0; i < m; i++)
                    for (int j = i + 1; j < m; j++)
                    {
                        var a = C(i); var b = C(j);
                        double dx = a.car.x - b.car.x, dy = a.car.y - b.car.y;
                        if (dx * dx + dy * dy > 49) { touching[i, j] = false; continue; }
                        // blame from the state before the contact response
                        int dI = b.idx - a.idx; if (dI > N / 2) dI -= N; else if (dI < -N / 2) dI += N;
                        double g = dI * t.ds, side = b.lateral - a.lateral;
                        double va = a.idx >= 0 ? LatV(t, a.car, a.idx) * Math.Sign(side) : 0, vb = b.idx >= 0 ? -LatV(t, b.car, b.idx) * Math.Sign(side) : 0;
                        double closeV = Math.Sqrt((a.car.vx - b.car.vx) * (a.car.vx - b.car.vx) + (a.car.vy - b.car.vy) * (a.car.vy - b.car.vy));
                        bool hit = Traffic.Collide(a.car, b.car);
                        if (hit && !touching[i, j])
                        {
                            int fault; bool rear = false;
                            if ((Math.Abs(side) < 1.3 && Math.Abs(va - vb) < 1.5) || (Math.Max(va, vb) < 0.5 && Math.Abs(g) > 2.5)) { fault = g > 0 ? i : j; rear = Math.Abs(side) < 1.3; }
                            else fault = va >= vb ? i : j;
                            tl.events++;
                            bool withPl = j == n || i == n;
                            if (fault == n) tl.plFault++; else { tl.aiFault++; if (rear) tl.aiRear++; if (withPl) tl.aiOnPlFault++; }
                            if (withPl) tl.plEvents++;
                            if (fault != n && tl.log.Count < 12) tl.log.Add($"{time:F0}s {(rear ? "rear" : "side")} {C(fault).who.name}->{C(fault == i ? j : i).who.name} at {(a.idx >= 0 ? t.pts[a.idx].s : 0):F0} m g {g:F1} side {side:F1} latv {va:F1}/{vb:F1} | {Dbg(C(fault))}");
                        }
                        touching[i, j] = hit;
                    }
                // overtakes
                for (int k = 0; k < m; k++) if (C(k).pitLoss != pitL[k] || C(k).InPit) { pitL[k] = C(k).pitLoss; for (int j = 0; j < m; j++) order[k, j] = order[j, k] = 0; }
                for (int i = 0; i < m; i++)
                    for (int j = i + 1; j < m; j++)
                    {
                        if (time - lastReset[i] < 3 || time - lastReset[j] < 3) { order[i, j] = 0; continue; }
                        double dd = (C(i).progress - C(j).progress) * t.length;
                        int s = dd > 4 ? 1 : dd < -4 ? -1 : 0; if (s == 0) continue;
                        if (order[i, j] != 0 && s != order[i, j])
                        {
                            tl.overtakes++;
                            if (j == n) { if (s > 0) { tl.aiOnPl++; if (tl.firstPass == 0) tl.firstPass = time; } else tl.plOnAi++; }
                        }
                        order[i, j] = s;
                    }
            }
            for (int k = 0; k < m; k++)
            {
                var d = C(k);
                if (d.InPit) { d.PitStep(H, time); continue; }
                if (steps % 4 == 1) WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1);
                if (hf != null && steps % 4 == 1) d.car.grade = hf.GradeAlong(d.car.x, d.car.y, d.car.heading, d.idx);
                d.car.Step(d.LastInput, H);
            }
            time += H;
        }
        for (int k = 0; k < m; k++) tl.impacts += C(k).car.damage.hits;
        for (int k = 0; k < n; k++) tl.defMoves += Cnt(ais[k], "defMoves");
        tl.time = time;
        return tl;
    }
    if (what == "all" || what == "scen")
    {
        var drivers = new[] { 1, 3, 11 };   // M. Voss (aggression 0.95), N. Lindqvist (0.60), N. Holz (0.45)
        var total = new TrafficTally[3]; for (int q = 0; q < 3; q++) total[q] = new TrafficTally();
        string[] scName = { "(a) slower player on the line ahead", "(b) player brakes early", "(c) player alongside" };
        foreach (var id in (Environment.GetEnvironmentVariable("SCENONLY") ?? "monza,spa,monaco").Split(','))
        {
            int ti = Array.FindIndex(defs, d => d.id == id); var t = defs[ti].Build(); var line = RacingLine.Compute(t, new CarConfig());
            int N = t.N, p0 = t.GridIndex(0);
            for (int scn = 0; scn < 3; scn++)
            {
                if (Environment.GetEnvironmentVariable("SCENNO") != null && Environment.GetEnvironmentVariable("SCENNO") != scn.ToString()) continue;
                var tally = new TrafficTally();
                foreach (int dv in Environment.GetEnvironmentVariable("SCENDRV") != null ? new[] { int.Parse(Environment.GetEnvironmentVariable("SCENDRV")) } : drivers)
                    foreach (int sideSel in scn == 2 ? new[] { 1, -1 } : new[] { 0 })
                    {
                        var plWho = new DriverProfile { name = "PLAYER", pace = 0.85, consistency = 1, aggression = 0.5 };
                        var tuP = AiTuning.For(ti); double diff = 0.97;
                        if (scn == 0) diff = 0.95;                    // slower everywhere (and paceScale below)
                        if (scn == 1) { tuP.brakeUse *= 0.72; diff = 1.0; plWho.pace = 1; }   // brakes early and soft, as fast elsewhere
                        var pl = new AiDriver(t, line, new CarConfig { damage = dmgFull }, plWho, tuP, diff, 99);
                        if (scn == 0) pl.paceScale = 0.97;            // 3 % slower on the straights too
                        var ai = new AiDriver(t, line, new CarConfig { damage = dmgFull }, DriverProfile.Grid[dv], AiTuning.For(ti), 1, dv + 1);
                        int pIdx = p0, aIdx = scn == 2 ? p0 : (p0 - (int)Math.Round((scn == 0 ? 60 : 20) / t.ds) + N) % N;
                        double v0 = Math.Min(line.speed[pIdx], line.speed[aIdx]) * 0.9;
                        pl.Place(pIdx, line.off[pIdx], v0);
                        double aLat = line.off[aIdx];
                        if (scn == 2) { double room = t.HalfWidth(aIdx) - 1.0; aLat = Math.Max(-room, Math.Min(room, line.off[aIdx] + sideSel * 2.6)); if (Math.Abs(aLat - line.off[aIdx]) < 2.3) aLat = line.off[aIdx] - sideSel * 2.6; }
                        ai.Place(aIdx, aLat, v0); ai.lap += 2; pl.lap += 2;   // past lap 1: Overtake Mode can arm
                        var r = Run(t, line, new List<AiDriver> { ai }, pl, scn == 2 ? 40 : 150, 0);
                        tally.Add(r);
                    }
                total[scn].Add(tally);
                Console.WriteLine($"  {id,-7} {scName[scn],-36} contacts {tally.events,2} (AI-caused {tally.aiFault}, rear-end by AI {tally.aiRear}, player-caused {tally.plFault}); AI passes on the player {tally.aiOnPl}, re-passed by him {tally.plOnAi}; resets {tally.resets}");
                foreach (var s in tally.log) Console.WriteLine("        " + s);
            }
        }
        for (int scn = 0; scn < 3; scn++) Console.WriteLine($"SCENARIO {scName[scn]}: contacts {total[scn].events}, AI-caused {total[scn].aiFault}, player-caused {total[scn].plFault}, AI passes {total[scn].aiOnPl}, resets {total[scn].resets}");
    }
    if (what == "all" || what == "duel")
    {
        // duel: one rival right behind the blind player (0.6 s back, flying, Overtake Mode armed), the player's pace set
        // so that the rival is DELTA s a lap faster alone (bisection on the player's difficulty, same profile and tuning);
        // 5 laps. Does a faster rival get by a player who just holds the racing line, and cleanly?
        double SoloLap(Track t, RacingLine line, DriverProfile w, int ti, double diff, int seed)
        {
            var d = new AiDriver(t, line, new CarConfig(), w, AiTuning.For(ti), diff, seed); var hf = t.h != null ? new HeightField(t) : null;
            d.Place(t.N - 30, line.off[t.N - 30]); double time = 0; int steps = 0;
            while (d.lap < 3 && time < 600)
            {
                if (steps++ % 4 == 0) { d.Think(4 * H, time, null, null, -1, 0); WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); if (hf != null) d.car.grade = hf.GradeAlong(d.car.x, d.car.y, d.car.heading, d.idx); }
                d.car.Step(d.LastInput, H); time += H;
            }
            return d.lastLap;
        }
        var deltas = (Environment.GetEnvironmentVariable("DELTA") ?? "0.3,0.5").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var duelIds = (Environment.GetEnvironmentVariable("DUELONLY") ?? string.Join(",", defs.Select(d => d.id))).Split(',');
        var drivers = new[] { 1, 3, 11 };   // M. Voss (aggression 0.95), N. Lindqvist (0.60), N. Holz (0.45)
        Console.WriteLine($"traffic duel: one rival 0.6 s behind the blind player, faster by {string.Join(" / ", deltas)} s a lap alone; 5 laps; damage {(dmgFull ? "FULL" : "off")}");
        foreach (double delta in deltas)
        {
            var sum = new TrafficTally(); int duels = 0, passedOnce = 0; double tFirst = 0; int nFirst = 0;
            foreach (var id in duelIds)
            {
                int ti = Array.FindIndex(defs, d => d.id == id); if (ti < 0) continue;
                var t = defs[ti].Build(); var line = RacingLine.Compute(t, new CarConfig()); int N = t.N;
                var row = new List<string>(); var rowT = new TrafficTally();
                foreach (int dv in drivers)
                {
                    var w = DriverProfile.Grid[dv];
                    var plWho = new DriverProfile { name = "PLAYER", pace = w.pace, consistency = 1, aggression = 0.5 };
                    double aiLap = SoloLap(t, line, new DriverProfile { name = w.name, pace = w.pace, consistency = 1, aggression = w.aggression }, ti, 1, dv + 1);
                    // secant on the player's difficulty: his solo lap - the rival's = delta
                    double d0 = 1.0, f0 = SoloLap(t, line, plWho, ti, d0, 99) - aiLap - delta, d1 = 0.985, f1 = SoloLap(t, line, plWho, ti, d1, 99) - aiLap - delta;
                    for (int it = 0; it < 3 && Math.Abs(f1) > 0.02 && Math.Abs(f1 - f0) > 1e-6; it++)
                    {
                        double d2 = Math.Max(0.9, Math.Min(1.02, d1 - f1 * (d1 - d0) / (f1 - f0)));
                        d0 = d1; f0 = f1; d1 = d2; f1 = SoloLap(t, line, plWho, ti, d1, 99) - aiLap - delta;
                    }
                    double diff = d1; if (Environment.GetEnvironmentVariable("DUELDBG") == "1") Console.WriteLine($"      {w.name} solo {aiLap:F2} player diff {diff:F4} gap {f1 + delta:F2}");
                    var pl = new AiDriver(t, line, new CarConfig { damage = dmgFull }, plWho, AiTuning.For(ti), diff, 99);
                    var ai = new AiDriver(t, line, new CarConfig { damage = dmgFull }, w, AiTuning.For(ti), 1, dv + 1);
                    int pIdx = t.GridIndex(0); double v0 = line.speed[pIdx] * 0.9;
                    int aIdx = (pIdx - (int)Math.Round(Math.Max(15, 0.6 * v0) / t.ds) + N) % N;
                    pl.Place(pIdx, line.off[pIdx], v0); ai.Place(aIdx, line.off[aIdx], v0);
                    ai.lap += 2; pl.lap += 2;   // past lap 1: Overtake Mode can arm
                    var r = Run(t, line, new List<AiDriver> { ai }, pl, 5 * 200, Math.Max(ai.lap, pl.lap) + 4);
                    rowT.Add(r); duels++; if (r.aiOnPl > 0) passedOnce++;
                    if (r.firstPass > 0) { tFirst += r.firstPass; nFirst++; }
                    row.Add($"{w.name.Split(' ')[1]} {r.aiOnPl}/{r.plOnAi}{(r.aiFault > 0 ? $" c{r.aiFault}" : "")}{(Environment.GetEnvironmentVariable("DUELDBG") == "1" ? $" a{Cnt(ai, "attacksMade")} d{Cnt(ai, "divesMade")} e[{string.Join(",", (int[])ai.GetType().GetField("attackEnds").GetValue(ai))}]" : "")}");
                }
                sum.Add(rowT);
                Console.WriteLine($"  {defs[ti].name,-28} passes on the player / re-passed: {string.Join(", ", row),-40} contacts {rowT.events} (AI-caused {rowT.aiFault}, player-caused {rowT.plFault}); AI 4-wheels-off {rowT.aiOffT:F1} s");
                foreach (var s in rowT.log.Take(3)) Console.WriteLine("        " + s);
            }
            Console.WriteLine($"DUEL TOTAL (rival {delta:F1} s/lap faster, {duels} duels of 5 laps): passes on the player {sum.aiOnPl} ({(double)sum.aiOnPl / Math.Max(1, duels):F2} per duel; at least one in {passedOnce} of {duels}; first after {(nFirst > 0 ? tFirst / nFirst : 0):F0} s on average), re-passed {sum.plOnAi}; contacts {sum.events}, AI-caused {sum.aiFault} (rear-ends {sum.aiRear}), player-caused {sum.plFault}; AI 4-wheels-off {sum.aiOffT:F1} s ({sum.offEvents} times)");
        }
    }
    if (what == "all" || what == "race")
    {
        var sum = new TrafficTally(); int races = 0;
        double plDiff = double.Parse(Environment.GetEnvironmentVariable("PLDIFF") ?? "0.97", CultureInfo.InvariantCulture);   // the player's pace (AiDriver difficulty)
        Console.WriteLine($"traffic race: 21 rivals + blind scripted player (P5, difficulty {plDiff:F2}: slower than the top rivals), 5 laps, tow + Overtake Mode, damage {(dmgFull ? "FULL" : "off")}");
        for (int ti = 0; ti < defs.Length; ti++)
        {
            if (only != null && !only.Contains(defs[ti].id)) continue;
            var t = defs[ti].Build(); var line = RacingLine.Compute(t, new CarConfig());
            var ais = new List<AiDriver>(); int slot = 0, plSlot = 4; AiDriver pl = null;
            for (int g = 0; g < 22; g++)
            {
                int gi = t.GridIndex(g); double lat = (g % 2 == 0 ? 1 : -1) * 1.8;
                if (g == plSlot)
                {
                    pl = new AiDriver(t, line, new CarConfig { damage = dmgFull }, new DriverProfile { name = "PLAYER", pace = 0.85, consistency = 1, aggression = 0.5 }, AiTuning.For(ti), plDiff, 99);
                    pl.Place(gi, lat); continue;
                }
                var d = new AiDriver(t, line, new CarConfig { damage = dmgFull }, DriverProfile.Grid[slot], AiTuning.For(ti), 1, slot + 1); d.Place(gi, lat); ais.Add(d); slot++;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int rl = int.Parse(Environment.GetEnvironmentVariable("RACELAPS") ?? "5"); var r = Run(t, line, ais, pl, (rl + 1) * 130, rl); races++; // RACELAPS: race length (default 5)
            int plPos = 1 + ais.Count(d => d.progress > pl.progress);
            Console.WriteLine($"  {defs[ti].name,-28} {r.time,4:F0} s ({sw.Elapsed.TotalSeconds,4:F1} s): contacts {r.events,3} (AI-caused {r.aiFault,3}, of them rear-ends {r.aiRear,2}; with the player {r.plEvents,2}: AI-caused {r.aiOnPlFault}, player-caused {r.plFault}); overtakes {r.overtakes,3}, on the player {r.aiOnPl,2} (he re-passed {r.plOnAi}); player P{plPos}; defensive moves {r.defMoves}; resets {r.resets} (player {r.plResets}); impacts {r.impacts}; AI 4-wheels-off {r.aiOffT:F1} s ({r.offEvents} times); wings damaged at the flag {ais.Count(d => d.car.damage.WingsDamaged)}/{ais.Count} (any damage {ais.Count(d => d.car.damage.Any)})");
            if (Environment.GetEnvironmentVariable("LAPS") == "1") Console.WriteLine($"      best laps: player {pl.bestLap:F2}; " + string.Join(" ", ais.OrderByDescending(d => d.progress).Select(d => $"{d.who.name.Split(' ')[1]} {d.bestLap:F2}")));
            foreach (var s in r.log.Take(4)) Console.WriteLine("        " + s);
            sum.Add(r);
        }
        Console.WriteLine($"TRAFFIC RACE TOTAL ({races} races): contacts {sum.events}, AI-caused {sum.aiFault} (rear-ends {sum.aiRear}), with the player {sum.plEvents} (AI-caused {sum.aiOnPlFault}, player-caused {sum.plFault}); overtakes {sum.overtakes}, on the player {sum.aiOnPl} ({(double)sum.aiOnPl / Math.Max(1, races):F1} per race), player re-passes {sum.plOnAi}; defensive moves {sum.defMoves}; resets {sum.resets}; impacts {sum.impacts}; AI 4-wheels-off {sum.aiOffT:F1} s ({sum.offEvents} times)");
    }
    return;
}

// ---- race test: dotnet run -- race [fuel | sc] ("fuel": start on a 70 kg race fuel load instead of qualifying trim;
// "sc": force a safety car on lap 2 and check the queue, no overtaking and the restart). RC=on: race control and
// tyre strategy on in the plain race test too (default off: the legacy race test).
// ---- load-time parts that are pure C#: dotnet run -c Release -- loadperf ----
if (args.Length > 0 && args[0] == "loadperf")
{
    double tb = 0, tl = 0, th = 0, ts = 0; double chk = 0;
    foreach (var def in FormulaSim.Circuits.All)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); var t = def.Build(); double a = sw.Elapsed.TotalMilliseconds;
        var line = RacingLine.Compute(t, new CarConfig()); double b = sw.Elapsed.TotalMilliseconds;
        var hf = new HeightField(t); double c = sw.Elapsed.TotalMilliseconds;
        // the world builder samples the ground for every terrain / scenery vertex: 200k samples as a stand-in
        var bb = t.Bounds(); var rng = new Random(1); double s0 = sw.Elapsed.TotalMilliseconds;
        for (int i = 0; i < 200000; i++) chk += hf.Sample(bb.minX + (bb.maxX - bb.minX) * rng.NextDouble(), bb.minY + (bb.maxY - bb.minY) * rng.NextDouble());
        for (int i = 0; i < 100000; i++) { var p = t.pts[i % t.N]; chk += hf.Sample(p.x + p.nx * (i % 13 - 6), p.y + p.ny * (i % 13 - 6)); }
        double d = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  {def.name,-28} track {a,6:F0} ms  racing line {b - a,6:F0} ms  height field {c - b,6:F0} ms  300k samples {d - s0,5:F0} ms");
        tb += a; tl += b - a; th += c - b; ts += d - s0;
    }
    Console.WriteLine($"  total: track {tb:F0} ms, racing line {tl:F0} ms, height field {th:F0} ms, samples {ts:F0} ms (checksum {chk:R})");
    return;
}

// ---- CPU cost per 240 Hz tick: dotnet run -c Release -- perf [circuit] ----
// The game's loop for 22 cars (car 0 = "player": Nearest + WheelContact every tick; 21 rivals: Think, wall and
// contact at 60 Hz, WheelContact at 60 Hz, Step at 240 Hz). Prints ms per tick by part and bytes allocated per tick.
// LOD=1: the rivals' level of detail (AiLod) on, as the game runs them.
if (args.Length > 0 && args[0] == "perf")
{
    string cid = args.Length > 1 ? args[1] : "monza"; int ti = Array.FindIndex(FormulaSim.Circuits.All, d => d.id == cid);
    var t = FormulaSim.Circuits.All[ti].Build(); var line = RacingLine.Compute(t, new CarConfig()); var hf = t.h != null ? new HeightField(t) : null;
    bool lod = Environment.GetEnvironmentVariable("LOD") == "1";
    int n = 22; var ds = new List<AiDriver>();
    for (int k = 0; k < n; k++) { var d = new AiDriver(t, line, new CarConfig(), DriverProfile.Grid[k % DriverProfile.Grid.Length], AiTuning.For(ti), 1, k + 1); d.Place(t.GridIndex(k), (k % 2 == 0 ? 1 : -1) * 1.8); ds.Add(d); }
    double wall = Traffic.WallOffset(t); var lods = new AiLod[n]; var want = new int[n]; for (int k = 0; k < n; k++) { lods[k] = new AiLod(); want[k] = 1; }
    var sw = new System.Diagnostics.Stopwatch[5]; for (int k = 0; k < 5; k++) sw[k] = new System.Diagnostics.Stopwatch();
    string[] part = { "Think+wall (60 Hz)", "contact + LOD pick (60 Hz)", "Nearest+WheelContact", "car.Step", "grade (HeightField)" };
    long alloc0 = 0; double time = 0; int steps = 0, ticks = 240 * 180, warm = 240 * 20, pIdx = ds[0].idx;
    var all = new System.Diagnostics.Stopwatch();
    for (int s = 0; s < ticks + warm; s++)
    {
        if (s == warm) { foreach (var w in sw) w.Reset(); all.Restart(); alloc0 = GC.GetAllocatedBytesForCurrentThread(); }
        if (steps++ % 4 == 0)
        {
            sw[0].Start(); for (int k = 0; k < n; k++) { ds[k].Think(4 * H, time, ds, null, -1, 0); if (ds[k].idx >= 0) Traffic.Wall(ds[k].car, t.pts[ds[k].idx], ds[k].lateral, wall); } sw[0].Stop();
            sw[1].Start(); for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) Traffic.Collide(ds[i].car, ds[j].car);
            if (lod) for (int k = 1; k < n; k++) want[k] = time > 60 ? AiLod.WantFor(t, pIdx, k, ds) : 1; sw[1].Stop();
            sw[4].Start(); if (hf != null) for (int k = 1; k < n; k++) ds[k].car.grade = hf.GradeAlong(ds[k].car.x, ds[k].car.y, ds[k].car.heading, ds[k].idx); sw[4].Stop();
        }
        sw[2].Start();
        { var c0 = ds[0].car; var nr = t.Nearest(c0.x, c0.y, pIdx); pIdx = nr.idx; WheelContact.Update(t, line, c0, nr.idx, nr.lateral, 0.5, 0); }
        if (steps % 4 == 1) for (int k = 1; k < n; k++) WheelContact.Update(t, line, ds[k].car, ds[k].idx, ds[k].lateral, 0.5, 0);
        sw[2].Stop();
        sw[4].Start(); if (hf != null) ds[0].car.grade = hf.GradeAlong(ds[0].car.x, ds[0].car.y, ds[0].car.heading, ds[0].idx); sw[4].Stop();
        sw[3].Start();
        ds[0].car.Step(ds[0].LastInput, H);
        for (int k = 1; k < n; k++)
        {
            var d = ds[k];
            if (lod && lods[k].Tick(d.car, d.LastInput, H, want[k])) continue;
            d.car.Step(d.LastInput, H);
        }
        sw[3].Stop();
        time += H;
    }
    all.Stop(); long bytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;
    Console.WriteLine($"perf {FormulaSim.Circuits.All[ti].name}, {n} cars, {ticks / 240} s at 240 Hz{(lod ? ", AI LOD on" : "")}: {all.Elapsed.TotalMilliseconds * 1000 / ticks:F1} us per tick ({all.Elapsed.TotalMilliseconds * 1000 / ticks / n:F2} us per car-tick), {all.Elapsed.TotalMilliseconds * 1000 / ticks * 4:F0} us per 60 fps frame");
    for (int k = 0; k < 5; k++) Console.WriteLine($"  {part[k],-24} {sw[k].Elapsed.TotalMilliseconds * 1000 / ticks,7:F2} us/tick");
    Console.WriteLine($"  allocated {bytes} bytes in {ticks} ticks ({(double)bytes / ticks:F1} B/tick)");
    return;
}

// ---- VSC delta check: dotnet run -c Release -- vsc ----
// A 22-car race with race control; when the leader starts lap 2 a VSC is forced. The 8th car becomes the "player":
// from then on it is driven by a stand-in for a human keeping RaceControl's VSC delta just above 0 (pure pursuit on
// the racing line, speed = the delta's own reference speed, nudged by the delta). Prints the distance and time gap to
// the car ahead at the VSC start and at the green flag, and the same for the AI pairs (they should keep theirs).
// RACEONLY=monza,spa: circuits; DIFF=0.89: the rivals' difficulty (the game's DiffScale; default 1); TARGET=0.1: the
// delta the stand-in keeps (s).
if (args.Length > 0 && args[0] == "vsc")
{
    var only = Environment.GetEnvironmentVariable("RACEONLY")?.Split(',') ?? new[] { "monza", "spa", "silverstone", "monaco", "test" };
    double diff = double.Parse(Environment.GetEnvironmentVariable("DIFF") ?? "1", CultureInfo.InvariantCulture);
    double target = double.Parse(Environment.GetEnvironmentVariable("TARGET") ?? "0.1", CultureInfo.InvariantCulture);
    Console.WriteLine($"VSC delta check: rivals' difficulty {diff:F2}, the player keeps the delta at +{target:F1} s");
    var tracks = Circuits(); double sumPl = 0, sumAi = 0; int nPl = 0, nAi = 0;
    for (int ti = 0; ti < tracks.Length; ti++)
    {
        if (!only.Contains(FormulaSim.Circuits.All[ti].id)) continue;
        var t = tracks[ti]; var line = RacingLine.Compute(t, new CarConfig()); int N = t.N;
        var hf = t.h != null ? new HeightField(t) : null;
        var ds = new List<AiDriver>(); int n = DriverProfile.Grid.Length;
        for (int k = 0; k < n; k++) { var d = new AiDriver(t, line, new CarConfig(), DriverProfile.Grid[k], AiTuning.For(ti), diff, k + 1); d.Place(t.GridIndex(k), (k % 2 == 0 ? 1 : -1) * 1.8); ds.Add(d); }
        var rcx = new RaceControl(t, line, ti + 1) { totalLaps = 12 }; rcx.Init(n);
        for (int k = 0; k < n; k++) { ds[k].rc = rcx; ds[k].slot = k; }
        double Dist(int k) => ds[k].lap + (double)ds[k].idx / N;
        int[] Order() => Enumerable.Range(0, n).OrderByDescending(Dist).ToArray();
        int P = -1; double time = 0, deployAt = -1, endAt = -1; int steps = 0;
        int[] ord0 = null; double[] gap0 = null, gapT0 = null; double vscSumPl = 0, vscSumRef = 0, aiSumV = 0, aiSumRef = 0;
        // time gaps: when each car last passed each track point (the car behind passing the same point later)
        var passT = new double[n, N]; var lastIdx = new int[n]; for (int k = 0; k < n; k++) lastIdx[k] = -1;
        double GapT(int behind, int ahead) { int i = ds[behind].idx; return i < 0 ? double.NaN : time - passT[ahead, i]; }
        CarInput Follow(AiDriver me, double vt)
        {
            var c = me.car; int i = me.idx; double v = Math.Sqrt(c.vx * c.vx + c.vy * c.vy);
            double Ld = 5 + 0.22 * v; int ia = (i + Math.Max(1, (int)Math.Round(Ld / t.ds))) % N;
            double room = t.HalfWidth(ia) - 1.0, off = Math.Max(-room, Math.Min(room, line.off[ia]));
            var pa = t.pts[ia]; double tx = pa.x + pa.nx * off - c.x, ty = pa.y + pa.ny * off - c.y;
            double ch = Math.Cos(c.heading), sh = Math.Sin(c.heading), fx = ch * tx + sh * ty, fy = -sh * tx + ch * ty;
            double delta = Math.Atan(c.cfg.wheelbase * 2 * fy / Math.Max(1.0, fx * fx + fy * fy));
            double lockNow = c.lockAngle > 1e-4 ? c.lockAngle : c.cfg.maxLockDeg * Math.PI / 180;
            double err = vt - v, thr = Math.Max(0, Math.Min(1, err * 0.5)), brk = Math.Max(0, Math.Min(1, -err * 0.3));
            if (brk > 0.02) thr = 0;
            return new CarInput(Math.Max(-1, Math.Min(1, -delta / lockNow)), thr, brk);
        }
        CarInput plIn = default;
        while (time < 1200 && endAt < 0)
        {
            if (steps++ % 4 == 0)
            {
                for (int k = 0; k < n; k++) ds[k].Think(4 * H, time, ds, null, -1, 0);
                var before = rcx.flag;
                for (int k = 0; k < n; k++) rcx.Feed(k, ds[k]);
                rcx.Update(4 * H, time, time > 2);
                if (deployAt < 0 && ds.Max(d => d.lap) >= 2)
                {
                    var o = Order(); P = o[7]; rcx.playerSlot = P; rcx.ForceVsc(); deployAt = time; ord0 = o;
                    gap0 = new double[n]; gapT0 = new double[n];
                    for (int r = 1; r < n; r++) { gap0[o[r]] = (Dist(o[r - 1]) - Dist(o[r])) * t.length; gapT0[o[r]] = GapT(o[r], o[r - 1]); }
                }
                if (deployAt >= 0 && before != FormulaSim.Flag.Green && rcx.flag == FormulaSim.Flag.Green) endAt = time;
                if (P >= 0 && endAt < 0)
                {
                    // the stand-in driver: the delta's reference speed (RaceControl.VscRef) a little ahead, nudged by the delta
                    var me = ds[P]; double v = Math.Sqrt(me.car.vx * me.car.vx + me.car.vy * me.car.vy);
                    int ip = (me.idx + (int)Math.Round(0.35 * v / t.ds)) % N;
                    double vRef = Math.Min(rcx.VscRef(ip), rcx.VscRef(me.idx) + 1.5);
                    double vt = vRef * Math.Max(0.85, Math.Min(1.2, 1 + 0.3 * (rcx.vscDelta - target)));
                    plIn = Follow(me, vt); vscSumPl += v; vscSumRef += rcx.VscRef(me.idx);
                    // the rivals against the same reference at their own points (after the first 3 s of slowing down)
                    if (time > deployAt + 3)
                        for (int k = 0; k < n; k++) { if (k == P || ds[k].idx < 0) continue; var c = ds[k].car; aiSumV += Math.Sqrt(c.vx * c.vx + c.vy * c.vy); aiSumRef += rcx.VscRef(ds[k].idx); }
                }
            }
            for (int k = 0; k < n; k++)
            {
                var d = ds[k];
                if (steps % 4 == 1) WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1);
                if (hf != null && steps % 4 == 1) d.car.grade = hf.GradeAlong(d.car.x, d.car.y, d.car.heading, d.idx);
                d.car.Step(k == P && endAt < 0 ? plIn : d.LastInput, H);
            }
            time += H;
            for (int k = 0; k < n; k++)
            {
                int i = ds[k].idx; if (i < 0 || i == lastIdx[k]) continue;
                if (lastIdx[k] >= 0) for (int j = (lastIdx[k] + 1) % N, g = 0; g < 40; j = (j + 1) % N, g++) { passT[k, j] = time; if (j == i) break; }
                else passT[k, i] = time;
                lastIdx[k] = i;
            }
        }
        if (P < 0 || endAt < 0) { Console.WriteLine($"  {CircuitNames[ti]}: no VSC run"); continue; }
        var o1 = ord0; int A = -1; for (int r = 1; r < n; r++) if (o1[r] == P) A = o1[r - 1];
        double g1 = (Dist(A) - Dist(P)) * t.length, gt1 = GapT(P, A);
        double aiD = 0, aiT = 0; int aiN = 0;
        for (int r = 1; r < n; r++)
        {
            int k = o1[r], a = o1[r - 1]; if (k == P || a == P) continue;
            double gd = (Dist(a) - Dist(k)) * t.length; if (gap0[k] > 300 || ds[k].recovered) continue; // close pairs only
            aiD += gd - gap0[k]; aiT += GapT(k, a) - gapT0[k]; aiN++;
        }
        Console.WriteLine($"  {CircuitNames[ti],-28} VSC {endAt - deployAt:F0} s: player (P8) to the car ahead {gap0[P]:F0} m / {gapT0[P]:F2} s -> {g1:F0} m / {gt1:F2} s "
                        + $"({g1 - gap0[P]:+0;-0} m, {gt1 - gapT0[P]:+0.00;-0.00} s), delta at the end {rcx.vscDelta:+0.00;-0.00} s, speed {vscSumPl / Math.Max(1, vscSumRef) * 100:F1}% of the reference, pen {rcx.penalty:F0} s; rivals {aiSumV / Math.Max(1, aiSumRef) * 100:F1}% of it; AI pairs: mean {aiD / Math.Max(1, aiN):+0;-0} m / {aiT / Math.Max(1, aiN):+0.00;-0.00} s");
        sumPl += g1 - gap0[P]; nPl++; sumAi += aiD / Math.Max(1, aiN); nAi++;
    }
    Console.WriteLine($"VSC: player's gap to the car ahead grew {sumPl / Math.Max(1, nPl):+0;-0} m on average (AI pairs {sumAi / Math.Max(1, nAi):+0;-0} m)");
    return;
}

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
    // LOD=1: the game's rival level of detail (AiLod: 120 Hz beyond 150 m, 60 Hz beyond 400 m when alone) with car 0
    // standing in for the player; prints the share of rival steps saved
    bool lodOn = Environment.GetEnvironmentVariable("LOD") == "1"; long lodSteps = 0, lodTicks = 0;
    if (lodOn) Console.WriteLine($"LOD: AiLod, {AiLod.Gap2} m -> 120 Hz, {AiLod.Gap4} m and alone ({AiLod.Alone} m) -> 60 Hz");
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
        var lods = new AiLod[n]; var want = new int[n]; for (int k = 0; k < n; k++) { lods[k] = new AiLod(); want[k] = 1; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (scTest ? time < 3000 && (greenAt < 0 || ds.Max(d => d.lap) <= greenLap) : time < 6 * 130 && ds.Min(d => d.lap) < 5)
        {
            if (steps++ % 4 == 0)
            {
                for (int k = 0; k < n; k++) { ds[k].Think(4 * H, time, ds, null, -1, 0); if (ds[k].recovered) { recov++; rec[k]++; var dm = ds[k].car.damage; where.Add($"{(ds[k].idx >= 0 ? t.pts[ds[k].idx].s : -1):F0}{(ds[k].lap <= 1 ? "*" : "")}{(Environment.GetEnvironmentVariable("RESETT") == "1" ? $"@{time:F0}s{(rcx != null ? "/" + rcx.flag : "")}" : "")}{(dm.Any ? $"[d fw {dm.wingFL:F1}/{dm.wingFR:F1} rw {dm.wingRear:F1} su {dm.suspFL:F1}/{dm.suspFR:F1}/{dm.suspRL:F1}/{dm.suspRR:F1} fl {dm.floor:F1}{(dm.Punctured ? " flat" : "")}]" : "")}"); } }
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
                if (lodOn) for (int k = 1; k < n; k++) want[k] = time > 60 ? AiLod.WantFor(t, ds[0].idx, k, ds) : 1;
            }
            if (steps % 240 == 0) { var c0 = ds[0].car; tMin = Math.Min(tMin, Math.Min(c0.tyreTF, c0.tyreTR)); tMaxF = Math.Max(tMaxF, c0.tyreTF); tMaxR = Math.Max(tMaxR, c0.tyreTR); if (time > 60) { tSumF += c0.tyreTF; tSumR += c0.tyreTR; tN++; } }
            for (int k = 0; k < n; k++)
            {
                var d = ds[k];
                if (steps % 4 == 1) WheelContact.Update(t, line, d.car, d.idx, d.lateral, 1); if (hf != null && steps % 4 == 1) d.car.grade = hf.GradeAlong(d.car.x, d.car.y, d.car.heading, d.idx);
                // COARSE=1: the game's far-rival saving (GameManagerPerf.cs) with car 0 standing in for the player and camera
                // (as RivalCoarse: skip a tick, then 2 ticks at once; a car leaving 120 Hz while a tick behind catches up)
                bool cs = coarse && k > 0 && time > 60 && Coarse(t, ds[0], d);
                if (lodOn && k > 0) { lodTicks++; if (lods[k].Tick(d.car, d.LastInput, H, want[k])) { if (lods[k].lag == 0) lodSteps++; continue; } lodSteps++; d.car.Step(d.LastInput, H); continue; }
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
    if (lodOn) Console.WriteLine($"LOD: rivals stepped {lodSteps} times in {lodTicks} car-ticks ({100.0 * lodSteps / Math.Max(1, lodTicks):F1}%; 100% = all at 240 Hz)");
    if (scTest) Console.WriteLine($"SAFETY CAR: {scPass} of {scRuns} circuits PASS");
    Console.WriteLine($"TOTAL (damage {(dmgFull ? "full" : "off")}, race control {(rcOn ? "on" : "off")}): {totalResets} resets, {totalHits} damaging impacts, {totalStops} pit stops");
    return;
}

var dir = AppContext.BaseDirectory.Split("/bin/")[0];
var sc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "scenarios.json"))).RootElement;
// golden.csv came from the old JS prototype; without it the JS comparison is skipped and the calibration still runs
if (!File.Exists(Path.Combine(dir, "golden.csv"))) Console.WriteLine("golden.csv missing: JS comparison skipped");
else {
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
}

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
                    if (st++ % 4 == 0) { dd.Think(4 * H, tt, null, null, -1, 0); WheelContact.Update(t2, line, dd.car, dd.idx, dd.lateral, 1); if (hf != null) dd.car.grade = hf.GradeAlong(dd.car.x, dd.car.y, dd.car.heading, dd.idx); }
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

// tallies of the racecraft test (traffic)
sealed class TrafficTally
{
    public int defMoves, offEvents, events, aiFault, aiRear, plFault, aiOnPlFault, plEvents, aiOnPl, plOnAi, overtakes, resets, plResets, impacts;
    public double aiOffT, time, firstPass;
    public List<string> log = new List<string>();
    public void Add(TrafficTally o)
    {
        defMoves += o.defMoves; offEvents += o.offEvents; events += o.events; aiFault += o.aiFault; aiRear += o.aiRear; plFault += o.plFault; aiOnPlFault += o.aiOnPlFault; plEvents += o.plEvents;
        aiOnPl += o.aiOnPl; plOnAi += o.plOnAi; overtakes += o.overtakes; resets += o.resets; plResets += o.plResets; impacts += o.impacts; aiOffT += o.aiOffT; time += o.time;
        foreach (var s in o.log) if (log.Count < 12) log.Add(s);
    }
}
