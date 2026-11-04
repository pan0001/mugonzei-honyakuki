param(
    [string]$RuntimeDirectory = (Join-Path $PSScriptRoot 'local-ocr-runtime'),
    [string]$DownloadCache = (Join-Path $PSScriptRoot '.local-ocr-cache')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Private, relocatable image-only CPU runtime. No global Python or pip install is used.
$runtimeRoot = [IO.Path]::GetFullPath($RuntimeDirectory)
$cacheRoot = [IO.Path]::GetFullPath($DownloadCache)
$commit = '6feccb29e33c2467ea48894e66502a7788ae2b2d'
$version = '1.3.1'
$pythonVersion = '3.13.16'
$pythonRoot = Join-Path $runtimeRoot 'python'
$pythonExe = Join-Path $pythonRoot 'python.exe'
$sourceRoot = Join-Path $runtimeRoot 'ndlocr-lite'
$siteRoot = Join-Path $pythonRoot 'Lib/site-packages'
$marker = Join-Path $runtimeRoot 'runtime-manifest.json'
$workerSource = Join-Path $PSScriptRoot 'local-japanese-ocr-worker.py'

$models = @(
    @{ name = 'deim-s-1024x1024.onnx'; bytes = 40256763; sha256 = 'C156CE0C4E704BC3BF7E4016D0A87B949CFFA8B3724F4B4CC696B8284C3C7373' },
    @{ name = 'parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx'; bytes = 36457393; sha256 = '9E651BAE4C1A4D5254DA1127E86E82E21EF62D5339B37E62D4A3D3D30831772D' },
    @{ name = 'parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx'; bytes = 37808553; sha256 = '49CEA9DB4552F19EB05C8EE202FCF74714977749B2F4C9376B127FDE41B07A99' },
    @{ name = 'parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx'; bytes = 42588187; sha256 = '06462B0DBD5B0B8508545C8C3D485CF20DBF4FFA652FE145E69C9E7457080602' }
)
# Only imports required for image OCR; GUI, PDF, training and CUDA packages are omitted.
# PyYAML 6.0.2 supplies CPython 3.13 wheels (the upstream 6.0.1 pin does not).
$requirements = @(
    'colorama==0.4.6', 'flatbuffers==25.12.19', 'lxml==5.4.0', 'networkx==3.3',
    'numpy==2.2.2', 'onnxruntime==1.26.0', 'opencv-python-headless==4.11.0.86',
    'packaging==26.3', 'pillow==12.1.1', 'protobuf==7.36.2', 'PyYAML==6.0.2', 'tqdm==4.66.4'
)

function Get-VerifiedDownload([string]$Url, [string]$Destination, [string]$Sha256) {
    if ((Test-Path -LiteralPath $Destination -PathType Leaf) -and
        (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256) { return }
    $partial = $Destination + '.part'
    Write-Host ('Downloading ' + [IO.Path]::GetFileName($Destination))
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial -TimeoutSec 1800
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Sha256) {
        throw ('Download verification failed: ' + [IO.Path]::GetFileName($Destination))
    }
    Move-Item -LiteralPath $partial -Destination $Destination -Force
}

function Expand-ContainedZip([string]$ArchivePath, [string]$Destination, [string]$StripPrefix = '') {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName
            if ($StripPrefix) {
                if (-not $relative.StartsWith($StripPrefix, [StringComparison]::Ordinal)) { throw 'Unexpected source archive prefix.' }
                $relative = $relative.Substring($StripPrefix.Length)
            }
            if ([string]::IsNullOrEmpty($relative)) { continue }
            $output = [IO.Path]::GetFullPath((Join-Path $Destination $relative))
            if (-not $output.StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escapes runtime directory.' }
            if ($entry.FullName.EndsWith('/')) {
                New-Item -ItemType Directory -Path $output -Force | Out-Null
            } else {
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($output)) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $output, $true)
            }
        }
    } finally { $archive.Dispose() }
}

