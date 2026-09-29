# Blender, background: converts every .glb in a folder to .fbx beside a target folder.
#   blender -b -P tools/art/glb2fbx.py -- <in_dir> <out_dir> [name ...]
# The Kenney mirror (github.com/shorepine/kenney) ships glTF-binary only; Unity reads FBX.
import bpy, os, sys

args = sys.argv[sys.argv.index("--") + 1:]
src, dst, names = args[0], args[1], args[2:]
os.makedirs(dst, exist_ok=True)
for f in sorted(os.listdir(src)):
    if not f.endswith(".glb") or (names and f[:-4] not in names):
        continue
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.join(src, f))
    bpy.ops.export_scene.fbx(filepath=os.path.join(dst, f[:-4] + ".fbx"), use_selection=False,
                             path_mode="STRIP", embed_textures=False, mesh_smooth_type="FACE")
    print("converted", f)
