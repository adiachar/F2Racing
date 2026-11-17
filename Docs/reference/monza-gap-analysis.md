# Monza: gap analysis against Real Racing 3 and the real circuit

Scope: only what the driver sees (chase and cockpit cameras). Sources: our shots v5-v9 and the owner's phone
screenshots, the RR3 Monza sheet and the RR3 quality sheet. Ranked by visual impact per unit of effort.
"Done" = implemented in this round (not yet seen in a build: no editor here, check the shots listed at the end).

| # | Gap (ours vs RR3 / real Monza) | Impact | Effort | Status |
|---|---|---|---|---|
| 1 | **Catch fence reads as a flat dark-green wall.** Its far mips were boosted to opaque, so 50 m away it hid the trees and stands behind it on both sides of the whole lap. RR3: grey steel mesh, a light veil at distance, cables and posts. | Very high | Low | Done: grey galvanised mesh with cables; far away the mesh's true coverage goes out as MSAA coverage (`BakedCutout._Veil`), without MSAA it thins out instead of closing up |
| 2 | **Grandstands are white wedges.** Map stands (`RealWorld.Stand`, the ones at 300-450 m): one noisy crowd slab, white plaster end triangles up to the roof, white slab roof. Procedural stands (`Trackside.Stand`): white stepped ends, billboard fascia. Real: steel roof on tapered beams, coloured cladding, stair aisles, vomitories, glazed boxes. | Very high | Medium | Done for both kinds: aisles, vomitories, coloured ribbed-steel ends with a white raker, glazed wind screens, boxes under the roof, standing-seam roof with lattice edge girder, cantilever beams, floodlights, roof flags, four cladding colours |
| 3 | **Pit building is a glass box with a billboard on every bay.** Real: long modern building, glazed floors, terrace, deep roof, podium. | High | Medium | Done: lintel, glass curtain wall with reflections, cantilevered balcony with glass balustrade and guests, set-back hospitality floor, deep roof slab on slim columns, name sign ("AUTODROMO DEL PARCO") and only a few advertisers on the fascia, penthouses and louvred plant on the roof |
| 4 | **Advertising looks like 8-bit blocks** (128 x 32, 3 px letters) and is **mirrored on the right-hand fence**. | High | Low | Done: 256 x 64 boards, smooth slanted lettering, dirt at the foot; left/right fixed |
| 5 | **Artifacts.** Dark box shadow on the road at ~2500 m; TV camera behind the fence at the pits. | High (looks broken) | Low | Done: the box was the two impostor cards of a model tree projected whole by `RoadShadow` (budget fallback puts impostors next to the track); model trees now cast dappled crowns. TV cameras stand 1.2 m behind the barrier, 6.6-8.2 m up, and shoot over the fence |
| 6 | **No circuit furniture.** RR3/real: camera towers, big screens, cranes, light masts everywhere. | Medium-high | Low | Done: TV towers outside the corners, a big screen per stand, recovery cranes at the slow corners, floodlight masts every ~450 m |
| 7 | **One tree height.** Model trees were all 15-25 m: a hedge-like top line. | Medium | Low | Done: a third are 7-12 m understorey trees, the rest 14-26 m |
| 8 | **Clean asphalt off line.** RR3: lock-up marks into every braking zone, dirt over exit kerbs. | Medium | Low | Done: lock-up pairs into the slow corners, dirt over the exit kerbs (ground-decal layer) |
| 9 | Crowd is a tiled 2D texture; RR3 crowds are too, but with more colour blocking. | Medium | Medium | Partly (aisles and seat blocks break it up) |
| 10 | Buildings near the track (Poly Haven models), forest floor / undergrowth cards, kerb wear in `Road.shader`, Armco depth (posts with spacers, double height at corners). | Medium | High | Not done: next round, after the shots |
| 11 | Old Lesmo/Ascari bridges, the old banking seen from the Serraglio straight, the Parabolica tower. | Medium (landmarks) | High | Not done |

## Cost

- Draw calls: **+0**. Everything new is in the existing layers (atlas mesh, crowd, grime, building mesh), cut into
  the same ~250 m chunks.
- Trackside atlas 512 x 256 -> 1024 x 512 (about +2 MB). Vertices: roughly +25-30k over the lap (stands and pit
  building mostly), spread over the chunks.
