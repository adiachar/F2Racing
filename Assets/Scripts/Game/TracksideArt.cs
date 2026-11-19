// TracksideArt.cs — the procedural textures the trackside is drawn with (made once per circuit load, no files):
//  * Atlas (1024 x 512, 5 mip levels so regions never bleed into each other up close). The bottom-left 512 x 256 is the
//    old atlas (below) except that its two advertising blocks (y 128..255) now hold architecture detail: glass curtain
//    wall, steel truss, standing-seam roof, louvres, glass balustrade, vomitory, big screen, poured concrete, scaffold,
//    hazard stripes. The advertising boards moved to 16 boards of 256 x 64 (x 0..511 y 256..511 and x 512..1023
//    y 0..255) with smooth slanted lettering instead of 3 px blocks; x 512..1023 y 256..319 is the circuit's name sign.
//    x 512..1023 y 320..511: four grandstand name banners (512 x 32, invented names), the race-control sign and the
//    pit-lane floor lettering ("SPEED LIMIT 80", white paint on pit asphalt).
//    Old layout of the bottom-left block:
//      left half (x 0..255), as before:
//        y   0..63   64 flat colour swatches (16 x 4 cells of 16 px); flat-coloured parts point all their UVs at a cell centre
//        y  64..127  garage door | glass windows | tyre-wall tops | scuffed concrete (64 x 64 each)
//        y 128..255  eight 128 x 32 advertising boards with INVENTED names (no real teams, sponsors or car makers)
//      right half (x 256..511), surface detail so walls and barriers are not flat-coloured boxes:
//        y   0..63   precast concrete block (128 x 64: end joints, tie holes, rain streaks, splash dirt at the foot) |
//                    galvanised Armco rail (64 x 64: zinc mottling, rolled edges, splice bolts, grime low down) |
//                    conveyor belt red | white (32 x 64 each: bolt rows, overlap seam, tyre scuffs)
//        y  64..127  ribbed steel cladding (grandstands, garage backs) | composite wall panels (pit building) |
//                    a second, newer concrete block (128 x 64) so neighbouring blocks differ
//        y 128..255  eight more advertising boards (condensed lettering, a mark block on the left)
//    So every opaque trackside part still shares one material: one draw call.
//  * Crowd: 512 x 128, seated people (shirts, heads, caps, raised arms, flags, banners) in four bands, tiled along grandstand rows.
//  * Fence: chain-link diamonds and steel cables in alpha for the alpha-tested debris/catch fence; the mip levels keep
//    (nearly) the mesh's true coverage, which BakedCutout turns into a light veil in the distance (MSAA coverage).
//  * Grime: soft dark ground decals in four columns (base shadow, tyre mark, dirt, rubber smear).
//  Atlas alpha: 1 everywhere except glass (glazing, curtain-wall panes, balustrades, the dark-glass swatch), where it
//  is lower the more mirror-like the glass; Baked.shader's F2_GLASS (Medium / High) reflects the sky there.
using UnityEngine;

