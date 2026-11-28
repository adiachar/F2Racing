# F2Racing — complete handover (28 Nov 2026)

Read this first if you are a new Claude session taking over this game. It assumes you know nothing about it.
Then read `/CLAUDE.md` (shared rules, auto-loaded) and `Docs/COORDINATION.md` (requests and done log).

---

## 0. ⚠️ THE PHYSICS IS CRITICAL. DO NOT TOUCH IT.

The car physics took weeks to calibrate against real 2026 F1 data, and the owner (Adi) loves how it drives.

- **Never change the behaviour of `Assets/Scripts/Physics/*`.** That covers `FormulaCar.cs` (car, tyres, aero, ERS, brakes, ABS/TC), `HeightField.cs`, `Track.cs`, `TrackData*.cs`, `Circuit.cs` (tracks, compounds), `RacingLine.cs` and `PitLane.cs`. Do not "improve", "tidy", tune or refactor it.
- **Allowed with care:** the AI's *decisions* in `AiDriver.cs`, `AiRacecraft.cs` and `RaceControl.cs` (strategy, overtaking, VSC, tyre rules). A rival's solo pace must stay identical.
- **Game-side knobs are how features change the feel without touching physics.** These are `CarConfig` values set from the game code, such as:
  - Settings > Downforce, which scales `clA` / `clAX`
  - tyre-wear rules (`TyreRules` in RaceControl.cs)
  - the Setup "Handling test" sliders

  Prefer this route.
- **Before and after any change near the game loop, AI or race control**, run:
  ```
  cd Tools/PhysicsCheck && dotnet run -c Release > /tmp/after.txt
  ```
  Compare the output with a run from before your change, ignoring the "ms" timing numbers. **It must be identical.** The calibration currently gives:
  - AI solo laps: Porto Reale 74.15 s, Ardennes 110.02 s, Parco 84.15 s (15 kg fuel).
  - Ideal laps vs real 2026 pole: −0.7 to −1.3%.
  - "Racing line JS vs C# … MATCH".
- If Adi asks for something that truly needs a physics change, **test it first, explain the numbers, and ask for his explicit OK.** He says "I'm a noob in car physics, check it, don't blindly follow". Example: he felt "too much grip". The test showed the cause was Traction Control, not the physics, and that was the answer.
- **The only physics changes ever approved:**
  - Raised kerbs and the Suzuka bridge height fix (Nov 24).
  - The rear ABS slip limit, so the car no longer spins when braking in a straight line (Nov 25, one line in FormulaCar.cs, approved by Adi).
- PhysicsCheck has diagnostic modes. Use them instead of guessing:
  - `spin`: power oversteer.
  - `brakes`: straight-line braking stability.
  - `corners <circuit>`: corner speeds vs real, `DF=0.6` env for downforce.
  - `tyres <circuit> <mult>` and `tyrefeel`: grip vs temperature, wear vs sliding.
  - `bridge suzuka`.
  - `kerbs`.
  - `race strat`, `race sc`, `vsc`.
  - `traffic scen|race|duel`.

---

## 1. The game and the owner

- **What it is:** a mobile F1-style racing game for Android (and WebGL), aiming to be "the best mobile racing game in the world". The targets are **GRID Autosport handling** and **Real Racing 3 graphics or better**, running smoothly on Adi's **Redmi Note 12 Pro 5G** (Dimensity 1080, Mali-G68 MC4, 2400×1080, 120 Hz, MIUI).
- **Owner:** Adi.
- **How Adi wants to work:**
  - Reply **very short, in simple English (1–2 lines)**.
  - **Always give the APK link after a build:** https://github.com/adiachar/F2Racing/releases/tag/latest
  - **Performance first:** nothing may make a race heavier. No per-frame allocations: no string building, LINQ, closures, `new` arrays, `GetComponent` or `renderer.material` in per-frame code. Cache strings and GUIContent.
  - **Made-up names only:** no real teams, drivers, brands or logos.
  - Save credits: reuse assets, and use parallel workers only for big independent pieces.
- **Security:**
  - Adi's Unity password was once posted in an old chat. Never write it anywhere, and remind him to change it and update the `UNITY_PASSWORD` GitHub secret.
  - Never write secrets.
  - Don't enter passwords or accept licences for him.
