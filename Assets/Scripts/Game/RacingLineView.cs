// RacingLineView.cs — the racing-line guide on the road (GRID Autosport style): chevrons along the optimal line
// (RacingLine, the same line and speed profile the AI drives), shown 220-420 m ahead of the player's car (further
// at speed: about 3.6 s of road) and faded at both ends. The colours are worked out every frame from the PLAYER's
// speed v and the distance d along the line to each point, so the braking zone shows up well before the corner:
//   Approach / braking points ("hold v until here, then brake"): the latest brake point for v is
//     B(p) = min over the points q at or after p of [d_q - Dist(v -> target_q)]
//   (q: the profile's braking and cornering-limited points, not its power-limited ones), where Dist is the distance
//   the speed profile's own braking (its tyre / aero law at brakeUse 0.8 of the grip,
//   read back from the profile per speed; so a_req = (v^2 - v_t^2) / 2d against that braking) needs. The time
//   left before it, (B(p) - d_p) / v, colours the point: green with more than ~1.1 s, yellow / amber ("lift, brake
//   soon") inside that, red from ~0.1 s before the brake point and everywhere after it ("brake now / keep
//   braking"). Slower than that, the red starts later (or not at all); faster, earlier. Braking at the profile's
//   rate keeps the zone ahead red; braking harder than needed turns it back to yellow / green ahead.
//   Corner points (the profile at its cornering limit there, the player above its target): green while he can
//   still get down to the target in time (a_req / profile braking <= 1), yellow to red as a_req passes the profile's
//   braking towards the tyres' full grip (1.3 x): too fast to hold the line. Near the car, 0-5 % over the target
//   there shows yellow (at the limit), 6-12 % over it red (past the grip).
// Each track point's colour is smoothed over 0.1 s (no flicker). Wet track: speeds scale by sqrt(grip), braking by
// grip. Modes: 0 off, 1 braking only (the yellow / red parts), 2 full.
// One small dynamic mesh (one draw call, a few hundred vertices rewritten each frame, no allocation), draped on the
// road from vertex heights worked out once.
// Physics (x, y) maps to Unity (x, height, y), as in WorldBuilder.
using FormulaSim;
using UnityEngine;

public sealed class RacingLineView
{
    const float HalfWidth = 0.42f;          // 0.84 m wide strip
    const float Lift = 0.03f + 0.025f;      // road surface (WorldBuilder Road strip at 0.03) + 2.5 cm (plus a depth offset in the shader)
    const double MinAhead = 220, MaxAhead = 420, AheadT = 3.6, FadeShare = 0.3, Behind = 5;
    const double Scan = 350;                // metres of constraints looked at past the drawn end (braking from top speed)
    const double Tol = 1.04;                // targets count 4 % over the profile (it corners at 0.82 of the grip)
    const double TGreen = 1.2, TRed = -0.1; // s before the brake point: green above ~1.1 s, yellow ~0.55 s, full red ~0.1 s
    const float Tau = 0.1f;                 // s, colour smoothing per point
    public const int Off = 0, BrakingOnly = 1, Full = 2;

    public readonly GameObject go;
    readonly Track track; readonly RacingLine line;
    readonly int N, M, MS, back;            // M points drawn, MS points scanned (drawn + constraints beyond)
    readonly Vector3[] lp, rp;              // every line point: left and right edge, draped (built once)
    readonly double[] sAt;                  // metres from point 0 to each point along the line
    readonly double lapLen, period;
    readonly Mesh mesh; readonly Material mat; readonly bool custom; readonly MeshRenderer rend;
    readonly Vector3[] v; readonly Vector2[] uv; readonly Color32[] col;
    readonly double[] dist, bmin;           // per scanned point: metres from the car; latest brake point (see top)
    readonly float[] sev; readonly int[] sevAt; int frameNo = 10; // per track point: smoothed severity 0 green .. 1 red, the frame it was set
    static readonly Color GreenL = new Color(0.20f, 0.95f, 0.35f).linear, YellowL = new Color(1f, 0.82f, 0.10f).linear, RedL = new Color(1f, 0.14f, 0.10f).linear;

    // the profile's braking (m/s^2) by speed, 2 m/s bins, and the distance it needs to brake from each bin edge to 0
    const int Bins = 64; const double BinW = 2;
    readonly double[] aCap = new double[Bins], brakeD = new double[Bins + 1]; double[] capFor;
    readonly double[] zoneK;                // per point: the profile's real braking zone into it / the D() model's (calibration)

