# The test suite as several test processes at once, each running its share of the tests, the serial Timing collection
# included. Everything a test shares with another test runs in one process (static state, process names, the ledger and
# mutex scope, temp folders are all per process), so processes can't clash where tests of one process would.
# AppTests goes by the first letters of its test names, in shares of about the same time; the last share takes the rest.
# $Alone: tests that race with themselves under load, run after the shares with nothing beside them.
param(
    [string]$Filter = 'Needs!=Gpu&Needs!=Game',
    [string]$Configuration = 'Release',
    [switch]$List   # print each share's test count instead of running them
)
$ErrorActionPreference = 'Stop'

$dll = Join-Path $PSScriptRoot "..\tests\SCSKiller.Tests\bin\$Configuration\net10.0-windows\SCSKiller.Tests.dll"
if (-not (Test-Path $dll)) { throw "not built: $dll" }

$app = 'FullyQualifiedName~Platform.AppTests.'
$appShares = @(
    @('T'),
    @('An'),
    @('A_r', 'O'),
    @('A_c', 'A_g'),
    @('B', 'A_p'),
    @('A_s', 'R', 'A_m'),
    @('A_d', 'Am', 'S', 'A_h', 'A_f', 'W', 'A_w')
)
function Any($prefixes) { '(' + (($prefixes | ForEach-Object { "$app$_" }) -join '|') + ')' }
$taken = $appShares | ForEach-Object { $_ } | ForEach-Object { "FullyQualifiedName!~Platform.AppTests.$_" }
$shares = [ordered]@{}
for ($i = 0; $i -lt $appShares.Count; $i++) { $shares["app$($i + 1)"] = Any $appShares[$i] }
$shares['app-rest'] = (@($app) + $taken) -join '&'
$shares['overlay'] = 'FullyQualifiedName~Platform.OverlayIniTests.'
$shares['rest'] = 'FullyQualifiedName!~Platform.AppTests.&FullyQualifiedName!~Platform.OverlayIniTests.'
$alone = @()
foreach ($name in @($shares.Keys)) { $shares[$name] = (@("($($shares[$name]))") + ($alone | ForEach-Object { "FullyQualifiedName!~$_" })) -join '&' }

$out = Join-Path ([IO.Path]::GetTempPath()) ("scskiller-test-shares-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $out | Out-Null
function Run($names) {
    $runs = foreach ($name in $names) {
        $a = @('test', "`"$dll`"", '--filter', "`"($($shares[$name]))&($Filter)`"")
        $a += if ($List) { '--list-tests' } else { @('--logger', '"console;verbosity=normal"', '--', 'RunConfiguration.TreatNoTestsAsError=true') }
        $p = Start-Process dotnet -ArgumentList $a -NoNewWindow -PassThru `
            -RedirectStandardOutput (Join-Path $out "$name.log") -RedirectStandardError (Join-Path $out "$name.err")
        $null = $p.Handle   # keeps the exit code readable after the process ends
        [pscustomobject]@{ Name = $name; Process = $p }
    }
    foreach ($r in $runs) {
        $r.Process.WaitForExit()
        $log = Get-Content (Join-Path $out "$($r.Name).log")
        $script:logs += $log
        if ($List) { "$($r.Name): $(@($log | Where-Object { $_ -match '^\s{4}\S' }).Count) tests, exit $($r.Process.ExitCode)" }
        else {
            "::group::$($r.Name)"
            $log
            '::endgroup::'
            "$($r.Name): exit $($r.Process.ExitCode) in $([int]($r.Process.ExitTime - $r.Process.StartTime).TotalSeconds) s"
        }
        Get-Content (Join-Path $out "$($r.Name).err")
        if ($r.Process.ExitCode -ne 0) { $script:failed += $r.Name }
    }
}
$failed = @(); $logs = @()
Run @($shares.Keys)
$shares['alone'] = '(' + (($alone | ForEach-Object { "FullyQualifiedName~$_" }) -join '|') + ')'
if ($alone) { Run @('alone') }
if (-not $List) {
    # each share's vstest summary: "Total tests: N" then indented "Passed: n", "Failed: n", "Skipped: n" lines
    $text = ($logs -join "`n") + "`n"; $n = @{ Total = 0; Passed = 0; Failed = 0; Skipped = 0 }; $done = 0
    foreach ($m in [regex]::Matches($text, '(?m)^Total tests:\s*(\d+)\s*\n((?:[ \t]+\w+:\s*\d+\s*\n)*)')) {
        $done++; $n.Total += [int]$m.Groups[1].Value
        foreach ($c in [regex]::Matches($m.Groups[2].Value, '(Passed|Failed|Skipped):\s*(\d+)')) { $n[$c.Groups[1].Value] += [int]$c.Groups[2].Value }
    }
    "shares: $done of $($shares.Count - 1 + [int][bool]$alone) with a summary"
    "total $($n.Total), passed $($n.Passed), failed $($n.Failed), skipped $($n.Skipped)"
    [regex]::Matches($text, '(?m)^\s+Failed\s+(.+?)(?:\s+\[[^\]]*\])?\s*$') | ForEach-Object { "FAILED $($_.Groups[1].Value)" } | Sort-Object -Unique
}
if ($failed) { "failed: $($failed -join ', ')" }
"exit $([int][bool]$failed)"
# a share's process can still hold its .err for a moment: a failed cleanup only warns
try {
    if ($out -and $out -like "$([IO.Path]::GetTempPath())scskiller-test-shares-*") { Remove-Item -Recurse -Force -LiteralPath $out }
    else { Write-Warning "left ${out}: not under $([IO.Path]::GetTempPath())scskiller-test-shares-*" }
} catch { Write-Warning "left ${out}: $($_.Exception.Message)" }
exit $([int][bool]$failed)
