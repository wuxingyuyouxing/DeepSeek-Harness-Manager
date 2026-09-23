# stop-dsh.ps1 — 停止 DeepSeek Harness 后台服务
# 用法：powershell -ExecutionPolicy Bypass -File stop-dsh.ps1 [-Port 3080]
param(
    [int]$Port = 3080
)

$ErrorActionPreference = 'SilentlyContinue'
$conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if (-not $conns) { "端口 $Port 没有监听中的服务。" ; exit 1 } # 未停止任何服务：非零退出码，调用方不会误判为"已停止"

# 按 PID 去重（IPv4/IPv6 双监听会出现同进程多条连接）
$pids = $conns | Select-Object -ExpandProperty OwningProcess -Unique
$killed = @()
$skipped = @()
$failed = @()
foreach ($procId in $pids) {
    $p = Get-CimInstance Win32_Process -Filter "ProcessId = $procId"
    if (-not $p) { continue }
    # 只停 DSH 服务：node 进程，且命令行是 dsh 的入口（bin.js / @deepseek-ai\dsh 安装布局）。
    # 不用宽松的 'dsh' 匹配：否则从 D:\DSH\... 之类目录启动的无关 node 服务也会被误杀。
    $isDsh = ($p.Name -match '^node') -and ($p.CommandLine -match 'bin\.js' -or $p.CommandLine -match '@deepseek-ai[\\/]dsh')
    if ($isDsh) {
        # 用 taskkill /T 连子进程一起结束（dsh 会派生子进程；只杀父进程会留下孤儿进程继续占端口）
        try {
            & taskkill.exe /PID $procId /T /F | Out-Null
            if ($LASTEXITCODE -eq 0) { $killed += "$($p.Name) (PID $procId)" }
            else { $failed += "$($p.Name) (PID $procId, taskkill 退出码 $LASTEXITCODE)" }
        } catch { $failed += "$($p.Name) (PID $procId, $($_.Exception.Message))" }
    } else {
        $skipped += "$($p.Name) (PID $procId)"
    }
}
if ($failed) {
    "以下进程停止失败（可能需要管理员权限）：$($failed -join '、')"
    exit 1
}
if ($killed) {
    "已停止 DeepSeek Harness 服务：$($killed -join '、')"
    exit 0
}
if ($skipped) {
    "端口 $Port 由非 DSH 进程占用：$($skipped -join '、')，未做处理。"
    exit 1
}
"端口 $Port 未发现可停止的 DSH 服务。"
exit 1
