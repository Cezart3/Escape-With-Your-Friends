"""Undergrowth and flowers, modelled from code (#246).

    blender -b --factory-startup -P tools/art/plants.py -- Assets/_Project/Art/Models/Plants

Same contract as palms.py, trees.py and rocks.py: one FBX per variant with `<name>` and `<name>_Far`,
one ramp texture (Textures/Leaves.png) on one material, `Leaves`, which ArtLibrary draws two-sided and
flutters in the wind. No folder: a preview row in the open Blender.

Everything is geometry, nothing is cut out of a card with alpha: a leaf is a strip along a drooping
arc, a leaflet or a petal is a diamond of two triangles. Normals are bent off a point low in the
middle of the plant, the way the tree canopies are, so a fern reads as one soft mound under the toon
terminator rather than a hundred flat cards that each catch the sun on their own.

A bush borrows the tree canopy outright (trees.hull and trees.leaves), drawn on this sheet.

The far mesh keeps each plant's silhouette and colours at a sixth of the triangles. The pack this
replaces had no far mesh at all, and the island carries some nine thousand of these.
"""

import math
import os
import random
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import palms as P  # noqa: E402
import trees as T  # noqa: E402

TAU, UP, GOLDEN, lerp = P.TAU, P.UP, P.GOLDEN, P.lerp

# ------------------------------------------------------------------------------------- textures

COLUMNS = 16
(FERN, FERN_LIGHT, LEAF_DARK, LEAF_BIG, PADDLE, BROM, BROM_HEART, CROTON,
 RED, WHITE, YELLOW, PURPLE, PINK, ORANGE, BRACT, STEM) = range(16)

RAMPS = {
    FERN: [(0, "1C3612"), (0.5, "3A6A22"), (1, "6E9E34")],
    FERN_LIGHT: [(0, "26461A"), (0.5, "52862C"), (1, "94BE48")],
    LEAF_DARK: [(0, "12281A"), (0.5, "24522A"), (1, "468236")],
    LEAF_BIG: [(0, "1A3E16"), (0.6, "367226"), (1, "56923A")],
    PADDLE: [(0, "203E2C"), (0.5, "386A44"), (1, "5E9258")],
    BROM: [(0, "28461C"), (0.5, "50722A"), (0.78, "9E4228"), (1, "D04E34")],
    BROM_HEART: [(0, "5E1028"), (0.5, "B82454"), (1, "F2628A")],
    CROTON: [(0, "3A2010"), (0.35, "8A3016"), (0.7, "CC6A1C"), (1, "E8B83A")],
    RED: [(0, "5E0610"), (0.35, "C41A2A"), (1, "F25A4E")],
    WHITE: [(0, "B8B8A0"), (0.3, "EEEEE2"), (1, "FFFFFA")],
    YELLOW: [(0, "B87408"), (0.5, "F0BE1E"), (1, "FFE468")],
    PURPLE: [(0, "2E1458"), (0.5, "6440B4"), (1, "AE90EE")],
    PINK: [(0, "8E2058"), (0.5, "E2589A"), (1, "FFA8CC")],
    ORANGE: [(0, "A8300A"), (0.5, "F06A18"), (1, "FFB244")],
    BRACT: [(0, "8A0E0E"), (0.6, "DE2618"), (0.82, "F09C1E"), (1, "F2D446")],
    STEM: [(0, "24361A"), (1, "62803A")],
}


def veined(stops, count, slant):
    """A broad leaf: pale midrib, and side veins running out and up from it."""
    def paint(u, v):
        c = P.ramp(stops, v)
        d = abs(u - 0.5) * 2
        c = c * (0.84 + 0.18 * (1 - d))
        if d < 0.07:
            return c * 1.22
        if math.sin((v * count - d * slant) * TAU) > 0.84:
            return c * 1.12
        return c
    return paint


def petal(stops, seed):
    """A petal: darker throat, fine streaks running to the tip."""
    streak = P.fibres(seed)
    return lambda u, v: P.ramp(stops, v) * (0.92 + 0.08 * streak(u) * (1 - v)) * (0.9 + 0.1 * (1 - abs(u - 0.5) * 2))


