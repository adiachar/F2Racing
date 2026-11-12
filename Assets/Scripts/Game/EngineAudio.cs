// EngineAudio.cs — the car's sound, synthesised sample by sample from the physics, with no recorded clips.
// It is what the driver steers the throttle by, so everything follows the car within a few milliseconds:
//  * engine: one exhaust pulse per cylinder firing (V6: 3 firings per crank turn, so 12,000 rpm = 600 Hz).
//    The six cylinders are not identical, which gives the rough "half-order" growl under the main note.
//    Throttle makes it louder and brighter; off-throttle it goes quiet and muffled, with overrun crackle.
//    The engine speed follows the driven wheels, so wheelspin is heard as the revs flaring.
//    Each car class has its own voice (see ClassParams below): F1 2026, Formula 2, Formula 3.
//  * turbo whistle that builds with boost, wastegate/bypass chatter on lift-off, a short ignition cut on
//    every gear change, rev-limiter stutter
//  * F1 only: the MGU-K electric motor whine, pitched by road speed, brighter when deploying and lower and
//    grittier when harvesting
//  * tyres, the driver's grip gauge: a scrub that starts at about half the peak slip angle and gets louder and
//    higher as the tyre nears its limit (lower for the fronts = understeer, higher for the rears = oversteer),
//    a squeal past the limit, a tonal squeal for locked (or ABS-held) fronts, a lower howl for locked rears,
//    and a harsher note for wheelspin that rises with the slip
//  * wind that grows with speed (a low roar, a hiss past the helmet, slow buffeting; quieter and more
//    turbulent in a slipstream), the plank scraping the road when the car bottoms out,
//    a rumble on the kerbs, crunching on gravel and a soft swish on grass
// It runs in the audio mixer callback (OnAudioFilterRead), so the delay is one small audio buffer.
// WebGL has no such callback; there it falls back to a streamed clip (slower to react).
// The audio thread never allocates: all state is fields, all tables are static readonly.
using UnityEngine;

public class EngineAudio : MonoBehaviour
{
    // written by the game every frame, read on the audio thread
    public volatile float rpm = 4000, throttle, slide, speed, redline = 12000;
    public volatile float wheelspin, lockup;   // 0..1 (lockup: rear wheels)
    public volatile float slipF, slipR;        // front / rear slip angle as a share of the angle of peak grip (1 = at the limit)
    public volatile float lockFront;           // 0..1 front wheels locked (about 0.3 = ABS holding them at the limit)
    public volatile float scrape;              // 0..1 floor plank touching the road
    public volatile float tow;                 // 0..1 how deep in another car's slipstream
    public volatile bool onAsphalt = true, onKerb, shifting;
    public volatile bool muted;
    public volatile int carClass;              // 0 F1 2026, 1 Formula 2, 2 Formula 3
    public volatile float ersKw;               // MGU-K power: + deploying, - harvesting (F1 only)
    public volatile int surface;               // 0 asphalt, 1 kerb, 2 grass, 3 gravel
    const float Volume = 0.55f;
    // volume settings, 0..1 (GameManager, Audio tab): everything, engine (with turbo and MGU-K), tyres, and
    // effects (wind, kerbs, gravel, grass)
    public volatile float masterVol = 1, engineVol = 1, tyreVol = 1, fxVol = 1;

