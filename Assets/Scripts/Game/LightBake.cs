// LightBake.cs — lighting worked out once, when the track loads, and stored in the vertex colours.
// The sun never moves and the world is built by code, so nothing has to be computed again per frame:
//  * a "roof map" (height of the tallest thing in every 1.5 m square) is drawn from all the world meshes;
//  * every vertex then gets: sunlight (if a ray towards the sun clears the roof map), sky light, and a darker
//    tone close to walls and buildings (ambient occlusion).
// The world is then drawn by a shader that only reads a texture and that colour (Baked.shader): building shadows
// across the street, dark wall bases and soft shading at almost no cost per frame, with no shadow maps.
// Cars are lit by their own light, which dims when the player's car drives into a baked shadow.
// Light model (load time only):
//  * sun: one march over the roof map per vertex; the closest the ray passes over an obstacle, against a penumbra
//    that widens with distance, gives soft shadow edges (no extra rays);
//  * sky light: blue from above, warm bounce from the sunlit ground below (from the sun's colour and height);
//  * ambient occlusion: a near ring (contact shadow at wall bases, kerb walls, barriers), a far ring (streets,
//    stands), and "cover" (a roof above: under grandstands, bridges and in the tunnel) - all on the ambient only;
//  * night (TimeOfDay, street circuits): floodlights on posts along the track baked in like the tunnel lamps,
//    with a cheap 3-sample occlusion test each, and the posts themselves (one mesh, glowing heads).
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
    static readonly Color LampCol = new Color(0.55f, 0.45f, 0.3f);
    // set per time of day by Sky.Setup (SetLight: the sun in linear x intensity); these defaults are the afternoon
    static Color SunCol = new Color(1f, 0.89f, 0.71f) * 1.45f, Sky = new Color(0.37f, 0.45f, 0.58f), Bounce;
    static readonly Color GroundAlbedo = new Color(0.36f, 0.31f, 0.24f) * 0.75f; // warm: asphalt, dry grass, sand
    static bool night;
    // the open sky on a clear day is a big, bright light: shade is a clean blue-grey at ~0.36 of the sun side in linear
    // (~0.58 on screen after the grade: crisp, readable shadows, Real Racing look), not the near-black of a single sun
    // lamp, nor the flat, blue-cast look of a sky as strong as the sun. 1 at night (the floodlights do the work).
    static float ambGainDay = 1.32f;
    // (depth pass: sky fill lowered ~40 % and the sun raised ~22 %, so the sunlit total is unchanged but shade is ~0.45-0.5 of sunlit on screen)
    static float AmbGain => night ? 1f : ambGainDay;
    public static Vector3 ToSun => toSun;
    public static bool Night => night;
    static Track track;

    public static void SetLight(Color sunTimesIntensity, Color sky, bool isNight, float ambGain = 1.32f) { SunCol = sunTimesIntensity; Sky = sky; night = isNight; ambGainDay = ambGain; }

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
        // light bounced off the ground: the sun on it (by its height) plus the sky, times a warm ground colour
        Bounce = GroundAlbedo * (SunCol * Mathf.Max(0.1f, toSun.y) + Sky); Bounce.a = 1;
        track = t;
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
            if (mf.name == "Trees") { TreeCards(mesh.vertices); continue; }
            var v = mesh.vertices; var t = mesh.triangles;
            for (int k = 0; k + 2 < t.Length; k += 3) Occluder(v[t[k]], v[t[k + 1]], v[t[k + 2]]);
        }
        // GPU-instanced model trees and shrubs (ModelInstancing.cs): the same triangles the merged meshes had, world space
        var mi = ModelInstancing.ForRoot(root); if (mi != null) mi.AddOccluders();
        // model trees (ModelLib.cs): the far ones are two upright impostor cards, thin lines in this map; give every one
        // the round crown the card trees get
        for (int k = 0; k < ModelLib.TreeFeet.Count; k++)
        {
            var f = ModelLib.TreeFeet[k]; float rad = ModelLib.TreeWidth[k] * 0.33f, h = f.y + f.w * 0.8f;
            for (float z = f.z - rad; z <= f.z + rad; z += cell * 0.5f) for (float x = f.x - rad; x <= f.x + rad; x += cell * 0.5f)
                if ((x - f.x) * (x - f.x) + (z - f.z) * (z - f.z) <= rad * rad) Put(x, z, h);
        }
    }
    // one world triangle into the roof map: its edges, and its inside (roofs, ground)
    public static void Occluder(Vector3 a, Vector3 b, Vector3 c)
    {
        if (!Active) return;
        Edge(a, b); Edge(b, c); Edge(c, a);
        float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)), maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x)), minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z)), maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
        if (maxX - minX < cell && maxZ - minZ < cell) return;
        float den = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
        if (Mathf.Abs(den) < 1e-6f) return;
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

    // tree cards (Environment.Trees: quads of foot-left, foot-right, top-right, top-left): the upright cards would only
    // mark thin lines in the map, so each card stands for a round crown instead, a third of its width across, at
    // four fifths of its height
    static void TreeCards(Vector3[] v)
    {
        for (int k = 0; k + 3 < v.Length; k += 4)
        {
            Vector3 b0 = v[k], b1 = v[k + 1]; float top = v[k + 2].y, foot = Mathf.Min(b0.y, b1.y);
            float cx = (b0.x + b1.x) * 0.5f, cz = (b0.z + b1.z) * 0.5f, rad = Vector3.Distance(b0, b1) * 0.33f, h = foot + (top - foot) * 0.8f;
            for (float z = cz - rad; z <= cz + rad; z += cell * 0.5f) for (float x = cx - rad; x <= cx + rad; x += cell * 0.5f)
                if ((x - cx) * (x - cx) + (z - cz) * (z - cz) <= rad * rad) Put(x, z, h);
        }
    }
    static void Edge(Vector3 a, Vector3 b)
    {
        float len = Mathf.Sqrt((b.x - a.x) * (b.x - a.x) + (b.z - a.z) * (b.z - a.z));
        int n = Mathf.Max(1, Mathf.CeilToInt(len / (cell * 0.5f)));
        for (int s = 0; s <= n; s++) { float u = (float)s / n; Put(a.x + (b.x - a.x) * u, a.z + (b.z - a.z) * u, a.y + (b.y - a.y) * u); }
    }

    // 1 = in the sun, 0 = in shadow, for a point at height y. At night the cars are floodlit almost everywhere,
    // so the moon's shadows only dim them a little (GameManager uses this for the cars' light).
    public static float Sun(float x, float z, float y) => night ? 0.6f + 0.4f * SunSoft(x, z, y) : SunSoft(x, z, y);

    // soft edges from one march: how close the ray passes under the top of an obstacle, against a penumbra that
    // widens with distance (0.35 m + 3.5 % of the distance, wider than the real sun so the coarse map does not show)
    static float SunSoft(float x, float z, float y)
    {
        if (!Active) return 1;
        float reach = Mathf.Min(90f, (maxH - y + 1f) / Mathf.Max(sunSlope, 0.05f)), lit = 1f;
        // steps grow with distance (the penumbra does too), so a low evening sun costs little more than noon
        for (float d = cell * 1.2f; d < reach; d += cell * (1f + d * 0.025f))
        {
            float over = Roof(x + sunDir.x * d, z + sunDir.y * d) - (y + sunSlope * d + 0.3f); // > 0: blocked
            if (over < -2f) continue;
            float f = Mathf.Clamp01(0.5f - over / (2f * (0.35f + 0.035f * d)));
            if (f < lit) { lit = f; if (lit <= 0f) return 0; }
        }
        return lit * lit * (3f - 2f * lit);
    }
    static readonly float[] ringC = { 1, 0.7071f, 0, -0.7071f, -1, -0.7071f, 0, 0.7071f }, ringS = { 0, 0.7071f, 1, 0.7071f, 0, -0.7071f, -1, -0.7071f };
    // sky visibility 0..1 for the ambient. contact: the near ring's occlusion, strongest at the foot of a wall
    // (only counts while the point is close to the lowest ground round it, so wall faces higher up stay open)
    static float Open(float x, float z, float y, Vector3 n, out float contact)
    {
        float near = 0, far = 0, low = y, mid = 0;
        for (int k = 0; k < 8; k++)
        {
            float c = ringC[k], s = ringS[k];
            float h1 = Roof(x + c * 1.2f, z + s * 1.2f);
            if (h1 > -1e5f) { near += Mathf.Clamp01((h1 - (y + 0.2f)) / 1.0f); if (h1 < low) low = h1; }
            float hm = Roof(x + c * 2.3f, z + s * 2.3f);
            if (hm > -1e5f) mid += Mathf.Clamp01((hm - (y + 0.2f)) / 1.2f);
            float h2 = Roof(x + c * 3.5f, z + s * 3.5f);
            far += Mathf.Clamp01((h2 - (y + 0.3f)) / 4f);
        }
        // a roof over the point (grandstand, bridge, tunnel): high cells all round (not just on one side, which is a
        // wall), and a roof one cell out along the normal, so a wall's own top does not count for the wall's face
        float all = Mathf.Clamp01((near / 8f - 0.6f) * 3f);
        float hc = Roof(x + n.x * cell, z + n.z * cell) - y;
        float cover = hc > 2f && hc < 30f ? Mathf.Clamp01((hc - 2f) / 2f) * all : 0f;
        // under a roof every sample is high: that says nothing about a wall close by
        contact = Mathf.Max(near / 8f, 0.7f * mid / 8f) * Mathf.Clamp01(1f - (y - low - 0.2f) / 1.2f) * (1f - all);
        // (kept gentle: sunny-day shade is open to a bright sky, so corners and wall feet darken, never go black)
        return Mathf.Max(0.22f, (1f - 0.6f * contact) * (1f - 0.4f * far / 8f) * (1f - 0.7f * cover));
    }

    // light every mesh under root and hand it to the baked shader
    public static void Apply(GameObject root)
    {
        if (!Active) return;
        var sw = System.Diagnostics.Stopwatch.StartNew(); int verts = 0;
        if (night) PlaceLamps(); else lamps = null;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = mf.sharedMesh; var r = mf.GetComponent<MeshRenderer>();
            if (mesh == null || r == null || r.sharedMaterial == null || !mf.transform.localToWorldMatrix.isIdentity) continue;
            var sh = r.sharedMaterial.shader; if (sh == null) continue;
            string sn = sh.name;
            bool own = sn == "F2/Baked" || sn == "F2/Ground" || sn == "F2/Foliage"; // baked-light shaders already
            if (sn != "Legacy Shaders/Diffuse" && sn != "Standard" && !own) continue; // not the see-through or glowing ones
            if (r.sharedMaterial.IsKeywordEnabled("_EMISSION")) continue;                               // lamps keep their glow
            if (mesh.normals == null || mesh.normals.Length != mesh.vertexCount) mesh.RecalculateNormals(); // never fall back to 'all up': flat
            var v = mesh.vertices; var n = mesh.normals; var col = new Color[v.Length];
            bool hasN = n != null && n.Length == v.Length; int hint = -1, hint2 = -1;
            bool groundShade = (mf.name == "Verge" || mf.name == "Gravel") && track != null && !night; // alpha: sign + sun share for the shadow mask (Ground.cginc)
            bool foliage = sn == "F2/Foliage"; bool cards = mf.name == "Trees" && v.Length % 4 == 0;
            // load time: the bake of one vertex only reads (the roof map, the height field, the track, the lamps) and
            // writes its own colour, so big meshes are baked in parallel chunks (pure math, no Unity API off the main
            // thread; mesh.colors is set below on the main thread). Each chunk starts its Nearest() search afresh.
            if (v.Length >= 2 * BakeChunk)
                FormulaSim.Par.For(0, (v.Length + BakeChunk - 1) / BakeChunk, ch =>
                {
                    int h1 = -1, h2 = -1, e = System.Math.Min(v.Length, (ch + 1) * BakeChunk);
                    for (int i = ch * BakeChunk; i < e; i++) col[i] = BakeVertex(i, v, n, hasN, groundShade, foliage, cards, ref h1, ref h2);
                });
            else
                for (int i = 0; i < v.Length; i++) col[i] = BakeVertex(i, v, n, hasN, groundShade, foliage, cards, ref hint, ref hint2);
            mesh.colors = col; verts += v.Length;
            if (!own) r.sharedMaterial.shader = shader;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        }
        // GPU-instanced model trees and shrubs (ModelInstancing.cs): every instance vertex lit exactly like the merged meshes
        var mi = ModelInstancing.ForRoot(root); if (mi != null) verts += mi.Bake();
        if (night && lamps != null) BuildPosts(root.transform);
        Debug.Log("LightBake: " + verts + " vertices in " + sw.ElapsedMilliseconds + " ms (" + System.Environment.ProcessorCount + " cores)");
    }

    const int BakeChunk = 2048;
    // foliage vertices that are not in a mesh (ModelInstancing: world positions and normals of every instance vertex):
    // the same light as a vertex of a mesh on F2/Foliage gets in Apply (rgb halved, alpha 1), in the same parallel chunks
    public static void BakeFoliage(Vector3[] v, Vector3[] n, Color[] col)
    {
        if (!Active || v == null || col == null) return;
        bool hasN = n != null && n.Length == v.Length; int hint = -1, hint2 = -1;
        if (v.Length >= 2 * BakeChunk)
            FormulaSim.Par.For(0, (v.Length + BakeChunk - 1) / BakeChunk, ch =>
            {
                int h1 = -1, h2 = -1, e = System.Math.Min(v.Length, (ch + 1) * BakeChunk);
                for (int i = ch * BakeChunk; i < e; i++) col[i] = BakeVertex(i, v, n, hasN, false, true, false, ref h1, ref h2);
            });
        else
            for (int i = 0; i < v.Length; i++) col[i] = BakeVertex(i, v, n, hasN, false, true, false, ref hint, ref hint2);
    }
    // the light of vertex i (rgb halved: the shader doubles it; alpha: the ground's shadow-mask data)
    static Color BakeVertex(int i, Vector3[] v, Vector3[] n, bool hasN, bool groundShade, bool foliage, bool cards, ref int hint, ref int hint2)
    {
        Vector3 p = v[i], nn = hasN ? n[i] : Vector3.up;
        // merged meshes share vertices between a wall and its top / the ground, so their smooth normals lean
        // upwards: push the sideways part out so walls keep a clear sun side and shade side
        if (nn.y < 0.92f && nn.y > -0.5f) { nn.x *= 1.5f; nn.z *= 1.5f; nn.Normalize(); }
        float ndl = Mathf.Max(0, Vector3.Dot(nn, toSun));
        float sun = ndl > 0.01f ? SunSoft(p.x + nn.x * 0.2f, p.z + nn.z * 0.2f, p.y + 0.05f) : 0;
        float ao = Open(p.x + nn.x * 0.3f, p.z + nn.z * 0.3f, p.y, nn, out float contact);
        // hemisphere: sky from above, warm bounce from the ground below, half of each on a wall
        Color amb = Color.Lerp(Bounce, Sky, 0.5f + 0.5f * nn.y) * AmbGain;
        Color sunPart = SunCol * (ndl * sun);
        Color c = sunPart + amb * ao;
        // dark where it meets the ground: contact darkening on the sun side as well (fill from a bright sky never reaches a crease)
        c *= 1f - 0.3f * contact;
        if (foliage || cards)
        {
            // crowns and bushes: the interior and the trunk end are shaded, the top catches the light
            float hAbove = cards ? (i & 3) < 2 ? 0f : 1f : Mathf.Clamp01((p.y - WorldBuilder.H(p.x, p.z)) / 3f);
            c *= 0.5f + 0.5f * Mathf.SmoothStep(0f, 1f, cards ? hAbove * 0.8f + 0.2f * ao : hAbove);
        }
        if (night && lamps != null) c += Flood(p, nn) * (1f - 0.35f * contact);
        // inside a tunnel: no sky, warm lamp light instead (darker into the corners)
        if (sun < 0.5f && Roof(p.x, p.z) - p.y > 1f && Roof(p.x, p.z) - p.y < 9f && Waterfront.InTunnel(p.x, p.z)) c = (amb * 0.15f + LampCol) * TunnelAo(p, ref hint);
        float alpha = 1f;
        if (groundShade)
        {
            // sign of the side (+ = left) and the sun's share of this light, for Ground.cginc's shadow-mask lookup
            var q = track.Nearest(p.x, p.z, hint2); hint2 = q.idx;
            float sf = Mathf.Clamp01((sunPart.r * 0.3f + sunPart.g * 0.59f + sunPart.b * 0.11f) / Mathf.Max(0.001f, c.r * 0.3f + c.g * 0.59f + c.b * 0.11f));
            alpha = q.lateral >= 0 ? 0.55f + 0.45f * sf : 0.45f * sf;
        }
        return new Color(c.r * 0.5f, c.g * 0.5f, c.b * 0.5f, alpha); // the shader doubles it
    }

    // the roof map cannot see a tunnel's walls (its roof covers them), so the tunnel's contact shadow comes from the
    // track: darker towards the walls (past the track edge) and close to the floor
    static float TunnelAo(Vector3 p, ref int hint)
    {
        if (track == null) return 1f;
        var q = track.Nearest(p.x, p.z, hint); hint = q.idx;
        float edge = Mathf.Clamp01(((float)System.Math.Abs(q.lateral) - (float)track.HalfWidth(q.idx) + 1f) / 2.5f);
        float low = Mathf.Clamp01(1f - (p.y - WorldBuilder.H(p.x, p.z)) / 2f);
        return 1f - 0.55f * edge * low - 0.15f * edge;
    }

    // ---------------------------------------------------------------- night: floodlights along the track
    const float LampStep = 25f, LampH = 11f, LampR0 = 14f, LampMax = 45f, LampI = 0.9f, LampCell = 45f;
    static readonly Color FloodCol = new Color(1f, 0.94f, 0.84f);
    static List<Vector3> lamps; static List<Vector3> lampBase;
    static Dictionary<long, List<int>> lampGrid;
    static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

    // one post every 25 m, sides alternating, just behind the barrier, the head 1.8 m out over the track edge
    static void PlaceLamps()
    {
        lamps = new List<Vector3>(); lampBase = new List<Vector3>(); lampGrid = new Dictionary<long, List<int>>();
        var t = track; if (t == null || t.pts == null || t.N < 2) { lamps = null; return; }
        double barrier = t.def != null ? t.def.barrier : 3;
        int every = Mathf.Max(1, Mathf.RoundToInt(LampStep / (float)t.ds)), side = 1;
        for (int i = 0; i < t.N; i += every, side = -side)
        {
            var q = t.pts[i]; float off = (float)(t.HalfWidth(i) + System.Math.Min(barrier, 6) + 0.6);
            float bx = (float)(q.x + q.nx * off * side), bz = (float)(q.y + q.ny * off * side);
            if (Waterfront.InTunnel(bx, bz) || Waterfront.InTunnel((float)q.x, (float)q.y) || Waterfront.InWater(bx, bz)) continue;
            float gy = WorldBuilder.H(q.x, q.y);
            float hx = (float)(q.x + q.nx * (off - 1.8f) * side), hz = (float)(q.y + q.ny * (off - 1.8f) * side);
            int k = lamps.Count;
            lamps.Add(new Vector3(hx, gy + LampH, hz)); lampBase.Add(new Vector3(bx, gy, bz));
            long key = Key(Mathf.FloorToInt(hx / LampCell), Mathf.FloorToInt(hz / LampCell));
            if (!lampGrid.TryGetValue(key, out var list)) lampGrid[key] = list = new List<int>();
            list.Add(k);
        }
        if (lamps.Count == 0) lamps = null;
    }

    // light from the floodlights at a point: soft inverse-square falloff cut off at 45 m, a little wrap so the
    // ground under a lamp is lit evenly, and 3 roof-map samples on the way for buildings in between
    static Color Flood(Vector3 p, Vector3 n)
    {
        float sum = 0; int ci = Mathf.FloorToInt(p.x / LampCell), cj = Mathf.FloorToInt(p.z / LampCell);
        for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
        {
            if (!lampGrid.TryGetValue(Key(ci + di, cj + dj), out var list)) continue;
            for (int m = 0; m < list.Count; m++)
            {
                var L = lamps[list[m]]; Vector3 d = L - p; float d2 = d.sqrMagnitude;
                if (d2 > LampMax * LampMax || d2 < 1e-4f) continue;
                float dist = Mathf.Sqrt(d2), ndl = Vector3.Dot(n, d) / dist;
                if (ndl < -0.2f) continue;
                float w = 1f - d2 / (LampMax * LampMax); w *= w;
                float e = LampI / (1f + d2 / (LampR0 * LampR0)) * w * Mathf.Clamp01(ndl * 0.85f + 0.15f);
                for (int s = 1; s <= 3; s++)
                {
                    float u = s * 0.25f; Vector3 a = p + d * u;
                    if (Roof(a.x, a.z) > a.y + 0.4f) { e *= 0.15f; break; }
                }
                sum += e;
            }
        }
        return FloodCol * sum;
    }

    // the posts: one mesh (pole, arm, lamp head with a glowing underside), lit by hand in the vertex colours
    static void BuildPosts(Transform root)
    {
        var v = new List<Vector3>(); var nrm = new List<Vector3>(); var col = new List<Color>(); var tri = new List<int>();
        Color dark = new Color(0.07f, 0.07f, 0.075f, 1), glow = new Color(1.6f, 1.5f, 1.32f, 1); // stored halved: glow = 3.2x
        for (int i = 0; i < lamps.Count; i++)
        {
            Vector3 b = lampBase[i], h = lamps[i];
            Vector3 dir = new Vector3(h.x - b.x, 0, h.z - b.z); float len = dir.magnitude; dir = len > 1e-3f ? dir / len : Vector3.forward;
            var rot = Quaternion.LookRotation(dir, Vector3.up);
            Box(v, nrm, col, tri, b + Vector3.up * (LampH * 0.5f + 0.3f), rot, new Vector3(0.22f, LampH + 0.6f, 0.22f), dark, dark);
            Box(v, nrm, col, tri, new Vector3((b.x + h.x) * 0.5f, h.y + 0.25f, (b.z + h.z) * 0.5f), rot, new Vector3(0.12f, 0.12f, len), dark, dark);
            Box(v, nrm, col, tri, h + Vector3.up * 0.1f, rot, new Vector3(1.1f, 0.22f, 0.6f), dark, glow);
        }
        var mesh = new Mesh { name = "Floodlights" };
        if (v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(v); mesh.SetNormals(nrm); mesh.SetColors(col); mesh.SetUVs(0, new Vector2[v.Count]); mesh.SetTriangles(tri, 0);
        mesh.RecalculateBounds();
        var go = new GameObject("Floodlights"); go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = new Material(shader) { mainTexture = Texture2D.whiteTexture };
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        lamps = null; lampBase = null; lampGrid = null;
    }

    // a box: 6 faces, 4 vertices each; the bottom face gets its own colour (the lamp's glowing glass)
    static void Box(List<Vector3> v, List<Vector3> n, List<Color> c, List<int> t, Vector3 centre, Quaternion rot, Vector3 size, Color side, Color bottom)
    {
        Vector3 hx = rot * Vector3.right * (size.x * 0.5f), hy = rot * Vector3.up * (size.y * 0.5f), hz = rot * Vector3.forward * (size.z * 0.5f);
        void Face(Vector3 normal, Vector3 a, Vector3 b, Color fc)
        {
            int k = v.Count; Vector3 f = centre + normal;
            v.Add(f - a - b); v.Add(f - a + b); v.Add(f + a + b); v.Add(f + a - b);
            Vector3 nn = normal.normalized; for (int q = 0; q < 4; q++) { n.Add(nn); c.Add(fc); }
            // wind so the face points along its normal
            if (Vector3.Dot(Vector3.Cross(b, a), normal) > 0) { t.Add(k); t.Add(k + 1); t.Add(k + 2); t.Add(k); t.Add(k + 2); t.Add(k + 3); }
            else { t.Add(k); t.Add(k + 2); t.Add(k + 1); t.Add(k); t.Add(k + 3); t.Add(k + 2); }
        }
        Face(hx, hy, hz, side); Face(-hx, hy, hz, side); Face(hz, hx, hy, side); Face(-hz, hx, hy, side);
        Face(hy, hx, hz, side); Face(-hy, hx, hz, bottom);
    }
}
