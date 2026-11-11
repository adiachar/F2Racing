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

## Overnight log (Nov 12)
- Done: cockpit eye under the halo with the centre pillar hidden; engine sound per class; start menu and results; sky, clouds and sun glare; road shader (asphalt, rubbered line, kerbs, paint, grid); fuel, tyre surface heat and pressures, brake migration, marbles, wind, kerb load transfer; 2026 poles and elevation for 8 circuits; AI retrained.
- Done (03:00–04:30): brake bias, haptics, pit stop; trackside (barriers, fences, stands, pits, gantry) + SetTriangles fix; TUM corner fit (mean +2.2% vs pole), Barcelona 2023 layout, Zandvoort banking (physics only).
- In progress (04:30): Suzuka/RBR AI resets; liveries + TV camera (+ replay if simple).
- Open: corner-speed targets are estimates; Zandvoort banking not shown in the road mesh; Hungaroring/Zandvoort/RBR still 3.5–4.8% slow.
- Next: variable track width, Zandvoort banking, liveries, replay/TV cameras, weather, CI compile check.
