// RaceControl.cs — race craft. Pure C# (no UnityEngine) and deterministic for a seed, so the harness
// (Tools/PhysicsCheck, "race sc" / "race strat") runs exactly what the game runs.
//  * TyreSets: the five tyre sets (the game's Weather numbering: 0-2 slicks, 3 intermediate, 4 full wet).
//  * TyreStrategy: a rival's race plan. One or two stops from the race length and the tyre wear (stints end
//    before the cliff: a Soft reaches the cliff, wear ~0.78, around lap 18-21 at Spa in this model), a pit window
//    with some spread per driver, the 2-compound rule in dry races of 10+ laps, inters or wets in the wet, and a
//    reaction when the weather changes. Races of 1-5 laps get no stops (only damage or a puncture forces one).
//    Live wear is measured lap by lap and brings a stop forward when the tyre would go past the cliff.
//  * RaceControl: flags. A stopped car or a heavy crash (a reset with high damage) shows a yellow flag; a
//    repeated, heavy or blocking incident brings out, by chance, a Virtual Safety Car (every car about 40 %
//    slower than its normal speed at that point of the lap) or the Safety Car (a pace car on the racing line,
//    the field queues behind it in order without overtaking, it comes in after 2-3 laps, restart at the line).
//    Blue flags for cars about to be lapped, track-limits strikes and the VSC delta for the player.
using System;
using System.Collections.Generic;

namespace FormulaSim
{
    public static class TyreSets
    {
        public const int Soft = 0, Medium = 1, Hard = 2, Inter = 3, Wet = 4, Count = 5;
        public static readonly string[] Letter = { "S", "M", "H", "I", "W" };
        public static bool Slick(int s) => s < 3;
        // the physics compound the set runs on (intermediates and wets use the Soft's temperature and wear model)
        public static Compound Physics(int s) => s < 3 ? (Compound)s : Compound.Soft;
        public static double Life(int s) => s < 3 ? CompoundTable.Defs[s].life : 1.0;
        public static double Grip(int s) => s < 3 ? CompoundTable.Defs[s].grip : 1.0;
        // weather 0 dry, 1 damp, 2 wet: is the set a sensible one for it?
        public static bool Suits(int s, int wet) => wet == 0 ? s < 3 : wet == 2 ? s >= 3 : s <= 1 || s == Inter;
    }

    public class TyreStrategy
    {
        public const double CliffWear = 0.78, MaxWear = 0.86;
        public const int ShortRace = 5, RealStrategy = 10;
        // Soft wear per lap (the worse axle) per circuit, from the race test (Tools/PhysicsCheck "race": wear after
        // 5 laps / 5). Only the first guess: the live wear of the car corrects it from lap 3 on.
        static double SoftRate(string id)
        {
            switch (id)
            {
                case "monaco": return 0.0246; case "spa": return 0.0378; case "monza": return 0.0264;
                case "silverstone": return 0.0348; case "suzuka": return 0.0384; case "austin": return 0.0370;
                case "hungaroring": return 0.0292; case "barcelona": return 0.0296; case "redbullring": return 0.0242;
                case "zandvoort": return 0.0286; default: return 0.030;
            }
        }

        public int totalLaps, wet;
        public readonly List<int> stopAfter = new List<int>();   // box at the end of these laps
        public readonly List<int> sets = new List<int>();        // sets[0] = start set, sets[k] = fitted at stop k
        public int next;                                          // index of the next stop in stopAfter
        public int current, stintStart = 1;
        public double softRate, lapRef = 90, pitLoss = AiDriver.PitLoss;
        readonly bool[] used = new bool[TyreSets.Count];
        readonly Random rng;
        int pendingWeatherLap = -1, pendingSet = -1;
        int wetPref = -1;                                         // the driver's choice in the damp / wet (set at the first plan)

