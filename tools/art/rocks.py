"""Rocks, fallen logs and stumps, modelled from code (#248).

    blender -b --factory-startup -P tools/art/rocks.py -- Assets/_Project/Art/Models/Rocks

Same contract as palms.py and trees.py, whose helpers it borrows: one FBX per variant with `<name>`
and `<name>_Far`, two ramp textures (Textures/Wood.png, Textures/Stone.png). No folder: a preview
row in the open Blender.

A rock is an icosphere chiselled by a handful of random planes - every vertex past a plane is pushed
back onto it - then lumped, squashed and sunk. The planes make the big flat facets a stylised rock
reads by; the faces keep their own normals, so the toon terminator cuts clean across them. Faces that
look at the sky take the moss column on mossy variants, and every face runs dark at the foot to light
on top. The far mesh is the same rock from a coarser sphere, cut by the same planes.

Wood is trees.py's branch tube lying down, with an end-grain cap (rings painted along the radius),
a jagged broken end, branch stubs, moss on top and, on the mossy log, a cluster of mushrooms.
"""

import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import palms as P  # noqa: E402
import trees as T  # noqa: E402

TAU, UP, lerp = P.TAU, P.UP, P.lerp

# ------------------------------------------------------------------------------------- textures

COLUMNS = 8
# Wood columns.
BARK, BARK_MOSS, ENDGRAIN, ROT, CAP_RED, CAP_TAN, STEM = range(7)
# Stone columns.
GRANITE, WARM, BASALT, SANDSTONE, MOSS, MOSS_DRY = range(6)

WOOD_RAMPS = {
    BARK: [(0, "3A3228"), (0.4, "54493A"), (1, "6E604E")],
    BARK_MOSS: [(0, "2A3E1C"), (0.5, "3E5A26"), (1, "55702E")],
    ROT: [(0, "2A2018"), (1, "4A3A2A")],
    CAP_RED: [(0, "E8D8B8"), (0.25, "B8401E"), (1, "8E2A14")],
    CAP_TAN: [(0, "E8DCC0"), (0.25, "B48A52"), (1, "7E5A34")],
    STEM: [(0, "C8B898"), (1, "EEE4CC")],
}
STONE_RAMPS = {
    GRANITE: [(0, "3C3E3C"), (0.35, "5E605C"), (0.8, "7E807A"), (1, "8E8E86")],
    WARM: [(0, "403A34"), (0.35, "665C52"), (0.8, "887C6E"), (1, "968A7A")],
    BASALT: [(0, "222224"), (0.5, "36363A"), (1, "4A4A4E")],
    SANDSTONE: [(0, "6E5A44"), (0.4, "A08664"), (1, "C2A882")],
    MOSS: [(0, "2C4A1C"), (0.5, "47702A"), (1, "6A9238")],
    MOSS_DRY: [(0, "4A5426"), (0.5, "6E7834"), (1, "8E9444")],
}


def endgrain(u, v):
    """Along v, centre (0) to rim (1): pale rings round a darker heart, then the bark."""
    if v > 0.92:
        return P.rgb("3A3228")
    if v > 0.86:
        return P.rgb("6A4E34")
    light, dark = P.rgb("C8A272"), P.rgb("9A7448")
    ring = 0.5 + 0.5 * math.sin(v * TAU * 6.5 + 0.6 * math.sin(u * TAU))
    c = dark.lerp(light, ring ** 1.5)
    return c * (0.82 + 0.18 * v)


def speckle(stops, seed):
    rng = random.Random(seed)
    spots = [(rng.random(), rng.random(), rng.uniform(0.02, 0.06), rng.uniform(-0.12, 0.1)) for _ in range(70)]

    def paint(u, v):
        c = P.ramp(stops, v)
        for su, sv, r, k in spots:
            if (u - su) ** 2 + ((v - sv) * 4) ** 2 < r * r:
                c = c * (1 + k)
        return c
    return paint


