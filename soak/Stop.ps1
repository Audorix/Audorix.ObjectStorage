. (Join-Path $PSScriptRoot 'Common.ps1')

if (-not (Test-Path -LiteralPath $script:CurrentRunPath)) {
    Write-Output 'No soak run record exists.'
    return
}
$record = Get-Content -LiteralPath $script:CurrentRunPath -Raw | ConvertFrom-Json
$owned = Get-OwnedProcess $record
if (-not $owned) {
    Write-Output "Recorded controller $($record.pid) is not an owned running process. Nothing was stopped."
    return
}

if (Test-Path -LiteralPath $record.checkpointPath) {
    $checkpoint = Get-Content -LiteralPath $record.checkpointPath -Raw | ConvertFrom-Json
    if ($checkpoint.hostProcessId -and $checkpoint.hostProcessStartedAt) {
        try {
            $hostProcess = Get-Process -Id ([int]$checkpoint.hostProcessId) -ErrorAction Stop
            $recordedHostStart = [DateTimeOffset]::Parse([string]$checkpoint.hostProcessStartedAt)
            $actualHostStart = [DateTimeOffset]$hostProcess.StartTime
            if ([Math]::Abs(($actualHostStart.ToUniversalTime() - $recordedHostStart.ToUniversalTime()).TotalSeconds) -le 1) {
                Stop-Process -Id $hostProcess.Id -Force
                $hostProcess.WaitForExit(10000) | Out-Null
            }
        } catch { }
    }
}
Stop-Process -Id $owned.Id -Force
$owned.WaitForExit(10000) | Out-Null
Write-Output "Stopped owned soak run $($record.runId)."