        public TyreStrategy(int seed) { rng = new Random(seed * 7349 + 101); }
        public TyreStrategy(int seed, string circuit) : this(seed + 977 * (circuit != null ? circuit.Length * 31 + circuit[0] : 0)) { }
        public int NextStop => next < stopAfter.Count ? stopAfter[next] : -1;
        public int StopsPlanned => stopAfter.Count;

        public string Describe()
        {
            var sb = new System.Text.StringBuilder(TyreSets.Letter[sets.Count > 0 ? sets[0] : current]);
            for (int i = 0; i < stopAfter.Count; i++) sb.Append($" -{stopAfter[i]}- ").Append(TyreSets.Letter[sets[i + 1]]);
            return sb.ToString();
        }

        // Plan the race: laps, weather (0 dry, 1 damp, 2 wet), circuit id, the reference lap (s). Returns the start set.
        public int Plan(int laps, int wet, string circuit, double lapRef, double softRatePerLap = 0)
        {
            totalLaps = Math.Max(1, laps); this.wet = wet; this.lapRef = lapRef > 0 ? lapRef : 90;
            softRate = softRatePerLap > 0 ? softRatePerLap : SoftRate(circuit);
            if (wetPref < 0) wetPref = rng.NextDouble() < (wet == 2 ? 0.2 : 0.5) ? 1 : 0; // wet: 20 % gamble on inters; damp: half on inters
            Array.Clear(used, 0, used.Length); stopAfter.Clear(); sets.Clear(); next = 0; pendingWeatherLap = -1;
            PlanFrom(1, wet, totalLaps >= RealStrategy && wet == 0);
            current = sets[0]; used[current] = true; stintStart = 1; next = 0;
            return current;
        }

        int[] Allowed(int w)
        {
            if (w == 0) return new[] { 0, 1, 2 };
            if (w == 1) return wetPref == 1 ? new[] { TyreSets.Inter } : new[] { 0, 1 };
            return wetPref == 1 ? new[] { TyreSets.Inter } : new[] { TyreSets.Wet };
        }

        // laps a set can do from new before MaxWear
        double Rate(int s) => softRate / TyreSets.Life(s);
        double StintCost(int s, int n)
        {
            double r = Rate(s); if (n * r > MaxWear) return double.NaN;
            double c = 0;
            for (int a = 0; a < n; a++) { double w = (a + 1) * r; c += (1 - TyreSets.Grip(s)) + 0.05 * w + 0.12 * w * w * w * w; }
            return c * lapRef * 0.6; // lap time ~ grip^-0.6 for a downforce car
        }

