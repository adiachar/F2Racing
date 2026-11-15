// RivalAudio.cs — the other cars, heard in 3D. Part of EngineAudio (it is mixed inside EngineAudio's audio
// callback, so it costs no extra AudioSource or filter).
//  * voices: the four rivals closest to the camera (within 150 m) each get a voice. A voice keeps its car until the
//    car drops out of the closest four, then fades out and only then takes a new car (no clicks or pitch jumps).
//  * each voice is a cheaper copy of the player's engine: the same firing pulse and cylinder spread for the class,
//    one exhaust resonance and one tone filter, driven by that car's own revs and throttle, with a short ignition
//    cut on upshifts and a few pops on downshifts
//  * where it is: loudness falls with distance (1/d past 4 m, faded out by 150 m), air takes the highs off with
//    distance, a car behind the camera sounds duller, a car pointing away from us (exhaust towards us) louder and
//    brighter; panned left/right by its bearing from the camera (equal power)
//  * Doppler: pitch from the closing speed between the car and the camera (slightly exaggerated, as TV mics do)
//  * whoosh: band-passed air noise that swells with the relative speed when a car is very close (passing or
//    being passed), panned with the car
// Game thread: Update() once per frame (via EngineAudio.Feed). Audio thread: Mix(). Nothing allocates in Mix.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public struct SpatialCue { public float gain, l, r, dopp, tone, whoosh, whooshF, dist; }

public class RivalAudio
{
    public const int MaxVoices = 4;
    const float MaxDist = 150f, FadeFrom = 110f, NearDist = 4f, SoundC = 343f, DopplerBoost = 1.25f, Level = 0.5f, WhooshLevel = 0.7f;

    sealed class Voice
    {
        // game thread -> audio thread
        public volatile int rival = -1;          // index into the rivals list, -1 = free
        public volatile bool reset;              // a new car in this slot: start silent at its revs
        public volatile float rpm = 4000, thr, gain, l = 1, r = 1, dopp = 1, tone = 6000, whoosh, whooshF = 800;
        public volatile int upSeq, downSeq;
        public volatile float level;             // audio thread -> game thread: current loudness (slot reusable when ~0)
        public int lastGear = 1;                 // game thread only
        // audio thread only
        public double ph, hph; public float rateK = 1, sg = 1; public readonly float[] wt = new float[6]; public int cyl, seenUp, seenDown, cutN, popsLeft, popWait;
        public float cRpm = 4000, cThr, cG, cL = 1, cR = 1, cDopp = 1, cWh, cTone = 6000, pulseG = 1, crackle, cut = 1;
        public uint seed = 1;
        public readonly EngineAudio.Biquad body = new EngineAudio.Biquad(), toneF = new EngineAudio.Biquad(), wh = new EngineAudio.Biquad();
    }

    readonly Voice[] voices = new Voice[MaxVoices];
    readonly int[] cand = new int[MaxVoices];
    readonly float[] candD = new float[MaxVoices];

    public RivalAudio()
    {
        for (int i = 0; i < MaxVoices; i++) voices[i] = new Voice { seed = 0x9E3779B9u ^ (uint)(i * 7919 + 13) };
    }

