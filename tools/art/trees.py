"""The jungle's broadleaf trees, modelled from code (#248).

    blender -b --factory-startup -P tools/art/trees.py -- Assets/_Project/Art/Models/Trees

Same contract as palms.py, whose helpers it borrows: re-runnable, one FBX per variant holding
`<name>` and `<name>_Far`, two ramp textures (Textures/TreeBark.png, Textures/TreeFoliage.png).
With no folder it builds the variants in a row inside the open Blender, for looking at.

How a canopy is made. Every branch tip carries a puff: a closed, low, dark blob with a fringe of
leaves anchored in it and pointing out. The blob fills the gaps, so a canopy never shows sky through
it; the leaves make the silhouette. Every foliage vertex carries a normal pointing away from its puff
and from the canopy's centre rather than its own face, so under the stylised shader's toon terminator
a canopy shades as a few soft lit masses instead of two thousand flickering facets. Foliage is
one-sided: from anywhere outside the tree, the leaves that show face the camera, and the material
keeps back-face culling (its slot is "Foliage", not "Leaves").

Roots, buttresses, lianas and aerial roots go in a third mesh, `<name>_Deco`, drawn with the near
tree. ArtLibrary measures the trunk collider without it: a fig's aerial roots stand metres from its
trunk, and a capsule round them would be an invisible wall.

The far mesh is the same skeleton with only the trunk and limbs, and each puff as its bare blob at
full size, coloured like the leaves.
"""

import math
import os
import random
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import palms as P  # noqa: E402

TAU, UP, GOLDEN, lerp = P.TAU, P.UP, P.GOLDEN, P.lerp

# ------------------------------------------------------------------------------------- textures

BARK_COLUMNS = 8
LEAF_COLUMNS = 16

GREY, PALE, DARK, FLAME_BARK, LIANA, AERIAL = range(6)
DEEP, MID, LIGHT, OLIVE, YOUNG, RED, ORANGE, HULL_DEEP, HULL_MID, HULL_LIGHT, HULL_FLAME, VINE = range(12)

# Bark runs base (v 0, moss) to crown (v 1). Branches use the upper part only.
BARK_RAMPS = {
    GREY: [(0, "3E4A2A"), (0.1, "54523E"), (0.22, "645C50"), (1, "7A7064")],
    PALE: [(0, "4A5632"), (0.08, "6A685A"), (0.25, "8C887A"), (1, "A09A8A")],
    DARK: [(0, "36442A"), (0.12, "463E32"), (0.3, "56493A"), (1, "6E6050")],
    FLAME_BARK: [(0, "485430"), (0.12, "5A4E44"), (1, "7A6858")],
    LIANA: [(0, "3E4E26"), (1, "6A6436")],
    AERIAL: [(0, "4A4232"), (0.6, "7A6C58"), (1, "8E806A")],
}

# Leaves run base (v 0) to tip (v 1); hulls run the puff's bottom to its top.
LEAF_RAMPS = {
    DEEP: [(0, "1C461C"), (0.6, "2C6626"), (1, "3C7A2C")],
    MID: [(0, "285A20"), (0.6, "44862C"), (1, "5C9C34")],
    LIGHT: [(0, "3A7424"), (0.6, "66A436"), (1, "88BC46")],
    OLIVE: [(0, "3C5620"), (0.6, "687A2C"), (1, "8A963A")],
    YOUNG: [(0, "548424"), (0.6, "96C040"), (1, "C0D866")],
    RED: [(0, "84200F"), (0.5, "CC3E1C"), (1, "EE6628")],
    ORANGE: [(0, "9C4012"), (0.5, "DC761C"), (1, "F2A236")],
    VINE: [(0, "2E5A22"), (1, "5A9030")],
}
HULL_RAMPS = {
    HULL_DEEP: [(0, "122C16"), (1, "28501E")],
    HULL_MID: [(0, "183A18"), (1, "366424")],
    HULL_LIGHT: [(0, "224A1C"), (1, "4A7E2C")],
    HULL_FLAME: [(0, "4A1A10"), (0.5, "8A2E16"), (1, "B04A1E")],
}


