[CmdletBinding()]
param(
    [string]$ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    [xml]$project = Get-Content 'FFMoGuang/FFMoGuang.csproj' -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($ExpectedVersion -and $version -cne $ExpectedVersion) {
        throw "Release tag version $ExpectedVersion does not match project version $version."
    }
    dotnet build FFMoGuang.slnx --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    dotnet run --project tests/FFMoGuang.Tests --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Controller verification failed.' }

    $packagePath = Join-Path $PSScriptRoot 'FFMoGuang/bin/Release/FFMoGuang/latest.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        foreach ($required in @('FFMoGuang.dll', 'FFMoGuang.Core.dll', 'FFMoGuang.deps.json', 'FFMoGuang.json')) {
            if ($entries -notcontains $required) { throw "Package is missing $required" }
        }
        if (@($entries | Where-Object { $_ -match '(^|/)(Dalamud|FFXIVClientStructs)\.dll$' }).Count -ne 0) {
            throw 'The package must not bundle Dalamud runtime assemblies.'
        }
        $reader = [System.IO.StreamReader]::new($archive.GetEntry('FFMoGuang.json').Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if ($manifest.AssemblyVersion -cne $version) { throw 'Package manifest version does not match project version.' }
    }
    finally { $archive.Dispose() }

    $artifactDirectory = Join-Path $PSScriptRoot 'artifacts'
    New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
    $destination = Join-Path $artifactDirectory "FFMoGuang-$($manifest.AssemblyVersion).zip"
    Copy-Item -LiteralPath $packagePath -Destination $destination -Force
    Write-Output "Package verified: $destination"
}
finally { Pop-Location }
