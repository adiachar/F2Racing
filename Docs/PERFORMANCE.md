# Performance on low-end Android phones

This is a static audit. It was done without an editor or a phone, so every number below is an estimate from the code, the asset sizes and the `Tools/PhysicsCheck` timings. Check the numbers on a device with the checklist at the end.

## Budget per graphics level

The target phone is a low-end Android phone (Cortex-A53/A55 CPU, Mali-G52 / Adreno 610 class GPU, 3-4 GB RAM, 720p-1080p screen).

| | Low | Medium (Auto starts here) | High |
|---|---|---|---|
| Frame target | 30 or 60 fps (setting) | 60 fps | 60 fps |
| CPU main thread | ≤ 12 ms | ≤ 10 ms | ≤ 10 ms |
| Draw calls (3D) | ≤ 250 | ≤ 400 | ≤ 600 |
| Player car | car_lod, about 65k triangles drawn (full car in cockpit/halo view) | same as Low | full car, about 190k triangles drawn |
| Rival cars | car_lod within ~30 m, car_far beyond; not drawn beyond 450 m | not drawn beyond 800 m | no distance cull |
| Car paint shader | per-vertex SubShader (LOD 100) | full per-pixel (cubemap, 2 speculars) | full |
| Colour grade | cheap (tone curve + saturation) | full | full |
| Wheel faces, sparks, tyre smoke | off | on | on |
| Spray | off | on, within 70 m | on |
| Rain | 160 drops | 420 | 420 |
| Sun glare | off | on | on |
| Fences, ground decals | off (TracksideLod) | on | on |
| Road | 1 texture read (LOD 150) | 2 reads + sheen (LOD 300) | same |
| Shadows | off | on (1 cascade, 55 m) unless the circuit is baked | 2 cascades, 90 m |
| MSAA / aniso | off / off | 2x / on | 4x / on |
| Far clip | 1500 m | 2500 m | 2500 m |
| Memory (game) | ≤ 350 MB | ≤ 450 MB | ≤ 600 MB |

## Where the time goes (from the code)

### CPU: physics

- The physics runs at 240 Hz for the player and every rival. At 60 fps that is 4 ticks per frame. At 30 fps it is 8.
- `Tools/PhysicsCheck -- race` (21 cars, all 11 circuits) races 3933 s in about 37 s on the build machine. That is about **1.9 µs per car-step**, including AI thinking (60 Hz), car-to-car contact and wheel contact.
- A phone running IL2CPP on ARM is roughly 6-10x slower, so about **12-19 µs per car-step**.
- The player plus 21 rivals is 22 cars × 240 Hz = 5280 car-steps per second. That is **about 1.0-1.7 ms per frame at 60 fps**.
- The player's own step also runs `Track.Nearest`, `WheelContact` and `Weather.Apply` on every tick. The rivals run these 60 times a second.

### CPU: drawing

- Each car is about 19 mesh parts, plus 4 tyre stripes, up to about 15 wing pieces split off for damage, a helmet and a contact shadow. That is **about 35 draw calls per car**. The player's car adds 4 wheel faces and the shift-light strip.
- Every car has its own materials (livery, brake glow, wear), so cars do not batch.
- A full field in view is 500+ draw calls. This is the largest render-thread cost.

### CPU: HUD

- The HUD and menus use IMGUI. Every `Fill` and every label is its own draw call: 80-150 for the race HUD.
- OnGUI ran at least twice a frame (Layout and Repaint).

### GPU

- The player's car is large on screen, and its full paint shader is about 70 ALU per pixel plus a cubemap read. In the cockpit it covers 40% of the screen.
- Shadows on Medium/High draw every caster a second time.

### Fill-rate risks (overdraw)

- **Clouds**: a dome alpha-blended over every sky pixel, with 2 texture reads. It is kept on every level and is cheap per pixel.
- **Sun glare**: an additive quad about 50° across, up to a quarter of the screen. **Now off on Low.**
- **Spray**: each puff is up to 0.35 of the screen (`maxParticleSize`), soft and alpha-blended, up to 30 per car within 70 m. Puffs behind the player's car in chase view stack 3-6 layers deep. It was already off on Low.
- **Fences**: alpha-tested (`BakedCutout`). Alpha test breaks early-Z on Mali/Adreno tile GPUs. They are hidden on Low.
- **Rain**: stretched streaks, cheap, 160 on Low.
- **Wheel faces**: 4 transparent quads. **Now off on Low.**