def paint_textures(folder):
    os.makedirs(folder, exist_ok=True)
    wood = {c: (T.bark_paint(s, 500 + c) if c in (BARK, BARK_MOSS) else P.ramp_paint(s, 500 + c, 0.04))
            for c, s in WOOD_RAMPS.items()}
    wood[ENDGRAIN] = endgrain
    stone = {c: speckle(s, 600 + c) for c, s in STONE_RAMPS.items()}

    def sheet(columns):
        def pixel(x, v):
            u = min(max((x % P.CELL + 0.5 - P.MARGIN) / (P.CELL - 2 * P.MARGIN), 0.0), 1.0)
            return columns.get(x // P.CELL, columns[0])(u, v)
        return pixel

    paths = (os.path.join(folder, "Wood.png"), os.path.join(folder, "Stone.png"))
    P.write_png(paths[0], P.CELL * COLUMNS, P.TEX_H, sheet(wood))
    P.write_png(paths[1], P.CELL * COLUMNS, P.TEX_H, sheet(stone))
    return paths


def UV(col, u, v):
    return P.uv_on(COLUMNS, col, u, v)


WOOD, STONE = 0, 1   # material slots; trees.tube paints slot 0 from an 8-column sheet, so wood is 0

# ---------------------------------------------------------------------------------------- rocks


def icosphere(sub):
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=sub, radius=1.0)
    verts = [v.co.copy() for v in bm.verts]
    faces = [[v.index for v in f.verts] for f in bm.faces]
    bm.free()
    return verts, faces


def rock_shape(rng, spec):
    """The plan for one rock, drawn once and applied to both spheres."""
    cuts = []
    for _ in range(spec.get("cuts", 14)):
        n = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-0.4, 1))).normalized()
        cuts.append((n, rng.uniform(*spec.get("depth", (0.5, 0.8)))))
    lumps = [(Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-0.5, 1))).normalized(),
              rng.uniform(0.08, 0.2)) for _ in range(4)]
    return cuts, lumps


def rock(m, rng, spec, far, at=Vector(), scale=1.0, spin=0.0):
    cuts, lumps = rock_shape(rng, spec)
    verts, faces = icosphere(2 if far else 3)
    sx, sy, sz = spec["size"]
    c, s = math.cos(spin), math.sin(spin)
    placed = []
    for v in verts:
        v = v * (1 + sum(k * max(0.0, v.dot(d)) ** 2 for d, k in lumps))
        for n, depth in cuts:
            t = v.dot(n)
            if t > depth:
                v = v - n * (t - depth)
        v = Vector((v.x * sx, v.y * sy, v.z * sz))
        v.z = max(v.z, -0.25 * sz)
        v = Vector((v.x * c - v.y * s, v.x * s + v.y * c, v.z + 0.12 * sz)) * scale + at
        placed.append(m.v(v))

    top = max(m.verts[i].z for i in placed)
    bottom = min(m.verts[i].z for i in placed)
    moss = spec.get("moss")
    for f in faces:
        ids = [placed[i] for i in f]
        a, b, cc = (m.verts[i] for i in ids)
        normal = (b - a).cross(cc - a).normalized()
        mossy = moss is not None and normal.z > moss[1]
        col = moss[0] if mossy else spec["stone"]
        uvs = []
        for i in ids:
            p = m.verts[i]
            h = (p.z - bottom) / max(top - bottom, 1e-3)
            uvs.append(UV(col, (p.x * 0.37 + p.y * 0.23) % 1.0, lerp(0.15, 1.0, h) if not mossy else 0.3 + 0.6 * h))
        m.f(ids, uvs, STONE, None, facing=normal)


def boulder(m, spec, rng, far):
    rock(m, rng, spec, far)


def cluster(m, spec, rng, far):
    """Three rocks leaning on each other: a big one, a mid one against it, a small one at the foot."""
    for at, scale, spin in ((Vector((0, 0, 0)), 1.0, 0.0), (Vector((1.1, 0.5, -0.1)), 0.62, 1.3),
                            (Vector((-0.6, 0.95, -0.1)), 0.42, 2.6)):
        rock(m, random.Random(rng.random()), spec, far, at, scale, spin)


