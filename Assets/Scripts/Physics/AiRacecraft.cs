// AiRacecraft.cs — the rivals' race craft: what a driver decides about the cars around him. AiDriver.Think calls
// Racecraft once per think (60 Hz) in place of the old "pull out / keep a gap" rule. Decisions only: the car, its
// physics and the driving controller are untouched, and with nobody within reach the driver does exactly what he did
// before (same offset decay and clamp, no speed cap), so solo laps and the trainer are unchanged.
//
// What a driver does:
//  * sees every car from 60 m behind to 160 m ahead: gap along the track, side, closing speed, braking (its
//    deceleration), and predicts where it will be over the next 1-2 s (a rival's lane from his offset and the offset
//    he is moving to; the player's from his offset from the racing line, held);
//  * never runs into the back of a car in his lane: a safe-following speed (Gipps: he can stop behind the car ahead
//    even if it brakes as hard as it can, after his own reaction time; its deceleration now is read in) — the bolder
//    the driver, the shorter his reaction margin, so the aggressive ones sit right on the gearbox;
//  * side by side, leaves a car's width (2.4 m centre to centre) to every car with overlap, all along his path: does
//    not turn in on it, does not squeeze it on the exit, does not cut back across a car he has just passed. When there
//    is no room for both on his path in the next 1.2 s, the car that is behind backs out (tucks in behind), the one
//    ahead holds his line (also against the player: level or ahead, he does not lift);
//  * under VSC / safety car: no racing, the queue's plain gap keeping as before; a stopped car is still driven round;
//  * takes a different line when the racing line is occupied: a car in his way that is clearly slower is passed on
//    whichever side has room, even in a bend;
//  * builds an overtake where it can work: the lap's best places to pass are its biggest braking zones after a run
//    long enough for a tow (FitGrip: passQ). Held up, he sits on the gearbox, through the slow corners too (pressure),
//    flat out in the slipstream (AiDriver: the tow's lower drag takes him over his own profile) and carries that speed
//    when he pulls out (no lifting back to his profile). He pulls out late, from close behind with the closing speed
//    the tow built (out of the tow the drag takes it back within a second), to the inside of the coming corner; round
//    the outside only when the pass is done before the braking point. Or he dives: up the inside in the last second
//    before the braking zone of a place to pass, and brakes later than his everyday profile, nearer the car's limit
//    (DiveSpeed: diveK x his braking, at most 98 % of the tyres, his own trail braking into the apex), close enough
//    that it gets him alongside; no overlap by the end of the braking: back in behind. Half a car alongside (three
//    quarters in a dive), the corner is shared: he holds his line, and an AI attacked like that gives the room (one of
//    the two always yields). Energy: Overtake Mode is saved for the straight into one of the lap's best places to pass,
//    from the exit when he still has to close into the tow, else timed to last to the braking point; the battery boost
//    only for the move itself, with a reserve kept. Switches side once (switchback) or aborts when the side closes
//    before he can get alongside, then waits before another try (no weaving);
//  * defends: one move per straight, to cover the inside before the braking zone (not when the attacker is already
//    there: no late block), no weaving; then back to the line for the corner, leaving room;
//  * a lane off the racing line is driven at a speed for its own curvature (an inside line is tighter and slower), so
//    a car never carries racing-line speed onto a tighter path; off the line in a bend he does not run ahead of his
//    profile, and in dirty air (the tow behind a car) he takes a bend that much slower. The path stays within the
//    track: the aim point is clamped 1 m inside the edge, as before (no gaining time across the track limits).
// Own dice (craftRng), so the driving's random sequence (lap pace, mistakes) is as before. No allocation per think.
using System;
using System.Collections.Generic;

namespace FormulaSim
{
    public partial class AiDriver
    {
        const int MaxSeen = 48, MaxBounds = 8, MaxPath = 96;
        const double Sep = 2.4;           // m, centre to centre beside another car (1.9 m wide cars, room for error)
        const double CarLen = 4.7;        // m, centre to centre nose to tail
        const double DiveK0 = 1.10, DiveKA = 0.08;   // late-braking dive: braking share x (DiveK0 + DiveKA x aggression), at most 98 % of the tyres
        const double DiveApex = 1.25;                // ... down to this x the zone's slowest speed, then his own braking into the apex
        const double PassZoneQ = 0.6, OtZoneQ = 0.75; // places to pass: braking zones this good x the lap's best; Overtake Mode only into these
        // the cars seen this think (fixed arrays)
        readonly double[] sGap = new double[MaxSeen], sLat = new double[MaxSeen], sDOff = new double[MaxSeen], sDOffT = new double[MaxSeen], sV = new double[MaxSeen], sAx = new double[MaxSeen];
        readonly int[] sIdx = new int[MaxSeen]; readonly object[] sRef = new object[MaxSeen]; readonly bool[] sPl = new bool[MaxSeen];
        int seen;
        // cars beside (or about to be) that must be left room: index into the seen arrays and the side they are on (+1 left)
        readonly int[] bCar = new int[MaxBounds], bSide = new int[MaxBounds]; int bounds;
        readonly double[] wL = new double[2 * MaxPath], wR = new double[2 * MaxPath], sL = new double[2 * MaxPath], sR = new double[2 * MaxPath], qL = new double[2 * MaxPath], qR = new double[2 * MaxPath], pDev = new double[MaxPath], pX = new double[MaxPath], pY = new double[MaxPath], lX = new double[MaxPath], lY = new double[MaxPath];
        Random craftRng;
        int[] nextBrake, nextCorner; sbyte[] cornerSgn;
        double muLat = -1;                // lateral grip this driver uses at the cornering limit of his profile (FitGrip)
        int mode;                         // 0 free, 1 attacking atkRef on side atkSide, 2 defending (holding defOff)
        int atkSide, atkSwitch, lastSide; object atkRef; double atkT, heldT, defOff, atkCool = -99, lastSideT = -99; bool defDone;
        object passedBy, passedCar; double passedByT = -99, passedCarT = -99;
        double pathCap = double.MaxValue, laneRate = 2.2;
        bool held;                        // this think: held below his own speed by a car in his lane
        bool prepPass;                    // this think: lining up a pass on the straight (closing up in the tow)
        // the braking zones of his own profile (FitGrip): for every point the end of the next one (the first point past
        // it), how good a place to pass it is (the speed it sheds, less when the run before it is short) and its corner's
        // inside (+1 left)
        int[] zoneEnd, zoneStart; float[] passQ; sbyte[] zoneSgn; double passQMax = 1;   // zoneStart: by zone end
        double diveK = 1;                 // a late-braking dive's braking share over his own (AiDriver constructor)
        bool diveOn; int diveEnd = -1;    // diving up the inside into the braking zone that ends at diveEnd
        bool ersAttack, ersPush;          // this think: Overtake Mode on the attack straight; the battery boost for the pass
        public int overtakesMade, defMoves, divesMade, attacksMade;   // completed passes, defensive moves, dives, attacks (diagnostics)
        public readonly int[] attackEnds = new int[6];   // how attacks ended: 0 past him, 1 door closed, 2 not gaining, 3 dive without overlap, 4 gave way, 5 lost sight
        public int CraftMode => mode;
        int dbgWhy; double dbgBrake;      // the test tools' read-out: why the last attack started, the braking point then
        // for the test tools only (allocates): the race craft's state now
        public string CraftDebug => $"mode {mode}{(mode == 1 ? $"[{dbgWhy} {dbgBrake:F0}]" : "")}{(diveOn ? " DIVE" : "")} side {atkSide} held {heldT:F2} cap {(speedCap > 1e6 ? -1 : speedCap * 3.6):F0} path {(pathCap > 1e6 ? -1 : pathCap * 3.6):F0} off {offset:F2}/{offsetTarget:F2} seen {seen} bounds {bounds}{(ersAttack ? " OT" : "")}{(ersPush ? " PUSH" : "")}{(car.otArmed ? " arm" : "")}{(car.otOn ? " otOn" : "")}{(car.otActive ? " otA" : "")} otLeft {car.OtLeft / 1e6:F2}{(car.boostActive ? " bA" : "")} soc {car.ersEnergy / car.cfg.ersCapacity:F2}";

