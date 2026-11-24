# Authenticode-signs the installers in a folder when a code-signing certificate is configured
# (secrets WINDOWS_CERTIFICATE_PFX_BASE64 / WINDOWS_CERTIFICATE_PASSWORD). Unsigned builds are labeled as such in docs/RELEASE.md.
param([Parameter(Mandatory)] [string] $Directory)
$ErrorActionPreference = 'Stop'
if (-not $env:WINDOWS_CERTIFICATE) { Write-Host 'No certificate configured; skipping signing.'; exit 0 }

$pfx = Join-Path ([IO.Path]::GetTempPath()) 'navigator-signing.pfx'
[IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:WINDOWS_CERTIFICATE))
try {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found' }
    foreach ($file in Get-ChildItem $Directory -Filter '*-setup.exe') {
        & $signtool.FullName sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /f $pfx /p $env:WINDOWS_CERTIFICATE_PASSWORD $file.FullName
        if ($LASTEXITCODE -ne 0) { throw "Signing $($file.Name) failed" }
    }
}
finally {
    Remove-Item $pfx -Force -ErrorAction SilentlyContinue
}
