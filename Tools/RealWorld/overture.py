"""overture.py - fetch only the parts of the Overture Maps GeoParquet release that cover a lon/lat box.

Anonymous HTTPS range reads against the public S3 bucket: list the files of a theme/type, read every file's
footer once (cached), use the row-group statistics of the bbox columns to pick the row groups that intersect the
box, download just those column chunks, filter the rows by bbox, and cache the result as a small local parquet.
"""
import hashlib
import io
import os
import re
import struct
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor

import pyarrow as pa
import pyarrow.compute as pc
import pyarrow.parquet as pq
import requests

BUCKET = "https://overturemaps-us-west-2.s3.amazonaws.com"
RELEASE = os.environ.get("OVERTURE_RELEASE", "2026-10-21.0")
CACHE = os.environ.get("RW_CACHE", "/home/claude/rw-cache")

_tls = threading.local()


def _sess():
    s = getattr(_tls, "s", None)
    if s is None:
        s = _tls.s = requests.Session()
    return s


def _get(url, rng=None, tries=6):
    for k in range(tries):
        try:
            h = {"Range": "bytes=%d-%d" % rng} if rng else {}
            r = _sess().get(url, headers=h, timeout=120)
            if r.status_code in (200, 206):
                return r.content
            raise IOError("HTTP %d %s" % (r.status_code, url))
        except Exception as e:  # noqa
            if k == tries - 1:
                raise
            time.sleep(1.5 * (k + 1))


def list_files(theme, typ):
    """[(key, size)] of the parquet files of one theme/type (cached)."""
    cp = os.path.join(CACHE, "listing", RELEASE, "%s_%s.txt" % (theme, typ))
    if os.path.exists(cp):
        out = []
        for line in open(cp):
            k, s = line.split()
            out.append((k, int(s)))
        return out
    prefix = "release/%s/theme=%s/type=%s/" % (RELEASE, theme, typ)
    out, token = [], None
    while True:
        url = BUCKET + "/?list-type=2&prefix=" + requests.utils.quote(prefix, safe="")
        if token:
            url += "&continuation-token=" + requests.utils.quote(token, safe="")
        xml = _get(url).decode()
        for k, s in re.findall(r"<Key>([^<]+)</Key>.*?<Size>(\d+)</Size>", xml):
            if k.endswith(".parquet"):
                out.append((k, int(s)))
        m = re.search(r"<NextContinuationToken>([^<]+)</NextContinuationToken>", xml)
        if not m:
            break
        token = m.group(1)
    os.makedirs(os.path.dirname(cp), exist_ok=True)
    with open(cp, "w") as f:
        for k, s in out:
            f.write("%s %d\n" % (k, s))
    return out


def _footer(key, size):
    """The file's tail bytes (footer + magic), cached on disk."""
    cp = os.path.join(CACHE, "footers", hashlib.md5(key.encode()).hexdigest() + ".bin")
    if os.path.exists(cp):
        return open(cp, "rb").read()
    url = BUCKET + "/" + key
    tail = _get(url, (max(0, size - 262144), size - 1))
    flen = struct.unpack("<I", tail[-8:-4])[0]
    if flen + 8 > len(tail):
        tail = _get(url, (size - flen - 8, size - 1))
    tail = tail[-(flen + 8):]
    os.makedirs(os.path.dirname(cp), exist_ok=True)
    with open(cp + ".tmp", "wb") as f:
        f.write(tail)
    os.replace(cp + ".tmp", cp)
    return tail


class RangeFile(io.RawIOBase):
    """Read-only, seekable file over HTTP range requests; the cached footer is served locally."""

    def __init__(self, key, size, tail):
        self.url, self.size, self.tail, self.pos = BUCKET + "/" + key, size, tail, 0
        self.tail0 = size - len(tail)
        self.nbytes = 0

    def readable(self):
        return True

    def seekable(self):
        return True

    def tell(self):
        return self.pos

    def seek(self, off, whence=0):
        self.pos = off if whence == 0 else self.pos + off if whence == 1 else self.size + off
        return self.pos

    def read(self, n=-1):
        if n < 0:
            n = self.size - self.pos
        n = min(n, self.size - self.pos)
        if n <= 0:
            return b""
        if self.pos >= self.tail0:
            b = self.tail[self.pos - self.tail0:self.pos - self.tail0 + n]
        else:
            b = _get(self.url, (self.pos, self.pos + n - 1))
            self.nbytes += len(b)
        self.pos += len(b)
        return b

    def readinto(self, buf):
        b = self.read(len(buf))
        buf[:len(b)] = b
        return len(b)