- **Two Claude accounts:**
  - The **lead** (this role) owns gameplay, loop, cameras, UI/HUD, AI decisions, audio, frame pacing, CI, people, career and story.
  - The **graphics account** owns the shaders, EnvTex, RoadSurface, WorldBuilder, LightBake, Trackside, Scenery, Sky, CarEnv, CarLod, RenderScale, PitGarage and Landmarks. It works on branch `gfx` and has been idle since 22 Nov.
  - File ownership is in `/CLAUDE.md`. When you edit the other side's file, keep it small and log it in `Docs/COORDINATION.md`.

## 2. Repo, build and tools

- **Repo:** `adiachar/F2Racing`, public, branch `main`. Unity **6000.0.85f2**, built-in render pipeline, linear colour.
- **Everything is built from C# at runtime:** no scenes or prefabs are edited in an editor, and no `.meta` files are committed. `Assets/Editor/BuildScript.cs` sets up the build: Vulkan first, multithreaded rendering, incremental GC, ASTC, instancing material assets, `androidIsGame`. `Assets/Editor/AndroidGameManifest.cs` marks the app as a game.
- **CI:** GitHub Actions + GameCI (`.github/workflows/build.yml`).
  - A push to `main` builds the Android APK and WebGL, and publishes the release **"latest"**.
  - Branch runs (`gh workflow run build.yml -R adiachar/F2Racing --ref <branch>`) publish a release named after the branch.
  - A build takes about 20 minutes, and a new push cancels the running build.
- **After every build:**
  ```
  gh run view <id> -R adiachar/F2Racing --log | grep -iE "error CS|Shader error|Invalid Bytecode"
  ```
  Shader errors do NOT fail the build. A transient "Gradle plugin not found" → `gh run rerun <id> --failed`.
- **Before every push:**
  - `cd Tools/SyntaxCheck && dotnet run -c Release -- ../../Assets` (install with `apt-get install -y dotnet-sdk-8.0`), then delete `bin/` and `obj/` under Tools.
  - **The syntax check does NOT catch name or type errors.** Past build failures came from:
    - CS0136, a local name clashing with a parameter;
    - CS1061/CS0103, wrong scope (for example `Environment.Landmarks` shadows the `Landmarks` class, so write `global::Landmarks`);
    - a missing field in a nested class.
  - Grep every method and field you call.
- **Code gotchas:**
  - WebGL has no threads: use `FormulaSim.Par.For`, never `Parallel.For`.
  - The game has its own `Environment` class, so write `System.Environment`.
  - `var a = 1, b = 2;` is a compile error (CS0819).
  - Check that every Unity API exists in 6000.0.
  - Shaders: never use `step()` or comparisons on `half` (they break on GLES3/Vulkan); use `float`.
- **Screenshots from the WebGL build:**
  ```
  python3 Tools/Shots/shoot.py --out /tmp/s --cam chase,cockpit,halo,tv --s 300,2500 --quality High [--track silverstone] [--w 1600 --h 720] --param hud=1 ...
  ```
  - Useful params: `hud=1`, `side=3` (a rival alongside), `tilt=1`, `map=both|radar|full|off`, `twear=0.4`, `ttemp=60`, `pen=5 peng=1` (penalty ghost), `lookback=1`, `tyrepick=1`, `haloback=100`, `story=prologue t=4,15,22`, `post=…`, `intro=…`, `menu=home|driver`, `dr=…`, `autofill=0`.
  - Each run writes a `contact_sheet.png`. Post-race runs are slow, so use few frames.
  - **Always screenshot new visuals before telling Adi something is done.**
- **Other tools** (`Tools/`):
  - `PhysicsCheck`, `Scenery` (generates `Assets/Resources/MapEdits/Auto/*.json`)
  - `TerrainCheck`, `PerfAudit`
  - `People2` (Blender pipeline: rigged FBX/GLB → `.bytes` people packs)
  - `Voice` (Kokoro TTS team radio), `Photo`, `Models`
  - `render_car_model.py` (renders the car mesh offline)
