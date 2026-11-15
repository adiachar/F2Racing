// SettingsUI.cs — the tabbed settings screen, drawn with IMGUI from GameManager.OnGUI (in a race = paused, and
// from the start menu). It knows nothing about the game: GameManager (GameManagerSettings.cs) hands it tabs of
// rows, each row with a label, a one-line help text, a default and get/set callbacks.
//  * Slider rows: a real draggable slider with the live value and its unit; a tap on the track jumps there,
//    and on wide screens "-" / "+" buttons move one step.
//  * Choice rows: segmented buttons; when the names do not fit they turn into a "< value >" stepper.
//  * Action rows: one button.
//  * Every tab has "Reset tab to defaults" (two taps, so a slip of the thumb does not reset anything).
// The list scrolls by dragging it. IMGUI's own buttons fire on release even after a drag, so all controls here
// are hit-tested by hand: a press becomes a tap, a list scroll (moved up/down) or a slider drag (moved sideways).
// Layout units are the HUD's: 390 units = screen height. Touch targets are 44 units; tabs sit in a column on the
// left, so the same layout fits a 19.5:9 phone (844 units wide) and a 4:3 tablet (520 units wide).
using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class SettingsUI
{
    public enum Kind { Slider, Choice, Action }

    public sealed class Item
    {
        public Kind kind; public string label, help;
        public float min, max, step = 1, def; public bool resettable = true;
        public string[] options;
        public Func<float> get; public Action<float> set; public Func<float, string> fmt;
        public Action act; public Func<string> actText;
        public Func<string> locked;   // returns why the row cannot be used right now, or null
        public Func<bool> hidden;
        public Item LockedWhen(Func<string> why) { locked = why; return this; }
        public Item HiddenWhen(Func<bool> h) { hidden = h; return this; }
        public Item NoReset() { resettable = false; return this; }   // a session choice (track, car...), not a tuning value
    }

    public sealed class Tab
    {
        public readonly string name; public readonly List<Item> items = new List<Item>();
        public Tab(string name) { this.name = name; }
        public Item Slider(string label, string help, float min, float max, float step, float def, Func<float> get, Action<float> set, Func<float, string> fmt)
        {
            var it = new Item { kind = Kind.Slider, label = label, help = help, min = min, max = max, step = step, def = def, get = get, set = set, fmt = fmt };
            items.Add(it); return it;
        }
        public Item Choice(string label, string help, string[] options, int def, Func<int> get, Action<int> set)
        {
            var it = new Item { kind = Kind.Choice, label = label, help = help, options = options, min = 0, max = options.Length - 1, def = def, get = () => get(), set = v => set(Mathf.RoundToInt(v)) };
            items.Add(it); return it;
        }
        static readonly string[] OffOn = { "Off", "On" };
        public Item Toggle(string label, string help, bool def, Func<bool> get, Action<bool> set) =>
            Choice(label, help, OffOn, def ? 1 : 0, () => get() ? 1 : 0, v => set(v == 1));
        public Item Button(string label, string help, Func<string> text, Action act)
        {
            var it = new Item { kind = Kind.Action, label = label, help = help, actText = text, act = act, resettable = false };
            items.Add(it); return it;
        }
    }

    readonly List<Tab> tabs; readonly float[] scroll;
    readonly Action commit;   // called after a change is finished (finger lifted): GameManager saves
    public int tab;

    public SettingsUI(List<Tab> tabs, Action commit) { this.tabs = tabs; this.commit = commit; scroll = new float[tabs.Count]; }

    // ------------------------------------------------------------------ pointer (one finger or the mouse)
    const int EvNone = 0, EvDown = 1, EvDrag = 2, EvUp = 3, EvWheel = 4;
    const int Idle = 0, Pending = 1, Scrolling = 2, SliderDrag = 3;
    const int IdTab = 900000, IdClose = 900100, IdReset = 900101;
    int evt, mode, pressId = -1, clickId = -1;
    bool pressSlider, pressInList, dirty;
    Vector2 mp, pressPos, origin; float pressScroll; Rect clip;
    int resetArmedTab = -1; float resetArmedT = -10, resetDoneT = -10;

    static int EventKind(Event e)
    {
        switch (e.type)
        {
            case EventType.MouseDown: return e.button == 0 ? EvDown : EvNone;
            case EventType.MouseDrag: return e.button == 0 ? EvDrag : EvNone;
            case EventType.MouseUp: return e.button == 0 ? EvUp : EvNone;
            case EventType.TouchDown: return EvDown;
            case EventType.TouchMove: return EvDrag;
            case EventType.TouchUp: return EvUp;
            case EventType.ScrollWheel: return EvWheel;
        }
        return EvNone;
    }
    bool Hit(Rect screenRect) => screenRect.Contains(mp) && clip.Contains(mp);
    Rect OnScreen(Rect local) => new Rect(local.x + origin.x, local.y + origin.y, local.width, local.height);

    // ------------------------------------------------------------------ drawing (UiKit look)
    static float S(float v) => UiKit.U(v);
    const int LookGlass = 0, LookAccent = 1, LookGhost = 2, LookArmed = 3, LookNone = 4;
    const float LabelSize = 13.5f, HelpSize = 10f, SegSize = 11.5f;

    // a hand-tested button: true on the frame it is tapped. Draws a rounded body by look (LookNone = only the press
    // tint) and the text centred; radius in layout units (999 = pill).
    bool Btn(Rect r, int id, string text, int look, bool live = true, float size = SegSize, float radius = 10f)
    {
        var sr = OnScreen(r);
        bool held = live && mode == Pending && pressId == id && Hit(sr);
        float p = UiKit.Smooth(id ^ 0x3A000000, held ? 1f : 0f, 26f);
        var rr = UiKit.Scale(r, 1f - 0.03f * p);
        Color tc = UiKit.TextCol;
        switch (look)
        {
            case LookGlass:
                UiKit.Rect(rr, Color.Lerp(new Color(1, 1, 1, 0.07f), new Color(1, 1, 1, 0.16f), p), radius);
                UiKit.Outline(rr, new Color(1, 1, 1, 0.09f), radius, 0.7f);
                break;
            case LookAccent:
                UiKit.Gradient(rr, accTop, accBottom, radius);
                UiKit.Rect(rr, new Color(0, 0, 0, 0.2f * p), radius);
                tc = Color.white;
                break;
            case LookGhost:
                UiKit.Rect(rr, new Color(1, 1, 1, 0.02f + 0.08f * p), radius);
                UiKit.Outline(rr, new Color(1, 1, 1, 0.12f), radius, 0.7f);
                tc = UiKit.Sub;
                break;
            case LookArmed:
                UiKit.Rect(rr, new Color(UiKit.Accent.r, UiKit.Accent.g, UiKit.Accent.b, 0.18f + 0.15f * p), radius);
                UiKit.Outline(rr, UiKit.Accent, radius, 1.2f);
                tc = Color.white;
                break;
            default:
                if (p > 0.01f) UiKit.Rect(rr, new Color(1, 1, 1, 0.08f * p), radius);
                break;
        }
        if (text != null) UiKit.Text(rr, text, UiKit.Bold, size, tc, TextAnchor.MiddleCenter);
        if (!live) return false;
        if (evt == EvDown && Hit(sr)) pressId = id;
        return evt == EvUp && clickId == id && Hit(sr);
    }
    static readonly Color accTop = new Color(1f, 0.16f, 0.09f, 1f), accBottom = new Color(0.78f, 0.02f, 0f, 1f);
    static readonly Color panelTop = new Color(0.075f, 0.085f, 0.11f, 0.97f), panelBottom = new Color(0.045f, 0.052f, 0.07f, 0.97f);
    const int KeyTabHl = 0x5E77AB, KeyTabSwitch = 0x5E77AC;

    void Set(Item it, float v)
    {
        if (it.step > 0) v = it.min + Mathf.Round((v - it.min) / it.step) * it.step;
        v = Mathf.Round(Mathf.Clamp(v, it.min, it.max) * 1000f) / 1000f;
        if (Mathf.Abs(v - it.get()) < 1e-4f) return;
        it.set(v); dirty = true;
    }

    // Draws the screen. Returns true when the player closes it.
    public bool Draw(string subtitle)
    {
        var e = Event.current; evt = EventKind(e); mp = e.mousePosition;
        float sw = Screen.width, sh = Screen.height, m = S(8);
        origin = Vector2.zero; clip = new Rect(0, 0, sw, sh);
        bool close = false;

        // ---- layout
        var safe = UiKit.Safe; // keeps clear of a notch at the short edges
        float left = Mathf.Max(m, safe.xMin + S(6)), right = Mathf.Min(sw - m, safe.xMax - S(6));
        float pw = Mathf.Min(right - left, S(920)), px = left + (right - left - pw) / 2;
        var panel = new Rect(px, Mathf.Max(m, safe.y + S(6)), pw, Mathf.Min(sh, safe.yMax) - Mathf.Max(m, safe.y + S(6)) - m);
        float pad = S(12), headH = S(54), bodyY = panel.y + headH, bodyH = panel.yMax - S(10) - bodyY;
        float tabW = Mathf.Clamp(pw * 0.2f, S(108), S(164)), tabGap = S(4);
        float tabH = Mathf.Min(S(44), (bodyH - (tabs.Count - 1) * tabGap) / tabs.Count);
        float cx = panel.x + pad + tabW + S(16), cw = panel.xMax - pad - cx;
        float footH = S(44), listH = bodyH - footH - S(8);
        var view = new Rect(cx, bodyY, cw, listH);
        float lw = Mathf.Min(cw * 0.42f, S(300)), ctlW = cw - lw - S(10);
        float rowH = lw < S(220) ? S(70) : S(60); // a narrow label column (4:3 screens): room for three lines of help
        tab = Mathf.Clamp(tab, 0, tabs.Count - 1);
        var cur = tabs[tab];
        int shown = 0; foreach (var it in cur.items) if (it.hidden == null || !it.hidden()) shown++;
        float maxScroll = Mathf.Max(0, shown * rowH - listH);

        // ---- pointer state for this event
        clickId = -1;
        if (evt == EvDown) { mode = Pending; pressId = -1; pressSlider = false; pressPos = mp; pressInList = view.Contains(mp); pressScroll = scroll[tab]; }
        else if (evt == EvDrag && mode != Idle)
        {
            float dx = mp.x - pressPos.x, dy = mp.y - pressPos.y;
            if (mode == Pending)
            {
                if (pressSlider && Mathf.Abs(dx) > S(6) && Mathf.Abs(dx) >= Mathf.Abs(dy)) mode = SliderDrag;
                else if (pressInList && Mathf.Abs(dy) > S(8)) mode = Scrolling;
                else if (!pressInList && dx * dx + dy * dy > S(14) * S(14)) mode = Scrolling; // slid off a button: no tap
            }
            if (mode == Scrolling && pressInList) scroll[tab] = pressScroll - dy;
        }
        else if (evt == EvUp && mode == Pending) clickId = pressId;
        else if (evt == EvWheel && view.Contains(mp)) scroll[tab] += e.delta.y * S(18);
        scroll[tab] = Mathf.Clamp(scroll[tab], 0, maxScroll);

        // ---- frame: dimmed page, one big glass sheet
        bool inRace = !string.IsNullOrEmpty(subtitle);
        UiKit.Backdrop(inRace ? 0.82f : 1f, !inRace);
        float ent = UiKit.Enter("settings", 0, 0.04f, 0.4f);
        UiKit.PushAlpha(ent);
        panel = UiKit.Rise(panel, ent, 14f);
        UiKit.Shadow(panel, 18f, 14f, 0.45f, 4f);
        UiKit.Gradient(panel, panelTop, panelBottom, 18f);
        UiKit.Outline(panel, new Color(1, 1, 1, 0.08f), 18f, 0.7f);
        UiKit.Bar(new Rect(panel.x + pad, panel.y + S(17), S(4), S(22)), UiKit.Accent);
        UiKit.Text(new Rect(panel.x + pad + S(12), panel.y + S(8), S(200), S(40)), "Settings", UiKit.Black, 24f, UiKit.TextCol);
        if (inRace)
        {
            string tag = UiKit.Upper(subtitle); float tw = UiKit.TagWidth(tag, 9f);
            float tx = panel.x + pad + S(12) + UiKit.Width("Settings", UiKit.Black, 24f) + S(12);
            UiKit.Tag(new Rect(tx, panel.y + S(19), tw, S(18)), tag, new Color(UiKit.Warn.r, UiKit.Warn.g, UiKit.Warn.b, 0.16f), UiKit.Warn, 9f);
        }
        var closeR = new Rect(panel.xMax - pad - S(44), panel.y + S(6), S(44), S(44));
        if (Btn(closeR, IdClose, null, LookGlass, true, SegSize, 999f)) close = true;
        UiKit.Icon(UiKit.Scale(closeR, 0.42f), UiKit.IconClose, UiKit.TextCol);
        UiKit.Rect(new Rect(panel.x + pad, bodyY - S(2), panel.width - 2 * pad, Mathf.Max(1f, S(0.6f))), new Color(1, 1, 1, 0.07f));

        // ---- tabs (a column on the left) with a sliding highlight
        float hy = UiKit.Smooth(KeyTabHl, tab, 16f);
        var hl = new Rect(panel.x + pad, bodyY + S(6) + hy * (tabH + tabGap), tabW, tabH);
        UiKit.Rect(hl, new Color(1, 1, 1, 0.09f), 12f);
        UiKit.Bar(new Rect(hl.x + S(6), hl.y + tabH * 0.28f, S(3), tabH * 0.44f), UiKit.Accent);
        for (int i = 0; i < tabs.Count; i++)
        {
            var r = new Rect(panel.x + pad, bodyY + S(6) + i * (tabH + tabGap), tabW, tabH);
            bool on = i == tab;
            if (Btn(r, IdTab + i, null, LookNone, true, SegSize, 12f) && !on) { tab = i; resetArmedTab = -1; UiKit.Mark(KeyTabSwitch); }
            float onK = Mathf.Clamp01(1f - Mathf.Abs(hy - i));
            UiKit.Text(new Rect(r.x + S(18), r.y, r.width - S(20), r.height), tabs[i].name, UiKit.Bold, Mathf.Min(11.5f, UiKit.Fit(tabs[i].name, UiKit.Bold, 11.5f, r.width - S(22), 8.5f)), Color.Lerp(UiKit.Faint, UiKit.TextCol, onK));
        }

        // ---- rows (a scrolling list); they slide in after a tab switch
        float since = UiKit.Since(KeyTabSwitch);
        GUI.BeginGroup(view);
        origin = view.position; clip = view;
        int row = 0;
        for (int i = 0; i < cur.items.Count; i++)
        {
            var it = cur.items[i];
            if (it.hidden != null && it.hidden()) continue;
            float y = row * rowH - scroll[tab]; row++;
            if (y + rowH < 0 || y > listH) continue;
            float ra = UiKit.EaseOut((since - (row - 1) * 0.03f) / 0.28f);
            UiKit.PushAlpha(ra);
            y += S(8) * (1f - ra);
            string why = it.locked != null ? it.locked() : null;
            bool live = why == null;
            int id = tab * 10000 + i * 20;
            UiKit.Rect(new Rect(0, y + rowH - S(1), cw - S(8), Mathf.Max(1f, S(0.6f))), new Color(1, 1, 1, 0.06f));
            var oc = GUI.color; if (!live) GUI.color = new Color(oc.r, oc.g, oc.b, oc.a * 0.38f);
            UiKit.Text(new Rect(0, y + S(6), lw - S(10), S(19)), it.label, UiKit.Bold, UiKit.Fit(it.label, UiKit.Bold, LabelSize, lw - S(10), 10.5f), UiKit.TextCol);
            GUI.color = oc;
            UiKit.Text(new Rect(0, y + S(25), lw - S(12), rowH - S(27)), live ? it.help : why, UiKit.Regular, HelpSize, live ? UiKit.Sub : UiKit.Warn, TextAnchor.UpperLeft, true);
            if (!live) GUI.color = new Color(oc.r, oc.g, oc.b, oc.a * 0.38f);
            var cr = new Rect(lw, y + (rowH - S(44)) / 2, ctlW - S(8), S(44));
            if (it.kind == Kind.Slider) DrawSlider(it, cr, id, live);
            else if (it.kind == Kind.Choice) DrawChoice(it, cr, id, live);
            else if (Btn(new Rect(cr.x, cr.y, Mathf.Min(cr.width, S(280)), cr.height), id, it.actText != null ? it.actText() : it.label, LookGlass, live, 12f, 12f)) { it.act?.Invoke(); dirty = true; }
            GUI.color = oc;
            UiKit.PopAlpha();
        }
        if (maxScroll > 0)
        {
            float bar = Mathf.Max(S(20), listH * listH / (shown * rowH)), bw = S(3);
            UiKit.Rect(new Rect(cw - bw, S(4), bw, listH - S(8)), new Color(1, 1, 1, 0.05f), 2f);
            UiKit.Rect(new Rect(cw - bw, S(4) + (listH - S(8) - bar) * scroll[tab] / maxScroll, bw, bar), new Color(1, 1, 1, mode == Scrolling ? 0.55f : 0.30f), 2f);
        }
        GUI.EndGroup();
        origin = Vector2.zero; clip = new Rect(0, 0, sw, sh);

        // ---- footer: hint and "Reset tab to defaults"
        float fy = panel.yMax - S(10) - footH, now = Time.realtimeSinceStartup, rbw = Mathf.Min(cw, S(220));
        UiKit.Rect(new Rect(cx, fy - S(4), cw, Mathf.Max(1f, S(0.6f))), new Color(1, 1, 1, 0.07f));
        if (cw - rbw > S(120)) UiKit.Text(new Rect(cx, fy, cw - rbw - S(8), footH), maxScroll <= 0 ? "Changes apply at once" : cw - rbw < S(280) ? "Drag the list to scroll" : "Drag the list to scroll  ·  changes apply at once", UiKit.Regular, 10f, UiKit.Faint);
        bool armed = resetArmedTab == tab && now - resetArmedT < 3f, done = now - resetDoneT < 1.5f;
        if (Btn(new Rect(cx + cw - rbw, fy + S(2), rbw, footH - S(4)), IdReset, done ? "DEFAULTS RESTORED" : armed ? "TAP AGAIN TO RESET" : "RESET TAB TO DEFAULTS", armed ? LookArmed : LookGhost, true, 10.5f, 999f))
        {
            if (!armed) { resetArmedTab = tab; resetArmedT = now; resetDoneT = -10; }
            else
            {
                foreach (var it in cur.items) if (it.resettable && it.kind != Kind.Action) Set(it, it.def);
                resetArmedTab = -1; resetDoneT = now; dirty = true;
            }
        }
        UiKit.PopAlpha();

        if (evt == EvUp)
        {
            mode = Idle; pressId = -1; pressSlider = false;
            if (dirty) { dirty = false; commit?.Invoke(); }
            e.Use();
        }
        else if (evt == EvDown || evt == EvDrag || evt == EvWheel) e.Use();
        if (close && dirty) { dirty = false; commit?.Invoke(); }
        return close;
    }

    // round "-" / "+" button drawn from bars
    bool StepBtn(Rect r, int id, bool plus, bool live)
    {
        bool hit = Btn(r, id, null, LookGlass, live, SegSize, 999f);
        float w = r.width * 0.34f, t = Mathf.Max(1.5f, S(1.6f)), cx = r.center.x, cy = r.center.y;
        UiKit.Rect(new Rect(cx - w * 0.5f, cy - t * 0.5f, w, t), UiKit.TextCol, 99f);
        if (plus) UiKit.Rect(new Rect(cx - t * 0.5f, cy - w * 0.5f, t, w), UiKit.TextCol, 99f);
        return hit;
    }

    void DrawSlider(Item it, Rect r, int id, bool live)
    {
        float v = it.get(), vw = S(84);
        var box = new Rect(r.xMax - vw, r.y + S(5), vw, r.height - S(10));
        UiKit.Rect(box, new Color(1, 1, 1, 0.06f), 999f);
        UiKit.Outline(box, new Color(1, 1, 1, 0.07f), 999f, 0.7f);
        UiKit.Text(box, it.fmt != null ? it.fmt(v) : v.ToString("0.##"), UiKit.Bold, 12.5f, UiKit.TextCol, TextAnchor.MiddleCenter);
        var ta = new Rect(r.x, r.y, r.width - vw - S(8), r.height);
        if (ta.width > S(250))
        {
            float b = S(36), by = ta.y + (ta.height - b) / 2;
            if (StepBtn(new Rect(ta.x, by, b, b), id + 1, false, live)) Set(it, v - it.step);
            if (StepBtn(new Rect(ta.xMax - b, by, b, b), id + 2, true, live)) Set(it, v + it.step);
            ta = new Rect(ta.x + b + S(4), ta.y, ta.width - 2 * b - S(8), ta.height);
        }
        float x0 = ta.x + S(12), w = Mathf.Max(1, ta.width - S(24)), cy = ta.y + ta.height / 2;
        float t = Mathf.InverseLerp(it.min, it.max, v);
        bool active = live && mode == SliderDrag && pressId == id;
        float th = S(4);
        UiKit.Rect(new Rect(x0, cy - th / 2, w, th), new Color(1, 1, 1, 0.12f), 99f);
        if (t > 0.001f) UiKit.Gradient(new Rect(x0, cy - th / 2, Mathf.Max(th, w * t), th), accBottom, accTop, 99f, true);
        if (it.resettable) UiKit.Rect(new Rect(x0 + w * Mathf.InverseLerp(it.min, it.max, it.def) - S(0.75f), cy - S(8), Mathf.Max(1f, S(1.5f)), S(16)), new Color(1, 1, 1, 0.30f), 1f); // the default
        float a = UiKit.Smooth(id ^ 0x2B000000, active ? 1f : 0f, 20f);
        var kc = new Vector2(x0 + w * t, cy);
        if (a > 0.01f) UiKit.Glow(new Rect(kc.x - S(22), kc.y - S(22), S(44), S(44)), UiKit.A(UiKit.Accent, 0.55f * a));
        UiKit.Dot(kc + new Vector2(0, S(1.2f)), 11.5f + 2f * a, new Color(0, 0, 0, 0.35f));
        UiKit.Dot(kc, 10.5f + 2f * a, Color.white);
        UiKit.Dot(kc, 4f, Color.Lerp(new Color(0.75f, 0.77f, 0.8f), UiKit.Accent, a));
        if (!live) return;
        var hit = OnScreen(ta);
        if (evt == EvDown && Hit(hit)) { pressId = id; pressSlider = true; }
        bool tap = evt == EvUp && clickId == id && Hit(hit);
        if ((active && evt == EvDrag) || tap) Set(it, it.min + Mathf.Clamp01((mp.x - (origin.x + x0)) / w) * (it.max - it.min));
    }

    void DrawChoice(Item it, Rect r, int id, bool live)
    {
        int n = it.options.Length, sel = Mathf.Clamp(Mathf.RoundToInt(it.get()), 0, n - 1);
        float inset = S(3), sw = (r.width - 2 * inset) / n;
        bool fits = n <= 5;
        for (int k = 0; k < n && fits; k++) if (UiKit.Width(it.options[k], UiKit.Bold, SegSize) + S(10) > sw) fits = false;
        if (fits)
        {
            // segmented: a dark track, the chosen value in a red pill that slides
            UiKit.Rect(r, new Color(0.03f, 0.035f, 0.05f, 0.75f), 999f);
            UiKit.Outline(r, new Color(1, 1, 1, 0.07f), 999f, 0.7f);
            float pos = UiKit.Smooth(id ^ 0x1C000000, sel, 16f);
            var hl = new Rect(r.x + inset + pos * sw, r.y + inset, sw, r.height - 2 * inset);
            UiKit.Gradient(hl, accTop, accBottom, 999f);
            for (int k = 0; k < n; k++)
            {
                var sr = new Rect(r.x + inset + k * sw, r.y + inset, sw, r.height - 2 * inset);
                bool tapped = Btn(sr, id + 1 + k, null, LookNone, live, SegSize, 999f);
                float on = Mathf.Clamp01(1f - Mathf.Abs(pos - k));
                UiKit.Text(sr, it.options[k], on > 0.5f ? UiKit.Bold : UiKit.Semi, SegSize, Color.Lerp(UiKit.Sub, Color.white, on), TextAnchor.MiddleCenter);
                if (tapped && k != sel) Set(it, k);
            }
            return;
        }
        // too many or too long for segments: "< value >"
        float b = r.height;
        UiKit.Rect(r, new Color(1, 1, 1, 0.05f), 999f);
        UiKit.Outline(r, new Color(1, 1, 1, 0.07f), 999f, 0.7f);
        var lb = new Rect(r.x, r.y, b, b); var rb = new Rect(r.xMax - b, r.y, b, b);
        if (Btn(lb, id + 1, null, LookNone, live, SegSize, 999f)) Set(it, (sel - 1 + n) % n);
        if (Btn(rb, id + 2, null, LookNone, live, SegSize, 999f)) Set(it, (sel + 1) % n);
        UiKit.Icon(UiKit.Scale(lb, 0.36f), UiKit.IconBack, UiKit.TextCol);
        UiKit.Icon(UiKit.Scale(rb, 0.36f), UiKit.IconNext, UiKit.TextCol);
        var mid = new Rect(r.x + b, r.y, r.width - 2 * b, b);
        UiKit.Text(mid, it.options[sel], UiKit.Bold, UiKit.Fit(it.options[sel], UiKit.Bold, 12.5f, mid.width - S(6), 9f), UiKit.TextCol, TextAnchor.MiddleCenter);
        // position dots under the value (up to 12 choices)
        if (n <= 12)
        {
            float d = S(4), dg = S(3), tw = n * d + (n - 1) * dg, dx = mid.center.x - tw / 2, dy = r.yMax - S(7);
            for (int k = 0; k < n; k++) UiKit.Rect(new Rect(dx + k * (d + dg), dy, d, Mathf.Max(1f, S(1.5f))), k == sel ? UiKit.Accent : new Color(1, 1, 1, 0.18f), 1f);
        }
    }
}
