<#
.SYNOPSIS
Builds build\packages\SCSKiller.CUE4Parse.<version>.nupkg: CUE4Parse at the pinned upstream commit with every game key
and decryption constant removed (strip.txt, by path and symbol, never by value), then scsk.patch (the user-key hooks),
then checked by tools\keyscan before it is copied.

-StripKept also removes strip-kept.txt: the cipher variants and header masks of games that still read with a user key.

Run it on every CUE4Parse bump: set $commit to the commit in the new package's nuspec, rebuild, review the strip
specs against the new sources, and commit the new .nupkg with the specs. scsk.patch applies to the stripped tree (LF):
regenerate it by diffing a stripped tree before and after the edits, never against upstream (its removed lines would
carry the values).
#>
param([switch]$StripKept, [switch]$KeepTemp)
$ErrorActionPreference = "Stop"
$commit = "b4e95441bcf0c975eb3adb68c0fb44c740c2cf62"   # CUE4Parse 1.2.2.202609's nuspec
$version = "1.2.2.202609-scsk.1"
$here = $PSScriptRoot
$repo = Split-Path (Split-Path $here -Parent) -Parent
$feed = Join-Path $repo "build\packages\"

$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT "dotnet.exe"))) { $dotnet = Join-Path $env:DOTNET_ROOT "dotnet.exe" }
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
function Run([string]$exe, [string[]]$argv) {
    & $exe @argv
    if ($LASTEXITCODE -ne 0) { throw "$exe $argv failed with exit code $LASTEXITCODE" }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("scs-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $src = Join-Path $tmp "src"
    Run git @("-c", "init.defaultBranch=main", "init", "-q", $src)
    Run git @("-C", $src, "-c", "core.autocrlf=false", "fetch", "-q", "--depth", "1", "https://github.com/FabianFG/CUE4Parse", $commit)
    Run git @("-C", $src, "-c", "core.autocrlf=false", "checkout", "-q", "FETCH_HEAD")
    $proj = Join-Path $src "CUE4Parse"

    $specs = @((Join-Path $here "strip.txt"))
    if ($StripKept) { $specs += Join-Path $here "strip-kept.txt" }
    Run $dotnet (@("run", (Join-Path $here "strip.cs"), "--", $proj) + $specs)

    $patch = Join-Path $here "scsk.patch"
    foreach ($m in Select-String -Path $patch -Pattern '^\+\+\+ (\S+)') {
        $f = Join-Path $proj $m.Matches[0].Groups[1].Value
        if (Test-Path $f) { [IO.File]::WriteAllText($f, ([IO.File]::ReadAllText($f) -replace "`r`n", "`n")) }
    }
    Run git @("-C", $src, "apply", "--directory=CUE4Parse", "-p0", $patch)

    # Apache-2.0 4(b): the changed files, by path
    $changed = @(Get-Content $specs | Where-Object { $_ -match '^\s*(gut\S*|empty)\s+(\S+)' } | ForEach-Object { $Matches[2] }) +
        @(Select-String -Path $patch -Pattern '^\+\+\+ (\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    $notice = "SCSKiller's build of CUE4Parse $commit. Changed by the SCSKiller authors: game keys and decryption constants " +
        "removed, the Theia and .utoc keys taken from the caller. Changed paths (under CUE4Parse/):`n" + (($changed | Sort-Object -Unique) -join "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $src "MODIFIED.txt"), $notice)

    $pkg = Join-Path $tmp "pkg"
    Run $dotnet @("pack", $proj, "-c", "Release", "-o", $pkg, "-p:CUE4PARSE_SKIP_NATIVE=true", "-p:PackageVersion=$version",
        "-p:ContinuousIntegrationBuild=true", "-p:DebugType=None", "-p:DebugSymbols=false", "-p:IncludeSymbols=false",
        "-p:PublishRepositoryUrl=false", "-p:EmbedUntrackedSources=false")
    $nupkg = Get-ChildItem $pkg -Filter "SCSKiller.CUE4Parse.$version.nupkg"
    if (-not $nupkg) { throw "dotnet pack made no SCSKiller.CUE4Parse.$version.nupkg" }

    $scan = Join-Path $tmp "keyscan"
    Run $dotnet @("build", (Join-Path $repo "tools\keyscan\keyscan.csproj"), "-c", "Release", "-o", $scan, "--nologo", "-v", "q")
    Run $dotnet @((Join-Path $scan "keyscan.dll"), "--known", (Join-Path $repo "build\keyscan\known-keys.txt"),
        "--allow", (Join-Path $repo "build\keyscan\allow.txt"), "--no-pins", $nupkg.FullName)

    New-Item -ItemType Directory -Force $feed | Out-Null
    Get-ChildItem $feed -Filter "SCSKiller.CUE4Parse.*.nupkg" | Remove-Item
    Copy-Item $nupkg.FullName $feed
    Write-Host "$($nupkg.Name): sha256 $((Get-FileHash $nupkg.FullName -Algorithm SHA256).Hash.ToLower())"
    # what ships is pinned: a new build fails every publish until its review updates the pin
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
    try { $dll = $z.GetEntry("lib/net10.0/CUE4Parse.dll").Open(); $sha = -join ([Security.Cryptography.SHA256]::Create().ComputeHash($dll) | ForEach-Object { $_.ToString("x2") }); $dll.Dispose() }
    finally { $z.Dispose() }
    Write-Host "CUE4Parse.dll: sha256 $sha (build/keyscan/allow.txt pins it: pin CUE4Parse <sha256>)"
}
finally {
    if ($KeepTemp) { Write-Host "kept $tmp" }
    elseif ($tmp -match '\\scs-[0-9a-f]{32}$' -and (Test-Path $tmp)) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}
