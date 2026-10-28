[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve the CN distribution through the same public version endpoint as XIVLauncherCN.
$info = Invoke-RestMethod 'https://aonyx.ffxiv.wang/Dalamud/Release/VersionInfo?track=release'
if ([version]::Parse($info.AssemblyVersion).Major -ne 15) {
    throw "Expected Dalamud CN API 15; distribution is $($info.AssemblyVersion). Update the SDK and validate compatibility first."
}
$downloadUri = [uri]$info.DownloadUrl
if ($downloadUri.Scheme -ne 'https') { throw 'Dalamud download must use HTTPS.' }
$target = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $target -Force | Out-Null
$archive = Join-Path $target 'dalamud-package'
Invoke-WebRequest -Uri $downloadUri -OutFile $archive
# The version endpoint embeds the archive SHA-256 in its download filename.
# Its separate Hash field describes hashes.json, not the archive itself.
$expectedHash = [regex]::Match($downloadUri.AbsolutePath, '\.([a-fA-F0-9]{64})\.(?:7z|zip)$')
if (!$expectedHash.Success -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expectedHash.Groups[1].Value) {
    throw 'Dalamud package SHA-256 does not match the download filename.'
}
if ($downloadUri.AbsolutePath.EndsWith('.7z')) {
    & tar -xf $archive -C $target
    if ($LASTEXITCODE -ne 0) { throw 'Dalamud extraction failed.' }
}
else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $target, $true)
}
foreach ($required in @('Dalamud.dll', 'Dalamud.Bindings.ImGui.dll', 'FFXIVClientStructs.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $target $required))) {
        throw "Dalamud package is missing $required."
    }
}
Write-Output "Dalamud CN $($info.AssemblyVersion), game $($info.SupportedGameVer): $target"
$env:DALAMUD_HOME = $target
if ($env:GITHUB_ENV) { "DALAMUD_HOME=$target" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append }
