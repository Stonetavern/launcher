#!/usr/bin/env bash
# WP0 — Linux Publish-Smoke (Mechagon / Stonetavern WowLauncher)
#
# Directory-Publish (KEIN Single-File) der UNVERAENDERTEN Codebasis als linux-x64-Target.
# Invarianten (AGENTS.md / PLAN §2): KEIN PublishTrimmed, KEIN PublishAot,
# KEIN IncludeNativeLibrariesForSelfExtract. Ohne SingleFile liegen die nativen
# Skia/HarfBuzz-Libs ohnehin als lose .so neben dem Binary -> genau das ist gewollt.
#
# Output: WowLauncher/bin/Release/net10.0/linux-x64/publish/
set -euo pipefail

ROOT="/AI/projects/wow/launcher"
PROJ="${ROOT}/WowLauncher/WowLauncher.csproj"
RID="linux-x64"

echo "== dotnet restore (${RID}) =="
dotnet restore -r "${RID}" "${PROJ}"

echo "== dotnet publish -c Release (${RID}, self-contained, Verzeichnis-Publish) =="
# Codex F3: die Schutz-Flags stehen NACH "$@". MSBuild wertet mehrfach angegebene -p:-Properties
# "last wins" aus — so kann ein durchgereichtes User-Argument (z.B. -p:PublishTrimmed=true) die
# Invarianten NICHT mehr ueberschreiben. PublishAot + IncludeNativeLibrariesForSelfExtract sind
# jetzt explizit auf false gepinnt (AGENTS.md / CLAUDE.md §3 — Defender-Heuristik).
dotnet publish -c Release -r "${RID}" "${PROJ}" \
  --self-contained true \
  "$@" \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  -p:PublishAot=false \
  -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:ContinuousIntegrationBuild=true \
  -p:DebugType=none

echo "== fertig =="
echo "Publish-Verzeichnis: ${ROOT}/WowLauncher/bin/Release/net10.0/${RID}/publish/"