def bark_paint(stops, seed):
    streak = P.fibres(seed)
    crack = P.fibres(seed + 50)

    def paint(u, v):
        c = P.ramp(stops, v) * (1 + 0.14 * streak(u))
        if crack(u) > 0.55:          # the fissures: thin dark runs up the bark
            c = c * 0.72
        return c
    return paint


def leaf_paint(stops):
    def paint(u, v):
        c = P.ramp(stops, v) * (0.86 + 0.16 * (1 - abs(u - 0.5) * 2))
        return c * 1.08 if abs(u - 0.5) < 0.05 and v < 0.85 else c
    return paint


def paint_textures(folder):
    os.makedirs(folder, exist_ok=True)
    bark = {c: bark_paint(s, 300 + c) for c, s in BARK_RAMPS.items()}
    leaf = {c: leaf_paint(s) for c, s in LEAF_RAMPS.items()}
    leaf.update({c: P.ramp_paint(s, 400 + c, 0.06) for c, s in HULL_RAMPS.items()})

    def sheet(columns):
        def pixel(x, v):
            u = min(max((x % P.CELL + 0.5 - P.MARGIN) / (P.CELL - 2 * P.MARGIN), 0.0), 1.0)
            return columns.get(x // P.CELL, columns[0])(u, v)
        return pixel

    paths = (os.path.join(folder, "TreeBark.png"), os.path.join(folder, "TreeFoliage.png"))
    P.write_png(paths[0], P.CELL * BARK_COLUMNS, P.TEX_H, sheet(bark))
    P.write_png(paths[1], P.CELL * LEAF_COLUMNS, P.TEX_H, sheet(leaf))
    return paths


def BUV(col, u, v):
    return P.uv_on(BARK_COLUMNS, col, u, v)


def FUV(col, u, v):
    return P.uv_on(LEAF_COLUMNS, col, u, v)


# ----------------------------------------------------------------------------------------- mesh

BARK, FOLIAGE = 0, 1
MATERIALS = []


class Mesh(P.Mesh):
    """palms.Mesh with a normal per corner (None: the face's own, for hard-edged parts)."""

    def f(self, idx, uvs, slot, normals=None, facing=None):
        if facing is not None:
            a, b, c = (self.verts[i] for i in idx[:3])
            if (b - a).cross(c - a).dot(facing) < 0:
                idx, uvs = idx[::-1], uvs[::-1]
                normals = normals[::-1] if normals else None
        self.faces.append((list(idx), list(uvs), slot, normals))

    def build(self, name):
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata([tuple(v) for v in self.verts], [], [f[0] for f in self.faces])
        for mat in MATERIALS:
            mesh.materials.append(mat)
        layer = mesh.uv_layers.new(name="UVMap")
        loop_normals = [None] * len(mesh.loops)
        for poly, (_, uvs, slot, normals) in zip(mesh.polygons, self.faces):
            poly.material_index = slot
            poly.use_smooth = True
            for k, (loop, uv) in enumerate(zip(poly.loop_indices, uvs)):
                layer.data[loop].uv = uv
                loop_normals[loop] = tuple(normals[k]) if normals else tuple(poly.normal)
        mesh.normals_split_custom_set(loop_normals)
        mesh.update()
        obj = bpy.data.objects.new(name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        return obj


def sides_for(r, far):
    n = 3 if r < 0.035 else 4 if r < 0.07 else 5 if r < 0.13 else 6 if r < 0.22 else 8 if r < 0.4 else 10
    return max(3, n - 2) if far else n


def tube(m, pts, radii, sides, col, v0=0.3, v1=1.0, lobes=None, tip=True):
    """A branch: rings along a polyline, frames carried along it, normals straight out of the axis.
    `lobes(i, a)` scales ring i at angle a: a trunk's root flare."""
    n = len(pts)
    tans = [(pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)]).normalized() for i in range(n)]
    ref = UP if abs(tans[0].dot(UP)) < 0.9 else Vector((1, 0, 0))
    nrm = (ref - tans[0] * ref.dot(tans[0])).normalized()
    rings = []
    for i, (p, t, r) in enumerate(zip(pts, tans, radii)):
        nrm = (nrm - t * nrm.dot(t)).normalized()
        b = t.cross(nrm)
        ring = []
        for k in range(sides):
            a = k * TAU / sides
            d = nrm * math.cos(a) + b * math.sin(a)
            ring.append((m.v(p + d * r * (lobes(i, a) if lobes else 1.0)), d))
        rings.append(ring)
    for i in range(n - 1):
        va, vb = lerp(v0, v1, i / (n - 1)), lerp(v0, v1, (i + 1) / (n - 1))
        for k in range(sides):
            j = (k + 1) % sides
            q = (rings[i][k], rings[i][j], rings[i + 1][j], rings[i + 1][k])
            m.f([x[0] for x in q],
                [BUV(col, k / sides, va), BUV(col, (k + 1) / sides, va), BUV(col, (k + 1) / sides, vb),
                 BUV(col, k / sides, vb)], BARK, [x[1] for x in q], facing=q[0][1] + q[1][1])
    if tip:
        apex = m.v(pts[-1] + tans[-1] * radii[-1])
        for k in range(sides):
            j = (k + 1) % sides
            m.f([rings[-1][k][0], rings[-1][j][0], apex], [BUV(col, k / sides, v1), BUV(col, (k + 1) / sides, v1),
                BUV(col, (k + 0.5) / sides, v1)], BARK, [rings[-1][k][1], rings[-1][j][1], tans[-1]],
                facing=tans[-1])


