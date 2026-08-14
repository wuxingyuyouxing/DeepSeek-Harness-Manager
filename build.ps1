# build.ps1 — 一键构建 DeepSeek-Harness-Manager.exe（含便携 Node 准备）
# 依赖：Windows 自带 .NET Framework 4.8 编译器（csc.exe），无需安装任何 SDK。
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1. 准备便携 Node（runtime\node），缺失时自动下载官方便携版（需联网一次）
$nodeExe = Join-Path $root 'runtime\node\node.exe'
if (-not (Test-Path $nodeExe)) {
    Write-Host '未找到 runtime\node，正在下载官方便携版 Node.js（约 34MB，需联网）…'
    $nodeZip = Join-Path $root 'runtime\node-portable.zip'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'runtime') | Out-Null
    Invoke-WebRequest -Uri 'https://nodejs.org/dist/latest-v22.x/node-v22.23.2-win-x64.zip' -OutFile $nodeZip -UseBasicParsing
    Expand-Archive -Path $nodeZip -DestinationPath (Join-Path $root 'runtime\node') -Force
    $inner = Get-ChildItem (Join-Path $root 'runtime\node') -Directory | Select-Object -First 1
    if ($inner -and $inner.Name -like 'node-v*') {
        Get-ChildItem $inner.FullName | Move-Item -Destination (Join-Path $root 'runtime\node') -Force
        Remove-Item $inner.FullName -Recurse -Force
    }
    Remove-Item $nodeZip -Force
    Write-Host '便携 Node 就绪。'
}

# 2. 编译主程序
& $csc /nologo /target:winexe /optimize+ `
    /out:"$root\DeepSeek-Harness-Manager.exe" `
    /win32icon:"$root\DeepSeek-Harness.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll `
    /r:System.Management.dll `
    "$root\Manager.cs"

if ($LASTEXITCODE -eq 0) {
    Write-Host "构建成功：$root\DeepSeek-Harness-Manager.exe"
    Write-Host '打包说明：dist\ 下的安装版与便携版由发布流程生成（见 README「打包分享」）。'
} else {
    Write-Host "构建失败，退出码 $LASTEXITCODE"
}
