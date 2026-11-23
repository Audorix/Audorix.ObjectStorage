. (Join-Path $PSScriptRoot 'Common.ps1')

if (-not (Test-Path -LiteralPath $script:CurrentRunPath)) {
    Write-Output 'No soak run has been launched.'
    return
}
$record = Get-Content -LiteralPath $script:CurrentRunPath -Raw | ConvertFrom-Json
$owned = [bool](Get-OwnedProcess $record)
$checkpoint = if (Test-Path -LiteralPath $record.checkpointPath) {
    Get-Content -LiteralPath $record.checkpointPath -Raw | ConvertFrom-Json
} else { $null }
$summary = if (Test-Path -LiteralPath $record.summaryPath) {
    Get-Content -LiteralPath $record.summaryPath -Raw | ConvertFrom-Json
} else { $null }

[pscustomobject]@{
    RunId = $record.runId
    Pid = $record.pid
    Owned = $owned
    State = if ($checkpoint) { $checkpoint.state } elseif ($owned) { 'starting' } else { 'not-running' }
    Workload = if ($checkpoint) { $checkpoint.workloadDuration } else { $null }
    Operations = if ($checkpoint) { $checkpoint.counters.completedOperations } else { 0 }
    IntegrityChecks = if ($checkpoint) { $checkpoint.counters.integrityChecks } else { 0 }
    Failures = if ($checkpoint) { $checkpoint.counters.unexpectedFailures } else { 0 }
    Retries = if ($checkpoint) { $checkpoint.counters.retries } else { 0 }
    Passed = if ($summary) { $summary.passed } else { $null }
    Summary = $record.summaryPath
}
