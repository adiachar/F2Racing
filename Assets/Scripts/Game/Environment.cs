// Environment.cs — what the land around each real circuit looks like, as DATA (one EnvDef per circuit id), and the
// builders that turn that data into a few merged, vertex-coloured meshes:
//   * ground: grass / soil palette (WorldBuilder tints the grass texture and the terrain vertices with it)
//   * terrain: Lift(x, z) raises the open land away from the track (valley sides, dunes, a steep town hillside);
//     it is always 0 near the road and near trackside buildings, so nothing the cars or the stands touch moves
//   * trees: kind (spruce, broad-leaf, pine, palm, scrub), mix, density, how close they come to the track
//   * backdrop: one low-poly ring mesh (hills, Alps, limestone cliffs, a far tree line), plus the sea where there is one
//   * landmarks: generic versions of what stands there in reality (observation tower, Ferris wheel, winged pit
//     building, casino, old banking bridge, hillside apartment blocks, yachts), placed by distance along the lap
// The existing CircuitDef fields still drive what they did: style (street -> town rows), forest (tree count),
// water* (harbour). Nothing here carries a real brand or logo.
// Cost: trees 1 draw call (alpha-tested cards, trunks painted in), backdrop 1 (was 2 hill rings), props 1, blocks 1 (shares the town material).
// Everything is lit by LightBake like the rest of the world; colours ride in the vertex tint (BakeTint).
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public enum TreeKind { None, Spruce, Broadleaf, Pine, Palm, Scrub }
public enum MarkKind { Tower, Wheel, Wing, Sculpture, Casino, Hotel, Bridge, Blocks }

// one ring of the distant backdrop, seen from inside
public class EnvRing
{
    public float dist, depth = 160, hMin, hVar, jag;   // m beyond the circuit's half-size; crest height = hMin + 0..hVar
    public Color low, top;                             // colour at the foot and at the highest crests
    public float peakDir = float.NaN, peakGain, peakWidth = 40; // an extra massif towards this compass bearing (deg, 0 = north / +z, 90 = east / +x)
    public float wood;                                 // 0 bare / rock, 1 wooded hills: crowns texture (EnvTex.FarWood), darker, less haze
}

// a landmark, placed relative to the track like the real one
public class EnvMark
{
    public MarkKind kind;
    public float s;        // m along the lap from the start line
    public int side;       // +1 left, -1 right, 2 inside of the bend there, -2 outside, 0 the pit side, 9 middle of the infield,
                           // 8 = by compass: s is then a bearing (deg) and off the distance beyond the circuit's edge
    public float off;      // m from the centreline
    public float size = 1;
}

public class EnvDef
{
    // ground palette (albedo): main cover, second tone in patches, bare soil / rock / sand on high ground
    public Color grass = new Color(0.24f, 0.44f, 0.17f), dry = new Color(0.30f, 0.46f, 0.19f), soil = new Color(0.45f, 0.40f, 0.30f);
    public float patch = 0.3f, patchScale = 140, soilAt = 0;      // share of the second tone, its blob size (m), lift (m) where soil shows (0 = never)
    public float scrub;                                           // share of the bare high ground (soil / rock) under scrub (maquis) in ~45 m patches
    // terrain away from the track
    public float flatTo = 60, ramp = 90;                          // m from the track where the land starts to move, and how gently
    public float rough = 3, roughScale = 220; public bool ridged; // rolling amplitude (m), wavelength (m), sharp dune crests
    public float rise = 0, riseDist = 300;                        // m gained going away from the track (valley sides), over this distance
    // trees
    public TreeKind tree = TreeKind.Broadleaf, tree2 = TreeKind.None; public float tree2Share = 0;
    public float treeMul = 1, treeNear = 26, treeFar = 600, treeSize = 1, clump = 0; // clump 0 = everywhere .. 0.7 = a few copses
    // woods as a mass: beyond canopyAt m from the road the wooded share of the land (cover 0..1) is raised by
    // canopy m and coloured like tree tops, so a few hundred real trees along its edge read as a deep forest
    public float canopy = 0, canopyAt = 48, cover = 0.5f;
    public float poplar = 0.06f;                                  // share of Lombardy poplars among the broad-leaf trees
    public bool realModels;                                       // real tree / shrub / lamp models near the track (ModelLib.cs; Medium and High)
    public Color leaf = new Color(0.19f, 0.42f, 0.21f), leaf2 = new Color(0.19f, 0.42f, 0.21f), trunk = new Color(0.36f, 0.25f, 0.19f);
    // backdrop
    public EnvRing[] rings;
    public float seaDir = float.NaN, seaAt = 520;                 // compass bearing of the sea, m from the circuit's edge to the shore
    // built-up land
    public int blocks; public int floorsMin = 3, floorsMax = 9;   // hillside apartment blocks; storeys of the town rows / blocks
    public Color[] walls;                                         // wall colours of town rows and blocks (null = the default four)
    public bool yachts;
    public EnvMark[] marks;
    // what the car paint reflects below the horizon, and the ambient light from the ground
    public Color groundRefl = new Color(0.13f, 0.16f, 0.12f);
}

public static class Environment
{
    static Color C(float r, float g, float b) => new Color(r, g, b);

