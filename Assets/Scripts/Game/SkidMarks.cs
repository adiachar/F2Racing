// SkidMarks.cs — dark rubber streaks left on the asphalt when a tyre slides, spins or locks.
// One mesh with a fixed ring buffer of quads (oldest marks are reused), one draw call, no allocations per frame.
// The vertex buffer is made once at its full size (positions in stream 0, the fixed UVs in stream 1) and only the quads
// written since the last frame are uploaded (Mesh.SetVertexBufferData on that range; a dynamic buffer). Before
// (Docs/PERF-INVESTIGATION.md, round 3) every frame with a new quad re-sent all 2800 positions (33.6 KB a frame while a
// tyre slid) through mesh.vertices, which also re-checks the indices and the bounds.
using UnityEngine;
using UnityEngine.Rendering;

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
    int dirtyLo = int.MaxValue, dirtyHi = -1; bool dirtyAll; // quads written since the last upload (ring indices)

    // for the frame-time overlay / soak test (FrameLog.cs, SoakTest.cs): quads in use, bytes uploaded so far
    public static int QuadsUsed; public static long UploadBytes; public static int Quads => MaxQuads;
    int used;

    const MeshUpdateFlags Quiet = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;

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
        mesh.MarkDynamic(); // rewritten piece by piece while racing
        var uv = new Vector2[MaxQuads * 4];
        var tri = new ushort[MaxQuads * 6];
        for (int q = 0; q < MaxQuads; q++)
        {
            int b = q * 4;
            uv[b] = new Vector2(0, 0); uv[b + 1] = new Vector2(1, 0); uv[b + 2] = new Vector2(0, 1); uv[b + 3] = new Vector2(1, 1);
            int i = q * 6;
            tri[i] = (ushort)b; tri[i + 1] = (ushort)(b + 2); tri[i + 2] = (ushort)(b + 1);
            tri[i + 3] = (ushort)(b + 1); tri[i + 4] = (ushort)(b + 2); tri[i + 5] = (ushort)(b + 3);
        }
        mesh.SetVertexBufferParams(MaxQuads * 4,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 1));
        mesh.SetVertexBufferData(verts, 0, 0, verts.Length, 0, Quiet);
        mesh.SetVertexBufferData(uv, 0, 0, uv.Length, 1, Quiet);
        mesh.SetIndexBufferParams(tri.Length, IndexFormat.UInt16);
        mesh.SetIndexBufferData(tri, 0, 0, tri.Length, Quiet);
        mesh.subMeshCount = 1;
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, tri.Length), Quiet);
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
        next = 0; used = 0; QuadsUsed = 0; dirtyAll = true;
    }

    // wheel: 0..3; pos: contact point on the ground; side: the car's right vector; on: is this tyre sliding now
    public void Track(int wheel, Vector3 pos, Vector3 side, bool on)
    {
        if (!on) { active[wheel] = false; return; }
        pos.y = Height + WorldBuilder.HAlong(pos.x, pos.z, side.z, -side.x); // (the car's own leg at a bridge: along its heading)
        side.y = 0; side = side.normalized * HalfWidth;
        Vector3 l = pos - side, r = pos + side;
        if (!active[wheel] || (pos - lastP[wheel]).sqrMagnitude > 25f)
        {
            active[wheel] = true; lastL[wheel] = l; lastR[wheel] = r; lastP[wheel] = pos;
            return;
        }
        if ((pos - lastP[wheel]).sqrMagnitude < MinStep * MinStep) return;
        int q = next, b = q * 4;
        verts[b] = lastL[wheel]; verts[b + 1] = lastR[wheel]; verts[b + 2] = l; verts[b + 3] = r;
        next = (next + 1) % MaxQuads;
        if (used < MaxQuads) { used++; QuadsUsed = used; }
        lastL[wheel] = l; lastR[wheel] = r; lastP[wheel] = pos;
        // the dirty range: one span of ring indices (a wrap past the end in one frame: everything, rare)
        if (dirtyHi < 0) { dirtyLo = q; dirtyHi = q + 1; }
        else if (q == dirtyHi) dirtyHi = q + 1;
        else if (q < dirtyLo || q > dirtyHi) dirtyAll = true;
    }

    // once a frame: only the quads written since the last call go to the GPU
    public void Upload()
    {
        if (dirtyAll) { dirtyAll = false; dirtyLo = 0; dirtyHi = MaxQuads; }
        if (dirtyHi < 0) return;
        int v0 = dirtyLo * 4, n = (dirtyHi - dirtyLo) * 4;
        mesh.SetVertexBufferData(verts, v0, v0, n, 0, Quiet);
        UploadBytes += n * 12L;
        dirtyLo = int.MaxValue; dirtyHi = -1;
    }
}
