"""The volcano's crater cap, modelled from code (#245).

    blender -b --factory-startup -P tools/art/volcano.py -- Assets/_Project/Art/Models/Volcano

Writes Crater.fbx and two textures into the folder given after `--`: Textures/Volcano.png, ramps
for the rock, and Textures/Lava.png, the lava's crust and the glow on the crater's foot. Run with no
folder inside a live Blender (the MCP, through runpy) and it only builds the cap for looking at.

The island's summit is a dome of terrain, and a heightmap cannot hold a crater: the pit would have to
be a hole in the ground the trees, the splat and the navmesh all agree on. So the crater sits on the
summit as a cap. Its pit floor is above the peak, so no terrain shows inside it, and its outer skirt
runs down at about fifty degrees to ninety metres below the peak, steeper than any slope of the dome,
so the skirt dives into the ground wherever it meets it and its edge is never seen. Volcano.cs puts it
on the highest point at run time.

The shape is one profile swept round the axis: skirt, shoulder, a jagged crest with a breach on one
side, the inner wall of red scoria, and a pool of lava with its crust cracked into plates. Faces are
flat shaded, like the rocks, so the toon terminator cuts across them.
"""

import math
import os
import random
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import palms as P  # noqa: E402

TAU, lerp = P.TAU, P.lerp

# ------------------------------------------------------------------------------------- textures

COLUMNS = 3
BASALT, ASH, SCORIA = range(COLUMNS)
RAMPS = {
    BASALT: [(0, "1C1C1E"), (0.5, "2C2B2D"), (1, "3E3C3E")],
    ASH: [(0, "3A3634"), (0.5, "544C46"), (1, "6E645A")],
    SCORIA: [(0, "4A2418"), (0.4, "5E3226"), (1, "3A2A26")],
}

LAVA_W, LAVA_H = 256, 128      # left half: the pool's crust; right half: the glow up the wall
SLOT_ROCK, SLOT_LAVA = 0, 1


