// BuildScript.cs — called by the cloud build (GameCI: buildMethod BuildScript.Build).
// Creates everything the build needs from code, so the project never has to be opened in the Unity editor:
// an empty startup scene (the game builds itself at runtime), a base material that keeps the Standard
// shader in the build, the player settings for Android / WebGL, and MSAA in the quality levels.
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildScript
{
    const string ScenePath = "Assets/Scenes/Main.unity";

    static string Arg(string name)
    {
        var a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return null;
    }

    [MenuItem("F2/Prepare project")]
    public static void Prepare()
    {
        Directory.CreateDirectory("Assets/Scenes");
        Directory.CreateDirectory("Assets/Resources");
        if (!File.Exists("Assets/Resources/Base.mat"))
        {
            var m = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(m, "Assets/Resources/Base.mat");
        }
        // materials that keep the shaders we create at runtime inside the build
        if (!File.Exists("Assets/Resources/Sky.mat"))
            AssetDatabase.CreateAsset(new Material(Shader.Find("Skybox/Procedural")), "Assets/Resources/Sky.mat");
        if (!File.Exists("Assets/Resources/Glow.mat"))
        {
            // Standard shader with emission switched on, so that shader variant is part of the build (brake lights)
            var g = new Material(Shader.Find("Standard"));
            g.EnableKeyword("_EMISSION"); g.SetColor("_EmissionColor", Color.red);
            g.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            AssetDatabase.CreateAsset(g, "Assets/Resources/Glow.mat");
        }
        if (!File.Exists("Assets/Resources/Diffuse.mat"))
        {
            var sh = Shader.Find("Legacy Shaders/Diffuse");
            if (sh != null) AssetDatabase.CreateAsset(new Material(sh), "Assets/Resources/Diffuse.mat");
        }
        // GPU instancing variants: with the default "Strip Unused" instancing setting Unity keeps a shader's
        // INSTANCING_ON variants only if a material in the build has GPU instancing on. The game makes its materials at
        // runtime, so these two assets keep them: the instanced model trees (FoliageInst, ModelInstancing.cs) and the
        // rivals' shared car material (CarPaint, CarModel.CreateMergedRival) and the shared pit garages (PitGarage.cs)
        foreach (var nm in new[] { "FoliageInst", "CarPaint", "PitGarage" })
        {
            string mp = "Assets/Resources/" + nm + "Instanced.mat";
            if (File.Exists(mp)) continue;
            var sh = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Resources/" + nm + ".shader");
            if (sh != null) AssetDatabase.CreateAsset(new Material(sh) { enableInstancing = true }, mp);
        }
        if (!File.Exists("Assets/Resources/Shadow.mat"))
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Transparent")), "Assets/Resources/Shadow.mat");
        if (!File.Exists(ScenePath))
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }
        AssetDatabase.SaveAssets();
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

        PlayerSettings.companyName = "adiachar";
        PlayerSettings.productName = "F2 Racing";
        PlayerSettings.bundleVersion = "0.1.0";
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.colorSpace = ColorSpace.Linear;

        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.adiachar.f2racing");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel23;
        // Android Frame Pacing (Swappy): frames are shown at even intervals, and on 90/120 Hz screens the display is
        // asked for a rate the 30/60 fps target divides into. Without it a 60 fps target on a 90 Hz panel alternates
        // 11 ms and 22 ms frames, which reads as a judder of everything on screen.
        PlayerSettings.Android.optimizedFramePacing = true;
        // Graphics APIs, explicitly (the same order Unity 6 picks by default, so a changed default can't move it): Vulkan
        // first, OpenGL ES 3 as the fallback for phones whose Vulkan driver Unity blocks. On Mali-G68 / G57 class GPUs
        // Vulkan has the lower CPU cost per draw call and is where dynamic resolution works (GameManagerPerf.DynResWorks).
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.Vulkan, UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
        // a separate render thread (the main thread runs the 240 Hz physics); graphics jobs are left at Unity's default
        PlayerSettings.SetMobileMTRendering(NamedBuildTarget.Android, true);
        PlayerSettings.Android.androidIsGame = true; // Android knows it is a game (game modes, game turbo); appCategory=game is added by AndroidGameManifest.cs
        PlayerSettings.enableFrameTimingStats = true; // FrameTimingManager in release players (PerfLab GPU ms)
        // incremental GC: a collection is spread over several frames instead of one long pause (a hitch mid-corner)
        PlayerSettings.gcIncremental = true;
        // ASTC for any imported texture (the game makes its textures at runtime; Texture2D.Compress picks its own format)
        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

        QualityLevels();

        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;
    }

    // The project uses the built-in render pipeline (no URP package), so MSAA is QualitySettings.antiAliasing.
    // Every quality level gets 4x, so whatever level a platform starts on, the screen buffer is created with MSAA
    // from the first frame; GameManager.ApplyGraphics then sets the per-level value (4x Medium/High, 2x Low).
    static void QualityLevels()
    {
        int keep = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.antiAliasing = 4;
        }
        QualitySettings.SetQualityLevel(keep, false);
    }

    public static void Build()
    {
        Prepare();
        var target = EditorUserBuildSettings.activeBuildTarget;
        string path = Arg("-customBuildPath") ?? (target == BuildTarget.Android ? "build/Android/F2Racing.apk" : "build/" + target);
        Debug.Log($"[F2] Building {target} -> {path}");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = path,
            target = target,
            options = BuildOptions.None,
        });
        Debug.Log($"[F2] Build result: {report.summary.result}, size {report.summary.totalSize / 1048576f:0.0} MB, errors {report.summary.totalErrors}");
        if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }
}
