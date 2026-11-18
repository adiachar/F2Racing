// DebugShots.cs — automatic in-game screenshots for the WebGL build (Tools/Shots/shoot.py drives it headless).
// Off unless the page URL has ?shot=1, and the URL is only read in a WebGL player build, so Android (and the editor)
// never enter it. Example:
//   index.html?shot=1&track=Monza&cars=6&quality=High&cam=chase,cockpit,tv&s=300,850,1450&w=1280&h=720&tod=afternoon
// Parameters (all optional except shot=1):
//   track    circuit name or id (Circuits.All, e.g. Monza, Spa)          default Monza
//   cars     rivals placed around the player (0-21)                       default 6
//   quality  Low | Medium | High | Auto                                   default High
//   pit      1 = the player's real car standing in its pit box, the pit crew mid-stop on it (pits=service time in s, default 1.0)
//   cam      list of chase | far | cockpit | halo | tv (one shot per s per cam)   default chase
//   s        list of lap distances in metres                              default the Monza spots (see README)
//   w, h     canvas size in CSS pixels (device pixel ratio forced to 1)   default 1280 x 720
//   tod      morning | afternoon | evening | night                        default afternoon
//   weather  dry | damp | wet                                             default dry
//   class    F1 | F2 | F3                                                 default F1
//   kmh      player speed shown (wheels / spray only: physics is frozen)  default 0
//   hud      1 = draw the race HUD                                        default 0 (clean frames)
//   settle   seconds to wait after each placement (real time)             default 1.0 (first shot + warm)
//   warm     extra seconds before the first shot (shaders, textures)      default 3
//   ack      1 = after SHOT_READY wait until the page sets window.__shotAck = i (the harness does this after its
//            screenshot); otherwise move on after `hold` seconds          default 0
//   hold     seconds each READY frame stays when ack=0                    default 1.5
// Progress is published through document.title (Assets/Plugins/WebGL/Shots.jslib) and the console:
//   SHOT_BOOT, SHOT_LOADING, SHOT_READY <i> <count> s=<m> cam=<name>, SHOT_DONE <count>, SHOT_ERROR <message>
// How it works: GameManager.Start calls ShotsBoot before the first circuit build, so the chosen circuit, time of
// day, weather, class and graphics level are set up front; the session starts as a quick race with `cars` rivals.
// The simulation is frozen for the whole run (shotFreeze is part of Paused): each spot places the player on the
// racing line at distance s, heading along the line, and spreads the rivals ahead and behind it; the camera and
// rendering keep running.
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    bool shotFreeze;          // shot mode: no physics steps (part of Paused)
    int shotCars = -1;        // shot mode: number of rivals (overrides the menu's count), -1 = normal play
    bool shotHud;             // shot mode: draw the HUD
    bool ShotsHideHud => shotFreeze && !shotHud;
    static readonly string[] ShotCamNames = { "chase", "far", "cockpit", "halo", "tv" }; // camMode 0-4

    // GameManager.Start, after the settings are loaded and before the first circuit is built
    void ShotsBoot()
    {
        var q = DebugShots.Query();
        if (q == null || DebugShots.Get(q, "shot", "0") != "1") return;
        var shots = gameObject.AddComponent<DebugShots>();
        shots.Init(this, q);
        string tr = DebugShots.Get(q, "track", "Monza").ToLowerInvariant();
        int map = -1;
        for (int i = 0; i < Circuits.All.Length; i++)
            if (Circuits.All[i].name.ToLowerInvariant() == tr || (Circuits.All[i].id ?? "").ToLowerInvariant() == tr) map = i;
        if (map < 0) { shots.Fail("unknown track " + tr); map = System.Array.FindIndex(Circuits.All, c => c.id == "monza"); if (map < 0) map = 0; }
        mapIdx = map; playground = false;
        shotCars = Mathf.Clamp(DebugShots.GetInt(q, "cars", 6), 0, StartGrid.Slots - 1);
        shotHud = DebugShots.Get(q, "hud", "0") == "1";
        string ql = DebugShots.Get(q, "quality", "High").ToLowerInvariant();
        int g = System.Array.FindIndex(GfxNames, n => n.ToLowerInvariant() == ql); gfxIdx = g >= 0 ? g : 3;
        string cl = DebugShots.Get(q, "class", "F1").ToUpperInvariant();
        classIdx = cl == "F3" ? 2 : cl == "F2" ? 1 : 0;
        string tod = DebugShots.Get(q, "tod", "afternoon").ToLowerInvariant();
        int t = System.Array.FindIndex(TimeOfDay.Names, n => n.ToLowerInvariant() == tod); TimeOfDay.Index = t >= 0 ? t : TimeOfDay.Default;
        string wx = DebugShots.Get(q, "weather", "dry").ToLowerInvariant();
        int w = System.Array.FindIndex(Weather.Names, n => n.ToLowerInvariant() == wx); Weather.Index = w >= 0 ? w : 0; Weather.PushGlobals();
        tyreSet = carSet = Weather.BestSet();
        camMode = ShotCam(shots.cams.Count > 0 ? shots.cams[0] : "chase");
        tilt = false; showLine = false;
        ui.mode = MenuUI.ModeQuick; ui.quali = false;
        shotFreeze = true;
    }

    internal static int ShotCam(string name) { int i = System.Array.IndexOf(ShotCamNames, name); return i >= 0 ? i : 0; }

    internal void ShotsStartSession() { ui.BeginLoading(false); }                      // a quick race: grid, rivals
    internal bool ShotsSessionReady => ui != null && !ui.Showing && world != null && track != null && line != null && car != null;
    internal string ShotsTrackInfo => track == null ? "" : $"{MapNames[mapIdx]} length {track.length:0} m, {rivals.Count} rivals";

    // put the player on the racing line at lap distance s (heading along the line), the rivals around it
    internal void ShotPlace(double s, int camIdx, double kmh)
    {
        PitShotClear(); // no pit stop carried over from the last shot (PitCrew.cs)
        int N = track.N;
        int i = ((int)System.Math.Round(s / track.ds) % N + N) % N;
        int a = (i + N - 1) % N, b = (i + 1) % N;
        double hw = track.width / 2 - 1.2;
        var p = track.pts[i];
        double lat = System.Math.Max(-hw, System.Math.Min(hw, line.off[i]));
        car.x = p.x + p.nx * lat; car.y = p.y + p.ny * lat;
        car.heading = System.Math.Atan2(line.y[b] - line.y[a], line.x[b] - line.x[a]);
        car.vx = kmh / 3.6; car.vy = 0; car.r = 0;
        px = car.x; py = car.y; ph = car.heading; pd = car.delta; psi = car.steerIn; acc = 0; // no in-between drawing
        idx = prevIdx = -1; offTime = wrongTime = 0;
        // rivals: a loose train ahead (seen from chase / cockpit), a few behind (seen from the TV cameras)
        for (int k = 0; k < rivals.Count; k++)
        {
            ShotRivalSlot(k, out double ahead, out double dl);
            int j = ((i + (int)System.Math.Round(ahead / track.ds)) % N + N) % N;
            double rl = System.Math.Max(-hw, System.Math.Min(hw, line.off[j] + dl));
            rivals[k].Place(j, rl, kmh / 3.6);
            rivals[k].car.gridHold = false;
        }
        camMode = camIdx;
        tvWasOn = true; camVel = Vector3.zero; tvCur = -1; tvCarIdx = -1; // chase: snap, no swoop; TV: fresh camera pick
        if (cam != null && camIdx != TvMode) cam.fieldOfView = camIdx == 2 ? fovCockpit : camIdx == 3 ? fovCockpit + 11 : fovChase;
    }

    // rival k: metres ahead of the player along the lap (negative = behind) and sideways offset from the racing line
    static readonly double[,] ShotSlots = { { 18, 2.6 }, { 34, -0.6 }, { -14, -2.4 }, { 55, 1.8 }, { 78, -1.6 }, { -32, 1.2 }, { 105, 2.2 }, { 135, -2.0 }, { 170, 0.6 }, { -55, -1.8 } };
    static void ShotRivalSlot(int k, out double ahead, out double lat)
    {
        if (k < ShotSlots.GetLength(0)) { ahead = ShotSlots[k, 0]; lat = ShotSlots[k, 1]; return; }
        int m = k - ShotSlots.GetLength(0);
        ahead = 210 + 40 * m; lat = m % 2 == 0 ? 1.5 : -1.5;
    }
}

