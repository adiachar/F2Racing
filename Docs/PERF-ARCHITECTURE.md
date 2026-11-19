# Performance architecture: why 20-23 fps, and how 60 fps mobile racers are built (Nov 2026)

Phone: Redmi Note 12 Pro 5G. Dimensity 1080 (2x Cortex-A78 + 6x A55), Mali-G68 MC4 (4 shader cores), panel
2400 x 1080 at 120 Hz. The owner sees 20-23 fps in a race, **on the Low graphics level too**.

Short answer: the game is **GPU-bound**, and the two biggest costs are things that are the same on every graphics level:
it draws **every pixel of a 2.6 Mpx panel**, and **the cars are 2/3 of the triangles in every frame**. The static world
is not the main problem: it is already built as a corridor with a backdrop and culled per track sector (numbers below).

## 1. What the measurements say

WebGL shooter (`Tools/Shots/shoot.py --param stats=1`, the `gfx` build of 19 Nov 10:45, Monza, 12 rivals, chase and
cockpit cameras, 6 lap positions). Triangle and draw counts do not depend on the browser, so they hold on the phone.

| Monza | draw calls | SetPass | triangles / frame | cars + people ("other") | static world | world, frustum only (no sector sets) |
|---|---|---|---|---|---|---|
| High, s=300 chase | 129 | 57 | 873k | **580k (66%)** | 280k | 290k |
| High, s=850 chase | 116 | 55 | 793k | **580k (73%)** | 205k | 212k |
| High, s=1450 chase | 106 | 56 | 735k | **567k (77%)** | 163k | 170k |
| High, s=2500 chase | 108 | 53 | 655k | **528k (81%)** | 124k | 124k |
| High, s=3920 chase | 130 | 63 | 825k | **587k (71%)** | 234k | 239k |
| High, s=5150 chase | 99 | 54 | 626k | **501k (80%)** | 120k | 120k |
| **Low**, s=300 chase | 98 | 49 | 707k | **451k (64%)** | 243k | 247k |
| **Low**, s=850 chase | 97 | 48 | 649k | **451k (70%)** | 190k | 196k |
| **Low**, s=1450 chase | 88 | 47 | 596k | **438k (73%)** | 153k | 159k |

- **Draw calls are fine** (90-130, SetPass ~50): that is inside the 100-300 budget of mobile racers. The render thread is
  not the problem.
- **Cars are 64-81% of the triangles**, on Low too. A rival is drawn with the same mesh at any distance: car_far
  (~25k triangles drawn) from 52 m to the horizon, car_lod (~56k) within 52 m; the player's car_lod 65k (Low/Medium
  chase), full car 190k (High, cockpit). A car 150 m away is ~25 px long: ~100 triangles per pixel. With 21 rivals on
  the grid the cars alone are ~0.8 M triangles on Low.
- **The world is ~120-280k triangles**, and "frustum only" vs "drawn" differs by 0-5% (the sector sets already cut what
  is behind hills and sub-pixel). The owner's idea "we draw things nobody can see" is right for the cars, not much
  for the world: see section 4.
- The overlay's "card overdraw" number (37-670 "screens") is not usable as it is: it sums the full area of every card
  cluster in the frustum by 1/z^2 with no clipping, so one fence cluster a metre from the camera explodes it. Treat
  alpha cost from the A/B switches instead (PerfLab, section 5).

## 2. The ranked causes of 20-23 fps (estimates for this phone, Low, chase, ~15 rivals)

The frame-rate lock explains the exact number: on a 120 Hz panel a frame shows for a whole number of 8.3 ms refreshes,
and Auto drops the lock 60 -> 40 -> 30 (GameManagerPerf.FramePacing). A frame that needs 34-42 ms shows at 41.7 ms
(24 fps), 42-50 ms at 50 ms (20 fps). **20-23 fps = the GPU needs ~40-48 ms per frame.** To hold 30 we must get under
33 ms; to hold 40, under 25 ms.

