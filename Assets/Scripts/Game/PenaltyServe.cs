// PenaltyServe.cs — live time penalties for the player (GT style), another part of GameManager. A time penalty the
// player gets in a race (track limits, a corner cut that kept the place, causing a collision, race control's VSC /
// safety-car offences) is no longer just added to his time: he serves it on track, at once.
//  * PsAward: the seconds go into pitLoss as before (the order, the tower and the radio count them at once) and into
//    psOwed. As he serves, both go down together, so his place in the order never jumps.
//  * Serving (PsInput, every physics step, on the player's INPUT only; the car's physics is not touched): the game
//    drives the car (Adi, Nov 25: "let the game take over the control"). An autopilot (pure pursuit on the racing line,
//    the cool-down lap's, PostRace.cs) steers and works the pedals to the serving speed: CapShare of v_ref, v_ref = the
//    racing line's target speed a little ahead, wet-scaled as RacingLineView does (sqrt of the grip) and scaled to the
//    player's own pace (his best lap against the line's lap; 0.97-1.3); never below CapMin, never above 85 % of v_ref
//    (Monaco's hairpin); it slows from racing speed at most AutoDecel (~1.8 g, no stamp on the brakes) unless the line
//    itself brakes harder. The player's steering and pedals are ignored meanwhile; no boost or Overtake Mode. Control blends
//    in over AutoIn and back to the player over AutoOut when the penalty is served (or serving goes on hold). The timer
//    runs in REAL seconds (a 5 s penalty takes 5 s; Nov 24). Serving waits under SC / VSC, in the pit lane, before the
//    start and in the last 200 m of the race (the player drives then); what is left at the flag stays added to his time.
//  * Ghost: while serving, no contact between the player and any rival (PlayerContact skips the pair; rivals still
//    hit each other) and the rivals' race craft does not see him (PsAiSees: AiDriver.Think gets no player car). After
//    the penalty he stays a ghost until no car overlaps him, then fades back in over 1 s (the rivals see him again
//    during that second; contact returns at its end).
//  * Look: CarGhost.Set (F2_GHOST twin materials, a dithered screen-door fade, CarGhost.cginc drops 0.65 g of the
//    pixels): g blinks softly between BlinkLo and 1 at BlinkHz (GT style: ~37-62 % of the car gone). Set is called every
//    frame while a ghost (the twins copy the live brake glow / wheel blur / LEDs). Prepared at session start for BOTH
//    player models (PickPlayerModel's full and light car), renderers looked up again when a ghost starts (shift lights,
//    damage pieces made after the session start). Cockpit / halo views: 0 (see PsLook).
//  * HUD (from DrawRcHud): "PENALTY · AUTO DRIVE" in the race HUD's dark glass, an amber dot blinking with the ghost,
//    the seconds left and a thin amber bar of the time still to serve; "PENALTY · ON HOLD" while serving waits.
//  * Results (PsResults, after MenuUI.ShowResults): only the unserved seconds are added; the chip reads "served".
//  * Screenshots (DebugShots.cs): pen=<s> shows a penalty being served in every shot (ghost + banner), peng=<0..1>
//    pins the blink value (default: the live blink).
// Rivals keep their time penalties as before. The safety rating is not touched. Nothing is allocated per frame (the
// renderer list once per ghost, the seconds strings once per tenth value ever shown).
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    const double CapShare = 0.35, CapMin = 50 / 3.6, CapSafe = 0.85, AutoDecel = 18, AutoIn = 0.3, AutoOut = 0.5, HoldLastM = 200;
    const float GhostFadeIn = 1f, GhostFadeOut = 0.5f, BlinkHz = 2f, BlinkLo = 0.55f;
    double psOwed, psBatch, psServed, psBank, psVScale = 1;   // s still to serve, this batch (bar), served, lost not yet booked
    double psAuto, psCapNow;                                // autopilot weight on the inputs 0..1; its rate-limited speed target
    bool psServing, psLive, psGhost, psFading;              // serving now; serving is possible; ghost (no contact); fading in
    float psVis;                                            // ghost look 0..1 (before the blink)
    float shotPen = 0f, shotPenG = -1f;                     // screenshots: pen= seconds being served, peng= pinned blink value
    // the look (psSecs: "3.2 s" per tenth, made once per value)
    CarModel psRendFor; float psGhostSet = -1f;
    static readonly string[] psSecs = new string[1000];

    bool PsOn => psLive && raceCtl != null && raceLaps > 0 && !playground;

    void PsReset()
    {
        psOwed = psBatch = psServed = psBank = 0; psServing = psGhost = psFading = false; psVis = 0f; psAuto = 0;
        psLive = true;
        PsWrite(0f);
        // twins + shader variants warmed now, not mid-race: the model in use and its LOD twin (PickPlayerModel)
        if (model != null) CarGhost.Prepare(model.gameObject);
        PsPrepare(carFull); PsPrepare(carLite);
    }
    void PsPrepare(CarModel m)
    {
        if (m == null || m == model) return;
        var go = m.gameObject; bool was = go.activeSelf;
        if (!was) go.SetActive(true);   // (an inactive car is not drawn by the warm-up camera; back off in the same call)
        CarGhost.Prepare(go);
        if (!was) go.SetActive(false);
    }

    // a time penalty for the player: true = it is served on track (else it is only added, as before)
    bool PsAward(double secs)
    {
        pitLoss += secs;
        if (!PsOn) return false;
        psOwed += secs; psBatch += secs; if (psBatch < psOwed) psBatch = psOwed; // a new one while serving: the bar grows
        return true;
    }

    // the time penalties added to the player's result: what was not served on track
    double PsUnserved(double total) => System.Math.Max(0, total - psServed);

    // the rivals' race craft: no player while he is a ghost (AiDriver.Think, a game-level choice; read-only)
    FormulaCar PsAiSees() => psGhost && !psFading ? null : car;

    // ---------------------------------------------------------------- every physics step (the player's input)
    CarInput PsInput(CarInput inp)
    {
        if (line == null || line.speed == null || track == null || idx < 0 || idx >= line.N) { psAuto = 0; return inp; }
        double want = psServing ? 1 : 0;
        if (psAuto <= 0 && want <= 0) return inp;
        double v = System.Math.Max(0, car.vx);
        if (psAuto <= 0) psCapNow = v; // taking over: slow down from the speed he has, not from a stale target
        psAuto = want > psAuto ? System.Math.Min(1, psAuto + H / AutoIn) : System.Math.Max(0, psAuto - H / AutoOut);
        // the serving speed: CapShare of v_ref a little ahead (CapMin at least, CapSafe of v_ref at most); the target
        // comes down at AutoDecel at most, except where the racing line itself brakes harder
        int N = line.N, ip = (idx + (int)System.Math.Round((4 + 0.4 * v) / track.ds)) % N;
        double vref = System.Math.Max(5, System.Math.Min(line.speed[ip], line.speed[idx]) * psVScale);
        double safe = CapSafe * vref, cap = System.Math.Min(safe, System.Math.Max(CapMin, vref * CapShare));
        psCapNow = cap >= psCapNow ? cap : System.Math.Min(safe, System.Math.Max(cap, psCapNow - AutoDecel * H));
        var auto = PsAutopilot(psCapNow, v);
        car.boostOn = false; car.otOn = false;
        if (car.deployMap > 0 && v > psCapNow * 0.95) car.deployMap = 0;
        double a = psAuto, b = 1 - a;
        inp.steer = auto.steer * a + inp.steer * b; inp.throttle = auto.throttle * a + inp.throttle * b; inp.brake = auto.brake * a + inp.brake * b;
        // the timer runs in real seconds (Adi: a 5 s penalty must take 5 s)
        if (psServing)
        {
            double take = System.Math.Min(H, psOwed);
            psOwed -= take; psServed += take; pitLoss -= take;
            if (psOwed <= 1e-9) { psOwed = 0; psBatch = 0; psBank = 0; psServing = false; }
        }
        return inp;
    }

    // pure pursuit on the racing line, pedals to the speed target, gently (PostRace.cs Autopilot, the same maths)
    CarInput PsAutopilot(double cap, double v)
    {
        int N = line.N;
        double Ld = 5 + 0.22 * v; int ia = (idx + System.Math.Max(1, (int)System.Math.Round(Ld / track.ds))) % N;
        double room = track.HalfWidth(ia) - 1.0;
        double off = System.Math.Max(-room, System.Math.Min(room, line.off[ia]));
        var pa = track.pts[ia];
        double tx = pa.x + pa.nx * off - car.x, ty = pa.y + pa.ny * off - car.y;
        double ch = System.Math.Cos(car.heading), sh = System.Math.Sin(car.heading);
        double fx = ch * tx + sh * ty, fy = -sh * tx + ch * ty;
        double d2 = System.Math.Max(1.0, fx * fx + fy * fy);
        double delta = System.Math.Atan(car.cfg.wheelbase * 2 * fy / d2);
        double lockNow = car.lockAngle > 1e-4 ? car.lockAngle : car.cfg.maxLockDeg * System.Math.PI / 180;
        double steer = System.Math.Max(-1, System.Math.Min(1, -delta / lockNow));
        double err = cap - v;
        double thr = System.Math.Max(0, System.Math.Min(v < 12 ? 0.35 : 0.6, err * 0.35)), brk = System.Math.Max(0, System.Math.Min(1, -err * 0.45));
        if (brk > 0.02) thr = 0;
        return new CarInput(steer, thr, brk);
    }

    // ---------------------------------------------------------------- every frame (RcFrame)
    void PsFrame(float dt)
    {
        if (shotFreeze) { PsShot(); return; }
        bool live = PsOn && !ui.Showing && !replaying && track != null && idx >= 0;
        if (!live) { psServing = false; psAuto = 0; if (psGhost || psVis > 0f) { psGhost = psFading = false; psVis = 0f; } PsLook(); return; }
        // serving waits: safety car / VSC, the pit lane, the last 200 m of the race, before the start
        bool hold = raceCtl.Neutral || pRun != null || raceTime < Countdown + 1
            || (pLap >= raceLaps && (track.N - idx) * track.ds < HoldLastM);
        psServing = psOwed > 0 && !hold;
        if (psServing && line != null)
        {
            double grip = System.Math.Max(0.3, System.Math.Min(1.2, Weather.TrackFactor(carSet, 1)));
            double ws = System.Math.Sqrt(grip), pace = bestLap ?? lastLap ?? 0, refLap = line.lapTime / ws;
            double ratio = pace > 0 && refLap > 0 ? System.Math.Max(0.97, System.Math.Min(1.3, pace / refLap)) : 1.04;
            psVScale = ws / ratio;
        }
        else psBank = 0;
        // ghost: on while serving; afterwards held until no car overlaps him, then a 1 s fade-in
        if (psServing) { psGhost = true; psFading = false; psVis = Mathf.Min(1f, psVis + dt / GhostFadeOut); }
        else if (psGhost)
        {
            if (PsOverlap()) { psFading = false; psVis = Mathf.Min(1f, psVis + dt / GhostFadeOut); }
            else
            {
                psFading = true; psVis -= dt / GhostFadeIn;
                if (psVis <= 0f) { psVis = 0f; psGhost = psFading = false; }
            }
        }
        PsLook();
    }

    // screenshots (DebugShots pen=): the physics is frozen, so the look and the banner are set here
    void PsShot()
    {
        bool on = shotPen > 0f && !playground;
        psOwed = psBatch = on ? shotPen : 0; psServing = psGhost = on; psFading = false; psVis = on ? 1f : 0f;
        PsLook();
    }

    // any rival's car over the player's (his frame: 6 m along, 2.4 m across, the cars' size and a margin)
    bool PsOverlap()
    {
        double ch = System.Math.Cos(car.heading), sh = System.Math.Sin(car.heading);
        for (int i = 0; i < rivals.Count; i++)
        {
            var d = rivals[i]; if (d.InPit) continue;
            double dx = d.car.x - car.x, dy = d.car.y - car.y;
            if (dx * dx + dy * dy > 49) continue;
            double along = dx * ch + dy * sh, side = -dx * sh + dy * ch;
            if (System.Math.Abs(along) < 6.0 && System.Math.Abs(side) < 2.4) return true;
        }
        return false;
    }

    // the blink: a soft wave between BlinkLo and 1 at BlinkHz (the HUD's amber dot uses it too)
    float PsBlink => shotPenG >= 0f ? shotPenG : BlinkLo + (1f - BlinkLo) * (0.5f + 0.5f * Mathf.Cos(Time.time * (2f * Mathf.PI * BlinkHz)));

    // the _Ghost value on the player's car, in EVERY view (Adi, Nov 25: he drives in halo / cockpit and wants to see it
    // there too). Cockpit / halo get 80 % of it so the wheel's display stays readable through the screen-door; the
    // clip() variant costs Mali early-Z on the tub only while a penalty is being served.
    void PsLook()
    {
        float g = psVis;
        if (g > 0f) g *= PsBlink;
        if (camMode == 2 || camMode == 3) g *= 0.8f;
        if (model != psRendFor)
        {
            if (psGhostSet > 0f) PsWrite(0f);              // the car model was swapped (PickPlayerModel): clear the old one
            psRendFor = model; psGhostSet = 0f;              // (a new model has nothing of ours on it)
        }
        if (g <= 0f && psGhostSet <= 0f) return;
        // a ghost starts: look the car's renderers up again (shift lights, damage pieces made since the session start;
        // once per ghost, the twins already exist)
        if (psGhostSet <= 0f && model != null) CarGhost.Forget(model.gameObject);
        PsWrite(g); // every frame while a ghost: CarGhost.Set keeps the twins' live values (brake glow, wheel blur, LEDs)
    }

    // through CarGhost: it swaps in the F2_GHOST twin materials while g > 0 (a plain _Ghost value alone draws nothing)
    void PsWrite(float g)
    {
        var m = psRendFor != null ? psRendFor : model;
        if (m != null) CarGhost.Set(m.gameObject, g);
        psGhostSet = g;
    }

    // ---------------------------------------------------------------- HUD (DrawRcHud): under the flag read-outs
    void DrawPenaltyServe(float y)
    {
        if (psOwed <= 0 || !PsOn) return;
        float w = S(200), h = S(22), x = Mathf.Round(Screen.width / 2f - w / 2);
        var r = new Rect(x, y, w, h);
        Glass(r, 10f);
        var dot = HudAmber; dot.a = 0.25f + 0.75f * Mathf.Clamp01((PsBlink - BlinkLo) / (1f - BlinkLo)); // blinks with the ghost
        Disc(new Rect(x + S(10), y + h * 0.5f - S(3), S(6), S(6)), psServing ? dot : HudGrey);
        TF(new Rect(x + S(22), y, w - S(70), h), psServing ? "PENALTY  ·  AUTO DRIVE" : "PENALTY  ·  ON HOLD", UiKit.Bold, 8.5f, Color.white);
        int k = Mathf.Clamp(Mathf.CeilToInt((float)psOwed * 10f), 0, psSecs.Length - 1);
        if (psSecs[k] == null) psSecs[k] = (k / 10) + "." + (k % 10) + " s"; // once per value ever shown
        TF(new Rect(x, y, w - S(10), h), psSecs[k], UiKit.Semi, 8.5f, psServing ? HudAmber : HudGrey, TextAnchor.MiddleRight);
        // the time still to serve: a thin bar along the bottom
        float bx = x + S(10), bw = w - S(20), by = y + h - S(4);
        UiKit.Rect(new Rect(bx, by, bw, S(1.5f)), new Color(1f, 1f, 1f, 0.12f), 1f);
        float f = psBatch > 0 ? Mathf.Clamp01((float)(psOwed / psBatch)) : 0f;
        if (f > 0f) UiKit.Rect(new Rect(bx, by, Mathf.Max(S(1.5f), bw * f), S(1.5f)), psServing ? HudAmber : HudGrey, 1f);
    }

    // ---------------------------------------------------------------- results (after MenuUI.ShowResults)
    void PsResults()
    {
        if (psServed > 0.05) ui.PenaltyServed(psServed);
    }
}

public sealed partial class MenuUI
{
    // live penalties served on track (PenaltyServe.cs): the player's chip says "served" instead of the added time
    public void PenaltyServed(double served)
    {
        if (results == null || pens == null) return;
        int sv = (int)System.Math.Round(served);
        for (int i = 0; i < results.Length && i < pens.Length; i++)
        {
            if (!results[i].player) continue;
            pens[i] = results[i].penalty >= 0.5 ? pens[i] + " · " + sv + "s served" : sv + "s served";
        }
        resPenStr = resPenalty >= 0.5 ? $"+{resPenalty:0} s  ·  {sv} s served" : $"{sv} s served";
    }
}
