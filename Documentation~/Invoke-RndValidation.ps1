param(
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [Parameter(Mandatory = $true)][string]$EditorPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('All', 'Lifecycle', 'Scale')][string]$Suite = 'All',
    [string]$SavePath,
    [ValidateRange(1, 1024)][int]$SyntheticSectors = 2
)

$ErrorActionPreference = 'Stop'
$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$EditorPath = (Resolve-Path -LiteralPath $EditorPath).Path
if ($SavePath) {
    if ($Suite -ne 'Scale') { throw 'Use -Suite Scale with -SavePath.' }
    $SavePath = (Resolve-Path -LiteralPath $SavePath).Path
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$runName = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Suite.ToLowerInvariant()
$stem = Join-Path $OutputDirectory $runName
$cli = (Get-Command unity.exe -ErrorAction Stop).Source
$testArgs = @('test', $ProjectPath, '--editor-path', $EditorPath, '--mode', 'EditMode',
    '--output', "$stem.xml", '--timeout', '1200')
if ($Suite -eq 'Lifecycle') {
    $testArgs += @('--filter', 'Caelix.Tests.ReplicationTests;Caelix.Tests.MeshingRendererTests;Caelix.Tests.HostIntegrationTests;Caelix.Tests.NetCodecTests')
} elseif ($Suite -eq 'Scale') {
    $testArgs += @('--filter', 'Caelix.Tests.ReplicationTests.ReplicationScale_LoadSyncDeltaAndReloadMaintainExactBlockStorage')
}
$testArgs += @('--', '-logFile', "$stem.log")

# Always restore the caller's environment, including after a test failure.
$previousSave = $env:CAELIX_VALIDATION_SAVE
$previousSectors = $env:CAELIX_VALIDATION_SECTORS
$previousReport = $env:CAELIX_VALIDATION_REPORT
try {
    $env:CAELIX_VALIDATION_SAVE = $SavePath
    $env:CAELIX_VALIDATION_SECTORS = [string]$SyntheticSectors
    $env:CAELIX_VALIDATION_REPORT = "$stem.json"
    & $cli @testArgs
    if ($LASTEXITCODE -ne 0) { throw "Unity validation failed. See $stem.log and $stem.xml" }
    [xml]$results = Get-Content -LiteralPath "$stem.xml"
    $results.'test-run' | Select-Object result, total, passed, failed, skipped, duration
    if ($results.'test-run'.result -ne 'Passed') { throw "Tests did not pass: $stem.xml" }
    Write-Output "Results: $stem.xml; log: $stem.log; scale measurements: $stem.json"
} finally {
    $env:CAELIX_VALIDATION_SAVE = $previousSave
    $env:CAELIX_VALIDATION_SECTORS = $previousSectors
    $env:CAELIX_VALIDATION_REPORT = $previousReport
}