    // ---- per-class engine voice ----
    // All three are V6s, so the firing note is always 3 pulses per crank revolution; what changes is
    // how even the cylinders are, where the exhaust/intake resonances sit, how bright it gets on
    // throttle, the turbo and how much the exhaust pops on overrun.
    //  F1 2026: 1.6 L V6 turbo hybrid. No MGU-H any more, so the turbo spins down on lift like a normal
    //    one: the boost whistle under acceleration and bypass-valve noise on lift-off come back, and the
    //    350 kW MGU-K whine is prominent under full deployment and different under braking/harvesting
    //    (f1chronicle.com/how-will-2026-f1-cars-sound-engine-noise-and-the-mgu-k-effect/).
    //    Smooth, high-pitched, fairly even firing; lean hybrid calibration = little overrun crackle.
    //  F2: Mecachrome 3.4 L V6 single turbo, ~620 hp (pitdebrief.com/?p=71363), ~8750 rpm. A big,
    //    low-revving production-derived turbo engine: deeper exhaust resonance, rougher cylinders,
    //    louder turbo and wastegate chatter, plenty of pops on lift.
    //  F3: the same 3.4 L V6 but naturally aspirated, ~380 hp, smoother power curve (pitdebrief.com/?p=71363),
    //    ~8500 rpm. No turbo; the open airbox gives a brighter, howling intake note (resonance high up).
    static readonly float[][] CylGainC =
    {
        new[] { 1f, 0.88f, 0.96f, 0.85f, 0.98f, 0.91f },   // F1: tight tolerances, even
        new[] { 1f, 0.72f, 0.93f, 0.68f, 0.97f, 0.80f },   // F2: rough, growly half-orders
        new[] { 1f, 0.82f, 0.95f, 0.78f, 0.99f, 0.86f },   // F3: in between
    };
    static readonly float[] JitterC      = { 0.05f, 0.11f, 0.06f };   // random pulse-to-pulse variation
    static readonly float[] Body1MulC    = { 1.00f, 0.75f, 1.00f };   // exhaust resonance / firing freq
    static readonly float[] Body1QC      = { 1.10f, 0.90f, 1.30f };
    static readonly float[] Body1WC      = { 0.90f, 1.10f, 0.70f };
    static readonly float[] Body2MulC    = { 2.00f, 1.50f, 3.00f };   // upper (intake) resonance / firing freq
    static readonly float[] Body2QC      = { 1.60f, 1.20f, 2.20f };
    static readonly float[] Body2WC      = { 0.60f, 0.55f, 0.95f };
    static readonly float[] ToneBaseC    = { 700f, 600f, 950f };       // tone low-pass: base + thr * span + rpm * k
    static readonly float[] ToneThrC     = { 4200f, 3400f, 5400f };
    static readonly float[] ToneRpmC     = { 0.12f, 0.16f, 0.30f };
    static readonly float[] EngGainC     = { 0.90f, 1.05f, 0.95f };
    static readonly float[] SpoolRpmC    = { 9000f, 5200f, 1f };       // revs where the turbo is fully spooled
    static readonly float[] WhistleLvlC  = { 0.012f, 0.018f, 0f };
    static readonly float[] WhistleBaseC = { 5200f, 3600f, 0f };
    static readonly float[] WhistleSpanC = { 5200f, 4800f, 0f };
    static readonly float[] ChatterLvlC  = { 0.015f, 0.06f, 0f };      // wastegate / bypass flutter on lift
    static readonly float[] CrackleThrC  = { 0.95f, 0.88f, 0.90f };    // higher = fewer pops (Noise() threshold)
    static readonly float[] CrackleRpmC  = { 0.58f, 0.55f, 0.60f };    // pops above this fraction of redline

    int rate = 48000;
    double firePh;          // firing phase 0..1
    int cyl;                // which cylinder fires next
    float curRpm = 4000, curThr, boost, gain, scrub, squeal, lockG, spinG, windG, kerbG, limiter, cut = 1;
    double whistlePh, kerbPh, limPh, chatPh, ersPh, swishPh;
    float pulseGain = 1, crackle;
    float chatG, ersG, ersF = 200, ersHm, gravG, grassG, gravEnv;
    float lockFG, scrapeG, hissG, rearBias, slipLvl, buffet, scrapeGrit;
    readonly Biquad lockFrF = new Biquad(), scrapeF = new Biquad(), scrapeLoF = new Biquad(), hissF = new Biquad();
    readonly Biquad body1 = new Biquad(), body2 = new Biquad(), tone = new Biquad(), scrubF = new Biquad(), squealF = new Biquad(), lockF = new Biquad(), spinF = new Biquad(), windF = new Biquad(), popF = new Biquad();
    readonly Biquad chatF = new Biquad(), gravF = new Biquad(), grassF = new Biquad(), rumbleF = new Biquad();
    uint seed = 22222;
    float Noise() { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return (seed & 0xFFFFFF) / 8388608f - 1f; }

