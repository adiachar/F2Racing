# Frame-time investigation: "same spot, different laps: 60 to 20 fps" (20 Nov 2026)

Report (Adi, Redmi Note 12 Pro 5G: Dimensity 1080 = 2x Cortex-A78 + 6x A55, Mali-G68 MC4, 2400x1080 at 120 Hz):
"While idle the frames drop and are inconsistent. Same road, same spot, same cars in front, different laps give
significantly different FPS (60 to 20)." His guess: per-frame allocations -> GC spikes.

## Round 2 (20 Nov): Adi's overlay data and what changed

Three 2-minute races with the FrameLog overlay (Redmi Note 12 Pro 5G, Mali-G68, Vulkan). Adi: "better than before".

| Lock | fps | frame ms | late | main / render / GPU ms | GC per 20 s | alloc | headroom, battery | res | display |
|---|---|---|---|---|---|---|---|---|---|
| 30 | 30.0 steady | p50 = p95 = 33.1 | 1% | ~10 / ~4.5 / **21-25** | 2-3 | ~200 KB/s | 0.78-0.84, 36.7-37.1 C | 71% x 100% | **60 Hz** |
| 40 | 26.5-30 | p95 49.6 | **100%** | - / - / ~20 | 2-4 | ~190 KB/s | 0.83-0.92 | 71% x 85% | **60 Hz** |
| 60 | 48.8-60 (most rows 56-60) | p50 16.6, p95 16.6-33.1, **max 99-115** | 0-18% | 9-14 / - / **12.5-14.5** | 3-4 | **290-366 KB/s** | **0.92-0.97**, 38.8-39.5 C | 71% x 75% | 60 Hz |

Thermal status 0 throughout, CPU 0.65 / 2.60 GHz, ~425 draws, ~354k triangles.

**Findings**
1. **The panel stays at 60 Hz in the game** (MIUI keeps games it does not list at 60 Hz), so 40 can never be even: Unity
   rounds a 40 target down to 30 on a 60 Hz panel (every frame 33 ms = "late 100%"). The 40 run was not a GPU problem.
