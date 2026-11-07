# Branch threshold 85% (decision 18.10.2026): UI/platform guard branches are not covered by unit tests; lines stay at 90%.
param([double]$MinimumLineCoverage = 90, [double]$MinimumBranchCoverage = 85)
$ErrorActionPreference = 'Stop'
$runDirectory = Join-Path $PSScriptRoot ('TestResults/' + [Guid]::NewGuid().ToString('N'))
dotnet test (Join-Path $PSScriptRoot 'StartManager.Tests') -c Release '--collect:XPlat Code Coverage' --settings (Join-Path $PSScriptRoot 'coverage.runsettings') --results-directory $runDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
$report = Get-ChildItem -LiteralPath $runDirectory -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
if (!$report) { throw 'Coverage report not found.' }
[xml]$coverage = Get-Content -LiteralPath $report.FullName
$culture = [Globalization.CultureInfo]::InvariantCulture
$lines = [double]::Parse($coverage.coverage.'line-rate', $culture) * 100
$branches = [double]::Parse($coverage.coverage.'branch-rate', $culture) * 100
Write-Host ('Core coverage: lines {0:N2}%, branches {1:N2}%' -f $lines, $branches)
Write-Host $report.FullName
if ($lines -lt $MinimumLineCoverage -or $branches -lt $MinimumBranchCoverage) { throw 'Coverage below configured threshold.' }
