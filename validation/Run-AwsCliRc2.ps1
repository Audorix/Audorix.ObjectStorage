param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repositoryRoot 'soak\Common.ps1')
$aws = Join-Path $env:LOCALAPPDATA 'Programs\Amazon\AWSCLIV2\aws.exe'
if (-not (Test-Path -LiteralPath $aws)) { throw "AWS CLI was not found at $aws" }

$stateRoot = Join-Path $repositoryRoot '.aws-cli'
$packages = Join-Path $stateRoot 'packages'
$build = Join-Path $stateRoot 'build'
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $stateRoot $runId
$root = Join-Path $runRoot 'storage'
$ready = Join-Path $runRoot 'ready.txt'
$stdout = Join-Path $runRoot 'host.stdout.log'
$stderr = Join-Path $runRoot 'host.stderr.log'
$schema = 'audorix_cli_' + $runId
$output = Join-Path $repositoryRoot 'docs\thirteenth-aws-cli-summary.json'
New-Item -ItemType Directory -Force -Path $packages, $runRoot, $root | Out-Null

Assert-CandidateHashes
dotnet restore $script:ProjectPath --configfile $script:CandidateConfig --packages $packages --force --no-cache --nologo
if ($LASTEXITCODE -ne 0) { throw 'Candidate-only restore failed.' }
dotnet publish $script:ProjectPath -c Release --no-restore --nologo -o $build -p:RestorePackagesPath=$packages
if ($LASTEXITCODE -ne 0) { throw 'Harness publish failed.' }