        // safe deceleration a car can count on at speed v (m/s^2): about 1.5 g slow, 4.8 g at 290 km/h
        static double ACap(double v) => 9.81 * (1.5 + 3.3 * Math.Min(1, v * v / 6400));

        // the speed profile at point i (as Think's V)
        double SpeedAt(int i) => speedFull == null ? speed[i] : speed[i] + (speedFull[i] - speed[i]) * (double.IsNaN(car.fuel) ? 1 : Clamp(car.fuel / car.cfg.fuelStart, 0, 1));

        // the speed at point i of a late-braking dive: the braking zone at diveK x his own deceleration down to 1.25 x the
        // zone's slowest speed, then his own profile into the corner (so the turn-in and the trail braking are his usual
        // ones; only the straight-line part, where the tyres have the most to give, brakes later)
        // Before the zone (on the straight) it is the zone's braking curve carried back at its first deceleration, so a car
        // carried over its profile by the tow still brakes later than it would.
        double DiveSpeed(int i)
        {
            double vi = SpeedAt(i); if (zoneEnd[i] != diveEnd) return vi;   // only the braking zone of the dive
            int N = track.N, st = zoneStart[diveEnd], dSt = (st - i + N) % N, dEnd = (diveEnd - i + N) % N;
            if (dSt < dEnd && dSt > 0)
            {
                double v0 = SpeedAt(st), v1 = SpeedAt((st + 1) % N), a0 = Math.Max(0, (v0 * v0 - v1 * v1) / (2 * track.ds));
                vi = Math.Sqrt(v0 * v0 + 2 * a0 * dSt * track.ds);
            }
            double ve = SpeedAt(diveEnd), d2 = vi * vi - DiveApex * DiveApex * ve * ve;
            return d2 <= 0 ? vi : Math.Sqrt(vi * vi + (diveK - 1) * d2);
        }

        void SeeCar(int oIdx, double oLat, FormulaCar oc, double dOff, double dOffT, object who0, bool isPl, bool racing)
        {
            int N = track.N;
            int d = oIdx - idx; if (d > N / 2) d -= N; else if (d < -N / 2) d += N;
            double gap = d * track.ds;
            if (gap > 1.5 && gap < aheadGap) aheadGap = gap; else if (gap < -1.5 && -gap < behindGap) behindGap = -gap;
            if (!racing || gap < -60 || gap > 160 || seen >= MaxSeen) return;
            int k = seen++;
            sGap[k] = gap; sLat[k] = oLat; sDOff[k] = dOff; sDOffT[k] = dOffT; sIdx[k] = oIdx; sRef[k] = who0; sPl[k] = isPl;
            sV[k] = Math.Sqrt(oc.vx * oc.vx + oc.vy * oc.vy); sAx[k] = oc.ax;
        }

        // predicted lateral (m from the centreline) of seen car c at track point j, tau s from now
        double LatOf(int c, int j, double tau)
        {
            double d = sDOff[c] + Clamp(sDOffT[c] - sDOff[c], -2.5 * tau, 2.5 * tau), hw = track.HalfWidth(j) - 0.9;
            return Clamp(line.off[j] + d, -hw, hw);
        }

        // the room the cars beside leave at point j (tau s ahead): lo..hi for this car's centre (lo > hi: no room)
        void BoundsAt(int j, double tau, double room, out double lo, out double hi)
        {
            lo = -room; hi = room;
            for (int b = 0; b < bounds; b++)
            {
                double Lc = LatOf(bCar[b], j, tau);
                if (bSide[b] > 0) hi = Math.Min(hi, Lc - Sep); else lo = Math.Max(lo, Lc + Sep);
            }
        }
        double ApplyBounds(int j, double L, double tau, double room)
        {
            BoundsAt(j, tau, room, out double lo, out double hi);
            if (lo > hi) return Clamp(0.5 * (lo + hi), -room, room);
            return Clamp(L, lo, hi);
        }

