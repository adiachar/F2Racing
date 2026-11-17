// RacingLineView.cs — the racing-line guide on the road (GRID Autosport style): chevrons along the optimal line
// (RacingLine, the same line and speed profile the AI drives), shown about 250 m ahead of the player's car and
// faded at both ends, coloured by the car's speed against the target speed there:
//   green  = below the target: you can go faster / accelerate
//   yellow = near the limit: lift, or you are at the cornering limit
//   red    = too fast: brake now (you need at least the profile's own braking to make the target speed there)
// The target speeds are RacingLine.speed (RacingLine.SpeedProfile: the car's own tyre / aero / power law, built for
// the player's car in GameManager.BuildLine); the braking the profile assumes at each speed is read back from the
// profile itself, so "red" starts where the profile starts to brake. Wet track: speeds scale by sqrt(grip), braking
// by grip.
// One small dynamic mesh (one draw call, a few hundred vertices rewritten each frame, no allocation), draped on the
// road from vertex heights worked out once. Modes: 0 off, 1 braking only (yellow / red parts), 2 full.
// Physics (x, y) maps to Unity (x, height, y), as in WorldBuilder.
using FormulaSim;
using UnityEngine;

public sealed class RacingLineView
{
    const float HalfWidth = 0.42f;          // 0.84 m wide strip
    const float Lift = 0.03f + 0.025f;      // road surface (WorldBuilder Road strip at 0.03) + 2.5 cm (plus a depth offset in the shader)
    const double Ahead = 255, FadeFrom = 175, Behind = 5;
    public const int Off = 0, BrakingOnly = 1, Full = 2;

    public readonly GameObject go;
    readonly Track track; readonly RacingLine line;
    readonly int N, M, back;
    readonly Vector3[] lp, rp;              // every line point: left and right edge, draped (built once)
    readonly double[] sAt;                  // metres from point 0 to each point along the line
    readonly double lapLen, period;
    readonly Mesh mesh; readonly Material mat; readonly bool custom; readonly MeshRenderer rend;
    readonly Vector3[] v; readonly Vector2[] uv; readonly Color32[] col; readonly double[] dist;
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
            lp[i] = new Vector3((float)ax, WorldBuilder.H(ax, ay) + Lift, (float)ay);
            rp[i] = new Vector3((float)bx, WorldBuilder.H(bx, by) + Lift, (float)by);
            sAt[i] = s; s += l.ds[i];
        }
        lapLen = s;
        period = lapLen / System.Math.Max(1, System.Math.Round(lapLen / 3.2)); // a whole number of chevrons per lap
        double mean = lapLen / N;
        back = Mathf.Max(1, (int)System.Math.Ceiling(Behind / mean));
        M = Mathf.Min(N - 1, back + (int)System.Math.Ceiling(Ahead / mean) + 2);

        v = new Vector3[M * 2]; uv = new Vector2[M * 2]; col = new Color32[M * 2]; dist = new double[M];
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
        brakeD[0] = 0;
        for (int b = 0; b < Bins; b++) brakeD[b + 1] = brakeD[b] + (b + 0.5) * BinW * BinW / System.Math.Max(4, aCap[b]); // integral of u du / a(u)
        // The bins hold the hardest braking at each speed; a zone braking while turning, or downhill, brakes softer.
        // Each point that ends a stretch of falling target speed gets the ratio of the profile's real zone length to
        // the model's, so the colour turns red exactly at the profile's own braking point for that corner.
        for (int i = 0; i < N; i++)
        {
            zoneK[i] = 1;
            int j = i; double len = 0;
            while (len < 1500) { int pj = (j - 1 + N) % N; if (!(sp[pj] > sp[j])) break; len += line.ds[pj]; j = pj; }
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
        if (mode == Off || idx < 0 || idx >= N) { rend.enabled = false; return; }
        rend.enabled = true;
        if (capFor != line.speed) BuildCap();
        grip = System.Math.Max(0.3, System.Math.Min(1.2, grip));
        double vScale = System.Math.Sqrt(grip), dNow = D(speed);

        // where the car is along the line at its own point
        int o = (idx - 1 + N) % N, q = (idx + 1) % N;
        double tx = line.x[q] - line.x[o], ty = line.y[q] - line.y[o], tl = System.Math.Sqrt(tx * tx + ty * ty);
        double along = tl > 1e-9 ? ((cx - line.x[idx]) * tx + (cy - line.y[idx]) * ty) / tl : 0;
        int i0 = ((idx - back) % N + N) % N;
        // distances from the car to each point of the window
        dist[back] = -along;
        for (int k = back + 1; k < M; k++) dist[k] = dist[k - 1] + line.ds[(i0 + k - 1) % N];
        for (int k = back - 1; k >= 0; k--) dist[k] = dist[k + 1] - line.ds[(i0 + k) % N];

        double phase0 = sAt[i0] % period, rel = 0;
        // already in a braking zone of the profile at (or above) its speed: the rest of that zone is red
        bool zone = line.braking[idx] && speed >= 0.96 * line.speed[idx] * vScale;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int k = 0; k < M; k++)
        {
            int i = (i0 + k) % N;
            double d = dist[k], vt = System.Math.Max(1, line.speed[i] * vScale);
            // red / yellow: the distance the profile's braking needs to slow from this speed to this point's target,
            // against the distance to it (1 = the profile's own braking point: brake now)
            double kq = speed > vt ? zoneK[i] * (dNow - D(vt)) / grip / System.Math.Max(d, 10) : 0;
            if (k > back && zone) { if (line.braking[(i - 1 + N) % N] && speed > vt) kq = System.Math.Max(kq, 1.05); else zone = false; }
            // at the cornering limit on the profile (phase 0) and about at its speed: yellow
            double ratio = speed / vt;
            if (line.phase[i] == 0) kq = System.Math.Max(kq, 0.86 * Smooth(0.88, 0.97, ratio) * (1 - Smooth(1.12, 1.3, ratio)));
            Color c;
            if (kq < 0.62) c = GreenL;
            else if (kq < 0.8) c = Color.Lerp(GreenL, YellowL, Smooth(0.62, 0.8, kq));
            else if (kq < 0.92) c = YellowL;
            else c = Color.Lerp(YellowL, RedL, Smooth(0.92, 1.0, kq));
            float a = 0.9f * Smooth(-Behind, 1.5, d) * (1 - Smooth(FadeFrom, Ahead, d));
            if (mode == BrakingOnly) a *= Smooth(0.55, 0.75, kq);
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
