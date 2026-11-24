// TyrePit.cs — tyre wear, temperature and the pit compound choice (part of GameManager):
//  * Settings > GAME "Tyre wear" (PlayerPrefs "tyreWear"): Auto, Real (x1), x3, x6, x10. Auto: a Soft lasts about 40 %
//    of a race of 5+ laps (x2 in shorter ones, x1 in practice and qualifying); a career round always uses Auto. Any
//    setting but Real also turns on the stronger tyre temperature (FormulaSim.TyreRules: more grip lost away from the
//    compound's window, each compound runs at its own heat). Applied to the player's car and every rival's CarConfig
//    (game-side only: the physics defaults are untouched) at each new car; TyreRules.Fit on every change of set.
//  * The pit compound chips: with BOX armed (and in the pit lane until the crew starts) a row of S / M / H / I / W chips
//    under the top buttons, each with its life at this wear ("~4 laps"); a tap picks the set the crew fits (pitChoice,
//    -1 = the old automatic pick, AutoSet).
//  * The starting tyres: at every race start from the grid (quick race, weekend, career; not qualifying) a card over the
//    grid, before the pre-race intro and the lights (the race clock is held at 0 while it is up): S / M / H chips (+ I / W
//    when the track is wet, or when one of them is the current setting), each with its life and a one-line pro / con,
//    the current setting ("tyreSet") picked; START fits the set (tyreSet, carSet, the physics compound, the stripe) and
//    saves it as the setting. Settings > Setup "Tyre compound" is locked in a race / qualifying (GameManagerSettings).
//  * A wet-weather tyre on a dry track overheats and wears fast (Weather.DryRunning, the player's car: FitPlayerSet).
//  * The HUD's tyre cue: worn % above the compound ring in the dash's tyre quad, COLD / HOT under it while the tyre
//    temperature costs grip.
// No per-frame garbage: every string is made when the session starts (the start card's when it opens).
using FormulaSim;
using UnityEngine;

public partial class GameManager
{
    // ---------------------------------------------------------------- setting
    int tyreWearIdx = -1;
    int TyreWearPref
    {
        get { if (tyreWearIdx < 0) tyreWearIdx = Mathf.Clamp(PlayerPrefs.GetInt("tyreWear", TyreRules.Auto), 0, TyreRules.Names.Length - 1); return tyreWearIdx; }
        set { tyreWearIdx = Mathf.Clamp(value, 0, TyreRules.Names.Length - 1); PlayerPrefs.SetInt("tyreWear", tyreWearIdx); }
    }
    int TyreWearNow => InSeasonRound ? TyreRules.Auto : TyreWearPref;   // a career round: Auto
    const string TyreWearHelp = "How fast the tyres wear. Auto: a Soft lasts about 40% of the race, so every race needs a tyre plan (x2 in sprints, x1 in practice). Real: as a real race (a Soft lasts about 20 laps). Faster wear also makes tyre temperature matter: cold tyres after a stop and tyres overheated by sliding lose grip. Soft: fastest, warms quickest, wears quickest. Hard: slowest, lasts longest, needs a lap or two of heat. From the next race.";

    double tyreMult = 1;                                   // the wear multiplier of this session
    readonly string[] pitLifeTxt = new string[Weather.Sets], pitFlashTxt = new string[Weather.Sets];

    // the session's rules: from PlayerConfig (every new car), before the rivals are made
    void TyreRulesBegin()
    {
        int st = TyreWearNow;
        string cid = track != null && track.def != null ? track.def.id : "test";
        tyreMult = raceLaps > 0 ? TyreRules.Mult(st, raceLaps, cid) : st == TyreRules.Auto ? 1 : TyreRules.Mult(st, 10, cid);
        TyreRules.Strong = st != TyreRules.Real;
        pitChoice = -1;
        for (int s = 0; s < Weather.Sets; s++)
        {
            int l = Mathf.Max(1, Mathf.RoundToInt((float)(TyreRules.LifeLaps(s, tyreMult, cid) * Weather.DryLife(s))));
            pitLifeTxt[s] = l >= 40 ? "40+ laps" : "~" + l + (l == 1 ? " lap" : " laps");
            pitFlashTxt[s] = "Box for " + Weather.TyreName(s) + "  ·  " + pitLifeTxt[s];
        }
    }
    // a car's config for this session (the player's and each rival's), on the set it starts on
    void TyreRulesFor(CarConfig c, int set) { TyreRules.Apply(c, tyreMult, TyreRules.Strong); TyreRules.Fit(c, set); }
    // the player's car on a set: as above (Apply resets life and heat to the session's, so nothing piles up from stop to
    // stop), plus a wet-weather tyre's penalty on a dry track (Weather.DryRunning). Only at a change of set.
    void FitPlayerSet(CarConfig c, int set) { TyreRulesFor(c, set); Weather.DryRunning(c, set); }

