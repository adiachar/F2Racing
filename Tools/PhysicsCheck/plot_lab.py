# Turns the physics lab data (dotnet run -- lab <dir>) into one sheet of charts.
# usage: python3 plot_lab.py <dir> <out.png>
import sys, csv, collections
import matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as plt
d = sys.argv[1]; out = sys.argv[2]
def rows(name):
    with open(f'{d}/{name}') as f: return list(csv.DictReader(f))
plt.rcParams.update({'font.size': 9, 'axes.grid': True, 'grid.alpha': 0.3})
fig, ax = plt.subplots(4, 2, figsize=(13, 17))

# 1 tyre curves
a = ax[0, 0]; g = collections.defaultdict(list)
for r in rows('tyre.csv'): g[float(r['load_ratio'])].append((float(r['alpha_deg']), float(r['mu_y'])))
for lr, pts in sorted(g.items()): a.plot(*zip(*pts), label=f'load x{lr:g}')
a.axvspan(5, 8, color='orange', alpha=0.12, label='F1 slick peak (5-8°)')
a.set(title='Tyre: side grip vs slip angle (magic formula)', xlabel='slip angle (deg)', ylabel='grip coefficient μy'); a.legend()

# 2 envelope
a = ax[0, 1]; e = collections.defaultdict(list)
for r in rows('envelope.csv'): e[r['kind']].append((float(r['kmh']), float(r['sim_g']), float(r['formula_g'])))
cols = {'brake': 'C3', 'corner': 'C0', 'accel': 'C2'}
for k, pts in e.items():
    pts.sort(); x = [p[0] for p in pts]
    a.plot(x, [p[1] for p in pts], color=cols[k], label=f'{k} (simulated)')
    a.plot(x, [p[2] for p in pts], color=cols[k], ls='--', alpha=0.7, label=f'{k} (formula)')
for (lo, hi, y, txt, c) in [(150, 330, 5.5, 'real braking 5-6 g', 'C3'), (230, 300, 4.5, 'real fast corners 4-5 g', 'C0')]:
    a.fill_between([lo, hi], y - 0.5, y + 0.5, color=c, alpha=0.08); a.text(lo, y + 0.55, txt, color=c, fontsize=8)
a.set(title='Performance envelope', xlabel='speed (km/h)', ylabel='g'); a.legend(fontsize=7, ncol=2)

# 3 handling
a = ax[1, 0]; h = collections.defaultdict(list)
for r in rows('handling.csv'):
    if r['test'] == 'ramp': h[int(r['kmh'])].append((float(r['x']), float(r['y'])))
for k, pts in sorted(h.items()): a.plot(*zip(*pts), '.', ms=3, label=f'{k} km/h')
a.axhline(0, color='k', lw=0.8)
a.set(title='Balance: steering beyond a neutral car (+ = understeer)', xlabel='lateral g', ylabel='extra front wheel angle (deg)'); a.legend()
a = ax[1, 1]; st = collections.defaultdict(list)
for r in rows('handling.csv'):
    if r['test'] == 'step': st[int(r['kmh'])].append((float(r['x']), float(r['y'])))
for k, pts in sorted(st.items()): a.plot(*zip(*pts), label=f'{k} km/h')
a.axvline(0, color='k', lw=0.8)
a.set(title='Step steer: yaw rate response', xlabel='time after steering (s)', ylabel='yaw rate (deg/s)'); a.legend()

# 4 lap
a = ax[2, 0]; L = rows('lap.csv')
s = [float(r['s']) for r in L]
a.plot(s, [float(r['kmh']) for r in L], label='AI driver', lw=1)
a.plot(s, [float(r['ideal_kmh']) for r in L], label='ideal speed profile', lw=1, alpha=0.7)
a.set(title='Spa lap: speed trace', xlabel='distance (m)', ylabel='km/h'); a.legend()

# 5 stint
a = ax[2, 1]; S = rows('stint.csv')
lap = [int(r['lap']) for r in S]
a.plot(lap, [float(r['time']) for r in S], 'k.-', label='lap time (s)')
a.set(xlabel='lap', ylabel='lap time (s)', title='Spa stint on Softs: lap time, tyre temperature, wear')
b = a.twinx()
b.plot(lap, [float(r['tyre_f']) for r in S], 'C0-', label='front °C'); b.plot(lap, [float(r['tyre_r']) for r in S], 'C3-', label='rear °C')
b.plot(lap, [float(r['wear_r']) * 100 for r in S], 'C3:', label='rear wear %'); b.plot(lap, [float(r['wear_f']) * 100 for r in S], 'C0:', label='front wear %')
b.set_ylabel('°C  /  wear %'); a.legend(loc='upper left'); b.legend(loc='center left', fontsize=7)
# 6 Monaco corners
a = ax[3, 0]; C = rows('corners.csv'); lap = [r for r in C if r['corner'] == 'LAP'][0]; C = [r for r in C if r['corner'] != 'LAP']
import numpy as np
x = np.arange(len(C)); wd = 0.27
a.bar(x - wd, [float(r['real']) for r in C], wd, label='real', color='k')
a.bar(x, [float(r['raw']) for r in C], wd, label=f"coarse outline (lap {float(lap['raw']):.1f} s)", color='C3')
a.bar(x + wd, [float(r['fitted']) for r in C], wd, label=f"fitted corners (lap {float(lap['fitted']):.1f} s)", color='C0')
a.set_xticks(x); a.set_xticklabels([r['corner'] for r in C], rotation=45, ha='right')
a.set(title=f"Monaco minimum corner speeds (real pole {float(lap['real']):.2f} s)", ylabel='km/h'); a.legend(fontsize=8)
# 7 formulas used
a = ax[3, 1]; a.axis('off')
a.text(0, 1, "\n".join([
 "Formulas behind the model",
 "",
 "Tyre side force:  Fy = D·sin(C·atan(Bα − E(Bα − atan Bα)))   (Pacejka)",
 "Peak force:  D = μ·Fz0·(Fz/Fz0)^0.8   (load sensitivity)",
 "Downforce / drag:  F = ½ρv²·ClA ,  ½ρv²·CdA   (ρ = 1.225 kg/m³)",
 "Ride-height aero:  ClA·(1 + 0.25·(h0 − h)/h0), stall below 8 mm",
 "Load transfer:  ΔFz = m·a·h_cg / wheelbase (pitch), / track (roll)",
 "Grip limit (envelope):  a = μ·Σ Fz^0.8 / m  (+ drag when braking)",
 "Corner speed:  v = √(a_lat·R)",
 "Power limit:  F = P·η / v ;  MGU-K ramp: P = 1800 − 5v kW above 290 km/h",
 "Gravity on slopes:  F = −m·g·grade",
 "Tyre heat:  C·dT/dt = k·P_slide − (h0 + h1·v)(T − T_track)",
 "Wear:  dw/dt ∝ P_slide / life ;  grip × (1 − 0.05w − 0.12w⁴)",
 "Understeer: δ − L/R vs a_lat (steady-state cornering)",
]), va='top', family='monospace', fontsize=8.5)
plt.tight_layout(); plt.savefig(out, dpi=72)
