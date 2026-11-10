// FormulaCar.cs — formula car vehicle model, a line-by-line port of the web prototype's physics.js.
// Pure C# (no UnityEngine), so it runs identically in Unity and in the command-line test harness.
// Units are SI (m, kg, s, N, rad). Doubles are used on purpose: results match the JS version bit-for-bit
// in normal driving, and Unity reads the outputs as floats.
//
// Physics frame: world x/y plane, heading 0 = +x, positive yaw = counter-clockwise (left).
// Car frame: vx forward, vy left, r yaw rate.
// Calibrated to the 2026 Formula 1 car (see CarConfig for sources and tuning notes).
using System;

namespace FormulaSim
{
    public enum TcMode { Off, Low, High }

    // Defaults = 2026 Formula 1 car. Sources: FIA 2026 technical regulations as published by the FIA, Honda and
    // the teams (mass, wheelbase, width, power unit, energy store, MGU-K ramp-down), Pirelli (tyre sizes).
    // Values marked "est." are not published; they were fitted so the model reproduces measured F1 performance:
    // the 2026 pole laps at Monaco (1:12.051) and Spa (1:44.361), speed-trap figures and typical g-forces.
    [Serializable]
    public class CarConfig
    {
        public double mass = 783;            // kg: 768 FIA minimum (with driver) + ~15 kg fuel (qualifying trim)
        public double inertiaZ = 1100;       // kg m^2 yaw inertia (est.)
        public double wheelbase = 3.40;      // m (FIA maximum, all teams use it)
        public double frontWeight = 0.45;    // static weight on front axle (est., regulation window ~44-46 %)
        public double cgHeight = 0.27;       // m (est.)
        public double wheelRadius = 0.355;   // m (Pirelli 2026 rear: 710 mm diameter)
        public double mu = 2.2;              // tyre friction at static load (est., see calibration in Tools/PhysicsCheck)
        public double rearGrip = 1.08;       // wider rear tyres (375 mm vs 280 mm)
        public double loadExp = 0.8;         // max tyre force ~ load^0.8 (tyre load sensitivity)
        public double tireB = 14.5, tireC = 1.6, tireE = 0.3; // magic formula, peak ~6.7 deg
        public double slipGrowth = 0.3;      // peak slip angle grows with load
        public double peakRatio = 0.11;      // longitudinal slip ratio at peak traction
        public double wheelInertia = 1.8;    // kg m^2, both rear wheels + tyres + discs (est.)
        public double engineInertia = 0.045; // kg m^2, felt at the wheel x ratio^2
        public double track = 1.58;          // m, track width (1.9 m car, 280/375 mm tyres)
        public double latTransferFront = 0.5;
        public double cdA = 1.10;            // drag area m^2, wings closed (corner mode) (est.)
        public double clA = 4.0;             // downforce area m^2, corner mode (est.)
        public bool activeAero = true;       // 2026 active aero: wings open on straights (straight mode)
        public double cdAX = 0.70, clAX = 2.5; // drag / downforce area with wings open (est.)
        public double aeroFront = 0.45;
        public double rollRes = 0.015;
        // combustion engine: 1.6 V6 turbo, ~400 kW from 10,500 rpm (fuel-flow limited), 15,000 rpm allowed
        public double peakTorque = 365;      // Nm at the crank
        public double[] torqueRpm = { 4000, 6000, 8000, 10500, 12000, 13000 };
        public double[] torqueFrac = { 0.55, 0.80, 0.96, 1.0, 0.875, 0.80 };
        public double redline = 12000, idleRpm = 4000;
        public double[] gearTopSpeeds = { 130, 165, 200, 232, 262, 292, 322, 350 }; // km/h at the limiter, 8 gears (est.)
        public double driveEff = 0.94;
        // hybrid: MGU-K 350 kW, 4 MJ battery, 8.5 MJ harvest per lap (FIA)
        public double ersPower = 350e3;      // W
        public double ersCapacity = 4e6;     // J usable
        public double ersHarvestLap = 8.5e6; // J per lap
        public double ersLiftPower = 100e3;  // W harvested when coasting off-throttle (est.)
        public double shiftTime = 0.03;
        public double clutchEngage = 13;     // m/s (47 km/h) by which the launch clutch is fully engaged
        public double brakeForce = 46000;    // N total at full pedal (enough for ~5 g with downforce)
        public double brakeBias = 0.57;      // front share
        public double maxLockDeg = 22;       // Monaco-spec steering lock
        public double speedSens = 0.5;      // steering aid: lock = full / (1 + speedSens (v/30)^2). 0 = full lock at any speed
        public double csFull = 0.07;        // slide angle (rad) at which countersteer gets full lock again
        public double steerSpeed = 3.0;
        public double steerReturn = 8.0;
        public bool csOpen = true;           // open up to full lock when countersteering
        // ---- chassis detail (suspension, ride-height aero, camber, tyre heat, differential, slipstream) ----
        // Teams do not publish these; the values are typical figures from F1 engineering literature and Pirelli's
        // prescriptions, chosen so the whole-car results still match the measured performance above.
        public bool chassis = true;
        public double rideF0 = 0.032, rideR0 = 0.060;     // m, static ride heights (front low, rear higher = rake)
        public double springF = 300e3, springR = 260e3;   // N/m per axle at the wheels (springs + heave element + tyres)
        public double heaveRate = 18;                     // 1/s, how fast the body settles (damping)
        public double rollStiff = 1.5e6;                  // Nm/rad, both axles together (about 0.35 deg of roll at 5 g)
        public double rideDesign = 0.030, rideGain = 0.25, rideStall = 0.008; // ground effect: lower = more downforce, until the floor stalls
        public double rakeGain = 2.0;                     // aero balance moves forward by this much per metre of extra rake (nose down)
        public double yawSens = 0.4;                      // downforce lost per radian of slide angle
        public double camberF = -3.25, camberR = -1.75;   // deg, static camber (Pirelli limits are about -3.5 / -2.0)
        public double toeF = -0.08, toeR = 0.10;          // deg per wheel (front toe-out, rear toe-in)
        public double tyreStart = 70, tyreOpt = 100, tyreWindow = 30, tyreLoss = 0.06; // deg C: blanket temp, best temp, and grip lost one window away
        public double trackTemp = 40;                     // deg C
        public double tyreHeatCap = 22e3;                 // J/K per axle (tread and carcass that follow the surface)
        public double tyreHeatShare = 0.45;               // part of the sliding energy that goes into the tyre (rest into the road)
        public double tyreCool0 = 50, tyreCoolV = 1.85;    // W/K at rest, plus per m/s of air and road speed
        public double diffPreload = 60, diffPower = 0.15, diffCoast = 0.25; // differential: Nm preload, locking per unit of drive / overrun torque
        public double towDrag = 0.30, towDown = 0.10;     // slipstream right behind another car: drag and downforce lost
        public TcMode tc = TcMode.Low;       // driver aids (not allowed in real F1; kept for touch controls)
        public bool abs = true, autoShift = true;

