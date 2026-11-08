# F2 Racing

Mobile formula-car racing game in Unity, with physics calibrated to the FIA Formula 2 car
(0-100 km/h 2.7 s, 3.5 g braking, 3.85 g cornering at 250 km/h).

- `Assets/Scripts/Physics` — the car model (pure C#, no Unity dependency): Pacejka tyres with combined slip,
  rear wheel-speed state, load transfer, aero, turbo engine, gearbox, clutch, TC/ABS. A line-by-line port of
  the web prototype, verified identical (`Tools/PhysicsCheck`: `dotnet run`).
- `Assets/Scripts/Game` — the game builds itself at runtime (track, playground, car, cameras, touch/tilt controls, HUD).
- `Assets/Editor/BuildScript.cs` — used by the cloud build; no need to open the project in the editor.
- `.github/workflows/build.yml` — every push builds an Android APK and a WebGL build (GameCI).
