# Measures how the Intel Arc driver caches shaders: the answers IntelBackend's VendorCaps need (ARCHITECTURE.md, Driver
# caches). Runs the vendor-neutral selftest probes and probe11 from this folder, lists the candidate cache folders before
# and after, and zips the logs. Run it from the unpacked intel-arc-kit artifact with no game or GPU-heavy app open:
#   powershell -ExecutionPolicy Bypass -File measure.ps1            (or double-click run.cmd)
# -Quick: one run per probe instead of three. -Skip fields,dxr,...: leave probes out.
# Takes 5-20 minutes. Writes only throwaway cache entries under fresh exe names (scskf*, scskdxr*, ...) and its results.
param([switch]$Quick, [string[]]$Skip = @())

$ErrorActionPreference = 'Continue'
$kit = Split-Path -Parent $MyInvocation.MyCommand.Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $kit "results-$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
$runs = if ($Quick) { 1 } else { 3 }

function Log([string]$s) { Write-Host $s; Add-Content -Path (Join-Path $out 'summary.txt') -Value $s }

# A zip downloaded from GitHub carries the mark of the web; SmartScreen would stop the child processes.
Get-ChildItem $kit -File | Unblock-File -ErrorAction SilentlyContinue

foreach ($f in 'selftest.exe', 'd3d12.dll', 'probe11.exe') {
    if (-not (Test-Path (Join-Path $kit $f))) { Write-Host "missing $f next to this script: unpack the whole intel-arc-kit"; exit 1 }
}

# --- System -------------------------------------------------------------------------------------------------------------
Log "SCSKiller Intel Arc measurement, $stamp"
$os = Get-CimInstance Win32_OperatingSystem
Log "OS: $($os.Caption) $($os.Version) build $($os.BuildNumber)"
Log "CPU: $((Get-CimInstance Win32_Processor | Select-Object -First 1).Name)"
foreach ($v in Get-CimInstance Win32_VideoController) {
    Log ("GPU: {0} | driver {1} ({2:yyyy-MM-dd}) | {3}" -f $v.Name, $v.DriverVersion, $v.DriverDate, $v.PNPDeviceID)
}
# The display class keys: Intel's own version strings and any shader cache settings the driver exposes.
$class = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
Get-ChildItem $class -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    if ($p.ProviderName -match 'Intel') {
        $p | Select-Object * -ExcludeProperty PS* | Format-List | Out-String -Width 400 |
            Set-Content (Join-Path $out "registry-$($_.PSChildName).txt")
        Log "registry: $($_.PSChildName) $($p.DriverDesc) $($p.DriverVersion)"
    }
}

# --- Cache folders ------------------------------------------------------------------------------------------------------
$la = $env:LOCALAPPDATA
$cacheDirs = @("$la\Intel", "$($la)Low\Intel", "$la\D3DSCache", "$la\NVIDIA\DXCache", "$la\AMD\DxcCache")
function Snapshot([string]$name) {
    $rows = foreach ($d in $cacheDirs) {
        if (Test-Path $d) {
            Get-ChildItem $d -Recurse -File -Force -ErrorAction SilentlyContinue |
                Select-Object FullName, Length, @{n = 'LastWriteTime'; e = { $_.LastWriteTimeUtc.ToString('o') } }
        }
    }
    $rows | Export-Csv (Join-Path $out "cache-$name.csv") -NoTypeInformation
    return $rows
}
$before = Snapshot 'before'
foreach ($d in $cacheDirs) {
    if (Test-Path $d) {
        $files = @(Get-ChildItem $d -Recurse -File -Force -ErrorAction SilentlyContinue)
        Log ("cache folder: {0}: {1} files, {2:N1} MB" -f $d, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))
    } else { Log "cache folder: $d (none)" }
}

# --- Probes -------------------------------------------------------------------------------------------------------------
# Each probe works in its own run folder next to the exe and prints its table; its whole output goes to <name>.log.
$probes = @(
    @{ n = 'e2e';        exe = 'selftest.exe'; args = @();                                why = 'recorder + warm through the proxy d3d12.dll' },
    @{ n = 'fields';     exe = 'selftest.exe'; args = @('fields', "$runs");               why = 'D3D12 cache key: exe name / folder, which PSO state recompiles, per stage or per pair (DXBC)' },
    @{ n = 'fieldsdxil'; exe = 'selftest.exe'; args = @('fields', "$runs", 'dxil');       why = 'the same with DXIL shaders, as D3D12 games ship' },
    @{ n = 'dxr';        exe = 'selftest.exe'; args = @('dxr', "$runs");                  why = 'ray tracing state objects: cached? per collection or whole object?' },
    @{ n = 'bindless';   exe = 'selftest.exe'; args = @('bindless', "$runs");             why = 'SM 6.6 heap-indexed and RayQuery compute PSOs' },
    @{ n = 'd3d11';      exe = 'probe11.exe';  args = @();                                why = 'D3D11 driver cache: keyed how, state-dependent?' },
    @{ n = 'vulkan';     exe = 'selftest.exe'; args = @('vulkan', '1');                   why = 'Vulkan driver cache (informative)' }
)
$env:SELFTEST_DXC = $kit   # dxcompiler.dll + dxil.dll ship in the kit
foreach ($p in $probes) {
    if ($Skip -contains $p.n) { Log "skip $($p.n)"; continue }
    Log ""
    Log "=== $($p.n): $($p.why)"
    $log = Join-Path $out "$($p.n).log"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Push-Location $kit
    & (Join-Path $kit $p.exe) @($p.args) 2>&1 | Tee-Object -FilePath $log | Out-Host
    $code = $LASTEXITCODE
    Pop-Location
    Log ("{0}: exit {1}, {2:N0} s" -f $p.n, $code, $sw.Elapsed.TotalSeconds)
    # the verdict lines, so summary.txt reads on its own
    Select-String -Path $log -Pattern 'cache key:|selftest (OK|FAILED)|adapter:|new driver-cache files|new D3DSCache files' -ErrorAction SilentlyContinue |
        ForEach-Object { Log "  $($_.Line.Trim())" }
}

# --- What the probes wrote ----------------------------------------------------------------------------------------------
$after = Snapshot 'after'
$old = @{}
foreach ($r in $before) { $old[$r.FullName] = $r }
Log ""
Log "=== cache files new or grown during the run"
foreach ($r in $after) {
    $o = $old[$r.FullName]
    if (-not $o) { Log ("  new    {0} ({1:N0} bytes)" -f $r.FullName, $r.Length) }
    elseif ($o.Length -ne $r.Length) { Log ("  grown  {0} {1:N0} -> {2:N0}" -f $r.FullName, $o.Length, $r.Length) }
    elseif ($o.LastWriteTime -ne $r.LastWriteTime) { Log "  written $($r.FullName)" }
}

$zip = Join-Path ([Environment]::GetFolderPath('Desktop')) "scskiller-intel-results-$stamp.zip"
Compress-Archive -Path "$out\*" -DestinationPath $zip -Force
Log ""
Log "Done. Results: $zip"
Log "Attach that zip (or paste summary.txt) where you were asked for it."
