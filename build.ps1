param(
    [string]$DotnetPath = 'dotnet',
    [switch]$SkipTests,
    [string]$PublishDirectory = 'dist/SteamVrTranslator'
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function Copy-PublishedRuntimeLicenses([string]$PublishRoot, [string]$AssetsPath) {
    $depsPath = Join-Path $PublishRoot 'SteamVrTranslator.deps.json'
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) { throw 'Published dependency metadata is missing; cannot identify runtime licenses.' }
    if (-not (Test-Path -LiteralPath $AssetsPath -PathType Leaf)) { throw 'NuGet project.assets.json is missing; cannot locate restored runtime packages.' }
    $deps = Get-Content -Raw -LiteralPath $depsPath | ConvertFrom-Json
    $assets = Get-Content -Raw -LiteralPath $AssetsPath | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    if ($packageRoots.Count -eq 0) { throw 'NuGet project.assets.json contains no package roots.' }

    $packages = @(
        @{ Id = 'Microsoft.NETCore.App.Runtime.win-x64'; Files = @(
            @{ Source = 'LICENSE.TXT'; Destination = 'DotNetRuntime-LICENSE.txt' },
            @{ Source = 'THIRD-PARTY-NOTICES.TXT'; Destination = 'DotNetRuntime-THIRD-PARTY-NOTICES.txt' }
        ) },
        @{ Id = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Files = @(
            @{ Source = 'LICENSE'; Destination = 'WindowsDesktopRuntime-LICENSE.txt' }
        ) }
    )
    $copies = [Collections.Generic.List[object]]::new()
    $records = [Collections.Generic.List[object]]::new()
    foreach ($package in $packages) {
        $prefix = 'runtimepack.' + $package.Id + '/'
        $runtimeEntries = @($deps.libraries.PSObject.Properties | Where-Object { $_.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
        if ($runtimeEntries.Count -ne 1) { throw ('Expected one published runtime version for ' + $package.Id + '; publish self-contained win-x64 before copying licenses.') }
        $version = $runtimeEntries[0].Name.Substring($prefix.Length)
        if ($version -notmatch '^[0-9][0-9A-Za-z.+-]*$') { throw ('Invalid published runtime version for ' + $package.Id) }
        $packageDirectory = $null
        foreach ($packageRoot in $packageRoots) {
            $candidate = Join-Path $packageRoot ($package.Id.ToLowerInvariant() + '/' + $version)
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = $candidate; break }
        }
        if (-not $packageDirectory) { throw ('Restored runtime package not found: ' + $package.Id + '/' + $version + '. Restore with the same SDK/NuGet package roots and retry.') }
        $files = [Collections.Generic.List[object]]::new()
        foreach ($file in $package.Files) {
            $source = Join-Path $packageDirectory $file.Source
            if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) {
                throw ('Runtime license is missing: ' + $package.Id + '/' + $version + '/' + $file.Source)
            }
            $copies.Add(@{ Source = $source; Destination = $file.Destination })
            $files.Add([ordered]@{ sourceFile = $file.Source; file = $file.Destination; sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash })
        }
        $records.Add([ordered]@{ package = $package.Id; version = $version; files = @($files.ToArray()) })
    }
    $licenseRoot = Join-Path $PublishRoot 'licenses'
    New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
    foreach ($copy in $copies) { Copy-Item -LiteralPath $copy.Source -Destination (Join-Path $licenseRoot $copy.Destination) -Force }
    # Provenance contains package versions/hashes only, never local NuGet/user paths.
    $manifest = [ordered]@{ schemaVersion = 1; packages = @($records.ToArray()) }
    [IO.File]::WriteAllText((Join-Path $licenseRoot 'DotNetRuntime-manifest.json'),
        ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) { throw 'PublishDirectory must not be empty.' }
$publishRoot = if ([IO.Path]::IsPathRooted($PublishDirectory)) { [IO.Path]::GetFullPath($PublishDirectory) }
    else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PublishDirectory)) }
$workspacePrefix = $PSScriptRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $publishRoot.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PublishDirectory must be a child directory inside this project.'
}
$cursor = $publishRoot
while ($cursor -and $cursor.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'PublishDirectory must not traverse a junction or symbolic link.'
    }
    $cursor = [IO.Path]::GetDirectoryName($cursor)
}
if (-not (& $DotnetPath --list-sdks | Select-String '^10\.')) {
    $bundled = Join-Path $env:USERPROFILE '.codex\tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $bundled) { $DotnetPath = $bundled }
    else { throw 'Install the .NET 10 SDK or pass -DotnetPath with its dotnet.exe path.' }
}
if (-not (Test-Path -LiteralPath 'tessdata\eng.traineddata')) { & '.\tools\fetch-ocr-models.ps1' }
if (-not (Test-Path -LiteralPath 'tools\local-ocr-runtime\runtime-manifest.json')) {
    & '.\tools\setup-local-ocr.ps1'
    if (-not (Test-Path -LiteralPath 'tools\local-ocr-runtime\runtime-manifest.json')) { throw 'Local OCR runtime setup did not complete.' }
}
if (-not $SkipTests) {
    & $DotnetPath run --project server.tests\Mugonzei.Server.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Server payment/credit tests failed.' }
    & $DotnetPath run --project tests\SteamVrTranslator.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & $DotnetPath run --project tests\SteamVrTranslator.Tests.csproj -c Release --no-build -- --credits-e2e `
        (Join-Path $PSScriptRoot 'server/bin/Release/net10.0/Mugonzei.Server.dll') (Get-Command $DotnetPath).Source
    if ($LASTEXITCODE -ne 0) { throw 'Desktop/server credit integration failed.' }
}
# Pass the whole MSBuild property as one argument, including spaces/Unicode in the root.
# This affects only this publish invocation and keeps local source paths out of debug metadata.
$pathMapArgument = '-p:PathMap=' + $PSScriptRoot + '=/_/'
& $DotnetPath publish SteamVrTranslator.csproj -c Release --self-contained true -o $publishRoot $pathMapArgument
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-PublishedRuntimeLicenses $publishRoot (Join-Path $PSScriptRoot 'obj/project.assets.json')
foreach ($document in @('README.md', 'README.ja.md', 'README.en.md', 'LICENSE')) {
    $source = Join-Path $PSScriptRoot $document
    if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $publishRoot $document) -Force }
}
$serverDocs = Join-Path $publishRoot 'server'
New-Item -ItemType Directory -Path $serverDocs -Force | Out-Null
foreach ($document in @('README.md', 'configuration.example.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('server/' + $document)) -Destination (Join-Path $serverDocs $document) -Force
}
$serverDeployDocs = Join-Path $serverDocs 'deploy'
New-Item -ItemType Directory -Path $serverDeployDocs -Force | Out-Null
foreach ($document in @('README.md', 'mugonzei.service', 'server.env.example', 'build-sqlite-linux.py', 'Caddyfile.example', 'caddy.service')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('server/deploy/' + $document)) -Destination (Join-Path $serverDeployDocs $document) -Force
}
$workspaceImage = Join-Path $PSScriptRoot 'docs/images/workspace.png'
if (-not (Test-Path -LiteralPath $workspaceImage -PathType Leaf)) { throw 'README workspace image is missing: docs/images/workspace.png' }
$documentationImages = Join-Path $publishRoot 'docs/images'
New-Item -ItemType Directory -Path $documentationImages -Force | Out-Null
Copy-Item -LiteralPath $workspaceImage -Destination (Join-Path $documentationImages 'workspace.png') -Force
Write-Host ('Ready: ' + (Join-Path $publishRoot 'SteamVrTranslator.exe'))
