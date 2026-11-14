// Sectors.cs — live timing for the HUD (RaceHud.cs): three sectors per circuit, sector times for every car with
// purple / green / yellow, timing loops for the gaps in the timing tower, and the player's live delta to the best lap.
// Pure bookkeeping: it reads the game's own lap counters and lap times and never changes them. No allocation per
// frame (arrays are made once per session).
using FormulaSim;

public static class SectorMap
{
    // Where sectors 1 and 2 end, as a share of the lap distance from the timing line. The circuits listed use the
    // official timing splits, placed by eye from the circuit maps (approximate: within about 2 % of the lap);
    // every other circuit is cut into thirds.
    public static void Ends(CircuitDef def, out double f1, out double f2)
    {
        switch (def != null ? def.id : null)
        {
            case "monaco": f1 = 0.31; f2 = 0.66; break; // S1 to the top of Beau Rivage hill, S2 to the exit of the tunnel chicane
            case "spa":    f1 = 0.29; f2 = 0.75; break; // S1 to the end of the Kemmel straight, S2 to before Blanchimont
            case "monza":  f1 = 0.34; f2 = 0.72; break; // S1 to after the Roggia chicane, S2 to after Ascari
            case "suzuka": f1 = 0.35; f2 = 0.74; break; // S1 to after the Dunlop curve, S2 to after Spoon
            default:       f1 = 1.0 / 3; f2 = 2.0 / 3; break;
        }
    }
}

// one car's timing: sector crossings this lap, last and best sectors, timing-loop pass times (for the gaps)
public class CarTiming
{
    public const int Loops = 25, Ring = Loops * 3; // timing loops per lap; pass times kept for the last three laps
    public int lastIdx = -1; public double lastT;
    public double lapT0 = double.NaN, s1At = double.NaN, s2At = double.NaN;
    public readonly double[] cur = new double[3], last = new double[3], best = new double[3];
    public readonly int[] curCol = new int[3], lastCol = new int[3];          // 0 none, 1 yellow, 2 green (personal best), 3 purple (overall best)
    public double lastLap = double.NaN, bestLap = double.NaN;
    public bool invalid;                                 // this lap (track limits): set by the caller
    // this update's events
    public int evSector = -1; public double evLap = double.NaN; public bool evLapValid, evLapPb;
    // timing loops: key = lap * Loops + loop
    public int lastKey = int.MinValue;
    readonly int[] ringKey = new int[Ring]; readonly double[] ringT = new double[Ring];

    public CarTiming() { Clear(); }
    public void Clear()
    {
        lastIdx = -1; lapT0 = s1At = s2At = double.NaN; lastLap = bestLap = double.NaN; invalid = false; lastKey = int.MinValue;
        for (int i = 0; i < 3; i++) { cur[i] = last[i] = best[i] = double.NaN; curCol[i] = lastCol[i] = 0; }
        for (int i = 0; i < Ring; i++) ringKey[i] = int.MinValue;
    }

    public bool TimeAt(int key, out double t)
    {
        t = double.NaN; if (key == int.MinValue) return false;
        int k = ((key % Ring) + Ring) % Ring;
        t = ringT[k]; return ringKey[k] == key;
    }
    public void AbortLap() { lapT0 = s1At = s2At = double.NaN; lastIdx = -1; invalid = false; lastKey = int.MinValue; for (int i = 0; i < 3; i++) { cur[i] = double.NaN; curCol[i] = 0; } }

    // the timing loops passed since the last update (lap = the game's lap counter for this car)
    public void Loop(int lap, int idx, int N, double t)
    {
        int key = lap * Loops + (int)((long)idx * Loops / N);
        if (lastKey == int.MinValue || key < lastKey || key - lastKey > Ring) { lastKey = key; Put(key, t); return; } // first, backwards or a jump
        for (int k = lastKey + 1; k <= key; k++) Put(k, t);
        lastKey = key;
    }
    void Put(int key, double t) { int k = ((key % Ring) + Ring) % Ring; ringKey[k] = key; ringT[k] = t; }
}

public class SectorTimer
{
    public int N, b1, b2;
    public readonly double[] overall = new double[3];    // best sector times of the session, all cars
    public SectorTimer(Track track)
    {
        N = track.N;
        SectorMap.Ends(track.def, out double f1, out double f2);
        b1 = Clamp((int)System.Math.Round(f1 * N)); b2 = Clamp((int)System.Math.Round(f2 * N));
        ResetBests();
    }
    int Clamp(int i) => i < 1 ? 1 : i > N - 1 ? N - 1 : i;
    public void ResetBests() { for (int i = 0; i < 3; i++) overall[i] = double.NaN; }
    public double Fraction(int sector) => sector == 0 ? b1 / (double)N : sector == 1 ? (b2 - b1) / (double)N : (N - b2) / (double)N;
    public int SectorOf(int idx) => idx < b1 ? 0 : idx < b2 ? 1 : 2;

