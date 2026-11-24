# Packages a published Windows build into the portable ZIP and the Inno Setup installer.
# Usage: ./installer/windows/package.ps1 -Version 1.2.3 -Rid win-x64 -PublishDir publish/win-x64 -OutputDir dist
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [ValidateSet('win-x64', 'win-arm64')] [string] $Rid,
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $OutputDir
)
$ErrorActionPreference = 'Stop'
$arch = $Rid.Substring(4)
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$publish = (Resolve-Path $PublishDir).Path
$output = (Resolve-Path $OutputDir).Path

if (-not (Test-Path (Join-Path $publish 'Navigator.exe'))) { throw "Navigator.exe missing in $publish" }

# Portable ZIP: the marker tells the updater this copy may replace itself in place.
$staging = Join-Path ([IO.Path]::GetTempPath()) "navigator-portable-$Rid"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Copy-Item $publish $staging -Recurse
New-Item -ItemType File -Path (Join-Path $staging '.navigator-portable') | Out-Null
$zip = Join-Path $output "Navigator-$Version-$Rid-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

# Installer
if (-not (Get-Command ISCC.exe -ErrorAction SilentlyContinue)) {
    choco install innosetup --no-progress -y | Out-Null
    $env:Path += ";${env:ProgramFiles(x86)}\Inno Setup 6"
}
& ISCC.exe "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$output" "/DArch=$arch" (Join-Path $PSScriptRoot 'Navigator.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }

Get-ChildItem $output | Format-Table Name, Length