def paint_textures(folder):
    os.makedirs(folder, exist_ok=True)
    cols = {}
    for c, s in RAMPS.items():
        if c in (LEAF_BIG, PADDLE):
            cols[c] = veined(s, 9 if c == LEAF_BIG else 16, 1.6 if c == LEAF_BIG else 0.6)
        elif c == CROTON:
            cols[c] = veined(s, 7, 1.2)
        elif c in (FERN, FERN_LIGHT, LEAF_DARK, BROM, BROM_HEART):
            cols[c] = P.leaflet_paint(s)
        elif c == STEM:
            cols[c] = P.ramp_paint(s, 700 + c, 0.05)
        else:
            cols[c] = petal(s, 700 + c)

    def pixel(x, v):
        u = min(max((x % P.CELL + 0.5 - P.MARGIN) / (P.CELL - 2 * P.MARGIN), 0.0), 1.0)
        return cols.get(x // P.CELL, cols[0])(u, v)

    path = os.path.join(folder, "Leaves.png")
    P.write_png(path, P.CELL * COLUMNS, P.TEX_H, pixel)
    return path


def UV(col, u, v):
    return P.uv_on(COLUMNS, col, u, v)


# trees.hull, trees.leaves and trees.tube read these at call time: point them at this sheet.
T.FUV = T.BUV = UV
T.FOLIAGE = T.BARK = 0

# ---------------------------------------------------------------------------------------- parts


def softener(c0):
    """Normals bent off one point low in the plant, a third of the leaf's own facing on top."""
    def nfn(p, leafn):
        s = p - c0
        s = s.normalized() if s.length > 1e-4 else UP
        return (s * 0.5 + UP * 0.35 + leafn * 0.3).normalized()
    return nfn


def heading(h, e):
    return Vector((math.cos(h) * math.cos(e), math.sin(h) * math.cos(e), math.sin(e)))


def arc(base, d, length, droop, n):
    return [base + d * length * (i / (n - 1)) - UP * droop * length * (i / (n - 1)) ** 2 for i in range(n)]


def strap(m, pts, widths, col, nfn, fold=0.0, cols=1, v0=0.0, v1=1.0, side=None, uv=None, slot=0):
    """A leaf along a polyline, `2*cols+1` vertices across (two when cols is 0). A positive fold
    raises the midrib (a drooping leaf), a negative one sinks it (a channelled one)."""
    uv = uv or UV
    n = len(pts)
    offs = [-1.0, 1.0] if cols == 0 else [-1 + k / cols for k in range(2 * cols + 1)]
    d0 = (pts[1] - pts[0]).normalized()
    side = side or d0.cross(UP)
    side = side.normalized() if side.length > 1e-3 else Vector((1, 0, 0))
    rows = []
    for i, p in enumerate(pts):
        t = (pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)]).normalized()
        s = (side - t * side.dot(t)).normalized()
        up = s.cross(t).normalized()
        w = widths[i]
        if w < 1e-5:
            vid = m.v(p)
            rows.append([(vid, nfn(p, up), 0.5)] * len(offs))
            continue
        row = []
        for o in offs:
            q = p + s * o * w + up * fold * w * (1 - abs(o))
            row.append((m.v(q), nfn(q, up), (o + 1) / 2))
        rows.append(row)
    for i in range(n - 1):
        va, vb = lerp(v0, v1, i / (n - 1)), lerp(v0, v1, (i + 1) / (n - 1))
        t = (pts[i + 1] - pts[i]).normalized()
        face_up = side.cross(t)
        for k in range(len(offs) - 1):
            corners = [(rows[i][k], va), (rows[i][k + 1], va), (rows[i + 1][k + 1], vb), (rows[i + 1][k], vb)]
            seen, keep = set(), []
            for (vid, nrm, u), v in corners:
                if vid not in seen:
                    seen.add(vid)
                    keep.append((vid, nrm, u, v))
            if len(keep) < 3:
                continue
            m.f([c[0] for c in keep], [uv(col, c[2], c[3]) for c in keep], slot, [c[1] for c in keep], facing=face_up)