    RacingLineView(Track t, RacingLine l, Transform parent)
    {
        track = t; line = l; N = l.N;
        lp = new Vector3[N]; rp = new Vector3[N]; sAt = new double[N]; zoneK = new double[N];
        double s = 0;
        for (int i = 0; i < N; i++)
        {
            int o = (i - 1 + N) % N, q = (i + 1) % N;
            double tx = l.x[q] - l.x[o], ty = l.y[q] - l.y[o], len = System.Math.Sqrt(tx * tx + ty * ty);
            if (len < 1e-9) { tx = t.pts[i].tx; ty = t.pts[i].ty; } else { tx /= len; ty /= len; }
            double ax = l.x[i] - ty * HalfWidth, ay = l.y[i] + tx * HalfWidth, bx = l.x[i] + ty * HalfWidth, by = l.y[i] - tx * HalfWidth;
            lp[i] = new Vector3((float)ax, WorldBuilder.HOn(i, ax, ay) + Lift, (float)ay); // (HOn: its own leg's level at a bridge)
            rp[i] = new Vector3((float)bx, WorldBuilder.HOn(i, bx, by) + Lift, (float)by);
            sAt[i] = s; s += l.ds[i];
        }
        lapLen = s;
        period = lapLen / System.Math.Max(1, System.Math.Round(lapLen / 3.2)); // a whole number of chevrons per lap
        double mean = lapLen / N;
        back = Mathf.Max(1, (int)System.Math.Ceiling(Behind / mean));
        MS = Mathf.Min(N - 1, back + (int)System.Math.Ceiling((MaxAhead + Scan) / mean) + 2);
        M = Mathf.Min(MS, back + (int)System.Math.Ceiling(MaxAhead / mean) + 2);

        v = new Vector3[M * 2]; uv = new Vector2[M * 2]; col = new Color32[M * 2]; dist = new double[MS]; bmin = new double[MS + 1];
        sev = new float[N]; sevAt = new int[N];
        var tris = new int[(M - 1) * 6];
        for (int k = 0; k < M - 1; k++)
        {
            int a = k * 2, n = a + 2, j = k * 6;
            tris[j] = a; tris[j + 1] = n; tris[j + 2] = a + 1;     // same winding as WorldBuilder.Strip (faces up)
            tris[j + 3] = n; tris[j + 4] = n + 1; tris[j + 5] = a + 1;
        }
        for (int k = 0; k < M; k++) { uv[k * 2] = new Vector2(-1, 0); uv[k * 2 + 1] = new Vector2(1, 0); }
        mesh = new Mesh { name = "RacingLine" };
        mesh.MarkDynamic();
        mesh.vertices = v; mesh.uv = uv; mesh.colors32 = col; mesh.triangles = tris;

        var sh = Resources.Load<Shader>("RacingLine");
        custom = sh != null && sh.isSupported;
        if (!custom) sh = Shader.Find("Sprites/Default"); // vertex colour + alpha, no chevrons
        mat = new Material(sh) { name = "RacingLine" };
        if (custom) mat.SetFloat("_Period", (float)period);
        mat.renderQueue = 2980;

        go = new GameObject("RacingLine");
        if (parent != null) go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat; rend = r;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        r.enabled = false; // until the first Frame
    }

    public static RacingLineView Build(Track t, RacingLine line, Transform parent) => t == null || line == null || line.N < 8 ? null : new RacingLineView(t, line, parent);

