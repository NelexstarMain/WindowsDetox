#!/usr/bin/env python3
"""Preview and validate a WindowsDetox plugin icon (SVG path data from plugin.json "icon").

Usage:
  python3 preview_icon.py plugins/GoalsTasks/plugin.json            # reads the "icon" field
  python3 preview_icon.py "M10 10 H90 V90 H10 Z"                    # raw path data
  python3 preview_icon.py <path-or-json> --out DIR                  # default DIR: ./icon-preview
  python3 preview_icon.py <path-or-json> --compare 'plugins/*/plugin.json'   # default compare glob shown

Stdlib only. Writes icon.svg, icon-18.png, icon-24.png, icon-96.png and icon-sheet.png (18/24/96 px, light and dark,
plus the 18 px icon enlarged 8x). Mirrors the host: WPF Geometry.Parse (even-odd fill, no F command), Stretch.Uniform
(the geometry's bounding box is scaled to the icon square, so the 0..100 box is only a drawing convention).
Exit code 1 when the path data would be rejected by UiTreeValidator.IsValidPathData.
"""
import argparse, glob, json, math, os, re, struct, sys, zlib

MAX_LEN = 4096
ALLOWED = set("0123456789 ,.-+eE" + "MmLlHhVvCcSsQqTtAaZz")


def validate(d):
    """Same rule as UiTreeValidator.IsValidPathData (+ a syntax check the C# validator does not do)."""
    errs = []
    if not d or not d.strip():
        errs.append("empty path data")
    if len(d) > MAX_LEN:
        errs.append(f"{len(d)} characters; the limit is {MAX_LEN}")
    bad = sorted({c for c in d if c not in ALLOWED})
    if bad:
        errs.append("characters not allowed: " + " ".join(repr(c) for c in bad))
    return errs


NUM = re.compile(r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
PARAMS = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "T": 2, "A": 7, "Z": 0}


def tokens(d):
    pos = 0
    while pos < len(d):
        c = d[pos]
        if c in " ,":
            pos += 1
        elif c.isalpha() and c not in "eE":
            yield c
            pos += 1
        else:
            m = NUM.match(d, pos)
            if not m:
                raise ValueError(f"cannot parse number at offset {pos}: {d[pos:pos + 12]!r}")
            yield float(m.group())
            pos = m.end()


