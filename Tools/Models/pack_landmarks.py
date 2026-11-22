#!/usr/bin/env python3
"""Landmark models (buildings, stands, trackside structures) from Adi's asset pack (RacingSim_Assets.zip, CC0, made for
this game) for hand placement with the map editor (MapEdits.cs, F2MapEditor.cs).

    /path/to/python-with-bpy pack_landmarks.py <pack assets/ dir>      (pip install bpy; Python 3.13)

Writes
  Assets/Resources/Landmarks/landmarks.bytes   every model's mesh, one submesh per material
  Assets/Resources/Photo/lm_<material>.png     each shared material's colour map (<= 1024, ASTC via F2TextureImport)

landmarks.bytes (little endian)
  "F2LM" u16 version (1) u16 nMaterials, per material: u8 len, key utf8 (texture = Photo/lm_<key>)
  u16 nModels, per model: u8 len, name utf8, f32 bboxMin[3], f32 bboxSize[3], u8 nSub,
    per submesh: u8 material, u32 nVerts, u32 nTris,
      nVerts x { f32 x, y, z; i8 nx, ny, nz; u8 0; f32 u, v }   (Unity space: metres, y up, base centre at the origin)
      nTris x 3 x u16 indices (Unity winding: clockwise front faces)
Coordinates: glTF x is negated (right- to left-handed), as Tools/Models/build_models.py does. Cut-out railing materials are
left out (the plain baked shader has no alpha test; the game draws its own railings).
"""
import os, sys, glob, struct
import bpy
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..", "..")
OUT = os.path.join(ROOT, "Assets", "Resources", "Landmarks", "landmarks.bytes")
PHOTO = os.path.join(ROOT, "Assets", "Resources", "Photo")
pack = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else sys.argv[1]
BLD = os.path.join(pack, "buildings")
SKIP_MATS = ("railing_cutout",)

def mat_key(mat):
    if mat is None or not mat.use_nodes: return None
    for nd in mat.node_tree.nodes:
        if nd.type == "BSDF_PRINCIPLED":
            inp = nd.inputs.get("Base Color")
            if inp and inp.is_linked:
                src = inp.links[0].from_node
                if src.type == "TEX_IMAGE" and src.image:
                    n = os.path.splitext(src.image.name)[0]
                    return n[:-7] if n.endswith("_albedo") else n
    return None

mats, models = [], []
files = sorted(glob.glob(os.path.join(BLD, "*", "*.glb")))
for f in files:
    name = os.path.splitext(os.path.basename(f))[0]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=f)
    subs = {}
    for o in bpy.data.objects:
        if o.type != "MESH": continue
        me = o.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh()
        me.calc_loop_triangles()
        M = o.matrix_world
        N3 = M.to_3x3().inverted().transposed()
        uvl = me.uv_layers.active.data if me.uv_layers.active else None
        cn = me.corner_normals
        for tri in me.loop_triangles:
            key = mat_key(o.material_slots[tri.material_index].material) if o.material_slots else None
            if key is None or key.startswith(SKIP_MATS): continue
            s = subs.setdefault(key, ([], []))
            vs, ix = s
            corner = []
            for li in tri.loops:
                vi = me.loops[li].vertex_index
                p = M @ me.vertices[vi].co
                n = (N3 @ cn[li].vector).normalized()
                uv = uvl[li].uv if uvl else (0.0, 0.0)
                # blender (x, y, z) z-up = glTF (x, z, -y) -> Unity (-x, z, -y)
                corner.append(len(vs))
                vs.append((-p.x, p.z, -p.y, -n.x, n.z, -n.y, uv[0], uv[1]))
            # handedness flip (x negated) keeps glTF's counter-clockwise order as Unity's clockwise front faces
            ix.append(tuple(corner))
        o.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh_clear()
    if not subs: print("skip", name); continue
    allv = [v for s in subs.values() for v in s[0]]
    mn = [min(v[i] for v in allv) for i in range(3)]; mx = [max(v[i] for v in allv) for i in range(3)]
    for k in subs:
        if k not in mats: mats.append(k)
    models.append((name, mn, [mx[i] - mn[i] for i in range(3)], subs))
    print(name, "subs", len(subs), "tris", sum(len(s[1]) for s in subs.values()), "size", [round(mx[i] - mn[i], 1) for i in range(3)])

os.makedirs(os.path.dirname(OUT), exist_ok=True)
b = bytearray(b"F2LM") + struct.pack("<HH", 1, len(mats))
for k in mats:
    e = k.encode(); b += struct.pack("<B", len(e)) + e
b += struct.pack("<H", len(models))
for name, mn, sz, subs in models:
    e = name.encode(); b += struct.pack("<B", len(e)) + e + struct.pack("<6f", *mn, *sz) + struct.pack("<B", len(subs))
    for k, (vs, ix) in subs.items():
        assert len(vs) < 65536, name
        b += struct.pack("<BII", mats.index(k), len(vs), len(ix))
        for v in vs:
            b += struct.pack("<3f3bB2f", v[0], v[1], v[2], *[max(-127, min(127, int(round(c * 127)))) for c in v[3:6]], 0, v[6], v[7])
        for t in ix: b += struct.pack("<3H", *t)
open(OUT, "wb").write(b)
print("landmarks.bytes", len(b), "bytes,", len(models), "models,", len(mats), "materials")

for k in mats:
    src = None
    for d in ("materials", "facades"):
        p = os.path.join(BLD, d, k + "_albedo.png")
        if os.path.exists(p): src = p; break
    if src is None: print("no texture for", k); continue
    im = Image.open(src).convert("RGB")
    if im.size[0] > 1024: im = im.resize((1024, im.size[1] * 1024 // im.size[0]), Image.LANCZOS)
    im.save(os.path.join(PHOTO, "lm_" + k + ".png"), optimize=True)
print("textures written")
