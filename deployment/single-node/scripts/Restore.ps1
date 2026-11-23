param([Parameter(Mandatory)][string]$BackupName,[Parameter(Mandatory)][string]$TargetVolume)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Env @('AUDORIX_RESTORE_POSTGRES_CONNECTION')
if ($BackupName -notmatch '^[A-Za-z0-9._-]+$' -or $TargetVolume -notmatch '^[A-Za-z0-9._-]+$') { throw 'Backup or volume name is invalid.' }
& docker volume inspect $TargetVolume *> $null
if ($LASTEXITCODE -eq 0) { throw 'Restore target volume already exists; refusing to overwrite it.' }
& docker volume create --label audorix.owner=$Project $TargetVolume | Out-Null
try {
  $image = if ($env:AUDORIX_IMAGE) { $env:AUDORIX_IMAGE } else { 'audorix-objectstorage:0.1.0-rc.2' }
  & docker run --rm --user 0 --entrypoint chown --volume "${TargetVolume}:/restore" $image 10001:10001 /restore
  if ($LASTEXITCODE -ne 0) { throw 'Could not assign the new restore volume to the runtime identity.' }
  & (Join-Path $PSScriptRoot 'Stop.ps1')
  Invoke-Compose run --rm --no-deps --volume "${TargetVolume}:/restore" -e Restore__BackupPath="/var/backups/audorix/$BackupName" -e Restore__StorageRoot=/restore/data -e Restore__ConnectionString=$env:AUDORIX_RESTORE_POSTGRES_CONNECTION storage restore
  & (Join-Path $PSScriptRoot 'Start.ps1')
  Write-Output 'Restore validated separately. Activate only after verification with AUDORIX_BLOB_VOLUME set to the target and AUDORIX_STORAGE_ROOT=/var/lib/audorix/data.'
} catch { try { & (Join-Path $PSScriptRoot 'Start.ps1') } catch {}; & docker volume rm $TargetVolume *> $null; throw }
