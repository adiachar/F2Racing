// BuildScript.cs — called by the cloud build (GameCI: buildMethod BuildScript.Build).
// Creates everything the build needs from code, so the project never has to be opened in the Unity editor:
// an empty startup scene (the game builds itself at runtime), a base material that keeps the Standard
// shader in the build, and the player settings for Android / WebGL.
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

        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;
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
