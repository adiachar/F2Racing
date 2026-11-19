#!/usr/bin/env python3
"""shoot.py - automatic in-game screenshots of the F2Racing WebGL build (no phone, no Unity editor).

Downloads the newest WebGL build (GitHub release "latest" of adiachar/F2Racing) or uses a given folder / zip,
serves it on localhost with the right MIME types and Content-Encoding headers, opens it in headless Chrome
with the screenshot parameters (Assets/Scripts/Game/DebugShots.cs), saves one PNG per READY frame and a
contact sheet. See README.md in this folder.

  python3 Tools/Shots/shoot.py                                   # Monza, 6 rivals, High, chase cam, 6 spots
  python3 Tools/Shots/shoot.py --cam chase,cockpit,tv --s 300,850 --quality Medium --out /tmp/shots
  python3 Tools/Shots/shoot.py --build ~/F2Racing-WebGL.zip --menu   # just the start menu (any build)

Needs: python3, playwright (python), Pillow (contact sheet; falls back to ffmpeg), Chrome, and `gh` (or network
access to github.com) for the download.
"""
import argparse
import datetime
import functools
import http.server
import json
import os
import re
import shutil
import socketserver
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
import zipfile

REPO = "adiachar/F2Racing"
ASSET = "F2Racing-WebGL.zip"
DEFAULT_SPOTS = "300,850,1450,2500,3920,5150"  # Monza: straight, Rettifilo, Curva Grande, Lesmo 1, Ascari, Parabolica
CHROME = "/usr/bin/google-chrome"
# --preset: spots for features no default run puts in frame (gap list item 26). Sun spots come from the track data
# (Physics Track tangents, 150 m ahead, nearly straight) against the sun of each time of day in Sky.cs / WorldBuilder.cs:
# afternoon Euler(48,-35) (high: glare only, the disc is above the chase frame), evening Euler(13,70) (low: disc,
# flare and glare in frame), morning Euler(24,-110). A preset fills in --track/--tod/--cam/--s not given on the
# command line and adds its --param values; anything given on the command line wins.
PRESETS = {
    # Monza afternoon, the runs that face the sun most (Lesmo 1 exit 2620, Ascari-Parabolica 4411 / 4881)
    "sun": {"track": "Monza", "tod": "afternoon", "cam": "chase,cockpit", "s": "2620,4411,4881"},
    # Monza evening: the Lesmo 2 - Serraglio run heads straight into the low sun (2910, 3210)
    "sun-evening": {"track": "Monza", "tod": "evening", "cam": "chase,cockpit", "s": "2910,3210"},
    # plank sparks: 320 km/h on the main straight, chase and far (the far cam sees the floor from behind)
    "sparks": {"track": "Monza", "cam": "chase,far", "s": "300,320,340", "param": ["kmh=320"]},
    # LOD sweep: chase every 15 m down the main straight; flip through the frames for trees / stands / boards popping
    "lod": {"track": "Monza", "cam": "chase", "s": ",".join(str(v) for v in range(150, 466, 15))},
}
CHROME_ARGS = ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist",
               "--autoplay-policy=no-user-gesture-required", "--mute-audio", "--disable-background-timer-throttling",
               "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows"]

T0 = time.time()
LOG = None


def log(msg):
    line = f"[{time.time() - T0:7.1f}s] {msg}"
    print(line, flush=True)
    if LOG:
        LOG.write(line + "\n")
        LOG.flush()


# ------------------------------------------------------------------------------------------------ build download
def gh_json(path):
    out = subprocess.run(["gh", "api", path], check=True, capture_output=True, text=True).stdout
    return json.loads(out)


def download_latest(cache):
    """Newest F2Racing-WebGL.zip of the "latest" release into cache/<updated_at>/; returns the zip path."""
    os.makedirs(cache, exist_ok=True)
    asset = None
    if shutil.which("gh"):
        try:
            rel = gh_json(f"repos/{REPO}/releases/tags/latest")
            asset = next((a for a in rel.get("assets", []) if a["name"] == ASSET), None)
        except Exception as e:  # noqa: BLE001
            log(f"gh api failed ({e}); trying the public download URL")
    stamp = re.sub(r"[^0-9A-Za-z]", "", asset["updated_at"]) if asset else "unknown"
    dest = os.path.join(cache, stamp, ASSET)
    if asset and os.path.exists(dest) and os.path.getsize(dest) == asset["size"]:
        log(f"using cached build {dest} ({asset['updated_at']})")
        return dest
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    tmp = dest + ".part"
    if asset:
        log(f"downloading {ASSET} ({asset['size'] / 1e6:.1f} MB, built {asset['updated_at']}) with gh api")
        with open(tmp, "wb") as f:
            subprocess.run(["gh", "api", "-H", "Accept: application/octet-stream",
                            f"repos/{REPO}/releases/assets/{asset['id']}"], check=True, stdout=f)
    else:
        url = f"https://github.com/{REPO}/releases/download/latest/{ASSET}"
        log(f"downloading {url}")
        with urllib.request.urlopen(url, timeout=600) as r, open(tmp, "wb") as f:
            shutil.copyfileobj(r, f)
    os.replace(tmp, dest)
    return dest


