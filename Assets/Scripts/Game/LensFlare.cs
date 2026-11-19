// LensFlare.cs — sun lens flare in ONE draw call: a single mesh of 8 screen-space quads (sun bloom, subtle lens dirt,
// a thin blue anamorphic streak through the sun, five ghosts along the line through the screen centre), additive,
// Resources/LensFlare.shader (no textures). Created by Sky.Create; Low graphics (Sky.NoGlare) draws nothing.
// Visibility: the sun must be on screen or just off its edge, and not blocked: LightBake.Sun (the roof map the light bake
// uses: trees, buildings, grandstands, bridges, tunnels) is asked from the camera, smoothed so it fades instead of popping.
// Setting: PlayerPrefs int "LensFlare" (default 1 = on, 0 = off), re-read about once a second.
using UnityEngine;

public class LensFlare : MonoBehaviour
{
    public const string PrefKey = "LensFlare";
    const int Quads = 12;
    static readonly float[] GhostAt = { -0.32f, 0.42f, -0.70f, 1.35f, -1.15f, -0.48f, 0.78f, -1.55f };      // along sun -> centre -> opposite side
    static readonly float[] GhostH  = { 0.060f, 0.040f, 0.110f, 0.050f, 0.075f, 0.022f, 0.030f, 0.160f };    // half height, NDC
    static readonly float[] GhostK  = { 0.30f, 0.38f, 0.16f, 0.30f, 0.14f, 0.45f, 0.40f, 0.08f };         // strength
    static readonly float[] GhostShape = { 2f, 1f, 2f, 1f, 1f, 0f, 2f, 2f };
    static readonly Color[] GhostCol =
    {
        new Color(1.0f, 0.85f, 0.55f), new Color(0.45f, 0.90f, 0.75f), new Color(0.60f, 0.55f, 1.0f),
        new Color(1.0f, 0.60f, 0.40f), new Color(0.50f, 0.75f, 1.0f),
        new Color(0.70f, 1.0f, 0.60f), new Color(1.0f, 0.75f, 0.90f), new Color(0.55f, 0.70f, 1.0f),
    };

    static Material mat;
    Mesh mesh; MeshRenderer mr; Camera cam; Vector3 sunDir;
    readonly Vector3[] pos = new Vector3[Quads * 4]; readonly Color32[] col = new Color32[Quads * 4];
    float occl = 1f, vis; int prefCheck; bool prefOn = true;

    public static LensFlare Create(Transform parent, Vector3 sunDir)
    {
        if (mat == null)
        {
            var s = Resources.Load<Shader>("LensFlare");
            if (s == null || !s.isSupported) return null;
            mat = new Material(s) { name = "LensFlare" };
        }
        var go = new GameObject("LensFlare"); go.transform.SetParent(parent, false);
        var f = go.AddComponent<LensFlare>();
        f.sunDir = sunDir.normalized;
        f.Build(go);
        return f;
    }

    void Build(GameObject go)
    {
        mesh = new Mesh { name = "LensFlare" }; mesh.MarkDynamic();
        var uv = new Vector4[Quads * 4]; var tri = new int[Quads * 6];
        // quad shapes: 0 bloom, 1 dirt, 2 streak, 3..7 ghosts
        float[] shape = { 0f, 4f, 3f, GhostShape[0], GhostShape[1], GhostShape[2], GhostShape[3], GhostShape[4], GhostShape[5], GhostShape[6], GhostShape[7], 5f };
        for (int q = 0; q < Quads; q++)
        {
            for (int c = 0; c < 4; c++) uv[q * 4 + c] = new Vector4((c == 0 || c == 3) ? -1 : 1, (c < 2) ? -1 : 1, shape[q], 0);
            int b = q * 4, t = q * 6;
            tri[t] = b; tri[t + 1] = b + 2; tri[t + 2] = b + 1; tri[t + 3] = b; tri[t + 4] = b + 3; tri[t + 5] = b + 2;
        }
        mesh.vertices = pos; mesh.SetUVs(0, uv); mesh.colors32 = col; mesh.triangles = tri;
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        mr.enabled = false;
    }

    void OnDestroy() { if (mesh != null) Destroy(mesh); }

