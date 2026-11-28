// GameManagerProgress.cs — driver progression and race rules in a race, another part of GameManager. Rules and
// numbers: Progression.cs; the profile: MenuProfile (MenuDriver.cs). Everything here READS the game and physics
// state; no car's step or collision response is changed (the two Traffic.Collide calls below are the same calls, in
// the same order, as before; the cars' state is only read around them). Exception: while the player serves a live
// penalty (PenaltyServe.cs) he is a ghost and his pairs are skipped.
//  * ProgressBegin (end of ResetCar): a new race's tally. A race left unfinished (restart, Leave) keeps its safety
//    penalties (MenuProfile.CommitLoss), not its clean driving.
//  * PlayerContact / RivalContact (StepRivals, 60 Hz): who caused a contact (Blame: nose-to-tail = the car behind
//    closing; alongside = the car moving sideways into the other, both measured against the track direction just
//    before the contact). The player: safety points (light / contact / ramming; a repeat within 10 s of the same car,
//    or a third within 30 s, counts one step harder) and, in clear cases, a 5 s / 10 s time penalty (served on track,
//    PenaltyServe.cs). A rival that
//    causes one gets the same time penalty. Being hit costs nothing.
//  * ProgressTick (Update, every frame): off-track excursions (all four wheels off; time gained against the racing
//    line at the player's pace = a corner cut; a place gained that way must be given back within 15 s or +5 s),
//    barrier hits, clean sectors and laps, overtakes for XP, the rivals' track limits (3 warnings, then +5 s, like the
//    player's in RaceControl). Off track or into a barrier within 2 s of being hit by a car = no penalty.
//  * ProgressFinish (FinishRace): rival time penalties into the result order, each car's driver rating and grid slot
//    into the rows (PgRateRows; DriverRating.cs), the race handed to MenuProfile.
//  * A race quit or restarted after lights out (ProgressAbandon) costs driver rating as a last place (DriverRating.cs).
//  * DrawProgressToast (RaceHud): small, top centre, 2 s: "Contact  ·  Safety –4", "VER  +5 s  ·  collision".
// Race penalties (not the safety rating) follow Settings > Flags & safety car (raceCtl, as the track-limit strikes).
// Nothing is allocated per frame: arrays are made per session, toast strings only when something happens.
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    RaceProgress pgRace;
    // per rival (index = rivals[i]); pair arrays index i * n + j
    double[] pgTouchT = new double[0], pgCausedT = new double[0], pgAiPen = new double[0], pgAiPenT = new double[0], pgAiTouchT = new double[0], pgPairT = new double[0];
    bool[] pgJudged = new bool[0], pgPairJ = new bool[0], pgOwed = new bool[0], pgAhead0 = new bool[0];
    int[] pgRel = new int[0], pgPass = new int[0], pgAiStrikes = new int[0], pgAiCutLap = new int[0];
    // the player
    double pgCaused1 = -1e9, pgCaused2 = -1e9, pgVictimT = -1e9, pgTeleT = -1e9, pgBarrierT = -1e9, pgCollPenT = -1e9, pgOwedT;
    double pgPrevX = double.NaN, pgPrevY, pgExcT0, pgF1 = 1.0 / 3, pgF2 = 2.0 / 3;
    bool pgExc, pgExcAll4, pgExcForced, pgSecClean, pgSecPit, pgLapClean, pgLapPit;
    int pgExcIdx0, pgSec = -1, pgSecLap, pgLap;
    float pgExcOnT;
    double pgPen; int pgPenSerial, pgPenLast;   // time penalties given here to the player (s), for the radio
    // toast
    string pgToast; float pgToastT, pgToastW = -1f; bool pgToastWarn; int pgToastH;
    double pgDrScale;   // the rival level of this race (DiffScale), for the rivals' driver rating
    bool pgDrArmed; float pgQuitD;   // past lights out + 1 s with rivals: leaving now costs pgQuitD levels of DR
    // Settings debug row
    float pgResetAsk = -10f, pgResetDone = -10f;

    bool PgRules => raceCtl != null;    // Flags & safety car On, a race with rivals
    double RivalPen(int i) => i >= 0 && i < pgAiPen.Length ? pgAiPen[i] : 0;
    // the time penalties added to the result: those not served on track (PenaltyServe.cs)
    double PlayerPenaltyTotal() => PsUnserved((jumped ? JumpPenalty : 0) + (raceCtl != null ? raceCtl.penalty : 0) + pgPen);
    // the player went off within 2 s of being hit by a car: not his fault (no strike, no safety points)
    bool PgForcedOff => raceTime - pgVictimT < 2.0;

    // ---------------------------------------------------------------- session
    void ProgressBegin()
    {
        ProgressAbandon();
        if (qualiSession) MenuProfile.PolePending = false;   // a new qualifying: an older pole no longer counts
        int n = rivals.Count;
        pgTouchT = new double[n]; pgCausedT = new double[n]; pgAiPen = new double[n]; pgAiPenT = new double[n]; pgAiTouchT = new double[n];
        pgJudged = new bool[n]; pgOwed = new bool[n]; pgAhead0 = new bool[n];
        pgRel = new int[n]; pgPass = new int[n]; pgAiStrikes = new int[n]; pgAiCutLap = new int[n];
        pgPairT = new double[n * n]; pgPairJ = new bool[n * n];
        for (int i = 0; i < n; i++) { pgTouchT[i] = pgCausedT[i] = pgAiPenT[i] = pgAiTouchT[i] = -1e9; pgAiCutLap[i] = -1; }
        for (int k = 0; k < pgPairT.Length; k++) pgPairT[k] = -1e9;
        pgCaused1 = pgCaused2 = pgVictimT = pgTeleT = pgBarrierT = pgCollPenT = -1e9; pgOwedT = 0;
        pgPrevX = double.NaN; pgExc = false; pgSec = -1; pgLap = 0; pgLapClean = true; pgLapPit = false;
        pgPen = 0; pgToastT = 0;
        PsReset(); // live penalties (PenaltyServe.cs)
        pgDrScale = DiffScale[Mathf.Clamp(diffIdx, 0, DiffScale.Length - 1)];
        if (raceLaps <= 0 || playground || track == null || shotFreeze) return;
        MenuProfile.Load();
        pgRace = new RaceProgress(MenuProfile.Sr, Progression.OfflineWeight) { fromPole = playerSlot == 0 };
        SectorMap.Ends(track.def, out pgF1, out pgF2);
    }

    void ProgressAbandon()
    {
        if (pgRace == null) return;
        MenuProfile.CommitLoss(pgRace.Losses);
        if (pgDrArmed) MenuProfile.CommitQuit(pgQuitD); // a DNF: driver rating as last (worked out at lights out + 1 s)
        pgDrArmed = false;
        pgRace = null;
    }

    // ---------------------------------------------------------------- driver rating (DriverRating.cs)
    double DrScale => pgDrScale > 0 ? pgDrScale : DiffScale[Mathf.Clamp(diffIdx, 0, DiffScale.Length - 1)];
    int RivalDr(int i) => DriverRating.RivalPoints(DrScale, rivals[i].who.pace, rivals[i].who.consistency);
    // rival i's grid slot (PlaceRival: the k-th slot the player does not take)
    int RivalSlot(int i) => i < playerSlot ? i : i + 1;

    // each car's DR points and grid slot into the result rows (rows[0] = the player, rows[i + 1] = rivals[i])
    internal void PgRateRows(MenuUI.Row[] rows)
    {
        if (rows == null || rows.Length == 0) return;
        MenuProfile.Load();
        rows[0].dr = Mathf.Max(1, MenuProfile.Dr); rows[0].grid = playerSlot;
        for (int i = 0; i < rivals.Count && i + 1 < rows.Length; i++) { rows[i + 1].dr = RivalDr(i); rows[i + 1].grid = RivalSlot(i); }
    }

    // shot mode (DebugShots.cs menu=): a menu page over the loaded session
    internal void ShotMenu(bool driver) => ui.ShotPage(driver);
    internal void ShotSetup(bool more) => ui.ShotSetup(more);
    internal void ShotSettings(int tab) { menuOpen = true; if (settingsUi == null) settingsUi = new SettingsUI(BuildSettings(), Save); settingsUi.tab = tab; } // DebugShots menu=settings tab=0..6

    // a quit: scored as last behind every rival, plus DriverRating.QuitCost
    float PgQuitDelta()
    {
        MenuProfile.Load();
        float lp = DriverRating.LevelOf(MenuProfile.Dr), sum = 0f;
        for (int i = 0; i < rivals.Count; i++) sum -= DriverRating.Expect(lp, RivalDr(i), playerSlot < RivalSlot(i), raceLaps);
        return DriverRating.Delta(sum, rivals.Count, raceLaps, true);
    }

    // FinishRace, before the results open: rival penalties into the order, the race to the profile
    void ProgressFinish(MenuUI.Row[] rows)
    {
        double refLap = RefLap();
        PgRateRows(rows); // driver rating inputs (DriverRating.cs)
        for (int i = 0; i < rivals.Count && i + 1 < rows.Length; i++)
            if (RivalPen(i) > 0) { rows[i + 1].progress -= RivalPen(i) / refLap; rows[i + 1].penalty = RivalPen(i); }
        if (rows.Length > 0) rows[0].penalty = PlayerPenaltyTotal();
        if (pgRace == null) return;
        if (idx >= 0) { PgLaps(); PgSectors(); }      // the last lap and sector (FinishRace runs before this frame's tick)
        if (pgExc) PgExcEnd(raceTime, true);
        if (pgRace.clean && pgRace.laps >= 1) pgRace.gainClean += pgRace.Pull(Progression.CleanRacePull);
        MenuProfile.Pending = pgRace; pgRace = null; pgDrArmed = false;
    }

    // ---------------------------------------------------------------- every frame (Update, after the physics)
    void ProgressTick(float dt)
    {
        if (pgToastT > 0 && !Paused) pgToastT -= dt;
        if (pgRace == null) return;
        if (ui.page == MenuUI.Page.Start) { ProgressAbandon(); return; } // left the race (Settings > Leave)
        if (Paused || track == null || idx < 0) return;
        double t = raceTime;
        // a jump of more than 25 m: a recovery or a reset, not driving
        bool tele = !double.IsNaN(pgPrevX) && (car.x - pgPrevX) * (car.x - pgPrevX) + (car.y - pgPrevY) * (car.y - pgPrevY) > 625;
        pgPrevX = car.x; pgPrevY = car.y;
        if (tele) { pgTeleT = t; if (pgExc) PgExcEnd(t, false); Unclean(); if (t > Countdown) pgRace.clean = false; } // a recovery: not a clean race
        if (t < Countdown) return;
        // once, 1 s after lights out: what a quit would cost (ResetCar has already reset the clock, the grid and the
        // rivals by the time a restart reaches ProgressAbandon)
        if (!pgDrArmed && t > Countdown + 1.0 && rivals.Count > 0) { pgQuitD = PgQuitDelta(); pgDrArmed = true; }
        PgLaps();
        PgSectors();
        PgExcursion(dt, t);
        PgBarrier(t);
        PgOvertakes(t);
        PgOwedPlaces(t);
        if (PgRules) PgAiLimits(t);
    }

    void Unclean() { pgSecClean = false; pgLapClean = false; }

    void PgLaps()
    {
        if (pRun != null) pgLapPit = true;
        if (pLap == pgLap) return;
        if (pLap > pgLap)
        {
            if (pgLap >= 1) { pgRace.laps++; if (pgLapClean && !pgLapPit) pgRace.cleanLaps++; }
            pgLapClean = true; pgLapPit = pRun != null;
        }
        pgLap = pLap;
    }

    // a sector driven clean (no incident, not in the pit lane, started after the first crossing of the line): a small pull
    void PgSectors()
    {
        double f = (double)idx / track.N;
        int s = f < pgF1 ? 0 : f < pgF2 ? 1 : 2;
        if (pRun != null) pgSecPit = true;
        if (pgSec < 0) { pgSec = s; pgSecClean = true; pgSecPit = pRun != null; pgSecLap = pLap; return; }
        if (s == pgSec) return;
        if (s == (pgSec + 1) % 3 && pgSecLap >= 1 && pgSecClean && !pgSecPit)
        {
            pgRace.gainSectors += pgRace.Pull(Progression.SectorPull);
            pgRace.cleanSectors++;
        }
        pgSec = s; pgSecClean = true; pgSecPit = pRun != null; pgSecLap = pLap;
    }

    static bool WheelOn(SurfaceType ty) => SurfaceTable.OnTrack(ty) || ty == SurfaceType.Kerb || ty == SurfaceType.SausageKerb;
    static int WheelsOn(FormulaCar c) { int on = 0; for (int w = 0; w < 4; w++) if (WheelOn(c.wType[w])) on++; return on; }

    // off-track excursions: from the first wheel beyond the kerbs until three are back on for 0.4 s; an incident when
    // all four were off
    void PgExcursion(float dt, double t)
    {
        if (pRun != null) { if (pgExc) PgExcEnd(t, false); return; }
        int on = WheelsOn(car);
        if (!pgExc)
        {
            if (on == 4) return;
            pgExc = true; pgExcAll4 = false; pgExcT0 = t; pgExcIdx0 = idx; pgExcOnT = 0; pgExcForced = PgForcedOff;
            double me = pLap + (double)idx / track.N;
            for (int i = 0; i < rivals.Count && i < pgAhead0.Length; i++)
            {
                var d = rivals[i]; double r = d.lap + (double)Mathf.Max(d.idx, 0) / track.N;
                pgAhead0[i] = d.idx >= 0 && !d.InPit && r > me && r - me < 0.25;
            }
            return;
        }
        if (on == 0) pgExcAll4 = true;
        if (PgForcedOff) pgExcForced = true;
        pgExcOnT = on >= 3 ? pgExcOnT + dt : 0f;
        if (pgExcOnT > 0.4f) PgExcEnd(t, true);
    }

    void PgExcEnd(double t, bool judge)
    {
        pgExc = false;
        if (!pgExcAll4 || pgExcForced) return;
        Unclean();
        if (judge && PgTimeGained(t))
        {
            float lost = pgRace.Penalise(Progression.PenCut); pgRace.cuts++; pgRace.lossCut += lost;
            PgToastSr("Corner cut", lost);
            if (PgRules) PgCheckAdvantage(t);
        }
        else
        {
            float lost = pgRace.Penalise(Progression.PenOffTrack); pgRace.offTracks++; pgRace.lossOff += lost;
            PgToastSr("Off track", lost);
        }
    }

    // time gained off track: the stretch took clearly less than the racing line needs at the player's own pace
    bool PgTimeGained(double t)
    {
        var ln = line; int N = track.N;
        if (ln == null || ln.speed == null || ln.N != N || ln.lapTime <= 0 || idx < 0) return false;
        int k = ((idx - pgExcIdx0) % N + N) % N;
        if (k > N / 2 || k * track.ds < 30) return false;
        double refT = 0;
        for (int j = 0; j < k; j++) refT += track.ds / System.Math.Max(5.0, ln.speed[(pgExcIdx0 + j) % N]);
        double pace = bestLap ?? lastLap ?? ln.lapTime * 1.08;
        double ratio = System.Math.Max(0.97, System.Math.Min(1.4, pace / ln.lapTime)), expect = refT * ratio;
        return t - pgExcT0 < expect - System.Math.Max(0.3, 0.1 * expect);
    }

    // a corner cut that gained places: those cars must be let by within 15 s, or +5 s
    void PgCheckAdvantage(double t)
    {
        double me = pLap + (double)idx / track.N; int owed = 0;
        for (int i = 0; i < rivals.Count && i < pgOwed.Length; i++)
        {
            var d = rivals[i]; if (!pgAhead0[i] || d.idx < 0 || d.InPit) continue;
            double r = d.lap + (double)d.idx / track.N;
            if (me > r && me - r < 0.25) { pgOwed[i] = true; owed++; }
        }
        if (owed == 0) return;
        pgOwedT = t + 15;
        Banner("GIVE THE PLACE BACK", owed > 1 ? "GAINED " + owed + " PLACES OFF TRACK" : "GAINED A PLACE OFF TRACK", HudYellow);
    }

    void PgOwedPlaces(double t)
    {
        if (pgOwedT <= 0) return;
        double me = pLap + (double)idx / track.N; bool any = false;
        for (int i = 0; i < rivals.Count && i < pgOwed.Length; i++)
        {
            if (!pgOwed[i]) continue;
            var d = rivals[i];
            if (d.idx < 0 || d.InPit || d.lap + (double)d.idx / track.N > me) pgOwed[i] = false;
            else any = true;
        }
        if (!any) { pgOwedT = 0; PgToastInfo("Place given back"); return; }
        if (t < pgOwedT) return;
        for (int i = 0; i < pgOwed.Length; i++) pgOwed[i] = false;
        pgOwedT = 0;
        PlayerPenalty(5, "LEAVING THE TRACK AND GAINING AN ADVANTAGE");
    }

    // a barrier hit (GameManager sets hitT = 1.5 in the physics step it happens): one per 3 s, not right after being hit
    void PgBarrier(double t)
    {
        if (hitT < 1.49f || pRun != null || t - pgBarrierT < 3) return;
        pgBarrierT = t;
        if (PgForcedOff) return;
        if (pgExc) { pgExcAll4 = true; return; }        // part of the excursion: one incident
        Unclean();
        float lost = pgRace.Penalise(Progression.PenBarrier); pgRace.barriers++; pgRace.lossBarrier += lost;
        PgToastSr("Barrier", lost);
    }

    // on-track passes (XP): a rival close ahead (< a quarter lap, same lap) is now behind; at most 2 per rival
    void PgOvertakes(double t)
    {
        double me = pLap + (double)idx / track.N;
        bool ok = pRun == null && !pgExc && !psGhost && t > Countdown + 1 && t - pgTeleT > 1.5 && !(raceCtl != null && raceCtl.Neutral);
        for (int i = 0; i < rivals.Count && i < pgRel.Length; i++)
        {
            var d = rivals[i];
            if (d.idx < 0 || d.InPit) { pgRel[i] = 0; continue; }
            double diff = me - (d.lap + (double)d.idx / track.N);
            int sg = diff > 0 && diff < 0.25 ? 1 : diff < 0 && diff > -0.25 ? -1 : 0;
            if (ok && pgRel[i] == -1 && sg == 1 && !d.recovered && d.car.vx > 12 && pgPass[i] < 2) { pgPass[i]++; pgRace.overtakes++; }
            pgRel[i] = sg;
        }
    }

    // the rivals' track limits: all four wheels off at speed, once per lap; the 4th = +5 s (as the player's)
    void PgAiLimits(double t)
    {
        if (t < Countdown + 2) return;
        for (int i = 0; i < rivals.Count && i < pgAiStrikes.Length; i++)
        {
            var d = rivals[i];
            if (d.idx < 0 || d.InPit || d.recovered || pgAiCutLap[i] == d.lap || d.car.vx < 15 || t - pgAiTouchT[i] < 3) continue;
            if (WheelsOn(d.car) > 0) continue;
            pgAiCutLap[i] = d.lap;
            if (++pgAiStrikes[i] > 3) { pgAiStrikes[i] = 0; RivalPenalty(i, 5, "track limits"); }
        }
    }

    // ---------------------------------------------------------------- contacts (StepRivals, 60 Hz)
    static void WorldV(FormulaCar c, out double wx, out double wy)
    {
        double ch = System.Math.Cos(c.heading), sh = System.Math.Sin(c.heading);
        wx = c.vx * ch - c.vy * sh; wy = c.vx * sh + c.vy * ch;
    }

    // Who caused a contact between car a and car b, from their state just before it. (tx, ty) = the track direction.
    // +1 = a, -1 = b, 0 = nobody clearly (a racing incident). c = closing speed along the line between them (m/s).
    static int Blame(double ax, double ay, double avx, double avy, double bx, double by, double bvx, double bvy, double tx, double ty, out double c)
    {
        double dx = bx - ax, dy = by - ay, d = System.Math.Sqrt(dx * dx + dy * dy);
        c = d > 1e-6 ? ((avx - bvx) * dx + (avy - bvy) * dy) / d : 0;
        double nx = -ty, ny = tx;                                   // the track's left
        double along = dx * tx + dy * ty, side = dx * nx + dy * ny;
        if (System.Math.Abs(along) > 2.4 && System.Math.Abs(side) < 1.9)
        {
            // nose to tail: the car behind, closing, caused it
            double dv = (avx - bvx) * tx + (avy - bvy) * ty;        // a faster along the track than b
            if (along > 0) return dv > 0.5 ? 1 : 0;                 // b ahead
            return dv < -0.5 ? -1 : 0;                              // a ahead, b closing from behind
        }
        // alongside (or overlapping at an angle): the car moving sideways into the other caused it
        double s = side >= 0 ? 1 : -1;
        double aIn = (avx * nx + avy * ny) * s, bIn = -(bvx * nx + bvy * ny) * s;
        if (aIn - bIn > 0.6 && aIn > 0.5) return 1;
        if (bIn - aIn > 0.6 && bIn > 0.5) return -1;
        return 0;
    }

    // rival i and the player: Traffic.Collide(rivals[i].car, car) exactly as before, judged when it hit
    void PlayerContact(int i)
    {
        if (psGhost) return; // serving a penalty: a ghost, no contact with any car (PenaltyServe.cs)
        var rc = rivals[i].car;
        double dx = rc.x - car.x, dy = rc.y - car.y;
        if (pgRace == null || idx < 0 || dx * dx + dy * dy > 49) { Traffic.Collide(rc, car); return; }
        double px0 = car.x, py0 = car.y, rx0 = rc.x, ry0 = rc.y;
        WorldV(car, out double pvx, out double pvy); WorldV(rc, out double rvx, out double rvy);
        if (!Traffic.Collide(rc, car)) return;
        var p = track.pts[idx];
        int who = Blame(px0, py0, pvx, pvy, rx0, ry0, rvx, rvy, p.tx, p.ty, out double c);
        double t = raceTime;
        bool fresh = t - pgTouchT[i] > 0.6; pgTouchT[i] = t; pgAiTouchT[i] = t;
        if (fresh) pgJudged[i] = false;
        if (pgJudged[i] || c < Progression.TouchMin) return;
        pgJudged[i] = true;
        if (t - pgTeleT < 1.5 || rivals[i].recovered || t < Countdown) return; // a car put back on the track onto another
        if (who < 0) { pgVictimT = t; pgRace.hitsTaken++; RivalCollision(i, c); return; }
        if (who == 0) return;
        PlayerCaused(i, c, t);
    }

    void PlayerCaused(int i, double c, double t)
    {
        bool repeated = t - pgCausedT[i] < 10 || t - pgCaused2 < 30;
        pgCaused2 = pgCaused1; pgCaused1 = t; pgCausedT[i] = t;
        int kind = c >= Progression.RamMin ? 2 : c >= Progression.ContactMin ? 1 : 0;
        if (repeated) kind = Mathf.Min(2, kind + 1);
        Unclean();
        float lost = pgRace.Penalise(kind == 2 ? Progression.PenRam : kind == 1 ? Progression.PenContact : Progression.PenLight);
        if (kind == 2) { pgRace.rams++; pgRace.lossRam += lost; } else { pgRace.contacts++; pgRace.lossContact += lost; }
        PgToastSr(kind == 2 ? "Ramming" : "Contact", lost);
        // a time penalty only for clear cases: a hard hit, or a repeat
        if (!PgRules || t - pgCollPenT < 5) return;
        int secs = c >= Progression.PenaltyMin10 || (repeated && c >= Progression.ContactMin) ? 10 : c >= Progression.PenaltyMin5 ? 5 : 0;
        if (secs == 0) return;
        pgCollPenT = t;
        PlayerPenalty(secs, "CAUSING A COLLISION");
    }

    // rival i and rival j: Traffic.Collide(rivals[i].car, rivals[j].car) exactly as before, judged when it hit
    void RivalContact(int i, int j)
    {
        var a = rivals[i].car; var b = rivals[j].car;
        double dx = b.x - a.x, dy = b.y - a.y; int n = rivals.Count;
        if (pgRace == null || !PgRules || rivals[i].idx < 0 || dx * dx + dy * dy > 49 || n * n != pgPairT.Length) { Traffic.Collide(a, b); return; }
        double ax = a.x, ay = a.y, bx = b.x, by = b.y;
        WorldV(a, out double avx, out double avy); WorldV(b, out double bvx, out double bvy);
        if (!Traffic.Collide(a, b)) return;
        double t = raceTime; int k = i * n + j;
        bool fresh = t - pgPairT[k] > 0.6; pgPairT[k] = t; pgAiTouchT[i] = pgAiTouchT[j] = t;
        if (fresh) pgPairJ[k] = false;
        if (pgPairJ[k]) return;
        var p = track.pts[rivals[i].idx];
        int who = Blame(ax, ay, avx, avy, bx, by, bvx, bvy, p.tx, p.ty, out double c);
        if (c < Progression.TouchMin) return;
        pgPairJ[k] = true;
        if (who == 0 || rivals[i].recovered || rivals[j].recovered || t < Countdown) return;
        RivalCollision(who > 0 ? i : j, c);
    }

    // a rival caused a contact: the same time penalty as the player would get
    void RivalCollision(int i, double c)
    {
        if (!PgRules || raceTime - pgAiPenT[i] < 5) return;
        int secs = c >= Progression.PenaltyMin10 ? 10 : c >= Progression.PenaltyMin5 ? 5 : 0;
        if (secs == 0) return;
        pgAiPenT[i] = raceTime;
        RivalPenalty(i, secs, "collision");
    }

    // ---------------------------------------------------------------- penalties
    // the player's: served on track at once (PenaltyServe.cs); until served it counts in pitLoss like the jump start
    // and race control's (the order, the tower and, if the flag comes first, the results)
    void PlayerPenalty(int secs, string why)
    {
        bool live = PsAward(secs); pgPen += secs; pgPenSerial++; pgPenLast = secs;
        Banner("PENALTY", (live ? "" : "+") + secs + " s  ·  " + why, HudAccent);
    }

    void RivalPenalty(int i, int secs, string why)
    {
        if (i < 0 || i >= pgAiPen.Length) return;
        pgAiPen[i] += secs;
        PgToastInfo(Code(rivals[i].who) + "  +" + secs + " s  ·  " + why);
    }

    // ---------------------------------------------------------------- toast (RaceHud.DrawRaceHud)
    void PgToastSr(string what, float lost) { if (lost >= 0.05f) PgToast(what + "  ·  Safety " + Progression.Signed(-lost), true); }
    void PgToastInfo(string s) => PgToast(s, false);
    void PgToast(string s, bool warn) { pgToast = s; pgToastT = 2f; pgToastW = -1f; pgToastWarn = warn; }

    void DrawProgressToast()
    {
        if (pgToastT <= 0f || pgToast == null || playground) return;
        if (pgToastW < 0f || pgToastH != Screen.height) { pgToastW = UiKit.Width(pgToast, UiKit.Semi, 8.5f); pgToastH = Screen.height; } // once per toast
        float a = Mathf.Clamp01((2f - pgToastT) / 0.15f) * Mathf.Clamp01(pgToastT / 0.3f);
        float w = pgToastW + S(30), h = S(20), x = Mathf.Round(Screen.width * 0.5f - w * 0.5f), y = hudT + S(2) - (1f - a) * S(4);
        var old = Fade(a);
        var r = new Rect(x, y, w, h);
        Disc(r, HudPanel);
        UiKit.Outline(r, GlassLine, 999f, 0.6f);
        Disc(new Rect(x + S(10), y + h * 0.5f - S(2.5f), S(5), S(5)), pgToastWarn ? HudAmber : HudGrey);
        TF(new Rect(x + S(20), y, w - S(26), h), pgToast, UiKit.Semi, 8.5f, Color.white, TextAnchor.MiddleCenter);
        GUI.color = old;
    }

    // ---------------------------------------------------------------- Settings > Game: debug reset (two taps)
    string ProgressResetLabel()
    {
        float now = Time.realtimeSinceStartup;
        return now - pgResetDone < 1.5f ? "Progress reset" : now - pgResetAsk < 3f ? "Tap again to reset" : "Reset progress";
    }
    void ProgressResetTap()
    {
        float now = Time.realtimeSinceStartup;
        if (now - pgResetAsk < 3f) { MenuProfile.ResetProgress(); pgResetDone = now; pgResetAsk = -10f; }
        else pgResetAsk = now;
    }
}
