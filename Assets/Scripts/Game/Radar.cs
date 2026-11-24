// Radar.cs — the proximity radar (Gran Turismo style), the default of the "Map" setting, top right under the race card
// where the local track map was (RaceHud.cs: HudLayout's cardRect; the track map itself is GameManager.cs: DrawMiniMap).
//  * a round, translucent panel (one cached texture: dark glass, a hairline rim, faint centre lines); the player's car
//    in the middle (a little below the centre: ~38 m ahead and ~26 m behind are in view), nose up, turned with the car
//  * every rival within range as a small rounded car shape at its real place and angle relative to the player (the same
//    in-between moment the cars are drawn at: GameManager.Update), white; amber as it closes up alongside, red once it
//    overlaps the player's car (within a car length, side by side). Cars fade out before they reach the rim
//  * the road edges round the car (the track map's 4 m points, GameManager.cs BuildMiniMap) and the pit lane, faintly
//  * side-warning arcs on the rim, left / right, while a car is alongside (amber -> red as it overlaps)
// Drawn in OnGUI (Repaint only) with immediate GL triangles (RaceHud.cs: GlBegin / GlEnd); nothing is allocated per
// frame: no strings, no arrays, no objects. Setting "Map": Radar / Track map / Off, PlayerPrefs "hudMapMode" (MapMode).
using UnityEngine;

public partial class GameManager
{
    // ---------------------------------------------------------------- the "Map" setting
    // 0 Radar (default), 1 Track map (the old local mini-map), 2 Off. Without a saved "hudMapMode" the old "hudMap"
    // toggle decides (a map switched off stays off).
    internal static readonly string[] MapModeNames = { "Radar", "Track map", "Off" };
    const string MapModeKey = "hudMapMode";
    int mapModeCache = -1;
    internal int MapMode
    {
        get
        {
            if (mapModeCache < 0) mapModeCache = PlayerPrefs.HasKey(MapModeKey) ? Mathf.Clamp(PlayerPrefs.GetInt(MapModeKey, 0), 0, 2) : PlayerPrefs.GetInt("hudMap", 1) == 1 ? 0 : 2;
            return mapModeCache;
        }
        set { mapModeCache = Mathf.Clamp(value, 0, 2); PlayerPrefs.SetInt(MapModeKey, mapModeCache); }
    }

    // OnGUI (GameManager.cs), where the track map was drawn
    void DrawMapHud()
    {
        int m = MapMode;
        if (m == 0) DrawRadar();
        else if (m == 1) DrawMiniMap();
    }

    // ---------------------------------------------------------------- radar
    const float RdRange = 32f;     // m from the panel's centre to its rim
    const float RdLead = 0.2f;     // the player sits this share of the radius below the centre (38 m ahead, 26 m behind)
    const float RdCarLen = 5.6f;   // m, a car length (alongside = less than this apart along the car)
    static Texture2D radarBg;
    float rdCx, rdCy, rdRim, rdFade, rdGuiA;
    static readonly float[] rdX = new float[8], rdY = new float[8];
    static readonly Color RdWhite = new Color(0.94f, 0.95f, 0.97f, 0.95f), RdEdge = new Color(0.86f, 0.89f, 0.94f, 0.55f), RdPit = new Color(0.86f, 0.89f, 0.94f, 0.22f);
    static readonly Color RdOutline = new Color(0.02f, 0.025f, 0.035f, 0.8f), RdMe = new Color(0f, 0.82f, 1f, 1f), RdMeEdge = new Color(1f, 1f, 1f, 0.95f);

    // the panel: square, right-aligned under the race card, ends above the gas pedal (the old map's place)
    Rect RadarRect()
    {
        float top = cardRect.yMax + S(6);
        float d = Mathf.Min(S(92), GasRect.y - S(6) - top);
        return new Rect(Mathf.Round(hudR - d), Mathf.Round(top), Mathf.Round(d), Mathf.Round(d));
    }

