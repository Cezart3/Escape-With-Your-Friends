"""The island's palms, modelled from code (#282).

    blender -b --factory-startup -P tools/art/palms.py -- Assets/_Project/Art/Models/Palms

Re-runnable: clears the scene, paints the two ramp textures, builds every variant in VARIANTS and
exports one FBX per variant, plus Textures/PalmBark.png and Textures/PalmLeaves.png, into the folder
given after `--`. Run inside a live Blender with no folder (the MCP's execute_blender_code, through
runpy) and it only builds them in a row for looking at.

Every FBX holds two meshes: `<name>`, the near one, and `<name>_Far`, a few hundred triangles of the
same tree that ArtLibrary.EnsureFloraPrefab puts on the far LOD. Both come from one plan, so the far
tree is the near tree with the detail taken out, never a different tree.

How it is painted. Two materials, PalmBark and PalmLeaves, each one texture of vertical colour ramps,
one ramp per column. A face picks its column and runs along the ramp: a leaflet goes dark at the
rachis to light at the tip, a trunk band goes shadowed under the lip above it to a pale fresh ring.
Across a leaflet column the middle is lighter, so the midrib shows. That is all the texture there
is; the shapes do the rest. Leaves are single faces drawn from both sides (ArtLibrary gives a
"Leaves" slot Cull Off, and the shader turns a back face's normal round).
"""

import bisect
import math
import os
import random
import struct
import sys
import zlib

import bpy
from mathutils import Vector

TAU = math.tau
UP = Vector((0.0, 0.0, 1.0))
GOLDEN = math.radians(137.508)

# ------------------------------------------------------------------------------------- textures

CELL = 32        # pixels per ramp column
MARGIN = 4       # pixels kept clear at each column's sides, against filtering and mip bleed
TEX_H = 256
LEAF_COLUMNS = 8
BARK_COLUMNS = 16

# Leaf columns.
FRESH, MATURE, DEEP, AGING, DYING, DRY, RACHIS, RACHIS_DRY = range(8)
# Bark columns.
TRUNK0, TRUNK1, TRUNK2, TRUNK3, ROOT, SHEATH, COCO_GREEN, COCO_YELLOW, COCO_BROWN, STEM, \
    CROWNSHAFT, FAN_TRUNK = range(12)

LEAF_RAMPS = {
    FRESH: [(0, "3F7A24"), (0.45, "6AA834"), (1, "B4D45C")],
    MATURE: [(0, "275E22"), (0.5, "468C30"), (1, "83B646")],
    DEEP: [(0, "1F4F1E"), (0.5, "36742B"), (1, "5F9A3A")],
    AGING: [(0, "3E6E24"), (0.45, "8AA238"), (1, "D2BC5C")],
    DYING: [(0, "7E8634"), (0.5, "BC9C48"), (1, "9E6E38")],
    DRY: [(0, "A08458"), (0.5, "8C6C44"), (1, "684C30")],
    RACHIS: [(0, "B08A3C"), (0.3, "8E9A40"), (1, "4E8430")],
    RACHIS_DRY: [(0, "9C7E54"), (1, "6A5036")],
}

# Trunk bands, base to crown: (shadow under the lip, body, fresh ring, lip).
BANDS = {
    TRUNK0: ("43392F", "6E6458", "938677", "524638"),
    TRUNK1: ("4A3E32", "7C6C5A", "A08D76", "5C4C3C"),
    TRUNK2: ("503F2F", "877056", "AC9272", "64503A"),
    TRUNK3: ("564230", "917450", "B79A6C", "6C5536"),
    FAN_TRUNK: ("2E2620", "5A4C40", "7A6856", "3E342C"),
}

BARK_RAMPS = {
    ROOT: [(0, "3E3226"), (0.35, "5A4C3E"), (0.7, "6A5E50"), (1, "6E6458")],
    SHEATH: [(0, "6A4A2C"), (0.5, "9C6E3C"), (1, "C49A5C")],
    COCO_GREEN: [(0, "A6B848"), (0.45, "6E8E2A"), (0.9, "4C6A22"), (0.93, "6A5228"), (1, "5A4422")],
    COCO_YELLOW: [(0, "E8C04A"), (0.5, "D2962E"), (0.9, "9A6A28"), (0.93, "6A5228"), (1, "5A4422")],
    COCO_BROWN: [(0, "946238"), (0.5, "6E4628"), (1, "4A2E1A")],
    STEM: [(0, "4A4E3A"), (0.08, "6E7454"), (0.8, "7E8462"), (0.9, "9A9C7A"), (0.94, "3E4230"),
           (1, "3E4230")],
    CROWNSHAFT: [(0, "7E9E44"), (0.5, "98B456"), (1, "B2C46A")],
}


def rgb(h):
    return Vector(tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)))


def ramp(stops, t):
    t = min(max(t, 0.0), 1.0)
    for (p0, c0), (p1, c1) in zip(stops, stops[1:]):
        if t <= p1:
            return rgb(c0).lerp(rgb(c1), (t - p0) / max(p1 - p0, 1e-6))
    return rgb(stops[-1][1])


def fibres(seed):
    """Noise periodic in u, so a trunk's texture meets itself round the back of the trunk."""
    rng = random.Random(seed)
    waves = [(f, rng.random(), 1 / (1 + i)) for i, f in enumerate((3, 5, 8, 13, 21))]
    return lambda u: sum(a * math.sin(TAU * (f * u + p)) for f, p, a in waves) / 2.3


def leaflet_paint(stops):
    def paint(u, v):
        c = ramp(stops, v)
        vein = 1 - abs(u - 0.5) * 2
        c = c * (0.84 + 0.18 * vein)
        if abs(u - 0.5) < 0.06:
            c = c * 1.1
        return c
    return paint


