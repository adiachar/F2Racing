// F2MapEditor.cs: F2Racing > Map Editor, Adi's hand-editing tool for a circuit's surroundings (MapEdits.cs).
// Use (Play mode): press Play, start a race on a circuit, press Pause, open this window.
//   "Show my saved edits" puts back the markers saved for this circuit.
//   "Add ..." drops a marker at the middle of the Scene view, on the ground; move / turn / scale it with the usual tools
//   (W E R). Remove area = red box: nothing is built inside it. Tree = green post (scale Y = height / 2).
//   Building = grey box (scale = width, height, depth; type / roof / colour in the Inspector).
//   Model = one of the asset pack's 37 real models (Landmarks.cs), shown as it is.
//   "Save my edits" writes Assets/Resources/MapEdits/<circuit>.json (send that file to Claude, or keep it in the project).
//   Stop and Play again to see the circuit rebuilt with the edits.
// Markers are play-mode objects: they vanish on Stop, the saved file stays.
using System.IO;
using UnityEditor;
using UnityEngine;

public class F2MapEditor : EditorWindow
{
    const string RootName = "MapEdits (your changes)";
    Vector2 scroll;
    int modelPick;

    [MenuItem("F2Racing/Map Editor")]
    static void Open() { GetWindow<F2MapEditor>("F2 Map Editor"); }

    void OnInspectorUpdate() { Repaint(); }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("F2Racing map editor", EditorStyles.boldLabel);
        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("1. Press Play.\n2. Start a race on the circuit you want to edit.\n3. Press Pause, then come back here.", MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }
        string id = MapEdits.TrackId;
        if (string.IsNullOrEmpty(id))
        {
            EditorGUILayout.HelpBox("No circuit built yet: start a race first.", MessageType.Warning);
            EditorGUILayout.EndScrollView();
            return;
        }
        EditorGUILayout.LabelField("Circuit", id);
        var root = GameObject.Find(RootName);
        int n = root != null ? root.transform.childCount : 0;
        EditorGUILayout.LabelField("Markers in the scene", n.ToString());
        EditorGUILayout.Space();