        // our planned offset from the line tau s from now
        double OffPlan(double tau) => offset + Clamp(offsetTarget - offset, -laneRate * tau, laneRate * tau);

        // Is there room for this car on side sgn (+1 left) of seen car c over the next `dist` m (2.6 m beside his
        // predicted lane, inside the track, no third car there)? Returns the least slack (m; < 0: no room).
        double SideRoom(int c, int sgn, double v, double dist)
        {
            int N = track.N, st = Math.Max(2, (int)Math.Round(8 / track.ds)), n = (int)(dist / (st * track.ds)) + 1;
            double slack = 1e9;
            for (int k = 0; k <= n; k++)
            {
                int j = (sIdx[c] + k * st) % N; double tau = k * st * track.ds / Math.Max(8, v);
                double room = track.HalfWidth(j) - 1.0, mine = LatOf(c, j, tau) + sgn * 2.6;
                slack = Math.Min(slack, room - sgn * mine);
            }
            // another car already in that lane, beside or just ahead
            for (int o = 0; o < seen; o++)
            {
                if (o == c || sGap[o] < -6 || sGap[o] > sGap[c] + 25) continue;
                double mine = sLat[c] + sgn * 2.6;
                if (Math.Abs(sLat[o] - mine) < 2.2 && (sLat[o] - sLat[c]) * sgn > 0.8) slack = Math.Min(slack, -1);
            }
            return slack;
        }

        // How far ahead of the car being attacked the lane must stay open: to the end of the straight (the braking
        // zone, where the line swings across to the outside) or the turn-in, whichever comes first; 40 m at least
        static double AttackLook(double g, double v, double brakeDist, double cornerDist)
        {
            double end = Math.Min(brakeDist + 40, cornerDist) - g;
            if (end > 1e8) end = Math.Max(200, v * 5) - g;
            return Clamp(end, Math.Min(40, Math.Max(10, cornerDist - g)), 600);
        }

        // Signed Menger curvature through three points
        static double Curv3(double ax, double ay, double bx, double by, double cx, double cy)
        {
            double abx = bx - ax, aby = by - ay, bcx = cx - bx, bcy = cy - by, cax = ax - cx, cay = ay - cy;
            double den = Math.Sqrt((abx * abx + aby * aby) * (bcx * bcx + bcy * bcy) * (cax * cax + cay * cay));
            return den < 1e-9 ? 0 : 2 * (abx * bcy - aby * bcx) / den;
        }