    void DrawRadar()
    {
        if (playground || track == null || car == null) return;
        if (Event.current.type != EventType.Repaint) return;
        if (mmFor != track || mmX == null) BuildMiniMap();
        if (mmLane != Trackside.Lane) BuildMmPit();
        var r = RadarRect();
        if (r.width < S(40)) return;
        RadarInit();
        GUI.DrawTexture(r, radarBg);

        float R = r.width * 0.5f;
        rdCx = r.center.x; rdCy = r.center.y; rdRim = R - S(1.5f); rdFade = rdRim * 0.24f; rdGuiA = GUI.color.a;
        float k = rdRim / RdRange, ox = rdCx, oy = rdCy + RdLead * rdRim; // px per metre; the player's spot

        // the player's car where it is drawn this frame (GameManager.Update: between the last two physics steps)
        double al = acc / H, dh = System.Math.Atan2(System.Math.Sin(car.heading - ph), System.Math.Cos(car.heading - ph));
        float mx = (float)(px + (car.x - px) * al), my = (float)(py + (car.y - py) * al), mh = (float)(ph + dh * al);
        float fx = Mathf.Cos(mh), fy = Mathf.Sin(mh);
        // world offset (dx, dy) -> panel: right of the car = +X, ahead = -Y
        //   X = ox + (dx fy - dy fx) k,  Y = oy - (dx fx + dy fy) k
        float lim = RdRange * (1f + RdLead) + 14f, lim2 = lim * lim;
        if (!GlBegin(r)) return;

        // road edges and the pit lane
        float w = Mathf.Max(1f, S(0.9f));
        for (int i = 0; i < mmN; i++)
        {
            int j = i + 1 == mmN ? 0 : i + 1;
            float ix = mmX[i] - mx, iy = mmY[i] - my; if (ix * ix + iy * iy > lim2) continue;
            float jx = mmX[j] - mx, jy = mmY[j] - my; if (jx * jx + jy * jy > lim2) continue;
            for (int s = -1; s <= 1; s += 2)
            {
                float hi = mmHw[i] * s, hj = mmHw[j] * s;
                float ax = ix + mmNx[i] * hi, ay = iy + mmNy[i] * hi, bx = jx + mmNx[j] * hj, by = jy + mmNy[j] * hj;
                RdSeg(ox + (ax * fy - ay * fx) * k, oy - (ax * fx + ay * fy) * k, ox + (bx * fy - by * fx) * k, oy - (bx * fx + by * fy) * k, w, RdEdge);
            }
        }
        for (int i = 0; i + 1 < mmPitN; i++)
        {
            float ax = mmPitX[i] - mx, ay = mmPitY[i] - my; if (ax * ax + ay * ay > lim2) continue;
            float bx = mmPitX[i + 1] - mx, by = mmPitY[i + 1] - my; if (bx * bx + by * by > lim2) continue;
            RdSeg(ox + (ax * fy - ay * fx) * k, oy - (ax * fx + ay * fy) * k, ox + (bx * fy - by * fx) * k, oy - (bx * fx + by * fy) * k, w, RdPit);
        }

        // rivals: at the same in-between moment as their models (GameManager.Update), a little larger than to scale
        float hl = Mathf.Max(S(4.4f), 2.8f * k * 1.2f), hw = Mathf.Max(S(1.6f), 0.95f * k * 1.3f);
        float leftHeat = 0f, rightHeat = 0f;
        for (int i = 0; i < rivals.Count; i++)
        {
            var d = rivals[i];
            if (d.InPit && pRun == null) continue; // in the pit lane while the player is out on track: not beside him
            var c = d.car;
            double back = (1 - al) * H - RivalBehind(i), cch = System.Math.Cos(c.heading), csh = System.Math.Sin(c.heading);
            float dx = (float)(c.x - (c.vx * cch - c.vy * csh) * back) - mx, dy = (float)(c.y - (c.vx * csh + c.vy * cch) * back) - my;
            if (dx * dx + dy * dy > lim2) continue;
            float side = dx * fy - dy * fx, along = dx * fx + dy * fy;
            // alongside: 1 while the cars overlap (less than a car length apart along the car), down to 0 four metres further
            float lat = Mathf.Abs(side), lon = Mathf.Abs(along);
            float heat = lat < 5.5f ? 1f - Mathf.Clamp01((lon - RdCarLen) / 4f) : 0f;
            if (heat > 0f && lat < 4.5f) { if (side > 0f) rightHeat = Mathf.Max(rightHeat, heat); else leftHeat = Mathf.Max(leftHeat, heat); }
            float sx = ox + side * k, sy = oy - along * k;
            float ex = sx - rdCx, ey = sy - rdCy;
            float a = Mathf.Clamp01((rdRim - (Mathf.Sqrt(ex * ex + ey * ey) + hl)) / rdFade); // gone before it reaches the rim
            if (a <= 0.01f) continue;
            float rel = (float)(c.heading - c.r * back) - mh; // its heading against the player's
            float ux = -Mathf.Sin(rel), uy = -Mathf.Cos(rel);
            Color col = heat >= 0.999f ? HudRed : Color.Lerp(RdWhite, HudAmber, heat);
            col.a = RdWhite.a;
            RdCar(sx, sy, ux, uy, hl + S(0.7f), hw + S(0.7f), RdOutline, a);
            RdCar(sx, sy, ux, uy, hl, hw, col, a);
        }

        // the player: cyan, white outline, nose up
        RdCar(ox, oy, 0f, -1f, hl + S(0.8f), hw + S(0.8f), RdMeEdge, 1f);
        RdCar(ox, oy, 0f, -1f, hl, hw, RdMe, 1f);

        // side warnings on the rim (centred level with the player)
        float lvl = Mathf.Asin(RdLead);
        if (rightHeat > 0.01f) RdArc(lvl, rightHeat);
        if (leftHeat > 0.01f) RdArc(Mathf.PI - lvl, leftHeat);
        GlEnd();
    }

