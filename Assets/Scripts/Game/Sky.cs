// Sky.cs — the sky on top of Unity's procedural skybox, for almost nothing on a phone:
//  * per-circuit look (cloud cover, skybox tint / exposure, fog), chosen from the circuit id (Spa grey, Monaco sunny),
//  * a cloud layer + horizon haze (Clouds.shader) on a sphere round the camera just inside the far plane, drawn
//    after the opaque world so the depth test leaves it only the sky pixels; one tiling noise texture made at load, 2 reads per pixel, scrolling with the wind,
//  * a soft sun glare (SunGlare.shader): a camera-facing additive quad that fades as the sun leaves the screen.
// Weather (Weather.cs): Damp / Wet close the sky (more cloud, greyer, darker), pull the fog in and take the sun glare
// away, by changing these same values (Wetted); SetWeather re-applies them at once when the setting changes.
// Cost: +2 draw calls (+1 when the sun is off-screen), no per-frame allocations. The cloud sphere sits on its own
// layer so the one-off sky reflection probe (cars' paint) captures it too: Create renders the probe and keeps the
// sphere round the probe until that render has finished.
// Time of day (TimeOfDay.cs, AtTime / SunAt): sun angle and colour, sky, fog, cloud light and the bake's light.
// Colour grade (Grade.cs / Grade.cginc): per circuit + time of day + weather, pushed as shader globals here.
using FormulaSim;
using UnityEngine;

[DefaultExecutionOrder(1000)] // after the camera has been moved for this frame
public class Sky : MonoBehaviour
{
    public const int Layer = 1; // built-in "TransparentFX" layer, used for nothing else: the reflection probe renders it

    public struct Look
    {
        public float cover;            // 0 clear .. 1 overcast
        public float haze;             // strength of the horizon haze band
        public float deck;             // cloud deck scale (smaller = bigger clouds)
        public float wind;             // cloud drift speed factor
        public Color tint;             // procedural skybox _SkyTint
        public float atmosphere, exposure;
        public Color fog; public float fogStart, fogEnd;
    }