        if (GUILayout.Button("Show my saved edits", GUILayout.Height(26))) ShowSaved(id);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Add at the middle of the Scene view", EditorStyles.boldLabel);
        if (GUILayout.Button("Add remove area (red box)", GUILayout.Height(26))) Select(Make(MapEditMarker.Kind.RemoveArea, Pivot(), 0f, new Vector3(20f, 30f, 20f)));
        if (GUILayout.Button("Add tree (green post)", GUILayout.Height(26))) Select(Make(MapEditMarker.Kind.Tree, Pivot(), 0f, new Vector3(1.5f, 6f, 1.5f)));
        if (GUILayout.Button("Add building (grey box)", GUILayout.Height(26))) Select(Make(MapEditMarker.Kind.Building, Pivot(), 0f, new Vector3(12f, 9f, 10f)));
        if (Landmarks.Load() && Landmarks.Names.Count > 0)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Real models (asset pack)", EditorStyles.boldLabel);
            modelPick = EditorGUILayout.Popup("Model", Mathf.Clamp(modelPick, 0, Landmarks.Names.Count - 1), Landmarks.Names.ToArray());
            if (GUILayout.Button("Add this model", GUILayout.Height(26))) Select(MakeModel(Landmarks.Names[modelPick], Pivot(), 0f, 1f, 0f));
        }
        EditorGUILayout.Space();
        GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
        if (GUILayout.Button("Save my edits", GUILayout.Height(32))) Save(id);
        GUI.backgroundColor = Color.white;
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Move = W, rotate = E, scale = R. Delete a marker with the Delete key.\n" +
            "Remove area: nothing (buildings, trees, bushes, lamps) is built inside the red box.\n" +
            "Tree: the post's height x 2 = the tree height.\n" +
            "Building: the box size = the building; pick its type, roof and colour in the Inspector.\n" +
            "Model: shown as it will look and saved exactly where it stands: move it so its base sits on the ground (W), turn it (E), scale it evenly (R, the centre cube).\n" +
            "After saving: Stop, then Play again to see the circuit with your edits.\n" +
            "File: Assets/Resources/MapEdits/" + id + ".json", MessageType.None);
        EditorGUILayout.EndScrollView();
    }

    static void Select(GameObject go) { Selection.activeGameObject = go; SceneView.FrameLastActiveSceneView(); }

    static Vector3 Pivot()
    {
        var sv = SceneView.lastActiveSceneView;
        Vector3 p = sv != null ? sv.pivot : Vector3.zero;
        p.y = Ground(p.x, p.z);
        return p;
    }
    static float Ground(float x, float z) => Environment.GroundY(x, z);

    static Transform Root()
    {
        var r = GameObject.Find(RootName);
        if (r == null) r = new GameObject(RootName);
        return r.transform;
    }

    static Material Mat(Color c)
    {
        var sh = Shader.Find("Legacy Shaders/Transparent/Diffuse");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        return new Material(sh) { color = c };
    }

    // ground: the foot of the marker (y = ground height), size as the edit
    static GameObject Make(MapEditMarker.Kind kind, Vector3 foot, float yaw, Vector3 size)
    {
        var prim = kind == MapEditMarker.Kind.Tree ? PrimitiveType.Cylinder : PrimitiveType.Cube;
        var go = GameObject.CreatePrimitive(prim);
        var col = go.GetComponent<Collider>(); if (col != null) Object.DestroyImmediate(col);
        go.name = kind == MapEditMarker.Kind.RemoveArea ? "Remove area" : kind == MapEditMarker.Kind.Tree ? "Tree" : "Building";
        go.transform.SetParent(Root(), true);
        go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        go.transform.localScale = size;
        // cube is 1 tall, cylinder 2 tall: lift so the bottom sits on the ground
        float half = kind == MapEditMarker.Kind.Tree ? size.y : size.y * 0.5f;
        go.transform.position = new Vector3(foot.x, foot.y + half, foot.z);
        var c = kind == MapEditMarker.Kind.RemoveArea ? new Color(1f, 0.15f, 0.1f, 0.35f) : kind == MapEditMarker.Kind.Tree ? new Color(0.2f, 0.8f, 0.25f, 0.85f) : new Color(0.75f, 0.75f, 0.78f, 0.7f);
        go.GetComponent<Renderer>().sharedMaterial = Mat(c);
        var m = go.AddComponent<MapEditMarker>(); m.kind = kind;
        Undo.RegisterCreatedObjectUndo(go, "Add " + go.name);
        return go;
    }

    static readonly System.Collections.Generic.Dictionary<string, Mesh> previewMesh = new System.Collections.Generic.Dictionary<string, Mesh>();
    static Material[] previewMats;
    static GameObject MakeModel(string name, Vector3 foot, float yaw, float scale, float lift)
    {
        var m = Landmarks.Get(name);
        if (m == null) return null;
        if (!previewMesh.TryGetValue(name, out var mesh) || mesh == null)
        {
            mesh = new Mesh { name = name, subMeshCount = m.subs.Length };
            var V = new System.Collections.Generic.List<Vector3>(); var N = new System.Collections.Generic.List<Vector3>(); var UV = new System.Collections.Generic.List<Vector2>();
            foreach (var sb in m.subs) { V.AddRange(sb.v); N.AddRange(sb.n); UV.AddRange(sb.uv); }
            mesh.SetVertices(V); mesh.SetNormals(N); mesh.SetUVs(0, UV);
            int b = 0;
            for (int i = 0; i < m.subs.Length; i++)
            {
                var t = (int[])m.subs[i].tri.Clone(); for (int j = 0; j < t.Length; j++) t[j] += b;
                mesh.SetTriangles(t, i); b += m.subs[i].v.Length;
            }
            mesh.RecalculateBounds();
            previewMesh[name] = mesh;
        }
        if (previewMats == null || previewMats.Length != Landmarks.MaterialCount)
        {
            previewMats = new Material[Landmarks.MaterialCount];
            var sh = Shader.Find("Standard");
            for (int i = 0; i < previewMats.Length; i++) { previewMats[i] = new Material(sh) { mainTexture = Landmarks.Texture(i) }; previewMats[i].SetFloat("_Glossiness", 0.1f); }
        }
        var go = new GameObject("Model " + name);
        go.transform.SetParent(Root(), true);
        go.transform.SetPositionAndRotation(new Vector3(foot.x, foot.y + lift, foot.z), Quaternion.Euler(0f, yaw, 0f));
        go.transform.localScale = Vector3.one * scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mats = new Material[m.subs.Length]; for (int i = 0; i < mats.Length; i++) mats[i] = previewMats[Mathf.Clamp(m.subs[i].mat, 0, previewMats.Length - 1)];
        go.AddComponent<MeshRenderer>().sharedMaterials = mats;
        var mk = go.AddComponent<MapEditMarker>(); mk.kind = MapEditMarker.Kind.Model; mk.modelName = name;
        Undo.RegisterCreatedObjectUndo(go, "Add model " + name);
        return go;
    }

    static void ShowSaved(string id)
    {
        var old = GameObject.Find(RootName); if (old != null) Object.DestroyImmediate(old);
        var f = MapEdits.Cur;
        if (f == null)
        {
            string path = FilePath(id);
            if (File.Exists(path)) f = JsonUtility.FromJson<MapEdits.File>(File.ReadAllText(path));
        }
        if (f == null) { EditorUtility.DisplayDialog("F2 Map Editor", "No saved edits for " + id + " yet.", "OK"); return; }
        if (f.remove != null) foreach (var b in f.remove) Make(MapEditMarker.Kind.RemoveArea, new Vector3(b.x, Ground(b.x, b.z), b.z), b.yaw, new Vector3(b.w, 30f, b.d));
        if (f.trees != null) foreach (var t in f.trees) Make(MapEditMarker.Kind.Tree, new Vector3(t.x, Ground(t.x, t.z), t.z), 0f, new Vector3(1.5f, t.h * 0.5f, 1.5f));
        if (f.models != null) foreach (var md in f.models) MakeModel(md.name, new Vector3(md.x, md.y, md.z), md.yaw, md.scale, 0f);
        if (f.buildings != null)
            foreach (var b in f.buildings)
            {
                var go = Make(MapEditMarker.Kind.Building, new Vector3(b.x, Ground(b.x, b.z), b.z), b.yaw, new Vector3(b.w, b.h, b.d));
                var m = go.GetComponent<MapEditMarker>();
                m.buildingType = (MapEditMarker.BuildingType)Mathf.Clamp(b.kind, 0, 8); m.roof = (MapEditMarker.Roof)Mathf.Clamp(b.roof, 0, 4); m.wallColour = b.colour;
            }
    }

    static string FilePath(string id) => "Assets/Resources/MapEdits/" + id + ".json";

    static void Save(string id)
    {
        var f = new MapEdits.File { track = id };
        var root = GameObject.Find(RootName);
        if (root != null)
            foreach (var m in root.GetComponentsInChildren<MapEditMarker>())
            {
                var tr = m.transform; var p = tr.position; var s = tr.lossyScale; float yaw = tr.eulerAngles.y;
                switch (m.kind)
                {
                    case MapEditMarker.Kind.RemoveArea: f.remove.Add(new MapEdits.Box { x = p.x, z = p.z, w = Mathf.Abs(s.x), d = Mathf.Abs(s.z), yaw = yaw }); break;
                    case MapEditMarker.Kind.Tree: f.trees.Add(new MapEdits.Tree { x = p.x, z = p.z, h = Mathf.Abs(s.y) * 2f }); break;
                    case MapEditMarker.Kind.Model: f.models.Add(new MapEdits.Mdl { name = m.modelName, x = p.x, z = p.z, y = p.y, yaw = yaw, scale = Mathf.Abs(s.x) }); break;
                    default:
                        f.buildings.Add(new MapEdits.Bld { x = p.x, z = p.z, w = Mathf.Abs(s.x), h = Mathf.Abs(s.y), d = Mathf.Abs(s.z), yaw = yaw, kind = (int)m.buildingType, roof = (int)m.roof, colour = m.wallColour });
                        break;
                }
            }
        string path = FilePath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(f, true));
        AssetDatabase.ImportAsset(path);
        EditorUtility.DisplayDialog("F2 Map Editor", "Saved " + f.remove.Count + " remove areas, " + f.trees.Count + " trees, " + f.buildings.Count + " buildings, " + f.models.Count + " models to\n" + path + "\n\nStop and Play again to see them in the game. Send this file to Claude to put it in the game for everyone.", "OK");
        EditorUtility.RevealInFinder(path);
    }
}
