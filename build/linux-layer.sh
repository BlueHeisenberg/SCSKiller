#!/bin/sh
# libVkLayer_scskiller.so and selftest_linux, and the Vulkan warm's scskiller_warm and fossilize-replay, built in the Steam
# Runtime's sniper SDK (glibc 2.31): the layer loads into games inside pressure-vessel's containers and on older
# distributions, so it fails here when one of them needs a newer glibc.
# Usage: [SCSK_VERSION=X.Y.Z[-pre]] [SCSK_MAKE_JOBS=N] build/linux-layer.sh [output folder]   (default proxy/build-linux;
# podman or docker, make on every CPU)
# SCSK_VERSION as publish.ps1 stamps the Windows build: the default 0.0.0-internal.0 keeps the replayer's test hooks.
set -eu
image=registry.gitlab.steamos.cloud/steamrt/sniper/sdk@sha256:9ae8083ee9ae6298361a62f289e62f0a2b0779d23fc8c276304157f0c06010c5
src=$(cd "$(dirname "$0")/.." && pwd)
out=${1:-$src/proxy/build-linux}
mkdir -p "$out"
out=$(cd "$out" && pwd)
engine=$(command -v podman || command -v docker)
"$engine" run --rm -e SCSK_VERSION="${SCSK_VERSION:-0.0.0-internal.0}" -e SCSK_MAKE_JOBS="${SCSK_MAKE_JOBS:-}" -v "$src:/src:ro" -v "$out:/build" -w /build "$image" \
  sh -c 'CC=gcc-14 CXX=g++-14 cmake -S /src/proxy -B . -DCMAKE_BUILD_TYPE=Release -DSCSK_VERSION="$SCSK_VERSION" && make -j"${SCSK_MAKE_JOBS:-$(nproc)}" VkLayer_scskiller selftest_linux scskiller_warm fossilize-replay'
for f in libVkLayer_scskiller.so scskiller_warm fossilize-replay; do
  dump=$(objdump -T "$out/$f")
  need=$(printf '%s\n' "$dump" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1)
  echo "$f needs ${need:-no glibc version}"
  if [ -z "$need" ] || [ "$(printf '%s\nGLIBC_2.31\n' "$need" | sort -V | tail -1)" != GLIBC_2.31 ]; then
    echo "$f: its glibc versions can't be read, or it needs one newer than 2.31" >&2
    exit 1
  fi
done