        // The race craft for this think. Returns false when nobody is within reach: the caller then drives as before.
        // Sets speedCap (cars ahead / backing out), pathCap (own lane's curvature), offsetTarget and laneRate.
        bool Racecraft(double dt, double v, bool straight, bool noPass, bool blueFlag, IList<AiDriver> others, FormulaCar player, int playerIdx, double playerLateral, double simTime)
        {
            int N = track.N; seen = 0; bounds = 0; pathCap = double.MaxValue; laneRate = 2.2; held = false; prepPass = false; ersAttack = ersPush = false;
            bool racing = pit == null;
            if (others != null) for (int i = 0; i < others.Count; i++) { var o = others[i]; if (o != this && o.idx >= 0) SeeCar(o.idx, o.lateral, o.car, o.offset, o.offsetTarget, o, false, racing && !o.InPit); }
            if (player != null && playerIdx >= 0) { double dp = playerLateral - line.off[playerIdx]; SeeCar(playerIdx, playerLateral, player, dp, dp, player, true, racing); }
            if (seen == 0) { mode = 0; atkRef = null; heldT = 0; diveOn = false; return false; }

            // ---- the road ahead: braking zone and the next corner (side of its inside) ----
            // (precomputed per track point by FitGrip: points to the next braking zone and the next bend, the bend's side)
            double brakeDist = 1e9, cornerDist = 1e9; int cornerSign = 0;
            {
                double scan = Math.Max(200, v * 5);
                double dC = nextCorner[idx] * track.ds, dB = nextBrake[idx] * track.ds;
                if (dC <= scan) { cornerDist = dC; cornerSign = cornerSgn[idx]; }
                if (dB <= Math.Min(scan, dC)) brakeDist = dB;
            }
            bool inCorner = Math.Abs(line.curv[idx]) > 0.006, inBend = Math.Abs(line.curv[idx]) > 0.004;
            if (inCorner) defDone = false;   // a new straight, a new defensive move
            int ip = (idx + (int)Math.Round(tune.preview * v / track.ds)) % N;
            double vFree = SpeedAt(ip) * lapPace;
            // the next braking zone of his own profile: where it ends (the apex side), whether it is one of the lap's best
            // places to pass (the biggest braking zones after a run long enough to build a tow), its inside; and the time
            // to the braking point
            int zE = zoneEnd[idx], zSide = zoneSgn[idx];
            double zDist = ((zE - idx + N) % N) * track.ds, tBrake = brakeDist < 1e8 ? brakeDist / Math.Max(10, v) : 1e9;
            bool passZone = PassZone(idx);
            // his own braking point for it (0 once braking), and the braking length left; braking later than he does
            // gains about that x (1 - 1 / diveK) (plus what he closes before it)
            int zSt = zoneStart[zE]; double zsDist = ((zSt - idx + N) % N) * track.ds;
            double ownBrake = zsDist < zDist ? zsDist : 0;
            double brakeLen = zDist - ownBrake;
            // the corner before the straight into a place to pass (braking for it or turning in): the exit set-up
            bool setupCorner = line.phase[idx] <= 0 && (line.phase[idx] < 0 ? passQ[zE] : passQ[idx]) >= 0.5 * passQMax;
            double diveReach = CarLen * 0.6 + brakeLen * (1 - 1 / diveK);
            int atkC = -1;
            if (mode == 1) { for (int c = 0; c < seen; c++) if (sRef[c] == atkRef) atkC = c; if (atkC < 0 || ((noPass || blueFlag) && sV[atkC] > 8)) { mode = 0; atkRef = null; atkC = -1; attackEnds[5]++; } }
            if (mode != 1) diveOn = false;

            // in his tow on a straight, lining up a pass (up to a second before the braking point: no running into the
            // back of a car that brakes early)
            prepPass = heldT > 0.25 + 0.6 * (1 - who.aggression) && line.phase[idx] > 0 && !inBend && brakeDist > v * 1.0;
            // ---- every car: lane, overlap, safe following ----
            double tr = 0.10 + 0.15 * (1 - who.aggression);   // reaction margin: the bold sit closer
            int capCar = -1;
            for (int c = 0; c < seen; c++)
            {
                double gap = sGap[c], side = sLat[c] - lateral, vo = sV[c], w = v - vo;
                // beside, or about to be within 0.7 s: must be left a car's width all along the path
                double front = CarLen + 0.5 * Math.Max(0, w), back = -(CarLen + 0.5 * Math.Max(0, -w));
                if (sRef[c] == passedCar && simTime - passedCarT < 4) back = Math.Min(back, -(5.5 + 0.06 * v));  // just passed him: no cutting back across his nose
                // (overlapping already, a car squeezed closer than 1.5 m still has his side: room for him, not a turn into him)
                bool along = (Math.Abs(side) >= 1.5 || (Math.Abs(side) >= 0.6 && Math.Abs(gap) < CarLen)) && gap < front && gap > back;
                if (!along && Math.Abs(side) >= 1.5)
                    for (double tau = 0.35; tau <= 0.71 && !along; tau += 0.35) { double g2 = gap - w * tau; if (g2 < CarLen && g2 > -CarLen) along = true; }
                if (along && bounds < MaxBounds) { bCar[bounds] = c; bSide[bounds] = side > 0 ? 1 : -1; bounds++; }
                if (gap <= 0) continue;
                if (noPass)
                {
                    // neutralised (VSC / safety car): the queue's plain gap keeping, as before (string-stable, the race
                    // control closes the queue up; no racing)
                    if (gap <= 1.5 || Math.Abs(side) > 2.2) continue;
                    double safeQ = 6.5 + (0.16 - 0.07 * who.aggression) * v + Math.Max(0, w) * 0.6;
                    if (gap < safeQ) { double cq = vo - (safeQ - gap) * 0.6; if (cq < speedCap) { speedCap = cq; capCar = c; } }
                    continue;
                }
                // ahead: in my lane when we meet (or now)? (diving up his inside: when he would be in it within 0.6 s; further
                // on the room beside him is the side-by-side rule's, below)
                double tauM = Clamp((gap - 5) / Math.Max(0.5, w), 0, diveOn && c == atkC ? 0.6 : 1.5);
                int jo = (sIdx[c] + (int)Math.Round(vo * tauM / track.ds)) % N;
                double roomJ = track.HalfWidth(jo) - 1.0, lme = Clamp(line.off[jo] + OffPlan(tauM), -roomJ, roomJ);
                bool inLane = Math.Abs(side) < 2.1 || Math.Abs(LatOf(c, jo, tauM) - lme) < 2.1;
                // pulling out to pass him: out of his lane before the nose reaches his gearbox (at the lane rate, with a
                // margin) and clear of him further on: no braking behind him for it
                if (inLane && mode == 1 && c == atkC && Math.Abs(LatOf(c, jo, tauM) - lme) >= 2.3 && (offsetTarget - offset) * atkSide > 0.3
                    && gap - CarLen - 1.0 > Math.Max(0, w) * (2.1 - Math.Abs(side)) / 2.5) inLane = false;
                if (!inLane || (Math.Abs(side) >= 1.9 && gap < 6)) continue;
                // safe-following speed (Gipps): stop behind him even if he brakes as hard as he can, after my reaction
                // (a car braking harder than that already: assume he keeps it up)
                double s0 = 5.3 + 0.02 * v, voE = vo, trC = heldT > 0.25 + (1 - who.aggression) ? tr * 0.7 : tr;   // ready to pass: right on his gearbox
                // lining up a pass on a straight (no braking for 1 s): close right up in his tow to slingshot out of it
                if (prepPass && !noPass && !blueFlag) { trC = Math.Min(trC, 0.04 + 0.04 * (1 - who.aggression)); s0 = CarLen + 1.5; }
                // pressure: held up for a while, on his gearbox round the slow corners too (in the fast ones his dirty
                // air keeps him back anyway); not under braking (his everyday margin there: a queue braking into a
                // hairpin concertinas), and still a safe following speed. Not round the corner before the straight to
                // pass on: there his everyday margin, his own line and exit (the exit set-up: a run out of it into the
                // tow, rather than stuck behind him through it)
                else if (!noPass && !blueFlag && heldT > 0.5 + 1.0 * (1 - who.aggression) && line.phase[idx] == 0 && v < 50 && sAx[c] > -4 && !setupCorner)
                { trC = Math.Min(trC, 0.05 + 0.05 * (1 - who.aggression)); s0 = Math.Min(s0, CarLen + 1.5 + 0.01 * v); }
                double aL = Math.Max(ACap(vo), -sAx[c] * 1.05), aF = ACap(v), roomS = gap - s0 + voE * voE / (2 * aL);
                double vs = roomS <= 0 ? 0 : aF * (-trC + Math.Sqrt(trC * trC + 2 * roomS / aF));
                if (vs < speedCap) { speedCap = vs; capCar = c; }
            }
            // held up: the car in my lane keeps me below my own pace (or I am closing on him fast)
            if (capCar >= 0 && (speedCap < vFree - 0.5 || (sGap[capCar] < 30 && v - sV[capCar] > 0.5))) heldT += dt;
            else heldT = Math.Max(0, heldT - 0.5 * dt);

            // ---- overtaking ----
            if (mode == 1)
            {
                atkT += dt; double g = sGap[atkC], w = v - sV[atkC];
                if (g < -(CarLen + 1.0))
                {
                    // past him: done. He will want it back.
                    overtakesMade++; attackEnds[0]++; passedCar = atkRef; passedCarT = simTime;
                    if (atkRef is AiDriver od) { od.passedBy = this; od.passedByT = simTime; }
                    mode = 0; atkRef = null; heldT = 0; diveOn = false;
                }
                else
                {
                    // a late-braking dive: on his inside, into the braking zone, close enough that braking later than he
                    // does (at the car's limit: DiveSpeed) puts the nose alongside before the turn-in
                    if (diveOn && zE != diveEnd) diveOn = false;   // that braking zone is behind
                    bool inside = zSide != 0 && atkSide == zSide && (lateral - sLat[atkC]) * atkSide > 1.6;
                    if (diveOn && ownBrake > v * 1.5) diveOn = false;   // not his braking zone yet after all
                    if (!diveOn && inside && ownBrake < Math.Max(25, v * 0.45) && brakeLen > 30 && g < diveReach + Math.Max(0, w) * Math.Min(ownBrake / Math.Max(10, v), 1) && g > -CarLen)
                    { diveOn = true; diveEnd = zE; divesMade++; }
                    if (diveOn && g > CarLen * 0.9 && zDist < Math.Max(30, 0.4 * v))
                    {
                        // no overlap by the end of the braking: no dive up the inside, back in behind him
                        diveOn = false; mode = 0; atkRef = null; heldT = 0.5 * heldT; atkCool = simTime + 2; attackEnds[3]++;
                    }
                    else
                    {
                        // the door: open as far as he goes until my nose is alongside him (at the closing speed now), not
                        // where his line swings across later on (by then I am beside him, and the side-by-side rule holds)
                        double look = AttackLook(g, v, brakeDist, cornerDist);
                        look = Math.Max(20, Math.Min(look, sV[atkC] * (Math.Max(0, g - CarLen * 0.5) / Math.Max(0.5, w) + 0.5)));
                        double slack = SideRoom(atkC, atkSide, v, look);
                        if (slack < -0.3 && g > 2.0 && atkT > 0.4 && !diveOn)
                        {
                            // the door closed before I had half a car alongside: switch side once (switchback), else tuck in
                            if (atkSwitch == 0 && !inBend && !diveOn && SideRoom(atkC, -atkSide, v, look) > 0.2) { atkSide = -atkSide; atkSwitch = 1; atkT = 0; }
                            else { mode = 0; atkRef = null; heldT = 0; atkCool = simTime + 3; diveOn = false; attackEnds[1]++; }
                        }
                        // not getting there (pulled out of the tow and not gaining): back in line for the next chance
                        else if (!diveOn && ((atkT > 2.5 && g > CarLen + 1 && w < 0.2 && brakeDist > v * 1.6) || (atkT > 7 && g > 3))) { mode = 0; atkRef = null; heldT = 0.5 * heldT; atkCool = simTime + 3; attackEnds[2]++; }
                    }
                }
            }
            if (mode != 1 && capCar >= 0 && ((!noPass && !blueFlag) || sV[capCar] < 8) && sGap[capCar] < 45)
            {
                int c = capCar; double g = sGap[c], vo = sV[c], w = v - vo;
                double need = 0.25 + 1.0 * (1 - who.aggression);
                if (sRef[c] == passedBy && simTime - passedByT < 8) need = 0;              // fight back at once
                bool slow = vo < 0.45 * SpeedAt(sIdx[c]) && vo < 20;                         // stopped or crawling where cars are fast: around him
                // on a straight: out of his tow late, with the closing speed it built: when the nose gets alongside before
                // the braking point, or when the tow is about to put him on the gearbox. Only into a place to pass, unless
                // the pass is done before the braking point.
                // (out of the tow the drag takes the extra speed back within a second or so: from close behind only)
                double tRun = Math.Min(tBrake - 0.4, 1.2);
                bool canAlong = w > 0.3 && g - 1.5 < w * tRun;
                bool canDone = w > 0.3 && g + CarLen + 2 < w * Math.Min(tBrake - 0.3, 4.0);
                bool boxed = g < CarLen + 2.5 + 0.8 * Math.Max(0, w) && w > -0.5;
                bool onStraight = simTime > atkCool && line.phase[idx] > 0 && !inBend && brakeDist > Math.Max(40, v * 0.7) && g < 30
                    && (canAlong || boxed) && (passZone || canDone || brakeDist > 1e8);
                // up the inside into the braking zone of a place to pass (or of any corner when right on him), close enough
                // for the late braking to get alongside
                // (still closing in his tow: stays in it until about to be on his gearbox or the last moment to change
                // lanes before the braking; the tow is worth more than an early look)
                bool diveTime = w > 1 ? brakeDist < v * 0.8 || g < CarLen + 3 + 0.6 * w : brakeDist < v * 1.3;
                bool dive = simTime > atkCool - 1 && !inBend && brakeDist < v * 1.3 && diveTime && cornerDist > 25 && w > -0.5 && zSide != 0
                    && ownBrake < v * 1.5 && brakeLen > 30 && (passZone || g < CarLen + 3) && g < diveReach + Math.Max(0, w) * Math.Min(tBrake, 1.0);
                if ((slow && (!inCorner || v < 25)) || (heldT >= need && !noPass && !blueFlag && (onStraight || dive)))
                {
                    double look = AttackLook(g, v, brakeDist, cornerDist);   // from him to the turn-in: the room to get alongside
                    int ins = brakeDist < 1e8 ? zSide : cornerSign;           // the inside of the corner the move is for
                    double best = -1e9; int bs = 0;
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        // round the outside only when the pass is done before the braking point (not into the bend)
                        if (!slow && ins != 0 && sgn != ins && !canDone && brakeDist < 1e8) continue;
                        double sc = SideRoom(c, sgn, v, look);
                        if (sc < 0) continue;
                        sc = Math.Min(sc, 2) + (sgn == ins ? (dive ? 2.0 : 0.8) : 0) + (sgn == Math.Sign(lateral - sLat[c]) ? 0.4 : 0) + (sgn == lastSide && simTime - lastSideT < 8 ? 1.5 : 0);   // no weaving: the same side again
                        if (sc > best) { best = sc; bs = sgn; }
                    }
                    if (bs != 0) { attacksMade++; dbgWhy = slow ? 4 : onStraight ? (canAlong ? 1 : 2) : 3; dbgBrake = brakeDist; mode = 1; atkRef = sRef[c]; atkSide = bs; atkSwitch = 0; atkT = 0; atkC = c; lastSide = bs; lastSideT = simTime; }
                }
            }
            // energy for the attack: Overtake Mode from the exit onto the straight into a place to pass, within 1 s of
            // the car ahead (AiDriver.ErsButtons); the battery boost for the last seconds of it, close behind or alongside
            if (!noPass && !blueFlag)
            {
                int tc = mode == 1 ? atkC : capCar;
                if (tc >= 0 && sGap[tc] > -CarLen)
                {
                    double tg = sGap[tc] / Math.Max(10, v);
                    // from the exit when he still has to close into the tow (over 0.3 s back); in the tow already, timed so
                    // the allowance lasts to the braking point (about 120 kW over the car's own map)
                    // (the lap's allowance is kept for the straight into one of its best places to pass)
                    ersAttack = line.phase[idx] > 0 && (passQ[idx] >= OtZoneQ * passQMax || (mode == 1 && sGap[tc] < 15)) && tg < 1.0
                        && (tg > 0.3 || mode == 1 || tBrake < car.OtLeft / 120e3 + 1.0);
                    // the battery is the lap's energy too: only for the move itself (pulled out, within 20 m) or the last
                    // 2.5 s of the tow into a place to pass, with a reserve kept (AiDriver.ErsButtons: above 35 %)
                    ersPush = (mode == 1 && sGap[tc] < 20) || (prepPass && passZone && tBrake < 2.5 && tg < 0.3);
                }
            }

