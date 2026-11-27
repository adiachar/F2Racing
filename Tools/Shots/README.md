# Tools/Shots — automatic in-game screenshots (WebGL, headless)

Takes in-game frames from the WebGL build without a phone, for side-by-side comparison with the Real Racing 3
reference frames (`Docs/reference/`).

How it works:

- **Game side**: `Assets/Scripts/Game/DebugShots.cs` and `Assets/Plugins/WebGL/Shots.jslib`. The game only enters
  shot mode in a **WebGL player build** whose page URL has `?shot=1`. The URL is never read on Android or in the
  editor, and the `.jslib` is WebGL-only. It skips the menu, builds the circuit, and starts a quick race with N
  rivals. The simulation stays frozen. For each lap distance `s` and each camera it puts the player on the racing
  line, pointed along the line, and spreads the rivals ahead of and behind it. It then waits for the picture to
  settle and sets `document.title = "SHOT_READY <i> <count> s=<m> cam=<name>"`. The harness takes the screenshot and
  sets `window.__shotAck = i`, and the game moves on.
- **Harness**: `shoot.py`. It downloads the newest `F2Racing-WebGL.zip` from the GitHub release `latest`, or uses
  `--build`. It serves the build on `127.0.0.1` and opens it in headless Chrome (SwiftShader), then saves one PNG
  per READY frame, plus `contact_sheet.png`, `console.log` (browser console, page errors, HTTP log) and `shots.json`.

## Use

```bash
# defaults: Monza, 6 rivals, High, chase cam, the 6 Monza spots below, 1280x720, afternoon, dry
python3 Tools/Shots/shoot.py --out /tmp/shots

# several cameras (one shot per spot per camera), other spots / quality / light
python3 Tools/Shots/shoot.py --cam chase,cockpit,tv --s 300,850,1450 --quality Medium --tod evening --out /tmp/shots

# extra game parameters: HUD on, Formula 2, a speed for wheels and spray (physics stays frozen)
python3 Tools/Shots/shoot.py --param hud=1 --param class=F2 --param kmh=250 --weather wet

# a local build (folder with index.html or the zip), or just the start menu (works with any build)
python3 Tools/Shots/shoot.py --build ~/Downloads/F2Racing-WebGL.zip --menu --out /tmp/menu
```

Presets (`--preset`, gap list item 26): spot sets for features the default run never puts in frame. A preset fills
in whichever of `--track/--tod/--cam/--s` are not on the command line and adds its `--param`s; anything on the command line wins.

| preset | what | spots |
|---|---|---|
| `sun` | Monza afternoon, facing the sun (glare, flare, haze); the sun is 48 deg up, so the disc is above the chase frame | chase+cockpit, s 2620, 4411, 4881 |
| `sun-evening` | Monza evening, straight into the low sun (13 deg): disc, lens flare and glare in frame | chase+cockpit, s 2910, 3210 |
| `sparks` | plank sparks at 320 km/h on the main straight | chase+far, s 300, 320, 340, `kmh=320` |
| `lod` | LOD sweep: chase every 15 m from 150 to 465 m; flip through the frames for popping trees / stands / boards | chase |

The sun spots come from the Physics track tangents (150 m ahead, nearly straight runs) against the sun directions
in `Sky.cs` (`SunAt`) and `WorldBuilder.cs` (afternoon `Euler(48, -35)`). Other circuits (Track tangent . sun):
Spa afternoon s 1310 / 830, evening 2730; Suzuka afternoon 410 / 20, evening 3200 / 2080. Note: the sparks come
from `car.bottoming` (physics), which stays false while physics is frozen, so `sparks` shows them only once shot
mode can force the plank down (a `DebugShots.cs` / `CarModel.cs` change, not in this tool).

Requirements: python3 with `playwright` and `Pillow` (without Pillow it falls back to ffmpeg, with no labels),
and Chrome at `/usr/bin/google-chrome` (`--chrome` to change it). The download needs `gh` (it uses `gh api`, the
REST API) or plain HTTPS access to github.com. Builds are cached in `~/.cache/f2racing-shots/<build time>/`.

Exit codes: 0 ok · 1 the game reported `SHOT_ERROR` or a shot timed out · 2 the build has no shot mode. With
exit code 2 the build predates `DebugShots.cs`; whatever is on screen is saved as `menu.png`.

By hand in a desktop browser (no harness):
`index.html?shot=1&track=Monza&cars=6&quality=High&cam=chase,tv&s=300,850&w=1280&h=720&tod=afternoon`.
Without `ack=1` each frame holds for `hold` seconds (default 1.5). Watch the tab title.

## URL parameters (DebugShots.cs)

