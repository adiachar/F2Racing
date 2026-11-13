// WeatherFx.cs — rain and tyre spray for Weather.cs, cheap enough for low-end phones.
//  * Rain: one particle system on the camera (local space, so it never runs out of drops at speed), stretched
//    billboards in a box ahead of and above the camera. The drops come at the camera with the car's speed, so at
//    speed they become long streaks fanning out from the vanishing point, and there are more of them. One draw call.
//    Wet only (damp = the rain has stopped). Low: fewer drops.
//  * Spray: one small particle system per car (30 particles at most), soft grey puffs thrown up behind the rear
//    wheels, by speed and wetness. Off on Low, and for cars more than SprayRange from the camera.
//    All spray systems share one material, so Unity can batch them.
// Both are generated here (textures and all); the shader is Resources/WeatherFx.shader, else the legacy
// alpha-blended particle shader if the build has it, else no effects at all (the wet road still shows).
using System.Collections.Generic;
using UnityEngine;

public class WeatherFx : MonoBehaviour
{
    const float SprayRange = 70f;           // m from the camera
    const int RainMax = 420, RainMaxLow = 160, SprayMax = 30;

    Camera cam; ParticleSystem rain; Material rainMat, sprayMat;
    readonly List<ParticleSystem> sprays = new List<ParticleSystem>();
    readonly List<Transform> sprayT = new List<Transform>();
    bool low; int used; float wet = -1f;

    public static WeatherFx Create(Camera cam)
    {
        var fx = cam.gameObject.AddComponent<WeatherFx>();
        fx.cam = cam;
        fx.Init();
        return fx;
    }

    void Init()
    {
        var sh = Resources.Load<Shader>("WeatherFx");
        if (sh == null || !sh.isSupported) sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
        if (sh == null || !sh.isSupported) return; // no effects
        rainMat = new Material(sh) { mainTexture = Soft(16, true), name = "Rain" };
        sprayMat = new Material(sh) { mainTexture = Soft(32, false), name = "Spray" };
        if (rainMat.HasProperty("_TintColor")) { rainMat.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f)); sprayMat.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f)); }

        var go = new GameObject("Rain"); go.transform.SetParent(cam.transform, false);
        rain = go.AddComponent<ParticleSystem>();
        rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var m = rain.main;
        m.loop = true; m.playOnAwake = false; m.simulationSpace = ParticleSystemSimulationSpace.Local;
        m.startLifetime = 0.55f; m.startSpeed = 0f; m.startSize = new ParticleSystem.MinMaxCurve(0.018f, 0.03f);
        m.startColor = new Color(0.82f, 0.85f, 0.9f, 0.32f); m.maxParticles = RainMax; m.gravityModifier = 0f;
        var shp = rain.shape; shp.enabled = true; shp.shapeType = ParticleSystemShapeType.Box;
        shp.position = new Vector3(0f, 2.2f, 11f); shp.scale = new Vector3(18f, 7f, 20f);
        var vel = rain.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.Local;
        vel.x = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f); vel.y = new ParticleSystem.MinMaxCurve(-10f, -8f); vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.045f; r.lengthScale = 2f;
        Plain(r, rainMat);
        go.SetActive(false);
    }

    static void Plain(ParticleSystemRenderer r, Material m)
    {
        r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
    }

    ParticleSystem NewSpray()
    {
        var go = new GameObject("Spray");
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var m = ps.main;
        m.loop = true; m.playOnAwake = false; m.simulationSpace = ParticleSystemSimulationSpace.World;
        m.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.8f); m.startSpeed = new ParticleSystem.MinMaxCurve(3f, 7f);
        m.startSize = new ParticleSystem.MinMaxCurve(0.7f, 1.3f); m.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
        m.startColor = new Color(0.80f, 0.82f, 0.86f, 0.22f); m.maxParticles = SprayMax; m.gravityModifier = 0.05f;
        var shp = ps.shape; shp.enabled = true; shp.shapeType = ParticleSystemShapeType.Cone; shp.angle = 14f; shp.radius = 0.7f;
        var inh = ps.inheritVelocity; inh.enabled = true; inh.mode = ParticleSystemInheritVelocityMode.Initial; inh.curveMultiplier = 0.75f;
        var sz = ps.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 3.2f));
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var em = ps.emission; em.rateOverTime = 0f;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard; r.maxParticleSize = 0.35f; // a puff never fills the screen
        Plain(r, sprayMat);
        return ps;
    }

    // ---------------------------------------------------------------- per frame (GameManager)
    // Graphics level and weather; cheap to call every frame.
    public void Begin(int gfxLevel, float camSpeed)
    {
        if (rain == null) return;
        low = gfxLevel <= 1;
        float w = Weather.Wetness;
        if (w != wet) { wet = w; }
        bool raining = w > 0.75f;
        if (rain.gameObject.activeSelf != raining) { rain.gameObject.SetActive(raining); if (raining) rain.Play(); }
        if (raining)
        {
            var m = rain.main; m.maxParticles = low ? RainMaxLow : RainMax;
            float v = Mathf.Max(0f, camSpeed);
            var em = rain.emission; em.rateOverTime = (low ? 0.4f : 1f) * (320f + 9f * v); // more drops hit you the faster you go
            var vel = rain.velocityOverLifetime; vel.z = new ParticleSystem.MinMaxCurve(-v * 0.95f - 1f, -v * 1.05f - 2f);
        }
        used = 0;
    }

    // One car (its body's transform, + z forward) at speed m/s. Call for the player and every rival, then End().
    public void Spray(Transform car, float speed)
    {
        if (rain == null || car == null) return;
        float w = Weather.Wetness;
        if (low || w <= 0f) return;
        var cp = cam.transform.position; var p = car.position;
        if ((p - cp).sqrMagnitude > SprayRange * SprayRange) return;
        if (used == sprays.Count) { sprays.Add(NewSpray()); sprayT.Add(sprays[used].transform); }
        var ps = sprays[used]; var t = sprayT[used]; used++;
        if (!ps.gameObject.activeSelf) ps.gameObject.SetActive(true);
        if (!ps.isPlaying) ps.Play();
        var fwd = car.forward;
        t.SetPositionAndRotation(p - fwd * 2.5f + Vector3.up * 0.3f, Quaternion.LookRotation(-fwd + Vector3.up * 0.25f));
        float k = Mathf.Clamp01((speed - 10f) / 50f) * (w > 0.75f ? 1f : 0.35f); // damp: a light mist off the drying track
        var em = ps.emission; em.rateOverTime = 50f * k;
    }

    public void End()
    {
        for (int i = used; i < sprays.Count; i++)
        {
            var ps = sprays[i];
            if (ps.gameObject.activeSelf) { var em = ps.emission; em.rateOverTime = 0f; if (ps.particleCount == 0) ps.gameObject.SetActive(false); }
        }
    }

    void OnDestroy()
    {
        foreach (var ps in sprays) if (ps != null) Destroy(ps.gameObject);
    }

    // soft shape in alpha: streak (thin across, fading at both ends) or round puff with a little grain
    static Texture2D Soft(int n, bool streak)
    {
        var px = new Color32[n * n];
        var rnd = new System.Random(7);
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f, a;
            if (streak) a = Mathf.Clamp01(1f - Mathf.Abs(u)) * Mathf.Sqrt(Mathf.Clamp01(1f - Mathf.Abs(v)));
            else { float r2 = u * u + v * v; a = r2 < 1f ? (1f - r2) * (1f - r2) * (0.8f + 0.2f * (float)rnd.NextDouble()) : 0f; }
            px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
        }
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }
}