### Memory and load time

- **Textures generated at runtime**: all small. Clouds 256², trackside atlas 256², boards 256×64, mini-map 128², and several 64² and 32² textures. The total is under 3 MB.
- **Car meshes**:
  - `car.bytes` is 6 MB: 129k source triangles, 191k drawn after the two-sided twins.
  - `car_lod` is 2.3 MB and 38k triangles (56k drawn).
  - `car_far` is 1.2 MB and 25k drawn.
  - The converter stores `halo_pillar` with the whole shared carbon vertex list: 53k vertices for 447 triangles, doubled to 106k by the twin. That was about 3 MB of vertex buffer, kept twice (CPU copy and GPU copy). **Now compacted at load.**
- **TextAsset copies**: `TextAsset.bytes` returns a copy. Every rival re-read all of its arrays even when the meshes were cached: about 2-3 MB of garbage per rival, so about 50 MB of GC at every race start with 21 rivals. **Now skipped for cached meshes.** The full car's TextAsset is unloaded after parsing.
- **LightBake bake** (load time): a sun march and two occlusion rings over a roof map, for every world vertex. Expect 1-3 s on a phone at Spa-size circuits. The roof map is about 600×900 floats there. It writes `mesh.colors` (`Color`, 16 bytes per vertex) where `Color32` would be 4 bytes. It is not changed, because the baked light goes slightly above 1.0.
- **Terrain/world building**: HeightField hashing, the environment and trackside meshes, and `RecalculateNormals` on every chunk. All of it is one-off, on the "Loading…" frame.

## What was changed

### Per-frame garbage (GC)

- `GameManager.ReadInput`: `Input.touches` allocated a new array every frame. It now uses `Input.touchCount` / `GetTouch`.
- `CarModel.Apply`: a `float[]` was allocated every frame for the player's wheel blur. It is now a scalar.
- Replay caption: a string was built every OnGUI call. It is now cached per second.
- Qualifying live timing (`Qualifying.cs`): a new list and sort ran every OnGUI call, plus about 60 interpolated strings per pass. Now the list is reused (sorted once a frame), row strings are rebuilt only when what they show changes, the clock string once per second, and position numbers come from `HudNum`.
- `Haptics.Pulse`: the `VibrationEffect` JNI class is now looked up once, not on every pulse (up to 12 a second on kerbs).
- `GameManager.Awake`: `useGUILayout = false`. Nothing uses GUILayout, so Unity skips the extra Layout pass of OnGUI each frame. This roughly halves the HUD's script cost.

The race HUD (`RaceHud.cs`) already cached its strings. The rest of the hot paths (`Sky.LateUpdate`, `SkidMarks`, `WeatherFx`, `AiDriver.Think`, `FormulaCar.Step`, `DriverFeel`) were checked and allocate nothing per frame. No LINQ, `Camera.main` loops, `Shader.Find` or `GetComponent` runs per frame (`Sky` reads `Camera.main` only until it has a camera). No `renderer.material` clones were found: every car has its own material instances, made once at load.

### Material writes

- `CarModel`: the brake glow and dust, tyre wear and dirt, and wheel blur now use cached property IDs.
- The glow, dust, wear and dirt are written only when they change. A cold disc stays black for most of a lap, so about 8 `SetColor`/`SetFloat` calls per car per frame mostly go away.

### Physics: rivals at 120 Hz when nobody can see them

- This is in `GameManagerPerf.RivalCoarse`. It applies to a rival that is:
  - more than 150 m along the track from the player,
  - outside the camera's detail range (`rivalNear`),
  - and racing for more than 60 s, so the lap-1 pack stays at 240 Hz.
- Such a rival skips one tick and then steps twice the time on the next. While it is one tick behind, it is drawn one tick further forward. When it leaves 120 Hz it catches up with one double step.
- The player's car is never changed.
- Checked with `COARSE=1 dotnet run -c Release -- race`, the same logic with car 0 standing in for the player:
  - 3 resets, the same as without it.
  - With `DAMAGE=full`: 3 resets against 4.
  - Best laps differ by +0.006 s on average and 0.18 s at most.
- The golden test still prints MATCH twice. The default race test still gives 3 resets.
- Expected saving on a phone: about **0.35-0.6 ms per frame** at 60 fps with 15-21 rivals spread out, and double that per frame at 30 fps.