    // ================================================================ the table
    static readonly Dictionary<string, EnvDef> Table = new Dictionary<string, EnvDef>
    {
        // Monaco: a city stacked on a steep slope between the harbour and pale limestone mountains. No countryside.
        ["monaco"] = new EnvDef
        {
            grass = C(0.37f, 0.36f, 0.34f), dry = C(0.20f, 0.31f, 0.16f), soil = C(0.44f, 0.41f, 0.36f), patch = 0.2f, patchScale = 70, soilAt = 95, scrub = 0.55f,
            flatTo = 46, ramp = 70, rough = 8, roughScale = 180, rise = 120, riseDist = 300,
            tree = TreeKind.Palm, tree2 = TreeKind.Pine, tree2Share = 0.55f, treeMul = 0.9f, treeNear = 16, treeFar = 340,
            leaf = C(0.22f, 0.40f, 0.18f), leaf2 = C(0.17f, 0.30f, 0.15f), trunk = C(0.40f, 0.33f, 0.26f),
            rings = new[]
            {
                new EnvRing { dist = 470, depth = 240, hMin = 230, hVar = 190, jag = 0.55f, low = C(0.27f, 0.31f, 0.22f), top = C(0.50f, 0.47f, 0.42f), peakDir = 250, peakGain = 170, peakWidth = 35, wood = 0.45f },
                new EnvRing { dist = 860, depth = 300, hMin = 420, hVar = 300, jag = 0.7f, low = C(0.33f, 0.36f, 0.32f), top = C(0.52f, 0.51f, 0.50f) },
            },
            blocks = 240, floorsMin = 5, floorsMax = 13,
            walls = new[] { C(0.93f, 0.87f, 0.74f), C(0.88f, 0.74f, 0.55f), C(0.96f, 0.93f, 0.88f), C(0.90f, 0.78f, 0.66f), C(0.84f, 0.66f, 0.48f) },
            yachts = true,
            marks = new[]
            {
                new EnvMark { kind = MarkKind.Hotel, s = 775, side = 2, off = 34 },    // the grand hotel inside Massenet
                new EnvMark { kind = MarkKind.Casino, s = 868, side = 2, off = 40 },   // the casino, inside the Casino Square right-hander
            },
            groundRefl = C(0.15f, 0.15f, 0.16f),
        },
        // Spa: the Ardennes. Steep valley sides under dense, dark spruce; damp grass.
        ["spa"] = new EnvDef
        {
            grass = C(0.16f, 0.31f, 0.13f), dry = C(0.21f, 0.34f, 0.14f), patch = 0.3f,
            flatTo = 62, ramp = 110, rough = 12, roughScale = 260, rise = 60, riseDist = 300,
            tree = TreeKind.Spruce, tree2 = TreeKind.Broadleaf, tree2Share = 0.12f, treeNear = 22, treeFar = 330, treeSize = 1.1f,
            canopy = 15, canopyAt = 46, cover = 0.82f,
            leaf = C(0.10f, 0.23f, 0.13f), leaf2 = C(0.17f, 0.36f, 0.16f),
            rings = new[]
            {
                new EnvRing { dist = 520, depth = 220, hMin = 95, hVar = 120, jag = 0.15f, low = C(0.11f, 0.22f, 0.15f), top = C(0.15f, 0.27f, 0.19f), wood = 1f },
                new EnvRing { dist = 900, depth = 260, hMin = 190, hVar = 170, jag = 0.15f, low = C(0.20f, 0.30f, 0.28f), top = C(0.27f, 0.36f, 0.36f), wood = 0.8f },
            },
            groundRefl = C(0.10f, 0.14f, 0.10f),
        },
        // Monza: the royal park. Dead flat, tall old trees right behind the barriers, warm haze, far Alps to the north.
        ["monza"] = new EnvDef
        {
            grass = C(0.27f, 0.42f, 0.17f), dry = C(0.37f, 0.44f, 0.20f), patch = 0.35f,
            rough = 1.2f, roughScale = 300,
            tree = TreeKind.Broadleaf, tree2 = TreeKind.Broadleaf, tree2Share = 0.4f, treeMul = 1.25f, treeNear = 9, treeFar = 380, treeSize = 2.2f,
            canopy = 24, canopyAt = 36, cover = 0.72f, poplar = 0.16f, realModels = true,
            leaf = C(0.19f, 0.38f, 0.16f), leaf2 = C(0.27f, 0.42f, 0.15f),
            rings = new[]
            {
                new EnvRing { dist = 430, depth = 60, hMin = 22, hVar = 12, jag = 0.5f, low = C(0.16f, 0.30f, 0.15f), top = C(0.22f, 0.37f, 0.18f), wood = 0.8f },
                new EnvRing { dist = 900, depth = 300, hMin = 20, hVar = 30, jag = 0.6f, low = C(0.40f, 0.46f, 0.48f), top = C(0.55f, 0.59f, 0.63f), peakDir = 0, peakGain = 230, peakWidth = 70 },
            },
            marks = new[] { new EnvMark { kind = MarkKind.Bridge, s = 3580, side = 1, off = 0 } }, // the old banked oval crosses over the Serraglio (where the map data has the bridge)
        },
        // Silverstone: a wartime airfield plateau. Open, windswept fields, hedgerow lines, hardly a tree; the winged pit building.
        ["silverstone"] = new EnvDef
        {
            grass = C(0.29f, 0.44f, 0.18f), dry = C(0.47f, 0.48f, 0.25f), patch = 0.42f, patchScale = 230,
            rough = 1.0f, roughScale = 320,
            tree = TreeKind.Broadleaf, treeMul = 0.45f, treeNear = 60, treeFar = 600, clump = 0.62f, treeSize = 1.1f,
            leaf = C(0.18f, 0.36f, 0.18f),
            rings = new[]
            {
                new EnvRing { dist = 520, depth = 40, hMin = 9, hVar = 7, jag = 0.6f, low = C(0.20f, 0.33f, 0.19f), top = C(0.25f, 0.38f, 0.22f) },
                new EnvRing { dist = 900, depth = 200, hMin = 14, hVar = 14, jag = 0.2f, low = C(0.36f, 0.45f, 0.36f), top = C(0.42f, 0.50f, 0.44f) },
            },
            marks = new[] { new EnvMark { kind = MarkKind.Wing, s = 20, side = 0, off = 84 } },
        },
        // Suzuka: gentle wooded hills, vivid green, the amusement park's big wheel beside the last corner.
        ["suzuka"] = new EnvDef
        {
            grass = C(0.19f, 0.39f, 0.14f), dry = C(0.24f, 0.41f, 0.16f), patch = 0.3f,
            flatTo = 62, rough = 8, roughScale = 240, rise = 14, riseDist = 260,
            tree = TreeKind.Broadleaf, tree2 = TreeKind.Spruce, tree2Share = 0.3f, treeMul = 1.3f, treeNear = 24, treeFar = 420, treeSize = 1.15f,
            canopy = 11, canopyAt = 52, cover = 0.5f,
            leaf = C(0.15f, 0.37f, 0.15f), leaf2 = C(0.10f, 0.26f, 0.14f),
            rings = new[]
            {
                new EnvRing { dist = 500, depth = 200, hMin = 45, hVar = 60, jag = 0.2f, low = C(0.14f, 0.31f, 0.18f), top = C(0.18f, 0.36f, 0.21f), wood = 1f },
                new EnvRing { dist = 900, depth = 280, hMin = 70, hVar = 90, jag = 0.35f, low = C(0.26f, 0.38f, 0.37f), top = C(0.34f, 0.44f, 0.46f), peakDir = 290, peakGain = 220, peakWidth = 60, wood = 0.7f },
            },
            marks = new[] { new EnvMark { kind = MarkKind.Wheel, s = 5560, side = -2, off = 150 } },
        },
        // Austin: dry central Texas. Straw-coloured grass, bare brown patches, scattered live oaks and scrub, a flat horizon.
        ["austin"] = new EnvDef
        {
            grass = C(0.46f, 0.43f, 0.25f), dry = C(0.38f, 0.32f, 0.20f), soil = C(0.47f, 0.38f, 0.26f), patch = 0.38f, patchScale = 120,
            rough = 2.5f, roughScale = 300,
            tree = TreeKind.Broadleaf, tree2 = TreeKind.Scrub, tree2Share = 0.55f, treeMul = 0.9f, treeNear = 45, treeFar = 600, clump = 0.5f, treeSize = 0.85f,
            leaf = C(0.22f, 0.31f, 0.16f), leaf2 = C(0.34f, 0.36f, 0.20f),
            rings = new[]
            {
                new EnvRing { dist = 520, depth = 60, hMin = 7, hVar = 8, jag = 0.6f, low = C(0.36f, 0.37f, 0.24f), top = C(0.30f, 0.35f, 0.20f) },
                new EnvRing { dist = 900, depth = 240, hMin = 16, hVar = 16, jag = 0.2f, low = C(0.42f, 0.40f, 0.29f), top = C(0.45f, 0.44f, 0.35f) },
            },
            marks = new[] { new EnvMark { kind = MarkKind.Tower, s = 4700, side = 2, off = 85 } }, // the observation tower in the arc of T16-T18
            groundRefl = C(0.17f, 0.16f, 0.12f),
        },
        // Hungaroring: a dusty natural bowl. Sun-bleached yellow-green banks the crowd sits on, some trees on the rim.
        ["budapest"] = new EnvDef
        {
            grass = C(0.45f, 0.48f, 0.23f), dry = C(0.58f, 0.53f, 0.30f), soil = C(0.55f, 0.47f, 0.32f), patch = 0.45f, patchScale = 110,
            flatTo = 58, ramp = 80, rough = 7, roughScale = 230, rise = 28, riseDist = 240,
            tree = TreeKind.Broadleaf, tree2 = TreeKind.Scrub, tree2Share = 0.3f, treeMul = 1.5f, treeNear = 50, treeFar = 520, clump = 0.42f,
            leaf = C(0.21f, 0.37f, 0.16f), leaf2 = C(0.33f, 0.40f, 0.20f),
            rings = new[]
            {
                new EnvRing { dist = 500, depth = 200, hMin = 40, hVar = 45, jag = 0.15f, low = C(0.36f, 0.42f, 0.23f), top = C(0.44f, 0.47f, 0.27f), wood = 0.5f },
                new EnvRing { dist = 900, depth = 260, hMin = 70, hVar = 70, jag = 0.2f, low = C(0.42f, 0.47f, 0.36f), top = C(0.50f, 0.53f, 0.44f) },
            },
            groundRefl = C(0.16f, 0.16f, 0.11f),
        },
        // Barcelona: dry Mediterranean. Ochre earth, umbrella pines and olive-grey scrub, low hills, a jagged pale massif far to the north-west.
        ["catalunya"] = new EnvDef
        {
            grass = C(0.39f, 0.41f, 0.23f), dry = C(0.50f, 0.41f, 0.26f), soil = C(0.52f, 0.42f, 0.28f), patch = 0.42f, patchScale = 100, soilAt = 18,
            rough = 9, roughScale = 240, rise = 14, riseDist = 280,
            tree = TreeKind.Pine, tree2 = TreeKind.Scrub, tree2Share = 0.4f, treeMul = 1.8f, treeNear = 36, treeFar = 480, clump = 0.2f,
            canopy = 8, canopyAt = 64, cover = 0.26f,
            leaf = C(0.17f, 0.30f, 0.14f), leaf2 = C(0.34f, 0.38f, 0.22f), trunk = C(0.42f, 0.30f, 0.22f),
            rings = new[]
            {
                new EnvRing { dist = 500, depth = 200, hMin = 40, hVar = 55, jag = 0.2f, low = C(0.27f, 0.32f, 0.19f), top = C(0.40f, 0.38f, 0.25f) },
                new EnvRing { dist = 900, depth = 280, hMin = 60, hVar = 70, jag = 0.9f, low = C(0.38f, 0.39f, 0.33f), top = C(0.52f, 0.50f, 0.47f), peakDir = 300, peakGain = 260, peakWidth = 28 },
            },
            groundRefl = C(0.17f, 0.15f, 0.11f),
        },
        // Red Bull Ring: Styria. Bright alpine meadow, dark spruce woods, and big green mountains closing in on every side.
        ["spielberg"] = new EnvDef
        {
            grass = C(0.20f, 0.40f, 0.14f), dry = C(0.26f, 0.43f, 0.16f), patch = 0.3f,
            flatTo = 62, ramp = 110, rough = 10, roughScale = 260, rise = 70, riseDist = 300,
            tree = TreeKind.Spruce, tree2 = TreeKind.Broadleaf, tree2Share = 0.2f, treeMul = 1.1f, treeNear = 40, treeFar = 380, clump = 0.4f, treeSize = 1.1f,
            canopy = 14, canopyAt = 70, cover = 0.42f,
            leaf = C(0.10f, 0.25f, 0.13f), leaf2 = C(0.18f, 0.38f, 0.16f),
            rings = new[]
            {
                new EnvRing { dist = 470, depth = 300, hMin = 240, hVar = 230, jag = 0.3f, low = C(0.12f, 0.27f, 0.15f), top = C(0.27f, 0.45f, 0.21f), wood = 0.9f },
                new EnvRing { dist = 860, depth = 340, hMin = 480, hVar = 380, jag = 0.75f, low = C(0.22f, 0.34f, 0.30f), top = C(0.60f, 0.62f, 0.64f) },
            },
            marks = new[] { new EnvMark { kind = MarkKind.Sculpture, side = 9 } }, // the big steel sculpture in the infield (generic shape)
        },
        // Zandvoort: North Sea dunes. Pale sand, grey-green marram grass, low scrub and wind-bent pines; the sea behind the dunes.
        ["zandvoort"] = new EnvDef
        {
            grass = C(0.38f, 0.43f, 0.25f), dry = C(0.60f, 0.54f, 0.39f), soil = C(0.64f, 0.58f, 0.43f), patch = 0.5f, patchScale = 60, soilAt = 9,
            flatTo = 38, ramp = 50, rough = 11, roughScale = 120, ridged = true,
            tree = TreeKind.Scrub, tree2 = TreeKind.Pine, tree2Share = 0.18f, treeMul = 1.6f, treeNear = 22, treeFar = 420, treeSize = 0.8f, clump = 0.25f,
            leaf = C(0.33f, 0.42f, 0.24f), leaf2 = C(0.16f, 0.29f, 0.16f),
            rings = new[] { new EnvRing { dist = 500, depth = 120, hMin = 12, hVar = 14, jag = 0.7f, low = C(0.38f, 0.42f, 0.26f), top = C(0.60f, 0.55f, 0.40f) } },
            seaDir = 285, seaAt = 560,
            marks = new[] { new EnvMark { kind = MarkKind.Blocks, s = 195, side = 8, off = 250 } }, // the seaside town's blocks, south of the circuit
            groundRefl = C(0.18f, 0.18f, 0.13f),
        },
    };
    // any other circuit (the test track): the old green countryside
    static readonly EnvDef Default = new EnvDef
    {
        rings = new[]
        {
            new EnvRing { dist = 620, depth = 140, hMin = 60, hVar = 95, low = C(0.30f, 0.40f, 0.33f), top = C(0.30f, 0.40f, 0.33f) },
            new EnvRing { dist = 980, depth = 140, hMin = 130, hVar = 170, low = C(0.38f, 0.47f, 0.50f), top = C(0.38f, 0.47f, 0.50f) },
        },
    };

    public static EnvDef For(Track t)
    {
        EnvDef e;
        return t != null && t.def != null && t.def.id != null && Table.TryGetValue(t.def.id, out e) ? e : Default;
    }

    // ================================================================ state for the circuit being built
    public static EnvDef Cur = Default;
    // landmark positions (x, ground y, z, height) of the circuit just built, e.g. for cameras and previews
    public static readonly List<Vector4> Landmarks = new List<Vector4>();
    static Track trk;
    static float[] dist; static int fnx, fnz; static float fx0, fz0, fx1, fz1; const float FCell = 12f;
    struct Placed { public EnvMark m; public Vector3 c, T, D; public int i, sg; public float r; }
    static readonly List<Placed> marks = new List<Placed>();
    struct Spot { public float x, z, r; }
    static readonly List<Spot> spots = new List<Spot>(); // ground taken by landmarks and blocks (trees keep out)

    public static void Prepare(Track t)
    {
        trk = t; Cur = For(t); dist = null; spots.Clear(); Landmarks.Clear(); marks.Clear();
    }

    // Distance (m) from every 12 m cell of the rectangle to the nearest road, trackside building or the test pad.
    // Call once Trackside has laid out its buildings, before the terrain mesh is made.
    public static void BuildField(Track t, float x0, float z0, float w, float h)
    {
        fx0 = x0; fz0 = z0; fx1 = x0 + w; fz1 = z0 + h;
        fnx = Mathf.CeilToInt(w / FCell) + 1; fnz = Mathf.CeilToInt(h / FCell) + 1;
        var d = new float[fnx * fnz];
        for (int i = 0; i < d.Length; i++) d[i] = 1e6f;
        for (int i = 0; i < t.N; i++)
        {
            int cx = Mathf.RoundToInt(((float)t.pts[i].x - x0) / FCell), cz = Mathf.RoundToInt(((float)t.pts[i].y - z0) / FCell);
            if (cx >= 0 && cz >= 0 && cx < fnx && cz < fnz) d[cz * fnx + cx] = 0;
        }
        for (int j = 0; j < fnz; j++)
            for (int i = 0; i < fnx; i++)
            {
                float x = x0 + i * FCell, z = z0 + j * FCell;
                if (Trackside.Blocked(x, z, FCell * 0.75f) || WorldBuilder.Pad.Contains(new Vector2(x, z))) d[j * fnx + i] = 0;
            }
        // two-pass chamfer distance transform
        const float A = FCell, B = FCell * 1.4142f;
        for (int j = 0; j < fnz; j++)
            for (int i = 0; i < fnx; i++)
            {
                int k = j * fnx + i; float v = d[k];
                if (i > 0) v = Mathf.Min(v, d[k - 1] + A);
                if (j > 0) { v = Mathf.Min(v, d[k - fnx] + A); if (i > 0) v = Mathf.Min(v, d[k - fnx - 1] + B); if (i < fnx - 1) v = Mathf.Min(v, d[k - fnx + 1] + B); }
                d[k] = v;
            }
        for (int j = fnz - 1; j >= 0; j--)
            for (int i = fnx - 1; i >= 0; i--)
            {
                int k = j * fnx + i; float v = d[k];
                if (i < fnx - 1) v = Mathf.Min(v, d[k + 1] + A);
                if (j < fnz - 1) { v = Mathf.Min(v, d[k + fnx] + A); if (i < fnx - 1) v = Mathf.Min(v, d[k + fnx + 1] + B); if (i > 0) v = Mathf.Min(v, d[k + fnx - 1] + B); }
                d[k] = v;
            }
        dist = d;
        PlaceMarks(t);
    }

