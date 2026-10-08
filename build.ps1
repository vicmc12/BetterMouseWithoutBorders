# Builds BetterMouseWithoutBorders.exe into .\dist (the only file you need to copy to each PC).
# Requires the .NET SDK on the build machine only; the exe itself runs on any Windows 10/11
# (it uses the .NET Framework 4.8 that ships with Windows).
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$exe = 'BetterMouseWithoutBorders.exe'
$built = "src\BetterMouse\bin\Release\net48\$exe"
$target = "dist\$exe"

dotnet build src\BetterMouse\BetterMouse.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

if (-not $SkipTests) {
    dotnet test BetterMouse.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

New-Item -ItemType Directory -Force dist | Out-Null
# A running copy locks its exe, but Windows allows renaming it: move the old one aside so the
# new build lands at the same path (the host's firewall rule is tied to that path). Restart to use it.
if (Test-Path $target) {
    Remove-Item "dist\BetterMouseWithoutBorders.old.exe" -ErrorAction SilentlyContinue
    try { Copy-Item $built $target -Force -ErrorAction Stop }
    catch {
        Rename-Item $target "BetterMouseWithoutBorders.old.exe"
        Copy-Item $built $target -Force
        Write-Host "The app was running: restart it (tray > Exit, then start $target) to use the new build."
    }
} else {
    Copy-Item $built $target -Force
}
$hash = (Get-FileHash $target -Algorithm SHA256).Hash
Write-Host "$target  ($([math]::Round((Get-Item $target).Length / 1KB)) KB, SHA256 $hash)"
