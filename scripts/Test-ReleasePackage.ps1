#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [switch]$WriteChecksums
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$directory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$channel = "$Runtime-stable"
$feedPath = Join-Path $directory "releases.$channel.json"
$feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
$legacyFeedPath = Join-Path $directory "releases.$Runtime-preview.json"
if ((Get-Content -LiteralPath $feedPath -Raw) -cne (Get-Content -LiteralPath $legacyFeedPath -Raw)) { throw 'The compatibility feed differs from the stable update feed.' }
$full = @($feed.Assets | Where-Object Type -eq 'Full')
if ($full.Count -ne 1 -or $full[0].Version -cne $Version -or $full[0].PackageId -cne 'io.github.stertz.quotematch') { throw 'The update feed does not identify the expected full release.' }
$asset = $full[0]
if ([IO.Path]::GetFileName($asset.FileName) -cne $asset.FileName -or $asset.FileName -match '[\\/]') { throw 'Invalid update package filename.' }
$packagePath = Join-Path $directory $asset.FileName
$package = Get-Item -LiteralPath $packagePath
$sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
if ($package.Length -ne $asset.Size -or $sha256 -ine $asset.SHA256) { throw 'The feed checksum or package size does not match.' }
if ([string]::IsNullOrWhiteSpace($asset.NotesMarkdown)) { throw 'The update package requires release notes.' }
$installerExtension = if ($Runtime.StartsWith('win-')) { '.exe' } elseif ($Runtime.StartsWith('osx-')) { '.pkg' } else { '.AppImage' }
$installers = @(Get-ChildItem -LiteralPath $directory -File | Where-Object Extension -eq $installerExtension)
if ($installers.Count -ne 1 -or $installers[0].Length -eq 0) { throw 'The platform installer/application is missing or ambiguous.' }
function Test-PayloadMetadata($Config, $Info) {
    if (-not $Config.runtimeOptions.includedFrameworks) { throw 'The update payload is not self-contained.' }
    if ($Info.version -cne $Version -or $Info.runtime -cne $Runtime -or $Info.channel -cne $channel -or $Info.commit -cne $Commit -or $Info.selfContained -ne $true) {
        throw 'The update payload identifies a different source, runtime or channel.'
    }
}
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    function Read-PackageJson([string]$Name) {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith("/$Name", [StringComparison]::Ordinal) })
        if ($entries.Count -ne 1 -or $entries[0].Length -gt 65536) { throw "Missing, ambiguous or oversized package metadata: $Name" }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { return $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    }
    if ($Runtime.StartsWith('linux-')) {
        # Linux updates contain the complete AppImage instead of loose application files.
        $images = @($archive.Entries | Where-Object { $_.FullName.EndsWith('/io.github.stertz.quotematch.AppImage', [StringComparison]::Ordinal) })
        if ($images.Count -ne 1) { throw 'The Linux update does not contain one AppImage.' }
        $stream = $images[0].Open(); $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $imageHash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') } finally { $stream.Dispose(); $algorithm.Dispose() }
        if ($imageHash -ine (Get-FileHash -LiteralPath $installers[0].FullName -Algorithm SHA256).Hash) { throw 'The downloadable AppImage differs from the update payload.' }
        if (-not $IsLinux -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') { throw 'Inspect the Linux AppImage on its native x64 runner.' }
        $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $temporary = Join-Path $tempBase "quotematch-appimage-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $temporary | Out-Null
        try {
            & chmod +x $installers[0].FullName
            if ($LASTEXITCODE -ne 0) { throw 'Cannot make the AppImage executable for inspection.' }
            $process = Start-Process -FilePath $installers[0].FullName -ArgumentList '--appimage-extract' -WorkingDirectory $temporary -PassThru -RedirectStandardOutput (Join-Path $temporary 'extract.log') -RedirectStandardError (Join-Path $temporary 'extract-error.log')
            try {
                if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'AppImage inspection timed out.' }
                if ($process.ExitCode -ne 0) { throw 'AppImage extraction failed.' }
            } finally { $process.Dispose() }
            $payload = Join-Path $temporary 'squashfs-root/usr/bin'
            $config = Get-Content -LiteralPath (Join-Path $payload 'QuoteMatch.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
            $info = Get-Content -LiteralPath (Join-Path $payload 'build-info.json') -Raw | ConvertFrom-Json
            Test-PayloadMetadata $config $info
            foreach ($name in @('QuoteMatch.Desktop', 'QuoteMatch.Desktop.dll', 'Velopack.dll')) {
                if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw "Missing Linux application payload: $name" }
            }
        } finally {
            $resolved = [IO.Path]::GetFullPath($temporary)
            $relative = [IO.Path]::GetRelativePath($tempBase, $resolved)
            if ($relative -cne [IO.Path]::GetFileName($resolved) -or -not $relative.StartsWith('quotematch-appimage-', [StringComparison]::Ordinal)) { throw 'Invalid AppImage cleanup path.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    } else {
        Test-PayloadMetadata (Read-PackageJson 'QuoteMatch.Desktop.runtimeconfig.json') (Read-PackageJson 'build-info.json')
        $hostName = if ($Runtime.StartsWith('win-')) { 'QuoteMatch.Desktop.exe' } else { 'QuoteMatch.Desktop' }
        foreach ($name in @($hostName, 'QuoteMatch.Desktop.dll', 'Velopack.dll')) {
            if (@($archive.Entries | Where-Object { $_.FullName.EndsWith("/$name", [StringComparison]::Ordinal) -and $_.Length -gt 0 }).Count -ne 1) { throw "Missing or ambiguous application payload: $name" }
        }
    }
} finally { $archive.Dispose() }
$files = @((Get-Item -LiteralPath $feedPath), (Get-Item -LiteralPath $legacyFeedPath), $package, $installers[0])
$portable = @(Get-ChildItem -LiteralPath $directory -Filter '*.zip' -File)
if ($portable.Count -gt 1) { throw 'Ambiguous portable application archive.' }
$files += $portable
$checksumPath = Join-Path $directory "SHA256SUMS-$Runtime.txt"
$checksums = @($files | Sort-Object Name | ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" })
if ($WriteChecksums) { $checksums | Set-Content -LiteralPath $checksumPath -Encoding utf8 }
elseif ((Compare-Object $checksums @(Get-Content -LiteralPath $checksumPath))) { throw 'The released files differ from their checksum manifest.' }
Write-Output "Validated $Runtime update feed, full package, installer, notes and checksums."
