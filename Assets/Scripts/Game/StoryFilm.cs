// StoryFilm.cs — the career story told with the game's own 3D instead of text cards (Adi: "show the story through
// visuals ... do the 3D story using the characters and assets in the game itself, not generating anything").
// Another part of GameManager (a partial class; the film itself is the nested StoryFilm class, like PreRace / PostRace).
//  * What plays: the career page (SeasonUI.FilmStep) hands over each run of story cards that have a scene (Story.cs:
//    StoryCard.scene) through StoryFilms.Request. One card = one beat (6.5-13 s) = one short real-time scene on the
//    circuit that is loaded behind the menu, with the card's own lines as big minimal captions (Paper-style kicker, a
//    heavy title, the body, a chapter's red round number), letterbox bars, a dip to black between beats. The goals
//    cards stay cards. A tap = the next beat; SKIP (top right) or back = the end of the film.
//    Setting: Settings > GAME "Story" (pref "storyMode"): Film (default) / Cards (the old cards) / Off (goals only).
//  * The scenes (every car and person is posed by script as a function of the beat's time: nothing is simulated,
//    the physics is not touched):
//      SCrash       PROLOGUE, twenty years ago: rain and a cold dusk grade. Teo Varga's navy car (white centre stripe) and
//                   the silver-black Argent car side by side into the circuit's showcase corner (the longest bend in the
//                   second half of the lap, as PreRace). Long lens from the outside of the entry as they come; then low
//                   on the inside in slow motion (0.3x): the Argent car dives up the inside, contact (a flash, a jolt);
//                   then high and wide over the outside wall: Teo's car spins off into the barrier, two marshals behind
//                   it wave yellow flags, the Argent car drives away.
//      SAftermath   Teo's car stopped at the wall in the rain, Teo (the race-suited driver, navy suit, white helmet)
//                   beside it looking at it, the marshals waving; the camera cranes up and away.
//      SHelmet      "Last winter": today, dry. The player's car in the team's pit box, the player (cap, team suit)
//                   standing beside the cockpit looking into it; wide from the lane, then a slow push-in on him.
//      SRival       the Argent garage: their car in the box, Viktor (driver_02; Nico = driver_01 in the junior tiers) in
//                   Argent black and silver, arms folded, looking into the lens; mechanics at the car; a slow push-in.
//      SPitLane     the player's car rolls down the pit lane; Mia (10: a fan body scaled to 0.78, red and yellow) waves
//                   at it from the team's garage front with an adult clapping; from behind her, then from the pit wall
//                   with the car crossing the frame.
//      SBriefing    Lena (team principal: a Rocketbox engineer, headset, team shirt) talks to the player at the garage
//                   front, mechanics kneel at the wheels of the car in the box; a two-shot, then over his shoulder.
//      SMia         Mia at the garage front waving a small flag in the team's colours into the lens.
//      SMiaCheer    Mia jumping, the crew cheering and clapping round the car; wide, then close on Mia.
//      SEstablish   a chapter: the team's box from a descending crane, mechanics at the car, an engineer behind it.
//      SRivalLeaves the Argent car pulls out of its box and drives off down the pit lane past a low camera.
//      SMemory      a memory of Teo: his navy car alone through the showcase corner (cold grade, slow).
//      SDebrief     Lena, arms folded, and the player, head down, at the garage front; a slow push-in.
//      SCelebrate   the season's end: the player, Lena, Mia and the crew celebrating at the box, a low orbit.
//    The garage scenes use the player's box (PitGarage.BayOrder: the box nearest the start line, as PitCrew) and the
//    Argent box (PitGarage.TeamBay team 0) of the loaded circuit; without a pit building the same layouts stand on the
//    main straight. The player's own car model is used (posed, its driver hidden while he is on foot); the other cars
//    are the rivals' merged LOD cars (CarModel.CreateFromAsset("Car/car_lod")) in the Teo / Argent liveries. People
//    are People.Rig (People2 / People3 / Mixamo clips: idle, wave, clap, talk, celebrate, arms folded, kneel).
//  * Cost: runs only in the menu (the career page) or in shot mode; while it runs the game is paused as on any menu
//    page, the studio is released and the main camera draws the circuit (race frame rate), the rivals left from the
//    last race are hidden. Each beat builds only what it shows (<= 7 rigs, <= 2 extra cars, a shadow mesh, flags) under
//    the dip to black and destroys it at the next; the rain weather of the flashback is set and given back. Finish
//    gives everything back (rivals, the car's look, weather, camera). No per-frame allocation (captions built once,
//    measured once per beat and screen size). The fans' pack (people3p, as the podium) is loaded once if Mia appears.
// Shot mode (DebugShots.cs): story=prologue|welcome|pre|end|<scene name> [round=N] t=t1,t2,... freezes the film at each
// time (s from its start). Prologue (5 beats, rookie): crash 0-12.5 (slow motion 2.4-6.4, contact 3.7, wall 6.4-),
// aftermath 12.5-19, helmet 19-26, rival 26-33, pit lane 33-41.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

// the setting and the hand-over between the career page (SeasonUI) and the film (GameManager.StoryFilm)
public static class StoryFilms
{
    public const int ModeFilm = 0, ModeCards = 1, ModeOff = 2;
    public static readonly string[] ModeNames = { "Film", "Cards", "Off" };
    static int mode = -1;
    public static int Mode
    {
        get { if (mode < 0) mode = Mathf.Clamp(PlayerPrefs.GetInt("storyMode", ModeFilm), 0, ModeNames.Length - 1); return mode; }
        set { mode = Mathf.Clamp(value, 0, ModeNames.Length - 1); PlayerPrefs.SetInt("storyMode", mode); }
    }

    internal static List<StoryCard> pending; // asked for, not started yet (GameManager picks it up in its next OnGUI)
    internal static bool running;            // playing
    static int result;                       // 1 played (or skipped), 2 could not play
    public static bool Busy => pending != null || running;

    // the cards [from, to) as one film: true = it will play (the game draws it from the next frame on)
    public static bool Request(List<StoryCard> cards, int from, int to)
    {
        if (Busy || cards == null || from < 0 || to > cards.Count || to <= from) return false;
        pending = cards.GetRange(from, to - from); result = 0;
        return true;
    }
    internal static void Done(bool played) { pending = null; running = false; result = played ? 1 : 2; }
    // the career page, once the film is over: true = played, false = it could not (show the cards instead)
    public static bool TakeResult() { bool ok = result == 1; result = 0; return ok; }

    // Story = Off: only the goals cards (the weekend's goals and the result) are left
    public static List<StoryCard> Filter(List<StoryCard> l)
    {
        if (l == null || Mode != ModeOff) return l;
        var o = new List<StoryCard>();
        for (int i = 0; i < l.Count; i++) if (l[i].kind == StoryCard.Goals) o.Add(l[i]);
        return o.Count > 0 ? o : null;
    }
}

public partial class GameManager
{
    StoryFilm story; string storyShotName; int storyShotRound = -1;

    // LateUpdate, first thing: true = the film placed the camera this frame
    bool StoryCamera()
    {
        if (story == null) return false;
        bool on;
        try { on = story.Frame(); }
        catch (System.Exception e) { Debug.LogWarning("StoryFilm: " + e.Message); on = false; }
        if (!on) StoryEnd();
        return on;
    }

    // OnGuiBody, before the menu: starts a film the career page asked for; true = the film drew the screen
    bool StoryGui()
    {
        if (story == null && StoryFilms.pending != null)
        {
            var l = StoryFilms.pending; StoryFilms.pending = null; StoryFilms.running = true;
            StoryBegin(l, false);
            if (story == null) StoryFilms.Done(false);
        }
        if (story == null) return false;
        try { story.Gui(); }
        catch (System.Exception e) { Debug.LogWarning("StoryFilm GUI: " + e.Message); }
        return true;
    }

    void StoryBegin(List<StoryCard> l, bool shot)
    {
        if (story != null) StoryEnd();
        if (track == null || model == null || cam == null || l == null || l.Count == 0) return;
        try
        {
            ui.ReleaseStudio(); // the menu's 3D off, the main camera on (the menu makes the studio again afterwards)
            cam.enabled = true;
            var f = new StoryFilm(this, l, shot);
            if (f.Valid) story = f; else f.Finish();
        }
        catch (System.Exception e) { Debug.LogWarning("StoryFilm: " + e.Message); story = null; }
    }

    void StoryEnd()
    {
        var s = story; story = null;
        if (s == null) return;
        bool page = !s.Shot; int seen = s.Shown;
        s.Finish();
        if (page) StoryFilms.Done(seen > 0);
    }

    // shot mode (DebugShots.cs): the film `name` frozen at t s
    internal void ShotStory(string name, float t, int round)
    {
        if (story == null || storyShotName != name || storyShotRound != round)
        {
            StoryEnd();
            storyShotName = name; storyShotRound = round;
            List<StoryCard> l = null;
            try { l = StoryShotCards(name, round); } catch (System.Exception e) { Debug.LogWarning("StoryFilm shot: " + e.Message); }
            StoryBegin(l, true);
        }
        if (story != null) story.ShotT = t;
    }

