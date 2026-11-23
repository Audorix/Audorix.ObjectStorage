. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Env @('AUDORIX_DEPLOYMENT_ID','AUDORIX_POSTGRES_CONNECTION','AUDORIX_POSTGRES_SCHEMA','AUDORIX_BUCKET','AUDORIX_S3_ACCESS_KEY','AUDORIX_S3_SECRET_KEY','AUDORIX_S3_TOKEN_KEY','AUDORIX_OPERATIONS_TOKEN')
Invoke-Compose up --detach --no-build storage
$id = (Invoke-Compose ps --quiet storage | Select-Object -Last 1).Trim()
if (-not $id) { throw 'Storage container did not start.' }
New-Item -ItemType Directory -Force -Path $State | Out-Null
@{ project=$Project; containerId=$id; recordedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content (Join-Path $State 'ownership.json')
$healthEndpoint = if ($env:AUDORIX_COMPOSE_TLS -eq 'true') { "https://localhost:$($env:AUDORIX_PORT ?? '5195')/health/ready" } else { "http://127.0.0.1:$($env:AUDORIX_PORT ?? '5195')/health/ready" }
for ($i=0; $i -lt 30; $i++) { try { $r=Invoke-RestMethod $healthEndpoint; if ($r.status -eq 'ready') { Write-Output 'Storage is ready.'; exit 0 } } catch {}; Start-Sleep 2 }
throw 'Storage failed its readiness check.'
