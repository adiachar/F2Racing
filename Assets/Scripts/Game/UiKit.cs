// UiKit.cs — the shared look of every screen (menus, settings, championship, race HUD), drawn with IMGUI.
// One design system so all screens match: Titillium Web (OFL, Assets/Resources/Fonts), dark glass panels with soft
// shadows and rounded corners (IMGUI's own rounded DrawTexture, no extra textures), one red accent, and short eased
// animations. Layout unit U(1) = 1/390 of the screen height (the same unit the old screens used).
//
// Use:  UiKit.Panel(r);  UiKit.Text(r, "LAP 3/10", UiKit.Bold, 14, UiKit.TextCol);  if (UiKit.Button(r, "RACE", true)) ...
// Fonts can be null on a platform where the .ttf failed to import; every helper then falls back to the default font.
//
// Type scale (layout units): display 40-64 Black · title 22-30 Black · heading 14-16 Bold · body 12-13 Semi ·
// eyebrow 9-10 Bold caps (tracked) · micro 8-9 Regular. Radii: cards 14, controls 12, chips 9, pills = full.
// Everything here is allocation-free per frame once warmed up: styles, gradients, caps strings and the few
// textures (glow, vignette) are cached. Fade groups with PushAlpha / PopAlpha (every helper multiplies GUI.color).
using System.Collections.Generic;
using UnityEngine;

public static class UiKit
{
    // ---------- units and safe area ----------
    public static float U(float v) => v * Screen.height / 390f;

    // the screen area not covered by notches / rounded corners, in GUI coordinates (y down)
    public static Rect Safe
    {
        get
        {
            var s = Screen.safeArea;
            return new Rect(s.x, Screen.height - s.yMax, s.width, s.height);
        }
    }

    // ---------- palette ----------
    // Colour roles (one meaning each; contrast of text on Base: TextCol ~15:1, Sub ~7:1, Faint ~4.6:1):
    //  * neutrals: Base (cool charcoal page), Surface / SurfaceHi (cards, raised), Line (hairlines), TextCol (off-white),
    //    Sub (one quiet grey for secondary text), Faint (captions only)
    //  * Accent (racing red #E10600): ONLY the primary action (RACE / START / NEXT) and the selected state. Never decoration.
    //  * Info (cyan): progress, XP, live data. Good (green): "good" only (safety rating, personal best). Warn (amber):
    //    warnings only. Purple / Gold / Silver / Bronze: timing conventions (fastest lap, podium) on timing screens.
    //  At most one of these accents on a screen besides red.
    public static readonly Color Base = new Color(0.071f, 0.078f, 0.094f, 1f);           // #121418 page background (cool charcoal)
    public static readonly Color Ink = Base;                                               // (older name)
    public static readonly Color Surface = new Color(0.098f, 0.106f, 0.125f, 0.92f);      // card
    public static readonly Color SurfaceHi = new Color(0.135f, 0.145f, 0.168f, 0.95f);    // raised / selected card
    public static readonly Color PanelCol = new Color(0.086f, 0.094f, 0.112f, 0.90f);    // glass panel
    public static readonly Color PanelHi = SurfaceHi;                                      // raised / selected panel
    public static readonly Color Line = new Color(1f, 1f, 1f, 0.08f);                    // hairlines and outlines
    public static readonly Color TextCol = new Color(0.94f, 0.945f, 0.955f, 1f);         // off-white
    public static readonly Color Sub = new Color(0.64f, 0.67f, 0.71f, 1f);               // secondary text (quiet grey)
    public static readonly Color Faint = new Color(0.50f, 0.53f, 0.57f, 1f);             // captions (still >= 4.5:1 on Base)
    public static readonly Color Accent = new Color(0.882f, 0.024f, 0f, 1f);             // #E10600 racing red: action / selected only
    public static readonly Color AccentHi = new Color(1f, 0.20f, 0.12f, 1f);
    public static readonly Color Info = new Color(0f, 0.78f, 0.95f, 1f);                 // cyan: progress, XP, live data
    public static readonly Color Good = new Color(0.20f, 0.78f, 0.42f, 1f);              // green: good (safety rating, personal best)
    public static readonly Color Warn = new Color(0.98f, 0.74f, 0.16f, 1f);              // amber: warnings
    public static readonly Color Purple = new Color(0.66f, 0.33f, 0.97f, 1f);            // overall best
    public static readonly Color Gold = new Color(1f, 0.80f, 0.32f, 1f);                 // podium P1
    public static readonly Color Silver = new Color(0.80f, 0.83f, 0.88f, 1f);            // podium P2
    public static readonly Color Bronze = new Color(0.86f, 0.55f, 0.33f, 1f);            // podium P3
    static readonly Color Glass = new Color(1f, 1f, 1f, 0.055f);                         // control body on a panel
    static readonly Color GlassHi = new Color(1f, 1f, 1f, 0.11f);