    static List<StoryCard> StoryShotCards(string name, int round)
    {
        if (!Season.Active) Season.Load();
        if (!Season.Active)
        {
            int team = 0;
            for (int k = 0; k < Season.Teams.Count; k++) if (Season.Midfield(k)) { team = k; break; }
            Season.New(team);
        }
        if (round > 0) Season.Data.round = Mathf.Clamp(round - 1, 0, Season.Rounds - 1);
        switch (name)
        {
            case "prologue": return Story.Prologue();
            case "welcome": return Story.Welcome();
            case "pre": return Story.PreRound();
            case "end": return Story.SeasonEnd();
        }
        int sc = System.Array.IndexOf(StoryFilm.SceneNames, name);
        if (sc <= 0) return null;
        var c = new StoryCard { kind = StoryCard.Chapter, scene = sc, kicker = "Scene  ·  " + name, title = "A short line of the story, as big as a chapter title.", body = "The body under it comes in a moment later." };
        if (sc == StoryCard.SRival || sc == StoryCard.SRivalLeaves) { c.kind = StoryCard.Quote; c.who = Season.Viktor ? StoryCard.Viktor : StoryCard.Nico; c.kicker = "Viktor Raske  ·  seven-time champion"; c.title = "“Teo Varga? I don't remember him.”"; }
        else if (sc == StoryCard.SMia || sc == StoryCard.SMiaCheer) { c.kind = StoryCard.Message; c.who = StoryCard.Mia; c.kicker = "Message  ·  Mia Varga, 10"; c.title = "“YOU WERE ON THE BIG SCREEN!! Everyone at the café cheered.”"; c.body = null; }
        else if (sc == StoryCard.SBriefing || sc == StoryCard.SDebrief) { c.kind = StoryCard.Message; c.who = StoryCard.Boss; c.kicker = "Message  ·  Lena Brandt, team principal"; c.title = "“Clean race, clean car. The rest will come.”"; c.body = null; }
        else if (sc == StoryCard.SEstablish) c.figure = "02";
        return new List<StoryCard> { c };
    }

    // =====================================================================================================================
    sealed class StoryFilm
    {
        public static readonly string[] SceneNames = { "", "crash", "aftermath", "helmet", "rival", "pitlane", "briefing", "mia", "miacheer", "establish", "rivalleaves", "memory", "debrief", "celebrate" };
        // the shortest beat per scene (s); a long caption makes it longer
        static readonly float[] MinDur = { 6f, 12.5f, 6.5f, 7f, 7f, 8f, 7f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 7f };
        const float DipIn = 0.45f, DipOut = 0.35f, SkipFade = 0.3f, FigSz = 64f, BodySz = 11.5f;
        static bool Flashback(int sc) => sc == StoryCard.SCrash || sc == StoryCard.SAftermath || sc == StoryCard.SMemory;
        static bool Rain(int sc) => sc == StoryCard.SCrash || sc == StoryCard.SAftermath;
        static readonly Color TeoHelmet = new Color(0.93f, 0.93f, 0.95f);

        readonly GameManager g;
        readonly List<StoryCard> cards = new List<StoryCard>();
        readonly float[] t0, dur, bodyAt;
        readonly float total;
        public readonly bool Shot; public float ShotT;
        public bool Valid => cards.Count > 0;
        float T, skipT; int beat = -1, shown; bool finished, skipAll;
        public int Shown => shown;

        // captions: measured once per beat and screen size
        int measKey = -1; float titleSz = 24f, titleH, bodyH, capW, figW;
        readonly string skipTxt = UiKit.Caps("Skip");

        // the stages: the player's box and the Argent box (x along the lane, z towards the garage), or the main straight
        Vector3 pC, pA, pG, rC, rA, rG; float pGd, rGd;
        // the flashback corner and the two cars' lines through it (L: m from the centre line, + = the outside)
        double cS, sStart; float cSign, cHw, wallL, lOut, lApex, lTc, lA;
        const float V0 = 58f, Dec = 10f, VMin = 30f, TBr = 2.8f, TC = 4.0f, KSlide = 1.2f;
        readonly Livery team, argent, teo, mia, marshal;
        // given back at the end
        readonly int savedWeather;
        CarModel modelFor; SkinnedMeshRenderer dmR; bool liveryChanged;

        // the beat's things
        GameObject root; People.Blobs blobs;
        readonly List<Actor> actors = new List<Actor>(8);
        readonly List<CarModel> cars = new List<CarModel>(3);
        Actor drv, lena, kid, adult, riv, teoA, m1, m2, m3, mar1, mar2;
        CarModel carT, carA;
        bool useModel, driverIn;
        Vector3 camPos;
        readonly People.PoseBuf pa = new People.PoseBuf(), pb = new People.PoseBuf();
        // shared by the film: the flag mesh and its materials
        Mesh flagMesh; Material poleMat, yellowMat, teamFlagMat;

        sealed class Actor
        {
            public People.Rig rig; public int clip = People.MIdle, clip2 = -1; public float mix, speed = 1f, phase, shadow = 0.65f, lodK = 0.75f;
            public Vector3 pos, look; public float yaw, lookW; public Transform flag;
        }

