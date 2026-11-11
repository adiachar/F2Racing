// TracksideArt.cs — the procedural textures the trackside is drawn with (made once per circuit load, no files):
//  * Atlas (256 x 256, 5 mip levels so swatches never bleed into each other up close):
//      y   0..63   64 flat colour swatches (16 x 4 cells of 16 px); flat-coloured parts point all their UVs at a cell centre
//      y  64..127  garage door | glass windows | tyre-wall tops | scuffed concrete (64 x 64 each)
//      y 128..255  eight 128 x 32 advertising boards with INVENTED names (no real teams, sponsors or car makers)
//    So every opaque trackside part shares one material: one draw call.
//  * Crowd: coloured noise of heads and shirts in four density bands, tiled along grandstand rows.
//  * Fence: chain-link diamonds in alpha for the alpha-tested debris/catch fence, with mip levels whose alpha is boosted
//    so the fence fades to a grey veil in the distance instead of vanishing.
//  * Grime: soft dark ground decals in four columns (base shadow, tyre mark, dirt, rubber smear).
using UnityEngine;

public static class TracksideArt
{
    public const int AS = 256;

    // ---------------------------------------------------------------- swatches
    public const int Concrete = 0, ConcreteDark = 1, PaintRed = 2, PaintWhite = 3, PaintYellow = 4, PaintBlue = 5, Steel = 6,
        PostDark = 7, TyreBlack = 8, GantryWhite = 9, GantryDark = 10, LampRed = 11, StandFrame = 12, RoofWhite = 13, RoofUnder = 14,
        SeatBlue = 15, SeatGrey = 16, BuildWhite = 17, BuildGrey = 18, GlassDark = 19, MarshalOrange = 20, PitAsphalt = 21,
        LineWhite = 22, BannerGreen = 23, Scaffold = 24, BeltRed = 25, BeltWhite = 26, BeltBlue = 27, LampYellow = 28,
        StripeRed = 29, StripeTeal = 30, StripeYellow = 31, StripeBlue = 32, SeatRed = 33, SeatGreen = 34, BannerBlue = 35;

    static readonly Color32[] Swatch =
    {
        new Color32(170, 170, 162, 255), new Color32(118, 118, 113, 255), new Color32(196, 30, 32, 255), new Color32(236, 236, 230, 255),
        new Color32(228, 186, 30, 255), new Color32(32, 82, 176, 255), new Color32(186, 191, 197, 255), new Color32(68, 70, 74, 255),
        new Color32(24, 24, 26, 255), new Color32(226, 228, 231, 255), new Color32(24, 24, 27, 255), new Color32(255, 46, 30, 255),
        new Color32(168, 170, 168, 255), new Color32(232, 232, 236, 255), new Color32(104, 106, 112, 255), new Color32(34, 68, 148, 255),
        new Color32(138, 138, 143, 255), new Color32(236, 234, 226, 255), new Color32(158, 163, 168, 255), new Color32(38, 52, 66, 255),
        new Color32(238, 118, 22, 255), new Color32(82, 83, 87, 255), new Color32(240, 240, 238, 255), new Color32(22, 108, 60, 255),
        new Color32(58, 60, 64, 255), new Color32(204, 36, 34, 255), new Color32(232, 232, 230, 255), new Color32(34, 74, 168, 255),
        new Color32(255, 204, 44, 255), new Color32(178, 30, 40, 255), new Color32(20, 140, 140, 255), new Color32(236, 190, 30, 255),
        new Color32(30, 70, 160, 255), new Color32(170, 34, 38, 255), new Color32(30, 120, 64, 255), new Color32(28, 60, 130, 255),
    };

    // UV rect (u0, v0, u1, v1) of a swatch: a single point at the cell centre
    public static Vector4 Sw(int s)
    {
        float u = ((s % 16) * 16 + 8) / (float)AS, v = ((s / 16) * 16 + 8) / (float)AS;
        return new Vector4(u, v, u, v);
    }
    static Vector4 Reg(int x, int y, int w, int h) => new Vector4((x + 1f) / AS, (y + 1f) / AS, (x + w - 1f) / AS, (y + h - 1f) / AS);
    public static readonly Vector4 Door = Reg(0, 64, 64, 64), Glass = Reg(64, 64, 64, 64), TyreTops = Reg(128, 64, 64, 64), Scuff = Reg(192, 64, 64, 64);
    public const int AdCount = 8;
    public static Vector4 Ad(int k) { k = ((k % AdCount) + AdCount) % AdCount; return Reg((k % 2) * 128, 128 + (k / 2) * 32, 128, 32); }
    // part of a region: u from a to b, v from c to d (fractions of the region)
    public static Vector4 Sub(Vector4 r, float a, float b, float c, float d)
        => new Vector4(r.x + (r.z - r.x) * a, r.y + (r.w - r.y) * c, r.x + (r.z - r.x) * b, r.y + (r.w - r.y) * d);

