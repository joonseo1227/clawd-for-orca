"""Clawd for Orca promo: builds the whole scene in Blender and renders it.

    blender -b -P Promo/scene/promo.py -- [--frames 0-899] [--still 120,300] [--out DIR] [--samples N] [--scale 100]

Everything that moves is a pure function of time (`update(t)`), applied from a frame-change
handler, so any single frame can be rendered on its own for checks.
UI images come from Promo/shots-light/<lang>/<name>.png when present, otherwise Promo/textures/<name>.png.
"""

import bpy, bmesh, json, math, os, sys
from mathutils import Vector, Euler

HERE = os.path.dirname(os.path.abspath(__file__))
PROMO = os.path.dirname(HERE)
LAYOUT = json.load(open(os.path.join(HERE, "layout.json")))
FPS = 30
DURATION = 35.0
N_FRAMES = int(DURATION * FPS)

# ----------------------------------------------------------------------------- args
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
def arg(name, default=None):
    return argv[argv.index(name) + 1] if name in argv else default
FRAMES = arg("--frames", f"0-{N_FRAMES - 1}")
STILLS = arg("--still")
LANG = arg("--lang", "ko")          # which language's app UI captures to use
OUT = arg("--out", os.path.join(PROMO, "out", f"frames_{LANG}"))
SAMPLES = int(arg("--samples", "96"))
ENGINE = arg("--engine", "cycles")
SCALE = int(arg("--scale", "100"))

def tex_path(name, *fallbacks):
    """Promo/shots-light/<lang>/<name> (real screenshot) wins over Promo/textures/<name> (drawn mock-up)."""
    sd = LAYOUT.get("shots_dir", "shots")
    for n in (name,) + fallbacks:
        # this language's captures, then Korean ones (until a translation's captures exist), then drawn fallbacks
        for d in (os.path.join(sd, LANG), os.path.join(sd, "ko"), sd, "textures"):
            p = os.path.join(PROMO, d, n)
            if os.path.exists(p):
                return p
    raise FileNotFoundError(name)

# ----------------------------------------------------------------------------- easing
def clamp(x, a=0.0, b=1.0): return max(a, min(b, x))
def lerp(a, b, k): return a + (b - a) * k
def seg(t, t0, t1): return clamp((t - t0) / (t1 - t0)) if t1 > t0 else float(t >= t0)
def smooth(x): x = clamp(x); return x * x * (3 - 2 * x)
def in_out(x):  # quintic-ish, calm
    x = clamp(x); return 16 * x**5 if x < 0.5 else 1 - (-2 * x + 2) ** 5 / 2
def in_out3(x):
    x = clamp(x); return 4 * x**3 if x < 0.5 else 1 - (-2 * x + 2) ** 3 / 2
def out3(x): x = clamp(x); return 1 - (1 - x) ** 3
def out5(x): x = clamp(x); return 1 - (1 - x) ** 5
def out_back(x, s=1.4):
    x = clamp(x); c3 = s + 1; return 1 + c3 * (x - 1) ** 3 + s * (x - 1) ** 2
def spring(x, freq=2.2, damp=6.0):
    """0 -> 1 with a soft overshoot, x in seconds since start."""
    if x <= 0: return 0.0
    return 1 - math.exp(-damp * x) * math.cos(2 * math.pi * freq * x)
def vlerp(a, b, k): return tuple(lerp(x, y, k) for x, y in zip(a, b))
def keys(t, pts, ease=in_out):
    """Piecewise interpolation through (time, value-tuple) keys."""
    if t <= pts[0][0]: return pts[0][1]
    for (t0, v0), (t1, v1) in zip(pts, pts[1:]):
        if t <= t1:
            return vlerp(v0, v1, ease(seg(t, t0, t1)))
    return pts[-1][1]

def srgb(hexstr, a=1.0):
    h = hexstr.lstrip("#")
    c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    lin = [x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4 for x in c]
    return (*lin, a)

# ----------------------------------------------------------------------------- scene reset
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.engine = "CYCLES" if ENGINE == "cycles" else "BLENDER_EEVEE"
scene.render.fps = FPS
scene.frame_start, scene.frame_end = 0, N_FRAMES - 1
scene.render.resolution_x, scene.render.resolution_y = 1920, 1080
scene.render.resolution_percentage = SCALE
scene.render.film_transparent = False
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGB"
ee = scene.eevee
ee.taa_render_samples = SAMPLES
for attr, val in [("use_raytracing", True), ("ray_tracing_method", "SCREEN"), ("use_shadows", True),
                  ("shadow_ray_count", 2), ("shadow_step_count", 8), ("use_gtao", True),
                  ("fast_gi_method", "GLOBAL_ILLUMINATION"), ("use_fast_gi", True)]:
    if hasattr(ee, attr):
        try: setattr(ee, attr, val)
        except Exception: pass
try:
    ee.ray_tracing_options.resolution_scale = "1"
    ee.ray_tracing_options.use_denoise = True
except Exception: pass
if ENGINE == "cycles":
    prefs = bpy.context.preferences.addons["cycles"].preferences
    # Metal on a Mac, OptiX or CUDA on an NVIDIA PC, HIP on AMD: the first that has a GPU.
    for backend in ("METAL", "OPTIX", "CUDA", "HIP", "ONEAPI"):
        try:
            prefs.compute_device_type = backend
            prefs.get_devices()
            gpus = [d for d in prefs.devices if d.type == backend]
            if not gpus:
                continue
            for d in prefs.devices: d.use = d.type == backend
            scene.cycles.device = "GPU"
            print("Cycles GPU:", backend, ", ".join(d.name for d in gpus))
            break
        except Exception as e:
            continue
    else:
        print("No GPU backend available, rendering on the CPU")
    c = scene.cycles
    c.samples = SAMPLES
    c.use_adaptive_sampling = True
    c.adaptive_threshold = 0.05
    # OpenImageDenoise guided by albedo and normal, prefiltered, so fine UI text keeps its edges
    c.use_denoising = arg("--denoise", "oidn") != "off"
    c.denoiser = "OPENIMAGEDENOISE"
    for attr, val in [("denoising_input_passes", "RGB_ALBEDO_NORMAL"), ("denoising_prefilter", "ACCURATE"),
                      ("denoising_quality", "HIGH")]:
        try: setattr(c, attr, val)
        except Exception as e: print("denoise setting skipped:", attr, e)
    try: c.denoising_use_gpu = True
    except Exception: pass
    c.max_bounces = 9; c.diffuse_bounces = 2; c.glossy_bounces = 2
    c.transmission_bounces = 6; c.transparent_max_bounces = 16; c.volume_bounces = 0
    c.caustics_reflective = False; c.caustics_refractive = False
    c.blur_glossy = 1.0
    scene.cycles.filter_width = 1.15   # a slightly tighter pixel filter keeps UI type crisp
    c.sample_clamp_indirect = 8.0
    scene.render.use_persistent_data = True
    scene.render.use_motion_blur = True
    scene.render.motion_blur_shutter = 0.5
# Khronos PBR Neutral keeps base colours (Claude orange, the UI) as authored
try: scene.view_settings.view_transform = "Khronos PBR Neutral"
except Exception: scene.view_settings.view_transform = "Standard"
scene.view_settings.exposure = 0.0

# compositor: a soft glow on only the brightest highlights (the breakout flash, the bulb, the ripple)
try:
    ng = bpy.data.node_groups.new("promo_comp", "CompositorNodeTree")
    rl = ng.nodes.new("CompositorNodeRLayers")
    gl = ng.nodes.new("CompositorNodeGlare")
    try: gl.inputs["Type"].default_value = "Fog Glow"
    except Exception: pass
    gl.inputs["Threshold"].default_value = 3.0
    gl.inputs["Strength"].default_value = 0.45
    gl.inputs["Size"].default_value = 0.6
    ng.interface.new_socket("Image", in_out="OUTPUT", socket_type="NodeSocketColor")
    go = ng.nodes.new("NodeGroupOutput")
    ng.links.new(rl.outputs["Image"], gl.inputs["Image"])
    ng.links.new(gl.outputs["Image"], go.inputs[0])
    scene.compositing_node_group = ng
    scene.render.use_compositing = True
except Exception as e:
    print("compositor glow skipped:", e)

# world: a bright, airy studio gradient: pale sky overhead, soft lilac to the right, a peach glow to
# the left. The camera sees it a little brighter than the light it casts.
ASSETS = os.path.join(PROMO, "assets")
def asset(*p): return os.path.join(ASSETS, *p)
world = bpy.data.worlds.new("World"); scene.world = world
world.use_nodes = True
WN = world.node_tree.nodes; WL = world.node_tree.links
bg = WN["Background"]
_wtc = WN.new("ShaderNodeTexCoord"); _wsp = WN.new("ShaderNodeSeparateXYZ")
WL.new(_wtc.outputs["Generated"], _wsp.inputs[0])
def _wmix(fac, a, b):
    m = WN.new("ShaderNodeMix"); m.data_type = "RGBA"
    WL.new(fac, m.inputs["Factor"])
    for k, v in (("A", a), ("B", b)):
        if isinstance(v, tuple): m.inputs[k].default_value = v
        else: WL.new(v, m.inputs[k])
    return m.outputs["Result"]
def _wrange(v, a, b):
    m = WN.new("ShaderNodeMapRange"); m.interpolation_type = "SMOOTHSTEP"
    WL.new(v, m.inputs["Value"]); m.inputs["From Min"].default_value = a; m.inputs["From Max"].default_value = b
    return m.outputs[0]
_side = _wmix(_wrange(_wsp.outputs[0], -0.8, 0.8), srgb("#FBEDE6"), srgb("#E6E4F8"))   # peach left -> lilac right
_wcol = _wmix(_wrange(_wsp.outputs[2], -0.05, 0.6), _side, srgb("#F3F6FC"))               # -> pale sky overhead
WL.new(_wcol, bg.inputs[0])
_wlp = WN.new("ShaderNodeLightPath"); _wst = WN.new("ShaderNodeMapRange")
_wst.inputs["To Min"].default_value = 0.75   # light and reflections
_wst.inputs["To Max"].default_value = 1.15   # seen directly
WL.new(_wlp.outputs["Is Camera Ray"], _wst.inputs["Value"]); WL.new(_wst.outputs[0], bg.inputs[1])

# ----------------------------------------------------------------------------- materials
def principled(name, color, rough=0.5, **kw):
    m = bpy.data.materials.new(name); m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = color
    b.inputs["Roughness"].default_value = rough
    for k, v in kw.items():
        b.inputs[k].default_value = v
    return m

def glass(name, tint, rough=0.32, alpha=1.0):
    m = principled(name, tint, rough, **{"Transmission Weight": 1.0, "IOR": 1.45})
    for attr, val in [("use_raytrace_refraction", True), ("surface_render_method", "DITHERED"),
                      ("use_transparent_shadow", True)]:
        if hasattr(m, attr):
            try: setattr(m, attr, val)
            except Exception: pass
    try: m.thickness_mode = "SLAB"
    except Exception: pass
    return m

ORANGE = srgb("#D77757")
M_CLAWD = principled("clawd", ORANGE, 0.5, **{"Subsurface Weight": 0.05, "Subsurface Radius": (1.0, 0.35, 0.2),
                                                  "Subsurface Scale": 0.03, "Coat Weight": 0.06, "Coat Roughness": 0.4,
                                                  "Specular IOR Level": 0.45})
_cb = M_CLAWD.node_tree.nodes["Principled BSDF"]

def node_math(nt, op, a, b=None):
    n = nt.nodes.new("ShaderNodeMath"); n.operation = op
    for i, v in enumerate((a, b)):
        if v is None: continue
        if isinstance(v, (int, float)): n.inputs[i].default_value = v
        else: nt.links.new(v, n.inputs[i])
    return n.outputs[0]

def noise(nt, scale, detail=4.0, rough=0.55, coord="Object"):
    tc = nt.nodes.new("ShaderNodeTexCoord"); nz = nt.nodes.new("ShaderNodeTexNoise")
    nz.inputs["Scale"].default_value = scale; nz.inputs["Detail"].default_value = detail
    nz.inputs["Roughness"].default_value = rough
    nt.links.new(tc.outputs[coord], nz.inputs["Vector"])
    return nz.outputs["Fac"]

def remap(nt, v, a, b, c, d):
    mr = nt.nodes.new("ShaderNodeMapRange"); nt.links.new(v, mr.inputs["Value"])
    mr.inputs["From Min"].default_value = a; mr.inputs["From Max"].default_value = b
    mr.inputs["To Min"].default_value = c; mr.inputs["To Max"].default_value = d
    return mr.outputs[0]

def micro_surface(mat, rough=(0.42, 0.58), rscale=9.0, bump_scale=420.0, bump=0.06, smudge=0.0):
    """Soft-touch plastic / satin surfaces: roughness that wanders a little, a fine grain in the
    normal, and optional faint smudges (glossier patches)."""
    nt = mat.node_tree; b = nt.nodes["Principled BSDF"]
    r = remap(nt, noise(nt, rscale), 0.35, 0.65, rough[0], rough[1])
    if smudge:
        sm = remap(nt, noise(nt, rscale * 0.6, 2.0, 0.5), 0.55, 0.75, 0.0, smudge)
        r = node_math(nt, "SUBTRACT", r, sm)
    nt.links.new(r, b.inputs["Roughness"])
    bp = nt.nodes.new("ShaderNodeBump"); bp.inputs["Strength"].default_value = bump
    bp.inputs["Distance"].default_value = 0.002
    nt.links.new(noise(nt, bump_scale, 2.0, 0.5), bp.inputs["Height"])
    nt.links.new(bp.outputs[0], b.inputs["Normal"])

micro_surface(M_CLAWD, (0.44, 0.58), 6.0, 380.0, 0.1)
_cb.inputs["Emission Color"].default_value = ORANGE; _cb.inputs["Emission Strength"].default_value = 0.0
M_EYE = principled("eye", srgb("#141212"), 0.18, **{"Coat Weight": 0.6, "Coat Roughness": 0.08})
def img_node(nt, path, non_color=False):
    t = nt.nodes.new("ShaderNodeTexImage"); t.image = bpy.data.images.load(path, check_existing=True)
    if non_color: t.image.colorspace_settings.name = "Non-Color"
    return t

def oak_mat(name, tile=4.0):
    """Lacquered light oak veneer (CC0, Poly Haven "oak_veneer_01"), one tile every `tile` metres,
    grain along X, with a satin coat that carries soft reflections and a few faint smudges."""
    m = bpy.data.materials.new(name); m.use_nodes = True
    nt = m.node_tree; b = nt.nodes["Principled BSDF"]
    tc = nt.nodes.new("ShaderNodeTexCoord"); mp = nt.nodes.new("ShaderNodeMapping")
    mp.inputs["Scale"].default_value = (1 / tile, 1 / tile, 1 / tile)
    mp.inputs["Rotation"].default_value = (0, 0, math.radians(90))
    nt.links.new(tc.outputs["Object"], mp.inputs[0])
    d = img_node(nt, asset("tex", "oak_veneer_01_diff_2k.jpg"))
    r = img_node(nt, asset("tex", "oak_veneer_01_rough_2k.jpg"), True)
    n = img_node(nt, asset("tex", "oak_veneer_01_nor_gl_2k.jpg"), True)
    for t in (d, r, n): nt.links.new(mp.outputs[0], t.inputs[0])
    # a touch warmer and lighter than the scan
    hsv = nt.nodes.new("ShaderNodeHueSaturation"); hsv.inputs["Saturation"].default_value = 0.5
    hsv.inputs["Value"].default_value = 1.35
    nt.links.new(d.outputs[0], hsv.inputs["Color"]); nt.links.new(hsv.outputs[0], b.inputs["Base Color"])
    rr = remap(nt, r.outputs[0], 0.0, 1.0, 0.38, 0.62)
    sm = remap(nt, noise(nt, 0.35, 3.0, 0.6), 0.55, 0.72, 0.0, 0.14)   # faint wipe marks
    nt.links.new(node_math(nt, "SUBTRACT", rr, sm), b.inputs["Roughness"])
    nm = nt.nodes.new("ShaderNodeNormalMap"); nm.inputs["Strength"].default_value = 0.6
    nt.links.new(n.outputs[0], nm.inputs["Color"]); nt.links.new(nm.outputs[0], b.inputs["Normal"])
    b.inputs["Coat Weight"].default_value = 0.35; b.inputs["Coat Roughness"].default_value = 0.22
    return m

