# build.ps1 — 一键构建 DeepSeek-Harness-Manager.exe（含便携 Node 准备）
# 依赖：Windows 自带 .NET Framework 4.8 编译器（csc.exe），无需安装任何 SDK。
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '未找到 csc.exe（需要 .NET Framework 4.8）' }
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1. 准备便携 Node（runtime\node），缺失时自动下载官方便携版（需联网一次）
$nodeExe = Join-Path $root 'runtime\node\node.exe'
if (-not (Test-Path $nodeExe)) {
    Write-Host '未找到 runtime\node，正在下载官方便携版 Node.js（约 34MB，需联网）…'
    $nodeZip = Join-Path $root 'runtime\node-portable.zip'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'runtime') | Out-Null
    # 从 latest-v22.x 目录动态解析最新版本号，避免 URL 钉死旧版本导致 404
    $idx = Invoke-WebRequest -Uri 'https://nodejs.org/dist/latest-v22.x/' -UseBasicParsing -TimeoutSec 30
    $m = [regex]::Match($idx.Content, 'node-v(\d+\.\d+\.\d+)-win-x64\.zip')
    if (-not $m.Success) { throw '无法解析 Node.js 最新版本号' }
    $ver = $m.Groups[1].Value
    Write-Host "下载 Node.js v$ver ..."
    Invoke-WebRequest -Uri "https://nodejs.org/dist/latest-v22.x/node-v$ver-win-x64.zip" -OutFile $nodeZip -UseBasicParsing -TimeoutSec 300
    # SHA256 校验（供应链防护）
    $shas = Invoke-WebRequest -Uri 'https://nodejs.org/dist/latest-v22.x/SHASUMS256.txt' -UseBasicParsing -TimeoutSec 30
    $line = (($shas.Content -split "`r?`n") | Where-Object { $_ -match "node-v$([regex]::Escape($ver))-win-x64\.zip\s*$" } | Select-Object -First 1)
    if ($line) {
        $want = (($line -split '\s+')[0]).ToLowerInvariant()
        $got = (Get-FileHash $nodeZip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($got -ne $want) { throw "Node.js 压缩包 SHA256 校验失败（期望 $want，实际 $got）" }
        Write-Host 'SHA256 校验通过。'
    } else { throw "SHASUMS256.txt 中未找到 node-v$ver-win-x64.zip 的校验条目（供应链防护：拒绝安装未校验的压缩包）" }
    Expand-Archive -Path $nodeZip -DestinationPath (Join-Path $root 'runtime\node') -Force
    $inner = Get-ChildItem (Join-Path $root 'runtime\node') -Directory | Select-Object -First 1
    if ($inner -and $inner.Name -like 'node-v*') {
        Get-ChildItem $inner.FullName | Move-Item -Destination (Join-Path $root 'runtime\node') -Force
        Remove-Item $inner.FullName -Recurse -Force
    }
    Remove-Item $nodeZip -Force
    if (-not (Test-Path $nodeExe)) { throw '便携 Node 准备失败：解压后未找到 runtime\node\node.exe' }
    Write-Host '便携 Node 就绪。'
}

# 2. 编译主程序
# /codepage:65001 强制按 UTF-8 读取源码（Manager.cs 为 UTF-8 无 BOM），
# 否则在 GBK 代码页系统上中文注释/字符串会乱码，编译产物中文显示异常。
& $csc /nologo /target:winexe /optimize+ /codepage:65001 `
    /out:"$root\DeepSeek-Harness-Manager.exe" `
    /win32icon:"$root\DeepSeek-Harness.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll `
    /r:System.Management.dll /r:System.IO.Compression.dll `
    "$root\Manager.cs"

if ($LASTEXITCODE -eq 0) {
    Write-Host "构建成功：$root\DeepSeek-Harness-Manager.exe"
    Write-Host '打包说明：dist\ 下的安装版与便携版由发布流程生成（见 README「打包分享」）。'
    exit 0
} else {
    Write-Host "构建失败，退出码 $LASTEXITCODE"
    exit 1   # 显式非零退出码：CI/自动化不会误判成功
}