def unpack(zip_path):
    """Extract next to the zip (once); returns the folder that holds index.html."""
    root = os.path.splitext(zip_path)[0] + "_unpacked"
    if not os.path.isdir(root) or os.path.getmtime(root) < os.path.getmtime(zip_path):
        shutil.rmtree(root, ignore_errors=True)
        with zipfile.ZipFile(zip_path) as z:
            for n in z.namelist():  # no absolute paths / .. in an archive we extract
                if n.startswith("/") or ".." in n.split("/"):
                    raise SystemExit(f"refusing odd path in zip: {n}")
            z.extractall(root)
    return find_index(root)


def find_index(folder):
    for dirpath, _dirs, files in os.walk(folder):
        if "index.html" in files and os.path.isdir(os.path.join(dirpath, "Build")):
            return dirpath
    raise SystemExit(f"no index.html with a Build/ folder under {folder}")


# ------------------------------------------------------------------------------------------------ local server
TYPES = {".wasm": "application/wasm", ".js": "application/javascript", ".data": "application/octet-stream",
         ".html": "text/html; charset=utf-8", ".css": "text/css", ".png": "image/png", ".ico": "image/x-icon",
         ".json": "application/json", ".symbols.json": "application/json", ".mem": "application/octet-stream",
         ".bundle": "application/octet-stream", ".txt": "text/plain"}
UNITY_BR = b"UnityWeb Compressed Content (brotli)"


def sniff_encoding(path, name):
    """'gzip', 'br' or None for a Build/ file: by extension (.gz, .br), or for .unityweb by its header."""
    if name.endswith(".gz"):
        return "gzip"
    if name.endswith(".br"):
        return "br"
    if name.endswith(".unityweb"):
        with open(path, "rb") as f:
            head = f.read(64)
        if head[:2] == b"\x1f\x8b":
            return "gzip"
        if UNITY_BR in head:
            return "br"
    return None


def content_type(name):
    base = name
    for suf in (".gz", ".br", ".unityweb"):
        if base.endswith(suf):
            base = base[: -len(suf)]
    for ext in sorted(TYPES, key=len, reverse=True):
        if base.endswith(ext):
            return TYPES[ext]
    return "application/octet-stream"


class Handler(http.server.SimpleHTTPRequestHandler):
    raw = False  # True: never send Content-Encoding (tests the loader's own decompression fallback)

    def log_message(self, fmt, *args):  # into the log file, not the terminal
        if LOG:
            LOG.write(f"[{time.time() - T0:7.1f}s] http {self.address_string()} {fmt % args}\n")

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def do_GET(self):  # noqa: N802
        path = self.translate_path(urllib.parse.urlsplit(self.path).path)
        if not os.path.isfile(path):
            return super().do_GET()
        name = os.path.basename(path)
        enc = sniff_encoding(path, name)
        with open(path, "rb") as f:
            body = f.read()
        if enc == "br" and not self.raw and "br" not in self.headers.get("Accept-Encoding", ""):
            enc = None  # the browser can't take br here (plain http): send the raw file, the loader's fallback decodes it
        if self.raw:
            enc = None
        self.send_response(200)
        self.send_header("Content-Type", content_type(name))
        if enc:
            self.send_header("Content-Encoding", enc)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def serve(folder, port, raw):
    Handler.raw = raw
    httpd = Server(("127.0.0.1", port), functools.partial(Handler, directory=folder))
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd


def check_build(folder):
    b = os.path.join(folder, "Build")
    for n in sorted(os.listdir(b)):
        p = os.path.join(b, n)
        log(f"  Build/{n:40s} {os.path.getsize(p) / 1e6:7.2f} MB  type {content_type(n):24s} encoding {sniff_encoding(p, n)}")
    html = open(os.path.join(folder, "index.html"), encoding="utf-8", errors="replace").read()
    if "unity-canvas" not in html:
        log("WARNING: index.html has no #unity-canvas (custom template?): canvas sizing may not apply")


# ------------------------------------------------------------------------------------------------ browser
INIT_JS = """
(() => {
  const W = %d, H = %d;
  const fit = () => {
    const c = document.querySelector('#unity-canvas');
    if (c) { c.style.width = W + 'px'; c.style.height = H + 'px'; }
  };
  const css = '#unity-container{left:0!important;top:0!important;transform:none!important}'
            + '#unity-footer{display:none!important}#unity-warning{display:none!important}'
            + 'html,body{margin:0;overflow:hidden;background:#000}';
  document.addEventListener('DOMContentLoaded', () => {
    const s = document.createElement('style'); s.textContent = css; document.head.appendChild(s);
    fit(); setTimeout(fit, 0); setTimeout(fit, 1000);
  });
  window.__shotAck = -1;
})();
"""