    // per circuit, from Track.def (id; style and forest for circuits not listed). Every dry circuit is a clear, sunny
    // race day: a clean, saturated blue sky (brighter than the old one, which at 45 degrees was darker than the sunlit
    // asphalt: a "dim movie" look), fair-weather cloud, and a light, clean blue horizon haze instead of a grey one.
    // Procedural skybox numbers (Unity's maths, linear colour space, sun 48 degrees up), sRGB on screen:
    //   tint (0.40, 0.54, 0.80), atmosphere 0.95, exposure 2.0: zenith (0.33, 0.47, 0.82), 45 deg (0.32, 0.46, 0.78),
    //   15 deg ~(0.52, 0.72, 1.0); the bottom ~8 degrees are covered by the haze band in the fog colour (Clouds.shader).
    // Fog: it starts further out and is a clean light blue that matches the horizon, so distance reads as air, not milk;
    // it is still well on at the cloud sphere (0.9 x the far plane), where the haze band takes over the far land.
    public static Look For(Track t, bool street)
    {
        var k = new Look
        {
            cover = 0.3f, haze = 0.75f, deck = 0.22f, wind = 1f, tint = new Color(0.30f, 0.48f, 0.85f), atmosphere = 0.85f, exposure = 2.0f,
            fog = new Color(0.62f, 0.76f, 0.90f), fogStart = 420, fogEnd = 1950,
        };
        var d = t.def;
        string id = d != null ? d.id : "";
        if (street) { k.fog = new Color(0.70f, 0.79f, 0.90f); k.fogStart = 350; k.fogEnd = 1750; } // sea air
        else if (d != null && d.forest > 0.6) { k.fog = new Color(0.66f, 0.77f, 0.86f); k.fogStart = 300; k.fogEnd = 1700; k.cover = 0.42f; }
        else if (d != null) k.cover = 0.22f + 0.25f * (float)d.forest;
        switch (id)
        {
            case "monaco": // Riviera: deep blue, a few fair-weather clouds
                k.cover = 0.22f; k.haze = 0.7f; k.tint = new Color(0.36f, 0.52f, 0.82f); k.atmosphere = 0.85f; k.exposure = 2.1f;
                break;
            case "spa": // Ardennes on a good day: big cumulus over the forest, fresh air
                k.cover = 0.5f; k.haze = 0.8f; k.deck = 0.18f; k.tint = new Color(0.42f, 0.55f, 0.78f); k.atmosphere = 1.0f; k.exposure = 1.85f;
                k.fog = new Color(0.66f, 0.76f, 0.84f); k.fogStart = 300; k.fogEnd = 1650;
                break;
            case "silverstone": // English summer on an open plateau: sunshine and fair-weather cumulus
                k.cover = 0.46f; k.deck = 0.19f; k.tint = new Color(0.42f, 0.55f, 0.79f); k.atmosphere = 1.0f; k.exposure = 1.9f;
                k.fog = new Color(0.68f, 0.77f, 0.88f); k.fogStart = 380; k.fogEnd = 1800;
                break;
            case "monza": // Po valley: warm, a little hazy, clear
                k.cover = 0.25f; k.haze = 0.85f; k.tint = new Color(0.20f, 0.44f, 0.88f); k.atmosphere = 0.9f; k.exposure = 2.0f; // deep clean azure, little sunset scatter (RR3)
                k.fog = new Color(0.62f, 0.76f, 0.90f); k.fogStart = 340; k.fogEnd = 1750;
                break;
            case "suzuka":
                k.cover = 0.38f; k.fog = new Color(0.68f, 0.78f, 0.90f);
                break;
            case "austin": // Texas: hot, nearly cloudless, a little dust low down
                k.cover = 0.15f; k.tint = new Color(0.42f, 0.55f, 0.80f); k.exposure = 2.05f;
                k.fog = new Color(0.74f, 0.80f, 0.88f); k.fogStart = 400; k.fogEnd = 1900;
                break;
            case "budapest": // summer heat
                k.cover = 0.22f; k.haze = 0.85f; k.exposure = 2.0f;
                k.fog = new Color(0.73f, 0.79f, 0.88f); k.fogStart = 360; k.fogEnd = 1800;
                break;
            case "catalunya": // Mediterranean: clear and blue
                k.cover = 0.18f; k.haze = 0.8f; k.tint = new Color(0.36f, 0.52f, 0.82f); k.atmosphere = 0.85f; k.exposure = 2.1f;
                k.fog = new Color(0.70f, 0.79f, 0.90f); k.fogStart = 400; k.fogEnd = 1850;
                break;
            case "spielberg": // Alps: clear air, big fair-weather cumulus
                k.cover = 0.42f; k.haze = 0.65f; k.deck = 0.17f; k.tint = new Color(0.38f, 0.53f, 0.82f); k.atmosphere = 0.85f; k.exposure = 2.05f;
                k.fog = new Color(0.66f, 0.78f, 0.92f); k.fogStart = 480; k.fogEnd = 2100;
                break;
            case "zandvoort": // North Sea dunes: windy, broken cloud, bright
                k.cover = 0.38f; k.wind = 2.0f; k.haze = 0.8f; k.tint = new Color(0.40f, 0.54f, 0.80f); k.exposure = 2.0f;
                k.fog = new Color(0.70f, 0.80f, 0.90f); k.fogStart = 380; k.fogEnd = 1800;
                break;
        }
        return k;
    }

    // ---------------------------------------------------------------- weather
    // the circuit's look under rain: w 0 = dry (unchanged), 0.5 = damp (broken grey cloud), 1 = wet (closed, raining)
    public static Look Wetted(Look k, float w)
    {
        if (w <= 0f) return k;
        var grey = new Color(0.50f, 0.53f, 0.57f) * wetGrey; // darker at dusk and night
        k.cover = Mathf.Lerp(k.cover, 0.98f, Mathf.Clamp01(w * 1.4f));
        k.haze = Mathf.Lerp(k.haze, 1f, w);
        k.deck = Mathf.Lerp(k.deck, 0.15f, w);                       // a low, even deck
        k.wind *= 1f + 0.8f * w;
        k.tint = Color.Lerp(k.tint, new Color(0.55f, 0.57f, 0.60f), w);
        k.atmosphere = Mathf.Lerp(k.atmosphere, 1.3f, w);
        k.exposure *= 1f - 0.5f * w;                                   // (the dry sky is brighter now: wet ends where it did)
        k.fog = Color.Lerp(k.fog, grey, Mathf.Clamp01(w * 1.3f));
        k.fogStart *= 1f - 0.8f * w;                                   // wet: fog from ~20 % of the dry start
        k.fogEnd *= 1f - 0.6f * w;                                     // and gone by ~40 % of the dry distance
        return k;
    }
    static float GlareMax(Look k, float w) => 0.4f * todGlare * (1f - 0.65f * k.cover) * Mathf.Clamp01(1f - 1.6f * w); // none in the rain

