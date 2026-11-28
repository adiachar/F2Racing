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
//   * circuit furniture: TV camera towers at the corners, big screens by the stands, a recovery crane behind the
//     barrier at the slow corners, floodlight masts; lock-up marks into the braking zones and dirt over the exit kerbs
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
        public readonly List<Vector4> u1 = new List<Vector4>(); // crowd layer only: the seat block's colour (Baked F2_CROWD)
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
    static Layer atlasL, crowdL, fenceL, grimeL, postL, peopleL;
    static MB A, C, F, G, Pm, Pp;  // Pp: standing spectators behind the fences (cut-out people, Baked F2_PEOPLE)       // the current chunk of each layer (P: thin posts, in short chunks culled by distance)
    static int chunkPts = 125, postPts = 20;
    static Color tint = Color.white;
    static Vector4 seatTint = new Vector4(0.36f, 0.37f, 0.40f, 1f); // crowd quads: colour of the empty seats (F2_CROWD)
    static Material crowdMat, peopleMat;
    static readonly List<Renderer> peopleRs = new List<Renderer>();
    static readonly int[] BoldAds = { 1, 6, 4, 0, 15, 12, 3, 8, 13, 2 };
    // Per-circuit stand layouts (gap list item 18): where the real grandstands are, in priority order (the graphics
    // level takes the first 1 / 3 / 5, as the generic layout did). Each entry: { s (m along the lap, from the circuit's
    // corner data), side (2 = the outside of the sharpest bend within 60 m of s, +1 left, -1 right), m before the apex
    // (or before s for a fixed side; negative = after), half length (m) }. Circuits not listed keep the generic layout
    // (the sharpest braking corners). MainRows: rows of the main-straight stands where they are much bigger than usual.
    static readonly Dictionary<string, float[][]> StandLayout = new Dictionary<string, float[][]>
    {
        // La Source (hairpin, outside), the Eau Rouge exit (up the left side of Raidillon, ~1112-1210 m), the Bus Stop
        // chicane, the Les Combes braking zone, Pouhon
        ["spa"] = new[] { new[] { 384f, 2f, 15f, 20f }, new[] { 1180f, 1f, 0f, 22f }, new[] { 6733f, 2f, 18f, 20f }, new[] { 2412f, 2f, 30f, 20f }, new[] { 3818f, 2f, 10f, 18f } },
        // the last chicane, T1 (braking from the main straight), the Esses, the hairpin, Spoon
        ["suzuka"] = new[] { new[] { 5402f, 2f, 18f, 20f }, new[] { 728f, 2f, 30f, 22f }, new[] { 1198f, 2f, 0f, 18f }, new[] { 2905f, 2f, 15f, 18f }, new[] { 3802f, 2f, 10f, 18f } },
        // T3 (the big uphill stop), T1, the last corner, T4
        ["spielberg"] = new[] { new[] { 1402f, 2f, 18f, 20f }, new[] { 463f, 2f, 25f, 20f }, new[] { 3994f, 2f, 15f, 20f }, new[] { 2225f, 2f, 15f, 18f }, new[] { 3790f, 2f, 15f, 18f } },
    };
    static readonly Dictionary<string, int> MainRows = new Dictionary<string, int> { ["suzuka"] = 17 };
    static bool aoOn = true;
    // Armco W-beam profile (Medium / High): heights up the 0.31 m beam and depth behind its front plane (m)
    static readonly float[] WY = { 0f, 0.075f, 0.155f, 0.235f, 0.31f }, WDep = { 0.035f, 0f, 0.075f, 0f, 0.035f };
    const float AoH = 1.4f, AoMin = 0.58f;

    static void SetChunk(int i)
    {
        int c = i / chunkPts;
        A = atlasL.Get(c); C = crowdL.Get(c); F = fenceL.Get(c); G = grimeL.Get(c); Pm = postL.Get(i / postPts); Pp = peopleL.Get(c);
    }

    // a vertical face from p0 to p1 cut into equal pieces of about pieceLen, each showing the whole region r: one precast
    // block, rail section or cladding panel per piece, so the detail keeps its real size on long faces
    static void Pieces(MB m, Vector3 p0, Vector3 p1, float y0, float y1, Vector3 n, Vector4 r, float pieceLen)
    {
        int k = Mathf.Max(1, Mathf.RoundToInt((p1 - p0).magnitude / pieceLen));
        for (int j = 0; j < k; j++) Wall(m, Vector3.Lerp(p0, p1, (float)j / k), Vector3.Lerp(p0, p1, (float)(j + 1) / k), y0, y1, n, r);
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
        if (m == C) { m.u1.Add(seatTint); m.u1.Add(seatTint); m.u1.Add(seatTint); m.u1.Add(seatTint); }
        // Unity: RecalculateNormals gives Cross(b - a, c - a) for triangle (a, b, c)
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) >= 0)
        { m.tri.Add(k); m.tri.Add(k + 1); m.tri.Add(k + 2); m.tri.Add(k); m.tri.Add(k + 2); m.tri.Add(k + 3); }
        else
        { m.tri.Add(k); m.tri.Add(k + 2); m.tri.Add(k + 1); m.tri.Add(k); m.tri.Add(k + 3); m.tri.Add(k + 2); }
    }

    // standing spectators: a crowd card from a to b (a at the viewer's left), base height y, h tall, leaning back a little
    // away from the viewer (D points from the viewer into the stand). Same crowd layer and texture as the seated rows.
    static void Standing(Vector3 a, Vector3 b, float y, float h, Vector3 D, int band, int seed)
    {
        Vector3 up = Vector3.up, back = D * (h * 0.12f);
        float len = (b - a).magnitude / (32f * 0.55f), u0 = Rand(seed, 61);
        Color ct = new Color(1f, 1f, 1f) * (0.9f + 0.2f * Rand(seed, 62));
        var keepT = tint; bool keepAo = aoOn;
        tint = ct; aoOn = false;
        Quad(C, a + up * y, b + up * y, b + back + up * (y + h), a + back + up * (y + h), -D + up * 0.2f,
             new Vector4(u0, band * 0.25f + 0.01f, u0 + len, band * 0.25f + 0.24f));
        tint = keepT; aoOn = keepAo;
    }

    // spectators standing behind the catch fence: a cut-out card of real people (Photo/crowd_standing, 2 bands; one
    // tile = 34 people x ~0.52 m = 17.7 m), feet at y, 2.24 m tall (the tile's 256 px), leaning back a touch. Unlit by
    // AO (people, not walls). Band 1 = cheering.
    const float PeopleTile = 17.7f, PeopleH = 2.24f;
    static void Spectators(Vector3 a, Vector3 b, float y, Vector3 D, int band, int seed)
    {
        Vector3 up = Vector3.up, back = D * 0.12f;
        float u0 = Rand(seed, 71), len = (b - a).magnitude / PeopleTile;
        var keepT = tint; bool keepAo = aoOn;
        tint = Color.white * (0.92f + 0.12f * Rand(seed, 72)); aoOn = false;
        Quad(Pp, a + up * y, b + up * y, b + back + up * (y + PeopleH), a + back + up * (y + PeopleH), -D + up * 0.2f,
             new Vector4(u0, band * 0.5f + 0.002f, u0 + len, band * 0.5f + 0.498f));
        tint = keepT; aoOn = keepAo;
    }

    // a flag on a pole, two-sided; cols = 1 or 3 vertical stripes (swatch ids)
    static void Flag(Vector3 c, Vector3 T, Vector3 D, float y, float w, float h, int c0, int c1, int c2)
    {
        Vector3 up = Vector3.up;
        Box(A, c + up * (y + (h + 0.8f) * 0.5f), T, up, D, 0.04f, (h + 0.8f) * 0.5f, 0.04f, TracksideArt.Steel);
        int[] cs = { c0, c1, c2 };
        float sw = w / 3f;
        for (int k = 0; k < 3; k++)
        {
            Vector3 p = c + T * ((k - 1) * sw + w * 0.5f + 0.06f);
            Panel(A, p, -D, sw * 0.5f, y + 0.8f, y + 0.8f + h, TracksideArt.Sw(cs[k]));
            Panel(A, p + D * 0.01f, D, sw * 0.5f, y + 0.8f, y + 0.8f + h, TracksideArt.Sw(cs[k]));
        }
    }

    // a track marshal standing at ground point c beside a post, facing the track (-D), turned a little towards the
    // approaching cars (they come from -T): orange overalls, a white or orange cap, a face in one of three skin tones
    // (PaintWhite tinted through the vertex colour); flag = holds a rolled yellow flag on a short pole. Six boxes,
    // ~60 triangles in the atlas mesh (no draw call); Medium / High.
    static void Marshal(Vector3 c, Vector3 T, Vector3 D, int seed, bool flag)
    {
        Vector3 up = Vector3.up;
        float yaw = -0.15f - 0.45f * Rand(seed, 81);
        Vector3 f = (-D * Mathf.Cos(yaw) - T * Mathf.Sin(yaw)).normalized, s = Vector3.Cross(up, f).normalized;
        var keepT = tint;
        float k = 0.9f + 0.14f * Rand(seed, 82);
        tint = keepT * new Color(k, k, k);
        Box(A, c + up * 0.45f, s, up, f, 0.15f, 0.45f, 0.1f, TracksideArt.MarshalOrange);                 // legs
        Box(A, c + up * 1.16f, s, up, f, 0.2f, 0.27f, 0.12f, TracksideArt.MarshalOrange);                  // torso
        Box(A, c + s * 0.255f + up * 1.12f, s, up, f, 0.05f, 0.28f, 0.06f, TracksideArt.MarshalOrange);    // arms
        Box(A, c - s * 0.255f + up * 1.12f, s, up, f, 0.05f, 0.28f, 0.06f, TracksideArt.MarshalOrange);
        float sk = Rand(seed, 83);
        tint = keepT * (sk < 0.55f ? new Color(0.86f, 0.68f, 0.55f) : sk < 0.85f ? new Color(0.7f, 0.5f, 0.37f) : new Color(0.42f, 0.29f, 0.22f));
        Box(A, c + up * 1.55f, s, up, f, 0.09f, 0.11f, 0.1f, TracksideArt.PaintWhite);                    // head
        tint = keepT;
        Box(A, c + up * 1.68f + f * 0.03f, s, up, f, 0.1f, 0.03f, 0.12f, Rand(seed, 84) < 0.6f ? TracksideArt.PaintWhite : TracksideArt.MarshalOrange); // cap
        if (flag)
        {
            Vector3 h = c + s * 0.3f + f * 0.1f;
            Box(A, h + up * 1.1f, s, up, f, 0.012f, 0.6f, 0.012f, TracksideArt.PostDark);                   // pole
            Box(A, h + up * 1.42f, s, up, f, 0.035f, 0.24f, 0.035f, TracksideArt.PaintYellow);              // rolled flag
        }
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
    // a steel member from a to b with a square section 2r: four sides, no ends (truss chords, webs, rails)
    static void Strut(MB m, Vector3 a, Vector3 b, float r, int sw)
    {
        Vector3 w = b - a; float L = w.magnitude; if (L < 1e-4f) return; w /= L;
        Vector3 u = Vector3.Cross(w, Vector3.up); if (u.sqrMagnitude < 1e-4f) u = Vector3.Cross(w, Vector3.right);
        u.Normalize(); Vector3 v = Vector3.Cross(u, w).normalized, c = (a + b) * 0.5f;
        var rr = TracksideArt.Sw(sw);
        Face(m, c + u * r, u, w, v, L * 0.5f, r, rr); Face(m, c - u * r, -u, w, v, L * 0.5f, r, rr);
        Face(m, c + v * r, v, w, u, L * 0.5f, r, rr); Face(m, c - v * r, -v, w, u, L * 0.5f, r, rr);
    }
    // a slim upright post: front and two sides only (the back faces the scenery), base at b
    static void Post(MB m, Vector3 b, Vector3 front, Vector3 along, float half, float y0, float y1, int sw)
    {
        var r = TracksideArt.Sw(sw); Vector3 f = front * half, s = along * half, up = Vector3.up;
        Quad(m, b + f - s + up * y0, b + f + s + up * y0, b + f + s + up * y1, b + f - s + up * y1, front, r);
        Quad(m, b - f + s + up * y0, b + f + s + up * y0, b + f + s + up * y1, b - f + s + up * y1, along, r);
        Quad(m, b - f - s + up * y0, b + f - s + up * y0, b + f - s + up * y1, b - f - s + up * y1, -along, r);
    }
    // a catch-fence post in the fence layer (F): the fence texture's opaque cable rows (v 0..2/64 of each 1 m tile), so it
    // shares the fence's material, visibility and distance veil
    static readonly Vector4 FenceSolid = new Vector4(0f, 0.006f, 0.03f, 0.025f);
    static void FencePost(Vector3 b, Vector3 front, Vector3 along, float half, float y0, float y1)
    {
        Vector3 f = front * half, s = along * half, up = Vector3.up; var r = FenceSolid;
        Quad(F, b + f - s + up * y0, b + f + s + up * y0, b + f + s + up * y1, b + f - s + up * y1, front, r);
        Quad(F, b - f + s + up * y0, b + f + s + up * y0, b + f + s + up * y1, b - f + s + up * y1, along, r);
        Quad(F, b - f - s + up * y0, b + f - s + up * y0, b + f - s + up * y1, b - f - s + up * y1, -along, r);
    }
    // flat ground decal strip piece (y = h) between lateral lines; r = grime column, v along
    // contact shadow on the ground round a box footprint (centre c, half lengths hl along T and hd along D): four strips
    // of the grime layer's shade column, darkest at the wall, gone w metres out. Medium / High (the decal layer is
    // hidden on Low); the bake's own contact AO only exists at the ground mesh's vertices, metres apart.
    static bool decalsOn;
    static void AoPad(Vector3 c, Vector3 T, Vector3 D, float hl, float hd, float w)
    {
        if (!decalsOn) return;
        T.y = 0f; D.y = 0f; T.Normalize(); D.Normalize();
        for (int k = 0; k < 4; k++)
        {
            Vector3 n = k == 0 ? D : k == 1 ? -D : k == 2 ? T : -T, a = k < 2 ? T : D;
            float dist = k < 2 ? hd : hl, half = (k < 2 ? hl : hd) + w * 0.5f;
            Vector3 p0 = c + n * dist - a * half, p1 = c + n * dist + a * half;
            Decal(G, p0 + n * w, p0, p1 + n * w, p1, 0.05f, 0f, 1f, TracksideArt.GrimeCol(TracksideArt.GrimeShade));
        }
    }
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
            if (WorldBuilder.UnderBridge(p.x, p.z)) return 0f; // under or on a bridge deck: on the track (the bridge has parapets)
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
    // top > 0: the footprint is also a solid that high (m above the ground), see Solids
    static void AddFoot(Vector3 c, Vector3 along, float hl, float hd, float top = 0f)
    {
        var a = new Vector2(along.x, along.z).normalized;
        feet.Add(new Foot { c = new Vector2(c.x, c.z), t = a, hl = hl, hd = hd });
        if (top > 0f) AddSolid(c, along, hl, hd, -1f, top, true);
    }

    // ---------------------------------------------------------------- solids (what a TV camera must not stand in or look through)
    // Oriented boxes round everything that stands beside the track: stands, the pit building, screens, towers, cranes,
    // masts, banners, bridges (here), map buildings and stands (RealWorld). rel = y0 / y1 are above the local ground
    // (Trackside builds at y 0 and is draped later); else absolute. GameManager.BuildTvCams tests its candidate camera
    // positions and their lines of sight to the road against them (and against the model trees' crowns).
    public struct Solid { public float cx, cz, tx, tz, hl, hd, y0, y1; public bool rel; }
    public static readonly List<Solid> Solids = new List<Solid>();
    // TV camera platforms on the scaffold towers (outside of the corners): where a real broadcast camera stands. pos is
    // the lens (y above the local ground, like rel solids), look = the way it faces (back up the road, horizontal);
    // GameManager.BuildTvCams can put its cameras here (they clear the fences and the tower itself)
    public struct TvMount { public Vector3 pos, look; public int idx; }
    public static readonly List<TvMount> TvMounts = new List<TvMount>();
    public static void AddSolid(Vector3 c, Vector3 along, float hl, float hd, float y0, float y1, bool rel)
    {
        var a = new Vector2(along.x, along.z); if (a.sqrMagnitude < 1e-8f) a = Vector2.right; a.Normalize();
        Solids.Add(new Solid { cx = c.x, cz = c.z, tx = a.x, tz = a.y, hl = hl, hd = hd, y0 = y0, y1 = y1, rel = rel });
    }
    // does the segment a-b pass through s (absolute heights) grown by pad on every side? (slab test in the box's frame;
    // a == b tests a point)
    public static bool Hits(in Solid s, Vector3 a, Vector3 b, float pad)
    {
        float ax = a.x - s.cx, az = a.z - s.cz, bx = b.x - s.cx, bz = b.z - s.cz;
        float au = ax * s.tx + az * s.tz, av = -ax * s.tz + az * s.tx, bu = bx * s.tx + bz * s.tz, bv = -bx * s.tz + bz * s.tx;
        float t0 = 0f, t1 = 1f;
        return Slab(au, bu, -s.hl - pad, s.hl + pad, ref t0, ref t1) && Slab(av, bv, -s.hd - pad, s.hd + pad, ref t0, ref t1)
            && Slab(a.y, b.y, s.y0 - pad, s.y1 + pad, ref t0, ref t1);
    }
    static bool Slab(float a, float b, float lo, float hi, ref float t0, ref float t1)
    {
        float d = b - a;
        if (Mathf.Abs(d) < 1e-6f) return a >= lo && a <= hi;
        float u0 = (lo - a) / d, u1 = (hi - a) / d;
        if (u0 > u1) { float tmp = u0; u0 = u1; u1 = tmp; }
        if (u0 > t0) t0 = u0;
        if (u1 < t1) t1 = u1;
        return t0 <= t1;
    }

    // objects that must not go into the light bake's shadow map (see-through fence, ground decals): WorldBuilder parents
    // this holder to the world only after LightBake.AddOccluders
    public static GameObject Late;
    // tall things standing at the barrier line (gantry posts, bridge columns, towers, masts, screens); the TV cameras
    // now test against Solids instead (their volumes, and the lines of sight)
    public static readonly List<Vector3> Tall = new List<Vector3>();
    // pit boxes (one per garage bay): where a car stops in the working lane. Used by PitCrew.cs. Rebuilt with the circuit.
    public struct PitBay { public Vector3 centre, fwd, side; public float garageDist; public int idx; } // side = towards the garage; garageDist = from centre to the garage front
    public static readonly List<PitBay> Bays = new List<PitBay>();
    // the pit lane cars drive (PitLane.cs): entry ramp, fast lane along the garages, exit ramp; null = no pit building
    public static PitLane Lane;
    static Material fenceMat;
    static Material glassMat;
    static readonly List<Renderer> fenceRs = new List<Renderer>(), optional = new List<Renderer>(), postRs = new List<Renderer>();

    static Vector3 P(TrackPoint p, float h = 0f) { return new Vector3((float)p.x, h, (float)p.y); }
    static Vector3 Tan(TrackPoint p) { return new Vector3((float)p.tx, 0f, (float)p.ty).normalized; }
    static Vector3 Nrm(TrackPoint p) { return new Vector3((float)p.nx, 0f, (float)p.ny).normalized; }
    static float Rand(int i, int s) { float x = Mathf.Sin(i * 12.9898f + s * 78.233f) * 43758.5453f; return x - Mathf.Floor(x); }

    // ================================================================ pit lane roads
    // does the pit entry / exit road cross a barrier at offset o (from the centreline) at track point i?
    static bool PitGap(Track t, int i, float o)
    {
        if (Lane == null) return false;
        double u = PitLane.RelS(t, i);
        if (!Lane.OnRamp(u)) return false;
        double L = System.Math.Abs(Lane.BaseLat(u));
        return L - PitLane.RoadIn < o + 0.7 && L + PitLane.RoadOut > o - 0.2;
    }
    // entry and exit roads: pit asphalt from the track's edge (kerb) out to the fast lane along the ramps, a white line
    // on the inner edge; footprints keep the scenery off them
    static void PitRoads(Track t, PitLane lane, float edge)
    {
        float ds = (float)t.ds, step = 3f;
        for (int part = 0; part < 2; part++)
        {
            double u0 = part == 0 ? lane.sEntry : lane.sB, u1 = part == 0 ? lane.sA : lane.sExit;
            int n = Mathf.Max(1, Mathf.CeilToInt((float)(u1 - u0) / step));
            for (int k = 0; k < n; k++)
            {
                double ua = u0 + (u1 - u0) * k / n, ub = u0 + (u1 - u0) * (k + 1) / n;
                float La = Mathf.Abs((float)lane.BaseLat(ua)), Lb = Mathf.Abs((float)lane.BaseLat(ub));
                float ia = Mathf.Max(La - (float)PitLane.RoadIn, edge), ib = Mathf.Max(Lb - (float)PitLane.RoadIn, edge);
                float oa = La + (float)PitLane.RoadOut, ob = Lb + (float)PitLane.RoadOut;
                if (oa - ia < 0.6f && ob - ib < 0.6f) continue;
                oa = Mathf.Max(oa, ia + 0.1f); ob = Mathf.Max(ob, ib + 0.1f);
                int a = lane.TrackIdx(ua), b = lane.TrackIdx(ub);
                SetChunk(a);
                var pa = t.pts[a]; var pb = t.pts[b];
                Vector3 ca = P(pa), cb = P(pb), Da = Nrm(pa) * lane.side, Db = Nrm(pb) * lane.side, up = Vector3.up;
                // positions along the track inside the point spacing (the ramps are sampled finer than the track)
                ca += Tan(pa) * (float)(ua - PitLane.RelS(t, a)); cb += Tan(pb) * (float)(ub - PitLane.RelS(t, b));
                Quad(A, ca + Da * ia + up * 0.03f, cb + Db * ib + up * 0.03f, cb + Db * ob + up * 0.03f, ca + Da * oa + up * 0.03f, up, TracksideArt.Sw(TracksideArt.PitAsphalt));
                Quad(A, ca + Da * ia + up * 0.04f, cb + Db * ib + up * 0.04f, cb + Db * (ib + 0.15f) + up * 0.04f, ca + Da * (ia + 0.15f) + up * 0.04f, up, TracksideArt.Sw(TracksideArt.LineWhite));
                if (k % 4 == 0) AddFoot((ca + cb) * 0.5f + (Da + Db) * 0.25f * (ia + oa), Tan(pa), step * 2f + 1f, (oa - ia) * 0.5f + 0.5f);
            }
        }
    }

    // ================================================================ build
    public static void Build(Track t, Transform root, bool street)
    {
        Bays.Clear(); Lane = null; feet.Clear(); Tall.Clear(); Solids.Clear(); TvMounts.Clear(); fenceRs.Clear(); optional.Clear(); postRs.Clear(); fenceMat = null; Late = null;
        if (t == null || t.N < 8 || root == null) return;
        { var tod = TimeOfDay.For(street); Shader.SetGlobalFloat("_F2Night", tod == TimeOfDay.Kind.Night ? 1f : tod == TimeOfDay.Kind.Evening ? 0.45f : 0f); } // lit windows (Baked)
        int N = t.N; float ds = (float)t.ds;
        float hw = (float)t.width * 0.5f, kerb = WorldBuilder.Kerb;
        float minClear = hw + kerb + 0.5f;
        int gfx = WorldBuilder.Gfx; bool low = gfx <= 1, high = gfx >= 3;
        bool forest = t.def != null && t.def.forest > 0.6;
        bool hills = WorldBuilder.Height != null;
        float skirt = hills ? -1.0f : 0f; // on hills walls reach a little below the street so no gap shows on slopes
        var cutSh = Resources.Load<Shader>("BakedCutout");
        bool fences = !low && WorldBuilder.BakeReady && cutSh != null && cutSh.isSupported;
        bool decals = !low && Resources.Load<Material>("Shadow") != null; decalsOn = decals;
        var grid = new Grid(t, 25f); var big = new Grid(t, 60f);
        atlasL = new Layer(); crowdL = new Layer(); fenceL = new Layer(); grimeL = new Layer(); postL = new Layer(); peopleL = new Layer();
        chunkPts = Mathf.Max(20, Mathf.RoundToInt(250f / ds)); postPts = Mathf.Max(8, Mathf.RoundToInt(40f / ds));
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
        var dirt = new float[2, N]; var fenceZ = new bool[2, N]; var tyreZ = new bool[2, N]; var tecZ = new bool[2, N];
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
                // the chicanes (parkland, radius under ~45 m): Tecpro-style foam blocks instead of the belted tyre wall
                bool tec = !street && curvAbs[apex] >= 1f / 45f;
                for (int j = -before; j <= after; j++) { tyreZ[s, W(apex + j)] = true; tecZ[s, W(apex + j)] = tec; if (!street) fenceZ[s, W(apex + j)] = true; }
            }
        }

        // ---------------------------------------------------------------- pit straight: extent and side
        int pitSide = 0, pitA = 0, pitB = -1; // pit run from track index offset pitA to pitB (relative to 0), on pitSide
        bool monza = t.def != null && t.def.id == "monza";
        float laneW = 12f, garD = 15f, garH = monza ? 10.5f : 9f, bay = 8f; // (Monza: the taller pit building with the hospitality floor)
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
        // catch fences (parkland): Monza has them along nearly the whole lap (straights, Curva Grande, Lesmo, Ascari,
        // Parabolica); elsewhere along the straights
        if (!street && !low)
        {
            int kw = Mathf.Max(2, Mathf.RoundToInt(30f / ds));
            for (int i = 0; i < N; i++)
            {
                float kk = 0f; for (int j = -kw; j <= kw; j += 2) kk = Mathf.Max(kk, curvAbs[W(i + j)]);
                if (monza || kk < 1f / 600f) { fenceZ[0, i] = true; fenceZ[1, i] = true; }
            }
        }

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
                // the pit box: a white rectangle on the working lane where the car stops (centre 8.6 m out from the wall lane edge)
                {
                    float boxLat = laneIn + 8.6f; Vector3 mid = (ca + cb) * 0.5f + D * boxLat;
                    float lw = 0.1f, hf = 3.0f, hs = 1.45f;
                    void Strip(float g0, float g1, float h0, float h1) { Vector3 bq = mid + up * 0.045f; Quad(A, bq + T * g0 + D * h0, bq + T * g1 + D * h0, bq + T * g1 + D * h1, bq + T * g0 + D * h1, up, TracksideArt.Sw(TracksideArt.LineWhite)); }
                    Strip(-hf, hf, -hs, -hs + lw); Strip(-hf, hf, hs - lw, hs); Strip(-hf, -hf + lw, -hs, hs); Strip(hf - lw, hf, -hs, hs);
                    Strip(-0.05f, 0.05f, -hs - 0.5f, -hs); // nose mark: where the front axle stops
                    Bays.Add(new PitBay { centre = new Vector3(mid.x, 0, mid.z), fwd = T, side = D, garageDist = laneOut - boxLat, idx = ia });
                }
                // pit-lane markings: a white line along the wall side of the fast lane; the speed-limit lines across the lane
                // at both ends of the pits, "SPEED LIMIT 80" painted (elongated, for the driver) before the entry line
                Quad(A, ca + Da * (laneIn + 0.25f) + up * 0.04f, cb + Db * (laneIn + 0.25f) + up * 0.04f, cb + Db * (laneIn + 0.37f) + up * 0.04f, ca + Da * (laneIn + 0.37f) + up * 0.04f, up, TracksideArt.Sw(TracksideArt.LineWhite));
                if (b == 0 || b == bays - 1)
                {
                    Vector3 e0 = b == 0 ? ca : cb, eD = b == 0 ? Da : Db, eT = T * (b == 0 ? 1f : -1f);
                    Quad(A, e0 + eD * laneIn + up * 0.042f, e0 + eD * laneIn + eT * 0.35f + up * 0.042f, e0 + eD * laneOut + eT * 0.35f + up * 0.042f, e0 + eD * laneOut + up * 0.042f, up, TracksideArt.Sw(TracksideArt.LineWhite));
                    if (b == 0 && !low)
                    {
                        Vector3 R = Vector3.Cross(up, T).normalized, lc = ca + Da * (laneIn + 3.25f) - T * 2.2f + up * 0.043f;
                        Quad(A, lc - R * 3.1f - T * 1.3f, lc + R * 3.1f - T * 1.3f, lc + R * 3.1f + T * 1.3f, lc - R * 3.1f + T * 1.3f, up, TracksideArt.PitText);
                    }
                }
                // garage front: door below, glass above, parapet with an advertiser on top
                Vector3 fa = ca + Da * laneOut, fb = cb + Db * laneOut, ba = ca + Da * backL, bk = cb + Db * backL;
                Vector3 fwdFace = -D;
                bool aFirst = Vector3.Dot(Vector3.Cross(fwdFace, up), fb - fa) > 0; // a on the viewer's left?
                Vector3 L0 = aFirst ? fa : fb, R0 = aFirst ? fb : fa;
                if (skirt < 0f) Wall(A, L0, R0, skirt, 0.04f, fwdFace, TracksideArt.Sw(TracksideArt.BuildGrey)); // open front: the garage inside is PitGarage.cs
                // above the garages: a lintel, a glass curtain wall, a balcony slab cantilevered over the lane with a glass
                // balustrade, the hospitality floor set back behind it, and a deep roof slab on slim columns whose fascia
                // carries the circuit's name and a few advertisers (not a billboard on every bay)
                float roofY = garH + 1.6f, y1 = 4.5f, fascia = 0.9f, y2 = y1 + (roofY - fascia - y1) * 0.5f;
                const float setB = 1.4f, balc = 1.1f, eave = 1.7f;
                Vector3 oB = -D * balc, oS = D * setB, oE = -D * eave;
                Wall(A, L0, R0, y1, y1 + 0.5f, fwdFace, TracksideArt.Sw(TracksideArt.BuildWhite));
                aoOn = false;
                Wall(A, L0, R0, y1 + 0.5f, y2, fwdFace, TracksideArt.GlassWall);
                Wall(A, L0 + oS, R0 + oS, y2 + 0.3f, roofY - fascia, fwdFace, TracksideArt.GlassWall);
                Pieces(A, L0 + oB, R0 + oB, y2 + 0.3f, y2 + 1.35f, fwdFace, TracksideArt.Balustrade, 2f);
                aoOn = true;
                Wall(A, L0 + oB, R0 + oB, y2, y2 + 0.3f, fwdFace, TracksideArt.Sw(TracksideArt.BuildWhite));
                Quad(A, fa + oB + up * y2, fb + oB + up * y2, fb + up * y2, fa + up * y2, -up, TracksideArt.Sw(TracksideArt.RoofUnder));
                Quad(A, fa + oB + up * (y2 + 0.3f), fb + oB + up * (y2 + 0.3f), fb + oS + up * (y2 + 0.3f), fa + oS + up * (y2 + 0.3f), up, TracksideArt.Sw(TracksideArt.Concrete));
                // white fin between the bays (first floor) and the slim roof column on the balcony edge
                Box(A, fa - D * 0.16f + up * ((y1 + y2) * 0.5f), T, up, D, 0.16f, (y2 - y1) * 0.5f, 0.16f, TracksideArt.BuildWhite);
                Box(A, fa - D * (balc - 0.12f) + up * ((y2 + 0.3f + roofY - fascia) * 0.5f), T, up, D, 0.08f, (roofY - fascia - y2 - 0.3f) * 0.5f, 0.08f, TracksideArt.Steel);
                // roof slab: fascia, soffit
                aoOn = false;
                Wall(A, L0 + oE, R0 + oE, roofY - fascia, roofY, fwdFace,
                     monza && b % 6 == 3 ? TracksideArt.NameSign : b % 6 == 0 ? TracksideArt.Ad(b / 6 * 5 + 2) : TracksideArt.Sw(TracksideArt.RoofWhite));
                aoOn = true;
                // soffit: cool-grey composite panels (three per bay) with two downlights, not one flat brown slab
                tint = new Color(0.84f, 0.87f, 0.92f);
                for (int q = 0; q < 3; q++)
                {
                    Vector3 sa0 = Vector3.Lerp(fa, fb, q / 3f), sb0 = Vector3.Lerp(fa, fb, (q + 1) / 3f), yS = up * (roofY - fascia);
                    Quad(A, sa0 + oE + yS, sb0 + oE + yS, sb0 + oS + yS, sa0 + oS + yS, -up, TracksideArt.PanelWall);
                }
                tint = Color.white;
                if (!low)
                {
                    aoOn = false;
                    for (int q = 0; q < 2; q++)
                    {
                        Vector3 lc = Vector3.Lerp(fa, fb, 0.25f + 0.5f * q) - D * 0.2f + up * (roofY - fascia - 0.02f);
                        Quad(A, lc - T * 0.3f - D * 0.12f, lc + T * 0.3f - D * 0.12f, lc + T * 0.3f + D * 0.12f, lc - T * 0.3f + D * 0.12f, -up, TracksideArt.Sw(TracksideArt.PaintWhite));
                    }
                    aoOn = true;
                }
                // guests on the balcony
                if (!low && Rand(b, 91) < 0.45f)
                    Standing(L0 + oB * 0.45f + (R0 - L0) * 0.08f, R0 + oB * 0.45f - (R0 - L0) * 0.08f, y2 + 0.3f, 1.7f, D, Rand(b, 92) < 0.5f ? 0 : 1, 9100 + b);
                // back, roof
                tint = new Color(0.94f, 0.96f, 1f);
                Pieces(A, ba, bk, skirt, garH + 1.6f, D, TracksideArt.Cladding, 4f);
                tint = Color.white;
                Quad(A, fa + oE + up * roofY, fb + oE + up * roofY, bk + up * roofY, ba + up * roofY, up, TracksideArt.Sw(TracksideArt.RoofUnder));
                // roof-top: a glazed penthouse at both ends (race control, the stair heads), louvred plant in between
                if (b == 0 || b == bays - 1 || b % 5 == 2)
                {
                    bool pent = b == 0 || b == bays - 1;
                    Vector3 rc = (fa + fb + ba + bk) * 0.25f + D * (pent ? -1.5f : 2f);
                    bool rcT = b == 0 && !low; // race control: two glazed storeys, a sign to the track
                    float ph = rcT ? 3.0f : pent ? 1.7f : 0.9f, pl = pent ? bay * 0.5f - 0.5f : 2.2f, pd = pent ? 3.6f : 1.6f;
                    Box(A, rc + up * (roofY + ph), T, up, D, pl, ph, pd, pent ? TracksideArt.BuildWhite : TracksideArt.BuildGrey);
                    aoOn = false;
                    if (rcT)
                    {
                        // glazed on the three sides that see the track, a slab line between the floors, the sign over them
                        for (int f2 = 0; f2 < 2; f2++)
                        {
                            float g0 = roofY + 0.5f + f2 * 2.9f, g1 = g0 + 2.1f;
                            Panel(A, rc - D * (pd + 0.02f), -D, pl - 0.25f, g0, g1, TracksideArt.GlassWall);
                            Panel(A, rc + T * (pl + 0.02f), T, pd - 0.3f, g0, g1, TracksideArt.GlassWall);
                            Panel(A, rc - T * (pl + 0.02f), -T, pd - 0.3f, g0, g1, TracksideArt.GlassWall);
                        }
                        Panel(A, rc - D * (pd + 0.04f), -D, pl - 0.25f, roofY + 2f * ph - 0.5f, roofY + 2f * ph - 0.05f, TracksideArt.RaceControl);
                    }
                    else if (pent) Panel(A, rc - D * (pd + 0.02f), -D, pl - 0.3f, roofY + 0.9f, roofY + 2.9f, TracksideArt.GlassWall);
                    else Panel(A, rc - D * (pd + 0.02f), -D, pl - 0.15f, roofY + 0.15f, roofY + 1.65f, TracksideArt.Louvre);
                    aoOn = true;
                    if (pent) Box(A, rc + up * (roofY + 2f * ph + 0.12f), T, up, D, pl + 0.5f, 0.12f, pd + 0.6f, TracksideArt.RoofWhite, true);
                    if (rcT) AddSolid(rc, T, pl + 0.6f, pd + 0.6f, roofY, roofY + 2f * ph + 2f, true);
                    if (rcT) { Box(A, rc + up * (roofY + 2f * ph + 1.0f), T, up, D, 0.05f, 0.9f, 0.05f, TracksideArt.Steel); Box(A, rc + T * (pl - 0.6f) + up * (roofY + 2f * ph + 0.6f), T, up, D, 0.6f, 0.5f, 0.6f, TracksideArt.BuildGrey); }
                }
                // ends of the building
                if (b == 0) Pieces(A, fa, ba, skirt, garH + 1.6f, -T, TracksideArt.PanelWall, 4f);
                if (b == bays - 1) Pieces(A, fb, bk, skirt, garH + 1.6f, T, TracksideArt.PanelWall, 4f);
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
                // (the team pit-wall stands are PitGarage.cs)
                AddFoot((ca + cb) * 0.5f + D * ((laneIn + backL) * 0.5f), T, bay * 0.5f + 0.5f, (backL - laneIn) * 0.5f + 0.5f);
                AddSolid((ca + cb) * 0.5f + D * ((laneOut + backL) * 0.5f), T, bay * 0.5f + 0.3f, (backL - laneOut) * 0.5f + 1.5f, -1f, garH + 5.5f, true); // the building
                AddSolid((ca + cb) * 0.5f + D * (laneOut - 1.75f), T, bay * 0.5f + 0.3f, 1.9f, 4.3f, 5.1f, true); // canopy over the working lane
            }
            // pit-wall gantry before the line: a steel portal on the pit wall carrying the timing screens, one to the
            // pit lane and one to the track (lap count / order), with an advertiser along the beam
            if (!low)
            {
                int ig = W(Mathf.RoundToInt(-40f / ds));
                if (InPit(ig, sg))
                {
                    SetChunk(ig);
                    var p = t.pts[ig]; Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up;
                    Vector3 c = P(p) + D * (Off(ig, sg) + 0.25f);
                    const float hlG = 4.2f, yb = 5.4f;
                    for (int e = -1; e <= 1; e += 2)
                    {
                        Box(A, c + T * (e * hlG) + up * ((1.15f + yb) * 0.5f), T, up, D, 0.13f, (yb - 1.15f) * 0.5f, 0.13f, TracksideArt.GantryDark);
                        Box(A, c + T * (e * hlG) + up * 1.2f, T, up, D, 0.25f, 0.05f, 0.25f, TracksideArt.Steel);
                    }
                    Box(A, c + up * (yb + 0.25f), T, up, D, hlG + 0.3f, 0.25f, 0.2f, TracksideArt.GantryDark, true);
                    Box(A, c + up * 4.3f, T, up, D, hlG - 0.9f, 0.95f, 0.16f, TracksideArt.GantryDark, true);
                    aoOn = false;
                    Panel(A, c - D * 0.18f, -D, hlG - 1.05f, 3.5f, 5.1f, TracksideArt.Screen);
                    Panel(A, c + D * 0.18f, D, hlG - 1.05f, 3.5f, 5.1f, TracksideArt.Screen);
                    Panel(A, c - D * 0.21f, -D, hlG + 0.3f, yb + 0.02f, yb + 0.48f, TracksideArt.Ad(5));
                    Panel(A, c + D * 0.21f, D, hlG + 0.3f, yb + 0.02f, yb + 0.48f, TracksideArt.Ad(12));
                    aoOn = true;
                    AddSolid(c, T, hlG + 0.6f, 0.6f, 1f, yb + 0.6f, true); Tall.Add(c);
                }
            }
            // Monza: the podium cantilevered from the pit building's roof out over the pit lane, the pit wall and part of
            // the track, just past the line: slab with an advertiser fascia and glass balustrade, the three steps, a light roof
            if (monza && !low)
            {
                int ip = W(Mathf.RoundToInt(28f / ds));
                if (InPit(ip, sg))
                {
                    SetChunk(ip);
                    var p = t.pts[ip]; Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up, c = P(p);
                    float y = garH + 2.4f, latIn = hw - 3.5f, latOut = laneOut + 3f, hlP = 9f, hd = (latOut - latIn) * 0.5f;
                    Vector3 mid = c + D * ((latIn + latOut) * 0.5f), front = c + D * latIn;
                    Box(A, mid + up * y, T, up, D, hlP, 0.45f, hd, TracksideArt.BuildWhite, true);
                    for (int s2 = -1; s2 <= 1; s2 += 2) Box(A, mid + T * (s2 * (hlP - 1.2f)) + up * (y - 0.9f), T, up, D, 0.3f, 0.45f, hd, TracksideArt.Steel, true);
                    Panel(A, front - D * 0.02f, -D, hlP, y - 0.45f, y + 0.45f, TracksideArt.Ad(7));
                    Panel(A, front + D * 0.15f, -D, hlP, y + 0.45f, y + 1.5f, TracksideArt.Sw(TracksideArt.GlassDark));
                    float[] stepH = { 0.9f, 0.6f, 0.45f }, stepAt = { 0f, -1.9f, 1.9f };
                    for (int q = 0; q < 3; q++)
                        Box(A, front + D * 3.5f + T * stepAt[q] + up * (y + 0.45f + stepH[q] * 0.5f), T, up, D, 0.9f, stepH[q] * 0.5f, 0.8f, TracksideArt.PaintWhite);
                    Box(A, mid + up * (y + 4.6f), T, up, D, hlP + 0.5f, 0.15f, hd + 0.5f, TracksideArt.RoofWhite, true);
                    // the slab seen from below (the chase and cockpit cameras pass right under it): cool-grey composite
                    // panels in a ~3 m grid, four steel beams across, rows of downlights; a dark shadow gap and a white
                    // trim round the advertiser fascia, sponsor boards on both ends
                    {
                        float yu = y - 0.47f;
                        int nu = Mathf.Max(1, Mathf.RoundToInt(2f * hlP / 3f)), nv = Mathf.Max(1, Mathf.RoundToInt(2f * hd / 3f));
                        tint = new Color(0.82f, 0.85f, 0.90f);
                        for (int iu = 0; iu < nu; iu++)
                            for (int iv = 0; iv < nv; iv++)
                            {
                                Vector3 q0 = mid + T * (-hlP + 2f * hlP * iu / nu) + D * (-hd + 2f * hd * iv / nv) + up * yu;
                                Vector3 du = T * (2f * hlP / nu), dv = D * (2f * hd / nv);
                                Quad(A, q0, q0 + du, q0 + du + dv, q0 + dv, -up, TracksideArt.PanelWall);
                            }
                        tint = Color.white;
                        for (int k = 0; k < 4; k++)
                            Box(A, mid + T * (-hlP + 2f * hlP * (k + 0.5f) / 4f) + up * (yu - 0.25f), T, up, D, 0.16f, 0.25f, hd - 0.15f, TracksideArt.Steel, true);
                        aoOn = false;
                        for (int k = 0; k < 4; k++)
                            for (int r = 0; r < Mathf.Max(1, Mathf.RoundToInt(2f * hd / 2.6f)); r++)
                            {
                                float al = -hlP + 2f * hlP * (k + 0.5f) / 4f + hlP / 4f, ac = -hd + 1.3f + 2.6f * r;
                                if (al > hlP - 0.6f || ac > hd - 0.5f) continue;
                                Vector3 lc = mid + T * al + D * ac + up * (yu - 0.01f);
                                Quad(A, lc - T * 0.22f - D * 0.22f, lc + T * 0.22f - D * 0.22f, lc + T * 0.22f + D * 0.22f, lc - T * 0.22f + D * 0.22f, -up, TracksideArt.Sw(TracksideArt.PaintWhite));
                            }
                        Panel(A, front - D * 0.03f, -D, hlP + 0.02f, y - 0.62f, y - 0.46f, TracksideArt.Sw(TracksideArt.GantryDark));
                        Panel(A, front - D * 0.035f, -D, hlP + 0.02f, y + 0.41f, y + 0.5f, TracksideArt.Sw(TracksideArt.PaintWhite));
                        Panel(A, mid + T * (hlP + 0.03f), T, hd - 0.2f, y - 0.42f, y + 0.42f, TracksideArt.Ad(3));
                        Panel(A, mid - T * (hlP + 0.03f), -T, hd - 0.2f, y - 0.42f, y + 0.42f, TracksideArt.Ad(10));
                        aoOn = true;
                    }
                    AddSolid(mid, T, hlP + 0.6f, hd + 0.6f, y - 1f, y + 4.9f, true);
                    // spectators on the podium slab behind the balustrade, and along the pit-building roof terrace
                    Standing(front + D * 0.7f - T * (hlP - 0.5f), front + D * 0.7f + T * (hlP - 0.5f), y + 0.45f, 1.7f, D, 1, 4242);
                    Standing(front + D * 1.9f - T * (hlP - 1.5f), front + D * 1.9f + T * (hlP - 1.5f), y + 0.45f, 1.7f, D, 0, 4243);
                    for (int s2 = -1; s2 <= 1; s2 += 2) for (int s3 = -1; s3 <= 1; s3 += 2)
                        Box(A, mid + T * (s2 * hlP) + D * (s3 * hd) + up * (y + 2.5f), T, up, D, 0.1f, 2.05f, 0.1f, TracksideArt.Steel);
                }
            }
            // pit-building roof terrace: guests standing along the front edge of the roof above the garages
            if (!low && pitB >= pitA && sg == pitSide)
            {
                float step = 9f;
                int nT = Mathf.FloorToInt((pitB - pitA) * ds / step);
                for (int k = 0; k < nT; k++)
                {
                    float sc = pitA * ds + (k + 0.5f) * step;
                    if (monza && Mathf.Abs(sc - 28f) < 13f) continue;
                    if (Rand(k, 71) < 0.25f) continue;
                    int i = W(Mathf.RoundToInt(sc / ds)); var p = t.pts[i];
                    Vector3 T = Tan(p), D = Nrm(p) * sg, c = P(p) + D * (laneOut + 1.6f);
                    SetChunk(i);
                    Standing(c - T * (step * 0.45f), c + T * (step * 0.45f), garH + 1.6f, 1.7f, D, Rand(k, 72) < 0.5f ? 0 : 1, 7000 + k);
                }
            }
            // the pit lane path (PitLane.cs): the fast lane's centre, every box by its track distance and offset
            if (Bays.Count > 0)
            {
                var pitBu = new List<double>(); var pitBl = new List<double>();
                foreach (var pitBay in Bays)
                {
                    var pitNear = t.Nearest(pitBay.centre.x, pitBay.centre.z); var pitTp = t.pts[pitNear.idx];
                    pitBu.Add(PitLane.RelS(t, pitNear.idx) + (pitBay.centre.x - pitTp.x) * pitTp.tx + (pitBay.centre.z - pitTp.y) * pitTp.ty); pitBl.Add(pitNear.lateral);
                }
                Lane = new PitLane(t, sg, pitA * ds, pitB * ds, laneIn + 3.25, pitBu, pitBl);
                PitRoads(t, Lane, hw + kerb + 0.3f);
                PitGarage.Build(N, root, gfx); // garage interiors, gantries, pit-wall stands (outside the world root; see PitGarage.cs)
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
                        AddFoot(c, T, 7f, 1.6f, 3.9f); AoPad(c, T, D, 6.5f, 1.3f, 1.2f);
                    }
            }
        }

        // ---------------------------------------------------------------- grandstands
        const float RowD = 0.85f, RowH = 0.45f;
        // seat-block colours (team colours): the atlas swatch for the tread and the same colour for the empty seats in the
        // crowd texture (uv1, linear, relative to the texture's light grey seats: ~0.56 linear)
        int[] teamSeat = { TracksideArt.SeatRed, TracksideArt.SeatBlue, TracksideArt.PaintYellow, TracksideArt.SeatGreen,
                           TracksideArt.MarshalOrange, TracksideArt.StripeTeal, TracksideArt.SeatGrey, TracksideArt.PaintWhite };
        Color32[] teamSrgb = { new Color32(170, 34, 38, 255), new Color32(34, 68, 148, 255), new Color32(228, 186, 30, 255), new Color32(30, 120, 64, 255),
                               new Color32(238, 118, 22, 255), new Color32(20, 140, 140, 255), new Color32(138, 138, 143, 255), new Color32(236, 236, 230, 255) };
        var teamSeatC = new Color[teamSeat.Length];
        for (int k = 0; k < teamSeat.Length; k++) { Color lc = ((Color)teamSrgb[k]).linear; teamSeatC[k] = new Color(Mathf.Min(1.6f, lc.r / 0.56f), Mathf.Min(1.6f, lc.g / 0.56f), Mathf.Min(1.6f, lc.b / 0.56f), 1f); }
        Color[] crowdTints = { new Color(1f, 1f, 1f), new Color(1.08f, 0.94f, 0.82f), new Color(0.9f, 0.96f, 1.08f), new Color(1.05f, 1.02f, 0.84f), new Color(0.86f, 0.86f, 0.88f) };
        Color[] standClad = { new Color(0.50f, 0.58f, 0.74f), new Color(0.72f, 0.73f, 0.76f), new Color(0.46f, 0.62f, 0.52f), new Color(0.78f, 0.56f, 0.48f) };
        int standNo = 0, screenNo = 0; bool endScreens = true; // (false: stands in a row, no screen between them)
        // a big screen on two lattice legs behind the barrier at track point qi, side qs, "behind" m beyond the barrier
        // line and "along" m down the track, facing the track
        bool BigScreen(int qi, int qs, float behind, float along)
        {
            int qk = W(qi + Mathf.RoundToInt(along / ds));
            if (InPit(qk, qs) || curvAbs[qk] > 1f / 60f) return false;
            var qp = t.pts[qk];
            Vector3 qT = Tan(qp), qD = Nrm(qp) * qs, qu = Vector3.up;
            float lat = Off(qk, qs) + behind;
            Vector3 qc = P(qp) + qD * lat;
            if (!Free(qc, lat - 1.5f) || !Free(qc + qT * 4.5f, lat - 1.5f) || !Free(qc - qT * 4.5f, lat - 1.5f)) return false;
            SetChunk(qk);
            const float y0 = 3.4f, y1 = 8.2f, hwS = 4.6f;
            for (int e = -1; e <= 1; e += 2)
            {
                Vector3 leg = qc + qT * (e * 3.2f) + qD * 0.45f;
                Pieces(A, leg - qT * 0.35f - qD * 0.31f, leg + qT * 0.35f - qD * 0.31f, skirt, y0, -qD, TracksideArt.ScaffoldR, 2f);
                Box(A, leg + qu * (y0 * 0.5f + skirt * 0.5f), qT, qu, qD, 0.35f, y0 * 0.5f - skirt * 0.5f, 0.3f, TracksideArt.Scaffold);
            }
            Box(A, qc + qD * 0.35f + qu * ((y0 + y1) * 0.5f), qT, qu, qD, hwS, (y1 - y0) * 0.5f, 0.35f, TracksideArt.GantryDark, true);
            aoOn = false;
            Panel(A, qc - qD * 0.02f, -qD, hwS - 0.15f, y0 + 0.15f, y1 - 0.15f, TracksideArt.Screen);
            aoOn = true;
            Panel(A, qc - qD * 0.03f, -qD, hwS, y1, y1 + 0.9f, TracksideArt.Ad(BoldAds[screenNo % BoldAds.Length]));
            screenNo++;
            AddFoot(qc + qD * 0.4f, qT, hwS + 0.5f, 1.2f, y1 + 1.0f); Tall.Add(qc);
            return true;
        }
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
                float topY = baseH + (rows - 1) * RowH;
                float backTop = roof ? topY + 4.4f : topY + 1.1f;
                // each stand has its own cladding colour (tint over the ribbed steel): blue, grey, green, terracotta
                Color cladC = street ? new Color(0.4f, 0.41f, 0.44f) : standClad[id % standClad.Length];
                // front: a low poured-concrete wall, the advertisers' band on top of it
                int panels = Mathf.Max(1, Mathf.RoundToInt(halfL * 2f / 8f));
                float pw = halfL * 2f / panels;
                for (int k = 0; k < panels; k++)
                {
                    Vector3 c = O + T * (-halfL + (k + 0.5f) * pw);
                    tint = new Color(0.86f, 0.86f, 0.86f);
                    Panel(A, c, -D, pw * 0.5f, skirt, baseH - 0.9f, TracksideArt.ConcreteWall);
                    tint = Color.white;
                    Panel(A, c, -D, pw * 0.5f, baseH - 0.9f, baseH, TracksideArt.Ad(id * 3 + k));
                }
                // steel columns and a beam along the front of the stand
                for (int k = 0; k <= panels; k++)
                    Box(A, O + T * (-halfL + k * pw) - D * 0.12f + up * (baseH * 0.5f + skirt * 0.5f), T, up, D, 0.1f, baseH * 0.5f - skirt * 0.5f, 0.1f, TracksideArt.GantryDark);
                Box(A, O - D * 0.12f + up * (baseH + 0.05f), T, up, D, halfL, 0.08f, 0.12f, TracksideArt.GantryDark);
                // rows: riser, concrete tread, then per section (between two stair aisles) the seats and the crowd on them
                int secs = Mathf.Max(1, Mathf.RoundToInt(halfL * 2f / 9f));
                float sl = halfL * 2f / secs; const float aisle = 0.55f;
                // seat blocks in team colours: one colour per section, the upper tier another (Monza: mostly red)
                int split = rows >= 9 ? rows / 2 + 1 : rows;
                int Blk(int s, int tier)
                {
                    int hb = id * 131 + s * 7 + tier * 3;
                    if (monza && Rand(hb, 41) < 0.42f) return 0;
                    return (int)(Rand(hb, 42) * teamSeat.Length) % teamSeat.Length;
                }
                for (int r = 0; r < rows; r++)
                {
                    float y = baseH + r * RowH, d0 = r * RowD, d1 = d0 + RowD;
                    // ambient occlusion under the roof: the further back (and the lower the sky seen past the roof's
                    // edge), the darker the rows, the seats and the people on them
                    float fr0 = rows > 1 ? (float)r / (rows - 1) : 0f, rowAo = roof ? Mathf.Lerp(0.92f, 0.5f, fr0 * fr0 * (3f - 2f * fr0)) : 1f;
                    Color rowT = new Color(rowAo, rowAo, rowAo * 1.03f);
                    tint = rowT;
                    Vector3 l0 = O - T * halfL + D * d0, r0 = O + T * halfL + D * d0, l1 = O - T * halfL + D * d1, r1 = O + T * halfL + D * d1;
                    if (r > 0) Quad(A, l0 + up * (y - RowH), r0 + up * (y - RowH), r0 + up * y, l0 + up * y, -D, TracksideArt.Sw(TracksideArt.ConcreteDark));
                    Quad(A, l0 + up * y, r0 + up * y, r1 + up * y, l1 + up * y, up, TracksideArt.Sw(TracksideArt.Concrete)); // the aisles' steps show between the seat blocks
                    // stair aisles: a yellow nosing on every step, and a handrail down the middle (Medium / High)
                    if (!low)
                        for (int s = 0; s <= secs; s++)
                        {
                            float xa = -halfL + s * sl;
                            if (s == 0 || s == secs) continue;
                            Vector3 nc = O + T * xa + D * (d0 + 0.03f) + up * (y + 0.008f);
                            Quad(A, nc - T * 0.45f, nc + T * 0.45f, nc + T * 0.45f + D * 0.06f, nc - T * 0.45f + D * 0.06f, up, TracksideArt.Sw(TracksideArt.PaintYellow));
                            if (r == 0) Strut(A, O + T * xa + up * (baseH + 0.95f), O + T * xa + D * (depth - 0.4f) + up * (baseH + (rows - 1) * RowH + 0.95f), 0.025f, TracksideArt.Steel);
                            if ((r & 3) == 1) Box(A, O + T * xa + D * (d0 + 0.4f) + up * (y + 0.48f), T, up, D, 0.025f, 0.48f, 0.025f, TracksideArt.Steel);
                        }
                    for (int s = 0; s < secs; s++)
                    {
                        float x0 = -halfL + s * sl + aisle, x1 = -halfL + (s + 1) * sl - aisle, wSec = x1 - x0;
                        // team-coloured seat blocks
                        Vector3 ta = O + T * x0 + D * d0, tb = O + T * x1 + D * d0;
                        int blk = Blk(s, r < split ? 0 : 1);
                        Quad(A, ta + up * (y + 0.012f), tb + up * (y + 0.012f), tb + D * RowD + up * (y + 0.012f), ta + D * RowD + up * (y + 0.012f), up, TracksideArt.Sw(teamSeat[blk]));
                        int h = id * 997 + r * 31 + s;
                        int band = Rand(h, 1) < 0.78f ? (Rand(h, 2) < (monza ? 0.35f : 0.5f) ? 0 : 1) : (Rand(h, 3) < 0.7f ? 2 : 3);
                        float u0 = Rand(h, 4), len = wSec / (32f * 0.55f);
                        Color ct = crowdTints[(int)(Rand(h, 5) * crowdTints.Length) % crowdTints.Length] * (0.9f + 0.2f * Rand(h, 6)) * rowAo;
                        var tc = teamSeatC[blk]; seatTint = new Vector4(tc.r, tc.g, tc.b, 1f);
                        tint = ct; aoOn = false;
                        Vector3 sa = ta + D * 0.2f, sb = tb + D * 0.2f;
                        Vector3 back = D * 0.3f;
                        Quad(C, sa + up * y, sb + up * y, sb + back + up * (y + 1.0f), sa + back + up * (y + 1.0f), -D + up * 0.3f,
                             new Vector4(u0, band * 0.25f + 0.01f, u0 + len, band * 0.25f + 0.24f));
                        seatTint = new Vector4(0.36f, 0.37f, 0.40f, 1f);
                        tint = rowT; aoOn = true;
                        // fans standing at the front rail
                        if (r == 0 && Rand(h, 7) < 0.45f)
                            Standing(O + T * (x0 + wSec * 0.1f) - D * 0.15f, O + T * (x1 - wSec * 0.1f) - D * 0.15f, baseH, 1.5f, D, Rand(h, 8) < 0.5f ? 0 : 1, h);
                    }
                    tint = Color.white;
                    // stepped infill under the rows at both ends, in the stand's cladding
                    for (int e = -1; e <= 1; e += 2)
                    {
                        Vector3 en = T * e, ea = O + T * (e * halfL) + D * d0, eb = O + T * (e * halfL) + D * d1;
                        tint = cladC;
                        Wall(A, ea, eb, skirt, y, en, TracksideArt.Sub(TracksideArt.Cladding, 0f, 0.25f, 0f, 1f));
                        tint = Color.white;
                    }
                }
                // vomitories: concrete "dog houses" on the rake at every other aisle, their dark mouths facing the track
                if (rows >= 9)
                    for (int s = 1; s < secs; s += 2)
                    {
                        int vr = rows / 2 - 1;
                        Vector3 vc = O + T * (-halfL + s * sl) + D * (vr * RowD + 1.0f);
                        float vy = baseH + vr * RowH;
                        Box(A, vc + up * (vy + 1.15f), T, up, D, 1.45f, 1.15f, 1.0f, TracksideArt.Concrete);
                        aoOn = false;
                        Panel(A, vc - D * 1.02f, -D, 1.45f, vy, vy + 2.3f, TracksideArt.Vomitory);
                        aoOn = true;
                    }
                // ends: a white raker beam along the rake and a glazed wind screen above it (seen from both sides)
                for (int e = -1; e <= 1; e += 2)
                {
                    Vector3 en = T * e, ef = O + T * (e * (halfL + 0.03f)), eb = ef + D * depth;
                    float yf = baseH - 0.1f, yb = topY + RowH - 0.1f;
                    Quad(A, ef + up * (yf - 0.32f), eb + up * (yb - 0.32f), eb + up * yb, ef + up * yf, en, TracksideArt.Sw(TracksideArt.GantryWhite));
                    int gp = Mathf.Max(1, Mathf.RoundToInt(depth / 2.2f));
                    aoOn = false;
                    for (int k = 0; k < gp; k++)
                    {
                        float f0 = (float)k / gp, f1 = (float)(k + 1) / gp;
                        Vector3 g0 = Vector3.Lerp(ef, eb, f0) + up * Mathf.Lerp(yf, yb, f0), g1 = Vector3.Lerp(ef, eb, f1) + up * Mathf.Lerp(yf, yb, f1);
                        Quad(A, g0, g1, g1 + up * 1.5f, g0 + up * 1.5f, en, TracksideArt.Balustrade);
                        Quad(A, g0 - en * 0.03f, g1 - en * 0.03f, g1 - en * 0.03f + up * 1.5f, g0 - en * 0.03f + up * 1.5f, -en, TracksideArt.Balustrade);
                    }
                    aoOn = true;
                }
                // standing row along the top rail, and a few big flags
                {
                    int topSecs = Mathf.Max(1, Mathf.RoundToInt(halfL * 2f / 8f));
                    float tl = halfL * 2f / topSecs;
                    for (int s = 0; s < topSecs; s++)
                        if (Rand(id * 53 + s, 9) < 0.8f)
                            Standing(O + T * (-halfL + s * tl) + D * (depth - 0.5f), O + T * (-halfL + (s + 1) * tl) + D * (depth - 0.5f), topY, 1.7f, D, Rand(id * 53 + s, 10) < 0.6f ? 1 : 0, id * 53 + s);
                    int nFlags = Mathf.Max(2, Mathf.RoundToInt(halfL / 6f));
                    for (int k = 0; k < nFlags; k++)
                    {
                        int fr = 1 + (int)(Rand(id * 17 + k, 11) * (rows - 2));
                        float fx = -halfL + (k + 0.3f + 0.4f * Rand(id * 17 + k, 12)) * (2f * halfL / nFlags);
                        Vector3 fc = O + T * fx + D * (fr * RowD + 0.5f);
                        float fy = baseH + fr * RowH + 0.9f;
                        float rr = Rand(id * 17 + k, 13);
                        if (monza && rr < 0.5f) Flag(fc, T, D, fy, 2.4f, 1.5f, TracksideArt.SeatGreen, TracksideArt.PaintWhite, TracksideArt.StripeRed);
                        else if (rr < 0.45f) Flag(fc, T, D, fy, 2.4f, 1.5f, TracksideArt.StripeRed, TracksideArt.StripeRed, TracksideArt.StripeRed);
                        else if (rr < 0.7f) Flag(fc, T, D, fy, 2.4f, 1.5f, TracksideArt.StripeBlue, TracksideArt.StripeYellow, TracksideArt.StripeRed);
                        else Flag(fc, T, D, fy, 2.4f, 1.5f, TracksideArt.StripeYellow, TracksideArt.StripeYellow, TracksideArt.StripeYellow);
                    }
                }
                // back wall: coloured cladding outside; inside, above the top row, a strip of cladding and (roofed stands)
                // the glazed fronts of the boxes under the roof
                Vector3 bl = O - T * halfL + D * depth, br = O + T * halfL + D * depth;
                tint = cladC;
                Pieces(A, br, bl, skirt, backTop, D, TracksideArt.Cladding, 4f);
                tint = Color.white;
                if (roof)
                {
                    Pieces(A, bl, br, topY, topY + 1.1f, -D, TracksideArt.Sub(TracksideArt.Cladding, 0f, 1f, 0.5f, 1f), 4f);
                    aoOn = false;
                    Pieces(A, bl, br, topY + 1.1f, backTop - 0.3f, -D, TracksideArt.Glass, 3f);
                    aoOn = true;
                }
                else Pieces(A, bl, br, topY, backTop, -D, TracksideArt.Sub(TracksideArt.Cladding, 0f, 1f, 0.5f, 1f), 4f);
                if (roof)
                {
                    // steel cantilever roof rising towards the track: grey standing-seam sheets on top, the underside in shade
                    // with purlins under it, a lattice girder along the front edge (an advertiser on every third bay, the
                    // stand's name on a banner in the middle), a lattice truss on every column, floodlights
                    float ry = backTop, rFront = -1.6f, rise = 1.1f, ov = 0.8f;
                    Vector3 fl = O - T * (halfL + ov) + D * rFront + up * (ry + rise), fr = O + T * (halfL + ov) + D * rFront + up * (ry + rise);
                    Vector3 bl2 = bl - T * ov + D * 0.5f + up * ry, br2 = br + T * ov + D * 0.5f + up * ry;
                    int fp = Mathf.Max(1, Mathf.RoundToInt((halfL * 2f + 2f * ov) / 6f));
                    tint = new Color(0.62f, 0.65f, 0.70f);
                    for (int k = 0; k < fp; k++)
                    {
                        float f0 = (float)k / fp, f1 = (float)(k + 1) / fp;
                        Quad(A, Vector3.Lerp(fl, fr, f0), Vector3.Lerp(fl, fr, f1), Vector3.Lerp(bl2, br2, f1), Vector3.Lerp(bl2, br2, f0), up, TracksideArt.RoofSeam);
                    }
                    // underside: in the roof's own shade (darker towards the back wall), purlins across
                    aoOn = false;
                    {
                        const int nu = 3;
                        for (int k = 0; k < nu; k++)
                        {
                            float g0 = (float)k / nu, g1 = (float)(k + 1) / nu, sh = Mathf.Lerp(0.62f, 0.4f, (g0 + g1) * 0.5f);
                            tint = new Color(sh, sh * 1.02f, sh * 1.08f);
                            Vector3 p0 = Vector3.Lerp(fl, bl2, g0) - up * 0.22f, p1 = Vector3.Lerp(fr, br2, g0) - up * 0.22f;
                            Vector3 p2 = Vector3.Lerp(fr, br2, g1) - up * 0.22f, p3 = Vector3.Lerp(fl, bl2, g1) - up * 0.22f;
                            Quad(A, p0, p1, p2, p3, -up, TracksideArt.Sw(TracksideArt.RoofWhite));
                        }
                        tint = new Color(0.55f, 0.57f, 0.6f);
                        if (!low)
                            for (int k = 1; k <= 4; k++)
                            {
                                float g = k / 5f;
                                Strut(A, Vector3.Lerp(fl, bl2, g) - up * 0.34f, Vector3.Lerp(fr, br2, g) - up * 0.34f, 0.1f, TracksideArt.Steel);
                            }
                    }
                    tint = Color.white;
                    Vector3 rightF = Vector3.Cross(-D, up).normalized; // the viewer's right, looking at the front edge from the track
                    bool flF = Vector3.Dot(fr - fl, rightF) > 0f; Vector3 eL = flF ? fl : fr, eR = flF ? fr : fl;
                    for (int k = 0; k < fp; k++)
                    {
                        Vector3 g0 = Vector3.Lerp(eL, eR, (float)k / fp), g1 = Vector3.Lerp(eL, eR, (float)(k + 1) / fp);
                        Quad(A, g0 - up * 0.95f, g1 - up * 0.95f, g1 + up * 0.05f, g0 + up * 0.05f, -D, k % 3 == 1 ? TracksideArt.Ad(id + k * 5 + 1) : TracksideArt.Truss);
                    }
                    // the girder's chords with real depth (they catch the sun along the front edge)
                    Strut(A, fl - D * 0.05f + up * 0.02f, fr - D * 0.05f + up * 0.02f, 0.09f, TracksideArt.GantryDark);
                    Strut(A, fl - D * 0.05f - up * 0.92f, fr - D * 0.05f - up * 0.92f, 0.09f, TracksideArt.GantryDark);
                    // the stand's name on a banner hung under the girder in the middle (Monza: every main stand)
                    {
                        float bw = Mathf.Min(8f, halfL - 2f);
                        Vector3 bc = (eL + eR) * 0.5f - D * 0.12f;
                        Quad(A, bc - rightF * bw - up * 1.9f, bc + rightF * bw - up * 1.9f, bc + rightF * bw - up * 0.98f, bc - rightF * bw - up * 0.98f, -D, TracksideArt.StandBanner(id));
                        Quad(A, bc - rightF * bw - up * 1.9f + D * 0.02f, bc + rightF * bw - up * 1.9f + D * 0.02f, bc + rightF * bw - up * 0.98f + D * 0.02f, bc - rightF * bw - up * 0.98f + D * 0.02f, D, TracksideArt.Sw(TracksideArt.GantryDark));
                    }
                    for (int e = -1; e <= 1; e += 2)
                    {
                        Vector3 f2 = e < 0 ? fl : fr, b2 = e < 0 ? bl2 : br2;
                        Quad(A, f2 - up * 0.95f, b2 - up * 0.5f, b2 + up * 0.05f, f2 + up * 0.05f, T * e, TracksideArt.Sw(TracksideArt.Steel));
                    }
                    aoOn = true;
                    // eaves beam along the back columns
                    Strut(A, bl + D * 0.25f + up * (ry - 0.45f), br + D * 0.25f + up * (ry - 0.45f), 0.2f, TracksideArt.GantryDark);
                    int cols = Mathf.Max(2, Mathf.RoundToInt(halfL / 4f) + 1);
                    for (int k = 0; k < cols; k++)
                    {
                        float f = (float)k / (cols - 1);
                        Vector3 cb = bl + T * (2f * halfL * f) + D * 0.25f;
                        // a broad-flange steel column on a base plate, cap plate on top
                        Box(A, cb + up * (ry * 0.5f), T, up, D, 0.16f, ry * 0.5f, 0.28f, TracksideArt.Steel);
                        Box(A, cb + up * (ry * 0.5f), T, up, D, 0.26f, ry * 0.5f - 0.05f, 0.04f, TracksideArt.GantryDark);
                        Box(A, cb + up * 0.06f, T, up, D, 0.42f, 0.06f, 0.42f, TracksideArt.ConcreteDark);
                        // lattice cantilever truss: deep at the column, slim at the roof's edge; chords and webs with thickness
                        Vector3 bt = cb + up * (ry - 0.22f), ft = O + T * (-halfL + 2f * halfL * f) + D * (rFront + 0.3f) + up * (ry + rise - 0.24f);
                        Vector3 bb = bt - up * 1.25f, fb = ft - up * 0.3f;
                        if (low)
                        {
                            Vector3 hwB = T * 0.13f;
                            Quad(A, bb + hwB, fb + hwB, ft + hwB, bt + hwB, T, TracksideArt.Sw(TracksideArt.Steel));
                            Quad(A, bb - hwB, fb - hwB, ft - hwB, bt - hwB, -T, TracksideArt.Sw(TracksideArt.Steel));
                        }
                        else
                        {
                            Strut(A, bt, ft, 0.12f, TracksideArt.Steel);
                            Strut(A, bb, fb, 0.11f, TracksideArt.Steel);
                            const int webs = 4;
                            for (int q = 0; q < webs; q++)
                            {
                                float w0 = (float)q / webs, w1 = (float)(q + 1) / webs;
                                Vector3 lo0 = Vector3.Lerp(bb, fb, w0), hi1 = Vector3.Lerp(bt, ft, w1), lo1 = Vector3.Lerp(bb, fb, w1);
                                Strut(A, lo0, hi1, 0.05f, TracksideArt.GantryDark);
                                if (q + 1 < webs) Strut(A, lo1, hi1, 0.045f, TracksideArt.GantryDark);
                            }
                        }
                        // a floodlight bar under the front edge
                        aoOn = false;
                        Box(A, ft - up * 0.5f + D * 0.4f, T, up, D, 0.7f, 0.1f, 0.14f, TracksideArt.GantryDark, true);
                        Panel(A, ft - up * 0.5f + D * 0.25f, -D, 0.6f, -0.07f, 0.07f, TracksideArt.Sw(TracksideArt.LampYellow));
                        aoOn = true;
                    }
                    // flags along the back edge of the roof
                    int rf = Mathf.Max(2, Mathf.RoundToInt(halfL / 8f));
                    for (int k = 0; k < rf; k++)
                    {
                        Vector3 fc = bl + T * ((k + 0.5f) * 2f * halfL / rf) + D * 0.3f;
                        if (monza || (k & 1) == 0) Flag(fc, T, D, ry, 2.7f, 1.7f, TracksideArt.SeatGreen, TracksideArt.PaintWhite, TracksideArt.StripeRed);
                        else Flag(fc, T, D, ry, 2.7f, 1.7f, TracksideArt.StripeBlue, TracksideArt.StripeYellow, TracksideArt.StripeBlue);
                    }
                }
                // a big screen for this stand: across the track if there is room, else just past the stand's far end
                if (!low && !BigScreen(i, -sg, 4f, 0f) && endScreens) BigScreen(i, sg, front + 2.5f - Off(i, sg), halfL + 9f);
                // a catch fence and a few metres of it either side, in front of the stand (parkland)
                if (!street)
                {
                    int span = Mathf.RoundToInt((halfL + 10f) / ds), s = SI(sg);
                    for (int j = -span; j <= span; j++) fenceZ[s, W(i + j)] = true;
                }
                AddFoot(O + D * (depth * 0.5f), T, halfL + 1f, depth * 0.5f + 1.5f, backTop + 3f);
                AoPad(O + D * (depth * 0.5f), T, D, halfL, depth * 0.5f, 1.8f);
                return true;
            }
            return false;
        }
        {
            // main straight: facing the pits (opposite side), centred on the line
            int ms = pitSide == 0 ? -1 : -pitSide;
            int mainN = low ? 1 : high ? 3 : 2;
            float mhl = street ? 18f : 24f;
            string cid = t.def != null && t.def.id != null ? t.def.id : "";
            int mrows = low ? 9 : 13, bigRows;
            if (!low && MainRows.TryGetValue(cid, out bigRows)) mrows = bigRows; // (Suzuka: the huge main-straight stands)
            float[] at = { 0f, -(2f * mhl + 6f), 2f * mhl + 6f };
            endScreens = false;
            for (int k = 0; k < mainN; k++)
            {
                int i = W(Mathf.RoundToInt((at[k] - 30f) / ds));
                if (!Stand(i, ms, mhl, mrows, true) && k == 0) Stand(i, -ms, mhl, mrows, true);
            }
            endScreens = true;
            if (!low) BigScreen(W(Mathf.RoundToInt((at[mainN > 2 ? 2 : 0] - 30f) / ds)), ms, 6f, mhl + 11f); // one screen past the last main stand
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
            float[][] layout;
            if (!street && StandLayout.TryGetValue(cid, out layout))
            {
                // this circuit's own layout: the real stands' corners, nothing else (scored stays for no one)
                scored.Clear();
                int pw = Mathf.Max(2, Mathf.RoundToInt(60f / ds));
                foreach (var st in layout)
                {
                    if (placed >= cornerN) break;
                    int si = W(Mathf.RoundToInt(st[0] / ds)), apex = si, side;
                    if (st[1] > 1.5f)
                    {
                        for (int j = -pw; j <= pw; j++) if (curvAbs[W(si + j)] > curvAbs[apex]) apex = W(si + j);
                        side = Outside(apex);
                    }
                    else side = st[1] > 0f ? 1 : -1;
                    bool nearU = false; foreach (int u in used) if (Gap(u, apex) < Mathf.RoundToInt(200f / ds)) nearU = true;
                    if (nearU) continue;
                    int at3 = W(apex - Mathf.RoundToInt(st[2] / ds));
                    // (a stand that does not fit, e.g. a building in the way, is moved out up to 10 m by Stand itself)
                    if (Stand(at3, side, st[3], low ? 7 : 10, !low)) { used.Add(apex); placed++; }
                }
            }
            foreach (var kv in scored)
            {
                if (placed >= cornerN) break;
                int apex = kv.Value;
                if (Gap(apex, 0) < Mathf.RoundToInt(180f / ds)) continue;
                bool near = false; foreach (int u in used) if (Gap(u, apex) < Mathf.RoundToInt(250f / ds)) near = true;
                if (near) continue;
                int at2 = W(apex - Mathf.RoundToInt(15f / ds));
                bool roof = !low;
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
                    // the marshals' kit, visible at speed: a yellow and a green flag draped over the parapet (both faces,
                    // hanging a little proud at the foot), a fire extinguisher and a bin of oil absorbent by the door
                    if (!low)
                    {
                        Vector3 f0 = c - D * 0.84f, f1 = c - D * 0.9f;
                        int[] fl = { TracksideArt.PaintYellow, TracksideArt.SeatGreen };
                        for (int q = 0; q < 2; q++)
                        {
                            float ta = q == 0 ? -0.72f : 0.18f, tb = ta + 0.52f + 0.06f * Rand(k, 71 + q);
                            float yb = 0.48f + 0.12f * Rand(k, 73 + q);
                            Quad(A, f1 + T * ta + up * yb, f1 + T * tb + up * yb, f0 + T * tb + up * 1.16f, f0 + T * ta + up * 1.16f, -D, TracksideArt.Sw(fl[q]));
                            Quad(A, f1 + T * ta + up * yb + D * 0.01f, f1 + T * tb + up * yb + D * 0.01f, f0 + T * tb + up * 1.16f + D * 0.01f, f0 + T * ta + up * 1.16f + D * 0.01f, D, TracksideArt.Sw(fl[q]));
                        }
                        Box(A, c - D * 1.0f + T * 1.05f + up * 0.3f, T, up, D, 0.085f, 0.3f, 0.085f, TracksideArt.StripeRed);
                        Box(A, c - D * 1.0f + T * 1.05f + up * 0.64f, T, up, D, 0.04f, 0.04f, 0.04f, TracksideArt.TyreBlack);
                        Box(A, c - D * 0.98f - T * 1.12f + up * 0.32f, T, up, D, 0.26f, 0.32f, 0.22f, TracksideArt.PaintWhite);
                        Box(A, c - D * 0.98f - T * 1.12f + up * 0.67f, T, up, D, 0.28f, 0.03f, 0.24f, TracksideArt.StripeBlue);
                        // the marshals themselves, beside the cabin and behind the barrier: one with the flag ready, a
                        // second at most posts (real posts are never empty on race day)
                        for (int q = 0; q < 2; q++)
                        {
                            if (q == 1 && Rand(k, 86) < 0.3f) continue;
                            Vector3 mc = c - D * (0.2f + 0.35f * Rand(k, 87 + q)) + T * (q == 0 ? 1.6f : -1.7f);
                            if (!Free(mc, lat - 1.6f)) continue;
                            Marshal(mc, T, D, k * 7 + q + (sg > 0 ? 0 : 3), q == 0);
                        }
                    }
                    AddFoot(c, T, 1.3f, 1.3f, 3.6f); AoPad(c, T, D, 1.05f, 1.05f, 0.9f);
                    sideT++;
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- spectators along the catch fence (general admission)
        // Race day at a parkland circuit: people stand two deep behind the catch fence on the outside of the big corners
        // where there is no grandstand. Crowd cards (the stands' texture and material, Standing): a full front row and a
        // back row on a low step with gaps; a footprint keeps the woods and the town out. High: up to 6 corners; Medium:
        // up to 4, only in chunks whose crowd layer already draws (no new draw call); Low: none. Procedural circuits
        // only: on a real-world map (Monza) the map's own stands and buildings come later and would clash with them.
        if (!low && !street && !RealWorld.HasBuildings)
        {
            int maxSp = high ? 6 : 4, spots = 0, segN = Mathf.Max(1, Mathf.RoundToInt(7f / ds));
            int spBefore = Mathf.RoundToInt(40f / ds), spAfter = Mathf.RoundToInt(70f / ds);
            var spAt = new List<int>();
            var spFeet = new List<Vector4>();
            foreach (int apex in corners)
            {
                if (spots >= maxSp) break;
                if (curvAbs[apex] < 1f / 300f || Gap(apex, 0) < Mathf.RoundToInt(250f / ds)) continue;
                bool near = false; foreach (int u in spAt) if (Gap(u, apex) < Mathf.RoundToInt(300f / ds)) near = true;
                if (near) continue;
                int sg = Outside(apex), s = SI(sg), cards = 0;
                spFeet.Clear();
                for (int j = -spBefore; j + segN <= spAfter; j += segN)
                {
                    int i0 = W(apex + j), i1 = W(apex + j + segN);
                    if (!fenceZ[s, i0] || !fenceZ[s, i1] || InPit(i0, sg) || InPit(i1, sg)) continue;
                    var p0 = t.pts[i0]; var p1 = t.pts[i1];
                    Vector3 D0 = Nrm(p0) * sg, D1 = Nrm(p1) * sg;
                    SetChunk(i0);
                    if (!high && C.v.Count == 0) continue;
                    bool front = false;
                    for (int row = 0; row < 2; row++)
                    {
                        if (row == 1 && (!front || Rand(i0, 93) < 0.25f)) continue; // the back row only behind a front row
                        float back = 3.4f + row * 1.1f + 0.3f * Rand(i0, 90 + row);
                        float o0 = Off(i0, sg) + back, o1 = Off(i1, sg) + back;
                        Vector3 a = P(p0) + D0 * o0, b = P(p1) + D1 * o1, mid = (a + b) * 0.5f;
                        float len = (b - a).magnitude;
                        if (len < 2f || len > 3f * segN * ds) continue;
                        if (!Free(a, o0 - 2f) || !Free(b, o1 - 2f) || !Free(mid, (o0 + o1) * 0.5f - 2f)) continue;
                        Standing(a, b, row * 0.3f, 1.7f, (D0 + D1).normalized, Rand(i0, 95 + row) < 0.45f ? 1 : 0, 8800 + i0 * 3 + row);
                        spFeet.Add(new Vector4(mid.x, mid.z, b.x - a.x, b.z - a.z));
                        if (row == 0) front = true;
                        cards++;
                    }
                }
                // footprints only now: Free above must not see this corner's own cards
                foreach (var fp in spFeet)
                {
                    var dir = new Vector3(fp.z, 0f, fp.w);
                    AddFoot(new Vector3(fp.x, 0f, fp.y), dir, dir.magnitude * 0.5f, 1.2f);
                }
                if (cards > 0) { spAt.Add(apex); spots++; }
            }
        }

        // ---------------------------------------------------------------- fans behind the catch fences (every circuit)
        // (Adi, 27 Nov: "add more people behind the fence watching the race ... it feels boring"): groups of standing
        // fans, two deep, behind the catch fence wherever there is room: more at the corners and on the main straight,
        // 20-70 m long, every 120-300 m round the lap, both sides. Real people (cut-out cards, one material, one draw per
        // chunk that has them, Baked F2_PEOPLE), footprints keep the woods and the town's buildings off them.
        // High: up to 26 groups; Medium: 14; Low: none. A street circuit (Monaco) keeps its balconies instead.
        if (!low && !street && EnvTex.PhotoTex("crowd_standing") != null)
        {
            int maxG = high ? 26 : 14, groups = 0, segN = Mathf.Max(1, Mathf.RoundToInt(6f / ds));
            int nFence = 0, nLen = 0, nFree = 0, nOk = 0; var gAt = new System.Text.StringBuilder(); // load-time log only
            var gFeet = new List<Vector4>();
            int iStep = Mathf.Max(1, Mathf.RoundToInt(10f / ds));
            int nextAt = Mathf.RoundToInt(60f / ds);
            for (int i = 0; i < N && groups < maxG; i += iStep)
            {
                if (i < nextAt) continue;
                // more where it is exciting: the corners (curvature) and the start straight (s < 400 m or the last 300 m)
                float sAt = i * ds, hot = Mathf.Clamp01(curvAbs[i] * 150f) + ((sAt < 400f || sAt > N * ds - 300f) ? 0.6f : 0f);
                if (Rand(i, 140) > 0.25f + 0.6f * hot) continue;
                float glen = 20f + 50f * Rand(i, 141);
                int gN = Mathf.Max(segN, Mathf.RoundToInt(glen / ds));
                int placed = 0;
                for (int pass = 0; pass < 2 && placed == 0; pass++)
                {
                    int sg = (Rand(i, 142) < 0.5f) == (pass == 0) ? 1 : -1, s = SI(sg);
                    gFeet.Clear();
                    for (int j = 0; j + segN <= gN; j += segN)
                    {
                        int i0 = W(i + j), i1 = W(i + j + segN);
                        if (!fenceZ[s, i0] || !fenceZ[s, i1] || InPit(i0, sg) || InPit(i1, sg)) { nFence++; continue; }
                        var p0 = t.pts[i0]; var p1 = t.pts[i1];
                        Vector3 D0 = Nrm(p0) * sg, D1 = Nrm(p1) * sg;
                        SetChunk(i0);
                        float back = 3.2f + 0.4f * Rand(i0, 143);
                        float o0 = Off(i0, sg) + back, o1 = Off(i1, sg) + back;
                        Vector3 a = P(p0) + D0 * o0, b = P(p1) + D1 * o1, mid = (a + b) * 0.5f;
                        float len = (b - a).magnitude;
                        if (len < 2f || len > 3f * segN * ds) { nLen++; continue; }
                        if (!Free(a, o0 - 2f) || !Free(b, o1 - 2f) || !Free(mid, (o0 + o1) * 0.5f - 2f)) { nFree++; continue; }
                        nOk++;
                        int band = Rand(i0, 144) < 0.3f + 0.3f * hot ? 1 : 0;
                        // a stretch faces the track: a is at the viewer's left when looking out from the track
                        if (sg > 0) Spectators(b, a, 0f, (D0 + D1).normalized, band, 9900 + i0);
                        else Spectators(a, b, 0f, (D0 + D1).normalized, band, 9900 + i0);
                        gFeet.Add(new Vector4(mid.x, mid.z, b.x - a.x, b.z - a.z));
                        placed++;
                    }
                    foreach (var fp in gFeet)
                    {
                        var dir = new Vector3(fp.z, 0f, fp.w);
                        AddFoot(new Vector3(fp.x, 0f, fp.y), dir, dir.magnitude * 0.5f, 1.4f);
                    }
                }
                if (placed > 0) { groups++; gAt.Append(' ').Append(Mathf.RoundToInt(sAt)); nextAt = i + gN + Mathf.RoundToInt((120f + 180f * Rand(i, 145)) / ds); }
                else nextAt = i + Mathf.RoundToInt(40f / ds);
            }
            Debug.Log("Trackside fans: " + groups + " groups, " + nOk + " cards (rejected: fence " + nFence + ", length " + nLen + ", not free " + nFree + "), wallLat " + wallLat.ToString("0.0") + ", s =" + gAt);
        }

        // ---------------------------------------------------------------- circuit furniture behind the barriers
        // What a real circuit is cluttered with, all in the atlas mesh (no draw calls): TV camera towers on the outside of
        // the corners (scaffold wrapped in mesh, a platform with a banner, the camera), a recovery crane parked behind the
        // barrier at the slow corners, and tall floodlight masts along the lap.
        if (!low)
        {
            var camAt = new List<int>();
            int maxCam = high ? 9 : 6, camNo = 0;
            foreach (int apex in corners)
            {
                if (camAt.Count >= maxCam) break;
                if (curvAbs[apex] < 1f / 220f) continue;
                bool near = false; foreach (int u in camAt) if (Gap(u, apex) < Mathf.RoundToInt(170f / ds)) near = true;
                if (near) continue;
                int sg = Outside(apex), i = W(apex + Mathf.RoundToInt(28f / ds));
                if (InPit(i, sg)) continue;
                var p = t.pts[i]; Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up;
                float lat = Off(i, sg) + 3.4f; Vector3 c = P(p) + D * lat;
                if (!Free(c, lat - 1.6f) || !Free(c + T * 1.6f, lat - 1.6f) || !Free(c - T * 1.6f, lat - 1.6f)) continue;
                camAt.Add(apex);
                SetChunk(i);
                const float th = 5.2f, hf = 1.0f;
                Vector3[] nn = { -D, D, T, -T };
                for (int f = 0; f < 4; f++)
                {
                    Vector3 n = nn[f], sd = Vector3.Cross(n, up).normalized * hf, fc = c + n * hf;
                    Wall(A, fc - sd, fc + sd, skirt, th * 0.5f, n, TracksideArt.ScaffoldR);
                    Wall(A, fc - sd, fc + sd, th * 0.5f, th, n, TracksideArt.ScaffoldR);
                }
                Box(A, c + up * (th + 0.06f), T, up, D, hf + 0.4f, 0.06f, hf + 0.4f, TracksideArt.Steel, true);
                for (int f = 0; f < 4; f++)
                {
                    Vector3 n = nn[f], sd = Vector3.Cross(n, up).normalized;
                    Box(A, c + n * (hf + 0.38f) + up * (th + 1.1f), sd, up, n, hf + 0.4f, 0.03f, 0.03f, TracksideArt.PostDark, true);
                    Post(A, c + n * (hf + 0.38f) + sd * (hf + 0.38f), n, sd, 0.03f, th, th + 1.1f, TracksideArt.PostDark);
                }
                aoOn = false;
                Panel(A, c - D * (hf + 0.42f), -D, hf + 0.4f, th + 0.12f, th + 1.0f, TracksideArt.Ad(BoldAds[camNo % BoldAds.Length]));
                aoOn = true;
                // the camera on its pedestal, pointed back up the road at the cars coming out of the corner
                Box(A, c - D * 0.5f + up * (th + 0.75f), T, up, D, 0.06f, 0.65f, 0.06f, TracksideArt.PostDark);
                Box(A, c - D * 0.5f - T * 0.2f + up * (th + 1.55f), T, up, D, 0.5f, 0.17f, 0.15f, TracksideArt.GantryDark, true);
                Box(A, c - D * 0.5f - T * 0.78f + up * (th + 1.55f), T, up, D, 0.1f, 0.12f, 0.12f, TracksideArt.GlassDark, true);
                AddFoot(c, T, hf + 1f, hf + 1f, th + 2.2f); Tall.Add(c); AoPad(c, T, D, hf, hf, 1.0f);
                // the lens: in front of the tower's solid (+1.5 m pad of the TV cameras' inside test), 1 m over the platform rail
                TvMounts.Add(new TvMount { pos = c - D * (hf + 2.7f) + up * (th + 2.3f), look = -T, idx = i });
                camNo++;
            }
            // recovery cranes: yellow carrier, cab, outriggers, the boom raised over the barrier
            int cranes = 0, maxCr = high ? 4 : 2;
            foreach (int apex in tyreCorners)
            {
                if (cranes >= maxCr) break;
                int sg = Outside(apex), i = W(apex - Mathf.RoundToInt(22f / ds));
                if (InPit(i, sg)) continue;
                var p = t.pts[i]; Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up;
                float lat = Off(i, sg) + 5.2f; Vector3 c = P(p) + D * lat;
                bool ok = true;
                for (int q = -1; q <= 1 && ok; q++) ok = Free(c + T * (q * 4f), lat - 2f);
                if (!ok) continue;
                SetChunk(i);
                Box(A, c + up * 1.25f, T, up, D, 3.6f, 0.55f, 1.2f, TracksideArt.PaintYellow, true);
                Box(A, c + T * 2.5f + up * 2.45f, T, up, D, 0.9f, 0.65f, 1.15f, TracksideArt.PaintYellow);
                aoOn = false;
                Panel(A, c + T * 2.5f - D * 1.17f, -D, 0.7f, 2.1f, 2.95f, TracksideArt.Sw(TracksideArt.GlassDark));
                Panel(A, c - T * 1.2f - D * 1.22f, -D, 1.6f, 0.8f, 1.7f, TracksideArt.Hazard);
                aoOn = true;
                for (int q = -1; q <= 1; q++)
                    Box(A, c + T * (q * 2.4f) + up * 0.5f, T, up, D, 0.5f, 0.5f, 1.25f, TracksideArt.TyreBlack);
                // boom: from the turntable up and out over the barrier, two telescoped sections
                Vector3 bx = (-D * 0.72f + up * 0.69f).normalized, by = Vector3.Cross(T, bx).normalized;
                if (by.y < 0f) by = -by;
                Vector3 foot = c - T * 1.0f + up * 2.0f;
                Box(A, foot + bx * 3.2f, bx, by, T, 3.2f, 0.28f, 0.28f, TracksideArt.PaintYellow, true);
                Box(A, foot + bx * 8.4f, bx, by, T, 2.2f, 0.2f, 0.2f, TracksideArt.PaintYellow, true);
                Vector3 tip = foot + bx * 10.6f;
                Box(A, tip - up * 1.6f, T, up, D, 0.02f, 1.6f, 0.02f, TracksideArt.PostDark);
                Box(A, tip - up * 3.35f, T, up, D, 0.12f, 0.16f, 0.12f, TracksideArt.LampRed, true);
                AddFoot(c, T, 4.2f, 1.8f, 3.2f); Tall.Add(c); AoPad(c, T, D, 3.6f, 1.2f, 1.3f);
                AddSolid(c - D * 4.2f, T, 0.7f, 4.6f, 2f, 9.8f, true); // the boom, out over the barrier
                cranes++;
            }
            // floodlight masts: a tall pole with a rack of lamps, about every 450 m, alternating sides, behind the fence
            int masts = Mathf.FloorToInt((float)t.length / 450f);
            for (int k = 0; k < masts; k++)
            {
                int i = W(Mathf.RoundToInt((k * 450f + 190f) / ds)), sg = (k & 1) == 0 ? -1 : 1;
                if (InPit(i, sg)) sg = -sg;
                if (InPit(i, sg)) continue;
                var p = t.pts[i]; Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up;
                float lat = Off(i, sg) + 6.5f; Vector3 c = P(p) + D * lat;
                if (!Free(c, lat - 1f)) continue;
                SetChunk(i);
                const float mh = 19f;
                Box(A, c + up * (mh * 0.5f + skirt * 0.5f), T, up, D, 0.16f, mh * 0.5f - skirt * 0.5f, 0.16f, TracksideArt.Steel);
                Box(A, c - D * 0.25f + up * (mh + 0.6f), T, up, D, 1.5f, 0.75f, 0.12f, TracksideArt.GantryDark, true);
                aoOn = false;
                for (int q = 0; q < 6; q++)
                    Panel(A, c - D * 0.38f + T * ((q % 3 - 1) * 0.95f), -D, 0.36f, mh + (q < 3 ? 0.05f : 0.75f), mh + (q < 3 ? 0.55f : 1.25f), TracksideArt.Sw(TracksideArt.PaintWhite));
                aoOn = true;
                AddFoot(c, T, 0.8f, 0.8f, mh + 1.6f); Tall.Add(c);
            }
            // feather banners along the start straight where there is neither a stand nor the pits: a 6.5 m pole with a
            // tall narrow flag in the advertisers' colours every 22 m, behind the fence (the straight looked bare there)
            int[] feather = { TracksideArt.StripeRed, TracksideArt.PaintYellow, TracksideArt.PaintWhite, TracksideArt.StripeBlue, TracksideArt.SeatGreen, TracksideArt.MarshalOrange };
            for (int sg = -1; sg <= 1; sg += 2)
                for (int k = -14; k <= 20; k++)
                {
                    int i = W(Mathf.RoundToInt(k * 22f / ds));
                    if (InPit(i, sg) || curvAbs[i] > 1f / 400f) continue;
                    var p = t.pts[i]; Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up;
                    float lat = Off(i, sg) + 2.2f; Vector3 c = P(p) + D * lat;
                    if (!Free(c, lat - 0.8f)) continue;
                    SetChunk(i);
                    Post(Pm, c, -D, T, 0.04f, skirt, 6.5f, TracksideArt.PostDark);
                    int fcol = feather[(((k + 14) / 3) + (sg > 0 ? 2 : 0)) % feather.Length]; // runs of three in one colour
                    aoOn = false;
                    Panel(A, c + T * 0.45f, -D, 0.4f, 2.2f, 6.4f, TracksideArt.Sw(fcol));
                    Panel(A, c + T * 0.45f + D * 0.01f, D, 0.4f, 2.2f, 6.4f, TracksideArt.Sw(fcol));
                    aoOn = true;
                    AddSolid(c + T * 0.45f, T, 0.55f, 0.3f, -1f, 6.6f, true);
                }
        }

        // ---------------------------------------------------------------- barriers (+ fences, + base shadow decals)
        {
            int step = Mathf.Max(1, Mathf.RoundToInt((street ? 3f : 4f) / ds));
            float maxLen = street ? 6f : low ? 22f : 15f, maxTurn = Mathf.Cos(3.5f * Mathf.Deg2Rad);
            float postGap = low ? 12f : 8f;
            // tunnel bore (Monaco, Waterfront.BuildTunnel: covered from tunnelFrom to tunnelTo): no debris fence inside it.
            // The fence stands 0.25 m behind the barrier line and leans 0.55 m in at the top, so inside the bore its top
            // came through the tunnel walls as diagonal strips of chain-link. A 2 m margin keeps the leaning top clear of
            // the portals too; the walls themselves stay.
            bool hasBore = t.def != null && t.def.tunnelTo > t.def.tunnelFrom;
            double boreFrom = hasBore ? t.def.tunnelFrom - 2.0 : 0.0, boreTo = hasBore ? t.def.tunnelTo + 2.0 : 0.0;
            bool InBore(int bi) { if (!hasBore) return false; double bs = t.pts[((bi % N) + N) % N].s; return bs >= boreFrom && bs <= boreTo; }
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
                    bool dirty = dirt[s, i] > 0.3f, fz = fences && (street || fenceZ[s, i]) && !InBore(i0) && !InBore(i1) && !InBore((i0 + i1) / 2);
                    float topY;
                    if (IsWall(i))
                    {
                        // concrete wall, painted strip along the top
                        float h = InPit(i, sg) ? 1.15f : 1.0f, th = 0.5f, hp = h - 0.28f;
                        int paint = InPit(i, sg) ? TracksideArt.PaintWhite : ((segNo & 1) == 0 ? TracksideArt.PaintRed : TracksideArt.PaintWhite);
                        Vector3 a1 = a + da * th, b1 = b + db * th;
                        // precast blocks of ~4 m (end joints, tie holes, streaks; two kinds mixed), scuffed at impacts
                        if (dirty) tint = new Color(0.86f, 0.83f, 0.8f);
                        int nb = Mathf.Max(1, Mathf.RoundToInt(len / 4f));
                        for (int q = 0; q < nb; q++)
                        {
                            Vector3 qa = Vector3.Lerp(a, b, (float)q / nb), qb = Vector3.Lerp(a, b, (float)(q + 1) / nb);
                            var rb2 = dirty ? TracksideArt.Scuff : Rand(segNo * 7 + q, 9) < 0.5f ? TracksideArt.ConcreteBlock : TracksideArt.ConcreteBlock2;
                            if (skirt < 0f) Quad(A, qa + up * skirt, qb + up * skirt, qb, qa, -mid, TracksideArt.Sub(rb2, 0f, 1f, 0f, 0.02f));
                            Quad(A, qa, qb, qb + up * hp, qa + up * hp, -mid, rb2);
                        }
                        tint = Color.white;
                        // painted strip with a 4 cm chamfer on the top edge (catches the light: a crisp line along the wall)
                        const float ch = 0.04f;
                        Vector3 a2 = a + da * ch, b2 = b + db * ch;
                        Quad(A, a + up * hp, b + up * hp, b + up * (h - ch), a + up * (h - ch), -mid, TracksideArt.Sw(paint));
                        Quad(A, a + up * (h - ch), b + up * (h - ch), b2 + up * h, a2 + up * h, (up - mid).normalized, TracksideArt.Sw(paint));
                        Quad(A, a2 + up * h, b2 + up * h, b1 + up * h, a1 + up * h, up, TracksideArt.Sw(paint));
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
                        // galvanised rail sections of ~4 m (zinc mottling, splice bolts, grime low down)
                        var rs = TracksideArt.Sw(TracksideArt.Steel);
                        Vector4 rLo = TracksideArt.Sub(TracksideArt.Armco, 0f, 1f, 0f, 0.5f), rHi = TracksideArt.Sub(TracksideArt.Armco, 0f, 1f, 0.5f, 1f);
                        int nr = Mathf.Max(1, Mathf.RoundToInt(len / 4f));
                        for (int k = 0; k + 1 < beams.Length; k += 2)
                        {
                            float yb = beams[k], yt = beams[k + 1], ym = (yb + yt) * 0.5f;
                            if (dirty && k == 0) tint = new Color(0.72f, 0.69f, 0.64f);
                            for (int q = 0; q < nr; q++)
                            {
                                float f0 = (float)q / nr, f1 = (float)(q + 1) / nr;
                                Vector3 qa = Vector3.Lerp(a, b, f0), qb = Vector3.Lerp(a, b, f1), qd = Vector3.Lerp(da, db, f0), qe = Vector3.Lerp(da, db, f1);
                                if (low)
                                {
                                    Vector3 ra = qa + qd * 0.05f, rb = qb + qe * 0.05f; // recessed middle
                                    Quad(A, qa + up * yb, qb + up * yb, rb + up * ym, ra + up * ym, -mid - up * 0.5f, rLo);
                                    Quad(A, ra + up * ym, rb + up * ym, qb + up * yt, qa + up * yt, -mid + up * 0.5f, rHi);
                                    continue;
                                }
                                // Medium / High: the real W profile (two ridges, the valley between them, rolled edges; 31 cm
                                // tall, 7.5 cm deep) in the near-only post layer (hidden beyond ~85 m, TracksideLod), and a
                                // flat face at the valley's depth in the atlas layer that stands for it far away
                                Quad(A, qa + qd * WDep[2] + up * yb, qb + qe * WDep[2] + up * yb, qb + qe * WDep[2] + up * yt, qa + qd * WDep[2] + up * yt, -mid, TracksideArt.Armco);
                                float hk = (yt - yb) / 0.31f;
                                for (int e = 0; e < 4; e++)
                                {
                                    float y0 = yb + WY[e] * hk, y1 = yb + WY[e + 1] * hk, d0 = WDep[e] - 0.004f, d1 = WDep[e + 1] - 0.004f;
                                    Vector3 fn = -mid + up * ((WDep[e + 1] - WDep[e]) / Mathf.Max(0.01f, y1 - y0));
                                    Quad(Pm, qa + qd * d0 + up * y0, qb + qe * d0 + up * y0, qb + qe * d1 + up * y1, qa + qd * d1 + up * y1, fn,
                                         TracksideArt.Sub(TracksideArt.Armco, 0f, 1f, e * 0.25f, (e + 1) * 0.25f));
                                }
                            }
                            tint = Color.white;
                        }
                        if (!low) Quad(A, a + da * 0.12f + up * beams[0], b + db * 0.12f + up * beams[0], b + db * 0.12f + up * beams[beams.Length - 1], a + da * 0.12f + up * beams[beams.Length - 1], mid, rs);
                        // posts, evenly spaced across segments
                        float d = postCarry;
                        while (d < len)
                        {
                            Vector3 pc = Vector3.Lerp(a, b, d / Mathf.Max(len, 1e-3f)) + Vector3.Lerp(da, db, d / Mathf.Max(len, 1e-3f)) * 0.2f;
                            Post(Pm, pc, -mid, len3 / Mathf.Max(len, 1e-3f), 0.07f, skirt, beams[beams.Length - 1] + 0.04f, TracksideArt.PostDark);
                            d += postGap;
                        }
                        postCarry = d - len;
                        topY = beams[beams.Length - 1];
                        if (decals && (!hills || t.runL != null && (sg > 0 ? t.runL[i] : t.runR[i]) == SurfaceType.Gravel))
                            Decal(G, a - da * 0.9f, a + da * 0.15f, b - db * 0.9f, b + db * 0.15f, 0.05f, fenceU / 4f, (fenceU + len) / 4f, TracksideArt.GrimeCol(TracksideArt.GrimeShade));
                    }
                    // debris / catch fence above the barrier (parkland: 3.7 m dark-green mesh), advertising boards in front of it
                    if (fz)
                    {
                        bool tall = !street;
                        float back = IsWall(i) ? 0.25f : 0.3f, y0 = topY - 0.05f, y1 = topY + (tall ? 3.0f : 2.6f), y2 = topY + (tall ? 3.7f : 3.3f);
                        Vector3 fa = a + da * back, fb = b + db * back, lean = -mid * 0.55f;
                        Vector4 r1 = new Vector4(fenceU, y0, fenceU + len, y1), r2 = new Vector4(fenceU, y1, fenceU + len, y2);
                        aoOn = false;
                        Quad(F, fa + up * y0, fb + up * y0, fb + up * y1, fa + up * y1, -mid, r1);
                        Quad(F, fa + up * y1, fb + up * y1, fb + lean + up * y2, fa + lean + up * y2, -mid, r2);
                        if (!low && (segNo / 3 + s) % 4 != 3 && !InPit(i, sg))
                        {
                            // 1 m boards in runs of three segments, bold colours (yellow / red / white / blue)
                            int nbp = Mathf.Max(1, Mathf.RoundToInt(len / 4f));
                            Vector3 ba = a + da * (back - 0.06f), bb = b + db * (back - 0.06f);
                            // the board's left end must be at the viewer's left (seen from the track), or its name reads mirrored:
                            // true along the left-hand fence, the other way round along the right-hand one
                            if (Vector3.Dot(Vector3.Cross(up, mid), len3) < 0f) { var sw3 = ba; ba = bb; bb = sw3; }
                            for (int q = 0; q < nbp; q++)
                            {
                                Vector3 qa = Vector3.Lerp(ba, bb, (float)q / nbp), qb = Vector3.Lerp(ba, bb, (float)(q + 1) / nbp);
                                Quad(A, qa + up * (topY - 0.15f), qb + up * (topY - 0.15f), qb + up * (topY + 0.85f), qa + up * (topY + 0.85f), -mid,
                                     TracksideArt.Ad(BoldAds[(segNo * 2 + q + s * 3) % BoldAds.Length]));
                            }
                        }
                        aoOn = true;
                        // the mesh is a solid for the TV cameras (GameManager.BuildTvCams): a camera must not film through it
                        AddSolid((fa + fb) * 0.5f + lean * 0.5f, len3, len * 0.5f, 0.4f, y0, y2 + 0.05f, true);
                        // the fence's posts and its top stay go in the fence layer itself (the solid cable rows of its texture,
                        // FenceSolid), so whatever hides the fence (Low, also when Auto steps down mid-race; no cutout shader)
                        // hides them too: no orphan comb of posts along the Armco. Far away they thin to the fence's veil.
                        FencePost(fa + da * 0.06f, -mid, len3 / Mathf.Max(len, 1e-3f), 0.05f, topY - 0.3f, y1);
                        if (tall && !low)
                            for (float d = 5f; d < len - 2f; d += 5f)
                                FencePost(Vector3.Lerp(fa, fb, d / len) + da * 0.06f, -mid, len3 / Mathf.Max(len, 1e-3f), 0.05f, topY - 0.3f, y1);
                        Quad(F, fa + da * 0.06f + up * y1, fa + da * 0.06f + up * (y1 + 0.1f), fa + lean + up * (y2 + 0.05f), fa + lean + up * (y2 - 0.05f), Vector3.Cross(mid, up), FenceSolid);
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
                    if (ok && sg == pitSide && PitGap(t, i, o)) ok = false; // the pit entry / exit road crosses the barrier line here
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
                int s = SI(sg), piece = 0, tecNo = 0;
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
                    if (ok && have && tecZ[s, pI])
                    {
                        // Tecpro-style blocks (chicanes): ~1.2 m foam-filled modules, alternately blue and red, bevelled
                        // vertical and top edges, a white strap band, a dark 4 cm joint between modules; scuffed at impacts
                        SetChunk(pI);
                        float len = (front - pf).magnitude;
                        int nbk = Mathf.Max(1, Mathf.RoundToInt(len / 1.2f));
                        float bv = 0.08f, yT = hT - bv;
                        Color tecBase = dirt[s, pI] > 0.3f ? new Color(0.82f, 0.8f, 0.78f) : Color.white;
                        for (int q = 0; q < nbk; q++)
                        {
                            // weathered, not showroom-new: each module a little lighter / darker and warmer (sun-faded,
                            // dusty) than its neighbours, and black tyre rubber smeared across the face at wheel height
                            // on some of them (more where cars hit: dirt > 0.3). Load time only; +2 quads per module.
                            int tq = tecNo;
                            float wk = 0.84f + 0.18f * Rand(tq + pI * 13, 301), warm = 0.05f * Rand(tq + pI * 13, 302);
                            Color modT = tecBase * new Color(wk, wk * (1f - warm * 0.4f), wk * (1f - warm), 1f);
                            bool rub = Rand(tq + pI * 13, 303) < (dirt[s, pI] > 0.3f ? 0.55f : 0.22f);
                            tint = modT;
                            float f0 = (float)q / nbk, f1 = (float)(q + 1) / nbk;
                            Vector3 a0 = Vector3.Lerp(pf, front, f0), b0 = Vector3.Lerp(pf, front, f1);
                            Vector3 ka = Vector3.Lerp(pb, back, f0), kb = Vector3.Lerp(pb, back, f1);
                            Vector3 nb = Vector3.Lerp(pd, D, (f0 + f1) * 0.5f).normalized, al = b0 - a0; // nb: away from the track
                            float bl = al.magnitude; if (bl < 0.3f) continue; al /= bl;
                            a0 += al * 0.02f; b0 -= al * 0.02f; ka += al * 0.02f; kb -= al * 0.02f;
                            var rc = TracksideArt.Sw(((tecNo++) & 1) == 0 ? TracksideArt.PaintBlue : TracksideArt.PaintRed);
                            Vector3 fa = a0 + al * bv, fb = b0 - al * bv, sa = a0 + nb * bv, sb = b0 + nb * bv;
                            if (rub)
                            {
                                // a dark rubber smear at wheel height (~0.25-0.5 m) over part of the module's width
                                float r0 = 0.22f + 0.06f * Rand(tq, 304), r1 = r0 + 0.16f + 0.12f * Rand(tq, 305);
                                float x0 = 0.05f + 0.4f * Rand(tq, 306), x1 = Mathf.Min(0.97f, x0 + 0.3f + 0.4f * Rand(tq, 307));
                                Vector3 ma = Vector3.Lerp(fa, fb, x0), mb = Vector3.Lerp(fa, fb, x1);
                                Quad(A, fa + up * skirt, fb + up * skirt, fb + up * r0, fa + up * r0, -nb, rc);
                                Quad(A, fa + up * r0, ma + up * r0, ma + up * r1, fa + up * r1, -nb, rc);
                                Quad(A, mb + up * r0, fb + up * r0, fb + up * r1, mb + up * r1, -nb, rc);
                                tint = modT * new Color(0.4f, 0.4f, 0.42f, 1f);
                                Quad(A, ma + up * r0, mb + up * r0, mb + up * r1, ma + up * r1, -nb, rc);
                                tint = modT;
                                Quad(A, fa + up * r1, fb + up * r1, fb + up * 0.66f, fa + up * 0.66f, -nb, rc);
                            }
                            else Quad(A, fa + up * skirt, fb + up * skirt, fb + up * 0.66f, fa + up * 0.66f, -nb, rc);
                            Quad(A, fa + up * 0.66f, fb + up * 0.66f, fb + up * 0.78f, fa + up * 0.78f, -nb, TracksideArt.Sw(TracksideArt.PaintWhite));
                            Quad(A, fa + up * 0.78f, fb + up * 0.78f, fb + up * yT, fa + up * yT, -nb, rc);
                            Quad(A, sa + up * skirt, fa + up * skirt, fa + up * yT, sa + up * yT, (-nb - al).normalized, rc);
                            Quad(A, fb + up * skirt, sb + up * skirt, sb + up * yT, fb + up * yT, (-nb + al).normalized, rc);
                            Quad(A, fa + up * yT, fb + up * yT, sb + up * hT, sa + up * hT, (up - nb).normalized, rc);
                            Quad(A, sa + up * yT, fa + up * yT, sa + up * hT, sa + up * hT, (up - nb - al).normalized, rc); // corner facets
                            Quad(A, fb + up * yT, sb + up * yT, sb + up * hT, sb + up * hT, (up - nb + al).normalized, rc);
                            Quad(A, sa + up * hT, sb + up * hT, kb + up * hT, ka + up * hT, up, rc);
                            // the joint: the next module's side in shade
                            Quad(A, b0 + up * skirt, kb + up * skirt, kb + up * hT, b0 + up * hT, al, TracksideArt.Sw(TracksideArt.TyreBlack));
                        }
                        tint = Color.white;
                        if (decals && !hills) Decal(G, pf - pd * 0.8f, pf, front - D * 0.8f, front, 0.05f, 0, len / 4f, TracksideArt.GrimeCol(TracksideArt.GrimeShade));
                        piece++;
                    }
                    else if (ok && have)
                    {
                        SetChunk(pI);
                        float len = (front - pf).magnitude;
                        // belt pieces with bolt rows and tyre scuffs; the blue belt (forest circuits) is the white one tinted
                        tint = dirt[s, pI] > 0.3f ? new Color(0.8f, 0.78f, 0.75f) : Color.white;
                        bool colour = (piece & 1) == 0;
                        if (colour && beltB == TracksideArt.BeltBlue) tint *= new Color(0.15f, 0.32f, 0.72f);
                        Quad(A, pf + up * skirt, front + up * skirt, front + up * hT, pf + up * hT, -(pd + D),
                             colour && beltB != TracksideArt.BeltBlue ? TracksideArt.BeltRedR : TracksideArt.BeltWhiteR);
                        tint = dirt[s, pI] > 0.3f ? new Color(0.8f, 0.78f, 0.75f) : Color.white; // (tops: no blue)
                        Quad(A, pf + up * hT, front + up * hT, back + up * hT, pb + up * hT, up, TracksideArt.Sub(TracksideArt.TyreTops, 0, Mathf.Min(1f, len / 2.5f), 0, depthT / 2.5f));
                        tint = Color.white;
                        if (decals && !hills) Decal(G, pf - pd * 0.8f, pf, front - D * 0.8f, front, 0.05f, 0, len / 4f, TracksideArt.GrimeCol(TracksideArt.GrimeShade));
                        piece++;
                    }
                    if (ok && !have) { SetChunk(k); Quad(A, front + up * skirt, back + up * skirt, back + up * hT, front + up * hT, -Tan(p), TracksideArt.Sw(tecZ[s, k] ? TracksideArt.PaintBlue : TracksideArt.TyreBlack)); }
                    if (!ok && have) { SetChunk(pI); Quad(A, pf + up * skirt, pb + up * skirt, pb + up * hT, pf + up * hT, Tan(t.pts[pI]), TracksideArt.Sw(tecZ[s, pI] ? TracksideArt.PaintRed : TracksideArt.TyreBlack)); }
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
            {
                Box(A, c + Nn * (po * sg) + up * (postH * 0.5f + skirt * 0.5f), Nn, up, T, 0.3f, postH * 0.5f - skirt * 0.5f, 0.3f, TracksideArt.GantryWhite);
                Tall.Add(c + Nn * (po * sg));
                AddSolid(c + Nn * (po * sg), T, 0.5f, 0.5f, -1f, postH + 0.3f, true);
            }
            Box(A, c + up * beamY, Nn, up, T, po + 0.3f, 0.45f, 0.5f, TracksideArt.GantryWhite, true);
            AddSolid(c - T * 0.2f, Nn, po + 0.4f, 0.75f, beamY - 2.1f, beamY + 0.6f, true); // beam and light panel
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

        // ---------------------------------------------------------------- banner bridges over the straights
        if (!low)
        {
            // (Adi, 27 Nov: more overhead banner bridges, it should look lived in): 5 round the lap on High, 3 on Medium
            // (each searches up to 300 m on for a straight; skipped where there is none)
            float L = (float)t.length;
            float[] want2 = monza ? (high ? new[] { 520f, 1180f, 2650f, 3950f, 4650f } : new[] { 520f, 2650f, 4650f })
                                  : (high ? new[] { L * 0.14f, L * 0.31f, L * 0.48f, L * 0.65f, L * 0.82f } : new[] { L * 0.2f, L * 0.45f, L * 0.7f });
            int bn = 0;
            foreach (float sw2 in want2)
            {
                int i0 = W(Mathf.RoundToInt(sw2 / ds)), found = -1;
                for (int tr = 0; tr < 12 && found < 0; tr++)
                {
                    int i = W(i0 + tr * Mathf.RoundToInt(25f / ds)); bool ok = true;
                    for (int j = -14; j <= 14 && ok; j += 2)
                    {
                        int k = W(i + Mathf.RoundToInt(j * 3f / ds));
                        ok = curvAbs[k] < 1f / 700f && !InPit(k, 1) && !InPit(k, -1);
                    }
                    if (ok && Gap(i, 0) > Mathf.RoundToInt(150f / ds)) found = i;
                }
                if (found < 0) continue;
                SetChunk(found);
                var p = t.pts[found];
                Vector3 c = P(p), T = Tan(p), Nn = Nrm(p), up = Vector3.up;
                float po = Mathf.Min(Off(found, 1), Off(found, -1)) + 0.9f;
                Vector3 cL = c + Nn * po, cR = c - Nn * po;
                if (!Free(cL, 0f) || !Free(cR, 0f)) continue;
                const float deckY = 6.0f;
                for (int sg = -1; sg <= 1; sg += 2)
                {
                    Vector3 pc = c + Nn * (po * sg);
                    Box(A, pc + up * ((deckY + 0.6f) * 0.5f + skirt * 0.5f), Nn, up, T, 0.45f, (deckY + 0.6f) * 0.5f - skirt * 0.5f, 0.45f, TracksideArt.GantryWhite);
                    Box(A, pc - T * 0.0f + up * 3.0f, Nn, up, T, 0.5f, 0.15f, 0.9f, TracksideArt.GantryDark); // base brace
                }
                // deck, parapets, steel underside
                Box(A, c + up * deckY, Nn, up, T, po + 0.6f, 0.25f, 1.1f, TracksideArt.Steel, true);
                Box(A, c + up * (deckY - 0.3f), Nn, up, T, po + 0.6f, 0.08f, 1.1f, TracksideArt.GantryDark, true);
                for (int sd = -1; sd <= 1; sd += 2)
                    Box(A, c + T * (sd * 1.05f) + up * (deckY + 0.7f), Nn, up, T, po + 0.6f, 0.45f, 0.05f, TracksideArt.GantryWhite);
                // banner above the parapet, both faces: two advertisers side by side
                int ab = bn * 5;
                for (int fc = -1; fc <= 1; fc += 2)
                    for (int h2 = 0; h2 < 2; h2++)
                    {
                        float off = (h2 - 0.5f) * (po + 0.6f);
                        aoOn = false;
                        Panel(A, c + T * (fc * 1.12f) + Nn * off, T * fc, (po + 0.6f) * 0.5f, deckY + 0.3f, deckY + 3.0f, TracksideArt.Ad(BoldAds[(ab + h2 + (fc > 0 ? 3 : 0)) % BoldAds.Length]));
                        aoOn = true;
                    }
                bn++;
                AddFoot(cL, T, 1f, 1f, deckY + 0.6f); AddFoot(cR, T, 1f, 1f, deckY + 0.6f); Tall.Add(cL); Tall.Add(cR);
                AddSolid(c, Nn, po + 0.7f, 1.3f, deckY - 0.5f, deckY + 3.1f, true); // deck and banners across the track (a long lens looked straight into one)
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
                    // a 6 cm thick board: top cap and ends, so it reads as a solid panel, not a sheet
                    Vector3 rt = Vector3.Cross(-D, up).normalized * 2.8f, th6 = D * 0.06f;
                    Quad(A, c - rt + up * h, c + rt + up * h, c + rt + th6 + up * h, c - rt + th6 + up * h, up, TracksideArt.Sw(TracksideArt.Steel));
                    Quad(A, c + rt + up * 0.25f, c + rt + th6 + up * 0.25f, c + rt + th6 + up * h, c + rt + up * h, rt, TracksideArt.Sw(TracksideArt.PostDark));
                    Quad(A, c - rt + th6 + up * 0.25f, c - rt + up * 0.25f, c - rt + up * h, c - rt + th6 + up * h, -rt, TracksideArt.Sw(TracksideArt.PostDark));
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
            // braking zones: a pair of black lock-up marks on the asphalt running wide of the line towards the outside of
            // the corner, and dirt thrown over the kerb on the exit; at the slow corners only, a few of them
            {
                var done = new List<int>(); int maxLk = high ? 6 : 4;
                foreach (int apex in corners)
                {
                    if (done.Count >= maxLk || curvAbs[apex] < 1f / 130f) break;
                    bool near = false; foreach (int u in done) if (Gap(u, apex) < Mathf.RoundToInt(150f / ds)) near = true;
                    if (near) continue;
                    done.Add(apex);
                    int sg = Outside(apex), st = Mathf.Max(1, Mathf.RoundToInt(3f / ds));
                    int lenL = Mathf.RoundToInt((14f + 12f * Rand(apex, 81)) / ds), i0 = apex - Mathf.RoundToInt(26f / ds) - lenL; // short: a real flat-spot mark, not a painted lane line
                    float latA = hw * (0.15f + 0.3f * Rand(apex, 82)), latB = hw - 0.9f;
                    for (int m = 0; m < 2; m++)
                        for (int j = 0; j + st <= lenL; j += st)
                        {
                            int ia = W(i0 + j), ib = W(i0 + j + st);
                            float fa = (float)j / lenL, fb = (float)(j + st) / lenL;
                            float la = Mathf.Lerp(latA, latB, fa * fa) - m * 1.55f, lb = Mathf.Lerp(latA, latB, fb * fb) - m * 1.55f;
                            // thin at both ends (the wheel locks, then the driver releases): reads as a fading skid
                            float wa = 0.015f + 0.075f * Mathf.Sin(fa * Mathf.PI), wb = 0.015f + 0.075f * Mathf.Sin(fb * Mathf.PI);
                            if (Rand(apex * 7 + j + m * 131, 83) < 0.12f) continue; // a few gaps where the tyre unloaded
                            var pa = t.pts[ia]; var pb = t.pts[ib];
                            Vector3 Da = Nrm(pa) * sg, Db = Nrm(pb) * sg, ca = P(pa), cb = P(pb);
                            SetChunk(ia);
                            Decal(G, ca + Da * (la - wa), ca + Da * (la + wa), cb + Db * (lb - wb), cb + Db * (lb + wb), 0.05f,
                                  j * ds / 4f, (j + st) * ds / 4f, TracksideArt.GrimeCol(TracksideArt.GrimeTyre));
                        }
                    // dirt over the exit kerb (outside), where cars drop a wheel
                    int e0 = apex + Mathf.RoundToInt(8f / ds), lenD = Mathf.RoundToInt(34f / ds);
                    for (int j = 0; j + st <= lenD; j += st)
                    {
                        int ia = W(e0 + j), ib = W(e0 + j + st);
                        var pa = t.pts[ia]; var pb = t.pts[ib];
                        if (InPit(ia, sg) || InPit(ib, sg)) continue;
                        Vector3 Da = Nrm(pa) * sg, Db = Nrm(pb) * sg;
                        float l0 = hw + kerb * 0.3f, l1 = hw + kerb + 1.3f;
                        if (grid.MinDist(P(pa) + Da * l1) < hw + kerb) continue;
                        SetChunk(ia);
                        Decal(G, P(pa) + Da * l0, P(pa) + Da * l1, P(pb) + Db * l0, P(pb) + Db * l1, 0.058f, j * ds / 6f, (j + st) * ds / 6f, TracksideArt.GrimeCol(TracksideArt.GrimeDirt));
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
            // worn grass where the astroturf meets the lawn: wheels dropped off the turf, the mowers' turn and the rain
            // run-off leave a ragged band of bare soil along its outer edge; patchy along the lap (a slow noise), wider
            // on the outside of the corner exits and at likely impacts (dirt[])
            if (!street && t.def != null && t.def.style == CircuitStyle.Parkland)
            {
                int st = Mathf.Max(1, Mathf.RoundToInt(3f / ds));
                for (int sg = -1; sg <= 1; sg += 2)
                {
                    int s = SI(sg);
                    float Wear(int i) => Mathf.PerlinNoise(i * ds * 0.045f, s * 7.3f + 0.5f);
                    for (int i = 0; i < N; i += st)
                    {
                        int ib = W(i + st);
                        if (InPit(i, sg) || InPit(ib, sg)) continue;
                        float nA = Wear(i), nB = Wear(i + st), dA = dirt[s, i], dB = dirt[s, ib];
                        if (nA < 0.34f && nB < 0.34f && dA < 0.2f && dB < 0.2f) continue;
                        float wA = 0.1f + 1.4f * Mathf.Clamp01(nA - 0.34f) + 0.9f * dA, wB = 0.1f + 1.4f * Mathf.Clamp01(nB - 0.34f) + 0.9f * dB;
                        var pa = t.pts[i]; var pb = t.pts[ib];
                        float ea = hw + (sg > 0 ? t.kerbL[i] : t.kerbR[i]) + 2f, eb = hw + (sg > 0 ? t.kerbL[ib] : t.kerbR[ib]) + 2f;
                        Vector3 Da = Nrm(pa) * sg, Db = Nrm(pb) * sg;
                        if (grid.MinDist(P(pa) + Da * (ea + wA)) < ea || Off(i, sg) < ea + 0.8f) continue; // another leg close by / no verge
                        SetChunk(i);
                        Decal(G, P(pa) + Da * (ea - 0.3f * wA), P(pa) + Da * (ea + 0.7f * wA), P(pb) + Db * (eb - 0.3f * wB), P(pb) + Db * (eb + 0.7f * wB), 0.045f,
                              i * ds / 5f, (i + st) * ds / 5f, TracksideArt.GrimeCol(TracksideArt.GrimeDirt));
                    }
                }
            }
        }

        // ---------------------------------------------------------------- Monza: asphalt run-offs with a painted edge
        // the real run-offs at the Rettifilo, the Roggia, Ascari and outside the Parabolica, between the astroturf and the
        // barrier where the layout has grass (the gravel traps stay); in the atlas mesh, so no extra draw call. (Visual:
        // the tyres still read the layout's surface there.) Drawn like asphalt, all load time:
        //  * the atlas's run-off aggregate tile (TracksideArt.RunOff, one tile per 4 m x ~2 m cell, seamless), a neutral
        //    road grey, with a slow tone drift from the world position (shared corners: no seams);
        //  * columns of ~2 m across, so the vertex-baked light resolves the shade of trees and stands across a 10-20 m
        //    run-off instead of one gradient between its four corners;
        //  * a ragged outer edge (+-0.25 m, a slow noise) and a soft dirt decal over it (the grime layer), so it no longer
        //    ends in a ruler-straight line against the grass.
        if (monza && t.runL != null)
        {
            float[,] zones = { { 790, 1070, 0 }, { 2030, 2270, 0 }, { 3850, 4200, 0 }, { 5050, 5430, 1 } }; // m along the lap, side (0 both, 1 left)
            int st = Mathf.Max(1, Mathf.RoundToInt(4f / ds));
            float Tone(Vector3 q) => 0.93f + 0.1f * Mathf.PerlinNoise(q.x * 0.055f + 31.7f, q.z * 0.055f + 7.9f);
            for (int zn = 0; zn < zones.GetLength(0); zn++)
                for (int sg = -1; sg <= 1; sg += 2)
                {
                    if (zones[zn, 2] != 0f && sg != (int)zones[zn, 2]) continue;
                    var run = sg > 0 ? t.runL : t.runR;
                    int i0 = Mathf.RoundToInt(zones[zn, 0] / ds), i1 = Mathf.RoundToInt(zones[zn, 1] / ds);
                    bool have = false; Vector3 pOut = Vector3.zero, pD = Vector3.zero, pC = Vector3.zero; float pA = 0f, pO = 0f, pRag = 0f;
                    for (int i = i0; i <= i1; i += st)
                    {
                        int k = W(i); var p = t.pts[k]; Vector3 D = Nrm(p) * sg, c = P(p), up = Vector3.up;
                        float a = hw + (sg > 0 ? t.kerbL[k] : t.kerbR[k]) + 2f, o = Off(k, sg) - 1.2f;
                        Vector3 qo = c + D * o;
                        bool ok = run[k] != SurfaceType.Gravel && o - a > 1.5f && !InPit(k, sg) && grid.MinDist(qo) >= o - 0.6f && !Waterfront.InWater(qo.x, qo.z);
                        if (ok && have && Vector3.Dot(qo - pOut, Tan(p)) < 0.3f * st * ds) ok = false; // folds back inside a bend
                        // the ragged outer edge (asphalt only; the painted line keeps to the straight edge 0.35 m inside it)
                        float rag = 0.25f * (Mathf.PerlinNoise(i * ds * 0.11f, zn * 3.1f + sg * 1.7f) * 2f - 1f);
                        if (ok && have)
                        {
                            SetChunk(k);
                            Vector3 y1 = up * 0.032f, y2 = up * 0.038f;
                            float wA = pO - pA, wB = o - a; int nc = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(wA, wB) / 2f));
                            for (int cI = 0; cI < nc; cI++)
                            {
                                float f0 = cI / (float)nc, f1 = (cI + 1) / (float)nc;
                                // the last column carries the ragged edge
                                float lA0 = pA + wA * f0, lA1 = cI == nc - 1 ? pO + pRag : pA + wA * f1;
                                float lB0 = a + wB * f0, lB1 = cI == nc - 1 ? o + rag : a + wB * f1;
                                Vector3 a0 = pC + pD * lA0 + y1, a1 = pC + pD * lA1 + y1, b0 = c + D * lB0 + y1, b1 = c + D * lB1 + y1;
                                int v0 = A.col.Count;
                                Quad(A, a0, a1, b1, b0, up, TracksideArt.RunOff);
                                A.col[v0] *= Tone(a0); A.col[v0 + 1] *= Tone(a1); A.col[v0 + 2] *= Tone(b1); A.col[v0 + 3] *= Tone(b0);
                            }
                            Quad(A, pOut - pD * 0.35f + y2, qo - D * 0.35f + y2, qo - D * 0.05f + y2, pOut - pD * 0.05f + y2, up, TracksideArt.Sw(TracksideArt.LineWhite));
                            // soft edge into the grass: worn soil over the boundary (u = 0 on the asphalt, 1 in the grass)
                            if (decals)
                                Decal(G, pC + pD * (pO + pRag - 0.55f), pC + pD * (pO + pRag + 0.75f), c + D * (o + rag - 0.55f), c + D * (o + rag + 0.75f), 0.045f,
                                      (i - st) * ds / 5f, i * ds / 5f, TracksideArt.GrimeCol(TracksideArt.GrimeDirt));
                        }
                        have = ok;
                        if (ok) { pOut = qo; pD = D; pC = c; pA = a; pO = o; pRag = rag; }
                    }
                }
        }

        // ---------------------------------------------------------------- emit one mesh per material per chunk
        var parent = new GameObject("Trackside").transform;
        parent.SetParent(root, false);
        Late = new GameObject("TracksideLate");
        var atlasMat = WorldBuilder.Mat(Color.white, TracksideArt.Atlas(), 0.1f); glassMat = atlasMat;
        crowdMat = WorldBuilder.Mat(Color.white, TracksideArt.Crowd(), 0.1f);
        Emit(parent, "Trackside", atlasL, atlasMat, null);
        Emit(parent, "Crowd", crowdL, crowdMat, null);
        var standTex = EnvTex.PhotoTex("crowd_standing");
        if (standTex != null)
        {
            peopleMat = WorldBuilder.Mat(Color.white, standTex, 0.1f);
            peopleRs.Clear();
            Emit(parent, "Spectators", peopleL, peopleMat, peopleRs);
        }
        Emit(parent, "Posts", postL, atlasMat, postRs); // thin posts: hidden beyond ~85 m (TracksideLod)
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
        atlasL = crowdL = fenceL = grimeL = postL = peopleL = null; A = C = F = G = Pm = Pp = null;
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
            if (mb.u1.Count == mb.v.Count) mesh.SetUVs(1, mb.u1);
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
                fenceMat.SetFloat("_Veil", 1f); // far away the mesh thins to a veil (MSAA coverage) instead of a solid sheet
                fenceMat.renderQueue = 2450;
            }
            else
            {
                // an opaque grey sheet would be worse than no fence
                foreach (var r in fenceRs) if (r != null) { r.enabled = false; optional.Remove(r); }
            }
        }
        // the crowd on the baked shader: seat-block colours and the people's shimmer (Baked.shader F2_CROWD)
        if (crowdMat != null && crowdMat.shader != null && crowdMat.shader.name == "F2/Baked") crowdMat.EnableKeyword("F2_CROWD");
        crowdMat = null;
        // fence spectators: cut out on the baked shader (F2_PEOPLE), after the opaque world; anything else would show
        // the cards as solid rectangles, so they are hidden then
        if (peopleMat != null)
        {
            if (peopleMat.shader != null && peopleMat.shader.name == "F2/Baked") { peopleMat.EnableKeyword("F2_PEOPLE"); peopleMat.renderQueue = 2450; }
            else foreach (var r in peopleRs) if (r != null) r.enabled = false;
            // load-time diagnostics (fans not showing, 28 Nov): WebGL shot URL fantest=1 solid cards, 2 = solid + 4 m up,
            // 3 = cut-out + 4 m up, 4 = Unlit/Transparent Cutout
            int ft = 0;
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                string url = Application.absoluteURL; int at = url.IndexOf("fantest=");
                if (at >= 0 && at + 8 < url.Length) ft = url[at + 8] - '0';
            }
            if (ft == 1 || ft == 2) { peopleMat.DisableKeyword("F2_PEOPLE"); peopleMat.renderQueue = 2000; }
            if (ft == 4) { var us = Shader.Find("Unlit/Transparent Cutout"); if (us != null) peopleMat.shader = us; }
            if (ft == 2 || ft == 3) foreach (var r in peopleRs) if (r != null) r.transform.position += Vector3.up * 4f;
            if (ft > 0) Debug.Log("Trackside fans: fantest " + ft);
            var tx = peopleMat.mainTexture as Texture2D;
            var sb = new System.Text.StringBuilder("Trackside fans draw: shader ").Append(peopleMat.shader != null ? peopleMat.shader.name : "null")
                .Append(" kw ").Append(peopleMat.IsKeywordEnabled("F2_PEOPLE")).Append(" q ").Append(peopleMat.renderQueue)
                .Append(" tex ").Append(tx != null ? tx.width + "x" + tx.height + " " + tx.format : "null").Append(" renderers ").Append(peopleRs.Count);
            for (int k = 0; k < peopleRs.Count && k < 4; k++)
            {
                var r = peopleRs[k]; if (r == null) { sb.Append(" [null]"); continue; }
                var mf = r.GetComponent<MeshFilter>();
                sb.Append(" [").Append(r.name).Append(" on ").Append(r.enabled).Append(" active ").Append(r.gameObject.activeInHierarchy)
                  .Append(" b ").Append(r.bounds.center.ToString("F0")).Append(r.bounds.size.ToString("F0"))
                  .Append(" v ").Append(mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount : -1)
                  .Append(" c0 ").Append(mf != null && mf.sharedMesh != null && mf.sharedMesh.colors.Length > 0 ? mf.sharedMesh.colors[0].ToString("F2") : "-").Append("]");
            }
            Debug.Log(sb.ToString());
        }
        peopleMat = null; peopleRs.Clear();
        // glass reflections (Baked.shader F2_GLASS) once the atlas material is on the baked shader; TracksideLod switches them
        TracksideLod.Glass = glassMat != null && glassMat.shader != null && glassMat.shader.name == "F2/Baked" && WorldBuilder.Gfx >= 2 ? glassMat : null;
        if ((optional.Count > 0 || postRs.Count > 0) && root != null) TracksideLod.Attach(root, optional.ToArray(), postRs.ToArray());
        glassMat = null; fenceMat = null; fenceRs.Clear(); optional.Clear(); postRs.Clear();
    }
}
