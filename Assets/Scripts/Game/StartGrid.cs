// StartGrid.cs — the starting grid: where each slot is, and the same numbers for the painted grid boxes.
// Real F1 layout: one slot every 8 m, staggered left and right of the centre (about 2.5 m off), pole on the side
// of the racing line into turn 1, pole's bar just behind the start line. Slot k's bar is Pitch * k metres behind
// the pole bar; a car sits with its centre CarBehindBar metres behind its bar.
// Paint(): pushes the same numbers into Road.shader's _Grid, so the painted boxes match the cars. (WorldBuilder sets
// the old numbers at build time; Paint() overrides them on the built road material.)
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public static class StartGrid
{
    public const double Pitch = 8, Lateral = 2.5, CarBehindBar = 2.5;
    public const int Slots = 22;

    // m from the start line back to the pole slot's bar. Uses CircuitDef.gridOffset once it exists: then replace the
    // body with "t.def != null ? t.def.gridOffset : 0" and delete FallbackOffset. Until then a constant per circuit.
    public static double Offset(Track t) => t == null || t.def == null ? 0 : FallbackOffset(t.def.id);
    static readonly Dictionary<string, double> Fallback = new Dictionary<string, double>(); // circuit id -> m (none yet: 0)
    static double FallbackOffset(string id) => id != null && Fallback.TryGetValue(id, out var v) ? v : 0;

    // +1: pole on the left of the direction of travel (track normal side), -1 right. The racing line into turn 1
    // runs on the outside of that corner, so pole goes there: a left-hander (curv > 0) puts pole on the right.
    static Track sideFor; static int sideVal = 1;
    public static int PoleSide(Track t)
    {
        if (t == null) return 1;
        if (t == sideFor) return sideVal;
        int side = 1;
        int n = Mathf.Min(t.N, (int)(1500 / t.ds));
        for (int i = 0; i < n; i++)
        {
            double c = t.pts[i].curv;
            if (System.Math.Abs(c) > 1.0 / 300) { side = c > 0 ? -1 : 1; break; }
        }
        sideFor = t; sideVal = side;
        return side;
    }

    // distance along the lap of slot k's car centre (k = 0 pole), and its sideways offset (+ = track normal side)
    public static double SlotS(Track t, int k) => t.length - Offset(t) - k * Pitch - CarBehindBar;
    public static double SlotLat(Track t, int k) => (k % 2 == 0 ? 1 : -1) * PoleSide(t) * Lateral;

    // pose of slot k: position, heading, and the nearest track point
    public static void Pose(Track t, int k, out double x, out double y, out double heading, out int idx)
    {
        double s = SlotS(t, k), f = s / t.ds;
        int i0 = (int)System.Math.Floor(f); double a = f - i0;
        i0 = ((i0 % t.N) + t.N) % t.N; int i1 = (i0 + 1) % t.N;
        var p = t.pts[i0]; var q = t.pts[i1];
        double lat = SlotLat(t, k);
        double cx = p.x + (q.x - p.x) * a, cy = p.y + (q.y - p.y) * a;
        double nx = p.nx + (q.nx - p.nx) * a, ny = p.ny + (q.ny - p.ny) * a, tx = p.tx + (q.tx - p.tx) * a, ty = p.ty + (q.ty - p.ty) * a;
        x = cx + nx * lat; y = cy + ny * lat; heading = System.Math.Atan2(ty, tx);
        idx = a < 0.5 ? i0 : i1;
    }

    // Road.shader's _Grid: first bar (m along the lap), pitch (m), slots, slot offset (m; the sign puts pole left or right)
    public static Vector4 ShaderGrid(Track t) => new Vector4((float)(t.length - Offset(t)), (float)Pitch, Slots, (float)(PoleSide(t) * Lateral));

    // set the painted grid on the built road (the "Road" object WorldBuilder makes with Road.shader)
    public static void Paint(GameObject world, Track t)
    {
        if (world == null || t == null) return;
        var g = ShaderGrid(t);
        foreach (var r in world.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r.gameObject.name != "Road") continue;
            var m = r.sharedMaterial;
            if (m != null && m.HasProperty("_Grid")) m.SetVector("_Grid", g);
        }
    }
}
