// Trackside.cs — procedural trackside dressing, built from code like the rest of the world:
//   * barriers on both sides: street circuit = concrete walls with red/white painted tops just outside the kerbs,
//     other circuits = metal guardrail (one rail slab on posts) with a wider run-off on the outside of corners;
//     segments are dropped where the offset curve would fold back in tight hairpins or come close to another leg
//   * tyre walls (low-poly hexagonal stacks) in front of the barrier on the outside of the tightest corners
//   * start/finish gantry over track point 0 (two posts, beam, start-light panel)
//   * 2-4 stepped grandstands with a back wall and roof near the start/finish straight, facing the track
//   * plain coloured advertising boards (no text, no brands) behind the barriers on straights
// All geometry is grouped into one mesh per material (Standard shader ignores vertex colours), so the whole lot is
// a handful of draw calls. Physics (x, y) maps to Unity (x, height, y); left normal (nx, ny) -> Unity (nx, 0, ny).
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
        public readonly List<int> tri = new List<int>();

        // quad a-b-c-d given in perimeter order; winding is chosen so the face points along n
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            int k = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
            // Unity: RecalculateNormals gives Cross(b - a, c - a) for triangle (a, b, c)
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) >= 0)
            {
                tri.Add(k); tri.Add(k + 1); tri.Add(k + 2);
                tri.Add(k); tri.Add(k + 2); tri.Add(k + 3);
            }
            else
            {
                tri.Add(k); tri.Add(k + 2); tri.Add(k + 1);
                tri.Add(k); tri.Add(k + 3); tri.Add(k + 2);
            }
        }

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 n)
        {
            int k = v.Count;
            v.Add(a); v.Add(b); v.Add(c);
            uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(0, 1));
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) >= 0) { tri.Add(k); tri.Add(k + 1); tri.Add(k + 2); }
            else { tri.Add(k); tri.Add(k + 2); tri.Add(k + 1); }
        }

        // rectangle centred at fc, spanned by unit vectors u, w with half sizes eu, ew, facing n
        void Face(Vector3 fc, Vector3 n, Vector3 u, Vector3 w, float eu, float ew)
        {
            Quad(fc - u * eu - w * ew, fc + u * eu - w * ew, fc + u * eu + w * ew, fc - u * eu + w * ew, n);
        }

        // oriented box: centre c, orthonormal axes ax, ay (up), az, half extents ex, ey, ez; bottom face optional
        public void Box(Vector3 c, Vector3 ax, Vector3 ay, Vector3 az, float ex, float ey, float ez, bool bottom = false)
        {
            Face(c + ax * ex, ax, ay, az, ey, ez);
            Face(c - ax * ex, -ax, ay, az, ey, ez);
            Face(c + az * ez, az, ax, ay, ex, ey);
            Face(c - az * ez, -az, ax, ay, ex, ey);
            Face(c + ay * ey, ay, ax, az, ex, ez);
            if (bottom) Face(c - ay * ey, -ay, ax, az, ex, ez);
        }

        // vertical hexagonal prism (tyre stack), base centre b, radius r, height h, top capped
        public void Hex(Vector3 b, float r, float h, float rot)
        {
            var ring = new Vector3[6];
            for (int i = 0; i < 6; i++)
            {
                float a = rot + i * Mathf.PI / 3f;
                ring[i] = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            }
            var up = new Vector3(0f, h, 0f);
            for (int i = 0; i < 6; i++)
            {
                Vector3 p = ring[i], q = ring[(i + 1) % 6];
                Vector3 n = (p + q).normalized;
                Quad(b + p, b + q, b + q + up, b + p + up, n);
            }
            Vector3 t0 = b + up + ring[0];
            for (int i = 1; i < 5; i++) Tri(t0, b + up + ring[i], b + up + ring[i + 1], Vector3.up);
        }
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

        // distance (Unity x/z plane) to the nearest centreline sample; returns cell size if none is that close
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
                        float ex = (float)q.x - p.x, ez = (float)q.y - p.z;
                        float d = ex * ex + ez * ez;
                        if (d < best) best = d;
                    }
                }
            return Mathf.Sqrt(best);
        }
    }

    // ---------------------------------------------------------------- material buckets (one mesh per material)
    class Bucket { public Material mat; public bool shadows; public MB mb = new MB(); }

    static Vector3 P(TrackPoint p, float h = 0f) { return new Vector3((float)p.x, h, (float)p.y); }
    static Vector3 Tan(TrackPoint p) { return new Vector3((float)p.tx, 0f, (float)p.ty).normalized; }
    static Vector3 Nrm(TrackPoint p) { return new Vector3((float)p.nx, 0f, (float)p.ny).normalized; }

    public static void Build(Track t, Transform root, bool streetCircuit)
    {
        if (t == null || t.N < 8 || root == null) return;
        int N = t.N;
        float hw = (float)t.width * 0.5f;
        float kerb = WorldBuilder.Kerb;
        float minClear = hw + kerb + 0.5f; // nothing may come closer than this to any centreline sample
        var grid = new Grid(t, 25f);

        var buckets = new Dictionary<string, Bucket>();
        Bucket B(string key, Color c, float smooth, float metal, bool shadows)
        {
            Bucket b;
            if (!buckets.TryGetValue(key, out b))
            {
                b = new Bucket { mat = WorldBuilder.Mat(c, null, smooth, metal), shadows = shadows };
                buckets[key] = b;
            }
            return b;
        }
        var concrete = B("Concrete", new Color(0.66f, 0.66f, 0.63f), 0.08f, 0f, false).mb;
        var paintRed = B("PaintRed", new Color(0.78f, 0.1f, 0.1f), 0.3f, 0f, false).mb;
        var paintWhite = B("PaintWhite", new Color(0.92f, 0.92f, 0.9f), 0.3f, 0f, false).mb;
        var rail = B("Rail", new Color(0.72f, 0.74f, 0.77f), 0.6f, 0.6f, false).mb;
        var post = B("Post", new Color(0.32f, 0.33f, 0.35f), 0.3f, 0.3f, false).mb;
        var tyres = B("Tyres", new Color(0.06f, 0.06f, 0.065f), 0.15f, 0f, false).mb;
        var gantryMetal = B("GantryMetal", new Color(0.85f, 0.86f, 0.88f), 0.45f, 0.4f, true).mb;
        var gantryDark = B("GantryDark", new Color(0.08f, 0.08f, 0.09f), 0.3f, 0f, true).mb;
        var lights = B("StartLights", new Color(0.95f, 0.08f, 0.05f), 0.9f, 0f, true).mb;
        var standFrame = B("StandFrame", new Color(0.7f, 0.7f, 0.68f), 0.1f, 0f, true).mb;
        var seatA = B("SeatsA", new Color(0.12f, 0.3f, 0.65f), 0.2f, 0f, true).mb;
        var seatB = B("SeatsB", new Color(0.85f, 0.85f, 0.85f), 0.2f, 0f, true).mb;
        var roof = B("Roof", new Color(0.9f, 0.9f, 0.92f), 0.4f, 0.2f, true).mb;
        var ads = new MB[]
        {
            B("AdBlue", new Color(0.1f, 0.35f, 0.8f), 0.25f, 0f, false).mb,
            B("AdYellow", new Color(0.95f, 0.78f, 0.1f), 0.25f, 0f, false).mb,
            B("AdGreen", new Color(0.1f, 0.6f, 0.3f), 0.25f, 0f, false).mb,
            B("AdOrange", new Color(0.95f, 0.45f, 0.1f), 0.25f, 0f, false).mb,
        };

        // ---------------------------------------------------------------- barrier offsets
        // smoothed "outside of a corner" factor per side (0 straight / inside, 1 outside of a real corner)
        var outL = new float[N]; var outR = new float[N];
        {
            var rawL = new float[N]; var rawR = new float[N];
            for (int i = 0; i < N; i++)
            {
                float c = (float)t.pts[i].curv;
                rawL[i] = Mathf.Clamp01(-c * 150f); // right turn -> left side is the outside
                rawR[i] = Mathf.Clamp01(c * 150f);
            }
            const int win = 12;
            for (int i = 0; i < N; i++)
            {
                float sl = 0f, sr = 0f;
                for (int j = -win; j <= win; j++) { int k = ((i + j) % N + N) % N; sl += rawL[k]; sr += rawR[k]; }
                outL[i] = sl / (2 * win + 1); outR[i] = sr / (2 * win + 1);
            }
        }
        float baseOff = streetCircuit ? hw + kerb + 1.5f : hw + kerb + 8f;
        float extraOut = streetCircuit ? 0f : 4f;
        // lateral offset of the barrier's track-facing side at sample i on side sg (+1 left, -1 right)
        float Off(int i, int sg)
        {
            float o = baseOff + extraOut * (sg > 0 ? outL[i] : outR[i]);
            // inside of a bend: keep well inside the radius so the offset curve cannot fold back
            float kIn = 0f;
            for (int j = -4; j <= 4; j++) { int k = ((i + j) % N + N) % N; kIn = Mathf.Max(kIn, sg * (float)t.pts[k].curv); }
            if (kIn > 1e-5f) o = Mathf.Min(o, 0.8f / kIn);
            return o;
        }

        // ---------------------------------------------------------------- barriers
        int stride = streetCircuit ? Mathf.Max(2, Mathf.CeilToInt(N / 1000f)) : Mathf.Max(3, Mathf.CeilToInt(N / 1300f));
        var samples = new List<int>();
        for (int i = 0; i < N; i += stride) samples.Add(i);
        if (N - samples[samples.Count - 1] < Mathf.Max(1, stride / 2)) samples.RemoveAt(samples.Count - 1); // avoid a sliver at the seam
        samples.Add(N); // closes the loop (index N == 0)

        float wallH = 1.0f, paintH = 0.25f, wallT = 0.5f;           // street wall
        float railLo = 0.35f, railHi = 0.85f, railT = 0.12f;          // guardrail
        for (int sg = -1; sg <= 1; sg += 2)
        {
            bool havePrev = false;
            Vector3 prevIn = Vector3.zero, prevDir = Vector3.zero;
            int seg = 0, sampleCount = 0;
            for (int si = 0; si < samples.Count; si++)
            {
                int i = samples[si] % N;
                var p = t.pts[i];
                float o = Off(i, sg);
                Vector3 dir = Nrm(p) * sg; // outward, away from the track
                Vector3 inner = P(p) + dir * o;
                bool ok = o >= minClear && grid.MinDist(inner) >= minClear;
                if (ok && havePrev)
                {
                    // the offset curve must keep advancing along the track direction (no fold-back in hairpins)
                    float adv = Vector3.Dot(inner - prevIn, Tan(p));
                    if (adv < 0.1f * stride * (float)t.ds) ok = false;
                }
                if (!ok)
                {
                    if (havePrev && streetCircuit && sampleCount > 1) EndCap(concrete, prevIn, prevDir, wallT, wallH, false);
                    havePrev = false; sampleCount = 0;
                    continue;
                }
                if (!havePrev)
                {
                    if (streetCircuit) EndCap(concrete, inner, dir, wallT, wallH, true);
                    havePrev = true; prevIn = inner; prevDir = dir; sampleCount = 1;
                    if (!streetCircuit) Post(post, inner, dir, Tan(p), railT, railHi);
                    continue;
                }

                Vector3 mid = (prevDir + dir).normalized;
                Vector3 up = Vector3.up;
                if (streetCircuit)
                {
                    Vector3 a0 = prevIn, b0 = inner;
                    Vector3 a1 = prevIn + prevDir * wallT, b1 = inner + dir * wallT;
                    float hp = wallH - paintH;
                    var paint = (seg & 1) == 0 ? paintRed : paintWhite;
                    concrete.Quad(a0, b0, b0 + up * hp, a0 + up * hp, -mid);                          // inner face, bare
                    paint.Quad(a0 + up * hp, b0 + up * hp, b0 + up * wallH, a0 + up * wallH, -mid);  // inner face, painted band
                    paint.Quad(a0 + up * wallH, b0 + up * wallH, b1 + up * wallH, a1 + up * wallH, up); // top
                    concrete.Quad(a1, b1, b1 + up * wallH, a1 + up * wallH, mid);                     // outer face
                }
                else
                {
                    Vector3 a0 = prevIn, b0 = inner;
                    Vector3 a1 = prevIn + prevDir * railT, b1 = inner + dir * railT;
                    rail.Quad(a0 + up * railLo, b0 + up * railLo, b0 + up * railHi, a0 + up * railHi, -mid);
                    rail.Quad(a0 + up * railHi, b0 + up * railHi, b1 + up * railHi, a1 + up * railHi, up);
                    rail.Quad(a1 + up * railLo, b1 + up * railLo, b1 + up * railHi, a1 + up * railHi, mid);
                    if ((seg & 1) == 1) Post(post, inner, dir, Tan(p), railT, railHi);
                }
                seg++; sampleCount++;
                prevIn = inner; prevDir = dir;
            }
            if (havePrev && streetCircuit && sampleCount > 1) EndCap(concrete, prevIn, prevDir, wallT, wallH, false);
        }

        // ---------------------------------------------------------------- tyre walls on the outside of the tightest corners
        {
            var cand = new List<int>();
            const float kTight = 1f / 45f;
            for (int i = 0; i < N; i++)
            {
                float k = Mathf.Abs((float)t.pts[i].curv);
                if (k < kTight) continue;
                bool peak = true;
                for (int j = -20; j <= 20 && peak; j++)
                {
                    if (j == 0) continue;
                    float kj = Mathf.Abs((float)t.pts[((i + j) % N + N) % N].curv);
                    if (kj > k || (kj == k && j < 0)) peak = false;
                }
                if (peak) cand.Add(i);
            }
            cand.Sort((a, b) => Mathf.Abs((float)t.pts[b].curv).CompareTo(Mathf.Abs((float)t.pts[a].curv)));
            var used = new List<int>();
            const int maxCorners = 8, halfSpan = 6;
            const float spacing = 0.62f, radius = 0.31f, stackH = 1.0f;
            for (int ci = 0; ci < cand.Count && used.Count < maxCorners; ci++)
            {
                int apex = cand[ci];
                bool near = false;
                foreach (int u in used) { int d = Mathf.Abs(u - apex); d = Mathf.Min(d, N - d); if (d < 30) { near = true; break; } }
                if (near) continue;
                used.Add(apex);
                int sg = t.pts[apex].curv > 0 ? -1 : 1; // outside of the corner
                float carry = 0f; int count = 0;
                for (int j = -halfSpan; j < halfSpan; j++)
                {
                    int i0 = ((apex + j) % N + N) % N, i1 = ((apex + j + 1) % N + N) % N;
                    var p0 = t.pts[i0]; var p1 = t.pts[i1];
                    Vector3 a = P(p0) + Nrm(p0) * sg * (Off(i0, sg) - radius - 0.1f);
                    Vector3 b = P(p1) + Nrm(p1) * sg * (Off(i1, sg) - radius - 0.1f);
                    float len = Vector3.Distance(a, b);
                    if (len < 1e-3f) continue;
                    float d = carry;
                    while (d < len)
                    {
                        Vector3 c = Vector3.Lerp(a, b, d / len);
                        if (grid.MinDist(c) >= minClear)
                        {
                            tyres.Hex(c, radius, stackH, count * 0.7f);
                            count++;
                        }
                        d += spacing;
                    }
                    carry = d - len;
                }
            }
        }

        // ---------------------------------------------------------------- start/finish gantry at track point 0
        {
            var p = t.pts[0];
            Vector3 c = P(p), T = Tan(p), Nn = Nrm(p), up = Vector3.up;
            float po = streetCircuit ? hw + kerb + 0.6f : hw + kerb + 2.5f;
            const float postH = 7.2f, beamY = 6.8f;
            for (int sg = -1; sg <= 1; sg += 2)
                gantryMetal.Box(c + Nn * (po * sg) + up * (postH * 0.5f), Nn, up, T, 0.3f, postH * 0.5f, 0.3f);
            gantryMetal.Box(c + up * beamY, Nn, up, T, po + 0.3f, 0.4f, 0.5f, true);
            // light panel hanging under the beam, facing the cars approaching the line (they come from -T)
            Vector3 panel = c + up * (beamY - 1.1f) - T * 0.3f;
            gantryDark.Box(panel, Nn, up, T, 2.4f, 0.7f, 0.15f, true);
            for (int col = 0; col < 5; col++)
                for (int row = 0; row < 2; row++)
                {
                    Vector3 lc = panel + Nn * ((col - 2) * 0.9f) + up * (row == 0 ? 0.28f : -0.28f) - T * 0.2f;
                    lights.Box(lc, Nn, up, T, 0.17f, 0.17f, 0.05f, true);
                }
        }

        // ---------------------------------------------------------------- grandstands near the start/finish straight
        {
            // (metres from the line along the track, side): + side = left of the racing direction
            var spots = new (float s, int sg)[] { (-95f, -1), (-40f, -1), (25f, -1), (-65f, 1) };
            const int rows = 8;
            const float rowD = 0.85f, rowH = 0.45f, baseH = 1.2f, halfL = 18f;
            float depth = rows * rowD;
            foreach (var spot in spots)
            {
                int i = ((Mathf.RoundToInt(spot.s / (float)t.ds) % N) + N) % N;
                var p = t.pts[i];
                Vector3 T = Tan(p), D = Nrm(p) * spot.sg, up = Vector3.up;
                float barrier = baseOff + extraOut * (spot.sg > 0 ? outL[i] : outR[i]);
                float gap = streetCircuit ? 3f : 4f;
                bool placed = false;
                for (int attempt = 0; attempt < 3 && !placed; attempt++)
                {
                    Vector3 O = P(p) + D * (barrier + gap + attempt * 6f);
                    bool clear = true;
                    for (int q = 0; q <= 6 && clear; q++)
                    {
                        float along = -halfL - 1f + q * (2f * halfL + 2f) / 6f;
                        if (grid.MinDist(O + T * along) < hw + kerb + 3f) clear = false;
                        if (grid.MinDist(O + T * along + D * (depth + 0.5f)) < hw + kerb + 3f) clear = false;
                    }
                    if (!clear) continue;
                    placed = true;
                    float topH = baseH + rows * rowH;
                    for (int r = 0; r < rows; r++)
                    {
                        float h = baseH + (r + 1) * rowH;
                        var m = (r & 1) == 0 ? seatA : seatB;
                        m.Box(O + D * (r * rowD + rowD * 0.5f) + up * (h * 0.5f), T, up, D, halfL, h * 0.5f, rowD * 0.5f);
                    }
                    float wallTop = topH + 3.2f;
                    standFrame.Box(O + D * (depth + 0.15f) + up * (wallTop * 0.5f), T, up, D, halfL + 0.3f, wallTop * 0.5f, 0.15f);
                    float roofD = depth + 1.8f;
                    roof.Box(O + D * (depth + 0.3f - roofD * 0.5f) + up * (wallTop + 0.12f), T, up, D, halfL + 0.8f, 0.12f, roofD * 0.5f, true);
                    // slim front columns at the ends
                    for (int e = -1; e <= 1; e += 2)
                        standFrame.Box(O + T * (e * (halfL + 0.4f)) + D * 0.2f + up * (wallTop * 0.5f), T, up, D, 0.12f, wallTop * 0.5f, 0.12f);
                }
            }
        }

        // ---------------------------------------------------------------- plain advertising boards on straights
        {
            int adCount = 0, adColour = 0, sideToggle = 0;
            const int maxBoards = 80, perBlock = 4;
            int startSkip = Mathf.CeilToInt(130f / (float)t.ds); // grandstand / gantry area
            int blockStep = Mathf.Max(perBlock * 3 + 1, Mathf.RoundToInt(120f / (float)t.ds));
            for (int i = 0; i < N && adCount < maxBoards; i += blockStep)
            {
                if (i < startSkip || i > N - startSkip) continue;
                float kMax = 0f;
                for (int j = -15; j <= 15 + perBlock * 3; j++) kMax = Mathf.Max(kMax, Mathf.Abs((float)t.pts[((i + j) % N + N) % N].curv));
                if (kMax > 1f / 600f) continue;
                int sg = (sideToggle++ & 1) == 0 ? 1 : -1;
                for (int b = 0; b < perBlock && adCount < maxBoards; b++)
                {
                    int k = (i + b * 3) % N;
                    var p = t.pts[k];
                    Vector3 T = Tan(p), D = Nrm(p) * sg, up = Vector3.up;
                    float behind = streetCircuit ? wallT + 0.4f : railT + 0.6f;
                    float h = streetCircuit ? 1.9f : 1.3f;
                    Vector3 c = P(p) + D * (Off(k, sg) + behind) + up * (h * 0.5f);
                    if (grid.MinDist(c) < minClear) continue;
                    ads[adColour++ % ads.Length].Box(c, T, up, D, 2.8f, h * 0.5f, 0.05f);
                    adCount++;
                }
            }
        }

        // ---------------------------------------------------------------- emit one mesh object per material
        var parent = new GameObject("Trackside").transform;
        parent.SetParent(root, false);
        foreach (var kv in buckets) Emit(parent, kv.Key, kv.Value.mb, kv.Value.mat, kv.Value.shadows);
    }

    // guardrail post: small box just behind the rail
    static void Post(MB mb, Vector3 inner, Vector3 dir, Vector3 tan, float railT, float railHi)
    {
        float h = railHi + 0.05f;
        mb.Box(inner + dir * (railT + 0.07f) + Vector3.up * (h * 0.5f), tan, Vector3.up, dir, 0.06f, h * 0.5f, 0.07f);
    }

    // street wall end face (start = faces backwards along the track, end = faces forwards)
    static void EndCap(MB mb, Vector3 inner, Vector3 dir, float thick, float h, bool start)
    {
        Vector3 along = Vector3.Cross(dir, Vector3.up); // horizontal, perpendicular to dir
        Vector3 a = inner, b = inner + dir * thick, up = Vector3.up * h;
        // faces are back-face culled, so emit the cap facing both ways (it is only 4 triangles)
        mb.Quad(a, b, b + up, a + up, along);
        mb.Quad(a, b, b + up, a + up, -along);
    }

    static void Emit(Transform parent, string name, MB mb, Material mat, bool shadows)
    {
        if (mb.tri.Count == 0) return;
        var mesh = new Mesh();
        mesh.name = "Trackside_" + name;
        if (mb.v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(mb.v);
        mesh.SetUVs(0, mb.uv);
        mesh.SetTriangles(mb.tri, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = true;
    }
}
