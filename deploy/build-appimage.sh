#!/usr/bin/env bash
# WP5b — AppImage-Bau (Mechagon / Stonetavern WowLauncher, Linux-Spur)  [V1-Komfortformat]
#
# Baut aus dem linux-x64-Directory-Publish ein Typ-2-AppImage. Wir erzeugen KEINE
# FUSE2-Abhängigkeit selbst — moderner appimagetool (statisches runtime, FUSE3/
# extract-and-run-fähig). Payload behält seine glibc-Untergrenze (Ubuntu-22.04-Ära).
#
# STATUS: BLOCKED, solange 'appimagetool' fehlt. Dieses Skript ist bis zum Tool-Aufruf
# getestet (AppDir-Aufbau läuft); der letzte Schritt ruft appimagetool.
#
# Codex F7: KEINE mutable 'continuous'-Binary mehr. Das Tool wird auf ein GETAGGTES Release
# gepinnt und per sha256 verifiziert (eine mutable URL kann sich unter dir ändern — Lieferkette).
# Installation (durch den User, KEIN sudo nötig) — Version + Hash sind unten in APPIMGTOOL_* gepinnt:
#   VER=1.9.1
#   SHA=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
#   mkdir -p ~/.local/bin
#   curl -L -o ~/.local/bin/appimagetool \
#     "https://github.com/AppImage/appimagetool/releases/download/${VER}/appimagetool-x86_64.AppImage"
#   echo "${SHA}  $HOME/.local/bin/appimagetool" | sha256sum -c -   # MUSS 'OK' sagen
#   chmod +x ~/.local/bin/appimagetool
#   # (falls FUSE fehlt: einmalig mit --appimage-extract-and-run testen)
# Hash-Herkunft: GitHub-Release-Asset-Digest (AppImage/appimagetool 1.9.1) — beim Pinnen einer
# neuen Version via `gh api repos/AppImage/appimagetool/releases` das Feld .assets[].digest ziehen.
#
# Das erzeugte AppImage wird nach dem Bau selbst gehasht und in deploy/dist/SHA256SUMS aufgenommen;
# release-check.sh --release scannt/signiert es zusätzlich (Codex F7/F3).
#
# Invarianten: KEINE Blizzard-Assets; Icon = launcher-eigene Marke.
# Keine Codeänderung an WowLauncher/.
set -euo pipefail
export LC_ALL=C TZ=UTC

# Gepinntes appimagetool (Codex F7) — Version + sha256 des offiziellen Release-Assets.
APPIMGTOOL_VER="1.9.1"
APPIMGTOOL_SHA="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
APPIMGTOOL_URL="https://github.com/AppImage/appimagetool/releases/download/${APPIMGTOOL_VER}/appimagetool-x86_64.AppImage"

ROOT="(internal design notes, not published)"
RID="linux-x64"
PUBLISH_DEFAULT="${ROOT}/WowLauncher/bin/Release/net10.0/${RID}/publish"
PUBLISH="${PUBLISH:-$PUBLISH_DEFAULT}"
OUTDIR="${OUTDIR:-${ROOT}/deploy/dist}"
ASSET_ICON="${ROOT}/deploy/assets/stonetavern-launcher.png"
APPID="stonetavern-launcher"

log() { printf '%s\n' "== $* =="; }
fail() { printf 'build-appimage: FEHLER: %s\n' "$*" >&2; exit 1; }

# Version (gleiche Logik wie package-linux.sh, ohne Duplikat-Pflege: dort delegieren wäre
# ideal, hier bewusst schlank gehalten — ENV > csproj > gebautes AssemblyInfo).
read_version() {
  [[ -n "${STONETAVERN_LAUNCHER_VERSION:-}" ]] && { printf '%s' "$STONETAVERN_LAUNCHER_VERSION"; return; }
  local cs="${ROOT}/WowLauncher/WowLauncher.csproj" v=""
  v="$(grep -oP '<Version(Prefix)?>\K[0-9]+\.[0-9]+(\.[0-9]+)?' "$cs" 2>/dev/null | head -1)"
  [[ -z "$v" ]] && v="$(grep -oP 'AssemblyVersionAttribute\("\K[0-9]+\.[0-9]+\.[0-9]+' \
      "${ROOT}/WowLauncher/obj/Release/net10.0/${RID}/WowLauncher.AssemblyInfo.cs" 2>/dev/null | head -1)"
  printf '%s' "$v"
}
VERSION="$(read_version)"; [[ -n "$VERSION" ]] || fail "Version nicht bestimmbar (Build zuerst)."