def band_paint(colours, seed):
    shadow, body, fresh, lip = (rgb(h) for h in colours)
    streak = fibres(seed)

    def paint(u, v):
        if v < 0.12:
            c = shadow.lerp(body, v / 0.12)
        elif v < 0.7:
            c = body.copy()
        elif v < 0.88:
            c = body.lerp(fresh, (v - 0.7) / 0.18)
        else:
            c = lip.copy()
        return c * (1 + 0.13 * streak(u) + 0.05 * math.sin(TAU * (v * 5 + 0.3)))
    return paint


def ramp_paint(stops, seed, grain=0.08):
    streak = fibres(seed)
    return lambda u, v: ramp(stops, v) * (1 + grain * streak(u))


def write_png(path, width, height, pixel):
    """An 8-bit RGB PNG, no alpha: an alpha channel would make ArtLibrary alpha-clip the material."""
    rows = []
    for y in range(height):
        yb = height - 1 - y
        v = min(max((yb + 0.5 - 2) / (height - 4), 0.0), 1.0)
        row = bytearray(b"\x00")
        for x in range(width):
            c = pixel(x, v)
            row += bytes(int(min(max(ch, 0.0), 1.0) * 255 + 0.5) for ch in c)
        rows.append(bytes(row))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
           + chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(png)


def paint_textures(folder):
    os.makedirs(folder, exist_ok=True)

    leaf = {c: (leaflet_paint(s) if c < RACHIS else ramp_paint(s, c, 0.05)) for c, s in LEAF_RAMPS.items()}
    bark = {c: band_paint(s, 100 + c) for c, s in BANDS.items()}
    bark.update({c: ramp_paint(s, 200 + c, 0.12 if c == SHEATH else 0.05) for c, s in BARK_RAMPS.items()})

    def sheet(columns):
        def pixel(x, v):
            col = x // CELL
            u = min(max((x % CELL + 0.5 - MARGIN) / (CELL - 2 * MARGIN), 0.0), 1.0)
            return columns.get(col, columns[0])(u, v)
        return pixel

    paths = (os.path.join(folder, "PalmLeaves.png"), os.path.join(folder, "PalmBark.png"))
    write_png(paths[0], CELL * LEAF_COLUMNS, TEX_H, sheet(leaf))
    write_png(paths[1], CELL * BARK_COLUMNS, TEX_H, sheet(bark))
    return paths


def uv_on(columns, col, u, v):
    return ((col * CELL + MARGIN + u * (CELL - 2 * MARGIN)) / (CELL * columns),
            (2 + v * (TEX_H - 4)) / TEX_H)


def LUV(col, u, v):
    return uv_on(LEAF_COLUMNS, col, u, v)


def BUV(col, u, v):
    return uv_on(BARK_COLUMNS, col, u, v)


# ----------------------------------------------------------------------------------------- mesh

BARK_SLOT, LEAF_SLOT = 0, 1
MATERIALS = []


