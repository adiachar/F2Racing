# Convert the SF-26 glb into a compact, logo-free, mobile-ready mesh file for the game.
# Output: car.bytes (custom binary, read by CarModel.cs) — Unity frame: x right, y up, z forward, origin = CG on the ground.
import trimesh, numpy as np, struct, fast_simplification, sys
WHEELBASE, FRONTW = 3.135, 0.45            # physics car: front axle at +a, rear at -b from CG
B = WHEELBASE * FRONTW; A = WHEELBASE - B
sc = trimesh.load('sf26/source/f1_2026_sf26.glb', force='scene')
parts = {}
for g in sc.dump(concatenate=False):
    m = g.visual.material.name if hasattr(g.visual, 'material') else '?'
    parts.setdefault(m, []).append(g)
def get(names):
    ms = [mm for n in names for mm in parts.get(n, [])]
    return trimesh.util.concatenate(ms) if ms else None

# material groups (logos, decals, numbers, cockpit interior and LEDs are dropped)
GROUPS = {
    'paint':   ['sf25_paints'],
    'carbon':  ['sf25_carbon1', 'sf25_carbon2', 'sf25_carbon3', 'sf25_generics', 'sf25_details', 'black_s.002'],
    'white':   ['sf25_carbon1_white'],
    'metal':   ['chrome.005', 'sf25_mirrorl'],
    'light':   ['rear_light.001'],
    'tyre':    ['tyre_sides', 'tyre_tread'],
    'hub':     ['sf25_wheel_hub', 'discs.008'],
}
DROP_REPORT = sorted(set(parts) - {n for v in GROUPS.values() for n in v})
print('dropped (logos/decals/interior):', DROP_REPORT)

# orientation: length axis, up axis
tyre = get(GROUPS['tyre'])
ext = tyre.bounds[1] - tyre.bounds[0]
print('tyre bounds', np.round(tyre.bounds, 3))
# find 4 wheel clusters in the horizontal plane (length = axis with the largest extent among x/z)
L_AX = 2 if ext[2] > ext[0] else 0; W_AX = 0 if L_AX == 2 else 2
c = tyre.triangles_center
mid_l = (c[:, L_AX].min() + c[:, L_AX].max()) / 2
centers = {}
for front_side in (1, -1):
    for side in (1, -1):
        sel = (np.sign(c[:, L_AX] - mid_l) == front_side) & (np.sign(c[:, W_AX]) == side)
        sub = tyre.submesh([np.where(sel)[0]], append=True)
        centers[(front_side, side)] = (sub.bounds[0] + sub.bounds[1]) / 2, sub.bounds[1] - sub.bounds[0]
for k, (ctr, sz) in centers.items(): print('wheel', k, np.round(ctr, 3), 'size', np.round(sz, 3))
# front axle = the narrower tyres
w_pos = centers[(1, 1)][1][W_AX]; w_neg = centers[(-1, 1)][1][W_AX]
front_sign = 1 if w_pos < w_neg else -1
fz = np.mean([centers[(front_sign, s)][0][L_AX] for s in (1, -1)]); rz = np.mean([centers[(-front_sign, s)][0][L_AX] for s in (1, -1)])
wb_model = abs(fz - rz); scale = WHEELBASE / wb_model
ground = tyre.bounds[0][1]
print(f'front side {front_sign}, model wheelbase {wb_model:.3f} -> scale {scale:.4f}')

def to_unity(v):
    # unity z = forward (towards the front axle); x = across; y = up. Uniform scale; CG origin.
    l = (v[:, L_AX] - rz) * front_sign * (1 if front_sign == np.sign(fz - rz) else -1)
    l = (v[:, L_AX] - rz) * np.sign(fz - rz)          # 0 at rear axle, + towards front
    z = l * scale - B
    x = v[:, W_AX] * scale * (1 if L_AX == 2 else -1)
    y = (v[:, 1] - ground) * scale
    out = np.stack([x, y, z], 1)
    # keep handedness consistent: if we swapped axes (det<0) the caller flips winding
    return out
det_flip = (L_AX == 0)  # swapping x/z is a mirror; also the sign flips below
def export_mesh(mesh, target_tris, smooth_angle=35):
    if mesh is None: return None
    v, f = mesh.vertices, mesh.faces
    if len(f) > target_tris:
        v, f = fast_simplification.simplify(v, f, target_reduction=1 - target_tris / len(f))
    m = trimesh.Trimesh(v, f, process=True)
    m = m.smooth_shaded if smooth_angle is None else trimesh.graph.smooth_shade(m, angle=np.radians(smooth_angle))
    pos = to_unity(np.asarray(m.vertices)); nrm = to_unity_dir(np.asarray(m.vertex_normals))
    faces = np.asarray(m.faces)
    # mirror check: L_AX/W_AX mapping and sign flips can mirror the mesh; fix winding with the determinant
    if MIRROR: faces = faces[:, ::-1]
    return pos.astype(np.float32), nrm.astype(np.float32), faces.astype(np.int32)