try {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'The local OCR runtime requires 64-bit Windows.' }
    if (-not (Test-Path -LiteralPath $workerSource -PathType Leaf)) { throw 'local-japanese-ocr-worker.py is missing beside this installer.' }
    New-Item -ItemType Directory -Path $runtimeRoot, $cacheRoot, $siteRoot -Force | Out-Null
    # A marker only describes a completed installation; a failed repair leaves it absent.
    if (Test-Path -LiteralPath $marker -PathType Leaf) { Remove-Item -LiteralPath $marker -Force }

    $sourceArchive = Join-Path $cacheRoot ('ndlocr-lite-' + $commit + '.zip')
    Get-VerifiedDownload ('https://codeload.github.com/ndl-lab/ndlocr-lite/zip/' + $commit) $sourceArchive 'B588A8A68507885570D53CBFB32D75B1507F5EEAF6246841027D1A79CB83B537'
    Expand-ContainedZip $sourceArchive $sourceRoot ('ndlocr-lite-' + $commit + '/')

    $pythonArchive = Join-Path $cacheRoot ('python-' + $pythonVersion + '-embed-amd64.zip')
    Get-VerifiedDownload ('https://www.python.org/ftp/python/' + $pythonVersion + '/python-' + $pythonVersion + '-embed-amd64.zip') $pythonArchive '97DAE5274CC54867065E8D5A3226E48C35017ED332A0FDB0E27D5B5821961297'
    Expand-ContainedZip $pythonArchive $pythonRoot
    [IO.File]::WriteAllText((Join-Path $pythonRoot 'python313._pth'), "python313.zip`n.`nLib/site-packages`nimport site`n", [Text.Encoding]::ASCII)

    $pipArchive = Join-Path $cacheRoot 'pip-26.2.2-py3-none-any.whl'
    Get-VerifiedDownload 'https://files.pythonhosted.org/packages/3f/06/3e4420302363fbb71be3c7f35ce042f54bfca330e3e66b3a2e3c4cca579f/pip-26.2.2-py3-none-any.whl' $pipArchive 'FD7F4DFA8190E014013B9F08F3E973F4E4C26FFD179A204895533A7254813A70'
    Expand-ContainedZip $pipArchive $siteRoot
    $lockPath = Join-Path $runtimeRoot 'requirements.lock.txt'
    [IO.File]::WriteAllLines($lockPath, $requirements, [Text.Encoding]::ASCII)
    & $pythonExe -I -X utf8 -m pip install --disable-pip-version-check --no-warn-script-location --only-binary=:all: --no-deps --upgrade --cache-dir (Join-Path $cacheRoot 'pip') --target $siteRoot -r $lockPath
    if ($LASTEXITCODE -ne 0) { throw 'Private CPU dependency installation failed; check the network and rerun this script.' }
    & $pythonExe -I -X utf8 -m pip check
    if ($LASTEXITCODE -ne 0) { throw 'Private dependency validation failed.' }

    $licenseRoot = Join-Path $runtimeRoot 'licenses'
    New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
    $dinoUrl = 'https://raw.githubusercontent.com/facebookresearch/dinov3/6876159a11b4df116f30f667f8c9888617df0751/LICENSE.md'
    Get-VerifiedDownload $dinoUrl (Join-Path $licenseRoot 'DINOv3-LICENSE.md') '25D122EB8F5B880FD23C736FB6EA8018EE45C12237E00B8A86D14C653904999E'
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENCE') -Destination (Join-Path $licenseRoot 'NDLOCR-Lite-LICENCE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENCE_DEPENDENCEIES') -Destination (Join-Path $licenseRoot 'NDLOCR-Lite-DEPENDENCIES.txt') -Force
    Copy-Item -LiteralPath (Join-Path $pythonRoot 'LICENSE.txt') -Destination (Join-Path $licenseRoot 'Python-LICENSE.txt') -Force
    Copy-Item -LiteralPath $workerSource -Destination (Join-Path $runtimeRoot 'local-japanese-ocr-worker.py') -Force

    foreach ($model in $models) {
        $modelPath = Join-Path $sourceRoot ('src/model/' + $model.name)
        if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf) -or
            (Get-Item -LiteralPath $modelPath).Length -ne $model.bytes -or
            (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash -ne $model.sha256) {
            throw ('Local OCR model verification failed: ' + $model.name)
        }
    }
    # -I avoids user site-packages/PYTHONPATH; -X utf8 also covers upstream YAML reads.
    $checkCode = 'import sys; from pathlib import Path; sys.path.insert(0,str(Path(sys.argv[1]).resolve())); import ocr,onnxruntime; assert sys.version_info[:3]==(3,13,16); assert "CPUExecutionProvider" in onnxruntime.get_available_providers(); print("Private OCR imports: OK (CPU)")'
    & $pythonExe -I -X utf8 -c $checkCode (Join-Path $sourceRoot 'src')
    if ($LASTEXITCODE -ne 0) { throw 'Local OCR import check failed; runtime is not marked ready.' }
    $manifest = [ordered]@{
        schemaVersion = 1; ready = $true; engine = 'NDLOCR-Lite'; version = $version; commit = $commit
        sourceUrl = ('https://github.com/ndl-lab/ndlocr-lite/tree/' + $commit)
        sourceArchiveSha256 = 'B588A8A68507885570D53CBFB32D75B1507F5EEAF6246841027D1A79CB83B537'
        pythonVersion = $pythonVersion; pythonExecutable = 'python/python.exe'
        pythonSource = ('https://www.python.org/downloads/release/python-' + $pythonVersion.Replace('.', '') + '/')
        pythonArchiveSha256 = '97DAE5274CC54867065E8D5A3226E48C35017ED332A0FDB0E27D5B5821961297'
        worker = 'local-japanese-ocr-worker.py'; upstreamCli = 'ndlocr-lite/src/ocr.py'
        device = 'cpu'; imageOnly = $true; modelBytes = 157110896; models = $models
        dependencies = $requirements; installerPip = '26.2.2'
        licenses = @('licenses/NDLOCR-Lite-LICENCE.txt', 'licenses/NDLOCR-Lite-DEPENDENCIES.txt', 'licenses/DINOv3-LICENSE.md', 'licenses/Python-LICENSE.txt', 'python/Lib/site-packages/*dist-info/')
        dinoLicenseSource = $dinoUrl
    }
    $json = $manifest | ConvertTo-Json -Depth 8
    $temporaryMarker = $marker + '.tmp'
    [IO.File]::WriteAllText($temporaryMarker, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporaryMarker -Destination $marker -Force
    Write-Host ('Local Japanese OCR ready: ' + $runtimeRoot)
} catch {
    throw ('Local Japanese OCR setup failed: ' + $_.Exception.Message + ' Fix the reported cause and rerun tools/setup-local-ocr.ps1. No global Python packages were changed.')
}