class Mesh:
    """Vertices, and faces carrying their own corner UVs, slot and shading."""

    def __init__(self):
        self.verts, self.faces = [], []

    def v(self, p):
        self.verts.append(Vector(p))
        return len(self.verts) - 1

    def f(self, idx, uvs, slot, smooth=False, facing=None):
        """A face. With `facing`, wound so its normal points that way (bark culls back faces)."""
        if facing is not None:
            a, b, c = (self.verts[i] for i in idx[:3])
            if (b - a).cross(c - a).dot(facing) < 0:
                idx, uvs = idx[::-1], uvs[::-1]
        self.faces.append((list(idx), list(uvs), slot, smooth))

    def centre(self, idx):
        return sum((self.verts[i] for i in idx), Vector()) / len(idx)

    def tris(self):
        return sum(len(f[0]) - 2 for f in self.faces)

    def build(self, name):
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata([tuple(v) for v in self.verts], [], [f[0] for f in self.faces])
        for mat in MATERIALS:
            mesh.materials.append(mat)
        layer = mesh.uv_layers.new(name="UVMap")
        for poly, (_, uvs, slot, smooth) in zip(mesh.polygons, self.faces):
            poly.material_index = slot
            poly.use_smooth = smooth
            for loop, uv in zip(poly.loop_indices, uvs):
                layer.data[loop].uv = uv
        mesh.update()
        obj = bpy.data.objects.new(name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        return obj


def loft(m, lo, hi, col, v0, v1, smooth=True, uv=BUV, slot=BARK_SLOT):
    """Quads between two rings of equal size, wound outward from the axis between them."""
    axis = (m.centre(lo) + m.centre(hi)) / 2
    n = len(lo)
    for k in range(n):
        j = (k + 1) % n
        quad = [lo[k], lo[j], hi[j], hi[k]]
        u0, u1 = k / n, (k + 1) / n
        m.f(quad, [uv(col, u0, v0), uv(col, u1, v0), uv(col, u1, v1), uv(col, u0, v1)], slot, smooth,
            facing=m.centre(quad) - axis)


def annulus(m, outer, inner, col, v0, v1, facing):
    n = len(outer)
    for k in range(n):
        j = (k + 1) % n
        m.f([outer[k], outer[j], inner[j], inner[k]],
            [BUV(col, k / n, v0), BUV(col, (k + 1) / n, v0), BUV(col, (k + 1) / n, v1), BUV(col, k / n, v1)],
            BARK_SLOT, False, facing=facing)


def cap(m, ring, apex_point, col, v0, v1, facing):
    apex = m.v(apex_point)
    n = len(ring)
    for k in range(n):
        j = (k + 1) % n
        m.f([ring[k], ring[j], apex], [BUV(col, k / n, v0), BUV(col, (k + 1) / n, v0), BUV(col, (k + 0.5) / n, v1)],
            BARK_SLOT, False, facing=facing)


def lerp(a, b, t):
    return a + (b - a) * t


# ---------------------------------------------------------------------------------------- spine

def bezier(p0, p1, p2, p3, t):
    s = 1 - t
    return p0 * s * s * s + p1 * 3 * s * s * t + p2 * 3 * s * t * t + p3 * t * t * t


class Spine:
    """A cubic Bezier walked by arc length, with frames carried along it so rings never flip."""

    def __init__(self, p0, p1, p2, p3, samples=96):
        self.pts = [bezier(p0, p1, p2, p3, i / samples) for i in range(samples + 1)]
        self.cum = [0.0]
        for a, b in zip(self.pts, self.pts[1:]):
            self.cum.append(self.cum[-1] + (b - a).length)
        self.length = self.cum[-1]

        n = len(self.pts)
        self.tans = [(self.pts[min(i + 1, n - 1)] - self.pts[max(i - 1, 0)]).normalized() for i in range(n)]
        normal = Vector((1, 0, 0))
        self.norms = []
        for t in self.tans:
            normal = (normal - t * normal.dot(t)).normalized()
            self.norms.append(normal.copy())

    def frame(self, s):
        d = min(max(s, 0.0), 1.0) * self.length
        i = min(max(bisect.bisect_left(self.cum, d), 1), len(self.cum) - 1)
        a = (d - self.cum[i - 1]) / max(self.cum[i] - self.cum[i - 1], 1e-9)
        p = self.pts[i - 1].lerp(self.pts[i], a)
        t = self.tans[i - 1].lerp(self.tans[i], a).normalized()
        n = self.norms[i - 1].lerp(self.norms[i], a)
        n = (n - t * n.dot(t)).normalized()
        if s < 0:
            p = p + t * s * self.length
        elif s > 1:
            p = p + t * (s - 1) * self.length
        return p, t, n, t.cross(n)


def ring(m, sp, s, r, sides, twist, wobble=None):
    p, t, n, b = sp.frame(s)
    out = []
    for k in range(sides):
        a = twist + k * TAU / sides
        rr = r * (wobble[k] if wobble else 1.0)
        out.append(m.v(p + (n * math.cos(a) + b * math.sin(a)) * rr))
    return out


def leaning_spine(base, height, lean, heading, bend):
    """Leans from the root and straightens toward the crown: the shape of a palm on a beach."""
    h = Vector((math.cos(heading), math.sin(heading), 0))
    side = Vector((-h.y, h.x, 0))
    return Spine(base,
                 base + h * lean * 0.38 + UP * height * 0.3,
                 base + h * lean * 0.92 + UP * height * 0.66 + side * bend,
                 base + h * lean + UP * height)


# ---------------------------------------------------------------------------------------- trunk

def trunk(m, sp, spec, rng, far, columns=(TRUNK0, TRUNK1, TRUNK2, TRUNK3), lip=True, flare=0.38,
          roots=6, root_size=1.0):
    sides = max(4, spec["sides"] - 3) if far else spec["sides"]
    band = spec["band"] * (2.6 if far else 1.0)
    count = max(3, round(sp.length / band))
    cuts = [0.0] + [(i + rng.uniform(-0.18, 0.18)) / count for i in range(1, count)] + [1.0]
    r_base, r_top = spec["r_base"], spec["r_top"]

    def radius(s):
        return lerp(r_base, r_top, s ** 0.8) * (1 + flare * math.exp(-s * sp.length / 0.45))

    twist = rng.uniform(0, TAU)
    # The mound the roots make: a buried ring and a lumpy one at the sand line, into the first band.
    skirt = ring(m, sp, -0.3 / sp.length, radius(0) * 1.12, sides, twist)
    lumps = ring(m, sp, -0.04 / sp.length, radius(0) * 1.05, sides, twist,
                 [rng.uniform(0.9, 1.1) for _ in range(sides)])
    loft(m, skirt, lumps, ROOT, 0.0, 0.5)

    for i in range(count):
        s0, s1 = cuts[i], cuts[i + 1]
        col = columns[min(len(columns) - 1, max(0, int(s0 * len(columns) + rng.uniform(-0.3, 0.3))))]
        wob = [rng.uniform(0.94, 1.06) for _ in range(sides)]
        r0, r1 = radius(s0), radius(s1)

        if far or not lip:
            a = ring(m, sp, s0, r0 * 0.97, sides, twist, wob)
            c = ring(m, sp, s1, r1 * (1.05 if lip else 1.0), sides, twist, wob)
            loft(m, a, c, col, 0.04, 0.97)
        else:
            a = ring(m, sp, s0, r0 * 0.92, sides, twist, wob)
            b = ring(m, sp, lerp(s0, s1, 0.76), lerp(r0, r1, 0.76), sides, twist, wob)
            c = ring(m, sp, s1, r1 * 1.1, sides, twist, wob)
            loft(m, a, b, col, 0.0, 0.72)
            loft(m, b, c, col, 0.72, 0.99)

        if i == 0:
            loft(m, lumps, a, ROOT, 0.5, 1.0)

        _, t, _, _ = sp.frame(s1)
        if i < count - 1:
            if lip and not far:
                inner = ring(m, sp, s1, radius(s1) * 0.8, sides, twist)
                annulus(m, c, inner, col, 0.99, 0.93, t)
        else:
            cap(m, c, sp.frame(s1)[0] + t * 0.05, col, 0.99, 0.9, t)

        twist += rng.uniform(0.3, 1.2)

    if far:
        return
    for k in range(roots):
        root(m, sp, radius, rng, k / max(roots, 1) * TAU + rng.uniform(-0.4, 0.4), root_size)


def root(m, sp, radius, rng, angle, size):
    """A rootlet out of the mound: short, thin, blunt, going into the sand at a slant."""
    p, t, n, b = sp.frame(0.0)
    out = (n * math.cos(angle) + b * math.sin(angle))
    out.z = 0
    out.normalize()
    start = p + out * radius(0) * 0.98 + UP * rng.uniform(-0.04, 0.06) * size
    dive = math.radians(rng.uniform(42, 64))
    d = out * math.cos(dive) - UP * math.sin(dive)
    length = rng.uniform(0.12, 0.24) * size
    r = rng.uniform(0.03, 0.05) * size
    across = out.cross(UP).normalized()
    lift = d.cross(across).normalized()

    def square(c, rr):
        return [m.v(c + (across * math.cos(a) + lift * math.sin(a)) * rr) for a in (0.4, 2.0, 3.5, 5.1)]

    a, e = square(start - d * 0.05, r), square(start + d * length, r * 0.7)
    loft(m, a, e, ROOT, 0.8, 0.45, smooth=True)
    cap(m, e, start + d * (length + r * 0.6), ROOT, 0.45, 0.35, d)


# ---------------------------------------------------------------------------------------- crown

class Frond:
    def __init__(self, **kw):
        self.__dict__.update(kw)


def plan_crown(spec, rng):
    """Every frond of a crown, youngest first. Decided once, built twice (near and far)."""
    fronds = []
    n = spec["fronds"]
    az0 = rng.uniform(0, TAU)
    length = spec["frond_len"]
    columns = spec.get("leaf_columns", (FRESH, MATURE, DEEP, AGING))

    for i in range(spec.get("spears", 2)):
        fronds.append(Frond(
            az=az0 + i * 2.4 + rng.uniform(-0.3, 0.3), elev=math.radians(rng.uniform(76, 86)),
            droop=math.radians(rng.uniform(6, 16)), length=length * rng.uniform(0.42, 0.58), attach=0.72,
            col=FRESH, rachis=RACHIS, alpha=(14, 8), beta=-4, sag=0.05, leaf=0.5, width=0.07, skip=0.0,
            per_side=spec["per_side"] * 0.7, roll=rng.uniform(-0.2, 0.2), age=0.0))

    for i in range(n):
        age = i / max(n - 1, 1)
        if age < 0.22:
            col = columns[0]
        elif age < 0.62:
            col = columns[1] if rng.random() < 0.65 else columns[2]
        elif age < 0.86:
            col = columns[2] if rng.random() < 0.5 else columns[1]
        else:
            col = columns[3] if rng.random() < 0.7 else DYING
        fronds.append(Frond(
            az=az0 + (i + 2) * GOLDEN + rng.uniform(-0.12, 0.12),
            elev=math.radians(lerp(spec.get("elev_young", 58), spec.get("elev_old", -10), age) + rng.uniform(-7, 7)),
            droop=math.radians(lerp(40, 92, age) * spec.get("droop", 1.0) * rng.uniform(0.85, 1.15)),
            length=length * lerp(0.82, 1.04, min(age * 1.6, 1.0)) * rng.uniform(0.92, 1.08),
            attach=lerp(0.62, 0.12, age), col=col, rachis=RACHIS if col != DYING else RACHIS_DRY,
            alpha=(lerp(64, 72, age), 38), beta=lerp(6, 22, age) + rng.uniform(-4, 4),
            sag=lerp(0.06, 0.12, age), leaf=1.0, width=spec.get("leaf_width", 0.15),
            skip=0.03 + 0.06 * age, per_side=spec["per_side"], roll=rng.uniform(-0.25, 0.25), age=age))

    for i in range(spec.get("dead", 1)):
        fronds.append(Frond(
            az=az0 + rng.uniform(0, TAU), elev=math.radians(rng.uniform(-82, -62)),
            droop=math.radians(rng.uniform(4, 14)), length=length * rng.uniform(0.6, 0.8), attach=0.0,
            col=DRY, rachis=RACHIS_DRY, alpha=(40, 20), beta=55, sag=0.1, leaf=0.75, width=0.05,
            skip=0.3, per_side=spec["per_side"] * 0.8, roll=rng.uniform(-0.6, 0.6), age=1.2))
    return fronds


def frond_spine(f, base, steps):
    h = Vector((math.cos(f.az), math.sin(f.az), 0))
    pts, dirs = [], []
    p = base.copy()
    for k in range(steps + 1):
        x = k / steps
        el = f.elev - f.droop * x ** 1.5
        d = (h * math.cos(el) + UP * math.sin(el)).normalized()
        pts.append(p.copy())
        dirs.append(d)
        p = p + d * (f.length / steps)
    side0 = h.cross(UP).normalized()
    return pts, dirs, side0


def along(pts, dirs, x):
    fx = x * (len(pts) - 1)
    i = min(int(fx), len(pts) - 2)
    a = fx - i
    return pts[i].lerp(pts[i + 1], a), dirs[i].lerp(dirs[i + 1], a).normalized()


def frame_at(d, side0, roll):
    up = side0.cross(d).normalized()
    s = (side0 * math.cos(roll) + up * math.sin(roll)).normalized()
    return s, s.cross(d).normalized()


def build_frond(m, f, base, rng, far, scale=1.0):
    steps = 5 if far else 8
    pts, dirs, side0 = frond_spine(f, base, steps)
    x0 = 0.2
    leaf_len = f.length * 0.28 * f.leaf
    thick = 0.05 * scale * (0.6 + 0.4 * f.length / 4.0)

    if not far:
        prev = None
        for k in range(steps + 1):
            x = k / steps
            p, d = pts[k], dirs[k]
            s, u = frame_at(d, side0, f.roll * x)
            th = thick * lerp(1.3, 0.15, x)
            cur = [m.v(p + u * th * 0.55), m.v(p - u * th * 0.35 + s * th * 0.6),
                   m.v(p - u * th * 0.35 - s * th * 0.6)]
            if prev:
                for a in range(3):
                    b = (a + 1) % 3
                    m.f([prev[a], prev[b], cur[b], cur[a]],
                        [LUV(f.rachis, a / 3, x - 1 / steps), LUV(f.rachis, (a + 1) / 3, x - 1 / steps),
                         LUV(f.rachis, (a + 1) / 3, x), LUV(f.rachis, a / 3, x)], LEAF_SLOT)
            prev = cur

    leaf_rng = random.Random(rng.random())
    per_side = max(4, int(round(f.per_side if not far else 7 * f.per_side / 22)))
    for side in (1, -1):
        for j in range(per_side):
            xa = x0 + (1 - x0) * j / per_side
            xb = x0 + (1 - x0) * (j + 1) / per_side
            x = (xa + xb) / 2 if far else min(0.985, x0 + (1 - x0) * (j + leaf_rng.uniform(0.3, 0.7)) / per_side)
            if not far and leaf_rng.random() < f.skip:
                continue
            xn = (x - x0) / (1 - x0)
            p, d = along(pts, dirs, x)
            s, u = frame_at(d, side0, f.roll * x)
            ell = leaf_len * math.sin(math.pi * (0.12 + 0.86 * xn)) ** 0.7 * (1.08 - 0.4 * xn)
            ell *= leaf_rng.uniform(0.88, 1.1)
            if not far and leaf_rng.random() < 0.04:
                ell *= 0.5
            alpha = math.radians(lerp(f.alpha[0], f.alpha[1], xn) + leaf_rng.uniform(-6, 6))
            beta = math.radians(f.beta + leaf_rng.uniform(-6, 6))
            dl = (d * math.cos(alpha) + s * side * math.sin(alpha)).normalized()
            dl = (dl * math.cos(beta) - u * math.sin(beta)).normalized()
            sag = f.sag * leaf_rng.uniform(0.8, 1.2)

            vb, vt = 0.06 + 0.38 * xn, 0.56 + 0.44 * xn
            if far:
                pa, _ = along(pts, dirs, xa)
                pb, _ = along(pts, dirs, xb)
                tip = p + dl * ell - UP * ell * sag * 1.4
                m.f([m.v(pa), m.v(pb), m.v(tip)],
                    [LUV(f.col, 0.5, vb), LUV(f.col, 0.5, vb), LUV(f.col, 0.0, vt)], LEAF_SLOT)
                continue

            w = ell * f.width
            width_dir = (d - dl * d.dot(dl)).normalized()
            normal = dl.cross(width_dir)
            if normal.dot(u) < 0:
                normal = -normal
            b = p + s * side * thick * 0.3
            mid = b + dl * ell * 0.5 - UP * ell * sag * 0.55
            tip = b + dl * ell - UP * ell * sag * 1.5
            e = b + dl * ell * 0.34 - UP * ell * sag * 0.25 + normal * w * 0.32
            e1, e2 = e + width_dir * w * 0.5, e - width_dir * w * 0.5
            ib, im, it, i1, i2 = (m.v(q) for q in (b, mid, tip, e1, e2))
            ve, vm = lerp(vb, vt, 0.34), lerp(vb, vt, 0.5)
            c = f.col
            m.f([ib, im, i1], [LUV(c, 0.5, vb), LUV(c, 0.5, vm), LUV(c, 0.0, ve)], LEAF_SLOT)
            m.f([im, it, i1], [LUV(c, 0.5, vm), LUV(c, 0.5, vt), LUV(c, 0.0, ve)], LEAF_SLOT)
            m.f([ib, i2, im], [LUV(c, 0.5, vb), LUV(c, 1.0, ve), LUV(c, 0.5, vm)], LEAF_SLOT)
            m.f([im, i2, it], [LUV(c, 0.5, vm), LUV(c, 1.0, ve), LUV(c, 0.5, vt)], LEAF_SLOT)


def bulb(m, sp, r, rng, far, col=SHEATH, height=1.0):
    """The crown's boot of old frond bases, where the trunk ends and the fronds begin."""
    p, t, n, b = sp.frame(1.0)
    sides = 5 if far else 8
    twist = rng.uniform(0, TAU)
    rings = []
    for off, rr in ((-0.12, 1.05), (0.22, 1.42), (0.5, 1.28), (0.74, 0.78)):
        c = p + t * off * height
        rings.append([m.v(c + (n * math.cos(twist + k * TAU / sides) + b * math.sin(twist + k * TAU / sides))
                          * r * rr * rng.uniform(0.92, 1.08)) for k in range(sides)])
    vs = (0.0, 0.4, 0.75, 1.0)
    for i in range(3):
        loft(m, rings[i], rings[i + 1], col, vs[i], vs[i + 1], smooth=False)
    cap(m, rings[3], p + t * 0.9 * height, col, 1.0, 0.9, t)
    return p, t


def coconuts(m, top, t, r, rng, count, colours):
    """Bunches tucked under the fronds: each nut a squashed sphere, long axis out and down."""
    placed = 0
    bunch = 0
    while placed < count:
        az = rng.uniform(0, TAU)
        h = Vector((math.cos(az), math.sin(az), 0))
        anchor = top + t * rng.uniform(0.05, 0.25) + h * r * 1.35
        col = colours[bunch % len(colours)]
        for _ in range(min(count - placed, rng.choice((2, 3, 3, 4)))):
            offset = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 0.3))) * 0.13
            centre = anchor + h * 0.12 + offset
            axis = (centre - (top + t * 0.4)).normalized()
            nut(m, centre, axis, rng.uniform(0.12, 0.15), col)
            placed += 1
        bunch += 1


