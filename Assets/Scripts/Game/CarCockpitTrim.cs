// CarCockpitTrim.cs — another part of CarModel (see CarModel.cs). Drawing only: nothing here touches the physics.
//
// In the cockpit view the camera sits at the driver's eyes (body z = +0.30, a few cm either way with the seat setting)
// and looks forward. Everything of the car that lies wholly behind the plane z = 0 (the rear 40%: engine cover, gearbox
// and rear bodywork, diffuser, rear wing, rear light) is behind the camera plane too, and with a field of view under
// ~100 degrees plus the small head yaw it can never be in frame. Those triangles were still binned and shaded every
// frame (Mali bins every triangle of a draw): ~70k of the full car's ~190k drawn triangles.
// While the cockpit view is on (and not LOOK BACK, which turns the camera round), each big body part gets a copy of its
// mesh without the triangles that are entirely behind z = 0 (built once per source mesh, cached, shared); a part left
// with no triangle is switched off (forceRenderingOff). The wheels, steering wheel, halo and driver are never touched,
// nor are triangles that cross the plane. Back to the full meshes in every other view.
using System.Collections.Generic;
using UnityEngine;

public partial class CarModel
{
    const float TrimZ = 0f; // body frame (model units): the eye is at +0.30 (seat setting +-0.10), so 20+ cm of margin
    struct TrimItem { public MeshFilter mf; public Renderer r; public Mesh full, trimmed; }
    TrimItem[] trimItems; bool trimOn;
    static readonly Dictionary<Mesh, Mesh> trimCache = new Dictionary<Mesh, Mesh>(); // value null = the whole mesh is behind the plane

    // the camera is at the driver's eyes: within ~35 cm of (0, 0.76, 0.30) in the car's frame (seat setting, head movement)
    public static bool AtCockpitEye(Transform car, Vector3 camPos)
    {
        Vector3 p = car.InverseTransformPoint(camPos);
        return Mathf.Abs(p.x) < 0.35f && p.y > 0.4f && p.y < 1.1f && p.z > 0.05f && p.z < 0.65f;
    }

    // once a frame is fine: it returns at once unless the state changes
    public void CockpitTrim(bool on)
    {
        if (rival || Res != "Car/car") return;
        if (on == trimOn) return;
        if (trimItems == null)
        {
            if (!on) return;
            var list = new List<TrimItem>();
            foreach (var mf in body.GetComponentsInChildren<MeshFilter>(true))
            {
                string n = mf.name;
                if (n.StartsWith("steer_") || n.StartsWith("halo_") || n.StartsWith("wheel_") || n == "CockpitTub" || n == "ContactShadow" || n == "Helmet") continue;
                var m = mf.sharedMesh; var r = mf.GetComponent<Renderer>();
                if (m == null || r == null || !m.isReadable || m.subMeshCount != 1 || m.vertexCount < 300) continue;
                list.Add(new TrimItem { mf = mf, r = r, full = m });
            }
            trimItems = list.ToArray();
            for (int i = 0; i < trimItems.Length; i++) trimItems[i].trimmed = Trimmed(trimItems[i].full);
        }
        trimOn = on;
        for (int i = 0; i < trimItems.Length; i++)
        {
            var it = trimItems[i]; if (it.mf == null) continue;
            if (on)
            {
                if (it.trimmed == null) it.r.forceRenderingOff = true;
                else it.mf.sharedMesh = it.trimmed;
            }
            else { it.r.forceRenderingOff = false; it.mf.sharedMesh = it.full; }
        }
    }

    // `m` without the triangles whose three vertices are all behind the plane; the same mesh when none is
    static Mesh Trimmed(Mesh m)
    {
        if (trimCache.TryGetValue(m, out var t)) return t;
        var v = m.vertices; var n = m.normals; var col = m.colors32; var tri = m.triangles;
        var keep = new List<int>(tri.Length / 3);
        for (int k = 0; k < tri.Length; k += 3)
            if (v[tri[k]].z >= TrimZ || v[tri[k + 1]].z >= TrimZ || v[tri[k + 2]].z >= TrimZ) keep.Add(k);
        if (keep.Count == tri.Length / 3) t = m;
        else if (keep.Count == 0) t = null;
        else t = SubMesh(v, n, col, tri, keep);
        trimCache[m] = t;
        return t;
    }
}
