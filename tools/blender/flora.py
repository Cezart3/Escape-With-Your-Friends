"""
Every plant, rock and piece of scenery on the island, generated (#78).

Run it inside Blender - through the MCP addon, or headless:

    "C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe" --background --python tools/blender/flora.py

It wipes the scene, builds each asset from primitives, and writes one .glb per asset into
Assets/_Project/Art/Models. Unity imports those, and Editor/ModelLibrary.cs turns them into the
prefabs the spawners place.

Why generated rather than downloaded: a photoscanned tree next to a seven-sided greybox hut looks
worse than either does alone. Everything here shares one vocabulary - flat shading, a few hundred
faces, and the exact colours in Editor/Palette.cs - so the island reads as a style rather than as a
pile of assets from different places. It is also re-runnable, which a downloaded mesh is not: change
a number here and every tree on both islands changes with it.

The budget is deliberately generous for a low-poly game - a big tree is under a thousand triangles -
because the thing that makes this look cheap is not triangle count, it is repetition. So there are
sixteen assets rather than four, every canopy is jittered so no two blobs are the same shape, and
the leaves use two greens so a canopy has a lit side and a shaded one. All of it still runs on an
integrated GPU, which is the actual constraint.

Seeds are fixed, so two runs give the same island.
"""

import bpy
import math
import os
import random

OUT = r"D:\Proiecte\JocStupid\Assets\_Project\Art\Models"

# Straight out of Editor/Palette.cs, plus two tones that only exist in the foliage. Unity snaps the
# imported materials back onto the palette assets; matching here means the Blender preview is the
# same island.
PALETTE = {
    "Wood":     ((0.42, 0.30, 0.19), 0.88),
    "WoodDark": ((0.28, 0.20, 0.13), 0.90),
    "Leaf":     ((0.28, 0.45, 0.24), 0.90),
    "LeafDark": ((0.19, 0.33, 0.18), 0.92),
    "Stone":    ((0.46, 0.46, 0.44), 0.94),
    "Sand":     ((0.82, 0.74, 0.55), 0.96),
    "Accent":   ((0.72, 0.28, 0.22), 0.80),
}


def material(name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    rgb, roughness = PALETTE[name]
    bsdf.inputs["Base Color"].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    return mat


def wipe():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.objects):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def jitter(obj, amount):
    """
    Shoves every vertex a little. The single cheapest thing that stops eight ico-spheres from
    reading as eight ico-spheres, and it costs no triangles at all.
    """
    for v in obj.data.vertices:
        v.co.x += random.uniform(-amount, amount)
        v.co.y += random.uniform(-amount, amount)
        v.co.z += random.uniform(-amount, amount)


def finish(name, parts, base):
    """Joins the pieces, applies the scales, flat-shades and returns the object."""
    bpy.ops.object.select_all(action="DESELECT")
    for p in parts:
        p.select_set(True)

    bpy.context.view_layer.objects.active = base
    bpy.ops.object.join()

    merged = bpy.context.object
    merged.name = name
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bpy.ops.object.shade_flat()
    return merged


def export(obj):
    os.makedirs(OUT, exist_ok=True)

    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    # FBX, not glTF: Unity imports FBX with no extra package, and glTF needs com.unity.cloud.gltfast.
    # Blender's defaults here (-Z forward, Y up) are Unity's convention; the importer bakes the
    # remaining axis conversion in, see ModelLibrary.
    path = os.path.join(OUT, obj.name + ".fbx")
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True,
                             apply_scale_options="FBX_SCALE_ALL",
                             axis_forward="-Z", axis_up="Y",
                             object_types={"MESH"}, use_mesh_modifiers=True,
                             mesh_smooth_type="FACE", bake_anim=False,
                             path_mode="COPY")

    tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    print("[flora] %-12s %4d tris" % (obj.name, tris))
    return tris


def cone(vertices, r1, r2, depth, location, rotation=(0, 0, 0), material_name="Wood"):
    bpy.ops.mesh.primitive_cone_add(vertices=vertices, radius1=r1, radius2=r2,
                                    depth=depth, location=location, rotation=rotation)
    obj = bpy.context.object
    obj.data.materials.append(material(material_name))
    return obj


