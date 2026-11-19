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

## Round 5 (after the v13 shots): the land beyond the barriers, sky, distance

What v13 showed: past the fence in the open stretches (s=300 right, the infield lawns) a bare, flat, single-tone lawn
out to a bare horizon. Cause: the main tree budget (6000 / 3600 / 1800) is spent on the woods and mapped trees within
~70 m of the track (estimate from the map: the sorted candidates run out at ~70 m), the bands stop at the tree reach,
and beyond that the woods are flat dark ground. Sky: an even scatter of flat-shaded clouds. Distance: the linear fog
only starts at ~350 m, so 200-600 m (stands, tree lines) kept their near colour.

Done (not yet seen in a build), all in `RealWorld.cs` unless named:

- **Service roads, marshal huts, service vehicles** (`ServicePlan`, `ServiceMeshes`): where the land 12-30 m behind the
  barrier is open (grass / park / farmland / gardens; no woods, buildings, stands, landmarks, water, other legs of the
  lap, insides of tight bends) for 140 m+, a 3.8 m worn-asphalt road runs 19 m behind the barrier, ends curving away
  into the park. Every 230-420 m on its outer side: a white site cabin with a window band, door and steel roof plus two
  blue portable toilets, or a parked van + car, cabin + van, or a red fire tender with a white band; all vehicles have
  wheels, glass and an amber light bar. They are TV-camera solids. Planned in Prepare so the trees keep off them.
- **Hedges, copses, bushes** (`Outfield`, own budget 2800 / 1600 / 700): hedgerows of squat scrub cards (now and then a
  hedgerow tree) along land-use boundaries (fields: hedges round them only), copses of mixed park trees with a fringe
  of bushes where a slow noise field peaks, lone bushes, bushes along the service roads; nearest first, thinning past
  380 m. The ground under each is darker rough grass in the land colour map (`lflag` bit 4), so they sit in their own
  shade.
- **Far tree lines** (`FarTrees`, `EnvFarTrees`): every wood edge facing the circuit, and 40 % of the field boundaries
  (rows of trees between fields), from 0.78 x the tree reach to the fog's end, gets an opaque silhouette strip every
  ~16 m: two rounded crowns of their own heights on a shaded foot, poplar spikes now and then, both faces. Opaque (no
  alpha-test shimmer or mip blobs at 1-2 km, MSAA smooths the outline); colour drifts olive / blue-green per stand.
- **Terrain swells** (`Undulation`): +-2 m over ~400 m and +-0.8 m over ~125 m in the open land, zero on the circuit's
  ground and growing over the first ~300 m beyond it; flatter on farmland, none in water. Everything placed afterwards
  (buildings, roads, trees, huts) takes its height from it; the bake shades the slopes.
- **Large lawns** (`LandTexture`): a ~80 m lush / thin hue drift on grass and park polygons on top of the existing
  brightness noise, so a big field is never one flat tone.
- **Clouds** (`Clouds.shader`, Medium/High; Low unchanged): a slow regional density (clear patches and clumped fields
  of cumulus), each cloud lit on its sun side and shaded on the far side (the deck sampled a step towards the sun), a
  soft silver lining and forward-scatter sheen, and a broad glow round the sun with a pale lift low on the sun's side.
- **Aerial perspective** (`Sky.PushHaze`): afternoon haze 0.10 -> 0.22, density 1/1400 -> 1/1100 m: ~5 % at 400 m,
  ~10 % at 1 km on top of the linear fog, so the layers between 200 m and the horizon separate. Near colours (RR3
  targets) untouched; vertex shader only.

Cost: draw calls +1 (`EnvFarTrees`), plus at most a few where a 1 km block had no road / building / tree mesh before
(the service roads, huts and plants go into the existing per-block meshes and materials) - well inside +8. Vertices
(High / Medium / Low): plants ~+28k / +15k / +6k, far tree lines ~+33k / +26k / +12k, roads and huts ~+8k; the bake time
grows by about as much. Pixel: Clouds HQ +2 texture reads and ~25 ALU on sky pixels only; Low unchanged.