    // ---------------------------------------------------------------- the pit choice
    int pitChoice = -1;                                    // the set picked on the chips, -1 = AutoSet
    // the automatic pick: the set for the weather first, then round the rest that suit it (it went round all five: a stop
    // from Hards in the dry fitted Intermediates)
    int AutoSet
    {
        get
        {
            int best = Weather.BestSet(); if (best != carSet) return best;
            for (int k = 1; k < Weather.Sets; k++) { int s = (carSet + k) % Weather.Sets; if (TyreSets.Suits(s, Weather.Index)) return s; }
            return (carSet + 1) % Weather.Sets;
        }
    }
    // the new wheels are on (GameManager.PitStop): the set's heat (TyreRules), and the next stop starts from the auto pick
    void PitFit() { FitPlayerSet(car.cfg, carSet); pitChoice = -1; }

    bool PitChipsShown => !menuOpen && !playground && raceLaps > 0 && form == null && !replaying
                          && ((boxArmed && pRun == null && pLap < raceLaps) || (pRun != null && pRun.phase == 0 && pRun.Clock < -1.5));

    // under the top buttons (and the FPS line), right-aligned with them
    void DrawPitChips()
    {
        if (!PitChipsShown) return;
        float cw = S(32), ch = S(27), cg = S(3), y = hudT + S(48);
        float x = btnRight - Weather.Sets * cw - (Weather.Sets - 1) * cg;
        int sel = NextSet, wet = Weather.Index;
        TF(new Rect(x, y - S(1), S(160), S(9)), "TYRES FOR THE STOP", UiKit.Semi, 5.4f, HudFaint, TextAnchor.UpperLeft);
        y += S(8);
        for (int s = 0; s < Weather.Sets; s++, x += cw + cg)
        {
            var r = new Rect(x, y, cw, ch);
            bool tap = HudButton(new Rect(r.x - S(1), r.y - S(2), r.width + S(2), r.height + S(4)), out float p);
            bool on = s == sel, suits = TyreSets.Suits(s, wet);
            Color col = SetCol[s];
            UiKit.Rect(UiKit.Scale(r, 1f - 0.05f * p), on ? new Color(col.r * 0.35f, col.g * 0.35f, col.b * 0.35f, 0.92f) : new Color(0.05f, 0.06f, 0.08f, 0.72f), 6f);
            UiKit.Outline(r, on ? col : new Color(1f, 1f, 1f, 0.12f), 6f, on ? 1.4f : 0.6f);
            var o = Fade(suits || on ? 1f : 0.45f);
            TyreIcon(new Rect(r.center.x - S(6.5f), r.y + S(3), S(13), S(13)), s);
            TF(new Rect(r.x, r.y + S(17), r.width, S(8)), pitLifeTxt[s], UiKit.Semi, 4.8f, on ? Color.white : HudGrey, TextAnchor.MiddleCenter);
            GUI.color = o;
            if (tap) { pitChoice = s; Flash(pitFlashTxt[s] ?? Weather.TyreName(s)); }
        }
    }

    // ---------------------------------------------------------------- the starting tyres (race start from the grid)
    bool startTyresOn, startTyresIntro, shotTyres;          // card up (race clock held) / StartSession's intro waits for it / shot mode tyrepick=1
    int startTyresSel, startTyresN = 3;                     // the set picked on the card / chips shown (3 slicks, 5 with I / W)
    readonly string[] startHint = new string[Weather.Sets], startFitTxt = new string[Weather.Sets];
    string startTyresSub;
    bool StartTyresHold => startTyresOn;                    // the race clock and every car held on the grid (GameManagerStart)