def ribbon(name, control, width, material_name="Leaf", segments=7, taper=0.6):
    """
    A tapering strip bent along a quadratic Bezier: a palm frond, a fern leaf, a blade of grass.

    Everything else here is primitives, but a leaf has to *arc*, and no amount of stacked cones
    does that - a straight cone aimed outwards gives a parasol, which is what the first two passes
    at the palm looked like. Blender's bend modifier cannot help either: a cone has two rings of
    vertices and a bend needs something to bend. So the curve is walked directly, two vertices per
    step, which costs fourteen triangles and is the only thing in this file that looks hand-made.
    """
    a, b, c = control
    verts, faces = [], []

    for i in range(segments + 1):
        t = i / segments
        u = 1.0 - t
        x = u * u * a[0] + 2 * u * t * b[0] + t * t * c[0]
        y = u * u * a[1] + 2 * u * t * b[1] + t * t * c[1]
        z = u * u * a[2] + 2 * u * t * b[2] + t * t * c[2]

        # Widest around a third of the way out, pinched at both ends.
        w = width * (math.sin(math.pi * min(t + 0.08, 1.0)) ** taper)
        verts.append((x, y - w * 0.5, z))
        verts.append((x, y + w * 0.5, z))

        if i:
            k = (i - 1) * 2
            faces.append((k, k + 1, k + 3, k + 2))

    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()

    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(material(material_name))
    return obj


def blob(radius, location, subdivisions=2, material_name="Leaf"):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=subdivisions, radius=radius,
                                          location=location)
    obj = bpy.context.object
    obj.data.materials.append(material(material_name))
    return obj


# --- trees ---------------------------------------------------------------------------------------

def broadleaf(name, seed, height=5.0, canopy=2.0, blobs=5, branches=2):
    """The workhorse. Most of the island is these at three sizes."""
    random.seed(seed)
    parts = []

    trunk = cone(7, 0.38, 0.16, height, (0, 0, height / 2))
    parts.append(trunk)

    # A flare at the root, so the tree grows out of the ground instead of being pushed into it.
    parts.append(cone(7, 0.64, 0.38, 0.6, (0, 0, 0.3)))

    for i in range(branches):
        angle = math.tau * (i + random.uniform(0.15, 0.55)) / branches
        z = height * random.uniform(0.52, 0.72)
        length = random.uniform(1.2, 1.9)
        parts.append(cone(5, 0.13, 0.05, length,
                          (math.cos(angle) * length * 0.35, math.sin(angle) * length * 0.35, z),
                          (math.radians(random.uniform(38, 58)), 0, -angle + math.pi / 2)))

    for i in range(blobs):
        angle = math.tau * (i + random.uniform(0, 0.6)) / blobs
        spread = canopy * random.uniform(0.35, 0.72)
        r = canopy * random.uniform(0.62, 1.0)

        # The lower blobs are the shaded side of the canopy. Two greens is the difference between a
        # tree and a green cloud.
        z = height * random.uniform(0.86, 1.12)
        tone = "Leaf" if z > height else "LeafDark"

        leaf = blob(r, (math.cos(angle) * spread, math.sin(angle) * spread, z),
                    material_name=tone)
        leaf.scale = (random.uniform(0.9, 1.2), random.uniform(0.9, 1.2),
                      random.uniform(0.62, 0.86))
        jitter(leaf, r * 0.10)
        parts.append(leaf)

    return finish(name, parts, trunk)


def conifer(name, seed, height=8.0, skirts=5, radius=1.9):
    """A pine. Stacked cones, which is the most readable silhouette in the whole set."""
    random.seed(seed)
    parts = []

    trunk = cone(6, 0.30, 0.10, height, (0, 0, height / 2), material_name="WoodDark")
    parts.append(trunk)

    for i in range(skirts):
        t = i / (skirts - 1.0)
        r = radius * (1.0 - 0.72 * t) * random.uniform(0.92, 1.08)
        z = height * (0.26 + 0.70 * t)
        skirt = cone(8, r, r * 0.12, height * 0.30, (0, 0, z),
                     (0, 0, random.uniform(0, math.tau)), "Leaf" if i % 2 else "LeafDark")
        jitter(skirt, r * 0.05)
        parts.append(skirt)

    return finish(name, parts, trunk)


