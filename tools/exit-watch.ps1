param([int]$ProcId, [string]$Out)
$p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
if (-not $p) { "gone" | Set-Content -LiteralPath $Out; exit }
$p.WaitForExit(3600000) | Out-Null
"exitcode=$($p.ExitCode) at $(Get-Date -Format o)" | Set-Content -LiteralPath $Out