M_FLOOR = oak_mat("desk_oak")
M_LAPTOP = principled("laptop", srgb("#AFB4BE"), 0.32, **{"Metallic": 0.8})
M_LAPTOP_DARK = principled("laptop_dark", srgb("#787D87"), 0.35, **{"Metallic": 0.8})
M_LOGO = principled("logo", srgb("#EBEEF5"), 0.2, **{"Emission Color": srgb("#EBEEF5"), "Emission Strength": 0.4})
def liquid_glass(name, frost=0.2, rim=0.012, tint="#F6F7FA"):
    """Liquid-Glass-like slab: clear refractive glass (IOR 1.5) whose flat faces are frosted, so
    what's behind is blurred and refracted, while the bevelled rim stays clear and glossy and
    catches bright highlights and a little lensing. A faint cool tint, no milky body."""
    m = bpy.data.materials.new(name); m.use_nodes = True
    nt = m.node_tree; b = nt.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = srgb(tint)
    b.inputs["Transmission Weight"].default_value = 1.0
    b.inputs["IOR"].default_value = 1.5
    b.inputs["Specular IOR Level"].default_value = 0.6
    b.inputs["Coat IOR"].default_value = 1.5
    b.inputs["Coat Roughness"].default_value = 0.015
    tc = nt.nodes.new("ShaderNodeTexCoord"); sp = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(tc.outputs["Normal"], sp.inputs[0])
    flat = remap(nt, node_math(nt, "ABSOLUTE", sp.outputs[1]), 0.93, 0.995, 0.0, 1.0)   # 1 on the faces, 0 on the rim
    nt.links.new(remap(nt, flat, 0.0, 1.0, rim, frost), b.inputs["Roughness"])
    nt.links.new(remap(nt, flat, 0.0, 1.0, 1.0, 0.18), b.inputs["Coat Weight"])
    for attr, val in [("use_raytrace_refraction", True), ("surface_render_method", "DITHERED"), ("use_transparent_shadow", True)]:
        if hasattr(m, attr):
            try: setattr(m, attr, val)
            except Exception: pass
    return m

M_PANEL_GLASS = liquid_glass("panel_glass", 0.24)
M_CARD_GLASS = liquid_glass("card_glass", 0.2)
DENSITY = 2.0  # captures are @2x: 2 px per point