    // StartReset (any new start): the card comes up on the grid of a race
    void StartTyresReset()
    {
        startTyresIntro = false;
        startTyresOn = GridStart && !qualiSession && !replaying && car != null && (!shotFreeze || shotTyres);
        if (!startTyresOn) return;
        int w = Weather.Index;
        startTyresSel = Mathf.Clamp(tyreSet, 0, Weather.Sets - 1);
        startTyresN = w > 0 || startTyresSel >= Weather.Inter ? Weather.Sets : 3;
        startTyresSub = "Track: " + Weather.Names[Mathf.Clamp(w, 0, Weather.Names.Length - 1)] + (raceLaps > 0 ? "  ·  " + raceLaps + (raceLaps == 1 ? " lap" : " laps") : "") + "  ·  you can change in the pits";
        if (w == 0)
        {
            startHint[0] = "Fastest. Warms up quickest, wears out first.";
            startHint[1] = "Balanced: a little slower, lasts much longer.";
            startHint[2] = "Lasts longest. Slowest, needs a lap to warm up.";
        }
        else
        {
            string slick = w == 1 ? "Quick on the dry line, slides off it." : "Very slippery on a wet track.";
            startHint[0] = startHint[1] = startHint[2] = slick;
        }
        startHint[Weather.Inter] = w == 0 ? "Slow on a dry track, overheats, wears fast." : w == 1 ? "Best in the damp. Grips on and off the line." : "Good in light rain, slower than Wets here.";
        for (int s = 0; s < Weather.Sets; s++) startFitTxt[s] = Weather.TyreName(s) + " fitted at START";
        startHint[Weather.Full] = w == 0 ? "Very slow in the dry, overheats, wears fast." : w == 1 ? "Safe but slow in the damp, runs hot." : "Best in the rain: the most grip on water.";
    }

    // StartSession, after ResetCar: the pre-race intro waits for the card
    void StartTyresThenIntro() { if (startTyresOn) startTyresIntro = true; else PreRaceBegin(); }

    // a set on the car before the start: the setting too (the next race's default)
    void StartTyresFit(int set)
    {
        set = Mathf.Clamp(set, 0, Weather.Sets - 1);
        tyreSet = carSet = set; compIdx = (int)Weather.PhysicsCompound(set); PlayerPrefs.SetInt("tyreSet", set);
        FitPlayerSet(car.cfg, set);
        car.wearF = car.wearR = 0; car.tyreTF = car.tyreTR = car.cfg.tyreStart; car.tyreSF = car.tyreSR = double.NaN; // fresh, off the blankets (as PitStop)
        if (model != null) model.SetStripe(Weather.Stripe(set));
        pitChoice = -1;
    }

    void StartTyresGo()
    {
        startTyresOn = false;
        if (startTyresSel != carSet) StartTyresFit(startTyresSel);
        Flash("Starting on " + Weather.TyreName(carSet));
        if (startTyresIntro) { startTyresIntro = false; PreRaceBegin(); } // then the intro (PreRace.cs), the formation lap and the lights
    }