    // ---------------------------------------------------------------- game thread
    // Where a car is heard from the camera. near: distance (m) inside which it is at full loudness.
    public static void Spatial(FormulaCar c, Vector3 cp, Vector3 right, Vector3 fwd, Vector3 camVel, float near, out SpatialCue s)
    {
        float dx = (float)c.x - cp.x, dz = (float)c.y - cp.z;         // sim (x, y) = Unity (x, z)
        float d = Mathf.Sqrt(dx * dx + dz * dz + 1f);                 // +1: a car right under the camera is not inside the head
        float ux = dx / d, uz = dz / d;                               // not unit when very close: keeps the pan soft there
        float h = (float)c.heading, ch = Mathf.Cos(h), sh = Mathf.Sin(h);
        float vx = (float)c.vx, vy = (float)c.vy;
        float wx = vx * ch - vy * sh, wz = vx * sh + vy * ch;         // the car's velocity in the world
        if (float.IsNaN(wx) || float.IsNaN(wz)) { wx = 0; wz = 0; }
        // Doppler: f' = f (c + listener speed towards the source) / (c + source speed away from the listener)
        float away = wx * ux + wz * uz, toward = camVel.x * ux + camVel.z * uz;
        float dopp = (SoundC + DopplerBoost * toward) / (SoundC + DopplerBoost * away);
        s.dopp = float.IsNaN(dopp) ? 1f : Mathf.Clamp(dopp, 0.7f, 1.4f);
        // pan by the bearing from the camera (equal power, centre = 1 on both sides)
        float px = ux * right.x + uz * right.z, pz = ux * fwd.x + uz * fwd.z;
        float pan = Mathf.Clamp(px * 0.9f, -1f, 1f), a = (pan + 1f) * 0.7853982f;
        s.l = Mathf.Cos(a) * 1.4142f; s.r = Mathf.Sin(a) * 1.4142f;
        s.dist = d;
        float exh = ch * ux + sh * uz;                                // + = the car points away from us: we hear its exhaust
        float behindHead = Mathf.Max(0f, -pz);
        s.gain = near / Mathf.Max(near, d) * (1f + 0.25f * exh);
        s.tone = 9000f / (1f + d / 45f) * (1f - 0.35f * behindHead) * (1f + 0.3f * exh);
        // whoosh: air pushed past when a car goes by close
        float rx = wx - camVel.x, rz = wz - camVel.z, rel = Mathf.Sqrt(rx * rx + rz * rz), carSp = Mathf.Sqrt(wx * wx + wz * wz);
        float prox = 1f / (1f + d * d / 30f);
        s.whoosh = prox * Mathf.Clamp01((rel - 2f) / 30f) * (0.35f + 0.65f * Mathf.Clamp01(carSp / 60f));
        if (float.IsNaN(s.whoosh)) s.whoosh = 0;
        s.whooshF = 450f + 22f * Mathf.Min(rel, 90f) + 300f * prox;
    }

    public void Update(List<AiDriver> rivals, Vector3 cp, Vector3 right, Vector3 fwd, Vector3 camVel, bool paused)
    {
        int n = rivals == null || paused ? 0 : rivals.Count;
        // the closest few, sorted
        int nc = 0;
        for (int i = 0; i < n; i++)
        {
            var c = rivals[i].car; if (c == null) continue;
            float dx = (float)c.x - cp.x, dz = (float)c.y - cp.z, d2 = dx * dx + dz * dz;
            if (!(d2 < MaxDist * MaxDist)) continue;                   // also drops NaN
            int k;
            if (nc < MaxVoices) k = nc++;
            else if (d2 < candD[MaxVoices - 1]) k = MaxVoices - 1;
            else continue;
            while (k > 0 && candD[k - 1] > d2) { candD[k] = candD[k - 1]; cand[k] = cand[k - 1]; k--; }
            candD[k] = d2; cand[k] = i;
        }
        // voices keep their car, or fade out and free themselves
        for (int v = 0; v < MaxVoices; v++)
        {
            var vo = voices[v]; int r = vo.rival; if (r < 0) continue;
            int found = -1;
            for (int j = 0; j < nc; j++) if (cand[j] == r) { found = j; break; }
            if (found < 0) { vo.gain = 0; vo.whoosh = 0; if (vo.level < 0.003f) vo.rival = -1; continue; }
            cand[found] = -1;
            Set(vo, rivals[r].car, cp, right, fwd, camVel);
        }
        // new cars into free voices
        for (int j = 0; j < nc; j++)
        {
            if (cand[j] < 0) continue;
            Voice free = null;
            for (int v = 0; v < MaxVoices; v++) if (voices[v].rival < 0) { free = voices[v]; break; }
            if (free == null) break;
            var c = rivals[cand[j]].car;
            free.lastGear = c.gear;
            Set(free, c, cp, right, fwd, camVel);
            free.rival = cand[j]; free.reset = true;
        }
    }

    static void Set(Voice vo, FormulaCar c, Vector3 cp, Vector3 right, Vector3 fwd, Vector3 camVel)
    {
        Spatial(c, cp, right, fwd, camVel, NearDist, out var s);
        float fade = Mathf.Clamp01((MaxDist - s.dist) / (MaxDist - FadeFrom));
        float rp = (float)c.rpm, th = (float)c.throttle;
        vo.rpm = float.IsNaN(rp) ? 4000f : rp; vo.thr = float.IsNaN(th) ? 0f : Mathf.Clamp01(th);
        vo.gain = s.gain * fade * Level; vo.l = s.l; vo.r = s.r; vo.dopp = s.dopp; vo.tone = s.tone;
        vo.whoosh = s.whoosh * fade; vo.whooshF = s.whooshF;
        if (c.gear != vo.lastGear) { if (c.gear > vo.lastGear) vo.upSeq++; else vo.downSeq++; vo.lastGear = c.gear; }
    }

