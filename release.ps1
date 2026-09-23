# release.ps1 — 一键发布 DeepSeek Harness 管理器
# 流程：编译 → 打包(便携版+安装版) → 签名 → 提交/打tag/推送 → 创建 GitHub Release 并上传两个安装包
# 凭据：优先 $env:GITHUB_TOKEN，否则复用 Git Credential Manager 已存的 GitHub 令牌（先 push 过即可）
# 用法（本脚本为 UTF-8 带 BOM，PS 5.1 与 PS 7 均可解析）：
#   powershell -ExecutionPolicy Bypass -File release.ps1            # 完整发布（版本号从 Manager.cs 读取）
#   powershell -ExecutionPolicy Bypass -File release.ps1 -DryRun    # 只构建+打包+签名+验证，不发布
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Draft     # 创建草稿（不公开）
# 注意：以下 git 调用不使用 2>&1/2>$null 重定向——Windows PowerShell 5.1 下被重定向的
# 原生命令 stderr 会变成**终止性** RemoteException（$PSNativeCommandUseErrorActionPreference
# 在 5.1 不存在），会在 push 之后中断脚本，留下"已推 tag 但没有 Release"的半成品状态。
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

# ── 1.5 前置校验：README 版本信息必须已同步（防止发布时忘记更新）─────────
# 显式 -Encoding UTF8：README/CHANGELOG 是 UTF-8 无 BOM，而 Windows PowerShell 5.1 的
# Get-Content 默认按系统 ANSI(GBK) 解码 → 中文变乱码，版本校验必然误报"未同步"、
# Release 说明也会乱码。加 -Encoding UTF8 后 PS 5.1 与 PS 7 行为一致。
$readme = Get-Content (Join-Path $root 'README.md') -Raw -Encoding UTF8
$expectCur     = "当前版本：**$tag**"
$expectSetup   = "DeepSeek-Harness-Manager-Setup-$tag.exe"
$expectPortable = "DeepSeek-Harness-Manager-Portable-$tag.zip"
$bad = @()
if (-not $readme.Contains($expectCur))      { $bad += "README「当前版本」应为 '$expectCur'" }
if (-not $readme.Contains($expectSetup))    { $bad += "README「安装版文件名」应为 '$expectSetup'" }
if (-not $readme.Contains($expectPortable)) { $bad += "README「便携版文件名」应为 '$expectPortable'" }
# tools/Setup.cs 的版本号决定"添加或删除程序"里显示的 DisplayVersion，必须与主程序一致
$setup = Get-Content (Join-Path $root 'tools\Setup.cs') -Raw -Encoding UTF8
if ($setup -notmatch [regex]::Escape('AssemblyVersion("' + $Version + '")')) {
    $bad += "tools\Setup.cs 的 AssemblyVersion 应为 '$Version'"
}
# CHANGELOG 顶部小节会被当作 GitHub Release 说明原样发布，标题必须是本次 tag
$clHead = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding UTF8
if ($clHead -notmatch ('(?m)^##\s+' + [regex]::Escape($tag) + '\b')) {
    $bad += "CHANGELOG.md 顶部缺少 '## $tag' 小节（Release 说明取自此节）"
}
if ($bad.Count -gt 0) { throw "发布前置校验失败：请先同步版本信息。`n  - " + ($bad -join "`n  - ") }
Ok "版本信息已同步（$tag）：Manager.cs / tools\Setup.cs / CHANGELOG.md / README.md"

