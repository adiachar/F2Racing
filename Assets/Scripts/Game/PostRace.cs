// PostRace.cs — the cinematic after the chequered flag (F1 25 / GT7 style) instead of the hard cut to the results table.
// Another part of GameManager (a partial class): the sequence is the nested PostRace class, the podium set PostPodium.
// Setting "Post-race" (Settings > GAME, saved as "postRace"): Full (default) / Short / Off (the old hard cut).
//  1. The flag (Full, Short): FinishRace settles the result exactly as before (rows, rival penalties, XP and safety
//     rating, championship points: ui.ShowResults + RaceFinished), then hands over to this. A white flash, then the
//     last ~1.5 s of the replay recording (up to 0.6 s past the line) played back at 0.42x: real slow motion with no
//     timeScale change (the poses come from the 30 Hz recording), seen from a low long-lens camera at the track edge just
//     past the line; the giant position (P1 WINNER / P3 PODIUM / P7 FINISHED) and the lower third. The race engineer's
//     result call comes from TeamRadio by itself. The simulation runs on for 0.6 s (still recorded, so the replay keeps
//     the line), then holds until the slow motion ends; nothing after that is recorded.
//  2. Cool-down lap (Full, 18.4 s): the player's car is driven at INPUT level (pure pursuit on the racing line, the
//     formation lap's autopilot, GameManagerFormation.cs) easing down from its flag speed (5.5 m/s^2, never faster than
//     90% of the racing line's speed) to 60% of the racing line, 150 km/h at most; the rivals get the formation caps
//     (AiDriver.formation / formCap: decision code only) with the same easing, so the whole field slows. Cameras cycle:
//     helicopter, side tracking, a front onboard looking back at the driver (he waves: DriverModel's right arm, posed
//     after the normal pose), a trackside TV camera. The classification slides in as a side panel, row by row.
//  3. Podium (Full 12.6 s with the player in the top 3, else 6.4 s from a TV angle; Short the same). Monza: the real
//     podium cantilevered over the track from the pit building (Trackside.cs builds it; its step positions are worked
//     out here from the same numbers), the crowd invading the track below it. Elsewhere: a podium set built here in front
//     of the garages, in the pit lane next to the line (or beside the track without a pit building): platform, three
//     steps with gold / silver / bronze bands, a backdrop with a red band and a chequer strip. The top three stand on
//     P2 / P1 / P3 (People rigs: the race driver body, team colours, the player's own look and yellow helmet), an
//     official hands the winner the cup, he lifts it (two-arm IK on the handles, pumps), confetti, champagne (two-hand
//     bottle grip, spray particles), the crew clapping, a crane push-in, a medium handover shot, a low hero shot, an
//     orbit and a high wide. Every car is hidden meanwhile. Fades to paper.
//  4. Progress reveal (MenuProgress.cs: ui.OpenResults(true)): XP count-up and line, level up, safety grade, points.
//  5. The results page as before (Replay / Menu / Race again or Next round).
// A tap anywhere skips to the next part (a dip to black, or the paper fade at the end of the podium).
// Physics untouched: the player's car gets CarInput only (the hook wraps car.Step's input), the rivals the existing
// formation caps; the hold is GameManager's normal paused step (no physics step at all). Tools/PhysicsCheck unchanged.
// Cost: everything is made when the sequence starts (texts, the pace table) or at the podium cut under black (podium
// meshes, 3 + 1 + 4..12 people, 4 particle systems, <= 296 particles) and destroyed at the end. Per frame: the camera,
// the rivals' caps, the poses; no allocation (all strings built once, IMGUI text widths measured once per screen size).
// Shot mode (DebugShots.cs): post=t1,t2,... (s from the flag; pos=<finishing place>, postmode=full|short): a made-up
// finish with the cars posed along the line as a function of t; past the podium the reveal page (t - its start), and
// from 30 s into the reveal the results table.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    // ---------------------------------------------------------------- setting
    static readonly string[] PostRaceNames = { "Full", "Short", "Off" };
    int postRaceIdx = -1;
    int PostRaceMode
    {
        get { if (postRaceIdx < 0) postRaceIdx = Mathf.Clamp(PlayerPrefs.GetInt("postRace", 0), 0, 2); return postRaceIdx; }
        set { postRaceIdx = Mathf.Clamp(value, 0, 2); PlayerPrefs.SetInt("postRace", postRaceIdx); }
    }

    PostRace post;
    Livery postLivery; bool postLiveryOk;
    bool postShotReady;

    bool PostFreeze => post != null && post.Freeze;          // GameManager.Update: no physics step (the hold)
    bool PostRecord => post == null || post.Recording;       // GameManager.Update: the replay keeps recording

    // FinishRace, first thing: the player's look before the championship is recorded (InSeasonRound may end with it)
    void PostCapture()
    {
        try { postLivery = InSeasonRound ? Livery.ForTeam(Season.Data.team, Season.TeamColour(Season.Data.team)) : Livery.Player[liveryIdx]; postLiveryOk = true; }
        catch (System.Exception) { postLiveryOk = false; }
    }

    // FinishRace, last thing (the results page is up): the sequence takes over and hides the page until it is done
    void PostBegin(MenuUI.Row[] rows, bool shot = false, int shotMode = 0)
    {
        PostAbort();
        int mode = shot ? shotMode : PostRaceMode;
        if (mode >= 2 || rows == null || rows.Length == 0 || track == null || line == null || car == null || model == null || cam == null || playground || raceLaps <= 0) return;
        try { post = new PostRace(this, rows, mode == 0, shot); ui.page = MenuUI.Page.None; }
        catch (System.Exception e) { Debug.LogWarning("PostRace: " + e.Message); PostAbort(); if (!shot) ui.OpenResults(false); }
    }
    void PostAbort() { var p = post; post = null; if (p != null) p.Finish(); }
    // the sequence is over: the progress reveal, then the results page
    void PostEnd() { PostAbort(); ui.OpenResults(true); }
    void PostFail(System.Exception e)
    {
        Debug.LogWarning("PostRace: " + e.Message);
        bool shot = post != null && post.Shot;
        PostAbort();
        if (!shot) ui.OpenResults(false);
    }

    // physics step (wraps the player's input): the autopilot of the cool-down lap
    CarInput PostInput(CarInput input)
    {
        if (post == null) return input;
        try { return post.PlayerStep(input); }
        catch (System.Exception e) { PostFail(e); return input; }
    }

    // LateUpdate: true = the sequence placed the camera
    bool PostCamera()
    {
        if (post == null) return false;
        if (!post.Shot && (ui.Showing || raceLaps <= 0 || pLap <= raceLaps)) { PostAbort(); return false; } // left the race, a restart
        try { return post != null && post.Frame(); }
        catch (System.Exception e) { PostFail(e); return false; }
    }

    // OnGUI before the HUD: true = the sequence drew the screen (no HUD)
    bool PostGui()
    {
        if (post == null) return false;
        try { return post.Gui(); }
        catch (System.Exception e) { Debug.LogWarning("PostRace GUI: " + e.Message); return true; }
    }

    // ---------------------------------------------------------------- shot mode (DebugShots.cs: post=t, pos=, postmode=)
    internal void ShotPost(float t, int pos, bool full)
    {
        if (track == null || line == null || car == null) return;
        if (!postShotReady)
        {
            postShotReady = true;
            int n = rivals.Count + 1; pos = Mathf.Clamp(pos, 1, n);
            var rows = new MenuUI.Row[n];
            rows[0] = new MenuUI.Row { name = "You", colour = new Color(1f, 0.25f, 0.2f), player = true, progress = raceLaps + 1 - (pos - 1) * 0.004, best = 83.123 };
            for (int i = 0; i < rivals.Count; i++)
            {
                var w = rivals[i].who; int rank = i < pos - 1 ? i : i + 1;
                rows[i + 1] = new MenuUI.Row { name = w.name, colour = new Color(w.r, w.g, w.b), progress = raceLaps + 1 - rank * 0.004, best = 83.4 + 0.11 * rank };
            }
            PostCapture();
            pLap = raceLaps + 1;
            PgRateRows(rows); // driver rating (DriverRating.cs; DebugShots dr= sets the rating before)
            ui.ShowResults(rows, MapNames[mapIdx], raceLaps, 600.0 * raceLaps / 6 + 61.234, 90, 0, 0);
            RaceFinished(rows);
            PostBegin(rows, true, full ? 0 : 1);
            postShotRows = rows;
        }
        float reveal = post != null ? post.RevealAt : postShotReveal;
        if (post != null) postShotReveal = reveal;
        if (t < reveal)
        {
            if (post == null && postShotRows != null) PostBegin(postShotRows, true, full ? 0 : 1);
            if (post != null) { ui.page = MenuUI.Page.None; post.Seek(t); }
        }
        else
        {
            PostAbort();
            ui.OpenResults(true);
            ui.RevealSeek(t - reveal);
        }
    }
    MenuUI.Row[] postShotRows; float postShotReveal = 30f;

    // =====================================================================================================================
    sealed class PostRace
    {
        // ---------------------------------------------------------------- timeline (s from the flag)
        const float SloFrom = -1.0f, SloRate = 0.42f, LiveFor = 0.6f;
        public const float SloLen = 3.6f, CoolShot = 4.6f, CoolLen = CoolShot * 4f, PodTop = 12.6f, PodOther = 6.4f, Dip = 0.3f, PaperFade = 0.45f;
        const double CoolMax = 150 / 3.6, CoolShare = 0.6, CoolDecel = 5.5, CoolBrake = 3.0, CoolAccel = 3.5;

        readonly GameManager g; readonly Track tk; readonly int N;
        public readonly bool Shot; readonly bool full;
        float t;                                  // the sequence clock
        readonly float tCool, tPod; float tEnd, podLen;
        int phase = -1;
        float skipDip = -1f, skipTo;              // a tap: black up to the next part, then on from there
        float tapGuard;                           // no skip right after a part starts
        // the result
        readonly int[] order;                     // finishing order: 0 = the player, k + 1 = rival k
        readonly int pos, cars; readonly bool top3;
        // recording (slow motion)
        readonly int flagHead, flagCount;
        // cool-down
        readonly double[] pace; readonly double[] vFlagR; double vFlag, simAtFlag;
        // texts (built once)
        readonly string sBig, sWord, sName, sSub, sEyeFlag, sEyeCool, sTitleCool, sEyePod, sTitlePod, sSubPod, sSkip, sPanel, sPanelSub;
        readonly string[] rowName, rowGap, rowPos; readonly Color[] rowCol; readonly bool[] rowMe; int rowsN;
        readonly string[] tagText = new string[3];
        float wKey = -1f, wLower, wBigWord, wSkip; readonly float[] wTag = new float[3];
        // camera
        Vector3 sloPos, sloLook, sloLookVel; float sloFov = 20f; int lastShot = -1; float heliAng;
        // the wave (the player's driver in the cockpit)
        CarModel waveFor; Transform armU, armF, armH;
        // the podium
        PostPodium pod; bool carsHidden;
        static Texture2D checker;

        public bool Freeze => phase >= 0 && !Shot && (full ? (t >= LiveFor && t < tCool) || t >= tPod : t >= LiveFor);
        public bool Recording => t < LiveFor;
        public float RevealAt => tEnd;

        public PostRace(GameManager owner, MenuUI.Row[] rows, bool fullMode, bool shot)
        {
            g = owner; Shot = shot; full = fullMode; tk = g.track; N = tk.N;
            // finishing order (the results page sorts the same way)
            int n = rows.Length; order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            System.Array.Sort(order, (a, b) => rows[b].progress.CompareTo(rows[a].progress));
            int p = 0; for (int i = 0; i < n; i++) if (order[i] == 0) { p = i; break; }
            pos = p + 1; cars = n; top3 = n > 1 && pos <= 3;
            tCool = SloLen; tPod = full ? SloLen + CoolLen : SloLen;
            podLen = People.Ready && n > 1 ? (top3 ? PodTop : PodOther) : 0f;
            tEnd = tPod + podLen;
            // the recording up to the flag
            flagHead = g.recHead; flagCount = g.recCount;
            simAtFlag = g.simTime;
            // the cool-down pace: a share of the racing line (wet: less), capped, gentle ramps either way
            pace = new double[N];
            var ln = g.line; bool ok = ln != null && ln.speed != null && ln.N == N;
            double wet = System.Math.Sqrt(Mathf.Clamp01((float)Weather.TrackFactor(g.carSet, 1)));
            for (int i = 0; i < N; i++) pace[i] = ok ? System.Math.Min(CoolMax, System.Math.Max(9, CoolShare * ln.speed[i] * wet)) : 30;
            double ds = tk.ds;
            for (int r = 0; r < 2; r++) for (int i = N - 1; i >= 0; i--) { int j = (i + 1) % N; pace[i] = System.Math.Min(pace[i], System.Math.Sqrt(pace[j] * pace[j] + 2 * CoolBrake * ds)); }
            for (int r = 0; r < 2; r++) for (int i = 0; i < N; i++) { int j = (i + 1) % N; pace[j] = System.Math.Min(pace[j], System.Math.Sqrt(pace[i] * pace[i] + 2 * CoolAccel * ds)); }
            var c = g.car; vFlag = System.Math.Sqrt(c.vx * c.vx + c.vy * c.vy);
            vFlagR = new double[g.rivals.Count];
            for (int i = 0; i < g.rivals.Count; i++)
            {
                var d = g.rivals[i]; var rc = d.car; vFlagR[i] = System.Math.Sqrt(rc.vx * rc.vx + rc.vy * rc.vy);
                d.formation = true; d.formCap = System.Math.Max(vFlagR[i], 10); d.formLatW = 0; d.formWeave = 0;
            }
            // texts
            MenuProfile.Load();
            string me = UiKit.Upper(string.IsNullOrEmpty(MenuProfile.Name) ? "Driver" : MenuProfile.Name);
            sBig = "P" + pos;
            sWord = cars < 2 ? "FINISHED" : pos == 1 ? "WINNER" : pos <= 3 ? "PODIUM" : pos <= 10 ? "IN THE POINTS" : "FINISHED";
            sName = me;
            string time = g.ui.ResultTime ?? "";
            sSub = (cars > 1 ? "P" + pos + " of " + cars : "Time trial") + (time.Length > 0 ? "  ·  " + time : "");
            sEyeFlag = UiKit.Caps("Chequered flag"); sEyeCool = UiKit.Caps(MapNames[g.mapIdx]); sTitleCool = "COOL-DOWN LAP";
            sEyePod = UiKit.Caps(MapNames[g.mapIdx] + "  ·  Podium");
            string winner = order[0] == 0 ? me : UiKit.Upper(rows[order[0]].name);
            sTitlePod = "WINNER  ·  " + winner; sSubPod = g.raceLaps + (g.raceLaps == 1 ? " lap" : " laps") + "  ·  " + cars + " cars";
            sSkip = "SKIP"; sPanel = "CLASSIFICATION"; sPanelSub = "FINAL";
            int m = Mathf.Min(g.ui.ResultCount, n);
            rowName = new string[m]; rowGap = new string[m]; rowPos = new string[m]; rowCol = new Color[m]; rowMe = new bool[m]; rowsN = m;
            for (int i = 0; i < m; i++)
            {
                var r = g.ui.ResultRow(i);
                rowName[i] = r.player ? me : UiKit.Upper(r.name); rowCol[i] = r.colour; rowMe[i] = r.player;
                string gp = g.ui.ResultGap(i); rowGap[i] = gp == null ? "" : i == 0 ? "WINNER" : gp;
                rowPos[i] = (i + 1).ToString();
            }
            for (int q = 0; q < 3; q++) tagText[q] = q < n ? (q + 1) + "  " + (order[q] == 0 ? me : UiKit.Upper(rows[order[q]].name)) : null;
            if (checker == null)
            {
                checker = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.HideAndDontSave };
                checker.SetPixels32(new[] { new Color32(255, 255, 255, 255), new Color32(17, 17, 17, 255), new Color32(17, 17, 17, 255), new Color32(255, 255, 255, 255) });
                checker.Apply(false, true);
            }
            phase = -1;
        }
        // ---------------------------------------------------------------- the end
        public void Finish()
        {
            try
            {
                for (int i = 0; i < g.rivals.Count; i++) { var d = g.rivals[i]; d.formation = false; d.formCap = double.MaxValue; d.formLatW = 0; d.formWeave = 0; }
                if (pod != null) { pod.Destroy(); pod = null; }
                ShowCars(true);
                var ae = g.audioEngine; if (ae != null) ae.engineVol = g.volEngine * 0.01f;
                g.tvWasOn = true; g.camVel = Vector3.zero; g.tvCur = -1; g.tvCarIdx = -1;
                if (g.cam != null) { g.cam.nearClipPlane = ChaseNear; g.cam.fieldOfView = g.camMode == 2 ? g.fovCockpit : g.camMode == 3 ? g.fovCockpit + 11f + 0.06f * g.haloBackPct : g.fovChase; }
            }
            catch (System.Exception e) { Debug.LogWarning("PostRace finish: " + e.Message); }
        }

        // ---------------------------------------------------------------- physics step: the cool-down autopilot
        public CarInput PlayerStep(CarInput input)
        {
            var c = g.car; c.boostOn = false; c.otOn = false; c.gridHold = false;
            if (g.idx < 0) return new CarInput(0, 0, 0.3);
            double ts = g.simTime - simAtFlag;
            int i = g.idx, ia = (i + 3) % N;
            double v = System.Math.Sqrt(c.vx * c.vx + c.vy * c.vy);
            double ramp = vFlag - CoolDecel * ts, safe = g.line.speed != null && g.line.N == N ? 0.9 * g.line.speed[ia] : 30;
            double cap = System.Math.Max(pace[ia], System.Math.Min(ramp, safe));
            return Autopilot(cap, v);
        }
        // pure pursuit on the racing line, pedals to the speed cap, gently (GameManagerFormation's autopilot)
        CarInput Autopilot(double cap, double v)
        {
            var c = g.car; int i = g.idx;
            double Ld = 5 + 0.22 * v; int ia = (i + System.Math.Max(1, (int)System.Math.Round(Ld / tk.ds))) % N;
            double room = tk.HalfWidth(ia) - 1.0;
            double off = System.Math.Max(-room, System.Math.Min(room, g.line.off[ia]));
            var pa = tk.pts[ia];
            double tx = pa.x + pa.nx * off - c.x, ty = pa.y + pa.ny * off - c.y;
            double ch = System.Math.Cos(c.heading), sh = System.Math.Sin(c.heading);
            double fx = ch * tx + sh * ty, fy = -sh * tx + ch * ty;
            double d2 = System.Math.Max(1.0, fx * fx + fy * fy);
            double delta = System.Math.Atan(c.cfg.wheelbase * 2 * fy / d2);
            double lockNow = c.lockAngle > 1e-4 ? c.lockAngle : c.cfg.maxLockDeg * System.Math.PI / 180;
            double steer = System.Math.Max(-1, System.Math.Min(1, -delta / lockNow));
            double err = cap - v;
            double thr = System.Math.Max(0, System.Math.Min(v < 12 ? 0.35 : 0.6, err * 0.35)), brk = System.Math.Max(0, System.Math.Min(1, -err * 0.45));
            if (brk > 0.02) thr = 0;
            return new CarInput(steer, thr, brk);
        }
        // every frame: the rivals' caps (their AI thinks at 60 Hz and keeps to them)
        void RivalCaps()
        {
            double ts = g.simTime - simAtFlag;
            for (int k = 0; k < g.rivals.Count && k < vFlagR.Length; k++)
            {
                var d = g.rivals[k]; int i = d.idx >= 0 ? d.idx : 0;
                d.formation = true; d.formLatW = 0; d.formWeave = 0;
                d.formCap = System.Math.Max(pace[(i + 3) % N], vFlagR[k] - CoolDecel * ts);
            }
        }

        // ---------------------------------------------------------------- the clock and the parts (LateUpdate)
        int PhaseAt(float tt) => tt < tCool ? 0 : tt < tPod ? 1 : 2;
        public void Seek(float tt) { t = Mathf.Max(0f, tt); skipDip = -1f; tapGuard = 2f; }

        public bool Frame()
        {
            if (g.model == null || g.cam == null || g.car == null) { g.PostAbort(); return false; }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (!Shot && !g.Paused)
            {
                if (skipDip >= 0f)
                {
                    skipDip += dt;
                    if (skipDip >= Dip) { t = skipTo; skipDip = -1f; }
                }
                else t += dt;
                tapGuard += dt;
            }
            int ph = PhaseAt(t);
            if (ph == 2 && podLen <= 0f) { if (Shot) { t = tEnd; return true; } g.PostEnd(); return false; }
            if (ph != phase) Enter(ph);
            if (pod == null && phase == 2) { if (Shot) return true; g.PostEnd(); return false; } // no podium could be built
            if (t >= tEnd && !Shot) { g.PostEnd(); return false; }
            Engine();
            if (phase <= 1) RivalCaps();
            if (phase == 0) SloFrame(dt);
            else if (phase == 1) CoolFrame(dt);
            else PodFrame();
            g.camVel = Vector3.zero;
            return true;
        }

        void Enter(int ph)
        {
            phase = ph; if (!Shot) tapGuard = 0f;
            if (ph == 0)
            {
                ShowCars(true);
                // a low camera at the track edge 20 m past the line, on the other side from the player's line
                Vector3 p = TrackAt(20, out _, out Vector3 n, out int i);
                float side = g.playerLat >= 0 ? -1f : 1f, edge = Mathf.Min((float)tk.HalfWidth(i) + 1.0f, (float)g.wallAt - 0.7f);
                sloPos = p + n * (side * edge) + Vector3.up * 0.9f;
                sloLook = g.model.transform.position; sloLookVel = Vector3.zero;
            }
            else if (ph == 1) { lastShot = -1; ShowCars(true); }
            else if (ph == 2)
            {
                ShowCars(false);
                if (pod == null)
                {
                    try { pod = new PostPodium(g, order, g.postLiveryOk ? g.postLivery : Livery.Player[g.liveryIdx], top3); if (!pod.Ok) { pod.Destroy(); pod = null; } }
                    catch (System.Exception e) { Debug.LogWarning("PostRace podium: " + e.Message); if (pod != null) pod.Destroy(); pod = null; }
                }
            }
        }

        // engine: down while the picture is held (its note would hang), back for the cool-down, off at the podium
        void Engine()
        {
            var ae = g.audioEngine; if (ae == null || Shot) return;
            float k = phase == 0 ? Mathf.Lerp(1f, 0.25f, Mathf.Clamp01(t / LiveFor)) : phase == 1 ? Mathf.Lerp(0.25f, 0.85f, Mathf.Clamp01((t - tCool) / 1.2f)) : 0f;
            ae.engineVol = g.volEngine * 0.01f * k;
        }

        void ShowCars(bool on)
        {
            if (carsHidden == !on) return;
            carsHidden = !on;
            if (g.model != null) g.model.gameObject.SetActive(on);
            for (int i = 0; i < g.rivals.Count && i < g.rivalModels.Count; i++)
            {
                bool near = g.rivalNear[i] || g.rivalFar[i] == null;
                if (g.rivalModels[i] != null) g.rivalModels[i].gameObject.SetActive(on && near);
                if (g.rivalFar[i] != null) g.rivalFar[i].gameObject.SetActive(on && !near);
            }
        }

        // ---------------------------------------------------------------- 1: the flag, in slow motion
        void SloFrame(float dt)
        {
            float rt = SloFrom + t * SloRate; // s from the flag (recording time)
            if (Shot) ShotPoseSlo(rt); else ShowRec(rt);
            Vector3 cp = g.model.transform.position + Vector3.up * 0.45f;
            if (Shot) sloLook = cp; else sloLook = Vector3.SmoothDamp(sloLook, cp, ref sloLookVel, 0.06f, Mathf.Infinity, Mathf.Max(dt * SloRate, 1e-4f));
            float dist = Mathf.Max(3f, (sloLook - sloPos).magnitude);
            float want = Mathf.Clamp(2f * Mathf.Atan(3.4f / dist) * Mathf.Rad2Deg, 9f, 58f);
            sloFov = Shot || t < 0.05f ? want : sloFov + (want - sloFov) * Mathf.Min(1f, dt * 6f);
            var rot = Quaternion.LookRotation(sloLook - sloPos, Vector3.up) * PrShake(0.08f, 0.6f, 5.3f, t);
            g.cam.transform.SetPositionAndRotation(sloPos, rot);
            g.cam.fieldOfView = Fit(sloFov); g.cam.nearClipPlane = ChaseNear;
            g.CockpitBits(false);
        }
        // the recorded poses at rt s from the flag (interpolated), drawn over the live ones
        void ShowRec(float rt)
        {
            int newer = (g.recHead - flagHead + RecCap) % RecCap;
            if (g.recCount < 2 || flagCount < 2) return;
            float lo = -(Mathf.Min(flagCount, RecCap - newer - 1) - 1), hi = newer;
            float f = Mathf.Clamp(rt * RecHz, lo, Mathf.Max(lo, hi - 0.001f));
            int i0 = Mathf.FloorToInt(f); float a = f - i0;
            int k0 = ((flagHead - 1 + i0) % RecCap + RecCap * 2) % RecCap, k1 = (k0 + 1) % RecCap;
            Show(g.model, g.recMe[k0], g.recMe[k1], a);
            for (int i = 0; i < g.rivals.Count && i < g.recRiv.Length; i++)
            {
                var m = g.rivalNear[i] || g.rivalFar[i] == null ? g.rivalModels[i] : g.rivalFar[i];
                if (m != null) Show(m, g.recRiv[i][k0], g.recRiv[i][k1], a);
            }
        }
        static void Show(CarModel m, RecPose p, RecPose q, float a)
        {
            double dh = System.Math.Atan2(System.Math.Sin(q.h - p.h), System.Math.Cos(q.h - p.h));
            m.Apply(p.x + (q.x - p.x) * a, p.y + (q.y - p.y) * a, p.h + dh * a, p.d + (q.d - p.d) * a, p.sf, p.sr, p.lon + (q.lon - p.lon) * a, p.lat + (q.lat - p.lat) * a);
            m.SetBrake(p.brake);
        }

        // ---------------------------------------------------------------- 2: the cool-down lap
        void CoolFrame(float dt)
        {
            float c = t - tCool;
            if (Shot) ShotPoseCool(c);
            int shot = Mathf.Clamp((int)(c / CoolShot), 0, 3);
            bool fresh = shot != lastShot; lastShot = shot;
            float sc = c - shot * CoolShot;
            var tr = g.model.transform; Vector3 cp = tr.position, f = PrFlat(tr.forward), r = Vector3.Cross(Vector3.up, f);
            Vector3 pos, look; float fov;
            if (shot == 3)
            {
                // a trackside TV camera (the race's own set)
                if (fresh) { g.tvCur = -1; g.tvCarIdx = -1; }
                g.TvCamera(tr, dt);
                g.CockpitBits(false); g.cam.nearClipPlane = ChaseNear;
                Wave(0f);
                return;
            }
            if (shot == 0)
            {
                // helicopter: high, circling slowly behind and beside the car
                if (fresh) heliAng = Mathf.Atan2(-f.x, -f.z) + 0.7f;
                float a = heliAng + sc * 0.11f;
                pos = cp + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * 38f + Vector3.up * 30f;
                pos = PrAbove(pos, 14f);
                look = cp + f * 10f; fov = 30f;
            }
            else if (shot == 1)
            {
                // side tracking shot, low, over the road (the side away from the car's edge of the track)
                float side = g.playerLat >= 0 ? -1f : 1f;
                pos = cp + r * (side * 5.2f) - f * (1.4f - 0.5f * sc / CoolShot) + Vector3.up * 1.05f;
                look = cp + Vector3.up * 0.45f + f * 0.9f; fov = 42f;
            }
            else
            {
                // front onboard looking back at the driver, who waves
                pos = tr.TransformPoint(new Vector3(0.62f, 1.02f, 1.10f));
                look = tr.TransformPoint(new Vector3(0.05f, 0.80f, 0.16f)); fov = 50f;
            }
            var rot = Quaternion.LookRotation(look - pos, Vector3.up) * PrShake(shot == 0 ? 0.25f : 0.12f, shot == 0 ? 0.35f : 0.9f, 7.1f + shot, c);
            g.cam.transform.SetPositionAndRotation(pos, rot);
            g.cam.fieldOfView = Fit(fov); g.cam.nearClipPlane = shot == 2 ? 0.08f : ChaseNear;
            g.CockpitBits(false);
            float w = shot == 2 ? PrSmooth((sc - 0.5f) / 0.5f) * PrSmooth((CoolShot - 0.3f - sc) / 0.5f) : shot == 1 ? PrSmooth((sc - 2.4f) / 0.5f) : 0f;
            Wave(w);
        }

        // the driver's right arm up out of the cockpit, the hand waving (after DriverModel's own pose this frame)
        void Wave(float w)
        {
            if (w <= 0.001f) return;
            if (waveFor != g.model)
            {
                waveFor = g.model; armU = armF = armH = null;
                var dm = g.model.GetComponentInChildren<DriverModel>(true);
                if (dm != null) { armU = dm.transform.Find("UpperArmR"); armF = dm.transform.Find("ForearmR"); armH = dm.transform.Find("HandR"); }
            }
            if (armU == null || armF == null || armH == null) return;
            var tr = g.model.transform;
            Vector3 s = armU.position, e = armF.position, h = armH.position;
            float l1 = (e - s).magnitude, l2 = (h - e).magnitude; if (l1 < 1e-4f || l2 < 1e-4f) return;
            float ph = Mathf.Sin(t * 2f * Mathf.PI * 1.6f);
            Vector3 want1 = (tr.right * 0.55f + tr.up * 0.82f + tr.forward * 0.12f).normalized;
            Vector3 want2 = (tr.up * 0.92f + tr.right * (0.12f + 0.38f * ph) + tr.forward * 0.1f).normalized;
            Vector3 d1 = Vector3.Slerp((e - s) / l1, want1, w), d2 = Vector3.Slerp((h - e) / l2, want2, w);
            Vector3 e2 = s + d1 * l1, h2 = e2 + d2 * l2;
            Quaternion q1 = Quaternion.FromToRotation(e - s, d1 * l1), q2 = Quaternion.FromToRotation(h - e, d2 * l2);
            armU.rotation = q1 * armU.rotation;
            armF.SetPositionAndRotation(e2, q2 * armF.rotation);
            armH.SetPositionAndRotation(h2, q2 * armH.rotation);
        }

        // ---------------------------------------------------------------- 3: the podium
        void PodFrame()
        {
            float u = t - tPod;
            if (pod == null) return;
            pod.Frame(u, g.cam, Shot);
            g.cam.nearClipPlane = 0.1f;
        }

        // ---------------------------------------------------------------- shot mode: the cars posed along the line
        void ShotPoseSlo(float rt)
        {
            double along = rt * 78.0;
            PoseCars(along, 78.0, 34.0);
        }
        void ShotPoseCool(float c)
        {
            double along = 60.0 + 42.0 * c;
            PoseCars(along, 42.0, 40.0);
        }
        // the player at along (m from the line), the others before / behind by finishing rank, gap m apart
        void PoseCars(double along, double v, double gap)
        {
            for (int k = 0; k < order.Length; k++)
            {
                int who = order[k]; double a = along - (k - (pos - 1)) * gap;
                int i = Idx(a); double lat = g.line.off[i] + (who == 0 ? 0 : 0.6 * System.Math.Sin(k * 2.1));
                double room = tk.HalfWidth(i) - 1.0; lat = System.Math.Max(-room, System.Math.Min(room, lat));
                int ia = (i + N - 1) % N, ib = (i + 1) % N;
                double head = System.Math.Atan2(g.line.y[ib] - g.line.y[ia], g.line.x[ib] - g.line.x[ia]);
                if (who == 0)
                {
                    var c = g.car; var p = tk.pts[i];
                    c.x = p.x + p.nx * lat; c.y = p.y + p.ny * lat; c.heading = head; c.vx = v; c.vy = 0; c.r = 0; c.delta = 0;
                    g.idx = i; g.playerLat = lat;
                    g.SnapDrawn();
                }
                else if (who - 1 < g.rivals.Count)
                {
                    var d = g.rivals[who - 1]; d.Place(i, lat, v); d.car.heading = head;
                    var m = g.rivalNear[who - 1] || g.rivalFar[who - 1] == null ? g.rivalModels[who - 1] : g.rivalFar[who - 1];
                    if (m != null) m.Apply(d.car.x, d.car.y, head, 0, 0, 0, 0, 0);
                }
            }
        }
        int Idx(double along) { int i = (int)System.Math.Round(along / tk.ds); return ((i % N) + N) % N; }

        // ---------------------------------------------------------------- overlays (OnGUI)
        public bool Gui()
        {
            var et = Event.current.type;
            g.HudInit(); Measure();
            float W = Screen.width, Hs = Screen.height;
            float lb = UiKit.U(18f) * PrSmooth(t / 0.4f);
            if (et == EventType.Repaint && phase >= 0)
            {
                if (phase < 2 || pod != null) { UiKit.Rect(new Rect(0, 0, W, lb), Color.black); UiKit.Rect(new Rect(0, Hs - lb, W, lb), Color.black); }
                if (phase == 0) GuiFlag();
                else if (phase == 1) GuiCool(lb);
                else GuiPodium(lb);
                // the skip hint, bottom right in the letterbox
                if (!Shot || g.shotHud)
                {
                    float a = Mathf.Clamp01((tapGuard - 0.4f) / 0.4f) * (phase == 2 ? Mathf.Clamp01((tEnd - PaperFade - t) / 0.3f) : 1f);
                    if (a > 0.01f)
                    {
                        UiKit.PushAlpha(a * 0.85f);
                        float h = UiKit.U(13f), w = wSkip + UiKit.U(22f);
                        var r = new Rect(UiKit.Safe.xMax - UiKit.U(14f) - w, Hs - lb * 0.5f - h * 0.5f, w, h);
                        UiKit.Outline(r, new Color(1f, 1f, 1f, 0.5f), 999f, 0.7f);
                        TF(new Rect(r.x, r.y, r.width - UiKit.U(8f), r.height), sSkip, UiKit.Bold, 6.4f, Color.white, TextAnchor.MiddleCenter);
                        UiKit.Icon(new Rect(r.xMax - UiKit.U(13f), r.center.y - UiKit.U(3.5f), UiKit.U(7f), UiKit.U(7f)), UiKit.IconNext, Color.white);
                        UiKit.PopAlpha();
                    }
                }
                // dips to black between the parts, a skip, the paper fade at the end
                float dip = 0f;
                if (full) dip = Mathf.Max(dip, 1f - Mathf.Abs(t - tCool) / Dip);
                dip = Mathf.Max(dip, 1f - Mathf.Abs(t - tPod) / Dip);
                if (skipDip >= 0f) dip = Mathf.Max(dip, skipDip / Dip);
                if (dip > 0.003f) UiKit.Rect(new Rect(0, 0, W, Hs), new Color(0f, 0f, 0f, Mathf.Clamp01(dip)));
                if (phase == 2 && pod != null)
                {
                    float pf = Mathf.Clamp01((t - (tEnd - PaperFade)) / PaperFade);
                    if (pf > 0.003f) UiKit.Rect(new Rect(0, 0, W, Hs), UiKit.A(UiKit.Paper, pf));
                }
            }
            // a tap anywhere: on to the next part
            if (!Shot && et == EventType.MouseDown && Event.current.button == 0 && tapGuard > 0.5f && skipDip < 0f)
            {
                Event.current.Use(); Skip();
            }
            return true;
        }
        void Skip()
        {
            if (phase < 2) { skipTo = tPod; skipDip = 0f; }
            else if (t < tEnd - PaperFade) t = tEnd - PaperFade; // straight into the paper fade
        }

        // the flash, the giant position, the lower third
        void GuiFlag()
        {
            float W = Screen.width, Hs = Screen.height;
            if (t < 0.4f) UiKit.Rect(new Rect(0, 0, W, Hs), new Color(1f, 1f, 1f, 0.8f * (1f - t / 0.4f)));
            // the giant position, centre: pops in, holds, fades
            float a = PrSmooth((t - 0.45f) / 0.25f) * PrSmooth((3.1f - t) / 0.45f);
            if (a > 0.01f)
            {
                float k = UiKit.EaseOut((t - 0.45f) / 0.45f), sc = 1.35f - 0.35f * k;
                var m = GUI.matrix;
                var pv = new Vector2(W * 0.5f, Hs * 0.45f);
                GUIUtility.ScaleAroundPivot(new Vector2(sc, sc), pv);
                UiKit.PushAlpha(a);
                float bh = UiKit.U(96f);
                var big = new Rect(0, pv.y - bh * 0.62f, W, bh);
                UiKit.Text(new Rect(big.x + UiKit.U(2.5f), big.y + UiKit.U(2.5f), big.width, big.height), sBig, UiKit.HeavyItalic, 92f, new Color(0f, 0f, 0f, 0.45f), TextAnchor.MiddleCenter);
                UiKit.Text(big, sBig, UiKit.HeavyItalic, 92f, pos == 1 ? UiKit.Red : Color.white, TextAnchor.MiddleCenter);
                // the word on a red / dark block under it
                float ww = wBigWord + UiKit.U(30f), wh = UiKit.U(24f);
                var wr = new Rect(Mathf.Round(W * 0.5f - ww * 0.5f), big.yMax - UiKit.U(4f), ww, wh);
                UiKit.Rect(wr, pos <= 3 && cars > 1 ? UiKit.Red : new Color(0.04f, 0.045f, 0.06f, 0.9f));
                UiKit.Text(wr, sWord, UiKit.Black, 13f, Color.white, TextAnchor.MiddleCenter);
                UiKit.PopAlpha();
                GUI.matrix = m;
            }
            Lower(sEyeFlag, sName, sSub, PrSmooth((t - 0.8f) / 0.3f), true);
        }

        // the lower third (eyebrow, title bar, sub bar) bottom left; flag: a chequer strip on the title bar
        void Lower(string eye, string title, string sub, float a, bool flag)
        {
            if (a <= 0.01f) return;
            var safe = UiKit.Safe;
            float lb = UiKit.U(18f);
            float pad = UiKit.U(10), h1 = UiKit.U(27), h2 = UiKit.U(17), eh = UiKit.U(13), gap = UiKit.U(3);
            float w = wLower + 2f * pad + UiKit.U(4);
            float x = safe.x + UiKit.U(18) - UiKit.U(24) * (1f - a), y0 = safe.yMax - lb - UiKit.U(16) - (eh + gap + h1 + h2);
            UiKit.PushAlpha(a);
            UiKit.Rect(new Rect(x, y0 + UiKit.U(2), UiKit.U(3), eh - UiKit.U(4)), UiKit.AccentHi);
            UiKit.TextShadow(new Rect(x + UiKit.U(7), y0, w, eh), eye, UiKit.Bold, 6.6f, new Color(1f, 1f, 1f, 0.92f));
            float y1 = y0 + eh + gap;
            UiKit.Rect(new Rect(x, y1, w, h1), new Color(0.035f, 0.04f, 0.055f, 0.9f));
            if (flag && checker != null)
            {
                float cs = UiKit.U(4.5f), cw = UiKit.U(40f);
                var cr = new Rect(x + w - cw, y1, cw, cs * 2f);
                GUI.DrawTextureWithTexCoords(cr, checker, new Rect(0, 0, cw / cs * 0.5f, 1f));
            }
            UiKit.Rect(new Rect(x, y1, UiKit.U(3), h1 + h2), UiKit.Accent);
            UiKit.Text(new Rect(x + pad, y1, w - pad, h1), title, UiKit.Black, 12.5f, Color.white);
            UiKit.Rect(new Rect(x, y1 + h1, w, h2), new Color(0.13f, 0.14f, 0.17f, 0.88f));
            UiKit.Text(new Rect(x + pad, y1 + h1, w - pad, h2), sub, UiKit.Semi, 7.6f, new Color(0.86f, 0.88f, 0.92f, 1f));
            UiKit.PopAlpha();
        }

        // the cool-down: the lower third, then the classification sliding in on the right, row by row
        void GuiCool(float lb)
        {
            float c = t - tCool;
            Lower(sEyeCool, sTitleCool, sSub, PrSmooth(c / 0.4f) * PrSmooth((4.2f - c) / 0.4f), false);
            if (rowsN == 0) return;
            var safe = UiKit.Safe;
            float e = UiKit.EaseOut((c - 1.0f) / 0.55f); if (e <= 0.001f) return;
            float pw = UiKit.U(178f), x = safe.xMax - UiKit.U(12f) - pw + (1f - e) * (pw + UiKit.U(24f));
            float y = safe.y + lb + UiKit.U(8f), hh = UiKit.U(22f), rh = UiKit.U(14.5f);
            int fit = Mathf.Max(3, (int)((Screen.height - lb - UiKit.U(10f) - (y + hh + UiKit.U(2f))) / rh));
            int me = 0; for (int i = 0; i < rowsN; i++) if (rowMe[i]) { me = i; break; }
            int show = Mathf.Min(rowsN, fit); bool meShown = me < show || show >= rowsN;
            int list = meShown ? show : show - 1;
            UiKit.PushAlpha(e);
            // header: red block + glass
            float tw = UiKit.U(26f);
            g.RR(new Rect(x, y, tw, hh), HudAccent, 6, 0, 0, 0);
            UiKit.Icon(new Rect(x + tw * 0.5f - UiKit.U(5f), y + hh * 0.5f - UiKit.U(5f), UiKit.U(10f), UiKit.U(10f)), UiKit.IconNext, Color.white);
            g.RR(new Rect(x + tw, y, pw - tw, hh), HudPanel, 0, 6, 0, 0);
            TF(new Rect(x + tw + UiKit.U(8f), y, pw, hh), sPanel, UiKit.Black, 7.6f, Color.white);
            TF(new Rect(x, y, pw - UiKit.U(8f), hh), sPanelSub, UiKit.Bold, 6.4f, HudGrey, TextAnchor.MiddleRight);
            float ry = y + hh;
            for (int k = 0; k < list + (meShown ? 0 : 1); k++)
            {
                int i = k < list ? k : me;
                float re = UiKit.EaseOut((c - 1.25f - k * 0.07f) / 0.35f); if (re <= 0.001f) break;
                float rx = x + (1f - re) * UiKit.U(40f);
                if (k == list && !meShown) ry += UiKit.U(4f);
                var row = new Rect(rx, ry, pw, rh);
                UiKit.PushAlpha(re);
                UiKit.Rect(row, rowMe[i] ? new Color(0.45f, 0.02f, 0f, 0.86f) : (k & 1) == 0 ? new Color(0.03f, 0.035f, 0.05f, 0.84f) : new Color(0.06f, 0.065f, 0.08f, 0.84f));
                Color pc = i == 0 ? UiKit.Gold : i == 1 ? UiKit.Silver : i == 2 ? UiKit.Bronze : Color.white;
                TF(new Rect(rx, ry, UiKit.U(20f), rh), rowPos[i], UiKit.Black, 7.2f, pc, TextAnchor.MiddleCenter);
                UiKit.Rect(new Rect(rx + UiKit.U(22f), ry + rh * 0.22f, Mathf.Max(2f, UiKit.U(2f)), rh * 0.56f), rowCol[i]);
                TF(new Rect(rx + UiKit.U(28f), ry, pw * 0.62f, rh), rowName[i], rowMe[i] ? UiKit.Black : UiKit.Semi, 6.9f, Color.white);
                TF(new Rect(rx, ry, pw - UiKit.U(8f), rh), rowGap[i], UiKit.Semi, 6.6f, rowMe[i] ? Color.white : HudGrey, TextAnchor.MiddleRight);
                UiKit.PopAlpha();
                ry += rh;
            }
            UiKit.PopAlpha();
        }

        // the podium: the lower third and the names over the drivers in the wide shots
        void GuiPodium(float lb)
        {
            if (pod == null) return;
            float u = t - tPod;
            Lower(sEyePod, sTitlePod, sSubPod, PrSmooth((u - 0.6f) / 0.4f) * PrSmooth((pod.TagsUntil - 0.4f - u) / 0.4f), false);
            float ta = pod.TagAlpha(u); if (ta <= 0.01f) return;
            UiKit.PushAlpha(ta);
            for (int q = 0; q < 3; q++)
            {
                if (tagText[q] == null || !pod.Head(q, out Vector3 hp)) continue;
                Vector3 sp = g.cam.WorldToScreenPoint(hp + Vector3.up * 0.42f);
                if (sp.z <= 0.5f) continue;
                float w = wTag[q] + UiKit.U(14f), h = UiKit.U(14f);
                var r = new Rect(Mathf.Round(sp.x - w * 0.5f), Mathf.Round(Screen.height - sp.y - h), w, h);
                UiKit.Rect(r, new Color(0.03f, 0.035f, 0.05f, 0.82f), 3f);
                UiKit.Rect(new Rect(r.x, r.y, UiKit.U(3f), r.height), q == 0 ? UiKit.Gold : q == 1 ? UiKit.Silver : UiKit.Bronze);
                TF(new Rect(r.x + UiKit.U(3f), r.y, r.width - UiKit.U(3f), r.height), tagText[q], UiKit.Bold, 6.8f, Color.white, TextAnchor.MiddleCenter);
            }
            UiKit.PopAlpha();
        }

        // text widths, once per screen size (measuring allocates)
        void Measure()
        {
            if (wKey == Screen.height) return;
            wKey = Screen.height;
            wLower = Mathf.Max(Mathf.Max(UiKit.Width(sName, UiKit.Black, 12.5f), UiKit.Width(sSub, UiKit.Semi, 7.6f)),
                     Mathf.Max(Mathf.Max(UiKit.Width(sTitleCool, UiKit.Black, 12.5f), UiKit.Width(sTitlePod, UiKit.Black, 12.5f)), UiKit.Width(sSubPod, UiKit.Semi, 7.6f)));
            wLower = Mathf.Max(wLower, UiKit.U(150f));
            wBigWord = UiKit.Width(sWord, UiKit.Black, 13f);
            wSkip = UiKit.Width(sSkip, UiKit.Bold, 6.4f);
            for (int q = 0; q < 3; q++) wTag[q] = tagText[q] != null ? UiKit.Width(tagText[q], UiKit.Bold, 6.8f) : 0f;
        }

        // ---------------------------------------------------------------- helpers
        Vector3 TrackAt(double s, out Vector3 tangent, out Vector3 normal, out int idx)
        {
            double fi = s / tk.ds;
            int i0 = (int)System.Math.Floor(fi); float a = (float)(fi - i0);
            i0 = ((i0 % N) + N) % N; int i1 = (i0 + 1) % N;
            var p = tk.pts[i0]; var q = tk.pts[i1];
            float x = (float)(p.x + (q.x - p.x) * a), z = (float)(p.y + (q.y - p.y) * a);
            tangent = PrFlat(new Vector3((float)(p.tx + (q.tx - p.tx) * a), 0f, (float)(p.ty + (q.ty - p.ty) * a)));
            normal = PrFlat(new Vector3((float)(p.nx + (q.nx - p.nx) * a), 0f, (float)(p.ny + (q.ny - p.ny) * a)));
            idx = a < 0.5f ? i0 : i1;
            return new Vector3(x, WorldBuilder.H(x, z), z);
        }
        float Fit(float fov)
        {
            float asp = Mathf.Max(0.5f, g.cam.aspect);
            return asp > 16f / 9f ? 2f * Mathf.Rad2Deg * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * (16f / 9f) / asp) : fov;
        }
    }

    // shared by the sequence and the podium
    static Quaternion PrShake(float amp, float hz, float seed, float time)
    {
        float tt = time * hz;
        return Quaternion.Euler((Mathf.PerlinNoise(tt, seed) - 0.5f) * 2f * amp, (Mathf.PerlinNoise(seed + 13.1f, tt * 0.9f) - 0.5f) * 2.4f * amp, (Mathf.PerlinNoise(tt * 0.7f, seed + 31.7f) - 0.5f) * 1.2f * amp);
    }
    static Vector3 PrAbove(Vector3 p, float clear) { if (WorldBuilder.Height != null) p.y = Mathf.Max(p.y, WorldBuilder.H(p.x, p.z) + clear); return p; }
    static Vector3 PrFlat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward; }
    static float PrSmooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
}

