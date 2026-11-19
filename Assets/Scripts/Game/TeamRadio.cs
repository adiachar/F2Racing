// TeamRadio.cs — F1-style team radio. The race engineer talks to the player during a race (start, position, gaps,
// laps to go, purple sectors, tyres, battery, pit calls, flags, damage, rain, the result) and now and then the
// player's driver answers ("Copy", "Tyres are gone!", "Yes! Get in there!").
//  * Voices: Assets/Resources/Radio/radio.bytes, made offline by Tools/Voice/make_radio.py (Kokoro neural TTS, already
//    band-passed, compressed and lightly distorted like a radio; 11025 Hz IMA ADPCM, ~1.1 MB). Decoded here, so it
//    works the same on Android and WebGL (Unity cannot decode Ogg from bytes at runtime).
//  * A transmission = key-on chirp, the voice clips (messages are built from parts: "P3" + "gap to the car ahead" +
//    "one point" + "two seconds"), key-off squelch, all over one continuous hiss. It is mixed once into a reusable
//    buffer and played from one AudioClip: no filters on the phone, nothing allocated per frame.
//  * Queue with priorities (safety > pit > race > info), never two at once, a quiet time of 15 s after each message
//    for anything not urgent, stale messages dropped, gap calls every 2-3 laps.
//  * Settings: PlayerPrefs int "teamRadio": 0 full, 1 key info only (start, pit, flags, final lap, result, overtake),
//    2 off. Muted with the game's sound; follows the master volume; ducks the engine a little while talking.
//  * A small caption above the dash while a line plays ("ENGINEER  Box, box.").
// Attaches itself (no change to GameManager.cs): it finds the GameManager and reads the race through
// GameManager.TeamRadioRead (the small partial at the end of this file), read only.
using System.Collections.Generic;
using FormulaSim;
using UnityEngine;

