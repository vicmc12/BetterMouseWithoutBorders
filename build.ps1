# Builds BetterMouse.exe into .\dist (the only file you need to copy to each PC).
# Requires the .NET SDK on the build machine only; the exe itself runs on any Windows 10/11
# (it uses the .NET Framework 4.8 that ships with Windows).
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet build src\BetterMouse\BetterMouse.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

if (-not $SkipTests) {
    dotnet test BetterMouse.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

New-Item -ItemType Directory -Force dist | Out-Null
# A running BetterMouse locks its exe, but Windows allows renaming it: move the old one aside so the
# new build lands at the same path (the firewall rule is tied to that path). Restart to use it.
if (Test-Path dist\BetterMouse.exe) {
    Remove-Item dist\BetterMouse.old.exe -ErrorAction SilentlyContinue
    try { Copy-Item src\BetterMouse\bin\Release\net48\BetterMouse.exe dist\BetterMouse.exe -Force -ErrorAction Stop }
    catch {
        Rename-Item dist\BetterMouse.exe BetterMouse.old.exe
        Copy-Item src\BetterMouse\bin\Release\net48\BetterMouse.exe dist\BetterMouse.exe -Force
        Write-Host "BetterMouse was running: restart it (tray > Exit, then start dist\BetterMouse.exe) to use the new build."
    }
} else {
    Copy-Item src\BetterMouse\bin\Release\net48\BetterMouse.exe dist\BetterMouse.exe -Force
}
$hash = (Get-FileHash dist\BetterMouse.exe -Algorithm SHA256).Hash
Write-Host "dist\BetterMouse.exe  ($([math]::Round((Get-Item dist\BetterMouse.exe).Length / 1KB)) KB, SHA256 $hash)"
