. (Join-Path $PSScriptRoot "Common.ps1")

$configuration = Read-PilotConfiguration
$records = @(Read-OwnedProcesses)
$status = foreach ($name in @("storage", "reportstudio", "frontend")) {
    $record = $records | Where-Object name -eq $name | Select-Object -First 1
    $owned = $null -ne $record -and (Test-OwnedProcess $record)
    $url = switch ($name) {
        "storage" { [string]$configuration.storage.url + "/" }
        "reportstudio" { [string]$configuration.reportStudio.url + "/api/health" }
        "frontend" { [string]$configuration.frontend.url + "/" }
    }
    $ready = $false
    if ($owned) {
        try { $ready = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { }
    }
    [pscustomobject]@{ Name = $name; Pid = if ($owned) { $record.pid } else { $null }; Owned = $owned; Ready = $ready; Url = $url }
}
$status | Format-Table -AutoSize
if (($status | Where-Object { -not $_.Owned -or -not $_.Ready }).Count -gt 0) { exit 1 }
