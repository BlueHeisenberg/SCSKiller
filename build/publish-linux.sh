#!/usr/bin/env bash
# Builds dist/SCSKiller-linux/, the folder the Linux packages (AppImage, Flatpak) are made from. Runs on Linux.
#
#   SCSKiller.Ui              the app (Avalonia), self-contained .NET for linux-x64
#   scskiller                 the command line on the app's runtime: every file they both publish is byte-identical, so it
#                             shares them (a cli/ folder of its own would add a second runtime)
#   libz-ng.so                the pinned zlib-ng, seeding each user's codecs folder; no Oodle (not redistributed, the app
#                             downloads it on first use)
#   native/                   the Windows tools that run under Proton, from the Windows build (build/publish.ps1's
#                             SCSKiller.zip, or its native folder), checked against its SHA-256 sums and its version first
#                             + the Linux builds (--linux-native, the layer build's output folder): the Vulkan recorder
#                             libVkLayer_scskiller.so with its manifest VkLayer_scskiller.json, and the Vulkan warm
#                             scskiller_warm with the fossilize-replay it runs. The app copies the layer and the warm into
#                             its data folder and uses those copies: a game's Steam Runtime container sees neither the
#                             AppImage's mount nor a Flatpak's /app
#   share/applications/com.BlueHeisenberg.SCSKiller.desktop     Exec is the package's to fill in (@EXEC@)
#   share/icons/hicolor/{256x256,512x512}/apps/com.BlueHeisenberg.SCSKiller.png
#   LICENSE, LICENSE-EXCEPTION.txt, THIRD-PARTY-NOTICES.md, notices/{dotnet,SkiaSharp,HarfBuzzSharp}-THIRD-PARTY-NOTICES.txt
#
# --version X.Y.Z[-pre] (a leading v is dropped) stamps both executables, as build/publish.ps1 -Version does, and must be
# the native files' ProductVersion. Without it: the v* tag at HEAD if there is exactly one, else the dev default
# 0.0.0-internal.0.
set -euo pipefail

usage() {
    cat <<'EOF'
usage: build/publish-linux.sh --windows <SCSKiller.zip | native dir> --sums <SHA256SUMS file>
                              (--linux-native <dir> | --no-linux-native)
                              [--version X.Y.Z[-pre]] [--codec-cache <dir> | --no-codecs] [--configuration <c>]
  --windows        the Windows build's SCSKiller.zip, or its extracted SCSKiller/native folder
  --sums           its SHA256SUMS (sha256sum format, paths relative to dist/: SCSKiller.zip, SCSKiller/native/...)
  --linux-native   the Linux build's output folder (proxy/build-linux): libVkLayer_scskiller.so, VkLayer_scskiller.json,
                   scskiller_warm and fossilize-replay, x86-64 ELF needing glibc 2.31 at most
  --no-linux-native  without any of them (no Vulkan recording or warm on Linux then)
  --codec-cache    where the pinned codecs are kept between builds (default ~/.local/share/SCSKiller-build/codecs)
  --no-codecs      ship no codec: the app downloads zlib-ng on first use
EOF
}
die() { echo "publish-linux: $*" >&2; exit 1; }

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
version= windows= sums= linux_native= no_linux_native= no_codecs= configuration=Release
codec_cache=${XDG_DATA_HOME:-$HOME/.local/share}/SCSKiller-build/codecs
while (($#)); do
    case $1 in
        --version) version=${2?}; shift 2 ;;
        --windows) windows=${2?}; shift 2 ;;
        --sums) sums=${2?}; shift 2 ;;
        --linux-native) linux_native=${2?}; shift 2 ;;
        --no-linux-native) no_linux_native=1; shift ;;
        --codec-cache) codec_cache=${2?}; shift 2 ;;
        --no-codecs) no_codecs=1; shift ;;
        --configuration) configuration=${2?}; shift 2 ;;
        -h | --help) usage; exit 0 ;;
        *) usage >&2; die "unknown argument: $1" ;;
    esac
done
[[ $(uname -s) == Linux ]] || die "runs on Linux (build/publish.ps1 builds the Windows package)"
[[ -n $windows && -n $sums ]] || { usage >&2; die "--windows and --sums are required"; }
[[ -f $sums ]] || die "no sums file $sums"
if [[ -n $linux_native && -n $no_linux_native ]] || [[ -z $linux_native && -z $no_linux_native ]]; then
    die "pass either --linux-native <dir> or --no-linux-native"
fi
command -v python3 >/dev/null || die "python3 is needed"

