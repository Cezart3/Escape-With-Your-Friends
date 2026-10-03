"""Every weapon's model, from code (#283).

    blender -b --factory-startup -P tools/art/weapons.py -- <absolute path>/Assets/_Project/Art/Casino/Models

Writes Weapons.fbx, painted from the slots' ramp sheet like the items: one mesh per WeaponFactory seed,
`Wpn_<id>`, one metre long and centred on the origin. WeaponFactory scales it to the seed's length.

The way WeaponFactory's kits lay them: a gun along +z with the muzzle forward and its top up; a blade
or a tool with its tip at +z, its handle at -z, flat across x with its edge to +x.
"""

import math
import os
import random
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import slots as S  # noqa: E402
import tables as T  # noqa: E402
import buildings as B  # noqa: E402
import items as I  # noqa: E402

u = T.u
box = B.box
log = B.log
ball = I.ball
shaped = I.shaped
rx = I.rx
at = I.at
TAU = math.tau


def bevel(s, lo, hi, col, b=0.006):
    T.ubox(s, lo, hi, col, bevel=b)


def rod(s, z0, z1, r, col, y=0.0, x=0.0, segs=8, outline=True):
    """A round along z."""
    shaped(s, lambda: I.lathe(s, [(0.0, 0.0), (r, 0.0), (r, z1 - z0), (0.0, z1 - z0)], col, segs=segs,
                              outline=outline), rx(90), at(x, y, z0))


def turned(s, profile, col, z0=0.0, y=0.0, segs=10, outline=True):
    """A lathe [(radius, length)] along z from z0."""
    shaped(s, lambda: I.lathe(s, profile, col, segs=segs, outline=outline), rx(90), at(0, y, z0))


def flat_c(s, pts, thick, col):
    """flat() centred on y 0 instead of standing on it."""
    shaped(s, lambda: I.flat(s, pts, thick, col), at(0, -thick / 2, 0))


def grip_wrap(s, z0, z1, r, col, y=0.0, n=4):
    for k in range(n):
        z = z0 + (z1 - z0) * (k + 0.5) / n
        rod(s, z - 0.012, z + 0.012, r, col, y=y, segs=8, outline=False)


# ------------------------------------------------------------------------------------------- guns

def pistol(s, slide="DARK", frame="STONE", accent=None):
    bevel(s, (-0.06, 0.02, -0.5), (0.06, 0.2, 0.42), slide)
    for k in range(5):
        z = -0.42 + k * 0.035
        box(s, (-0.062, 0.06, z), (0.062, 0.18, z + 0.014), "OUTLINE")
    bevel(s, (-0.055, -0.08, -0.45), (0.055, 0.03, 0.3), frame)
    rod(s, 0.4, 0.5, 0.04, "METAL", y=0.12)
    shaped(s, lambda: bevel(s, (-0.065, -0.5, -0.11), (0.065, 0.0, 0.11), frame, 0.012), rx(16), at(0, -0.04, -0.33))
    for k in range(4):
        shaped(s, lambda k=k: box(s, (-0.068, -0.12 - k * 0.08, -0.1), (0.068, -0.1 - k * 0.08, 0.1), "OUTLINE"),
               rx(16), at(0, -0.04, -0.33))
    pts = [(0, -0.08, -0.13), (0, -0.2, -0.12), (0, -0.21, 0.02), (0, -0.08, 0.06)]
    S.tube(s, [u(*p) for p in pts], 0.014, frame, sides=5)
    box(s, (-0.012, -0.16, -0.08), (0.012, -0.08, -0.05), "METAL")
    box(s, (-0.015, 0.2, 0.33), (0.015, 0.24, 0.38), "METAL")
    box(s, (-0.03, 0.2, -0.48), (0.03, 0.24, -0.44), "METAL")
    if accent:
        box(s, (-0.063, 0.09, -0.2), (0.063, 0.13, 0.36), accent)


def pistol_mk2(s):
    pistol(s, slide="NAVY", frame="DARK", accent="GOLD")
    rod(s, 0.45, 1.05, 0.065, "DARK", y=0.12, segs=10)
    for z in (0.5, 1.0):
        rod(s, z - 0.02, z + 0.02, 0.07, "GOLD", y=0.12, segs=10, outline=False)
    bevel(s, (-0.04, 0.2, -0.1), (0.04, 0.3, 0.1), "DARK")
    ball(s, (0, 0.27, 0.1), (0.03, 0.03, 0.01), "RED", segs=8, rings=4, outline=False)


