// TrackDump: builds every circuit exactly as the game does (CircuitDef.Build) and writes its final centreline
// as CSV: x, y (= Unity X, Z), h (track height = Unity Y before HeightField blending), half-width.
// Usage: dotnet run -- <outDir>
using System.Globalization;
using FormulaSim;
var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);
var inv = CultureInfo.InvariantCulture;
foreach (var def in Circuits.All)
{
    if (def.id == "test") continue;
    var t = def.Build();
    using var w = new StreamWriter(Path.Combine(outDir, def.id + ".csv"));
    w.WriteLine($"# id={def.id} N={t.N} ds={t.ds.ToString(inv)} width={t.width.ToString(inv)} length={t.length.ToString(inv)} barrier={def.barrier.ToString(inv)}");
    w.WriteLine("x,y,h,hw,bank");
    for (int i = 0; i < t.N; i++)
    {
        var p = t.pts[i];
        double h = t.h != null ? t.h[i] : 0, b = t.bank != null ? t.bank[i] : 0;
        w.WriteLine(string.Join(",", p.x.ToString("F3", inv), p.y.ToString("F3", inv), h.ToString("F3", inv), (t.width / 2).ToString("F3", inv), b.ToString("F5", inv)));
    }
    Console.WriteLine($"{def.id}: N={t.N} len={t.length:F0} w={t.width:F1}");
}