- **Private assets:** Mixamo-derived files must not be public. `Assets/Resources/People2/mixamo/` is gitignored. CI copies it from the private repo `adiachar/F2Racing-assets` when the secret `ASSETS_TOKEN` exists. **Adi still hasn't created that repo and secret.** Without the pack, people use fallback animation clips.

## 3. Code map (`Assets/Scripts/`)

- `Physics/` (namespace `FormulaSim`): **frozen**, see section 0. `RaceControl.cs` also holds the tyre strategy, `TyreRules` (wear multiplier, temperature/heat factors, slide-wear) and the VSC reference.
- `Game/` (about 90 files; `GameManager` is split into partials):
  - **`GameManager.cs`:** the main loop, cameras (chase, far, cockpit, halo, TV; the halo "helmet in view" setting), input, OnGUI.
  - **Other GameManager partials:**
    - `GameManagerSettings` (all settings rows)
    - `GameManagerPerf` (frame pacing, display mode, LOD twins)
    - `GameManagerStart`, `GameManagerFormation`
    - `GameManagerRaceControl`
    - `GameManagerProgress` (XP, safety rating, driver rating)
    - `GameManagerLookBack`
  - **HUD:** `RaceHud.cs` (dash, pedals, buttons), `Radar.cs` (GT-style radar plus the full track map with no background), `TyrePit.cs` (tyre-wear setting, pit compound chips, start-tyre card, GT-style tyre widget, COLD/HOT cue, scrub wear), `RacingLineView.cs` (live GRID-style line), `FrameLog.cs`.
  - **Race rules:** `PenaltyServe.cs` (GT-style live penalties: real-second timer, autopilot while serving, blinking see-through ghost via `CarGhost.cs` in every camera).
  - **Films:**
    - `PreRace.cs`: garage walk, pit exit, grid, formation lap.
    - `PostRace.cs`: cool-down lap, podium, XP screen.
    - `StoryFilm.cs`: in-engine 3D story scenes for the career.
  - **Career:** `Career.cs`, `CareerDifficulty.cs`, `CareerSession.cs`, `Season.cs`, `SeasonUI.cs`, `Story.cs`.
  - **Progression:** `Progression.cs`, `DriverRating.cs` (GT-style DR E→A+ and SR letters).
  - **Menus:** `UiKit.cs` (the "Paper" style: cream #F3F1EC, ink #111, red #E10600, Archivo font), `MenuUI.cs`, `MenuHome.cs`, `MenuPlay.cs`, `MenuDriver.cs`, `MenuProgress.cs`, `MenuStudio.cs`, `SettingsUI.cs`.
  - **People:** `People.cs` (People2/People3 packs, podium cast), `DriverModel.cs` (in-car driver), `PitCrew.cs`.
  - **Audio:** `TeamRadio.cs`, `EngineAudio.cs`.
  - **World (graphics account):** `WorldBuilder.cs`, `Environment.cs`, `Landmarks.cs`, `MapEdits.cs`, `Trackside.cs`, `Scenery.cs`, `Sky.cs`, `RoadSurface.cs`.
- **Docs:** `CAREER.md` (story), `PERFORMANCE.md`, `PERF-INVESTIGATION.md`, `ARCHITECTURE-PLAN.md` (planned module split, not done), `MAP-EDITOR.md`, `ASSETS.md` (credits), `COORDINATION.md`, `BACKLOG.md` (old).

## 4. What the game has now (build 5e240ba, green)

- **Content:**
  - 10 real-layout circuits with made-up names: Monza "Autodromo del Parco", Monaco "Porto Reale", Spa "Ardennes", Silverstone "Old Airfield", Suzuka "Ise Bay", Austin, Budapest, Catalunya, Spielberg, Zandvoort.
  - Car classes Rookie / Challenger / Premier (Apex World Series).
- **Driving:**
  - 2026 rules: Overtake Mode and Boost buttons (on the left in tilt mode). Rivals use them when attacking the player.
  - ABS / TC / racing-line assists.
  - **Settings > Downforce defaults to "Fun 60%"** for every car (Adi's choice). The real car is 100%.
- **Tyres:**
  - Soft/Medium/Hard/Inter/Wet with F1-like trade-offs.
  - The "Tyre wear" setting (Auto = a Soft lasts about 40% of the race) is fast, F1-like wear.
  - Grip depends on temperature (×2.0 effect).
  - Sliding wears ×8, and overheated tyres wear up to ×4 more (player only).
  - You pick your tyres on the grid before the start, and with chips when you box. The setting is locked mid-race.
  - AI rivals plan 1–2 stop strategies.
- **HUD:**
  - GT-style radar (red arrow = you, blue = rivals) plus a full track map with no background.
  - GT-style tyre widget: bars white for tread left, red from the top for wear.
  - Tyre-temperature boxes, blue → white → red.
  - Smaller pedals and a LOOK BACK hold button.
  - Halo-cam "helmet in view" slider.
- **Race control:**
  - Penalties are served live, GT-style (the car is driven slowly for you, blinking and see-through).
  - VSC delta, SC, track limits, safety rating.
  - "Back to track" keeps tyres, fuel and battery.
- **Progression:** XP/levels, safety rating (S–E), Driver Rating (E, D, C, B, A, A+).
- **Career:** a 10-round season, contracts, goals, difficulty presets (Easy → Master, Custom), and story told with in-engine 3D films (Settings > GAME > Story: Film / Cards / Off).
  - **The story:** the rookie promised his late mentor **Teo Varga** to "bring it home". Teo crashed out of a title 20 years ago when **Viktor Raske** (now a 7-time champion, Argent team) dived up the inside. Viktor's son **Nico Raske** is the rival, **Mia** (Teo's 10-year-old granddaughter) is the biggest fan, and **Lena Brandt** is the team principal.
- **Race weekend presentation:**
  - Pre-race: drone shot, the driver walks to the car, pit exit, grid, formation lap, lights.
  - Post-race film: cool-down lap, podium with champagne, XP / DR reveal.
- **World:**
  - Pit lanes with every team's garage, crews, marshals and fans (Rocketbox people).
  - Raised kerbs at about 40% of the corners.
  - Generated trackside fill (grandstands, hospitality, sheds, trees) on 8 circuits.
- **Performance work:** frame pacing fixes for MIUI (re-asserting 120 Hz, a performance hint, the game category), cached HUD strings, GPU instancing.

## 5. Open items / next steps

1. **New realistic driver** (Adi had another chat make the assets):
   - Received: `driver_base_m.glb` (CC0-derived body, Mixamo bone names) and `helmet_base.glb` (livery UVs, tint mask, 5 skins; only the glb has been received so far, not the skin textures). Staged at `/home/claude/private-assets-staging/DriverBase/`. This path is only on the old machine, so ask Adi to re-upload if needed.
   - The body has only underwear. **Waiting for the race suit:** the brief `RACE-SUIT-BRIEF.md` was given to Adi. Once the suit arrives, build a new driver pack via `Tools/People2` and use it in menus, garage, podium, story films and the cockpit. Add the helmet with team tint and liveries.
2. The story films' framing could be polished further (garage shots); the people look low-quality until the new driver exists.
3. Ask Adi to create the private repo `adiachar/F2Racing-assets` plus the `ASSETS_TOKEN` secret, so the Mixamo animations reach CI.
4. Open graphics requests in `Docs/COORDINATION.md`:
   - PitGarage far crews → Rocketbox engineers.
   - CarLod damaged-rival LOD.
   - TV cams behind fences.
   - Resolution-setting cleanup.
5. Trackside fill: maybe add more detail closer to the track. Monza and Monaco were skipped because they are already full.
6. Later: the code restructure (`ARCHITECTURE-PLAN.md`), URP (paused on branch `urp`), real engine sounds, multiplayer (later; Photon free tier), replacing the SF-26-replica car model before release (its CC-BY credit names a real brand), renaming the app and repo.

## 6. Working rhythm that worked

- At the start of a session: `git fetch && git rebase origin/main`, then re-read `CLAUDE.md` and `COORDINATION.md`.
- Answer Adi in 1–2 lines, then do the work. For big multi-part requests, split the work across parallel workers in **separate git worktrees** (`git worktree add -b w-x /home/claude/wt-x HEAD`). Merge them yourself, run the syntax check and PhysicsCheck, push once, scan the build log, screenshot, and send the link.
- When Adi reports a feel problem, measure it with PhysicsCheck before changing anything, and show him the numbers.