    public static Color A(Color c, float a) { c.a *= a; return c; }

    // ---------- fonts ----------
    static bool fontsLoaded;
    static Font regular, semi, bold, black, boldItalic;
    static void LoadFonts()
    {
        if (fontsLoaded) return; fontsLoaded = true;
        regular = Resources.Load<Font>("Fonts/TitilliumWeb-Regular");
        semi = Resources.Load<Font>("Fonts/TitilliumWeb-SemiBold");
        bold = Resources.Load<Font>("Fonts/TitilliumWeb-Bold");
        black = Resources.Load<Font>("Fonts/TitilliumWeb-Black");
        boldItalic = Resources.Load<Font>("Fonts/TitilliumWeb-BoldItalic");
    }
    public static Font Regular { get { LoadFonts(); return regular; } }
    public static Font Semi { get { LoadFonts(); return semi ?? regular; } }
    public static Font Bold { get { LoadFonts(); return bold ?? semi ?? regular; } }
    public static Font Black { get { LoadFonts(); return black ?? bold ?? regular; } }
    public static Font BoldItalic { get { LoadFonts(); return boldItalic ?? bold ?? regular; } }

    // ---------- text ----------
    static readonly Dictionary<long, GUIStyle> styles = new Dictionary<long, GUIStyle>();
    // a cached label style; size is in layout units
    public static GUIStyle Style(Font f, float size, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false)
    {
        int px = Mathf.Max(6, Mathf.RoundToInt(U(size)));
        long key = ((long)(f != null ? f.GetInstanceID() : 0) << 24) ^ ((long)px << 8) ^ ((long)anchor << 1) ^ (wrap ? 1 : 0);
        if (!styles.TryGetValue(key, out var st))
        {
            st = new GUIStyle(GUI.skin.label) { font = f, fontSize = px, alignment = anchor, wordWrap = wrap, clipping = TextClipping.Overflow, richText = false };
            st.padding = new RectOffset(0, 0, 0, 0); st.margin = new RectOffset(0, 0, 0, 0);
            st.normal.textColor = Color.white;
            styles[key] = st;
        }
        return st;
    }

    public static void Text(Rect r, string s, Font f, float size, Color c, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false)
    {
        if (string.IsNullOrEmpty(s) || c.a <= 0.002f) return;
        var st = Style(f, size, anchor, wrap);
        var old = GUI.color; GUI.color = c * old; GUI.Label(r, s, st); GUI.color = old;
    }

    // text with a soft dark drop behind it, for text drawn straight over the 3D view
    public static void TextShadow(Rect r, string s, Font f, float size, Color c, TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        float o = Mathf.Max(1f, U(0.8f));
        Text(new Rect(r.x + o, r.y + o, r.width, r.height), s, f, size, new Color(0, 0, 0, 0.55f * c.a), anchor);
        Text(r, s, f, size, c, anchor);
    }

    // wide-tracked capitals (IMGUI has no letter spacing, so pad with spaces); cached, so no garbage per frame
    static readonly Dictionary<string, string> capsCache = new Dictionary<string, string>();
    public static string Caps(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (capsCache.TryGetValue(s, out var c)) return c;
        var up = s.ToUpperInvariant(); var sb = new System.Text.StringBuilder(up.Length * 2);
        for (int i = 0; i < up.Length; i++) { sb.Append(up[i]); if (i < up.Length - 1 && up[i] != ' ') sb.Append(' '); }
        c = sb.ToString();
        if (capsCache.Count > 600) capsCache.Clear();
        capsCache[s] = c; return c;
    }
    // plain capitals, cached
    static readonly Dictionary<string, string> upperCache = new Dictionary<string, string>();
    public static string Upper(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (upperCache.TryGetValue(s, out var c)) return c;
        c = s.ToUpperInvariant();
        if (upperCache.Count > 600) upperCache.Clear();
        upperCache[s] = c; return c;
    }

    static readonly GUIContent gc = new GUIContent();
    public static float Width(string s, Font f, float size) { gc.text = s; return Style(f, size).CalcSize(gc).x; }
    // the largest size <= size (down to minSize) at which s fits in maxW
    public static float Fit(string s, Font f, float size, float maxW, float minSize)
    {
        float w = Width(s, f, size);
        if (w <= maxW || w <= 0) return size;
        return Mathf.Max(minSize, size * maxW / w * 0.98f);
    }
    // small tracked caps label (section headers, data captions)
    public static void Eyebrow(Rect r, string s, Color c, float size = 9.5f, TextAnchor anchor = TextAnchor.MiddleLeft) => Text(r, Caps(s), Bold, size, c, anchor);

