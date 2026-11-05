#Requires -Version 7.0
# Packages and verifies an existing self-contained publish; never starts the app.
# Run build.ps1 first so localized README files and runtime licenses are included.
[CmdletBinding()]
param(
    [string]$PublishDirectory = 'dist/SteamVrTranslator',
    [string]$ArchiveName = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd([IO.Path]::DirectorySeparatorChar)
$workspacePrefix = $workspaceRoot + [IO.Path]::DirectorySeparatorChar
if (-not (Test-Path -LiteralPath (Join-Path $workspaceRoot 'SteamVrTranslator.csproj') -PathType Leaf)) {
    throw 'Run the copy of this script located in the SteamVrTranslator workspace tools directory.'
}
if ([string]::IsNullOrWhiteSpace($ArchiveName)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $workspaceRoot 'SteamVrTranslator.csproj') -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'Project version is missing.' }
    $ArchiveName = 'Mugonzei-Honyakuki-win-x64-v' + $version + '.zip'
}
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) { throw 'PublishDirectory must not be empty.' }
if ([string]::IsNullOrWhiteSpace($ArchiveName) -or [IO.Path]::GetFileName($ArchiveName) -ne $ArchiveName -or
    $ArchiveName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or -not $ArchiveName.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ArchiveName must be a plain .zip file name without directory components.'
}
$publishRoot = if ([IO.Path]::IsPathRooted($PublishDirectory)) { [IO.Path]::GetFullPath($PublishDirectory) }
    else { [IO.Path]::GetFullPath((Join-Path $workspaceRoot $PublishDirectory)) }
$archivePath = [IO.Path]::GetFullPath((Join-Path (Join-Path $workspaceRoot 'dist') $ArchiveName))
$reportPath = [IO.Path]::GetFullPath((Join-Path $workspaceRoot 'artifacts/release-package.json'))
$temporaryArchive = $archivePath + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
$temporaryReport = $reportPath + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
$entryPrefix = 'Mugonzei-Honyakuki/'

