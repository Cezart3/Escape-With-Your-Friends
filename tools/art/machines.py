"""The revive machine, from code (#245, the oddities).

    blender -b --factory-startup -P tools/art/machines.py -- <absolute path>/Assets/_Project/Art/Casino/Models

Writes Machines.fbx, painted from the slots' ramp sheet like the vehicles. Every mesh is drawn in
metres in the prefab's own space (ReviveMachineBuilder), so the builder puts it at the origin:

- `Mach_Revive`: the machine round the old greybox's 3 x 2.8 x 2 m housing, front +z. Scrap built and
  proud of it: a riveted teal cabinet on wooden skids, a hazard-striped funnel for a mouth where the
  Intake socket takes the body in (0, 1.6, 1.1), a REVIVE sign, a pressure gauge, a big lever, pipes
  to the top and a smoking chimney at the back.
- `Mach_Revive_Rotor`: the fan on the lid, drawn round its own pivot, which the builder puts at
  (0, 2.9, 0). ReviveMachine spins it about y faster as the cycle nears the end.

The machine was the last Unity primitive on the island: grey cubes on the default material, which
read as an unlit black box in the island 2 shots.
"""

import math
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import slots as S  # noqa: E402
import buildings as B  # noqa: E402
import items as I  # noqa: E402
import vehicles as V  # noqa: E402

box = B.box
log = B.log
ball = I.ball
turned = V.turned
TAU = math.tau

W, H, D = 1.4, 2.6, 0.9      # the cabinet's half width, top and half depth; skids under it to 0.2
MOUTH = (0.0, 1.6)            # the funnel's centre, x and y, on the front face


def revive(s):
    # Skids and the cabinet, with a darker plinth and a lid plate.
    for z in (-0.7, 0.7):
        box(s, (-1.55, 0.0, z - 0.14), (1.55, 0.2, z + 0.14), "BROWN", outline=True)
    box(s, (-W, 0.2, -D), (W, 0.45, D), "NAVY", outline=True)
    box(s, (-W, 0.45, -D), (W, H, D), "TEAL", outline=True)
    box(s, (-W - 0.04, H, -D - 0.04), (W + 0.04, H + 0.12, D + 0.04), "METAL", outline=True)

    # Corner posts and rivets down them.
    for x in (-W, W):
        for z in (-D, D):
            box(s, (x - 0.07, 0.2, z - 0.07), (x + 0.07, H + 0.05, z + 0.07), "METAL", outline=True)
            for y in (0.7, 1.3, 1.9, 2.45):
                ball(s, (x + 0.08 * (1 if x > 0 else -1), y, z + 0.08 * (1 if z > 0 else -1)), 0.035, "METAL",
                     segs=6, rings=3, outline=False)

    # The funnel: a hazard ring, a flared cone into the cabinet, a black throat.
    mx, my = MOUTH
    turned(s, [(0.72, 0.0), (0.72, 0.08), (0.46, 0.08), (0.46, 0.0)], "YELLOW", z0=D, x=mx, y=my, segs=16,
           smooth=False)
    for k in range(8):
        t = TAU * (k + 0.5) / 8
        c = Vector((mx + math.cos(t) * 0.59, my + math.sin(t) * 0.59, D + 0.09))
        a = Vector((math.cos(t + 0.18), math.sin(t + 0.18), 0)) * 0.11
        log(s, tuple(c - a), tuple(c + a), 0.035, "DARK", sides=4)
    turned(s, [(0.68, 0.08), (0.62, 0.28), (0.52, 0.38), (0.46, 0.4)], "METAL", z0=D, x=mx, y=my, segs=16)
    turned(s, [(0.0, -0.02), (0.48, -0.02), (0.48, 0.0), (0.0, 0.0)], "DARK", z0=D + 0.02, x=mx, y=my, segs=16,
           smooth=False, outline=False)

    # The sign over the mouth.
    box(s, (-0.95, 2.35, D), (0.95, 2.58, D + 0.06), "RED", outline=True)
    B.sign_text(s, "REVIVE", 0.2, (0, 2.465, D + 0.07), (0, 1), "CREAM", depth=0.02, res=3)

    # A pressure gauge, top left of the front, its needle in the red.
    turned(s, [(0.0, 0.0), (0.2, 0.0), (0.22, 0.05), (0.2, 0.08), (0.0, 0.08)], "METAL", z0=D, x=-1.05, y=2.0,
           segs=14, smooth=False)
    turned(s, [(0.0, 0.0), (0.17, 0.0), (0.17, 0.01), (0.0, 0.01)], "CREAM", z0=D + 0.08, x=-1.05, y=2.0, segs=14,
           smooth=False, outline=False)
    log(s, (-1.05, 2.0, D + 0.1), (-0.95, 2.11, D + 0.1), 0.012, "RED", sides=4)

    # Two bulbs on the front corners of the lid, and a row of buttons under the gauge.
    for x, col in ((-1.15, "LIME"), (1.15, "RED")):
        I.lathe(s, [(0.09, 0.0), (0.09, 0.08), (0.0, 0.08)], "DARK", c=(x, H + 0.12, 0.7), segs=8, smooth=False)
        ball(s, (x, H + 0.26, 0.7), 0.1, col, segs=10, rings=6)
    for k, col in enumerate(("RED", "YELLOW", "LIME")):
        turned(s, [(0.0, 0.0), (0.05, 0.0), (0.05, 0.04), (0.0, 0.05)], col, z0=D, x=-1.2 + k * 0.16, y=1.62,
               segs=8, outline=False)

    # The lever on the right side: a pivot, a rod raked back, a red knob.
    V.axle_x(s, [(0.0, 0.0), (0.13, 0.0), (0.13, 0.12), (0.0, 0.12)], "DARK", (W, 1.5, 0.25), segs=10, smooth=False)
    log(s, (W + 0.12, 1.5, 0.25), (W + 0.18, 2.25, -0.1), 0.035, "METAL", sides=6, outline=True)
    ball(s, (W + 0.18, 2.3, -0.12), 0.09, "RED", segs=10, rings=6)

    # Pipes from the sides up to the lid, and a hopper drum on top for the rotor to sit in.
    for side in (-1, 1):
        x = side * (W + 0.1)
        log(s, (x, 0.6, -0.5), (x, H - 0.15, -0.5), 0.07, "METAL", sides=8, outline=True)
        log(s, (x, H - 0.15, -0.5), (side * 0.75, H + 0.2, -0.5), 0.07, "METAL", sides=8, outline=True)
        for y in (0.9, 1.7):
            box(s, (x - 0.1, y - 0.04, -0.6), (x + 0.1, y + 0.04, -0.4), "DARK")
    I.lathe(s, [(0.78, 0.0), (0.78, 0.14), (0.72, 0.17), (0.72, 0.1), (0.0, 0.1)], "METAL", c=(0, H + 0.12, 0),
            segs=18, smooth=False)

    # A hatch on the back, screwed shut.
    box(s, (-0.8, 0.7, -D - 0.05), (0.8, 2.1, -D), "NAVY", outline=True)
    for x in (-0.7, 0.7):
        for y in (0.8, 2.0):
            ball(s, (x, y, -D - 0.06), 0.04, "METAL", segs=6, rings=3, outline=False)
    return s


