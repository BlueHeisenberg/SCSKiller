#!/usr/bin/env bash
# The Linux package as build.yml's build job makes it, in the Windows runner's WSL: build/publish-linux.sh on this
# checkout's Windows publish (dist/SCSKiller/native, dist/SHA256SUMS-unsigned.txt) and Linux layer (proxy/build-linux),
# then build/check-linux-package.sh, and with --pack build/pack-linux.sh as a release packs it. All in a clone of HEAD
# on Linux's own filesystem: a Windows folder keeps no file modes, which the package's executables need. A versioned
# package builds its own layer (build/linux-layer.sh with SCSK_VERSION): the tests' build keeps the replayer's test
# hooks.
# The runners share one WSL VM: one build at a time (a lock, waited for at most an hour), make on 16 jobs.
# Usage: build/ci-linux-package.sh --layer | [--version X.Y.Z[-pre]] [--pack | --release <dir>]
#   --layer    the Linux layer into proxy/build-linux (build/linux-layer.sh), then `selftest_linux vkrec`
#   --version  the Windows publish's version (default publish-linux.sh's)
#   --pack     vpk's Linux pack too, nothing kept (vpk takes no version below 0.0.1: not the dev default)
#   --release  a release tag's build: the pack's release files go into <dir> (new or empty), and NuGet restores into
#              folders of its own, as build.yml's Windows steps do for a tag
# CI_LINUX_LOCK, CI_LINUX_LOCK_WAIT (seconds) and CI_LINUX_DOTNET replace the lock file, its wait and the SDK folder.
set -euo pipefail
die() { echo "ci-linux-package: $*" >&2; exit 1; }

# the SDK that builds the package, and the .NET runtime the package must carry
dotnet_dir=${CI_LINUX_DOTNET:-/root/.dotnet} sdk_version=10.0.402 runtime_version=10.0.13
lock=${CI_LINUX_LOCK:-/var/lock/scskiller-linux-build} lock_wait=${CI_LINUX_LOCK_WAIT:-3600}
export SCSK_MAKE_JOBS=16

src=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
args=("$@") version=() layer_only= pack= release=
while (($#)); do
    case $1 in
        --layer) layer_only=1; shift ;;
        --version) version=(--version "${2?}"); shift 2 ;;
        --pack) pack=1; shift ;;
        --release) pack=1 release=$(realpath -m "${2?}"); shift 2 ;;
        *) die "unknown argument: $1" ;;
    esac
done

if [[ -z $layer_only ]]; then
    [[ -d $src/dist/SCSKiller/native && -f $src/dist/SHA256SUMS-unsigned.txt ]] || die "no Windows publish in $src/dist"
    [[ -d $src/proxy/build-linux ]] || die "no Linux layer build in $src/proxy/build-linux"
    [[ -x $dotnet_dir/dotnet ]] || die "no $dotnet_dir/dotnet: the package is built with the .NET SDK $sdk_version there"
    [[ -f $dotnet_dir/shared/Microsoft.NETCore.App/$runtime_version/libcoreclr.so ]] || die "$dotnet_dir has no .NET runtime $runtime_version"
    export PATH=$dotnet_dir:$PATH DOTNET_ROOT=$dotnet_dir DOTNET=$dotnet_dir/dotnet
    got=$(cd "$src" && dotnet --version 2>&1) || true
    [[ $got == "$sdk_version" ]] || die "dotnet --version prints '$got', not $sdk_version ($dotnet_dir)"
fi

if [[ -z ${CI_LINUX_LOCKED-} ]]; then
    exec {fd}>>"$lock" || die "can't open the lock file $lock"
    if ! flock -n "$fd"; then
        echo "ci-linux-package: waiting for $lock: another Linux build runs in this WSL (at most ${lock_wait}s)"
        flock -w "$lock_wait" "$fd" || die "no lock on $lock after ${lock_wait}s: the build holding it may hang (fuser -v $lock)"
    fi
    echo "ci-linux-package: got $lock"
    # without the lock's descriptor: a process the build leaves running can't keep the lock
    CI_LINUX_LOCKED=1 bash "${BASH_SOURCE[0]}" "${args[@]}" {fd}>&-
    exit