            // ---- defending: one move per straight, to cover the inside before the braking zone ----
            if (mode == 2 && (inCorner || behindGap > 50 || noPass || blueFlag)) mode = 0;
            bool busy = capCar >= 0 && sGap[capCar] < 30 && heldT > 0.3;   // racing the car ahead: no looking back
            if (mode == 0 && !busy && !defDone && !noPass && !blueFlag && cornerSign != 0 && line.phase[idx] > 0 && !inCorner
                && brakeDist > v * 0.8 && brakeDist < v * 4.0)
            {
                int att = -1;
                for (int c = 0; c < seen; c++)
                {
                    double g = sGap[c];
                    if (g > -CarLen - 1.5 || g < -Math.Max(12, 0.5 * v)) continue;           // overlapping already: too late to move; far: no threat yet
                    if (!sPl[c] && sRef[c] is AiDriver od && od.progress > progress + 0.5) continue; // lapping me: let him by
                    if (sV[c] - v < 0.5 && g < -12) continue;                                 // not closing: no threat
                    if (att < 0 || g > sGap[att]) att = c;
                }
                if (att >= 0)
                {
                    defDone = true;   // decided for this straight, move or not
                    double room = track.HalfWidth(idx) - 1.0, cover = cornerSign * (room - 2.0);
                    bool committed = (sLat[att] - lateral) * cornerSign > 1.2;                // he is already on the inside: no late block
                    bool needMove = (lateral - cover) * cornerSign < -0.5;
                    if (!committed && needMove && craftRng.NextDouble() < 0.35 + 0.6 * who.aggression)
                    { mode = 2; defOff = cover - line.off[idx]; defMoves++; }
                }
            }

