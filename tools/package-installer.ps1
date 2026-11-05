#Requires -Version 7.0
# Build an offline, current-user installer from the output of tools/package-release.ps1.
# -VerifyOnly compiles against an existing verified release and discards the test installer.
[CmdletBinding()]
param(
    [string]$PackageReport = 'artifacts/release-package.json',
    [string]$MakeNsis = 'makensis.exe',
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function WorkspaceFile([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path, $root)
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Outside the workspace: $full" }
    for ($p = $full; $p; $p = [IO.Path]::GetDirectoryName($p)) {
        try {
            if (([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked path: $p" }
        } catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] { }
        if ($p -eq $root) { break }
    }
    return $full
}

$report = Get-Content -Raw -LiteralPath (WorkspaceFile $PackageReport) | ConvertFrom-Json
if ($report.success -ne $true -or $report.sha256 -notmatch '^[0-9a-f]{64}$' -or
    $report.archive -cnotmatch '^Mugonzei-Honyakuki-win-x64-v(?<version>(?<numeric>\d+\.\d+\.\d+)(?:-[A-Za-z0-9.-]+)?)\.zip$') {
    throw 'A successful verified Windows release report is required.'
}
$version = $Matches.version
$fileVersion = $Matches.numeric + '.0'
[xml]$project = Get-Content -LiteralPath (WorkspaceFile 'SteamVrTranslator.csproj') -Raw
if (-not $VerifyOnly -and $version -ne [string]$project.Project.PropertyGroup.Version) { throw 'Release ZIP and application version differ.' }
foreach ($flag in 'entryCountMatchesSource','entryNamesAndLengthsMatchSource','excludedFilesAbsent','criticalHashesMatchSource','criticalSourcesUnchanged','sourceMetadataUnchangedDuringCopy') {
    if ($report.verification.$flag -ne $true) { throw "Incomplete release verification: $flag" }
}
$archive = WorkspaceFile ('dist/' + $report.archive)
if ($archive -ne (WorkspaceFile $report.archivePath)) { throw 'Report path does not match the release ZIP.' }
$output = WorkspaceFile ("dist/Mugonzei-Honyakuki-Setup-win-x64-v$version.exe")
$checksum = WorkspaceFile ($output + '.sha256')
$compiler = (Get-Command $MakeNsis -CommandType Application -ErrorAction Stop).Source
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$source = WorkspaceFile 'tools/installer/Payload.cs'
$recipe = WorkspaceFile 'tools/installer/Mugonzei.nsi'
$icon = WorkspaceFile 'assets/mugonzei.ico'
$work = WorkspaceFile ('artifacts/installer-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($work)
$helper = Join-Path $work 'payload.exe'
$temporary = Join-Path $work ([IO.Path]::GetFileName($output))
$temporaryChecksum = Join-Path $work 'setup.sha256'
# Keep the exact verified input locked against replacement until makensis finishes.
$locked = [IO.File]::Open($archive, 'Open', 'Read', 'Read')
try {
    if ($locked.Length -ne $report.bytes -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($locked)) -ne $report.sha256) {
        throw 'Release ZIP size or SHA-256 differs from its verification report.'
    }
    $locked.Position = 0
    $zip = [IO.Compression.ZipArchive]::new($locked, 'Read', $true)
    try {
        if ($zip.Entries.Count -ne $report.entries) { throw 'Release ZIP entry count differs.' }
        $installedKB = [math]::Ceiling(($zip.Entries | Measure-Object Length -Sum).Sum / 1KB)
        $base = 'Mugonzei-Honyakuki/local-ocr-runtime/'
        $reader = [IO.StreamReader]::new($zip.GetEntry($base + 'runtime-manifest.json').Open())
        try { $ocr = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($ocr.ready -ne $true -or $ocr.models.Count -lt 1) { throw 'Offline OCR model manifest is incomplete.' }
        foreach ($model in $ocr.models) {
            $entry = $zip.GetEntry($base + 'ndlocr-lite/src/model/' + $model.name)
            if (-not $entry -or $entry.Length -ne $model.bytes) { throw "Missing or incomplete OCR model: $($model.name)" }
            $stream = $entry.Open()
            try {
                if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) -ne $model.sha256) { throw 'OCR model SHA-256 differs from the manifest.' }
            } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    & $csc /nologo /optimize+ /platform:x64 /target:exe /reference:System.IO.Compression.dll "/out:$helper" $source
    if ($LASTEXITCODE) { throw 'Could not compile the payload helper using the Windows .NET Framework compiler.' }
    & $helper verify $archive $report.sha256
    if ($LASTEXITCODE) { throw 'Invalid installer payload.' }
    & $compiler /V2 /INPUTCHARSET UTF8 "/DOUTPUT=$temporary" "/DPRODUCT_ICON=$icon" "/DHELPER=$helper" "/DARCHIVE=$archive" "/DDIGEST=$($report.sha256)" "/DINSTALLED_KB=$installedKB" "/DVERSION=$version" "/DFILEVERSION=$fileVersion" $recipe
    if ($LASTEXITCODE -or -not [IO.File]::Exists($temporary)) { throw 'NSIS compilation failed.' }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporary).Hash.ToLowerInvariant()
    if ($VerifyOnly) { Write-Host "Verified NSIS build against $($report.archive)"; return }
    [IO.File]::WriteAllText($temporaryChecksum, "$hash  $([IO.Path]::GetFileName($output))`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $output, $true)
    [IO.File]::Move($temporaryChecksum, $checksum, $true)
    Write-Host "Offline installer: $output"
    Write-Host "SHA-256: $hash"
} finally {
    $locked.Dispose()
    foreach ($file in $helper,$temporary,$temporaryChecksum) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($work)
}
