// FormulaCar.cs — formula car vehicle model, a line-by-line port of the web prototype's physics.js.
// Pure C# (no UnityEngine), so it runs identically in Unity and in the command-line test harness.
// Units are SI (m, kg, s, N, rad). Doubles are used on purpose: results match the JS version bit-for-bit
// in normal driving, and Unity reads the outputs as floats.
//
// Physics frame: world x/y plane, heading 0 = +x, positive yaw = counter-clockwise (left).
// Car frame: vx forward, vy left, r yaw rate.
// Calibrated to the FIA Formula 2 car (see CarConfig for sources and tuning notes).
using System;

namespace FormulaSim
{
    public enum TcMode { Off, Low, High }

    [Serializable]
    public class CarConfig
    {
        public double mass = 795;            // kg: 755 FIA minimum (with driver) + ~40 kg fuel
        public double inertiaZ = 950;        // kg m^2 yaw inertia (estimate)
        public double wheelbase = 3.135;     // m (FIA)
        public double frontWeight = 0.45;    // static weight on front axle
        public double cgHeight = 0.28;       // m
        public double wheelRadius = 0.33;    // m (13" rim + slick)
        public double mu = 2.2;              // effective peak friction (tuned with clA 4.0: FIA 3.9 g in fast corners)
        public double rearGrip = 1.08;       // wider rear tyres
        public double loadExp = 0.8;         // max tyre force ~ load^0.8
        public double tireB = 14.5, tireC = 1.6, tireE = 0.3; // magic formula, peak ~6.7 deg
        public double slipGrowth = 0.3;      // peak slip angle grows with load
        public double peakRatio = 0.11;      // longitudinal slip ratio at peak traction
        public double wheelInertia = 1.6;    // kg m^2, both rear wheels + tyres + discs
        public double engineInertia = 0.045; // kg m^2, felt at the wheel x ratio^2
        public double track = 1.50;          // m, track width (lateral weight transfer)
        public double latTransferFront = 0.5;
        public double cdA = 1.05;            // drag area m^2
        public double clA = 4.0;             // downforce area m^2
        public double aeroFront = 0.45;
        public double rollRes = 0.015;
        public double peakTorque = 600;      // Nm (FIA)
        public double redline = 8750, idleRpm = 3000;
        public double[] gearTopSpeeds = { 128, 168, 207, 245, 283, 320 }; // km/h at the limiter
        public double driveEff = 0.80;
        public double shiftTime = 0.05;
        public double clutchEngage = 13;     // m/s (47 km/h) by which the launch clutch is fully engaged
        public double brakeForce = 22500;    // N total at full pedal
        public double brakeBias = 0.60;      // front share
        public double maxLockDeg = 18;
        public double speedSens = 1.0;
        public double steerSpeed = 3.0;
        public double steerReturn = 8.0;
        public bool csOpen = true;           // open up to full lock when countersteering
        public TcMode tc = TcMode.Low;
        public bool abs = true, autoShift = true;

        public CarConfig Clone()
        {
            var c = (CarConfig)MemberwiseClone();
            c.gearTopSpeeds = (double[])gearTopSpeeds.Clone();
            return c;
        }
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
        public static readonly (double mu, double drag) Asphalt = (1.0, 0.0), Kerb = (0.95, 0.01), Grass = (0.42, 0.15);
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
        public double wheelSpin;
        public double lockV = double.NaN, csBlend, lockAngle;
        public double wR = double.NaN;      // rear wheel spin speed (rad/s)
        public double slipRatioR, Fdrive, FxR, DR, torque;

        public FormulaCar(CarConfig cfg = null) { this.cfg = cfg ?? new CarConfig(); }

        static double Clamp(double v, double a, double b) => v < a ? a : v > b ? b : v;
        static double Approach(double v, double t, double d) => v < t ? Math.Min(v + d, t) : Math.Max(v - d, t);
        static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

        public static double GearRatio(CarConfig c, int gear) =>
            (c.redline * 2 * Math.PI / 60 * c.wheelRadius) / (c.gearTopSpeeds[gear - 1] / 3.6);

        static readonly double[,] TorquePts = { { 2000, 0.40 }, { 4000, 0.62 }, { 5500, 0.88 }, { 6500, 1.0 }, { 7500, 0.97 }, { 8750, 0.84 } };
        public static double TorqueAt(CarConfig c, double rpm)
        {
            int n = TorquePts.GetLength(0);
            if (rpm <= TorquePts[0, 0]) return c.peakTorque * TorquePts[0, 1];
            for (int i = 1; i < n; i++)
                if (rpm <= TorquePts[i, 0])
                {
                    double r0 = TorquePts[i - 1, 0], f0 = TorquePts[i - 1, 1], r1 = TorquePts[i, 0], f1 = TorquePts[i, 1];
                    return c.peakTorque * (f0 + (f1 - f0) * (rpm - r0) / (r1 - r0));
                }
            return c.peakTorque * TorquePts[n - 1, 1];
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
            double cs = countering && c.csOpen ? Clamp(Math.Abs(beta) / 0.14, 0, 1) : 0;
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
            double down = q * c.clA, drag = q * c.cdA;
            double transfer = m * axLong * c.cgHeight / L;
            double fz0F = m * G * b / L, fz0R = m * G * a / L;
            fzF = Math.Max(300, fz0F - transfer + down * c.aeroFront);
            fzR = Math.Max(300, fz0R + transfer + down * (1 - c.aeroFront));
            ayF = ayF + (ay - ayF) * Math.Min(1, dt * 10);
            double dLat = m * Math.Abs(ayF) * c.cgHeight / c.track;
            double AxleGrip(double fz, double fz0, double share)
            {
                double half = fz / 2, n = fz0 / 2, d = Math.Min(half * 0.95, dLat * share);
                return n * (Math.Pow((half + d) / n, c.loadExp) + Math.Pow((half - d) / n, c.loadExp));
            }
            double DF = c.mu * surfaceMu * AxleGrip(fzF, fz0F, c.latTransferFront);
            double DRl = c.mu * c.rearGrip * surfaceMu * AxleGrip(fzR, fz0R, 1 - c.latTransferFront);

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

            // ---------- resistances ----------
            double roll = (c.rollRes + surfaceDrag) * m * G;
            double resist = (drag + roll) * dir;

            // ---------- integrate ----------
            double cd = Math.Cos(delta), sd = Math.Sin(delta);
            double Fx = FxF * cd - FyF * sd + FxRl - resist;
            double Fy = FxF * sd + FyF * cd + FyR;
            double Mz = a * (FxF * sd + FyF * cd) - b * FyR;
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