    // m to the nearest road or trackside building (0 outside the terrain rectangle)
    public static float Clear(float x, float z)
    {
        if (dist == null) return 0;
        float gx = (x - fx0) / FCell, gz = (z - fz0) / FCell;
        if (gx < 0 || gz < 0 || gx >= fnx - 1 || gz >= fnz - 1) return 0;
        int i = (int)gx, j = (int)gz; float a = gx - i, b = gz - j; int k = j * fnx + i;
        return (dist[k] * (1 - a) + dist[k + 1] * a) * (1 - b) + (dist[k + fnx] * (1 - a) + dist[k + fnx + 1] * a) * b;
    }
    // 0 at the rim of the terrain rectangle .. 1 well inside it (the lift and the colour patches fade out at the rim)
    static float Rim(float x, float z)
    {
        if (dist == null) return 0;
        float e = Mathf.Min(Mathf.Min(x - fx0, fx1 - x), Mathf.Min(z - fz0, fz1 - z));
        return Mathf.SmoothStep(0, 1, e / 110f);
    }

    // ---------------------------------------------------------------- noise
    static float Hash(int x, int y, int s)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + s * 2147483;
            h = (h ^ (int)((uint)h >> 13)) * 1274126177;
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296f;
        }
    }
    static float Noise(float x, float y, int s)
    {
        int i = Mathf.FloorToInt(x), j = Mathf.FloorToInt(y); float a = x - i, b = y - j;
        a = a * a * (3 - 2 * a); b = b * b * (3 - 2 * b);
        return Mathf.Lerp(Mathf.Lerp(Hash(i, j, s), Hash(i + 1, j, s), a), Mathf.Lerp(Hash(i, j + 1, s), Hash(i + 1, j + 1, s), a), b);
    }
    static float Fbm(float x, float y, int s) => (Noise(x, y, s) + 0.5f * Noise(x * 2.03f, y * 2.03f, s + 1) + 0.25f * Noise(x * 4.1f, y * 4.1f, s + 2)) / 1.75f;

    // ================================================================ terrain
    // Extra height of the open land at (x, z), on top of the circuit's own height field. 0 near the road.
    public static float Lift(float x, float z)
    {
        if (dist == null || RealWorld.Active) return 0; // (real terrain: RealWorld.cs)
        var e = Cur; float d = Clear(x, z);
        if (d <= e.flatTo) return 0;
        float f = Mathf.SmoothStep(0, 1, (d - e.flatTo) / e.ramp) * Rim(x, z);
        if (f <= 0) return 0;
        float n = Fbm(x / e.roughScale, z / e.roughScale, 11);
        if (e.ridged) { n = 1f - Mathf.Abs(n * 2f - 1f); n = n * n * 1.6f; }  // dunes: sharp crests, wide hollows
        else n = 0.25f + n;
        float up = e.rise > 0 ? e.rise * Mathf.SmoothStep(0, 1, (d - e.flatTo) / e.riseDist) : 0;
        return f * (e.rough * n + up * (0.75f + 0.5f * Noise(x / 400f, z / 400f, 5)));
    }
    // 0 = open land .. 1 = inside a wood (the raised, tree-top coloured part of the terrain)
    public static float Canopy(float x, float z)
    {
        var e = Cur; if (dist == null || e.canopy <= 0 || RealWorld.Active) return 0;
        float d = Clear(x, z); if (d <= e.canopyAt) return 0;
        float thr = 0.5f + (0.5f - e.cover) * 0.5f;
        float m = Mathf.SmoothStep(0, 1, (Fbm(x / 210f, z / 210f, 91) - thr + 0.05f) / 0.1f) * Mathf.SmoothStep(0, 1, (d - e.canopyAt) / 7f) * Rim(x, z);
        if (m <= 0) return 0;
        // clearings round the landmarks
        for (int k = 0; k < marks.Count; k++) { float dx = x - marks[k].c.x, dz = z - marks[k].c.z, r = marks[k].r + 30f; m *= Mathf.SmoothStep(0, 1, (Mathf.Sqrt(dx * dx + dz * dz) - r) / 14f); }
        return m;
    }
    // the ground itself (landmarks and blocks stand on it), without the tree tops
    public static float GroundY(float x, float z) => RealWorld.Ready ? RealWorld.GroundY(x, z) : WorldBuilder.H(x, z) + Lift(x, z);
    // the terrain mesh: the ground. (The woods used to raise it into a canopy mass, which read as faceted green hills;
    // they are now rows of trees and forest-edge bands of cards, Trees below, over a dark forest floor, GroundTint.)
    public static float Surface(float x, float z, float lift) => lift;

    // colour of the ground at (x, z) relative to the grass texture's own colour (multiplied in after the light bake)
    public static Color GroundTint(float x, float z, float lift)
    {
        var e = Cur; float rim = Rim(x, z);
        float pn = Fbm(x / e.patchScale, z / e.patchScale, 31);
        // spread the noise so the "patch" share is roughly the share of land in the second tone
        float a = Mathf.SmoothStep(0, 1, (pn - (0.5f + (0.5f - e.patch) * 0.45f) + 0.05f) / 0.1f) * rim;
        Color c = Color.Lerp(e.grass, e.dry, a);
        if (e.soilAt > 0)
        {
            float s = Mathf.SmoothStep(0, 1, (lift - e.soilAt * 0.55f) / (e.soilAt * 0.45f)) * (0.55f + 0.45f * Noise(x / 37f, z / 37f, 41));
            c = Color.Lerp(c, e.soil, s);
            if (e.scrub > 0)
            {
                // the rock is not bare everywhere: dark scrub (maquis, pines) in patches, more in the hollows (load time)
                float sn = 0.65f * Noise(x / 45f, z / 45f, 47) + 0.35f * Noise(x / 13f, z / 13f, 49);
                float sc = Mathf.SmoothStep(0, 1, (sn - (1f - e.scrub)) / 0.18f) * s;
                c = Color.Lerp(c, Color.Lerp(e.leaf, e.leaf2, Noise(x / 30f, z / 30f, 53)) * (0.7f + 0.25f * Noise(x / 9f, z / 9f, 57)), sc);
            }
        }
        float cn = Canopy(x, z);
        if (cn > 0) { var lf = Color.Lerp(e.leaf, e.leaf2, e.tree2Share * Noise(x / 60f, z / 60f, 95)) * (0.42f + 0.2f * Noise(x / 15f, z / 15f, 97) + 0.16f * Hash(Mathf.RoundToInt(x), Mathf.RoundToInt(z), 98)); c = Color.Lerp(c, lf, Mathf.Clamp01(cn * 5f)); }
        float k = 0.93f + 0.14f * Noise(x / 23f, z / 23f, 43); // mottling
        return new Color(c.r / e.grass.r * k, c.g / e.grass.g * k, c.b / e.grass.b * k, 1f);
    }

    // ================================================================ mesh helpers
    class Buf
    {
        public List<Vector3> v = new List<Vector3>(); public List<Vector2> uv = new List<Vector2>();
        public List<int> tri = new List<int>(); public List<Color> col = new List<Color>();
    }
    // flat quad seen from the side "outward" points to; colour c0 on p0, p1 (the foot) and c1 on p2, p3 (the top)
    static void Quad(Buf b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 outward, Color c0, Color c1, float uu = 0, float vv = 0)
    {
        Vector2 u0 = new Vector2(0, 0), u1 = new Vector2(uu, 0), u2 = new Vector2(uu, vv), u3 = new Vector2(0, vv);
        Color k0 = c0, k1 = c0, k2 = c1, k3 = c1;
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0)
        {
            var tp = p1; p1 = p3; p3 = tp; var tu = u1; u1 = u3; u3 = tu; var tc = k1; k1 = k3; k3 = tc;
        }
        int i = b.v.Count;
        b.v.Add(p0); b.v.Add(p1); b.v.Add(p2); b.v.Add(p3);
        b.uv.Add(u0); b.uv.Add(u1); b.uv.Add(u2); b.uv.Add(u3);
        b.col.Add(k0); b.col.Add(k1); b.col.Add(k2); b.col.Add(k3);
        b.tri.Add(i); b.tri.Add(i + 1); b.tri.Add(i + 2); b.tri.Add(i); b.tri.Add(i + 2); b.tri.Add(i + 3);
    }
    static void Quad(Buf b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 outward, Color c) => Quad(b, p0, p1, p2, p3, outward, c, c);
    // seen from both sides (thin things: rims, ribbons, palm fronds)
    static void Quad2(Buf b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color c)
    {
        var n = Vector3.Cross(p1 - p0, p2 - p0);
        Quad(b, p0, p1, p2, p3, n, c); Quad(b, p0, p1, p2, p3, -n, c);
    }
    static void Tri(Buf b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 outward, Color c)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0) { var tp = p1; p1 = p2; p2 = tp; }
        int i = b.v.Count;
        b.v.Add(p0); b.v.Add(p1); b.v.Add(p2);
        for (int k = 0; k < 3; k++) { b.uv.Add(Vector2.zero); b.col.Add(c); }
        b.tri.Add(i); b.tri.Add(i + 1); b.tri.Add(i + 2);
    }
    // box round centre c with half-sizes ex, ey, ez along the unit axes ax, ay, az (no bottom face)
    static void Box(Buf b, Vector3 c, Vector3 ax, Vector3 ay, Vector3 az, float ex, float ey, float ez, Color col, bool bottom = false)
    {
        Vector3 x = ax * ex, y = ay * ey, z = az * ez;
        Quad(b, c + x - y - z, c + x - y + z, c + x + y + z, c + x + y - z, ax, col);
        Quad(b, c - x - y - z, c - x - y + z, c - x + y + z, c - x + y - z, -ax, col);
        Quad(b, c + z - y - x, c + z - y + x, c + z + y + x, c + z + y - x, az, col);
        Quad(b, c - z - y - x, c - z - y + x, c - z + y + x, c - z + y - x, -az, col);
        Quad(b, c + y - x - z, c + y + x - z, c + y + x + z, c + y - x + z, ay, col);
        if (bottom) Quad(b, c - y - x - z, c - y + x - z, c - y + x + z, c - y - x + z, -ay, col);
    }
    // four-sided pyramid roof on a w x d base
    static void Pyramid(Buf b, Vector3 c, Vector3 ax, Vector3 az, float ex, float ez, float h, Color col)
    {
        Vector3 x = ax * ex, z = az * ez, tip = c + Vector3.up * h;
        Tri(b, c - x - z, c + x - z, tip, -az + Vector3.up, col); Tri(b, c + x - z, c + x + z, tip, ax + Vector3.up, col);
        Tri(b, c + x + z, c - x + z, tip, az + Vector3.up, col); Tri(b, c - x + z, c - x - z, tip, -ax + Vector3.up, col);
    }
    static GameObject Emit(string name, Buf b, Material m, Transform root)
    {
        if (b.tri.Count == 0) return null;
        var go = WorldBuilder.MeshObj(name, b.v, b.uv, b.tri, m, false);
        go.transform.SetParent(root, false);
        BakeTint.Add(go.GetComponent<MeshFilter>().sharedMesh, b.col.ToArray());
        return go;
    }
    static Vector3 Dir(float bearingDeg) { float a = bearingDeg * Mathf.Deg2Rad; return new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)); }

    // ================================================================ backdrop: rings of distant land, and the sea
    public static void Backdrop(Transform parent, float cx, float cy, float ext)
    {
        var e = Cur; Buf bPlain = new Buf(), bWood = new Buf();
        var haze = RenderSettings.fogColor;
        bool sea = !float.IsNaN(e.seaDir) && !RealWorld.HasWaterAreas;
        bool real = RealWorld.Active && RealWorld.HasTerrain;
        Vector3 seaD = sea ? Dir(e.seaDir) : Vector3.zero; float shore = ext * 0.5f + e.seaAt;
        var mid = new Vector3(cx, 0, cy);
        const int Seg = 96;
        if (e.rings != null)
            for (int r = 0; r < e.rings.Length; r++)
            {
                var g = e.rings[r]; int seed = 3 + r * 8;
                float radius = ext * 0.5f + g.dist;
                if (real)
                {
                    // the real terrain covers the near land: low tree lines go, hills and mountains stand beyond it
                    if (g.hMin + g.hVar + (float.IsNaN(g.peakDir) ? 0 : g.peakGain) < 45f) continue;
                    radius = Mathf.Max(radius, RealWorld.Reach + 150f + 300f * r);
                }
                // aerial perspective: further rings lean more towards the haze colour, so they separate and melt into the horizon
                // (strong, bright aerial perspective: distant land is a soft, light blue-grey that melts into the sky, never a
                // dark wall. The tint is a linear multiplier of the baked light, which is about 1.6 on these up-turned faces,
                // so the haze target is the fog colour in linear space over 1.6: far crests end up at the fog colour on screen)
                float hz = Mathf.Lerp(0.30f, 0.56f, Mathf.Clamp01((g.dist - 450f) / 500f));
                // wooded hills (gap list item 18: the Ardennes, Suzuka's hills read as bare pale shapes): the crowns
                // texture (EnvTex.FarWood, one read like the white texture before), the tint divided by its mean so the
                // average is kept, then darker (forest albedo) and a little less haze: dark blue-green woods
                bool wood = g.wood > 0.01f && WorldBuilder.BakeReady;
                var rb = wood ? bWood : bPlain;
                if (wood) hz *= 1f - 0.45f * g.wood; // (the linear fog still hazes them on top: Spa's hills were near the fog colour)
                Color hazeL = haze.linear / 1.6f;
                Color low = Color.Lerp(g.low, hazeL, hz), top = Color.Lerp(g.top, hazeL, Mathf.Min(1f, hz + 0.2f));
                if (wood) { float k = (1f - 0.3f * g.wood) / Mathf.Max(0.05f, EnvTex.FarWoodMean); low *= k; top *= k; low.a = top.a = 1f; }
                float uScale = radius / 36f, vScale = 1f / 36f; // ~36 m per tile along the ring and up the slope
                float Crest(int i)
                {
                    float a = i * Mathf.PI * 2 / Seg;
                    float roll = 0.5f + 0.28f * Mathf.Sin(a * 3 + seed) + 0.14f * Mathf.Sin(a * 7 + seed * 2.3f) + 0.08f * Mathf.Sin(a * 17 + seed * 0.7f);
                    float jag = (Hash(i % Seg, seed, 7) - 0.5f) * 0.9f + (Hash((i % Seg) / 2, seed, 9) - 0.5f) * 0.6f;
                    float h = g.hMin + g.hVar * Mathf.Clamp01(roll + jag * g.jag);
                    if (!float.IsNaN(g.peakDir))
                    {
                        float brg = Mathf.Atan2(Mathf.Cos(a), Mathf.Sin(a)) * Mathf.Rad2Deg; // (x, z) = (cos a, sin a) -> compass bearing
                        float off = Mathf.Abs(Mathf.DeltaAngle(brg, g.peakDir)) / g.peakWidth;
                        if (off < 1) h += g.peakGain * (1 - off * off) * (1 - off * off) * (0.75f + 0.5f * Hash(i % Seg, seed, 13) * g.jag);
                    }
                    return h;
                }
                float hTop = g.hMin + g.hVar + (float.IsNaN(g.peakDir) ? 0 : g.peakGain);
                for (int i = 0; i < Seg; i++)
                {
                    float a0 = i * Mathf.PI * 2 / Seg, a1 = (i + 1) * Mathf.PI * 2 / Seg;
                    Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                    Vector3 p0 = mid + d0 * radius, p1 = mid + d1 * radius;
                    if (Waterfront.InWater(p0.x, p0.z) || Waterfront.InWater(p1.x, p1.z)) continue;              // the harbour mouth: open sea
                    if (sea && (Vector3.Dot(p0 - mid, seaD) > shore - 60 || Vector3.Dot(p1 - mid, seaD) > shore - 60)) continue;
                    float h0 = Crest(i), h1 = Crest(i + 1);
                    // two bands: a steeper foot and a crest leaning back, so the slope catches the light like a hillside
                    Vector3 m0 = mid + d0 * (radius + g.depth * 0.45f) + Vector3.up * (h0 * 0.62f), m1 = mid + d1 * (radius + g.depth * 0.45f) + Vector3.up * (h1 * 0.62f);
                    Vector3 t0 = mid + d0 * (radius + g.depth) + Vector3.up * h0, t1 = mid + d1 * (radius + g.depth) + Vector3.up * h1;
                    p0.y = p1.y = -4;
                    var inward = mid + Vector3.up * 60 - (p0 + p1) * 0.5f;
                    Color cm0 = Color.Lerp(low, top, Mathf.Clamp01(h0 * 0.62f / hTop)), cm1 = Color.Lerp(low, top, Mathf.Clamp01(h1 * 0.62f / hTop));
                    Color ct0 = Color.Lerp(low, top, Mathf.Clamp01(h0 / hTop * 1.15f)), ct1 = Color.Lerp(low, top, Mathf.Clamp01(h1 / hTop * 1.15f));
                    if (wood)
                    {
                        Vector2 q0 = new Vector2(a0 * uScale, p0.y * vScale), q1 = new Vector2(a1 * uScale, p1.y * vScale);
                        Vector2 n0 = new Vector2(a0 * uScale, m0.y * vScale), n1 = new Vector2(a1 * uScale, m1.y * vScale);
                        Vector2 w0 = new Vector2(a0 * uScale, t0.y * vScale), w1 = new Vector2(a1 * uScale, t1.y * vScale);
                        Band(rb, p0, p1, m1, m0, inward, low, low, cm1, cm0, q0, q1, n1, n0);
                        Band(rb, m0, m1, t1, t0, inward, cm0, cm1, ct1, ct0, n0, n1, w1, w0);
                    }
                    else
                    {
                        Band(rb, p0, p1, m1, m0, inward, low, low, cm1, cm0);
                        Band(rb, m0, m1, t1, t0, inward, cm0, cm1, ct1, ct0);
                    }
                }
            }
        var b = bPlain;
        if (sea)
        {
            // a beach, then the sea out to the horizon (a flat sheet: the dunes hide most of it from the track)
            Vector3 side = Vector3.Cross(Vector3.up, seaD), o = mid + seaD * shore;
            Color sand = e.soil, surf = new Color(0.46f, 0.53f, 0.53f), deep = Color.Lerp(new Color(0.17f, 0.27f, 0.33f), haze / 1.4f, 0.3f);
            float[] at = { -70, 0, 45, 400, 4500 }; Color[] cc = { sand, sand, surf, deep, deep }; float[] yy = { 0.6f, 0.25f, 0.2f, 0.2f, 0.2f };
            for (int k = 0; k + 1 < at.Length; k++)
                for (int q = -4; q < 4; q++)
                {
                    Vector3 a0 = o + side * (q * 1500f) + seaD * at[k] + Vector3.up * yy[k], a1 = o + side * ((q + 1) * 1500f) + seaD * at[k] + Vector3.up * yy[k];
                    Vector3 b0 = o + side * (q * 1500f) + seaD * at[k + 1] + Vector3.up * yy[k + 1], b1 = o + side * ((q + 1) * 1500f) + seaD * at[k + 1] + Vector3.up * yy[k + 1];
                    Band(b, a0, a1, b1, b0, Vector3.up, cc[k], cc[k], cc[k + 1], cc[k + 1]);
                }
        }
        // not baked (old phones): vertex colours are not drawn, so the material carries an average colour
        Color avg = e.rings != null && e.rings.Length > 0 ? Color.Lerp(e.rings[0].low, haze, 0.15f) : new Color(0.3f, 0.4f, 0.33f);
        var bgo = Emit("EnvBackdrop", b, WorldBuilder.Mat(WorldBuilder.BakeReady ? Color.white : avg, null, 0f), parent);
        SkyNormals(bgo);
        // the wooded rings: one more draw call (only on circuits that have them), same shader, the crowns texture
        SkyNormals(Emit("EnvBackdropWood", bWood, WorldBuilder.Mat(Color.white, EnvTex.FarWood(), 0f), parent));
    }
    // normals turned mostly skywards: distant slopes are lit almost evenly (the haze washes out their shading),
    // so the side of the ring away from the sun is not a dark band
    static void SkyNormals(GameObject bgo)
    {
        if (bgo == null) return;
        var bm = bgo.GetComponent<MeshFilter>().sharedMesh; var bn = bm.normals;
        for (int i = 0; i < bn.Length; i++) bn[i] = (bn[i] + Vector3.up * 1.6f).normalized;
        bm.normals = bn;
    }
    // quad with a colour per corner
    static void Band(Buf b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 outward, Color c0, Color c1, Color c2, Color c3)
        => Band(b, p0, p1, p2, p3, outward, c0, c1, c2, c3, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
    // ... and a uv per corner
    static void Band(Buf b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 outward, Color c0, Color c1, Color c2, Color c3,
                     Vector2 u0, Vector2 u1, Vector2 u2, Vector2 u3)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0)
        {
            var tp = p1; p1 = p3; p3 = tp; var tc = c1; c1 = c3; c3 = tc; var tu = u1; u1 = u3; u3 = tu;
        }
        int i = b.v.Count;
        b.v.Add(p0); b.v.Add(p1); b.v.Add(p2); b.v.Add(p3);
        b.col.Add(c0); b.col.Add(c1); b.col.Add(c2); b.col.Add(c3);
        b.uv.Add(u0); b.uv.Add(u1); b.uv.Add(u2); b.uv.Add(u3);
        b.tri.Add(i); b.tri.Add(i + 1); b.tri.Add(i + 2); b.tri.Add(i); b.tri.Add(i + 2); b.tri.Add(i + 3);
    }

    // ================================================================ tree cards, shared with RealWorld.cs
    // the tree material: Foliage.shader on the atlas, with the sun's direction for the back-light (off at night)
    public static Material FoliageMaterial()
    {
        var atlas = EnvTex.Foliage();
        if (WorldBuilder.FoliageShader == null) return WorldBuilder.Mat(Color.white, atlas);
        var m = new Material(WorldBuilder.FoliageShader) { mainTexture = atlas };
        m.SetFloat("_Cutoff", 0.5f);
        var sun = RenderSettings.sun;
        if (sun != null)
        {
            Vector3 to = -sun.transform.forward;
            m.SetVector("_SunDir", new Vector4(to.x, to.y, to.z, 0));
            float k = Mathf.Clamp01((sun.intensity - 0.3f) / 0.8f) * Mathf.Clamp01(to.y * 4f + 0.2f);
            m.SetVector("_Trans", new Vector4(1.2f, 1.4f, 0.5f, 0.9f * k));
        }
        return m;
    }

    // one card: foot-left, foot-right, top-right, top-left (LightBake and RoadShadow read the cards in this order),
    // normals bent out to the card's sides and up (the bake then lights the sun side of the crown and shades the far
    // side and the foot), uv2 = (sway weight, phase) for Foliage.shader
    static void Card4(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<Vector2> uv2, List<Color> col, List<int> tri,
                      Vector3 foot, Vector3 half, float h, Vector4 r, float vFrom, bool flip, Vector3 outward, float sway, float phase, Color footC, Color topC)
    {
        int k = v.Count;
        Vector3 b0 = foot - half, b1 = foot + half;
        v.Add(b0); v.Add(b1); v.Add(b1 + Vector3.up * h); v.Add(b0 + Vector3.up * h);
        float u0 = flip ? r.z : r.x, u1 = flip ? r.x : r.z, v0 = Mathf.Lerp(r.y, r.w, vFrom);
        uv.Add(new Vector2(u0, v0)); uv.Add(new Vector2(u1, v0)); uv.Add(new Vector2(u1, r.w)); uv.Add(new Vector2(u0, r.w));
        var side = half.normalized;
        n.Add((-side * 0.7f + outward * 0.5f + Vector3.up * 0.12f).normalized); n.Add((side * 0.7f + outward * 0.5f + Vector3.up * 0.12f).normalized);
        n.Add((side * 0.6f + outward * 0.35f + Vector3.up * 0.75f).normalized); n.Add((-side * 0.6f + outward * 0.35f + Vector3.up * 0.75f).normalized);
        uv2.Add(new Vector2(0, phase)); uv2.Add(new Vector2(0, phase)); uv2.Add(new Vector2(sway, phase)); uv2.Add(new Vector2(sway, phase));
        col.Add(footC); col.Add(footC); col.Add(topC); col.Add(topC);
        tri.Add(k); tri.Add(k + 2); tri.Add(k + 1); tri.Add(k); tri.Add(k + 3); tri.Add(k + 2);
    }
    // per-tree colour relative to the colour painted in the card: the species' leaf colour, then a shade and one of
    // a few hue families (as in a real park: olive, yellow-green, dark green next to each other)
    static Color LeafTint(Color leafLin, EnvTex.Card card, System.Random rnd, float shade)
    {
        var painted = EnvTex.CardLeaf[(int)card]; if (painted.g <= 0f) painted = new Color(0.1f, 0.2f, 0.1f);
        float sh = (0.86f + (float)rnd.NextDouble() * 0.28f) * shade, h = (float)rnd.NextDouble(), kr = 1, kg = 1, kb = 1;
        if (h < 0.2f) { kr = 1.14f; kg = 0.97f; kb = 0.78f; }               // olive
        else if (h < 0.38f) { kr = 1.2f; kg = 1.1f; kb = 0.72f; }           // yellow-green
        else if (h < 0.55f) { kr = 0.8f; kg = 0.86f; kb = 1.0f; }           // dark blue-green
        return new Color(Mathf.Clamp(leafLin.r / painted.r, 0.5f, 1.9f) * sh * kr, Mathf.Clamp(leafLin.g / painted.g, 0.5f, 1.9f) * sh * kg,
                         Mathf.Clamp(leafLin.b / painted.b, 0.5f, 1.9f) * sh * kb, 1f);
    }
    // One tree: "cards" crossed cards round one foot (2 far away and on Low, 3 near). Broad-leaf trees are a mix of round
    // park trees, spreading oaks and poplars (Cur.poplar); the cards of one tree alternate between two crown cards and
    // are mirrored at random, so the crown's outline changes as the view goes round it. The first card is always the
    // tree's own, unmirrored (RoadShadow reads the tree's kind from it).
    public static void EmitTree(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<Vector2> uv2, List<Color> col, List<int> tri,
                                Vector3 foot, TreeKind kind, float sz, Color leafLin, System.Random rnd, int cards)
    {
        float rot = (float)rnd.NextDouble() * 6.28f, phase = (float)rnd.NextDouble() * 6.28f, wsz = sz > 1.2f ? 1.2f + (sz - 1.2f) * 0.6f : sz;
        float w, h, vFrom = 0f, sway; EnvTex.Card a, b;
        switch (kind)
        {
            case TreeKind.Spruce: w = 7.2f * sz; h = 15f * sz; sway = 0.45f; a = b = EnvTex.Card.Spruce; break;
            case TreeKind.Pine: w = 9.5f * sz; h = 9.6f * sz; sway = 0.6f; a = b = EnvTex.Card.Pine; break;
            case TreeKind.Palm: w = 8.4f * sz; h = 9.4f * sz; sway = 1.2f; a = b = EnvTex.Card.Palm; break;
            case TreeKind.Scrub: w = 3.8f * sz; h = 2.2f * sz; vFrom = 0.26f; sway = 0.25f; foot += Vector3.down * 0.2f; a = b = EnvTex.Card.Oak; break;
            default:
            {
                float r = (float)rnd.NextDouble();
                if (r < Cur.poplar) { w = 4.4f * wsz; h = 12.5f * sz; sway = 0.7f; a = b = EnvTex.Card.Poplar; }
                else if (r < Cur.poplar + 0.35f) { w = 10.6f * wsz; h = 9.2f * sz; sway = 0.7f; a = EnvTex.Card.Oak; b = EnvTex.Card.Broadleaf; }
                else { w = 8.8f * wsz; h = 10f * sz; sway = 0.8f; a = EnvTex.Card.Broadleaf; b = EnvTex.Card.Oak; }
                break;
            }
        }
        Color top = LeafTint(leafLin, a, rnd, 1f), low = top * 0.72f; low.a = 1f; // the foot in the crown's own shade
        for (int c = 0; c < cards; c++)
        {
            float ang = rot + c * Mathf.PI / cards + (c == 0 ? 0f : ((float)rnd.NextDouble() - 0.5f) * 0.35f);
            var card = (c & 1) == 1 ? b : a;
            float ww = w * (c == 0 ? 1f : 0.88f + 0.24f * (float)rnd.NextDouble()) * (card == a ? 1f : 0.95f), hh = h * (c == 0 ? 1f : 0.94f + 0.1f * (float)rnd.NextDouble());
            var half = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * (ww * 0.5f);
            Card4(v, n, uv, uv2, col, tri, foot, half, hh, EnvTex.CardRect(card), vFrom, c > 0 && rnd.NextDouble() < 0.5, Vector3.zero, sway, phase, low, top);
        }
    }
    // One card of a forest edge band (EnvTex.Card.Grove / Grove2): a strip of mixed crowns over a dense understorey,
    // standing a few rows behind the first trees and facing out of the wood, so the woods read as deep, layered forest
    // with a sunlit or shaded edge, instead of a raised green mass. along / outward: unit vectors along the edge and
    // out of the wood.
    public static void EmitBand(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<Vector2> uv2, List<Color> col, List<int> tri,
                                Vector3 foot, Vector3 along, Vector3 outward, float w, float h, Color leafLin, System.Random rnd)
    {
        var card = rnd.NextDouble() < 0.5 ? EnvTex.Card.Grove : EnvTex.Card.Grove2;
        Color top = LeafTint(leafLin, card, rnd, 0.92f), low = top * 0.6f; low.a = 1f;
        Card4(v, n, uv, uv2, col, tri, foot, along * (w * 0.5f), h, EnvTex.CardRect(card), 0f, rnd.NextDouble() < 0.5, outward, 0.22f, (float)rnd.NextDouble() * 6.28f, low, top);
    }

    // ================================================================ trees: one mesh of alpha-tested cards
    // Every tree is two (Low) or three crossed upright cards cut from one atlas (EnvTex.Foliage: spruce, broad-leaf,
    // umbrella pine, palm; scrub is the broad-leaf crown alone, squat). The trunk is painted into the card, so the
    // trunk mesh is gone: thousands of trees are one draw call. The normals are bent outwards from the tree's axis
    // and upwards, so the bake lights the crowns like rounded volumes (sun side bright, the far side and the foot in
    // shade). uv2 = (sway weight, phase) for the breeze in Foliage.shader (Medium/High).
    struct ModelPend { public Vector3 foot; public float edge, sz; public TreeKind kind; public Color leaf; public int cards; }
    public static void Trees(Track t, Transform root, float ext)
    {
        var e = Cur; var bb = t.Bounds();
        if (e.tree == TreeKind.None || RealWorld.HasVegetation) return; // (real woods and trees: RealWorld.cs)
        float dens = t.def != null ? (float)t.def.forest : 0.3f;
        var rnd = new System.Random(7); int placed = 0, tries = 0;
        int want = Mathf.Clamp((int)((t.length * 0.4f * dens + ext / 6) * e.treeMul), 120, 2500);
        bool low = WorldBuilder.Gfx <= 1;
        if (low) want = want * 3 / 5; // Low: fewer trees
        float near = e.treeNear + (t.def != null ? (float)t.def.barrier : 0), far = e.treeFar;
        EnvTex.Foliage();
        var v = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var uv2 = new List<Vector2>();
        var tri = new List<int>(); var col = new List<Color>();
        // real tree models near the track (ModelLib.cs): collected here, nearest first, the rest stay cards
        bool mdl = e.realModels && ModelLib.Begin(WorldBuilder.Gfx);
        var pend = mdl ? new List<ModelPend>() : null; float wall = (float)(t.width / 2 + (t.def != null ? t.def.barrier : 1.4));
        while (placed < want && tries++ < want * 40)
        {
            double x = bb.minX - far * 0.6 + rnd.NextDouble() * (bb.maxX - bb.minX + far * 1.2), y = bb.minY - far * 0.6 + rnd.NextDouble() * (bb.maxY - bb.minY + far * 1.2);
            float fx = (float)x, fz = (float)y;
            if (WorldBuilder.Pad.Contains(new Vector2(fx, fz)) || Waterfront.InWater(fx, fz) || Trackside.Blocked(fx, fz, 5f) || Taken(fx, fz, 4f)) continue;
            if (e.clump > 0 && Fbm(fx / 170f, fz / 170f, 71) < e.clump) continue; // copses with open land between
            if (e.canopy > 0)
            {
                // woods: rows of trees along the edge and a little way in (the band below stands for the rest); thinner in the open
                float cn = Canopy(fx, fz);
                if (cn > 0.75f || (cn < 0.02f && Clear(fx, fz) > e.canopyAt + 18f && rnd.NextDouble() > 0.22)) continue;
            }
            double dist2 = t.Nearest(x, y).dist;
            if (dist2 < near || dist2 > far) continue;
            bool second = e.tree2 != TreeKind.None && rnd.NextDouble() < e.tree2Share;
            var kind = second ? e.tree2 : e.tree;
            float sz = (0.7f + (float)rnd.NextDouble() * 0.8f) * e.treeSize;
            if (kind != TreeKind.Scrub && dist2 < near + 14) sz = Mathf.Min(sz, 1.25f * Mathf.Max(1f, e.treeSize * 0.75f)); // no giant right at the fence
            // (heights relative to the circuit's height field: WorldBuilder.Drape lifts the mesh onto it)
            var foot = new Vector3(fx, Lift(fx, fz) - 0.35f, fz);
            int cards = low || dist2 > 300 ? 2 : 3; // far trees: two cards (LOD by distance)
            if (mdl && kind == TreeKind.Broadleaf && dist2 - wall < ModelLib.Reach)
            {
                pend.Add(new ModelPend { foot = foot, edge = (float)dist2 - wall, kind = kind, sz = sz, leaf = (second ? e.leaf2 : e.leaf).linear, cards = cards });
                placed++; continue;
            }
            EmitTree(v, nrm, uv, uv2, col, tri, foot, kind, sz, (second ? e.leaf2 : e.leaf).linear, rnd, cards);
            placed++;
        }
        if (mdl)
        {
            pend.Sort((ma, mb) => ma.edge.CompareTo(mb.edge));
            foreach (var mp in pend)
            {
                var fin = mp.foot + Vector3.up * WorldBuilder.H(mp.foot.x, mp.foot.z); // (models are made at their final height: Drape leaves them alone)
                if (!ModelLib.TryTree(fin, mp.edge, mp.leaf, rnd)) EmitTree(v, nrm, uv, uv2, col, tri, mp.foot, mp.kind, mp.sz, mp.leaf, rnd, mp.cards);
            }
            ModelLib.PlaceProps(t);
            ModelLib.Flush(root);
        }
        if (e.canopy > 0) WoodBands(t, bb, near, far, v, nrm, uv, uv2, col, tri, rnd);
        if (tri.Count == 0) return;
        // (named "Trees" as before: WorldBuilder.Drape lays them onto the circuit's height field and keeps these normals)
        var fm = FoliageMaterial();
        var treesGo = WorldBuilder.MeshObj("Trees", v, uv, tri, fm, true, uv2); treesGo.transform.SetParent(root);
        var mesh = treesGo.GetComponent<MeshFilter>().sharedMesh; mesh.SetNormals(nrm);
        BakeTint.Add(mesh, col.ToArray());
    }
    // the woods' edge: on an 8 m lattice, the wooded points (Canopy > 0.5) next to open land get a forest-edge band
    // card a few metres in, turned along the edge and facing out of the wood; a second, taller row further in near the
    // track for depth. The cards overlap about twice, so the band reads as one ragged wall of foliage.
    static void WoodBands(Track t, (double minX, double minY, double maxX, double maxY) bb, float near, float far, List<Vector3> v, List<Vector3> nrm, List<Vector2> uv, List<Vector2> uv2, List<Color> col, List<int> tri, System.Random rnd)
    {
        var e = Cur; const float S = 8f; bool low = WorldBuilder.Gfx <= 1;
        float x0 = (float)bb.minX - far, z0 = (float)bb.minY - far;
        int nx = Mathf.CeilToInt(((float)bb.maxX + far - x0) / S) + 1, nz = Mathf.CeilToInt(((float)bb.maxY + far - z0) / S) + 1;
        var inW = new bool[nx * nz];
        for (int j = 0; j < nz; j++) for (int i = 0; i < nx; i++)
        {
            float x = x0 + i * S, z = z0 + j * S;
            if (Clear(x, z) > far + 30f) continue;
            inW[j * nx + i] = Canopy(x, z) > 0.5f;
        }
        int count = 0, cap = low ? 1500 : 3000;
        for (int j = 1; j < nz - 1 && count < cap; j++) for (int i = 1; i < nx - 1 && count < cap; i++)
        {
            int k = j * nx + i; if (!inW[k]) continue;
            int open = (inW[k - 1] ? 0 : 1) + (inW[k + 1] ? 0 : 1) + (inW[k - nx] ? 0 : 1) + (inW[k + nx] ? 0 : 1);
            if (open == 0) continue;
            float x = x0 + i * S + ((float)rnd.NextDouble() - 0.5f) * S * 0.5f, z = z0 + j * S + ((float)rnd.NextDouble() - 0.5f) * S * 0.5f;
            // out of the wood: down the canopy's slope
            float gx = Canopy(x + 4f, z) - Canopy(x - 4f, z), gz = Canopy(x, z + 4f) - Canopy(x, z - 4f);
            var outward = new Vector3(-gx, 0, -gz); if (outward.sqrMagnitude < 1e-6f) outward = new Vector3((inW[k - 1] ? 0 : -1) + (inW[k + 1] ? 0 : 1), 0, (inW[k - nx] ? 0 : -1) + (inW[k + nx] ? 0 : 1));
            if (outward.sqrMagnitude < 1e-6f) continue;
            outward.Normalize(); var along = new Vector3(-outward.z, 0, outward.x);
            double d = t.Nearest(x, z).dist; if (d < near + 6 || d > far * 1.15f) continue;
            if (WorldBuilder.Pad.Contains(new Vector2(x, z)) || Waterfront.InWater(x, z) || Trackside.Blocked(x, z, 6f) || Taken(x, z, 8f)) continue;
            var lc = Color.Lerp(e.leaf, e.leaf2, e.tree2Share * Noise(x / 60f, z / 60f, 95)).linear;
            for (int row = 0; row < (d < 260 && !low ? 2 : 1); row++)
            {
                var p = new Vector3(x, 0, z) - outward * (5f + row * 16f);
                p.y = Lift(p.x, p.z) - 0.5f;
                float h = e.canopy * (0.95f + 0.3f * (float)rnd.NextDouble()) * (row == 0 ? 1f : 1.18f), w = S * 2.3f * (0.9f + 0.25f * (float)rnd.NextDouble());
                EmitBand(v, nrm, uv, uv2, col, tri, p, along, outward, w, h, lc, rnd);
                count++;
            }
        }
    }

    // ground a landmark will stand on (known before the town rows are built, so they leave room)
    public static bool Reserved(float x, float z, float r)
    {
        for (int k = 0; k < marks.Count; k++) { float dx = x - marks[k].c.x, dz = z - marks[k].c.z, rr = marks[k].r + r; if (marks[k].r > 0 && dx * dx + dz * dz < rr * rr) return true; }
        return false;
    }
    // free of landmarks and the ground they claimed (RealWorld's trees keep out)
    public static bool Free(float x, float z, float r) => !Taken(x, z, r) && !Reserved(x, z, r);
    static bool Taken(float x, float z, float r)
    {
        for (int k = 0; k < spots.Count; k++) { var s = spots[k]; float dx = x - s.x, dz = z - s.z, rr = s.r + r; if (dx * dx + dz * dz < rr * rr) return true; }
        return false;
    }

    // Where each landmark stands: found before the terrain is made, so the woods can leave a clearing for it.
    static void PlaceMarks(Track t)
    {
        var e = Cur; var bb = t.Bounds(); var rnd = new System.Random(23);
        if (e.marks == null) return;
        foreach (var m in e.marks)
            {
                // the real buildings stand there already (RealWorld.cs)
                if (RealWorld.HasBuildings && (m.kind == MarkKind.Casino || m.kind == MarkKind.Hotel || m.kind == MarkKind.Blocks)) continue;
                // where: the track point at s, then out to the chosen side
                int i = ((Mathf.RoundToInt(m.s / (float)t.ds) % t.N) + t.N) % t.N; var p = t.pts[i];
                Vector3 T = new Vector3((float)p.tx, 0, (float)p.ty).normalized, Nl = new Vector3((float)p.nx, 0, (float)p.ny).normalized;
                Vector3 c0 = new Vector3((float)p.x, 0, (float)p.y);
                int sg = m.side;
                if (sg == 2 || sg == -2)
                {
                    // inside / outside of the bend: from the mean curvature around the point
                    double k = 0; int win = Mathf.Max(2, Mathf.RoundToInt(40f / (float)t.ds));
                    for (int j = -win; j <= win; j++) k += t.pts[((i + j) % t.N + t.N) % t.N].curv;
                    int inside = k > 0 ? 1 : -1; // left-hand bend: the inside is on the left
                    sg = sg == 2 ? inside : -inside;
                }
                else if (sg == 0)
                {
                    // the side the pit buildings stand on (the inside of the circuit if there are none)
                    float probe = (float)t.width * 0.5f + (float)(t.def != null ? t.def.barrier : 9.3) + 16f;
                    Vector3 a = c0 + Nl * probe, b2 = c0 - Nl * probe;
                    if (Trackside.Blocked(a.x, a.z, 4f)) sg = 1;
                    else if (Trackside.Blocked(b2.x, b2.z, 4f)) sg = -1;
                    else sg = Vector3.Dot(Nl, new Vector3((float)(bb.minX + bb.maxX) * 0.5f, 0, (float)(bb.minY + bb.maxY) * 0.5f) - c0) > 0 ? 1 : -1;
                }
                Vector3 D = Nl * (sg == 9 || sg == 8 ? 1 : sg), c = c0 + D * m.off;
                if (sg == 8)
                {
                    D = Dir(m.s); T = new Vector3(D.z, 0, -D.x);
                    float half = (float)System.Math.Max(bb.maxX - bb.minX, bb.maxY - bb.minY) * 0.5f;
                    c = new Vector3((float)(bb.minX + bb.maxX) * 0.5f, 0, (float)(bb.minY + bb.maxY) * 0.5f) + D * (half * 0.75f + m.off);
                }
                if (sg == 9)
                {
                    // middle of the infield: the point furthest from the road near the circuit's centre of mass
                    double sx = 0, sy = 0; for (int j = 0; j < t.N; j++) { sx += t.pts[j].x; sy += t.pts[j].y; }
                    c = new Vector3((float)(sx / t.N), 0, (float)(sy / t.N)); float best = -1; var bc = c;
                    for (int q = 0; q < 200; q++)
                    {
                        var cand = c + new Vector3((float)(rnd.NextDouble() - 0.5) * 360f, 0, (float)(rnd.NextDouble() - 0.5) * 360f);
                        float cl = Mathf.Min(Clear(cand.x, cand.z), 90f) - (cand - c).magnitude * 0.08f;
                        if (cl > best) { best = cl; bc = cand; }
                    }
                    c = bc; var np = t.pts[t.Nearest(c.x, c.z).idx]; T = new Vector3((float)np.tx, 0, (float)np.ty).normalized; D = new Vector3(-T.z, 0, T.x);
                }
                else if (sg != 8 && m.kind != MarkKind.Bridge && m.kind != MarkKind.Blocks)
                {
                    // never on another leg of the track or on a stand: step further out until the ground is free
                    float need = m.kind == MarkKind.Wing ? 16f : m.kind == MarkKind.Casino || m.kind == MarkKind.Hotel ? 20f : 26f;
                    for (int q = 0; q < 8 && (Clear(c.x, c.z) < need || Waterfront.InWater(c.x, c.z)); q++) c += D * 8f;
                }
                float rad = m.kind == MarkKind.Wheel ? 30 : m.kind == MarkKind.Casino ? 34 : m.kind == MarkKind.Hotel ? 26 : m.kind == MarkKind.Blocks ? 150 : m.kind == MarkKind.Wing ? 0 : 16;
                marks.Add(new Placed { m = m, c = c, T = T, D = D, i = i, sg = sg, r = rad });
            }
    }

    // ================================================================ landmarks, apartment blocks, yachts
    // "EnvProps": flat-coloured parts (one white material, colour in the vertex tint).
    // "EnvBlocks": walls with the town's window texture (the town rows' own material, so they batch together).
    public static void Props(Track t, Transform root, Transform late = null)
    {
        var e = Cur; var props = new Buf(); var blocks = new Buf();
        var bb = t.Bounds(); var rnd = new System.Random(19);
        bool low = WorldBuilder.Gfx <= 1;
        Color[] wallCols = e.walls ?? new[] { C(0.93f, 0.86f, 0.72f), C(0.90f, 0.74f, 0.62f), C(0.96f, 0.93f, 0.88f), C(0.86f, 0.80f, 0.66f) };

        foreach (var pm in marks)
            {
                var m = pm.m; Vector3 c = pm.c, T = pm.T, D = pm.D; int i = pm.i, sg = pm.sg;
                float gy = GroundY(c.x, c.z); c.y = gy;
                float tall = 20;
                switch (m.kind)
                {
                    case MarkKind.Tower: tall = Tower(props, c, T, D, m.size); Claim(c, 16); break;
                    case MarkKind.Wheel: tall = Wheel(props, c, T, D, m.size); Claim(c, 30); break;
                    case MarkKind.Wing: tall = Wing(props, t, i, sg, m.off, m.size); break;
                    case MarkKind.Sculpture: tall = Sculpture(props, c, T, D, m.size); Claim(c, 14); break;
                    case MarkKind.Casino: tall = Casino(props, blocks, c, T, D, m.size); Claim(c, 34); break;
                    case MarkKind.Hotel: tall = Hotel(props, blocks, c, T, D, m.size); Claim(c, 26); break;
                    case MarkKind.Bridge: tall = Bridge(props, t, i); break;
                    case MarkKind.Blocks:
                        {
                            // a small town: a dozen slabs and towers
                            int n = low ? 7 : 12; tall = 0;
                            for (int q = 0; q < n; q++)
                            {
                                var bc = c + T * ((q % 4 - 1.5f) * 62f + (float)rnd.NextDouble() * 20f) + D * ((q / 4) * 70f + (float)rnd.NextDouble() * 24f);
                                if (Waterfront.InWater(bc.x, bc.z) || Clear(bc.x, bc.z) < 60f && dist != null && bc.x > fx0 && bc.x < fx1 && bc.z > fz0 && bc.z < fz1) continue;
                                float h = (5 + rnd.Next(9)) * 3.2f; tall = Mathf.Max(tall, h);
                                Block(props, blocks, bc, T, D, 14 + (float)rnd.NextDouble() * 16f, 8 + (float)rnd.NextDouble() * 4f, h,
                                      wallCols[2] * (0.9f + 0.1f * (float)rnd.NextDouble()), C(0.55f, 0.55f, 0.56f));
                                Claim(bc, 22);
                            }
                            break;
                        }
                }
                Landmarks.Add(new Vector4(c.x, gy, c.z, tall));
            }

        // apartment blocks climbing the slope behind the town rows (Monaco)
        if (e.blocks > 0 && dist != null && !RealWorld.HasBuildings)
        {
            int want = low ? e.blocks * 3 / 5 : e.blocks, placed = 0, tries = 0;
            while (placed < want && tries++ < want * 60)
            {
                float x = fx0 + 60 + (float)rnd.NextDouble() * (fx1 - fx0 - 120), z = fz0 + 60 + (float)rnd.NextDouble() * (fz1 - fz0 - 120);
                float d = Clear(x, z);
                if (d < e.flatTo + 16 || d > 370 || Waterfront.InWater(x, z) || Taken(x, z, 21f) || Trackside.Blocked(x, z, 16f)) continue;
                float w = 9 + (float)rnd.NextDouble() * 6f, dp = 7 + (float)rnd.NextDouble() * 3f; // half sizes
                // face the slope: the long side lies along the contour
                float gx = Lift(x + 6, z) - Lift(x - 6, z), gz = Lift(x, z + 6) - Lift(x, z - 6);
                Vector3 up = new Vector3(gx, 0, gz); if (up.sqrMagnitude < 1e-4f) up = new Vector3(1, 0, 0.3f); up = up.normalized;
                Vector3 along = new Vector3(-up.z, 0, up.x);
                if (Waterfront.InWater(x + along.x * w, z + along.z * w) || Waterfront.InWater(x - along.x * w, z - along.z * w)) continue;
                // the towers get taller up the hill, as in the real town
                int floors = e.floorsMin + 2 + rnd.Next(Mathf.Max(1, e.floorsMax - e.floorsMin)) + (rnd.NextDouble() < 0.14 ? 8 + rnd.Next(8) : 0);
                var col = wallCols[rnd.Next(wallCols.Length)] * (0.92f + 0.1f * (float)rnd.NextDouble()); col.a = 1;
                bool tile = rnd.NextDouble() < 0.3 && floors < 12;
                var c = new Vector3(x, GroundY(x, z), z);
                Block(props, blocks, c, along, up, w, dp, floors * 3.2f, col, tile ? C(0.62f, 0.34f, 0.25f) : C(0.60f, 0.59f, 0.57f));
                Claim(c, Mathf.Max(w, dp)); placed++;
            }
        }

        // yachts moored stern-to along the quay
        if (e.yachts && Waterfront.HasWater && t.def != null)
        {
            float step = low ? 34f : 20f, last = -1e9f; int rows = low ? 1 : 2;
            for (int i = 0; i < t.N; i++)
            {
                var p = t.pts[i];
                if (p.s < t.def.waterFrom || p.s > t.def.waterTo || p.s - last < step) continue;
                Vector3 c0 = new Vector3((float)p.x, 0, (float)p.y), Nl = new Vector3((float)p.nx, 0, (float)p.ny).normalized, T = new Vector3((float)p.tx, 0, (float)p.ty).normalized;
                Vector3 D = Waterfront.InWater(c0.x + Nl.x * 30, c0.z + Nl.z * 30) ? Nl : -Nl;
                bool any = false;
                for (int r = 0; r < rows; r++)
                {
                    float len = 9 + Hash(i, r, 3) * 13f, beam = len * 0.24f, at = 24 + r * 44 + len;
                    var c = c0 + D * at + T * ((Hash(i, r, 4) - 0.5f) * 6f);
                    bool wet = true;
                    for (int q = 0; q < 4 && wet; q++) { var k = c + D * ((q < 2 ? 1 : -1) * (len + 6)) + T * ((q % 2 == 0 ? 1 : -1) * (beam + 5)); wet = Waterfront.InWater(k.x, k.z) && !Waterfront.InTunnel(k.x, k.z); }
                    if (!wet || Hash(i, r, 5) < 0.15f) continue;
                    Yacht(props, c + Vector3.up * 1.0f, D, T, len, beam, Hash(i, r, 6));
                    any = true;
                }
                if (any) last = (float)p.s;
            }
        }

        Emit("EnvProps", props, WorldBuilder.Mat(WorldBuilder.BakeReady ? Color.white : C(0.85f, 0.84f, 0.80f), null, 0.1f), root);
        if (blocks.tri.Count > 0)
        {
            var mat = Scenery.TownMat != null && WorldBuilder.BakeReady ? Scenery.TownMat
                    : WorldBuilder.Mat(WorldBuilder.BakeReady ? Color.white : wallCols[0], Scenery.WindowTex(), 0.25f);
            // kept out of the light bake's shadow map (tall towers on the slope would make every shadow ray run its
            // full length and slow the track load), but lit by the bake like everything else
            Emit("EnvBlocks", blocks, mat, late != null ? late : root);
        }
    }
    static void Claim(Vector3 c, float r) { spots.Add(new Spot { x = c.x, z = c.z, r = r }); }

    // a block of flats: windowed walls (reaching down into the slope) and a flat or tiled roof
    static void Block(Buf props, Buf walls, Vector3 c, Vector3 ax, Vector3 az, float ex, float ez, float h, Color col, Color roof)
    {
        Vector3 x = ax * ex, z = az * ez, up = Vector3.up * h, dn = Vector3.down * 14f;
        Vector3[] k = { c - x - z, c + x - z, c + x + z, c - x + z };
        // the ground under the lowest corner, so no wall floats above the slope
        Color foot = col * 0.72f; foot.a = 1;
        for (int q = 0; q < 4; q++)
        {
            Vector3 a0 = k[q], a1 = k[(q + 1) % 4];
            float uu = Mathf.Max(1, Mathf.Round((a1 - a0).magnitude / 3.1f)), vv = Mathf.Round(h / 3.2f);
            Quad(walls, a0 + dn, a1 + dn, a1 + up, a0 + up, (a0 + a1) * 0.5f - c, foot, col, uu, vv + 4);
            // roof edge: a pale cornice 0.3 m proud with a shaded underside (uv 0 of the facade is plain plaster)
            Vector3 o = ((a0 + a1) * 0.5f - c); o.y = 0; o = o.normalized * 0.3f; var ed = (a1 - a0).normalized * 0.3f;
            var cc = Color.Lerp(col, Color.white, 0.45f); cc.a = 1;
            Vector3 c0 = a0 - ed + up + Vector3.down * 0.45f, c1 = a1 + ed + up + Vector3.down * 0.45f, k0 = a0 - ed + up + Vector3.up * 0.2f, k1 = a1 + ed + up + Vector3.up * 0.2f;
            Quad(walls, c0 + o, c1 + o, k1 + o, k0 + o, o, cc);
            Quad(walls, c0, c1, c1 + o, c0 + o, Vector3.down, cc);
            Quad(walls, k0, k1, k1 + o, k0 + o, Vector3.up, cc);
        }
        Quad(props, k[0] + up, k[1] + up, k[2] + up, k[3] + up, Vector3.up, roof);
    }

    // observation tower: twin lift shafts, a deck near the top, and a sweeping ribbon canopy down one side
    static float Tower(Buf b, Vector3 c, Vector3 T, Vector3 D, float size)
    {
        float h = 76f * size; Vector3 up = Vector3.up;
        Color white = C(0.90f, 0.90f, 0.88f), steel = C(0.62f, 0.64f, 0.67f), red = C(0.78f, 0.13f, 0.11f), glass = C(0.25f, 0.33f, 0.40f);
        Box(b, c + T * 2.4f + up * (h * 0.5f), T, up, D, 1.3f, h * 0.5f, 1.3f, white);
        Box(b, c - T * 2.4f + up * (h * 0.5f), T, up, D, 1.3f, h * 0.5f, 1.3f, white);
        // stair landings between the shafts
        for (int k = 1; k < 12; k++) Box(b, c + up * (h * k / 12f), T, up, D, 1.4f, 0.12f, 1.2f, steel, true);
        // the deck: a glazed box with a wide floor and roof
        Box(b, c + up * (h - 7.2f), T, up, D, 7.5f, 0.3f, 6f, white, true);
        Box(b, c + up * (h - 5.3f), T, up, D, 6.2f, 1.6f, 4.8f, glass);
        Box(b, c + up * (h - 3.4f), T, up, D, 7.8f, 0.3f, 6.3f, white, true);
        // the ribbon: a fan of red strips from the roof, arching out and down behind the tower
        const int N = 9;
        for (int s = -2; s <= 2; s++)
        {
            Vector3 prevA = Vector3.zero, prevB = Vector3.zero;
            for (int k = 0; k <= N; k++)
            {
                float u = (float)k / N, out2 = 5f + 26f * u * u, y = h - 2.6f - (h - 6f) * u * u * (0.55f + 0.45f * u), wdt = 0.35f + 0.5f * u;
                Vector3 mid2 = c - D * out2 + T * (s * (1.3f + 2.6f * u)) + up * y;
                Vector3 a = mid2 + T * wdt, a2 = mid2 - T * wdt;
                if (k > 0) Quad2(b, prevA, prevB, a2, a, red);
                prevA = a; prevB = a2;
            }
        }
        return h;
    }

    // big wheel: two rims on spokes, gondolas, A-frame legs
    static float Wheel(Buf b, Vector3 c, Vector3 T, Vector3 D, float size)
    {
        float r = 25f * size, hub = r + 4.5f; Vector3 up = Vector3.up, o = c + up * hub;
        Color white = C(0.90f, 0.90f, 0.90f), steel = C(0.70f, 0.72f, 0.75f);
        Color[] cab = { C(0.85f, 0.20f, 0.18f), C(0.95f, 0.78f, 0.16f), C(0.18f, 0.45f, 0.80f), C(0.20f, 0.62f, 0.35f), C(0.92f, 0.50f, 0.14f), C(0.93f, 0.93f, 0.90f) };
        const int N = 24;
        for (int s = -1; s <= 1; s += 2)
        {
            Vector3 off = D * (s * 1.5f);
            for (int k = 0; k < N; k++)
            {
                float a0 = k * Mathf.PI * 2 / N, a1 = (k + 1) * Mathf.PI * 2 / N;
                Vector3 d0 = T * Mathf.Cos(a0) + up * Mathf.Sin(a0), d1 = T * Mathf.Cos(a1) + up * Mathf.Sin(a1);
                Quad2(b, o + off + d0 * (r - 0.9f), o + off + d1 * (r - 0.9f), o + off + d1 * r, o + off + d0 * r, white);
                if (k % 2 == 0)
                {
                    // spoke
                    Vector3 w = T * -Mathf.Sin(a0) + up * Mathf.Cos(a0);
                    Quad2(b, o + off - w * 0.25f, o + off + w * 0.25f, o + off + d0 * (r - 0.9f) + w * 0.25f, o + off + d0 * (r - 0.9f) - w * 0.25f, steel);
                }
            }
        }
        for (int k = 0; k < N; k += 2)
        {
            float a0 = (k + 0.5f) * Mathf.PI * 2 / N;
            Box(b, o + (T * Mathf.Cos(a0) + up * Mathf.Sin(a0)) * r - up * 1.6f, T, up, D, 1.3f, 1.2f, 1.3f, cab[(k / 2) % cab.Length], true);
        }
        Box(b, o, T, up, D, 1.4f, 1.4f, 2.6f, steel, true);
        // legs: two leaning posts each side
        for (int s = -1; s <= 1; s += 2)
            for (int q = -1; q <= 1; q += 2)
            {
                Vector3 foot = c + D * (s * 3.4f) + T * (q * hub * 0.42f), head = o + D * (s * 2.3f), ax = (head - foot).normalized;
                Vector3 sd = Vector3.Cross(ax, D).normalized;
                Box(b, (foot + head) * 0.5f - up * 0.5f, sd, ax, D, 0.55f, (head - foot).magnitude * 0.5f + 0.6f, 0.55f, white);
            }
        // a low station building under it
        Box(b, c + up * 2f, T, up, D, 9f, 2f, 5f, C(0.86f, 0.84f, 0.78f));
        return hub + r;
    }

    // long pit and paddock building with a folded, pointed roof (follows the pit straight)
    static float Wing(Buf b, Track t, int i0, int sg, float off, float size)
    {
        float L = 300f * size, depth = 24f, wallH = 13f; int n = Mathf.Max(6, Mathf.RoundToInt(L / 20f));
        Color white = C(0.90f, 0.91f, 0.92f), glass = C(0.20f, 0.28f, 0.36f), alu = C(0.74f, 0.77f, 0.80f), dark = C(0.30f, 0.32f, 0.35f);
        float tall = 0; Vector3 pf = Vector3.zero, pb = Vector3.zero, pr = Vector3.zero; float prevH = 0;
        Vector3 up = Vector3.up, first = Vector3.zero, D0 = Vector3.zero;
        for (int k = 0; k <= n; k++)
        {
            float s = -L * 0.5f + L * k / n; int i = ((i0 + Mathf.RoundToInt(s / (float)t.ds)) % t.N + t.N) % t.N; var p = t.pts[i];
            Vector3 c = new Vector3((float)p.x, 0, (float)p.y), D = new Vector3((float)p.nx, 0, (float)p.ny).normalized * sg;
            Vector3 f = c + D * (off - depth * 0.5f), bk = c + D * (off + depth * 0.5f);
            float gy = WorldBuilder.H(f.x, f.z); f.y = gy; bk.y = gy;
            // the roof's ridge rises and falls in folds, and sweeps up to a point at the far end
            float fold = (k % 2 == 0 ? 6.5f : 2.5f) + 9f * Mathf.Pow((float)k / n, 3f), rh = wallH + fold;
            Vector3 ridge = c + D * (off - depth * 0.1f) + up * (gy + rh);
            if (k == 0) { first = f; D0 = D; }
            if (k > 0)
            {
                // front: glass band between white bands; back wall; folded roof with a deep front overhang
                Quad(b, pf, f, f + up * 3.5f, pf + up * 3.5f, -D, white);
                Quad(b, pf + up * 3.5f, f + up * 3.5f, f + up * 10.5f, pf + up * 10.5f, -D, glass);
                Quad(b, pf + up * 10.5f, f + up * 10.5f, f + up * wallH, pf + up * wallH, -D, white);
                Quad(b, pb, bk, bk + up * wallH, pb + up * wallH, D, white);
                Vector3 e0 = pf - D * 5f + up * (wallH + 0.8f), e1 = f - D * 5f + up * (wallH + 0.8f);
                Quad(b, e0, e1, ridge, pr, up - D, alu);
                Quad(b, pb + up * wallH, bk + up * wallH, ridge, pr, up + D, alu);
                Quad(b, e0, e1, f + up * wallH, pf + up * wallH, -up, dark); // underside of the overhang
            }
            tall = Mathf.Max(tall, rh); pf = f; pb = bk; pr = ridge; prevH = rh;
            if (k % 3 == 0) Claim((f + bk) * 0.5f, 24);
        }
        // end walls
        Quad(b, first, first + D0 * depth, first + D0 * depth + up * wallH, first + up * wallH, -(pf - first), white);
        Quad(b, pf, pb, pb + up * wallH, pf + up * wallH, pf - first, white);
        Tri(b, pf + up * wallH, pb + up * wallH, pr, pf - first, glass);
        return tall;
    }

    // big abstract steel sculpture: an arch with a faceted form leaping off its crown
    static float Sculpture(Buf b, Vector3 c, Vector3 T, Vector3 D, float size)
    {
        float r = 9f * size; Vector3 up = Vector3.up;
        Color rust = C(0.46f, 0.27f, 0.17f), steel = C(0.55f, 0.56f, 0.58f);
        const int N = 10;
        for (int k = 0; k < N; k++)
        {
            float a0 = Mathf.PI * k / N, a1 = Mathf.PI * (k + 1) / N, am = (a0 + a1) * 0.5f;
            Vector3 d = T * Mathf.Cos(am) + up * Mathf.Sin(am), w = T * -Mathf.Sin(am) + up * Mathf.Cos(am);
            Box(b, c + d * r, d, w, D, 0.7f, r * Mathf.PI / N * 0.52f, 1.1f, rust, true);
        }
        // the form on top: two long faceted wedges, like something in mid-leap
        Vector3 o = c + up * (r + 0.6f);
        Vector3 nose = o + T * (r * 1.25f) + up * (r * 0.55f), tail = o - T * (r * 1.0f) + up * (r * 0.25f), back = o + up * (r * 0.75f), belly = o - up * 0.2f;
        foreach (int s in new[] { -1, 1 })
        {
            Vector3 sd = D * (s * 1.5f);
            Tri(b, nose, back, belly + sd, sd + up, steel); Tri(b, back, tail, belly + sd, sd + up, steel);
            Tri(b, nose, belly, belly + sd, sd - up, steel); Tri(b, belly, tail, belly + sd, sd - up, steel);
        }
        Tri(b, nose + up * 0.2f, nose - T * 2.5f + D * 1.2f + up * (r * 0.4f), nose - T * 2.2f + D * 0.3f, D, rust);
        Tri(b, nose + up * 0.2f, nose - T * 2.5f - D * 1.2f + up * (r * 0.4f), nose - T * 2.2f - D * 0.3f, -D, rust);
        Box(b, c + up * 0.4f, T, up, D, r + 3f, 0.4f, 4f, C(0.62f, 0.62f, 0.60f));
        return r * 1.9f;
    }

    // Belle Epoque casino: a cream palace, two corner towers and a central dome with green copper roofs
    static float Casino(Buf props, Buf walls, Vector3 c, Vector3 T, Vector3 D, float size)
    {
        Color cream = C(0.95f, 0.90f, 0.78f), copper = C(0.36f, 0.56f, 0.50f), slate = C(0.42f, 0.44f, 0.47f), stone = C(0.84f, 0.80f, 0.70f);
        float ex = 30f * size, ez = 15f * size, h = 16f; Vector3 up = Vector3.up;
        Block(props, walls, c, T, D, ex, ez, h, cream, slate);
        // central pavilion with a dome, towards the square (the track side)
        Vector3 f = c - D * (ez * 0.55f);
        Block(props, walls, f, T, D, 9f, 9f, h + 5f, cream, copper);
        Pyramid(props, f + up * (h + 5f), T, D, 9f, 9f, 8f, copper);
        Box(props, f + up * (h + 14f), T, up, D, 0.9f, 1.6f, 0.9f, stone);
        for (int s = -1; s <= 1; s += 2)
        {
            Vector3 tw = c + T * (s * (ex - 5f)) - D * (ez - 4f);
            Block(props, walls, tw, T, D, 4.5f, 4.5f, h + 9f, cream, copper);
            Pyramid(props, tw + up * (h + 9f), T, D, 4.5f, 4.5f, 7f, copper);
        }
        // entrance canopy and steps
        Box(props, c - D * (ez + 8.5f) + up * 4.6f, T, up, D, 8f, 0.25f, 3.2f, C(0.90f, 0.90f, 0.86f), true);
        Box(props, c - D * (ez + 8.5f) + up * 0.3f, T, up, D, 11f, 0.3f, 3.6f, stone);
        return h + 16f;
    }

    // grand hotel: a tall cream block with a grey mansard roof and corner turret
    static float Hotel(Buf props, Buf walls, Vector3 c, Vector3 T, Vector3 D, float size)
    {
        Color cream = C(0.96f, 0.93f, 0.85f), slate = C(0.40f, 0.43f, 0.48f);
        float ex = 24f * size, ez = 11f * size, h = 25.6f; Vector3 up = Vector3.up;
        Block(props, walls, c, T, D, ex, ez, h, cream, slate);
        // mansard: a truncated roof
        Vector3 x = T * ex, z = D * ez, x2 = T * (ex - 3f), z2 = D * (ez - 3f), a = c + up * h, t2 = c + up * (h + 4.5f);
        Quad(props, a - x - z, a + x - z, t2 + x2 - z2, t2 - x2 - z2, -D + up, slate); Quad(props, a + x + z, a - x + z, t2 - x2 + z2, t2 + x2 + z2, D + up, slate);
        Quad(props, a + x - z, a + x + z, t2 + x2 + z2, t2 + x2 - z2, T + up, slate); Quad(props, a - x + z, a - x - z, t2 - x2 - z2, t2 - x2 + z2, -T + up, slate);
        Quad(props, t2 - x2 - z2, t2 + x2 - z2, t2 + x2 + z2, t2 - x2 + z2, up, slate);
        Vector3 tw = c + T * (ex - 3f) - D * (ez - 3f);
        Block(props, walls, tw, T, D, 3.6f, 3.6f, h + 5f, cream, slate);
        Pyramid(props, tw + up * (h + 5f), T, D, 3.6f, 3.6f, 6f, slate);
        return h + 11f;
    }

    // the old banked oval crossing over the track: a tilted concrete deck on two piers, with earth ramps either side
    static float Bridge(Buf b, Track t, int i)
    {
        var p = t.pts[i]; Vector3 up = Vector3.up;
        Vector3 c = new Vector3((float)p.x, WorldBuilder.H(p.x, p.y), (float)p.y), T = new Vector3((float)p.tx, 0, (float)p.ty).normalized, D = new Vector3((float)p.nx, 0, (float)p.ny).normalized;
        float half = (float)t.width * 0.5f + (float)(t.def != null ? t.def.barrier : 9.3) + 3.5f, wide = 7f, clear = 6.2f;
        Color conc = C(0.62f, 0.61f, 0.58f), dark = C(0.44f, 0.44f, 0.43f), moss = C(0.40f, 0.45f, 0.33f);
        // deck: tilted across its width like the banking it carries
        Vector3 tilt = (T + up * 0.35f).normalized, nrm = Vector3.Cross(D, tilt).normalized; if (nrm.y < 0) nrm = -nrm;
        Vector3 dc = c + up * (clear + 2.2f);
        Box(b, dc, D, nrm, tilt, half + 3f, 0.6f, wide, conc, true);
        Box(b, dc + nrm * 1.1f + tilt * wide, D, nrm, tilt, half + 3f, 0.55f, 0.2f, dark); // crash rail along the top edge
        for (int s = -1; s <= 1; s += 2)
        {
            Vector3 pc = c + D * (s * (half + 1.2f));
            Box(b, pc + up * (clear * 0.5f + 0.6f), D, up, T, 1.2f, clear * 0.5f + 1.2f, wide * 0.8f, dark);
            // the banking carries on beyond the track as a grassed-over concrete ramp
            Vector3 a0 = pc + D * (s * 1.2f), a1 = a0 + D * (s * 46f);
            Vector3 lo0 = a0 - T * wide + up * (clear - 0.2f), hi0 = a0 + T * wide + up * (clear + 4.6f), lo1 = a1 - T * wide + up * 0.3f, hi1 = a1 + T * wide + up * 2.6f;
            Quad(b, lo0, hi0, hi1, lo1, up, conc, moss);
            Quad(b, a0 - T * wide, a1 - T * wide, lo1, lo0, -T, dark); Quad(b, a0 + T * wide, a1 + T * wide, hi1, hi0, T, dark);
            Claim((a0 + a1) * 0.5f, 26);
        }
        return clear + 6f;
    }

    // motor yacht: hull, two decks, dark glass band
    static void Yacht(Buf b, Vector3 c, Vector3 F, Vector3 S, float len, float beam, float seed)
    {
        Vector3 up = Vector3.up;
        Color hull = seed > 0.8f ? C(0.16f, 0.20f, 0.30f) : C(0.94f, 0.94f, 0.93f), white = C(0.95f, 0.95f, 0.94f), glass = C(0.16f, 0.20f, 0.25f);
        float hh = 0.9f + len * 0.05f;
        // hull with a pointed bow (towards open water)
        Vector3 s0 = c - F * len, bow = c + F * len, m = c + F * (len * 0.45f);
        Vector3 x = S * beam, top = up * (hh * 2);
        Quad(b, s0 - x, m - x, m - x + top, s0 - x + top, -S, hull); Quad(b, s0 + x, m + x, m + x + top, s0 + x + top, S, hull);
        Quad(b, s0 - x, s0 + x, s0 + x + top, s0 - x + top, -F, hull);
        Quad(b, m - x, bow, bow + top * 1.15f, m - x + top, -S + F, hull); Quad(b, m + x, bow, bow + top * 1.15f, m + x + top, S + F, hull);
        Quad(b, s0 - x + top, s0 + x + top, m + x + top, m - x + top, up, C(0.80f, 0.74f, 0.62f)); // deck
        Tri(b, m - x + top, m + x + top, bow + top * 1.15f, up, white);
        Box(b, c - F * (len * 0.1f) + up * (hh * 2 + 0.8f), F, up, S, len * 0.45f, 0.8f, beam * 0.78f, glass);
        Box(b, c - F * (len * 0.1f) + up * (hh * 2 + 1.75f), F, up, S, len * 0.5f, 0.15f, beam * 0.85f, white, true);
        if (len > 13) Box(b, c - F * (len * 0.15f) + up * (hh * 2 + 2.7f), F, up, S, len * 0.25f, 0.8f, beam * 0.6f, white);
    }
}
