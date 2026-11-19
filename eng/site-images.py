#!/usr/bin/env python3
"""Builds the landing page's pictures (site/assets/img) from the CI captures in docs/screenshots.

    python3 eng/site-images.py            # from the screenshots CI has produced so far
    python3 eng/site-images.py --in-use   # prefer 18-goals-in-use and 19-session-widgets-in-use once CI has made them
    python3 eng/site-images.py --out DIR  # write somewhere else (a dry run that leaves site/assets/img alone)

Needs Pillow and ffmpeg. Every capture has a 1 px window-edge line (the focus border), which is cropped off here.

The loops (picker, drag, resize) are made from the frame sequences in docs/screenshots/anim/<name>/NN.png, which
eng/screenshots.ps1 records at full window size (1000 x 660), about 15 per second, up to 48 frames per clip:

  picker  "+" clicked, rail slides in, pointer on the Goals rail item, flyout, pointer on the "Add Goals & Tasks" banner,
          click, the widget lands on the board, Esc closes the rail
  drag    pointer on the first widget, 600 ms long press, slow carry onto the second widget, drop
  resize  Edit, pointer on the first widget's corner, drag one column right, release, Done

Frames are cropped to the page area only (no sidebar, no title bar, no 1 px edge) and are never upscaled; they are scaled down
only when the crop is wider than MAX_WIDTH. The picker crop keeps the top of the page, where the rail and its flyout are; the
drag and resize crops start just above the "Widgets" header, so the idle session clock is not in the picture. The first
frame is held for 0.7 s and the last one for 1 s, the encode is H.264 (crf 18, yuv420p) and VP9 (crf 30), and each poster is
the most telling frame of its clip.
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
if "--out" in sys.argv:
    OUT = os.path.abspath(sys.argv[sys.argv.index("--out") + 1])


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


def session_scroll(im):
    """How far the Session page sits below where the coordinates in running_session were measured: the top of the green
    Start session button was at y 249 there. 0 when it is the same, 65 for a capture of the page scrolled to the top."""
    for y in range(150, 460):
        r, g, b = im.getpixel((520, y))
        if g > b + 40 and g > 90:
            return y - 249
    return 0


def running_session(im):
    """19-session-widgets-in-use is captured idle: the clock shows its placeholder dashes and there is a Start session button.
    The landing page shows a running session, so the clock face and the button row are painted over with the canvas colour,
    a time is set in the app's clock style (Segoe UI Variable Display Light 74 px, tabular figures, Text.Primary; Inter Display
    Light is the closest face installed here) and the 460 x 4 session progress bar from DashboardPage.xaml takes the buttons' place.
    Coordinates are in the 1000 x 660 capture, before the 1 px edge is cropped; they were measured on a page scrolled 65 px and
    are moved by dy, the distance the page is actually scrolled from that (0 then, 65 for an unscrolled capture)."""
    dy = session_scroll(im)
    canvas = im.getpixel((400, 150))                      # Blocker.Bg.Canvas, flat around the clock
    d = ImageDraw.Draw(im)
    d.rectangle((300, 150 + dy, 994, 300 + dy), fill=canvas)  # clock face, buttons: nothing else lives in this band
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
    cx, baseline = 611, 206 + dy                          # centre of the old clock and of the button row; baseline of the old colon dots
    x = cx - glyphs.width // 2
    y = baseline - glyphs.height
    ink = (238, 240, 244)                                 # Text.Primary #EEF0F4
    im.paste(Image.new("RGB", glyphs.size, ink), (x, y), glyphs)
    # ProgressBar: 460 x 4, track #16181F, fill Blocker.Accent.Bar #7C913D, about 30 % of a two hour session elapsed.
    bx, by = cx - 230, 246 + dy
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
    save(Image.open(goals).convert("RGB").crop((255, 108, 968, 300)), "plugin-goals.webp")
else:
    save(Image.open(goals).convert("RGB").crop((255, 95, 968, 397)), "plugin-goals.webp")

# --- Loops ------------------------------------------------------------------------------------------------------------
FPS = 15                # what eng/screenshots.ps1 records at
HOLD_START = 0.7        # seconds the first frame stays
HOLD_END = 1.0          # seconds the last frame stays (the result)
MAX_WIDTH = 900         # wider crops are scaled down to this; narrower ones are left alone
SIDEBAR = 218           # the left navigation, in pixels of a 1000 px wide capture
CLIPS = {
    # name: (crop top as a fraction of the frame height, poster frame as a fraction of the clip)
    # picker: from just above the rail's close button, so the rail and its flyout are whole; poster: pointer on the banner,
    #         flyout open (just before the click)
    "picker": (0.085, 0.68),
    # drag and resize: from just above the "Widgets" header (y 422 of 660); drag poster: widget lifted, mid-carry;
    #         resize poster: corner pulled out, mid-resize
    "drag": (0.575, 0.62),
    "resize": (0.575, 0.52),
}

if not shutil.which("ffmpeg"):
    sys.exit("ffmpeg not found: skipped the loops")


def clip_box(im, top_fraction):
    """The page area of a full-size frame as (left, top, right, bottom): right of the sidebar, below the crop top, inside the
    1 px edge. Scales with the frame (a half-size frame gets half the margins) and has even width and height for yuv420p."""
    w, h = im.size
    k = w / 1000
    left = round(SIDEBAR * k)
    right = w - max(1, round(k))
    top = round(h * top_fraction)
    bottom = h - max(1, round(k))
    right -= (right - left) % 2
    bottom -= (bottom - top) % 2
    return left, top, right, bottom


def frame(path, top_fraction):
    """A recorded frame cropped to the page area, scaled down only when it is wider than MAX_WIDTH (never up)."""
    im = Image.open(path).convert("RGB")
    out = im.crop(clip_box(im, top_fraction))
    if out.width > MAX_WIDTH:
        w = MAX_WIDTH - MAX_WIDTH % 2
        h = round(out.height * w / out.width)
        out = out.resize((w, h - h % 2), Image.LANCZOS)
    return out


for name, (top_fraction, poster_at) in CLIPS.items():
    frames = sorted(glob.glob(os.path.join(SHOTS, "anim", name, "*.png")))
    if not frames:
        print("no frames for", name)
        continue
    os.makedirs(OUT, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        hold_a, hold_b = round(HOLD_START * FPS), round(HOLD_END * FPS)
        seq = [frames[0]] * hold_a + frames + [frames[-1]] * hold_b
        cache = {}
        for i, f in enumerate(seq):
            if f not in cache:
                cache[f] = frame(f, top_fraction)
            cache[f].save(os.path.join(tmp, "%03d.png" % i))
        size = cache[frames[0]].size
        src = os.path.join(tmp, "%03d.png")
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", src,
                        "-c:v", "libx264", "-profile:v", "high", "-crf", "18", "-preset", "slow",
                        "-pix_fmt", "yuv420p", "-r", str(FPS), "-movflags", "+faststart", "-an",
                        os.path.join(OUT, name + ".mp4")], check=True)
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", src,
                        "-c:v", "libvpx-vp9", "-crf", "30", "-b:v", "0", "-pix_fmt", "yuv420p", "-r", str(FPS), "-an",
                        os.path.join(OUT, name + ".webm")], check=True)
    pf = frames[min(len(frames) - 1, round(poster_at * len(frames)))]
    print(name, len(frames), "frames", "%dx%d" % size, "poster", os.path.basename(pf))
    save(cache[pf], name + "-poster.webp", 95)
    for ext in ("mp4", "webm"):
        p = os.path.join(OUT, name + "." + ext)
        print("wrote", name + "." + ext, os.path.getsize(p) // 1024, "KB")