    // ---------------------------------------------------------------- time of day (TimeOfDay.cs)
    // Afternoon is the original light. The others move and colour the sun, change the sky, fog and cloud light,
    // the cars' ambient and the colours LightBake bakes with, and add their grade on top of the circuit's.
    static TimeOfDay.Kind tod = TimeOfDay.Kind.Afternoon;
    static float todSun = 1f, todGlare = 1f, wetGrey = 1f;
    static Color cloudLit = Color.white, cloudShade = Color.white, cloudSun = Color.white;
    static GradeLook dryGrade = Grade.Neutral;

    static Look AtTime(Look k, TimeOfDay.Kind t)
    {
        cloudLit = cloudShade = cloudSun = Color.white; todGlare = 1f; wetGrey = 1f;
        switch (t)
        {
            case TimeOfDay.Kind.Morning: // low, cool-gold sun, fresh haze
                k.atmosphere *= 1.15f; k.exposure *= 0.95f; k.haze = Mathf.Min(1f, k.haze + 0.1f);
                k.fog = Color.Lerp(k.fog, new Color(0.80f, 0.84f, 0.90f), 0.3f); k.fogStart *= 0.8f; k.fogEnd *= 0.9f;
                cloudLit = new Color(1f, 0.97f, 0.93f); cloudSun = new Color(1f, 0.9f, 0.75f);
                break;
            case TimeOfDay.Kind.Evening: // golden hour: orange sun low in the west, warm haze, long shadows
                k.atmosphere = Mathf.Max(k.atmosphere, 1.2f); k.exposure *= 0.85f;
                k.fog = Color.Lerp(k.fog, new Color(0.86f, 0.70f, 0.56f), 0.45f);
                cloudLit = new Color(1f, 0.82f, 0.66f) * 0.92f; cloudShade = new Color(0.85f, 0.78f, 0.80f); cloudSun = new Color(1.4f, 0.85f, 0.42f);
                todGlare = 1.25f; wetGrey = 0.75f;
                break;
            case TimeOfDay.Kind.Night:   // a dark sky with a small moon, dark fog, floodlights (LightBake)
                k.exposure = 0.08f; k.atmosphere = 0.5f; k.tint = new Color(0.30f, 0.35f, 0.50f);
                k.fog = new Color(0.06f, 0.06f, 0.075f); k.fogStart *= 0.8f; k.fogEnd *= 0.9f;
                cloudLit = new Color(0.07f, 0.075f, 0.095f); cloudShade = new Color(0.12f, 0.12f, 0.15f); cloudSun = new Color(0.2f, 0.22f, 0.3f);
                todGlare = 0f; wetGrey = 0.12f;
                break;
        }
        return k;
    }

