param([string]$Destination = (Join-Path $PSScriptRoot '..\tessdata'))
$ErrorActionPreference = 'Stop'
$commit = '87416418657359cb625c412a48b6e1d6d41c29bd'
$baseUrl = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/$commit"
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$records = foreach ($language in @('eng','jpn','kor','chi_sim','chi_tra')) {
    $filename = "$language.traineddata"
    $target = Join-Path $Destination $filename
    Invoke-WebRequest -Uri "$baseUrl/$filename" -OutFile $target
    [pscustomobject]@{file=$filename; sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant(); source="$baseUrl/$filename"}
}
Invoke-WebRequest -Uri "$baseUrl/LICENSE" -OutFile (Join-Path $Destination 'LICENSE.txt')
[pscustomobject]@{repository='https://github.com/tesseract-ocr/tessdata_fast';commit=$commit;license='Apache-2.0';files=@($records)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Destination 'models-manifest.json') -Encoding utf8
Write-Output 'Downloaded and recorded five pinned OCR language models.'