def diamond(m, base, tip, w, col, nfn, hint, mid=0.42, vb=0.0, vt=1.0, lift=0.0):
    """A leaflet or a petal: two triangles on a base-to-tip spine, `lift` folding them into a V."""
    axis = tip - base
    a = axis.normalized()
    perp = a.cross(hint)
    if perp.length < 1e-4:
        perp = a.cross(Vector((1, 0, 0)))
    perp.normalize()
    nl = perp.cross(a).normalized()
    if nl.dot(hint) < 0:
        nl = -nl
    c = base + axis * mid + nl * axis.length * lift
    pts = (base, c + perp * w * 0.5, tip, c - perp * w * 0.5)
    ids = [m.v(q) for q in pts]
    vm = lerp(vb, vt, mid)
    uvs = [UV(col, 0.5, vb), UV(col, 0, vm), UV(col, 0.5, vt), UV(col, 1, vm)]
    ns = [nfn(q, nl) for q in pts]
    m.f([ids[0], ids[1], ids[2]], [uvs[0], uvs[1], uvs[2]], 0, [ns[0], ns[1], ns[2]], facing=nl)
    m.f([ids[0], ids[2], ids[3]], [uvs[0], uvs[2], uvs[3]], 0, [ns[0], ns[2], ns[3]], facing=nl)


def frame(n):
    a = n.cross(Vector((0.31, 0.77, 0.12))).normalized()
    return a, n.cross(a).normalized()


def bloom(m, at, n, r, count, col, cup, rng, nfn, heart=None, width=0.5):
    """A flat-ish flower: petals round a centre, tips raised by `cup`, an optional dome of a heart."""
    n = n.normalized()
    a, b = frame(n)
    spin = rng.uniform(0, TAU)
    for k in range(count):
        th = spin + k * TAU / count + rng.uniform(-0.12, 0.12)
        d = a * math.cos(th) + b * math.sin(th)
        L = r * rng.uniform(0.85, 1.1)
        diamond(m, at, at + d * L + n * L * cup, L * width * 2.2 * math.sin(math.pi / count) * 1.6, col, nfn, n,
                mid=0.55, vb=0.05, vt=1.0)
    if heart is not None:
        top = m.v(at + n * r * 0.18)
        ring = [m.v(at + (a * math.cos(k * TAU / 5) + b * math.sin(k * TAU / 5)) * r * 0.24 + n * r * 0.05)
                for k in range(5)]
        for k in range(5):
            j = (k + 1) % 5
            m.f([ring[k], ring[j], top], [UV(heart, 0, 0.25), UV(heart, 1, 0.25), UV(heart, 0.5, 0.55)], 0,
                [nfn(at, n)] * 3, facing=n)


def stem(m, base, top, r, far, bend=None):
    mid = (base + top) / 2 + (bend or Vector())
    pts = [base, mid, top] if not far else [base, top]
    T.tube(m, pts, [r, r * 0.8, r * 0.6][:len(pts)], 3, STEM, 0.1, 0.9, tip=False)


# --------------------------------------------------------------------------------------- plants

def frond(m, base, d, length, droop, col, nfn, far, pinna):
    n = 5 if far else 10
    pts = arc(base, d, length, droop, n)

    def shape(t):
        return math.sin(math.pi * min(1.0, t * 1.05 + 0.1)) ** 0.6

    if far:
        widths = [length * pinna * 1.5 * shape(i / (n - 1)) for i in range(n)]
        widths[-1] = 0.0
        strap(m, pts, widths, col, nfn, fold=0.2, cols=0, v0=0.25, v1=0.95)
        return
    strap(m, pts, [0.012] * (n - 1) + [0.0], STEM, nfn, cols=0)
    side = d.cross(UP).normalized()
    for i in range(1, n):
        t = i / (n - 1)
        p = pts[i]
        tg = (pts[min(i + 1, n - 1)] - pts[i - 1]).normalized()
        s = (side - tg * side.dot(tg)).normalized()
        up = s.cross(tg).normalized()
        L = length * pinna * shape(t)
        v = 0.15 + 0.45 * t
        if i == n - 1:
            diamond(m, p, p + tg * L * 0.8, L * 0.3, col, nfn, up, vb=v, vt=v + 0.35, lift=0.08)
            continue
        for sgn in (1, -1):
            dp = (s * sgn + tg * 0.5 - up * 0.18).normalized()
            diamond(m, p, p + dp * L, L * 0.32, col, nfn, up, vb=v, vt=v + 0.35, lift=0.08)