    class Biquad
    {
        float b0, b1, b2, a1, a2, x1, x2, y1, y2;
        public void LowPass(float f, float q, float fs) { Set(f, q, fs, 0); }
        public void BandPass(float f, float q, float fs) { Set(f, q, fs, 1); }
        void Set(float f, float q, float fs, int kind)
        {
            float w = 2 * Mathf.PI * Mathf.Clamp(f, 20, fs * 0.45f) / fs, cw = Mathf.Cos(w), al = Mathf.Sin(w) / (2 * q), a0 = 1 + al;
            if (kind == 1) { b0 = al / a0; b1 = 0; b2 = -al / a0; }
            else { b0 = (1 - cw) / 2 / a0; b1 = (1 - cw) / a0; b2 = b0; }
            a1 = -2 * cw / a0; a2 = (1 - al) / a0;
        }
        public float Run(float x)
        {
            float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            if (float.IsNaN(y) || float.IsInfinity(y)) y = 0;
            x2 = x1; x1 = x; y2 = y1; y1 = y; return y;
        }
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    AudioSource src;
    void Start()
    {
        rate = AudioSettings.outputSampleRate; Setup();
        var clip = AudioClip.Create("engine", rate, 1, rate, true, d => Render(d, 1, false));
        src = gameObject.AddComponent<AudioSource>();
        src.clip = clip; src.loop = true; src.spatialBlend = 0; src.volume = 1f; src.playOnAwake = false;
        src.Play();
    }
#else
    bool ready;
    void Start()
    {
        // small mixer buffer = quick response (about 10 ms)
        var cfg = AudioSettings.GetConfiguration();
        if (cfg.dspBufferSize > 512) { cfg.dspBufferSize = 512; AudioSettings.Reset(cfg); }
        rate = AudioSettings.outputSampleRate; Setup(); ready = true;
    }
    // this component sits next to the AudioListener, so Unity hands it the final mix to add to
    void OnAudioFilterRead(float[] data, int channels) { if (ready) Render(data, channels, true); }
#endif

    void Setup()
    {
        if (rate <= 0) rate = 48000;
        scrubF.BandPass(850, 1.2f, rate); squealF.BandPass(1750, 6f, rate); lockF.BandPass(620, 4f, rate);
        spinF.BandPass(1250, 2.5f, rate); windF.LowPass(500, 0.7f, rate); popF.BandPass(900, 1.5f, rate);
        chatF.BandPass(2600, 1.5f, rate); gravF.BandPass(1900, 0.8f, rate); grassF.LowPass(400, 0.7f, rate);
        rumbleF.LowPass(90, 0.7f, rate);
        lockFrF.BandPass(980, 7f, rate); scrapeF.BandPass(2700, 1.1f, rate); scrapeLoF.BandPass(240, 1.5f, rate); hissF.BandPass(3200, 0.6f, rate);
    }