def _rg_boxes(md):
    """Per row group (xmin, ymin, xmax, ymax) from the bbox column statistics (None if missing)."""
    sch = md.schema
    idx = {}
    for i in range(sch.__len__()):
        p = sch.column(i).path
        if p in ("bbox.xmin", "bbox.xmax", "bbox.ymin", "bbox.ymax"):
            idx[p] = i
    out = []
    for r in range(md.num_row_groups):
        rg = md.row_group(r)
        try:
            xmin = rg.column(idx["bbox.xmin"]).statistics.min
            xmax = rg.column(idx["bbox.xmax"]).statistics.max
            ymin = rg.column(idx["bbox.ymin"]).statistics.min
            ymax = rg.column(idx["bbox.ymax"]).statistics.max
            out.append((xmin, ymin, xmax, ymax))
        except Exception:
            out.append(None)
    return out


_meta_lock = threading.Lock()


def file_index(theme, typ, workers=48):
    """[(key, size, tail, [rg boxes])] for every file of theme/type (footers fetched in parallel, cached)."""
    files = list_files(theme, typ)

    def one(ks):
        k, s = ks
        tail = _footer(k, s)
        md = pq.read_metadata(_TailOnly(s, tail))
        return (k, s, tail, _rg_boxes(md))

    with ThreadPoolExecutor(workers) as ex:
        return list(ex.map(one, files))


class _TailOnly(io.RawIOBase):
    def __init__(self, size, tail):
        self.size, self.tail, self.pos, self.t0 = size, tail, 0, size - len(tail)

    def readable(self):
        return True

    def seekable(self):
        return True

    def tell(self):
        return self.pos

    def seek(self, off, whence=0):
        self.pos = off if whence == 0 else self.pos + off if whence == 1 else self.size + off
        return self.pos

    def read(self, n=-1):
        if n < 0:
            n = self.size - self.pos
        b = self.tail[self.pos - self.t0:self.pos - self.t0 + n]
        self.pos += len(b)
        return b

    def readinto(self, buf):
        b = self.read(len(buf))
        buf[:len(b)] = b
        return len(b)


def _isect(a, b):
    return a is None or not (a[2] < b[0] or a[0] > b[2] or a[3] < b[1] or a[1] > b[3])


def fetch(theme, typ, box, name, columns=None, verbose=True):
    """Rows of theme/type whose bbox intersects box = (lon0, lat0, lon1, lat1); cached as <CACHE>/data/<name>/<typ>.parquet."""
    cp = os.path.join(CACHE, "data", RELEASE, name, "%s.parquet" % typ)
    if os.path.exists(cp):
        return pq.read_table(cp)
    t0 = time.time()
    idx = file_index(theme, typ)
    jobs = []
    for k, s, tail, boxes in idx:
        rgs = [i for i, b in enumerate(boxes) if _isect(b, box)]
        if rgs:
            jobs.append((k, s, tail, rgs))
    if verbose:
        print("  %s/%s: %d files, %d row groups to read" % (theme, typ, len(jobs), sum(len(j[3]) for j in jobs)), flush=True)

    def one_rg(job):
        k, s, tail, r = job
        f = RangeFile(k, s, tail)
        pf = pq.ParquetFile(f)
        cols = columns
        if cols is not None:
            names = pf.schema_arrow.names
            cols = [c for c in cols if c in names]
        t = pf.read_row_group(r, columns=cols)
        bb = t.column("bbox")
        xmin = pc.struct_field(bb, "xmin"); xmax = pc.struct_field(bb, "xmax")
        ymin = pc.struct_field(bb, "ymin"); ymax = pc.struct_field(bb, "ymax")
        m = pc.and_(pc.and_(pc.greater_equal(xmax, box[0]), pc.less_equal(xmin, box[2])),
                    pc.and_(pc.greater_equal(ymax, box[1]), pc.less_equal(ymin, box[3])))
        return t.filter(m), f.nbytes

    flat = [(k, s, tail, r) for k, s, tail, rgs in jobs for r in rgs]
    tables, nb = [], 0
    with ThreadPoolExecutor(16) as ex:
        for t, b in ex.map(one_rg, flat):
            nb += b
            if t.num_rows:
                tables.append(t)
    if tables:
        schema = tables[0].schema
        tables = [t.cast(schema) if t.schema != schema else t for t in tables]
        out = pa.concat_tables(tables, promote_options="permissive")
    else:
        out = None
    os.makedirs(os.path.dirname(cp), exist_ok=True)
    if out is None:
        return None
    pq.write_table(out, cp + ".tmp.parquet")
    os.replace(cp + ".tmp.parquet", cp)
    if verbose:
        print("  %s/%s: %d rows, %.1f MB downloaded, %.0f s" % (theme, typ, out.num_rows, nb / 1e6, time.time() - t0), flush=True)
    return out


if __name__ == "__main__":
    th, ty = sys.argv[1], sys.argv[2]
    idx = file_index(th, ty)
    print(len(idx), "files", sum(len(b) for _, _, _, b in idx), "row groups")
