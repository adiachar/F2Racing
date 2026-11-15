// WorldData.cs — the real surroundings of a circuit (buildings, land use, roads, trees and the terrain height grid,
// from OpenStreetMap / Overture Maps and terrain data), read from Resources/World/<Name>.bytes into plain arrays.
// RealWorld.cs turns them into meshes. Pure C# (no UnityEngine below the Load method), so it can be tested outside
// Unity. A missing file gives null: the circuit then keeps its procedural scenery.
//
// Format v1, little-endian: "F2W1", int32 version (1), int32 section count, then per section a 4-byte ASCII tag,
// int32 payload bytes, payload. Unknown tags are skipped; a broken section is dropped, the others are kept.
//   TERR  int32 nx, nz; float x0, z0, cell; nx * nz float heights (z outer, x inner), metres, matched to the track
//   BLDG  int32 n; per building: float height, minHeight; byte roofShape (0 flat, 1 gabled, 2 hipped, 3 dome,
//         4 pyramidal, 5 other), byte kind (0 unknown, 1 residential, 2 commercial/office, 3 industrial,
//         4 grandstand/stadium/sports, 5 civic, 6 religious, 7 garage/shed, 8 hotel/apartment tower),
//         Color32 rgba (alpha 0 = unknown), ushort nPts, nPts x (float x, z): outer ring, CCW from above
//   AREA  int32 n; per polygon: byte kind (0 grass, 1 forest, 2 farmland, 3 residential, 4 commercial/industrial,
//         5 water, 6 sand, 7 parking/paved, 8 park, 9 rock, 10 wetland, 11 motorsport paved), ushort nPts, pts
//   LINE  int32 n; per line: byte kind (0 motorway, 1 primary, 2 secondary/tertiary, 3 residential/service, 4 path,
//         5 railway), float width, ushort nPts, pts
//   TREE  int32 n; float x, z, height each
// Coordinates are Unity world metres (x, z), the same frame as the track.
using System;
using System.Collections.Generic;

public sealed class WorldData
{
    // terrain grid (null = none)
    public int tnx, tnz; public float tx0, tz0, tcell; public float[] th;
    // buildings: points of building b are bX/bZ[bOff[b] .. bOff[b] + bCnt[b])
    public int bN; public float[] bH, bMinH; public byte[] bRoof, bKind; public uint[] bRgba; public int[] bOff, bCnt; public float[] bX, bZ;
    // land-use polygons, in drawing order (background first, water last)
    public int aN; public byte[] aKind; public int[] aOff, aCnt; public float[] aX, aZ;
    // roads, paths, railways
    public int lN; public byte[] lKind; public float[] lW; public int[] lOff, lCnt; public float[] lX, lZ;
    // single trees
    public int tN; public float[] tX, tZ, tH;
    public string error; // the first problem met (that section was dropped), for the log

    public bool HasTerrain => th != null && tnx >= 2 && tnz >= 2 && tcell > 0;

    // terrain height at (x, z), bilinear, clamped at the edge of the grid
    public float Terrain(float x, float z)
    {
        float fx = (x - tx0) / tcell, fz = (z - tz0) / tcell;
        if (fx < 0) fx = 0; else if (fx > tnx - 1.001f) fx = tnx - 1.001f;
        if (fz < 0) fz = 0; else if (fz > tnz - 1.001f) fz = tnz - 1.001f;
        int i = (int)fx, j = (int)fz; float a = fx - i, b = fz - j; int k = j * tnx + i;
        return (th[k] * (1 - a) + th[k + 1] * a) * (1 - b) + (th[k + tnx] * (1 - a) + th[k + tnx + 1] * a) * b;
    }

