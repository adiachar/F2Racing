# fbx2glb.py — Blender (background) script: one FBX (Mixamo character or animation) -> GLB, untouched
# (skin, all bones, animations, embedded textures). Run:
#   blender -b --factory-startup -P fbx2glb.py -- in.fbx out.glb
import sys, bpy
argv = sys.argv[sys.argv.index('--') + 1:]
src, dst = argv[0], argv[1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=src, automatic_bone_orientation=False, ignore_leaf_bones=False)
try:
    bpy.ops.file.pack_all()
except Exception as e:
    print('pack:', e)
bpy.ops.export_scene.gltf(filepath=dst, export_format='GLB', export_animations=True, export_skins=True,
                          export_apply=False, export_yup=True, export_image_format='AUTO',
                          export_force_sampling=True, export_frame_range=False)