def nut(m, centre, axis, r, col, seg=8, rings=4):
    side = axis.cross(UP)
    side = side.normalized() if side.length > 1e-4 else Vector((1, 0, 0))
    other = axis.cross(side).normalized()
    pole_out = m.v(centre + axis * r * 1.2)
    pole_in = m.v(centre - axis * r * 1.0)
    lat = []
    for i in range(1, rings + 1):
        phi = math.pi * i / (rings + 1)
        z = math.cos(phi) * (1.2 if math.cos(phi) > 0 else 1.0)
        rr = math.sin(phi)
        lat.append([m.v(centre + axis * z * r + (side * math.cos(a) + other * math.sin(a)) * rr * r
                        * (1 + 0.08 * math.cos(3 * a)))
                    for a in (k * TAU / seg for k in range(seg))])
    for k in range(seg):
        j = (k + 1) % seg
        q = [lat[0][k], lat[0][j], pole_out]
        m.f(q, [BUV(col, k / seg, 0.15), BUV(col, (k + 1) / seg, 0.15), BUV(col, k / seg, 0.0)], BARK_SLOT, True,
            facing=m.centre(q) - centre)
        q = [lat[-1][j], lat[-1][k], pole_in]
        m.f(q, [BUV(col, (k + 1) / seg, 0.85), BUV(col, k / seg, 0.85), BUV(col, k / seg, 1.0)], BARK_SLOT, True,
            facing=m.centre(q) - centre)
        for i in range(rings - 1):
            q = [lat[i][k], lat[i][j], lat[i + 1][j], lat[i + 1][k]]
            v0, v1 = 0.15 + 0.7 * i / (rings - 1), 0.15 + 0.7 * (i + 1) / (rings - 1)
            m.f(q, [BUV(col, k / seg, v0), BUV(col, (k + 1) / seg, v0), BUV(col, (k + 1) / seg, v1),
                    BUV(col, k / seg, v1)], BARK_SLOT, True, facing=m.centre(q) - centre)


