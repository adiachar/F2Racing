// EnvTex.cs — the scenery's surface textures, painted procedurally at load; where a CC0 photo is present in
// Resources/Textures (asphalt, grass, gravel, concrete, bark; for buildings plaster, brick, rooftiles, panels, cladding,
// corrugated, shutter; see CREDITS.txt) its real structure is used instead of / on top of the
// procedural detail (LoadPhoto), tinted to the circuit's palette. Missing photo = procedural fallback.
// All tile seamlessly and carry full mip chains; sizes are powers of two so they compress and filter cleanly.
//   GroundDetail  256  grass (blades, clover, tips) or, for sandy / paved circuits, fine sand and grit; mean colour = the
//                      circuit's ground albedo, so Environment's palettes still decide the look. 4.5 m per tile.
//   Macro         256  linear data: R brightness drift, G dry patches, B clumps (gravel ruts appear where B is high);
//                      read again at ~100 m per tile, turned, so the detail tile never shows a repeat.
//   Gravel        256  rounded pale stones of mixed size with dark gaps. 2.5 m per tile.
//   Turf          128  astroturf: fibre grain, two-tone painted bands, thin white edge lines.
//   Paving        256  light concrete slabs with joints and stains (street-circuit pavements).
//   Stone         256  coursed stone / concrete blocks (retaining walls).
//   RoadDetail    512  R8 linear, Road.shader HQ fine grain (1.6 m per tile), hand-made mips that fade it to neutral.
//   RoadMacro     256  linear data for Road.shader (48 m across x 192 m along): tone drift, mottle, resurfaced patches.
//   Facade        128  one window bay of a plastered town house: frame, sky-lit glass, sill, lintel shadow.
//   Foliage  1024x512  alpha-tested tree cards, a 4 x 2 atlas: spruce, round park tree, umbrella pine, palm, spreading
//                      oak, poplar and two forest-edge groves. Crowns are hundreds of small leaf clusters shaded as
//                      lobes of a volume (dark interior, light top and rim, sky holes at the edge). Colour is dilated into
//                      the cut-out areas and the mip chain keeps each card's coverage, so trees do not thin out or get
//                      dark fringes in the distance.
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
    // alpha: optional linear data in A (else 255)
    static Texture2D Albedo(int w, int h, Color mean, System.Func<float, float, int, int, Color> f, int aniso = 4, System.Func<float, float, int, int, float> alpha = null)
    {
        // load time: the per-pixel functions are pure math (noise, photo arrays; no Unity API), so the rows run in
        // parallel; the mean is still summed in pixel order, so the texture is bit-identical to the serial loop
        var c = new Color[w * h]; double sr = 0, sg = 0, sb = 0;
        FormulaSim.Par.For(0, h, y => { for (int x = 0; x < w; x++) c[y * w + x] = f((float)x / w, (float)y / h, x, y); });
        for (int i = 0; i < c.Length; i++) { var k = c[i]; sr += k.r; sg += k.g; sb += k.b; }
        float n = w * h, kr = mean.r / Mathf.Max(1e-4f, (float)(sr / n)), kg = mean.g / Mathf.Max(1e-4f, (float)(sg / n)), kb = mean.b / Mathf.Max(1e-4f, (float)(sb / n));
        var px = new Color32[w * h];
        FormulaSim.Par.For(0, h, y =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
            {
                byte a = 255;
                if (alpha != null) a = B(alpha((float)x / w, (float)y / h, x, y));
                px[i] = new Color32(S(c[i].r * kr), S(c[i].g * kg), S(c[i].b * kb), a);
            }
        });
        return Make(w, h, px, false, aniso);
    }


    // ---------------------------------------------------------------- photo-scanned CC0 textures (Resources/Textures/*.bytes, see CREDITS.txt)
    // JPEG data in .bytes files: decoded here with ImageConversion.LoadImage, so the pixels are CPU-readable with no
    // import settings. Anything missing returns null and the procedural texture below is used instead. The photos are
    // never shown raw: each is turned into "relative to its own mean" detail, so the circuit's palette still decides
    // the colour and only the real-world structure (stones, blades, grain) is added.
    public sealed class Photo
    {
        public int W, H; public Color[] lin; public Color mean; public float[] rel; // rel: luminance / mean luminance
        public Color At(int x, int y) => lin[((y % H + H) % H) * W + ((x % W + W) % W)];
        public float L(int x, int y) => rel[((y % H + H) % H) * W + ((x % W + W) % W)];
        // bilinear, wrapping, u / v in tiles
        public float L(float u, float v)
        {
            float fx = u * W - 0.5f, fy = v * H - 0.5f; int x = Mathf.FloorToInt(fx), y = Mathf.FloorToInt(fy); float tx = fx - x, ty = fy - y;
            return Mathf.Lerp(Mathf.Lerp(L(x, y), L(x + 1, y), tx), Mathf.Lerp(L(x, y + 1), L(x + 1, y + 1), tx), ty);
        }
        // colour relative to the mean colour, chroma softened by "chroma" (0 = grey detail only)
        public Color Rel(int x, int y, float chroma)
        {
            var c = At(x, y); float l = rel[((y % H + H) % H) * W + ((x % W + W) % W)];
            float rr = c.r / Mathf.Max(1e-4f, mean.r), rg = c.g / Mathf.Max(1e-4f, mean.g), rb = c.b / Mathf.Max(1e-4f, mean.b);
            return new Color(l * (1f + (rr / l - 1f) * chroma), l * (1f + (rg / l - 1f) * chroma), l * (1f + (rb / l - 1f) * chroma));
        }
    }
    static readonly System.Collections.Generic.Dictionary<string, Photo> photos = new System.Collections.Generic.Dictionary<string, Photo>();
    public static Photo LoadPhoto(string name)
    {
        Photo p;
        if (photos.TryGetValue(name, out p)) return p;
        photos[name] = null;
        try
        {
            var ta = Resources.Load<TextAsset>("Textures/" + name);
            if (ta == null) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!ImageConversion.LoadImage(t, ta.bytes, false)) { Object.Destroy(t); return null; }
            var px = t.GetPixels32(); p = new Photo { W = t.width, H = t.height };
            Object.Destroy(t);
            p.lin = new Color[px.Length]; p.rel = new float[px.Length];
            double sr = 0, sg = 0, sb = 0;
            for (int i = 0; i < px.Length; i++)
            {
                var c = new Color(Mathf.Pow(px[i].r / 255f, 2.2f), Mathf.Pow(px[i].g / 255f, 2.2f), Mathf.Pow(px[i].b / 255f, 2.2f));
                p.lin[i] = c; sr += c.r; sg += c.g; sb += c.b;
            }
            float n = px.Length; p.mean = new Color((float)(sr / n), (float)(sg / n), (float)(sb / n));
            float ml = Mathf.Max(1e-4f, 0.299f * p.mean.r + 0.587f * p.mean.g + 0.114f * p.mean.b);
            for (int i = 0; i < px.Length; i++) p.rel[i] = Mathf.Clamp((0.299f * p.lin[i].r + 0.587f * p.lin[i].g + 0.114f * p.lin[i].b) / ml, 0.05f, 4f);
            return photos[name] = p;
        }
        catch (System.Exception) { return null; }
    }

    public static int Bytes; // texture memory made here (RGBA32 + mips), for the performance notes
    static Texture2D Count(Texture2D t) { Bytes += t.width * t.height * 4 * 4 / 3; return t; }

    // ---------------------------------------------------------------- ground
    public static bool IsGreen(Color c) => c.g > c.r * 1.12f && c.g > c.b * 1.2f;

    // RR3 reference: sunlit lawns are warm yellow-olive, not neon green (linear multipliers on the circuit's grass mean)
    static readonly Vector3 GrassTint = new Vector3(1.09f, 0.80f, 1.25f);
    // GroundDetail's alpha (linear data, mean ~0.5): clumps of ~0.75 m at the 4.5 m tile; Ground.shader HQ reads it
    // again at the 15 m tile, where the same field makes ~2.6 m patches (tufts / lush and thin patches)
    static float Clumps(float u, float v, int x, int y) => Mathf.SmoothStep(0f, 1f, (Fbm(u, v, 6, 3, 301) - 0.32f) / 0.36f);
    public static Texture2D GroundDetail(Color albedo)
    {
        if (IsGreen(albedo))
        {
            albedo = new Color(albedo.r * GrassTint.x, albedo.g * GrassTint.y, albedo.b * GrassTint.z, albedo.a);
            var gp = LoadPhoto("grass");
            if (gp != null) // real turf: photo structure, the circuit's green; softened chroma keeps it from going patchy
                return Count(Albedo(gp.W, gp.H, albedo, (u, v, x, y) =>
                {
                    var r = gp.Rel(x, y, 0.45f);
                    float k = 1f + 0.8f * (gp.L(x, y) - 1f);
                    return new Color(Mathf.Max(0.05f, r.r * 0.5f + k * 0.5f), Mathf.Max(0.05f, r.g * 0.5f + k * 0.5f), Mathf.Max(0.05f, r.b * 0.5f + k * 0.5f));
                }, 4, Clumps));
        }
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
            }, 4, Clumps));
        // sand / dust / grit (desert circuits, the paved ground of street circuits)
        return Count(Albedo(256, 256, albedo, (u, v, x, y) =>
        {
            float g = Fbm(u, v, 16, 4, 23), fine = Hash(x, y, 29), r = Hash(x >> 1, y >> 1, 31);
            float k = 0.8f + 0.35f * (g - 0.5f) + 0.18f * (fine - 0.5f);
            if (r > 0.96f) k += 0.25f; else if (r < 0.03f) k -= 0.3f;
            float ripple = 0.06f * Mathf.Sin((u * 6 + Fbm(u, v, 4, 2, 37) * 2f) * Mathf.PI * 2 * 4);
            return new Color(k + ripple, k + ripple * 0.9f, k * 0.97f + ripple * 0.7f);
        }, 4, (u, v, x, y) => 0.5f)); // (no clumps on sand: neutral)
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
        var gp = LoadPhoto("gravel");
        if (gp != null) return Count(Albedo(gp.W, gp.H, new Color(0.50f, 0.45f, 0.36f), (u, v, x, y) => gp.Rel(x, y, 0.7f)));
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
        var cp = LoadPhoto("concrete");
        return Count(Albedo(256, 256, new Color(0.46f, 0.45f, 0.42f), (u, v, x, y) =>
        {
            float sx = u * 4, sy = v * 7; int i = Mathf.FloorToInt(sx), j = Mathf.FloorToInt(sy);
            float ex = Mathf.Min(sx - i, 1 - (sx - i)) * 4f * 64f / 4f, ey = Mathf.Min(sy - j, 1 - (sy - j)) * 256f / 7f;
            float joint = Mathf.Clamp01(Mathf.Min(ex, ey) / 1.6f);
            float slab = 0.9f + 0.2f * Hash(i, j, 81), stain = Fbm(u, v, 8, 3, 83);
            float k = slab * (0.85f + 0.3f * stain) * (0.9f + 0.1f * Hash(x, y, 85)) * (0.55f + 0.45f * joint);
            if (cp != null) k *= Mathf.Clamp(1f + 0.9f * (cp.L(u * 2f, v * 2f) - 1f), 0.5f, 1.6f); // real concrete grain and stains
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

    // ---------------------------------------------------------------- road (Road.shader, both set as globals by RoadSurface.cs)
    // The road reads as RR3-style clean asphalt: a calm mid grey with long, soft tonal drifts and a fine grain that only
    // shows near the car. Nothing in either texture has metre-scale contrast: at 300 km/h and ~35 fps the road moves
    // ~2.5 m per frame, so anything between ~5 cm and ~5 m wide strobes ("the road crawls"). Fine grain is either far
    // below that (and faded with distance, speed and mip) or the variation is tens of metres long.

    // RoadDetail: fine aggregate grain, 512 x 512 R8 (linear), 1.6 m per tile (3 mm texels), HQ only. 0.5 = neutral.
    // Mean exactly 0.5 on every mip, and the mip chain is made by hand: each level is the box-filtered (energy-preserving)
    // average of the one above, then its contrast is scaled down (RoadDetailMipFade), so the grain melts to flat grey by
    // the time a pixel covers ~5 cm, whatever the distance or the viewing angle. The shader never has to guess when to
    // fade it out, and nothing is left to sparkle or crawl.
    public static readonly float[] RoadDetailMipFade = { 1f, 1f, 0.9f, 0.6f, 0.3f, 0.1f, 0f };
    public const float RoadDetailStd = 0.15f; // standard deviation of mip 0 around 0.5 (the shader scales it)
    static Texture2D cRoadDetail;
    public static Texture2D RoadDetail() => cRoadDetail != null ? cRoadDetail : (cRoadDetail = MakeRoadDetail());
    static Texture2D MakeRoadDetail()
    {
        const int N = 512;
        var l0 = new float[N * N];
        var ap = LoadPhoto("asphalt"); // CC0 racing asphalt (Poly Haven asphalt_track): real stone and binder structure
        FormulaSim.Par.For(0, N, y =>
        {
            for (int x = 0; x < N; x++)
            {
                float u = (x + 0.5f) / N, v = (y + 0.5f) / N, l;
                if (ap != null) l = ap.L(u, v); // one photo tile per texture tile
                else
                {
                    // procedural fallback: soft, band-limited stones (value noise, 2.5 cm and finer), a little sand
                    float s = Fbm(u, v, 64, 3, 401), p = Fbm(u, v, 160, 2, 409);
                    l = 1f + 0.9f * (s - 0.5f) + 0.5f * (p - 0.5f) + 0.12f * (Hash(x, y, 417) - 0.5f);
                }
                // log response: the photo's few very bright stone tops would otherwise dominate (and sparkle)
                l0[y * N + x] = Mathf.Log(Mathf.Clamp(l, 0.25f, 3f));
            }
        });
        // normalise mip 0 to mean 0, standard deviation RoadDetailStd, soft-clipped so no texel is an outlier
        double s1 = 0, s2 = 0;
        for (int k = 0; k < l0.Length; k++) { s1 += l0[k]; s2 += l0[k] * l0[k]; }
        float mean = (float)(s1 / l0.Length), sd = Mathf.Sqrt(Mathf.Max(1e-8f, (float)(s2 / l0.Length) - mean * mean));
        for (int k = 0; k < l0.Length; k++)
        {
            float z = (l0[k] - mean) / sd;                    // in standard deviations
            l0[k] = z / Mathf.Sqrt(1f + z * z * 0.2f);        // soft clip beyond ~2 sd
        }
        s1 = 0; s2 = 0;
        for (int k = 0; k < l0.Length; k++) { s1 += l0[k]; s2 += l0[k] * l0[k]; }
        mean = (float)(s1 / l0.Length); sd = Mathf.Sqrt(Mathf.Max(1e-8f, (float)(s2 / l0.Length) - mean * mean));
        for (int k = 0; k < l0.Length; k++) l0[k] = (l0[k] - mean) / sd * RoadDetailStd; // zero mean, exact std

        bool r8 = SystemInfo.SupportsTextureFormat(TextureFormat.R8);
        var t = new Texture2D(N, N, r8 ? TextureFormat.R8 : TextureFormat.RGBA32, true, true)
        { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8, name = "RoadDetail" };
        int mips = t.mipmapCount, w = N; var cur = l0;
        for (int m = 0; m < mips; m++)
        {
            float f = m < RoadDetailMipFade.Length ? RoadDetailMipFade[m] : 0f;
            if (r8)
            {
                var b = new byte[w * w];
                for (int k = 0; k < b.Length; k++) b[k] = B(0.5f + cur[k] * f);
                t.SetPixelData(b, m);
            }
            else
            {
                var c = new Color32[w * w];
                for (int k = 0; k < c.Length; k++) { byte g = B(0.5f + cur[k] * f); c[k] = new Color32(g, g, g, 255); }
                t.SetPixels32(c, m);
            }
            if (w == 1) break;
            // next level: 2 x 2 box average of the unfaded data (the fade is applied per level, never compounded)
            int nw = w / 2; var nx = new float[nw * nw];
            for (int y = 0; y < nw; y++)
                for (int x = 0; x < nw; x++)
                {
                    int i0 = 2 * y * w + 2 * x;
                    nx[y * nw + x] = 0.25f * (cur[i0] + cur[i0 + 1] + cur[i0 + w] + cur[i0 + w + 1]);
                }
            cur = nx; w = nw;
        }
        t.Apply(false, true);
        Bytes += N * N * (r8 ? 1 : 4) * 4 / 3;
        return t;
    }

    // RoadMacro: 256 x 256 RGBA32 (linear data), read stretched: 48 m across x 192 m along the track, so every variation
    // is a long, soft drift in the direction of travel (resurfaced lanes, rubbered / sun-bleached bands), never a blotch
    // that flicks past. Both levels read it (Low: its only texture besides the shadow mask).
    //   R tone drift (lane-wide, tens of metres long), G slow mottle (rubber density, wet pools, kerb grime),
    //   B darker resurfaced patches (soft edges, ~20-80 m long). 0.5 = neutral in R and G, B 0 = none.
    static Texture2D cRoadMacro;
    public static Texture2D RoadMacro() => cRoadMacro != null ? cRoadMacro : (cRoadMacro = MakeRoadMacro());
    static Texture2D MakeRoadMacro()
    {
        const int N = 256;
        var px = new Color32[N * N];
        FormulaSim.Par.For(0, N, y =>
        {
            for (int x = 0; x < N; x++)
            {
                float u = (float)x / N, v = (float)y / N;
                // low-passed: 3 octaves from 4 cells (12 m x 48 m down to 3 m x 12 m), the finest one weak
                float r = 0.5f + (Fbm(u, v, 4, 3, 431) - 0.5f) * 2.0f;
                float g = 0.5f + (Fbm(u, v, 6, 3, 437) - 0.5f) * 2.2f;
                float pf = Fbm(u, v, 3, 3, 443);
                float b = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.54f, 0.66f, pf));
                px[y * N + x] = new Color32(B(r), B(g), B(b), 255);
            }
        });
        // smooth data (0.2 m x 0.75 m texels, no edges): 2x anisotropy is plenty, the fine grain gets the 8x
        return Count(Make(N, N, px, true, 2));
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
    // Atlas 1024 x 512: 8 x 4 cells of 128 px, each one tile that repeats over a wall or roof (Baked.shader F2_ATLAS
    // wraps inside the cell, 2 px margin). uv2.x = the cell index, uv2.y = an alternative cell + its share: the shader
    // swaps single bays to it (closed shutters, a door among the shop windows), so a facade does not repeat one bay.
    // Alpha = tint mask: 1 where the building's own colour applies (plaster, brick, cladding, roofing: tinted per
    // building by uv3, a Lombardy palette in RealWorld), 0 where the pixel keeps its colour (glass, frames, shutters,
    // stone surrounds, doors). The base materials are CC0 photos (Resources/Textures, see CREDITS.txt) box-filtered
    // to the tile; missing photos fall back to flat colour. Wall tiles are one bay x one storey (3.0 x 3.2 m homes).
    //   row 0 upper floors: 0 green shutters  1 brown shutters  2 roller blind (tapparella)  3 brick + shutters
    //                       4 flats with balcony  5 office ribbon window  6 glass curtain wall  7 stone, arched window
    //   row 1 ground floors: 8 barred window on a rusticated base  9 front door  10 shop front  11 roller garage door
    //                       12 office lobby  13 industrial loading door  14 metal cladding, strip window  15 corrugated shed
    //   row 2 materials:    16 clay barrel tiles (coppi)  17 gravel roof  18 corrugated metal roof  19 skylight
    //                       20 precast concrete panels  21 plaster  22 grandstand crowd  23 rooftop unit (louvres, fan)
    //   row 3:              24 brick  25 eave soffit (rafters, terracotta tavelle)  26 light stone trim
    //                       27 concrete panel with a window  28 plaster  29, 30 shutters closed  31 blind down
    public const int AtlasCols = 8, AtlasRows = 4;
    static Texture2D bAtlas;
    static Color[] dPl, dBr, dTi, dPa, dCl, dCo, dSh, dGr; // photo detail per tile (128 x 128, linear, relative to the photo's mean)
    static float mk; // tint mask of the pixel being painted
    public static Texture2D BuildingAtlas()
    {
        if (bAtlas != null) return bAtlas;
        const int C = 128, W = C * AtlasCols, H = C * AtlasRows; var px = new Color32[W * H];
        dPl = Det("plaster", 1, 0.3f, 3f); dBr = Det("brick", 2, 1f, 1f); dTi = Det("rooftiles", 1, 0.55f, 0.75f);
        dPa = Det("panels", 2, 0.3f, 1f); dCl = Det("cladding", 3, 0f, 2.5f); dCo = Det("corrugated", 1, 0.2f, 1.6f);
        dSh = Det("shutter", 1, 0f, 1.3f); dGr = Det("gravel", 2, 0.2f, 0.6f);
        for (int cell = 0; cell < AtlasCols * AtlasRows; cell++)
        {
            int ox0 = (cell % AtlasCols) * C, oy0 = (cell / AtlasCols) * C;
            for (int y = 0; y < C; y++) for (int x = 0; x < C; x++)
            {
                // the tile spans pixels 2 .. 126 (the shader's margin); the margin repeats the opposite edge
                float u = ((x - 2 + 0.5f) / 124f + 1f) % 1f, v = ((y - 2 + 0.5f) / 124f + 1f) % 1f;
                mk = 1f;
                var c = BCell(cell, u, v, Mathf.Min(127, (int)(u * C)), Mathf.Min(127, (int)(v * C)));
                px[(oy0 + y) * W + ox0 + x] = new Color32(B(c.r), B(c.g), B(c.b), B(mk));
            }
        }
        dPl = dBr = dTi = dPa = dCl = dCo = dSh = dGr = null;
        var t = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        t.SetPixels32(px); t.Apply(true, true);
        return bAtlas = Count(t);
    }
    // a photo box-filtered to 128 x 128 per tile with "rep" repeats, as colour relative to its mean (luminance ~1):
    // chroma 0 = grey detail only, contrast scales the deviation from 1. Null if the photo is missing.
    static Color[] Det(string name, int rep, float chroma, float contrast)
    {
        var p = LoadPhoto(name); if (p == null) return null;
        const int N = 128; var d = new Color[N * N];
        float step = (float)p.W * rep / N; int k = Mathf.Max(1, Mathf.RoundToInt(step));
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
        {
            float r = 0, g = 0, b = 0; int x0 = (int)(x * step), y0 = (int)(y * p.H * rep / (float)N);
            for (int j = 0; j < k; j++) for (int i = 0; i < k; i++) { var c = p.Rel(x0 + i, y0 + j, chroma); r += c.r; g += c.g; b += c.b; }
            float s = 1f / (k * k);
            d[y * N + x] = new Color(1f + (r * s - 1f) * contrast, 1f + (g * s - 1f) * contrast, 1f + (b * s - 1f) * contrast);
        }
        photos.Remove(name); // (the atlas is kept; the decoded photo is not needed again)
        return d;
    }
    static Color32 Col(Color c) => new Color32(B(c.r), B(c.g), B(c.b), 255);
    static Color Dt(Color[] d, int x, int y) => d == null ? Color.white : d[(y & 127) * 128 + (x & 127)];
    // linear albedo x photo detail -> sRGB colour
    static Color Lin(Color[] d, int x, int y, Color lin)
    {
        var k = Dt(d, x, y);
        return new Color(Mathf.Pow(Mathf.Clamp01(lin.r * k.r), 1f / 2.2f), Mathf.Pow(Mathf.Clamp01(lin.g * k.g), 1f / 2.2f), Mathf.Pow(Mathf.Clamp01(lin.b * k.b), 1f / 2.2f));
    }
    static readonly Color PlasterLin = new Color(0.80f, 0.79f, 0.77f), StoneS = new Color(0.88f, 0.86f, 0.80f), FrameW = new Color(0.92f, 0.92f, 0.90f);
    static readonly Color ShutG = new Color(0.25f, 0.40f, 0.27f), ShutB = new Color(0.40f, 0.27f, 0.17f), BlindS = new Color(0.74f, 0.72f, 0.66f);
    static Color Plaster(int x, int y) => Lin(dPl, x, y, PlasterLin);
    static Color Stone(int x, int y) { mk = 0f; return Lin(dPa, x, y, new Color(0.72f, 0.69f, 0.60f)); }
    // glass reflecting the sky: darker low down, lighter at the top, a streak of reflection
    static Color Glass(float t, float u, float v)
    {
        var c = Color.Lerp(new Color(0.16f, 0.21f, 0.27f), new Color(0.50f, 0.60f, 0.70f), t * t);
        if (u + v * 0.6f > 0.85f && u + v * 0.6f < 0.9f) c += new Color(0.1f, 0.1f, 0.1f);
        return c;
    }
    // slatted panel (shutter leaf, roller blind): a slat every 3 px, a darker border
    static Color Slats(Color col, int y, bool edge) => col * (edge ? 0.72f : y % 3 == 0 ? 0.68f : 1f);
    // a window opening [u0, u1] x [v0, v1] with a stone surround "sur" wide (0: none), a sill with a rain stain under
    // it, the reveal's shadow, a sash frame with a mullion and a transom, and a roller blind covering "blind" of the
    // height from the top. False outside it (plaster: stain only).
    static bool Window(ref Color c, float u, float v, int y, float u0, float u1, float v0, float v1, float sur, float blind, Color frame)
    {
        float s0 = u0 - sur - 0.025f, s1 = u1 + sur + 0.025f;
        if (u > s0 && u < s1 && v < v0 - 0.045f && v > v0 - 0.30f) c *= 1f - 0.09f * (v - v0 + 0.30f) / 0.255f; // rain stain
        if (u > s0 && u < s1 && v >= v0 - 0.045f && v < v0 - 0.02f) { c *= 0.6f; return true; }              // under the sill
        if (u > s0 && u < s1 && v >= v0 - 0.02f && v < v0 + 0.012f) { c = StoneS * 1.04f; mk = 0f; return true; } // sill
        if (sur > 0 && u > u0 - sur && u < u1 + sur && v >= v0 && v < v1 + sur && (u < u0 || u > u1 || v > v1)) { c = StoneS; mk = 0f; return true; }
        if (u < u0 || u > u1 || v < v0 || v > v1) return false;
        mk = 0f;
        float w = u1 - u0, h = v1 - v0, t = (v - v0) / h;
        if (v > v1 - 0.03f || u < u0 + 0.018f) { c = new Color(0.10f, 0.10f, 0.11f); return true; }          // reveal in shadow
        if (t > 1f - blind) { c = Slats(BlindS, y, false); return true; }
        bool fr = u < u0 + 0.04f || u > u1 - 0.022f || v < v0 + 0.03f || Mathf.Abs(u - (u0 + w * 0.5f)) < 0.014f || Mathf.Abs(t - 0.74f) < 0.018f;
        c = fr ? frame : Glass(t, u, v) * 0.85f;
        return true;
    }
    static bool Shutters(ref Color c, float u, float v, int y, float u0, float u1, float v0, float v1, float sur, Color col, bool closed)
    {
        float lw = (u1 - u0) * 0.5f;
        if (closed)
        {
            if (u < u0 || u > u1 || v < v0 || v > v1) return false;
            mk = 0f; c = Slats(col, y, Mathf.Abs(u - u0 - lw) < 0.012f || u < u0 + 0.012f || u > u1 - 0.012f || v > v1 - 0.015f); return true;
        }
        float a0 = u0 - sur - lw, a1 = u0 - sur, b0 = u1 + sur, b1 = u1 + sur + lw;
        if (v < v0 || v > v1 || !(u > a0 && u < a1 || u > b0 && u < b1)) return false;
        mk = 0f; c = Slats(col, y, u < a0 + 0.012f || u > b1 - 0.012f || v > v1 - 0.015f); return true;
    }
    // a string course at floor level: a light band with its shadow under it
    static void Course(ref Color c, float v) { if (v < 0.018f) c *= 0.78f; else if (v < 0.05f) c *= 1.08f; }
    static Color BCell(int cell, float u, float v, int x, int y)
    {
        int s = 300 + cell * 13;
        switch (cell)
        {
            case 0: case 1: case 29: case 30: // shuttered bay (persiane), open or closed
            {
                var c = Plaster(x, y); Course(ref c, v);
                Color sc = cell == 0 || cell == 29 ? ShutG : ShutB;
                if (Shutters(ref c, u, v, y, 0.34f, 0.66f, 0.28f, 0.80f, 0.025f, sc, cell >= 29)) return c;
                Window(ref c, u, v, y, 0.34f, 0.66f, 0.28f, 0.80f, 0.025f, 0f, FrameW);
                return c;
            }
            case 2: case 31: // stone surround, roller blind part or all the way down
            {
                var c = Plaster(x, y); Course(ref c, v);
                Window(ref c, u, v, y, 0.33f, 0.67f, 0.28f, 0.80f, 0.03f, cell == 2 ? 0.36f : 0.9f, FrameW);
                return c;
            }
            case 3: // brick, stone sill and lintel, brown shutters (farmhouse)
            {
                var c = Lin(dBr, x, y, new Color(0.30f, 0.15f, 0.09f) * 1.25f);
                if (u > 0.31f && u < 0.69f && v > 0.80f && v < 0.85f) { mk = 0f; return StoneS * 0.95f; }
                if (Shutters(ref c, u, v, y, 0.34f, 0.66f, 0.28f, 0.80f, 0f, ShutB, false)) return c;
                Window(ref c, u, v, y, 0.34f, 0.66f, 0.28f, 0.80f, 0f, 0f, FrameW);
                return c;
            }
            case 4: // block of flats: balcony slab, railing, French window with a blind, the slab above in shadow
            {
                var c = Plaster(x, y);
                if (v > 0.94f) return c * 0.62f;
                if (v < 0.055f) { mk = 0f; return StoneS * (v < 0.015f ? 0.8f : 1f); }
                bool rail = v < 0.36f && (v > 0.335f || x % 5 == 0);
                if (rail) { mk = 0f; return new Color(0.22f, 0.23f, 0.24f); }
                if (u > 0.2f && u < 0.8f && v < 0.84f)
                {
                    mk = 0f; float t = (v - 0.055f) / 0.785f;
                    if (v > 0.81f) return new Color(0.1f, 0.1f, 0.11f);
                    if (t > 0.62f) return Slats(BlindS, y, false);
                    bool fr = u < 0.225f || u > 0.775f || Mathf.Abs(u - 0.5f) < 0.012f;
                    var g = fr ? FrameW : Glass(t, u, v) * 0.8f;
                    return v < 0.36f ? g * 0.75f : g; // behind the railing: in the balcony's shade
                }
                if (v < 0.36f) return c * 0.8f;
                return c;
            }
            case 5: // office: ribbon window between precast spandrels
            {
                if (v < 0.30f || v > 0.94f) { var c = Lin(dPa, x, y, new Color(0.70f, 0.69f, 0.66f)); return v < 0.03f ? c * 0.8f : c; }
                mk = 0f;
                if (u < 0.02f || u > 0.98f || Mathf.Abs(u - 0.5f) < 0.012f || v < 0.32f || v > 0.925f) return new Color(0.55f, 0.57f, 0.60f);
                var gl = Glass((v - 0.3f) / 0.64f, u, v) * 0.9f;
                if (u < 0.5f && v > 0.76f) gl = Slats(new Color(0.78f, 0.78f, 0.76f), y, false); // venetian blind
                return gl;
            }
            case 6: // glass curtain wall
            {
                mk = 0f;
                if (v < 0.1f) return new Color(0.30f, 0.32f, 0.35f) * (v < 0.03f ? 0.8f : 1f);
                if (x % 32 < 2) return new Color(0.62f, 0.64f, 0.66f);
                return Glass((v - 0.1f) / 0.9f, u, v) * 0.95f;
            }
            case 7: // dressed stone, tall window with a round top
            {
                int row = y / 16; bool joint = y % 16 == 0 || (x + (row % 2) * 20) % 40 == 0;
                var c = Plaster(x, y) * (0.94f + 0.08f * Hash((x + (row % 2) * 20) / 40, row, s));
                if (joint) c *= 0.82f;
                float du = (u - 0.5f) / 0.15f, dv = (v - 0.62f) / 0.15f;
                bool arch = v >= 0.62f && du * du + dv * dv < 1f, win = Mathf.Abs(u - 0.5f) < 0.15f && v > 0.18f && v < 0.62f || arch;
                bool sur = Mathf.Abs(u - 0.5f) < 0.18f && v > 0.16f && v < 0.62f || v >= 0.62f && du * du * 0.69f + dv * dv * 0.69f < 1f;
                if (win) { mk = 0f; return Mathf.Abs(u - 0.5f) < 0.012f ? new Color(0.3f, 0.3f, 0.3f) : Glass((v - 0.18f) / 0.6f, u, v) * 0.75f; }
                if (sur) { mk = 0f; return StoneS; }
                return c;
            }
            case 8: // ground floor: rusticated plaster over a stone plinth, a barred window
            {
                if (v < 0.13f) { var p = Stone(x, y); return v > 0.115f ? p * 1.1f : p; }
                var c = Plaster(x, y);
                if ((y % 14) < 1) c *= 0.74f; // rustication grooves
                if (Window(ref c, u, v, y, 0.35f, 0.65f, 0.32f, 0.76f, 0.03f, 0f, FrameW))
                {
                    if (u > 0.35f && u < 0.65f && v > 0.32f && v < 0.76f && (x % 7 == 0 || Mathf.Abs(v - 0.45f) < 0.008f || Mathf.Abs(v - 0.64f) < 0.008f)) c = new Color(0.12f, 0.12f, 0.12f);
                    return c;
                }
                return c;
            }
            case 9: // ground floor: panelled wooden front door in a stone frame, a step
            {
                if (v < 0.13f && (u < 0.29f || u > 0.71f)) { var p = Stone(x, y); return v > 0.115f ? p * 1.1f : p; }
                var c = Plaster(x, y);
                if ((y % 14) < 1) c *= 0.74f;
                if (u > 0.29f && u < 0.71f && v < 0.84f)
                {
                    mk = 0f;
                    if (v < 0.04f) return StoneS * 0.9f;
                    if (u < 0.32f || u > 0.68f || v > 0.81f) return StoneS;
                    if (v > 0.70f) return v > 0.80f || Mathf.Abs(u - 0.5f) < 0.01f ? new Color(0.25f, 0.18f, 0.12f) : Glass((v - 0.7f) / 0.1f, u, v) * 0.7f; // fanlight
                    var wood = new Color(0.36f, 0.24f, 0.15f) * (0.92f + 0.08f * Hash(x, y / 4, s));
                    bool panel = (u > 0.35f && u < 0.47f || u > 0.53f && u < 0.65f) && (v > 0.08f && v < 0.36f || v > 0.42f && v < 0.66f);
                    if (Mathf.Abs(u - 0.5f) < 0.008f) return wood * 0.5f;
                    return panel ? wood * 0.82f : wood;
                }
                return c;
            }
            case 10: // ground floor: shop window, sign fascia, plaster above
            {
                if (v > 0.84f) return Plaster(x, y);
                mk = 0f;
                if (v < 0.035f) return Stone(x, y) * 0.8f;
                if (v > 0.68f) // fascia with the shop's name
                {
                    var f = new Color(0.16f, 0.30f, 0.26f);
                    if (v > 0.715f && v < 0.80f && u > 0.18f && u < 0.82f && Hash(x / 3, (int)(v * 40f), s) > 0.45f) return new Color(0.93f, 0.90f, 0.82f);
                    return v < 0.695f || v > 0.825f ? f * 0.6f : f;
                }
                if (u < 0.05f || u > 0.95f) { mk = 1f; return Plaster(x, y) * 0.95f; }
                if (u < 0.07f || u > 0.93f || v < 0.06f || v > 0.65f || Mathf.Abs(u - 0.66f) < 0.012f) return new Color(0.20f, 0.21f, 0.22f);
                if (u > 0.66f && v < 0.12f) return new Color(0.20f, 0.21f, 0.22f);
                float t = (v - 0.06f) / 0.59f;
                var gl = Glass(t, u, v) * 0.8f;
                if (t < 0.6f) // lit shop interior: shelves and goods
                {
                    float hk = Hash(x / 6, (int)(t * 7f), s);
                    var goods = new Color(0.55f + 0.35f * hk, 0.48f + 0.3f * Hash(x / 6, (int)(t * 7f), s + 1), 0.38f + 0.25f * hk);
                    gl = Color.Lerp(gl, goods, 0.45f * (1f - t));
                }
                return gl;
            }
            case 11: // roller garage door (also a shop with its shutter down)
            {
                var c = Plaster(x, y);
                if ((y % 14) < 1) c *= 0.74f; // rustication grooves, as on the other ground-floor bays
                if (v < 0.08f && (u < 0.1f || u > 0.9f)) return Stone(x, y);
                if (u > 0.08f && u < 0.92f && v < 0.76f)
                {
                    mk = 0f;
                    if (v > 0.72f || u < 0.1f || u > 0.9f) return StoneS * 0.92f;
                    var sh = Lin(dSh, x, y, new Color(0.42f, 0.44f, 0.46f));
                    return v > 0.68f ? sh * 0.7f : sh;
                }
                return c;
            }
            case 12: // office lobby: glazing between concrete piers, a door in the middle
            {
                if (u < 0.07f || u > 0.93f || v > 0.86f) return Lin(dPa, x, y, new Color(0.70f, 0.69f, 0.66f)) * (v > 0.86f && v < 0.88f ? 0.75f : 1f);
                mk = 0f;
                if (v < 0.02f || u < 0.09f || u > 0.91f || v > 0.84f || (v < 0.62f && Mathf.Abs(u - 0.5f) < 0.01f) || Mathf.Abs(u - 0.34f) < 0.008f || Mathf.Abs(u - 0.66f) < 0.008f || Mathf.Abs(v - 0.62f) < 0.01f)
                    return new Color(0.22f, 0.23f, 0.25f);
                return Glass(v / 0.84f, u, v) * 0.8f + new Color(0.06f, 0.05f, 0.03f) * (1f - v);
            }
            case 13: // industrial bay with a sectional loading door
            {
                if (v < 0.07f && (u < 0.14f || u > 0.86f)) { mk = 0f; return Lin(dPa, x, y, new Color(0.50f, 0.49f, 0.46f)); }
                if (u > 0.13f && u < 0.87f && v < 0.74f)
                {
                    mk = 0f;
                    if (u < 0.15f || u > 0.85f || v > 0.715f) return new Color(0.28f, 0.29f, 0.30f);
                    if (v > 0.50f && v < 0.56f && (x % 16) > 3) return Glass(0.5f, u, v) * 0.8f;
                    float f = (v * 11f) % 1f;
                    return new Color(0.82f, 0.83f, 0.84f) * (f < 0.08f ? 0.7f : 1f - 0.08f * f);
                }
                return Lin(dCl, x, y, new Color(0.62f, 0.62f, 0.62f));
            }
            case 14: // ribbed metal cladding, a strip window high up, concrete plinth
            {
                if (v < 0.07f) { mk = 0f; return Lin(dPa, x, y, new Color(0.50f, 0.49f, 0.46f)) * (v > 0.06f ? 0.8f : 1f); }
                if (v > 0.72f && v < 0.80f) { mk = 0f; return x % 16 < 2 || v < 0.73f || v > 0.79f ? new Color(0.62f, 0.63f, 0.64f) : Glass((v - 0.72f) / 0.08f, u, v) * 0.85f; }
                var c = Lin(dCl, x, y, new Color(0.62f, 0.62f, 0.62f));
                return v > 0.97f ? c * 0.85f : c;
            }
            case 15: // corrugated sheet shed
            {
                if (v < 0.06f) { mk = 0f; return Lin(dPa, x, y, new Color(0.48f, 0.47f, 0.44f)); }
                return Lin(dCo, x, y, new Color(0.60f, 0.60f, 0.58f));
            }
            case 16: return Lin(dTi, x, y, new Color(0.62f, 0.62f, 0.62f));               // coppi: the roof colour rides in the tint
            case 17:
            {
                var c = Lin(dGr, x, y, new Color(0.46f, 0.45f, 0.43f));
                return x % 64 == 0 || y % 64 == 0 ? c * 0.86f : c;                           // gravel ballast, membrane seams
            }
            case 18: return Lin(dCo, x, y, new Color(0.66f, 0.66f, 0.66f));                 // corrugated roof sheets
            case 19: // ribbed polycarbonate skylight in a frame
            {
                mk = 0f;
                if (x < 6 || x > 121 || y < 4 || y > 123) return new Color(0.55f, 0.56f, 0.57f);
                return new Color(0.78f, 0.83f, 0.86f) * (x % 10 < 2 ? 0.86f : 1f - 0.1f * Hash(x / 10, 0, s));
            }
            case 20: return Lin(dPa, x, y, new Color(0.62f, 0.61f, 0.58f));                 // precast concrete panels
            case 22:
            {
                // grandstand rows with a seated crowd: 4 rows x 16 people per tile (RealWorld's map grandstands), many red shirts
                mk = 0f;
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
            case 23: // rooftop unit: louvred casing, a fan grille
            {
                mk = 0f;
                var cs = new Color(0.80f, 0.80f, 0.78f);
                if (x < 5 || x > 122 || y < 5 || y > 122) return cs * 0.85f;
                float du = u - 0.5f, dv = v - 0.52f, r = Mathf.Sqrt(du * du + dv * dv);
                if (r < 0.31f) return r < 0.05f ? cs * 0.6f : new Color(0.18f, 0.18f, 0.19f) * ((int)(r * 60f) % 2 == 0 ? 1.4f : 1f);
                if (r < 0.33f) return cs * 0.7f;
                return cs * (y % 6 < 2 ? 0.72f : 1f);
            }
            case 24: return Lin(dBr, x, y, new Color(0.30f, 0.15f, 0.09f) * 1.25f);           // brick
            case 25: // eave soffit: wooden rafters with terracotta tavelle between them (4 per tile)
            {
                mk = 0f;
                float f = (u * 4f) % 1f;
                if (f < 0.16f) return new Color(0.34f, 0.23f, 0.15f) * (f < 0.03f || f > 0.13f ? 0.75f : 1f);
                return new Color(0.74f, 0.52f, 0.40f) * (y % 21 < 1 ? 0.8f : 0.95f + 0.05f * Hash(x, y / 3, s));
            }
            case 26: { var c = Stone(x, y); return new Color(c.r * 1.12f, c.g * 1.12f, c.b * 1.12f); } // light stone trim
            case 27: // concrete panel with a window and a blind (1970s offices)
            {
                var c = Lin(dPa, x, y, new Color(0.66f, 0.65f, 0.62f));
                if (y % 64 < 1) c *= 0.8f;
                Window(ref c, u, v, y, 0.2f, 0.8f, 0.30f, 0.84f, 0f, 0.28f, new Color(0.30f, 0.31f, 0.33f));
                return c;
            }
            default: return Plaster(x, y);
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
    // 4 x 2 cells of 256 px (1024 x 512). The first four values are the ones the road shadows know (RoadShadow: 0 spruce,
    // 2 pine, 3 palm, the rest broad-leaf); SlotOf places each card in the atlas. The bottom row holds the cards whose
    // foliage reaches the foot of the cell (the groves) and the poplar, so mip bleeding only ever meets other foliage.
    //   Broadleaf  round park tree (lime, maple): many lobes, crown from a fifth of the height, thin dark trunk
    //   Oak        broad spreading crown, short trunk, low limbs
    //   Poplar     tall narrow flame (Lombardy poplar)
    //   Grove(2)   a strip of forest edge: several crowns of mixed height over a dense understorey, foliage to the
    //              ground and ragged sides (the band of cards that stands for the woods beyond the first trees)
    public enum Card { Spruce = 0, Broadleaf = 1, Pine = 2, Palm = 3, Oak = 4, Poplar = 5, Grove = 6, Grove2 = 7 }
    public const int CardCount = 8;
    static readonly int[] SlotOf = { 4, 5, 6, 7, 2, 3, 0, 1 };
    const int AW = 1024, AH = 512, CN = 256;
    // uv rect of each card in the atlas (u0, v0, u1, v1), a half-texel in so neighbours never bleed
    public static Vector4 CardRect(Card c)
    {
        int s = SlotOf[(int)c]; float eu = 1f / AW, ev = 1f / AH, u0 = (s % 4) * 0.25f, v0 = (s / 4) * 0.5f;
        return new Vector4(u0 + eu, v0 + ev, u0 + 0.25f - eu, v0 + 0.5f - ev);
    }
    // mean leaf colour (linear) painted into each card, so circuits can tint relative to it
    public static readonly Color[] CardLeaf = new Color[CardCount];

    static float[] cr, cg, cb, ca;
    static System.Random rnd;
    static float R01() => (float)rnd.NextDouble();

    // paint over the cell (premultiplied "over": soft edges keep the leaf colour, never a dark fringe)
    static void Put(int x, int y, Color c, float a)
    {
        if (x < 0 || y < 0 || x >= CN || y >= CN || a <= 0.002f) return;
        int i = (oy + y) * AW + ox + x;
        float na = a + ca[i] * (1f - a), k = a / na;
        cr[i] += (c.r - cr[i]) * k; cg[i] += (c.g - cg[i]) * k; cb[i] += (c.b - cb[i]) * k; ca[i] = na;
    }
    static int ox, oy;
    static void Cell(Card c) { int s = SlotOf[(int)c]; ox = (s % 4) * CN; oy = (s / 4) * CN; }
    static Photo leafPh, barkPh;
    // a leaf cluster: a disc with a ragged rim and holes, lit from above (lighter top, darker underside) (conifers, palm)
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
            if (leafPh != null) lit *= Mathf.Clamp(1f + 0.6f * (leafPh.L((ox + x) / 128f, (oy + y) / 128f) - 1f), 0.55f, 1.5f); // real leaf structure
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
                if (barkPh != null) bark = Mathf.Clamp(1f + 0.9f * (barkPh.L((ox + xx) / 64f, (oy + yy) / 128f) - 1f), 0.5f, 1.6f); // real bark grooves
                Put(xx, yy, col * lit * bark, 1f);
            }
        }
    }

    // one leaf: a small pointed ellipse, anti-aliased, its upper half a little lighter
    static void Leaf(float cx, float cy, float len, float wid, float ang, Color c)
    {
        float co = Mathf.Cos(ang), si = Mathf.Sin(ang), r = len + 1f, edge = Mathf.Max(wid, 0.9f);
        float bx = Mathf.Abs(co) * len + Mathf.Abs(si) * wid + 1f, by = Mathf.Abs(si) * len + Mathf.Abs(co) * wid + 1f; // the leaf's box
        int x0 = Mathf.FloorToInt(cx - bx), x1 = Mathf.CeilToInt(cx + bx), y0 = Mathf.FloorToInt(cy - by), y1 = Mathf.CeilToInt(cy + by);
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
        {
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy, u = (dx * co + dy * si) / len;
            if (u * u >= 1.2f) continue;
            float v = (-dx * si + dy * co) / (wid * (1f - 0.35f * u * u)), d2 = u * u + v * v;
            if (d2 >= 1.6f) continue;
            float a = Mathf.Clamp01((1f - d2) * 0.5f * edge + 0.5f); // (1 - d) ~ (1 - d^2) / 2 at the rim: no square root
            if (a > 0f) Put(x, y, c * (1f + 0.16f * dy / r), a);
        }
    }
    // a twig's worth of leaves scattered over a disc: the top and rim lighter, the lower inner ones in the cluster's own
    // shade, each leaf's shade and hue jittered (the "many small leaves" look, not a smooth blob)
    static void Cluster(float cx, float cy, float rad, Color col, float leaf, float fill)
    {
        int n = Mathf.Max(3, (int)(rad * rad / (leaf * leaf) * fill));
        for (int k = 0; k < n; k++)
        {
            float a = R01() * 6.2832f, rr = Mathf.Sqrt(R01());
            float x = cx + Mathf.Cos(a) * rr * rad, y = cy + Mathf.Sin(a) * rr * rad * 0.9f;
            float sh = 0.8f + 0.26f * (y - cy) / rad + 0.1f * rr + (R01() - 0.5f) * 0.3f, hue = (R01() - 0.5f) * 0.16f;
            var c = new Color(col.r * sh * (1f + hue), col.g * sh, col.b * sh * (1f - hue * 1.5f));
            float l = leaf * (0.7f + 0.6f * R01());
            Leaf(x, y, l, l * (0.42f + 0.2f * R01()), R01() * 3.1416f, c);
        }
    }

    struct Blob { public float x, y, z, r; public Color c; }
    static readonly System.Collections.Generic.List<Blob> blobs = new System.Collections.Generic.List<Blob>();
    // A crown: "lobes" sub-crowns (spheres) inside the envelope (cx, cy, rx, ry), each covered in leaf clusters. A cluster
    // is shaded as part of its lobe (lit from above and the front) and of the whole crown (clusters deep inside and low
    // down in the crown's own shade), and all are painted back to front: the gaps between the front clusters show the
    // dark interior, the outline is the ragged edge of many clusters (sky holes at the rim), never a disc.
    // shape: 0 round / ellipsoid, 1 flame (poplar: widest low down, narrowing to a point), 2 spreading (wide, flat-bottomed)
    static void Crown(float cx, float cy, float rx, float ry, int lobes, float clusterR, float leaf, Color col, int shape = 0, float dens = 1f)
    {
        blobs.Clear();
        var Ld = new Vector3(0.2f, 0.78f, 0.6f).normalized;
        for (int l = 0; l < lobes; l++)
        {
            float t = shape == 2 ? 0.3f + 0.65f * R01() : 0.12f + 0.8f * R01();      // height in the envelope (0 foot .. 1 top)
            float prof = shape == 1 ? Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.75f)), 0.8f) * (1f - 0.3f * t)
                       : shape == 2 ? Mathf.Sqrt(Mathf.Clamp01(1f - (t - 0.55f) * (t - 0.55f) * 3.2f))
                       : Mathf.Sqrt(Mathf.Clamp01(1f - (2f * t - 1f) * (2f * t - 1f)));
            prof = Mathf.Max(prof, 0.25f);
            float lr = (0.42f + 0.22f * R01()) * Mathf.Min(rx * (0.3f + 0.7f * prof), ry * 0.75f);
            float lx = cx + (R01() * 2f - 1f) * Mathf.Max(0f, rx * prof - lr * 0.75f), ly = cy - ry + 2f * ry * t, lz = (R01() * 2f - 1f) * rx * prof * 0.4f;
            ly = Mathf.Clamp(ly, cy - ry + lr * 0.8f, cy + ry - lr * 0.8f);
            if (l >= lobes - lobes / 6)
            {
                // a few small lobes out on the rim (branch ends breaking the outline)
                float a = (R01() * 1.5f - 0.25f) * Mathf.PI; lr = (0.16f + 0.1f * R01()) * Mathf.Min(rx, ry);
                lx = cx + Mathf.Cos(a) * (rx - lr * 0.6f); ly = cy + Mathf.Sin(a) * (ry - lr * 0.6f); lz = (R01() - 0.5f) * rx * 0.3f;
            }
            float hue = (R01() - 0.5f) * 0.22f; // lobes a little olive or yellow-green, a little blue-green
            int m = (int)(3.6f * lr * lr / (clusterR * clusterR) * dens) + 4;
            for (int q = 0; q < m; q++)
            {
                // a point on the lobe's surface (a little inside it)
                float z = R01() * 2f - 1f, ph = R01() * 6.2832f, s = Mathf.Sqrt(1f - z * z);
                var n = new Vector3(s * Mathf.Cos(ph), s * Mathf.Sin(ph), z);
                float rad = lr * (0.72f + 0.28f * R01());
                float px = lx + n.x * rad, py = ly + n.y * rad, pz = lz + n.z * rad;
                float lit = 0.3f + 0.7f * Mathf.Max(0f, Vector3.Dot(n, Ld));
                float ex = (px - cx) / rx, ey = (py - cy) / ry, ez = pz / rx, rc = Mathf.Sqrt(ex * ex + ey * ey + ez * ez);
                if (rc > 0.8f && R01() < 0.3f) continue;                                       // sky holes round the rim
                float u = Mathf.Clamp01((rc - 0.15f) / 0.85f), ao = 0.3f + 0.7f * u * u * (3f - 2f * u);       // deep inside the crown: shade
                ao *= 0.66f + 0.34f * Mathf.Clamp01((py - (cy - ry)) / (1.3f * ry));           // underside: shade
                float j = 0.9f + 0.2f * R01(), warm = hue + (lit > 0.8f ? (lit - 0.8f) * 0.6f : 0f); // sunlit tips a touch yellower
                var c = new Color(col.r * (1f + warm), col.g, col.b * (1f - warm), 1f) * (lit * ao * j);
                blobs.Add(new Blob { x = px, y = py, z = pz, r = clusterR * (0.75f + 0.5f * R01()), c = c });
            }
        }
        blobs.Sort((a, b) => a.z.CompareTo(b.z));
        for (int k = 0; k < blobs.Count; k++) { var b = blobs[k]; Cluster(b.x, b.y, b.r, b.c, leaf, 0.9f); }
        blobs.Clear();
    }

    static Texture2D foliage;
    public static Texture2D Foliage()
    {
        if (foliage != null) return foliage;
        leafPh = LoadPhoto("grass"); barkPh = LoadPhoto("bark");
        cr = new float[AW * AH]; cg = new float[AW * AH]; cb = new float[AW * AH]; ca = new float[AW * AH];
        rnd = new System.Random(5);
        Color bark = new Color(0.20f, 0.14f, 0.10f), palmBark = new Color(0.36f, 0.30f, 0.22f);
        Color barkDark = new Color(0.07f, 0.06f, 0.05f);                       // deciduous trunks: thin and dark, in the crown's shade
        Color leafMid = new Color(0.17f, 0.31f, 0.085f);                       // (the circuit's leaf colours ride in the vertex tint)

        // spruce: a straight trunk, drooping tiers of needle sprays that shorten towards the spire
        Cell(Card.Spruce);
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
        // round park tree: a thin dark trunk forking into limbs that show in the gaps, the crown from a fifth of the
        // height (the trunk mostly hidden by the lower foliage) to the top
        Cell(Card.Broadleaf);
        {
            Limb(128, 0, 127, 120, 7, 3.5f, barkDark);
            for (int b = 0; b < 6; b++) Limb(127, 58 + b * 11, 128 + (R01() - 0.5f) * 160f, 110 + R01() * 100f, 3.2f, 1f, barkDark);
            Crown(128, 145, 104, 99, 18, 8.5f, 2.0f, leafMid);
            for (int b = 0; b < 3; b++) Crown(128 + (R01() - 0.5f) * 70f, 52 + R01() * 14f, 24, 18, 2, 6.5f, 1.9f, leafMid * 0.82f);
        }
        // broad spreading tree (oak, plane): short trunk, low heavy limbs, a wide crown flatter at the bottom
        Cell(Card.Oak);
        {
            Limb(128, 0, 126, 76, 10, 7, barkDark);
            Limb(126, 66, 52, 118, 5, 1.5f, barkDark); Limb(127, 70, 204, 124, 5, 1.5f, barkDark); Limb(126, 74, 112, 160, 4, 1.5f, barkDark); Limb(127, 72, 150, 150, 4, 1.5f, barkDark);
            Crown(128, 148, 116, 88, 22, 9f, 2.1f, leafMid * 0.95f, 2);
        }
        // Lombardy poplar: a tall narrow flame on a slim stem
        Cell(Card.Poplar);
        {
            Limb(128, 0, 128, 190, 5, 1.5f, barkDark);
            Crown(128, 134, 40, 112, 28, 6.5f, 1.8f, new Color(0.15f, 0.30f, 0.09f), 1, 1.2f);
        }
        // umbrella pine: a tall, slightly bent bare trunk forking into a wide flat crown
        Cell(Card.Pine);
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
        Cell(Card.Palm);
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
        // forest edge, two strips: tall crowns of mixed height at the back (darker, deeper in the wood), a middle row,
        // dark trunks between, and a dense understorey of bushes to the ground in front. Crowns stay inside the cell so the
        // sides are ragged outlines (neighbouring band cards overlap them), never a straight cut.
        for (int g = 0; g < 2; g++)
        {
            Cell(g == 0 ? Card.Grove : Card.Grove2);
            Color back = leafMid * (g == 0 ? 0.74f : 0.66f), midC = leafMid * 0.9f, bush = leafMid * 0.7f;
            for (int k = 0; k < 4; k++)
            {
                bool pop = g == 1 && (k == 1 || k == 3);
                float rx = pop ? 26f : 44f + R01() * 16f, ry = pop ? 104f : 66f + R01() * 26f;
                float x = Mathf.Clamp(36f + k * 61f + (R01() - 0.5f) * 20f, rx + 5f, 251f - rx), y = Mathf.Min(150f + R01() * 30f, 250f - ry);
                if (pop) y = 250f - ry;
                Crown(x, y, rx, ry, pop ? 12 : 12, pop ? 6.5f : 8f, 1.9f, pop ? back * 1.08f : back, pop ? 1 : 0);
            }
            for (int k = 0; k < 4; k++) { float x = 40f + k * 58f + (R01() - 0.5f) * 16f; Limb(x, 20f, x + (R01() - 0.5f) * 8f, 110f + R01() * 40f, 3.5f, 1.8f, barkDark); }
            for (int k = 0; k < 3; k++)
            {
                float rx = 42f + R01() * 12f, x = Mathf.Clamp(66f + k * 62f + (R01() - 0.5f) * 24f, rx + 5f, 251f - rx);
                Crown(x, 98f + R01() * 22f, rx, 58f + R01() * 14f, 10, 8f, 1.9f, midC);
            }
            for (int k = 0; k < 10; k++)
            {
                float rx = 22f + R01() * 8f, x = Mathf.Clamp(22f + k * 24f + (R01() - 0.5f) * 10f, rx + 3f, 253f - rx);
                Crown(x, 26f + R01() * 14f, rx, 34f + R01() * 8f, 6, 6.5f, 1.8f, bush * (0.9f + 0.2f * R01()), 0, 2.2f);
            }
        }

        // mean leaf colour per card (for tinting)
        var done = new bool[AW * AH];
        for (int k = 0; k < CardCount; k++)
        {
            int s = SlotOf[k], x0 = (s % 4) * CN, y0 = (s / 4) * CN; double r = 0, g = 0, b = 0; int n = 0;
            for (int y = 0; y < CN; y++) for (int x = 0; x < CN; x++)
            {
                int i = (y0 + y) * AW + x0 + x; if (ca[i] < 0.5f || cg[i] < cr[i] * 1.2f) continue;
                r += cr[i]; g += cg[i]; b += cb[i]; n++;
            }
            CardLeaf[k] = n > 0 ? new Color((float)(r / n), (float)(g / n), (float)(b / n)) : new Color(0.1f, 0.2f, 0.1f);
        }
        // colour into the cut-out texels: grown outwards from the leaves for a few texels, the rest the cell's mean leaf
        // colour, so filtering across the silhouette never blends in a foreign colour
        for (int i = 0; i < done.Length; i++) done[i] = ca[i] > 0.004f;
        var grow = new System.Collections.Generic.List<int>();
        for (int pass = 0; pass < 6; pass++)
        {
            grow.Clear();
            for (int y = 0; y < AH; y++) for (int x = 0; x < AW; x++)
            {
                int i = y * AW + x; if (done[i]) continue;
                float r = 0, g = 0, b = 0; int n = 0;
                if (x > 0 && done[i - 1] && ((x - 1) / CN == x / CN)) { r += cr[i - 1]; g += cg[i - 1]; b += cb[i - 1]; n++; }
                if (x < AW - 1 && done[i + 1] && ((x + 1) / CN == x / CN)) { r += cr[i + 1]; g += cg[i + 1]; b += cb[i + 1]; n++; }
                if (y > 0 && done[i - AW] && ((y - 1) / CN == y / CN)) { r += cr[i - AW]; g += cg[i - AW]; b += cb[i - AW]; n++; }
                if (y < AH - 1 && done[i + AW] && ((y + 1) / CN == y / CN)) { r += cr[i + AW]; g += cg[i + AW]; b += cb[i + AW]; n++; }
                if (n == 0) continue;
                cr[i] = r / n; cg[i] = g / n; cb[i] = b / n; grow.Add(i);
            }
            for (int q = 0; q < grow.Count; q++) done[grow[q]] = true;
        }
        for (int k = 0; k < CardCount; k++)
        {
            int s = SlotOf[k], x0 = (s % 4) * CN, y0 = (s / 4) * CN;
            for (int y = 0; y < CN; y++) for (int x = 0; x < CN; x++)
            {
                int i = (y0 + y) * AW + x0 + x; if (done[i]) continue;
                cr[i] = CardLeaf[k].r * 0.8f; cg[i] = CardLeaf[k].g * 0.8f; cb[i] = CardLeaf[k].b * 0.8f;
            }
        }

        // mip chain by hand: colour averaged by coverage (the cut-out fill never darkens the leaves), alpha remapped per
        // cell so each level keeps the full level's coverage at the cut-off (Castano): distant trees neither thin out
        // to sticks nor swell into blobs, and the alpha-to-coverage edge stays where the leaves are
        var tex = new Texture2D(AW, AH, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        int w = AW, h = AH, cs = CN, mip = 0; float[] R = cr, G = cg, Bb = cb, A = ca;
        var cov = new float[CardCount]; var scratch = new float[CN * CN];
        var lut = new byte[4096]; for (int i = 0; i < 4096; i++) lut[i] = S(i / 4095f); // linear -> sRGB, a table: 700k texels
        for (int s = 0; s < CardCount; s++)
        {
            int n = 0, x0 = (s % 4) * CN, y0 = (s / 4) * CN;
            for (int y = 0; y < CN; y++) for (int x = 0; x < CN; x++) if (A[(y0 + y) * AW + x0 + x] >= 0.5f) n++;
            cov[s] = n / (float)(CN * CN);
        }
        while (true)
        {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(lut[(int)(Mathf.Clamp01(R[i]) * 4095f)], lut[(int)(Mathf.Clamp01(G[i]) * 4095f)], lut[(int)(Mathf.Clamp01(Bb[i]) * 4095f)], B(A[i]));
            if (mip > 0 && cs >= 2)
                for (int s = 0; s < CardCount; s++)
                {
                    if (cov[s] <= 0f) continue;
                    int x0 = (s % 4) * cs, y0 = (s / 4) * cs, n = cs * cs, q = 0;
                    for (int y = 0; y < cs; y++) for (int x = 0; x < cs; x++) scratch[q++] = A[(y0 + y) * w + x0 + x];
                    System.Array.Sort(scratch, 0, n);
                    float t = Mathf.Clamp(scratch[Mathf.Clamp(n - Mathf.RoundToInt(cov[s] * n), 0, n - 1)], 0.03f, 0.97f);
                    for (int y = 0; y < cs; y++) for (int x = 0; x < cs; x++)
                    {
                        int i = (y0 + y) * w + x0 + x; float a = A[i];
                        a = a < t ? a * 0.5f / t : 0.5f + (a - t) * 0.5f / (1f - t);
                        px[i].a = B(a);
                    }
                }
            tex.SetPixels32(px, mip);
            if (w == 1 && h == 1) break;
            int nw = Mathf.Max(1, w / 2), nh = Mathf.Max(1, h / 2);
            var nR = new float[nw * nh]; var nG = new float[nw * nh]; var nB = new float[nw * nh]; var nA = new float[nw * nh];
            for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++)
            {
                int xa = Mathf.Min(x * 2, w - 1), xb = Mathf.Min(x * 2 + 1, w - 1), ya = Mathf.Min(y * 2, h - 1), yb = Mathf.Min(y * 2 + 1, h - 1);
                int i0 = ya * w + xa, i1 = ya * w + xb, i2 = yb * w + xa, i3 = yb * w + xb, o = y * nw + x;
                float sa = A[i0] + A[i1] + A[i2] + A[i3];
                if (sa > 0.01f)
                {
                    nR[o] = (R[i0] * A[i0] + R[i1] * A[i1] + R[i2] * A[i2] + R[i3] * A[i3]) / sa;
                    nG[o] = (G[i0] * A[i0] + G[i1] * A[i1] + G[i2] * A[i2] + G[i3] * A[i3]) / sa;
                    nB[o] = (Bb[i0] * A[i0] + Bb[i1] * A[i1] + Bb[i2] * A[i2] + Bb[i3] * A[i3]) / sa;
                }
                else { nR[o] = (R[i0] + R[i1] + R[i2] + R[i3]) * 0.25f; nG[o] = (G[i0] + G[i1] + G[i2] + G[i3]) * 0.25f; nB[o] = (Bb[i0] + Bb[i1] + Bb[i2] + Bb[i3]) * 0.25f; }
                nA[o] = sa * 0.25f;
            }
            R = nR; G = nG; Bb = nB; A = nA; w = nw; h = nh; cs /= 2; mip++;
        }
        tex.Apply(false, true);
        cr = cg = cb = ca = null;
        return foliage = Count(tex);
    }
}
