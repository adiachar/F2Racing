// PreRace.cs — the cinematic before a race start, built around the player's driver (F1 25 / GT7 style), another part
// of GameManager (a partial class; the cinematic itself is the nested PreRace class). Any tap skips it.
// Setting "Race start" (Settings > GAME, saved as "preRace", same values as the old "Race intro" On / Short / Off):
//   Full (default): the garage, the pit exit and the formation lap (GameManagerFormation.cs), then the lights
//   Short: engine start in the cockpit and the grid, then the lights          Off: straight to the lights
// Full, on a circuit with a pit lane (the real order of an F1 race start):
//  1. two drone shots (wide, 48-64 deg, eased drift): high over the showcase corner (the longest sustained bend in the
//     second half of the lap), then down over the start straight onto the team's garage
//  2. the player's car stands in its pit box (PitRun.InBox, posed every frame: no physics), the rivals are already on the
//     grid. The driver (People.cs rig, helmet on, race suit in the team colours: the menu driver's look, MenuStudio.cs)
//     walks from the back of the garage to the car (wide from the fast lane, then low at the nose), stands at the
//     cockpit edge with a hand on the tub and reaches for his visor
//  3. a dip to black (he climbs in), then his eyes: hands on the wheel, the visor comes down, ignition (the wheel display
//     lights up, LED sweep), the engine catches with two blips; the pit crew stands round the car (PitCrew, its stop
//     clock held just before the release)
//  4. the release: the light goes green, the car pulls out of the box and the crew steps away (low from the fast lane),
//     then a cut to the pit exit: the car runs the last metres down the lane at the 80 km/h limiter onto the track
//     (the lane's own path, PitLane.cs, as a function of time: driven, not simulated; a cut skips the middle)
//  5. a dip to black: the reconnaissance lap is cut, the car is in its grid slot; a low TV shot down the grid, then the
//     formation lap (GameManagerFormation.cs) and the lights.
//  Without a pit lane: the drones, the driver walks up to his car on the grid, the cockpit, the grid, the formation lap.
// While it runs the race clock is held at 0 (GameManagerStart: StartInput / StartAfterStep / PreRaceRevs), so every car
// stays held on its brakes in its slot and the lights wait. The HUD is replaced by F1-TV lower-third captions, a "TAP TO
// SKIP" hint and thin letterbox bars. The view mode (camMode) is set per shot (TV for the outside shots: rival detail by
// zoom, no car cull, the sound heard from the camera; cockpit for the onboard: the full car model) and given back at the end.
// Cost: one People rig and one contact-shadow quad, created in PreRaceBegin and destroyed in Finish (played out,
// skipped, a restart or an error); nothing of it runs in the race. Nothing allocates per frame (captions are built once).
// Shot mode (DebugShots.cs): ?intro=t1,t2,... freezes the cinematic at those times (s), one frame each; times past its
// end show the formation lap (GameManagerFormation.cs: the cars posed along it).
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    // ---------------------------------------------------------------- setting, state, hooks
    static readonly string[] PreRaceNames = { "Full", "Short", "Off" };
    int preRaceIdx;                         // 0 full (garage + formation lap), 1 short (cockpit + grid), 2 off ("preRace")
    bool shotIntro; float shotIntroT = -1f; // shot mode: intro=... given / the time it is frozen at (-1 = not yet)
    PreRace intro; float introPostFade;     // the running intro (null = none); black fading out after a skip

    bool PreRaceHold => intro != null;                              // race clock held, cars on their brakes (GameManagerStart)
    float PreRaceThrottle => intro != null ? intro.Throttle : 0f;  // the engine-start blips (grid revs)
    bool PreRaceCarPosed => intro != null && intro.Posed;           // the car is in its pit box / the pit lane: no physics step (GameManager.Update)

    // StartSession, after the car is reset: the intro, if this is a race start and the setting wants it
    void PreRaceBegin()
    {
        PreRaceAbort();
        if (!GridStart || qualiSession || model == null || cam == null || track == null || track.N < 8) return;
        if (shotFreeze && !shotIntro) return; // screenshot mode: only when asked for (intro=...)
        int mode = shotIntro ? 0 : preRaceIdx;
        if (mode >= 2) return;
        try
        {
            var p = new PreRace(this, mode == 0);
            if (p.Valid) intro = p; else { p.Finish(); if (mode == 0) FormBegin(); }
        }
        catch (System.Exception e) { Debug.LogWarning("PreRace: " + e.Message); PreRaceAbort(); if (mode == 0) FormBegin(); }
    }

    // StartReset (any new start): a running intro is dropped at once
    void PreRaceAbort() { var p = intro; intro = null; if (p != null) p.Finish(); }

    // GameManager.LateUpdate, first thing: true = the intro placed the camera this frame
    bool PreRaceCamera()
    {
        if (intro == null) { if (introPostFade > 0f) introPostFade -= Mathf.Min(Time.unscaledDeltaTime, 0.05f); return false; }
        try { return intro != null && intro.Frame(); }
        catch (System.Exception e) { Debug.LogWarning("PreRace: " + e.Message); PreRaceAbort(); return false; }
    }

    // GameManager.OnGUI, before the HUD: true = the intro drew the screen (no HUD). After a skip: the picture comes
    // back out of black under the HUD.
    bool PreRaceGui()
    {
        if (intro == null)
        {
            if (introPostFade > 0f && Event.current.type == EventType.Repaint)
                UiKit.Rect(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, Mathf.Clamp01(introPostFade / PreRace.SkipFade)));
            return false;
        }
        try { intro.Gui(); } catch (System.Exception e) { Debug.LogWarning("PreRace GUI: " + e.Message); }
        return true;
    }

    // shot mode: freeze the intro at t seconds (DebugShots.cs); a time before the formation lap after one: the
    // cinematic again from the start (the car back in its pit box)
    internal void ShotIntro(float t)
    {
        shotIntroT = Mathf.Max(0f, t);
        if (form != null && formShotBase >= 0f && shotIntroT < formShotBase) { FormAbort(true); PreRaceBegin(); }
        else if (intro != null && intro.BackToBox(shotIntroT)) PreRaceBegin(); // (an earlier time than the cut to the grid)
    }

    // ---------------------------------------------------------------- the intro
    sealed class PreRace
    {
        public const float SkipFade = 0.3f;
        enum K { FlyCorner, FlyGrid, FlyPits, Garage, GridWalk, Edge, Cockpit, BoxOut, PitExit, GridTv }
        sealed class Cap { public string eyebrow, title, sub, block; public float from; }
        struct Shot { public K k; public float t0, dur; public Cap cap; }

        // timings (s)
        const float DFlyCorner = 2.4f, DFlyGrid = 2.6f, DGarage = 2.2f, DGridWalk = 2.8f, DEdge = 1.9f, DCockpit = 3.8f;
        const float DBoxWalk = 3.2f, DBoxOut = 3.3f, DPitExit = 3.0f; // (full, with a pit lane: the garage walk, the release, the pit exit)
        const float MoveDelay = 0.4f;       // the car pulls away this long into the release shot (the crew's light goes green first)
        const float CrewHeld = 2.0f;        // the crew's stop clock while the car waits (PitCrew: jacks down, guns off; green 2.12, release 2.4)
        const float WalkV = 1.55f;          // m/s, the garage walk at most
        const float TvMain = 2.4f, Handover = 1.6f;
        const float Ign = 1.1f;             // ignition, from the start of the onboard shot
        const float DipHalf = 0.28f;        // the dip to black between the cockpit edge and the onboard
        const float WalkLead = 0.6f;        // he is already walking as the grid-walk shot opens
        const float WalkTime = DGridWalk + WalkLead - 0.45f; // ... and stops at the cockpit just before its end
        static readonly Color HelmetCol = new Color(1f, 0.82f, 0.18f); // the player's helmet (CarModel.PlayerHelmet)
        static readonly int DashId = Shader.PropertyToID("_Dash"), LedId = Shader.PropertyToID("_Led");

        readonly GameManager g;
        readonly List<Shot> shots = new List<Shot>();
        readonly int userCam; readonly bool crane, full;
        float T, total, skipT; bool skipping, finished, cranked, buzzed, ledsCleared;
        float ignAt = -1f, walkStart, walkTime, edgeEnd = -1f, dipAt = -1f; // dipAt: the cut into the onboard (a dip to black)
        public bool Valid => shots.Count > 0;
        // the pit box and the pit lane (full, with a pit lane): the car posed on its lane path until the cut to the grid
        readonly bool pits; readonly int bay; PitRun boxRun; readonly PitLane lane;
        float moveAt = -1f, exitAt = -1f, cutAt = -1f, crewFrom = -1f, crewT = CrewHeld; bool crewOn; System.Func<float> crewClock;
        double uMoveEnd, uExit0;
        public bool Posed => boxRun != null;
        public bool BackToBox(float t) => pits && boxRun == null && cutAt > 0f && t < cutAt; // shot mode: the car has left its box already
        public float Total => total;

        // the field
        readonly double playerS, playerLat, cornerS; readonly float cornerSign;
        Vector3 outer;                               // the player's car: its side away from the field (flat); in the pit box: the garage side
        readonly Color suit, trimCol; readonly string tapTxt;
        // the garage
        readonly bool bayOk; readonly Vector3 bc, bg, bf; readonly float gd;
        // the driver's rig (the menu driver's look and stance, MenuStudio.cs)
        GameObject root; People.Rig rig; People.Blobs blob;
        readonly People.PoseBuf pa = new People.PoseBuf(), pb = new People.PoseBuf();
        Vector3 rigPos; bool rigShown; int calfR = -1;
        // things changed on the car (given back at the end)
        readonly List<SkinnedMeshRenderer> hiddenDrivers = new List<SkinnedMeshRenderer>();
        readonly List<SteeringWheel> darkWheels = new List<SteeringWheel>(); readonly List<Texture> darkTex = new List<Texture>();
        CarModel dmFor; SkinnedMeshRenderer dmR;

        public PreRace(GameManager owner, bool fullMode)
        {
            bool shortMode = !fullMode; full = fullMode;
            g = owner; userCam = g.camMode == TvMode ? TvMode : Mathf.Clamp(g.camMode, 0, 3); crane = !full && (userCam == 0 || userCam == 1); // (full: the formation lap's own shots follow)
            var tk = g.track;
            int slot = g.playerSlot, cars = g.rivals.Count + 1;
            playerS = StartGrid.SlotS(tk, slot); playerLat = StartGrid.SlotLat(tk, slot);
            TrackAt(playerS, out _, out Vector3 slotN, out _);
            outer = slotN * (playerLat >= 0 ? 1f : -1f);
            cornerS = ShowcaseCorner(tk, out cornerSign);

            // the player: livery (as SessionStarted gives the car), suit colours, names
            bool season = g.InSeasonRound;
            Livery liv = season ? Livery.ForTeam(Season.Data.team, Season.TeamColour(Season.Data.team)) : Livery.Player[Mathf.Clamp(g.liveryIdx, 0, Livery.Player.Length - 1)];
            suit = liv.primary; trimCol = Trim(liv);
            MenuProfile.Load();
            string driver = MenuProfile.Name, num = MenuProfile.NumStr;
            string team = season && Season.Data.team >= 0 && Season.Data.team < Season.Teams.Count ? Season.Teams[Season.Data.team] : liv.name;
            tapTxt = UiKit.Caps("Tap to skip");

            // the team's garage (the player's pit box)
            try
            {
                var pc = g.pitCrew;
                if (pc != null && Trackside.Bays.Count > 0)
                {
                    int b = pc.PlayerBay; bay = b;
                    if (b >= 0 && b < Trackside.Bays.Count)
                    {
                        var pbay = Trackside.Bays[b];
                        bc = pbay.centre; bg = Flat(pbay.side); bf = Flat(pbay.fwd); gd = pbay.garageDist;
                        bayOk = pbay.side.sqrMagnitude > 0.25f && gd > 1f && gd < 40f;
                    }
                }
            }
            catch (System.Exception) { bayOk = false; }
            // full: the car starts in its pit box when the circuit has a pit lane (else on the grid, as before)
            lane = Lane;
            pits = full && bayOk && g.PitLaneOn && g.pitCrew != null && lane != null && bay >= 0 && bay < lane.bayU.Length;
            if (pits)
            {
                try
                {
                    boxRun = PitRun.InBox(lane, bay, g.playerPitKey, 0);
                    boxRun.ApplyTo(g.car); g.car.vx = g.car.vy = 0; g.car.r = 0; g.car.delta = 0;
                    g.SnapDrawn();
                    outer = Flat(bg - bf * Vector3.Dot(bg, bf)); // the driver comes from the garage side
                }
                catch (System.Exception e) { Debug.LogWarning("PreRace: no pit box (" + e.Message + ")"); if (boxRun != null) boxRun.Cancel(); boxRun = null; pits = false; }
            }

            // captions (built once: nothing is formatted per frame)
            float km = (float)(tk.length / 1000.0);
            int round = season ? Mathf.Clamp(Season.Data.round, 0, Season.GpNames.Length - 1) : 0;
            string session = season ? "Round " + (round + 1) + "  ·  " + Season.GpNames[round] + " Grand Prix"
                           : g.sessionMode == MenuUI.ModeWeekend ? "Race weekend" : "Quick race";
            string circuit = g.mapIdx >= 0 && g.mapIdx < MapNames.Length ? MapNames[g.mapIdx] : "Circuit";
            string lenLaps = km.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " km  ·  " + g.raceLaps + " laps";
            string pole = slot == 0 || g.rivals.Count == 0 ? driver : g.rivals[0].who.name;
            string poleTeam = slot == 0 || g.rivals.Count == 0 ? team : g.rivals[0].who.team;
            string gridP = "P" + (slot + 1);
            var capCircuit = new Cap { eyebrow = session, title = circuit, sub = lenLaps };
            var capPole = new Cap { eyebrow = "Starting grid", title = "Pole  ·  " + pole, sub = poleTeam };
            var capDriver = new Cap { eyebrow = "Driver", title = driver, sub = team + "  ·  Grid " + gridP, block = "#" + num };
            var capGrid = new Cap { eyebrow = "On the grid", title = "Starts " + gridP + " of " + cars, sub = Weather.TyreName(g.carSet) + " tyres  ·  " + Weather.Names[Mathf.Clamp(Weather.Index, 0, Weather.Names.Length - 1)] };
            var capStart = new Cap { eyebrow = session, title = shortMode ? circuit : "Race start", sub = shortMode ? lenLaps : g.raceLaps + " laps  ·  " + (km * g.raceLaps).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " km" };
            var capPit = new Cap { eyebrow = "Pit lane open", title = "Reconnaissance lap", sub = "Pit limiter  ·  80 km/h" };

            // the driver himself (the People models; none = no shots on foot)
            bool rigOk = false;
            if (!shortMode)
            {
                try
                {
                    if (People.Ready)
                    {
                        root = new GameObject("PreRace");
                        rig = new People.Rig(root.transform, People.DriverBody >= 0 ? People.DriverBody : 0, People.GHelmet, "PlayerDriver"); // (People2's race-suited driver when there is one)
                        rig.SetLod(0);
                        Dress(rig, liv);
                        for (int j = 0; j < People.NJ; j++) if (People.Parent[j] == People.JThighR) { calfR = j; break; }
                        blob = new People.Blobs(root.transform, 1, "PlayerDriverShadow");
                        rig.Show(false); blob.Show(false); rigShown = false;
                        rigOk = true;
                    }
                }
                catch (System.Exception e) { Debug.LogWarning("PreRace: no driver rig (" + e.Message + ")"); DestroyRig(); rigOk = false; }
            }

            // the running order
            if (pits)
            {
                Add(K.FlyCorner, DFlyCorner, capCircuit); Add(K.FlyPits, DFlyGrid, capPole);
                if (rigOk) { Add(K.Garage, DBoxWalk, capDriver); Add(K.GridWalk, DGridWalk - 0.4f, null); Add(K.Edge, DEdge, null); }
                else capDriver.from = 0.9f;
                Add(K.Cockpit, DCockpit, rigOk ? null : capDriver);
                Add(K.BoxOut, DBoxOut, null); Add(K.PitExit, DPitExit, capPit);
                Add(K.GridTv, TvMain, capGrid);
            }
            else
            {
                if (!shortMode)
                {
                    Add(K.FlyCorner, DFlyCorner, capCircuit); Add(K.FlyGrid, DFlyGrid, capPole);
                    if (rigOk && bayOk) Add(K.Garage, DGarage, capDriver);
                    if (rigOk) { Add(K.GridWalk, DGridWalk, bayOk ? capGrid : capDriver); Add(K.Edge, DEdge, null); }
                }
                if (shortMode || !rigOk) capDriver.from = 0.9f; // over the onboard: after the visor is down
                Add(K.Cockpit, DCockpit, shortMode || !rigOk ? capDriver : null);
                Add(K.GridTv, TvMain + (crane ? Handover : 0f), shortMode ? capStart : full ? capGrid : rigOk ? capStart : capGrid);
            }
            walkTime = WalkTime;
            for (int i = 0; i < shots.Count; i++)
            {
                var s = shots[i];
                if (s.k == K.Cockpit) { ignAt = s.t0 + Ign; if (i > 0 && shots[i - 1].k == K.Edge) dipAt = s.t0; if (pits) crewFrom = s.t0; }
                if (s.k == K.Edge) edgeEnd = s.t0 + s.dur;
                if (s.k == K.GridWalk && !pits) walkStart = s.t0 - WalkLead;
                if (s.k == K.Garage && pits) { walkStart = s.t0 - 0.3f; walkTime = DBoxWalk + DGridWalk - 0.4f + 0.3f - 0.45f; }
                if (s.k == K.BoxOut) moveAt = s.t0 + MoveDelay;
                if (s.k == K.PitExit) exitAt = s.t0;
                if (s.k == K.GridTv && pits) cutAt = s.t0;
            }
            if (pits)
            {
                // the release shot accelerates the car out of the box to the limiter; the pit-exit shot then shows its last
                // metres to the end of the lane (a cut: the middle of the lane is skipped)
                uMoveEnd = BoxU(exitAt - moveAt);
                uExit0 = System.Math.Max(uMoveEnd, lane.sExit - 4 - PitLane.Limiter * DPitExit);
                crewClock = () => crewT;
            }
        }
        // the car's lane distance t s after it pulls away: AOut to the limiter, then the limiter
        double BoxU(double t)
        {
            if (t <= 0) return boxRun != null ? boxRun.BoxU : 0;
            double vL = PitLane.Limiter, tA = vL / PitLane.AOut, u0 = boxRun != null ? boxRun.BoxU : 0;
            return t < tA ? u0 + 0.5 * PitLane.AOut * t * t : u0 + 0.5 * PitLane.AOut * tA * tA + vL * (t - tA);
        }

        void Add(K k, float d, Cap c) { shots.Add(new Shot { k = k, t0 = total, dur = d, cap = c }); total += d; }
        int ShotAt(float t) { int si = 0; for (int i = 0; i < shots.Count; i++) if (t >= shots[i].t0) si = i; return si; }

        // ---------------------------------------------------------------- per frame (LateUpdate)
        // false = it has just finished (the game's own camera takes this frame)
        public bool Frame()
        {
            if (finished) return false;
            if (g.model == null || g.cam == null || g.car == null) { g.PreRaceAbort(); return false; }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f); // the first frames (and a car build) can be long: never jump a shot
            if (g.shotIntro)
            {
                float st = g.shotIntroT < 0f ? 0f : g.shotIntroT;
                if (full && st >= total) { g.formShotBase = total; End(); return false; } // shot mode: past the cinematic = the formation lap
                T = Mathf.Clamp(st, 0f, total - 0.02f); // shot mode: frozen
            }
            else
            {
                T += dt;
                if (!skipping && T > 0.35f && SkipPressed()) { skipping = true; skipT = 0f; }
                if (skipping) { skipT += dt; if (skipT >= SkipFade) { g.introPostFade = SkipFade; End(); return false; } }
                if (T >= total) { End(); return false; }
            }
            var s = shots[ShotAt(T)]; float lt = T - s.t0, u = Mathf.Clamp01(lt / s.dur);

            // view mode for this shot: TV outside (rival detail by zoom, no car cull, sound from the camera), cockpit
            // onboard (switched under the dip to black: the full car may be built that frame)
            bool handover = s.k == K.GridTv && crane && lt >= TvMain;
            int want = handover ? userCam : TvMode;
            if (s.k == K.Cockpit || (dipAt > 0f && T >= dipAt - DipHalf * 0.5f && T < dipAt)) want = 2;
            g.camMode = want;

            // the pit box and the pit lane: the car on its lane path, the crew round it; under the dip: to the grid
            if (boxRun != null)
            {
                if (cutAt > 0f && T >= cutAt) ToGrid();
                else PoseBox();
            }
            Crew();

            // the driver is in his car from the onboard shot on; before that he is on foot
            bool inCar = s.k == K.Cockpit || s.k == K.GridTv || s.k == K.BoxOut || s.k == K.PitExit;
            CarBits(s.k == K.Cockpit, inCar);
            Engine();
            Rig(s.k);

            Vector3 pos; Quaternion rot; float fov, near = ChaseNear;
            switch (s.k)
            {
                case K.FlyCorner: FlyCorner(u, out pos, out rot, out fov); near = 1f; break;
                case K.FlyGrid: FlyGrid(u, out pos, out rot, out fov); near = 0.7f; break;
                case K.FlyPits: FlyPits(u, out pos, out rot, out fov); near = 0.7f; break;
                case K.Garage: if (pits) BoxWide(u, out pos, out rot, out fov); else Garage(u, out pos, out rot, out fov); break;
                case K.BoxOut: BoxOut(u, out pos, out rot, out fov); break;
                case K.PitExit: PitExit(u, out pos, out rot, out fov); break;
                case K.GridWalk: GridWalk(u, out pos, out rot, out fov); break;
                case K.Edge: Edge(u, out pos, out rot, out fov); near = 0.3f; break;
                case K.Cockpit: Onboard(lt, out pos, out rot, out fov); break;
                default:
                    if (handover) Crane((lt - TvMain) / Handover, out pos, out rot, out fov);
                    else GridTv(Mathf.Clamp01(lt / TvMain), out pos, out rot, out fov);
                    break;
            }
            g.cam.transform.SetPositionAndRotation(pos, rot);
            g.cam.fieldOfView = fov;
            g.cam.nearClipPlane = s.k == K.Cockpit ? g.CockpitNear() : near;
            g.camVel = Vector3.zero;
            g.FpsWindowReset(2f); // the flyover draws the whole circuit: Auto never steps the frame rate or graphics down for it
            return true;
        }

        // ---------------------------------------------------------------- the pit box and the pit lane
        // the car on its lane path at this moment (a function of T: shot mode freezes it like the rest): in the box until
        // the release, out of the box to the limiter, then (after the cut) the last metres to the pit exit
        void PoseBox()
        {
            var r = boxRun; var c = g.car;
            double u, v, mv = moveAt > 0f ? T - moveAt : -1; bool accel = false;
            if (mv <= 0) { u = r.BoxU; v = 0; }
            else if (exitAt < 0f || T < exitAt)
            {
                u = BoxU(mv); double tA = PitLane.Limiter / PitLane.AOut;
                accel = mv < tA; v = accel ? PitLane.AOut * mv : PitLane.Limiter;
            }
            else { u = uExit0 + PitLane.Limiter * (T - exitAt); v = PitLane.Limiter; }
            u = System.Math.Min(u, lane.sExit - 0.5);
            r.phase = mv > 0 ? 2 : 1;
            // the path's curvature 1 m either side (the steering), then the pose itself (a frozen run only poses: Step(0))
            r.u = u + 1; r.Step(0); double h1 = r.heading;
            r.u = u - 1; r.Step(0); double h0 = r.heading;
            r.u = u; r.v = v; r.Step(0);
            r.ApplyTo(c);
            double k = System.Math.Atan2(System.Math.Sin(h1 - h0), System.Math.Cos(h1 - h0)) / 2;
            c.delta = v > 0.3 ? System.Math.Atan(c.cfg.wheelbase * k) : 0; c.r = v * k;
            c.throttle = accel ? 0.5 : v > 1 ? 0.18 : 0; c.brake = v > 0.3 ? 0 : 1; c.axLong = accel ? PitLane.AOut : 0;
            g.SnapDrawn();
        }

        // the pit crew round the car from the onboard on: its stop clock held just before the release, then running (the
        // light goes green, the car is released and they step away)
        void Crew()
        {
            var pc = g.pitCrew;
            if (pc == null || crewFrom < 0f) return;
            crewT = Mathf.Min(CrewHeld + Mathf.Max(0f, moveAt > 0f ? T - (moveAt - MoveDelay) : 0f), PitCrew.ReleaseT + 1.9f);
            bool want = boxRun != null && T >= crewFrom - DipHalf && T < cutAt;
            if (want && !crewOn) crewOn = pc.Begin(-1, bay, Livery.Player[Livery.PlayerIndex], Weather.Stripe(g.carSet), Weather.Stripe(g.carSet), 0f, g.PlayerPitModel, crewClock);
            else if (!want && crewOn) { crewOn = false; if (pc.Owns(-1)) pc.Stop(); }
        }

        // under the dip after the pit exit: the reconnaissance lap is cut, the car stands in its grid slot
        void ToGrid()
        {
            if (crewOn) { crewOn = false; if (g.pitCrew != null && g.pitCrew.Owns(-1)) g.pitCrew.Stop(); }
            if (boxRun != null) { boxRun.Cancel(); boxRun = null; }
            g.ParkOnGrid();
            TrackAt(playerS, out _, out Vector3 slotN, out _);
            outer = slotN * (playerLat >= 0 ? 1f : -1f);
        }

        static bool SkipPressed()
        {
            for (int i = 0, n = Input.touchCount; i < n; i++) if (Input.GetTouch(i).phase == TouchPhase.Began) return true;
            return Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Escape);
        }

        // ---------------------------------------------------------------- the car: helmet, halo, the driver in it, the wheel's display
        void CarBits(bool cockpit, bool inCar)
        {
            var m = g.model;
            if (dmFor != m)
            {
                dmFor = m; dmR = null;
                var dm = m.GetComponentInChildren<DriverModel>(true);
                if (dm != null) dmR = dm.GetComponent<SkinnedMeshRenderer>();
            }
            // (with a DriverModel the helmet is its head's stand-in; the code-built car's plain helmet goes with him)
            if (m.helmet != null) m.helmet.gameObject.SetActive(!cockpit && (inCar || dmR != null));
            bool halo = !(cockpit && !g.haloInCockpit);
            if (m.haloPillar != null) m.haloPillar.SetActive(halo);
            if (m.haloHoop != null) m.haloHoop.SetActive(halo);
            if (m.cockpitFloor) m.cockpitFloor.SetActive(cockpit);
            if (dmR != null && dmR.enabled != inCar)
            {
                dmR.enabled = inCar; // the driver is out of his car while he walks to it
                if (!inCar && !hiddenDrivers.Contains(dmR)) hiddenDrivers.Add(dmR);
            }
            m.PoseDriver(g.feel); // hands on the wheel (the head hidden in the onboard: helmet stand-in off)

            // the wheel: dark until ignition, then an LED sweep, then live (DriverFeel.Cockpit -> SteeringWheel.Feed)
            var w = m.WheelRig;
            if (w == null || w.Mat == null) return;
            int k = darkWheels.IndexOf(w);
            if (ignAt < 0f || T < ignAt)
            {
                if (k < 0) { darkWheels.Add(w); darkTex.Add(w.Mat.GetTexture(DashId)); w.Mat.SetTexture(DashId, Texture2D.blackTexture); }
                w.Mat.SetVector(LedId, Vector4.zero);
                ledsCleared = false;
                return;
            }
            if (k >= 0) { w.Mat.SetTexture(DashId, darkTex[k]); darkWheels.RemoveAt(k); darkTex.RemoveAt(k); }
            float su = (T - ignAt) / 0.75f;
            if (su < 1f) { w.Mat.SetVector(LedId, new Vector4(Mathf.Round(15f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(su))), 0f, 0f, 0f)); ledsCleared = false; return; }
            if (!ledsCleared) { w.Mat.SetVector(LedId, Vector4.zero); ledsCleared = true; } // (the wheel's own LED state: off at idle)
            if (cockpit) g.feel.Cockpit(m, g.car, Time.time); // the display live, shift lights
        }

        // engine: silent until ignition (every car: the grid waits with engines off), the starter brings the revs up,
        // it catches; then two blips (Throttle -> the grid revs of GameManagerStart -> EngineAudio)
        void Engine()
        {
            var ae = g.audioEngine;
            float k = ignAt < 0f ? 1f : Mathf.Clamp01((T - ignAt - 0.12f) / 0.25f);
            if (ae != null) ae.engineVol = g.volEngine * 0.01f * k;
            if (g.shotIntro || ignAt < 0f) return;
            if (!cranked && T >= ignAt + 0.12f) { cranked = true; g.gridRpm = g.car.cfg.idleRpm * 0.45; } // turning over on the starter
            if (!buzzed && T >= ignAt + 0.35f) { buzzed = true; Haptics.Pulse(70, 150); }                 // it fires
        }
        public float Throttle
        {
            get
            {
                if (ignAt < 0f || finished) return 0f;
                float x = T - ignAt;
                return 0.8f * Bump(x - 0.6f, 0.17f) + 0.55f * Bump(x - 1.15f, 0.13f);
            }
        }
        static float Bump(float x, float w) => x <= 0f || x >= w ? 0f : Mathf.Sin(x / w * Mathf.PI);

        // ---------------------------------------------------------------- the driver on foot
        // the menu driver's race suit (MenuStudio.Dress): livery colours, a contrasting trim, dark boots, yellow helmet
        static float Lum(Color c) => 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;
        static Color Trim(Livery l)
        {
            Color prim = l.primary, sec = l.secondary, dark = new Color(0.05f, 0.05f, 0.06f);
            return Mathf.Abs(Lum(sec) - Lum(prim)) < 0.12f ? (Lum(prim) > 0.5f ? dark : new Color(0.9f, 0.9f, 0.91f)) : sec;
        }
        static void Dress(People.Rig r, Livery l)
        {
            var pal = new Vector4[People.NReg];
            People.DriverPalette(pal, l, HelmetCol); // (as MenuStudio: team suit, trim on the accent, yellow helmet)
            var smr = r.go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null) return;
            var mpb = new MaterialPropertyBlock();
            mpb.SetVectorArray("_Pal", pal);
            smr.SetPropertyBlock(mpb);
        }

        void Rig(K k)
        {
            if (rig == null) return;
            bool on = k == K.Garage || k == K.GridWalk || k == K.Edge;
            if (on != rigShown) { rigShown = on; rig.Show(on); if (blob != null) blob.Show(on); }
            if (!on) return;
            float ground;
            if (k == K.Garage && !pits)
            {
                // out of the garage into the box, at a steady walk
                Vector3 a = bc + bg * (gd + 2.3f), b = bc + bg * (gd - 2.2f);
                float len = Vector3.Distance(a, b), v = 1.4f, d = Mathf.Min(len, v * (T - StartOf(K.Garage) + 0.4f));
                Vector3 pos = a + (b - a) * (d / Mathf.Max(len, 0.01f));
                ground = GroundAt(pos, bc.y); pos.y = ground;
                Vector3 dir = b - a;
                People.Sample(People.WalkClip, d / Stride(v) * People.Length(People.WalkClip), pa);
                rig.Place(pos, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg); rig.Apply(pa);
                rig.LookAt(pos + Flat(dir) * 4f + Vector3.up * 1.5f, 0.4f);
                rigPos = pos;
            }
            else
            {
                // along his car to the cockpit (from behind it): he slows, stops, turns to the car and stands as the
                // menu driver does; a hand on the tub, a look into the cockpit, then a hand to his visor
                // (in the pit box: straight out of the back of the garage to the car's side, at most WalkV)
                CarFrame(out Vector3 c, out Vector3 f, out float along);
                float wt = walkTime;
                Vector3 b = c + f * (along + 0.05f) + outer * 1.2f;
                Vector3 a = pits ? b + outer * Mathf.Min(gd + 6.2f - 1.2f, WalkV * (wt - 0.25f)) : c + f * (along - 4.6f) + outer * 1.95f;
                float rt = T - walkStart, len = Vector3.Distance(a, b), v0 = len / (wt - 0.25f), d;
                if (rt < wt - 0.5f) d = v0 * Mathf.Max(0f, rt);
                else if (rt < wt) { float tau = rt - (wt - 0.5f); d = v0 * (wt - 0.5f) + v0 * (tau - tau * tau); }
                else d = len;
                d = Mathf.Min(d, len);
                Vector3 pos = a + (b - a) * (d / Mathf.Max(len, 0.01f));
                ground = GroundAt(pos, c.y); pos.y = ground;
                Vector3 dir = b - a, face = -outer;
                float walkYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, faceYaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
                float stop = Smooth((rt - (wt - 0.35f)) / 0.6f);
                rig.Place(pos, Mathf.LerpAngle(walkYaw, faceYaw, Smooth((rt - (wt - 0.2f)) / 0.7f)));
                People.Sample(People.WalkClip, d / Stride(v0) * People.Length(People.WalkClip), pa);
                People.Sample(People.MIdle, T, pb);
                People.Blend(pa, pb, stop, pa);
                rig.Apply(pa);
                Stance(T, stop);
                var tr = rig.tr; Vector3 right = tr.right, fwd = tr.forward, up = tr.up;
                // the hand nearer the car's nose on the cockpit rim
                bool noseLeft = Vector3.Dot(Vector3.Cross(Vector3.up, face), f) < 0f;
                Vector3 rim = c + f * (along + 0.25f) + outer * 0.5f; rim.y = c.y + 0.68f;
                float onTub = Smooth((rt - (wt + 0.1f)) / 0.45f);
                rig.Arm(noseLeft, rim, onTub, -face + Vector3.up * 0.3f);
                // the other hand to the visor at the end of the edge shot (it comes down in the onboard)
                float visor = edgeEnd > 0f ? Smooth((T - (edgeEnd - 0.95f)) / 0.45f) : 0f;
                if (visor > 0.001f)
                {
                    float side = noseLeft ? 1f : -1f; // the visor hand is the other one
                    Vector3 hp = rig.b[People.JHead].position;
                    rig.Arm(!noseLeft, hp + fwd * 0.17f + right * (0.05f * side) - up * (0.02f + 0.05f * Smooth((T - (edgeEnd - 0.45f)) / 0.35f)), visor, right * side - up * 0.6f);
                }
                // the head: ahead while walking, into the cockpit as he arrives, then up (ready)
                Vector3 head = rig.b[People.JHead].position;
                Vector3 cockpit = c + f * (along + 0.1f) + Vector3.up * 0.75f;
                rig.LookAt(head + Flat(dir) * 4f, 0.35f * (1f - stop));
                rig.LookAt(cockpit, 0.85f * Smooth((rt - (wt - 0.6f)) / 0.8f) * (1f - visor));
                rig.LookAt(head + fwd * 3f + up * 0.05f, 0.5f * visor);
                rigPos = pos;
            }
            if (blob != null)
            {
                blob.Begin();
                Vector3 gp = rig.Pelvis; gp.y = ground;
                blob.Set(0, gp, rig.tr.forward, 0.45f * rig.scale, 0.8f, 0.72f);
                blob.Flush();
            }
        }

        // the menu driver's standing posture and idle motion (MenuStudio.Animate), weight w (0 while walking): chest a
        // touch forward, shoulders dropped, a normal stance, breathing, weight from foot to foot, arms hanging relaxed.
        // Over the mocap idle (People.Mocap) only a little of it: that clip already stands, breathes and shifts naturally.
        void Stance(float t, float w)
        {
            if (w <= 0.001f) return;
            float wa = w; if (People.Mocap) w *= 0.3f;
            var tr = rig.tr; Vector3 right = tr.right, fwd = tr.forward, up = tr.up;
            var b = rig.b;
            Turn(b[People.JSpine3], 2.5f * w, right);
            Turn(b[People.JClavL], 3f * w, fwd); Turn(b[People.JClavR], -3f * w, fwd);
            Turn(b[People.JThighL], 3.5f * w, fwd); Turn(b[People.JCalfL], -3.5f * w, fwd);
            Turn(b[People.JThighR], -3.5f * w, fwd); if (calfR >= 0) Turn(b[calfR], 3.5f * w, fwd);
            float br = Mathf.Sin(t * 1.5f) * w;
            Turn(b[People.JSpine3], -0.5f * br, right);
            Turn(b[People.JClavL], -0.5f * br, fwd); Turn(b[People.JClavR], 0.5f * br, fwd);
            float ws = (Mathf.Sin(t * 0.7f) + 0.35f * Mathf.Sin(t * 1.9f + 1.3f)) * 0.75f * w;
            b[People.JPelvis].position += right * (0.010f * ws);
            Turn(b[People.JPelvis], 1.3f * ws, fwd); Turn(b[People.JSpine1], -1.1f * ws, fwd);
            Hang(true, People.Mocap ? 0.35f * wa : w, t, right, fwd, up); Hang(false, People.Mocap ? 0.35f * wa : w, t, right, fwd, up);
        }
        static void Turn(Transform b, float deg, Vector3 axis) { b.rotation = Quaternion.AngleAxis(deg, axis) * b.rotation; }
        void Hang(bool left, float w, float t, Vector3 right, Vector3 fwd, Vector3 up)
        {
            var b = rig.b;
            Transform ua = b[left ? People.JUpL : People.JUpR], la = b[left ? People.JLoL : People.JLoR], hd = b[left ? People.JHandL : People.JHandR];
            float l1 = (la.position - ua.position).magnitude, l2 = (hd.position - la.position).magnitude;
            float side = left ? -1f : 1f, sway = Mathf.Sin(t * 0.9f + (left ? 0f : 1.7f)) * 0.01f;
            var palm = ua.position - up * ((l1 + l2) * 0.92f + 0.06f) + right * (side * 0.05f) + fwd * (0.04f + sway);
            rig.Arm(left, palm, w, -fwd + right * (side * 0.35f));
        }
        static float Stride(float v) => People.Mocap ? People.WalkStride : 1.66f * Mathf.Max(0.3f, v) / 1.25f; // m per walk cycle at speed v (as PeoplePits; mocap: its own stride, feet planted)
        static float GroundAt(Vector3 p, float fallback) => WorldBuilder.Height != null ? WorldBuilder.H(p.x, p.z) : fallback;
        float StartOf(K k) { for (int i = 0; i < shots.Count; i++) if (shots[i].k == k) return shots[i].t0; return 0f; }

        // ---------------------------------------------------------------- the shots
        // 1a. high over the showcase corner, drifting along it and down (the circuit, its run-off, the straight after it)
        void FlyCorner(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            float e = Smooth(u);
            Vector3 a = TrackAt(cornerS, out Vector3 tg, out Vector3 n, out _);
            Vector3 o = -n * cornerSign; // the outside of the bend
            pos = a + o * (135f - 20f * e) + tg * (-75f + 50f * e); pos.y = a.y + 78f - 14f * e;
            pos = Above(pos, 25f);
            fov = Fit(64f);
            rot = Aim(pos, a + tg * 35f, fov, 0f, 0.06f) * Shake(0.22f, 0.3f, 1.7f);
        }
        // 1b. down over the back of the field onto the player's car, the drone easing to a stop behind it
        void FlyGrid(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            float e = Smooth(u);
            Vector3 a = TrackAt(playerS - 46.0, out _, out Vector3 na, out _) + na * (float)(playerLat * 0.2) + Vector3.up * 26f;
            Vector3 b = TrackAt(playerS - 14.0, out _, out Vector3 nb, out _) + nb * (float)(playerLat * 0.6) + Vector3.up * 8f;
            pos = Above(Vector3.Lerp(a, b, e), 3f);
            fov = Fit(Mathf.Lerp(58f, 48f, e));
            rot = Aim(pos, g.model.transform.position + Vector3.up * 0.45f, fov, 0f, -0.15f) * Shake(0.18f, 0.4f, 7.3f);
        }
        // 2a. the garage: long lens from the fast lane, he walks out of the team's garage towards us
        void Garage(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            float e = Smooth(u);
            Vector3 bf = Vector3.Cross(Vector3.up, bg); // along the lane
            pos = bc - bg * 3.4f + bf * (2.4f - 0.5f * e); pos.y = GroundAt(pos, bc.y) + 0.95f;
            Vector3 subj = rigPos + Vector3.up * 1.25f;
            Vector3 right = Vector3.Cross(Vector3.up, subj - pos).normalized;
            fov = Fit(25f);
            rot = Aim(pos, subj, fov, -0.28f * Sgn(Vector3.Dot(-bg, right)), 0.06f) * Shake(0.35f, 0.9f, 10.9f);
        }
        // (pit box) 1b. down over the start straight onto the team's garage, the drone easing to a stop off the pit wall
        void FlyPits(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            float e = Smooth(u);
            CarFrame(out Vector3 c, out Vector3 f, out _);
            Vector3 a = c - outer * 62f - f * 75f + Vector3.up * 40f, b = c - outer * 17f - f * 15f + Vector3.up * 7.5f;
            pos = Above(Vector3.Lerp(a, b, e), 3f);
            fov = Fit(Mathf.Lerp(56f, 44f, e));
            rot = Aim(pos, c + outer * 2.5f + Vector3.up * 1.2f, fov, 0f, -0.1f) * Shake(0.18f, 0.4f, 5.9f);
        }
        // (pit box) 2a. the team's garage: wide and high from the fast lane over the car in its box, he walks out of the
        //    back of the garage towards it (a slow push in)
        void BoxWide(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            float e = Smooth(u);
            CarFrame(out Vector3 c, out Vector3 f, out float along);
            pos = c - outer * (6.4f - 0.6f * e) + f * (along + 2.6f - 0.5f * e); pos.y = GroundAt(pos, c.y) + 2.5f - 0.3f * e;
            Vector3 subj = Vector3.Lerp(rigPos + Vector3.up * 1.2f, c + Vector3.up * 0.6f, 0.25f);
            Vector3 right = Vector3.Cross(Vector3.up, subj - pos).normalized;
            fov = Fit(Mathf.Lerp(40f, 35f, e));
            rot = Aim(pos, subj, fov, 0.12f * Sgn(Vector3.Dot(f, right)), 0.04f) * Shake(0.25f, 0.7f, 9.7f);
        }
        // (pit box) 4a. the release: low from the fast lane ahead, by the pit wall; the car pulls out of the box towards us,
        //    the crew stepping away behind it
        void BoxOut(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            double uc = System.Math.Min(boxRun != null ? boxRun.BoxU + 44 : 0, lane.sExit - 2);
            lane.Pos(uc, lane.BaseLat(uc) - lane.side * 2.6, out double x, out double y);
            pos = new Vector3((float)x, 0f, (float)y); pos.y = GroundAt(pos, g.model.transform.position.y) + 1.05f;
            Vector3 subj = g.model.transform.position + Vector3.up * 0.5f;
            float dist = Mathf.Max(4f, (subj - pos).magnitude);
            fov = Fit(Mathf.Clamp(2f * Mathf.Atan(4.2f / dist) * Mathf.Rad2Deg, 12f, 48f));
            rot = Aim(pos, subj, fov, 0f, -0.05f) * Shake(0.12f, 0.6f, 29.3f);
        }
        // (pit box) 4b. the pit exit: on the track past the end of the lane, long lens back up the exit at the car
        void PitExit(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            Vector3 p = TrackAt(lane.sExit + 22.0, out _, out Vector3 n, out int i);
            float hw = (float)g.track.HalfWidth(i);
            pos = p + n * (lane.side * Mathf.Max(1f, hw - 3.2f)) + Vector3.up * 1.3f;
            Vector3 subj = g.model.transform.position + Vector3.up * 0.5f;
            float dist = Mathf.Max(4f, (subj - pos).magnitude);
            fov = Fit(Mathf.Clamp(2f * Mathf.Atan(4.8f / dist) * Mathf.Rad2Deg, 9f, 45f));
            rot = Aim(pos, subj, fov, 0f, -0.05f) * Shake(0.1f, 0.5f, 31.1f);
        }
        // 2b. on the grid: low at the front quarter, his car's nose in the foreground, he walks up to the cockpit
        void GridWalk(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            float e = Smooth(u);
            CarFrame(out Vector3 c, out Vector3 f, out float along);
            pos = c + f * (along + 4.6f - 0.4f * e) + outer * 2.9f; pos.y = Mathf.Max(c.y, GroundAt(pos, c.y)) + 0.55f;
            Vector3 cockpit = c + f * along + Vector3.up * 0.8f;
            Vector3 subj = Vector3.Lerp(rigPos + Vector3.up * 1.15f, cockpit, 0.2f);
            Vector3 right = Vector3.Cross(Vector3.up, -f);
            fov = Fit(30f);
            rot = Aim(pos, subj, fov, 0.25f * Sgn(Vector3.Dot(outer, right)), 0.05f) * Shake(0.3f, 0.85f, 13.3f);
        }
        // 3a. at the cockpit edge: across the cockpit from the other side (above the halo), long lens, a slow push in
        void Edge(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            float e = Smooth(u);
            CarFrame(out Vector3 c, out Vector3 f, out float along);
            pos = c + f * (along + 0.9f - 0.2f * e) - outer * (1.75f - 0.15f * e); pos.y = Mathf.Max(c.y, GroundAt(pos, c.y)) + 1.45f;
            Vector3 subj = rigPos + Vector3.up * 1.55f;
            Vector3 right = Vector3.Cross(Vector3.up, outer);
            fov = Fit(Mathf.Lerp(32f, 28f, e));
            rot = Aim(pos, subj, fov, -0.2f * Sgn(Vector3.Dot(f, right)), 0.28f) * Shake(0.3f, 0.8f, 17.9f);
        }
        // 3b. his eyes: looking down at the wheel, visor down (Gui), ignition, then up to the road
        void Onboard(float lt, out Vector3 pos, out Quaternion rot, out float fov)
        {
            var tr = g.model.transform;
            float sy = g.seatUpCm * 0.01f, sz = g.seatFwdCm * 0.01f;
            pos = tr.TransformPoint(new Vector3(0f, 0.76f + sy, 0.30f + sz));
            Vector3 ahead = tr.TransformPoint(new Vector3(0f, 0.76f + sy, 30f));
            Vector3 wheel = g.model.SteerWheel != null ? g.model.SteerWheel.position + tr.up * 0.03f : tr.TransformPoint(new Vector3(0f, 0.62f, 0.8f));
            Quaternion down = Quaternion.LookRotation(wheel - pos, tr.up), fwd = Quaternion.LookRotation(ahead - pos, Vector3.up);
            float k = Smooth((lt - 1.9f) / 1.3f);
            float ign = ignAt < 0f ? 0f : Mathf.Clamp01((T - ignAt - 0.3f) / 0.2f) * (1f - 0.6f * Mathf.Clamp01((T - ignAt - 0.6f) / 1.5f));
            float vib = ign * 0.07f * (Mathf.PerlinNoise(T * 31f, 3.3f) - 0.5f);
            rot = Quaternion.Slerp(down, fwd, k) * Shake(0.12f, 1.1f, 21.1f) * Quaternion.Euler(vib * 10f, vib * 6f, vib * 4f);
            fov = g.fovCockpit;
        }
        // 4. low TV shot down the grid: the track edge 9 m ahead of the player on his side, long lens back at his car
        //    with the field behind it, a slow creep inwards and zoom in
        void GridTv(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            float e = Smooth(u);
            Vector3 p = TrackAt(playerS + 9.5, out _, out Vector3 n, out int i);
            float hw = (float)g.track.HalfWidth(i), side = playerLat >= 0 ? 1f : -1f;
            float lat = Mathf.Min(Mathf.Max(hw - 0.9f - 0.5f * e, (float)StartGrid.Lateral + 1.3f), hw - 0.2f);
            pos = p + n * (side * lat) + Vector3.up * (0.62f - 0.04f * e);
            Vector3 subj = g.model.transform.position + Vector3.up * 0.5f;
            Vector3 right = Vector3.Cross(Vector3.up, subj - pos).normalized;
            fov = Fit(Mathf.Lerp(25f, 21.5f, e));
            rot = Aim(pos, subj, fov, 0.22f * Sgn(Vector3.Dot(outer, right)), -0.1f) * Shake(0.07f, 0.5f, 25.7f);
        }
        // 4b. (chase views) the hand-over: up and over the car into the chase camera's exact pose
        void Crane(float u, out Vector3 pos, out Quaternion rot, out float fov)
        {
            u = Mathf.Clamp01(u); float e = UiKit.EaseInOut(u);
            GridTv(1f, out Vector3 p0, out Quaternion r0, out float f0);
            ChasePose(out Vector3 p3, out Quaternion r3, out float f3);
            Vector3 cf = Flat(g.model.transform.forward);
            Vector3 p1 = p0 + Vector3.up * 5f + (p3 - p0) * 0.15f, p2 = p3 - cf * 9f + Vector3.up * 4f;
            float w = 1f - e;
            pos = w * w * w * p0 + 3f * w * w * e * p1 + 3f * w * e * e * p2 + e * e * e * p3;
            Quaternion onCar = Quaternion.LookRotation(g.model.transform.position + Vector3.up * 0.6f - pos, Vector3.up);
            rot = Quaternion.Slerp(Quaternion.Slerp(r0, onCar, Smooth(u / 0.35f)), r3, Smooth((u - 0.55f) / 0.45f));
            fov = Mathf.Lerp(f0, f3, Smooth(u));
        }
        // GameManager.LateUpdate's (rigid) chase / far chase pose with the car at rest
        void ChasePose(out Vector3 pos, out Quaternion rot, out float fov)
        {
            var tr = g.model.transform;
            float back = userCam == 0 ? 6.2f : 10f, up = userCam == 0 ? 2f : 3.4f;
            Vector3 b = tr.position, upV = tr.up, fw = tr.forward;
            pos = b + upV * up - fw * back;
            rot = Quaternion.LookRotation(b + upV * 0.7f + fw * 6f - pos, Vector3.up);
            fov = Fit(g.fovChase);
        }

        // ---------------------------------------------------------------- camera helpers
        // look at subject so that it sits at (nx, ny) on the screen (-1..1, + = right / up): rule of thirds = +-0.33
        Quaternion Aim(Vector3 pos, Vector3 subject, float fov, float nx, float ny)
        {
            Vector3 d = subject - pos; if (d.sqrMagnitude < 1e-6f) d = Vector3.forward;
            float tv = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), th = tv * Mathf.Max(0.5f, g.cam.aspect);
            float yaw = Mathf.Atan(th * -nx) * Mathf.Rad2Deg, pitchUp = Mathf.Atan(tv * -ny) * Mathf.Rad2Deg;
            return Quaternion.LookRotation(d, Vector3.up) * Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(-pitchUp, Vector3.right);
        }
        // handheld / drone float: smooth noise (degrees, Hz)
        Quaternion Shake(float amp, float hz, float seed)
        {
            float t = T * hz;
            float p = (Mathf.PerlinNoise(t, seed) - 0.5f) * 2f * amp;
            float y = (Mathf.PerlinNoise(seed + 13.1f, t * 0.9f) - 0.5f) * 2.4f * amp;
            float r = (Mathf.PerlinNoise(t * 0.7f, seed + 31.7f) - 0.5f) * 1.2f * amp;
            return Quaternion.Euler(p, y, r);
        }
        // a field of view framed for 16:9 keeps its side-to-side view on wider phones (as the chase camera does)
        float Fit(float fov)
        {
            float a = Mathf.Max(0.5f, g.cam.aspect);
            return a > 16f / 9f ? 2f * Mathf.Rad2Deg * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * (16f / 9f) / a) : fov;
        }
        static Vector3 Above(Vector3 p, float clear) { if (WorldBuilder.Height != null) p.y = Mathf.Max(p.y, WorldBuilder.H(p.x, p.z) + clear); return p; }
        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward; }
        static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        static float Sgn(float v) => v >= 0f ? 1f : -1f;

        // the player's car: ground point, flat forward, and how far ahead of it the cockpit (eye point) is (m)
        void CarFrame(out Vector3 c, out Vector3 f, out float along)
        {
            var tr = g.model.transform;
            c = tr.position; f = Flat(tr.forward);
            along = Vector3.Dot(tr.TransformPoint(new Vector3(0f, 0.76f, 0.30f)) - c, f);
        }

        // a point of the centreline at lap distance s (any s; it wraps), its direction and the track normal (flat, unit)
        Vector3 TrackAt(double s, out Vector3 tangent, out Vector3 normal, out int idx)
        {
            var t = g.track; double fi = s / t.ds;
            int i0 = (int)System.Math.Floor(fi); float a = (float)(fi - i0);
            i0 = ((i0 % t.N) + t.N) % t.N; int i1 = (i0 + 1) % t.N;
            var p = t.pts[i0]; var q = t.pts[i1];
            float x = (float)(p.x + (q.x - p.x) * a), z = (float)(p.y + (q.y - p.y) * a);
            tangent = Flat(new Vector3((float)(p.tx + (q.tx - p.tx) * a), 0f, (float)(p.ty + (q.ty - p.ty) * a)));
            normal = Flat(new Vector3((float)(p.nx + (q.nx - p.nx) * a), 0f, (float)(p.ny + (q.ny - p.ny) * a)));
            idx = a < 0.5f ? i0 : i1;
            return new Vector3(x, WorldBuilder.H(x, z), z);
        }

        // the corner to open on: the strongest sustained bend (curvature averaged over +-50 m, so chicanes cancel out)
        // in the second half of the lap, the later the better (it leads onto the straight and the grid)
        static double ShowcaseCorner(Track t, out float sign)
        {
            int n = t.N, w = Mathf.Max(1, (int)(50.0 / t.ds)), step = Mathf.Max(1, (int)(4.0 / t.ds));
            double best = -1, bestS = t.length * 0.85; sign = 1f;
            for (int i = n / 2; i < (int)(n * 0.97); i += step)
            {
                double c = 0; int cnt = 0;
                for (int k = -w; k <= w; k += step) { c += t.pts[((i + k) % n + n) % n].curv; cnt++; }
                c /= System.Math.Max(1, cnt);
                double score = System.Math.Abs(c) * (0.6 + (double)i / n);
                if (score > best) { best = score; bestS = i * t.ds; sign = c >= 0 ? 1f : -1f; }
            }
            return bestS;
        }

        // ---------------------------------------------------------------- overlay (OnGUI, instead of the HUD)
        public void Gui()
        {
            if (finished || Event.current.type != EventType.Repaint) return;
            var s = shots[ShotAt(T)]; float lt = T - s.t0;
            float W = Screen.width, H = Screen.height;

            // the visor coming down (onboard): a tinted pane slides from the top, its lip catching the light
            if (s.k == K.Cockpit)
            {
                float c = UiKit.EaseInOut((lt - 0.2f) / 0.6f), tint = 0.3f * (1f - Mathf.Clamp01((lt - 2.4f) / 1.0f));
                if (c > 0f && tint > 0.004f)
                {
                    var col = new Color(0.03f, 0.04f, 0.075f, tint);
                    float edge = H * c;
                    UiKit.Rect(new Rect(0, 0, W, edge), col);
                    if (c < 0.999f)
                    {
                        UiKit.Gradient(new Rect(0, edge, W, UiKit.U(30)), col, new Color(col.r, col.g, col.b, 0f));
                        UiKit.Rect(new Rect(0, edge - UiKit.U(1.6f), W, UiKit.U(1.6f)), new Color(1f, 1f, 1f, 0.16f));
                    }
                }
            }

            // letterbox bars (they open up during the hand-over)
            float bars = 1f;
            if (s.k == K.GridTv && crane) bars = 1f - Smooth((lt - TvMain) / (Handover * 0.8f));
            if (skipping) bars *= 1f - skipT / SkipFade;
            float lb = UiKit.U(18f) * bars;
            if (lb > 0.5f) { UiKit.Rect(new Rect(0, 0, W, lb), Color.black); UiKit.Rect(new Rect(0, H - lb, W, lb), Color.black); }

            // the caption of this shot (lower third)
            if (s.cap != null)
            {
                float end = s.k == K.GridTv ? TvMain : s.dur;
                Caption(s.cap, lt - s.cap.from, end - s.cap.from, lb);
            }

            // tap to skip
            if (T > 0.6f && !skipping && !g.shotIntro)
            {
                float a = Mathf.Clamp01((T - 0.6f) / 0.5f) * (s.k == K.GridTv && crane ? 1f - Mathf.Clamp01((lt - TvMain) / 0.5f) : 1f);
                if (a > 0.01f)
                {
                    var safe = UiKit.Safe;
                    float tw = UiKit.Width(tapTxt, UiKit.Semi, 7.2f), w = tw + UiKit.U(34), h = UiKit.U(22);
                    var r = new Rect(safe.xMax - UiKit.U(16) - w, safe.yMax - UiKit.U(12) - h - lb, w, h);
                    UiKit.PushAlpha(a);
                    UiKit.Rect(r, new Color(0f, 0f, 0f, 0.42f), 11f);
                    UiKit.Outline(r, new Color(1f, 1f, 1f, 0.14f), 11f, 0.7f);
                    UiKit.Text(new Rect(r.x + UiKit.U(11), r.y, tw, h), tapTxt, UiKit.Semi, 7.2f, new Color(1f, 1f, 1f, 0.85f));
                    UiKit.Icon(new Rect(r.xMax - UiKit.U(19), r.center.y - UiKit.U(5), UiKit.U(10), UiKit.U(10)), UiKit.IconNext, new Color(1f, 1f, 1f, 0.85f));
                    UiKit.PopAlpha();
                }
            }

            // black: in from the loading screen, the dip into the cockpit, a skip
            float black = 1f - Mathf.Clamp01(T / 0.6f);
            if (dipAt > 0f) black = Mathf.Max(black, 1f - Mathf.Abs(T - dipAt) / DipHalf);
            if (cutAt > 0f) black = Mathf.Max(black, 1f - Mathf.Abs(T - cutAt) / DipHalf);
            if (skipping) black = Mathf.Max(black, skipT / SkipFade);
            if (black > 0.004f) UiKit.Rect(new Rect(0, 0, W, H), new Color(0f, 0f, 0f, Mathf.Clamp01(black)));
        }

        // F1-TV lower third, bottom left: a small red-ticked eyebrow, then the title bar (dark, team-colour edge) over the
        // sub bar; the driver card has his number on a team-colour block in front. Wipes in from the left.
        void Caption(Cap c, float lt, float dur, float lb)
        {
            if (dur < 1.2f) return;
            float wipe = UiKit.EaseOut((lt - 0.25f) / 0.45f), fade = Mathf.Clamp01((dur - 0.12f - lt) / 0.3f);
            if (wipe <= 0.01f || fade <= 0.01f) return;
            var safe = UiKit.Safe;
            string title = UiKit.Upper(c.title), sub = UiKit.Upper(c.sub), eye = UiKit.Caps(c.eyebrow);
            const float TitleSz = 12.5f, SubSz = 7.6f, EyeSz = 6.6f, NumSz = 15f;
            float pad = UiKit.U(10), h1 = UiKit.U(27), h2 = UiKit.U(17), eh = UiKit.U(13), gap = UiKit.U(3);
            bool card = c.block != null;
            float box = card ? Mathf.Max(h1 + h2, UiKit.Width(c.block, UiKit.Black, NumSz) + UiKit.U(14)) : 0f;
            float w = Mathf.Max(UiKit.Width(title, UiKit.Black, TitleSz), UiKit.Width(sub, UiKit.Semi, SubSz)) + 2f * pad + UiKit.U(4);
            w = Mathf.Max(w, UiKit.Width(eye, UiKit.Bold, EyeSz) + UiKit.U(8) - box);
            float full = box + w, hAll = eh + gap + h1 + h2;
            float x = safe.x + UiKit.U(18), y = safe.yMax - lb - UiKit.U(16) - hAll;
            Color team = card ? suit : UiKit.Accent;
            UiKit.PushAlpha(fade);
            GUI.BeginGroup(new Rect(x, y, full * wipe + 1f, hAll + UiKit.U(4)));
            UiKit.Rect(new Rect(0, UiKit.U(2), UiKit.U(3), eh - UiKit.U(4)), UiKit.AccentHi);
            UiKit.TextShadow(new Rect(UiKit.U(7), 0, full, eh), eye, UiKit.Bold, EyeSz, new Color(1f, 1f, 1f, 0.92f));
            float y1 = eh + gap;
            if (card)
            {
                var br = new Rect(0, y1, box, h1 + h2);
                UiKit.Rect(br, team);
                UiKit.Gradient(br, new Color(1f, 1f, 1f, 0.10f), new Color(0f, 0f, 0f, 0.18f));
                UiKit.Rect(new Rect(0, y1 + h1 + h2 - UiKit.U(3), box, UiKit.U(3)), trimCol);
                UiKit.Text(br, c.block, UiKit.Black, NumSz, Lum(team) > 0.6f ? UiKit.Ink : Color.white, TextAnchor.MiddleCenter);
            }
            UiKit.Rect(new Rect(box, y1, w, h1), new Color(0.035f, 0.04f, 0.055f, 0.9f));
            UiKit.Rect(new Rect(box, y1, UiKit.U(3), h1 + h2), card ? new Color(1f, 1f, 1f, 0.85f) : team);
            UiKit.Text(new Rect(box + pad, y1, w - pad, h1), title, UiKit.Black, TitleSz, Color.white);
            UiKit.Rect(new Rect(box, y1 + h1, w, h2), new Color(0.13f, 0.14f, 0.17f, 0.88f));
            UiKit.Text(new Rect(box + pad, y1 + h1, w - pad, h2), sub, UiKit.Semi, SubSz, new Color(0.86f, 0.88f, 0.92f, 1f));
            GUI.EndGroup();
            UiKit.PopAlpha();
        }

        // ---------------------------------------------------------------- the end: everything back as it was
        // (full: the formation lap follows, GameManagerFormation.cs)
        void End() { bool f = full; var gm = g; gm.PreRaceAbort(); if (f) gm.FormBegin(); }

        // played out, skipped, dropped by a new start or an error: all restore the same things (GameManager.PreRaceAbort)
        public void Finish()
        {
            if (finished) return;
            finished = true;
            try
            {
                // still in the pit box / lane (a skip, a restart): the car to its grid slot, the crew away
                if (crewOn) { crewOn = false; if (g.pitCrew != null && g.pitCrew.Owns(-1)) g.pitCrew.Stop(); }
                if (boxRun != null) { boxRun.Cancel(); boxRun = null; g.ParkOnGrid(); }
                for (int i = 0; i < hiddenDrivers.Count; i++) if (hiddenDrivers[i] != null) hiddenDrivers[i].enabled = true;
                hiddenDrivers.Clear();
                for (int i = 0; i < darkWheels.Count; i++) if (darkWheels[i] != null && darkWheels[i].Mat != null) darkWheels[i].Mat.SetTexture(DashId, darkTex[i]);
                darkWheels.Clear(); darkTex.Clear();
                if (g.model != null && g.model.WheelRig != null && g.model.WheelRig.Mat != null) g.model.WheelRig.Mat.SetVector(LedId, Vector4.zero);
                g.camMode = userCam;
                g.tvWasOn = true; g.camVel = Vector3.zero; g.tvCur = -1; g.tvCarIdx = -1; // the game's camera: a clean start, no swoop
                if (g.cam != null)
                {
                    if (userCam == 2) g.cam.fieldOfView = g.fovCockpit;
                    else if (userCam == 3) g.cam.fieldOfView = g.fovCockpit + 11f;
                    else if (userCam != TvMode) g.cam.fieldOfView = Fit(g.fovChase);
                }
                g.ApplyAudio(); // the engine volume
                g.gridThr = 0;
                g.FpsWindowReset(2f); g.fpsAvg = g.FpsTarget; // the flyover's frame rate is not held against the race
                g.ClearRecording(); // the replay starts at the start, not with the cars standing on the grid
            }
            catch (System.Exception e) { Debug.LogWarning("PreRace finish: " + e.Message); }
            DestroyRig();
        }

        void DestroyRig()
        {
            if (blob != null) { blob.Destroy(); blob = null; }
            if (rig != null) { rig.Destroy(); rig = null; }
            if (root != null) { Object.Destroy(root); root = null; }
        }
    }
}
