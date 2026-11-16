#!/usr/bin/env python3
"""Download chosen Poly Haven models (CC0): gltf + bin + 1k diffuse textures only -> $1 (default /tmp/ph_models)."""
import json, os, sys, urllib.request
op = urllib.request.build_opener(); op.addheaders = [('User-Agent', 'model-fetch/1.0')]; urllib.request.install_opener(op)
IDS = ["tree_small_02","jacaranda_tree","island_tree_01","island_tree_02","island_tree_03",
       "shrub_01","shrub_02","shrub_03","street_lamp_01","street_lamp_02"]
out = sys.argv[1] if len(sys.argv) > 1 else "/tmp/ph_models"
def get(url, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if os.path.exists(path): return
    open(path, 'wb').write(urllib.request.urlopen(url).read())
for i in IDS:
    info = json.load(urllib.request.urlopen(f"https://api.polyhaven.com/files/{i}"))
    g = info["gltf"]["1k"]["gltf"]
    d = os.path.join(out, i)
    get(g["url"], f"{d}/{i}.gltf")
    for rel, f in g["include"].items():
        if rel.endswith(".bin") or "_diff_" in rel:
            get(f["url"], f"{d}/{rel}")
    for k in ("leaves_alpha", "Alpha"):
        if k in info:
            u = info[k]["1k"]["png"]["url"]; get(u, f"{d}/textures/{os.path.basename(u)}")
    print("ok", i)