def contact_sheet(files, labels, out, cols=3, thumb_w=640):
    if not files:
        return
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        log("Pillow missing: contact sheet with ffmpeg (no labels)")
        cols = min(cols, len(files))
        rows = (len(files) + cols - 1) // cols
        args = ["ffmpeg", "-y", "-loglevel", "error"]
        for f in files:
            args += ["-i", f]
        n = len(files)
        chain = "".join(f"[{i}:v]" for i in range(n)) + f"concat=n={n}:v=1:a=0,scale={thumb_w}:-1,tile={cols}x{rows}"
        subprocess.run(args + ["-filter_complex", chain, "-frames:v", "1", out], check=False)
        return
    ims = [Image.open(f).convert("RGB") for f in files]
    th = int(thumb_w * ims[0].height / ims[0].width)
    cols = min(cols, len(ims))
    rows = (len(ims) + cols - 1) // cols
    pad, bar = 8, 26
    sheet = Image.new("RGB", (cols * (thumb_w + pad) + pad, rows * (th + bar + pad) + pad), (18, 18, 20))
    try:
        font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 15)
    except OSError:
        font = ImageFont.load_default()
    d = ImageDraw.Draw(sheet)
    for k, (im, lab) in enumerate(zip(ims, labels)):
        x, y = pad + (k % cols) * (thumb_w + pad), pad + (k // cols) * (th + bar + pad)
        sheet.paste(im.resize((thumb_w, th), Image.LANCZOS), (x, y + bar))
        d.text((x + 4, y + 4), lab, fill=(235, 235, 235), font=font)
    sheet.save(out)
    log(f"contact sheet {out}")


def run(args):
    global LOG
    os.makedirs(args.out, exist_ok=True)
    LOG = open(os.path.join(args.out, "console.log"), "w", encoding="utf-8")
    log(f"output {os.path.abspath(args.out)}")

    # build
    if args.build:
        b = os.path.expanduser(args.build)
        folder = unpack(os.path.abspath(b)) if b.endswith(".zip") else find_index(os.path.abspath(b))
    else:
        folder = unpack(download_latest(os.path.expanduser(args.cache)))
    log(f"build folder {folder}")
    check_build(folder)
    httpd = serve(folder, args.port, args.raw)
    port = httpd.server_address[1]

    # URL
    params = {}
    if not args.menu:
        params = {"shot": "1", "ack": "1", "track": args.track, "cars": str(args.cars), "quality": args.quality,
                  "cam": args.cam, "s": args.s, "w": str(args.w), "h": str(args.h), "tod": args.tod,
                  "weather": args.weather, "settle": str(args.settle), "warm": str(args.warm)}
        for kv in args.param or []:
            k, _, v = kv.partition("=")
            params[k] = v
    url = f"http://127.0.0.1:{port}/index.html" + ("?" + urllib.parse.urlencode(params, safe=",|") if params else "")
    log(f"url {url}")

    from playwright.sync_api import sync_playwright
    shots, labels, meta = [], [], {"url": url, "build": folder, "started": datetime.datetime.now().isoformat(), "shots": []}
    errors = 0
    with sync_playwright() as p:
        launch = dict(headless=not args.headed, args=CHROME_ARGS)
        if os.path.exists(args.chrome):
            launch["executable_path"] = args.chrome
        browser = p.chromium.launch(**launch)
        ctx = browser.new_context(viewport={"width": args.w, "height": args.h}, device_scale_factor=1)
        page = ctx.new_page()
        page.add_init_script(INIT_JS % (args.w, args.h))

        seen = {}

        def on_console(m):
            nonlocal errors
            if m.type == "error":
                errors += 1
            LOG.write(f"[{time.time() - T0:7.1f}s] console.{m.type}: {m.text.rstrip()}\n")
            LOG.flush()
            text = m.text.strip()
            seen[text] = seen.get(text, 0) + 1
            if args.verbose or "[Shots]" in text or (m.type == "error" and seen[text] <= 2):
                print(f"  console.{m.type}: {text[:300]}" + ("  (repeats are only in console.log)" if seen[text] == 2 else ""), flush=True)

        def on_dialog(d):
            log(f"DIALOG ({d.type}): {d.message}")  # the template alert()s a failed load
            meta.setdefault("dialogs", []).append(d.message)
            d.dismiss()

        page.on("console", on_console)
        page.on("pageerror", lambda e: log(f"PAGE ERROR: {e}"))
        page.on("requestfailed", lambda r: log(f"REQUEST FAILED: {r.url} {r.failure}"))
        page.on("response", lambda r: r.status >= 400 and log(f"HTTP {r.status}: {r.url}"))
        page.on("dialog", on_dialog)
        page.on("crash", lambda *_: log("PAGE CRASHED"))

        page.goto(url, wait_until="load", timeout=120_000)
        log("page loaded, waiting for Unity to start (SwiftShader: this can take minutes)")

        def title():
            try:
                return page.title()
            except Exception:  # noqa: BLE001  (busy page / navigation)
                return ""

        def unity_started():
            try:
                return page.evaluate("() => { const b = document.querySelector('#unity-loading-bar');"
                                     " return !!b && getComputedStyle(b).display === 'none'; }")
            except Exception:  # noqa: BLE001
                return False

        def progress():
            try:
                return page.evaluate("() => (document.querySelector('#unity-progress-bar-full')||{style:{}}).style.width || ''")
            except Exception:  # noqa: BLE001
                return ""

        def shoot(path):
            page.screenshot(path=path, clip={"x": 0, "y": 0, "width": args.w, "height": args.h}, timeout=args.shot_timeout * 1000)

        # 1. the engine starts
        deadline, last = time.time() + args.load_timeout, 0
        while not unity_started():
            if meta.get("dialogs"):
                raise SystemExit("load failed: " + meta["dialogs"][-1])
            if time.time() > deadline:
                shoot(os.path.join(args.out, "timeout.png"))
                raise SystemExit(f"Unity did not start within {args.load_timeout} s (see console.log, timeout.png)")
            if time.time() - last > 15:
                last = time.time()
                log(f"  loading... progress {progress()} title '{title()}'")
            time.sleep(0.5)
        log(f"Unity started; title '{title()}'")

        if args.menu:
            time.sleep(args.menu_wait)  # the menu's entry animations
            path = os.path.join(args.out, "menu.png")
            shoot(path)
            shots.append(path); labels.append("start menu")
            log(f"saved {path}")
        else:
            # 2. shot mode: SHOT_BOOT comes right after the first frame; an older build never sends it
            boot_deadline = time.time() + args.boot_timeout
            while not title().startswith("SHOT_"):
                if time.time() > boot_deadline:
                    path = os.path.join(args.out, "menu.png")
                    shoot(path)
                    log(f"no SHOT_ title after {args.boot_timeout} s: this build has no screenshot mode "
                        f"(older than DebugShots.cs?). Saved what is on screen: {path}")
                    meta["error"] = "no shot mode"
                    json.dump(meta, open(os.path.join(args.out, "shots.json"), "w"), indent=1)
                    browser.close()
                    return 2
                time.sleep(0.5)
            # 3. one PNG per SHOT_READY i
            want, total, last_t, shot_deadline = 0, None, "", time.time() + args.load_timeout
            while True:
                t = title()
                if t != last_t:
                    log(f"title: {t}")
                    last_t = t
                if t.startswith("SHOT_ERROR"):
                    meta["error"] = t
                    log("game reported " + t)
                    break
                m = re.match(r"SHOT_READY (\d+) (\d+)(.*)", t)
                if m and int(m.group(1)) == want:
                    total = int(m.group(2))
                    info = dict(re.findall(r"(\w+)=(\S+)", m.group(3)))
                    name = f"shot_{want:02d}_s{info.get('s', 'x')}_{info.get('cam', 'cam')}.png"
                    path = os.path.join(args.out, name)
                    t1 = time.time()
                    shoot(path)
                    page.evaluate("i => { window.__shotAck = i; }", want)
                    log(f"saved {name} ({time.time() - t1:.1f} s)")
                    shots.append(path)
                    labels.append(f"#{want}  s={info.get('s', '?')} m  {info.get('cam', '')}")
                    meta["shots"].append({"i": want, "file": name, **info})
                    want += 1
                    shot_deadline = time.time() + args.shot_timeout
                    if want >= total:
                        break
                elif t.startswith("SHOT_DONE"):
                    break
                if time.time() > shot_deadline:
                    shoot(os.path.join(args.out, f"timeout_{want:02d}.png"))
                    meta["error"] = f"timed out waiting for SHOT_READY {want}"
                    log(meta["error"])
                    break
                time.sleep(0.25)
        browser.close()
    httpd.shutdown()

    meta["console_errors"] = errors
    json.dump(meta, open(os.path.join(args.out, "shots.json"), "w"), indent=1)
    if len(shots) > 1 or (shots and not args.menu):
        contact_sheet(shots, labels, os.path.join(args.out, "contact_sheet.png"), cols=args.cols)
    log(f"done: {len(shots)} image(s), {errors} console error(s); log {os.path.join(args.out, 'console.log')}")
    return 1 if meta.get("error") else 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--build", help="WebGL build folder or zip (default: download the latest release)")
    ap.add_argument("--cache", default="~/.cache/f2racing-shots", help="download cache")
    ap.add_argument("--out", default="shots_out", help="output folder (PNGs, contact_sheet.png, console.log, shots.json)")
    ap.add_argument("--track", default="Monza")
    ap.add_argument("--cars", type=int, default=6, help="rivals placed around the player (0-21)")
    ap.add_argument("--quality", default="High", choices=["Low", "Medium", "High", "Auto"])
    ap.add_argument("--cam", default="chase", help="comma list: chase, far, cockpit, halo, tv")
    ap.add_argument("--s", default=DEFAULT_SPOTS, help="comma list of lap distances (m)")
    ap.add_argument("--w", type=int, default=1280)
    ap.add_argument("--h", type=int, default=720)
    ap.add_argument("--tod", default="afternoon", choices=["morning", "afternoon", "evening", "night"])
    ap.add_argument("--weather", default="dry", choices=["dry", "damp", "wet"])
    ap.add_argument("--settle", type=float, default=2.0, help="seconds the game waits after each placement")
    ap.add_argument("--warm", type=float, default=5.0, help="extra seconds before the first shot")
    ap.add_argument("--param", action="append", help="extra URL parameter k=v (e.g. hud=1, class=F2, kmh=250)")
    ap.add_argument("--menu", action="store_true", help="no shot mode: screenshot the start menu (works with any build)")
    ap.add_argument("--menu-wait", type=float, default=8.0, help="seconds after start before the menu screenshot")
    ap.add_argument("--load-timeout", type=int, default=1200, help="seconds for Unity to start / the circuit to build")
    ap.add_argument("--boot-timeout", type=int, default=120, help="seconds after start to see SHOT_BOOT")
    ap.add_argument("--shot-timeout", type=int, default=600, help="seconds per shot (and per screenshot)")
    ap.add_argument("--cols", type=int, default=3, help="contact sheet columns")
    ap.add_argument("--port", type=int, default=0, help="local port (0 = any free)")
    ap.add_argument("--raw", action="store_true", help="never send Content-Encoding (test the loader's fallback)")
    ap.add_argument("--chrome", default=CHROME)
    ap.add_argument("--headed", action="store_true")
    ap.add_argument("--verbose", action="store_true", help="print every console line")
    ap.add_argument("--preset", choices=sorted(PRESETS), help="ready-made spot set: sun, sun-evening, sparks, lod (see PRESETS)")
    args = ap.parse_args()
    if args.preset:
        given = {a.split("=", 1)[0][2:] for a in sys.argv[1:] if a.startswith("--")}
        for k, v in PRESETS[args.preset].items():
            if k == "param":
                args.param = list(v) + (args.param or [])
            elif k not in given:
                setattr(args, k, v)
    sys.exit(run(args))


if __name__ == "__main__":
    main()
