// VectorFont.cs — a smooth stroke font for the trackside lettering (ad boards, banners, signs, braking boards).
// Each glyph is a few centre-line strokes (lines and elliptic arcs) in a 5 x 7 cell box, the same box the old pixel
// font used, so callers keep their layout. At first use the strokes are turned into a distance atlas (8 samples per
// cell, made once at load, ~0.5 MB of floats): Cov() reads it with bilinear filtering and turns the distance into
// coverage with a one-pixel ramp, i.e. antialiased edges at any letter size. The textures the letters are drawn into
// are mipmapped, so they stay smooth far away too. Load time only; nothing per frame.
using System.Collections.Generic;
using UnityEngine;

public static class VectorFont
{
    // centre lines: polylines split by '|', points "x,y" (x 0..5 across, y 0..7 down from the top), and arcs
    // "a cx,cy,rx,ry,deg0,deg1" (0 deg = +x, 90 deg = down) that continue the polyline from its last point
    const string O0 = "a2.5,2.5,2,2,180,360 a2.5,4.5,2,2,0,180 0.5,2.5";
    static string Strokes(char ch)
    {
        switch (ch)
        {
            case 'A': return "0.5,6.5 2.5,0.5 4.5,6.5 | 1.17,4.5 3.83,4.5";
            case 'B': return "0.5,3.5 3.0,3.5 a3.0,5.0,1.5,1.5,-90,90 0.5,6.5 0.5,0.5 2.9,0.5 a2.9,2.0,1.35,1.5,-90,90";
            case 'C': return "a2.5,2.5,2,2,-25,-180 a2.5,4.5,2,2,180,25";
            case 'D': return "0.5,0.5 2.3,0.5 a2.3,2.7,2.2,2.2,-90,0 a2.3,4.3,2.2,2.2,0,90 0.5,6.5 0.5,0.5";
            case 'E': return "4.4,0.5 0.5,0.5 0.5,6.5 4.4,6.5 | 0.5,3.5 3.6,3.5";
            case 'F': return "4.4,0.5 0.5,0.5 0.5,6.5 | 0.5,3.5 3.6,3.5";
            case 'G': return "a2.5,2.5,2,2,-25,-180 a2.5,4.5,2,2,180,0 4.5,3.7 2.7,3.7";
            case 'H': return "0.5,0.5 0.5,6.5 | 4.5,0.5 4.5,6.5 | 0.5,3.5 4.5,3.5";
            case 'I': return "2.5,0.5 2.5,6.5 | 1.3,0.5 3.7,0.5 | 1.3,6.5 3.7,6.5";
            case 'J': return "1.6,0.5 4.5,0.5 4.5,4.5 a2.5,4.5,2,2,0,160";
            case 'K': return "0.5,0.5 0.5,6.5 | 4.4,0.5 0.5,4.3 | 1.9,3.0 4.5,6.5";
            case 'L': return "0.5,0.5 0.5,6.5 4.4,6.5";
            case 'M': return "0.5,6.5 0.5,0.5 2.5,4.2 4.5,0.5 4.5,6.5";
            case 'N': return "0.5,6.5 0.5,0.5 4.5,6.5 4.5,0.5";
            case 'O': return O0;
            case 'P': return "0.5,6.5 0.5,0.5 2.9,0.5 a2.9,2.1,1.6,1.6,-90,90 0.5,3.7";
            case 'Q': return O0 + " | 3.0,5.0 4.7,6.8";
            case 'R': return "0.5,6.5 0.5,0.5 2.9,0.5 a2.9,2.1,1.6,1.6,-90,90 0.5,3.7 | 2.6,3.7 4.5,6.5";
            case 'S': return "a2.5,2.0,1.95,1.5,-20,-270 a2.5,5.0,2,1.5,-90,160";
            case 'T': return "0.5,0.5 4.5,0.5 | 2.5,0.5 2.5,6.5";
            case 'U': return "0.5,0.5 0.5,4.5 a2.5,4.5,2,2,180,0 4.5,0.5";
            case 'V': return "0.5,0.5 2.5,6.5 4.5,0.5";
            case 'W': return "0.3,0.5 1.4,6.5 2.5,2.6 3.6,6.5 4.7,0.5";
            case 'X': return "0.5,0.5 4.5,6.5 | 4.5,0.5 0.5,6.5";
            case 'Y': return "0.5,0.5 2.5,3.6 4.5,0.5 | 2.5,3.6 2.5,6.5";
            case 'Z': return "0.6,0.5 4.5,0.5 0.5,6.5 4.5,6.5";
            case '0': return O0;
            case '1': return "1.2,1.8 2.9,0.5 2.9,6.5";
            case '2': return "a2.5,2.4,2,1.9,-170,30 0.5,6.5 4.5,6.5";
            case '3': return "a2.5,2.0,1.9,1.5,-160,90 a2.5,5.0,2,1.5,-90,160 | 1.7,3.5 2.5,3.5";
            case '4': return "3.6,6.5 3.6,0.5 0.5,4.7 4.6,4.7";
            case '5': return "4.3,0.5 1.0,0.5 0.7,3.4 a2.5,4.6,2,1.9,-150,155";
            case '6': return "a2.5,2.5,2,2,-30,-180 0.5,4.6 a2.5,4.6,2,1.9,180,-180";
            case '7': return "0.5,0.5 4.5,0.5 1.8,6.5";
            case '8': return "a2.5,2.0,1.8,1.5,0,360 | a2.5,5.0,2,1.5,0,360";
            case '9': return "a2.5,2.4,2,1.9,0,360 | 4.5,2.4 4.5,4.5 a2.5,4.5,2,2,0,150";
            default: return null;
        }
    }