| # | Cause | Where | Est. GPU / CPU ms per frame | Same on Low? |
|---|---|---|---|---|
| 1 | **Native 2400 x 1080 rendering** (2.6 Mpx; 25% more than a 1080p TV). Every pixel pays the road / ground / grade shader, MSAA, aniso, alpha-tested foliage and fences. | Unity default on Android (no render scale); the "Resolution scale" setting is 100% by default; Auto's one resolution step only exists on Vulkan and only after everything else failed | **12-20 ms GPU** (about half the fragment work at 0.71 scale) | yes |
| 2 | **Car triangles**: ~0.45-0.8 M per frame. Mali bins every triangle and writes its varyings to memory (IDVS), and micro-triangles shade 2x2 quads that are mostly empty | `CarModel` merged rivals, one mesh at every distance | **5-9 ms GPU** | yes |
| 3 | **Alpha-tested foliage and fences** (discard / alpha-to-coverage). On Mali these force late Z: no early-Z kill, no Forward Pixel Kill, so every stacked card layer is shaded ([Arm: hidden surface removal in Mali GPUs](https://developer.arm.com/community/arm-community-blogs/b/mobile-graphics-and-gaming-blog/posts/hidden-surface-graphics-removal-methods-in-mali-gpus)). Monza's park canopy and the catch fences cover 30-50% of the screen | Foliage.cginc, BakedCutout | 4-8 ms GPU at native (half at 0.71) | trees yes; fences are off on Low |
| 4 | **IMGUI HUD** (~150 controls: rounded rects, text). Every OnGUI event re-runs the whole HUD; touch moves (the steering thumb) add events every frame. Each control is a draw call on the render thread | GameManager.OnGUI, RaceHud, UiKit | **2-5 ms main thread + 1-2 ms render thread** | yes |
| 5 | **Anisotropic filtering forced on every texture** on Medium/High (`ForceEnable`, minimum 4x), including the tree atlas, buildings and people, at 2.6 Mpx | GameManager.SetGraphics | 1-3 ms GPU | no (Low: off) |
| 6 | **Uncompressed RGBA32 textures** (41 MB, all runtime-generated). 4x the texture bandwidth / cache footprint of ETC2/ASTC | EnvTex, TracksideArt, WorldBuilder | 1-3 ms GPU (bandwidth) | yes |
| 7 | MSAA 4x (Medium/High) / 2x (Low). On Mali the samples live in tile memory and resolve on chip: cheap per sample, but with alpha-to-coverage it is part of #3 | QualitySettings.antiAliasing | 1-3 ms at native (4x) | 2x |
| 8 | Physics: 22 cars x 240 Hz; at a 30 fps lock 8 steps per frame. Tools/PhysicsCheck: ~1.9 us per car-step on x64; A78 ~2-4x slower | GameManager.Update loop | 1.5-3 ms main thread at 30 fps | yes |
| - | Checked and **not** a cause: shadow maps (off on every baked circuit), reflection probe (rendered once), cameras (one in a race; the menu studio camera is destroyed at session start), HDR (off), post-processing (none), soft particles (default Android quality has them off), GC (incremental, ~0 B per frame in a race), vSync / targetFrameRate (vSyncCount 0 + targetFrameRate divisors of 120 Hz with Swappy: correct) | | | |

Main thread total ~8-12 ms and render thread ~5-8 ms: neither limits a 30 fps frame. The GPU (#1-#3, #5-#7) does.

## 3. What was changed now (graphics account files, no physics change)

| Change | File | Expected gain |
|---|---|---|
| **Base render scale on Android**: the screen buffer is set to a ~1.3 Mpx budget at start-up (2400 x 1080 -> 1700 x 765, 0.71; 1600 x 720 phones stay at 100%; floor 0.6). `Screen.SetResolution` on Android sizes the window surface and the display compositor upscales it: no extra pass, texture or memory. The HUD lays out from Screen.height, so it keeps its size (text a little softer). The existing "Resolution scale" setting and Auto's resolution step multiply on top. PlayerPrefs `renderBase` (0 = Auto, else %). | `RenderScale.cs` (new) | **~45-50% of the fragment work: 12-20 ms GPU on this phone**; the single biggest win |
| **Rival level of detail**: the merged rival meshes get simplified levels at load (vertex clustering per part ID and facing; cell under ~0.6 px at the switch distance at 756p), switched by a LODGroup (screen height, so the zoomed TV camera keeps detail). car_lod: full to 36 m, then ~2x fewer triangles; car_far: full to 75 m, 6 cm grid (~3x fewer) to 170 m, 12 cm grid (~6x fewer) beyond. Wheels keep spinning / steering on near rivals; a car with a broken wing shows its full mesh; livery, lights and instancing unchanged. Check: `Docs/car-previews/rival_lod_cluster.png` (full vs simplified at the switch distance; car_far 14.8k -> 4.9k source triangles, car_lod 38.8k -> 19.1k). | `CarLod.cs` (new; `CarModel` made partial, 2 hook lines) | Shooter scene: ~580k -> ~340k car triangles; a race with 21 rivals spread round the lap: ~0.5 M -> ~0.15 M. **3-6 ms GPU** |
| **On-device measurement**: CPU / main / render thread / GPU ms (FrameTimingManager through its ProfilerRecorder counters, which switch it on in a release player too), script Update and OnGUI time (Stopwatch brackets, no allocation), screen size, base scale, native size, MSAA, shadows, vSync, target fps, graphics API, drawn world triangles by distance (<150 / <400 / <1000 m / beyond), triangles outside a 250 m corridor round the track, and the 6 biggest drawn world groups (name / shader) | `PerfStats.cs`, `PerfLab.cs` (new) | debug only, off by default |
| **PerfLab A/B switches** (one tap each, under the overlay): Trees, Fences, Grade, HUD (needs the lead's hook), Particles, Rivals, Car LOD, Shadows, World, Sky, MSAA (4 / 2 / off), Res (Auto / 100 / 85 / 70 / 60 %) | `PerfLab.cs`, `ModelInstancing.Off` | finds the culprit on the phone in a minute |

Expected together on this phone, Low: ~40-48 ms -> **~22-28 ms**: a steady 30 at worst, 40 most of the time
(Auto will try 60 first, then settle on 40 with "40 FPS LOCK"). Medium should hold 30-40. These are estimates;
the overlay now shows the real GPU ms.

Not changed (no way to check the look here, or owned by the lead): far foliage as opaque impostors, texture compression
(ETC2/ASTC of the EnvTex atlases: needs an A/B look at the road grain), the HUD rewrite, the aniso setting.

## 4. The corridor question ("are we drawing what nobody can see?")

What the code already does (read from RealWorld.cs, Environment.cs, SectorVisibility.cs):

- **Real-world circuits (Monza)** are already a corridor + backdrop: trees only within `TreeReach` of the track (450 m
  Low, 700 Medium, 900 High), woods only to 32 m deep from their edge (`BandAt + 6`) with two layers of wood-band cards
  standing in for the depth, buildings within 900 / 1400 / 1800 m, and beyond 0.78 x TreeReach to the fog's end one
  opaque far tree-line silhouette mesh (`FarTrees`). Blocks of one terrain chunk (512-768 m) with LODGroup distance
  cut-offs.
- **Procedural circuits** have distant backdrop rings (`EnvRing`, 430-980 m, opaque hill / tree bands) instead of a
  modelled far landscape.
- **Per-sector view sets** (50 m sectors) drop what is under a pixel or behind the terrain from the whole sector. The
  shots show "frustum only" vs drawn differing by 0-30 renderers and 0-5% of the world's triangles, because what is
  left is mostly near.

What is not done: occlusion by the **tree line or the stands** (only terrain occludes). That is where the remaining
invisible geometry is: buildings and park furniture behind the Monza woods, the rear sides of stands. The new overlay
counters (`wdist` and `offcorridor`, plus `top`) measure it per position on the phone or in the next CI WebGL build.

**Estimate today**: world triangles are 15-35% of the frame; of those, perhaps a third is behind the tree line or
beyond 250 m. So a perfect corridor would cut ~5-10% of the frame's triangles and very few pixels (distant things are
small). That is real but much smaller than the render scale (~45% of the pixels) and the car LOD (~25-40% of the
triangles). The High-only undergrowth and fence-foot grass added this week are small in the counts (the instanced
part is ~1-7k triangles) and are not on Low, where the owner measured 20-23 fps; they stay until the overlay shows them
costing something.

**Plan (next round, measured first):**
1. Read `offcorridor` / `top` / `wdist` at 6 Monza spots and 2 procedural circuits (next WebGL build + the phone).
2. *Tree-line occlusion in the sector sets*: add the woods (RealWorld `gEff` depth > 30 m, canopy height) and the
   stands' back walls as occluders in `SectorVisibility`'s horizon profiles (they are height fields seen from the
   track, like the terrain). Buildings and props behind a wood or a stand then drop out of the sector's set.
3. *Tighter reach on Low/Medium*: buildings to 600 / 900 m (the fog takes most of their contrast there), trees to 350 m
   on Low; beyond that the existing `FarTrees` silhouette and the `EnvRing` backdrop.
4. *One backdrop ring per circuit* (1-2 draw calls): a cylinder at ~1.5-2 km with 2-3 parallax layers (far hills,
   tree line, skyline) painted at load from what is there (render the far world once from the circuit centre into a
   1024 x 128 strip per layer, opaque except the top edge). Then everything beyond the corridor (far terrain, far tree
   lines, far buildings) is not built at all. This is the Real Racing 3 / GRID approach.

## 5. Finding the culprit on a phone (PerfLab)

Settings > GRAPHICS > Render stats (debug). The overlay shows `ms cpu= main= render= gpu=`. If `gpu` is the biggest
and close to the frame time, the GPU is the limit (expected). Then tap the red/green buttons one at a time and watch
`gpu`: `Res 60%` (pixels), `Rivals` (cars), `Trees`, `Fences`, `World`, `MSAA`. The one that drops `gpu` most is the
culprit. `scripts update=` is our Update work (physics, AI, cars); `ongui=` all IMGUI (HUD, menus).

FrameTimingManager in Unity 6000.0: always on in Development players; in a release player it needs "Frame Timing Stats"
(`PlayerSettings.enableFrameTimingStats`) **or** a ProfilerRecorder on one of its counters, which switches it on
([Unity 6 manual: Frame Timing Manager](https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/frametimingmanager)).
PerfLab uses the ProfilerRecorder counters ("CPU Total Frame Time", "CPU Main Thread Frame Time", "CPU Render Thread
Frame Time", "GPU Frame Time"; ns) and falls back to `FrameTimingManager.GetLatestTimings`; the BuildScript line is
requested anyway (COORDINATION.md) so the fallback works too. GPU time can read 0 / n/a on drivers without timer
queries (WebGL always).

## 6. How the 60 fps mobile racers do it (research)

Public technical detail on Firemonkeys' Mint engine is thin (EA's own pieces mention per-pixel car shadows under trees
and spherical-harmonic night lighting, nothing on budgets: [EA: Under the hood of Real Racing 3](https://www.ea.com/news/under-the-hood-how-real-racing-3-gets-its-motor-running)).
What follows is the common practice of RR3 / GRID Autosport mobile / Asphalt-class games, as known from their
platform talks, Arm / Qualcomm / Unity mobile guidance and frame captures, with what each costs.

| Technique | How they do it | Cost / why | This project |
|---|---|---|---|
| Baked lighting | Light maps or vertex light for the whole track; only the cars get dynamic light (a probe / SH + one sun) | per-pixel lighting of the world is nearly free | yes (LightBake, vertex light) |
| Draw-call budget | 100-300 per frame, track in sector chunks, cars merged | each draw ~10-30 us CPU on GLES, less on Vulkan | 90-130: good |
| **Render resolution below native** | 3D at 0.6-0.8 of the panel on high-dpi phones, HUD at native or scaled; a 2400 x 1080 panel is rarely drawn at full size | fragment cost scales with pixels: 0.7 scale = ~half the cost | **was missing; now RenderScale** |
| MSAA vs resolution on Mali | 2x-4x MSAA at a lower resolution beats native without AA: samples stay in tile memory and resolve on chip; what costs is extra pixels | 4x ~5-15% of a frame on Mali; native res +100% | keep 4x/2x, lower the resolution |
| Texture atlases + ASTC / ETC2 | few big atlases, compressed (ASTC 6x6 ~3.6 bpp, ETC2 8 bpp vs RGBA32 32 bpp) | bandwidth and cache: compressed textures are ~4-8x cheaper to read | atlases yes; **runtime textures uncompressed** (next step) |
| LOD and impostors | 3-4 LODs per car (a far car is a few thousand triangles), impostor cards for far trees and crowds | triangles on Mali cost binning bandwidth; micro-triangles waste quads | world: yes; **cars: added now** |
| Occlusion / sector visibility | pre-computed visible sets per track sector (PVS) from the racing surface | removes hidden pieces at zero run-time cost | yes (terrain only; tree line next) |
| Shader ALU | simple world shaders (one or two texture reads, vertex fog, light from vertex/lightmap), detail only near; the car is the only rich shader | 2.6 Mpx x 100 ALU is most of a Mali-G68's frame | grade 20 ALU / pixel, haze per vertex (already); road / ground 2-5 reads |
| Alpha test | avoided at distance: opaque impostors, solid tinted strips for far fences, alpha only near | on Mali alpha test / A2C force late Z: no hidden-surface removal for the layers behind | fences off on Low; far trees are still cards (next step) |
| Real-time shadows | none for the world (baked); a blob or projected shadow for cars | a shadow map pass draws every caster again | none on baked circuits |
| Physics cost | fixed-step physics on the main thread, a few hundred Hz for the player, AI cars simplified | ~1-3 ms | 240 Hz all cars, LOD for far rivals: fine |
| Thermal | a fixed 30 or 40 fps target chosen per device class, not 60 that drops; quality tiers per GPU | a phone that runs hot clocks down after 5-10 min: a stable 40 beats a 60 that falls to 35 | steady divisors of 120 Hz, one-way Auto: good |

Unity built-in pipeline specifics for Mali:

| Item | Cost / advice |
|---|---|
| Vulkan vs GLES | Vulkan: lower CPU per draw call, dynamic resolution (ScalableBufferManager) works; GLES3 kept as fallback. Already Vulkan first |
| MSAA 4x at 2400 x 1080 | 2.6 Mpx x 4 samples of colour + depth in tile memory; Valhall keeps 4x RGBA8 at full tile size, resolve on chip: ~1-3 ms. Native resolution is the expensive part, not the samples |
| Screen.SetResolution / render scale | the cheapest scale on Android: a smaller surface, the compositor upscales for free. The built-in pipeline has no camera render scale; a camera target texture costs an extra full-screen blit (~1 ms) |
| FrameTimingManager | gives CPU / main / render / GPU ms on the device; see section 5 |
| SRP Batcher | not in the built-in pipeline; static batching + GPU instancing instead (done) |
| Static batching memory | a second copy of every batched vertex (here ~66-86 MB of mesh memory) |
| GPU instancing | pays only for repeated meshes of hundreds+ vertices (cars, model trees): done |
| IMGUI | every control is a draw call and its layout runs for every event (Layout skipped here via useGUILayout = false); 150 controls ~2-5 ms CPU on a phone. A cached mesh HUD (uGUI or one dynamic mesh) is ~0.3 ms |
| Physics.autoSimulation | the game has no PhysX bodies (own physics); Unity's PhysX step still runs each FixedUpdate if there are colliders: set `UnityEngine.Physics.simulationMode = SimulationMode.Script` (lead request) |

## 7. Thermal and the frame-rate lock

The lock (GameManagerPerf.FramePacing) is right in principle: only rates the 120 Hz panel divides (60 / 40 / 30), one
way down per session, measured in 3 s windows. Two recommendations (lead account):
- With the render scale and car LOD this phone should hold 40 on Low/Medium. A stable 40 is better than a 60 that
  throttles after 5-10 minutes: start Auto at **40** on phones with a Mali-G5x/G6x MC4-class GPU (or after the first
  session that settled on 40), and keep 60 for faster GPUs.
- Below 30 nothing else happens on a fixed graphics level; Auto's resolution step exists only on Vulkan and comes last.
  Since the base scale now handles the panel size, that is acceptable.
