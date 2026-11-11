// BakeTint.cs — per-vertex colour detail (ambient occlusion at wall bases, dirt near impacts, crowd and tree variety)
// that survives the light bake. LightBake.Apply writes the light into the vertex colours and so overwrites anything
// stored there; builders register the tint they want here, and WorldBuilder multiplies it in right after the bake
// (before static batching). Load-time only: nothing here runs per frame.
using System.Collections.Generic;
using UnityEngine;

public static class BakeTint
{
    static readonly List<Mesh> meshes = new List<Mesh>();
    static readonly List<Color[]> tints = new List<Color[]>();

    public static void Clear() { meshes.Clear(); tints.Clear(); }

    public static void Add(Mesh m, Color[] tint)
    {
        if (m == null || tint == null || tint.Length == 0) return;
        meshes.Add(m); tints.Add(tint);
    }

    // baked: multiply the baked light by the tint. Not baked: the plain diffuse shader ignores vertex colours, so skip.
    public static void Apply(bool baked)
    {
        if (baked)
            for (int k = 0; k < meshes.Count; k++)
            {
                var m = meshes[k]; if (m == null) continue;
                var c = m.colors; var t = tints[k];
                if (c == null || c.Length != t.Length) continue;
                for (int i = 0; i < c.Length; i++) { c[i].r *= t[i].r; c[i].g *= t[i].g; c[i].b *= t[i].b; }
                m.colors = c;
            }
        Clear();
    }
}
