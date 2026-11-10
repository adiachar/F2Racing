// LightBake.cs — lighting worked out once, when the track loads, and stored in the vertex colours.
// The sun never moves and the world is built by code, so nothing has to be computed again per frame:
//  * a "roof map" (height of the tallest thing in every 1.5 m square) is drawn from all the world meshes;
//  * every vertex then gets: sunlight (if a ray towards the sun clears the roof map), sky light, and a darker
//    tone close to walls and buildings (ambient occlusion).
// The world is then drawn by a shader that only reads a texture and that colour (Baked.shader): building shadows
// across the street, dark wall bases and soft shading at almost no cost per frame, with no shadow maps.
// Cars are lit by their own light, which dims when the player's car drives into a baked shadow.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public static class LightBake
{
    public static bool Active;
    public static Light CarSun;
    public const int CarLayer = 4;
    static float[] roof; static int nx, nz; static float x0, z0, cell, maxH;
    static Vector3 toSun; static Vector2 sunDir; static float sunSlope;
    static Shader shader;
    static readonly Color SunCol = new Color(1f, 0.95f, 0.86f) * 1.25f, Sky = new Color(0.62f, 0.72f, 0.85f), Side = new Color(0.55f, 0.6f, 0.6f), Below = new Color(0.3f, 0.35f, 0.25f);

    public static bool Begin(Track t, Vector3 towardsSun)
    {
        Active = false;
        if (shader == null) shader = Resources.Load<Shader>("Baked");
        if (shader == null || !shader.isSupported) return false;
        var bb = t.Bounds(); const float M = 130;
        float ext = (float)System.Math.Max(bb.maxX - bb.minX, bb.maxY - bb.minY);
        cell = ext > 1500 ? 2.5f : 1.5f;
        x0 = (float)bb.minX - M; z0 = (float)bb.minY - M;
        nx = Mathf.CeilToInt(((float)(bb.maxX - bb.minX) + 2 * M) / cell) + 1; nz = Mathf.CeilToInt(((float)(bb.maxY - bb.minY) + 2 * M) / cell) + 1;
        roof = new float[nx * nz]; for (int i = 0; i < roof.Length; i++) roof[i] = -1e6f;
        maxH = -1e6f;
        toSun = towardsSun.normalized;
        float hl = Mathf.Sqrt(toSun.x * toSun.x + toSun.z * toSun.z);
        sunDir = new Vector2(toSun.x, toSun.z) / Mathf.Max(hl, 1e-4f); sunSlope = toSun.y / Mathf.Max(hl, 1e-4f);
        Active = true;
        return true;
    }

    static void Put(float x, float z, float y)
    {
        int i = (int)((x - x0) / cell), j = (int)((z - z0) / cell);
        if (i < 0 || j < 0 || i >= nx || j >= nz) return;
        int k = j * nx + i; if (y > roof[k]) { roof[k] = y; if (y > maxH) maxH = y; }
    }
    static float Roof(float x, float z)
    {
        int i = (int)((x - x0) / cell), j = (int)((z - z0) / cell);
        if (i < 0 || j < 0 || i >= nx || j >= nz) return -1e6f;
        return roof[j * nx + i];
    }

    // draw everything under root into the roof map
    public static void AddOccluders(GameObject root)
    {
        if (!Active) return;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = mf.sharedMesh; if (mesh == null) continue;
            if (!mf.transform.localToWorldMatrix.isIdentity)
            {
                // placed objects (trees, cones): a round footprint at most of their height
                var r = mf.GetComponent<Renderer>(); if (r == null) continue;
                var b = r.bounds; if (b.size.y < 1.5f || b.size.x > 30f) continue;
                float rx = b.extents.x, rz = b.extents.z;
                for (float z = b.min.z; z <= b.max.z; z += cell * 0.5f) for (float x = b.min.x; x <= b.max.x; x += cell * 0.5f)
                {
                    float ux = (x - b.center.x) / Mathf.Max(rx, 0.01f), uz = (z - b.center.z) / Mathf.Max(rz, 0.01f);
                    if (ux * ux + uz * uz <= 1) Put(x, z, b.min.y + b.size.y * 0.85f);
                }
                continue;
            }
            var v = mesh.vertices; var t = mesh.triangles;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                Vector3 a = v[t[k]], b = v[t[k + 1]], c = v[t[k + 2]];
                Edge(a, b); Edge(b, c); Edge(c, a);
                // inside of the triangle (roofs, ground)
                float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)), maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x)), minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z)), maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
                if (maxX - minX < cell && maxZ - minZ < cell) continue;
                float den = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(den) < 1e-6f) continue;
                int i0 = Mathf.Max(0, (int)((minX - x0) / cell)), i1 = Mathf.Min(nx - 1, (int)((maxX - x0) / cell)), j0 = Mathf.Max(0, (int)((minZ - z0) / cell)), j1 = Mathf.Min(nz - 1, (int)((maxZ - z0) / cell));
                for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++)
                {
                    float px = x0 + (i + 0.5f) * cell, pz = z0 + (j + 0.5f) * cell;
                    float wa = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / den, wb = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / den, wc = 1 - wa - wb;
                    if (wa < 0 || wb < 0 || wc < 0) continue;
                    float y = wa * a.y + wb * b.y + wc * c.y; int q = j * nx + i;
                    if (y > roof[q]) { roof[q] = y; if (y > maxH) maxH = y; }
                }
            }
        }
    }
    static void Edge(Vector3 a, Vector3 b)
    {
        float len = Mathf.Sqrt((b.x - a.x) * (b.x - a.x) + (b.z - a.z) * (b.z - a.z));
        int n = Mathf.Max(1, Mathf.CeilToInt(len / (cell * 0.5f)));
        for (int s = 0; s <= n; s++) { float u = (float)s / n; Put(a.x + (b.x - a.x) * u, a.z + (b.z - a.z) * u, a.y + (b.y - a.y) * u); }
    }

    // 1 = in the sun, 0 = in shadow, for a point at height y
    public static float Sun(float x, float z, float y)
    {
        if (!Active) return 1;
        float reach = Mathf.Min(90f, (maxH - y) / Mathf.Max(sunSlope, 0.05f));
        for (float d = cell * 1.2f; d < reach; d += cell)
            if (Roof(x + sunDir.x * d, z + sunDir.y * d) > y + sunSlope * d + 0.3f) return 0;
        return 1;
    }
    static float Open(float x, float z, float y)
    {
        float occ = 0;
        for (int k = 0; k < 8; k++)
        {
            float a = k * Mathf.PI / 4, h = Roof(x + Mathf.Cos(a) * 2.6f, z + Mathf.Sin(a) * 2.6f);
            occ += Mathf.Clamp01((h - (y + 0.3f)) / 3f);
        }
        return 1 - 0.55f * occ / 8f;
    }

    // light every mesh under root and hand it to the baked shader
    public static void Apply(GameObject root)
    {
        if (!Active) return;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = mf.sharedMesh; var r = mf.GetComponent<MeshRenderer>();
            if (mesh == null || r == null || r.sharedMaterial == null || !mf.transform.localToWorldMatrix.isIdentity) continue;
            var sh = r.sharedMaterial.shader; if (sh == null) continue;
            string sn = sh.name;
            if (sn != "Legacy Shaders/Diffuse" && sn != "Standard" && sn != "F2/Baked") continue; // not the see-through or glowing ones
            var v = mesh.vertices; var n = mesh.normals; var col = new Color[v.Length];
            bool hasN = n != null && n.Length == v.Length;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 p = v[i], nn = hasN ? n[i] : Vector3.up;
                float ndl = Mathf.Max(0, Vector3.Dot(nn, toSun));
                float sun = ndl > 0.01f ? Sun(p.x + nn.x * 0.2f, p.z + nn.z * 0.2f, p.y + 0.05f) : 0;
                float ao = Open(p.x + nn.x * 0.3f, p.z + nn.z * 0.3f, p.y);
                Color amb = nn.y >= 0 ? Color.Lerp(Side, Sky, nn.y) : Color.Lerp(Side, Below, -nn.y);
                Color c = SunCol * (ndl * sun) + amb * ao;
                col[i] = new Color(c.r * 0.5f, c.g * 0.5f, c.b * 0.5f, 1); // the shader doubles it
            }
            mesh.colors = col;
            if (sn != "F2/Baked") r.sharedMaterial.shader = shader;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        }
    }
}
