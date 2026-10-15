#!/usr/bin/env bash
# Builds Shaderless's two Windows-side binaries with mingw-w64 (they run inside Proton):
#   src/SCSKiller.Linux.Core/Native/d3d12.dll          the recorder (recorder.cpp, exports as in d3d12.def)
#   src/SCSKiller.Linux.Core/Native/scskiller_warm.exe the warmer   (warmer.cpp + upstream proxy/warm11.cpp)
# Needs: x86_64-w64-mingw32-g++ (Arch: mingw-w64-gcc, Debian/Ubuntu: g++-mingw-w64-x86-64-posix)
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
N="$DIR/src/SCSKiller.Linux.Core/Native"
CXX="${CXX:-x86_64-w64-mingw32-g++}"
FLAGS=(-O2 -s -std=c++20 -static -static-libgcc -static-libstdc++)

echo "== d3d12.dll (recorder)"
"$CXX" "${FLAGS[@]}" -shared -o "$N/d3d12.dll" "$N/recorder.cpp" "$N/d3d12.def" -lbcrypt -ldxguid -luuid

echo "== scskiller_warm.exe (warmer)"
"$CXX" "${FLAGS[@]}" -municode -I"$DIR/upstream/proxy" -o "$N/scskiller_warm.exe" "$N/warmer.cpp" "$DIR/upstream/proxy/warm11.cpp" \
    -ld3d12 -ld3d11 -ldxgi -ld3dcompiler -lbcrypt -ldxguid -luuid -lole32

echo "done"
