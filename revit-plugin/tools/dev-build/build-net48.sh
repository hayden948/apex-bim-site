#!/usr/bin/env bash
# Container dev-build script (committed by the audit round so the /deterministic
# local recipe is in VCS). APEX_TOOLCHAIN_ROOT must point at the assembled
# toolchain (csc, ref packs, Revit API stubs, BouncyCastle) described in the
# ship ledger; the CANONICAL build remains CI dotnet build (revit-plugin.yml).
set -e
S=${APEX_TOOLCHAIN_ROOT:?set APEX_TOOLCHAIN_ROOT}
DOTNET=$S/dotnet-asm/dotnet/dotnet
CSC=$S/dotnet-asm/x/csc/tasks/netcore/bincore/csc.dll
N48=$(find $S/dotnet-asm/x/net48refs -type d -name net48 | grep -i 'ref' | head -1)
[ -z "$N48" ] && N48=$(dirname $(find $S/dotnet-asm/x/net48refs -name mscorlib.dll | head -1))
RAPI=$(find $S/dotnet-asm/x/revitapi22 -name RevitAPI.dll | head -1)
RAPIUI=$(find $S/dotnet-asm/x/revitapiui22 -name RevitAPIUI.dll | head -1)
STJ=$(find $S/dotnet-asm/x/stj -path '*net462*' -name System.Text.Json.dll | head -1)
SRC=/home/user/apex-bim-site/revit-plugin/src
OUT=$S/build-net48
mkdir -p $OUT
rm -f $OUT/ApexBimStudio.dll
R=""
for d in $N48/*.dll; do case $d in *EnterpriseServices*) continue;; esac; R="$R /reference:$d"; done
for pkg in sysmem sysbuf sysvt sysnum systask sysunsafe bclasync encweb; do
  DLL=$(find $S/dotnet-asm/x/$pkg -path '*net4*' -name '*.dll' | grep -v resources | head -1)
  [ -n "$DLL" ] && R="$R /reference:$DLL"
done
if ! "$DOTNET" "$CSC" /nologo /deterministic /target:library /nullable:enable /langversion:12 \
  /out:$OUT/ApexBimStudio.dll \
  $R /reference:/tmp/claude-0/-home-user-apex-bim-site/c660091d-69ee-5500-a827-87fb6f801ca2/scratchpad/bc/lib/net461/BouncyCastle.Cryptography.dll /reference:$RAPI /reference:$RAPIUI /reference:$STJ \
  $SRC/*.cs /tmp/claude-0/-home-user-apex-bim-site/c660091d-69ee-5500-a827-87fb6f801ca2/scratchpad/AssemblyInfoStamp.cs > $S/net48-build.log 2>&1; then
  echo "NET48 COMPILE FAILED:"
  head -40 $S/net48-build.log
  exit 1
fi
echo "net48 csc exit: 0 (dll present: $(test -f $OUT/ApexBimStudio.dll && echo yes || echo NO))"