// =========================================================================================================================
// The podium: where it stands, the set (if the circuit has none), the people, the cups, bottles, confetti and champagne,
// the cameras. Made at the cut to the podium (under black), destroyed with the sequence.
public partial class GameManager
{
    sealed class PostPodium
    {
        public bool Ok;
        public float TagsUntil;
        readonly GameManager g;
        readonly GameObject root;
        readonly bool top3, monza;
        readonly int count;
        Vector3 O, F, R;                                      // P1's standing point, the way the drivers face, their right
        readonly Vector3[] spot = new Vector3[3];             // standing points P1, P2, P3
        float baseY;                                          // the floor the official stands on
        readonly People.Rig[] drv = new People.Rig[3];
        readonly float[] drvYaw = new float[3];
        People.Rig official; Vector3 offSpot, offSide; float offYaw0, offYaw1;
        People.Rig[] crowd; float[] crowdPhase; Vector3[] crowdAt;
        People.Blobs blobs;
        readonly Transform[] cup = new Transform[3], bottle = new Transform[3];
        ParticleSystem confetti; readonly ParticleSystem[] spray = new ParticleSystem[3];
        readonly List<Object> owned = new List<Object>();
        readonly People.PoseBuf bufA = new People.PoseBuf(), bufB = new People.PoseBuf();
        bool confettiOn; readonly bool[] sprayOn = new bool[3]; bool bottlesShown;
        // timing (s from the cut to the podium)
        readonly float hand0, hand1, lift0, lift1, liftOthers, champ, wide2, len, burst;
        static readonly Color HelmetCol = new Color(1f, 0.82f, 0.18f); // the player's helmet (as the pre-race driver)

