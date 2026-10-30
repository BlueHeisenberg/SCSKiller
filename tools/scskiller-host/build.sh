#!/bin/sh
# scskiller-host for the Flatpak: a static x86-64 binary with musl, built by a pinned zig, so it runs on whatever libc
# the host has. The app id is the Flatpak's: the helper allows the data folder under ~/.var/app/<app id> alone.
# Usage: tools/scskiller-host/build.sh <app id> [output file]   (default dist/linux/scskiller-host)
# ZIG names a zig of the pinned version to use, else the pinned tarball is downloaded and checked against its sha256.
set -eu
zig_version=0.17.0
# zig-x86_64-linux-$zig_version.tar.xz's sha256, as ziglang.org/download/index.json lists it
zig_sha256=
here=$(cd "$(dirname "$0")" && pwd)
src=$(cd "$here/../.." && pwd)
app_id=${1:?usage: build.sh <app id> [output file]}
out=${2:-$src/dist/linux/scskiller-host}
case $app_id in *[!A-Za-z0-9._-]* | .* | *..*) echo "not an app id: $app_id" >&2; exit 1 ;; esac

zig=${ZIG:-}
if [ -z "$zig" ]; then
  if [ -z "$zig_sha256" ]; then
    echo "zig_sha256 isn't pinned in $0 yet: set ZIG to a zig $zig_version" >&2
    exit 1
  fi
  name=zig-x86_64-linux-$zig_version
  cache=${XDG_CACHE_HOME:-$HOME/.cache}/scskiller-zig
  mkdir -p "$cache"
  if [ ! -x "$cache/$name/zig" ]; then
    curl -fsSL -o "$cache/$name.tar.xz.part" "https://ziglang.org/download/$zig_version/$name.tar.xz"
    if ! echo "$zig_sha256  $cache/$name.tar.xz.part" | sha256sum -c - >/dev/null; then
      echo "$name.tar.xz doesn't match the pinned sha256" >&2
      rm -f "$cache/$name.tar.xz.part"
      exit 1
    fi
    tar -xJf "$cache/$name.tar.xz.part" -C "$cache"
    rm -f "$cache/$name.tar.xz.part"
  fi
  zig=$cache/$name/zig
fi
if [ "$("$zig" version)" != "$zig_version" ]; then
  echo "$zig is not zig $zig_version" >&2
  exit 1
fi

mkdir -p "$(dirname "$out")"
"$zig" cc -target x86_64-linux-musl -static -std=c11 -O2 -s -Wall -Wextra -Wpedantic -Werror \
  -DSCSKILLER_APP_ID="\"$app_id\"" -o "$out" "$here/scskiller-host.c"
"$here/check-static.sh" "$out"