            // ---- lateral: where to be ----
            if (mode == 1 && sGap[atkC] < -CarLen * 0.6)
            {
                // clearly ahead of him (his nose behind my sidepod): my own line again (the side-by-side rule below
                // keeps a car's width to him while he is alongside); level, I keep my side of him
                offsetTarget *= Math.Max(0, 1 - dt * 1.2);
                WindowClamp(v);
            }
            else if (mode == 1)
            {
                double dO = sDOff[atkC] + Clamp(sDOffT[atkC] - sDOff[atkC], -1.0, 1.0);
                offsetTarget = dO + atkSide * 2.6; laneRate = inBend ? 1.2 : 3.0;   // no sudden moves in a bend
                WindowClamp(v, 1.0, 35, 4.5);
            }
            else if (mode == 2) { offsetTarget = defOff; laneRate = inBend ? 1.2 : 3.0; WindowClamp(v, 1.0, 35, 4.5); }
            else
            {
                // free: back to the line as before (the bounds below keep the room for the cars beside)
                offsetTarget *= Math.Max(0, 1 - dt * 0.8);
                WindowClamp(v);
            }
            {
                double roomN = track.HalfWidth(idx) - 1.0;
                offsetTarget = Clamp(offsetTarget, -roomN - line.off[idx], roomN - line.off[idx]);
            }
            if (bounds > 0)
            {
                // the room the cars beside leave, at the aim point; and who backs out when there is none on my path
                double Ld = tune.look0 + tune.lookV * v; int ia = (idx + Math.Max(1, (int)Math.Round(Ld / track.ds))) % N;
                double tA = Ld / Math.Max(5, v), roomA = track.HalfWidth(ia) - 1.0;
                BoundsAt(ia, tA, roomA, out double lo, out double hi);
                if (lo <= hi)
                {
                    double t0 = offsetTarget;
                    offsetTarget = Clamp(line.off[ia] + offsetTarget, lo, hi) - line.off[ia];
                    if (Math.Abs(offsetTarget - t0) > 0.05) laneRate = 4.0;
                }
                int st = Math.Max(2, (int)Math.Round(6 / track.ds)), n = (int)(Math.Max(25, v * 1.2) / (st * track.ds)) + 1;
                for (int b = 0; b < bounds; b++)
                {
                    int c = bCar[b]; double vo = sV[c], w = v - vo;
                    for (int k = 0; k <= n; k++)
                    {
                        int j = (idx + k * st) % N; double tau = k * st * track.ds / Math.Max(8, v), room = track.HalfWidth(j) - 1.0;
                        double Lc = LatOf(c, j, tau);
                        bool fits = bSide[b] > 0 ? Lc - Sep >= -room : Lc + Sep <= room;
                        if (fits) continue;
                        // no room for both: the one behind gives way
                        double gP = sGap[c] - w * tau;    // + = he is ahead
                        bool iLead = sPl[c] ? gP < -0.5 : gP < -0.5 || (gP <= 0.5 && sRef[c] is AiDriver od && string.CompareOrdinal(who.name, od.who.name) < 0);
                        // an attack with half a car alongside (three quarters diving up the inside: my nose past his rear
                        // wheels) keeps its line, and an AI attacked like that gives the room (one of the two always yields)
                        double ovA = CarLen * (diveOn ? 0.75 : 0.5);
                        if (mode == 1 && sRef[c] == atkRef && gP < ovA && sGap[c] < ovA) iLead = true;
                        else if (sRef[c] is AiDriver oa && oa.mode == 1 && oa.atkRef == this)
                        {
                            double ovB = CarLen * (oa.diveOn ? 0.75 : 0.5);
                            if (gP > -ovB && sGap[c] > -ovB) iLead = false;
                        }
                        if (!iLead)
                        {
                            speedCap = Math.Min(speedCap, Math.Max(0, vo - Clamp(1.0 + 0.6 * (gP + 6), 1, 6)));
                            if (mode == 1 && sRef[c] == atkRef && gP > 0) { mode = 0; atkRef = null; heldT = 0; atkCool = simTime + 3; diveOn = false; attackEnds[4]++; }
                        }
                        break;
                    }
                }
            }
            if (mode != 0 || bounds > 0 || capCar >= 0) laneRate = Math.Max(laneRate, 2.2);
            offset += Clamp(offsetTarget - offset, -laneRate * dt, laneRate * dt);