    // sun angle / colour / intensity, the cars' ambient and the bake's light for the time of day.
    // Balance (afternoon, on flat ground): sun 1.45 x sin 48 = 1.08 against a sky fill of (0.49, 0.59, 0.77) after
    // LightBake's gain: sunlit white is neutral-warm (it was blue-tinted: the old sky fill was as strong as the sun and
    // bluer), shade is a clean blue-grey at ~0.36 of the sun side in linear (0.58 on screen; it was 0.69: flat).
    // The bake gets the sun colour in linear, like the real-time light the cars get, so world and cars match.
    static void SunAt(Light sun, TimeOfDay.Kind t)
    {
        Color sky = new Color(0.37f, 0.45f, 0.58f); // the bake's sky light, linear (blue from above; LightBake scales it)
        switch (t)
        {
            case TimeOfDay.Kind.Morning: // low, cool-gold sun
                sun.transform.rotation = Quaternion.Euler(24, -110, 0); sun.color = new Color(1f, 0.93f, 0.82f); sun.intensity = 1.4f;
                sky = new Color(0.36f, 0.44f, 0.60f);
                RenderSettings.ambientSkyColor = new Color(0.66f, 0.74f, 0.86f); RenderSettings.ambientEquatorColor = new Color(0.60f, 0.62f, 0.64f);
                break;
            case TimeOfDay.Kind.Afternoon:
                sun.color = new Color(1f, 0.95f, 0.86f); sun.intensity = 1.45f;
                RenderSettings.ambientSkyColor = new Color(0.69f, 0.76f, 0.86f); RenderSettings.ambientEquatorColor = new Color(0.62f, 0.64f, 0.64f);
                break;
            case TimeOfDay.Kind.Evening: // golden hour: a strong, low, warm sun lights every wall that faces it
                sun.transform.rotation = Quaternion.Euler(13, 70, 0); sun.color = new Color(1f, 0.80f, 0.56f); sun.intensity = 1.6f;
                sky = new Color(0.29f, 0.32f, 0.45f);
                RenderSettings.ambientSkyColor = new Color(0.58f, 0.62f, 0.74f); RenderSettings.ambientEquatorColor = new Color(0.64f, 0.58f, 0.52f);
                RenderSettings.ambientGroundColor *= new Color(1f, 0.85f, 0.7f);
                break;
            case TimeOfDay.Kind.Night:   // the "sun" is the moon; the cars get the floodlights (Create)
                sun.transform.rotation = Quaternion.Euler(42, 20, 0); sun.color = new Color(0.62f, 0.72f, 1f); sun.intensity = 0.16f;
                sky = new Color(0.025f, 0.032f, 0.06f);
                RenderSettings.ambientSkyColor = new Color(0.06f, 0.07f, 0.11f); RenderSettings.ambientEquatorColor = new Color(0.16f, 0.15f, 0.14f);
                RenderSettings.ambientGroundColor = new Color(0.10f, 0.09f, 0.08f);
                break;
        }
        todSun = t == TimeOfDay.Kind.Night ? 0.5f : sun.intensity / 1.25f; // GameManager: car sun = 1.25 x SunShare
        bool night = t == TimeOfDay.Kind.Night;
        // night keeps its tuned (non-linear) moon value: the floodlights do the work there
        LightBake.SetLight(night ? sun.color * sun.intensity : sun.color.linear * sun.intensity, sky, night);
    }

    static Look dryLook; static bool haveLook;
    static Material skyInst; static Light sunLight; static float sunBase = -1f, shadowBase = 1f;
    public static Sky Current;                                         // the live sky (null before the first build)
    // 0..1: how much of the sun reaches the ground through the cloud (the cars' sun light follows this)
    public static float SunShare => (1f - 0.55f * Weather.Wetness) * todSun;

    // Re-applies the sky, fog, clouds, sun and glare for a new weather without rebuilding the world, and re-renders
    // the cars' sky reflection once (a one-off, like at the build).
    public static void SetWeather(float w)
    {
        if (!haveLook) return;
        var k = Wetted(dryLook, w);
        Grade.Push(Grade.Wetted(dryGrade, w));
        SkyAndFog(k);
        if (cloudMat != null) CloudParams(k);
        SunFor(w);
        var s = Current;
        if (s == null) return;
        s.glareMax = GlareMax(k, w); s.glareLast = -1f;
        if (s.probeRef != null && s.probeRef.isActiveAndEnabled)
        {
            s.probe = s.probeRef; s.probeWait = 0;
            if (s.dome != null) { s.dome.position = s.probeRef.transform.position; s.dome.localScale = Vector3.one * (s.probeRef.farClipPlane * DomeFill); }
            s.probeId = s.probeRef.RenderProbe();
        }
    }

