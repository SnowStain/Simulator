param(
    [switch]$IncludeLegacyWindows
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $root

$patterns = [ordered]@{
    "Windows TFM / WinForms project" = "net[0-9.]+-windows|UseWindowsForms"
    "WinForms API" = "System\.Windows\.Forms|TextRenderer|OpenFileDialog|SaveFileDialog|FolderBrowserDialog"
    "Win32 / WGL API" = "DllImport\(`"(user32|gdi32|kernel32)|WGL|wgl[A-Za-z0-9_]+|GetHicon|SendMessage"
    "Windows OpenCV runtime" = "OpenCvSharp4\.runtime\.win"
    "GDI drawing surface" = "System\.Drawing\.Graphics|Graphics\.FromImage|Bitmap\(|DrawString|DrawImage"
}

$linuxScanRoots = @(
    "src\Simulator.Linux",
    "src\Simulator.OpenTk",
    "src\Simulator.Platform",
    "src\Simulator.Core",
    "src\Simulator.Assets",
    "src\Simulator.AutoAimCalibrationTool",
    "src\Simulator.Editors",
    "src\Simulator.LoadLargeTerrain",
    "src\Simulator.Decision",
    "src\Simulator.Runtime"
)
$scanRoots = if ($IncludeLegacyWindows) { @("src") } else { $linuxScanRoots }

if ($IncludeLegacyWindows) {
    Write-Host "[windows-blockers] full legacy source audit"
} else {
    Write-Host "[windows-blockers] Linux-callable graph audit"
    Write-Host "[windows-blockers] pass -IncludeLegacyWindows to include the legacy ThreeD/WinForms shell"
}
foreach ($entry in $patterns.GetEnumerator()) {
    Write-Host ""
    Write-Host "## $($entry.Key)"
    $matches = foreach ($path in $scanRoots) {
        if (!(Test-Path $path)) {
            continue
        }

        Get-ChildItem -LiteralPath $path -Recurse -File -Include *.cs,*.csproj -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
            Select-String -Pattern $entry.Value -CaseSensitive
    }

    if (!$matches) {
        Write-Host "none"
        continue
    }

    $matches |
        Group-Object { $_.Path } |
        Sort-Object Name |
        ForEach-Object {
            $relative = Resolve-Path -LiteralPath $_.Name -Relative
            $count = $_.Count
            Write-Host ("{0} ({1})" -f $relative, $count)
            $_.Group | Select-Object -First 5 | ForEach-Object {
                Write-Host ("  L{0}: {1}" -f $_.LineNumber, $_.Line.Trim())
            }
            if ($count -gt 5) {
                Write-Host ("  ... {0} more" -f ($count - 5))
            }
        }
}

Write-Host ""
Write-Host "[windows-blockers] note: System.Drawing Color/Rectangle primitives are allowed in shared contracts; Graphics/Bitmap/TextRenderer are not."