# --------------------------------------------------------------------------------------- palms

def coconut_palm(m, spec, rng, far):
    for t_spec in spec["trunks"]:
        sp = leaning_spine(Vector(t_spec.get("base", (0, 0, 0))), t_spec["height"], t_spec["lean"],
                           t_spec.get("heading", 0.0), t_spec.get("bend", 0.0))
        trunk_spec = dict(spec, **t_spec)
        trunk(m, sp, trunk_spec, random.Random(rng.random()), far, roots=spec.get("roots", 11))
        crown_rng = random.Random(rng.random())
        top, t = bulb(m, sp, trunk_spec["r_top"], crown_rng, far)
        crown = dict(spec, **t_spec.get("crown", {}))
        plan = plan_crown(crown, crown_rng)
        frond_rng = random.Random(crown_rng.random())
        for f in plan:
            base = top + t * f.attach + Vector((math.cos(f.az), math.sin(f.az), 0)) * trunk_spec["r_top"] * 1.05
            build_frond(m, f, base, random.Random(frond_rng.random()), far)
        if not far and crown.get("coconuts", 0):
            coconuts(m, top, t, trunk_spec["r_top"], random.Random(crown_rng.random()), crown["coconuts"],
                     crown.get("nut_colours", (COCO_GREEN, COCO_YELLOW, COCO_GREEN)))