        public StoryFilm(GameManager owner, List<StoryCard> list, bool shot)
        {
            g = owner; Shot = shot;
            for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].scene > 0 && list[i].scene < StoryCard.SCount) cards.Add(list[i]);
            int n = cards.Count;
            t0 = new float[n]; dur = new float[n]; bodyAt = new float[n];
            for (int i = 0; i < n; i++)
            {
                var c = cards[i]; int sc = c.scene;
                int tw = Words(c.title), bw = Words(c.body);
                float d = Mathf.Clamp(2.4f + 0.3f * (tw + bw), MinDur[sc], 14f);
                t0[i] = total; dur[i] = d; total += d;
                float ba = sc == StoryCard.SCrash ? 6.6f : sc == StoryCard.SAftermath ? 2.4f : 1.2f + 0.28f * tw;
                bodyAt[i] = Mathf.Min(ba, d - 2.6f);
            }
            savedWeather = Weather.Index;
            bool season = Season.Active && Season.Data != null && Season.Data.team >= 0 && Season.Data.team < Season.Teams.Count;
            team = season ? Livery.ForTeam(Season.Data.team, Season.TeamColour(Season.Data.team)) : Livery.Player[Mathf.Clamp(g.liveryIdx, 0, Livery.Player.Length - 1)];
            argent = Livery.Teams[0];
            teo = new Livery("Varga", new Color(0.03f, 0.06f, 0.22f), new Color(0.93f, 0.93f, 0.95f), new Color(0.93f, 0.93f, 0.95f), 3); // navy, white centre stripe
            mia = new Livery("Mia", new Color(0.86f, 0.08f, 0.06f), new Color(1f, 0.80f, 0.10f), new Color(0.05f, 0.05f, 0.06f));
            marshal = new Livery("Marshal", new Color(1f, 0.42f, 0.05f), new Color(0.95f, 0.95f, 0.95f), new Color(0.1f, 0.1f, 0.1f));
            Stages();
            Corner();
        }

        static int Words(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int w = 1; for (int i = 0; i < s.Length; i++) if (s[i] == ' ') w++;
            return w;
        }

        // ---------------------------------------------------------------- the stages
        void Stages()
        {
            var tk = g.track;
            var bays = Trackside.Bays;
            if (bays.Count > 0)
            {
                var order = PitGarage.BayOrder(tk.N);
                int pbI = order[0], rbI = PitGarage.TeamBay(order, 0); // the player's box, Argent's (team 0)
                Bay(bays[pbI], out pC, out pA, out pG, out pGd);
                Bay(bays[rbI], out rC, out rA, out rG, out rGd);
                return;
            }
            // no pit building: on the main straight past the line, the people at its edge
            Straight(40.0, out pC, out pA, out pG, out pGd);
            Straight(110.0, out rC, out rA, out rG, out rGd);
        }
        static void Bay(Trackside.PitBay b, out Vector3 c, out Vector3 a, out Vector3 gz, out float gd)
        {
            gz = PrFlat(b.side);
            a = PrFlat(b.fwd - gz * Vector3.Dot(b.fwd, gz));
            c = b.centre; c.y = WorldBuilder.H(c.x, c.z);
            gd = Mathf.Clamp(b.garageDist, 2.5f, 12f);
        }
        void Straight(double s, out Vector3 c, out Vector3 a, out Vector3 gz, out float gd)
        {
            Vector3 p = TrackAt(s, out a, out Vector3 n, out int i);
            float hw = (float)g.track.HalfWidth(i);
            gz = n; c = p + n * Mathf.Max(0f, hw - 3.2f); c.y = WorldBuilder.H(c.x, c.z);
            gd = 3.2f;
        }
        // a point of a stage: x m along the lane, z m towards the garage, on the ground
        Vector3 SP(bool rv, float x, float z)
        {
            Vector3 p = (rv ? rC : pC) + (rv ? rA : pA) * x + (rv ? rG : pG) * z;
            p.y = WorldBuilder.H(p.x, p.z);
            return p;
        }

        // ---------------------------------------------------------------- the flashback corner
        void Corner()
        {
            var tk = g.track;
            cS = ShowcaseCorner(tk, out cSign);
            TrackAt(cS, out _, out _, out int ci);
            cHw = (float)tk.HalfWidth(ci);
            wallL = Mathf.Clamp((float)g.wallAt - 1.2f, cHw + 1.2f, cHw + 9f);
            lOut = 0.55f * cHw; lApex = -0.6f * cHw;
            lTc = LT(TC); lA = lTc - 1.95f;
            sStart = cS - 12.0 - S(TC);
        }
        static float S(float tau) => tau < TBr ? V0 * tau - 0.5f * Dec * tau * tau : V0 * TBr - 0.5f * Dec * TBr * TBr + VMin * (tau - TBr);
        float LT(float tau) => lOut - (lOut - lApex) * PrSmooth((tau - 3.0f) / 1.4f);
        // where a car is at scene time tau: lap distance, offset (+ = outside) and its spin (rad, + = the way the bend turns)
        void CrashAt(bool arg, float tau, out double s, out float L, out float spinYaw)
        {
            spinYaw = 0f;
            if (tau <= TC)
            {
                double sT = sStart + S(tau);
                if (!arg) { s = sT; L = LT(tau); return; }
                float k = PrSmooth(tau / TC);
                s = sT - (9f * (1f - k) - 1.0f * k);              // from behind Teo to a metre ahead of him at the contact
                L = Mathf.Lerp(lOut, lA, PrSmooth((tau - 0.8f) / 2.4f)); // out of his slipstream, up the inside
                return;
            }
            float u = tau - TC; double sTc = sStart + S(TC);
            if (!arg)
            {
                s = sTc + VMin * KSlide * (1f - Mathf.Exp(-u / KSlide));
                L = lTc + (wallL - lTc) * PrSmooth(u / 2.1f);
                spinYaw = 2.35f * UiKit.EaseOut(u / 1.9f) + 0.12f * Mathf.Exp(-u * 6f) * Mathf.Sin(u * 40f);
            }
            else
            {
                s = sTc + 1.0 + VMin * u + 2.0 * u * u;
                L = lA + 0.55f * PrSmooth(u / 0.5f) + (lOut * 0.8f - lA - 0.55f) * PrSmooth((u - 0.7f) / 2.6f);
                spinYaw = -0.10f * Mathf.Exp(-u * 3f) * Mathf.Sin(u * 18f);
            }
        }
        // Teo's lap alone (the memory): the same line, no contact, back out to the exit
        void MemoryAt(float tau, out double s, out float L)
        {
            s = sStart + S(tau) + (tau > 4.2f ? 4.0 * (tau - 4.2f) * (tau - 4.2f) : 0.0);
            L = tau < 4.4f ? LT(tau) : lApex + (lOut - lApex) * PrSmooth((tau - 4.4f) / 1.8f);
        }
        Vector3 Lane(double s, float L)
        {
            Vector3 p = TrackAt(s, out _, out Vector3 n, out _);
            p -= n * (cSign * L); p.y = WorldBuilder.H(p.x, p.z);
            return p;
        }
        Vector3 Outside(double s) { TrackAt(s, out _, out Vector3 n, out _); return -n * cSign; }
        // a car on its path: the direction of travel from the path (a step ahead), then its spin
        static void PoseCar(CarModel c, Vector3 p, Vector3 ahead, Vector3 fallback, float yaw, double s, bool brake)
        {
            if (c == null) return;
            Vector3 d = ahead - p; d.y = 0f;
            Vector3 f = d.sqrMagnitude > 1e-6f ? d.normalized : fallback;
            double h = System.Math.Atan2(f.z, f.x) + yaw;
            float spin = (float)(s / 0.33);
            c.Apply(p.x, p.z, h, 0, spin, spin, 0, 0);
            c.SetBrake(brake);
        }
        void PoseCrashCar(CarModel c, bool arg, float tau)
        {
            if (c == null) return;
            CrashAt(arg, tau, out double s, out float L, out float sy);
            float td = arg ? tau : Mathf.Min(tau, TC + 1.6f); // the slide's heading freezes as it stops (the body keeps the spin)
            CrashAt(arg, td, out double s1, out float L1, out _);
            CrashAt(arg, td + 0.04f, out double s2, out float L2, out _);
            TrackAt(s, out Vector3 tg, out _, out _);
            Vector3 p = Lane(s, L), f = Flat(Lane(s2, L2) - Lane(s1, L1), tg);
            PoseCar(c, p, p + f, tg, sy * cSign, s, tau > 2.2f && tau < TC + 0.3f);
        }
        static Vector3 Flat(Vector3 d, Vector3 fallback) { d.y = 0f; return d.sqrMagnitude > 1e-6f ? d.normalized : fallback; }

        // ---------------------------------------------------------------- per frame (LateUpdate); false = it is over
        public bool Frame()
        {
            if (finished) return false;
            if (g.cam == null || g.track == null || g.model == null) return false;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (Shot) T = Mathf.Clamp(ShotT, 0f, total - 0.01f);
            else
            {
                T += dt;
                if (Input.GetKeyDown(KeyCode.Escape)) skipAll = true; // Android back
                if (skipAll) { skipT += dt; if (skipT >= SkipFade) return false; }
                if (T >= total) return false;
            }
            int b = BeatAt(T);
            if (b != beat) BuildBeat(b);
            float lt = T - t0[b], d = dur[b], u = Mathf.Clamp01(lt / d);
            HideOthers();
            switch (cards[b].scene)
            {
                case StoryCard.SCrash: Crash(lt); break;
                case StoryCard.SAftermath: Aftermath(lt, u); break;
                case StoryCard.SHelmet: Helmet(lt, u); break;
                case StoryCard.SRival: RivalGarage(lt, u); break;
                case StoryCard.SPitLane: PitLane(lt, u, d); break;
                case StoryCard.SBriefing: Briefing(lt, u); break;
                case StoryCard.SMia: MiaWave(lt, u); break;
                case StoryCard.SMiaCheer: MiaCheer(lt, u); break;
                case StoryCard.SEstablish: Establish(lt, u); break;
                case StoryCard.SRivalLeaves: RivalLeaves(lt, u); break;
                case StoryCard.SMemory: Memory(lt); break;
                case StoryCard.SDebrief: Debrief(lt, u); break;
                default: Celebrate(lt, u); break;
            }
            Animate(lt);
            g.camVel = Vector3.zero;
            g.FpsWindowReset(2f); // the whole circuit may be in view: Auto never steps the frame rate down for it
            return true;
        }
        int BeatAt(float t) { int k = 0; for (int i = 0; i < cards.Count; i++) if (t >= t0[i]) k = i; return k; }
        // a tap: the rest of this beat is cut (its dip to black, then the next one)
        void Next() { if (beat < 0) return; float end = t0[beat] + dur[beat] - DipOut; if (T < end) T = end; }

        // the cars of the last race and (when the scene does not use it) the player's car are hidden; the rivals are
        // shown again by GameManager.Update's own level-of-detail switch only on a change, so they are hidden every frame
        void HideOthers()
        {
            var rm = g.rivalModels; var rf = g.rivalFar;
            for (int i = 0; i < rm.Count; i++) if (rm[i] != null && rm[i].gameObject.activeSelf) rm[i].gameObject.SetActive(false);
            for (int i = 0; i < rf.Count; i++) if (rf[i] != null && rf[i].gameObject.activeSelf) rf[i].gameObject.SetActive(false);
            var m = g.model;
            if (m != modelFor) Adopt(m);
            if (m.gameObject.activeSelf != useModel) m.gameObject.SetActive(useModel);
            if (useModel)
            {
                if (dmR != null && dmR.enabled != driverIn) dmR.enabled = driverIn;
                if (m.helmet != null && m.helmet.gameObject.activeSelf != (driverIn || dmR != null)) m.helmet.gameObject.SetActive(driverIn || dmR != null); // (as PreRace)
            }
        }
        // the player's car: its driver (hidden while he is on foot) and the team's livery for the film
        void Adopt(CarModel m)
        {
            if (modelFor != null && modelFor != m) GiveBackModel();
            modelFor = m; dmR = null;
            var dm = m.GetComponentInChildren<DriverModel>(true);
            if (dm != null) dmR = dm.GetComponent<SkinnedMeshRenderer>();
            m.SetLivery(team); liveryChanged = true;
        }
        void GiveBackModel()
        {
            var m = modelFor; if (m == null) return;
            if (dmR != null) dmR.enabled = true;
            if (m.helmet != null) m.helmet.gameObject.SetActive(true);
            if (!m.gameObject.activeSelf && (m == g.model)) m.gameObject.SetActive(true);
            if (liveryChanged) m.SetLivery(g.InSeasonRound ? Livery.ForTeam(Season.Data.team, Season.TeamColour(Season.Data.team)) : Livery.Player[Mathf.Clamp(g.liveryIdx, 0, Livery.Player.Length - 1)]);
            liveryChanged = false; dmR = null; modelFor = null;
        }
        // the player's car standing at p facing f (in its box), or rolling (spin: wheel angle, rad)
        void PoseModel(Vector3 p, Vector3 f, float spin)
        {
            var m = g.model; if (m == null) return;
            m.Apply(p.x, p.z, System.Math.Atan2(f.z, f.x), 0, spin, spin, 0, 0);
            m.SetBrake(false);
        }

        // ---------------------------------------------------------------- building a beat (under its dip to black)
        void BuildBeat(int b)
        {
            ClearBeat();
            beat = b; shown = Mathf.Max(shown, b + 1); measKey = -1;
            var c = cards[b]; int sc = c.scene;
            SetRain(Rain(sc));
            root = new GameObject("StoryFilm");
            useModel = false; driverIn = false;
            switch (sc)
            {
                case StoryCard.SCrash:
                case StoryCard.SAftermath:
                    carT = Car(teo);
                    if (sc == StoryCard.SCrash) carA = Car(argent);
                    else { teoA = Driver(teo, TeoHelmet, false, People.GHelmet); if (teoA != null) teoA.lodK = 0.6f; }
                    mar1 = Marshal(311); mar2 = Marshal(733);
                    if (mar1 != null) { mar1.clip2 = People.MWave; mar1.flag = Flag(yellow: true); }
                    if (mar2 != null) { mar2.clip2 = People.MWave; mar2.phase = 0.4f; mar2.flag = Flag(yellow: true); }
                    break;
                case StoryCard.SMemory:
                    carT = Car(teo);
                    break;
                case StoryCard.SHelmet:
                    useModel = true;
                    drv = Driver(team, People.PlayerHelmet, false, People.GCap); if (drv != null) { drv.speed = 0.8f; drv.lodK = 0.5f; }
                    break;
                case StoryCard.SRival:
                {
                    carA = Car(argent);
                    bool nico = c.who == StoryCard.Nico;
                    riv = Driver(argent, argent.secondary, !nico, People.GCap);
                    if (riv != null) { riv.clip = People.FoldArms; riv.lodK = 0.5f; }
                    m1 = Crew(501, argent, People.OMechanic, People.GCap); if (m1 != null) m1.clip = People.Kneel;
                    m2 = Engineer(502, argent, false);
                    break;
                }
                case StoryCard.SRivalLeaves:
                    carA = Car(argent);
                    m1 = Crew(511, argent, People.OMechanic, People.GCap);
                    m2 = Engineer(512, argent, false);
                    break;
                case StoryCard.SPitLane:
                    useModel = true; driverIn = true;
                    kid = Kid(); if (kid != null) { kid.clip = People.MWave; kid.lodK = 0.5f; }
                    adult = Engineer(97, mia, false, People.GBare); if (adult != null) { adult.clip = People.MClap; adult.phase = 0.3f; }
                    break;
                case StoryCard.SBriefing:
                case StoryCard.SDebrief:
                case StoryCard.SEstablish:
                    useModel = true;
                    m1 = Crew(601, team, People.OMechanic, People.GCap); if (m1 != null) m1.clip = People.Kneel;
                    m2 = Crew(602, team, People.OMechanic, People.GCap); if (m2 != null) { m2.clip = People.Kneel; m2.phase = 1.3f; }
                    if (sc == StoryCard.SEstablish) { m3 = Engineer(603, team, false); break; }
                    lena = Lena(); drv = Driver(team, People.PlayerHelmet, false, People.GCap);
                    if (lena != null) { lena.clip = sc == StoryCard.SDebrief ? People.FoldArms : People.MTalk; lena.lodK = 0.5f; }
                    if (drv != null) drv.lodK = 0.5f;
                    break;
                case StoryCard.SMia:
                case StoryCard.SMiaCheer:
                    useModel = true;
                    kid = Kid(); adult = Engineer(97, mia, false, People.GBare);
                    if (kid != null) { kid.clip = sc == StoryCard.SMia ? People.MWave : People.Celebrate; kid.lodK = 0.45f; if (sc == StoryCard.SMia) kid.flag = Flag(yellow: false); }
                    if (adult != null) { adult.clip = sc == StoryCard.SMia ? People.MIdle : People.MClap; adult.phase = 0.6f; }
                    if (sc == StoryCard.SMiaCheer)
                    {
                        m1 = Crew(701, team, People.OMechanic, People.GCap); if (m1 != null) m1.clip = People.Celebrate;
                        m2 = Crew(702, team, People.OMechanic, People.GCap); if (m2 != null) { m2.clip = People.MClap; m2.phase = 0.5f; }
                        m3 = Crew(703, team, People.OMechanic, People.GCap); if (m3 != null) { m3.clip = People.Yes; m3.phase = 0.9f; }
                    }
                    break;
                default: // SCelebrate
                    useModel = true;
                    drv = Driver(team, People.PlayerHelmet, false, People.GCap); if (drv != null) { drv.clip = People.Celebrate; drv.lodK = 0.5f; }
                    lena = Lena(); if (lena != null) lena.clip = People.MClap;
                    kid = Kid(); if (kid != null) { kid.clip = People.Celebrate; kid.phase = 0.3f; }
                    m1 = Crew(801, team, People.OMechanic, People.GCap); if (m1 != null) m1.clip = People.Celebrate;
                    m2 = Crew(802, team, People.OMechanic, People.GCap); if (m2 != null) { m2.clip = People.MClap; m2.phase = 0.4f; }
                    m3 = Crew(803, team, People.OMechanic, People.GCap); if (m3 != null) { m3.clip = People.Yes; m3.phase = 0.8f; }
                    break;
            }
            if (actors.Count > 0) blobs = new People.Blobs(root.transform, actors.Count, "StoryShadows");
        }

        void ClearBeat()
        {
            for (int i = 0; i < cars.Count; i++) if (cars[i] != null) Object.Destroy(cars[i].gameObject);
            cars.Clear(); carT = carA = null;
            if (blobs != null) { blobs.Destroy(); blobs = null; }
            actors.Clear(); drv = lena = kid = adult = riv = teoA = m1 = m2 = m3 = mar1 = mar2 = null;
            if (root != null) { Object.Destroy(root); root = null; } // the rigs and flags under it
        }

        // the flashback's rain: Weather set for the beat (sky, fog, the road's wetness, the camera's rain) and given back
        void SetRain(bool on)
        {
            int want = on ? 2 : savedWeather;
            if (Weather.Index == want) return;
            Weather.Index = want; Weather.PushGlobals(); Sky.SetWeather(Weather.Wetness);
        }

        // ---------------------------------------------------------------- the cast
        CarModel Car(Livery l)
        {
            var c = CarModel.CreateFromAsset("Car/car_lod", l.primary, true);
            if (c == null) return null;
            c.SetLivery(l); cars.Add(c);
            return c;
        }
        Actor Person(int body, int gear, string name, float scale)
        {
            if (!People.Ready || root == null) return null;
            var a = new Actor { rig = new People.Rig(root.transform, body, gear, name) { scale = scale } };
            actors.Add(a);
            return a;
        }
        // a race driver (the pack's driver_01, or driver_02 as `second`) in a livery: cap off the track, helmet in the car
        Actor Driver(Livery l, Color helmet, bool second, int gear)
        {
            int body = second && People.Driver2Body >= 0 ? People.Driver2Body : People.DriverBody >= 0 ? People.DriverBody : 0;
            var a = Person(body, gear, "StoryDriver", 1f);
            if (a != null) a.rig.DressDriver(l, helmet);
            return a;
        }
        Actor Crew(int seed, Livery l, int outfit, int gear)
        {
            int body = People.CrewBodyOf(seed, 0.25f, true);
            var a = Person(body, gear, "StoryCrew", People.HeightOf(seed, body));
            if (a != null) { a.rig.Dress(outfit, l, seed); a.phase = (seed % 7) * 0.37f; }
            return a;
        }
        Actor Engineer(int seed, Livery l, bool female, int gear = People.GHeadset)
        {
            int body = People.EngineerBodyOf(seed, female ? 1f : 0.2f);
            var a = Person(body, gear, "StoryEngineer", People.HeightOf(seed, body));
            if (a != null) { a.rig.Dress(People.OEngineer, l, seed); a.phase = (seed % 5) * 0.41f; }
            return a;
        }
        // Lena Brandt: a woman engineer with her own headset, the team's shirt
        Actor Lena()
        {
            var a = Engineer(4242, team, true);
            if (a != null) a.lodK = 0.5f;
            return a;
        }
        Actor Marshal(int seed)
        {
            int body = People.MarshalBodyOf(seed);
            var a = Person(body, People.GCap, "StoryMarshal", People.HeightOf(seed, body));
            if (a != null) { a.rig.Dress(People.OMarshal, marshal, seed); a.phase = (seed % 3) * 0.5f; }
            return a;
        }
        // Mia, 10: a girl from the fans' pack (loaded once, as for the podium) scaled to a child's height, in red and
        // yellow; without that pack a woman engineer, scaled the same
        Actor Kid()
        {
            if (!People.Ready) return null;
            People.PreparePodium();
            int body = -1; var bs = People.bodies;
            if (bs != null)
                for (int i = 0; i < bs.Length && body < 0; i++)
                    if (bs[i] != null && bs[i].female && bs[i].name != null && bs[i].name.StartsWith("Fan", System.StringComparison.Ordinal)) body = i;
            if (body < 0) body = People.FanBodyOf(10);
            if (body < 0) body = People.EngineerBodyOf(10, 1f);
            var a = Person(body, People.GBare, "Mia", 0.78f);
            if (a != null) { a.rig.Dress(People.OEngineer, mia, 10); a.shadow = 0.5f; }
            return a;
        }

        // a small flag on a pole in the right hand: yellow (marshals) or the team's colours (Mia)
        Transform Flag(bool yellow)
        {
            if (flagMesh == null) flagMesh = FlagMesh();
            if (poleMat == null) poleMat = WorldBuilder.Mat(new Color(0.12f, 0.12f, 0.13f), null, 0.3f);
            Material cloth;
            if (yellow) { if (yellowMat == null) yellowMat = WorldBuilder.Mat(new Color(1f, 0.84f, 0.05f)); cloth = yellowMat; }
            else { if (teamFlagMat == null) teamFlagMat = WorldBuilder.Mat(team.primary); cloth = teamFlagMat; }
            var go = new GameObject("StoryFlag"); go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = flagMesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = new[] { poleMat, cloth };
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            return go.transform;
        }
        // pole along +z out of the fist, the cloth beside its top; every face both ways (any side may face the camera)
        static Mesh FlagMesh()
        {
            var v = new List<Vector3>(); var t0l = new List<int>(); var t1l = new List<int>();
            float r = 0.011f;
            Face(v, t0l, new Vector3(-r, -r, -0.08f), new Vector3(2f * r, 0f, 0f), new Vector3(0f, 0f, 1.0f));
            Face(v, t0l, new Vector3(-r, r, -0.08f), new Vector3(2f * r, 0f, 0f), new Vector3(0f, 0f, 1.0f));
            Face(v, t0l, new Vector3(-r, -r, -0.08f), new Vector3(0f, 2f * r, 0f), new Vector3(0f, 0f, 1.0f));
            Face(v, t0l, new Vector3(r, -r, -0.08f), new Vector3(0f, 2f * r, 0f), new Vector3(0f, 0f, 1.0f));
            Face(v, t1l, new Vector3(r, 0f, 0.48f), new Vector3(0.58f, 0f, 0f), new Vector3(0f, 0f, 0.42f));
            var m = new Mesh { name = "StoryFlag" };
            m.SetVertices(v); m.subMeshCount = 2; m.SetTriangles(t0l, 0); m.SetTriangles(t1l, 1);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }
        static void Face(List<Vector3> v, List<int> t, Vector3 o, Vector3 a, Vector3 b)
        {
            for (int side = 0; side < 2; side++)
            {
                int k = v.Count;
                v.Add(o); v.Add(o + a); v.Add(o + a + b); v.Add(o + b);
                if (side == 0) { t.Add(k); t.Add(k + 1); t.Add(k + 2); t.Add(k); t.Add(k + 2); t.Add(k + 3); }
                else { t.Add(k); t.Add(k + 2); t.Add(k + 1); t.Add(k); t.Add(k + 3); t.Add(k + 2); }
            }
        }

        static void Put(Actor a, Vector3 p, float yaw) { if (a == null) return; a.pos = p; a.yaw = yaw; }
        static void Look(Actor a, Vector3 at, float w) { if (a == null) return; a.look = at; a.lookW = w; }
        static float Yaw(Vector3 d) => Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        static Vector3 Head(Actor a, Vector3 fallback) => a != null ? a.pos + Vector3.up * (1.62f * a.rig.scale) : fallback;

        // every person: the clip (or a blend of two), placed, the head turned, the flag in the hand, LOD, contact shadow
        void Animate(float lt)
        {
            if (actors.Count == 0) return;
            if (blobs != null) blobs.Begin();
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i]; var r = a.rig;
                float tt = lt * a.speed + a.phase;
                People.Sample(a.clip, Mathf.Repeat(tt, Mathf.Max(0.2f, People.Length(a.clip))), pa);
                if (a.clip2 >= 0 && a.mix > 0.001f)
                {
                    People.Sample(a.clip2, Mathf.Repeat(tt, Mathf.Max(0.2f, People.Length(a.clip2))), pb);
                    People.Blend(pa, pb, a.mix, pa);
                }
                r.Place(a.pos, a.yaw); r.Apply(pa);
                if (a.lookW > 0.001f) r.LookAt(a.look, a.lookW);
                if (a.flag != null)
                {
                    Vector3 fd = r.ForearmDir(false), up = Mathf.Abs(Vector3.Dot(fd, Vector3.up)) > 0.95f ? r.tr.forward : Vector3.up;
                    a.flag.SetPositionAndRotation(r.Palm(false), Quaternion.LookRotation(fd, up));
                }
                r.SetLodAt(Vector3.Distance(camPos, a.pos) * a.lodK);
                if (blobs != null) blobs.Set(i, a.pos, r.tr.forward, 0.38f * r.scale, 0.8f, a.shadow);
            }
            if (blobs != null) blobs.Flush();
        }

        // ---------------------------------------------------------------- camera
        void Cam(Vector3 pos, Vector3 subject, float fov, float nx, float ny, Quaternion shake, float near = 0.25f)
        {
            var c = g.cam;
            fov = Fit(fov);
            camPos = pos;
            c.transform.SetPositionAndRotation(pos, Aim(pos, subject, fov, nx, ny) * shake);
            c.fieldOfView = fov; c.nearClipPlane = near;
        }
        // look at subject so that it sits at (nx, ny) on the screen (-1..1, + = right / up), as PreRace
        Quaternion Aim(Vector3 pos, Vector3 subject, float fov, float nx, float ny)
        {
            Vector3 d = subject - pos; if (d.sqrMagnitude < 1e-6f) d = Vector3.forward;
            float tv = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), th = tv * Mathf.Max(0.5f, g.cam.aspect);
            float yaw = Mathf.Atan(th * -nx) * Mathf.Rad2Deg, pitchUp = Mathf.Atan(tv * -ny) * Mathf.Rad2Deg;
            return Quaternion.LookRotation(d, Vector3.up) * Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(-pitchUp, Vector3.right);
        }
        // a field of view framed for 16:9 keeps its side-to-side view on wider phones (as PreRace / the chase camera)
        float Fit(float fov)
        {
            float a = Mathf.Max(0.5f, g.cam.aspect);
            return a > 16f / 9f ? 2f * Mathf.Rad2Deg * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * (16f / 9f) / a) : fov;
        }
        // a long lens that keeps a subject `half` m tall-ish in frame at any distance
        static float Lens(float dist, float half, float lo, float hi) => Mathf.Clamp(2f * Mathf.Atan(half / Mathf.Max(1f, dist)) * Mathf.Rad2Deg, lo, hi);
        Quaternion Hand(float amp, float hz, float seed, float t) => PrShake(amp, hz, seed, t);

        // ---------------------------------------------------------------- the scenes
        // the crash: scene time runs at 1x, then 0.3x through the contact, then 1x again
        static float CrashTau(float lt) => lt < 2.4f ? 1.2f + lt : lt < 6.4f ? 3.6f + (lt - 2.4f) * 0.3f : 4.8f + (lt - 6.4f);
        public const float CrashContactLt = 2.4f + (TC - 3.6f) / 0.3f; // (3.73 s into the beat)

        void Crash(float lt)
        {
            float tau = CrashTau(lt);
            PoseCrashCar(carT, false, tau); PoseCrashCar(carA, true, tau);
            Marshals(tau);
            Vector3 pT = carT != null ? carT.transform.position : Lane(sStart + S(tau), 0f);
            Vector3 pAr = carA != null ? carA.transform.position : pT;
            Vector3 mid = (pT + pAr) * 0.5f + Vector3.up * 0.55f;
            if (lt < 2.4f)
            {
                // the entry, long lens from the outside at the apex, back up the road at the two cars coming side by side
                Vector3 p = Lane(cS + 5.0, cHw + 2.6f) + Vector3.up * 1.25f;
                float dist = Vector3.Distance(p, mid);
                Cam(p, mid, Lens(dist, 6f, 5f, 40f), 0f, -0.05f, Hand(0.12f, 0.6f, 3.1f, lt), 1f);
            }
            else if (lt < 6.4f)
            {
                // slow motion: low on the inside past the turn-in, the dive and the contact
                double sTc = sStart + S(TC);
                Vector3 p = Lane(sTc + 14.0, -(cHw + 1.8f)) + Vector3.up * 0.7f;
                float dist = Vector3.Distance(p, mid);
                float jolt = tau > TC ? 1.6f * Mathf.Exp(-(tau - TC) * 8f) : 0f;
                Cam(p, Vector3.Lerp(mid, pT + Vector3.up * 0.5f, 0.4f), Lens(dist, 4.2f, 14f, 50f), 0f, -0.08f, Hand(0.2f + jolt, 2.5f, 7.7f, lt), 0.4f);
            }
            else
            {
                // high and wide over the outside wall: the spin into the barrier, the marshals, the other car gone
                double sStop = sStart + S(TC) + VMin * KSlide;
                Vector3 o = Outside(sStop);
                Vector3 p = PrAbove(Lane(sStop - 30.0, wallL + 14f) + Vector3.up * 8.5f, 4f);
                float e = PrSmooth((lt - 6.4f) / 6f);
                Cam(p, pT + Vector3.up * 0.4f - o * 1.5f, Mathf.Lerp(40f, 31f, e), 0f, 0.05f, Hand(0.15f, 0.4f, 11.3f, lt), 1f);
            }
        }
        // the marshals behind the outside wall past the stopping point: flags up once the car is off
        void Marshals(float tau)
        {
            if (mar1 == null && mar2 == null) return;
            double sStop = sStart + S(TC) + VMin * KSlide;
            Vector3 o = Outside(sStop);
            float wave = PrSmooth((tau - TC - 1.0f) / 0.6f);
            Vector3 a = Lane(sStop + 15.0, wallL + 2.6f), b = Lane(sStop - 9.0, wallL + 2.4f);
            Put(mar1, a, Yaw(-o) - 20f); Put(mar2, b, Yaw(-o) + 15f);
            if (mar1 != null) mar1.mix = wave;
            if (mar2 != null) mar2.mix = wave;
            Vector3 car = carT != null ? carT.transform.position : a;
            Look(mar1, car, 0.6f); Look(mar2, car, 0.6f);
        }

        void Aftermath(float lt, float u)
        {
            const float tau = TC + 8f; // stopped
            PoseCrashCar(carT, false, tau);
            Marshals(tau);
            if (mar1 != null) mar1.mix = 1f;
            if (mar2 != null) mar2.mix = 1f;
            double sStop = sStart + S(TC) + VMin * KSlide;
            Vector3 car = carT != null ? carT.transform.position : Lane(sStop, wallL);
            TrackAt(sStop, out Vector3 tg, out _, out _);
            Vector3 o = Outside(sStop);
            Vector3 tp = car - o * 2.3f + tg * 1.4f; tp.y = WorldBuilder.H(tp.x, tp.z);
            Put(teoA, tp, Yaw(car - tp));
            Look(teoA, car + Vector3.up * 0.2f, 0.8f); // head down at his car
            float e = PrSmooth(u);
            Vector3 p0 = car - o * 6.5f + tg * 5f + Vector3.up * 1.7f, p1 = car - o * 24f - tg * 20f + Vector3.up * 17f;
            Vector3 p = PrAbove(Vector3.Lerp(p0, p1, e), 1.2f);
            Vector3 subj = Vector3.Lerp(Head(teoA, car), car + Vector3.up * 0.5f, 0.35f + 0.4f * e);
            Cam(p, subj, Mathf.Lerp(32f, 44f, e), 0f, 0f, Hand(0.18f, 0.35f, 5.3f, lt), 0.5f);
        }

        void Memory(float lt)
        {
            float tau = 1.2f + lt * 0.85f;
            MemoryAt(tau, out double s, out float L);
            MemoryAt(tau + 0.04f, out double s2, out float L2);
            Vector3 p = Lane(s, L);
            TrackAt(s, out Vector3 tg, out _, out _);
            PoseCar(carT, p, Lane(s2, L2), tg, 0f, s, tau > 2.2f && tau < 3.6f);
            Vector3 subj = p + Vector3.up * 0.55f;
            if (lt < 3.0f)
            {
                Vector3 cp = Lane(cS + 5.0, cHw + 2.6f) + Vector3.up * 1.25f;
                Cam(cp, subj, Lens(Vector3.Distance(cp, subj), 5f, 5f, 40f), 0f, -0.05f, Hand(0.12f, 0.6f, 2.3f, lt), 1f);
            }
            else
            {
                Vector3 cp = Lane(cS + 2.0, -(cHw + 1.6f)) + Vector3.up * 0.65f;
                Cam(cp, subj, Lens(Vector3.Distance(cp, subj), 3.5f, 16f, 55f), 0f, -0.08f, Hand(0.15f, 1.5f, 9.1f, lt), 0.4f);
            }
        }

        // the car in the player's box, nose along the lane, the driver out of it
        Vector3 BoxCar(bool rv)
        {
            Vector3 p = SP(rv, 0f, 0f);
            if (!rv) PoseModel(p, pA, 0f);
            return p;
        }

        void Helmet(float lt, float u)
        {
            Vector3 car = BoxCar(false);
            Vector3 at = SP(false, 0.45f, 1.45f);
            Put(drv, at, Yaw(-pG));
            Look(drv, SP(false, 0.4f, 0f) + Vector3.up * 0.75f, 0.85f); // into the cockpit
            Vector3 head = Head(drv, at + Vector3.up * 1.6f);
            if (u < 0.5f)
            {
                float e = PrSmooth(u / 0.5f);
                Vector3 p = SP(false, -5.5f + 1.2f * e, -4.6f) + Vector3.up * 1.55f;
                Cam(p, Vector3.Lerp(head, car + Vector3.up * 0.6f, 0.4f), 34f, 0.1f, 0f, Hand(0.2f, 0.5f, 4.4f, lt));
            }
            else
            {
                float e = PrSmooth((u - 0.5f) / 0.5f);
                Vector3 p = SP(false, 2.7f - 0.4f * e, -1.9f + 0.3f * e) + Vector3.up * 1.35f;
                Cam(p, head - Vector3.up * 0.1f, Mathf.Lerp(27f, 22f, e), -0.25f, 0.1f, Hand(0.18f, 0.6f, 6.6f, lt));
            }
        }

        void RivalGarage(float lt, float u)
        {
            Vector3 car = SP(true, 0f, 0f);
            PoseCar(carA, car, car + rA, rA, 0f, 0.0, false);
            Vector3 at = SP(true, -3.5f, Mathf.Min(1.4f, rGd - 0.4f));
            Put(riv, at, Yaw(-rG));
            Put(m1, SP(true, 1.65f, 1.25f), Yaw(-rG));
            Put(m2, SP(true, 3.3f, 1.4f), Yaw(-rG) - 30f);
            float e = PrSmooth(u);
            Vector3 p = SP(true, -1.1f - 1.0f * e, -6.0f + 2.2f * e) + Vector3.up * 1.5f;
            Vector3 head = Head(riv, at + Vector3.up * 1.6f);
            Cam(p, head - Vector3.up * 0.15f, Mathf.Lerp(30f, 20f, e), 0.25f, 0.05f, Hand(0.12f, 0.4f, 8.8f, lt));
            Look(riv, camPos, 0.75f); // into the lens
            Look(m2, car + Vector3.up * 0.6f, 0.5f);
        }

        void RivalLeaves(float lt, float u)
        {
            float mv = Mathf.Max(0f, lt - 0.8f);
            float x = mv < 3.2f ? 2.5f * mv * mv : 25.6f + 16f * (mv - 3.2f); // 5 m/s^2 to 16 m/s
            float z = -3.2f * PrSmooth(x / 9f);
            Vector3 p = SP(true, x, z), ahead = SP(true, x + 0.6f, -3.2f * PrSmooth((x + 0.6f) / 9f));
            PoseCar(carA, p, ahead, rA, 0f, x, false);
            Put(m1, SP(true, 0.5f, 1.4f), Yaw(-rG));
            Put(m2, SP(true, -1.6f, 1.6f), Yaw(-rG) + 20f);
            Look(m1, p + Vector3.up * 0.6f, 0.7f); Look(m2, p + Vector3.up * 0.6f, 0.7f);
            Vector3 cp = SP(true, 24f, -4.6f) + Vector3.up * 0.8f, subj = p + Vector3.up * 0.5f;
            Cam(cp, subj, Lens(Vector3.Distance(cp, subj), 3.8f, 10f, 46f), 0f, -0.05f, Hand(0.12f, 0.6f, 12.4f, lt));
        }

        void PitLane(float lt, float u, float d)
        {
            const float v = 13f;
            float x = -48f + v * lt;
            Vector3 car = SP(false, x, -3.3f);
            PoseModel(car, pA, (x + 48f) / 0.33f);
            Vector3 kp = SP(false, 1.2f, Mathf.Min(1.5f, pGd - 0.3f));
            Put(kid, kp, Yaw(-pG));
            Put(adult, SP(false, 0.3f, Mathf.Min(1.9f, pGd)), Yaw(-pG) + 10f);
            Look(kid, car + Vector3.up * 0.7f, 0.8f); Look(adult, car + Vector3.up * 0.7f, 0.6f);
            Vector3 kh = Head(kid, kp + Vector3.up * 1.2f);
            if (lt < 3.1f)
            {
                Vector3 p = SP(false, 2.6f, Mathf.Min(4.4f, pGd + 1.2f)) + Vector3.up * 1.05f;
                Cam(p, Vector3.Lerp(kh, car + Vector3.up * 0.5f, 0.55f), 40f, 0f, 0f, Hand(0.25f, 0.7f, 13.9f, lt));
            }
            else
            {
                float e = PrSmooth((lt - 3.1f) / Mathf.Max(1f, d - 3.1f));
                Vector3 p = SP(false, 6.5f - 0.6f * e, -5.6f) + Vector3.up * 1.2f;
                Cam(p, kh - Vector3.up * 0.2f, Mathf.Lerp(26f, 22f, e), 0.15f, 0f, Hand(0.15f, 0.5f, 14.7f, lt));
            }
        }

        // two people talking at the garage front beside the car's nose: Lena (x 3.4) and the driver (x 4.6)
        void Pair(out Vector3 lp, out Vector3 dp)
        {
            lp = SP(false, 3.4f, Mathf.Min(2.2f, pGd)); dp = SP(false, 4.6f, Mathf.Min(2.0f, pGd - 0.2f));
            Put(lena, lp, Yaw(dp - lp)); Put(drv, dp, Yaw(lp - dp));
        }
        void Mechanics()
        {
            Put(m1, SP(false, 1.65f, 1.25f), Yaw(-pG));
            Put(m2, SP(false, -1.55f, 1.25f), Yaw(-pG));
        }

        void Briefing(float lt, float u)
        {
            BoxCar(false);
            Mechanics();
            Pair(out Vector3 lp, out Vector3 dp);
            Vector3 lh = Head(lena, lp + Vector3.up * 1.6f), dh = Head(drv, dp + Vector3.up * 1.6f);
            Look(lena, dh, 0.8f);
            Look(drv, lh + Vector3.up * (0.04f * Mathf.Sin(lt * 2.2f)), 0.8f); // listening, a small nod
            if (u < 0.55f)
            {
                float e = PrSmooth(u / 0.55f);
                Vector3 p = SP(false, 3.0f + 0.8f * e, -4.4f) + Vector3.up * 1.5f;
                Cam(p, (lh + dh) * 0.5f - Vector3.up * 0.15f, 30f, 0f, 0.05f, Hand(0.18f, 0.5f, 15.5f, lt));
            }
            else
            {
                Vector3 toL = PrFlat(lp - dp);
                Vector3 p = dh - toL * 0.9f - pG * 0.45f + Vector3.up * 0.05f;
                Cam(p, lh - Vector3.up * 0.05f, 30f, 0.2f, 0.05f, Hand(0.2f, 0.6f, 16.1f, lt), 0.15f);
            }
        }

        void Debrief(float lt, float u)
        {
            BoxCar(false);
            Mechanics();
            Pair(out Vector3 lp, out Vector3 dp);
            Vector3 lh = Head(lena, lp + Vector3.up * 1.6f), dh = Head(drv, dp + Vector3.up * 1.6f);
            Look(lena, dh, 0.8f);
            Look(drv, Vector3.Lerp(dp, lp, 0.35f) + Vector3.up * 0.2f, 0.9f); // head down
            float e = PrSmooth(u);
            Vector3 p = SP(false, 4.6f - 0.3f * e, -4.4f + 0.6f * e) + Vector3.up * 1.45f;
            Cam(p, (lh + dh) * 0.5f - Vector3.up * 0.2f, Mathf.Lerp(30f, 25f, e), 0f, 0.05f, Hand(0.15f, 0.4f, 17.3f, lt));
        }

        void MiaWave(float lt, float u)
        {
            BoxCar(false);
            Vector3 kp = SP(false, 4.4f, Mathf.Min(1.4f, pGd - 0.3f));
            Put(kid, kp, Yaw(-pG));
            Put(adult, SP(false, 5.3f, Mathf.Min(1.9f, pGd)), Yaw(-pG) - 15f);
            float e = PrSmooth(u);
            Vector3 p = SP(false, 5.3f - 0.4f * e, -2.9f + 0.5f * e) + Vector3.up * 1.0f;
            Vector3 kh = Head(kid, kp + Vector3.up * 1.25f);
            Cam(p, kh - Vector3.up * 0.15f, Mathf.Lerp(34f, 28f, e), -0.15f, 0.05f, Hand(0.2f, 0.6f, 18.2f, lt));
            Look(kid, camPos, 0.7f); Look(adult, kh, 0.5f);
        }

        void MiaCheer(float lt, float u)
        {
            BoxCar(false);
            Vector3 kp = SP(false, 4.0f, Mathf.Min(1.6f, pGd - 0.3f));
            Put(kid, kp, Yaw(-pG));
            Put(adult, SP(false, 5.0f, Mathf.Min(2.0f, pGd)), Yaw(-pG) - 10f);
            Put(m1, SP(false, -0.8f, Mathf.Min(2.3f, pGd)), Yaw(-pG) + 10f);
            Put(m2, SP(false, -2.0f, Mathf.Min(2.6f, pGd + 0.3f)), Yaw(-pG) + 20f);
            Put(m3, SP(false, 0.6f, Mathf.Min(2.7f, pGd + 0.4f)), Yaw(-pG) - 5f);
            Vector3 kh = Head(kid, kp + Vector3.up * 1.25f);
            if (u < 0.45f)
            {
                Vector3 p = SP(false, 1.5f, -6.0f) + Vector3.up * 2.0f;
                Cam(p, SP(false, 1.2f, 1.8f) + Vector3.up * 1.0f, 42f, 0f, 0f, Hand(0.2f, 0.5f, 19.4f, lt));
            }
            else
            {
                float e = PrSmooth((u - 0.45f) / 0.55f);
                Vector3 p = SP(false, 4.9f - 0.3f * e, -2.8f + 0.4f * e) + Vector3.up * 1.0f;
                Cam(p, kh - Vector3.up * 0.1f, Mathf.Lerp(34f, 29f, e), -0.15f, 0.05f, Hand(0.25f, 0.7f, 20.6f, lt));
            }
            Look(kid, camPos, 0.5f); Look(adult, kh, 0.5f);
        }

        void Establish(float lt, float u)
        {
            Vector3 car = BoxCar(false);
            Mechanics();
            Put(m3, SP(false, -3.4f, Mathf.Min(1.6f, pGd)), Yaw(-pG) + 25f);
            Look(m3, car + Vector3.up * 0.6f, 0.6f);
            float e = PrSmooth(u);
            Vector3 p = PrAbove(Vector3.Lerp(SP(false, -16f, -11f) + Vector3.up * 12f, SP(false, -6.5f, -4.6f) + Vector3.up * 2.4f, e), 1.5f);
            Cam(p, SP(false, 0f, 0.8f) + Vector3.up * 0.5f, Mathf.Lerp(46f, 36f, e), 0f, 0f, Hand(0.18f, 0.35f, 21.8f, lt));
        }

        void Celebrate(float lt, float u)
        {
            BoxCar(false);
            Vector3 dp = SP(false, 3.6f, Mathf.Min(1.7f, pGd - 0.2f));
            Put(drv, dp, Yaw(-pG));
            Put(lena, SP(false, 5.0f, Mathf.Min(2.3f, pGd)), Yaw(-pG) - 20f);
            Put(kid, SP(false, 2.3f, Mathf.Min(1.3f, pGd - 0.5f)), Yaw(-pG) + 15f);
            Put(m1, SP(false, -0.6f, Mathf.Min(2.4f, pGd)), Yaw(-pG) + 10f);
            Put(m2, SP(false, -1.9f, Mathf.Min(2.7f, pGd + 0.3f)), Yaw(-pG) + 20f);
            Put(m3, SP(false, 0.8f, Mathf.Min(2.9f, pGd + 0.4f)), Yaw(-pG));
            Vector3 dh = Head(drv, dp + Vector3.up * 1.6f);
            Look(lena, dh, 0.6f); Look(kid, dh, 0.6f);
            float e = PrSmooth(u), ang = Mathf.Lerp(-30f, 25f, e);
            Vector3 dir = Quaternion.AngleAxis(ang, Vector3.up) * -pG;
            Vector3 p = dp + dir * 4.2f + Vector3.up * 0.75f;
            p.y = Mathf.Max(p.y, WorldBuilder.H(p.x, p.z) + 0.5f);
            Cam(p, dh - Vector3.up * 0.25f, 36f, 0f, 0.1f, Hand(0.2f, 0.5f, 22.9f, lt));
        }

        // ---------------------------------------------------------------- overlay (OnGUI, instead of the menu)
        public void Gui()
        {
            if (finished) return;
            var ev = Event.current;
            int b = Mathf.Clamp(beat, 0, cards.Count - 1);
            float lt = beat < 0 ? 0f : T - t0[b], d = dur[b];
            float W = Screen.width, H = Screen.height, lb = UiKit.U(24f);
            var safe = UiKit.Safe;
            float sw = UiKit.Width(skipTxt, UiKit.Bold, 7.2f) + UiKit.U(30), sh = UiKit.U(20);
            var skipR = new Rect(safe.xMax - UiKit.U(14) - sw, Mathf.Max(safe.y, 0f) + UiKit.U(2), sw, sh);
            if (!Shot && ev.type == EventType.MouseDown)
            {
                if (skipR.Contains(ev.mousePosition)) skipAll = true;
                else if (lt > 0.6f) Next();
                ev.Use();
                return;
            }
            if (ev.type != EventType.Repaint) return;
            if (beat < 0) { UiKit.Rect(new Rect(0, 0, W, H), Color.black); return; }
            var c = cards[b]; int sc = c.scene;

            // the flashback look: a cold dusk tint and a vignette
            if (Flashback(sc))
            {
                UiKit.Rect(new Rect(0, 0, W, H), new Color(0.03f, 0.05f, 0.13f, 0.36f));
                float vw = W * 0.22f, vh = H * 0.26f;
                var dark = new Color(0f, 0f, 0f, 0.55f); var clear = new Color(0f, 0f, 0f, 0f);
                UiKit.Gradient(new Rect(0, 0, W, vh), dark, clear);
                UiKit.Gradient(new Rect(0, H - vh, W, vh), clear, dark);
                UiKit.Gradient(new Rect(0, 0, vw, H), dark, clear, 0f, true);
                UiKit.Gradient(new Rect(W - vw, 0, vw, H), clear, dark, 0f, true);
            }
            // the contact: a white flash
            if (sc == StoryCard.SCrash && lt > CrashContactLt)
            {
                float fl = 0.5f * Mathf.Exp(-(lt - CrashContactLt) * 9f);
                if (fl > 0.004f) UiKit.Rect(new Rect(0, 0, W, H), new Color(1f, 1f, 1f, fl));
            }

            Captions(b, lt, d, lb);

            // letterbox bars, the film's progress on the top one, SKIP
            UiKit.Rect(new Rect(0, 0, W, lb), Color.black); UiKit.Rect(new Rect(0, H - lb, W, lb), Color.black);
            UiKit.Rect(new Rect(0, lb - UiKit.U(1.2f), W * Mathf.Clamp01(T / total), UiKit.U(1.2f)), UiKit.A(UiKit.Red, 0.8f));
            if (!Shot && T > 0.4f)
            {
                UiKit.Rect(skipR, new Color(0f, 0f, 0f, 0.42f), 10f);
                UiKit.Outline(skipR, new Color(1f, 1f, 1f, 0.2f), 10f, 0.7f);
                UiKit.Text(new Rect(skipR.x + UiKit.U(10), skipR.y, sw, sh), skipTxt, UiKit.Bold, 7.2f, new Color(1f, 1f, 1f, 0.88f));
                UiKit.Icon(new Rect(skipR.xMax - UiKit.U(17), skipR.center.y - UiKit.U(4.5f), UiKit.U(9), UiKit.U(9)), UiKit.IconNext, new Color(1f, 1f, 1f, 0.88f));
            }

            // black: into each beat, out of it, a skip
            float black = Mathf.Max(1f - lt / DipIn, 1f - (d - lt) / DipOut);
            if (Shot) black = Mathf.Min(black, 1f - lt / DipIn); // (frozen frames: no dip at the end)
            if (skipAll) black = Mathf.Max(black, skipT / SkipFade);
            if (black > 0.004f) UiKit.Rect(new Rect(0, 0, W, H), new Color(0f, 0f, 0f, Mathf.Clamp01(black)));
        }

        Font TitleFont(StoryCard c) => c.kind == StoryCard.Quote || c.kind == StoryCard.Message || (c.title != null && c.title.StartsWith("“", System.StringComparison.Ordinal)) ? UiKit.HeavyItalic : UiKit.Heavy;

        void Measure(int b)
        {
            int key = b * 1000003 + Screen.height * 7 + Screen.width;
            if (key == measKey) return;
            measKey = key;
            var c = cards[b]; var safe = UiKit.Safe;
            figW = c.figure != null ? UiKit.Width(c.figure, UiKit.HeavyItalic, FigSz) + UiKit.U(14) : 0f;
            capW = Mathf.Min(safe.width * 0.58f, UiKit.U(560));
            Font tf = TitleFont(c);
            titleSz = 24f;
            while (titleSz > 15f && UiKit.Height(c.title, tf, titleSz, capW) > UiKit.U(84)) titleSz -= 1.5f;
            titleH = UiKit.Height(c.title, tf, titleSz, capW);
            bodyH = c.body != null ? UiKit.Height(c.body, UiKit.Body, BodySz, capW) : 0f;
        }

        // bottom left over a soft dark ground: the kicker (red on a chapter), the title, then the body
        void Captions(int b, float lt, float d, float lb)
        {
            var c = cards[b];
            if (string.IsNullOrEmpty(c.title)) return;
            Measure(b);
            float H = Screen.height, W = Screen.width;
            var safe = UiKit.Safe;
            float fade = Mathf.Clamp01((d - lt - 0.2f) / 0.45f);
            float aK = Mathf.Clamp01((lt - 0.45f) / 0.5f) * fade, aT = UiKit.EaseOut((lt - 0.65f) / 0.6f) * fade;
            float aB = c.body != null ? UiKit.EaseOut((lt - bodyAt[b]) / 0.6f) * fade : 0f;
            float aG = Mathf.Max(aK, aT);
            if (aG <= 0.003f) return;
            // the ground under the text
            UiKit.Gradient(new Rect(0, H * 0.42f, W, H * 0.58f), new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, 0.62f * aG));

            float x = safe.x + UiKit.U(30), bottom = H - lb - UiKit.U(18);
            float yB = bottom - bodyH, yT = (c.body != null ? yB - UiKit.U(8) : bottom) - titleH, yK = yT - UiKit.U(18);
            if (c.figure != null)
            {
                UiKit.PushAlpha(aT);
                float fh = UiKit.U(FigSz * 1.05f);
                UiKit.BigNumber(UiKit.Rise(new Rect(x - UiKit.U(4), bottom - fh, figW + UiKit.U(8), fh), aT, 12f), c.figure, FigSz, UiKit.Red);
                UiKit.PopAlpha();
                x += figW;
            }
            bool chapter = c.kind == StoryCard.Chapter;
            if (!string.IsNullOrEmpty(c.kicker))
            {
                UiKit.PushAlpha(aK);
                UiKit.Kicker(new Rect(x, yK, capW, UiKit.U(14)), c.kicker, chapter ? UiKit.AccentHi : new Color(1f, 1f, 1f, 0.78f), UiKit.Fit(UiKit.Caps(c.kicker), UiKit.Body, 9.5f, capW, 7f));
                UiKit.PopAlpha();
            }
            Font tf = TitleFont(c);
            UiKit.PushAlpha(aT);
            var tr = UiKit.Rise(new Rect(x, yT, capW, titleH + UiKit.U(4)), aT, 10f);
            float o = Mathf.Max(1f, UiKit.U(0.9f));
            UiKit.Text(new Rect(tr.x + o, tr.y + o, tr.width, tr.height), c.title, tf, titleSz, new Color(0f, 0f, 0f, 0.5f), TextAnchor.UpperLeft, true);
            UiKit.Text(tr, c.title, tf, titleSz, Color.white, TextAnchor.UpperLeft, true);
            UiKit.PopAlpha();
            if (aB > 0.003f)
            {
                UiKit.PushAlpha(aB);
                UiKit.Text(new Rect(x + o, yB + o, capW, bodyH + UiKit.U(4)), c.body, UiKit.Body, BodySz, new Color(0f, 0f, 0f, 0.45f), TextAnchor.UpperLeft, true);
                UiKit.Text(new Rect(x, yB, capW, bodyH + UiKit.U(4)), c.body, UiKit.Body, BodySz, new Color(1f, 1f, 1f, 0.86f), TextAnchor.UpperLeft, true);
                UiKit.PopAlpha();
            }
        }

        // ---------------------------------------------------------------- the end: everything back as it was
        public void Finish()
        {
            if (finished) return;
            finished = true;
            try
            {
                ClearBeat();
                var rm = g.rivalModels; var rf = g.rivalFar; var rn = g.rivalNear;
                for (int i = 0; i < rm.Count; i++)
                {
                    bool near = i < rn.Count ? rn[i] : true;
                    if (rm[i] != null) rm[i].gameObject.SetActive(near);
                    if (i < rf.Count && rf[i] != null) rf[i].gameObject.SetActive(!near);
                }
                GiveBackModel();
                if (g.model != null && !g.model.gameObject.activeSelf) g.model.gameObject.SetActive(true);
                if (Weather.Index != savedWeather) { Weather.Index = savedWeather; Weather.PushGlobals(); Sky.SetWeather(Weather.Wetness); }
                if (g.cam != null) { g.cam.nearClipPlane = ChaseNear; g.cam.fieldOfView = Fit(g.fovChase); }
                g.tvWasOn = true; g.camVel = Vector3.zero; g.tvCur = -1; g.tvCarIdx = -1; // the game's camera: a clean start
                g.FpsWindowReset(2f);
            }
            catch (System.Exception e) { Debug.LogWarning("StoryFilm finish: " + e.Message); }
            if (flagMesh != null) Object.Destroy(flagMesh);
            if (poleMat != null) Object.Destroy(poleMat);
            if (yellowMat != null) Object.Destroy(yellowMat);
            if (teamFlagMat != null) Object.Destroy(teamFlagMat);
            flagMesh = null; poleMat = yellowMat = teamFlagMat = null;
        }

        // ---------------------------------------------------------------- track helpers (as PreRace)
        // a point of the centreline at lap distance s (any s; it wraps), its direction and the track normal (flat, unit)
        Vector3 TrackAt(double s, out Vector3 tangent, out Vector3 normal, out int idx)
        {
            var t = g.track; double fi = s / t.ds;
            int i0 = (int)System.Math.Floor(fi); float a = (float)(fi - i0);
            i0 = ((i0 % t.N) + t.N) % t.N; int i1 = (i0 + 1) % t.N;
            var p = t.pts[i0]; var q = t.pts[i1];
            float x = (float)(p.x + (q.x - p.x) * a), z = (float)(p.y + (q.y - p.y) * a);
            tangent = PrFlat(new Vector3((float)(p.tx + (q.tx - p.tx) * a), 0f, (float)(p.ty + (q.ty - p.ty) * a)));
            normal = PrFlat(new Vector3((float)(p.nx + (q.nx - p.nx) * a), 0f, (float)(p.ny + (q.ny - p.ny) * a)));
            idx = a < 0.5f ? i0 : i1;
            return new Vector3(x, WorldBuilder.H(x, z), z);
        }
        // the strongest sustained bend in the second half of the lap, the later the better (as PreRace.ShowcaseCorner)
        static double ShowcaseCorner(Track t, out float sign)
        {
            int n = t.N, w = Mathf.Max(1, (int)(50.0 / t.ds)), step = Mathf.Max(1, (int)(4.0 / t.ds));
            double best = -1, bestS = t.length * 0.85; sign = 1f;
            for (int i = n / 2; i < (int)(n * 0.97); i += step)
            {
                double cv = 0; int cnt = 0;
                for (int k = -w; k <= w; k += step) { cv += t.pts[((i + k) % n + n) % n].curv; cnt++; }
                cv /= System.Math.Max(1, cnt);
                double score = System.Math.Abs(cv) * (0.6 + (double)i / n);
                if (score > best) { best = score; bestS = i * t.ds; sign = cv >= 0 ? 1f : -1f; }
            }
            return bestS;
        }
    }
}
