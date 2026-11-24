// PenaltyServe.cs — live time penalties for the player (GT style), another part of GameManager. A time penalty the
// player gets in a race (track limits, a corner cut that kept the place, causing a collision, race control's VSC /
// safety-car offences) is no longer just added to his time: he serves it on track, at once.
//  * PsAward: the seconds go into pitLoss as before (the order, the tower and the radio count them at once) and into
//    psOwed. As he serves, both go down together, so his place in the order never jumps.
//  * Serving (PsInput, every physics step, on the player's INPUT only, like a pit limiter; the car's physics is not
//    touched): the timer runs in REAL seconds (a 5 s penalty takes 5 s; Nov 24) while the car is held very slow: the
//    cap is CapShare of v_ref and the car brakes itself down to it. (Before: the time lost, lost += (1 - v / v_ref) dt, v_ref = the racing line's target speed at the car's point,
//    wet-scaled as RacingLineView does (sqrt of the grip) and scaled to the player's own pace (his best lap against
//    the line's lap; 0.97-1.3), so the time he really loses is the penalty. His speed is capped at CapShare of v_ref
//    (never below 50 km/h): the throttle fades out over the last few km/h below the cap, and boost, Overtake Mode
//    and the deploy map are cut near it (above the cap the throttle is 0, so the MGU-K gives nothing either).
//    done when the lost time reached the penalty.) Serving waits under SC / VSC, in the pit lane and in the last 200 m
//    of the race; what is left at the flag stays added to his time (as before).
//  * Ghost: while serving, no contact between the player and any rival (PlayerContact skips the pair; rivals still
//    hit each other) and the rivals' race craft does not see him (PsAiSees: AiDriver.Think gets no player car). After
//    the penalty he stays a ghost until no car overlaps him, then fades back in over 1 s (the rivals see him again
//    during that second; contact returns at its end).
//  * Look: CarGhost.Set (F2_GHOST twin materials, dithered fade ~80 % of the pixels at 1): a slow gentle pulse while
//    a ghost, 0 in the cockpit / halo views.
//  * HUD (from DrawRcHud): "PENALTY · SLOW DOWN" in the race HUD's dark glass, an amber dot, the seconds left and a
//    thin amber bar of the time still to serve; "PENALTY · ON HOLD" while serving waits.
//  * Results (PsResults, after MenuUI.ShowResults): only the unserved seconds are added; the chip reads "served".
// Rivals keep their time penalties as before. The safety rating is not touched. Nothing is allocated per frame (the
// renderer list once per car model, the seconds strings once per tenth value ever shown).
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    const double CapShare = 0.35, CapMin = 50 / 3.6, CapBand = 0.04, HoldLastM = 200;
    const float GhostFadeIn = 1f, GhostFadeOut = 0.5f, GhostPulseT = 1.8f;
    double psOwed, psBatch, psServed, psBank, psVScale = 1;   // s still to serve, this batch (bar), served, lost not yet booked
    bool psServing, psLive, psGhost, psFading;              // serving now; serving is possible; ghost (no contact); fading in
    float psVis;                                            // ghost look 0..1 (before the pulse)
    // the look (psSecs: "3.2 s" per tenth, made once per value)
    static readonly int GhostId = Shader.PropertyToID("_Ghost");
    MaterialPropertyBlock psMpb; Renderer[] psRend; CarModel psRendFor; float psGhostSet = -1f;
    static readonly string[] psSecs = new string[1000];

    bool PsOn => psLive && raceCtl != null && raceLaps > 0 && !playground;

    void PsReset()
    {
        psOwed = psBatch = psServed = psBank = 0; psServing = psGhost = psFading = false; psVis = 0f;
        psLive = true;
        PsWrite(0f);
        if (model != null) CarGhost.Prepare(model.gameObject); // twins + shader variant warmed now, not mid-race
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
        if (!psServing || line == null || line.speed == null || track == null || idx < 0 || idx >= line.N) return inp;
        double v = System.Math.Max(0, car.vx);
        double vref = System.Math.Max(5, line.speed[idx] * psVScale);
        double cap = System.Math.Max(CapMin, vref * CapShare);
        // input only: the throttle fades out over the last CapBand below the cap (0 above it); no boost or Overtake
        // Mode while serving; near the cap no deploy map either
        car.boostOn = false; car.otOn = false;
        if (v > cap * 0.95 && car.deployMap > 0) car.deployMap = 0;
        double k = (cap - v) / (cap * CapBand + 0.5);
        if (k < 1) inp.throttle *= k > 0 ? k : 0;
        // well above the cap the car brakes itself down to it, like a pit limiter (Adi: "very slow")
        if (v > cap * 1.05) { double b = System.Math.Min(0.6, (v / cap - 1.05) * 1.5); if (inp.brake < b) inp.brake = b; }
        // the timer runs in real seconds (Adi: a 5 s penalty must take 5 s, not ~23 s of lost time at 78 %)
        double take = System.Math.Min(H, psOwed);
        psOwed -= take; psServed += take; pitLoss -= take;
        if (psOwed <= 1e-9) { psOwed = 0; psBatch = 0; psBank = 0; psServing = false; }
        return inp;
    }

    // ---------------------------------------------------------------- every frame (RcFrame)
    void PsFrame(float dt)
    {
        bool live = PsOn && !ui.Showing && !replaying && track != null && idx >= 0;
        if (!live) { psServing = false; if (psGhost || psVis > 0f) { psGhost = psFading = false; psVis = 0f; } PsLook(); return; }
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

    // the _Ghost value on the player's car: a slow gentle pulse (0.78-1 of psVis), none from the cockpit / halo
    void PsLook()
    {
        float g = psVis;
        if (g > 0f) g *= 0.89f + 0.11f * Mathf.Sin(Time.time * (2f * Mathf.PI / GhostPulseT));
        if (camMode == 2 || camMode == 3) g = 0f;
        if (model != psRendFor)
        {
            if (psGhostSet != 0f) PsWrite(0f);              // the car model was swapped (PickPlayerModel): clear the old one
            psRendFor = model; psGhostSet = 0f;              // (a new model has nothing of ours on it)
            if (g <= 0f) return;
        }
        if (Mathf.Abs(g - psGhostSet) < 0.004f && (g > 0f || psGhostSet == 0f)) return;
        PsWrite(g);
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
        Disc(new Rect(x + S(10), y + h * 0.5f - S(3), S(6), S(6)), psServing ? HudAmber : HudGrey);
        TF(new Rect(x + S(22), y, w - S(70), h), psServing ? "PENALTY  ·  SLOW DOWN" : "PENALTY  ·  ON HOLD", UiKit.Bold, 8.5f, Color.white);
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
