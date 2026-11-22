// MapEdits.cs: Adi's hand edits to a circuit's surroundings, made in the Unity editor with F2Racing > Map Editor
// (Assets/Editor/F2MapEditor.cs) and saved to Resources/MapEdits/<circuit id>.json. Applied on every build:
//   remove areas: boxes (centre x, z; width along local x, depth along local z; yaw in degrees, as the editor marker's
//                 Y rotation) in which no building (by its footprint centre), real tree, model tree, shrub, bush,
//                 undergrowth card or street prop is placed;
//   trees:        extra real trees (x, z, height), drawn like the map's own trees (RealWorld, the TREE list);
//   buildings:    extra buildings: a w x d rectangle turned by yaw, h tall, OSM kind / roof shape / colour, drawn with
//                 the same walls, roofs and lighting as the map's buildings (RealWorld, the BLDG list).
// No file = no change. Cost: a list check per placed object at build time; nothing per frame.
using System;
using System.Collections.Generic;
using UnityEngine;

public static class MapEdits
{
    [Serializable] public class Box { public float x, z, w = 20f, d = 20f, yaw; }
    [Serializable] public class Tree { public float x, z, h = 12f; }
    [Serializable]
    public class Bld
    {
        public float x, z, w = 12f, d = 10f, h = 9f, yaw;
        public int kind = 1, roof; // kind / roof as WorldData's BLDG section (1 residential; 0 flat roof)
        public Color colour = new Color(0f, 0f, 0f, 0f); // alpha 0 = the town's usual wall colours
    }
    [Serializable]
    public class File
    {
        public int version = 1;
        public string track = "";
        public List<Box> remove = new List<Box>();
        public List<Tree> trees = new List<Tree>();
        public List<Bld> buildings = new List<Bld>();
    }

    public static File Cur;              // the edits of the circuit being built (null = none)
    public static string TrackId { get; private set; } // the circuit last built (the editor saves under this name)
    public static bool Disabled;         // A/B: build without the edits

    public static void Load(string id)
    {
        Cur = null; TrackId = id;
        if (Disabled || string.IsNullOrEmpty(id)) return;
        var ta = Resources.Load<TextAsset>("MapEdits/" + id);
        if (ta == null) return;
        try { Cur = JsonUtility.FromJson<File>(ta.text); }
        catch (Exception e) { Debug.LogWarning("MapEdits/" + id + ": " + e.Message); Cur = null; }
        Resources.UnloadAsset(ta);
        if (Cur == null) return;
        if (Cur.remove == null) Cur.remove = new List<Box>();
        if (Cur.trees == null) Cur.trees = new List<Tree>();
        if (Cur.buildings == null) Cur.buildings = new List<Bld>();
        Debug.Log("MapEdits: " + id + ": " + Cur.remove.Count + " remove areas, " + Cur.trees.Count + " trees, " + Cur.buildings.Count + " buildings");
    }

    // inside any remove box (x, z in world metres)
    public static bool Removed(float x, float z)
    {
        if (Cur == null) return false;
        var rs = Cur.remove;
        for (int i = 0; i < rs.Count; i++)
        {
            var b = rs[i];
            float dx = x - b.x, dz = z - b.z, a = b.yaw * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            // world = Ry(yaw) * local (Unity: x' = x cos + z sin, z' = -x sin + z cos), so local = Ry(-yaw) * world
            float lx = dx * c - dz * s, lz = dx * s + dz * c;
            if (Mathf.Abs(lx) <= b.w * 0.5f && Mathf.Abs(lz) <= b.d * 0.5f) return true;
        }
        return false;
    }
    public static bool Removed(Vector3 p) => Removed(p.x, p.z);

