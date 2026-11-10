// EngineAudio.cs — the car's sound, synthesised sample by sample from the physics, with no recorded clips.
// It is what the driver steers the throttle by, so everything follows the car within a few milliseconds:
//  * engine: one exhaust pulse per cylinder firing (V6: 3 firings per crank turn, so 12,000 rpm = 600 Hz).
//    The six cylinders are not identical, which gives the rough "half-order" growl under the main note.
//    Throttle makes it louder and brighter; off-throttle it goes quiet and muffled, with overrun crackle.
//    The engine speed follows the driven wheels, so wheelspin is heard as the revs flaring.
//  * turbo whistle that builds with boost, a short ignition cut on every gear change, rev-limiter stutter
//  * tyres: a soft scrub as they approach their limit, a squeal past it, a lower howl when a wheel locks,
//    and a harsher note for wheelspin
//  * wind that grows with speed, and a rumble on the kerbs
// It runs in the audio mixer callback (OnAudioFilterRead), so the delay is one small audio buffer.
// WebGL has no such callback; there it falls back to a streamed clip (slower to react).
using UnityEngine;

public class EngineAudio : MonoBehaviour
{
    // written by the game every frame, read on the audio thread
    public volatile float rpm = 4000, throttle, slide, speed, redline = 12000;
    public volatile float wheelspin, lockup;   // 0..1
    public volatile bool onAsphalt = true, onKerb, shifting;
    public volatile bool muted;
    const float Volume = 0.55f;

    int rate = 48000;
    double firePh;          // firing phase 0..1
    int cyl;                // which cylinder fires next
    float curRpm = 4000, curThr, boost, gain, scrub, squeal, lockG, spinG, windG, kerbG, limiter, cut = 1;
    double whistlePh, kerbPh, limPh;
    float pulseGain = 1, crackle;
    static readonly float[] CylGain = { 1f, 0.80f, 0.94f, 0.76f, 0.98f, 0.86f };
    readonly Biquad body1 = new Biquad(), body2 = new Biquad(), tone = new Biquad(), scrubF = new Biquad(), squealF = new Biquad(), lockF = new Biquad(), spinF = new Biquad(), windF = new Biquad(), popF = new Biquad();
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
    }

    void Render(float[] data, int channels, bool add)
    {
        int frames = data.Length / channels;
        float fs = rate;
        // smoothing per sample: revs 8 ms, levels 25 ms, tyre sounds 40 ms
        float kR = 1 - Mathf.Exp(-1f / (0.008f * fs)), kG = 1 - Mathf.Exp(-1f / (0.025f * fs)), kS = 1 - Mathf.Exp(-1f / (0.04f * fs)), kB = 1 - Mathf.Exp(-1f / (0.35f * fs));
        float tRpm = Mathf.Clamp(rpm, 1500, 16000), tThr = Mathf.Clamp01(throttle), sp = speed, red = redline;
        bool mute = muted;
        float sl = slide;
        float tScrub = onAsphalt && sp > 8 ? Mathf.Clamp01((sl - 0.80f) / 0.18f) * 0.05f : 0;
        float tSqueal = onAsphalt && sp > 6 ? Mathf.Clamp((sl - 0.97f) * 1.6f, 0, 0.12f) : 0;
        float tLock = onAsphalt && sp > 4 ? Mathf.Clamp01(lockup) * 0.16f : 0;
        float tSpin = onAsphalt ? Mathf.Clamp01(wheelspin) * 0.12f : 0;
        float tWind = Mathf.Clamp01(sp / 95f); tWind = tWind * tWind * 0.11f;
        float tKerb = onKerb && sp > 5 ? 0.22f : 0;
        float tCut = shifting ? 0.15f : 1f;
        for (int i = 0; i < frames; i++)
        {
            curRpm += (tRpm - curRpm) * kR; curThr += (tThr - curThr) * kG; boost += (tThr * Mathf.Clamp01(curRpm / 9000f) - boost) * kB;
            scrub += (tScrub - scrub) * kS; squeal += (tSqueal - squeal) * kS; lockG += (tLock - lockG) * kS; spinG += (tSpin - spinG) * kS;
            windG += (tWind - windG) * kS; kerbG += (tKerb - kerbG) * kS; cut += (tCut - cut) * (shifting ? 0.02f : kG);
            if ((i & 31) == 0)
            {
                float f0c = curRpm / 60f * 3f;
                // exhaust/intake resonances follow the firing note; throttle opens the tone up
                body1.BandPass(Mathf.Clamp(f0c * 1.0f, 120, 900), 1.1f, fs);
                body2.BandPass(Mathf.Clamp(f0c * 2.0f, 300, 2400), 1.6f, fs);
                tone.LowPass(700 + curThr * 4200 + curRpm * 0.12f, 0.8f, fs);
                windF.LowPass(350 + sp * 14f, 0.7f, fs);
                limiter = curRpm > red - 60 ? 1 : 0;
            }
            // ---- engine: one pulse per firing ----
            float f0 = curRpm / 60f * 3f;
            firePh += f0 / fs;
            if (firePh >= 1)
            {
                firePh -= 1; cyl = (cyl + 1) % 6;
                pulseGain = CylGain[cyl] * (1 + 0.06f * Noise());
                // overrun: unburnt fuel popping in the exhaust when the driver lifts at high revs
                if (curThr < 0.05f && curRpm > 7000 && Noise() > 0.93f) crackle = 0.5f + 0.5f * Mathf.Abs(Noise());
            }
            float ph = (float)firePh;
            float pulse = (1 - ph); pulse = pulse * pulse * pulse * 2f - 0.5f;          // sharp bang that dies away
            float raw = pulse * pulseGain;
            float load = 0.22f + 0.78f * curThr;
            float e = tone.Run(raw * 0.55f + body1.Run(raw) * 0.9f + body2.Run(raw) * 0.6f * (0.4f + 0.6f * curThr));
            if (limiter > 0) { limPh += 28.0 / fs; if (limPh >= 1) limPh -= 1; if (limPh > 0.5) e *= 0.2f; }
            float engine = e * load * cut * 0.9f;
            crackle *= 1 - 1f / (0.012f * fs);
            engine += popF.Run(Noise() * crackle) * 0.5f;
            // turbo whistle
            whistlePh += (5200 + boost * 5200) / fs; if (whistlePh >= 1) whistlePh -= 1;
            engine += Mathf.Sin((float)whistlePh * 6.2831853f) * boost * boost * 0.012f;
            // ---- tyres, wind, kerbs ----
            float n = Noise();
            float tyres = scrubF.Run(n) * scrub + squealF.Run(n) * squeal + lockF.Run(n) * lockG + spinF.Run(n) * spinG;
            float wind = windF.Run(Noise()) * windG;
            kerbPh += Mathf.Clamp(sp * 1.6f, 12, 70) / fs; if (kerbPh >= 1) kerbPh -= 1;
            float kerb = (kerbPh < 0.3 ? 1f : -0.4f) * kerbG * 0.5f;
            float outS = mute ? 0 : (engine + tyres + wind + kerb) * Volume;
            // soft limit so peaks never crackle on a phone speaker
            outS = outS / (1 + Mathf.Abs(outS));
            int o = i * channels;
            if (add) for (int c = 0; c < channels; c++) data[o + c] = Mathf.Clamp(data[o + c] + outS, -1f, 1f);
            else data[o] = outS;
        }
    }
}