            // ---- the speed my own lane allows: an off-line path through a bend is tighter or wider than the line ----
            PathCap(v);
            return true;
        }

        // the old clamp: the offset may only use the room the line leaves over the next 1.5 s (40 m at least)
        // (window: s of road ahead; the attack and defence lanes use a shorter one, so an inside lane is held to the
        // turn-in and then wound back, and only a car beside keeps the two cars apart through the bend: BoundsAt)
        void WindowClamp(double v, double window = 1.5, double min = 40, double max = 3.2)
        {
            int N = track.N;
            double roomA = track.width / 2 - 1.0, maxL = max, maxR = max;
            int nA = (int)Math.Round(Math.Max(min, v * window) / track.ds);
            for (int k = 0; k <= nA; k += 2) { int j = (idx + k) % N; if (track.halfW != null) roomA = track.halfW[j] - 1.0; maxL = Math.Min(maxL, roomA - line.off[j]); maxR = Math.Min(maxR, roomA + line.off[j]); }
            offsetTarget = Clamp(offsetTarget, -Math.Max(0, maxR), Math.Max(0, maxL));
        }

        // the grip this driver's own speed profile uses where it is at the cornering limit (median over those points):
        // v^2 k = mu (g + q v^2), q = downforce per mass and v^2
        void FitGrip()
        {
            double q = 0.5 * 1.225 * car.cfg.clA / car.cfg.mass; int N = track.N, n = 0;
            var r = new double[N];   // once per driver (constructor)
            for (int j = 0; j < N; j++)
            {
                if (line.phase[j] != 0 || Math.Abs(line.curv[j]) < 0.008) continue;
                double V = SpeedAt(j); r[n++] = V * V * Math.Abs(line.curv[j]) / (9.81 + q * V * V);
            }
            if (n == 0) muLat = 1.5; else { Array.Sort(r, 0, n); muLat = 0.97 * r[n / 2]; }
            // the road ahead of every point: points to the next braking zone and to the next bend (and its side)
            nextBrake = new int[N]; nextCorner = new int[N]; cornerSgn = new sbyte[N];
            int nb = 1 << 20, nc = 1 << 20; sbyte sc = 0;
            for (int k = 2 * N - 1; k >= 0; k--)
            {
                int j = k % N; nb++; nc++;
                if (line.phase[j] < 0) nb = 0;
                if (Math.Abs(line.curv[j]) > 0.008) { nc = 0; sc = (sbyte)(line.curv[j] > 0 ? 1 : -1); }
                if (k < N) { nextBrake[j] = nb; nextCorner[j] = nc; cornerSgn[j] = sc; }
            }
            // the braking zones of his own profile (as Think's brakingZone): zoneEnd = the first point past the next one;
            // passQ = the speed it sheds (m/s), scaled down when the run before it is under 300 m (no tow to build);
            // zoneSgn = the inside of its corner (the next bend from where the braking starts)
            zoneEnd = new int[N]; zoneStart = new int[N]; passQ = new float[N]; zoneSgn = new sbyte[N];
            var brk = new bool[N]; var qz = new float[N]; var zs = new sbyte[N];
            for (int j = 0; j < N; j++) brk[j] = SpeedAt((j + 1) % N) < SpeedAt(j) - 0.05;
            for (int e = 0; e < N; e++)
            {
                // a lift of under 4 m/s (a kink) is not a braking zone: the zone that counts is the one after it
                int jb = (e - 1 + N) % N; if (!brk[jb] || brk[e]) continue;
                int st = jb, m0 = 0; while (m0++ < N && brk[(st - 1 + N) % N]) st = (st - 1 + N) % N;
                if (SpeedAt(st) - SpeedAt(e) < 4) for (int j = st; ; j = (j + 1) % N) { brk[j] = false; if (j == jb) break; }
            }
            passQMax = 1;
            for (int e = 0; e < N; e++)
            {
                int jb = (e - 1 + N) % N; if (!brk[jb] || brk[e]) continue;   // e: the first point past a braking run
                int st = jb, m0 = 0; while (m0++ < N && brk[(st - 1 + N) % N]) st = (st - 1 + N) % N;
                int rr = (st - 1 + N) % N, nr = 0; while (nr < N && !brk[rr]) { rr = (rr - 1 + N) % N; nr++; }
                double drop = SpeedAt(st) - SpeedAt(e);
                qz[e] = (float)(drop * Clamp(nr * track.ds / 300, 0.25, 1)); zs[e] = cornerSgn[st]; zoneStart[e] = st;
                passQMax = Math.Max(passQMax, qz[e]);
            }
            int ze = -1;
            for (int k = 2 * N - 1; k >= 0; k--)
            {
                int j = k % N, j1 = (j + 1) % N;
                if (brk[j] && !brk[j1]) ze = j1;
                if (k < N) { zoneEnd[j] = ze < 0 ? j : ze; passQ[j] = ze < 0 ? 0 : qz[ze]; zoneSgn[j] = ze < 0 ? (sbyte)0 : zs[ze]; }
            }
        }