    // the map data, right after it is read (RealWorld.Load): drop what lies in the remove areas, add the new trees and
    // buildings
    public static void Apply(WorldData w)
    {
        if (Cur == null || w == null) return;
        int dropB = 0, dropT = 0;
        // ---- buildings
        if (Cur.remove.Count > 0 || Cur.buildings.Count > 0)
        {
            var H = new List<float>(); var MinH = new List<float>(); var Roof = new List<byte>(); var Kind = new List<byte>();
            var Rgba = new List<uint>(); var Off = new List<int>(); var Cnt = new List<int>(); var X = new List<float>(); var Z = new List<float>();
            for (int b = 0; b < w.bN && w.bOff != null; b++)
            {
                int o = w.bOff[b], n = w.bCnt[b];
                float cx = 0f, cz = 0f;
                for (int k = 0; k < n; k++) { cx += w.bX[o + k]; cz += w.bZ[o + k]; }
                if (n > 0 && Removed(cx / n, cz / n)) { dropB++; continue; }
                H.Add(w.bH[b]); MinH.Add(w.bMinH[b]); Roof.Add(w.bRoof[b]); Kind.Add(w.bKind[b]); Rgba.Add(w.bRgba[b]);
                Off.Add(X.Count); Cnt.Add(n);
                for (int k = 0; k < n; k++) { X.Add(w.bX[o + k]); Z.Add(w.bZ[o + k]); }
            }
            for (int i = 0; i < Cur.buildings.Count; i++)
            {
                var nb = Cur.buildings[i];
                float hw = Mathf.Max(1f, nb.w) * 0.5f, hd = Mathf.Max(1f, nb.d) * 0.5f, a = nb.yaw * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
                H.Add(Mathf.Max(2.5f, nb.h)); MinH.Add(0f); Roof.Add((byte)Mathf.Clamp(nb.roof, 0, 5)); Kind.Add((byte)Mathf.Clamp(nb.kind, 0, 8));
                Color32 c32 = nb.colour;
                Rgba.Add(nb.colour.a > 0.01f ? (uint)c32.r | (uint)c32.g << 8 | (uint)c32.b << 16 | 255u << 24 : 0u);
                Off.Add(X.Count); Cnt.Add(4);
                // corners counter-clockwise seen from above (x right, z up): (-,-) (+,-) (+,+) (-,+), turned by yaw
                for (int k = 0; k < 4; k++)
                {
                    float lx = k == 0 || k == 3 ? -hw : hw, lz = k < 2 ? -hd : hd;
                    X.Add(nb.x + lx * c + lz * s); Z.Add(nb.z - lx * s + lz * c);
                }
            }
            w.bN = H.Count; w.bH = H.ToArray(); w.bMinH = MinH.ToArray(); w.bRoof = Roof.ToArray(); w.bKind = Kind.ToArray();
            w.bRgba = Rgba.ToArray(); w.bOff = Off.ToArray(); w.bCnt = Cnt.ToArray(); w.bX = X.ToArray(); w.bZ = Z.ToArray();
        }
        // ---- single trees
        if (Cur.remove.Count > 0 || Cur.trees.Count > 0)
        {
            var X = new List<float>(); var Z = new List<float>(); var Hh = new List<float>();
            for (int i = 0; i < w.tN && w.tX != null; i++)
            {
                if (Removed(w.tX[i], w.tZ[i])) { dropT++; continue; }
                X.Add(w.tX[i]); Z.Add(w.tZ[i]); Hh.Add(w.tH[i]);
            }
            for (int i = 0; i < Cur.trees.Count; i++) { var t = Cur.trees[i]; X.Add(t.x); Z.Add(t.z); Hh.Add(Mathf.Clamp(t.h, 2f, 40f)); }
            w.tN = X.Count; w.tX = X.ToArray(); w.tZ = Z.ToArray(); w.tH = Hh.ToArray();
        }
        Debug.Log("MapEdits: removed " + dropB + " buildings, " + dropT + " map trees; added " + Cur.buildings.Count + " buildings, " + Cur.trees.Count + " trees");
    }
}