# ── 2. 便携 Node 准备 ──────────────────────────────────────────────────
$nodeExe = Join-Path $root 'runtime\node\node.exe'
if (-not (Test-Path $nodeExe)) {
    Step '未找到 runtime\node，下载官方便携版 Node.js（约 34MB，需联网）…'
    $nodeZip = Join-Path $root 'runtime\node-portable.zip'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'runtime') | Out-Null
    # 从 latest-v22.x 目录动态解析最新版本号，避免 URL 钉死旧版本导致 404
    $idx = Invoke-WebRequest -Uri 'https://nodejs.org/dist/latest-v22.x/' -UseBasicParsing -TimeoutSec 30
    $m = [regex]::Match($idx.Content, 'node-v(\d+\.\d+\.\d+)-win-x64\.zip')
    if (-not $m.Success) { throw '无法解析 Node.js 最新版本号' }
    $ver = $m.Groups[1].Value
    Invoke-WebRequest -Uri "https://nodejs.org/dist/latest-v22.x/node-v$ver-win-x64.zip" -OutFile $nodeZip -UseBasicParsing -TimeoutSec 300
    # SHA256 校验（供应链防护）
    $shas = Invoke-WebRequest -Uri 'https://nodejs.org/dist/latest-v22.x/SHASUMS256.txt' -UseBasicParsing -TimeoutSec 30
    $line = (($shas.Content -split "`r?`n") | Where-Object { $_ -match "node-v$([regex]::Escape($ver))-win-x64\.zip\s*$" } | Select-Object -First 1)
    if ($line) {
        $want = (($line -split '\s+')[0]).ToLowerInvariant()
        $got = (Get-FileHash $nodeZip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($got -ne $want) { throw "Node.js 压缩包 SHA256 校验失败（期望 $want，实际 $got）" }
        Ok "Node v$ver SHA256 校验通过"
    } else { throw "SHASUMS256.txt 中未找到 node-v$ver-win-x64.zip 的校验条目（供应链防护：拒绝安装未校验的压缩包）" }
    Expand-Archive -Path $nodeZip -DestinationPath (Join-Path $root 'runtime\node') -Force
    $inner = Get-ChildItem (Join-Path $root 'runtime\node') -Directory | Select-Object -First 1
    if ($inner -and $inner.Name -like 'node-v*') {
        Get-ChildItem $inner.FullName | Move-Item -Destination (Join-Path $root 'runtime\node') -Force
        Remove-Item $inner.FullName -Recurse -Force
    }
    Remove-Item $nodeZip -Force
    if (-not (Test-Path $nodeExe)) { throw '便携 Node 准备失败：解压后未找到 runtime\node\node.exe' }
    Ok '便携 Node 就绪'
}

# ── 3. 编译主程序 ──────────────────────────────────────────────────────
Step '编译主程序…'
# /codepage:65001：源码为 UTF-8 无 BOM，强制按 UTF-8 读取避免中文乱码
& $csc /nologo /target:winexe /optimize+ /codepage:65001 `
    /out:"$root\DeepSeek-Harness-Manager.exe" `
    /win32icon:"$root\DeepSeek-Harness.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll `
    /r:System.Management.dll /r:System.IO.Compression.dll "$root\Manager.cs"
if ($LASTEXITCODE -ne 0) { throw '主程序编译失败' }