        // Plan the laps from fromLap to the finish on new tyres now. needSecond: the 2-compound rule still applies
        // (the plan together with the sets already used must have two different slicks).
        void PlanFrom(int fromLap, int w, bool needSecond)
        {
            int L = totalLaps - fromLap + 1; var allow = Allowed(w);
            stopAfter.RemoveRange(next, stopAfter.Count - next);
            if (sets.Count > next + 1) sets.RemoveRange(next + 1, sets.Count - next - 1);
            int baseIdx = next;
            if (sets.Count <= baseIdx) sets.Add(0);
            if (totalLaps <= ShortRace || L <= 1)
            {
                // a sprint: no stops, and the grippiest set that lasts (the Soft in the dry; the sprint is too short
                // for a harder compound to come in, and cold Mediums on lap 1 only cost grip)
                int s0 = allow[0];
                sets[baseIdx] = s0; return;
            }
            bool Ok(int a, int b = -1, int c = -1)
            {
                if (!needSecond) return true;
                var m = (bool[])used.Clone(); m[a] = true; if (b >= 0) m[b] = true; if (c >= 0) m[c] = true;
                return (m[0] ? 1 : 0) + (m[1] ? 1 : 0) + (m[2] ? 1 : 0) >= 2;
            }
            var cost = new double[TyreSets.Count][];
            foreach (int s in allow) { cost[s] = new double[L + 1]; for (int n = 1; n <= L; n++) cost[s][n] = StintCost(s, n); }
            double bestScore = double.MaxValue; int[] bestSets = null, bestLen = null;
            void Try(double c, int[] ss, int[] len)
            {
                if (double.IsNaN(c)) return;
                double score = c + rng.NextDouble() * 6.0;             // each driver weighs it a little differently
                if (score < bestScore) { bestScore = score; bestSets = ss; bestLen = len; }
            }
            int minStint = Math.Max(3, L / 5);
            // no stop (races under 10 laps, or when the rule is already met)
            foreach (int s in allow) if (Ok(s)) Try(cost[s][L], new[] { s }, new[] { L });
            // one stop
            foreach (int s1 in allow) foreach (int s2 in allow)
                {
                    if (!Ok(s1, s2)) continue;
                    double bc = double.NaN; int ba = -1;
                    for (int a = minStint; a <= L - minStint; a++) { double c = cost[s1][a] + cost[s2][L - a]; if (!double.IsNaN(c) && (double.IsNaN(bc) || c < bc)) { bc = c; ba = a; } }
                    if (ba > 0) Try(bc + pitLoss, new[] { s1, s2 }, new[] { ba, L - ba });
                }
            // two stops
            if (L >= 3 * minStint)
                foreach (int s1 in allow) foreach (int s2 in allow) foreach (int s3 in allow)
                        {
                            if (!Ok(s1, s2, s3)) continue;
                            double bc = double.NaN; int ba = -1, bb = -1;
                            for (int a = minStint; a <= L - 2 * minStint; a++)
                                for (int b = minStint; a + b <= L - minStint; b++)
                                {
                                    double c = cost[s1][a] + cost[s2][b] + cost[s3][L - a - b];
                                    if (!double.IsNaN(c) && (double.IsNaN(bc) || c < bc)) { bc = c; ba = a; bb = b; }
                                }
                            if (ba > 0) Try(bc + 2 * pitLoss, new[] { s1, s2, s3 }, new[] { ba, bb, L - ba - bb });
                        }
            if (bestSets == null)
            {
                // nothing fits in two stops (very long race or a very hard-wearing circuit): equal stints on the longest-lasting set
                int s = allow[allow.Length - 1]; foreach (int x in allow) if (TyreSets.Life(x) > TyreSets.Life(s)) s = x;
                int n = Math.Max(1, (int)Math.Floor(MaxWear / Rate(s))), k = (L + n - 1) / n;
                bestSets = new int[k]; bestLen = new int[k];
                for (int i = 0; i < k; i++) { bestSets[i] = s; bestLen[i] = L / k + (i < L % k ? 1 : 0); }
                if (needSecond && k > 1 && !Ok(s)) bestSets[k - 1] = s == 2 ? 1 : 2;
            }
            // the pit window: each stop moves up to 2 laps either way, as long as every stint still lasts
            int lap = fromLap - 1;
            sets[baseIdx] = bestSets[0];
            for (int i = 0; i < bestLen.Length - 1; i++)
            {
                lap += bestLen[i];
                int shift = rng.Next(-2, 3), prev = i == 0 ? fromLap - 1 : stopAfter[stopAfter.Count - 1];
                int cand = lap + shift, nextEnd = i + 2 < bestLen.Length ? lap + bestLen[i + 1] : totalLaps;
                bool fits = cand - prev >= 2 && nextEnd - cand >= 2 && (cand - prev) * Rate(bestSets[i]) <= MaxWear && (nextEnd - cand) * Rate(bestSets[i + 1]) <= MaxWear;
                stopAfter.Add(fits ? cand : lap); sets.Add(bestSets[i + 1]);
                lap = stopAfter[stopAfter.Count - 1];
            }
        }