def fern(m, spec, rng, far):
    nfn = softener(Vector((0, 0, 0.15)))
    count = spec["fronds"]
    for i in range(count):
        r = random.Random(rng.random())
        h = i * GOLDEN + r.uniform(-0.25, 0.25)
        e = math.radians(r.uniform(*spec["elev"]))
        length = spec["length"] * r.uniform(0.7, 1.1)
        droop = r.uniform(*spec["droop"])
        col = r.choice(spec["cols"])
        if far and i % 2:
            continue
        frond(m, Vector((math.cos(h), math.sin(h), 0)) * 0.04 + UP * 0.03, heading(h, e), length, droop, col, nfn,
              far, spec["pinna"])
    if not far:
        r = random.Random(rng.random())
        for k in range(spec.get("curls", 2)):
            h = r.uniform(0, TAU)
            out = Vector((math.cos(h), math.sin(h), 0))
            top = UP * r.uniform(0.18, 0.3)
            pts = [out * 0.02, out * 0.03 + top * 0.6]
            for j in range(6):
                a = j / 5 * 1.7 * math.pi
                rr = 0.06 * (1 - j / 8)
                pts.append(top + out * math.sin(a) * rr + UP * (math.cos(a) * rr - rr))
            T.tube(m, pts, [0.012 - 0.0012 * j for j in range(len(pts))], 3, FERN_LIGHT, 0.4, 0.95)


def bush(m, spec, rng, far):
    puffs = []
    for i in range(spec["puffs"]):
        a = i * GOLDEN + rng.uniform(-0.3, 0.3)
        out = spec["spread"] * (rng.uniform(0.5, 1.0) if i else 0.0)
        r = spec["puff"] * rng.uniform(0.8, 1.1) * (1.2 if i == 0 else 1.0)
        c = Vector((math.cos(a) * out, math.sin(a) * out, r * 0.9 + rng.uniform(0, spec["rise"])
                    + (spec["rise"] if i == 0 else 0)))
        puffs.append((c, r, random.Random(rng.random())))
    centre = sum((c for c, _, _ in puffs), Vector()) / len(puffs) - UP * spec["puff"] * 0.5
    nfn = softener(centre)
    flowers = random.Random(rng.random())

    if not far:
        for c, r, _ in puffs[1:4]:
            stem(m, Vector((c.x * 0.2, c.y * 0.2, 0)), c - UP * r * 0.3, 0.03, False)
    for c, r, pr in puffs:
        # Always the coarse hull: near, it is the dark shade between the leaves; far, it is the whole bush and
    # takes a lighter column, or a distant bush reads as a black lump.
        T.hull(m, c, r, spec["flat"], centre, random.Random(pr.random()), spec["far_hull"] if far else spec["hull"],
               True)
    blooms = []
    for _ in range(spec.get("flowers", 0)):
        c, r, _ = flowers.choice(puffs)
        d = Vector((flowers.uniform(-1, 1), flowers.uniform(-1, 1), flowers.uniform(-0.1, 1))).normalized()
        blooms.append((c + Vector((d.x, d.y, d.z * spec["flat"])) * r * 1.15, d + UP * 0.4,
                       random.Random(flowers.random())))
    for at, n, fr in blooms:
        if far:
            bloom(m, at, n, spec["bloom"] * 1.3, 3, spec["petals"], 0.2, fr, nfn)
        else:
            bloom(m, at, n, spec["bloom"], 5, spec["petals"], 0.35, fr, nfn, heart=YELLOW)
    if far:
        return
    budget = spec["tris"] - m.tris()
    area = sum(r * r for _, r, _ in puffs)
    for c, r, pr in puffs:
        T.leaves(m, c, r, spec["flat"], centre, random.Random(pr.random()), int(budget / 2 * r * r / area * 1.1),
                 spec["cols"], spec["light"], spec["leaf"], spec.get("leaf_width", 0.5))


def big_leaf(m, top, h, e, L, W, droop, col, nfn, far, shape, fold, back=0.15):
    d = heading(h, e)
    n = 4 if far else 7
    pts = arc(top - d * L * back, d, L, droop, n)
    widths = [W * shape(i / (n - 1)) for i in range(n)]
    widths[-1] = 0.0
    strap(m, pts, widths, col, nfn, fold=fold, cols=1 if far else 2)


def heart(t):
    return max(0.0, math.sin(math.pi * (t * 0.82 + 0.12))) ** 0.75


def paddle(t):
    return max(0.0, math.sin(math.pi * min(1.0, t * 0.92 + 0.06))) ** 0.45


