// TracksideLod.cs — hides the optional trackside layers (alpha-tested fences, ground decals) when the game is at the
// Low graphics level, including when Auto steps down to Low during a race. Like RoadSurface.cs it reads the
// anisotropic setting GameManager.ApplyGraphics switches off only on Low: one property read per frame, no allocation.
// It also hides the thin-post layer (Armco and fence posts, in ~40 m chunks) beyond ~85 m: a 10-14 cm post is about
// one pixel wide there and only flickers in and out as the car moves. The distance grows when the camera zooms in
// (TV views). A few chunks are checked per frame (squared distance to their bounds), so it costs next to nothing.
using UnityEngine;

public class TracksideLod : MonoBehaviour
{
    Renderer[] renderers; int state = -1;
    Renderer[] far; Bounds[] farB; int farNext;
    const float FarDist = 85f; // metres at a 60 degree vertical field of view

    public static void Attach(GameObject root, Renderer[] list, Renderer[] thin = null)
    {
        var c = root.AddComponent<TracksideLod>();
        c.renderers = list;
        if (thin != null && thin.Length > 0)
        {
            c.far = thin; c.farB = new Bounds[thin.Length];
            for (int k = 0; k < thin.Length; k++) if (thin[k] != null) c.farB[k] = thin[k].bounds;
        }
        c.Apply();
    }

    void Update() { Apply(); Far(); Msaa(); }

    // BakedCutout's distance veil for the fences needs MSAA (alpha to coverage); tell the shader when it is on
    static readonly int MsaaId = Shader.PropertyToID("_F2Msaa"); int msaa = -1;
    void Msaa()
    {
        int m = QualitySettings.antiAliasing > 1 ? 1 : 0;
        if (m == msaa) return;
        msaa = m; Shader.SetGlobalFloat(MsaaId, m);
    }

    void Apply()
    {
        if (renderers == null) return;
        int s = QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable ? 0 : 1;
        if (s == state) return;
        state = s;
        for (int k = 0; k < renderers.Length; k++) if (renderers[k] != null) renderers[k].enabled = s == 1;
    }

    void Far()
    {
        if (far == null) return;
        var cam = Camera.main; if (cam == null) return;
        Vector3 p = cam.transform.position;
        float lim = FarDist * 60f / Mathf.Max(5f, cam.fieldOfView), lim2 = lim * lim;
        int n = Mathf.Min(far.Length, 32);
        for (int k = 0; k < n; k++)
        {
            int i = farNext; farNext = (farNext + 1) % far.Length;
            var r = far[i]; if (r == null) continue;
            bool on = farB[i].SqrDistance(p) < lim2;
            if (r.enabled != on) r.enabled = on;
        }
    }
}