- Fence shader: two derivative reads and a lerp more per pixel; fences stay off on Low.

## Shots to verify (High and Medium, afternoon)

| s (m) | cams | look for |
|---|---|---|
| 0 | chase, cockpit, tv | pit building floors/balcony/sign, main stands, TV camera over the fence |
| 300 | chase, cockpit | map stands (aisles, coloured ends, roof beams), grey see-through fences, unmirrored boards on the right |
| 850 | chase | first chicane: TV tower, crane, lock-up marks, kerb dirt |
| 1450 | chase, tv | fence veil at distance, floodlight mast, tree storeys |
| 2500 | chase, cockpit | the dark box on the road is gone; dappled tree shade instead |
| 3920, 5150 | chase, far | corner stands with screens, towers |

Risks to check first: the fence on a build without MSAA (should thin out, not vanish abruptly), the lettering
scale on the boards, the balcony guests' height on the pit building.

## Round 2 (after the v10 shots)

- The "box" at ~2500 m was `Road.cginc`'s resurfaced patch, not a shadow: its tar outline is gone, its edges are
  feathered (1.5 m along, 0.6 m across) and it is 4.5 % darker instead of 13 %, with no extra gloss.
- Whitish asphalt: edge dust 0.45 -> 0.2, dry sheen lobe 0.55 -> 0.24, stone sparks 1.6 -> 0.7, grazing sky 0.22 -> 0.13;
  base asphalt 0.38 -> 0.43 sRGB so the sunlit road sits near 150 sRGB.
- TV cameras keep 25 m away from everything in `Trackside.Tall` (gantry posts, bridge columns, towers, masts, screens).
- Start straight: feather banners behind the fence where there is no stand or pit; trees round map buildings
  within 380 m of the track (gardens), so houses no longer stand in a bare field.
- Still open: Poly Haven building models, undergrowth cards, Armco depth, kerb wear.

## Round 3 (after the v11 shots)

Done (not yet seen in a build):