        public PostPodium(GameManager owner, int[] order, Livery playerLiv, bool playerTop3)
        {
            g = owner; top3 = playerTop3;
            count = Mathf.Min(3, order.Length);
            if (top3) { hand0 = 3.15f; hand1 = 3.8f; lift0 = 4.4f; lift1 = 5.0f; liftOthers = 5.4f; champ = 7.2f; wide2 = 10.6f; len = PostRace.PodTop; burst = 5.05f; TagsUntil = 3.0f; }
            else { hand0 = hand1 = -1f; lift0 = 1.6f; lift1 = 2.2f; liftOthers = 2.6f; champ = 99f; wide2 = 99f; len = PostRace.PodOther; burst = 2.25f; TagsUntil = len - 0.4f; }
            root = new GameObject("PostPodium");
            monza = Place();
            if (!monza) BuildSet();
            int lvl = WorldBuilder.Gfx;

            // materials (the car paint: lit by the cars' sun, sky reflections; one per colour)
            var gold = Paint(new Color(1f, 0.76f, 0.30f), 0.55f, 0.92f, 1f);
            var silver = Paint(new Color(0.86f, 0.88f, 0.92f), 0.55f, 0.92f, 1f);
            var bronze = Paint(new Color(0.80f, 0.48f, 0.26f), 0.55f, 0.9f, 1f);
            var lacquer = Paint(new Color(0.03f, 0.03f, 0.035f), 1f, 0.9f, 0f);
            var glass = Paint(new Color(0.04f, 0.14f, 0.07f), 1f, 0.95f, 0.15f);
            Material[] metal = { gold, silver, bronze };

            // the cups (one mesh: base + cup and handles) and the bottles (glass + foil)
            var cupMesh = TrophyMesh(); var bottleMesh = BottleMesh();
            for (int q = 0; q < count; q++)
            {
                cup[q] = Part("Cup" + q, cupMesh, new[] { lacquer, metal[q] }).transform;
                cup[q].localScale = Vector3.one * (q == 0 ? 1f : 0.82f);
                bottle[q] = Part("Bottle" + q, bottleMesh, new[] { glass, gold }).transform;
                bottle[q].gameObject.SetActive(false);
            }

            // the drivers: the race-suited body in team colours; the player's own look
            int body = People.DriverBody >= 0 ? People.DriverBody : 0;
            for (int q = 0; q < count; q++)
            {
                int who = order[q];
                Livery liv; Color helm, helmTop;
                if (who == 0) { liv = playerLiv; helm = HelmetCol; helmTop = liv.primary; }
                else
                {
                    var w = g.rivals[who - 1].who;
                    liv = Livery.ForTeam(TeamIndex(w), new Color(w.r, w.g, w.b)); helm = liv.secondary; helmTop = liv.primary;
                }
                var r = new People.Rig(root.transform, body, People.GCap, "PodiumDriver" + q);
                r.SetLod(0); r.stance = People.DriverStance; DressDriver(r, liv, helm, helmTop, 31 + who * 7);
                drvYaw[q] = Yaw(F); r.Place(spot[q], drvYaw[q]);
                drv[q] = r;
            }
            // the official with the winner's cup: in front of the steps, between P1 and P3
            offSide = PrFlat(count > 2 ? spot[2] - spot[0] : -R);
            offSpot = new Vector3(O.x, baseY, O.z) + F * 1.05f + offSide * 1.0f;
            offSpot.y = baseY;
            int ob = People.CrewBodyOf(4711, 0.3f);
            official = new People.Rig(root.transform, ob, People.GBare, "PodiumOfficial") { scale = People.HeightOf(4711, ob) };
            official.Dress(People.OEngineer, new Livery("Official", new Color(0.09f, 0.11f, 0.17f), new Color(0.92f, 0.92f, 0.94f), new Color(0.8f, 0.65f, 0.2f)), 4711);
            offYaw0 = Yaw(PrFlat(O - offSpot)); offYaw1 = Yaw(PrFlat(F - offSide * 0.4f));
            official.Place(offSpot, offYaw0);
            // the crowd: team crews clapping (on the track below the Monza podium, in front of the set elsewhere)
            int nc = lvl >= 3 ? 12 : lvl == 2 ? 8 : 4;
            crowd = new People.Rig[nc]; crowdPhase = new float[nc]; crowdAt = new Vector3[nc];
            for (int k = 0; k < nc; k++)
            {
                int seed = 900 + k * 37; int cb = People.CrewBodyOf(seed, 0.3f);
                var c = new People.Rig(root.transform, cb, (k % 3) == 0 ? People.GHeadset : People.GCap, "PodiumCrowd" + k) { scale = People.HeightOf(seed, cb) };
                c.Dress(k % 2 == 0 ? People.OMechanic : People.OEngineer, Livery.Teams[(k * 5 + 3) % Livery.Teams.Length], seed);
                Vector3 at = CrowdSpot(k, nc);
                crowdAt[k] = at; crowdPhase[k] = Hash(k, 3) * 4f;
                c.Place(at, Yaw(PrFlat(new Vector3(O.x, at.y, O.z) - at)));
                c.SetLodAt(30f);
                crowd[k] = c;
            }
            // contact shadows under everybody
            blobs = new People.Blobs(root.transform, count + 1 + nc, "PodiumShadows");
            for (int q = 0; q < count; q++) blobs.Set(q, spot[q], F, 0.34f, 0.8f, 0.55f);
            blobs.Set(count, offSpot, F, 0.34f, 0.8f, 0.5f);
            for (int k = 0; k < nc; k++) blobs.Set(count + 1 + k, crowdAt[k], F, 0.34f, 0.8f, 0.45f);
            blobs.Flush();
            // confetti and champagne (<= 296 particles in all)
            MakeParticles();
            Ok = count > 0;
        }

