. (Join-Path $PSScriptRoot "Common.ps1")

$records = @(Read-OwnedProcesses)
[array]::Reverse($records)
foreach ($record in $records) {
    if (Test-OwnedProcess $record) {
        Stop-Process -Id $record.pid
        try { Wait-Process -Id $record.pid -Timeout 15 -ErrorAction Stop } catch {
            if (Test-OwnedProcess $record) { Stop-Process -Id $record.pid -Force }
        }
        Write-Host "Stopped owned $($record.name) process $($record.pid)."
    }
    else {
        Write-Host "Skipped stale or non-owned $($record.name) PID record."
    }
}
if (Test-Path -LiteralPath $script:PilotProcessesPath) { Remove-Item -LiteralPath $script:PilotProcessesPath }
