# Performance on low-end Android phones

This is a static audit. It was done without an editor or a phone, so every number below is an estimate from the code, the asset sizes and the `Tools/PhysicsCheck` timings. Check the numbers on a device with the checklist at the end.

## Rendering architecture audit (Nov 2026): track-sector visibility

The sections further down are older (Sept-Oct); the quality tiers and Auto now live in `GameManager.cs`
(`SetGraphics`, `AutoQuality`) and `GameManagerPerf.cs` (`ApplyPerfLevel`). This section is the newer world-render pass.

### How to measure (headless or on a phone)

- **On a phone**: Settings > GRAPHICS > **Render stats (debug)** (off by default). Top-left overlay, refreshed twice a second
  (`Assets/Scripts/Game/PerfStats.cs`): engine draw calls / batches / SetPass / triangles (ProfilerRecorder, "n/a" where
  the player has no counters), world renderers drawn + triangles **now** and **before** (frustum only, i.e. what
  Unity alone would draw without the sector sets), other renderers (cars, people, sky), alpha-tested card overdraw in
  screens (now / before), distinct materials and shader variants drawn, the sector and how many renderers its set
  hides, texture memory (runtime-generated part separately; readable textures count twice) and mesh memory.
- **Headless**: `python3 Tools/PerfAudit/audit.py --out /tmp/perf` runs the WebGL shot harness for Low/Medium/High at
  Monza s = 0, 1450, 2500, 5150 with `stats=1`; the game logs one `PERF_STATS` line per shot and the script prints a
  markdown table (+ `perf_audit.json`). `--parse console.log` re-reads an old run. The overlay is in the PNGs too.

### Findings (static, from the code)

| # | Finding | Cost |
|---|---|---|
| 1 | **Circuit-wide single meshes** are never frustum-culled: the card trees (`Trees`, up to 2500 trees x 2-3 cards + 3000 wood-band cards on the 10 procedural circuits), `EnvProps`, `EnvBlocks`, `Town`/`TownRoofs`, the procedural `Terrain` grid (~180 x 180 cells), RealWorld's `EnvFarTrees` (whole region, Monza). Every vertex is shaded every frame, behind the camera too. | the largest single vertex cost on procedural circuits; ~60-75% of it is off-screen at any moment |
| 2 | **RealWorld blocks were 1-1.3 km** (EB = 2 on Medium/High). Buildings, roads and trees are LOD-culled per block by distance to the *block centre*, so a block whose near edge is 700 m away kept trees 1.7 km away; frustum culling worked on 1 km pieces. | Monza: most visible building / tree vertices are beyond the intended reach |
| 3 | **Trackside chunks** (250 m, atlas, crowd, fences) have no distance limit at all; only the posts (TracksideLod, 85 m). | draw calls and vertices of 1-2 km-away barriers, a pixel tall |
| 4 | **No occlusion**: hills (Spa, Austria, Suzuka, the Brianza terrain round Monza) hide whole sectors of the circuit that are still drawn. | circuit-dependent |
| 5 | **Shader variants**: every world shader used `multi_compile_fog` (4 fog variants) but the game only ever uses linear fog (Sky.cs). | 2x the world-shader variants: memory, load-time warm-up |
| 6 | **RealLand** colour map (RealWorld) is RGBA32 with mips: 1280^2 = 8.7 MB on Medium, 2048^2 = 22 MB on High, read on every terrain pixel. The model atlas and wheel atlas were already compressed; the other generated textures are small or already non-readable. | memory + texture bandwidth |
| 7 | Already good: rivals merged + instanced, player car LOD, people LOD (LOD1 > 40 m, hidden > 230 m), posts LOD, foliage LOD 150 on Low (no alpha-to-coverage, no sway, mip bias), static batching of the world, all big generated textures non-readable. | - |

### What was changed

