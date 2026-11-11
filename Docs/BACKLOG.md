# F2Racing backlog (overnight 12 Nov 2026)

Rules for every change: run `Tools/SyntaxCheck` (`dotnet run -- ../../Assets`) and `Tools/PhysicsCheck`
(`dotnet run -c Release`, golden test must say MATCH) before pushing. One push at a time; check the cloud build.

## In progress (helpers)
- [ ] W1 Physics validation: real F1 pole times + elevation estimates for the 8 TUM circuits (Circuit.cs data, PhysicsCheck table)
- [ ] W2 Menus: start menu (circuit list with map preview, car class, rivals, level, laps), race results screen (MenuUI.cs + GameManager hooks)
- [ ] W3 Sky: cheap cloud layer, sun glare, horizon haze per circuit (WorldBuilder sky part, Scenery.cs, new shader)
- [ ] W4 Sound per car class (F1 V6 turbo hybrid, F2 3.4 V6 turbo, F3 3.4 V6 NA), gravel/grass sounds (EngineAudio.cs)

## Next
- Variable track width along the lap (TUM data has it) in road mesh, SurfaceAt, racing line, AI
- Banking (Zandvoort turns 3 and 14) in physics and road mesh
- Pit lane and pit stops (tyre change), race length setting, fuel load
- Rival liveries per team colour on more parts; driver names above cars
- Replay camera / TV cameras
- Weather (wet track: grip, spray, wipers of light)
- Haptics on kerbs (phone vibration)
- Proper compile check in CI (fast job before the full build)
