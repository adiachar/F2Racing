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
- 2026-11-19 gfx -> lead (perf, Docs/PERF-ARCHITECTURE.md; ready to paste, in priority order):
  1. `BuildScript.cs`, in `Prepare()` after `PlayerSettings.SetMobileMTRendering(...)`: `PlayerSettings.enableFrameTimingStats = true; // FrameTimingManager in release players (PerfLab GPU ms)`
  2. `GameManagerPerf.cs`, `FixedResolution`: replace `if (nativeW == 0) { nativeW = Screen.width; nativeH = Screen.height; }` with
     `if (nativeW == 0) { nativeW = RenderScale.NativeW > 0 ? Mathf.RoundToInt(RenderScale.NativeW * RenderScale.Scale) : Screen.width; nativeH = RenderScale.NativeH > 0 ? Mathf.RoundToInt(RenderScale.NativeH * RenderScale.Scale) : Screen.height; } // the base render scale (RenderScale.cs) is the 100% size`
     (Without it, on GLES with "Resolution scale" < 100 the first SetRenderScale reads Screen.width before the base scale lands and undoes it.)
  3. `GameManager.cs`: rename `void OnGUI()` to `void OnGuiBody()` and add above it:
     `void OnGUI() { var et = Event.current.type; bool hud = !menuOpen && !ui.Showing; if (hud && et != EventType.Repaint && et != EventType.MouseDown && et != EventType.MouseUp) return; /* the race HUD reacts to nothing else: every steering-thumb move was a full HUD pass */ if (hud && PerfLab.HudOff) return; PerfLab.GuiBegin(); OnGuiBody(); PerfLab.GuiEnd(); }`
  4. `GameManager.cs` Update: put `PerfLab.PhysBegin();` on the line before `while (acc >= H)` and `PerfLab.PhysEnd();` right after the loop's closing brace (timing only).
  5. `SettingsUI.cs` GRAPHICS: a row "3D resolution" cycling `RenderScale.Pct` through 0 (label "Auto (" + Mathf.RoundToInt(RenderScale.AutoScale(RenderScale.NativeW, RenderScale.NativeH) * 100) + "%)"), 100, 85, 70 (setting it applies at once; it is saved by RenderScale itself).
  6. `GameManager.cs` SetGraphics: `QualitySettings.anisotropicFiltering = level <= 1 ? AnisotropicFiltering.Disable : AnisotropicFiltering.Enable;` (per texture: road / ground / kerb textures already ask for 4-8x; forcing 4x on every texture, trees and people included, costs 1-3 ms GPU at native res). Keep the `SetGlobalAnisotropicFilteringLimits` line.
  7. `GameManager.cs` Start, after `QualitySettings.vSyncCount = 0;`: `UnityEngine.Physics.simulationMode = SimulationMode.Script; // the game has its own physics: no PhysX step per FixedUpdate`
  8. Optional: `GameManagerPerf.FpsStart`, Auto: start at the 40 rung instead of 60 when `SystemInfo.graphicsDeviceName` contains "Mali-G5" or "Mali-G6" and the panel is 120 Hz (a stable 40 beats a 60 that throttles); and later: the race HUD off IMGUI (one cached mesh + font atlas), ~2-5 ms main thread on this phone.

## Done log
- 2026-11-19 gfx perf: base render scale on Android (RenderScale.cs, ~1.3 Mpx: 2400x1080 -> 1700x765), rival LODs (CarLod.cs: car_far 3 levels, car_lod 2; CarModel is now partial), PerfLab A/B switches + frame timing in the debug overlay (PerfLab.cs, PerfStats.cs), Docs/PERF-ARCHITECTURE.md. Physics unchanged (PhysicsCheck identical).
- 2026-11-19 gfx: round 9 (stands: steel trusses, shaded roof underside, AO rows, team-coloured seat blocks, crowd shimmer, aisles, name banners; map-building glass mirrors the sky + lit windows at dusk; pit-lane markings, pit-wall gantry, race-control tower). Draw calls +0.
- 2026-11-19 gfx: rounds 1-6 on `gfx` (road grain/rubber, kerbs+run-off, sharp shadows on grass, Monza park species + furniture, forest floor/undergrowth (High), lens-flare ghosts, cockpit gloves/halo/paint); merged main into gfx (no rebase: force-push not allowed, so merge commits instead). Ready to merge when Adi approves.
- 2026-11-19 lead: chase camera tied to the car (no lag/shuffle); brief for the graphics account written.
