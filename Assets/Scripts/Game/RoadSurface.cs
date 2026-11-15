// RoadSurface.cs — keeps Road.shader in step with the graphics level, without touching GameManager.
// GameManager.ApplyGraphics turns anisotropic filtering off only on Low, so that is read here (a property read per
// frame, no allocation): Low -> the cheap SubShader (LOD 150, one texture read, no sheen), Medium/High -> the full one.
// It also hands Road.shader its fine aggregate texture (EnvTex.RoadFine, 256 x 256, made once and cached) as the
// global _F2RoadFine, read by the Medium/High SubShader only. Set before the road is first drawn.
using UnityEngine;

public class RoadSurface : MonoBehaviour
{
    Shader shader; int level = -1;
    static readonly int FineId = Shader.PropertyToID("_F2RoadFine");

    public static void Attach(GameObject root, Shader roadShader)
    {
        Shader.SetGlobalTexture(FineId, EnvTex.RoadFine());
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
