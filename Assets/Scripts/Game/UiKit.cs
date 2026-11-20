// UiKit.cs — the shared drawing kit of every screen, drawn with IMGUI. Two looks, one kit:
//  * PAPER, the front end (Home, Play steps, Driver, Settings, Season hub, Loading, Results, Qualifying results): a
//    light editorial / Swiss page. Warm paper (#F3F1EC), ink text (#111111) and one grey (#4B4B4B), 1 px ink rules that
//    frame the page (under the masthead, above the actions), whitespace and strict left / right alignment on a 28-unit
//    margin. Typeface Archivo (SIL OFL 1.1, Assets/Resources/Fonts/OFL-Archivo.txt): Regular, SemiBold, ExtraBold and
//    ExtraBold Italic (the giant number). Red #E10600 is the ONLY accent: the giant italic number, the active nav
//    underline, the small square on the black primary button, selected states. The primary button is solid black with
//    white tracked capitals (rectangular, no glow); secondary actions are ExtraBold text links with a grey sub-line.
//    Green / amber appear only as good / bad tags. No glass, gradients, glows or dark panels; the only shadow is the soft
//    ellipse under the 3D driver (MenuStudio.cs).
//  * GLASS, the in-race HUD and captions drawn over the 3D view (RaceHud.cs, TeamRadio.cs, PreRace.cs...): dark glass,
//    Titillium Web (OFL), kept as it was for readability over the moving picture.
// Layout unit U(1) = 1/390 of the screen height (the mockups are drawn on a 844 x 390 artboard in these units).
//
// Use (paper):  var f = UiKit.PaperFrame();  UiKit.PaperPage();  int nav = UiKit.Masthead(f, UiKit.NavPlay);
//               UiKit.Text(r, "Monza", UiKit.Heavy, 26f, UiKit.Ink);  if (UiKit.BlackButton(r, "Race")) ...
// Fonts can be null on a platform where a .ttf failed to import; every helper then falls back (Archivo -> Titillium ->
// the default font). Everything here is allocation-free per frame once warmed up: styles, caps strings and the few
// textures are cached. Fade groups with PushAlpha / PopAlpha (every helper multiplies GUI.color).
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

    static Color Hex(int rgb, float a = 1f) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
    public static Color A(Color c, float a) { c.a *= a; return c; }

    // ---------- PAPER palette (front end) ----------
    // Contrast on Paper: Ink 16.7:1, InkSub 7.7:1, InkMuted 4.7:1 (captions only), GoodInk 4.7:1, BadInk 5.2:1; white on
    // Ink 18.9:1, white on GoodInk / BadInk >= 5.2:1. Red on Paper is 4.4:1: only for large type and marks, never small text.
    public static readonly Color Paper = Hex(0xF3F1EC);          // the page
    public static readonly Color Ink = Hex(0x111111);            // text, rules, the primary button
    public static readonly Color InkSub = Hex(0x4B4B4B);         // secondary text
    public static readonly Color InkMuted = Hex(0x6B6B6B);       // captions, hints, credits
    public static readonly Color PaperTrack = Hex(0xDAD6CE);     // meter tracks, quiet row dividers
    public static readonly Color PaperTint = Hex(0x111111, 0.05f); // pressed / the player's row
    public static readonly Color InkPressed = Hex(0x2E2E2E);     // the black button while held
    public static readonly Color Red = Hex(0xE10600);            // THE accent: giant number, active nav, selection, the button's mark
    public static readonly Color GoodInk = Hex(0x1E7B3A);        // good tags (safety up, gains)
    public static readonly Color BadInk = Hex(0x9A5200);         // bad tags (safety down, penalties)
    public static readonly Color PaperShadow = Hex(0x000000, 0.18f);

    // safety grade S A B -> good, C -> ink, D E -> bad (Progression.Grades order)
    public static Color PaperGrade(int g) => g <= 2 ? GoodInk : g == 3 ? Ink : BadInk;

    // ---------- GLASS palette (in-race HUD and captions only; the front end does not use these) ----------
    public static readonly Color Base = new Color(0.071f, 0.078f, 0.094f, 1f);           // #121418 charcoal
    public static readonly Color Surface = new Color(0.098f, 0.106f, 0.125f, 0.92f);
    public static readonly Color SurfaceHi = new Color(0.135f, 0.145f, 0.168f, 0.95f);
    public static readonly Color PanelCol = new Color(0.086f, 0.094f, 0.112f, 0.90f);
    public static readonly Color PanelHi = SurfaceHi;
    public static readonly Color Line = new Color(1f, 1f, 1f, 0.08f);
    public static readonly Color TextCol = new Color(0.94f, 0.945f, 0.955f, 1f);
    public static readonly Color Sub = new Color(0.64f, 0.67f, 0.71f, 1f);
    public static readonly Color Faint = new Color(0.50f, 0.53f, 0.57f, 1f);
    public static readonly Color Accent = Red;                                             // #E10600 (same red in both looks)
    public static readonly Color AccentHi = new Color(1f, 0.20f, 0.12f, 1f);
    public static readonly Color Info = new Color(0f, 0.78f, 0.95f, 1f);
    public static readonly Color Good = new Color(0.20f, 0.78f, 0.42f, 1f);
    public static readonly Color Warn = new Color(0.98f, 0.74f, 0.16f, 1f);
    public static readonly Color Purple = new Color(0.66f, 0.33f, 0.97f, 1f);
    public static readonly Color Gold = new Color(1f, 0.80f, 0.32f, 1f);
    public static readonly Color Silver = new Color(0.80f, 0.83f, 0.88f, 1f);
    public static readonly Color Bronze = new Color(0.86f, 0.55f, 0.33f, 1f);

    // ---------- fonts ----------
    // Front end (Archivo): Body 400, Medium 600, Heavy 800, HeavyItalic 800 italic.
    // HUD (Titillium Web): Regular, Semi, Bold, Black, BoldItalic.
    static bool fontsLoaded;
    static Font regular, semi, bold, black, boldItalic;
    static Font body, medium, heavy, heavyItalic;
    static void LoadFonts()
    {
        if (fontsLoaded) return; fontsLoaded = true;
        regular = Resources.Load<Font>("Fonts/TitilliumWeb-Regular");
        semi = Resources.Load<Font>("Fonts/TitilliumWeb-SemiBold");
        bold = Resources.Load<Font>("Fonts/TitilliumWeb-Bold");
        black = Resources.Load<Font>("Fonts/TitilliumWeb-Black");
        boldItalic = Resources.Load<Font>("Fonts/TitilliumWeb-BoldItalic");
        body = Resources.Load<Font>("Fonts/Archivo-Regular");
        medium = Resources.Load<Font>("Fonts/Archivo-SemiBold");
        heavy = Resources.Load<Font>("Fonts/Archivo-ExtraBold");
        heavyItalic = Resources.Load<Font>("Fonts/Archivo-ExtraBoldItalic");
    }
    public static Font Regular { get { LoadFonts(); return regular; } }
    public static Font Semi { get { LoadFonts(); return semi ?? regular; } }
    public static Font Bold { get { LoadFonts(); return bold ?? semi ?? regular; } }
    public static Font Black { get { LoadFonts(); return black ?? bold ?? regular; } }
    public static Font BoldItalic { get { LoadFonts(); return boldItalic ?? bold ?? regular; } }
    public static Font Body { get { LoadFonts(); return body ?? Regular; } }
    public static Font Medium { get { LoadFonts(); return medium ?? body ?? Semi; } }
    public static Font Heavy { get { LoadFonts(); return heavy ?? medium ?? Black; } }
    public static Font HeavyItalic { get { LoadFonts(); return heavyItalic ?? heavy ?? BoldItalic; } }

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
        var ev = Event.current; if (ev != null && ev.type != EventType.Repaint) return; // a label draws nothing on other events (GUI.Label returns too)
        var st = Style(f, size, anchor, wrap);
        var old = GUI.color; GUI.color = c * old; GUI.Label(r, Content(s), st); GUI.color = old;
    }

    // One GUIContent per string object. Unity 6's GUI.Label(Rect, string, ...) goes through GUIContent.Temp, whose text
    // setter makes a new string (text + a zero-width space) on EVERY call: ~40 B per label per OnGUI pass, ~3 KB a frame
    // for the race HUD (Docs/PERF-INVESTIGATION.md). A GUIContent made once per string keeps that copy. The HUD's
    // strings come from tables (HudNum, HudSlot), so the same objects come back. Keyed by reference: a string must not
    // be changed in place after it was shown here (FrameLog's TextLine is drawn by BitText instead).
    sealed class RefEq : IEqualityComparer<string>
    {
        public bool Equals(string a, string b) => ReferenceEquals(a, b);
        public int GetHashCode(string s) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s);
    }
    static readonly Dictionary<string, GUIContent> contents = new Dictionary<string, GUIContent>(1024, new RefEq());
    public static GUIContent Content(string s)
    {
        if (!contents.TryGetValue(s, out var gcs))
        {
            if (contents.Count >= 3000) contents.Clear(); // menus that build a new string per frame (slider values) cycle it
            gcs = new GUIContent(s); contents[s] = gcs;
        }
        return gcs;
    }

    // text with a soft dark drop behind it, for text drawn straight over the 3D view (HUD)
    public static void TextShadow(Rect r, string s, Font f, float size, Color c, TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        float o = Mathf.Max(1f, U(0.8f));
        Text(new Rect(r.x + o, r.y + o, r.width, r.height), s, f, size, new Color(0, 0, 0, 0.55f * c.a), anchor);
        Text(r, s, f, size, c, anchor);
    }

    // wide-tracked capitals (IMGUI has no letter spacing, so pad with spaces: an Archivo space is ~0.19 em, the
    // mockups' 3 px tracking at 14 px); cached, so no garbage per frame
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

    public static float Width(string s, Font f, float size) => string.IsNullOrEmpty(s) ? 0f : Style(f, size).CalcSize(Content(s)).x;
    // the height of s wrapped to width w
    public static float Height(string s, Font f, float size, float w) => Style(f, size, TextAnchor.UpperLeft, true).CalcHeight(string.IsNullOrEmpty(s) ? GUIContent.none : Content(s), w);
    // the largest size <= size (down to minSize) at which s fits in maxW
    public static float Fit(string s, Font f, float size, float maxW, float minSize)
    {
        float w = Width(s, f, size);
        if (w <= maxW || w <= 0) return size;
        return Mathf.Max(minSize, size * maxW / w * 0.98f);
    }
    // small tracked caps label in the glass look (HUD captions)
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

    // soft shadow (glass look): a few widening, fading rounded rects
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
    // a vertical (or horizontal: top = left) two-colour gradient, rounded (glass look). Pass FIXED colours (fade with
    // GUI.color / PushAlpha): every distinct pair makes one small cached texture.
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
    // the glass card (in-race captions): shadow, body, a faint top highlight and a hairline edge
    public static void Panel(Rect r, float radius = 12f, float alpha = 1f, bool shadow = true)
    {
        if (shadow) Shadow(r, radius, 10f, 0.32f * alpha);
        PushAlpha(alpha);
        Gradient(r, panelTop, PanelCol, radius);
        Rect(new Rect(r.x + U(radius) * 0.6f, r.y + U(0.6f), r.width - U(radius) * 1.2f, Mathf.Max(1f, U(0.7f))), new Color(1, 1, 1, 0.07f)); // top light
        Outline(r, Line, radius, 0.7f);
        PopAlpha();
    }

    // a thin bar with round ends (a team-colour stripe...)
    public static void Bar(Rect r, Color c) => Rect(r, c, Mathf.Min(r.width, r.height) * 0.5f / Mathf.Max(0.0001f, U(1)));

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

    // =====================================================================================================================
    // PAPER: the front end's page, masthead and controls
    // =====================================================================================================================

    // The page grid: x0 / x1 = the 28-unit margins inside the safe area; head = the rule under the masthead; foot = the
    // rule above the actions (footH units above the safe bottom); top = where content starts under the head rule;
    // actY = the centre line of the action row (links and the black button).
    public struct Frame { public float x0, x1, w, head, foot, top, bottom, actY; public Rect safe; }
    public const float Margin = 28f;
    public static Frame PaperFrame(float footH = 82f)
    {
        var s = Safe; var f = new Frame();
        f.safe = s; f.x0 = s.x + U(Margin); f.x1 = s.xMax - U(Margin); f.w = f.x1 - f.x0;
        f.head = s.y + U(56); f.foot = s.yMax - U(footH); f.top = f.head + U(14); f.bottom = s.yMax;
        f.actY = f.foot + (f.bottom - f.foot) * 0.5f + U(3);
        return f;
    }

    // the whole screen in paper (opacity < 1 lets a paused race show through a little)
    public static void PaperPage(float opacity = 1f) => Rect(new Rect(0, 0, Screen.width, Screen.height), A(Paper, opacity));

    // a 1 px-class rule (U(0.7) rounded, at least one pixel), ink by default
    public static float HairPx => Mathf.Max(1f, Mathf.Round(U(0.7f)));
    public static void Hair(float x0, float x1, float y) => Hair(x0, x1, y, Ink);
    public static void Hair(float x0, float x1, float y, Color c) => Rect(new Rect(x0, Mathf.Round(y), x1 - x0, HairPx), c);
    public static void VHair(float x, float y0, float y1, Color c) => Rect(new Rect(Mathf.Round(x), y0, HairPx, y1 - y0), c);

    // a tracked-caps caption (Archivo Regular; "NEXT · QUICK RACE"), InkSub by default
    public static void Kicker(Rect r, string s, float size = 10.5f, TextAnchor anchor = TextAnchor.MiddleLeft) => Text(r, Caps(s), Body, size, InkSub, anchor);
    public static void Kicker(Rect r, string s, Color c, float size = 10.5f, TextAnchor anchor = TextAnchor.MiddleLeft) => Text(r, Caps(s), Body, size, c, anchor);

    // ---- masthead: "APEX WORLD SERIES" (tap = Home) and the text nav, the top rule under them
    public const int NavNone = -1, NavPlay = 0, NavDriver = 1, NavSettings = 2, NavHome = 3;
    static readonly string[] NavNames = { "PLAY", "DRIVER", "SETTINGS" };
    const string Wordmark = "Apex World Series";
    // Set by a page that is not MenuUI's (Settings) when its nav is tapped; MenuUI.Draw takes it (then sets it back to NavNone).
    public static int NavRequest = NavNone;

    // Draws the masthead and returns the item tapped (NavPlay / NavDriver / NavSettings / NavHome) or NavNone.
    // active: the nav item of this page (NavNone: none underlined). nav = false: the wordmark only (session screens, a
    // paused race), with an optional caption on the right instead (tracked caps, grey).
    public static int Masthead(Frame f, int active, bool nav = true, string caption = null)
    {
        int hit = NavNone; float y = f.safe.y;
        string mark = Caps(Wordmark);
        float mw = Width(mark, Heavy, 13.5f);
        if (nav)
        {
            if (Press(new Rect(f.x0 - U(8), y + U(4), mw + U(16), U(46)), true, out float pm)) hit = NavHome;
            Text(new Rect(f.x0, y + U(17), mw + U(4), U(20)), mark, Heavy, 13.5f, Color.Lerp(Ink, Red, pm));
            // items right to left from the margin, 22 units apart; the touch target is the item plus half the gap each side
            float x = f.x1;
            for (int i = NavNames.Length - 1; i >= 0; i--)
            {
                float w = Width(NavNames[i], Medium, 12.5f);
                var r = new Rect(x - w, y + U(15), w, U(22));
                if (Press(new Rect(r.x - U(11), y + U(4), w + U(22), U(46)), true, out float p)) hit = i;
                bool on = i == active;
                Text(r, NavNames[i], on ? Heavy : Medium, 12.5f, Color.Lerp(on ? Ink : InkSub, Red, p));
                if (on) Rect(new Rect(r.x, r.yMax + U(1.5f), Width(NavNames[i], Heavy, 12.5f), Mathf.Max(2f, Mathf.Round(U(2f)))), Red);
                x -= w + U(22);
            }
        }
        else
        {
            Text(new Rect(f.x0, y + U(17), mw + U(4), U(20)), mark, Heavy, 13.5f, Ink);
            if (caption != null)
            {
                string cs = Caps(caption); float cw = f.x1 - f.x0 - mw - U(24);
                Text(new Rect(f.x1 - cw, y + U(17), cw, U(20)), cs, Body, Fit(cs, Body, 10.5f, cw, 7f), InkSub, TextAnchor.MiddleRight);
            }
        }
        Hair(f.x0, f.x1, f.head);
        return hit;
    }

    // ---- the primary button: solid black, white tracked caps, a small red square after the label. Rectangular.
    // Width for a label (30 units padding each side, as the mockup)
    public static float BlackButtonWidth(string label, float size = 16f) => Width(Caps(label), Heavy, size) + U(12 + 10 + 60);
    public static bool BlackButton(Rect r, string label, float size = 16f, bool enabled = true)
    {
        bool clicked = Press(r, enabled, out float p);
        PushAlpha(enabled ? 1f : 0.3f);
        Rect(r, Color.Lerp(Ink, InkPressed, p));
        string s = Caps(label);
        float mark = Mathf.Round(U(9f)), gap = U(12);
        float ts = Fit(s, Heavy, size, r.width - U(32) - mark - gap, size * 0.6f);
        float tw = Width(s, Heavy, ts), x = r.center.x - (tw + gap + mark) * 0.5f;
        Text(new Rect(x, r.y, tw + U(2), r.height), s, Heavy, ts, Color.white);
        Rect(new Rect(Mathf.Round(x + tw + gap), Mathf.Round(r.center.y - mark * 0.5f), mark, mark), Red);
        PopAlpha();
        return clicked;
    }

    // ---- a secondary action: an ExtraBold link with an optional grey sub-line ("Championship / Round 3 of 10 · P4").
    // The rect is the touch target (>= 44 high); the text block is centred in it vertically. Red while pressed;
    // armed (tap again to confirm): a red underline under the title.
    public static float LinkWidth(string title, string sub, float size = 13f) => Mathf.Max(Width(title, Heavy, size), sub != null ? Width(sub, Body, size * 0.92f) : 0f);
    public static bool Link(Rect r, string title, string sub, bool enabled = true, float size = 13f, TextAnchor align = TextAnchor.MiddleLeft, bool armed = false)
    {
        bool clicked = Press(r, enabled, out float p);
        PushAlpha(enabled ? 1f : 0.35f);
        Color tc = Color.Lerp(Ink, Red, p);
        float th = U(size * 1.3f), sh = sub != null ? U(size * 1.15f) : 0f, y0 = r.center.y - (th + sh) * 0.5f;
        Text(new Rect(r.x, y0, r.width, th), title, Heavy, size, tc, align);
        if (sub != null) Text(new Rect(r.x, y0 + th, r.width, sh), sub, Body, size * 0.92f, armed ? Ink : InkSub, align);
        if (armed) // a confirm-to-do state: a red underline under the title (red is not used for small text)
        {
            float tw = Width(title, Heavy, size), ux = align == TextAnchor.MiddleRight ? r.xMax - tw : r.x;
            Rect(new Rect(ux, y0 + th - U(1), tw, Mathf.Max(2f, Mathf.Round(U(2f)))), Red);
        }
        PopAlpha();
        return clicked;
    }

    // ---- a text glyph button ("−", "+", "‹", "›"): the whole rect is the touch target; red while pressed
    public static bool Glyph(Rect r, string glyph, float size = 20f, bool enabled = true)
    {
        bool clicked = Press(r, enabled, out float p);
        Text(r, glyph, Heavy, size, enabled ? Color.Lerp(Ink, Red, p) : A(Ink, 0.22f), TextAnchor.MiddleCenter);
        return clicked;
    }
    public const string Minus = "−", Plus = "+", Prev = "‹", Next = "›";

    // ---- "‹ value ›" picker on a hairline; returns the new index
    public static int Stepper(Rect r, string value, int index, int count, bool wrap = true, float size = 14f)
    {
        float bw = Mathf.Min(U(44), r.width * 0.34f);
        var lb = new Rect(r.x, r.y, bw, r.height); var rb = new Rect(r.xMax - bw, r.y, bw, r.height);
        if (Glyph(lb, Prev, 22f, wrap || index > 0)) index = wrap ? (index - 1 + count) % count : Mathf.Max(0, index - 1);
        if (Glyph(rb, Next, 22f, wrap || index < count - 1)) index = wrap ? (index + 1) % count : Mathf.Min(count - 1, index + 1);
        var mid = new Rect(r.x + bw, r.y, r.width - 2 * bw, r.height);
        Text(mid, value, Heavy, Fit(value, Heavy, size, mid.width - U(4), size * 0.65f), Ink, TextAnchor.MiddleCenter);
        Hair(r.x, r.xMax, r.yMax - HairPx, Ink);
        return index;
    }

    // ---- text tabs: names left to right, the chosen one ExtraBold ink with a red underline; returns the new choice
    public static int Tabs(Rect r, string[] names, int sel, float size = 12.5f, bool enabled = true)
    {
        float x = r.x, gap = U(24); int res = sel;
        for (int i = 0; i < names.Length; i++)
        {
            bool on = i == sel;
            float w = Width(names[i], Heavy, size);
            var tr = new Rect(x, r.y, w, r.height);
            float pad = Mathf.Max(0f, (U(44) - r.height) * 0.5f); // a 44-unit touch target however thin the row
            if (Press(new Rect(x - gap * 0.5f, r.y - pad, w + gap, r.height + 2f * pad), enabled, out float p) && !on) res = i;
            Text(tr, names[i], on ? Heavy : Medium, size, Color.Lerp(on ? Ink : InkSub, Red, p));
            if (on) Rect(new Rect(tr.x, r.center.y + U(size * 0.62f) + U(2), w, Mathf.Max(2f, Mathf.Round(U(2f)))), Red);
            x += w + gap;
        }
        return res;
    }

    // ---- data: a tracked caption over a value (no box); returns the width used
    public static float DatumWidth(string caption, string value, float valueSize = 15f) => Mathf.Max(Width(Caps(caption), Body, 9f), Width(value, Heavy, valueSize));
    public static void Datum(Rect r, string caption, string value, Color valueCol, float valueSize = 15f, TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        Kicker(new Rect(r.x, r.y, r.width, U(13)), caption, 9f, anchor);
        Text(new Rect(r.x, r.y + U(13), r.width, Mathf.Max(U(valueSize * 1.3f), r.height - U(13))), value, Heavy, valueSize, valueCol, anchor);
    }

    // ---- a small rectangular tag ("+18 PTS", "LEVEL UP"): white on ink, or white on GoodInk / BadInk
    public static float StampWidth(string text, float size = 9f) => Width(text, Heavy, size) + U(12);
    public static void Stamp(Rect r, string text, Color bg, Color fg, float size = 9f)
    {
        Rect(r, bg);
        Text(r, text, Heavy, size, fg, TextAnchor.MiddleCenter);
    }

    // ---- a meter: a 3-unit track with an ink fill (XP, loading)
    public static void Meter(Rect r, float frac)
    {
        Rect(r, PaperTrack);
        if (frac > 0.001f) Rect(new Rect(r.x, r.y, Mathf.Max(r.height, r.width * Mathf.Clamp01(frac)), r.height), Ink);
    }

    // ---- a table row: a quiet divider under it; the player's row tinted with a red bar at its left edge
    public static void PaperRow(Rect r, bool player)
    {
        if (player)
        {
            Rect(r, PaperTint);
            Rect(new Rect(r.x, r.y, Mathf.Max(2f, Mathf.Round(U(3f))), r.height), Red);
        }
        Hair(r.x, r.xMax, r.yMax - HairPx, PaperTrack);
    }

    // ---- the giant italic number (driver number, finishing position): ExtraBold Italic, red
    public static void BigNumber(Rect r, string s, float size, Color c, TextAnchor anchor = TextAnchor.MiddleLeft) => Text(r, s, HeavyItalic, size, c, anchor);
}
