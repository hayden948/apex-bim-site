<#
Apex BIM Studio — clean uninstall (round 5).

    powershell -ExecutionPolicy Bypass -File uninstall.ps1 -RevitVersion 2025

Removes the add-in manifest and every file the installer copied for that Revit
version. By default the LICENSE and the Apex LOG FILES are kept (they are
yours, and reinstalling picks the license straight back up):
    -RemoveLicense   also deletes %ProgramData%\Apex\license.apexlic
    -RemoveLogs      also deletes %LOCALAPPDATA%\Apex\logs
Families you built (.rfa), build reports, and your drawings are NEVER touched.
#>
param(
    [ValidateSet("2022","2023","2024","2025","2026")]
    [string]$RevitVersion = "2025",
    [switch]$RemoveLicense,
    [switch]$RemoveLogs
)
$ErrorActionPreference = "Stop"
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$dest = Join-Path $addins "ApexBimStudio"
$manifest = Join-Path $addins "ApexBimStudio.addin"

$removed = @()
if (Test-Path $manifest) { Remove-Item -Force $manifest; $removed += $manifest }
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest; $removed += $dest }
if ($RemoveLicense) {
    $lic = Join-Path $env:ProgramData "Apex\license.apexlic"
    if (Test-Path $lic) { Remove-Item -Force $lic; $removed += $lic }
}
if ($RemoveLogs) {
    $logs = Join-Path $env:LOCALAPPDATA "Apex\logs"
    if (Test-Path $logs) { Remove-Item -Recurse -Force $logs; $removed += $logs }
}
if ($removed.Count -eq 0) {
    Write-Host "Nothing to remove — Apex BIM Studio is not installed for Revit $RevitVersion."
} else {
    Write-Host "Removed:"
    $removed | ForEach-Object { Write-Host ("  " + $_) }
    if (-not $RemoveLicense) { Write-Host "License kept (use -RemoveLicense to delete it)." }
    if (-not $RemoveLogs) { Write-Host "Apex logs kept (use -RemoveLogs to delete them)." }
}
Write-Host "Your families, build reports, and drawings were not touched."