def deflect(d, angle, az):
    side = d.cross(UP)
    side = side.normalized() if side.length > 1e-3 else Vector((1, 0, 0))
    other = d.cross(side).normalized()
    perp = side * math.cos(az) + other * math.sin(az)
    return (d * math.cos(angle) + perp * math.sin(angle)).normalized()


def jitter(rng, k):
    return Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))) * k


# ------------------------------------------------------------------------------------ skeleton

class Tree:
    """What a tree decided: its tips (where puffs go) and the points lianas and roots hang from."""

    def __init__(self):
        self.tips, self.hangs = [], []


def grow(m, tree, rng, spec, start, d, length, r0, depth, far):
    """One branch and, recursively, its children. Every random draw happens whatever `far` is,
    so the far tree is the near tree."""
    segs = spec["segs"]
    pts, radii = [start], [r0]
    r1 = r0 * spec["taper"]
    p, dd = start.copy(), d.copy()
    for s in range(1, segs + 1):
        dd = (dd + UP * spec["rise"] / segs + jitter(rng, spec["wiggle"])).normalized()
        p = p + dd * length / segs
        pts.append(p.copy())
        radii.append(lerp(r0, r1, s / segs))
    last = depth == spec["depth"]
    kids = 0 if last else spec["kids"][depth]
    plans = []
    for c in range(kids):
        at = lerp(spec.get("fork_from", 0.55), 1.0, (c + 1) / kids)
        az = rng.uniform(0, TAU) if c == 0 else plans[-1][1] + TAU / kids + rng.uniform(-0.5, 0.5)
        angle = math.radians(spec["angle"][depth] + rng.uniform(-10, 10))
        plans.append((at, az, angle, rng.uniform(0.8, 1.15)))
    if depth == 1:
        tree.hangs.append((pts[segs // 2], radii[segs // 2]))

    if not (far and depth >= 2):
        tube(m, pts, radii, sides_for(r0, far), spec["bark"], lerp(0.3, 0.9, depth / 3), 1.0)
    if last:
        tree.tips.append((p, dd, 1.0))
        return
    if depth == spec["depth"] - 1:
        # A smaller puff partway along the last forks, so the crown is a mass and not a ring of tips.
        k = int(segs * 0.6)
        tree.tips.append((pts[k], (pts[k + 1] - pts[k]).normalized(), 0.8))
    for at, az, angle, scale in plans:
        k = at * segs
        i = min(int(k), segs - 1)
        q = pts[i].lerp(pts[i + 1], k - i)
        r = lerp(radii[i], radii[i + 1], k - i)
        cd = deflect(dd if at > 0.95 else (pts[i + 1] - pts[i]).normalized(), angle, az)
        cd = (cd + Vector((cd.x, cd.y, 0)) * spec.get("spread", 0.0)).normalized()
        grow(m, tree, rng, spec, q, cd, length * spec["ratio"] * scale, max(r * 0.78, 0.025), depth + 1, far)


def trunk(m, tree, rng, spec, stem, far):
    sp = P.leaning_spine(Vector(stem.get("base", (0, 0, 0))), stem["height"], stem.get("lean", 0.3),
                         stem.get("heading", rng.uniform(0, TAU)), stem.get("bend", 0.2))
    r_base, r_top = stem.get("r_base", spec["r_base"]), stem.get("r_top", spec["r_top"])
    flare = spec.get("flare", 0.5)
    n = 5 if far else 9
    ss = [-0.25 / sp.length] + [(i / (n - 1)) ** 1.3 for i in range(n)]
    pts = [sp.frame(s)[0] for s in ss]
    radii = [lerp(r_base, r_top, max(s, 0)) * (1 + flare * math.exp(-max(s, 0) * sp.length / 0.7)) for s in ss]
    phase = rng.uniform(0, TAU)
    lobe = spec.get("lobes", 0.25)

    def lobes(i, a):
        s = max(ss[i], 0)
        return 1 + lobe * math.exp(-s * sp.length / 0.9) * math.cos(5 * a + phase)

    tube(m, pts, radii, sides_for(r_base, far) + (0 if far else 2), spec["bark"], 0.0, 0.62, lobes)

    limbs = stem.get("limbs", spec["limbs"])
    az0 = rng.uniform(0, TAU)
    for i in range(limbs):
        s = lerp(spec["crown_from"], 1.0, i / max(limbs - 1, 1)) + rng.uniform(-0.03, 0.03)
        p, t, _, _ = sp.frame(min(s, 1.0))
        r = lerp(r_base, r_top, s)
        angle = math.radians(spec["limb_angle"] + rng.uniform(-8, 8)) if i < limbs - 1 else math.radians(12)
        d = deflect(t, angle, az0 + i * GOLDEN)
        length = spec["limb_len"] * rng.uniform(0.85, 1.15) * (0.8 if i == limbs - 1 else 1.0)
        grow(m, tree, rng, spec, p - t * r * 0.5, d, length, r * 0.72, 1, far)
    return sp, r_base


def buttress(m, rng, sp, r, count, reach, height, far):
    """Plank roots: thin fins out of the trunk foot, tall at the bark, running down to the ground."""
    if far:
        return
    p, t, n, b = sp.frame(0.0)
    a0 = rng.uniform(0, TAU)
    for k in range(count):
        a = a0 + k * TAU / count + rng.uniform(-0.3, 0.3)
        out = Vector((math.cos(a), math.sin(a), 0))
        perp = Vector((-out.y, out.x, 0))
        rr = reach * rng.uniform(0.75, 1.2)
        hh = height * rng.uniform(0.8, 1.15)
        secs = []
        for i in range(5):
            x = i / 4
            c = p + out * (r * 0.8 + rr * x) + perp * math.sin(x * 2.2) * 0.12
            top = hh * (1 - x) ** 1.7 + 0.06
            th = lerp(0.16, 0.05, x)
            secs.append([m.v(c + perp * th - UP * 0.25), m.v(c + perp * th * 0.6 + UP * top),
                         m.v(c - perp * th * 0.6 + UP * top), m.v(c - perp * th - UP * 0.25)])
        for i in range(4):
            va, vb = 0.04 + 0.1 * i / 4, 0.04 + 0.1 * (i + 1) / 4
            for e, facing in ((0, perp), (1, UP + out * 0.3), (2, -perp)):
                q = [secs[i][e], secs[i][e + 1], secs[i + 1][e + 1], secs[i + 1][e]]
                m.f(q, [BUV(GREY, e / 3, va), BUV(GREY, (e + 1) / 3, va), BUV(GREY, (e + 1) / 3, vb),
                        BUV(GREY, e / 3, vb)], BARK, None, facing=facing)
        q = secs[-1]
        m.f([q[0], q[1], q[2], q[3]], [BUV(GREY, 0, 0.1), BUV(GREY, 0.3, 0.1), BUV(GREY, 0.6, 0.1),
                                       BUV(GREY, 1, 0.1)], BARK, None, facing=out)


def surface_roots(m, rng, sp, r, count, reach, col, far):
    if far:
        return
    p = sp.frame(0.0)[0]
    a0 = rng.uniform(0, TAU)
    for k in range(count):
        a = a0 + k * TAU / count + rng.uniform(-0.35, 0.35)
        out = Vector((math.cos(a), math.sin(a), 0))
        length = reach * rng.uniform(0.6, 1.2)
        pts = [p + out * r * 0.5 + UP * 0.45]
        for i in range(1, 5):
            x = i / 4
            pts.append(p + out * (r * 0.5 + length * x) + UP * (0.45 - 0.6 * x ** 1.2)
                       + Vector((-out.y, out.x, 0)) * math.sin(x * 3 + k) * 0.12)
        rr = r * rng.uniform(0.32, 0.42)
        tube(m, pts, [rr * lerp(1, 0.4, i / 4) for i in range(5)], 4, col, 0.05, 0.2)


def hanging(m, rng, top, r, ground, col, sides, thicken=1.0):
    """A liana or an aerial root: from a branch down toward the ground, swinging a little."""
    drop = top.z - ground
    off = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0)) * 0.35
    pts = [top + off * (i / 6) - UP * drop * (i / 6) + Vector((math.sin(i * 1.3), math.cos(i * 0.9), 0)) * 0.05
           for i in range(7)]
    tube(m, pts, [r * lerp(1.0, thicken, (i / 6) ** 2) for i in range(7)], sides, col, 0.2, 0.9, tip=False)
    return pts


# --------------------------------------------------------------------------------------- canopy

def soft_normal(p, centre, canopy):
    n = (p - centre).normalized() * 0.55 + (p - canopy).normalized() * 0.45 + UP * 0.15
    return n.normalized()


def hull(m, c, r, flat, canopy, rng, col, far):
    lat, lon = (3, 6) if far else (4, 7)
    scale = 1.0 if far else 0.84
    poles = (m.v(c + UP * r * flat * scale), m.v(c - UP * r * flat * scale * 0.9))
    rings = []
    for i in range(1, lat):
        phi = math.pi * i / lat
        rings.append([m.v(c + Vector((math.sin(phi) * math.cos(th), math.sin(phi) * math.sin(th),
                                      math.cos(phi) * flat)) * r * scale * rng.uniform(0.9, 1.08))
                      for th in (k * TAU / lon + i * 0.4 for k in range(lon))])

    def uv(v):
        return (FUV(col, 0.5, lerp(0.1, 0.62, v)) if far else FUV(col, 0.5, v))

    def face(ids, vs):
        pts = [m.verts[i] for i in ids]
        m.f(ids, [uv(v) for v in vs], FOLIAGE, [soft_normal(q, c, canopy) for q in pts],
            facing=sum(pts, Vector()) / len(pts) - c)

    for k in range(lon):
        j = (k + 1) % lon
        face([rings[0][k], rings[0][j], poles[0]], (0.8, 0.8, 1.0))
        face([rings[-1][j], rings[-1][k], poles[1]], (0.15, 0.15, 0.0))
        for i in range(lat - 2):
            v0, v1 = 0.8 - 0.65 * i / (lat - 2), 0.8 - 0.65 * (i + 1) / (lat - 2)
            face([rings[i][k], rings[i][j], rings[i + 1][j], rings[i + 1][k]], (v0, v0, v1, v1))


def leaves(m, c, r, flat, canopy, rng, count, cols, light, size, width):
    """A fringe of leaves on a puff: spread over its surface by the golden spiral, rooted inside the
    hull, pointing out and a little down, folded along the midrib."""
    for i in range(count):
        z = 1 - 2 * (i + 0.5) / count
        if z < -0.6 and rng.random() < 0.6:
            continue
        rr = math.sqrt(max(0.0, 1 - z * z))
        th = i * GOLDEN
        d = (Vector((rr * math.cos(th), rr * math.sin(th), z)) + jitter(rng, 0.12)).normalized()
        surf = c + Vector((d.x, d.y, d.z * flat)) * r * rng.uniform(0.84, 1.02)
        out = Vector((d.x, d.y, d.z / flat)).normalized()
        down = -UP - out * (-UP).dot(out)
        tang = jitter(rng, 1.0)
        tang = (tang - out * tang.dot(out))
        axis = (down.normalized() * 0.9 if down.length > 1e-3 else Vector()) + tang.normalized() * 0.6
        axis = (axis.normalized() + out * rng.uniform(0.1, 0.4)).normalized()
        side = axis.cross(out).normalized()
        length = size * rng.uniform(0.8, 1.2)
        w = length * width
        base = surf - axis * length * 0.35
        tip = base + axis * length
        mid = base + axis * length * 0.45 + out * length * 0.07
        left, right = mid + side * w * 0.5, mid - side * w * 0.5
        col = light if (out.z > 0.35 and rng.random() < 0.55) else rng.choice(cols)
        ids = [m.v(q) for q in (base, left, tip, right)]
        uvs = [FUV(col, 0.5, 0.0), FUV(col, 0.0, 0.45), FUV(col, 0.5, 1.0), FUV(col, 1.0, 0.45)]
        ns = [soft_normal(q, c, canopy) for q in (base, left, tip, right)]
        m.f([ids[0], ids[1], ids[2]], [uvs[0], uvs[1], uvs[2]], FOLIAGE, [ns[0], ns[1], ns[2]], facing=out)
        m.f([ids[0], ids[2], ids[3]], [uvs[0], uvs[2], uvs[3]], FOLIAGE, [ns[0], ns[2], ns[3]], facing=out)


def vine_leaves(m, rng, pts, col):
    """Little two-sided leaves along a liana."""
    for i in range(1, len(pts) - 1):
        if rng.random() < 0.35:
            continue
        p = pts[i]
        a = rng.uniform(0, TAU)
        out = Vector((math.cos(a), math.sin(a), rng.uniform(-0.3, 0.3))).normalized()
        side = out.cross(UP).normalized()
        tip = p + out * 0.28 - UP * 0.1
        l, r = p + out * 0.12 + side * 0.08, p + out * 0.12 - side * 0.08
        ids = [m.v(q) for q in (p, l, tip, r)]
        uv = [FUV(col, 0.5, 0), FUV(col, 0, 0.5), FUV(col, 0.5, 1), FUV(col, 1, 0.5)]
        n = side.cross(out).normalized()
        if n.z < 0:
            n = -n
        for flip in (n, -n):
            m.f([ids[0], ids[1], ids[2], ids[3]], uv, FOLIAGE, [flip] * 4, facing=flip)


def canopy(m, tree, rng, spec, far):
    tips = tree.tips
    puffs = []
    for p, d, size in tips:
        r = spec["puff"] * size * rng.uniform(0.85, 1.15)
        c = p + d * r * 0.35 + UP * r * 0.15
        puffs.append((c, r))
    for c, r in spec.get("extra_puffs", []):
        puffs.append((Vector(c), r))
    centre = sum((c for c, _ in puffs), Vector()) / len(puffs) - UP * spec["puff"] * 0.6
    zs = [c.z for c, _ in puffs]
    lo, hi = min(zs), max(zs)
    hull_cols = spec["hulls"]
    plans = []
    for c, r in puffs:
        h = (c.z - lo) / max(hi - lo, 1e-3)
        hc = hull_cols[min(len(hull_cols) - 1, int(h * len(hull_cols)))]
        plans.append((c, r, hc, h, random.Random(rng.random())))

    if far:
        for c, r, hc, h, prng in plans:
            hull(m, c, r, spec["flat"], centre, prng, spec["far_col"](h, prng), True)
        return
    for c, r, hc, h, prng in plans:
        hull(m, c, r, spec["flat"], centre, prng, hc, False)
    budget = spec.get("tris", 6300) - m.tris() - m.deco.tris()
    area = sum(r * r for _, r, _, _, _ in plans)
    for c, r, hc, h, prng in plans:
        n = int(budget / 2 * r * r / area * 1.12)
        leaves(m, c, r, spec["flat"], centre, prng, n, spec["leaf_cols"](h, prng), spec["light"], spec["leaf"],
               spec.get("leaf_width", 0.5))


# ---------------------------------------------------------------------------------------- trees

def broadleaf(m, spec, rng, far):
    tree = Tree()
    for stem in spec["stems"]:
        sp, r = trunk(m, tree, rng, spec, stem, far)
        if spec.get("buttress"):
            buttress(m.deco, random.Random(rng.random()), sp, r, *spec["buttress"], far)
        if spec.get("roots"):
            surface_roots(m.deco, random.Random(rng.random()), sp, r, spec["roots"], spec.get("root_reach", 1.2),
                          spec["bark"], far)

    hang_rng = random.Random(rng.random())
    vine_plan = []
    for top, r in tree.hangs:
        if hang_rng.random() < spec.get("lianas", 0.0):
            vine_plan.append(("liana", top, hang_rng.uniform(0.6, top.z * 0.5), random.Random(hang_rng.random())))
        if hang_rng.random() < spec.get("aerial", 0.0):
            vine_plan.append(("aerial", top, -0.2, random.Random(hang_rng.random())))
    if not far:
        for kind, top, ground, vr in vine_plan:
            if kind == "liana":
                pts = hanging(m.deco, vr, top, 0.03, ground, LIANA, 3)
                vine_leaves(m.deco, vr, pts, VINE)
            else:
                hanging(m.deco, vr, top, vr.uniform(0.035, 0.06), ground, AERIAL, 4, thicken=2.2)

    canopy(m, tree, random.Random(rng.random()), spec, far)


def greens(*cols):
    return lambda h, rng: cols


def flame_leaves(h, rng):
    return (RED, ORANGE, RED, LIGHT) if h > 0.35 else (MID, LIGHT, RED)


def flame_far(h, rng):
    return RED if h > 0.35 else MID


BASE = dict(segs=4, taper=0.55, rise=0.25, wiggle=0.08, ratio=0.62, depth=3, kids=(0, 2, 2), angle=(0, 34, 30),
            r_base=0.32, r_top=0.2, flare=0.55, lobes=0.28, limbs=4, crown_from=0.75, limb_angle=48, limb_len=3.2,
            spread=0.25, puff=1.6, flat=0.8, leaf=0.85, leaf_width=0.55, bark=GREY,
            hulls=(HULL_DEEP, HULL_MID, HULL_LIGHT), light=LIGHT, leaf_cols=greens(DEEP, MID, MID, OLIVE),
            far_col=lambda h, rng: DEEP if h < 0.4 else MID, lianas=0.3, roots=5)

VARIANTS = [
    # A rain tree: short trunk, four great limbs, a wide low dome.
    ("Tree_Rain", broadleaf, 21, dict(BASE, stems=[dict(height=3.4, lean=0.4)], limbs=5, crown_from=0.7,
                                     limb_angle=54, limb_len=3.6, puff=1.8, flat=0.7, spread=0.5,
                                     r_base=0.42, r_top=0.32, root_reach=1.6)),
    # A kapok: a tall pale column on plank roots, flat tiers of crown on top.
    ("Tree_Kapok", broadleaf, 22, dict(BASE, stems=[dict(height=11.0, lean=0.5)], r_base=0.48, r_top=0.26,
                                      flare=0.35, lobes=0.12, bark=PALE, limbs=6, crown_from=0.72, limb_angle=74,
                                      limb_len=3.3, rise=0.18, puff=1.5, flat=0.65, spread=0.45, roots=0,
                                      buttress=(6, 1.3, 1.8), lianas=0.5,
                                      leaf_cols=greens(MID, OLIVE, LIGHT, MID))),
    # A strangler fig: three stems fused, aerial roots falling to the ground, a dark dense crown.
    ("Tree_Fig", broadleaf, 23, dict(BASE, bark=DARK, r_base=0.24, r_top=0.15, flare=0.4, lobes=0.35,
                                    stems=[dict(height=6.0, lean=0.5, base=(0.18, 0, 0), heading=0.2, limbs=3),
                                           dict(height=5.4, lean=0.9, base=(-0.1, 0.16, 0), heading=2.3, limbs=2),
                                           dict(height=4.8, lean=1.0, base=(-0.1, -0.16, 0), heading=4.3,
                                                limbs=2)],
                                    limb_angle=50, limb_len=3.1, kids=(0, 2, 1), puff=1.9, aerial=0.9, lianas=0.2, roots=7,
                                    root_reach=1.5, hulls=(HULL_DEEP, HULL_DEEP, HULL_MID),
                                    leaf_cols=greens(DEEP, DEEP, MID), light=MID)),
    # A flame tree in flower: an umbrella of red over the green.
    ("Tree_Flame", broadleaf, 24, dict(BASE, stems=[dict(height=3.0, lean=0.6)], bark=FLAME_BARK, limbs=5,
                                      limb_angle=56, limb_len=3.0, puff=1.55, flat=0.65, spread=0.55,
                                      hulls=(HULL_MID, HULL_FLAME, HULL_FLAME), leaf_cols=flame_leaves,
                                      light=ORANGE, far_col=flame_far, leaf=0.75, lianas=0.0)),
    # A young tree: one slim stem, a loose crown, the light green of new growth.
    ("Tree_Young", broadleaf, 25, dict(BASE, stems=[dict(height=3.4, lean=0.3)], r_base=0.12, r_top=0.07,
                                      flare=0.4, lobes=0.15, limbs=4, crown_from=0.55, limb_angle=36,
                                      limb_len=1.7, depth=2, kids=(0, 2), puff=1.1, leaf=0.6, roots=0,
                                      lianas=0.0, tris=4200, hulls=(HULL_MID, HULL_LIGHT),
                                      leaf_cols=greens(MID, LIGHT, YOUNG), light=YOUNG)),
    # An old giant of the forest: heavy, crooked, hung with lianas.
    ("Tree_Old", broadleaf, 26, dict(BASE, stems=[dict(height=5.5, lean=1.2, bend=0.6)], r_base=0.5, r_top=0.3,
                                    flare=0.6, lobes=0.32, limbs=5, limb_angle=52, limb_len=3.8, wiggle=0.14,
                                    puff=1.7, lianas=0.8, roots=7, root_reach=1.7,
                                    leaf_cols=greens(DEEP, OLIVE, MID, DEEP), light=OLIVE)),
]


# --------------------------------------------------------------------------------------- driver

def materials(textures):
    MATERIALS.clear()
    for name, path in (("TreeBark", textures[0]), ("TreeFoliage", textures[1])):
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        mat.use_backface_culling = True
        nodes = mat.node_tree.nodes
        bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        bsdf.inputs["Roughness"].default_value = 0.9
        image = bpy.data.images.load(path, check_existing=False)
        image.name = name
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = image
        mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        MATERIALS.append(mat)


def main(out_dir=None, only=None):
    P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "trees")
    materials(paint_textures(folder))

    objects = []
    for name, maker, seed, spec in VARIANTS:
        if only and name not in only:
            continue
        built = []
        for far in (False, True):
            m = Mesh()
            m.deco = Mesh()
            maker(m, spec, random.Random(seed), far)
            obj = m.build(name + ("_Far" if far else ""))
            built.append(obj)
            if m.deco.faces:
                built.append(m.deco.build(name + "_Deco"))
            xs, ys, zs = ([v[i] for v in m.verts] for i in range(3))
            wide, tall = max(max(xs) - min(xs), max(ys) - min(ys)), max(zs) - min(zs)
            print(f"[trees] {obj.name}: {m.tris() + m.deco.tris()} tris, {wide:.1f} m across, {tall:.1f} m tall"
                  + ("" if tall >= 0.8 * wide else "  LIES DOWN for ArtVisual.Standing"))
        objects.append(built)
        if out_dir is None:
            for obj in built:
                obj.location.x = len(objects) * 14.0 - 14.0
                obj.location.y = 18.0 if obj.name.endswith("_Far") else 0.0

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
    print(f"[trees] exported {len(objects)} to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