    // alpha: 1 inside, down to 0 at the rim
    float RdFadeAt(float x, float y)
    {
        float dx = x - rdCx, dy = y - rdCy;
        return Mathf.Clamp01((rdRim - Mathf.Sqrt(dx * dx + dy * dy)) / rdFade);
    }
    // a line w pixels wide, cut at the rim, fading towards it (one colour per end)
    void RdSeg(float ax, float ay, float bx, float by, float w, Color c)
    {
        float rr = rdRim * rdRim;
        float ux = ax - rdCx, uy = ay - rdCy, vx = bx - rdCx, vy = by - rdCy;
        bool ia = ux * ux + uy * uy < rr, ib = vx * vx + vy * vy < rr;
        if (!ia && !ib) return;
        if (ia != ib)
        {
            if (!ia) // make a the end inside
            {
                float t0 = ax; ax = bx; bx = t0; t0 = ay; ay = by; by = t0;
                t0 = ux; ux = vx; vx = t0; t0 = uy; uy = vy; vy = t0;
            }
            float ddx = vx - ux, ddy = vy - uy, qa = ddx * ddx + ddy * ddy, qb = 2f * (ux * ddx + uy * ddy), qc = ux * ux + uy * uy - rr;
            if (qa < 1e-6f) return;
            float t = (-qb + Mathf.Sqrt(Mathf.Max(0f, qb * qb - 4f * qa * qc))) / (2f * qa);
            bx = ax + (bx - ax) * t; by = ay + (by - ay) * t;
        }
        float dx = bx - ax, dy = by - ay, l = Mathf.Sqrt(dx * dx + dy * dy);
        if (l < 1e-3f) return;
        float nx = -dy / l * w * 0.5f, ny = dx / l * w * 0.5f;
        Color ca = c, cb = c; ca.a *= RdFadeAt(ax, ay) * rdGuiA; cb.a *= RdFadeAt(bx, by) * rdGuiA;
        if (ca.a <= 0.002f && cb.a <= 0.002f) return;
        GL.Color(ca); GL.Vertex3(ax + nx, ay + ny, 0f); GL.Color(cb); GL.Vertex3(bx + nx, by + ny, 0f); GL.Vertex3(bx - nx, by - ny, 0f);
        GL.Color(ca); GL.Vertex3(ax + nx, ay + ny, 0f); GL.Color(cb); GL.Vertex3(bx - nx, by - ny, 0f); GL.Color(ca); GL.Vertex3(ax - nx, ay - ny, 0f);
    }
    // a car: a rounded shape, narrower at the nose. (x, y) its centre, (ux, uy) the unit direction its nose points on
    // screen, hl / hw half length / width in pixels
    void RdCar(float x, float y, float ux, float uy, float hl, float hw, Color c, float a)
    {
        float vx = -uy, vy = ux; // to the car's right on screen
        float cf = hl * 0.38f, cr = hw * 0.45f, nose = hw * 0.48f;
        RdPt(0, x, y, ux, uy, vx, vy, hl, -nose); RdPt(1, x, y, ux, uy, vx, vy, hl, nose);
        RdPt(2, x, y, ux, uy, vx, vy, hl - cf, hw); RdPt(3, x, y, ux, uy, vx, vy, -hl + cr, hw);
        RdPt(4, x, y, ux, uy, vx, vy, -hl, hw - cr); RdPt(5, x, y, ux, uy, vx, vy, -hl, -hw + cr);
        RdPt(6, x, y, ux, uy, vx, vy, -hl + cr, -hw); RdPt(7, x, y, ux, uy, vx, vy, hl - cf, -hw);
        c.a *= a * rdGuiA;
        GL.Color(c);
        for (int i = 1; i + 1 < 8; i++) { GL.Vertex3(rdX[0], rdY[0], 0f); GL.Vertex3(rdX[i], rdY[i], 0f); GL.Vertex3(rdX[i + 1], rdY[i + 1], 0f); }
    }
    static void RdPt(int i, float x, float y, float ux, float uy, float vx, float vy, float along, float side)
    {
        rdX[i] = x + ux * along + vx * side; rdY[i] = y + uy * along + vy * side;
    }
    // a warning arc on the rim centred at angle ang (radians, screen: 0 = right, y down), soft ends and a glow inwards
    void RdArc(float ang, float heat)
    {
        Color c = Color.Lerp(HudAmber, HudRed, heat);
        float aMain = (0.4f + 0.55f * heat) * rdGuiA, aGlow = 0.28f * heat * rdGuiA;
        float r1 = rdRim + S(0.6f), r0 = rdRim - S(2.6f), rg = r0 - S(5f);
        const int K = 14; const float Span = 0.62f; // +-35 deg
        for (int s = 0; s < K; s++)
        {
            float t0 = (float)s / K, t1 = (float)(s + 1) / K;
            float a0 = ang - Span + 2f * Span * t0, a1 = ang - Span + 2f * Span * t1;
            float w0 = Mathf.Sin(Mathf.PI * t0), w1 = Mathf.Sin(Mathf.PI * t1);
            float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0), c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
            Color m0 = c, m1 = c, g0 = c, g1 = c, z = c; m0.a = aMain * w0; m1.a = aMain * w1; g0.a = aGlow * w0; g1.a = aGlow * w1; z.a = 0f;
            // the band
            GL.Color(m0); GL.Vertex3(rdCx + c0 * r0, rdCy + s0 * r0, 0f); GL.Vertex3(rdCx + c0 * r1, rdCy + s0 * r1, 0f); GL.Color(m1); GL.Vertex3(rdCx + c1 * r1, rdCy + s1 * r1, 0f);
            GL.Color(m0); GL.Vertex3(rdCx + c0 * r0, rdCy + s0 * r0, 0f); GL.Color(m1); GL.Vertex3(rdCx + c1 * r1, rdCy + s1 * r1, 0f); GL.Vertex3(rdCx + c1 * r0, rdCy + s1 * r0, 0f);
            // the glow, fading inwards
            GL.Color(z); GL.Vertex3(rdCx + c0 * rg, rdCy + s0 * rg, 0f); GL.Color(g0); GL.Vertex3(rdCx + c0 * r0, rdCy + s0 * r0, 0f); GL.Color(g1); GL.Vertex3(rdCx + c1 * r0, rdCy + s1 * r0, 0f);
            GL.Color(z); GL.Vertex3(rdCx + c0 * rg, rdCy + s0 * rg, 0f); GL.Color(g1); GL.Vertex3(rdCx + c1 * r0, rdCy + s1 * r0, 0f); GL.Color(z); GL.Vertex3(rdCx + c1 * rg, rdCy + s1 * rg, 0f);
        }
    }

    // the panel, drawn once into a texture: dark glass (a little darker towards the rim), a hairline rim, a faint ring at
    // half range and faint lines through the player's place (straight ahead, and level with the car)
    static void RadarInit()
    {
        if (radarBg != null) return;
        const int D = 256; const float Px = D * 0.5f; // pixels per unit radius
        radarBg = new Texture2D(D, D, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[D * D];
        float lvl = -RdLead * 0.97f; // the player's level (v up), the rim being ~0.97 of the panel's radius
        for (int y = 0; y < D; y++) for (int x = 0; x < D; x++)
        {
            float u = (x + 0.5f) / Px - 1f, v = (y + 0.5f) / Px - 1f, r = Mathf.Sqrt(u * u + v * v);
            float inside = Mathf.Clamp01((1f - r) * Px + 0.5f);
            if (inside <= 0f) { px[y * D + x] = new Color32(0, 0, 0, 0); continue; }
            float baseA = 0.36f + 0.2f * r * r;
            float inner = Mathf.Clamp01((0.95f - r) * 5f);
            float line = 0f;
            line = Mathf.Max(line, Mathf.Clamp01(1f - Mathf.Abs(u) * Px / 0.9f) * 0.10f * inner);          // straight ahead / behind
            line = Mathf.Max(line, Mathf.Clamp01(1f - Mathf.Abs(v - lvl) * Px / 0.9f) * 0.08f * inner);    // level with the car
            line = Mathf.Max(line, Mathf.Clamp01(1f - Mathf.Abs(r - 0.5f) * Px / 0.9f) * 0.07f);           // half range
            line = Mathf.Max(line, Mathf.Clamp01(1f - Mathf.Abs(r - 0.982f) * Px / 1.1f) * 0.30f);         // rim
            float a = baseA + line * (1f - baseA), wht = line / Mathf.Max(a, 1e-4f);
            float g = Mathf.Lerp(0.025f, 1f, wht);
            px[y * D + x] = new Color32((byte)(255 * Mathf.Lerp(0.02f, 1f, wht)), (byte)(255 * g), (byte)(255 * Mathf.Lerp(0.035f, 1f, wht)), (byte)(255 * Mathf.Clamp01(a * inside)));
        }
        radarBg.SetPixels32(px); radarBg.Apply(false);
    }
}
