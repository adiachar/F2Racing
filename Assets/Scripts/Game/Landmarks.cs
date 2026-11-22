// Landmarks.cs: hand-placed models (Adi's asset pack: buildings, grandstands, pit modules, gantries, kiosks ... 37
// models, Tools/Models/pack_landmarks.py -> Resources/Landmarks/landmarks.bytes + Photo/lm_<material> textures), put on
// a circuit by the map editor (MapEdits.models). All placements of a circuit are merged into ONE world-space mesh per
// material (about 22 materials at most, usually far fewer), so they cost a few draw calls in all, and the light bake
// (LightBake.Apply) lights them like every other building: sun, sky, contact shade, night lamps.
// Load: the model file is parsed once per session (~1 MB); textures load only for the materials a circuit uses.
using System.Collections.Generic;
using UnityEngine;

public static class Landmarks
{
    public sealed class Sub { public int mat; public Vector3[] v; public Vector3[] n; public Vector2[] uv; public int[] tri; }
    public sealed class Model { public string name; public Vector3 min, size; public Sub[] subs; }

    static string[] matKeys;
    static readonly Dictionary<string, Model> models = new Dictionary<string, Model>();
    static bool loaded;
    public static readonly List<string> Names = new List<string>(); // for the map editor's list

    public static bool Load()
    {
        if (loaded) return models.Count > 0;
        loaded = true;
        var ta = Resources.Load<TextAsset>("Landmarks/landmarks");
        if (ta == null) return false;
        try { Parse(ta.bytes); }
        catch (System.Exception e) { Debug.LogWarning("Landmarks: " + e.Message); models.Clear(); Names.Clear(); }
        Resources.UnloadAsset(ta);
        return models.Count > 0;
    }

    public static Model Get(string name) { Load(); return name != null && models.TryGetValue(name, out var m) ? m : null; }

    static void Parse(byte[] d)
    {
        int p = 0;
        if (d.Length < 8 || d[0] != 'F' || d[1] != '2' || d[2] != 'L' || d[3] != 'M') throw new System.Exception("not a landmark file");
        p = 4;
        int ver = U16(d, ref p); if (ver != 1) throw new System.Exception("version " + ver);
        int nm = U16(d, ref p);
        matKeys = new string[nm];
        for (int i = 0; i < nm; i++) matKeys[i] = Str(d, ref p);
        int nModels = U16(d, ref p);
        for (int k = 0; k < nModels; k++)
        {
            var m = new Model { name = Str(d, ref p) };
            m.min = new Vector3(F(d, ref p), F(d, ref p), F(d, ref p));
            m.size = new Vector3(F(d, ref p), F(d, ref p), F(d, ref p));
            int ns = d[p++];
            m.subs = new Sub[ns];
            for (int s = 0; s < ns; s++)
            {
                var sb = new Sub { mat = d[p++] };
                int nv = I32(d, ref p), nt = I32(d, ref p);
                sb.v = new Vector3[nv]; sb.n = new Vector3[nv]; sb.uv = new Vector2[nv]; sb.tri = new int[nt * 3];
                for (int i = 0; i < nv; i++)
                {
                    sb.v[i] = new Vector3(F(d, ref p), F(d, ref p), F(d, ref p));
                    sb.n[i] = new Vector3((sbyte)d[p], (sbyte)d[p + 1], (sbyte)d[p + 2]) / 127f; p += 4;
                    sb.uv[i] = new Vector2(F(d, ref p), F(d, ref p));
                }
                for (int i = 0; i < nt * 3; i++) sb.tri[i] = U16(d, ref p);
                m.subs[s] = sb;
            }
            models[m.name] = m; Names.Add(m.name);
        }
    }
    static int U16(byte[] d, ref int p) { int v = d[p] | d[p + 1] << 8; p += 2; return v; }
    static int I32(byte[] d, ref int p) { int v = System.BitConverter.ToInt32(d, p); p += 4; return v; }
    static float F(byte[] d, ref int p) { float v = System.BitConverter.ToSingle(d, p); p += 4; return v; }
    static string Str(byte[] d, ref int p) { int n = d[p++]; var s = System.Text.Encoding.UTF8.GetString(d, p, n); p += n; return s; }

    public static Texture2D Texture(int mat) => matKeys != null && mat >= 0 && mat < matKeys.Length ? Resources.Load<Texture2D>("Photo/lm_" + matKeys[mat]) : null;
    public static int MaterialCount => matKeys != null ? matKeys.Length : 0;

    // every model of the circuit's edits, merged per material, under root (call before the light bake)
    public static void Build(Transform root)
    {
        var f = MapEdits.Cur;
        if (f == null || f.models == null || f.models.Count == 0 || !Load()) return;
        int nMat = matKeys.Length;
        var V = new List<Vector3>[nMat]; var N = new List<Vector3>[nMat]; var UV = new List<Vector2>[nMat]; var T = new List<int>[nMat];
        int placed = 0;
        for (int i = 0; i < f.models.Count; i++)
        {
            var e = f.models[i];
            var m = Get(e.name); if (m == null) { Debug.LogWarning("Landmarks: no model " + e.name); continue; }
            float y = e.y; // the base height as placed in the editor (what Adi saw there)
            var rot = Quaternion.Euler(0f, e.yaw, 0f); float sc = e.scale > 0.01f ? e.scale : 1f;
            var org = new Vector3(e.x, y, e.z);
            foreach (var sb in m.subs)
            {
                int k = sb.mat; if (k < 0 || k >= nMat) continue;
                if (V[k] == null) { V[k] = new List<Vector3>(); N[k] = new List<Vector3>(); UV[k] = new List<Vector2>(); T[k] = new List<int>(); }
                int b = V[k].Count;
                for (int j = 0; j < sb.v.Length; j++) { V[k].Add(org + rot * (sb.v[j] * sc)); N[k].Add(rot * sb.n[j]); UV[k].Add(sb.uv[j]); }
                for (int j = 0; j < sb.tri.Length; j++) T[k].Add(b + sb.tri[j]);
            }
            placed++;
        }
        if (placed == 0) return;
        var holder = new GameObject("Landmarks").transform; holder.SetParent(root, false);
        var baked = Resources.Load<Shader>("Baked");
        var sh = baked != null && baked.isSupported && WorldBuilder.BakeReady ? baked : Shader.Find("Legacy Shaders/Diffuse");
        for (int k = 0; k < nMat; k++)
        {
            if (V[k] == null) continue;
            var mesh = new Mesh { name = "Landmarks_" + matKeys[k] };
            if (V[k].Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(V[k]); mesh.SetNormals(N[k]); mesh.SetUVs(0, UV[k]); mesh.SetTriangles(T[k], 0); mesh.RecalculateBounds();
            var go = new GameObject("Landmark " + matKeys[k]); go.transform.SetParent(holder, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mat = new Material(sh) { mainTexture = Texture(k), color = Color.white };
            if (mat.mainTexture == null) mat.mainTexture = Texture2D.whiteTexture;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }
        Debug.Log("Landmarks: " + placed + " models placed");
    }
}