def arc_points(x1, y1, rx, ry, phi, fa, fs, x2, y2, steps=48):
    if (x1, y1) == (x2, y2):
        return []
    if rx == 0 or ry == 0:
        return [(x2, y2)]
    rx, ry = abs(rx), abs(ry)
    cp, sp = math.cos(math.radians(phi)), math.sin(math.radians(phi))
    dx, dy = (x1 - x2) / 2, (y1 - y2) / 2
    x1p, y1p = cp * dx + sp * dy, -sp * dx + cp * dy
    lam = x1p ** 2 / rx ** 2 + y1p ** 2 / ry ** 2
    if lam > 1:
        rx, ry = rx * math.sqrt(lam), ry * math.sqrt(lam)
    num = rx ** 2 * ry ** 2 - rx ** 2 * y1p ** 2 - ry ** 2 * x1p ** 2
    den = rx ** 2 * y1p ** 2 + ry ** 2 * x1p ** 2
    co = math.sqrt(max(0, num / den)) * (-1 if fa == fs else 1)
    cxp, cyp = co * rx * y1p / ry, -co * ry * x1p / rx
    cx, cy = cp * cxp - sp * cyp + (x1 + x2) / 2, sp * cxp + cp * cyp + (y1 + y2) / 2

    def ang(ux, uy, vx, vy):
        a = math.atan2(ux * vy - uy * vx, ux * vx + uy * vy)
        return a

    t1 = ang(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry)
    dt = ang((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)
    if not fs and dt > 0:
        dt -= 2 * math.pi
    elif fs and dt < 0:
        dt += 2 * math.pi
    pts = []
    for i in range(1, steps + 1):
        t = t1 + dt * i / steps
        pts.append((cp * rx * math.cos(t) - sp * ry * math.sin(t) + cx, sp * rx * math.cos(t) + cp * ry * math.sin(t) + cy))
    return pts


def flatten(d):
    """Returns a list of closed polygons (every subpath is implicitly closed for filling)."""
    toks = list(tokens(d))
    polys, cur = [], []
    x = y = sx = sy = 0.0
    lastc = None  # last control point for S/T
    i, cmd = 0, None

    def need(n):
        nonlocal i
        if i + n > len(toks) or any(isinstance(t, str) for t in toks[i:i + n]):
            raise ValueError(f"command {cmd} needs {n} numbers")
        v = toks[i:i + n]
        i += n
        return v

    def bez(p0, p1, p2, p3, n=24):
        return [tuple((1 - t) ** 3 * a + 3 * (1 - t) ** 2 * t * b + 3 * (1 - t) * t ** 2 * c + t ** 3 * e
                      for a, b, c, e in zip(p0, p1, p2, p3)) for t in (k / n for k in range(1, n + 1))]

    while i < len(toks):
        if isinstance(toks[i], str):
            cmd = toks[i]
            i += 1
        elif cmd is None:
            raise ValueError("path must start with a command")
        elif cmd in "Zz":
            raise ValueError("numbers after Z")
        up, rel = cmd.upper(), cmd.islower()
        if up == "Z":
            if cur:
                polys.append(cur)
            cur, x, y, lastc = [], sx, sy, None
            continue
        a = need(PARAMS[up])
        if up == "M":
            if cur:
                polys.append(cur)
            x, y = (x + a[0], y + a[1]) if rel else (a[0], a[1])
            sx, sy, cur, lastc = x, y, [(x, y)], None
            cmd = "l" if rel else "L"
            continue
        if not cur:
            cur = [(x, y)]
        if up == "L":
            x, y = (x + a[0], y + a[1]) if rel else (a[0], a[1]); cur.append((x, y)); lastc = None
        elif up == "H":
            x = x + a[0] if rel else a[0]; cur.append((x, y)); lastc = None
        elif up == "V":
            y = y + a[0] if rel else a[0]; cur.append((x, y)); lastc = None
        elif up in "CS":
            if up == "C":
                p1, p2, p3 = (a[0], a[1]), (a[2], a[3]), (a[4], a[5])
            else:
                p1 = (2 * x - lastc[0], 2 * y - lastc[1]) if lastc and lastc[2] == "C" else (x, y)
                p2, p3 = (a[0], a[1]), (a[2], a[3])
            if rel:
                ox, oy = x, y
                p2, p3 = (p2[0] + ox, p2[1] + oy), (p3[0] + ox, p3[1] + oy)
                if up == "C":
                    p1 = (p1[0] + ox, p1[1] + oy)
            cur += bez((x, y), p1, p2, p3)
            lastc = (p2[0], p2[1], "C")
            x, y = p3
        elif up in "QT":
            if up == "Q":
                p1, p2 = (a[0], a[1]), (a[2], a[3])
            else:
                p1 = (2 * x - lastc[0], 2 * y - lastc[1]) if lastc and lastc[2] == "Q" else (x, y)
                p2 = (a[0], a[1])
            if rel:
                p2 = (p2[0] + x, p2[1] + y)
                if up == "Q":
                    p1 = (p1[0] + x, p1[1] + y)
            cur += [((1 - t) ** 2 * x + 2 * (1 - t) * t * p1[0] + t * t * p2[0], (1 - t) ** 2 * y + 2 * (1 - t) * t * p1[1] + t * t * p2[1])
                    for t in (k / 24 for k in range(1, 25))]
            lastc = (p1[0], p1[1], "Q")
            x, y = p2
        elif up == "A":
            ex, ey = (x + a[5], y + a[6]) if rel else (a[5], a[6])
            cur += arc_points(x, y, a[0], a[1], a[2], a[3] != 0, a[4] != 0, ex, ey) or [(ex, ey)]
            x, y, lastc = ex, ey, None
    if cur:
        polys.append(cur)
    return polys


def bbox(polys):
    xs = [p[0] for poly in polys for p in poly]
    ys = [p[1] for poly in polys for p in poly]
    return min(xs), min(ys), max(xs), max(ys)


def rasterize(polys, size, ss=4):
    """Even-odd coverage 0..255, bounding box scaled uniformly into size x size (WPF Stretch.Uniform)."""
    x0, y0, x1, y1 = bbox(polys)
    w, h = max(x1 - x0, 1e-6), max(y1 - y0, 1e-6)
    k = size / max(w, h)
    ox, oy = (size - w * k) / 2 - x0 * k, (size - h * k) / 2 - y0 * k
    edges = []
    for poly in polys:
        pts = [(px * k + ox, py * k + oy) for px, py in poly]
        for j in range(len(pts)):
            a, b = pts[j], pts[(j + 1) % len(pts)]
            if a[1] != b[1]:
                edges.append((a, b))
    n = size * ss
    cover = [[0] * size for _ in range(size)]
    for sy in range(n):
        yy = (sy + 0.5) / ss
        xs = sorted(a[0] + (yy - a[1]) * (b[0] - a[0]) / (b[1] - a[1]) for a, b in edges if (a[1] <= yy < b[1]) or (b[1] <= yy < a[1]))
        for p in range(0, len(xs) - 1, 2):
            sa, sb = max(0, round(xs[p] * ss)), min(n, round(xs[p + 1] * ss))
            for sx in range(sa, sb):
                cover[sy // ss][sx // ss] += 1
    return [[min(255, round(c * 255 / (ss * ss))) for c in row] for row in cover]


def png(path, rows):
    h, w = len(rows), len(rows[0])
    raw = b"".join(b"\x00" + b"".join(bytes(px) for px in row) for row in rows)
    def chunk(t, data):
        c = struct.pack(">I", len(data)) + t + data
        return c + struct.pack(">I", zlib.crc32(t + data) & 0xFFFFFFFF)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def tint(cov, fg, bg):
    return [[tuple(round(bg[i] + (fg[i] - bg[i]) * c / 255) for i in range(3)) for c in row] for row in cov]


def paste(canvas, img, x, y):
    for r, row in enumerate(img):
        canvas[y + r][x:x + len(row)] = row


def load(arg):
    if arg.lower().endswith(".json") and os.path.isfile(arg):
        with open(arg, encoding="utf-8") as f:
            return json.load(f)["icon"]
    return arg


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("icon", help="path data, or a plugin.json file")
    ap.add_argument("--out", default="icon-preview")
    ap.add_argument("--compare", default="plugins/*/plugin.json", help="glob of plugin.json files to compare against")
    args = ap.parse_args()
    d = load(args.icon).strip()
    errs = validate(d)
    if errs:
        print("INVALID (the host would reject this icon):")
        for e in errs:
            print("  - " + e)
        return 1
    try:
        polys = flatten(d)
        if not polys:
            raise ValueError("no drawable geometry")
        box = bbox(polys)
    except (ValueError, ZeroDivisionError) as ex:
        print(f"INVALID syntax: {ex}")
        return 1
    print(f"valid: {len(d)} of {MAX_LEN} characters, {len(polys)} subpath(s)")
    w, h = box[2] - box[0], box[3] - box[1]
    print(f"bounding box ({box[0]:.1f},{box[1]:.1f})-({box[2]:.1f},{box[3]:.1f}) = {w:.1f} x {h:.1f}; "
          f"center ({(box[0] + box[2]) / 2:.1f},{(box[1] + box[3]) / 2:.1f}); the host scales it uniformly to the icon square")
    if abs(w - h) > 0.25 * max(w, h):
        print("note: very non-square; it will look smaller than square icons (height or width is the limit).")
    os.makedirs(args.out, exist_ok=True)
    pad = 4
    with open(os.path.join(args.out, "icon.svg"), "w", encoding="utf-8") as f:
        f.write(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{box[0]-pad:.2f} {box[1]-pad:.2f} {w+2*pad:.2f} {h+2*pad:.2f}" '
                f'width="96" height="96"><path fill="#222" fill-rule="evenodd" d="{d}"/></svg>\n')
    covs = {s: rasterize(polys, s) for s in (18, 24, 96)}
    for s, cov in covs.items():
        png(os.path.join(args.out, f"icon-{s}.png"), tint(cov, (0, 0, 0), (255, 255, 255)))
    # contact sheet: light panel, dark panel, dark panel with the 18 px icon enlarged 8x (nearest neighbour)
    z, panel = 8, 12 + 18 + 12 + 24 + 12 + 96 + 12
    sheet_w, sheet_h = panel * 2 + 18 * z + 24, 18 * z + 24
    light, dark = ((20, 20, 20), (255, 255, 255)), ((235, 235, 235), (24, 24, 24))
    canvas = [[(255, 255, 255)] * panel + [(24, 24, 24)] * (sheet_w - panel) for _ in range(sheet_h)]
    for half, (fg, bg) in enumerate((light, dark)):
        x = half * panel + 12
        for s in (18, 24, 96):
            paste(canvas, tint(covs[s], fg, bg), x, 12 + (96 - s) // 2)
            x += s + 12
    zoom = [[c for c in row for _ in range(z)] for row in covs[18] for _ in range(z)]
    paste(canvas, tint(zoom, *dark), panel * 2 + 12, 12)
    png(os.path.join(args.out, "icon-sheet.png"), canvas)
    print(f"wrote {args.out}/icon.svg, icon-18.png, icon-24.png, icon-96.png, icon-sheet.png")

    # uniqueness against other plugins
    me = os.path.abspath(args.icon) if args.icon.lower().endswith(".json") else None
    for other in sorted(glob.glob(args.compare)):
        if me and os.path.abspath(other) == me:
            continue
        try:
            od = load(other).strip()
            oc = rasterize(flatten(od), 32)
        except (KeyError, ValueError, OSError, json.JSONDecodeError):
            continue
        mine = rasterize(polys, 32)
        inter = sum(1 for r in range(32) for c in range(32) if mine[r][c] >= 128 and oc[r][c] >= 128)
        union = sum(1 for r in range(32) for c in range(32) if mine[r][c] >= 128 or oc[r][c] >= 128)
        iou = inter / union if union else 0
        flag = "  <-- IDENTICAL" if od == d else ("  <-- too similar, redraw" if iou > 0.75 else "")
        print(f"vs {other}: overlap {iou:.0%}{flag}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
