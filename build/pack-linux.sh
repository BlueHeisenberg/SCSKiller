#!/usr/bin/env bash
# Packs a finished dist/SCSKiller-linux (build/publish-linux.sh) for release with vpk, as CI does on a release tag
# (build/ci-linux-package.sh). Nothing here signs: the feed is signed offline with tools/release-sign, after these files
# are checked against SHA256SUMS-linux.txt. Writes into --out, which must be new or empty:
#   SCSKiller-x86_64.AppImage                            vpk's AppImage, under one name for every version (stable's
#                                                        releases/latest/download link), its .desktop fixed (step 4)
#   SCSKiller.App-<version>-<channel>-linux-full.nupkg   the update package
#   releases.<channel>-linux.json                        its feed: vpk channel <channel>-linux, the Linux client's
#   SHA256SUMS-linux.txt                                 the three files' SHA-256, sha256sum format
# No delta package: the app installs full packages only.
# The version is the package's own (scskiller --version); --version (a leading v is dropped) must be the same. Its
# pre-release label is the channel: none = stable, beta, alpha, internal.
set -euo pipefail

usage() {
    cat <<'EOF'
usage: build/pack-linux.sh --out <dir> [--dir <package folder>] [--version X.Y.Z[-pre]] [--vpk <vpk>]
  --out      where the release files go: a new or empty folder
  --dir      the package folder (default dist/SCSKiller-linux)
  --version  the version it must be (a tag: v1.4.0-alpha.1)
  --vpk      vpk 1.2.158 (default vpk on PATH: dotnet tool install -g vpk --version 1.2.158)
needs mksquashfs and unsquashfs (squashfs-tools) on PATH: vpk runs the first, the .desktop fix both
EOF
}
die() { echo "pack-linux: $*" >&2; exit 1; }

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
dir=$repo/dist/SCSKiller-linux out= version= vpk=vpk
while (($#)); do
    case $1 in
        --out) out=${2?}; shift 2 ;;
        --dir) dir=${2?}; shift 2 ;;
        --version) version=${2?}; shift 2 ;;
        --vpk) vpk=${2?}; shift 2 ;;
        -h | --help) usage; exit 0 ;;
        *) usage >&2; die "unknown argument: $1" ;;
    esac
done
[[ $(uname -s) == Linux ]] || die "runs on Linux: the AppImage takes the package's file modes, which only a Linux folder keeps"
[[ -n $out ]] || { usage >&2; die "--out is required"; }
[[ ! -e $out || -d $out && -z $(ls -A "$out") ]] || die "$out isn't a new or empty folder"
[[ -d $dir ]] || die "no package folder $dir"
command -v python3 >/dev/null || die "python3 is needed"
command -v "$vpk" >/dev/null || die "no vpk ($vpk): dotnet tool install -g vpk --version 1.2.158, or --vpk <path>"
for t in mksquashfs unsquashfs; do command -v $t >/dev/null || die "no $t (squashfs-tools): vpk and the .desktop fix need it"; done
icon=$dir/share/icons/hicolor/256x256/apps/com.BlueHeisenberg.SCSKiller.png
desktop=$dir/share/applications/com.BlueHeisenberg.SCSKiller.desktop
[[ -x $dir/SCSKiller.Ui && -x $dir/scskiller && -f $icon && -f $desktop ]] ||
    die "$dir isn't a package folder (SCSKiller.Ui, scskiller, the icon, the .desktop file)"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# 1. version and channel
