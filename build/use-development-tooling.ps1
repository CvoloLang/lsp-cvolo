# Build a development Tooling bundle from a fixed source revision.
# No published bundle is overwritten and tooling.version remains the release pin.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CompilerSource,
    [Parameter(Mandatory = $true)][string]$ExpectedCommit
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = [System.IO.Path]::GetFullPath((Join-Path $root $CompilerSource))
if ($ExpectedCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'ExpectedCommit must be a complete Git SHA'
}
$project = Join-Path $source 'src/Cvolo.Compiler.Tooling/Cvolo.Compiler.Tooling.csproj'
if (!(Test-Path $project)) { throw "Compiler source missing at $source" }
$actualCommit = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $ExpectedCommit) {
    throw "Compiler checkout mismatch: expected $ExpectedCommit, got $actualCommit"
}
$version = (Get-Content (Join-Path $root 'tooling.version') -Raw).Trim()
$compatibility = (Get-Content (Join-Path $root 'compiler-compatibility.version') -Raw).Trim()
$outputDir = (Join-Path $root 'artifacts/tooling/development-pinned') + [System.IO.Path]::DirectorySeparatorChar
& dotnet restore $project --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tooling restore failed' }
& dotnet build $project -c Release --no-restore --nologo "-p:ToolingArtifactDir=$outputDir" "-p:ToolingCommit=$ExpectedCommit"
if ($LASTEXITCODE -ne 0) { throw 'Tooling build failed' }
$manifestPath = Join-Path $outputDir 'tooling.manifest.json'
$dll = Join-Path $outputDir 'Cvolo.Compiler.Tooling.dll'
$sums = Join-Path $outputDir 'SHA256SUMS.txt'
if (!(Test-Path $dll) -or !(Test-Path $manifestPath) -or !(Test-Path $sums)) {
    throw 'Missing development Tooling dll, manifest or SHA256SUMS'
}
$m = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($m.Commit -ne $ExpectedCommit -or $m.ToolingVersion -ne $version -or
    $m.CompilerCompatibilityLine -ne $compatibility -or $null -ne $m.RuntimeIdentifier) {
    throw 'Development Tooling manifest provenance/version mismatch'
}
$entries = @(Get-Content $sums | Where-Object { $_ -ne '' })
foreach ($entry in $entries) {
    if ($entry -cnotmatch '^([0-9a-f]{64})  (.+)$') {
        throw "Invalid SHA256SUMS line: $entry"
    }
    $expected = $Matches[1]
    $relative = $Matches[2]
    if ($relative -match '(^/|^\.|(^|/)\.\.(/|$)|\\)' -or [System.IO.Path]::IsPathRooted($relative)) {
        throw "Invalid Tooling file path: $relative"
    }
    $file = Join-Path $outputDir ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (!(Test-Path $file -PathType Leaf)) { throw "Missing verified file: $relative" }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $expected) { throw "Checksum mismatch: $relative" }
}
$propsPath = Join-Path $root 'artifacts/tooling-dir.props'
$dirValue = [System.Security.SecurityElement]::Escape($outputDir)
$versionValue = [System.Security.SecurityElement]::Escape($version)
$commitValue = [System.Security.SecurityElement]::Escape($ExpectedCommit)
$compatValue = [System.Security.SecurityElement]::Escape($compatibility)
$props = @"
<Project>
  <PropertyGroup>
    <CvoloToolingDir>$dirValue</CvoloToolingDir>
    <CvoloToolingVersion>$versionValue</CvoloToolingVersion>
    <ToolingCommit>$commitValue</ToolingCommit>
    <CompilerCompatLine>$compatValue</CompilerCompatLine>
  </PropertyGroup>
</Project>
"@
[System.IO.File]::WriteAllText($propsPath, $props, [System.Text.UTF8Encoding]::new($false))
Write-Host "Verified development Tooling $version, compiler commit $ExpectedCommit"
