# Graphics account — handover (28 Nov 2026)

Use this to start a new chat on the graphics work of F2Racing with no other context. Read it all, then read
`CLAUDE.md` and `Docs/COORDINATION.md` in the repo before touching anything.

## 1. The project in one minute
- **Game:** a mobile F1-style racing game (Android + WebGL). Owner: **Adi** (timezone Asia/Calcutta). Repo:
  https://github.com/adiachar/F2Racing (clone to `/home/claude/f2racing`).
- **Engine:** Unity 6000.0.85f2, built-in render pipeline. **The whole world is built from C# at runtime** (track,
  terrain, trackside, sky, lighting bake). There is no scene editing. `Assets/Editor/BuildScript.cs` sets up the
  cloud build. **Adi has dropped the Unity editor**; don't suggest it again. Everything is done in code and checked
  with cloud builds plus WebGL screenshots.
- **Goal:** Real Racing 3-level graphics or better, GRID-level handling, smooth on Adi's **Redmi Note 12 Pro 5G**
  (Mali-G68, 120 Hz). FPS is good right now; **don't make a race heavier.**
- **Made-up names only:** no real teams, brands or logos (this includes trackside banners).
- **Two Claude accounts work at once:**
  - **Lead account** (branch `main`): gameplay, game loop, cameras, UI/HUD/menus, AI, audio, frame pacing, CI.
  - **Graphics account** (you, branch `gfx`): shaders and visuals.
  - File ownership is listed in `CLAUDE.md`. Edit only your own files. Ask the lead in `Docs/COORDINATION.md`.

## 2. Graphics-owned files (yours)
- Shaders and cginc in `Assets/Resources/` (not `People/`). Main ones:
  - `Road.cginc` / road shader
  - `Baked.shader`: world, vertex-colour light, keywords F2_LAND / F2_ATLAS / F2_GLASS / F2_CROWD / F2_PEOPLE
  - `CarPaint.shader`, `CarBlob.shader`
  - sky, terrain, trees
- `EnvTex.cs`, `RoadSurface.cs`, `WorldBuilder.cs`, `LightBake.cs`, `LensFlare.cs`, `ModelLib.cs`,
  `ModelInstancing.cs`, `Trackside.cs`, `TracksideArt.cs`, `Scenery.cs`, `Sky.cs`, `RealWorld.cs`, `CarEnv*.cs`,
  `CarLod.cs`, `RenderScale.cs`, `RoadShadow.cs`, `Tools/Models`, `Tools/Photo`.
- Shared (small edits only, note them in COORDINATION): `PerfLab.cs`, `PerfStats.cs`, `DebugShots.cs`,
  `CarModel.cs`, `Docs/PERFORMANCE.md`.
- **Physics is frozen** (`Assets/Scripts/Physics/*`). Never change it.

## 3. Hard technical rules (from CLAUDE.md and past mistakes)
- Shaders: **never `step()` or compares on `half`**; they break silently on Mali GLES3/Vulkan. Use `float`
  (e.g. `(half)max((float)x, 0.35)`).
- No per-frame allocations: no strings, LINQ, closures, `new` arrays, `GetComponent` or `renderer.material` in
  per-frame code. Use MaterialPropertyBlock.
- WebGL has no threads: use `FormulaSim.Par.For`, never `Parallel.For`. Write `System.Environment` (the game has
  its own `Environment`). `var a = 1, b = 2;` is a compile error.
- Mali early-Z: an alpha `clip()` goes only in its own keyword variant and material, render queue 2450.
- Never write secrets (Unity licence, keys) anywhere.

## 4. Git and build workflow (follow exactly)
1. Start:
   - `cd /home/claude/f2racing && git fetch origin && git checkout gfx`
   - Note: a **tag** named `gfx` also exists. Use `refs/heads/gfx` when git says "ambiguous".
2. Before every commit:
   - `git merge origin/main` into gfx (the lead merges gfx into main; rebasing is also fine).
   - Re-read `Docs/COORDINATION.md` for requests to graphics.
3. C# syntax check (installs with `apt-get install -y dotnet-sdk-8.0`), then delete its build output:
   - `cd Tools/SyntaxCheck && dotnet run -c Release -- ../../Assets`
   - then `rm -rf bin obj`
4. Commit:
   - End the message with the attribution lines the session asks for.
   - Add one dated line to the **Done log** in `Docs/COORDINATION.md`.
5. Push: `git push origin refs/heads/gfx:refs/heads/gfx`
6. Cloud build: `gh workflow run build.yml -R adiachar/F2Racing --ref gfx`
   - Find the run: `gh run list -R adiachar/F2Racing --branch gfx -L 1`
   - It takes ~25–30 min (WebGL then Android).
7. Check the logs (shader errors do NOT fail the build):
   `gh run view <id> -R adiachar/F2Racing --log | grep -iE "Shader error|Invalid Bytecode|error CS"`
   - If Android fails with "Gradle plugin not found", it's transient: `gh run rerun <id> --failed`.
8. Adi's APK: https://github.com/adiachar/F2Racing/releases/tag/gfx
   - Always give this link after a build.
   - The main build is at …/releases/tag/latest.
9. Merging to main: this sandbox can't push to `main`. The **lead account merges gfx into main**, so ask in
   COORDINATION if it's needed. Never force-push main.

## 5. Checking your work without a phone (WebGL screenshots)
1. Get the WebGL zip from the gfx release:
   ```
   id=$(gh api repos/adiachar/F2Racing/releases/tags/gfx -q '.assets[]|select(.name=="F2Racing-WebGL.zip")|.id')
   gh api -H "Accept: application/octet-stream" repos/adiachar/F2Racing/releases/assets/$id > /tmp/web.zip
   ```
