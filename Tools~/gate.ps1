#requires -Version 7
<#
  The merge gate: a headless NUnit EditMode run of this package's tests against a dedicated test
  venue. Exit 0 only when the suite completed with no failure, no skip (unless -AllowSkips) and at
  least one test.

  The venue, its provisioner and the runner belong to the Atelier workspace this repo is developed
  in (https://github.com/Ryan6-VRC/atelier), and have no version to ship, so the gate reaches up for them through -AtelierRoot and refuses
  by name when the workspace is absent. Outside that workspace, run the suite from Unity's Test
  Runner window instead.

  This folder ends in "~" so Unity never imports it into a project that mounts the package.
#>
[CmdletBinding()]
param(
  [string]$AtelierRoot = '',
  [Parameter(Mandatory)][string]$Tag,
  [string]$Filter = '',
  # One venue per concurrent run: two runs against one venue fight over its Library/.
  [string]$Venue = '',
  [string]$Assembly = 'Ryan6VRC.MeshOptimizer.Tests',
  [int]$TimeoutSec = 900,
  [switch]$AllowSkips
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pkgName = 'com.ryan6vrc.mesh-optimizer'

# The workspace root is the parent of this repo's main checkout, which holds from a worktree too.
if (-not $AtelierRoot) {
  $mainWt = (& git -C $root worktree list --porcelain | Select-Object -First 1) -replace '^worktree ', ''
  if (-not $mainWt) { Write-Error "cannot find this repo's main checkout through git; pass -AtelierRoot <the Atelier workspace root>."; exit 1 }
  $AtelierRoot = (Resolve-Path (Join-Path $mainWt '..')).Path
}
$setup  = Join-Path $AtelierRoot 'tools/setup-test-editor.ps1'
$runner = Join-Path $AtelierRoot 'tools/run-editmode-tests.ps1'
foreach ($f in @($setup, $runner)) {
  if (-not (Test-Path -LiteralPath $f)) {
    Write-Error "'$AtelierRoot' has no tools/$(Split-Path $f -Leaf); pass -AtelierRoot <the Atelier workspace root>."
    exit 1
  }
}
if (-not $Venue) { $Venue = Join-Path $AtelierRoot 'TestEditor-MeshOpt' }
$manifestPath = Join-Path $Venue 'Packages/manifest.json'

if (-not (Test-Path -LiteralPath $manifestPath)) {
  Write-Host "[gate] no venue at $Venue; provisioning."
  & pwsh -NoProfile -File $setup -Dest $Venue
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $manifestPath)) {
    Write-Error "provisioning did not produce '$Venue'; run $setup -Dest '$Venue' and read its refusal."
    exit 1
  }
}

# Mount this tree, main checkout or worktree, and unmount the workspace's own tool packages, which
# this suite does not use. Reapplied every run because re-provisioning regenerates the manifest.
$m = Get-Content $manifestPath -Raw | ConvertFrom-Json
$want = 'file:' + ($root -replace '\\', '/')
$changed = $false
foreach ($drop in 'com.ryan6vrc.agent-tools', 'com.ryan6vrc.avatar-tools') {
  if ($m.dependencies.PSObject.Properties[$drop]) { $m.dependencies.PSObject.Properties.Remove($drop); $changed = $true }
}
if ($m.dependencies.PSObject.Properties[$pkgName]) {
  if ($m.dependencies.$pkgName -ne $want) { $m.dependencies.$pkgName = $want; $changed = $true }
} else {
  $m.dependencies | Add-Member -NotePropertyName $pkgName -NotePropertyValue $want; $changed = $true
}
# The test asmdef is constrained on UNITY_INCLUDE_TESTS so a project that installs the package never
# compiles it; Unity compiles it only for a package the manifest lists as testable.
$testables = @($m.PSObject.Properties['testables'] ? $m.testables : @())
if ($pkgName -notin $testables) {
  $m | Add-Member -NotePropertyName testables -NotePropertyValue @($testables + $pkgName) -Force
  $changed = $true
}
if ($changed) {
  [IO.File]::WriteAllText($manifestPath, ($m | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}
Write-Host "[gate] tree under test: $root"
Write-Host "[gate] venue: $Venue"

$before = @(& git -C $root status --porcelain --untracked-files=all)
$runArgs = @('-NoProfile', '-File', $runner, '-Project', $Venue, '-Assemblies', $Assembly,
             '-Tag', $Tag, '-TimeoutSec', $TimeoutSec)
if ($Filter) { $runArgs += @('-Filter', $Filter) }
& pwsh @runArgs 2>&1 | Tee-Object -Variable out | Out-Host
$code = $LASTEXITCODE

# Unity writes a .meta beside every new file in a mounted package. An uncommitted one gets a fresh
# GUID in the next project that imports the package, so name them.
$after = @(& git -C $root status --porcelain --untracked-files=all)
$new = @($after | Where-Object { $_ -notin $before })
if ($new.Count -gt 0) {
  Write-Warning ("the run wrote into this tree; commit any .meta among these so its GUID holds:`n  " + ($new -join "`n  "))
}

if ($code -eq 0) {
  $line = @($out | Select-String 'OUTCOME=COMPLETED .*total=(\d+) passed=(\d+) failed=(\d+) skipped=(\d+)') | Select-Object -Last 1
  if (-not $line) { Write-Host "[gate] FAIL: the runner exited 0 without an OUTCOME=COMPLETED line."; exit 2 }
  $total, $passed, $failed, $skipped = $line.Matches[0].Groups[1..4] | ForEach-Object { [int]$_.Value }
  if ($total -eq 0) { Write-Host "[gate] FAIL: no test ran; '$Assembly' must be the test asmdef's name, and that assembly must compile in the venue."; exit 2 }
  if ($skipped -gt 0 -and -not $AllowSkips) { Write-Host "[gate] FAIL: $skipped skipped, and a skip is not a pass; -AllowSkips accepts them."; exit 6 }
  # An inconclusive test is counted in none of failed, skipped or passed, and it is not a pass.
  if ($failed -gt 0 -or $passed + $skipped -ne $total) { Write-Host "[gate] FAIL: $passed of $total passed with $failed failed and $skipped skipped; every test must pass."; exit 2 }
}
Write-Host "[gate] results: $(Join-Path $AtelierRoot "test-output/results-$Tag.xml")"
exit $code