    void Render(float[] data, int channels, bool add)
    {
        int frames = data.Length / channels;
        float fs = rate;
        // smoothing per sample: revs 8 ms, levels 25 ms, tyre sounds 40 ms
        float kR = 1 - Mathf.Exp(-1f / (0.008f * fs)), kG = 1 - Mathf.Exp(-1f / (0.025f * fs)), kS = 1 - Mathf.Exp(-1f / (0.04f * fs)), kB = 1 - Mathf.Exp(-1f / (0.35f * fs));
        float crackDecay = 1 - 1f / (0.012f * fs), gravDecay = Mathf.Exp(-1f / (0.004f * fs));
        float tRpm = Mathf.Clamp(rpm, 1500, 16000), tThr = Mathf.Clamp01(throttle), sp = speed, red = redline;
        bool mute = muted;
        float vMaster = Mathf.Clamp01(masterVol) * Volume, vEng = Mathf.Clamp01(engineVol), vTyre = Mathf.Clamp01(tyreVol), vFx = Mathf.Clamp01(fxVol);
        float sl = slide;
        // per-class parameters, read once per buffer
        int cc = carClass; if (cc < 0 || cc > 2) cc = 0;
        float[] cylG = CylGainC[cc];
        float jitter = JitterC[cc], b1Mul = Body1MulC[cc], b1Q = Body1QC[cc], b1W = Body1WC[cc], b2Mul = Body2MulC[cc], b2Q = Body2QC[cc], b2W = Body2WC[cc];
        float toneBase = ToneBaseC[cc], toneThr = ToneThrC[cc], toneRpm = ToneRpmC[cc], engGain = EngGainC[cc], spool = 1f / SpoolRpmC[cc];
        float whLvl = WhistleLvlC[cc], whBase = WhistleBaseC[cc], whSpan = WhistleSpanC[cc], chLvl = ChatterLvlC[cc];
        float crThr = CrackleThrC[cc], crRpm = CrackleRpmC[cc] * red;
        bool turbo = whLvl > 0;
        // surfaces
        int surf = surface;
        bool asph = onAsphalt && surf <= 1;
        float tScrub = asph && sp > 8 ? Mathf.Clamp01((sl - 0.80f) / 0.18f) * 0.05f : 0;
        float tSqueal = asph && sp > 6 ? Mathf.Clamp((sl - 0.97f) * 1.6f, 0, 0.12f) : 0;
        // slip angle against the angle of peak grip: the scrub starts at 45 % of it and is full at the limit,
        // the squeal comes in just past the limit. (The terms above cover braking and traction in a straight line.)
        float sF = slipF, sR = slipR; if (float.IsNaN(sF)) sF = 0; if (float.IsNaN(sR)) sR = 0;
        float sMax = Mathf.Max(sF, sR), spK = asph ? Mathf.Clamp01((sp - 6f) / 10f) : 0;
        float rise = Mathf.Clamp01((sMax - 0.45f) / 0.55f); rise *= Mathf.Sqrt(rise);
        tScrub = Mathf.Max(tScrub, rise * 0.055f * spK);
        tSqueal = Mathf.Max(tSqueal, Mathf.Clamp((sMax - 1.05f) * 0.2f, 0, 0.12f) * spK);
        float tRear = sR / (sF + sR + 0.001f), tSlip = Mathf.Clamp(sMax, 0, 1.6f);
        float tLock = asph && sp > 4 ? Mathf.Clamp01(lockup) * 0.16f : 0;
        float tLockF = asph && sp > 4 ? Mathf.Clamp01(lockFront) * 0.15f : 0;
        float spinIn = Mathf.Clamp01(wheelspin);
        float tSpin = asph ? spinIn * 0.12f : 0;
        float towIn = Mathf.Clamp01(tow);
        float tWind = Mathf.Clamp01(sp / 95f);
        float tHiss = tWind * tWind * tWind * 0.03f * (1 - 0.3f * towIn);   // air tearing past the helmet: only really there above 200 km/h
        tWind = tWind * tWind * 0.11f * (1 - 0.35f * towIn);
        float buffDepth = 1.2f + 2.5f * towIn;                              // dirty air shakes the helmet more
        float kBuf = 1 - Mathf.Exp(-6.2831853f * 5f / fs);                  // buffeting: noise below about 5 Hz
        float scr = scrape; if (float.IsNaN(scr)) scr = 0;
        float tScrape = sp > 10 ? Mathf.Clamp01(scr) * 0.13f * Mathf.Clamp01(sp / 50f) : 0;
        float gritDecay = Mathf.Exp(-1f / (0.003f * fs)), gritP = (60f + sp * 6f) / fs;
        float tKerb = (onKerb || surf == 1) && sp > 5 ? 0.22f : 0;
        float tGrav = surf == 3 && sp > 1 ? Mathf.Clamp01(sp / 45f) : 0;
        float tGrass = surf == 2 && sp > 1 ? Mathf.Clamp01(sp / 40f) : 0;
        float gravP = (30f + sp * 20f) / fs;                 // gravel stones hitting the floor per sample
        float swishInc = (2.5f + sp * 0.12f) / fs;           // grass blades: slow swish cycle
        float tCut = shifting ? 0.15f : 1f;
        // ERS (F1 only): the MGU-K is geared to the engine but at racing speeds its pitch follows road speed
        float kw = cc == 0 ? ersKw : 0;
        float spFade = Mathf.Clamp01(sp / 6f);
        float tErsG = kw > 0 ? Mathf.Sqrt(Mathf.Clamp01(kw / 350f)) * 0.035f * spFade
                    : kw < 0 ? Mathf.Sqrt(Mathf.Clamp01(-kw / 350f)) * 0.026f * spFade : 0;
        float tErsHm = kw < 0 ? 1f : 0f;                     // 0 = deploy timbre, 1 = harvest timbre
        float tErsF = kw < 0 ? 120f + sp * 30f : 180f + sp * 52f;
        float tChat = turbo && tThr < 0.2f ? chLvl : 0;
        for (int i = 0; i < frames; i++)
        {
            curRpm += (tRpm - curRpm) * kR; curThr += (tThr - curThr) * kG; boost += (tThr * Mathf.Clamp01(curRpm * spool) - boost) * kB;
            scrub += (tScrub - scrub) * kS; squeal += (tSqueal - squeal) * kS; lockG += (tLock - lockG) * kS; spinG += (tSpin - spinG) * kS;
            windG += (tWind - windG) * kS; kerbG += (tKerb - kerbG) * kS; cut += (tCut - cut) * (shifting ? 0.02f : kG);
            gravG += (tGrav - gravG) * kS; grassG += (tGrass - grassG) * kS; chatG += (tChat - chatG) * kG;
            ersG += (tErsG - ersG) * kS; ersHm += (tErsHm - ersHm) * kS; ersF += (tErsF - ersF) * kG;
            lockFG += (tLockF - lockFG) * kS; scrapeG += (tScrape - scrapeG) * kG; hissG += (tHiss - hissG) * kS;
            rearBias += (tRear - rearBias) * kS; slipLvl += (tSlip - slipLvl) * kS;
            if ((i & 31) == 0)
            {
                float f0c = curRpm / 60f * 3f;
                // exhaust/intake resonances follow the firing note; throttle opens the tone up
                body1.BandPass(Mathf.Clamp(f0c * b1Mul, 120, 900), b1Q, fs);
                body2.BandPass(Mathf.Clamp(f0c * b2Mul, 300, 3200), b2Q, fs);
                tone.LowPass(toneBase + curThr * toneThr + curRpm * toneRpm, 0.8f, fs);
                windF.LowPass(350 + sp * 14f, 0.7f, fs);
                if (grassG > 1e-4f) grassF.LowPass(250 + sp * 8f, 0.7f, fs);
                // the tyre note climbs as the slip grows; fronts sound lower (understeer), rears higher (oversteer)
                if (scrub > 1e-4f) scrubF.BandPass(620 + 380 * Mathf.Min(1, slipLvl) + 260 * rearBias, 1.2f, fs);
                if (squeal > 1e-4f) squealF.BandPass(1500 + 450 * rearBias + 250 * Mathf.Clamp01(slipLvl - 1), 6f, fs);
                if (spinG > 1e-4f) spinF.BandPass(1100 + 500 * spinIn, 2.5f, fs);
                limiter = curRpm > red - 60 ? 1 : 0;
            }
            // ---- engine: one pulse per firing ----
            float f0 = curRpm / 60f * 3f;
            firePh += f0 / fs;
            if (firePh >= 1)
            {
                firePh -= 1; cyl = (cyl + 1) % 6;
                pulseGain = cylG[cyl] * (1 + jitter * Noise());
                // overrun: unburnt fuel popping in the exhaust when the driver lifts at high revs
                if (curThr < 0.05f && curRpm > crRpm && Noise() > crThr) crackle = 0.5f + 0.5f * Mathf.Abs(Noise());
            }
            float ph = (float)firePh;
            float pulse = (1 - ph); pulse = pulse * pulse * pulse * 2f - 0.5f;          // sharp bang that dies away
            float raw = pulse * pulseGain;
            float load = 0.22f + 0.78f * curThr;
            float e = tone.Run(raw * 0.55f + body1.Run(raw) * b1W + body2.Run(raw) * b2W * (0.4f + 0.6f * curThr));
            if (limiter > 0) { limPh += 28.0 / fs; if (limPh >= 1) limPh -= 1; if (limPh > 0.5) e *= 0.2f; }
            float engine = e * load * cut * engGain;
            crackle *= crackDecay;
            engine += popF.Run(Noise() * crackle) * 0.5f;
            if (turbo)
            {
                // turbo whistle
                whistlePh += (whBase + boost * whSpan) / fs; if (whistlePh >= 1) whistlePh -= 1;
                engine += Mathf.Sin((float)whistlePh * 6.2831853f) * boost * boost * whLvl;
                // wastegate / bypass flutter while the boost bleeds off after a lift
                if (chatG > 1e-5f)
                {
                    chatPh += (16f + boost * 14f) / fs; if (chatPh >= 1) chatPh -= 1;
                    float gate = chatPh < 0.35 ? 1f : 0.15f;
                    engine += chatF.Run(Noise()) * gate * chatG * boost * 4f;
                }
            }
            // ---- MGU-K whine (F1) ----
            if (ersG > 1e-5f)
            {
                ersPh += ersF / fs; if (ersPh >= 1) ersPh -= 1;
                float a = (float)ersPh * 6.2831853f;
                float w = Mathf.Sin(a) + Mathf.Sin(a + a) * (0.3f - 0.18f * ersHm);   // deploy: brighter 2nd harmonic
                w *= 1 + ersHm * 0.35f * Noise();                                      // harvest: grittier
                engine += w * ersG;
            }
            // ---- tyres, wind, kerbs, run-off ----
            float n = Noise();
            float tyres = scrubF.Run(n) * scrub + squealF.Run(n) * squeal + lockF.Run(n) * lockG + spinF.Run(n) * spinG;
            if (lockFG > 1e-5f) tyres += lockFrF.Run(n) * lockFG * 2.2f;                 // narrow band = a clear squeal
            float wn = Noise();
            buffet += (wn - buffet) * kBuf;
            float wind = (windF.Run(wn) * windG + hissF.Run(wn) * hissG) * Mathf.Max(0.3f, 1 + buffet * buffDepth * 8f);
            if (scrapeG > 1e-5f)
            {
                // plank on the road: a gritty, bright scrape in short random bursts over a dull grinding
                if (Noise() * 0.5f + 0.5f < gritP) scrapeGrit = 0.5f + 0.5f * Mathf.Abs(Noise());
                scrapeGrit *= gritDecay;
                tyres += (scrapeF.Run(n * (0.35f + scrapeGrit)) + scrapeLoF.Run(n) * 0.8f) * scrapeG;
            }
            kerbPh += Mathf.Clamp(sp * 1.6f, 12, 70) / fs; if (kerbPh >= 1) kerbPh -= 1;
            float kerb = (kerbPh < 0.3 ? 1f : -0.4f) * kerbG * 0.5f;
            float offT = 0;
            if (gravG > 1e-4f || grassG > 1e-4f)
            {
                float rumble = rumbleF.Run(n) * 2.5f;
                if (gravG > 1e-4f)
                {
                    // gravel: random stone impacts, each a short crunchy noise burst; more of them the faster
                    if (Noise() * 0.5f + 0.5f < gravP) gravEnv = 0.4f + 0.6f * Mathf.Abs(Noise());
                    gravEnv *= gravDecay;
                    offT += (gravF.Run(n * (gravEnv + 0.08f)) * 0.5f + rumble * 0.10f) * gravG;
                }
                if (grassG > 1e-4f)
                {
                    // grass: soft low swish that breathes slowly, over a dull rumble
                    swishPh += swishInc; if (swishPh >= 1) swishPh -= 1;
                    float sw = (float)swishPh; sw = sw < 0.5f ? sw * 2 : 2 - sw * 2;   // triangle 0..1
                    offT += (grassF.Run(n) * (0.6f + 0.4f * sw) * 0.35f + rumble * 0.07f) * grassG;
                }
            }
            float outS = mute ? 0 : (engine * vEng + tyres * vTyre + (wind + kerb + offT) * vFx) * vMaster;
            // soft limit so peaks never crackle on a phone speaker
            outS = outS / (1 + Mathf.Abs(outS));
            int o = i * channels;
            if (add) for (int c = 0; c < channels; c++) data[o + c] = Mathf.Clamp(data[o + c] + outS, -1f, 1f);
            else data[o] = outS;
        }
    }
}
