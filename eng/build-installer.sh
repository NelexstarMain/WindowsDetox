#!/usr/bin/env bash
# Builds out/WindowsDetox-Setup.exe: every executable is published self-contained (ReadyToRun) into one shared
# folder, zipped, and embedded into a single-file Blocker.Setup. Works on Linux and Windows (bash).
# Usage: eng/build-installer.sh [Release|Debug]   (Debug = DEVMODE build, see docs/VM_TESTING.md)
set -euo pipefail
cd "$(dirname "$0")/.."
config="${1:-Release}"
out="out"
app="$out/app"
rm -rf "$out"
mkdir -p "$app"
export NO_COLOR=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

for project in Blocker.Service Blocker.Watchdog Blocker.PluginHost Blocker.Stub Blocker.UI Blocker.Setup; do
  echo "publish $project"
  dotnet publish "src/$project" -c "$config" -r win-x64 --self-contained -o "$app" -p:DebugType=none -p:GenerateDocumentationFile=false -v quiet -nologo > "$out/publish-$project.log" 2>&1 \
    || { cat "$out/publish-$project.log"; exit 1; }
done
find "$app" \( -name '*.pdb' -o -name '*.xml' \) -delete

echo "zip payload"
python3 - "$app" "$out/payload.zip" <<'PY'
import os, sys, zipfile
root, target = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for base, _, files in os.walk(root):
        for name in sorted(files):
            path = os.path.join(base, name)
            z.write(path, os.path.relpath(path, root).replace(os.sep, "/"))
PY

echo "publish installer"
dotnet publish src/Blocker.Setup -c "$config" -r win-x64 --self-contained -o "$out/setup" \
  -p:SetupPayload="$(pwd)/$out/payload.zip" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:PublishReadyToRun=false -v quiet -nologo > "$out/publish-setup.log" 2>&1 \
  || { cat "$out/publish-setup.log"; exit 1; }
mv "$out/setup/Blocker.Setup.exe" "$out/WindowsDetox-Setup.exe"
rm -rf "$out/setup"
ls -lh "$out/WindowsDetox-Setup.exe"