public static class TracksideArt
{
    public const int AS = 256, AW = 1024, AH = 512; // AS: the old 256 square part (swatches, regions, first ads)

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
        float u = ((s % 16) * 16 + 8) / (float)AW, v = ((s / 16) * 16 + 8) / (float)AH;
        return new Vector4(u, v, u, v);
    }
    static Vector4 Reg(int x, int y, int w, int h) => new Vector4((x + 1f) / AW, (y + 1f) / AH, (x + w - 1f) / AW, (y + h - 1f) / AH);
    public static readonly Vector4 Door = Reg(0, 64, 64, 64), Glass = Reg(64, 64, 64, 64), TyreTops = Reg(128, 64, 64, 64), Scuff = Reg(192, 64, 64, 64);
    // surface detail regions (each is mapped whole onto one block / rail / panel, see Trackside.Pieces)
    public static readonly Vector4 ConcreteBlock = Reg(256, 0, 128, 64), ConcreteBlock2 = Reg(384, 64, 128, 64), Armco = Reg(384, 0, 64, 64),
        BeltRedR = Reg(448, 0, 32, 64), BeltWhiteR = Reg(480, 0, 32, 64), Cladding = Reg(256, 64, 64, 64), PanelWall = Reg(320, 64, 64, 64);
    // architecture detail (each mapped whole onto one bay / panel; see the pixel functions below)
    public static readonly Vector4 GlassWall = Reg(0, 128, 128, 128), Truss = Reg(128, 128, 128, 64), RoofSeam = Reg(128, 192, 64, 64),
        Louvre = Reg(192, 192, 64, 64), Balustrade = Reg(256, 128, 64, 64), Vomitory = Reg(320, 128, 64, 64), Screen = Reg(384, 128, 128, 64),
        ConcreteWall = Reg(256, 192, 128, 64), ScaffoldR = Reg(384, 192, 64, 64), Hazard = Reg(448, 192, 64, 64), NameSign = Reg(512, 256, 512, 64);
    // grandstand name banners (k 0..3), race control, pit-lane floor lettering
    public static Vector4 StandBanner(int k) { k = ((k % 4) + 4) % 4; return Reg(512, 320 + k * 32, 512, 32); }
    public static readonly Vector4 RaceControl = Reg(512, 448, 512, 32), PitText = Reg(512, 480, 512, 32);
    static readonly string[] StandNames = { "TRIBUNA AURORA", "TRIBUNA DEL PARCO", "TRIBUNA VELOCE", "TRIBUNA SOLE" };
    static readonly Color32[] StandBack = { new Color32(150, 22, 30, 255), new Color32(22, 40, 92, 255), new Color32(18, 92, 52, 255), new Color32(26, 26, 30, 255) };
    // x 512..1023, y 320..511 (lx 0..511, ly 0..191)
    static Color32 ExtraPixel(int lx, int ly)
    {
        int row = ly / 32, y = ly % 32;
        string txt; Color32 bg, fg;
        if (row < 4) { txt = StandNames[row]; bg = StandBack[row]; fg = new Color32(246, 244, 236, 255); }
        else if (row == 4) { txt = "RACE CONTROL"; bg = new Color32(34, 36, 40, 255); fg = new Color32(240, 240, 236, 255); }
        else { txt = "SPEED LIMIT 80"; bg = Swatch[PitAsphalt]; fg = new Color32(232, 232, 228, 255); }
        float cw = 3.2f, adv = 6f, x0 = (512f - txt.Length * adv * cw) * 0.5f + cw * 0.5f;
        float cov = TextCov(txt, lx, y, x0, 28f, cw, 3.4f, adv, 0f);
        var c = Mix(bg, fg, cov);
        if (row <= 4)
        {
            // banners: a thin white keyline and a darker hem top and bottom (the cloth's tensioned edge)
            if (y < 2 || y > 29) c = Mul(bg, 0.7f);
            else if (y < 3 || y > 28) c = Mix(c, fg, 0.6f);
            if (row < 4 && (lx < 3 || lx > 508)) c = Mul(c, 0.6f); // eyelets / frame
        }
        else c = Mul(c, 0.94f + 0.08f * Hash(lx / 2, y / 2, 611)); // painted on asphalt
        return c;
    }
    public const int AdCount = 16;
    public static Vector4 Ad(int k)
    {
        k = ((k % AdCount) + AdCount) % AdCount;
        return k < 8 ? Reg((k % 2) * 256, 256 + (k / 2) * 64, 256, 64) : Reg(512 + (k % 2) * 256, ((k - 8) / 2) * 64, 256, 64);
    }
    // part of a region: u from a to b, v from c to d (fractions of the region)
    public static Vector4 Sub(Vector4 r, float a, float b, float c, float d)
        => new Vector4(r.x + (r.z - r.x) * a, r.y + (r.w - r.y) * c, r.x + (r.z - r.x) * b, r.y + (r.w - r.y) * d);

    // ---------------------------------------------------------------- invented advertisers
    static readonly string[] AdNames = { "VORLEK", "ZANTRO", "QUELBA", "NIMRAX", "KAVBRO", "OBRANI", "TEMPIX", "HULGEN",
                                         "BRELTO", "VASKIN", "MORLEX", "SKYRAL", "FENRIQ", "CORVIK", "YALDEN", "WIXORA" };
    static readonly Color32[] AdBack = {
        new Color32(24, 64, 160, 255), new Color32(240, 196, 30, 255), new Color32(20, 120, 70, 255), new Color32(236, 112, 24, 255),
        new Color32(240, 240, 236, 255), new Color32(20, 20, 24, 255), new Color32(196, 28, 34, 255), new Color32(18, 128, 136, 255),
        new Color32(248, 248, 246, 255), new Color32(28, 28, 32, 255), new Color32(118, 22, 38, 255), new Color32(78, 38, 136, 255),
        new Color32(0, 146, 200, 255), new Color32(236, 236, 234, 255), new Color32(18, 62, 40, 255), new Color32(250, 198, 0, 255) };
    static readonly Color32[] AdText = {
        new Color32(250, 250, 250, 255), new Color32(20, 20, 24, 255), new Color32(250, 250, 250, 255), new Color32(250, 250, 250, 255),
        new Color32(196, 28, 34, 255), new Color32(244, 200, 40, 255), new Color32(250, 250, 250, 255), new Color32(250, 250, 250, 255),
        new Color32(22, 42, 112, 255), new Color32(124, 204, 58, 255), new Color32(250, 250, 250, 255), new Color32(250, 214, 50, 255),
        new Color32(250, 250, 250, 255), new Color32(206, 28, 40, 255), new Color32(240, 200, 60, 255), new Color32(20, 20, 24, 255) };
    // 5 x 7 pixel letters (top row first)
    static string[] Glyph(char ch)
    {
        switch (ch)
        {
            case 'A': return new[] { " ### ", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" };
            case 'B': return new[] { "#### ", "#   #", "#   #", "#### ", "#   #", "#   #", "#### " };
            case 'C': return new[] { " ### ", "#   #", "#    ", "#    ", "#    ", "#   #", " ### " };
            case 'D': return new[] { "#### ", "#   #", "#   #", "#   #", "#   #", "#   #", "#### " };
            case 'E': return new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#####" };
            case 'F': return new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#    " };
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
            case 'S': return new[] { " ####", "#    ", "#    ", " ### ", "    #", "    #", "#### " };
            case 'T': return new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  " };
            case 'U': return new[] { "#   #", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " };
            case 'V': return new[] { "#   #", "#   #", "#   #", "#   #", "#   #", " # # ", "  #  " };
            case 'W': return new[] { "#   #", "#   #", "#   #", "# # #", "# # #", "## ##", "#   #" };
            case 'X': return new[] { "#   #", "#   #", " # # ", "  #  ", " # # ", "#   #", "#   #" };
            case 'Y': return new[] { "#   #", "#   #", " # # ", "  #  ", "  #  ", "  #  ", "  #  " };
            case 'Z': return new[] { "#####", "    #", "   # ", "  #  ", " #   ", "#    ", "#####" };
            case '8': return new[] { " ### ", "#   #", "#   #", " ### ", "#   #", "#   #", " ### " };
            case '0': return new[] { " ### ", "#   #", "#  ##", "# # #", "##  #", "#   #", " ### " };
            default: return null;
        }
    }

    // smooth lettering: the 5 x 7 bitmap read with bilinear filtering and a threshold, so strokes get round corners and
    // clean diagonals at any size. gx 0..5 across, gy 0..7 from the top; returns 0..1 (0.5 = the outline)
    static readonly System.Collections.Generic.Dictionary<char, string[]> glyphs = new System.Collections.Generic.Dictionary<char, string[]>();
    static float Bit(string[] g, int x, int y) => x >= 0 && x < 5 && y >= 0 && y < 7 && g[y][x] == '#' ? 1f : 0f;
    static float GlyphCov(char ch, float gx, float gy)
    {
        string[] g;
        if (!glyphs.TryGetValue(ch, out g)) { g = Glyph(ch); glyphs[ch] = g; }
        if (g == null) return 0f;
        float fx = gx - 0.5f, fy = gy - 0.5f; int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy); float a = fx - ix, b = fy - iy;
        float v0 = Bit(g, ix, iy) + (Bit(g, ix + 1, iy) - Bit(g, ix, iy)) * a, v1 = Bit(g, ix, iy + 1) + (Bit(g, ix + 1, iy + 1) - Bit(g, ix, iy + 1)) * a;
        return v0 + (v1 - v0) * b;
    }
    // text coverage at pixel (lx, ly): letters cw x ch px per bitmap cell, adv cells per letter, top of the letters at yTop
    // (v runs up), slant = px of lean per px of height
    static float TextCov(string txt, float lx, float ly, float x0, float yTop, float cw, float ch, float adv, float slant)
    {
        float ty = (yTop - ly) / ch; if (ty < -0.6f || ty > 7.6f) return 0f;
        float u = (lx - x0 - (ly - (yTop - 3.5f * ch)) * slant) / cw; if (u < -0.6f) return 0f;
        int ci = Mathf.FloorToInt((u + 0.5f) / adv); if (ci < 0 || ci >= txt.Length) return 0f;
        float v = GlyphCov(txt[ci], u - ci * adv, ty);
        return Mathf.Clamp01((v - 0.45f) * Mathf.Min(cw, ch) + 0.5f);
    }
    static Color32 Mix(Color32 a, Color32 b, float t) => new Color32(B(a.r + (b.r - a.r) * t), B(a.g + (b.g - a.g) * t), B(a.b + (b.b - a.b) * t), 255);

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
        var px = new Color32[AW * AH];
        for (int y = 0; y < AH; y++)
            for (int x = 0; x < AW; x++)
            {
                Color32 c;
                if (y >= 256)
                {
                    if (x < 512) c = AdPixel((x / 256) + ((y - 256) / 64) * 2, x % 256, (y - 256) % 64);
                    else if (y < 320) c = SignPixel(x - 512, y - 256);
                    else c = ExtraPixel(x - 512, y - 320);
                }
                else if (x >= 512) c = AdPixel(8 + ((x - 512) / 256) + (y / 64) * 2, (x - 512) % 256, y % 64);
                else if (y >= 128) c = ArchPixel(x, y - 128);
                else if (x >= AS) c = DetailPixel(x - AS, y);
                else if (y < 64)
                {
                    int s = (y / 16) * 16 + x / 16;
                    c = s < Swatch.Length ? Swatch[s] : new Color32(128, 128, 128, 255);
                    if (s == GlassDark) c.a = 60; // glass (Baked F2_GLASS mirrors the sky in it)
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
                        c = mull ? new Color32(60, 62, 66, 255) : Mul(new Color32(70, 98, 124, 70), k); // (alpha < 1: glass, Baked F2_GLASS)
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
                else c = new Color32(128, 128, 128, 255);
                px[y * AW + x] = c;
            }
        var t = new Texture2D(AW, AH, TextureFormat.RGBA32, 5, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }

    // one advertising board, 256 x 64 (~5.6 x 1.1 m): invented name in smooth slanted capitals with an invented mark;
    // a little road dirt along the foot and a darker edge
    static Color32 AdPixel(int k, int lx, int ly)
    {
        k = ((k % AdCount) + AdCount) % AdCount;
        var bg = AdBack[k]; var fg = AdText[k];
        float cov;
        if (k >= 8)
        {
            // second set: a block in the text colour with the mark (ring or chevron) cut out, condensed upright name
            if (lx >= 4 && lx <= 57)
            {
                float mx = (lx - 30.5f) * 0.5f, my = (ly - 31.5f) * 0.5f, r = Mathf.Sqrt(mx * mx + my * my);
                float mark = (k & 1) == 0 ? Mathf.Clamp01(1.75f - Mathf.Abs(r - 7.25f)) * 1f : Mathf.Clamp01(2.2f - Mathf.Abs(Mathf.Abs(my) * 0.9f - (mx + 4f))) * Mathf.Clamp01(9f - Mathf.Abs(my));
                cov = 1f - Mathf.Clamp01(mark * 2f);
            }
            else cov = TextCov(AdNames[k], lx, ly, 68f, 52f, 4.6f, 6f, 6.4f, 0f);
        }
        else
        {
            // first set: a slanted bar left of a slanted name
            float sx = lx - 8f - (ly - 10f) * 0.3f;
            float bar = Mathf.Clamp01(Mathf.Min(sx + 0.5f, 14.5f - sx)) * Mathf.Clamp01(Mathf.Min(ly - 9.5f, 53.5f - ly));
            cov = Mathf.Max(bar, TextCov(AdNames[k], lx, ly, 42f, 52f, 5.7f, 6f, 6.1f, 0.2f));
        }
        var c = Mix(bg, fg, cov);
        float sh = 0.96f + 0.06f * ly / 64f;
        sh *= 1f - 0.16f * Mathf.Clamp01(1f - ly / (5f + 5f * Noise(lx / 7f, 0f, 391 + k)));  // splash dirt at the foot
        if (ly < 2 || ly > 61 || lx < 1 || lx > 254) sh *= 0.7f;                               // frame
        return Mul(c, sh);
    }

    // the circuit's name on the pit building and the bridges, 512 x 64: dark blue capitals on white, tricolour line below
    static Color32 SignPixel(int lx, int ly)
    {
        var bg = new Color32(240, 240, 236, 255);
        if (ly < 7) bg = lx < 171 ? new Color32(30, 130, 70, 255) : lx < 342 ? new Color32(240, 240, 236, 255) : new Color32(196, 32, 38, 255);
        float cov = TextCov("AUTODROMO DEL PARCO", lx, ly, 30f, 54f, 4f, 5.6f, 6f, 0f);
        var c = Mix(bg, new Color32(22, 40, 92, 255), cov);
        return ly > 61 || lx < 1 || lx > 510 ? Mul(c, 0.75f) : c;
    }

    // ---------------------------------------------------------------- architecture detail (x 0..511, y 128..255 of the atlas)
    // lx 0..511, ly 0..127
    static Color32 ArchPixel(int lx, int ly)
    {
        if (lx < 128) return GlassWallPixel(lx, ly);
        if (lx < 256)
        {
            if (ly < 64) return TrussPixel(lx - 128, ly);
            return lx < 192 ? RoofSeamPixel(lx - 128, ly - 64) : LouvrePixel(lx - 192, ly - 64);
        }
        if (ly < 64)
        {
            if (lx < 320) return BalustradePixel(lx - 256, ly);
            if (lx < 384) return VomitoryPixel(lx - 320, ly);
            return ScreenPixel(lx - 384, ly);
        }
        if (lx < 384) return ConcreteWallPixel(lx - 256, ly - 64);
        return lx < 448 ? ScaffoldPixel(lx - 384, ly - 64) : HazardPixel(lx - 448, ly - 64);
    }

    // glass curtain wall, one 8 m bay x one storey (128 x 128): four panes, aluminium mullions, a back-painted spandrel
    // at the floor, the sky and the tree line mirrored in the glass, a blind drawn here and there
    static Color32 GlassWallPixel(int lx, int ly)
    {
        int pane = lx / 32, ix = lx % 32;
        if (ix < 2 || ly == 22 || ly == 23 || ly > 125) return Mul(new Color32(168, 172, 176, 255), ix == 0 ? 0.8f : 1f);
        float n = Noise(lx / 46f + 3f, ly / 20f, 401), n2 = Noise(lx / 11f, ly / 9f, 402);
        if (ly < 22) return Mul(new Color32(58, 74, 88, 255), 0.9f + 0.12f * n2);                  // spandrel
        float t = (ly - 24f) / 102f;                                                              // 0 foot .. 1 head
        var sky = Mix(new Color32(92, 122, 150, 255), new Color32(168, 198, 222, 255), t);
        var c = Mix(sky, new Color32(236, 240, 244, 255), Mathf.Clamp01((n - 0.58f) * 3f) * (0.35f + 0.65f * t)); // clouds
        float trees = Mathf.Clamp01((0.34f + 0.16f * Noise(lx / 9f, 0f, 403) - t) * 9f);          // the tree line across the track
        c = Mix(c, new Color32(40, 60, 48, 255), trees * 0.85f);
        bool blind = Hash(pane, 7, 404) > 0.8f && t > 0.42f;
        if (blind) c = Mix(c, new Color32(206, 206, 198, 255), 0.75f);
        c.a = (byte)(blind ? 200 : 40); // glass: alpha < 1 (Baked F2_GLASS mirrors the real sky in it; a drawn blind less)
        return Mul(c, (0.95f + 0.06f * Hash(pane, 3, 405)) * (ix < 4 ? 0.82f : 1f));
    }

    // steel lattice girder (128 x 64) in front of the shade behind it: chords, posts every 32 px, alternating diagonals
    static Color32 TrussPixel(int lx, int ly)
    {
        int ix = lx % 32, bayNo = lx / 32;
        float dg = (bayNo & 1) == 0 ? ix - (ly - 8f) * (32f / 48f) : (31 - ix) - (ly - 8f) * (32f / 48f);
        bool steel = ly < 8 || ly > 55 || ix < 3 || Mathf.Abs(dg) < 2.2f;
        if (!steel) return Mul(new Color32(44, 47, 52, 255), 0.85f + 0.2f * ly / 64f);
        float k = 0.92f + 0.1f * Hash(lx, ly, 411);
        if (ly == 7 || ly == 56 || ix == 2) k *= 0.8f;
        return Mul(new Color32(206, 209, 212, 255), k);
    }

    // standing-seam metal roof (64 x 64, ~3 m): a seam every 8 px, faint streaks down the fall
    static Color32 RoofSeamPixel(int lx, int ly)
    {
        int r = lx % 8;
        float k = r == 0 ? 1.14f : r == 1 ? 0.78f : 0.97f + 0.05f * (Noise(lx / 3f, ly / 30f, 421) - 0.5f);
        k *= 0.96f + 0.08f * Noise(lx / 20f, ly / 20f, 422);
        return Tone(new Color32(178, 182, 186, 255), k, k, k * 1.01f);
    }

    // plant-room louvres in a frame (64 x 64)
    static Color32 LouvrePixel(int lx, int ly)
    {
        if (lx < 3 || lx > 60 || ly < 3 || ly > 60 || lx == 31 || lx == 32) return new Color32(150, 153, 156, 255);
        int r = ly % 5;
        return Mul(new Color32(142, 146, 150, 255), r == 0 ? 0.35f : r == 1 ? 0.6f : 0.9f + 0.03f * r);
    }

    // glass balustrade (64 x 64, ~2 m x 1.1 m): steel handrail, a post each end, clamps, greenish glass
    static Color32 BalustradePixel(int lx, int ly)
    {
        if (ly > 58) return new Color32(196, 200, 204, 255);
        if (lx < 2 || lx > 61) return new Color32(150, 154, 158, 255);
        if (ly < 3) return new Color32(120, 122, 126, 255);
        float t = ly / 58f;
        var c = Mix(new Color32(92, 124, 136, 255), new Color32(150, 186, 198, 255), t);
        c.a = 130; // glass, half mirror (Baked F2_GLASS)
        return Mul(c, 0.95f + 0.08f * Noise(lx / 14f, ly / 10f, 431));
    }

    // a vomitory in the stand's concrete (64 x 64, ~3 x 3 m): dark mouth, steps catching the light, centre handrail, a sign
    static Color32 VomitoryPixel(int lx, int ly)
    {
        float kc = 0.9f + 0.12f * (Noise(lx / 7f, ly / 7f, 441) - 0.5f);
        var conc = Mul(Swatch[Concrete], kc);
        if (ly >= 53 && ly <= 60 && lx >= 14 && lx <= 49) return ly >= 55 && ly <= 58 && lx >= 18 && lx <= 45 && (lx / 3) % 3 != 2 ? new Color32(236, 236, 232, 255) : new Color32(26, 70, 150, 255);
        if (lx < 10 || lx > 53 || ly > 49) return lx == 9 || lx == 54 || ly == 50 ? Mul(conc, 0.7f) : conc;
        if ((lx == 31 || lx == 32) && ly < 30) return new Color32(226, 186, 40, 255);
        float deep = ly / 50f;                                               // darker towards the head of the opening
        int step = ly % 6;
        float k = ly < 30 ? (step == 5 ? 0.75f : step == 4 ? 0.5f : 0.3f) * (1f - 0.7f * deep) : 0.1f;
        return Mul(Swatch[Concrete], Mathf.Max(0.07f, k));
    }

    // a big screen (128 x 64): bezel, a timing tower on the left, the "picture" (sky, trees, track, a red car) beside it
    static Color32 ScreenPixel(int lx, int ly)
    {
        if (lx < 3 || lx > 124 || ly < 3 || ly > 60) return new Color32(18, 18, 20, 255);
        Color32 c;
        if (lx < 34)
        {
            int row = (ly - 4) / 6, ry = (ly - 4) % 6;
            c = new Color32(16, 22, 44, 255);
            if (ry > 0 && ry < 5 && row < 9)
            {
                if (lx >= 5 && lx <= 7) c = AdBack[(row * 5 + 2) % AdBack.Length];
                else if (lx >= 10 && lx <= 21 && Hash(lx / 2, row, 451) > 0.3f) c = new Color32(230, 232, 236, 255);
                else if (lx >= 24 && lx <= 31 && Hash(lx / 2, row, 452) > 0.4f) c = row == 0 ? new Color32(180, 80, 220, 255) : new Color32(120, 220, 120, 255);
            }
        }
        else
        {
            float t = ly / 64f, hz = 0.52f + 0.06f * Noise(lx / 12f, 0f, 453);
            c = t > hz + 0.14f ? Mix(new Color32(120, 170, 230, 255), new Color32(70, 120, 210, 255), (t - hz) * 2f)
              : t > hz ? Mul(new Color32(44, 104, 52, 255), 0.8f + 0.4f * Noise(lx / 4f, ly / 4f, 454))
              : Mul(new Color32(98, 100, 106, 255), 0.85f + 0.2f * t);
            float cx = (lx - 82f) / 13f, cy = (ly - 24f) / 5f;
            if (cx * cx + cy * cy < 1f) c = cy < -0.45f ? new Color32(20, 20, 22, 255) : new Color32(214, 30, 34, 255);
            if (Mathf.Abs(ly - 12f - (lx - 34f) * 0.1f) < 1.2f && (lx / 6) % 2 == 0) c = new Color32(230, 230, 226, 255);
        }
        return Mul(c, (ly & 1) == 0 ? 1.12f : 0.98f); // LED rows
    }

    // poured concrete (128 x 64, ~6 x 3 m): board marks, pour lines, rain streaks from the top, green-brown damp at the foot
    static Color32 ConcreteWallPixel(int lx, int ly)
    {
        float k = 0.88f + 0.16f * (Noise(lx / 13f, ly / 8f, 461) - 0.5f) + 0.07f * (Noise(lx / 2.5f, ly / 2.5f, 462) - 0.5f) + 0.04f * (Hash(lx, ly, 463) - 0.5f);
        if (ly % 21 == 0) k *= 0.9f;
        if (lx % 64 == 0) k *= 0.86f;
        float streak = Mathf.Clamp01((Noise(lx / 2.2f, ly / 50f, 464) - 0.5f) * 3.5f) * Mathf.Clamp01(ly / 64f + 0.1f);
        k *= 1f - 0.22f * streak;
        float foot = Mathf.Clamp01(1f - ly / (7f + 9f * Noise(lx / 6f, 0f, 465)));
        var c = Tone(Swatch[Concrete], k, k, k * 0.98f);
        return Tone(c, 1f - 0.42f * foot, 1f - 0.34f * foot, 1f - 0.5f * foot);
    }

    // scaffold tower face (64 x 64, ~2 x 2 m) wrapped in dark mesh: standards, ledgers, a brace
    static Color32 ScaffoldPixel(int lx, int ly)
    {
        bool tube = lx < 3 || lx > 60 || lx == 31 || lx == 32 || ly % 32 < 2 || Mathf.Abs((lx % 64) - (ly % 64)) < 2;
        if (tube) return Mul(new Color32(176, 180, 184, 255), 0.85f + 0.2f * Hash(lx, ly, 471));
        return Mul(new Color32(38, 42, 46, 255), ((lx + ly) & 1) == 0 ? 1.25f : 0.85f);
    }

    // yellow / black hazard chevrons (64 x 64)
    static Color32 HazardPixel(int lx, int ly)
    {
        bool y = ((lx + ly) / 8) % 2 == 0;
        float k = 0.92f + 0.1f * Noise(lx / 5f, ly / 5f, 481);
        return Mul(y ? new Color32(236, 190, 24, 255) : new Color32(26, 26, 28, 255), k);
    }

    // ---------------------------------------------------------------- surface detail (right half of the atlas)
    // smooth value noise (no tiling needed: each region is mapped whole onto one block, rail or panel)
    static float Noise(float x, float y, int s)
    {
        int i = Mathf.FloorToInt(x), j = Mathf.FloorToInt(y); float a = x - i, b = y - j;
        a = a * a * (3 - 2 * a); b = b * b * (3 - 2 * b);
        return Mathf.Lerp(Mathf.Lerp(Hash(i, j, s), Hash(i + 1, j, s), a), Mathf.Lerp(Hash(i, j + 1, s), Hash(i + 1, j + 1, s), a), b);
    }
    static Color32 Tone(Color32 c, float r, float g, float b) => new Color32(B(c.r * r), B(c.g * g), B(c.b * b), 255);

    // x 0..255, y 0..255 within the right half
    static Color32 DetailPixel(int x, int y)
    {
        if (y < 64)
        {
            if (x < 128) return ConcretePixel(x, y, 0);
            if (x < 192) return ArmcoPixel(x - 128, y);
            return BeltPixel((x - 192) % 32, y, x < 224);
        }
        if (y < 128)
        {
            if (x < 64) return CladPixel(x, y - 64);
            if (x < 128) return PanelPixel(x - 64, y - 64);
            return ConcretePixel(x - 128, y - 64, 1);
        }
        return new Color32(128, 128, 128, 255);
    }

    // one precast concrete block, 128 x 64 (~4 m x 1 m); v = 0 at the foot
    static Color32 ConcretePixel(int lx, int ly, int variant)
    {
        int sd = 300 + variant * 40;
        float k = 0.9f + 0.14f * (Noise(lx / 9f, ly / 6f, sd) - 0.5f) + 0.08f * (Noise(lx / 3f, ly / 2f, sd + 1) - 0.5f) + 0.05f * (Hash(lx, ly, sd + 2) - 0.5f);
        if (variant == 1) k += 0.05f;                                                   // the newer block is a little paler
        // rain streaks running down from the top, stronger near it
        float streak = Mathf.Clamp01((Noise(lx / 2.5f, ly / 40f, sd + 3) - 0.55f) * 4f) * Mathf.Clamp01(ly / 64f + 0.15f);
        k *= 1f - (variant == 1 ? 0.10f : 0.18f) * streak;
        // splash dirt at the foot, ragged
        float foot = Mathf.Clamp01(1f - ly / (9f + 6f * Noise(lx / 4f, 0, sd + 4)));
        // pour line and tie holes
        if (ly == 33) k *= 0.93f;
        float hx = Mathf.Min(Mathf.Abs(lx - 32f), Mathf.Abs(lx - 96f)), hy = Mathf.Min(Mathf.Abs(ly - 20f), Mathf.Abs(ly - 46f));
        if (hx * hx + hy * hy < 2.3f) k *= 0.6f;
        // block ends: a dark gap and a chamfered, lighter arris
        if (lx < 2 || lx > 125) k *= 0.5f; else if (lx < 4 || lx > 123) k *= 1.1f;
        var c = Tone(Swatch[Concrete], k, k, k * 0.99f);
        return Tone(c, 1f - 0.35f * foot, 1f - 0.38f * foot, 1f - 0.45f * foot);
    }

    // a galvanised W-beam rail section, 64 x 64 (~4 m); v 0..0.5 the lower face, 0.5..1 the upper face
    static Color32 ArmcoPixel(int lx, int ly)
    {
        float k = 0.88f + 0.16f * (Noise(lx / 3f, ly / 3f, 351) - 0.5f) + 0.06f * (Hash(lx, ly, 352) - 0.5f); // zinc spangle
        if (ly < 2 || ly > 61) k *= 1.12f;                 // rolled edges catch the light
        else if (ly == 31 || ly == 32) k *= 0.82f;         // the fold of the W
        // splice bolts at both ends and the post bolt in the middle
        foreach (float bx in new[] { 3.5f, 60.5f, 31.5f })
            foreach (float by in new[] { 16f, 47f })
            {
                if (bx == 31.5f && by == 47f) continue;
                float dx = lx - bx, dy = ly - by, d = dx * dx + dy * dy;
                if (d < 1.6f) k *= 0.45f; else if (d < 3.2f) k *= 1.15f;
            }
        if (lx < 1 || lx > 62) k *= 0.6f;                  // overlap of the next rail
        // road grime and a few rust-brown runs on the lower face
        float g = Mathf.Clamp01(1f - ly / 18f) * (0.5f + 0.5f * Noise(lx / 5f, 0, 353));
        float rust = Mathf.Clamp01((Noise(lx / 1.5f, ly / 20f, 354) - 0.78f) * 5f) * Mathf.Clamp01(1f - ly / 40f);
        var c = Tone(Swatch[Steel], k, k, k);
        return Tone(c, 1f - 0.3f * g - 0.1f * rust, 1f - 0.32f * g - 0.2f * rust, 1f - 0.34f * g - 0.32f * rust);
    }

    // tyre-wall conveyor belt, 32 x 64 (one ~2.4 m piece): bolt rows, an overlap seam, black tyre scuffs
    static Color32 BeltPixel(int lx, int ly, bool red)
    {
        var bc = red ? Swatch[BeltRed] : Swatch[BeltWhite];
        float k = 0.93f + 0.08f * (Noise(lx / 4f, ly / 4f, 361 + (red ? 0 : 7)) - 0.5f);
        if (ly < 2 || ly > 61) k *= 0.72f;
        if (lx < 2) k *= 0.6f;                                                     // overlap seam
        foreach (float by in new[] { 9f, 33f, 55f })
            for (int b = 0; b < 4; b++)
            {
                float dx = lx - (4f + b * 8f), dy = ly - by, d = dx * dx + dy * dy;
                if (d < 1.3f) { var bolt = new Color32(70, 72, 76, 255); return Tone(bolt, 1f, 1f, 1f); }
                if (d < 2.6f) k *= 0.8f;
            }
        float scuff = Mathf.Clamp01((Noise(lx / 5f, ly / 3f, 365 + (red ? 0 : 3)) - 0.62f) * 3f) * Mathf.Clamp01(1.2f - ly / 40f);
        k *= 1f - 0.55f * scuff;
        return Tone(bc, k, k, k);
    }

    // ribbed steel cladding, 64 x 64: a rib every 8 px (~0.5 m), weathered low down
    static Color32 CladPixel(int lx, int ly)
    {
        int r = lx % 8;
        float k = r == 0 ? 0.70f : r == 1 ? 0.86f : r == 4 ? 1.10f : r == 5 ? 1.04f : 0.97f;
        k *= 0.95f + 0.08f * (Noise(lx / 6f, ly / 10f, 371) - 0.5f);
        k *= 1f - 0.15f * Mathf.Clamp01(1f - ly / 10f);
        if (ly > 61) k *= 0.8f;                                  // flashing at the top
        return Tone(new Color32(168, 170, 168, 255), k, k, k * 1.01f);
    }

    // composite wall panels, 64 x 64: two panels each way with joints, a slight tone per panel, fixings
    static Color32 PanelPixel(int lx, int ly)
    {
        int px = lx / 32, py = ly / 32, ix = lx % 32, iy = ly % 32;
        float k = 0.97f + 0.05f * Hash(px, py, 381) + 0.03f * (Noise(lx / 8f, ly / 8f, 382) - 0.5f);
        if (ix == 0 || iy == 0) k *= 0.72f; else if (ix == 1 || iy == 1) k *= 1.04f;
        if ((ix == 3 || ix == 29) && (iy == 3 || iy == 29)) k *= 0.7f;
        k *= 1f - 0.12f * Mathf.Clamp01(1f - ly / 8f);
        return Tone(Swatch[BuildWhite], k, k, k);
    }

    // ---------------------------------------------------------------- crowd: 32 seats (16 x 32 px each) x 4 bands
    // band 0 mixed fans, band 1 mostly red (Ferrari), band 2 mixed and thinner, band 3 sparse. Seated people: shoulders,
    // head, hair or cap, sometimes both arms up, a flag pole, and now and then a banner across four seats. Only mid-sized
    // contrasts (no single-pixel detail), so the mips turn into soft coloured noise.
    public static Texture2D Crowd()
    {
        const int W = 512, H = 128, CW = 16, CH = 32;
        Color32[] shirts = {
            new Color32(230, 40, 40, 255), new Color32(245, 245, 245, 255), new Color32(30, 30, 34, 255), new Color32(240, 130, 20, 255),
            new Color32(250, 210, 40, 255), new Color32(40, 90, 200, 255), new Color32(30, 140, 70, 255), new Color32(120, 120, 128, 255),
            new Color32(200, 60, 140, 255), new Color32(90, 170, 220, 255), new Color32(150, 20, 30, 255), new Color32(60, 40, 90, 255) };
        Color32[] reds = { new Color32(225, 30, 35, 255), new Color32(190, 20, 30, 255), new Color32(240, 70, 60, 255) };
        Color32[] skin = { new Color32(236, 196, 160, 255), new Color32(214, 164, 124, 255), new Color32(168, 116, 80, 255), new Color32(110, 74, 50, 255), new Color32(80, 54, 40, 255) };
        Color32[] hair = { new Color32(30, 24, 20, 255), new Color32(70, 46, 28, 255), new Color32(150, 110, 60, 255), new Color32(210, 190, 140, 255), new Color32(120, 120, 120, 255) };
        float[] fill = { 0.9f, 0.9f, 0.82f, 0.55f };
        var px = new Color32[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int band = y / CH, ly = y % CH, seat = x / CW, lx = x % CW;
                // seats in a neutral light grey: Baked F2_CROWD colours them per seat block (uv1) where alpha is 1
                Color32 seatCol = new Color32(206, 206, 210, 255);
                Color32 c = ly < 4 ? Mul(seatCol, 0.45f) : Mul(seatCol, 0.8f + 0.2f * (lx > 1 && lx < 14 ? 1f : 0f)); // seat back with a dark gap under it
                Color32 seat0 = c;
                if (Hash(seat, band, 21) < fill[band])
                {
                    bool redBand = band == 1;
                    Color32 sh = (redBand ? Hash(seat, band, 40) < 0.7f : Hash(seat, band, 40) < 0.16f)
                        ? reds[(int)(Hash(seat, band, 41) * 3f) % 3]
                        : shirts[(int)(Hash(seat, band, 22) * shirts.Length) % shirts.Length];
                    Color32 sk = skin[(int)(Hash(seat, band, 23) * skin.Length) % skin.Length];
                    Color32 hr = hair[(int)(Hash(seat, band, 27) * hair.Length) % hair.Length];
                    int dx = (int)(Hash(seat, band, 24) * 3f) - 1;                        // lean -1, 0, +1
                    int cx = 8 + dx, hy = 21 + (int)(Hash(seat, band, 28) * 3f);           // head centre
                    bool cap = Hash(seat, band, 29) < (redBand ? 0.35f : 0.14f), arms = Hash(seat, band, 30) < 0.09f;
                    float sw = 0.88f + 0.22f * Hash(x / 2, y / 3, 25);
                    // torso with rounded shoulders
                    int half = ly >= 15 ? 4 - (ly - 14) / 2 : 5;
                    if (ly >= 4 && ly <= 17 && Mathf.Abs(lx - cx) <= half) c = Mul(sh, sw * (ly < 7 ? 0.7f : 1f));
                    // arms up: two skin columns beside the head, or a flag on a pole
                    if (arms && ly >= 14 && ly <= 29 && (lx == cx - 5 || lx == cx + 5) && lx > 0 && lx < 15) c = sk;
                    // head
                    float dxh = lx - cx, dyh = (ly - hy) * 0.9f;
                    if (dxh * dxh + dyh * dyh <= 10.5f) c = ly > hy + 1 && !cap ? hr : sk;
                    if (cap && ly >= hy + 2 && ly <= hy + 3 && Mathf.Abs(lx - cx) <= 4) c = Mul(sh, 1.1f);
                    if (ly >= 17 && ly <= 18 && Mathf.Abs(lx - cx) <= 1) c = Mul(sk, 0.9f);   // neck
                    if (Hash(seat, band, 26) > 0.94f)                                      // flag on a pole
                    {
                        if (lx == 13 && ly >= 8) c = new Color32(210, 210, 210, 255);
                        if (lx >= 9 && lx <= 12 && ly >= 24 && ly <= 30)
                            c = (seat & 1) == 0 ? (lx < 11 ? new Color32(30, 150, 60, 255) : new Color32(230, 40, 40, 255)) : (ly > 27 ? new Color32(245, 245, 245, 255) : new Color32(230, 40, 40, 255));
                    }
                }
                // banner stretched over a run of four seats
                int run = seat / 4;
                if (Hash(run, band, 50) > 0.955f && ly >= 8 && ly <= 15)
                {
                    bool two = Hash(run, band, 51) > 0.5f;
                    Color32 b0 = two ? new Color32(220, 30, 35, 255) : new Color32(25, 60, 150, 255), b1 = new Color32(245, 240, 225, 255);
                    bool txt = ly >= 10 && ly <= 13 && ((x / 3) % 3 != 2) && Hash(x / 3, run, 52) > 0.3f;
                    c = txt ? b1 : (ly == 8 || ly == 15 ? Mul(b1, 0.9f) : b0);
                }
                c.a = c.r == seat0.r && c.g == seat0.g && c.b == seat0.b ? (byte)255 : (byte)0; // seat (tinted) or person
                px[y * W + x] = c;
            }
        var t = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }

    // ---------------------------------------------------------------- chain-link fence (1 m tile), alpha-tested
    public static Texture2D Fence()
    {
        const int S = 64;
        // trilinear: the wire's alpha is blended between mip levels before the alpha test, so it does not pop or crawl
        var t = new Texture2D(S, S, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        var lvl = new Color32[S * S];
        var a = new float[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d1 = Mathf.Abs(((x + y) % 8) - 4f), d2 = Mathf.Abs((((x - y) % 8) + 8) % 8 - 4f);
                float w = Mathf.Max(Mathf.Clamp01(1.6f - (4f - d1) * 1.1f), Mathf.Clamp01(1.6f - (4f - d2) * 1.1f));
                if (y < 2 || y == 31 || y == 32) w = 1; // steel cables: one at each metre and a thinner one between
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
                px[i] = new Color32(86, 96, 94, B(al * 255)); // galvanised mesh gone dull (grey with a breath of green)
            }
            t.SetPixels32(px, m);
            if (size == 1) break;
            int ns = size / 2; var nx = new float[ns * ns];
            for (int y = 0; y < ns; y++)
                for (int x = 0; x < ns; x++)
                {
                    float s = cur[(2 * y) * size + 2 * x] + cur[(2 * y) * size + 2 * x + 1] + cur[(2 * y + 1) * size + 2 * x] + cur[(2 * y + 1) * size + 2 * x + 1];
                    // a mild boost only: far away the mesh must thin to a veil (BakedCutout: MSAA coverage) or vanish (no MSAA),
                    // never close up into an opaque wall that hides the trees behind it
                    nx[y * ns + x] = Mathf.Min(1f, s * 0.25f * 1.12f);
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
        var t = new Texture2D(S, S, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear };
        t.SetPixels32(px); t.Apply(true, true);
        return t;
    }

    // ---------------------------------------------------------------- start lights
    // The gantry's lamps are baked into the merged trackside mesh, so the lit lamps are one small extra mesh of ten
    // glowing quads laid just in front of them (same layout as Trackside's gantry at track point 0): one draw call,
    // rebuilt only when the count changes. litCount 0..5 = columns lit from the left; 0 hides it.
    static GameObject lightsGo; static Mesh lightsMesh; static FormulaSim.Track lightsFor; static int lightsLit = -1;
    public static void SetStartLights(int litCount, FormulaSim.Track t = null, Transform parent = null)
    {
        litCount = Mathf.Clamp(litCount, 0, 5);
        if (t != null && (lightsGo == null || lightsFor != t)) BuildStartLights(t, parent);
        if (lightsGo == null) return;
        if (litCount == lightsLit) return;
        lightsLit = litCount;
        lightsGo.SetActive(litCount > 0);
        if (litCount == 0) return;
        var cols = new Color32[40];
        for (int q = 0; q < 10; q++)
        {
            int col = q / 2; var c = col < litCount ? new Color32(255, 30, 20, 255) : new Color32(0, 0, 0, 0);
            for (int k = 0; k < 4; k++) cols[q * 4 + k] = c;
        }
        lightsMesh.colors32 = cols;
    }

    static void BuildStartLights(FormulaSim.Track t, Transform parent)
    {
        if (lightsGo != null) Object.Destroy(lightsGo);
        var p = t.pts[0];
        Vector3 c = new Vector3((float)p.x, 0f, (float)p.y), T = new Vector3((float)p.tx, 0f, (float)p.ty).normalized;
        Vector3 Nn = new Vector3((float)p.nx, 0f, (float)p.ny).normalized, up = Vector3.up;
        const float beamY = 6.8f; // as Trackside's gantry
        Vector3 panel = c + up * (beamY - 1.2f) - T * 0.3f;
        Vector3 right = Vector3.Cross(-T, up).normalized;
        var v = new Vector3[40]; var tri = new int[60];
        for (int col = 0; col < 5; col++)
            for (int row = 0; row < 2; row++)
            {
                int q = col * 2 + row, b = q * 4;
                Vector3 lc = panel + Nn * ((col - 2) * 0.95f) + up * (row == 0 ? 0.3f : -0.3f) - T * 0.19f;
                const float h = 0.17f;
                v[b] = lc - right * h - up * h; v[b + 1] = lc + right * h - up * h; v[b + 2] = lc + right * h + up * h; v[b + 3] = lc - right * h + up * h;
                for (int k = 0; k < 4; k++) v[b + k].y += WorldBuilder.H(v[b + k].x, v[b + k].z);
                tri[q * 6] = b; tri[q * 6 + 1] = b + 2; tri[q * 6 + 2] = b + 1; tri[q * 6 + 3] = b; tri[q * 6 + 4] = b + 3; tri[q * 6 + 5] = b + 2;
            }
        lightsMesh = new Mesh { name = "StartLights", vertices = v, triangles = tri, colors32 = new Color32[40] };
        lightsMesh.RecalculateBounds();
        lightsGo = new GameObject("StartLights");
        if (parent != null) lightsGo.transform.SetParent(parent, false);
        lightsGo.AddComponent<MeshFilter>().sharedMesh = lightsMesh;
        var mr = lightsGo.AddComponent<MeshRenderer>();
        var sh = Shader.Find("Sprites/Default"); // always in builds; vertex colour, no lighting, both faces
        mr.sharedMaterial = new Material(sh);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        lightsGo.SetActive(false);
        lightsFor = t; lightsLit = -1;
    }
}