        // ---------------------------------------------------------------- where
        // Monza: the podium Trackside.cs builds on the pit building (28 m past the line, steps over the track edge, the
        // slab 12.9 + 0.45 m up: the same numbers as there). Else a set in the pit lane in front of the garages, or on the
        // straight past the line without a pit building.
        bool Place()
        {
            var tk = g.track; int N = tk.N; float ds = (float)tk.ds, hw = (float)tk.width * 0.5f;
            var lane = Trackside.Lane;
            bool hasLane = lane != null && lane.track == tk && lane.sB > lane.sA + 12;
            if (tk.def != null && tk.def.id == "monza" && WorldBuilder.Gfx >= 2 && hasLane && 28.0 >= lane.sA && 28.0 <= lane.sB)
            {
                int ip = ((Mathf.RoundToInt(28f / ds) % N) + N) % N;
                var p = tk.pts[ip];
                Vector3 T = new Vector3((float)p.tx, 0f, (float)p.ty).normalized, D = new Vector3((float)p.nx, 0f, (float)p.ny).normalized * lane.side;
                Vector3 c = new Vector3((float)p.x, 0f, (float)p.y) + D * hw;
                float[] stepH = { 0.9f, 0.6f, 0.45f }, stepAt = { 0f, -1.9f, 1.9f };
                for (int q = 0; q < 3; q++) { var s = c + T * stepAt[q]; s.y = WorldBuilder.H(s.x, s.z) + 13.35f + stepH[q]; spot[q] = s; }
                F = -D; R = Vector3.Cross(Vector3.up, F); O = spot[0];
                baseY = WorldBuilder.H(c.x, c.z) + 13.35f;
                return true;
            }
            Vector3 centre, Dn;
            if (hasLane)
            {
                double u = System.Math.Max(lane.sA + 8, System.Math.Min(lane.sB - 8, 30.0));
                double lat = lane.side * (lane.laneLat - 3.25 + 6.8); // the middle of the working lane, before the garages
                lane.Pos(u, lat, out double x, out double y);
                var p = tk.pts[lane.TrackIdx(u)];
                Dn = new Vector3((float)p.nx, 0f, (float)p.ny).normalized * lane.side;
                centre = new Vector3((float)x, 0f, (float)y);
            }
            else
            {
                int i = ((Mathf.RoundToInt(45f / ds) % N) + N) % N; var p = tk.pts[i];
                Dn = new Vector3((float)p.nx, 0f, (float)p.ny).normalized;
                centre = new Vector3((float)p.x, 0f, (float)p.y) + Dn * Mathf.Min(1.5f, hw - 3f);
            }
            centre.y = WorldBuilder.H(centre.x, centre.z);
            F = -Dn; R = Vector3.Cross(Vector3.up, F);
            setC = centre; baseY = centre.y + SetPlat;
            float[] h = { 1.0f, 0.7f, 0.5f }, side = { 0f, 1f, -1f }; // P2 on the winner's right
            for (int q = 0; q < 3; q++) spot[q] = centre - F * 0.5f + R * (side[q] * 1.9f) + Vector3.up * (SetPlat + h[q]);
            O = spot[0];
            return false;
        }
        Vector3 setC; const float SetPlat = 0.22f;

