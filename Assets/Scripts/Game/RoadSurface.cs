// RoadSurface.cs — keeps Road.shader in step with the graphics level, without touching GameManager.
// GameManager.ApplyGraphics turns anisotropic filtering off only on Low, so that is read here (a property read per
// frame, no allocation): Low -> the cheap SubShader (LOD 150, one texture read, no sheen), Medium/High -> the full one.
using UnityEngine;

public class RoadSurface : MonoBehaviour
{
    Shader shader; int level = -1;

    public static void Attach(GameObject root, Shader roadShader)
    {
        var rs = root.AddComponent<RoadSurface>();
        rs.shader = roadShader;
        rs.Apply();
    }

    void Update() { Apply(); }

    void Apply()
    {
        if (shader == null) return;
        int lvl = QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable ? 0 : 1;
        if (lvl == level) return;
        level = lvl;
        shader.maximumLOD = lvl == 0 ? 150 : 300;
    }
}
