#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateRange(1024,65535)][int]$Port = 5197,
    [string]$DotNet = 'dotnet',
    [string]$ServerDll
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This preview launcher targets Windows.' }
if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains $Port) {
    throw "Port $Port is already in use. Choose another with -Port."
}
$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Mugonzei/AdminDemo'
$run = Join-Path $root ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run -Force
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($sid)
$acl.SetAccessRuleProtection($true, $false)
$rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule)
Set-Acl -LiteralPath $run -AclObject $acl

if ($ServerDll) {
    $ServerDll = (Resolve-Path -LiteralPath $ServerDll).Path
} else {
    $project = Join-Path $PSScriptRoot '../server/Mugonzei.Server.csproj'
    $build = Join-Path $run 'server'
    & $DotNet publish $project -c Release -o $build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Failed to publish the preview server.' }
    $ServerDll = Join-Path $build 'Mugonzei.Server.dll'
}

$passwordFile = Join-Path $run '管理口令.txt'
$token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$url = "http://127.0.0.1:$Port"
$launch = [Diagnostics.ProcessStartInfo]::new((Get-Command $DotNet -CommandType Application).Source)
$launch.UseShellExecute = $false
$launch.WorkingDirectory = $run
$launch.ArgumentList.Add($ServerDll)
foreach ($key in @($launch.Environment.Keys)) {
    if ($key -match '^(MUGONZEI_|AFDIAN_|Translation__|Kestrel__|ASPNETCORE_|DOTNET_)') {
        $null = $launch.Environment.Remove($key)
    }
}
$launch.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
$launch.Environment['ASPNETCORE_URLS'] = $url
$launch.Environment['MUGONZEI_ADMIN_TOKEN'] = $token
$launch.Environment['MUGONZEI_DATABASE'] = Join-Path $run 'demo.db'
$launch.Environment['MUGONZEI_MOCK_PAYMENTS'] = 'true'
$launch.Environment['MUGONZEI_LIVE_PAYMENTS'] = 'false'
$launch.Environment['Translation__Mock'] = 'true'
$launch.Environment['MUGONZEI_PLANS_JSON'] = ConvertTo-Json -Compress -InputObject @(
    @{ id='demo-200'; name='演示 · 200 积分'; credits=200; amountFen=500; afdianPlanId=('a' * 32) },
    @{ id='demo-500'; name='演示 · 500 积分'; credits=500; amountFen=1000; afdianPlanId=('b' * 32) }
)

[IO.File]::WriteAllText($passwordFile, $token)
try {
    Write-Host "本机演示后台  $url/admin/"
    Write-Host "本次管理口令  $passwordFile"
    $process = [Diagnostics.Process]::Start($launch)
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Demo server exited with code $($process.ExitCode)." }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
} finally {
    if (Test-Path -LiteralPath $passwordFile) { Remove-Item -LiteralPath $passwordFile }
}
