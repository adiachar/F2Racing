// EnvTex.cs — the scenery's surface textures, painted procedurally at load (no third-party art, see Docs/ASSETS.md).
// All tile seamlessly and carry full mip chains; sizes are powers of two so they compress and filter cleanly.
//   GroundDetail  256  grass (blades, clover, tips) or, for sandy / paved circuits, fine sand and grit; mean colour = the
//                      circuit's ground albedo, so Environment's palettes still decide the look. 4.5 m per tile.
//   Macro         256  linear data: R brightness drift, G dry patches, B clumps (gravel ruts appear where B is high);
//                      read again at ~100 m per tile, turned, so the detail tile never shows a repeat.
//   Gravel        256  rounded pale stones of mixed size with dark gaps. 2.5 m per tile.
//   Turf          128  astroturf: fibre grain, two-tone painted bands, thin white edge lines.
//   Paving        256  light concrete slabs with joints and stains (street-circuit pavements).
//   Stone         256  coursed stone / concrete blocks (retaining walls).
//   RoadFine      256  linear data for Road.shader HQ (1.4 m per tile): aggregate albedo, stone polish, stone slopes.
//   Facade        128  one window bay of a plastered town house: frame, sky-lit glass, sill, lintel shadow.
//   Foliage       512  alpha-tested tree cards, a 2 x 2 atlas: spruce, broad-leaf, umbrella pine, palm. Colour is
//                      dilated into the cut-out areas and the mip chain keeps its coverage, so trees do not thin out or
//                      get dark fringes in the distance.
using UnityEngine;

