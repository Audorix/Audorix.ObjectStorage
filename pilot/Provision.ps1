param([switch] $ForceNewSecrets)

. (Join-Path $PSScriptRoot "Common.ps1")

New-Item -ItemType Directory -Force -Path $script:PilotRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $script:PilotRoot "data\object-storage") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $script:PilotRoot "data\data-protection") | Out-Null

if (-not (Test-Path -LiteralPath $script:PilotConfigPath)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "templates\pilot.settings.template.json") -Destination $script:PilotConfigPath
}

if ($ForceNewSecrets -and (Test-Path -LiteralPath $script:PilotSecretsPath)) {
    throw "Refusing to overwrite pilot secrets. Stop the pilot and remove .pilot/secrets.json explicitly before reprovisioning."
}

if (-not (Test-Path -LiteralPath $script:PilotSecretsPath)) {
    $connection = $env:AUDORIX_PILOT_POSTGRES
    if ([string]::IsNullOrWhiteSpace($connection)) {
        $databasePath = Join-Path $script:RepositoryRoot "src\Audorix.ObjectStorage.PostgreSql\data\database.json"
        if (-not (Test-Path -LiteralPath $databasePath)) {
            throw "Set AUDORIX_PILOT_POSTGRES or provide the ignored local PostgreSQL database.json."
        }
        $database = Get-Content -LiteralPath $databasePath -Raw | ConvertFrom-Json
        $connection = $database.Database.ActiveConnectionString
        if ([string]::IsNullOrWhiteSpace($connection)) { $connection = $database.Database.PostgreSql.ConnectionString }
    }
    if ([string]::IsNullOrWhiteSpace($connection)) { throw "The pilot PostgreSQL connection is empty." }

    function New-Secret([int] $bytes) {
        [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes($bytes)).ToLowerInvariant()
    }
    $secrets = [ordered]@{
        postgreSqlConnectionString = $connection
        s3AccessKeyId = "PILOT" + (New-Secret 8).ToUpperInvariant()
        s3SecretAccessKey = New-Secret 32
        continuationTokenSigningKey = New-Secret 48
    }
    $secrets | ConvertTo-Json | Set-Content -LiteralPath $script:PilotSecretsPath -Encoding utf8NoBOM
}

Write-Host "Pilot provisioned at $script:PilotRoot. Existing configuration, secrets, database data, and object bytes were preserved."
