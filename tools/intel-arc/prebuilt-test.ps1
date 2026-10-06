# Does Intel's driver load a prebuilt shader file (C:\ProgramData\Intel\IGSDS\PrebuiltShaderBinaries\<dGPU|iGPU>\<family>\
# <name>.pso.bin, as Intel ships directml.pso.bin) for any exe, and does it accept a copy of that exe's own cache file
# (LocalLow\Intel\ShaderCache, the same INSC container, unpacked)? Needs an administrator PowerShell (ProgramData\Intel).
#   powershell -ExecutionPolicy Bypass -File prebuilt-test.ps1
# Under a throwaway exe name: compile PSOs (cold), again (cache hit), with the cache file moved away (cold again: the
# baseline), then with a copy of the cache file placed as a prebuilt under each candidate name and the cache file moved
# away again. A run as fast as the hit = the driver loaded that prebuilt. Only files it creates are touched; the cache
# file is put back and every prebuilt copy removed at the end.
param([int]$Count = 600, [int]$Unroll = 250, [string]$Family)

$ErrorActionPreference = 'Continue'
$kit = Split-Path -Parent $MyInvocation.MyCommand.Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $kit "prebuilt-test-$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
function Log([string]$s) { Write-Host $s; Add-Content -Path (Join-Path $out 'summary.txt') -Value $s }
Get-ChildItem $kit -File | Unblock-File -ErrorAction SilentlyContinue

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Run this from an administrator PowerShell: it writes to C:\ProgramData\Intel.'; exit 1
}
$cache = "$($env:LOCALAPPDATA)Low\Intel\ShaderCache"
$root = 'C:\ProgramData\Intel\IGSDS\PrebuiltShaderBinaries'
# The GPU family folder: where Intel's own prebuilts are (BMG on a B580), unless given.
if (-not $Family) {
    $fam = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | ForEach-Object { Get-ChildItem $_.FullName -Directory } |
        Where-Object { Get-ChildItem $_.FullName -Filter *.pso.bin -File -ErrorAction SilentlyContinue } | Select-Object -First 1
    $Family = if ($fam) { $fam.FullName } else { Join-Path $root 'dGPU\BMG' }
}
Log "prebuilt test, $stamp"
foreach ($v in Get-CimInstance Win32_VideoController) { Log ("GPU: {0} | driver {1}" -f $v.Name, $v.DriverVersion) }
Log "prebuilt folder: $Family"
Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { Log ("  existing: {0} ({1:N0} bytes)" -f $_.FullName, $_.Length) }

$name = "scskpb$((Get-Random -Maximum 999999))"
$exe = Join-Path $kit "$name.exe"
Copy-Item (Join-Path $kit 'selftest.exe') $exe
function Run([string]$what) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $exe dxcfill $Count $Unroll 7 0 | Out-Null
    $s = $sw.Elapsed.TotalSeconds
    Log ("{0,-48} {1,7:N2} s" -f $what, $s)
    return $s
}
$before = @{}; Get-ChildItem $cache -File -ErrorAction SilentlyContinue | ForEach-Object { $before[$_.Name] = 1 }
$cold = Run 'cold (first run under this name)'
$mine = Get-ChildItem $cache -File | Where-Object { -not $before.ContainsKey($_.Name) } | Sort-Object Length -Descending | Select-Object -First 1
if (-not $mine) { Log 'no new cache file: is the Intel GPU the default adapter?'; Remove-Item $exe; exit 1 }
Log ("cache file: {0} ({1:N0} bytes)" -f $mine.Name, $mine.Length)
$hit = Run 'again (driver cache hit)'
$saved = Join-Path $out "cache-$($mine.Name)"
Move-Item $mine.FullName $saved
$base = Run 'cache file moved away (cold baseline)'
Remove-Item (Join-Path $cache $mine.Name) -ErrorAction SilentlyContinue   # that run wrote a fresh one

# Candidate names: the exe's name in the forms a driver might use, and the cache file's own name.
$placed = @()
$result = 'no candidate loaded'
foreach ($n in @("$name.exe", $name, $name.ToUpperInvariant() + '.EXE', $mine.Name)) {
    $p = Join-Path $Family "$n.pso.bin"
    try { New-Item -ItemType Directory -Force $Family | Out-Null; Copy-Item $saved $p -ErrorAction Stop; $placed += $p }
    catch { Log "couldn't write $p : $($_.Exception.Message)"; continue }
    $t = Run "prebuilt as '$n.pso.bin'"
    Remove-Item $p -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $cache $mine.Name) -ErrorAction SilentlyContinue
    if ($t -lt ($hit + ($base - $hit) / 3)) { $result = "LOADED as '$n.pso.bin'"; break }
}
Log ''
Log ("result: {0} (cold {1:N2} s, hit {2:N2} s, baseline {3:N2} s)" -f $result, $cold, $hit, $base)

# Clean up: no prebuilt left, the cache file back where it was, no throwaway exe.
foreach ($p in $placed) { Remove-Item $p -ErrorAction SilentlyContinue }
Move-Item $saved (Join-Path $cache $mine.Name) -Force -ErrorAction SilentlyContinue
Remove-Item $exe -ErrorAction SilentlyContinue
$zip = Join-Path ([Environment]::GetFolderPath('Desktop')) "scskiller-intel-prebuilt-$stamp.zip"
Get-ChildItem $out -Filter 'cache-*' | Remove-Item -ErrorAction SilentlyContinue
Compress-Archive -Path "$out\*" -DestinationPath $zip -Force
Log "Done. Results: $zip"
