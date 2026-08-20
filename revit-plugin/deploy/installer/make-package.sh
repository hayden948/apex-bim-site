#!/usr/bin/env bash
# Assemble the Apex BIM Studio install package (round 5).
#
#   make-package.sh <version> <net48-bin-dir> <net8-bin-dir> <out-dir>
#
# The bin dirs are `dotnet build -c Release` outputs (CopyLocalLockFileAssemblies
# is on, so every runtime dependency is already beside the DLL). The CANONICAL
# customer package must be assembled from the Windows CI artifacts of the tagged
# release commit — a package assembled from any other build is for testing only.
# Layout produced:
#   ApexBimStudio-<version>/
#     install.ps1 uninstall.ps1 README.txt ApexBimStudio.addin
#     net48/  (Revit 2022–2024)      net8/  (Revit 2025+)
#     SHA256SUMS.txt                 (verified by install.ps1 before any copy)
set -euo pipefail
VER="$1"; N48="$2"; N8="$3"; OUT="$4"
HERE="$(cd "$(dirname "$0")" && pwd)"
PKG="$OUT/ApexBimStudio-$VER"
rm -rf "$PKG"; mkdir -p "$PKG/net48" "$PKG/net8"
cp "$N48"/*.dll "$PKG/net48/"
cp "$N8"/*.dll "$PKG/net8/"
# Reference-only Revit API assemblies must never ship (license + version safety).
rm -f "$PKG"/net*/RevitAPI.dll "$PKG"/net*/RevitAPIUI.dll
# Refuse to produce a package that would install cleanly and then fail at
# first click (round-5 adversarial finding 1): every runtime dependency the
# code binds must be present in the folder it ships from.
for f in ApexBimStudio.dll BouncyCastle.Cryptography.dll; do
  [ -f "$PKG/net48/$f" ] || { echo "FATAL: net48/$f missing — incomplete build output; not packaging." >&2; exit 1; }
  [ -f "$PKG/net8/$f" ]  || { echo "FATAL: net8/$f missing — incomplete build output; not packaging." >&2; exit 1; }
done
# net48 only: package-provided there. On net8.0-windows ProtectedData ships in
# the Microsoft.WindowsDesktop.App shared framework Revit 2025 hosts, so the
# SDK rightly does not copy it (this guard's first CI run proved that).
for f in System.Security.Cryptography.ProtectedData.dll System.Text.Json.dll System.Memory.dll System.Buffers.dll System.Text.Encodings.Web.dll; do
  [ -f "$PKG/net48/$f" ] || { echo "FATAL: net48/$f missing (runtime dependency) — not packaging." >&2; exit 1; }
done
cp "$HERE/install.ps1" "$HERE/uninstall.ps1" "$PKG/"
cp "$HERE/../ApexBimStudio.addin" "$PKG/"
cp "$HERE/README.txt" "$PKG/" 2>/dev/null || true
cp "$HERE/../QUICKSTART.md" "$HERE/../KNOWN_LIMITATIONS.md" "$PKG/" 2>/dev/null || true
( cd "$PKG" && find . -type f ! -name SHA256SUMS.txt -print0 | sort -z \
  | xargs -0 sha256sum | sed 's|\./||' > SHA256SUMS.txt )
( cd "$OUT" && rm -f "ApexBimStudio-$VER.zip" \
  && python3 -c "import shutil; shutil.make_archive('ApexBimStudio-$VER','zip','.', 'ApexBimStudio-$VER')" )
echo "package: $OUT/ApexBimStudio-$VER.zip"
sha256sum "$OUT/ApexBimStudio-$VER.zip"