    // distance atlas: per glyph a (5 + 2 PAD) x (7 + 2 PAD) cell box at RES samples per cell, distance in cells
    const int RES = 8, PAD = 1, GW = (5 + 2 * PAD) * RES + 1, GH = (7 + 2 * PAD) * RES + 1;
    static readonly Dictionary<char, float[]> atlas = new Dictionary<char, float[]>();

    static void Parse(string s, List<Vector2> seg)
    {
        foreach (var part in s.Split('|'))
        {
            bool have = false; Vector2 last = Vector2.zero;
            var tok = part.Trim().Split(' ');
            for (int k = 0; k < tok.Length; k++)
            {
                string tk = tok[k]; if (tk.Length == 0) continue;
                if (tk[0] == 'a')
                {
                    var f = tk.Substring(1).Split(',');
                    float cx = F(f[0]), cy = F(f[1]), rx = F(f[2]), ry = F(f[3]), a0 = F(f[4]), a1 = F(f[5]);
                    int n = Mathf.Max(4, Mathf.CeilToInt(Mathf.Abs(a1 - a0) / 9f));
                    for (int q = 0; q <= n; q++)
                    {
                        float a = Mathf.Lerp(a0, a1, (float)q / n) * Mathf.Deg2Rad;
                        var p = new Vector2(cx + rx * Mathf.Cos(a), cy + ry * Mathf.Sin(a));
                        if (have) { seg.Add(last); seg.Add(p); }
                        last = p; have = true;
                    }
                }
                else
                {
                    var f = tk.Split(',');
                    var p = new Vector2(F(f[0]), F(f[1]));
                    if (have) { seg.Add(last); seg.Add(p); }
                    last = p; have = true;
                }
            }
        }
    }
    static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    static float[] Build(char ch)
    {
        string s = Strokes(ch); if (s == null) return null;
        var seg = new List<Vector2>(); Parse(s, seg);
        var d = new float[GW * GH];
        for (int j = 0; j < GH; j++)
            for (int i = 0; i < GW; i++)
            {
                var p = new Vector2((float)i / RES - PAD, (float)j / RES - PAD);
                float best = 1e9f;
                for (int k = 0; k + 1 < seg.Count; k += 2)
                {
                    Vector2 a = seg[k], ab = seg[k + 1] - a; float l2 = ab.sqrMagnitude;
                    float t = l2 > 1e-8f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2) : 0f;
                    float dd = (p - a - ab * t).sqrMagnitude; if (dd < best) best = dd;
                }
                d[j * GW + i] = Mathf.Sqrt(best);
            }
        return d;
    }

    // distance (cells) from the glyph's nearest stroke centre line at (gx 0..5 across, gy 0..7 from the top); large
    // outside the box or for characters the font does not have (space)
    public static float Dist(char ch, float gx, float gy)
    {
        float[] d;
        if (!atlas.TryGetValue(ch, out d)) { d = Build(ch); atlas[ch] = d; }
        if (d == null) return 9f;
        float fx = (gx + PAD) * RES, fy = (gy + PAD) * RES;
        if (fx < 0f || fy < 0f || fx >= GW - 1 || fy >= GH - 1) return 9f;
        int ix = (int)fx, iy = (int)fy; float a = fx - ix, b = fy - iy; int o = iy * GW + ix;
        float v0 = d[o] + (d[o + 1] - d[o]) * a, v1 = d[o + GW] + (d[o + GW + 1] - d[o + GW]) * a;
        return v0 + (v1 - v0) * b;
    }

    // coverage 0..1 of the letter at (gx, gy): strokes "weight" cells thick, ppc = texture pixels per cell (the
    // antialiasing ramp is one pixel wide, centred on the stroke's edge: a box filter of the outline)
    public static float Cov(char ch, float gx, float gy, float ppc, float weight = 1.05f)
    {
        float d = Dist(ch, gx, gy);
        return Mathf.Clamp01((weight * 0.5f - d) * ppc + 0.5f);
    }
}
