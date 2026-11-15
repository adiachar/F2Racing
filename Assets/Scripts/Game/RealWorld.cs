// RealWorld.cs — the real surroundings of a circuit, built from Resources/World/<Name>.bytes (WorldData.cs: buildings,
// land use, roads and trees from OpenStreetMap / Overture Maps, and a terrain height grid). Without a file the circuit
// keeps its procedural scenery (Environment.cs) exactly as before.
// What it builds, in world coordinates, lit by LightBake like the rest of the world:
//  * terrain: the real heights on a regular grid (8 m; 10 m on Medium, 12 m on Low), blended into the circuit's own height field near
//    the track so it never rises through the road, the verges, the stands or the test pad. Cut into 512 m chunks that
//    get coarser away from the track (2, 4, 8 cells per step), with skirts where neighbouring chunks differ.
//  * ground colour: the land-use polygons painted into one colour map (Baked.shader F2_LAND: the map times a
//    world-space detail texture: grass, paving, rough ground or tree canopy, picked per texel).
//  * woods: inside forest polygons the terrain is raised into a canopy with a crown texture; card trees
//    (Environment's atlas, Foliage.shader) stand along the wood edges near the track, in parks and gardens, and at
//    the mapped single trees.
//  * water: lakes, rivers and sea as flat surfaces with the harbour's look, the ground carved below them.
//  * roads, paths and railways: flat ribbons on the terrain (one atlas), kept level over water like a bridge.
//  * buildings: extruded footprints with flat roofs and parapets, gabled, hipped, pyramidal or domed roofs; facades
//    from a 4 x 4 tile atlas by building kind; colours per circuit (Monaco cream and ochre, Zandvoort brick ...).
// Cost: one mesh per material per 1 km block (land, water, roads, buildings, trees), static batched; buildings,
// roads and trees switched off by distance with LODGroups (no script per frame). Budgets per graphics level keep the
// vertex count (and so the light bake) small; see Budget().
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public static class RealWorld
{
    public const string Attribution = "Map data © OpenStreetMap contributors, Overture Maps Foundation";

    public static bool Active;            // a World file is loaded for the circuit being built
    public static bool HasBuildings, HasVegetation, HasWaterAreas, HasTerrain;
    public static bool Ready;             // ground heights known (GroundY), between Prepare and the end of Build
    public static Rect Region;            // area covered by the real terrain (world x, z); the flat sheet leaves it out
    public static float Reach;            // from the circuit's centre to the nearest edge of the region (m)
    public static float SheetLevel;       // height of the flat ground sheet round the region
    public static float LoadMs;           // time spent here during the last load, for the performance notes
    public static int Vertices, BuildingCount, TreeCount;
    public static string Timing = "";     // ms per stage of the last load
    static System.Diagnostics.Stopwatch stage = new System.Diagnostics.Stopwatch();
    static void Mark(string what) { Timing += what + " " + stage.Elapsed.TotalMilliseconds.ToString("F0") + "  "; stage.Restart(); }

    const float SeaBed = -4f;
    static WorldData D; static Track trk; static EnvDef env; static Style sty; static int gfx;
    static float edgeLevel, c0, ramp, wallLat, padLevel; static bool street;
    static float fx0, fz0, fx1, fz1; static float bbX0, bbZ0, bbX1, bbZ1; // distance field rectangle, track bounds
    // base grid of heights: point (i, j) at (rx0 + i B, rz0 + j B)
    static float B; static int gnx, gnz; static float rx0, rz0, rx1, rz1;
    static float[] gGround, gTop, gW, gCan; // ground, ground + canopy, 0 on the circuit's ground .. 1 real terrain, canopy 0..1
    // land grid (the colour map): ids of the land-use polygon covering each texel, and flags (1 road, 2 building)
    static int lnx, lnz; static float lcx, lcz; static ushort[] lid; static byte[] lflag;
    static float[] waterL; // water level per land-use polygon (NaN: not water)
    // LOD chunks of CH cells; meshes are emitted per block of EB x EB chunks
    const int CH = 64; static int cnx, cnz, EB; static int[] cStep; static float[] cDist;
    static readonly List<int> selB = new List<int>(); static readonly List<float> selBd = new List<float>();
    static readonly List<int> selL = new List<int>();
    static Dictionary<long, List<int>> tbk; const float TBK = 20f; // track points in 20 m buckets

    // ================================================================ circuit styles (buildings)
    sealed class Style
    {
        public Color[] walls = { C(0.93f, 0.88f, 0.78f), C(0.88f, 0.84f, 0.76f), C(0.95f, 0.93f, 0.88f), C(0.84f, 0.78f, 0.68f), C(0.90f, 0.80f, 0.66f) };
        public Color[] brick = { C(1f, 1f, 1f), C(0.92f, 0.88f, 0.86f), C(1.04f, 0.96f, 0.92f), C(0.84f, 0.8f, 0.78f) };
        public Color[] tiles = { C(0.55f, 0.30f, 0.22f), C(0.48f, 0.27f, 0.20f), C(0.36f, 0.33f, 0.32f) };
        public Color flat = C(0.58f, 0.57f, 0.55f);
        public int house = 1;          // facade cell of houses: 0 shutters, 1 plain plaster
        public float brickShare;       // share of houses in brick
        public float pitched = 0.55f;  // share of houses without roof data that get a pitched roof
    }
    static Color C(float r, float g, float b) => new Color(r, g, b);
    static readonly Color[] Greys = { C(0.86f, 0.86f, 0.85f), C(0.78f, 0.80f, 0.82f), C(0.88f, 0.86f, 0.80f), C(0.70f, 0.74f, 0.78f), C(0.82f, 0.78f, 0.72f) };

    static Style StyleFor(string id, EnvDef e)
    {
        var s = new Style();
        switch (id)
        {
            case "monaco":
                s.walls = e.walls ?? s.walls; s.house = 0; s.pitched = 0.25f;
                s.tiles = new[] { C(0.64f, 0.38f, 0.27f), C(0.58f, 0.40f, 0.30f), C(0.70f, 0.48f, 0.36f) }; s.flat = C(0.72f, 0.68f, 0.62f); break;
            case "zandvoort":
                s.walls = new[] { C(0.95f, 0.95f, 0.93f), C(0.90f, 0.88f, 0.84f), C(0.82f, 0.80f, 0.76f), C(0.92f, 0.90f, 0.86f) };
                s.house = 1; s.brickShare = 0.75f; s.pitched = 0.8f;
                s.tiles = new[] { C(0.30f, 0.20f, 0.17f), C(0.24f, 0.22f, 0.22f), C(0.42f, 0.24f, 0.18f) }; break;
            case "silverstone":
                s.walls = new[] { C(0.92f, 0.89f, 0.82f), C(0.85f, 0.82f, 0.76f), C(0.95f, 0.94f, 0.90f) };
                s.brickShare = 0.65f; s.pitched = 0.85f; s.tiles = new[] { C(0.45f, 0.25f, 0.18f), C(0.30f, 0.28f, 0.28f), C(0.38f, 0.26f, 0.22f) }; break;
            case "spa":
                s.walls = new[] { C(0.93f, 0.92f, 0.89f), C(0.80f, 0.78f, 0.74f), C(0.88f, 0.85f, 0.78f), C(0.72f, 0.70f, 0.67f) };
                s.brickShare = 0.2f; s.pitched = 0.9f; s.tiles = new[] { C(0.25f, 0.26f, 0.29f), C(0.30f, 0.30f, 0.32f), C(0.22f, 0.22f, 0.24f) }; break;
            case "monza":
                s.walls = new[] { C(0.93f, 0.84f, 0.66f), C(0.92f, 0.76f, 0.62f), C(0.95f, 0.91f, 0.82f), C(0.86f, 0.74f, 0.58f), C(0.90f, 0.88f, 0.84f) };
                s.pitched = 0.55f; s.tiles = new[] { C(0.60f, 0.33f, 0.24f), C(0.54f, 0.30f, 0.22f), C(0.50f, 0.34f, 0.27f) }; break;
            case "suzuka":
                s.walls = new[] { C(0.92f, 0.92f, 0.90f), C(0.86f, 0.85f, 0.82f), C(0.80f, 0.78f, 0.74f), C(0.88f, 0.84f, 0.76f) };
                s.pitched = 0.75f; s.tiles = new[] { C(0.28f, 0.30f, 0.34f), C(0.34f, 0.36f, 0.40f), C(0.42f, 0.30f, 0.24f) }; break;
            case "austin":
                s.walls = new[] { C(0.88f, 0.84f, 0.76f), C(0.80f, 0.78f, 0.74f), C(0.92f, 0.90f, 0.86f), C(0.76f, 0.70f, 0.62f) };
                s.brickShare = 0.15f; s.pitched = 0.8f; s.tiles = new[] { C(0.30f, 0.29f, 0.28f), C(0.40f, 0.36f, 0.32f), C(0.34f, 0.30f, 0.27f) }; break;
            case "budapest":
                s.walls = new[] { C(0.94f, 0.88f, 0.70f), C(0.92f, 0.92f, 0.88f), C(0.88f, 0.80f, 0.70f), C(0.90f, 0.86f, 0.78f) };
                s.pitched = 0.85f; s.tiles = new[] { C(0.58f, 0.30f, 0.22f), C(0.50f, 0.28f, 0.22f), C(0.36f, 0.34f, 0.33f) }; break;
            case "catalunya":
                s.walls = new[] { C(0.95f, 0.92f, 0.85f), C(0.92f, 0.84f, 0.70f), C(0.88f, 0.80f, 0.68f), C(0.96f, 0.95f, 0.92f) };
                s.house = 0; s.pitched = 0.45f; s.tiles = new[] { C(0.66f, 0.38f, 0.26f), C(0.60f, 0.34f, 0.24f), C(0.70f, 0.45f, 0.32f) }; break;
            case "spielberg":
                s.walls = new[] { C(0.95f, 0.94f, 0.91f), C(0.92f, 0.88f, 0.80f), C(0.86f, 0.84f, 0.80f) };
                s.pitched = 0.95f; s.tiles = new[] { C(0.42f, 0.24f, 0.20f), C(0.30f, 0.30f, 0.32f), C(0.50f, 0.32f, 0.24f) }; break;
        }
        return s;
    }

    // per graphics level (1 Low, 2 Medium, 3 High)
    static int BuildingBudget => gfx <= 1 ? 1000 : gfx == 2 ? 2600 : 4500;
    static float BuildingReach => gfx <= 1 ? 900f : gfx == 2 ? 1400f : 1800f;
    static int TreeBudget => gfx <= 1 ? 1800 : gfx == 2 ? 3600 : 6000;
    static float TreeReach => gfx <= 1 ? 450f : gfx == 2 ? 700f : 900f;
    static int LandCap => gfx <= 1 ? 1024 : gfx == 2 ? 1280 : 2048;

    // ================================================================ 1. load (right after Waterfront.Prepare)
    public static void Load(Track t)
    {
        Active = Ready = HasBuildings = HasVegetation = HasWaterAreas = HasTerrain = false;
        D = null; trk = t; FreeGrids(); Vertices = BuildingCount = TreeCount = 0; LoadMs = 0;
        if (t == null || t.def == null || t.N < 8) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        WorldData w = null;
        try { w = WorldData.Load(t.def.id); } catch (System.Exception e) { Debug.LogWarning("RealWorld: " + e.Message); }
        if (w == null || !(w.HasTerrain || w.bN > 0 || w.aN > 0 || w.lN > 0 || w.tN > 0)) return;
        D = w; env = Environment.Cur; sty = StyleFor(t.def.id, env); gfx = WorldBuilder.Gfx;
        var bb = t.Bounds(); bbX0 = (float)bb.minX; bbZ0 = (float)bb.minY; bbX1 = (float)bb.maxX; bbZ1 = (float)bb.maxY;
        float reach = gfx <= 1 ? 1600f : 2300f; // beyond that the fog hides everything (far clip 1500 / 2500 m)
        float x0 = bbX0 - reach, z0 = bbZ0 - reach, x1 = bbX1 + reach, z1 = bbZ1 + reach;
        if (w.HasTerrain)
        {
            float tx1 = w.tx0 + (w.tnx - 1) * w.tcell, tz1 = w.tz0 + (w.tnz - 1) * w.tcell;
            if (w.tx0 > bbX0 - 60 || w.tz0 > bbZ0 - 60 || tx1 < bbX1 + 60 || tz1 < bbZ1 + 60)
            { Debug.LogWarning("RealWorld: the terrain grid does not cover the circuit; ignored"); w.th = null; }
            else { x0 = Mathf.Max(x0, w.tx0); z0 = Mathf.Max(z0, w.tz0); x1 = Mathf.Min(x1, tx1); z1 = Mathf.Min(z1, tz1); }
        }
        HasTerrain = w.HasTerrain;
        rx0 = x0; rz0 = z0; rx1 = x1; rz1 = z1;
        Region = new Rect(rx0, rz0, rx1 - rx0, rz1 - rz0);
        float cx = (bbX0 + bbX1) * 0.5f, cz = (bbZ0 + bbZ1) * 0.5f;
        Reach = Mathf.Min(Mathf.Min(cx - rx0, rx1 - cx), Mathf.Min(cz - rz0, rz1 - cz));
        HasBuildings = w.bN > 0;
        double water = 0; bool veg = w.tN > 0;
        for (int a = 0; a < w.aN; a++)
        {
            if (w.aCnt[a] < 3) continue;
            if (w.aKind[a] == 1 || w.aKind[a] == 8) veg = true;
            if (w.aKind[a] == 5) water += System.Math.Abs(PolyArea(w.aX, w.aZ, w.aOff[a], w.aCnt[a]));
        }
        HasVegetation = veg; HasWaterAreas = water > 3e5;
        // the level of the land at the edge of the region: the flat sheet beyond continues just below it
        if (HasTerrain)
        {
            var hs = new List<float>();
            for (float s = 0; s <= 1.0001f; s += 0.02f)
            {
                hs.Add(w.Terrain(Mathf.Lerp(rx0, rx1, s), rz0)); hs.Add(w.Terrain(Mathf.Lerp(rx0, rx1, s), rz1));
                hs.Add(w.Terrain(rx0, Mathf.Lerp(rz0, rz1, s))); hs.Add(w.Terrain(rx1, Mathf.Lerp(rz0, rz1, s)));
            }
            hs.Sort(); edgeLevel = hs[hs.Count / 2];
        }
        else edgeLevel = WorldBuilder.Height == null ? -0.1f : (float)WorldBuilder.Height.baseHeight - 0.15f;
        SheetLevel = edgeLevel - 0.8f;
        if (Waterfront.HasWater) SheetLevel = Mathf.Min(SheetLevel, SeaBed);
        Active = true;
        LoadMs = (float)sw.Elapsed.TotalMilliseconds;
    }

    static float PolyArea(float[] x, float[] z, int o, int n)
    {
        double a = 0;
        for (int i = 0, j = n - 1; i < n; j = i++) a += (double)x[o + j] * z[o + i] - (double)x[o + i] * z[o + j];
        return (float)(a * 0.5);
    }

    // ================================================================ 2. prepare (after Trackside.Build, before the scenery)
    public static void Prepare(Track t)
    {
        if (!Active) return;
        var sw = System.Diagnostics.Stopwatch.StartNew(); stage.Restart(); Timing = "";
        // distance to the nearest road / trackside building (Environment's field: it also places the landmarks)
        const float FM = 460f;
        fx0 = bbX0 - FM; fz0 = bbZ0 - FM; fx1 = bbX1 + FM; fz1 = bbZ1 + FM;
        Environment.BuildField(t, fx0, fz0, fx1 - fx0, fz1 - fz0);
        tbk = new Dictionary<long, List<int>>();
        for (int i = 0; i < t.N; i++)
        {
            long key = Key(Mathf.FloorToInt((float)t.pts[i].x / TBK), Mathf.FloorToInt((float)t.pts[i].y / TBK));
            if (!tbk.TryGetValue(key, out var l)) tbk[key] = l = new List<int>();
            l.Add(i);
        }
        street = t.def != null && t.def.style == CircuitStyle.Street;
        float hw = (float)t.width * 0.5f; wallLat = (float)Traffic.WallOffset(t);
        // the circuit's own ground (road, verges, run-off) out to c0, then a ramp into the real terrain
        c0 = street ? hw + 5f : Mathf.Min(wallLat + 16f, hw + Mathf.Max(env.flatTo - 2f, 12f)) + 8f;
        ramp = street ? 28f : 55f;
        var pc = WorldBuilder.Pad.center; padLevel = WorldBuilder.H(pc.x, pc.y) + 0.03f;
        // grids (the region grows to whole cells)
        B = gfx <= 1 ? 12f : gfx == 2 ? 10f : 8f;
        gnx = Mathf.CeilToInt((rx1 - rx0) / B) + 1; gnz = Mathf.CeilToInt((rz1 - rz0) / B) + 1;
        rx1 = rx0 + (gnx - 1) * B; rz1 = rz0 + (gnz - 1) * B;
        Region = new Rect(rx0, rz0, rx1 - rx0, rz1 - rz0);
        lnx = Mathf.Clamp(Mathf.CeilToInt((rx1 - rx0) / 3f), 64, LandCap); lnz = Mathf.Clamp(Mathf.CeilToInt((rz1 - rz0) / 3f), 64, LandCap);
        lcx = (rx1 - rx0) / lnx; lcz = (rz1 - rz0) / lnz;
        lid = new ushort[lnx * lnz]; lflag = new byte[lnx * lnz];
        Mark("field");
        RasterAreas(); Mark("areas");
        SelectBuildings(); Mark("selB");
        SelectRoads(); Mark("selL");
        Heights(); Mark("heights");
        Water(); Mark("water");
        Canopy(); Mark("canopy");
        ChunkSteps(); Mark("chunks");
        Ready = true;
        LoadMs += (float)sw.Elapsed.TotalMilliseconds;
    }

    // ---------------------------------------------------------------- small helpers
    static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;
    static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }
    static float Hash(int x, int y)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263;
            h = (h ^ (int)((uint)h >> 13)) * 1274126177;
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296f;
        }
    }
    static float Noise(float x, float y, int s)
    {
        int i = Mathf.FloorToInt(x), j = Mathf.FloorToInt(y); float a = x - i, b = y - j;
        a = a * a * (3 - 2 * a); b = b * b * (3 - 2 * b);
        return Mathf.Lerp(Mathf.Lerp(Hash(i + s * 101, j), Hash(i + 1 + s * 101, j), a), Mathf.Lerp(Hash(i + s * 101, j + 1), Hash(i + 1 + s * 101, j + 1), a), b);
    }
    // approximate distance (m) to the nearest road or trackside building (12 m field; outside it, to the track's box)
    static float Dist(float x, float z)
    {
        if (x > fx0 + 13f && x < fx1 - 13f && z > fz0 + 13f && z < fz1 - 13f) return Environment.Clear(x, z);
        float dx = Mathf.Max(Mathf.Max(bbX0 - x, 0f), x - bbX1), dz = Mathf.Max(Mathf.Max(bbZ0 - z, 0f), z - bbZ1);
        return Mathf.Max(440f, Mathf.Sqrt(dx * dx + dz * dz));
    }
    // exact distance to the nearest centreline point when one is within lim (else lim)
    static float TrackDist(float x, float z, float lim)
    {
        int r = Mathf.CeilToInt(lim / TBK), ci = Mathf.FloorToInt(x / TBK), cj = Mathf.FloorToInt(z / TBK);
        float best = lim * lim;
        for (int dj = -r; dj <= r; dj++) for (int di = -r; di <= r; di++)
        {
            if (!tbk.TryGetValue(Key(ci + di, cj + dj), out var l)) continue;
            for (int q = 0; q < l.Count; q++)
            {
                var p = trk.pts[l[q]]; float dx = (float)p.x - x, dz = (float)p.y - z, d2 = dx * dx + dz * dz;
                if (d2 < best) best = d2;
            }
        }
        return Mathf.Sqrt(best);
    }
    static float PadDist(float x, float z)
    {
        var p = WorldBuilder.Pad;
        float dx = Mathf.Max(Mathf.Max(p.xMin - x, 0f), x - p.xMax), dz = Mathf.Max(Mathf.Max(p.yMin - z, 0f), z - p.yMax);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
    static int Texel(float x, float z)
    {
        int i = (int)((x - rx0) / lcx), j = (int)((z - rz0) / lcz);
        if (x < rx0 || z < rz0 || i >= lnx || j >= lnz) return -1;
        return j * lnx + i;
    }
    static int KindAt(float x, float z, out int id)
    {
        int k = Texel(x, z); id = 0;
        if (k < 0) return -1;
        id = lid[k];
        return id == 0 ? 0 : D.aKind[id - 1];
    }
    static float Bilinear(float[] g, float x, float z)
    {
        float fx = Mathf.Clamp((x - rx0) / B, 0f, gnx - 1.001f), fz = Mathf.Clamp((z - rz0) / B, 0f, gnz - 1.001f);
        int i = (int)fx, j = (int)fz; float a = fx - i, b = fz - j; int k = j * gnx + i;
        return (g[k] * (1 - a) + g[k + 1] * a) * (1 - b) + (g[k + gnx] * (1 - a) + g[k + gnx + 1] * a) * b;
    }
    static float Ground(float x, float z) => Bilinear(gGround, x, z);

    // the ground height at (x, z) for the scenery (landmarks): the real terrain while it is known, else the circuit's
    public static float GroundY(float x, float z) => Ready && gGround != null ? Ground(x, z) : WorldBuilder.H(x, z);

    // ---------------------------------------------------------------- land-use polygons -> texel ids
    static void RasterAreas()
    {
        int n = Mathf.Min(D.aN, 65534);
        for (int p = 0; p < n; p++)
        {
            if (D.aCnt[p] < 3) continue;
            ushort id = (ushort)(p + 1);
            WorldGeo.Fill(D.aX, D.aZ, D.aOff[p], D.aCnt[p], rx0, rz0, lcx, lcz, lnx, lnz, (r, a, b) => { int o = r * lnx; for (int c = a; c <= b; c++) lid[o + c] = id; });
        }
    }

    // ---------------------------------------------------------------- which buildings: nearest first, within budget
    struct BCand { public float d, cx, cz, rad; public int b; }
    static void SelectBuildings()
    {
        selB.Clear(); selBd.Clear();
        if (D.bN == 0) return;
        float reach = BuildingReach; var cand = new List<BCand>();
        for (int b = 0; b < D.bN; b++)
        {
            int n = D.bCnt[b], o = D.bOff[b]; if (n < 3) continue;
            float cx = 0, cz = 0;
            for (int k = 0; k < n; k++) { cx += D.bX[o + k]; cz += D.bZ[o + k]; }
            cx /= n; cz /= n;
            if (cx < rx0 + 20 || cx > rx1 - 20 || cz < rz0 + 20 || cz > rz1 - 20) continue;
            float rad = 0;
            for (int k = 0; k < n; k++) { float dx = D.bX[o + k] - cx, dz = D.bZ[o + k] - cz; rad = Mathf.Max(rad, dx * dx + dz * dz); }
            rad = Mathf.Sqrt(rad); if (rad > 450f) continue;
            float d = Dist(cx, cz) - rad;
            if (d > reach) continue;
            float area = Mathf.Abs(PolyArea(D.bX, D.bZ, o, n));
            if (area < 6f || ((D.bKind[b] == 7 || area < 30f) && d > 220f)) continue;
            cand.Add(new BCand { d = d, cx = cx, cz = cz, rad = rad, b = b });
        }
        cand.Sort((a, c) => a.d.CompareTo(c.d));
        float lim = wallLat + (street ? 0.8f : 3f);
        int budget = BuildingBudget;
        for (int q = 0; q < cand.Count && selB.Count < budget; q++)
        {
            var c = cand[q]; int b = c.b, n = D.bCnt[b], o = D.bOff[b];
            if (PadDist(c.cx, c.cz) < c.rad + 15f) continue;
            if (Environment.Reserved(c.cx, c.cz, c.rad * 0.7f)) continue;
            if (Waterfront.HasWater && Waterfront.InWater(c.cx, c.cz)) continue;
            if (c.d < 90f && Conflicts(o, n, lim, c.cx, c.cz, c.d)) continue;
            selB.Add(b); selBd.Add(Mathf.Max(0f, c.d));
            WorldGeo.Fill(D.bX, D.bZ, o, n, rx0, rz0, lcx, lcz, lnx, lnz, (r, a, e) => { int w = r * lnx; for (int k = a; k <= e; k++) lflag[w + k] |= 2; });
        }
        BuildingCount = selB.Count;
    }
    // does the footprint reach the road, the strip up to behind the barrier, or a stand / pit building?
    static bool Conflicts(int o, int n, float lim, float cx, float cz, float d)
    {
        for (int k = 0; k < n; k++)
        {
            float ax = D.bX[o + k], az = D.bZ[o + k], bx = D.bX[o + (k + 1) % n], bz = D.bZ[o + (k + 1) % n];
            float len = Mathf.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            int steps = Mathf.Clamp(Mathf.CeilToInt(len / 4f), 1, 200);
            for (int s = 0; s < steps; s++)
            {
                float u = (float)s / steps, x = ax + (bx - ax) * u, z = az + (bz - az) * u;
                if (TrackDist(x, z, lim) < lim || Trackside.Blocked(x, z, 1.5f)) return true;
            }
        }
        if (d < 30f && Waterfront.InTunnel(cx, cz)) return true;
        return false;
    }

    // ---------------------------------------------------------------- which roads (by kind and distance)
    static float Width(int l)
    {
        float w = D.lW[l];
        if (w > 0.5f) return Mathf.Clamp(w, 1.5f, 30f);
        switch (D.lKind[l]) { case 0: return 12f; case 1: return 8f; case 2: return 6.5f; case 3: return 5f; case 4: return 2f; default: return 3.4f; }
    }
    static void SelectRoads()
    {
        selL.Clear(); if (D.lN == 0) return;
        float k = gfx <= 1 ? 0.62f : 1f;
        for (int l = 0; l < D.lN; l++)
        {
            int n = D.lCnt[l], o = D.lOff[l]; if (n < 2) continue;
            int kind = D.lKind[l];
            float reach = (kind <= 1 ? 1700f : kind == 2 ? 1300f : kind == 3 ? 900f : kind == 4 ? 280f : 1300f) * k;
            float dmin = float.MaxValue;
            for (int q = 0; q < n; q++)
            {
                float x = D.lX[o + q], z = D.lZ[o + q];
                if (x < rx0 || x > rx1 || z < rz0 || z > rz1) continue;
                dmin = Mathf.Min(dmin, Dist(x, z));
            }
            if (dmin > reach) continue;
            selL.Add(l);
            float r = Width(l) * 0.5f + 2.5f; // the road and a margin: no trees or canopy on it
            for (int q = 0; q + 1 < n; q++) MarkSegment(D.lX[o + q], D.lZ[o + q], D.lX[o + q + 1], D.lZ[o + q + 1], r, 1);
        }
    }
    static void MarkSegment(float ax, float az, float bx, float bz, float r, byte bit)
    {
        int i0 = Mathf.Max(0, (int)((Mathf.Min(ax, bx) - r - rx0) / lcx)), i1 = Mathf.Min(lnx - 1, (int)((Mathf.Max(ax, bx) + r - rx0) / lcx));
        int j0 = Mathf.Max(0, (int)((Mathf.Min(az, bz) - r - rz0) / lcz)), j1 = Mathf.Min(lnz - 1, (int)((Mathf.Max(az, bz) + r - rz0) / lcz));
        float dx = bx - ax, dz = bz - az, l2 = Mathf.Max(1e-6f, dx * dx + dz * dz), r2 = r * r;
        for (int j = j0; j <= j1; j++)
        {
            float pz = rz0 + (j + 0.5f) * lcz;
            for (int i = i0; i <= i1; i++)
            {
                float px = rx0 + (i + 0.5f) * lcx, t = Mathf.Clamp01(((px - ax) * dx + (pz - az) * dz) / l2);
                float qx = px - ax - t * dx, qz = pz - az - t * dz;
                if (qx * qx + qz * qz < r2) lflag[j * lnx + i] |= bit;
            }
        }
    }

    // ---------------------------------------------------------------- ground heights
    static void Heights()
    {
        int n = gnx * gnz; gGround = new float[n]; gW = new float[n];
        var hf = WorldBuilder.Height; float lowR = B * 1.1f + 2f;
        for (int j = 0; j < gnz; j++)
        {
            float z = rz0 + j * B;
            for (int i = 0; i < gnx; i++)
            {
                float x = rx0 + i * B; int k = j * gnx + i;
                float y = HasTerrain ? D.Terrain(x, z) : (hf == null ? -0.1f : WorldBuilder.H(x, z) - 0.15f);
                if (HasTerrain)
                {
                    float e = Mathf.Min(Mathf.Min(x - rx0, rx1 - x), Mathf.Min(z - rz0, rz1 - z));
                    if (e < 250f) y = Mathf.Lerp(edgeLevel, y, Smooth(e / 250f));
                }
                float d = Dist(x, z), w = Smooth((d - c0) / ramp);
                if (w < 1f)
                {
                    // the circuit's own ground: below every road within reach of this cell
                    float tb = hf == null ? -0.1f : (d < c0 + B ? Lowest(hf, x, z, lowR) : (float)hf.Sample(x, z)) - 0.12f;
                    y = d < c0 ? tb : Mathf.Lerp(tb, y, w);
                }
                float dp = PadDist(x, z);
                if (dp < 70f) { float wp = Smooth((dp - 12f) / 55f); y = Mathf.Lerp(padLevel - 0.1f, y, wp); w = Mathf.Min(w, wp); }
                if (Waterfront.HasWater && Waterfront.InWater(x, z)) { y = SeaBed; w = 0f; }
                gGround[k] = y; gW[k] = w;
            }
        }
    }

    // lowest of the circuit's ground at the point and four points round it (HeightField.Lowest takes 17 samples)
    static float Lowest(HeightField hf, float x, float z, float r)
    {
        double lo = hf.Sample(x, z);
        lo = System.Math.Min(lo, hf.Sample(x + r, z)); lo = System.Math.Min(lo, hf.Sample(x - r, z));
        lo = System.Math.Min(lo, hf.Sample(x, z + r)); lo = System.Math.Min(lo, hf.Sample(x, z - r));
        return (float)lo;
    }

    // water level of each lake / river / sea polygon, and the ground carved below it
    static void Water()
    {
        waterL = new float[D.aN];
        for (int p = 0; p < D.aN; p++) waterL[p] = float.NaN;
        var hs = new List<float>();
        for (int p = 0; p < D.aN; p++)
        {
            int n = D.aCnt[p], o = D.aOff[p];
            if (n < 3 || D.aKind[p] != 5) continue;
            hs.Clear(); bool harbour = false; float corr = float.MaxValue;
            int stride = Mathf.Max(1, n / 400);
            for (int k = 0; k < n; k += stride)
            {
                float x = D.aX[o + k], z = D.aZ[o + k];
                if (x < rx0 || x > rx1 || z < rz0 || z > rz1) continue;
                float g = Ground(x, z); hs.Add(g);
                if (Waterfront.HasWater && Waterfront.InWater(x, z)) harbour = true;
                if (Bilinear(gW, x, z) < 1f) corr = Mathf.Min(corr, g - 0.45f);
            }
            if (hs.Count == 0) continue;
            hs.Sort();
            float L = Mathf.Min(hs[(int)(hs.Count * 0.15f)] - 0.3f, corr);
            if (harbour) L = 1.0f; // the harbour's own water level (WorldBuilder: Waterfront.BuildWater(root, 1.0f))
            waterL[p] = L;
        }
        for (int j = 0; j < gnz; j++)
            for (int i = 0; i < gnx; i++)
            {
                int k = j * gnx + i; if (gW[k] <= 0f) continue;
                int kind = KindAt(rx0 + i * B, rz0 + j * B, out int id);
                if (kind != 5 || float.IsNaN(waterL[id - 1])) continue;
                float target = waterL[id - 1] - 1.8f;
                if (gGround[k] > target) gGround[k] = Mathf.Lerp(gGround[k], target, gW[k]);
            }
    }

    // ---------------------------------------------------------------- woods: the canopy raised over forest polygons
    static float CanopyHeight()
    {
        if (env.canopy > 0) return env.canopy;
        return Mathf.Max(6f, BaseH(env.tree) * env.treeSize * 0.85f);
    }
    static float BaseH(TreeKind k) => k == TreeKind.Spruce ? 15f : k == TreeKind.Pine ? 9.6f : k == TreeKind.Palm ? 9.4f : k == TreeKind.Scrub ? 2.2f : 10f;
    static void Canopy()
    {
        int n = gnx * gnz; gCan = new float[n]; gTop = new float[n];
        var fd = new float[n]; bool any = false;
        for (int j = 0; j < gnz; j++)
            for (int i = 0; i < gnx; i++)
            {
                int k = j * gnx + i, t = Texel(rx0 + i * B, rz0 + j * B);
                bool f = t >= 0 && lid[t] != 0 && D.aKind[lid[t] - 1] == 1 && lflag[t] == 0 && gW[k] > 0f;
                fd[k] = f ? 1e6f : 0f; any |= f;
            }
        if (!any) { System.Array.Copy(gGround, gTop, n); return; }
        // chamfer distance (m) from the edge of the woods
        float A = B, Dg = B * 1.4142f;
        for (int j = 0; j < gnz; j++)
            for (int i = 0; i < gnx; i++)
            {
                int k = j * gnx + i; float v = fd[k]; if (v == 0f) continue;
                if (i > 0) v = Mathf.Min(v, fd[k - 1] + A);
                if (j > 0) { v = Mathf.Min(v, fd[k - gnx] + A); if (i > 0) v = Mathf.Min(v, fd[k - gnx - 1] + Dg); if (i < gnx - 1) v = Mathf.Min(v, fd[k - gnx + 1] + Dg); }
                fd[k] = v;
            }
        for (int j = gnz - 1; j >= 0; j--)
            for (int i = gnx - 1; i >= 0; i--)
            {
                int k = j * gnx + i; float v = fd[k]; if (v == 0f) continue;
                if (i < gnx - 1) v = Mathf.Min(v, fd[k + 1] + A);
                if (j < gnz - 1) { v = Mathf.Min(v, fd[k + gnx] + A); if (i < gnx - 1) v = Mathf.Min(v, fd[k + gnx + 1] + Dg); if (i > 0) v = Mathf.Min(v, fd[k + gnx - 1] + Dg); }
                fd[k] = v;
            }
        float ch = CanopyHeight();
        for (int j = 0; j < gnz; j++)
            for (int i = 0; i < gnx; i++)
            {
                int k = j * gnx + i; float c = 0f;
                if (fd[k] > 0f)
                {
                    float x = rx0 + i * B, z = rz0 + j * B;
                    // a steep edge (a wall of trees) a little beyond the circuit's own ground, not the long terrain ramp
                    float dd = Dist(x, z);
                    c = Smooth((fd[k] - 4f) / 12f) * Smooth((dd - c0 + 2f) / 12f) * (gW[k] > 0f ? 1f : 0f);
                    if (PadDist(x, z) < 40f) c = 0f;
                    if (c > 0f && Environment.Reserved(x, z, 14f)) c = 0f; // clearings round the landmarks
                    if (c > 0f) gTop[k] = gGround[k] + c * ch * (0.82f + 0.36f * Noise(x / 41f, z / 41f, 7));
                    else gTop[k] = gGround[k];
                }
                else gTop[k] = gGround[k];
                gCan[k] = c;
            }
    }

    // ---------------------------------------------------------------- terrain LOD per chunk (by distance to the track)
    static void ChunkSteps()
    {
        cnx = (gnx - 2) / CH + 1; cnz = (gnz - 2) / CH + 1;
        EB = B <= 10f ? 2 : 1; // mesh blocks of about 1-1.3 km
        cStep = new int[cnx * cnz]; cDist = new float[cnx * cnz];
        float k = gfx <= 1 ? 0.6f : gfx == 2 ? 0.75f : 1f; int stride = Mathf.Max(1, trk.N / 400);
        for (int cj = 0; cj < cnz; cj++)
            for (int ci = 0; ci < cnx; ci++)
            {
                float ax = rx0 + ci * CH * B, az = rz0 + cj * CH * B, bx = Mathf.Min(rx1, ax + CH * B), bz = Mathf.Min(rz1, az + CH * B);
                float best = float.MaxValue;
                for (int i = 0; i < trk.N; i += stride)
                {
                    float x = (float)trk.pts[i].x, z = (float)trk.pts[i].y;
                    float dx = Mathf.Max(Mathf.Max(ax - x, 0f), x - bx), dz = Mathf.Max(Mathf.Max(az - z, 0f), z - bz);
                    best = Mathf.Min(best, dx * dx + dz * dz);
                }
                float d = Mathf.Sqrt(best);
                cDist[cj * cnx + ci] = d;
                cStep[cj * cnx + ci] = d < 260f * k ? 1 : d < 800f * k ? 2 : d < 1600f * k ? 4 : 8;
            }
    }
    // height of the terrain mesh at (x, z): the same lattice and triangle split as the chunk's mesh
    static float MeshY(float x, float z)
    {
        float fx = Mathf.Clamp((x - rx0) / B, 0f, gnx - 1.001f), fz = Mathf.Clamp((z - rz0) / B, 0f, gnz - 1.001f);
        int ci = Mathf.Min(cnx - 1, (int)(fx / CH)), cj = Mathf.Min(cnz - 1, (int)(fz / CH)), s = cStep[cj * cnx + ci];
        int i0 = ci * CH, iEnd = Mathf.Min(i0 + CH, gnx - 1), j0 = cj * CH, jEnd = Mathf.Min(j0 + CH, gnz - 1);
        int ia = i0 + ((int)(fx - i0) / s) * s; if (ia >= iEnd) ia = i0 + ((iEnd - 1 - i0) / s) * s;
        int ja = j0 + ((int)(fz - j0) / s) * s; if (ja >= jEnd) ja = j0 + ((jEnd - 1 - j0) / s) * s;
        int ib = Mathf.Min(ia + s, iEnd), jb = Mathf.Min(ja + s, jEnd);
        float a = Mathf.Clamp01((fx - ia) / (ib - ia)), b = Mathf.Clamp01((fz - ja) / (jb - ja));
        float y00 = gTop[ja * gnx + ia], y10 = gTop[ja * gnx + ib], y01 = gTop[jb * gnx + ia], y11 = gTop[jb * gnx + ib];
        return a + b <= 1f ? y00 + a * (y10 - y00) + b * (y01 - y00) : y11 + (1f - a) * (y01 - y11) + (1f - b) * (y10 - y11);
    }

    // ================================================================ 3. build the meshes (after WorldBuilder.Drape)
    sealed class MB
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>(), uv2 = new List<Vector2>();
        public readonly List<Color> col = new List<Color>();
        public readonly List<int> tri = new List<int>();
    }
    const int SLand = 0, SWater = 1, SRoad = 2, SBuild = 3, STree = 4;
    static readonly Dictionary<int, List<MB>> bufs = new Dictionary<int, List<MB>>();
    static int Block(float x, float z)
    {
        int ci = Mathf.Clamp((int)((x - rx0) / (CH * B)), 0, cnx - 1) / EB, cj = Mathf.Clamp((int)((z - rz0) / (CH * B)), 0, cnz - 1) / EB;
        return cj * ((cnx + EB - 1) / EB) + ci;
    }
    // the buffer of this material in this block with room for "need" more vertices (16-bit indices)
    static MB Buf(int slot, int block, int need)
    {
        int key = block * 8 + slot;
        if (!bufs.TryGetValue(key, out var l)) bufs[key] = l = new List<MB>();
        if (l.Count == 0 || l[l.Count - 1].v.Count + need > 64000) l.Add(new MB());
        return l[l.Count - 1];
    }

    public static void Build(Track t, Transform root)
    {
        if (!Active || !Ready) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bufs.Clear();
        try
        {
            stage.Restart();
            TerrainMeshes(); Mark("terrain");
            WaterMeshes(); Mark("waterM");
            Roads(); Mark("roads");
            Buildings(); Mark("buildings");
            Trees(); Mark("trees");
            var land = LandTexture(); Mark("landtex");
            Emit(root, land); Mark("emit");
        }
        catch (System.Exception e) { Debug.LogError("RealWorld: " + e); }
        bufs.Clear();
        FreeGrids(); D = null; // (the flags HasBuildings ... stay for the rest of this load)
        LoadMs += (float)sw.Elapsed.TotalMilliseconds;
        Debug.Log("RealWorld " + t.def.id + ": " + BuildingCount + " buildings, " + TreeCount + " trees, " + Vertices + " vertices, " + LoadMs.ToString("F0") + " ms (" + Timing + ")");
    }

    static void FreeGrids()
    {
        Ready = false;
        gGround = gTop = gW = gCan = null; lid = null; lflag = null; waterL = null; cStep = null; cDist = null; tbk = null;
    }

    // ---------------------------------------------------------------- terrain
    static Vector3 Normal(int i, int j, int s)
    {
        int ia = Mathf.Max(0, i - s), ib = Mathf.Min(gnx - 1, i + s), ja = Mathf.Max(0, j - s), jb = Mathf.Min(gnz - 1, j + s);
        float dx = (gTop[j * gnx + ib] - gTop[j * gnx + ia]) / ((ib - ia) * B), dz = (gTop[jb * gnx + i] - gTop[ja * gnx + i]) / ((jb - ja) * B);
        return new Vector3(-dx, 1f, -dz).normalized;
    }
    static void TerrainMeshes()
    {
        var cols = new List<int>(CH + 2); var rows = new List<int>(CH + 2);
        for (int cj = 0; cj < cnz; cj++)
            for (int ci = 0; ci < cnx; ci++)
            {
                int c = cj * cnx + ci, s = cStep[c];
                int i0 = ci * CH, i1 = Mathf.Min(i0 + CH, gnx - 1), j0 = cj * CH, j1 = Mathf.Min(j0 + CH, gnz - 1);
                if (i1 <= i0 || j1 <= j0) continue;
                cols.Clear(); for (int i = i0; i < i1; i += s) cols.Add(i); cols.Add(i1);
                rows.Clear(); for (int j = j0; j < j1; j += s) rows.Add(j); rows.Add(j1);
                int w = cols.Count, h = rows.Count;
                var b = Buf(SLand, Block(rx0 + (i0 + 1) * B, rz0 + (j0 + 1) * B), w * h + 4 * (w + h) + 8);
                int start = b.v.Count;
                for (int r = 0; r < h; r++)
                    for (int q = 0; q < w; q++)
                    {
                        int i = cols[q], j = rows[r]; float x = rx0 + i * B, z = rz0 + j * B;
                        b.v.Add(new Vector3(x, gTop[j * gnx + i], z)); b.n.Add(Normal(i, j, s)); b.uv.Add(new Vector2(x, z));
                    }
                for (int r = 0; r + 1 < h; r++)
                    for (int q = 0; q + 1 < w; q++)
                    {
                        int k = start + r * w + q;
                        b.tri.Add(k); b.tri.Add(k + w); b.tri.Add(k + 1);
                        b.tri.Add(k + 1); b.tri.Add(k + w); b.tri.Add(k + w + 1);
                    }
                // skirts where a neighbour has another step (hide the cracks of the T-junctions)
                if (ci > 0 && cStep[c - 1] != s) Skirt(b, start, w, h, cols, rows, 0);
                if (ci < cnx - 1 && cStep[c + 1] != s) Skirt(b, start, w, h, cols, rows, 1);
                if (cj > 0 && cStep[c - cnx] != s) Skirt(b, start, w, h, cols, rows, 2);
                if (cj < cnz - 1 && cStep[c + cnx] != s) Skirt(b, start, w, h, cols, rows, 3);
            }
    }
    // side 0 left (first column), 1 right, 2 bottom (first row), 3 top
    static void Skirt(MB b, int start, int w, int h, List<int> cols, List<int> rows, int side)
    {
        int cnt = side < 2 ? h : w, first = b.v.Count;
        int Idx(int e) => start + (side < 2 ? e * w + (side == 0 ? 0 : w - 1) : (side == 2 ? 0 : h - 1) * w + e);
        for (int e = 0; e < cnt; e++)
        {
            int vi = Idx(e); var top = b.v[vi]; float lo = top.y;
            if (side < 2)
            {
                int i = cols[side == 0 ? 0 : w - 1], ja = rows[Mathf.Max(0, e - 1)], jb = rows[Mathf.Min(h - 1, e + 1)];
                for (int j = ja; j <= jb; j++) lo = Mathf.Min(lo, gTop[j * gnx + i]);
            }
            else
            {
                int j = rows[side == 2 ? 0 : h - 1], ia = cols[Mathf.Max(0, e - 1)], ib = cols[Mathf.Min(w - 1, e + 1)];
                for (int i = ia; i <= ib; i++) lo = Mathf.Min(lo, gTop[j * gnx + i]);
            }
            b.v.Add(new Vector3(top.x, lo - 0.6f, top.z)); b.n.Add(b.n[vi]); b.uv.Add(b.uv[vi]);
        }
        for (int e = 0; e + 1 < cnt; e++)
        {
            int t0 = Idx(e), t1 = Idx(e + 1), d0 = first + e, d1 = first + e + 1;
            b.tri.Add(t0); b.tri.Add(t1); b.tri.Add(d0); b.tri.Add(t1); b.tri.Add(d1); b.tri.Add(d0);
            b.tri.Add(t0); b.tri.Add(d0); b.tri.Add(t1); b.tri.Add(t1); b.tri.Add(d0); b.tri.Add(d1);
        }
    }

    // ---------------------------------------------------------------- water surfaces
    static void WaterMeshes()
    {
        var tl = new List<int>();
        for (int p = 0; p < D.aN; p++)
        {
            if (D.aKind[p] != 5 || D.aCnt[p] < 3 || float.IsNaN(waterL[p])) continue;
            int n = D.aCnt[p], o = D.aOff[p];
            var x = new float[n]; var z = new float[n];
            System.Array.Copy(D.aX, o, x, 0, n); System.Array.Copy(D.aZ, o, z, 0, n);
            n = WorldGeo.Clean(x, z, n); if (n < 3) continue;
            float tol = 0.8f;
            n = WorldGeo.Simplify(x, z, n, tol, true);
            while (n > 1500) { tol *= 2f; n = WorldGeo.Simplify(x, z, n, tol, true); }
            n = WorldGeo.ClipRect(ref x, ref z, n, rx0, rz0, rx1, rz1);
            n = WorldGeo.Clean(x, z, n); if (n < 3) continue;
            tl.Clear(); WorldGeo.Triangulate(x, z, n, tl);
            float L = waterL[p], mx = 0, mz = 0;
            for (int k = 0; k < n; k++) { mx += x[k]; mz += z[k]; }
            var b = Buf(SWater, 0, n); // (all water in one mesh: a few thousand vertices, one draw call)
            int start = b.v.Count;
            for (int k = 0; k < n; k++) { b.v.Add(new Vector3(x[k], L, z[k])); b.n.Add(Vector3.up); b.uv.Add(Vector2.zero); }
            for (int k = 0; k + 2 < tl.Count; k += 3)
            {
                int a = tl[k], c = tl[k + 1], e = tl[k + 2];
                float tx = (x[a] + x[c] + x[e]) / 3f, tz = (z[a] + z[c] + z[e]) / 3f;
                if (Dist(tx, tz) < c0 - 3f || PadDist(tx, tz) < 5f) continue; // never over the circuit's own ground
                b.tri.Add(start + a); b.tri.Add(start + e); b.tri.Add(start + c); // (counter-clockwise from above -> Unity's winding)
            }
        }
    }

    // ---------------------------------------------------------------- roads, paths, railways
    static void Roads()
    {
        float lim = wallLat + (street ? 1.5f : 3.5f);
        var sx = new List<float>(); var sz = new List<float>(); var sd = new List<float>(); var ok = new List<bool>();
        foreach (int l in selL)
        {
            int n = D.lCnt[l], o = D.lOff[l];
            var x = new float[n]; var z = new float[n];
            System.Array.Copy(D.lX, o, x, 0, n); System.Array.Copy(D.lZ, o, z, 0, n);
            n = WorldGeo.Simplify(x, z, n, 0.5f, false);
            int kind = D.lKind[l]; float half = Width(l) * 0.5f;
            int col = kind <= 2 ? (kind == 2 && half < 3f ? 1 : 0) : kind == 3 ? 1 : kind == 4 ? 2 : 3;
            float period = col == 0 ? 12f : col == 1 ? 8f : col == 2 ? 6f : 9.6f;
            float lift = kind == 5 ? 0.16f : kind == 0 ? 0.13f : kind == 1 ? 0.12f : kind == 2 ? 0.11f : kind == 3 ? 0.10f : 0.08f;
            float u0 = col * 0.25f + 0.008f, u1 = col * 0.25f + 0.242f;
            // stations every 8 m or less (the terrain cell)
            sx.Clear(); sz.Clear(); sd.Clear(); ok.Clear(); float acc = 0;
            for (int q = 0; q < n; q++)
            {
                if (q > 0)
                {
                    float dx = x[q] - x[q - 1], dz = z[q] - z[q - 1], len = Mathf.Sqrt(dx * dx + dz * dz);
                    int steps = Mathf.Clamp(Mathf.CeilToInt(len / 8f), 1, 400);
                    for (int s = 1; s <= steps; s++)
                    {
                        float u = (float)s / steps; sx.Add(x[q - 1] + dx * u); sz.Add(z[q - 1] + dz * u); sd.Add(acc + len * u);
                    }
                    acc += len;
                }
                else { sx.Add(x[0]); sz.Add(z[0]); sd.Add(0); }
            }
            for (int s = 0; s < sx.Count; s++)
            {
                float px = sx[s], pz = sz[s]; bool good = px > rx0 + 4 && px < rx1 - 4 && pz > rz0 + 4 && pz < rz1 - 4;
                if (good && Dist(px, pz) < lim + half + 14f) good = TrackDist(px, pz, lim + half) >= lim + half && !Trackside.Blocked(px, pz, half);
                if (good && PadDist(px, pz) < half + 8f) good = false;
                ok.Add(good);
            }
            int a = 0;
            while (a < sx.Count)
            {
                if (!ok[a]) { a++; continue; }
                int e = a; while (e + 1 < sx.Count && ok[e + 1]) e++;
                if (e > a) Ribbon(sx, sz, sd, a, e, half, lift, u0, u1, period);
                a = e + 1;
            }
        }
    }
    static void Ribbon(List<float> sx, List<float> sz, List<float> sd, int a, int e, float half, float lift, float u0, float u1, float period)
    {
        MB b = null; int block = -1, prevL = -1;
        for (int s = a; s <= e; s++)
        {
            int sp = Mathf.Max(a, s - 1), sn = Mathf.Min(e, s + 1);
            float tx = sx[sn] - sx[sp], tz = sz[sn] - sz[sp], tl = Mathf.Sqrt(tx * tx + tz * tz);
            if (tl < 1e-4f) { tx = 1; tz = 0; } else { tx /= tl; tz /= tl; }
            // miter: wider where the line bends, at most 1.6 x
            float m = 1f;
            if (s > a && s < e)
            {
                float ax = sx[s] - sx[sp], az = sz[s] - sz[sp], al = Mathf.Sqrt(ax * ax + az * az);
                if (al > 1e-4f) { float cs = (ax * tx + az * tz) / al; m = Mathf.Min(1.6f, 1f / Mathf.Max(0.3f, cs)); }
            }
            float nx = -tz * half * m, nz = tx * half * m;
            float px = sx[s], pz = sz[s];
            int blk = Block(px, pz);
            float wl = float.NaN; int kind = KindAt(px, pz, out int id);
            if (kind == 5 && id > 0) wl = waterL[id - 1];
            Vector3 L = new Vector3(px + nx, 0, pz + nz), R = new Vector3(px - nx, 0, pz - nz);
            L.y = MeshY(L.x, L.z) + lift; R.y = MeshY(R.x, R.z) + lift;
            if (!float.IsNaN(wl)) { L.y = Mathf.Max(L.y, wl + 0.7f); R.y = Mathf.Max(R.y, wl + 0.7f); } // a bridge over the water
            float v = sd[s] / period;
            if (blk != block)
            {
                // into another block: close the strip here and start a new one from the same station
                if (b != null) AddRibbonPair(b, ref prevL, L, R, u0, u1, v);
                block = blk; b = Buf(SRoad, blk, (e - s + 1) * 2 + 2); prevL = -1;
            }
            AddRibbonPair(b, ref prevL, L, R, u0, u1, v);
        }
    }
    static void AddRibbonPair(MB b, ref int prevL, Vector3 L, Vector3 R, float u0, float u1, float v)
    {
        int k = b.v.Count;
        b.v.Add(L); b.v.Add(R); b.n.Add(Vector3.up); b.n.Add(Vector3.up);
        b.uv.Add(new Vector2(u0, v)); b.uv.Add(new Vector2(u1, v));
        if (prevL >= 0)
        {
            int l0 = prevL, r0 = prevL + 1, l1 = k, r1 = k + 1;
            b.tri.Add(l0); b.tri.Add(l1); b.tri.Add(r0); b.tri.Add(r0); b.tri.Add(l1); b.tri.Add(r1);
        }
        prevL = k;
    }

    // ---------------------------------------------------------------- buildings
    // atlas cells (EnvTex.BuildingAtlas) and the size in metres one tile covers (bay width, floor height)
    const int CShutter = 0, CHouse = 1, CBrick = 2, COffice = 3, CIndustry = 4, CFlats = 5, CTower = 6, CShed = 7,
              CTiles = 8, CFlatRoof = 9, CMetal = 10, CStone = 11, CConcrete = 12, CPlaster = 13;
    static readonly float[] CellW = { 3.1f, 3.0f, 3.0f, 3.0f, 6f, 3.4f, 3.0f, 3f, 2f, 8f, 4f, 4f, 5f, 4f, 4f, 4f };
    static readonly float[] CellH = { 3.2f, 3.0f, 3.0f, 3.8f, 6f, 3.0f, 3.4f, 3f, 2f, 8f, 4f, 6f, 4f, 4f, 4f, 4f };
    static Vector2 CellUv(int c) => new Vector2(c % 4, c / 4);

    static float[] px = new float[256], pz = new float[256];
    static readonly List<int> roofTri = new List<int>();

    static void Buildings()
    {
        for (int q = 0; q < selB.Count; q++)
        {
            float d = selBd[q];
            Building(selB[q], d < 250f ? 0 : d < 700f ? 1 : 2);
        }
    }

    static float EstimateHeight(int kind, float area, float h)
    {
        switch (kind)
        {
            case 1: return area > 700f ? 12f + 9f * h : area > 250f ? 8f + 5f * h : 5.5f + 3f * h;
            case 2: return 8f + 8f * h;
            case 3: return 7f + 5f * h;
            case 4: return 10f + 6f * h;
            case 5: return 10f + 6f * h;
            case 6: return 12f + 8f * h;
            case 7: return 2.6f + 1.2f * h;
            case 8: return 18f + 20f * h;
            default: return area < 40f ? 3f + h : area < 250f ? 6f + 3f * h : 8f + 6f * h;
        }
    }
    static int Cell(int kind, float H, float area, float h)
    {
        int house = h < sty.brickShare ? CBrick : sty.house;
        switch (kind)
        {
            case 1: return H > 14f ? CFlats : house;
            case 2: return H > 11f || h > 0.5f ? COffice : CHouse;
            case 3: return CIndustry;
            case 4: return CConcrete;
            case 5: case 6: return CStone;
            case 7: return CShed;
            case 8: return H > 26f ? CTower : CFlats;
            default: return area < 40f ? CShed : H > 14f ? CFlats : area > 1500f ? CIndustry : house;
        }
    }
    static Color WallColour(int b, int cell, float h)
    {
        Color c;
        int pick = (int)(h * 9973f);
        if (cell == CBrick) c = sty.brick[pick % sty.brick.Length];
        else if (cell == COffice || cell == CIndustry || cell == CTower || cell == CShed || cell == CConcrete) c = Greys[pick % Greys.Length];
        else if (cell == CStone) c = C(0.97f, 0.95f, 0.90f);
        else c = sty.walls[pick % sty.walls.Length];
        uint rgba = D.bRgba[b];
        if ((rgba >> 24) != 0 && cell != CBrick && cell != CStone)
        {
            var oc = new Color((rgba & 255) / 255f, ((rgba >> 8) & 255) / 255f, ((rgba >> 16) & 255) / 255f);
            float lum = 0.3f * oc.r + 0.59f * oc.g + 0.11f * oc.b;
            if (lum < 0.3f) oc = Color.Lerp(oc, Color.white, (0.3f - lum) / 0.7f);
            c = Color.Lerp(oc, c, 0.35f);
        }
        float k = 0.93f + 0.1f * Hash(b, 77);
        return new Color(c.r * k, c.g * k, c.b * k, 1f);
    }

    static void Building(int b, int lod)
    {
        int n0 = D.bCnt[b], o = D.bOff[b];
        if (n0 > px.Length) { px = new float[n0]; pz = new float[n0]; }
        System.Array.Copy(D.bX, o, px, 0, n0); System.Array.Copy(D.bZ, o, pz, 0, n0);
        int n = WorldGeo.Clean(px, pz, n0); if (n < 3) return;
        n = WorldGeo.Simplify(px, pz, n, lod == 0 ? 0.25f : lod == 1 ? 0.7f : 1.8f, true);
        n = WorldGeo.Clean(px, pz, n); if (n < 3) return;
        float area = WorldGeo.Area(px, pz, n); if (area < 4f) return;
        WorldGeo.Obb(px, pz, n, out float ocx, out float ocz, out float ax, out float az, out float hl, out float hs, out float fill);
        if (lod == 2 && n > 8 && fill > 0.6f)
        {
            // far away: the footprint's rectangle
            float bx = -az, bz = ax;
            px[0] = ocx - ax * hl - bx * hs; pz[0] = ocz - az * hl - bz * hs;
            px[1] = ocx + ax * hl - bx * hs; pz[1] = ocz + az * hl - bz * hs;
            px[2] = ocx + ax * hl + bx * hs; pz[2] = ocz + az * hl + bz * hs;
            px[3] = ocx - ax * hl + bx * hs; pz[3] = ocz - az * hl + bz * hs;
            n = WorldGeo.Clean(px, pz, 4); if (n < 3) return; fill = 1f;
        }
        float h0 = Hash(b, 1), h1 = Hash(b, 2), h2 = Hash(b, 3);
        int kind = D.bKind[b];
        float H = D.bH[b]; if (H <= 0.5f) H = EstimateHeight(kind, area, h0);
        H = Mathf.Min(H, 400f);
        float minH = D.bMinH[b]; if (minH >= H - 1f) minH = 0f;
        float gmin = float.MaxValue, gmax = float.MinValue, cx = 0, cz = 0;
        for (int k = 0; k < n; k++) { float g = Ground(px[k], pz[k]); gmin = Mathf.Min(gmin, g); gmax = Mathf.Max(gmax, g); cx += px[k]; cz += pz[k]; }
        cx /= n; cz /= n;
        float top = gmin + H;
        // roof shape: the data's, or a pitched roof on houses where the data has none (most houses in Europe)
        int roof = D.bRoof[b];
        bool rectish = fill > 0.72f && n <= 14;
        if (roof == 0 && rectish && area < 450f && H < 16f && minH <= 0f && (kind == 1 || kind == 0) && h1 < sty.pitched) roof = h2 < 0.55f ? 1 : 2;
        if ((roof == 1 || roof == 2 || roof == 4) && !rectish) roof = 5;
        if (roof == 3 && fill < 0.6f) roof = 5;
        float roofH = roof == 1 ? hs * 0.62f : roof == 2 ? hs * 0.55f : roof == 4 ? hs * 0.7f : roof == 3 ? hs * 0.85f : 0f;
        roofH = Mathf.Min(roofH, (H - minH) * 0.5f);
        if (roof >= 1 && roof <= 4 && roofH < 0.6f) { roof = 5; roofH = 0f; }
        float eave = top - roofH;
        float foot = minH > 0.5f ? gmin + minH : gmin - 0.7f - (gmax - gmin); // walls reach into the slope
        int cell = Cell(kind, H, area, h0);
        float bw = CellW[cell], fh = CellH[cell];
        Color wall = WallColour(b, cell, h1);
        Color roofC = roof == 0 ? sty.flat * (0.9f + 0.15f * h2) : roof == 3 ? (h2 < 0.5f ? C(0.42f, 0.58f, 0.52f) : C(0.55f, 0.56f, 0.58f)) : sty.tiles[(int)(h2 * 7919f) % sty.tiles.Length] * (0.92f + 0.12f * h1);
        roofC.a = 1f;
        var mb = Buf(SBuild, Block(cx, cz), n * 28 + 64);
        var cuv = CellUv(cell); var plaster = CellUv(CPlaster);
        bool split = lod == 0 && minH <= 0.5f && eave - gmin > 4f;
        Color footC = new Color(wall.r * 0.74f, wall.g * 0.74f, wall.b * 0.74f, 1f);
        bool parapet = roof == 0 && lod < 2 && cell != CShed && H > 4f;
        Color cornice = Color.Lerp(wall, Color.white, 0.35f); cornice.a = 1f;
        for (int k = 0; k < n; k++)
        {
            int k2 = (k + 1) % n;
            float x0 = px[k], z0 = pz[k], x1 = px[k2], z1 = pz[k2], dx = x1 - x0, dz = z1 - z0, len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 0.05f) continue;
            var outw = new Vector3(dz / len, 0f, -dx / len); // right of the edge: outside of a counter-clockwise ring
            float bays = len < bw * 0.55f ? 0f : Mathf.Max(1f, Mathf.Round(len / bw));
            var c2 = bays > 0 ? cuv : plaster; float uu = bays > 0 ? bays : 0f;
            if (split)
            {
                Wall(mb, x0, z0, x1, z1, foot, gmin + 2f, outw, uu, gmin, fh, c2, footC, wall);
                Wall(mb, x0, z0, x1, z1, gmin + 2f, eave, outw, uu, gmin, fh, c2, wall, wall);
            }
            else Wall(mb, x0, z0, x1, z1, foot, eave, outw, uu, gmin, fh, c2, lod == 2 ? wall : footC, wall);
            if (parapet)
            {
                Wall(mb, x0, z0, x1, z1, eave, eave + 0.8f, outw, 0f, gmin, fh, plaster, cornice, cornice);
                if (lod == 0) Wall(mb, x0, z0, x1, z1, eave + 0.05f, eave + 0.8f, -outw, 0f, gmin, fh, plaster, wall * 0.8f, cornice);
            }
        }
        float bx2 = -az, bz2 = ax; // short axis of the rectangle
        var A = new Vector3(ax, 0f, az); var Bv = new Vector3(bx2, 0f, bz2); var cen = new Vector3(ocx, 0f, ocz);
        float ov = lod < 2 ? 0.35f : 0f;
        switch (roof)
        {
            case 1: Gabled(mb, cen, A, Bv, hl, hs, eave, top, ov, roofC, wall, cuv, bw, fh, gmin); break;
            case 2: Hipped(mb, cen, A, Bv, hl, hs, eave, top, ov, roofC); break;
            case 4: Hipped(mb, cen, A, Bv, hs, hs, eave, top, ov, roofC, hl); break;
            case 3: FlatRoof(mb, n, eave, roofC, CFlatRoof); Dome(mb, cen, A, Bv, hl * 0.97f, hs * 0.97f, eave, roofH, roofC); break;
            default: FlatRoof(mb, n, roof == 0 ? eave : top, roofC, roof == 0 ? (cell == CIndustry || cell == CShed ? CMetal : CFlatRoof) : CTiles); break;
        }
    }

    // one wall quad from height y0 to y1 along an edge; uv in tiles (bays across, floors up from the ground gmin)
    static void Wall(MB b, float x0, float z0, float x1, float z1, float y0, float y1, Vector3 outw, float bays, float gmin, float fh, Vector2 cell, Color c0, Color c1)
    {
        if (y1 <= y0) return;
        Vector3 p0 = new Vector3(x0, y0, z0), p1 = new Vector3(x1, y0, z1), p2 = new Vector3(x1, y1, z1), p3 = new Vector3(x0, y1, z0);
        float v0 = bays > 0 ? (y0 - gmin) / fh : 0.5f, v1 = bays > 0 ? (y1 - gmin) / fh : 0.5f, u1 = bays > 0 ? bays : 0.5f, ua = bays > 0 ? 0f : 0.5f;
        Quad(b, p0, p1, p2, p3, outw, new Vector2(ua, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(ua, v1), cell, c0, c0, c1, c1);
    }
    // quad wound so its face points along "outward"
    static void Quad(MB b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 outward, Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3, Vector2 cell, Color c0, Color c1, Color c2, Color c3)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0)
        {
            var tp = p1; p1 = p3; p3 = tp; var tu = t1; t1 = t3; t3 = tu; var tc = c1; c1 = c3; c3 = tc;
        }
        int i = b.v.Count;
        b.v.Add(p0); b.v.Add(p1); b.v.Add(p2); b.v.Add(p3);
        b.uv.Add(t0); b.uv.Add(t1); b.uv.Add(t2); b.uv.Add(t3);
        for (int k = 0; k < 4; k++) b.uv2.Add(cell);
        b.col.Add(c0); b.col.Add(c1); b.col.Add(c2); b.col.Add(c3);
        b.tri.Add(i); b.tri.Add(i + 1); b.tri.Add(i + 2); b.tri.Add(i); b.tri.Add(i + 2); b.tri.Add(i + 3);
    }
    static void Tri(MB b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 outward, Vector2 t0, Vector2 t1, Vector2 t2, Vector2 cell, Color c)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0) { var tp = p1; p1 = p2; p2 = tp; var tu = t1; t1 = t2; t2 = tu; }
        int i = b.v.Count;
        b.v.Add(p0); b.v.Add(p1); b.v.Add(p2);
        b.uv.Add(t0); b.uv.Add(t1); b.uv.Add(t2);
        for (int k = 0; k < 3; k++) { b.uv2.Add(cell); b.col.Add(c); }
        b.tri.Add(i); b.tri.Add(i + 1); b.tri.Add(i + 2);
    }
    // flat roof over the (cleaned, counter-clockwise) footprint in px / pz
    static void FlatRoof(MB b, int n, float y, Color c, int cell)
    {
        roofTri.Clear();
        WorldGeo.Triangulate(px, pz, n, roofTri);
        int start = b.v.Count; var cu = CellUv(cell); float s = 1f / CellW[cell];
        for (int k = 0; k < n; k++) { b.v.Add(new Vector3(px[k], y, pz[k])); b.uv.Add(new Vector2(px[k] * s, pz[k] * s)); b.uv2.Add(cu); b.col.Add(c); }
        for (int k = 0; k + 2 < roofTri.Count; k += 3) { b.tri.Add(start + roofTri[k]); b.tri.Add(start + roofTri[k + 2]); b.tri.Add(start + roofTri[k + 1]); }
    }
    // gabled roof over the footprint's rectangle: ridge along the long axis A, gable walls at the ends
    static void Gabled(MB b, Vector3 c, Vector3 A, Vector3 Bv, float hl, float hs, float eave, float top, float ov, Color roofC, Color wall, Vector2 wallCell, float bw, float fh, float gmin)
    {
        float rh = top - eave, drop = ov * rh / Mathf.Max(hs, 0.1f), L = hl + ov, S = hs + ov, slope = Mathf.Sqrt(S * S + (rh + drop) * (rh + drop));
        Vector3 up = Vector3.up, ye = up * (eave - drop), yt = up * top;
        var tc = CellUv(CTiles); float tw = 1f / CellW[CTiles];
        for (int sd = -1; sd <= 1; sd += 2)
        {
            Vector3 e0 = c - A * L + Bv * (S * sd) + ye, e1 = c + A * L + Bv * (S * sd) + ye, r1 = c + A * L + yt, r0 = c - A * L + yt;
            Quad(b, e0, e1, r1, r0, Bv * sd + up, new Vector2(0, 0), new Vector2(2 * L * tw, 0), new Vector2(2 * L * tw, slope * tw), new Vector2(0, slope * tw), tc, roofC, roofC, roofC, roofC);
        }
        // gable triangles in the end walls
        for (int sd = -1; sd <= 1; sd += 2)
        {
            Vector3 g0 = c + A * (hl * sd) - Bv * hs + up * eave, g1 = c + A * (hl * sd) + Bv * hs + up * eave, ap = c + A * (hl * sd) + yt;
            float uw = Mathf.Max(1f, Mathf.Round(2f * hs / bw));
            Tri(b, g0, g1, ap, A * sd, new Vector2(0, (eave - gmin) / fh), new Vector2(uw, (eave - gmin) / fh), new Vector2(uw * 0.5f, (top - gmin) / fh), wallCell, wall);
        }
    }
    // hipped roof (ridge = long half-size minus short half-size); a pyramid when the ridge is that short. hlPyr: the
    // real long half-size for a pyramidal roof on a long rectangle (called with hl = hs, so the ridge is a point)
    static void Hipped(MB b, Vector3 c, Vector3 A, Vector3 Bv, float hl, float hs, float eave, float top, float ov, Color roofC, float hlPyr = -1f)
    {
        float baseL = hlPyr > 0 ? hlPyr : hl;
        float rh = top - eave, drop = ov * rh / Mathf.Max(hs, 0.1f), L = baseL + ov, S = hs + ov, rl = Mathf.Max(0f, hl - hs);
        Vector3 up = Vector3.up, ye = up * (eave - drop), yt = up * top;
        Vector3 e00 = c - A * L - Bv * S + ye, e10 = c + A * L - Bv * S + ye, e11 = c + A * L + Bv * S + ye, e01 = c - A * L + Bv * S + ye;
        Vector3 r0 = c - A * rl + yt, r1 = c + A * rl + yt;
        var tc = CellUv(CTiles); float tw = 1f / CellW[CTiles];
        float sl = Mathf.Sqrt(S * S + (rh + drop) * (rh + drop)) * tw, el = Mathf.Sqrt((L - rl) * (L - rl) + (rh + drop) * (rh + drop)) * tw;
        Quad(b, e00, e10, r1, r0, -Bv + up, new Vector2(0, 0), new Vector2(2 * L * tw, 0), new Vector2((L + rl) * tw, sl), new Vector2((L - rl) * tw, sl), tc, roofC, roofC, roofC, roofC);
        Quad(b, e11, e01, r0, r1, Bv + up, new Vector2(0, 0), new Vector2(2 * L * tw, 0), new Vector2((L + rl) * tw, sl), new Vector2((L - rl) * tw, sl), tc, roofC, roofC, roofC, roofC);
        Tri(b, e01, e00, r0, -A + up, new Vector2(0, 0), new Vector2(2 * S * tw, 0), new Vector2(S * tw, el), tc, roofC);
        Tri(b, e10, e11, r1, A + up, new Vector2(0, 0), new Vector2(2 * S * tw, 0), new Vector2(S * tw, el), tc, roofC);
    }
    // a low dome over the ellipse inside the rectangle (smooth: shared vertices)
    static void Dome(MB b, Vector3 c, Vector3 A, Vector3 Bv, float ra, float rb, float eave, float rh, Color col)
    {
        const int Seg = 12, Rings = 4;
        int start = b.v.Count; var cu = CellUv(CMetal);
        for (int r = 0; r <= Rings; r++)
        {
            float th = r * Mathf.PI * 0.5f / Rings, k = Mathf.Cos(th), y = eave + rh * Mathf.Sin(th);
            for (int s = 0; s <= Seg; s++)
            {
                float a = s * Mathf.PI * 2f / Seg;
                b.v.Add(c + A * (Mathf.Cos(a) * ra * k) + Bv * (Mathf.Sin(a) * rb * k) + Vector3.up * y);
                b.uv.Add(new Vector2(s * 0.5f, r * 0.5f)); b.uv2.Add(cu); b.col.Add(col);
            }
        }
        // (A, Bv) is a right-handed pair seen from above, so the ring runs counter-clockwise: wind for Unity
        for (int r = 0; r < Rings; r++)
            for (int s = 0; s < Seg; s++)
            {
                int k0 = start + r * (Seg + 1) + s, k1 = k0 + 1, k2 = k0 + Seg + 1, k3 = k2 + 1;
                b.tri.Add(k0); b.tri.Add(k2); b.tri.Add(k1); b.tri.Add(k1); b.tri.Add(k2); b.tri.Add(k3);
            }
    }

    // ---------------------------------------------------------------- trees (Environment's card atlas)
    struct TCand { public float x, z, h, pr; public byte src; }
    static void Trees()
    {
        if (!HasVegetation) return; // (no vegetation in the data: Environment.Trees planted the usual ones)
        int budget = TreeBudget; float reach = TreeReach, near = wallLat + (street ? 1.5f : 3f);
        var cand = new List<TCand>();
        for (int i = 0; i < D.tN; i++)
        {
            float x = D.tX[i], z = D.tZ[i]; if (x < rx0 || x > rx1 || z < rz0 || z > rz1) continue;
            float d = Dist(x, z); if (d > reach) continue;
            cand.Add(new TCand { x = x, z = z, h = D.tH[i], pr = d - 150f, src = 0 });
        }
        var rnd = new System.Random(11);
        for (int j = 0; j < gnz - 1; j++)
            for (int i = 0; i < gnx - 1; i++)
            {
                int cc = Mathf.Min(cnz - 1, j / CH) * cnx + Mathf.Min(cnx - 1, i / CH);
                if (cDist[cc] > reach) { i = (i / CH + 1) * CH - 1; continue; } // a whole chunk out of reach
                float x = rx0 + (i + 0.5f) * B, z = rz0 + (j + 0.5f) * B;
                float d = Dist(x, z); if (d > reach || d < near * 0.5f) continue;
                int kind = KindAt(x, z, out int id); if (kind < 0) continue;
                float p; byte src = 2;
                int k = j * gnx + i;
                switch (kind)
                {
                    case 1: { float cn = gCan[k]; p = cn < 0.55f ? (d < 200f ? 1.6f : 1.0f) : 0f; src = 1; break; }
                    case 8: p = 0.45f; break;
                    case 3: p = 0.16f; break;
                    case 0: p = 0.05f; break;
                    case 10: p = 0.08f; break;
                    case 2: p = 0.012f; break;
                    case 4: p = 0.03f; break;
                    case 6: p = env.tree == TreeKind.Scrub || env.tree2 == TreeKind.Scrub ? 0.12f : 0.02f; break;
                    default: p = 0f; break;
                }
                if (p <= 0f) continue;
                if (d > 300f) p *= 0.6f;
                int cnt = (int)p + (rnd.NextDouble() < p - (int)p ? 1 : 0);
                for (int q = 0; q < cnt; q++)
                    cand.Add(new TCand { x = x + ((float)rnd.NextDouble() - 0.5f) * B, z = z + ((float)rnd.NextDouble() - 0.5f) * B, h = 0f, pr = d + (src == 1 ? 0f : 60f), src = src });
            }
        cand.Sort((a, c) => a.pr.CompareTo(c.pr));
        bool low = gfx <= 1; int cards = low ? 2 : 3; float ch = CanopyHeight();
        bool med = env.tree == TreeKind.Palm || env.tree == TreeKind.Pine; // Mediterranean towns: palms and pines in the streets too
        foreach (var t in cand)
        {
            if (TreeCount >= budget) break;
            float x = t.x, z = t.z;
            float d = Dist(x, z);
            if (d < near + 14f && TrackDist(x, z, near) < near) continue;
            if (Trackside.Blocked(x, z, 3f) || PadDist(x, z) < 8f) continue;
            if (Waterfront.HasWater && Waterfront.InWater(x, z)) continue;
            int kind = KindAt(x, z, out int id); if (kind == 5) continue;
            if (Flagged(x, z) || Flagged(x + 2.5f, z) || Flagged(x - 2.5f, z) || Flagged(x, z + 2.5f) || Flagged(x, z - 2.5f)) continue;
            if (!Environment.Free(x, z, 3f)) continue;
            // kind and size
            TreeKind tk;
            float r = (float)rnd.NextDouble();
            if (t.src == 1) tk = env.tree2 != TreeKind.None && r < env.tree2Share ? env.tree2 : env.tree;
            else if (med) tk = env.tree2 != TreeKind.None && r < env.tree2Share ? env.tree2 : env.tree;
            else tk = env.tree == TreeKind.Scrub && r < 0.5f ? TreeKind.Scrub : TreeKind.Broadleaf;
            if (tk == TreeKind.None) tk = TreeKind.Broadleaf;
            float bh = BaseH(tk), sz;
            if (t.h > 0f) sz = t.h / bh;
            else if (t.src == 1 && tk != TreeKind.Scrub) sz = ch / bh * (0.9f + 0.35f * (float)rnd.NextDouble());
            else sz = (0.7f + 0.8f * (float)rnd.NextDouble()) * env.treeSize;
            sz = Mathf.Clamp(sz, 0.4f, 2.6f);
            if (tk != TreeKind.Scrub && d < near + 14f) sz = Mathf.Min(sz, 1.25f * Mathf.Max(1f, env.treeSize * 0.75f)); // no giant right at the fence
            float y = Mathf.Min(Ground(x, z), MeshY(x, z)) - 0.35f;
            Card(Buf(STree, Block(x, z), 12), new Vector3(x, y, z), tk, sz, (float)rnd.NextDouble() * 6.28f, (float)rnd.NextDouble() * 6.28f, t.src == 1, rnd, cards);
            TreeCount++;
        }
    }
    static bool Flagged(float x, float z) { int k = Texel(x, z); return k >= 0 && lflag[k] != 0; }

    // crossed cards cut from the atlas (as Environment.Trees: foot-left, foot-right, top-right, top-left per card,
    // normals bent outwards and up, uv2 = sway weight and phase for Foliage.shader)
    static void Card(MB b, Vector3 foot, TreeKind kind, float sz, float rot, float phase, bool second, System.Random rnd, int cards)
    {
        float w, h, vFrom = 0f, sway;
        switch (kind)
        {
            case TreeKind.Spruce: w = 7.2f * sz; h = 15f * sz; sway = 0.45f; break;
            case TreeKind.Pine: w = 9.5f * sz; h = 9.6f * sz; sway = 0.6f; break;
            case TreeKind.Palm: w = 8.4f * sz; h = 9.4f * sz; sway = 1.2f; break;
            case TreeKind.Scrub: w = 3.8f * sz; h = 2.2f * sz; vFrom = 0.42f; sway = 0.25f; foot += Vector3.down * 0.2f; break;
            default: w = 8.6f * sz; h = 10f * sz; sway = 0.8f; break;
        }
        var card = kind == TreeKind.Spruce ? EnvTex.Card.Spruce : kind == TreeKind.Pine ? EnvTex.Card.Pine : kind == TreeKind.Palm ? EnvTex.Card.Palm : EnvTex.Card.Broadleaf;
        var rc = EnvTex.CardRect(card); var painted = EnvTex.CardLeaf[(int)card];
        float sh = 0.86f + (float)rnd.NextDouble() * 0.28f, warm = (float)rnd.NextDouble() * 0.12f;
        var lc = (second && rnd.NextDouble() < env.tree2Share ? env.leaf2 : env.leaf).linear;
        if (painted.g <= 0f) painted = new Color(0.1f, 0.2f, 0.1f);
        Color tint = new Color(Mathf.Clamp(lc.r / painted.r, 0.55f, 1.8f) * sh * (1f + warm), Mathf.Clamp(lc.g / painted.g, 0.55f, 1.8f) * sh,
                               Mathf.Clamp(lc.b / painted.b, 0.55f, 1.8f) * sh * (1f - warm * 0.5f), 1f);
        Color footC = tint * 0.85f; footC.a = 1f;
        float v0 = Mathf.Lerp(rc.y, rc.w, vFrom);
        for (int c = 0; c < cards; c++)
        {
            float a = rot + c * Mathf.PI / cards;
            var dv = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (w * 0.5f);
            int k = b.v.Count;
            Vector3 b0 = foot - dv, b1 = foot + dv, t1 = b1 + Vector3.up * h, t0 = b0 + Vector3.up * h;
            b.v.Add(b0); b.v.Add(b1); b.v.Add(t1); b.v.Add(t0);
            b.uv.Add(new Vector2(rc.x, v0)); b.uv.Add(new Vector2(rc.z, v0)); b.uv.Add(new Vector2(rc.z, rc.w)); b.uv.Add(new Vector2(rc.x, rc.w));
            var side = dv.normalized;
            b.n.Add((-side * 0.75f + Vector3.up * 0.1f).normalized); b.n.Add((side * 0.75f + Vector3.up * 0.1f).normalized);
            b.n.Add((side * 0.5f + Vector3.up * 0.85f).normalized); b.n.Add((-side * 0.5f + Vector3.up * 0.85f).normalized);
            b.uv2.Add(new Vector2(0, phase)); b.uv2.Add(new Vector2(0, phase)); b.uv2.Add(new Vector2(sway, phase)); b.uv2.Add(new Vector2(sway, phase));
            b.col.Add(footC); b.col.Add(footC); b.col.Add(tint); b.col.Add(tint);
            b.tri.Add(k); b.tri.Add(k + 2); b.tri.Add(k + 1); b.tri.Add(k); b.tri.Add(k + 3); b.tri.Add(k + 2);
        }
    }

    // ---------------------------------------------------------------- the land colour map
    static float[] noiseTab; static byte[] toSrgb;
    static void Tables()
    {
        if (noiseTab != null) return;
        noiseTab = new float[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                // tileable value noise, three octaves
                float s = 0, amp = 1, norm = 0; int c = 8;
                for (int o = 0; o < 3; o++)
                {
                    float fx = x / 256f * c, fy = y / 256f * c; int i = (int)fx, j = (int)fy; float a = fx - i, b = fy - j;
                    a = a * a * (3 - 2 * a); b = b * b * (3 - 2 * b);
                    int i1 = (i + 1) % c, j1 = (j + 1) % c;
                    s += amp * Mathf.Lerp(Mathf.Lerp(Hash(i + o * 977, j), Hash(i1 + o * 977, j), a), Mathf.Lerp(Hash(i + o * 977, j1), Hash(i1 + o * 977, j1), a), b);
                    norm += amp; amp *= 0.5f; c *= 2;
                }
                noiseTab[y * 256 + x] = s / norm;
            }
        toSrgb = new byte[1025];
        for (int i = 0; i <= 1024; i++) toSrgb[i] = (byte)Mathf.Clamp(Mathf.Pow(i / 1024f, 1f / 2.2f) * 255f + 0.5f, 0, 255);
    }
    static byte S(float lin) { int i = (int)(lin * 1024f); return toSrgb[i < 0 ? 0 : i > 1024 ? 1024 : i]; }

    static Texture2D LandTexture()
    {
        Tables();
        bool green = EnvTex.IsGreen(env.grass);
        Color grass = (green ? new Color(env.grass.r * 1.12f, env.grass.g * 1.08f, env.grass.b * 0.95f) : env.grass).linear;
        Color dry = env.dry.linear, soil = env.soil.linear;
        Color dryK = new Color(dry.r / Mathf.Max(grass.r, 1e-3f), dry.g / Mathf.Max(grass.g, 1e-3f), dry.b / Mathf.Max(grass.b, 1e-3f));
        int na = D.aN; var pc = new Color[na + 1]; var pa = new byte[na + 1];
        pc[0] = grass; pa[0] = 85;
        for (int p = 0; p < na; p++) { Palette(D.aKind[p], p, grass, dry, soil, out pc[p + 1], out pa[p + 1]); }
        Color leafA = env.leaf.linear * 0.62f, leafB = env.leaf2.linear * 0.62f;
        var px = new Color32[lnx * lnz];
        float patch = Mathf.Clamp01(env.patch * 1.4f);
        for (int j = 0; j < lnz; j++)
        {
            float z = rz0 + (j + 0.5f) * lcz; int nz1 = ((int)(z * 0.2f) & 255) << 8, nz2 = ((int)(z * 0.031f + 41f) & 255) << 8;
            float ez = Mathf.Min(z - rz0, rz1 - z);
            for (int i = 0; i < lnx; i++)
            {
                float x = rx0 + (i + 0.5f) * lcx; int k = j * lnx + i, id = lid[k];
                int kind = id == 0 ? 0 : D.aKind[id - 1];
                Color c = pc[id]; byte a = pa[id];
                float n1 = noiseTab[nz1 + ((int)(x * 0.2f) & 255)], n2 = noiseTab[nz2 + ((int)(x * 0.031f + 97f) & 255)];
                if (kind == 0 || kind == 3 || kind == 8 || kind == 10)
                {
                    float t = Smooth((n2 - 0.55f) / 0.15f) * patch;
                    c = new Color(c.r * (1f + (dryK.r - 1f) * t), c.g * (1f + (dryK.g - 1f) * t), c.b * (1f + (dryK.b - 1f) * t));
                }
                else if (kind == 1)
                {
                    float cn = Bilinear(gCan, x, z);
                    if (cn > 0.3f) { c = Color.Lerp(leafA, leafB, env.tree2Share * n2); a = 255; }
                }
                float bright = 0.9f + 0.2f * n1;
                float e = Mathf.Min(Mathf.Min(x - rx0, rx1 - x), ez);
                if (e < 150f) { float u = e / 150f; c = Color.Lerp(grass, c, u); if (u < 0.5f) a = 85; }
                px[k] = new Color32(S(c.r * bright), S(c.g * bright), S(c.b * bright), a);
            }
        }
        var tex = new Texture2D(lnx, lnz, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4, name = "RealLand" };
        tex.SetPixels32(px); tex.Apply(true, true);
        return tex;
    }
    // colour (linear albedo) and ground detail of a land-use polygon (alpha: 0 paved, 85 grass, 170 rough, 255 canopy)
    static void Palette(int kind, int p, Color grass, Color dry, Color soil, out Color c, out byte a)
    {
        float h = Hash(p, 501), k = 0.94f + 0.12f * Hash(p, 503);
        switch (kind)
        {
            case 0: c = grass; a = 85; break;
            case 1: c = Color.Lerp(env.leaf.linear * 0.55f, soil, 0.35f); a = 170; break;
            case 2:
                c = h < 0.3f ? dry : h < 0.5f ? C(0.62f, 0.52f, 0.30f).linear : h < 0.7f ? C(0.40f, 0.31f, 0.22f).linear : h < 0.9f ? grass * 1.05f : C(0.55f, 0.55f, 0.36f).linear;
                a = 170; break;
            case 3: c = Color.Lerp(grass, C(0.46f, 0.45f, 0.43f).linear, 0.45f); a = 85; break;
            case 4: c = C(0.52f, 0.51f, 0.49f).linear; a = 0; break;
            case 5: c = C(0.12f, 0.17f, 0.18f).linear; a = 0; break;
            case 6: c = Color.Lerp(C(0.72f, 0.65f, 0.50f).linear, soil, 0.4f); a = 170; break;
            case 7: c = C(0.36f, 0.36f, 0.37f).linear; a = 0; break;
            case 8: c = grass * 1.06f; a = 85; break;
            case 9: c = C(0.56f, 0.53f, 0.49f).linear; a = 170; break;
            case 10: c = C(0.30f, 0.36f, 0.22f).linear; a = 85; break;
            case 11: c = C(0.33f, 0.33f, 0.34f).linear; a = 0; break;
            default: c = grass; a = 85; break;
        }
        c = new Color(c.r * k, c.g * k, c.b * k, 1f);
    }

    // ---------------------------------------------------------------- materials and objects
    static Material LandMaterial(Texture2D landTex)
    {
        var sh = Resources.Load<Shader>("Baked");
        float w = rx1 - rx0, h = rz1 - rz0;
        if (WorldBuilder.BakeReady && sh != null)
        {
            var m = new Material(sh) { mainTexture = EnvTex.LandDetail(), name = "RealLand" };
            m.EnableKeyword("F2_LAND");
            m.SetTexture("_LandTex", landTex); m.SetTexture("_LandMacro", EnvTex.LandMacro());
            m.SetVector("_LandRect", new Vector4(rx0, rz0, 1f / w, 1f / h));
            m.SetFloat("_DetailScale", 1f / 4.5f);
            return m;
        }
        // (no baked lighting: the plain diffuse shader with the colour map stretched over the area)
        var d = WorldBuilder.Mat(Color.white, landTex);
        d.mainTextureScale = new Vector2(1f / w, 1f / h); d.mainTextureOffset = new Vector2(-rx0 / w, -rz0 / h);
        return d;
    }
    static Material BuildingMaterial()
    {
        var sh = Resources.Load<Shader>("Baked");
        if (WorldBuilder.BakeReady && sh != null)
        {
            var m = new Material(sh) { mainTexture = EnvTex.BuildingAtlas(), name = "RealBuildings" };
            m.EnableKeyword("F2_ATLAS");
            return m;
        }
        return WorldBuilder.Mat(new Color(0.9f, 0.86f, 0.78f), EnvTex.Facade(), 0.2f);
    }
    static Material TreeMaterial()
    {
        var atlas = EnvTex.Foliage();
        if (WorldBuilder.FoliageShader != null) { var m = new Material(WorldBuilder.FoliageShader) { mainTexture = atlas }; m.SetFloat("_Cutoff", 0.5f); return m; }
        return WorldBuilder.Mat(Color.white, atlas);
    }

    static void Emit(Transform root, Texture2D landTex)
    {
        var holder = new GameObject("RealWorld").transform; holder.SetParent(root, false);
        var mats = new Material[5];
        mats[SLand] = LandMaterial(landTex); mats[SWater] = Waterfront.WaterMaterial();
        mats[SRoad] = WorldBuilder.Mat(Color.white, EnvTex.RoadAtlas(), 0.1f);
        mats[SBuild] = BuildingMaterial(); mats[STree] = TreeMaterial();
        string[] names = { "EnvRealLand", "EnvRealWater", "EnvRealRoads", "EnvRealBuildings", "Trees" }; // ("Trees": LightBake reads the cards as crowns)
        float bCull = gfx <= 1 ? 900f : gfx == 2 ? 1400f : 1900f, tCull = gfx <= 1 ? 500f : gfx == 2 ? 750f : 950f;
        int blocks = ((cnx + EB - 1) / EB) * ((cnz + EB - 1) / EB);
        var far = new List<Renderer>(); var trees = new List<Renderer>();
        for (int blk = 0; blk < blocks; blk++)
        {
            Transform farT = null, treeT = null; far.Clear(); trees.Clear();
            for (int slot = 0; slot < 5; slot++)
            {
                if (!bufs.TryGetValue(blk * 8 + slot, out var list)) continue;
                foreach (var b in list)
                {
                    if (b.tri.Count == 0) continue;
                    Transform parent = holder;
                    if (slot == SRoad || slot == SBuild) { if (farT == null) farT = Child(holder, "RealNear" + blk); parent = farT; }
                    else if (slot == STree) { if (treeT == null) treeT = Child(holder, "RealTrees" + blk); parent = treeT; }
                    var r = Make(b, names[slot], mats[slot], parent);
                    if (slot == SRoad || slot == SBuild) far.Add(r); else if (slot == STree) trees.Add(r);
                }
            }
            if (farT != null) Cull(farT.gameObject, far, bCull);
            if (treeT != null) Cull(treeT.gameObject, trees, tCull);
        }
    }
    static Transform Child(Transform parent, string name) { var go = new GameObject(name); go.transform.SetParent(parent, false); return go.transform; }
    // switched off beyond "dist" metres (at the normal field of view; zooming in brings it back), by the engine
    static void Cull(GameObject go, List<Renderer> rs, float dist)
    {
        var lg = go.AddComponent<LODGroup>();
        var arr = rs.ToArray();
        lg.SetLODs(new[] { new LOD(0.01f, arr) }); lg.RecalculateBounds();
        float thr = Mathf.Clamp(lg.size / (2f * dist * 0.6f), 0.002f, 0.9f); // tan(31 deg) = 0.6: the 62 degree chase view
        lg.SetLODs(new[] { new LOD(thr, arr) });
    }
    static Renderer Make(MB b, string name, Material m, Transform parent)
    {
        var mesh = new Mesh { name = name };
        mesh.indexFormat = b.v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(b.v);
        if (b.n.Count == b.v.Count) mesh.SetNormals(b.n);
        mesh.SetUVs(0, b.uv);
        if (b.uv2.Count == b.v.Count) mesh.SetUVs(2, b.uv2); // (channel 2: static batching may use channel 1 for lightmaps)
        mesh.SetTriangles(b.tri, 0, true);
        if (b.n.Count != b.v.Count) mesh.RecalculateNormals();
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = true;
        if (b.col.Count == b.v.Count) BakeTint.Add(mesh, b.col.ToArray());
        Vertices += b.v.Count;
        return r;
    }
}