def pistol_auto(s):
    pistol(s, slide="STONE", frame="DARK", accent="RED")
    shaped(s, lambda: bevel(s, (-0.045, -1.0, -0.075), (0.045, -0.45, 0.075), "DARK"), rx(16), at(0, -0.04, -0.33))
    bevel(s, (-0.07, 0.04, 0.38), (0.07, 0.18, 0.56), "METAL")
    for k in range(3):
        box(s, (-0.072, 0.13, 0.41 + k * 0.05), (0.072, 0.19, 0.43 + k * 0.05), "OUTLINE")
    log(s, (0, -0.02, -0.46), (0, 0.02, -0.75), 0.02, "METAL", sides=5)
    bevel(s, (-0.06, -0.12, -0.8), (0.06, 0.06, -0.72), "DARK")


def smg(s):
    bevel(s, (-0.05, -0.02, -0.25), (0.05, 0.12, 0.25), "DARK")
    rod(s, 0.25, 0.48, 0.035, "STONE", y=0.06)
    for k in range(4):
        rod(s, 0.27 + k * 0.05, 0.29 + k * 0.05, 0.04, "OUTLINE", y=0.06, outline=False)
    rod(s, 0.47, 0.5, 0.025, "METAL", y=0.06)
    shaped(s, lambda: bevel(s, (-0.04, -0.3, -0.05), (0.04, 0.0, 0.05), "STONE"), rx(12), at(0, -0.02, -0.1))
    bevel(s, (-0.035, -0.32, 0.04), (0.035, -0.02, 0.12), "DARK")
    shaped(s, lambda: bevel(s, (-0.04, -0.22, -0.04), (0.04, 0.0, 0.04), "STONE"), rx(8), at(0, -0.02, 0.32))
    log(s, (0, 0.02, -0.25), (0, 0.0, -0.48), 0.015, "METAL", sides=5)
    log(s, (0, 0.09, -0.25), (0, 0.0, -0.48), 0.015, "METAL", sides=5)
    bevel(s, (-0.045, -0.06, -0.52), (0.045, 0.06, -0.46), "DARK")
    bevel(s, (-0.02, 0.12, -0.05), (0.02, 0.17, 0.12), "METAL", 0.004)
    box(s, (-0.051, 0.0, -0.18), (0.051, 0.04, 0.2), "TEAL")


def shotgun(s):
    rod(s, -0.05, 0.5, 0.032, "METAL", y=0.045)
    rod(s, -0.05, 0.42, 0.028, "STONE", y=-0.01)
    rod(s, 0.06, 0.28, 0.045, "BROWN", y=-0.005, segs=10)
    for k in range(5):
        rod(s, 0.08 + k * 0.045, 0.095 + k * 0.045, 0.047, "OUTLINE", y=-0.005, outline=False)
    bevel(s, (-0.04, -0.04, -0.2), (0.04, 0.08, -0.04), "DARK")
    pts = [(0, 0.02, -0.2), (0, -0.04, -0.3), (0, -0.09, -0.5)]
    S.tube(s, [u(*p) for p in pts], [0.035, 0.04, 0.05], "BROWN", sides=6)
    shaped(s, lambda: bevel(s, (-0.042, -0.06, -0.24), (0.042, 0.05, 0.0), "BROWN", 0.01), rx(14),
           at(0, -0.08, -0.5))
    bevel(s, (-0.045, -0.14, -0.53), (0.045, 0.02, -0.5), "DARK")
    ball(s, (0, 0.085, 0.48), 0.008, "RED", segs=6, rings=3, outline=False)


