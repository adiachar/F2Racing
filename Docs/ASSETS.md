# Art assets and licences

Most scenery textures are painted procedurally in C# when the track loads (Assets/Scripts/Game/EnvTex.cs,
TracksideArt.cs, WorldBuilder.cs): original work of this project. On top of that the game uses CC0 / MIT photo scans
and models, listed below with their sources (the "Photo textures" tables and "Downloaded models and textures"). Every
procedural texture stays in the code as the fallback when a photo file is missing.

| Texture | Made in | Size | Used for |
|---|---|---|---|
| Ground detail (grass, or sand/grit) | EnvTex.GroundDetail | 256² RGBA32 + mips | terrain, verges, far ground (Ground.shader) |
| Macro variation (linear data) | EnvTex.Macro | 256² | second read in Ground.shader: dry patches, brightness drift, gravel ruts |
| Gravel | EnvTex.Gravel | 256² | gravel traps |
| Astroturf | EnvTex.Turf | 128² | strip behind the kerbs |
| Paving | EnvTex.Paving | 256² | street-circuit pavements |
| Stone blocks | EnvTex.Stone | 256² | retaining walls |
| Facade bay | EnvTex.Facade | 128² | town rows, apartment blocks |
| Foliage atlas (spruce, broad-leaf, umbrella pine, palm) | EnvTex.Foliage | 512², alpha, hand-built mips | tree cards (Foliage.shader) |
| Asphalt / road layers | WorldBuilder.RoadTex, AsphaltTex | 256² | road, pit lane, tunnel |
| Trackside atlas, crowd, fence, grime | TracksideArt | — | stands, boards (invented names only), fences, decals |

Before you add any downloaded texture: use only CC0 or public-domain sources (MIT-style with the notice kept is the
exception, see EZ-Tree below). Record the URL, author and licence of each one here. Imported GPU textures go in
`Assets/Resources/Photo/` (512-1024 px for tiling surfaces, power of two; skies up to 8192 wide), where
`Assets/Editor/F2TextureImport.cs` gives them ASTC, mips and aniso by file name; textures that C# reworks at load stay
JPEG / PNG bytes in `Assets/Resources/Textures/`.

## Photo textures (2026-11-20, wave 1: road, kerbs, grass, gravel, sky, foliage)

Network note (2026-11-20): `polyhaven.com`, `api.polyhaven.com`, `dl.polyhaven.org`, `ambientcg.com` (and cdn /
jsdelivr / unpkg / wikimedia / opengameart) are blocked by the build network's proxy (403 "no rule allows this host").
GitHub works: the files below are the original, unmodified CC0 files as committed to public GitHub repositories (Git
LFS, fetched through `media.githubusercontent.com`; the mirror paths are in `Tools/Photo/make_photos.py` SOURCES). The
licence is the original publisher's, CC0 1.0, whatever the mirror repository's own licence. Rebuild everything with
`python3 Tools/Photo/make_photos.py --fetch /tmp/photo_src && python3 Tools/Photo/make_photos.py /tmp/photo_src`.

