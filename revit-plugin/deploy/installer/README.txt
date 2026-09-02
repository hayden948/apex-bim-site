Apex BIM Studio — install package
=================================

1. Right-click the downloaded zip -> Properties -> Unblock (if shown), then extract it.
2. Open PowerShell in the extracted folder and run:
       powershell -ExecutionPolicy Bypass -File install.ps1 -RevitVersion 2025
   (use your Revit year; 2022-2024 install the net48 build automatically)
3. Put the license.apexlic file you received from Apex into C:\ProgramData\Apex\
   (or pass -LicenseFile <path> to the installer).
4. Start Revit. The "Apex BIM Studio" tab appears; see QUICKSTART.md.

The installer verifies every file against SHA256SUMS.txt before copying anything,
installs per-user (no admin), and uninstall.ps1 removes it cleanly.
