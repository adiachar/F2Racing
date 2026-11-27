// Tools/Scenery: finds where each circuit looks empty from the track and writes a deterministic scenery fill for it.
//
//   cd Tools/Scenery && dotnet run -c Release                 # analyse, generate, write, analyse again (all circuits)
//   dotnet run -c Release -- analyse [ids...]                 # the empty stretches only (with the current fill files)
//   dotnet run -c Release -- generate [ids...]                # (re)write Assets/Resources/MapEdits/Auto/<id>.json
//   (then delete bin/ and obj/ here)
//
// How:
//  * The circuit is built exactly as the game builds it (Assets/Scripts/Physics, CircuitDef.Build).
//  * What the game already puts round it is estimated from the same rules (kept in step by hand with the game code):
//    the procedural trees (Environment.Trees: same random stream, same noise, same near / far / clump / canopy rules;
//    the trackside buildings are approximated), the woods (Environment.Canopy > 0.5, 12 m cells), the pit building and
//    the grandstands (Trackside: pit straight, main stands, per-circuit / scored corner stands), the landmarks
//    (Environment marks) and, at Monza, the real map (Resources/World/Monza.bytes: buildings, trees, forest areas).
//  * Density: every 25 m along the lap, per side, the weighted count of what stands 3-90 m ("near") and 90-250 m
//    ("far") beyond the barrier line within +-60 m along the lap (a tree 1, a bush 0.3, a 12 m wood cell 1, a stand
//    or pit bay 4-6 per 8 m, a building by its floor area). Below NearMin / FarMin = empty.
//  * Fill (per circuit theme, seeded by the circuit id): near = grandstand rows on the outside of the big corners
//    (+ a TV tower), hospitality units, services (toilets, kiosks, merchandise, medical), bleachers, a media centre,
//    tree lines; mid = sheds / hangars, woods, houses; far = farms, woods, blocks of flats. Every footprint stays >= 15 m
//    beyond the barrier line of every part of the track (other legs too), off the pits, stands, the test pad, the real
//    buildings, and nothing tall goes on the inside of a bend (sightlines). The game drops anything that still meets a
//    trackside building, the water, a steep slope or a remove area (Environment.AutoFill).
//  * Output: MapEdits format (MapEdits.File: trees, buildings, models; world metres; model y = lift above the ground),
//    read by MapEdits.Load from Resources/MapEdits/Auto/<id>.json. Report: counts, triangles, draw calls (one per
//    landmark material, merged), shot positions for Tools/Shots/shoot.py.
using System.Globalization;
using System.Text;
using FormulaSim;