public class DebugShots : MonoBehaviour
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void ShotsTitle(string s);
    [DllImport("__Internal")] static extern void ShotsCanvas(int w, int h);
    [DllImport("__Internal")] static extern int ShotsAck();
#else
    static void ShotsTitle(string s) { }
    static void ShotsCanvas(int w, int h) { }
    static int ShotsAck() => -1;
#endif

    GameManager gm;
    Dictionary<string, string> q;
    internal readonly List<string> cams = new List<string>();
    readonly List<double> spots = new List<double>();
    string error;

    // the page URL's query (WebGL player only; null everywhere else, so shot mode cannot start)
    internal static Dictionary<string, string> Query()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        string url = Application.absoluteURL;
        if (string.IsNullOrEmpty(url)) return null;
        int h = url.IndexOf('#'); if (h >= 0) url = url.Substring(0, h);
        int at = url.IndexOf('?'); if (at < 0) return null;
        var d = new Dictionary<string, string>();
        foreach (var part in url.Substring(at + 1).Split('&'))
        {
            if (part.Length == 0) continue;
            int e = part.IndexOf('=');
            string k = e < 0 ? part : part.Substring(0, e), v = e < 0 ? "1" : part.Substring(e + 1);
            try { k = System.Uri.UnescapeDataString(k.Replace('+', ' ')); v = System.Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { }
            d[k.Trim().ToLowerInvariant()] = v.Trim();
        }
        return d;
#else
        return null;
#endif
    }
    internal static string Get(Dictionary<string, string> q, string k, string def) => q != null && q.TryGetValue(k, out var v) && v.Length > 0 ? v : def;
    internal static int GetInt(Dictionary<string, string> q, string k, int def) => int.TryParse(Get(q, k, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;
    internal static float GetF(Dictionary<string, string> q, string k, float def) => float.TryParse(Get(q, k, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;
    static readonly char[] ListSep = { ',', '|', ';', ' ' };

    internal void Init(GameManager owner, Dictionary<string, string> query)
    {
        gm = owner; q = query;
        foreach (var c in Get(q, "cam", "chase").ToLowerInvariant().Split(ListSep, System.StringSplitOptions.RemoveEmptyEntries)) cams.Add(c == "tv" || c == "far" || c == "cockpit" || c == "halo" ? c : "chase");
        if (cams.Count == 0) cams.Add("chase");
        foreach (var s in Get(q, "s", "300,850,1450,2500,3920,5150").Split(ListSep, System.StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) spots.Add(v);
        if (spots.Count == 0) spots.Add(300);
        Title("SHOT_BOOT");
        PerfStats.FromQuery(q); // stats=1: render stats overlay + a PERF_STATS console line per shot (PerfStats.cs)
        ShotsCanvas(Mathf.Clamp(GetInt(q, "w", 1280), 64, 8192), Mathf.Clamp(GetInt(q, "h", 720), 64, 8192));
    }
    internal void Fail(string msg) { error = msg; Debug.LogWarning("[Shots] " + msg); }

    static void Title(string s) { Debug.Log("[Shots] " + s); ShotsTitle(s); }

    IEnumerator Start()
    {
        yield return null; // GameManager.Start has finished (camera, car, first circuit build)
        if (error != null) { Title("SHOT_ERROR " + error); yield break; }
        Title("SHOT_LOADING");
        gm.ShotsStartSession();
        float t0 = Time.realtimeSinceStartup;
        while (!gm.ShotsSessionReady)
        {
            if (Time.realtimeSinceStartup - t0 > 600f) { Title("SHOT_ERROR session did not start"); yield break; }
            yield return null;
        }
        Debug.Log("[Shots] session: " + gm.ShotsTrackInfo + ", screen " + Screen.width + "x" + Screen.height);
        float settle = Mathf.Max(0f, GetF(q, "settle", 1f)), warm = Mathf.Max(0f, GetF(q, "warm", 3f)), hold = Mathf.Max(0f, GetF(q, "hold", 1.5f));
        float ackTimeout = Mathf.Max(1f, GetF(q, "acktimeout", 300f)), kmh = Mathf.Max(0f, GetF(q, "kmh", 0f));
        bool ack = Get(q, "ack", "0") == "1";
        int count = spots.Count * cams.Count, n = 0;
        foreach (double s in spots)
            foreach (string cam in cams)
            {
                gm.ShotPlace(s, GameManager.ShotCam(cam), kmh);
                if (Get(q, "pit", "0") == "1") gm.ShotPit(GetF(q, "pits", 1.0f)); // pit=1: the player's car in its pit box, the crew frozen on it at service time pits (s; 0 = car stopped, 2.4 = release)
                // a few frames for the camera snap, the rival LODs, shadows and any texture / shader upload,
                // and some real time (SwiftShader draws only a few frames a second)
                float wait = settle + (n == 0 ? warm : 0f), ts = Time.realtimeSinceStartup;
                int f0 = Time.frameCount;
                while (Time.frameCount - f0 < 8 || Time.realtimeSinceStartup - ts < wait) yield return null;
                PerfStats.ShotLog($"s={s.ToString("0", CultureInfo.InvariantCulture)} cam={cam}");
                Title($"SHOT_READY {n} {count} s={s.ToString("0", CultureInfo.InvariantCulture)} cam={cam}");
                float tr = Time.realtimeSinceStartup;
                if (ack) { while (ShotsAck() < n && Time.realtimeSinceStartup - tr < ackTimeout) yield return null; }
                else { while (Time.realtimeSinceStartup - tr < hold) yield return null; }
                n++;
            }
        Title("SHOT_DONE " + count);
    }
}
