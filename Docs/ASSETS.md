# Art assets and licences

No third-party textures, models or images are used for the scenery. Every scenery texture is painted procedurally
in C# when the track loads (Assets/Scripts/Game/EnvTex.cs, TracksideArt.cs, WorldBuilder.cs). It is original work
belonging to this project, so no attribution is needed and no outside licence applies.

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

Before you add any downloaded texture: use only CC0 or public-domain sources. Record the URL, author and licence of
each one in this table, keep it between 256 and 512 px (power of two), and import it with mipmaps and platform
compression (ASTC/ETC2).

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