public class TeamRadio : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (FindFirstObjectByType<TeamRadio>() == null) new GameObject("TeamRadio").AddComponent<TeamRadio>(); }

    public const string PrefKey = "teamRadio";
    public const int ModeFull = 0, ModeKey = 1, ModeOff = 2;
    public static readonly string[] ModeNames = { "Full", "Key info", "Off" };

    // ---------------------------------------------------------------- the race, as GameManager shows it (read only)
    public sealed class View
    {
        public bool paused, results, race, sound = true;
        public EngineAudio audio;
        public int laps, lap, idx, pitStops, rcSlot = -1, tyreSet;
        public double raceTime, countdown, simTime, pitLoss, refLap = 90, lapStart = double.NaN, lastLap = double.NaN;
        public Track track; public FormulaCar car; public List<AiDriver> rivals;
        public bool cut, boxArmed, pitLane, jumped, seasonRound;
        public PitRun pitRun; public RaceControl rc;
        public int penSerial, penSecs;   // the game's own time penalties (GameManagerProgress.cs): count, last one's seconds
    }

    // ---------------------------------------------------------------- clips (names as in the pack, Tools/Voice/radio_lines.py)
    enum C
    {
        radio_check, lights, good_start,
        p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19, p20, p21, p22,
        good_move, lost_place, leading, gap_ahead, gap_behind,
        n0, n1, n2, n3, n4, n5, n6, n7, n8, n9, s0, s1, s2, s3, s4, s5, s6, s7, s8, s9,
        catching, pulling_away, defend, within_one, overtake, boost,
        laps20, laps15, laps10, laps5, laps3, laps2, halfway, last_lap, final_lap,
        fastest_lap, purple_sector, pb, good_pace,
        fronts_cold, rears_cold, fronts_hot, rears_hot, tyres_window, wear_mid, wear_high, batt_low,
        box, box_confirm, stay_out, pit_ready, limiter, good_stop, box_inters, box_wets, box_slicks, box_wing, puncture, cheap_stop,
        track_limits, tl_last, penalty, jump_start, yellow, sc, vsc, vsc_ending, sc_in, green, blue,
        damage_seen, are_you_ok, no_worries, rain, rain_heavy, copy,
        win, champion, podium, points, consolation,
        d_copy, d_understood, d_confirm, d_loud_clear, d_tyres_gone, d_no_grip, d_yes, d_sorry, d_ok, d_damage, d_thanks, d_rain,
        fx_keyon, fx_keyoff, fx_noise,
        Count
    }
    const int MaxPos = 22;
    static readonly string[] PosText = new string[MaxPos + 1];

    // ---------------------------------------------------------------- the pack (IMA ADPCM, see make_radio.py)
    const int Rate = 22050, MaxLen = Rate * 14;
    byte[] data; int dataStart, srcRate = 11025;
    readonly int[] clipAt = new int[(int)C.Count], clipLen = new int[(int)C.Count];
    static readonly int[] StepTab = {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
        1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 };
    static readonly int[] IdxTab = { -1, -1, -1, -1, 2, 4, 6, 8 };
    // half-band interpolator (8 taps, Hann-windowed sinc) for the 2x upsampling 11025 -> 22050
    const float H0 = 0.6105f, H1 = -0.1461f, H2 = 0.0392f, H3 = -0.0035f;
    const int Pad = 4;
    float[] dec, mix, noise; int mixUsed;
    AudioSource src; AudioClip clip;

    bool LoadPack()
    {
        var ta = Resources.Load<TextAsset>("Radio/radio");
        if (ta == null) return false;
        var b = ta.bytes; Resources.UnloadAsset(ta);
        if (b.Length < 12 || b[0] != 'R' || b[1] != 'D' || b[2] != 'O' || b[3] != '1') return false;
        srcRate = System.BitConverter.ToInt32(b, 4); int count = System.BitConverter.ToInt32(b, 8), p = 12;
        var at = new Dictionary<string, int>(); var len = new Dictionary<string, int>();
        for (int i = 0; i < count; i++)
        {
            int nl = b[p++]; string name = System.Text.Encoding.ASCII.GetString(b, p, nl); p += nl;
            len[name] = System.BitConverter.ToInt32(b, p); at[name] = System.BitConverter.ToInt32(b, p + 4); p += 8;
        }
        dataStart = p; data = b;
        int longest = 0;
        for (int c = 0; c < (int)C.Count; c++)
        {
            string name = ((C)c).ToString();
            if (!at.TryGetValue(name, out clipAt[c])) { clipAt[c] = -1; clipLen[c] = 0; Debug.LogWarning("TeamRadio: no clip " + name); continue; }
            clipLen[c] = len[name]; if (clipLen[c] > longest) longest = clipLen[c];
        }
        dec = new float[longest + 2 * Pad];
        mix = new float[MaxLen];
        // the hiss, upsampled once
        int nn = Decode((int)C.fx_noise);
        noise = new float[System.Math.Max(2, 2 * nn)];
        Up(noise, 0, nn, 1f);
        return true;
    }

    // one clip into dec[Pad ..], zero padded both sides; returns the sample count (source rate)
    int Decode(int c)
    {
        int n = clipLen[c]; if (n <= 0 || clipAt[c] < 0) return 0;
        int off = dataStart + clipAt[c], pred = 0, ix = 0;
        for (int i = 0; i < Pad; i++) dec[i] = 0f;
        for (int i = 0; i < n; i++)
        {
            int code = (data[off + (i >> 1)] >> ((i & 1) << 2)) & 15;
            int step = StepTab[ix], dq = step >> 3;
            if ((code & 4) != 0) dq += step;
            if ((code & 2) != 0) dq += step >> 1;
            if ((code & 1) != 0) dq += step >> 2;
            pred = (code & 8) != 0 ? pred - dq : pred + dq;
            if (pred > 32767) pred = 32767; else if (pred < -32768) pred = -32768;
            ix += IdxTab[code & 7]; if (ix < 0) ix = 0; else if (ix > 88) ix = 88;
            dec[Pad + i] = pred * (1f / 32768f);
        }
        for (int i = 0; i < Pad; i++) dec[Pad + n + i] = 0f;
        return n;
    }

    // add dec (n samples at the source rate) to dst from sample 'at', upsampled 2x
    void Up(float[] dst, int at, int n, float g)
    {
        int lim = dst.Length - 1;
        for (int i = 0; i < n; i++)
        {
            int o = at + 2 * i; if (o >= lim) break;
            int k = Pad + i;
            dst[o] += g * dec[k];
            dst[o + 1] += g * (H0 * (dec[k] + dec[k + 1]) + H1 * (dec[k - 1] + dec[k + 2]) + H2 * (dec[k - 2] + dec[k + 3]) + H3 * (dec[k - 3] + dec[k + 4]));
        }
    }

    int Put(C c, int at, float g) { int n = Decode((int)c); Up(mix, at, n, g); return at + 2 * n; }

    // ---------------------------------------------------------------- messages
    const int PSafety = 0, PPit = 1, PRace = 2, PInfo = 3;
    const int TNone = 0, TPos = 1, TGap = 2, TTyre = 3, TFlag = 4, TLap = 5, TPit = 6, TTiming = 7, TBatt = 8;
    const int PauseTok = -1;
    const float Quiet = 15f, MinGap = 0.8f, ReplyGap = 0.35f;

    sealed class Msg
    {
        public readonly int[] parts = new int[12]; public int n;
        public int prio, topic; public bool key, urgent, driver, reply; public float life = 10f, expires, notBefore;
        public string text; public Msg next;
        public Msg A(C c) { if (n < parts.Length) parts[n++] = (int)c; return this; }
        public Msg Pause() { if (n < parts.Length) parts[n++] = PauseTok; return this; }
        public Msg Then(Msg m) { var t = this; while (t.next != null) t = t.next; t.next = m; m.reply = true; return this; }
    }
    static Msg E(int prio, bool key, bool urgent, string text, int topic = TNone, float life = 10f) =>
        new Msg { prio = prio, key = key, urgent = urgent, text = text, topic = topic, life = life };
    static Msg D(string text, C c) => new Msg { prio = PRace, driver = true, urgent = true, text = text, life = 6f }.A(c);

    readonly List<Msg> pending = new List<Msg>(8);
    Msg cur; bool playing; float playEnd, lastEnd = -100f, clock;
    string capText, capFor; bool capDriver; float capStart, capUntil = -1f, capW, capSize = 9f;

    void Post(Msg m)
    {
        if (m == null || mode == ModeOff || data == null) return;
        for (var c = m.next; c != null; c = c.next) { m.key |= c.key; if (c.prio < m.prio) m.prio = c.prio; } // a driver line leading an engineer call
        if (mode == ModeKey && !m.key) return;
        if (m.topic != TNone) for (int i = pending.Count - 1; i >= 0; i--) if (pending[i].topic == m.topic) pending.RemoveAt(i);
        if (pending.Count >= 8)
        {
            int worst = 0; for (int i = 1; i < pending.Count; i++) if (pending[i].prio >= pending[worst].prio) worst = i;
            if (pending[worst].prio < m.prio) return;
            pending.RemoveAt(worst);
        }
        m.expires = clock + m.life;
        pending.Add(m);
    }

    void Pump()
    {
        if (playing)
        {
            if (clock < playEnd) return;
            playing = false; lastEnd = clock; src.Stop();
            if (cur != null && cur.next != null) { var r = cur.next; r.notBefore = clock + ReplyGap; r.expires = clock + 4f; pending.Insert(0, r); }
            cur = null;
        }
        for (int i = pending.Count - 1; i >= 0; i--) if (pending[i].expires < clock) pending.RemoveAt(i);
        int best = -1;
        for (int i = 0; i < pending.Count; i++)
        {
            var m = pending[i];
            if (clock < m.notBefore) continue;
            if (!m.reply && clock < lastEnd + (m.urgent ? MinGap : Quiet)) continue;
            if (best < 0 || Rank(m) < Rank(pending[best])) best = i;
        }
        if (best < 0) return;
        var p = pending[best]; pending.RemoveAt(best);
        Play(p);
    }

    static int Rank(Msg m) => m.reply ? -1 : m.prio;

    void Play(Msg m)
    {
        System.Array.Clear(mix, 0, System.Math.Min(mixUsed + 4, mix.Length));
        int at = Put(C.fx_keyon, 0, 0.5f), gap = Rate / 100;
        for (int i = 0; i < m.n; i++)
        {
            int c = m.parts[i];
            if (c == PauseTok) { at += Rate * 15 / 100; continue; }
            at = Put((C)c, at, 0.92f) + gap;
            if (at >= MaxLen - Rate) break;
        }
        at = Put(C.fx_keyoff, at, 0.8f);
        int len = System.Math.Min(at, MaxLen);
        // the hiss under the whole transmission (the driver is in a racing car: more of it)
        float lvl = m.driver ? 0.17f : 0.11f; int fin = Rate / 60, fout = Rate / 25, nl = noise.Length;
        for (int i = 0, j = 0; i < len; i++)
        {
            float e = i < fin ? i / (float)fin : i > len - fout ? (len - i) / (float)fout : 1f;
            mix[i] += lvl * e * noise[j];
            if (++j >= nl) j = 0;
        }
        mixUsed = len;
        clip.SetData(mix, 0);
        src.clip = clip; src.time = 0f; src.Play();
        playing = true; cur = m; playEnd = clock + len / (float)Rate + 0.05f;
        capText = m.text; capDriver = m.driver; capStart = clock; capUntil = playEnd + 0.7f;
    }

    void StopAll()
    {
        pending.Clear(); cur = null;
        if (playing) { src.Stop(); playing = false; lastEnd = clock; }
        capUntil = -1f;
    }

    // ---------------------------------------------------------------- engine ducking (EngineAudio.engineVol, restored after)
    const float DuckTo = 0.62f;
    float duckK = 1f, duckBase, duckSet; bool ducking;
    void Duck(bool on, float dt)
    {
        var ae = view.audio; if (ae == null) { ducking = false; duckK = 1f; return; }
        duckK = Mathf.MoveTowards(duckK, on ? DuckTo : 1f, dt * (on ? 3f : 1.2f));
        if (!ducking && duckK >= 0.999f) return;
        float now = ae.engineVol;
        if (!ducking) { duckBase = now; ducking = true; }
        else if (Mathf.Abs(now - duckSet) > 1e-4f) duckBase = now;   // the settings (or the pre-race intro) set a new level
        if (duckK >= 0.999f) { ae.engineVol = duckBase; ducking = false; return; }
        duckSet = duckBase * duckK; ae.engineVol = duckSet;
    }

    // ---------------------------------------------------------------- setup
    GameManager gm; readonly View view = new View(); float findT;
    RadioCaption caption; int mode = ModeFull; float prefT; bool srcPaused;
    System.Random rng = new System.Random(1);

    void Awake()
    {
        for (int i = 1; i <= MaxPos; i++) PosText[i] = "P" + i;
        if (!LoadPack()) { Debug.LogWarning("TeamRadio: Resources/Radio/radio.bytes missing"); enabled = false; return; }
        src = gameObject.AddComponent<AudioSource>();
        src.playOnAwake = false; src.loop = false; src.spatialBlend = 0f; src.dopplerLevel = 0f; src.priority = 0; src.bypassReverbZones = true;
        clip = AudioClip.Create("TeamRadio", MaxLen, 1, Rate, false);
        caption = gameObject.AddComponent<RadioCaption>(); caption.owner = this; caption.enabled = false;
        ReadPrefs();
    }

    void ReadPrefs() { mode = Mathf.Clamp(PlayerPrefs.GetInt(PrefKey, ModeFull), 0, 2); }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        if (gm == null)
        {
            findT -= dt; if (findT > 0f) return;
            findT = 1f; gm = FindFirstObjectByType<GameManager>(); if (gm == null) return;
        }
        view.race = false; view.audio = null;
        gm.TeamRadioRead(view);
        bool paused = view.paused && !view.results;
        if (paused != srcPaused) { srcPaused = paused; if (playing) { if (paused) src.Pause(); else src.UnPause(); } if (!paused) ReadPrefs(); }
        bool showCap = !view.paused && capText != null && clock < capUntil;
        if (caption.enabled != showCap) caption.enabled = showCap;
        if (paused) { Duck(false, dt); return; }
        clock += dt;
        if ((prefT -= dt) <= 0f) { prefT = 2f; ReadPrefs(); }

        var ae = view.audio;
        src.volume = ae != null ? Mathf.Clamp01(ae.masterVol) * 0.95f : 0.95f;
        bool mute = !view.sound || (ae != null && ae.muted);
        if (!view.race || mode == ModeOff || mute)
        {
            if (playing || pending.Count > 0) StopAll();
            active = false; // back on: start watching afresh (no stale events)
            Duck(false, dt); return;
        }
        if (!active || view.raceTime < prevRaceTime - 0.25) { StopAll(); ResetSession(); active = true; }
        prevRaceTime = view.raceTime;
        if (!finished) Watch(dt);
        Pump();
        Duck(playing, dt);
    }

    // ---------------------------------------------------------------- race watching
    bool active, finished; double prevRaceTime;
    bool saidCheck, saidLights, saidStart, saidJump, seasonRound;
    int prevLap, prevPitStops, prevWeather, prevSerial, prevPenSerial, prevGamePen, gridPos, posNow, posCand, posSaid, nextGapLap, overtakeLap = -10, defendLap = -10, pbLap = -10, purpleLap = -10, tyreCallLap = -10;
    float posCandT, coldFT, coldRT, hotFT, hotRT, battT, defendT, hotSaidAt = -999f, battSaidAt = -999f, tlAt = -1f, tlSaidAt = -999f, teleportAt = -999f, blueSaidAt = -999f, yellowSaidAt = -999f, damageSaidAt = -999f, okSaidAt = -999f, tyreSuitAt = -999f, pitOutAt = -999f;
    bool coldFSaid, coldRSaid, windowSaid, wearMidSaid, wearHighSaid, boxPrev, inPitPrev, cutPrev, bluePrev, yellowPrev, neutralPrev, gapPending, damageNeedSaid;
    int damageHits; double lastX = double.NaN, lastY, lastGapAhead = double.NaN, lastGapBehind = double.NaN;
    // timing (own copies of the HUD's loops, see Sectors.cs): gaps in seconds, purple sectors, fastest lap
    SectorTimer secT; Track secFor; readonly CarTiming me = new CarTiming(); CarTiming[] riv = new CarTiming[0];
    double fastest = double.NaN; int lapsTimed;

    void ResetSession()
    {
        rng = new System.Random(System.Environment.TickCount);
        finished = saidCheck = saidLights = saidStart = saidJump = false;
        prevLap = view.lap; prevPitStops = view.pitStops; prevWeather = Weather.Index;
        prevSerial = view.rc != null ? view.rc.serial : 0; prevPenSerial = view.rc != null ? view.rc.penaltySerial : 0; prevGamePen = view.penSerial;
        gridPos = posNow = posCand = posSaid = Position(); posCandT = clock;
        nextGapLap = 2 + rng.Next(0, 2); overtakeLap = defendLap = pbLap = purpleLap = tyreCallLap = -10;
        coldFT = coldRT = hotFT = hotRT = battT = defendT = 0f;
        hotSaidAt = battSaidAt = tlSaidAt = teleportAt = blueSaidAt = yellowSaidAt = damageSaidAt = okSaidAt = tyreSuitAt = pitOutAt = -999f; tlAt = -1f;
        coldFSaid = coldRSaid = windowSaid = wearMidSaid = wearHighSaid = damageNeedSaid = gapPending = false;
        boxPrev = view.boxArmed; inPitPrev = view.pitRun != null; cutPrev = view.cut; bluePrev = yellowPrev = neutralPrev = false;
        damageHits = view.car != null ? view.car.damage.hits : 0;
        lastX = double.NaN; lastGapAhead = lastGapBehind = double.NaN;
        secT = null; secFor = null; me.Clear(); foreach (var r in riv) r.Clear(); fastest = double.NaN; lapsTimed = 0;
    }

    double Progress() => view.lap + (view.idx < 0 ? 0 : (double)view.idx / view.track.N) - view.pitLoss / view.refLap;
    int Position()
    {
        if (view.rivals == null || view.track == null) return 1;
        double mine = Progress(); int pos = 1;
        foreach (var d in view.rivals) if (d.progress > mine) pos++;
        return pos;
    }
    bool Chance(double p) => rng.NextDouble() < p;
    static C PosClip(int p) => (C)((int)C.p1 + Mathf.Clamp(p, 1, MaxPos) - 1);
    static string Pos(int p) => p >= 1 && p <= MaxPos ? PosText[p] : "P" + p;

    // seconds to the car ahead / behind on the timing loops (as the HUD's interval); false if not known or lapped
    bool GapAhead(out double g)
    {
        g = 0; var rv = view.rivals; double mine = Progress(); int a = -1;
        for (int i = 0; i < rv.Count; i++) if (rv[i].progress > mine && (a < 0 || rv[i].progress < rv[a].progress)) a = i;
        if (a < 0 || a >= riv.Length || rv[a].progress - mine >= 1) return false;
        int key = me.lastKey;
        if (!me.TimeAt(key, out double tMe) || !riv[a].TimeAt(key, out double tA)) return false;
        g = tMe + view.pitLoss - tA; return g >= 0;
    }
    bool GapBehind(out double g)
    {
        g = 0; var rv = view.rivals; double mine = Progress(); int b = -1;
        for (int i = 0; i < rv.Count; i++) if (rv[i].progress < mine && (b < 0 || rv[i].progress > rv[b].progress)) b = i;
        if (b < 0 || b >= riv.Length || mine - rv[b].progress >= 1) return false;
        int key = riv[b].lastKey;
        if (!riv[b].TimeAt(key, out double tB) || !me.TimeAt(key, out double tMe)) return false;
        g = tB - tMe - view.pitLoss; return g >= 0;
    }
    // "one point two seconds" as two parts; false above 9.9 s
    static bool Num(Msg m, double s)
    {
        int k = (int)System.Math.Round(s * 10); if (k < 0 || k > 99) return false;
        m.A((C)((int)C.n0 + k / 10)).A((C)((int)C.s0 + k % 10)); return true;
    }
    static string Sec(double s) => (System.Math.Round(s * 10) / 10).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";

    void Timing()
    {
        var t = view.track; if (t == null || view.idx < 0) return;
        if (secT == null || secFor != t) { secT = new SectorTimer(t); secFor = t; me.Clear(); foreach (var r in riv) r.Clear(); }
        var rv = view.rivals;
        if (riv.Length != rv.Count) { riv = new CarTiming[rv.Count]; for (int i = 0; i < riv.Length; i++) riv[i] = new CarTiming(); }
        bool started = view.raceTime >= view.countdown;
        me.invalid |= view.cut;
        secT.Update(me, view.idx, view.simTime, view.lapStart, view.lastLap, null);
        if (started) me.Loop(view.lap, view.idx, t.N, view.simTime);
        bool mineFastest = false;
        if (!double.IsNaN(me.evLap) && me.evLapValid)
        {
            if (double.IsNaN(fastest) || me.evLap < fastest) { mineFastest = lapsTimed > 0 && rv.Count > 0; fastest = me.evLap; }
            lapsTimed++;
        }
        for (int i = 0; i < rv.Count; i++)
        {
            var d = rv[i]; var r = riv[i];
            secT.Update(r, d.idx, view.simTime, d.lapStart, d.lastLap, null);
            if (!double.IsNaN(r.evLap)) { if (double.IsNaN(fastest) || r.evLap < fastest) fastest = r.evLap; lapsTimed++; }
            if (started && d.idx >= 0) r.Loop(d.lap, d.idx, t.N, view.simTime);
        }
        if (Neutral()) return;
        // fastest lap of the race / personal best
        if (mineFastest) Post(E(PInfo, false, true, "Fastest lap! Purple, purple.", TTiming, 8f).A(C.fastest_lap));
        else if (!double.IsNaN(me.evLap) && me.evLapPb && view.lap >= 3 && view.lap - pbLap >= 3)
        {
            pbLap = view.lap;
            Post(Chance(0.5) ? E(PInfo, false, false, "Personal best. Nice lap.", TTiming, 8f).A(C.pb) : E(PInfo, false, false, "Good pace. Keep it up.", TTiming, 8f).A(C.good_pace));
        }
        // purple sector (sectors 1 and 2; the third ends with the lap)
        if ((me.evSector == 0 || me.evSector == 1) && me.curCol[me.evSector] == 3 && view.lap >= 2 && view.lap - purpleLap >= 2)
        {
            purpleLap = view.lap;
            Post(E(PInfo, false, false, "Purple sector.", TTiming, 6f).A(C.purple_sector));
        }
    }

    bool Neutral() => view.rc != null && view.rc.Neutral;

    float tick4;
    void Watch(float dt)
    {
        var car = view.car; var t = view.track;
        double rt = view.raceTime, cd = view.countdown;
        bool started = rt >= cd, racing = rt > cd + 2;
        int lap = view.lap, laps = view.laps, left = laps - lap; // left: laps after this one
        bool inPit = view.pitRun != null;

        // ---- the finish: the result (the championship is already updated by now: use the flag from the race)
        if (lap > laps && started)
        {
            finished = true; Result(); return;
        }
        seasonRound = view.seasonRound;

        // ---- before the start and the start
        if (!saidCheck && mode == ModeFull && rt > 0.25 && rt < cd - 4.5)
        {
            saidCheck = true;
            Post(E(PRace, false, true, "Radio check.", TNone, 2f).A(C.radio_check).Then(D("Loud and clear.", C.d_loud_clear)));
        }
        if (!saidLights && started)
        {
            saidLights = true; gridPos = posNow = posCand = posSaid = Position(); posCandT = clock;
            if (rt - cd < 1.5) Post(E(PRace, true, true, "Lights out, good luck.", TNone, 1.5f).A(C.lights));
        }
        if (!started) { Timing(); return; }
        if (view.jumped && !saidJump)
        {
            saidJump = true;
            var j = E(PSafety, true, true, "Jump start. Five second penalty.", TNone, 12f).A(C.jump_start); j.notBefore = clock + 2.5f; // after "lights out"
            Post(j);
        }

        Timing();

        // ---- recovered (put back on the track): a jump of more than 30 m between two frames, not in the pit lane
        if (!double.IsNaN(lastX) && !inPit && !inPitPrev)
        {
            double dx = car.x - lastX, dy = car.y - lastY;
            if (dx * dx + dy * dy > 900)
            {
                teleportAt = clock;
                if (clock - okSaidAt > 40f && mode == ModeFull)
                {
                    okSaidAt = clock;
                    Post(E(PSafety, false, true, "Are you okay?", TNone, 5f).A(C.are_you_ok).Then(D("Yeah, I'm okay.", C.d_ok)));
                }
            }
        }
        lastX = car.x; lastY = car.y;

        // ---- flags (race control)
        var rc = view.rc;
        if (rc != null)
        {
            if (rc.serial != prevSerial)
            {
                prevSerial = rc.serial;
                switch (rc.flag)
                {
                    case Flag.Sc:
                    {
                        var m = E(PSafety, true, true, "Safety car, safety car.", TFlag, 8f).A(C.sc);
                        double w = System.Math.Max(car.wearF, car.wearR);
                        if (view.pitLane && left >= 3 && !inPit && !view.boxArmed)
                        {
                            if (w > 0.35) { m.Pause().A(C.cheap_stop); m.text = "Safety car, safety car. Box, box. Cheap stop under the safety car."; }
                            else { m.Pause().A(C.stay_out); m.text = "Safety car, safety car. Stay out, stay out."; }
                        }
                        Post(m); break;
                    }
                    case Flag.Vsc: Post(E(PSafety, true, true, "VSC, VSC. Keep to the delta.", TFlag, 8f).A(C.vsc)); break;
                    case Flag.VscEnding: Post(E(PSafety, true, true, "VSC ending. Get ready.", TFlag, 5f).A(C.vsc_ending)); break;
                    case Flag.ScIn: Post(E(PSafety, true, true, "Safety car in this lap.", TFlag, 10f).A(C.sc_in)); break;
                    case Flag.Green: if (neutralPrev) Post(E(PSafety, true, true, "Green flag, green flag. Go, go!", TFlag, 5f).A(C.green)); break;
                }
                neutralPrev = rc.Neutral || rc.flag == Flag.Yellow && neutralPrev;
            }
            if (rc.penaltySerial != prevPenSerial)
            {
                prevPenSerial = rc.penaltySerial;
                var m = E(PSafety, true, true, "We have a five second penalty.", TNone, 10f).A(C.penalty);
                if (mode == ModeFull && Chance(0.5)) m.Then(D("Ugh, sorry guys.", C.d_sorry));
                Post(m);
            }
            bool blue = view.rcSlot >= 0 && rc.Blue(view.rcSlot);
            if (blue && !bluePrev && clock - blueSaidAt > 45f) { blueSaidAt = clock; Post(E(PSafety, true, true, "Blue flags. Let him through.", TNone, 6f).A(C.blue)); }
            bluePrev = blue;
        }

        // the game's own penalties (collision, place not given back): the clip says five seconds, so only those
        if (view.penSerial != prevGamePen)
        {
            prevGamePen = view.penSerial;
            if (view.penSecs == 5) Post(E(PSafety, true, true, "We have a five second penalty.", TNone, 10f).A(C.penalty));
        }

        // ---- track limits (read the strike a moment later: race control counts it in the same frame)
        if (view.cut && !cutPrev && !double.IsNaN(view.lapStart) && racing && !inPit) tlAt = clock + 0.6f;
        cutPrev = view.cut;
        if (tlAt > 0f && clock >= tlAt)
        {
            tlAt = -1f;
            if (clock - teleportAt > 2f && clock - tlSaidAt > 20f)
            {
                tlSaidAt = clock;
                int strikes = rc != null ? rc.strikes : 0;
                var m = strikes >= 3 ? E(PSafety, true, true, "That's your last warning for track limits.", TNone, 8f).A(C.tl_last)
                                     : E(PSafety, true, true, "Track limits. Watch the white lines.", TNone, 8f).A(C.track_limits);
                if (mode == ModeFull && Chance(0.35)) m.Then(D("Copy.", C.d_copy));
                Post(m);
            }
        }

        // ---- pit: box armed, the lane, the stop
        if (view.boxArmed && !boxPrev && !inPit)
        {
            if (left <= 0) Post(E(PPit, true, true, "Stay out, stay out.", TPit, 5f).A(C.stay_out));
            else Post(E(PPit, true, true, "Copy. We're ready for you.", TPit, 6f).A(C.pit_ready));
        }
        boxPrev = view.boxArmed;
        if (inPit && !inPitPrev) Post(E(PPit, true, true, "Pit limiter, pit limiter.", TPit, 4f).A(C.limiter));
        if (!inPit && inPitPrev) { pitOutAt = clock; Post(E(PPit, true, true, "Good stop. Push now, push.", TPit, 6f).A(C.good_stop)); }
        if (view.pitStops != prevPitStops)
        {
            prevPitStops = view.pitStops;
            if (!view.pitLane) { pitOutAt = clock; Post(E(PPit, true, true, "Good stop. Push now, push.", TPit, 6f).A(C.good_stop)); }
            coldFSaid = coldRSaid = windowSaid = wearMidSaid = wearHighSaid = damageNeedSaid = false; coldFT = coldRT = 0f;
        }
        inPitPrev = inPit;

        // ---- damage
        if (car.cfg.damage && car.damage.hits != damageHits)
        {
            damageHits = car.damage.hits;
            var dm = car.damage;
            if (dm.NeedsPit && !damageNeedSaid && left >= 1 && !inPit && !view.boxArmed)
            {
                damageNeedSaid = true;
                if (dm.Punctured) Post(E(PSafety, true, true, "You have a puncture. Box, box.", TPit, 8f).A(C.puncture));
                else Post(D("I think I've got damage.", C.d_damage).Then(E(PSafety, true, true, "Box, box. We'll change the wing.", TPit).A(C.box_wing)));
            }
            else if (dm.lastSize > 0.25 && clock - damageSaidAt > 60f && mode == ModeFull)
            {
                damageSaidAt = clock;
                Post(D("I think I've got damage.", C.d_damage).Then(E(PSafety, false, true, "Copy, we can see the damage.", TNone).A(C.damage_seen)));
            }
        }

        // ---- weather
        if (Weather.Index != prevWeather)
        {
            bool wetter = Weather.Index > prevWeather; prevWeather = Weather.Index;
            if (wetter)
            {
                var e = Weather.Index >= 2 ? E(PSafety, true, true, "It's raining hard now.", TNone).A(C.rain_heavy) : E(PSafety, true, true, "Rain is starting. Take care.", TNone).A(C.rain);
                Post(mode == ModeFull ? D("It's starting to rain.", C.d_rain).Then(e) : e);
            }
            tyreSuitAt = clock + 6f;
        }

        // ---- lap events
        if (lap != prevLap)
        {
            bool fwd = lap > prevLap; prevLap = lap;
            if (fwd && lap >= 2) LapEvents(lap, laps);
        }
        if (gapPending && t != null && view.idx > t.N / 4 && view.idx < t.N / 2) { gapPending = false; GapCall(); }

        // ---- the slower checks, 4 times a second
        if ((tick4 -= dt) > 0f) return;
        tick4 = 0.25f; const float T = 0.25f;

        // position
        posNow = Position();
        if (posNow != posCand) { posCand = posNow; posCandT = clock; }
        if (posCand != posSaid && clock - posCandT > 2.5f && rt > cd + 12)
        {
            bool gained = posCand < posSaid; int lost = posCand - posSaid;
            bool pitShuffle = inPit || clock - pitOutAt < 20f;
            posSaid = posCand;
            bool rivalPitting = false;
            foreach (var d in view.rivals) if (d.InPit) { rivalPitting = true; break; }
            if (gained && !pitShuffle && !Neutral() && rivalPitting && posCand > 1) Post(E(PRace, false, false, Pos(posCand) + ".", TPos, 6f).A(PosClip(posCand)));
            else if (gained && !pitShuffle && !Neutral())
            {
                if (posCand == 1)
                {
                    var m = E(PRace, true, false, "You're leading the race!", TPos, 6f).A(C.leading);
                    if (mode == ModeFull && Chance(0.6)) m.Then(D("Yes! Get in there!", C.d_yes));
                    Post(m);
                }
                else Post(E(PRace, true, false, "Good move. " + Pos(posCand) + ".", TPos, 6f).A(C.good_move).A(PosClip(posCand)));
            }
            else if (!gained && !pitShuffle)
            {
                if (lost >= 3 && mode == ModeFull) Post(D("Ugh, sorry guys.", C.d_sorry).Then(E(PRace, false, true, "No worries. We go again.", TPos).A(C.no_worries)));
                else Post(E(PRace, false, false, Pos(posCand) + ". Lost a place. Keep your head down.", TPos, 6f).A(PosClip(posCand)).A(C.lost_place));
            }
        }
        if (!saidStart && rt > cd + 9)
        {
            saidStart = true;
            if (posNow <= gridPos - 2 && mode == ModeFull) Post(E(PRace, false, false, "Good start, good start. " + Pos(posNow) + ".", TPos, 6f).A(C.good_start).A(PosClip(posNow)));
        }

        // yellow flag: a stopped car up to 400 m ahead (as the HUD)
        if (t != null && view.idx >= 0 && rt > cd + 4)
        {
            bool y = false; var rv = view.rivals;
            for (int i = 0; i < rv.Count && !y; i++)
            {
                var d = rv[i]; if (d.idx < 0 || d.InPit) continue;
                double ahead = ((d.idx - view.idx + t.N) % t.N) * t.ds;
                if (d.car.vx * d.car.vx + d.car.vy * d.car.vy < 36 && ahead > 5 && ahead < 400) y = true;
            }
            if (y && !yellowPrev && clock - yellowSaidAt > 20f && !Neutral()) { yellowSaidAt = clock; Post(E(PSafety, true, true, "Yellow flag ahead. Careful.", TNone, 5f).A(C.yellow)); }
            yellowPrev = y;
        }

        if (!racing || inPit || Neutral()) return;
        double speed = System.Math.Sqrt(car.vx * car.vx + car.vy * car.vy);

        // car behind close: defend
        if (GapBehind(out double gb) && gb < 0.5) defendT += T; else defendT = 0f;
        if (defendT > 4f && lap - defendLap >= 3 && mode == ModeFull) { defendLap = lap; defendT = 0f; Post(E(PRace, false, false, "Car behind is close. Defend.", TGap, 6f).A(C.defend)); }

        // tyre temperatures (after the first half minute: the blankets' heat and the start)
        var cfg = car.cfg;
        if (rt > cd + 30 && !double.IsNaN(car.tyreTF))
        {
            double opt = cfg.tyreOpt, win = cfg.tyreWindow;
            bool fast = speed > 30;
            coldFT = car.tyreTF < opt - 0.55 * win ? (fast ? coldFT + T : coldFT) : car.tyreTF > opt - 0.35 * win ? 0f : coldFT;
            coldRT = car.tyreTR < opt - 0.55 * win ? (fast ? coldRT + T : coldRT) : car.tyreTR > opt - 0.35 * win ? 0f : coldRT;
            hotFT = car.tyreTF > opt + 0.6 * win ? hotFT + T : car.tyreTF < opt + 0.3 * win ? 0f : hotFT;
            hotRT = car.tyreTR > opt + 0.6 * win ? hotRT + T : car.tyreTR < opt + 0.3 * win ? 0f : hotRT;
            if (coldFT > 8f && !coldFSaid) { coldFSaid = true; var m = E(PInfo, false, false, "Fronts are cold, push to bring them in.", TTyre, 10f).A(C.fronts_cold); if (Chance(0.4)) m.Then(D("Copy.", C.d_copy)); Post(m); }
            else if (coldRT > 8f && !coldRSaid) { coldRSaid = true; Post(E(PInfo, false, false, "Rears are cold. Careful on traction.", TTyre, 10f).A(C.rears_cold)); }
            else if (hotRT > 6f && clock - hotSaidAt > 150f) { hotSaidAt = clock; var m = E(PInfo, false, false, "Rears overheating, manage them.", TTyre, 10f).A(C.rears_hot); if (Chance(0.4)) m.Then(D("Understood.", C.d_understood)); Post(m); }
            else if (hotFT > 6f && clock - hotSaidAt > 150f) { hotSaidAt = clock; Post(E(PInfo, false, false, "Fronts overheating. Take care in the slow corners.", TTyre, 10f).A(C.fronts_hot)); }
            if ((coldFSaid || coldRSaid) && !windowSaid && System.Math.Abs(car.tyreTF - opt) < 0.3 * win && System.Math.Abs(car.tyreTR - opt) < 0.3 * win)
            { windowSaid = true; Post(E(PInfo, false, false, "Tyres are in the window now.", TTyre, 8f).A(C.tyres_window)); }
        }

        // tyre wear: worn -> box (the driver says it first), past the point of a stop -> look after them
        double wear = System.Math.Max(car.wearF, car.wearR);
        if (wear > 0.5 && !wearMidSaid) { wearMidSaid = true; Post(E(PInfo, false, false, "The tyres are wearing now. Look after them.", TTyre, 10f).A(C.wear_mid)); }
        if (wear > 0.72 && !wearHighSaid)
        {
            wearHighSaid = true;
            if (view.pitLane && left >= 2 && !view.boxArmed)
                Post(D("Tyres are gone!", C.d_tyres_gone).Then(E(PPit, true, true, "Box this lap, confirm.", TPit).A(C.box_confirm)));
            else if (left >= 2) Post(E(PInfo, false, false, "Tyres are close to the cliff.", TTyre, 10f).A(C.wear_high));
        }
        // the wrong tyres for the weather
        if (clock > tyreSuitAt && rt > cd + 20 && left >= 1 && !view.boxArmed && lap - tyreCallLap >= 2 && !TyreSets.Suits(view.tyreSet, Weather.Index))
        {
            tyreCallLap = lap; tyreSuitAt = clock + 60f;
            int best = Weather.BestSet();
            var e = best >= Weather.Full ? E(PPit, true, true, "Box for the full wet tyres.", TPit).A(C.box_wets)
                  : best == Weather.Inter ? E(PPit, true, true, "Box for intermediates.", TPit).A(C.box_inters)
                  : E(PPit, true, true, "The track is drying. Box for slick tyres.", TPit).A(C.box_slicks);
            bool slicksInWet = TyreSets.Slick(view.tyreSet) && Weather.Index >= 1;
            Post(slicksInWet && mode == ModeFull ? D("I've got no grip!", C.d_no_grip).Then(e) : e);
        }

        // battery (F1 only)
        if (cfg.ersPower > 0 && cfg.ersCapacity > 0 && !double.IsNaN(car.ersEnergy))
        {
            double soc = car.ersEnergy / cfg.ersCapacity;
            battT = soc < 0.1 ? battT + T : soc > 0.3 ? 0f : battT;
            if (battT > 3f && clock - battSaidAt > 120f)
            {
                battSaidAt = clock; battT = 0f;
                var m = E(PInfo, false, false, "Battery low, harvest.", TBatt, 8f).A(C.batt_low);
                if (Chance(0.4)) m.Then(D("Understood.", C.d_understood));
                Post(m);
            }
        }
    }

    // at the line, lap = the lap starting now (2 .. laps)
    void LapEvents(int lap, int laps)
    {
        int togo = laps - lap + 1; // this lap included
        if (lap == laps)
        {
            bool fin = Chance(0.5);
            Post(fin ? E(PRace, true, true, "Final lap, final lap. Give it everything.", TLap, 15f).A(C.final_lap)
                     : E(PRace, true, true, "Last lap. Bring it home.", TLap, 15f).A(C.last_lap));
        }
        else
        {
            C c = togo == 20 ? C.laps20 : togo == 15 ? C.laps15 : togo == 10 ? C.laps10 : togo == 5 ? C.laps5 : togo == 3 ? C.laps3 : togo == 2 ? C.laps2 : C.Count;
            if (c != C.Count) Post(E(PInfo, false, false, togo + " laps to go.", TLap, 12f).A(c));
            else if (laps >= 8 && lap == laps / 2 + 1) Post(E(PInfo, false, false, "Halfway. Good work so far.", TLap, 12f).A(C.halfway));
        }
        if (Neutral()) return;
        // overtake: within a second of the car ahead at the line
        if (lap - overtakeLap >= 2 && GapAhead(out double ga) && ga < 1.0)
        {
            overtakeLap = lap;
            var cfg = view.car.cfg;
            bool boost = cfg.ersPower > 0 && !double.IsNaN(view.car.ersEnergy) && view.car.ersEnergy > 0.5 * cfg.ersCapacity && Chance(0.5);
            Post(boost ? E(PRace, true, true, "Within one second. Use your boost.", TGap, 5f).A(C.within_one).A(C.boost)
                       : E(PRace, true, true, "Within one second. Overtake available.", TGap, 5f).A(C.within_one).A(C.overtake));
        }
        else if (lap >= nextGapLap && lap < laps) { nextGapLap = lap + 2 + rng.Next(0, 2); gapPending = true; }
    }

    // position and gaps, a quarter into the lap
    void GapCall()
    {
        if (Neutral() || view.pitRun != null || mode != ModeFull) return;
        int pos = Position();
        bool hasA = GapAhead(out double ga) && ga < 9.95, hasB = GapBehind(out double gb) && gb < 9.95;
        if (!hasA && !hasB) return;
        var m = E(PRace, false, false, null, TGap, 10f).A(PosClip(pos)).Pause();
        string s = Pos(pos);
        if (hasA) { m.A(C.gap_ahead); Num(m, ga); s += ". Gap ahead " + Sec(ga); }
        bool catching = hasA && !double.IsNaN(lastGapAhead) && ga < lastGapAhead - 0.7;
        bool growing = hasB && !double.IsNaN(lastGapBehind) && gb > lastGapBehind + 1.0 && gb < 6;
        if (catching) { m.Pause().A(C.catching); s += ". You're catching him."; }
        else if (hasB && (gb < 2.5 || !hasA)) { m.Pause().A(C.gap_behind); Num(m, gb); s += (hasA ? ", behind " : ". Gap behind ") + Sec(gb); }
        else if (growing) { m.Pause().A(C.pulling_away); s += ". Gap is growing."; }
        m.text = s + ".";
        lastGapAhead = hasA ? ga : double.NaN; lastGapBehind = hasB ? gb : double.NaN;
        Post(m);
    }

    void Result()
    {
        StopAll();
        int pos = Position();
        bool champ = false;
        if (seasonRound && Season.Over) champ = Season.Rank(0) == 1;
        Msg m;
        if (pos == 1)
        {
            m = E(PSafety, true, true, "P1! P1! You've won the race! Fantastic job!", TNone, 20f).A(C.win);
            if (champ) { m.Pause().A(C.champion); m.text += " And you are the world champion!"; }
            m.Then(D("Yes! Get in there!", C.d_yes)).Then(D("Thanks guys! The car was amazing today!", C.d_thanks));
        }
        else if (pos <= 3)
        {
            m = E(PSafety, true, true, Pos(pos) + ". Podium! Great job today, really great job.", TNone, 20f).A(PosClip(pos)).Pause().A(C.podium);
            if (champ) { m.Pause().A(C.champion); m.text += " And you are the world champion!"; }
            m.Then(D("Thanks guys! The car was amazing today!", C.d_thanks));
        }
        else if (champ) m = E(PSafety, true, true, Pos(pos) + ". And you are the world champion! Unbelievable, mate!", TNone, 20f).A(PosClip(pos)).Pause().A(C.champion).Then(D("Yes! Get in there!", C.d_yes));
        else if (pos <= 10) m = E(PSafety, true, true, Pos(pos) + ". Good points today. Well done.", TNone, 20f).A(PosClip(pos)).Pause().A(C.points).Then(D("Copy.", C.d_copy));
        else m = E(PSafety, true, true, Pos(pos) + ". Tough one today. We'll come back stronger.", TNone, 20f).A(PosClip(pos)).Pause().A(C.consolation).Then(D("Ugh, sorry guys.", C.d_sorry));
        m.notBefore = clock + 0.6f;
        lastEnd = -100f;
        Post(m);
    }

    // ---------------------------------------------------------------- caption above the dash (RadioCaption calls this)
    internal void DrawCaption()
    {
        if (capText == null || clock >= capUntil) return;
        string label = capDriver ? "DRIVER" : "ENGINEER";
        float maxW = Screen.width * 0.62f;
        if (!ReferenceEquals(capFor, capText))
        {
            capFor = capText; capSize = UiKit.Fit(capText, UiKit.Semi, 9f, maxW - UiKit.U(78), 6.5f);
            capW = UiKit.Width(capText, UiKit.Semi, capSize);
        }
        float a = Mathf.Clamp01((clock - capStart) / 0.15f) * Mathf.Clamp01((capUntil - clock) / 0.3f);
        var sa = UiKit.Safe;
        float dashTop = sa.yMax - UiKit.U(8) - UiKit.U(52);
        float h = UiKit.U(20), padX = UiKit.U(7), labelW = UiKit.U(58);
        float w = Mathf.Min(UiKit.U(4) + padX + labelW + capW + padX, maxW);
        var r = new Rect(Mathf.Round(Screen.width * 0.5f - UiKit.U(125)), Mathf.Round(dashTop - UiKit.U(21) - h), w, h);
        if (r.x < sa.x + UiKit.U(8)) r.x = sa.x + UiKit.U(8);
        UiKit.PushAlpha(a);
        UiKit.Panel(r, 6f, 1f, false);
        Color acc = capDriver ? UiKit.Warn : UiKit.Info;
        UiKit.Rect(new Rect(r.x, r.y, UiKit.U(3), r.height), acc, 1.5f);
        UiKit.Text(new Rect(r.x + UiKit.U(4) + padX, r.y, labelW, h), label, UiKit.Bold, 7f, acc);
        UiKit.Text(new Rect(r.x + UiKit.U(4) + padX + labelW, r.y, w - labelW - padX * 2 - UiKit.U(4), h), capText, UiKit.Semi, capSize, UiKit.TextCol);
        UiKit.PopAlpha();
    }
}