        public CarConfig Clone()
        {
            var c = (CarConfig)MemberwiseClone();
            c.gearTopSpeeds = (double[])gearTopSpeeds.Clone();
            c.torqueRpm = (double[])torqueRpm.Clone(); c.torqueFrac = (double[])torqueFrac.Clone();
            return c;
        }

        // The Formula 2 set-up the web prototype was built on. Only used by the regression tests,
        // which replay the JavaScript version's results.
        public static CarConfig LegacyF2() => new CarConfig
        {
            mass = 795, inertiaZ = 950, wheelbase = 3.135, frontWeight = 0.45, cgHeight = 0.28, wheelRadius = 0.33,
            chassis = false, mu = 2.2, wheelInertia = 1.6, speedSens = 1.0, csFull = 0.14, track = 1.50, cdA = 1.05, clA = 4.0, activeAero = false, cdAX = 1.05, clAX = 4.0,
            peakTorque = 600, torqueRpm = new double[] { 2000, 4000, 5500, 6500, 7500, 8750 }, torqueFrac = new[] { 0.40, 0.62, 0.88, 1.0, 0.97, 0.84 },
            redline = 8750, idleRpm = 3000, gearTopSpeeds = new double[] { 128, 168, 207, 245, 283, 320 }, driveEff = 0.80,
            ersPower = 0, shiftTime = 0.05, brakeForce = 22500, brakeBias = 0.60, maxLockDeg = 18,
        };
    }

