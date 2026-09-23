<#
  start-dsh.ps1 — DeepSeek Harness 一键启动器（核心逻辑）

  行为：
    1. 探测 http://127.0.0.1:3080 —— 若已有 DeepSeek Harness 实例在运行，直接打开浏览器；
    2. 否则在后台以隐藏窗口启动 `dsh web`（node 直接调用，输出写入 logs\）；
    3. 轮询等待服务就绪（最多 120 秒），然后打开默认浏览器。
  参数：
    -Port <n>        监听端口（默认 3080）
    -HostName <host> 绑定地址（默认 127.0.0.1）
    -NoBrowser       只启动/检测服务，不打开浏览器（供脚本调用/测试）
    -Force           端口被其他程序占用时也强制启动
#>
param(
    [int]$Port     = 3080,
    [string]$HostName = '127.0.0.1',
    [switch]$NoBrowser,
    [switch]$Force
)

# 环境变量覆盖（供隐藏启动链路/自动化使用）：DSH_LAUNCH_NOBROWSER=1 等同 -NoBrowser
if ($env:DSH_LAUNCH_NOBROWSER -eq '1') { $NoBrowser = $true }

$ErrorActionPreference = 'Stop'
$Root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$LogDir = Join-Path $Root 'logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$Stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$LogFile = Join-Path $LogDir "launcher-$Stamp.log"          # 启动器自身日志
$OutFile = Join-Path $LogDir "dsh-web-$Stamp.out.log"       # 服务 stdout
$ErrFile = Join-Path $LogDir "dsh-web-$Stamp.err.log"       # 服务 stderr
$Url     = "http://${HostName}:${Port}"

# 日志尽力而为：绝不因写日志失败而中断启动流程
function Write-Log([string]$msg) {
    try {
        $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
        Add-Content -Path $LogFile -Value $line -Encoding UTF8
    } catch { }
}

# 是否已经有一个 DSH 实例在响应。
# dsh 0.1.2-rc+ 对无 token 的根路径返回 401 "dsh web authentication required"，
# 该响应本身即证明 dsh 服务已就绪（与管理器 Probe 的判定一致）；旧版则看 __DSH_BOOT__ 标记。
# PS5.1/PS7 的对象模型不同：PS7 用 -SkipHttpErrorCheck 直接拿 401 响应（异常路径的 Response
# 已被释放读不到正文），PS5.1 只能从异常的 HttpWebResponse 读。
function Test-DshRunning {
    try {
        $status = 0; $content = ''
        if ($PSVersionTable.PSVersion.Major -ge 7) {
            $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3 -Method Get -SkipHttpErrorCheck
            $status = [int]$r.StatusCode; $content = [string]$r.Content
        } else {
            try {
                $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3 -Method Get
                $status = [int]$r.StatusCode; $content = [string]$r.Content
            } catch {
                $resp = $_.Exception.Response
                if ($resp) { $status = [int]$resp.StatusCode }
                if ($status -eq 401) {
                    try {
                        $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
                        $content = $sr.ReadToEnd(); $sr.Close()
                    } catch { }
                } else { return $false }
            }
        }
        if ($status -ge 200 -and $status -lt 400) {
            return ($content -match '__DSH_BOOT__' -or $content -match '@deepseek-ai')
        }
        if ($status -eq 401 -and $content -match 'authentication required') { return $true }
    } catch { }
    return $false
}

# 定位 node.exe（优先随包便携 Node，与管理器定位顺序一致）
function Find-NodeExe {
    # 1. 随包便携 Node
    $bundled = Join-Path $Root 'runtime\node\node.exe'
    if (Test-Path $bundled) { return $bundled }
    # 2. PATH（条目去引号，避免第三方安装器带引号的 PATH 项漏判）
    $c = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $c2 = Get-Command node -ErrorAction SilentlyContinue
    if ($c2 -and $c2.CommandType -eq 'Application') { return $c2.Source }
    foreach ($d in ($env:PATH -split ';')) {
        $d2 = if ($d) { $d.Trim('"') } else { '' }
        if ($d2) { $p = Join-Path $d2 'node.exe'; if (Test-Path $p) { return $p } }
    }
    return $null
}

