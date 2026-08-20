<#
Apex BIM Studio — installer (round 5).
Run from an extracted package folder, in a normal PowerShell (no admin needed):

    powershell -ExecutionPolicy Bypass -File install.ps1 -RevitVersion 2025

What it does, in order:
 1. Verifies every file in the package against SHA256SUMS.txt (a damaged or
    altered download refuses to install).
 2. Copies the right build (net8 for Revit 2025+, net48 for 2022–2024) into
    %APPDATA%\Autodesk\Revit\Addins\<version>\ApexBimStudio\ and the .addin
    manifest beside it. Per-user install: no admin, no other users affected.
 3. Unblocks the copied files (Windows marks internet downloads; a blocked
    DLL silently fails to load in Revit).
 4. Installs the license file if one is provided (-LicenseFile) or included
    in the package; otherwise says where to put it later.
Nothing else is touched. Uninstall: uninstall.ps1 in this folder.
#>
param(
    [ValidateSet("2022","2023","2024","2025")]
    [string]$RevitVersion = "2025",
    [string]$LicenseFile = "",
    [switch]$SelfTest,
    [string]$PackageDir = ""
)
$ErrorActionPreference = "Stop"

# Shell gate: Windows PowerShell 5.1 (a clean machine's default) and
# PowerShell 7+ are supported. Anything older fails HERE, clearly, not
# halfway through an install. (Statically audited for 5.1: no ternary,
# null-coalescing, or chain operators anywhere in this script — those break
# 5.1 at parse time, before any line runs.)
$psv = $PSVersionTable.PSVersion
if ($psv.Major -lt 5 -or ($psv.Major -eq 5 -and $psv.Minor -lt 1)) {
    Write-Host "This installer needs Windows PowerShell 5.1 or PowerShell 7+. This shell is $psv."
    exit 1
}

$pkg = if ($PackageDir -ne "") { $PackageDir } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

if ($SelfTest) {
    # ~30-second machine readiness check. Makes NO changes to Revit or Apex
    # files (one temp probe file in %APPDATA%, deleted immediately).
    Write-Host "Apex installer self-test (no install actions will be taken)"
    Write-Host ("  shell: PowerShell " + $psv + " - supported")
    $sums = Join-Path $pkg "SHA256SUMS.txt"
    if (-not (Test-Path $sums)) {
        Write-Host "  FAIL: SHA256SUMS.txt not found in '$pkg'. Run with -PackageDir <extracted package folder>."
        exit 1
    }
    $rows = @(Get-Content $sums | Where-Object { $_.Trim() -ne "" })
    Write-Host ("  manifest: readable, " + $rows.Count + " entries")
    $probeHash = (Get-FileHash -Algorithm SHA256 $sums).Hash
    Write-Host ("  hashing: Get-FileHash works (manifest self-hash " + $probeHash.Substring(0, 12) + "...)")
    $probeFile = Join-Path $env:APPDATA ("apex-selftest-" + [guid]::NewGuid().ToString("N") + ".tmp")
    Set-Content -Path $probeFile -Value "probe"
    Remove-Item -Force $probeFile
    Write-Host ("  write access: OK under " + $env:APPDATA)
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
    if (Test-Path $addins) { Write-Host ("  Revit $RevitVersion addins folder: exists (" + $addins + ")") }
    else { Write-Host ("  Revit $RevitVersion addins folder: will be created at install (" + $addins + ")") }
    $licDir = Join-Path $env:ProgramData "Apex"
    if (Test-Path $licDir) { Write-Host ("  license folder: exists (" + $licDir + ")") }
    else { Write-Host ("  license folder: will be created at install; if that fails on a shared machine, an admin places the license") }
    Write-Host "SELF-TEST PASSED - this shell can run the installer."
    exit 0
}

Write-Host "Apex BIM Studio installer — package: $pkg"

# --- 1. integrity check (refuse a damaged package) ---
$sumsPath = Join-Path $pkg "SHA256SUMS.txt"
if (-not (Test-Path $sumsPath)) { throw "SHA256SUMS.txt missing — this is not a complete package. Re-download it." }
$bad = @()
$inManifest = @{}
foreach ($line in Get-Content $sumsPath) {
    if ($line.Trim() -eq "") { continue }
    $hash, $rel = $line -split '\s+', 2
    $rel = $rel.Trim().TrimStart('*')
    $inManifest[$rel] = $true
    $file = Join-Path $pkg $rel
    if (-not (Test-Path $file)) { $bad += "$rel (missing)"; continue }
    $actual = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
    if ($actual -ne $hash.ToLowerInvariant()) { $bad += "$rel (contents differ)" }
}
# Also refuse files the manifest does NOT list — an added file is as much
# tampering as a changed one (round-6 adversarial finding 2).
foreach ($f in Get-ChildItem -Recurse -File $pkg) {
    $rel = $f.FullName.Substring($pkg.Length + 1) -replace '\\', '/'
    if ($rel -eq "SHA256SUMS.txt") { continue }
    if (-not $inManifest.ContainsKey($rel)) { $bad += "$rel (present but NOT in the manifest)" }
}
if ($bad.Count -gt 0) {
    throw "The package failed its integrity check — do not install it. Re-download and try again.`nProblems:`n  " + ($bad -join "`n  ")
}
Write-Host "Package integrity: OK (every file matches SHA256SUMS.txt)"

# --- 2. copy the right build ---
$target = if ([int]$RevitVersion -ge 2025) { "net8" } else { "net48" }
$src = Join-Path $pkg $target
if (-not (Test-Path (Join-Path $src "ApexBimStudio.dll"))) { throw "Build folder '$target' is missing from the package." }
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$dest = Join-Path $addins "ApexBimStudio"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Force (Join-Path $src "*") $dest
Copy-Item -Force (Join-Path $pkg "ApexBimStudio.addin") $addins
Write-Host "Installed $target build for Revit $RevitVersion into: $dest"

# --- 3. unblock (SmartScreen zone marker prevents DLL load) ---
Get-ChildItem -Recurse $dest | Unblock-File
Unblock-File (Join-Path $addins "ApexBimStudio.addin")

# --- 4. license ---
$licDir = Join-Path $env:ProgramData "Apex"
$licDest = Join-Path $licDir "license.apexlic"
$licSrc = if ($LicenseFile -ne "") { $LicenseFile }
          elseif (Test-Path (Join-Path $pkg "license.apexlic")) { Join-Path $pkg "license.apexlic" }
          else { "" }
if ($licSrc -ne "") {
    New-Item -ItemType Directory -Force -Path $licDir | Out-Null
    Copy-Item -Force $licSrc $licDest
    Write-Host "License installed: $licDest"
} elseif (Test-Path $licDest) {
    Write-Host "Existing license kept: $licDest"
} else {
    Write-Host "NO LICENSE INSTALLED. The add-in loads but build commands will ask for one."
    Write-Host "Put the license.apexlic you received from Apex into: $licDir"
}

Write-Host ""
Write-Host "Done. Start Revit $RevitVersion — the 'Apex BIM Studio' tab appears on the ribbon."
Write-Host "Installed files:"
Get-ChildItem $dest | ForEach-Object { Write-Host ("  " + $_.Name) }