    public struct CarInput
    {
        public double steer;    // -1..1, + = right
        public double throttle; // 0..1
        public double brake;    // 0..1
        public CarInput(double steer, double throttle, double brake) { this.steer = steer; this.throttle = throttle; this.brake = brake; }
    }

    public static class Surfaces
    {
        // peak friction relative to dry asphalt; kerbs use anti-slip paint
        public static readonly (double mu, double drag) Asphalt = (1.0, 0.0), Kerb = (0.95, 0.01), Grass = (0.2, 0.15); // slicks on grass: ~0.4 against ~2.2 on asphalt
    }

    public class FormulaCar
    {
        const double G = 9.81, RHO = 1.225;
        public CarConfig cfg;

        // state
        public double x, y, heading, vx, vy, r;
        public double steerIn, delta, throttle, brake;
        public int gear = 1;
        public double rpm = 3000, shiftTimer, stopTimer;
        public bool reverse;
        public double axLong, ax, ay, ayF;
        public double fzF, fzR, useF, useR, alphaF, alphaR;
        public bool lockF, lockR, spinR, tcActive, absActive;
        public double surfaceMu = 1, surfaceDrag;
        // chassis detail: state and read-outs
        public double tow;                  // 0..1 how deep in another car's slipstream (set by the game)
        public double rideF = double.NaN, rideR, bodyRoll, pitch;      // m, m, rad, rad
        public double tyreTF = double.NaN, tyreTR;                 // deg C, front and rear tyres
        public double aeroBalance, downforce, dragForce;           // front share now, N, N
        public bool bottoming;
        public double grade;                // slope of the road in the direction the car points (rise per metre, + = uphill)
        public double wheelSpin;
        public double lockV = double.NaN, csBlend, lockAngle;
        public double wR = double.NaN;      // rear wheel spin speed (rad/s)
        public double slipRatioR, Fdrive, FxR, DR, torque;
        public double aeroX;                // 0 = wings closed (corner mode), 1 = open (straight mode)
        public double ersEnergy = double.NaN, ersHarvested, ersKw; // battery (J), harvested this lap (J), MGU-K power now (kW, - = charging)
        public void NewLap() { ersHarvested = 0; }

        public FormulaCar(CarConfig cfg = null) { this.cfg = cfg ?? new CarConfig(); }

        static double Clamp(double v, double a, double b) => v < a ? a : v > b ? b : v;
        static double Approach(double v, double t, double d) => v < t ? Math.Min(v + d, t) : Math.Max(v - d, t);
        static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

        public static double GearRatio(CarConfig c, int gear) =>
            (c.redline * 2 * Math.PI / 60 * c.wheelRadius) / (c.gearTopSpeeds[gear - 1] / 3.6);

        public static double TorqueAt(CarConfig c, double rpm)
        {
            double[] R = c.torqueRpm, F = c.torqueFrac; int n = R.Length;
            if (rpm <= R[0]) return c.peakTorque * F[0];
            for (int i = 1; i < n; i++)
                if (rpm <= R[i])
                {
                    double r0 = R[i - 1], f0 = F[i - 1], r1 = R[i], f1 = F[i];
                    return c.peakTorque * (f0 + (f1 - f0) * (rpm - r0) / (r1 - r0));
                }
            return c.peakTorque * F[n - 1];
        }

        // MGU-K power allowed at this road speed (FIA 2026 ramp-down): full to 290 km/h,
        // 1800 - 5 v [kW] to 340 km/h (100 kW), then down to zero at 345 km/h
        public static double ErsPowerAt(CarConfig c, double v)
        {
            double kmh = Math.Abs(v) * 3.6, f = kmh <= 290 ? 1 : kmh <= 340 ? (1800 - 5 * kmh) / 350 : kmh < 345 ? (345 - kmh) / 5 * (100.0 / 350) : 0;
            return c.ersPower * f;
        }

        // slip (in B*alpha units) where the magic formula peaks, for this C/E shape (cached)
        double peakKeyC = double.NaN, peakKeyE = double.NaN, peakCached;
        double PeakBa(CarConfig c)
        {
            if (c.tireC != peakKeyC || c.tireE != peakKeyE)
            {
                double best = 0, v = 1;
                for (double xx = 0.05; xx < 6; xx += 0.002)
                {
                    double f = Math.Sin(c.tireC * Math.Atan(xx - c.tireE * (xx - Math.Atan(xx))));
                    if (f > best) { best = f; v = xx; }
                }
                peakKeyC = c.tireC; peakKeyE = c.tireE; peakCached = v;
            }
            return peakCached;
        }