        // At each crossing of the line (after any stop there): box at the end of this lap? wear = the worse axle now.
        public bool BoxThisLap(int lap, double wear)
        {
            int left = totalLaps - lap;              // laps after this one
            if (left < 0) return false;
            int done = lap - stintStart;             // laps completed on this set
            if (done >= 2 && wear > 0.02)
            {
                double r = wear / done;              // measured, this car on this set
                softRate = 0.5 * softRate + 0.5 * r * TyreSets.Life(current);
            }
            if (pendingWeatherLap >= 0 && lap >= pendingWeatherLap && left >= 1) return true;
            if (left < 1) return false;              // the last lap: never
            if (NextStop == lap) return true;
            if (NextStop >= 0 && NextStop < lap) { next++; return BoxThisLap(lap, wear); } // missed (e.g. a stop forced earlier)
            // the tyre would pass the cliff before the stop (or the flag): come in now
            if (done >= 2 && left >= 3) { double r = wear / done; if (wear + 1.5 * r > MaxWear + 0.04) return true; }
            return false;
        }

        // Under a Safety Car a stop costs about half: take it if one is due within 8 laps, or (now and then) an extra one
        // on old tyres when the race is long enough.
        public bool CheapStop(int lap, double wear)
        {
            int left = totalLaps - lap; if (left < 3) return false;
            if (NextStop >= 0 && NextStop - lap <= 8) return true;
            if (NextStop < 0 && left >= 6 && wear > 0.3 && rng.NextDouble() < 0.4) return true;
            return false;
        }

        // The weather changed: a car on the wrong tyres boxes at the end of this lap or the next.
        public void WeatherChanged(int newWet, int lap)
        {
            if (newWet == wet) return;
            wet = newWet;
            if (TyreSets.Suits(current, newWet)) return;
            var allow = Allowed(newWet);
            pendingSet = allow[0]; pendingWeatherLap = lap + rng.Next(0, 2);
        }

        // A stop at the line: lap = the lap starting now. reason 0 plan, 1 damage or puncture, 2 Safety Car, 3 weather.
        // Returns the set fitted.
        public int OnStop(int lap, int reason)
        {
            int prevLap = lap - 1;
            if (pendingWeatherLap >= 0)
            {
                pendingWeatherLap = -1;
                sets[next] = current; // keep the history in step
                stopAfter.Insert(next, prevLap); sets.Insert(next + 1, pendingSet); next++;
                current = pendingSet;
                PlanFrom(lap, wet, false); current = sets[next];
            }
            else if (NextStop >= 0 && (reason == 0 || NextStop - prevLap <= Math.Max(8, (NextStop - stintStart) / 2)))
            {
                // the planned stop (or one due soon, taken early): its set, then plan the rest again from here
                current = sets[next + 1]; next++;
                stopAfter[next - 1] = prevLap;
                if (next < stopAfter.Count) { int n = next; PlanFrom(lap, wet, totalLaps >= RealStrategy && wet == 0 && !Two()); next = n; current = sets[next]; }
            }
            else
            {
                // an unplanned stop: fresh tyres now, plan the rest of the race again
                stopAfter.Insert(next, prevLap); sets.Insert(next + 1, current); next++;
                PlanFrom(lap, wet, totalLaps >= RealStrategy && wet == 0 && !Two());
                current = sets[next];
            }
            used[current] = true; stintStart = lap;
            return current;
        }
        bool Two() => (used[0] ? 1 : 0) + (used[1] ? 1 : 0) + (used[2] ? 1 : 0) >= 2;
    }

    public enum Flag { Green, Yellow, Vsc, VscEnding, Sc, ScIn, Restart }

    // what race control sees of a car (filled every update by the caller)
    public struct RcCar
    {
        public int idx;          // nearest track point (-1 = not on track yet)
        public double dist;      // physical laps: lap + idx / N (what overtaking is measured on)
        public double progress;  // race distance (laps, less pit time): for blue flags
        public double v;         // m/s
        public double damage;    // sum of the damage values (0 = none)
        public bool recovered;   // put back on track this update (a reset)
    }