# ----------------------------------------------------------------------------------------- wood

def cap(m, centre, axis, rim, col, jag=None, rng=None):
    """A log's end: a fan of end grain from the heart to the bark; `jag` pushes the heart out into
    splinters, for a broken end."""
    n = len(rim)
    if jag:
        inner = []
        for k in range(n):
            p = m.verts[rim[k]]
            q = centre.lerp(p, 0.55) + axis * rng.uniform(0.05, jag)
            inner.append(m.v(q))
        for k in range(n):
            j = (k + 1) % n
            m.f([rim[k], rim[j], inner[j], inner[k]], [UV(ROT, k / n, 0.9), UV(ROT, (k + 1) / n, 0.9),
                UV(ROT, (k + 1) / n, 0.4), UV(ROT, k / n, 0.4)], WOOD, None, facing=axis)
        tip = m.v(centre + axis * rng.uniform(jag * 0.3, jag * 0.7))
        for k in range(n):
            j = (k + 1) % n
            m.f([inner[k], inner[j], tip], [UV(ROT, k / n, 0.4), UV(ROT, (k + 1) / n, 0.4), UV(ROT, 0.5, 0.0)],
                WOOD, None, facing=axis)
        return
    c = m.v(centre)
    for k in range(n):
        j = (k + 1) % n
        m.f([rim[k], rim[j], c], [UV(col, k / n, 1.0), UV(col, (k + 1) / n, 1.0), UV(col, (k + 0.5) / n, 0.0)],
            WOOD, None, facing=axis)


def log_rings(m, pts, radii, sides, rng, wob=0.07):
    """trees.tube, but keeping the end rings so the caps can close them."""
    n = len(pts)
    tans = [(pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)]).normalized() for i in range(n)]
    ref = UP if abs(tans[0].dot(UP)) < 0.9 else Vector((1, 0, 0))
    nrm = (ref - tans[0] * ref.dot(tans[0])).normalized()
    wobble = [rng.uniform(1 - wob, 1 + wob) for _ in range(sides)]
    rings = []
    for p, t, r in zip(pts, tans, radii):
        nrm = (nrm - t * nrm.dot(t)).normalized()
        b = t.cross(nrm)
        rings.append([(m.v(p + (nrm * math.cos(a) + b * math.sin(a)) * r * wobble[k]),
                       nrm * math.cos(a) + b * math.sin(a))
                      for k, a in enumerate(k * TAU / sides for k in range(sides))])
    return rings, tans


def bark_faces(m, rings, moss, rng, v0=0.2, v1=1.0):
    """The bark round a log; moss on what faces the sky, its edge ragged."""
    n, sides = len(rings), len(rings[0])
    for i in range(n - 1):
        va, vb = lerp(v0, v1, i / (n - 1)), lerp(v0, v1, (i + 1) / (n - 1))
        for k in range(sides):
            j = (k + 1) % sides
            q = (rings[i][k], rings[i][j], rings[i + 1][j], rings[i + 1][k])
            out = q[0][1] + q[1][1]
            col = BARK_MOSS if moss is not None and out.normalized().z > moss + rng.uniform(-0.3, 0.3) else BARK
            m.f([x[0] for x in q], [UV(col, k / sides, va), UV(col, (k + 1) / sides, va), UV(col, (k + 1) / sides, vb),
                                    UV(col, k / sides, vb)], WOOD, [x[1] for x in q], facing=out)


