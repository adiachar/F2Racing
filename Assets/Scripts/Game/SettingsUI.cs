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

    // ------------------------------------------------------------------ drawing
    static float S(float v) => v * Screen.height / 390f;
    GUIStyle ttl, cap, lab, hlp, val, seg, tabSt;
    static readonly GUIContent tmp = new GUIContent();
    static readonly Color Accent = MenuUI.Accent;

    void Styles()
    {
        if (ttl == null)
        {
            ttl = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false };
            cap = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            lab = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            hlp = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true, clipping = TextClipping.Clip };
            val = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Clip };
            seg = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Clip };
            tabSt = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            ttl.normal.textColor = lab.normal.textColor = val.normal.textColor = seg.normal.textColor = tabSt.normal.textColor = Color.white;
            cap.normal.textColor = hlp.normal.textColor = MenuUI.Dim;
        }
        ttl.fontSize = (int)S(20); cap.fontSize = (int)S(11); lab.fontSize = (int)S(14); hlp.fontSize = (int)S(10.5f);
        val.fontSize = (int)S(13); seg.fontSize = (int)S(12); tabSt.fontSize = (int)S(12);
    }
    // colour times the current GUI alpha, so a greyed-out row greys its boxes too
    static void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = new Color(c.r, c.g, c.b, c.a * o.a); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }

    // a hand-tested button: true on the frame it is tapped
    bool Btn(Rect r, int id, string text, GUIStyle st, Color bg, bool live = true)
    {
        var sr = OnScreen(r);
        bool held = live && mode == Pending && pressId == id;
        Fill(r, held ? new Color(Mathf.Min(1, bg.r + 0.18f), Mathf.Min(1, bg.g + 0.18f), Mathf.Min(1, bg.b + 0.18f), Mathf.Min(1, bg.a + 0.18f)) : bg);
        if (text != null) GUI.Label(r, text, st);
        if (!live) return false;
        if (evt == EvDown && Hit(sr)) pressId = id;
        return evt == EvUp && clickId == id && Hit(sr);
    }

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
        Styles();
        var e = Event.current; evt = EventKind(e); mp = e.mousePosition;
        float sw = Screen.width, sh = Screen.height, m = S(4);
        origin = Vector2.zero; clip = new Rect(0, 0, sw, sh);
        bool close = false;

        // ---- layout
        var safe = Screen.safeArea; // keeps clear of a notch at the short edges
        float left = Mathf.Max(m, safe.xMin), right = Mathf.Min(sw - m, safe.xMax);
        float pw = Mathf.Min(right - left, S(900)), px = left + (right - left - pw) / 2;
        var panel = new Rect(px, m, pw, sh - 2 * m);
        float pad = S(10), headH = S(46), bodyY = panel.y + headH, bodyH = panel.yMax - S(6) - bodyY;
        float tabW = Mathf.Clamp(pw * 0.2f, S(104), S(160)), tabGap = S(3);
        float tabH = Mathf.Min(S(48), (bodyH - (tabs.Count - 1) * tabGap) / tabs.Count);
        float cx = panel.x + tabW + pad * 1.5f, cw = panel.xMax - pad - cx;
        float footH = S(44), listH = bodyH - footH - S(6);
        var view = new Rect(cx, bodyY, cw, listH);
        float lw = Mathf.Min(cw * 0.42f, S(300)), ctlW = cw - lw - S(8);
        float rowH = lw < S(220) ? S(70) : S(58); // a narrow label column (4:3 screens): room for three lines of help
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

        // ---- frame
        Fill(new Rect(0, 0, sw, sh), new Color(0, 0, 0, 0.62f));
        Fill(panel, new Color(MenuUI.PanelCol.r, MenuUI.PanelCol.g, MenuUI.PanelCol.b, 0.96f));
        Fill(new Rect(panel.x, panel.y, panel.width, S(3)), Accent);
        GUI.Label(new Rect(panel.x + pad, panel.y + S(3), S(140), headH - S(3)), "SETTINGS", ttl);
        if (!string.IsNullOrEmpty(subtitle)) GUI.Label(new Rect(panel.x + pad + S(128), panel.y + S(5), pw * 0.4f, headH - S(3)), subtitle.ToUpperInvariant(), cap);
        seg.fontSize = (int)S(14);
        if (Btn(new Rect(panel.xMax - pad - S(110), panel.y + S(4), S(110), headH - S(6)), IdClose, "CLOSE", seg, Accent)) close = true;
        seg.fontSize = (int)S(12);

        // ---- tabs (a column on the left)
        tabSt.fontSize = (int)Mathf.Min(S(12), tabW / 9.2f);
        for (int i = 0; i < tabs.Count; i++)
        {
            var r = new Rect(panel.x + pad * 0.5f, bodyY + i * (tabH + tabGap), tabW, tabH);
            bool on = i == tab;
            if (Btn(r, IdTab + i, null, tabSt, on ? new Color(1, 1, 1, 0.16f) : new Color(1, 1, 1, 0.05f)) && !on) { tab = i; resetArmedTab = -1; }
            if (on) Fill(new Rect(r.x, r.y, S(4), r.height), Accent);
            var oc = GUI.color; GUI.color = new Color(1, 1, 1, on ? 1f : 0.7f);
            GUI.Label(new Rect(r.x + S(12), r.y, r.width - S(14), r.height), tabs[i].name, tabSt);
            GUI.color = oc;
        }

        // ---- rows (a scrolling list)
        GUI.BeginGroup(view);
        origin = view.position; clip = view;
        int row = 0;
        for (int i = 0; i < cur.items.Count; i++)
        {
            var it = cur.items[i];
            if (it.hidden != null && it.hidden()) continue;
            float y = row * rowH - scroll[tab]; row++;
            if (y + rowH < 0 || y > listH) continue;
            string why = it.locked != null ? it.locked() : null;
            bool live = why == null;
            int id = tab * 10000 + i * 20;
            Fill(new Rect(0, y + rowH - S(1), cw, S(1)), new Color(1, 1, 1, 0.07f));
            var oc = GUI.color; if (!live) GUI.color = new Color(1, 1, 1, 0.38f);
            GUI.Label(new Rect(0, y + S(4), lw - S(8), S(19)), it.label, lab);
            GUI.color = oc;
            GUI.Label(new Rect(0, y + S(23), lw - S(8), rowH - S(25)), live ? it.help : why, hlp);
            if (!live) GUI.color = new Color(1, 1, 1, 0.38f);
            var cr = new Rect(lw, y + (rowH - S(44)) / 2, ctlW, S(44));
            if (it.kind == Kind.Slider) DrawSlider(it, cr, id, live);
            else if (it.kind == Kind.Choice) DrawChoice(it, cr, id, live);
            else if (Btn(new Rect(cr.x, cr.y, Mathf.Min(cr.width, S(280)), cr.height), id, it.actText != null ? it.actText() : it.label, seg, new Color(1, 1, 1, 0.14f), live)) { it.act?.Invoke(); dirty = true; }
            GUI.color = oc;
        }
        if (maxScroll > 0)
        {
            float bar = listH * listH / (shown * rowH);
            Fill(new Rect(cw - S(3), 0, S(3), listH), new Color(1, 1, 1, 0.06f));
            Fill(new Rect(cw - S(3), (listH - bar) * scroll[tab] / maxScroll, S(3), bar), new Color(1, 1, 1, 0.4f));
        }
        GUI.EndGroup();
        origin = Vector2.zero; clip = new Rect(0, 0, sw, sh);

        // ---- footer: hint and "Reset tab to defaults"
        float fy = panel.yMax - S(6) - footH, now = Time.realtimeSinceStartup, rbw = Mathf.Min(cw, S(230));
        Fill(new Rect(cx, fy - S(3), cw, S(1)), new Color(1, 1, 1, 0.12f));
        if (cw - rbw > S(120)) GUI.Label(new Rect(cx, fy, cw - rbw - S(8), footH), maxScroll <= 0 ? "Changes apply at once" : cw - rbw < S(280) ? "Drag the list to scroll" : "Drag the list to scroll  ·  changes apply at once", cap);
        bool armed = resetArmedTab == tab && now - resetArmedT < 3f, done = now - resetDoneT < 1.5f;
        if (Btn(new Rect(cx + cw - rbw, fy, rbw, footH), IdReset, done ? "DEFAULTS RESTORED" : armed ? "TAP AGAIN TO RESET" : "RESET TAB TO DEFAULTS", seg, armed ? Accent : new Color(1, 1, 1, 0.12f)))
        {
            if (!armed) { resetArmedTab = tab; resetArmedT = now; resetDoneT = -10; }
            else
            {
                foreach (var it in cur.items) if (it.resettable && it.kind != Kind.Action) Set(it, it.def);
                resetArmedTab = -1; resetDoneT = now; dirty = true;
            }
        }

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

    void DrawSlider(Item it, Rect r, int id, bool live)
    {
        float v = it.get(), vw = S(84);
        var box = new Rect(r.xMax - vw, r.y + S(4), vw, r.height - S(8));
        Fill(box, new Color(1, 1, 1, 0.06f));
        GUI.Label(box, it.fmt != null ? it.fmt(v) : v.ToString("0.##"), val);
        var ta = new Rect(r.x, r.y, r.width - vw - S(6), r.height);
        if (ta.width > S(250))
        {
            float b = S(38), by = ta.y + (ta.height - b) / 2;
            seg.fontSize = (int)S(18);
            if (Btn(new Rect(ta.x, by, b, b), id + 1, "-", seg, new Color(1, 1, 1, 0.10f), live)) Set(it, v - it.step);
            if (Btn(new Rect(ta.xMax - b, by, b, b), id + 2, "+", seg, new Color(1, 1, 1, 0.10f), live)) Set(it, v + it.step);
            seg.fontSize = (int)S(12);
            ta = new Rect(ta.x + b + S(4), ta.y, ta.width - 2 * b - S(8), ta.height);
        }
        float x0 = ta.x + S(11), w = Mathf.Max(1, ta.width - S(22)), cy = ta.y + ta.height / 2;
        float t = Mathf.InverseLerp(it.min, it.max, v);
        bool active = live && mode == SliderDrag && pressId == id;
        Fill(new Rect(x0, cy - S(3), w, S(6)), new Color(1, 1, 1, 0.16f));
        Fill(new Rect(x0, cy - S(3), w * t, S(6)), Accent);
        if (it.resettable) Fill(new Rect(x0 + w * Mathf.InverseLerp(it.min, it.max, it.def) - S(1), cy - S(9), S(2), S(18)), new Color(1, 1, 1, 0.35f)); // the default
        Fill(new Rect(x0 + w * t - S(9), cy - S(15), S(18), S(30)), active ? new Color(1f, 0.8f, 0.78f) : Color.white);
        if (!live) return;
        var hit = OnScreen(ta);
        if (evt == EvDown && Hit(hit)) { pressId = id; pressSlider = true; }
        bool tap = evt == EvUp && clickId == id && Hit(hit);
        if ((active && evt == EvDrag) || tap) Set(it, it.min + Mathf.Clamp01((mp.x - (origin.x + x0)) / w) * (it.max - it.min));
    }

    void DrawChoice(Item it, Rect r, int id, bool live)
    {
        int n = it.options.Length, sel = Mathf.Clamp(Mathf.RoundToInt(it.get()), 0, n - 1);
        float gap = S(3), sw = (r.width - (n - 1) * gap) / n;
        bool fits = n <= 5;
        for (int k = 0; k < n && fits; k++) { tmp.text = it.options[k]; if (seg.CalcSize(tmp).x + S(8) > sw) fits = false; }
        if (fits)
        {
            for (int k = 0; k < n; k++)
                if (Btn(new Rect(r.x + k * (sw + gap), r.y, sw, r.height), id + 1 + k, it.options[k], seg, k == sel ? Accent : new Color(1, 1, 1, 0.10f), live) && k != sel) Set(it, k);
            return;
        }
        // too many or too long for segments: "< value >"
        float b = r.height;
        seg.fontSize = (int)S(20);
        if (Btn(new Rect(r.x, r.y, b, b), id + 1, "<", seg, new Color(1, 1, 1, 0.10f), live)) Set(it, (sel - 1 + n) % n);
        if (Btn(new Rect(r.xMax - b, r.y, b, b), id + 2, ">", seg, new Color(1, 1, 1, 0.10f), live)) Set(it, (sel + 1) % n);
        seg.fontSize = (int)S(12);
        var mid = new Rect(r.x + b + gap, r.y, r.width - 2 * b - 2 * gap, b);
        Fill(mid, new Color(1, 1, 1, 0.05f));
        GUI.Label(mid, it.options[sel], val);
    }
}
