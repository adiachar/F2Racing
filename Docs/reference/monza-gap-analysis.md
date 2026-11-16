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
