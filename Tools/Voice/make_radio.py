#!/usr/bin/env python3
# make_radio.py — builds Assets/Resources/Radio/radio.bytes, the team-radio voice pack (TeamRadio.cs).
#
#   python3 make_radio.py --models /path/to/kokoro            (kokoro-v1.0.onnx + voices-v1.0.bin, see README.md)
#   python3 make_radio.py --models ... --preview /tmp/radio   (also writes a WAV per line and a demo transmission)
#
# 1. Speech: Kokoro-82M (open neural TTS, Apache-2.0) through kokoro-onnx; engineer bm_george (British),
#    driver am_michael. Raw takes are cached (--cache) so the radio processing can be tuned without the TTS.
# 2. Radio processing, done here so the phone does no filtering: trim, band-pass ~300-3400 Hz with a presence
#    lift, a fast compressor, soft-clip distortion, band-pass again. The driver also gets engine rumble and a bit
#    more drive (he is in the car). Noise, the key-on beep and the key-off squelch are separate clips that the
#    game mixes round each transmission, so built-up messages ("gap to car ahead" + "one point" + "two seconds")
#    run under one continuous hiss.
# 3. Storage: 11025 Hz mono (the band stops at ~3.4 kHz), IMA ADPCM, 4 bits a sample (5.5 kB/s). Unity cannot
#    decode Ogg from bytes at runtime (and WebGL Safari has no Ogg), so the game decodes this itself (a few
#    lines of C#) and upsamples to 22050 Hz.
#
# Pack format (little-endian): "RDO1", int32 sampleRate, int32 count, then per clip: byte nameLen, name (ASCII),
# int32 samples, int32 offset (bytes, from the start of the data block); then the data block. ADPCM per clip:
# predictor 0 / step index 0 at the start, low nibble first.
import argparse, hashlib, os, struct, sys
import numpy as np
from scipy import signal

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from radio_lines import LINES

OUT_SR = 11025
VOICE = {"E": ("bm_george", "en-gb"), "D": ("am_michael", "en-us")}
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Resources", "Radio", "radio.bytes"))


# ------------------------------------------------------------------ TTS (cached)
def synth_all(models, cache):
    os.makedirs(cache, exist_ok=True)
    tts = None
    takes = {}
    for cid, spk, text, speed in LINES:
        voice, lang = VOICE[spk]
        key = hashlib.sha1(f"{voice}|{lang}|{speed}|{text}".encode()).hexdigest()[:16]
        path = os.path.join(cache, key + ".npy")
        if os.path.exists(path):
            takes[cid] = np.load(path); continue
        if tts is None:
            from kokoro_onnx import Kokoro
            tts = Kokoro(os.path.join(models, "kokoro-v1.0.onnx"), os.path.join(models, "voices-v1.0.bin"))
        s, sr = tts.create(text, voice=voice, speed=speed, lang=lang)
        assert sr == 24000, sr
        s = np.asarray(s, dtype=np.float32)
        np.save(path, s); takes[cid] = s
        print(f"  tts {cid:14s} {len(s) / sr:5.2f} s  {text}")
    return takes


# ------------------------------------------------------------------ DSP helpers
def sos_hp(f, sr, order=4): return signal.butter(order, f, "highpass", fs=sr, output="sos")
def sos_lp(f, sr, order=4): return signal.butter(order, f, "lowpass", fs=sr, output="sos")


def peaking(f0, gain_db, q, sr):
    # RBJ peaking EQ as one second-order section
    a = 10 ** (gain_db / 40); w = 2 * np.pi * f0 / sr; al = np.sin(w) / (2 * q)
    b = [1 + al * a, -2 * np.cos(w), 1 - al * a]; d = [1 + al / a, -2 * np.cos(w), 1 - al / a]
    return np.array([[b[0] / d[0], b[1] / d[0], b[2] / d[0], 1, d[1] / d[0], d[2] / d[0]]])


def trim(x, sr, pre=0.015, post=0.07):
    env = np.abs(x); k = max(1, int(0.004 * sr))
    env = np.convolve(env, np.ones(k) / k, mode="same")
    thr = 0.02 * env.max()
    on = np.where(env > thr)[0]
    if len(on) == 0: return x
    a = max(0, on[0] - int(pre * sr)); b = min(len(x), on[-1] + int(post * sr))
    return x[a:b]


def compress(x, sr, thr_db=-22.0, ratio=4.0, att=0.003, rel=0.09):
    ga = np.exp(-1 / (att * sr)); gr = np.exp(-1 / (rel * sr))
    env = 0.0; out = np.empty_like(x); thr = 10 ** (thr_db / 20)
    for i, v in enumerate(x):
        a = abs(v); env = ga * env + (1 - ga) * a if a > env else gr * env + (1 - gr) * a
        g = 1.0 if env <= thr else (thr * (env / thr) ** (1 / ratio)) / env
        out[i] = v * g
    return out