def fan_leaf(m, centre, d, rng, far, radius, col, spread, droop, pleat=0.07):
    """A costapalmate fan: pleated ribs out of the hastula, the outer third split into hanging tips."""
    side = d.cross(UP)
    side = side.normalized() if side.length > 1e-3 else Vector((1, 0, 0))
    up = side.cross(d).normalized()
    segs = 8 if far else 22
    spread = math.radians(spread)

    def point(theta, rho, lift):
        radial = d * math.cos(theta) + side * math.sin(theta)
        cupped = radial + up * 0.28 * abs(math.sin(theta))
        q = centre + cupped.normalized() * rho * radius + up * lift * radius
        return q - UP * droop * radius * rho ** 2

    c = m.v(centre)
    inner = 0.62
    ridge = []
    for j in range(segs + 1):
        theta = -spread / 2 + spread * j / segs
        up_fold = (j % 2 == 0)
        lift = (pleat if up_fold else -pleat) * (0 if far else 1)
        ridge.append((theta, m.v(point(theta, inner, lift)), up_fold))

    for j in range(segs):
        (ta, ia, fa), (tb, ib, fb) = ridge[j], ridge[j + 1]
        ua, ub = (0.5, 1.0) if fa else (0.0, 0.5)
        m.f([c, ia, ib], [LUV(col, 0.5, 0.02), LUV(col, ua, 0.55), LUV(col, ub, 0.55)], LEAF_SLOT)
        inset = (tb - ta) * 0.12
        tm = (ta + tb) / 2
        if far:
            tip = m.v(point(tm, 1.02, 0) - UP * droop * radius * 0.25)
            m.f([ia, ib, tip], [LUV(col, ua, 0.55), LUV(col, ub, 0.55), LUV(col, 0.5, 1.0)], LEAF_SLOT)
            continue
        hang = rng.uniform(0.7, 1.3)
        la = m.v(point(ta + inset, 0.86, 0) - UP * droop * radius * 0.12 * hang)
        lb = m.v(point(tb - inset, 0.86, 0) - UP * droop * radius * 0.12 * hang)
        tip = m.v(point(tm, rng.uniform(1.0, 1.12), 0) - UP * droop * radius * 0.45 * hang)
        m.f([ia, ib, lb, la], [LUV(col, ua, 0.55), LUV(col, ub, 0.55), LUV(col, ub, 0.8), LUV(col, ua, 0.8)],
            LEAF_SLOT)
        m.f([la, lb, tip], [LUV(col, ua, 0.8), LUV(col, ub, 0.8), LUV(col, 0.5, 1.0)], LEAF_SLOT)


