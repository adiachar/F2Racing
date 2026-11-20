# inspect_glb.py — prints the meshes, materials, textures, skin joints and animations of a GLB / glTF
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'People'))
from gltf_io import Gltf
for p in sys.argv[1:]:
    G = Gltf(p); g = G.g
    print('==', os.path.basename(p), 'nodes', len(g.nodes), 'skins', len(g.skins), 'anims', len(g.animations))
    for m in g.meshes:
        for pr in m.primitives:
            mat = g.materials[pr.material] if pr.material is not None else None
            tex = None
            if mat and mat.pbrMetallicRoughness and mat.pbrMetallicRoughness.baseColorTexture:
                im = g.images[g.textures[mat.pbrMetallicRoughness.baseColorTexture.index].source]; tex = im.name or im.uri
            print('  mesh', m.name, 'mat', mat.name if mat else None, 'tex', tex, 'tris', g.accessors[pr.indices].count // 3, 'verts', g.accessors[pr.attributes.POSITION].count)
    for s in g.skins: print('  skin', len(s.joints), [g.nodes[j].name for j in s.joints][:8], '...')
    for a in g.animations: print('  anim', a.name, len(a.channels), 'dur', max(G.acc(sm.input)[-1, 0] for sm in a.samplers))
    for i, im in enumerate(g.images): print('  img', i, im.name, im.mimeType)
