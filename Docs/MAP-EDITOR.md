# Editing a circuit in Unity (F2Racing > Map Editor)

You edit a circuit while the game is running inside Unity. The game still builds the circuit from map data, then adds your saved edits on top every time.

## One-time setup
1. Install Unity Hub, then the editor **6000.0.85f2** (Hub → Installs → Install Editor → Archive). Sign in with your own Unity account (the free Personal licence).
2. On GitHub, open adiachar/F2Racing, switch to branch **gfx**, then Code → **Download ZIP**, and unzip it.
3. In Unity Hub: Add → Add project from disk → pick the unzipped folder. The first import takes 10-30 minutes.

## Each editing session (example: Monza)
1. Press **Play** ▶. In the game, pick **Monza** and start a race (any mode).
2. Press **Pause** ⏸.
3. Menu **F2Racing → Map Editor**. Dock the window next to the Inspector.
4. Click the **Scene** tab. Fly around: hold the right mouse button + W A S D (Q / E down / up, Shift = faster). Click an object, then press F to jump to it.
5. If you saved edits before, click **Show my saved edits**.
6. Add things. Each marker is dropped at the middle of the Scene view. Move it with W, turn it with E, scale it with R.

   | Button | What it does |
   | --- | --- |
   | Add remove area (red box) | Nothing is built inside it: map buildings, trees, bushes, undergrowth, street lamps and benches. Stretch it with R. |
   | Add tree (green post) | A real tree here. The post's height x 2 = the tree's height. |
   | Add building (grey box) | A simple building with the town's walls and roof. Box size = building size. Pick its type, roof and colour in the Inspector. |
   | Add this model | One of the asset pack's 37 real models (grandstands, pit modules, villa, church, hotel, footbridge, billboards, kiosks...), shown as it will look. Put its base on the ground (W, up and down). |

7. Delete a marker: select it and press **Delete**.
8. Click **Save my edits**. This writes `Assets/Resources/MapEdits/monza.json`, and the folder opens.
9. Press **Stop**, then **Play** again and drive to the same place: the circuit is rebuilt with your edits.
10. When you're happy, **send `monza.json` to Claude** in the chat. Claude adds it to the game for everyone and builds the APK.

## Good to know
- Markers vanish when you press Stop. Your saved file stays.
- Remove areas don't yet remove things the trackside code builds: the pit building, the grandstands, barriers, fences and kerbs. Ask Claude if you need those.
- Edits cost nothing per frame: everything is merged into the same batches as the rest of the world.
- Don't change other files in the project. Only send the `.json` files from `Assets/Resources/MapEdits/`.
- Besides your edits, most circuits also load a generated fill (`Resources/MapEdits/Auto/<circuit>.json`, made by `Tools/Scenery`): grandstands, hospitality units, kiosks, sheds, farms, tree lines and woods in the empty land beside the track. The editor does not show it or save it, but your remove areas clear it too (and anything of it that would stand on a trackside building is left out).
