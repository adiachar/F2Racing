// RacingLineView.cs — the racing line drawn on the road: one thin ribbon mesh along RacingLine,
// split into three submeshes (green = accelerate, yellow = at the cornering limit, red = brake),
// one material each (the Standard shader ignores vertex colours).
// Physics (x, y) maps to Unity (x, height, y), as in WorldBuilder.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public static class RacingLineView
{
    const float HalfWidth = 0.3f;        // 0.6 m wide ribbon
    const float Height = 0.03f + 0.02f;  // road surface (WorldBuilder Road strip at 0.03) + 0.02 m
    static readonly Color Green = new Color(0.25f, 0.95f, 0.4f), Yellow = new Color(1f, 0.85f, 0.15f), Red = new Color(1f, 0.18f, 0.12f);

    public static GameObject Build(Track t, RacingLine line, Transform parent)
    {
        int N = line.N;
        var v = new List<Vector3>(N * 2); var uv = new List<Vector2>(N * 2);
        double dist = 0;
        for (int i = 0; i < N; i++)
        {
            int o = (i - 1 + N) % N, q = (i + 1) % N;
            double tx = line.x[q] - line.x[o], ty = line.y[q] - line.y[o], l = System.Math.Sqrt(tx * tx + ty * ty);
            if (l < 1e-9) { tx = t.pts[i].tx; ty = t.pts[i].ty; } else { tx /= l; ty /= l; }
            // left (+normal = (-ty, tx)) then right
            v.Add(new Vector3((float)(line.x[i] - ty * HalfWidth), Height, (float)(line.y[i] + tx * HalfWidth)));
            v.Add(new Vector3((float)(line.x[i] + ty * HalfWidth), Height, (float)(line.y[i] - tx * HalfWidth)));
            float vv = (float)(dist / 2.2); uv.Add(new Vector2(0, vv)); uv.Add(new Vector2(1, vv));
            dist += line.ds[i];
        }
        // segment i -> i+1 (wrapping) goes to the submesh of its phase: 0 = accelerate, 1 = coast, 2 = brake
        var tris = new[] { new List<int>(), new List<int>(), new List<int>() };
        for (int i = 0; i < N; i++)
        {
            int g = line.phase[i] > 0 ? 0 : line.phase[i] == 0 ? 1 : 2;
            int k = i * 2, n = ((i + 1) % N) * 2; // k = left, k+1 = right, n = next left, n+1 = next right
            var tri = tris[g];
            tri.Add(k); tri.Add(n); tri.Add(k + 1);       // same winding as WorldBuilder.Strip (faces up)
            tri.Add(n); tri.Add(n + 1); tri.Add(k + 1);
        }
        var cols = new[] { Green, Yellow, Red };
        var used = new List<int>();
        for (int g = 0; g < 3; g++) if (tris[g].Count > 0) used.Add(g);

        var mesh = new Mesh { name = "RacingLine", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(v); mesh.SetUVs(0, uv);
        mesh.subMeshCount = used.Count;
        var mats = new Material[used.Count];
        for (int s = 0; s < used.Count; s++)
        {
            mesh.SetTriangles(tris[used[s]], s);
            mats[s] = WorldBuilder.Mat(cols[used[s]], null, 0.3f, 0f);
        }
        mesh.RecalculateNormals(); mesh.RecalculateBounds();

        var go = new GameObject("RacingLine");
        if (parent != null) go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterials = mats;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        return go;
    }
}
