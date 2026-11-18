// TerrainCheck: does the ground beside each hilly circuit follow the road? Pure C# (the game's HeightField).
//   dotnet run -c Release [-- <circuit id> ...]   (default: every circuit with heights or banking)
// For every track point (2 m apart) and both sides it samples the ground the game drapes the verge, gravel,
// barriers and trackside objects on (HeightField.Sample) from the road edge out to 60 m, and compares it with the
// height of the road edge there:
//   band   = worst |ground - road edge| from the edge of the road out to 3 m past the barrier line (run-off,
//            barriers, fences: must be ~0); the count is of edge lines (track point and side) worse than 0.5 m
//   slope  = steepest ground slope across the track direction, edge .. 60 m (deg; embankments should stay <= ~35)
// (the edge level: the road plane, banked, out to the shoulder (HeightField.Shoulder), level beyond)
//   at 30 m = worst |ground - road edge| 30 m from the centreline (the outer edge of the mown verge)
//   mesh   = the terrain mesh's height there (HeightField.Lowest, as WorldBuilder.BuildTerrain) vs the road edge
// Where another leg of the circuit runs close by (closer than twice the distance sampled plus room for a ~31 deg bank
// to its level) the sample is skipped: the ground between two legs at different levels has to climb from one to the other.
// Crossings (one leg over another): the road surface of both legs (the height the road mesh gets) vs the leg's own
// height, so a bridge shows up as 0 and a road dragged up onto the other leg as the height difference; and the same
// for a car driving along the leg (what the car model stands on).
using System.Reflection;
using FormulaSim;

const double Far = 60, Tol = 0.5, MaxSlopeDeg = 36;
var ids = args.Length > 0 ? args : Circuits.All.Where(d => d.elev != null || d.bank != null).Select(d => d.id).ToArray();
Console.WriteLine($"{"circuit",-11} {"band>0.5m",9} {"worst band",11} {"at",14} {"slope>36",9} {"worst slope",12} {"at",14} {"mesh worst",11} {"at 30 m",8} {"road @cross",12} {"car @cross",11}");
foreach (var id in ids)
{
    var def = Circuits.All.First(d => d.id == id);
    var t = def.Build(); if (t.h == null && t.bank == null) { Console.WriteLine($"{id}: flat"); continue; }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var hf = new HeightField(t); double buildMs = sw.Elapsed.TotalMilliseconds;
    // the run-off band: the barrier line plus 3 m (where barriers, fences and the first stands stand)
    double Band = t.width / 2 + def.barrier + 3;
    var on = typeof(HeightField).GetMethod("SampleOn", new[] { typeof(double), typeof(double), typeof(int) });
    double RoadY(int i, double x, double y) => on != null ? (double)on.Invoke(hf, new object[] { x, y, i }) : hf.Sample(x, y);
    var al = typeof(HeightField).GetMethod("SampleAlong", new[] { typeof(double), typeof(double), typeof(double), typeof(double) });
    double CarY(double x, double y, double dx, double dy) => al != null ? (double)al.Invoke(hf, new object[] { x, y, dx, dy }) : hf.Sample(x, y);
    double[] tb = t.bank == null ? null : t.bank.Select(Math.Tan).ToArray();
    // nearest point of another leg (lap separation > 1.6 x planar distance + 30 m): distance and height difference
    (double d, double dh) OtherLeg(int i, double x, double y)
    {
        double best = double.MaxValue, dh = 0;
        for (int k = 0; k < t.N; k += 2)
        {
            double sep = Math.Abs(k - i) * t.ds; sep = Math.Min(sep, t.length - sep);
            double dx = t.pts[k].x - t.pts[i].x, dy = t.pts[k].y - t.pts[i].y, pd = Math.Sqrt(dx * dx + dy * dy);
            if (sep < 1.6 * pd + 30) continue;
            double ex = t.pts[k].x - x, ey = t.pts[k].y - y, e = Math.Sqrt(ex * ex + ey * ey);
            if (e < best) { best = e; dh = t.h[k] - t.h[i]; }
        }
        return (best, dh);
    }
    var near = new (double d, double dh)[t.N];
    for (int i = 0; i < t.N; i++) near[i] = OtherLeg(i, t.pts[i].x, t.pts[i].y);
    int bandBad = 0, slopeBad = 0, samples = 0; double worstBand = 0, worstSlope = 0, worstMesh = 0, worst30 = 0; string atB = "", atS = "";
    for (int i = 0; i < t.N; i++)
    {
        var p = t.pts[i]; double hw = t.HalfWidth(i);
        for (int sg = -1; sg <= 1; sg += 2)
        {
            double prev = double.NaN; bool bb = false, sb = false;
            for (double lat = hw; lat <= Far; lat += 1)
            {
                double x = p.x + p.nx * lat * sg, y = p.y + p.ny * lat * sg;
                // another leg close by at another level: from here on the ground has to climb to it (room for a bank of
                // ~31 deg kept free: 2.5 x the height difference)
                if (near[i].d < 2 * lat + 2.5 * Math.Abs(near[i].dh) + 6) break;
                // the road edge's level: the road plane (banked) out to the shoulder, level beyond
                double edge = t.h[i] - (tb != null ? sg * Math.Min(lat, hf.Shoulder) * tb[i] : 0);
                double g = hf.Sample(x, y), dev = g - edge;
                if (Math.Abs(lat - 30) < 0.5 && Math.Abs(dev) > Math.Abs(worst30)) worst30 = dev;
                if (lat <= Band)
                {
                    if (Math.Abs(dev) > Tol) bb = true;
                    if (Math.Abs(dev) > Math.Abs(worstBand)) { worstBand = dev; atB = $"s={p.s:F0} {(sg > 0 ? "L" : "R")}{lat:F0}"; }
                    double mesh = hf.Lowest(x, y, 10.8) - 0.12 - edge;
                    if (Math.Abs(mesh) > Math.Abs(worstMesh)) worstMesh = mesh;
                }
                if (!double.IsNaN(prev))
                {
                    double sl = Math.Atan(Math.Abs(g - prev)) * 180 / Math.PI;
                    if (sl > MaxSlopeDeg) sb = true;
                    if (sl > worstSlope) { worstSlope = sl; atS = $"s={p.s:F0} {(sg > 0 ? "L" : "R")}{lat:F0}"; }
                }
                prev = g; samples++;
            }
            if (bb) bandBad++; if (sb) slopeBad++;
        }
    }
    // crossings: road surface (what the road mesh is draped to) vs the leg's own height, across the road
    double worstRoad = 0, worstCar = 0;
    for (int i = 0; i < t.N; i++)
    {
        if (near[i].d > 30) continue;
        var p = t.pts[i]; double hw = t.HalfWidth(i);
        for (double lat = -hw; lat <= hw; lat += 1)
        {
            double own = t.h[i] - (tb != null ? lat * tb[i] : 0);
            double r = RoadY(i, p.x + p.nx * lat, p.y + p.ny * lat) - own;
            if (Math.Abs(r) > Math.Abs(worstRoad)) worstRoad = r;
            double c = CarY(p.x + p.nx * lat, p.y + p.ny * lat, p.tx, p.ty) - own;
            if (Math.Abs(c) > Math.Abs(worstCar)) worstCar = c;
        }
    }
    Console.WriteLine($"{id,-11} {bandBad,9} {worstBand,10:F1}m {atB,14} {slopeBad,9} {worstSlope,10:F0}deg {atS,14} {worstMesh,10:F1}m {worst30,7:F1}m {worstRoad,11:F1}m {worstCar,10:F1}m   ({2 * t.N} edge lines, grid {buildMs:F0} ms)");
}
