$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $root

$solution = "Simulator.Linux.sln"
$project = "src\Simulator.Linux\Simulator.Linux.csproj"
$toolProjects = @(
    "src\Simulator.AutoAimCalibrationTool\Simulator.AutoAimCalibrationTool.csproj",
    "src\Simulator.LoadLargeTerrain\LoadLargeTerrain.csproj",
    "src\Simulator.Decision\Simulator.Decision.csproj"
)
$scanPaths = @(
    $project,
    "src\Simulator.Linux",
    "src\Simulator.OpenTk",
    "src\Simulator.Platform",
    "src\Simulator.Core",
    "src\Simulator.Assets",
    "src\Simulator.AutoAimCalibrationTool",
    "src\Simulator.Runtime",
    "src\Simulator.LoadLargeTerrain",
    "src\Simulator.Decision"
)

Write-Host "[linux-portability] checking project references"
$solutionRefs = dotnet sln $solution list
if ($LASTEXITCODE -ne 0) {
    throw "[linux-portability] failed to list Linux solution projects"
}
$solutionRefs | ForEach-Object { Write-Host $_ }
if (($solutionRefs | Select-String -Pattern "Simulator\.ThreeD")) {
    throw "[linux-portability] forbidden Windows shell project in Linux solution"
}

$refs = dotnet list $project reference
if ($LASTEXITCODE -ne 0) {
    throw "[linux-portability] failed to list project references"
}
$refs | ForEach-Object { Write-Host $_ }
if (($refs | Select-String -Pattern "Simulator\.ThreeD|Simulator\.LoadLargeTerrain|Simulator\.Decision|Simulator\.AutoAimCalibrationTool")) {
    throw "[linux-portability] forbidden Windows/editor project reference in Linux graph"
}

Write-Host "[linux-portability] checking package graph"
$packages = dotnet list $project package --include-transitive
if ($LASTEXITCODE -ne 0) {
    throw "[linux-portability] failed to list package graph"
}
$packages | ForEach-Object { Write-Host $_ }
if (($packages | Select-String -Pattern "OpenCvSharp4\.runtime\.win|System\.Windows\.Forms|Microsoft\.Windows")) {
    throw "[linux-portability] forbidden Windows package in Linux graph"
}

Write-Host "[linux-portability] scanning source for Windows-only APIs"
$pattern = 'net[0-9.]+-windows|UseWindowsForms|System\.Windows\.Forms|OpenCvSharp4\.runtime\.win|DllImport\("(user32|gdi32|kernel32)|Microsoft\.Win32\.Registry|WGL|OpenFileDialog|SaveFileDialog|FolderBrowserDialog|System\.Drawing\.Graphics|TextRenderer'
foreach ($path in $scanPaths) {
    if (!(Test-Path $path)) {
        continue
    }

    $matches = Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Select-String -Pattern $pattern -CaseSensitive
    if ($matches) {
        $matches | ForEach-Object { Write-Host $_ }
        throw "[linux-portability] forbidden Windows-only API found under $path"
    }
}

Write-Host "[linux-portability] building Linux operator"
dotnet build $solution -c Debug
if ($LASTEXITCODE -ne 0) {
    throw "[linux-portability] Linux solution build failed"
}

foreach ($toolProject in $toolProjects) {
    Write-Host "[linux-portability] building cross-platform tool $toolProject"
    dotnet build $toolProject -c Debug
    if ($LASTEXITCODE -ne 0) {
        throw "[linux-portability] cross-platform tool build failed: $toolProject"
    }
}

Write-Host "[linux-portability] OK"
