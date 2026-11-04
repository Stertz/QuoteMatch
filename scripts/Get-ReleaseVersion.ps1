#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$RunNumber,
    [string]$ProjectPath = (Join-Path $PSScriptRoot '../src/QuoteMatch.Desktop/QuoteMatch.Desktop.csproj')
)

$ErrorActionPreference = 'Stop'
$prefix = (& dotnet msbuild $ProjectPath -getProperty:VersionPrefix -nologo).Trim()
if ($LASTEXITCODE -ne 0 -or $prefix -notmatch '^\d+\.\d+\.\d+$') { throw 'The project must define one numeric VersionPrefix.' }
$baseVersion = [version]$prefix
$patch = [long]$baseVersion.Build + $RunNumber - 1
$version = "$($baseVersion.Major).$($baseVersion.Minor).$patch"
Write-Output $version
if ($env:GITHUB_OUTPUT) {
    "version=$version`ntag=v$version" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
}
