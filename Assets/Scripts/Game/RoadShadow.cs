// RoadShadow.cs — a sharp sun-shadow mask for the road and kerbs, made once when the track loads (no per-frame cost).
// The baked vertex light (LightBake.cs) has road rows every ~2 m, so the shadows of trees, stands, bridges and
// fences on the asphalt come out as soft blobs. This draws them properly, in track space:
//   * texture: U = across the road (road + kerbs + 2 m of verge each side, ~0.25 m per texel), V = along the lap
//     (~0.3 m per texel). The lap is cut into strips laid side by side (<= 2048 x 2048), R8 with mips (~2-3 MB);
//   * occluders, taken from whatever the world holds (so any track works, no lists to keep up to date):
//       - every opaque mesh more than 0.5 m above the ground (stands, pit building, walls, bridges, gantry, buildings,
//         boards; terrain only where it rises > 3 m, e.g. raised forest canopy): each triangle is projected along the
//         sun onto the ground and filled, with a soft edge that widens with its height (penumbra);
//       - alpha-tested meshes (catch fences, F2/BakedCutout): the same, letting half the light through;
//       - tree cards ("Trees" meshes, foot-left / foot-right / top-right / top-left quads): each tree becomes a crown
//         (ellipsoid; cone for spruce, flat umbrella for pine, fronds for palms) plus its trunk, with leaf gaps from
//         value noise in the crown's silhouette plane, so the shade is dappled (gaps more open towards the crown edge,
//         flecks softer the higher the crown); the projection stretches them along the sun as real sun flecks;
//   * the shadows are first drawn into a world grid (0.2 m cells, in 6.4 m tiles, only tiles under the mask exist),
//     then read into the track-space texture.
// The road's vertex colours are then split: rgb = sky/ambient light only, a = the bake's own sun visibility with the
// part the mask already explains lifted (so its blurry blob does not double the sharp shadow). Road.shader adds
// _F2SunLin x N.L x a x mask back. Night or no sun: nothing is built, the road keeps its plain baked light.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public static class RoadShadow
{
    public static Texture2D Mask;
    public static float BuildMs;
    const float VergeReach = 12f;
    static readonly int TexId = Shader.PropertyToID("_F2RoadShadow"), ParId = Shader.PropertyToID("_F2RoadShadowP"), SunId = Shader.PropertyToID("_F2SunLin");

    // no mask: white texture, no sun split (Road.shader then uses the baked light as it is)
    public static void Reset()
    {
        Shader.SetGlobalTexture(TexId, Texture2D.whiteTexture);
        Shader.SetGlobalVector(ParId, new Vector4(0f, 1f, 0f, 0f));
        Shader.SetGlobalVector(SunId, Vector4.zero);
    }

    // ---------------------------------------------------------------- world grid of light (255 = sun, 0 = shade)
    const float Cell = 0.2f; const int TC = 32; const float TileW = Cell * TC;
    static float gx0, gz0; static int gnx, gnz; static int[] tileOf; static byte[] pool;
    static Vector3 s; static Vector2 sH; static float sy; // towards the sun; its horizontal part; its height

    static void MarkTile(float x, float z, List<int> order)
    {
        int ti = (int)((x - gx0) / TileW), tj = (int)((z - gz0) / TileW);
        if (ti < 0 || tj < 0 || ti >= gnx || tj >= gnz) return;
        int k = tj * gnx + ti; if (tileOf[k] >= 0) return;
        tileOf[k] = order.Count; order.Add(k);
    }
    static int CellLight(int i, int j)
    {
        if (i < 0 || j < 0) return 255;
        int ti = i >> 5, tj = j >> 5; if (ti >= gnx || tj >= gnz) return 255;
        int t = tileOf[tj * gnx + ti]; if (t < 0) return 255;
        return pool[(t << 10) + ((j & 31) << 5) + (i & 31)];
    }
    static float LightAt(float x, float z)
    {
        float fx = (x - gx0) / Cell - 0.5f, fz = (z - gz0) / Cell - 0.5f;
        int i = Mathf.FloorToInt(fx), j = Mathf.FloorToInt(fz); float a = fx - i, b = fz - j;
        float l0 = CellLight(i, j) + (CellLight(i + 1, j) - CellLight(i, j)) * a;
        float l1 = CellLight(i, j + 1) + (CellLight(i + 1, j + 1) - CellLight(i, j + 1)) * a;
        return l0 + (l1 - l0) * b;
    }
    // the tiles a world rectangle touches, as cell ranges per existing tile: FillTri / FillTree(tileBase, i0, i1, j0, j1,
    // first cell centre x, z) with the shape in the static fields below (no closures: this runs per triangle)
    static void ForTiles(float minX, float minZ, float maxX, float maxZ, bool tree)
    {
        int ci0 = Mathf.Max(0, Mathf.FloorToInt((minX - gx0) / Cell)), cj0 = Mathf.Max(0, Mathf.FloorToInt((minZ - gz0) / Cell));
        int ci1 = Mathf.Min(gnx * TC - 1, Mathf.FloorToInt((maxX - gx0) / Cell)), cj1 = Mathf.Min(gnz * TC - 1, Mathf.FloorToInt((maxZ - gz0) / Cell));
        if (ci1 < ci0 || cj1 < cj0) return;
        for (int tj = cj0 >> 5; tj <= cj1 >> 5; tj++)
            for (int ti = ci0 >> 5; ti <= ci1 >> 5; ti++)
            {
                int t = tileOf[tj * gnx + ti]; if (t < 0) continue;
                int i0 = Mathf.Max(ci0, ti * TC) - ti * TC, i1 = Mathf.Min(ci1, ti * TC + TC - 1) - ti * TC;
                int j0 = Mathf.Max(cj0, tj * TC) - tj * TC, j1 = Mathf.Min(cj1, tj * TC + TC - 1) - tj * TC;
                float cx0 = gx0 + (ti * TC + 0.5f) * Cell, cz0 = gz0 + (tj * TC + 0.5f) * Cell;
                if (tree) FillTree(t << 10, i0, i1, j0, j1, cx0, cz0); else FillTri(t << 10, i0, i1, j0, j1, cx0, cz0);
            }
    }
    static bool AnyTile(float minX, float minZ, float maxX, float maxZ)
    {
        int ti0 = Mathf.Max(0, (int)((minX - gx0) / TileW)), tj0 = Mathf.Max(0, (int)((minZ - gz0) / TileW));
        int ti1 = Mathf.Min(gnx - 1, (int)((maxX - gx0) / TileW)), tj1 = Mathf.Min(gnz - 1, (int)((maxZ - gz0) / TileW));
        for (int tj = tj0; tj <= tj1; tj++) for (int ti = ti0; ti <= ti1; ti++) if (tileOf[tj * gnx + ti] >= 0) return true;
        return false;
    }
    static float H(float x, float z) => WorldBuilder.H(x, z);
    // where a point lands on the ground along the sun ray, and the ground height there
    static Vector2 Land(Vector3 p, float g) { float d = Mathf.Max(0f, p.y - g) / sy; return new Vector2(p.x - sH.x * d, p.z - sH.y * d); }

    // ---------------------------------------------------------------- build
    public static void Build(Track t, GameObject root, MeshRenderer road, Light sun)
    {
        Reset();
        if (t == null || road == null || sun == null) return;
        s = (-sun.transform.forward).normalized; sy = s.y;
        if (sy < 0.06f || sun.intensity < 0.4f) return; // night (moon) or the sun on the horizon: keep the plain bake
        var sw = System.Diagnostics.Stopwatch.StartNew();
        sH = new Vector2(s.x, s.z);

        // layout: C texels across, strips of Hs rows along, S strips side by side
        // the mask also covers the verges and run-offs (VergeReach m beyond the road) so Ground.cginc can continue the shadows onto the grass
        float L = (float)(t.N * t.ds), half = (float)t.width * 0.5f + WorldBuilder.Kerb + 2f + VergeReach;
        int C = Mathf.Clamp(Mathf.CeilToInt(2f * half / 0.27f / 4f) * 4, 32, 192);
        int S = Mathf.Max(1, 2048 / C);
        int R = Mathf.CeilToInt(L / 0.3f);
        S = Mathf.Min(S, Mathf.Max(1, Mathf.CeilToInt(R / 256f)));
        int Hs = Mathf.Min(2048, Mathf.CeilToInt((float)R / S));
        float dA = L / (S * Hs), dL = 2f * half / C;
        int W = S * C;

        // world tile grid over the track (+ margin), tiles marked under every texel
        var bb = t.Bounds(); const float M = 20f;
        gx0 = (float)bb.minX - M; gz0 = (float)bb.minY - M;
        gnx = Mathf.CeilToInt(((float)(bb.maxX - bb.minX) + 2 * M) / TileW) + 1; gnz = Mathf.CeilToInt(((float)(bb.maxY - bb.minY) + 2 * M) / TileW) + 1;
        tileOf = new int[gnx * gnz]; for (int k = 0; k < tileOf.Length; k++) tileOf[k] = -1;
        var order = new List<int>(4096);
        int rows = S * Hs;
        var rowP = new Vector4[rows]; // per row: centre x, z, left normal x, z
        for (int r = 0; r < rows; r++)
        {
            float a = (r + 0.5f) * dA / (float)t.ds; int i0 = (int)a; float u = a - i0;
            var p0 = t.pts[i0 % t.N]; var p1 = t.pts[(i0 + 1) % t.N];
            float nx = Mathf.Lerp((float)p0.nx, (float)p1.nx, u), nz = Mathf.Lerp((float)p0.ny, (float)p1.ny, u), nl = Mathf.Sqrt(nx * nx + nz * nz);
            rowP[r] = new Vector4(Mathf.Lerp((float)p0.x, (float)p1.x, u), Mathf.Lerp((float)p0.y, (float)p1.y, u), nx / Mathf.Max(nl, 1e-4f), nz / Mathf.Max(nl, 1e-4f));
            if ((r & 3) != 0 && r != rows - 1) continue; // every 1.2 m along is enough to mark the 6.4 m tiles
            var q = rowP[r];
            for (float l = -half - 0.6f; l <= half + 0.6f; l += 1.2f)
            {
                float x = q.x + q.z * l, z = q.y + q.w * l;
                MarkTile(x - 0.6f, z - 0.6f, order); MarkTile(x + 0.6f, z - 0.6f, order); MarkTile(x - 0.6f, z + 0.6f, order); MarkTile(x + 0.6f, z + 0.6f, order);
            }
        }
        pool = new byte[order.Count << 10];
        for (int k = 0; k < pool.Length; k++) pool[k] = 255;
        Noise();
        U = Vector3.Cross(s, Vector3.up).normalized; V = Vector3.Cross(U, s);

        // occluders
        int tris = 0, trees = 0;
        var lodSkip = new HashSet<Renderer>();
        foreach (var lg in root.GetComponentsInChildren<LODGroup>(true))
        {
            var lods = lg.GetLODs();
            for (int l = 1; l < lods.Length; l++) foreach (var r in lods[l].renderers) if (r != null) lodSkip.Add(r);
        }
        foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (mr == road || !mr.enabled || !mr.gameObject.activeInHierarchy || lodSkip.Contains(mr)) continue;
            var mf = mr.GetComponent<MeshFilter>(); if (mf == null) continue;
            var mesh = mf.sharedMesh; if (mesh == null || !mesh.isReadable) continue;
            var mat = mr.sharedMaterial; if (mat == null || mat.renderQueue >= 2500) continue; // see-through: no shadow
            string sn = mat.shader != null ? mat.shader.name : "";
            if (sn == "F2/Road" || mat.IsKeywordEnabled("_EMISSION")) continue;
            // quick reject: the shadow of the bounds reaches no tile
            var b = mr.bounds; float reach = (b.max.y - b.min.y) / sy;
            if (!AnyTile(Mathf.Min(b.min.x, b.min.x - sH.x * reach), Mathf.Min(b.min.z, b.min.z - sH.y * reach),
                         Mathf.Max(b.max.x, b.max.x - sH.x * reach), Mathf.Max(b.max.z, b.max.z - sH.y * reach))) continue;
            var v = mesh.vertices; var m = mf.transform.localToWorldMatrix;
            if (!m.isIdentity) for (int k = 0; k < v.Length; k++) v[k] = m.MultiplyPoint3x4(v[k]);
            if (mf.name == "Trees") { trees += TreeCards(v, mesh.uv); continue; }
            float T = 0f, minH = 0.5f;
            if (sn == "F2/BakedCutout") T = 0.5f;                       // chain-link fences
            else if (sn == "F2/Foliage") T = 0.3f;                      // hedges, bushes
            else if (sn == "F2/Ground" || mf.name == "Terrain" || mf.name.StartsWith("EnvRealLand")) minH = 3f; // only where it rises
            tris += Solid(v, mesh.triangles, T, minH);
        }

        // read the world grid into the track-space texture
        var px = new byte[W * Hs];
        for (int r = 0; r < rows; r++)
        {
            var q = rowP[r]; int st = r / Hs, row = r - st * Hs, o = row * W + st * C;
            for (int j = 0; j < C; j++)
            {
                float l = -half + (j + 0.5f) * dL;
                px[o + j] = (byte)Mathf.Clamp(LightAt(q.x + q.z * l, q.y + q.w * l) + 0.5f, 0f, 255f);
            }
        }

        // split the road's baked light into ambient (rgb) and sun visibility (a)
        Color sunLin = sun.color.linear * sun.intensity;
        var rmf = road.GetComponent<MeshFilter>();
        SplitRoad(rmf != null ? rmf.sharedMesh : null, sunLin, px, W, C, Hs, dA, dL, half);

        if (Mask != null) Object.Destroy(Mask);
        Mask = new Texture2D(W, Hs, TextureFormat.R8, true, true) { name = "RoadShadow", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        Mask.SetPixelData(px, 0);
        Mask.Apply(true, true);
        Shader.SetGlobalTexture(TexId, Mask);
        // x: strips per metre along, y: 1 / strips, z: 1 / width covered (m), w: half that width (m)
        Shader.SetGlobalVector(ParId, new Vector4(1f / (Hs * dA), 1f / S, 1f / (2f * half), half));
        Shader.SetGlobalVector(SunId, new Vector4(sunLin.r, sunLin.g, sunLin.b, 1f));
        pool = null; tileOf = null;
        BuildMs = (float)sw.Elapsed.TotalMilliseconds;
        Debug.Log("RoadShadow: " + W + "x" + Hs + " (" + S + " strips), " + trees + " trees, " + tris + " triangles, " + order.Count + " tiles, " + BuildMs.ToString("F0") + " ms");
    }

    // ---------------------------------------------------------------- solid and see-through meshes
    static int Solid(Vector3[] v, int[] tri, float T, float minH)
    {
        var gh = new float[v.Length];
        for (int k = 0; k < v.Length; k++) gh[k] = v[k].y - H(v[k].x, v[k].z);
        int n = 0;
        for (int k = 0; k + 2 < tri.Length; k += 3)
        {
            int ia = tri[k], ib = tri[k + 1], ic = tri[k + 2];
            if (gh[ia] < minH && gh[ib] < minH && gh[ic] < minH) continue; // ground, road, kerbs, decals, low stuff
            Vector3 A = v[ia], B = v[ib], Cc = v[ic];
            Vector3 c = (A + B + Cc) * (1f / 3f);
            Vector2 lc = Land(c, c.y - (gh[ia] + gh[ib] + gh[ic]) * (1f / 3f));
            float g = H(lc.x, lc.y) + 0.03f;
            float dist = Mathf.Max(0f, c.y - g) / sy;
            Tri(Land(A, g), Land(B, g), Land(Cc, g), Mathf.Max(0.22f, 0.04f + 0.012f * dist), T);
            n++;
        }
        return n;
    }

    // fill a projected triangle: 1 inside, a soft rim of width pen outside it only (shared edges leave no seam)
    static void Tri(Vector2 a, Vector2 b, Vector2 c, float pen, float T)
    {
        float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        if (Mathf.Abs(area) < 1e-4f) return;
        if (area < 0) { var tmp = b; b = c; c = tmp; }
        // outward unit normals (counter-clockwise) and offsets: sd = n.q - o
        Vector2 e0 = b - a, e1 = c - b, e2 = a - c;
        Vector2 n0 = new Vector2(e0.y, -e0.x).normalized, n1 = new Vector2(e1.y, -e1.x).normalized, n2 = new Vector2(e2.y, -e2.x).normalized;
        float o0 = Vector2.Dot(n0, a), o1 = Vector2.Dot(n1, b), o2 = Vector2.Dot(n2, c);
        float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - pen, maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + pen;
        float minZ = Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - pen, maxZ = Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + pen;
        if (maxX - minX > 400f || maxZ - minZ > 400f) return;
        tn0 = n0; tn1 = n1; tn2 = n2; to0 = o0; to1 = o1; to2 = o2; tPen = pen; tIp = 1f / pen; tDark = 1f - T;
        ForTiles(minX, minZ, maxX, maxZ, false);
    }
    static Vector2 tn0, tn1, tn2; static float to0, to1, to2, tPen, tIp, tDark;
    static void FillTri(int tb, int i0, int i1, int j0, int j1, float cx0, float cz0)
    {
        Vector2 n0 = tn0, n1 = tn1, n2 = tn2; float o0 = to0, o1 = to1, o2 = to2, pen = tPen, ip = tIp, dark = tDark;
        for (int j = j0; j <= j1; j++)
        {
            float z = cz0 + j * Cell; int row = tb + (j << 5);
            for (int i = i0; i <= i1; i++)
            {
                if (pool[row + i] < 8) continue;
                float x = cx0 + i * Cell;
                float sd = Mathf.Max(n0.x * x + n0.y * z - o0, Mathf.Max(n1.x * x + n1.y * z - o1, n2.x * x + n2.y * z - o2));
                if (sd >= pen) continue;
                float cov = sd <= 0f ? 1f : 1f - sd * ip; cov = cov * cov * (3f - 2f * cov);
                int lv = (int)(255f * (1f - cov * dark) + 0.5f);
                if (lv < pool[row + i]) pool[row + i] = (byte)lv;
            }
        }
    }

    // ---------------------------------------------------------------- trees
    static Vector3 U, V; // silhouette plane (perpendicular to the sun ray)
    static float[] lattice;
    static void Noise()
    {
        if (lattice != null) return;
        lattice = new float[256 * 256]; uint h = 2166136261u;
        for (int k = 0; k < lattice.Length; k++) { h ^= (uint)k; h *= 16777619u; h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15; lattice[k] = (h & 0xffffff) / 16777215f; }
    }
    static float VN(float x, float y)
    {
        float fx = Mathf.Floor(x), fy = Mathf.Floor(y); int ix = (int)fx & 255, iy = (int)fy & 255, ix1 = (ix + 1) & 255, iy1 = (iy + 1) & 255;
        float a = x - fx, b = y - fy; a = a * a * (3f - 2f * a); b = b * b * (3f - 2f * b);
        float v0 = lattice[(iy << 8) + ix] + (lattice[(iy << 8) + ix1] - lattice[(iy << 8) + ix]) * a;
        float v1 = lattice[(iy1 << 8) + ix] + (lattice[(iy1 << 8) + ix1] - lattice[(iy1 << 8) + ix]) * a;
        return v0 + (v1 - v0) * b;
    }
    static int KindOf(Vector2 uvFoot, Vector2 uvTop)
    {
        for (int k = 0; k < 4; k++)
        {
            var rc = EnvTex.CardRect((EnvTex.Card)k);
            if (Mathf.Abs(uvFoot.x - rc.x) < 0.02f && Mathf.Abs(uvTop.y - rc.w) < 0.02f) return k;
        }
        return 1;
    }
    // crossed cards (2-3 per tree, same foot): one crown per tree
    static int TreeCards(Vector3[] v, Vector2[] uv)
    {
        int n = 0; Vector3 last = new Vector3(1e9f, 0, 0);
        bool hasUv = uv != null && uv.Length == v.Length;
        for (int k = 0; k + 3 < v.Length; k += 4)
        {
            Vector3 b0 = v[k], b1 = v[k + 1]; Vector3 foot = (b0 + b1) * 0.5f;
            if ((foot - last).sqrMagnitude < 0.01f) continue;
            last = foot;
            float w = new Vector2(b1.x - b0.x, b1.z - b0.z).magnitude, h = v[k + 2].y - Mathf.Min(b0.y, b1.y);
            if (h < 3.5f || w < 1f) continue; // scrub: too low to matter
            int kind = hasUv ? KindOf(uv[k], uv[k + 2]) : 1;
            if (Tree(foot, w, h, kind)) n++;
        }
        return n;
    }

    static bool Tree(Vector3 foot, float w, float h, int kind)
    {
        // crown: centre height, radius, half height; trunk: top, radius; leaf gap threshold at the core and the rim
        float cy, r, ry, tt, tr = 0.12f + 0.011f * h, thrC, thrE;
        switch (kind)
        {
            case 0: cy = 0.50f; r = 0.40f * w; ry = 0.48f * h; tt = 0.20f; thrC = 0.80f; thrE = 0.64f; break; // spruce: dense cone
            case 2: cy = 0.80f; r = 0.46f * w; ry = 0.17f * h; tt = 0.72f; thrC = 0.72f; thrE = 0.52f; break; // stone pine: umbrella
            case 3: cy = 0.88f; r = 0.44f * w; ry = 0.12f * h; tt = 0.86f; thrC = 0.70f; thrE = 0.45f; tr = 0.17f; break; // palm
            default: cy = 0.62f; r = 0.40f * w; ry = 0.36f * h; tt = 0.42f; thrC = 0.71f; thrE = 0.53f; break; // broadleaf
        }
        var c = foot + Vector3.up * (cy * h);
        Vector2 lc = Land(c, foot.y); float g = H(lc.x, lc.y) + 0.03f;
        Vector2 cl = Land(c, g);
        float dc = Mathf.Max(0f, c.y - g) / sy;                         // ray length from the ground to the crown
        Vector2 t0 = Land(foot, g), t1 = Land(foot + Vector3.up * (tt * h), g);
        // bounds of the shadow: crown ellipse (projected box) and trunk
        float ext = r * 1.25f, extY = ry * 1.25f / sy;
        float minX = Mathf.Min(cl.x - ext - Mathf.Abs(sH.x) * extY, Mathf.Min(t0.x, t1.x) - 1f), maxX = Mathf.Max(cl.x + ext + Mathf.Abs(sH.x) * extY, Mathf.Max(t0.x, t1.x) + 1f);
        float minZ = Mathf.Min(cl.y - ext - Mathf.Abs(sH.y) * extY, Mathf.Min(t0.y, t1.y) - 1f), maxZ = Mathf.Max(cl.y + ext + Mathf.Abs(sH.y) * extY, Mathf.Max(t0.y, t1.y) + 1f);
        if (!AnyTile(minX, minZ, maxX, maxZ)) return false;

        float k = r / ry; Vector3 ss = new Vector3(s.x, s.y * k, s.z); float sl = ss.magnitude; ss /= sl;
        float seedX = Mathf.Repeat(foot.x * 0.6173f + foot.z * 0.3119f, 211f), seedY = Mathf.Repeat(foot.z * 0.5413f - foot.x * 0.2291f, 197f);
        float soft = 0.06f + 0.0035f * dc;                               // flecks blur with height (sun's disc)
        float ir = 1f / r;                                               // (crown lobes ~3 m, leaf clumps ~1.2 m, small gaps ~0.4 m: FillTree)
        Vector2 td = t1 - t0; float tl2 = Mathf.Max(td.sqrMagnitude, 1e-4f); float tpen = Mathf.Max(0.12f, 0.03f + 0.01f * (tt * h * 0.5f) / sy);
        float frond = Mathf.Repeat(seedX, 6.283f);
        Z = new TreeShape { c = c, g = g, k = k, ss = ss, ir = ir, seedX = seedX, seedY = seedY, kind = kind, ry = ry, thrC = thrC, thrE = thrE,
                            soft = soft, frond = frond, t0 = t0, td = td, tl2 = tl2, tr = tr, tpen = tpen };
        ForTiles(minX, minZ, maxX, maxZ, true);
        return true;
    }
    struct TreeShape { public Vector3 c, ss; public Vector2 t0, td; public float g, k, ir, seedX, seedY, ry, thrC, thrE, soft, frond, tl2, tr, tpen; public int kind; }
    static TreeShape Z;
    static void FillTree(int tb, int i0, int i1, int j0, int j1, float cx0, float cz0)
    {
        Vector3 c = Z.c, ss = Z.ss; Vector2 t0 = Z.t0, td = Z.td; int kind = Z.kind;
        float g = Z.g, k = Z.k, ir = Z.ir, seedX = Z.seedX, seedY = Z.seedY, ry = Z.ry, thrC = Z.thrC, thrE = Z.thrE, soft = Z.soft, frond = Z.frond;
        float tl2 = Z.tl2, tr = Z.tr, tpen = Z.tpen; const float f1 = 0.85f, f2 = 2.4f;
        for (int j = j0; j <= j1; j++)
        {
            float z = cz0 + j * Cell; int row = tb + (j << 5);
            for (int i = i0; i <= i1; i++)
            {
                if (pool[row + i] < 8) continue; // already in full shade
                float x = cx0 + i * Cell, lit = 1f;
                // crown: the ray from this ground point towards the sun, against the (scaled) ellipsoid
                float wx = c.x - x, wy = c.y - g, wz = c.z - z;
                float wys = wy * k, tp = wx * ss.x + wys * ss.y + wz * ss.z;
                if (tp > 0f)
                {
                    float b2 = (wx * wx + wys * wys + wz * wz - tp * tp) * ir * ir;
                    if (b2 < 1.6f)
                    {
                        float b = Mathf.Sqrt(b2);
                        float tw = wx * s.x + wy * s.y + wz * s.z;            // offset from the crown centre, in the silhouette plane
                        float ex = wx - s.x * tw, ey = wy - s.y * tw, ez = wz - s.z * tw;
                        float nu = ex * U.x + ey * U.y + ez * U.z, nv = ex * V.x + ey * V.y + ez * V.z;
                        float n1 = VN(nu * f1 + seedX, nv * f1 + seedY), n2 = VN(nu * f2 + seedY + 31f, nv * f2 + seedX + 17f);
                        if (kind == 0) b /= Mathf.Max(0.08f, Mathf.Clamp01(0.5f * (1f + ey / ry) + 0.08f)); // cone: narrower towards the top
                        float n0 = VN(nu * 0.3f + seedY + 7f, nv * 0.3f + seedX + 3f);
                        float bn = b + (n0 - 0.5f) * 0.45f + (n2 - 0.5f) * 0.12f; // lobed, ragged leafy rim
                        float inside = Mathf.Clamp01((1.02f - bn) * 5f); inside = inside * inside * (3f - 2f * inside);
                        if (inside > 0f)
                        {
                            float through;
                            if (kind == 3)
                            {
                                // palm: fronds radiating from the crown centre, gaps between them and between leaflets
                                float ang = Mathf.Atan2(nv, nu);
                                float fr = Mathf.Abs(Mathf.Sin(ang * 4.5f + frond + (n1 - 0.5f) * 1.5f));
                                float fe = Mathf.Clamp01((Mathf.Sin(b * 48f + ang * 2f) * 0.5f + 0.25f) * 3f); fe = fe * fe * (3f - 2f * fe);
                                float leaf = Mathf.Max(Mathf.Clamp01((fr - 0.97f) * 25f), Mathf.Clamp01((fr - 0.48f - 0.35f * b) * 4f) * fe); // midrib + leaflets
                                through = 1f - Mathf.Max(leaf, Mathf.Clamp01((0.22f - b) * 6f)) * 0.9f;
                            }
                            else
                            {
                                float nn = 0.7f * n1 + 0.3f * n2, thr = Mathf.Lerp(thrC, thrE, Mathf.Clamp01(bn * bn));
                                float gap = Mathf.Clamp01((nn - thr + soft) / (2f * soft)); gap = gap * gap * (3f - 2f * gap);
                                through = 0.05f + 0.95f * gap;
                            }
                            lit = 1f - inside * (1f - through);
                        }
                    }
                }
                // trunk: a band along its projected line, half width = its radius
                float px = x - t0.x, pz = z - t0.y; float u = Mathf.Clamp01((px * td.x + pz * td.y) / tl2);
                float dx = px - td.x * u, dz = pz - td.y * u, d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < tr + tpen) { float a = Mathf.Clamp01((d - tr) / tpen + 0.5f); lit = Mathf.Min(lit, 0.06f + 0.94f * a * a * (3f - 2f * a)); }
                int lv = (int)(255f * lit + 0.5f);
                if (lv < pool[row + i]) pool[row + i] = (byte)lv;
            }
        }
    }

    // ---------------------------------------------------------------- the road's vertex colours
    static void SplitRoad(Mesh m, Color sunLin, byte[] px, int W, int C, int Hs, float dA, float dL, float half)
    {
        if (m == null) return;
        var v = m.vertices; var n = m.normals; var col = m.colors; var uv = m.uv;
        if (col == null || col.Length != v.Length || uv == null || uv.Length != v.Length) return;
        bool hasN = n != null && n.Length == v.Length;
        int rows = px.Length / W * (W / C);
        float MaskAt(float lat, float along)
        {
            int j = Mathf.Clamp((int)((lat + half) / dL), 0, C - 1);
            int r = (int)(along / dA); r = ((r % rows) + rows) % rows;
            int st = r / Hs; return px[(r - st * Hs) * W + st * C + j] * (1f / 255f);
        }
        Vector3 toSun = s;
        for (int i = 0; i < v.Length; i++)
        {
            Vector3 p = v[i], nn = hasN ? n[i] : Vector3.up;
            float ndl = Mathf.Max(0f, Vector3.Dot(nn, toSun));
            float vs = ndl > 0.01f ? LightBake.Sun(p.x + nn.x * 0.2f, p.z + nn.z * 0.2f, p.y + 0.05f) : 0f;
            Color lit = col[i] * 2f;
            float sr = sunLin.r * ndl * vs, sg = sunLin.g * ndl * vs, sb = sunLin.b * ndl * vs;
            // the bake's soft shadow is mostly the same occluders the mask draws sharply (but from a coarse map, so
            // blurred and a little offset): wherever the mask has shade within a few metres, lift it
            float mn = 1f;
            for (float da = -4f; da <= 4.01f; da += 1f)
                for (float dl = -2.4f; dl <= 2.41f; dl += 1.2f) mn = Mathf.Min(mn, MaskAt(uv[i].x + dl, uv[i].y + da));
            float resid = Mathf.Clamp01(vs + (1f - mn) * 1.1f);
            col[i] = new Color(Mathf.Max(0f, lit.r - sr) * 0.5f, Mathf.Max(0f, lit.g - sg) * 0.5f, Mathf.Max(0f, lit.b - sb) * 0.5f, resid);
        }
        m.colors = col;
    }
}
