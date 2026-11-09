// EngineAudio.cs — engine, tyre and wind sound synthesised live (no sound files to download).
// Turbo V6 note: 3 firing pulses per crank revolution; louder and harsher with throttle.
// Tyre squeal rises when the tyres slide past their grip peak; wind noise grows with speed.
using UnityEngine;

public class EngineAudio : MonoBehaviour
{
    // written by the game every frame, read on the audio thread
    public volatile float rpm = 3000, throttle, slide, speed;
    public bool muted;

    AudioSource src;
    int rate;
    double phEngine, phSub, phSqueal;
    float lp, wind, squealEnv, rpmS = 3000, thrS;
    readonly System.Random rng = new System.Random(1);

    void Start()
    {
        rate = AudioSettings.outputSampleRate;
        var clip = AudioClip.Create("engine", rate, 1, rate, true, OnRead);
        src = gameObject.AddComponent<AudioSource>();
        src.clip = clip; src.loop = true; src.spatialBlend = 0; src.volume = 0.7f; src.playOnAwake = false;
        src.Play();
    }

    void Update() { if (src != null) src.mute = muted; }

    void OnRead(float[] data)
    {
        float noise() => (float)rng.NextDouble() * 2 - 1;
        for (int i = 0; i < data.Length; i++)
        {
            // smooth the inputs per sample so pitch changes glide
            rpmS += (rpm - rpmS) * 0.0008f;
            thrS += (throttle - thrS) * 0.0005f;
            double fire = rpmS / 60.0 * 3.0; // firing frequency (Hz)
            phEngine += fire / rate; if (phEngine > 1) phEngine -= 1;
            phSub += fire * 0.5 / rate; if (phSub > 1) phSub -= 1;
            float saw = (float)(phEngine * 2 - 1);
            float pulse = Mathf.Exp(-(float)phEngine * 6f) * 2 - 0.5f; // combustion "bark"
            float tone = 0.45f * saw + 0.35f * pulse + 0.3f * Mathf.Sin((float)(phSub * 2 * Mathf.PI));
            float rough = noise() * (0.08f + 0.25f * thrS);
            float raw = tone + rough;
            // low-pass that opens with throttle and revs (muffled when coasting)
            float cut = 0.08f + 0.35f * thrS + 0.15f * Mathf.Clamp01(rpmS / 9000f);
            lp += (raw - lp) * cut;
            float engine = lp * (0.18f + 0.32f * thrS);

            // tyre squeal: narrow-band noise around 700-1000 Hz when sliding
            squealEnv += (Mathf.Clamp01(slide) - squealEnv) * 0.0005f;
            phSqueal += (780 + 160 * Mathf.Sin(i * 0.0007f)) / rate; if (phSqueal > 1) phSqueal -= 1;
            float squeal = Mathf.Sin((float)(phSqueal * 2 * Mathf.PI)) * (0.6f + 0.4f * noise()) * squealEnv * 0.22f;

            // wind: filtered noise, grows with speed^2
            wind += (noise() - wind) * 0.05f;
            float w = wind * Mathf.Clamp01(speed * speed / 6400f) * 0.25f;

            data[i] = Mathf.Clamp(engine + squeal + w, -1f, 1f);
        }
    }
}