def chimney(s):
    """Drawn upright: a lathe round Unity y."""
    I.lathe(s, [(0.13, 0.0), (0.13, 0.7), (0.21, 0.75), (0.21, 0.85), (0.0, 0.85)], "DARK", c=(0.8, H + 0.12, -0.55),
            segs=10, smooth=False)


def rotor(s):
    """Four paddle blades round a hub, a red beacon on top: the part that spins."""
    I.lathe(s, [(0.0, -0.08), (0.16, -0.08), (0.16, 0.1), (0.1, 0.16), (0.0, 0.16)], "DARK", c=(0, 0, 0), segs=12,
            smooth=False)
    for k in range(4):
        t = TAU * k / 4
        d = Vector((math.cos(t), 0, math.sin(t)))
        side = Vector((-d.z, 0, d.x))
        root, tip = d * 0.14, d * 0.62
        quad_lo = [root - side * 0.06, tip - side * 0.12, tip + side * 0.12, root + side * 0.06]
        lo = [tuple(p + Vector((0, -0.03, 0)) + side * 0.03 * (i % 2)) for i, p in enumerate(quad_lo)]
        hi = [tuple(p + Vector((0, 0.03, 0)) - side * 0.03 * (i % 2)) for i, p in enumerate(quad_lo)]
        B.prism(s, lo, hi, "YELLOW" if k % 2 else "RED", outline=True)
    ball(s, (0, 0.24, 0), 0.09, "RED", segs=10, rings=6)


MODELS = [("Mach_Revive", lambda s: (revive(s), chimney(s))), ("Mach_Revive_Rotor", rotor)]


def main(out_dir=None):
    S.P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "slots")
    mat = S.material(S.paint_texture(folder))
    objects = []
    for i, (name, make) in enumerate(MODELS):
        s = S.Sym()
        make(s)
        obj = S.finish(s, name, mat, fit=False, outline=0.012)
        print(f"[machines] {obj.name}: {V.tri_count(obj)} tris")
        if out_dir is None:
            obj.location = (-i * 5, 0, 0)
        objects.append(obj)

    if out_dir is None:
        return objects

    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "Machines.fbx"), use_selection=True,
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z",
                             axis_up="Y", mesh_smooth_type="FACE", bake_space_transform=True, path_mode="RELATIVE")
    print(f"[machines] exported {len(objects)} meshes to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
