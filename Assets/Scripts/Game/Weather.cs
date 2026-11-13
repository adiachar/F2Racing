// Weather.cs — Dry / Damp / Wet, all on the game side (the physics in Assets/Scripts/Physics is not touched):
//  * the setting itself (saved as "weather"), and the global shader value _Wet (0 dry, 0.5 damp, 1 wet) that the
//    road (Road.cginc) and the car paint (CarPaint.shader) read, so the wet look costs no extra draw call;
//  * grip: after WheelContact.Update has set each wheel's grip from its surface, Apply scales car.wMu for the
//    weather and the tyre fitted. In the damp the racing line has dried, so wheels on it grip more;
//  * the tyre sets: the three slick compounds of CompoundTable plus Intermediate and Wet. While CompoundTable has
//    only the slicks, an Intermediate or Wet runs on the Soft compound's temperature and wear model, and its
//    grip comes from the table below (the tyre's weather factor relative to a dry slick).
//
// Grip factors (share of a dry slick's grip on dry asphalt):
//   tyre          Dry    Damp (off line / dry line)   Wet
//   slick         1.00   0.78 / 0.88                  0.62
//   intermediate  0.88   0.84 / 0.86                  0.72
//   full wet      0.78   0.78 / 0.79                  0.77
// Paint and kerbs lose more when wet (x0.82 damp, x0.80 wet: a slick on a wet kerb is at ~0.50), grass and
// astroturf more still, gravel a little.
// Basis: slicks at about 0.80 of dry grip when damp and 0.60-0.65 when wet (the brief); Pirelli's rule of thumb
// that the slick -> intermediate crossover comes when the lap time is 110-112 % of the dry lap, and the same again
// from intermediates to full wets (lap time goes roughly with grip^-0.5..-1 for a downforce car, so ~0.8 of the grip
// at the first crossover). The crossovers in this table: damp = slicks and inters about even (slicks faster on the
// dry line, inters off it), wet = full wets > inters > slicks.
using FormulaSim;
using UnityEngine;

public static class Weather
{
    public static readonly string[] Names = { "Dry", "Damp", "Wet" };
    public static int Index;                               // 0 dry, 1 damp, 2 wet
    public static float Wetness => Index == 0 ? 0f : Index == 1 ? 0.5f : 1f;

    // ---- tyre sets: 0..2 = CompoundTable's slicks, 3 = Intermediate, 4 = Wet
    public const int Inter = 3, Full = 4, Sets = 5;
    public static string TyreName(int set) => set < CompoundTable.Defs.Length && set < 3 ? CompoundTable.Defs[set].name : set == Inter ? "Intermediate" : "Wet";
    public static string[] TyreNames() { var n = new string[Sets]; for (int i = 0; i < Sets; i++) n[i] = TyreName(i); return n; }
    // The physics compound the set runs on: its own once CompoundTable has Intermediate and Wet (indices 3, 4),
    // until then the Soft (the coolest-running slick).
    public static Compound PhysicsCompound(int set) => set < CompoundTable.Defs.Length ? (Compound)set : Compound.Soft;
    static int Kind(int set) => set == Inter ? 1 : set == Full ? 2 : 0; // 0 slick, 1 inter, 2 wet
    public static Color Stripe(int set) => set == Inter ? new Color(0.15f, 0.70f, 0.20f) : set == Full ? new Color(0.10f, 0.35f, 0.90f)
        : CarModel.StripeFor(PhysicsCompound(set));

    //                                          dry   damp off  damp line  wet
    static readonly double[,] Asphalt = { { 1.00, 0.78, 0.88, 0.62 },   // slick
                                          { 0.88, 0.84, 0.86, 0.72 },   // intermediate
                                          { 0.78, 0.78, 0.79, 0.77 } }; // full wet

    // grip factor of a set on track asphalt, dryLine 0..1 (how much of the tyre is on the dried line; damp only)
    public static double TrackFactor(int set, double dryLine = 0)
    {
        int k = Kind(set);
        if (Index == 0) return Asphalt[k, 0];
        if (Index == 2) return Asphalt[k, 3];
        return Asphalt[k, 1] + (Asphalt[k, 2] - Asphalt[k, 1]) * dryLine;
    }

    // extra loss on other surfaces when wet (multiplies TrackFactor)
    static double SurfaceExtra(SurfaceType t)
    {
        if (Index == 0) return 1;
        bool damp = Index == 1;
        switch (t)
        {
            case SurfaceType.Paint: case SurfaceType.Kerb: case SurfaceType.SausageKerb: return damp ? 0.82 : 0.80;
            case SurfaceType.Grass: case SurfaceType.Astroturf: return damp ? 0.80 : 0.70;
            case SurfaceType.Gravel: return 0.95;
            default: return 1; // asphalt, concrete, run-off asphalt
        }
    }

    // the best set for the weather on the racing line (the rivals' choice)
    public static int BestSet()
    {
        int best = 0; double g = 0;
        for (int s = 0; s < Sets; s++) { double f = TrackFactor(s, 1); if (f > g + 1e-6) { g = f; best = s; } }
        return best;
    }

    // Call right after WheelContact.Update(track, line, car, idx, lat, ...) with the same arguments.
    public static void Apply(Track t, RacingLine line, FormulaCar car, int idx, double lat, int set)
    {
        if (Index == 0 && Kind(set) == 0) return;               // dry slicks: exactly as before
        if (idx < 0 || t.kerbL == null) return;                 // WheelContact did nothing: do not scale twice
        var c = car.cfg; var p = t.pts[idx];
        double bw = c.wheelbase * c.frontWeight, aw = c.wheelbase - bw;
        double ch = System.Math.Cos(car.heading), sh = System.Math.Sin(car.heading);
        for (int w = 0; w < 4; w++)
        {
            double dry = 0;
            if (Index == 1 && line != null)
            {
                // the same wheel position as WheelContact: lateral offset and track point under the tyre
                double along = w < 2 ? aw : -bw, side = (w % 2 == 0 ? 1 : -1) * c.track / 2;
                double dx = ch * along - sh * side, dy = sh * along + ch * side;
                double wl = lat + dx * p.nx + dy * p.ny;
                int wi = ((idx + (int)System.Math.Round((dx * p.tx + dy * p.ty) / t.ds)) % t.N + t.N) % t.N;
                double off = System.Math.Abs(wl - line.off[wi]);
                dry = System.Math.Max(0, System.Math.Min(1, (2.2 - off) / 1.2)); // dry within ~1 m of the line, wet by 2.2 m
            }
            car.wMu[w] *= TrackFactor(set, dry) * SurfaceExtra(car.wType[w]);
        }
    }

    // the playground (no track data): one surface for all wheels, 0 asphalt, 1 kerb, 2 grass
    public static double PadFactor(int surface, int set) =>
        TrackFactor(set) * SurfaceExtra(surface == 0 ? SurfaceType.Asphalt : surface == 1 ? SurfaceType.Kerb : SurfaceType.Grass);

    // ---------------------------------------------------------------- look
    static readonly int WetId = Shader.PropertyToID("_Wet");
    public static void PushGlobals() { Shader.SetGlobalFloat(WetId, Wetness); }

    public static void Load() { Index = Mathf.Clamp(PlayerPrefs.GetInt("weather", 0), 0, Names.Length - 1); PushGlobals(); }
    public static void Save() { PlayerPrefs.SetInt("weather", Index); }
}