    static void SkyAndFog(Look k)
    {
        if (skyInst != null)
        {
            skyInst.SetFloat("_AtmosphereThickness", k.atmosphere); skyInst.SetFloat("_Exposure", k.exposure);
            skyInst.SetColor("_SkyTint", k.tint);
        }
        RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear; RenderSettings.fogColor = k.fog;
        RenderSettings.fogStartDistance = k.fogStart; RenderSettings.fogEndDistance = k.fogEnd;
        var cam = Camera.main; if (cam != null && cam.clearFlags == CameraClearFlags.SolidColor) cam.backgroundColor = Grade.Apply(k.fog); // no shader grades it
    }

    static void SunFor(float w)
    {
        if (sunLight == null) return;
        if (sunBase < 0f) { sunBase = sunLight.intensity; shadowBase = sunLight.shadowStrength; }
        sunLight.intensity = sunBase * (1f - 0.55f * w);
        sunLight.shadowStrength = shadowBase * (1f - 0.7f * w);        // overcast: soft, faint shadows
    }

    static void CloudParams(Look k)
    {
        float c = k.cover;
        cloudMat.SetFloat("_Cover", c);
        cloudMat.SetFloat("_Scale", k.deck);
        cloudMat.SetFloat("_Haze", k.haze);
        cloudMat.SetFloat("_HazeHeight", 0.2f); // a little higher: covers the procedural sky's over-cyan bottom degrees
        cloudMat.SetColor("_LitCol", Color.Lerp(new Color(1f, 0.99f, 0.96f), new Color(0.90f, 0.91f, 0.92f), c * c) * cloudLit * (1f - 0.3f * Weather.Wetness));
        // fair-weather cumulus have light, sky-lit blue-grey bases; only a closing (rain) deck goes dark grey
        cloudMat.SetColor("_ShadeCol", Color.Lerp(new Color(0.74f, 0.78f, 0.86f), new Color(0.44f, 0.47f, 0.52f), c) * cloudShade * (1f - 0.2f * Weather.Wetness));
        cloudMat.SetColor("_SunCol", new Color(1f, 0.95f, 0.85f) * cloudSun * (1f - 0.5f * c));
        cloudMat.SetVector("_Wind", new Vector4(0.030f, 0.012f, 0.050f, 0.020f) * ((0.7f + 0.6f * c) * k.wind));
    }

    // ---------------------------------------------------------------- setup
    static Texture2D noiseTex, glareTex;
    static Mesh domeMesh, quadMesh;
    static Material cloudMat, glareMat;
    static int glareColorId; static float glareScale = 1f; static bool glareTinted;

    // Skybox, fog and the sun (call at the top of the world build, before anything is fogged or baked).
    public static Look Setup(Track t, bool street, Light sun)
    {
        tod = TimeOfDay.For(street); TimeOfDay.Current = tod; TimeOfDay.BuiltStreet = street;
        dryLook = AtTime(For(t, street), tod); haveLook = true;
        if (sun != null) SunAt(sun, tod); // before the road reads the sun colour and the bake reads its direction
        dryGrade = Grade.At(Grade.For(t.def != null ? t.def.id : ""), tod);
        Grade.Push(Grade.Wetted(dryGrade, Weather.Wetness));
        // fog is authored as the colour it should have ON SCREEN; the grade (exposure x warm tint) would push a pale
        // fog into the tone shoulder and turn the horizon cream, so divide it out for the clear-sky times of day
        if (tod != TimeOfDay.Kind.Night && tod != TimeOfDay.Kind.Evening)
        {
            float e = dryGrade.exposure * Grade.BaseExposure;
            dryLook.fog = new Color(Mathf.Min(dryLook.fog.r / (dryGrade.tint.x * e), 0.75f), Mathf.Min(dryLook.fog.g / (dryGrade.tint.y * e), 0.8f), Mathf.Min(dryLook.fog.b / (dryGrade.tint.z * e), 0.85f), 1f);
        }
        var k = Wetted(dryLook, Weather.Wetness);
        sunLight = sun; sunBase = -1f; // dimmed in Create, after the road has read the dry sun colour and the bake is done
        var skyMat = Resources.Load<Material>("Sky");
        if (skyMat != null)
        {
            skyMat = new Material(skyMat);
            skyMat.SetFloat("_SunSize", tod == TimeOfDay.Kind.Night ? 0.018f : 0.035f); skyMat.SetFloat("_SunSizeConvergence", 6f); // night: a small moon
            skyMat.SetFloat("_AtmosphereThickness", k.atmosphere); skyMat.SetFloat("_Exposure", k.exposure);
            skyMat.SetColor("_SkyTint", k.tint); skyMat.SetColor("_GroundColor", new Color(0.38f, 0.42f, 0.36f));
            RenderSettings.skybox = skyMat; skyInst = skyMat;
        }
        // the cloud shader paints the horizon in exactly this colour, so sky and fogged hills always meet
        RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear; RenderSettings.fogColor = k.fog;
        RenderSettings.fogStartDistance = k.fogStart; RenderSettings.fogEndDistance = k.fogEnd;
        if (sun != null) RenderSettings.sun = sun; // the skybox's sun disk follows the world sun, not the cars' one
        return k;
    }

