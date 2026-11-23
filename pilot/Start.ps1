. (Join-Path $PSScriptRoot "Common.ps1")

& (Join-Path $PSScriptRoot "Provision.ps1")
$existing = @(Read-OwnedProcesses)
if ($existing.Count -gt 0 -and ($existing | Where-Object { Test-OwnedProcess $_ }).Count -gt 0) {
    throw "An owned pilot is already running. Use pilot/Status.ps1 or pilot/Stop.ps1 first."
}

$configuration = Read-PilotConfiguration
$secrets = Read-PilotSecrets
$dotnet = Get-DotNetPath
$apps = Join-Path $script:PilotRoot "apps"
New-Item -ItemType Directory -Force -Path $apps | Out-Null

$storageOutput = Join-Path $apps "storage"
$reportStudioOutput = Join-Path $apps "reportstudio"
& $dotnet build (Join-Path $script:RepositoryRoot "src\Audorix.ObjectStorage.Host\Audorix.ObjectStorage.Host.csproj") `
    -c Release --nologo -v:minimal -o $storageOutput /p:UseAppHost=false
if ($LASTEXITCODE -ne 0) { throw "Storage host build failed." }
& $dotnet build (Join-Path $script:WorkspaceRoot "Phase0Work\src\SaaSRDLGenerator.Api\SaaSRDLGenerator.Api.csproj") `
    -c Release --nologo -v:minimal -o $reportStudioOutput /p:UseAppHost=false
if ($LASTEXITCODE -ne 0) { throw "ReportStudio build failed." }

$records = @()
try {
    $storageDll = Join-Path $storageOutput "Audorix.ObjectStorage.Host.dll"
    $storageEnvironment = @{
        ASPNETCORE_ENVIRONMENT = "Development"
        ConnectionStrings__AudorixObjectStorage = [string]$secrets.postgreSqlConnectionString
        Audorix__ObjectStorage__StorageRootPath = (Join-Path $script:PilotRoot "data\object-storage")
        Audorix__ObjectStorage__PostgreSqlSchema = [string]$configuration.storage.schema
        Audorix__S3__Region = "us-east-1"
        Audorix__S3__ContinuationTokenSigningKey = [string]$secrets.continuationTokenSigningKey
        Audorix__S3__AccessKeyId = [string]$secrets.s3AccessKeyId
        Audorix__S3__SecretAccessKey = [string]$secrets.s3SecretAccessKey
        Audorix__S3__AllowedBuckets = [string]$configuration.storage.bucket
    }
    $records += Start-OwnedProcess "storage" $dotnet @($storageDll, "--urls", [string]$configuration.storage.url) `
        $script:RepositoryRoot $storageEnvironment $storageDll
    Wait-Ready "Storage host" ([string]$configuration.storage.url + "/")
    Invoke-WebRequest -Method Post -Uri ([string]$configuration.storage.url + "/demo/" + [string]$configuration.storage.bucket + "/pilot/provisioned.txt") `
        -Body "Audorix tenth-slice pilot" -ContentType "text/plain" -UseBasicParsing | Out-Null

    $reportStudioDll = Join-Path $reportStudioOutput "SaaSRDLGenerator.Api.dll"
    $reportStudioEnvironment = @{
        ASPNETCORE_ENVIRONMENT = "Development"
        REPORT_STUDIO = [string]$secrets.postgreSqlConnectionString
        RDL__Storage__Provider = "AudorixS3"
        RDL__Storage__AudorixS3__Endpoint = ([string]$configuration.storage.url + "/s3")
        RDL__Storage__AudorixS3__Bucket = [string]$configuration.storage.bucket
        RDL__Storage__AudorixS3__Region = "us-east-1"
        RDL__Storage__AudorixS3__UsePathStyle = "true"
        RDL__Storage__AudorixS3__TimeoutSeconds = "100"
        RDL__Storage__AudorixS3__AccessKey = [string]$secrets.s3AccessKeyId
        RDL__Storage__AudorixS3__SecretKey = [string]$secrets.s3SecretAccessKey
        Authentication__DataProtectionPath = (Join-Path $script:PilotRoot "data\data-protection")
    }
    $records += Start-OwnedProcess "reportstudio" $dotnet @($reportStudioDll, "--urls", [string]$configuration.reportStudio.url) `
        (Join-Path $script:WorkspaceRoot "Phase0Work") $reportStudioEnvironment $reportStudioDll
    Wait-Ready "ReportStudio API" ([string]$configuration.reportStudio.url + "/api/health") 60

    $node = (Get-Command node -ErrorAction Stop).Source
    $vite = Join-Path $script:WorkspaceRoot "Phase0Work\client\node_modules\vite\bin\vite.js"
    if (-not (Test-Path -LiteralPath $vite)) { throw "Frontend dependencies are missing. Run npm install in Phase0Work/client." }
    $frontendEnvironment = @{ VITE_API_TARGET = [string]$configuration.reportStudio.url }
    $frontendPort = ([Uri]$configuration.frontend.url).Port.ToString()
    $records += Start-OwnedProcess "frontend" $node @($vite, "--host", "127.0.0.1", "--port", $frontendPort, "--strictPort") `
        (Join-Path $script:WorkspaceRoot "Phase0Work\client") $frontendEnvironment $vite
    Wait-Ready "Frontend" ([string]$configuration.frontend.url + "/")

    $records | ConvertTo-Json | Set-Content -LiteralPath $script:PilotProcessesPath -Encoding utf8NoBOM
    Write-Host "Pilot ready: storage=$($configuration.storage.url) api=$($configuration.reportStudio.url) frontend=$($configuration.frontend.url)"
}
catch {
    [array]::Reverse($records)
    foreach ($record in $records) {
        if (Test-OwnedProcess $record) { Stop-Process -Id $record.pid -Force }
    }
    throw
}