        Vector3 CrowdSpot(int k, int n)
        {
            float a = (k + 0.5f) / n - 0.5f; // -0.5 .. 0.5 across
            if (monza)
            {
                // on the track under the podium, looking up
                Vector3 c = new Vector3(O.x, 0f, O.z) + F * (5.5f + 3.5f * Hash(k, 1)) + R * (a * 14f + (Hash(k, 2) - 0.5f) * 1.5f);
                c.y = WorldBuilder.H(c.x, c.z);
                return c;
            }
            Vector3 s = new Vector3(setC.x, 0f, setC.z) + F * (3.4f + 2.6f * Hash(k, 1)) + R * (a * 7.5f + (Hash(k, 2) - 0.5f) * 0.8f);
            s.y = WorldBuilder.H(s.x, s.z);
            return s;
        }

        // ---------------------------------------------------------------- the set (no podium on this circuit)
        void BuildSet()
        {
            var white = Paint(new Color(0.93f, 0.93f, 0.94f), 0.35f, 0.5f, 0f);
            var ink = Paint(new Color(0.07f, 0.07f, 0.08f), 0.5f, 0.6f, 0f);
            var red = Paint(new Color(0.88f, 0.03f, 0.01f), 0.6f, 0.7f, 0f);
            var gold = Paint(new Color(1f, 0.76f, 0.30f), 0.5f, 0.85f, 1f);
            var silver = Paint(new Color(0.86f, 0.88f, 0.92f), 0.5f, 0.85f, 1f);
            var bronze = Paint(new Color(0.80f, 0.48f, 0.26f), 0.5f, 0.85f, 1f);
            var mb = new MeshB(6); // 0 white, 1 ink, 2 red, 3 gold, 4 silver, 5 bronze
            Vector3 c = setC, up = Vector3.up;
            mb.Box(1, c + up * (SetPlat * 0.5f), R, up, F, 3.6f, SetPlat * 0.5f, 1.7f);                  // platform
            mb.Box(0, c + F * 1.69f + up * (SetPlat - 0.02f), R, up, F, 3.6f, 0.025f, 0.025f);           // its white front edge
            float[] h = { 1.0f, 0.7f, 0.5f }, side = { 0f, 1f, -1f };
            for (int q = 0; q < 3; q++)
            {
                Vector3 sc = c - F * 0.5f + R * (side[q] * 1.9f) + up * (SetPlat + h[q] * 0.5f);
                mb.Box(0, sc, R, up, F, 0.9f, h[q] * 0.5f, 0.8f);                                       // the step
                mb.Box(3 + q, sc + F * 0.805f + up * (h[q] * 0.5f - 0.13f), R, up, F, 0.9f, 0.065f, 0.012f); // its gold / silver / bronze band
            }
            // the backdrop: an ink wall, a red band, a chequer strip over it
            Vector3 b = c - F * 1.85f;
            mb.Box(1, b + up * 2.15f, R, up, F, 4.3f, 2.15f, 0.1f);
            mb.Box(2, b + F * 0.11f + up * 3.3f, R, up, F, 4.3f, 0.17f, 0.012f);
            mb.Box(0, b + F * 0.11f + up * 3.03f, R, up, F, 4.3f, 0.025f, 0.012f);
            const float sq = 0.25f;
            for (int row = 0; row < 2; row++)
                for (int k = 0; k < 34; k++)
                {
                    if (((k + row) & 1) != 0) continue;
                    Vector3 q0 = b + F * 0.115f + R * (-4.25f + k * sq + sq * 0.5f) + up * (3.62f + row * sq + sq * 0.5f);
                    mb.Quad(0, q0, R * (sq * 0.5f), up * (sq * 0.5f), F);
                }
            var mesh = mb.Make("PodiumSet"); owned.Add(mesh);
            Part("PodiumSet", mesh, new[] { white, ink, red, gold, silver, bronze }).transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        // ---------------------------------------------------------------- every frame (u: s from the cut)
        public void Frame(float u, Camera cam, bool shot)
        {
            // camera first (the drivers look at it)
            Shot(u, out Vector3 cpos, out Vector3 clook, out float fov);
            cpos = PrAbove(cpos, 0.4f);
            cam.transform.SetPositionAndRotation(cpos, Quaternion.LookRotation(clook - cpos, Vector3.up) * PrShake(0.1f, 0.5f, 3.3f, u));
            float asp = Mathf.Max(0.5f, cam.aspect);
            cam.fieldOfView = asp > 16f / 9f ? 2f * Mathf.Rad2Deg * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * (16f / 9f) / asp) : fov;
            bool close = top3 && u >= hand0 && u < champ;
            int frame = Time.frameCount;
            bool champagne = u >= champ;
            if (champagne != bottlesShown) { bottlesShown = champagne; for (int q = 0; q < count; q++) if (bottle[q] != null) bottle[q].gameObject.SetActive(champagne); }

            // the drivers
            for (int q = 0; q < count; q++)
            {
                var r = drv[q]; if (r == null) continue;
                r.SetLodAt((spot[q] - cpos).magnitude);
                bool waving = q == 0 && top3 && u < hand0;
                People.Sample(People.MIdle, u + q * 1.3f, bufA);
                if (q == 0 && top3)
                {
                    People.Sample(People.MWave, u, bufB);
                    float wv = PrSmooth((hand0 - 0.15f - u) / 0.35f) * PrSmooth((u - 0.2f) / 0.4f);
                    People.Blend(bufA, bufB, wv, bufA);
                }
                r.Apply(bufA);
                Vector3 fwd = r.tr.forward, right = r.tr.right, up = Vector3.up;
                Vector3 chest = r.b[People.JSpine3].position, head = r.b[People.JHead].position;
                float s = cup[q] != null ? cup[q].localScale.x : 1f;
                // where the cup is: chest, lifted, at the feet (champagne), or (the winner, before the handover) the official's
                Vector3 atChest = new Vector3(chest.x, chest.y - 0.12f - 0.455f * s, chest.z) + fwd * 0.30f;
                Vector3 lifted = new Vector3(head.x, head.y + (q == 0 ? 0.42f : 0.22f) - 0.455f * s, head.z) + fwd * 0.06f;
                float lt = q == 0 ? PrSmooth((u - lift0) / (lift1 - lift0)) : PrSmooth((u - liftOthers) / 0.6f);
                if (q == 0) lt *= 1f - PrSmooth((u - (champ - 0.2f)) / 0.2f); else lt *= 1f - PrSmooth((u - (champ - 0.6f)) / 0.4f);
                float pump = q == 0 && u > lift1 && u < champ ? 0.05f * Mathf.Max(0f, Mathf.Sin((u - lift1) * 5.2f)) : 0f;
                Vector3 hold = Vector3.Lerp(atChest, lifted, lt) + up * pump;
                Quaternion rot = Quaternion.Euler(0f, drvYaw[q], 0f);
                float wArms = waving ? 0f : 1f;
                if (q == 0 && top3)
                {
                    // the handover
                    Vector3 oc = OfficialHold(out Quaternion orot);
                    float k = PrSmooth((u - hand0 - 0.15f) / 0.5f);
                    hold = Vector3.Lerp(oc, hold, k) + up * (0.12f * Mathf.Sin(k * Mathf.PI));
                    rot = Quaternion.Slerp(orot, rot, k);
                    wArms = PrSmooth((u - hand0) / 0.4f);
                }
                if (champagne)
                {
                    // the cup at his feet, the bottle in both hands, spraying
                    if (cup[q] != null) cup[q].SetPositionAndRotation(new Vector3(spot[q].x, spot[q].y, spot[q].z) + fwd * 0.42f + right * (q == 2 ? 0.3f : -0.3f), rot);
                    Vector3 aim = Aim(q, u, fwd, right);
                    var bt = bottle[q];
                    if (bt != null)
                    {
                        bt.SetPositionAndRotation(chest - up * 0.18f + fwd * 0.16f, Quaternion.FromToRotation(Vector3.up, aim));
                        Vector3 pole = -up * 0.6f;
                        r.Arm(true, bt.TransformPoint(new Vector3(0f, 0.11f, 0f)) - right * 0.035f, 1f, -right + pole);
                        r.Arm(false, bt.TransformPoint(new Vector3(0f, 0.235f, 0f)) + right * 0.02f, 1f, right + pole);
                    }
                }
                else if (cup[q] != null)
                {
                    cup[q].SetPositionAndRotation(hold, rot);
                    if (wArms > 0.001f)
                    {
                        Vector3 hl = cup[q].TransformPoint(new Vector3(-0.24f, 0.455f, 0f)), hr = cup[q].TransformPoint(new Vector3(0.24f, 0.455f, 0f));
                        r.Arm(true, hl, wArms, -right - up * 0.5f);
                        r.Arm(false, hr, wArms, right - up * 0.5f);
                    }
                }
                // the head: the camera in the close shots, the crowd otherwise
                r.LookAt(close ? cpos : spot[q] + F * 10f + up * (monza ? -3f : 0.5f), close ? 0.55f : 0.35f);
            }
            // the official: holds the cup until the handover, then turns to the crowd and claps
            if (official != null)
            {
                float turn = top3 ? PrSmooth((u - hand1 - 0.1f) / 0.6f) : 1f;
                official.Place(offSpot, Mathf.LerpAngle(offYaw0, offYaw1, turn));
                official.SetLodAt((offSpot - cpos).magnitude);
                People.Sample(People.MIdle, u, bufA);
                People.Sample(People.MClap, u, bufB);
                People.Blend(bufA, bufB, top3 ? PrSmooth((u - hand1 - 0.6f) / 0.4f) : 1f, bufA);
                official.Apply(bufA);
                if (top3 && u < hand1 + 0.1f && cup[0] != null)
                {
                    float w = 1f - PrSmooth((u - hand0 - 0.25f) / 0.45f);
                    if (w > 0.001f)
                    {
                        Vector3 right = official.tr.right;
                        official.Arm(true, cup[0].TransformPoint(new Vector3(-0.2f, 0.25f, 0f)), w, -right - Vector3.up * 0.5f);
                        official.Arm(false, cup[0].TransformPoint(new Vector3(0.2f, 0.25f, 0f)), w, right - Vector3.up * 0.5f);
                    }
                }
                official.LookAt(O + Vector3.up * 0.6f, 0.6f);
            }
            // the crowd
            for (int k = 0; k < crowd.Length; k++)
            {
                var c = crowd[k]; if (c == null) continue;
                float d = (crowdAt[k] - cpos).magnitude;
                c.SetLodAt(d);
                if (!People.AnimDue(d, frame, k) && !shot) continue;
                People.Sample(People.MClap, u + crowdPhase[k], bufA);
                c.Apply(bufA);
                c.LookAt(O + Vector3.up * 0.8f, 0.7f);
            }
            // confetti when the winner's cup goes up, champagne
            Particles(u, shot);
        }