    public class RaceControl
    {
        public const double VscFactor = 0.6, ScFactor = 0.55, ScMax = 50, QueueGap = 9, CarLen = 5.6;
        public const double PitLossSc = 12, PitLossVsc = 15;
        public Flag flag = Flag.Green;
        public bool yellow; public int yellowIdx = -1;
        public int serial;                      // +1 on every change of flag (the HUD shows a banner)
        public int totalLaps;                   // 0 = open (no SC with too few laps left is not checked)
        public double flagT;                    // s in the current flag
        // safety car
        public double scPos = -1, scV;          // track points (fractional), m/s
        public int scLapsDone, scLapsTarget;
        public bool ScOnTrack => flag == Flag.Sc || flag == Flag.ScIn;
        public bool Neutral => flag == Flag.Vsc || flag == Flag.VscEnding || flag == Flag.Sc || flag == Flag.ScIn || flag == Flag.Restart;
        public bool NoOvertaking => Neutral;
        public int vscCount, scCount, incidents;
        public int overtakes;                   // position changes on track while neutralised (should stay 0)
        public double queueSpread, maxQueueGap, maxQueueGapS; // m, front to back of the queue / biggest gap in it (m, and s at the speed there) (SC only)
        public int leaderSlot = -1;             // the car right behind the safety car (and the one leading the restart)
        // per car
        public RcCar[] cars = new RcCar[0];
        public double[] gapAhead = new double[0], vAhead = new double[0];
        public bool[] blue = new bool[0];
        public int[] rank = new int[0];         // place in the queue behind the safety car (0 = first)
        // the player
        public int playerSlot = -1;
        public double vscDelta;                 // s, + = slower than the VSC delta (fine), - = too fast
        public bool deltaWarn; public double penalty; public int penaltySerial; public string penaltyWhy = "";
        public int strikes;                     // track limits this race

        readonly Track track; readonly RacingLine line; readonly Random rng;
        double[] stopT = new double[0], prevDist = new double[0];
        readonly List<int> queueOrder = new List<int>(); int sortRef; Comparison<int> byBehind; // Update's queue sort, no garbage
        double lastIncident = -1e9, vscLeft, deltaPenT; bool penalisedThis; int restartPrevIdx = -1, lastCar = -1;

        public RaceControl(Track track, RacingLine line, int seed) { this.track = track; this.line = line; rng = new Random(seed * 104729 + 17); }

        public void Init(int n)
        {
            cars = new RcCar[n]; gapAhead = new double[n]; vAhead = new double[n]; blue = new bool[n]; rank = new int[n];
            stopT = new double[n]; prevDist = new double[n];
            for (int i = 0; i < n; i++) { cars[i].idx = -1; gapAhead[i] = double.MaxValue; prevDist[i] = double.NaN; }
        }

        public void Feed(int k, AiDriver d)
        {
            var dm = d.car.damage;
            cars[k] = new RcCar
            {
                idx = d.idx, dist = d.idx < 0 ? 0 : d.lap + (double)d.idx / track.N, progress = d.progress, v = Math.Sqrt(d.car.vx * d.car.vx + d.car.vy * d.car.vy), recovered = d.recovered,
                damage = dm.wingFL + dm.wingFR + dm.wingRear + dm.floor + dm.suspFL + dm.suspFR + dm.suspRL + dm.suspRR + (dm.Punctured ? 0.5 : 0)
            };
        }

        int N => track.N;
        double Behind(int refIdx, int i) => (((refIdx - i) % N + N) % N) * track.ds; // m from point i forward to refIdx

        // the time a stop costs now
        public double PitLoss(double normal) => ScOnTrack ? PitLossSc : flag == Flag.Vsc || flag == Flag.VscEnding ? PitLossVsc : normal;

        public void ForceSc() { if (flag != Flag.Sc && flag != Flag.ScIn) DeploySc(); }
        public void ForceVsc() { if (!Neutral) DeployVsc(); }

