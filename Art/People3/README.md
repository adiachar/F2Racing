# People3: realistic people from Adi's asset pack (RacingSim_Assets.zip, 21 Nov 2026)

Source files only (not imported by Unity: outside Assets/). Microsoft Rocketbox avatars, MIT licence (LICENSE_rocketbox.md:
its text must ship in the game's credits), converted by the pack's maker:

- GLB, Y-up, facing +Z, feet at Y = 0, metres; Mixamo bone names (52 bones, `mixamorig:` prefix), A-pose, max 4 weights.
- One material each: `<name>_albedo.png` 1024 (+ `_normal` / `_rough` 512, OpenGL normals). Hair = alpha-clip cards.
- engineer_* also `_tint.png`: white = team colour (shirt).
- 6.4k - 10.3k tris each (the game's LODs: decimate to ~4k / 1.5k / 0.5k as Tools/People2/build.py does).

| Files | Who |
|---|---|
| fan_01 .. fan_16 | grandstand / paddock fans (7 women, 9 men) |
| marshal_01 .. 03 | orange-overall track marshals |
| official_01, 02 | race officials (suits) |
| engineer_01 .. 04 | team engineers with headsets (tintable shirts) |
| photographer_01 | trackside photographer (camera in the right hand) |
| tv_presenter_01 | TV presenter (microphone in the right hand) |

The pack's own crew_* / driver_* (MakeHuman, plain) were left out: the game's People2 driver and crew look better.
