// GameManagerLookBack.cs — LOOK BACK (like GT / GRID): hold the button on the left of the screen to see behind, let go
// to look ahead again. The switch is instant both ways (no blend, no smoothing).
//  * chase / far chase: the same camera mirrored to ahead of the car (6.2 / 10 m ahead, 2 / 3.4 m up) looking back at
//    the car's nose and the cars behind
//  * cockpit / halo: a rear-facing camera on the car, just above the airbox (like an onboard rear camera): the engine
//    cover and rear wing at the bottom, the cars behind above them. Near plane = the halo cam's (0.12 m)
//  * TV camera and replays: no rear view (the button is hidden)
//  * Tilt lock (horizon) is applied to the rear view the same way as to the front view
// The button reads the touches itself (like BOOST, RaceHud.cs: ErsButtonsInput), so it works while the other thumb is
// on the gas / brake or steering; the finger that pressed it is never taken as a steering finger (ReadInput skips it).
// Where it sits (RaceHud.cs layout units, 390 per screen height):
//  * tilt steering: above OVERTAKE / BOOST (which sit right of the brake), centred on the pair
//  * touch (drag) steering: left half, right of the timing tower (and the qualifying list), at mid height, above the
//    steering thumb; a touch that starts on it looks back instead of steering
// Setting: CAMERA > "Look back button" (PlayerPrefs "lookBackBtn", default shown). Key: E (desktop / WebGL).
// Screenshots (DebugShots.cs): lookback=1 holds it in every shot; map=radar|track|off, tilt=1 (tilt layout),
// side=<m> (rival 0 alongside the player, m to the left of the racing line; negative = right).
using System.Collections.Generic;
using UnityEngine;

public partial class GameManager
{
    const string LookBackKey = "lookBackBtn";
    int lookBtnCache = -1;
    internal bool LookBackBtn
    {
        get { if (lookBtnCache < 0) lookBtnCache = PlayerPrefs.GetInt(LookBackKey, 1) == 1 ? 1 : 0; return lookBtnCache == 1; }
        set { lookBtnCache = value ? 1 : 0; PlayerPrefs.SetInt(LookBackKey, lookBtnCache); }
    }
    int lookFinger = -1;    // the finger holding LOOK BACK (ReadInput skips it)
    bool lookBack;          // the rear view is on this frame
    bool shotLookBack;      // screenshot mode: lookback=1
    float shotSide;         // screenshot mode: side=<m>, rival 0 alongside (0 = off)

    bool LookBackAllowed => car != null && model != null && !replaying && !menuOpen && (ui == null || !ui.Showing) && camMode != TvMode && post == null;
    bool LookBackShown => LookBackAllowed && LookBackBtn && form == null && !ShotsHideHud;

    Rect LookBackRect
    {
        get
        {
            float d = S(40);
            if (tilt)
            {
                var o = OvertakeRect; // tilt: OVERTAKE right of the brake, BOOST S(8) right of it
                float cx = o.xMax + S(4);
                return new Rect(Mathf.Round(cx - d * 0.5f), Mathf.Round(o.y - S(14) - d), d, d);
            }
            // touch steering: the race tower ends at hudL + S(136), the qualifying list at hudL + S(150)
            return new Rect(Mathf.Round(UiKit.Safe.x + S(166)), Mathf.Round(Screen.height * 0.5f - d * 0.5f), d, d);
        }
    }