def mushroom(m, base, out, size, col):
    """A stem out of the wood and a domed cap, its underside pale."""
    side = out.cross(UP)
    side = side.normalized() if side.length > 1e-3 else Vector((1, 0, 0))
    up = out.cross(side).normalized()
    if up.z < 0:
        up = -up
    stem_top = base + out * size * 0.5 + up * size * 0.6
    T.tube(m, [base, stem_top], [size * 0.12, size * 0.1], 4, STEM, 0.0, 1.0, tip=False)
    centre = stem_top
    segs = 7
    rim = [m.v(centre + (side * math.cos(a) + out * math.sin(a)) * size * 0.55 - up * size * 0.08)
           for a in (k * TAU / segs for k in range(segs))]
    mid = [m.v(centre + (side * math.cos(a) + out * math.sin(a)) * size * 0.4 + up * size * 0.18)
           for a in (k * TAU / segs for k in range(segs))]
    top = m.v(centre + up * size * 0.3)
    under = m.v(centre - up * size * 0.02)
    for k in range(segs):
        j = (k + 1) % segs
        q = [rim[k], rim[j], mid[j], mid[k]]
        m.f(q, [UV(col, k / segs, 0.45), UV(col, (k + 1) / segs, 0.45), UV(col, (k + 1) / segs, 0.8),
                UV(col, k / segs, 0.8)], WOOD, None, facing=m.centre(q) - centre + up * 0.2)
        m.f([mid[k], mid[j], top], [UV(col, k / segs, 0.8), UV(col, (k + 1) / segs, 0.8), UV(col, 0.5, 1.0)],
            WOOD, None, facing=up)
        m.f([rim[j], rim[k], under], [UV(col, k / segs, 0.1), UV(col, (k + 1) / segs, 0.1), UV(col, 0.5, 0.0)],
            WOOD, None, facing=-up)


def fallen_log(m, spec, rng, far):
    length, r = spec["length"], spec["radius"]
    sides = 6 if far else 10
    n = 3 if far else 7
    bend = rng.uniform(-0.15, 0.15)
    pts = [Vector((lerp(-length / 2, length / 2, i / (n - 1)), bend * math.sin(math.pi * i / (n - 1)), r * 0.82))
           for i in range(n)]
    radii = [r * lerp(1.0, spec.get("taper", 0.82), i / (n - 1)) * (1.08 if i == 0 else 1.0) for i in range(n)]
    rings, tans = log_rings(m, pts, radii, sides, random.Random(rng.random()))
    bark_faces(m, rings, spec.get("moss"), random.Random(rng.random()))
    cap(m, pts[0], -tans[0], [x[0] for x in rings[0]], ENDGRAIN)
    jag_rng = random.Random(rng.random())
    if spec.get("broken"):
        cap(m, pts[-1], tans[-1], [x[0] for x in rings[-1]], ENDGRAIN, jag=spec["broken"], rng=jag_rng)
    else:
        cap(m, pts[-1], tans[-1], [x[0] for x in rings[-1]], ENDGRAIN)

    stub_rng = random.Random(rng.random())
    shroom_rng = random.Random(rng.random())
    if far:
        return
    for _ in range(spec.get("stubs", 3)):
        x = stub_rng.uniform(0.15, 0.85)
        a = stub_rng.uniform(-0.6, 2.2)
        p = Vector((lerp(-length / 2, length / 2, x), 0, r * 0.82))
        d = Vector((stub_rng.uniform(0.2, 0.6), math.cos(a), math.sin(a))).normalized()
        rr = r * stub_rng.uniform(0.18, 0.28)
        T.tube(m, [p + d * r * 0.7, p + d * (r + stub_rng.uniform(0.12, 0.3))], [rr, rr * 0.85], 5, BARK, 0.3, 0.6)
    for _ in range(spec.get("mushrooms", 0)):
        x = shroom_rng.uniform(0.2, 0.8)
        a = shroom_rng.uniform(-0.4, 0.5)
        out = Vector((0, -math.cos(a), math.sin(a)))
        p = Vector((lerp(-length / 2, length / 2, x), 0, r * 0.82)) + out * r * 0.92
        mushroom(m, p, out, shroom_rng.uniform(0.14, 0.24), CAP_RED if shroom_rng.random() < 0.6 else CAP_TAN)