2. **60 is affordable**: the GPU needs 12.5-14.5 ms at 71% x 75% (Auto's one resolution step). The GPU clocks down at
   a 30 lock, so its 21-25 ms there is not comparable. The cost of 60 is heat: headroom 0.92-0.97 (close to throttling).
3. **Garbage: 190-366 KB/s = a collection every 5-7 s, and it was mostly IMGUI text, half of it the overlay itself.**
   Read in Unity 6000.0's C# reference (Modules/IMGUI): `GUI.Label(Rect, string, style)` calls `GUIContent.Temp`, whose
   text setter does `text + "​"` = **a new string on every label draw**, every OnGUI pass (~40 B x ~80 HUD labels =
   ~3 KB a frame, more on touch frames). Then `IMGUITextHandle.GetTextHandle` caches text by a hash of (text, colour,
   rect size, font, size); any value not drawn in the last 5 s allocates a new handle (a finalizable TextHandle, a list
   node) and, for the native mesh, `new MeshInfoBindings[]` + `new TextCoreVertex[4 x chars]` (32 B a vertex): ~1-2 KB
   per new number. Speed, delta, kW and brake temperatures changed nearly every frame. The old overlay's three ~150
   character lines changed twice a second: ~38 KB of vertex arrays each time (~77 KB/s) plus ~1.3 KB a frame of copies.
   Script code itself is still 0 B (round 1).
4. **The 99-115 ms spikes**: no screenshot of the graph markers, so not proven. Candidates, now each marked in the
   overlay: GC collections (Boehm, 42 MB heap, a collection every 5-7 s at that rate), the TextCore handle clean-up
   (every 30 s or every 500 new text handles: one native destroy per expired handle; at the old rate it ran every few
   seconds), a team radio message (1.2 MB `AudioClip.SetData` of the whole 14 s buffer), the 2 s thermal read (binder
   calls; the battery one is a sticky-broadcast query through the activity manager).

**Fixes**
- **Display mode** (Thermal.cs, GameManagerPerf.cs): while the lock is 40 the game asks Android for the panel's fastest
  mode (`WindowManager.LayoutParams.preferredDisplayModeId` + `preferredRefreshRate`, the mode with the highest rate at the
  current physical size from `Display.getSupportedModes`, set on the UI thread through one `AndroidJavaRunnable` made once);
  60 / 30 and the menus give the choice back to the system (battery). The refresh rate is read back with
  `Display.getRefreshRate` (raw JNI; it reflects MIUI's per-app override) ~2 s after asking. Still 60 Hz -> `Can120 = No`
  for this app run: a 40 lock becomes a steady 30 at once ("Screen stays at 60 Hz: steady 30 fps"), Auto never uses 40,
  and the Frame rate setting shows "40 (needs 120 Hz)". Belt and braces: a 40 window with >= 40% late frames and either
  early (16.7 ms) frames or no 120 Hz read-back is treated the same. No 120 Hz mode at all -> No without asking.
  `Surface.setFrameRate` was not used: Unity's surface is not reachable without a Java plugin.
- **Auto** (GameManagerPerf.FramePacing): starts at **60** (the "Mali starts at 40" rule is gone). Holds while late <= ~5%;
  steps only after **two failing 3 s windows in a row** (late > 8% or average < 92% of the lock), or **two thermal reads
  in a row** that say hot (status >= MODERATE or headroom >= 0.9; at most once per 30 s). The next rate skips 40 unless
  120 Hz is confirmed (Unknown: 40 is tried once and read back). Still one-way per session, per-circuit memory kept.
  On Adi's phone: 60 while cool; when headroom stays >= 0.9 -> a steady 30.
- **HUD garbage** (UiKit.cs, RaceHud.cs): `UiKit.Text` (every HUD and menu label) draws with one cached `GUIContent` per
  string object (keyed by reference; capped at 3000, then cleared) and returns early on non-Repaint events; `UiKit.Width`
  / `Height` use the same cache. Read-outs that change every frame are held (`HudHold`): speed and delta 10 Hz, kW 5 Hz,
  ERS / boost / overtake % and brake temperatures 4 Hz, gaps 4 Hz per car, tyre temperatures, fuel and laps 2 Hz, the fps
  text 2 Hz. The lap clock is drawn as "m:ss" + ".t" (the ten tenths stay cached): one new text a second instead of ten.
  RadioCaption and PerfLab's two components no longer ask for an IMGUI Layout pass (two layout groups a frame each).
- **Overlay** (FrameLog.cs): no IMGUI text at all; the five lines and the Results table are written with a 6 x 11 bitmap
  font (Pillow's built-in, HPND) into textures (twice a second / once a second) and drawn as images: **0 B** (checked:
  0 bytes in 10,000 write + upload calls). New: an allocation strip under the graph (KB per frame), KB/s, markers for the
  thermal read (orange) and radio messages (blue), long frames (> 50 ms) counted per 20 s row with their cause
  (g GC, r radio, t thermal read, p physics, ? other; a long frame's time is the previous frame's work, so both frames'
  events count), the thermal read's own time (last / max ms), the radio mix + upload time, and "120:Y/N/?".
- **Radio** (TeamRadio.cs): uploads only the message plus 0.2 s of silence, in 0.5 s pieces from one reused buffer,
  instead of the whole 1.2 MB buffer; mix + upload time measured.
- **Thermal reads**: battery temperature every 10 s instead of 2 s; each read timed.

Expected: HUD garbage from ~3-6 KB a frame to well under 1 KB a frame (estimated 10-20 KB/s in a race at 60, most of it
new speed / delta values; not measurable here: no device). Note: `GC.GetTotalMemory` on IL2CPP moves in heap-block
steps, so per-frame values are coarse; the 0.5 s and 20 s averages are the numbers to read.

**What Adi should re-test**: Frame rate = Auto and = 40, Performance graph on, a 2-minute race each; screenshot the
Results table. Look at: Hz and "120:" (40 should show N and run a steady 30, unless the phone gives 120 Hz), KB/s and GC
per row (target < 20 KB/s, GC rare), the >50ms column and its letters, and whether Auto stays at 60 until headroom
reaches 0.9.

## Summary (round 1)

The scene is the same on both laps, so whatever differs has to be **state that changes over time**: the managed heap
(GC), the simulation's catch-up work, the phone itself (clocks, temperature, display refresh rate, touch boost), the
Auto logic's own decisions, or one-off work (the first draw of a shader variant, a texture, a clip). This round
checked each one with a method that does not depend on the others, fixed what the code shows, and added an
on-device overlay that records all of them side by side. There is no phone or editor here, so the phone-side
hypotheses are ranked from the code and the measurements possible offline. **The overlay screenshots decide them.**

| # | Hypothesis | Verdict | Evidence |
|---|---|---|---|
| 1 | GC from per-frame allocations | **Unlikely as the main cause.** Script code allocates ~0 B a frame in a race (one real leak fixed: the safety-car queue); engine-side text allocation is not ruled out (measured by the overlay) | static audit of every per-frame path (below); PhysicsCheck `perf`: **0 bytes** in 43,200 ticks of the 22-car loop |
| 2 | Physics catch-up spiral | **Ruled out** (measured). Guard added anyway, dormant | 39 us per 240 Hz tick for 22 cars (29 us with AI LOD); even 6x slower on the phone it is ~12% of real time: linear, bounded |
| 3 | Thermal throttling | **Most likely main cause** (fits every detail of the report); now measured and acted on | GPU-bound phone (gpu ~20-28 ms est., PERF-ARCHITECTURE.md), Auto tried 60 first every session = GPU flat out; Dimensity 1080 / Mali-G68 throttle after minutes |
| 4 | Auto / frame pacing | **Real contributor, fixed**: Auto's graphics step changed level mid-race (keywords, shader LODs, MSAA) -> a lap of first-use hitches at new spots | read FramePacing + everything SetGraphics touches |
| 5 | First-use hitches | Contributor after any level change; otherwise lap 1 only. CarEnv re-bake, radio clip upload, debug stats refresh found | code read |
| 6 | Which thread | overlay shows main / render / GPU / present-wait ms per frame and per 20 s | FrameTimingManager (Frame Timing Stats is on in BuildScript) |
| - | "While idle": display refresh rate or touch boost | **Open, measured now**: MIUI can drop 120 Hz -> 60 Hz when the screen is not touched (a 40 lock then becomes 30, a missed 30 becomes 20); no touch also means no CPU touch boost | overlay: Hz, CPU clock and the "idle%" column |

## Method and evidence

### 1. GC / per-frame allocations

**Static audit** of every per-frame path, for: string concat / format / interpolation, ToString, LINQ, foreach over
non-List enumerables, boxing, lambdas / closures, params arrays, new arrays / lists, GUIContent / GUIStyle creation,
GetComponent / Find / FindObjectsByType, Camera.main, renderer.material(s), mesh array getters, Resources.Load,
Debug.Log, `new WaitForSeconds`.

Paths read: `GameManager.Update / LateUpdate / OnGUI` and everything they call (ReadInput, the 240 Hz loop, StepRivals,
UpdateTow, LineCheck, Rumble, LapsAndRecover, cameras incl. TV, Record, UpdateSkids, LineFrame, weather spray, audio
feed, FramePacing), `RaceHud` (all HUD text goes through per-value caches: HudNum, QualiRowText, rcDeltaText,
fpsTexts), `GameManagerRaceControl.RcFrame / RcStep`, `GameManagerProgress.ProgressTick`, `PreRace` (captions built
once at the start), `MenuUI` (results strings built once in ShowResults), `UiKit` (style / caps caches),
`TeamRadio.Update / Pump`, `EngineAudio.OnAudioFilterRead / Feed`, `RivalAudio.Update`, `DriverFeel`, `DriverModel`,
`CarModel.Feed / Apply / Sparks / Smoke / ShowDamage`, `PitCrew`, `PeoplePits`, `SkidMarks`, AI (`AiDriver.Think`,
`AiRacecraft`, `AiLod`), `RaceControl.Update`, and the gfx side's per-frame code (SectorVisibility, ModelInstancing,
CarEnv.Pick, Sky, LensFlare, TracksideLod, RoadSurface, EnvSurface, RenderScaleCheck).

| Found | Where | Rate | Status |
|---|---|---|---|
| `new List<int>` + a capturing lambda (closure + delegate) for the queue sort | `RaceControl.Update` | every AI tick (60/s) while the safety car is out or a restart runs: ~450 B x 60 = ~27 KB/s | **fixed**: list and comparison made once; same list, same sort, same order (PhysicsCheck `race sc` output identical) |
| `sharedMaterials` array per drawn renderer, keyword `string.Join` per material | `PerfStats.Refresh` (Render stats debug) | every 0.5 s, only with that overlay on | **reduced** (GetSharedMaterials into one list, keyword string cached per material). It still allocates (FindObjectsByType, the text) and walks every renderer: keep it OFF while measuring frame times |
| `Flash("Best lap! " + Fmt(...))`, banners, radio message text, pit stop flash | GameManager.LineCheck, RaceHud, TeamRadio | per event (a lap, a penalty, a message): a few hundred bytes a minute | left (not per frame) |
| TeamRadio `clip.SetData(mix)` copies the whole 14 s buffer (308,700 floats, 1.2 MB) per message | TeamRadio.Play | per radio message | left: a sub-millisecond copy, not garbage (noted under "What is left") |
| Settings / menu pages: slider value strings (`$"{v:0}°"`) per frame | GameManagerSettings via SettingsUI | per frame **while the settings page is open** (game paused) | left: menus only; the garbage they make can trigger a collection later, in a race (the overlay shows when) |

Everything else in the list is clean: no LINQ, no per-frame GetComponent / Find / Camera.main allocation (Camera.main
is cached by Unity since 2020.2), no renderer.material clones, no coroutines, no Debug.Log per frame; foreach only over
List / arrays (struct enumerators); local functions in UpdateTow / ReplayStep are not turned into delegates (no
closure allocation); audio callbacks allocate nothing.

**Measured (offline):** PhysicsCheck `perf monza`: the game's 22-car physics + AI loop allocates **0 bytes** in
43,200 ticks (with and without AI LOD).

**Not ruled out:** allocations inside the engine. Unity 6's IMGUI draws text through TextCore; labels whose text
changes every frame (speed, clock, gaps) may allocate inside Unity. The overlay's `alloc KB/f` measures all managed
heap growth, engine included; PerfLab's "HUD" button (Render stats) switches the HUD off for an A/B.

### 2. Physics catch-up spiral

`Tools/PhysicsCheck: dotnet run -c Release -- perf monza` (player Nearest + WheelContact every tick; 21 rivals Think /
wall / contact / WheelContact at 60 Hz, Step at 240 Hz), x64 sandbox, 2 cores:

| | us per 240 Hz tick | per car-tick | allocated |
|---|---|---|---|
| AI LOD off | 39.0 (Think+wall 9.4, contact 0.5, Nearest+WheelContact 1.5, Step 24.7, grade 2.8) | 1.77 us | 0 B |
| AI LOD on (as the game) | 29.1 | 1.32 us | 0 B |

A spiral needs one simulated second to cost more than one real second. Even at 6x slower (an A55 little core at a
throttled clock, ~250 us/tick) plus the same again for the game-side work in the loop, one simulated second costs
~120 ms: 12% of real time. A 50 ms frame runs 12 ticks (~6 ms), the 0.1 s dt cap 24 ticks (~12 ms): physics makes a
slow frame somewhat slower but cannot run away.

**Guard added anyway (dormant):** the step loop stops early only after at least 8 steps AND more than 20 ms of
physics wall time in one frame; the remaining whole steps of that frame are dropped (the game slows for that frame,
like the existing 0.1 s cap). The 240 Hz step is unchanged; PhysicsCheck calibration identical. The overlay counts
any drop ("drops", white marker), so we will know if it ever fires.

### 3. Thermal throttling

Why it fits: the phone is GPU-bound (PERF-ARCHITECTURE.md: ~20-28 ms GPU per frame after the render scale and rival
LODs). Auto started **every session at 60**: a 16.7 ms target the GPU cannot meet, so for at least 3 s per session
the GPU ran flat out, then held 40 (25 ms, little headroom). The Dimensity 1080 / Mali-G68 drop their clocks after a
few minutes of that; a GPU frame of 22 ms becomes 35-45 ms, which on a 120 Hz panel shows as 25-24 fps or (missing
the 30 lock) 20 fps. Clocks recover when the load drops (menus, results), so each race starts fast and degrades:
"same spot, different laps".

Now measured on the phone (Thermal.cs, every 2 s, no allocation per read): Android thermal status (API 29+),
thermal headroom with a 10 s forecast (API 30+; 1.0 = where SEVERE throttling starts), battery temperature, SoC
temperature, the fastest core's clock vs its maximum, the GPU clock (where the phone exposes them), the display's
refresh rate.

**Fix (Auto only; "60 / 40 / 30 fps" settings untouched):**
- Mali-G5x / G6x with a 120 Hz panel: Auto starts at the steady 40 (gfx request #8), not 60.
- When the phone reports MODERATE or a headroom >= 0.9, Auto steps to the next steady rate at once (at most every
  30 s), before the clocks collapse ("Phone is warm: steady 40 fps").

### 4. Auto / frame-pacing logic (GameManagerPerf.FramePacing)

- Lock steps 60 -> 40 -> 30: one-way per session, cheap (only Application.targetFrameRate). Fine.
- **Graphics step mid-race (fixed):** step 2 called `SetGraphics(level - 1)` in the race. That switches at once the
  global keyword `F2_GRADE_LOW` (every world shader variant changes), CarPaint and Road `maximumLOD` (another
  SubShader), anisotropic filtering (TracksideLod hides renderers, the glass keyword flips) and MSAA 4x -> 2x (the
  screen buffer is recreated; on Vulkan the sample count is part of every pipeline state). Unity creates a variant's
  GPU program / pipeline state the first time it is drawn, so every object hitched the first time it came into view
  after the change: a lap of hitches at new places. Now the step is held and applied when the next session starts
  (before its world is built, behind the loading screen); the flash says "Lower graphics from the next session".
  Within the session Auto goes on to its resolution step as before.
- Resolution step (one per session, Vulkan dynamic resolution): kept.
- Per-circuit memory (`fpsMem`): correct (highest rung reached per circuit in this app run; forgotten on any
  graphics change, and now also when Auto lowers the level). It is conservative: a circuit where a hot phone fell to
  30 starts at 30 for the rest of the app run.
- FpsRungs are read once from `Screen.resolutions`; if MIUI runs the panel at 60 Hz, a 40 lock is rounded down to
  30 by Unity. The overlay's Hz column shows if that happens.

### 5. First-use and periodic work

- Shader variants / pipeline states: created on first draw (above). On Vulkan, Unity 6.0's manual says
  `Shader.WarmupAllShaders` / `ShaderVariantCollection.WarmUp` "aren't recommended on Vulkan, DirectX 12, and Metal"
  (they can create the wrong PSOs); the 6.0 tool is the experimental `GraphicsStateCollection` (trace on a device,
  warm up at load). Request to gfx, only if the overlay shows unmarked red spikes in lap 1.
- CarEnv re-bakes every reflection cube (6 world renders per cube, one cube per frame, up to 22 frames, sector
  culling lifted) on every `Sky.SetWeather`, which `StartSession` calls every session even when the weather did not
  change. It falls in the pre-race intro; request to gfx: skip when the wetness is unchanged.
- TeamRadio: 1.2 MB SetData per message (above).
- PerfStats debug overlay: a CPU spike every 0.5 s and a bigger one every 5 s (texture / mesh walk) while it is on.
- SectorVisibility toggles only the renderers whose state changes, once per 50 m sector: cheap. PreRace and
  MenuStudio objects are destroyed / released at session start (ReleaseStudio, PreRace.Finish).

### 6. Which thread

The overlay shows FrameTimingManager's main thread, render thread and GPU ms and the main thread's wait for present.
Reading: if `gpu` is close to the frame time and `wait` is large, the GPU is the limit (expected); if `main` is close
to the frame time, the CPU (scripts: `phys`, HUD). Rising `gpu` at the same spot lap after lap, with the GPU / CPU
clocks falling or `therm` / `head` / `soc C` rising, is throttling.

## What was built

| File | What |
|---|---|
| `Game/FrameLog.cs` (new) | the overlay (Settings > GRAPHICS > "Performance graph (debug)", off by default): 5 s frame graph with GC / physics / guard markers, 3 lines of numbers, and the 2-minute table on the race Results screen. No allocation per frame (preallocated rings; text written in place into fixed strings via a Span over the string, checked: 0 bytes in 100k writes) |
| `Game/Thermal.cs` (new) | thermal status, headroom, battery / SoC temperature, CPU / GPU clocks, display Hz: raw JNI with cached method ids and argument arrays, sysfs files kept open and re-read from offset 0 (checked: 0 bytes in 1000 reads) |
| `Game/GameManager.cs` | Update: Thermal.Tick, FrameLogFeed, physics steps / time, spiral guard; StartSession: deferred Auto graphics step, FrameLog.SessionStart; Start: FrameLog.Ensure |
| `Game/GameManagerPerf.cs` | Mali 40 start, thermal step, FpsStepDown, deferred graphics step (AutoGfxAtSessionStart), guard constants, FrameLogFeed |
| `Game/GameManagerSettings.cs` | the "Performance graph (debug)" row; Frame rate help text |
| `Physics/RaceControl.cs` | safety-car queue sort without garbage (allocation only; PhysicsCheck `race sc` and calibration identical) |
| `Game/PerfStats.cs` (shared, small) | less garbage per refresh |

Checks: C# syntax check OK; FrameLog.cs + Thermal.cs compiled (both the Android and the other branch) against stubs
with the Unity 6.0 signatures; PhysicsCheck calibration identical; `race sc` identical. Unity 6.0 APIs used:
`FrameTimingManager.IsFeatureEnabled / CaptureFrameTimings / GetLatestTimings`, `FrameTiming.cpuMainThreadFrameTime /
cpuRenderThreadFrameTime / gpuFrameTime / cpuMainThreadPresentWaitTime`, `ProfilerRecorder` "Draw Calls Count",
"Triangles Count" (Render) and "GC Reserved Memory" (Memory), all available in release players per the 6.0 counters
reference ("GC Allocated In Frame" is NOT available in release players, so allocation is estimated from
`GC.GetTotalMemory` growth between frames without a collection, and collections from `GC.CollectionCount(0)`),
`GarbageCollector.isIncremental`, `Screen.currentResolution.refreshRateRatio`, `AndroidJNI.GetMethodID / CallIntMethod
/ CallFloatMethod / CallObjectMethod / ExceptionOccurred / ExceptionClear`, `AndroidJavaObject.GetRawObject /
GetRawClass`.

## What Adi should do (and screenshot)

1. Settings > GRAPHICS: **Performance graph (debug) = On**, **Render stats (debug) = Off**, Frame rate = Auto.
2. A race at Monza, 5+ laps, rivals on. At the spot where it used to drop, screenshot the top-left graph on lap 1
   and again on the lap where it is slow.
3. Once, stop driving and do not touch the screen for 20 s: screenshot (the Hz and cpu numbers).
4. Screenshot the **Results** screen (the 2-minute table appears over it).
5. Optional A/B: the same with Frame rate = 60 fps.

How to read it: `gpu` rising and `cpu/gpu` clocks falling or `therm`/`head`/`soc C` rising across rows = heat.
`Hz` 60 instead of 120 when idle = the phone's refresh-rate saver. Magenta markers on red bars, `GC` rising,
`KB/s` > 0 = garbage collection. `phys` > 3 ms or cyan markers = physics. Red spikes with no marker in one lap
after a setting / Auto change = first-use hitches.

## What is left / next

- **Read the screenshots**, then: if heat, the next lever is a lighter sustained GPU budget on Mali-G68 (request to
  gfx below); if Hz drops when idle, ask Android for 120 Hz (`Screen.SetResolution(w, h, mode, RefreshRate)` /
  preferred display mode) while racing; if `alloc` > 0 with the HUD on and ~0 with PerfLab's HUD switch off, the HUD
  off IMGUI (one cached mesh + font atlas) moves up; if lap-1 spikes, PSO warm-up.
- TeamRadio: stream the mixed message from a PCM reader callback instead of a 1.2 MB SetData per message.
- Render-side requests to gfx: Docs/COORDINATION.md (20 Nov).
