param([Parameter(Mandatory)][string]$Name)
. (Join-Path $PSScriptRoot 'Common.ps1')
if ($Name -notmatch '^[A-Za-z0-9._-]+$') { throw 'Backup name is invalid.' }
& (Join-Path $PSScriptRoot 'Stop.ps1')
Invoke-Compose run --rm --no-deps -e Backup__Destination="/var/backups/audorix/$Name" storage backup
& (Join-Path $PSScriptRoot 'Start.ps1')
Write-Output "Backup created and validated as $Name. Copy it to protected off-host storage."
