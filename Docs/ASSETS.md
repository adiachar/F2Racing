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
