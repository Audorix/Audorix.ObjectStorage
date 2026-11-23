param([Parameter(Mandatory)][string]$PriorImage)
. (Join-Path $PSScriptRoot 'Common.ps1')
if ($PriorImage -notmatch '^.+@sha256:[0-9a-fA-F]{64}$') { throw 'Rollback requires an immutable image digest.' }
$env:AUDORIX_IMAGE=$PriorImage
& (Join-Path $PSScriptRoot 'Stop.ps1')
& (Join-Path $PSScriptRoot 'Start.ps1')
