# stop-dsh.ps1 — 停止 DeepSeek Harness 后台服务
# 用法：powershell -ExecutionPolicy Bypass -File stop-dsh.ps1 [-Port 3080]
param(
    [int]$Port = 3080
)

$ErrorActionPreference = 'SilentlyContinue'
$conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if (-not $conns) { "端口 $Port 没有监听中的服务。" ; exit 0 }

$killed = @()
foreach ($c in $conns) {
    $p = Get-CimInstance Win32_Process -Filter "ProcessId = $($c.OwningProcess)"
    if ($p -and $p.Name -match 'node') {
        Stop-Process -Id $p.ProcessId -Force
        $killed += "$($p.Name) (PID $($p.ProcessId))"
    } else {
        $who = if ($p) { $p.Name } else { '未知进程' }
        "端口 $Port 由 $who (PID $($c.OwningProcess)) 占用，不是 node/DSH 服务，未做处理。"
    }
}
if ($killed) { "已停止 DeepSeek Harness 服务：$($killed -join '、')" }
