# release.ps1 — 一键发布 DeepSeek Harness 管理器
# 流程：编译 → 打包(便携版+安装版) → 签名 → 提交/打tag/推送 → 创建 GitHub Release 并上传两个安装包
# 凭据：优先 $env:GITHUB_TOKEN，否则复用 Git Credential Manager 已存的 GitHub 令牌（先 push 过即可）
# 用法：
#   powershell -ExecutionPolicy Bypass -File release.ps1            # 完整发布（版本号从 Manager.cs 读取）
#   powershell -ExecutionPolicy Bypass -File release.ps1 -DryRun    # 只构建+打包+签名+验证，不发布
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Draft     # 创建草稿（不公开）
param(
    [string]$Version = "",        # 版本号，默认从 Manager.cs 的 AssemblyVersion 读取
    [string]$Repo = "wuxingyuyouxing/DeepSeek-Harness-Manager",
    [switch]$DryRun,              # 只构建打包签名验证，不提交/推送/发布
    [switch]$Draft,               # Release 以草稿形式创建
    [string]$Notes = ""           # Release 说明，默认取 CHANGELOG.md 顶部小节
)
$ErrorActionPreference = 'Stop'
# PowerShell 7+ 下 git 把正常进度信息写到 stderr，配合 $ErrorActionPreference='Stop'
# 会被误判为异常并中断脚本（本次发布就因此在 push 后中断）。显式关闭该行为。
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '未找到 csc.exe（需要 .NET Framework 4.8）' }

function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok([string]$m)   { Write-Host "    OK: $m" -ForegroundColor Green }
function Warn([string]$m) { Write-Host "    注意: $m" -ForegroundColor Yellow }

# ── 1. 版本号（默认读取 Manager.cs）────────────────────────────────────
if (-not $Version) {
    $m = Select-String -Path "$root\Manager.cs" -Pattern 'AssemblyVersion\("([^"]+)"\)' | Select-Object -First 1
    if ($m) { $Version = $m.Matches[0].Groups[1].Value }
    if (-not $Version) { throw '无法从 Manager.cs 读取版本号' }
}
$parts = $Version -split '\.'
$tagVer = ($parts[0..([math]::Min(2, $parts.Count - 1))] -join '.')
$tag = "v$tagVer"
Step "版本：$Version  →  tag：$tag"

# ── 2. 便携 Node 准备 ──────────────────────────────────────────────────
$nodeExe = Join-Path $root 'runtime\node\node.exe'
if (-not (Test-Path $nodeExe)) {
    Step '未找到 runtime\node，下载官方便携版 Node.js（约 34MB，需联网）…'
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
    Ok '便携 Node 就绪'
}

# ── 3. 编译主程序 + 签名 ───────────────────────────────────────────────
Step '编译主程序…'
# /codepage:65001：源码为 UTF-8 无 BOM，强制按 UTF-8 读取避免中文乱码
& $csc /nologo /target:winexe /optimize+ /codepage:65001 `
    /out:"$root\DeepSeek-Harness-Manager.exe" `
    /win32icon:"$root\DeepSeek-Harness.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll `
    /r:System.Management.dll "$root\Manager.cs"
if ($LASTEXITCODE -ne 0) { throw '主程序编译失败' }

# ── 4. 打包便携版 zip ─────────────────────────────────────────────────
Step '打包便携版…'
$stage = Join-Path $root 'dist\_stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$inc = @('DeepSeek-Harness-Manager.exe','README.md','CHANGELOG.md','LICENSE','Manager.cs',
         'build.ps1','build-icon.ps1','DeepSeek-Harness.ico','start-dsh.vbs','start-dsh.ps1',
         'stop-dsh.ps1','docs','assets','runtime', (Join-Path $root 'dist\数字签名说明.md'))
