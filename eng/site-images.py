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

from PIL import Image, ImageDraw, ImageFont

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


def running_session(im):
    """19-session-widgets-in-use is captured idle: the clock shows its placeholder dashes and there is a Start session button.
    The landing page shows a running session, so the clock face and the button row are painted over with the canvas colour,
    a time is set in the app's clock style (Segoe UI Variable Display Light 74 px, tabular figures, Text.Primary; Inter Display
    Light is the closest face installed here) and the 460 x 4 session progress bar from DashboardPage.xaml takes the buttons' place.
    Coordinates are in the 1000 x 660 capture, before the 1 px edge is cropped."""
    canvas = im.getpixel((400, 150))                      # Blocker.Bg.Canvas, flat around the clock
    d = ImageDraw.Draw(im)
    d.rectangle((300, 150, 930, 300), fill=canvas)        # clock face, buttons: nothing else lives in this band
    face = None
    for name in ("InterDisplay-Light.otf", "Inter-Light.otf"):
        for base in ("/usr/share/fonts/opentype/inter", "/usr/share/fonts/truetype/inter", os.path.expanduser("~/.fonts")):
            path = os.path.join(base, name)
            if os.path.exists(path):
                face = ImageFont.truetype(path, 72, layout_engine=ImageFont.Layout.RAQM)
                break
        if face:
            break
    if face is None:
        sys.exit("Inter Display Light not found: cannot paint the session clock")
    text = "01:24:37"
    feats = ["tnum", "case"]
    # Hold the text to a 4x canvas and shrink it, which gives the soft grey-scale edges of the app's own rendering.
    k = 4
    big = Image.new("L", (900 * k, 160 * k), 0)
    f4 = face.font_variant(size=72 * k)
    ImageDraw.Draw(big).text((20 * k, 10 * k), text, font=f4, fill=255, features=feats)
    box = big.getbbox()
    mask = big.resize((big.width // k, big.height // k), Image.LANCZOS)
    mbox = (box[0] // k, box[1] // k, -(-box[2] // k), -(-box[3] // k))
    glyphs = mask.crop(mbox)
    cx, baseline = 611, 206                               # centre of the old clock and of the button row; baseline of the old colon dots
    x = cx - glyphs.width // 2
    y = baseline - glyphs.height
    ink = (238, 240, 244)                                 # Text.Primary #EEF0F4
    im.paste(Image.new("RGB", glyphs.size, ink), (x, y), glyphs)
    # ProgressBar: 460 x 4, track #16181F, fill Blocker.Accent.Bar #7C913D, about 30 % of a two hour session elapsed.
    bx, by = cx - 230, 246
    d.rectangle((bx, by, bx + 459, by + 3), fill=(22, 24, 31))
    d.rectangle((bx, by, bx + 459 * 30 // 100, by + 3), fill=(124, 145, 61))
    return im


hero = Image.open(session).convert("RGB")
if session_new:
    hero = running_session(hero)
save(inner(hero), "hero-session.webp")
save(inner(Image.open(shot("07-block-screen")).convert("RGB")), "hero-blocked.webp")

# The page area right of the sidebar (x 215+), without the title bar line.
save(Image.open(shot("03-allowlist")).convert("RGB").crop((215, 1, 999, 659)), "step-allowlist.webp")
save(Image.open(shot("04-session-setup")).convert("RGB").crop((215, 1, 999, 659)), "step-setup.webp")

# Plugin pages: the page title and the first cards only, so the text stays large when the picture is shown near full size.
save(Image.open(shot("09-insights")).convert("RGB").crop((255, 100, 968, 388)), "plugin-insights.webp")
goals, goals_new = pick("18-goals-in-use", "16-goals")
if goals_new:
    # 18 is scrolled to its task list: keep the page area from the daily goal card down.
    save(Image.open(goals).convert("RGB").crop((255, 108, 968, 410)), "plugin-goals.webp")
else:
    save(Image.open(goals).convert("RGB").crop((255, 95, 968, 397)), "plugin-goals.webp")

# --- Loops ------------------------------------------------------------------------------------------------------------
CROP = (108, 28, 500, 320)  # the session page without its sidebar: timer, buttons and the widget board (392x292)
SCALE = 1.5
FPS = 12
CLIPS = {
    # name: (frames to hold at the start, frames to hold at the end, poster frame)
    "picker": (5, 7, 13),
    "drag": (2, 9, 7),
    "resize": (4, 9, 12),
}

if not shutil.which("ffmpeg"):
    sys.exit("ffmpeg not found: skipped the loops")

size = (int(CROP[2] - CROP[0]) * 3 // 2, int(CROP[3] - CROP[1]) * 3 // 2)


def frame(path):
    """Crops a recorded frame to the board; CROP is in half-size (500 px wide) coordinates, so full-size frames scale it."""
    im = Image.open(path).convert("RGB")
    k = im.width / 500
    return im.crop(tuple(round(v * k) for v in CROP)).resize(size, Image.LANCZOS)


for name, (hold_a, hold_b, poster) in CLIPS.items():
    frames = sorted(glob.glob(os.path.join(SHOTS, "anim", name, "*.png")))
    if not frames:
        print("no frames for", name)
        continue
    with tempfile.TemporaryDirectory() as tmp:
        seq = [frames[0]] * hold_a + frames + [frames[-1]] * hold_b
        for i, f in enumerate(seq):
            im = frame(f)
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
    save(frame(pf), name + "-poster.webp", 90)
    for ext in ("mp4", "webm"):
        p = os.path.join(OUT, name + "." + ext)
        print("wrote", name + "." + ext, os.path.getsize(p) // 1024, "KB")