def rifle(s, wood="BROWN", scope=True):
    rod(s, -0.1, 0.5, 0.016, "METAL", y=0.04)
    pts = [(0, 0.0, 0.32), (0, -0.01, 0.0), (0, -0.02, -0.18), (0, -0.05, -0.26), (0, -0.07, -0.5)]
    S.tube(s, [u(*p) for p in pts], [0.026, 0.035, 0.04, 0.03, 0.05], wood, sides=7)
    shaped(s, lambda: bevel(s, (-0.035, -0.07, -0.2), (0.035, 0.03, 0.0), wood, 0.01), rx(10), at(0, -0.06, -0.5))
    bevel(s, (-0.03, 0.01, -0.18), (0.03, 0.07, 0.0), "DARK", 0.004)
    log(s, (0.03, 0.06, -0.08), (0.08, 0.05, -0.1), 0.008, "METAL", sides=4)
    ball(s, (0.085, 0.05, -0.1), 0.015, "METAL", segs=6, rings=4, outline=False)
    S.tube(s, [u(*p) for p in [(0, -0.03, -0.12), (0, -0.07, -0.11), (0, -0.06, -0.02), (0, -0.03, 0.0)]],
           0.008, "METAL", sides=4)
    if scope:
        turned(s, [(0.0, 0.0), (0.028, 0.0), (0.028, 0.05), (0.02, 0.08), (0.02, 0.2), (0.03, 0.24),
                   (0.03, 0.28), (0.0, 0.28)], "DARK", z0=-0.16, y=0.12, segs=10)
        for z in (-0.08, 0.06):
            box(s, (-0.012, 0.06, z - 0.012), (0.012, 0.11, z + 0.012), "DARK")
        ball(s, (0, 0.12, 0.122), (0.024, 0.024, 0.004), "SKY", segs=8, rings=4, outline=False)


def sniper(s):
    rifle(s, wood="SEA", scope=False)
    rod(s, 0.48, 0.56, 0.026, "DARK", y=0.04)
    turned(s, [(0.0, 0.0), (0.035, 0.0), (0.035, 0.06), (0.024, 0.09), (0.024, 0.24), (0.04, 0.28),
               (0.04, 0.34), (0.0, 0.34)], "DARK", z0=-0.2, y=0.13, segs=12)
    for z in (-0.1, 0.07):
        box(s, (-0.014, 0.06, z - 0.014), (0.014, 0.12, z + 0.014), "DARK")
    ball(s, (0, 0.13, 0.142), (0.03, 0.03, 0.004), "SKY", segs=8, rings=4, outline=False)
    for xx in (-1, 1):
        log(s, (0, 0.0, 0.3), (xx * 0.06, -0.16, 0.38), 0.008, "DARK", sides=4)
    for k in range(3):
        box(s, (-0.03, -0.03 - k * 0.025, 0.12 + k * 0.05), (0.03, -0.02 - k * 0.025, 0.16 + k * 0.05), "SEA")


def machinegun(s):
    bevel(s, (-0.06, -0.06, -0.22), (0.06, 0.1, 0.16), "DARK")
    rod(s, 0.16, 0.42, 0.05, "STONE", y=0.03, segs=10)
    for k in range(5):
        for a in range(0, 360, 60):
            ball(s, (0.051 * math.sin(math.radians(a)), 0.03 + 0.051 * math.cos(math.radians(a)), 0.19 + k * 0.045),
                 0.009, "OUTLINE", segs=4, rings=3, outline=False)
    rod(s, 0.42, 0.52, 0.018, "METAL", y=0.03)
    rod(s, 0.5, 0.53, 0.028, "DARK", y=0.03)
    pts = [(0, 0.1, -0.12), (0, 0.17, -0.06), (0, 0.17, 0.06), (0, 0.1, 0.12)]
    S.tube(s, [u(*p) for p in pts], 0.012, "METAL", sides=5)
    bevel(s, (0.06, -0.12, -0.12), (0.17, 0.04, 0.06), "SEA")
    for k in range(6):
        ball(s, (0.06 - k * 0.025, 0.05, -0.05), (0.008, 0.008, 0.024), "GOLD", segs=5, rings=3, outline=False)
    shaped(s, lambda: bevel(s, (-0.045, -0.24, -0.05), (0.045, 0.0, 0.05), "DARK"), rx(14), at(0, -0.04, -0.16))
    pts = [(0, 0.04, -0.22), (0, 0.0, -0.36), (0, -0.04, -0.48)]
    S.tube(s, [u(*p) for p in pts], [0.04, 0.045, 0.06], "DARK", sides=6)
    for xx in (-1, 1):
        log(s, (0, 0.0, 0.36), (xx * 0.08, -0.18, 0.42), 0.01, "DARK", sides=4)


# ------------------------------------------------------------------------------------------ melee

def handle(s, z0, z1, r, col, wrap="DARK"):
    rod(s, z0, z1, r, col, segs=8)
    grip_wrap(s, z0 + 0.02, z1 - 0.02, r * 1.08, wrap, n=4)
    ball(s, (0, 0, z0), (r * 1.3, r * 1.3, r * 0.8), col, segs=8, rings=4)


