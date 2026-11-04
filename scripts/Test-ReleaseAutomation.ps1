#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporary = Join-Path $temporaryBase "quotematch-automation-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporary | Out-Null
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-True $rejected 'Invalid input was unexpectedly accepted.'
}
try {
    $versionProject = Join-Path $temporary 'Version.proj'
    '<Project><PropertyGroup><VersionPrefix>0.1.0</VersionPrefix></PropertyGroup></Project>' | Set-Content $versionProject
    $previousOutput = $env:GITHUB_OUTPUT
    try {
        $env:GITHUB_OUTPUT = Join-Path $temporary 'version-output.txt'
        foreach ($case in @(@(1, '0.1.0'), @(2, '0.1.1'), @(42, '0.1.41'))) {
            $version = & (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1') -RunNumber $case[0] -ProjectPath $versionProject
            Assert-True ($version -ceq $case[1]) 'The workflow counter did not produce the expected numeric version.'
        }
        Assert-True ((Get-Content $env:GITHUB_OUTPUT) -contains 'tag=v0.1.41') 'The generated release tag does not match the version.'
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1') -RunNumber 0 -ProjectPath $versionProject }
    } finally { $env:GITHUB_OUTPUT = $previousOutput }
    '{"sdk":{"version":"99.2.345"}}' | Set-Content (Join-Path $temporary 'global.json')
    '<Project><PropertyGroup><TargetFramework>net99.2</TargetFramework></PropertyGroup></Project>' | Set-Content (Join-Path $temporary 'Directory.Build.props')
    @'
<Project><ItemGroup>
<PackageVersion Include="Avalonia" Version="91.2.3" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="92.3.4" />
<PackageVersion Include="CommunityToolkit.Mvvm" Version="93.4.5" />
<PackageVersion Include="Microsoft.Data.SqlClient" Version="94.5.6" />
</ItemGroup></Project>
'@ | Set-Content (Join-Path $temporary 'Directory.Packages.props')
    $readme = Join-Path $temporary 'README.md'
    '<!-- PROJECT-BADGES:START --><!-- PROJECT-BADGES:END -->' | Set-Content $readme
    $summary = @{
        Modules = @(
            @{ Module = 'QuoteMatch.Domain'; CoveredLines = 1; ValidLines = 2 },
            @{ Module = 'QuoteMatch.Application'; CoveredLines = 1; ValidLines = 2 },
            @{ Module = 'QuoteMatch.Infrastructure'; CoveredLines = 1; ValidLines = 2 },
            @{ Module = 'QuoteMatch.Desktop'; CoveredLines = 9; ValidLines = 10 }
        )
    }
    $coverage = Join-Path $temporary 'summary.json'
    $summary | ConvertTo-Json -Depth 5 | Set-Content $coverage
    $arguments = @{ ReadmePath = $readme; CoverageSummaryPath = $coverage; RepositoryRoot = $temporary; Repository = 'synthetic/quotation' }
    & (Join-Path $PSScriptRoot 'Update-ReadmeBadges.ps1') @arguments | Out-Null
    $generated = Get-Content $readme -Raw
    Assert-True (-not $generated.Contains('include_prereleases')) 'The release badge must show normal releases.'
    foreach ($excluded in @('EF%20Core-', 'MVVM%20Toolkit-', 'SqlClient-')) {
        Assert-True (-not $generated.Contains($excluded)) 'The README must retain only the selected stack badges.'
    }
    foreach ($expected in @('.NET-99.2-', 'SDK-99.2.345-', 'Avalonia-91.2.3-', 'Core-50.00%25-red', 'Coverage-75.00%25-yellow', 'synthetic/quotation/actions/workflows/verify.yml')) {
        Assert-True $generated.Contains($expected) "Generated badges omitted $expected."
    }
    & (Join-Path $PSScriptRoot 'Update-ReadmeBadges.ps1') @arguments -Check | Out-Null
    [xml]$packages = Get-Content (Join-Path $temporary 'Directory.Packages.props') -Raw
    $packages.SelectSingleNode('/Project/ItemGroup/PackageVersion[@Include="Avalonia"]').SetAttribute('Version', '91.2.4')
    $packages.Save((Join-Path $temporary 'Directory.Packages.props'))
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Update-ReadmeBadges.ps1') @arguments -Check }
    Assert-True ((Get-Content $readme -Raw) -ceq $generated) 'Check mode changed the README.'
    & (Join-Path $PSScriptRoot 'Update-ReadmeBadges.ps1') @arguments | Out-Null
    Assert-True (Get-Content $readme -Raw).Contains('Avalonia-91.2.4-') 'A changed central package version did not refresh the badge.'
    $generated = Get-Content $readme -Raw
    foreach ($invalid in @(-1, 3, 1.5)) {
        $summary.Modules[0].CoveredLines = $invalid
        $summary | ConvertTo-Json -Depth 5 | Set-Content $coverage
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Update-ReadmeBadges.ps1') @arguments }
        Assert-True ((Get-Content $readme -Raw) -ceq $generated) 'Invalid measurements changed the README.'
    }
    $summary.Modules[0].CoveredLines = 1
    $summary.Modules[0].ValidLines = 2.5
    $summary | ConvertTo-Json -Depth 5 | Set-Content $coverage
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Update-ReadmeBadges.ps1') @arguments }
    $summary.Modules = @($summary.Modules | Where-Object Module -ne 'QuoteMatch.Desktop')
    $summary | ConvertTo-Json -Depth 5 | Set-Content $coverage
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Update-ReadmeBadges.ps1') @arguments }
    Write-Output 'Automation checks passed: measured aggregate coverage, source-derived versions, idempotence, check mode and rejected invalid inputs.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $relative = [IO.Path]::GetRelativePath($temporaryBase, $resolved)
    if ($relative -cne [IO.Path]::GetFileName($resolved) -or -not $relative.StartsWith('quotematch-automation-', [StringComparison]::Ordinal)) { throw 'Invalid automation cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
