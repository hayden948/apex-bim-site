# Apex batch build launcher.
#
# Usage:  powershell -ExecutionPolicy Bypass -File run-batch.ps1 -BatchDir C:\apex\batch1 [-RevitVersion 2024]
#
# Sets APEX_BATCH_DIR (which makes BatchBuildCommand run without any dialog) and starts
# Revit. Two levels of automation:
#  A. Semi-scripted (default, reliable): Revit opens; you click Apex BIM Studio -> Submittals ->
#     Batch Build once. Everything after that click is unattended.
#  B. Fully headless (journal replay): record once, replay forever. Journal replay is
#     version- and id-sensitive, so the template ships with a placeholder:
#       1. Do one run of variant A with journaling on (Revit always journals).
#       2. Open the newest journal in %LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <ver>\Journals
#          and copy the line that looks like:
#            Jrn.RibbonEvent "Execute external command:CustomCtrl_%...ApexBatchBuild:Apex.BimStudio.Commands.BatchBuildCommand"
#       3. Paste it over the PLACEHOLDER line in journal-template.txt (same folder as this
#          script) and re-run with -Journal.
#     If the replayed journal stalls on a dialog, that dialog is a bug — report it; the
#     scripted path is supposed to be dialog-free.

param(
    [Parameter(Mandatory = $true)][string]$BatchDir,
    [string]$RevitVersion = "2024",
    [switch]$Journal
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $BatchDir)) { throw "Batch folder not found: $BatchDir" }
$env:APEX_BATCH_DIR = (Resolve-Path $BatchDir).Path
Write-Host "APEX_BATCH_DIR = $env:APEX_BATCH_DIR"

$revit = "C:\Program Files\Autodesk\Revit $RevitVersion\Revit.exe"
if (-not (Test-Path $revit)) { throw "Revit.exe not found at $revit (pass -RevitVersion)" }

if ($Journal) {
    $template = Join-Path $PSScriptRoot "journal-template.txt"
    $content = Get-Content $template -Raw
    if ($content -match "PLACEHOLDER") {
        throw "journal-template.txt still contains the PLACEHOLDER line - do the one-time recording (see header of this script)."
    }
    $run = Join-Path $env:TEMP "apex-batch-journal.txt"
    Set-Content -Path $run -Value $content -Encoding ASCII
    Write-Host "Replaying journal $run"
    Start-Process -FilePath $revit -ArgumentList "`"$run`"" -Wait
} else {
    Write-Host "Starting Revit. Click: Apex BIM Studio -> Submittals -> Batch Build (one click; the rest is unattended)."
    Start-Process -FilePath $revit -Wait
}

$matrix = Join-Path $env:APEX_BATCH_DIR "RUN_MATRIX.md"
if (Test-Path $matrix) {
    Write-Host "`n===== RUN_MATRIX.md ====="
    Get-Content $matrix | Write-Host
    Write-Host "`nArtifacts: $matrix ; $(Join-Path $env:APEX_BATCH_DIR 'batch-run.jsonl') ; quarantine\ ; out\"
} else {
    Write-Warning "RUN_MATRIX.md was not written - the batch did not complete. Check %LOCALAPPDATA%\Apex\logs and the Revit journal."
    exit 1
}