# ── 3.5 签名主程序 exe（必须在打包之前，确保便携包/安装包内 exe 已签名）──
function Sign-File([string]$path) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -like '*DeepSeek Harness Manager*' } | Select-Object -First 1
    if (-not $cert) { Warn "未找到签名证书，跳过签名：$path"; return }
    # 时间戳必须真的盖上：自签名证书 2029 到期后，没有时间戳的签名会失效。
    # 优先 HTTPS，失败再退回 HTTP（RFC3161 时间戳令牌本身由 TSA 签名，HTTP 传输是 Authenticode 的
    # 常规做法，官方示例也多用 http://）。本机实测 https://timestamp.digicert.com 会"静默失败"——
    # 既不抛异常也不加时间戳，只按 Status 判断会漏掉，所以这里逐个服务器验证时间戳是否真的嵌入。
    $servers = @('https://timestamp.digicert.com', 'http://timestamp.digicert.com')
    $last = $null
    foreach ($ts in $servers) {
        try { Set-AuthenticodeSignature -FilePath $path -Certificate $cert -TimestampServer $ts -HashAlgorithm SHA256 -ErrorAction Stop | Out-Null }
        catch { Warn "签名失败（$ts）：$($_.Exception.Message)"; continue }
        $sig = Get-AuthenticodeSignature $path
        $last = $sig
        if ($sig.SignerCertificate -and $sig.TimeStamperCertificate) {
            Ok "$(Split-Path $path -Leaf) 已签名 + 已加盖时间戳（$ts）"
            # 自签名根证书不在受信任根存储中 → Status 恒为 UnknownError(UntrustedRoot)，属预期，不是失败
            if ($sig.Status -ne 'Valid') { Warn "  链状态 $($sig.Status)：自签名根不受信任属预期（见 dist\数字签名说明.md），签名完整性不受影响" }
            return
        }
    }
    if ($last -and $last.SignerCertificate) {
        Warn "$(Split-Path $path -Leaf) 已签名但未加盖时间戳（时间戳服务器均失败）：证书到期后签名将失效，请人工核验"
    } else {
        Warn "签名未生效：$path"
    }
}
Step '签名主程序…'
Sign-File "$root\DeepSeek-Harness-Manager.exe"

# ── 4. 打包便携版 zip（内含已签名 exe）─────────────────────────────────
Step '打包便携版…'
$stage = Join-Path $root 'dist\_stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$inc = @('DeepSeek-Harness-Manager.exe','README.md','CHANGELOG.md','LICENSE','Manager.cs',
         'build.ps1','build-icon.ps1','DeepSeek-Harness.ico','start-dsh.vbs','start-dsh.ps1',
         'stop-dsh.ps1','docs','assets','runtime','dist\数字签名说明.md')
# ⚠ 这里必须是**相对**路径：下面用 Join-Path $root $f 再拼一次，
# 写成绝对路径会在第二次拼接后变成 "D:\...\D:\...\x" 导致 Test-Path 失败、文件被静默跳过
# （历史 bug：dist\数字签名说明.md 一直没进便携包）。
foreach ($f in $inc) {
    $src = Join-Path $root $f
    if (-not (Test-Path $src)) { Warn "打包条目缺失，已跳过：$f"; continue }
    Copy-Item $src (Join-Path $stage (Split-Path $f -Leaf)) -Recurse -Force
}
$portable = Join-Path $root "dist\DeepSeek-Harness-Manager-Portable-$tag.zip"
if (Test-Path $portable) { Remove-Item $portable -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $portable -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force
Ok "便携版：$([math]::Round((Get-Item $portable).Length/1MB,1)) MB"

# ── 5. 构建安装版（嵌入 payload，内含已签名 exe）───────────────────────
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

# ── 6. 签名安装版 ──────────────────────────────────────────────────────
Step '签名安装版…'
Sign-File $setupFinal

# ── 6.5 生成 checksums.txt（管理器自更新校验用，覆盖最终产物）──────────
# 注意：`@(expr1, expr2)` 字面量在本环境会塌缩成单元素（两行被拼成一行），
# 必须先用变量承接两行，再以显式换行写入，确保每行一个 "hash  文件名"。
$checksumsFile = Join-Path $root 'dist\checksums.txt'
$csLine1 = (Get-FileHash $portable -Algorithm SHA256).Hash.ToLower() + "  " + (Split-Path $portable -Leaf)
$csLine2 = (Get-FileHash $setupFinal -Algorithm SHA256).Hash.ToLower() + "  " + (Split-Path $setupFinal -Leaf)
Set-Content -Path $checksumsFile -Value ($csLine1 + "`r`n" + $csLine2) -Encoding ASCII
Ok "校验文件：$checksumsFile"

if ($DryRun) {
    Write-Host "`n[DryRun] 构建、打包、签名完成。未提交/推送/发布。" -ForegroundColor Yellow
    Write-Host "[DryRun] 若正式发布将执行：git add/commit → git tag $tag → git push → 创建 Release($tag) 并上传两个安装包"
    exit 0
}

# ── 7. 提交 + tag + 推送 ──────────────────────────────────────────────
# ⚠ 原生命令失败不再抛错，每个 git 步骤必须显式检查 $LASTEXITCODE，否则失败仍会继续发布（状态不一致）。
# ⚠ 这里禁用 2>&1/2>$null：PS 5.1 下被重定向的原生命令 stderr 会变成终止性 RemoteException，
#    会在 push 成功后中断脚本，留下"tag 已推送但没有 Release"的半成品状态。
Step '提交并推送…'
Push-Location $root
try {
    $pending = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'git status 失败' }
    if ($pending) {
        git add -A
        if ($LASTEXITCODE -ne 0) { throw 'git add 失败' }
        git commit -m "Release $tag" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'git commit 失败（请检查 user.name/user.email 配置）' }
        Ok "已提交本地改动"
    } else { Ok '工作区干净，无需提交' }
    if (git tag --list $tag) {
        throw "tag $tag 已存在，已中止（避免重复发布同一版本）。若上次发布是半途失败，请先执行：`n" +
              "    git tag -d $tag`n    git push origin :refs/tags/$tag`n  然后重新运行本脚本。"
    }
    git tag $tag
    if ($LASTEXITCODE -ne 0) { throw 'git tag 失败' }
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if (-not $branch) { $branch = 'main' }
    git push origin $branch
    if ($LASTEXITCODE -ne 0) { throw "git push origin $branch 失败" }
    git push origin $tag
    if ($LASTEXITCODE -ne 0) { throw "git push origin $tag 失败（tag 可能已推送但 Release 未创建：重跑前先看上面的补救命令）" }
    Ok "已推送 $branch 与 tag $tag"
}
finally { Pop-Location }

