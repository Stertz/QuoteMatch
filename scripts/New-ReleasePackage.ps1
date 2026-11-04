#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory)][string]$ToolPath,
    [string]$OutputDirectory = 'artifacts/releases'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$native = if ($IsWindows) { "win-$architecture" } elseif ($IsMacOS) { "osx-$architecture" } else { "linux-$architecture" }
if ($native -cne $Runtime) { throw 'Package on the native operating system and architecture.' }
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh package output directory.' }
$config = Get-Content -LiteralPath (Join-Path $publish 'QuoteMatch.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $config.runtimeOptions.includedFrameworks) { throw 'Release packages must include the .NET runtime.' }
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'QuoteMatch.Desktop.dll')).ProductVersion
if ($productVersion -cne $Version -and -not $productVersion.StartsWith("$Version+", [StringComparison]::Ordinal)) { throw 'Published assembly and release versions differ.' }
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish
if (Test-Path -LiteralPath (Join-Path $root 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $publish }
@{ version = $Version; commit = $Commit; runtime = $Runtime; channel = "$Runtime-stable"; selfContained = $true; signed = $false } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'build-info.json') -Encoding utf8
$changelog = Get-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Raw
$notes = [regex]::Match($changelog, '(?sm)^## Unreleased\r?\n(?<notes>.*?)(?=^## |\z)').Groups['notes'].Value.Trim()
if ([string]::IsNullOrWhiteSpace($notes)) { throw 'Write release notes in CHANGELOG.md before packaging.' }
$notesPath = Join-Path $output 'release-notes.md'
"# QuoteMatch $Version`n`n$notes" | Set-Content -LiteralPath $notesPath -Encoding utf8
$executable = if ($IsWindows) { 'QuoteMatch.Desktop.exe' } else { 'QuoteMatch.Desktop' }
if (-not (Test-Path -LiteralPath (Join-Path $publish $executable))) { throw 'The native application host is missing.' }
$arguments = @('pack', '--packId', 'io.github.stertz.quotematch', '--packTitle', 'QuoteMatch', '--packVersion', $Version,
    '--packDir', $publish, '--mainExe', $executable, '--runtime', $Runtime, '--channel', "$Runtime-stable",
    '--outputDir', $output, '--releaseNotes', $notesPath, '--delta', 'None')
if ($IsMacOS) {
    $iconset = Join-Path $output 'QuoteMatch.iconset'
    New-Item -ItemType Directory -Path $iconset | Out-Null
    foreach ($size in @(16, 32, 128, 256, 512)) {
        foreach ($scale in @(1, 2)) {
            $suffix = if ($scale -eq 2) { '@2x' } else { '' }
            & sips -z ($size * $scale) ($size * $scale) (Join-Path $root 'src/QuoteMatch.Desktop/Assets/AppIcon.png') --out (Join-Path $iconset "icon_${size}x$size$suffix.png") | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'macOS icon conversion failed.' }
        }
    }
    $icon = Join-Path $output 'QuoteMatch.icns'
    & iconutil -c icns $iconset -o $icon
    if ($LASTEXITCODE -ne 0) { throw 'macOS icon packaging failed.' }
    $arguments += @('--icon', $icon, '--bundleId', 'io.github.stertz.quotematch')
} else {
    $iconName = if ($IsWindows) { 'AppIcon.ico' } else { 'AppIcon.png' }
    $arguments += @('--icon', (Join-Path $root "src/QuoteMatch.Desktop/Assets/$iconName"))
}
& $ToolPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
# Existing preview installations discover the normal release and adopt its stable channel after updating.
Copy-Item -LiteralPath (Join-Path $output "releases.$Runtime-stable.json") -Destination (Join-Path $output "releases.$Runtime-preview.json")
& (Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1') -PackageDirectory $output -Runtime $Runtime -Version $Version -Commit $Commit -WriteChecksums
Write-Output "Packaged $Version for $Runtime."