# linear part of to_unity, for normals and the mirror test
def to_unity_dir(d):
    M = np.zeros((3, 3)); s = np.sign(fz - rz)
    M[2, L_AX] = s; M[0, W_AX] = (1 if L_AX == 2 else -1); M[1, 1] = 1
    return d @ M.T
Mlin = np.zeros((3, 3)); Mlin[2, L_AX] = np.sign(fz - rz); Mlin[0, W_AX] = (1 if L_AX == 2 else -1); Mlin[1, 1] = 1
# glTF is right-handed, Unity left-handed: a mapping with det=+1 means the result is mirrored for Unity -> keep CCW order
# (Unity front faces are clockwise as seen by the camera). det=-1 cancels that.
MIRROR = np.linalg.det(Mlin) < 0
print('axis map det', np.linalg.det(Mlin), 'flip winding', MIRROR)

out = []  # (name, matId, pivot(xyz), pos, nrm, faces)
MAT = {'paint_red': 0, 'paint_white': 1, 'carbon': 2, 'metal': 3, 'tyre': 4, 'hub': 5, 'light': 6, 'white': 1}
# body: paint split into red (front/lower) and white (engine cover top / rear upper) by triangle position
paint = get(GROUPS['paint'])
pc = to_unity(paint.triangles_center)
white_sel = (pc[:, 2] < 0.25) & (pc[:, 1] > 0.62 * scale / scale)  # behind the cockpit and above sidepod height
for nm, sel, tgt in (('paint_red', ~white_sel, 9000), ('paint_white', white_sel, 5000)):
    sub = paint.submesh([np.where(sel)[0]], append=True)
    r = export_mesh(sub, tgt); out.append((nm, MAT[nm], (0, 0, 0)) + r)
for g, tgt in (('carbon', 26000), ('white', 800), ('metal', 1500), ('light', 400)):
    r = export_mesh(get(GROUPS[g]), tgt)
    if r: out.append((g, MAT[g], (0, 0, 0)) + r)
# wheels: split tyre + hub per wheel, pivot at the wheel centre (so it can spin and steer)
hub = get(GROUPS['hub'])
for key, nm in (((front_sign, 1), 'FL'), ((front_sign, -1), 'FR'), ((-front_sign, 1), 'RL'), ((-front_sign, -1), 'RR')):
    piv = None
    for src, kind, tgt in ((tyre, 'tyre', 1600), (hub, 'hub', 1100)):
        c2 = src.triangles_center
        sel = (np.sign(c2[:, L_AX] - mid_l) == key[0]) & (np.sign(c2[:, W_AX]) == key[1])
        sub = src.submesh([np.where(sel)[0]], append=True)
        if kind == 'tyre':
            piv = to_unity(((sub.bounds[0] + sub.bounds[1]) / 2)[None])[0]
            pieces = [('tyre', sub, tgt)]
        else:
            # rim/wheel cover sits on the outboard half and spins; brake caliper, upright and duct sit
            # inboard and must only steer (spinning them made the wheels look disconnected)
            cu = to_unity(sub.triangles_center); outward = np.sign(piv[0]) * (cu[:, 0] - piv[0]) > 0.0
            pieces = [('hub', sub.submesh([np.where(outward)[0]], append=True), 800),
                      ('hubstatic', sub.submesh([np.where(~outward)[0]], append=True), 500)]
        for kname, mesh, t2 in pieces:
            r = export_mesh(mesh, t2, smooth_angle=50)
            pos = r[0] - piv.astype(np.float32)
            out.append((f'wheel_{nm}_{kname}', MAT['hub' if kname != 'tyre' else 'tyre'], tuple(piv)) + (pos, r[1], r[2]))
# helmet position: just ahead of the airbox, above the cockpit
total = 0
with open('car.bytes', 'wb') as f:
    f.write(b'F2M1'); f.write(struct.pack('<i', len(out)))
    for nm, mid, piv, pos, nrm, faces in out:
        b = nm.encode(); f.write(struct.pack('<i', len(b))); f.write(b)
        f.write(struct.pack('<i3f', mid, *piv))
        f.write(struct.pack('<ii', len(pos), faces.size))
        f.write(pos.tobytes()); f.write(nrm.tobytes()); f.write(faces.astype('<i4').tobytes())
        total += len(faces); print(f'{nm:18s} mat {mid} verts {len(pos):6d} tris {len(faces):6d} pivot {np.round(piv,3)}')
allp = np.concatenate([o[3] + np.array(o[2], np.float32) for o in out])
print('TOTAL tris', total, 'bounds', np.round(allp.min(0), 3), np.round(allp.max(0), 3))
import os; print('file MB', os.path.getsize('car.bytes') / 1e6)
