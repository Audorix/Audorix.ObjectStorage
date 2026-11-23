. (Join-Path $PSScriptRoot 'Common.ps1')
$expected = @{
  'Audorix.ObjectStorage.0.1.0-rc.2.nupkg'='F7A1847C1B9968BD1FF37CE27639D6ABD0256CAFFAF363372A128A3FDA4D08FE'
  'Audorix.ObjectStorage.Api.0.1.0-rc.2.nupkg'='C283BDCD7CCDD75702B0D342781D366DB796AECC9A4F49BCBC6BD9D530510432'
  'Audorix.ObjectStorage.PostgreSql.0.1.0-rc.2.nupkg'='0858DC2761023F421397B598C08C422B4C77CADB313462B471E0D9E173BA2CB4'
  'Audorix.ObjectStorage.S3.0.1.0-rc.2.nupkg'='A88788A91C99EA99E9D1CAE882472D150B92A6C161642628B57CCD1ACF60D00E'
}
$feed = Join-Path $Repo 'artifacts\candidates\4c5bbe9\packages'
foreach ($item in $expected.GetEnumerator()) { $actual=(Get-FileHash (Join-Path $feed $item.Key) -Algorithm SHA256).Hash; if ($actual -ne $item.Value) { throw "Candidate hash mismatch: $($item.Key)" } }
Invoke-Compose build --pull storage
$image = if ($env:AUDORIX_IMAGE) { $env:AUDORIX_IMAGE } else { 'audorix-objectstorage:0.1.0-rc.2' }
$id = (& docker image inspect $image --format '{{.Id}}').Trim(); if ($LASTEXITCODE -ne 0) { throw 'Built image cannot be inspected.' }
New-Item -ItemType Directory -Force -Path $State | Out-Null
$id | Set-Content (Join-Path $State 'image-id.txt')
Write-Output "Built $image as $id"
