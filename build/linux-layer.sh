#!/bin/sh
# libVkLayer_scskiller.so and selftest_linux, and the Vulkan warm's scskiller_warm and fossilize-replay, built in the Steam
# Runtime's sniper SDK (glibc 2.31): the layer loads into games inside pressure-vessel's containers and on older
# distributions, so it fails here when one of them needs a newer glibc.
# Usage: [SCSK_VERSION=X.Y.Z[-pre]] [SCSK_MAKE_JOBS=N] [SCSK_ARCH=x86_64|aarch64] build/linux-layer.sh [output folder]
# (default proxy/build-linux, proxy/build-linux-arm64 for aarch64; podman or docker, make on every CPU)
# SCSK_VERSION as publish.ps1 stamps the Windows build: the default 0.0.0-internal.0 keeps the replayer's test hooks.
# SCSK_ARCH: the CPU the files are for, default the machine's own. aarch64 builds in sniper's arm64 SDK (same glibc 2.31);
# on an x86-64 machine that takes qemu-user's binfmt (slow), an arm64 machine runs it natively.
set -eu
arch=${SCSK_ARCH:-$(uname -m)}
case $arch in
  x86_64 | amd64)
    arch=x86_64 platform= dir=build-linux
    image=registry.gitlab.steamos.cloud/steamrt/sniper/sdk@sha256:9ae8083ee9ae6298361a62f289e62f0a2b0779d23fc8c276304157f0c06010c5 ;;
  aarch64 | arm64)
    arch=aarch64 platform=linux/arm64 dir=build-linux-arm64
    # 3.0.20260507.232682
    image=registry.gitlab.steamos.cloud/steamrt/sniper/sdk/arm64@sha256:966ad3ef70751b98253f031d22c5b75798ceb9c49612b0a3489fc5f8f58858fc ;;
  *) echo "linux-layer: no Steam Runtime SDK for $arch (SCSK_ARCH: x86_64 or aarch64)" >&2; exit 1 ;;
esac
src=$(cd "$(dirname "$0")/.." && pwd)
out=${1:-$src/proxy/$dir}
mkdir -p "$out"
out=$(cd "$out" && pwd)
engine=$(command -v podman || command -v docker)
"$engine" run --rm ${platform:+--platform "$platform"} -e SCSK_VERSION="${SCSK_VERSION:-0.0.0-internal.0}" -e SCSK_MAKE_JOBS="${SCSK_MAKE_JOBS:-}" -v "$src:/src:ro" -v "$out:/build" -w /build "$image" \
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