[[ -d "$PUBLISH" ]] || fail "Publish fehlt: $PUBLISH"
[[ -f "$PUBLISH/WowLauncher" ]] || fail "apphost fehlt im Publish"
[[ -f "$ASSET_ICON" ]] || fail "Icon fehlt: $ASSET_ICON"

# Do not turn an old publish directory into a brand-new-looking AppImage. This happened twice on
# 2026-08-04: appimagetool succeeded, but the resulting artifact contained an earlier launcher.
# The tarball path has had this gate since then; the AppImage is the player-facing Linux format and
# must reject the same two lies: uncommitted source and a publish older than its source commit.
if git -C "$ROOT" rev-parse --git-dir >/dev/null 2>&1; then
  if [[ -n "$(git -C "$ROOT" status --porcelain -- WowLauncher/ 2>/dev/null)" ]]; then
    fail "Working-Tree unter WowLauncher/ ist dirty — uncommittete Quelle wird NICHT verpackt. Erst committen, dann deploy/publish-linux.sh."
  fi
  SRC_CT="$(git -C "$ROOT" log -1 --format=%ct -- WowLauncher/ 2>/dev/null || echo '')"
  APPHOST_MTIME="$(stat -c%Y "$PUBLISH/WowLauncher" 2>/dev/null || echo 0)"
  if [[ -n "$SRC_CT" ]] && (( APPHOST_MTIME < SRC_CT )); then
    fail "Publish stale — der Publish ist älter als der jüngste WowLauncher/-Commit. Erst deploy/publish-linux.sh."
  fi
fi

mkdir -p "$OUTDIR"
APPDIR="$(mktemp -d /tmp/${APPID}-AppDir.XXXXXX)/${APPID}.AppDir"
trap 'rm -rf "$(dirname "$APPDIR")"' EXIT
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/512x512/apps" \
         "$APPDIR/usr/share/icons/hicolor/256x256/apps" \
         "$APPDIR/usr/share/icons/hicolor/128x128/apps"

log "AppDir-Payload"
cp -R "$PUBLISH/." "$APPDIR/usr/bin/"
chmod 0755 "$APPDIR/usr/bin/WowLauncher"
find "$APPDIR/usr/bin" -type f -name '*.so' -exec chmod 0755 {} +

# Icon (Top-Level + hicolor; appimagetool erwartet .DirIcon/Top-Level-Icon).
# Drei Groessen, damit Dateimanager und Docks nicht die 256er hochskalieren
# muessen — ein weichgezeichnetes Icon ist das Erste, was billig aussieht.
ICON_512="${ROOT}/deploy/assets/stonetavern-launcher-512.png"
ICON_128="${ROOT}/deploy/assets/stonetavern-launcher-128.png"
[[ -f "$ICON_512" ]] || fail "Icon 512 fehlt: $ICON_512 (deploy/assets/make-icon.py)"
[[ -f "$ICON_128" ]] || fail "Icon 128 fehlt: $ICON_128 (deploy/assets/make-icon.py)"
cp "$ICON_512"   "$APPDIR/usr/share/icons/hicolor/512x512/apps/${APPID}.png"
cp "$ASSET_ICON" "$APPDIR/usr/share/icons/hicolor/256x256/apps/${APPID}.png"
cp "$ICON_128"   "$APPDIR/usr/share/icons/hicolor/128x128/apps/${APPID}.png"
cp "$ASSET_ICON" "$APPDIR/${APPID}.png"
cp "$ICON_512"   "$APPDIR/.DirIcon"

# .desktop (Top-Level + usr/share/applications)
cat > "$APPDIR/usr/share/applications/${APPID}.desktop" <<DESKTOPEOF
[Desktop Entry]
Type=Application
Name=Stonetavern Launcher
Comment=Launcher for the Stonetavern realm (Vanilla WoW 1.12.1)
Exec=WowLauncher
Icon=${APPID}
Terminal=false
Categories=Game;
StartupNotify=true
StartupWMClass=stonetavern-launcher
DESKTOPEOF
cp "$APPDIR/usr/share/applications/${APPID}.desktop" "$APPDIR/${APPID}.desktop"

# AppRun → apphost, cwd-neutral, argument-durchreichend
cat > "$APPDIR/AppRun" <<'APPRUNEOF'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "${0}")")"
exec "${HERE}/usr/bin/WowLauncher" "$@"
APPRUNEOF
chmod 0755 "$APPDIR/AppRun"

log "AppDir aufgebaut: $APPDIR"
find "$APPDIR" -maxdepth 2 | sed "s|$APPDIR|  AppDir|" | sort