        void Set(Flag f) { if (f != flag) { flag = f; flagT = 0; serial++; } }

        void DeployVsc() { Set(Flag.Vsc); vscCount++; vscLeft = 18 + rng.NextDouble() * 15; penalisedThis = false; vscDelta = 0.5; }

        void DeploySc()
        {
            // out of the pit lane just ahead of the leader (the car furthest round the race)
            int lead = -1; for (int k = 0; k < cars.Length; k++) if (cars[k].idx >= 0 && (lead < 0 || cars[k].progress > cars[lead].progress)) lead = k;
            if (lead < 0) return;
            Set(Flag.Sc); scCount++;
            scPos = (cars[lead].idx + 120 / track.ds) % N; scV = Math.Min(line.speed[(int)scPos] * ScFactor, ScMax);
            scLapsDone = 0; scLapsTarget = 2 + rng.Next(0, 2); penalisedThis = false; vscDelta = 0.5;
        }

        void Incident(int k, bool serious, double t)
        {
            incidents++;
            // repeated: another car within 30 s (a stop and the reset that follows it are one incident)
            bool repeated = t - lastIncident < 30 && (k != lastCar || t - lastIncident > 12);
            if (k != lastCar || t - lastIncident > 12) { lastCar = k; }
            lastIncident = t; yellowIdx = cars[k].idx;
            if (Neutral) return;
            // a heavy, blocking or repeated incident: VSC or SC, by chance (and only with laps left to run it)
            if (!serious && !repeated) return;
            int leadLap = 0; foreach (var c in cars) leadLap = Math.Max(leadLap, (int)Math.Floor(c.dist));
            int left = totalLaps > 0 ? totalLaps - leadLap : 99;
            double roll = rng.NextDouble(), pSc = serious && repeated ? 0.45 : 0.25, pVsc = 0.35;
            if (roll < pSc && left >= 3) DeploySc();
            else if (roll < pSc + pVsc && left >= 1) DeployVsc();
        }