- **`SectorVisibility.cs`** (hooked by one line in `WorldBuilder.Build`, before static batching):
  1. *Split*: any world mesh wider than 600 m with 2000+ triangles (not under a LODGroup, no scripts/children, not
     the `Road`) is cut into tiles of >= 256 m and >= ~600 triangles by triangle centre, keeping every vertex attribute
     in its format (normals, colours, uv0-3). Same pixels, but now frustum-culled per tile (finding 1).
  2. *View sets* (the GT / F1 approach): the lap is cut into 50 m sectors. A sector's camera region = its centreline
     +-15 m, half width + 30 m each side (pit lane, chase offsets), 3 m below to 9 m above the road. A renderer is in the
     sector's set unless from the whole region it is (a) under 0.5 px (High) / 0.8 px (Medium) / 1.3 px (Low) of
     contrast at 1080p and a 45 degree lens, measured by the height of the tallest thing in it (per 16 m cell, not
     its bounds) and the dry haze (finding 3), or (b) behind the terrain: a horizon profile (256 directions x 64 steps
     of 36 m) from 9 eye points per sector over a lower bound of the terrain, 2 m margin, top face of the bounds tested,
     never within 200 m, only when the terrain has 15 m+ relief (finding 4). Ground-like layers (terrain, road,
     verges, water, far tree lines, flat ribbons) are never size-culled.
  3. *Run time*: the camera's nearest centreline point picks the sector; only renderers whose state changes are
     touched (`Renderer.forceRenderingOff`, so TracksideLod's and the LODGroups' own switching is untouched). TV/replay
     zoom under 44 degrees, the test pad, menu views and any camera outside the region draw everything as before.
     Cost: ~120 point distance checks per frame; one pass over the renderers per sector change (every ~0.6 s at 300 km/h).
  Since a set is a superset of what can be seen from anywhere in its region, there is no popping by construction:
  what goes is either under a pixel or behind a hill.
- **RealWorld blocks** = one terrain chunk (512 m High, 640 m Medium, 768 m Low; were 1024 / 1280 / 768): 4x finer
  frustum and distance culling of buildings, roads and trees on High/Medium (finding 2).
