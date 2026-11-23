param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repositoryRoot 'soak\Common.ps1')
$stateRoot = Join-Path $repositoryRoot '.recovery'
$packages = Join-Path $stateRoot 'packages'
$build = Join-Path $stateRoot 'build'
$output = Join-Path $repositoryRoot 'docs\thirteenth-recovery-summary.json'
New-Item -ItemType Directory -Force -Path $stateRoot, $packages | Out-Null
Assert-CandidateHashes
dotnet restore $script:ProjectPath --configfile $script:CandidateConfig --packages $packages --force --no-cache --nologo
if ($LASTEXITCODE -ne 0) { throw 'Candidate-only restore failed.' }
dotnet publish $script:ProjectPath -c Release --no-restore --nologo -o $build -p:RestorePackagesPath=$packages
if ($LASTEXITCODE -ne 0) { throw 'Harness publish failed.' }
$prior = $env:AUDORIX_OBJECTSTORAGE_POSTGRES
try {
    $env:AUDORIX_OBJECTSTORAGE_POSTGRES = Read-LocalConnectionString
    & (Join-Path $build 'Audorix.ObjectStorage.LoadHarness.exe') --recovery --work $stateRoot --output $output
    if ($LASTEXITCODE -ne 0) { throw 'Recovery harness failed.' }
}
finally {
    $env:AUDORIX_OBJECTSTORAGE_POSTGRES = $prior
}
$output