    // ---------------------------------------------------------------- parsing
    sealed class Rd
    {
        readonly byte[] d; public int p; readonly int end;
        public Rd(byte[] d, int p, int end) { this.d = d; this.p = p; this.end = end; }
        public int Left => end - p;
        void Need(int n) { if (p + n > end) throw new FormatException("section ends early"); }
        public int I32() { Need(4); int v = d[p] | d[p + 1] << 8 | d[p + 2] << 16 | d[p + 3] << 24; p += 4; return v; }
        public ushort U16() { Need(2); int v = d[p] | d[p + 1] << 8; p += 2; return (ushort)v; }
        public byte U8() { Need(1); return d[p++]; }
        public float F32()
        {
            Need(4);
            float f;
            if (BitConverter.IsLittleEndian) f = BitConverter.ToSingle(d, p);
            else { var t = new[] { d[p + 3], d[p + 2], d[p + 1], d[p] }; f = BitConverter.ToSingle(t, 0); }
            p += 4; return f;
        }
        public void Floats(float[] dst, int n)
        {
            Need(n * 4);
            if (BitConverter.IsLittleEndian) Buffer.BlockCopy(d, p, dst, 0, n * 4);
            else for (int i = 0; i < n; i++) { var t = new[] { d[p + i * 4 + 3], d[p + i * 4 + 2], d[p + i * 4 + 1], d[p + i * 4] }; dst[i] = BitConverter.ToSingle(t, 0); }
            p += n * 4;
        }
        // a count that the rest of the section can hold (each item at least minBytes)
        public int Count(int minBytes)
        {
            int n = I32();
            if (n < 0 || (long)n * minBytes > Left) throw new FormatException("bad count " + n);
            return n;
        }
    }
    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && v > -1e6f && v < 1e6f;

    public static WorldData Parse(byte[] d)
    {
        if (d == null || d.Length < 12 || d[0] != 'F' || d[1] != '2' || d[2] != 'W' || d[3] != '1') return null;
        var w = new WorldData();
        var head = new Rd(d, 4, d.Length);
        int version = head.I32(), count = head.I32();
        if (version < 1) return null;
        int p = 12;
        for (int s = 0; s < count && p + 8 <= d.Length; s++)
        {
            string tag = new string(new[] { (char)d[p], (char)d[p + 1], (char)d[p + 2], (char)d[p + 3] });
            int len = d[p + 4] | d[p + 5] << 8 | d[p + 6] << 16 | d[p + 7] << 24;
            p += 8;
            if (len < 0 || len > d.Length - p) { w.error = w.error ?? tag + ": runs past the end of the file"; len = d.Length - p; }
            var r = new Rd(d, p, p + len);
            try
            {
                switch (tag)
                {
                    case "TERR": w.ReadTerrain(r); break;
                    case "BLDG": w.ReadBuildings(r); break;
                    case "AREA": w.ReadAreas(r); break;
                    case "LINE": w.ReadLines(r); break;
                    case "TREE": w.ReadTrees(r); break;
                }
            }
            catch (Exception e)
            {
                w.error = w.error ?? tag + ": " + e.Message;
                switch (tag)
                {
                    case "TERR": w.th = null; break;
                    case "BLDG": w.bN = 0; break;
                    case "AREA": w.aN = 0; break;
                    case "LINE": w.lN = 0; break;
                    case "TREE": w.tN = 0; break;
                }
            }
            p += len;
        }
        return w;
    }

    void ReadTerrain(Rd r)
    {
        int nx = r.I32(), nz = r.I32(); float x0 = r.F32(), z0 = r.F32(), cell = r.F32();
        if (nx < 2 || nz < 2 || (long)nx * nz * 4 > r.Left || !(cell > 0.01f) || !Finite(x0) || !Finite(z0)) throw new FormatException("bad grid");
        var h = new float[nx * nz]; r.Floats(h, h.Length);
        // holes in the data (NaN, absurd values): the mean of the good ones
        double sum = 0; int good = 0;
        for (int i = 0; i < h.Length; i++) if (Finite(h[i]) && h[i] > -500 && h[i] < 9000) { sum += h[i]; good++; }
        if (good == 0) throw new FormatException("no valid heights");
        float mean = (float)(sum / good);
        for (int i = 0; i < h.Length; i++) if (!(Finite(h[i]) && h[i] > -500 && h[i] < 9000)) h[i] = mean;
        tnx = nx; tnz = nz; tx0 = x0; tz0 = z0; tcell = cell; th = h;
    }

    // reads n points into growing coordinate lists; false when a coordinate is not a real number
    static bool Points(Rd r, int n, List<float> xs, List<float> zs)
    {
        bool ok = true;
        for (int k = 0; k < n; k++)
        {
            float x = r.F32(), z = r.F32();
            if (!Finite(x) || !Finite(z)) ok = false;
            xs.Add(x); zs.Add(z);
        }
        return ok;
    }

