#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;

// Tells Android (and phone "game turbo"/game-mode features such as MIUI Game Turbo and Android's GameManager) that this
// app is a GAME: android:appCategory="game" and android:isGame="true" on the <application> of every generated manifest.
// Runs after Unity writes the Gradle project, before Gradle builds it.
public class AndroidGameManifest : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 100;
    const string NsAndroid = "http://schemas.android.com/apk/res/android";

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        // path = <gradle>/unityLibrary; the launcher module sits next to it
        string root = Directory.GetParent(path).FullName;
        Mark(Path.Combine(path, "src", "main", "AndroidManifest.xml"));
        Mark(Path.Combine(root, "launcher", "src", "main", "AndroidManifest.xml"));
    }

    static void Mark(string file)
    {
        if (!File.Exists(file)) return;
        var doc = new XmlDocument();
        doc.Load(file);
        var app = doc.SelectSingleNode("/manifest/application") as XmlElement;
        if (app == null) return;
        app.SetAttribute("appCategory", NsAndroid, "game");
        app.SetAttribute("isGame", NsAndroid, "true");
        doc.Save(file);
    }
}
#endif
