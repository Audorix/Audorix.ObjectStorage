$ErrorActionPreference = 'Stop'

$script:RepositoryRoot = Split-Path -Parent $PSScriptRoot
$script:StateRoot = Join-Path $script:RepositoryRoot '.soak'
$script:RunsRoot = Join-Path $script:StateRoot 'runs'
$script:CurrentRunPath = Join-Path $script:StateRoot 'current-run.json'
$script:ProjectPath = Join-Path $script:RepositoryRoot 'tools\Audorix.ObjectStorage.LoadHarness\Audorix.ObjectStorage.LoadHarness.csproj'
$script:CandidateConfig = Join-Path $script:RepositoryRoot 'tools\Audorix.ObjectStorage.LoadHarness\NuGet.Candidate.Config'
$script:PackageCache = Join-Path $script:StateRoot 'packages'
$script:BuildRoot = Join-Path $script:StateRoot 'build'

function Get-OwnedProcess {
    param([Parameter(Mandatory)]$Record)
    try {
        $process = Get-Process -Id ([int]$Record.pid) -ErrorAction Stop
        $recordedStart = [DateTimeOffset]::Parse([string]$Record.startedAt)
        $actualStart = [DateTimeOffset]$process.StartTime
        if ([Math]::Abs(($actualStart.ToUniversalTime() - $recordedStart.ToUniversalTime()).TotalSeconds) -gt 1) { return $null }
        $command = (Get-CimInstance Win32_Process -Filter "ProcessId = $($Record.pid)" -ErrorAction Stop).CommandLine
        if ($command -notlike "*$($Record.commandMarker)*") { return $null }
        return $process
    }
    catch { return $null }
}

function Read-LocalConnectionString {
    if (-not [string]::IsNullOrWhiteSpace($env:AUDORIX_OBJECTSTORAGE_POSTGRES)) {
        return $env:AUDORIX_OBJECTSTORAGE_POSTGRES
    }
    $path = Join-Path $script:RepositoryRoot 'src\Audorix.ObjectStorage.PostgreSql\data\database.json'
    if (-not (Test-Path -LiteralPath $path)) {
        throw 'Set AUDORIX_OBJECTSTORAGE_POSTGRES or provide the ignored local provider database.json.'
    }
    $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    return [string]$document.Database.ActiveConnectionString
}

function Assert-CandidateHashes {
    $directory = Join-Path $script:RepositoryRoot 'artifacts\candidates\4c5bbe9\packages'
    $expected = [ordered]@{
        'Audorix.ObjectStorage.0.1.0-rc.2.nupkg' = 'F7A1847C1B9968BD1FF37CE27639D6ABD0256CAFFAF363372A128A3FDA4D08FE'
        'Audorix.ObjectStorage.Api.0.1.0-rc.2.nupkg' = 'C283BDCD7CCDD75702B0D342781D366DB796AECC9A4F49BCBC6BD9D530510432'
        'Audorix.ObjectStorage.PostgreSql.0.1.0-rc.2.nupkg' = '0858DC2761023F421397B598C08C422B4C77CADB313462B471E0D9E173BA2CB4'
        'Audorix.ObjectStorage.S3.0.1.0-rc.2.nupkg' = 'A88788A91C99EA99E9D1CAE882472D150B92A6C161642628B57CCD1ACF60D00E'
    }
    foreach ($item in $expected.GetEnumerator()) {
        $path = Join-Path $directory $item.Key
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing candidate package: $($item.Key)" }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actual -ne $item.Value) { throw "Candidate hash mismatch: $($item.Key)" }
    }
}
