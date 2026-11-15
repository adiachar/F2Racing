// Waterfront.cs — harbour water and tunnels, built from the circuit data (CircuitDef.water*, tunnel*).
//  * The water outline follows the quay side of the track, then runs far out to sea. The terrain is pushed down
//    inside it and a flat water surface fills it; the water uses the car-paint shader, which gives sky
//    reflections that grow towards the horizon (Fresnel) at the cost of one pass.
//  * A tunnel is two walls and a roof over the track with a row of lamps; the baked lighting (LightBake) then
//    makes it dark inside, lit only by the lamps.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public static class Waterfront
{
    static List<Vector2> poly;
    static float minX, maxX, minY, maxY;
    static Track trk;

    public static bool HasWater => poly != null;

    public static void Prepare(Track t)
    {
        poly = null; trk = t; waterMat = null;
        var d = t.def;
        if (d == null || d.waterTo <= d.waterFrom) return;
        var pts = new List<Vector2>();
        const float Quay = 13f;
        for (int i = 0; i < t.N; i++)
        {
            var p = t.pts[i];
            if (p.s < d.waterFrom || p.s > d.waterTo) continue;
            // the side of the road that faces the water
            float sx = (float)(p.x + p.nx * Quay), sy = (float)(p.y + p.ny * Quay), ox = (float)(p.x - p.nx * Quay), oy = (float)(p.y - p.ny * Quay);
            float ds1 = (sx - (float)d.waterX) * (sx - (float)d.waterX) + (sy - (float)d.waterY) * (sy - (float)d.waterY);
            float ds2 = (ox - (float)d.waterX) * (ox - (float)d.waterX) + (oy - (float)d.waterY) * (oy - (float)d.waterY);
            pts.Add(ds1 < ds2 ? new Vector2(sx, sy) : new Vector2(ox, oy));
        }
        if (pts.Count < 3) return;
        // close the outline far out to sea: from the last quay point straight away from the land, round, and back
        Vector2 first = pts[0], last = pts[pts.Count - 1], c = new Vector2((float)d.waterX, (float)d.waterY);
        Vector2 outA = (last - c).normalized, outB = (first - c).normalized, away = (c - (first + last) * 0.5f).normalized;
        const float Far = 4000;
        pts.Add(last + (outA + away).normalized * Far);
        pts.Add(c + away * Far);
        pts.Add(first + (outB + away).normalized * Far);
        poly = pts;
        minX = maxX = pts[0].x; minY = maxY = pts[0].y;
        foreach (var q in pts) { minX = Mathf.Min(minX, q.x); maxX = Mathf.Max(maxX, q.x); minY = Mathf.Min(minY, q.y); maxY = Mathf.Max(maxY, q.y); }
    }

    // is (x, z) on the water? (even-odd rule)
    public static bool InWater(float x, float z)
    {
        if (poly == null || x < minX || x > maxX || z < minY || z > maxY) return false;
        bool inside = false; int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 a = poly[i], b = poly[j];
            if ((a.y > z) != (b.y > z) && x < (b.x - a.x) * (z - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }
        return inside;
    }

    // the water look (car-paint shader: sky reflections growing towards the horizon), one material per circuit load,
    // shared by the harbour and the real lakes, rivers and sea (RealWorld.cs) so they draw together
    static Material waterMat;
    public static Material WaterMaterial()
    {
        if (waterMat != null) return waterMat;
        var sh = Resources.Load<Shader>("CarPaint");
        if (sh != null && sh.isSupported)
        {
            waterMat = new Material(sh) { color = new Color(0.03f, 0.13f, 0.16f) };
            waterMat.SetFloat("_Coat", 1f); waterMat.SetFloat("_Gloss", 0.93f); waterMat.SetFloat("_Metal", 0f);
        }
        else waterMat = WorldBuilder.Mat(new Color(0.08f, 0.25f, 0.32f), null, 0.9f, 0f);
        return waterMat;
    }

    public static void BuildWater(Transform root, float level)
    {
        if (poly == null) return;
        // flat surface made of row strips over the water cells
        const float Cell = 8;
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
        float x0 = Mathf.Max(minX, -6000), x1 = Mathf.Min(maxX, 6000), z0 = Mathf.Max(minY, -6000), z1 = Mathf.Min(maxY, 6000);
        for (float z = z0; z < z1; z += Cell)
        {
            float run0 = float.NaN;
            for (float x = x0; x <= x1 + Cell; x += Cell)
            {
                bool w = x <= x1 && InWater(x + Cell / 2, z + Cell / 2);
                if (w && float.IsNaN(run0)) run0 = x;
                if (!w && !float.IsNaN(run0))
                {
                    int k = v.Count;
                    v.Add(new Vector3(run0, level, z)); v.Add(new Vector3(x, level, z)); v.Add(new Vector3(run0, level, z + Cell)); v.Add(new Vector3(x, level, z + Cell));
                    uv.Add(Vector2.zero); uv.Add(Vector2.zero); uv.Add(Vector2.zero); uv.Add(Vector2.zero);
                    tri.Add(k); tri.Add(k + 2); tri.Add(k + 1); tri.Add(k + 1); tri.Add(k + 2); tri.Add(k + 3);
                    run0 = float.NaN;
                }
            }
        }
        var go = WorldBuilder.MeshObj("Harbour", v, uv, tri, WaterMaterial(), false);
        go.transform.SetParent(root);
        go.GetComponent<MeshRenderer>().receiveShadows = false;
    }

    public static void BuildTunnel(Track t, Transform root)
    {
        var d = t.def;
        if (d == null || d.tunnelTo <= d.tunnelFrom) return;
        float hw = (float)t.width / 2, wall = hw + (float)d.barrier, height = 6.2f, roof = 1.2f;
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
        var lv = new List<Vector3>(); var luv = new List<Vector2>(); var ltri = new List<int>();
        int prev = -1;
        for (int i = 0; i < t.N; i++)
        {
            var p = t.pts[i];
            if (p.s < d.tunnelFrom || p.s > d.tunnelTo) { prev = -1; continue; }
            var n = new Vector3((float)p.nx, 0, (float)p.ny); var c = new Vector3((float)p.x, 0, (float)p.y);
            // cross-section, walking round: left floor, left ceiling, right ceiling, right floor, then the roof top
            Vector3[] ring = { c + n * wall, c + n * wall + Vector3.up * height, c - n * wall + Vector3.up * height, c - n * wall,
                               c - n * (wall + 0.6f) + Vector3.up * (height + roof), c + n * (wall + 0.6f) + Vector3.up * (height + roof) };
            int k = v.Count;
            for (int r = 0; r < ring.Length; r++) { v.Add(ring[r]); uv.Add(new Vector2(r, (float)p.s / 4f)); }
            if (prev >= 0)
            {
                // inner faces (walls and ceiling seen from the road) and the roof top seen from above
                int[,] faces = { { 0, 1 }, { 1, 2 }, { 2, 3 }, { 2, 4 }, { 4, 5 }, { 5, 1 } };
                for (int f = 0; f < faces.GetLength(0); f++)
                {
                    int a0 = prev + faces[f, 0], a1 = prev + faces[f, 1], b0 = k + faces[f, 0], b1 = k + faces[f, 1];
                    tri.Add(a0); tri.Add(b0); tri.Add(a1); tri.Add(a1); tri.Add(b0); tri.Add(b1);
                    tri.Add(a0); tri.Add(a1); tri.Add(b0); tri.Add(a1); tri.Add(b1); tri.Add(b0);
                }
            }
            prev = k;
            // a lamp every 8 m along the ceiling
            if ((int)(p.s / 8) != (int)((p.s - t.ds) / 8))
            {
                var tng = new Vector3((float)p.tx, 0, (float)p.ty);
                var lc = c + Vector3.up * (height - 0.05f); int q = lv.Count;
                lv.Add(lc - tng * 0.8f + n * 0.25f); lv.Add(lc - tng * 0.8f - n * 0.25f); lv.Add(lc + tng * 0.8f + n * 0.25f); lv.Add(lc + tng * 0.8f - n * 0.25f);
                for (int u = 0; u < 4; u++) luv.Add(Vector2.zero);
                ltri.Add(q); ltri.Add(q + 1); ltri.Add(q + 2); ltri.Add(q + 1); ltri.Add(q + 3); ltri.Add(q + 2);
                ltri.Add(q); ltri.Add(q + 2); ltri.Add(q + 1); ltri.Add(q + 1); ltri.Add(q + 2); ltri.Add(q + 3);
            }
        }
        if (v.Count == 0) return;
        WorldBuilder.MeshObj("Tunnel", v, uv, tri, WorldBuilder.Mat(new Color(0.78f, 0.74f, 0.66f), WorldBuilder.AsphaltTex(), 0.1f)).transform.SetParent(root);
        var lamps = WorldBuilder.MeshObj("TunnelLamps", lv, luv, ltri, WorldBuilder.Glow(new Color(1f, 0.85f, 0.55f), new Color(1.6f, 1.2f, 0.6f)), false);
        lamps.transform.SetParent(root);
    }

    // lamp light inside a tunnel: is (x, z) under a tunnel roof?
    public static bool InTunnel(float x, float z)
    {
        var t = trk; if (t == null || t.def == null || t.def.tunnelTo <= t.def.tunnelFrom) return false;
        var n = t.Nearest(x, z);
        var p = t.pts[n.idx];
        return p.s >= t.def.tunnelFrom && p.s <= t.def.tunnelTo && System.Math.Abs(n.lateral) < t.width / 2 + t.def.barrier + 0.3;
    }
}
