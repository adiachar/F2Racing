# Asset brief: realistic humans, road textures and buildings for a mobile racing game

You are making 3D assets and textures for a **mobile F1-style racing game** (Unity, Android phones such as a Redmi Note 12 Pro / Mali-G68). The goal is **Real Racing 3-level realism or better** while staying light enough for a mid-range phone. The game's own tools will import, retarget, compress and LOD everything you deliver, so focus on **clean, realistic, correctly sized source files**.

Please deliver **one zip** laid out like the folder plan at the end, with a `MANIFEST.md` listing every asset.

---

## 0. Rules for every asset

1. **Licence:** use only assets you made yourself, or that are **CC0** or **CC-BY**. Do not use anything marked Editorial, NonCommercial (NC), NoDerivatives (ND), or "personal use only".
   - For every downloaded asset, give its source URL, author and licence in `MANIFEST.md`.
2. **No real brands:**
   - no real logos, team names, sponsors, car makers or clothing brands
   - no real people's faces (celebrities, drivers)
   - paint out any logo found on a downloaded asset
   - made-up names are fine, but plain unbranded areas are better, because the game prints its own made-up names
3. **Units and orientation:**
   - real-world scale in **metres**
   - **Y-up**, facing **+Z** (glTF default)
   - pivot/origin at the **base centre** (feet for people, ground for buildings)
4. **File formats:**
   - **GLB (glTF 2.0)** preferred; FBX is fine
   - textures as separate **PNG** files (8-bit; JPEG is acceptable for colour maps)
   - power-of-two sizes (512, 1024, 2048, 4096)
5. **Lighting:** colour/albedo maps must be **flat and evenly lit**. That means no baked sun, no strong shadows and no bright highlights, because the game adds its own lighting. Faint ambient occlusion in creases is fine.
6. **Materials:** simple PBR, a few at most: colour, plus optional normal (OpenGL / Y+) and roughness.
7. **Size:** keep the whole zip **under ~300 MB**. Quality matters more than quantity.

---

## 1. Humans (highest priority)

### Who we need

| Group | Count of different-looking people | Notes |
|---|---|---|
| Grandstand fans | 10–16 | Mixed ages, genders and skin tones. Casual summer clothes (T-shirts, caps, shorts, jeans). Some in plain team-colour shirts (no logos). |
| Pit crew / mechanics | 4–6 | Fire-suit overalls, gloves, some with closed helmets, some with caps. Clothing should be **one main colour plus one accent colour** so the game can tint it per team (see "Tint mask" below). |
| Engineers / team staff | 3–4 | Polo shirt, trousers, headset. Team-tintable like the crew. |
| Marshals | 3 | Bright orange overalls, white or orange gloves. |
| Officials / media | 3–4 | Smart casual; a photographer with a camera; a TV person. |
| Driver | 1–2 | Race suit, gloves, closed helmet with a dark visor. Team-tintable suit. Optionally an open-face version with the balaclava visible. |

### Technical requirements (each person)

- **Realistic** proportions and materials. No cartoon or low-poly "stylised" look.
- **Height:**
  - about 1.60–1.90 m; please vary it between people
  - standing in an **A-pose or T-pose**, arms relaxed, feet on the ground at Y = 0
- **Rig:**
  - a standard humanoid skeleton; **Mixamo bone names are ideal** (`mixamorig:Hips`, `Spine`, `Spine1`, `Spine2`, `Neck`, `Head`, `LeftShoulder`, `LeftArm`, `LeftForeArm`, `LeftHand`, … `LeftUpLeg`, `LeftLeg`, `LeftFoot`, `LeftToeBase`).
  - Finger bones are optional and will be ignored.
  - Skinned, with at most **4 bone weights per vertex**.
- **Triangles:**
  - **8,000–20,000** for the source mesh; the game makes its own LODs (about 4,000 / 1,500 / 500)
  - one single mesh per person is preferred, or a few meshes (body, clothes, head)
- **Hair:** use **solid mesh hair or a cap/hat**. Do not use alpha-blended hair cards, because they are very slow on phones. If you must use cards, they need a cutout (alpha test) only, with very few of them.
- **Textures:**
  - **one texture set per person**: 1024×1024 (2048 max) colour, optional normal and roughness
  - all body and clothing parts on **one UV layout**, if possible
  - faces must be clean and neutral (no make-up, logos or text)
- **Tint mask** (crew, engineers, driver): an extra greyscale PNG with the same UVs.
  - **white** = main team colour areas, **mid-grey (128)** = accent colour areas, **black** = keep as painted (skin, black parts, metal)
  - the clothing in the colour map should then be painted **light grey** in those areas
- **Animations (optional, nice to have):** the game already has idle, walk, talk, clap, seated clap, wave and briefcase-carry. Extra clips are welcome on the same skeleton:
  - cheer with arms up, jump-cheer
  - point at the track
  - flag-waving (marshal)
  - photographer shooting
  - crew: wheel-gun crouch, jack-pull, lollipop hold
  - driver: climb into the cockpit, walk with helmet in hand
  - Deliver them as GLB/FBX animation files with the same bone names, in place, at 30 fps.

---

## 2. Road and ground textures

Every texture here must:

- **tile seamlessly** (wrap on all sides with no visible seam or repeating blotch)
- be **square**
- be shot or rendered from **straight above** in **flat, overcast light**

### Texture list