        // the winner's cup in the official's hands (before the handover)
        Vector3 OfficialHold(out Quaternion rot)
        {
            rot = Quaternion.Euler(0f, offYaw0, 0f);
            Vector3 fwd = rot * Vector3.forward;
            return offSpot + Vector3.up * (official != null ? 1.02f * official.scale : 1.0f) + fwd * 0.34f;
        }

        // where a bottle points: the winner at the crowd (sweeping), the others at the winner; shaken
        Vector3 Aim(int q, float u, Vector3 fwd, Vector3 right)
        {
            float sw = Mathf.Sin(u * 2.3f + q * 1.7f), shake = Mathf.Sin(u * 21f + q) * 0.05f;
            Vector3 a;
            if (q == 0) a = fwd + right * (0.45f * sw) + Vector3.up * 0.75f;
            else { Vector3 to = PrFlat(spot[0] - spot[q]); a = fwd * 0.45f + to * 0.85f + Vector3.up * (0.6f + 0.1f * sw); }
            a += right * shake + Vector3.up * shake;
            return a.normalized;
        }

        // ---------------------------------------------------------------- cameras
        void Shot(float u, out Vector3 pos, out Vector3 look, out float fov)
        {
            Vector3 up = Vector3.up;
            if (!top3)
            {
                // a TV camera across from the podium, long lens, drifting slowly
                pos = O + F * 24f + up * (monza ? 2f : 8f) + R * (7f - 0.5f * u);
                look = O + up * 0.7f - F * 0.3f;
                fov = Mathf.Lerp(19f, 15.5f, u / len);
                return;
            }
            if (u < hand0)
            {
                // a crane pushing in on the three of them
                float k = UiKit.EaseInOut(u / hand0);
                pos = Vector3.Lerp(O + F * 11.5f + up * 4f + R * 2.4f, O + F * 7.8f + up * 2.6f + R * 1.2f, k);
                look = O + up * 0.6f - F * 0.4f; fov = 36f;
            }
            else if (u < lift0)
            {
                // medium on the winner and the official
                Vector3 a = drv[0] != null ? drv[0].b[People.JSpine3].position : O + up * 1.3f;
                Vector3 b = official != null ? official.b[People.JSpine3].position : a;
                Vector3 mid = (a + b) * 0.5f;
                pos = mid + F * 3.7f - offSide * 1.3f + up * (monza ? 0.9f : 0.4f);
                look = mid + up * 0.1f; fov = 36f;
            }
            else if (u < champ)
            {
                // low hero shot of the cup going up, a slow arc
                float k = (u - lift0) / (champ - lift0), ang = Mathf.Lerp(-16f, 14f, k);
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * F;
                pos = O + dir * (monza ? 1.35f : 2.6f) + up * (monza ? -0.35f : -0.3f);
                Vector3 head = drv[0] != null ? drv[0].b[People.JHead].position : O + up * 1.7f;
                look = head + up * 0.25f; fov = monza ? 52f : 44f;
            }
            else if (u < wide2)
            {
                // the champagne: an orbit round the podium
                float k = (u - champ) / (wide2 - champ), ang = Mathf.Lerp(-38f, 38f, UiKit.EaseInOut(k));
                pos = O + (Quaternion.Euler(0f, ang, 0f) * F) * 7.2f + up * 1.9f;
                look = O + up * 0.7f - F * 0.2f; fov = 44f;
            }
            else
            {
                // the high wide to end on
                float k = (u - wide2) / Mathf.Max(0.1f, len - wide2);
                pos = O + F * (12.5f + 2.5f * k) + up * (4.6f + 1.2f * k) - R * 3.2f;
                look = O + up * 0.5f; fov = 34f;
            }
        }