Shots to verify: s=300 chase + cockpit (right: service road, hut / vehicles, hedges and copses on the lawn, tree
line on the horizon), s=1450 and s=2500 chase (far tree lines through the fog, haze on the far woods), s=3920 / 5150
chase (infield park), any shot looking towards the sun (cloud lighting, sun glow). Risks to check first: a service road
crossing something it should not (a path through a stand's back, a gravel trap); hedges in front of a stand; the far
strips' outline at 600-800 m on Medium (they should read as tree tops, not as cut-outs); the cloud glow on Low (none).

Still open (by impact per cost):

1. Sky / environment reflection on glass (pit curtain wall, stand boxes, map buildings).
2. Procedural `Trackside.Stand` tier AO bands; pit building roof top; real emissive downlights.
3. Poly Haven building models near the track; forest-floor undergrowth cards at the near tree line.
4. Mowing stripes that flip with the view direction.
5. Landmarks: old Lesmo/Ascari bridges, the old banking, the Parabolica tower.
6. Crowd variety (colour blocking, flags).

## Round 6: road edges, chicane barriers, marshal kit, glass

What the v13 shots and RR3 still showed: asphalt -> kerb -> astroturf -> grass meet in perfectly clean, ruler-straight
lines (no dirt, no stones, no worn grass); the same belted tyre wall at every slow corner; marshal posts with nothing
on them; glass that is a flat printed picture of a sky (open item 1 since round 3).

Done (not yet seen in a build):

- **Road edges** (`Road.cginc`, both levels unless noted; new mesh channel uv3 = edge dirt left / right from
  `WorldBuilder.EdgeDirt`): a dusty soil film reaching 0.5-1.6 m in from the edge with a wandering inner limit, over the
  kerbs too; strong beside the gravel traps (spread ~30 m downstream, where cars rejoin) and on the outside of every
  corner exit (10-70 m after the curvature, by how tight it is), a faint drifting base elsewhere. Gravel stones on a
  6 cm grid in it (single box-filtered stones close up on HQ, their mean colour further away and on Low). A grime strip
  outside the white line (never swept). **Marbles**: off the line (> 2.2 m from it) where the rubber is heaviest (the
  corners), a dark speckle of pellets (HQ grains, mean darkening on Low).
- **Timing loops** (`Road.cginc` `_Loops`): a pair of sealed saw cuts 0.6 m apart across the road at each of the 25
  loops `Sectors.cs` times (lap / 25) and at the two sector ends (`SectorMap.Ends`), so the timing really happens where
  the road shows it.
- **Worn grass at the turf edge** (`Trackside`, grime layer, Medium/High): a ragged band of bare soil along the
  astroturf's outer edge, patchy along the lap (slow noise), wider on corner exits and at the likely impacts.
- **Tecpro-style blocks at the chicanes** (`Trackside`, parkland corners tighter than R 45 m, i.e. the Rettifilo and the
  Roggia at Monza): 1.2 m foam modules alternately blue / red, bevelled vertical and top edges (so the sun picks out
  each block), a white strap band, a dark joint between modules, scuffed at impacts; coloured end faces. The other slow
  corners keep the belted tyre wall.
- **Marshal posts** (Medium/High): a yellow and a green flag draped over the parapet, a red extinguisher and a white
  bin of absorbent by the door.
- **Braking boards**: 200 / 150 / 100 / 50 m (the 200 only before braking zones long enough for it), new "2" glyph; the
  texture grows to 96 x 128.
- **Glass reflections** (open item 1; `Baked.shader` `F2_GLASS`, Medium/High via `TracksideLod`): the trackside atlas
  marks glass in its alpha (curtain-wall panes, glazing, balustrades, the dark-glass swatch; drawn blinds barely). Those
  texels mirror the sky (fog colour at the horizon, deeper blue overhead), the ground below the horizon and the sun
  (s^64 glint) with Schlick Fresnel (F0 0.04), independent of the bake: shaded glass on the pit building and the stand
  boxes still shows the bright sky, and the reflection slides as the car passes.

Cost: draw calls **+0** (all in existing layers / materials; the boards keep their two). Vertices: Tecpro ~+2.5k, worn
grass decals ~+9k (Medium/High), marshal kit ~+60 per post. Pixel: Road HQ ~+45 ALU (no new reads), Road Low ~+20 ALU;
Baked `F2_GLASS` ~+30 ALU on the trackside atlas only (Medium/High); Low unchanged except the road maths.

Shots to verify: s=850 chase + cockpit (Rettifilo: Tecpro, stones and dust on the exit edge and kerb, marbles off the
line, 200/150/100/50 boards before it), s=0 chase + tv (pit-building and balcony glass mirroring the sky, timing loops
on the straight), s=300 chase (worn turf edge, timing loop, marshal post flags), s=2030 chase (Roggia: Tecpro, gravel
on the edge), s=2500 chase (Lesmo exit dirt). Risks to check first: stones / film too strong (should read as a dusty
edge, not a gravel road); the loops' contrast far away (box-filtered: should fade, not flicker); glass too bright on
Medium at grazing angles; Tecpro colours vs the real Monza (blue / red assumed).

Still open (by impact per cost):

1. Procedural `Trackside.Stand` tier AO bands; pit building roof top; real emissive downlights.
2. Car lighting consistency (CarPaint sky term vs the world's sky; owned by the car engineer this round).
3. Poly Haven building models near the track; forest-floor undergrowth cards at the near tree line.
4. Mowing stripes that flip with the view direction.
5. Landmarks: old Lesmo/Ascari bridges, the old banking, the Parabolica tower.
6. Crowd variety (colour blocking, flags); marshals at the posts (People*, other engineer).
7. Post numbers on the marshal posts, painted sector lines on the kerbs.

## Round 7: the park's real trees and furniture

Research (what is trackside): the woods round the circuit are the Parco di Monza's old oak-hornbeam forest (Quercus
robur / farnia dominant, hornbeam, elm, beech, red oak, plenty of Robinia and ash at the edges), big London planes
on the avenues and round the villa, a few Scots pines; the July 2023 storms felled ~10,000 trees inside the
circuit's grounds and ~8,000 have been replanted (young trees). Ranked by share of the driver's view: woods
(~60 % of everything beyond the barriers), stands / pit building, fences / barriers, service roads and huts, the
landmarks (old banking over the Serraglio, Parabolica), park furniture.

Done (not yet seen in a build):

- **Park species** (`Tools/Models/build_park.py`, appended to models.bytes / atlas.bytes): park_oak (wide crooked
  crown), park_plane (tall straight trunk, mottled bark, high dome), park_robinia (slender, pinnate leaves),
  park_pine (Scots pine: long bare orange trunk, flat tufted crown). Same clump / LOD / impostor pipeline.
- **Monza mix** (`ModelLib.PickPark`, only with a World file): oak 36 %, robinia 24 %, plane 16 %, tree_small_02
  (hornbeam / elm stand-in) 16 %, pine 8 %, in ~70 m stands (each patch has a dominant species for half of its
  trees). The tropical jacaranda / island trees are no longer used at Monza. Planes are always 18-28 m, pines never
  understorey, pine needles darker and bluer.
- **Park furniture** along the service roads (`RealWorld.ParkFurniture`): the cast-iron park lamp every ~55 m
  (max 24 High / 14 Medium) and a bench facing the road with a green bin every ~115-155 m (max 14 / 8), on the far
  side of the road, never at a hut.

Cost: tree instancing buckets unchanged (5 species at Monza, as before). Park trees are lighter (LOD0 1.5-2.2k tris
vs 2.3k; LOD1 450-550 vs 570), the vertex budgets are unchanged, so a few more trees become models instead of cards
at the same cost. Props now go in 1.2 km chunks (was 400 m): about +1 draw call where a chunk is in view.
APK: models.bytes +310 KB, atlas.bytes +470 KB (raw).

Not done: the old banking over the Serraglio and the Parabolica tower: the oval is not in the map data
(Overture has no raceways; OSM is not reachable here), so there is nothing accurate to build it from.

## Round 8: forest edge, undergrowth and the forest floor

Shots showed tall trees on bare, mown lawn with a hard line at the trunks, and uniform grass up to the fence foot.

Done (not yet seen in a build):

- **Forest floor** (`RealWorld.FloorMask`): a 0..1 mask on the terrain grid from the mapped woods (depth into them, the
  circuit's own ground included, ramping in over ~12 m) and the crowns of the trees planted near the track. The land
  texture blends to dark brown-green leaf litter with mossy patches (no hard switch); the verge gets the mask in uv3.y
  (`VergeFloor`) and Ground.cginc draws the same floor there, without stripes or barrier wear (no extra texture read).
- **Undergrowth** (`RealWorld.Undergrowth`, Medium / High, within 150 m of the track): hazel / elder bushes 1.5-4 m and
  bramble patches 0.5-1 m (the two shrub models resized, instanced), young replanted trees 3-6 m in ~55 m patches
  (robinia / small tree, slender, LOD 1 at most), ferns in the shade, grass tufts at the tree feet, bushes along the
  service roads. High 150 / 130 / 64 / 520 ferns, Medium 70 / 60 / 28 / 220. Low: none.
- **Tall-grass band** (`RealWorld.FenceBand`): pale, uneven grass cards at the barrier feet, park side every ~1.5 m
  (Medium 2 m) with gaps, plus short tufts on the track side where the verge is grass. Low: none.
- Card tiles (tall grass, fern) are painted at load (`EnvTex.PaintUnder`) into a free corner of the model atlas, so
  the cards share the model trees' material and their merged 400 m chunks; LightBake keeps them out of the roof map.

## Round 9: grandstands, buildings, pit straight

Done (not yet seen in a build):

- **Procedural stands** (`Trackside.Stand`): grey standing-seam roof (no longer white); the underside is in shade (lighter at
  the front edge, darker at the back) with four steel purlins; the front girder has real top / bottom chords; every column is
  a flanged steel column on a base plate, carrying a lattice cantilever truss (chords + webs with thickness; Low keeps the flat
  beam); an eaves beam along the back. Rows get baked AO under the roof (treads, risers, seats and crowd darker towards the
  back). Seats in team-coloured blocks (one colour per section, the upper tier another; Monza mostly red). Stair aisles:
  yellow nosing on every step, a handrail down the middle with posts. A name banner under the roof's front girder
  (invented names: "TRIBUNA AURORA", "TRIBUNA DEL PARCO", "TRIBUNA VELOCE", "TRIBUNA SOLE").
- **Crowd** (`Baked.shader F2_CROWD`, trackside crowd material after the bake): the empty seats in the crowd texture are
  neutral and take the seat block's colour from uv1; each person bobs / sways by a texel (per-person phase, `_Time`),
  sampled with unshifted gradients (no mip seams). The map stands' crowd cell (building atlas cell 22) gets the same shimmer.
- **Map buildings** (`EnvTex.BuildingAtlas` + `Baked.shader F2_ATLAS`): window glass is marked (alpha 0; the tint mask now
  starts at 14/255) and mirrors the sky / ground / sun with Fresnel like the trackside glass (Medium / High); ground-floor
  bays darker towards the pavement; office ribbon windows get a glass balcony balustrade with a rail; curtain-wall panes
  vary (tint, blinds, transom). At dusk / night a share of the windows glows warm (`_F2Night`, set in `Trackside.Build`
  from the time of day; also the trackside glass).
- **Pit straight**: white line along the wall side of the fast lane, speed-limit lines across the lane at both ends,
  "SPEED LIMIT 80" painted before the entry line; a pit-wall gantry 40 m before the line (two timing screens, to the lane
  and to the track, advertisers on the beam); the first bay's penthouse is now a two-storey race-control tower glazed on
  three sides with a "RACE CONTROL" sign and a mast. (Garage interiors / team boxes / pit-wall stands stay `PitGarage.cs`.)
- Atlas: the free block x 512..1023 y 320..511 of `TracksideArt.Atlas` holds the banners, the race-control sign and the
  pit-lane lettering; glyphs '8', '0' added.

Cost: draw calls **+0**. Triangles: ~+0.9k per roofed stand on Medium / High (trusses ~0.6k, aisles ~0.25k), so ~+4.5k
Medium, ~+7k High over the lap; Low ~+0.2k; pit ~+0.2k. Pixel: F2_ATLAS ~+35 ALU (Low ~+10: no mirror), two more
interpolators; F2_CROWD ~+15 ALU. Crowd meshes +16 bytes / vertex (uv1).

Shots to verify: s=0 chase + tv (main stand roof, trusses, banner, seat colours, crowd movement; race-control tower;
pit-wall gantry at s=-40; speed-limit line at the pit entry), any map building with offices (glass reflecting the sky).
Risks: glass on map buildings too bright at grazing angles; seat tint strength (linear uv1 values); the banner hiding the
top rows from the chase camera.
