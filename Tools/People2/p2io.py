# p2io.py — loading for the People2 converter: a glTF / GLB (Blender's export of a Mixamo FBX, or a Sketchfab glTF)
# into Unity space (left-handed: x flipped), as one list of primitives with world-space rest positions, normals, UVs,
# skin (joint node ids + weights) and the base-colour image; the node graph for skeletons and clips.
import os, sys, io
import numpy as np
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'People'))
from gltf_io import Gltf, quat_mat, mat_quat, trs, sample_channel, slerp  # noqa: F401

S = np.diag([-1.0, 1.0, 1.0, 1.0])


def to_unity(m):
    return S @ m @ S


def strip(nm):
    """'mixamorig7:LeftArm' / 'LeftArm_27' -> 'LeftArm'"""
    if ':' in nm: nm = nm.split(':')[-1]
    if '_' in nm and nm.rsplit('_', 1)[1].isdigit(): nm = nm.rsplit('_', 1)[0]
    return nm


class Model:
    def __init__(self, path):
        self.G = G = Gltf(path); g = G.g
        self.byname = {}
        for i, n in enumerate(g.nodes):
            if n.name: self.byname.setdefault(strip(n.name), i)
        self.prims = []
        for ni, n in enumerate(g.nodes):
            if n.mesh is None: continue
            Wn = G.world(ni)
            skin = g.skins[n.skin] if n.skin is not None else None
            for pr in g.meshes[n.mesh].primitives:
                P = G.acc(pr.attributes.POSITION); N = G.acc(pr.attributes.NORMAL) if pr.attributes.NORMAL is not None else None
                UV = G.acc(pr.attributes.TEXCOORD_0) if pr.attributes.TEXCOORD_0 is not None else np.zeros((len(P), 2))
                I = G.acc(pr.indices).astype(int).reshape(-1, 3)
                mat = g.materials[pr.material] if pr.material is not None else None
                d = dict(name=g.meshes[n.mesh].name, mat=mat.name if mat else '', uv=UV.copy(), I=I)
                if skin is not None:
                    J = G.acc(pr.attributes.JOINTS_0).astype(int); Wt = G.acc(pr.attributes.WEIGHTS_0)
                    ibm = G.acc(skin.inverseBindMatrices).reshape(-1, 4, 4).transpose(0, 2, 1)
                    joints = np.array(skin.joints)
                    # bind-pose world positions: sum w * jointWorldAtBind * ibm * p  (at bind, jointWorld @ ibm = identity-ish
                    # mapping into the mesh's bind space); use the nodes' rest world matrices
                    Mj = np.array([G.world(j) @ ibm[k] for k, j in enumerate(joints)])
                    M = np.einsum('vk,vkab->vab', Wt, Mj[J])
                    Pw = np.einsum('vab,vb->va', M, np.c_[P, np.ones(len(P))])[:, :3]
                    Nw = np.einsum('vab,vb->va', M[:, :3, :3], N) if N is not None else None
                    d['J'] = joints[J]; d['W'] = Wt
                else:
                    Pw = (Wn @ np.c_[P, np.ones(len(P))].T).T[:, :3]
                    Nw = (Wn[:3, :3] @ N.T).T if N is not None else None
                    d['J'] = None; d['W'] = None
                Pw = Pw.copy(); Pw[:, 0] *= -1
                if Nw is not None: Nw = Nw.copy(); Nw[:, 0] *= -1; Nw /= np.linalg.norm(Nw, axis=1, keepdims=True) + 1e-12
                d['P'] = Pw; d['N'] = Nw; d['I'] = I[:, [0, 2, 1]]  # (mirroring flips the winding)
                d['img'] = self.base_image(mat); d['imgname'] = self.image_name(mat)
                d['color'] = list(mat.pbrMetallicRoughness.baseColorFactor) if mat and mat.pbrMetallicRoughness and mat.pbrMetallicRoughness.baseColorFactor else [1, 1, 1, 1]
                self.prims.append(d)

    def base_image(self, mat):
        from PIL import Image
        if mat is None or mat.pbrMetallicRoughness is None or mat.pbrMetallicRoughness.baseColorTexture is None: return None
        g = self.G.g; im = g.images[g.textures[mat.pbrMetallicRoughness.baseColorTexture.index].source]
        if im.bufferView is not None:
            bv = g.bufferViews[im.bufferView]; raw = self.G.buffer(bv.buffer)[(bv.byteOffset or 0):(bv.byteOffset or 0) + bv.byteLength]
            return Image.open(io.BytesIO(raw)).convert('RGBA')
        return Image.open(os.path.join(os.path.dirname(self.G.path), im.uri)).convert('RGBA')

    def image_name(self, mat):
        if mat is None or mat.pbrMetallicRoughness is None or mat.pbrMetallicRoughness.baseColorTexture is None: return None
        g = self.G.g; im = g.images[g.textures[mat.pbrMetallicRoughness.baseColorTexture.index].source]
        return os.path.basename(im.uri) if im.uri else im.name

    def world_u(self, node):
        """rest world matrix of a node, Unity space"""
        return to_unity(self.G.world(node))
