// TracksideLod.cs — hides the optional trackside layers (alpha-tested fences, ground decals) when the game is at the
// Low graphics level, including when Auto steps down to Low during a race. Like RoadSurface.cs it reads the
// anisotropic setting GameManager.ApplyGraphics switches off only on Low: one property read per frame, no allocation.
using UnityEngine;

public class TracksideLod : MonoBehaviour
{
    Renderer[] renderers; int state = -1;

    public static void Attach(GameObject root, Renderer[] list)
    {
        var c = root.AddComponent<TracksideLod>();
        c.renderers = list;
        c.Apply();
    }

    void Update() { Apply(); }

    void Apply()
    {
        if (renderers == null) return;
        int s = QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable ? 0 : 1;
        if (s == state) return;
        state = s;
        for (int k = 0; k < renderers.Length; k++) if (renderers[k] != null) renderers[k].enabled = s == 1;
    }
}