| File | Source (original page) | Licence | Size / format in the build | Used for |
|---|---|---|---|---|
| `Resources/Photo/grass.png` | ambientCG [Grass001](https://ambientcg.com/view?id=Grass001) (2K colour, halved) | CC0 1.0 | 1024², RGBA, ASTC 6x6 + mips, aniso 4 (0.62 MB) | all green circuits' grass, verges, terrain (Ground.shader `_MainTex`, x `_Gain` to the circuit's palette); alpha = clumps (fbm) |
| `Resources/Photo/gravel.png` | Poly Haven [gravel_floor_02](https://polyhaven.com/a/gravel_floor_02) (2K diffuse, halved) | CC0 1.0 | 1024², ASTC 6x6 + mips (0.62 MB) | gravel traps (Ground.shader, `_Gain`) |
| `Resources/Photo/sky_day.png` | Poly Haven [kloofendal_48d_partly_cloudy_puresky](https://polyhaven.com/a/kloofendal_48d_partly_cloudy_puresky) (8K HDR) | CC0 1.0 | 8192 x 2048 upper hemisphere (v = sin elevation, sun at u = 0), tone-mapped offline, ASTC 6x6, no mips (7.5 MB) | afternoon skybox (SkyPhoto.shader) and so the cars' reflection probe / CarEnv cubes |
| `Resources/Textures/asphalt.bytes` | Poly Haven [asphalt_track](https://polyhaven.com/a/asphalt_track) (2K diffuse, halved) | CC0 1.0 | 1024² JPEG -> EnvTex.RoadDetail R (RG8 + hand mips, 2.8 MB with G) | road fine grain, 3.2 m tile (was the same scan at 512 / 1.6 m) |
| `Resources/Textures/kerbconc.bytes` | Poly Haven [concrete_floor_worn_001](https://polyhaven.com/a/concrete_floor_worn_001) (2K diffuse, halved) | CC0 1.0 | 1024² JPEG -> EnvTex.RoadDetail G | kerb concrete under the paint (same fetch as the asphalt grain) |
| `Resources/Textures/leafspray.bytes` | ambientCG [LeafSet002](https://ambientcg.com/view?id=LeafSet002), [LeafSet005](https://ambientcg.com/view?id=LeafSet005) (2K colour + opacity) | CC0 1.0 | 1024 x 512 PNG, 8 cut-out sprays -> EnvTex.Foliage (2048 x 1024 atlas, ETC2/DXT5 at load, 2.8 MB) | the broad-leaf tree cards' leaf clusters (real silhouettes) |

### Wave 2 (2026-11-20: skies for every time of day and rain, barriers, walls, tyre walls, buildings, stand seats)

| File | Source (original page) | Licence | Size / format in the build | Used for |
|---|---|---|---|---|
| `Resources/Photo/sky_evening_hq.png` | Poly Haven [kloppenheim_06_puresky](https://polyhaven.com/a/kloppenheim_06_puresky) (4K HDR) | CC0 1.0 | 4096 x 1024, ASTC 4x4, no mips (4.2 MB) | evening sky (golden-hour cumulus, sun 8 deg: the world's evening sun moves 13 -> 8 deg with it) |
| `Resources/Photo/sky_morning_hq.png` | Poly Haven [autumn_field_puresky](https://polyhaven.com/a/autumn_field_puresky) (4K HDR) | CC0 1.0 | 4096 x 1024, ASTC 4x4 (4.2 MB) | morning sky (clear, hazy, sun 29 deg: the world's morning sun moves 24 -> 29 deg) |
| `Resources/Photo/sky_overcast.png` | Poly Haven [overcast_soil_puresky](https://polyhaven.com/a/overcast_soil_puresky) (4K HDR) | CC0 1.0 | 4096 x 1024, ASTC 6x6 (1.9 MB) | wet weather, any time but night (the closed grey deck) |
| `Resources/Textures/zinc.bytes` | ambientCG [Metal032](https://ambientcg.com/view?id=Metal032) | CC0 1.0 | 512² JPEG -> TracksideArt atlas | Armco rail zinc mottling (rail region 256² , 4x the texels) |
| `Resources/Textures/wallconc.bytes` | Poly Haven [concrete_wall_008](https://polyhaven.com/a/concrete_wall_008) (4K, quartered) | CC0 1.0 | 1024² JPEG -> TracksideArt atlas | poured concrete walls (pores, form-tie holes, panel marks) |
| `Resources/Textures/kerbconc.bytes` (wave 1) | Poly Haven concrete_floor_worn_001 | CC0 1.0 | (as above) | also the precast barrier blocks (512 x 256 regions), tyre-wall belts, tyre tops, scuffed concrete (4x the texels) |
| `Resources/Textures/plaster, brick, rooftiles, panels, cladding, corrugated, shutter.bytes` | Poly Haven [plastered_wall](https://polyhaven.com/a/plastered_wall), [red_brick](https://polyhaven.com/a/red_brick), [clay_roof_tiles_02](https://polyhaven.com/a/clay_roof_tiles_02), [concrete_slab_wall](https://polyhaven.com/a/concrete_slab_wall), [box_profile_metal_sheet](https://polyhaven.com/a/box_profile_metal_sheet), [corrugated_iron_02](https://polyhaven.com/a/corrugated_iron_02), [painted_metal_shutter](https://polyhaven.com/a/painted_metal_shutter) | CC0 1.0 | 512² JPEGs (were 256) -> EnvTex.BuildingAtlas, now 2048 x 1024 with 256 px cells, ETC2 / DXT5 at load (2.8 MB) | building facades and roofs (RealWorld map buildings) |
| (procedural) grandstand seats and crowd | TracksideArt.Crowd | ours | 1024 x 256 (was 512 x 128) | moulded seat shells, anti-aliased people |

Not found on any reachable mirror: a CC0 astroturf / artificial-grass scan (the turf strip stays procedural) and a CC0
tyre-stack photo (tyre walls keep their painted shapes, with photo rubber wear).

Mirrors used: `kennyhassmusic/sky-city` (SourceArt/CC0: Poly Haven + ambientCG), wave 2: `Brooklyn-Dev/ray-tracing`, `FionaWright/CherryPip`, `efimco/BakeForge`, `rito15/Unity-Shader-Graph-Study`, `i-machine-think/grasp`, `Kacper79/UKSW_Escape_Within_Reach_Gr_1`, `mitarthpathak/Blender-Fable-5.2`, `dunkean/magna-urbis`, `shadow-kernel/Vortex-Engine-OpenWorld-Template`, `Shahidul-Khan2004/UIU-Sim`, `mrxizao/gundam-hathaway`; `RosaryMala/BlaseballStadiumViewer`,
`Rodrigo-AO/FS_assetto_track_creator` (presets/common/cc0, its LICENSE.txt names the Poly Haven pages), `Kitjesen/
MapPilot` (Poly Haven library), `cdav1990/OverWatch2` (8K HDR). Checked candidates, not used yet: Poly Haven leafy_grass,
sparse_grass, large_pebbles, asphalt_02, clean_asphalt, concrete_floor_painted (all 2K, same mirrors), ambientCG
Grass004, LeafSet001 / 004; skies overcast_soil_puresky, belfast_sunset_puresky, industrial_sunset_puresky,
kloofendal_43d_clear_puresky, qwantani_dusk_2_puresky, belfast_sunset_puresky, industrial_sunset_puresky, citrus_orchard_road_puresky, sunflowers_puresky (4K; wave 2 took kloppenheim_06, autumn_field, overcast_soil).

## Downloaded models and textures (Resources/Models: models.bytes + atlas.bytes)

All cut down for phones (leaf clumps, decimated wood, impostor cards, one shared 2048² atlas with mips and GPU
compression). Built offline: `Tools/Models/build_models.py` (Poly Haven set) and `Tools/Models/build_park.py`
(Monza park species and furniture, appended to the existing files). Notices also in `Resources/Models/CREDITS.txt`.

| Asset | Source | Licence | Used as |
|---|---|---|---|
| tree_small_02, jacaranda_tree, island_tree_01-03, shrub_02/03, street_lamp_01/02 | [Poly Haven](https://polyhaven.com/models) | CC0 | park trees (tree_small_02 stands in for hornbeam / elm at Monza), shrubs, the cast-iron park lamp |
| Leaf cut-outs oak.png, ash.png, pine.png | [EZ-Tree](https://github.com/dgreenheck/ez-tree) demo textures (Daniel Greenheck) | MIT (keep the notice) | clump tiles of park_oak (Quercus robur), park_robinia (Robinia / ash, pinnate leaves), park_pine (Scots pine) |
| Bark001, Bark004, Bark006, Bark014 colour maps | [ambientCG](https://ambientcg.com) via the EZ-Tree repo | CC0 | bark strips: oak (deep furrows), robinia, plane (mottled), Scots pine (orange-grey) |
| Tree skeletons park_oak / park_plane / park_robinia / park_pine | grown by `build_park.py` (our own generator) | ours | wood mesh + leaf clouds; park_plane reuses island_tree_01's broad-leaf clump tiles |
| banco_praca.glb, lixeira.glb | [kit-cidade-lowpoly](https://github.com/kayquer/kit-cidade-lowpoly) (kayquer) | CC0 | park_bench (recoloured: cast iron, varnished slats), park_bin (recoloured dark green); flat colours in an 8 px palette, no logos |

## Fonts (Assets/Resources/Fonts, loaded by UiKit.cs)

| Font | Files | Licence | Used for |
|---|---|---|---|
| Archivo (Omnibus-Type, from github.com/google/fonts `ofl/archivo`) | Archivo-Regular / -SemiBold / -ExtraBold / -ExtraBoldItalic.ttf: static instances (wdth 100; wght 400 / 600 / 800 / 800 italic) cut from the variable font with fontTools `varLib.instancer` | SIL OFL 1.1 (OFL-Archivo.txt; no Reserved Font Name) | the front end ("Paper" look) |
| Titillium Web | TitilliumWeb-*.ttf | SIL OFL 1.1 (OFL-TitilliumWeb.txt) | the in-race HUD; fallback if Archivo fails to load |
