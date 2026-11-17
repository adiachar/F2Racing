// EngineAudio.cs — the car's sound, synthesised sample by sample from the physics, with no recorded clips.
// It is what the driver steers the throttle by, so everything follows the car within a few milliseconds:
//  * engine: one exhaust pulse per cylinder firing (V6: 3 firings per crank turn, so 12,000 rpm = 600 Hz).
//    The six cylinders are not identical, which gives the rough "half-order" growl under the main note.
//    Throttle makes it louder, brighter and harder (more exhaust/intake harmonics, a little saturation under
//    load, a pulsed induction roar from the airbox); off-throttle it goes quiet and muffled, with overrun crackle.
//    The engine speed follows the driven wheels, so wheelspin is heard as the revs flaring.
//    Each car class has its own voice (see ClassParams below): F1 2026, Formula 2, Formula 3.
//  * turbo whistle and compressor hiss riding on the physics' boost, a bypass-valve "pssh" when the driver lifts
//    off at high boost, wastegate chatter (F2), a short ignition cut and a bark on every upshift, a cut plus a
//    burst of exhaust pops on every downshift, a rev limiter that really bounces the revs (cut, fall, re-fire)
//  * F1 only: the MGU-K electric motor whine, pitched by road speed, brighter when deploying and lower and
//    grittier when harvesting
//  * tyres: the grip cue a real driver listens for, per axle, from the combined slip against the slip of peak grip
//    (1 = at the peak). Smooth curves with no on/off gates, 8 ms attack and 120 ms release, so it follows the car:
//    - from about 85 % of the peak: a soft scrub/hum that grows to full at the peak (fronts lower, rears higher)
//    - past the peak, louder and brighter the further over: FRONT = a low, juddering groan (understeer);
//      REAR = a high, sharp squeal whose pitch follows the slide and wobbles as the rear grabs and lets go (oversteer)
//    - wheelspin: a fast "zizz", its buzz rate and pitch tied to how much faster the tread runs than the road
//    - lock-up: a harsh, flat, clipped skid (fronts or rears); the ABS holding the fronts = a light chirp
//    - only on asphalt / kerbs (per axle); panned towards the outside (loaded) tyres; louder in the cockpit;
//      a strong slide ducks the engine up to 2.7 dB so it cuts through. Optional phone pulses as the rear goes over.
//    - surfaces by the share of wheels on them: kerb rumble (sausage kerbs thump), gravel crunch, grass swish
//  * wind: a low roar, a mid air-rush and a hiss that grow with speed, with slow gusts; in the cockpit a helmet /
//    airbox boom and more of everything, in the chase view much less; in a slipstream the air is dirty and
//    buffets the helmet (fast turbulence and low thumps)
//  * the plank scraping the road, rain on the car and the tyres' spray on a wet road
//  * the rival cars (RivalAudio.cs): the closest four, each with its own engine, panned, distance-filtered,
//    Doppler-shifted, with a whoosh as they go past
//  * TV camera: the player's car is heard from where the camera stands (distance, pan, Doppler, whoosh)
// It runs in the audio mixer callback (OnAudioFilterRead), so the delay is one small audio buffer.
// WebGL has no such callback; there it falls back to a streamed clip (slower to react, mono).
// The audio thread never allocates: all state is fields, all tables are static readonly.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public class EngineAudio : MonoBehaviour
{
    // written by the game every frame, read on the audio thread
    public volatile float rpm = 4000, throttle, slide, speed, redline = 12000;
    public volatile float wheelspin, lockup;   // 0..1 (lockup: rear wheels); used only if Feed() is not called
    public volatile float slipF, slipR;        // front / rear slip angle as a share of the angle of peak grip (1 = at the limit)
    public volatile float lockFront;           // 0..1 front wheels locked (about 0.3 = ABS holding them at the limit)
    public volatile float scrape;              // 0..1 floor plank touching the road
    public volatile float tow;                 // 0..1 how deep in another car's slipstream
    public volatile bool onAsphalt = true, onKerb, shifting;
    public volatile bool muted;
    public volatile int carClass;              // 0 F1 2026, 1 Formula 2, 2 Formula 3
    public volatile float ersKw;               // MGU-K power: + deploying, - harvesting (F1 only)
    public volatile int surface;               // 0 asphalt, 1 kerb, 2 grass, 3 gravel
    public volatile float wet;                 // 0 dry, 0.5 damp, 1 wet (raining)
    const float Volume = 0.55f;
    // volume settings, 0..1 (GameManager, Audio tab): everything, engine (with turbo, MGU-K and the rivals' engines),
    // tyres, and effects (wind, whooshes, kerbs, gravel, grass)
    public volatile float masterVol = 1, engineVol = 1, tyreVol = 1, fxVol = 1;

    // ---- written by Feed() (GameManager's audio block, once per frame) ----
    public volatile float loadF = 1, loadR = 1;      // axle load against the static load (downforce makes it 2-3x)
    public volatile float lonR;                      // rear slip ratio against the ratio of peak traction (+ spin, - locking)
    public volatile float spinSpeed;                 // m/s the rear tyres' tread runs faster than the road
    public volatile float lockRear;                  // 0..1
    public volatile float gripF, gripR;              // combined slip against the slip of peak grip, front / rear axle (1 = at the peak).
                                                     // Rears: lateral + traction/braking slip, the latter capped below the peak (spin has its own sound)
    public volatile float skidF, absF;               // front tyres: 0..1 sliding locked; 0..1 the ABS holding them at the limit
    public volatile float tyrePan;                   // -1..1, + = the right-hand tyres are the outer, loaded ones (left turn)
    public volatile float asphF = 1, asphR = 1;      // share of each axle's tyres on a surface that squeals (not grass, gravel)
    public volatile float boostIn = -1;              // turbo boost from the physics 0..1 (-1 = not given: own estimate)
    public volatile int upSeq, downSeq;              // up- / downshifts so far (the audio thread reacts to a change)
    public volatile float vib;                       // 0..1 surface shake
    public volatile float shGravel, shGrass, shKerb, shSausage; // share of the four wheels on each surface
    public volatile int view;                        // 0 chase / far, 1 cockpit / halo, 2 TV camera
    public volatile float pL = 1, pR = 1, pGain = 1, pDopp = 1, pTone = 20000, pWhoosh, pWhooshF = 800; // TV: player's car heard from outside
    public volatile int feedSeq;                     // counts Feed calls: no calls for a while (replay) = fade out
    public readonly RivalAudio rivalsAudio = new RivalAudio();

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
    internal static readonly float[][] CylGainC =
    {
        new[] { 1f, 0.88f, 0.96f, 0.85f, 0.98f, 0.91f },   // F1: tight tolerances, even
        new[] { 1f, 0.72f, 0.93f, 0.68f, 0.97f, 0.80f },   // F2: rough, growly half-orders
        new[] { 1f, 0.82f, 0.95f, 0.78f, 0.99f, 0.86f },   // F3: in between
    };
    internal static readonly float[] JitterC      = { 0.07f, 0.11f, 0.06f };   // random pulse-to-pulse level variation
    // 90-degree V6: the firing gaps alternate 150 / 90 degrees (average 120), so every second pulse comes early
    // or late. That is the "wobbly", half-order texture of an F1 V6. Share of the gap (0.25 = the full 150/90).
    internal static readonly float[] UnevenC      = { 0.25f, 0.10f, 0.12f };
    internal static readonly float[] TimeJitC     = { 0.012f, 0.02f, 0.012f }; // cycle-to-cycle timing wobble of the gaps
    // Tonal stack: sines at 1/2, 1, 3/2, 2, 3 and 4 times the firing frequency (the half orders come from the
    // uneven firing). Relative levels per class, then shaped by the exhaust formant (ResC) and the tone filter.
    internal static readonly float[] OrdC =
    {
        0.30f, 1.00f, 0.42f, 0.72f, 0.46f, 0.26f,   // F1: strong 1st and 2nd orders, 1.5 audible, raspy 3rd and 4th
        0.60f, 1.00f, 0.60f, 0.55f, 0.30f, 0.15f,   // F2: more half-order growl, less top
        0.40f, 1.00f, 0.50f, 0.50f, 0.35f, 0.20f,   // F3
    };
    internal static readonly float[] ResC         = { 1100f, 800f, 1500f };    // exhaust / airbox formant (Hz)
    internal static readonly float[] RaspC        = { 0.30f, 0.22f, 0.22f };   // upper-harmonic rasp when on the throttle
    static readonly int[] OrdK = { 1, 2, 3, 4, 6, 8 };                          // multiples of half the firing frequency
    internal static readonly float[] Body1MulC    = { 1.00f, 0.75f, 1.00f };   // exhaust resonance / firing freq
    internal static readonly float[] Body1QC      = { 1.10f, 0.90f, 1.30f };
    internal static readonly float[] Body1WC      = { 0.90f, 1.10f, 0.70f };
    internal static readonly float[] Body2MulC    = { 2.00f, 1.50f, 3.00f };   // upper (intake) resonance / firing freq
    internal static readonly float[] Body2QC      = { 1.60f, 1.20f, 2.20f };
    internal static readonly float[] Body2WC      = { 0.60f, 0.55f, 0.95f };
    internal static readonly float[] ToneBaseC    = { 520f, 600f, 950f };       // tone low-pass: base + thr * span + rpm * k
    internal static readonly float[] ToneThrC     = { 5600f, 3400f, 5400f };
    internal static readonly float[] ToneRpmC     = { 0.16f, 0.16f, 0.30f };
    internal static readonly float[] EngGainC     = { 0.80f, 1.05f, 0.95f };
    static readonly float[] SpoolRpmC    = { 9000f, 5200f, 1f };       // revs where the turbo is fully spooled (own estimate)
    static readonly float[] WhistleLvlC  = { 0.017f, 0.018f, 0f };
    static readonly float[] WhistleBaseC = { 3100f, 3600f, 0f };
    static readonly float[] WhistleSpanC = { 3800f, 4800f, 0f };
    static readonly float[] ChatterLvlC  = { 0.03f, 0.06f, 0f };      // wastegate / bypass flutter on lift
    internal static readonly float[] CrackleThrC  = { 0.90f, 0.88f, 0.90f };    // higher = fewer pops (Noise() threshold)
    internal static readonly float[] CrackleRpmC  = { 0.45f, 0.55f, 0.60f };    // pops above this fraction of redline
    static readonly float[] IntakeC      = { 1.5f, 0.7f, 1.4f };       // induction roar (F3: open airbox howl)
    static readonly float[] LimHzC       = { 16f, 10f, 12f };          // rev-limiter bounces per second
    internal static readonly int[] DownPopsC      = { 3, 4, 3 };                // exhaust pops after a downshift (plus up to 2 random)

    // tyre layer levels (at a full drive of 1)
    const float LvScrubF = 0.6f, LvScrubR = 0.5f, LvGroan = 1.6f, LvSqueal = 2.0f, LvSpin = 0.5f, LvLock = 0.5f, LvChirp = 0.12f;
    int rate = 48000;
    double firePh;          // firing phase 0..1
    int cyl;                // which cylinder fires next
    float curRpm = 4000, curThr, boost, scrub, squeal, lockG, spinG, windG, kerbG, cut = 1;
    double whistlePh, kerbPh, limPh, chatPh, ersPh, swishPh;
    float pulseGain = 1, crackle;
    float chatG, ersG, ersF = 200, ersHm, gravG, grassG, gravEnv;
    float lockFG, scrapeG, hissG, buffet, scrapeGrit, rainG, sprayG;
    // new state
    float sqF = 1500, sqBright, sqWob, sqWobT, spinF0 = 600, spinAm = 30; double spinPh;
    float scrubRG, groanG, grF = 600, grWob, grWobS, grWobT, sqWobD, duck = 1, tyrePanS; double sqLfo;
    float rushG, boomG, thumpG, gust, gustT, gustTimer, turb, turbT, turbTimer;
    float limOff, ign = 1, bark, bovG, hissT, prevThrIn;
    int cutN, popsLeft, popWait, seenUp, seenDown, lastFeed = -1, staleN;
    float live = 1, vView = 0, cpL = 1, cpR = 1, cpG = 1, cpD = 1, cpWh, dcX, dcY, sausEnv; double sausPh, thumpPh;
    readonly Biquad rainF = new Biquad(), sprayF = new Biquad();
    readonly Biquad lockFrF = new Biquad(), scrapeF = new Biquad(), scrapeLoF = new Biquad(), hissF = new Biquad();
    readonly Biquad body1 = new Biquad(), body2 = new Biquad(), tone = new Biquad(), scrubF = new Biquad(), squealF = new Biquad(), lockF = new Biquad(), spinF = new Biquad(), windF = new Biquad(), popF = new Biquad();
    readonly Biquad chatF = new Biquad(), gravF = new Biquad(), grassF = new Biquad(), rumbleF = new Biquad();
    readonly Biquad subF = new Biquad(), indF = new Biquad(), popLoF = new Biquad(), compF = new Biquad(), bovF = new Biquad();
    readonly Biquad squeal2F = new Biquad(), spinHiF = new Biquad(), lockHF = new Biquad(), kerbF = new Biquad();
    readonly Biquad scrubRF = new Biquad(), groanF = new Biquad();
    readonly Biquad rushF = new Biquad(), boomF = new Biquad(), thumpF = new Biquad(), pToneF = new Biquad(), pWhF = new Biquad();
    double halfPh, gearPh; float rateK = 1, sg = 1, wobR, wobT, chuff, gearG, raspLvl;
    readonly float[] wt = new float[6];
    readonly Biquad raspF = new Biquad(), chuffF = new Biquad();
    uint seed = 22222;
    float Noise() { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return (seed & 0xFFFFFF) / 8388608f - 1f; }
    static float Fin(float v, float def) => float.IsNaN(v) || float.IsInfinity(v) ? def : v;
    static float Ramp(float x, float a, float b) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }

    // Tonal weights for the stack, from half the firing frequency (Hz): level per order x exhaust formant x
    // low-pass at fc. Throttle brightens the upper orders, the onboard view (view 1) trims them. No allocation.
    internal static void Weigh(int cc, float fHalf, float fc, float fs, float thr, float view, float[] w)
    {
        float fres = ResC[cc], lim = fs * 0.42f;
        for (int i = 0; i < 6; i++)
        {
            float fk = OrdK[i] * fHalf;
            if (fk > lim) { w[i] = 0; continue; }
            float r = fk / fc, x = (fk - fres) / (0.6f * fres);
            float g = OrdC[cc * 6 + i] / (1f + r * r) * (1f + 1.1f / (1f + x * x));
            g *= i >= 3 ? (0.3f + 0.7f * thr) * (1f - 0.25f * view) : i == 2 ? 0.55f + 0.45f * thr : 0.7f + 0.3f * thr;
            w[i] = g;
        }
    }
    // Sum of the six sines of a (radians) using multiple-angle recursion: one sin and one cos per sample.
    internal static float Stack(float a, float[] w)
    {
        float s = Mathf.Sin(a), c = Mathf.Cos(a);
        float s2 = 2 * s * c, c2 = c * c - s * s, s3 = s2 * c + c2 * s, c3 = c2 * c - s2 * s;
        float s4 = 2 * s2 * c2, c4 = c2 * c2 - s2 * s2, s6 = 2 * s3 * c3, s8 = 2 * s4 * c4;
        return s * w[0] + s2 * w[1] + s3 * w[2] + s4 * w[3] + s6 * w[4] + s8 * w[5];
    }

    internal sealed class Biquad
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
            if (float.IsNaN(y) || float.IsInfinity(y)) { y = 0; x1 = x2 = y1 = y2 = 0; }
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
        scrubF.BandPass(380, 1.3f, rate); squealF.BandPass(1200, 10f, rate); squeal2F.BandPass(2400, 7f, rate); lockF.BandPass(800, 0.8f, rate);
        spinF.BandPass(700, 1.8f, rate); spinHiF.BandPass(2600, 2f, rate); lockHF.BandPass(2000, 0.7f, rate);
        scrubRF.BandPass(600, 1.2f, rate); groanF.BandPass(600, 5f, rate);
        windF.LowPass(500, 0.7f, rate); popF.BandPass(900, 1.5f, rate); popLoF.BandPass(240, 1.2f, rate);
        chatF.BandPass(2600, 1.5f, rate); gravF.BandPass(1900, 0.8f, rate); grassF.LowPass(400, 0.7f, rate);
        rumbleF.LowPass(90, 0.7f, rate); kerbF.LowPass(220, 0.8f, rate);
        lockFrF.BandPass(980, 7f, rate); scrapeF.BandPass(2700, 1.1f, rate); scrapeLoF.BandPass(240, 1.5f, rate); hissF.BandPass(3200, 0.6f, rate);
        rainF.BandPass(5200, 0.5f, rate); sprayF.BandPass(1800, 0.8f, rate);
        compF.BandPass(6500, 2f, rate); bovF.BandPass(3800, 0.9f, rate); raspF.BandPass(2700, 0.8f, rate); chuffF.BandPass(1000, 0.7f, rate);
        rushF.BandPass(900, 0.7f, rate); boomF.BandPass(220, 1.5f, rate); thumpF.LowPass(90, 0.7f, rate);
        pToneF.LowPass(18000, 0.7f, rate); pWhF.BandPass(800, 0.9f, rate);
    }

    // ---------------------------------------------------------------- game thread
    int lastGear = 1; Vector3 lastCam, camVel; bool camInit;

    // Called once per frame from GameManager's audio block, after the fields above are set: reads the extra
    // physics the tyre, turbo and shift sounds need, the camera (view, position, velocity) and the rival cars.
    // viewMode: 0 chase / far, 1 cockpit / halo, 2 TV camera.
    public void Feed(FormulaCar car, Transform cam, int viewMode, List<AiDriver> rivals, bool paused)
    {
        if (car == null || cam == null) return;
        var c = car.cfg;
        float nF = Mathf.Max(1f, (float)(c.mass * 9.81 * c.frontWeight)), nR = Mathf.Max(1f, (float)(c.mass * 9.81 * (1 - c.frontWeight)));
        loadF = Mathf.Clamp(Fin((float)car.fzF / nF, 1f), 0.1f, 6f); loadR = Mathf.Clamp(Fin((float)car.fzR / nR, 1f), 0.1f, 6f);
        float lr = Mathf.Clamp(Fin((float)(car.slipRatioR / System.Math.Max(0.02, c.peakRatio)), 0f), -20f, 20f);
        lonR = lr;
        spinSpeed = double.IsNaN(car.wR) ? 0f : Mathf.Clamp(Fin((float)(car.wR * c.wheelRadius - car.vx), 0f), 0f, 60f);
        lockRear = car.lockR ? 1f : car.brake > 0.05 ? Mathf.Clamp01((-lr - 1.6f) / 2.5f) : 0f;
        boostIn = double.IsNaN(car.boost) ? -1f : Mathf.Clamp01((float)car.boost);
        if (car.gear != lastGear) { if (car.gear > lastGear) upSeq++; else downSeq++; lastGear = car.gear; }
        vib = Mathf.Clamp01(Fin((float)car.vib, 0f));
        int gv = 0, gs = 0, kb = 0, sk = 0, offF = 0, offR = 0;
        for (int w = 0; w < 4; w++)
        {
            var t = car.wType[w];
            if (t == SurfaceType.Gravel) gv++;
            else if (t == SurfaceType.Grass || t == SurfaceType.Astroturf) gs++;
            else if (t == SurfaceType.Kerb) kb++;
            else if (t == SurfaceType.SausageKerb) sk++;
            if (t == SurfaceType.Gravel || t == SurfaceType.Grass || t == SurfaceType.Astroturf) { if (w < 2) offF++; else offR++; }
        }
        asphF = 1 - 0.5f * offF; asphR = 1 - 0.5f * offR;
        if (gv + gs == 0 && surface >= 2) { if (surface == 3) gv = 2; else gs = 2; asphF = asphR = 0; } // playground pad: no per-wheel types
        FeedTyres(car, nF, nR, lr, paused);
        shGravel = gv * 0.25f; shGrass = gs * 0.25f; shKerb = kb * 0.25f; shSausage = sk * 0.25f;
        if (kb + sk == 0 && onKerb && surface <= 1) shKerb = 0.5f;
        view = viewMode;
        // camera: horizontal axes and velocity (for panning and Doppler)
        Vector3 cp = cam.position; float dt = Time.deltaTime;
        if (!camInit) { lastCam = cp; camVel = Vector3.zero; camInit = true; }
        if (dt > 1e-4f)
        {
            Vector3 raw = (cp - lastCam) / dt;
            if (raw.sqrMagnitude > 150f * 150f || float.IsNaN(raw.x)) raw = Vector3.zero; // a jump (reset, camera cut)
            camVel = Vector3.Lerp(camVel, raw, Mathf.Min(1f, dt * 12f));
        }
        lastCam = cp;
        Vector3 right = cam.right; right.y = 0; Vector3 fwd = cam.forward; fwd.y = 0;
        if (right.sqrMagnitude < 1e-4f || fwd.sqrMagnitude < 1e-4f) { right = Vector3.right; fwd = Vector3.forward; }
        right.Normalize(); fwd.Normalize();
        if (viewMode == 2)
        {
            RivalAudio.Spatial(car, cp, right, fwd, camVel, 10f, out var s);
            pGain = Mathf.Max(0.04f, s.gain); pL = s.l; pR = s.r; pDopp = s.dopp; pTone = Mathf.Min(18000f, s.tone * 1.6f);
            pWhoosh = s.whoosh; pWhooshF = s.whooshF;
        }
        else { pGain = 1; pL = 1; pR = 1; pDopp = 1; pTone = 20000; pWhoosh = 0; }
        rivalsAudio.Update(rivals, cp, right, fwd, camVel, paused);
        feedSeq++;
    }

    // Grip cues: each axle's combined slip against the slip where its tyres grip best (the physics' magic formula,
    // whose peak slip angle grows with load), the front's usage turned back into slip so braking counts too,
    // which side is loaded, the front skid / ABS, and a light phone pulse as the rear goes over the peak.
    double pkC = double.NaN, pkE = double.NaN, pkBa = 1.7;
    readonly float[] invUse = new float[65];   // usage of the peak force (i / 64) -> slip share below the peak
    bool hapArmed = true; float hapWait;

    void TyreCurve(CarConfig c)
    {
        if (c.tireC == pkC && c.tireE == pkE) return;
        double C = c.tireC, E = c.tireE;
        double Mf(double x) => System.Math.Sin(C * System.Math.Atan(x - E * (x - System.Math.Atan(x))));
        double best = 0; pkBa = 1.7;
        for (double x = 0.05; x < 6; x += 0.002) { double f = Mf(x); if (f > best) { best = f; pkBa = x; } }
        int j = 0;
        for (int i = 0; i <= 64; i++)
        {
            double u = i / 64.0 * best;
            while (j < 1000 && Mf(j / 1000.0 * pkBa) < u) j++;
            invUse[i] = j / 1000f;
        }
        pkC = C; pkE = E;
    }

    void FeedTyres(FormulaCar car, float nF, float nR, float lr, bool paused)
    {
        var c = car.cfg;
        TyreCurve(c);
        double B = System.Math.Max(1, c.tireB), g = c.slipGrowth;
        double aPkF = pkBa / B * System.Math.Pow(Mathf.Clamp((float)car.fzF / nF, 0.2f, 6f), g);
        double aPkR = pkBa / B * System.Math.Pow(Mathf.Clamp((float)car.fzR / nR, 0.2f, 6f), g);
        bool moving = car.vx > 4;
        // front: the slip angle, or the force usage (braking + cornering) read back as slip; over 1 = the demand is
        // past the friction circle (or the tyres are locking, which has its own skid)
        float sF = Fin((float)(System.Math.Abs(car.alphaF) / aPkF), 0f), uF = Fin((float)car.useF, 0f), xu;
        if (uF >= 1) xu = Mathf.Min(2f, 1 + (uF - 1) * 2);
        else { float p = Mathf.Max(0, uF) * 64; int i = Mathf.Min(63, (int)p); xu = Mathf.Lerp(invUse[i], invUse[i + 1], p - i); }
        gripF = moving ? Mathf.Clamp(Mathf.Max(sF, xu), 0, 3) : 0;
        // rear: as the physics combines them (tan of the slip angle, slip ratio against the peak ratio)
        float sy = Fin((float)System.Math.Abs(System.Math.Tan(Mathf.Clamp((float)car.alphaR, -1.4f, 1.4f)) / System.Math.Tan(System.Math.Min(1.4, aPkR))), 0f);
        float sx = Mathf.Min(Mathf.Abs(lr), 0.92f);
        gripR = moving ? Mathf.Clamp(Mathf.Sqrt(sy * sy + sx * sx), 0, 3) : 0;
        skidF = car.brake > 0.05 && car.lockF ? 1 : 0;
        absF = car.absActive && car.brake > 0.2 && skidF < 0.5f ? 1 : 0;
        tyrePan = Mathf.Clamp(Fin((float)car.ayF, 0) / 20f, -1, 1);
        // phone: a light tap as the rear passes its peak, then soft pulses while it stays over (Vibration setting)
        float xr = gripR;
        if (paused || car.vx < 12 || asphR < 0.5f) { hapArmed = true; hapWait = 0; return; }
        hapWait -= Time.deltaTime;
        if (xr < 0.93f) hapArmed = true;
        if (hapArmed && xr > 1.0f) { hapArmed = false; hapWait = 0.18f; Haptics.Pulse(14, 70); }
        else if (!hapArmed && xr > 1.12f && hapWait <= 0)
        {
            float ex = Mathf.Clamp01((xr - 1.1f) / 0.6f);
            hapWait = 0.2f - 0.07f * ex; Haptics.Pulse(10 + (int)(ex * 12), 55 + (int)(ex * 80));
        }
    }

    // ---------------------------------------------------------------- audio thread
    const int Chunk = 1024;
    readonly float[] bufL = new float[Chunk], bufR = new float[Chunk];

    void Render(float[] data, int channels, bool add)
    {
        if (channels <= 0 || data == null) return;
        int frames = data.Length / channels;
        for (int s = 0; s < frames; s += Chunk) RenderChunk(data, channels, add, s, Mathf.Min(Chunk, frames - s));
    }

    void RenderChunk(float[] data, int channels, bool add, int start, int frames)
    {
        float fs = rate;
        // smoothing per sample: revs 8 ms, levels 25 ms, tyre sounds 8 ms (release 120 ms), boost 350 ms
        float kR = 1 - Mathf.Exp(-1f / (0.008f * fs)), kG = 1 - Mathf.Exp(-1f / (0.025f * fs)), kS = 1 - Mathf.Exp(-1f / (0.04f * fs)), kB = 1 - Mathf.Exp(-1f / (0.35f * fs));
        float kTa = 1 - Mathf.Exp(-1f / (0.008f * fs)), kTr = 1 - Mathf.Exp(-1f / (0.12f * fs)), kDr = 1 - Mathf.Exp(-1f / (0.25f * fs));
        float chuffDecay = Mathf.Exp(-1f / (0.12f * fs)), kRfall = 1 - Mathf.Exp(-1f / (0.045f * fs)), kWob = 1 - Mathf.Exp(-1f / (0.03f * fs)), kSg = 1 - Mathf.Exp(-1f / (0.004f * fs));
        float crackDecay = 1 - 1f / (0.012f * fs), gravDecay = Mathf.Exp(-1f / (0.004f * fs)), bovDecay = Mathf.Exp(-1f / (0.18f * fs)), barkDecay = Mathf.Exp(-1f / (0.05f * fs));
        float chunkSec = frames / fs;
        float tRpm = Mathf.Clamp(Fin(rpm, 4000), 1500, 16000), tThr = Mathf.Clamp01(Fin(throttle, 0)), sp = Mathf.Max(0, Fin(speed, 0)), red = Mathf.Max(3000, Fin(redline, 12000));
        bool mute = muted;
        float vMaster = Mathf.Clamp01(masterVol) * Volume, vEng = Mathf.Clamp01(engineVol), vTyre = Mathf.Clamp01(tyreVol), vFx = Mathf.Clamp01(fxVol);
        // no Feed for a while (replay, a long stall): fade everything out
        int fseq = feedSeq; bool fed = fseq != 0;
        if (fseq != lastFeed) { lastFeed = fseq; staleN = 0; } else staleN += frames;
        float tLive = !fed || staleN < (int)(0.35f * fs) ? 1f : 0f;
        // per-class parameters, read once per buffer
        int cc = carClass; if (cc < 0 || cc > 2) cc = 0;
        float uneven = UnevenC[cc], timeJit = TimeJitC[cc], raspC = RaspC[cc];
        float[] cylG = CylGainC[cc];
        float jitter = JitterC[cc], b1Mul = Body1MulC[cc], b1Q = Body1QC[cc], b1W = Body1WC[cc], b2Mul = Body2MulC[cc], b2Q = Body2QC[cc], b2W = Body2WC[cc];
        float toneBase = ToneBaseC[cc], toneThr = ToneThrC[cc], toneRpm = ToneRpmC[cc], engGain = EngGainC[cc], spool = 1f / SpoolRpmC[cc];
        float whLvl = WhistleLvlC[cc], whBase = WhistleBaseC[cc], whSpan = WhistleSpanC[cc], chLvl = ChatterLvlC[cc];
        float crThr = CrackleThrC[cc], crRpm = CrackleRpmC[cc] * red, intake = IntakeC[cc];
        bool turbo = whLvl > 0;
        float bIn = boostIn;
        // view: 0 chase (exhaust towards us, brighter, less wind), 1 onboard (meatier, airbox and helmet), 2 TV
        int vw = view; float tView = vw == 1 ? 1f : 0f;
        bool tv = vw == 2;
        float tpL = Fin(pL, 1), tpR = Fin(pR, 1), tpG = Mathf.Clamp(Fin(pGain, 1), 0, 1.5f), tpD = Mathf.Clamp(Fin(pDopp, 1), 0.6f, 1.6f), tpW = tv ? Mathf.Clamp01(Fin(pWhoosh, 0)) : 0;
        if (tv) { pToneF.LowPass(Mathf.Clamp(Fin(pTone, 18000), 300, 18000), 0.7f, fs); pWhF.BandPass(Mathf.Clamp(Fin(pWhooshF, 800), 200, 5000), 0.9f, fs); }
        if (!tv) { tpL = 1; tpR = 1; tpG = 1; tpD = 1; }

        // ---- shifts ----
        int us = upSeq, ds = downSeq;
        if (us != seenUp) { seenUp = us; cutN = (int)((cc == 0 ? 0.032f : 0.045f) * fs); bark = 0; }
        if (ds != seenDown)
        {
            seenDown = ds; cutN = (int)(0.025f * fs);
            if (tThr < 0.3f) { popsLeft = DownPopsC[cc] + (int)(Mathf.Abs(Noise()) * 2.99f); popWait = (int)(0.03f * fs); }
        }
        // bypass valve: a fast lift at high boost
        if (turbo && prevThrIn > 0.55f && tThr < 0.15f && boost > 0.4f) { bovG = Mathf.Max(bovG, boost); chuff = Mathf.Max(chuff, boost); }
        prevThrIn = tThr;
        // rev limiter: the target revs sit at the limit on throttle
        bool atLim = tRpm >= red - 50 && tThr > 0.3f;
        float limHz = LimHzC[cc], limDrop = red * 0.025f;

        // ---- tyres ----
        float gGrav = fed ? shGravel : surface == 3 ? 1 : 0, gGrass = fed ? shGrass : surface == 2 ? 1 : 0;
        float gKerb = fed ? shKerb : (onKerb || surface == 1) ? 1 : 0, gSaus = fed ? shSausage : 0;
        float asphS = Mathf.Clamp01(1 - gGrav - gGrass);
        if (!onAsphalt && !fed) asphS = 0;
        float spK = asphS * Mathf.Clamp01((sp - 5f) / 10f);
        float sF = Mathf.Clamp(Fin(slipF, 0), 0, 3), sR = Mathf.Clamp(Fin(slipR, 0), 0, 3);
        float lr = fed ? Mathf.Clamp(Fin(lonR, 0), -6, 10) : 0;
        // locked tyres: the flat skid below takes over from that axle's scrub and squeal
        float lkR = Mathf.Clamp01(Fin(fed ? lockRear : lockup, 0)), lkF = fed ? Mathf.Clamp01(Fin(skidF, 0)) : lockFront > 0.6f ? 1 : 0;
        float lF = Mathf.Clamp(Fin(loadF, 1), 0.2f, 4), lR = Mathf.Clamp(Fin(loadR, 1), 0.2f, 4);
        // combined slip against the peak, per axle (Feed); without Feed the slip angles alone
        float xF = fed ? Mathf.Clamp(Fin(gripF, 0), 0, 3) : sF, xR = fed ? Mathf.Clamp(Fin(gripR, 0), 0, 3) : sR;
        float spdK = Mathf.Clamp01((sp - 5f) / 10f);
        float kAF = fed ? Mathf.Clamp01(Fin(asphF, 1)) : asphS, kAR = fed ? Mathf.Clamp01(Fin(asphR, 1)) : asphS;
        float kLF = kAF * (1 - 0.75f * lkF), kLR = kAR * (1 - 0.75f * lkR);
        float ldF = Mathf.Min(1.5f, 0.6f + 0.4f * Mathf.Sqrt(lF)), ldR = Mathf.Min(1.5f, 0.6f + 0.4f * Mathf.Sqrt(lR));
        float wetIn = Mathf.Clamp01(Fin(wet, 0));
        float dryS = 1 - 0.3f * wetIn, dryQ = 1 - 0.65f * wetIn;     // a wet tyre hisses rather than squeals
        // No on/off gates: every layer is a smooth curve of the slip, so a tyre hovering at a level just holds a level.
        // scrub/hum: from about 85 % of the peak, full at the peak, and on growing past it
        float scF = Ramp(xF, 0.84f, 1.0f) * (0.5f + 0.5f * Ramp(xF, 1.0f, 1.45f)) * ldF * kLF * spdK;
        float scR = Ramp(xR, 0.84f, 1.0f) * (0.5f + 0.5f * Ramp(xR, 1.0f, 1.45f)) * ldR * kLR * spdK;
        // over the peak: fronts groan (understeer), rears squeal (oversteer); louder and brighter the further over
        float qF = Ramp(xF, 0.95f, 1.1f) * (0.45f + 0.55f * Ramp(xF, 1.05f, 1.6f)) * ldF * kLF * spdK * dryQ;
        float qR = Ramp(xR, 0.95f, 1.1f) * (0.45f + 0.55f * Ramp(xR, 1.05f, 1.6f)) * ldR * kLR * spdK * dryQ;
        float tScrub = scF * LvScrubF * dryS, tScrubR = scR * LvScrubR * dryS, tGroan = qF * LvGroan, tSqueal = qR * LvSqueal;
        float exF = Mathf.Clamp01((xF - 0.95f) / 0.8f), exR = Mathf.Clamp01((xR - 0.95f) / 0.8f);
        float tGrF = 520f + 200f * exF + 50f * (lF - 1), tSqF = 1150f + 450f * exR + 70f * (lR - 1);   // the pitch follows the slide
        float tBright = Ramp(xR, 1.0f, 1.6f), tWobD = 0.012f + 0.03f * Ramp(xR, 1.0f, 1.5f);
        // rear stick-slip: the slide grabs and lets go -> a random pitch target every 50-130 ms on a 5.5-8 Hz wobble
        sqWobT -= chunkSec; if (sqWobT <= 0) { sqWobT = 0.05f + 0.08f * Mathf.Abs(Noise()); sqWob = Noise(); }
        float wobHz = 5.5f + 2.5f * exR;
        // front judder: the scrubbing tyre shudders a few times a second
        grWobT -= chunkSec; if (grWobT <= 0) { grWobT = 0.12f + 0.15f * Mathf.Abs(Noise()); grWob = Noise(); }
        // wheelspin: a fast zizz, rate and pitch from how much faster the tread runs than the road
        float spinIn = fed ? 0 : Mathf.Clamp01(Fin(wheelspin, 0));
        float ss = fed ? Mathf.Clamp(Fin(spinSpeed, 0), 0, 30) : spinIn * 10;
        float spinD = fed ? (tThr > 0.02f ? Ramp(lr, 1.0f, 3.0f) * (0.6f + 0.4f * Ramp(ss, 1.5f, 10f)) : 0) : spinIn;
        float tSpin = spinD * LvSpin * kAR;
        float tSpinF = 650f + 45f * ss, tSpinAm = Mathf.Clamp(28f + 14f * ss, 28f, 260f);
        // lock-up: a flat skid (fronts or rears); the ABS at the limit: a light chirp
        float lockD = sp > 4 ? Mathf.Max(lkR * kAR, lkF * kAF) : 0;
        float tLock = lockD * LvLock;
        float tLockF = sp > 4 ? (fed ? Mathf.Clamp01(Fin(absF, 0)) : lockFront > 0.1f && lockFront < 0.6f ? 1 : 0) * LvChirp * kAF : 0;
        // mix: the outside (loaded) tyres louder on their side; cockpit a little louder; a strong slide ducks the engine
        float tPan = vw == 2 ? 0 : Mathf.Clamp(Fin(tyrePan, 0), -1, 1) * (vw == 1 ? 0.35f : 0.25f);
        float tyreView = vw == 1 ? 1.2f : 1f;
        float tDuck = 1 - 0.27f * Ramp(Mathf.Max(Mathf.Max(qR, qF * 0.8f), Mathf.Max(spinD * 0.7f, lockD * 0.8f)), 0.25f, 0.9f);
        float tRain = wetIn > 0.75f ? 0.014f + 0.012f * Mathf.Clamp01(sp / 80f) : 0;      // drops on the bodywork and helmet
        float tSpray = wetIn * Mathf.Clamp01(sp / 60f) * 0.06f * asphS;                   // water off the tyres

        // ---- wind ----
        float towIn = Mathf.Clamp01(Fin(tow, 0));
        float vn = Mathf.Clamp01(sp / 95f);
        float windView = vw == 1 ? 2.6f : vw == 0 ? 1.0f : 0f, hissView = vw == 1 ? 2.2f : vw == 0 ? 0.7f : 0f;
        float tWind = vn * vn * 0.11f * (1 - 0.35f * towIn) * windView;
        float tHiss = vn * vn * vn * 0.03f * (1 - 0.3f * towIn) * hissView;   // air tearing past the helmet: only really there above 200 km/h
        float tRush = Mathf.Pow(vn, 2.2f) * 0.045f * windView;
        float tBoom = vw == 1 ? vn * vn * 0.07f : 0;                          // helmet / airbox resonance (onboard)
        float tThump = towIn * vn * vn * 0.10f * (vw == 1 ? 1f : vw == 0 ? 0.35f : 0f); // dirty air buffeting the helmet
        float buffDepth = 1.2f + 2.5f * towIn;
        float kBuf = 1 - Mathf.Exp(-6.2831853f * 5f / fs);                  // buffeting: noise below about 5 Hz
        gustTimer -= chunkSec; if (gustTimer <= 0) { gustTimer = 0.35f + 0.9f * Mathf.Abs(Noise()); gustT = Noise(); }
        turbTimer -= chunkSec; if (turbTimer <= 0) { turbTimer = 0.05f + 0.07f * Mathf.Abs(Noise()); turbT = Noise(); }
        float kGust = 1 - Mathf.Exp(-1f / (0.4f * fs)), kTurb = 1 - Mathf.Exp(-1f / (0.03f * fs));

        // ---- plank, kerbs, run-off ----
        float scr = Mathf.Clamp01(Fin(scrape, 0));
        float tScrape = sp > 10 ? scr * 0.13f * Mathf.Clamp01(sp / 50f) : 0;
        float gritDecay = Mathf.Exp(-1f / (0.003f * fs)), gritP = (60f + sp * 6f) / fs;
        float vb = Mathf.Clamp01(Fin(vib, 0));
        float tKerb = sp > 5 ? Mathf.Clamp01(gKerb) * 0.22f * (0.6f + 0.4f * vb) : 0;
        float sausP = sp > 5 ? Mathf.Clamp(sp * 0.8f, 4f, 25f) / fs : 0, tSaus = Mathf.Clamp01(gSaus) * 0.3f, sausDecay = Mathf.Exp(-1f / (0.035f * fs));
        float slideAmt = Mathf.Clamp01(Mathf.Max(xF, xR) - 0.5f);
        float tGrav = sp > 1 ? Mathf.Clamp01(gGrav * 2f) * Mathf.Clamp01(sp / 45f) : 0;
        float tGrass = sp > 1 ? Mathf.Clamp01(gGrass * 2f) * Mathf.Clamp01(sp / 40f) : 0;
        float gravP = (30f + sp * 20f) / fs * (1 + 1.5f * slideAmt);  // gravel stones hitting the floor per sample
        float swishInc = (2.5f + sp * 0.12f) / fs;           // grass blades: slow swish cycle

        // ---- MGU-K (F1 only): at racing speeds its pitch follows road speed ----
        float kw = cc == 0 ? Fin(ersKw, 0) : 0;
        float spFade = Mathf.Clamp01(sp / 6f), ersView = vw == 1 ? 1.3f : 1f;
        float tErsG = kw > 0 ? Mathf.Sqrt(Mathf.Clamp01(kw / 350f)) * 0.04f * spFade * ersView
                    : kw < 0 ? Mathf.Sqrt(Mathf.Clamp01(-kw / 350f)) * 0.026f * spFade * ersView : 0;
        float tErsHm = kw < 0 ? 1f : 0f;                     // 0 = deploy timbre, 1 = harvest timbre
        float tErsF = kw < 0 ? 260f + sp * 15f : 380f + sp * 20f;
        float tGear = Mathf.Clamp01((sp - 4f) / 30f) * (0.25f + 0.75f * tThr) * (vw == 1 ? 0.014f : vw == 0 ? 0.004f : 0.003f) * (cc == 0 ? 1f : 0.6f);
        float gearF0 = 130f + sp * 15f;
        float tChat = turbo && tThr < 0.2f ? chLvl : 0;
        bool shiftFlag = shifting;

        // the rival cars, into their own stereo buffers
        float vR = mute ? 0 : vMaster;
        rivalsAudio.Mix(bufL, bufR, frames, fs, vEng * vR, vFx * vR, cc, red, tLive > 0.5f);

        for (int i = 0; i < frames; i++)
        {
            live += (tLive - live) * kG;
            vView += (tView - vView) * kG;
            cpL += (tpL - cpL) * kG; cpR += (tpR - cpR) * kG; cpG += (tpG - cpG) * kG; cpD += (tpD - cpD) * kG; cpWh += (tpW - cpWh) * kS;
            // limiter: ignition cut while the revs fall about 2.5 %, then it fires again and climbs back
            float limT = 0;
            if (atLim)
            {
                limPh += limHz / fs; if (limPh >= 1) { limPh -= 1; if (Noise() > crThr - 0.15f) crackle = 0.4f + 0.4f * Mathf.Abs(Noise()); }
                float lp = (float)limPh;
                limT = lp < 0.4f ? -limDrop * (lp / 0.4f) : -limDrop * (1 - (lp - 0.4f) / 0.6f);
                ign = lp < 0.4f ? 0.15f : 1f;
            }
            else { limPh = 0; ign += (1 - ign) * kG; }
            limOff += (limT - limOff) * kR;
            curRpm += (tRpm - curRpm) * (tRpm >= curRpm ? kR : tThr < 0.15f ? kRfall : kR * 0.5f); curThr += (tThr - curThr) * kG;
            float tBoost = bIn >= 0 ? bIn : tThr * Mathf.Clamp01(curRpm * spool);
            boost += (tBoost - boost) * (bIn >= 0 ? kG : kB);
            // tyres: 8 ms attack, 120 ms release
            scrub += (tScrub - scrub) * (tScrub > scrub ? kTa : kTr); scrubRG += (tScrubR - scrubRG) * (tScrubR > scrubRG ? kTa : kTr);
            groanG += (tGroan - groanG) * (tGroan > groanG ? kTa : kTr); squeal += (tSqueal - squeal) * (tSqueal > squeal ? kTa : kTr);
            lockG += (tLock - lockG) * (tLock > lockG ? kTa : kTr); spinG += (tSpin - spinG) * (tSpin > spinG ? kTa : kTr);
            sqF += (tSqF - sqF) * kTa; grF += (tGrF - grF) * kTa; sqBright += (tBright - sqBright) * kTa; sqWobD += (tWobD - sqWobD) * kTa;
            spinF0 += (tSpinF - spinF0) * kTa; spinAm += (tSpinAm - spinAm) * kTa;
            duck += (tDuck - duck) * (tDuck < duck ? kTa : kDr); tyrePanS += (tPan - tyrePanS) * kG; grWobS += (grWob - grWobS) * kG;
            windG += (tWind - windG) * kS; kerbG += (tKerb - kerbG) * kS;
            float tCut = cutN > 0 || shiftFlag ? 0.12f : 1f; if (cutN > 0) { cutN--; if (cutN == 0) { bark = cc == 0 ? 0.8f : 0.4f; crackle = Mathf.Max(crackle, cc == 0 ? 0.4f : 0.25f); } }
            cut += (tCut - cut) * (tCut < cut ? 0.02f : kG);
            gravG += (tGrav - gravG) * kS; grassG += (tGrass - grassG) * kS; chatG += (tChat - chatG) * kG;
            gearG += (tGear - gearG) * kS; ersG += (tErsG - ersG) * kS; ersHm += (tErsHm - ersHm) * kS; ersF += (tErsF - ersF) * kG;
            lockFG += (tLockF - lockFG) * (tLockF > lockFG ? kTa : kTr); scrapeG += (tScrape - scrapeG) * kG; hissG += (tHiss - hissG) * kS;
            rushG += (tRush - rushG) * kS; boomG += (tBoom - boomG) * kS; thumpG += (tThump - thumpG) * kS;
            gust += (gustT - gust) * kGust; turb += (turbT - turb) * kTurb;
            rainG += (tRain - rainG) * kS; sprayG += (tSpray - sprayG) * kS;
            float rpmNow = curRpm + limOff;
            if ((i & 31) == 0)
            {
                float f0c = rpmNow / 20f * cpD;
                // exhaust/intake resonances follow the firing note; throttle opens the tone up; the helmet takes a little top off
                body1.BandPass(Mathf.Clamp(f0c * b1Mul, 120, 900), b1Q, fs);
                body2.BandPass(Mathf.Clamp(f0c * b2Mul, 300, 3200), b2Q, fs);
                subF.LowPass(Mathf.Clamp(f0c * 0.8f, 60, 600), 0.9f, fs);
                indF.BandPass(Mathf.Clamp(f0c * 2.5f, 400, 4000), 1.4f, fs);
                float toneFc = (toneBase + curThr * toneThr + rpmNow * toneRpm) * (1.05f - 0.2f * vView);
                tone.LowPass(toneFc, 0.8f, fs);
                Weigh(cc, f0c * 0.5f, toneFc * 1.2f, fs, curThr, vView, wt);
                windF.LowPass((350 + sp * 14f) * (1 + 0.15f * gust), 0.7f, fs);
                if (rushG > 1e-5f) rushF.BandPass(700 + sp * 12f, 0.7f, fs);
                if (boomG > 1e-5f) boomF.BandPass(180 + sp * 1.2f, 1.5f, fs);
                if (sprayG > 1e-4f) sprayF.BandPass(900 + sp * 22f, 0.8f, fs);
                if (grassG > 1e-4f) grassF.LowPass(250 + sp * 8f, 0.7f, fs);
                // the tyre notes climb as the slip grows; fronts lower (understeer), rears higher (oversteer)
                if (scrub > 1e-5f) scrubF.BandPass(280 + 150 * Mathf.Min(1.3f, xF), 1.3f, fs);
                if (scrubRG > 1e-5f) scrubRF.BandPass(430 + 220 * Mathf.Min(1.3f, xR), 1.2f, fs);
                if (groanG > 1e-5f) groanF.BandPass(grF * (1 + 0.03f * grWob), 5f, fs);
                if (squeal > 1e-5f)
                {
                    sqLfo += 32.0 * wobHz / fs; if (sqLfo >= 1) sqLfo -= 1;
                    float f = sqF * (1 + sqWobD * (0.7f * Mathf.Sin((float)sqLfo * 6.2831853f) + 0.5f * sqWob));
                    squealF.BandPass(f, 10f, fs); squeal2F.BandPass(f * 2.02f, 7f, fs);
                }
                if (spinG > 1e-5f) { spinF.BandPass(spinF0, 1.8f, fs); spinHiF.BandPass(2200 + 0.6f * spinF0, 2f, fs); }
            }
            // ---- engine: one pulse per firing ----
            wobR += (wobT - wobR) * kWob;
            float f0 = rpmNow / 20f * cpD * (1f + wobR);
            firePh += f0 * rateK / fs;
            halfPh += f0 * 0.5f / fs; if (halfPh >= 1) halfPh -= 1;
            if (firePh >= 1)
            {
                firePh -= 1; cyl = (cyl + 1) % 6;
                // uneven gaps (150 / 90 degrees) plus a little cycle-to-cycle timing jitter
                rateK = ((cyl & 1) == 0 ? 1f / (1f + uneven) : 1f / (1f - uneven)) * (1f + timeJit * Noise());
                wobT = 0.0025f * Noise() * (0.4f + curThr);   // slow wander of the whole note
                pulseGain = cylG[cyl] * (1 + jitter * Noise());
                // overrun: unburnt fuel popping in the exhaust when the driver lifts at high revs
                if (curThr < 0.05f && curRpm > crRpm && Noise() > crThr) crackle = 0.5f + 0.5f * Mathf.Abs(Noise());
            }
            // downshift: a short train of pops (the throttle blip lets fuel into a hot exhaust)
            if (popsLeft > 0 && --popWait <= 0) { crackle = 0.6f + 0.4f * Mathf.Abs(Noise()); popsLeft--; popWait = (int)((0.03f + 0.06f * Mathf.Abs(Noise())) * fs); }
            float ph = (float)firePh;
            float pulse = (1 - ph); pulse = pulse * pulse * pulse * 2f - 0.5f;          // sharp bang that dies away
            float raw = pulse * pulseGain;
            float load = 0.22f + 0.78f * curThr;
            float rpmFrac = Mathf.Clamp01(rpmNow / red);
            sg += (pulseGain - sg) * kSg;
            // the pulse train (transients, noisy edge) plus the tonal stack on the firing orders
            float e = tone.Run(raw * 0.4f + body1.Run(raw) * b1W * (0.6f + 0.3f * curThr + 0.3f * vView) + body2.Run(raw) * b2W * (0.3f + 0.6f * curThr));
            e += Stack((float)halfPh * 6.2831853f, wt) * (0.3f + 0.7f * sg) * 0.26f;
            e += subF.Run(raw) * (0.10f + 0.34f * vView) * (0.5f + 0.5f * curThr);    // the firing fundamental: chest thump onboard
            // under load the exhaust note hardens: asymmetric soft clip adds the even and odd harmonics (rasp)
            float drive = 1f + 2.4f * curThr * (0.3f + rpmFrac), ed = e * drive + 0.12f * e * e * curThr;
            e = Mathf.Lerp(e, ed / (1f + Mathf.Abs(ed)) * 1.45f, 0.7f * curThr);
            // raspy top: the sharp edge of each pulse through a 2.7 kHz band, strong on throttle, more in the chase view
            e += raspF.Run(raw) * raspC * curThr * (0.3f + 0.7f * rpmFrac) * (1f - 0.45f * vView);
            // induction: noise breathed in with each firing, through the airbox resonance
            float breath = 1 - ph; breath *= breath; breath *= breath;
            e += indF.Run(Noise() * (0.25f + breath)) * curThr * (0.35f + 0.65f * rpmFrac) * 0.09f * intake * (0.7f + 0.9f * vView);
            float engine = e * load * cut * ign * engGain * (1 + bark) * (1f + 0.2f * vView);
            bark *= barkDecay;
            crackle *= crackDecay;
            if (crackle > 1e-4f) { float pn = Noise() * crackle; engine += popF.Run(pn) * 0.5f + popLoF.Run(pn) * 0.45f; }
            if (turbo)
            {
                float tv1 = 1 + 0.3f * vView;
                // turbo whistle and compressor hiss
                whistlePh += (whBase + boost * whSpan) * cpD / fs; if (whistlePh >= 1) whistlePh -= 1;
                engine += Mathf.Sin((float)whistlePh * 6.2831853f) * boost * boost * whLvl * tv1;
                float hs = boost * curThr;
                if (hs > 0.02f) engine += compF.Run(Noise()) * hs * whLvl * 1.6f * tv1;
                // wastegate / blow-off "chuff" after a lift (no MGU-H to soak up the boost any more)
                if (chuff > 1e-4f) { engine += chuffF.Run(Noise()) * chuff * whLvl * 6f * tv1; chuff *= chuffDecay; }
                // bypass valve after a lift
                if (bovG > 1e-4f) { engine += bovF.Run(Noise()) * bovG * whLvl * 4.5f; bovG *= bovDecay; }
                // wastegate / bypass flutter while the boost bleeds off after a lift
                if (chatG > 1e-5f)
                {
                    chatPh += (16f + boost * 14f) / fs; if (chatPh >= 1) chatPh -= 1;
                    float gate = chatPh < 0.35 ? 1f : 0.15f;
                    engine += chatF.Run(Noise()) * gate * chatG * boost * 4f;
                }
            }
            // ---- gearbox whine: pitched by road speed, loud onboard ----
            if (gearG > 1e-5f)
            {
                gearPh += gearF0 / fs; if (gearPh >= 1) gearPh -= 1;
                float ga = (float)gearPh * 6.2831853f;
                engine += (Mathf.Sin(ga) + 0.5f * Mathf.Sin(3 * ga)) * gearG;
            }
            // ---- MGU-K whine (F1) ----
            if (ersG > 1e-5f)
            {
                ersPh += ersF * cpD / fs; if (ersPh >= 1) ersPh -= 1;
                float a = (float)ersPh * 6.2831853f;
                float w = Mathf.Sin(a) + Mathf.Sin(a + a) * (0.3f - 0.18f * ersHm);   // deploy: brighter 2nd harmonic
                w *= 1 + ersHm * 0.35f * Noise();                                      // harvest: grittier
                engine += w * ersG;
            }
            // ---- tyres ----
            float n = Noise();
            float grip = 0;
            if (scrub > 1e-5f) grip += scrubF.Run(n) * scrub;
            if (scrubRG > 1e-5f) grip += scrubRF.Run(n) * scrubRG;
            if (groanG > 1e-5f) grip += groanF.Run(n) * groanG * (1 + 0.3f * grWobS);
            if (squeal > 1e-5f) grip += (squealF.Run(n) + squeal2F.Run(n) * (0.15f + 0.6f * sqBright)) * squeal * (1 + 0.2f * sqWob * sqBright);
            if (spinG > 1e-5f)
            {
                spinPh += spinAm / fs; if (spinPh >= 1) spinPh -= 1;
                float am = spinPh < 0.35 ? 1f : 0.2f;                                     // rubber stick-slipping: a fast buzz
                grip += (spinF.Run(n) * am + spinHiF.Run(n) * (0.35f + 0.35f * am)) * spinG;
            }
            if (lockG > 1e-5f)
            {
                float h = lockHF.Run(n) * lockG * 12f; h = h / (1 + Mathf.Abs(h)) * 0.12f; // harsh, flat, clipped skid
                grip += lockF.Run(n) * lockG + h;
            }
            if (lockFG > 1e-5f) grip += lockFrF.Run(n) * lockFG;                         // narrow band = a light chirp
            grip *= tyreView;
            float tyres = grip;
            // ---- wind ----
            float wn = Noise();
            buffet += (wn - buffet) * kBuf;
            float gustMul = 1 + 0.25f * gust * vn;
            float turbMul = 1 + 0.6f * towIn * turb;
            float wind = (windF.Run(wn) * windG + hissF.Run(wn) * hissG) * Mathf.Max(0.3f, 1 + buffet * buffDepth * 8f) * gustMul * turbMul;
            if (rushG > 1e-5f) wind += rushF.Run(wn) * rushG * 2f * gustMul * turbMul;
            if (boomG > 1e-5f) wind += boomF.Run(wn) * boomG * 2.5f * gustMul;
            if (thumpG > 1e-5f) wind += thumpF.Run(wn) * thumpG * 3f * (0.5f + Mathf.Abs(turb));
            if (rainG > 1e-5f) wind += rainF.Run(wn) * rainG;
            float fxCar = 0;
            if (sprayG > 1e-5f) fxCar += sprayF.Run(n) * sprayG;
            if (scrapeG > 1e-5f)
            {
                // plank on the road: a gritty, bright scrape in short random bursts over a dull grinding
                if (Noise() * 0.5f + 0.5f < gritP) scrapeGrit = 0.5f + 0.5f * Mathf.Abs(Noise());
                scrapeGrit *= gritDecay;
                tyres += (scrapeF.Run(n * (0.35f + scrapeGrit)) + scrapeLoF.Run(n) * 0.8f) * scrapeG;
            }
            if (kerbG > 1e-5f)
            {
                kerbPh += Mathf.Clamp(sp * 1.6f, 12, 70) / fs; if (kerbPh >= 1) kerbPh -= 1;
                float sq = kerbPh < 0.3 ? 1f : -0.4f;
                fxCar += (kerbF.Run(sq + n * 0.3f) * 1.4f + sq * 0.25f) * kerbG * 0.5f;     // thudding ribs, a little edge
            }
            if (tSaus > 0)
            {
                // sausage kerbs: a heavy thump per hump
                if (Noise() * 0.5f + 0.5f < sausP) { sausEnv = 0.7f + 0.3f * Mathf.Abs(Noise()); thumpPh = 0; }
                sausEnv *= sausDecay; thumpPh += 70.0 / fs;
                fxCar += Mathf.Sin((float)thumpPh * 6.2831853f) * sausEnv * tSaus;
            }
            if (gravG > 1e-4f || grassG > 1e-4f)
            {
                float rumble = rumbleF.Run(n) * 2.5f;
                if (gravG > 1e-4f)
                {
                    // gravel: random stone impacts, each a short crunchy noise burst; more of them the faster
                    if (Noise() * 0.5f + 0.5f < gravP) gravEnv = 0.4f + 0.6f * Mathf.Abs(Noise());
                    gravEnv *= gravDecay;
                    fxCar += (gravF.Run(n * (gravEnv + 0.08f)) * 0.5f + rumble * 0.10f) * gravG;
                }
                if (grassG > 1e-4f)
                {
                    // grass: soft low swish that breathes slowly, over a dull rumble
                    swishPh += swishInc; if (swishPh >= 1) swishPh -= 1;
                    float sw = (float)swishPh; sw = sw < 0.5f ? sw * 2 : 2 - sw * 2;   // triangle 0..1
                    fxCar += (grassF.Run(n) * (0.6f + 0.4f * sw) * 0.35f + rumble * 0.07f) * grassG;
                }
            }
            // ---- mix ----
            float m = engine * vEng * duck + tyres * vTyre + fxCar * vFx;
            float side = grip * vTyre * tyrePanS;                                       // + = right
            // DC blocker (the saturation and pulse shape are not symmetric)
            float dc = m - dcX + 0.9985f * dcY; dcX = m; dcY = dc; m = dc;
            float wh = 0;
            if (tv) { m = pToneF.Run(m) * cpG; if (cpWh > 1e-5f) wh = pWhF.Run(Noise()) * cpWh * 0.6f * vFx; }
            float k = mute ? 0 : vMaster * live;
            float outL = ((m + wh) * cpL - side + wind * vFx) * k + bufL[i];
            float outR = ((m + wh) * cpR + side + wind * vFx) * k + bufR[i];
            // soft limit so peaks never crackle on a phone speaker
            outL = outL / (1 + Mathf.Abs(outL)); outR = outR / (1 + Mathf.Abs(outR));
            int o = (start + i) * channels;
            if (channels == 1)
            {
                float mo = (outL + outR) * 0.5f;
                if (add) data[o] = Mathf.Clamp(data[o] + mo, -1f, 1f); else data[o] = mo;
            }
            else if (add)
            {
                data[o] = Mathf.Clamp(data[o] + outL, -1f, 1f); data[o + 1] = Mathf.Clamp(data[o + 1] + outR, -1f, 1f);
            }
            else { data[o] = outL; data[o + 1] = outR; }
        }
    }
}
