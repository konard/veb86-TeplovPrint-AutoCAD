param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ReleaseRoot
)

# Verifies the archives produced by scripts/package-release.ps1.
# Exits with a non-zero code and lists every problem if the layout is wrong.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    $ReleaseRoot = [System.IO.Path]::Combine($root, 'release', "v$Version")
}

$failures = [System.Collections.Generic.List[string]]::new()

function Get-ArchiveEntries {
    param([Parameter(Mandatory = $true)][string]$ArchiveName)

    $path = Join-Path $ReleaseRoot $ArchiveName
    if (-not (Test-Path -LiteralPath $path)) {
        $failures.Add("Archive is missing: $path")
        return @()
    }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        return @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    }
    finally {
        $zip.Dispose()
    }
}

function Assert-Entries {
    param(
        [Parameter(Mandatory = $true)][string]$ArchiveName,
        [Parameter(Mandatory = $true)][string[]]$Required
    )

    $entries = Get-ArchiveEntries -ArchiveName $ArchiveName
    if ($entries.Count -eq 0) {
        return
    }

    foreach ($entry in $Required) {
        if ($entries -notcontains $entry) {
            $failures.Add("${ArchiveName}: missing '$entry'")
        }
    }

    foreach ($entry in $entries) {
        if ($entry -match '\.pdb$') {
            $failures.Add("${ArchiveName}: debug symbols must not be packaged: '$entry'")
        }
        if ($entry -match '(^|/)runtimes/(?!win/|win-x64/)[^/]+/') {
            $failures.Add("${ArchiveName}: non Windows x64 runtime asset: '$entry'")
        }
    }

    Write-Host "$ArchiveName : $($entries.Count) entries checked"
}

$bundle = 'AppData/Roaming/Autodesk/ApplicationPlugins/TeplovPrint.bundle'
$nanoCad = 'AppData/Roaming/TeplovPrint/nanoCAD24'

Assert-Entries -ArchiveName "Teplov-Print-manual-install-v$Version.zip" -Required @(
    'INSTALL.md',
    'install.cmd',
    'uninstall.cmd',
    'LICENSE',
    "$bundle/PackageContents.xml",
    "$bundle/Contents/AutoCAD2015-2024/AcadBatchPlot.dll",
    "$bundle/Contents/AutoCAD2015-2024/Newtonsoft.Json.dll",
    "$bundle/Contents/AutoCAD2015-2024/PianNoCN.dll",
    "$bundle/Contents/AutoCAD2015-2024/pdfium.dll",
    "$bundle/Contents/AutoCAD2015-2024/Plotters/PIA3/LA_pdf.pc3",
    "$bundle/Contents/AutoCAD2025-2027/AcadBatchPlot.Core.dll",
    "$nanoCad/NcadBatchPlot.dll",
    "$nanoCad/Newtonsoft.Json.dll",
    "$nanoCad/PianNoCN.dll",
    "$nanoCad/pdfium.dll",
    "$nanoCad/Plotters/PIA3/LA_pdf.pc3"
)

Assert-Entries -ArchiveName "Teplov-Print-nanoCAD24-v$Version.zip" -Required @(
    'NcadBatchPlot.dll',
    'PianNoCN.dll',
    'pdfium.dll'
)

Assert-Entries -ArchiveName "Teplov-Print-AutoCAD2015-2024-v$Version.zip" -Required @(
    'AcadBatchPlot.dll',
    'PianNoCN.dll'
)

# Every DLL referenced by PackageContents.xml must exist in the bundle.
$manualEntries = Get-ArchiveEntries -ArchiveName "Teplov-Print-manual-install-v$Version.zip"
[xml]$packageContents = Get-Content -LiteralPath ([System.IO.Path]::Combine($root, 'installer_acad', 'PackageContents.xml')) -Raw -Encoding UTF8
foreach ($component in $packageContents.ApplicationPackage.Components.ComponentEntry) {
    $module = "$bundle/" + $component.ModuleName.TrimStart('.', '/')
    if ($manualEntries.Count -gt 0 -and $manualEntries -notcontains $module) {
        $failures.Add("PackageContents.xml references '$($component.ModuleName)', but '$module' is not packaged")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    throw "$($failures.Count) release package check(s) failed."
}

Write-Host "Release package layout is valid." -ForegroundColor Green
