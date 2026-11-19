// RealWorld.cs — the real surroundings of a circuit, built from Resources/World/<Name>.bytes (WorldData.cs: buildings,
// land use, roads and trees from OpenStreetMap / Overture Maps, and a terrain height grid). Without a file the circuit
// keeps its procedural scenery (Environment.cs) exactly as before.
// What it builds, in world coordinates, lit by LightBake like the rest of the world:
//  * terrain: the real heights on a regular grid (8 m; 10 m on Medium, 12 m on Low), blended into the circuit's own height field near
//    the track so it never rises through the road, the verges, the stands or the test pad. Cut into 512 m chunks that
//    get coarser away from the track (2, 4, 8 cells per step), with skirts where neighbouring chunks differ.
//  * ground colour: the land-use polygons painted into one colour map (Baked.shader F2_LAND: the map times a
//    world-space detail texture: grass, paving, rough ground or tree canopy, picked per texel).
//  * woods: forest polygons stay flat (a dark forest floor); card trees (Environment's atlas, Foliage.shader) stand
//    in rows along the wood edges near the track, in parks and gardens, and at the mapped single trees, and two rows of
//    forest-edge band cards a few tree rows into the wood stand for the forest beyond (no raised canopy mass: it read
//    as faceted green hills).
//  * water: lakes, rivers and sea as flat surfaces with the harbour's look, the ground carved below them.
//  * roads, paths and railways: flat ribbons on the terrain (one atlas), kept level over water like a bridge.
//  * buildings: extruded footprints with flat roofs and parapets, gabled, hipped, pyramidal or domed roofs; facades
//    from an 8 x 4 tile atlas (EnvTex.BuildingAtlas: CC0 photo plaster, brick, coppi, cladding, panels) by building
//    class: homes with shutters or roller blinds over a ground floor of barred windows, doors, shops or garages; flats,
//    offices, metal-clad industrial sheds with skylights, garages. Storeys snap to whole floors (homes 3.2 m) so the
//    window grid is regular; single bays vary (closed shutters, doors) in the shader. Colour per building from the
//    circuit's palette (Monza: Lombardy plaster colours) in uv3, so glass, frames and shutters keep their own. Within
//    NearTier of the track (400 m on High): split ground floor, projecting cornice, eave soffits with rafters,
//    rooftop units; light corner trims everywhere (shader). Farther: one quad per wall, footprint rectangles.
//  * the open land (round 5): gentle swells away from the track (Undulation), service roads with marshal huts and
//    parked service vehicles behind the barrier in the open stretches (ServicePlan), hedgerows, copses and bushes in
//    the fields and parks (Outfield, own budget), and opaque far tree-line silhouettes along the wood edges out to the
//    fog's end (FarTrees, one extra mesh).
// Cost: one mesh per material per terrain-chunk block (512-768 m; land, water, roads, buildings, trees), static batched; buildings,
// roads and trees switched off by distance with LODGroups (no script per frame); plus one far tree-line mesh. Budgets
// per graphics level keep the vertex count (and so the light bake) small; see Budget().
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
    static float[] gGround, gTop, gW, gCan, gEff; // ground, terrain mesh height (= ground), 0 on the circuit's ground .. 1 real terrain, woods 0..1, depth into the woods (m)
    static float[] gFd, gFloor; // depth into the mapped woods (m, the circuit's ground included), forest-floor mask 0..1 (FloorMask)
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
        public int house = 1;          // homes: 0 mostly shutters (persiane), 1 more roller blinds
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
                // Lombardy: cream, ochre, Milan yellow, salmon, faded brick red, white and grey plaster; terracotta coppi
                s.walls = new[] { C(0.95f, 0.88f, 0.70f), C(0.93f, 0.74f, 0.44f), C(0.96f, 0.86f, 0.56f), C(0.90f, 0.63f, 0.49f), C(0.80f, 0.50f, 0.36f),
                                  C(0.90f, 0.89f, 0.85f), C(0.88f, 0.80f, 0.66f), C(0.95f, 0.93f, 0.88f), C(0.92f, 0.80f, 0.60f) };
                s.house = 0; s.brickShare = 0.08f; s.pitched = 0.6f;
                s.tiles = new[] { C(0.80f, 0.37f, 0.21f), C(0.74f, 0.36f, 0.22f), C(0.68f, 0.40f, 0.28f), C(0.60f, 0.38f, 0.30f) }; break;
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
        ModelLib.Reset(); // (real tree / shrub / lamp models are queued per build)
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
        ServicePlan(t); Mark("service");
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
    // Gentle swells in the open land away from the track (the map's heights are a coarse, smoothed grid: dead flat
    // fields read as a painted sheet). None on the circuit's own ground; growing over the first ~300 m beyond it, so
    // the far fields roll a little and catch the sun on their slopes (the bake lights them by the terrain's normals).
    // Low and wide (+-2 m over ~400 m, +-0.8 m over ~120 m): buildings, roads, water and trees all take their heights
    // from this ground afterwards, so nothing floats. Flatter in water polygons (rivers keep one level).
    static float und;
    static float Undulation(float x, float z, float d)
    {
        float far = Smooth((d - c0 - 20f) / 300f);
        if (far <= 0f) return 0f;
        int kind = KindAt(x, z, out _);
        if (kind == 5) return 0f;
        float s = (Noise(x / 410f, z / 410f, 71) - 0.5f) * 4.2f + (Noise(x / 125f + 3.1f, z / 125f, 73) - 0.5f) * 1.6f;
        if (kind == 2) s *= 0.6f; // farmland is levelled
        return s * und * far;
    }
    static void Heights()
    {
        und = street || env == null ? 0f : Mathf.Clamp(env.rough / 1.2f, 0.4f, 1.8f);
        int n = gnx * gnz; gGround = new float[n]; gW = new float[n];
        var hf = WorldBuilder.Height; float lowR = B * 1.1f + 2f;
        for (int j = 0; j < gnz; j++)
        {
            float z = rz0 + j * B;
            for (int i = 0; i < gnx; i++)
            {
                float x = rx0 + i * B; int k = j * gnx + i;
                float y = HasTerrain ? D.Terrain(x, z) : (hf == null ? -0.1f : WorldBuilder.H(x, z) - 0.15f);
                float d = Dist(x, z);
                if (und > 0f) y += Undulation(x, z, d);
                if (HasTerrain)
                {
                    float e = Mathf.Min(Mathf.Min(x - rx0, rx1 - x), Mathf.Min(z - rz0, rz1 - z));
                    if (e < 250f) y = Mathf.Lerp(edgeLevel, y, Smooth(e / 250f));
                }
                float w = Smooth((d - c0) / ramp);
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

    // ---------------------------------------------------------------- woods: how deep into the forest polygons each grid point is
    static float CanopyHeight()
    {
        if (env.canopy > 0) return env.canopy;
        return Mathf.Max(6f, BaseH(env.tree) * env.treeSize * 0.85f);
    }
    static float BaseH(TreeKind k) => k == TreeKind.Spruce ? 15f : k == TreeKind.Pine ? 9.6f : k == TreeKind.Palm ? 9.4f : k == TreeKind.Scrub ? 2.2f : 10f;
    static void Canopy()
    {
        int n = gnx * gnz; gCan = new float[n]; gTop = new float[n]; gEff = new float[n]; gFd = new float[n];
        var fd = new float[n]; bool any = false, anyRaw = false;
        for (int j = 0; j < gnz; j++)
            for (int i = 0; i < gnx; i++)
            {
                int k = j * gnx + i, t = Texel(rx0 + i * B, rz0 + j * B);
                bool raw = t >= 0 && lid[t] != 0 && D.aKind[lid[t] - 1] == 1 && lflag[t] == 0, f = raw && gW[k] > 0f;
                fd[k] = f ? 1e6f : 0f; any |= f;
                gFd[k] = raw ? 1e6f : 0f; anyRaw |= raw;
            }
        // the depth into the mapped woods without the circuit's ground taken off (the undergrowth and the forest floor)
        if (anyRaw) Chamfer(gFd);
        if (!any) { System.Array.Copy(gGround, gTop, n); return; }
        // (ch, the canopy height, now sizes the trees and bands only)
        Chamfer(fd);
        for (int j = 0; j < gnz; j++)
            for (int i = 0; i < gnx; i++)
            {
                int k = j * gnx + i; float c = 0f, eff = 0f;
                if (fd[k] > 0f)
                {
                    float x = rx0 + i * B, z = rz0 + j * B;
                    // the wood begins a little beyond the circuit's own ground, not on the long terrain ramp
                    float dd = Dist(x, z);
                    eff = gW[k] > 0f ? Mathf.Max(0f, Mathf.Min(fd[k] - 4f, dd - c0 + 2f)) : 0f;
                    if (PadDist(x, z) < 40f || (eff > 0f && Environment.Reserved(x, z, 14f))) eff = 0f; // clearings round the landmarks
                    c = Smooth(eff / 12f);
                }
                gTop[k] = gGround[k]; // (flat: no canopy mass)
                gCan[k] = c; gEff[k] = eff;
            }
    }
    // chamfer distance (m) from the edge of the marked cells (1e6 inside, 0 outside), in place
    static void Chamfer(float[] fd)
    {
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
    }

    // ---------------------------------------------------------------- terrain LOD per chunk (by distance to the track)
    static void ChunkSteps()
    {
        cnx = (gnx - 2) / CH + 1; cnz = (gnz - 2) / CH + 1;
        EB = 1; // mesh blocks of one terrain chunk (512-768 m; was 1-1.3 km): the frustum and the LODGroup distance
                // cull of buildings, roads and trees work on 4x smaller pieces (a block 700 m away no longer keeps
                // trees 1.7 km away drawn); static batching keeps the extra draws free of state changes
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
        public readonly List<Vector4> t4 = new List<Vector4>(); // uv3 of building vertices: colour + wall length (Baked.shader F2_ATLAS)
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
            ServiceMeshes(); Mark("service");
            Buildings(); Mark("buildings");
            Trees(); Mark("trees");
            VergeFloor(root); // (the forest-floor mask onto the verge, Ground.cginc)
            if (HasVegetation) { Outfield(); FarTrees(); Mark("outfield"); }
            ModelLib.Flush(root); Mark("models"); // (final heights, like the rest of RealWorld)
            var land = LandTexture(); Mark("landtex");
            Emit(root, land); Mark("emit");
        }
        catch (System.Exception e) { Debug.LogError("RealWorld: " + e); }
        bufs.Clear();
        FreeGrids(); D = null; // (the flags HasBuildings ... stay for the rest of this load)
        LoadMs += (float)sw.Elapsed.TotalMilliseconds;
        Debug.Log("RealWorld " + t.def.id + ": " + BuildingCount + " buildings, " + TreeCount + " trees, " + BandCount + " forest-edge cards, " + OutfieldCount + " hedge/copse plants, "
                  + FarCount + " far tree-line strips, " + svcRuns.Count + " service roads, " + svcProps.Count + " huts/vehicle groups, " + LampCount + " park lamps, " + BenchCount + " benches, undergrowth " + UBush + " bushes / " + UBramble + " brambles / " + UYoung + " young trees / " + UFern + " ferns / " + UClump + " clumps / " + UBand + " fence-band cards, " + Vertices + " vertices, " + LoadMs.ToString("F0") + " ms (" + Timing + ")");
    }

    static void FreeGrids()
    {
        Ready = false;
        gGround = gTop = gW = gCan = gEff = gFd = gFloor = null; crowns.Clear(); lid = null; lflag = null; waterL = null; cStep = null; cDist = null; tbk = null;
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
    // atlas cells (EnvTex.BuildingAtlas, 8 x 4; see there)
    const int CShutG = 0, CShutB = 1, CBlind = 2, CBrickWin = 3, CFlats = 4, COffice = 5, CTower = 6, CStone = 7,
              GBarred = 8, GDoor = 9, GShop = 10, GGarage = 11, GLobby = 12, GLoading = 13, CIndustry = 14, CShed = 15,
              CTiles = 16, CFlatRoof = 17, CMetal = 18, CSkylight = 19, CConcrete = 20, CPlaster = 21, CCrowd = 22, CUnit = 23,
              CBrick = 24, CSoffit = 25, CTrim = 26, CPanelWin = 27, CShutGx = 29, CShutBx = 30, CBlindx = 31;
    // size of one tile of the material cells (m): across, up
    static float TileW(int c) => c == CTiles ? 2.5f : c == CFlatRoof ? 4f : c == CMetal ? 2.7f : c == CConcrete ? 4.6f : c == CCrowd ? 4f : c == CSoffit ? 2.4f : c == CBrick ? 2.8f : 3f;
    static float TileH(int c) => c == CCrowd ? 3.6f : c == CBrick ? 3.2f : TileW(c);
    // uv2: the cell, and an alternative cell for "share" of the bays (Baked.shader F2_ATLAS)
    static Vector2 CellUv(int c) => new Vector2(c, c + 0.05f);
    static Vector2 CellUv(int c, int alt, float share) => new Vector2(c, alt + 0.05f + 0.9f * share);
    // uv3 of the vertices made next: the building's colour where the atlas' alpha is 1, w = wall length in tiles (corner trims)
    static Vector4 curTint = new Vector4(1f, 1f, 1f, 0f);
    static bool trimOn;
    static readonly Color Grey1 = new Color(1f, 1f, 1f, 1f);
    static Color G(float k) => new Color(k, k, k, 1f);

    static float[] px = new float[256], pz = new float[256], ox = new float[256], oz = new float[256];
    static readonly List<int> roofTri = new List<int>();

    // the best treatment (ground floors, cornices, eave soffits, rooftop units) within this distance of the track
    static float NearTier => gfx <= 1 ? 220f : gfx == 2 ? 320f : 400f;
    static void Buildings()
    {
        float near = NearTier;
        for (int q = 0; q < selB.Count; q++)
        {
            float d = selBd[q];
            Building(selB[q], d < near ? 0 : d < 800f ? 1 : 2, d);
        }
        curTint = new Vector4(1f, 1f, 1f, 0f);
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

    // ---------------------------------------------------------------- the look of one building's walls
    // classes: 0 home, 1 flats, 2 office, 3 industrial, 4 shed / garages, 5 stand, 6 civic / church
    static int fClass, fPal; // palette: 0 plaster colours, 1 brick, 2 greys, 3 stone
    static Vector2 fUpC, fGfC; static float fBw, fFh, fGfH, fCap, fCornice;
    static void Facade(int kind, float H, float area, float h, float h2, float h3)
    {
        fBw = 3f; fFh = 3.2f; fGfH = 3.4f; fPal = 0; fCap = 0.75f; fCornice = 0.28f;
        // upper floors of a home: green or brown shutters (persiane), or roller blinds (tapparelle) behind a stone surround
        Vector2 home;
        float shut = sty.house == 0 ? 0.8f : 0.62f;
        if (h < sty.brickShare) { home = CellUv(CBrickWin); fPal = 1; }
        else if (h2 < shut * 0.6f) home = CellUv(CShutG, CShutGx, 0.3f);
        else if (h2 < shut) home = CellUv(CShutB, CShutBx, 0.3f);
        else home = CellUv(CBlind, CBlindx, 0.3f);
        Vector2 gfHome = CellUv(GBarred, GDoor, 0.22f), gfShop = CellUv(GShop, GGarage, 0.25f);
        if (kind == 7 || kind == 0 && area < 40f)
        {
            fClass = 4; fPal = 2; fBw = 3f; fFh = fGfH = 3f;
            fUpC = fGfC = kind == 7 && area < 90f ? CellUv(GGarage) : CellUv(CShed);
            if (kind == 7 && area < 90f) { fPal = 0; fGfH = 2.8f; }
        }
        else if (kind == 3 || kind == 0 && area > 1500f && H <= 14f)
        {
            fClass = 3; fPal = 2; fBw = 6f; fFh = fGfH = 6f; fCap = 0.6f; fCornice = 0f;
            fUpC = CellUv(CIndustry); fGfC = CellUv(CIndustry, GLoading, 0.18f);
        }
        else if (kind == 4) { fClass = 5; fPal = 2; fBw = fFh = fGfH = 4.6f; fUpC = fGfC = CellUv(CConcrete); fCornice = 0f; }
        else if (kind == 5 || kind == 6) { fClass = 6; fPal = 3; fFh = fGfH = 4f; fUpC = fGfC = CellUv(CStone); fCornice = 0.4f; fCap = 0.9f; }
        else if (kind == 8 && H > 26f) { fClass = 2; fPal = 2; fFh = 3.4f; fGfH = 4.2f; fUpC = CellUv(CTower); fGfC = CellUv(GLobby); fCornice = 0f; fCap = 0.9f; }
        else if (kind == 2 && (H > 11f || h > 0.5f))
        {
            fClass = 2; fPal = 2; fFh = 3.6f; fGfH = 4.2f; fCornice = 0.15f; fCap = 0.9f;
            fUpC = h2 < 0.5f ? CellUv(COffice) : CellUv(CPanelWin); fGfC = CellUv(GLobby, GShop, 0.3f);
        }
        else if (H > 14f)
        {
            fClass = 1; fUpC = h2 < 0.55f ? CellUv(CFlats) : CellUv(CBlind, CBlindx, 0.3f);
            if (h3 < 0.5f || kind == 2) { fGfC = gfShop; fGfH = 3.8f; } else fGfC = gfHome;
        }
        else
        {
            fClass = 0; fUpC = home; fGfC = gfHome;
            if (kind == 2 || h3 < 0.15f && area > 120f) { fGfC = gfShop; fGfH = 3.8f; }
            else if (h3 > 0.85f) fGfC = CellUv(GGarage, GBarred, 0.5f);
            if (fPal == 1) fGfC = CellUv(CBrickWin);
        }
    }
    // Lombardy palette (StyleFor) for plaster; brick and stone carry their own colour; greys for offices and sheds
    static Color WallColour(int b, int pal, float h)
    {
        Color c;
        int pick = (int)(h * 9973f);
        if (pal == 1) c = sty.brick[pick % sty.brick.Length];
        else if (pal == 2) c = Greys[pick % Greys.Length];
        else if (pal == 3) c = C(0.97f, 0.94f, 0.88f);
        else c = sty.walls[pick % sty.walls.Length];
        uint rgba = D.bRgba[b];
        if ((rgba >> 24) != 0 && pal != 1 && pal != 3)
        {
            var oc = new Color((rgba & 255) / 255f, ((rgba >> 8) & 255) / 255f, ((rgba >> 16) & 255) / 255f);
            float lum = 0.3f * oc.r + 0.59f * oc.g + 0.11f * oc.b;
            if (lum < 0.3f) oc = Color.Lerp(oc, Color.white, (0.3f - lum) / 0.7f);
            c = Color.Lerp(oc, c, 0.35f);
        }
        float k = 0.93f + 0.1f * Hash(b, 77);
        return new Color(c.r * k, c.g * k, c.b * k, 1f);
    }
    static Vector4 Tint(Color c) => new Vector4(c.r, c.g, c.b, 0f);

    static void Building(int b, int lod, float dist)
    {
        int n0 = D.bCnt[b], o = D.bOff[b];
        if (n0 > px.Length) { px = new float[n0]; pz = new float[n0]; ox = new float[n0]; oz = new float[n0]; }
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
        float h0 = Hash(b, 1), h1 = Hash(b, 2), h2 = Hash(b, 3), h3 = Hash(b, 4);
        int kind = D.bKind[b];
        float H = D.bH[b]; if (H <= 0.5f) H = EstimateHeight(kind, area, h0);
        else H = Mathf.Max(H, MinHeight(kind, area)); // (the ML heights run low: a two-storey house reads as a shed)
        H = Mathf.Min(H, 400f);
        float minH = D.bMinH[b]; if (minH >= H - 1f) minH = 0f;
        float gmin = float.MaxValue, gmax = float.MinValue, cx = 0, cz = 0;
        for (int k = 0; k < n; k++) { float g = Ground(px[k], pz[k]); gmin = Mathf.Min(gmin, g); gmax = Mathf.Max(gmax, g); cx += px[k]; cz += pz[k]; }
        cx /= n; cz /= n;
        curTint = new Vector4(1f, 1f, 1f, 0f);
        if (kind == 4 && dist < 260f && lod < 2 && fill > 0.6f && hl >= 8f && hs >= 3.5f && Stand(b, ocx, ocz, ax, az, hl, hs, gmin, gmax)) return;
        float top = gmin + H;
        // near the track: a solid for the TV cameras (Trackside.Solids), the footprint's rectangle up to the roof
        if (dist < 160f) Trackside.AddSolid(new Vector3(ocx, 0f, ocz), new Vector3(ax, 0f, az), hl, hs, gmin - 1f, top + 2.5f, false);
        Facade(kind, H, area, h0, h2, h3);
        bool home = fClass <= 1 || fClass == 6;
        // roof shape: the data's, or a pitched roof on houses where the data has none (most houses in Europe)
        int roof = D.bRoof[b];
        bool rectish = fill > 0.72f && n <= 14;
        if (roof == 0 && rectish && area < 450f && H < 16f && minH <= 0f && (kind == 1 || kind == 0) && fClass == 0 && h1 < sty.pitched) roof = h2 < 0.45f ? 1 : 2;
        if ((roof == 1 || roof == 2 || roof == 4) && !rectish) roof = 5;
        if (roof == 3 && fill < 0.6f) roof = 5;
        bool metal = fClass == 3 || fClass == 4 && fGfC.x != GGarage;
        float roofH = roof == 1 ? hs * (metal ? 0.3f : 0.55f) : roof == 2 ? hs * 0.5f : roof == 4 ? hs * 0.7f : roof == 3 ? hs * 0.85f : 0f;
        roofH = Mathf.Min(roofH, (H - minH) * 0.5f);
        if (roof >= 1 && roof <= 4 && roofH < 0.6f) { roof = 5; roofH = 0f; }
        float eave = top - roofH;
        bool hasGf = minH <= 0.5f;
        float baseY = hasGf ? gmin : gmin + minH;
        // whole storeys: the eave snaps to the ground floor + n upper floors, so the window grid is regular
        int nUp = 0;
        if (hasGf && eave - gmin > fGfH)
        {
            nUp = Mathf.RoundToInt((eave - gmin - fGfH) / fFh);
            eave = gmin + fGfH + nUp * fFh; top = eave + roofH;
        }
        float foot = hasGf ? gmin - 0.7f - (gmax - gmin) : baseY; // walls reach into the slope
        Color wall = WallColour(b, fPal, h1);
        Color roofC = roof == 0 ? sty.flat * (0.9f + 0.15f * h2) : roof == 3 ? (h2 < 0.5f ? C(0.42f, 0.58f, 0.52f) : C(0.55f, 0.56f, 0.58f))
                    : metal ? C(0.68f, 0.69f, 0.70f) * (0.88f + 0.16f * h1) : sty.tiles[(int)(h2 * 7919f) % sty.tiles.Length] * (0.92f + 0.12f * h1);
        roofC.a = 1f;
        var mb = Buf(SBuild, Block(cx, cz), n * 40 + 160);
        trimOn = home && fPal != 1;
        Color ao = G(lod == 2 ? 1f : 0.72f);
        bool parapet = roof == 0 && lod < 2 && fClass != 4 && H > 4f;
        float cap = parapet ? fCap : 0f;
        float roofY = roof == 0 ? eave + (parapet ? cap - 0.1f : 0f) : top;
        var wallT = Tint(wall);
        Color light = Color.Lerp(wall, Color.white, 0.4f); var lightT = Tint(light);
        // offset outline for the cornice (mitred, so the corners close)
        float co = lod == 0 && parapet ? fCornice : 0f;
        if (co > 0f) Offset(n, co);
        for (int k = 0; k < n; k++)
        {
            int k2 = (k + 1) % n;
            float x0 = px[k], z0 = pz[k], x1 = px[k2], z1 = pz[k2], dx = x1 - x0, dz = z1 - z0, len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 0.05f) continue;
            var outw = new Vector3(dz / len, 0f, -dx / len); // right of the edge: outside of a counter-clockwise ring
            float bays = len < fBw * 0.7f ? 0f : Mathf.Max(1f, Mathf.Round(len / fBw));
            curTint = wallT;
            if (bays == 0f) Wall(mb, x0, z0, x1, z1, foot, eave, outw, len / 3f, gmin, 3f, CellUv(fPal == 1 ? CBrick : CPlaster), ao, Grey1, false);
            else if (lod == 0 && nUp > 0)
            {
                Wall(mb, x0, z0, x1, z1, foot, gmin + fGfH, outw, bays, gmin, fGfH, fGfC, ao, Grey1, trimOn);
                Wall(mb, x0, z0, x1, z1, gmin + fGfH, eave, outw, bays, gmin + fGfH, fFh, fUpC, Grey1, Grey1, trimOn);
            }
            else if (hasGf && nUp == 0) Wall(mb, x0, z0, x1, z1, foot, eave, outw, bays, gmin, fGfH, fGfC, ao, Grey1, trimOn);
            else
            {
                float org = eave - Mathf.Max(1, Mathf.RoundToInt((eave - baseY) / fFh)) * fFh; // grid aligned with the eave
                Wall(mb, x0, z0, x1, z1, foot, eave, outw, bays, org, fFh, fUpC, hasGf ? ao : Grey1, Grey1, trimOn);
            }
            if (parapet)
            {
                curTint = fClass == 3 ? wallT : lightT;
                int pc = fClass == 2 || fClass == 3 ? CConcrete : CPlaster;
                if (co > 0f)
                {
                    // projecting cornice: soffit (in shade under it) and the band up to the parapet's top
                    Vector3 a0 = new Vector3(x0, eave, z0), a1 = new Vector3(x1, eave, z1), b0 = new Vector3(ox[k], eave, oz[k]), b1 = new Vector3(ox[k2], eave, oz[k2]);
                    float lu = len / TileW(pc);
                    Quad(mb, a0, a1, b1, b0, Vector3.down, new Vector2(0, 0), new Vector2(lu, 0), new Vector2(lu, 0.08f), new Vector2(0, 0.08f), CellUv(pc), G(0.8f), G(0.8f), G(0.8f), G(0.8f));
                    float cv = (cap) / TileH(pc);
                    Quad(mb, b0, b1, b1 + Vector3.up * cap, b0 + Vector3.up * cap, outw, new Vector2(0, 0), new Vector2(lu, 0), new Vector2(lu, cv), new Vector2(0, cv), CellUv(pc), Grey1, Grey1, Grey1, Grey1);
                }
                else Wall(mb, x0, z0, x1, z1, eave, eave + cap, outw, len / TileW(pc), eave, TileH(pc), CellUv(pc), Grey1, Grey1, false);
            }
        }
        curTint = new Vector4(1f, 1f, 1f, 0f);
        float bx2 = -az, bz2 = ax; // short axis of the rectangle
        var A = new Vector3(ax, 0f, az); var Bv = new Vector3(bx2, 0f, bz2); var cen = new Vector3(ocx, 0f, ocz);
        float ov = lod == 0 ? (metal ? 0.35f : 0.6f) : lod == 1 ? 0.4f : 0f;
        int roofCell = metal ? CMetal : CTiles;
        bool extras = lod == 0 && gfx >= 2;
        switch (roof)
        {
            case 1:
                Gabled(mb, cen, A, Bv, hl, hs, eave, top, ov, roofC, roofCell, wall, fPal == 1 ? CBrick : CPlaster, gmin, lod == 0);
                if (extras && metal) Skylights(mb, cen, A, Bv, hl, hs, eave, top, b);
                break;
            case 2: Hipped(mb, cen, A, Bv, hl, hs, eave, top, ov, roofC, roofCell, lod == 0); break;
            case 4: Hipped(mb, cen, A, Bv, hs, hs, eave, top, ov, roofC, roofCell, lod == 0, hl); break;
            case 3: FlatRoof(mb, n, eave, roofC, CFlatRoof); Dome(mb, cen, A, Bv, hl * 0.97f, hs * 0.97f, eave, roofH, roofC); break;
            default:
                FlatRoof(mb, n, roofY, roofC, roof == 0 ? (fClass == 3 || fClass == 4 ? CMetal : CFlatRoof) : CTiles);
                if (extras && roof == 0 && rectish && fill > 0.75f && hl > 3f && hs > 2.5f)
                {
                    if (fClass == 3 && area > 300f) Skylights(mb, cen, A, Bv, hl, hs, roofY, roofY, b);
                    else if (fClass != 4 && fClass != 5 && area > 90f) Units(mb, cen, A, Bv, hl, hs, roofY, b, fClass == 1 && H > 14f ? wall : default);
                }
                break;
        }
        curTint = new Vector4(1f, 1f, 1f, 0f);
    }

    // the footprint pushed out by d (mitred corners, capped at sharp ones) into ox / oz
    static void Offset(int n, float d)
    {
        for (int k = 0; k < n; k++)
        {
            int kp = (k + n - 1) % n, kn = (k + 1) % n;
            float ex = px[k] - px[kp], ez = pz[k] - pz[kp], fx = px[kn] - px[k], fz = pz[kn] - pz[k];
            float le = Mathf.Max(1e-4f, Mathf.Sqrt(ex * ex + ez * ez)), lf = Mathf.Max(1e-4f, Mathf.Sqrt(fx * fx + fz * fz));
            float n0x = ez / le, n0z = -ex / le, n1x = fz / lf, n1z = -fx / lf;
            float k1 = Mathf.Max(0.35f, 1f + n0x * n1x + n0z * n1z);
            ox[k] = px[k] + (n0x + n1x) / k1 * d; oz[k] = pz[k] + (n0z + n1z) / k1 * d;
        }
    }

    // a box on a roof: four sides and a lid (rooftop units, stair and lift housings)
    static void Box(MB b, Vector3 c, Vector3 A, Vector3 Bv, float ha, float hb, float y0, float y1, int side, Color col, Vector4 tint)
    {
        var up = Vector3.up; var prev = curTint; curTint = tint;
        Vector3 p0 = c - A * ha - Bv * hb, p1 = c + A * ha - Bv * hb, p2 = c + A * ha + Bv * hb, p3 = c - A * ha + Bv * hb;
        Vector3[] q = { p0, p1, p2, p3 }; Vector3[] nrm = { -Bv, A, Bv, -A };
        var cu = CellUv(side); float tv = side == CUnit ? 1f : (y1 - y0) / TileH(side);
        for (int s = 0; s < 4; s++)
        {
            Vector3 a = q[s], e = q[(s + 1) % 4]; float tu = side == CUnit ? 1f : (a - e).magnitude / TileW(side);
            Quad(b, a + up * y0, e + up * y0, e + up * y1, a + up * y1, nrm[s], new Vector2(0, 0), new Vector2(tu, 0), new Vector2(tu, tv), new Vector2(0, tv), cu, col * 0.85f, col * 0.85f, col, col);
        }
        Quad(b, p0 + up * y1, p1 + up * y1, p2 + up * y1, p3 + up * y1, up, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), CellUv(CFlatRoof), col, col, col, col);
        curTint = prev;
    }
    // rooftop clutter on a flat roof: air-conditioning units, a lift / stair housing on blocks of flats
    static void Units(MB b, Vector3 c, Vector3 A, Vector3 Bv, float hl, float hs, float y, int id, Color housing)
    {
        var white = new Vector4(1f, 1f, 1f, 0f);
        if (housing.a > 0f && hl > 5f && hs > 4f)
        {
            float f = Hash(id, 21) - 0.5f;
            Box(b, c + A * (f * (hl - 3f)), A, Bv, 1.6f, 1.8f, y, y + 2.7f, CPlaster, Grey1, Tint(Color.Lerp(housing, Color.white, 0.2f)));
        }
        int count = 1 + (int)(Hash(id, 22) * Mathf.Clamp(hl * hs / 40f, 1f, 3f));
        for (int u = 0; u < count; u++)
        {
            float fa = (Hash(id, 30 + u) - 0.5f) * 2f * (hl - 1.6f), fb = (Hash(id, 40 + u) - 0.5f) * 2f * (hs - 1.4f);
            bool big = Hash(id, 50 + u) < 0.35f && hl > 6f;
            float ha = big ? 1.4f : 0.6f, hb = big ? 0.9f : 0.45f, hh = big ? 1.5f : 1.0f;
            Box(b, c + A * fa + Bv * fb, A, Bv, ha, hb, y, y + hh, CUnit, G(0.95f), white);
        }
    }
    // skylight strips on an industrial roof: across a flat roof, or patches up both slopes of a pitched one
    static void Skylights(MB b, Vector3 c, Vector3 A, Vector3 Bv, float hl, float hs, float eave, float top, int id)
    {
        var up = Vector3.up; var cu = CellUv(CSkylight); var w = G(1f);
        int cnt = Mathf.Clamp((int)(hl / 7f), 1, 6);
        for (int s = 0; s < cnt; s++)
        {
            float a = -hl + (s + 0.5f) * 2f * hl / cnt, wa = 0.8f;
            if (top <= eave + 0.01f)
            {
                float lb = hs * 0.75f, y = eave + 0.12f;
                Quad(b, c + A * (a - wa) - Bv * lb + up * y, c + A * (a + wa) - Bv * lb + up * y, c + A * (a + wa) + Bv * lb + up * y, c + A * (a - wa) + Bv * lb + up * y, up,
                     new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, lb * 2f / 3f), new Vector2(0, lb * 2f / 3f), cu, w, w, w, w);
                continue;
            }
            for (int sd = -1; sd <= 1; sd += 2)
            {
                var nrm = (Bv * (sd * (top - eave)) + up * hs).normalized * 0.06f;
                Vector3 lo = c + Bv * (sd * hs * 0.8f) + up * (eave + (top - eave) * 0.2f) + nrm, hi = c + Bv * (sd * hs * 0.15f) + up * (eave + (top - eave) * 0.85f) + nrm;
                float tv = (hi - lo).magnitude / 3f;
                Quad(b, lo + A * (a - wa), lo + A * (a + wa), hi + A * (a + wa), hi + A * (a - wa), Bv * sd + up,
                     new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, tv), new Vector2(0, tv), cu, w, w, w, w);
            }
        }
    }

    // lowest believable height of a mapped building of this kind and footprint (m)
    static float MinHeight(int kind, float area)
    {
        switch (kind)
        {
            case 1: case 8: return area > 150f ? 6.2f : area > 60f ? 4.5f : 3f;
            case 2: case 3: return area > 400f ? 7f : 4.5f;
            case 4: return 8f;
            case 5: case 6: return 7f;
            case 7: return 2.4f;
            default: return area > 300f ? 5.5f : 3f;
        }
    }

    // direction from (x, z) to the nearest centreline point within lim (false if none)
    static bool TrackDir(float x, float z, float lim, out Vector3 dir)
    {
        dir = Vector3.zero;
        int r = Mathf.CeilToInt(lim / TBK), ci = Mathf.FloorToInt(x / TBK), cj = Mathf.FloorToInt(z / TBK);
        float best = lim * lim; bool found = false;
        for (int dj = -r; dj <= r; dj++) for (int di = -r; di <= r; di++)
        {
            if (!tbk.TryGetValue(Key(ci + di, cj + dj), out var l)) continue;
            for (int q = 0; q < l.Count; q += 2)
            {
                var p = trk.pts[l[q]]; float dx = (float)p.x - x, dz = (float)p.y - z, d2 = dx * dx + dz * dz;
                if (d2 < best) { best = d2; dir = new Vector3(dx, 0f, dz); found = true; }
            }
        }
        if (found) dir.Normalize();
        return found;
    }

    // a grandstand from the map whose long side faces the track: low front wall, raked seating with the crowd (atlas
    // cell CCrowd, shared with the buildings: no extra draw call), end walls, back wall and a cantilever roof
    static bool Stand(int b, float ocx, float ocz, float ax, float az, float hl, float hs, float gmin, float gmax)
    {
        if (!TrackDir(ocx, ocz, 300f, out var toT)) return false;
        Vector3 A = new Vector3(ax, 0f, az), F = new Vector3(-az, 0f, ax);
        float f = Vector3.Dot(F, toT); if (Mathf.Abs(f) < 0.55f) return false; // the track is off an end
        if (f < 0) F = -F;
        Vector3 up = Vector3.up, cen = new Vector3(ocx, 0f, ocz);
        // each stand its own rake and roof height (a row of identical wedges read as blocks from a distance)
        float depth = 2f * hs, frontY = gmin + 2.6f, rise = Mathf.Min(depth * (0.42f + 0.16f * Hash(b, 79)), 22f), topY = frontY + rise, roofY = topY + 2.9f + 1.6f * Hash(b, 80);
        float foot = gmin - 0.7f - (gmax - gmin);
        Vector3 FL = cen + F * hs - A * hl, FR = cen + F * hs + A * hl, BL = cen - F * hs - A * hl, BR = cen - F * hs + A * hl;
        Trackside.AddSolid(cen, A, hl + 0.8f, hs + 1.2f, gmin - 1f, roofY + 3.5f, false); // (roof flags, floodlights)
        var mb = Buf(SBuild, Block(ocx, ocz), 220 + 28 * (Mathf.RoundToInt(2f * hl / 9f) + 3));
        Color conc = C(0.80f, 0.80f, 0.78f), dark = C(0.62f, 0.62f, 0.61f), white = Color.white;
        float k = 0.95f + 0.1f * Hash(b, 77); conc *= k; conc.a = 1f; dark.a = 1f;
        var cc = CellUv(CConcrete); var pl = CellUv(CPlaster); float bw = TileW(CConcrete), fh = TileH(CConcrete);
        float bays = Mathf.Max(1f, Mathf.Round(2f * hl / bw));
        // front wall up to the first row, back wall (outside and the part above the top row)
        Wall(mb, FL.x, FL.z, FR.x, FR.z, foot, frontY, F, bays, gmin, fh, cc, dark, conc);
        Wall(mb, BR.x, BR.z, BL.x, BL.z, foot, roofY + 0.3f, -F, bays, gmin, fh, cc, dark, conc);
        // inside, above the top row: the glazed fronts of the boxes and the press room under the roof
        Wall(mb, BL.x, BL.z, BR.x, BR.z, topY, roofY, F, Mathf.Max(1f, Mathf.Round(2f * hl / 3.4f)), topY, roofY - topY, CellUv(COffice), white, white);
        // raked seating: one tile = 4 rows (3.6 m of slope) x 4 m
        float slope = Mathf.Sqrt(depth * depth + rise * rise) / TileH(CCrowd), across = 2f * hl / TileW(CCrowd);
        Vector3 rakeN = (F * rise / depth + up).normalized;
        // the rake in tiers (vertices for the bake to put the roof's shadow on, and colour bands): seat colours alternate
        // tier by tier in this stand's two seat colours, and the rows under the roof go darker towards the back (the
        // roof's shade and the closed back: strong AO that reads at 300 m)
        Color[,] seatPair = { { C(1.00f, 0.86f, 0.80f), C(0.82f, 0.86f, 1.00f) }, { C(0.92f, 0.98f, 0.86f), C(1.00f, 0.96f, 0.84f) },
                              { C(1.00f, 0.84f, 0.84f), C(0.94f, 0.94f, 0.96f) }, { C(0.84f, 0.90f, 1.02f), C(1.00f, 0.95f, 0.88f) } };
        int sp = (int)(Hash(b, 81) * 3.999f);
        float rakeLen = Mathf.Sqrt(depth * depth + rise * rise);
        int tiers = Mathf.Clamp(Mathf.RoundToInt(rakeLen / 4.5f), 3, 8);
        Color TierCol(int tq, float u)
        {
            float shade = Mathf.Lerp(1f, 0.5f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.18f, 1f, u)));
            Color sc = seatPair[sp, tq & 1] * shade; sc.a = 1f; return sc;
        }
        for (int tq = 0; tq < tiers; tq++)
        {
            float u0 = (float)tq / tiers, u1 = (float)(tq + 1) / tiers;
            Vector3 a0 = Vector3.Lerp(FL, BL, u0) + up * (frontY + rise * u0), b0 = Vector3.Lerp(FR, BR, u0) + up * (frontY + rise * u0);
            Vector3 a1 = Vector3.Lerp(FL, BL, u1) + up * (frontY + rise * u1), b1 = Vector3.Lerp(FR, BR, u1) + up * (frontY + rise * u1);
            Color c0 = TierCol(tq, u0), c1 = TierCol(tq, u1);
            Quad(mb, a0, b0, b1, a1, rakeN, new Vector2(0, slope * u0), new Vector2(across, slope * u0), new Vector2(across, slope * u1), new Vector2(0, slope * u1),
                 CellUv(CCrowd), c0, c0, c1, c1);
        }
        // stair aisles up the rake every ~9 m (bare concrete between the seat blocks), a dark vomitory mouth on every
        // other one, and a white rail along the front: the seating reads as blocks of people, not one noisy slab
        Vector2 flat = new Vector2(0.5f, 0.5f);
        int aisles = Mathf.Max(2, Mathf.RoundToInt(2f * hl / 9f)); float slopeLen = Mathf.Sqrt(depth * depth + rise * rise);
        Color step = C(0.74f, 0.74f, 0.72f), mouth = C(0.10f, 0.10f, 0.11f), rail = C(0.93f, 0.93f, 0.92f);
        Vector3 upRake = (BL - FL + up * (topY - frontY)) / slopeLen; // unit vector up the slope
        for (int q = 0; q <= aisles; q++)
        {
            float a = -hl + 2f * hl * q / aisles, hwA = q == 0 || q == aisles ? 0.35f : 0.6f;
            a = Mathf.Clamp(a, -hl + hwA, hl - hwA);
            Vector3 f0 = cen + F * hs + A * a + up * frontY + rakeN * 0.07f;
            Quad(mb, f0 - A * hwA, f0 + A * hwA, f0 + A * hwA + upRake * slopeLen, f0 - A * hwA + upRake * slopeLen, rakeN,
                 new Vector2(0, 0), new Vector2(0.25f, 0), new Vector2(0.25f, slopeLen / fh), new Vector2(0, slopeLen / fh), cc, step, step, step, step);
            if (q > 0 && q < aisles && (q & 1) == 1 && slopeLen > 9f)
            {
                Vector3 v0 = f0 + rakeN * 0.04f + upRake * (slopeLen * 0.36f);
                Quad(mb, v0 - A * 1.5f, v0 + A * 1.5f, v0 + A * 1.5f + upRake * 2.6f, v0 - A * 1.5f + upRake * 2.6f, rakeN, flat, flat, flat, flat, pl, mouth, mouth, mouth, mouth);
                Quad(mb, v0 - A * 1.7f + upRake * 2.6f, v0 + A * 1.7f + upRake * 2.6f, v0 + A * 1.7f + upRake * 3.0f, v0 - A * 1.7f + upRake * 3.0f, rakeN, flat, flat, flat, flat, pl, rail, rail, rail, rail);
            }
        }
        Quad(mb, FL + F * 0.06f + up * (frontY - 0.1f), FR + F * 0.06f + up * (frontY - 0.1f), FR + F * 0.06f + up * (frontY + 0.55f), FL + F * 0.06f + up * (frontY + 0.55f), F, flat, flat, flat, flat, pl, rail, rail, rail, rail);
        // a cross gangway about 40 % up the rake (a long horizontal concrete band with a dark riser and a white rail): the
        // structure that splits a big stand into a lower and an upper tier
        if (rakeLen > 13f)
        {
            float gu0 = 0.4f, gu1 = 0.4f + 1.5f / rakeLen;
            Vector3 g0 = Vector3.Lerp(FL, BL, gu0) + up * (frontY + rise * gu0) + rakeN * 0.09f, g1 = Vector3.Lerp(FL, BL, gu1) + up * (frontY + rise * gu1) + rakeN * 0.09f;
            Vector3 wA = A * (2f * hl);
            Quad(mb, g0 - rakeN * 0.5f, g0 - rakeN * 0.5f + wA, g0 + wA, g0, F, flat, flat, flat, flat, pl, mouth, mouth, mouth, mouth);
            Quad(mb, g0, g0 + wA, g1 + wA, g1, rakeN, new Vector2(0, 0), new Vector2(2f * hl / bw, 0), new Vector2(2f * hl / bw, 0.3f), new Vector2(0, 0.3f), cc, step, step, step, step);
            Quad(mb, g0 + F * 0.05f + up * 0.55f, g0 + F * 0.05f + up * 0.55f + wA, g0 + F * 0.05f + up * 1.0f + wA, g0 + F * 0.05f + up * 1.0f, F, flat, flat, flat, flat, pl, rail, rail, rail, rail);
        }
        // ends: the space under the rake closed with ribbed steel in the stand's own colour (no white wedge up to the roof:
        // above the rake the end is open), a white raker beam along the slope
        Color[] liv = { C(0.44f, 0.54f, 0.72f), C(0.42f, 0.58f, 0.48f), C(0.66f, 0.68f, 0.72f), C(0.72f, 0.50f, 0.44f) };
        Color clad = liv[(int)(Hash(b, 78) * 3.999f)]; clad.a = 1f; Color cladLo = clad * 0.8f; cladLo.a = 1f;
        float tw = TileW(CMetal);
        for (int e = -1; e <= 1; e += 2)
        {
            Vector3 fe = e < 0 ? FL : FR, be = e < 0 ? BL : BR;
            Quad(mb, fe + up * foot, be + up * foot, be + up * topY, fe + up * frontY, A * e,
                 new Vector2(0, 0), new Vector2(depth / tw, 0), new Vector2(depth / tw, (topY - foot) / tw), new Vector2(0, (frontY - foot) / tw), CellUv(CMetal), cladLo, cladLo, clad, clad);
            Vector3 o = A * (e * 0.07f);
            Quad(mb, fe + o + up * (frontY - 0.5f), be + o + up * (topY - 0.5f), be + o + up * (topY + 0.05f), fe + o + up * (frontY + 0.05f), A * e, flat, flat, flat, flat, pl, rail, rail, rail, rail);
            // the back wall's return above the rake (a slim pier up to the roof)
            Quad(mb, be + up * topY, be + F * 1.2f + up * (topY + 1.2f * rise / depth), be + F * 1.2f + up * (roofY + 0.3f), be + up * (roofY + 0.3f), A * e, flat, flat, flat, flat, pl, conc, conc, conc, conc);
        }
        // cantilever roof over the back three quarters, rising a little towards the track: metal sheets, a deep white
        // fascia, tapered steel beams under it every ~9 m
        Vector3 rf = F * (hs - depth * 0.78f), lift = up * 0.8f, ov = A * 0.5f;
        Vector3 r0 = BL - ov + up * (roofY + 0.3f), r1 = BR + ov + up * (roofY + 0.3f), r2 = cen + rf + A * hl + ov + up * (roofY + 0.3f) + lift, r3 = cen + rf - A * hl - ov + up * (roofY + 0.3f) + lift;
        Color[] roofs = { C(0.80f, 0.82f, 0.85f), C(0.62f, 0.64f, 0.67f), C(0.70f, 0.74f, 0.71f), C(0.74f, 0.70f, 0.66f) };
        Color roofC = roofs[(int)(Hash(b, 82) * 3.999f)]; roofC.a = 1f; Color under = C(0.32f, 0.32f, 0.34f); under.a = 1f;
        float ru = 2f * hl / TileW(CMetal), rv = depth * 0.78f / TileW(CMetal);
        Quad(mb, r0, r1, r2, r3, up, new Vector2(0, 0), new Vector2(ru, 0), new Vector2(ru, rv), new Vector2(0, rv), CellUv(CMetal), roofC, roofC, roofC, roofC);
        Quad(mb, r0 - up * 0.35f, r1 - up * 0.35f, r2 - up * 0.35f, r3 - up * 0.35f, -up, flat, flat, flat, flat, pl, under, under, under, under);
        Quad(mb, r3 - up * 1.0f, r2 - up * 1.0f, r2, r3, F, flat, flat, flat, flat, pl, rail, rail, rail, rail);
        Quad(mb, r3 - up * 1.0f, r2 - up * 1.0f, r2 - F * 0.5f - up * 0.35f, r3 - F * 0.5f - up * 0.35f, -up, flat, flat, flat, flat, pl, under, under, under, under);
        Color beam = C(0.86f, 0.87f, 0.88f), beamLo = C(0.6f, 0.61f, 0.63f);
        // a lattice truss along the roof's front edge, under the fascia (top and bottom chords, diagonals every ~3 m): the
        // steelwork reads against the dark shade under the roof from far down the straight
        {
            Vector3 tA = r3 - up * 1.0f + F * 0.02f, tB = r2 - up * 1.0f + F * 0.02f; const float td = 1.5f, tw2 = 0.12f;
            Quad(mb, tA - up * 0.16f, tB - up * 0.16f, tB, tA, F, flat, flat, flat, flat, pl, beam, beam, beam, beam);
            Quad(mb, tA - up * td, tB - up * td, tB - up * (td - 0.16f), tA - up * (td - 0.16f), F, flat, flat, flat, flat, pl, beamLo, beamLo, beam, beam);
            int nd = Mathf.Max(2, Mathf.RoundToInt((tB - tA).magnitude / 3f));
            for (int q = 0; q < nd; q++)
            {
                Vector3 p0 = Vector3.Lerp(tA, tB, (float)q / nd), p1 = Vector3.Lerp(tA, tB, (float)(q + 1) / nd);
                Vector3 top = (q & 1) == 0 ? p0 : p1, bot = ((q & 1) == 0 ? p1 : p0) - up * td, side = A * tw2;
                Quad(mb, bot - side, bot + side, top + side, top - side, F, flat, flat, flat, flat, pl, beamLo, beamLo, beam, beam);
            }
        }
        // slim columns from the rake up to the truss on every other aisle (vertical rhythm along the stand)
        for (int q = 1; q < aisles; q += 2)
        {
            float a = -hl + 2f * hl * q / aisles;
            float uc = 0.22f;
            Vector3 cb = Vector3.Lerp(FL, BL, uc) + A * a + up * (frontY + rise * uc), ct = cen + rf + A * a + up * (roofY + 0.3f - 2.5f) + lift * 0.9f;
            ct = new Vector3(cb.x, ct.y, cb.z);
            Vector3 hc = A * 0.14f;
            Quad(mb, cb - hc, cb + hc, ct + hc, ct - hc, F, flat, flat, flat, flat, pl, beamLo, beamLo, beam, beam);
            Quad(mb, cb + hc, cb + hc - F * 0.28f, ct + hc - F * 0.28f, ct + hc, A, flat, flat, flat, flat, pl, beamLo, beamLo, beamLo, beamLo);
            Quad(mb, cb - hc - F * 0.28f, cb - hc, ct - hc, ct - hc - F * 0.28f, -A, flat, flat, flat, flat, pl, beamLo, beamLo, beamLo, beamLo);
        }
        for (int q = 0; q <= aisles; q++)
        {
            float a = Mathf.Clamp(-hl + 2f * hl * q / aisles, -hl + 0.3f, hl - 0.3f);
            Vector3 bt = cen - F * hs + A * a + up * (roofY - 0.05f), ft = cen + rf - F * 0.6f + A * a + up * (roofY - 0.05f) + lift * 0.96f;
            Vector3 bb = bt - up * 1.7f, fb = ft - up * 0.3f, hb = A * 0.16f;
            Quad(mb, bb + hb, fb + hb, ft + hb, bt + hb, A, flat, flat, flat, flat, pl, beamLo, beam, beam, beam);
            Quad(mb, bb - hb, fb - hb, ft - hb, bt - hb, -A, flat, flat, flat, flat, pl, beamLo, beam, beam, beam);
            Quad(mb, bb - hb, bb + hb, fb + hb, fb - hb, -up, flat, flat, flat, flat, pl, beamLo, beamLo, beamLo, beamLo);
        }
        return true;
    }

    // one wall quad from height y0 to y1 along an edge; uv in tiles (bays across, storeys of fh up from "org");
    // trim: lighter corner strips at both ends (the shader reads the wall's length in tiles from uv3.w)
    static void Wall(MB b, float x0, float z0, float x1, float z1, float y0, float y1, Vector3 outw, float bays, float org, float fh, Vector2 cell, Color c0, Color c1, bool trim = false)
    {
        if (y1 <= y0) return;
        Vector3 p0 = new Vector3(x0, y0, z0), p1 = new Vector3(x1, y0, z1), p2 = new Vector3(x1, y1, z1), p3 = new Vector3(x0, y1, z0);
        float v0 = bays > 0 ? (y0 - org) / fh : 0.5f, v1 = bays > 0 ? (y1 - org) / fh : 0.5f, u1 = bays > 0 ? bays : 0.5f, ua = bays > 0 ? 0f : 0.5f;
        float w = curTint.w; curTint.w = trim && bays > 0 ? bays : 0f;
        Quad(b, p0, p1, p2, p3, outw, new Vector2(ua, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(ua, v1), cell, c0, c0, c1, c1);
        curTint.w = w;
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
        for (int k = 0; k < 4; k++) { b.uv2.Add(cell); b.t4.Add(curTint); }
        b.col.Add(c0); b.col.Add(c1); b.col.Add(c2); b.col.Add(c3);
        b.tri.Add(i); b.tri.Add(i + 1); b.tri.Add(i + 2); b.tri.Add(i); b.tri.Add(i + 2); b.tri.Add(i + 3);
    }
    static void Tri(MB b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 outward, Vector2 t0, Vector2 t1, Vector2 t2, Vector2 cell, Color c)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0) { var tp = p1; p1 = p2; p2 = tp; var tu = t1; t1 = t2; t2 = tu; }
        int i = b.v.Count;
        b.v.Add(p0); b.v.Add(p1); b.v.Add(p2);
        b.uv.Add(t0); b.uv.Add(t1); b.uv.Add(t2);
        for (int k = 0; k < 3; k++) { b.uv2.Add(cell); b.col.Add(c); b.t4.Add(curTint); }
        b.tri.Add(i); b.tri.Add(i + 1); b.tri.Add(i + 2);
    }
    // flat roof over the (cleaned, counter-clockwise) footprint in px / pz
    static void FlatRoof(MB b, int n, float y, Color c, int cell)
    {
        roofTri.Clear();
        WorldGeo.Triangulate(px, pz, n, roofTri);
        int start = b.v.Count; var cu = CellUv(cell); float s = 1f / TileW(cell);
        for (int k = 0; k < n; k++) { b.v.Add(new Vector3(px[k], y, pz[k])); b.uv.Add(new Vector2(px[k] * s, pz[k] * s)); b.uv2.Add(cu); b.col.Add(c); b.t4.Add(curTint); }
        for (int k = 0; k + 2 < roofTri.Count; k += 3) { b.tri.Add(start + roofTri[k]); b.tri.Add(start + roofTri[k + 2]); b.tri.Add(start + roofTri[k + 1]); }
    }
    // the underside of an overhanging roof (rafters and tavelle, in the shade) and the gutter line along the eaves
    const float RoofThick = 0.14f;
    static readonly Color SoffitC = new Color(0.85f, 0.85f, 0.85f, 1f), GutterC = new Color(0.62f, 0.60f, 0.56f, 1f);
    static void SoffitQuad(MB b, Vector3 e0, Vector3 e1, Vector3 r1, Vector3 r0, Vector3 faceUp)
    {
        var d = Vector3.down * RoofThick; float lu = (e1 - e0).magnitude / TileW(CSoffit), lv = (r0 - e0).magnitude / TileW(CSoffit);
        Quad(b, e0 + d, e1 + d, r1 + d, r0 + d, -faceUp, new Vector2(0, 0), new Vector2(lu, 0), new Vector2(lu, lv), new Vector2(0, lv), CellUv(CSoffit), SoffitC, SoffitC, SoffitC, SoffitC);
        Quad(b, e0 + d, e1 + d, e1, e0, new Vector3(faceUp.x, 0f, faceUp.z), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), CellUv(CTrim), GutterC, GutterC, GutterC, GutterC);
    }
    // gabled roof over the footprint's rectangle: ridge along the long axis A, gable walls at the ends
    static void Gabled(MB b, Vector3 c, Vector3 A, Vector3 Bv, float hl, float hs, float eave, float top, float ov, Color roofC, int roofCell, Color wall, int gableCell, float gmin, bool soffit)
    {
        float rh = top - eave, drop = ov * rh / Mathf.Max(hs, 0.1f), L = hl + ov, S = hs + ov, slope = Mathf.Sqrt(S * S + (rh + drop) * (rh + drop));
        Vector3 up = Vector3.up, ye = up * (eave - drop), yt = up * top;
        var tc = CellUv(roofCell); float tw = 1f / TileW(roofCell);
        for (int sd = -1; sd <= 1; sd += 2)
        {
            Vector3 e0 = c - A * L + Bv * (S * sd) + ye, e1 = c + A * L + Bv * (S * sd) + ye, r1 = c + A * L + yt, r0 = c - A * L + yt;
            Quad(b, e0, e1, r1, r0, Bv * sd + up, new Vector2(0, 0), new Vector2(2 * L * tw, 0), new Vector2(2 * L * tw, slope * tw), new Vector2(0, slope * tw), tc, roofC, roofC, roofC, roofC);
            if (soffit && ov > 0f) SoffitQuad(b, e0, e1, r1, r0, Bv * sd + up);
        }
        // gable triangles in the end walls: plain plaster (or brick) in the building's colour
        var prev = curTint; curTint = Tint(wall);
        float gw = 1f / TileW(gableCell), gh = 1f / TileH(gableCell);
        for (int sd = -1; sd <= 1; sd += 2)
        {
            Vector3 g0 = c + A * (hl * sd) - Bv * hs + up * eave, g1 = c + A * (hl * sd) + Bv * hs + up * eave, ap = c + A * (hl * sd) + yt;
            Tri(b, g0, g1, ap, A * sd, new Vector2(0, (eave - gmin) * gh), new Vector2(2f * hs * gw, (eave - gmin) * gh), new Vector2(hs * gw, (top - gmin) * gh), CellUv(gableCell), Grey1);
        }
        curTint = prev;
    }
    // hipped roof (ridge = long half-size minus short half-size); a pyramid when the ridge is that short. hlPyr: the
    // real long half-size for a pyramidal roof on a long rectangle (called with hl = hs, so the ridge is a point)
    static void Hipped(MB b, Vector3 c, Vector3 A, Vector3 Bv, float hl, float hs, float eave, float top, float ov, Color roofC, int roofCell, bool soffit, float hlPyr = -1f)
    {
        float baseL = hlPyr > 0 ? hlPyr : hl;
        float rh = top - eave, drop = ov * rh / Mathf.Max(hs, 0.1f), L = baseL + ov, S = hs + ov, rl = Mathf.Max(0f, hl - hs);
        Vector3 up = Vector3.up, ye = up * (eave - drop), yt = up * top;
        Vector3 e00 = c - A * L - Bv * S + ye, e10 = c + A * L - Bv * S + ye, e11 = c + A * L + Bv * S + ye, e01 = c - A * L + Bv * S + ye;
        Vector3 r0 = c - A * rl + yt, r1 = c + A * rl + yt;
        var tc = CellUv(roofCell); float tw = 1f / TileW(roofCell);
        float sl = Mathf.Sqrt(S * S + (rh + drop) * (rh + drop)) * tw, el = Mathf.Sqrt((L - rl) * (L - rl) + (rh + drop) * (rh + drop)) * tw;
        Quad(b, e00, e10, r1, r0, -Bv + up, new Vector2(0, 0), new Vector2(2 * L * tw, 0), new Vector2((L + rl) * tw, sl), new Vector2((L - rl) * tw, sl), tc, roofC, roofC, roofC, roofC);
        Quad(b, e11, e01, r0, r1, Bv + up, new Vector2(0, 0), new Vector2(2 * L * tw, 0), new Vector2((L + rl) * tw, sl), new Vector2((L - rl) * tw, sl), tc, roofC, roofC, roofC, roofC);
        Tri(b, e01, e00, r0, -A + up, new Vector2(0, 0), new Vector2(2 * S * tw, 0), new Vector2(S * tw, el), tc, roofC);
        Tri(b, e10, e11, r1, A + up, new Vector2(0, 0), new Vector2(2 * S * tw, 0), new Vector2(S * tw, el), tc, roofC);
        if (soffit && ov > 0f)
        {
            SoffitQuad(b, e00, e10, r1, r0, -Bv + up); SoffitQuad(b, e11, e01, r0, r1, Bv + up);
            // hip ends: the underside as a triangle, the gutter as a quad
            var d = Vector3.down * RoofThick; float ts = 1f / TileW(CSoffit);
            Tri(b, e01 + d, e00 + d, r0 + d, A - up, new Vector2(0, 0), new Vector2(2 * S * ts, 0), new Vector2(S * ts, el / tw * ts), CellUv(CSoffit), SoffitC);
            Tri(b, e10 + d, e11 + d, r1 + d, -A - up, new Vector2(0, 0), new Vector2(2 * S * ts, 0), new Vector2(S * ts, el / tw * ts), CellUv(CSoffit), SoffitC);
        }
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
                b.uv.Add(new Vector2(s * 0.5f, r * 0.5f)); b.uv2.Add(cu); b.col.Add(col); b.t4.Add(curTint);
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
    struct MPend { public float x, y, z, edge, sz; public Color lc; public int nc; }
    static void Trees()
    {
        crowns.Clear();
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
                    case 1:
                    {
                        // rows of trees from the wood's edge to just past the first band (BandAt), thinning inwards
                        float ef = gEff[k]; src = 1;
                        p = ef > BandAt + 6f ? 0f : (ef < 12f ? 1.3f : ef < 24f ? 0.8f : 0.45f) * (d < 150f ? Mathf.Max(1f, env.treeMul) : 1f) * (d < 200f ? 1f : 0.7f);
                        break;
                    }
                    case 8: p = 0.45f; break;
                    case 3: p = 0.16f; break;
                    case 0: p = 0.05f; break;
                    case 10: p = 0.08f; break;
                    case 2: p = 0.012f; break;
                    case 4: p = 0.03f; break;
                    case 6: p = env.tree == TreeKind.Scrub || env.tree2 == TreeKind.Scrub ? 0.12f : 0.02f; break;
                    default: p = 0f; break;
                }
                // gardens: trees round the buildings near the circuit (a house alone in a bare field looks dropped there)
                if (p < 0.3f && d < 380f && !Flagged(x, z) && (Built(x + 9f, z) || Built(x - 9f, z) || Built(x, z + 9f) || Built(x, z - 9f))) { p = 0.4f; src = 2; }
                if (p <= 0f) continue;
                if (d > 300f) p *= 0.6f;
                int cnt = (int)p + (rnd.NextDouble() < p - (int)p ? 1 : 0);
                for (int q = 0; q < cnt; q++)
                    cand.Add(new TCand { x = x + ((float)rnd.NextDouble() - 0.5f) * B, z = z + ((float)rnd.NextDouble() - 0.5f) * B, h = 0f, pr = d + (src == 1 ? 0f : 60f), src = src });
            }
        cand.Sort((a, c) => a.pr.CompareTo(c.pr));
        bool low = gfx <= 1; int cards = low ? 2 : 3; float ch = CanopyHeight();
        // real tree models near the track (ModelLib.cs, Medium and High): queued, nearest first; whatever is out of budget stays a card
        bool mdl = env.realModels && ModelLib.Begin(gfx); var pend = mdl ? new List<MPend>() : null;
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
            if (t.h > 0f)
            {
                sz = t.h / bh;
                // the data's default height (no tag: 10 m, Monaco 8 m): as big as the circuit's trees are (Monza: 20-30 m park trees)
                if (Mathf.Abs(t.h - 10f) < 0.05f || Mathf.Abs(t.h - 8f) < 0.05f) sz = Mathf.Max(sz, (0.75f + 0.5f * (float)rnd.NextDouble()) * env.treeSize);
            }
            else if (t.src == 1 && tk != TreeKind.Scrub) sz = ch / bh * (0.9f + 0.35f * (float)rnd.NextDouble());
            else sz = (0.7f + 0.8f * (float)rnd.NextDouble()) * env.treeSize;
            sz = Mathf.Clamp(sz, 0.4f, 2.6f);
            if (tk != TreeKind.Scrub && d < near + 14f) sz = Mathf.Min(sz, 1.25f * Mathf.Max(1f, env.treeSize * 0.95f)); // no giant right at the fence
            float y = Mathf.Min(Ground(x, z), MeshY(x, z)) - 0.35f;
            var lc = (t.src == 1 && rnd.NextDouble() < env.tree2Share ? env.leaf2 : env.leaf).linear;
            int nc = d > 320f ? 2 : cards; // far trees: two cards (LOD by distance)
            if (d < 260f) crowns.Add(new Vector3(x, z, Mathf.Clamp(3.6f * sz, 2.5f, 7f))); // (the forest floor under it: FloorMask)
            if (mdl && tk == TreeKind.Broadleaf)
            {
                float edge = TrackDist(x, z, ModelLib.Reach + wallLat + 1f) - wallLat;
                if (edge < ModelLib.Reach) { pend.Add(new MPend { x = x, y = y, z = z, edge = edge, sz = sz, lc = lc, nc = nc }); TreeCount++; continue; }
            }
            var mb = Buf(STree, Block(x, z), 12);
            Environment.EmitTree(mb.v, mb.n, mb.uv, mb.uv2, mb.col, mb.tri, new Vector3(x, y, z), tk, sz, lc, rnd, nc);
            TreeCount++;
        }
        if (mdl)
        {
            pend.Sort((ma, mc) => ma.edge.CompareTo(mc.edge));
            foreach (var mp in pend)
                if (!ModelLib.TryTree(new Vector3(mp.x, mp.y, mp.z), mp.edge, mp.lc, rnd))
                {
                    var mb = Buf(STree, Block(mp.x, mp.z), 12);
                    Environment.EmitTree(mb.v, mb.n, mb.uv, mb.uv2, mb.col, mb.tri, new Vector3(mp.x, mp.y, mp.z), TreeKind.Broadleaf, mp.sz, mp.lc, rnd, mp.nc);
                }
            ModelLib.PlaceProps(trk);
            ParkFurniture();
        }
        Bands(rnd);
        FloorMask();
        if (mdl) Undergrowth();
    }
    // Forest-edge bands: where the depth into the woods crosses BandAt (and, near the track, BandAt + 18 m) a band card
    // (Environment.EmitBand) stands along the edge facing out, as tall as the canopy; they overlap about twice, so the
    // woods behind the first rows of trees read as a deep layered forest with ragged tops, never as a raised green mass.
    const float BandAt = 26f;
    static void Bands(System.Random rnd)
    {
        float reach = TreeReach * 1.1f, ch = CanopyHeight(); int count = 0, cap = gfx <= 1 ? 2000 : 4000;
        float[] at = { BandAt, BandAt + 18f };
        for (int j = 1; j < gnz - 1 && count < cap; j++)
            for (int i = 1; i < gnx - 1 && count < cap; i++)
            {
                int cc = Mathf.Min(cnz - 1, j / CH) * cnx + Mathf.Min(cnx - 1, i / CH);
                if (cDist[cc] > reach) { i = (i / CH + 1) * CH - 1; continue; }
                int k = j * gnx + i; float e = gEff[k];
                for (int row = 0; row < 2; row++)
                {
                    float L = at[row];
                    // the contour: this point at or beyond L, a neighbour short of it
                    if (e < L || (gEff[k - 1] >= L && gEff[k + 1] >= L && gEff[k - gnx] >= L && gEff[k + gnx] >= L)) continue;
                    float x = rx0 + i * B + ((float)rnd.NextDouble() - 0.5f) * B * 0.4f, z = rz0 + j * B + ((float)rnd.NextDouble() - 0.5f) * B * 0.4f;
                    float d = Dist(x, z); if (d > reach || (row == 1 && (d > 300f || gfx <= 1))) continue;
                    var outward = new Vector3(gEff[k - 1] - gEff[k + 1], 0f, gEff[k - gnx] - gEff[k + gnx]); // down the depth: out of the wood
                    if (outward.sqrMagnitude < 1e-4f) continue;
                    outward.Normalize(); var along = new Vector3(-outward.z, 0f, outward.x);
                    if (Trackside.Blocked(x, z, 6f) || PadDist(x, z) < 30f || (Waterfront.HasWater && Waterfront.InWater(x, z)) || !Environment.Free(x, z, 6f)) continue;
                    float y = Mathf.Min(Ground(x, z), MeshY(x, z)) - 0.5f;
                    float h = ch * (0.95f + 0.3f * (float)rnd.NextDouble()) * (row == 0 ? 1f : 1.15f), w = B * 2.4f * (0.9f + 0.25f * (float)rnd.NextDouble());
                    var lc = Color.Lerp(env.leaf, env.leaf2, env.tree2Share * Noise(x / 60f, z / 60f, 95)).linear;
                    var mb = Buf(STree, Block(x, z), 4);
                    Environment.EmitBand(mb.v, mb.n, mb.uv, mb.uv2, mb.col, mb.tri, new Vector3(x, y, z), along, outward, w, h, lc, rnd);
                    count++;
                }
            }
        BandCount = count;
    }
    public static int BandCount;

    // ================================================================ forest floor and undergrowth (round 8)
    // The woods stood on mown lawn with a hard line at the trunks. Now:
    //  * FloorMask: a 0..1 forest-floor mask on the terrain grid from the mapped woods (depth into them, the circuit's
    //    own ground included, ramping in over the first ~12 m) and from the crowns of the trees actually planted near
    //    the track (a lone park tree gives a soft patch, a stand of them full cover). LandTexture turns it into a dark,
    //    brown-green leaf-littered floor (no hard switch), and VergeFloor writes it into the verge's uv3.y, where
    //    Ground.cginc does the same on the verge's grass between the fence and the land (no extra texture read).
    //  * Undergrowth (Medium / High, with the real models): within 150 m of the track, at the wood edges and under the
    //    trees, hazel / elder bushes 1.5-4 m and bramble patches 0.5-1 m (the two shrub models, instanced), young
    //    replanted trees 3-6 m in ~55 m patches (Monza replanted ~8,000 after the 2023 storms; slender, LOD 1 at most),
    //    ferns deeper in the shade and grass clumps at the tree feet (low cards in the model trees' merged chunks),
    //    bushes along the service roads, and a narrow, uneven band of tall pale grass cards at the barrier / fence feet
    //    (the park side, and the track side where the verge is grass). Low: nothing (ModelLib is off).
    static readonly List<Vector3> crowns = new List<Vector3>(); // trees planted near the track: x, z, crown radius
    public static int UBush, UBramble, UYoung, UFern, UClump, UBand;

    static void FloorMask()
    {
        int n = gnx * gnz; gFloor = new float[n];
        if (gFd != null) for (int k = 0; k < n; k++) gFloor[k] = gFd[k] > 0f ? 0.9f * Smooth((gFd[k] - B * 0.4f) / 12f) : 0f;
        var acc = new float[n];
        foreach (var c in crowns)
        {
            float r = c.z * 1.3f;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((c.x - r - rx0) / B)), i1 = Mathf.Min(gnx - 1, Mathf.CeilToInt((c.x + r - rx0) / B));
            int j0 = Mathf.Max(0, Mathf.FloorToInt((c.y - r - rz0) / B)), j1 = Mathf.Min(gnz - 1, Mathf.CeilToInt((c.y + r - rz0) / B));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float dx = rx0 + i * B - c.x, dz = rz0 + j * B - c.y, d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d < r) acc[j * gnx + i] += 0.5f * (1f - d / r);
                }
        }
        for (int k = 0; k < n; k++) gFloor[k] = Mathf.Max(gFloor[k], 0.95f * Smooth(acc[k]));
    }

    // the verge (WorldBuilder.BuildVerge, Ground.shader) gets the mask in uv3.y: none on the track side of the barrier,
    // ramping in behind it (its outer rows reach ~16 m past the barrier, where the land texture takes over)
    static void VergeFloor(Transform root)
    {
        if (gFloor == null || root == null) return;
        var vt = root.Find("Verge"); if (vt == null) return;
        var mf = vt.GetComponent<MeshFilter>(); if (mf == null) return;
        var mesh = mf.sharedMesh; if (mesh == null || !mesh.isReadable) return;
        var v = mesh.vertices; var u2 = new List<Vector2>(); mesh.GetUVs(2, u2);
        if (u2.Count != v.Length) return;
        var u3 = new Vector2[v.Length]; bool any = false;
        for (int i = 0; i < v.Length; i++)
        {
            float lat = u2[i].x, m = Bilinear(gFloor, v[i].x, v[i].z) * Smooth((lat - wallLat - 0.2f) / 5f);
            u3[i] = new Vector2(0f, m); any |= m > 0.01f;
        }
        if (any) mesh.uv4 = u3; // (uv4 = TEXCOORD3 = Ground.cginc's uv3)
    }

    // open, unpaved ground for a plant (r: its radius)
    static bool PlantSpot(float x, float z, float r)
    {
        if (x < rx0 + 20f || x > rx1 - 20f || z < rz0 + 20f || z > rz1 - 20f) return false;
        int k = Texel(x, z); if (k < 0 || lflag[k] != 0) return false;
        int kind = lid[k] == 0 ? 0 : D.aKind[lid[k] - 1];
        if (kind == 4 || kind == 5 || kind == 7 || kind == 11) return false; // paved, water, rail / parking
        return !Trackside.Blocked(x, z, r) && PadDist(x, z) > r + 6f && !(Waterfront.HasWater && Waterfront.InWater(x, z)) && Environment.Free(x, z, r);
    }

    static void Undergrowth()
    {
        UBush = UBramble = UYoung = UFern = UClump = UBand = 0;
        if (!ModelLib.Active || street || gFloor == null || trk == null || trk.N < 200) return;
        var rnd = new System.Random(23);
        bool hi = gfx >= 3;
        if (!hi) return; // High only, and half the first budgets: the race must not get heavier on mid-range phones
        int maxBush = 75, maxBramble = 60, maxYoung = 32, maxFern = 240, maxClump = 200;
        const float Near = 150f;
        Color leaf = env.leaf.linear, leaf2 = env.leaf2.linear;
        // ---- in the woods and under the trees: candidates, nearest the barrier first (with a jitter)
        var cand = new List<Vector4>(); // x, z, priority, mask
        for (int j = 1; j < gnz - 1; j++)
            for (int i = 1; i < gnx - 1; i++)
            {
                int cc = Mathf.Min(cnz - 1, j / CH) * cnx + Mathf.Min(cnx - 1, i / CH);
                if (cDist[cc] > Near + 20f) { i = (i / CH + 1) * CH - 1; continue; }
                if (gFloor[j * gnx + i] < 0.22f) continue;
                for (int q = 0; q < 2; q++)
                {
                    float x = rx0 + (i + (float)rnd.NextDouble() - 0.5f) * B, z = rz0 + (j + (float)rnd.NextDouble() - 0.5f) * B;
                    float td = TrackDist(x, z, Near + 1f); if (td > Near) continue;
                    float edge = td - wallLat; if (edge < 2.5f) continue;
                    float m = Bilinear(gFloor, x, z); if (m < 0.2f) continue;
                    cand.Add(new Vector4(x, z, edge + 30f * (float)rnd.NextDouble(), m));
                }
            }
        cand.Sort((a, b) => a.z.CompareTo(b.z));
        foreach (var c in cand)
        {
            float x = c.x, z = c.y, m = c.w;
            if (!PlantSpot(x, z, 1.5f) || TrackDist(x, z, wallLat + 3f) < wallLat + 2.5f) continue;
            float fdr = Bilinear(gFd, x, z), y = Mathf.Min(Ground(x, z), MeshY(x, z));
            // the wood's edge (its first ~16 m, or thin cover under park trees) is where light reaches the ground
            bool edgeZone = fdr < 16f || m < 0.7f;
            bool replant = Hash(Mathf.FloorToInt(x / 55f) + 3001, Mathf.FloorToInt(z / 55f) + 7919) < 0.3f;
            float r = (float)rnd.NextDouble();
            var lc = Color.Lerp(leaf, leaf2, (float)rnd.NextDouble() * env.tree2Share);
            var foot = new Vector3(x, y - 0.08f, z);
            if (replant && edgeZone && r < 0.6f)
            {
                if (UYoung < maxYoung && ModelLib.TryYoung(foot, 3f + 3f * (float)rnd.NextDouble(), lc, rnd)) UYoung++;
                continue;
            }
            if (edgeZone && r < 0.4f)
            {
                if (UBush >= maxBush) continue;
                float h = 1.5f + 2.5f * Mathf.Pow((float)rnd.NextDouble(), 1.3f);
                if (ModelLib.TryBush(foot, h, h * (0.42f + 0.25f * (float)rnd.NextDouble()), (float)rnd.NextDouble() * 6.28f, lc, false, rnd)) UBush++;
            }
            else if (edgeZone && r < 0.68f)
            {
                if (UBramble >= maxBramble) continue;
                float h = 0.5f + 0.5f * (float)rnd.NextDouble();
                if (ModelLib.TryBush(foot, h, 0.8f + 1.0f * (float)rnd.NextDouble(), (float)rnd.NextDouble() * 6.28f, lc, true, rnd)) UBramble++;
            }
            else if (m > 0.45f && UFern < maxFern)
            {
                // ferns in the shade (a few at the edge too), often two or three together
                int nf = 1 + (rnd.NextDouble() < 0.5 ? 1 : 0) + (rnd.NextDouble() < 0.25 ? 1 : 0);
                for (int f = 0; f < nf && UFern < maxFern; f++)
                {
                    float fx = x + ((float)rnd.NextDouble() - 0.5f) * 2.4f, fz = z + ((float)rnd.NextDouble() - 0.5f) * 2.4f;
                    if (Fern(new Vector3(fx, Mathf.Min(Ground(fx, fz), MeshY(fx, fz)) - 0.05f, fz), rnd)) UFern++;
                }
            }
            else if (UClump < maxClump && GrassClump(foot, 0.55f + 0.45f * (float)rnd.NextDouble(), rnd)) UClump++;
        }
        // ---- tufts of long grass (or a fern in the shade) at the feet of the trees near the track: no hard line where a trunk meets the ground
        foreach (var c in crowns)
        {
            if (UClump >= maxClump) break;
            if (rnd.NextDouble() > 0.45) continue;
            float a = (float)rnd.NextDouble() * 6.28f, o = 0.4f + 0.6f * (float)rnd.NextDouble(), x = c.x + Mathf.Cos(a) * o, z = c.y + Mathf.Sin(a) * o;
            float td = TrackDist(x, z, wallLat + 81f); if (td - wallLat > 80f || td < wallLat + 2.5f) continue;
            if (!PlantSpot(x, z, 0.6f)) continue;
            var foot = new Vector3(x, Mathf.Min(Ground(x, z), MeshY(x, z)) - 0.05f, z);
            bool ok = Bilinear(gFloor, x, z) > 0.7f && UFern < maxFern ? Fern(foot, rnd) : GrassClump(foot, 0.5f + 0.4f * (float)rnd.NextDouble(), rnd);
            if (ok) UClump++;
        }
        // ---- along the service roads (the side away from the track): a bush or a bramble patch every ~40-70 m
        foreach (var sr in svcRuns)
        {
            int n = sr.Length / 2; float acc = 0f, next = 20f + 30f * Hash(n, 91);
            for (int k = 1; k < n && UBush < maxBush; k++)
            {
                float x0 = sr[k * 2 - 2], z0 = sr[k * 2 - 1], dx = sr[k * 2] - x0, dz = sr[k * 2 + 1] - z0, len = Mathf.Sqrt(dx * dx + dz * dz);
                if (len < 0.01f) continue;
                dx /= len; dz /= len; float a0 = acc; acc += len;
                while (next <= acc)
                {
                    float cx = x0 + dx * (next - a0), cz = z0 + dz * (next - a0), ox = dz, oz = -dx;
                    if (TrackDist(cx + ox * 4f, cz + oz * 4f, 80f) < TrackDist(cx - ox * 4f, cz - oz * 4f, 80f)) { ox = -ox; oz = -oz; }
                    float lat = SvcHalf + 3.4f + 1.5f * (float)rnd.NextDouble(), x = cx + ox * lat, z = cz + oz * lat;
                    next += 40f + 30f * (float)rnd.NextDouble();
                    if (TrackDist(x, z, Near + 1f) > Near || !PlantSpot(x, z, 1.5f)) continue;
                    var foot = new Vector3(x, Mathf.Min(Ground(x, z), MeshY(x, z)) - 0.08f, z);
                    var lc = Color.Lerp(leaf, leaf2, (float)rnd.NextDouble() * env.tree2Share);
                    if (rnd.NextDouble() < 0.6) { float h = 1.4f + 1.6f * (float)rnd.NextDouble(); if (ModelLib.TryBush(foot, h, h * 0.5f, (float)rnd.NextDouble() * 6.28f, lc, false, rnd)) UBush++; }
                    else if (UBramble < maxBramble && ModelLib.TryBush(foot, 0.6f + 0.4f * (float)rnd.NextDouble(), 1.0f + 0.8f * (float)rnd.NextDouble(), (float)rnd.NextDouble() * 6.28f, lc, true, rnd)) UBramble++;
                }
            }
        }
        FenceBand(rnd, hi);
    }

    // a fern: two crossed cards of the fern tile, dark green, a little sway
    static bool Fern(Vector3 foot, System.Random rnd)
    {
        float w = 1.0f + 0.7f * (float)rnd.NextDouble(), h = 0.55f + 0.45f * (float)rnd.NextDouble(), a = (float)rnd.NextDouble() * 3.1416f, ph = (float)rnd.NextDouble() * 6.28f;
        float sh = 0.8f + 0.35f * (float)rnd.NextDouble();
        Color f0 = new Color(0.55f * sh, 0.6f * sh, 0.55f * sh, 1f), f1 = new Color(1.0f * sh, 1.05f * sh, 0.95f * sh, 1f);
        bool ok = false;
        for (int c = 0; c < 2; c++)
        {
            float ang = a + c * 1.5708f; var half = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (w * 0.5f);
            ok |= ModelLib.AddCard(1, foot, half, h, Vector3.zero, f0, f1, 0.25f, ph, rnd.NextDouble() < 0.5);
        }
        return ok;
    }
    // a clump of tall grass: two crossed grass cards, pale tips
    static bool GrassClump(Vector3 foot, float h, System.Random rnd)
    {
        float w = 0.9f + 0.6f * (float)rnd.NextDouble(), a = (float)rnd.NextDouble() * 3.1416f, ph = (float)rnd.NextDouble() * 6.28f;
        Color t = GrassTint(rnd, 0.3f + 0.4f * (float)rnd.NextDouble());
        bool ok = false;
        for (int c = 0; c < 2; c++)
        {
            float ang = a + c * 1.5708f; var half = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (w * 0.5f);
            ok |= ModelLib.AddCard(0, foot, half, h * (c == 0 ? 1f : 0.85f), Vector3.zero, t * 0.62f, t * 1.06f, 0.35f, ph, rnd.NextDouble() < 0.5);
        }
        return ok;
    }
    // the grass cards' tint on the painted tile: straw (dry, paler) by share, a shade
    static Color GrassTint(System.Random rnd, float straw)
    {
        float sh = 0.86f + 0.24f * (float)rnd.NextDouble();
        var c = Color.Lerp(new Color(0.95f, 1.0f, 0.92f), new Color(1.18f, 1.08f, 0.78f), straw) * sh; c.a = 1f;
        return c;
    }

    // the tall-grass band at the barrier / catch-fence feet: a card every ~1.5 m (Medium 2 m) on the park side (0.45-1.1 m tall,
    // gaps and heights from a slow noise along the lap), a second, crossing one on High; on the track side short tufts
    // where the verge is grass (no gravel, run-off or pit lane). Cards in the model trees' merged chunks.
    static void FenceBand(System.Random rnd, bool hi)
    {
        float ds = (float)trk.ds; int step = Mathf.Max(1, Mathf.RoundToInt((hi ? 2.5f : 3f) / ds)); // a card every ~2.5 m (High only)
        var L = Trackside.Lane; double len = trk.N * trk.ds;
        for (int side = -1; side <= 1; side += 2)
            for (int i = 0; i < trk.N; i += step)
            {
                var p = trk.pts[i]; float s = i * ds;
                if (L != null && L.side == side)
                {
                    double a = L.sEntry - 40.0, b = L.sExit + 40.0, u = s;
                    bool inPit = a <= b ? (u >= a && u <= b) : (u >= a || u <= b);
                    if (a < 0 && u >= a + len) inPit = true;
                    if (b > len && u <= b - len) inPit = true;
                    if (inPit) continue;
                }
                float nz = Noise(s / 19f, side * 5.3f + 11f, 61), nh = Noise(s / 7f, side * 3.1f, 63);
                if (nz < 0.3f) continue; // mown / trodden gaps
                var tan = new Vector3((float)p.tx, 0f, (float)p.ty); var nrm = new Vector3((float)p.nx, 0f, (float)p.ny) * side;
                for (int pass = 0; pass < 2; pass++)
                {
                    bool park = pass == 0;
                    if (!park)
                    {
                        if (trk.runL == null || trk.runR == null) continue;
                        var run = side > 0 ? trk.runL[i] : trk.runR[i];
                        if (run != SurfaceType.Grass || !WorldBuilder.GroundShaded || nz < 0.45f) continue;
                    }
                    float lat = park ? wallLat + 0.3f + 0.45f * (float)rnd.NextDouble() : wallLat - 0.22f - 0.25f * (float)rnd.NextDouble();
                    float x = (float)p.x + nrm.x * lat + tan.x * ((float)rnd.NextDouble() - 0.5f), z = (float)p.y + nrm.z * lat + tan.z * ((float)rnd.NextDouble() - 0.5f);
                    if (Trackside.Blocked(x, z, 0.6f) || PadDist(x, z) < 3f || (Waterfront.HasWater && Waterfront.InWater(x, z)) || Waterfront.InTunnel(x, z)) continue;
                    if (RoadOrBuilt(x, z) || TrackDist(x, z, lat + 1f) < lat - 0.6f) continue; // a road crossing / another leg of the lap
                    float h = (park ? 0.45f + 0.65f * nh : 0.22f + 0.3f * nh) * (0.8f + 0.4f * (float)rnd.NextDouble());
                    float ang = ((float)rnd.NextDouble() - 0.5f) * 0.5f, ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                    var along = tan * ca + nrm * sa;
                    float hw = (park ? 0.7f : 0.5f) + 0.35f * (float)rnd.NextDouble();
                    var lean = tan * (((float)rnd.NextDouble() - 0.5f) * 0.3f * h) + nrm * (0.1f * h);
                    var foot = new Vector3(x, Mathf.Min(Ground(x, z), MeshY(x, z)) - 0.04f, z);
                    if (!park) foot.y = Mathf.Max(foot.y, WorldBuilder.H(x, z) - 0.04f);
                    Color t = GrassTint(rnd, 0.25f + 0.55f * Noise(s / 53f, side * 2.7f, 67));
                    float ph = (float)rnd.NextDouble() * 6.28f;
                    if (!ModelLib.AddCard(0, foot, along * hw, h, lean, t * 0.6f, t * 1.08f, 0.35f, ph, rnd.NextDouble() < 0.5)) return;
                    UBand++;
                    if (park && (hi || rnd.NextDouble() < 0.35))
                    {
                        float a2 = ang + (rnd.NextDouble() < 0.5 ? 0.7f : -0.7f);
                        var al2 = tan * Mathf.Cos(a2) + nrm * Mathf.Sin(a2);
                        var f2 = foot + nrm * 0.25f + tan * (((float)rnd.NextDouble() - 0.5f) * 0.6f);
                        if (ModelLib.AddCard(0, f2, al2 * (hw * 0.8f), h * (0.75f + 0.3f * (float)rnd.NextDouble()), lean * 0.6f, t * 0.6f, t * 1.05f, 0.35f, ph + 1.3f, rnd.NextDouble() < 0.5)) UBand++;
                    }
                }
            }
    }

    // ================================================================ the open land beyond the barriers
    // (round 5: the open stretches showed bare, flat lawns out to a bare horizon)
    //
    // Service roads: where the land 12-30 m behind the barrier is open (grass, park, farmland, gardens, wetland; no
    // woods, buildings, stands, landmarks or water) for 140 m or more, a 3.8 m worn-asphalt service road runs parallel
    // to the track 19 m behind the barrier, its ends curving away into the park. Along it every 230-420 m stands a
    // marshal / service hut with portable toilets, or parked service vehicles (van, car, fire tender, with an orange
    // light bar). Planned in Prepare, so their ground is flagged and the trees keep off; built with the map's roads and
    // buildings (same meshes and materials: no draw calls).
    struct SvcProp { public float x, z, ax, az, bx, bz, reach; public int kind, seed; }
    static readonly List<SvcProp> svcProps = new List<SvcProp>();
    static readonly List<float[]> svcRuns = new List<float[]>(); // x0, z0, x1, z1, ... of each service road
    const float SvcHalf = 1.9f;
    // open, unbuilt land of a kind a service road / hedge can cross
    static bool Open(float x, float z)
    {
        if (x < rx0 + 30f || x > rx1 - 30f || z < rz0 + 30f || z > rz1 - 30f) return false;
        int k = Texel(x, z); if (k < 0 || (lflag[k] & 2) != 0) return false;
        int kind = lid[k] == 0 ? 0 : D.aKind[lid[k] - 1];
        return kind == 0 || kind == 2 || kind == 3 || kind == 8 || kind == 10;
    }
    static bool OpenSpot(float x, float z, float r)
    {
        return Open(x, z) && !Trackside.Blocked(x, z, r) && PadDist(x, z) > r + 12f
               && !(Waterfront.HasWater && Waterfront.InWater(x, z)) && Environment.Free(x, z, r);
    }
    static void ServicePlan(Track t)
    {
        svcRuns.Clear(); svcProps.Clear();
        if (street || t.N < 200 || lid == null) return;
        float off = wallLat + 19f;
        int step = Mathf.Max(1, Mathf.RoundToInt(6f / (float)t.ds)), n = t.N / step, look = Mathf.Max(1, Mathf.RoundToInt(16f / (float)t.ds));
        int lstep = Mathf.Max(1, look / 4);
        var ok = new bool[n];
        for (int side = -1; side <= 1; side += 2)
        {
            for (int q = 0; q < n; q++)
            {
                int i = q * step; var p = t.pts[i];
                // the inside of a tight bend: the parallel line would fold over itself
                float kin = 0f;
                for (int w = -look; w <= look; w += lstep) kin = Mathf.Max(kin, (float)t.pts[((i + w) % t.N + t.N) % t.N].curv * side);
                bool good = kin * off < 0.5f;
                for (int l = 0; good && l < 4; l++)
                {
                    float lat = l == 0 ? off - SvcHalf - 3f : l == 1 ? off : l == 2 ? off + SvcHalf + 2f : off + 9f;
                    float x = (float)(p.x + p.nx * lat * side), z = (float)(p.y + p.ny * lat * side);
                    good = OpenSpot(x, z, 2.5f);
                    if (good && l == 0) good = TrackDist(x, z, lat) >= lat - 1.5f; // no other part of the lap nearer
                }
                ok[q] = good;
            }
            int first = -1; for (int q = 0; q < n; q++) if (!ok[q]) { first = q; break; }
            if (first < 0) continue; // (open all the way round: no road)
            var run = new List<int>();
            for (int c = 1; c <= n; c++)
            {
                int q = (first + c) % n;
                if (ok[q]) { run.Add(q * step); continue; }
                if (run.Count * step * t.ds >= 140.0) ServiceRun(t, run, side, off, step);
                run.Clear();
            }
        }
    }
    static void ServiceRun(Track t, List<int> run, int side, float off, int step)
    {
        Vector2 At(int i, float lat)
        {
            var p = t.pts[((i % t.N) + t.N) % t.N];
            return new Vector2((float)(p.x + p.nx * lat * side), (float)(p.y + p.ny * lat * side));
        }
        // ends: curving away into the park, as far as the land stays open
        var pts = new List<Vector2>(); var lead = new List<Vector2>();
        for (int j = 1; j <= 4; j++) { var a = At(run[0] - j * step, off + 3f * j * j); if (!OpenSpot(a.x, a.y, 2f)) break; lead.Add(a); }
        for (int j = lead.Count - 1; j >= 0; j--) pts.Add(lead[j]);
        foreach (int i in run) pts.Add(At(i, off));
        for (int j = 1; j <= 4; j++) { var a = At(run[run.Count - 1] + j * step, off + 3f * j * j); if (!OpenSpot(a.x, a.y, 2f)) break; pts.Add(a); }
        var arr = new float[pts.Count * 2];
        for (int k = 0; k < pts.Count; k++) { arr[k * 2] = pts[k].x; arr[k * 2 + 1] = pts[k].y; }
        svcRuns.Add(arr);
        for (int k = 0; k + 1 < pts.Count; k++) MarkSegment(pts[k].x, pts[k].y, pts[k + 1].x, pts[k + 1].y, SvcHalf + 2.5f, 1);
        // huts and parked vehicles on the outer side of the road
        float acc = 0f, next = 50f + 110f * Hash(run[0], side + 7);
        for (int k = 1; k < run.Count - 1; k++)
        {
            acc += (float)t.ds * step;
            if (acc < next) continue;
            int i = run[k]; var p = t.pts[i];
            int kind = Mathf.Min(3, (int)(Hash(i, 31) * 4f));
            float lat = off + SvcHalf + (kind == 3 ? 3.4f : 2.9f), reach = kind == 0 ? 5.2f : kind == 1 ? 5.6f : kind == 2 ? 5.2f : 4.2f;
            var c = At(i, lat); float ax = (float)p.tx, az = (float)p.ty;
            if (!OpenSpot(c.x, c.y, 2f) || !OpenSpot(c.x + ax * reach, c.y + az * reach, 1.5f) || !OpenSpot(c.x - ax * reach, c.y - az * reach, 1.5f)) continue;
            svcProps.Add(new SvcProp { x = c.x, z = c.y, ax = ax, az = az, bx = (float)p.nx * side, bz = (float)p.ny * side, reach = reach, kind = kind, seed = i });
            MarkSegment(c.x - ax * reach, c.y - az * reach, c.x + ax * reach, c.y + az * reach, 3.2f, 2);
            acc = 0f; next = 230f + 190f * Hash(i, 33);
        }
    }

    // Park furniture along the service roads (ModelLib props, Medium/High): the park's cast-iron lamp every ~55 m and,
    // every ~130 m, a bench facing the road with a green bin beside it, on the side of the road away from the track
    // (inside the road's flagged strip, so the trees keep off). Not next to a hut / vehicle group. A few hundred
    // vertices each, merged into ModelLib's 1.2 km prop chunks.
    public static int LampCount, BenchCount;
    static void ParkFurniture()
    {
        LampCount = BenchCount = 0;
        int maxLamps = gfx >= 3 ? 24 : 14, maxBench = gfx >= 3 ? 14 : 8;
        foreach (var r in svcRuns)
        {
            int n = r.Length / 2; if (n < 3) continue;
            float acc = 0f, nextLamp = 14f + 20f * Hash(n, 71), nextBench = 40f + 60f * Hash(n, 73);
            for (int k = 1; k < n; k++)
            {
                float x0 = r[k * 2 - 2], z0 = r[k * 2 - 1], x1 = r[k * 2], z1 = r[k * 2 + 1];
                float dx = x1 - x0, dz = z1 - z0, len = Mathf.Sqrt(dx * dx + dz * dz); if (len < 0.01f) continue;
                dx /= len; dz /= len;
                float a0 = acc; acc += len;
                for (int pass = 0; pass < 2; pass++)
                {
                    bool lamp = pass == 0; float at = lamp ? nextLamp : nextBench;
                    while (at <= acc)
                    {
                        float t = at - a0, cx = x0 + dx * t, cz = z0 + dz * t;
                        // the side away from the track
                        float ox = dz, oz = -dx;
                        if (TrackDist(cx + ox * 4f, cz + oz * 4f, 80f) < TrackDist(cx - ox * 4f, cz - oz * 4f, 80f)) { ox = -ox; oz = -oz; }
                        float lat = SvcHalf + (lamp ? 0.7f : 1.3f), x = cx + ox * lat, z = cz + oz * lat;
                        bool ok = !Trackside.Blocked(x, z, 1.2f) && !(Waterfront.HasWater && Waterfront.InWater(x, z));
                        foreach (var sp in svcProps) if (ok && (sp.x - x) * (sp.x - x) + (sp.z - z) * (sp.z - z) < (sp.reach + 4f) * (sp.reach + 4f)) ok = false;
                        if (ok)
                        {
                            float y = Mathf.Min(Ground(x, z), MeshY(x, z));
                            if (lamp && LampCount < maxLamps && ModelLib.AddProp("street_lamp_01", new Vector3(x, y, z), 6.2832f * Hash((int)x, (int)z), 4.6f)) LampCount++;
                            else if (!lamp && BenchCount < maxBench)
                            {
                                // bench front (model -z) towards the road; the bin 1.6 m along, level with the bench's end
                                if (ModelLib.AddProp("park_bench", new Vector3(x, y, z), Mathf.Atan2(ox, oz), 0.9f)) BenchCount++; // (real size: 1.75 m long, 0.9 m high)
                                float bx = x + dx * 1.6f, bz = z + dz * 1.6f;
                                ModelLib.AddProp("park_bin", new Vector3(bx, Mathf.Min(Ground(bx, bz), MeshY(bx, bz)), bz), Mathf.Atan2(ox, oz), 0.95f);
                            }
                        }
                        at += lamp ? 52f + 12f * Hash((int)(x * 3f), 5) : 115f + 40f * Hash((int)(z * 3f), 9);
                    }
                    if (lamp) nextLamp = at; else nextBench = at;
                }
            }
        }
    }

    static void ServiceMeshes()
    {
        var sx = new List<float>(); var sz = new List<float>(); var sd = new List<float>();
        foreach (var r in svcRuns)
        {
            sx.Clear(); sz.Clear(); sd.Clear(); float acc = 0f;
            for (int k = 0; k < r.Length / 2; k++)
            {
                float x = r[k * 2], z = r[k * 2 + 1];
                if (k > 0) acc += Mathf.Sqrt((x - sx[k - 1]) * (x - sx[k - 1]) + (z - sz[k - 1]) * (z - sz[k - 1]));
                sx.Add(x); sz.Add(z); sd.Add(acc);
            }
            if (sx.Count >= 2) Ribbon(sx, sz, sd, 0, sx.Count - 1, SvcHalf, 0.1f, 0.25f + 0.008f, 0.25f + 0.242f, 8f); // (minor-road asphalt)
        }
        foreach (var p in svcProps) ServiceProp(p);
        curTint = new Vector4(1f, 1f, 1f, 0f);
    }

    // paint / glass / tyre colours: linear multipliers of the plaster cell (albedo ~0.8)
    static readonly Vector4 GlassT = new Vector4(0.09f, 0.10f, 0.12f, 0f), TyreT = new Vector4(0.035f, 0.035f, 0.035f, 0f), AmberT = new Vector4(1.1f, 0.42f, 0.02f, 0f);
    static readonly Vector4[] Paints =
    {
        new Vector4(1.0f, 1.0f, 0.98f, 0f), new Vector4(1.0f, 1.0f, 0.98f, 0f), new Vector4(1.0f, 0.24f, 0.03f, 0f), // white, white, safety orange
        new Vector4(0.68f, 0.70f, 0.72f, 0f), new Vector4(0.06f, 0.10f, 0.26f, 0f), new Vector4(0.80f, 0.68f, 0.08f, 0f), // silver, dark blue, yellow
    };
    static void ServiceProp(SvcProp p)
    {
        var c = new Vector3(p.x, 0f, p.z); var A = new Vector3(p.ax, 0f, p.az); var Bv = new Vector3(p.bx, 0f, p.bz);
        float g = Mathf.Min(Ground(p.x, p.z), MeshY(p.x, p.z));
        for (int s = -1; s <= 1; s += 2) g = Mathf.Min(g, Mathf.Min(Ground(p.x + p.ax * p.reach * s, p.z + p.az * p.reach * s), MeshY(p.x + p.ax * p.reach * s, p.z + p.az * p.reach * s)));
        var mb = Buf(SBuild, Block(p.x, p.z), 900);
        int sd = p.seed; Vector4 paint = Paints[(int)(Hash(sd, 41) * Paints.Length) % Paints.Length];
        switch (p.kind)
        {
            case 0: // marshal hut and two portable toilets
                Hut(mb, c, A, Bv, g, sd);
                for (int q = 0; q < 2; q++)
                {
                    var tc = c + A * (3.15f + q * 1.3f);
                    Box(mb, tc, A, Bv, 0.6f, 0.6f, g, g + 2.25f, CPlaster, Grey1, new Vector4(0.06f, 0.20f, 0.62f, 0f));
                    Box(mb, tc, A, Bv, 0.63f, 0.63f, g + 2.25f, g + 2.36f, CPlaster, Grey1, new Vector4(0.95f, 0.95f, 0.93f, 0f));
                }
                break;
            case 1: // a van and a car
                Vehicle(mb, c - A * 2.75f, A, Bv, 5.2f, 2.0f, 2.35f, g, paint, true);
                Vehicle(mb, c + A * 2.95f, A, Bv, 4.4f, 1.8f, 1.5f, g, Paints[(int)(Hash(sd, 43) * Paints.Length) % Paints.Length], false);
                break;
            case 2: // hut and a van
                Hut(mb, c - A * 2.3f, A, Bv, g, sd);
                Vehicle(mb, c + A * 2.45f, A, Bv, 5.2f, 2.0f, 2.35f, g, paint, true);
                break;
            default: // fire tender: red, a white band, roller shutters along the sides (the band reads at distance)
                Vehicle(mb, c, A, Bv, 7.4f, 2.4f, 2.95f, g, new Vector4(0.72f, 0.04f, 0.03f, 0f), true);
                Box(mb, c - A * 0.5f, A, Bv, 3.0f, 1.23f, g + 1.25f, g + 1.42f, CPlaster, Grey1, new Vector4(1f, 1f, 0.97f, 0f));
                break;
        }
        // (a solid for the TV cameras, like the map's buildings)
        Trackside.AddSolid(new Vector3(p.x, 0f, p.z), A, p.reach, 1.6f, g - 1f, g + 3.4f, false);
    }
    // a white site cabin on a low plinth: a dark window band round it, a door at one end, a flat steel roof with an overhang
    static void Hut(MB b, Vector3 c, Vector3 A, Vector3 Bv, float g, int seed)
    {
        Box(b, c, A, Bv, 1.7f, 1.25f, g - 0.3f, g + 2.55f, CPlaster, Grey1, new Vector4(0.98f, 0.97f, 0.93f, 0f));
        Box(b, c - A * 0.35f, A, Bv, 1.05f, 1.28f, g + 1.2f, g + 1.95f, CPlaster, Grey1, GlassT);
        Box(b, c + A * 1.7f + Bv * 0.4f, A, Bv, 0.035f, 0.46f, g + 0.02f, g + 2.08f, CPlaster, Grey1, new Vector4(0.30f, 0.33f, 0.36f, 0f));
        Box(b, c, A, Bv, 1.95f, 1.5f, g + 2.55f, g + 2.74f, CMetal, Grey1, Hash(seed, 45) < 0.5f ? new Vector4(0.33f, 0.40f, 0.36f, 0f) : new Vector4(0.55f, 0.56f, 0.58f, 0f));
    }
    // a parked vehicle: four wheels, the body, glass (van: windscreen and cab windows; car: the glasshouse and a roof),
    // and an amber light bar on top
    static void Vehicle(MB b, Vector3 c, Vector3 A, Vector3 Bv, float len, float wid, float hgt, float g, Vector4 paint, bool van)
    {
        float hl = len * 0.5f, hw = wid * 0.5f, clear = van ? 0.38f : 0.28f, wr = van ? 0.37f : 0.32f, wx = hl - (van ? 0.95f : 0.82f);
        for (int s = -1; s <= 1; s += 2)
            for (int e = -1; e <= 1; e += 2)
                Box(b, c + A * (wx * s) + Bv * ((hw - 0.12f) * e), A, Bv, wr, 0.15f, g, g + wr * 2f, CPlaster, Grey1, TyreT);
        float roof;
        if (van)
        {
            Box(b, c, A, Bv, hl, hw, g + clear, g + hgt, CPlaster, Grey1, paint);
            Box(b, c + A * (hl - 0.36f), A, Bv, 0.38f, hw + 0.03f, g + hgt * 0.55f, g + hgt * 0.86f, CPlaster, Grey1, GlassT);
            roof = g + hgt;
        }
        else
        {
            float hb = clear + (hgt - clear) * 0.55f;
            Box(b, c, A, Bv, hl, hw, g + clear, g + hb, CPlaster, Grey1, paint);
            Box(b, c - A * (hl * 0.1f), A, Bv, hl * 0.5f, hw - 0.07f, g + hb, g + hgt - 0.07f, CPlaster, Grey1, GlassT);
            Box(b, c - A * (hl * 0.1f), A, Bv, hl * 0.46f, hw - 0.11f, g + hgt - 0.07f, g + hgt, CPlaster, Grey1, paint);
            roof = g + hgt;
        }
        Box(b, c + A * (van ? hl - 0.7f : 0f), A, Bv, 0.16f, hw * 0.62f, roof, roof + 0.13f, CPlaster, Grey1, AmberT);
    }

    // Hedges, copses and bushes in the open land (grass, park, farmland, gardens, wetland), from just past the trackside
    // trees out to the tree reach, with a budget of their own: the main budget goes to the woods and the mapped trees
    // close to the track, which left the open fields bare. Hedgerows (squat scrub cards, now and then a tree) along the
    // boundaries between land-use polygons (fields have hedges round them only); copses of mixed trees with a fringe of
    // bushes where a slow noise field peaks; a few lone bushes. The ground under them is darker, rough grass in the land
    // colour map (lflag bit 4), so they stand in their own shade instead of on a clean lawn. Same tree meshes: no draw calls.
    static int OutfieldBudget => gfx <= 1 ? 700 : gfx == 2 ? 1600 : 2800;
    public static int OutfieldCount, FarCount;
    struct OCand { public float x, z, d, ax, az; public byte k; } // k: 0 hedge (two bushes along ax, az), 1 copse tree, 2 bush, 3 hedgerow tree
    static bool RoadOrBuilt(float x, float z) { int k = Texel(x, z); return k >= 0 && (lflag[k] & 3) != 0; }
    static void Stamp(float x, float z, float r)
    {
        int i0 = Mathf.Max(0, (int)((x - r - rx0) / lcx)), i1 = Mathf.Min(lnx - 1, (int)((x + r - rx0) / lcx));
        int j0 = Mathf.Max(0, (int)((z - r - rz0) / lcz)), j1 = Mathf.Min(lnz - 1, (int)((z + r - rz0) / lcz));
        float r2 = r * r;
        for (int j = j0; j <= j1; j++)
        {
            float pz = rz0 + (j + 0.5f) * lcz - z;
            for (int i = i0; i <= i1; i++) { float qx = rx0 + (i + 0.5f) * lcx - x; if (qx * qx + pz * pz <= r2) lflag[j * lnx + i] |= 4; }
        }
    }
    static void Outfield()
    {
        OutfieldCount = 0;
        if (street) return;
        float reach = TreeReach, near = wallLat + 14f;
        var rnd = new System.Random(29); var cand = new List<OCand>();
        for (int j = 1; j < gnz - 1; j++)
            for (int i = 1; i < gnx - 1; i++)
            {
                int cc = Mathf.Min(cnz - 1, j / CH) * cnx + Mathf.Min(cnx - 1, i / CH);
                if (cDist[cc] > reach) { i = (i / CH + 1) * CH - 1; continue; }
                float x = rx0 + (i + 0.5f) * B, z = rz0 + (j + 0.5f) * B;
                float d = Dist(x, z); if (d > reach || d < near) continue;
                int t0 = Texel(x, z); if (t0 < 0 || (lflag[t0] & 3) != 0) continue;
                int id0 = lid[t0], kind = id0 == 0 ? 0 : D.aKind[id0 - 1];
                if (!(kind == 0 || kind == 2 || kind == 3 || kind == 8 || kind == 10)) continue;
                float fade = d < 380f ? 1f : Mathf.Lerp(1f, 0.45f, (d - 380f) / Mathf.Max(1f, reach - 380f));
                // a boundary with the next polygon (east, north): a hedge along it
                for (int dir = 0; dir < 2; dir++)
                {
                    float x1 = dir == 0 ? x + B : x, z1 = dir == 0 ? z : z + B;
                    int t1 = Texel(x1, z1); if (t1 < 0 || lid[t1] == id0) continue;
                    int k1 = lid[t1] == 0 ? 0 : D.aKind[lid[t1] - 1];
                    if (k1 == 1 || k1 == 4 || k1 == 5 || k1 == 7 || k1 == 9 || k1 == 11 || (lflag[t1] & 2) != 0) continue; // woods, paving, water: no hedge
                    float ph = (kind == 2 || k1 == 2 ? 0.7f : kind == 3 || k1 == 3 ? 0.45f : 0.55f) * fade;
                    if (rnd.NextDouble() > ph) continue;
                    float hx = (x + x1) * 0.5f, hz = (z + z1) * 0.5f;
                    cand.Add(new OCand { x = hx, z = hz, d = d, ax = dir == 0 ? 0f : 1f, az = dir == 0 ? 1f : 0f, k = 0 });
                    if (rnd.NextDouble() < 0.18) cand.Add(new OCand { x = hx, z = hz, d = d + 1f, k = 3 });
                }
                if (kind == 2) continue; // (fields: hedges round them only)
                float cop = Noise(x / 115f, z / 115f, 61) * 0.75f + Noise(x / 37f, z / 37f, 63) * 0.25f;
                float pt = (cop - 0.63f) * 1.9f * fade, pb = (cop > 0.55f ? 0.18f : 0.025f) * fade;
                if (kind == 3) { pt *= 0.5f; pb *= 0.6f; }
                if (pt > 0f && d > near + 25f && rnd.NextDouble() < pt) cand.Add(new OCand { x = x + ((float)rnd.NextDouble() - 0.5f) * B, z = z + ((float)rnd.NextDouble() - 0.5f) * B, d = d, k = 1 });
                if (rnd.NextDouble() < pb) cand.Add(new OCand { x = x + ((float)rnd.NextDouble() - 0.5f) * B, z = z + ((float)rnd.NextDouble() - 0.5f) * B, d = d, k = 2 });
            }
        // bushes along the outer side of the service roads, here and there
        foreach (var r in svcRuns)
            for (int k = 1; k + 1 < r.Length / 2; k += 2)
            {
                if (rnd.NextDouble() > 0.3) continue;
                float x0 = r[k * 2 - 2], z0 = r[k * 2 - 1], x1 = r[k * 2 + 2], z1 = r[k * 2 + 3];
                float tx = x1 - x0, tz = z1 - z0, tl = Mathf.Sqrt(tx * tx + tz * tz); if (tl < 1f) continue;
                tx /= tl; tz /= tl;
                // (the outer side: away from the track)
                float ox = -tz, oz = tx, mx = r[k * 2], mz = r[k * 2 + 1];
                if (TrackDist(mx + ox * 6f, mz + oz * 6f, 80f) < TrackDist(mx - ox * 6f, mz - oz * 6f, 80f)) { ox = -ox; oz = -oz; }
                cand.Add(new OCand { x = mx + ox * (SvcHalf + 4.5f), z = mz + oz * (SvcHalf + 4.5f), d = Dist(mx, mz), ax = tx, az = tz, k = 0 });
            }
        cand.Sort((a, b) => a.d.CompareTo(b.d));
        int budget = OutfieldBudget, cards = gfx <= 1 ? 2 : 3;
        float ts = Mathf.Max(1f, env.treeSize);
        foreach (var c in cand)
        {
            if (OutfieldCount >= budget) break;
            int n = c.k == 0 ? 2 : 1;
            for (int q = 0; q < n; q++)
            {
                float x = c.x, z = c.z;
                if (c.k == 0) { float o = (q == 0 ? -0.25f : 0.25f) * B + ((float)rnd.NextDouble() - 0.5f) * 1.2f; x += c.ax * o; z += c.az * o; }
                float clr = c.k == 2 || c.k == 0 ? 1.5f : 3f;
                if (TrackDist(x, z, near + 10f) < near + 4f) continue;
                if (Trackside.Blocked(x, z, clr) || PadDist(x, z) < 8f || (Waterfront.HasWater && Waterfront.InWater(x, z))) continue;
                if (RoadOrBuilt(x, z) || RoadOrBuilt(x + clr, z) || RoadOrBuilt(x - clr, z) || RoadOrBuilt(x, z + clr) || RoadOrBuilt(x, z - clr)) continue;
                if (!Environment.Free(x, z, clr)) continue;
                float y = Mathf.Min(Ground(x, z), MeshY(x, z)) - 0.35f;
                var mb = Buf(STree, Block(x, z), 12);
                bool tree = c.k == 1 || c.k == 3;
                float sz; Color lc;
                if (tree)
                {
                    sz = (0.5f + 0.5f * (float)rnd.NextDouble()) * ts * (c.k == 3 ? 0.9f : 1f);
                    lc = (rnd.NextDouble() < env.tree2Share ? env.leaf2 : env.leaf).linear;
                    Environment.EmitTree(mb.v, mb.n, mb.uv, mb.uv2, mb.col, mb.tri, new Vector3(x, y, z), TreeKind.Broadleaf, sz, lc, rnd, c.d > 320f ? 2 : cards);
                    Stamp(x, z, Mathf.Min(6f, 2.2f * sz));
                }
                else
                {
                    // hedges a little denser and darker than the lone bushes
                    sz = c.k == 0 ? 0.95f + 0.4f * (float)rnd.NextDouble() : 0.7f + 0.7f * (float)rnd.NextDouble();
                    lc = Color.Lerp(env.leaf, env.leaf2, (float)rnd.NextDouble()).linear * (c.k == 0 ? 0.85f : 1f);
                    Environment.EmitTree(mb.v, mb.n, mb.uv, mb.uv2, mb.col, mb.tri, new Vector3(x, y + 0.15f, z), TreeKind.Scrub, sz, lc, rnd, 2);
                    Stamp(x, z, 1.7f * sz);
                }
                OutfieldCount++;
            }
        }
    }

    // ---------------------------------------------------------------- far tree lines
    // Beyond the card trees' reach the woods were flat dark ground, so the horizon past the open fields was a bare line.
    // Now every wood edge facing the circuit (and some field boundaries: Lombardy's rows of trees between the fields),
    // from just inside the card trees' reach out to the end of the fog, gets an opaque tree-line silhouette: one upright
    // strip per ~16 m of edge, its top two rounded crowns of their own heights (now and then a poplar spike), the foot
    // in the crowns' shade. Opaque triangles, no alpha test: no shimmer or mip blobs at 1-2 km, and MSAA smooths the
    // outline. Both faces; normals tipped up like the backdrop's so they light evenly; the fog and the aerial
    // perspective turn them blue-grey with distance. One mesh, one draw call.
    static MB farMB;
    struct FCand { public float x, z, d, ox, oz, h; }
    static void FarTrees()
    {
        farMB = null; FarCount = 0;
        if (street || gEff == null) return;
        float from = TreeReach * 0.78f, to = Mathf.Min(RenderSettings.fogEndDistance * 1.02f, gfx <= 1 ? 1500f : 2300f);
        int cap = gfx <= 1 ? 900 : gfx == 2 ? 2000 : 2600;
        float ch = CanopyHeight(), cx = (bbX0 + bbX1) * 0.5f, cz = (bbZ0 + bbZ1) * 0.5f, keep = Mathf.Min(1f, 1.25f * B / 16f);
        var rnd = new System.Random(41); var cand = new List<FCand>();
        const float L = 8f;
        for (int j = 2; j < gnz - 2; j++)
            for (int i = 2; i < gnx - 2; i++)
            {
                int k = j * gnx + i;
                float x = rx0 + i * B, z = rz0 + j * B;
                float ddx = Mathf.Max(Mathf.Max(bbX0 - x, 0f), x - bbX1), ddz = Mathf.Max(Mathf.Max(bbZ0 - z, 0f), z - bbZ1);
                if (ddx * ddx + ddz * ddz > to * to) continue; // (cheap reject before the field lookup)
                float e = gEff[k]; Vector2 o; float h;
                if (e >= L && (gEff[k - 1] < L || gEff[k + 1] < L || gEff[k - gnx] < L || gEff[k + gnx] < L))
                {
                    o = new Vector2(gEff[k - 1] - gEff[k + 1], gEff[k - gnx] - gEff[k + gnx]); // out of the wood
                    h = ch * (0.85f + 0.3f * (float)rnd.NextDouble());
                }
                else if (e <= 0f)
                {
                    // a field boundary: a row of trees between two fields (only some of them)
                    int t0 = Texel(x, z), t1 = Texel(x + B, z), t2 = Texel(x, z + B);
                    if (t0 < 0 || t1 < 0 || t2 < 0 || lid[t0] == 0) continue;
                    int k0 = D.aKind[lid[t0] - 1]; if (k0 != 2 && k0 != 0 && k0 != 8) continue;
                    bool bx = lid[t1] != lid[t0], bz = lid[t2] != lid[t0]; if (!bx && !bz) continue;
                    int other = lid[bx ? t1 : t2]; int ko = other == 0 ? 0 : D.aKind[other - 1];
                    if (ko != 2 && ko != 0 && ko != 8) continue;
                    if (Hash(lid[t0] * 7 + other, 211) > 0.4f) continue; // the whole boundary has a row or not
                    o = bx ? new Vector2(1f, 0f) : new Vector2(0f, 1f);
                    if (o.x * (cx - x) + o.y * (cz - z) < 0f) o = -o;
                    h = ch * (0.5f + 0.25f * (float)rnd.NextDouble());
                }
                else continue;
                if (rnd.NextDouble() > keep) continue;
                if (o.sqrMagnitude < 1e-4f) continue;
                o.Normalize();
                var tc = new Vector2(cx - x, cz - z).normalized;
                if (o.x * tc.x + o.y * tc.y < -0.2f) continue; // edges facing away from the circuit are hidden behind their wood
                float d = Dist(x, z); if (d < from || d > to) continue;
                if (Trackside.Blocked(x, z, 8f) || (Waterfront.HasWater && Waterfront.InWater(x, z)) || !Environment.Free(x, z, 8f)) continue;
                cand.Add(new FCand { x = x, z = z, d = d, ox = o.x, oz = o.y, h = h });
            }
        cand.Sort((a, b) => a.d.CompareTo(b.d));
        var mb = new MB();
        float w = Mathf.Max(16f, 1.25f * B / keep) * 1.45f;
        foreach (var c in cand)
        {
            if (FarCount >= cap || mb.v.Count > 250000) break;
            var outward = new Vector3(c.ox, 0f, c.oz); var along = new Vector3(-c.oz, 0f, c.ox);
            float y = Mathf.Min(Ground(c.x, c.z), MeshY(c.x, c.z)) - 0.4f;
            float r = (float)rnd.NextDouble(), hue = (float)rnd.NextDouble();
            var leaf = Color.Lerp(env.leaf, env.leaf2, env.tree2Share * r + 0.25f * (float)rnd.NextDouble()).linear * (0.72f + 0.2f * (float)rnd.NextDouble());
            if (hue < 0.2f) { leaf.r *= 1.12f; leaf.b *= 0.8f; } else if (hue < 0.4f) { leaf.r *= 0.85f; leaf.b *= 1.1f; } // olive / blue-green stands
            TreeLine(mb, new Vector3(c.x, y, c.z), along, outward, w * (0.85f + 0.3f * (float)rnd.NextDouble()), c.h, leaf, rnd);
            FarCount++;
        }
        if (mb.tri.Count > 0) farMB = mb;
    }
    // one strip of a far tree line: a base 1 m into the ground, the top two rounded crowns (a notch between them), fanned
    // from the middle of the base; a poplar spike now and then; both faces
    static void TreeLine(MB b, Vector3 foot, Vector3 along, Vector3 outward, float w, float h, Color leaf, System.Random rnd)
    {
        const int P = 9;
        var nrm = (outward * 0.55f + Vector3.up).normalized;
        float h1 = h * (0.8f + 0.35f * (float)rnd.NextDouble()), h2 = h * (0.8f + 0.35f * (float)rnd.NextDouble()), split = 0.38f + 0.24f * (float)rnd.NextDouble();
        Color low = leaf * 0.5f, top = leaf * 1.05f; low.a = top.a = 1f;
        float hm = Mathf.Max(h1, h2);
        int k0 = b.v.Count;
        Vector3 a = foot - along * (w * 0.5f) + Vector3.down, e = foot + along * (w * 0.5f) + Vector3.down, m = foot + Vector3.down;
        Add(b, a, nrm, low); Add(b, e, nrm, low); Add(b, m, nrm, low);
        for (int q = 0; q < P; q++)
        {
            float u = q / (float)(P - 1), hh;
            if (u <= split) { float s = 2f * u / split - 1f; hh = h1 * (0.6f + 0.4f * Mathf.Sqrt(Mathf.Max(0f, 1f - s * s))); }
            else { float s = 2f * (u - split) / (1f - split) - 1f; hh = h2 * (0.6f + 0.4f * Mathf.Sqrt(Mathf.Max(0f, 1f - s * s))); }
            hh *= 0.96f + 0.08f * (float)rnd.NextDouble();
            Add(b, foot + along * ((u - 0.5f) * w) + Vector3.up * hh, nrm, Color.Lerp(low, top, 0.55f + 0.45f * hh / hm));
        }
        // fan from the middle of the base: a -> top0 .. top8 -> e
        Tri2(b, k0 + 2, k0, k0 + 3);
        for (int q = 0; q + 1 < P; q++) Tri2(b, k0 + 2, k0 + 3 + q, k0 + 4 + q);
        Tri2(b, k0 + 2, k0 + 3 + P - 1, k0 + 1);
        if (rnd.NextDouble() < env.poplar * 0.8f)
        {
            float u = 0.2f + 0.6f * (float)rnd.NextDouble(), pw = 1.7f;
            int p0 = b.v.Count; var pc = foot + along * ((u - 0.5f) * w) - outward * 0.5f;
            Add(b, pc - along * pw + Vector3.down, nrm, low); Add(b, pc + along * pw + Vector3.down, nrm, low);
            Add(b, pc + along * (pw * 0.9f) + Vector3.up * (h * 1.05f), nrm, top); Add(b, pc + Vector3.up * (h * 1.5f), nrm, top);
            Add(b, pc - along * (pw * 0.9f) + Vector3.up * (h * 1.05f), nrm, top);
            Tri2(b, p0, p0 + 1, p0 + 2); Tri2(b, p0, p0 + 2, p0 + 3); Tri2(b, p0, p0 + 3, p0 + 4);
        }
    }
    static void Add(MB b, Vector3 p, Vector3 n, Color c) { b.v.Add(p); b.n.Add(n); b.uv.Add(new Vector2(p.x, p.z)); b.col.Add(c); }
    static void Tri2(MB b, int i, int j, int k) { b.tri.Add(i); b.tri.Add(j); b.tri.Add(k); b.tri.Add(i); b.tri.Add(k); b.tri.Add(j); }

    static bool Flagged(float x, float z) { int k = Texel(x, z); return k >= 0 && lflag[k] != 0; }
    static bool Built(float x, float z) { int k = Texel(x, z); return k >= 0 && (lflag[k] & 2) != 0; } // under a building's footprint

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
        Color litter = Color.Lerp(soil * 0.6f, C(0.34f, 0.25f, 0.15f).linear, 0.6f); // dead leaves (oak, hornbeam) on the soil
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
                // the forest floor (FloorMask: the mapped woods and the crowns planted near the track), blended in, never a
                // hard switch: dark, brown-green leaf litter with mossy patches (n1, ~5 m) and darker hollows
                float fm = gFloor != null ? Bilinear(gFloor, x, z) : (kind == 1 && Bilinear(gCan, x, z) > 0.3f ? 1f : 0f);
                if (fm > 0.01f && kind != 4 && kind != 5 && kind != 7 && kind != 11)
                {
                    float lit = Smooth((n1 - 0.38f) / 0.3f);
                    Color moss = Color.Lerp(leafA, leafB, env.tree2Share * n2) * 0.85f;
                    Color fl = Color.Lerp(moss, litter, 0.35f + 0.5f * lit) * (0.78f + 0.12f * n2);
                    c = Color.Lerp(c, fl, fm);
                    if (fm > 0.35f && a < 170) a = 170;
                }
                // under hedges, copses and bushes (Outfield): their shade and rough, uncut grass
                if ((lflag[k] & 4) != 0 && kind != 1) { c *= 0.74f; if (a < 170) a = 170; }
                // large lawns: slow lush / thin drift (~120 m) so a big field is never one flat tone
                else if (kind == 0 || kind == 8)
                {
                    float n3 = noiseTab[(((int)(z * 0.23f + 0.37f * x + 13f) & 255) << 8) + ((int)(x * 0.21f - 0.3f * z + 171f) & 255)]; // (~140 m blobs, rotated off n1)
                    float lush = (n3 - 0.5f) * 0.32f;
                    c = new Color(c.r * (1f - lush * 0.6f), c.g * (1f + lush * 0.25f), c.b * (1f - lush * 0.5f));
                    c *= 1f - 0.12f * Mathf.Abs(n3 - 0.5f);
                }
                float bright = 0.9f + 0.2f * n1;
                float e = Mathf.Min(Mathf.Min(x - rx0, rx1 - x), ez);
                if (e < 150f) { float u = e / 150f; c = Color.Lerp(grass, c, u); if (u < 0.5f) a = 85; }
                px[k] = new Color32(S(c.r * bright), S(c.g * bright), S(c.b * bright), a);
            }
        }
        var tex = new Texture2D(lnx, lnz, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4, name = "RealLand" };
        tex.SetPixels32(px);
        // Low / Medium: GPU-compressed (ETC2 / DXT5, 8 bits per texel instead of 32: 1280^2 = 2.2 MB instead of 8.7 MB, and
        // a quarter of the texture bandwidth on every terrain pixel). The map is smooth (~3 m per texel, multiplied by the
        // detail texture), so the block coding does not show; High keeps the exact RGBA32 map.
        if (gfx <= 2 && (SystemInfo.SupportsTextureFormat(TextureFormat.ETC2_RGBA8) || SystemInfo.SupportsTextureFormat(TextureFormat.DXT5))) { tex.Apply(true, false); tex.Compress(true); tex.Apply(false, true); }
        else tex.Apply(true, true);
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
    static Material TreeMaterial() => Environment.FoliageMaterial();

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
        // the far tree lines: one mesh for the whole region (one draw call), never culled by distance
        if (farMB != null)
        {
            // not baked (old phones): vertex colours are not drawn, so the material carries the woods' average colour
            var fm = WorldBuilder.Mat(WorldBuilder.BakeReady ? Color.white : Color.Lerp(env.leaf, RenderSettings.fogColor, 0.35f), null, 0f);
            Make(farMB, "EnvFarTrees", fm, holder);
            farMB = null;
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
        if (b.t4.Count == b.v.Count) mesh.SetUVs(3, b.t4);
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
