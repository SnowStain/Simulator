$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $root

$project = "src\Simulator.Linux\Simulator.Linux.csproj"
$scanPaths = @(
    $project,
    "src\Simulator.Linux",
    "src\Simulator.Platform",
    "src\Simulator.Core",
    "src\Simulator.Assets"
)

Write-Host "[linux-portability] checking project references"
$refs = dotnet list $project reference
$refs | ForEach-Object { Write-Host $_ }
if (($refs | Select-String -Pattern "Simulator\.ThreeD|Simulator\.LoadLargeTerrain|Simulator\.Decision|Simulator\.AutoAimCalibrationTool")) {
    throw "[linux-portability] forbidden Windows/editor project reference in Linux graph"
}

Write-Host "[linux-portability] checking package graph"
$packages = dotnet list $project package --include-transitive
$packages | ForEach-Object { Write-Host $_ }
if (($packages | Select-String -Pattern "OpenCvSharp4\.runtime\.win|System\.Windows\.Forms|Microsoft\.Windows")) {
    throw "[linux-portability] forbidden Windows package in Linux graph"
}

Write-Host "[linux-portability] scanning source for Windows-only APIs"
$pattern = 'net[0-9.]+-windows|UseWindowsForms|System\.Windows\.Forms|OpenCvSharp4\.runtime\.win|DllImport\("(user32|gdi32|kernel32)|Microsoft\.Win32\.Registry|WGL'
foreach ($path in $scanPaths) {
    if (!(Test-Path $path)) {
        continue
    }

    $matches = Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Select-String -Pattern $pattern
    if ($matches) {
        $matches | ForEach-Object { Write-Host $_ }
        throw "[linux-portability] forbidden Windows-only API found under $path"
    }
}

Write-Host "[linux-portability] building Linux operator"
dotnet build $project -c Debug --no-restore

Write-Host "[linux-portability] OK"
