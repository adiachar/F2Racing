// Scenery.cs — the backdrop: distant hills around every circuit, town buildings along street circuits (Monaco),
// and the dark rubber groove that real tracks have along the racing line.
// Everything is a handful of combined meshes (one per material), so it costs only a few draw calls.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public static class Scenery
{
    class Buf { public List<Vector3> v = new List<Vector3>(); public List<Vector2> uv = new List<Vector2>(); public List<int> tri = new List<int>(); }

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
    }

    static float Rand(int i, int s) { float x = Mathf.Sin(i * 12.9898f + s * 78.233f) * 43758.5453f; return x - Mathf.Floor(x); }

    public static void Build(Track t, Transform root, bool street)
    {
        var parent = new GameObject("Scenery").transform; parent.SetParent(root, false);
        var bb = t.Bounds();
        float cx = (float)(bb.minX + bb.maxX) / 2, cy = (float)(bb.minY + bb.maxY) / 2;
        float ext = (float)System.Math.Max(bb.maxX - bb.minX, bb.maxY - bb.minY);
        Hills(parent, cx, cy, ext * 0.5f + 620, 60, 95, new Color(0.30f, 0.40f, 0.33f), 3);
        Hills(parent, cx, cy, ext * 0.5f + 980, 130, 170, new Color(0.38f, 0.47f, 0.50f), 11);
        if (street) Town(t, parent);
    }

    // a ring of rolling hills seen from inside
    static void Hills(Transform parent, float cx, float cy, float radius, float hMin, float hVar, Color col, int seed)
    {
        var b = new Buf(); const int Seg = 120;
        float H(int i)
        {
            float a = i * Mathf.PI * 2 / Seg;
            return hMin + hVar * (0.5f + 0.28f * Mathf.Sin(a * 3 + seed) + 0.14f * Mathf.Sin(a * 7 + seed * 2.3f) + 0.08f * Mathf.Sin(a * 17 + seed * 0.7f));
        }
        for (int i = 0; i < Seg; i++)
        {
            float a0 = i * Mathf.PI * 2 / Seg, a1 = (i + 1) * Mathf.PI * 2 / Seg;
            var p0 = new Vector3(cx + Mathf.Cos(a0) * radius, -4, cy + Mathf.Sin(a0) * radius);
            var p1 = new Vector3(cx + Mathf.Cos(a1) * radius, -4, cy + Mathf.Sin(a1) * radius);
            // slope back from the viewer so the hill catches the light like a hillside, not a wall
            var t0 = new Vector3(cx + Mathf.Cos(a0) * (radius + 140), H(i), cy + Mathf.Sin(a0) * (radius + 140));
            var t1 = new Vector3(cx + Mathf.Cos(a1) * (radius + 140), H(i + 1), cy + Mathf.Sin(a1) * (radius + 140));
            var inward = new Vector3(cx, 60, cy) - (p0 + p1) * 0.5f;
            Quad(b, p0, p1, t1, t0, inward, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
        }
        Emit("Hills", b, WorldBuilder.Mat(col, null, 0f), parent, false);
    }

    // rows of town houses behind the walls of a street circuit
    static void Town(Track t, Transform parent)
    {
        var win = WorldBuilder.Tex(64, 64, (u, v, x, y) =>
        {
            bool glass = u > 0.22f && u < 0.78f && v > 0.25f && v < 0.80f;
            bool frame = u > 0.18f && u < 0.82f && v > 0.21f && v < 0.84f;
            if (glass) return new Color32(70, 95, 120, 255);
            if (frame) return new Color32(225, 225, 220, 255);
            byte w = (byte)(236 + (int)(Rand(x / 8, y / 8) * 14));
            return new Color32(w, w, w, 255);
        });
        Color[] cols = { new Color(0.93f, 0.86f, 0.72f), new Color(0.90f, 0.74f, 0.62f), new Color(0.96f, 0.93f, 0.88f), new Color(0.86f, 0.80f, 0.66f) };
        var walls = new Buf[cols.Length]; for (int i = 0; i < walls.Length; i++) walls[i] = new Buf();
        var roofs = new Buf();
        float hw = (float)t.width / 2, clear = hw + WorldBuilder.Kerb + 5.5f;
        int N = t.N, step = Mathf.Max(1, Mathf.RoundToInt(13f / (float)t.ds)), count = 0;
        for (int i = 0; i < N; i += step)
        {
            var p = t.pts[i];
            for (int side = -1; side <= 1; side += 2)
            {
                int id = i * 2 + (side > 0 ? 1 : 0);
                if (Rand(id, 5) < 0.12f) continue; // gaps between blocks
                float depth = 10 + Rand(id, 1) * 8, width = 12.4f, height = 9 + Mathf.Floor(Rand(id, 2) * 7) * 3.2f;
                float off = clear + depth / 2;
                float bx = (float)(p.x + p.nx * off * side), by = (float)(p.y + p.ny * off * side);
                // keep clear of every other part of the circuit
                bool free = true; float need = hw + WorldBuilder.Kerb + 3.5f + depth / 2 + 3; need *= need;
                for (int k = 0; k < N; k += 2) { var q = t.pts[k]; float dx = (float)q.x - bx, dy = (float)q.y - by; if (dx * dx + dy * dy < need) { free = false; break; } }
                if (!free) continue;
                var c = new Vector3(bx, 0, by);
                var tx = new Vector3((float)p.tx, 0, (float)p.ty) * (width / 2);
                var nx = new Vector3((float)p.nx, 0, (float)p.ny) * (depth / 2);
                var up = Vector3.up * height;
                Vector3[] corner = { c - tx - nx, c + tx - nx, c + tx + nx, c - tx + nx };
                var b = walls[(int)(Rand(id, 3) * cols.Length) % cols.Length];
                for (int e = 0; e < 4; e++)
                {
                    Vector3 a0 = corner[e], a1 = corner[(e + 1) % 4];
                    float wlen = (a1 - a0).magnitude, uu = Mathf.Round(wlen / 3.1f), vv = Mathf.Round(height / 3.2f);
                    Quad(b, a0, a1, a1 + up, a0 + up, (a0 + a1) * 0.5f - c, new Vector2(0, 0), new Vector2(uu, 0), new Vector2(uu, vv), new Vector2(0, vv));
                }
                Quad(roofs, corner[0] + up, corner[1] + up, corner[2] + up, corner[3] + up, Vector3.up, Vector2.zero, Vector2.right, Vector2.one, Vector2.up);
                count++;
            }
        }
        for (int i = 0; i < walls.Length; i++) Emit("Town", walls[i], WorldBuilder.Mat(cols[i], win, 0.25f), parent, true);
        Emit("TownRoofs", roofs, WorldBuilder.Mat(new Color(0.62f, 0.33f, 0.24f), null, 0.1f), parent, false);
    }

    // the dark band of laid-down rubber along the racing line
    public static GameObject Rubber(Track t, RacingLine line, Transform parent)
    {
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
        var tex = new Texture2D(32, 4, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
        var px = new Color32[32 * 4];
        for (int y = 0; y < 4; y++) for (int x = 0; x < 32; x++)
        {
            float u = (x + 0.5f) / 32f, e = Mathf.Clamp01(Mathf.Min(u, 1 - u) * 3f);
            px[y * 32 + x] = new Color32(10, 10, 12, (byte)(e * e * 70));
        }
        tex.SetPixels32(px); tex.Apply(false); mat.mainTexture = tex;
        var mesh = new Mesh { name = "Rubber", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(b.v); mesh.SetUVs(0, b.uv); mesh.SetTriangles(b.tri, 0); mesh.RecalculateBounds();
        var go = new GameObject("Rubber"); go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return go;
    }
}
