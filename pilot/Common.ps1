$ErrorActionPreference = "Stop"

$script:RepositoryRoot = Split-Path $PSScriptRoot -Parent
$script:WorkspaceRoot = Split-Path $script:RepositoryRoot -Parent
$script:PilotRoot = Join-Path $script:RepositoryRoot ".pilot"
$script:PilotConfigPath = Join-Path $script:PilotRoot "pilot.settings.json"
$script:PilotSecretsPath = Join-Path $script:PilotRoot "secrets.json"
$script:PilotProcessesPath = Join-Path $script:PilotRoot "processes.json"

function Read-PilotConfiguration {
    if (-not (Test-Path -LiteralPath $script:PilotConfigPath)) {
        throw "Pilot configuration is missing. Run pilot/Provision.ps1 first."
    }
    Get-Content -LiteralPath $script:PilotConfigPath -Raw | ConvertFrom-Json
}

function Read-PilotSecrets {
    if (-not (Test-Path -LiteralPath $script:PilotSecretsPath)) {
        throw "Pilot secrets are missing. Run pilot/Provision.ps1 first."
    }
    Get-Content -LiteralPath $script:PilotSecretsPath -Raw | ConvertFrom-Json
}

function Read-OwnedProcesses {
    if (-not (Test-Path -LiteralPath $script:PilotProcessesPath)) { return @() }
    @((Get-Content -LiteralPath $script:PilotProcessesPath -Raw | ConvertFrom-Json))
}

function Test-OwnedProcess($record) {
    $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $false }
    $actualStart = $process.StartTime
    if ($null -eq $record.startedAt) { return $false }
    $recordedStart = [DateTime]::Parse(
        $record.startedAt,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
    if ([Math]::Abs(($actualStart - $recordedStart).TotalSeconds) -gt 2) { return $false }
    $command = (Get-CimInstance Win32_Process -Filter "ProcessId = $($record.pid)" -ErrorAction SilentlyContinue).CommandLine
    return -not [string]::IsNullOrWhiteSpace($command) -and $command.Contains([string]$record.commandMarker, [StringComparison]::OrdinalIgnoreCase)
}

function Wait-Ready([string] $Name, [string] $Url, [int] $Seconds = 45) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "$Name did not become ready at $Url within $Seconds seconds."
}

function Start-OwnedProcess(
    [string] $Name,
    [string] $Executable,
    [string[]] $Arguments,
    [string] $WorkingDirectory,
    [hashtable] $Environment,
    [string] $CommandMarker) {
    $logs = Join-Path $script:PilotRoot "logs"
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $stdout = Join-Path $logs "$Name.stdout.log"
    $stderr = Join-Path $logs "$Name.stderr.log"
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory `
        -Environment $Environment -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
        -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 300
    if ($process.HasExited) { throw "$Name exited during startup. See $stderr." }
    [pscustomobject]@{
        name = $Name
        pid = $process.Id
        startedAt = $process.StartTime.ToString("O")
        commandMarker = $CommandMarker
        stdout = $stdout
        stderr = $stderr
    }
}

function Get-DotNetPath {
    (Get-Command dotnet -ErrorAction Stop).Source
}