def elephant(m, spec, rng, far):
    nfn = softener(Vector((0, 0, 0.3)))
    for i in range(spec["leaves"]):
        r = random.Random(rng.random())
        h = i * GOLDEN + r.uniform(-0.2, 0.2)
        d = heading(h, math.radians(r.uniform(48, 78)))
        top = d * spec["petiole"] * r.uniform(0.6, 1.1)
        bend = Vector((d.x, d.y, 0)) * 0.08
        stem(m, Vector((d.x, d.y, 0)) * 0.05, top, 0.028 if not far else 0.04, far, bend)
        big_leaf(m, top, h + r.uniform(-0.3, 0.3), math.radians(r.uniform(-40, -12)), spec["blade"] * r.uniform(0.75, 1.1),
                 spec["blade"] * 0.36 * r.uniform(0.9, 1.1), 0.2, LEAF_BIG, nfn, far, heart, 0.22)


def stalk_leaves(m, base, top, rng, spec, nfn, far):
    for k in range(spec["per_stalk"]):
        at = base.lerp(top, rng.uniform(0.45, 0.85))
        h = rng.uniform(0, TAU)
        e = math.radians(rng.uniform(*spec.get("leaf_elev", (25, 55))))
        p_top = at + heading(h, math.radians(60)) * spec.get("leaf_stalk", 0.12)
        if not far:
            stem(m, at, p_top, 0.015, True)
        big_leaf(m, p_top, h, e, spec["blade"] * rng.uniform(0.8, 1.1), spec["blade"] * spec["blade_w"], 0.35,
                 PADDLE, nfn, far, paddle, 0.12, back=0.0)


def heliconia(m, spec, rng, far):
    nfn = softener(Vector((0, 0, 0.5)))
    for i in range(spec["stalks"]):
        r = random.Random(rng.random())
        a = r.uniform(0, TAU)
        base = Vector((math.cos(a), math.sin(a), 0)) * r.uniform(0.0, 0.25)
        lean = Vector((math.cos(a) * 0.18, math.sin(a) * 0.18, 1)).normalized()
        top = base + lean * spec["height"] * r.uniform(0.7, 1.1)
        T.tube(m, [base, base.lerp(top, 0.5), top], [0.035, 0.028, 0.02], 3 if far else 4, STEM, 0.1, 0.9, tip=False)
        stalk_leaves(m, base, top, random.Random(r.random()), spec, nfn, far)
        if r.random() < spec["bloom"]:
            claw(m, top, lean, random.Random(r.random()), nfn, far)


def claw(m, top, lean, rng, nfn, far):
    """Heliconia's lobster claws: boat-shaped bracts alternating up a zigzag stalk."""
    side = lean.cross(heading(rng.uniform(0, TAU), 0)).normalized()
    count = 4 if far else 7
    p = top
    for k in range(count):
        sgn = 1 if k % 2 else -1
        q = p + lean * 0.07 + side * sgn * 0.025
        size = 0.24 * (1 - k / (count * 1.6))
        out = (side * sgn * 0.85 + lean * 0.55).normalized()
        diamond(m, q, q + out * size, size * 0.42, BRACT, nfn, lean.cross(side), mid=0.35, vb=0.15, vt=1.0, lift=0.3)
        p = q


def strelitzia(m, spec, rng, far):
    nfn = softener(Vector((0, 0, 0.4)))
    face = rng.uniform(0, TAU)
    for i in range(spec["leaves"]):
        r = random.Random(rng.random())
        h = face + (i / (spec["leaves"] - 1) - 0.5) * 2.4 + (math.pi if i % 2 else 0) + r.uniform(-0.15, 0.15)
        d = heading(h, math.radians(r.uniform(62, 80)))
        top = d * spec["petiole"] * r.uniform(0.7, 1.1)
        stem(m, Vector(), top, 0.022 if not far else 0.035, far)
        big_leaf(m, top, h, math.radians(r.uniform(30, 55)), spec["blade"] * r.uniform(0.8, 1.1),
                 spec["blade"] * 0.28, 0.25, PADDLE, nfn, far, paddle, 0.1, back=0.0)
    for k in range(spec["flowers"]):
        r = random.Random(rng.random())
        h = r.uniform(0, TAU)
        top = Vector((math.cos(h), math.sin(h), 0)) * 0.08 + UP * spec["petiole"] * r.uniform(0.9, 1.2)
        stem(m, Vector(), top, 0.02, far)
        fwd = heading(h + r.uniform(-1, 1), math.radians(8))
        k = 1.6
        diamond(m, top - fwd * 0.04 * k, top + fwd * 0.24 * k, 0.05 * k, PURPLE if far else STEM, nfn, UP, mid=0.3,
                lift=-0.2)
        for j in range(2 if far else 3):
            out = (UP * 0.9 + fwd * (0.15 + 0.25 * j) + fwd.cross(UP) * (j - 1) * 0.15).normalized()
            base = top + fwd * 0.05 * k
            diamond(m, base, base + out * 0.2 * k, 0.05 * k, ORANGE, nfn, fwd, mid=0.4, vb=0.2)
        if not far:
            diamond(m, top + fwd * 0.07 * k, top + (fwd * 0.17 + UP * 0.12) * k, 0.035 * k, PURPLE, nfn, fwd, vb=0.3)


