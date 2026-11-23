. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Env @('AUDORIX_BUCKET','AUDORIX_S3_ACCESS_KEY','AUDORIX_S3_SECRET_KEY')
$port=$env:AUDORIX_PORT ?? '5195'; $endpoint=if($env:AUDORIX_COMPOSE_TLS -eq 'true'){"https://localhost:$port/s3"}else{"http://127.0.0.1:$port/s3"}; $key="validation/space +percent%25/question?literal.txt"
$temp=Join-Path ([IO.Path]::GetTempPath()) ("audorix-"+[guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $temp | Out-Null
try {
  $source=Join-Path $temp 'source.bin'; $target=Join-Path $temp 'target.bin'; [IO.File]::WriteAllBytes($source, (0..65535 | ForEach-Object { [byte]($_ % 251) }))
  $env:AWS_ACCESS_KEY_ID=$env:AUDORIX_S3_ACCESS_KEY; $env:AWS_SECRET_ACCESS_KEY=$env:AUDORIX_S3_SECRET_KEY; $env:AWS_DEFAULT_REGION='us-east-1'; $env:AWS_REQUEST_CHECKSUM_CALCULATION='when_required'; $env:AWS_RESPONSE_CHECKSUM_VALIDATION='when_required'
  $common=@('--endpoint-url',$endpoint,'--no-cli-pager'); & aws @common s3api put-object --bucket $env:AUDORIX_BUCKET --key $key --body $source --content-type application/octet-stream --metadata 'owner=audorix,purpose=deployment'; if ($LASTEXITCODE) { throw 'PUT failed.' }
  & aws @common s3api head-object --bucket $env:AUDORIX_BUCKET --key $key; if ($LASTEXITCODE) { throw 'HEAD failed.' }
  & aws @common s3api list-objects-v2 --bucket $env:AUDORIX_BUCKET --prefix validation/; if ($LASTEXITCODE) { throw 'LIST failed.' }
  & aws @common s3api get-object --bucket $env:AUDORIX_BUCKET --key $key --range bytes=10-99 (Join-Path $temp 'range.bin'); if ($LASTEXITCODE) { throw 'RANGE failed.' }
  & aws @common s3api get-object --bucket $env:AUDORIX_BUCKET --key $key $target; if ($LASTEXITCODE -or (Get-FileHash $source).Hash -ne (Get-FileHash $target).Hash) { throw 'Download hash mismatch.' }
  & aws @common s3api delete-object --bucket $env:AUDORIX_BUCKET --key $key; if ($LASTEXITCODE) { throw 'DELETE failed.' }
  Write-Output 'SDK-compatible CLI validation passed.'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
