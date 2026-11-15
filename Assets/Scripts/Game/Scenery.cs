// Scenery.cs — the backdrop: distant hills around every circuit, town buildings along street circuits (Monaco),
// and the dark rubber groove that real tracks have along the racing line.
// Everything is a handful of combined meshes (one per material), so it costs only a few draw calls.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public static class Scenery
{
    class Buf { public List<Vector3> v = new List<Vector3>(); public List<Vector2> uv = new List<Vector2>(); public List<int> tri = new List<int>(); public List<Color> col; }

    // one flat quad, wound so that it is visible from the side "outward" points to
    static void Quad(Buf b, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 outward, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0)
        {
            var tp = p1; p1 = p3; p3 = tp; var tu = uv1; uv1 = uv3; uv3 = tu;
        }
        int i = b.v.Count;
        b.v.Add(p0); b.v.Add(p1); b.v.Add(p2); b.v.Add(p3);
        b.uv.Add(uv0); b.uv.Add(uv1); b.uv.Add(uv2); b.uv.Add(uv3);
        b.tri.Add(i); b.tri.Add(i + 1); b.tri.Add(i + 2); b.tri.Add(i); b.tri.Add(i + 2); b.tri.Add(i + 3);
        if (b.col != null) for (int k = 0; k < 4; k++) b.col.Add(Color.white);
    }
    // tint the last four vertices: colour times ambient occlusion (dark at street level, clear from AoH up)
    static void TintLast(Buf b, Color c)
    {
        if (b.col == null) return;
        for (int k = b.v.Count - 4; k < b.v.Count; k++)
        {
            float y = b.v[k].y, ao = Mathf.Lerp(0.66f, 1f, Mathf.Clamp01(y / 1.6f));
            b.col[k] = new Color(c.r * ao, c.g * ao, c.b * ao, 1f);
        }
    }

    static void Emit(string name, Buf b, Material m, Transform root, bool castShadows)
    {
        if (b.v.Count == 0) return;
        var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(b.v); mesh.SetUVs(0, b.uv); mesh.SetTriangles(b.tri, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        var go = new GameObject(name); go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = m;
        r.shadowCastingMode = castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
        if (b.col != null && b.col.Count == b.v.Count) BakeTint.Add(mesh, b.col.ToArray());
    }

    static float Rand(int i, int s) { float x = Mathf.Sin(i * 12.9898f + s * 78.233f) * 43758.5453f; return x - Mathf.Floor(x); }

    public static void Build(Track t, Transform root, bool street)
    {
        var parent = new GameObject("Scenery").transform; parent.SetParent(root, false);
        var bb = t.Bounds();
        float cx = (float)(bb.minX + bb.maxX) / 2, cy = (float)(bb.minY + bb.maxY) / 2;
        float ext = (float)System.Math.Max(bb.maxX - bb.minX, bb.maxY - bb.minY);
        // the distant land: hills, mountains, a far tree line or the sea, from the circuit's environment (one mesh)
        TownMat = null;
        Environment.Backdrop(parent, cx, cy, ext);
        if (street) Town(t, parent);
    }

    // the town's wall material and window texture, shared with the environment's apartment blocks (one draw call)
    public static Material TownMat;
    public static Texture2D WindowTex() => EnvTex.Facade(); // one bay of a town house (EnvTex): frame, glass, sill, shutters

    // rows of town houses behind the walls of a street circuit
    static void Town(Track t, Transform parent)
    {
        var win = WindowTex(); var env = Environment.Cur;
        Color[] cols = env.walls ?? new[] { new Color(0.93f, 0.86f, 0.72f), new Color(0.90f, 0.74f, 0.62f), new Color(0.96f, 0.93f, 0.88f), new Color(0.86f, 0.80f, 0.66f) };
        // with the baked lighting the colour can ride in the vertex tint (BakeTint): all houses then share one material
        // (one draw call instead of four) and get a darker band at street level; otherwise one material per colour
        bool merged = WorldBuilder.BakeReady;
        var walls = new Buf[merged ? 1 : cols.Length];
        for (int i = 0; i < walls.Length; i++) walls[i] = new Buf { col = merged ? new List<Color>() : null };
        var roofs = new Buf();
        float gapP = WorldBuilder.Gfx <= 1 ? 0.3f : 0.12f; // Low: more gaps between blocks
        float hw = (float)t.width / 2, clear = hw + WorldBuilder.Kerb + 5.5f;
        int N = t.N, step = Mathf.Max(1, Mathf.RoundToInt(13f / (float)t.ds)), count = 0;
        for (int i = 0; i < N; i += step)
        {
            var p = t.pts[i];
            for (int side = -1; side <= 1; side += 2)
            {
                int id = i * 2 + (side > 0 ? 1 : 0);
                if (Rand(id, 5) < gapP) continue; // gaps between blocks
                float depth = 10 + Rand(id, 1) * 8, width = 12.4f, height = (env.floorsMin + Mathf.Floor(Rand(id, 2) * (env.floorsMax - env.floorsMin + 1))) * 3.2f - 0.6f;
                float off = clear + depth / 2;
                float bx = (float)(p.x + p.nx * off * side), by = (float)(p.y + p.ny * off * side);
                // keep clear of every other part of the circuit
                // (the block itself stands clear + depth / 2 from its own track point, 2 m more than this, so only other legs fail)
                bool free = true; float need = hw + WorldBuilder.Kerb + 3.5f + depth / 2; need *= need;
                for (int k = 0; k < N; k += 2) { var q = t.pts[k]; float dx = (float)q.x - bx, dy = (float)q.y - by; if (dx * dx + dy * dy < need) { free = false; break; } }
                if (!free || Environment.Reserved(bx, by, Mathf.Max(width, depth) * 0.6f) || Waterfront.InWater(bx, by) || Trackside.Blocked(bx, by, Mathf.Max(width, depth) * 0.5f) || Waterfront.InTunnel(bx, by)) continue;
                var c = new Vector3(bx, 0, by);
                var tx = new Vector3((float)p.tx, 0, (float)p.ty) * (width / 2);
                var nx = new Vector3((float)p.nx, 0, (float)p.ny) * (depth / 2);
                var up = Vector3.up * height;
                Vector3[] corner = { c - tx - nx, c + tx - nx, c + tx + nx, c - tx + nx };
                int ci = (int)(Rand(id, 3) * cols.Length) % cols.Length;
                var b = walls[merged ? 0 : ci];
                Color tc = merged ? cols[ci] * (0.94f + 0.08f * Rand(id, 4)) : Color.white;
                for (int e = 0; e < 4; e++)
                {
                    Vector3 a0 = corner[e], a1 = corner[(e + 1) % 4];
                    float wlen = (a1 - a0).magnitude, uu = Mathf.Round(wlen / 3.1f), vv = Mathf.Round(height / 3.2f);
                    // on hills the walls carry on down below street level, so no building floats above a slope
                    float below = WorldBuilder.Height != null ? 16f : 0f, vb = -Mathf.Round(below / 3.2f);
                    var dn = Vector3.down * below; var outw = (a0 + a1) * 0.5f - c;
                    if (b.col == null) Quad(b, a0 + dn, a1 + dn, a1 + up, a0 + up, outw, new Vector2(0, vb), new Vector2(uu, vb), new Vector2(uu, vv), new Vector2(0, vv));
                    else
                    {
                        // split at street level and at 2 m, so the shade gathers at the foot of the wall
                        float v2 = vv * 2f / height; var u2 = Vector3.up * 2f;
                        if (below > 0) { Quad(b, a0 + dn, a1 + dn, a1, a0, outw, new Vector2(0, vb), new Vector2(uu, vb), new Vector2(uu, 0), new Vector2(0, 0)); TintLast(b, tc); }
                        Quad(b, a0, a1, a1 + u2, a0 + u2, outw, new Vector2(0, 0), new Vector2(uu, 0), new Vector2(uu, v2), new Vector2(0, v2)); TintLast(b, tc);
                        Quad(b, a0 + u2, a1 + u2, a1 + up, a0 + up, outw, new Vector2(0, v2), new Vector2(uu, v2), new Vector2(uu, vv), new Vector2(0, vv)); TintLast(b, tc);
                        // the roof edge: a pale stone cornice standing 0.35 m proud, with its underside in shade (uv 0 = plaster)
                        var o = outw.normalized * 0.35f; var ed = (a1 - a0).normalized * 0.35f; var cc = Color.Lerp(tc, Color.white, 0.45f);
                        Vector3 c0 = a0 - ed + up + Vector3.down * 0.5f, c1 = a1 + ed + up + Vector3.down * 0.5f, k0 = a0 - ed + up + Vector3.up * 0.25f, k1 = a1 + ed + up + Vector3.up * 0.25f;
                        Quad(b, c0 + o, c1 + o, k1 + o, k0 + o, outw, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero); TintLast(b, cc);
                        Quad(b, c0, c1, c1 + o, c0 + o, Vector3.down, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero); TintLast(b, cc);
                        Quad(b, k0, k1, k1 + o, k0 + o, Vector3.up, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero); TintLast(b, cc);
                    }
                }
                Quad(roofs, corner[0] + up, corner[1] + up, corner[2] + up, corner[3] + up, Vector3.up, Vector2.zero, Vector2.right, Vector2.one, Vector2.up);
                count++;
            }
        }
        for (int i = 0; i < walls.Length; i++)
        {
            var wm = WorldBuilder.Mat(merged ? Color.white : cols[i], win, 0.25f);
            if (i == 0) TownMat = wm;
            Emit("Town", walls[i], wm, parent, true);
        }
        Emit("TownRoofs", roofs, WorldBuilder.Mat(new Color(0.62f, 0.33f, 0.24f), null, 0.1f), parent, false);
    }

    // Braking distance boards (150 / 100 / 50 m) before every real braking zone, on the outside of the corner,
    // facing the driver. The distances are measured to the turn-in point of the racing line.
    static readonly string[] Digit = {
        "01110100011001110101110011000101110", "00100011000010000100001000010001110", "", "", "",
        "11111100001111000001000011000101110" };
    public static GameObject Boards(Track t, RacingLine line, Transform parent, float wallAt)
    {
        string[] label = { "150", "100", "50" };
        var tex = WorldBuilder.Tex(96, 96, (u, v, x, y) =>
        {
            int row = Mathf.Min(2, y / 32), yy = y - row * 32 - 5; string txt = label[row];
            int x0 = (96 - txt.Length * 18) / 2 + 1, xx = x - x0;
            bool border = x < 3 || x > 92 || y % 32 < 2 || y % 32 > 29;
            if (!border && xx >= 0 && yy >= 0 && yy < 21 && xx < txt.Length * 18 && xx % 18 < 15)
            {
                string bits = Digit[txt[xx / 18] - '0'];
                if (bits[(6 - yy / 3) * 5 + (xx % 18) / 3] == '1') return new Color32(15, 15, 18, 255);
            }
            return border ? new Color32(15, 15, 18, 255) : new Color32(245, 245, 240, 255);
        });
        var face = new Buf(); var post = new Buf();
        int N = line.N; float hw = (float)t.width / 2;
        // keep the board between the kerb and the barrier
        float inner = hw + WorldBuilder.Kerb + 0.15f, width = Mathf.Min(1.1f, wallAt - inner - 0.1f);
        if (width < 0.5f) return null;
        int i0 = 0; while (i0 < N && line.braking[i0]) i0++; // begin outside a braking zone
        for (int c = 0; c < N; c++)
        {
            int i = (i0 + c) % N;
            if (!line.braking[i] || line.braking[(i - 1 + N) % N]) continue;
            int len = 0; while (len < N && line.braking[(i + len) % N]) len++;
            int e = (i + len) % N;
            if (line.speed[i] - line.speed[e] < 14 || len * t.ds < 35) continue;
            float turn = 0; for (int k = 0; k < 20; k++) turn += (float)line.curv[(e + k) % N];
            float side = turn > 0 ? -1 : 1; // left-hand corner: boards on the right
            float[] dist = { 150, 100, 50 };
            for (int b = 0; b < 3; b++)
            {
                if (dist[b] > len * t.ds + 70) continue; // no 150 m board for a short braking zone
                int k = ((e - Mathf.RoundToInt(dist[b] / (float)t.ds)) % N + N) % N; var p = t.pts[k];
                var tan = new Vector3((float)p.tx, 0, (float)p.ty); var nrm = new Vector3((float)p.nx, 0, (float)p.ny);
                var c0 = new Vector3((float)p.x, 0, (float)p.y);
                Vector3 a = c0 + nrm * side * inner, d = c0 + nrm * side * (inner + width);
                Vector3 l = side > 0 ? d : a, r = side > 0 ? a : d; // l = the driver's left
                float y0 = 0.55f, y1 = 1.25f, v0 = b / 3f, v1 = (b + 1) / 3f;
                Quad(face, l + Vector3.up * y0, r + Vector3.up * y0, r + Vector3.up * y1, l + Vector3.up * y1, -tan,
                     new Vector2(0, v0), new Vector2(1, v0), new Vector2(1, v1), new Vector2(0, v1));
                var w = new Vector2(0.5f, 0.02f);
                Quad(face, l + Vector3.up * y0, r + Vector3.up * y0, r + Vector3.up * y1, l + Vector3.up * y1, tan, w, w, w, w);
                Vector3 m = (a + d) * 0.5f + tan * 0.02f, hx = nrm * 0.04f;
                Quad(post, m - hx, m + hx, m + hx + Vector3.up * y0, m - hx + Vector3.up * y0, -tan, w, w, w, w);
                Quad(post, m - hx, m + hx, m + hx + Vector3.up * y0, m - hx + Vector3.up * y0, tan, w, w, w, w);
            }
        }
        var go = new GameObject("BrakeBoards"); go.transform.SetParent(parent, false);
        Emit("Boards", face, WorldBuilder.Mat(Color.white, tex, 0.2f), go.transform, false);
        Emit("BoardPosts", post, WorldBuilder.Mat(new Color(0.25f, 0.25f, 0.27f), null, 0.2f), go.transform, false);
        return go;
    }

    // the dark band of laid-down rubber along the racing line
    public static GameObject Rubber(Track t, RacingLine line, Transform parent)
    {
        if (WorldBuilder.RoadShaded)
        {
            // Road.shader already darkens the racing line (and the tyre tracks in it): no see-through overlay needed
            var empty = new GameObject("Rubber"); empty.transform.SetParent(parent, false);
            return empty;
        }
        var b = new Buf(); int N = line.N; const float half = 1.5f, h = 0.04f;
        for (int i = 0; i < N; i++)
        {
            var p = t.pts[i];
            var c = new Vector3((float)line.x[i], h, (float)line.y[i]);
            var n = new Vector3((float)p.nx, 0, (float)p.ny) * half;
            b.v.Add(c + n); b.v.Add(c - n);
            b.uv.Add(new Vector2(0, i * 0.5f)); b.uv.Add(new Vector2(1, i * 0.5f));
        }
        for (int i = 0; i < N; i++)
        {
            int j = (i + 1) % N, a0 = i * 2, a1 = i * 2 + 1, b0 = j * 2, b1 = j * 2 + 1;
            // both windings: the band must show whichever way the circuit runs
            b.tri.Add(a0); b.tri.Add(b0); b.tri.Add(a1); b.tri.Add(a1); b.tri.Add(b0); b.tri.Add(b1);
            b.tri.Add(a0); b.tri.Add(a1); b.tri.Add(b0); b.tri.Add(a1); b.tri.Add(b1); b.tri.Add(b0);
        }
        var mat = new Material(Resources.Load<Material>("Shadow"));
        var tex = new Texture2D(32, 4, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear };
        var px = new Color32[32 * 4];
        for (int y = 0; y < 4; y++) for (int x = 0; x < 32; x++)
        {
            float u = (x + 0.5f) / 32f, e = Mathf.Clamp01(Mathf.Min(u, 1 - u) * 3f);
            px[y * 32 + x] = new Color32(10, 10, 12, (byte)(e * e * 70));
        }
        tex.SetPixels32(px); tex.Apply(true, true); mat.mainTexture = tex;
        var mesh = new Mesh { name = "Rubber", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(b.v); mesh.SetUVs(0, b.uv); mesh.SetTriangles(b.tri, 0); mesh.RecalculateBounds();
        var go = new GameObject("Rubber"); go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return go;
    }
}
