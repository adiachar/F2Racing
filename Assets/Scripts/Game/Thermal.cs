// Thermal.cs — what the phone itself says about heat and clocks, read every 2 s (never per frame). Used by the Auto
// frame rate (GameManagerPerf.FramePacing: a hot phone drops to the next steady rate before its clocks collapse) and
// shown by the frame-time overlay (FrameLog.cs). See Docs/PERF-INVESTIGATION.md.
//  * Android thermal status (PowerManager.getCurrentThermalStatus, API 29+): 0 none, 1 light, 2 moderate,
//    3 severe, 4 critical, 5 emergency, 6 shutdown; -1 = not available
//  * thermal headroom (PowerManager.getThermalHeadroom(10 s forecast), API 30+): 1.0 = the level where SEVERE
//    throttling starts; NaN = not available (it is also NaN when asked more than once a second)
//  * battery temperature (the sticky ACTION_BATTERY_CHANGED intent, "temperature" in 0.1 C)
//  * SoC temperature (the first readable thermal zone whose type names the CPU / SoC), the fastest CPU core's clock
//    now and at most, and the GPU clock (vendor paths; many phones hide these from apps: then NaN)
//  * the display's refresh rate now (some phones drop 120 Hz to 60 Hz when the screen is not touched)
// Cost: set-up once (a few small allocations); every read is raw JNI with cached method ids and argument arrays, and
// sysfs files kept open and re-read from offset 0: no managed allocation per read. Any failure turns that one reading
// off for the session. Editor / WebGL / iOS: only the refresh rate.
using System;
using System.IO;
using UnityEngine;

public static class Thermal
{
    public static int Status = -1;
    public static float Headroom = float.NaN, BatteryC = float.NaN, SocC = float.NaN;
    public static float CpuGHz = float.NaN, CpuMaxGHz = float.NaN, GpuMHz = float.NaN, Hz = float.NaN;
    public static int Reads;                 // completed reads (the overlay shows "-" until the first)
    public const float Period = 2f;
    // hot enough that Auto should take load off: Android's MODERATE, or headroom close to the SEVERE level
    public static bool Hot => Status >= 2 || Headroom >= 0.9f;

    static float nextT = -1f; static bool inited;

    // once a frame (GameManager.Update); reads only every Period seconds
    public static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < nextT) return;
        nextT = now + Period;
        if (!inited) { inited = true; Init(); }
        Read();
        Reads++;
    }

    // ---------------------------------------------------------------- sysfs: one file kept open, re-read from 0
    sealed class SysFile
    {
        readonly FileStream fs; readonly byte[] buf = new byte[64]; public bool ok = true;
        SysFile(FileStream f) { fs = f; }
        public static SysFile Open(string path)
        {
            try { if (!File.Exists(path)) return null; var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1); var s = new SysFile(f); return s.Number() >= 0 ? s : null; }
            catch (Exception) { return null; }
        }
        // the largest whole number in the file (sysfs regenerates the text on a read at offset 0); -1 on failure
        public long Number()
        {
            if (!ok) return -1;
            try
            {
                fs.Seek(0, SeekOrigin.Begin);
                int n = fs.Read(buf, 0, buf.Length);
                long best = -1, cur = -1;
                for (int i = 0; i <= n; i++)
                {
                    int c = i < n ? buf[i] : ' ';
                    if (c >= '0' && c <= '9') cur = (cur < 0 ? 0 : cur * 10) + (c - '0');
                    else { if (cur > best) best = cur; cur = -1; }
                }
                return best;
            }
            catch (Exception) { ok = false; return -1; }
        }
    }
    static SysFile cpuCur, gpuFreq, socTemp;

    static void InitSysfs()
    {
        // the fastest core (highest cpuinfo_max_freq; on the Dimensity 1080 cpu6/7, the A78s)
        long bestMax = -1; int bestCpu = -1;
        for (int c = 0; c < 16; c++)
        {
            try
            {
                string p = "/sys/devices/system/cpu/cpu" + c + "/cpufreq/cpuinfo_max_freq";
                if (!File.Exists(p)) continue;
                long v = ParseLong(File.ReadAllText(p));
                if (v >= bestMax && v > 0) { bestMax = v; bestCpu = c; }
            }
            catch (Exception) { }
        }
        if (bestCpu >= 0)
        {
            cpuCur = SysFile.Open("/sys/devices/system/cpu/cpu" + bestCpu + "/cpufreq/scaling_cur_freq");
            if (cpuCur != null) CpuMaxGHz = bestMax / 1e6f; // kHz
        }
        // GPU clock: Mali devfreq (MediaTek / Exynos), MediaTek GED, Pixel / Exynos, Adreno
        string[] gpu = { "/sys/class/devfreq/13000000.mali/cur_freq", "/sys/class/misc/mali0/device/cur_freq", "/sys/kernel/ged/hal/current_freqency",
                         "/sys/kernel/gpu/gpu_clock", "/sys/class/kgsl/kgsl-3d0/gpuclk" };
        for (int i = 0; i < gpu.Length && gpuFreq == null; i++) gpuFreq = SysFile.Open(gpu[i]);
        // SoC temperature: a thermal zone whose type names the CPU / SoC (MediaTek "mtktscpu", "soc_max", "cpu-big"...)
        for (int z = 0; z < 48 && socTemp == null; z++)
        {
            try
            {
                string d = "/sys/class/thermal/thermal_zone" + z + "/";
                if (!File.Exists(d + "type")) continue;
                string ty = File.ReadAllText(d + "type").Trim().ToLowerInvariant();
                if (ty.Contains("cpu") || ty.Contains("soc") || ty.Contains("ap_ntc") || ty == "mtktscpu") socTemp = SysFile.Open(d + "temp");
            }
            catch (Exception) { }
        }
    }
    static long ParseLong(string s) { long v = 0; bool any = false; foreach (char ch in s) { if (ch >= '0' && ch <= '9') { v = v * 10 + (ch - '0'); any = true; } else if (any) break; } return any ? v : -1; }

    static void ReadSysfs()
    {
        if (cpuCur != null) { long v = cpuCur.Number(); CpuGHz = v > 0 ? v / 1e6f : float.NaN; }
        if (gpuFreq != null)
        {
            long v = gpuFreq.Number();
            GpuMHz = v <= 0 ? float.NaN : v > 100_000_000 ? v / 1e6f : v > 100_000 ? v / 1e3f : v;
        }
        if (socTemp != null) { long v = socTemp.Number(); SocC = v <= 0 ? float.NaN : v > 1000 ? v / 1000f : v; }
    }

    // ---------------------------------------------------------------- Android (raw JNI)
