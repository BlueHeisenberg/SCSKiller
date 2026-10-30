#!/usr/bin/env bash
# Checks a finished dist/SCSKiller-linux (build/publish-linux.sh) from the outside, as build/ci-linux-package.sh does:
#   - every file build/publish-linux.sh's header lists is there, codecs included (a release ships libz-ng.so), and
#     SCSKiller.Ui, scskiller, native/scskiller_warm and native/fossilize-replay are executable
#   - every ELF file in it is 64-bit little-endian x86-64, the Linux native files among them
#   - native/libVkLayer_scskiller.so, scskiller_warm and fossilize-replay need glibc 2.31 at most (readelf's version
#     needs): they run inside the Steam Runtime's sniper container
#   - a versioned package's native/fossilize-replay has none of the test hooks (SCSK_TEST_HOOKS, 0.0.0-internal builds)
#   - the .desktop file passes desktop-file-validate, when that is installed
# Usage: build/check-linux-package.sh [folder]   (default dist/SCSKiller-linux)
set -euo pipefail
die() { echo "check-linux-package: $*" >&2; exit 1; }
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
dir=${1:-$repo/dist/SCSKiller-linux}
[[ -d $dir ]] || die "no folder $dir"
command -v python3 >/dev/null || die "python3 is needed"
command -v readelf >/dev/null || die "readelf (binutils) is needed"
id=com.BlueHeisenberg.SCSKiller
# the replayer's fault injection, which only a 0.0.0-internal build compiles in; the release step reads this line too
replay_hooks=(SCSKILLER_REPLAY_CRASH SCSKILLER_REPLAY_HANG SCSKILLER_REPLAY_WAIT)

for f in SCSKiller.Ui scskiller SCSKiller.Ui.deps.json libz-ng.so \
    native/{scskiller_warm.exe,segheap/scskiller_warm.exe,d3d12.dll,scskiller_ags.dll,amd_ags_x64.dll,fossilize-replay.exe,VkLayer_scskiller.dll} \
    native/{libVkLayer_scskiller.so,VkLayer_scskiller.json,scskiller_warm,fossilize-replay} \
    "share/applications/$id.desktop" "share/icons/hicolor/256x256/apps/$id.png" "share/icons/hicolor/512x512/apps/$id.png" \
    LICENSE LICENSE-EXCEPTION.txt THIRD-PARTY-NOTICES.md notices/{dotnet,SkiaSharp,HarfBuzzSharp}-THIRD-PARTY-NOTICES.txt; do
    [[ -f $dir/$f ]] || die "missing $f"
done
for f in SCSKiller.Ui scskiller native/scskiller_warm native/fossilize-replay; do [[ -x $dir/$f ]] || die "$f must be executable"; done
echo "the files publish-linux.sh lists are there"

python3 -I - "$dir" native/{libVkLayer_scskiller.so,scskiller_warm,fossilize-replay} <<'PY'
import os, struct, sys
top, need, elf, bad = sys.argv[1], set(sys.argv[2:]), 0, []
for root, _, files in os.walk(top):
    for name in files:
        path = os.path.join(root, name)
        if os.path.islink(path): continue
        with open(path, 'rb') as f: h = f.read(20)
        if h[:4] != b'\x7fELF': continue
        elf += 1
        need.discard(os.path.relpath(path, top))
        # e_ident[EI_CLASS] 2 = 64-bit, [EI_DATA] 1 = little-endian, e_machine 62 = x86-64
        if len(h) < 20 or h[4] != 2 or h[5] != 1 or struct.unpack_from('<H', h, 18)[0] != 62:
            bad.append(os.path.relpath(path, top))
if need: sys.exit('check-linux-package: not ELF: ' + ', '.join(sorted(need)))
if bad: sys.exit('check-linux-package: not x86-64 ELF: ' + ', '.join(sorted(bad)))
print(f'{elf} ELF files, all x86-64')
PY

for f in native/{libVkLayer_scskiller.so,scskiller_warm,fossilize-replay}; do
    need=$(readelf -V --wide "$dir/$f" | grep -o 'GLIBC_[0-9][0-9.]*' | sort -uV | tail -1 || true)
    [[ -n $need ]] || die "$f: no glibc version could be read"
    [[ $(printf '%s\nGLIBC_2.31\n' "$need" | sort -V | tail -1) == GLIBC_2.31 ]] || die "$f needs glibc ${need#GLIBC_}, newer than 2.31"
    echo "$f needs glibc ${need#GLIBC_}"
done

version=$(python3 -I - "$dir/SCSKiller.Ui.deps.json" <<'PY'
import json, sys
v = [k.split('/', 1)[1] for k, l in json.load(open(sys.argv[1]))['libraries'].items() if k.split('/')[0] == 'SCSKiller.Ui' and l.get('type') == 'project']
if len(v) != 1: sys.exit('check-linux-package: SCSKiller.Ui.deps.json names no single SCSKiller.Ui version')
print(v[0])
PY
)
if [[ $version =~ ^0\.0\.0-internal ]]; then
    echo "version $version: the replayer may keep its test hooks"
else
    for h in "${replay_hooks[@]}"; do
        ! grep -qaF "$h" "$dir/native/fossilize-replay" || die "native/fossilize-replay has the test hook $h: version $version must not ship it"
    done
    echo "version $version: the replayer has no test hooks"
fi

desktop=$dir/share/applications/$id.desktop
if command -v desktop-file-validate >/dev/null; then
    desktop-file-validate "$desktop" || die "$id.desktop fails desktop-file-validate"
    echo "$id.desktop passes desktop-file-validate"
else
    echo "no desktop-file-validate: $id.desktop not validated"
fi
