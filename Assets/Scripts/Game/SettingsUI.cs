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
// Look: the front end's "Paper" page (UiKit.cs), from the menu and in a paused race alike: the masthead (in the menu
// with the nav, SETTINGS underlined, PLAY / DRIVER / the wordmark leave through UiKit.NavRequest; in a race the wordmark
// and "GAME PAUSED"), tabs as a row of text tabs under it (the chosen one ExtraBold with a red underline that slides;
// two rows on a narrow screen), rows as label / help on the left and the control on the right over quiet rules: sliders with an ink fill, choices as text options with a
// red underline under the chosen one, "‹ value ›" steppers, outlined action buttons; under the bottom rule the
// "Reset tab to defaults" link and the black DONE (RESUME in a race) button.
// Layout units: 390 units = screen height. Touch targets are 44 units (the tabs' 30 when they need two rows on a 4:3
// tablet), so the same layout fits a 19.5:9 phone (844 units wide) and a 4:3 tablet (520 units wide).
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

    readonly float[] tabX, tabY, tabW;   // the tab row's layout, measured each draw (no allocation)
    public SettingsUI(List<Tab> tabs, Action commit)
    {
        this.tabs = tabs; this.commit = commit; scroll = new float[tabs.Count];
        tabX = new float[tabs.Count]; tabY = new float[tabs.Count]; tabW = new float[tabs.Count];
    }

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
    const float LabelSize = 13.5f, HelpSize = 10f, SegSize = 11.5f, TabSize = 11.5f;

    // a hand-tested button: true on the frame it is tapped. Draws by look: LookGlass = an outlined ink rectangle (a
    // secondary action), LookAccent = the black primary (tracked caps, the red square), LookGhost = a text link,
    // LookArmed = ink text over a red underline (confirm), LookNone = only the press tint. Text centred.
    bool Btn(Rect r, int id, string text, int look, bool live = true, float size = SegSize, float radius = 0f)
    {
        var sr = OnScreen(r);
        bool held = live && mode == Pending && pressId == id && Hit(sr);
        float p = UiKit.Smooth(id ^ 0x3A000000, held ? 1f : 0f, 26f);
        Color tc = Color.Lerp(UiKit.Ink, UiKit.Red, p);
        switch (look)
        {
            case LookGlass:
                if (p > 0.01f) UiKit.Rect(r, UiKit.A(UiKit.PaperTint, p));
                UiKit.Outline(r, UiKit.Ink, 0f, 0.7f);
                break;
            case LookAccent:
            {
                UiKit.Rect(r, Color.Lerp(UiKit.Ink, UiKit.InkPressed, p));
                if (text != null)
                {
                    string cs = UiKit.Caps(text); float mark = Mathf.Round(S(9f)), gap = S(12);
                    float ts = UiKit.Fit(cs, UiKit.Heavy, size, r.width - S(32) - mark - gap, size * 0.6f), tw = UiKit.Width(cs, UiKit.Heavy, ts);
                    float x = r.center.x - (tw + gap + mark) * 0.5f;
                    UiKit.Text(new Rect(x, r.y, tw + S(2), r.height), cs, UiKit.Heavy, ts, Color.white);
                    UiKit.Rect(new Rect(Mathf.Round(x + tw + gap), Mathf.Round(r.center.y - mark * 0.5f), mark, mark), UiKit.Red);
                    text = null;
                }
                break;
            }
            case LookGhost:
                break;
            case LookArmed:
                UiKit.Rect(new Rect(r.x, r.yMax - Mathf.Max(2f, Mathf.Round(S(2f))), r.width, Mathf.Max(2f, Mathf.Round(S(2f)))), UiKit.Red);
                break;
            default:
                if (p > 0.01f) UiKit.Rect(r, UiKit.A(UiKit.PaperTint, p));
                break;
        }
        if (text != null) UiKit.Text(r, text, UiKit.Heavy, size, tc, TextAnchor.MiddleCenter);
        if (!live) return false;
        if (evt == EvDown && Hit(sr)) pressId = id;
        return evt == EvUp && clickId == id && Hit(sr);
    }
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
        float sw = Screen.width, sh = Screen.height;
        origin = Vector2.zero; clip = new Rect(0, 0, sw, sh);
        bool close = false;

        // ---- layout: the paper page frame; a row of text tabs under the masthead (two on a narrow screen), the rows
        bool inRace = !string.IsNullOrEmpty(subtitle);
        var f = UiKit.PaperFrame(72f);
        int nt = tabs.Count; float tgap = S(26), total = -tgap;
        for (int i = 0; i < nt; i++) { tabW[i] = UiKit.Width(tabs[i].name, UiKit.Heavy, TabSize); total += tabW[i] + tgap; }
        int perRow = total <= f.w ? nt : (nt + 1) / 2, tabRows = (nt + perRow - 1) / perRow;
        float tabH = tabRows == 1 ? S(44) : S(30), tabsTop = f.head + S(4);
        for (int i = 0, k = 0; i < nt; i++, k++)
        {
            if (k == perRow) k = 0;
            tabX[i] = k == 0 ? f.x0 : tabX[i - 1] + tabW[i - 1] + tgap;
            tabY[i] = tabsTop + (i / perRow) * tabH;
        }
        float bodyY = tabsTop + tabRows * tabH + S(6);
        float cx = f.x0, cw = f.w;
        float listH = f.foot - S(8) - bodyY;
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

        // ---- frame: the paper page and the masthead (nav in the menu; the wordmark and "game paused" in a race)
        UiKit.PaperPage(inRace ? 0.97f : 1f);
        float ent = UiKit.Enter("settings", 0, 0.04f, 0.4f);
        if (!inRace)
        {
            int nav = UiKit.Masthead(f, UiKit.NavSettings);
            if (nav == UiKit.NavPlay || nav == UiKit.NavDriver || nav == UiKit.NavHome) { UiKit.NavRequest = nav; close = true; }
        }
        else UiKit.Masthead(f, UiKit.NavNone, false, subtitle);
        UiKit.PushAlpha(ent);

        // ---- tabs: text, the chosen one ExtraBold ink with a red underline that slides; a quiet rule under the row(s)
        float hy = UiKit.Smooth(KeyTabHl, tab, 16f);
        int h0 = Mathf.Clamp(Mathf.FloorToInt(hy), 0, nt - 1), h1 = Mathf.Min(nt - 1, h0 + 1); float hk = hy - h0;
        float ux = Mathf.Lerp(tabX[h0], tabX[h1], hk), uw = Mathf.Lerp(tabW[h0], tabW[h1], hk), uy = Mathf.Lerp(tabY[h0], tabY[h1], hk) + tabH * 0.5f + S(9);
        UiKit.Rect(new Rect(ux, uy, uw, Mathf.Max(2f, Mathf.Round(S(2f)))), UiKit.Red);
        for (int i = 0; i < nt; i++)
        {
            var r = new Rect(tabX[i] - tgap * 0.5f, tabY[i], tabW[i] + tgap, tabH);
            bool on = i == tab;
            if (Btn(r, IdTab + i, null, LookNone, true, SegSize) && !on) { tab = i; resetArmedTab = -1; UiKit.Mark(KeyTabSwitch); }
            float onK = Mathf.Clamp01(1f - Mathf.Abs(hy - i));
            UiKit.Text(new Rect(tabX[i], tabY[i], tabW[i] + S(4), tabH), tabs[i].name, onK > 0.5f ? UiKit.Heavy : UiKit.Medium, TabSize, Color.Lerp(UiKit.InkSub, UiKit.Ink, onK));
        }
        UiKit.Hair(f.x0, f.x1, bodyY - S(4), UiKit.PaperTrack);

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
            UiKit.Hair(0, cw - S(8), y + rowH - UiKit.HairPx, UiKit.PaperTrack);
            var oc = GUI.color; if (!live) GUI.color = new Color(oc.r, oc.g, oc.b, oc.a * 0.38f);
            UiKit.Text(new Rect(0, y + S(6), lw - S(10), S(19)), it.label, UiKit.Heavy, UiKit.Fit(it.label, UiKit.Heavy, LabelSize, lw - S(10), 10.5f), UiKit.Ink);
            GUI.color = oc;
            UiKit.Text(new Rect(0, y + S(25), lw - S(12), rowH - S(27)), live ? it.help : why, UiKit.Body, HelpSize, live ? UiKit.InkSub : UiKit.BadInk, TextAnchor.UpperLeft, true);
            if (!live) GUI.color = new Color(oc.r, oc.g, oc.b, oc.a * 0.38f);
            var cr = new Rect(lw, y + (rowH - S(44)) / 2, ctlW - S(8), S(44));
            if (it.kind == Kind.Slider) DrawSlider(it, cr, id, live);
            else if (it.kind == Kind.Choice) DrawChoice(it, cr, id, live);
            else if (Btn(new Rect(cr.x, cr.y, Mathf.Min(cr.width, S(280)), cr.height), id, it.actText != null ? it.actText() : it.label, LookGlass, live, 12f)) { it.act?.Invoke(); dirty = true; }
            GUI.color = oc;
            UiKit.PopAlpha();
        }
        if (maxScroll > 0)
        {
            float bar = Mathf.Max(S(20), listH * listH / (shown * rowH)), bw = S(3);
            UiKit.Rect(new Rect(cw - bw, S(4), bw, listH - S(8)), UiKit.PaperTrack);
            UiKit.Rect(new Rect(cw - bw, S(4) + (listH - S(8) - bar) * scroll[tab] / maxScroll, bw, bar), mode == Scrolling ? UiKit.Ink : UiKit.InkSub);
        }
        GUI.EndGroup();
        origin = Vector2.zero; clip = new Rect(0, 0, sw, sh);

        // ---- the action row: "Reset tab to defaults" (two taps) and a hint on the left, the black DONE / RESUME right
        UiKit.Hair(f.x0, f.x1, f.foot);
        float now = Time.realtimeSinceStartup, ah = S(44), ay = f.actY - ah * 0.5f;
        float doneW = Mathf.Max(S(150), UiKit.BlackButtonWidth(inRace ? "Resume" : "Done"));
        if (Btn(new Rect(f.x1 - doneW, f.actY - S(24), doneW, S(48)), IdClose, inRace ? "Resume" : "Done", LookAccent, true, 16f)) close = true;
        bool armed = resetArmedTab == tab && now - resetArmedT < 3f, done = now - resetDoneT < 1.5f;
        string rt = done ? "Defaults restored" : armed ? "Tap again to reset" : "Reset tab to defaults";
        float rbw = UiKit.Width(rt, UiKit.Heavy, 12.5f) + S(8);
        var rr = new Rect(f.x0, ay, rbw, ah);
        if (Btn(rr, IdReset, null, LookNone, true))
        {
            if (!armed) { resetArmedTab = tab; resetArmedT = now; resetDoneT = -10; }
            else
            {
                foreach (var it in cur.items) if (it.resettable && it.kind != Kind.Action) Set(it, it.def);
                resetArmedTab = -1; resetDoneT = now; dirty = true;
            }
        }
        UiKit.Text(new Rect(rr.x, rr.y + S(3), rr.width, S(20)), rt, UiKit.Heavy, 12.5f, UiKit.Ink);
        if (armed) UiKit.Rect(new Rect(rr.x, rr.y + S(21), rbw - S(8), Mathf.Max(2f, Mathf.Round(S(2f)))), UiKit.Red); // armed: a red underline (no small red text)
        UiKit.Text(new Rect(rr.x, rr.y + S(24), S(300), S(18)), armed ? "Every row on this tab goes back to its default" : maxScroll <= 0 ? "Changes apply at once" : "Drag the list to scroll  ·  changes apply at once", UiKit.Body, 11f, UiKit.InkSub);
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

    // a "−" / "+" text button
    bool StepBtn(Rect r, int id, bool plus, bool live)
    {
        bool hit = Btn(r, id, null, LookNone, live, SegSize);
        bool held = live && mode == Pending && pressId == id && Hit(OnScreen(r));
        UiKit.Text(r, plus ? UiKit.Plus : UiKit.Minus, UiKit.Heavy, 20f, held ? UiKit.Red : UiKit.Ink, TextAnchor.MiddleCenter);
        return hit;
    }

    void DrawSlider(Item it, Rect r, int id, bool live)
    {
        float v = it.get(), vw = S(72);
        var box = new Rect(r.xMax - vw, r.y, vw, r.height);
        UiKit.Text(box, it.fmt != null ? it.fmt(v) : v.ToString("0.##"), UiKit.Heavy, 13f, UiKit.Ink, TextAnchor.MiddleRight);
        var ta = new Rect(r.x, r.y, r.width - vw - S(8), r.height);
        if (ta.width > S(250))
        {
            float b = S(44), by = ta.y + (ta.height - b) / 2;
            if (StepBtn(new Rect(ta.x, by, b, b), id + 1, false, live)) Set(it, v - it.step);
            if (StepBtn(new Rect(ta.xMax - b, by, b, b), id + 2, true, live)) Set(it, v + it.step);
            ta = new Rect(ta.x + b + S(4), ta.y, ta.width - 2 * b - S(8), ta.height);
        }
        float x0 = ta.x + S(10), w = Mathf.Max(1, ta.width - S(20)), cy = ta.y + ta.height / 2;
        float t = Mathf.InverseLerp(it.min, it.max, v);
        bool active = live && mode == SliderDrag && pressId == id;
        float th = Mathf.Max(2f, Mathf.Round(S(3f)));
        UiKit.Rect(new Rect(x0, cy - th / 2, w, th), UiKit.PaperTrack);
        if (t > 0.001f) UiKit.Rect(new Rect(x0, cy - th / 2, Mathf.Max(th, w * t), th), UiKit.Ink);
        if (it.resettable) UiKit.Rect(new Rect(x0 + w * Mathf.InverseLerp(it.min, it.max, it.def) - UiKit.HairPx * 0.5f, cy - S(7), UiKit.HairPx, S(14)), UiKit.InkMuted); // the default
        float a = UiKit.Smooth(id ^ 0x2B000000, active ? 1f : 0f, 20f);
        var kc = new Vector2(x0 + w * t, cy);
        UiKit.Dot(kc, 8f + 2f * a, UiKit.Paper);
        UiKit.Dot(kc, 6.5f + 2f * a, Color.Lerp(UiKit.Ink, UiKit.Red, a)); // the knob: red while dragged (the selected state)
        if (!live) return;
        var hit = OnScreen(ta);
        if (evt == EvDown && Hit(hit)) { pressId = id; pressSlider = true; }
        bool tap = evt == EvUp && clickId == id && Hit(hit);
        if ((active && evt == EvDrag) || tap) Set(it, it.min + Mathf.Clamp01((mp.x - (origin.x + x0)) / w) * (it.max - it.min));
    }

    void DrawChoice(Item it, Rect r, int id, bool live)
    {
        int n = it.options.Length, sel = Mathf.Clamp(Mathf.RoundToInt(it.get()), 0, n - 1);
        float sw = r.width / n;
        bool fits = n <= 5;
        for (int k = 0; k < n && fits; k++) if (UiKit.Width(it.options[k], UiKit.Heavy, SegSize) + S(10) > sw) fits = false;
        if (fits)
        {
            // text options on a quiet rule; the chosen one ExtraBold ink with a red underline that slides
            float pos = UiKit.Smooth(id ^ 0x1C000000, sel, 16f), uy = r.center.y + S(11), uh = Mathf.Max(2f, Mathf.Round(S(2f)));
            UiKit.Rect(new Rect(r.x, uy + uh - UiKit.HairPx, r.width, UiKit.HairPx), UiKit.PaperTrack);
            float uw = Mathf.Min(sw - S(12), S(60));
            UiKit.Rect(new Rect(r.x + pos * sw + (sw - uw) * 0.5f, uy, uw, uh), UiKit.Red);
            for (int k = 0; k < n; k++)
            {
                var sr = new Rect(r.x + k * sw, r.y, sw, r.height);
                bool tapped = Btn(sr, id + 1 + k, null, LookNone, live, SegSize);
                float on = Mathf.Clamp01(1f - Mathf.Abs(pos - k));
                UiKit.Text(new Rect(sr.x, sr.y - S(2), sr.width, sr.height), it.options[k], on > 0.5f ? UiKit.Heavy : UiKit.Medium, SegSize, Color.Lerp(UiKit.InkSub, UiKit.Ink, on), TextAnchor.MiddleCenter);
                if (tapped && k != sel) Set(it, k);
            }
            return;
        }
        // too many or too long for options in a row: "‹ value ›" on a quiet rule, position ticks under the value
        float b = r.height;
        var lb = new Rect(r.x, r.y, b, b); var rb = new Rect(r.xMax - b, r.y, b, b);
        bool lh = live && mode == Pending && pressId == id + 1 && Hit(OnScreen(lb)), rh = live && mode == Pending && pressId == id + 2 && Hit(OnScreen(rb));
        if (Btn(lb, id + 1, null, LookNone, live, SegSize)) Set(it, (sel - 1 + n) % n);
        if (Btn(rb, id + 2, null, LookNone, live, SegSize)) Set(it, (sel + 1) % n);
        UiKit.Text(lb, UiKit.Prev, UiKit.Heavy, 22f, lh ? UiKit.Red : UiKit.Ink, TextAnchor.MiddleCenter);
        UiKit.Text(rb, UiKit.Next, UiKit.Heavy, 22f, rh ? UiKit.Red : UiKit.Ink, TextAnchor.MiddleCenter);
        var mid = new Rect(r.x + b, r.y, r.width - 2 * b, b);
        UiKit.Text(new Rect(mid.x, mid.y - S(3), mid.width, mid.height), it.options[sel], UiKit.Heavy, UiKit.Fit(it.options[sel], UiKit.Heavy, 12.5f, mid.width - S(6), 9f), UiKit.Ink, TextAnchor.MiddleCenter);
        UiKit.Hair(r.x, r.xMax, r.yMax - UiKit.HairPx, UiKit.PaperTrack);
        // position ticks under the value (up to 12 choices), the chosen one red
        if (n <= 12)
        {
            float d = S(4), dg = S(3), tw = n * d + (n - 1) * dg, dx = mid.center.x - tw / 2, dy = r.yMax - S(9);
            for (int k = 0; k < n; k++) UiKit.Rect(new Rect(dx + k * (d + dg), dy, d, Mathf.Max(1f, Mathf.Round(S(1.5f)))), k == sel ? UiKit.Red : UiKit.PaperTrack);
        }
    }
}