foreach ($f in $inc) {
    $src = Join-Path $root $f
    if (-not (Test-Path $src)) { continue }
    Copy-Item $src (Join-Path $stage (Split-Path $f -Leaf)) -Recurse -Force
}
$portable = Join-Path $root "dist\DeepSeek-Harness-Manager-Portable-$tag.zip"
if (Test-Path $portable) { Remove-Item $portable -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $portable -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force
Ok "便携版：$([math]::Round((Get-Item $portable).Length/1MB,1)) MB"

# ── 5. 构建安装版（嵌入 payload）+ 签名 ────────────────────────────────
Step '构建安装版…'
$tools = Join-Path $root 'tools'
Copy-Item $portable (Join-Path $tools 'payload.zip') -Force
$setupOut = Join-Path $tools 'DeepSeek-Harness-Manager-Setup.exe'
if (Test-Path $setupOut) { Remove-Item $setupOut -Force }
& $csc /nologo /target:winexe /optimize+ /codepage:65001 `
    /out:"$setupOut" `
    /win32icon:"$tools\DeepSeek-Harness.ico" `
    /resource:"$tools\payload.zip,payload.zip" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.CSharp.dll `
    "$tools\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw '安装版编译失败' }
$setupFinal = Join-Path $root "dist\DeepSeek-Harness-Manager-Setup-$tag.exe"
Copy-Item $setupOut $setupFinal -Force
Ok "安装版：$([math]::Round((Get-Item $setupFinal).Length/1MB,1)) MB"

# ── 6. 签名 + 验证 ────────────────────────────────────────────────────
Step '签名…'
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like '*DeepSeek Harness Manager*' } | Select-Object -First 1
foreach ($f in @("$root\DeepSeek-Harness-Manager.exe", $setupFinal)) {
    if ($cert) {
        Set-AuthenticodeSignature -FilePath $f -Certificate $cert -TimestampServer 'http://timestamp.digicert.com' | Out-Null
        $sig = Get-AuthenticodeSignature $f
        Ok "$(Split-Path $f -Leaf) 签名: $($sig.SignerCertificate.Subject)"
    } else {
        Warn '未找到签名证书，跳过签名（不影响发布，仅 SmartScreen 提示更明显）'
        break
    }
}

if ($DryRun) {
    Write-Host "`n[DryRun] 构建、打包、签名完成。未提交/推送/发布。" -ForegroundColor Yellow
    Write-Host "[DryRun] 若正式发布将执行：git add/commit → git tag $tag → git push → 创建 Release($tag) 并上传两个安装包"
    exit 0
}

# ── 7. 提交 + tag + 推送 ──────────────────────────────────────────────
Step '提交并推送…'
Push-Location $root
try {
    $pending = git status --porcelain
    if ($pending) {
        git add -A
        git commit -m "Release $tag" | Out-Null
        Ok "已提交本地改动"
    } else { Ok '工作区干净，无需提交' }
    git tag $tag 2>$null
    git push origin main 2>&1 | Out-Null
    git push origin $tag 2>&1 | Out-Null
    Ok "已推送 main 与 tag $tag"
}
finally { Pop-Location }

# ── 8. 创建 Release 并上传安装包 ──────────────────────────────────────
Step '创建 GitHub Release…'
$token = $env:GITHUB_TOKEN
if (-not $token) {
    $cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    foreach ($line in ($cred -split "`n")) {
        if ($line -like 'password=*') { $token = $line.Substring(9); break }
    }
}
if (-not $token) { throw '无法获取 GitHub 令牌：请先 git push 登录一次，或设置环境变量 GITHUB_TOKEN' }

if (-not $Notes) {
    $cl = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw
    $m = [regex]::Match($cl, '## v[^\r\n]*\r?\n(.*?)(?=\r?\n## v|\z)', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($m.Success) { $Notes = $m.Groups[1].Value.Trim() }
}

$headers = @{ Authorization = "token $token"; Accept = 'application/vnd.github.v3+json' }
$body = @{ tag_name = $tag; name = $tag; body = $Notes; draft = [bool]$Draft } | ConvertTo-Json
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases" -Method Post -Headers $headers -Body $body -ContentType 'application/json'

foreach ($asset in @($portable, $setupFinal)) {
    $name = Split-Path $asset -Leaf
    $up = $rel.upload_url -replace '\{\?name,label\}', ("?name=" + [uri]::EscapeDataString($name))
    Invoke-WebRequest -Uri $up -Method Post -Headers $headers -InFile $asset -ContentType 'application/octet-stream' -TimeoutSec 600 | Out-Null
    Ok "已上传：$name"
}

Write-Host "`n发布完成：$($rel.html_url)" -ForegroundColor Green
