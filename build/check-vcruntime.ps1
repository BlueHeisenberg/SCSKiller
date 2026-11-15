<#
.SYNOPSIS
Fails when a PE under -Dir imports a Visual C++ runtime DLL (vcruntime140.dll, msvcp140.dll, ...) that isn't in that
PE's own folder. Windows doesn't ship the VC++ runtime: without it beside the DLL, a PC without the redistributable
can't load it (libsodium at startup: the app never starts). Normal and delay-load imports both count. With
vcruntime140.dll at the top of -Dir, VC-REDIST.txt there must name its file version.
  powershell -NoProfile -ExecutionPolicy Bypass -File <path to this script> -Dir <folder>
#>
param([Parameter(Mandatory = $true)][string]$Dir)
$ErrorActionPreference = "Stop"
# api-ms-win-crt-*.dll and ucrtbase.dll (the Universal CRT) are part of Windows 10 and later, so they don't match
$vcrt = '^(vcruntime|msvcp|msvcr|vcomp|concrt|vccorlib|vcamp|mfc)\d'

# the DLL names a PE imports, nothing for a file without the MZ signature; throws on a malformed PE
function Get-PEImports([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    if ($b.Length -lt 2 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return }
    if ($b.Length -lt 64) { throw "truncated DOS header" }
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    if ($pe -lt 0 -or $pe + 26 -gt $b.Length -or [BitConverter]::ToUInt32($b, $pe) -ne 0x4550) { throw "no PE header" }
    $opt = $pe + 24
    $magic = [BitConverter]::ToUInt16($b, $opt)
    if ($magic -ne 0x10B -and $magic -ne 0x20B) { throw "optional header magic 0x$('{0:X}' -f $magic)" }
    $pe32plus = $magic -eq 0x20B
    $imageBase = if ($pe32plus) { [BitConverter]::ToUInt64($b, $opt + 24) } else { [uint64][BitConverter]::ToUInt32($b, $opt + 28) }
    $dirs = $opt + $(if ($pe32plus) { 112 } else { 96 })
    $count = [BitConverter]::ToUInt32($b, $dirs - 4)
    $sections = $opt + [BitConverter]::ToUInt16($b, $pe + 20)
    $n = [BitConverter]::ToUInt16($b, $pe + 6)
    if ($sections + 40 * $n -gt $b.Length) { throw "truncated section table" }
    function Offset([uint64]$rva) {
        for ($i = 0; $i -lt $n; $i++) {
            $s = $sections + 40 * $i
            $va = [BitConverter]::ToUInt32($b, $s + 12)
            if ($rva -ge $va -and $rva -lt $va + [BitConverter]::ToUInt32($b, $s + 16)) {
                $o = [int64]($rva - $va + [BitConverter]::ToUInt32($b, $s + 20))
                if ($o -lt $b.Length) { return [int]$o }
            }
        }
        throw "RVA 0x$('{0:X}' -f $rva) is in no section"
    }
    function AllZero([int]$o, [int]$size) { for ($i = 0; $i -lt $size; $i++) { if ($b[$o + $i]) { return $false } }; $true }
    # import directory (1): 20-byte descriptors, name RVA at +12. Delay-load directory (13): 32-byte, name at +4, an RVA
    # when attributes bit 0 is set, else a virtual address (old linkers). Each table ends with an all-zero descriptor.
    foreach ($d in @(1, 20, 12), @(13, 32, 4)) {
        if ($count -le $d[0]) { continue }
        $rva = [BitConverter]::ToUInt32($b, $dirs + 8 * $d[0])
        if ($rva -eq 0) { continue }
        for ($o = Offset $rva; ; $o += $d[1]) {
            if ($o + $d[1] -gt $b.Length) { throw "import table runs past the end of the file" }
            if (AllZero $o $d[1]) { break }
            [uint64]$name = [BitConverter]::ToUInt32($b, $o + $d[2])
            if ($name -eq 0) { throw "import descriptor without a name" }
            if ($d[0] -eq 13 -and -not ([BitConverter]::ToUInt32($b, $o) -band 1)) { $name -= $imageBase }
            $s = Offset $name
            $e = [Array]::IndexOf($b, [byte]0, $s)
            if ($e -lt 0) { throw "unterminated import name" }
            [Text.Encoding]::ASCII.GetString($b, $s, $e - $s)
        }
    }
}

$root = (Resolve-Path $Dir).Path
$missing = foreach ($f in Get-ChildItem $root -Recurse -File -Include *.dll, *.exe) {
    try { $imports = @(Get-PEImports $f.FullName) } catch { throw "$($f.FullName): can't read its imports: $_" }
    foreach ($i in $imports | Where-Object { $_ -match $vcrt } | Sort-Object -Unique) {
        if (-not (Test-Path -LiteralPath (Join-Path $f.DirectoryName $i) -PathType Leaf)) { "$($f.FullName.Substring($root.Length + 1)) imports $i, not in its folder" }
    }
}
if ($missing) { throw "VC++ runtime missing (the app can't start on a PC without the redistributable):`n  $($missing -join "`n  ")" }
$dll = Join-Path $root "vcruntime140.dll"
if (Test-Path -LiteralPath $dll -PathType Leaf) {
    $v = (Get-Item -LiteralPath $dll).VersionInfo
    $version = "$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart).$($v.FilePrivatePart)"
    $txt = Join-Path $root "VC-REDIST.txt"
    $text = if (Test-Path -LiteralPath $txt -PathType Leaf) { [IO.File]::ReadAllText($txt) } else { "" }
    if ($version -eq "0.0.0.0" -or $text -notmatch "(^|\s)$([regex]::Escape($version))(\s|$)") { throw "VC-REDIST.txt doesn't name vcruntime140.dll's file version $version" }
}
Write-Host "VC++ runtime: every import is satisfied in its own folder"
