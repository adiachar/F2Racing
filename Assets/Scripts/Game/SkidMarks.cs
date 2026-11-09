// SkidMarks.cs — dark rubber streaks left on the asphalt when a tyre slides, spins or locks.
// One mesh with a fixed ring buffer of quads (oldest marks are reused), one draw call, no allocations per frame.
using UnityEngine;

public class SkidMarks
{
    const int MaxQuads = 700;
    const float HalfWidth = 0.17f, Height = 0.045f, MinStep = 0.35f;
    readonly Vector3[] verts = new Vector3[MaxQuads * 4];
    readonly Mesh mesh;
    readonly GameObject go;
    int next;
    readonly Vector3[] lastL = new Vector3[4], lastR = new Vector3[4], lastP = new Vector3[4];
    readonly bool[] active = new bool[4];
    bool dirty;

    static Material mat;
    static Material Mat()
    {
        if (mat != null) return mat;
        mat = new Material(Resources.Load<Material>("Shadow"));
        var t = new Texture2D(16, 4, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[16 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 16; x++)
            {
                float u = (x + 0.5f) / 16f, edge = Mathf.Clamp01(Mathf.Min(u, 1 - u) * 5f);
                px[y * 16 + x] = new Color32(8, 8, 8, (byte)(edge * 120));
            }
        t.SetPixels32(px); t.Apply(false);
        mat.mainTexture = t;
        return mat;
    }

    public SkidMarks()
    {
        mesh = new Mesh { name = "SkidMarks" };
        var uv = new Vector2[MaxQuads * 4];
        var tri = new int[MaxQuads * 6];
        for (int q = 0; q < MaxQuads; q++)
        {
            int b = q * 4;
            uv[b] = new Vector2(0, 0); uv[b + 1] = new Vector2(1, 0); uv[b + 2] = new Vector2(0, 1); uv[b + 3] = new Vector2(1, 1);
            int i = q * 6;
            tri[i] = b; tri[i + 1] = b + 2; tri[i + 2] = b + 1;
            tri[i + 3] = b + 1; tri[i + 4] = b + 2; tri[i + 5] = b + 3;
        }
        mesh.vertices = verts; mesh.uv = uv; mesh.triangles = tri;
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(100000, 100, 100000));
        go = new GameObject("SkidMarks");
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = Mat();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    public void Clear()
    {
        for (int i = 0; i < verts.Length; i++) verts[i] = Vector3.zero;
        for (int w = 0; w < 4; w++) active[w] = false;
        next = 0; dirty = true;
    }

    // wheel: 0..3; pos: contact point on the ground; side: the car's right vector; on: is this tyre sliding now
    public void Track(int wheel, Vector3 pos, Vector3 side, bool on)
    {
        if (!on) { active[wheel] = false; return; }
        pos.y = Height;
        side.y = 0; side = side.normalized * HalfWidth;
        Vector3 l = pos - side, r = pos + side;
        if (!active[wheel] || (pos - lastP[wheel]).sqrMagnitude > 25f)
        {
            active[wheel] = true; lastL[wheel] = l; lastR[wheel] = r; lastP[wheel] = pos;
            return;
        }
        if ((pos - lastP[wheel]).sqrMagnitude < MinStep * MinStep) return;
        int b = next * 4;
        verts[b] = lastL[wheel]; verts[b + 1] = lastR[wheel]; verts[b + 2] = l; verts[b + 3] = r;
        next = (next + 1) % MaxQuads;
        lastL[wheel] = l; lastR[wheel] = r; lastP[wheel] = pos;
        dirty = true;
    }

    public void Upload()
    {
        if (!dirty) return;
        mesh.vertices = verts;
        dirty = false;
    }
}