    // ---------------------------------------------------------------- invented advertisers
    static readonly string[] AdNames = { "VORLEK", "ZANTRO", "QUELBA", "NIMRAX", "KAVBRO", "OBRANI", "TEMPIX", "HULGEN" };
    static readonly Color32[] AdBack = {
        new Color32(24, 64, 160, 255), new Color32(240, 196, 30, 255), new Color32(20, 120, 70, 255), new Color32(236, 112, 24, 255),
        new Color32(240, 240, 236, 255), new Color32(20, 20, 24, 255), new Color32(196, 28, 34, 255), new Color32(18, 128, 136, 255) };
    static readonly Color32[] AdText = {
        new Color32(250, 250, 250, 255), new Color32(20, 20, 24, 255), new Color32(250, 250, 250, 255), new Color32(250, 250, 250, 255),
        new Color32(196, 28, 34, 255), new Color32(244, 200, 40, 255), new Color32(250, 250, 250, 255), new Color32(250, 250, 250, 255) };
    // 5 x 7 pixel letters (top row first)
    static string[] Glyph(char ch)
    {
        switch (ch)
        {
            case 'A': return new[] { " ### ", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" };
            case 'B': return new[] { "#### ", "#   #", "#   #", "#### ", "#   #", "#   #", "#### " };
            case 'E': return new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#####" };
            case 'G': return new[] { " ### ", "#   #", "#    ", "# ###", "#   #", "#   #", " ### " };
            case 'H': return new[] { "#   #", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" };
            case 'I': return new[] { " ### ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### " };
            case 'K': return new[] { "#   #", "#  # ", "# #  ", "##   ", "# #  ", "#  # ", "#   #" };
            case 'L': return new[] { "#    ", "#    ", "#    ", "#    ", "#    ", "#    ", "#####" };
            case 'M': return new[] { "#   #", "## ##", "# # #", "# # #", "#   #", "#   #", "#   #" };
            case 'N': return new[] { "#   #", "##  #", "# # #", "#  ##", "#   #", "#   #", "#   #" };
            case 'O': return new[] { " ### ", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " };
            case 'P': return new[] { "#### ", "#   #", "#   #", "#### ", "#    ", "#    ", "#    " };
            case 'Q': return new[] { " ### ", "#   #", "#   #", "#   #", "# # #", "#  # ", " ## #" };
            case 'R': return new[] { "#### ", "#   #", "#   #", "#### ", "# #  ", "#  # ", "#   #" };
            case 'T': return new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  " };
            case 'U': return new[] { "#   #", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " };
            case 'V': return new[] { "#   #", "#   #", "#   #", "#   #", "#   #", " # # ", "  #  " };
            case 'X': return new[] { "#   #", "#   #", " # # ", "  #  ", " # # ", "#   #", "#   #" };
            case 'Z': return new[] { "#####", "    #", "   # ", "  #  ", " #   ", "#    ", "#####" };
            default: return null;
        }
    }

    static float Hash(int x, int y, int s)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + s * 2147483;
            h = (h ^ (int)((uint)h >> 13)) * 1274126177;
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296f;
        }
    }
    static byte B(float v) => (byte)Mathf.Clamp(v, 0, 255);
    static Color32 Mul(Color32 c, float k) => new Color32(B(c.r * k), B(c.g * k), B(c.b * k), c.a);

    // ---------------------------------------------------------------- atlas
    public static Texture2D Atlas()
    {
        var px = new Color32[AS * AS];
        for (int y = 0; y < AS; y++)
            for (int x = 0; x < AS; x++)
            {
                Color32 c;
                if (y < 64)
                {
                    int s = (y / 16) * 16 + x / 16;
                    c = s < Swatch.Length ? Swatch[s] : new Color32(128, 128, 128, 255);
                }
                else if (y < 128)
                {
                    int lx = x % 64, ly = y - 64, reg = x / 64;
                    float n = Hash(x, y, 5);
                    if (reg == 0)
                    {
                        // roller shutter: horizontal ribs, darker frame
                        bool frame = lx < 3 || lx > 60 || ly > 60;
                        float k = frame ? 0.45f : (ly % 4 == 0 ? 0.62f : 0.82f + 0.06f * n);
                        c = Mul(new Color32(196, 199, 204, 255), k);
                    }
                    else if (reg == 1)
                    {
                        // glazing: 2 x 2 panes with mullions, sky reflection brighter at the top
                        bool mull = lx % 32 < 2 || ly % 32 < 2;
                        float k = 0.75f + 0.5f * ly / 64f + 0.05f * n;
                        c = mull ? new Color32(60, 62, 66, 255) : Mul(new Color32(70, 98, 124, 255), k);
                    }
                    else if (reg == 2)
                    {
                        // tyre tops seen from above: 4 x 4 rings, 16 px each
                        float cx = lx % 16 - 7.5f, cy = ly % 16 - 7.5f, r = Mathf.Sqrt(cx * cx + cy * cy);
                        float k = r < 3.2f ? 0.35f : r < 7.4f ? (r > 6.4f ? 1.25f : 0.85f + 0.1f * n) : 0.25f;
                        c = Mul(new Color32(40, 40, 42, 255), k);
                    }
                    else
                    {
                        // concrete with black rubber smears and scrapes low down (impact marks)
                        float k = 0.9f + 0.12f * n;
                        var bc = Mul(Swatch[Concrete], k);
                        float smear = Mathf.Clamp01(1.2f - ly / 30f) * (0.5f + 0.5f * Mathf.Sin(lx * 0.31f + Hash(lx / 9, 0, 7) * 6f));
                        if (Hash(lx, ly / 3, 9) > 0.86f && ly < 40) smear = Mathf.Max(smear, 0.8f);
                        c = Mul(bc, 1f - 0.75f * smear);
                    }
                }
                else
                {
                    int k = (x / 128) + ((y - 128) / 32) * 2, lx = x % 128, ly = (y - 128) % 32;
                    c = AdPixel(k, lx, ly);
                }
                px[y * AS + x] = c;
            }
        var t = new Texture2D(AS, AS, TextureFormat.RGBA32, 5, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }

    static Color32 AdPixel(int k, int lx, int ly)
    {
        var bg = AdBack[k]; var fg = AdText[k];
        if (ly < 2 || ly > 29) return Mul(bg, 0.7f); // thin darker edge
        // invented mark: a slanted bar left of the name
        int sx = lx - 4 - (ly - 4) / 3;
        if (ly >= 5 && ly <= 26 && sx >= 0 && sx < 7) return fg;
        string name = AdNames[k];
        int x0 = 20, gx = lx - x0, gy = ly - 5;
        if (gx >= 0 && gy >= 0 && gy < 21 && gx < name.Length * 18 && gx % 18 < 15)
        {
            var g = Glyph(name[gx / 18]);
            if (g != null && g[6 - gy / 3][(gx % 18) / 3] == '#') return fg;
        }
        return bg;
    }

    // ---------------------------------------------------------------- crowd: 32 seats x 4 density bands
    public static Texture2D Crowd()
    {
        const int W = 256, H = 64;
        Color32[] shirts = {
            new Color32(230, 40, 40, 255), new Color32(245, 245, 245, 255), new Color32(30, 30, 34, 255), new Color32(240, 130, 20, 255),
            new Color32(250, 210, 40, 255), new Color32(40, 90, 200, 255), new Color32(30, 140, 70, 255), new Color32(120, 120, 128, 255),
            new Color32(200, 60, 140, 255), new Color32(90, 170, 220, 255) };
        Color32[] skin = { new Color32(236, 196, 160, 255), new Color32(200, 150, 110, 255), new Color32(140, 96, 64, 255), new Color32(90, 60, 42, 255) };
        float[] fill = { 0.96f, 0.9f, 0.78f, 0.5f };
        var px = new Color32[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int band = y / 16, ly = y % 16, seat = x / 8, lx = x % 8;
                float occ = Hash(seat, band, 21);
                Color32 seatCol = (band & 1) == 0 ? new Color32(40, 64, 120, 255) : new Color32(70, 74, 82, 255);
                Color32 c = ly < 3 ? Mul(seatCol, 0.55f) : seatCol; // seat edge
                if (occ < fill[band])
                {
                    var sh = shirts[(int)(Hash(seat, band, 22) * shirts.Length) % shirts.Length];
                    var sk = skin[(int)(Hash(seat, band, 23) * skin.Length) % skin.Length];
                    int dx = Hash(seat, band, 24) > 0.5f ? 1 : 0; // lean a little left or right
                    if (ly >= 2 && ly <= 9 && lx >= 1 && lx <= 6) c = Mul(sh, 0.85f + 0.2f * Hash(x, y, 25));
                    if (ly >= 10 && ly <= 13 && lx >= 2 + dx && lx <= 4 + dx) c = ly == 13 ? Mul(sk, 0.45f) : sk; // head, hair on top
                    if (Hash(seat, band, 26) > 0.93f && ly >= 13 && lx >= 1 && lx <= 6) c = shirts[(seat + band) % shirts.Length]; // flag / scarf held up
                }
                px[y * W + x] = c;
            }
        var t = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }

    // ---------------------------------------------------------------- chain-link fence (1 m tile), alpha-tested
    public static Texture2D Fence()
    {
        const int S = 64;
        var t = new Texture2D(S, S, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 2 };
        var lvl = new Color32[S * S];
        var a = new float[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d1 = Mathf.Abs(((x + y) % 8) - 4f), d2 = Mathf.Abs((((x - y) % 8) + 8) % 8 - 4f);
                float w = Mathf.Max(Mathf.Clamp01(1.6f - (4f - d1) * 1.1f), Mathf.Clamp01(1.6f - (4f - d2) * 1.1f));
                if (y < 2) w = 1; // horizontal tension wire once per metre
                a[y * S + x] = w;
            }
        // level 0 and boosted-alpha box-filtered levels
        int size = S; var cur = a;
        for (int m = 0; m < t.mipmapCount; m++)
        {
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++)
            {
                float al = Mathf.Clamp01(cur[i]);
                px[i] = new Color32(150, 156, 162, B(al * 255));
            }
            t.SetPixels32(px, m);
            if (size == 1) break;
            int ns = size / 2; var nx = new float[ns * ns];
            for (int y = 0; y < ns; y++)
                for (int x = 0; x < ns; x++)
                {
                    float s = cur[(2 * y) * size + 2 * x] + cur[(2 * y) * size + 2 * x + 1] + cur[(2 * y + 1) * size + 2 * x] + cur[(2 * y + 1) * size + 2 * x + 1];
                    nx[y * ns + x] = Mathf.Min(1f, s * 0.25f * 1.7f);
                }
            cur = nx; size = ns;
        }
        t.Apply(false, true);
        return t;
    }

    // ---------------------------------------------------------------- ground grime: 4 columns of 16 px, v along the strip
    public const int GrimeShade = 0, GrimeTyre = 1, GrimeDirt = 2, GrimeSmear = 3;
    public static Vector4 GrimeCol(int k) => new Vector4((k * 16 + 1.5f) / 64f, 0, (k * 16 + 14.5f) / 64f, 1);
    public static Texture2D Grime()
    {
        const int S = 64;
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                int col = x / 16; float u = (x % 16 + 0.5f) / 16f, n = Hash(x, y, 31), n2 = Hash(x / 4, y / 6, 32);
                Color32 c;
                switch (col)
                {
                    case 0: c = new Color32(0, 0, 0, B(150 * u * u)); break; // darkest against the wall (u = 1)
                    case 1:
                        {
                            float e = 1f - Mathf.Abs(u - 0.5f) * 2.2f; e = Mathf.Clamp01(e * 2f);
                            c = new Color32(8, 8, 9, B(200 * e * (0.55f + 0.45f * n2)));
                            break;
                        }
                    case 2:
                        {
                            float e = Mathf.Clamp01(1f - Mathf.Abs(u - 0.5f) * 2f);
                            c = new Color32(96, 84, 66, B(170 * e * (n > 0.4f ? 1f : 0.3f) * (0.4f + 0.6f * n2)));
                            break;
                        }
                    default:
                        {
                            float e = Mathf.Clamp01(1f - Mathf.Abs(u - 0.5f) * 2f), v = Mathf.Sin(y / (float)S * Mathf.PI);
                            c = new Color32(10, 10, 11, B(150 * e * v * (0.7f + 0.3f * n)));
                            break;
                        }
                }
                px[y * S + x] = c;
            }
        var t = new Texture2D(S, S, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }
}