own=$(SCSKILLER_DATA_DIR=$tmp/data "$dir/scskiller" --version)
[[ $own =~ ^[0-9]+\.[0-9]+\.[0-9]+(-([0-9A-Za-z-]+)(\.[0-9A-Za-z-]+)*)?$ ]] || die "the package's version '$own' isn't X.Y.Z[-pre]"
label=${BASH_REMATCH[2]}
[[ -z $version || ${version#v} == "$own" ]] || die "the package is version $own, not ${version#v}"
channel=${label:-stable}
[[ $channel =~ ^(stable|beta|alpha|internal)$ ]] || die "pre-release label '$label' isn't a channel (beta, alpha, internal)"
pkg=SCSKiller.App-$own-$channel-linux-full.nupkg
feed=releases.$channel-linux.json
echo "version $own, channel $channel-linux"

# 2. vpk, into a folder of its own: it lists every package it finds in its output folder in the feed
"$vpk" --skip-updates pack --packId SCSKiller.App --packVersion "$own" --packDir "$dir" --mainExe SCSKiller.Ui \
    --packTitle SCSKiller --packAuthors "The SCSKiller authors" --channel "$channel-linux" --runtime linux-x64 \
    --outputDir "$tmp/vpk" --icon "$icon"

# 3. what vpk wrote: one AppImage, the package, and a feed that lists exactly that package as built
shopt -s nullglob
images=("$tmp"/vpk/*.AppImage)
((${#images[@]} == 1)) || die "vpk wrote ${#images[@]} AppImages, not one (did its naming change?)"
[[ -f $tmp/vpk/$pkg ]] || die "vpk wrote no $pkg (did its naming change?)"
[[ -f $tmp/vpk/$feed ]] || die "vpk wrote no $feed"
check() {
python3 -I - "$tmp/vpk/$feed" "$tmp/vpk/$pkg" "$own" "${images[0]}" <<'PY'
import hashlib, json, os, struct, sys
feed, pkg, version, image = sys.argv[1:]
name, data = os.path.basename(pkg), open(pkg, 'rb').read()
try: assets = json.load(open(feed, encoding='utf-8-sig'))['Assets']
except (ValueError, KeyError, TypeError): sys.exit(f'pack-linux: {os.path.basename(feed)} is no feed')
if not isinstance(assets, list) or len(assets) != 1 or not isinstance(assets[0], dict):
    sys.exit(f'pack-linux: {os.path.basename(feed)} must list {name} alone')
a = assets[0]
want = {'PackageId': 'SCSKiller.App', 'Version': version, 'FileName': name, 'SHA256': hashlib.sha256(data).hexdigest(), 'Size': len(data)}
got = {k: str(a.get(k)).lower() if k == 'SHA256' else a.get(k) for k in want}
if got != want or a.get('Type') not in ('Full', 1):
    sys.exit(f'pack-linux: {os.path.basename(feed)} lists {name} otherwise than vpk built it')
h = open(image, 'rb').read(20)
# a type 2 AppImage is an x86-64 ELF executable with 'AI', 2 in e_ident's padding (offset 8)
if len(h) < 20 or h[:4] != b'\x7fELF' or h[4] != 2 or h[5] != 1 or h[8:11] != b'AI\x02' or struct.unpack_from('<H', h, 18)[0] != 62:
    sys.exit(f'pack-linux: {os.path.basename(image)} is no x86-64 AppImage')
PY
}
check

# 4. vpk's own .desktop at the AppImage's root, the one AppImage menu tools install, says Categories=Utility; and
#    StartupWMClass=<packId>, which isn't Avalonia's window class: it takes the package .desktop's two values. The
#    squashfs is repacked as vpk made it, the package and its feed entry follow, then step 3 checks again.
python3 -I - "${images[0]}" "$tmp/vpk/$pkg" "$tmp/vpk/$feed" "$desktop" "$tmp/fix" <<'PY'
import hashlib, json, os, struct, subprocess, sys, zipfile
image, pkg, feed, own_desktop, work = sys.argv[1:]
def die(m): sys.exit('pack-linux: ' + m)
name = os.path.basename(image)
data = open(image, 'rb').read()
# the squashfs starts where the runtime's ELF section headers end
shoff, = struct.unpack_from('<Q', data, 0x28)
shentsize, shnum = struct.unpack_from('<HH', data, 0x3A)
at = shoff + shentsize * shnum
if data[at:at + 4] != b'hsqs': die(f'{name}: no squashfs after its runtime')
_, mkfs_time, block_size, _, comp, _, flags = struct.unpack_from('<IIIIHHH', data, at + 4)
comps = {1: 'gzip', 2: 'lzma', 3: 'lzo', 4: 'xz', 5: 'lz4', 6: 'zstd'}
# 0xc0 (duplicates, exportable) is what mksquashfs makes without options beyond these; compressor options would be 0x400
if comp not in comps or flags != 0xc0: die(f'{name}: a squashfs (compression {comp}, flags {flags:#x}) this script doesn\'t repack')
opts = ['-comp', comps[comp], '-b', str(block_size), '-mkfs-time', str(mkfs_time), '-root-owned', '-noappend', '-quiet', '-no-progress']
def unsquash(path, dest):
    subprocess.run(['unsquashfs', '-quiet', '-no-progress', '-offset', str(at), '-dest', dest, path], check=True, stdout=subprocess.DEVNULL)
def squash(out):
    subprocess.run(['mksquashfs', root, out] + opts, check=True, stdout=subprocess.DEVNULL)
    return open(out, 'rb').read()
# the package carries the AppImage itself (lib/app/), so it changes with it
with zipfile.ZipFile(pkg) as z:
    infos = z.infolist()
    bodies = [z.read(i) for i in infos]
apps = [i for i in infos if i.filename.startswith('lib/app/') and i.filename.endswith('.AppImage')]
if len(apps) != 1 or bodies[infos.index(apps[0])] != data: die(f'{os.path.basename(pkg)} doesn\'t carry the AppImage vpk wrote')
inner = apps[0]
os.makedirs(work)
root = os.path.join(work, 'root')
unsquash(image, root)
# vpk's options: they give its squashfs back byte for byte, else the repack would be another build
if squash(os.path.join(work, 'same.sqfs')) != data[at:]: die(f'{name}: its squashfs isn\'t what mksquashfs {" ".join(opts)} makes of it')

want = {}
for line in open(own_desktop, encoding='utf-8').read().splitlines():
    k, _, v = line.partition('=')
    if k in ('Categories', 'StartupWMClass'):
        if k in want: die(f'{os.path.basename(own_desktop)} has {k} twice')
        want[k] = v
if len(want) != 2: die(f'{os.path.basename(own_desktop)} lacks Categories or StartupWMClass')
entries = [f for f in os.listdir(root) if f.endswith('.desktop')]
if len(entries) != 1: die(f'{name} has {len(entries)} .desktop files at its root, not one')
path = os.path.join(root, entries[0])
st, text = os.stat(path), open(path, encoding='utf-8').read()
lines = text.split('\n')
for k, v in want.items():
    i = [n for n, l in enumerate(lines) if l.startswith(k + '=')]
    if len(i) != 1: die(f'{name}: {entries[0]} has {len(i)} {k} lines, not one')
    lines[i[0]] = f'{k}={v}'
if '\n'.join(lines) == text:
    print(f'{entries[0]}: already Categories={want["Categories"]}, StartupWMClass={want["StartupWMClass"]}')
    sys.exit()
with open(path, 'w', encoding='utf-8', newline='') as f: f.write('\n'.join(lines))
os.utime(path, ns=(st.st_atime_ns, st.st_mtime_ns))
fixed = data[:at] + squash(os.path.join(work, 'fixed.sqfs'))
check = os.path.join(work, 'check')
open(image + '.fixed', 'wb').write(fixed)
unsquash(image + '.fixed', check)
def tree(top):
    t = {}
    for d, dirs, files in os.walk(top):
        for f in dirs + files:
            p = os.path.join(d, f)
            st = os.lstat(p)
            t[os.path.relpath(p, top)] = (st.st_mode, st.st_mtime_ns, os.readlink(p) if os.path.islink(p)
                else hashlib.sha256(open(p, 'rb').read()).digest() if os.path.isfile(p) else None)
    return t
if tree(root) != tree(check): die(f'{name}: the repacked squashfs doesn\'t give back the fixed tree')
os.chmod(image + '.fixed', os.stat(image).st_mode & 0o7777)
os.replace(image + '.fixed', image)

with zipfile.ZipFile(pkg + '.fixed', 'w') as z:
    for i, body in zip(infos, bodies): z.writestr(i, fixed if i is inner else body, compress_type=i.compress_type)
os.replace(pkg + '.fixed', pkg)

raw = open(feed, 'rb').read()
j = json.loads(raw.decode('utf-8-sig'))
asset, body = j['Assets'][0], open(pkg, 'rb').read()
for k, h in (('SHA256', hashlib.sha256), ('SHA1', hashlib.sha1)):
    if k in asset: asset[k] = h(body).hexdigest().upper() if asset[k] == asset[k].upper() else h(body).hexdigest()
asset['Size'] = len(body)
open(feed, 'wb').write(raw[:3] * raw.startswith(b'\xef\xbb\xbf') + json.dumps(j, separators=(',', ':'), ensure_ascii=False).encode())
print(f'{entries[0]}: Categories={want["Categories"]}, StartupWMClass={want["StartupWMClass"]} (squashfs repacked: {" ".join(opts)})')
PY
check

# 5. the release files and their sums
mkdir -p "$out"
install -m 755 "${images[0]}" "$out/SCSKiller-x86_64.AppImage"
install -m 644 "$tmp/vpk/$pkg" "$tmp/vpk/$feed" "$out/"
(cd "$out" && sha256sum SCSKiller-x86_64.AppImage "$pkg" "$feed" > SHA256SUMS-linux.txt)
echo "$out: SCSKiller-x86_64.AppImage ($(du -m "$out/SCSKiller-x86_64.AppImage" | cut -f1) MB), $pkg, $feed, SHA256SUMS-linux.txt"