| param | values | default |
|---|---|---|
| `shot` | `1` turns shot mode on | off |
| `track` | circuit name or id (`Monza`, `Spa`, `Monaco`, …) | Monza |
| `cars` | rivals placed around the player, 0-21 | 6 |
| `quality` | `Low`, `Medium`, `High`, `Auto` | High |
| `cam` | list of `chase`, `far`, `cockpit`, `halo`, `tv` | chase |
| `s` | list of lap distances in m (from the start/finish line) | 300,850,1450,2500,3920,5150 |
| `w`, `h` | canvas size in CSS px (device pixel ratio forced to 1) | 1280, 720 |
| `tod` | `morning`, `afternoon`, `evening`, `night` (night: street circuits only) | afternoon |
| `weather` | `dry`, `damp`, `wet` | dry |
| `class` | `F1`, `F2`, `F3` | F1 |
| `kmh` | speed shown (wheels, spray); physics is frozen | 0 |
| `hud` | `1` draws the race HUD | 0 |
| `intro` | list of times (s) into the pre-race intro (`PreRace.cs`), one frozen frame each, instead of `s`; e.g. `--param intro=1.5,4,6,8.5,11,13.5,17` | off |
| `settle` / `warm` | seconds to wait after each placement / before the first shot | 1 / 3 (harness: 2 / 5) |
| `ack` / `acktimeout` | `1` = wait for `window.__shotAck = i` (harness sets this) / max wait s | 0 / 300 |
| `hold` | seconds per frame without `ack` | 1.5 |
| `autofill` | `0` = build without the generated scenery fill (`Resources/MapEdits/Auto`, `Tools/Scenery`) for A/B | 1 |

Lists accept `,` `|` or `;`. Rival slots (m ahead, m sideways from the racing line): +18, +34, −14, +55, +78,
−32, +105, +135, +170, −55, then every 40 m further ahead. The chase and cockpit cameras see the cars ahead,
and the TV cameras also catch the ones behind.

## Monza spots (checked against the game's track: length 5791 m)

These come from `Circuit.cs` (corner notes, `realCorners`) and from the curvature and racing-line speed of
`Circuits.Monza.Build()` (F1 class), dumped with the Physics sources. The spots are corner entries, so that the
chase camera looks into the bend.

| s (m) | spot | what the track data says |
|---|---|---|
| 300 | main straight | straight, ~316 km/h |
| 850 | Rettifilo entry | braking from 325 km/h at ~800 m; chicane apexes 912 / 960 m (R 40 m) |
| 1450 | Curva Grande | long right-hander from 1250 to 1780 m (R 200-370 m), ~278 km/h |
| 2500 | Lesmo 1 | turn-in at ~2450 m, apex ~2540 m (R 78 m), ~156 km/h |
| 3920 | Ascari | braking from ~3840 m; left kink at 3936 m, then 3984-4130 m |
| 5150 | Parabolica | turn-in at ~5040 m, tightest at 5137-5233 m (R 84 m), exits by ~5450 m |

The first suggestions (Rettifilo ~900, Curva Grande ~1700, Lesmo 1 ~2600, Ascari ~4300, Parabolica ~5300) fall
at or past the apex in this track model, and 4300 m is already on the straight after Ascari. Use them through
`--s` if you want exit views. Roggia (2112-2160 m) and Lesmo 2 (2832-2880 m) are also good spots.

## Scenery fill spots (Tools/Scenery, Resources/MapEdits/Auto)

Where the generated fill (grandstands, hospitality, kiosks, sheds, farms, tree lines, woods) is easiest to see. Each
`s` is 70 m before one of the biggest near-track clusters, so the chase camera looks into it; the TV cameras show it
from the side. Shoot each pair with and without the fill (`--param autofill=0`) and compare. Regenerate the list with
`cd Tools/Scenery && dotnet run -c Release` (the `shots:` lines of the summary).

```
python3 Tools/Shots/shoot.py --out /tmp/fill --track silverstone --cam chase,tv --s 4130,4450,4610,5130
python3 Tools/Shots/shoot.py --out /tmp/fill0 --track silverstone --cam chase,tv --s 4130,4450,4610,5130 --param autofill=0
```

| circuit | `--s` (chase, tv) | fill on the |
|---|---|---|
| silverstone | 4130,4450,4610,5130 | right / right / right / left |
| austin | 130,1770,3330,4650 | right / left / right / left |
| budapest | 1490,2650,3650,4010 | right / left / right / left |
| catalunya | 90,530,3010,4410 | left / right / right / left |
| zandvoort | 890,1530,2170,3530 | left (all) |
| suzuka | 1810,1970,3050,3530 | right / right / left / left |
| spielberg | 770,850,1490,1890 | right (all) |
| spa | 210 (farms and woods further out: `--cam tv`) | right |

Monza (real map data) and Monaco (street circuit) get no fill.

## Server notes

- Unity 6 names compressed files `*.unityweb` here (Gzip, with the loader's decompression fallback turned on in
  `BuildScript.cs`). The server reads each file's header: gzip files get `Content-Encoding: gzip`, and the
  Content-Type comes from the inner name (`.wasm` → `application/wasm`, `.js`, `.data`). `.gz` / `.br`
  extensions work too. Brotli is sent with `Content-Encoding: br` only when the browser asks for br. Otherwise
  it goes out raw and the loader's JS fallback decodes it. `--raw` never sends Content-Encoding, to test that
  fallback.
- The harness forces the canvas to `w`×`h` at (0,0), hides the template footer, and screenshots that clip.
  The game also sizes the canvas itself (`ShotsCanvas`).
- SwiftShader is slow. The engine takes seconds to start, but a circuit build at High can take minutes. The
  timeouts are generous (`--load-timeout 1200`, `--shot-timeout 600`).
- Known console noise in the current builds: about 80 `Can't add component because class 'BoxCollider' /
  'CapsuleCollider' doesn't exist!` errors (code stripping removed the physics module in WebGL; harmless).
  There is also a `screen.orientation.lock()` error and a streamed-audio-clip warning. The harness counts
  console errors in `shots.json`. It prints each distinct error twice at most; the full list is in
  `console.log`.
