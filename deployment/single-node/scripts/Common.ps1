$ErrorActionPreference = 'Stop'
$script:Bundle = Split-Path -Parent $PSScriptRoot
$script:Repo = (Resolve-Path (Join-Path $Bundle '..\..')).Path
$script:State = Join-Path $Repo '.deployment\single-node'
$script:Compose = Join-Path $Bundle 'compose.yaml'
$script:Project = if ($env:AUDORIX_COMPOSE_PROJECT) { $env:AUDORIX_COMPOSE_PROJECT } else { 'audorix-objectstorage' }
$script:ComposeArguments = @('--project-name', $script:Project, '--file', $script:Compose)
if ($env:AUDORIX_COMPOSE_LOCAL_POSTGRES -eq 'true') { $script:ComposeArguments += @('--file', (Join-Path $Bundle 'compose.local-postgres.yaml'), '--profile', 'local-postgres') }
if ($env:AUDORIX_COMPOSE_TLS -eq 'true') { $script:ComposeArguments += @('--file', (Join-Path $Bundle 'compose.tls.yaml')) }
function Assert-Env([string[]]$Names) { foreach ($name in $Names) { if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Required environment variable is absent: $name" } } }
function Invoke-Compose { & docker compose @script:ComposeArguments @args; if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed with exit code $LASTEXITCODE." } }