    // Cloud sphere + sun glare, created under root once the world is built (after static batching and the light
    // bake, which must not see them). Then renders the sky reflection probe once, clouds included.
    public static Sky Create(Transform root, Look k, Vector3 sunDir, ReflectionProbe probe)
    {
        var go = new GameObject("Sky"); go.transform.SetParent(root, false);
        var sky = go.AddComponent<Sky>();
        sky.sunDir = sunDir.normalized;

        var cs = Resources.Load<Shader>("Clouds");
        if (cs != null && cs.isSupported)
        {
            if (noiseTex == null) noiseTex = CloudNoise();
            if (domeMesh == null) domeMesh = Dome();
            if (cloudMat == null) cloudMat = new Material(cs) { mainTexture = noiseTex };
            CloudParams(k);
            cloudMat.SetVector("_SunDir", new Vector4(sky.sunDir.x, sky.sunDir.y, sky.sunDir.z, 0));
            var dome = new GameObject("Clouds"); dome.layer = Layer;
            dome.transform.SetParent(go.transform, false);
            dome.AddComponent<MeshFilter>().sharedMesh = domeMesh;
            sky.dome = dome.transform;
            Plain(dome.AddComponent<MeshRenderer>(), cloudMat);
        }
        // else: no clouds; the procedural skybox and fog still look as before

        if (glareMat == null) glareMat = GlareMaterial();
        if (glareMat != null)
        {
            if (quadMesh == null) quadMesh = Quad();
            var q = new GameObject("SunGlare"); q.transform.SetParent(go.transform, false); // Default layer: not in the reflections
            q.AddComponent<MeshFilter>().sharedMesh = quadMesh;
            sky.glare = q.AddComponent<MeshRenderer>();
            Plain(sky.glare, glareMat);
            sky.glareMax = GlareMax(k, Weather.Wetness); // a cloudy sky diffuses the sun; none in the rain
            sky.glare.enabled = false;
        }
        sky.probe = probe; sky.probeRef = probe; Current = sky;
        TimeOfDay.BuiltRoot = root.gameObject;
        if (tod == TimeOfDay.Kind.Night)
        {
            // the cars are lit by the floodlights (white, from above), not by the moon
            if (LightBake.CarSun != null) LightBake.CarSun.color = new Color(1f, 0.95f, 0.88f);
            // no baked floodlights on this phone: lift the ambient so the circuit can still be seen
            if (!LightBake.Active) { RenderSettings.ambientSkyColor = new Color(0.25f, 0.26f, 0.3f); RenderSettings.ambientEquatorColor = new Color(0.3f, 0.29f, 0.27f); }
        }
        SunFor(Weather.Wetness);
        if (probe != null)
        {
            if (sky.dome != null)
            {
                sky.dome.position = probe.transform.position;
                sky.dome.localScale = Vector3.one * (probe.farClipPlane * DomeFill);
            }
            sky.probeId = probe.RenderProbe();
        }
        return sky;
    }

    static void Plain(MeshRenderer r, Material m)
    {
        r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
    }