### Player car level of detail (`GameManagerPerf.PickPlayerModel`)

- **On High**: the full car (`car.bytes`) is always used.
- **In the cockpit and halo views**: the full car is always used. A software render of the cockpit view (`Tools/render_car_model.py`, view `cockpit`) shows that car_lod's simplified sidepods next to the cockpit break into spiky triangles with gaps. car_lod is not good enough there, so the cockpit view looks exactly as before.
- **On Low/Medium in the chase, far-chase and TV views and in replays**: `car_lod` is used.
  - Its body paint is drawn two-sided (`twinPaint`), because some of its decimated body triangles face inwards and would show as holes.
  - This is about 65k triangles drawn instead of about 190k. The shadow-map pass (Medium, unbaked circuits) gets the same cut.
- **Building and switching**:
  - Each model is built on first use, so on Low/Medium the full car is never parsed unless the cockpit is chosen.
  - Switching only toggles two objects. The livery (player or team) and the tyre stripe are copied across, and `DriverFeel` re-attaches the shift lights.

### Graphics levels now really cut the expensive things (`GameManagerPerf.ApplyPerfLevel`)

These apply on Low:

- **Car paint**: a new per-vertex SubShader (LOD 100, about a third of the full pass).
  - It keeps the livery split, the AO, the helmet visor and the brake-disc glow.
  - It drops the carbon weave, the tyre sheen and the reflection-probe read.
- **Colour grade**: the global keyword `F2_GRADE_LOW` gives a cheap grade: white balance, exposure, tone curve and saturation, without the sqrt-space contrast/lift/gamma/gain.
  - It is compiled only into the shaders drawn on Low: Baked, BakedCutout, CarFx, Clouds, WeatherFx, the Low Road SubShader and the Low CarPaint.
  - The look on Low is slightly flatter than Medium.
- **Other effects**: the wheel faces (CarWheelFace), plank sparks and tyre smoke (CarFx) and the sun glare are off. Spray was already off.
- **Rival distance cull**: cars are not drawn beyond 450 m on Low and 800 m on Medium (`Camera.layerCullDistances` on the car layer). This is turned off in the TV camera and in replays, where a zoomed lens can show a far car big.
- **Tyre stripes on far rivals**: `car_far` rivals have no tyre-stripe objects. That is 4 draw calls less per far rival, and the stripe is under a pixel at 30 m and beyond.

### Dynamic resolution and the 30/60 fps setting

