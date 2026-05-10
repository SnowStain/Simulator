$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$mapPreset = if ($args.Count -ge 1) { $args[0] } else { "rmuc2026" }
Set-Location $root

Write-Host "[verify-linux] 1/4 portability gate"
powershell -ExecutionPolicy Bypass -File scripts\linux\check-linux-portability.ps1

Write-Host "[verify-linux] 2/4 headless diagnostics"
dotnet run --project src\Simulator.Linux\Simulator.Linux.csproj -- --diagnostics --map $mapPreset

Write-Host "[verify-linux] 3/4 linux-x64 publish"
dotnet publish src\Simulator.Linux\Simulator.Linux.csproj -c Debug -r linux-x64 --self-contained false -o .linux-smoke\publish

Write-Host "[verify-linux] 4/4 published artifact check"
$linuxBinary = Join-Path $root ".linux-smoke\publish\Simulator.Linux"
if (!(Test-Path $linuxBinary)) {
    throw "linux-x64 publish did not produce $linuxBinary"
}
Write-Host "OK linux-x64 artifact: $linuxBinary"

Write-Host "[verify-linux] OK; OpenGL window smoke must run on Linux with: bash scripts/linux/smoke-linux-operator.sh $mapPreset"