    // own shader; else the legacy additive particle shader (if it is in the build); else the project's
    // Unlit/Transparent "Shadow" material (alpha-blended white glow, can only be switched on and off)
    static Material GlareMaterial()
    {
        if (glareTex == null) glareTex = GlareTexture();
        var s = Resources.Load<Shader>("SunGlare");
        if (s != null && s.isSupported) { glareColorId = Shader.PropertyToID("_Color"); glareTinted = true; glareScale = 1f; return new Material(s) { mainTexture = glareTex }; }
        s = Shader.Find("Legacy Shaders/Particles/Additive");
        if (s != null && s.isSupported) { glareColorId = Shader.PropertyToID("_TintColor"); glareTinted = true; glareScale = 0.5f; return new Material(s) { mainTexture = glareTex }; }
        var sm = Resources.Load<Material>("Shadow");
        if (sm != null) { glareTinted = false; return new Material(sm) { mainTexture = glareTex }; }
        return null;
    }

    // ---------------------------------------------------------------- per frame
    public static bool NoGlare; // Low graphics level (GameManagerPerf.cs): the additive glare quad is not drawn
    Transform dome; MeshRenderer glare; Vector3 sunDir; float glareMax, glareLast = -1f;
    Camera cam; ReflectionProbe probe, probeRef; int probeId, probeWait;
    const float DomeFill = 0.9f;     // sphere radius / far clip (its flat facets stay inside the far plane)
    const float GlareFill = 0.8f;    // glare distance / far clip
    // radians: a glow ~50 degrees across; the sun stands 48 degrees up, above the top of a 62-degree view in normal
    // driving, so mostly the halo's lower part shows, hanging into the top of the screen
    const float GlareHalfAngle = 0.45f;

    void LateUpdate()
    {
        // until the probe has captured the sky, the cloud sphere stays round the probe
        if (probe != null)
        {
            if (probe.IsFinishedRendering(probeId) || ++probeWait > 30) probe = null;
            else return;
        }
        if (cam == null || !cam.isActiveAndEnabled) { cam = Camera.main; if (cam == null) return; }
        var ct = cam.transform; var pos = ct.position; float far = cam.farClipPlane; // far changes with the graphics level
        if (dome != null)
        {
            dome.position = pos;
            float r = far * DomeFill;
            if (Mathf.Abs(dome.localScale.x - r) > 0.5f) dome.localScale = new Vector3(r, r, r);
        }
        if (glare == null) return;

        // glare strength: full when the sun is inside the view, fading as it moves out past the edges (the halo still
        // reaches in while the sun is just off-screen), zero behind
        float gd = far * GlareFill;
        var at = pos + sunDir * gd;
        var vp = cam.WorldToViewportPoint(at);
        float vis = 0f;
        if (vp.z > 0f)
        {
            float e = Mathf.Min(Mathf.Min(vp.x, 1f - vp.x), Mathf.Min(vp.y, 1f - vp.y));
            vis = Mathf.Clamp01((e + 0.35f) / 0.45f); vis = vis * vis * (3f - 2f * vis);
            vis *= 0.6f + 0.4f * Mathf.Clamp01(Vector3.Dot(ct.forward, sunDir)); // brighter looking straight at it
        }
        bool on = !NoGlare && vis > 0.01f && glareMax > 0.001f && (glareTinted || vis > 0.35f);
        if (glare.enabled != on) glare.enabled = on;
        if (!on) return;
        var gt = glare.transform;
        gt.SetPositionAndRotation(at, ct.rotation);
        float size = gd * GlareHalfAngle;
        gt.localScale = new Vector3(size, size, size);
        float k = vis * glareMax;
        if (glareTinted && Mathf.Abs(k - glareLast) > 0.003f)
        {
            glareLast = k;
            float s = glareScale; // the legacy particle shader doubles colour and alpha
            glareMat.SetColor(glareColorId, new Color(s, 0.9f * s, 0.75f * s, k * s));
        }
    }