static class Program
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    static int Main(string[] args)
    {
        // repo root: walk up from the current directory to the folder with Assets/
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "Assets", "Scripts"))) d = d.Parent;
        if (d == null) { Console.Error.WriteLine("run inside the repo"); return 1; }
        Root = d.FullName;
        string mode = args.Length > 0 && (args[0] == "analyse" || args[0] == "generate" || args[0] == "all") ? args[0] : "all";
        var ids = new HashSet<string>(args.Where(a => a != mode));
        Lm.Load(Path.Combine(Root, "Assets/Resources/Landmarks/landmarks.bytes"));
        var report = new StringBuilder();
        foreach (var def in Circuits.All)
        {
            if (def.id == "test" || (ids.Count > 0 && !ids.Contains(def.id))) continue;
            var c = new Ctx(def);
            if (def.style == CircuitStyle.Street)
            {
                Console.WriteLine($"== {def.id}: street circuit, town rows line both sides of the whole lap (Scenery.Town): nothing to fill");
                continue;
            }
            string path = Path.Combine(Root, "Assets/Resources/MapEdits/Auto", def.id + ".json");
            if (c.RealMap)
            {
                // the real map (Resources/World): its buildings, woods, roads and car parks are what is there; the
                // generator does not know the roads and paved areas, so it adds nothing
                c.Analyse(true, "real map: nothing generated");
                if (mode != "analyse" && File.Exists(path)) File.Delete(path);
                continue;
            }
            if (mode == "analyse")
            {
                if (File.Exists(path)) c.LoadFill(File.ReadAllText(path));
                c.Analyse(true, "current");
                continue;
            }
            var before = c.Analyse(mode == "all", "before");
            c.Generate();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, c.Json());
            var dump = System.Environment.GetEnvironmentVariable("SCENERY_DUMP");
            if (!string.IsNullOrEmpty(dump)) { Directory.CreateDirectory(dump); c.Dump(Path.Combine(dump, def.id)); }
            var after = c.Analyse(mode == "all", "after");
            report.AppendLine(c.Summary(before, after));
        }
        if (report.Length > 0) { Console.WriteLine(); Console.WriteLine("== summary"); Console.Write(report); }
        return 0;
    }

    // ================================================================ landmark models (Resources/Landmarks/landmarks.bytes)
    public sealed class LmModel { public string name; public float minX, minY, minZ, sx, sy, sz; public int tris; public HashSet<string> mats = new HashSet<string>(); }
    public static class Lm
    {
        public static readonly Dictionary<string, LmModel> M = new Dictionary<string, LmModel>();
        public static void Load(string path)
        {
            var d = File.ReadAllBytes(path); int p = 4;
            int U16() { int v = d[p] | d[p + 1] << 8; p += 2; return v; }
            int I32() { int v = BitConverter.ToInt32(d, p); p += 4; return v; }
            float F() { float v = BitConverter.ToSingle(d, p); p += 4; return v; }
            string S() { int n = d[p++]; var s = Encoding.UTF8.GetString(d, p, n); p += n; return s; }
            U16(); int nm = U16(); var mats = new string[nm]; for (int i = 0; i < nm; i++) mats[i] = S();
            int nModels = U16();
            for (int k = 0; k < nModels; k++)
            {
                var m = new LmModel { name = S() };
                m.minX = F(); m.minY = F(); m.minZ = F(); m.sx = F(); m.sy = F(); m.sz = F();
                int ns = d[p++];
                for (int s = 0; s < ns; s++)
                {
                    int mat = d[p++]; int nv = I32(), nt = I32();
                    p += nv * 24 + nt * 6; m.tris += nt; m.mats.Add(mats[mat]);
                }
                M[m.name] = m;
            }
        }
    }

    // ================================================================ per-circuit environment (copied from Environment.cs)
    public sealed class Env
    {
        public string tree = "Broadleaf", tree2 = "None"; public float tree2Share, treeMul = 1, treeNear = 26, treeFar = 600, treeSize = 1, clump, canopy, canopyAt = 48, cover = 0.5f;
        // fill theme: weights of the near / mid / far cluster kinds, tree heights (m) of lines / woods
        public Dictionary<string, float> near = new(), mid = new(), far = new();
        public float treeH0 = 9, treeH1 = 14; public bool buildings = true; public string[] walls; public int maxTrees = 600;
    }
    static readonly Dictionary<string, Env> Envs = new()
    {
        ["spa"] = new Env { tree = "Spruce", tree2 = "Broadleaf", tree2Share = 0.12f, treeNear = 22, treeFar = 330, treeSize = 1.1f, canopy = 15, canopyAt = 38, cover = 0.82f,
            near = { ["hosp"] = 2, ["services"] = 2, ["treeline"] = 2, ["bleachers"] = 1 }, mid = { ["woodland"] = 3, ["houses"] = 1 }, far = { ["farm"] = 3, ["woodland"] = 3 }, treeH0 = 14, treeH1 = 21, maxTrees = 250 },
        ["monza"] = new Env { tree = "Broadleaf", tree2 = "Broadleaf", tree2Share = 0.4f, treeMul = 1.25f, treeNear = 9, treeFar = 380, treeSize = 2.2f, canopy = 24, canopyAt = 36, cover = 0.72f,
            near = { ["hosp"] = 2, ["services"] = 2, ["treeline"] = 3, ["bleachers"] = 1 }, mid = { ["woodland"] = 4 }, far = { ["woodland"] = 3 }, treeH0 = 14, treeH1 = 22, buildings = false, maxTrees = 200 },
        ["silverstone"] = new Env { tree = "Broadleaf", treeMul = 0.45f, treeNear = 60, treeFar = 600, clump = 0.62f, treeSize = 1.1f,
            near = { ["hosp"] = 3, ["services"] = 2, ["treeline"] = 3, ["bleachers"] = 2 }, mid = { ["hangar"] = 3, ["woodland"] = 2, ["houses"] = 1 }, far = { ["farm"] = 3, ["woodland"] = 3 }, treeH0 = 9, treeH1 = 15, maxTrees = 1000 },
        ["suzuka"] = new Env { tree = "Broadleaf", tree2 = "Spruce", tree2Share = 0.3f, treeMul = 1.3f, treeNear = 24, treeFar = 420, treeSize = 1.15f, canopy = 11, canopyAt = 52, cover = 0.5f,
            near = { ["hosp"] = 2, ["services"] = 2, ["treeline"] = 3, ["bleachers"] = 1 }, mid = { ["woodland"] = 4, ["industrial"] = 1, ["houses"] = 2 }, far = { ["woodland"] = 4, ["houses"] = 2 }, treeH0 = 10, treeH1 = 16, maxTrees = 550 },
        ["austin"] = new Env { tree = "Broadleaf", tree2 = "Scrub", tree2Share = 0.55f, treeMul = 0.9f, treeNear = 45, treeFar = 600, clump = 0.5f, treeSize = 0.85f,
            near = { ["hosp"] = 3, ["services"] = 3, ["bleachers"] = 2, ["treeline"] = 1 }, mid = { ["industrial"] = 2, ["woodland"] = 2, ["houses"] = 1 }, far = { ["farm"] = 2, ["woodland"] = 2 }, treeH0 = 7, treeH1 = 11,
            walls = new[] { "0.86,0.80,0.68", "0.78,0.70,0.58", "0.92,0.90,0.86" }, maxTrees = 1000 },
        ["budapest"] = new Env { tree = "Broadleaf", tree2 = "Scrub", tree2Share = 0.3f, treeMul = 1.5f, treeNear = 50, treeFar = 520, clump = 0.42f,
            near = { ["bleachers"] = 3, ["services"] = 2, ["hosp"] = 1, ["treeline"] = 2 }, mid = { ["woodland"] = 3, ["houses"] = 2 }, far = { ["houses"] = 3, ["woodland"] = 2, ["farm"] = 1 }, treeH0 = 9, treeH1 = 14, maxTrees = 800 },
        ["catalunya"] = new Env { tree = "Pine", tree2 = "Scrub", tree2Share = 0.4f, treeMul = 1.8f, treeNear = 36, treeFar = 480, clump = 0.2f, canopy = 8, canopyAt = 64, cover = 0.26f,
            near = { ["hosp"] = 2, ["services"] = 2, ["bleachers"] = 2, ["treeline"] = 2 }, mid = { ["industrial"] = 4, ["woodland"] = 2 }, far = { ["industrial"] = 2, ["woodland"] = 2, ["flats"] = 1 }, treeH0 = 8, treeH1 = 11,
            walls = new[] { "0.90,0.84,0.72", "0.86,0.72,0.58", "0.95,0.93,0.88" }, maxTrees = 800 },
        ["spielberg"] = new Env { tree = "Spruce", tree2 = "Broadleaf", tree2Share = 0.2f, treeMul = 1.1f, treeNear = 40, treeFar = 380, clump = 0.4f, treeSize = 1.1f, canopy = 14, canopyAt = 70, cover = 0.42f,
            near = { ["hosp"] = 2, ["services"] = 2, ["treeline"] = 2, ["bleachers"] = 1 }, mid = { ["woodland"] = 3, ["houses"] = 1 }, far = { ["farm"] = 4, ["woodland"] = 3 }, treeH0 = 14, treeH1 = 20, maxTrees = 350 },
        ["zandvoort"] = new Env { tree = "Scrub", tree2 = "Pine", tree2Share = 0.18f, treeMul = 1.6f, treeNear = 22, treeFar = 420, treeSize = 0.8f, clump = 0.25f,
            near = { ["bleachers"] = 3, ["services"] = 2, ["hosp"] = 2, ["treeline"] = 1 }, mid = { ["houses"] = 3, ["woodland"] = 2 }, far = { ["flats"] = 2, ["houses"] = 2, ["woodland"] = 1 }, treeH0 = 7, treeH1 = 10,
            walls = new[] { "0.80,0.40,0.30", "0.92,0.90,0.86", "0.70,0.66,0.60" }, maxTrees = 800 },
    };

    // ================================================================ objects round the track
    public enum K { Tree, Bush, Wood, Stand, Pit, Building, Mark, Model, AutoTree, AutoBld }
    public struct Obj { public float x, z, w; public K k; public int idx; public float beyond; public int side; }
    public struct Foot { public float cx, cz, tx, tz, hl, hd; }   // oriented box (as Trackside's feet)
    public struct Circle { public float x, z, r; }

    // output records (MapEdits.File)
    public sealed class OutTree { public float x, z, h; }
    public sealed class OutBld { public float x, z, w, d, h, yaw; public int kind, roof; public float r, g, b, a; }
    public sealed class OutMdl { public string name; public float x, z, y, yaw, scale = 1; }

    public sealed class Ctx
    {
        public readonly CircuitDef def; public readonly Track t; public readonly Env e; readonly int N; readonly float ds, wall, hw;
        readonly float[] curvAbs; readonly List<int> corners = new();
        readonly List<Foot> feet = new(); readonly List<Circle> claims = new(); readonly List<Obj> existing = new();
        readonly List<Circle> placed = new(); // footprints of the fill
        public readonly List<OutTree> trees = new(); public readonly List<OutBld> blds = new(); public readonly List<OutMdl> mdls = new();
        readonly List<(int idx, int side, float w)> shotSpots = new();
        int pitSide, pitA, pitB; float padX0, padX1, padZ0, padZ1;
        WorldData world; public bool RealMap => world != null;
        // Clear field (Environment.BuildField): distance to the track / trackside buildings on a 12 m grid
        float[] dist; int fnx, fnz; float fx0, fz0, fx1, fz1; const float FCell = 12f;
        System.Random rng;

        public Ctx(CircuitDef d)
        {
            def = d; t = d.Build(); N = t.N; ds = (float)t.ds; hw = (float)t.width / 2;
            wall = (float)Traffic.WallOffset(t) + 0.05f;
            e = Envs.TryGetValue(d.id, out var env) ? env : new Env();
            curvAbs = new float[N]; for (int i = 0; i < N; i++) curvAbs[i] = Math.Abs((float)t.pts[i].curv);
            uint h = 2166136261; foreach (char ch in d.id) { h ^= ch; h *= 16777619; }
            rng = new System.Random((int)(h & 0x7fffffff));
            var bb = t.Bounds(); float cx = (float)(bb.minX + bb.maxX) / 2;
            padX0 = cx - 400; padX1 = cx + 400; padZ0 = (float)bb.minY - 780; padZ1 = (float)bb.minY - 300;
            if (d.id == "monza")
            {
                var wp = Path.Combine(Root, "Assets/Resources/World/Monza.bytes");
                if (File.Exists(wp)) world = WorldData.Parse(File.ReadAllBytes(wp));
            }
            FindCorners(); Trackside(); BuildField(); Marks(); ExistingTrees(); ExistingWorld();
            foreach (var o in existingTmp) existing.Add(Locate(o));
        }
        int W(int k) => ((k % N) + N) % N;
        int Gap(int a, int b) { int dd = Math.Abs(a - b); return Math.Min(dd, N - dd); }
        (float x, float z) P(int i) => ((float)t.pts[W(i)].x, (float)t.pts[W(i)].y);
        (float x, float z) Tn(int i) { var p = t.pts[W(i)]; float l = (float)Math.Sqrt(p.tx * p.tx + p.ty * p.ty); return ((float)p.tx / l, (float)p.ty / l); }
        (float x, float z) Nm(int i) { var p = t.pts[W(i)]; float l = (float)Math.Sqrt(p.nx * p.nx + p.ny * p.ny); return ((float)p.nx / l, (float)p.ny / l); }
        readonly List<Obj> existingTmp = new();
        void Add(float x, float z, float w, K k) => existingTmp.Add(new Obj { x = x, z = z, w = w, k = k });

        // ---------------------------------------------------------------- Trackside (approximate): corners, pits, stands
        int Outside(int apex) => t.pts[apex].curv > 0 ? -1 : 1;
        void FindCorners()
        {
            int win = Math.Max(4, (int)Math.Round(40f / ds));
            for (int i = 0; i < N; i++)
            {
                float k = curvAbs[i]; if (k < 1f / 250f) continue;
                bool peak = true;
                for (int j = -win; j <= win && peak; j++) { if (j == 0) continue; float kj = curvAbs[W(i + j)]; if (kj > k || (kj == k && j < 0)) peak = false; }
                if (peak) corners.Add(i);
            }
            corners.Sort((a, b) => curvAbs[b].CompareTo(curvAbs[a]));
        }
        void Box(int i, int sg, float latFront, float depth, float halfL, K k, float wPer8)
        {
            var (px, pz) = P(i); var (tx, tz) = Tn(i); var (nx, nz) = Nm(i);
            float lc = latFront + depth / 2; float cx = px + nx * sg * lc, cz = pz + nz * sg * lc;
            feet.Add(new Foot { cx = cx, cz = cz, tx = tx, tz = tz, hl = halfL, hd = depth / 2 });
            for (float a = -halfL + 4; a <= halfL; a += 8) Add(cx + tx * a, cz + tz * a, wPer8, k);
        }
        static readonly Dictionary<string, float[][]> StandLayout = new()
        {
            ["spa"] = new[] { new[] { 384f, 2f, 15f, 20f }, new[] { 1180f, 1f, 0f, 22f }, new[] { 6733f, 2f, 18f, 20f }, new[] { 2412f, 2f, 30f, 20f }, new[] { 3818f, 2f, 10f, 18f } },
            ["suzuka"] = new[] { new[] { 5402f, 2f, 18f, 20f }, new[] { 728f, 2f, 30f, 22f }, new[] { 1198f, 2f, 0f, 18f }, new[] { 2905f, 2f, 15f, 18f }, new[] { 3802f, 2f, 10f, 18f } },
            ["spielberg"] = new[] { new[] { 1402f, 2f, 18f, 20f }, new[] { 463f, 2f, 25f, 20f }, new[] { 3994f, 2f, 15f, 20f }, new[] { 2225f, 2f, 15f, 18f }, new[] { 3790f, 2f, 15f, 18f } },
        };
        void Trackside()
        {
            // pit straight extent (Trackside.Build), on the circuit's pit side
            float kS = 1f / 800f; int fwd = 0, back = 0, lim = (int)Math.Round(350f / ds);
            while (fwd < lim && curvAbs[W(fwd)] < kS) fwd++;
            while (back < lim && curvAbs[W(-back)] < kS) back++;
            float s0 = -Math.Max(30f, back * ds * 0.85f), s1 = Math.Max(30f, fwd * ds * 0.85f);
            if (s1 - s0 > 280f) { float c0 = (s0 + s1) * 0.5f; s0 = Math.Max(s0, c0 - 140f); s1 = s0 + 280f; }
            pitSide = def.pitSide != 0 ? def.pitSide : -1;
            pitA = (int)Math.Round(s0 / ds); pitB = (int)Math.Round(s1 / ds);
            if (def.id != "monza") // (Monza: the map's own paddock buildings)
                for (float s = s0; s < s1; s += 8f) Box(W((int)Math.Round((s + 4f) / ds)), pitSide, wall + 0.5f, 27f, 4f, K.Pit, 6f);
            // main stands opposite the pits; then the corner stands (High)
            int ms = -pitSide; bool suz = def.id == "suzuka"; float mhl = 24f; int mrows = suz ? 17 : 13;
            foreach (float at in new[] { 0f, -(2f * mhl + 6f), 2f * mhl + 6f })
                Box(W((int)Math.Round((at - 30f) / ds)), ms, wall + 4.5f, mrows * 0.85f, mhl, K.Stand, 4f);
            var used = new List<int>(); int placedN = 0, cornerN = 5;
            if (StandLayout.TryGetValue(def.id, out var layout))
            {
                int pw = Math.Max(2, (int)Math.Round(60f / ds));
                foreach (var st in layout)
                {
                    if (placedN >= cornerN) break;
                    int si = W((int)Math.Round(st[0] / ds)), apex = si, side;
                    if (st[1] > 1.5f) { for (int j = -pw; j <= pw; j++) if (curvAbs[W(si + j)] > curvAbs[apex]) apex = W(si + j); side = Outside(apex); }
                    else side = st[1] > 0 ? 1 : -1;
                    if (used.Any(u => Gap(u, apex) < (int)Math.Round(200f / ds))) continue;
                    Box(W(apex - (int)Math.Round(st[2] / ds)), side, wall + 4.5f, 8.5f, st[3], K.Stand, 4f); used.Add(apex); placedN++;
                }
            }
            else
            {
                var scored = new List<KeyValuePair<float, int>>();
                foreach (int apex in corners)
                {
                    if (curvAbs[apex] < 1f / 160f) continue;
                    int run = 0; while (run < N / 4 && curvAbs[W(apex - (int)Math.Round(60f / ds) - run)] < 1f / 500f) run++;
                    scored.Add(new(curvAbs[apex] * 100f * (1f + run * ds / 250f), apex));
                }
                scored.Sort((a, b) => b.Key.CompareTo(a.Key));
                foreach (var kv in scored)
                {
                    if (placedN >= cornerN) break;
                    int apex = kv.Value;
                    if (Gap(apex, 0) < (int)Math.Round(180f / ds)) continue;
                    if (used.Any(u => Gap(u, apex) < (int)Math.Round(250f / ds))) continue;
                    Box(W(apex - (int)Math.Round(15f / ds)), Outside(apex), wall + 4.5f, 8.5f, 20f, K.Stand, 4f); used.Add(apex); placedN++;
                }
            }
        }
        bool Blocked(float x, float z, float r)
        {
            foreach (var f in feet)
            {
                float dx = x - f.cx, dz = z - f.cz;
                float a = Math.Abs(dx * f.tx + dz * f.tz), b = Math.Abs(-dx * f.tz + dz * f.tx);
                if (a < f.hl + r && b < f.hd + r) return true;
            }
            return false;
        }
        bool Taken(float x, float z, float r) { foreach (var s in claims) { float dx = x - s.x, dz = z - s.z, rr = s.r + r; if (dx * dx + dz * dz < rr * rr) return true; } return false; }
        bool InPad(float x, float z, float m) => x > padX0 - m && x < padX1 + m && z > padZ0 - m && z < padZ1 + m;

        // ---------------------------------------------------------------- Environment port: Clear field, noise, canopy
        void BuildField()
        {
            var bb = t.Bounds(); float m = 400;
            float x0 = (float)bb.minX - m, z0 = (float)bb.minY - m, w = (float)(bb.maxX - bb.minX) + 2 * m, h = (float)(bb.maxY - bb.minY) + 2 * m;
            fx0 = x0; fz0 = z0; fx1 = x0 + w; fz1 = z0 + h;
            fnx = (int)Math.Ceiling(w / FCell) + 1; fnz = (int)Math.Ceiling(h / FCell) + 1;
            var dd = new float[fnx * fnz]; Array.Fill(dd, 1e6f);
            for (int i = 0; i < N; i++)
            {
                int cx = (int)Math.Round(((float)t.pts[i].x - x0) / FCell), cz = (int)Math.Round(((float)t.pts[i].y - z0) / FCell);
                if (cx >= 0 && cz >= 0 && cx < fnx && cz < fnz) dd[cz * fnx + cx] = 0;
            }
            for (int j = 0; j < fnz; j++) for (int i = 0; i < fnx; i++)
            {
                float x = x0 + i * FCell, z = z0 + j * FCell;
                if (Blocked(x, z, FCell * 0.75f) || InPad(x, z, 0)) dd[j * fnx + i] = 0;
            }
            const float A = FCell, B = FCell * 1.4142f;
            for (int j = 0; j < fnz; j++) for (int i = 0; i < fnx; i++)
            {
                int k = j * fnx + i; float v = dd[k];
                if (i > 0) v = Math.Min(v, dd[k - 1] + A);
                if (j > 0) { v = Math.Min(v, dd[k - fnx] + A); if (i > 0) v = Math.Min(v, dd[k - fnx - 1] + B); if (i < fnx - 1) v = Math.Min(v, dd[k - fnx + 1] + B); }
                dd[k] = v;
            }
            for (int j = fnz - 1; j >= 0; j--) for (int i = fnx - 1; i >= 0; i--)
            {
                int k = j * fnx + i; float v = dd[k];
                if (i < fnx - 1) v = Math.Min(v, dd[k + 1] + A);
                if (j < fnz - 1) { v = Math.Min(v, dd[k + fnx] + A); if (i < fnx - 1) v = Math.Min(v, dd[k + fnx + 1] + B); if (i > 0) v = Math.Min(v, dd[k + fnx - 1] + B); }
                dd[k] = v;
            }
            dist = dd;
        }
        float Clear(float x, float z)
        {
            float gx = (x - fx0) / FCell, gz = (z - fz0) / FCell;
            if (gx < 0 || gz < 0 || gx >= fnx - 1 || gz >= fnz - 1) return 0;
            int i = (int)gx, j = (int)gz; float a = gx - i, b = gz - j; int k = j * fnx + i;
            return (dist[k] * (1 - a) + dist[k + 1] * a) * (1 - b) + (dist[k + fnx] * (1 - a) + dist[k + fnx + 1] * a) * b;
        }
        static float SS(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); } // Mathf.SmoothStep(0, 1, t)
        float Rim(float x, float z) { float ee = Math.Min(Math.Min(x - fx0, fx1 - x), Math.Min(z - fz0, fz1 - z)); return SS(ee / 110f); }
        static float Hash(int x, int y, int s)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + s * 2147483;
                h = (h ^ (int)((uint)h >> 13)) * 1274126177;
                return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296f;
            }
        }
        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        static float Noise(float x, float y, int s)
        {
            int i = (int)Math.Floor(x), j = (int)Math.Floor(y); float a = x - i, b = y - j;
            a = a * a * (3 - 2 * a); b = b * b * (3 - 2 * b);
            return Lerp(Lerp(Hash(i, j, s), Hash(i + 1, j, s), a), Lerp(Hash(i, j + 1, s), Hash(i + 1, j + 1, s), a), b);
        }
        static float Fbm(float x, float y, int s) => (Noise(x, y, s) + 0.5f * Noise(x * 2.03f, y * 2.03f, s + 1) + 0.25f * Noise(x * 4.1f, y * 4.1f, s + 2)) / 1.75f;
        float Canopy(float x, float z)
        {
            if (e.canopy <= 0) return 0;
            float d = Clear(x, z); if (d <= e.canopyAt) return 0;
            float thr = 0.5f + (0.5f - e.cover) * 0.5f;
            float m = SS((Fbm(x / 210f, z / 210f, 91) - thr + 0.05f) / 0.1f) * SS((d - e.canopyAt) / 7f) * Rim(x, z);
            foreach (var c in markClear) { float dx = x - c.x, dz = z - c.z, r = c.r + 30f; m *= SS(((float)Math.Sqrt(dx * dx + dz * dz) - r) / 14f); }
            return m;
        }

        // ---------------------------------------------------------------- landmarks (Environment marks, approximate)
        readonly List<Circle> markClear = new();
        void Marks()
        {
            var bb = t.Bounds(); float cx = (float)(bb.minX + bb.maxX) / 2, cz = (float)(bb.minY + bb.maxY) / 2;
            void Side2(float s, int side, float off, float w, float r)
            {
                int i = W((int)Math.Round(s / ds)); double k = 0; int win = Math.Max(2, (int)Math.Round(40f / ds));
                for (int j = -win; j <= win; j++) k += t.pts[W(i + j)].curv;
                int inside = k > 0 ? 1 : -1, sg = side == 2 ? inside : -inside;
                var (px, pz) = P(i); var (nx, nz) = Nm(i); float x = px + nx * sg * off, z = pz + nz * sg * off;
                for (int q = 0; q < 8 && Clear(x, z) < 26f; q++) { x += nx * sg * 8; z += nz * sg * 8; }
                Add(x, z, w, K.Mark); claims.Add(new Circle { x = x, z = z, r = r }); markClear.Add(new Circle { x = x, z = z, r = r });
            }
            switch (def.id)
            {
                case "austin": Side2(4700, 2, 85, 30, 16); break;   // observation tower
                case "suzuka": Side2(5560, -2, 150, 40, 30); break; // big wheel
                case "silverstone":                                  // the winged pit building, 84 m out on the pit side
                    for (float s = -150; s <= 150; s += 10) { int i = W((int)Math.Round((20 + s) / ds)); var (px, pz) = P(i); var (nx, nz) = Nm(i); Add(px + nx * pitSide * 84, pz + nz * pitSide * 84, 6, K.Mark); }
                    break;
                case "spielberg":                                    // the infield sculpture (centre of mass, roughly)
                {
                    double sx = 0, sy = 0; for (int j = 0; j < N; j++) { sx += t.pts[j].x; sy += t.pts[j].y; }
                    Add((float)(sx / N), (float)(sy / N), 15, K.Mark); claims.Add(new Circle { x = (float)(sx / N), z = (float)(sy / N), r = 14 });
                    break;
                }
                case "zandvoort":                                    // the seaside town's blocks, south (bearing 195)
                {
                    float half = (float)Math.Max(bb.maxX - bb.minX, bb.maxY - bb.minY) * 0.5f, a = 195f * MathF.PI / 180f;
                    float dx = MathF.Sin(a), dz = MathF.Cos(a), tx = dz, tz = -dx, ox = cx + dx * (half * 0.75f + 250), oz = cz + dz * (half * 0.75f + 250);
                    for (int q = 0; q < 12; q++) { float bx = ox + tx * ((q % 4 - 1.5f) * 62f) + dx * ((q / 4) * 70f), bz = oz + tz * ((q % 4 - 1.5f) * 62f) + dz * ((q / 4) * 70f); Add(bx, bz, 8, K.Mark); claims.Add(new Circle { x = bx, z = bz, r = 22 }); }
                    markClear.Add(new Circle { x = ox, z = oz, r = 150 });
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- Environment.Trees port (High; same random stream)
        static int Kind(string s) => s switch { "Spruce" => 1, "Broadleaf" => 2, "Pine" => 3, "Palm" => 4, "Scrub" => 5, _ => 0 };
        void ExistingTrees()
        {
            if (world != null) return; // (Monza: the real map's trees, ExistingWorld)
            var bb = t.Bounds(); float ext = (float)Math.Max(bb.maxX - bb.minX, bb.maxY - bb.minY);
            float dens = (float)def.forest; var rnd = new System.Random(7); int placedN = 0, tries = 0;
            int want = Math.Clamp((int)(((float)t.length * 0.4f * dens + ext / 6) * e.treeMul), 120, 2500);
            float near = e.treeNear + (float)def.barrier, far = e.treeFar;
            int k1 = Kind(e.tree), k2 = Kind(e.tree2);
            while (placedN < want && tries++ < want * 40)
            {
                double x = bb.minX - far * 0.6 + rnd.NextDouble() * (bb.maxX - bb.minX + far * 1.2), y = bb.minY - far * 0.6 + rnd.NextDouble() * (bb.maxY - bb.minY + far * 1.2);
                float fx = (float)x, fz = (float)y;
                if (InPad(fx, fz, 0) || Blocked(fx, fz, 5f) || Taken(fx, fz, 4f)) continue;
                if (e.clump > 0 && Fbm(fx / 170f, fz / 170f, 71) < e.clump) continue;
                if (e.canopy > 0)
                {
                    float cn = Canopy(fx, fz);
                    if (cn > 0.75f || (cn < 0.02f && Clear(fx, fz) > e.canopyAt + 18f && rnd.NextDouble() > 0.22)) continue;
                }
                double dist2 = t.Nearest(x, y).dist;
                if (dist2 < near || dist2 > far) continue;
                bool second = k2 != 0 && rnd.NextDouble() < e.tree2Share;
                int kind = second ? k2 : k1;
                rnd.NextDouble(); // sz
                int cards = dist2 > 300 ? 2 : 3;
                // EmitTree's draws: rotation, phase, (broad-leaf: crown type), leaf tint x2, 4 per extra card
                int draws = 2 + (kind == 2 ? 1 : 0) + 2 + 4 * (cards - 1);
                for (int q = 0; q < draws; q++) rnd.NextDouble();
                Add(fx, fz, kind == 5 ? 0.3f : 1f, kind == 5 ? K.Bush : K.Tree);
                placedN++;
            }
            // the woods themselves (forest-edge bands and the canopy-coloured ground): 12 m cells
            if (e.canopy > 0)
                for (float x = fx0; x < fx1; x += 12f) for (float z = fz0; z < fz1; z += 12f)
                {
                    float c = Clear(x, z); if (c < e.canopyAt || c > 300) continue;
                    if (Canopy(x, z) > 0.5f) Add(x, z, 1f, K.Wood);
                }
        }
        // Monza: the real map (buildings, trees, forest areas)
        void ExistingWorld()
        {
            if (world == null) return;
            for (int b = 0; b < world.bN; b++)
            {
                int o = world.bOff[b], n = world.bCnt[b]; if (n < 3) continue;
                float cx = 0, cz = 0, area = 0;
                for (int k = 0; k < n; k++) { cx += world.bX[o + k]; cz += world.bZ[o + k]; int k2 = (k + 1) % n; area += world.bX[o + k] * world.bZ[o + k2] - world.bX[o + k2] * world.bZ[o + k]; }
                cx /= n; cz /= n; area = Math.Abs(area) * 0.5f;
                Add(cx, cz, Math.Clamp(area / 100f, 0.5f, 10f), K.Building);
                claims.Add(new Circle { x = cx, z = cz, r = (float)Math.Sqrt(area) * 0.6f + 3f });
            }
            for (int i = 0; i < world.tN; i++) Add(world.tX[i], world.tZ[i], 1f, K.Tree);
            // forest polygons (kind 1): 12 m cells inside them, within 300 m of the track
            for (int a = 0; a < world.aN; a++)
            {
                if (world.aKind[a] != 1) continue;
                int o = world.aOff[a], n = world.aCnt[a];
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                for (int k = 0; k < n; k++) { x0 = Math.Min(x0, world.aX[o + k]); x1 = Math.Max(x1, world.aX[o + k]); z0 = Math.Min(z0, world.aZ[o + k]); z1 = Math.Max(z1, world.aZ[o + k]); }
                for (float x = (float)Math.Floor(x0 / 12) * 12; x <= x1; x += 12) for (float z = (float)Math.Floor(z0 / 12) * 12; z <= z1; z += 12)
                {
                    if (Clear(x, z) > 300) continue;
                    bool inside = false;
                    for (int k = 0, j = n - 1; k < n; j = k++)
                    {
                        float xi = world.aX[o + k], zi = world.aZ[o + k], xj = world.aX[o + j], zj = world.aZ[o + j];
                        if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
                    }
                    if (inside) Add(x, z, 1f, K.Wood);
                }
            }
        }

        // ---------------------------------------------------------------- density
        Obj Locate(Obj o)
        {
            var nr = t.Nearest(o.x, o.z);
            o.idx = nr.idx; o.side = nr.lateral >= 0 ? 1 : -1; o.beyond = (float)Math.Abs(nr.lateral) - wall;
            return o;
        }
        IEnumerable<Obj> FillObjs()
        {
            foreach (var tr in trees) yield return Locate(new Obj { x = tr.x, z = tr.z, w = tr.h < 5 ? 0.3f : 1f, k = K.AutoTree });
            foreach (var b in blds) yield return Locate(new Obj { x = b.x, z = b.z, w = Math.Clamp(b.w * b.d / 100f, 0.5f, 10f), k = K.AutoBld });
            foreach (var m in mdls) yield return Locate(new Obj { x = m.x, z = m.z, w = ModelWeight(m.name), k = K.Model });
        }
        static float ModelWeight(string n) => n switch
        {
            "grandstand_covered" or "grandstand_covered_end" => 10, "warehouse_shed" => 10, "media_centre" => 8, "hospitality_unit" => 5,
            "grandstand_bleacher" => 4, "medical_centre" => 3, "tv_camera_tower" => 2, _ => 1.5f
        };
        public const float NearMin = 8f, FarMin = 10f, NearTo = 90f, FarTo = 250f;
        float[,] nearS, farS, baseN, baseF; int step;
        void Score(IEnumerable<Obj> objs, float[,] nS, float[,] fS)
        {
            int ns = nS.GetLength(1), win = (int)Math.Round(60f / ds);
            foreach (var o in objs)
            {
                if (o.beyond < 3f || o.beyond > FarTo) continue;
                int si = o.side > 0 ? 0 : 1;
                for (int q = (o.idx - win) / step - 1; q <= (o.idx + win) / step + 1; q++)
                {
                    int qq = ((q % ns) + ns) % ns;
                    if (Gap(qq * step, o.idx) > win) continue;
                    if (o.beyond < NearTo) nS[si, qq] += o.w; else fS[si, qq] += o.w * 0.5f;
                }
            }
        }
        // existing objects (once) + the fill so far
        void Score()
        {
            step = Math.Max(1, (int)Math.Round(25f / ds)); int ns = N / step;
            if (baseN == null) { baseN = new float[2, ns]; baseF = new float[2, ns]; Score(existing, baseN, baseF); }
            nearS = (float[,])baseN.Clone(); farS = (float[,])baseF.Clone();
            Score(FillObjs(), nearS, farS);
        }
        public sealed class Result { public float emptyNear, emptyBoth, lap, insideLen; public List<string> lines = new(); }
        public Result Analyse(bool print, string label)
        {
            Score();
            int ns = nearS.GetLength(1); var r = new Result { lap = (float)t.length };
            if (print) Console.WriteLine($"== {def.id} ({label}): lap {t.length:F0} m, barrier {wall:F1} m from the centre line; empty = near < {NearMin} (3-{NearTo} m beyond the barrier) / far < {FarMin} ({NearTo}-{FarTo} m)");
            if (print) Console.WriteLine("   existing (estimated): " + string.Join(", ", existing.GroupBy(o => o.k).Select(g => g.Key + " " + g.Count())) + $"; fill: {mdls.Count} models, {blds.Count} buildings, {trees.Count} trees");
            for (int si = 0; si < 2; si++)
            {
                int q = 0;
                while (q < ns)
                {
                    if (nearS[si, q] >= NearMin || PitRun(q * step, si == 0 ? 1 : -1)) { q++; continue; }
                    int a = q; float sumF = 0, sumN = 0; bool far = true;
                    while (q < ns && nearS[si, q] < NearMin && !PitRun(q * step, si == 0 ? 1 : -1)) { sumN += nearS[si, q]; sumF += farS[si, q]; if (farS[si, q] >= FarMin) far = false; q++; }
                    float len = (q - a) * step * ds; if (len < 75f) continue;
                    int ins = 0; for (int u = a; u < q; u++) if (CurvSide(u * step, si == 0 ? 1 : -1, 40f) < -1f / 300f) ins++;
                    bool insideBend = ins * 2 > q - a;
                    r.emptyNear += len; if (far) r.emptyBoth += len;
                    r.lines.Add($"   {(si == 0 ? "left " : "right")} s {a * step * ds,6:F0}-{q * step * ds,6:F0} m ({len,4:F0} m)  near {sumN / (q - a),5:F1}  far {sumF / (q - a),5:F1}{(far ? "  EMPTY (near + far)" : "  thin near the track")}{(insideBend ? "  (inside of a bend: kept open for the sightline)" : "")}");
                    if (insideBend) r.insideLen += len;
                }
            }
            if (print) { foreach (var l in r.lines) Console.WriteLine(l); Console.WriteLine($"   => {r.emptyNear:F0} m of {2 * t.length:F0} m of track sides thin near the track ({r.insideLen:F0} m of it on the inside of bends), {r.emptyBoth:F0} m empty near and far"); }
            return r;
        }
        bool PitRun(int i, int sg)
        {
            int rel = i > N / 2 ? i - N : i;
            return sg == pitSide && rel >= pitA - (int)(40 / ds) && rel <= pitB + (int)(40 / ds);
        }

        // ---------------------------------------------------------------- the fill
        // is a footprint (centre, half sizes along the unit axes ux / across) free?
        bool Free(float cx, float cz, float ux, float uz, float hx, float hz, float clearBeyond, float margin = 4f)
        {
            float vx = -uz, vz = ux;
            for (int q = 0; q < 5; q++)
            {
                float px = cx, pz = cz;
                if (q < 4) { px += ux * (q % 2 == 0 ? hx : -hx) + vx * (q < 2 ? hz : -hz); pz += uz * (q % 2 == 0 ? hx : -hx) + vz * (q < 2 ? hz : -hz); }
                float need = wall + clearBeyond + (q == 4 ? Math.Min(hx, hz) : 0f);
                if (t.Nearest(px, pz).dist < need) return false;
                if (InPad(px, pz, 25f)) return false;
            }
            float r = (float)Math.Sqrt(hx * hx + hz * hz);
            if (Blocked(cx, cz, Math.Max(hx, hz) + 6f) || Taken(cx, cz, r * 0.8f)) return false;
            foreach (var c in placed) { float dx = cx - c.x, dz = cz - c.z, rr = c.r + r + margin; if (dx * dx + dz * dz < rr * rr) return false; }
            return true;
        }
        // the inside of a bend near the track stays open (the driver looks across it to the exit): nothing within 70 m
        // beyond the barrier on the inside of a tight bend (radius < 180 m), nothing taller than 5 m within 45 m on the
        // inside of a gentle one (< 300 m). Checked against the nearest part of the track (other legs too).
        bool SightOk(float x, float z, float height)
        {
            var nr = t.Nearest(x, z); float beyond = (float)Math.Abs(nr.lateral) - wall;
            if (beyond > 70f) return true;
            float k = CurvSide(nr.idx, nr.lateral >= 0 ? 1 : -1, 40f);
            if (k < -1f / 180f) return false;
            return !(k < -1f / 300f && beyond < 45f && height > 5f);
        }
        float CurvSide(int i, int sg, float reach)
        {
            // + = this side is the outside of the bend round i, - = the inside; magnitude = the sharpest curvature
            int w = (int)Math.Round(reach / ds); double sum = 0; float kmax = 0;
            for (int j = -w; j <= w; j++) { sum += t.pts[W(i + j)].curv; kmax = Math.Max(kmax, curvAbs[W(i + j)]); }
            bool inside = sum * sg > 0; // left-hand bend (curv > 0): the inside is on the left (+1)
            return inside ? -kmax : kmax;
        }
        float R() => (float)rng.NextDouble();
        float Rr(float a, float b) => a + (b - a) * (float)rng.NextDouble();
        string Pick(Dictionary<string, float> w)
        {
            float sum = w.Values.Sum(), x = R() * sum;
            foreach (var kv in w) { x -= kv.Value; if (x <= 0) return kv.Key; }
            return w.Keys.Last();
        }
        int standGroups, mediaN; const int MaxModels = 150, MaxBlds = 45; int MaxTrees => e.maxTrees;
        readonly Dictionary<string, int> used = new();
        static readonly Dictionary<string, int> Caps = new() { ["hosp"] = 14, ["services"] = 12, ["bleachers"] = 12, ["media"] = 1, ["industrial"] = 8, ["hangar"] = 6, ["houses"] = 8, ["farm"] = 7, ["flats"] = 3 };
        string PickCapped(Dictionary<string, float> w, string fallback)
        {
            foreach (var k in w.Keys.ToList()) if (Caps.TryGetValue(k, out int cap) && used.GetValueOrDefault(k) >= cap) w.Remove(k);
            string kind = w.Count > 0 ? Pick(w) : fallback;
            used[kind] = used.GetValueOrDefault(kind) + 1; return kind;
        }

        // a model with its front (+z) to the track at track point i, side sg: front edge latFront m from the centre line,
        // along m down the track; false if it does not fit
        bool Model(string name, int i, int sg, float latFront, float along, float clearBeyond = 15f, bool faceTrack = true, float scale = 1f)
        {
            if (mdls.Count >= MaxModels) return false;
            var m = Lm.M[name];
            var (px, pz) = P(i); var (tx, tz) = Tn(i); var (nx, nz) = Nm(i);
            float Dx = nx * sg, Dz = nz * sg;
            float Fx = faceTrack ? -Dx : tx, Fz = faceTrack ? -Dz : tz; // the model's +z
            float Rx = Fz, Rz = -Fx;                                        // its +x (Euler(0, yaw, 0) * right)
            float zf = (m.minZ + m.sz) * scale, latO = latFront + (faceTrack ? zf : (m.sx * 0.5f) * scale);
            float ox = px + Dx * latO + tx * along, oz = pz + Dz * latO + tz * along;
            // footprint centre and half sizes
            float lcx = (m.minX + m.sx * 0.5f) * scale, lcz = (m.minZ + m.sz * 0.5f) * scale;
            float cx = ox + Rx * lcx + Fx * lcz, cz = oz + Rz * lcx + Fz * lcz;
            if (!Free(cx, cz, Rx, Rz, m.sx * 0.5f * scale, m.sz * 0.5f * scale, clearBeyond) || !SightOk(cx, cz, m.sy * scale)) return false;
            float yaw = MathF.Atan2(Fx, Fz) * 180f / MathF.PI;
            mdls.Add(new OutMdl { name = name, x = ox, z = oz, y = -0.05f, yaw = yaw, scale = scale });
            placed.Add(new Circle { x = cx, z = cz, r = (float)Math.Sqrt(m.sx * m.sx + m.sz * m.sz) * 0.5f * scale });
            return true;
        }
        int nearTrees; bool nearPass;
        bool Tree(float x, float z, float h)
        {
            if (trees.Count >= MaxTrees || (nearPass && nearTrees >= MaxTrees * 2 / 5)) return false; // (tree lines near the track: 40 % of the trees at most)
            if (t.Nearest(x, z).dist < wall + 15f || InPad(x, z, 20f) || Blocked(x, z, 4f) || Taken(x, z, 3f) || !SightOk(x, z, h)) return false;
            foreach (var c in placed) { float dx = x - c.x, dz = z - c.z; if (dx * dx + dz * dz < (c.r + 2.5f) * (c.r + 2.5f)) return false; }
            foreach (var o in trees) { float dx = x - o.x, dz = z - o.z; if (dx * dx + dz * dz < 25f) return false; }
            trees.Add(new OutTree { x = x, z = z, h = h }); if (nearPass) nearTrees++;
            return true;
        }
        bool Bld(float x, float z, float w, float dd, float h, float yawDeg, int kind, int roof, string col = null)
        {
            if (blds.Count >= MaxBlds || !e.buildings) return false;
            float a = yawDeg * MathF.PI / 180f, ux = MathF.Cos(a), uz = -MathF.Sin(a); // Euler(0, yaw, 0) * right
            if (!Free(x, z, ux, uz, w / 2, dd / 2, 15f, 6f) || !SightOk(x, z, h)) return false;
            var b = new OutBld { x = x, z = z, w = w, d = dd, h = h, yaw = yawDeg, kind = kind, roof = roof };
            if (col != null) { var p = col.Split(','); b.r = float.Parse(p[0], Inv); b.g = float.Parse(p[1], Inv); b.b = float.Parse(p[2], Inv); b.a = 1; }
            blds.Add(b); placed.Add(new Circle { x = x, z = z, r = (float)Math.Sqrt(w * w + dd * dd) * 0.5f });
            return true;
        }
        float TreeH() => Rr(e.treeH0, e.treeH1);
        // a point at track index i, side sg, lat m from the centre line, along m down the track
        (float x, float z) At(int i, int sg, float lat, float along)
        {
            var (px, pz) = P(i); var (tx, tz) = Tn(i); var (nx, nz) = Nm(i);
            return (px + nx * sg * lat + tx * along, pz + nz * sg * lat + tz * along);
        }
        float TrackYaw(int i, int sg) { var (nx, nz) = Nm(i); float Fx = -nx * sg, Fz = -nz * sg; return MathF.Atan2(Fx, Fz) * 180f / MathF.PI; }

        // ---- near clusters (3-45 m beyond the 15 m clearance)
        int NearCluster(int i, int sg, float kSide, bool lowOnly)
        {
            int before = mdls.Count + trees.Count;
            float f0 = wall + 15f;
            // the outside of a big braking corner: a covered grandstand row and a TV tower (few per circuit)
            if (!lowOnly && kSide > 1f / 140f && standGroups < 3 && !existing.Any(o => o.k == K.Stand && Gap(o.idx, i) < (int)(220 / ds) && o.side == sg))
            {
                int n = R() < 0.5f ? 2 : 3, ok = 0;
                for (int u = 0; u < n; u++)
                {
                    float al = (u - (n - 1) * 0.5f) * 20.2f;
                    if (Model(u == n - 1 ? "grandstand_covered_end" : "grandstand_covered", i, sg, f0 + 1f, al)) ok++;
                }
                if (ok > 0) { standGroups++; Model("tv_camera_tower", i, sg, f0 + 0.5f, (n * 0.5f) * 20.2f + 6f); return mdls.Count + trees.Count - before; }
            }
            var w = new Dictionary<string, float>(e.near);
            if (kSide > 1f / 300f) w.Remove("hosp"); // corners: smaller things
            if (mediaN == 0 && kSide < 1f / 500f && !lowOnly) w["media"] = 1.5f;
            if (lowOnly) { w.Remove("hosp"); w.Remove("treeline"); if (w.Count == 0) return 0; } // (the inside of a gentle bend: kiosks and low stands only)
            string kind = PickCapped(w, "treeline");
            switch (kind)
            {
                case "media":
                    if (Model("media_centre", i, sg, f0 + Rr(2, 8), 0)) { mediaN++; Model("medical_centre", i, sg, f0 + 4, 26); }
                    break;
                case "hosp":
                {
                    int n = 2 + (int)(R() * 3); float lf = f0 + Rr(2, 8);
                    for (int u = 0; u < n; u++) Model("hospitality_unit", i, sg, lf, (u - (n - 1) * 0.5f) * 17.5f);
                    for (int u = 0; u < 3; u++) { var (x, z) = At(i, sg, lf + 16f + Rr(0, 6), (u - 1) * 22f + Rr(-4, 4)); Tree(x, z, TreeH()); }
                    break;
                }
                case "services":
                {
                    string[] set = { "toilet_block", "food_kiosk", "food_kiosk", "merch_stand", "merch_stand", "medical_centre", "food_kiosk" };
                    int n = 4 + (int)(R() * 3); float al = -n * 4.5f, lf = f0 + Rr(1, 5);
                    for (int u = 0; u < n; u++) { var nm = set[(int)(R() * set.Length)]; if (Model(nm, i, sg, lf, al)) al += Lm.M[nm].sx + 3.5f; else al += 4f; }
                    if (!lowOnly) for (int u = 0; u < 4; u++) { var (x, z) = At(i, sg, lf + 12f + Rr(0, 10), Rr(-30, 30)); Tree(x, z, TreeH()); }
                    break;
                }
                case "bleachers":
                {
                    int n = 2 + (int)(R() * 3);
                    for (int u = 0; u < n; u++) Model("grandstand_bleacher", i, sg, f0 + 0.5f, (u - (n - 1) * 0.5f) * 15.4f);
                    if (!lowOnly && R() < 0.5f) Model("tv_camera_tower", i, sg, f0, (n * 0.5f) * 15.4f + 5f);
                    break;
                }
                default: // treeline: one or two rows along the track
                {
                    int n = 7 + (int)(R() * 8); float lat = f0 + Rr(2, 8), sp = Rr(8.5f, 12f);
                    for (int u = 0; u < n; u++)
                    {
                        var (x, z) = At(i, sg, lat + Rr(-1.5f, 1.5f), (u - (n - 1) * 0.5f) * sp + Rr(-2, 2)); Tree(x, z, TreeH());
                        if (R() < 0.35f) { var (x2, z2) = At(i, sg, lat + Rr(8, 12), (u - (n - 1) * 0.5f) * sp + sp * 0.5f); Tree(x2, z2, TreeH()); }
                    }
                    break;
                }
            }
            return mdls.Count + trees.Count - before;
        }
        // ---- mid / far clusters
        int FarCluster(int i, int sg, bool mid)
        {
            int before = mdls.Count + trees.Count + blds.Count;
            var w = new Dictionary<string, float>(mid ? e.mid : e.far);
            if (!e.buildings) { w.Remove("houses"); w.Remove("farm"); w.Remove("flats"); }
            if (w.Count == 0) return 0;
            string kind = PickCapped(w, "woodland");
            float lat = mid ? wall + Rr(60, 120) : wall + Rr(140, 215), al = Rr(-25, 25);
            var (cx, cz) = At(i, sg, lat, al); float yaw = TrackYaw(i, sg);
            switch (kind)
            {
                case "industrial":
                case "hangar":
                {
                    float sc = kind == "hangar" ? 1.15f : 1f;
                    int n = R() < 0.55f ? 1 : 2; float lf = lat - 15f;
                    for (int u = 0; u < n; u++) Model("warehouse_shed", i, sg, lf + (u == 1 ? 34f : 0f), al + (u == 1 ? Rr(-15, 15) : 0f), 15f, true, sc);
                    if (R() < 0.6f) { var (bx, bz) = At(i, sg, lf + 12f, al + 42f); Bld(bx, bz, Rr(14, 20), Rr(9, 12), Rr(5, 7), yaw + 90f, 3, 0, "0.72,0.73,0.74"); }
                    for (int u = 0; u < 5; u++) { var (x, z) = At(i, sg, lf + Rr(-4, 40), al + (u % 2 == 0 ? -1 : 1) * Rr(32, 45)); Tree(x, z, TreeH()); }
                    break;
                }
                case "houses":
                {
                    int n = 2 + (int)(R() * 3); string col = e.walls != null ? e.walls[(int)(R() * e.walls.Length)] : null;
                    for (int u = 0; u < n; u++)
                    {
                        var (bx, bz) = At(i, sg, lat + Rr(-12, 12), al + (u - (n - 1) * 0.5f) * Rr(17, 24));
                        Bld(bx, bz, Rr(9, 12.5f), Rr(7.5f, 9.5f), Rr(6, 7.8f), yaw + Rr(-8, 8) + (R() < 0.3f ? 90 : 0), 1, 1, e.walls != null ? e.walls[(int)(R() * e.walls.Length)] : col);
                    }
                    for (int u = 0; u < 6; u++) { var (x, z) = At(i, sg, lat + Rr(-20, 25), al + Rr(-45, 45)); Tree(x, z, TreeH()); }
                    break;
                }
                case "farm":
                {
                    string[] farmWalls = { "0.86,0.84,0.79", "0.62,0.58,0.52", "0.78,0.72,0.62" };
                    float fy = yaw + Rr(-20, 20);
                    Bld(cx, cz, Rr(12, 15), Rr(8.5f, 10), Rr(7, 8), fy, 1, 1, farmWalls[(int)(R() * 3)]);
                    var (bx, bz) = At(i, sg, lat + Rr(14, 20), al + Rr(14, 22));
                    Bld(bx, bz, Rr(20, 27), Rr(11, 13), Rr(7.5f, 9.5f), fy + 90f, 7, 1, farmWalls[(int)(R() * 3)]);
                    if (R() < 0.6f) { var (sx, sz) = At(i, sg, lat + Rr(-6, 4), al - Rr(18, 24)); Bld(sx, sz, Rr(12, 16), Rr(7, 9), Rr(4.5f, 5.5f), fy, 7, 0, "0.55,0.57,0.58"); }
                    for (int u = 0; u < 7; u++) { var (x, z) = At(i, sg, lat + Rr(-25, 35), al + Rr(-40, 45)); Tree(x, z, TreeH()); }
                    break;
                }
                case "flats":
                {
                    int n = 1 + (int)(R() * 3);
                    for (int u = 0; u < n; u++)
                    {
                        var (bx, bz) = At(i, sg, lat + Rr(-8, 8), al + (u - (n - 1) * 0.5f) * 36f);
                        Bld(bx, bz, Rr(24, 32), Rr(12, 14), 3.2f * (5 + (int)(R() * 4)), yaw, 8, 0, e.walls != null ? e.walls[(int)(R() * e.walls.Length)] : null);
                    }
                    break;
                }
                default: // woodland: a copse of trees round the spot
                {
                    int n = mid ? 14 + (int)(R() * 14) : 20 + (int)(R() * 18); float rad = mid ? Rr(20, 32) : Rr(28, 42);
                    for (int u = 0, tries = 0; u < n && tries < n * 6; tries++)
                    {
                        float a = R() * MathF.PI * 2, rr = rad * MathF.Sqrt(R());
                        if (Tree(cx + MathF.Cos(a) * rr, cz + MathF.Sin(a) * rr, TreeH() * Rr(0.85f, 1.15f))) u++;
                    }
                    break;
                }
            }
            return mdls.Count + trees.Count + blds.Count - before;
        }

        public void Generate()
        {
            trees.Clear(); blds.Clear(); mdls.Clear(); placed.Clear(); used.Clear(); standGroups = 0; mediaN = 0; shotSpots.Clear();
            int slot = Math.Max(1, (int)Math.Round(80f / ds));
            // the near band first, then mid / far; in each pass the emptiest slots first (so the caps go where it is
            // emptiest), each slot re-scored as the fill grows
            nearTrees = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                nearPass = pass == 0; Score();
                var cand = new List<(int i, int sg, float score)>();
                for (int i0 = 0; i0 < N; i0 += slot)
                    for (int sg = 1; sg >= -1; sg -= 2)
                    {
                        int i = W(i0 + (sg < 0 ? slot / 2 : 0));
                        if (PitRun(i, sg)) continue;
                        int q = Math.Min(nearS.GetLength(1) - 1, i / step), si = sg > 0 ? 0 : 1;
                        cand.Add((i, sg, pass == 0 ? nearS[si, q] : farS[si, q] + nearS[si, q] * 0.25f));
                    }
                cand.Sort((a, b) => a.score != b.score ? a.score.CompareTo(b.score) : a.i.CompareTo(b.i));
                foreach (var (i, sg, _) in cand)
                {
                    Score();
                    int q = Math.Min(nearS.GetLength(1) - 1, i / step), si = sg > 0 ? 0 : 1;
                    float kSide = CurvSide(i, sg, 40f);
                    bool inside = kSide < -1f / 300f; // the inside of a bend: keep the sightline open near the track
                    if (pass == 0)
                    {
                        // (gentle bends, radius 180-300 m: only low things on the inside; tighter: nothing)
                        if (nearS[si, q] >= NearMin || kSide < -1f / 180f) continue;
                        int got = NearCluster(i, sg, kSide, inside);
                        if (got > 0) shotSpots.Add((i, sg, got));
                    }
                    else
                    {
                        if (farS[si, q] >= FarMin) continue;
                        if (!inside && nearS[si, q] < NearMin * 2.5f) FarCluster(i, sg, true);
                        Score();
                        if (farS[si, q] < FarMin) FarCluster(i, sg, false);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- output
        public void LoadFill(string json)
        {
            // minimal reader for our own output (one record per line)
            foreach (var line in json.Split('\n'))
            {
                string L = line.Trim();
                float F(string key) { int a = L.IndexOf("\"" + key + "\":"); if (a < 0) return 0; a += key.Length + 3; int b = a; while (b < L.Length && "-0123456789.eE".IndexOf(L[b]) >= 0) b++; return float.Parse(L.Substring(a, b - a), Inv); }
                if (L.StartsWith("{\"name\"")) { int a = L.IndexOf(':') + 2, b = L.IndexOf('"', a); mdls.Add(new OutMdl { name = L.Substring(a, b - a), x = F("x"), z = F("z") }); }
                else if (L.StartsWith("{\"x\"") && L.Contains("\"kind\"")) blds.Add(new OutBld { x = F("x"), z = F("z"), w = F("w"), d = F("d"), h = F("h") });
                else if (L.StartsWith("{\"x\"")) trees.Add(new OutTree { x = F("x"), z = F("z"), h = F("h") });
            }
        }
        // top-down preview data (SCENERY_DUMP=<dir>): track, barrier line, trackside boxes, existing objects, fill footprints
        public void Dump(string stem)
        {
            using (var w = new StreamWriter(stem + "_track.csv")) { w.WriteLine(wall.ToString(Inv)); for (int i = 0; i < N; i++) { var p = t.pts[i]; w.WriteLine($"{N2((float)p.x)},{N2((float)p.y)},{N2((float)p.nx)},{N2((float)p.ny)}"); } }
            using (var w = new StreamWriter(stem + "_feet.csv")) foreach (var f in feet) w.WriteLine($"{N2(f.cx)},{N2(f.cz)},{N2(f.tx)},{N2(f.tz)},{N2(f.hl)},{N2(f.hd)}");
            using (var w = new StreamWriter(stem + "_exist.csv")) foreach (var o in existing) if (o.k != K.Wood || (o.x + o.z) % 24 < 12) w.WriteLine($"{N2(o.x)},{N2(o.z)},{o.k}");
            using (var w = new StreamWriter(stem + "_fill.csv"))
            {
                foreach (var m in mdls)
                {
                    var lm = Lm.M[m.name]; float a = m.yaw * MathF.PI / 180f, rx = MathF.Cos(a), rz = -MathF.Sin(a), fx = MathF.Sin(a), fz = MathF.Cos(a), sc = m.scale;
                    float lcx = (lm.minX + lm.sx * 0.5f) * sc, lcz = (lm.minZ + lm.sz * 0.5f) * sc;
                    w.WriteLine($"M,{N2(m.x + rx * lcx + fx * lcz)},{N2(m.z + rz * lcx + fz * lcz)},{N2(rx)},{N2(rz)},{N2(lm.sx * 0.5f * sc)},{N2(lm.sz * 0.5f * sc)},{m.name}");
                }
                foreach (var b in blds) { float a = b.yaw * MathF.PI / 180f; w.WriteLine($"B,{N2(b.x)},{N2(b.z)},{N2(MathF.Cos(a))},{N2(-MathF.Sin(a))},{N2(b.w / 2)},{N2(b.d / 2)},k{b.kind}"); }
                foreach (var tr in trees) w.WriteLine($"T,{N2(tr.x)},{N2(tr.z)},0,0,0,0,{N2(tr.h)}");
            }
        }
        static string N2(float v) => v.ToString("0.##", Inv);
        public string Json()
        {
            var sb = new StringBuilder();
            sb.Append("{\"version\":1,\"track\":\"").Append(def.id).Append("\",\"remove\":[],\n\"trees\":[\n");
            for (int k = 0; k < trees.Count; k++) { var r = trees[k]; sb.Append("{\"x\":").Append(N2(r.x)).Append(",\"z\":").Append(N2(r.z)).Append(",\"h\":").Append(N2(r.h)).Append('}').Append(k + 1 < trees.Count ? ",\n" : "\n"); }
            sb.Append("],\n\"buildings\":[\n");
            for (int k = 0; k < blds.Count; k++)
            {
                var b = blds[k];
                sb.Append("{\"x\":").Append(N2(b.x)).Append(",\"z\":").Append(N2(b.z)).Append(",\"w\":").Append(N2(b.w)).Append(",\"d\":").Append(N2(b.d)).Append(",\"h\":").Append(N2(b.h))
                  .Append(",\"yaw\":").Append(N2(b.yaw)).Append(",\"kind\":").Append(b.kind).Append(",\"roof\":").Append(b.roof)
                  .Append(",\"colour\":{\"r\":").Append(N2(b.r)).Append(",\"g\":").Append(N2(b.g)).Append(",\"b\":").Append(N2(b.b)).Append(",\"a\":").Append(N2(b.a)).Append("}}")
                  .Append(k + 1 < blds.Count ? ",\n" : "\n");
            }
            sb.Append("],\n\"models\":[\n");
            for (int k = 0; k < mdls.Count; k++)
            {
                var m = mdls[k];
                sb.Append("{\"name\":\"").Append(m.name).Append("\",\"x\":").Append(N2(m.x)).Append(",\"z\":").Append(N2(m.z)).Append(",\"y\":").Append(N2(m.y))
                  .Append(",\"yaw\":").Append(N2(m.yaw)).Append(",\"scale\":").Append(N2(m.scale)).Append(",\"alongTrack\":false}").Append(k + 1 < mdls.Count ? ",\n" : "\n");
            }
            sb.Append("]}\n");
            return sb.ToString();
        }
        // triangles as the game builds them: landmark meshes; trees 3 cards (6 tris; 2 on Low); buildings: gabled house
        // 16, shed 20, block of flats 34
        public string Summary(Result before, Result after)
        {
            int lmTris = mdls.Sum(m => Lm.M[m.name].tris);
            var mats = new HashSet<string>(); foreach (var m in mdls) mats.UnionWith(Lm.M[m.name].mats);
            int bTris = blds.Sum(b => b.roof > 0 ? 16 : b.kind == 3 || b.kind == 7 ? 20 : 34), tTris = trees.Count * 6;
            bool flats = blds.Any(b => b.roof == 0 && b.kind != 3 && b.kind != 7);
            int draws = mats.Count + (flats && def.id != "zandvoort" ? 1 : 0); // landmark materials (+ EnvBlocks where the circuit had none); trees / EnvProps: existing batches
            var byName = mdls.GroupBy(m => m.name).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count());
            var sb = new StringBuilder();
            sb.AppendLine($"{def.id}: thin sides {before.emptyNear:F0} -> {after.emptyNear:F0} m ({after.insideLen:F0} m of it inside bends, kept open) (empty near+far {before.emptyBoth:F0} -> {after.emptyBoth:F0} m) of {2 * t.length:F0} m");
            sb.AppendLine($"   {mdls.Count} models ({string.Join(", ", byName)}), {blds.Count} buildings, {trees.Count} trees");
            sb.AppendLine($"   ~{lmTris + bTris + tTris} tris (models {lmTris}, buildings {bTris}, trees {tTris}); +{draws} draw calls ({string.Join(" ", mats.OrderBy(x => x))})");
            // shot spots: the biggest near clusters, the camera 70 m before them (chase looks ahead)
            var spots = shotSpots.OrderByDescending(s => s.w).Take(4).OrderBy(s => s.idx).Select(s => ((int)Math.Round(((s.idx * ds - 70) % t.length + t.length) % t.length / 10) * 10, s.side));
            sb.AppendLine($"   shots: --track {def.id} --s {string.Join(",", spots.Select(s => s.Item1))}   (fill on the {string.Join("/", spots.Select(s => s.side > 0 ? "left" : "right"))})");
            return sb.ToString();
        }
    }
}