def petiole(m, pts, col, thick, far):
    if far:
        return
    prev = None
    for k, p in enumerate(pts):
        x = k / (len(pts) - 1)
        d = (pts[min(k + 1, len(pts) - 1)] - pts[max(k - 1, 0)]).normalized()
        s = d.cross(UP)
        s = s.normalized() if s.length > 1e-3 else Vector((1, 0, 0))
        u = s.cross(d).normalized()
        th = thick * lerp(1.0, 0.5, x)
        cur = [m.v(p + u * th * 0.55), m.v(p - u * th * 0.35 + s * th * 0.6), m.v(p - u * th * 0.35 - s * th * 0.6)]
        if prev:
            for a in range(3):
                b = (a + 1) % 3
                m.f([prev[a], prev[b], cur[b], cur[a]],
                    [LUV(col, a / 3, x), LUV(col, (a + 1) / 3, x), LUV(col, (a + 1) / 3, x), LUV(col, a / 3, x)],
                    LEAF_SLOT)
        prev = cur


def fan_palm(m, spec, rng, far):
    sp = leaning_spine(Vector((0, 0, 0)), spec["height"], spec["lean"], 0.0, spec.get("bend", 0.0))
    trunk(m, sp, spec, random.Random(rng.random()), far, columns=(FAN_TRUNK,), flare=0.4, roots=9, root_size=0.9)

    stub_rng = random.Random(rng.random())
    if not far:
        # The old leaf bases left on the trunk, crossed like basketwork: the fan palm's tell.
        for i in range(spec["stubs"]):
            s = lerp(0.35, 0.96, i / spec["stubs"]) + stub_rng.uniform(-0.02, 0.02)
            p, t, n, b = sp.frame(s)
            a = stub_rng.uniform(0, TAU)
            out = n * math.cos(a) + b * math.sin(a)
            r = lerp(spec["r_base"], spec["r_top"], s ** 0.8)
            across = out.cross(t).normalized()
            base = p + out * r * 0.85
            ring3 = [m.v(base + t * 0.06), m.v(base - t * 0.04 + across * 0.06), m.v(base - t * 0.04 - across * 0.06)]
            cap(m, ring3, base + out * 0.2 + t * 0.14, SHEATH, 0.2, 0.9, out)

    crown_rng = random.Random(rng.random())
    top, t = bulb(m, sp, spec["r_top"], crown_rng, far, col=FAN_TRUNK, height=0.7)
    az0 = crown_rng.uniform(0, TAU)
    n = spec["leaves"]
    for i in range(n + spec["dead"]):
        dead = i >= n
        age = i / max(n - 1, 1) if not dead else 1.2
        az = az0 + i * GOLDEN + crown_rng.uniform(-0.1, 0.1)
        h = Vector((math.cos(az), math.sin(az), 0))
        elev = math.radians(crown_rng.uniform(-84, -66) if dead else lerp(68, -18, age) + crown_rng.uniform(-6, 6))
        length = (0.7 if dead else lerp(1.1, 1.7, age)) * spec["petiole"]
        base = top + t * (0.0 if dead else lerp(0.5, 0.1, age)) + h * spec["r_top"]
        pts, p = [], base.copy()
        for k in range(5):
            el = elev - math.radians(12) * k / 4
            pts.append(p.copy())
            p = p + (h * math.cos(el) + UP * math.sin(el)) * length / 4
        petiole(m, pts, RACHIS_DRY if dead else RACHIS, 0.06, far)
        d = (pts[-1] - pts[-2]).normalized()
        if dead:
            col = DRY
        elif age < 0.15:
            col = FRESH
        elif age < 0.8:
            col = DEEP if crown_rng.random() < 0.5 else MATURE
        else:
            col = AGING
        fan_leaf(m, pts[-1], d, crown_rng, far, spec["fan"] * (0.75 if dead else crown_rng.uniform(0.85, 1.1)),
                 col, 120 if dead else crown_rng.uniform(190, 230), 0.15 if dead else lerp(0.2, 0.45, age),
                 pleat=0.12 if dead else 0.07)


def clump_palm(m, spec, rng, far):
    stems = spec["stems"]
    for i, (height, lean) in enumerate(stems):
        a = i / len(stems) * TAU + rng.uniform(-0.4, 0.4)
        base = Vector((math.cos(a), math.sin(a), 0)) * rng.uniform(0.12, 0.3)
        sp = leaning_spine(base, height, lean, a + rng.uniform(-0.3, 0.3), rng.uniform(-0.2, 0.2))
        stem_spec = dict(spec, r_base=spec["r_base"] * height / 5, r_top=spec["r_top"] * height / 5)
        trunk(m, sp, stem_spec, random.Random(rng.random()), far, columns=(STEM,), lip=False, flare=0.25,
              roots=0)
        crown_rng = random.Random(rng.random())
        top, t = bulb(m, sp, stem_spec["r_top"] * 0.9, crown_rng, far, col=CROWNSHAFT, height=0.8)
        crown = dict(spec, fronds=spec["fronds_per_stem"] if height > 3 else 5)
        plan = plan_crown(crown, crown_rng)
        frond_rng = random.Random(crown_rng.random())
        for f in plan:
            base = top + t * f.attach * 0.7 + Vector((math.cos(f.az), math.sin(f.az), 0)) * stem_spec["r_top"]
            build_frond(m, f, base, random.Random(frond_rng.random()), far, scale=0.5)


