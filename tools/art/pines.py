"""Highland pines, modelled from code (#248).

    blender -b --factory-startup -P tools/art/pines.py -- <absolute path>/Assets/_Project/Art/Models/Pines

Same contract as the other packs: one FBX per variant with `<name>` and `<name>_Far`, two ramp
textures (Textures/PineBark.png, Textures/PineLeaves.png) on two materials. No folder: a preview row.

A pine is a tapering trunk and whorls of branches up it. Each branch carries a spray - plants.strap
with a serrated edge, so one strip of triangles reads as a bough of needle tufts - and every other
spray has a second, shorter one laid on top a little higher. Inside each whorl a dark cone fills the
gaps you would otherwise see the sky through. Normals come off the trunk axis, tilted up, so the
whole tree shades as one cone under the toon terminator.

The far mesh is the same whorls with fewer, plainer sprays and no layer on top.
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
import plants as PL  # noqa: E402

TAU, UP, GOLDEN, lerp = P.TAU, P.UP, P.GOLDEN, P.lerp

# ------------------------------------------------------------------------------------- textures

BARK_COLUMNS = 4
LEAF_COLUMNS = 8
BARK, BARK_GREY, DEAD = range(3)
NEEDLE_DARK, NEEDLE, NEEDLE_LIGHT, NEEDLE_BLUE, NEEDLE_DRY, CONE_DARK, CONE_BLUE = range(7)

BARK_RAMPS = {
    BARK: [(0, "2E1E16"), (0.5, "5A3826"), (1, "7A5036")],
    BARK_GREY: [(0, "2C2824"), (0.5, "4E4740"), (1, "6A625A")],
    DEAD: [(0, "4A4038"), (0.5, "7A7064"), (1, "A09484")],
}
LEAF_RAMPS = {
    NEEDLE_DARK: [(0, "0E2218"), (0.5, "1C3E26"), (1, "2E5A32")],
    NEEDLE: [(0, "142C1C"), (0.5, "28502E"), (1, "467840")],
    NEEDLE_LIGHT: [(0, "1C3820"), (0.5, "3A6A34"), (1, "6A9A4A")],
    NEEDLE_BLUE: [(0, "10261E"), (0.5, "2A4E40"), (1, "4E7A64")],
    NEEDLE_DRY: [(0, "3A2A14"), (0.5, "7A5424"), (1, "A87A3A")],
    CONE_DARK: [(0, "0A1A12"), (1, "1E3A24")],
    CONE_BLUE: [(0, "0A1A16"), (1, "1E3830")],
}


def needles(stops):
    """Tufts: short strokes slanting out from the midrib, darker in the gaps between them."""
    def paint(u, v):
        c = P.ramp(stops, v)
        d = abs(u - 0.5) * 2
        stroke = math.sin((v * 22 - d * 3.5) * TAU)
        c = c * (0.8 + 0.22 * (stroke > -0.2)) * (0.9 + 0.12 * (1 - d))
        if d < 0.06:
            c = c * 0.8
        return c
    return paint


def plates(stops, seed):
    """Pine bark: vertical plates split by dark fissures."""
    streak = P.fibres(seed)
    rng = random.Random(seed)
    breaks = [rng.random() for _ in range(9)]

    def paint(u, v):
        c = P.ramp(stops, 0.3 + 0.7 * (0.5 + 0.5 * streak(u)))
        if abs(math.sin(u * TAU * 3 + 0.4 * math.sin(v * 9))) < 0.16:
            c = c * 0.55
        if min(abs(v - b) for b in breaks) < 0.004:
            c = c * 0.85
        return c
    return paint


def paint_textures(folder):
    os.makedirs(folder, exist_ok=True)
    bark = {c: plates(s, 800 + c) for c, s in BARK_RAMPS.items()}
    leaf = {c: (P.ramp_paint(s, 900 + c, 0.04) if c >= CONE_DARK else needles(s)) for c, s in LEAF_RAMPS.items()}

    def sheet(columns):
        def pixel(x, v):
            u = min(max((x % P.CELL + 0.5 - P.MARGIN) / (P.CELL - 2 * P.MARGIN), 0.0), 1.0)
            return columns.get(x // P.CELL, columns[0])(u, v)
        return pixel

    paths = (os.path.join(folder, "PineBark.png"), os.path.join(folder, "PineLeaves.png"))
    P.write_png(paths[0], P.CELL * BARK_COLUMNS, P.TEX_H, sheet(bark))
    P.write_png(paths[1], P.CELL * LEAF_COLUMNS, P.TEX_H, sheet(leaf))
    return paths


def BUV(col, u, v):
    return P.uv_on(BARK_COLUMNS, col, u, v)


def FUV(col, u, v):
    return P.uv_on(LEAF_COLUMNS, col, u, v)


WOOD, LEAVES = 0, 1
T.BUV, T.BARK = BUV, WOOD   # trees.tube reads these at call time

# ---------------------------------------------------------------------------------------- parts


def cone_normal(axis_at):
    """Out of the trunk and up: a pine is lit as one cone."""
    def nfn(p, leafn):
        a = axis_at(p.z)
        out = Vector((p.x - a.x, p.y - a.y, 0))
        out = out.normalized() if out.length > 1e-4 else Vector((1, 0, 0))
        return (out * 0.65 + UP * 0.45 + leafn * 0.2).normalized()
    return nfn


def spray(m, base, h, e, L, W, droop, col, nfn, far, rng):
    """A bough: a serrated strip out along a sagging arc, wide in the middle, a point at the end."""
    n = 4 if far else 7
    pts = PL.arc(base, PL.heading(h, e), L, droop, n)
    widths = []
    for i in range(n):
        t = i / (n - 1)
        w = W * math.sin(math.pi * min(1.0, 0.12 + t * 0.95)) ** 0.7
        if not far and 0 < i < n - 1 and i % 2:
            w *= rng.uniform(0.55, 0.7)
        widths.append(w)
    widths[-1] = 0.0
    PL.strap(m, pts, widths, col, nfn, fold=0.32, cols=0 if far else 1, v0=0.1, v1=1.0, uv=FUV, slot=LEAVES)


def cone(m, c, r, h, col, nfn, sides):
    """A dark skirt inside a whorl, apex up: the shade between the boughs."""
    apex = m.v(c + UP * h)
    ring = [m.v(c + Vector((math.cos(k * TAU / sides), math.sin(k * TAU / sides), 0)) * r) for k in range(sides)]
    for k in range(sides):
        j = (k + 1) % sides
        q = [ring[k], ring[j], apex]
        pts = [m.verts[i] for i in q]
        m.f(q, [FUV(col, 0, 0.1), FUV(col, 1, 0.1), FUV(col, 0.5, 0.9)], LEAVES, [nfn(p, UP) for p in pts],
            facing=(pts[0] + pts[1]) / 2 - c)


# ---------------------------------------------------------------------------------------- trees

def pine(m, spec, rng, far):
    H = spec["height"]
    lean = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0)) * spec.get("lean", 0.03)
    bend = spec.get("bend", 0.0)

    def axis_at(z):
        t = z / H
        return lean * z + Vector((bend * t * t * H, 0, 0)) + UP * z

    nfn = cone_normal(axis_at)
    n = 5 if far else 8
    pts = [axis_at(H * 1.02 * i / (n - 1)) for i in range(n)]
    r0 = spec["trunk"]
    T.tube(m, pts, [r0 * (1.25 if i == 0 else 1.0) * (1 - i / (n - 1)) ** 0.85 + 0.01 for i in range(n)],
           5 if far else 7, spec.get("bark", BARK), 0.0, 1.0, tip=False)

    whorls = spec["whorls"]
    start = spec["clear"] * H
    for k in range(whorls):
        r = random.Random(rng.random())
        t = k / (whorls - 1)
        z = lerp(start, H * 0.96, t ** spec.get("crowd", 1.0)) + r.uniform(-0.1, 0.1)
        reach = spec["reach"] * ((1 - t) ** spec.get("taper", 0.9)) + 0.35
        c = axis_at(z)
        count = spec["boughs"] if not far else max(3, spec["boughs"] - 2)
        count = max(3, int(count * lerp(1.0, 0.55, t)))
        col = r.choice(spec["cols"])
        dead = r.random() < spec.get("dead", 0.0)
        if dead:
            col = NEEDLE_DRY
        if spec.get("gaps", 0.0) and r.random() < spec["gaps"] and 0 < k < whorls - 2:
            if not far:
                for b in range(3):
                    h = r.uniform(0, TAU)
                    T.tube(m, [c, c + PL.heading(h, math.radians(-15)) * reach * 0.7], [0.04, 0.012], 3, DEAD,
                           tip=True)
            continue
        if not far or k % 2 == 0:
            cone(m, c - UP * reach * 0.42, reach * 0.62, reach * 0.62, spec.get("shade", CONE_DARK), nfn,
                 5 if far else 7)
        spin = r.uniform(0, TAU)
        for b in range(count):
            br = random.Random(r.random())
            h = spin + b * TAU / count + br.uniform(-0.25, 0.25)
            e = math.radians(br.uniform(*spec["elev"]))
            L = reach * br.uniform(0.8, 1.1)
            droop = spec["droop"] * br.uniform(0.7, 1.2) * lerp(1.0, 0.5, t)
            # The lowest boughs may sweep down to the ground, not through it.
            droop = min(droop, max(0.05, (z - 0.25) / L - math.sin(-e) - 0.1))
            W = L * spec["width"] * (1.35 if far else 1.0)
            spray(m, c, h, e, L, W, droop, col, nfn, far, br)
            if not far and b % 2 == 0 and t < 0.85:
                spray(m, c + UP * 0.12, h + 0.3, e + math.radians(10), L * 0.7, W * 0.85, droop * 0.8,
                      NEEDLE_LIGHT if col != NEEDLE_DRY else col, nfn, far, br)
    tip = axis_at(H * 1.02)
    PL.strap(m, [tip - UP * 0.6, tip - UP * 0.2, tip + UP * 0.35], [0.14, 0.08, 0.0], spec["cols"][0],
             nfn, fold=0.3, cols=0, uv=FUV, slot=LEAVES, side=Vector((1, 0, 0)))
    PL.strap(m, [tip - UP * 0.6, tip - UP * 0.2, tip + UP * 0.35], [0.14, 0.08, 0.0], spec["cols"][0],
             nfn, fold=0.3, cols=0, uv=FUV, slot=LEAVES, side=Vector((0, 1, 0)))


# ------------------------------------------------------------------------------------- variants

PINE = dict(height=9.0, trunk=0.22, whorls=15, clear=0.18, reach=2.2, boughs=10, elev=(-8, 12), droop=0.35,
            width=0.3, cols=(NEEDLE, NEEDLE_DARK, NEEDLE))

VARIANTS = [
    ("Pine_Classic", pine, 71, PINE),
    ("Pine_Wide", pine, 72, dict(PINE, height=8.5, trunk=0.28, whorls=14, clear=0.17, reach=3.0, boughs=11,
                                 droop=0.45, taper=0.75, cols=(NEEDLE_DARK, NEEDLE, NEEDLE_DARK))),
    ("Pine_Tall", pine, 73, dict(PINE, height=12.0, trunk=0.26, whorls=17, clear=0.32, reach=1.9, boughs=9,
                                 crowd=0.85, cols=(NEEDLE_BLUE, NEEDLE_BLUE, NEEDLE_DARK), shade=CONE_BLUE,
                                 bark=BARK_GREY)),
    ("Pine_Sparse", pine, 74, dict(PINE, height=10.0, whorls=12, clear=0.35, reach=2.3, boughs=8, gaps=0.3,
                                   dead=0.07, bend=0.08, lean=0.06, width=0.36)),
    ("Pine_Young", pine, 75, dict(PINE, height=4.5, trunk=0.12, whorls=11, clear=0.12, reach=1.5, boughs=8,
                                  droop=0.25, cols=(NEEDLE_LIGHT, NEEDLE, NEEDLE_LIGHT))),
]

# --------------------------------------------------------------------------------------- driver


def materials(textures):
    T.MATERIALS.clear()
    for name, path, cull in (("PineBark", textures[0], True), ("PineLeaves", textures[1], False)):
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        mat.use_backface_culling = cull
        nodes = mat.node_tree.nodes
        bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        bsdf.inputs["Roughness"].default_value = 0.9
        image = bpy.data.images.load(path, check_existing=False)
        image.name = name
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = image
        mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        T.MATERIALS.append(mat)


def main(out_dir=None, only=None):
    P.clear()
    T.BUV, T.BARK = BUV, WOOD
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "pines")
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
            print(f"[pines] {obj.name}: {m.tris()} tris, {wide:.1f} m across, {tall:.1f} m tall")
        objects.append(built)
        if out_dir is None:
            for obj in built:
                obj.location.x = len(objects) * 8.0 - 8.0
                obj.location.y = 10.0 if obj.name.endswith("_Far") else 0.0

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
    print(f"[pines] exported {len(objects)} to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
