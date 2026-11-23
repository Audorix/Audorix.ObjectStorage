. (Join-Path $PSScriptRoot 'Common.ps1')
$ownership = Join-Path $State 'ownership.json'; if (-not (Test-Path $ownership)) { throw 'No owned deployment is recorded.' }
$owner = Get-Content $ownership | ConvertFrom-Json
$actualProject = (& docker inspect $owner.containerId --format '{{index .Config.Labels "com.docker.compose.project"}}' 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $actualProject -ne $owner.project -or $owner.project -ne $Project) { throw 'Ownership check failed; refusing to stop a process by port or name.' }
Invoke-Compose stop --timeout 120 storage
Remove-Item -LiteralPath $ownership
