#!/usr/bin/env bash
# Renders the real WPF UI to PNG files on Linux: publishes tools/Blocker.UI.Render (win-x64, self-contained) and runs it under
# Wine + Xvfb. One process renders every scenario of tools/Blocker.UI.Render/scenarios.json.
# Usage: eng/render.sh [--only name1,name2] [--out dir]      (default output: artifacts/renders)
# Needs: dotnet SDK, wine64, Xvfb, python3. First run: publishes the harness and sets up the Wine prefix and fonts (about a minute);
# later runs republish only when sources changed and render all shots in a few seconds.
# Wine crashes with a large environment, so it always runs under `env -i`.
set -euo pipefail
cd "$(dirname "$0")/.."
repo="$(pwd)"
out="$repo/artifacts/renders"
passthrough=()
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out="$(realpath -m "$2")"; shift 2 ;;
    --only) passthrough+=(--only "$2"); shift 2 ;;
    *) echo "unknown option $1 (use --only a,b and --out dir)" >&2; exit 2 ;;
  esac
done

display=":99"
screen="1920x2600x24"   # tall: Windows clamps a window to the screen, and some shots are taller than a desktop
prefix="${RENDER_WINEPREFIX:-$HOME/.wine-wd}"
harness="$repo/artifacts/render-harness"
scenarios="$repo/tools/Blocker.UI.Render/scenarios.json"
wine_bin="$(command -v wine64 || true)"
[ -n "$wine_bin" ] || wine_bin=/usr/lib/wine/wine64
wineserver_bin="$(command -v wineserver || true)"
for candidate in /usr/lib/wine/wineserver /usr/lib/wine/wineserver64; do
  if [ -z "$wineserver_bin" ] && [ -x "$candidate" ]; then wineserver_bin="$candidate"; fi
done
fonts="$HOME/.local/share/fonts/wd"

# Runs a command with the minimal environment Wine tolerates (PGO instrumentation only slows a short-lived process down).
clean() { env -i HOME="$HOME" PATH=/usr/bin:/bin DISPLAY="$display" WINEPREFIX="$prefix" WINEDEBUG=-all DOTNET_TieredPGO=0 "$@"; }
winpath() { printf 'Z:%s' "$(realpath -m "$1" | tr / '\\')"; }
now_ms() { date +%s%3N; }
total_start=$(now_ms)

# 1. Publish the harness when any source changed since the last publish (scenarios.json is read from the repository, never published).
stamp="$harness/.published"
stale=1
if [ -f "$stamp" ] && [ -z "$(find tools/Blocker.UI.Render src plugins Directory.Build.props Directory.Build.targets Directory.Packages.props global.json \
    -type f ! -path '*/obj/*' ! -path '*/bin/*' ! -name scenarios.json ! -name '*.md' -newer "$stamp" -print -quit)" ]; then
  stale=0
fi
publish_ms=0
if [ "$stale" = 1 ]; then
  echo "publish harness"
  start=$(now_ms)
  # ReadyToRun would add about a minute for a second of startup; the harness is a developer tool.
  dotnet publish tools/Blocker.UI.Render -c Release -r win-x64 --self-contained -p:EnableWindowsTargeting=true \
    -p:PublishReadyToRun=false --disable-build-servers -o "$harness" -v quiet -nologo > "$repo/artifacts/render-publish.log" 2>&1 \
    || { cat "$repo/artifacts/render-publish.log"; exit 1; }
  touch "$stamp"
  publish_ms=$(( $(now_ms) - start ))
fi

# 2. Fonts: WPF needs "Segoe UI"; Wine only knows host fonts, so Inter is installed under that name (Liberation Sans as Arial, ...).
#    The wineserver reads the font list when it starts, hence the restart after the first install.
restart_wine=0
if [ ! -f "$fonts/.installed" ]; then
  python3 -c 'import fontTools' 2>/dev/null || python3 -m pip install --quiet --target "$repo/artifacts/render-pylib" fonttools
  PYTHONPATH="$repo/artifacts/render-pylib${PYTHONPATH:+:$PYTHONPATH}" python3 - "$fonts" <<'PY'
import glob, os, sys
from fontTools.ttLib import TTFont

