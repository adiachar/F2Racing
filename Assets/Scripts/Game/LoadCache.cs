// LoadCache.cs — load time: results of slow, pure-math generation steps kept on disk between runs
// (Application.persistentDataPath/gen), so the second load of a circuit skips them.
//  * HeightField's terrain grid (every 5 m cell weighs every track point: the slowest pure-C# step of a load).
// Every entry's key is a hash of everything the data depends on plus a version (HeightField.GridVersion), so a
// changed circuit or algorithm never reads stale data. At most MaxFiles entries are kept (oldest removed).
using System.IO;
using UnityEngine;

public static class LoadCache
{
    const int MaxFiles = 24;
    static string dir;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        try { dir = Path.Combine(Application.persistentDataPath, "gen"); Directory.CreateDirectory(dir); }
        catch (System.Exception e) { Debug.LogWarning("LoadCache off: " + e.Message); dir = null; return; }
        FormulaSim.HeightField.GridLoad = LoadFloats;
        FormulaSim.HeightField.GridSave = SaveFloats;
    }

    static float[] LoadFloats(string key, int count)
    {
        if (dir == null || key == null) return null;
        try
        {
            var path = Path.Combine(dir, key + ".bin");
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != count * 4) return null;
            var f = new float[count]; System.Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
            File.SetLastWriteTimeUtc(path, System.DateTime.UtcNow); // recently used: kept by Prune
            return f;
        }
        catch (System.Exception e) { Debug.LogWarning("LoadCache read " + key + ": " + e.Message); return null; }
    }

    static void SaveFloats(string key, float[] f)
    {
        if (dir == null || key == null || f == null) return;
        try
        {
            var bytes = new byte[f.Length * 4]; System.Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
            var path = Path.Combine(dir, key + ".bin"); var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path); // never a half-written entry
            Prune();
        }
        catch (System.Exception e) { Debug.LogWarning("LoadCache write " + key + ": " + e.Message); }
    }

    static void Prune()
    {
        var files = new DirectoryInfo(dir).GetFiles("*.bin");
        if (files.Length <= MaxFiles) return;
        System.Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        for (int i = 0; i < files.Length - MaxFiles; i++) files[i].Delete();
    }
}