def knife(s):
    handle(s, -0.5, -0.12, 0.05, "BROWN")
    box(s, (-0.11, -0.03, -0.13), (0.11, 0.03, -0.1), "GOLD")
    flat_c(s, [(-0.06, -0.1), (0.07, -0.1), (0.08, 0.2), (0.03, 0.42), (-0.06, 0.5), (-0.07, 0.2)], 0.02, "METAL")
    flat_c(s, [(-0.035, -0.08), (-0.025, -0.08), (-0.03, 0.4), (-0.04, 0.4)], 0.024, "STONE")


def machete(s):
    handle(s, -0.5, -0.24, 0.035, "DARK", wrap="RED")
    box(s, (-0.05, -0.02, -0.25), (0.06, 0.02, -0.22), "METAL")
    flat_c(s, [(-0.04, -0.23), (0.05, -0.23), (0.09, 0.2), (0.1, 0.42), (0.04, 0.5), (-0.05, 0.42), (-0.04, 0.0)],
           0.014, "METAL")
    flat_c(s, [(0.035, -0.2), (0.05, -0.2), (0.09, 0.38), (0.075, 0.4)], 0.017, "CREAM")


def hatchet(s, head="METAL", shaft="BROWN", spike=False, wrap="DARK"):
    log(s, (0, 0, -0.5), (0, 0, 0.48), 0.032, shaft, sides=7, outline=True)
    grip_wrap(s, -0.46, -0.26, 0.038, wrap, n=4)
    edge = [(-0.04, 0.3), (0.06, 0.3), (0.2, 0.22), (0.26, 0.28), (0.27, 0.46), (0.2, 0.52), (0.06, 0.46),
            (-0.04, 0.46)]
    flat_c(s, edge, 0.05, head)
    flat_c(s, [(0.24, 0.24), (0.265, 0.28), (0.275, 0.46), (0.245, 0.5)], 0.054, "CREAM")
    if spike:
        flat_c(s, [(-0.04, 0.32), (-0.04, 0.44), (-0.22, 0.39)], 0.04, head)


def hatchet_fire(s):
    hatchet(s, head="RED", shaft="YELLOW", spike=True, wrap="DARK")


def shovel(s):
    rod(s, -0.38, 0.12, 0.028, "BROWN", segs=8)
    pts = [(-0.06, 0, -0.38), (-0.07, 0, -0.46), (0, 0, -0.5), (0.07, 0, -0.46), (0.06, 0, -0.38)]
    S.tube(s, [u(*p) for p in pts], 0.02, "DARK", sides=5, cap=True)
    log(s, (-0.07, 0, -0.46), (0.07, 0, -0.46), 0.022, "BROWN", sides=6)
    rod(s, 0.1, 0.18, 0.04, "METAL", segs=8)
    spade = [(-0.12, 0.16), (0.12, 0.16), (0.13, 0.36), (0.07, 0.47), (0.0, 0.5), (-0.07, 0.47), (-0.13, 0.36)]
    shaped(s, lambda: flat_c(s, spade, 0.018, "METAL"), rx(-6))
    log(s, (0, 0.015, 0.18), (0, 0.006, 0.42), 0.012, "STONE", sides=4)


def bat(s, extra=None):
    turned(s, [(0.0, 0.0), (0.05, 0.0), (0.05, 0.02), (0.032, 0.035), (0.03, 0.3), (0.04, 0.5), (0.068, 0.75),
               (0.075, 0.92), (0.065, 0.98), (0.0, 1.0)], "STRAW", z0=-0.5, segs=12)
    rod(s, -0.47, -0.22, 0.034, "DARK" if extra != "shark" else "BLUE", segs=10)
    for k in range(5):
        rod(s, -0.45 + k * 0.05, -0.44 + k * 0.05, 0.036, "OUTLINE", segs=10, outline=False)
    if extra == "nails":
        rng = random.Random(9)
        for k in range(16):
            z = 0.12 + rng.uniform(0, 0.32)
            a = rng.uniform(0, TAU)
            r = 0.04 + 0.03 * (z - 0.0) / 0.45
            p0 = (r * math.sin(a), r * math.cos(a), z)
            p1 = ((r + 0.06) * math.sin(a), (r + 0.06) * math.cos(a), z + 0.01)
            log(s, p0, p1, 0.005, "METAL", sides=3)
            ball(s, p0, 0.01, "METAL", segs=4, rings=3, outline=False)
        rod(s, 0.0, 0.03, 0.06, "RED", segs=10, outline=False)
    if extra == "shark":
        for row in range(3):
            z = 0.15 + row * 0.11
            r = 0.055 + 0.018 * row
            for k in range(7):
                a = k * TAU / 7 + row * 0.4
                n = Vector((math.sin(a), math.cos(a), 0))
                tan = Vector((math.cos(a), -math.sin(a), 0))
                base = n * r + Vector((0, 0, z))
                tri = [base - tan * 0.022, base + tan * 0.022, base + n * 0.07 + Vector((0, 0, 0.015))]
                I.pipe(s, [tuple(tri[0]), tuple(tri[2]), tuple(tri[1])], 0.007, "WHITE", sides=3, outline=False)
                ball(s, tuple(base + n * 0.025), (0.016, 0.016, 0.016), "WHITE", segs=4, rings=3, outline=False)


