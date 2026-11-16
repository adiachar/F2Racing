// GameManagerRaceControl.cs — race craft in the game, another part of GameManager. The logic is in
// Physics/RaceControl.cs (pure C#, the same code the harness runs); this file only connects it:
//  * RcPlan: each rival's tyre strategy and start set (dry: the 2-compound rule from 10 laps; wet: inters or wets)
//  * RcSetup / RcStep: race control (flags, VSC, safety car, blue flags) sees every car, the player included
//  * RcFrame: the safety car model, rivals' tyre changes (stripe, wet grip), weather changes, track-limits strikes,
//    penalties (added to pitLoss, like the jump-start penalty)
//  * RcHudTick / DrawRcHud: SAFETY CAR / VSC / SC IN THIS LAP / GREEN FLAG banners, the VSC delta, blue flags
// Settings (GameManagerSettings.cs): "Flags & safety car" Off / On, "Race distance".
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    RaceControl raceCtl; SafetyCarModel scModel;
    int rcPlayerSlot = -1, rcSeenSerial, rcSeenPen, rcWeather; bool rcCutSeen; double rcPlayerProg; bool rcWasNeutral;
    float rcBlink; int[] rcSetSeen = new int[0];
    double rcPlayerPrevX = double.NaN, rcPlayerPrevY;

    // Settings > Race distance: a share of a Grand Prix (305 km, as real F1)
    static readonly string[] RaceDistNames = { "Menu laps", "10 %", "25 %", "50 %", "100 %" };
    static readonly float[] RaceDistPct = { 0, 10, 25, 50, 100 };
    int RaceDistanceLaps(int menuLaps)
    {
        if (raceDistIdx <= 0 || track == null || track.length <= 0) return menuLaps;
        return Mathf.Max(1, Mathf.CeilToInt((float)(305000.0 * RaceDistPct[raceDistIdx] / 100 / track.length)));
    }

    // a rival's tyre plan, from SetupRivals right after the driver is made
    void RcPlan(AiDriver d, int k)
    {
        if (raceLaps <= 0) return;
        int set = d.PlanRace(raceLaps, Weather.Index, k + 1 + mapIdx * 37);
        // the driver's speed profile was made for the weather's best set (tune.grip); another set grips a little differently
        d.paceScale = System.Math.Sqrt(Weather.TrackFactor(set, 1) / Weather.TrackFactor(rivalSet, 1));
    }

    void RcSetup()
    {
        raceCtl = null; rcPlayerSlot = -1; rcCutSeen = false; rcWeather = Weather.Index; rcWasNeutral = false;
        rcSetSeen = new int[rivals.Count];
        for (int i = 0; i < rivals.Count; i++) { rcSetSeen[i] = -1; rivals[i].rc = null; rivals[i].slot = -1; }
        if (flagsMode == 1 && raceLaps > 0 && !playground && track != null && line != null && rivals.Count > 0)
        {
            raceCtl = new RaceControl(track, line, mapIdx * 101 + rivals.Count) { totalLaps = raceLaps };
            raceCtl.Init(rivals.Count + 1);
            for (int i = 0; i < rivals.Count; i++) { rivals[i].rc = raceCtl; rivals[i].slot = i; }
            rcPlayerSlot = rivals.Count; raceCtl.playerSlot = rcPlayerSlot;
            rcSeenSerial = raceCtl.serial; rcSeenPen = raceCtl.penaltySerial;
        }
        rcPlayerPrevX = double.NaN;
        if (scModel != null) scModel.Show(false);
    }

    // every AI tick, after the rivals have thought
    void RcStep()
    {
        if (raceCtl == null) return;
        for (int i = 0; i < rivals.Count; i++) raceCtl.Feed(i, rivals[i]);
        // the player: a jump of more than 25 m between two ticks is a recovery (Back on track)
        bool rec = !double.IsNaN(rcPlayerPrevX) && (car.x - rcPlayerPrevX) * (car.x - rcPlayerPrevX) + (car.y - rcPlayerPrevY) * (car.y - rcPlayerPrevY) > 625;
        rcPlayerPrevX = car.x; rcPlayerPrevY = car.y;
        var dm = car.damage;
        rcPlayerProg = pLap + (idx < 0 ? 0 : (double)idx / track.N) - pitLoss / RefLap();
        raceCtl.cars[rcPlayerSlot] = new RcCar
        {
            idx = idx, dist = idx < 0 ? 0 : pLap + (double)idx / track.N, progress = rcPlayerProg,
            v = System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy), recovered = rec,
            damage = dm.wingFL + dm.wingFR + dm.wingRear + dm.floor + dm.suspFL + dm.suspFR + dm.suspRL + dm.suspRR
        };
        PitRcMask(); // cars in the pit lane are not on the track for race control (PitCrew.cs)
        raceCtl.Update(4 * H, raceTime, raceTime > Countdown + 2);
    }

    double RcPitLoss(double normal) => raceCtl != null ? raceCtl.PitLoss(normal) : normal;

    // every frame
    void RcFrame(float dt)
    {
        rcBlink += dt;
        // rivals' tyre changes: stripe and the wet grip of the new set
        for (int i = 0; i < rivals.Count && i < rcSetSeen.Length; i++)
        {
            var d = rivals[i]; if (d.setSerial == rcSetSeen[i]) continue;
            rcSetSeen[i] = d.setSerial;
            d.paceScale = System.Math.Sqrt(Weather.TrackFactor(d.tyreSet, 1) / Weather.TrackFactor(rivalSet, 1));
            var stripe = Weather.Stripe(d.tyreSet);
            if (i < rivalModels.Count && rivalModels[i] != null) rivalModels[i].SetStripe(stripe);
            if (i < rivalFar.Count && rivalFar[i] != null) rivalFar[i].SetStripe(stripe);
        }
        // the weather changed in the settings: the rivals on the wrong tyres box
        if (Weather.Index != rcWeather) { rcWeather = Weather.Index; foreach (var d in rivals) d.SetWeather(rcWeather); }
        if (raceCtl == null) { if (scModel != null) scModel.Show(false); return; }
        // track limits: one strike per lap with all four tyres off (the lap is deleted as before)
        if (cut && !rcCutSeen && lapStart.HasValue && raceTime > Countdown)
        {
            int s = raceCtl.TrackLimits();
            if (s <= 2) Banner("TRACK LIMITS", "WARNING " + s + " OF 3", HudYellow);
            else if (s == 3) Banner("TRACK LIMITS", "BLACK AND WHITE FLAG", HudYellow);
        }
        rcCutSeen = cut;
        // penalties (track limits, VSC delta, overtaking under the safety car): added to the race time
        if (raceCtl.penaltySerial != rcSeenPen)
        {
            rcSeenPen = raceCtl.penaltySerial;
            pitLoss += 5;
            Banner("PENALTY", "+5 s  ·  " + raceCtl.penaltyWhy, HudAccent);
        }
        // the safety car itself
        if (raceCtl.ScPose(out double sx, out double sy, out double sh))
        {
            if (scModel == null && car != null) scModel = SafetyCarModel.Create(car.cfg.wheelbase, car.cfg.frontWeight, car.cfg.wheelRadius);
            if (scModel != null) { scModel.Show(true); scModel.Place(sx, sy, sh, raceCtl.scV, dt, raceCtl.flag == Flag.Sc); }
        }
        else if (scModel != null) scModel.Show(false);
    }

    // banners on a change of flag (from HudTick)
    void RcHudTick()
    {
        if (raceCtl == null || raceCtl.serial == rcSeenSerial) return;
        rcSeenSerial = raceCtl.serial;
        switch (raceCtl.flag)
        {
            case Flag.Sc: Banner("SAFETY CAR", "DEPLOYED  ·  NO OVERTAKING", HudYellow); break;
            case Flag.ScIn: Banner("SAFETY CAR", "SC IN THIS LAP", HudYellow); break;
            case Flag.Restart: Banner("SAFETY CAR", "IN  ·  RESTART AT THE LINE", HudYellow); break;
            case Flag.Vsc: Banner("VSC", "VIRTUAL SAFETY CAR  ·  KEEP TO THE DELTA", HudYellow); break;
            case Flag.VscEnding: Banner("VSC ENDING", "GREEN FLAG SOON", HudYellow); break;
            case Flag.Green: if (rcWasNeutral) Banner("GREEN FLAG", "TRACK CLEAR  ·  RACING", HudGreen); break;
        }
        rcWasNeutral = raceCtl.Neutral || raceCtl.flag == Flag.Yellow && rcWasNeutral;
    }

    // the read-out under the timing panel: SC / VSC with the delta, blue flag
    void DrawRcHud()
    {
        if (raceCtl == null || playground || Paused) return;
        float w = S(200), h = S(22), x = Screen.width / 2f - w / 2, y = Screen.height * 0.245f;
        var f = raceCtl.flag;
        if (raceCtl.Neutral)
        {
            bool vsc = f == Flag.Vsc || f == Flag.VscEnding;
            string title = vsc ? (f == Flag.VscEnding ? "VSC ENDING" : "VSC") : f == Flag.ScIn ? "SC IN" : f == Flag.Restart ? "RESTART" : "SAFETY CAR";
            Para(new Rect(x, y, S(84), h), HudYellow, true, false);
            T(new Rect(x, y, S(84), h), title, 11, TextAnchor.MiddleCenter, Color.black);
            Para(new Rect(x + S(86), y, w - S(86), h), HudPanel, false, true);
            bool showDelta = vsc || (raceCtl.ScOnTrack && rcPlayerSlot >= 0 && raceCtl.gapAhead[rcPlayerSlot] > 150);
            if (showDelta)
            {
                double dl = raceCtl.vscDelta; bool bad = dl < 0;
                bool on = !bad || Mathf.Repeat(rcBlink * 4, 1) > 0.4f;
                string s = (dl >= 0 ? "DELTA +" : "DELTA -") + System.Math.Abs(dl).ToString("0.0") + (bad ? "  SLOW DOWN" : "");
                if (on) T(new Rect(x + S(92), y, w - S(96), h), s, 11, TextAnchor.MiddleLeft, bad ? HudAccent : HudGreen);
            }
            else T(new Rect(x + S(92), y, w - S(96), h), f == Flag.Restart ? "NO OVERTAKING TO THE LINE" : "NO OVERTAKING", 10, TextAnchor.MiddleLeft, Color.white);
            y += h + S(4);
        }
        if (rcPlayerSlot >= 0 && raceCtl.Blue(rcPlayerSlot))
        {
            Para(new Rect(x, y, S(84), h), HudBlue, true, false);
            T(new Rect(x, y, S(84), h), "BLUE FLAG", 11, TextAnchor.MiddleCenter, Color.white);
            Para(new Rect(x + S(86), y, w - S(86), h), HudPanel, false, true);
            T(new Rect(x + S(92), y, w - S(96), h), "LET THE FASTER CAR BY", 10, TextAnchor.MiddleLeft, Color.white);
        }
    }
}