#if UNITY_ANDROID && !UNITY_EDITOR
    static AndroidJavaObject activity, power, filter;
    static IntPtr midStatus, midHeadroom, midRegister, midGetIntExtra, jTemperature;
    static readonly jvalue[] noArgs = new jvalue[0], headArgs = new jvalue[1], regArgs = new jvalue[2], extraArgs = new jvalue[2];

    static void Init()
    {
        try
        {
            int sdk;
            using (var v = new AndroidJavaClass("android.os.Build$VERSION")) sdk = v.GetStatic<int>("SDK_INT");
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer")) activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            if (activity == null) return;
            if (sdk >= 29)
            {
                power = activity.Call<AndroidJavaObject>("getSystemService", "power");
                if (power != null)
                {
                    IntPtr cls = power.GetRawClass();
                    midStatus = AndroidJNI.GetMethodID(cls, "getCurrentThermalStatus", "()I"); Clear(ref midStatus);
                    if (sdk >= 30) { midHeadroom = AndroidJNI.GetMethodID(cls, "getThermalHeadroom", "(I)F"); Clear(ref midHeadroom); }
                }
            }
            filter = new AndroidJavaObject("android.content.IntentFilter", "android.intent.action.BATTERY_CHANGED");
            midRegister = AndroidJNI.GetMethodID(activity.GetRawClass(), "registerReceiver", "(Landroid/content/BroadcastReceiver;Landroid/content/IntentFilter;)Landroid/content/Intent;");
            Clear(ref midRegister);
            IntPtr intentCls = AndroidJNI.FindClass("android/content/Intent");
            if (intentCls != IntPtr.Zero)
            {
                midGetIntExtra = AndroidJNI.GetMethodID(intentCls, "getIntExtra", "(Ljava/lang/String;I)I"); Clear(ref midGetIntExtra);
                AndroidJNI.DeleteLocalRef(intentCls);
            }
            else AndroidJNI.ExceptionClear();
            IntPtr s = AndroidJNI.NewStringUTF("temperature");
            jTemperature = AndroidJNI.NewGlobalRef(s); AndroidJNI.DeleteLocalRef(s);
            headArgs[0] = new jvalue { i = 10 };
            regArgs[0] = new jvalue { l = IntPtr.Zero }; regArgs[1] = new jvalue { l = filter.GetRawObject() };
            extraArgs[0] = new jvalue { l = jTemperature }; extraArgs[1] = new jvalue { i = -10000 };
        }
        catch (Exception) { midStatus = midHeadroom = midRegister = IntPtr.Zero; }
        try { InitSysfs(); } catch (Exception) { }
    }
    // a pending Java exception (method missing on this phone): clear it and forget the method
    static void Clear(ref IntPtr mid) { if (AndroidJNI.ExceptionOccurred() != IntPtr.Zero) { AndroidJNI.ExceptionClear(); mid = IntPtr.Zero; } }
    static bool Failed() { if (AndroidJNI.ExceptionOccurred() == IntPtr.Zero) return false; AndroidJNI.ExceptionClear(); return true; }

    static void Read()
    {
        ReadHz();
        try
        {
            if (midStatus != IntPtr.Zero)
            {
                int st = AndroidJNI.CallIntMethod(power.GetRawObject(), midStatus, noArgs);
                if (Failed()) { midStatus = IntPtr.Zero; Status = -1; } else Status = st;
            }
            if (midHeadroom != IntPtr.Zero)
            {
                float h = AndroidJNI.CallFloatMethod(power.GetRawObject(), midHeadroom, headArgs);
                if (Failed()) { midHeadroom = IntPtr.Zero; Headroom = float.NaN; } else if (!float.IsNaN(h)) Headroom = h; // NaN: asked too soon, keep the last
            }
            if (midRegister != IntPtr.Zero && midGetIntExtra != IntPtr.Zero)
            {
                IntPtr intent = AndroidJNI.CallObjectMethod(activity.GetRawObject(), midRegister, regArgs);
                if (Failed()) { midRegister = IntPtr.Zero; }
                else if (intent != IntPtr.Zero)
                {
                    int t = AndroidJNI.CallIntMethod(intent, midGetIntExtra, extraArgs);
                    if (Failed()) midGetIntExtra = IntPtr.Zero; else if (t > -10000) BatteryC = t * 0.1f;
                    AndroidJNI.DeleteLocalRef(intent);
                }
            }
        }
        catch (Exception) { midStatus = midHeadroom = midRegister = IntPtr.Zero; }
        ReadSysfs();
    }
#else
    static void Init() { }
    static void Read() { ReadHz(); }
#endif

    static void ReadHz()
    {
        try { double hz = Screen.currentResolution.refreshRateRatio.value; Hz = hz > 1 ? (float)hz : float.NaN; } catch (Exception) { }
    }
}