    void LateUpdate()
    {
        if (--prefCheck <= 0) { prefCheck = 60; prefOn = PlayerPrefs.GetInt(PrefKey, 1) != 0; }
        float gain = Sky.FlareGain;
        if (Sky.NoGlare || !prefOn || gain < 0.01f) { Hide(); return; }
        if (cam == null || !cam.isActiveAndEnabled) { cam = Camera.main; if (cam == null) { Hide(); return; } }
        var ct = cam.transform;
        var vp = cam.WorldToViewportPoint(ct.position + sunDir * 1000f);
        if (vp.z <= 0f) { Hide(); return; }
        // on screen, then a smooth fade over the first 14 % outside the edge
        float e = Mathf.Min(Mathf.Min(vp.x, 1f - vp.x), Mathf.Min(vp.y, 1f - vp.y));
        float v = Mathf.Clamp01((e + 0.14f) / 0.2f); v = v * v * (3f - 2f * v);
        v *= 0.5f + 0.5f * Mathf.Clamp01(Vector3.Dot(ct.forward, sunDir) * 1.6f);
        // blocked by trees / buildings / roofs: the light bake's roof map; fade at about 4 per second
        float target = LightBake.Active ? LightBake.Sun(ct.position.x, ct.position.z, ct.position.y) : 1f;
        occl = Mathf.MoveTowards(occl, target, Time.deltaTime * 4f);
        vis = v * occl * gain;
        if (vis < 0.01f) { Hide(); return; }

        float asp = (float)cam.pixelWidth / Mathf.Max(1, cam.pixelHeight);
        var s = new Vector2(vp.x * 2f - 1f, vp.y * 2f - 1f);
        float sl = Mathf.Min(1f, s.magnitude);                   // 0 sun at the centre, 1 at the edge: ghosts need an offset line
        bool evening = TimeOfDay.Current == TimeOfDay.Kind.Evening;
        var sun = evening ? new Color(1f, 0.72f, 0.42f) : new Color(1f, 0.90f, 0.72f);

        // 0 bloom (the soft sun halo itself is Sky's glare quad: this is the tight hot core)
        Quad(0, s, 0.09f / asp, 0.09f, sun, 0.45f * vis);
        // 1 dirt: a big faint glow with a mottled pattern, only when the sun is well inside the view
        Quad(1, s, 0.55f / asp, 0.55f, new Color(1f, 0.95f, 0.85f), 0.11f * vis * (1f - 0.5f * sl));
        // 2 anamorphic streak: thin, wide, blue-ish, through the sun (stronger when the sun is clear of everything)
        Quad(2, s, 0.95f, 0.014f, new Color(0.42f, 0.60f, 1f), 0.50f * vis * vis);
        // 3..10 ghosts along the line sun -> centre (hexagons from the 6-blade iris, rings, small hot dots)
        for (int g = 0; g < 8; g++)
        {
            float h = GhostH[g];
            var c = Color.Lerp(GhostCol[g], sun, 0.25f);
            Quad(3 + g, s * GhostAt[g], h / asp, h, c, GhostK[g] * vis * (0.35f + 0.65f * sl));
        }
        // 11 rainbow halo: a thin chromatic ring round the sun (dispersion in the front element), when the sun is in view
        Quad(11, s, 0.42f / asp, 0.42f, new Color(1f, 1f, 1f), 0.10f * vis * (1f - 0.6f * sl));
        if (!mr.enabled) mr.enabled = true;
        mesh.vertices = pos; mesh.colors32 = col;
    }

    void Hide() { if (mr != null && mr.enabled) mr.enabled = false; }

    void Quad(int q, Vector2 c, float hx, float hy, Color tint, float k)
    {
        k = Mathf.Clamp01(k);
        var cc = (Color32)new Color(tint.r * k, tint.g * k, tint.b * k, 1f);
        int b = q * 4;
        pos[b] = new Vector3(c.x - hx, c.y - hy, 0); pos[b + 1] = new Vector3(c.x + hx, c.y - hy, 0);
        pos[b + 2] = new Vector3(c.x + hx, c.y + hy, 0); pos[b + 3] = new Vector3(c.x - hx, c.y + hy, 0);
        for (int i = 0; i < 4; i++) col[b + i] = cc;
    }
}