def stump(m, spec, rng, far):
    h, r = spec["height"], spec["radius"]
    sides = 6 if far else 11
    n = 3 if far else 6
    pts = [Vector((0, 0, lerp(-0.2, h, i / (n - 1)))) for i in range(n)]
    radii = [r * (1 + 0.55 * math.exp(-max(p.z, 0) / 0.25)) for p in pts]
    rings, tans = log_rings(m, pts, radii, sides, random.Random(rng.random()), wob=0.1)
    bark_faces(m, rings, spec.get("moss"), random.Random(rng.random()), 0.0, 0.5)
    jag_rng = random.Random(rng.random())
    cap(m, pts[-1], UP, [x[0] for x in rings[-1]], ENDGRAIN, jag=spec.get("broken"), rng=jag_rng)
    root_rng = random.Random(rng.random())
    if far:
        return
    for k in range(spec.get("roots", 5)):
        a = k * TAU / spec.get("roots", 5) + root_rng.uniform(-0.3, 0.3)
        out = Vector((math.cos(a), math.sin(a), 0))
        length = root_rng.uniform(0.4, 0.7) * r * 2
        rp = [out * r * 0.9 + UP * 0.22, out * (r + length * 0.5) + UP * 0.05, out * (r + length) - UP * 0.12]
        rr = r * root_rng.uniform(0.25, 0.32)
        T.tube(m, rp, [rr, rr * 0.8, rr * 0.65], 5, BARK, 0.1, 0.4)


ROCK = dict(stone=GRANITE, size=(1.2, 1.0, 0.8))

VARIANTS = [
    ("Rock_Boulder", boulder, 31, dict(ROCK, moss=(MOSS_DRY, 0.82))),
    ("Rock_Small", boulder, 32, dict(ROCK, stone=WARM, size=(1.1, 0.9, 0.75), cuts=12)),
    ("Rock_Slab", boulder, 33, dict(ROCK, size=(1.6, 1.1, 0.45), cuts=12, depth=(0.6, 0.85), moss=(MOSS_DRY, 0.9))),
    ("Rock_Tall", boulder, 34, dict(ROCK, stone=WARM, size=(0.7, 0.6, 1.6), cuts=16, depth=(0.5, 0.75))),
    ("Rock_Mossy", boulder, 35, dict(ROCK, size=(1.15, 1.05, 0.85), cuts=15, moss=(MOSS, 0.35))),
    ("Rock_Cluster", cluster, 36, dict(ROCK, size=(1.0, 0.9, 0.8), moss=(MOSS_DRY, 0.75))),
    ("Rock_Sand", boulder, 37, dict(ROCK, stone=SANDSTONE, size=(1.3, 1.0, 0.6), cuts=9, depth=(0.68, 0.9))),
    ("Log_Fallen", fallen_log, 41, dict(length=3.4, radius=0.36, moss=0.7, stubs=3, broken=0.35)),
    ("Log_Mossy", fallen_log, 42, dict(length=4.0, radius=0.42, taper=0.75, moss=0.3, stubs=2, mushrooms=6,
                                       broken=0.5)),
    ("Stump_Cut", stump, 43, dict(height=0.55, radius=0.34, moss=0.9)),
    ("Stump_Broken", stump, 44, dict(height=0.95, radius=0.3, moss=0.4, broken=0.55, roots=6)),
]


# --------------------------------------------------------------------------------------- driver

def materials(textures):
    T.MATERIALS.clear()
    for name, path in (("Wood", textures[0]), ("Stone", textures[1])):
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
        T.MATERIALS.append(mat)


def main(out_dir=None, only=None):
    P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "rocks")
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
            print(f"[rocks] {obj.name}: {m.tris()} tris, {max(xs) - min(xs):.1f} x {max(ys) - min(ys):.1f} x "
                  f"{max(zs) - min(zs):.1f} m")
        objects.append(built)
        if out_dir is None:
            for obj in built:
                obj.location.x = len(objects) * 5.0 - 5.0
                obj.location.y = 6.0 if obj.name.endswith("_Far") else 0.0

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
    print(f"[rocks] exported {len(objects)} to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
