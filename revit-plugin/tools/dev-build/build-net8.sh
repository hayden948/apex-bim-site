#!/usr/bin/env bash
# Container dev-build script (committed by the audit round so the /deterministic
# local recipe is in VCS). APEX_TOOLCHAIN_ROOT must point at the assembled
# toolchain (csc, ref packs, Revit API stubs, BouncyCastle) described in the
# ship ledger; the CANONICAL build remains CI dotnet build (revit-plugin.yml).
set -e
S=${APEX_TOOLCHAIN_ROOT:?set APEX_TOOLCHAIN_ROOT}
DOTNET=$S/dotnet-asm/dotnet/dotnet
CSC=$S/dotnet-asm/x/csc/tasks/netcore/bincore/csc.dll
REFS=$S/dotnet-asm/x/refs/ref/net8.0
WPF=$(ls -d $S/dotnet-asm/x/wpfrefs/ref/net8.0 2>/dev/null || true)
RAPI=$(find $S/dotnet-asm/x/revitapi -name RevitAPI.dll | head -1)
RAPIUI=$(find $S/dotnet-asm/x/revitapiui -name RevitAPIUI.dll | head -1)
PROT=$(find $S/dotnet-asm/x/protdata -path '*net8.0*' -name '*.dll' | head -1)
SRC=/home/user/apex-bim-site/revit-plugin/src
OUT=$S/build-net8
mkdir -p $OUT
rm -f $OUT/ApexBimStudio.dll
R=""
# Skip the core ref pack's stub WindowsBase (16KB compat shim, no types) —
# the real one comes from the WindowsDesktop ref pack; both on the command
# line makes csc's identity resolution flaky (CS7069).
for d in $REFS/*.dll; do case $d in *WindowsBase*) continue;; esac; R="$R /reference:$d"; done
if [ -n "$WPF" ]; then for d in $WPF/*.dll; do R="$R /reference:$d"; done; fi
"$DOTNET" "$CSC" /nologo /deterministic /target:library /nullable:enable /langversion:12 \
  /define:REVIT_2024_OR_GREATER /out:$OUT/ApexBimStudio.dll \
  $R /reference:/tmp/claude-0/-home-user-apex-bim-site/c660091d-69ee-5500-a827-87fb6f801ca2/scratchpad/bc/lib/net6.0/BouncyCastle.Cryptography.dll /reference:$RAPI /reference:$RAPIUI /reference:$PROT \
  $SRC/*.cs /tmp/claude-0/-home-user-apex-bim-site/c660091d-69ee-5500-a827-87fb6f801ca2/scratchpad/AssemblyInfoStamp.cs > $S/net8-build.log 2>&1
RC=$?
head -40 $S/net8-build.log
echo "net8 csc exit: $RC (dll present: $(test -f $OUT/ApexBimStudio.dll && echo yes || echo NO))"
exit $RC