# 0. version: --version, else the one v* tag at HEAD
if [[ -z $version ]]; then
    mapfile -t tags < <(git -C "$repo" tag --points-at HEAD -l 'v*')
    ((${#tags[@]} <= 1)) || die "HEAD has several tags (${tags[*]}): pass --version"
    version=${tags[0]:-}
fi
version=${version#v}
version_args=()
if [[ -n $version ]]; then
    [[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] || die "not a SemVer X.Y.Z[-pre]: $version"
    # a versioned build verifies the server's signed lists: the rules key must be pinned
    grep -Eq '\["rules-a"\][[:space:]]*=[[:space:]]*"[A-Za-z0-9+/]{43}="' "$repo/src/SCSKiller.Core/App/ContentTrust.cs" ||
        die "ContentTrust.RulesKeys has no rules-a key: pin it before a release"
    version_args=("-p:Version=$version" "-p:IncludeSourceRevisionInInformationalVersion=false")
fi
stamp=${version:-0.0.0-internal.0}
echo "version: ${version:-0.0.0-internal.0 (dev)}"

dotnet=${DOTNET:-dotnet}
if ! command -v "$dotnet" >/dev/null && [[ -x $HOME/.dotnet/dotnet ]]; then dotnet=$HOME/.dotnet/dotnet; fi   # a per-user SDK
command -v "$dotnet" >/dev/null || die "no dotnet (set DOTNET)"

dist=$repo/dist
out=$dist/SCSKiller-linux
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# Helpers that read the Windows build (untrusted until its sums match) run isolated from the working directory.
py() {
    python3 -I - "$@" <<'PY'
import json, os, re, shutil, struct, sys, zlib, zipfile

def native_from_zip(zip_path, out, *names):
    with zipfile.ZipFile(zip_path) as z:
        entries = {}
        for i in z.infolist():
            n = i.filename.replace('\\', '/')   # Windows PowerShell's ZipFile may write backslashes
            if n in entries: sys.exit(f'publish-linux: {zip_path}: {n} is in it twice')
            entries[n] = i
        for name in names:
            i = entries.get('SCSKiller/native/' + name)
            if i is None or i.is_dir(): sys.exit(f'publish-linux: {zip_path} has no SCSKiller/native/{name}')
            dest = os.path.join(out, name)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with z.open(i) as src, open(dest, 'xb') as dst: shutil.copyfileobj(src, dst)

def product_version(path):
    # VS_VERSIONINFO String: UTF-16 key, padding to 32 bits, UTF-16 value
    d = open(path, 'rb').read()
    key = 'ProductVersion\0'.encode('utf-16-le')
    i = d.find(key)
    if i < 0 or d.find(key, i + 1) >= 0: return ''
    i += len(key)
    while d[i:i + 2] == b'\0\0': i += 2
    j = i
    while j < len(d) and d[j:j + 2] != b'\0\0': j += 2
    print(d[i:j].decode('utf-16-le', 'replace'))

def linux_binary(path):
    # the layer loads into games inside the Steam Runtime's container (sniper: glibc 2.31)
    d = open(path, 'rb').read()
    if len(d) < 20 or d[:4] != b'\x7fELF' or d[4] != 2 or d[5] != 1 or struct.unpack_from('<H', d, 18)[0] != 0x3E:
        sys.exit(f'publish-linux: {os.path.basename(path)} is not an x86-64 ELF file')
    need = max(((int(a), int(b)) for a, b in re.findall(rb'GLIBC_(\d+)\.(\d+)', d)), default=(0, 0))
    if need > (2, 31): sys.exit(f'publish-linux: {os.path.basename(path)} needs glibc {need[0]}.{need[1]}, newer than 2.31')

def layer_manifest(path):
    layer = json.load(open(path))['layer']
    if layer.get('library_path') != './libVkLayer_scskiller.so':
        sys.exit(f'publish-linux: {os.path.basename(path)}: library_path is not ./libVkLayer_scskiller.so')

def ico_png(path, size):
    d = open(path, 'rb').read()
    for k in range(struct.unpack_from('<H', d, 4)[0]):
        w, h, _, _, _, _, length, off = struct.unpack_from('<BBBBHHII', d, 6 + 16 * k)
        if (w or 256, h or 256) == (size, size) and d[off:off + 8] == b'\x89PNG\r\n\x1a\n': return d[off:off + length]
    sys.exit(f'publish-linux: {path} has no {size}x{size} PNG image')

def png_rows(png):
    pos, idat, ihdr = 8, b'', None
    while pos < len(png):
        length, kind = struct.unpack_from('>I4s', png, pos)
        body = png[pos + 8:pos + 8 + length]
        pos += 12 + length
        if kind == b'IHDR': ihdr = struct.unpack('>IIBBBBB', body)
        elif kind == b'IDAT': idat += body
    w, h, depth, color, _, _, interlace = ihdr
    if (depth, color, interlace) != (8, 6, 0): sys.exit('publish-linux: the icon is not 8-bit RGBA without interlacing')
    raw, stride, rows, prev = zlib.decompress(idat), w * 4, [], bytearray(w * 4)
    for y in range(h):
        f, line = raw[y * (stride + 1)], bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for x in range(stride):
            a, b, c = line[x - 4] if x >= 4 else 0, prev[x], prev[x - 4] if x >= 4 else 0
            if f == 1: line[x] = (line[x] + a) & 255
            elif f == 2: line[x] = (line[x] + b) & 255
            elif f == 3: line[x] = (line[x] + (a + b) // 2) & 255
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line)
        prev = line
    return w, h, rows

def double(s):
    # 2x with a triangle filter (sample weights 3/4 and 1/4), times 4
    n = len(s)
    return [v for i in range(n) for v in (3 * s[i] + s[max(i - 1, 0)], 3 * s[i] + s[min(i + 1, n - 1)])]

def png_2x(w, h, rows):
    # on premultiplied alpha, else the transparent corners darken the edge
    planes = []
    for ch in range(4):
        plane = [[r[4 * x + ch] * (r[4 * x + 3] if ch < 3 else 1) for x in range(w)] for r in rows]
        wide = [double(r) for r in plane]
        planes.append([double([wide[y][x] for y in range(h)]) for x in range(2 * w)])
    out = []
    for y in range(2 * h):
        line = bytearray()
        for x in range(2 * w):
            a16 = planes[3][x][y]
            line += bytes(min(255, (planes[c][x][y] + a16 // 2) // a16) if a16 else 0 for c in range(3))
            line.append((a16 + 8) // 16)
        out.append(line)
    chunk = lambda kind, body: struct.pack('>I', len(body)) + kind + body + struct.pack('>I', zlib.crc32(kind + body))
    raw = b''.join(b'\0' + bytes(r) for r in out)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 2 * w, 2 * h, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))

def icons(ico, out256, out512):
    png = ico_png(ico, 256)
    open(out256, 'wb').write(png)
    open(out512, 'wb').write(png_2x(*png_rows(png)))

def framework_version(runtimeconfig):
    fws = json.load(open(runtimeconfig))['runtimeOptions'].get('includedFrameworks')
    if not fws: sys.exit(f'publish-linux: {runtimeconfig} is framework-dependent')
    print(next(f['version'] for f in fws if f['name'] == 'Microsoft.NETCore.App'))

def package_path(deps, package):
    # its folder in the NuGet cache, for the version the publish took
    paths = [v['path'] for k, v in json.load(open(deps))['libraries'].items() if v.get('type') == 'package' and k.split('/')[0] == package]
    if len(paths) != 1: sys.exit(f'publish-linux: {os.path.basename(deps)} lists {package} {len(paths)} times')
    print(paths[0])

cmd, *args = sys.argv[1:]
{'native-from-zip': native_from_zip, 'product-version': product_version, 'linux-binary': linux_binary,
 'layer-manifest': layer_manifest, 'icons': icons, 'framework-version': framework_version, 'package-path': package_path}[cmd](*args)
PY
}

# 1. native files: staged, then checked against the sums (and the sums against this list, so a file the Windows
#    package gains isn't left out silently), then their version
natives=(scskiller_warm.exe segheap/scskiller_warm.exe d3d12.dll scskiller_ags.dll amd_ags_x64.dll fossilize-replay.exe VkLayer_scskiller.dll d3d9.dll x86/d3d9.dll
         preload/winmm.dll preload/version.dll preload/dinput8.dll)   # the recorder's preloader, which Proton's Wine loads as Windows does
own=(scskiller_warm.exe segheap/scskiller_warm.exe d3d12.dll scskiller_ags.dll fossilize-replay.exe VkLayer_scskiller.dll d3d9.dll x86/d3d9.dll preload/winmm.dll preload/version.dll preload/dinput8.dll)   # with proxy/version.rc's resource
stage=$tmp/native
sum_of() {   # the sums file's hash for a dist/-relative path: exactly one line, else empty
    awk -v p="$1" '{ sub(/\r$/, "") } substr($0, 65, 1) == " " && substr($0, 67) == p && substr($0, 66, 1) ~ /[ *]/ && tolower(substr($0, 1, 64)) ~ /^[0-9a-f]+$/ { n++; h = tolower(substr($0, 1, 64)) }
        END { if (n == 1 && length(h) == 64) print h }' "$sums"
}
hash_of() { sha256sum "$1" | cut -d' ' -f1; }
listed=$(awk '{ sub(/\r$/, "") } substr($0, 67) ~ /^SCSKiller\/native\// { print substr($0, 84) }' "$sums" | sort -u)
expected=$(printf '%s\n' "${natives[@]}" | sort -u)
[[ $listed == "$expected" ]] || die "the sums' SCSKiller/native files differ from what this script packages:
$(diff <(echo "$expected") <(echo "$listed") | grep '^[<>]' | sed 's/^</  missing from the sums:/; s/^>/  not packaged here:/')"
mkdir -p "$stage"
if [[ -f $windows ]]; then
    want=$(sum_of SCSKiller.zip)
    [[ -n $want ]] || die "$sums has no single line for SCSKiller.zip"
    [[ $(hash_of "$windows") == "$want" ]] || die "$windows doesn't match its sum"
    py native-from-zip "$windows" "$stage" "${natives[@]}"
elif [[ -d $windows ]]; then
    for f in "${natives[@]}"; do
        [[ -f $windows/$f ]] || die "$windows has no $f"
        mkdir -p "$stage/$(dirname "$f")"
        cp "$windows/$f" "$stage/$f"
    done
else
    die "no file or folder $windows"
fi
for f in "${natives[@]}"; do
    want=$(sum_of "SCSKiller/native/$f")
    [[ -n $want ]] || die "$sums has no single line for SCSKiller/native/$f"
    [[ $(hash_of "$stage/$f") == "$want" ]] || die "native/$f doesn't match its sum"
done
# an internal build's native files keep their test hooks: a versioned package never takes them, nor another version's
for f in "${own[@]}"; do
    v=$(py product-version "$stage/$f")
    [[ $v == "$stamp" ]] || die "native/$f is version '${v:-?}', this package $stamp"
done
linux_natives=()
if [[ -n $linux_native ]]; then
    [[ -d $linux_native ]] || die "no folder $linux_native"
    linux_natives=(libVkLayer_scskiller.so VkLayer_scskiller.json scskiller_warm fossilize-replay)
    mkdir -p "$tmp/linux"
    for f in "${linux_natives[@]}"; do
        [[ -f $linux_native/$f ]] || die "$linux_native has no $f"
        cp "$linux_native/$f" "$tmp/linux/$f"
    done
    for f in libVkLayer_scskiller.so scskiller_warm fossilize-replay; do py linux-binary "$tmp/linux/$f"; done
    py layer-manifest "$tmp/linux/VkLayer_scskiller.json"
    chmod 755 "$tmp/linux/scskiller_warm" "$tmp/linux/fossilize-replay"
fi
echo "native: ${#natives[@]} Windows files match $sums, ${#linux_natives[@]} Linux files"

# 2. app + CLI (no debug symbols: they embed the checkout path); the CLI joins the app's folder only where every file
#    the two share is the same
rm -rf "$out"
common=(-c "$configuration" -f net10.0 -r linux-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false "${version_args[@]}")
"$dotnet" publish "$repo/src/SCSKiller.Ui/SCSKiller.Ui.csproj" -o "$out" "${common[@]}"
"$dotnet" publish "$repo/src/SCSKiller.Cli/SCSKiller.Cli.csproj" -o "$tmp/cli" "${common[@]}"
(
    cd "$tmp/cli"
    find . -type f -print0 | while IFS= read -r -d '' f; do
        if [[ -e $out/$f ]]; then cmp -s "$f" "$out/$f" || die "the app and the CLI publish different ${f#./}: they can't share a folder"
        else mkdir -p "$out/$(dirname "$f")" && cp -p "$f" "$out/$f"; fi
    done
)

# 3. native files
mkdir -p "$out/native"
cp -r "$stage/." "$out/native/"
if ((${#linux_natives[@]})); then cp -r "$tmp/linux/." "$out/native/"; fi

# 4. zlib-ng next to the executables, from a build-owned cache, never the app's data folder: fetch-codecs keeps the
#    pinned copies there, downloads one that is missing or doesn't match, and fails when none matches
if [[ -z $no_codecs ]]; then
    SCSKILLER_DATA_DIR=$tmp/data "$out/scskiller" fetch-codecs "$codec_cache" || die "fetch-codecs failed"
    install -m 644 "$codec_cache/libz-ng.so" "$out/"
fi

# 5. licences, and the notices of the .NET runtime and of the native Skia and HarfBuzz builds as shipped
install -m 644 "$repo/LICENSE" "$repo/LICENSE-EXCEPTION.txt" "$out/"
nuget=${NUGET_PACKAGES:-$HOME/.nuget/packages}
runtime=$(py framework-version "$out/SCSKiller.Ui.runtimeconfig.json")
row="| .NET runtime (self-contained) |"
sed "s/^$row [^|]* |/$row $runtime |/" "$repo/THIRD-PARTY-NOTICES.md" >"$out/THIRD-PARTY-NOTICES.md"
grep -qF "$row $runtime |" "$out/THIRD-PARTY-NOTICES.md" || die "THIRD-PARTY-NOTICES.md has no '$row' row to put .NET $runtime in"
chmod 644 "$out/THIRD-PARTY-NOTICES.md"
install -D -m 644 "$nuget/microsoft.netcore.app.runtime.linux-x64/$runtime/THIRD-PARTY-NOTICES.TXT" "$out/notices/dotnet-THIRD-PARTY-NOTICES.txt"
for p in SkiaSharp HarfBuzzSharp; do
    dir=$(py package-path "$out/SCSKiller.Ui.deps.json" "$p.NativeAssets.Linux")
    install -m 644 "$nuget/$dir/THIRD-PARTY-NOTICES.txt" "$out/notices/$p-THIRD-PARTY-NOTICES.txt"
done

# 6. menu entry and icons (the 512 is the 256 scaled up: the icon has no larger source)
id=com.BlueHeisenberg.SCSKiller
mkdir -p "$out/share/applications" "$out/share/icons/hicolor/256x256/apps" "$out/share/icons/hicolor/512x512/apps"
# StartupWMClass: Avalonia's X11 WM_CLASS is the entry assembly's name
cat >"$out/share/applications/$id.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=SCSKiller
GenericName=Shader cache compiler
Comment=Compiles your games' shaders into your GPU driver's cache before you play
# the package fills in Exec: the AppImage's path, or the Flatpak's command
Exec=@EXEC@
Icon=$id
Terminal=false
Categories=Game;
StartupWMClass=SCSKiller.Ui
EOF
py icons "$repo/src/SCSKiller.App/Assets/app.ico" "$out/share/icons/hicolor/256x256/apps/$id.png" "$out/share/icons/hicolor/512x512/apps/$id.png"
if command -v desktop-file-validate >/dev/null; then desktop-file-validate "$out/share/applications/$id.desktop"; fi

# 7. layout check, including self-contained .NET (no "install .NET" prompt) and the version the CLI reports
expected_files=(SCSKiller.Ui scskiller libcoreclr.so libhostfxr.so libSkiaSharp.so libHarfBuzzSharp.so "${natives[@]/#/native/}"
    LICENSE LICENSE-EXCEPTION.txt THIRD-PARTY-NOTICES.md notices/dotnet-THIRD-PARTY-NOTICES.txt
    notices/SkiaSharp-THIRD-PARTY-NOTICES.txt notices/HarfBuzzSharp-THIRD-PARTY-NOTICES.txt
    "share/applications/$id.desktop" "share/icons/hicolor/256x256/apps/$id.png" "share/icons/hicolor/512x512/apps/$id.png")
[[ -n $no_codecs ]] || expected_files+=(libz-ng.so)
((${#linux_natives[@]} == 0)) || expected_files+=("${linux_natives[@]/#/native/}")
for f in "${expected_files[@]}"; do [[ -f $out/$f ]] || die "dist/SCSKiller-linux is missing $f"; done
[[ -x $out/SCSKiller.Ui && -x $out/scskiller ]] || die "SCSKiller.Ui and scskiller must be executable"
((${#linux_natives[@]} == 0)) || [[ -x $out/native/scskiller_warm && -x $out/native/fossilize-replay ]] ||
    die "native/scskiller_warm and native/fossilize-replay must be executable"
[[ -z $(find "$out" \( -name '*.pdb' -o -name 'liboodle-data-shared.so' \) -print -quit) ]] || die "debug symbols and Oodle must not be published"
for rc in SCSKiller.Ui scskiller; do py framework-version "$out/$rc.runtimeconfig.json" >/dev/null; done
reported=$(SCSKILLER_DATA_DIR=$tmp/data "$out/scskiller" --version)
[[ $reported == "$stamp" ]] || die "scskiller --version says '$reported', not $stamp"

echo "dist/SCSKiller-linux: $(du -sm "$out" | cut -f1) MB, version $stamp"