2. Take shots:
   ```
   python3 Tools/Shots/shoot.py --build /tmp/web.zip --out /tmp/s --track monza --cam chase,cockpit \
       --s 300,2500 --quality High [--tod morning|afternoon|evening|night] [--weather dry|damp|wet] [--param hud=1 stats=1]
   ```
3. Look at the PNGs; send the good ones to Adi. All parameters are listed at the top of `DebugShots.cs` and in
   `Tools/Shots/README.md`.

## 6. Where things stand (state at handover)
**Branch `gfx` is 7 commits ahead of `main`** (not yet merged by the lead). The last gfx build is clean (no shader
or compile errors) and is on the gfx release.

### Done recently (in that build)
- **Light direction** (Adi: "flat lighting… one side of the car bright, the other dark"):
  - `Sky.cs`: afternoon sun lowered to 40° (`Quaternion.Euler(40,-35,0)`), deeper shade (`ambGain` 0.60;
    morning 0.66), cooler sky / neutral equator ambient for the car trilight.
- **Car shadow on all sides** (Adi: "the car shadow is only behind it"):
  - `CarModel.AddSunBlob`: a soft, sun-offset blob under each car.
  - It uses `CarBlob.shader` with the new `_Alpha` property.
  - The `SunBlobFollow` component handles it, gated like `SunShadowFollow` (off at night and in rain).
- **Used / worn track** (Adi: "should feel used… kerbs muddy / darker"), in `Road.cginc` (HQ path only):
  - repaired patches with wobbly edges and sealant seams
  - tar snakes
  - aged, rubbered and grimy kerbs
  - dirtier road edges
  - a darker racing line
- **Fans behind the catch fences on every circuit**, including real-world maps:
  - `Trackside.cs` "fans behind the catch fences" section, `Spectators()` card function, `peopleL` layer.
  - Texture: `Assets/Resources/Photo/crowd_standing.png` (2048×512, 2 bands, made by `Tools/Photo/pack_standing.py`
    from Rocketbox people in Adi's asset pack; MIT licence, credited in `Photo/CREDITS.txt`).
  - Drawn on the `Baked.shader` F2_PEOPLE cut-out, queue 2450.
  - Groups: High 26, Medium 14, Low none; street circuits keep their balconies.
- **More banner bridges** ("boxy thing above the road"): 5 per lap on High, 3 on Medium (`want2` in Trackside).

### Not yet verified
Adi hasn't commented on the new lighting, fans or used look yet. **Next step:** take WebGL shots of Monza plus one
other circuit to check them, then ask Adi how it looks on the phone.

### Still open from Adi's last big request (27 Nov)
- More trackside variety on **all** circuits: more materials, props, boards with random made-up brand names
  (`TracksideArt.cs` has the brand list and art).
- Make it feel "lived in".
- Realistic human characters are being made separately; they will later replace the cut-out fans and crowd cards.

### Decided / rejected (don't redo)
- **GT cars:** Adi uploaded 3 GT car .glb files, then said **"let's not add GT"**. All GT work was removed.
- **Render resolution:** Adi rejected the 1.1 Mpx render-scale change and the rival vertex-lighting change. The
  older resolution and lighting were restored ("resolution was never the cause").
- **Car shadow:** Adi rejected a hard planar silhouette shadow (20 Nov); the soft sun blob is the compromise.
- **FPS drops:** these came from the MIUI phone (refresh / touch-boost / heat), not from graphics. Opening and
  closing settings brings 60 back. That's the lead's area (frame pacing).
- **Changing time of day** rebuilds the track, so it takes a few seconds. It is **not a crash**.

## 7. Useful background
- **Lighting:**
  - Vertex-colour light is baked by `LightBake.Apply` (0.5 = full light).
  - The player car gets the `_CarLit` global; rivals use `LightBake.Sun(x,z,y)`.
  - Per-time-of-day sun, ambient and bake gain live in `Sky.SunAt`.
- **Road:** `Road.cginc` has a `ROAD_HQ` path (Medium/High) built on 113 m paving sections. `_F2RoadMotion` fades
  fine detail with speed.
- **Trackside:**
  - Built in layers of merged meshes per chunk (`SetChunk`, `Quad`, `Emit`).
  - `Free()` / `AddFoot()` footprints keep buildings and trees off placed things.
  - Monza and the other real-world maps also get buildings from `RealWorld.cs`.
- **Photo textures:** live in `Assets/Resources/Photo/`, imported as ASTC by `Assets/Editor/F2TextureImport.cs`, and
  loaded with `EnvTex.PhotoTex(name)`.
- **Map editor** (`MapEdits.cs`, `Docs/MAP-EDITOR.md`): built for Unity editing, now unused because Adi dropped Unity.
  The code stays.
- **Offline asset conversion:** Blender works as a Python module (`pip install bpy` in a Python 3.13 venv). Asset
  packers live in `Tools/Models` and `Tools/Photo`.
- **More docs:** `Docs/PERFORMANCE.md`, `PERF-ARCHITECTURE.md`, `ASSETS.md`, `ASSET-BRIEF.md`, `BACKLOG.md`.

## 8. How to talk to Adi
- Very short replies in simple English (1–2 lines).
- Always give the APK link after a build.
- Do the work yourself; don't ask him to run things.
- He sends feedback from playing on his phone. Show him screenshots when they help.