    // the braking the speed profile uses at each speed: the steepest fall of the target speed per speed bin
    void BuildCap()
    {
        capFor = line.speed;
        for (int b = 0; b < Bins; b++) aCap[b] = 0;
        var sp = line.speed;
        for (int i = 0; i < N; i++)
        {
            int q = (i + 1) % N;
            double v0 = sp[i], v1 = sp[q], ds = line.ds[i];
            if (v1 >= v0 || ds <= 0) continue;
            double a = (v0 * v0 - v1 * v1) / (2 * ds);
            int b = Mathf.Clamp((int)(v0 / BinW), 0, Bins - 1);
            if (a > aCap[b]) aCap[b] = a;
        }
        // fill empty bins from their neighbours (linear), then hold the ends
        int first = -1, last = -1;
        for (int b = 0; b < Bins; b++) if (aCap[b] > 0) { if (first < 0) first = b; last = b; }
        if (first < 0) { for (int b = 0; b < Bins; b++) aCap[b] = 15; return; }
        for (int b = 0; b < first; b++) aCap[b] = aCap[first];
        for (int b = last + 1; b < Bins; b++) aCap[b] = aCap[last];
        for (int b = first + 1, prev = first; b <= last; b++)
        {
            if (aCap[b] <= 0) continue;
            for (int k = prev + 1; k < b; k++) aCap[k] = aCap[prev] + (aCap[b] - aCap[prev]) * (k - prev) / (b - prev);
            prev = b;
        }
        // a formula car brakes harder the faster it goes (downforce, drag): never less than at a lower speed (the ERS
        // superclip's gentle slow-down at the end of a long straight is no measure of the brakes)
        for (int b = 1; b < Bins; b++) if (aCap[b] < aCap[b - 1]) aCap[b] = aCap[b - 1];
        brakeD[0] = 0;
        for (int b = 0; b < Bins; b++) brakeD[b + 1] = brakeD[b] + (b + 0.5) * BinW * BinW / System.Math.Max(4, aCap[b]); // integral of u du / a(u)
        // The bins hold the hardest braking at each speed; a zone braking while turning, or downhill, brakes softer.
        // Each point that ends a stretch of falling target speed gets the ratio of the profile's real zone length to
        // the model's, so the colour turns red exactly at the profile's own braking point for that corner.
        for (int i = 0; i < N; i++)
        {
            zoneK[i] = 1;
            int j = i; double len = 0;
            while (len < 1500) { int pj = (j - 1 + N) % N; if (!line.braking[pj] || (sp[pj] * sp[pj] - sp[j] * sp[j]) / (2 * System.Math.Max(0.01, line.ds[pj])) < 0.4 * aCap[Mathf.Clamp((int)(sp[pj] / BinW), 0, Bins - 1)]) break; len += line.ds[pj]; j = pj; } // real braking only (not a superclip)
            double model = D(sp[j]) - D(sp[i]);
            if (len > 1 && model > 0.5) zoneK[i] = System.Math.Max(0.7, System.Math.Min(3, len / model));
        }
    }
    // metres the profile's braking needs from speed u to a stop (slowing from u to w takes D(u) - D(w))
    double D(double u)
    {
        double f = u / BinW; if (f <= 0) return 0;
        int b = (int)f; if (b >= Bins) return brakeD[Bins] + (u * u - Bins * BinW * Bins * BinW) / (2 * System.Math.Max(4, aCap[Bins - 1]));
        return brakeD[b] + (brakeD[b + 1] - brakeD[b]) * (f - b);
    }

    static float Smooth(double e0, double e1, double x) { double t = (x - e0) / (e1 - e0); t = t < 0 ? 0 : t > 1 ? 1 : t; return (float)(t * t * (3 - 2 * t)); }

    // once a frame: idx = the player's track point, (cx, cy) its position, speed in m/s, grip = wet factor (1 dry)
    public void Frame(int mode, int idx, double cx, double cy, double speed, double grip)
    {
        if (mode == Off || idx < 0 || idx >= N) { rend.enabled = false; frameNo += 2; return; } // (+2: no smoothing from stale colours)
        rend.enabled = true;
        if (capFor != line.speed) BuildCap();
        frameNo++;
        float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f), blend = 1f - Mathf.Exp(-dt / Tau);
        grip = System.Math.Max(0.3, System.Math.Min(1.2, grip));
        double vScale = System.Math.Sqrt(grip), dNow = D(speed), vs = System.Math.Max(speed, 8);
        double ahead = System.Math.Max(MinAhead, System.Math.Min(MaxAhead, speed * AheadT)), fadeFrom = ahead * (1 - FadeShare);

        // where the car is along the line at its own point
        int o = (idx - 1 + N) % N, q = (idx + 1) % N;
        double tx = line.x[q] - line.x[o], ty = line.y[q] - line.y[o], tl = System.Math.Sqrt(tx * tx + ty * ty);
        double along = tl > 1e-9 ? ((cx - line.x[idx]) * tx + (cy - line.y[idx]) * ty) / tl : 0;
        int i0 = ((idx - back) % N + N) % N;
        // distances from the car to each point of the scan
        dist[back] = -along;
        for (int k = back + 1; k < MS; k++) dist[k] = dist[k - 1] + line.ds[(i0 + k - 1) % N];
        for (int k = back - 1; k >= 0; k--) dist[k] = dist[k + 1] - line.ds[(i0 + k) % N];
        // backward: the latest brake point for the car's speed over every target at or after each point
        bmin[MS] = double.MaxValue;
        for (int k = MS - 1; k >= 0; k--)
        {
            int i = (i0 + k) % N;
            double vt = System.Math.Max(1, line.speed[i] * vScale * Tol), b = bmin[k + 1];
            if (speed > vt && line.phase[i] <= 0) { double c = dist[k] - zoneK[i] * (dNow - D(vt)) / grip; if (c < b) b = c; }
            bmin[k] = b;
        }