    // ---------------------------------------------------------------- audio thread
    // Fills L and R (cleared first) with n samples of all voices. vEng / vFx: engine and effects volume (with master).
    public void Mix(float[] L, float[] R, int n, float fs, float vEng, float vFx, int cc, float red, bool alive)
    {
        System.Array.Clear(L, 0, n); System.Array.Clear(R, 0, n);
        if (cc < 0 || cc > 2) cc = 0;
        float kR = 1 - Mathf.Exp(-1f / (0.012f * fs)), kG = 1 - Mathf.Exp(-1f / (0.04f * fs)), crDecay = 1 - 1f / (0.012f * fs);
        float[] cylG = EngineAudio.CylGainC[cc];
        float uneven = EngineAudio.UnevenC[cc], tJit = EngineAudio.TimeJitC[cc];
        float jitter = EngineAudio.JitterC[cc], b1Mul = EngineAudio.Body1MulC[cc], b1Q = EngineAudio.Body1QC[cc], b1W = EngineAudio.Body1WC[cc];
        float toneBase = EngineAudio.ToneBaseC[cc], toneThr = EngineAudio.ToneThrC[cc], toneRpm = EngineAudio.ToneRpmC[cc], engGain = EngineAudio.EngGainC[cc];
        float crThr = EngineAudio.CrackleThrC[cc], crFrac = EngineAudio.CrackleRpmC[cc];
        for (int v = 0; v < MaxVoices; v++)
        {
            var vo = voices[v];
            if (vo.rival < 0 && vo.cG < 1e-5f && vo.cWh < 1e-5f) { vo.cG = 0; vo.cWh = 0; vo.level = 0; continue; }
            if (vo.reset)
            {
                vo.reset = false;
                vo.cRpm = Mathf.Clamp(vo.rpm, 1500, 16000); vo.cThr = vo.thr; vo.cG = 0; vo.cWh = 0; vo.cDopp = vo.dopp;
                vo.cL = vo.l; vo.cR = vo.r; vo.cTone = vo.tone; vo.seenUp = vo.upSeq; vo.seenDown = vo.downSeq; vo.popsLeft = 0; vo.cutN = 0;
            }
            float tR = Mathf.Clamp(vo.rpm, 1500, 16000), tT = vo.thr, tG = alive ? vo.gain : 0, tL = vo.l, tRr = vo.r, tD = vo.dopp, tW = alive ? vo.whoosh : 0;
            float tTone = vo.tone, whF = Mathf.Clamp(vo.whooshF, 200, 5000);
            if (float.IsNaN(tR)) tR = 4000; if (float.IsNaN(tG)) tG = 0; if (float.IsNaN(tD)) tD = 1; if (float.IsNaN(tTone)) tTone = 4000;
            if (float.IsNaN(tL) || float.IsNaN(tRr)) { tL = 1; tRr = 1; }
            if (float.IsNaN(tW)) tW = 0;
            // shifts (the car's own revs already drop / blip; this adds the ignition cut and the pops)
            int us = vo.upSeq, ds = vo.downSeq;
            if (us != vo.seenUp) { vo.seenUp = us; vo.cutN = (int)(0.04f * fs); }
            if (ds != vo.seenDown) { vo.seenDown = ds; vo.cutN = (int)(0.02f * fs); if (tT < 0.3f) { vo.popsLeft = EngineAudio.DownPopsC[cc]; vo.popWait = (int)(0.03f * fs); } }
            // locals for speed
            double ph = vo.ph, hph = vo.hph; float rateK = vo.rateK, sg = vo.sg; var wt = vo.wt; int cyl = vo.cyl, cutN = vo.cutN, popsLeft = vo.popsLeft, popWait = vo.popWait;
            float cRpm = vo.cRpm, cThr = vo.cThr, cG = vo.cG, cL = vo.cL, cR = vo.cR, cDopp = vo.cDopp, cWh = vo.cWh, cTone = vo.cTone, pulseG = vo.pulseG, crackle = vo.crackle, cut = vo.cut;
            uint sd = vo.seed; if (sd == 0) sd = 1;
            var body = vo.body; var toneF = vo.toneF; var whf = vo.wh;
            float crRpm = crFrac * red;
            for (int i = 0; i < n; i++)
            {
                cRpm += (tR - cRpm) * kR; cThr += (tT - cThr) * kG; cG += (tG - cG) * kG; cL += (tL - cL) * kG; cR += (tRr - cR) * kG;
                cDopp += (tD - cDopp) * kG; cWh += (tW - cWh) * kG; cTone += (tTone - cTone) * kG;
                float tCut = cutN > 0 ? 0.15f : 1f; if (cutN > 0) cutN--;
                cut += (tCut - cut) * (tCut < cut ? 0.02f : kG);
                if ((i & 31) == 0)
                {
                    float f0c = cRpm / 20f * cDopp;
                    body.BandPass(Mathf.Clamp(f0c * b1Mul, 120, 900), b1Q, fs);
                    float fcT = Mathf.Min(cTone, toneBase + cThr * toneThr + cRpm * toneRpm);
                    toneF.LowPass(fcT, 0.8f, fs);
                    EngineAudio.Weigh(cc, f0c * 0.5f, fcT * 1.2f, fs, cThr, 0f, wt);
                    if (cWh > 1e-5f) whf.BandPass(whF, 0.9f, fs);
                }
                float f0 = cRpm / 20f * cDopp;
                ph += f0 * rateK / fs;
                hph += f0 * 0.5f / fs; if (hph >= 1) hph -= 1;
                if (ph >= 1)
                {
                    ph -= 1; cyl = (cyl + 1) % 6;
                    sd ^= sd << 13; sd ^= sd >> 17; sd ^= sd << 5; float nz = (sd & 0xFFFFFF) / 8388608f - 1f;
                    rateK = ((cyl & 1) == 0 ? 1f / (1f + uneven) : 1f / (1f - uneven)) * (1f + tJit * nz);
                    pulseG = cylG[cyl] * (1 + jitter * nz);
                    if (cThr < 0.05f && cRpm > crRpm && nz > crThr) crackle = 0.5f;
                }
                if (popsLeft > 0 && --popWait <= 0) { crackle = 0.7f; popsLeft--; popWait = (int)((0.035f + 0.04f * (cyl & 1)) * fs); }
                float p = 1 - (float)ph; p = p * p * p * 2f - 0.5f;
                float raw = p * pulseG;
                sd ^= sd << 13; sd ^= sd >> 17; sd ^= sd << 5; float noise = (sd & 0xFFFFFF) / 8388608f - 1f;
                crackle *= crDecay;
                sg += (pulseG - sg) * 0.02f;
                // same recipe as the player's engine, cheaper: pulse + body + tonal stack, soft-clipped on throttle
                float x = raw * 0.4f + body.Run(raw) * b1W * 0.7f + EngineAudio.Stack((float)hph * 6.2831853f, wt) * (0.3f + 0.7f * sg) * 0.26f;
                float xd = x * (1f + 2f * cThr);
                x = x + (xd / (1f + Mathf.Abs(xd)) * 1.4f - x) * 0.6f * cThr;
                float e = toneF.Run(x * cut + noise * crackle * 1.2f) * (0.2f + 0.8f * cThr) * engGain;
                float s = e * cG * vEng;
                if (cWh > 1e-5f) s += whf.Run(noise) * cWh * WhooshLevel * vFx;
                L[i] += s * cL; R[i] += s * cR;
            }
            vo.ph = ph; vo.hph = hph; vo.rateK = rateK; vo.sg = sg; vo.cyl = cyl; vo.cutN = cutN; vo.popsLeft = popsLeft; vo.popWait = popWait;
            vo.cRpm = cRpm; vo.cThr = cThr; vo.cG = cG; vo.cL = cL; vo.cR = cR; vo.cDopp = cDopp; vo.cWh = cWh; vo.cTone = cTone;
            vo.pulseG = pulseG; vo.crackle = crackle; vo.cut = cut; vo.seed = sd;
            vo.level = cG + cWh;
        }
    }
}