    // once a frame, before ReadInput (Update)
    void LookBackInput()
    {
        bool held = false;
        if (LookBackShown && !Paused)
        {
            var b = LookBackRect; float m = S(4);
            var hit = new Rect(b.x - m, b.y - m, b.width + 2 * m, b.height + 2 * m);
            bool seen = false;
            for (int ti = 0, tn = Input.touchCount; ti < tn; ti++) // GetTouch: Input.touches allocates
            {
                var t = Input.GetTouch(ti);
                var p = new Vector2(t.position.x, Screen.height - t.position.y);
                if (t.phase == TouchPhase.Began)
                {
                    if (hit.Contains(p)) lookFinger = t.fingerId;
                    else if (t.fingerId == lookFinger) lookFinger = -1; // the id was reused by a new touch elsewhere
                }
                if (t.fingerId != lookFinger) continue;
                seen = true; // (kept through its Ended frame, so ReadInput never takes it for steering)
                if (t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled) held = true;
            }
            if (!seen) lookFinger = -1;
            if (Input.touchCount == 0 && Input.GetMouseButton(0) && hit.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y))) held = true;
        }
        else lookFinger = -1;
        if (LookBackAllowed && (Input.GetKey(KeyCode.E) || shotLookBack)) held = true;
        lookBack = held;
    }

    // LateUpdate, after the front view is set: the rear view instead, while held
    void LookBackCamera(Transform tr)
    {
        if (!lookBack || !LookBackAllowed) return;
        Vector3 up = tr.up, pos, dir; float near;
        if (camMode == 2 || camMode == 3)
        {
            pos = tr.TransformPoint(new Vector3(0f, 1.45f, -0.15f));
            dir = -tr.forward * 0.99756f - up * 0.06976f; // straight back, 4 deg down
            near = HaloNear;
        }
        else
        {
            bool far = camMode == 1;
            float ahead = far ? 10f : 6.2f, height = far ? 3.4f : 2f;
            Vector3 f = chaseDir.sqrMagnitude > 0.5f ? chaseDir : tr.forward, basePos = tr.position;
            pos = basePos + up * height + f * ahead;
            dir = basePos + up * 0.7f - f * 6f - pos;
            near = ChaseNear;
        }
        cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir, Vector3.up));
        if (horizonMode == 2 || (horizonMode == 1 && (camMode == 2 || camMode == 3))) cam.transform.Rotate(0, 0, -lockRoll, Space.Self); // lockRoll: read this frame by the front view
        cam.nearClipPlane = near;
    }

    // OnGUI: the button (glass disc like BOOST; cyan while held)
    void DrawLookBack()
    {
        if (!LookBackShown) return;
        var r = LookBackRect;
        bool on = lookBack;
        var b = UiKit.Scale(r, on ? 0.93f : 1f);
        if (on) UiKit.Rect(UiKit.Scale(b, 1.16f), new Color(HudCyan.r, HudCyan.g, HudCyan.b, 0.22f), 999f);
        Disc(b, on ? new Color(HudCyan.r * 0.85f, HudCyan.g * 0.85f, HudCyan.b * 0.85f, 0.92f) : new Color(0.05f, 0.06f, 0.08f, 0.66f));
        UiKit.Outline(b, on ? new Color(1f, 1f, 1f, 0.5f) : new Color(1f, 1f, 1f, 0.16f), 999f, 0.8f);
        Color tc = on ? new Color(0.03f, 0.04f, 0.06f) : Color.white;
        // a small mirror above the words
        float s = b.width / S(40);
        UiKit.Outline(new Rect(b.center.x - S(8) * s, b.y + S(8) * s, S(16) * s, S(6) * s), tc, 2.5f, 0.9f);
        TF(new Rect(b.x, b.center.y - S(3) * s, b.width, S(9) * s), "LOOK", UiKit.Bold, 6.4f, tc, TextAnchor.MiddleCenter);
        TF(new Rect(b.x, b.center.y + S(5) * s, b.width, S(8) * s), "BACK", UiKit.Semi, 5.6f, on ? tc : HudGrey, TextAnchor.MiddleCenter);
    }

    // ---------------------------------------------------------------- screenshots (DebugShots.cs)
    // ShotsBoot: lookback=1, map=radar|track|off (not saved), tilt=1, side=<m>
    void ShotsHudParams(Dictionary<string, string> q)
    {
        shotLookBack = DebugShots.Get(q, "lookback", "0") == "1";
        string m = DebugShots.Get(q, "map", "").ToLowerInvariant();
        if (m == "radar") mapModeCache = 0; else if (m == "track") mapModeCache = 1; else if (m == "off") mapModeCache = 2;
        if (DebugShots.Get(q, "tilt", "0") == "1") tilt = true;
        shotSide = DebugShots.GetF(q, "side", 0f);
    }
    // ShotPlace, after the rivals are spread: rival 0 level with the player (1.5 m ahead), shotSide m off the racing line
    void ShotsAlongside(int i, double hw)
    {
        if (shotSide == 0f || rivals.Count == 0 || track == null || line == null) return;
        int N = track.N, j = (i + Mathf.Max(1, (int)System.Math.Round(1.5 / track.ds))) % N;
        double rl = System.Math.Max(-hw, System.Math.Min(hw, line.off[j] + shotSide));
        rivals[0].Place(j, rl, car.vx);
        rivals[0].car.gridHold = false;
    }
}