class UIImage:
    """A UI image cropped to the inside of its window: the capture's shadow margin, popover arrow
    and its own 1-2 px rim are cut away, and the corner radius is measured from the alpha, so the
    glass slab can match it exactly (one edge, the slab's). `whole` keeps the full image
    (transparent-background art such as the agent chips) as a capsule."""
    def __init__(self, path, whole=False, inset_pt=1.5):
        import numpy as np
        self.path = path
        self.image = bpy.data.images.load(path, check_existing=True)
        w, h = self.image.size
        self.size = (w, h)
        a = np.empty(w * h * 4, dtype=np.float32); self.image.pixels.foreach_get(a)
        a = a.reshape(h, w, 4)   # bottom-up rows, sRGB-encoded
        if whole:
            self.x0, self.x1, self.y0, self.y1 = 0, w, 0, h
            self.r_px = h / 2
        else:
            # the window's bounds: rows/columns that are substantially opaque (drops the popover
            # arrow and soft shadow, keeps the rounded corners' first and last columns)
            opaque = a[:, :, 3] > 0.5
            rc, cc = opaque.sum(axis=1), opaque.sum(axis=0)
            rows = np.where(rc > 0.2 * rc.max())[0]
            cols = np.where(cc > 0.2 * cc.max())[0]
            x0, x1 = int(cols.min()), int(cols.max()) + 1
            y0, y1 = int(rows.min()), int(rows.max()) + 1                       # bottom-up
            # corner radius from the top-left corner's diagonal: d = r (1 - 1/sqrt2)
            d = 0
            while d < min(x1 - x0, y1 - y0) // 2 and a[y1 - 1 - d, x0 + d, 3] < 0.5:
                d += 1
            r = d / (1 - 1 / math.sqrt(2))
            k = int(round(inset_pt * DENSITY))
            self.x0, self.x1, self.y0, self.y1 = x0 + k, x1 - k, y0 + k, y1 - k
            self.r_px = max(r - k, 2.0)
        self.w_px, self.h_px = self.x1 - self.x0, self.y1 - self.y0
        self.r_pt = self.r_px / DENSITY
        def common(reg):
            q = np.round(reg.reshape(-1, 3) * 50).astype(int)
            vals, counts = np.unique(q, axis=0, return_counts=True)
            c = vals[counts.argmax()] / 50.0
            return srgb("#%02x%02x%02x" % tuple(int(round(x * 255)) for x in c))
        # the window's flat backgrounds: conversation area (right half) and sidebar (left eighth)
        self.bg = common(a[self.y0:self.y1, (self.x0 + self.x1) // 2:self.x1, :3])
        self.bg2 = common(a[self.y0:self.y1, self.x0:self.x0 + max(self.w_px // 8, 1), :3])
        # the window alone, top-down rows, for measuring content (rows of text, typed text, ...)
        self.win = a[self.y0:self.y1, self.x0:self.x1, :3][::-1]

    def uv_rect(self):
        w, h = self.size
        return (self.x0 / w, self.y0 / h, self.x1 / w, self.y1 / h)

KEY_FULL = 0.5   # linear-RGB distance from the background at which content is fully opaque

# ----------------------------------------------------------------------------- measuring captures
# Positions inside the captures are measured from the pixels, per language, so a translated or
# re-taken capture needs no hand-tuned numbers. All results are window fractions (u from the left,
# v from the top), like layout.json.
def _mode(px):
    import numpy as np
    q = np.round(px.reshape(-1, 3) * 50).astype(int)
    vals, counts = np.unique(q, axis=0, return_counts=True)
    return vals[counts.argmax()] / 50.0

def ink_bands(ui, u0, u1, v0, v1, thr=0.25):
    """The rows of content (text lines, icons) inside a part of the window: [(vtop, vbottom)]."""
    import numpy as np
    H, W = ui.win.shape[:2]
    y0, y1, x0, x1 = int(v0 * H), int(math.ceil(v1 * H)), int(u0 * W), int(math.ceil(u1 * W))
    reg = ui.win[y0:y1, x0:x1]
    ink = np.where((np.abs(reg - _mode(reg)).max(axis=2) > thr).any(axis=1))[0]
    bands = []
    for r in ink:
        if bands and r <= bands[-1][1] + 1: bands[-1][1] = r
        else: bands.append([r, r])
    return [((y0 + a) / H, (y0 + b + 1) / H) for a, b in bands]

def reveal_cuts(bands, v_end):
    """Where a top-down reveal stops so each row appears whole: halfway into the gap below it."""
    cuts = [(a[1] + b[0]) / 2 for a, b in zip(bands, bands[1:])]
    if bands: cuts.append(min(bands[-1][1] + 0.006, v_end))
    return cuts

def measure_typing(before, after, u_min):
    """Text typed into a field: `after` is the same capture as `before` with the text typed in.
    Returns the field's text region, where each typed glyph ends (the reveal stops), the caret
    (if the capture shows one), a blank column of the field, and a send button that appears with
    the text (if any)."""
    import numpy as np
    A, B = before.win, after.win
    H, W = A.shape[:2]
    diff = np.abs(A - B).max(axis=2) > 0.12
    diff[:, :int(u_min * W) + 4] = False          # the sidebar's spinners also differ
    ys, xs = np.where(diff)
    groups = []
    for c in np.unique(xs):
        if groups and c - groups[-1][1] <= 40: groups[-1][1] = c
        else: groups.append([c, c])
    send = None
    if len(groups) > 1 and groups[-1][0] - groups[-2][1] > 80:   # far right: the send button
        g = groups.pop(); sel = (xs >= g[0]) & (xs <= g[1])
        send = ((g[0] - 3) / W, (ys[sel].min() - 3) / H, (g[1] + 4) / W, (ys[sel].max() + 4) / H)
    tx0, tx1 = groups[0][0], groups[-1][1]
    sel = (xs >= tx0) & (xs <= tx1)
    ty0, ty1 = ys[sel].min(), ys[sel].max() + 1
    bx = tx1 + 40 if send is None else min(tx1 + 40, int(send[0] * W) - 12)
    rows = B[ty0:ty1, tx0:tx1 + 1]
    ink = (np.abs(rows - B[ty0:ty1, bx:bx + 1]).max(axis=2) > 0.15).any(axis=0)
    # the caret: a thin blue bar at the end of the typed text (a capture may catch it mid-blink,
    # pale; it's drawn in full, in the accent colour, wherever the typing has got to)
    bl = (rows[:, :, 2] - rows[:, :, 0] > 0.18) & (rows[:, :, 2] > 0.55)
    blue = np.where(bl.any(axis=0))[0]
    caret = None
    if len(blue) and blue.max() - blue.min() < 8 and blue.min() > 0.5 * (tx1 - tx0):
        br = np.where(bl.any(axis=1))[0]
        caret = ((tx0 + blue.min()) / W, (tx0 + blue.min() + 2 * DENSITY) / W, (ty0 + br.min()) / H, (ty0 + br.max() + 1) / H)
        ink[max(blue.min() - 1, 0):] = False
    runs = []
    for c in np.where(ink)[0]:
        if runs and c <= runs[-1][1] + 1: runs[-1][1] = c
        else: runs.append([c, c])
    # glyphs: runs grouped until each is about a syllable / letter wide
    min_w = 0.4 * (ty1 - ty0)
    glyphs = []
    for r in runs:
        if glyphs and glyphs[-1][1] - glyphs[-1][0] < min_w: glyphs[-1][1] = r[1]
        else: glyphs.append(list(r))
    stops = [(tx0 + g[1] + 1 + tx0 + n[0]) / 2 / W for g, n in zip(glyphs, glyphs[1:])]
    stops.append(caret[0] if caret else (tx0 + glyphs[-1][1] + 3) / W)
    start = (tx0 + glyphs[0][0] - 1) / W
    region = ((tx0 - 3) / W, (ty0 - 3) / H, (bx - 4) / W, (ty1 + 3) / H)
    return dict(region=region, start=start, stops=stops, caret=caret, blank_u=bx / W, send=send,
                rows=(ty0 / H, ty1 / H))

def measure_scroll(before, after, v0, v1, u_min):
    """How far `after` is scrolled up from `before` (a fraction of the window's height): the
    shift that best lines up the rows of `before` between v0 and v1 with those of `after`."""
    import numpy as np
    H, W = before.win.shape[:2]
    g = lambda a: a[:, int(u_min * W) + 4::3].mean(axis=2)
    A, B = g(before.win), g(after.win)
    y0, y1 = int(v0 * H), int(v1 * H)
    best = (1e9, 0)
    for d in range(0, int(0.45 * H)):
        lo = max(y0, d + int(0.08 * H))
        if y1 - lo < 120: break
        e = float(np.abs(A[lo:y1] - B[lo - d:y1 - d]).mean())
        best = min(best, (e, d))
    return best[1] / H

CARET_COL = srgb("#3F92F7")   # the text caret, in the system accent blue (as in the ko capture)

def ui_material(name, ui, mask=None, emit=1.0, crop=None, key=None, radius_pt=None, dim=None, key_bg=None, key_full=None,
                veil_color=None, typing=None, scroll=False, notch=None):
    """Self-lit UI image, printed on the front of a glass slab. UVs run 0..1 over the window (or
    over `crop`, window fractions from the top-left). A rounded-rect mask with the slab's own
    radius trims the edge, `key` makes the window's flat backgrounds see-through (so the frosted
    glass shows), and `mask` adds the row-by-row reveal (see layout.json).
    `typing` (from measure_typing, plus the typed capture as "image") types text into a field of
    this face: one layer, glyph by glyph up to the "rcut" value, with the caret in front.
    `scroll` lets this face scroll its content up into place (the "scroll" value, a fraction of
    the window's height), as a terminal does when new output arrives."""
    m = bpy.data.materials.new(name); m.use_nodes = True
    nt = m.node_tree; N = nt.nodes; L = nt.links
    for n in list(N): N.remove(n)
    def math(op, a, b=None):
        n = N.new("ShaderNodeMath"); n.operation = op
        for i, v in enumerate((a, b)):
            if v is None: continue
            if isinstance(v, (int, float)): n.inputs[i].default_value = v
            else: L.new(v, n.inputs[i])
        return n.outputs[0]
    def value(nm, v):
        n = N.new("ShaderNodeValue"); n.name = nm; n.outputs[0].default_value = v
        return n.outputs[0]
    def mix(f, a, b, rgba=False):
        n = N.new("ShaderNodeMix"); n.data_type = "RGBA" if rgba else "FLOAT"
        k = (6, 7) if rgba else (2, 3)
        L.new(f, n.inputs[0])
        for i, v in zip(k, (a, b)):
            if isinstance(v, (int, float)): n.inputs[i].default_value = v
            elif isinstance(v, tuple): n.inputs[i].default_value = v
            else: L.new(v, n.inputs[i])
        return n.outputs[2 if rgba else 0]
    def in_rect(uu, vv, r):
        return math("MULTIPLY", math("MULTIPLY", math("GREATER_THAN", uu, r[0]), math("LESS_THAN", uu, r[2])),
                    math("MULTIPLY", math("GREATER_THAN", vv, r[1]), math("LESS_THAN", vv, r[3])))
    out = N.new("ShaderNodeOutputMaterial")
    tc = N.new("ShaderNodeTexCoord")
    u0, v0, u1, v1 = ui.uv_rect()
    if crop:   # crop = (u0, vtop0, u1, vtop1) inside the window
        cu0, ct0, cu1, ct1 = crop
        u0, u1 = lerp(u0, u1, cu0), lerp(u0, u1, cu1)
        v0, v1 = lerp(v1, v0, ct1), lerp(v1, v0, ct0)
    sep = N.new("ShaderNodeSeparateXYZ"); L.new(tc.outputs["UV"], sep.inputs[0])
    u = sep.outputs[0]
    vt = math("SUBTRACT", 1.0, sep.outputs[1])  # v from the top
    def sample(image, uu, vv):
        cb = N.new("ShaderNodeCombineXYZ"); L.new(uu, cb.inputs[0]); L.new(math("SUBTRACT", 1.0, vv), cb.inputs[1])
        mp = N.new("ShaderNodeMapping"); mp.vector_type = "POINT"
        mp.inputs["Location"].default_value = (u0, v0, 0)
        mp.inputs["Scale"].default_value = (u1 - u0, v1 - v0, 1)
        L.new(cb.outputs[0], mp.inputs["Vector"])
        img = N.new("ShaderNodeTexImage"); img.image = image
        img.interpolation = "Linear"; img.extension = "CLIP"; img.image.alpha_mode = "STRAIGHT"
        L.new(mp.outputs[0], img.inputs["Vector"])
        return img.outputs["Color"], img.outputs["Alpha"]
    vsrc = vt
    if scroll:
        # the content between the header and the composer is drawn from further up the image
        content = math("MULTIPLY", math("MULTIPLY", math("GREATER_THAN", vt, mask["header_v"]), math("LESS_THAN", vt, mask["composer_v"])),
                       math("GREATER_THAN", u, mask["sidebar_u"]))
        vsrc = math("SUBTRACT", vt, math("MULTIPLY", value("scroll", 0.0), content))
    col, a_img = sample(ui.image, u, vsrc)
    if typing:
        # the field shows `before` until typing starts; then the typed capture up to rcut, the caret
        # sampled from the typed capture's own caret just right of it, and a blank field beyond
        rcut = value("rcut", typing["start"]); tflag = value("typing", 0.0)
        usrc = mix(math("LESS_THAN", u, rcut), typing["blank_u"], u)
        use = math("MULTIPLY", in_rect(u, vt, typing["region"]), tflag)
        if typing["send"]:
            ins = math("MULTIPLY", in_rect(u, vt, typing["send"]), value("send_on", 0.0))
            usrc = mix(ins, usrc, u)
            use = math("MAXIMUM", use, ins)
        colb, ab = sample(typing["image"].image, usrc, vt)
        col = mix(use, col, colb, rgba=True); a_img = mix(use, a_img, ab)
        if typing["caret"]:
            c0, c1, cv0, cv1 = typing["caret"]
            inc = math("MULTIPLY", math("MULTIPLY", math("LESS_THAN", rcut, math("ADD", u, 1e-5)), math("LESS_THAN", u, math("ADD", rcut, c1 - c0))),
                       math("MULTIPLY", math("GREATER_THAN", vt, cv0), math("LESS_THAN", vt, cv1)))
            col = mix(math("MULTIPLY", inc, tflag), col, CARET_COL, rgba=True)
    em = N.new("ShaderNodeEmission"); em.inputs[1].default_value = emit
    tr = N.new("ShaderNodeBsdfTransparent")
    mixs = N.new("ShaderNodeMixShader")
    fade = value("fade", 1.0)
    alpha = math("MULTIPLY", a_img, fade)
    # rounded-rect edge in pixels: the same radius as the slab, so there is a single edge
    wpx = (u1 - u0) * ui.size[0]; hpx = (v1 - v0) * ui.size[1]
    r = (radius_pt if radius_pt is not None else ui.r_pt) * DENSITY
    qx = math("SUBTRACT", math("ABSOLUTE", math("MULTIPLY", math("SUBTRACT", u, 0.5), wpx)), wpx / 2 - r)
    qy = math("SUBTRACT", math("ABSOLUTE", math("MULTIPLY", math("SUBTRACT", sep.outputs[1], 0.5), hpx)), hpx / 2 - r)
    qx = math("MAXIMUM", qx, 0.0); qy = math("MAXIMUM", qy, 0.0)
    dist = math("SUBTRACT", math("SQRT", math("ADD", math("MULTIPLY", qx, qx), math("MULTIPLY", qy, qy))), r)
    edge = N.new("ShaderNodeMapRange")
    L.new(dist, edge.inputs["Value"])
    edge.inputs["From Min"].default_value = -2.5; edge.inputs["From Max"].default_value = -1.0
    edge.inputs["To Min"].default_value = 1.0; edge.inputs["To Max"].default_value = 0.0
    edge = edge.outputs[0]
    if notch:   # where the popover's arrow joins the bottom edge, the face runs right up to it
        edge = math("MAXIMUM", edge, math("MULTIPLY", math("LESS_THAN", math("ABSOLUTE", math("SUBTRACT", u, 0.5)), notch),
                                          math("LESS_THAN", sep.outputs[1], 0.5)))
    alpha = math("MULTIPLY", alpha, edge)
    if mask:
        cut = value("cut", 1.0); hole = value("hole", 0.0)
        # rows are revealed down to `cut` (in the image's own rows, so they scroll with it), with
        # a soft edge a few pixels tall
        mr = N.new("ShaderNodeMapRange"); mr.interpolation_type = "SMOOTHSTEP"
        L.new(vsrc, mr.inputs["Value"])
        L.new(math("SUBTRACT", cut, 0.006), mr.inputs["From Min"]); L.new(cut, mr.inputs["From Max"])
        mr.inputs["To Min"].default_value = 1.0; mr.inputs["To Max"].default_value = 0.0
        revealed = mr.outputs[0]
        if scroll:   # nothing above the image's own top while it scrolls into place
            revealed = math("MULTIPLY", revealed, math("GREATER_THAN", vsrc, mask["header_v"]))
        if "permission_rect" in mask:
            inside = in_rect(u, vt, mask["permission_rect"])
            revealed = math("MULTIPLY", revealed, math("SUBTRACT", 1.0, math("MULTIPLY", inside, hole)))
        always = math("MAXIMUM", math("LESS_THAN", u, mask["sidebar_u"]),
                      math("MAXIMUM", math("LESS_THAN", vt, mask["header_v"]), math("GREATER_THAN", vt, mask["composer_v"])))
        show = math("MAXIMUM", always, revealed)
        if "hide_rect" in mask:
            show = math("MULTIPLY", show, math("SUBTRACT", 1.0, in_rect(u, vt, mask["hide_rect"])))
        col = mix(show, ui.bg, col, rgba=True)
    if dim:
        # a rounded rect (u0, vtop0, u1, vtop1 in this face's UVs, radius in px) that darkens by
        # the "press" value: the recess left behind when a button on its own layer is pressed in
        du0, dv0, du1, dv1, dr = dim
        cxp, cyp = (du0 + du1) / 2 * wpx, (dv0 + dv1) / 2 * hpx
        hw, hh = (du1 - du0) / 2 * wpx - dr, (dv1 - dv0) / 2 * hpx - dr
        qx = math("MAXIMUM", math("SUBTRACT", math("ABSOLUTE", math("SUBTRACT", math("MULTIPLY", u, wpx), cxp)), hw), 0.0)
        qy = math("MAXIMUM", math("SUBTRACT", math("ABSOLUTE", math("SUBTRACT", math("MULTIPLY", vt, hpx), cyp)), hh), 0.0)
        sd = math("SUBTRACT", math("SQRT", math("ADD", math("MULTIPLY", qx, qx), math("MULTIPLY", qy, qy))), dr)
        din = N.new("ShaderNodeMapRange"); L.new(sd, din.inputs["Value"])
        din.inputs["From Min"].default_value = -1.0; din.inputs["From Max"].default_value = 1.0
        din.inputs["To Min"].default_value = 1.0; din.inputs["To Max"].default_value = 0.0
        dm = N.new("ShaderNodeMix"); dm.data_type = "RGBA"; dm.blend_type = "MULTIPLY"
        L.new(math("MULTIPLY", din.outputs[0], value("press", 0.0)), dm.inputs["Factor"])
        L.new(col, dm.inputs["A"]); dm.inputs["B"].default_value = (0.35, 0.38, 0.5, 1)
        col = dm.outputs["Result"]
    if key:
        # Difference key against the window's two flat backgrounds (content area, sidebar), with
        # colour decontamination: a pixel C over background B is a*F + (1-a)*B, a from its
        # distance to B. What's left is the content F at coverage a, over a thin veil of B at
        # opacity v (key) that keeps text contrast over a busy scene:
        #   alpha = a + (1-a) v,   colour = (C - (1-a)(1-v) B) / alpha
        def dist(c):
            d = N.new("ShaderNodeVectorMath"); d.operation = "DISTANCE"
            L.new(col, d.inputs[0]); d.inputs[1].default_value = c[:3]
            return d.outputs["Value"]
        kb1, kb2 = key_bg or (ui.bg, ui.bg2)
        d1, d2 = dist(kb1), dist(kb2)
        if mask and "sidebar_u" in mask:
            # which background a pixel sits on is where it is, not its colour: grey text on the
            # white content area would otherwise be keyed against the grey sidebar and thin out
            side = math("LESS_THAN", u, mask["sidebar_u"])
            dmin = mix(side, d1, d2)
        else:
            side = math("LESS_THAN", d2, d1)
            dmin = math("MINIMUM", d1, d2)
        ka = N.new("ShaderNodeMapRange"); ka.clamp = True
        L.new(dmin, ka.inputs["Value"])
        ka.inputs["From Min"].default_value = 0.012; ka.inputs["From Max"].default_value = key_full or KEY_FULL
        a_c = ka.outputs[0]
        bsel = N.new("ShaderNodeMix"); bsel.data_type = "RGBA"
        L.new(side, bsel.inputs["Factor"]); bsel.inputs["A"].default_value = kb1; bsel.inputs["B"].default_value = kb2
        veil = math("ADD", key[0], math("MULTIPLY", side, key[1] - key[0]))
        one_a = math("SUBTRACT", 1.0, a_c)
        A = math("ADD", a_c, math("MULTIPLY", one_a, veil))
        # the veil can have its own colour (cards captured over a grey vibrancy background get a
        # light one):  colour = (C - (1-a) B + (1-a) v V) / alpha
        sB = N.new("ShaderNodeVectorMath"); sB.operation = "SCALE"
        L.new(bsel.outputs["Result"], sB.inputs[0]); L.new(one_a, sB.inputs["Scale"])
        sV = N.new("ShaderNodeVectorMath"); sV.operation = "SCALE"
        if veil_color: sV.inputs[0].default_value = veil_color[:3]
        else: L.new(bsel.outputs["Result"], sV.inputs[0])
        L.new(math("MULTIPLY", one_a, veil), sV.inputs["Scale"])
        num0 = N.new("ShaderNodeVectorMath"); num0.operation = "SUBTRACT"
        L.new(col, num0.inputs[0]); L.new(sB.outputs[0], num0.inputs[1])
        num = N.new("ShaderNodeVectorMath"); num.operation = "ADD"
        L.new(num0.outputs[0], num.inputs[0]); L.new(sV.outputs[0], num.inputs[1])
        out_c = N.new("ShaderNodeVectorMath"); out_c.operation = "SCALE"
        L.new(num.outputs[0], out_c.inputs[0]); L.new(math("DIVIDE", 1.0, math("MAXIMUM", A, 0.0005)), out_c.inputs["Scale"])
        clampc = N.new("ShaderNodeMix"); clampc.data_type = "RGBA"; clampc.clamp_result = True
        clampc.inputs["Factor"].default_value = 0.0
        L.new(out_c.outputs[0], clampc.inputs["A"])
        col = clampc.outputs["Result"]
        alpha = math("MULTIPLY", alpha, A)
    L.new(col, em.inputs["Color"])
    L.new(alpha, mixs.inputs[0])
    L.new(tr.outputs[0], mixs.inputs[1]); L.new(em.outputs[0], mixs.inputs[2])
    L.new(mixs.outputs[0], out.inputs[0])
    for attr, val in [("surface_render_method", "BLENDED"), ("use_transparent_shadow", True)]:
        if hasattr(m, attr):
            try: setattr(m, attr, val)
            except Exception: pass
    return m

GLASS_OF = {}   # UI material name -> its own glass material, faded together
PLANES_OF = {}  # material name -> the planes that use it (hidden while faded out: fewer transparent layers to trace)

def set_fade(mat, v):
    mat.node_tree.nodes["fade"].outputs[0].default_value = clamp(v)
    for ob in PLANES_OF.get(mat.name, []):
        if not ob.hide_viewport:   # left alone while set_visible() has hidden it
            ob.hide_render = v <= 0.002
    g = GLASS_OF.get(mat.name)
    if g:
        # glass can't fade cleanly (dithered); things appear and leave by scale instead
        g.node_tree.nodes["Principled BSDF"].inputs["Alpha"].default_value = 1.0 if v > 0.02 else 0.0

# ----------------------------------------------------------------------------- geometry helpers
def box(name, size, loc=(0, 0, 0), mat=None, parent=None, bevel=0.0, segs=3):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts: v.co = Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
    bm.to_mesh(me); bm.free()
    ob = bpy.data.objects.new(name, me); scene.collection.objects.link(ob)
    ob.location = loc
    if mat: me.materials.append(mat)
    if parent: ob.parent = parent
    if bevel > 0:
        md = ob.modifiers.new("bevel", "BEVEL"); md.width = bevel; md.segments = max(segs, 4)
        md.limit_method = "NONE"; md.harden_normals = False
        for p in me.polygons: p.use_smooth = True
        try: me.shade_smooth()
        except Exception: pass
    return ob

def empty(name, loc=(0, 0, 0), parent=None):
    ob = bpy.data.objects.new(name, None); scene.collection.objects.link(ob)
    ob.location = loc
    if parent: ob.parent = parent
    return ob

def arrow_outline(aw, ah, base_z, tip_r, segs=8):
    """A popover arrow pointing down from the edge at base_z, left to right, its tip rounded."""
    th = math.atan2(aw / 2, ah)                  # half the tip's angle
    cz = base_z - ah + tip_r / math.sin(th)      # centre of the tip's rounding
    pts = [(-aw / 2, base_z)]
    for i in range(segs + 1):
        a = math.radians(-180) + th + (math.pi - 2 * th) * i / segs
        pts.append((tip_r * math.cos(a), cz + tip_r * math.sin(a)))
    return pts + [(aw / 2, base_z)]

def rounded_rect_mesh(name, w, h, r, depth=0.0, segs=16, arrow=None):
    """Rounded rectangle in the XZ plane (facing -Y), optionally extruded along +Y. `arrow`
    (width, height, tip radius) adds a popover arrow in the middle of the bottom edge, so the
    outline (and a glass slab's rim) runs around it in one piece."""
    bm = bmesh.new()
    pts = []
    corners = [(w / 2 - r, h / 2 - r, 0), (-w / 2 + r, h / 2 - r, 90), (-w / 2 + r, -h / 2 + r, 180), (w / 2 - r, -h / 2 + r, 270)]
    for k, (cx, cz, a0) in enumerate(corners):
        for i in range(segs + 1):
            a = math.radians(a0 + 90 * i / segs)
            pts.append((cx + r * math.cos(a), cz + r * math.sin(a)))
        if arrow and k == 2:   # the bottom edge runs left to right from here
            pts += arrow_outline(arrow[0], arrow[1], -h / 2, arrow[2])
    verts = [bm.verts.new((x, 0, z)) for x, z in pts]
    face = bm.faces.new(verts)
    if depth > 0:
        ext = bmesh.ops.extrude_face_region(bm, geom=[face])
        for v in [e for e in ext["geom"] if isinstance(e, bmesh.types.BMVert)]:
            v.co.y += depth
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    return me

def ui_plane(name, w, h, mat, parent=None, loc=(0, 0, 0)):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    vs = [bm.verts.new(p) for p in [(-w / 2, 0, -h / 2), (w / 2, 0, -h / 2), (w / 2, 0, h / 2), (-w / 2, 0, h / 2)]]
    f = bm.faces.new(vs)
    uv = bm.loops.layers.uv.new()
    for loop, c in zip(f.loops, [(0, 0), (1, 0), (1, 1), (0, 1)]): loop[uv].uv = c
    bm.normal_update()
    bm.to_mesh(me); bm.free()
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me); scene.collection.objects.link(ob)
    ob.location = loc
    if parent: ob.parent = parent
    ob.visible_shadow = False
    PLANES_OF.setdefault(mat.name, []).append(ob)
    return ob

def glass_slab(name, w, h, r, depth, mat, parent=None, loc=(0, 0, 0), arrow=None):
    me = rounded_rect_mesh(name, w, h, r, depth, arrow=arrow)
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me); scene.collection.objects.link(ob)
    ob.location = loc
    if parent: ob.parent = parent
    md = ob.modifiers.new("bevel", "BEVEL"); md.width = depth * 0.45 if depth >= 0.01 else min(depth * 0.4, 0.0018)
    md.segments = 6 if depth >= 0.01 else 4
    md.limit_method = "ANGLE"
    try: me.shade_smooth_by_angle()
    except Exception:
        try: ob.modifiers.new("smooth", "SMOOTH_BY_ANGLE")
        except Exception: pass
    return ob

# ----------------------------------------------------------------------------- set
# the desk: a lacquered oak top (its top face is z = 0), deep enough to run out of frame
DESK = box("desk", (14.0, 15.0, 0.32), (0, 1.0, -0.16), M_FLOOR, None, 0.04)

# The "Bloom": soft translucent petals in Clawd's oranges, far behind and out of focus.
BLOOM = empty("bloom", (4.3, 6.0, 1.9)); BLOOM.scale = (1.6, 1.6, 1.6)
petal_cols = ["#3B6FE0", "#5C8BF0", "#86A8F7", "#A9C1FA", "#2E5AD0", "#7B98F2", "#C3D2FC", "#4C7AE8",
              "#EFBC9E", "#E58B68", "#F0C2A4", "#D06F52"]
PETALS = []
for i, hx in enumerate(petal_cols):
    _c = [lerp(a, b, 0.15) for a, b in zip(srgb(hx), srgb("#E8E4FA"))]
    # satin petals with a glossy coat (the soft, sculptural backdrop form)
    m = principled(f"petal{i}", tuple(_c), 0.35, **{"Coat Weight": 0.8, "Coat Roughness": 0.08, "Sheen Weight": 0.4,
                                                   "Sheen Tint": (0.85, 0.88, 1.0, 1.0)})
    bpy.ops.mesh.primitive_uv_sphere_add(segments=64, ring_count=32, radius=1.0)
    p = bpy.context.active_object; p.name = f"petal{i}"
    p.data.materials.append(m)
    bpy.ops.object.shade_smooth()
    p.parent = BLOOM
    p.rotation_mode = "ZXY"
    PETALS.append(p)

def petal_pose(t):
    n = len(PETALS)
    for i, p in enumerate(PETALS):
        ang = i / n * 2 * math.pi + 0.08 * math.sin(t * 0.3 + i)
        breathe = 1 + 0.035 * math.sin(t * 0.6 + i * 1.3)
        length = 1.35 + 0.25 * math.sin(i * 2.1)
        p.scale = (0.48 * breathe, 0.10, length * breathe)
        d = length * 0.92
        p.location = (d * math.sin(ang), 0.18 * math.sin(i * 1.7), d * math.cos(ang))
        # radial long axis, each ribbon twisted and leaning toward the camera
        p.rotation_euler = (math.radians(-22 + 8 * math.sin(i * 1.3)), ang, math.radians(35 + 25 * math.sin(i * 0.9 + t * 0.2)))

# ----------------------------------------------------------------------------- Clawd
# Sprite (Sources/Sprite.swift): 18x5 grid, each pixel 1 wide x 2 tall. Body x3..15 rows 0..4,
# claws x1..3 / x15..17 on row 2, legs at x 4,6,11,13 (step: 5,7,10,12), eyes at x 5 and 12 row 1.
U = 0.05
DEPTH = 6 * U
def sx(x): return (x - 9) * U          # sprite x -> world X (centre)
def rz(row): return (10 - 2 * row) * U  # sprite row top -> world Z above the feet

CLAWD = empty("clawd")                  # origin at the feet; squash scales around it
BODY = empty("body", parent=CLAWD)      # bobs with the body
bev = 0.55 * U
box("torso", (12 * U, DEPTH, 8 * U), (sx(9), 0, rz(0) - 4 * U), M_CLAWD, BODY, bev)
ARM_L = box("arm_l", (2 * U, 3 * U, 2 * U), (sx(2), 0, rz(2) - U), M_CLAWD, BODY, bev)
ARM_R = box("arm_r", (2 * U, 3 * U, 2 * U), (sx(16), 0, rz(2) - U), M_CLAWD, BODY, bev)
EYES = []
for ex in (5, 12):
    e = box(f"eye{ex}", (1 * U, 0.6 * U, 2 * U), (sx(ex + 0.5), -DEPTH / 2 - 0.12 * U, rz(1) - U), M_EYE, BODY, 0.18 * U)
    EYES.append((e, ex))
LEGS = []
for i, lx in enumerate((4, 6, 11, 13)):
    for yy in (-1, 1):
        l = box(f"leg{lx}{yy}", (1 * U, 1 * U, 2 * U), (sx(lx + 0.5), yy * (DEPTH / 2 - 0.9 * U), U), M_CLAWD, CLAWD, 0.25 * U)
        LEGS.append((l, i, yy))

# laptop prop (.type activity): seen from behind, screen back with a light logo dot
LAPTOP = empty("laptop", (0, -DEPTH / 2 - 3.4 * U, 0), CLAWD)
box("lap_base", (10 * U, 6 * U, 0.6 * U), (0, -1.0 * U, 0.3 * U), M_LAPTOP_DARK, LAPTOP, 0.2 * U)
LAP_SCREEN = empty("lap_hinge", (0, -3.8 * U, 0.6 * U), LAPTOP)
box("lap_screen", (8 * U, 0.35 * U, 5.2 * U), (0, 0, 2.6 * U), M_LAPTOP, LAP_SCREEN, 0.15 * U)
box("lap_logo", (0.9 * U, 0.1 * U, 0.9 * U), (0, -0.2 * U, 3.0 * U), M_LOGO, LAP_SCREEN, 0.3 * U)
LAP_SCREEN.rotation_euler = (math.radians(-14), 0, 0)

# '!' over the head when an agent waits (star colour from the sprite)
M_STAR = principled("star", srgb("#FFD65A"), 0.35, **{"Emission Color": srgb("#FFD65A"), "Emission Strength": 0.6})
BANG = empty("bang", (0, 0, 0), CLAWD)
box("bang_bar", (1.1 * U, 1.1 * U, 3.2 * U), (0, 0, 2.6 * U), M_STAR, BANG, 0.3 * U)
box("bang_dot", (1.1 * U, 1.1 * U, 1.1 * U), (0, 0, 0.3 * U), M_STAR, BANG, 0.3 * U)

# ----------------------------------------------------------------------------- UI objects
PT = 0.002  # metres per UI point
SLAB = 0.022  # glass thickness: thick enough for a rounded, refracting rim
KEYS = LAYOUT["key"]

def ui_object(name, ui, mat, slab_mat, w, h, r):
    """A glass slab exactly the size and corner radius of its UI, the UI printed on its front."""
    root = empty(name)
    glass_slab(name + "_glass", w, h, r, SLAB, slab_mat, root, (0, 0.0004, 0)).visible_shadow = False
    ui_plane(name + "_face", w, h, mat, root, (0, 0.0, 0))
    return root

def card(name, png, whole=False):
    ui = UIImage(tex_path(png), whole=whole)
    w, h = ui.w_px / DENSITY * PT, ui.h_px / DENSITY * PT
    mat = ui_material(name + "_ui", ui, emit=1.0, key=None if whole else tuple(KEYS["card"]), veil_color=srgb("#F6F7FA"))
    GLASS_OF[mat.name] = g = M_CARD_GLASS.copy()
    root = ui_object(name, ui, mat, g, w, h, ui.r_pt * PT)
    return root, mat, w, h

CARDS = {k: card("card_" + k, f) for k, f in LAYOUT["cards"].items()}
CHIP_FILES = LAYOUT["chips"]
CHIPS = {k: card("chip_" + k, f, whole=True) for k, f in CHIP_FILES.items()}

ARROW_W, ARROW_H, ARROW_TIP_R = 44 * PT, 20 * PT, 3 * PT   # the popover's arrow, pointing down at Clawd

def panel(name, faces):
    """Popover-sized glass slab carrying one or more UI faces (cross-faded). A face whose layout
    names `typed` captures types its text in (see measure_typing); one with `scroll_from` scrolls
    up from that face's typed capture (see measure_scroll)."""
    uis = {k: UIImage(tex_path(*LAYOUT[k]["files"])) for k in faces}
    first = uis[faces[0]]
    w = first.w_px / DENSITY * PT
    h = first.h_px / DENSITY * PT
    root = empty(name)
    mats, typing = [], {}
    for i, k in enumerate(faces):
        lay, ui = LAYOUT[k], uis[k]
        ty = None
        if "typed" in lay:
            tui = UIImage(tex_path(*lay["typed"]))
            ty = typing[k] = dict(measure_typing(ui, tui, lay["sidebar_u"]), image=tui)
            print("typing", k, "region", [round(x, 4) for x in ty["region"]], "glyphs", len(ty["stops"]),
                  "caret", ty["caret"] is not None, "send", ty["send"] is not None)
        m = ui_material(f"{name}_{k}_ui", ui, mask=lay, emit=1.0, key=tuple(KEYS["panel"]), typing=ty,
                        scroll="scroll_from" in lay, notch=ARROW_W / 2 / w)
        ui_plane(f"{name}_{k}_face", w, h, m, root, (0, -0.0003 * i, 0))
        mats.append(m)
    glass_slab(name + "_glass", w, h, first.r_pt * PT, SLAB, M_PANEL_GLASS, root, (0, 0.0004, 0),
               arrow=(ARROW_W, ARROW_H, ARROW_TIP_R)).visible_shadow = False
    # the arrow's own veil: the window's background at the panel key's opacity, like the face above it
    am = bpy.data.materials.new(name + "_arrow_face"); am.use_nodes = True
    an = am.node_tree; [an.nodes.remove(n) for n in list(an.nodes)]
    ao = an.nodes.new("ShaderNodeOutputMaterial"); ae = an.nodes.new("ShaderNodeEmission"); ae.inputs[0].default_value = first.bg
    at = an.nodes.new("ShaderNodeBsdfTransparent"); amx = an.nodes.new("ShaderNodeMixShader")
    amx.inputs[0].default_value = KEYS["panel"][0]
    an.links.new(at.outputs[0], amx.inputs[1]); an.links.new(ae.outputs[0], amx.inputs[2]); an.links.new(amx.outputs[0], ao.inputs[0])
    try: am.surface_render_method = "BLENDED"
    except Exception: pass
    bm = bmesh.new()
    vs = [bm.verts.new((x, 0, z)) for x, z in arrow_outline(ARROW_W, ARROW_H, -h / 2, ARROW_TIP_R)]
    bm.faces.new(vs); bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name + "_arrow_face"); bm.to_mesh(me); bm.free(); me.materials.append(am)
    if me.polygons[0].normal.y > 0: me.flip_normals()
    ob = bpy.data.objects.new(name + "_arrow_face", me); scene.collection.objects.link(ob); ob.parent = root
    ob.visible_shadow = False
    return root, mats, w, h, uis, typing

def auto_steps(ui, lay, v_from=None):
    """layout "reveal_steps_v": "auto": a stop under each row of content between v_from (default
    the header) and the composer."""
    v0 = lay["header_v"] if v_from is None else v_from
    bands = ink_bands(ui, lay["sidebar_u"] + 0.005, 1.0, v0, lay["composer_v"] - 0.004)
    return reveal_cuts(bands, lay["composer_v"])

FACES = ["chat_permission", "chat_question", "chat_replied", "chat_timeline", "terminal", "terminal_after"]
CHAT, _mats, CW, CH, _uis, TYPING = panel("chat", FACES)
CHAT_UIS = [_uis[k] for k in FACES]
FACE = dict(zip(FACES, _mats))
PERMVIEW_MAT, TIMELINE_MAT, TERM_MAT, AFTER_MAT = FACE["chat_permission"], FACE["chat_timeline"], FACE["terminal"], FACE["terminal_after"]
TW, TH = CW, CH
def _steps(k):
    v = LAYOUT[k]["reveal_steps_v"]
    return auto_steps(_uis[k], LAYOUT[k]) if v == "auto" else v
CHAT_STEPS, TERM_STEPS = _steps("chat_timeline"), _steps("terminal")
# after Enter the terminal scrolls up by as much as its new capture is scrolled, and the new rows
# (everything under the submitted prompt) appear one by one
_ta = LAYOUT["terminal_after"]; _typed = TYPING[_ta["scroll_from"]]
SCROLL_D = measure_scroll(_typed["image"], _uis["terminal_after"], LAYOUT["terminal"]["header_v"], _typed["rows"][0] - 0.01,
                          _ta["sidebar_u"])
_tl = LAYOUT["terminal"]
_hist = ink_bands(_typed["image"], _tl["sidebar_u"] + 0.005, 1.0, _tl["header_v"], _typed["rows"][0] - 0.005)[-1]
_abands = ink_bands(_uis["terminal_after"], _ta["sidebar_u"] + 0.005, 1.0, _ta["header_v"], _ta["composer_v"] - 0.004)
_i = min(range(len(_abands)), key=lambda i: abs(sum(_abands[i]) / 2 - (sum(_hist) / 2 - SCROLL_D)))
_cuts = reveal_cuts(_abands, _ta["composer_v"])
AFTER_START, AFTER_STEPS = _cuts[_i + 1], _cuts[_i + 2:]   # the submitted prompt shows at once, then the rest
print("reveal rows: timeline", len(CHAT_STEPS), "terminal", len(TERM_STEPS), "after", len(AFTER_STEPS),
      "scroll", round(SCROLL_D * CH, 3), "m")

def measure_prompt(ui, pr, pad_u=0.058):
    """The permission prompt's own box inside the permission area `pr` (window fractions), just
    inside its border, cut `pad_u` right of its longest line (the rest of the box is empty)."""
    import numpy as np
    H, W = ui.win.shape[:2]
    x0, y0, x1, y1 = int(pr[0] * W), int(pr[1] * H), int(math.ceil(pr[2] * W)), int(math.ceil(pr[3] * H))
    reg = ui.win[y0:y1, x0:x1]
    off = np.abs(reg - _mode(ui.win[:, W // 2:])).max(axis=2) > 0.008   # not the window's white
    rows = np.where(off.mean(axis=1) > 0.6)[0]
    cols = np.where(off[rows.min():rows.max() + 1].mean(axis=0) > 0.6)[0]
    by0, by1, bx0, bx1 = rows.min() + 2, rows.max() - 1, cols.min() + 2, cols.max() - 1
    box = reg[by0:by1, bx0:bx1]
    ink = np.where((np.abs(box - _mode(box)).max(axis=2) > 0.25).any(axis=0))[0]
    right = min((x0 + bx0 + ink.max()) / W + pad_u, (x0 + bx1) / W)
    return ((x0 + bx0) / W, (y0 + by0) / H, right, (y0 + by1) / H)

# the permission prompt, lifted out of the chat as its own glass layer (cropped from the same image)
PR = LAYOUT["chat_permission"]["permission_rect"]     # hole left in the panel (the prompt and its header)
LR = LAYOUT["chat_permission"]["lift_rect"]           # what lifts out (the prompt's content)
if LR == "auto": LR = measure_prompt(CHAT_UIS[0], PR)
print("lift_rect", LANG, [round(x, 4) for x in LR])
pw, ph = (LR[2] - LR[0]) * CW, (LR[3] - LR[1]) * CH
PERM = empty("perm", parent=CHAT)
PERM_R = 10
def measure_button(ui, lift):
    """The filled blue button (허용 / Allow) inside `lift` (window fractions) of this language's
    capture: its rect as fractions of `lift` (u from the left, v from the top), its corner radius
    in points, and the prompt's own background colour beside it."""
    import numpy as np
    w, h = ui.size
    a = np.array(ui.image.pixels[:], dtype=np.float32).reshape(h, w, 4)[::-1]   # top-down rows
    X0, X1, Y0, Y1 = ui.x0, ui.x1, h - ui.y1, h - ui.y0
    lx0, lx1 = X0 + lift[0] * (X1 - X0), X0 + lift[2] * (X1 - X0)
    ly0, ly1 = Y0 + lift[1] * (Y1 - Y0), Y0 + lift[3] * (Y1 - Y0)
    ix0, iy0 = int(math.floor(lx0)), int(math.floor(ly0))
    reg = a[iy0:int(math.ceil(ly1)), ix0:int(math.ceil(lx1)), :3]
    blue = (reg[:, :, 2] > 0.7) & (reg[:, :, 0] < 0.5) & (reg[:, :, 2] - reg[:, :, 0] > 0.3)
    rs, cs = blue.sum(1), blue.sum(0)
    rows = np.where(rs > 0.3 * rs.max())[0]; cols = np.where(cs > 0.3 * cs.max())[0]
    # the first (leftmost) run of columns: the filled button, not anything blue further right
    run_end = cols[0]
    while run_end + 1 in set(cols.tolist()): run_end += 1
    bx0, bx1, by0, by1 = cols[0], run_end + 1, rows.min(), rows.max() + 1
    d = 0
    while d < (by1 - by0) // 2 and not blue[by0 + d, bx0 + d]: d += 1
    r_px = d / (1 - 1 / math.sqrt(2))
    # the prompt's own colour all around the button, a few pixels out
    ring = np.zeros(blue.shape, bool)
    ring[max(by0 - 6, 0):by1 + 6, max(bx0 - 6, 0):bx1 + 6] = True
    ring[max(by0 - 3, 0):by1 + 3, max(bx0 - 3, 0):bx1 + 3] = False
    c = np.median(reg[ring], axis=0)
    lw, lh = lx1 - lx0, ly1 - ly0
    fx0, fy0 = ix0 - lx0, iy0 - ly0
    rect = ((bx0 + fx0) / lw, (by0 + fy0) / lh, (bx1 + fx0) / lw, (by1 + fy0) / lh)
    return rect, r_px / DENSITY, srgb("#%02x%02x%02x" % tuple(int(round(x * 255)) for x in c)), (1.0 / lw, 1.0 / lh)

# 허용 / Allow, measured from this language's own capture (its width differs per language)
BTN, BTN_R, BTN_BG, (_pxu, _pxv) = measure_button(CHAT_UIS[0], LR)
print("button_rect", LANG, [round(x, 4) for x in BTN], "r", round(BTN_R, 1))
PERM_MAT = ui_material("perm_ui", CHAT_UIS[0], crop=LR, emit=1.0, key=tuple(KEYS["permission"]), radius_pt=PERM_R,
                       key_bg=(BTN_BG, BTN_BG), key_full=0.06,
                       dim=(BTN[0], BTN[1], BTN[2], BTN[3], BTN_R * DENSITY))
GLASS_OF[PERM_MAT.name] = M_CARD_GLASS.copy()
glass_slab("perm_glass", pw, ph, PERM_R * PT, SLAB, GLASS_OF[PERM_MAT.name], PERM, (0, 0.0004, 0)).visible_shadow = False
ui_plane("perm_face", pw, ph, PERM_MAT, PERM, (0, 0.0, 0))
PERM_W, PERM_H = pw, ph
PERM_HOME = ((LR[0] + LR[2]) / 2 * CW - CW / 2, -0.004, CH / 2 - (LR[1] + LR[3]) / 2 * CH)
# the button on its own layer, so it can be pressed in (slightly smaller and darker), then glow
_bw, _bh = (BTN[2] - BTN[0]) * pw, (BTN[3] - BTN[1]) * ph
_bcrop = (lerp(LR[0], LR[2], BTN[0]), lerp(LR[1], LR[3], BTN[1]), lerp(LR[0], LR[2], BTN[2]), lerp(LR[1], LR[3], BTN[3]))
BTN_MAT = ui_material("perm_button_ui", CHAT_UIS[0], crop=_bcrop, emit=1.0, radius_pt=BTN_R + 0.75)
BTN_EM = [n for n in BTN_MAT.node_tree.nodes if n.type == "EMISSION"][0]
BTN_Y = -0.0006
BTN_OB = ui_plane("perm_button", _bw, _bh, BTN_MAT, PERM,
                  ((BTN[0] + BTN[2]) / 2 * pw - pw / 2, BTN_Y, ph / 2 - (BTN[1] + BTN[3]) / 2 * ph))
# where the cursor tip lands: a little right of and below the button's centre, as a hand would
CLICK_UV = ((BTN[0] + BTN[2]) / 2 + 0.12 * (BTN[2] - BTN[0]), (BTN[1] + BTN[3]) / 2 + 0.12 * (BTN[3] - BTN[1]))
# a ripple ring that spreads from the cursor tip
RIP_MAT = bpy.data.materials.new("ripple"); RIP_MAT.use_nodes = True
_rn = RIP_MAT.node_tree; [_rn.nodes.remove(n) for n in list(_rn.nodes)]
_ro = _rn.nodes.new("ShaderNodeOutputMaterial"); _re = _rn.nodes.new("ShaderNodeEmission")
_re.inputs[0].default_value = (0.86, 0.92, 1.0, 1); _re.inputs[1].default_value = 1.6
_rtc = _rn.nodes.new("ShaderNodeTexCoord"); _rlen = _rn.nodes.new("ShaderNodeVectorMath"); _rlen.operation = "LENGTH"
_rn.links.new(_rtc.outputs["Object"], _rlen.inputs[0])
_rd = _rlen.outputs["Value"]
_band = remap(_rn, node_math(_rn, "ABSOLUTE", node_math(_rn, "SUBTRACT", _rd, 0.86)), 0.0, 0.12, 1.0, 0.0)
_disc = remap(_rn, _rd, 0.0, 1.0, 0.22, 0.0)
_rv = _rn.nodes.new("ShaderNodeValue"); _rv.name = "fade"; _rv.outputs[0].default_value = 0.0
_ra = node_math(_rn, "MULTIPLY", node_math(_rn, "ADD", _band, _disc), _rv.outputs[0])
_rt = _rn.nodes.new("ShaderNodeBsdfTransparent"); _rm = _rn.nodes.new("ShaderNodeMixShader")
_rn.links.new(_ra, _rm.inputs[0]); _rn.links.new(_rt.outputs[0], _rm.inputs[1]); _rn.links.new(_re.outputs[0], _rm.inputs[2])
_rn.links.new(_rm.outputs[0], _ro.inputs[0])
try: RIP_MAT.surface_render_method = "BLENDED"
except Exception: pass
bpy.ops.mesh.primitive_circle_add(vertices=64, radius=1.0, fill_type="NGON")
RIPPLE = bpy.context.active_object; RIPPLE.name = "perm_ripple"; RIPPLE.data.materials.append(RIP_MAT)
RIPPLE.parent = PERM; RIPPLE.rotation_euler = (math.radians(90), 0, 0); RIPPLE.visible_shadow = False
RIPPLE.location = (CLICK_UV[0] * pw - pw / 2, -0.0012, ph / 2 - CLICK_UV[1] * ph)
RIP_R = 1.5 * _bh

# ----------------------------------------------------------------------------- voxel props & glyphs
# Everything here is built from Sources/Sprite.swift at the sprite's own scale: canvas units are
# one sprite pixel wide (U), y runs down and Clawd's feet sit at canvas y = 20.
def C(cx, cy, y=0.0):
    return ((cx - 10) * U, y, (20 - cy) * U)

def glow_mat(name, hexcol, emit=0.35, rough=0.35):
    return principled(name, srgb(hexcol), rough, **{"Emission Color": srgb(hexcol), "Emission Strength": emit})

def voxel(name, rows, size, mat, parent=None, depth=None):
    """The glyph's '#' pixels as bevelled cubes (size in canvas units); origin at the top-left."""
    bm = bmesh.new()
    d = depth if depth is not None else size
    for ry, row in enumerate(rows):
        for rx, ch in enumerate(row):
            if ch != "#": continue
            res = bmesh.ops.create_cube(bm, size=1.0)
            for v in res["verts"]:
                v.co = Vector(((rx + 0.5 + v.co.x) * size * U, v.co.y * d * U, -(ry + 0.5 + v.co.z) * size * U))
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me); scene.collection.objects.link(ob)
    if parent: ob.parent = parent
    md = ob.modifiers.new("bevel", "BEVEL"); md.width = 0.18 * size * U; md.segments = 2; md.limit_method = "NONE"
    for p in me.polygons: p.use_smooth = True
    return ob

GLYPHS = {
    "heart": [".#.#.", "#####", ".###.", "..#.."], "star": [".#.", "###", ".#."],
    "note": ["..##", "..#.", "..#.", "###.", "##.."], "bang": ["#", "#", "#", ".", "#"],
    "bit": ["#", "#"], "crumb": ["#"],
}
GLYPH_COL = {"heart": "#ED5C73", "star": "#FFD65A", "note": "#82BEFF", "bang": "#FFD65A", "bit": "#78DC8C"}
GMATS = {k: glow_mat("g_" + k, v, 0.45) for k, v in GLYPH_COL.items()}
CRUMB_COLS = ["#EB5A50", "#5A96F0", "#FAC846", "#78DC8C", "#ED5C73"]
CMATS = [glow_mat(f"crumb{i}", c, 0.3) for i, c in enumerate(CRUMB_COLS)]

FRONT = -DEPTH / 2 - 1.2 * U   # just in front of Clawd's face

# hammer + workbench (.build)
M_WOOD = principled("wood", srgb("#6B4A33"), 0.55)
M_HANDLE = principled("handle", srgb("#B07A45"), 0.5)
M_STEEL = principled("steel", srgb("#3E424A"), 0.3, **{"Metallic": 0.85})
BUILD = empty("build", parent=CLAWD)
BENCH_Y = FRONT - 3.0 * U
box("bench", (6 * U, 4 * U, 3 * U), (0.5 * U, BENCH_Y, 1.5 * U), M_WOOD, BUILD, 0.3 * U)
HAMMER = empty("hammer", C(17.5, 14.5, BENCH_Y), BUILD)
box("hammer_handle", (0.6 * U, 0.6 * U, 4.5 * U), (0, 0, 2.75 * U - 0.5 * U), M_HANDLE, HAMMER, 0.15 * U)
box("hammer_head", (3 * U, 1.4 * U, 1.4 * U), (0, 0, 5.5 * U), M_STEEL, HAMMER, 0.2 * U)

# thought bubble with dots, then a light bulb (.think)
M_BUBBLE = principled("bubble", srgb("#F5F5F5"), 0.4, **{"Emission Color": srgb("#FFFFFF"), "Emission Strength": 0.15})
M_DOT = principled("dot", srgb("#141212"), 0.3)
M_BULB = glow_mat("bulb", "#FFD65A", 3.0, 0.2)
M_BULB_BASE = principled("bulb_base", srgb("#9696A0"), 0.3, **{"Metallic": 0.6})
THINK = empty("think", parent=CLAWD)
BUBBLE = empty("bubble_root", C(16.3, 3.2, FRONT + 2 * U), THINK)    # bubble centre, pops from here
def bub_box(name, x, y, w, h, mat, d=1.6, parent=BUBBLE, bevel=0.35):
    cx, _, cz = C(x + w / 2, y + h / 2)
    ox, _, oz = C(16.3, 3.2)
    return box(name, (w * U, d * U, h * U), (cx - ox, 0, cz - oz), mat, parent, bevel * U)
bub_box("bub_a", 13.5, 0.6, 5.6, 5.2, M_BUBBLE); bub_box("bub_b", 13.0, 1.1, 6.6, 4.2, M_BUBBLE)
# a dark outline one pixel-fraction behind, so the bubble reads against the bright set
M_OUTLINE = principled("outline", srgb("#3A3330"), 0.5)
_o1 = bub_box("bub_oa", 13.2, 0.3, 6.2, 5.8, M_OUTLINE, 1.0); _o1.location.y += 0.8 * U
_o2 = bub_box("bub_ob", 12.7, 0.8, 7.2, 4.8, M_OUTLINE, 1.0); _o2.location.y += 0.8 * U
_o3 = bub_box("bub_oc", 14.4, 8.2, 1.2, 1.2, M_OUTLINE, 0.6); _o3.location.y += 0.5 * U
_o4 = bub_box("bub_od", 15.6, 6.4, 1.5, 1.5, M_OUTLINE, 0.6); _o4.location.y += 0.5 * U
bub_box("bub_c", 14.6, 8.4, 0.8, 0.8, M_BUBBLE, 0.8); bub_box("bub_d", 15.8, 6.6, 1.1, 1.1, M_BUBBLE, 1.0)
DOTS = [bub_box(f"bub_dot{d}", 14.3 + d * 1.5, 2.8, 0.8, 0.8, M_DOT, 1.9, bevel=0.2) for d in range(3)]
BULB = empty("bulb", (0, 0, 0), BUBBLE)
_bulb = voxel("bulb_glass", [".###.", "#####", "#####", ".###."], 0.7, M_BULB, BULB, 1.9)
_bulb.location = tuple(a - b for a, b in zip(C(15.05, 1.3), C(16.3, 3.2)))
bub_box("bulb_base", 15.75, 4.1, 1.4, 0.7, M_BULB_BASE, 1.9, parent=BULB, bevel=0.15)
BULB_LIGHT = bpy.data.lights.new("bulb_light", "POINT"); BULB_LIGHT.energy = 0; BULB_LIGHT.color = (1.0, 0.85, 0.45)
BULB_LIGHT.shadow_soft_size = 0.05
_bl = bpy.data.objects.new("bulb_light", BULB_LIGHT); scene.collection.objects.link(_bl); _bl.parent = BULB
_bl.location = tuple(a - b for a, b in zip(C(16.8, 2.6, -0.04), C(16.3, 3.2)))

# juggling balls (.juggle): one per busy agent
M_BALLS = [principled(f"ball{i}", srgb(c), 0.3, **{"Coat Weight": 0.5}) for i, c in enumerate(["#EB5A50", "#5A96F0", "#FAC846"])]
BALLS = [box(f"ball{i}", (1.2 * U, 1.2 * U, 1.2 * U), (0, 0, 0), M_BALLS[i], CLAWD, 0.3 * U) for i in range(3)]

# '!' over the head when an agent waits for you
BANG3 = empty("bang3", parent=CLAWD)
_bang = voxel("bang_glyph", GLYPHS["bang"], 1.0, GMATS["bang"], BANG3, 1.0)
_bang.location = (-0.5 * U, 0, 0)

# particles: deterministic pools, each a pure function of time
import random
_rnd = random.Random(7)
PARTS = []   # (object, t0, life, x, y, vx, rise, depth, kind)
def particle(kind, t0, life, x, y, vx=0.0, rise=None, depth=None, mat=None, size=0.5):
    rows = GLYPHS[kind]
    sz = size * (1.0 if kind in ("heart", "note") else 1.3)
    ob = voxel(f"p_{kind}_{len(PARTS)}", rows, sz, mat or GMATS[kind], None, sz)
    w = len(rows[0]) * sz
    rise = rise if rise is not None else {"heart": 3, "note": 2.5, "bit": 2.5, "star": 2, "crumb": -5}[kind]
    PARTS.append((ob, t0, life, x - w / 2, y, vx, rise, FRONT if depth is None else depth))

# schedule (seconds): typing bits, hammer stars, celebration confetti and hearts
TYPE_SPANS = [(0.2, 2.9), (22.0, 30.4), (34.4, 36.0)]
BUILD_SPAN = (3.0, 5.8)
THINK_SPAN = (40.0, 41.0)
JUGGLE_SPAN = (40.0, 41.0)
ALERT_SPAN = (7.5, 10.2)
SCREEN_ALERT = 6.0     # on the screen: '!' and a hop
BREAK_T, BREAK_END = 6.7, 7.45   # out of the screen onto the desk
BACK_T, BACK_END = 33.5, 34.3    # and back in at the end
CELEB_SPAN = (31.2, 32.5)
for a, b in TYPE_SPANS:
    t = a + 0.3
    while t + 1.0 < b + 0.1:   # every bit is gone by the time the laptop folds away
        particle("bit", t, 1.0, _rnd.uniform(8, 12), 14.6, depth=FRONT - 4.4 * U)
        t += 0.45
for k in range(int((BUILD_SPAN[1] - BUILD_SPAN[0]) * 2.2) + 1):
    hit = BUILD_SPAN[0] + (k + 0.7) / 2.2
    if hit < BUILD_SPAN[1]:
        particle("star", hit, 0.5, 11.5, 15, vx=-3, depth=BENCH_Y)
        particle("star", hit, 0.5, 13.0, 15, vx=3, depth=BENCH_Y)
t = CELEB_SPAN[0]
while t < CELEB_SPAN[1]:
    particle("crumb", t, 2.0, _rnd.uniform(1, 19), _rnd.uniform(5, 7), vx=_rnd.uniform(-2, 2), rise=-_rnd.uniform(4, 8),
             depth=_rnd.uniform(-0.25, 0.2), mat=_rnd.choice(CMATS), size=0.6)
    t += 0.07
# approved: two hearts float up beside Clawd, under the open popover (which starts just above its head)
for tt, x, vx in [(17.55, -1.5, -0.8), (17.8, 21.5, 0.8)]:
    particle("heart", tt, 1.4, x, 9.5, vx=vx, rise=1.2)
# done: hearts rise over the head, staying under the card
for tt, x in [(31.3, 6), (31.55, 14), (31.8, 9)]:
    particle("heart", tt, 1.4, x, 6, rise=2.2)

def apply_particles(t):
    cx, cy, cz = CLAWD.location
    cz -= CLAWD_HOP[0]
    ks, kd = CLAWD.scale[0], CLAWD.scale[1]
    for ob, t0, life, x, y, vx, rise, depth in PARTS:
        age = t - t0
        vis = 0 <= age < life
        ob.hide_render = ob.hide_viewport = not vis
        if not vis: continue
        px, _, pz = C(x + vx * age, y - rise * age)
        s = out_back(clamp(age / 0.12), 1.6) * (1 - smooth(seg(age, life * 0.7, life)))
        ob.location = (cx + px * ks, cy + depth * kd, cz + pz * ks)
        ob.scale = (max(s * ks, 0.0001), max(s * kd, 0.0001), max(s * ks, 0.0001))

# ----------------------------------------------------------------------------- desk: monitor, phone, cursor
def tex_mat(name, path, emit=1.0, alpha=False):
    m = bpy.data.materials.new(name); m.use_nodes = True
    N = m.node_tree.nodes; Lk = m.node_tree.links
    for n in list(N): N.remove(n)
    o = N.new("ShaderNodeOutputMaterial"); tc = N.new("ShaderNodeTexCoord"); mp = N.new("ShaderNodeMapping"); mp.name = "map"
    im = N.new("ShaderNodeTexImage"); im.image = bpy.data.images.load(path, check_existing=True); im.extension = "CLIP"
    em = N.new("ShaderNodeEmission"); em.inputs[1].default_value = emit
    Lk.new(tc.outputs["UV"], mp.inputs[0]); Lk.new(mp.outputs[0], im.inputs[0]); Lk.new(im.outputs["Color"], em.inputs[0])
    if alpha:
        tr = N.new("ShaderNodeBsdfTransparent"); mx = N.new("ShaderNodeMixShader")
        Lk.new(im.outputs["Alpha"], mx.inputs[0]); Lk.new(tr.outputs[0], mx.inputs[1]); Lk.new(em.outputs[0], mx.inputs[2])
        Lk.new(mx.outputs[0], o.inputs[0])
        try: m.surface_render_method = "BLENDED"
        except Exception: pass
    else:
        Lk.new(em.outputs[0], o.inputs[0])
    return m

SCREEN_W, SCREEN_H = 4.8, 2.7
SCREEN_Y = 1.0
def add_coat(mat, rough, level=0.5, smudge=0.0):
    """Adds a glossy cover (glass / screen coating) over a self-lit material: real reflections
    with the right falloff, optionally with faint smudges in its roughness."""
    nt = mat.node_tree; N = nt.nodes
    out = [n for n in N if n.type == "OUTPUT_MATERIAL"][0]
    src = out.inputs[0].links[0].from_socket
    pb = N.new("ShaderNodeBsdfPrincipled"); pb.inputs["Base Color"].default_value = (0, 0, 0, 1)
    pb.inputs["Specular IOR Level"].default_value = level
    pb.inputs["Roughness"].default_value = rough
    if smudge:
        sm = remap(nt, noise(nt, 6.0, 3.0, 0.6, "UV"), 0.5, 0.75, 0.0, smudge)
        nt.links.new(node_math(nt, "ADD", sm, rough), pb.inputs["Roughness"])
    ad = N.new("ShaderNodeAddShader")
    nt.links.new(src, ad.inputs[0]); nt.links.new(pb.outputs[0], ad.inputs[1]); nt.links.new(ad.outputs[0], out.inputs[0])

# A generic studio-style display (no branding): a 16:9 panel behind one sheet of black glass with
# an even bezel and a camera dot, in a deep aluminium chassis with rounded corners, raised on an
# aluminium stand (foot plate and a tilted arm to a hinge on the back).
M_BEZEL = principled("bezel", srgb("#0A0A0B"), 0.22)
# bead-blasted: an even roughness (large patches of varying roughness read as smoke on the stand,
# which mostly reflects the shaded desk under the display), only a fine grain in the normal
M_ALU = principled("alu", srgb("#C4C1BC"), 0.42, **{"Metallic": 1.0})
micro_surface(M_ALU, (0.42, 0.42), 3.0, 900.0, 0.02)
BEZEL_W = 0.065                     # black border around the picture
CHASSIS_EDGE = 0.016                # aluminium seen around the glass
MON_LIFT = 0.55                     # desk to the bottom of the chassis
MON_W, MON_H = SCREEN_W + 2 * (BEZEL_W + CHASSIS_EDGE), SCREEN_H + 2 * (BEZEL_W + CHASSIS_EDGE)
MON_Z = MON_LIFT + MON_H / 2
MONITOR = empty("monitor", (0, SCREEN_Y, MON_Z))
SCREEN_BOTTOM = MON_Z - SCREEN_H / 2
_bz = glass_slab("monitor_front", SCREEN_W + 2 * BEZEL_W, SCREEN_H + 2 * BEZEL_W, 0.10, 0.006, M_BEZEL, MONITOR, (0, -0.0004, 0))
_sh = glass_slab("monitor_body", MON_W, MON_H, 0.12, 0.24, M_ALU, MONITOR, (0, 0.004, 0))
_sh.modifiers["bevel"].width = 0.022; _sh.modifiers["bevel"].segments = 6
glass_slab("monitor_camera", 0.024, 0.024, 0.012, 0.001, principled("camera_dot", srgb("#1A1D24"), 0.05, **{"Coat Weight": 1.0}),
           MONITOR, (0, -0.0012, SCREEN_H / 2 + BEZEL_W / 2))
SCREEN_MAT = tex_mat("screen", tex_path("desktop.png"), 1.0)
add_coat(SCREEN_MAT, 0.07, 0.4)
SCREEN_OB = ui_plane("monitor_screen", SCREEN_W, SCREEN_H, SCREEN_MAT, MONITOR, (0, -0.001, 0))
# stand: the foot plate reaches a little in front of the display, the arm rises behind it to a hinge
_hinge = Vector((0, SCREEN_Y + 0.27, MON_Z - 0.25)); _footj = Vector((0, SCREEN_Y + 1.15, 0.05))
_d = _hinge - _footj
_arm = box("monitor_arm", (1.05, 0.07, _d.length + 0.1), tuple((_hinge + _footj) / 2), M_ALU, None, 0.03)
_arm.rotation_euler.x = math.asin(-_d.y / _d.length)
box("monitor_foot", (1.35, 1.5, 0.05), (0, SCREEN_Y + 0.8, 0.025), M_ALU, None, 0.022)
box("monitor_hinge", (0.9, 0.12, 0.3), (0, SCREEN_Y + 0.27, MON_Z - 0.25), M_ALU, None, 0.04)

PHONE_W, PHONE_H, PHONE_R = 0.25, 0.52, 0.042
BEZEL = 0.008
DISP_W, DISP_H, DISP_R = PHONE_W - 2 * BEZEL, PHONE_H - 2 * BEZEL, PHONE_R - BEZEL
PHONE = empty("phone")
M_PHONE_FRAME = principled("phone_frame", srgb("#8E8A84"), 0.28, **{"Metallic": 1.0})
micro_surface(M_PHONE_FRAME, (0.22, 0.34), 40.0, 3000.0, 0.02)
_pb = glass_slab("phone_body", PHONE_W, PHONE_H, PHONE_R, 0.014, M_PHONE_FRAME, PHONE, (0, 0.001, 0))
_pb.modifiers["bevel"].width = 0.0035; _pb.modifiers["bevel"].segments = 5
glass_slab("phone_front", PHONE_W - 0.003, PHONE_H - 0.003, PHONE_R - 0.0015, 0.0012,
           principled("phone_black", srgb("#050506"), 0.06), PHONE, (0, -0.0002, 0))

def round_mask(mat, w, h, r):
    """Multiply the material's alpha by a rounded-rect mask in plane UV space (w, h, r in metres)."""
    N = mat.node_tree.nodes; Lk = mat.node_tree.links
    def m(op, a, b=None):
        n = N.new("ShaderNodeMath"); n.operation = op
        for i, v in enumerate((a, b)):
            if v is None: continue
            if isinstance(v, (int, float)): n.inputs[i].default_value = v
            else: Lk.new(v, n.inputs[i])
        return n.outputs[0]
    tc = N.new("ShaderNodeTexCoord"); sp = N.new("ShaderNodeSeparateXYZ"); Lk.new(tc.outputs["UV"], sp.inputs[0])
    qx = m("MAXIMUM", m("SUBTRACT", m("ABSOLUTE", m("MULTIPLY", m("SUBTRACT", sp.outputs[0], 0.5), w)), w / 2 - r), 0.0)
    qy = m("MAXIMUM", m("SUBTRACT", m("ABSOLUTE", m("MULTIPLY", m("SUBTRACT", sp.outputs[1], 0.5), h)), h / 2 - r), 0.0)
    d = m("SUBTRACT", m("SQRT", m("ADD", m("MULTIPLY", qx, qx), m("MULTIPLY", qy, qy))), r)
    a = m("LESS_THAN", d, 0.0)
    out = N["Material Output"] if "Material Output" in N else [n for n in N if n.type == "OUTPUT_MATERIAL"][0]
    shader = out.inputs[0].links[0].from_socket
    tr = N.new("ShaderNodeBsdfTransparent"); mx = N.new("ShaderNodeMixShader")
    Lk.new(a, mx.inputs[0]); Lk.new(tr.outputs[0], mx.inputs[1]); Lk.new(shader, mx.inputs[2]); Lk.new(mx.outputs[0], out.inputs[0])
    try: mat.surface_render_method = "BLENDED"
    except Exception: pass

# full-bleed vertical clips (three stacked in one strip), the player chrome on top, a pill cutout
PHONE_MAT = tex_mat("phone_clips", tex_path("phone-clips.png"), 1.0)
round_mask(PHONE_MAT, DISP_W, DISP_H, DISP_R)
ui_plane("phone_screen", DISP_W, DISP_H, PHONE_MAT, PHONE, (0, -0.0005, 0))
_ov = tex_mat("phone_overlay", tex_path("phone-overlay.png"), 1.0, alpha=True)
round_mask(_ov, DISP_W, DISP_H, DISP_R)
ui_plane("phone_overlay", DISP_W, DISP_H, _ov, PHONE, (0, -0.0008, 0))
glass_slab("phone_pill", 0.062, 0.017, 0.0085, 0.001, principled("pill", srgb("#050505"), 0.4), PHONE,
           (0, -0.0012, DISP_H / 2 - 0.022))
# the cover glass: reflections (strongest at grazing angles) with a few faint fingerprints
_cg = bpy.data.materials.new("phone_glass"); _cg.use_nodes = True
_gn = _cg.node_tree; [_gn.nodes.remove(n) for n in list(_gn.nodes)]
_go = _gn.nodes.new("ShaderNodeOutputMaterial"); _gt = _gn.nodes.new("ShaderNodeBsdfTransparent")
_gg = _gn.nodes.new("ShaderNodeBsdfGlossy"); _gg.inputs["Roughness"].default_value = 0.03
_gs = remap(_gn, noise(_gn, 9.0, 4.0, 0.65, "UV"), 0.52, 0.72, 0.0, 0.16)
_gn.links.new(node_math(_gn, "ADD", _gs, 0.03), _gg.inputs["Roughness"])
_gf = _gn.nodes.new("ShaderNodeLayerWeight"); _gf.inputs["Blend"].default_value = 0.12
_gm = _gn.nodes.new("ShaderNodeMixShader")
_gn.links.new(node_math(_gn, "ADD", _gf.outputs["Fresnel"], 0.02), _gm.inputs[0])
_gn.links.new(_gt.outputs[0], _gm.inputs[1]); _gn.links.new(_gg.outputs[0], _gm.inputs[2]); _gn.links.new(_gm.outputs[0], _go.inputs[0])
round_mask(_cg, PHONE_W - 0.004, PHONE_H - 0.004, PHONE_R - 0.002)
ui_plane("phone_cover", PHONE_W - 0.004, PHONE_H - 0.004, _cg, PHONE, (0, -0.0016, 0))
CLIP_N = 3

# ripple where Clawd breaks through the screen
RING_MAT = bpy.data.materials.new("ring"); RING_MAT.use_nodes = True
_N = RING_MAT.node_tree.nodes; _L = RING_MAT.node_tree.links
for _n in list(_N): _N.remove(_n)
_o = _N.new("ShaderNodeOutputMaterial"); _e = _N.new("ShaderNodeEmission"); _e.inputs[0].default_value = (1.0, 0.75, 0.55, 1); _e.inputs[1].default_value = 8.0
_t = _N.new("ShaderNodeBsdfTransparent"); _m = _N.new("ShaderNodeMixShader"); _v = _N.new("ShaderNodeValue"); _v.name = "fade"
_L.new(_v.outputs[0], _m.inputs[0]); _L.new(_t.outputs[0], _m.inputs[1]); _L.new(_e.outputs[0], _m.inputs[2]); _L.new(_m.outputs[0], _o.inputs[0])
try: RING_MAT.surface_render_method = "BLENDED"
except Exception: pass
bpy.ops.mesh.primitive_torus_add(major_radius=1.0, minor_radius=0.025, major_segments=64, minor_segments=8)
RING = bpy.context.active_object; RING.name = "ring"; RING.data.materials.append(RING_MAT)
RING.rotation_euler = (math.radians(90), 0, 0)

# the flash itself: a hot glowing disc on the screen where Clawd breaks through (the compositor glow blooms it)
FLASH_DISC_MAT = bpy.data.materials.new("flash_disc"); FLASH_DISC_MAT.use_nodes = True
_N = FLASH_DISC_MAT.node_tree.nodes; _L = FLASH_DISC_MAT.node_tree.links
for _n in list(_N): _N.remove(_n)
_o = _N.new("ShaderNodeOutputMaterial"); _e = _N.new("ShaderNodeEmission"); _e.name = "em"
_e.inputs[0].default_value = (1.0, 0.82, 0.62, 1); _e.inputs[1].default_value = 0.0
_gr = _N.new("ShaderNodeTexGradient"); _gr.gradient_type = "SPHERICAL"; _tc = _N.new("ShaderNodeTexCoord")
_mx = _N.new("ShaderNodeMixShader"); _tr = _N.new("ShaderNodeBsdfTransparent")
_pw = _N.new("ShaderNodeMath"); _pw.operation = "POWER"; _pw.inputs[1].default_value = 4.0   # soft falloff
_L.new(_tc.outputs["Object"], _gr.inputs[0]); _L.new(_gr.outputs[1], _pw.inputs[0]); _L.new(_pw.outputs[0], _mx.inputs[0])
_L.new(_tr.outputs[0], _mx.inputs[1]); _L.new(_e.outputs[0], _mx.inputs[2]); _L.new(_mx.outputs[0], _o.inputs[0])
try: FLASH_DISC_MAT.surface_render_method = "BLENDED"
except Exception: pass
bpy.ops.mesh.primitive_circle_add(vertices=48, radius=1.0, fill_type="NGON")
FLASH_DISC = bpy.context.active_object; FLASH_DISC.name = "flash_disc"; FLASH_DISC.data.materials.append(FLASH_DISC_MAT)
FLASH_DISC.rotation_euler = (math.radians(90), 0, 0)

CURSOR = empty("cursor")
CURSOR_W = 0.055
ui_plane("cursor_face", CURSOR_W, CURSOR_W * 72 / 52, tex_mat("cursor", tex_path("cursor.png"), 1.0, alpha=True), CURSOR,
         (CURSOR_W * (0.5 - 6 / 52), 0, -CURSOR_W * 72 / 52 * (0.5 - 4 / 72)))   # hotspot at the arrow tip

# ----------------------------------------------------------------------------- lights
def area(name, loc, target, size, power, color="#FFFFFF", shape="DISK", size_y=None):
    L = bpy.data.lights.new(name, "AREA"); L.shape = shape; L.size = size
    if size_y: L.size_y = size_y
    L.energy = power; L.color = srgb(color)[:3]
    ob = bpy.data.objects.new(name, L); scene.collection.objects.link(ob)
    ob.location = loc
    ob.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    return ob

# warm key from the front left, cool rim from behind right: the voxels read orange with defined edges
KEY = area("key", (-2.6, -1.6, 2.6), (0, 0, 0.4), 3.0, 1100, "#FFF1E8")
RIM = area("rim", (2.2, 0.6, 1.4), (0, 0, 0.35), 1.4, 900, "#D9E2FF")
FILL = area("fill", (1.8, -3.2, 0.9), (0, 0, 0.5), 4.0, 30, "#FFF8F2")
TOP = area("top", (0.0, -0.5, 5.0), (0, 0.0, 0.8), 6.0, 650, "#F7F9FF")
# low dusk sun through a window on the left: long warm light across the desk
WINDOW = area("window", (7.0, 1.0, 3.0), (0.0, -0.6, 0.4), 4.0, 1400, "#D8D0FF", "RECTANGLE", 4.0)   # lilac from the right
# tall strip softboxes either side: long specular lines along the rounded glass edges
STRIP_L = area("strip_l", (-2.8, -1.2, 1.4), (0, 0.3, 1.1), 0.18, 300, "#FFFFFF", "RECTANGLE", 3.6)
STRIP_R = area("strip_r", (2.9, -0.9, 1.5), (0, 0.3, 1.1), 0.18, 300, "#FFFFFF", "RECTANGLE", 3.6)
for s_ in (STRIP_L, STRIP_R):
    s_.visible_diffuse = False
for l_ in (KEY, RIM, FILL, TOP, WINDOW, STRIP_L, STRIP_R):
    l_.visible_transmission = False
for l_ in (FILL, TOP):
    l_.visible_glossy = False
# the monitor is a practical light: cool light onto the desk and Clawd
MON_LIGHT = area("monitor_light", (0, 0.95, MON_Z), (0, -3.0, 1.0), 4.6, 260, "#DCE6FF", "RECTANGLE", 2.6)
MON_LIGHT.visible_glossy = False
# the breakout: a burst of light where Clawd jumps out of the screen
BURST = bpy.data.lights.new("burst", "POINT"); BURST.color = (1.0, 0.78, 0.6); BURST.energy = 0; BURST.shadow_soft_size = 0.1
BURST_OB = bpy.data.objects.new("burst", BURST); scene.collection.objects.link(BURST_OB)
# the phone screen lights the foreground a little
PHONE_LIGHT = area("phone_light", (0.3, -3.1, 0.6), (-0.3, -3.95, 0.72), 0.25, 8, "#EEF2FF")
# warm glow behind the card that calls you
GLOW = bpy.data.lights.new("glow", "POINT"); GLOW.color = (1.0, 0.62, 0.35); GLOW.energy = 0; GLOW.shadow_soft_size = 0.25
GLOW_OB = bpy.data.objects.new("glow", GLOW); scene.collection.objects.link(GLOW_OB)
# seen through the clear glass these would show as hot discs; they only light the scene
for _o in (GLOW_OB, BURST_OB): _o.visible_transmission = False; _o.visible_glossy = False

# the studio lights (softboxes, key, rim) would show as hard streaks in the monitor's coated
# screen; light linking keeps them out of it, so it only reflects the room and the desk
NO_GLARE = bpy.data.collections.new("no_screen_glare")
for _o in (SCREEN_OB, _bz, bpy.data.objects["monitor_camera"]): NO_GLARE.objects.link(_o)
for _co in NO_GLARE.collection_objects: _co.light_linking.link_state = "EXCLUDE"
for _l in (KEY, RIM, FILL, TOP, WINDOW, STRIP_L, STRIP_R, PHONE_LIGHT):
    _l.light_linking.receiver_collection = NO_GLARE

from mathutils import Matrix

# ----------------------------------------------------------------------------- props
# Minimal desk: just a keyboard (modelled here), so nothing fights the glass UI.
# keyboard: an aluminium tray and white keycaps
M_KEYCAP = principled("keycap", srgb("#F3F3F5"), 0.45)
micro_surface(M_KEYCAP, (0.38, 0.5), 30.0, 1500.0, 0.03)
KB = empty("keyboard", (-2.3, -1.35, 0.0)); KB.rotation_euler.z = math.radians(5)
KB.hide_render = True
box("kb_tray", (3.45, 1.12, 0.07), (0, 0, 0.035), M_ALU, KB, 0.025)
_bm = bmesh.new()
_k, _g = 0.19, 0.035
_total = 3.45 - 0.18
for row, widths in enumerate([[1.0] * 14, [1.5] + [1.0] * 12 + [1.5], [1.8] + [1.0] * 11 + [2.2],
                              [2.3] + [1.0] * 10 + [2.7], [1.2, 1.2, 1.4, 6.6, 1.4, 1.2, 1.0, 1.0, 1.0]]):
    unit = (_total - _g * (len(widths) - 1)) / sum(widths)
    x = -_total / 2
    yy = 1.12 / 2 - 0.13 - row * (_k + _g)
    for wi in widths:
        wk = wi * unit
        r = bmesh.ops.create_cube(_bm, size=1.0)
        for v in r["verts"]:
            v.co = Vector((x + wk / 2 + v.co.x * wk, yy + v.co.y * _k, 0.105 + v.co.z * 0.06))
        x += wk + _g
_me = bpy.data.meshes.new("keycaps"); _bm.to_mesh(_me); _bm.free(); _me.materials.append(M_KEYCAP)
_kc = bpy.data.objects.new("keycaps", _me); scene.collection.objects.link(_kc); _kc.parent = KB
for _o in [KB] + list(KB.children): _o.hide_render = True
_md = _kc.modifiers.new("bevel", "BEVEL"); _md.width = 0.018; _md.segments = 2; _md.limit_method = "NONE"
for _p in _me.polygons: _p.use_smooth = True

# ----------------------------------------------------------------------------- camera
cam_data = bpy.data.cameras.new("cam")
CAM = bpy.data.objects.new("cam", cam_data); scene.collection.objects.link(CAM); scene.camera = CAM
cam_data.sensor_width = 36
cam_data.dof.use_dof = True
cam_data.dof.aperture_blades = 9
FOCUS = empty("focus")
cam_data.dof.focus_object = FOCUS
cam_data.clip_start = 0.02
cam_data.clip_end = 200

def look_at(cam, eye, target, roll=0.0):
    cam.location = eye
    d = Vector(target) - Vector(eye)
    cam.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
    if roll:
        cam.rotation_euler.rotate_axis("Z", math.radians(roll))

# ----------------------------------------------------------------------------- animation
from mathutils import Matrix
HEAD = (0.0, 0.0, 10 * U)

def stepped_cut(t, t0, dt, steps, start_v, ease_len=0.22):
    v = start_v
    for i, s in enumerate(steps):
        ts = t0 + i * dt
        v = lerp(v, s, out3(seg(t, ts, ts + ease_len)))
    return v

def place(ob, loc=None, rot=None, scale=None):
    if loc is not None: ob.location = loc
    if rot is not None: ob.rotation_euler = [math.radians(a) for a in rot]
    if scale is not None: ob.scale = (scale, scale, scale) if isinstance(scale, (int, float)) else scale

def set_visible(ob, vis):
    for o in [ob] + list(ob.children_recursive):
        o.hide_render = not vis
        o.hide_viewport = not vis

def world_point(ob, local):
    m = Matrix.LocRotScale(ob.location, ob.rotation_euler, ob.scale)
    if ob.parent:
        m = Matrix.LocRotScale(ob.parent.location, ob.parent.rotation_euler, ob.parent.scale) @ m
    return tuple(m @ Vector(local))

def panel_point(u, v, w, h, dy=0.0):
    return (u * w - w / 2, dy, h / 2 - v * h)

def in_span(t, span): return span[0] <= t < span[1]

CLICK_T = 13.2      # the cursor clicks Clawd
OPEN_T = 13.35      # the popover springs out of Clawd
PRESS_T = 16.9      # 허용 pressed
Q_T = 18.7          # the sidebar switches to the agent asking a question
COMPOSER_T = 19.5   # the cursor clicks the composer
TYPE_A, TYPE_B = 19.8, 21.1   # the reply is typed
SEND_T = 21.5       # send pressed: the reply lands, the agent is working again
TL_T = 23.6         # web-app's live step timeline
TAB_T = 26.4        # the cursor clicks the terminal tab
TT_A, TT_B = 27.3, 28.4       # a command is typed at the Claude Code prompt
ENTER_T = 28.7      # Enter: new output lines
CLOSE_T = 30.6      # popover closes back into Clawd
DONE_T = 31.1       # "작업 완료" card
SWITCH_T = Q_T

def clawd_pose(t):
    p = dict(z=0.0, squash=1.0, bob=0.0, arm_l=2.0, arm_r=2.0, eyes="open", look=0.0, eye_dy=0.0,
             laptop=0.0, build=0.0, hammer=-95.0, think=0.0, bulb=0.0, dots=0, juggle=0.0, juggle_t=0.0, bang=0.0)
    # mirror: Edit -> type
    for a, b in TYPE_SPANS:
        if a - 0.2 <= t < b + 0.2:
            p["laptop"] = (out_back(seg(t, a - 0.2, a + 0.15)) if a > 1 else 1.0) * (1 - in_out3(seg(t, b - 0.05, b + 0.2)))
        if in_span(t, (a, b)):
            k = int(t * 10) % 2 == 0
            p["arm_l"] = 1.75 if k else 2.0; p["arm_r"] = 2.0 if k else 1.75
            p["eye_dy"] = 0.3
            if int(t) % 4 == 3: p["eye_dy"] = 0; p["look"] = 0.8
    # Bash -> build
    a, b = BUILD_SPAN
    if a - 0.2 <= t < b + 0.2:
        p["build"] = out_back(seg(t, a - 0.2, a + 0.15)) * (1 - in_out3(seg(t, b - 0.05, b + 0.2)))
    if in_span(t, BUILD_SPAN):
        c = ((t - a) * 2.2) % 1
        if c < 0.45: ang = lerp(-95, 0, in_out3(c / 0.45))
        elif c < 0.6: ang = 0
        elif c < 0.7: ang = lerp(0, -95, (seg(c, 0.6, 0.7)) ** 2)
        else: ang = -95
        p["hammer"] = ang
        p["arm_r"] = 1 if ang > -50 else 2
        p["eye_dy"] = 0.3; p["look"] = -0.2
    # an agent needs you: wide eyes, flapping claws, hops, '!'
    if in_span(t, ALERT_SPAN):
        k = t - ALERT_SPAN[0]
        p["eyes"] = "wide"
        flap = int(k * 8) % 2 == 0
        p["arm_l"] = 0.5 if flap else 1.5; p["arm_r"] = 1.5 if flap else 0.5
        hop = k % 0.8
        p["z"] = 0.09 * math.sin(clamp(hop / 0.42) * math.pi)
        if 0.42 < hop < 0.5: p["squash"] = 0.92
    p["bang"] = out_back(seg(t, SCREEN_ALERT, SCREEN_ALERT + 0.25), 2.0) * (1 - smooth(seg(t, CLICK_T - 0.2, CLICK_T)))
    if SCREEN_ALERT <= t < BREAK_END:       # on the screen: wide eyes and a hop, then the jump out
        p["eyes"] = "wide"; p["arm_l"] = p["arm_r"] = 0.5 if t > BREAK_T else (0.5 if int(t * 8) % 2 else 1.5)
        if t < BREAK_T: p["z"] = 0.09 * math.sin(clamp(((t - SCREEN_ALERT) % 0.35) / 0.3) * math.pi)
    if BREAK_END <= t < BREAK_END + 0.4:    # landing squash
        k = t - BREAK_END
        p["squash"] = 1 - 0.28 * math.exp(-8 * k) * math.cos(2 * math.pi * 2.5 * k)
    if BACK_T - 0.3 <= t < BACK_END:
        p["eyes"] = "happy"; p["arm_l"] = p["arm_r"] = 1.0
    if ALERT_SPAN[1] <= t < CLICK_T:     # "hold": arms up under the card, waiting
        p["arm_l"] = p["arm_r"] = 0.5; p["eyes"] = "wide"; p["look"] = -0.3; p["eye_dy"] = -0.2
    if CLICK_T <= t < CLICK_T + 0.25:
        p["squash"] = 0.9; p["eyes"] = "closed"
    if OPEN_T + 0.3 <= t < CLOSE_T and not any(a <= t < b for a, b in TYPE_SPANS):
        p["eye_dy"] = -0.35; p["look"] = -0.2
        if 15.2 < t < 15.32: p["eyes"] = "closed"
    # approved: a little celebration
    if PRESS_T + 0.6 <= t < PRESS_T + 1.7:
        beat = int(t * 5) % 2 == 0
        p["eyes"] = "happy"; p["arm_l"] = 0.5 if beat else 1.0; p["arm_r"] = 1.0 if beat else 0.5
        p["z"] = 0.05 * abs(math.sin((t - PRESS_T) * math.pi * 2.5))
    # done: celebrate, then wave
    if in_span(t, CELEB_SPAN):
        k = t - CELEB_SPAN[0]
        beat = int(t * 4) % 2 == 0
        p["arm_l"] = 0.5 if beat else 1.0; p["arm_r"] = 1.0 if beat else 0.5; p["eyes"] = "happy"
        p["z"] = 0.13 * math.sin(clamp((k % 0.7) / 0.5) * math.pi)
    if CELEB_SPAN[1] <= t < 33.4:
        p["arm_r"] = 0.5 if int(t * 4) % 2 == 0 else 1.2; p["eyes"] = "happy"   # wave
    if t >= CELEB_SPAN[1]:
        k = t - CELEB_SPAN[1]
        p["squash"] = 1 - 0.12 * math.exp(-6 * k) * math.cos(2 * math.pi * 2.0 * k)
        if t >= 33.4: p["eyes"] = "open"
    return p

def pop(ob, s):
    s = max(s, 0.0001)
    ob.scale = (s, s, s)
    set_visible(ob, s > 0.001)

SCREEN_POS = Vector((0.62, SCREEN_Y - 0.012, SCREEN_BOTTOM + 0.06))   # Clawd's spot on the bottom edge of the screen
SC2D = 0.26                                            # its on-screen size
def screen_k(t):
    """1 = a flat pixel sprite on the screen, 0 = the 3D Clawd on the desk."""
    return 1 - seg(t, BREAK_T, BREAK_END) + seg(t, BACK_T, BACK_END)

CLAWD_HOP = [0.0]
def apply_clawd(t):
    p = clawd_pose(t)
    out = seg(t, BREAK_T, BREAK_END); back = seg(t, BACK_T, BACK_END)
    k = out if t < BACK_T else 1 - back          # 0 on screen .. 1 on the desk
    pos = Vector(SCREEN_POS).lerp(Vector((0, 0, 0)), smooth(k))
    pos.z += 0.55 * math.sin(k * math.pi)         # the jump arc
    sc = lerp(SC2D, 1.0, out3(k) if t < BACK_T else in_out3(k))
    depth = lerp(0.03, 1.0, smooth(seg(k, 0.05, 0.6)))   # the pixels extrude into voxels
    sq = p["squash"]
    CLAWD.location = (pos.x, pos.y, pos.z + p["z"] * sc)
    CLAWD_HOP[0] = p["z"] * sc
    CLAWD.scale = (sc / math.sqrt(sq), sc * depth / math.sqrt(sq), sc * sq)
    CLAWD.rotation_euler = (math.radians(-18 * math.sin(k * math.pi)), 0, 0)
    pk = math.sin(seg(t, BREAK_T - 0.05, BREAK_T + 0.45) * math.pi) ** 2      # the breakout flash
    fl = max(pk, math.sin(seg(t, BACK_T - 0.05, BACK_T + 0.4) * math.pi) ** 2 * 0.6)
    FLASH_DISC.hide_render = FLASH_DISC.hide_viewport = fl < 0.01
    FLASH_DISC.location = (SCREEN_POS.x, SCREEN_Y - 0.006, SCREEN_POS.z + 0.08)
    _r = 0.12 + 0.25 * fl; FLASH_DISC.scale = (_r, _r, _r)
    FLASH_DISC_MAT.node_tree.nodes["em"].inputs[1].default_value = 45 * fl
    _cb.inputs["Emission Strength"].default_value = 0.9 * (1 - smooth(seg(k, 0.0, 0.5))) + 2.5 * pk
    # the monitor dims a little as Clawd gathers itself, then flashes as it breaks out
    dim = smooth(seg(t, SCREEN_ALERT, BREAK_T)) * (1 - smooth(seg(t, BREAK_T + 0.3, BREAK_T + 1.2)))
    SCREEN_MAT.node_tree.nodes["Emission"].inputs[1].default_value = 0.9 - 0.35 * dim + 1.1 * pk
    MON_LIGHT.data.energy = 260 * (1 - 0.4 * dim) + 500 * pk
    # light burst and ripple on the screen at the moment of breaking out / back in
    for t0 in (BREAK_T, BACK_T):
        a = seg(t, t0 - 0.05, t0 + 0.6)
        if 0 < a < 1:
            BURST_OB.location = (SCREEN_POS.x, SCREEN_POS.y - 0.5, SCREEN_POS.z + 0.15)
            BURST.energy = 2600 * math.sin(a * math.pi) ** 3
            RING.location = (SCREEN_POS.x, SCREEN_Y - 0.004, SCREEN_POS.z + 0.07)
            r = 0.05 + 0.38 * out3(a); RING.scale = (r, r, r)
            RING_MAT.node_tree.nodes["fade"].outputs[0].default_value = 1 - a
            RING.hide_render = RING.hide_viewport = False
            break
    else:
        BURST.energy = 0
        RING.hide_render = RING.hide_viewport = True
    BODY.location = (0, 0, -p["bob"] * 2 * U)
    ARM_L.location.z = rz(p["arm_l"]) - U
    ARM_R.location.z = rz(p["arm_r"]) - U
    for l, i, yy in LEGS:
        l.location.x = sx((4, 6, 11, 13)[i] + 0.5)
    shift = clamp(p["look"], -1, 1) * 0.3
    for e, ex in EYES:
        e.location.x = sx(ex + 0.5 + shift)
        e.location.z = rz(1 + p["eye_dy"]) - U
        mode = p["eyes"]
        if mode == "open": e.scale = (1, 1, 1)
        elif mode == "wide": e.scale = (1.3, 1, 1.3)
        elif mode == "closed": e.scale = (1.2, 1, 0.15); e.location.z = rz(1.6) - 0.15 * U
        elif mode == "happy": e.scale = (1.25, 1, 0.3); e.location.z = rz(1.25) - 0.3 * U
    pop(LAPTOP, p["laptop"])
    pop(BUILD, p["build"])
    HAMMER.rotation_euler = (0, math.radians(p["hammer"]), 0)
    pop(THINK, 1.0 if p["think"] > 0.001 else 0.0)
    if p["think"] > 0.001:
        BUBBLE.scale = (max(p["think"], 0.0001),) * 3
        for d, ob in enumerate(DOTS):
            ob.hide_render = ob.hide_viewport = not (d < p["dots"])
        pop(BULB, p["bulb"])
        BULB_LIGHT.energy = 6 * p["bulb"]
    # juggling balls follow the sprite's arc over the head
    for i, ob in enumerate(BALLS):
        vis = p["juggle"] > 0.001
        ob.hide_render = ob.hide_viewport = not vis
        if vis:
            c = (p["juggle_t"] * 1.1 + i / 3) % 1
            ob.location = C(2.6 + 14 * c + 0.6, 12.5 - 36 * c * (1 - c) + 0.6, FRONT - 0.5 * U)
            ob.scale = (p["juggle"],) * 3
    pop(BANG3, p["bang"])
    BANG3.location = C(10.0, 3.6 - 0.6 * abs(math.sin(t * 6)), FRONT)   # just over the head, under the card
    set_visible(BANG, False)

# agent chips around Clawd: (position, yaw)
CHIP_SLOTS = {
    "web-app":    ((-0.66, 0.05, 0.76), 16, 1.55),
    "ios-widget": ((0.68, 0.10, 0.84), -16, 1.75),
    "api-server": ((-0.50, 0.40, 1.10), 12, 1.95),
    "docs-site":  ((0.52, 0.45, 1.20), -12, 2.15),
}
CHIP_FOCUS = [("web-app", TYPE_SPANS[0]), ("ios-widget", BUILD_SPAN), ("api-server", THINK_SPAN)]
CHIPS_OUT = 12.35

def apply_chips(t):
    for name, (root, mat, w, h) in CHIPS.items():
        set_visible(root, False)

# the cards sit high enough that the hopping '!' and the celebration never reach their text
CARD_SPANS = {"permission": (ALERT_SPAN[0] + 0.2, CLICK_T - 0.05, ((0.0, -0.18, 1.17), (-4, 0, 0), 1.45)),
              "done": (DONE_T, 33.3, ((0.0, -0.18, 1.10), (-4, 0, 0), 1.3))}

def apply_cards(t):
    GLOW.energy = 0
    for name, (root, mat, w, h) in CARDS.items():
        t_in, t_out, (slot, rot, sc) = CARD_SPANS[name]
        a_in = seg(t, t_in, t_in + 0.7)
        a_out = seg(t, t_out, t_out + 0.4)
        vis = a_in > 0 and a_out < 1
        set_visible(root, vis)
        if not vis: continue
        loc = vlerp(HEAD, slot, out5(a_in))
        s = sc * max(out_back(a_in, 1.3), 0.0001)
        rx = rot[0] + lerp(-40, 0, out3(a_in))
        k = in_out3(a_out)
        loc = vlerp(loc, HEAD, k); s *= max(1 - k, 0.0001)
        place(root, (loc[0], loc[1], loc[2] + 0.01 * math.sin(t * 2)), (rx, 0, rot[2]), s)
        set_fade(mat, smooth(a_in * 3) * (1 - smooth(seg(a_out, 0.6, 1.0))))
        if name == "permission":
            pulse = 0.75 + 0.25 * math.sin((t - t_in) * 4.5)
            GLOW.energy = 22 * smooth(seg(t, t_in + 0.2, t_in + 0.8)) * pulse * (1 - k)
            GLOW_OB.location = (loc[0], loc[1] + 0.12, loc[2])

TIP = (0.0, -0.02, 10 * U + 0.07)          # the arrow tip, just above Clawd's head
CHAT_ROT = (-3, 0, -6)
CHAT_OFF = Vector((0, 0, ARROW_H + CH / 2))

def apply_panels(t):
    # scale out of the arrow tip with a slight overshoot, back into it when closing; gone once it's
    # a few millimetres across (a lone bright speck over Clawd's head otherwise)
    grow = out_back(seg(t, OPEN_T, OPEN_T + 0.55), 1.25) * (1 - in_out3(seg(t, CLOSE_T, CLOSE_T + 0.45)))
    vis = OPEN_T <= t < CLOSE_T + 0.5 and grow > 0.03
    set_visible(CHAT, vis)
    if vis:
        s = max(grow, 0.0001)
        rot = Euler([math.radians(a) for a in CHAT_ROT])
        off = CHAT_OFF.copy(); off.rotate(rot)
        place(CHAT, tuple(Vector(TIP) + off * s), CHAT_ROT, s)
        # which face shows: permission -> question -> (reply typed in) -> replied -> timeline -> terminal -> (typed) -> after Enter
        def on(a, b=99.0, fa=0.15, fb=0.15): return smooth(seg(t, a, a + fa)) * (1 - smooth(seg(t, b, b + fb)))
        vis_f = {"chat_permission": on(-1, Q_T), "chat_question": on(Q_T, SEND_T), "chat_replied": on(SEND_T, TL_T),
                 "chat_timeline": on(TL_T, TAB_T), "terminal": on(TAB_T, ENTER_T, fb=0.04),
                 "terminal_after": on(ENTER_T, fa=0.04)}   # Enter redraws the terminal at once (no ghosting)
        for k, m in FACE.items():
            set_fade(m, vis_f[k])
            nodes = m.node_tree.nodes
            nodes["cut"].outputs[0].default_value = 1.0
            nodes["hole"].outputs[0].default_value = 0.0
        # typing, glyph by glyph; the reply's send button appears once the text is in
        for k, (a, b) in (("chat_question", (TYPE_A, TYPE_B)), ("terminal", (TT_A, TT_B))):
            ty, nodes = TYPING[k], FACE[k].node_tree.nodes
            n = int(clamp((t - a) / (b - a)) * len(ty["stops"]) + 1e-6)
            nodes["rcut"].outputs[0].default_value = ty["stops"][n - 1] if n else ty["start"]
            nodes["typing"].outputs[0].default_value = 1.0 if t >= a else 0.0
            if ty["send"]: nodes["send_on"].outputs[0].default_value = 1.0 if t > b + 0.12 else 0.0
        # after Enter: new rows appear one by one and the terminal scrolls up with them, so the
        # newest row stays where the prompt was until everything is in place
        cut = stepped_cut(t, ENTER_T + 0.15, 0.1, AFTER_STEPS, AFTER_START, 0.08)
        span = min(SCROLL_D, AFTER_STEPS[-1] - AFTER_START)
        AFTER_MAT.node_tree.nodes["cut"].outputs[0].default_value = cut
        AFTER_MAT.node_tree.nodes["scroll"].outputs[0].default_value = SCROLL_D * clamp((AFTER_STEPS[-1] - cut) / span)
        tl_dt = min(0.12, (TAB_T - 0.5 - TL_T - 0.15) / max(len(CHAT_STEPS), 1))
        TIMELINE_MAT.node_tree.nodes["cut"].outputs[0].default_value = stepped_cut(t, TL_T + 0.15, tl_dt, CHAT_STEPS, LAYOUT["chat_timeline"]["header_v"])
        # a tab switch: the terminal is there at once, a quick wipe rather than row by row
        TERM_MAT.node_tree.nodes["cut"].outputs[0].default_value = stepped_cut(t, TAB_T + 0.12, 0.015, TERM_STEPS, LAYOUT["terminal"]["header_v"], 0.06)
        lift_t = OPEN_T + 1.1
        PERMVIEW_MAT.node_tree.nodes["hole"].outputs[0].default_value = 1.0 if t >= lift_t else 0.0
        pa = seg(t, lift_t, lift_t + 0.01); lift = in_out(seg(t, lift_t + 0.05, lift_t + 0.9))
        press = seg(t, PRESS_T, PRESS_T + 0.4); gone = in_out3(seg(t, PRESS_T + 0.5, PRESS_T + 1.0))
        pv = pa > 0 and gone < 1
        set_visible(PERM, pv)
        if pv:
            depth = -0.28 * lift + 0.03 * math.sin(press * math.pi) + 0.24 * gone
            place(PERM, (PERM_HOME[0], PERM_HOME[1] + depth, PERM_HOME[2]), (lift * 3, 0, lift * 6),
                  max((1 + 0.04 * lift) * (1 - 0.025 * math.sin(press * math.pi)) * (1 - gone), 0.0001))
            f = pa * (1 - smooth(seg(gone, 0.5, 1.0)))
            set_fade(PERM_MAT, f); set_fade(BTN_MAT, f)
            # the button goes in as the cursor clicks, springs back, and glows once
            down = smooth(seg(t, PRESS_T - 0.05, PRESS_T + 0.03)) * (1 - smooth(seg(t, PRESS_T + 0.14, PRESS_T + 0.34)))
            glow = math.sin(clamp(seg(t, PRESS_T + 0.14, PRESS_T + 0.62)) * math.pi)
            BTN_OB.scale = (1 - 0.07 * down,) * 3
            BTN_OB.location.y = BTN_Y + 0.0004 * down
            BTN_EM.inputs[1].default_value = 1.0 - 0.28 * down + 0.22 * glow
            PERM_MAT.node_tree.nodes["press"].outputs[0].default_value = down
            ra = seg(t, PRESS_T - 0.02, PRESS_T + 0.6)
            rv = 0 < ra < 1
            RIPPLE.hide_render = RIPPLE.hide_viewport = not rv
            if rv:
                r = RIP_R * lerp(0.3, 1.0, out3(ra))
                RIPPLE.scale = (r, r, r)
                RIP_MAT.node_tree.nodes["fade"].outputs[0].default_value = 0.9 * (1 - ra) ** 1.5

def apply_cursor(t):
    # glides in to Clawd and clicks, then to 허용 and clicks
    vis = 11.9 <= t < TT_B + 0.6
    set_visible(CURSOR, vis)
    if not vis: return
    clawd_pt = (0.12, -0.25, 0.32)
    def on_panel(u, v): return world_point(CHAT, panel_point(u, v, CW, CH, -0.012))
    if t < OPEN_T + 1.4:
        loc = keys(t, [(11.9, (1.1, -0.25, 0.05)), (CLICK_T - 0.1, clawd_pt), (OPEN_T + 1.4, (0.55, -0.3, 0.45))], in_out3)
    elif t < Q_T:
        btn = world_point(PERM, panel_point(CLICK_UV[0], CLICK_UV[1], PERM_W, PERM_H, -0.005))
        loc = keys(t, [(OPEN_T + 1.4, (0.55, -0.3, 0.45)), (PRESS_T - 0.15, btn), (Q_T, btn)], in_out3)
    else:
        sr = TYPING["chat_question"]["send"]; cr = TYPING["chat_question"]["rows"]; tr_ = TYPING["terminal"]["rows"]
        tab = LAYOUT["terminal"]["tab_point"]
        pts = [(Q_T, on_panel(0.32, 0.86)), (COMPOSER_T - 0.1, on_panel(0.45, (cr[0] + cr[1]) / 2)), (TYPE_B, on_panel(0.62, cr[1] + 0.03)),
               (SEND_T - 0.1, on_panel((sr[0] + sr[2]) / 2, (sr[1] + sr[3]) / 2)), (TL_T, on_panel(0.85, 0.7)),
               (TAB_T - 0.1, on_panel(tab[0], tab[1])), (TT_A - 0.25, on_panel(0.5, (tr_[0] + tr_[1]) / 2 + 0.02)),
               (TT_B + 0.6, on_panel(0.62, 0.86))]
        loc = keys(t, pts, in_out3)
    clicks = (CLICK_T, PRESS_T, COMPOSER_T, SEND_T, TAB_T, TT_A - 0.2)
    dip = 0.85 if any(c - 0.05 < t < c + 0.12 for c in clicks) else 1.0
    place(CURSOR, loc, (0, 0, 0), dip)

PHONE_POS = (-0.42, -3.0, 0.70)
def apply_phone(t):
    # propped in the right foreground; its feed scrolls while you're away
    place(PHONE, PHONE_POS, (-14, 0, 10), 0.85)
    PHONE_LIGHT.location = (PHONE_POS[0], PHONE_POS[1] - 0.08, PHONE_POS[2] + 0.1)
    # swipe up to the next clip twice; each clip slowly pushes in while it plays
    i = in_out3(seg(t, 2.0, 2.35)) + in_out3(seg(t, 4.3, 4.65))
    if t > 33.5: i = 2 * (1 - in_out3(seg(t, 33.5, 33.85)))   # back to the first clip at the end
    start = [0.0, 2.35, 4.65][min(int(round(i)), 2)]
    z = 1 - 0.07 * smooth(seg(t, start, start + 2.5))
    mp = PHONE_MAT.node_tree.nodes["map"]
    mp.inputs["Scale"].default_value = (z, z / CLIP_N, 1)
    mp.inputs["Location"].default_value = ((1 - z) / 2, 1 - (i + 1) / CLIP_N + (1 - z) / (2 * CLIP_N), 0)

def apply_camera(t):
    perm = world_point(PERM, (0, 0, 0)) if OPEN_T < t < SWITCH_T else (0.25, 0.1, 0.95)
    # the popover is static (fully open) during these beats, so its points can be computed from its rest pose
    def rest_point(u, v):
        rot = Euler([math.radians(a) for a in CHAT_ROT])
        p = Vector(panel_point(u, v, CW, CH)); p.rotate(rot)
        off = CHAT_OFF.copy(); off.rotate(rot)
        return tuple(Vector(TIP) + off + p)
    q_c = rest_point(0.62, 0.82)
    comp = rest_point(0.62, 0.89)
    term_lo = rest_point(0.50, 0.76)   # low enough that the output (it scrolls up from the prompt) stays clear of the caption
    phone = PHONE_POS
    shots = [
        (0.0,  (-0.25, -4.20, 0.80), (0.10, 0.2, 0.62), 35),    # 1 desk: you're on the phone, Clawd works on screen
        (6.0,  (-0.18, -4.12, 0.80), (0.12, 0.2, 0.62), 35),
        (7.4,  (-0.15, -4.05, 0.80), (0.10, 0.1, 0.62), 35),    #   Clawd breaks out of the screen
        (8.6,  (-0.10, -3.10, 0.86), (-0.32, 0.0, 0.85), 38),   # 2 alert: push past the phone (Clawd right, caption left)
        (11.6, (0.0, -2.05, 0.98), (-0.22, -0.1, 1.0), 40),     #   the card, readable from across the room
        (13.3, (-0.25, -2.55, 0.95), (0.0, 0.0, 0.85), 38),     # 3 cursor clicks Clawd, popover springs out
        (14.6, (-0.70, -2.45, 1.32), (0.0, 0.05, 1.20), 35),
        (15.8, (perm[0] - 0.55, perm[1] - 1.25, perm[2] + 0.10), (perm[0] - 0.05, perm[1], perm[2] - 0.02), 42),
        (17.2, (perm[0] - 0.50, perm[1] - 1.20, perm[2] + 0.08), (perm[0] - 0.08, perm[1], perm[2] - 0.03), 42),
        (18.2, (-0.55, -2.65, 1.02), (0.0, 0.0, 0.88), 36),     #   Clawd celebrates under the popover
        (19.3, tuple(Vector(q_c) + Vector((-0.28, -1.15, 0.14))), q_c, 40),   # 4 reply: the question, then
        (20.3, tuple(Vector(comp) + Vector((-0.22, -1.58, 0.10))), comp, 40),  #   push in on the composer while typing
        (22.4, tuple(Vector(comp) + Vector((-0.16, -1.60, 0.12))), comp, 40),
        (24.0, (-0.55, -2.35, 1.34), (0.0, 0.05, 1.22), 35),    # 5 live timeline
        (26.2, (-0.10, -2.30, 1.34), (0.0, 0.05, 1.22), 35),    #   wide enough to see the terminal tab clicked
        (27.4, tuple(Vector(term_lo) + Vector((0.18, -1.12, 0.10))), term_lo, 40),   # 6 terminal: its lower half,
        (30.2, tuple(Vector(term_lo) + Vector((0.10, -1.16, 0.12))), term_lo, 40),   #   typing and new output
        (31.6, (0.15, -2.45, 0.82), (0.30, 0.0, 0.80), 38),     # 7 done (Clawd left, caption right)
        (33.5, (0.25, -4.20, 0.82), (0.75, 0.2, 0.66), 35),     #   pull back: Clawd hops back into the screen
        (35.0, (0.22, -4.32, 0.82), (0.75, 0.2, 0.66), 35),
    ]
    eye = keys(t, [(s[0], s[1]) for s in shots], in_out3)
    tgt = keys(t, [(s[0], s[2]) for s in shots], in_out3)
    lens = keys(t, [(s[0], (s[3],)) for s in shots])[0]
    look_at(CAM, eye, tgt)
    cam_data.lens = lens
    face = (0.0, -0.16, 0.3)
    card = (0.0, -0.18, 1.17)
    bubble = world_point(CHAT, panel_point(0.86, 0.64, CW, CH)) if OPEN_T < t < SWITCH_T else (0, 0.3, 1.2)
    button = world_point(PERM, panel_point((BTN[0] + BTN[2]) / 2, (BTN[1] + BTN[3]) / 2, PERM_W, PERM_H)) if OPEN_T < t < SWITCH_T else (0, 0.3, 1.0)
    pop_c = (0.0, 0.05, 1.22)
    composer = comp
    prompt = term_lo
    on_screen = (SCREEN_POS.x, SCREEN_POS.y, SCREEN_POS.z + 0.08)
    fpts = [(0, phone), (5.8, phone), (6.4, on_screen), (6.8, on_screen), (7.6, face), (8.8, card), (11.8, card), (12.8, face), (13.6, face), (14.4, pop_c),
            (15.6, bubble), (16.0, bubble), (16.5, button), (17.3, button), (18.0, face), (18.6, face), (19.4, composer),
            (22.6, composer), (24.0, pop_c), (26.2, pop_c), (27.2, prompt), (30.2, prompt), (31.4, face), (33.2, face),
            (33.7, on_screen), (34.4, on_screen), (35, on_screen)]
    FOCUS.location = keys(t, fpts, in_out3)
    cam_data.dof.aperture_fstop = keys(t, [(0, (2.0,)), (6.4, (2.0,)), (7.8, (2.8,)), (11.6, (2.8,)), (14.6, (3.2,)),
                                         (15.8, (1.8,)), (17.2, (1.8,)), (18.2, (2.8,)), (19.4, (2.4,)), (22.6, (2.4,)),
                                         (24.0, (3.2,)), (26.2, (3.2,)), (27.2, (2.4,)), (30.2, (2.4,)), (31.6, (2.4,)), (33.7, (2.8,))])[0]

def apply_world(t):
    for p in PETALS: p.hide_render = p.hide_viewport = True

def update(t):
    apply_clawd(t)
    apply_chips(t)
    apply_cards(t)
    apply_panels(t)
    apply_cursor(t)
    apply_phone(t)
    apply_particles(t)
    apply_camera(t)
    apply_world(t)

def on_frame(sc, depsgraph=None):
    update((sc.frame_current + sc.frame_subframe) / FPS)

bpy.app.handlers.frame_change_pre.clear()
bpy.app.handlers.frame_change_pre.append(on_frame)

# ----------------------------------------------------------------------------- render
os.makedirs(OUT, exist_ok=True)
if arg("--save-blend"):
    bpy.ops.wm.save_as_mainfile(filepath=arg("--save-blend"))

import time
if STILLS:
    for f in [int(x) for x in STILLS.split(",")]:
        t0 = time.time()
        scene.frame_set(f)
        scene.render.filepath = os.path.join(OUT, f"still_{f:04d}.png")
        bpy.ops.render.render(write_still=True)
        print(f"STILL {f} {time.time() - t0:.1f}s")
else:
    a, b = [int(x) for x in FRAMES.split("-")]
    scene.frame_start, scene.frame_end = a, b
    scene.frame_step = int(arg("--step", "1"))
    scene.render.filepath = os.path.join(OUT, "f_")
    scene.render.use_overwrite = False   # resume: skip frames already on disk
    scene.render.use_placeholder = True
    t0 = time.time()
    bpy.ops.render.render(animation=True)
    print(f"ANIM {a}-{b} {time.time() - t0:.1f}s")
