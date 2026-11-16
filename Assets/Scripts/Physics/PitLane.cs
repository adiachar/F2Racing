// PitLane.cs — the pit lane as a path the cars drive (pure C#, no UnityEngine: the headless tools compile it too).
//
//  * The path is described by the track distance u (m from the timing line, negative before it) and a lateral offset
//    from the centreline (+ = left, the track's normal). Track distance in the lane maps 1:1 to lap distance, so race
//    positions, timing loops and the line crossing work while a car is in the pits.
//  * Shape (one side of the start straight, Trackside.PitSide): entry at sEntry, a ramp from the track edge out to
//    the fast lane (behind the pit wall), the fast lane along the garages (limiter zone limA..limB), a ramp back
//    to the track edge at sExit. Entry/exit come from the pit building's extent (Trackside: pitA..pitB) plus ~110 m
//    of road, and are at least 200 m before / after the line. On the inside of a bend the offset is capped (no fold).
//  * A stop (PitRun): kinematic, as a pit lane is driven: brake from track speed to the 80 km/h limiter by the
//    limiter line, follow the fast lane, swing into the team's box (Trackside.Bays) and stop exactly on it, service
//    (2.4 s, + the wing change), swing out, limiter to the exit, hand back at the end of the exit ramp. Cars in the
//    lane keep a gap to the one ahead; a car finding its box taken waits behind it.
//  * Time lost against staying out (Monza, ~500 m lane): ~20-24 s, from the drive itself (no time is added).
using System;
using System.Collections.Generic;

namespace FormulaSim
{
    public sealed class PitLane
    {
        public const double Limiter = 80 / 3.6;         // m/s
        public const double Service = 2.4, TyresOn = 1.06; // s from the stop: release, new wheels on (PitCrew's timeline)
        public const double AStop = 13, AGo = 8, AOut = 9, RoadIn = 3.0, RoadOut = 3.25;
        public readonly Track track; public readonly int side;     // side: +1 left, -1 right of the direction of travel
        public readonly double sEntry, sExit, sA, sB, limA, limB, laneLat;
        public readonly double[] bayU, bayLat;
        readonly object[] bayOwner;
        readonly double rampIn, rampOut;
        readonly List<PitRun> runs = new List<PitRun>();

        // sA..sB: the pit building (track distance from the line); laneLat: the fast lane's distance from the centreline;
        // bays: each box's track distance and signed lateral offset
        public PitLane(Track t, int side, double sA, double sB, double laneLat, IList<double> bayU, IList<double> bayLat)
        {
            track = t; this.side = side >= 0 ? 1 : -1; this.sA = sA; this.sB = sB; this.laneLat = laneLat;
            double lim = 0.3 * t.length;
            sEntry = Math.Max(-lim, Math.Min(sA - 110, -200)); sExit = Math.Min(lim, Math.Max(sB + 110, 200));
            limA = Math.Max(sEntry + 30, sA - 10); limB = Math.Min(sExit - 30, sB + 10);
            rampIn = limA - sEntry; rampOut = sExit - limB;
            int n = bayU != null ? bayU.Count : 0;
            this.bayU = new double[n]; this.bayLat = new double[n]; bayOwner = new object[n];
            for (int i = 0; i < n; i++) { this.bayU[i] = bayU[i]; this.bayLat[i] = bayLat[i]; }
        }

        // ---------------------------------------------------------------- geometry
        int W(int i) { int N = track.N; return ((i % N) + N) % N; }
        public int TrackIdx(double u) => W((int)Math.Round(u / track.ds));
        // signed track distance of a track point from the line (-L/2..L/2)
        public static double RelS(Track t, int idx) => (idx > t.N / 2 ? idx - t.N : idx) * t.ds;
        public static double Smooth(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);
        public bool InLimiter(double u) => u >= limA && u <= limB;
        public bool OnRamp(double u) => (u >= sEntry && u <= limA) || (u >= limB && u <= sExit);

        double BaseAt(int i, double u)
        {
            int k = W(i);
            double edge = track.HalfWidth(k) - 1.2;
            double m = Math.Min(Smooth((u - sEntry) / rampIn), Smooth((sExit - u) / rampOut));
            double L = edge + (laneLat - edge) * m;
            double kin = side * track.pts[k].curv; // inside of a bend: keep well within the radius
            if (kin > 1e-5) L = Math.Min(L, Math.Max(edge, 0.8 / kin));
            return side * L;
        }
        // the lane's own lateral offset (signed) at track distance u
        public double BaseLat(double u)
        {
            double fi = u / track.ds; int i0 = (int)Math.Floor(fi); double f = fi - i0;
            return BaseAt(i0, u) * (1 - f) + BaseAt(i0 + 1, u) * f;
        }
        // world position of (u, lateral)
        public void Pos(double u, double lat, out double x, out double y)
        {
            double fi = u / track.ds; int i0 = (int)Math.Floor(fi); double f = fi - i0;
            var a = track.pts[W(i0)]; var b = track.pts[W(i0 + 1)];
            double nx = a.nx + (b.nx - a.nx) * f, ny = a.ny + (b.ny - a.ny) * f, nl = Math.Sqrt(nx * nx + ny * ny);
            if (nl > 1e-9) { nx /= nl; ny /= nl; }
            x = a.x + (b.x - a.x) * f + nx * lat; y = a.y + (b.y - a.y) * f + ny * lat;
        }