fi

if [[ -n $layer_only ]]; then
    sh "$src/build/linux-layer.sh" "$src/proxy/build-linux"
    "$src/proxy/build-linux/selftest_linux" vkrec
    exit
fi

# wsl.exe returns only once every process holding its output has exited: no build servers outliving the publish
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
if [[ -n $release ]]; then export NUGET_PACKAGES=$work/nuget/packages NUGET_HTTP_CACHE_PATH=$work/nuget/http; fi
# the Windows checkout's owner isn't always WSL's user
git -c safe.directory="$src" clone -q --no-checkout "$src" "$work/repo"
git -C "$work/repo" checkout -q --detach "$(git -c safe.directory="$src" -C "$src" rev-parse HEAD)"

layer=$src/proxy/build-linux
if ((${#version[@]})); then
    layer=$work/layer
    # the tests' build fetched Fossilize's sources already: the configure checks their SHA-256 again
    if [[ -d $src/proxy/build-linux/fossilize-archives ]]; then mkdir -p "$layer" && cp -r "$src/proxy/build-linux/fossilize-archives" "$layer/"; fi
    SCSK_VERSION=${version[1]#v} sh "$work/repo/build/linux-layer.sh" "$layer"
fi
bash "$work/repo/build/publish-linux.sh" --windows "$src/dist/SCSKiller/native" --sums "$src/dist/SHA256SUMS-unsigned.txt" \
    --linux-native "$layer" "${version[@]}"
bash "$work/repo/build/check-linux-package.sh" "$work/repo/dist/SCSKiller-linux"

# both runtimeconfigs, both deps.json runtime packs and libcoreclr.so's build are the SDK's runtime_version
python3 -I - "$work/repo/dist/SCSKiller-linux" "$runtime_version" "$dotnet_dir/shared/Microsoft.NETCore.App/$runtime_version/libcoreclr.so" <<'PY'
import json, os, re, sys
d, want, ref = sys.argv[1:]
bad = []
for app in ('SCSKiller.Ui', 'scskiller'):
    fws = json.load(open(os.path.join(d, app + '.runtimeconfig.json')))['runtimeOptions'].get('includedFrameworks') or []
    v = [f['version'] for f in fws if f.get('name') == 'Microsoft.NETCore.App']
    if v != [want]: bad.append(f'{app}.runtimeconfig.json carries {v}')
    libs = json.load(open(os.path.join(d, app + '.deps.json')))['libraries']
    v = [k.split('/', 1)[1] for k in libs if k.startswith('runtimepack.Microsoft.NETCore.App.Runtime.')]
    if v != [want]: bad.append(f'{app}.deps.json has the runtime pack {v}')
def build(p):
    m = re.search(rb'@\(#\)Version ([0-9.]+) @Commit: ([0-9a-f]+)', open(p, 'rb').read())
    return m and m.group(1).decode() + ' @' + m.group(2).decode()
mine, sdk = build(os.path.join(d, 'libcoreclr.so')), build(ref)
if not mine or mine != sdk: bad.append(f'libcoreclr.so is {mine}, the SDK\'s {want} is {sdk}')
if bad: sys.exit(f'ci-linux-package: the package doesn\'t carry .NET {want}: ' + ', '.join(bad))
print(f'ci-linux-package: the package carries .NET {want} (libcoreclr.so {mine})')
PY
[[ -n $pack ]] || exit 0

# vpk runs on the pinned SDK's runtime (DOTNET_ROOT)
"$DOTNET" tool install vpk --version 1.2.158 --tool-path "$work/vpk" >/dev/null
bash "$work/repo/build/pack-linux.sh" --dir "$work/repo/dist/SCSKiller-linux" --vpk "$work/vpk/vpk" "${version[@]}" \
    --out "${release:-$work/release}"
