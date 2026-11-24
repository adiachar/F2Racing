// TyrePit.cs — tyre wear, temperature and the pit compound choice (part of GameManager):
//  * Settings > GAME "Tyre wear" (PlayerPrefs "tyreWear"): Auto, Real (x1), x3, x6, x10. Auto: a Soft lasts about 40 %
//    of a race of 5+ laps (x2 in shorter ones, x1 in practice and qualifying); a career round always uses Auto. Any
//    setting but Real also turns on the stronger tyre temperature (FormulaSim.TyreRules: more grip lost away from the
//    compound's window, each compound runs at its own heat). Applied to the player's car and every rival's CarConfig
//    (game-side only: the physics defaults are untouched) at each new car; TyreRules.Fit on every change of set.
//  * The pit compound chips: with BOX armed (and in the pit lane until the crew starts) a row of S / M / H / I / W chips
//    under the top buttons, each with its life at this wear ("~4 laps"); a tap picks the set the crew fits (pitChoice,
//    -1 = the old automatic pick, AutoSet).
//  * The HUD's tyre cue: worn % above the compound ring in the dash's tyre quad, COLD / HOT under it while the tyre
//    temperature costs grip.
// No per-frame garbage: every string is made when the session starts.
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
            int l = Mathf.Max(1, Mathf.RoundToInt((float)TyreRules.LifeLaps(s, tyreMult, cid)));
            pitLifeTxt[s] = l >= 40 ? "40+ laps" : "~" + l + (l == 1 ? " lap" : " laps");
            pitFlashTxt[s] = "Box for " + Weather.TyreName(s) + "  ·  " + pitLifeTxt[s];
        }
    }
    // a car's config for this session (the player's and each rival's), on the set it starts on
    void TyreRulesFor(CarConfig c, int set) { TyreRules.Apply(c, tyreMult, TyreRules.Strong); TyreRules.Fit(c, set); }

    // ---------------------------------------------------------------- the pit choice
    int pitChoice = -1;                                    // the set picked on the chips, -1 = AutoSet
    // the automatic pick (as before): the set for the weather first, then round the rest
    int AutoSet => Weather.BestSet() != carSet ? Weather.BestSet() : (carSet + 1) % Weather.Sets;
    // the new wheels are on (GameManager.PitStop): the set's heat (TyreRules), and the next stop starts from the auto pick
    void PitFit() { TyreRules.Fit(car.cfg, carSet); pitChoice = -1; }

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
