param(
    [string] $PackageDirectory = (Join-Path $PSScriptRoot "..\artifacts\packages"),
    [string] $Version = "0.1.0-rc.1",
    [switch] $RequireLicense
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$expected = @{
    "Audorix.ObjectStorage" = @("Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Options")
    "Audorix.ObjectStorage.PostgreSql" = @("Audorix.ObjectStorage", "Npgsql")
    "Audorix.ObjectStorage.S3" = @("Audorix.ObjectStorage")
    "Audorix.ObjectStorage.Api" = @("Audorix.ObjectStorage")
}
$forbiddenNames = '(?i)(database\.json|appsettings|\.log$|testdata|test-output|connectionstrings|credentials|/obj/|/bin/)'
$forbiddenContent = '(?i)([A-Z]:\\Users\\|ActiveConnectionString|AUDORIXINTEGRATION|PACKAGECONSUMER|package-consumer-validation-secret)'
$results = @()

foreach ($packageId in $expected.Keys | Sort-Object) {
    $path = Join-Path $PackageDirectory "$packageId.$Version.nupkg"
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing package: $path" }
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $path))
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        $unexpectedNames = @($entries | Where-Object { $_ -match $forbiddenNames })
        if ($unexpectedNames.Count -gt 0) { throw "$packageId contains forbidden entries: $($unexpectedNames -join ', ')" }
        foreach ($entry in $archive.Entries | Where-Object { $_.Length -gt 0 -and $_.Length -lt 5MB }) {
            $stream = $entry.Open()
            try {
                $memory = [IO.MemoryStream]::new()
                $stream.CopyTo($memory)
                $text = [Text.Encoding]::UTF8.GetString($memory.ToArray())
                if ($text -match $forbiddenContent) { throw "$packageId contains forbidden development content in $($entry.FullName)." }
            }
            finally { $stream.Dispose() }
        }

        $nuspecEntry = $archive.Entries | Where-Object FullName -like '*.nuspec' | Select-Object -First 1
        $reader = [IO.StreamReader]::new($nuspecEntry.Open())
        try { $nuspec = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        if ($metadata.id -ne $packageId -or $metadata.version -ne $Version) {
            throw "$packageId has unexpected identity $($metadata.id) $($metadata.version)."
        }
        if (-not $entries.Contains("README.md") -or -not $entries.Contains("THIRD_PARTY_NOTICES.md")) {
            throw "$packageId is missing its readme or third-party notices."
        }
        if (-not ($entries -contains "lib/net10.0/$packageId.dll") -or -not ($entries -contains "lib/net10.0/$packageId.xml")) {
            throw "$packageId is missing its net10.0 DLL or XML documentation."
        }
        $actualDependencies = @($metadata.dependencies.group.dependency | ForEach-Object { [string]$_.id })
        foreach ($dependency in $expected[$packageId]) {
            if ($dependency -notin $actualDependencies) { throw "$packageId is missing dependency $dependency." }
        }

        $hasLicense = $null -ne $metadata.license -or -not [string]::IsNullOrWhiteSpace([string]$metadata.licenseUrl)
        if ($RequireLicense -and -not $hasLicense) { throw "$packageId has no declared license." }
        $results += [pscustomobject]@{
            Package = $packageId
            Version = [string]$metadata.version
            Bytes = (Get-Item -LiteralPath $path).Length
            Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            Entries = $entries.Count
            Dependencies = $actualDependencies -join ", "
            LicenseDeclared = $hasLicense
        }
    }
    finally { $archive.Dispose() }
}

$results | Format-Table -AutoSize
if ($results.LicenseDeclared -contains $false) {
    Write-Warning "Publication blocker: the owner has not supplied a project license. Internal RC validation may continue; public publication must not."
}
