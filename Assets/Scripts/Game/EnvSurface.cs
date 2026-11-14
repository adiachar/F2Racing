// EnvSurface.cs — keeps the scenery shaders (Ground.shader, Foliage.shader) in step with the graphics level, like
// RoadSurface does for the road: GameManager turns anisotropic filtering off only on Low, so that is read here (a
// property read per frame, no allocation). Low -> LOD 150 (positive mip bias, no tree sway), Medium/High -> LOD 300.
using UnityEngine;

public class EnvSurface : MonoBehaviour
{
    Shader[] shaders; int level = -1;

    public static void Attach(GameObject root, params Shader[] list)
    {
        var es = root.AddComponent<EnvSurface>();
        es.shaders = list;
        es.Apply();
    }

    void Update() { Apply(); }

    void Apply()
    {
        if (shaders == null) return;
        int lvl = QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable ? 0 : 1;
        if (lvl == level) return;
        level = lvl;
        for (int i = 0; i < shaders.Length; i++) if (shaders[i] != null) shaders[i].maximumLOD = lvl == 0 ? 150 : 300;
    }
}