// The caption's OnGUI lives in its own component, enabled only while a caption shows (no IMGUI cost otherwise).
public class RadioCaption : MonoBehaviour
{
    internal TeamRadio owner;
    void OnGUI() { GUI.depth = -5; if (owner != null && Event.current.type == EventType.Repaint) owner.DrawCaption(); }
}

// TeamRadio's read-only view of the race. One member, so it cannot clash with the rest of GameManager.
public partial class GameManager
{
    internal void TeamRadioRead(TeamRadio.View v)
    {
        v.audio = audioEngine; v.sound = sound;
        v.paused = Paused || replaying;
        v.results = ui != null && ui.page == MenuUI.Page.Results;
        v.race = raceLaps > 0 && !playground && !qualiSession && track != null && car != null && rivals != null;
        if (!v.race) return;
        v.laps = raceLaps; v.raceTime = raceTime; v.countdown = Countdown; v.simTime = simTime;
        v.lap = pLap; v.idx = idx; v.track = track; v.car = car; v.rivals = rivals;
        v.pitLoss = pitLoss; v.refLap = RefLap();
        v.lapStart = lapStart ?? double.NaN; v.lastLap = lastLap ?? double.NaN;
        v.cut = cut; v.boxArmed = boxArmed; v.pitLane = PitLaneOn; v.pitRun = pRun; v.pitStops = pitStops;
        v.rc = raceCtl; v.rcSlot = rcPlayerSlot; v.jumped = jumped; v.tyreSet = carSet; v.seasonRound = InSeasonRound;
        v.penSerial = pgPenSerial; v.penSecs = pgPenLast;
    }
}