def chainsaw(s):
    bevel(s, (-0.11, -0.13, -0.5), (0.11, 0.12, -0.05), "ORANGE", 0.02)
    bevel(s, (-0.112, -0.05, -0.42), (0.112, 0.08, -0.15), "DARK")
    for k in range(5):
        box(s, (-0.114, -0.03 + k * 0.022, -0.4), (0.114, -0.02 + k * 0.022, -0.17), "STONE")
    pts = [(0, 0.12, -0.42), (0, 0.24, -0.38), (0, 0.25, -0.18), (0, 0.12, -0.08)]
    S.tube(s, [u(*p) for p in pts], 0.02, "DARK", sides=6)
    pts = [(-0.12, 0.1, -0.06), (-0.14, 0.2, -0.04), (0.0, 0.26, -0.02), (0.14, 0.2, -0.04), (0.12, 0.1, -0.06)]
    S.tube(s, [u(*p) for p in pts], 0.016, "DARK", sides=5)
    bevel(s, (-0.02, -0.07, -0.08), (0.02, 0.07, 0.48), "METAL", 0.01)
    ball(s, (0, 0, 0.48), (0.02, 0.07, 0.03), "METAL", segs=8, rings=4)
    for k in range(18):
        z = -0.05 + k * 0.03
        for y in (-0.075, 0.075):
            box(s, (-0.022, y - 0.006, z), (0.022, y + 0.006, z + 0.016), "DARK")
    log(s, (0.11, 0.02, -0.48), (0.14, 0.02, -0.5), 0.02, "RED", sides=5)
    box(s, (-0.113, -0.12, -0.12), (0.113, -0.02, -0.06), "YELLOW")


# ---------------------------------------------------------------------------------------- export

WEAPONS = {
    "knife": knife, "machete": machete, "hatchet": hatchet, "hatchet_fire": hatchet_fire, "shovel": shovel,
    "bat": bat, "bat_nailed": lambda s: bat(s, "nails"), "bat_shark": lambda s: bat(s, "shark"),
    "chainsaw": chainsaw,
    "pistol": pistol, "pistol_mk2": pistol_mk2, "pistol_auto": pistol_auto, "smg": smg, "shotgun": shotgun,
    "rifle": rifle, "sniper": sniper, "machinegun": machinegun,
}


def fit(s):
    """Centred on the origin, one metre along z."""
    pts = [B.unity(v.co) for v in s.bm.verts]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    k = 1.0 / (hi.z - lo.z)
    mid = (lo + hi) / 2
    for v in s.bm.verts:
        v.co = u(*((B.unity(v.co) - mid) * k))


def main(out_dir=None, only=None):
    S.P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "slots")
    mat = S.material(S.paint_texture(folder))
    objects = []
    for i, (name, make) in enumerate(WEAPONS.items()):
        if only and name not in only:
            continue
        s = S.Sym()
        make(s)
        fit(s)
        obj = S.finish(s, "Wpn_" + name, mat, fit=False, outline=0.006)
        tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        print(f"[weapons] {obj.name}: {tris} tris")
        if out_dir is None:
            obj.location = (-(i % 6) * 0.5, (i // 6) * 1.3, 0)
        objects.append(obj)

    if out_dir is None:
        return objects

    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "Weapons.fbx"), use_selection=True,
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z",
                             axis_up="Y", mesh_smooth_type="FACE", bake_space_transform=True, path_mode="RELATIVE")
    print(f"[weapons] exported {len(objects)} meshes to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