def fade(x, sr, t_in=0.004, t_out=0.012):
    n1 = min(len(x), int(t_in * sr)); n2 = min(len(x), int(t_out * sr))
    x = x.copy(); x[:n1] *= np.linspace(0, 1, n1); x[len(x) - n2:] *= np.linspace(1, 0, n2)
    return x


def engine_bed(n, sr, rng):
    # in-car rumble for the driver: a wobbling V6-ish harmonic stack and some wind
    t = np.arange(n) / sr
    f0 = 170 + 25 * np.sin(2 * np.pi * 0.7 * t + rng.uniform(0, 6)) + 10 * np.sin(2 * np.pi * 2.3 * t)
    ph = 2 * np.pi * np.cumsum(f0) / sr
    e = sum((0.6 / k) * np.sin(k * ph + rng.uniform(0, 6)) for k in range(1, 9))
    wind = signal.sosfilt(signal.butter(2, [500, 2500], "bandpass", fs=sr, output="sos"), rng.standard_normal(n))
    e = e / (np.abs(e).max() + 1e-9) + 0.35 * wind / (np.abs(wind).max() + 1e-9)
    return e.astype(np.float32)


def radio(x, sr, spk, rng):
    x = trim(x.astype(np.float64), sr)
    x = x / (np.abs(x).max() + 1e-9)
    if spk == "D": x = x + 0.07 * engine_bed(len(x), sr, rng)       # about -23 dB under the voice
    x = signal.sosfilt(sos_hp(300, sr), x)
    x = signal.sosfilt(sos_lp(3400, sr), x)
    x = signal.sosfilt(peaking(1900, 4.0, 1.0, sr), x)              # presence: the "radio honk"
    x = x / (np.abs(x).max() + 1e-9)
    x = compress(x, sr)
    x = x / (np.abs(x).max() + 1e-9)
    drive = 2.0 if spk == "E" else 2.8
    x = np.tanh(drive * x) / np.tanh(drive)                          # light distortion
    x = signal.sosfilt(sos_hp(250, sr, 2), x)
    x = signal.sosfilt(sos_lp(3800, sr, 2), x)
    x = signal.resample_poly(x, 147, 320)                            # 24000 -> 11025
    x = fade(x, OUT_SR)
    return (0.86 * x / (np.abs(x).max() + 1e-9)).astype(np.float32)


# ------------------------------------------------------------------ radio effects
def fx_clips(rng):
    sr = OUT_SR
    # hiss: band-limited noise with a slow flutter and a little crackle, 2 s, loops seamlessly (crossfaded ends)
    n = 2 * sr; extra = int(0.1 * sr)
    w = rng.standard_normal(n + extra)
    w = signal.sosfilt(signal.butter(2, [350, 3200], "bandpass", fs=sr, output="sos"), w)
    t = np.arange(n + extra) / sr
    w *= 1 + 0.25 * np.sin(2 * np.pi * 3.1 * t) * np.sin(2 * np.pi * 0.43 * t)
    crackle = (rng.random(n + extra) < 0.0015) * rng.standard_normal(n + extra) * 3
    w += signal.sosfilt(sos_lp(2500, sr, 2), crackle)
    xf = np.linspace(0, 1, extra)
    loop = w[:n].copy(); loop[:extra] = w[:extra] * xf + w[n:n + extra] * (1 - xf)
    noise = 0.5 * loop / np.abs(loop).max()

    # key-on: a click, then the two-tone chirp
    def tone(f, d, a=0.5):
        k = int(d * sr); tt = np.arange(k) / sr
        env = np.minimum(1, np.minimum(tt / 0.004, (d - tt) / 0.008))
        return a * env * (np.sin(2 * np.pi * f * tt) + 0.25 * np.sin(2 * np.pi * 2 * f * tt))
    click = np.zeros(int(0.012 * sr)); click[:24] = rng.standard_normal(24) * np.linspace(1, 0, 24)
    click = signal.sosfilt(sos_hp(600, sr, 2), click) * 0.9
    keyon = np.concatenate([click, np.zeros(int(0.02 * sr)), tone(1580, 0.055), np.zeros(int(0.012 * sr)), tone(2100, 0.07), np.zeros(int(0.05 * sr))])
    # key-off: a short burst of open squelch, then the click
    k = int(0.11 * sr)
    sq = rng.standard_normal(k) * np.exp(-np.arange(k) / (0.035 * sr))
    sq = signal.sosfilt(signal.butter(2, [600, 4000], "bandpass", fs=sr, output="sos"), sq)
    sq = 0.55 * sq / np.abs(sq).max()
    keyoff = np.concatenate([np.zeros(int(0.03 * sr)), sq, click * 0.7, np.zeros(int(0.02 * sr))])
    return {"fx_keyon": keyon.astype(np.float32), "fx_keyoff": keyoff.astype(np.float32), "fx_noise": noise.astype(np.float32)}


