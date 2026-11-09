// EngineAudio.cs — the web prototype's sound, ported 1:1 (it sounded better than the first Unity version):
// engine = sawtooth at rpm/60*1.5 + square at half that (x0.35) -> low-pass (500 + throttle*2600 + rpm*0.08 Hz)
//          -> gain 0.035 + 0.075*throttle
// squeal = white noise -> band-pass 1700 Hz (Q 5) -> gain from how far the tyres are past their grip
// Parameter changes glide with the same time constants as WebAudio's setTargetAtTime (15/30/50 ms).
using UnityEngine;

public class EngineAudio : MonoBehaviour
{
    // written by the game every frame, read on the audio thread
    public volatile float rpm = 3000, throttle, slide, speed;
    public volatile bool onAsphalt = true;
    public bool muted;
    const float Volume = 4.0f; // WebAudio levels were low; scale to a phone speaker

    AudioSource src;
    int rate;
    double ph1, ph2;
    float freq, cutoff = 1200, gain, sqGain;
    readonly Biquad lp = new Biquad(), bp = new Biquad();
    readonly System.Random rng = new System.Random(1);

    class Biquad
    {
        float b0, b1, b2, a1, a2, x1, x2, y1, y2;
        public void LowPass(float f, float q, float fs) { Set(f, q, fs, false); }
        public void BandPass(float f, float q, float fs) { Set(f, q, fs, true); }
        void Set(float f, float q, float fs, bool band)
        {
            float w = 2 * Mathf.PI * Mathf.Clamp(f, 20, fs * 0.45f) / fs, cw = Mathf.Cos(w), al = Mathf.Sin(w) / (2 * q), a0 = 1 + al;
            if (band) { b0 = al / a0; b1 = 0; b2 = -al / a0; }
            else { b0 = (1 - cw) / 2 / a0; b1 = (1 - cw) / a0; b2 = b0; }
            a1 = -2 * cw / a0; a2 = (1 - al) / a0;
        }
        public float Run(float x) { float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2; x2 = x1; x1 = x; y2 = y1; y1 = y; return y; }
    }

    void Start()
    {
        rate = AudioSettings.outputSampleRate;
        bp.BandPass(1700, 5, rate);
        var clip = AudioClip.Create("engine", rate, 1, rate, true, OnRead);
        src = gameObject.AddComponent<AudioSource>();
        src.clip = clip; src.loop = true; src.spatialBlend = 0; src.volume = 1f; src.playOnAwake = false;
        src.Play();
    }

    void Update() { if (src != null) src.mute = muted; }

    void OnRead(float[] data)
    {
        // per-sample smoothing factors for 15 / 30 / 50 ms time constants
        float kF = 1 - Mathf.Exp(-1f / (0.015f * rate)), kG = 1 - Mathf.Exp(-1f / (0.03f * rate)), kS = 1 - Mathf.Exp(-1f / (0.05f * rate));
        float targetF = rpm / 60f * 1.5f, thr = throttle;
        float targetCut = 500 + thr * 2600 + rpm * 0.08f, targetGain = 0.035f + 0.075f * thr;
        float targetSq = onAsphalt && speed > 6 ? Mathf.Clamp((slide - 0.96f) * 1.5f, 0, 0.1f) : 0;
        if (freq == 0) freq = targetF;
        for (int i = 0; i < data.Length; i++)
        {
            freq += (targetF - freq) * kF;
            gain += (targetGain - gain) * kG;
            sqGain += (targetSq - sqGain) * kS;
            if ((i & 31) == 0) { cutoff += (targetCut - cutoff) * (1 - Mathf.Pow(1 - kG, 32)); lp.LowPass(cutoff, 0.707f, rate); }
            ph1 += freq / rate; if (ph1 >= 1) ph1 -= 1;
            ph2 += freq * 0.5 / rate; if (ph2 >= 1) ph2 -= 1;
            float saw = (float)(ph1 * 2 - 1);
            float square = ph2 < 0.5 ? 1f : -1f;
            float engine = lp.Run(saw + 0.35f * square) * gain;
            float squeal = bp.Run((float)rng.NextDouble() * 2 - 1) * sqGain;
            data[i] = Mathf.Clamp((engine + squeal) * Volume, -1f, 1f);
        }
    }
}