        double phase0 = sAt[i0] % period, rel = 0;
        int lastK = 0;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int k = 0; k < M; k++)
        {
            int i = (i0 + k) % N;
            double d = dist[k];
            if (d > ahead + 2 && k > back + 1)
            {
                // past the drawn end: folded onto the last drawn point, invisible (no fill cost)
                v[k * 2] = v[lastK * 2]; v[k * 2 + 1] = v[lastK * 2 + 1];
                col[k * 2] = default; col[k * 2 + 1] = default;
                uv[k * 2].y = uv[lastK * 2].y; uv[k * 2 + 1].y = uv[lastK * 2 + 1].y;
                continue;
            }
            lastK = k;
            double vt0 = System.Math.Max(1, line.speed[i] * vScale), vt = vt0 * Tol, s;
            if (line.phase[i] != 0 || speed <= vt)
            {
                // braking / approach point: time left before the brake point if the car held its speed to here
                double m = bmin[k] - d;
                s = m >= 1e8 ? 0 : 1 - Smooth(TRed, TGreen, m / vs);
            }
            else
            {
                // corner point, the car is above its target: the braking it needs to get there in time against the
                // profile's (1 = the profile's own rate, about 1.25 = the tyres' full grip)
                double r = zoneK[i] * (dNow - D(vt)) / grip / System.Math.Max(d, 1);
                s = Smooth(1.0, 1.3, r);
                // and right in front of the car, 6-12 % over the target (past the tyres' grip: the profile corners
                // at 0.82 of it) is red whatever the braking
                if (d < 0.45 * vs) s = System.Math.Max(s, Smooth(1.06, 1.12, speed / vt0) * (1 - Smooth(0.25 * vs, 0.45 * vs, d)));
            }
            // near the car, at 0-5 % over the target: at the limit (yellow)
            if (d < 45 && line.phase[i] == 0) s = System.Math.Max(s, 0.5 * Smooth(1.0, 1.05, speed / vt0) * (1 - Smooth(25, 45, d)));
            float sv = (float)s;
            if (sevAt[i] == frameNo - 1) sv = sev[i] + (sv - sev[i]) * blend; // 0.1 s smoothing per point
            sev[i] = sv; sevAt[i] = frameNo;
            Color c = sv < 0.5f ? Color.Lerp(GreenL, YellowL, Smooth(0.12, 0.45, sv)) : Color.Lerp(YellowL, RedL, Smooth(0.55, 0.92, sv));
            float a = 0.88f * Smooth(-Behind, 1.5, d) * (1 - Smooth(fadeFrom, ahead, d));
            if (mode == BrakingOnly) a *= Smooth(0.18, 0.4, sv);
            var c32 = new Color32((byte)(c.r * 255 + 0.5f), (byte)(c.g * 255 + 0.5f), (byte)(c.b * 255 + 0.5f), (byte)(a * 255 + 0.5f));
            col[k * 2] = c32; col[k * 2 + 1] = c32;
            var L = lp[i]; var R = rp[i];
            v[k * 2] = L; v[k * 2 + 1] = R;
            float vy = (float)(phase0 + rel);
            uv[k * 2].y = vy; uv[k * 2 + 1].y = vy;
            rel += line.ds[i];
            if (L.x < minX) minX = L.x; if (R.x < minX) minX = R.x; if (L.x > maxX) maxX = L.x; if (R.x > maxX) maxX = R.x;
            if (L.y < minY) minY = L.y; if (R.y < minY) minY = R.y; if (L.y > maxY) maxY = L.y; if (R.y > maxY) maxY = R.y;
            if (L.z < minZ) minZ = L.z; if (R.z < minZ) minZ = R.z; if (L.z > maxZ) maxZ = L.z; if (R.z > maxZ) maxZ = R.z;
        }
        mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetColors(col);
        var lo = new Vector3(minX, minY, minZ); var hi = new Vector3(maxX, maxY, maxZ);
        mesh.bounds = new Bounds((lo + hi) * 0.5f, hi - lo + Vector3.one);
    }

    public void Destroy()
    {
        if (go != null) Object.Destroy(go);
        if (mesh != null) Object.Destroy(mesh);
        if (mat != null) Object.Destroy(mat);
    }
}
