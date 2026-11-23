. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Env @('AUDORIX_DEPLOYMENT_ID','AUDORIX_POSTGRES_CONNECTION','AUDORIX_POSTGRES_SCHEMA','AUDORIX_BUCKET','AUDORIX_S3_ACCESS_KEY','AUDORIX_S3_SECRET_KEY','AUDORIX_S3_TOKEN_KEY','AUDORIX_OPERATIONS_TOKEN')
$existing = Invoke-Compose ps --quiet storage
if ($existing) { throw 'Storage is already running; provisioning is a separate maintenance action.' }
Invoke-Compose run --rm --no-deps -e Deployment__AllowInitialize=true storage provision
Write-Output 'Provisioning completed. Normal Start.ps1 is now fail-closed against the recorded root and bucket.'