def bromeliad(m, spec, rng, far):
    nfn = softener(Vector((0, 0, 0.08)))
    count = spec["leaves"]
    for i in range(count):
        r = random.Random(rng.random())
        t = i / (count - 1)
        h = i * GOLDEN
        e = math.radians(lerp(74, 14, t) + r.uniform(-6, 6))
        L = spec["length"] * lerp(0.5, 1.0, t) * r.uniform(0.85, 1.1)
        if far and i % 2:
            continue
        n = 4 if far else 6
        pts = arc(Vector((math.cos(h), math.sin(h), 0)) * 0.03 + UP * 0.03, heading(h, e), L, lerp(0.1, 0.55, t), n)
        W = spec["width"] * (1.4 if far else 1.0)
        widths = [W * (0.8 if k == 0 else 1 - (k / (n - 1)) ** 2.2) for k in range(n)]
        widths[-1] = 0.0
        strap(m, pts, widths, BROM_HEART if t < spec["heart"] else spec["col"], nfn, fold=-0.35, cols=0 if far else 1)
    rings = 1 if far else 3
    for k in range(rings):
        z = 0.12 + k * 0.07
        for j in range(4):
            a = j * TAU / 4 + k * 0.7
            out = Vector((math.cos(a), math.sin(a), 1.3 + k * 0.4)).normalized()
            base = UP * z
            diamond(m, base, base + out * (0.13 - k * 0.025), 0.06, YELLOW if k == rings - 1 else BROM_HEART, nfn,
                    out.cross(UP).cross(out), lift=0.2)