# Each variant: name, builder, seed, shape. Heights are the model's own; ArtLibrary scales each to
# its catalogue size.
COCONUT = dict(sides=8, band=0.46, r_base=0.3, r_top=0.19, per_side=25, frond_len=4.2, fronds=16,
               spears=2, dead=2, coconuts=6)

VARIANTS = [
    ("Palm_Straight", coconut_palm, 11, dict(COCONUT, trunks=[dict(height=8.0, lean=0.9, bend=0.25)])),
    ("Palm_Bend", coconut_palm, 12, dict(COCONUT, coconuts=5, trunks=[dict(height=7.0, lean=2.9, bend=-0.3)])),
    ("Palm_Tall", coconut_palm, 13, dict(COCONUT, r_base=0.27, r_top=0.16, fronds=15, frond_len=4.0, coconuts=7,
                                         trunks=[dict(height=9.8, lean=1.6, bend=0.5)])),
    ("Palm_Lean", coconut_palm, 14, dict(COCONUT, fronds=15, coconuts=4, droop=1.1,
                                         trunks=[dict(height=5.8, lean=3.3, bend=0.2)])),
    ("Palm_Twin", coconut_palm, 15, dict(COCONUT, fronds=10, per_side=18, spears=1, dead=1, coconuts=3,
                                         frond_len=3.8, sides=7, roots=8,
                                         trunks=[dict(height=7.6, lean=1.7, heading=0.0, bend=0.2,
                                                      base=(0.1, 0, 0)),
                                                 dict(height=6.0, lean=2.0, heading=2.6, bend=-0.2,
                                                      base=(-0.1, 0.05, 0))])),
    ("Palm_Young", coconut_palm, 16, dict(COCONUT, r_base=0.3, r_top=0.24, fronds=13, per_side=20, frond_len=2.8,
                                          dead=1, coconuts=0, elev_young=72, elev_old=18, droop=0.8, band=0.36,
                                          leaf_columns=(FRESH, FRESH, MATURE, AGING),
                                          trunks=[dict(height=1.5, lean=0.35)])),
    ("Palm_Old", coconut_palm, 17, dict(COCONUT, r_base=0.26, r_top=0.15, fronds=12, per_side=20, frond_len=3.6,
                                        dead=4, coconuts=5, nut_colours=(COCO_BROWN, COCO_YELLOW),
                                        leaf_columns=(MATURE, DEEP, AGING, DYING),
                                        trunks=[dict(height=10.4, lean=1.2, bend=-0.6)])),
    ("Palm_Fan", fan_palm, 18, dict(sides=8, band=0.4, r_base=0.24, r_top=0.19, height=6.2, lean=0.5, bend=0.15,
                                    leaves=19, dead=6, petiole=1.25, fan=1.25, stubs=34)),
    ("Palm_Clump", clump_palm, 19, dict(sides=6, band=0.3, r_base=0.11, r_top=0.075, per_side=12, frond_len=2.7,
                                        fronds_per_stem=7, spears=1, dead=0, elev_young=72, elev_old=4, droop=1.1,
                                        leaf_width=0.16, leaf_columns=(FRESH, MATURE, DEEP, AGING),
                                        stems=[(5.6, 0.9), (4.8, 1.3), (4.0, 1.0), (3.0, 0.8), (2.1, 0.6)])),
]


# --------------------------------------------------------------------------------------- driver

def materials(textures):
    MATERIALS.clear()
    for name, path in (("PalmBark", textures[1]), ("PalmLeaves", textures[0])):
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        mat.use_backface_culling = name == "PalmBark"
        nodes = mat.node_tree.nodes
        bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        bsdf.inputs["Roughness"].default_value = 0.9
        image = bpy.data.images.load(path, check_existing=False)
        image.name = name
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = image
        mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        MATERIALS.append(mat)


def clear():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.images):
        for item in list(block):
            block.remove(item)


def build(name, maker, seed, spec):
    built = []
    for far in (False, True):
        m = Mesh()
        maker(m, spec, random.Random(seed), far)
        obj = m.build(name + ("_Far" if far else ""))
        xs = [v.x for v in m.verts]
        ys = [v.y for v in m.verts]
        zs = [v.z for v in m.verts]
        wide = max(max(xs) - min(xs), max(ys) - min(ys))
        tall = max(zs) - min(zs)
        print(f"[palms] {obj.name}: {m.tris()} tris, {wide:.1f} m across, {tall:.1f} m tall"
              + ("" if tall >= 0.8 * wide else "  LIES DOWN for ArtVisual.Standing"))
        built.append(obj)
    return built


def main(out_dir=None, only=None):
    clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "palms")
    materials(paint_textures(folder))

    objects = []
    for i, (name, maker, seed, spec) in enumerate(VARIANTS):
        if only and name not in only:
            continue
        near, far = build(name, maker, seed, spec)
        objects.append((near, far))
        if out_dir is None:
            near.location.x = far.location.x = len(objects) * 10.0 - 10.0
            far.location.y = 14.0

    if out_dir is None:
        return objects

    for near, far in objects:
        bpy.ops.object.select_all(action="DESELECT")
        near.select_set(True)
        far.select_set(True)
        bpy.context.view_layer.objects.active = near
        bpy.ops.export_scene.fbx(
            filepath=os.path.join(out_dir, near.name + ".fbx"),
            use_selection=True,
            apply_unit_scale=True,
            apply_scale_options="FBX_SCALE_UNITS",
            axis_forward="-Z",
            axis_up="Y",
            mesh_smooth_type="FACE",
            bake_space_transform=True,
            path_mode="RELATIVE",
        )
    print(f"[palms] exported {len(objects)} to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
