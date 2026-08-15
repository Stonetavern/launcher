#!/usr/bin/env bash
# Stonetavern Launcher — Windows-Release bauen: EINE Datei, kein Ordner.
#
# Spiegelt exakt die Ship-Shape aus .github/workflows/build.yml und CLAUDE.md §3, damit ein lokaler
# Build und der CI-Build dasselbe Artefakt erzeugen. Wer hier Flags ändert, ändert sie dort mit.
#
# Ergebnis: <out>/WowLauncher.exe — self-contained, kein .NET auf dem Zielsystem nötig, keine losen
# DLLs daneben, Icon eingebettet (ApplicationIcon in der csproj).
#
# 🔴 Verbotene Flags (nicht "optimieren"):
#   -p:PublishTrimmed=true                        -> historische AV-False-Positives
#   -p:IncludeNativeLibrariesForSelfExtract=true   -> triggert Defenders Dropper-Heuristik
#
# Aufruf:  deploy/publish-windows.sh [version] [outdir]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/WowLauncher/WowLauncher.csproj"

VERSION="${1:-}"
OUT="${2:-$ROOT/deploy/dist/win-x64}"

# Version ohne Argument aus der csproj ziehen — eine Quelle, nicht zwei. Nur echte Elementzeilen
# matchen: in der csproj steht "<Version>" auch in einem Kommentar, und ein loses grep zieht sich
# den Kommentartext als Version.
if [[ -z "$VERSION" ]]; then
  VERSION="$(grep -oPm1 '^\s*<Version>\K[^<]+' "$PROJECT")"
fi
[[ -n "$VERSION" ]] || { echo "FEHLER: keine Version ermittelbar" >&2; exit 1; }

# 🔴 AssemblyVersion MUSS mitgesetzt werden, nicht nur Version.
#
# Bis 2026-08-01 setzte dieser Publish ausschliesslich -p:Version. Die csproj trug daneben eine fest
# eingetragene <AssemblyVersion>1.1.0.0</AssemblyVersion>, und die gewinnt gegen ein nicht gesetztes
# Property. Der Self-Update verglich genau diese Zahl gegen das Manifest — jeder ausgelieferte Build
# meldete sich als 1.1.0.0, das Manifest sagte 1.5.1, also war IMMER ein Update faellig: laden,
# tauschen, neu starten, wieder 1.1.0.0. Windows-Spieler steckten in einer Endlosschleife
# (Akte: decisions/2026-08-01-update-loop-assemblyversion.md).
#
# Ein Release, das dieses Skript mit einem Versionsargument aufruft, wuerde die Divergenz sofort neu
# erzeugen — deshalb wird sie hier abgeleitet statt der csproj ueberlassen. AssemblyVersion verlangt
# rein numerische Stellen, also faellt ein etwaiges Suffix (1.6.1-rc1) weg.
ASM_VERSION="${VERSION%%-*}"
ASM_VERSION="${ASM_VERSION%%+*}"
[[ "$ASM_VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || {
  echo "FEHLER: '$VERSION' ergibt keine numerische AssemblyVersion ('$ASM_VERSION')" >&2; exit 1; }

echo "== Stonetavern Launcher $VERSION -> win-x64 (single file)"
rm -rf "$OUT"
mkdir -p "$OUT"

dotnet publish "$PROJECT" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishReadyToRun=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=embedded \
  -p:Version="$VERSION" \
  -p:AssemblyVersion="$ASM_VERSION" \
  -p:FileVersion="$ASM_VERSION" \
  -o "$OUT"

echo
echo "== Ergebnis"
ls -lh "$OUT"

# Gate gegen einen stillen Rückfall auf einen Ordner voller managed DLLs.
#
# 🔴 OFFENE OWNER-ENTSCHEIDUNG (2026-07-21): der Owner will EINE Exe. Gemessen: mit
#    -p:IncludeNativeLibrariesForSelfExtract=true liefert der Publish tatsaechlich genau eine
#    68-MB-Exe. Genau dieses Flag verbietet CLAUDE.md §3 aber, weil es beim Start native DLLs nach
#    %TEMP%\.net entpackt und damit Windows Defenders Dropper-Heuristik triggert - bei einem
#    Spiel-Launcher die teuerste aller Fehlalarm-Kategorien. Ohne das Flag bleiben drei native
#    Bibliotheken (Skia, HarfBuzz, ANGLE) neben der Exe liegen; die gehoeren zwingend mit
#    ausgeliefert. Bis die Abwaegung entschieden ist, baut dieses Skript regelkonform.
ALLOWED_NATIVE=(av_libglesv2.dll libHarfBuzzSharp.dll libSkiaSharp.dll)
UNEXPECTED=""
while IFS= read -r f; do
  base="$(basename "$f")"
  [[ "$base" == "WowLauncher.exe" ]] && continue
  printf '%s\n' "${ALLOWED_NATIVE[@]}" | grep -qx "$base" && continue
  UNEXPECTED+="  $base"$'\n'
done < <(find "$OUT" -maxdepth 1 -type f)

if [[ -n "$UNEXPECTED" ]]; then
  echo >&2
  echo "FEHLER: unerwartete Dateien neben der Exe - Single-File-Publish hat nicht gegriffen:" >&2
  printf '%s' "$UNEXPECTED" >&2
  exit 2
fi

EXE="$OUT/WowLauncher.exe"
[[ -f "$EXE" ]] || { echo "FEHLER: $EXE fehlt" >&2; exit 3; }

echo
echo "== Prüfung"
file "$EXE"
echo "Größe: $(du -h "$EXE" | cut -f1)"

# Icon-Gate: ohne eingebettete Icon-Ressource trägt die Exe das generische .NET-Symbol.
if command -v wrestool >/dev/null 2>&1; then
  if wrestool -l "$EXE" 2>/dev/null | grep -q -- '--type=14'; then
    echo "Icon:  eingebettet (Gruppe vorhanden)"
  else
    echo "FEHLER: kein Icon in der Exe — ApplicationIcon in der csproj prüfen." >&2
    exit 4
  fi
else
  echo "Icon:  nicht geprüft (wrestool fehlt; dnf install icoutils)"
fi

echo
echo "Fertig: $EXE"