$executable = Join-Path $build 'Audorix.ObjectStorage.LoadHarness.exe'
$connection = Read-LocalConnectionString
$prior = @{
    db = $env:AUDORIX_OBJECTSTORAGE_POSTGRES
    access = $env:AWS_ACCESS_KEY_ID
    secret = $env:AWS_SECRET_ACCESS_KEY
    region = $env:AWS_DEFAULT_REGION
    requestChecksum = $env:AWS_REQUEST_CHECKSUM_CALCULATION
    responseChecksum = $env:AWS_RESPONSE_CHECKSUM_VALIDATION
}
$hostProcess = $null
try {
    $env:AUDORIX_OBJECTSTORAGE_POSTGRES = $connection
    $start = @{ FilePath = $executable; ArgumentList = @('server', $root, $schema, $ready); PassThru = $true; WindowStyle = 'Hidden'; RedirectStandardOutput = $stdout; RedirectStandardError = $stderr }
    $hostProcess = Start-Process @start
    $endpoint = $null
    for ($attempt = 0; $attempt -lt 300 -and -not $endpoint; $attempt++) {
        if ($hostProcess.HasExited) { throw 'Package-only AWS CLI host exited before readiness.' }
        if (Test-Path -LiteralPath $ready) {
            try { $endpoint = (Get-Content -LiteralPath $ready -Raw).Trim() } catch [IO.IOException] { }
        }
        if (-not $endpoint) { Start-Sleep -Milliseconds 100 }
    }
    if (-not $endpoint) { throw 'Package-only AWS CLI host did not become ready.' }

    $env:AWS_ACCESS_KEY_ID = 'AUDORIXLOADHARNESS'
    $env:AWS_SECRET_ACCESS_KEY = 'audorix-load-harness-secret'
    $env:AWS_DEFAULT_REGION = 'us-east-1'
    $env:AWS_REQUEST_CHECKSUM_CALCULATION = 'when_required'
    $env:AWS_RESPONSE_CHECKSUM_VALIDATION = 'when_required'
    $service = $endpoint + '/s3'
    $common = @('--endpoint-url', $service, '--region', 'us-east-1', '--no-cli-pager')
    $key = 'metadata/space +percent%25/question?literal.txt'
    $unsupportedKey = 'unsupported/default-checksum.bin'
    $source = Join-Path $runRoot 'source.bin'
    $download = Join-Path $runRoot 'download.bin'
    $range = Join-Path $runRoot 'range.bin'
    [IO.File]::WriteAllBytes($source, [Text.Encoding]::UTF8.GetBytes('Audorix rc.2 CLI metadata and special-key verification payload.'))

    $put = & $aws @common s3api put-object --bucket measurements --key $key --body $source --content-type 'text/plain' --metadata 'owner=audorix,purpose=rc2' 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Supported put-object failed: $put" }
    $headRaw = & $aws @common s3api head-object --bucket measurements --key $key 2>&1
    if ($LASTEXITCODE -ne 0) { throw "head-object failed: $headRaw" }
    $head = ($headRaw -join "`n") | ConvertFrom-Json
    $get = & $aws @common s3api get-object --bucket measurements --key $key $download 2>&1
    if ($LASTEXITCODE -ne 0) { throw "get-object failed: $get" }
    $listRaw = & $aws @common s3api list-objects-v2 --bucket measurements --prefix 'metadata/' 2>&1
    if ($LASTEXITCODE -ne 0) { throw "list-objects-v2 failed: $listRaw" }
    $list = ($listRaw -join "`n") | ConvertFrom-Json
    $rangeResult = & $aws @common s3api get-object --bucket measurements --key $key --range 'bytes=2-9' $range 2>&1
    if ($LASTEXITCODE -ne 0) { throw "range get-object failed: $rangeResult" }
    $sourceBytes = [IO.File]::ReadAllBytes($source)
    $expectedRange = $sourceBytes[2..9]
    $actualRange = [IO.File]::ReadAllBytes($range)
    $rangeMatches = [Convert]::ToHexString($expectedRange) -eq [Convert]::ToHexString($actualRange)
    $fullHashMatches = (Get-FileHash $source -Algorithm SHA256).Hash -eq (Get-FileHash $download -Algorithm SHA256).Hash

    $delete = & $aws @common s3api delete-object --bucket measurements --key $key 2>&1
    if ($LASTEXITCODE -ne 0) { throw "delete-object failed: $delete" }
    & $aws @common s3api head-object --bucket measurements --key $key *> $null
    $deleteVerified = $LASTEXITCODE -ne 0

    $env:AWS_REQUEST_CHECKSUM_CALCULATION = $null
    $env:AWS_RESPONSE_CHECKSUM_VALIDATION = $null
    $unsupportedOutput = & $aws @common s3api put-object --bucket measurements --key $unsupportedKey --body $source 2>&1
    $unsupportedExitCode = $LASTEXITCODE
    $unsupportedFailed = $unsupportedExitCode -ne 0
    $env:AWS_REQUEST_CHECKSUM_CALCULATION = 'when_required'
    $env:AWS_RESPONSE_CHECKSUM_VALIDATION = 'when_required'
    & $aws @common s3api head-object --bucket measurements --key $unsupportedKey *> $null
    $unsupportedObjectAbsent = $LASTEXITCODE -ne 0

    $version = (& $aws --version 2>&1) -join ' '
    $summary = [ordered]@{
        completedAt = [DateTimeOffset]::UtcNow
        candidateVersion = '0.1.0-rc.2'
        sourceCommit = '4c5bbe953687023018eb3d2ca230f75e4e6f6c94'
        awsCliVersion = $version
        endpoint = $service
        requiredConfiguration = [ordered]@{ requestChecksumCalculation = 'when_required'; responseChecksumValidation = 'when_required'; forcePathStyle = $true }
        commands = @(
            'aws --endpoint-url <redacted-loopback>/s3 --region us-east-1 s3api put-object --bucket measurements --key <special-key> --body <file> --metadata owner=audorix,purpose=rc2',
            'aws ... s3api head-object --bucket measurements --key <special-key>',
            'aws ... s3api get-object --bucket measurements --key <special-key> <file>',
            'aws ... s3api list-objects-v2 --bucket measurements --prefix metadata/',
            'aws ... s3api get-object --bucket measurements --key <special-key> --range bytes=2-9 <file>',
            'aws ... s3api delete-object --bucket measurements --key <special-key>'
        )
        supported = [ordered]@{
            put = $true
            head = $head.ContentLength -eq $sourceBytes.Length
            metadata = $head.Metadata.owner -eq 'audorix' -and $head.Metadata.purpose -eq 'rc2'
            getHash = $fullHashMatches
            listSpecialKey = @($list.Contents | Where-Object Key -eq $key).Count -eq 1
            range = $rangeMatches
            delete = $deleteVerified
        }
        unsupportedDefaultChecksum = [ordered]@{
            attempted = $true
            exitCode = $unsupportedExitCode
            failedExplicitly = $unsupportedFailed
            objectAbsent = $unsupportedObjectAbsent
            diagnostic = (($unsupportedOutput -join ' ') -replace [regex]::Escape($service), '<redacted-loopback>/s3')
        }
        passed = $fullHashMatches -and $rangeMatches -and $deleteVerified -and $unsupportedFailed -and $unsupportedObjectAbsent -and $head.Metadata.owner -eq 'audorix'
    }
    $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $output -Encoding utf8
    if (-not $summary.passed) { throw 'AWS CLI acceptance criteria failed.' }
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) {
        try { Invoke-WebRequest -Uri ($endpoint + '/harness/stop') -Method Post -UseBasicParsing | Out-Null; $hostProcess.WaitForExit(15000) | Out-Null } catch { }
        if (-not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force }
    }
    try {
        $env:AUDORIX_OBJECTSTORAGE_POSTGRES = $connection
        & $executable drop-schema $schema | Out-Null
    } catch { }
    $env:AUDORIX_OBJECTSTORAGE_POSTGRES = $prior.db
    $env:AWS_ACCESS_KEY_ID = $prior.access
    $env:AWS_SECRET_ACCESS_KEY = $prior.secret
    $env:AWS_DEFAULT_REGION = $prior.region
    $env:AWS_REQUEST_CHECKSUM_CALCULATION = $prior.requestChecksum
    $env:AWS_RESPONSE_CHECKSUM_VALIDATION = $prior.responseChecksum
}
$output
