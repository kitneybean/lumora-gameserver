#!/usr/bin/env bash
# Builds self-contained single-file Lumora Agent archives into artifacts/.
# Usage: build/publish.sh <version> <rid>...   e.g. build/publish.sh 0.1.0 linux-x64 win-x64
set -euo pipefail
VERSION="${1:?version}"; shift
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts"
rm -rf "$OUT"; mkdir -p "$OUT"
for RID in "$@"; do
  DIR="$ROOT/publish/$RID"
  rm -rf "$DIR"
  dotnet publish "$ROOT/src/Lumora.Agent/Lumora.Agent.csproj" -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true -p:Version="$VERSION" -p:DebugType=none -o "$DIR"
  if [[ "$RID" == win-* ]]; then
    (cd "$DIR" && zip -q -r "$OUT/lumora-agent-$RID.zip" .)
  else
    tar -czf "$OUT/lumora-agent-$RID.tar.gz" -C "$DIR" .
  fi
done
(cd "$OUT" && sha256sum lumora-agent-* > checksums.txt)
ls -l "$OUT"