    void ReadBuildings(Rd r)
    {
        int n = r.Count(14);
        var h = new float[n]; var mh = new float[n]; var roof = new byte[n]; var kind = new byte[n]; var rgba = new uint[n];
        var off = new int[n]; var cnt = new int[n];
        var xs = new List<float>(n * 6); var zs = new List<float>(n * 6);
        for (int b = 0; b < n; b++)
        {
            h[b] = r.F32(); mh[b] = r.F32(); roof[b] = r.U8(); kind[b] = r.U8();
            uint cr = r.U8(), cg = r.U8(), cb = r.U8(), ca = r.U8(); rgba[b] = cr | cg << 8 | cb << 16 | ca << 24;
            int np = r.U16();
            off[b] = xs.Count;
            bool ok = Points(r, np, xs, zs);
            cnt[b] = ok && np >= 3 ? np : 0;
            if (!Finite(h[b])) h[b] = 0;
            if (!Finite(mh[b]) || mh[b] < 0) mh[b] = 0;
        }
        bN = n; bH = h; bMinH = mh; bRoof = roof; bKind = kind; bRgba = rgba; bOff = off; bCnt = cnt; bX = xs.ToArray(); bZ = zs.ToArray();
    }

    void ReadAreas(Rd r)
    {
        int n = r.Count(3);
        var kind = new byte[n]; var off = new int[n]; var cnt = new int[n];
        var xs = new List<float>(n * 16); var zs = new List<float>(n * 16);
        for (int a = 0; a < n; a++)
        {
            kind[a] = r.U8(); int np = r.U16();
            off[a] = xs.Count;
            bool ok = Points(r, np, xs, zs);
            cnt[a] = ok && np >= 3 ? np : 0;
        }
        aN = n; aKind = kind; aOff = off; aCnt = cnt; aX = xs.ToArray(); aZ = zs.ToArray();
    }

    void ReadLines(Rd r)
    {
        int n = r.Count(7);
        var kind = new byte[n]; var wd = new float[n]; var off = new int[n]; var cnt = new int[n];
        var xs = new List<float>(n * 8); var zs = new List<float>(n * 8);
        for (int l = 0; l < n; l++)
        {
            kind[l] = r.U8(); wd[l] = r.F32(); int np = r.U16();
            off[l] = xs.Count;
            bool ok = Points(r, np, xs, zs);
            cnt[l] = ok && np >= 2 ? np : 0;
            if (!Finite(wd[l]) || wd[l] < 0 || wd[l] > 80) wd[l] = 0;
        }
        lN = n; lKind = kind; lW = wd; lOff = off; lCnt = cnt; lX = xs.ToArray(); lZ = zs.ToArray();
    }

    void ReadTrees(Rd r)
    {
        int n = r.Count(12);
        var x = new float[n]; var z = new float[n]; var h = new float[n]; int m = 0;
        for (int i = 0; i < n; i++)
        {
            float a = r.F32(), b = r.F32(), c = r.F32();
            if (!Finite(a) || !Finite(b)) continue;
            x[m] = a; z[m] = b; h[m] = Finite(c) && c > 0 && c < 80 ? c : 0; m++;
        }
        tN = m; tX = x; tZ = z; tH = h;
    }

    // Resources/World/<Name>.bytes, Name = the circuit id with a capital first letter (Monza, Spa, Budapest ...)
    public static string FileName(string circuitId) =>
        string.IsNullOrEmpty(circuitId) ? null : char.ToUpperInvariant(circuitId[0]) + circuitId.Substring(1);
#if UNITY_5_3_OR_NEWER
    public static WorldData Load(string circuitId)
    {
        var name = FileName(circuitId); if (name == null) return null;
        var ta = UnityEngine.Resources.Load<UnityEngine.TextAsset>("World/" + name);
        if (ta == null) return null;
        WorldData w = null;
        try { w = Parse(ta.bytes); }
        catch (Exception e) { UnityEngine.Debug.LogWarning("World/" + name + ": " + e.Message); }
        UnityEngine.Resources.UnloadAsset(ta);
        if (w != null && w.error != null) UnityEngine.Debug.LogWarning("World/" + name + ": " + w.error);
        return w;
    }
#endif
}

// Polygon helpers for RealWorld (pure C#): clean-up, simplification, triangulation, clipping, scan-line filling.
// Polygons are coordinate arrays x[0..n), z[0..n) without a repeated closing point.
public static class WorldGeo
{
    public static float Area(float[] x, float[] z, int n)
    {
        double a = 0;
        for (int i = 0, j = n - 1; i < n; j = i++) a += (double)x[j] * z[i] - (double)x[i] * z[j];
        return (float)(a * 0.5);
    }