- **The problem**: `ScalableBufferManager` only works on Vulkan, Metal and D3D12. On OpenGL ES (which many low-end phones run, and Unity's fallback where Vulkan is blocked) it does nothing. `AutoQuality` waited for "resolution at minimum" before cutting effects, so on GLES it spent about 10 s lowering a resolution that never changed. The Resolution scale setting did nothing at all.
- **The fix**:
  - On GLES, dynamic resolution is skipped, so Auto goes straight to lowering the graphics level.
  - The Resolution scale setting sets a smaller screen buffer instead (`Screen.SetResolution`). The HUD scales with `Screen.height`, so its layout stays the same, only softer.
- **The 30/60 fps setting**: both `AutoQuality` and `DynamicResolution` measure against `FpsTarget`: "too slow" is under 83% of it and "fast enough" is over 96%. 30 fps and 60 fps therefore both work with them. At 30 fps there are 8 physics ticks per frame instead of 4.

### Load time and memory

- The vertex lists are compacted (the halo pillar was 106k vertices for 447 triangles), saving about 3 MB on the GPU and 3 MB on the CPU for the full car, and about 1 MB for each LOD.
- Cached rival meshes are no longer re-read.
- The full car's 6 MB TextAsset is unloaded after parsing.
- On Low/Medium the full car is not loaded at all unless the cockpit or halo view is used.

### Estimated gain on a low-end phone, Medium, chase view, 15 rivals

| Change | Saving per frame |
|---|---|
| Player car LOD (main pass + shadow pass) | 1-3 ms GPU |
| No Layout pass in OnGUI | 0.5-1 ms CPU |
| Rivals at 120 Hz | 0.35-0.6 ms CPU |
| Fewer material writes | about 0.1 ms CPU |

- On top of that, there are fewer GC spikes: the qualifying HUD alone made about 10 KB of garbage a frame.
- **On Low**, also:
  - Car paint: 1-2 ms GPU in chase view, more in the cockpit.
  - Glare and grade: about 0.5-1 ms GPU.
  - Fewer far draw calls: 1-3 ms render thread with a long straight in view.
- **Overall**: about 2-5 ms on Medium and 4-9 ms on Low. On a phone that was at 40-50 fps, that is the gap to a steady 60.

## Still to do (not changed: bigger or riskier)

1. **One draw call per far rival.** Bake `car_far` into a single mesh with the livery in vertex colours and one shared material, so far cars batch. This takes about 35 draw calls per car down to 2. It is the largest remaining CPU item.
2. **HUD off IMGUI.** One dynamic mesh for all the HUD panels, and text from a font atlas. IMGUI costs 80-150 draw calls and 1-2 ms.
3. **LightBake cache.** Store the baked vertex colours per circuit, time of day and weather in `persistentDataPath`, or spread the bake over several frames behind the loading screen. Use `Color32` with a 0..2 range for the baked colours.
4. **Make car meshes non-readable once built** (`UploadMeshData(true)`) after the damage split has been cached. This frees the CPU copies, about 10 MB with 21 rivals.
5. **Fix the inward-facing triangles in `car_lod`** in `Tools/convert_car_model.py`. Then rivals would not show small holes either, and the player LOD would not need the two-sided paint.
6. **Spray**: shrink `maxParticleSize` on Medium (0.35 → 0.2), or cap it at 15 particles behind the player's car in chase view.
7. **Clouds on Low**: draw a static gradient instead of the 2-read cloud dome.
8. **Player tick work**: `Track.Nearest` and `WheelContact` run at 240 Hz for the player. Running them at 120 Hz would change the player's physics, so it was not done (golden test).

## On-device test checklist

1. **Build and setup.** Build the APK (GameCI) and install it on the slowest phone you have. Use a Vulkan phone and, if you can, a GLES-only one too.
2. **FPS counter.**
   - Turn it on in Settings → GRAPHICS → **Show FPS** (on by default).
   - It is the small grey text at the top right, under the Settings / Cam / Restart buttons.
   - It shows the smoothed frame rate: "57 fps".
   - When dynamic resolution has lowered the picture it adds "(80% res)". On GLES this appears only when Resolution scale is under 100.
   - The number is an average over about the last 20 frames. A short hitch shows as a dip of a few fps, not as a single bad frame.
3. **Quick race.**
   - Set Monza, 15 rivals, Auto graphics, 60 fps.
   - Watch the counter on the grid, through the first chicane and on the straights.
   - Auto should keep about 50+ fps.
   - If it shows "Graphics lowered to keep it smooth", note which level it settled on: Settings → GRAPHICS → Graphics level still says Auto, and the effects you can see tell you the level.
4. **Each level.** Set Low, Medium and High by hand and do one lap each in chase view and in cockpit view. Expect:
   - Low: no glare, no sparks or smoke, plain car paint (no carbon weave), slightly flatter colours.
   - Medium: like before.
   - High: the full car in every view.
5. **Cockpit look.** Switch Cam to Cockpit on Medium. There should be a short one-time pause the first time while the full car is built. Then check the sidepods and halo look like before (no gaps). Switch back to Chase: no pause.
6. **Livery after a switch.** In a season round (team livery), switch Chase → Cockpit → Chase. The colours and the tyre stripe must stay the same.
7. **Far rivals.**
   - Run 2 minutes into a race. Rivals more than 150 m away now run at 120 Hz.
   - In the TV camera, watch a car far ahead: it must move smoothly, without shaking.
   - Check the lap times in the timing tower look normal.
8. **30 fps.** Set Frame rate to 30 fps. The counter should sit at about 30. Auto and the resolution scaling should not fight: the resolution may drop if it shows under 25.
9. **GLES phone.** Set Resolution scale to 70%. The picture gets softer, the HUD stays in place and touch still works.
10. **Thermal.** Do a 10-minute race at 60 fps. If the counter slowly falls, the phone is throttling. Recommend 30 fps on it.
11. **Optional profiler pass.** Use a Development build with the Unity Profiler over USB. Look for:
    - GC.Alloc in `GameManager.Update` / `OnGUI`: it should be about 0 B per frame in a race. Qualifying shows a few strings when a time changes.
    - Render-thread draw calls: below the budget above.