        // Once per AI tick (after Feed for every car): flags, the safety car, the queue, blue flags.
        public void Update(double dt, double t, bool started)
        {
            int n = cars.Length; flagT += dt;
            // ---- incidents: a car stopped on track, or a reset (heavy with high damage)
            if (started)
                for (int k = 0; k < n; k++)
                {
                    var c = cars[k]; if (c.idx < 0) continue;
                    bool queued = ScOnTrack || flag == Flag.Restart;
                    if (c.v < 3 && !queued) { stopT[k] += dt; if (stopT[k] - dt < 1.0 && stopT[k] >= 1.0) Incident(k, false, t); if (stopT[k] - dt < 6 && stopT[k] >= 6) Incident(k, true, t); } // 6 s: blocking
                    else stopT[k] = 0;
                    if (c.recovered) Incident(k, c.damage >= 1.0, t);
                }
            bool stopped = false; for (int k = 0; k < n; k++) if (stopT[k] >= 1.0) stopped = true;
            yellow = started && (t - lastIncident < 10 || stopped);
            // ---- flags
            switch (flag)
            {
                case Flag.Green: case Flag.Yellow: Set(yellow ? Flag.Yellow : Flag.Green); break;
                case Flag.Vsc: vscLeft -= dt; if (vscLeft < 6) Set(Flag.VscEnding); break;
                case Flag.VscEnding: vscLeft -= dt; if (vscLeft <= 0) Set(Flag.Green); break;
            }
            // ---- the safety car: drives the racing line at reduced speed
            if (ScOnTrack)
            {
                int i0 = ((int)Math.Floor(scPos) % N + N) % N;
                scV = Math.Min(line.speed[i0] * ScFactor, ScMax);
                double before = scPos; scPos += scV * dt / track.ds;
                if (scPos >= N) { scPos -= N; scLapsDone++; if (flag == Flag.Sc && scLapsDone >= scLapsTarget - 1 && (maxQueueGapS < 1.2 || scLapsDone >= scLapsTarget + 1)) Set(Flag.ScIn); }
                if (flag == Flag.ScIn && before < N * 0.85 && scPos >= N * 0.85) { Set(Flag.Restart); restartPrevIdx = leaderSlot >= 0 ? cars[leaderSlot].idx : -1; }
            }
            // ---- the queue: physical order behind the safety car (or behind the car leading the restart)
            int refIdx = ScOnTrack ? (int)Math.Floor(scPos) : flag == Flag.Restart && leaderSlot >= 0 && cars[leaderSlot].idx >= 0 ? (cars[leaderSlot].idx + 1) % N : -1;
            for (int k = 0; k < n; k++) { gapAhead[k] = double.MaxValue; vAhead[k] = 0; }
            queueSpread = maxQueueGap = maxQueueGapS = 0;
            if (refIdx >= 0)
            {
                // the list and the comparison are made once (this ran every AI tick under the safety car: a new list, a
                // closure and a delegate 60 times a second); the same sort of the same list, so the same order
                var order = queueOrder; order.Clear(); for (int k = 0; k < n; k++) if (cars[k].idx >= 0) order.Add(k);
                sortRef = refIdx; if (byBehind == null) byBehind = (a, b) => Behind(sortRef, cars[a].idx).CompareTo(Behind(sortRef, cars[b].idx));
                order.Sort(byBehind);
                for (int r = 0; r < order.Count; r++)
                {
                    int k = order[r]; rank[k] = r; double d = Behind(refIdx, cars[k].idx);
                    if (r == 0) { if (ScOnTrack) { gapAhead[k] = d; vAhead[k] = scV; } if (flag != Flag.Restart) leaderSlot = k; }
                    else { int a = order[r - 1]; gapAhead[k] = d - Behind(refIdx, cars[a].idx) - CarLen; vAhead[k] = cars[a].v; maxQueueGap = Math.Max(maxQueueGap, gapAhead[k]); maxQueueGapS = Math.Max(maxQueueGapS, gapAhead[k] / Math.Max(10, cars[k].v)); }
                    if (r == order.Count - 1) queueSpread = d - Behind(refIdx, cars[order[0]].idx);
                }
                // the restart: green when the car leading the queue crosses the line
                if (flag == Flag.Restart && leaderSlot >= 0)
                {
                    int li = cars[leaderSlot].idx;
                    if ((restartPrevIdx > N * 0.8 && li >= 0 && li < N * 0.2) || flagT > 150) { Set(Flag.Green); scPos = -1; }
                    restartPrevIdx = li;
                }
            }
            // ---- overtaking while neutralised (position changes on track between cars less than half a lap apart)
            for (int i = 0; i < n; i++)
            {
                if (Neutral && !double.IsNaN(prevDist[i]) && cars[i].idx >= 0)
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i || cars[j].idx < 0 || double.IsNaN(prevDist[j])) continue;
                        double before = prevDist[i] - prevDist[j], now = cars[i].dist - cars[j].dist;
                        if (before < 0 && now > 0 && now - before < 0.5 && !cars[i].recovered && !cars[j].recovered)
                        {
                            overtakes++;
                            if (i == playerSlot) Penalise(5, "OVERTAKING UNDER " + (ScOnTrack || flag == Flag.Restart ? "SC" : "VSC"));
                        }
                    }
            }
            for (int i = 0; i < n; i++) prevDist[i] = cars[i].idx >= 0 ? cars[i].dist : double.NaN;
            // ---- blue flags: a car at least ~one lap up the road is close behind
            for (int k = 0; k < n; k++)
            {
                blue[k] = false; if (cars[k].idx < 0 || Neutral) continue;
                for (int j = 0; j < n && !blue[k]; j++)
                {
                    if (j == k || cars[j].idx < 0) continue;
                    double b = Behind(cars[k].idx, cars[j].idx);
                    if (b > 0 && b < 60 && cars[j].progress - cars[k].progress > 0.7 && cars[j].v > cars[k].v - 3) blue[k] = true;
                }
            }
            // ---- the player's VSC delta: + = slower than VscFactor of the racing-line speed (as it must be)
            deltaWarn = false;
            if (playerSlot >= 0 && playerSlot < n && cars[playerSlot].idx >= 0)
            {
                var p = cars[playerSlot];
                bool applies = flag == Flag.Vsc || flag == Flag.VscEnding || (ScOnTrack && gapAhead[playerSlot] > 150);
                if (applies)
                {
                    double vRef = Math.Max(5, line.speed[p.idx] * VscFactor);
                    vscDelta = Math.Max(-5, Math.Min(2, vscDelta + dt * (1 - p.v / vRef)));
                    deltaWarn = vscDelta < 0;
                    deltaPenT = vscDelta < -1.0 ? deltaPenT + dt : 0;
                    if (deltaPenT > 2 && !penalisedThis) { penalisedThis = true; Penalise(5, "VSC DELTA"); }
                }
                else vscDelta = Math.Min(2, vscDelta + dt);
            }
        }

        void Penalise(double s, string why) { penalty += s; penaltySerial++; penaltyWhy = why; }

        // Track limits for the player: strikes 1-3 are warnings (the third with the black-and-white flag), the next
        // one is a 5 s time penalty and the count starts again. Returns the strike number (4 = penalty given).
        public int TrackLimits()
        {
            strikes++;
            if (strikes <= 3) return strikes;
            strikes = 0; Penalise(5, "TRACK LIMITS"); return 4;
        }

        // The speed a car may do now. vHere = its normal target speed at this point; returns double.MaxValue when free.
        public double Cap(int k, double vHere)
        {
            if (k < 0 || k >= cars.Length) return double.MaxValue;
            switch (flag)
            {
                case Flag.Vsc: case Flag.VscEnding: return VscFactor * vHere;
                case Flag.Sc: case Flag.ScIn: case Flag.Restart:
                    {
                        double delta = 0.8 * vHere; // catching up to the queue: still well below racing speed
                        if (flag == Flag.Restart && k == leaderSlot) return VscFactor * vHere; // the leader sets the pace to the line
                        double g = gapAhead[k];
                        if (g > 150) return delta;
                        return Math.Min(delta, Math.Max(0, vAhead[k] + (g - QueueGap - 0.15 * vAhead[k]) * 0.5)); // about 0.15 s more per m/s: a time gap
                    }
                default:
                    if (yellow && yellowIdx >= 0 && cars[k].idx >= 0) { double b = Behind(yellowIdx, cars[k].idx); if (b < 250) return 0.93 * vHere; }
                    return double.MaxValue;
            }
        }
        public bool Blue(int k) => k >= 0 && k < blue.Length && blue[k];
        public bool InQueue(int k) => (ScOnTrack || flag == Flag.Restart) && k >= 0 && k < gapAhead.Length && gapAhead[k] < 40;
        // the safety car's place for drawing it: on the racing line
        public bool ScPose(out double x, out double y, out double heading)
        {
            x = y = heading = 0; if (!ScOnTrack || scPos < 0) return false;
            int i = (int)Math.Floor(scPos) % N, j = (i + 1) % N; double f = scPos - Math.Floor(scPos);
            var a = track.pts[i]; var b = track.pts[j];
            double xa = a.x + a.nx * line.off[i], ya = a.y + a.ny * line.off[i], xb = b.x + b.nx * line.off[j], yb = b.y + b.ny * line.off[j];
            x = xa + (xb - xa) * f; y = ya + (yb - ya) * f; heading = Math.Atan2(yb - ya, xb - xa);
            return true;
        }
    }
}
