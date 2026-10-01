#!/usr/bin/env bash
# The perf route (#239) on every tier, on both GPUs of the dev laptop. Appends to $OUT.
#
#   tools/perf-route.sh                       # dGPU Medium High Ultra, iGPU Low Medium High
#   OUT=/d/Builds/perf/scratch.md tools/perf-route.sh   # a scratch run, kept out of docs
#
# The iGPU run uses a copy of the build at D:\Builds\EWYF-igpu, pinned to the power-saving GPU by a
# per-exe Windows GPU preference (HKCU\Software\Microsoft\DirectX\UserGpuPreferences,
# "GpuPreference=1;"). The script refreshes the copy and the preference, and fails a run whose log
# does not name the GPU it was meant for.
set -u
OUT="${OUT:-$(cd "$(dirname "$0")/.." && pwd)/docs/PERF.md}"
COMMIT="$(git -C "$(dirname "$0")/.." rev-parse --short HEAD)"
mkdir -p "$(dirname "$OUT")" /d/Builds/perf

cp -r /d/Builds/EWYF-dev/. /d/Builds/EWYF-igpu/
reg add 'HKCU\Software\Microsoft\DirectX\UserGpuPreferences' //v 'D:\Builds\EWYF-igpu\EscapeWithYourFriends.exe' \
    //t REG_SZ //d 'GpuPreference=1;' //f > /dev/null

run() { # folder gpu-substring tier
  local log="D:/Builds/perf/$2_$3.log"
  "D:/Builds/$1/EscapeWithYourFriends.exe" -logFile "$log" -host -port 8150 -playerKey test:host -scene island \
    -quality "$3" -screen-fullscreen 1 -screen-width 1920 -screen-height 1080 \
    -perfRoute "$(cygpath -w "$OUT")" -commit "$COMMIT" -quitAfter 400 > /dev/null
  if ! grep -qa "\[GraphicsBoot\].*$2" "$log"; then echo "FAILED: $3 did not run on the $2"; else echo "$2 $3 done"; fi
}

for tier in Medium High Ultra; do run EWYF-dev 4060 "$tier"; done
for tier in Low Medium High; do run EWYF-igpu 760M "$tier"; done
