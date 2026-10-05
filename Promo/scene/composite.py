"""Lays the captions and end title over the rendered frames and encodes the mp4 (ffmpeg).
All on-screen text comes from Promo/copy/<lang>.json (rendered to Promo/captions/<lang>/ by
ui/Textures.swift), so the 3D render itself has no text and one render serves every language.

    python3 Promo/scene/composite.py [--lang ko|en] [--frames DIR --step N] [--out FILE]
"""
import json, os, subprocess

PROMO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
import sys
def _arg(name, default):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
# --frames DIR --step N --out FILE: e.g. an animatic rendered every 2nd frame
STEP = int(_arg("--step", "1"))
FRAMES_DIR = None  # set after --lang is known
LANG = _arg("--lang", "ko")
FRAMES_DIR = _arg("--frames", os.path.join(PROMO, "out", f"frames_{LANG}"))
CAPS = os.path.join(PROMO, "captions", LANG)
OUT = _arg("--out", os.path.join(PROMO, "out", f"clawd-for-orca-promo-{LANG}.mp4"))
FPS = 30
MARGIN = 112
COPY = json.load(open(os.path.join(PROMO, "copy", f"{LANG}.json")))
# (image, start s, end s, placement)
CUES = [(c["id"] + ".png", c["start"], c["end"], c["placement"]) for c in COPY["cues"]]

def main():
    cmd = ["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS / STEP), "-pattern_type", "glob",
           "-i", os.path.join(FRAMES_DIR, "f_*.png")]
    for img, *_ in CUES:
        cmd += ["-loop", "1", "-framerate", str(FPS), "-t", "36", "-i", os.path.join(CAPS, img)]
    chains, last = ["[0:v]scale=1920:1080:flags=bicubic,fps=30[base]"], "[base]"
    for i, (img, st, en, anchor) in enumerate(CUES, start=1):
        fin, fout = 0.6, 0.45
        chains.append(f"[{i}:v]format=rgba,fade=t=in:st={st}:d={fin}:alpha=1,"
                      f"fade=t=out:st={en - fout}:d={fout}:alpha=1[c{i}]")
        # captions rise a few pixels as they fade in (ease-out cubic)
        rise = f"18*pow(1-min(1,max(0,(t-{st})/0.9)),3)"
        if anchor == "left":
            x, y = f"{MARGIN}", f"H-h-{MARGIN - 10}+{rise}"
        elif anchor == "center":
            x, y = "(W-w)/2", f"H-h-{MARGIN - 10}+{rise}"
        elif anchor == "right":
            x, y = f"W-w-{MARGIN}", f"H-h-{MARGIN - 10}+{rise}"
        else:
            x, y = "(W-w)/2", f"120+{rise}"
        out = f"[v{i}]"
        chains.append(f"{last}[c{i}]overlay=x={x}:y='{y}':eval=frame:enable='between(t,{st},{en})'{out}")
        last = out
    # fade from / to the set colour at the very ends
    chains.append(f"{last}fade=t=in:st=0:d=0.5:color=0xE4DDD5,format=yuv420p[out]")
    n_out = len([f for f in os.listdir(FRAMES_DIR) if f.startswith("f_") and f.endswith(".png")]) * STEP
    cmd += ["-filter_complex", ";".join(chains), "-map", "[out]", "-frames:v", str(n_out),
            "-c:v", "libx264", "-preset", "slow", "-crf", "16", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", "-r", str(FPS), OUT]
    subprocess.run(cmd, check=True)
    print("wrote", OUT)

if __name__ == "__main__":
    main()