dest = sys.argv[1]
os.makedirs(dest, exist_ok=True)
inter = "/usr/share/fonts/opentype/inter/Inter-%s.otf"
liberation = "/usr/share/fonts/truetype/liberation/LiberationSans-%s.ttf"
mono = ["/usr/share/fonts/truetype/jetbrains-mono/JetBrainsMono-%s.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSansMono%s.ttf"]
jobs = [(inter % s, "Segoe UI", s, w, False, w >= 700) for s, w in (("Light", 300), ("Regular", 400), ("SemiBold", 600), ("Bold", 700))]
jobs += [(liberation % s, "Arial", s, 700 if "Bold" in s else 400, "Italic" in s, "Bold" in s) for s in ("Regular", "Bold", "Italic", "BoldItalic")]
for style, bold in (("Regular", False), ("Bold", True)):
    source = mono[0] % style if os.path.exists(mono[0] % style) else mono[1] % ("-Bold" if bold else "")
    jobs += [(source, "Cascadia Mono", style, 700 if bold else 400, False, bold), (source, "Consolas", style, 700 if bold else 400, False, bold)]
jobs.append(("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "Segoe UI Symbol", "Regular", 400, False, False))

for source, family, style, weight, italic, bold in jobs:
    if not os.path.exists(source):
        print("missing font", source, file=sys.stderr)
        continue
    font = TTFont(source)
    names = font["name"]
    legacy_style = "Bold Italic" if bold and italic else "Bold" if bold else "Italic" if italic else "Regular"
    face = style.replace("BoldItalic", "Bold Italic")
    for record in list(names.names):
        if record.nameID in (1, 2, 3, 4, 6, 16, 17, 21, 22, 25):
            names.removeNames(nameID=record.nameID)
    for platform, encoding, language in ((3, 1, 0x409), (1, 0, 0)):
        names.setName(family if face in ("Regular", "Bold", "Italic", "Bold Italic") else f"{family} {face}", 1, platform, encoding, language)
        names.setName(legacy_style, 2, platform, encoding, language)
        names.setName(f"{family} {face}", 4, platform, encoding, language)
        names.setName(f"{family}-{style}".replace(" ", ""), 6, platform, encoding, language)
        names.setName(family, 16, platform, encoding, language)
        names.setName(face, 17, platform, encoding, language)
    os2 = font["OS/2"]
    os2.usWeightClass = weight
    selection = os2.fsSelection & ~0b1100001
    selection |= (1 if italic else 0) | (0b100000 if bold else 0) | (0b1000000 if not italic and not bold else 0)
    os2.fsSelection = selection
    font.save(os.path.join(dest, f"{family}-{style}".replace(" ", "") + os.path.splitext(source)[1]))
PY
  touch "$fonts/.installed"
  restart_wine=1
fi

# 3. Xvfb (restarted when it has another size) and a warm wineserver.
if ! pgrep -f "Xvfb $display -screen 0 $screen" > /dev/null; then
  if pgrep -f "Xvfb $display" > /dev/null; then
    pkill -f "Xvfb $display" || true
    restart_wine=1
    sleep 1
  fi
  setsid Xvfb "$display" -screen 0 "$screen" -nolisten tcp > /dev/null 2>&1 &
  for _ in $(seq 50); do [ -e "/tmp/.X11-unix/X${display#:}" ] && break; sleep 0.1; done
fi
if [ "$restart_wine" = 1 ]; then clean "$wineserver_bin" -k < /dev/null > /dev/null 2>&1 || true; sleep 1; fi
if [ ! -f "$prefix/system.reg" ]; then
  echo "create wine prefix $prefix"
  clean "$wine_bin" wineboot -u < /dev/null > /dev/null 2>&1 || true
fi
if ! pgrep -f 'wineserver(64)? .*-p' > /dev/null; then
  setsid env -i HOME="$HOME" PATH=/usr/bin:/bin DISPLAY="$display" WINEPREFIX="$prefix" WINEDEBUG=-all "$wineserver_bin" -p > /dev/null 2>&1 &
  sleep 0.5
fi

# 4. Render everything in one process.
mkdir -p "$out"
rm -f "$out"/*.png
echo "render -> $out"
start=$(now_ms)
status=0
# The output goes through a file: the persistent wineserver keeps a copy of the stdio handles of the processes it starts, so a pipe given
# to Wine would never reach end-of-file for the caller (CI log, `| tail`).
log="$out/render.log"
clean timeout 600 "$wine_bin" "$(winpath "$harness/Blocker.UI.Render.exe")" --out "$(winpath "$out")" --scenarios "$(winpath "$scenarios")" "${passthrough[@]}" < /dev/null > "$log" 2>&1 || status=$?
cat "$log"
render_ms=$(( $(now_ms) - start ))
count=$(find "$out" -name '*.png' | wc -l)
echo "publish ${publish_ms} ms, render process ${render_ms} ms, script total $(( $(now_ms) - total_start )) ms, ${count} images in ${out}"
[ "$status" = 0 ] && [ "$count" -gt 0 ]