# ------------------------------------------------------------------ IMA ADPCM
STEP = [7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
        1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767]
IDX = [-1, -1, -1, -1, 2, 4, 6, 8]


def adpcm_encode(x):
    pcm = np.clip(np.round(x * 32767), -32768, 32767).astype(np.int32).tolist()
    pred, ix = 0, 0; nib = []
    for s in pcm:
        step = STEP[ix]; diff = s - pred; code = 0
        if diff < 0: code = 8; diff = -diff
        dq = step >> 3
        if diff >= step: code |= 4; diff -= step; dq += step
        step >>= 1
        if diff >= step: code |= 2; diff -= step; dq += step
        step >>= 1
        if diff >= step: code |= 1; dq += step
        pred = pred - dq if code & 8 else pred + dq
        pred = -32768 if pred < -32768 else 32767 if pred > 32767 else pred
        ix = min(88, max(0, ix + IDX[code & 7])); nib.append(code)
    if len(nib) & 1: nib.append(0)
    return bytes((nib[i] | (nib[i + 1] << 4)) for i in range(0, len(nib), 2))


def adpcm_decode(b, n):
    pred, ix = 0, 0; out = np.empty(n, np.float32)
    for i in range(n):
        code = (b[i >> 1] >> ((i & 1) * 4)) & 15; step = STEP[ix]
        dq = step >> 3
        if code & 4: dq += step
        if code & 2: dq += step >> 1
        if code & 1: dq += step >> 2
        pred = pred - dq if code & 8 else pred + dq
        pred = -32768 if pred < -32768 else 32767 if pred > 32767 else pred
        ix = min(88, max(0, ix + IDX[code & 7])); out[i] = pred / 32768
    return out


# ------------------------------------------------------------------ main
def write_wav(path, x, sr):
    import wave
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(sr)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", default=os.environ.get("KOKORO_DIR", os.path.join(HERE, "models")))
    ap.add_argument("--cache", default=os.path.expanduser("~/.cache/f2racing-radio"))
    ap.add_argument("--out", default=OUT)
    ap.add_argument("--preview", default=None, help="folder for WAV previews (decoded from the pack)")
    a = ap.parse_args()

    ids = [l[0] for l in LINES]
    assert len(ids) == len(set(ids)), "duplicate id"
    takes = synth_all(a.models, a.cache)
    rng = np.random.default_rng(7)
    clips = {}
    for cid, spk, text, speed in LINES:
        clips[cid] = radio(takes[cid], 24000, spk, rng)
    clips.update(fx_clips(rng))

    names = list(clips.keys()); data = bytearray(); table = []
    for nm in names:
        enc = adpcm_encode(clips[nm]); table.append((nm, len(clips[nm]), len(data))); data += enc
    head = bytearray(b"RDO1") + struct.pack("<ii", OUT_SR, len(names))
    for nm, n, off in table:
        nb = nm.encode("ascii"); head += struct.pack("<B", len(nb)) + nb + struct.pack("<ii", n, off)
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "wb") as f: f.write(head + data)
    secs = sum(len(c) for c in clips.values()) / OUT_SR
    print(f"{len(names)} clips, {secs:.1f} s of audio, {len(head) + len(data)} bytes -> {a.out}")
    longest = max(names, key=lambda k: len(clips[k]))
    print(f"longest: {longest} {len(clips[longest]) / OUT_SR:.2f} s")

    if a.preview:
        os.makedirs(a.preview, exist_ok=True)
        dec = {nm: adpcm_decode(data[off:off + (n + 1) // 2], n) for nm, n, off in table}
        for nm in names: write_wav(os.path.join(a.preview, nm + ".wav"), dec[nm], OUT_SR)
        # a demo of what the game plays: key-on, parts, key-off over the hiss (as TeamRadio.Build mixes it)
        def tx(parts, noise_lvl=0.11):
            seq = [dec["fx_keyon"]]
            for p in parts: seq += [dec[p], np.zeros(int(0.03 * OUT_SR), np.float32)]
            seq.append(dec["fx_keyoff"]); y = np.concatenate(seq)
            nz = np.resize(dec["fx_noise"], len(y)) * noise_lvl
            return 0.9 * y + nz
        gap = np.zeros(int(0.6 * OUT_SR), np.float32)
        demo = np.concatenate([tx(["lights"]), gap, tx(["p3", "gap_ahead", "n1", "s2"]), gap, tx(["box_confirm"]), gap,
                               tx(["d_tyres_gone"], 0.16), gap, tx(["win"]), gap, tx(["d_yes"], 0.16)])
        write_wav(os.path.join(a.preview, "_demo.wav"), demo, OUT_SR)
        print("previews ->", a.preview)


if __name__ == "__main__":
    main()
