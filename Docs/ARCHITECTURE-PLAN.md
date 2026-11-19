# Code structure plan (for scaling and debugging)

Status: **planned**. It will run after the frame-time investigation, in one coordinated step: the graphics account merges and pauses, the lead restructures, and the graphics account then rebases.

## Why
`Assets/Scripts/Game/` holds 70+ files in one folder. `GameManager` is split across many partial files that share state freely, and every change recompiles everything. As the game grows (career, online, more tracks), this makes bugs hard to find and lets the two accounts get in each other's way.

## Target layout
```
Assets/Scripts/
  Physics/      FormulaSim.Physics.asmdef  pure C#, no UnityEngine (car, tyres, track, AI decisions)  [frozen]
  Core/         Game.Core.asmdef           game loop, session/race state, input, settings, save data, events
  Race/         Game.Race.asmdef           race control, rules/penalties, timing, pit lane, progression, radio logic
  Rendering/    Game.Rendering.asmdef      world build, trackside, shaders' C# side, LOD/instancing, sky, cars' look  [graphics account]
  Camera/       Game.Camera.asmdef         chase, cockpit, TV, intro, replay cameras
  UI/           Game.UI.asmdef             UiKit, menus, HUD, settings screens
  Audio/        Game.Audio.asmdef          engine, rivals, tyres, radio playback
  Diagnostics/  Game.Diagnostics.asmdef    PerfLab, PerfStats, FrameLog, DebugShots
  Editor/       BuildScript
```
- **Assembly definitions** set one-way dependencies: UI → Race → Core → Physics. Rendering can't reach into the game loop, and Physics can't reach Unity. Each change also recompiles only its own module.
- **`GameManager` gets slimmer:** it keeps the frame loop and hands work to small classes (`RaceSession`, `CameraRig`, `HudPresenter`, `SaveData`) that talk through a few explicit events (lap completed, incident, penalty, race finished) instead of shared private fields.
- **One settings/save module** (typed keys instead of PlayerPrefs strings spread across files).
- **Conventions:** one class per file, file name = class name, and a header comment saying what the file does and who owns it.
- **Tests:** PhysicsCheck (physics + AI), TerrainCheck, SyntaxCheck and the shot tool stay. Add a smoke test that builds each module.

## Safe execution
- The repo has no `.meta` files (Unity creates them in CI), so moving files is a plain `git mv`. The cloud build checks it.
- Do it in small steps, one module per commit: Diagnostics → Audio → UI → Camera → Race → Core. Check each step with a cloud build plus screenshots, and PhysicsCheck identical.
- Behaviour must stay the same at every step: no gameplay or visual change.
