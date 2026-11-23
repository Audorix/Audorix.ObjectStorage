. (Join-Path $PSScriptRoot 'Common.ps1')
$ownership = Join-Path $State 'ownership.json'
if (-not (Test-Path $ownership)) { Write-Output 'No owned deployment is recorded.'; exit 1 }
$owner = Get-Content $ownership | ConvertFrom-Json
$actualProject = (& docker inspect $owner.containerId --format '{{index .Config.Labels "com.docker.compose.project"}}' 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $actualProject -ne $owner.project) { throw 'Recorded container is absent or ownership labels do not match.' }
Invoke-Compose ps storage
$healthEndpoint = if ($env:AUDORIX_COMPOSE_TLS -eq 'true') { "https://localhost:$($env:AUDORIX_PORT ?? '5195')/health/ready" } else { "http://127.0.0.1:$($env:AUDORIX_PORT ?? '5195')/health/ready" }
Invoke-RestMethod $healthEndpoint | ConvertTo-Json