        // the next braking zone (from point i) is one of the best places on the lap to pass
        bool PassZone(int i) => passQ[i] >= PassZoneQ * passQMax;

        void PathCap(double v)
        {
            if (bounds == 0 && Math.Abs(offset) < 0.3 && Math.Abs(offsetTarget) < 0.3) return;   // on the line: nothing to do
            int N = track.N, st = Math.Max(2, (int)Math.Round(5 / track.ds));
            double D = Math.Max(80, v * 1.6 + v * v / (2 * 0.7 * ACap(v)));
            int K = Math.Min(MaxPath - 1, (int)(D / (st * track.ds)) + 4);
            double dev = 0;
            // the offset as it will evolve: towards the target, at the lane rate, wound back by the same window clamp
            // as the driver applies at each place (so a lane is not assumed to be held into a bend it will leave)
            double win = mode == 0 ? Math.Max(40, v * 1.5) : Math.Max(35, v * 1.0), cap = mode == 0 ? 3.2 : 4.5;
            int W = Math.Min(MaxPath - 1, (int)(win / (st * track.ds)) + 1), KW = Math.Min(2 * MaxPath - 1, K + W);
            for (int k = 0; k <= KW; k++)
            {
                int j = (idx + k * st) % N; double room = track.HalfWidth(j) - 1.0;
                wL[k] = room - line.off[j]; wR[k] = room + line.off[j];
            }
            // window minima wL[k..k+W] in one backward pass (block suffix / prefix minima, van Herk), in place of an O(K W) loop
            for (int k = KW; k >= 0; k--)
            {
                bool blockEnd = k == KW || (k + 1) % (W + 1) == 0;
                sL[k] = blockEnd ? wL[k] : Math.Min(wL[k], sL[k + 1]); sR[k] = blockEnd ? wR[k] : Math.Min(wR[k], sR[k + 1]);
            }
            for (int k = 0; k <= KW; k++)
            {
                bool blockStart = k % (W + 1) == 0;
                qL[k] = blockStart ? wL[k] : Math.Min(wL[k], qL[k - 1]); qR[k] = blockStart ? wR[k] : Math.Min(wR[k], qR[k - 1]);
            }
            double o = offset, dTau = st * track.ds / Math.Max(8, v);
            for (int k = 0; k <= K; k++)
            {
                int j = (idx + k * st) % N; double tau = k * dTau, room = track.HalfWidth(j) - 1.0;
                int e = Math.Min(KW, k + W);   // min over [k, e]: the suffix of k's block and the prefix of e's block
                double mL = Math.Min(cap, Math.Min(sL[k], qL[e])), mR = Math.Min(cap, Math.Min(sR[k], qR[e]));
                double tgt = Clamp(offsetTarget, -Math.Max(0, mR), Math.Max(0, mL));
                if (k > 0) o += Clamp(tgt - o, -laneRate * dTau, laneRate * dTau);
                double L = Clamp(line.off[j] + o, -room, room);
                if (bounds > 0) L = ApplyBounds(j, L, tau, room);
                pDev[k] = L - line.off[j]; dev = Math.Max(dev, Math.Abs(pDev[k]));
            }
            if (dev < 0.3) return;
            // the car does not drive the kinks of a clamped lane (pure pursuit rounds them): smooth it over ~20 m
            for (int pass = 0; pass < 2; pass++)
            {
                double prev = pDev[0];
                for (int k = 1; k < K; k++) { double cur = pDev[k]; pDev[k] = 0.25 * prev + 0.5 * cur + 0.25 * pDev[k + 1]; prev = cur; }
            }
            for (int k = 0; k <= K; k++)
            {
                int j = (idx + k * st) % N; var p = track.pts[j]; double L = line.off[j] + pDev[k];
                pX[k] = p.x + p.nx * L; pY[k] = p.y + p.ny * L; lX[k] = p.x + p.nx * line.off[j]; lY[k] = p.y + p.ny * line.off[j];
            }
            int ipc = (idx + (int)Math.Round(tune.preview * v / track.ds)) % N;
            double vHere = (diveOn ? DiveSpeed(ipc) : SpeedAt(ipc)) * lapPace;   // a dive: against its later braking
            double q = 0.5 * 1.225 * car.cfg.clA / car.cfg.mass;
            for (int k = 3; k <= K - 3; k++)
            {
                if (Math.Abs(pDev[k]) < 0.3) continue;   // on the line here: its own profile holds
                double kp = Math.Abs(Curv3(pX[k - 3], pY[k - 3], pX[k], pY[k], pX[k + 3], pY[k + 3]));
                double kl = Math.Abs(Curv3(lX[k - 3], lY[k - 3], lX[k], lY[k], lX[k + 3], lY[k + 3]));
                if (kp <= kl * 1.03 || kp < 0.002) continue;
                // the speed the lane's own curvature allows: v^2 k = mu (g + q v^2), with the grip this driver uses on the line
                double den = kp - muLat * q;
                double vAllow = den <= 1e-6 ? 1e9 : Math.Sqrt(muLat * 9.81 / den);
                int j = (idx + k * st) % N; double Vj = diveOn ? DiveSpeed(j) : SpeedAt(j);
                if (vAllow >= Vj) continue;
                // brake for it as the profile brakes for its own speed there (same deceleration, so the same distance
                // lost: v^2 = V^2 - (Vj^2 - vAllow^2)), never above what a gentle 0.7 g-share stop would allow
                double vNow = Math.Max(Math.Sqrt(Math.Max(0, vHere * vHere - (Vj * Vj - vAllow * vAllow))), Math.Sqrt(vAllow * vAllow + 2 * 0.7 * ACap(vAllow) * k * st * track.ds));
                if (vNow < pathCap) pathCap = vNow;
            }
        }
    }
}