def palm(name, seed, height=7.5, fronds=8):
    """The one that says 'island' from across the water."""
    random.seed(seed)
    parts = []

    # The lean is the whole character of a palm, so the trunk is a stack of short segments that
    # curve rather than one straight cone.
    segments = 8
    lean = random.uniform(0.10, 0.20)
    x = z = 0.0

    for i in range(segments):
        t = i / (segments - 1.0)
        seg = height / segments
        r1 = 0.30 * (1.0 - 0.45 * t)
        r2 = 0.30 * (1.0 - 0.45 * (t + 1.0 / segments))
        x += lean * seg * t
        parts.append(cone(6, r1, r2, seg * 1.06, (x, 0, z + seg / 2),
                          (0, math.atan(lean * t), 0)))
        z += seg

    trunk = parts[0]

    for i in range(fronds):
        angle = math.tau * i / fronds + random.uniform(-0.14, 0.14)
        reach = random.uniform(2.6, 3.4)
        rise = random.uniform(0.9, 1.35)
        fall = random.uniform(1.0, 1.7)

        frond = ribbon("Frond", ((0, 0, 0), (reach * 0.55, 0, rise), (reach, 0, -fall)),
                       width=random.uniform(0.52, 0.72),
                       material_name="Leaf" if i % 2 else "LeafDark")
        frond.location = (x, 0, z - 0.15)
        frond.rotation_euler = (0, 0, angle)
        parts.append(frond)

    # Coconuts. Two, because three is a bunch and one is a mistake.
    for _ in range(2):
        parts.append(blob(0.18, (x + random.uniform(-0.25, 0.25),
                                 random.uniform(-0.25, 0.25), z - 0.3), 1, "WoodDark"))

    return finish(name, parts, trunk)


def dead_tree(name, seed, height=5.5, limbs=4):
    """Bare, grey and wrong-looking. Island two needs these; island one needs a few."""
    random.seed(seed)
    parts = []

    trunk = cone(6, 0.34, 0.08, height, (0, 0, height / 2), material_name="WoodDark")
    parts.append(trunk)
    parts.append(cone(6, 0.58, 0.34, 0.5, (0, 0, 0.25), material_name="WoodDark"))

    for i in range(limbs):
        angle = math.tau * (i + random.uniform(0.1, 0.6)) / limbs
        z = height * random.uniform(0.45, 0.88)
        length = random.uniform(1.0, 2.2)
        parts.append(cone(4, 0.11, 0.03, length,
                          (math.cos(angle) * length * 0.38, math.sin(angle) * length * 0.38, z),
                          (math.radians(random.uniform(25, 70)), 0, -angle + math.pi / 2),
                          "WoodDark"))

    return finish(name, parts, trunk)


# --- undergrowth ---------------------------------------------------------------------------------

def bush(name, seed, radius=0.9, blobs=4, berries=0):
    random.seed(seed)
    parts = []
    base = None

    for i in range(blobs):
        angle = math.tau * i / blobs
        spread = radius * random.uniform(0.25, 0.6)
        r = radius * random.uniform(0.55, 0.95)
        leaf = blob(r, (math.cos(angle) * spread, math.sin(angle) * spread,
                        r * random.uniform(0.55, 0.8)), 1,
                    "Leaf" if i % 2 else "LeafDark")
        leaf.scale = (1.0, 1.0, random.uniform(0.6, 0.85))
        jitter(leaf, r * 0.12)
        parts.append(leaf)
        base = base or leaf

    for _ in range(berries):
        angle = random.uniform(0, math.tau)
        parts.append(blob(0.08, (math.cos(angle) * radius * 0.6, math.sin(angle) * radius * 0.6,
                                 radius * random.uniform(0.6, 1.0)), 1, "Accent"))

    return finish(name, parts, base)


def fern(name, seed, fronds=7, length=1.3):
    """Ground cover with a shape to it. Cheaper than grass and reads better in shade."""
    random.seed(seed)
    parts = []
    base = None

    for i in range(fronds):
        angle = math.tau * i / fronds + random.uniform(-0.15, 0.15)
        reach = length * random.uniform(0.8, 1.25)

        leaf = ribbon("Frond", ((0, 0, 0.05), (reach * 0.5, 0, reach * 0.72),
                                (reach, 0, reach * 0.20)),
                      width=reach * 0.34, segments=5,
                      material_name="Leaf" if i % 2 else "LeafDark")
        leaf.rotation_euler = (0, 0, angle)
        parts.append(leaf)
        base = base or leaf

    return finish(name, parts, base)


