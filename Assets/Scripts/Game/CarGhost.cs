// CarGhost.cs — the "ghost" look of a car serving a live penalty (PenaltyServe.cs).
//   CarGhost.Prepare(car)   at load (session start, after the car, driver and steering wheel are built): makes the
//                           ghost twin materials and compiles their shader variants off-screen (no mid-race compile)
//   CarGhost.Set(car, g)    every frame while g may be > 0 (g 0..1; cheap, allocates nothing)
// The car shaders (CarPaint, CarWheelFace, SteeringWheel, ShiftLed: CarGhost.cginc) have the ghost code only in the
// F2_GHOST variant: a stable 4x4 screen-space dither drops ~0.55 g of the pixels in the opaque pass, the rest gets a
// cool tint and a rim lift. The normal variant has no clip(), so Mali keeps early-Z / hidden-surface removal for
// the cars at all other times. While g > 0 the car's renderers draw with "twins" of their materials (a copy with
// F2_GHOST on, one per source material, made once; its values are copied from the source every Set, so brake glow,
// tyre wear, wheel blur and the steering-wheel LEDs stay live); at g = 0 the original materials go back.
//  * _Ghost itself goes per renderer through ONE shared MaterialPropertyBlock (read back first: other values stay)
//  * merged rivals (instanced material): their twin is a twin of the shared rival material, and g goes into the car's
//    own block as the instanced _GhostV (CarModel.SetGhost), so instanced draws stay together
//  * the contact blob (CarBlob.shader, transparent) just pales with _Ghost: no twin needed
// Call Forget(car) if renderers are added to a car after Prepare (a rebuilt model is a new GameObject anyway).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public static class CarGhost
{
    sealed class Slot { public Renderer r; public bool inst; public Material[] orig = new Material[1], ghost = new Material[1]; public int n; }
    sealed class Pair { public Material src, twin; public LocalKeyword kw; }
    sealed class Entry
    {
        public GameObject car; public Slot[] slots; public CarModel cm; public float g = -1f; public bool swapped;
        public readonly List<Pair> live = new List<Pair>(); // the twins in use while swapped (synced every Set)
    }

    static readonly int GhostId = Shader.PropertyToID("_Ghost");
    static readonly Dictionary<int, Entry> cache = new Dictionary<int, Entry>();
    static readonly Dictionary<Material, Pair> twins = new Dictionary<Material, Pair>();
    static readonly List<Renderer> tmpR = new List<Renderer>();
    static readonly List<Material> tmpM = new List<Material>();
    static readonly List<int> dead = new List<int>();
    static MaterialPropertyBlock mpb;
    static Entry last;

    // at load: twins + one off-screen draw of the car with them (compiles the F2_GHOST variants now)
    public static void Prepare(GameObject car)
    {
        if (car == null) return;
        var e = Find(car, true);
        if (e == null) return;
        Warm(e);
    }

    // g: 0 = normal .. 1 = full ghost (about half the pixels gone)
    public static void Set(GameObject car, float g)
    {
        if (car == null) return;
        g = Mathf.Clamp01(g);
        var e = Find(car, false);
        if (e == null)
        {
            if (g <= 0f) return; // never ghosted: nothing to clear
            e = Find(car, true); // (not prepared: twins made now, the variant may compile on first draw)
            if (e == null) return;
        }
        if (g > 0f && !e.swapped) SwapIn(e);
        else if (g <= 0f && e.swapped) SwapOut(e);
        if (e.swapped) Sync(e);
        if (Mathf.Abs(g - e.g) < 0.002f && (g > 0f || e.g == 0f)) return;
        if (e.cm != null && e.cm.IsMerged) e.cm.SetGhost(g);
        WriteMpb(e, g);
        e.g = g;
    }

    // drop the cached renderers of this car (they are looked up again at the next Prepare / Set)
    public static void Forget(GameObject car)
    {
        if (car == null) return;
        int id = car.GetInstanceID();
        if (cache.TryGetValue(id, out var e) && e.swapped) SwapOut(e);
        if (last != null && last.car == car) last = null;
        cache.Remove(id);
    }

    static void WriteMpb(Entry e, float g)
    {
        if (mpb == null) mpb = new MaterialPropertyBlock();
        var ss = e.slots;
        for (int i = 0; i < ss.Length; i++)
        {
            var s = ss[i]; if (s.inst || s.r == null) continue; // (instanced rival parts: CarModel.SetGhost)
            s.r.GetPropertyBlock(mpb);
            mpb.SetFloat(GhostId, g);
            s.r.SetPropertyBlock(mpb);
        }
    }

    static void SwapIn(Entry e)
    {
        e.live.Clear();
        var ss = e.slots;
        for (int i = 0; i < ss.Length; i++)
        {
            var s = ss[i]; if (s.r == null) { s.n = 0; continue; }
            s.r.GetSharedMaterials(tmpM); // (the current ones: whatever was set since Prepare goes back at the end)
            int n = tmpM.Count;
            if (s.orig.Length != n) { s.orig = new Material[n]; s.ghost = new Material[n]; } // (only if the count changed)
            bool any = false;
            for (int k = 0; k < n; k++)
            {
                var m = tmpM[k]; s.orig[k] = m;
                var p = Twin(m);
                s.ghost[k] = p != null ? p.twin : m;
                if (p != null) { any = true; if (!e.live.Contains(p)) e.live.Add(p); }
            }
            s.n = any ? n : 0;
            if (!any) continue;
            if (n == 1) s.r.sharedMaterial = s.ghost[0]; else s.r.sharedMaterials = s.ghost;
        }
        tmpM.Clear();
        e.swapped = true;
        Sync(e);
    }

    static void SwapOut(Entry e)
    {
        var ss = e.slots;
        for (int i = 0; i < ss.Length; i++)
        {
            var s = ss[i]; if (s.r == null || s.n == 0) continue;
            if (s.n == 1) s.r.sharedMaterial = s.orig[0]; else s.r.sharedMaterials = s.orig;
            for (int k = 0; k < s.orig.Length; k++) { s.orig[k] = null; s.ghost[k] = null; }
            s.n = 0;
        }
        e.live.Clear();
        e.swapped = false;
    }

    // the twins take the source materials' current values (glow, wear, blur, LEDs...); the copy brings the source's
    // keywords too, so F2_GHOST is switched back on (a cached LocalKeyword: no string work)
    static void Sync(Entry e)
    {
        var l = e.live;
        for (int i = 0; i < l.Count; i++)
        {
            var p = l[i]; if (p.src == null || p.twin == null) continue;
            p.twin.CopyPropertiesFromMaterial(p.src);
            p.twin.EnableKeyword(p.kw);
        }
    }

    static Pair Twin(Material src)
    {
        if (src == null) return null;
        if (twins.TryGetValue(src, out var p)) return p;
        p = null;
        var sh = src.shader;
        if (sh != null)
        {
            string sn = sh.name;
            if (sn == "F2/CarPaint" || sn == "F2/CarWheelFace" || sn == "F2/SteeringWheel" || sn == "F2/ShiftLed")
            {
                var kw = new LocalKeyword(sh, "F2_GHOST");
                if (kw.isValid)
                {
                    var t = new Material(src) { name = src.name + " (ghost)" };
                    t.EnableKeyword(kw);
                    p = new Pair { src = src, twin = t, kw = kw };
                }
            }
        }
        twins[src] = p; // (null: no ghost variant, the material stays as it is)
        return p;
    }

    static Entry Find(GameObject car, bool make)
    {
        if (last != null && last.car == car) return last;
        int id = car.GetInstanceID();
        if (cache.TryGetValue(id, out var e) && e.car == car) return last = e;
        if (!make) return null;
        // first use for this car: forget cars (and twins of materials) that were destroyed, then cache this one
        dead.Clear();
        foreach (var kv in cache) if (kv.Value.car == null) dead.Add(kv.Key);
        for (int i = 0; i < dead.Count; i++) cache.Remove(dead[i]);
        PurgeTwins();
        e = new Entry { car = car, cm = car.GetComponentInParent<CarModel>() };
        tmpR.Clear();
        car.GetComponentsInChildren(true, tmpR);
        var list = new List<Slot>(tmpR.Count);
        for (int i = 0; i < tmpR.Count; i++)
        {
            var r = tmpR[i];
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue; // no particles (sparks, smoke)
            var m = r.sharedMaterial;
            var s = new Slot { r = r, inst = m != null && m.enableInstancing && m.IsKeywordEnabled("_MERGED") };
            r.GetSharedMaterials(tmpM);
            for (int k = 0; k < tmpM.Count; k++) Twin(tmpM[k]); // twins made now, at load
            list.Add(s);
        }
        tmpR.Clear(); tmpM.Clear();
        e.slots = list.ToArray();
        cache[id] = e;
        return last = e;
    }

    static readonly List<Material> deadM = new List<Material>();
    static void PurgeTwins()
    {
        deadM.Clear();
        foreach (var kv in twins) if (kv.Key == null) deadM.Add(kv.Key);
        for (int i = 0; i < deadM.Count; i++)
        {
            var p = twins[deadM[i]];
            if (p != null && p.twin != null) Object.Destroy(p.twin);
            twins.Remove(deadM[i]);
        }
        deadM.Clear();
    }

    // one draw of the car with its twins into a small off-screen target, from behind and above (wide angle: every
    // part in view), so the F2_GHOST variants (at the current graphics level, fog mode and MSAA) compile at load
    static void Warm(Entry e)
    {
        bool was = e.swapped;
        if (!was) SwapIn(e);
        int mask = 0;
        for (int i = 0; i < e.slots.Length; i++) if (e.slots[i].r != null) mask |= 1 << e.slots[i].r.gameObject.layer;
        var t = e.car.transform;
        var go = new GameObject("GhostWarm");
        var cam = go.AddComponent<Camera>();
        cam.enabled = false;
        int aa = Mathf.Max(1, QualitySettings.antiAliasing);
        var rt = RenderTexture.GetTemporary(64, 64, 24, RenderTextureFormat.Default, RenderTextureReadWrite.Default, aa);
        try
        {
            go.transform.position = t.position + t.rotation * new Vector3(0f, 2.2f, -5.5f);
            go.transform.LookAt(t.position + t.up * 0.5f);
            cam.fieldOfView = 90f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 50f;
            cam.cullingMask = mask; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.allowHDR = false; cam.allowMSAA = aa > 1; cam.useOcclusionCulling = false;
            cam.targetTexture = rt;
            cam.Render();
        }
        finally
        {
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            Object.Destroy(go);
            if (!was) SwapOut(e);
        }
    }
}
