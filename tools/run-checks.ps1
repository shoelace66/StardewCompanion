[CmdletBinding()]
param([string]$GamePath='F:\SteamLibrary\steamapps\common\Stardew Valley')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$dotnet=Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $dotnet)){throw '先运行 tools/bootstrap.ps1。'}
if(Get-Process -Name 'StardewModdingAPI' -ErrorAction SilentlyContinue){throw '统一检查需要暂时没有运行中的 AI 客户端。请先正常退出 AI 游戏实例。'}
$probe=[Net.Sockets.TcpClient]::new()
try{$probe.Connect('127.0.0.1',27841);throw '27841 端口已占用；请关闭诊断服务或桌面应用后重试。'}catch [Net.Sockets.SocketException]{}finally{$probe.Dispose()}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1';$env:SC_GAME_PATH=$GamePath
$env:DOTNET_ROOT=Join-Path $repoRoot '.tools\dotnet'
$server=$null
try{
    & $dotnet build (Join-Path $repoRoot 'StardewCompanion.slnx') -c Release "-p:GamePath=$GamePath"
    if($LASTEXITCODE -ne 0){throw '统一构建失败。'}
    & $dotnet run --project (Join-Path $repoRoot 'tools\AppChecks\AppChecks.csproj') -c Release --no-build -- $repoRoot
    if($LASTEXITCODE -ne 0){throw '应用配置与生命周期检查失败。'}
    & $dotnet run --project (Join-Path $repoRoot 'tools\DesktopChecks\DesktopChecks.csproj') -c Release --no-build -- $repoRoot
    if($LASTEXITCODE -ne 0){throw '桌面启动与恢复检查失败。'}
    $serverDll=Join-Path $repoRoot 'src\StardewCompanion.Diagnostics\bin\Release\net10.0\StardewCompanion.Diagnostics.dll'
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$dotnet;$start.WorkingDirectory=$repoRoot;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.Arguments='"'+$serverDll+'"';$start.EnvironmentVariables['SC_SESSION_FILE']=Join-Path $repoRoot '.local\session.json'
    $server=[Diagnostics.Process]::Start($start)
    $sessionPath=Join-Path $repoRoot '.local\session.json';$ready=$false
    for($i=0;$i -lt 40;$i++){
        Start-Sleep -Milliseconds 250
        if(-not(Test-Path -LiteralPath $sessionPath)){continue}
        try{$session=Get-Content -LiteralPath $sessionPath -Raw|ConvertFrom-Json;$headers=@{Authorization='Bearer '+$session.token};$null=Invoke-RestMethod 'http://127.0.0.1:27841/health' -Headers $headers -TimeoutSec 1;$ready=$true;break}catch{}
    }
    if(-not $ready){throw '本机诊断服务未能就绪。'}
    & $dotnet run --project (Join-Path $repoRoot 'tools\Checks\Checks.csproj') -c Release --no-build -- $repoRoot
    if($LASTEXITCODE -ne 0){throw '游戏桥接与寻路检查失败。'}
    & (Join-Path $PSScriptRoot 'run-core-checks.ps1') -DotnetPath $dotnet -NoBuild
    Write-Host '统一自动检查全部通过。该命令不替代双开实机验收。' -ForegroundColor Green
}finally{
    if($server -and -not $server.HasExited){Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue}
}