- **TV camera inside an object (v11 #3, s=300).** The camera for that stretch stood ~25 m past a banner bridge and its
  long lens looked straight into the banner. Placement is now tested against volumes: `Trackside.Solids` (oriented
  boxes: procedural stands, pit building + canopy + podium, screens, TV towers, cranes + booms, masts, marshal posts,
  motorhomes, feather banners, start gantry posts + beam, banner bridges; `RealWorld` map buildings within 160 m and map
  stands; street-circuit town blocks) plus the model trees (crown + trunk). `GameManager.BuildTvCams` rejects a spot
  inside any solid (1.5 m margin) and counts blocked lines of sight (solids and the ground) to the road over the whole
  stretch the camera films (previous camera to 15 m past this one, every 12 m); candidates up to 48 m along the track,
  +3 / +6 m higher, the other side; the first fully clear one wins, else the least blocked. `Trackside.Tall` is no
  longer used for this.
- **Road close up (HQ):** stone-by-stone sun relief (the photo aggregate's slopes now modulate the diffuse sun, not only
  the sheen); tyre marks: three families of single-car mark pairs around the racing line (own offset per stretch,
  wandering, fading in/out, stronger in corners); more rubber on the line (straights 0.45 -> 0.55, darkening 0.42 -> 0.48);
  sealed transverse cracks (one 9 m stretch in four) and a longitudinal paver joint in some sections.
- **Kerbs:** rounded raised profile in the mesh (6 columns: rounded nose, rising top, rounded outer fall) so the baked and
  sun light change across them; HQ paint wear (scuffed patches showing the grey base, mostly on the inner half) and dirt
  on the outer part.
- **Grass (HQ, grass material only, `_Clumps`):** the detail texture's alpha is a clump field read at both tiles: lumpy
  tufts (~0.75 m), lush darker and thin paler patches (~2.6 m); mowing stripes wander and their contrast varies along the
  lap; long clumpy grass with dry tips at the barrier foot; bare dusty soil between tufts along the verge's inner edge.
- **Armco (Medium/High):** real W-beam (two ridges, valley, rolled edges, 31 x 7.5 cm) in the near-only post layer
  (hidden beyond ~85 m); a flat face at the valley depth in the atlas layer stands for it far away. Low keeps the V.
- **Contact shadows** on the ground round stands, TV towers, cranes, marshal posts and motorhomes (grime decal layer,
  Medium/High).
- **Precision fix:** `Baked.shader` (F2_LAND, F2_ATLAS) and `Ground.cginc` compared half values; now float (the same
  min-precision bytecode failure that dropped CarPaint to its fallback).

Cost: draw calls +0 (all in existing layers/materials). Vertices: kerbs x3 (~+15k at Monza), W-beams ~+45k in the
distance-culled post layer, flat far faces replace two faces per beam piece. Pixel cost: Road HQ ~+60 ALU (no new
reads), Ground HQ alpha of the existing second read (no new reads); Low unchanged.

Shots to verify: s=300 tv (no board; camera moved or raised), s=0 tv and 2500 tv; s=0 / 300 chase and cockpit (road
relief in the sun, tyre marks, cracks, kerb profile, grass clumps / stripes / barrier grass), s=2500 chase (Armco
W-profile at the right, kerb wear), any stand from a chase cam (contact shadow at its foot).

Still open (next rounds, by impact per cost):

1. Sky / environment reflection on glass (pit building curtain wall, stand boxes, map buildings): needs a view-dependent
   term in `Baked.shader` for the glass texels (atlas alpha mask) - medium effort.
2. Grandstand and pit-straight flat surfaces: map-stand roofs and back walls still large single-tone quads; pit wall
   concrete is one repeating block texture; add panel seams, rust/streak variation per bay and AO under roof edges.
3. Poly Haven building models near the track; forest-floor / undergrowth cards at the tree line.
4. Mowing stripes that flip light/dark with the view direction (needs the track direction in the ground shader).
5. Old Lesmo/Ascari bridges, the old banking, the Parabolica tower (landmarks).
6. Crowd variety (colour blocking, flags).

## Round 4 (after the v12 shots)

Done (not yet seen in a build):

- **Deep red cars read pink** (Scarlet Dart, cockpit/chase). The clear coat added the sky (bright, blue-white) over the
  paint as a flat add; from the cockpit the sidepod and nose tops are seen at a grazing angle and mirror the bright sky
  overhead, so G and B rose under a deep red. `CarPaint.KeepHue` (both levels): the reflection keeps its luminance but,
  on saturated paint, its dim part takes the paint's hue; bright reflections (clouds, horizon glare) and the true grazing
  silhouette stay a neutral mirror; white/grey/black/carbon unchanged. Fresnel F0 0.05 -> 0.04, a touch steeper at
  grazing. Livery zones, gloss and the float-compare rule untouched.
- **Map grandstands** (`RealWorld.Stand`): rake in 3-8 tiers with alternating seat-colour bands (four colour pairs per
  stand) and strong AO darkening the rows under the roof towards the back (also gives the bake vertices for the roof's
  shadow); a cross gangway at ~40 % (concrete band, dark riser, white rail); a lattice truss along the roof front
  (chords + diagonals every ~3 m); slim columns on every other aisle; darker roof underside; rake angle, roof height and
  roof colour vary per stand.
- **Pit podium** (the brown slab over the start, chase/cockpit): underside of cool-grey composite panels in a ~3 m grid,
  four steel beams, rows of downlights, a dark shadow gap and white trim on the fascia, sponsor boards on both ends.
  Pit-building soffit: three panels per bay and two downlights instead of one flat swatch.
- **Tree lines** (`Foliage.shader` HQ): at mid distance the alpha cut-off follows the painted shade (gaps between clumps
  open, lit clumps stay), so crowns break into lobes instead of mip-rounded blobs; per-vertex colour drift in ~2.5 m
  patches (yellower / bluer, lighter / darker) inside each crown. No extra reads.

Cost: draw calls +0. Vertices: a few hundred per map stand, ~150 per pit bay, ~200 on the podium. Pixel: CarPaint
~10 ALU, Foliage ~6 ALU (HQ only).

Shots: s=0 chase + cockpit (red car colour, podium underside), s=300 chase (stands both sides: bands, truss, shade
under roof), s=2500 chase (tree line), s=0 tv.

Still open (in addition to round 3's list): procedural `Trackside.Stand` gets no tier AO bands yet; real emissive
downlights (they are white swatches lit by the bake); the pit building's main roof top is a flat swatch (seen only from
TV cameras).
