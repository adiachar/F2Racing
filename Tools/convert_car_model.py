# Convert the SF-26 glb into a compact, logo-free, mobile-ready mesh file for the game.
# Output: car.bytes (custom binary, read by CarModel.cs) — Unity frame: x right, y up, z forward, origin = CG on the ground.
import trimesh, numpy as np, struct, fast_simplification, sys
# usage: convert.py [lod|far|full]
MODE = sys.argv[1] if len(sys.argv) > 1 else ''
# lod = rival cars close by, far = rival cars in the distance (the game swaps between them by distance)
LOD = {'lod': 0.28, 'far': 0.06}.get(MODE, 1.0)
OUT = {'lod': 'car_lod.bytes', 'far': 'car_far.bytes'}.get(MODE, 'car.bytes')
FULL = len(sys.argv) > 1 and sys.argv[1] == 'full'   # diagnostic: nothing simplified, dropped groups included
if FULL: OUT = 'car_full.bytes'
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
def export_mesh(mesh, target_tris, smooth_angle=35, lod_tris=None):
    if mesh is None: return None
    v, f = mesh.vertices, mesh.faces
    target_tris = max(60, int(target_tris * LOD)) if lod_tris is None or LOD >= 1 else (lod_tris if MODE == 'lod' else max(60, lod_tris // 5))
    if len(f) > target_tris and not FULL:
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
# body paint: one colour, kept at full detail (it is the smooth surface the eye rests on, and what the driver sees
# from the cockpit; simplifying it left jagged shards)
r = export_mesh(get(GROUPS['paint']), 31000); out.append(('paint_red', MAT['paint_red'], (0, 0, 0)) + r)
for g, tgt in (('carbon', 56000), ('white', 1000), ('metal', 1500), ('light', 400)):
    r = export_mesh(get(GROUPS[g]), tgt)
    if r and g == 'carbon':
        # the halo's centre pillar as its own part: the cockpit camera hides it (a driver sees round it with two
        # eyes; a single camera would show it as a bar down the middle of the screen)
        pos, nrm, faces = r; c = pos[faces].mean(1)
        sel = (np.abs(c[:, 0]) < 0.06) & (c[:, 2] > 0.55) & (c[:, 2] < 1.2) & (c[:, 1] > 0.71) & (c[:, 1] < 0.80)
        out.append(('carbon', MAT[g], (0, 0, 0), pos, nrm, faces[~sel]))
        if sel.any(): out.append(('halo_pillar', MAT[g], (0, 0, 0), pos, nrm, faces[sel]))
        print('halo pillar triangles', int(sel.sum()))
    elif r: out.append((g, MAT[g], (0, 0, 0)) + r)
if FULL:
    for nm, names, mid in (('decals', ['sf25_decals', 'sf25_decals2', 'numbers'], 7), ('sw', ['sf25_sw', 'sf25_sw_carbon', 'sf25_sw_decals', 'sf25_lcd'], 8), ('misc', ['gp21_cinture.005', 'driver_color', 'glass', 'plastic_interior.002'], 9)):
        r = export_mesh(get(names), 10**9)
        if r: out.append((nm, mid, (0, 0, 0)) + r)
# wheels: split tyre + hub per wheel, pivot at the wheel centre (so it can spin and steer)
hub = get(GROUPS['hub'])
for key, nm in (((front_sign, 1), 'FL'), ((front_sign, -1), 'FR'), ((-front_sign, 1), 'RL'), ((-front_sign, -1), 'RR')):
    piv = None
    for src, kind in ((tyre, 'tyre'), (hub, 'hub')):
        c2 = src.triangles_center
        sel = (np.sign(c2[:, L_AX] - mid_l) == key[0]) & (np.sign(c2[:, W_AX]) == key[1])
        sub = src.submesh([np.where(sel)[0]], append=True)
        if kind == 'tyre':
            piv = to_unity(((sub.bounds[0] + sub.bounds[1]) / 2)[None])[0]
            # The model's wheels have camber and toe; spun about the car's cross axis they would wobble.
            # Find the wheel's own axis from the FULL-detail tyre (direction of least spread), stand the wheel up
            # straight, and put the axis exactly through the pivot.
            full = to_unity(np.asarray(sub.vertices)) - piv
            cc = full - full.mean(0); ev, evec = np.linalg.eigh(cc.T @ cc); ax = evec[:, 0] * np.sign(evec[0, 0])
            vv = np.cross(ax, [1, 0, 0]); sn = np.linalg.norm(vv); cs = ax[0]
            K = np.array([[0, -vv[2], vv[1]], [vv[2], 0, -vv[0]], [-vv[1], vv[0], 0]])
            Rw = np.eye(3) if sn < 1e-9 else np.eye(3) + K + K @ K * ((1 - cs) / sn ** 2)
            fr = full @ Rw.T; shift = np.array([0, (fr[:, 1].min() + fr[:, 1].max()) / 2, (fr[:, 2].min() + fr[:, 2].max()) / 2])
            print(f'wheel {nm}: straightened by {np.degrees(np.arcsin(sn)):.2f} deg, centre moved {np.round(shift * 1000, 1)} mm')
            pieces = [('tyre', sub, 3200, 1500)]
        else:
            # rim/wheel cover sits on the outboard half and spins; brake caliper, upright and duct sit
            # inboard and must only steer (spinning them made the wheels look disconnected)
            cu = to_unity(sub.triangles_center); outward = np.sign(piv[0]) * (cu[:, 0] - piv[0]) > 0.0
            pieces = [('hub', sub.submesh([np.where(outward)[0]], append=True), 10**9, 1500),   # simplifying the rim bends it: keep it
                      ('hubstatic', sub.submesh([np.where(~outward)[0]], append=True), 900, 150)]
        for kname, mesh, t2, t3 in pieces:
            r = export_mesh(mesh, t2, smooth_angle=50, lod_tris=t3)
            pos = (r[0] - piv.astype(np.float32)) @ Rw.T - shift; nr = (r[1] @ Rw.T).astype(np.float32)
            # the simplifier sometimes throws a vertex far out of place; keep every vertex inside the original part
            fv = (to_unity(np.asarray(mesh.vertices)) - piv) @ Rw.T - shift; rmax = np.hypot(fv[:, 1], fv[:, 2]).max()
            pos[:, 0] = np.clip(pos[:, 0], fv[:, 0].min(), fv[:, 0].max())
            rr = np.hypot(pos[:, 1], pos[:, 2]); k = np.minimum(1, rmax / np.maximum(rr, 1e-9)); pos[:, 1] *= k; pos[:, 2] *= k
            pos = pos.astype(np.float32)
            out.append((f'wheel_{nm}_{kname}', MAT['hub' if kname != 'tyre' else 'tyre'], tuple(piv)) + (pos, nr, r[2]))
# steering wheel: its own part, stored facing straight down the car (axis = z) so the game can turn it; the part
# name carries how far the column is tilted back
sw = get(['sf25_sw', 'sf25_sw_carbon'])
if sw is not None and MODE != 'far':
    r = export_mesh(sw, 3000, smooth_angle=40)
    full = to_unity(np.asarray(sw.vertices)); c = (full.min(0) + full.max(0)) / 2; c[0] = 0
    cc = full - full.mean(0); ev, evec = np.linalg.eigh(cc.T @ cc); ax = evec[:, 0]; ax[0] = 0; ax /= np.linalg.norm(ax); ax *= np.sign(ax[2])
    pitch = np.degrees(np.arctan2(-ax[1], ax[2]))        # Unity: +pitch about x tips +z down
    a = np.radians(-pitch); Rx = np.array([[1, 0, 0], [0, np.cos(a), -np.sin(a)], [0, np.sin(a), np.cos(a)]])
    out.append((f'steer_{pitch:.2f}', MAT['carbon'], tuple(c)) + (((r[0] - c.astype(np.float32)) @ Rx.T).astype(np.float32), (r[1] @ Rx.T).astype(np.float32), r[2]))
    print('steering wheel centre', np.round(c, 3), 'column pitch', round(pitch, 2))
# helmet position: just ahead of the airbox, above the cockpit
total = 0
with open(OUT, 'wb') as f:
    f.write(b'F2M1'); f.write(struct.pack('<i', len(out)))
    for nm, mid, piv, pos, nrm, faces in out:
        b = nm.encode(); f.write(struct.pack('<i', len(b))); f.write(b)
        f.write(struct.pack('<i3f', mid, *piv))
        f.write(struct.pack('<ii', len(pos), faces.size))
        f.write(pos.tobytes()); f.write(nrm.tobytes()); f.write(faces.astype('<i4').tobytes())
        total += len(faces); print(f'{nm:18s} mat {mid} verts {len(pos):6d} tris {len(faces):6d} pivot {np.round(piv,3)}')
allp = np.concatenate([o[3] + np.array(o[2], np.float32) for o in out])
print('TOTAL tris', total, 'bounds', np.round(allp.min(0), 3), np.round(allp.max(0), 3))
import os; print('file MB', os.path.getsize(OUT) / 1e6)
