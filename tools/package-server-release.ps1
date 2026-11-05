#Requires -Version 7.0
# Packages an existing framework-dependent Linux x64 publish. Does not build or run it.
[CmdletBinding()]
param([string]$PublishDirectory = '')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$workspacePrefix = $workspaceRoot + [IO.Path]::DirectorySeparatorChar
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

function Assert-WorkspacePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($workspacePrefix, $pathComparison)) {
        throw ('Path must be inside the workspace: ' + $absolute)
    }
    # Check every existing ancestor, including the workspace itself. Never follow a link/junction.
    for ($cursor = $absolute; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw ('Reparse points are not permitted: ' + $cursor)
        }
    }
}

function Test-ExcludedPath([string]$RelativePath) {
    $name = $RelativePath.Replace('\', '/')
    if ($name -match '(^|/)(data|env|keys|secrets|logs|artifacts|\.git|\.codex|CreditSessions)(/|$)') { return $true }
    if ($name -match '(^|/)(\.?env(?:\..*)?|keys?(?:\.[^/]+)?|libe_sqlite3\.a|appsettings(?:\.[^/]+)?\.json|settings\.json|auth\.json|credentials\.json|key-[^/]+\.xml)$') { return $true }
    if ($name -match '\.(pdb|db(?:-(?:shm|wal|journal)|\.lock)?|sqlite3?(?:-(?:shm|wal|journal)|\.lock)?|env|key|pem|pfx|p12|snk|log)$') { return $true }
    if ($name -match '(^|/)(redemption|redeem)-codes[^/]*\.json$') { return $true }
    return $false
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Get-FileSha256([string]$Path) {
    Assert-WorkspacePath $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { return Get-StreamSha256 $stream }
    finally { $stream.Dispose() }
}

$projectPath = Join-Path $workspaceRoot 'server/Mugonzei.Server.csproj'
Assert-WorkspacePath $projectPath
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Server project version is missing or invalid.' }
$packageName = 'Mugonzei-Server-linux-x64-v' + $version
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) { $PublishDirectory = 'dist/' + $packageName }
$publishRoot = if ([IO.Path]::IsPathRooted($PublishDirectory)) { [IO.Path]::GetFullPath($PublishDirectory) }
    else { [IO.Path]::GetFullPath((Join-Path $workspaceRoot $PublishDirectory)) }
$publishRoot = $publishRoot.TrimEnd('\', '/')
$publishPrefix = $publishRoot + [IO.Path]::DirectorySeparatorChar
$archivePath = Join-Path $workspaceRoot ('dist/' + $packageName + '.zip')
$checksumPath = $archivePath + '.sha256'
$temporaryArchive = $archivePath + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
$temporaryChecksum = $checksumPath + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
foreach ($path in @($publishRoot, $archivePath, $checksumPath, $temporaryArchive, $temporaryChecksum)) {
    Assert-WorkspacePath $path
}
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) { throw 'Publish directory is missing.' }
if ($archivePath.StartsWith($publishPrefix, $pathComparison)) {
    throw 'The archive must not be inside PublishDirectory.'
}

$required = @(
    'Mugonzei.Server.dll', 'Mugonzei.Server.deps.json', 'Mugonzei.Server.runtimeconfig.json',
    'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.core.dll',
    'SQLitePCLRaw.provider.e_sqlite3.dll', 'libe_sqlite3.so',
    'README.md', 'configuration.example.txt', 'licenses/Microsoft.Data.Sqlite-LICENSE.txt',
    'deploy/README.md', 'deploy/mugonzei.service', 'deploy/server.env.example', 'deploy/build-sqlite-linux.py',
    'deploy/Caddyfile.example', 'deploy/caddy.service',
    'licenses/SQLitePCLRaw-LICENSE.txt', 'licenses/SQLite-native-LICENSE.txt'
)
$files = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
$directories = [Collections.Generic.Stack[string]]::new()
$directories.Push($publishRoot)
while ($directories.Count -gt 0) {
    $directory = $directories.Pop()
    Assert-WorkspacePath $directory
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        # Check before excluding so hidden links are also rejected without traversing them.
        Assert-WorkspacePath $item.FullName
        $relative = $item.FullName.Substring($publishPrefix.Length).Replace('\', '/')
        if (Test-ExcludedPath $relative) { continue }
        if ($item.PSIsContainer) { $directories.Push($item.FullName) }
        else {
            if ($relative -match '[\r\n]' -or $relative -eq 'manifest.sha256') { throw 'Unsafe or reserved package entry name.' }
            $files.Add($relative, $item)
        }
    }
}
foreach ($relative in $required) {
    if (-not $files.ContainsKey($relative) -or $files[$relative].Length -eq 0) { throw ('Missing or empty required file: ' + $relative) }
}
$runtime = Get-Content -LiteralPath $files['Mugonzei.Server.runtimeconfig.json'].FullName -Raw | ConvertFrom-Json -AsHashtable
$frameworks = @($runtime.runtimeOptions.frameworks)
if ($runtime.runtimeOptions.tfm -ne 'net10.0' -or $runtime.runtimeOptions.ContainsKey('includedFrameworks') -or
    @($frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' -and $_.version -match '^10\.' }).Count -ne 1 -or
    @($frameworks | Where-Object { $_.name -eq 'Microsoft.AspNetCore.App' -and $_.version -match '^10\.' }).Count -ne 1) {
    throw 'Publish must require the .NET 10 ASP.NET Core Runtime (framework-dependent).'
}
$deps = Get-Content -LiteralPath $files['Mugonzei.Server.deps.json'].FullName -Raw | ConvertFrom-Json -AsHashtable
if ($deps.runtimeTarget.name -notmatch '/linux-x64$' -or -not $deps.libraries.ContainsKey('Mugonzei.Server/' + $version)) {
    throw 'Published architecture or application version does not match the server project.'
}

$hashes = [ordered]@{}
foreach ($relative in @($files.Keys | Sort-Object)) { $hashes[$relative] = Get-FileSha256 $files[$relative].FullName }
$manifest = (@($hashes.Keys | ForEach-Object { $hashes[$_] + '  ' + $_ }) -join "`n") + "`n"
$entryPrefix = $packageName + '/'
$utf8 = [Text.UTF8Encoding]::new($false)
try {
    Assert-WorkspacePath ([IO.Path]::GetDirectoryName($archivePath))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($archivePath)) | Out-Null
    $stream = [IO.File]::Open($temporaryArchive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true, $utf8)
        try {
            foreach ($relative in $hashes.Keys) {
                Assert-WorkspacePath $files[$relative].FullName
                $entry = $zip.CreateEntry($entryPrefix + $relative, [IO.Compression.CompressionLevel]::Optimal)
                $inputStream = [IO.File]::OpenRead($files[$relative].FullName)
                try {
                    $outputStream = $entry.Open()
                    try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
                } finally { $inputStream.Dispose() }
            }
            $entry = $zip.CreateEntry($entryPrefix + 'manifest.sha256')
            $writer = [IO.StreamWriter]::new($entry.Open(), $utf8)
            try { $writer.Write($manifest) } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }

    $stream = [IO.File]::OpenRead($temporaryArchive)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true, $utf8)
        try {
            if ($zip.Entries.Count -ne $files.Count + 1) { throw 'Archive entry count differs from source.' }
            foreach ($relative in $hashes.Keys) {
                $entry = $zip.GetEntry($entryPrefix + $relative)
                if ($null -eq $entry) { throw ('Archive is missing ' + $relative) }
                $inputStream = $entry.Open()
                try { $archivedHash = Get-StreamSha256 $inputStream } finally { $inputStream.Dispose() }
                if ($archivedHash -ne $hashes[$relative] -or (Get-FileSha256 $files[$relative].FullName) -ne $hashes[$relative]) {
                    throw ('File changed or failed archive verification: ' + $relative)
                }
            }
            $reader = [IO.StreamReader]::new($zip.GetEntry($entryPrefix + 'manifest.sha256').Open(), $utf8)
            try { if ($reader.ReadToEnd() -cne $manifest) { throw 'Archive manifest differs from source hashes.' } }
            finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    $archiveHash = Get-FileSha256 $temporaryArchive
    [IO.File]::WriteAllText($temporaryChecksum, $archiveHash + '  ' + [IO.Path]::GetFileName($archivePath) + "`n", $utf8)
    foreach ($path in @($temporaryArchive, $temporaryChecksum, $archivePath, $checksumPath)) { Assert-WorkspacePath $path }
    [IO.File]::Move($temporaryArchive, $archivePath, $true)
    [IO.File]::Move($temporaryChecksum, $checksumPath, $true)
} finally {
    foreach ($path in @($temporaryArchive, $temporaryChecksum)) {
        Assert-WorkspacePath $path
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
}
Write-Host ('Verified server package: ' + $archivePath)
Write-Host ('SHA256: ' + $archiveHash)
Write-Host 'Requires the .NET 10 ASP.NET Core Runtime on Linux x64.'