        static double Magic(CarConfig c, double alpha, double loadRatio = 1)
        {
            double Ba = c.tireB * Math.Pow(loadRatio, -c.slipGrowth) * alpha;
            return Math.Sin(c.tireC * Math.Atan(Ba - c.tireE * (Ba - Math.Atan(Ba))));
        }

        public void Shift(int dir)
        {
            int g = (int)Clamp(gear + dir, 1, cfg.gearTopSpeeds.Length);
            if (g != gear) { gear = g; shiftTimer = cfg.shiftTime; }
        }

        struct Combined { public double Fx, Fy, use; public bool slide, limited; }

        // front axle: longitudinal demand Fx combined with lateral Fy inside the friction circle D
        static Combined Combine(double Fx, double Fy, double D, bool limiter, double latPriority = 0.75)
        {
            bool limited = false;
            double lat = Math.Min(Math.Abs(Fy), D);
            if (limiter)
            {
                double floor = Fx > 0 ? 0.05 : 0.25;
                double maxFx = Math.Max(floor * D, Math.Sqrt(Math.Max(0, D * D - (latPriority * lat) * (latPriority * lat))) * 0.95);
                if (Math.Abs(Fx) > maxFx) { Fx = Math.Sign(Fx) * maxFx; limited = true; }
            }
            else
            {
                double lockAt = Math.Sqrt(Math.Max(0, D * D - (0.6 * lat) * (0.6 * lat))) * 0.98;
                if (Math.Abs(Fx) > lockAt)
                {
                    double k = Math.Min(1, (Math.Abs(Fx) - lockAt) / (0.6 * D));
                    double sk = k * k * (3 - 2 * k);
                    double fxOut = lockAt + (0.85 * D - lockAt) * sk;
                    double fyCap = Math.Sqrt(Math.Max(0, D * D - fxOut * fxOut)) * (1 - sk) + 0.12 * D * sk;
                    return new Combined { Fx = Math.Sign(Fx) * fxOut, Fy = Clamp(Fy, -fyCap, fyCap), slide = sk > 0.5, limited = limited, use = 1 + 0.1 * sk };
                }
            }
            double fyMax = Math.Sqrt(Math.Max(0, D * D - Fx * Fx));
            double use = Hypot(Fx, Fy) / D;
            Fy = Clamp(Fy, -fyMax, fyMax);
            return new Combined { Fx = Fx, Fy = Fy, slide = false, limited = limited, use = use };
        }

        struct RearForce { public double Fx, Fy, s, kap; }

