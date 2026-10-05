"""Renders the three vertical clips the phone plays into Promo/textures/phone-clips.png (stacked
top to bottom, 780x1688 each). Photographic, brand-free, all from CC0 Poly Haven assets (see
Promo/assets/fetch.sh): a harbour at blue hour, a cake on an oak board, a skate park bowl.

    blender -b -P Promo/scene/phone_clips.py [-- --samples 64]
"""
import bpy, math, os, subprocess, sys
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
PROMO = os.path.dirname(HERE)
A = os.path.join(PROMO, "assets")
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
SAMPLES = int(argv[argv.index("--samples") + 1]) if "--samples" in argv else 64
W, H = 780, 1688
import tempfile
TMP = tempfile.mkdtemp(prefix="phone_clips_")


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.samples = SAMPLES
    sc.cycles.use_denoising = True
    for backend in ("METAL", "OPTIX", "CUDA"):
        try:
            prefs = bpy.context.preferences.addons["cycles"].preferences
            prefs.compute_device_type = backend; prefs.get_devices()
            if any(d.type == backend for d in prefs.devices):
                for d in prefs.devices: d.use = d.type == backend
                sc.cycles.device = "GPU"; break
        except Exception: pass
    sc.render.resolution_x, sc.render.resolution_y = W, H
    sc.view_settings.view_transform = "AgX"
    sc.view_settings.look = "AgX - Medium High Contrast"
    w = bpy.data.worlds.new("w"); sc.world = w; w.use_nodes = True
    N = w.node_tree.nodes
    env = N.new("ShaderNodeTexEnvironment"); mp = N.new("ShaderNodeMapping"); tc = N.new("ShaderNodeTexCoord")
    w.node_tree.links.new(tc.outputs["Generated"], mp.inputs[0]); w.node_tree.links.new(mp.outputs[0], env.inputs[0])
    w.node_tree.links.new(env.outputs[0], N["Background"].inputs[0])
    cd = bpy.data.cameras.new("c"); cam = bpy.data.objects.new("c", cd); sc.collection.objects.link(cam); sc.camera = cam
    return sc, env, mp, N["Background"], cam


def hdri_clip(name, hdr, yaw, pitch=85, lens=24, exposure=0.0, strength=1.0):
    sc, env, mp, bg, cam = reset()
    env.image = bpy.data.images.load(os.path.join(A, "clips", hdr))
    bg.inputs[1].default_value = strength
    cam.data.lens = lens
    cam.rotation_euler = (math.radians(pitch), 0, math.radians(yaw))
    sc.view_settings.exposure = exposure
    sc.render.filepath = os.path.join(TMP, name + ".png")
    bpy.ops.render.render(write_still=True)


def cake_clip(name):
    sc, env, mp, bg, cam = reset()
    env.image = bpy.data.images.load(os.path.join(A, "clips", "comfy_cafe_4k.hdr"))
    mp.inputs["Rotation"].default_value = (0, 0, math.radians(200))
    bg.inputs[1].default_value = 0.8
    # oak board
    m = bpy.data.materials.new("oak"); m.use_nodes = True
    nt = m.node_tree; b = nt.nodes["Principled BSDF"]
    def tex(f, non_color=False):
        t = nt.nodes.new("ShaderNodeTexImage"); t.image = bpy.data.images.load(os.path.join(A, "tex", f))
        if non_color: t.image.colorspace_settings.name = "Non-Color"
        return t
    d = tex("oak_veneer_01_diff_2k.jpg"); r = tex("oak_veneer_01_rough_2k.jpg", True); n = tex("oak_veneer_01_nor_gl_2k.jpg", True)
    # the board runs well past the top of the frame (a 4 m board ended in view and the cafe
    # backdrop showed as a dark blob over the cake); the grain keeps its size: one tile per 4 m
    BOARD = 24.0
    tc_ = nt.nodes.new("ShaderNodeTexCoord"); mp_ = nt.nodes.new("ShaderNodeMapping")
    mp_.inputs["Scale"].default_value = (BOARD / 4.0, BOARD / 4.0, 1.0)
    nt.links.new(tc_.outputs["UV"], mp_.inputs[0])
    for t_ in (d, r, n): nt.links.new(mp_.outputs[0], t_.inputs[0])
    nm = nt.nodes.new("ShaderNodeNormalMap")
    nt.links.new(d.outputs[0], b.inputs["Base Color"]); nt.links.new(r.outputs[0], b.inputs["Roughness"])
    nt.links.new(n.outputs[0], nm.inputs["Color"]); nt.links.new(nm.outputs[0], b.inputs["Normal"])
    bpy.ops.mesh.primitive_plane_add(size=BOARD); board = bpy.context.active_object; board.data.materials.append(m)
    bpy.ops.import_scene.gltf(filepath=os.path.join(A, "models", "strawberry_chocolate_cake", "strawberry_chocolate_cake_1k.gltf"))
    objs = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    lo = Vector((min(min((o.matrix_world @ Vector(c)).x for c in o.bound_box) for o in objs),
                 min(min((o.matrix_world @ Vector(c)).y for c in o.bound_box) for o in objs),
                 min(min((o.matrix_world @ Vector(c)).z for c in o.bound_box) for o in objs)))
    hi = Vector((max(max((o.matrix_world @ Vector(c)).x for c in o.bound_box) for o in objs),
                 max(max((o.matrix_world @ Vector(c)).y for c in o.bound_box) for o in objs),
                 max(max((o.matrix_world @ Vector(c)).z for c in o.bound_box) for o in objs)))
    size = max(hi.x - lo.x, hi.y - lo.y)
    centre = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))
    root = bpy.data.objects.new("cake_root", None); sc.collection.objects.link(root)
    for o in objs:
        if o.parent is None or o.parent not in objs:
            o.parent = root
    root.location = -centre * (0.3 / size); root.scale = (0.3 / size,) * 3
    h = (hi.z - lo.z) * 0.3 / size
    cam.data.lens = 55
    cam.data.dof.use_dof = True; cam.data.dof.aperture_fstop = 2.0
    tgt = Vector((0, 0, h * 0.5))
    eye = Vector((0.05, -0.80, h * 0.5 + 0.30))
    cam.location = eye
    cam.rotation_euler = (tgt - eye).to_track_quat("-Z", "Y").to_euler()
    cam.data.dof.focus_distance = (tgt - eye).length
    L = bpy.data.lights.new("key", "AREA"); L.size = 0.6; L.energy = 60; L.color = (1.0, 0.85, 0.7)
    lo_ = bpy.data.objects.new("key", L); sc.collection.objects.link(lo_)
    lo_.location = (-0.6, -0.2, 0.7); lo_.rotation_euler = (Vector((0, 0, 0.1)) - lo_.location).to_track_quat("-Z", "Y").to_euler()
    sc.render.filepath = os.path.join(TMP, name + ".png")
    bpy.ops.render.render(write_still=True)


hdri_clip("clip1", "golden_bay_4k.hdr", 112, 86, 22, 0.3)
cake_clip("clip2")
hdri_clip("clip3", "skate_park_4k.hdr", 150, 86, 20, 0.5)
out = os.path.join(PROMO, "textures", "phone-clips.png")
subprocess.run(["ffmpeg", "-y", "-loglevel", "error"] + sum([["-i", os.path.join(TMP, f"clip{i}.png")] for i in (1, 2, 3)], [])
               + ["-filter_complex", "vstack=3", out], check=True)
print("wrote", out)
