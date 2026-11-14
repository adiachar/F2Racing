// Haptics.cs — short vibration pulses on Android (kerbs, wall hits). Uses the Vibrator service through JNI:
// VibrationEffect.createOneShot(ms, amplitude) on Android 8+, the plain vibrate(ms) before that.
// Everything is guarded: in the editor and on other platforms it does nothing, and any JNI failure turns it off
// for the session. At most one pulse every 80 ms.
using UnityEngine;

public static class Haptics
{
    public static bool enabled = true;
    const float MinGap = 0.08f;
    static float lastPulse = -1f;

#if UNITY_ANDROID && !UNITY_EDITOR
    static AndroidJavaObject vibrator; static AndroidJavaClass effectClass;
    static bool tried, failed;
    static int sdk;

    static bool Init()
    {
        if (tried) return !failed;
        tried = true;
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
            using (var version = new AndroidJavaClass("android.os.Build$VERSION")) sdk = version.GetStatic<int>("SDK_INT");
            failed = vibrator == null || !vibrator.Call<bool>("hasVibrator");
        }
        catch (System.Exception) { failed = true; }
        return !failed;
    }

    // Never called. Its presence makes Unity add the VIBRATE permission to the generated Android manifest.
    public static void PermissionHint() { Handheld.Vibrate(); }
#endif

    // ms: pulse length; amplitude: 1..255 (ignored by phones without amplitude control)
    public static void Pulse(int ms, int amplitude)
    {
        if (!enabled || ms <= 0) return;
        float now = Time.unscaledTime;
        if (lastPulse >= 0 && now - lastPulse < MinGap) return;
        lastPulse = now;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Init()) return;
        try
        {
            amplitude = Mathf.Clamp(amplitude, 1, 255);
            if (sdk >= 26)
            {
                if (effectClass == null) effectClass = new AndroidJavaClass("android.os.VibrationEffect"); // looked up once, not per pulse
                using (var effect = effectClass.CallStatic<AndroidJavaObject>("createOneShot", (long)ms, amplitude))
                    vibrator.Call("vibrate", effect);
            }
            else vibrator.Call("vibrate", (long)ms);
        }
        catch (System.Exception) { failed = true; }
#endif
    }
}
