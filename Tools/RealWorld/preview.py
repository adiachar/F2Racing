"""preview.py - top-down PNG of a built circuit world: areas, lines, buildings, trees, the game centreline (red)
and the real OSM raceway after the fit (blue, dashed). Left: whole file; right: a 700 m close-up at the start."""
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
from matplotlib.collections import LineCollection, PolyCollection  # noqa: E402

AREA_COL = {0: "#9ccc65", 1: "#2e7d32", 2: "#d4c77a", 3: "#d7ccc8", 4: "#bcaaa4", 5: "#4fa3e0", 6: "#f3e0a0",
            7: "#9e9e9e", 8: "#7cb342", 9: "#a1887f", 10: "#80cbc4", 11: "#616161"}
LINE_COL = {0: "#e65100", 1: "#f9a825", 2: "#fff176", 3: "#ffffff", 4: "#8d6e63", 5: "#424242"}


def _draw(ax, c, lw_scale=1.0):
    t = c.terr
    ext = (t["x0"], t["x0"] + (t["nx"] - 1) * t["cell"], t["z0"], t["z0"] + (t["nz"] - 1) * t["cell"])
    ax.imshow(t["h"], origin="lower", extent=ext, cmap="gray", alpha=0.35, zorder=0)
    ax.add_collection(PolyCollection([r for _, r in c.areas], facecolors=[AREA_COL[k] for k, _ in c.areas],
                                     edgecolors="none", alpha=0.85, zorder=1))
    if c.lines:
        ax.add_collection(LineCollection([p for _, _, p in c.lines], colors=[LINE_COL[k] for k, _, _ in c.lines],
                                         linewidths=[max(0.3, w / 8 * lw_scale) for _, w, _ in c.lines], zorder=2))
    if c.trees:
        tr = np.array(c.trees)
        ax.scatter(tr[:, 0], tr[:, 1], s=0.6 * lw_scale, c="#1b5e20", lw=0, zorder=3)
    if c.bldg:
        hs = np.array([b[0] for b in c.bldg])
        cols = plt.cm.inferno(np.clip(hs / 40.0, 0, 1) * 0.75 + 0.1)
        ax.add_collection(PolyCollection([b[5] for b in c.bldg], facecolors=cols, edgecolors="k",
                                         linewidths=0.15 * lw_scale, zorder=4))
    P = c.trk[:, :2]
    ax.plot(np.r_[P[:, 0], P[:1, 0]], np.r_[P[:, 1], P[:1, 1]], "-", color="red", lw=1.4 * lw_scale, zorder=6)
    ax.plot(c.outline[:, 0], c.outline[:, 1], "--", color="#1565c0", lw=0.8 * lw_scale, zorder=7)
    ax.set_xlim(ext[0], ext[1])
    ax.set_ylim(ext[2], ext[3])
    ax.set_aspect("equal")


def render(c, path):
    fig, axs = plt.subplots(1, 2, figsize=(22, 11), gridspec_kw=dict(width_ratios=[1.15, 1]))
    _draw(axs[0], c, 0.6)
    axs[0].set_title("%s: %d buildings, %d areas, %d lines, %d trees, %.2f MB" % (
        c.name, len(c.bldg), len(c.areas), len(c.lines), len(c.trees), c.size / 1e6))
    _draw(axs[1], c, 1.6)
    # close-up round the start line
    P = c.trk[:, :2]
    cx, cz = P[0]
    axs[1].set_xlim(cx - 350, cx + 350)
    axs[1].set_ylim(cz - 350, cz + 350)
    axs[1].set_title("start/finish close-up; red = game centreline, blue dashed = OSM raceway (fit RMS %.2f m, scale %.4f)" % (
        c.fit["rms"], c.fit["scale"]))
    for ax in axs:
        ax.set_xlabel("Unity X (m)")
        ax.set_ylabel("Unity Z (m)")
    fig.tight_layout()
    fig.savefig(path, dpi=80)
    plt.close(fig)
