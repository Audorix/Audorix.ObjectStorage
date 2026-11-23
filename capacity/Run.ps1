param(
    [ValidateRange(5, 300)][int]$StageSeconds = 15,
    [ValidateRange(1, 64)][int[]]$Concurrency = @(1, 2, 4, 8, 16),
    [ValidateRange(1, 16384)][int]$SmallObjectKiB = 64,
    [ValidateRange(2, 256)][int]$LargeObjectMiB = 64,
    [ValidateRange(2, 5000)][int]$MaxListObjects = 1000
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repositoryRoot 'soak\Common.ps1')

$stateRoot = Join-Path $repositoryRoot '.capacity'
$packageCache = Join-Path $stateRoot 'packages'
$buildRoot = Join-Path $stateRoot 'build'
$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $stateRoot $runId
$finalOutput = Join-Path $repositoryRoot 'docs\thirteenth-capacity-summary.json'
New-Item -ItemType Directory -Force -Path $stateRoot, $packageCache, $runRoot | Out-Null

Assert-CandidateHashes
dotnet restore $script:ProjectPath --configfile $script:CandidateConfig --packages $packageCache --force --no-cache --nologo
if ($LASTEXITCODE -ne 0) { throw 'Candidate-only restore failed.' }
dotnet publish $script:ProjectPath -c Release --no-restore --nologo -o $buildRoot -p:RestorePackagesPath=$packageCache
if ($LASTEXITCODE -ne 0) { throw 'Harness publish failed.' }

$executable = Join-Path $buildRoot 'Audorix.ObjectStorage.LoadHarness.exe'
$prior = $env:AUDORIX_OBJECTSTORAGE_POSTGRES
$started = [DateTimeOffset]::UtcNow
try {
    $env:AUDORIX_OBJECTSTORAGE_POSTGRES = Read-LocalConnectionString
    $stages = @()
    foreach ($level in $Concurrency) {
        $path = Join-Path $runRoot "small-c$level.json"
        & $executable --duration-seconds $StageSeconds --concurrency $level --object-kib $SmallObjectKiB --work $runRoot --output $path
        if ($LASTEXITCODE -ne 0) { throw "Small-object stage failed at concurrency $level." }
        $stages += Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }

    $listingPath = Join-Path $runRoot 'listing.json'
    & $executable --listing --counts "10,100,$MaxListObjects" --repetitions 20 --page-size 100 --work $runRoot --output $listingPath
    if ($LASTEXITCODE -ne 0) { throw 'Listing stage failed.' }

    $largePath = Join-Path $runRoot 'large-transfer.json'
    $comparisonMiB = [Math]::Max(1, [Math]::Floor($LargeObjectMiB / 4))
    & $executable --comparison-mib $comparisonMiB --size-mib $LargeObjectMiB --concurrency '1,2,4' --work $runRoot --output $largePath
    if ($LASTEXITCODE -ne 0) { throw 'Large-transfer stage failed.' }

    $overloadPath = Join-Path $runRoot 'overload.json'
    & $executable --overload --clients 16 --object-mib 8 --max-concurrent 4 --max-queued 2 --work $runRoot --output $overloadPath
    if ($LASTEXITCODE -ne 0) { throw 'Overload stage failed.' }

    $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($repositoryRoot).Substring(0, 1))
    $packages = Get-ChildItem (Join-Path $repositoryRoot 'artifacts\candidates\4c5bbe9\packages\*.nupkg') | ForEach-Object {
        [ordered]@{ file = $_.Name; bytes = $_.Length; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    }
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $computer = Get-CimInstance Win32_ComputerSystem
    $summary = [ordered]@{
        runId = $runId
        candidateVersion = '0.1.0-rc.2'
        sourceCommit = '4c5bbe953687023018eb3d2ca230f75e4e6f6c94'
        startedAt = $started
        completedAt = [DateTimeOffset]::UtcNow
        environment = [ordered]@{
            operatingSystem = [Environment]::OSVersion.VersionString
            runtime = (& dotnet --version)
            logicalProcessors = [Environment]::ProcessorCount
            cpu = [string]$cpu.Name
            cpuCores = [int]$cpu.NumberOfCores
            memoryBytes = [long]$computer.TotalPhysicalMemory
            hardware = ([string]$computer.Manufacturer + ' ' + [string]$computer.Model).Trim()
            machineTopology = 'client, Kestrel server, filesystem blobs, and PostgreSQL all on one local machine'
            freeDiskBytesAfter = [long]$drive.Free
        }
        limits = [ordered]@{
            stageSeconds = $StageSeconds
            concurrency = $Concurrency
            smallObjectBytes = $SmallObjectKiB * 1024
            maxListObjects = $MaxListObjects
            largeObjectBytes = $LargeObjectMiB * 1024 * 1024
            overloadClients = 16
            overloadServerConcurrent = 4
            overloadServerQueue = 2
        }
        packages = $packages
        smallObjectStages = $stages
        listing = Get-Content -LiteralPath $listingPath -Raw | ConvertFrom-Json
        largeTransfers = Get-Content -LiteralPath $largePath -Raw | ConvertFrom-Json
        overload = Get-Content -LiteralPath $overloadPath -Raw | ConvertFrom-Json
        notes = @(
            'Cache-warm local evidence only; client and server share this machine.',
            'Small-object offered load is closed-loop and bounded by the configured client concurrency.',
            'Large-transfer process metrics separate the client and server; other stage environment metrics are point-in-time.'
        )
    }
    $summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $finalOutput -Encoding utf8
    $finalOutput
}
finally {
    $env:AUDORIX_OBJECTSTORAGE_POSTGRES = $prior
}