    // ---------- shapes ----------
    public static void Rect(Rect r, Color c, float radius = 0f)
    {
        if (c.a <= 0.001f || r.width <= 0 || r.height <= 0) return;
        float rad = Mathf.Min(U(radius), Mathf.Min(r.width, r.height) * 0.5f);
        GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c * GUI.color, 0f, rad);
    }

    public static void Outline(Rect r, Color c, float radius = 0f, float width = 1f)
    {
        if (c.a <= 0.001f) return;
        float rad = Mathf.Min(U(radius), Mathf.Min(r.width, r.height) * 0.5f);
        GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c * GUI.color, Mathf.Max(1f, U(width)), rad);
    }

    // a filled circle (centre, radius in layout units)
    public static void Dot(Vector2 c, float radius, Color col) { float r = U(radius); Rect(new Rect(c.x - r, c.y - r, 2 * r, 2 * r), col, 999f); }

    // soft shadow: a few widening, fading rounded rects
    public static void Shadow(Rect r, float radius = 12f, float spread = 8f, float alpha = 0.35f, float dropY = 3f)
    {
        const int N = 4;
        for (int i = N; i >= 1; i--)
        {
            float k = i / (float)N, g = U(spread) * k;
            var rr = new Rect(r.x - g, r.y - g + U(dropY), r.width + 2 * g, r.height + 2 * g);
            Rect(rr, new Color(0, 0, 0, alpha * (1f - k) * 0.6f + alpha * 0.08f), radius + spread * k);
        }
    }

    static readonly Dictionary<long, Texture2D> gradients = new Dictionary<long, Texture2D>();
    static long Key(Color a) => ((long)(a.r * 255) << 24) | ((long)(a.g * 255) << 16) | ((long)(a.b * 255) << 8) | (long)(a.a * 255);
    // a vertical (or horizontal: top = left) two-colour gradient, rounded. Pass FIXED colours (fade with GUI.color /
    // PushAlpha): every distinct pair makes one small cached texture.
    public static void Gradient(Rect r, Color top, Color bottom, float radius = 0f, bool horizontal = false)
    {
        if (r.width <= 0 || r.height <= 0) return;
        long key = (Key(top) * 1000003L) ^ Key(bottom) ^ (horizontal ? 1L << 62 : 0);
        if (!gradients.TryGetValue(key, out var t) || t == null)
        {
            if (gradients.Count > 160) { foreach (var g in gradients.Values) if (g != null) Object.Destroy(g); gradients.Clear(); } // a caller animating colours: stay bounded
            const int N = 32;
            t = horizontal ? new Texture2D(N, 1, TextureFormat.RGBA32, false) : new Texture2D(1, N, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; t.hideFlags = HideFlags.HideAndDontSave;
            for (int i = 0; i < N; i++)
            {
                var c = Color.Lerp(bottom, top, i / (float)(N - 1)); // texture row 0 is the bottom
                if (horizontal) t.SetPixel(N - 1 - i, 0, c); else t.SetPixel(0, i, c);
            }
            t.Apply(false, true); gradients[key] = t;
        }
        float rad = Mathf.Min(U(radius), Mathf.Min(r.width, r.height) * 0.5f);
        GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true, 0f, GUI.color, 0f, rad);
    }

    static readonly Color panelTop = new Color(0.112f, 0.120f, 0.140f, 0.92f);
    // the standard glass card: shadow, body, a faint top highlight and a hairline edge
    public static void Panel(Rect r, float radius = 12f, float alpha = 1f, bool shadow = true)
    {
        if (shadow) Shadow(r, radius, 10f, 0.32f * alpha);
        PushAlpha(alpha);
        Gradient(r, panelTop, PanelCol, radius);
        Rect(new Rect(r.x + U(radius) * 0.6f, r.y + U(0.6f), r.width - U(radius) * 1.2f, Mathf.Max(1f, U(0.7f))), new Color(1, 1, 1, 0.07f)); // top light
        Outline(r, Line, radius, 0.7f);
        PopAlpha();
    }

    // a thin accent bar (left edge of a selected row, under a title...)
    public static void Bar(Rect r, Color c) => Rect(r, c, Mathf.Min(r.width, r.height) * 0.5f / Mathf.Max(0.0001f, U(1)));

    // ---------- glow, vignette and the page backdrop ----------
    static Texture2D radialTex, vignetteTex;
    static Texture2D MakeRadial(int n, bool vignette)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f, d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = vignette ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1.42f, d)) : Mathf.Pow(Mathf.Clamp01(1f - d), 2.2f);
                px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
        t.SetPixels32(px); t.Apply(false, true);
        return t;
    }
    public static Texture2D RadialTex { get { if (radialTex == null) radialTex = MakeRadial(64, false); return radialTex; } }
    static Texture2D VignetteTex { get { if (vignetteTex == null) vignetteTex = MakeRadial(64, true); return vignetteTex; } }

    // a soft round light (white texture tinted c), e.g. behind a title or under the primary button
    public static void Glow(Rect r, Color c)
    {
        if (c.a <= 0.001f) return;
        var o = GUI.color; GUI.color = c * o; GUI.DrawTexture(r, RadialTex, ScaleMode.StretchToFill, true); GUI.color = o;
    }

    static readonly Color bgTop = new Color(0.082f, 0.090f, 0.108f, 0.92f), bgBottom = new Color(0.052f, 0.057f, 0.070f, 0.98f);
    static readonly Color leftShade = new Color(0f, 0f, 0f, 0.38f), clear = new Color(0f, 0f, 0f, 0f);
    // the dark page behind a menu: gradient, side shade, a red and a blue light, angled "speed" slashes, vignette.
    // opacity fades all of it (the 3D scene shows through a little at 1).
    public static void Backdrop(float opacity = 1f, bool slashes = true)
    {
        float sw = Screen.width, sh = Screen.height;
        var full = new Rect(0, 0, sw, sh);
        PushAlpha(opacity);
        Gradient(full, bgTop, bgBottom);
        Gradient(new Rect(0, 0, sw * 0.62f, sh), leftShade, clear, 0f, true);
        Glow(new Rect(-sh * 0.75f, -sh * 0.85f, sh * 1.9f, sh * 1.6f), new Color(Accent.r, Accent.g, Accent.b, 0.20f));
        Glow(new Rect(sw - sh * 0.9f, sh * 0.30f, sh * 1.5f, sh * 1.3f), new Color(0.10f, 0.35f, 0.65f, 0.10f));
        if (slashes) Slashes();
        var o = GUI.color; GUI.color = new Color(0, 0, 0, 0.62f) * o; GUI.DrawTexture(full, VignetteTex, ScaleMode.StretchToFill, true); GUI.color = o;
        PopAlpha();
    }

    // three long angled bars across the right of the screen: a quiet F1-graphics motif (a handful of draws)
    static void Slashes()
    {
        float sw = Screen.width, sh = Screen.height;
        var m = GUI.matrix;
        var pivot = new Vector2(sw * 0.78f, sh * 0.5f);
        GUIUtility.RotateAroundPivot(28f, pivot);
        float len = sh * 2.4f, y0 = pivot.y - len * 0.5f;
        Rect(new Rect(pivot.x - U(70), y0, U(46), len), new Color(1, 1, 1, 0.022f));
        Rect(new Rect(pivot.x - U(14), y0, U(14), len), new Color(1, 1, 1, 0.030f));
        Rect(new Rect(pivot.x + U(10), y0, U(3), len), new Color(Accent.r, Accent.g, Accent.b, 0.30f));
        Rect(new Rect(pivot.x + U(120), y0, U(90), len), new Color(1, 1, 1, 0.014f));
        GUI.matrix = m;
    }

    // ---------- alpha groups and entry motion ----------
    static readonly Stack<Color> colStack = new Stack<Color>();
    public static void PushAlpha(float a) { var c = GUI.color; colStack.Push(c); c.a *= Mathf.Clamp01(a); GUI.color = c; }
    public static void PopAlpha() { if (colStack.Count > 0) GUI.color = colStack.Pop(); }
    // r moved down by dy layout units while t < 1 (pair with PushAlpha(t) for a fade-up)
    public static Rect Rise(Rect r, float t, float dy = 10f) { r.y += U(dy) * (1f - t); return r; }
    // r moved sideways while t < 1
    public static Rect Slide(Rect r, float t, float dx = 14f) { r.x += U(dx) * (1f - t); return r; }

    // ---------- animation ----------
    public static float EaseOut(float t) { t = Mathf.Clamp01(t); float u = 1f - t; return 1f - u * u * u; }
    public static float EaseInOut(float t) { t = Mathf.Clamp01(t); return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f; }
    // a little overshoot at the end (pop-in of chips and badges)
    public static float EaseBack(float t) { t = Mathf.Clamp01(t); const float c1 = 1.70158f, c3 = c1 + 1f; float u = t - 1f; return 1f + c3 * u * u * u + c1 * u * u; }

    // a value that eases toward a target, kept per id (hover / press / selection highlights)
    static readonly Dictionary<int, float> anim = new Dictionary<int, float>();
    public static float Anim(int id, float target, float speed = 12f)
    {
        anim.TryGetValue(id, out float v);
        if (Event.current == null || Event.current.type == EventType.Repaint)
            v = Mathf.MoveTowards(v, target, Time.unscaledDeltaTime * speed * Mathf.Max(0.15f, Mathf.Abs(target - v)) * 2f + Time.unscaledDeltaTime * 0.5f);
        anim[id] = v; return v;
    }
    // like Anim, but starts AT the target the first time (sliding highlights that must not fly in from 0),
    // and moves with an exponential ease (frame-rate independent)
    static readonly Dictionary<int, float> smooth = new Dictionary<int, float>();
    public static float Smooth(int key, float target, float speed = 14f)
    {
        if (!smooth.TryGetValue(key, out float v)) v = target;
        else if (Event.current == null || Event.current.type == EventType.Repaint)
        {
            v = Mathf.Lerp(v, target, 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime));
            if (Mathf.Abs(v - target) < 0.0005f) v = target;
        }
        smooth[key] = v; return v;
    }
    // seconds since Mark(key) was last called (a value change, a tab switch...); large if never
    static readonly Dictionary<int, float> marks = new Dictionary<int, float>();
    public static void Mark(int key) => marks[key] = Time.unscaledTime;
    public static float Since(int key) => marks.TryGetValue(key, out float t) ? Time.unscaledTime - t : 999f;

    // time since a screen was opened, for entry animations: call Opened(key) every frame the screen shows
    static readonly Dictionary<string, float> opened = new Dictionary<string, float>();
    static readonly Dictionary<string, int> openedFrame = new Dictionary<string, int>();
    public static float Opened(string key)
    {
        int f = Time.frameCount;
        if (!openedFrame.TryGetValue(key, out int last) || f - last > 2) opened[key] = Time.unscaledTime;
        openedFrame[key] = f;
        return Time.unscaledTime - opened[key];
    }
    // entry animation for the n-th element on a screen: 0..1 over ~0.35 s, staggered
    public static float Enter(string screen, int n, float stagger = 0.04f, float dur = 0.35f) => EaseOut((Opened(screen) - n * stagger) / dur);

    // ---------- touch ----------
    // The press logic of every control here: a press inside r makes it the hot control, it fires on release inside
    // (so a thumb can slide off to cancel), press = 0..1 eased feedback.
    public static bool Press(Rect r, bool enabled, out float press)
    {
        int id = GUIUtility.GetControlID(FocusType.Passive, r);
        var e = Event.current; bool clicked = false;
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (enabled && e.button == 0 && r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; e.Use(); }
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == id) e.Use();
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == id)
                {
                    GUIUtility.hotControl = 0;
                    if (enabled && r.Contains(e.mousePosition)) clicked = true;
                    e.Use();
                }
                break;
        }
        bool down = GUIUtility.hotControl == id && r.Contains(e.mousePosition);
        press = Anim(id, down ? 1f : 0f, 22f);
        return clicked;
    }

    // ---------- buttons ----------
    public enum Look { Primary, Secondary, Ghost, Armed }

    // a touch-friendly button: fires on release inside, shows press feedback. primary = filled red.
    public static bool Button(Rect r, string label, bool primary = false, bool enabled = true, float size = 13f, float radius = 10f)
        => Button(r, label, primary ? Look.Primary : Look.Secondary, enabled, size, null, radius);

    static readonly Color priTop = new Color(1f, 0.16f, 0.09f, 1f), priBottom = new Color(0.78f, 0.02f, 0f, 1f);
    // the full button: Primary (red, the one main action), Secondary (glass), Ghost (outline only), Armed (a
    // confirm-to-do state, red outline). sub = an optional small second line (e.g. "5 laps · 10 rivals").
    public static bool Button(Rect r, string label, Look look, bool enabled = true, float size = 13f, string sub = null, float radius = 12f)
    {
        bool clicked = Press(r, enabled, out float p);
        var rr = Scale(r, 1f - 0.03f * p);
        PushAlpha(enabled ? 1f : 0.35f);
        Color tc = TextCol;
        switch (look)
        {
            case Look.Primary:
                Glow(new Rect(rr.x - rr.width * 0.15f, rr.y - rr.height * 0.4f, rr.width * 1.3f, rr.height * 2.0f), new Color(Accent.r, Accent.g, Accent.b, 0.30f * (1f - 0.5f * p)));
                Shadow(rr, radius, 6f, 0.30f, 2f);
                Gradient(rr, priTop, priBottom, radius);
                Rect(new Rect(rr.x + U(radius) * 0.7f, rr.y + U(0.8f), rr.width - U(radius) * 1.4f, Mathf.Max(1f, U(0.8f))), new Color(1, 1, 1, 0.30f)); // top light
                Rect(rr, new Color(0, 0, 0, 0.22f * p), radius);
                Outline(rr, new Color(1, 1, 1, 0.16f), radius, 0.7f);
                tc = Color.white;
                break;
            case Look.Secondary:
                Rect(rr, Color.Lerp(new Color(0.13f, 0.14f, 0.18f, 0.90f), new Color(0.20f, 0.21f, 0.26f, 0.95f), p), radius);
                Outline(rr, new Color(1, 1, 1, 0.13f + 0.12f * p), radius, 0.7f);
                break;
            case Look.Ghost:
                Rect(rr, new Color(1, 1, 1, 0.025f + 0.08f * p), radius);
                Outline(rr, new Color(1, 1, 1, 0.12f + 0.1f * p), radius, 0.7f);
                tc = Color.Lerp(Sub, TextCol, 0.5f + 0.5f * p);
                break;
            case Look.Armed:
                Rect(rr, new Color(Accent.r, Accent.g, Accent.b, 0.16f + 0.14f * p), radius);
                Outline(rr, Accent, radius, 1.2f);
                tc = Color.white;
                break;
        }
        Font f = look == Look.Primary ? Black : Bold;
        string up = Upper(label); float room = rr.width - U(14);
        float ls = Fit(up, f, size, room, size * 0.7f);
        if (sub == null) Text(rr, up, f, ls, tc, TextAnchor.MiddleCenter);
        else
        {
            float h = rr.height, ss = Mathf.Max(8.5f, size * 0.52f);
            Text(new Rect(rr.x, rr.y + h * 0.10f, rr.width, h * 0.52f), up, f, ls, tc, TextAnchor.MiddleCenter);
            Text(new Rect(rr.x, rr.y + h * 0.56f, rr.width, h * 0.30f), sub, Semi, Fit(sub, Semi, ss, room, 7.5f), A(tc, 0.78f), TextAnchor.MiddleCenter);
        }
        PopAlpha();
        return clicked;
    }

    public static Rect Scale(Rect r, float k)
    {
        float w = r.width * k, h = r.height * k;
        return new Rect(r.center.x - w * 0.5f, r.center.y - h * 0.5f, w, h);
    }

    // ---------- icons (drawn from rects, no textures) ----------
    public const int IconSettings = 0, IconClose = 1, IconBack = 2, IconNext = 3;
    public static void Icon(Rect r, int icon, Color c)
    {
        float s = Mathf.Min(r.width, r.height), cx = r.center.x, cy = r.center.y, t = Mathf.Max(1.5f, s * 0.085f);
        switch (icon)
        {
            case IconSettings: // three slider lines with knobs at different places
                for (int i = 0; i < 3; i++)
                {
                    float y = cy + (i - 1) * s * 0.28f, w = s * 0.78f, x0 = cx - w * 0.5f;
                    Rect(new Rect(x0, y - t * 0.5f, w, t), A(c, 0.85f), 99f);
                    float k = i == 0 ? 0.30f : i == 1 ? 0.70f : 0.45f, kr = s * 0.11f;
                    var kc = new Rect(x0 + w * k - kr, y - kr, 2 * kr, 2 * kr);
                    Rect(kc, c, 99f);
                }
                break;
            case IconClose:
            {
                var m = GUI.matrix; var pv = new Vector2(cx, cy); float w = s * 0.62f;
                GUIUtility.RotateAroundPivot(45f, pv); Rect(new Rect(cx - w * 0.5f, cy - t * 0.5f, w, t), c, 99f); Rect(new Rect(cx - t * 0.5f, cy - w * 0.5f, t, w), c, 99f);
                GUI.matrix = m;
                break;
            }
            case IconBack:
            case IconNext:
            {
                // a chevron: an L of two bars with its corner at the tip, turned 45° (clockwise on screen)
                var m = GUI.matrix; float w = s * 0.42f, dir = icon == IconBack ? -1f : 1f, h = t * 0.5f;
                var pv = new Vector2(cx + dir * w * 0.35f, cy);
                GUIUtility.RotateAroundPivot(45f, pv);
                if (icon == IconNext) { Rect(new Rect(pv.x - w, pv.y - h, w + h, t), c, 99f); Rect(new Rect(pv.x - h, pv.y - h, t, w + h), c, 99f); } // arms left + down -> ">"
                else { Rect(new Rect(pv.x - h, pv.y - h, w + h, t), c, 99f); Rect(new Rect(pv.x - h, pv.y - w, t, w + h), c, 99f); }       // arms right + up -> "<"
                GUI.matrix = m;
                break;
            }
        }
    }

    // a round glass button with an icon (settings, close)
    public static bool IconButton(Rect r, int icon, bool enabled = true)
    {
        bool clicked = Press(r, enabled, out float p);
        var rr = Scale(r, 1f - 0.06f * p);
        PushAlpha(enabled ? 1f : 0.35f);
        Shadow(rr, 999f, 6f, 0.25f, 2f);
        Rect(rr, Color.Lerp(new Color(0.12f, 0.13f, 0.17f, 0.88f), new Color(0.22f, 0.23f, 0.28f, 0.95f), p), 999f);
        Outline(rr, new Color(1, 1, 1, 0.14f + 0.12f * p), 999f, 0.7f);
        Icon(Scale(rr, 0.48f), icon, TextCol);
        PopAlpha();
        return clicked;
    }

    // ---------- pickers ----------
    // a segmented picker (‹ value ›) for small choice lists; returns the new index
    public static int Stepper(Rect r, string value, int index, int count, bool wrap = true)
    {
        Rect(r, Glass, 10f); Outline(r, Line, 10f, 0.7f);
        float bw = Mathf.Min(r.height, r.width * 0.3f);
        var lb = new Rect(r.x, r.y, bw, r.height); var rb = new Rect(r.xMax - bw, r.y, bw, r.height);
        if (Arrow(lb, IconBack, wrap || index > 0)) index = wrap ? (index - 1 + count) % count : Mathf.Max(0, index - 1);
        if (Arrow(rb, IconNext, wrap || index < count - 1)) index = wrap ? (index + 1) % count : Mathf.Min(count - 1, index + 1);
        Text(new Rect(r.x + bw, r.y, r.width - 2 * bw, r.height), value, Semi, 12.5f, TextCol, TextAnchor.MiddleCenter);
        return index;
    }
    // a chevron tap zone (the whole rect is the touch target; the chevron sits in a small round chip)
    static bool Arrow(Rect r, int icon, bool enabled)
    {
        bool clicked = Press(r, enabled, out float p);
        float d = Mathf.Min(r.width, r.height) * 0.62f;
        var chip = new Rect(r.center.x - d * 0.5f, r.center.y - d * 0.5f, d, d);
        PushAlpha(enabled ? 1f : 0.3f);
        Rect(Scale(chip, 1f - 0.08f * p), new Color(1, 1, 1, 0.06f + 0.16f * p), 999f);
        Icon(Scale(chip, 0.62f), icon, TextCol);
        PopAlpha();
        return clicked;
    }

    static readonly Dictionary<int, float> pillDir = new Dictionary<int, float>();
    // a compact option pill: small caption on top, the value below, ‹ › on the right. Tapping the body steps
    // forward too (one-thumb friendly). The value slides in when it changes. Returns the new index.
    public static int Pill(Rect r, string caption, string value, int index, int count, bool enabled = true, bool wrap = true)
    {
        int key = caption.GetHashCode();
        float a = enabled ? 1f : 0.38f;
        PushAlpha(a);
        Rect(r, new Color(0.11f, 0.12f, 0.155f, 0.92f), 12f);
        Outline(r, new Color(1, 1, 1, 0.07f), 12f, 0.7f);
        float aw = Mathf.Min(U(34), r.width * 0.22f);
        var body = new Rect(r.x, r.y, r.width - 2 * aw, r.height);
        var lb = new Rect(r.xMax - 2 * aw, r.y, aw, r.height); var rb = new Rect(r.xMax - aw, r.y, aw, r.height);
        int old = index; float dirNow = 0f;
        if (Press(body, enabled && count > 1, out float pb)) { index = wrap ? (index + 1) % count : Mathf.Min(count - 1, index + 1); dirNow = 1f; }
        if (pb > 0.01f) Rect(body, new Color(1, 1, 1, 0.06f * pb), 12f);
        if (Arrow(lb, IconBack, enabled && (wrap || index > 0))) { index = wrap ? (index - 1 + count) % count : Mathf.Max(0, index - 1); dirNow = -1f; }
        if (Arrow(rb, IconNext, enabled && (wrap || index < count - 1))) { index = wrap ? (index + 1) % count : Mathf.Min(count - 1, index + 1); dirNow = 1f; }
        Rect(new Rect(lb.x, r.y + r.height * 0.28f, Mathf.Max(1f, U(0.6f)), r.height * 0.44f), new Color(1, 1, 1, 0.07f));
        if (index != old) { Mark(key); pillDir[key] = dirNow; }
        float t = EaseOut(Since(key) / 0.22f), dir = pillDir.TryGetValue(key, out float sd) ? sd : 1f;
        float px = U(12);
        Text(new Rect(r.x + px, r.y + r.height * 0.10f, body.width - px, r.height * 0.36f), Caps(caption), Bold, 8.2f, Faint);
        float vs = Fit(value, Bold, 13.5f, body.width - px * 1.3f, 10f);
        PushAlpha(0.25f + 0.75f * t);
        Text(new Rect(r.x + px + U(8) * dir * (1f - t), r.y + r.height * 0.42f, body.width - px, r.height * 0.46f), value, Bold, vs, TextCol);
        PopAlpha();
        PopAlpha();
        return index;
    }

    // segmented control (2-5 choices): a sliding highlight. accent = the highlight is red (setting values), else glass
    // (navigation). key: any stable number for the highlight's motion (0 = from the names array).
    public static int Segmented(Rect r, string[] names, int sel, bool accent = false, bool enabled = true, float size = 11.5f, int key = 0)
    {
        int n = names.Length; if (n == 0) return sel;
        if (key == 0) key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(names);
        PushAlpha(enabled ? 1f : 0.38f);
        Rect(r, new Color(0.05f, 0.06f, 0.08f, 0.85f), 999f);
        Outline(r, new Color(1, 1, 1, 0.07f), 999f, 0.7f);
        float inset = U(3), w = (r.width - 2 * inset) / n;
        float pos = Smooth(key, sel, 16f);
        var hl = new Rect(r.x + inset + pos * w, r.y + inset, w, r.height - 2 * inset);
        if (sel >= 0 && sel < n)
        {
            if (accent) { Shadow(hl, 999f, 4f, 0.25f, 1.5f); Gradient(hl, priTop, priBottom, 999f); }
            else { Rect(hl, new Color(1, 1, 1, 0.13f), 999f); Outline(hl, new Color(1, 1, 1, 0.14f), 999f, 0.7f); }
        }
        int res = sel;
        for (int i = 0; i < n; i++)
        {
            var cr = new Rect(r.x + inset + i * w, r.y, w, r.height);
            if (Press(cr, enabled, out float p) && i != sel) res = i;
            if (p > 0.01f && i != sel) Rect(Scale(new Rect(cr.x, r.y + inset, cr.width, r.height - 2 * inset), 1f - 0.04f * p), new Color(1, 1, 1, 0.07f * p), 999f);
            float on = Mathf.Clamp01(1f - Mathf.Abs(pos - i));
            Text(cr, names[i], on > 0.5f ? Bold : Semi, Fit(names[i], Bold, size, w - U(10), 8f), Color.Lerp(Sub, Color.white, on), TextAnchor.MiddleCenter);
        }
        PopAlpha();
        return res;
    }

    // ---------- data chips and tags ----------
    // a data chip: tracked caption over a value; returns the width it used (for laying chips in a row)
    public static float ChipWidth(string caption, string value, float valueSize = 13f) => Mathf.Max(Width(Caps(caption), Bold, 8.2f), Width(value, Bold, valueSize)) + U(22);
    public static void Chip(Rect r, string caption, string value, Color valueCol, float valueSize = 13f)
    {
        Rect(r, new Color(1, 1, 1, 0.05f), 10f);
        Outline(r, new Color(1, 1, 1, 0.07f), 10f, 0.7f);
        float px = U(11);
        Text(new Rect(r.x + px, r.y + r.height * 0.08f, r.width - px, r.height * 0.40f), Caps(caption), Bold, 8.2f, Faint);
        Text(new Rect(r.x + px, r.y + r.height * 0.42f, r.width - px, r.height * 0.48f), value, Bold, valueSize, valueCol);
    }
    // a small filled pill tag ("+18 PTS", "FASTEST LAP"); returns its width
    public static float TagWidth(string text, float size = 9f) => Width(text, Black, size) + U(16);
    public static void Tag(Rect r, string text, Color bg, Color fg, float size = 9f)
    {
        Rect(r, bg, 999f);
        Text(r, text, Black, size, fg, TextAnchor.MiddleCenter);
    }

    // a table row background: zebra, or the player's row (red tint with a bar)
    public static void RowBg(Rect r, int i, bool player)
    {
        if (player)
        {
            Gradient(r, new Color(Accent.r, Accent.g, Accent.b, 0.34f), new Color(Accent.r, Accent.g, Accent.b, 0.10f), 6f, true);
            Bar(new Rect(r.x, r.y + r.height * 0.18f, U(2.5f), r.height * 0.64f), AccentHi);
        }
        else if (i % 2 == 0) Rect(r, new Color(1, 1, 1, 0.035f), 6f);
    }

    // full-screen dim behind a modal page
    public static void Scrim(float a = 0.55f) => Rect(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, a));
}