- **RealLand** compressed on Low/Medium (`Texture2D.Compress`, ETC2/DXT5: 8.7 -> 2.2 MB on Medium); High keeps RGBA32.
- **Fog variants**: Baked, BakedCutout, Foliage, Road, Ground use `multi_compile __ FOG_LINEAR` (2 variants instead of
  4; halves those shaders' variant count). The car / FX shaders were left alone.
- **Debug overlay + headless audit** (above).

### Before / after (estimates; confirm with `audit.py`, numbers are per frame, Medium, chase view)

| | Before | After (expected) |
|---|---|---|
| Procedural circuit, circuit-wide meshes (trees, props, town, terrain) | 100% of their vertices | ~25-45% (only the tiles in the frustum) |
| Monza, RealWorld buildings/trees beyond their reach still drawn (block granularity) | blocks of 1.28 km | blocks of 640 m: ~half the overshoot |
| Monza, `EnvFarTrees` (whole region, never culled) | 100% | ~30-40% (tiles in the frustum) |
| Trackside chunks 1.5-2.5 km away (sub-pixel barriers) | drawn | culled by the sector sets |
| Hilly circuits, sectors behind hills | drawn | culled when hidden from the whole sector region |
| World shader variants (5 shaders) | x4 fog | x2 fog |
| RealLand memory, Medium / Low | 8.7 / 5.6 MB | 2.2 / 1.4 MB |
| Draw calls | - | up a little where tiles add pieces (split meshes, 4x RealWorld blocks), down where sets cull; static batching keeps SetPass calls unchanged |
| Load time | - | + the split and the sets: tens of ms flat, ~0.1-0.3 s on a phone with occlusion (logged: "SectorVisibility ... ms") |

### Considered, not done (and why)

- **Alpha-to-coverage only on High**: Medium has 4x MSAA, and A2C *is* the anti-aliasing of the foliage edges there;
  turning it off makes the tree line crawl (a visible loss). Low already has no A2C (Foliage LOD 150).
- **Distance-faded detail reads** in Ground/Baked: the second detail read is already Medium/High only (LOD 300 vs 150);
  far pixels read the top mips (cache-resident), so skipping them saves ALU only, and a branch around a sample needs
  explicit gradients, which are half-rate on some Mali GPUs.
- **Camera-distance LOD for ModelLib trees** (LOD0 near the track seen from 1 km): needs a second mesh per 100 m block
  and dithered cross-fade in Foliage.shader; next step if the overlay shows model-tree triangles high.
- **Far plane = fog end**: Unity's fog uses view depth, so at the screen edge an object is fully fogged only at
  ~1.47x fog end (2870 m at Monza), beyond today's 2500 m far plane: no gain.
- **EnvTex atlas compression** belongs with the EnvTex caching work (other engineer): `Compress` must run before the
  textures are made non-readable there.

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

Draw calls per car (counted from the code and the `.bytes` files; contact shadow included):

| Car | Before | Now | Now, GPU instancing (GLES3, Vulkan, Metal) |
|---|---|---|---|
| Player (car or car_lod) | 34 + 4 wheel faces + shift lights | the same (not changed) | the same |
| Rival, car_lod (within ~30 m), safety car | 34 (19 parts, 9 wing pieces, 4 stripes, helmet, shadow) | **8** (body, 4 wheels, 2 front uprights, shadow); **13** while a wing is damaged | 7 instanced calls shared by ALL near rivals, + 1 shadow each |
| Rival, car_far | 28 (18 parts, 8 wing pieces, helmet, shadow) | **2** (one mesh, shadow); 7 while a wing is damaged | 1 instanced call for ALL far rivals, + 1 shadow each |

- Before, every car had its own materials (livery, brake glow, wear), so nothing batched, and a full field in view was 500+ draw calls.
- Now every rival shares ONE material; what differs per car goes through a `MaterialPropertyBlock` (see "Rival cars: merged meshes" below).
- A full field of 21 rivals in view: about **60** draw calls on GLES2 (5 near, 16 far: 5 × 8 + 16 × 2) instead of about 620; with instancing about 30 (8 instanced calls + 21 shadows).

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

### Rival cars: merged meshes, one shared material (`CarModel.CreateMergedRival`)

- **What is built.** The first rival merges the parts of `car_lod` / `car_far` into a few meshes, cached for every other rival (and the safety car, which uses car_lod):
  - `car_far`: the whole car in **one mesh**, wheels and helmet included. The wheels stand still and do not steer (the car is under about 20 px tall beyond 30 m). About 37k vertices.
  - `car_lod`: one body mesh (bodywork, wings, halo with its pillar, mirrors, rear uprights, steering wheel at rest, helmet; about 59k vertices, 16-bit indices). Per wheel one spinning mesh (tyre, rim, compound stripe), and on the two front wheels one steering-only mesh (caliper, upright). So the wheels still spin and steer.
  - The two-sided parts are twinned exactly as before. The part meshes are not built for rivals at all, and the `.bytes` file is unloaded after the merge.
- **Part IDs in the vertex colour.** Alpha = part ID (0 body paint, 1 wings, 2 carbon, 3 metal, 4/5 tyre front/rear, 6/7 rim and brake front/rear, 8 rear lights, 9 tyre stripe, 10 helmet, 11 steering wheel). R = baked AO with the old per-material strength already applied; G = brake-disc mask. `CarPaint.shader` has a new keyword `_MERGED`: the part ID picks the colour, clear coat, gloss and metal the old material had, and the carbon weave, tyre sheen and wear, brake dust and disc glow (per axle), rear-light glow, livery split and helmet visor are all still done, in the full and the Low SubShader.
- **Per car: a `MaterialPropertyBlock`.** Livery (`_LivA/B/C`), stripe colour, brake glow front/rear, brake dust, tyre wear/dirt front/rear and the rear-light colour (brake, rain light, harvest blink). The block is written only when a value changes, at most once a frame (`LateUpdate`).
- **GPU instancing.** These properties are declared as instanced properties (`#pragma multi_compile_instancing`, `enableInstancing` on the shared material). Where instancing works (GLES3, Vulkan, Metal), the same mesh on many rivals is one draw call. On GLES2 Unity has no instancing; the same shader variant without `INSTANCING_ON` reads the properties as plain uniforms, so it is one draw call per renderer, but with no material switch between cars.
- **Damage.** The merged body is split into the five wing pieces only when a wing of that car is damaged (+5 draw calls, cached per model), and put back together when it is repaired. Wheel triangles never go with a wing piece. Bent wheels, flat tyres, debris and smoke work as before on car_lod. A flat tyre now squashes the rim with the tyre (they are one mesh); this is small and only on a punctured rival.
- **Not changed.** The player's car (full and car_lod, `twinPaint`) is still built part by part with its own materials and all its effects. `car.bytes`, `car_lod.bytes` and `car_far.bytes` are unchanged: the merge is done at load.
- **Fallback.** If `CarPaint` is not supported on a phone, rivals are built part by part as before.
- **Check.** `python3 Tools/render_car_model.py game Assets/Resources/Car/car_lod.bytes before|after out.png front34,rear34,side,chase` draws a rival the old way (24 objects) and the merged way (7 objects). The two are pixel-identical for car_lod and car_far: `Docs/car-previews/rival_lod_before.png`, `rival_lod_after.png`, `rival_far_before.png`, `rival_far_after.png`. This checks the merge logic as written again in Python, not the C# itself or the shader.
- **Expected saving:** about 550 draw calls less with a full field in view: about **3-6 ms of render thread** on a low-end phone, more on GLES2 drivers, where each draw call costs more.

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
- **Rivals merged** (with a full field in view): about 3-6 ms render thread.
- **On Low**, also:
  - Car paint: 1-2 ms GPU in chase view, more in the cockpit.
  - Glare and grade: about 0.5-1 ms GPU.
  - Fewer far draw calls: 1-3 ms render thread with a long straight in view.
- **Overall**: about 2-5 ms on Medium and 4-9 ms on Low. On a phone that was at 40-50 fps, that is the gap to a steady 60.

## Still to do (not changed: bigger or riskier)

1. ~~One draw call per far rival.~~ Done, also for the near rivals: see "Rival cars: merged meshes".
2. **HUD off IMGUI.** One dynamic mesh for all the HUD panels, and text from a font atlas. IMGUI costs 80-150 draw calls and 1-2 ms.
3. **LightBake cache.** Store the baked vertex colours per circuit, time of day and weather in `persistentDataPath`, or spread the bake over several frames behind the loading screen. Use `Color32` with a 0..2 range for the baked colours.
4. **Make the player's car meshes non-readable once built** (`UploadMeshData(true)`) after the damage split has been cached. The rivals now share one set of merged meshes (about 3 MB in all, not per car); they stay readable because the wing pieces are split off lazily, on the first wing damage.
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
11. **Rival cars (merged meshes).** In a race with 20 rivals:
    - Liveries: every rival has its team's two colours and accent line, near and far; the front wing and rear-wing flaps take the secondary colour.
    - Tyre stripes on near rivals match their compound, and change after a pit stop.
    - Brake discs of near rivals glow at night or in a tunnel under hard braking; rear lights light up on braking and blink in the rain.
    - Wheels of near rivals spin and the fronts steer.
    - Crash into a rival's front wing (Damage: Full): the wing droops or comes off; after its pit stop it is whole again.
    - The far/near swap at about 30 m shows no colour jump.
    - Frame Debugger (Editor) or the profiler: a far rival is 1 draw (+ shadow), a near one 7 (+ shadow). On a GLES3/Vulkan phone they show as "Draw Mesh (instanced)".
    - On a GLES2-only phone (if you have one), rivals must look the same.
12. **Optional profiler pass.** Use a Development build with the Unity Profiler over USB. Look for:
    - GC.Alloc in `GameManager.Update` / `OnGUI`: it should be about 0 B per frame in a race. Qualifying shows a few strings when a time changes.
    - Render-thread draw calls: below the budget above.

## CPU & load

Measured offline on the build machine (.NET 8, x64, 2 cores, `DOTNET_TieredCompilation=0`). A phone running IL2CPP on ARM is roughly 6-10x slower per core. New bench modes in `Tools/PhysicsCheck`:

- `dotnet run -c Release -- perf [circuit]`: the game's per-tick loop for 22 cars (car 0 = player), ms per 240 Hz tick by part, and bytes allocated. `LOD=1` turns on the rivals' level of detail.
- `dotnet run -c Release -- loadperf`: the pure-C# load steps of every circuit (track, racing line, height field, 300k ground samples).
- `LOD=1 dotnet run -c Release -- race`: the AI race with the rival LOD (car 0 stands in for the player); prints the share of rival steps run.

### Physics and AI per frame

Where the time goes (Monza-size circuit, 22 cars, per 240 Hz tick, before): `car.Step` about 17-22 µs (75%), `Think` + walls about 5 µs, ground grade (`HeightField.Grade`) about 3.3 µs, `Nearest` + `WheelContact` about 1 µs, car-to-car contact 0.3 µs. **0 bytes allocated per tick** (checked with `GC.GetAllocatedBytesForCurrentThread` over 43,200 ticks). There is no LINQ, no per-step array and no string work in the simulation.

| Change | Before | After |
|---|---|---|
| `HeightField.Sample` (the player's grade every tick, the rivals' at 60 Hz, and every terrain/scenery vertex at load): the 9 `Dictionary<long, List<int>>` lookups per sample replaced with a flat bucket grid (same buckets, same order) | 1019 ms / 3.3M samples | **327 ms** (3.1x) |
| Rival physics LOD (`AiLod`, below), 22 cars, Monza, `perf`, best of 3 | 23.7 µs/tick | **19.2 µs/tick** (LOD only after 60 s of the 3-minute run) |
| Same, over the full AI race (`race`, 11 circuits) | 100% of rival steps | **64.7%** of rival steps |

**Rival level of detail (`AiLod` in `AiDriver.cs`, used by `GameManager.StepRivals` / `PickRivalRates`).** Replaces `GameManagerPerf.RivalCoarse` (120 Hz only; that method is now unused). From one minute after the start, a rival outside the camera's detail range runs at:

- 240 Hz within 150 m (along the track) of the player;
- 120 Hz beyond 150 m;
- 60 Hz beyond 400 m when no other car is within 60 m of it, so contacts are resolved on fresh positions.

Time stays exact. The car counts the ticks it is behind, and its next step covers all of them, so it never gains or loses time. It is drawn that much further forward (`RivalBehind`). Back in range, it catches up in one step. The player's car is never touched.

Fairness check (`race`, 11 circuits × 20 drivers, against the plain 240 Hz run):

- Without damage: same finishing position for 220/220 drivers. Best laps differ by -0.002 s on average (0.013 s mean absolute). 2 resets in both runs.
- With `DAMAGE=full`: 198/220 drivers finish in the same place. Mean best-lap change: -0.44 s. These figures are the same as the old 120 Hz scheme (`COARSE=1`: 198/220, -0.44 s). With damage on, any perturbation sends the contacts down a different path, so the run is chaotic. No NaN, no extra resets.

**Calibration and race output are IDENTICAL** with the LOD off (`dotnet run` and `race`, diffed apart from the timing columns). The player's physics is unchanged bit for bit. The `HeightField` changes give bit-identical heights (checksum over 300k samples equal to the last digit).

### Audio

- `EngineAudio` per sample: the turbo whistle, gearbox whine (2 harmonics), MGU-K whine (2) and kerb thump called `Mathf.Sin` (a double-precision sin) on every sample, up to 6 per sample at 48 kHz.
- They now read a 4096-step sine table with linear interpolation (`SinCyc`). The error is under 3e-7, about 130 dB down, so it is inaudible.
- The biquad coefficients were already updated only every 32 samples. `RivalAudio` has no per-sample transcendental functions.

### HUD and game loop (allocations)

- Checked: `RaceHud` / `UiKit` already format every number through cached strings (`HudNum`, `HudSlot`, `UiKit.Caps`). `GUI.Label(string)` uses `GUIContent.Temp`, so it does not allocate.
- Only event banners (pit stop, fastest lap) build a string, once per event.
- No `GetComponent`, `Find` or LINQ runs per frame in the loop.
- Nothing needed changing here.

### People

- `People.AnimDue` controls how often the pit-lane people (`PeoplePits`) are animated:
  - every frame within 30 m;
  - every 2nd frame to 60 m;
  - every 4th frame to 120 m, staggered per person;
  - beyond that, the last pose holds.
- Before: every frame to 40 m, then every 2nd frame to 120 m. That is about half the clip sampling and bone writes for the people 60-120 m away.

### Load time

| Step (pure C#, all 11 circuits) | Before | After |
|---|---|---|
| `HeightField` terrain grid (every 5 m cell × every 8 m track point) | 2041 ms | **779 ms** on 2 cores (2.6x); about 4-6x on a 4-8 core phone |
| 300k ground samples (stand-in for terrain/scenery draping) | 1019 ms | **327 ms** |

- **HeightField grid**:
  - Blocks of 8×8 cells that are surely more than 382 m from the circuit are filled straight with the base level. This is exactly what the formula gives there: u = 1.
  - The other rows run in `Parallel.For`.
  - The grid is cached on disk per circuit (`LoadCache.cs`, `persistentDataPath/gen`, key = hash of the sampled points + `GridVersion`, at most 24 files, about 1 MB each). A second load of the same circuit skips the grid.
- **LightBake** per-vertex bake: meshes of 4096+ vertices are baked in parallel chunks of 2048 vertices.
  - Each vertex only reads the roof map, height field, track and lamps. The colours are set on the main thread.
  - The only difference: each chunk starts its `Track.Nearest` search afresh, which can only make it more exact.
  - It logs `LightBake: N vertices in X ms (cores)`.
  - Expected: 1-3 s → about 0.3-0.8 s on a 4-core phone.
- **EnvTex** procedural textures (`Albedo`): the pixel functions run in parallel rows. The mean is still summed in pixel order, so the textures are bit-identical. They were already made once per run and uploaded non-readable.
- **RoadShadow**: the mask read-back runs in parallel rows. The triangle rasterisation stays serial, because it writes a shared grid. It already logged its time.
- Not measured: Unity-side steps (mesh upload, RealWorld, ModelLib, people rigs). Read the `LightBake:` / `RoadShadow:` lines in `adb logcat -s Unity` on a device.

### Memory

- Generated textures (`EnvTex`, `RoadShadow` mask) were already uploaded with `Apply(…, makeNoLongerReadable: true)`.
- World meshes must stay readable, because `RoadShadow` reads them and static batching runs after the bake. Making them non-readable after batching belongs to the batching code (GPU owner).
- `LightBake` still writes `Color` (float4) vertex colours. A 16-bit format would save 8 bytes per world vertex. It is not changed.

### Not done (CPU/load)

- **Smooth loading progress**: the circuit is built in one frame (`GameManager.StartSession` → `WorldBuilder.Build`). A real progress bar needs the build split into stages over several frames, which touches WorldBuilder and the GPU-owned builders. It is listed as the next step. The parallel steps above shorten that frame instead.
- `RacingLine.Compute` (100-230 ms per circuit and car class here) could be cached per circuit and class the same way as the height field.
- `FormulaCar.Step` micro-optimisations (`Math.Pow` → multiplications) were not done, because they change the last bits of the player's physics.
