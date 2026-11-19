# Coordination between the two Claude accounts

Both accounts read this file before starting work and add a short dated line when they finish something or need something from the other. Newest entries at the top of each section. Keep entries to one line.

## Who owns what
- **Lead account (main branch):** gameplay, UI/menus/HUD (`GameManager*.cs`, `RaceHud.cs`, `UiKit.cs`, `Menu*.cs`, `SeasonUI.cs`, `SettingsUI.cs`), AI racecraft, audio, cameras, CI (`.github/`), merging `gfx` into main, and builds Adi gets on "latest".
- **Graphics account (branch `gfx`):** shaders and cginc in `Assets/Resources/` (not People), `EnvTex.cs`, `RoadSurface.cs`, `WorldBuilder.cs`, `LightBake.cs`, `LensFlare.cs`, `ModelLib.cs`, `ModelInstancing.cs`, `Trackside.cs`, `TracksideArt.cs`, `Scenery.cs`, sky/environment, `Tools/Models`.
- **Nobody** edits `Assets/Scripts/Physics/*` (physics is frozen).
- Paused: URP migration (branch `urp`). It touches every shader, so it waits until the graphics work settles.

## Rules
- The graphics account rebases `gfx` on `origin/main` before each build; the lead merges `gfx` into main when Adi approves a round.
- Need a change in a file the other account owns? Write a request below instead of editing it.
- Every build: scan the log for `Shader error|Invalid Bytecode|error CS`.

## Requests to the other account
- (none yet)

## Done log
- 2026-11-19 gfx: rounds 1-6 on `gfx` (road grain/rubber, kerbs+run-off, sharp shadows on grass, Monza park species + furniture, forest floor/undergrowth (High), lens-flare ghosts, cockpit gloves/halo/paint); merged main into gfx (no rebase: force-push not allowed, so merge commits instead). Ready to merge when Adi approves.
- 2026-11-19 lead: chase camera tied to the car (no lag/shuffle); brief for the graphics account written.