        public void Step(CarInput input, double dt)
        {
            var c = cfg;
            double L = c.wheelbase, b = L * c.frontWeight, a = L - b; // a: CG->front, b: CG->rear
            double m = c.mass;
            double speed = Hypot(vx, vy);

            // ---------- steering filter ----------
            double target = Clamp(input.steer, -1, 1);
            double beta = Math.Atan2(vy, Math.Max(Math.Abs(vx), 1));
            bool countering = -target * beta > 0 && Math.Abs(beta) > 0.03;
            bool unwinding = Math.Abs(target) < Math.Abs(steerIn) || Math.Sign(target) != Math.Sign(steerIn);
            double rate = countering ? Math.Max(c.steerReturn, c.steerSpeed * 2) : unwinding ? c.steerReturn : c.steerSpeed;
            steerIn = Approach(steerIn, target, rate * dt);
            double fullLock = c.maxLockDeg * Math.PI / 180;
            // lock follows speed when turning in or straightening, but is not reduced while holding a steady input
            if (double.IsNaN(lockV) || speed < lockV) lockV = speed;
            else if (!countering && (Math.Abs(target) > Math.Abs(steerIn) + 0.02 || Math.Abs(steerIn) < 0.1)) lockV = Math.Min(speed, lockV + 10 * dt);
            double speedLock = fullLock / (1 + c.speedSens * (lockV / 30) * (lockV / 30));
            double cs = countering && c.csOpen ? Clamp(Math.Abs(beta) / c.csFull, 0, 1) : 0;
            csBlend = csBlend + (cs - csBlend) * Math.Min(1, dt * 12);
            double lockA = speedLock + (fullLock - speedLock) * csBlend;
            lockAngle = lockA;
            delta = -steerIn * lockA; // + delta = left

            throttle = Clamp(input.throttle, 0, 1);
            brake = Clamp(input.brake, 0, 1);
            double thr = throttle, brk = brake;

            // ---------- reverse: hold brake when stopped ----------
            if (!reverse)
            {
                if (speed < 0.5 && brk > 0.1 && thr < 0.05) { stopTimer += dt; if (stopTimer > 0.6) { reverse = true; gear = 1; } }
                else stopTimer = 0;
            }
            else if (thr > 0.1) { reverse = false; stopTimer = 0; }

            // ---------- aero + loads ----------
            double q = 0.5 * RHO * vx * vx;
            if (c.activeAero)
            {
                // wings open on the straights: full throttle, no brake, little cornering; they close at once otherwise
                bool open = thr > 0.9 && brk <= 0 && vx > 30 && Math.Abs(ayF) < (aeroX > 0.5 ? 9 : 6);
                aeroX = Approach(aeroX, open ? 1 : 0, (open ? 4 : 8) * dt);
            }
            double down = q * (c.clA + (c.clAX - c.clA) * aeroX), drag = q * (c.cdA + (c.cdAX - c.cdA) * aeroX);
            double aeroF = c.aeroFront, tempKF = 1, tempKR = 1;
            if (c.chassis)
            {
                if (double.IsNaN(rideF)) { rideF = c.rideF0; rideR = c.rideR0; }
                if (double.IsNaN(tyreTF)) { tyreTF = c.tyreStart; tyreTR = c.tyreStart; }
                // ground effect: the lower the floor runs, the more downforce, until it gets so low that it stalls
                double hm = (rideF + rideR) / 2;
                double downK = Clamp(1 + c.rideGain * (c.rideDesign - hm) / c.rideDesign, 0.8, 1.12) * Clamp(hm / c.rideStall, 0.6, 1);
                downK *= 1 - Math.Min(0.15, c.yawSens * Math.Abs(beta));   // sliding sideways spoils the airflow
                downK *= 1 - c.towDown * tow;                               // dirty air behind another car
                down *= downK; drag *= 1 - c.towDrag * tow;
                // nose down (braking) moves the aero balance forward; squatting (acceleration) moves it back
                aeroF = c.aeroFront + Clamp(c.rakeGain * ((rideR - rideF) - (c.rideR0 - c.rideF0)), -0.06, 0.06);
                // tyres give their best grip in a temperature window
                double eF = (tyreTF - c.tyreOpt) / c.tyreWindow, eR = (tyreTR - c.tyreOpt) / c.tyreWindow;
                tempKF = Math.Max(0.75, 1 - c.tyreLoss * eF * eF); tempKR = Math.Max(0.75, 1 - c.tyreLoss * eR * eR);
                // toe: the wheels are set a fraction of a degree off straight, which scrubs a little
                double tf = c.toeF * Math.PI / 180, tr2 = c.toeR * Math.PI / 180;
                drag += (tf * tf + tr2 * tr2) * 2 * 250e3 * (vx > 1 ? 1 : 0);
            }
            aeroBalance = aeroF; downforce = down; dragForce = drag;
            double transfer = m * axLong * c.cgHeight / L;
            double fz0F = m * G * b / L, fz0R = m * G * a / L;
            fzF = Math.Max(300, fz0F - transfer + down * aeroF);
            fzR = Math.Max(300, fz0R + transfer + down * (1 - aeroF));
            ayF = ayF + (ay - ayF) * Math.Min(1, dt * 10);
            double dLat = m * Math.Abs(ayF) * c.cgHeight / c.track;
            if (c.chassis)
            {
                // suspension: each axle sinks under load (downforce, braking, acceleration) and settles with a short lag
                double k = Math.Min(1, dt * c.heaveRate);
                double tgF = c.rideF0 - (fzF - fz0F) / c.springF, tgR = c.rideR0 - (fzR - fz0R) / c.springR;
                bottoming = tgF < 0.003 || tgR < 0.003;
                rideF += (Math.Max(0.003, tgF) - rideF) * k; rideR += (Math.Max(0.003, tgR) - rideR) * k;
                bodyRoll = m * ayF * c.cgHeight / c.rollStiff; pitch = ((rideR - rideF) - (c.rideR0 - c.rideF0)) / L;
            }
            double wLat = Clamp(Math.Abs(ayF) / 10, 0, 1), rollDeg = Math.Abs(bodyRoll) * 180 / Math.PI * 0.7;
            double AxleGrip(double fz, double fz0, double share, double camber)
            {
                double half = fz / 2, n = fz0 / 2, d = Math.Min(half * 0.95, dLat * share);
                double mo = 1, mi = 1;
                if (c.chassis)
                {
                    // camber: the wheels lean in at the top. In a corner that suits the loaded outer tyre and hurts
                    // the inner one; in a straight line it costs a little braking and traction.
                    double lean = Math.Abs(camber), straight = 1 - 0.004 * lean;
                    double po = lean - rollDeg, pi = -lean - rollDeg;
                    mo = straight + (1 + 0.012 * po - 0.0016 * po * po - straight) * wLat;
                    mi = straight + (1 + 0.012 * pi - 0.0016 * pi * pi - straight) * wLat;
                }
                return n * (Math.Pow((half + d) / n, c.loadExp) * mo + Math.Pow((half - d) / n, c.loadExp) * mi);
            }
            double DF = c.mu * surfaceMu * tempKF * AxleGrip(fzF, fz0F, c.latTransferFront, c.camberF);
            double DRl = c.mu * c.rearGrip * surfaceMu * tempKR * AxleGrip(fzR, fz0R, 1 - c.latTransferFront, c.camberR);

            // ---------- engine / drive ----------
            double ratio = GearRatio(c, gear);
            if (double.IsNaN(wR)) wR = vx / c.wheelRadius;
            double wheelRpm = Math.Abs(wR) * 60 / (2 * Math.PI);
            double rpmT = wheelRpm * ratio;
            double engage = Clamp(1 - Math.Abs(vx) / c.clutchEngage, 0, 1);
            double launch = c.idleRpm + thr * 0.5 * c.redline * engage;
            if (gear == 1 && rpmT < launch) rpmT = launch;
            rpmT = Math.Max(rpmT, c.idleRpm);
            rpm += (Math.Min(rpmT, c.redline + 200) - rpm) * Math.Min(1, dt * 30);

            // auto gearbox shifts on road speed, so wheelspin hits the limiter instead of shifting up
            double roadRpm = Math.Abs(vx) / c.wheelRadius * 60 / (2 * Math.PI);
            if (c.autoShift && !reverse && shiftTimer <= 0)
            {
                if (roadRpm * ratio > c.redline * 0.975 && gear < c.gearTopSpeeds.Length) Shift(1);
                else if (gear > 1)
                {
                    double rpmLower = roadRpm * GearRatio(c, gear - 1);
                    if (rpmLower < c.redline * 0.86 && roadRpm * ratio < c.redline * 0.68) Shift(-1);
                }
            }
            shiftTimer = Math.Max(0, shiftTimer - dt);

            double fdrive = 0;
            if (reverse)
            {
                if (vx > -2.8) fdrive = -brk * 3500;
                brk = 0;
            }
            else if (shiftTimer <= 0)
            {
                if (rpm < c.redline) fdrive = TorqueAt(c, rpm) * thr * ratio * c.driveEff / c.wheelRadius;
                if (thr < 0.05 && vx > 1) fdrive -= (rpm / c.redline) * 1200; // engine braking
            }
            // ---------- hybrid (MGU-K + battery) ----------
            double fK = 0, harvestW = 0;
            if (c.ersPower > 0)
            {
                if (double.IsNaN(ersEnergy)) ersEnergy = c.ersCapacity;
                double room = Math.Min(c.ersCapacity - ersEnergy, c.ersHarvestLap - ersHarvested);
                if (!reverse && shiftTimer <= 0 && thr > 0 && rpm < c.redline && ersEnergy > 0)
                {
                    fK = thr * c.driveEff * ErsPowerAt(c, vx) / Math.Max(Math.Abs(wR) * c.wheelRadius, 15);
                    fdrive += fK;
                }
                else if (!reverse && vx > 5 && room > 0)
                {
                    // charging: on the brakes the motor takes over part of the rear braking (no extra force);
                    // when coasting it adds some drag
                    if (brk > 0) harvestW = Math.Min(c.ersPower, brk * c.brakeForce * (1 - c.brakeBias) * vx);
                    else if (thr < 0.05) { harvestW = c.ersLiftPower; fdrive -= Math.Min(harvestW / vx, 1500); harvestW = Math.Min(harvestW, 1500 * vx); }
                }
            }

            // ---------- tyres ----------
            double vxs = Math.Max(Math.Abs(vx), 4);
            alphaF = Math.Atan2(vy + a * r, vxs) - delta * (vx >= 0 ? 1 : -1);
            alphaR = Math.Atan2(vy - b * r, vxs);
            double FyF = -DF * Magic(c, alphaF, fzF / fz0F);

            int dir = vx > 0.05 ? 1 : vx < -0.05 ? -1 : 0;
            double Bd = brk * c.brakeForce;
            double FxF = -dir * Bd * c.brakeBias;
            var rF = Combine(FxF, FyF, DF, c.abs);
            FxF = rF.Fx; FyF = rF.Fy;
            lockF = rF.slide; useF = rF.use;

            // ---------- rear axle: wheel speed + combined slip ----------
            double rw = c.wheelRadius, lrR = fzR / fz0R;
            double aPk = PeakBa(c) / (c.tireB * Math.Pow(lrR, -c.slipGrowth));
            double kPk = c.peakRatio, vden = Math.Max(Math.Abs(vx), 3);
            double syR = Math.Tan(alphaR) / Math.Tan(aPk);
            double vxNow = vx;
            RearForce RearTyre(double w)
            {
                double kap = (w * rw - vxNow) / vden, sx = kap / kPk, sv = Hypot(sx, syR);
                if (sv < 1e-6) return new RearForce { Fx = 0, Fy = 0, s = 0, kap = kap };
                double f = Magic(c, sv * aPk, lrR);
                double F = DRl * (sv > 1 ? Math.Max(f, 0.75) : f);
                return new RearForce { Fx = F * sx / sv, Fy = -F * syR / sv, s = sv, kap = kap };
            }
            double Iw = c.wheelInertia + c.engineInertia * ratio * ratio;
            double brakeT = Bd * (1 - c.brakeBias) * rw;
            double wv = wR;
            {
                var t0 = RearTyre(wv); double eps = 0.05;
                double slope = Math.Max(0, (RearTyre(wv + eps).Fx - t0.Fx) / eps) * rw;
                double zone = 0.5;
                double bT = brakeT * Clamp(wv / zone, -1, 1);
                double bSlope = brakeT > 0 && Math.Abs(wv) < zone ? brakeT / zone : 0;
                wv += dt * (fdrive * rw - t0.Fx * rw - bT) / (Iw + dt * (slope + bSlope));
            }
            tcActive = false;
            bool rearAbs = false;
            double KapOf(double ww) => (ww * rw - vxNow) / vden;
            if (fdrive > 0 && c.tc != TcMode.Off)
            {
                // low TC lets the tyres spin up more at a standing start (break traction, then hook up)
                double sMax = c.tc == TcMode.High ? 0.95 : 1.15 + 3.5 * Clamp(1 - Math.Abs(vxNow) / 25, 0, 1);
                double sxMax = Math.Max(c.tc == TcMode.High ? 0.3 : 0.6, Math.Sqrt(Math.Max(0, sMax * sMax - syR * syR)));
                if (KapOf(wv) > sxMax * kPk) { wv = (vxNow + sxMax * kPk * vden) / rw; tcActive = true; }
            }
            if (brk > 0 && c.abs && dir > 0)
            {
                double sxMax = Math.Max(0.35, Math.Sqrt(Math.Max(0, 0.9 - syR * syR)));
                if (KapOf(wv) < -sxMax * kPk) { wv = (vxNow - sxMax * kPk * vden) / rw; rearAbs = true; }
            }
            if (Math.Abs(vx) < 0.5 && thr < 0.05 && !reverse) wv = vx / rw; // parked: wheels just roll
            wR = wv;
            var tR = RearTyre(wv);
            double FxRl = tR.Fx, FyR = tR.Fy;
            slipRatioR = tR.kap; Fdrive = fdrive; FxR = FxRl; DR = DRl;
            torque = fdrive > 0 ? fdrive * rw / (ratio * c.driveEff) : 0;
            useR = tR.s <= 1 ? Hypot(FxRl, FyR) / DRl : 1 + 0.1 * Math.Min(tR.s - 1, 1);
            spinR = fdrive > 0 && tR.kap > 1.5 * kPk;
            lockR = brk > 0 && tR.kap < -0.5;
            absActive = (rF.limited || rearAbs) && fdrive <= 0;
            if (c.ersPower > 0)
            {
                // the battery only pays for electric drive that reaches the road (traction control cuts the rest)
                double used = fK > 0 && fdrive > 0 ? Math.Min(fK, Math.Max(FxRl, 0) * fK / fdrive) * Math.Abs(vx) / c.driveEff : 0;
                double got = Math.Min(harvestW * dt, Math.Max(0, Math.Min(c.ersCapacity - ersEnergy, c.ersHarvestLap - ersHarvested)));
                ersEnergy = Clamp(ersEnergy - used * dt + got, 0, c.ersCapacity); ersHarvested += got;
                ersKw = (used - got / dt) / 1000;
            }

            // ---------- resistances ----------
            double roll = (c.rollRes + surfaceDrag) * m * G;
            double resist = (drag + roll) * dir;

            // ---------- integrate ----------
            double cd = Math.Cos(delta), sd = Math.Sin(delta);
            double Fx = FxF * cd - FyF * sd + FxRl - resist - m * G * grade; // gravity pulls the car back on a climb
            double Fy = FxF * sd + FyF * cd + FyR;
            double Mz = a * (FxF * sd + FyF * cd) - b * FyR;
            if (c.chassis)
            {
                // differential: resists the rear wheels turning at different speeds, more so under power or on overrun
                double tLock = c.diffPreload + (fdrive > 0 ? c.diffPower : c.diffCoast) * Math.Abs(FxRl) * rw;
                Mz -= tLock / rw * (c.track / 2) * Math.Tanh(r * c.track / 2 / 0.3);
                // tyre heat: sliding and flexing heat the tyres, air and road cool them
                if (speed > 1)
                {
                    double pF = Math.Abs(FyF * vxs * Math.Tan(alphaF)) + Math.Abs(FxF) * Math.Abs(vx) * (lockF ? 0.6 : 0.04) + 0.6 * c.rollRes * fzF * speed;
                    double pR = Math.Abs(FyR * vxs * Math.Tan(alphaR)) + Math.Abs(FxRl * (wR * rw - vx)) + 0.6 * c.rollRes * fzR * speed;
                    double cool = c.tyreCool0 + c.tyreCoolV * speed;
                    tyreTF += dt * (c.tyreHeatShare * pF - cool * (tyreTF - c.trackTemp)) / c.tyreHeatCap;
                    tyreTR += dt * (c.tyreHeatShare * pR - cool * (tyreTR - c.trackTemp)) / c.tyreHeatCap;
                    tyreTF = Clamp(tyreTF, 20, 200); tyreTR = Clamp(tyreTR, 20, 200);
                }
            }
            ax = Fx / m; ay = Fy / m;

            vx += (ax + r * vy) * dt;
            vy += (ay - r * vx) * dt;
            r += Mz / c.inertiaZ * dt;

            if (dir != 0 && Math.Sign(vx) != dir && fdrive == 0) vx = 0;
            if (brk > 0 && Math.Abs(vx) < 0.3 && Math.Abs(fdrive) < 1) vx *= 0.5;

            if (speed < 3)
            {
                double k = 1 - speed / 3;
                double rk = vx * Math.Tan(delta) / L;
                double t = Math.Min(1, dt * 15) * k;
                r += (rk - r) * t;
                vy -= vy * t;
            }

            axLong += (ax - axLong) * Math.Min(1, dt * 8);

            double ch = Math.Cos(heading), sh = Math.Sin(heading);
            x += (vx * ch - vy * sh) * dt;
            y += (vx * sh + vy * ch) * dt;
            heading += r * dt;
            wheelSpin += wR * dt;
        }
    }
}
