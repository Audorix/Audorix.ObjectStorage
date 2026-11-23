param(
    [ValidateRange(1, 604800)][int]$DurationSeconds = 7200,
    [ValidateRange(1, 64)][int]$Concurrency = 4,
    [ValidateRange(1, 16384)][int]$SmallKiB = 64,
    [ValidateRange(1, 256)][int]$LargeMiB = 4,
    [ValidateRange(128, 1048576)][int]$DiskBudgetMiB = 1024,
    [ValidateRange(5, 3600)][int]$CheckpointSeconds = 60
)

. (Join-Path $PSScriptRoot 'Common.ps1')

New-Item -ItemType Directory -Force -Path $script:StateRoot, $script:RunsRoot, $script:PackageCache | Out-Null
if (Test-Path -LiteralPath $script:CurrentRunPath) {
    $existing = Get-Content -LiteralPath $script:CurrentRunPath -Raw | ConvertFrom-Json
    if (Get-OwnedProcess $existing) { throw "Owned soak run $($existing.runId) is already active." }
}

Assert-CandidateHashes
dotnet restore $script:ProjectPath --configfile $script:CandidateConfig --packages $script:PackageCache --force --no-cache --nologo
if ($LASTEXITCODE -ne 0) { throw 'Candidate-only restore failed.' }
dotnet publish $script:ProjectPath -c Release --no-restore --nologo -o $script:BuildRoot -p:RestorePackagesPath=$script:PackageCache
if ($LASTEXITCODE -ne 0) { throw 'Harness publish failed.' }

$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $script:RunsRoot $runId
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$summary = Join-Path $runDirectory 'summary.json'
$checkpoint = Join-Path $runDirectory 'checkpoint.json'
$stdout = Join-Path $runDirectory 'stdout.log'
$stderr = Join-Path $runDirectory 'stderr.log'
$executable = Join-Path $script:BuildRoot 'Audorix.ObjectStorage.LoadHarness.exe'
$arguments = @(
    '--soak', '--run-id', $runId,
    '--duration-seconds', $DurationSeconds,
    '--concurrency', $Concurrency,
    '--small-kib', $SmallKiB,
    '--large-mib', $LargeMiB,
    '--disk-budget-mib', $DiskBudgetMiB,
    '--checkpoint-seconds', $CheckpointSeconds,
    '--run-dir', $runDirectory,
    '--output', $summary,
    '--checkpoint', $checkpoint
)

$prior = $env:AUDORIX_OBJECTSTORAGE_POSTGRES
try {
    $env:AUDORIX_OBJECTSTORAGE_POSTGRES = Read-LocalConnectionString
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
}
finally {
    $env:AUDORIX_OBJECTSTORAGE_POSTGRES = $prior
}

$process.Refresh()
$record = [ordered]@{
    runId = $runId
    pid = $process.Id
    startedAt = $process.StartTime.ToString('O')
    commandMarker = $runId
    requestedDurationSeconds = $DurationSeconds
    concurrency = $Concurrency
    runDirectory = $runDirectory
    summaryPath = $summary
    checkpointPath = $checkpoint
    stdoutPath = $stdout
    stderrPath = $stderr
    executablePath = $executable
    executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runDirectory 'process.json') -Encoding utf8
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $script:CurrentRunPath -Encoding utf8
$record
