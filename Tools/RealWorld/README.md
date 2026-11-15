# RealWorld: the real surroundings of each circuit

Offline pipeline that turns free open data into `Assets/Resources/World/<Name>.bytes` (Unity TextAsset, format
F2W1 v1): terrain, buildings with heights, land use / land cover / water areas, roads and railways, and trees, all
in final Unity world coordinates (X = track x, Z = track y, Y = the game's track height frame).

Status: only **Monza** is built and checked for now (owner's request). The pipeline is generic: every circuit is
configured in `CIRCUITS` in `build_world.py` and builds with the same command.

## Re-run

```
pip install --break-system-packages pyarrow requests numpy shapely pyproj pillow scipy matplotlib pandas
python3 -I Tools/RealWorld/build_world.py Monza --preview   # one circuit (no name = all circuits)
python3 -I Tools/RealWorld/check_world.py                     # validate every .bytes file
```

Needs `dotnet` (8.0) for `TrackDump/` and network access to the Overture S3 bucket, the AWS terrain tiles and
raw.githubusercontent.com. Raw downloads are cached in `/home/claude/rw-cache` (`RW_CACHE` to change it), never in
the repo. Overture release: `OVERTURE_RELEASE` (default `2026-10-21.0`). A Monza build takes ~30 s once cached.

## Files

| file | what |
|---|---|
| `build_world.py` | the pipeline; classification tables (building kinds, area kinds, road kinds/widths) at the top |
| `overture.py` | Overture GeoParquet over HTTP range reads: footers cached, only row groups whose bbox statistics intersect the circuit box are read |
| `georef.py` | game frame <-> WGS84: ICP similarity fit + smooth local warp |
| `terrain.py` | Terrarium elevation tiles (download, mosaic, smoothing, bilinear sampling) |
| `TrackDump/` | small C# tool: compiles `Assets/Scripts/Physics/*.cs` and dumps every circuit's centreline exactly as the game builds it (`CircuitDef.Build`: x, y, height, half-width) |
| `preview.py`, `preview_<Name>.png` | top-down check image: areas, roads, buildings (colour = height), trees, game centreline (red), real raceway after the fit (blue dashed); right: 700 m close-up at the start line |
| `check_world.py` | independent reader / validator of the binary format (also a reference reader) |
| `results.json` | per circuit: fit, transform, terrain check, counts, section sizes |

## How it works

1. **Game centreline**: `TrackDump` runs the game's own `CircuitDef.Build()` (TUM data, LocalFit, start shift,
   elevation), so the output matches the in-game track, not the raw TUM rows.
2. **Georeferencing**: the TUM frames have no lat/lon and the Overture transportation layer (TomTom-based) has no
   raceways, so the real centreline is the OpenStreetMap raceway from
   [bacinger/f1-circuits](https://github.com/bacinger/f1-circuits) (GeoJSON). It is projected to a local transverse
   Mercator plane and fitted to the game centreline by ICP (global search over rotation and mirror, then trimmed
   ICP) as a similarity transform. The scale comes out 0.5-2 % above 1 because the game's LocalFit rounds bends and
   then scales the lap back to its length; using it keeps the surroundings next to the in-game track.
   Then a **smooth local warp** (residuals smoothed along the lap, sigma 30 m, fading out from 40 to 300 m away from
   the track) removes the remaining few metres, so the gravel traps, grandstands and roads line up with the game road.
3. **Terrain**: Terrarium DEM (z13 for 20 m cells, z14 for 10 m), smoothed (Gaussian 25 m) to remove pixel noise and
   canopy/roof bumps. `game height = DEM + offset` (offset = median along the lap); the difference between the game's
   measured height profile and the DEM is spread into the land (fading out 60-500 m); within half-width + 8 m of the
   centreline the terrain is exactly the track height (where two parts of the lap pass close, the lower one), blended
   into the land by half-width + 60 m. Sea bed clamped to 8 m below sea level.
4. **Buildings**: Overture `buildings/building` (OSM + Microsoft/Google ML footprints). Dropped: underground, < 15 m^2,
   touching the track surface (centreline +- half-width + 4 m), more than 2 km from the track, or not wholly inside the
   terrain grid. Rings: outer ring only, 0.5 m Douglas-Peucker, CCW. Height: mapped height; else levels x 3.2 m;
   ML heights below 2.5 m treated as missing; grandstands/stands without a mapped height 8 m (12 m if >= 300 m^2);
   garages/sheds 3 m; otherwise the median of the known heights within 150 m, else the kind's default; small
   footprints capped (< 40 m^2: 4 m, < 100 m^2: 8 m). Roof shape: mapped, else (estimate) small low houses/sheds
   gabled, the rest flat. Colour: facade colour, else roof colour, else alpha 0.
5. **Areas**: land cover (most detailed zoom only; drawn first), then land (natural: forest, grass, sand...),
   land use, parking/apron polygons from infrastructure, sorted by area (big first); water polygons and rivers /
   canals / streams (lines buffered to 12 / 8 / 3 m) always last. Clipped to the terrain grid, holes dropped,
   1 m simplification (land cover 3 m).
6. **Lines**: Overture transportation segments (roads, rail). Tunnels and underground segments dropped; everything
   within half-width + 3 m of the game centreline cut away (so the race track itself and street-circuit roads under
   it never appear). Width: Overture width rule if present, else a class default.
7. **Trees**: individual OSM trees (`base/land` class `tree`) and tree rows (a tree every 7 m); height from the
   `height` tag, else 10 m (Monaco 8 m). Trees on the track or inside buildings are dropped.

## Format notes for the game side

Exactly the F2W1 v1 spec (TERR, BLDG, AREA, LINE, TREE). One extra section is appended at the end:
**`META`**: UTF-8 `key=value` lines (skip it by its length if unused). Keys: `lon0, lat0, scale, rot_deg, tx, ty,
mirror` (game = scale * R(rot) * tmerc(lon, lat) + (tx, ty), plus the local warp near the track), `fit_rms_m`,
`warp_rms_m`, `height_offset_m` (game Y = DEM metres above sea + offset), `sea_level_y` (sea level in game Y),
`overture_release`, `attribution`.

## Monza result

Fit RMS 1.69 m (median 0.79 m, scale 1.0055, rotation 0.12 deg, no mirror); after the local warp 0.61 m. Terrain
under the centreline matches the game height within 0.07 m RMS (max 0.26 m). 8204 buildings, 2614 areas, 4543
lines, 7402 trees, 1.41 MB.

Known gaps (not in the open data used here): the old high-speed oval (OSM `highway=raceway`, not in Overture),
paved run-off areas (area kind 11 unused), most heights are Microsoft ML estimates (median about 5 m), colours
are rarely known.

## Sources and licences

Attribution to show in the game credits:
**"© OpenStreetMap contributors (ODbL), Overture Maps Foundation, Mapzen/AWS Terrain Tiles"**

- Overture Maps Foundation, release 2026-10-21.0 (buildings, base, transportation): ODbL for OSM-derived data,
  CDLA-Permissive-2.0 for Microsoft/Google ML buildings; transportation data under ODbL per Overture's terms.
- OpenStreetMap contributors (ODbL), via Overture and via the f1-circuits raceway outlines
  (bacinger/f1-circuits, MIT licence for the repository).
- Mapzen / AWS Terrain Tiles (Terrarium): SRTM, GMTED, ETOPO1, EU-DEM and other public sources; see
  https://github.com/tilezen/joerd/blob/master/docs/attribution.md
- Track centrelines: TUM racetrack database (LGPL-3.0), as already used by the game.
