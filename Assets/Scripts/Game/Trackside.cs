// Trackside.cs — procedural Grand Prix dressing, built from the circuit data like the rest of the world:
//   * barriers at the physics wall line (Traffic.WallOffset): street = concrete walls with a painted top strip,
//     parkland = double Armco on posts; segments are long on straights and short in bends, dropped where the offset
//     curve would fold back or come close to another leg
//   * debris fence (street, on top of the walls) and catch fence (parkland, behind tyre walls, stands and the pit wall):
//     one alpha-tested chain-link mesh, made after the light bake's shadow map so it casts no solid baked shadow
//   * tyre walls with a striped conveyor-belt front at corner run-offs
//   * pit complex on the start straight: pit wall, pit lane, garages with an upper floor, canopy and pit-wall stands
//   * start gantry with two rows of five lamps
//   * grandstands on the main straight and at the big braking corners: stepped rows, crowd (coloured-noise texture,
//     per-section vertex-colour variety), end walls, back wall and cantilever roofs with advertising fascias
//   * marshal posts, advertising boards (invented names only), a paddock of motorhomes (High)
//   * vertex-colour ambient occlusion at the base of everything, dirt/scuffs and tyre marks at likely impact points
// Everything opaque shares ONE atlas material (TracksideArt): the colour lives in the texture, the light and the
// AO/dirt tint in the vertex colours (BakeTint). The rest is crowd, fence and ground decals: four materials in all.
// Meshes are cut into ~250 m chunks along the lap so the camera only draws the nearby ones.
// Graphics levels (WorldBuilder.Gfx): Low = no fences or decals, single Armco beam, fewer stands, posts and boards.
// Physics (x, y) maps to Unity (x, height, y); left normal (nx, ny) -> Unity (nx, 0, ny).
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public static class Trackside
{
    // ---------------------------------------------------------------- mesh accumulator
    class MB
    {
        public readonly List<Vector3> v = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<Color> col = new List<Color>();
        public readonly List<int> tri = new List<int>();
    }
    class Layer
    {
        public readonly Dictionary<int, MB> parts = new Dictionary<int, MB>();
        public MB Get(int chunk)
        {
            MB m;
            if (!parts.TryGetValue(chunk, out m)) { m = new MB(); parts[chunk] = m; }
            return m;
        }
    }

    // build state (load time only)
    static Layer atlasL, crowdL, fenceL, grimeL;
    static MB A, C, F, G;          // the current chunk of each layer
    static int chunkPts = 125;
    static Color tint = Color.white;
    static bool aoOn = true;
    const float AoH = 1.4f, AoMin = 0.58f;

    static void SetChunk(int i)
    {
        int c = i / chunkPts;
        A = atlasL.Get(c); C = crowdL.Get(c); F = fenceL.Get(c); G = grimeL.Get(c);
    }

    static Color VTint(float y, Vector3 n)
    {
        float ao = 1f;
        if (aoOn)
        {
            float ny = n.normalized.y;
            if (ny > -0.7f && ny < 0.7f) { float k = Mathf.Clamp01(y / AoH); ao = Mathf.Lerp(AoMin, 1f, k * (2f - k)); }
            else if (ny <= -0.7f) ao = 0.8f; // undersides (roofs, canopies)
        }
        return new Color(tint.r * ao, tint.g * ao, tint.b * ao, 1f);
    }

    // quad a-b-c-d in perimeter order (a = bottom-left as seen from the front); the face is wound to point along n.
    // r = UV rect (u0, v0, u1, v1): a gets (u0, v0), b (u1, v0), c (u1, v1), d (u0, v1)
    static void Quad(MB m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Vector4 r)
    {
        int k = m.v.Count;
        m.v.Add(a); m.v.Add(b); m.v.Add(c); m.v.Add(d);
        m.uv.Add(new Vector2(r.x, r.y)); m.uv.Add(new Vector2(r.z, r.y)); m.uv.Add(new Vector2(r.z, r.w)); m.uv.Add(new Vector2(r.x, r.w));
        m.col.Add(VTint(a.y, n)); m.col.Add(VTint(b.y, n)); m.col.Add(VTint(c.y, n)); m.col.Add(VTint(d.y, n));
        // Unity: RecalculateNormals gives Cross(b - a, c - a) for triangle (a, b, c)
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) >= 0)
        { m.tri.Add(k); m.tri.Add(k + 1); m.tri.Add(k + 2); m.tri.Add(k); m.tri.Add(k + 2); m.tri.Add(k + 3); }
        else
        { m.tri.Add(k); m.tri.Add(k + 2); m.tri.Add(k + 1); m.tri.Add(k); m.tri.Add(k + 3); m.tri.Add(k + 2); }
    }

    // a vertical panel facing n (towards the viewer), centred on c at ground, from height y0 to y1, unmirrored texture
    static void Panel(MB m, Vector3 c, Vector3 n, float halfW, float y0, float y1, Vector4 r)
    {
        Vector3 right = Vector3.Cross(n, Vector3.up).normalized * halfW, up = Vector3.up;
        Quad(m, c - right + up * y0, c + right + up * y0, c + right + up * y1, c - right + up * y1, n, r);
    }

    // a vertical face from p0 to p1 (seen from n), y0..y1; tall faces get a row of vertices at the AO height
    static void Wall(MB m, Vector3 p0, Vector3 p1, float y0, float y1, Vector3 n, Vector4 r)
    {
        Vector3 up = Vector3.up;
        if (aoOn && y0 < AoH * 0.5f && y1 > AoH * 2f)
        {
            float f = (AoH - y0) / (y1 - y0);
            Quad(m, p0 + up * y0, p1 + up * y0, p1 + up * AoH, p0 + up * AoH, n, TracksideArt.Sub(r, 0, 1, 0, f));
            Quad(m, p0 + up * AoH, p1 + up * AoH, p1 + up * y1, p0 + up * y1, n, TracksideArt.Sub(r, 0, 1, f, 1));
        }
        else Quad(m, p0 + up * y0, p1 + up * y0, p1 + up * y1, p0 + up * y1, n, r);
    }

    // oriented box: centre c, unit axes ax, ay (up), az, half extents; bottom face optional; one swatch
    static void Box(MB m, Vector3 c, Vector3 ax, Vector3 ay, Vector3 az, float ex, float ey, float ez, int sw, bool bottom = false)
    {
        var r = TracksideArt.Sw(sw);
        Face(m, c + ax * ex, ax, ay, az, ey, ez, r);
        Face(m, c - ax * ex, -ax, ay, az, ey, ez, r);
        Face(m, c + az * ez, az, ax, ay, ex, ey, r);
        Face(m, c - az * ez, -az, ax, ay, ex, ey, r);
        Face(m, c + ay * ey, ay, ax, az, ex, ez, r);
        if (bottom) Face(m, c - ay * ey, -ay, ax, az, ex, ez, r);
    }
    static void Face(MB m, Vector3 fc, Vector3 n, Vector3 u, Vector3 w, float eu, float ew, Vector4 r)
    {
        Quad(m, fc - u * eu - w * ew, fc + u * eu - w * ew, fc + u * eu + w * ew, fc - u * eu + w * ew, n, r);
    }
    // a slim upright post: front and two sides only (the back faces the scenery), base at b
    static void Post(MB m, Vector3 b, Vector3 front, Vector3 along, float half, float y0, float y1, int sw)
    {
        var r = TracksideArt.Sw(sw); Vector3 f = front * half, s = along * half, up = Vector3.up;
        Quad(m, b + f - s + up * y0, b + f + s + up * y0, b + f + s + up * y1, b + f - s + up * y1, front, r);
        Quad(m, b - f + s + up * y0, b + f + s + up * y0, b + f + s + up * y1, b - f + s + up * y1, along, r);
        Quad(m, b - f - s + up * y0, b + f - s + up * y0, b + f - s + up * y1, b - f - s + up * y1, -along, r);
    }
    // flat ground decal strip piece (y = h) between lateral lines; r = grime column, v along
    static void Decal(MB m, Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1, float h, float va, float vb, Vector4 r)
    {
        // a0/b0 = the "u = 0" edge, a1/b1 = the "u = 1" edge; a = start, b = end
        Vector3 up = Vector3.up * h;
        Quad(m, a0 + up, a1 + up, b1 + up, b0 + up, Vector3.up, new Vector4(r.x, va, r.z, vb));
        // (Quad gives a (u0,va) a1 (u1,va) b1 (u1,vb) b0 (u0,vb))
    }

    // ---------------------------------------------------------------- spatial grid over centreline samples
    class Grid
    {
        readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        readonly Track t;
        public readonly float cell;
        public Grid(Track track, float cellSize)
        {
            t = track; cell = cellSize;
            for (int i = 0; i < t.N; i++)
            {
                long key = Key(Mathf.FloorToInt((float)t.pts[i].x / cell), Mathf.FloorToInt((float)t.pts[i].y / cell));
                List<int> list;
                if (!cells.TryGetValue(key, out list)) { list = new List<int>(); cells[key] = list; }
                list.Add(i);
            }
        }
        static long Key(int cx, int cy) { return ((long)cx << 32) ^ (long)(uint)cy; }
        // distance (Unity x/z plane) to the nearest centreline sample; the cell size if none is that close
        public float MinDist(Vector3 p)
        {
            int cx = Mathf.FloorToInt(p.x / cell), cy = Mathf.FloorToInt(p.z / cell);
            float best = cell * cell;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    List<int> list;
                    if (!cells.TryGetValue(Key(cx + dx, cy + dy), out list)) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        var q = t.pts[list[k]];
                        float ex = (float)q.x - p.x, ez = (float)q.y - p.z, d = ex * ex + ez * ez;
                        if (d < best) best = d;
                    }
                }
            return Mathf.Sqrt(best);
        }
    }

    // ---------------------------------------------------------------- footprints (Scenery.Town and the trees keep out)
    struct Foot { public Vector2 c, t; public float hl, hd; }
    static readonly List<Foot> feet = new List<Foot>();
    public static bool Blocked(float x, float z, float r)
    {
        for (int k = 0; k < feet.Count; k++)
        {
            var f = feet[k]; float dx = x - f.c.x, dz = z - f.c.y;
            float a = Mathf.Abs(dx * f.t.x + dz * f.t.y), b = Mathf.Abs(-dx * f.t.y + dz * f.t.x);
            if (a < f.hl + r && b < f.hd + r) return true;
        }
        return false;
    }
    static void AddFoot(Vector3 c, Vector3 along, float hl, float hd)
    {
        var a = new Vector2(along.x, along.z).normalized;
        feet.Add(new Foot { c = new Vector2(c.x, c.z), t = a, hl = hl, hd = hd });
    }

    // objects that must not go into the light bake's shadow map (see-through fence, ground decals): WorldBuilder parents
    // this holder to the world only after LightBake.AddOccluders
    public static GameObject Late;
    static Material fenceMat;
    static readonly List<Renderer> fenceRs = new List<Renderer>(), optional = new List<Renderer>();

    static Vector3 P(TrackPoint p, float h = 0f) { return new Vector3((float)p.x, h, (float)p.y); }
    static Vector3 Tan(TrackPoint p) { return new Vector3((float)p.tx, 0f, (float)p.ty).normalized; }
    static Vector3 Nrm(TrackPoint p) { return new Vector3((float)p.nx, 0f, (float)p.ny).normalized; }
    static float Rand(int i, int s) { float x = Mathf.Sin(i * 12.9898f + s * 78.233f) * 43758.5453f; return x - Mathf.Floor(x); }

    // ================================================================ build
    public static void Build(Track t, Transform root, bool street)
    {
        feet.Clear(); fenceRs.Clear(); optional.Clear(); fenceMat = null; Late = null;
        if (t == null || t.N < 8 || root == null) return;
        int N = t.N; float ds = (float)t.ds;
        float hw = (float)t.width * 0.5f, kerb = WorldBuilder.Kerb;
        float minClear = hw + kerb + 0.5f;
        int gfx = WorldBuilder.Gfx; bool low = gfx <= 1, high = gfx >= 3;
        bool forest = t.def != null && t.def.forest > 0.6;
        bool hills = WorldBuilder.Height != null;
        float skirt = hills ? -1.0f : 0f; // on hills walls reach a little below the street so no gap shows on slopes
        var cutSh = Resources.Load<Shader>("BakedCutout");
        bool fences = !low && WorldBuilder.BakeReady && cutSh != null && cutSh.isSupported;
        bool decals = !low && Resources.Load<Material>("Shadow") != null;
        var grid = new Grid(t, 25f); var big = new Grid(t, 60f);
        atlasL = new Layer(); crowdL = new Layer(); fenceL = new Layer(); grimeL = new Layer();
        chunkPts = Mathf.Max(20, Mathf.RoundToInt(250f / ds));
        tint = Color.white; aoOn = true;
        SetChunk(0);
        System.Func<int, int> W = k => ((k % N) + N) % N;

        // ---------------------------------------------------------------- barrier line = the physics wall
        float wallLat = (float)Traffic.WallOffset(t) + 0.05f;
        var curvAbs = new float[N];
        for (int i = 0; i < N; i++) curvAbs[i] = Mathf.Abs((float)t.pts[i].curv);
        float Off(int i, int sg)
        {
            float o = wallLat, kIn = 0f;
            // inside of a bend: keep well inside the radius so the offset curve cannot fold back
            for (int j = -4; j <= 4; j++) kIn = Mathf.Max(kIn, sg * (float)t.pts[W(i + j)].curv);
            if (kIn > 1e-5f) o = Mathf.Min(o, 0.8f / kIn);
            return o;
        }
        // is p clear of every part of the circuit by at least "need" (capped by the coarse grid's reach)?
        bool Free(Vector3 p, float need)
        {
            if (Waterfront.InWater(p.x, p.z) || WorldBuilder.Pad.Contains(new Vector2(p.x, p.z)) || Blocked(p.x, p.z, 0.3f)) return false;
            return big.MinDist(p) >= Mathf.Min(need, big.cell - 1f);
        }

        // ---------------------------------------------------------------- corners (curvature peaks), outside side
        var corners = new List<int>();
        {
            int win = Mathf.Max(4, Mathf.RoundToInt(40f / ds));
            for (int i = 0; i < N; i++)
            {
                float k = curvAbs[i];
                if (k < 1f / 250f) continue;
                bool peak = true;
                for (int j = -win; j <= win && peak; j++)
                {
                    if (j == 0) continue;
                    float kj = curvAbs[W(i + j)];
                    if (kj > k || (kj == k && j < 0)) peak = false;
                }
                if (peak) corners.Add(i);
            }
            corners.Sort((a, b) => curvAbs[b].CompareTo(curvAbs[a]));
        }
        int Outside(int apex) => t.pts[apex].curv > 0 ? -1 : 1; // left-hand bend: the outside is on the right
        int Gap(int a, int b) { int d = Mathf.Abs(a - b); return Mathf.Min(d, N - d); }

        // per side flags along the lap: 0 = left (+1), 1 = right (-1)
        var dirt = new float[2, N]; var fenceZ = new bool[2, N]; var tyreZ = new bool[2, N];
        int SI(int sg) => sg > 0 ? 0 : 1;
        // likely impacts: on the outside, a little after the apex of the faster-than-hairpin corners
        var impacts = new List<int>();
        {
            int maxImp = low ? 6 : 14, after = Mathf.RoundToInt(30f / ds), spread = Mathf.RoundToInt(16f / ds);
            foreach (int apex in corners)
            {
                if (impacts.Count >= maxImp) break;
                bool near = false; foreach (int u in impacts) if (Gap(u, apex) < Mathf.RoundToInt(120f / ds)) near = true;
                if (near) continue;
                impacts.Add(apex);
                int e = apex + after, s = SI(Outside(apex));
                for (int j = -spread; j <= spread; j++) dirt[s, W(e + j)] = Mathf.Max(dirt[s, W(e + j)], 1f - Mathf.Abs(j) / (float)(spread + 1));
            }
        }

        // ---------------------------------------------------------------- tyre walls: outside of the slow corners
        var tyreCorners = new List<int>();
        {
            int maxC = low ? 4 : high ? 12 : 8;
            float kMin = street ? 1f / 45f : 1f / 110f;
            int before = Mathf.RoundToInt((street ? 12f : 15f) / ds), after = Mathf.RoundToInt((street ? 14f : 45f) / ds);
            foreach (int apex in corners)
            {
                if (tyreCorners.Count >= maxC || curvAbs[apex] < kMin) break;
                bool near = false; foreach (int u in tyreCorners) if (Gap(u, apex) < Mathf.RoundToInt(60f / ds)) near = true;
                if (near) continue;
                tyreCorners.Add(apex);
                int s = SI(Outside(apex));
                for (int j = -before; j <= after; j++) { tyreZ[s, W(apex + j)] = true; if (!street) fenceZ[s, W(apex + j)] = true; }
            }
        }

        // ---------------------------------------------------------------- pit straight: extent and side
        int pitSide = 0, pitA = 0, pitB = -1; // pit run from track index offset pitA to pitB (relative to 0), on pitSide
        float laneW = 12f, garD = 15f, garH = 9f, bay = 8f;
        {
            float kS = street ? 1f / 300f : 1f / 800f;
            int fwd = 0, back = 0, lim = Mathf.RoundToInt(350f / ds);
            while (fwd < lim && curvAbs[W(fwd)] < kS) fwd++;
            while (back < lim && curvAbs[W(-back)] < kS) back++;
            float s0 = -Mathf.Max(30f, back * ds * 0.85f), s1 = Mathf.Max(30f, fwd * ds * 0.85f);
            if (s1 - s0 > 280f) { float c0 = (s0 + s1) * 0.5f; s0 = Mathf.Max(s0, c0 - 140f); s1 = s0 + 280f; }
            int nb = Mathf.Max(1, Mathf.FloorToInt((s1 - s0) / bay));
            // circuit centre: the pits usually sit on the inside
            var bb = t.Bounds(); var mid = new Vector3((float)(bb.minX + bb.maxX) * 0.5f, 0, (float)(bb.minY + bb.maxY) * 0.5f);
            int bestLen = 0, bestSide = 0, bestA = 0;
            // the real pit side where the circuit data gives it (CircuitDef.pitSide); the other side only if that one has no room
            int want = t.def != null ? t.def.pitSide : 0;
            for (int pass = 0; pass < 2 && bestLen < 4; pass++)
            for (int sg = -1; sg <= 1; sg += 2)
            {
                if (want != 0 && (pass == 0) != (sg == want)) continue;
                int run = 0, runA = 0;
                for (int b = 0; b <= nb; b++)
                {
                    bool ok = b < nb;
                    if (ok)
                    {
                        float sc = s0 + (b + 0.5f) * bay; int i = W(Mathf.RoundToInt(sc / ds)); var p = t.pts[i];
                        Vector3 D = Nrm(p) * sg, T = Tan(p), c = P(p);
                        float back2 = wallLat + 0.5f + laneW + garD;
                        for (int q = 0; q < 6 && ok; q++)
                        {
                            float lat = q < 3 ? wallLat + 1f : back2, al = (q % 3 - 1) * bay * 0.5f;
                            ok = Free(c + D * lat + T * al, lat - 1.5f) && !Waterfront.InTunnel((c + D * lat).x, (c + D * lat).z);
                        }
                    }
                    if (ok) { if (run == 0) runA = b; run++; }
                    else
                    {
                        // prefer the inside of the circuit on a tie
                        var p0 = t.pts[0]; bool inside = Vector3.Dot(Nrm(p0) * sg, mid - P(p0)) > 0;
                        if (run > bestLen || (run == bestLen && run > 0 && inside)) { bestLen = run; bestSide = sg; bestA = runA; }
                        run = 0;
                    }
                }
            }
            if (bestLen >= 4)
            {
                pitSide = bestSide;
                pitA = Mathf.RoundToInt((s0 + bestA * bay) / ds);
                pitB = Mathf.RoundToInt((s0 + (bestA + bestLen) * bay) / ds);
            }
        }
        bool InPit(int i, int sg)
        {
            if (sg != pitSide || pitB < pitA) return false;
            int rel = i > N / 2 ? i - N : i;
            return rel >= pitA && rel <= pitB;
        }
        if (pitB >= pitA) for (int k = pitA; k <= pitB; k++) fenceZ[SI(pitSide), W(k)] = true;

        // ---------------------------------------------------------------- pit complex
        if (pitB >= pitA)
        {
            int sg = pitSide;
            float laneIn = wallLat + 0.5f, laneOut = laneIn + laneW, backL = laneOut + garD;
            int bays = Mathf.Max(1, Mathf.RoundToInt((pitB - pitA) * ds / bay));
            for (int b = 0; b < bays; b++)
            {
                float sa = pitA * ds + b * bay, sb = sa + bay;
                int ia = W(Mathf.RoundToInt(sa / ds)), ib = W(Mathf.RoundToInt(sb / ds));
                SetChunk(ia);
                var pa = t.pts[ia]; var pb = t.pts[ib];
                Vector3 ca = P(pa), cb = P(pb), Da = Nrm(pa) * sg, Db = Nrm(pb) * sg, T = Tan(pa), D = (Da + Db).normalized;
                Vector3 up = Vector3.up;
                // pit lane floor and the dividing line between the fast lane and the working lane
                Quad(A, ca + Da * laneIn + up * 0.03f, cb + Db * laneIn + up * 0.03f, cb + Db * laneOut + up * 0.03f, ca + Da * laneOut + up * 0.03f, up, TracksideArt.Sw(TracksideArt.PitAsphalt));
                float lineL = laneIn + 6.5f;
                Quad(A, ca + Da * lineL + up * 0.04f, cb + Db * lineL + up * 0.04f, cb + Db * (lineL + 0.15f) + up * 0.04f, ca + Da * (lineL + 0.15f) + up * 0.04f, up, TracksideArt.Sw(TracksideArt.LineWhite));
                // garage front: door below, glass above, parapet with an advertiser on top
                Vector3 fa = ca + Da * laneOut, fb = cb + Db * laneOut, ba = ca + Da * backL, bk = cb + Db * backL;
                Vector3 fwdFace = -D;
                bool aFirst = Vector3.Dot(Vector3.Cross(fwdFace, up), fb - fa) > 0; // a on the viewer's left?
                Vector3 L0 = aFirst ? fa : fb, R0 = aFirst ? fb : fa;
                Wall(A, L0, R0, skirt, 4.5f, fwdFace, TracksideArt.Door);
                Wall(A, L0, R0, 4.5f, garH - 0.2f, fwdFace, TracksideArt.Glass);
                Wall(A, L0, R0, garH - 0.2f, garH + 1.6f, fwdFace, TracksideArt.Ad(b));
                // back, roof
                Wall(A, ba, bk, skirt, garH + 1.6f, D, TracksideArt.Sw(TracksideArt.BuildGrey));
                Quad(A, fa + up * (garH + 1.6f), fb + up * (garH + 1.6f), bk + up * (garH + 1.6f), ba + up * (garH + 1.6f), up, TracksideArt.Sw(TracksideArt.RoofUnder));
                // ends of the building
                if (b == 0) Wall(A, fa, ba, skirt, garH + 1.6f, -T, TracksideArt.Sw(TracksideArt.BuildWhite));
                if (b == bays - 1) Wall(A, fb, bk, skirt, garH + 1.6f, T, TracksideArt.Sw(TracksideArt.BuildWhite));
                // canopy over the working lane, and a garage divider pillar
                if (!low)
                {
                    float cy = 4.6f, cIn = laneOut - 3.5f;
                    Vector3 ia3 = ca + Da * cIn, ib3 = cb + Db * cIn;
                    Quad(A, ia3 + up * (cy + 0.3f), ib3 + up * (cy + 0.3f), fb + up * (cy + 0.3f), fa + up * (cy + 0.3f), up, TracksideArt.Sw(TracksideArt.RoofWhite));
                    Quad(A, ia3 + up * cy, ib3 + up * cy, fb + up * cy, fa + up * cy, -up, TracksideArt.Sw(TracksideArt.RoofUnder));
                    Wall(A, ia3, ib3, cy, cy + 0.3f, -D, TracksideArt.Sw(TracksideArt.RoofWhite));
                    Box(A, fa - D * 0.2f + up * 2.3f, T, up, D, 0.2f, 2.3f, 0.2f, TracksideArt.BuildWhite);
                }
                // pit-wall stand every third bay (team stands on the pit wall)
                if (!low && b % 3 == 1)
                {
                    Vector3 c = ca + Da * (laneIn + 0.9f) + up * 1.2f;
                    Box(A, c, T, up, D, 1.6f, 1.2f, 0.6f, TracksideArt.Scaffold);
                    Box(A, c + up * 1.35f, T, up, D, 1.8f, 0.08f, 0.9f, TracksideArt.RoofWhite, true);
                }
                AddFoot((ca + cb) * 0.5f + D * ((laneIn + backL) * 0.5f), T, bay * 0.5f + 0.5f, (backL - laneIn) * 0.5f + 0.5f);
            }
            // paddock motorhomes behind the garages (High, parkland)
            if (high && !street)
            {
                int[] stripe = { TracksideArt.StripeRed, TracksideArt.StripeTeal, TracksideArt.StripeYellow, TracksideArt.StripeBlue };
                float step = 17f;
                int n = Mathf.FloorToInt((pitB - pitA) * ds / step);
                for (int r = 0; r < 2; r++)
                    for (int k = 0; k < n; k++)
                    {
                        float sc = pitA * ds + (k + 0.5f) * step; int i = W(Mathf.RoundToInt(sc / ds)); var p = t.pts[i];
                        Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up;
                        float lat = backL + 8f + r * 9f;
                        Vector3 c = P(p) + D * lat;
                        if (!Free(c, lat - 3f) || !Free(c + T * 7f, lat - 3f) || !Free(c - T * 7f, lat - 3f)) continue;
                        SetChunk(i);
                        Box(A, c + up * 1.9f, T, up, D, 6.5f, 1.9f, 1.3f, TracksideArt.BuildWhite);
                        Box(A, c + up * 1.2f, T, up, D, 6.52f, 0.25f, 1.32f, stripe[(k + r) % stripe.Length]);
                        AddFoot(c, T, 7f, 1.6f);
                    }
            }
        }

        // ---------------------------------------------------------------- grandstands
        const float RowD = 0.85f, RowH = 0.45f;
        int[] seatCols = { TracksideArt.SeatBlue, TracksideArt.SeatGrey, TracksideArt.SeatRed, TracksideArt.SeatGreen };
        Color[] crowdTints = { new Color(1f, 1f, 1f), new Color(1.08f, 0.94f, 0.82f), new Color(0.9f, 0.96f, 1.08f), new Color(1.05f, 1.02f, 0.84f), new Color(0.86f, 0.86f, 0.88f) };
        int standNo = 0;
        bool Stand(int i, int sg, float halfL, int rows, bool roof)
        {
            var p = t.pts[i];
            Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up;
            float baseH = street ? 2.4f : 1.4f, depth = rows * RowD;
            float front0 = Off(i, sg) + (street ? 1.8f : 4.5f);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                float front = front0 + attempt * 5f;
                Vector3 O = P(p) + D * front;
                bool ok = true;
                for (int q = 0; q <= 6 && ok; q++)
                {
                    float al = -halfL - 1f + q * (2f * halfL + 2f) / 6f;
                    ok = Free(O + T * al, front - 2f) && Free(O + T * al + D * (depth + 1f), front + depth - 2f);
                }
                if (!ok) continue;
                SetChunk(i);
                int id = standNo++;
                int seat = seatCols[id % seatCols.Length];
                float topY = baseH + (rows - 1) * RowH;
                // front wall of the stand with advertisers
                int panels = Mathf.Max(1, Mathf.RoundToInt(halfL * 2f / 8f));
                float pw = halfL * 2f / panels;
                for (int k = 0; k < panels; k++)
                {
                    Vector3 c = O + T * (-halfL + (k + 0.5f) * pw);
                    Panel(A, c, -D, pw * 0.5f, skirt, baseH - 0.9f, TracksideArt.Sw(street ? TracksideArt.Scaffold : TracksideArt.StandFrame));
                    Panel(A, c, -D, pw * 0.5f, baseH - 0.9f, baseH, TracksideArt.Ad(id * 3 + k));
                }
                // rows: riser, tread and the crowd sitting on it
                for (int r = 0; r < rows; r++)
                {
                    float y = baseH + r * RowH, d0 = r * RowD, d1 = d0 + RowD;
                    Vector3 l0 = O - T * halfL + D * d0, r0 = O + T * halfL + D * d0, l1 = O - T * halfL + D * d1, r1 = O + T * halfL + D * d1;
                    if (r > 0) Quad(A, l0 + up * (y - RowH), r0 + up * (y - RowH), r0 + up * y, l0 + up * y, -D, TracksideArt.Sw(TracksideArt.ConcreteDark));
                    Quad(A, l0 + up * y, r0 + up * y, r1 + up * y, l1 + up * y, up, TracksideArt.Sw(seat));
                    // crowd: sections of ~10 m with their own band, offset and colour mood
                    int secs = Mathf.Max(1, Mathf.RoundToInt(halfL * 2f / 10f));
                    float sl = halfL * 2f / secs;
                    for (int s = 0; s < secs; s++)
                    {
                        int h = id * 997 + r * 31 + s;
                        int band = Rand(h, 1) < 0.72f ? (Rand(h, 2) < 0.5f ? 0 : 1) : (Rand(h, 3) < 0.6f ? 2 : 3);
                        float u0 = Rand(h, 4), len = sl / (32f * 0.55f);
                        Color ct = crowdTints[(int)(Rand(h, 5) * crowdTints.Length) % crowdTints.Length] * (0.9f + 0.2f * Rand(h, 6));
                        tint = ct; aoOn = false;
                        Vector3 sa = O + T * (-halfL + s * sl) + D * (d0 + 0.2f), sb = sa + T * sl;
                        Vector3 back = D * 0.3f;
                        Quad(C, sa + up * y, sb + up * y, sb + back + up * (y + 1.0f), sa + back + up * (y + 1.0f), -D + up * 0.3f,
                             new Vector4(u0, band * 0.25f + 0.01f, u0 + len, band * 0.25f + 0.24f));
                        tint = Color.white; aoOn = true;
                    }
                    // stepped end walls
                    for (int e = -1; e <= 1; e += 2)
                    {
                        Vector3 en = T * e, ea = O + T * (e * halfL) + D * d0, eb = O + T * (e * halfL) + D * d1;
                        Wall(A, ea, eb, skirt, y, en, TracksideArt.Sw(TracksideArt.StandFrame));
                    }
                }
                // back wall (outside and the part seen above the top row), railing height
                Vector3 bl = O - T * halfL + D * depth, br = O + T * halfL + D * depth;
                float backTop = roof ? topY + 3.6f : topY + 1.1f;
                Wall(A, bl, br, skirt, backTop, D, TracksideArt.Sw(street ? TracksideArt.Scaffold : TracksideArt.StandFrame));
                Quad(A, bl + up * topY, br + up * topY, br + up * backTop, bl + up * backTop, -D, TracksideArt.Sw(TracksideArt.StandFrame));
                if (roof)
                {
                    // cantilever roof, sloping slightly up towards the track, advertiser fascia along its front edge
                    float ry = backTop, rFront = -1.2f;
                    Vector3 fl = O - T * (halfL + 0.6f) + D * rFront, fr = O + T * (halfL + 0.6f) + D * rFront;
                    Vector3 bl2 = bl - T * 0.6f, br2 = br + T * 0.6f;
                    Vector3 lift = up * 0.6f;
                    Quad(A, fl + up * ry + lift, fr + up * ry + lift, br2 + up * ry, bl2 + up * ry, up, TracksideArt.Sw(TracksideArt.RoofWhite));
                    Quad(A, fl + up * (ry - 0.25f) + lift, fr + up * (ry - 0.25f) + lift, br2 + up * (ry - 0.25f), bl2 + up * (ry - 0.25f), -up, TracksideArt.Sw(TracksideArt.RoofUnder));
                    int fp = Mathf.Max(1, Mathf.RoundToInt(halfL * 2f / 6f));
                    float fw = (halfL * 2f + 1.2f) / fp;
                    for (int k = 0; k < fp; k++)
                    {
                        Vector3 c = fl + T * ((k + 0.5f) * fw) + lift;
                        Panel(A, c, -D, fw * 0.5f, ry - 0.9f, ry + 0.05f, TracksideArt.Ad(id + k * 5 + 1));
                    }
                    // slim back columns and diagonal-looking struts
                    int cols = Mathf.Max(2, Mathf.RoundToInt(halfL / 8f) + 1);
                    for (int k = 0; k < cols; k++)
                    {
                        Vector3 c = bl + T * (2f * halfL * k / (cols - 1)) + D * 0.25f;
                        Box(A, c + up * (ry * 0.5f), T, up, D, 0.18f, ry * 0.5f, 0.18f, TracksideArt.Steel);
                    }
                }
                // a catch fence and a few metres of it either side, in front of the stand (parkland)
                if (!street)
                {
                    int span = Mathf.RoundToInt((halfL + 10f) / ds), s = SI(sg);
                    for (int j = -span; j <= span; j++) fenceZ[s, W(i + j)] = true;
                }
                AddFoot(O + D * (depth * 0.5f), T, halfL + 1f, depth * 0.5f + 1.5f);
                return true;
            }
            return false;
        }
        {
            // main straight: facing the pits (opposite side), centred on the line
            int ms = pitSide == 0 ? -1 : -pitSide;
            int mainN = low ? 1 : high ? 3 : 2;
            float mhl = street ? 18f : 24f;
            int mrows = low ? 9 : 13;
            float[] at = { 0f, -(2f * mhl + 6f), 2f * mhl + 6f };
            for (int k = 0; k < mainN; k++)
            {
                int i = W(Mathf.RoundToInt((at[k] - 30f) / ds));
                if (!Stand(i, ms, mhl, mrows, true) && k == 0) Stand(i, -ms, mhl, mrows, true);
            }
            // big corners: braking corners score by tightness and how long the run into them is
            int cornerN = low ? 1 : high ? 5 : 3;
            var scored = new List<KeyValuePair<float, int>>();
            foreach (int apex in corners)
            {
                if (curvAbs[apex] < 1f / 160f) continue;
                int run = 0; while (run < N / 4 && curvAbs[W(apex - Mathf.RoundToInt(60f / ds) - run)] < 1f / 500f) run++;
                scored.Add(new KeyValuePair<float, int>(curvAbs[apex] * 100f * (1f + run * ds / 250f), apex));
            }
            scored.Sort((a, b) => b.Key.CompareTo(a.Key));
            var used = new List<int>();
            int placed = 0;
            foreach (var kv in scored)
            {
                if (placed >= cornerN) break;
                int apex = kv.Value;
                if (Gap(apex, 0) < Mathf.RoundToInt(180f / ds)) continue;
                bool near = false; foreach (int u in used) if (Gap(u, apex) < Mathf.RoundToInt(250f / ds)) near = true;
                if (near) continue;
                int at2 = W(apex - Mathf.RoundToInt(15f / ds));
                bool roof = high || placed == 0;
                if (Stand(at2, Outside(apex), street ? 14f : 20f, low ? 7 : 10, roof)) { used.Add(apex); placed++; }
            }
        }

        // ---------------------------------------------------------------- marshal posts
        {
            float spacing = low ? 600f : high ? 260f : 360f;
            int n = Mathf.FloorToInt((float)t.length / spacing), sideT = 0;
            for (int k = 0; k < n; k++)
            {
                int i = W(Mathf.RoundToInt((k * spacing + spacing * 0.6f) / ds));
                for (int tries = 0; tries < 2; tries++)
                {
                    int sg = ((sideT + tries) & 1) == 0 ? 1 : -1;
                    if (InPit(i, sg)) continue;
                    var p = t.pts[i]; Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up;
                    float lat = Off(i, sg) + (street ? 1.4f : 1.8f) + 0.8f;
                    Vector3 c = P(p) + D * lat;
                    if (!Free(c, lat - 1.2f) || !Free(c + T * 1.2f, lat - 1.2f) || !Free(c - T * 1.2f, lat - 1.2f)) continue;
                    SetChunk(i);
                    Box(A, c + up * 1.15f, T, up, D, 0.8f, 1.15f, 0.8f, TracksideArt.PaintWhite);
                    Box(A, c + up * 1.95f, T, up, D, 0.82f, 0.2f, 0.82f, TracksideArt.MarshalOrange);
                    Panel(A, c - D * 0.82f, -D, 0.6f, 1.2f, 1.7f, TracksideArt.Sw(TracksideArt.GlassDark));
                    Box(A, c + up * 2.4f, T, up, D, 1.05f, 0.08f, 1.05f, TracksideArt.RoofUnder, true);
                    // light panel on a pole, facing the approaching cars
                    Vector3 pole = c - D * 1.1f - T * 0.9f;
                    Post(A, pole, -D, T, 0.05f, skirt, 3.2f, TracksideArt.PostDark);
                    Box(A, pole + up * 3.2f, T, up, D, 0.35f, 0.35f, 0.05f, TracksideArt.GantryDark, true);
                    Panel(A, pole + up * 3.2f - T * 0.06f, -T, 0.25f, -0.25f, 0.25f, TracksideArt.Sw(TracksideArt.LampYellow));
                    AddFoot(c, T, 1.3f, 1.3f);
                    sideT++;
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- barriers (+ fences, + base shadow decals)
        {
            int step = Mathf.Max(1, Mathf.RoundToInt((street ? 3f : 4f) / ds));
            float maxLen = street ? 6f : low ? 22f : 15f, maxTurn = Mathf.Cos(3.5f * Mathf.Deg2Rad);
            float postGap = low ? 12f : 8f;
            var samples = new List<int>();
            for (int i = 0; i < N; i += step) samples.Add(i);
            if (N - samples[samples.Count - 1] < Mathf.Max(1, step / 2)) samples.RemoveAt(samples.Count - 1);
            samples.Add(N);
            for (int sg = -1; sg <= 1; sg += 2)
            {
                int s = SI(sg);
                bool run = false; int aI = 0, lastI = 0, segNo = 0; Vector3 aIn = Vector3.zero, aDir = Vector3.zero, lastIn = Vector3.zero, lastDir = Vector3.zero;
                float fenceU = 0f, postCarry = 0f;
                int Sig(int i) { i %= N; return (InPit(i, sg) ? 1 : 0) | (fences && fenceZ[s, i] ? 2 : 0) | (dirt[s, i] > 0.3f ? 4 : 0); }
                bool IsWall(int i) => street || InPit(i % N, sg);
                void Seg(int i0, Vector3 a, Vector3 da, int i1, Vector3 b, Vector3 db)
                {
                    SetChunk(i0 % N);
                    int i = i0 % N;
                    Vector3 up = Vector3.up, mid = (da + db).normalized, len3 = b - a;
                    float len = len3.magnitude;
                    bool dirty = dirt[s, i] > 0.3f, fz = fences && (street || fenceZ[s, i]);
                    float topY;
                    if (IsWall(i))
                    {
                        // concrete wall, painted strip along the top
                        float h = InPit(i, sg) ? 1.15f : 1.0f, th = 0.5f, hp = h - 0.28f;
                        int paint = InPit(i, sg) ? TracksideArt.PaintWhite : ((segNo & 1) == 0 ? TracksideArt.PaintRed : TracksideArt.PaintWhite);
                        Vector3 a1 = a + da * th, b1 = b + db * th;
                        if (dirty) tint = new Color(0.86f, 0.83f, 0.8f);
                        Quad(A, a + up * skirt, b + up * skirt, b + up * hp, a + up * hp, -mid, dirty ? TracksideArt.Scuff : TracksideArt.Sw(TracksideArt.Concrete));
                        tint = Color.white;
                        Quad(A, a + up * hp, b + up * hp, b + up * h, a + up * h, -mid, TracksideArt.Sw(paint));
                        Quad(A, a + up * h, b + up * h, b1 + up * h, a1 + up * h, up, TracksideArt.Sw(paint));
                        Quad(A, a1 + up * skirt, b1 + up * skirt, b1 + up * h, a1 + up * h, mid, TracksideArt.Sw(TracksideArt.Concrete));
                        topY = h;
                        if (decals && (street || InPit(i, sg) || !hills))
                        {
                            float va = fenceU / 4f, vb = (fenceU + len) / 4f;
                            Decal(G, a - da * 0.9f, a, b - db * 0.9f, b, 0.05f, va, vb, TracksideArt.GrimeCol(TracksideArt.GrimeShade));
                            if (dirty && street) Decal(G, a - da * 1.4f, a - da * 0.1f, b - db * 1.4f, b - db * 0.1f, 0.055f, 0f, 1f, TracksideArt.GrimeCol(TracksideArt.GrimeSmear));
                        }
                    }
                    else
                    {
                        // Armco: W-profile beams (two faces each, so the ridge catches the light) on posts behind
                        float[] beams = low ? new[] { 0.45f, 0.80f } : new[] { 0.40f, 0.71f, 0.76f, 1.07f };
                        var rs = TracksideArt.Sw(TracksideArt.Steel);
                        for (int k = 0; k + 1 < beams.Length; k += 2)
                        {
                            float yb = beams[k], yt = beams[k + 1], ym = (yb + yt) * 0.5f;
                            if (dirty && k == 0) tint = new Color(0.72f, 0.69f, 0.64f);
                            Vector3 ra = a + da * 0.05f, rb = b + db * 0.05f; // recessed middle
                            Quad(A, a + up * yb, b + up * yb, rb + up * ym, ra + up * ym, -mid - up * 0.5f, rs);
                            Quad(A, ra + up * ym, rb + up * ym, b + up * yt, a + up * yt, -mid + up * 0.5f, rs);
                            tint = Color.white;
                        }
                        if (!low) Quad(A, a + da * 0.12f + up * beams[0], b + db * 0.12f + up * beams[0], b + db * 0.12f + up * beams[beams.Length - 1], a + da * 0.12f + up * beams[beams.Length - 1], mid, rs);
                        // posts, evenly spaced across segments
                        float d = postCarry;
                        while (d < len)
                        {
                            Vector3 pc = Vector3.Lerp(a, b, d / Mathf.Max(len, 1e-3f)) + Vector3.Lerp(da, db, d / Mathf.Max(len, 1e-3f)) * 0.2f;
                            Post(A, pc, -mid, len3 / Mathf.Max(len, 1e-3f), 0.07f, skirt, beams[beams.Length - 1] + 0.04f, TracksideArt.PostDark);
                            d += postGap;
                        }
                        postCarry = d - len;
                        topY = beams[beams.Length - 1];
                        if (decals && (!hills || t.runL != null && (sg > 0 ? t.runL[i] : t.runR[i]) == SurfaceType.Gravel))
                            Decal(G, a - da * 0.9f, a + da * 0.15f, b - db * 0.9f, b + db * 0.15f, 0.05f, fenceU / 4f, (fenceU + len) / 4f, TracksideArt.GrimeCol(TracksideArt.GrimeShade));
                    }
                    // debris / catch fence above the barrier, leaning in at the top, posts every segment
                    if (fz)
                    {
                        float back = IsWall(i) ? 0.25f : 0.3f, y0 = topY - 0.05f, y1 = topY + 2.6f, y2 = topY + 3.3f;
                        Vector3 fa = a + da * back, fb = b + db * back, lean = -mid * 0.55f;
                        Vector4 r1 = new Vector4(fenceU, y0, fenceU + len, y1), r2 = new Vector4(fenceU, y1, fenceU + len, y2);
                        aoOn = false;
                        Quad(F, fa + up * y0, fb + up * y0, fb + up * y1, fa + up * y1, -mid, r1);
                        Quad(F, fa + up * y1, fb + up * y1, fb + lean + up * y2, fa + lean + up * y2, -mid, r2);
                        aoOn = true;
                        Post(A, fa + da * 0.06f, -mid, len3 / Mathf.Max(len, 1e-3f), 0.05f, topY - 0.3f, y1, TracksideArt.PostDark);
                        Quad(A, fa + da * 0.06f + up * y1, fa + da * 0.06f + up * (y1 + 0.1f), fa + lean + up * (y2 + 0.05f), fa + lean + up * (y2 - 0.05f), Vector3.Cross(mid, up), TracksideArt.Sw(TracksideArt.PostDark));
                    }
                    fenceU += len;
                    segNo++;
                }
                void Cap(Vector3 a, Vector3 da, int i)
                {
                    if (!IsWall(i % N)) return;
                    SetChunk(i % N);
                    Vector3 along = Vector3.Cross(da, Vector3.up), b = a + da * 0.5f;
                    Quad(A, a + Vector3.up * skirt, b + Vector3.up * skirt, b + Vector3.up * 1.0f, a + Vector3.up * 1.0f, along, TracksideArt.Sw(TracksideArt.Concrete));
                    Quad(A, a + Vector3.up * skirt, b + Vector3.up * skirt, b + Vector3.up * 1.0f, a + Vector3.up * 1.0f, -along, TracksideArt.Sw(TracksideArt.Concrete));
                }
                for (int si = 0; si < samples.Count; si++)
                {
                    int i = samples[si] % N;
                    var p = t.pts[i];
                    float o = Off(i, sg);
                    Vector3 dir = Nrm(p) * sg, inner = P(p) + dir * o;
                    bool ok = o >= minClear && grid.MinDist(inner) >= minClear;
                    if (ok && run && Vector3.Dot(inner - lastIn, Tan(p)) < 0.1f * step * ds) ok = false; // no fold-back in hairpins
                    if (!ok)
                    {
                        if (run) { if (lastI != aI) Seg(aI, aIn, aDir, lastI, lastIn, lastDir); Cap(lastIn, lastDir, lastI); }
                        run = false; continue;
                    }
                    if (!run) { run = true; aI = lastI = samples[si]; aIn = lastIn = inner; aDir = lastDir = dir; Cap(inner, dir, i); continue; }
                    bool split = (inner - aIn).magnitude > maxLen || Vector3.Dot(aDir, dir) < maxTurn || Sig(samples[si]) != Sig(aI);
                    if (split && lastI != aI) { Seg(aI, aIn, aDir, lastI, lastIn, lastDir); aI = lastI; aIn = lastIn; aDir = lastDir; }
                    lastI = samples[si]; lastIn = inner; lastDir = dir;
                }
                if (run && lastI != aI) { Seg(aI, aIn, aDir, lastI, lastIn, lastDir); Cap(lastIn, lastDir, lastI); }
            }
        }

        // ---------------------------------------------------------------- tyre walls with a conveyor-belt front
        {
            float depthT = street ? 0.65f : 1.25f, hT = 1.0f;
            int beltB = forest ? TracksideArt.BeltBlue : TracksideArt.BeltRed;
            for (int sg = -1; sg <= 1; sg += 2)
            {
                int s = SI(sg), piece = 0;
                bool have = false; Vector3 pf = Vector3.zero, pb = Vector3.zero, pd = Vector3.zero; int pI = 0;
                for (int i = 0; i <= N; i++)
                {
                    int k = i % N;
                    var p = t.pts[k];
                    Vector3 D = Nrm(p) * sg;
                    float o = Off(k, sg);
                    Vector3 back = P(p) + D * (o - 0.03f), front = P(p) + D * (o - depthT);
                    bool ok = tyreZ[s, k] && i < N && o - depthT >= minClear && grid.MinDist(front) >= minClear && !InPit(k, sg);
                    if (ok && have && Vector3.Dot(front - pf, Tan(p)) < 0.1f * ds) ok = false;
                    Vector3 up = Vector3.up;
                    if (ok && have && (front - pf).magnitude < 2.4f && i % Mathf.Max(1, Mathf.RoundToInt(2f / ds)) != 0) continue;
                    if (ok && have)
                    {
                        SetChunk(pI);
                        float len = (front - pf).magnitude;
                        tint = dirt[s, pI] > 0.3f ? new Color(0.8f, 0.78f, 0.75f) : Color.white;
                        Quad(A, pf + up * skirt, front + up * skirt, front + up * hT, pf + up * hT, -(pd + D), TracksideArt.Sw((piece & 1) == 0 ? beltB : TracksideArt.BeltWhite));
                        Quad(A, pf + up * hT, front + up * hT, back + up * hT, pb + up * hT, up, TracksideArt.Sub(TracksideArt.TyreTops, 0, Mathf.Min(1f, len / 2.5f), 0, depthT / 2.5f));
                        tint = Color.white;
                        if (decals && !hills) Decal(G, pf - pd * 0.8f, pf, front - D * 0.8f, front, 0.05f, 0, len / 4f, TracksideArt.GrimeCol(TracksideArt.GrimeShade));
                        piece++;
                    }
                    if (ok && !have) { SetChunk(k); Quad(A, front + up * skirt, back + up * skirt, back + up * hT, front + up * hT, -Tan(p), TracksideArt.Sw(TracksideArt.TyreBlack)); }
                    if (!ok && have) { SetChunk(pI); Quad(A, pf + up * skirt, pb + up * skirt, pb + up * hT, pf + up * hT, Tan(t.pts[pI]), TracksideArt.Sw(TracksideArt.TyreBlack)); }
                    have = ok;
                    if (ok) { pf = front; pb = back; pd = D; pI = k; }
                }
            }
        }

        // ---------------------------------------------------------------- start/finish gantry at track point 0
        {
            SetChunk(0);
            var p = t.pts[0];
            Vector3 c = P(p), T = Tan(p), Nn = Nrm(p), up = Vector3.up;
            float po = Mathf.Min(wallLat - 0.4f, street ? hw + kerb + 0.6f : hw + kerb + 2.5f);
            const float postH = 7.2f, beamY = 6.8f;
            for (int sg = -1; sg <= 1; sg += 2)
                Box(A, c + Nn * (po * sg) + up * (postH * 0.5f + skirt * 0.5f), Nn, up, T, 0.3f, postH * 0.5f - skirt * 0.5f, 0.3f, TracksideArt.GantryWhite);
            Box(A, c + up * beamY, Nn, up, T, po + 0.3f, 0.45f, 0.5f, TracksideArt.GantryWhite, true);
            // advertiser on both faces of the beam
            Panel(A, c + up * 0f - T * 0.52f, -T, po * 0.6f, beamY - 0.42f, beamY + 0.42f, TracksideArt.Ad(1));
            Panel(A, c + T * 0.52f, T, po * 0.6f, beamY - 0.42f, beamY + 0.42f, TracksideArt.Ad(4));
            // light panel under the beam facing the cars approaching the line (they come from -T): 5 columns x 2 lamps
            Vector3 panel = c + up * (beamY - 1.2f) - T * 0.3f;
            Box(A, panel, Nn, up, T, 2.5f, 0.75f, 0.15f, TracksideArt.GantryDark, true);
            for (int col = 0; col < 5; col++)
                for (int row = 0; row < 2; row++)
                {
                    Vector3 lc = panel + Nn * ((col - 2) * 0.95f) + up * (row == 0 ? 0.3f : -0.3f) - T * 0.17f;
                    aoOn = false;
                    Panel(A, lc, -T, 0.18f, -0.18f, 0.18f, TracksideArt.Sw(TracksideArt.LampRed));
                    aoOn = true;
                    Box(A, lc + up * 0.24f - T * 0.1f, Nn, up, T, 0.22f, 0.03f, 0.12f, TracksideArt.GantryDark); // visor
                }
        }

        // ---------------------------------------------------------------- advertising boards on the straights
        {
            int adCount = 0, adNo = 0, sideToggle = 0;
            int maxBoards = low ? 40 : 90; const int perBlock = 4;
            int startSkip = Mathf.CeilToInt(130f / ds);
            int blockStep = Mathf.Max(perBlock * 3 + 1, Mathf.RoundToInt(120f / ds));
            int bStep = Mathf.Max(2, Mathf.RoundToInt(6f / ds));
            for (int i = 0; i < N && adCount < maxBoards; i += blockStep)
            {
                if (i < startSkip || i > N - startSkip) continue;
                float kMax = 0f;
                for (int j = -15; j <= 15 + perBlock * bStep; j++) kMax = Mathf.Max(kMax, curvAbs[W(i + j)]);
                if (kMax > 1f / 600f) continue;
                int sg = (sideToggle++ & 1) == 0 ? 1 : -1;
                for (int b = 0; b < perBlock && adCount < maxBoards; b++)
                {
                    int k = W(i + b * bStep);
                    if (InPit(k, sg)) continue;
                    var p = t.pts[k];
                    Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up;
                    float behind = street ? 0.9f : 0.7f, h = street ? 2.0f : 1.35f;
                    Vector3 c = P(p) + D * (Off(k, sg) + behind);
                    if (grid.MinDist(c) < minClear || Blocked(c.x, c.z, 2.9f) || Waterfront.InWater(c.x, c.z)) continue;
                    SetChunk(k);
                    Panel(A, c, -D, 2.8f, 0.25f, h, TracksideArt.Ad(adNo++));
                    Panel(A, c + D * 0.06f, D, 2.8f, skirt, h, TracksideArt.Sw(TracksideArt.ConcreteDark));
                    Post(A, c + D * 0.03f - T * 2.6f, -D, T, 0.05f, skirt, 0.25f, TracksideArt.PostDark);
                    Post(A, c + D * 0.03f + T * 2.6f, -D, T, 0.05f, skirt, 0.25f, TracksideArt.PostDark);
                    adCount++;
                }
            }
        }

        // ---------------------------------------------------------------- tyre marks and dirt at likely impacts
        if (decals)
        {
            foreach (int apex in impacts)
            {
                int sg = Outside(apex), s = SI(sg);
                int e = apex + Mathf.RoundToInt(30f / ds);
                float lat0 = hw + kerb + 0.15f;
                float lat1 = street ? Mathf.Min(wallLat - 0.5f, hw + 3.1f) : hw + kerb + 1.85f;
                if (lat1 <= lat0 + 0.3f) continue;
                int len = Mathf.RoundToInt(28f / ds), st = Mathf.Max(1, Mathf.RoundToInt(2f / ds));
                for (int m = 0; m < 2; m++)
                {
                    int i0 = e - len / 2 + m * Mathf.RoundToInt(5f / ds);
                    float endLat = Mathf.Lerp(lat0 + 0.4f, lat1, 0.7f + 0.3f * m);
                    for (int j = 0; j + st <= len; j += st)
                    {
                        int ia = W(i0 + j), ib = W(i0 + j + st);
                        float fa = Mathf.SmoothStep(0, 1, Mathf.Clamp01(j / (len * 0.6f))), fb = Mathf.SmoothStep(0, 1, Mathf.Clamp01((j + st) / (len * 0.6f)));
                        float la = Mathf.Lerp(lat0, endLat, fa), lb = Mathf.Lerp(lat0, endLat, fb);
                        var pa = t.pts[ia]; var pb = t.pts[ib];
                        Vector3 Da = Nrm(pa) * sg, Db = Nrm(pb) * sg, ca = P(pa), cb = P(pb);
                        if (grid.MinDist(ca + Da * la) < hw + kerb) continue;
                        SetChunk(ia);
                        Decal(G, ca + Da * (la - 0.16f), ca + Da * (la + 0.16f), cb + Db * (lb - 0.16f), cb + Db * (lb + 0.16f), 0.06f,
                              j * ds / 4f, (j + st) * ds / 4f, TracksideArt.GrimeCol(TracksideArt.GrimeTyre));
                    }
                }
            }
            // gravel dragged back onto the astroturf where a trap ends (parkland)
            if (!street && t.runL != null)
                for (int sg = -1; sg <= 1; sg += 2)
                {
                    var run = sg > 0 ? t.runL : t.runR;
                    for (int i = 0; i < N; i++)
                    {
                        if (run[i] != SurfaceType.Gravel || run[W(i + 1)] == SurfaceType.Gravel) continue;
                        int len = Mathf.RoundToInt(10f / ds);
                        var pa = t.pts[W(i - len)]; var pb = t.pts[W(i + 2)];
                        float l0 = hw + kerb + 0.3f, l1 = hw + kerb + 1.9f;
                        Vector3 Da = Nrm(pa) * sg, Db = Nrm(pb) * sg;
                        SetChunk(i);
                        Decal(G, P(pa) + Da * l0, P(pa) + Da * l1, P(pb) + Db * l0, P(pb) + Db * l1, 0.055f, 0, 2.5f, TracksideArt.GrimeCol(TracksideArt.GrimeDirt));
                    }
                }
        }

        // ---------------------------------------------------------------- emit one mesh per material per chunk
        var parent = new GameObject("Trackside").transform;
        parent.SetParent(root, false);
        Late = new GameObject("TracksideLate");
        var atlasMat = WorldBuilder.Mat(Color.white, TracksideArt.Atlas(), 0.1f);
        var crowdMat = WorldBuilder.Mat(Color.white, TracksideArt.Crowd(), 0.1f);
        Emit(parent, "Trackside", atlasL, atlasMat, null);
        Emit(parent, "Crowd", crowdL, crowdMat, null);
        if (fences)
        {
            fenceMat = WorldBuilder.Mat(Color.white, TracksideArt.Fence(), 0.1f);
            Emit(Late.transform, "Fence", fenceL, fenceMat, fenceRs);
            optional.AddRange(fenceRs);
        }
        if (decals)
        {
            var gm = new Material(Resources.Load<Material>("Shadow")) { mainTexture = TracksideArt.Grime() };
            Emit(Late.transform, "Grime", grimeL, gm, optional);
        }
        atlasL = crowdL = fenceL = grimeL = null; A = C = F = G = null;
    }

    static void Emit(Transform parent, string name, Layer layer, Material mat, List<Renderer> collect)
    {
        foreach (var kv in layer.parts)
        {
            var mb = kv.Value;
            if (mb.tri.Count == 0) continue;
            var mesh = new Mesh { name = "Trackside_" + name + "_" + kv.Key };
            if (mb.v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(mb.v);
            mesh.SetUVs(0, mb.uv);
            mesh.SetTriangles(mb.tri, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject(name + "_" + kv.Key);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            BakeTint.Add(mesh, mb.col.ToArray());
            if (collect != null) collect.Add(r);
        }
    }

    // after LightBake.Apply: fences get the alpha-tested baked shader; the optional layers follow the graphics level
    public static void AfterBake(GameObject root)
    {
        if (fenceMat != null)
        {
            var sh = Resources.Load<Shader>("BakedCutout");
            if (LightBake.Active && sh != null && sh.isSupported)
            {
                fenceMat.shader = sh;
                fenceMat.SetFloat("_Cutoff", 0.5f);
                fenceMat.renderQueue = 2450;
            }
            else
            {
                // an opaque grey sheet would be worse than no fence
                foreach (var r in fenceRs) if (r != null) { r.enabled = false; optional.Remove(r); }
            }
        }
        if (optional.Count > 0 && root != null) TracksideLod.Attach(root, optional.ToArray());
        fenceMat = null; fenceRs.Clear(); optional.Clear();
    }
}