def flowerbed(m, spec, rng, far):
    nfn = softener(Vector((0, 0, 0.1)))
    for i in range(spec["blades"]):
        r = random.Random(rng.random())
        if far and i % 2:
            continue
        h = r.uniform(0, TAU)
        n = 3 if far else 4
        L = spec["blade_len"] * r.uniform(0.6, 1.1)
        pts = arc(Vector((math.cos(h), math.sin(h), 0)) * r.uniform(0, spec["spread"] * 0.7),
                  heading(h, math.radians(r.uniform(45, 75))), L, r.uniform(0.3, 0.6), n)
        W = spec["blade_w"] * (1.5 if far else 1.0)
        strap(m, pts, [W, W * 0.85, W * 0.5, 0.0][-n:] if n == 4 else [W, W * 0.6, 0.0], r.choice((FERN, FERN_LIGHT)),
              nfn, fold=0.25, cols=0)
    for i in range(spec["count"]):
        r = random.Random(rng.random())
        a, rad = r.uniform(0, TAU), spec["spread"] * math.sqrt(r.random())
        base = Vector((math.cos(a) * rad, math.sin(a) * rad, 0))
        lean = Vector((math.cos(a) * 0.25 + r.uniform(-0.1, 0.1), math.sin(a) * 0.25 + r.uniform(-0.1, 0.1), 1))
        top = base + lean.normalized() * spec["height"] * r.uniform(0.6, 1.1)
        col = r.choice(spec["colours"])
        if not far:
            stem(m, base, top, 0.007, False, Vector((r.uniform(-1, 1), r.uniform(-1, 1), 0)) * 0.03)
        kind = spec["kind"]
        if kind == "spike":
            spike(m, top, lean.normalized(), col, r, nfn, far)
        elif kind == "lily":
            lily(m, top, (lean.normalized() + UP * 0.5 + Vector((math.cos(a), math.sin(a), 0)) * 0.6).normalized(),
                 col, r, nfn, far)
        else:
            facing = (UP + Vector((math.cos(a), math.sin(a), 0)) * 0.35).normalized()
            petals = spec["petals"]
            bloom(m, top, facing, spec["bloom"] * r.uniform(0.85, 1.15) * (1.2 if far else 1.0),
                  max(3, petals // 2) if far else petals, col, spec["cup"], r, nfn,
                  heart=None if far else spec.get("heart", YELLOW))


def spike(m, top, axis, col, rng, nfn, far):
    """A lupin: florets spiralling up the last third of a stalk, shrinking towards the tip."""
    count = 6 if far else 16
    for k in range(count):
        t = k / count
        at = top + axis * (t * 0.3 - 0.12)
        phi = k * GOLDEN
        out = (Vector((math.cos(phi), math.sin(phi), 0)) - UP * 0.25).normalized()
        L = 0.085 * (1 - t * 0.6) * (1.4 if far else 1.0)
        diamond(m, at, at + out * L, L * 1.1, col, nfn, UP, mid=0.5, vb=0.15 + 0.3 * t, vt=0.7 + 0.3 * t, lift=0.25)


def lily(m, top, n, col, rng, nfn, far):
    """A trumpet: six petals rising from the throat and curling out at the lip."""
    a, b = frame(n)
    count = 3 if far else 6
    for k in range(count):
        th = k * TAU / count + rng.uniform(-0.1, 0.1)
        d = a * math.cos(th) + b * math.sin(th)
        L = 0.11 * rng.uniform(0.9, 1.1)
        mid = top + (n * 0.75 + d * 0.35).normalized() * L * 0.55
        tip = mid + (d * 0.85 + n * 0.1).normalized() * L * 0.6
        strap(m, [top, mid, tip], [0.008, L * 0.28 * (1.5 if far else 1.0), 0.0], col, nfn, fold=-0.25,
              cols=0, side=d.cross(n))


def grass(m, spec, rng, far):
    nfn = softener(Vector((0, 0, 0.1)))
    for i in range(spec["blades"]):
        r = random.Random(rng.random())
        if far and i % 3:
            continue
        h = r.uniform(0, TAU)
        n = 3 if far else 4
        pts = arc(Vector((math.cos(h), math.sin(h), 0)) * r.uniform(0, 0.08),
                  heading(h, math.radians(r.uniform(55, 85))), spec["length"] * r.uniform(0.55, 1.1),
                  r.uniform(0.2, 0.55), n)
        W = 0.022 * (2.0 if far else 1.0)
        strap(m, pts, [W, W * 0.6, 0.0] if far else [W, W * 0.85, W * 0.5, 0.0], r.choice((FERN, FERN_LIGHT, FERN_LIGHT)),
              nfn, fold=0.3, cols=0, v0=0.1)
    for i in range(spec["seeds"]):
        r = random.Random(rng.random())
        h = r.uniform(0, TAU)
        top = Vector((math.cos(h) * 0.15, math.sin(h) * 0.15, spec["length"] * r.uniform(1.1, 1.35)))
        if not far:
            strap(m, [Vector((0, 0, 0)), top * 0.5 + Vector((0, 0, 0.02)), top], [0.006, 0.005, 0.004], FERN_LIGHT,
                  nfn, cols=0, v0=0.4, v1=0.9)
        diamond(m, top, top + (top.normalized() + UP).normalized() * 0.12, 0.035, YELLOW, nfn,
                heading(h, 0), vb=0.0, vt=0.35, lift=0.2)


# ------------------------------------------------------------------------------------- variants

BUSH = dict(puffs=6, spread=0.6, puff=0.4, rise=0.3, flat=0.75, hull=LEAF_DARK, far_hull=FERN_LIGHT,
            cols=(LEAF_DARK, FERN), light=FERN_LIGHT, leaf=0.3, tris=660)
BLOOMS = dict(kind="bloom", blades=22, blade_len=0.32, blade_w=0.032, spread=0.36, count=14, height=0.38,
              bloom=0.07, petals=6, cup=0.25)

VARIANTS = [
    ("Plant_Fern", fern, 51, dict(fronds=12, elev=(30, 62), length=0.95, droop=(0.45, 0.75), pinna=0.24,
                                  cols=(FERN, FERN, FERN_LIGHT))),
    ("Plant_FernTall", fern, 52, dict(fronds=10, elev=(55, 78), length=1.25, droop=(0.35, 0.55), pinna=0.2,
                                      cols=(FERN_LIGHT, FERN), curls=3)),
    ("Plant_Bush", bush, 53, BUSH),
    ("Plant_Hibiscus", bush, 54, dict(BUSH, puffs=4, flowers=16, bloom=0.14, petals=RED, tris=680)),
    ("Plant_Croton", bush, 55, dict(BUSH, puffs=5, spread=0.45, puff=0.34, hull=LEAF_DARK, far_hull=CROTON,
                                    cols=(CROTON, CROTON, LEAF_DARK), light=CROTON, leaf=0.3, leaf_width=0.36)),
    ("Plant_ElephantEar", elephant, 56, dict(leaves=8, petiole=0.8, blade=0.75)),
    ("Plant_Bromeliad", bromeliad, 57, dict(leaves=20, length=0.62, width=0.075, heart=0.22, col=BROM)),
    ("Plant_Heliconia", heliconia, 58, dict(stalks=5, height=1.5, per_stalk=2, blade=0.8, blade_w=0.22, bloom=0.85)),
    ("Plant_Strelitzia", strelitzia, 59, dict(leaves=7, petiole=0.75, blade=0.5, flowers=2)),
    ("Flowers_Wild", flowerbed, 61, dict(BLOOMS, count=22, petals=5, bloom=0.06,
                                         colours=(PINK, PINK, YELLOW, WHITE, PURPLE, RED))),
    ("Flowers_Daisy", flowerbed, 62, dict(BLOOMS, count=16, petals=12, bloom=0.075, cup=0.12,
                                          colours=(WHITE, WHITE, WHITE, YELLOW))),
    ("Flowers_Lily", flowerbed, 63, dict(BLOOMS, kind="lily", count=8, height=0.5, blade_len=0.45,
                                         colours=(ORANGE, ORANGE, PINK, YELLOW))),
    ("Flowers_Spike", flowerbed, 64, dict(BLOOMS, kind="spike", count=9, height=0.6,
                                          colours=(PURPLE, PURPLE, PINK, WHITE))),
    ("Plant_Grass", grass, 65, dict(blades=36, length=0.6, seeds=6)),
]

# --------------------------------------------------------------------------------------- driver


def materials(texture):
    T.MATERIALS.clear()
    mat = bpy.data.materials.new("Leaves")
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.9
    image = bpy.data.images.load(texture, check_existing=False)
    image.name = "Leaves"
    tex = nodes.new("ShaderNodeTexImage")
    tex.image = image
    mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    T.MATERIALS.append(mat)


def main(out_dir=None, only=None):
    P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "plants")
    materials(paint_textures(folder))

    objects = []
    for name, maker, seed, spec in VARIANTS:
        if only and name not in only:
            continue
        built = []
        for far in (False, True):
            m = T.Mesh()
            maker(m, spec, random.Random(seed), far)
            obj = m.build(name + ("_Far" if far else ""))
            built.append(obj)
            xs, ys, zs = ([v[i] for v in m.verts] for i in range(3))
            wide, tall = max(max(xs) - min(xs), max(ys) - min(ys)), max(zs) - min(zs)
            print(f"[plants] {obj.name}: {m.tris()} tris, {wide:.2f} m across, {tall:.2f} m tall"
                  + (" (stands)" if tall >= 0.8 * wide else ""))
        objects.append(built)
        if out_dir is None:
            for obj in built:
                obj.location.x = len(objects) * 2.5 - 2.5
                obj.location.y = 3.0 if obj.name.endswith("_Far") else 0.0

    if out_dir is None:
        return objects

    for built in objects:
        bpy.ops.object.select_all(action="DESELECT")
        for obj in built:
            obj.select_set(True)
        bpy.context.view_layer.objects.active = built[0]
        bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, built[0].name + ".fbx"), use_selection=True,
                                 apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z",
                                 axis_up="Y", mesh_smooth_type="FACE", bake_space_transform=True,
                                 path_mode="RELATIVE")
    print(f"[plants] exported {len(objects)} to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