| Name | Size | Real-world area covered | Notes |
|---|---|---|---|
| `asphalt_fresh` | 2048 | 2 × 2 m | Dark, fine aggregate, uniform. |
| `asphalt_worn` | 2048 | 2 × 2 m | Lighter grey, slightly polished, small stones visible. |
| `asphalt_rubbered` | 2048 | 2 × 2 m | Dark rubbered racing line with tyre-streak direction along **+V (vertical in the image)**. |
| `asphalt_patch_decals` | 2048 | 4 × 4 m | Repair patches, sealed cracks and oil stains on a **transparent background** (PNG with alpha). |
| `kerb_concrete` | 1024 | 1 × 1 m | Concrete surface for kerbs, **unpainted grey**; the game paints the red and white. |
| `paint_line_white` | 1024 | 1 × 1 m | Worn white road paint, slightly rough, with dirt; alpha = paint coverage. |
| `gravel_trap` | 2048 | 2 × 2 m | Rounded light gravel, 1–3 cm stones. |
| `grass_mowed` | 2048 | 4 × 4 m | Short, healthy circuit grass, even colour, **no mowing stripes** (the game adds its own). |
| `grass_rough` | 2048 | 4 × 4 m | Longer, patchier grass for the back of the verges. |
| `astroturf_green` | 1024 | 1 × 1 m | Artificial grass at run-offs. |
| `runoff_paint` | 1024 | 1 × 1 m | Painted tarmac run-off texture (rough, grippy), **light grey** so the game can tint it. |
| `concrete_pitlane` | 2048 | 2 × 2 m | Smooth pit-lane / paddock concrete with fine joints. |
| `dirt_dry` | 2048 | 2 × 2 m | Dry dirt and dust for the edges of gravel traps. |
| `sand` | 1024 | 1 × 1 m | Optional. |

For each texture deliver `<name>_albedo.png` (required), `<name>_normal.png` (optional, OpenGL Y+) and `<name>_rough.png` (optional).

---

## 3. Buildings and trackside structures

Keep buildings **simple in shape and rich in texture**, the way Real Racing 3 does it. Big flat walls should carry detailed photo facades instead of many polygons.

### Models

| Model | Size guide | Triangle budget |
|---|---|---|
| Pit building (2–3 storeys, garages along the ground floor, glass upper floors, roof terrace) | one 12 m wide module that repeats | ≤ 3,000 per module |
| Race control tower | ~10 × 10 × 18 m | ≤ 3,000 |
| Grandstand (covered, steel roof, seat rows) | one 20 m wide module that repeats | ≤ 3,000 per module |
| Small open grandstand (bleacher) | 15 m wide | ≤ 1,500 |
| Hospitality / team unit (modern glass box) | ~15 × 10 × 8 m | ≤ 2,000 |
| Medical centre, marshal post, timing hut | small | ≤ 800 each |
| Footbridge over the track (with banner panel) | 30–40 m span | ≤ 2,500 |
| Billboard frames (blank panel) | 6 × 3 m and 12 × 3 m | ≤ 300 |
| Tyre wall block, Tecpro barrier block, concrete wall block | 1–2 m units | ≤ 300 each |
| City buildings (European apartment blocks, 4–8 storeys, balconies) | one facade module | ≤ 1,500 |
| Italian park villa / old brick buildings (Monza style) | ~20 × 12 × 12 m | ≤ 2,500 |

**Model rules:**
- no interiors
- no tiny details smaller than ~10 cm, except where they shape the outline (railings can be a texture with alpha cutout)
- glass is an opaque dark reflective material, not transparent

### Facade textures (tileable horizontally, 1 storey = 3 m tall)

These each cover one storey, about 4 × 3 m per tile, at 1024 × 1024 (or 2048 × 1024 for 2 storeys):

- `facade_apartment_*`: 3–4 variants: plaster with windows, shutters, balconies
- `facade_glass_office`
- `facade_brick_old`
- `facade_concrete_industrial`
- `facade_garage_doors` (pit garages: roller doors, half open and closed)

---

## 4. Optional extras (if time allows)

- **Trees:** realistic broadleaf (plane tree, oak, poplar) and pine. Each as LOD0 with ≤ 4,000 triangles, leaves as cutout cards, plus a **billboard image** (front view, alpha) for distance.
- **Sky:** HDRI skies, 8K equirectangular, clear, partly cloudy and sunset, with the sun not too low. We already have some, so different moods are welcome.
- **Crowd sprites:** 32 front-view seated spectators and 32 standing ones, each a 128 × 256 PNG with alpha, evenly lit, for distant crowds.

---

## 5. Folder plan for the zip

```
assets/
  MANIFEST.md                 <- one table: file, what it is, real size (m), tris, licence, source URL, author
  humans/
    fan_01/ fan_01.glb  fan_01_albedo.png  fan_01_normal.png
    crew_01/ crew_01.glb  crew_01_albedo.png  crew_01_tint.png
    ...
    animations/ cheer.glb  point.glb  flag_wave.glb ...
  textures/
    asphalt_fresh_albedo.png  asphalt_fresh_normal.png  asphalt_fresh_rough.png
    ...
  buildings/
    pit_module/ pit_module.glb  pit_module_albedo.png ...
    facades/ facade_apartment_1_albedo.png ...
  extras/ (trees, skies, crowd sprites)
```

### Final check before zipping

- [ ] Every file opens; people stand on Y = 0 at the right height; buildings are at real size.
- [ ] No logos, brand names or real people's faces anywhere.
- [ ] Every downloaded item is listed in MANIFEST.md with its CC0 / CC-BY licence and URL.
- [ ] Textures tile with no seams, are flat-lit, and are power-of-two sizes.
- [ ] Triangle counts are within the budgets above.