    // OnGUI, before the HUD: true = the card drew the screen (no HUD under it)
    bool StartTyresGui()
    {
        if (!startTyresOn) return false;
        if (car == null || ui == null || ui.Showing) { startTyresOn = false; return false; }
        HudInit(); HudLayout();
        UiKit.Rect(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.38f));
        int n = Mathf.Clamp(startTyresN, 3, Weather.Sets);
        float cw = S(n > 3 ? 78 : 100), chH = S(92), cg = S(6), pad = S(14);
        float w = n * cw + (n - 1) * cg + 2 * pad, h = S(188);
        var p = new Rect(Mathf.Round((Screen.width - w) * 0.5f), Mathf.Round((Screen.height - h) * 0.5f), w, h);
        UiKit.Shadow(p, 14f, 10f, 0.4f);
        Glass(p, 12f);
        TF(new Rect(p.x + pad, p.y + S(9), w - 2 * pad, S(10)), "RACE START", UiKit.Semi, 5.6f, HudFaint, TextAnchor.UpperLeft);
        TF(new Rect(p.x + pad, p.y + S(18), w - 2 * pad, S(16)), "STARTING TYRES", UiKit.Black, 11f, Color.white, TextAnchor.MiddleLeft);
        TF(new Rect(p.x + pad, p.y + S(34), w - 2 * pad, S(10)), startTyresSub, UiKit.Semi, 5.6f, HudGrey, TextAnchor.MiddleLeft);
        float x = p.x + pad, y = p.y + S(50);
        for (int s = 0; s < n; s++, x += cw + cg)
        {
            var r = new Rect(x, y, cw, chH);
            bool tap = HudButton(r, out float pr);
            bool on = s == startTyresSel, suits = TyreSets.Suits(s, Weather.Index);
            Color col = SetCol[s];
            UiKit.Rect(UiKit.Scale(r, 1f - 0.04f * pr), on ? new Color(col.r * 0.30f, col.g * 0.30f, col.b * 0.30f, 0.95f) : new Color(0.05f, 0.06f, 0.08f, 0.72f), 8f);
            UiKit.Outline(r, on ? col : new Color(1f, 1f, 1f, 0.12f), 8f, on ? 1.6f : 0.6f);
            var o = Fade(suits || on ? 1f : 0.5f);
            TyreIcon(new Rect(r.center.x - S(11), r.y + S(7), S(22), S(22)), s);
            TF(new Rect(r.x, r.y + S(32), r.width, S(11)), Weather.TyreName(s), UiKit.Bold, 7f, Color.white, TextAnchor.MiddleCenter);
            TF(new Rect(r.x, r.y + S(43), r.width, S(9)), pitLifeTxt[s], UiKit.Semi, 5.2f, on ? Color.white : HudGrey, TextAnchor.MiddleCenter);
            UiKit.Text(new Rect(r.x + S(5), r.y + S(54), r.width - S(10), S(34)), startHint[s], UiKit.Semi, 5.0f, on ? new Color(0.92f, 0.93f, 0.95f) : HudGrey, TextAnchor.UpperCenter, true);
            GUI.color = o;
            if (tap) startTyresSel = s;
        }
        float bw = S(96), bh = S(24);
        TF(new Rect(p.x + pad, p.yMax - S(12) - bh, w - 2 * pad - bw - S(8), bh), startFitTxt[startTyresSel], UiKit.Semi, 6f, HudGrey, TextAnchor.MiddleLeft);
        if (PillBtn(new Rect(p.xMax - pad - bw, p.yMax - S(12) - bh, bw, bh), "START", null, HudAccent, true, 8.5f)) StartTyresGo();
        return true;
    }

    // ---------------------------------------------------------------- the HUD's tyre cue (dash tyre quad)
    readonly HudHold hTyreWear = new HudHold();
    int tyreCue;                                           // 0 none, -1 cold, 1 hot (with some hysteresis)
    void DrawTyreCue(float dx, float dy)
    {
        if (double.IsNaN(car.tyreTF)) return;
        double wear = System.Math.Max(car.wearF, car.wearR);
        int pct = Mathf.Clamp(Mathf.RoundToInt((float)(wear * 100)), 0, 100);
        Color wc = wear > 0.72 ? HudRed : wear > 0.5 ? HudAmber : Color.white;
        TF(new Rect(dx + S(206), dy + S(11.5f), S(30), S(11)), hTyreWear.Get(HudNum.Pct(pct), 0.5f), UiKit.Bold, 6.2f, wc, TextAnchor.MiddleCenter);
        // grip lost to temperature, the worse axle (as FormulaCar: the carcass plus a share of the surface)
        var cd = CompoundTable.Get(car.cfg.compound); var c = car.cfg;
        double gF = car.tyreTF, gR = car.tyreTR;
        if (c.tyreSurface && !double.IsNaN(car.tyreSF)) { gF += c.tyreSurfGrip * (car.tyreSF - car.tyreTF); gR += c.tyreSurfGrip * (car.tyreSR - car.tyreTR); }
        double eF = (gF - cd.opt) / cd.window, eR = (gR - cd.opt) / cd.window;
        double lossF = c.tyreLoss * eF * eF, lossR = c.tyreLoss * eR * eR, loss = System.Math.Max(lossF, lossR), e = lossF >= lossR ? eF : eR;
        if (loss > 0.022) tyreCue = e < 0 ? -1 : 1; else if (loss < 0.012) tyreCue = 0;
        if (tyreCue != 0)
            TF(new Rect(dx + S(206), dy + S(37), S(30), S(10)), tyreCue < 0 ? "COLD" : "HOT", UiKit.Bold, 5.6f, tyreCue < 0 ? HudBlue : HudRed, TextAnchor.MiddleCenter);
    }
}
