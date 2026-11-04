#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ReadmePath = (Join-Path $PSScriptRoot '../README.md'),
    [string]$CoverageSummaryPath = (Join-Path $PSScriptRoot '../.local/coverage/summary.json'),
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'Stertz/QuoteMatch',
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$invariant = [Globalization.CultureInfo]::InvariantCulture
$sdk = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
[xml]$properties = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
[xml]$packages = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Packages.props') -Raw
$framework = $properties.SelectSingleNode('/Project/PropertyGroup/TargetFramework').InnerText
if ($framework -notmatch '^net(\d+\.\d+)$' -or $sdk -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid pinned framework or SDK version.' }
$target = $framework.Substring(3)
$summary = Get-Content -LiteralPath $CoverageSummaryPath -Raw | ConvertFrom-Json
$expectedModules = @('QuoteMatch.Domain', 'QuoteMatch.Application', 'QuoteMatch.Infrastructure', 'QuoteMatch.Desktop')
if (@($summary.Modules).Count -ne 4 -or (Compare-Object ($summary.Modules.Module | Sort-Object) ($expectedModules | Sort-Object))) {
    throw 'Coverage badges require the four measured application assemblies.'
}
foreach ($module in $summary.Modules) {
    if (($module.ValidLines -isnot [long] -and $module.ValidLines -isnot [int]) -or
        ($module.CoveredLines -isnot [long] -and $module.CoveredLines -isnot [int]) -or
        $module.ValidLines -le 0 -or $module.CoveredLines -lt 0 -or $module.CoveredLines -gt $module.ValidLines) {
        throw 'Coverage line counts must be nonempty, consistent integers.'
    }
}
function Get-CoverageBadge([object[]]$Modules, [string]$Label) {
    $covered = ($Modules | Measure-Object CoveredLines -Sum).Sum
    $valid = ($Modules | Measure-Object ValidLines -Sum).Sum
    $percentage = 100.0 * $covered / $valid
    $value = $percentage.ToString('F2', $invariant)
    $color = if ($percentage -ge 80) { 'brightgreen' } elseif ($percentage -ge 60) { 'yellow' } else { 'red' }
    return "  <a href=`"https://github.com/$Repository/actions/workflows/verify.yml`"><img src=`"https://img.shields.io/badge/$Label-$value%25-$color`" alt=`"$Label $value%`" /></a>"
}
function Get-StackBadge([string]$Label, [string]$Version, [string]$Color, [string]$Url) {
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$' -and $Version -notmatch '^\d+\.\d+$') { throw "Invalid version for $Label." }
    $badgeLabel = [Uri]::EscapeDataString($Label.Replace('-', '--'))
    $badgeVersion = [Uri]::EscapeDataString($Version.Replace('-', '--'))
    return "  <a href=`"$Url`"><img src=`"https://img.shields.io/badge/$badgeLabel-$badgeVersion-$Color`" alt=`"$Label $Version`" /></a>"
}
$lines = @(
    "  <a href=`"https://github.com/$Repository/actions/workflows/verify.yml`"><img src=`"https://github.com/$Repository/actions/workflows/verify.yml/badge.svg`" alt=`"Build and tests`" /></a>",
    "  <a href=`"https://github.com/$Repository/releases`"><img src=`"https://img.shields.io/github/v/release/$Repository`?label=release`" alt=`"Latest release`" /></a>",
    (Get-CoverageBadge @($summary.Modules) 'Coverage'),
    (Get-CoverageBadge @($summary.Modules | Where-Object Module -ne 'QuoteMatch.Desktop') 'Core'),
    (Get-StackBadge '.NET' $target '512BD4' 'https://dotnet.microsoft.com/'),
    (Get-StackBadge 'SDK' $sdk '512BD4' 'https://dotnet.microsoft.com/download')
)
$avaloniaVersions = $packages.SelectNodes('/Project/ItemGroup/PackageVersion[@Include="Avalonia"]/@Version')
if ($avaloniaVersions.Count -ne 1) { throw 'Missing or ambiguous central Avalonia version.' }
$lines += Get-StackBadge 'Avalonia' $avaloniaVersions[0].Value 'FF6600' 'https://avaloniaui.net/'
$readme = (Get-Content -LiteralPath $ReadmePath -Raw).Replace("`r`n", "`n")
$pattern = '(?s)<!-- PROJECT-BADGES:START -->.*?<!-- PROJECT-BADGES:END -->'
if ([regex]::Matches($readme, $pattern).Count -ne 1) { throw 'The README must contain exactly one PROJECT-BADGES marker block.' }
$block = "<!-- PROJECT-BADGES:START -->`n$($lines -join "`n")`n  <!-- PROJECT-BADGES:END -->"
$updated = [regex]::Replace($readme, $pattern, [Text.RegularExpressions.MatchEvaluator]{ param($match) $block })
if ($Check) {
    if ($updated -cne $readme) { throw 'README badges differ from the pinned stack and measured coverage.' }
} else {
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReadmePath), $updated, [Text.UTF8Encoding]::new($false))
}
Write-Output 'README badges match the pinned stack and measured application/core coverage.'