def grass(name, seed, blades=9, height=0.55):
    """A tuft. Scattered in thousands, so it is four triangles a blade and nothing else."""
    random.seed(seed)
    parts = []
    base = None

    for i in range(blades):
        angle = math.tau * i / blades + random.uniform(-0.3, 0.3)
        h = height * random.uniform(0.65, 1.35)
        lean = random.uniform(0.18, 0.42)

        blade = ribbon("Blade", ((0, 0, 0), (h * lean * 0.5, 0, h * 0.62), (h * lean, 0, h)),
                       width=0.08, segments=3, taper=0.35,
                       material_name="Leaf" if i % 2 else "LeafDark")
        blade.location = (math.cos(angle) * 0.13, math.sin(angle) * 0.13, 0)
        blade.rotation_euler = (0, 0, angle)
        parts.append(blade)
        base = base or blade

    return finish(name, parts, base)


# --- rocks and debris ----------------------------------------------------------------------------

def rock(name, seed, size=1.0):
    """An ico-sphere with its vertices shoved about. Flat shaded, so every dent reads."""
    random.seed(seed)
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=size, location=(0, 0, size * 0.55))
    stone = bpy.context.object
    stone.name = name
    stone.data.materials.append(material("Stone"))

    for v in stone.data.vertices:
        v.co.x *= random.uniform(0.72, 1.32)
        v.co.y *= random.uniform(0.72, 1.32)
        v.co.z *= random.uniform(0.55, 1.05)

    jitter(stone, size * 0.09)

    stone.scale = (1.0, 1.0, random.uniform(0.62, 0.9))
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bpy.ops.object.shade_flat()
    return stone


def log_(name, seed, length=2.6):
    """A fallen trunk. Cover, seating, and something for a body to land on."""
    random.seed(seed)
    parts = []

    trunk = cone(7, 0.34, 0.29, length, (0, 0, 0.32),
                 (0, math.radians(90), random.uniform(0, 0.4)))
    parts.append(trunk)

    for _ in range(2):
        angle = random.uniform(0, math.tau)
        parts.append(cone(4, 0.10, 0.03, random.uniform(0.5, 0.9),
                          (random.uniform(-length * 0.3, length * 0.3), math.sin(angle) * 0.3, 0.5),
                          (math.radians(random.uniform(50, 85)), 0, angle), "WoodDark"))

    return finish(name, parts, trunk)


def stump(name, seed, radius=0.5):
    random.seed(seed)
    parts = []

    body = cone(8, radius, radius * 0.86, 0.7, (0, 0, 0.35))
    parts.append(body)
    parts.append(cone(8, radius * 0.92, radius * 0.7, 0.08, (0, 0, 0.72), material_name="WoodDark"))

    for i in range(3):
        angle = math.tau * i / 3 + random.uniform(0, 0.5)
        parts.append(cone(5, 0.16, 0.05, 0.7,
                          (math.cos(angle) * radius * 0.8, math.sin(angle) * radius * 0.8, 0.12),
                          (math.radians(72), 0, -angle + math.pi / 2)))

    return finish(name, parts, body)


def main():
    wipe()

    built = []
    assets = (
        broadleaf("Tree_Small", 11, height=3.4, canopy=1.4, blobs=4, branches=1),
        broadleaf("Tree_Mid", 12, height=5.2, canopy=2.1, blobs=5, branches=2),
        broadleaf("Tree_Large", 13, height=7.4, canopy=3.0, blobs=7, branches=3),
        conifer("Pine_Mid", 14, height=7.0, skirts=5, radius=1.7),
        conifer("Pine_Tall", 15, height=10.0, skirts=6, radius=2.1),
        palm("Palm_Tall", 21, height=7.8, fronds=9),
        palm("Palm_Short", 22, height=5.2, fronds=7),
        dead_tree("Tree_Dead", 23, height=5.8, limbs=5),
        bush("Bush_Small", 31, radius=0.7, blobs=3),
        bush("Bush_Wide", 32, radius=1.25, blobs=5),
        bush("Bush_Berry", 33, radius=0.9, blobs=4, berries=5),
        fern("Fern", 34, fronds=7, length=1.3),
        grass("Grass_Tuft", 35, blades=9, height=0.55),
        rock("Rock_Small", 41, size=0.55),
        rock("Rock_Mid", 42, size=1.2),
        rock("Rock_Large", 43, size=2.2),
        log_("Log", 51, length=2.8),
        stump("Stump", 52, radius=0.55),
    )

    for i, obj in enumerate(assets):
        built.append((obj.name, export(obj)))
        # Spread them out so one screenshot shows the whole set rather than one pile.
        obj.location.x = (i % 6) * 7.0
        obj.location.y = (i // 6) * 9.0

    print("[flora] %d assets, %d tris total, biggest %d"
          % (len(built), sum(t for _, t in built), max(t for _, t in built)))


main()