public static class EnvTex
{
    // ---------------------------------------------------------------- noise (tileable over 0..1)
    static float Hash(int x, int y, int s)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + s * 2147483;
            h = (h ^ (int)((uint)h >> 13)) * 1274126177;
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296f;
        }
    }
    static float VN(float u, float v, int c, int s)
    {
        float fx = u * c, fy = v * c; int i = Mathf.FloorToInt(fx), j = Mathf.FloorToInt(fy);
        float a = fx - i, b = fy - j; a = a * a * (3 - 2 * a); b = b * b * (3 - 2 * b);
        int i0 = ((i % c) + c) % c, j0 = ((j % c) + c) % c, i1 = (i0 + 1) % c, j1 = (j0 + 1) % c;
        return Mathf.Lerp(Mathf.Lerp(Hash(i0, j0, s), Hash(i1, j0, s), a), Mathf.Lerp(Hash(i0, j1, s), Hash(i1, j1, s), a), b);
    }
    static float Fbm(float u, float v, int c, int oct, int s)
    {
        float sum = 0, amp = 1, norm = 0;
        for (int o = 0; o < oct; o++) { sum += amp * VN(u, v, c, s + o * 7); norm += amp; amp *= 0.5f; c *= 2; }
        return sum / norm;
    }
    static byte B(float v) => (byte)Mathf.Clamp(v * 255f + 0.5f, 0, 255);
    // linear albedo -> sRGB byte (textures are sRGB; the project renders in linear space)
    static byte S(float lin) => B(Mathf.Pow(Mathf.Clamp01(lin), 1f / 2.2f));

    static Texture2D Make(int w, int h, Color32[] px, bool linear = false, int aniso = 4)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true, linear) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = aniso };
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }
    // per-pixel colour in linear albedo, then scaled so the tile's mean is exactly "mean" (keeps circuit palettes)
    static Texture2D Albedo(int w, int h, Color mean, System.Func<float, float, int, int, Color> f, int aniso = 4)
    {
        var c = new Color[w * h]; double sr = 0, sg = 0, sb = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var k = f((float)x / w, (float)y / h, x, y); c[y * w + x] = k; sr += k.r; sg += k.g; sb += k.b; }
        float n = w * h, kr = mean.r / Mathf.Max(1e-4f, (float)(sr / n)), kg = mean.g / Mathf.Max(1e-4f, (float)(sg / n)), kb = mean.b / Mathf.Max(1e-4f, (float)(sb / n));
        var px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(S(c[i].r * kr), S(c[i].g * kg), S(c[i].b * kb), 255);
        return Make(w, h, px, false, aniso);
    }

    public static int Bytes; // texture memory made here (RGBA32 + mips), for the performance notes
    static Texture2D Count(Texture2D t) { Bytes += t.width * t.height * 4 * 4 / 3; return t; }

    // ---------------------------------------------------------------- ground
    public static bool IsGreen(Color c) => c.g > c.r * 1.12f && c.g > c.b * 1.2f;

    public static Texture2D GroundDetail(Color albedo)
    {
        if (IsGreen(albedo))
            return Count(Albedo(256, 256, albedo, (u, v, x, y) =>
            {
                // blades: short strokes (stretched noise in two directions), a bright tip here and there, clover and
                // darker tufts in patches, and a gentle yellow / blue-green hue drift
                float s1 = VN(u, v * 0.25f + u * 0.1f, 96, 3), s2 = VN(u * 0.3f + v * 0.2f, v, 96, 5);
                float blade = 0.55f * s1 + 0.45f * s2, fine = Hash(x, y, 7);
                float mid = Fbm(u, v, 8, 3, 11), tuft = Fbm(u, v, 24, 2, 13);
                float k = 0.62f + 0.55f * blade + 0.22f * (fine - 0.5f) + 0.25f * (mid - 0.5f);
                if (fine > 0.94f) k += 0.35f;               // sunlit tips
                if (tuft > 0.66f) k *= 0.78f;               // dense dark tufts
                float hue = Fbm(u, v, 4, 2, 17) - 0.5f;
                var c = new Color(k * (1f + 0.5f * hue), k, k * (1f - 0.6f * hue));
                if (Fbm(u, v, 32, 2, 19) > 0.70f) c = new Color(c.r * 0.82f, c.g * 0.95f, c.b * 1.05f); // clover
                return c;
            }));
        // sand / dust / grit (desert circuits, the paved ground of street circuits)
        return Count(Albedo(256, 256, albedo, (u, v, x, y) =>
        {
            float g = Fbm(u, v, 16, 4, 23), fine = Hash(x, y, 29), r = Hash(x >> 1, y >> 1, 31);
            float k = 0.8f + 0.35f * (g - 0.5f) + 0.18f * (fine - 0.5f);
            if (r > 0.96f) k += 0.25f; else if (r < 0.03f) k -= 0.3f;
            float ripple = 0.06f * Mathf.Sin((u * 6 + Fbm(u, v, 4, 2, 37) * 2f) * Mathf.PI * 2 * 4);
            return new Color(k + ripple, k + ripple * 0.9f, k * 0.97f + ripple * 0.7f);
        }));
    }

    static Texture2D cMacro;
    public static Texture2D Macro() => cMacro != null ? cMacro : (cMacro = MakeMacro());
    static Texture2D MakeMacro()
    {
        var px = new Color32[256 * 256];
        for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
        {
            float u = x / 256f, v = y / 256f;
            float bright = Fbm(u, v, 4, 4, 41);
            float dry = Mathf.SmoothStep(0, 1, (Fbm(u, v, 3, 4, 43) - 0.56f) / 0.12f) * (0.6f + 0.4f * Fbm(u, v, 24, 2, 45));
            float clump = Fbm(u, v, 6, 3, 47);
            px[y * 256 + x] = new Color32(B(bright), B(dry), B(clump), 255);
        }
        return Count(Make(256, 256, px, true));
    }

    static Texture2D cGravel;
    public static Texture2D Gravel() => cGravel != null ? cGravel : (cGravel = MakeGravel());
    static Texture2D MakeGravel()
    {
        return Count(Albedo(256, 256, new Color(0.50f, 0.45f, 0.36f), (u, v, x, y) =>
        {
            // jittered cells (20 per tile = 12 cm stones), nearest and second-nearest for the gaps
            const int C = 20; float fx = u * C, fy = v * C; int ci = Mathf.FloorToInt(fx), cj = Mathf.FloorToInt(fy);
            float d1 = 9, d2 = 9; int best = 0;
            for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++)
            {
                int gi = ((ci + i) % C + C) % C, gj = ((cj + j) % C + C) % C;
                float px = ci + i + 0.15f + 0.7f * Hash(gi, gj, 51), py = cj + j + 0.15f + 0.7f * Hash(gi, gj, 53);
                float dx = fx - px, dy = fy - py, d = dx * dx + dy * dy;
                if (d < d1) { d2 = d1; d1 = d; best = gi * 97 + gj; } else if (d < d2) d2 = d;
            }
            float edge = Mathf.Sqrt(d2) - Mathf.Sqrt(d1);              // 0 at the gap between two stones
            float tone = 0.75f + 0.5f * Hash(best, 0, 55), warm = Hash(best, 1, 57) - 0.5f;
            float shade = Mathf.SmoothStep(0.25f, 1f, edge / 0.22f);  // rounded: darker at the rims
            float grit = 0.88f + 0.24f * Hash(x, y, 59);
            float k = tone * (0.35f + 0.65f * shade) * grit;
            if (Hash(best, 2, 61) > 0.9f) k *= 1.35f;                  // a few white stones
            return new Color(k * (1f + 0.25f * warm), k, k * (1f - 0.3f * warm));
        }));
    }

    static Texture2D cTurf;
    public static Texture2D Turf() => cTurf != null ? cTurf : (cTurf = MakeTurf());
    static Texture2D MakeTurf()
    {
        var px = new Color32[128 * 128];
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
        {
            float u = x / 128f, v = y / 128f;
            bool band = ((int)(v * 4)) % 2 == 0;
            float fib = 0.86f + 0.2f * VN(u * 0.4f, v, 64, 71) + 0.1f * Hash(x, y, 73);
            float k = (band ? 0.86f : 1f) * fib * (0.95f + 0.1f * Fbm(u, v, 8, 2, 75));
            bool line = u < 0.03f || u > 0.97f;     // painted white lines along both edges
            px[y * 128 + x] = line ? new Color32(S(0.80f * fib), S(0.80f * fib), S(0.78f * fib), 255)
                                   : new Color32(S(0.035f * k), S(0.17f * k), S(0.05f * k), 255);
        }
        return Count(Make(128, 128, px));
    }

    static Texture2D cPaving;
    public static Texture2D Paving() => cPaving != null ? cPaving : (cPaving = MakePaving());
    static Texture2D MakePaving()
    {
        return Count(Albedo(256, 256, new Color(0.46f, 0.45f, 0.42f), (u, v, x, y) =>
        {
            float sx = u * 4, sy = v * 7; int i = Mathf.FloorToInt(sx), j = Mathf.FloorToInt(sy);
            float ex = Mathf.Min(sx - i, 1 - (sx - i)) * 4f * 64f / 4f, ey = Mathf.Min(sy - j, 1 - (sy - j)) * 256f / 7f;
            float joint = Mathf.Clamp01(Mathf.Min(ex, ey) / 1.6f);
            float slab = 0.9f + 0.2f * Hash(i, j, 81), stain = Fbm(u, v, 8, 3, 83);
            float k = slab * (0.85f + 0.3f * stain) * (0.9f + 0.1f * Hash(x, y, 85)) * (0.55f + 0.45f * joint);
            return new Color(k, k * 0.99f, k * 0.95f);
        }));
    }

    static Texture2D cStone;
    public static Texture2D Stone() => cStone != null ? cStone : (cStone = MakeStone());
    static Texture2D MakeStone()
    {
        return Count(Albedo(256, 256, new Color(0.47f, 0.43f, 0.37f), (u, v, x, y) =>
        {
            int row = Mathf.FloorToInt(v * 8); float off = (row % 2) * 0.5f;
            float sx = u * 4 + off; int i = Mathf.FloorToInt(sx);
            float ex = Mathf.Min(sx - i, 1 - (sx - i)) * 64f, ey = Mathf.Min(v * 8 - row, 1 - (v * 8 - row)) * 32f;
            float joint = Mathf.Clamp01(Mathf.Min(ex, ey) / 2f);
            float blk = 0.85f + 0.3f * Hash(((i % 4) + 4) % 4, row, 91), rough = Fbm(u, v, 16, 3, 93);
            float k = blk * (0.8f + 0.4f * rough) * (0.5f + 0.5f * joint);
            return new Color(k, k * 0.97f, k * 0.92f);
        }));
    }

    // ---------------------------------------------------------------- road: fine aggregate (Road.shader HQ, 1.4 m per tile)
    // Linear data, every channel 0.5 = neutral (so a missing texture changes nothing and the mips fade it out evenly):
    //   R albedo around the mean (pale and dark stones, black binder and pores between them)
    //   G polish of the stone tops (tyres wear them smooth: these glint), BA slope of the surface (x, z), 0.5 = flat
    // Stones are jittered ellipses, ~3 cm cells (48 per tile, ~5 texels each): flat worn tops with a little tilt of
    // their own, rounded rims that slope away.
    static Texture2D cRoadFine;
    public static Texture2D RoadFine() => cRoadFine != null ? cRoadFine : (cRoadFine = MakeRoadFine());
    static Texture2D MakeRoadFine()
    {
        const int N = 256, C = 48;
        var alb = new float[N * N]; var px = new Color32[N * N]; double sum = 0;
        var gl = new float[N * N]; var sx = new float[N * N]; var sz = new float[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            float fx = (x + 0.5f) / N * C, fy = (y + 0.5f) / N * C; int ci = Mathf.FloorToInt(fx), cj = Mathf.FloorToInt(fy);
            float best = 9f, bdx = 0, bdy = 0; int bi = 0, bj = 0;
            for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++)
            {
                int gi = ((ci + i) % C + C) % C, gj = ((cj + j) % C + C) % C;
                if (Hash(gi, gj, 201) < 0.1f) continue;                  // a cell of plain binder
                float cx = ci + i + 0.25f + 0.5f * Hash(gi, gj, 203), cy = cj + j + 0.25f + 0.5f * Hash(gi, gj, 205);
                float rad = 0.30f + 0.24f * Hash(gi, gj, 207), ang = Hash(gi, gj, 209) * Mathf.PI, e = 0.65f + 0.35f * Hash(gi, gj, 211);
                float dx = fx - cx, dy = fy - cy, ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                float u = dx * ca + dy * sa, w = (-dx * sa + dy * ca) / e, d = Mathf.Sqrt(u * u + w * w) / rad;
                if (d < best) { best = d; bdx = dx; bdy = dy; bi = gi; bj = gj; }
            }
            int k = y * N + x; float a, g, slx = 0, slz = 0;
            if (best < 1f)
            {
                // a stone: tone of its own (mostly mid grey, some pale, a few near black), darker at the rim
                float tone = Hash(bi, bj, 213), rim = Mathf.Clamp01((best - 0.7f) / 0.3f);
                a = tone > 0.85f ? 0.85f : tone < 0.15f ? 0.30f : 0.48f + 0.25f * tone;
                a *= 1f - 0.3f * rim * rim;
                // worn flat top with its own tilt; the rim slopes away from the centre
                float tx = (Hash(bi, bj, 215) - 0.5f) * 0.5f, tz = (Hash(bi, bj, 217) - 0.5f) * 0.5f;
                float l = Mathf.Max(1e-4f, Mathf.Sqrt(bdx * bdx + bdy * bdy)), out1 = 1.2f * rim;
                slx = tx * (1f - rim) + bdx / l * out1; slz = tz * (1f - rim) + bdy / l * out1;
                g = (1f - rim) * (0.35f + 0.65f * Hash(bi, bj, 219));
            }
            else
            {
                // binder (bitumen mastic) with fine sand and the odd pore
                float n = Hash(x, y, 221);
                a = 0.20f + 0.08f * n; g = 0.12f;
                if (Hash(x >> 1, y >> 1, 223) > 0.9f) { a = 0.08f; g = 0f; }
                slx = (Hash(x, y, 225) - 0.5f) * 0.4f; slz = (Hash(x, y, 227) - 0.5f) * 0.4f;
            }
            alb[k] = a; gl[k] = g; sx[k] = slx; sz[k] = slz; sum += a;
        }
        float mean = (float)(sum / (N * N)), gmean = 0;
        for (int k = 0; k < gl.Length; k++) gmean += gl[k];
        gmean /= gl.Length;
        for (int k = 0; k < px.Length; k++)
            px[k] = new Color32(B(0.5f + (alb[k] - mean) * 0.9f), B(0.5f + (gl[k] - gmean)), B(0.5f + 0.5f * Mathf.Clamp(sx[k], -1f, 1f)), B(0.5f + 0.5f * Mathf.Clamp(sz[k], -1f, 1f)));
        return Count(Make(N, N, px, true, 4));
    }

    // one bay of a town house: plaster, a window with a white frame, glass reflecting the sky (lighter at the top),
    // a sill with its shadow, a lintel shadow and a faint string course at floor level. Texel (0, 0) is plain plaster,
    // so faces mapped to uv (0, 0) (cornices, parapets) come out as plaster.
    static Texture2D facade;
    public static Texture2D Facade()
    {
        if (facade != null) return facade;
        const int N = 128; var px = new Color32[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            float u = (x + 0.5f) / N, v = (y + 0.5f) / N;
            float pl = 0.86f + 0.08f * Fbm(u, v, 8, 3, 101) + 0.04f * Hash(x, y, 103);
            Color c = new Color(pl, pl, pl * 0.98f);
            if (v < 0.035f) c *= 0.86f;                                          // string course
            bool inF = u > 0.27f && u < 0.73f && v > 0.24f && v < 0.84f, inG = u > 0.31f && u < 0.69f && v > 0.28f && v < 0.80f;
            if (u > 0.25f && u < 0.75f && v > 0.19f && v < 0.24f) c = new Color(0.95f, 0.95f, 0.93f); // sill
            else if (u > 0.25f && u < 0.75f && v > 0.15f && v <= 0.19f) c *= 0.62f;                     // under the sill
            if (u > 0.27f && u < 0.73f && v >= 0.84f && v < 0.89f) c *= 0.7f;                          // lintel shadow
            if (inF) c = new Color(0.93f, 0.93f, 0.91f);
            if (inG)
            {
                float t = (v - 0.28f) / 0.52f;
                c = Color.Lerp(new Color(0.20f, 0.27f, 0.34f), new Color(0.55f, 0.66f, 0.76f), t * t);  // sky in the glass
                if (Mathf.Abs(u - 0.5f) < 0.012f || Mathf.Abs(v - 0.60f) < 0.012f) c = new Color(0.9f, 0.9f, 0.88f); // glazing bars
                if (u + v * 0.6f > 0.85f && u + v * 0.6f < 0.9f) c += new Color(0.12f, 0.12f, 0.12f);  // a streak of reflection
            }
            // side shutters
            if ((u > 0.13f && u < 0.26f || u > 0.74f && u < 0.87f) && v > 0.25f && v < 0.83f)
                c = new Color(0.5f, 0.5f, 0.48f) * ((y / 3) % 2 == 0 ? 0.85f : 1f);
            px[y * N + x] = new Color32(B(c.r), B(c.g), B(c.b), 255);
        }
        return facade = Count(Make(N, N, px));
    }

    // ---------------------------------------------------------------- real-world buildings (RealWorld.cs)
    // Atlas 512 x 512: 4 x 4 cells of 128 px, each one tile that repeats over a wall or roof (Baked.shader F2_ATLAS
    // wraps inside the cell; uv2 = the cell). Walls are pale and neutral where the plaster is, so the building's colour
    // rides in the vertex tint (BakeTint); brick and stone carry their own colour.
    //   0 town house bay with shutters   1 plain house bay        2 brick house bay        3 office curtain wall
    //   4 industrial cladding             5 block of flats bay     6 glass tower bay        7 shed / garage cladding
    //   8 roof tiles                      9 flat roof membrane    10 standing-seam metal   11 stone with an arched window
    //  12 precast concrete panels        13 plain plaster (parapets, gables)   14, 15 plaster
    static Texture2D bAtlas;
    public static Texture2D BuildingAtlas()
    {
        if (bAtlas != null) return bAtlas;
        const int C = 128, N = 512; var px = new Color32[N * N];
        for (int cell = 0; cell < 16; cell++)
        {
            int ox0 = (cell % 4) * C, oy0 = (cell / 4) * C;
            for (int y = 0; y < C; y++) for (int x = 0; x < C; x++)
            {
                float u = (x + 0.5f) / C, v = (y + 0.5f) / C;
                px[(oy0 + y) * N + ox0 + x] = Col(BCell(cell, u, v, x, y, ox0, oy0));
            }
        }
        var t = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        t.SetPixels32(px); t.Apply(true, true);
        return bAtlas = Count(t);
    }
    static Color32 Col(Color c) => new Color32(B(c.r), B(c.g), B(c.b), 255);
    static Color Plaster(float u, float v, int x, int y, int s) { float p = 0.86f + 0.08f * Fbm(u, v, 8, 3, s) + 0.04f * Hash(x, y, s + 2); return new Color(p, p, p * 0.98f); }
    // glass reflecting the sky: darker low down, lighter at the top, a streak of reflection
    static Color Glass(float t, float u, float v)
    {
        var c = Color.Lerp(new Color(0.20f, 0.27f, 0.34f), new Color(0.55f, 0.66f, 0.76f), t * t);
        if (u + v * 0.6f > 0.85f && u + v * 0.6f < 0.9f) c += new Color(0.1f, 0.1f, 0.1f);
        return c;
    }
    // a framed window in [u0, u1] x [v0, v1], with a sill and a lintel shadow; false outside it
    static bool Window(ref Color c, float u, float v, float u0, float u1, float v0, float v1)
    {
        if (u > u0 - 0.02f && u < u1 + 0.02f && v > v0 - 0.05f && v <= v0 - 0.01f) { c = new Color(0.95f, 0.95f, 0.93f); return true; } // sill
        if (u > u0 - 0.02f && u < u1 + 0.02f && v > v0 - 0.09f && v <= v0 - 0.05f) { c *= 0.62f; return true; }                   // under the sill
        if (u > u0 && u < u1 && v >= v1 && v < v1 + 0.05f) { c *= 0.72f; return true; }                                             // lintel shadow
        if (u < u0 || u > u1 || v < v0 || v > v1) return false;
        if (u < u0 + 0.035f || u > u1 - 0.035f || v < v0 + 0.035f || v > v1 - 0.035f) { c = new Color(0.93f, 0.93f, 0.91f); return true; } // frame
        float t = (v - v0) / (v1 - v0);
        c = Glass(t, u, v);
        if (Mathf.Abs(u - (u0 + u1) * 0.5f) < 0.012f || Mathf.Abs(t - 0.6f) < 0.02f) c = new Color(0.9f, 0.9f, 0.88f); // glazing bars
        return true;
    }
    static Color BCell(int cell, float u, float v, int x, int y, int ox0, int oy0)
    {
        int s = 300 + cell * 13;
        switch (cell)
        {
            case 0:
            {
                var c = Plaster(u, v, x, y, s);
                if (v < 0.035f) c *= 0.86f; // string course
                if ((u > 0.13f && u < 0.26f || u > 0.74f && u < 0.87f) && v > 0.25f && v < 0.83f)
                    return new Color(0.5f, 0.5f, 0.48f) * ((y / 3) % 2 == 0 ? 0.85f : 1f); // shutters
                Window(ref c, u, v, 0.27f, 0.73f, 0.24f, 0.84f);
                return c;
            }
            case 1:
            {
                var c = Plaster(u, v, x, y, s);
                Window(ref c, u, v, 0.3f, 0.7f, 0.3f, 0.82f);
                return c;
            }
            case 2:
            {
                // stretcher bond: 16 courses, bricks 32 px long; light mortar
                int row = y / 8; int bx = (x + (row % 2) * 16) / 32;
                bool mortar = y % 8 == 0 || (x + (row % 2) * 16) % 32 == 0;
                float h = Hash(bx, row, s), k = 0.85f + 0.3f * h;
                var c = mortar ? new Color(0.72f, 0.70f, 0.66f) : new Color(0.56f * k, 0.30f * k, 0.22f * k) * (0.92f + 0.12f * Hash(x, y, s + 1));
                Window(ref c, u, v, 0.3f, 0.7f, 0.3f, 0.8f);
                return c;
            }
            case 3:
            {
                // curtain wall: spandrel panel at the floor line, glass above, aluminium mullions and transom
                if (x < 3 || x > 124 || (v > 0.25f && v < 0.28f)) return new Color(0.72f, 0.74f, 0.76f);
                if (v <= 0.25f) return new Color(0.34f, 0.37f, 0.41f) * (0.95f + 0.1f * Fbm(u, v, 8, 2, s));
                return Glass((v - 0.28f) / 0.72f, u, v) * 0.9f;
            }
            case 4:
            {
                // ribbed steel cladding, a high strip window, concrete plinth
                if (v < 0.08f) return new Color(0.62f, 0.61f, 0.58f) * (0.9f + 0.15f * Hash(x, y, s));
                if (v > 0.78f && v < 0.86f) return x % 32 < 2 ? new Color(0.7f, 0.7f, 0.7f) : Glass((v - 0.78f) / 0.08f, u, v) * 0.85f;
                float rib = (x % 8) < 2 ? 1.08f : (x % 8) < 4 ? 0.9f : 1f;
                float k = rib * (0.95f + 0.06f * Fbm(u, v, 4, 2, s)) * (v < 0.2f ? 0.93f : 1f);
                return new Color(0.84f * k, 0.84f * k, 0.82f * k);
            }
            case 5:
            {
                var c = Plaster(u, v, x, y, s) * 0.97f;
                if (v > 0.12f && v < 0.19f) return new Color(0.93f, 0.93f, 0.91f);       // balcony slab
                if (v >= 0.06f && v <= 0.12f) return c * 0.6f;                           // its shadow
                Window(ref c, u, v, 0.18f, 0.82f, 0.36f, 0.86f);
                if (v >= 0.19f && v < 0.36f && (x % 6 < 1 || v > 0.33f)) c = new Color(0.45f, 0.46f, 0.47f); // railing
                return c;
            }
            case 6:
            {
                if (v < 0.1f) return new Color(0.88f, 0.88f, 0.87f) * (v < 0.03f ? 0.8f : 1f); // floor slab
                if (x % 32 < 2) return new Color(0.62f, 0.64f, 0.66f);                      // mullion
                return Glass((v - 0.1f) / 0.9f, u, v) * 0.95f;
            }
            case 7:
            {
                float rib = (x % 6) < 2 ? 1.1f : 0.95f;
                float k = rib * (0.9f + 0.1f * Fbm(u, v, 4, 2, s)) * (v < 0.1f ? 0.82f : 1f);
                return new Color(0.80f * k, 0.80f * k, 0.78f * k);
            }
            case 8:
            {
                // clay tiles: 8 courses, each lit at the top and shaded where the next course overlaps
                int row = y / 16; float f = (y % 16) / 16f; int tx = (x + (row % 2) * 8) / 16;
                float k = (0.8f + 0.25f * f) * (0.88f + 0.2f * Hash(tx, row, s)) * (f < 0.12f ? 0.65f : 1f) * ((x + (row % 2) * 8) % 16 == 0 ? 0.8f : 1f);
                return new Color(0.86f * k, 0.86f * k, 0.86f * k);
            }
            case 9:
            {
                float k = 0.78f + 0.1f * Fbm(u, v, 8, 3, s) + 0.06f * Hash(x, y, s);
                if (x % 64 == 0 || y % 64 == 0) k *= 0.85f;
                return new Color(k, k * 0.99f, k * 0.97f);
            }
            case 10:
            {
                float k = (x % 16 < 2 ? 1.1f : 0.95f) * (0.95f + 0.06f * Fbm(u, v, 4, 2, s));
                return new Color(0.82f * k, 0.83f * k, 0.84f * k);
            }
            case 11:
            {
                int row = y / 16; int bx = (x + (row % 2) * 20) / 40;
                bool joint = y % 16 == 0 || (x + (row % 2) * 20) % 40 == 0;
                float k = (0.82f + 0.18f * Hash(bx, row, s)) * (0.92f + 0.1f * Fbm(u, v, 16, 2, s + 1));
                var c = joint ? new Color(0.6f, 0.58f, 0.54f) : new Color(0.86f * k, 0.82f * k, 0.74f * k);
                // tall window with a round top
                float du = (u - 0.5f) / 0.13f, dv = (v - 0.62f) / 0.13f;
                bool win = (Mathf.Abs(u - 0.5f) < 0.13f && v > 0.18f && v < 0.62f) || (v >= 0.62f && du * du + dv * dv < 1f);
                if (win) c = Glass((v - 0.18f) / 0.57f, u, v) * 0.8f;
                return c;
            }
            case 12:
            {
                float k = 0.8f + 0.08f * Fbm(u, v, 8, 3, s) + 0.04f * Hash(x, y, s);
                if (y % 32 < 2 || x % 64 < 1) k *= 0.8f;
                return new Color(k, k * 0.99f, k * 0.96f);
            }
            case 14:
            {
                // grandstand rows with a seated crowd: 4 rows x 16 people per tile (RealWorld's map grandstands), many red shirts
                int row = y / 32, ry = y % 32, man = x / 8, mx = x % 8;
                var c = new Color(0.56f, 0.56f, 0.55f) * (0.93f + 0.07f * Hash(x, y, s));
                if (ry < 3) return c * 0.62f;  // riser in shadow
                if (ry < 11) return c;         // tread
                if (Hash(man, row, s + 5) < 0.08f) return ry < 19 && mx > 0 && mx < 7 ? new Color(0.18f, 0.30f, 0.58f) : c * 0.75f; // empty seat
                float hc = Hash(man, row, s + 7);
                Color shirt = hc < 0.42f ? new Color(0.78f, 0.10f, 0.08f) : hc < 0.6f ? new Color(0.92f, 0.92f, 0.90f) : hc < 0.68f ? new Color(0.95f, 0.80f, 0.15f)
                            : hc < 0.8f ? new Color(0.16f, 0.24f, 0.46f) : hc < 0.9f ? new Color(0.13f, 0.13f, 0.14f) : new Color(0.46f, 0.52f, 0.56f);
                int top = 23 + (int)(Hash(man, row, s + 9) * 3f);
                if (ry < top) return mx >= 1 && mx <= 6 ? shirt * (mx == 1 || mx == 6 ? 0.8f : 1f) : c * 0.7f;
                if (ry < top + 5 && mx >= 2 && mx <= 5)
                {
                    if (ry >= top + 3 && Hash(man, row, s + 11) < 0.5f) return Hash(man, row, s + 13) < 0.4f ? new Color(0.75f, 0.1f, 0.08f) : new Color(0.2f, 0.16f, 0.12f); // cap / hair
                    return Hash(man, row, s + 15) < 0.8f ? new Color(0.86f, 0.67f, 0.54f) : new Color(0.55f, 0.38f, 0.27f);
                }
                return c * 0.7f;
            }
            default: return Plaster(u, v, x, y, s);
        }
    }

    // ---------------------------------------------------------------- real-world ground (Baked.shader F2_LAND)
    // LandDetail 256, linear data, 4.5 m per tile, every channel around 0.5: R grass blades, G paving grain, B rough
    // ground (furrows and clods, forest litter). The land colour map chooses between them per texel.
    static Texture2D cLandDetail;
    public static Texture2D LandDetail()
    {
        if (cLandDetail != null) return cLandDetail;
        const int N = 256; var r = new float[N * N]; var g = new float[N * N]; var b = new float[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            float u = x / (float)N, v = y / (float)N; int k = y * N + x;
            float s1 = VN(u, v * 0.25f + u * 0.1f, 96, 3), s2 = VN(u * 0.3f + v * 0.2f, v, 96, 5);
            float fine = Hash(x, y, 7), blade = 0.62f + 0.55f * (0.55f * s1 + 0.45f * s2) + 0.22f * (fine - 0.5f) + 0.25f * (Fbm(u, v, 8, 3, 11) - 0.5f);
            if (fine > 0.94f) blade += 0.3f;
            if (Fbm(u, v, 24, 2, 13) > 0.66f) blade *= 0.8f;
            r[k] = blade;
            float pv = 0.8f + 0.3f * (Fbm(u, v, 16, 4, 23) - 0.5f) + 0.18f * (Hash(x, y, 29) - 0.5f);
            float st = Hash(x >> 1, y >> 1, 31); if (st > 0.96f) pv += 0.22f; else if (st < 0.03f) pv -= 0.25f;
            if (Mathf.Abs(Fbm(u, v, 6, 3, 33) - 0.5f) < 0.012f) pv -= 0.25f; // hairline cracks
            g[k] = pv;
            float fur = 0.5f + 0.5f * Mathf.Sin((v * 5f + 0.08f * Fbm(u, v, 4, 2, 35)) * Mathf.PI * 2f);
            b[k] = 0.7f + 0.3f * fur * (0.6f + 0.4f * Fbm(u, v, 12, 2, 37)) + 0.25f * (Fbm(u, v, 24, 3, 39) - 0.5f) + 0.15f * (Hash(x, y, 41) - 0.5f);
        }
        var px = new Color32[N * N];
        float mr = Mean(r), mg = Mean(g), mb = Mean(b);
        for (int k = 0; k < px.Length; k++)
            px[k] = new Color32(B(0.5f + (r[k] - mr) * 0.75f), B(0.5f + (g[k] - mg) * 0.9f), B(0.5f + (b[k] - mb) * 0.85f), 128);
        return cLandDetail = Count(Make(N, N, px, true, 4));
    }
    static float Mean(float[] a) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i]; return (float)(s / a.Length); }

    // LandMacro 256, linear, ~48 m per tile (turned 37 degrees in the shader): R slow brightness drift, G tree crowns
    // seen from above (round, lit from one side, dark gaps), for the wooded parts of the land.
    static Texture2D cLandMacro;
    public static Texture2D LandMacro()
    {
        if (cLandMacro != null) return cLandMacro;
        const int N = 256, Cc = 8; var px = new Color32[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            float u = x / (float)N, v = y / (float)N;
            float bright = Fbm(u, v, 4, 3, 51);
            float fx = u * Cc, fy = v * Cc; int ci = Mathf.FloorToInt(fx), cj = Mathf.FloorToInt(fy);
            float crown = 0.16f + 0.06f * Hash(x, y, 53);
            for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++)
            {
                int gi = ((ci + i) % Cc + Cc) % Cc, gj = ((cj + j) % Cc + Cc) % Cc;
                float cx = ci + i + 0.2f + 0.6f * Hash(gi, gj, 55), cy = cj + j + 0.2f + 0.6f * Hash(gi, gj, 57), rad = 0.48f + 0.25f * Hash(gi, gj, 59);
                float dx = (fx - cx) / rad, dy = (fy - cy) / rad, d2 = dx * dx + dy * dy;
                if (d2 >= 1f) continue;
                float nz = Mathf.Sqrt(1f - d2), lit = Mathf.Clamp01(0.55f + 0.45f * (0.5f * dx + 0.6f * dy + 0.62f * nz));
                float leafy = 0.85f + 0.3f * Hash(x >> 1, y >> 1, 61 + gi);
                crown = Mathf.Max(crown, (0.3f + 0.75f * lit) * leafy * (0.85f + 0.3f * Hash(gi, gj, 63)));
            }
            px[y * N + x] = new Color32(B(bright), B(crown * 0.62f), 128, 255);
        }
        return cLandMacro = Count(Make(N, N, px, true, 2));
    }

    // RoadAtlas 256 x 256, four 64 px columns across a ribbon (u), one period along it (v):
    //   0 major road: asphalt, edge lines, dashed centre line   1 minor road: plain worn asphalt
    //   2 path: pale gravel / paving                             3 railway: ballast, sleepers, two rails
    static Texture2D cRoads;
    public static Texture2D RoadAtlas()
    {
        if (cRoads != null) return cRoads;
        const int N = 256; var px = new Color32[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            int col = x / 64, cx = x % 64; float u = x / (float)N, v = y / (float)N;
            float grain = 0.9f + 0.14f * Fbm(u, v, 32, 2, 71) + 0.1f * (Hash(x, y, 73) - 0.5f);
            Color c;
            if (col == 0)
            {
                c = new Color(0.25f, 0.25f, 0.26f) * grain;
                if ((cx >= 3 && cx <= 4) || (cx >= 59 && cx <= 60)) c = new Color(0.82f, 0.82f, 0.8f) * (0.9f + 0.1f * grain);
                if (cx >= 31 && cx <= 32 && y < 102) c = new Color(0.84f, 0.84f, 0.82f) * (0.9f + 0.1f * grain);
            }
            else if (col == 1)
            {
                c = new Color(0.29f, 0.29f, 0.29f) * grain * (0.94f + 0.12f * Fbm(u, v, 4, 3, 75));
                if (cx < 3 || cx > 60) c *= 0.8f;
            }
            else if (col == 2)
            {
                c = new Color(0.56f, 0.53f, 0.47f) * grain * (0.92f + 0.12f * Fbm(u, v, 8, 2, 77));
                if (cx < 2 || cx > 61) c *= 0.85f;
            }
            else
            {
                float st = 0.85f + 0.3f * Hash(x >> 1, y >> 1, 79);
                c = new Color(0.43f, 0.41f, 0.38f) * st * grain;
                if (y % 16 < 6 && cx >= 8 && cx <= 56) c = new Color(0.46f, 0.43f, 0.40f) * grain;    // sleepers
                if (cx == 19 || cx == 20 || cx == 44 || cx == 45) c = new Color(0.30f, 0.29f, 0.28f); // rail web
                if (cx == 19 || cx == 45) c = new Color(0.62f, 0.62f, 0.63f);                         // polished head
            }
            px[y * N + x] = Col(c);
        }
        var t = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        t.SetPixels32(px); t.Apply(true, true);
        return cRoads = Count(t);
    }

    // ---------------------------------------------------------------- tree cards
    public enum Card { Spruce = 0, Broadleaf = 1, Pine = 2, Palm = 3 }
    // uv rect of each card in the atlas (u0, v0, u1, v1), a half-texel in so neighbours never bleed
    public static Vector4 CardRect(Card c)
    {
        int k = (int)c; float u0 = (k % 2) * 0.5f, v0 = (k / 2) * 0.5f, e = 1f / 512f;
        return new Vector4(u0 + e, v0 + e, u0 + 0.5f - e, v0 + 0.5f - e);
    }
    // mean leaf colour (linear) painted into each card, so circuits can tint relative to it
    public static readonly Color[] CardLeaf = new Color[4];

    const int AN = 512, CN = 256;
    static float[] cr, cg, cb, ca;
    static System.Random rnd;
    static float R01() => (float)rnd.NextDouble();

    static void Put(int x, int y, Color c, float a)
    {
        if (x < 0 || y < 0 || x >= CN || y >= CN) return;
        int i = (oy + y) * AN + ox + x;
        cr[i] = Mathf.Lerp(cr[i], c.r, a); cg[i] = Mathf.Lerp(cg[i], c.g, a); cb[i] = Mathf.Lerp(cb[i], c.b, a); ca[i] = Mathf.Max(ca[i], a);
    }
    static int ox, oy;
    // a leaf cluster: a disc with a ragged rim and holes, lit from above (lighter top, darker underside)
    static void Leaves(float cx, float cy, float r, Color col, float holes = 0.12f)
    {
        int x0 = Mathf.FloorToInt(cx - r - 1), x1 = Mathf.CeilToInt(cx + r + 1), y0 = Mathf.FloorToInt(cy - r - 1), y1 = Mathf.CeilToInt(cy + r + 1);
        int seed = rnd.Next(1000);
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
        {
            float dx = x - cx, dy = y - cy, d = Mathf.Sqrt(dx * dx + dy * dy) / r;
            float ang = Mathf.Atan2(dy, dx);
            float rim = 0.78f + 0.22f * Hash(Mathf.FloorToInt((ang + 3.2f) * 4f), seed, 111);
            if (d > rim) continue;
            if (d > 0.45f && Hash(x, y, seed) < holes) continue;
            float lit = 0.72f + 0.42f * (dy / r * 0.5f + 0.5f) - 0.18f * d * d;
            lit *= 0.85f + 0.3f * Hash(x >> 1, y >> 1, seed + 3);
            Put(x, y, col * lit, 1f);
        }
    }
    // a tapering limb from (x0, y0) to (x1, y1)
    static void Limb(float x0, float y0, float x1, float y1, float w0, float w1, Color col)
    {
        float len = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)); int n = Mathf.Max(2, Mathf.CeilToInt(len * 2));
        for (int s = 0; s <= n; s++)
        {
            float t = (float)s / n, x = Mathf.Lerp(x0, x1, t), y = Mathf.Lerp(y0, y1, t), w = Mathf.Lerp(w0, w1, t) * 0.5f;
            for (int yy = Mathf.FloorToInt(y - w); yy <= Mathf.CeilToInt(y + w); yy++) for (int xx = Mathf.FloorToInt(x - w); xx <= Mathf.CeilToInt(x + w); xx++)
            {
                float dx = xx - x; if (Mathf.Abs(dx) > w || Mathf.Abs(yy - y) > w) continue;
                float lit = 0.75f + 0.45f * (dx / Mathf.Max(w, 0.5f) * 0.5f + 0.5f); // round: one side catches the light
                float bark = 0.85f + 0.3f * Hash(xx, yy >> 1, 131);
                Put(xx, yy, col * lit * bark, 1f);
            }
        }
    }

    static Texture2D foliage;
    public static Texture2D Foliage()
    {
        if (foliage != null) return foliage;
        cr = new float[AN * AN]; cg = new float[AN * AN]; cb = new float[AN * AN]; ca = new float[AN * AN];
        rnd = new System.Random(5);
        Color bark = new Color(0.20f, 0.14f, 0.10f), barkGrey = new Color(0.30f, 0.27f, 0.24f), palmBark = new Color(0.36f, 0.30f, 0.22f);

        // spruce: a straight trunk, drooping tiers of needle sprays that shorten towards the spire
        ox = 0; oy = 0;
        {
            Color dark = new Color(0.045f, 0.12f, 0.06f), lite = new Color(0.08f, 0.20f, 0.09f);
            Limb(128, 0, 128, 236, 9, 2, bark);
            for (float ty = 22; ty < 246; ty += 7f + R01() * 4f)
            {
                float f = (ty - 18f) / 232f, half = Mathf.Pow(1f - f, 0.95f) * 112f + 5f;
                for (int side = -1; side <= 1; side += 2)
                {
                    float len = half * (0.8f + 0.25f * R01()), droop = 10f + 16f * (1f - f);
                    for (float s = 0; s <= 1f; s += 0.07f)
                    {
                        float x = 128 + side * len * s, y = ty + 4f * s - droop * s * s;
                        float r = 3.5f + 7f * (1f - s) * (0.6f + 0.4f * (1f - f));
                        Leaves(x, y, r, Color.Lerp(dark, lite, 0.3f + 0.7f * s * R01()), 0.18f);
                    }
                }
                Leaves(128, ty + 2, 6 + 7 * (1 - f), dark, 0.05f);
            }
        }
        // broad-leaf: trunk and limbs, then a dome of leaf clusters (back ones darker, front ones lighter)
        ox = CN; oy = 0;
        {
            Limb(128, 0, 126, 110, 16, 9, barkGrey);
            Limb(126, 90, 84, 150, 8, 3, barkGrey); Limb(127, 95, 176, 156, 8, 3, barkGrey); Limb(126, 105, 132, 180, 7, 3, barkGrey);
            Color deep = new Color(0.07f, 0.16f, 0.05f), leaf = new Color(0.16f, 0.32f, 0.09f);
            for (int layer = 0; layer < 3; layer++)
                for (int k = 0; k < 46; k++)
                {
                    float a = R01() * 6.283f, rr = Mathf.Sqrt(R01());
                    float x = 128 + Mathf.Cos(a) * rr * 100f, y = 160 + Mathf.Sin(a) * rr * 80f;
                    if (y < 92) continue;
                    float hi = (y - 80f) / 170f;
                    var c = Color.Lerp(deep, leaf, Mathf.Clamp01(0.25f + 0.35f * layer + 0.4f * hi + 0.2f * (R01() - 0.5f)));
                    for (int q = 0; q < 5; q++) Leaves(x + (R01() - 0.5f) * 26f, y + (R01() - 0.5f) * 20f, 9f + R01() * 9f - layer * 1.5f, c, 0.14f);
                }
        }
        // umbrella pine: a tall, slightly bent bare trunk forking into a wide flat crown
        ox = 0; oy = CN;
        {
            Limb(128, 0, 122, 120, 13, 9, bark); Limb(122, 120, 128, 160, 9, 7, bark);
            Limb(126, 150, 70, 178, 7, 3, bark); Limb(126, 152, 190, 180, 7, 3, bark); Limb(128, 158, 128, 190, 6, 3, bark);
            Color deep = new Color(0.05f, 0.12f, 0.05f), leaf = new Color(0.12f, 0.24f, 0.08f);
            for (int layer = 0; layer < 2; layer++)
                for (int k = 0; k < 60; k++)
                {
                    float a = R01() * 6.283f, rr = Mathf.Sqrt(R01());
                    float x = 128 + Mathf.Cos(a) * rr * 112f, y = 192 + Mathf.Sin(a) * rr * 36f * (Mathf.Sin(a) > 0 ? 1f : 0.7f);
                    var c = Color.Lerp(deep, leaf, Mathf.Clamp01(0.3f + 0.4f * layer + (y - 170f) / 80f));
                    for (int q = 0; q < 4; q++) Leaves(x + (R01() - 0.5f) * 22f, y + (R01() - 0.5f) * 10f, 7f + R01() * 7f, c, 0.16f);
                }
        }
        // palm: a curved ringed trunk and a crown of arching fronds with leaflets
        ox = CN; oy = CN;
        {
            float px0 = 0, py0 = 0;
            for (int s = 0; s <= 40; s++)
            {
                float t = s / 40f, x = 120 + 18f * t * t, y = t * 178f;
                if (s > 0) Limb(px0, py0, x, y, Mathf.Lerp(12, 8, t), Mathf.Lerp(12, 8, t), palmBark * (s % 3 == 0 ? 0.75f : 1f));
                px0 = x; py0 = y;
            }
            Color frond = new Color(0.12f, 0.26f, 0.07f), frondLite = new Color(0.22f, 0.36f, 0.10f);
            float hx = 138, hy = 180;
            for (int f = 0; f < 11; f++)
            {
                float a = -0.2f + f * (3.54f / 10f) + (R01() - 0.5f) * 0.2f;   // fan from right round over the top to the left
                float dirx = Mathf.Cos(a), len = 95f + R01() * 25f;
                float qx = hx, qy = hy;
                for (int s = 1; s <= 28; s++)
                {
                    float t = s / 28f;
                    float x = hx + dirx * len * t, y = hy + (Mathf.Sin(a) * 0.55f + 0.25f) * len * t - 75f * t * t * (0.6f + 0.4f * Mathf.Abs(dirx));
                    Limb(qx, qy, x, y, 3f, 2f, frond * 0.8f);
                    // leaflets either side, hanging down
                    float ll = 22f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, t * 1.15f)) + 3f;
                    var lc = Color.Lerp(frond, frondLite, R01());
                    Limb(x, y, x - dirx * 4f + 3f, y - ll, 2.6f, 1.2f, lc);
                    Limb(x, y, x + dirx * 3f - 3f, y - ll * 0.9f, 2.6f, 1.2f, lc * 0.85f);
                    qx = x; qy = y;
                }
            }
            Leaves(hx, hy - 4, 9, new Color(0.18f, 0.12f, 0.06f), 0f); // the coconuts / frond bases
        }

        // mean leaf colour per card (for tinting), then dilate colour into the cut-out pixels
        for (int k = 0; k < 4; k++)
        {
            int x0 = (k % 2) * CN, y0 = (k / 2) * CN; double r = 0, g = 0, b = 0; int n = 0;
            for (int y = 0; y < CN; y++) for (int x = 0; x < CN; x++)
            {
                int i = (y0 + y) * AN + x0 + x; if (ca[i] < 0.5f || cg[i] < cr[i] * 1.2f) continue;
                r += cr[i]; g += cg[i]; b += cb[i]; n++;
            }
            CardLeaf[k] = n > 0 ? new Color((float)(r / n), (float)(g / n), (float)(b / n)) : new Color(0.1f, 0.2f, 0.1f);
            for (int y = 0; y < CN; y++) for (int x = 0; x < CN; x++)
            {
                int i = (y0 + y) * AN + x0 + x; if (ca[i] > 0) continue;
                cr[i] = CardLeaf[k].r * 0.8f; cg[i] = CardLeaf[k].g * 0.8f; cb[i] = CardLeaf[k].b * 0.8f;
            }
        }

        // mip chain by hand: colour averaged, alpha sharpened round the cut-off so each level keeps the full
        // level's coverage (otherwise distant trees fade to sticks)
        var tex = new Texture2D(AN, AN, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        int w = AN; float[] R = cr, G = cg, Bb = cb, A = ca; int mip = 0;
        while (true)
        {
            var px = new Color32[w * w];
            float sharp = 1f + 0.6f * mip;
            for (int i = 0; i < px.Length; i++)
            {
                float a = Mathf.Clamp01((A[i] - 0.5f) * sharp + 0.5f);
                px[i] = new Color32(S(R[i]), S(G[i]), S(Bb[i]), B(a));
            }
            tex.SetPixels32(px, mip);
            if (w == 1) break;
            int nw = w / 2; var nR = new float[nw * nw]; var nG = new float[nw * nw]; var nB = new float[nw * nw]; var nA = new float[nw * nw];
            for (int y = 0; y < nw; y++) for (int x = 0; x < nw; x++)
            {
                int i0 = (y * 2) * w + x * 2, i1 = i0 + 1, i2 = i0 + w, i3 = i2 + 1, o = y * nw + x;
                nR[o] = (R[i0] + R[i1] + R[i2] + R[i3]) * 0.25f; nG[o] = (G[i0] + G[i1] + G[i2] + G[i3]) * 0.25f; nB[o] = (Bb[i0] + Bb[i1] + Bb[i2] + Bb[i3]) * 0.25f;
                nA[o] = (A[i0] + A[i1] + A[i2] + A[i3]) * 0.25f;
            }
            R = nR; G = nG; Bb = nB; A = nA; w = nw; mip++;
        }
        tex.Apply(false, true);
        cr = cg = cb = ca = null;
        return foliage = Count(tex);
    }
}
