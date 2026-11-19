#!/usr/bin/env python3
"""Builds the landing page's pictures (site/assets/img) from the CI captures in docs/screenshots.

    python3 eng/site-images.py            # from the screenshots CI has produced so far
    python3 eng/site-images.py --in-use   # prefer 18-goals-in-use and 19-session-widgets-in-use once CI has made them

Needs Pillow and ffmpeg. Every capture has a 1 px window-edge line (the focus border), which is cropped off here.
The loops are made from the frame sequences in docs/screenshots/anim (about 12 fps), cropped to the widget board,
scaled 1.5x with Lanczos, held for a moment at both ends, and encoded as MP4 and WebM with a WebP poster.
"""
import glob
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SHOTS = os.path.join(ROOT, "docs", "screenshots")
OUT = os.path.join(ROOT, "site", "assets", "img")
IN_USE = "--in-use" in sys.argv


def shot(name):
    return os.path.join(SHOTS, name + ".png")


def pick(in_use_name, fallback):
    if IN_USE and os.path.exists(shot(in_use_name)):
        return shot(in_use_name), True
    return shot(fallback), False


def save(im, name, quality=88):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    im.save(path, "WEBP", quality=quality, method=6)
    print("wrote", name, im.size, os.path.getsize(path) // 1024, "KB")


def inner(im):
    """The capture without its 1 px edge line."""
    w, h = im.size
    return im.crop((1, 1, w - 1, h - 1))


# --- Window captures --------------------------------------------------------------------------------------------------
session, session_new = pick("19-session-widgets-in-use", "02-session")
save(inner(Image.open(session).convert("RGB")), "hero-session.webp")
save(inner(Image.open(shot("07-block-screen")).convert("RGB")), "hero-blocked.webp")

# The page area right of the sidebar (x 215+), without the title bar line.
save(Image.open(shot("03-allowlist")).convert("RGB").crop((215, 1, 999, 659)), "step-allowlist.webp")
save(Image.open(shot("04-session-setup")).convert("RGB").crop((215, 1, 999, 659)), "step-setup.webp")

save(Image.open(shot("09-insights")).convert("RGB").crop((255, 95, 968, 595)), "plugin-insights.webp")
goals, goals_new = pick("18-goals-in-use", "16-goals")
if goals_new:
    # 18 is scrolled to its task list: keep the page area, full height.
    save(Image.open(goals).convert("RGB").crop((215, 1, 999, 659)), "plugin-goals.webp")
else:
    save(Image.open(goals).convert("RGB").crop((255, 95, 968, 595)), "plugin-goals.webp")

# --- Loops ------------------------------------------------------------------------------------------------------------
CROP = (108, 28, 500, 320)  # the session page without its sidebar: timer, buttons and the widget board (392x292)
SCALE = 1.5
FPS = 12
CLIPS = {
    # name: (frames to hold at the start, frames to hold at the end, poster frame)
    "picker": (5, 7, 11),
    "drag": (2, 9, 7),
    "resize": (4, 9, 12),
}

if not shutil.which("ffmpeg"):
    sys.exit("ffmpeg not found: skipped the loops")

size = (int(CROP[2] - CROP[0]) * 3 // 2, int(CROP[3] - CROP[1]) * 3 // 2)
for name, (hold_a, hold_b, poster) in CLIPS.items():
    frames = sorted(glob.glob(os.path.join(SHOTS, "anim", name, "*.png")))
    if not frames:
        print("no frames for", name)
        continue
    with tempfile.TemporaryDirectory() as tmp:
        seq = [frames[0]] * hold_a + frames + [frames[-1]] * hold_b
        for i, f in enumerate(seq):
            im = Image.open(f).convert("RGB").crop(CROP).resize(size, Image.LANCZOS)
            im.save(os.path.join(tmp, "%03d.png" % i))
        src = os.path.join(tmp, "%03d.png")
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", src,
                        "-c:v", "libx264", "-profile:v", "high", "-crf", "20", "-preset", "slow",
                        "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an",
                        os.path.join(OUT, name + ".mp4")], check=True)
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", src,
                        "-c:v", "libvpx-vp9", "-crf", "34", "-b:v", "0", "-pix_fmt", "yuv420p", "-an",
                        os.path.join(OUT, name + ".webm")], check=True)
    pf = frames[min(poster, len(frames)) - 1]
    save(Image.open(pf).convert("RGB").crop(CROP).resize(size, Image.LANCZOS), name + "-poster.webp", 90)
    for ext in ("mp4", "webm"):
        p = os.path.join(OUT, name + "." + ext)
        print("wrote", name + "." + ext, os.path.getsize(p) // 1024, "KB")
