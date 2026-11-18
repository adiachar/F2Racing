// RoadSurface.cs — keeps Road.shader in step with the graphics level, without touching GameManager.
// GameManager.ApplyGraphics turns anisotropic filtering off only on Low, so that is read here (a property read per
// frame, no allocation): Low -> the cheap SubShader (LOD 150, one texture read, no grain, no sheen), Medium/High -> the
// full one.
// It hands Road.shader its textures as globals, set before the road is first drawn (both made once and cached):
//   _F2RoadMacro  (EnvTex.RoadMacro, both levels)  long, soft tonal drifts, mottle and resurfaced patches
//   _F2RoadDetail (EnvTex.RoadDetail, HQ only)     fine grain, faded to flat grey down its own mip chain
// and, every frame, _F2RoadMotion: 0 below ~50 km/h .. 1 above ~200 km/h of camera speed. The shader holds the fine
// grain down with it, as motion blur would: at 300 km/h and ~35 fps the road moves ~2.5 m a frame, so any grain left
// at full contrast strobes and crawls under the car. Smoothed, and camera cuts (teleports) are ignored.
// It also clears the sharp shadow mask (RoadShadow.cs builds it after the light bake; both levels read it).
using UnityEngine;

public class RoadSurface : MonoBehaviour
{
    Shader shader; int level = -1;
    static readonly int MacroId = Shader.PropertyToID("_F2RoadMacro");
    static readonly int DetailId = Shader.PropertyToID("_F2RoadDetail");
    static readonly int MotionId = Shader.PropertyToID("_F2RoadMotion");
    Camera cam; Vector3 lastPos; bool havePos; float motion = -1f;

    public static void Attach(GameObject root, Shader roadShader)
    {
        Shader.SetGlobalTexture(MacroId, EnvTex.RoadMacro());
        Shader.SetGlobalTexture(DetailId, EnvTex.RoadDetail());
        Shader.SetGlobalFloat(MotionId, 0f);
        RoadShadow.Reset(); // no shadow mask until RoadShadow.Build (after the bake) makes one
        var rs = root.AddComponent<RoadSurface>();
        rs.shader = roadShader;
        rs.Apply();
    }

    void Update() { Apply(); Motion(); }

    void Apply()
    {
        if (shader == null) return;
        int lvl = QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable ? 0 : 1;
        if (lvl == level) return;
        level = lvl;
        shader.maximumLOD = lvl == 0 ? 150 : 300;
    }

    void Motion()
    {
        if (cam == null || !cam.isActiveAndEnabled) { cam = Camera.main; havePos = false; if (cam == null) return; }
        float dt = Time.unscaledDeltaTime;
        Vector3 p = cam.transform.position;
        float target = motion < 0f ? 0f : motion;
        if (havePos && dt > 1e-4f && Time.timeScale > 0f)
        {
            float v = (p - lastPos).magnitude / dt;            // m/s
            if (v < 150f) target = Mathf.Clamp01((v - 14f) / 42f); // 50 -> 200 km/h; faster = a camera cut, ignored
        }
        lastPos = p; havePos = true;
        // ~0.3 s smoothing: frame-time jitter and gear-change lurches do not pump the grain
        float m = motion < 0f ? target : Mathf.Lerp(motion, target, 1f - Mathf.Exp(-dt * 3.5f));
        if (Mathf.Abs(m - motion) > 0.002f) { motion = m; Shader.SetGlobalFloat(MotionId, m); }
    }
}