    // drop repeated points (closer than eps), the closing duplicate and points on a straight line; make it CCW.
    // Returns the new count (< 3: nothing left).
    public static int Clean(float[] x, float[] z, int n, float eps = 0.05f)
    {
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            if (m > 0 && Math.Abs(x[i] - x[m - 1]) < eps && Math.Abs(z[i] - z[m - 1]) < eps) continue;
            x[m] = x[i]; z[m] = z[i]; m++;
        }
        while (m > 1 && Math.Abs(x[0] - x[m - 1]) < eps && Math.Abs(z[0] - z[m - 1]) < eps) m--;
        // straight-line points (a few passes: removing one can make its neighbour straight)
        for (int pass = 0; pass < 3 && m > 3; pass++)
        {
            int k = 0;
            for (int i = 0; i < m; i++)
            {
                int a = k > 0 ? k - 1 : m - 1, c = (i + 1) % m;
                float ax = k > 0 ? x[k - 1] : x[a], az = k > 0 ? z[k - 1] : z[a];
                float cr = (x[i] - ax) * (z[c] - z[i]) - (z[i] - az) * (x[c] - x[i]);
                float l = Math.Abs(x[c] - ax) + Math.Abs(z[c] - az);
                if (Math.Abs(cr) <= 1e-4f * (l * l + 1e-3f) && m - (i - k) > 3) continue;
                x[k] = x[i]; z[k] = z[i]; k++;
            }
            if (k == m) break;
            m = k;
        }
        if (m < 3) return m;
        if (Area(x, z, m) < 0) { for (int i = 0, j = m - 1; i < j; i++, j--) { float t = x[i]; x[i] = x[j]; x[j] = t; t = z[i]; z[i] = z[j]; z[j] = t; } }
        return m;
    }

    // Douglas-Peucker on a closed ring (closed = true) or an open line; tol in metres. Returns the new count.
    public static int Simplify(float[] x, float[] z, int n, float tol, bool closed)
    {
        if (n <= (closed ? 4 : 2) || tol <= 0) return n;
        var keep = new bool[n];
        int a = 0, b = n - 1;
        if (closed)
        {
            // split the ring at its two furthest points
            float best = -1;
            for (int i = 1; i < n; i++) { float d = (x[i] - x[0]) * (x[i] - x[0]) + (z[i] - z[0]) * (z[i] - z[0]); if (d > best) { best = d; b = i; } }
            keep[0] = keep[b] = true;
            DP(x, z, 0, b, tol * tol, keep, n);
            DP(x, z, b, n, tol * tol, keep, n);
        }
        else { keep[a] = keep[b] = true; DP(x, z, a, b, tol * tol, keep, n); }
        int m = 0;
        for (int i = 0; i < n; i++) if (keep[i]) { x[m] = x[i]; z[m] = z[i]; m++; }
        return m;
    }
    // (index n stands for point 0 again, closing the ring)
    static void DP(float[] x, float[] z, int i0, int i1, float tol2, bool[] keep, int n)
    {
        var stack = new Stack<(int, int)>(); stack.Push((i0, i1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            if (e - s < 2) continue;
            float ax = x[s % n], az = z[s % n], bx = x[e % n], bz = z[e % n], dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            float worst = -1; int wi = -1;
            for (int i = s + 1; i < e; i++)
            {
                float px = x[i] - ax, pz = z[i] - az, d2;
                if (l2 < 1e-9f) d2 = px * px + pz * pz;
                else { float t = (px * dx + pz * dz) / l2; t = t < 0 ? 0 : t > 1 ? 1 : t; float qx = px - t * dx, qz = pz - t * dz; d2 = qx * qx + qz * qz; }
                if (d2 > worst) { worst = d2; wi = i; }
            }
            if (worst > tol2) { keep[wi] = true; stack.Push((s, wi)); stack.Push((wi, e)); }
        }
    }

    // ear clipping of a CCW simple polygon; triangles (indices into the polygon, plus baseIndex) are appended,
    // wound counter-clockwise seen from above. False if it had to give up (the triangles so far are kept).
    public static bool Triangulate(float[] x, float[] z, int n, List<int> tri, int baseIndex = 0)
    {
        if (n < 3) return false;
        if (n == 3) { tri.Add(baseIndex); tri.Add(baseIndex + 1); tri.Add(baseIndex + 2); return true; }
        var prev = new int[n]; var next = new int[n];
        for (int i = 0; i < n; i++) { prev[i] = i == 0 ? n - 1 : i - 1; next[i] = i == n - 1 ? 0 : i + 1; }
        int left = n, cur = 0, miss = 0; bool clean = true;
        while (left > 3)
        {
            int a = prev[cur], c = next[cur];
            if (IsEar(x, z, a, cur, c, next))
            {
                tri.Add(baseIndex + a); tri.Add(baseIndex + cur); tri.Add(baseIndex + c);
                next[a] = c; prev[c] = a; left--; cur = c; miss = 0;
                continue;
            }
            cur = c;
            if (++miss > left)
            {
                // no ear (self-touching or crossing outline): cut the most convex corner anyway and carry on
                clean = false;
                int best = cur; float bc = float.NegativeInfinity; int q = cur;
                for (int k = 0; k < left; k++, q = next[q])
                {
                    float cr = Cross(x, z, prev[q], q, next[q]);
                    if (cr > bc) { bc = cr; best = q; }
                }
                a = prev[best]; c = next[best];
                if (bc > 0) { tri.Add(baseIndex + a); tri.Add(baseIndex + best); tri.Add(baseIndex + c); }
                next[a] = c; prev[c] = a; left--; cur = c; miss = 0;
            }
        }
        { int a = prev[cur], c = next[cur]; if (Cross(x, z, a, cur, c) > 0) { tri.Add(baseIndex + a); tri.Add(baseIndex + cur); tri.Add(baseIndex + c); } }
        return clean;
    }
    static float Cross(float[] x, float[] z, int a, int b, int c) => (x[b] - x[a]) * (z[c] - z[b]) - (z[b] - z[a]) * (x[c] - x[b]);
    static bool IsEar(float[] x, float[] z, int a, int b, int c, int[] next)
    {
        float ax = x[a], az = z[a], bx = x[b], bz = z[b], cx = x[c], cz = z[c];
        float area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
        if (area <= 1e-6f) return false; // reflex or flat corner
        for (int p = next[c]; p != a; p = next[p])
        {
            float px = x[p], pz = z[p];
            if ((px == ax && pz == az) || (px == bx && pz == bz) || (px == cx && pz == cz)) continue;
            // inside or on the edge of the triangle
            if ((bx - ax) * (pz - az) - (bz - az) * (px - ax) >= 0 && (cx - bx) * (pz - bz) - (cz - bz) * (px - bx) >= 0 && (ax - cx) * (pz - cz) - (az - cz) * (px - cx) >= 0) return false;
        }
        return true;
    }

    // Sutherland-Hodgman clip of a polygon to a rectangle; returns the new arrays' count (outputs replaced)
    public static int ClipRect(ref float[] x, ref float[] z, int n, float x0, float z0, float x1, float z1)
    {
        for (int side = 0; side < 4 && n >= 3; side++)
        {
            var ox = new List<float>(n + 4); var oz = new List<float>(n + 4);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float ax = x[i], az = z[i], bx = x[j], bz = z[j];
                float da = side == 0 ? ax - x0 : side == 1 ? x1 - ax : side == 2 ? az - z0 : z1 - az;
                float db = side == 0 ? bx - x0 : side == 1 ? x1 - bx : side == 2 ? bz - z0 : z1 - bz;
                if (da >= 0) { ox.Add(ax); oz.Add(az); }
                if ((da >= 0) != (db >= 0)) { float t = da / (da - db); ox.Add(ax + (bx - ax) * t); oz.Add(az + (bz - az) * t); }
            }
            x = ox.ToArray(); z = oz.ToArray(); n = x.Length;
        }
        return n;
    }

    // Scan-line fill (even-odd) of a polygon over a grid of nx x nz cells of size cx x cz from (gx0, gz0): calls
    // span(row, first, last) for every run of cell centres inside it. Crossings are bucketed per row, so a polygon
    // with thousands of points costs about (points + filled cells), not points x rows.
    public static void Fill(float[] x, float[] z, int off, int n, float gx0, float gz0, float cx, float cz, int nx, int nz, Action<int, int, int> span)
    {
        if (n < 3) return;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < n; i++) { float v = z[off + i]; if (v < minZ) minZ = v; if (v > maxZ) maxZ = v; }
        int j0 = Math.Max(0, (int)Math.Ceiling((minZ - gz0) / cz - 0.5f)), j1 = Math.Min(nz - 1, (int)Math.Floor((maxZ - gz0) / cz - 0.5f));
        if (j1 < j0) return;
        int rows = j1 - j0 + 1;
        var count = new int[rows + 1];
        for (int pass = 0; pass < 2; pass++)
        {
            float[] xs = pass == 1 ? new float[count[rows]] : null;
            int[] fillAt = pass == 1 ? new int[rows] : null;
            if (pass == 1) for (int r = 0; r < rows; r++) fillAt[r] = count[r];
            for (int i = 0, k = n - 1; i < n; k = i++)
            {
                float ax = x[off + k], az = z[off + k], bx = x[off + i], bz = z[off + i];
                if (az == bz) continue;
                float lo = Math.Min(az, bz), hi = Math.Max(az, bz);
                // rows whose centre line zc satisfies lo <= zc < hi
                int ra = Math.Max(j0, (int)Math.Ceiling((lo - gz0) / cz - 0.5f)), rb = Math.Min(j1, (int)Math.Ceiling((hi - gz0) / cz - 0.5f) - 1);
                for (int r = ra; r <= rb; r++)
                {
                    if (pass == 0) { count[r - j0 + 1]++; continue; }
                    float zc = gz0 + (r + 0.5f) * cz;
                    xs[fillAt[r - j0]++] = ax + (bx - ax) * (zc - az) / (bz - az);
                }
            }
            if (pass == 0) { for (int r = 0; r < rows; r++) count[r + 1] += count[r]; continue; }
            for (int r = 0; r < rows; r++)
            {
                int s = count[r], e = count[r + 1];
                if (e - s < 2) continue;
                if (e - s <= 24)
                    for (int q = s + 1; q < e; q++) { float v = xs[q]; int m = q - 1; while (m >= s && xs[m] > v) { xs[m + 1] = xs[m]; m--; } xs[m + 1] = v; }
                else Array.Sort(xs, s, e - s);
                for (int q = s; q + 1 < e; q += 2)
                {
                    int c0 = Math.Max(0, (int)Math.Ceiling((xs[q] - gx0) / cx - 0.5f)), c1 = Math.Min(nx - 1, (int)Math.Ceiling((xs[q + 1] - gx0) / cx - 0.5f) - 1);
                    if (c1 >= c0) span(r + j0, c0, c1);
                }
            }
        }
    }

    // the smallest rectangle round the polygon among those aligned with one of its edges: centre, unit long axis
    // (ax, az), half length / half width, and how much of it the polygon fills (1 = a rectangle)
    public static void Obb(float[] x, float[] z, int n, out float cx, out float cz, out float ax, out float az, out float hl, out float hs, out float fill)
    {
        float best = float.MaxValue; cx = cz = 0; ax = 1; az = 0; hl = hs = 0;
        for (int e = 0; e < n; e++)
        {
            int f = (e + 1) % n; float dx = x[f] - x[e], dz = z[f] - z[e], l = (float)Math.Sqrt(dx * dx + dz * dz);
            if (l < 0.5f) continue;
            dx /= l; dz /= l;
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float u = x[i] * dx + z[i] * dz, v = -x[i] * dz + z[i] * dx;
                if (u < u0) u0 = u; if (u > u1) u1 = u; if (v < v0) v0 = v; if (v > v1) v1 = v;
            }
            float area = (u1 - u0) * (v1 - v0);
            if (area < best)
            {
                best = area; float um = (u0 + u1) * 0.5f, vm = (v0 + v1) * 0.5f;
                cx = um * dx - vm * dz; cz = um * dz + vm * dx;
                if (u1 - u0 >= v1 - v0) { ax = dx; az = dz; hl = (u1 - u0) * 0.5f; hs = (v1 - v0) * 0.5f; }
                else { ax = -dz; az = dx; hl = (v1 - v0) * 0.5f; hs = (u1 - u0) * 0.5f; }
            }
        }
        fill = best < float.MaxValue && best > 1e-6f ? Math.Abs(Area(x, z, n)) / best : 0;
    }

    public static bool Inside(float[] x, float[] z, int off, int n, float px, float pz)
    {
        bool c = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float ax = x[off + i], az = z[off + i], bx = x[off + j], bz = z[off + j];
            if ((az > pz) != (bz > pz) && px < (bx - ax) * (pz - az) / (bz - az) + ax) c = !c;
        }
        return c;
    }
}