def paint_rock(folder):
    columns = {c: P.ramp_paint(s, 700 + c, 0.14) for c, s in RAMPS.items()}

    def pixel(x, v):
        u = min(max((x % P.CELL + 0.5 - P.MARGIN) / (P.CELL - 2 * P.MARGIN), 0.0), 1.0)
        return columns[x // P.CELL](u, v)

    path = os.path.join(folder, "Volcano.png")
    P.write_png(path, P.CELL * COLUMNS, P.TEX_H, pixel)
    return path


def paint_lava(folder):
    """Plates of dark crust with molten cracks between them, the cracks brightest where they are
    widest; then a ramp from white-hot at the foot of the wall to cooled rock a couple of metres up."""
    rng = random.Random(61)
    pts = [(rng.random(), rng.random()) for _ in range(22)]

    def crust(u, v):
        d = sorted(min(abs(u - px), 1 - abs(u - px)) ** 2 + min(abs(v - py), 1 - abs(v - py)) ** 2
                   for px, py in pts)
        edge = math.sqrt(d[1]) - math.sqrt(d[0])
        hot = max(0.0, 1 - edge * 22)
        plate = P.rgb("2A1612") * (0.8 + 0.4 * ((math.sin(u * 91 + v * 37) + 1) / 2))
        return plate * (1 - hot) + (P.rgb("FFC040") * hot ** 0.6 + P.rgb("FF5A10") * (1 - hot ** 0.6)) * hot

    def glow(v):
        return P.ramp([(0, "FFB038"), (0.25, "E84A10"), (0.6, "6A2014"), (1, "2E1A16")], v)

    def pixel(x, v):
        if x < LAVA_W // 2:
            return crust((x + 0.5) / (LAVA_W / 2), v)
        return glow(v)

    path = os.path.join(folder, "Lava.png")
    P.write_png(path, LAVA_W, LAVA_H, pixel)
    return path


def RUV(col, u, v):
    return P.uv_on(COLUMNS, col, u, v)


def crust_uv(p, radius):
    """The pool's crust, mapped flat from above onto the left half of Lava.png."""
    return (0.02 + 0.46 * (p.x / radius * 0.5 + 0.5), 0.04 + 0.92 * (p.y / radius * 0.5 + 0.5))


def glow_uv(v):
    return (0.75, 0.04 + 0.92 * v)


# ----------------------------------------------------------------------------------------- shape

SIDES = 40

# The profile, outside in: (radius, height above the peak, how much the crest's jag moves it).
PROFILE = [
    (80.0, -90.0, 0.0), (58.0, -50.0, 0.0), (40.0, -20.0, 0.1), (28.0, -4.0, 0.3), (20.0, 3.5, 0.6),
    (15.5, 7.0, 0.9), (13.5, 8.2, 1.0),                                     # the crest
    (12.2, 7.6, 1.0), (10.5, 5.0, 0.7), (9.0, 2.8, 0.4), (7.9, 1.6, 0.15), (7.4, 0.9, 0.0),
]
CREST = 6
# Which ramp each band between two profile rings takes; None is the glow, on the lava material.
BANDS = [BASALT, BASALT, BASALT, ASH, ASH, ASH, ASH, SCORIA, SCORIA, None, None]
POOL_Z = 1.25
POOL_RINGS = (7.9, 5.6, 3.0)


def wave(rng, terms):
    """A periodic 1D noise round the axis: a few sines with random phases."""
    parts = [(k, rng.uniform(0, TAU), rng.uniform(0.5, 1.0) / k) for k in terms]
    total = sum(a for _, _, a in parts)
    return lambda t: sum(a * math.sin(k * t + ph) for k, ph, a in parts) / total


def crater(m, rng):
    jag = wave(rng, (3, 5, 8, 13))
    bulge = wave(rng, (2, 4, 7))
    breach = rng.uniform(0, TAU)

    rings = []
    for k, (r, z, w) in enumerate(PROFILE):
        ring = []
        for i in range(SIDES):
            t = i / SIDES * TAU
            notch = math.exp(-(((t - breach + math.pi) % TAU - math.pi) / 0.32) ** 2)
            rr = r * (1 + 0.07 * bulge(t + k * 0.4))
            zz = z + w * (2.2 * jag(t) - 3.6 * notch)
            ring.append(m.v((rr * math.cos(t), rr * math.sin(t), zz)))
        rings.append(ring)

    up = Vector((0, 0, 1))
    for k, col in enumerate(BANDS):
        outer, inner = rings[k], rings[k + 1]
        # The band's normal leans out of the axis on the outside and into it on the inside.
        inward = k >= CREST
        for i in range(SIDES):
            j = (i + 1) % SIDES
            quad = [outer[i], outer[j], inner[j], inner[i]]
            mid = m.centre(quad)
            facing = up + Vector((mid.x, mid.y, 0)).normalized() * (-1.2 if inward else 1.2)
            if col is None:
                v0 = (m.verts[outer[i]].z - POOL_Z) / 2.0
                v1 = (m.verts[inner[i]].z - POOL_Z) / 2.0
                uvs = [glow_uv(min(max(v, 0), 1)) for v in (v0, v0, v1, v1)]
                m.f(quad, uvs, SLOT_LAVA, facing=facing)
                continue
            c = col
            u0, u1 = (0.15, 0.85) if i % 2 else (0.85, 0.15)
            v_out, v_in = 1 - k / len(BANDS), 1 - (k + 1) / len(BANDS)
            if col == SCORIA:
                v_out, v_in = 0.95, 0.35
            m.f(quad, [RUV(c, u0, v_out), RUV(c, u1, v_out), RUV(c, u1, v_in), RUV(c, u0, v_in)],
                SLOT_ROCK, facing=facing)

    # The pool: rings of crust plates, each vertex nudged up or down a little so the plates tilt.
    pool = []
    for n, r in enumerate(POOL_RINGS):
        ring = []
        for i in range(SIDES):
            t = (i + 0.5 * n) / SIDES * TAU
            ring.append(m.v((r * math.cos(t), r * math.sin(t), POOL_Z + rng.uniform(-0.08, 0.1) * (n > 0))))
        pool.append(ring)
    radius = POOL_RINGS[0]
    for n in range(len(POOL_RINGS) - 1):
        a, b = pool[n], pool[n + 1]
        for i in range(SIDES):
            j = (i + 1) % SIDES
            for tri in ((a[i], a[j], b[i]), (a[j], b[j], b[i])):
                m.f(tri, [crust_uv(m.verts[x], radius) for x in tri], SLOT_LAVA, facing=up)
    centre = m.v((0, 0, POOL_Z + 0.05))
    inner = pool[-1]
    for i in range(SIDES):
        tri = (inner[i], inner[(i + 1) % SIDES], centre)
        m.f(tri, [crust_uv(m.verts[x], radius) for x in tri], SLOT_LAVA, facing=up)


# --------------------------------------------------------------------------------------- driver

def materials(rock, lava):
    P.MATERIALS.clear()
    for name, path in (("VolcanoRock", rock), ("Lava", lava)):
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        mat.use_backface_culling = True
        nodes = mat.node_tree.nodes
        bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        bsdf.inputs["Roughness"].default_value = 0.95
        image = bpy.data.images.load(path, check_existing=False)
        image.name = name
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = image
        mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        P.MATERIALS.append(mat)


def main(out_dir=None):
    P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "volcano")
    os.makedirs(folder, exist_ok=True)
    materials(paint_rock(folder), paint_lava(folder))

    m = P.Mesh()
    crater(m, random.Random(245))
    obj = m.build("Crater")
    xs, ys, zs = ([v[i] for v in m.verts] for i in range(3))
    print(f"[volcano] Crater: {m.tris()} tris, {max(xs) - min(xs):.0f} x {max(ys) - min(ys):.0f} x "
          f"{max(zs) - min(zs):.0f} m")
    if out_dir is None:
        return obj

    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "Crater.fbx"), use_selection=True,
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z",
                             axis_up="Y", mesh_smooth_type="FACE", bake_space_transform=True,
                             path_mode="RELATIVE")
    print(f"[volcano] exported to {out_dir}")
    return obj


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
