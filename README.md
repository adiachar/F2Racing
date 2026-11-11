# F2 Racing

Mobile formula-car racing game in Unity, with physics calibrated to the FIA Formula 2 car
(0-100 km/h 2.7 s, 3.5 g braking, 3.85 g cornering at 250 km/h).

- `Assets/Scripts/Physics` — the car model (pure C#, no Unity dependency): Pacejka tyres with combined slip,
  rear wheel-speed state, load transfer, aero, turbo engine, gearbox, clutch, TC/ABS. A line-by-line port of
  the web prototype, verified identical (`Tools/PhysicsCheck`: `dotnet run`).
- `Assets/Scripts/Game` — the game builds itself at runtime (track, playground, car, cameras, touch/tilt controls, HUD).
- `Assets/Editor/BuildScript.cs` — used by the cloud build; no need to open the project in the editor.
- `.github/workflows/build.yml` — every push builds an Android APK and a WebGL build (GameCI).

## Credits
- Car model based on "2026 Ferrari SF-26" by Tyler_Dave (https://sketchfab.com/3d-models/2026-ferrari-sf-26-e5ca6cecdc42449283f4bed27360f2a7),
  licensed CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/). Changes: all logos, decals, numbers and the cockpit
  interior removed, geometry simplified to ~53k triangles, repainted. Converted with `Tools/convert_car_model.py`.
  Note: this is a replica of a real team car; replace it with an original design before any public release.

- Circuit data: [f1-circuits](https://github.com/bacinger/f1-circuits) by Tomislav Bacinger, MIT licence.

## Physics reference (2026 Formula 1)

The car model (`Assets/Scripts/Physics/FormulaCar.cs`) uses published 2026 F1 figures: 768 kg minimum mass, 3.40 m wheelbase,
1.9 m width, 705/710 mm tyres, about 400 kW combustion engine plus a 350 kW MGU-K with the regulation speed ramp-down,
a 4 MJ battery, 8.5 MJ harvest per lap, and active aero (wings open on straights). Tyre grip, drag and downforce are not
published; they are estimates checked against real performance figures. Run `dotnet run` in `Tools/PhysicsCheck` to see the comparison.

### Chassis detail

On top of the basic car, the model includes: suspension (ride height, pitch and roll from the loads), ground-effect
aero that depends on ride height and rake, downforce loss when sliding and in another car's dirty air, slipstream,
camber and toe, tyre temperature with a grip window, a locking differential, and a rubbered-in racing line.
All of it is a few dozen arithmetic operations per step. Teams do not publish these numbers; they are typical
values, tuned so that whole-car performance still matches measured figures (`Tools/PhysicsCheck`).

## Physics lab (maths, formulas and charts)

`cd Tools/PhysicsCheck && dotnet run -c Release -- lab /tmp/lab && python3 plot_lab.py /tmp/lab ../../Docs/physics-lab.png`
drives the car through standard vehicle-dynamics tests and compares them with formulas and real data:
tyre force curves, the braking / acceleration / cornering envelope against the closed-form grip formula,
understeer gradient, step-steer yaw response, a lap trace against the ideal speed profile, a 25-lap tyre stint,
and Monaco corner speeds against real ones. Result: `Docs/physics-lab.png`.

* Monaco: the public outline has a point only every ~20 m, which squeezes bends into single vertices. Each bend's
  curvature spread is fitted (`dotnet run -- fitgeo`) to published real minimum corner speeds. The lap time was
  not fitted, and still lands within 2% of the real 2026 pole (70.7 s ideal vs 72.05 s).
* Spa: centre line and widths from the TUM racetrack database (OpenStreetMap, LGPL-3.0). Ideal lap 109.5 s vs
  104.4 s real: elevation (Eau Rouge, Kemmel) is not modelled yet.
