#!/usr/bin/env bash
# Repair Phase 1 — Datei-Manifest-Generator (MANIFEST-SCHEMA.md §files_url)
#
# Erzeugt aus einem bereits ENTPACKTEN Client-Verzeichnis ein "files.json" im vereinbarten Schema
# ({ build, version, files: [{ path, size, sha256 }] }). Diese Datei ist die Voraussetzung dafür,
# dass der Launcher-Repair-Knopf pruefen kann, WELCHE Datei kaputt ist, statt immer das ganze
# Mehr-GB-ZIP neu zu laden.
#
# Nutzung:
#   deploy/make-file-manifest.sh <entpackter-client-ordner> <build-nummer> [version] [ausgabe-datei]
#
# Beispiel:
#   deploy/make-file-manifest.sh /srv/clients/vanilla-1.12.1 5875 1.12.1 deploy/dist/vanilla-1.12.1-files.json
#
# Deterministisch: die Dateiliste wird nach Pfad sortiert, damit zwei Läufe über denselben Baum
# byte-identisches JSON erzeugen (git-diff-freundlich, reproduzierbar).

set -euo pipefail

CLIENT_DIR="${1:-}"
BUILD="${2:-}"
VERSION="${3:-}"
OUT_FILE="${4:-files.json}"

if [[ -z "$CLIENT_DIR" || -z "$BUILD" ]]; then
    echo "Nutzung: $0 <entpackter-client-ordner> <build-nummer> [version] [ausgabe-datei]" >&2
    exit 1
fi

if [[ ! -d "$CLIENT_DIR" ]]; then
    echo "Fehler: '$CLIENT_DIR' ist kein Verzeichnis." >&2
    exit 1
fi

if ! command -v sha256sum >/dev/null 2>&1; then
    echo "Fehler: sha256sum fehlt (coreutils)." >&2
    exit 1
fi

# Absoluter Pfad, damit die relativen Pfade unten sauber abgeschnitten werden können — egal
# von wo das Skript aufgerufen wird.
CLIENT_DIR="$(cd "$CLIENT_DIR" && pwd)"

echo "Scanne $CLIENT_DIR (Build $BUILD) …" >&2

# Alle regulären Dateien, NUL-getrennt (robust gegen Leerzeichen/Sonderzeichen im Dateinamen),
# dann nach relativem Pfad sortiert — die Sortierung MUSS vor dem Schreiben passieren, damit die
# Ausgabe deterministisch bleibt (nicht die Dateisystem-Traversierungsreihenfolge).
mapfile -d '' -t FILES < <(find "$CLIENT_DIR" -type f -print0 | sort -z)

TMP_JSON="$(mktemp)"
trap 'rm -f "$TMP_JSON"' EXIT

{
    printf '{\n'
    printf '  "build": %s,\n' "$BUILD"
    printf '  "version": %s,\n' "$(printf '%s' "$VERSION" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))' 2>/dev/null || printf '"%s"' "$VERSION")"
    printf '  "files": [\n'

    total=${#FILES[@]}
    i=0
    for f in "${FILES[@]}"; do
        i=$((i + 1))
        # Relativer Pfad mit "/" als Trenner (Wire-Konvention, siehe MANIFEST-SCHEMA.md) — auf Linux
        # ist das schon der Fall, aber wir ersetzen defensiv, falls das Skript je auf einem anderen
        # Trenner läuft.
        rel="${f#"$CLIENT_DIR"/}"
        rel="${rel//\\//}"
        size="$(stat -c%s "$f")"
        hash="$(sha256sum "$f" | cut -d' ' -f1)"

        # JSON-Pfad-Escaping (Backslash, Anführungszeichen) — Dateinamen im Client-Baum sind in der
        # Praxis ASCII, aber ein Addon-Pfad mit Sonderzeichen soll nicht stillschweigend kaputtes
        # JSON erzeugen.
        rel_escaped="${rel//\\/\\\\}"
        rel_escaped="${rel_escaped//\"/\\\"}"

        if [[ "$i" -eq "$total" ]]; then
            printf '    { "path": "%s", "size": %s, "sha256": "%s" }\n' "$rel_escaped" "$size" "$hash"
        else
            printf '    { "path": "%s", "size": %s, "sha256": "%s" },\n' "$rel_escaped" "$size" "$hash"
        fi
    done

    printf '  ]\n'
    printf '}\n'
} > "$TMP_JSON"

mkdir -p "$(dirname "$OUT_FILE")"
mv "$TMP_JSON" "$OUT_FILE"
trap - EXIT

echo "Fertig: $OUT_FILE (${#FILES[@]} Dateien)" >&2