        // ---------------------------------------------------------------- boxes and traffic
        public bool Claim(int bay, object who)
        {
            if (bay < 0 || bay >= bayOwner.Length) return true;
            if (bayOwner[bay] == null || bayOwner[bay] == who) { bayOwner[bay] = who; return true; }
            return false;
        }
        public void Free(int bay, object who) { if (bay >= 0 && bay < bayOwner.Length && bayOwner[bay] == who) bayOwner[bay] = null; }
        public void Reset() { runs.Clear(); for (int i = 0; i < bayOwner.Length; i++) bayOwner[i] = null; }
        internal void Add(PitRun r) { if (!runs.Contains(r)) runs.Add(r); }
        internal void Remove(PitRun r) { runs.Remove(r); }
        public int Count => runs.Count;
        // the nearest car ahead in the same part of the lane (m, its speed); MaxValue = none
        internal double GapAhead(PitRun r, out double vAhead)
        {
            double best = double.MaxValue; vAhead = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                var o = runs[i]; if (o == r || o.Done) continue;
                double g = o.u - r.u; if (g <= 0 || g >= best) continue;
                if (Math.Abs(o.lateral - r.lateral) > 2.4) continue;
                best = g; vAhead = o.v;
            }
            return best;
        }
    }

    // One car's trip through the pit lane. Step it every physics step; read the pose (x, y, heading), speed and the
    // track point. TakeService() is true once, when the new wheels go on (apply tyres / repairs then).
    public sealed class PitRun
    {
        public readonly PitLane lane; public readonly int bay; public readonly object who;
        public double u, v, accel, x, y, heading, yawRate, lateral;
        public int idx;
        public int phase;                 // 0 to the box, 1 service, 2 out, 3 done (handed back)
        public double serviceT, serviceLen, extraHold; // s since the stop (keeps counting after the release), length of the service, extra (wing)
        public bool frozen, claimed;      // frozen: shot mode (pose only)
        public double laneTime;           // s since the entry
        bool serviceDue, serviceTaken;
        readonly double u0, corr, aIn, uBox, shift;

        // u, v, lat: the car's track distance, speed and lateral offset at the entry; extra: seconds beyond a normal stop
        public PitRun(PitLane lane, int bay, object who, double u, double v, double lat, double extra)
        {
            this.lane = lane; this.who = who; this.u = u0 = u; this.v = Math.Max(0, v); extraHold = Math.Max(0, extra);
            this.bay = bay >= 0 && bay < lane.bayU.Length ? bay : -1;
            uBox = this.bay >= 0 ? lane.bayU[this.bay] : (lane.limA + lane.limB) * 0.5;
            shift = this.bay >= 0 ? lane.bayLat[this.bay] - lane.BaseLat(uBox) : 0;
            corr = lat - lane.BaseLat(u);
            double vL = PitLane.Limiter, d = lane.limA - u;
            aIn = d > 5 ? Math.Max(4, Math.Min(60, (this.v * this.v - vL * vL) / (2 * d))) : 60;
            lane.Add(this);
            Pose(0);
        }
        // shot mode: a car standing in its box, t s into the service, not moving
        public static PitRun InBox(PitLane lane, int bay, object who, double t)
        {
            var r = new PitRun(lane, bay, who, lane.sEntry, PitLane.Limiter, lane.BaseLat(lane.sEntry), 0);
            r.u = r.uBox; r.v = 0; r.phase = 1; r.claimed = lane.Claim(r.bay, who); r.serviceT = Math.Max(0, t); r.serviceLen = PitLane.Service;
            r.serviceTaken = true; r.frozen = true; r.Pose(0);
            return r;
        }

        public bool Done => phase == 3;
        public bool InLimiter => lane.InLimiter(u);
        public double BoxU => uBox;
        // the pit crew's clock: s to the stop (negative) before it, s since the stop after it
        public double Clock => phase == 0 ? -TimeToStop() : serviceT;
        public bool TakeService() { if (!serviceDue) return false; serviceDue = false; return true; }
        public void Cancel() { lane.Free(bay, who); lane.Remove(this); phase = 3; }

        double TimeToStop()
        {
            if (!claimed) return 99;
            double d = Math.Max(0, uBox - u), a = PitLane.AStop, v1 = Math.Max(v, 0.5);
            if (d <= v1 * v1 / (2 * a)) return 2 * d / v1;
            double vL = PitLane.Limiter;
            return Math.Max(0, (d - vL * vL / (2 * a)) / vL) + vL / a;
        }

        // the lateral offset of this car's path at track distance s (entry blend, the swing into and out of the box)
        double Lat(double s)
        {
            double L = lane.BaseLat(s);
            double k = (s - u0) / 60; if (k < 1) L += corr * (1 - PitLane.Smooth(k));
            if (shift != 0)
                L += shift * (phase < 2 ? PitLane.Smooth((s - (uBox - 24)) / 18) : 1 - PitLane.Smooth((s - (uBox + 3)) / 18));
            return L;
        }
        double Follow()
        {
            double g = lane.GapAhead(this, out double va);
            return g >= 1e8 ? double.MaxValue : Math.Max(0, va + (g - 9) * 0.8);
        }
        void Advance(double dt)
        {
            lane.Pos(u - 0.5, Lat(u - 0.5), out double x0, out double y0); lane.Pos(u + 0.5, Lat(u + 0.5), out double x1, out double y1);
            double f = Math.Max(0.5, Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));
            u += v * dt / f;
        }

        public void Step(double dt)
        {
            if (phase == 3) return;
            if (frozen) { Pose(0); return; }
            laneTime += dt;
            double vL = PitLane.Limiter, v0 = v;
            if (phase == 0)
            {
                if (!claimed) claimed = lane.Claim(bay, who);
                double uT = claimed ? uBox : uBox - 9;   // box taken (team-mate): wait behind it
                double vmax = u < lane.limA ? Math.Sqrt(vL * vL + 2 * aIn * (lane.limA - u)) : vL;
                double vt = Math.Min(Math.Min(vmax, Math.Sqrt(2 * PitLane.AStop * Math.Max(0, uT - u))), Follow());
                v = Math.Max(0, Math.Min(v + PitLane.AGo * dt, vt));
                Advance(dt);
                if (u > uT) u = uT;
                if (claimed && uBox - u < 0.03 && v < 0.6) { u = uBox; v = 0; phase = 1; serviceT = 0; serviceLen = PitLane.Service + extraHold; }
                else if (uT - u < 0.03 && v < 0.6) v = 0;
            }
            else if (phase == 1)
            {
                v = 0; serviceT += dt;
                if (!serviceTaken && serviceT >= PitLane.TyresOn) { serviceTaken = true; serviceDue = true; }
                if (serviceT >= serviceLen) phase = 2;
            }
            else
            {
                serviceT += dt;
                v = Math.Max(0, Math.Min(v + PitLane.AOut * dt, Math.Min(vL, Follow())));
                Advance(dt);
                if (claimed && u > uBox + 7) { lane.Free(bay, who); claimed = false; }
                if (u >= lane.sExit) { u = lane.sExit; phase = 3; lane.Free(bay, who); lane.Remove(this); }
            }
            accel = dt > 0 ? (v - v0) / dt : 0;
            Pose(dt);
        }

        void Pose(double dt)
        {
            double L = Lat(u);
            lane.Pos(u, L, out x, out y);
            lane.Pos(u - 0.5, Lat(u - 0.5), out double x0, out double y0); lane.Pos(u + 0.5, Lat(u + 0.5), out double x1, out double y1);
            double h = Math.Atan2(y1 - y0, x1 - x0);
            if (dt > 0) { double dh = Math.Atan2(Math.Sin(h - heading), Math.Cos(h - heading)); yawRate = dh / dt; } else yawRate = 0;
            heading = h; lateral = L; idx = lane.TrackIdx(u);
        }

        // the car follows the run: pose, speed, pedals and revs (for the sound and the dash), steering from the path
        public void ApplyTo(FormulaCar c)
        {
            c.x = x; c.y = y; c.heading = heading; c.vx = v; c.vy = 0; c.r = yawRate;
            c.delta = v > 0.5 ? Math.Atan(c.cfg.wheelbase * yawRate / v) : c.delta * 0.9;
            c.axLong = accel; c.ax = accel; c.ay = v * yawRate;
            c.throttle = accel > 0.3 ? 0.45 : v > 1 && accel > -0.3 ? 0.15 : 0;
            c.brake = accel < -0.5 ? Math.Min(1, -accel / 40) : 0;
            c.wR = v / c.cfg.wheelRadius;
            var tops = c.cfg.gearTopSpeeds; int g = 1;
            if (tops != null && tops.Length > 0)
            {
                while (g < tops.Length && v * 3.6 > tops[g - 1] * 0.8) g++;
                c.rpm = Math.Max(c.cfg.idleRpm, c.cfg.redline * v * 3.6 / Math.Max(1, tops[g - 1]));
            }
            c.gear = g;
        }
        // back on the track: the car keeps the speed and heading of the lane, its wheels re-start from the road speed
        public void HandBack(FormulaCar c)
        {
            c.x = x; c.y = y; c.heading = heading; c.vx = v; c.vy = 0; c.r = 0; c.wR = double.NaN;
            var tops = c.cfg.gearTopSpeeds; int g = 1;
            if (tops != null) while (g < tops.Length && v * 3.6 > tops[g - 1] * 0.8) g++;
            c.gear = g;
        }
    }
}