# ── 8. 创建 Release 并上传安装包 ──────────────────────────────────────
Step '创建 GitHub Release…'
$token = $env:GITHUB_TOKEN
if (-not $token) {
    # 不加 2>$null：PS 5.1 下重定向的原生命令 stderr 会变成终止性异常（见脚本头部说明）
    $cred = "protocol=https`nhost=github.com`n`n" | git credential fill
    foreach ($line in ($cred -split "`n")) {
        if ($line -like 'password=*') { $token = $line.Substring(9); break }
    }
}
if (-not $token) { throw '无法获取 GitHub 令牌：请先 git push 登录一次，或设置环境变量 GITHUB_TOKEN' }

if (-not $Notes) {
    $cl = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding UTF8
    $m = [regex]::Match($cl, '## v[^\r\n]*\r?\n(.*?)(?=\r?\n## v|\z)', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($m.Success) { $Notes = $m.Groups[1].Value.Trim() }
}

$headers = @{ Authorization = "token $token"; Accept = 'application/vnd.github.v3+json' }
$body = @{ tag_name = $tag; name = $tag; body = $Notes; draft = [bool]$Draft } | ConvertTo-Json
# 显式 charset=utf-8：PS 5.1 对字符串 body 默认按非 UTF-8 编码发送，中文 Release 说明会乱码
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases" -Method Post -Headers $headers -Body $body -ContentType 'application/json; charset=utf-8'

foreach ($asset in @($portable, $setupFinal, $checksumsFile)) {
    $name = Split-Path $asset -Leaf
    $up = $rel.upload_url -replace '\{\?name,label\}', ("?name=" + [uri]::EscapeDataString($name))
    Invoke-WebRequest -Uri $up -Method Post -Headers $headers -InFile $asset -ContentType 'application/octet-stream' -TimeoutSec 600 | Out-Null
    Ok "已上传：$name"
}

Write-Host "`n发布完成：$($rel.html_url)" -ForegroundColor Green