    // ---------------------------------------------------------------- generated assets (made once per run)
    static float Hash(int x, int y, int s)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + s * 2147483;
            h = (h ^ (int)((uint)h >> 13)) * 1274126177;
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296f;
        }
    }
    // tiling value noise (period 1 in u, v), fractal sum
    static float Fbm(float u, float v, int cells, int oct, int s)
    {
        float sum = 0, amp = 1, norm = 0; int c = cells;
        for (int o = 0; o < oct; o++)
        {
            float fx = u * c, fy = v * c; int i = Mathf.FloorToInt(fx), j = Mathf.FloorToInt(fy);
            float a = fx - i, b = fy - j; a = a * a * (3 - 2 * a); b = b * b * (3 - 2 * b);
            int i0 = ((i % c) + c) % c, j0 = ((j % c) + c) % c, i1 = (i0 + 1) % c, j1 = (j0 + 1) % c;
            float h00 = Hash(i0, j0, s + o), h10 = Hash(i1, j0, s + o), h01 = Hash(i0, j1, s + o), h11 = Hash(i1, j1, s + o);
            sum += amp * Mathf.Lerp(Mathf.Lerp(h00, h10, a), Mathf.Lerp(h01, h11, a), b);
            norm += amp; amp *= 0.5f; c *= 2;
        }
        return sum / norm;
    }

    // 256x256 cloud density in alpha: domain-warped fbm (organic shapes) plus a little billow (puffy edges),
    // stretched to the full 0..1 range so the shader's cover threshold means the same for every seed
    static Texture2D CloudNoise()
    {
        const int N = 256;
        var val = new float[N * N]; float lo = 1e9f, hi = -1e9f;
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            float u = (float)x / N, v = (float)y / N;
            float wx = Fbm(u, v, 4, 2, 101) - 0.5f, wy = Fbm(u, v, 4, 2, 203) - 0.5f;
            float n = Fbm(u + wx * 0.25f, v + wy * 0.25f, 4, 5, 7) * 0.75f + (1f - Mathf.Abs(Fbm(u, v, 8, 3, 55) * 2f - 1f)) * 0.25f;
            val[y * N + x] = n; if (n < lo) lo = n; if (n > hi) hi = n;
        }
        // trim the extreme tails a little so the range is well used
        float span = hi - lo; lo += span * 0.04f; hi -= span * 0.04f;
        var px = new Color32[N * N];
        for (int i = 0; i < px.Length; i++)
        {
            byte b = (byte)Mathf.Clamp(Mathf.RoundToInt((val[i] - lo) / (hi - lo) * 255f), 0, 255);
            px[i] = new Color32(255, 255, 255, b);
        }
        var t = new Texture2D(N, N, TextureFormat.RGBA32, true) { name = "CloudNoise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 0 };
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }

    // radial glow: a bright core and a soft halo that falls off quickly, zero at the quad's edge
    static Texture2D GlareTexture()
    {
        const int N = 64;
        var px = new Color32[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
            float g = 0f;
            if (r < 1f) { float f = 1f - r; g = 0.6f * Mathf.Exp(-r * r * 40f) + 0.4f * f * f * f; } // tight halo: no milky veil
            px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(g * 255f), 0, 255));
        }
        var t = new Texture2D(N, N, TextureFormat.RGBA32, true) { name = "SunGlare", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }

    // unit sphere (16 x 8 facets); the cloud shader takes the exact view direction per pixel, so a coarse one is enough
    static Mesh Dome()
    {
        const int Seg = 16, Rings = 8;
        var v = new Vector3[(Seg + 1) * (Rings + 1)];
        for (int r = 0; r <= Rings; r++)
        {
            float lat = Mathf.PI * r / Rings - Mathf.PI / 2, cl = Mathf.Cos(lat), sl = Mathf.Sin(lat);
            for (int a = 0; a <= Seg; a++)
            {
                float lon = Mathf.PI * 2 * a / Seg;
                v[r * (Seg + 1) + a] = new Vector3(Mathf.Cos(lon) * cl, sl, Mathf.Sin(lon) * cl);
            }
        }
        var tri = new int[Seg * Rings * 6]; int n = 0;
        for (int r = 0; r < Rings; r++) for (int a = 0; a < Seg; a++)
        {
            int i0 = r * (Seg + 1) + a, i1 = i0 + 1, i2 = i0 + Seg + 1, i3 = i2 + 1;
            tri[n++] = i0; tri[n++] = i2; tri[n++] = i1; tri[n++] = i1; tri[n++] = i2; tri[n++] = i3;
        }
        var m = new Mesh { name = "SkyDome" };
        m.vertices = v; m.triangles = tri;
        m.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
        return m;
    }

    static Mesh Quad()
    {
        var m = new Mesh { name = "GlareQuad" };
        m.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
        m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        m.RecalculateBounds();
        return m;
    }
}