        public float TagAlpha(float u)
        {
            if (!top3) return PrSmooth((u - 0.6f) / 0.4f) * PrSmooth((len - 0.8f - u) / 0.4f);
            return Mathf.Max(PrSmooth((u - 0.6f) / 0.4f) * PrSmooth((hand0 - 0.2f - u) / 0.3f), PrSmooth((u - wide2 - 0.3f) / 0.4f) * PrSmooth((len - 0.6f - u) / 0.3f));
        }
        public bool Head(int q, out Vector3 p)
        {
            if (q < count && drv[q] != null) { p = drv[q].b[People.JHead].position; return true; }
            p = Vector3.zero; return false;
        }

        // ---------------------------------------------------------------- particles
        void MakeParticles()
        {
            var sh = Resources.Load<Shader>("WeatherFx");
            if (sh == null || !sh.isSupported) sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (sh == null || !sh.isSupported) return;
            var sqTex = Tex(8, false); var softTex = Tex(32, true);
            var mConf = new Material(sh) { mainTexture = sqTex, name = "Confetti" }; owned.Add(mConf);
            var mSpray = new Material(sh) { mainTexture = softTex, name = "Champagne" }; owned.Add(mSpray);
            foreach (var m in new[] { mConf, mSpray })
            {
                if (m.HasProperty("_Fade")) m.SetVector("_Fade", new Vector4(0.25f, 1f / 0.5f, 0f, 0f)); // only the camera fade
                if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            }
            // confetti: one burst over the podium
            var go = new GameObject("Confetti"); go.transform.SetParent(root.transform, false);
            go.transform.SetPositionAndRotation(O + Vector3.up * 3.2f + F * 1.2f, Quaternion.LookRotation(Vector3.up, F));
            confetti = go.AddComponent<ParticleSystem>();
            confetti.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = confetti.main;
            main.loop = false; main.playOnAwake = false; main.duration = 1f; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4.5f, 6.5f); main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 8.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.09f); main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = 0.16f; main.maxParticles = 200;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.82f, 0.25f), 0f), new GradientColorKey(new Color(0.95f, 0.1f, 0.08f), 0.33f), new GradientColorKey(Color.white, 0.66f), new GradientColorKey(new Color(1f, 0.82f, 0.25f), 1f) },
                         new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            main.startColor = new ParticleSystem.MinMaxGradient(grad) { mode = ParticleSystemGradientMode.RandomColor };
            var em = confetti.emission; em.enabled = true; em.rateOverTime = 0f; em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)200) });
            var shp = confetti.shape; shp.enabled = true; shp.shapeType = ParticleSystemShapeType.Cone; shp.angle = 38f; shp.radius = 2.2f;
            var rol = confetti.rotationOverLifetime; rol.enabled = true; rol.z = new ParticleSystem.MinMaxCurve(-6f, 6f);
            var lim = confetti.limitVelocityOverLifetime; lim.enabled = true; lim.limit = 1.3f; lim.dampen = 0.12f;
            var col = confetti.colorOverLifetime; col.enabled = true;
            var fade = new Gradient(); fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(fade);
            var pr = go.GetComponent<ParticleSystemRenderer>(); pr.sharedMaterial = mConf; pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; pr.receiveShadows = false;
            // champagne: a stream out of each bottle while it sprays
            if (!top3) return;
            for (int q = 0; q < count; q++)
            {
                if (bottle[q] == null) continue;
                var sg = new GameObject("Champagne" + q); sg.transform.SetParent(bottle[q], false);
                sg.transform.localPosition = new Vector3(0f, 0.33f, 0f); sg.transform.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
                var ps = sg.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var m = ps.main;
                m.loop = true; m.playOnAwake = false; m.duration = 1f; m.simulationSpace = ParticleSystemSimulationSpace.World;
                m.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.0f); m.startSpeed = new ParticleSystem.MinMaxCurve(6f, 8.5f);
                m.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.08f); m.gravityModifier = 0.7f; m.maxParticles = 32;
                m.startColor = new Color(1f, 0.96f, 0.84f, 0.75f);
                var e = ps.emission; e.enabled = true; e.rateOverTime = 32f;
                var s = ps.shape; s.enabled = true; s.shapeType = ParticleSystemShapeType.Cone; s.angle = 7f; s.radius = 0.01f;
                var sz = ps.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 3.5f));
                var c = ps.colorOverLifetime; c.enabled = true;
                var gf = new Gradient(); gf.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
                c.color = new ParticleSystem.MinMaxGradient(gf);
                var r = sg.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = mSpray; r.renderMode = ParticleSystemRenderMode.Billboard;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
                spray[q] = ps;
            }
        }
        void Particles(float u, bool shot)
        {
            if (confetti != null)
            {
                if (shot) { if (u >= burst) confetti.Simulate(u - burst, true, true); else confetti.Clear(true); }
                else if (!confettiOn && u >= burst) { confettiOn = true; confetti.Play(); }
            }
            float s0 = champ + 0.45f, s1 = len - 0.3f;
            for (int q = 0; q < count; q++)
            {
                var ps = spray[q]; if (ps == null) continue;
                float st = s0 + q * 0.25f;
                bool on = u >= st && u < s1;
                if (shot) { if (on) ps.Simulate(Mathf.Min(u - st, 1.5f), true, true); else ps.Clear(true); continue; }
                if (on != sprayOn[q]) { sprayOn[q] = on; if (on) ps.Play(); else ps.Stop(true, ParticleSystemStopBehavior.StopEmitting); }
            }
        }
        // white alpha textures: a square (confetti) or a soft blob (spray)
        Texture2D Tex(int n, bool soft)
        {
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float a = 1f;
                if (soft) { float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f, r2 = u * u + v * v; a = r2 < 1f ? (1f - r2) * (1f - r2) : 0f; }
                px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
            }
            var t = new Texture2D(n, n, TextureFormat.RGBA32, soft) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            t.SetPixels32(px); t.Apply(soft, true);
            owned.Add(t);
            return t;
        }

        // ---------------------------------------------------------------- the end
        public void Destroy()
        {
            if (root != null) Object.Destroy(root); // the people, the set, the cups, the particle systems
            foreach (var o in owned) if (o != null) Object.Destroy(o);
            owned.Clear();
        }

        // ---------------------------------------------------------------- building blocks
        static Shader paintSh; static bool paintTried;
        Material Paint(Color c, float coat, float gloss, float metal)
        {
            if (!paintTried) { paintTried = true; paintSh = Resources.Load<Shader>("CarPaint"); if (paintSh != null && !paintSh.isSupported) paintSh = null; }
            Material m;
            if (paintSh != null) { m = new Material(paintSh) { color = c }; m.SetFloat("_Coat", coat); m.SetFloat("_Gloss", gloss); m.SetFloat("_Metal", metal); }
            else m = WorldBuilder.Mat(c, null, gloss * 0.8f, metal);
            m.name = "Podium"; owned.Add(m);
            return m;
        }
        GameObject Part(string name, Mesh mesh, Material[] mats)
        {
            var go = new GameObject(name); go.transform.SetParent(root.transform, false);
            go.layer = LightBake.CarLayer; // lit by the cars' sun, like the people
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = mats;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return go;
        }
        static float Yaw(Vector3 f) => Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        static float Hash(int a, int b) { float x = Mathf.Sin(a * 12.9898f + b * 78.233f) * 43758.5453f; return x - Mathf.Floor(x); }

        // a race suit in the livery, dark gloves and boots, the helmet (worn on the podium as the model has it) in its colours
        static void DressDriver(People.Rig r, Livery l, Color helm, Color helmTop, int seed)
        {
            var pal = new Vector4[People.NReg];
            Color prim = l.primary, sec = l.secondary, dark = new Color(0.05f, 0.05f, 0.06f);
            Color trim = Mathf.Abs(Lum(sec) - Lum(prim)) < 0.12f ? (Lum(prim) > 0.5f ? dark : new Color(0.9f, 0.9f, 0.91f)) : sec;
            for (int i = 0; i < People.NReg; i++) P(pal, i, prim, 0.15f);
            P(pal, People.RSide, trim, 0.15f); P(pal, People.RYoke, trim, 0.15f); P(pal, People.RStripe, trim, 0.15f);
            P(pal, People.RGlove, Color.Lerp(trim, dark, 0.35f), 0.2f); P(pal, People.RShin, prim * 0.85f, 0.15f); P(pal, People.RKnee, prim, 0.15f);
            P(pal, People.RBoot, dark, 0.35f); P(pal, People.RBelt, dark, 0.3f); P(pal, People.RNeck, new Color(0.08f, 0.08f, 0.09f), 0.1f);
            Color skin = seed % 3 == 0 ? new Color(0.66f, 0.48f, 0.35f) : seed % 3 == 1 ? new Color(0.86f, 0.68f, 0.56f) : new Color(0.78f, 0.58f, 0.45f);
            P(pal, People.RFace, skin, 0.18f); P(pal, People.RHair, new Color(0.20f, 0.13f, 0.08f), 0.1f);
            P(pal, People.RHelmet, helm, 0.8f); P(pal, People.RHelmetTop, helmTop, 0.8f); P(pal, People.RVisor, new Color(0.02f, 0.025f, 0.04f), 1f);
            P(pal, People.RCap, prim, 0.2f); P(pal, People.RCapBrim, trim, 0.2f);
            var smr = r.go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null) return;
            var mpb = new MaterialPropertyBlock();
            mpb.SetVectorArray("_Pal", pal);
            smr.SetPropertyBlock(mpb);
        }
        static float Lum(Color c) => 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;
        static void P(Vector4[] pal, int i, Color c, float gloss) { var lc = c.linear; pal[i] = new Vector4(lc.r, lc.g, lc.b, gloss); }

        // the cup: submesh 0 the lacquered base, 1 the cup with its two handles (origin = the bottom, 0.62 m tall)
        Mesh TrophyMesh()
        {
            var mb = new MeshB(2);
            mb.Lathe(0, new[] { new Vector2(0f, 0f), new Vector2(0.12f, 0f), new Vector2(0.12f, 0f), new Vector2(0.12f, 0.075f), new Vector2(0.12f, 0.075f),
                                new Vector2(0.09f, 0.087f), new Vector2(0.065f, 0.10f), new Vector2(0.03f, 0.105f) }, 28);
            mb.Lathe(1, new[] { new Vector2(0.05f, 0.098f), new Vector2(0.032f, 0.13f), new Vector2(0.022f, 0.2f), new Vector2(0.034f, 0.232f), new Vector2(0.022f, 0.262f),
                                new Vector2(0.03f, 0.29f), new Vector2(0.065f, 0.312f), new Vector2(0.112f, 0.352f), new Vector2(0.146f, 0.42f), new Vector2(0.16f, 0.5f),
                                new Vector2(0.166f, 0.56f), new Vector2(0.174f, 0.585f), new Vector2(0.174f, 0.585f), new Vector2(0.158f, 0.585f), new Vector2(0.158f, 0.585f),
                                new Vector2(0.15f, 0.52f), new Vector2(0.12f, 0.43f), new Vector2(0.06f, 0.365f), new Vector2(0f, 0.355f) }, 28);
            for (int sgn = -1; sgn <= 1; sgn += 2) mb.Handle(1, sgn, 0.15f, 0.455f, 0.09f, 0.085f, 0.012f);
            var m = mb.Make("Trophy"); owned.Add(m); return m;
        }
        // a champagne bottle: submesh 0 the dark glass, 1 the gold foil on the neck (0.32 m)
        Mesh BottleMesh()
        {
            var mb = new MeshB(2);
            mb.Lathe(0, new[] { new Vector2(0f, 0f), new Vector2(0.045f, 0f), new Vector2(0.045f, 0f), new Vector2(0.046f, 0.19f), new Vector2(0.04f, 0.22f),
                                new Vector2(0.024f, 0.25f), new Vector2(0.016f, 0.27f) }, 18);
            mb.Lathe(1, new[] { new Vector2(0.026f, 0.245f), new Vector2(0.0175f, 0.272f), new Vector2(0.017f, 0.316f), new Vector2(0.019f, 0.322f), new Vector2(0.019f, 0.322f), new Vector2(0f, 0.325f) }, 18);
            var m = mb.Make("Bottle"); owned.Add(m); return m;
        }
    }

    // ---------------------------------------------------------------- a small mesh builder (submeshes by colour)
    sealed class MeshB
    {
        readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        readonly List<int>[] tri;
        public MeshB(int subs) { tri = new List<int>[subs]; for (int i = 0; i < subs; i++) tri[i] = new List<int>(); }

        // a quad centred at c with half axes a (right) and b (up), facing nrm (clockwise seen from the front)
        public void Quad(int s, Vector3 c, Vector3 a, Vector3 b, Vector3 nrm)
        {
            int o = v.Count;
            v.Add(c - a - b); v.Add(c - a + b); v.Add(c + a + b); v.Add(c + a - b);
            for (int k = 0; k < 4; k++) n.Add(nrm);
            // make the winding face nrm
            bool flip = Vector3.Dot(Vector3.Cross(b, a), nrm) < 0f;
            if (!flip) { tri[s].Add(o); tri[s].Add(o + 1); tri[s].Add(o + 2); tri[s].Add(o); tri[s].Add(o + 2); tri[s].Add(o + 3); }
            else { tri[s].Add(o); tri[s].Add(o + 2); tri[s].Add(o + 1); tri[s].Add(o); tri[s].Add(o + 3); tri[s].Add(o + 2); }
        }
        // a box (no bottom): centre, unit axes x / y / z, half sizes
        public void Box(int s, Vector3 c, Vector3 ax, Vector3 ay, Vector3 az, float ex, float ey, float ez)
        {
            Quad(s, c + az * ez, ax * ex, ay * ey, az); Quad(s, c - az * ez, ax * ex, ay * ey, -az);
            Quad(s, c + ax * ex, az * ez, ay * ey, ax); Quad(s, c - ax * ex, az * ez, ay * ey, -ax);
            Quad(s, c + ay * ey, ax * ex, az * ez, ay);
        }
        // a surface of revolution round y from a (radius, height) profile, bottom to top; a point given twice = a sharp edge
        public void Lathe(int s, Vector2[] prof, int seg)
        {
            int np = prof.Length, o = v.Count;
            var pn = new Vector2[np];
            for (int i = 0; i < np; i++)
            {
                // smooth: both neighbouring segments; at a doubled point each copy takes only its own side's segment
                Vector2 sum = Vector2.zero;
                if (i > 0 && (prof[i] - prof[i - 1]).sqrMagnitude >= 1e-10f) sum += SegN(prof[i - 1], prof[i]);
                if (i + 1 < np && (prof[i + 1] - prof[i]).sqrMagnitude >= 1e-10f) sum += SegN(prof[i], prof[i + 1]);
                pn[i] = sum.sqrMagnitude > 1e-12f ? sum.normalized : new Vector2(0f, 1f);
            }
            for (int i = 0; i < np; i++)
                for (int j = 0; j <= seg; j++)
                {
                    float a = j * Mathf.PI * 2f / seg, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                    v.Add(new Vector3(prof[i].x * ca, prof[i].y, prof[i].x * sa));
                    n.Add(new Vector3(pn[i].x * ca, pn[i].y, pn[i].x * sa));
                }
            int W = seg + 1;
            for (int i = 0; i + 1 < np; i++)
            {
                if ((prof[i + 1] - prof[i]).sqrMagnitude < 1e-10f) continue;
                for (int j = 0; j < seg; j++)
                {
                    int a = o + i * W + j, b = o + (i + 1) * W + j, c = b + 1, d = a + 1;
                    tri[s].Add(a); tri[s].Add(b); tri[s].Add(c); tri[s].Add(a); tri[s].Add(c); tri[s].Add(d);
                }
            }
        }
        static Vector2 SegN(Vector2 a, Vector2 b) { Vector2 d = b - a; return new Vector2(d.y, -d.x).normalized; }
        // a cup handle: a tube along a half ellipse in the x-y plane on side sgn (centre cx, cy; radii rx, ry; tube radius)
        public void Handle(int s, int sgn, float cx, float cy, float rx, float ry, float rad)
        {
            const int K = 12, A = 6;
            int o = v.Count;
            for (int k = 0; k <= K; k++)
            {
                float phi = Mathf.Lerp(100f, -100f, k / (float)K) * Mathf.Deg2Rad;
                Vector3 p = new Vector3(sgn * (cx + rx * Mathf.Cos(phi)), cy + ry * Mathf.Sin(phi), 0f);
                Vector3 tg = new Vector3(sgn * rx * Mathf.Sin(phi), -ry * Mathf.Cos(phi), 0f).normalized; // d/dk (phi falls)
                Vector3 B = Vector3.forward, Nn = Vector3.Cross(B, tg).normalized;
                for (int a = 0; a <= A; a++)
                {
                    float al = a * Mathf.PI * 2f / A;
                    Vector3 dir = Nn * Mathf.Cos(al) + B * Mathf.Sin(al);
                    v.Add(p + dir * rad); n.Add(dir);
                }
            }
            int W = A + 1;
            for (int k = 0; k < K; k++)
                for (int a = 0; a < A; a++)
                {
                    int p0 = o + k * W + a, p1 = o + (k + 1) * W + a, p2 = p1 + 1, p3 = p0 + 1;
                    tri[s].Add(p0); tri[s].Add(p2); tri[s].Add(p1); tri[s].Add(p0); tri[s].Add(p3); tri[s].Add(p2);
                }
        }
        public Mesh Make(string name)
        {
            var m = new Mesh { name = name };
            if (v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(v); m.SetNormals(n);
            m.subMeshCount = tri.Length;
            for (int i = 0; i < tri.Length; i++) m.SetTriangles(tri[i], i);
            m.RecalculateBounds();
            return m;
        }
    }
}
