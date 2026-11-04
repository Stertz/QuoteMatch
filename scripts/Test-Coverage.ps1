param(
    [ValidateRange(0, 100)][double]$MinimumLineCoverage = 80,
    [string]$ArtifactsDirectory = '.local/verification',
    [string]$OutputDirectory = '.local/coverage',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    $artifactsPath = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
    $outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
    $settingsPath = Join-Path $repositoryRoot 'tests/coverage.config.xml'
    [void][System.IO.Directory]::CreateDirectory($outputPath)
    if (-not $NoRestore) {
        & dotnet restore QuoteMatch.slnx --artifacts-path $artifactsPath
        if ($LASTEXITCODE -ne 0) { throw 'Dependency restoration failed.' }
    }
    & dotnet build QuoteMatch.slnx --artifacts-path $artifactsPath --no-restore -p:ContinuousIntegrationBuild=true --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The coverage build failed.' }

    $moduleLines = @{}
    foreach ($project in @('QuoteMatch.Tests', 'QuoteMatch.Desktop.Tests')) {
        $assembly = Join-Path $artifactsPath "bin/$project/debug/$project.dll"
        $reportPath = Join-Path $outputPath "$project.cobertura.xml"
        & dotnet $assembly --coverage --coverage-output-format cobertura --coverage-output $reportPath --coverage-settings $settingsPath --progress off
        if ($LASTEXITCODE -ne 0) { throw "Tests or coverage collection failed for $project." }
        [xml]$report = Get-Content -LiteralPath $reportPath -Raw
        foreach ($package in $report.coverage.packages.package) {
            if (-not $moduleLines.ContainsKey($package.name)) { $moduleLines[$package.name] = @{} }
            foreach ($class in $package.classes.class) {
                foreach ($line in $class.lines.line) {
                    $key = $class.filename.Replace('\', '/') + ':' + $line.number
                    $moduleLines[$package.name][$key] = $moduleLines[$package.name][$key] -or ([long]$line.hits -gt 0)
                }
            }
        }
    }
    $expectedModules = @('QuoteMatch.Domain', 'QuoteMatch.Application', 'QuoteMatch.Infrastructure', 'QuoteMatch.Desktop')
    $modules = foreach ($name in $expectedModules) {
        if (-not $moduleLines.ContainsKey($name) -or $moduleLines[$name].Count -eq 0) { throw "Coverage is missing for $name." }
        $covered = @($moduleLines[$name].Values | Where-Object { $_ }).Count
        [PSCustomObject]@{ Module = $name; CoveredLines = $covered; ValidLines = $moduleLines[$name].Count; LineCoveragePercent = [math]::Round(100.0 * $covered / $moduleLines[$name].Count, 2) }
    }
    $allCovered = ($modules | Measure-Object -Property CoveredLines -Sum).Sum
    $allValid = ($modules | Measure-Object -Property ValidLines -Sum).Sum
    $coreModules = @($modules | Where-Object { $_.Module -ne 'QuoteMatch.Desktop' })
    $coreCovered = ($coreModules | Measure-Object -Property CoveredLines -Sum).Sum
    $coreValid = ($coreModules | Measure-Object -Property ValidLines -Sum).Sum
    $totalRate = 100.0 * $allCovered / $allValid
    $coreRate = 100.0 * $coreCovered / $coreValid
    $summary = [PSCustomObject]@{
        MinimumLineCoveragePercent = $MinimumLineCoverage
        TotalLineCoveragePercent = [math]::Round($totalRate, 2)
        CoreLineCoveragePercent = [math]::Round($coreRate, 2)
        Modules = @($modules)
    }
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'summary.json') -Encoding utf8
    $modules | Format-Table -AutoSize | Out-Host
    Write-Host ('Core line coverage: {0:N2}%; total line coverage: {1:N2}%.' -f $coreRate, $totalRate)
    if ($totalRate -lt $MinimumLineCoverage -or $coreRate -lt $MinimumLineCoverage) { throw 'Line coverage is below the required threshold.' }
}
finally { Pop-Location }