    // One car, once a frame. lapStart / lapTime are the game's own (lapStart changes when a lap starts; lapTime is
    // the lap just finished). trace: the player's delta trace, or null.
    public void Update(CarTiming c, int idx, double t, double lapStart, double lapTime, DeltaTrace trace)
    {
        c.evSector = -1; c.evLap = double.NaN;
        if (idx < 0) return;
        if (double.IsNaN(lapStart)) { c.lapT0 = c.s1At = c.s2At = double.NaN; c.lastIdx = idx; c.lastT = t; if (trace != null) trace.Abort(); return; }
        if (lapStart != c.lapT0)
        {
            // a new lap: the one before ends here if it was timed from its start
            if (!double.IsNaN(c.lapT0) && lapStart > c.lapT0 && !double.IsNaN(c.s2At) && !double.IsNaN(lapTime))
            {
                double s3 = lapTime - (c.s2At - c.lapT0);
                Done(c, 2, s3);
                bool valid = !c.invalid;
                c.lastLap = lapTime; c.evLap = lapTime; c.evLapValid = valid;
                c.evLapPb = valid && (double.IsNaN(c.bestLap) || lapTime < c.bestLap);
                if (c.evLapPb) c.bestLap = lapTime;
                for (int i = 0; i < 3; i++) { c.last[i] = c.cur[i]; c.lastCol[i] = c.curCol[i]; }
                if (trace != null) trace.Finish(lapTime, c.evLapPb);
            }
            else if (trace != null) trace.Abort();
            c.lapT0 = lapStart; c.s1At = c.s2At = double.NaN; c.invalid = false;
            for (int i = 0; i < 3; i++) { c.cur[i] = double.NaN; c.curCol[i] = 0; }
            if (trace != null) trace.Begin(idx, t - lapStart);
            c.lastIdx = idx; c.lastT = t;
            return;
        }
        if (c.lastIdx < 0) { c.lastIdx = idx; c.lastT = t; return; }
        int d = idx - c.lastIdx;
        if (d < -N / 2) d += N; else if (d > N / 2) d -= N;
        if (d > 0 && d < N / 4)
        {
            if (double.IsNaN(c.s1At) && Passed(c.lastIdx, d, b1, out double f)) { c.s1At = c.lastT + (t - c.lastT) * f; Done(c, 0, c.s1At - c.lapT0); }
            if (!double.IsNaN(c.s1At) && double.IsNaN(c.s2At) && Passed(c.lastIdx, d, b2, out f)) { c.s2At = c.lastT + (t - c.lastT) * f; Done(c, 1, c.s2At - c.s1At); }
            if (trace != null) trace.Mark(c.lastIdx, d, c.lastT - c.lapT0, t - c.lapT0);
        }
        c.lastIdx = idx; c.lastT = t;
    }

    // did the car move over point b going from a by d points forward? f = where in the step
    bool Passed(int a, int d, int b, out double f)
    {
        int k = ((b - a) % N + N) % N;
        f = d > 0 ? k / (double)d : 1;
        return k >= 1 && k <= d;
    }

    void Done(CarTiming c, int s, double time)
    {
        c.cur[s] = time; c.evSector = s;
        if (c.invalid || time <= 0) { c.curCol[s] = 1; return; }
        bool pb = double.IsNaN(c.best[s]) || time < c.best[s];
        if (pb) c.best[s] = time;
        if (double.IsNaN(overall[s]) || time <= overall[s]) { overall[s] = time; c.curCol[s] = 3; }
        else c.curCol[s] = pb ? 2 : 1;
    }
}

// The player's lap, point by point: time into the lap at every track point, this lap and the best lap.
// Delta = time into this lap now - the best lap's time at the same point.
public class DeltaTrace
{
    float[] cur, best; bool curOk, bestOk; public double bestTime = double.NaN;
    public DeltaTrace(int n) { cur = new float[n]; best = new float[n]; }
    public int Length => cur.Length;
    public void Clear() { curOk = bestOk = false; bestTime = double.NaN; }
    public void Abort() { curOk = false; }
    public void Begin(int idx, double t) { curOk = true; for (int i = 0; i < cur.Length; i++) cur[i] = float.NaN; if (idx >= 0 && idx < cur.Length) cur[idx] = (float)t; }
    public void Mark(int a, int d, double ta, double tb)
    {
        if (!curOk) return;
        int n = cur.Length;
        for (int k = 1; k <= d; k++) cur[(a + k) % n] = (float)(ta + (tb - ta) * k / d);
    }
    public void Finish(double lapTime, bool newBest)
    {
        if (curOk && newBest) { var tmp = best; best = cur; cur = tmp; bestOk = true; bestTime = lapTime; }
        curOk = false;
    }
    // seconds against the best lap at point idx (NaN = nothing to compare)
    public double Delta(int idx, double lapT)
    {
        if (!curOk || !bestOk || idx < 0 || idx >= best.Length) return double.NaN;
        float b = best[idx]; if (float.IsNaN(b)) return double.NaN;
        return lapT - b;
    }
}
