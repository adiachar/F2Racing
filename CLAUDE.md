# READ THIS FIRST — rules for every Claude session on this repo

Two Claude accounts work on this game at the same time. This file is how they stay aligned. Read it in full, and read `Docs/COORDINATION.md`, **before you start and before every commit**.

## The game
Mobile F1-style racing game (Android + WebGL), owner **Adi**. Unity 6000.0.85f2, built-in render pipeline, everything built from C# at runtime (no editor; `Assets/Editor/BuildScript.cs` sets the build up). Target: Real Racing 3-level graphics or better, GRID-level handling, smooth on Adi's Redmi Note 12 Pro 5G (Mali-G68, 120 Hz). Made-up names only (no real teams, brands or logos).

## Hard rules
1. **Physics is frozen.** Never change `Assets/Scripts/Physics/*` behaviour for the car (FormulaCar, tyres, aero, ERS, collisions, HeightField, Track). AI *decision* code (`AiDriver.cs`, `AiRacecraft.cs`) may change, but solo AI pace must stay identical. Run `cd Tools/PhysicsCheck && dotnet run -c Release` before and after any change near the game loop; the calibration output must be identical.
2. **Performance first.** Nothing may make a race heavier. No per-frame allocations (no string building, LINQ, closures, `new` arrays, `GetComponent`, `renderer.material` in per-frame code).
3. **Shaders:** never use `step()` or comparisons on `half` values (they break silently on GLES3/Vulkan). Use `float`.
4. **WebGL has no threads:** use `FormulaSim.Par.For`, never `Parallel.For`. The game has its own `Environment` class, so write `System.Environment`. `var a = 1, b = 2;` is a compile error (CS0819). Check every Unity API exists in Unity 6000.0.
5. **Never write secrets** (Unity password, keys) anywhere.

## Who owns which files (edit only your own; ask for the rest in Docs/COORDINATION.md)
- **Lead account:** gameplay, game loop, cameras, UI/menus/HUD (`GameManager*.cs`, `RaceHud.cs`, `UiKit.cs`, `Menu*.cs`, `SeasonUI.cs`, `SettingsUI.cs`, `PreRace.cs`, `Progression.cs`), AI decisions, audio/radio, frame pacing, CI (`.github/`), `CLAUDE.md`.
- **Graphics account:** shaders and cginc in `Assets/Resources/` (not `People/`), `EnvTex.cs`, `RoadSurface.cs`, `WorldBuilder.cs`, `LightBake.cs`, `LensFlare.cs`, `ModelLib.cs`, `ModelInstancing.cs`, `Trackside.cs`, `TracksideArt.cs`, `Scenery.cs`, `Sky.cs`, `RealWorld.cs`, `CarEnv*.cs`, `CarLod.cs`, `RenderScale.cs`, `RoadShadow.cs`, `Tools/Models`.
- **Shared, small edits only, note them in COORDINATION:** `PerfLab.cs`, `PerfStats.cs`, `DebugShots.cs`, `CarModel.cs`, `Docs/PERFORMANCE.md`.

## Git workflow (both accounts)
1. **Before starting and before every commit:** `git fetch origin && git rebase origin/main` (on your branch), then re-read `Docs/COORDINATION.md` for requests to you.
2. Lead works on `main`. Graphics works on `gfx`.
3. **When a graphics round builds clean** (no shader or compile errors), the graphics account itself merges it: rebase `gfx` on `origin/main`, run the C# syntax check, then `git push origin gfx:main` (a fast-forward). Then write one line in COORDINATION.md's Done log.
4. Never force-push `main`. On a conflict in a file you don't own, keep the other side's version and write a request.
5. Write one short dated line in `Docs/COORDINATION.md` for every finished piece of work and every request.

## Before every push
- C# syntax check: `cd Tools/SyntaxCheck && dotnet run -c Release -- ../../Assets` (install with `apt-get install -y dotnet-sdk-8.0`), then delete `bin/` and `obj/` under Tools.
- After the cloud build: `gh run view <id> -R adiachar/F2Racing --log | grep -iE "Shader error|Invalid Bytecode|error CS"`. Shader errors do NOT fail the build. If Android fails with "Gradle plugin not found", it's transient: `gh run rerun <id> --failed`.
- Screenshots: `python3 Tools/Shots/shoot.py --out /tmp/s --cam chase,cockpit --s 300,2500 --quality High [--param hud=1 stats=1]`.

## Builds Adi downloads
- `main` → https://github.com/adiachar/F2Racing/releases/tag/latest
- `gfx` (manual: `gh workflow run build.yml -R adiachar/F2Racing --ref gfx`) → https://github.com/adiachar/F2Racing/releases/tag/gfx

## Talking to Adi
Very short replies in simple English (1–2 lines). Always give the APK link after a build.