# 定位 dsh 的入口 bin.js（PATH 上的 dsh.cmd → npm root -g 全局安装 → npx 缓存）
function Find-DshBinJs {
    # 1. PATH 上的 dsh 命令 → npm 安装布局
    $cmd = Get-Command 'dsh.cmd' -ErrorAction SilentlyContinue
    if ($cmd) {
        $bin = Join-Path (Split-Path $cmd.Source -Parent) '..\@deepseek-ai\dsh\lib\bin.js'
        if (Test-Path $bin) { return (Resolve-Path $bin).Path }
    }
    # 2. npm 全局安装目录（npm root -g）——管理器"一键安装 dsh"即装到这里
    $node = Find-NodeExe
    if ($node) {
        $npm = Join-Path (Split-Path $node -Parent) 'npm.cmd'
        if (Test-Path $npm) {
            # 该调用去掉 2>$null（PS 5.1 下重定向的原生命令 stderr 会变成终止性异常，导致一键启动器
            # 无提示地中断），改为临时放宽 ErrorActionPreference，npm 的 WARN/notice 不影响找路径。
            $eap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try { $globalRoot = (& $npm root -g | Select-Object -Last 1) }
            finally { $ErrorActionPreference = $eap }
            if ($globalRoot -and (Test-Path $globalRoot)) {
                $bin = Join-Path $globalRoot '@deepseek-ai\dsh\lib\bin.js'
                if (Test-Path $bin) { return (Resolve-Path $bin).Path }
            }
        }
    }
    # 3. npx 缓存（旧版安装布局，向后兼容）
    if ($env:LOCALAPPDATA) {
        $npxRoot = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
        if (Test-Path $npxRoot) {
            $dirs = Get-ChildItem $npxRoot -Directory -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending
            foreach ($d in $dirs) {
                $cand = Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\lib\bin.js'
                if (Test-Path $cand) { return (Resolve-Path $cand).Path }
            }
        }
    }
    return $null
}

Write-Log "=== DeepSeek Harness launcher (port $Port) ==="

if (Test-DshRunning) {
    Write-Log "已有实例在 $Url 运行，直接打开浏览器。"
    if (-not $NoBrowser) { Start-Process $Url | Out-Null }
    Write-Log '完成。'
    exit 0
}

# 端口被占用但不是 DSH：报错并提示
$listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($listeners -and -not $Force) {
    $pid0 = $listeners[0].OwningProcess
    $own  = Get-CimInstance Win32_Process -Filter "ProcessId = $pid0" -ErrorAction SilentlyContinue
    $who  = if ($own) { "$($own.Name) (PID $pid0)" } else { "PID $pid0" }
    Write-Log "端口被占用：$who"
    $msg = "端口 $Port 已被 $who 占用，且不是 DeepSeek Harness。`n`n可先关闭该程序，或用 -Force 强制启动。"
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($msg, 'DeepSeek Harness', 'OK', 'Exclamation') | Out-Null
    exit 1
}

$node  = Find-NodeExe
$binJs = Find-DshBinJs
if (-not $node)  { Write-Log '未找到 node.exe';  exit 1 }
if (-not $binJs) { Write-Log '未找到 dsh（@deepseek-ai/dsh）安装。'; exit 1 }

Write-Log "启动服务：node $binJs web --host $HostName --port $Port"
# 用单条命令行字符串传参（内部已引号包裹 binJs），规避 PS5.1/PS7 对 -ArgumentList 数组
# 引号策略不一致导致含空格路径传错的问题；同时不再覆盖自动变量 $args
$cmdLine = '"' + $binJs + '" web --host ' + $HostName + ' --port ' + $Port
$proc = Start-Process -FilePath $node -ArgumentList $cmdLine -WorkingDirectory $Root `
    -WindowStyle Hidden -RedirectStandardOutput $OutFile -RedirectStandardError $ErrFile -PassThru

# 轮询等待就绪（进程即退则快速失败，不空等满 120s）
$deadline = (Get-Date).AddSeconds(120)
$ready = $false
$procExited = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { $procExited = $true; break }
    if (Test-DshRunning) { $ready = $true; break }
}

if ($ready) {
    Write-Log "服务已就绪：$Url"
    if (-not $NoBrowser) { Start-Process $Url | Out-Null; Write-Log '已打开浏览器。' }
    exit 0
} else {
    # 失败/超时：不再把浏览器打开到未就绪的 URL
    Add-Type -AssemblyName System.Windows.Forms
    if ($procExited) {
        Write-Log "dsh 进程启动后立即退出（退出码 $($proc.ExitCode)）。请查看：$ErrFile"
        [System.Windows.Forms.MessageBox]::Show(
            "dsh 启动失败：进程已退出（退出码 $($proc.ExitCode)）。`n`n错误日志：$ErrFile",
            'DeepSeek Harness', 'OK', 'Error') | Out-Null
    } else {
        Write-Log '服务在 120 秒内未就绪，请查看日志。'
        [System.Windows.Forms.MessageBox]::Show(
            "DeepSeek Harness 启动超时（120 秒）。`n`n日志：$LogFile`n错误：$ErrFile",
            'DeepSeek Harness', 'OK', 'Error') | Out-Null
    }
    exit 1
}