function Assert-WorkspacePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Refusing a path outside the workspace: ' + $absolute)
    }
    # Reject junctions/symlinks, including an existing output file redirected elsewhere.
    $cursor = $absolute
    while ($cursor -and $cursor.StartsWith($workspaceRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('Refusing a reparse point in the package path: ' + $cursor)
            }
        }
        if ($cursor.Equals($workspaceRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Test-ExcludedPath([string]$RelativePath) {
    $name = $RelativePath.Replace('\', '/')
    # Generated caches and the machine-specific SteamVR registration are never shipped.
    if ($name -match '(^|/)__pycache__(/|$)' -or $name -match '\.(pyc|pyo|pdb|vrmanifest)$') { return $true }
    # Do not traverse or read accidental local user artifacts in a reused publish directory.
    if ($name -match '(^|/)(artifacts|screenshots|captures|CreditSessions|data|GoogleWebView2|[^/]+\.WebView2|\.git|\.codex)(/|$)') { return $true }
    if ($name -match '\.(db|db-shm|db-wal|sqlite|sqlite-shm|sqlite-wal)$') { return $true }
    if ($name -match '(^|/)(settings\.json|auth\.json|credentials\.json|\.env(?:\..*)?|runtime-faults\.jsonl(?:\.previous)?|[^/]*-diagnostics\.json|baseline-[^/]*\.json)$') { return $true }
    return $false
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Stream)).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Get-FileSha256([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { return Get-StreamSha256 $stream }
    finally { $stream.Dispose() }
}

function Publish-ExactFile([string]$TemporaryPath, [string]$Destination) {
    Assert-WorkspacePath $TemporaryPath
    Assert-WorkspacePath $Destination
    [IO.File]::Move($TemporaryPath, $Destination, $true)
}

foreach ($path in @($publishRoot, $archivePath, $reportPath, $temporaryArchive, $temporaryReport)) {
    Assert-WorkspacePath $path
}
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) { throw 'Publish directory is missing.' }
$publishBoundary = $publishRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
foreach ($output in @($archivePath, $reportPath, $temporaryArchive, $temporaryReport)) {
    if ($output.StartsWith($publishBoundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package and report outputs must not be inside PublishDirectory.'
    }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($archivePath)) | Out-Null

$required = @(
    'SteamVrTranslator.exe', 'SteamVrTranslator.dll', 'SteamVrTranslator.deps.json',
    'README.md', 'README.ja.md', 'README.en.md', 'docs/images/workspace.png', 'THIRD-PARTY-NOTICES.md',
    'server/README.md', 'server/configuration.example.txt',
    'server/deploy/README.md', 'server/deploy/mugonzei.service', 'server/deploy/server.env.example', 'server/deploy/build-sqlite-linux.py',
    'server/deploy/Caddyfile.example', 'server/deploy/caddy.service',
    'licenses/DotNetRuntime-LICENSE.txt', 'licenses/DotNetRuntime-THIRD-PARTY-NOTICES.txt',
    'licenses/WindowsDesktopRuntime-LICENSE.txt', 'licenses/DotNetRuntime-manifest.json',
    'assets/actions.json', 'assets/bindings_oculus_touch.json',
    'assets/bindings_knuckles.json', 'assets/bindings_vive_controller.json',
    'local-ocr-runtime/runtime-manifest.json', 'local-ocr-runtime/local-japanese-ocr-worker.py',
    'WebView2Loader.dll', 'runtimes/win-x64/native/WebView2Loader.dll',
    'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll'
)
if (Test-Path -LiteralPath (Join-Path $workspaceRoot 'LICENSE') -PathType Leaf) { $required += 'LICENSE' }
$report = [ordered]@{
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    archive = [IO.Path]::GetFileName($archivePath)
    archivePath = $archivePath
    sourceDirectory = $publishRoot
    success = $false
    bytes = 0
    entries = 0
    sha256 = $null
    excludedRuntimeCachesUserArtifactsAndBrowserProfile = $true
    excludedPaths = @()
    publishedFileHashes = [ordered]@{}
    fileVerification = @()
    verification = [ordered]@{}
}

try {
    $files = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
    $directories = [Collections.Generic.Stack[string]]::new()
    $excluded = [Collections.Generic.List[string]]::new()
    $directories.Push($publishRoot)
    $publishPrefix = $publishRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            $relative = $item.FullName.Substring($publishPrefix.Length).Replace('\', '/')
            if (Test-ExcludedPath $relative) { $excluded.Add($relative); continue }
            Assert-WorkspacePath $item.FullName
            if ($item.PSIsContainer) { $directories.Push($item.FullName) }
            else { $files.Add($relative, $item) }
        }
    }
    if ($files.Count -eq 0) { throw 'The publish directory contains no package files.' }
    $report.excludedPaths = @($excluded | Sort-Object)
    $initialHashes = @{}
    foreach ($relative in $required) {
        if (-not $files.ContainsKey($relative) -or $files[$relative].Length -eq 0) {
            throw ('A required published file is missing or empty: ' + $relative)
        }
        $initialHashes[$relative] = Get-FileSha256 $files[$relative].FullName
    }

    # Detect stale license copies if a different SDK/runtime was published over this folder.
    $deps = Get-Content -Raw -LiteralPath (Join-Path $publishRoot 'SteamVrTranslator.deps.json') | ConvertFrom-Json
    $runtimeLicenses = Get-Content -Raw -LiteralPath (Join-Path $publishRoot 'licenses/DotNetRuntime-manifest.json') | ConvertFrom-Json
    if ($runtimeLicenses.schemaVersion -ne 1 -or @($runtimeLicenses.packages).Count -ne 2) { throw 'Runtime license provenance is invalid; rerun build.ps1.' }
    $expectedLicensePackages = @{
        'Microsoft.NETCore.App.Runtime.win-x64' = @('DotNetRuntime-LICENSE.txt', 'DotNetRuntime-THIRD-PARTY-NOTICES.txt')
        'Microsoft.WindowsDesktop.App.Runtime.win-x64' = @('WindowsDesktopRuntime-LICENSE.txt')
    }
    foreach ($packageId in $expectedLicensePackages.Keys) {
        $records = @($runtimeLicenses.packages | Where-Object package -EQ $packageId)
        if ($records.Count -ne 1) { throw ('Runtime license provenance is missing ' + $packageId) }
        $record = $records[0]
        $libraryName = 'runtimepack.' + $packageId + '/' + $record.version
        if (-not $deps.libraries.PSObject.Properties[$libraryName]) { throw ('Runtime license version does not match published dependencies: ' + $packageId) }
        if (@($record.files).Count -ne $expectedLicensePackages[$packageId].Count) { throw ('Unexpected runtime license file count: ' + $packageId) }
        foreach ($licenseName in $expectedLicensePackages[$packageId]) {
            $licenseRecords = @($record.files | Where-Object file -EQ $licenseName)
            if ($licenseRecords.Count -ne 1 -or $initialHashes['licenses/' + $licenseName] -ne $licenseRecords[0].sha256) {
                throw ('Runtime license hash does not match its provenance: ' + $licenseName)
            }
        }
    }

    $copiedLengths = @{}
    $zipStream = [IO.File]::Open($temporaryArchive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $zip = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create,
            $true, [Text.UTF8Encoding]::new($false))
        try {
            foreach ($relative in @($files.Keys | Sort-Object)) {
                $file = $files[$relative]
                $originalLength = $file.Length
                $originalTime = $file.LastWriteTimeUtc
                $entry = $zip.CreateEntry($entryPrefix + $relative, [IO.Compression.CompressionLevel]::Optimal)
                if ($originalTime.Year -ge 1980 -and $originalTime.Year -le 2107) {
                    $entry.LastWriteTime = [DateTimeOffset]$originalTime
                }
                $inputStream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try {
                    $outputStream = $entry.Open()
                    try { $inputStream.CopyTo($outputStream) }
                    finally { $outputStream.Dispose() }
                }
                finally { $inputStream.Dispose() }
                $file.Refresh()
                if ($file.Length -ne $originalLength -or $file.LastWriteTimeUtc -ne $originalTime) {
                    throw ('Published file changed during packaging: ' + $relative)
                }
                $copiedLengths[$relative] = $originalLength
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $zipStream.Dispose() }

    $verification = [Collections.Generic.List[object]]::new()
    $readStream = [IO.File]::OpenRead($temporaryArchive)
    try {
        $readZip = [IO.Compression.ZipArchive]::new($readStream, [IO.Compression.ZipArchiveMode]::Read,
            $true, [Text.UTF8Encoding]::new($false))
        try {
            $report.entries = $readZip.Entries.Count
            if ($readZip.Entries.Count -ne $files.Count) { throw 'Archive/source entry counts differ.' }
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $readZip.Entries) {
                if (-not $entry.FullName.StartsWith($entryPrefix, [StringComparison]::Ordinal)) {
                    throw 'Archive entry escaped the expected top-level directory.'
                }
                $relative = $entry.FullName.Substring($entryPrefix.Length)
                if (-not $names.Add($relative) -or -not $files.ContainsKey($relative) -or
                    (Test-ExcludedPath $relative) -or $entry.Length -ne $copiedLengths[$relative]) {
                    throw ('Archive entry verification failed: ' + $relative)
                }
            }
            foreach ($relative in $required) {
                $entry = $readZip.GetEntry($entryPrefix + $relative)
                $entryStream = $entry.Open()
                try { $archiveHash = Get-StreamSha256 $entryStream }
                finally { $entryStream.Dispose() }
                $sourceHash = Get-FileSha256 $files[$relative].FullName
                $matches = $sourceHash -eq $archiveHash
                $unchanged = $sourceHash -eq $initialHashes[$relative]
                $verification.Add([ordered]@{
                    path = $relative; entry = $entry.FullName; bytes = $entry.Length
                    sourceSha256 = $sourceHash; archiveSha256 = $archiveHash
                    matchesSource = $matches; sourceUnchanged = $unchanged
                })
                $report.fileVerification = @($verification.ToArray())
                $report.publishedFileHashes[$relative] = $archiveHash
                if (-not $matches -or -not $unchanged) { throw ('Critical file hash mismatch: ' + $relative) }
            }
        }
        finally { $readZip.Dispose() }
    }
    finally { $readStream.Dispose() }

    $report.bytes = ([IO.FileInfo]::new($temporaryArchive)).Length
    $report.sha256 = Get-FileSha256 $temporaryArchive
    $report.verification = [ordered]@{
        entryCountMatchesSource = $true; entryNamesAndLengthsMatchSource = $true
        excludedFilesAbsent = $true; criticalHashesMatchSource = $true
        criticalSourcesUnchanged = $true; sourceMetadataUnchangedDuringCopy = $true
        topLevelDirectory = $entryPrefix; criticalFilesChecked = $required.Count
    }
    # Only the selected archive is replaced. Other releases and the running app are untouched.
    Publish-ExactFile $temporaryArchive $archivePath
    $report.success = $true
}
catch {
    $report['error'] = $_.Exception.Message
    throw
}
finally {
    if ([IO.File]::Exists($temporaryArchive)) {
        Assert-WorkspacePath $temporaryArchive
        [IO.File]::Delete($temporaryArchive)
    }
    Assert-WorkspacePath $reportPath
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
    try {
        [IO.File]::WriteAllText($temporaryReport, ($report | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        Publish-ExactFile $temporaryReport $reportPath
    }
    finally {
        if ([IO.File]::Exists($temporaryReport)) {
            Assert-WorkspacePath $temporaryReport
            [IO.File]::Delete($temporaryReport)
        }
    }
}

Write-Host ('Verified package: ' + $archivePath)
Write-Host ('Entries: ' + $report.entries + '; bytes: ' + $report.bytes + '; SHA256: ' + $report.sha256)
Write-Host ('Verification report: ' + $reportPath)