# --- Tool-Aufruf ------------------------------------------------------------
APPIMGTOOL=""
for c in appimagetool "$HOME/.local/bin/appimagetool" "$HOME/Applications/appimagetool"; do
  # Den AUFGELOESTEN Pfad merken, nicht den Kandidaten-String: steht das Tool im PATH, war
  # APPIMGTOOL frueher der blosse Name "appimagetool" — und die Hash-Pruefung unten rief dann
  # `sha256sum appimagetool` auf eine Datei, die im Arbeitsverzeichnis gar nicht liegt. Ergebnis:
  # leerer Hash, Abbruch mit "Hash != gepinnt", und der Bau galt faelschlich als BLOCKED, obwohl
  # das Tool korrekt installiert war (2026-07-21 real erlitten).
  if resolved="$(command -v "$c" 2>/dev/null)"; then APPIMGTOOL="$resolved"; break; fi
done
if [[ -z "$APPIMGTOOL" ]]; then
  printf '\nbuild-appimage: BLOCKED — appimagetool nicht gefunden.\n' >&2
  printf 'AppDir ist fertig aufgebaut und validiert. Zum Fertigstellen (gepinnt + hash-verifiziert):\n' >&2
  printf '  mkdir -p ~/.local/bin\n' >&2
  printf '  curl -L -o ~/.local/bin/appimagetool "%s"\n' "$APPIMGTOOL_URL" >&2
  printf '  echo "%s  $HOME/.local/bin/appimagetool" | sha256sum -c -   # MUSS OK sein\n' "$APPIMGTOOL_SHA" >&2
  printf '  chmod +x ~/.local/bin/appimagetool\n' >&2
  printf 'Dann dieses Skript erneut ausführen.\n' >&2
  exit 3
fi

# Codex F7 + Re-Gate: der Pin ist ERZWUNGEN — ein Tool mit fremdem Hash wird NICHT
# ausgeführt (ein Release-Werkzeug ohne verifizierte Herkunft ist ein Supply-Risiko).
if command -v sha256sum >/dev/null 2>&1; then
  got_sha="$(sha256sum "$APPIMGTOOL" 2>/dev/null | awk '{print $1}')"
  if [[ "$got_sha" == "$APPIMGTOOL_SHA" ]]; then
    log "appimagetool sha256 verifiziert (== gepinnt ${APPIMGTOOL_VER})"
  else
    printf 'build-appimage: FEHLER — appimagetool-Hash != gepinnt (%s). ABBRUCH.\n' "${APPIMGTOOL_VER}" >&2
    printf '  gefunden : %s\n  erwartet : %s\n' "${got_sha:-?}" "$APPIMGTOOL_SHA" >&2
    printf '  Ein Release-Werkzeug ohne verifizierte Herkunft wird nicht ausgeführt — gepinntes Asset installieren (s.o.).\n' >&2
    exit 4
  fi
else
  printf 'build-appimage: FEHLER — sha256sum fehlt, Tool-Pin nicht verifizierbar. ABBRUCH.\n' >&2
  exit 4
fi

OUT="${OUTDIR}/${APPID}-${VERSION}-x86_64.AppImage"
log "appimagetool: $APPIMGTOOL → $OUT"
# Deterministischer Timestamp; kein GPG-Sign hier (macht release-check über SHA256SUMS).
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --format=%ct 2>/dev/null || echo 1700000000)}"
ARCH=x86_64 "$APPIMGTOOL" --no-appstream "$APPDIR" "$OUT"

OUT_SHA="$(sha256sum "$OUT" | awk '{print $1}')"
printf 'AppImage: %s (%s Bytes)\n' "$OUT" "$(stat -c%s "$OUT")"
printf 'SHA256  : %s\n' "$OUT_SHA"

# Codex F7: erzeugtes AppImage in deploy/dist/SHA256SUMS aufnehmen (dedupliziert). release-check
# regeneriert SHA256SUMS ohnehin autoritativ, wenn ein AppImage in dist/ liegt — dies hier stellt
# sicher, dass der Hash auch ohne unmittelbaren release-check-Lauf festgehalten ist.
SUMS="${OUTDIR}/SHA256SUMS"
base="$(basename "$OUT")"
tmp="$(mktemp)"; touch "$SUMS"
grep -v -- "  ${base}\$" "$SUMS" > "$tmp" 2>/dev/null || true
printf '%s  %s\n' "$OUT_SHA" "$base" >> "$tmp"
sort -k2 "$tmp" > "$SUMS"; rm -f "$tmp"
printf 'SHA256SUMS aktualisiert: %s\n' "$SUMS"
