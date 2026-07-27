#!/usr/bin/env bash
# WP5b — deterministischer Release-Tarball (Mechagon / Stonetavern WowLauncher, Linux-Spur)
#
# Baut aus dem linux-x64-Directory-Publish ein reproduzierbares
#   stonetavern-launcher-<version>-linux-x64.tar.xz
# mit Wrapper-Verzeichnis stonetavern-launcher/, README, LICENSE und optionaler
# .desktop-/Icon-Integration (Doku im README, KEINE Auto-Installation).
#
# Determinismus (Reproducible-Builds-Konvention + Research-Dossier
#   research/RESEARCH-linux-security-hygiene-2026-07-17.md §"Tar-Inhalte deterministisch"):
#   - sortierte Reihenfolge (--sort=name, LC_ALL=C)
#   - Owner/Group 0/0 numerisch  (--owner=0 --group=0 --numeric-owner)
#   - fester mtime (SOURCE_DATE_EPOCH; Default = HEAD-Commit-Zeit)
#   - normalisierte Modes (Verzeichnisse/apphost/*.so/*.sh 0755, Rest 0644)
#   - keine xattrs/ACLs/SELinux-Labels (--no-xattrs --no-acls --no-selinux)
#   - xz einzelthreadig (-T1) → byte-identisch über Läufe
#
# Invarianten (AGENTS.md / PLAN §2): KEIN SingleFile, KEIN Packer/UPX, KEINE
# Blizzard-Assets. Das Icon ist die launcher-eigene Marke (lantern.png-Ableitung).
#
# Keine Codeänderung an WowLauncher/. Read-only ggü. dem Publish (Staging-Kopie).
set -euo pipefail
export LC_ALL=C TZ=UTC

ROOT="/AI/projects/wow/launcher"
RID="linux-x64"
PUBLISH_DEFAULT="${ROOT}/WowLauncher/bin/Release/net10.0/${RID}/publish"
PUBLISH="${PUBLISH:-$PUBLISH_DEFAULT}"
OUTDIR="${OUTDIR:-${ROOT}/deploy/dist}"
WRAPPER="stonetavern-launcher"
ASSET_ICON="${ROOT}/deploy/assets/stonetavern-launcher.png"
LICENSE_SRC="${ROOT}/LICENSE"

log() { printf '%s\n' "== $* =="; }
fail() { printf 'package-linux: FEHLER: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# 1) Version bestimmen (Präzedenz: ENV > csproj > GEBAUTE Assembly im Publish)
#     Codex F5: Quelle des Fallbacks ist das GEBAUTE Artefakt (Publish), NICHT obj/ —
#     obj/ kann von einem alten/anderen Build stammen und lügt dann über das, was wirklich
#     im Tarball landet. Der Publish enthält WowLauncher.deps.json, die der Build zusammen
#     mit der Assembly erzeugt und in der die Projekt-Version ("WowLauncher/<ver>") steht.
#     Danach GEGEN EIN STRIKTES SCHEMA validiert (SemVer + optionaler Pre/Build-Suffix) —
#     das Schema verbietet zugleich jedes Pfad-Sonderzeichen (/, .., Whitespace, Shell-Meta).
# ---------------------------------------------------------------------------
VERSION_RE='^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.]+)?$'
read_version() {
  if [[ -n "${STONETAVERN_LAUNCHER_VERSION:-}" ]]; then
    printf '%s' "$STONETAVERN_LAUNCHER_VERSION"; return 0
  fi
  local csproj="${ROOT}/WowLauncher/WowLauncher.csproj" v=""
  v="$(grep -oP '<Version>\K[0-9]+\.[0-9]+(\.[0-9]+)?' "$csproj" 2>/dev/null | head -1)"
  [[ -z "$v" ]] && v="$(grep -oP '<VersionPrefix>\K[0-9]+\.[0-9]+(\.[0-9]+)?' "$csproj" 2>/dev/null | head -1)"
  [[ -z "$v" ]] && v="$(grep -oP '<AssemblyVersion>\K[0-9]+\.[0-9]+(\.[0-9]+)?' "$csproj" 2>/dev/null | head -1)"
  if [[ -z "$v" ]]; then
    # Fallback: die vom Build erzeugte deps.json IM PUBLISH (authoritative fürs Artefakt).
    local deps="${PUBLISH}/WowLauncher.deps.json"
    v="$(grep -oP '"WowLauncher/\K[0-9][0-9A-Za-z.\-]*(?=":)' "$deps" 2>/dev/null | head -1)"
  fi
  [[ -z "$v" ]] && return 1
  printf '%s' "$v"
}

VERSION="$(read_version)" || fail "Version weder in csproj noch in der gebauten deps.json im Publish gefunden (Build zuerst laufen lassen)."
if [[ ! "$VERSION" =~ $VERSION_RE ]]; then
  fail "Version '$VERSION' verletzt das Release-Schema (${VERSION_RE}) — kein SemVer / verbotene Zeichen. Abbruch."
fi

# ---------------------------------------------------------------------------
# 2) SOURCE_DATE_EPOCH (fester Timestamp)
# ---------------------------------------------------------------------------
if [[ -z "${SOURCE_DATE_EPOCH:-}" ]]; then
  if SOURCE_DATE_EPOCH="$(git -C "$ROOT" log -1 --format=%ct 2>/dev/null)" && [[ -n "$SOURCE_DATE_EPOCH" ]]; then
    :
  else
    SOURCE_DATE_EPOCH=1700000000   # deterministischer Fallback (2023-11-14)
  fi
fi
export SOURCE_DATE_EPOCH

# ---------------------------------------------------------------------------
# 3) Publish prüfen
# ---------------------------------------------------------------------------
[[ -d "$PUBLISH" ]] || fail "Publish-Verzeichnis fehlt: $PUBLISH — erst deploy/publish-linux.sh laufen lassen."
[[ -x "$PUBLISH/WowLauncher" || -f "$PUBLISH/WowLauncher" ]] || fail "apphost 'WowLauncher' fehlt im Publish: $PUBLISH"
[[ -f "$LICENSE_SRC" ]] || fail "LICENSE fehlt: $LICENSE_SRC"
[[ -f "$ASSET_ICON" ]] || fail "Icon fehlt: $ASSET_ICON (deploy/assets/ — launcher-eigene Marke)"

# ---------------------------------------------------------------------------
# 3b) Stale-Publish-Schutz (Codex F4 — real passiert: Publish von 839d951 verpackt,
#     HEAD war 9fef322). Zwei Fallen schließen:
#     (a) Working-Tree unter WowLauncher/ dirty  → wir würden Quelle verpacken, die nicht
#         committet ist (nicht reproduzierbar, nicht nachvollziehbar).
#     (b) Publish ÄLTER als der jüngste WowLauncher/-Quell-Commit → der Publish spiegelt
#         eine überholte Quelle; das Artefakt lügt über seinen Stand.
#     Beides = harter Abbruch mit Klartext „erst deploy/publish-linux.sh".
#     BUILD_COMMIT/BUILD_DIRTY wandern zusätzlich in die BUILDINFO (Abschnitt 4).
# ---------------------------------------------------------------------------
BUILD_COMMIT="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
BUILD_COMMIT_SHORT="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
BUILD_DIRTY="no"
if git -C "$ROOT" rev-parse --git-dir >/dev/null 2>&1; then
  if [[ -n "$(git -C "$ROOT" status --porcelain -- WowLauncher/ 2>/dev/null)" ]]; then
    BUILD_DIRTY="yes"
    fail "Working-Tree unter WowLauncher/ ist dirty — uncommittete Quelle wird NICHT verpackt. Erst committen, dann deploy/publish-linux.sh, dann packen."
  fi
  SRC_CT="$(git -C "$ROOT" log -1 --format=%ct -- WowLauncher/ 2>/dev/null || echo '')"
  APPHOST_MTIME="$(stat -c%Y "$PUBLISH/WowLauncher" 2>/dev/null || echo 0)"
  if [[ -n "$SRC_CT" ]] && (( APPHOST_MTIME < SRC_CT )); then
    fail "Publish stale — der Publish (apphost mtime $APPHOST_MTIME) ist älter als der jüngste WowLauncher/-Commit ($SRC_CT). Erst deploy/publish-linux.sh."
  fi
fi

log "Version: $VERSION"
log "Publish: $PUBLISH"
log "SOURCE_DATE_EPOCH: $SOURCE_DATE_EPOCH ($(date -u -d "@$SOURCE_DATE_EPOCH" '+%Y-%m-%dT%H:%M:%SZ'))"

TARBALL="${OUTDIR}/${WRAPPER}-${VERSION}-${RID}.tar.xz"
mkdir -p "$OUTDIR"

# ---------------------------------------------------------------------------
# 4) Staging aufbauen
# ---------------------------------------------------------------------------
STAGE="$(mktemp -d /tmp/stonetavern-pkg.XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT
DEST="${STAGE}/${WRAPPER}"
mkdir -p "$DEST"

log "Payload kopieren"
# Der Payload landet in lib/, NICHT im Wurzelverzeichnis. Grund (Owner-Befund
# 2026-07-22, „ich extrahiere und sehe den Schmutzhaufen"): ein self-contained
# .NET-Publish sind ~270 Dateien. Liegen die oben, sieht der Spieler nach dem
# Entpacken eine Wand aus System.*.dll und muss raten, was davon das Programm ist.
# Oben liegt jetzt genau eine Sache zum Anklicken.
# cp -R statt -a: bewusst KEINE Timestamps/xattrs mitnehmen — Modes normalisieren wir eh.
mkdir -p "$DEST/lib"
cp -R "$PUBLISH/." "$DEST/lib/"

# Der Startpunkt. exec mit absolutem Pfad, KEIN cd: der apphost sucht seine DLLs
# neben sich selbst, nicht im Arbeitsverzeichnis — ein cd wuerde nur das
# Arbeitsverzeichnis des Spielers verbiegen.
cat > "$DEST/Start Stonetavern Launcher" <<'STARTEOF'
#!/usr/bin/env bash
# Startet den Launcher. Alles Weitere liegt in lib/ und geht dich nichts an.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$HERE/lib/WowLauncher" "$@"
STARTEOF

# LICENSE + README + Integration
cp "$LICENSE_SRC" "$DEST/LICENSE"
mkdir -p "$DEST/integration"
cp "$ASSET_ICON" "$DEST/integration/${WRAPPER}.png"

cat > "$DEST/README.md" <<READMEEOF
# Stonetavern Launcher — Linux (x86-64)

Dieser Launcher startet den Client des **Stonetavern**-Realms (Vanilla-WoW 1.12.1) unter
Linux. Zum Einrichten das Archiv an einen beliebigen Ort entpacken und den Launcher direkt
starten — er verwaltet Download, SHA256-Prüfung, Entpacken und Realmlist selbst:

\`\`\`bash
tar -xf ${WRAPPER}-${VERSION}-${RID}.tar.xz
cd ${WRAPPER}
"./Start Stonetavern Launcher"
\`\`\`

Im entpackten Ordner liegt genau **eine** Datei zum Starten. Alles andere, rund 270
Dateien Laufzeit, liegt in \`lib/\` und darf ignoriert werden.

> **Bequemer:** das **AppImage** von derselben Seite. Eine Datei, ausführbar machen,
> doppelklicken, nichts entpacken. Dieses Archiv ist der Weg für alle, die lieber selbst
> sehen, was sie vor sich haben.

WoW 1.12.1 ist eine Windows-Anwendung; der Launcher startet sie über **Wine** (System-Wine,
eigener verwalteter Prefix). Fehlt Wine, zeigt der Launcher eine Anleitung statt zu crashen.

## Wine installieren (pro Distribution)

| Distribution | Befehl |
|---|---|
| Ubuntu · Debian · Linux Mint | \`sudo apt install wine\` |
| Fedora | \`sudo dnf install wine\` |
| Arch · Manjaro | \`sudo pacman -S wine\` (dazu das **multilib**-Repo aktivieren — 32-Bit-Support ist für den 1.12.1-Client Pflicht) |
| openSUSE | \`sudo zypper install wine\` |

Getestet ab Ubuntu-22.04-Ära (glibc ≥ 2.35); Best-effort ab Ubuntu 20.04.

### Immutable-Systeme & Steam Deck (SteamOS, Fedora Silverblue/Kinoite, Bazzite)

Auf **unveränderlichen** Systemen gibt es keinen schreibenden System-Paketmanager — \`apt\`/\`dnf\`/\`pacman\`
für Wine funktioniert dort **nicht** (das Basissystem ist read-only). Zwei ehrliche Wege:

- **Flatpak-Wine:** ein Wine im Flatpak-Container (z.B. \`org.winehq.Wine\`) installieren und den
  Launcher daraus/dagegen laufen lassen.
- **Distrobox / toolbox:** einen beschreibbaren Container (Arch/Fedora/Ubuntu) anlegen, dort Wine
  wie oben installieren und den Launcher darin starten.

**Steam Deck (SteamOS):** in den **Desktop-Modus** wechseln (SteamOS ist immutable), dann einen der
beiden Wege oben nutzen. Ein \`pacman -S wine\` im Spielmodus/auf dem Basissystem ist nicht vorgesehen
und übersteht das nächste SteamOS-Update ohnehin nicht.

## Optionale Menü-Integration (kein Auto-Setup)

Im Ordner \`integration/\` liegen eine Desktop-Datei und ein Icon. Der Launcher installiert sie
**nicht** selbst. Wer einen Startmenü-Eintrag möchte, kopiert sie von Hand (absoluten Pfad in
\`Exec=\` eintragen):

\`\`\`bash
APPDIR="\$(pwd)"
install -Dm644 integration/${WRAPPER}.png \\
  "\${XDG_DATA_HOME:-\$HOME/.local/share}/icons/hicolor/256x256/apps/${WRAPPER}.png"
sed "s#@EXEC@#\${APPDIR}/lib/WowLauncher#" integration/${WRAPPER}.desktop \\
  > "\${XDG_DATA_HOME:-\$HOME/.local/share}/applications/${WRAPPER}.desktop"
update-desktop-database "\${XDG_DATA_HOME:-\$HOME/.local/share}/applications" 2>/dev/null || true
\`\`\`

Deinstallation: Launcher-Ordner löschen und — falls angelegt — die beiden Dateien oben
sowie die Nutzerdaten unter \`~/.config/${WRAPPER}\`, \`~/.local/share/${WRAPPER}\`,
\`~/.cache/${WRAPPER}\`, \`~/.local/state/${WRAPPER}\`.
READMEEOF

cat > "$DEST/integration/${WRAPPER}.desktop" <<'DESKTOPEOF'
[Desktop Entry]
Type=Application
Name=Stonetavern Launcher
Comment=Launcher für den Stonetavern-Realm (Vanilla WoW 1.12.1)
Exec=@EXEC@
Icon=stonetavern-launcher
Terminal=false
Categories=Game;
StartupNotify=true
StartupWMClass=WowLauncher
DESKTOPEOF

# BUILDINFO — Provenienz im Tarball (Codex F4). Deterministisch: build_time = SOURCE_DATE_EPOCH
# (NICHT Wall-Clock — sonst bräche der 2x-Pack-Determinismus-Check). release-check verifiziert
# commit == aktueller HEAD (FAIL im --release-Modus, WARN im Dev-Modus).
cat > "$DEST/BUILDINFO" <<BUILDINFOEOF
commit=${BUILD_COMMIT}
commit_short=${BUILD_COMMIT_SHORT}
dirty=${BUILD_DIRTY}
version=${VERSION}
build_epoch=${SOURCE_DATE_EPOCH}
build_time=$(date -u -d "@${SOURCE_DATE_EPOCH}" '+%Y-%m-%dT%H:%M:%SZ')
BUILDINFOEOF

# ---------------------------------------------------------------------------
# 5) Modes normalisieren (deterministisch)
# ---------------------------------------------------------------------------
log "Modes normalisieren"
find "$DEST" -type d -exec chmod 0755 {} +
find "$DEST" -type f -exec chmod 0644 {} +
# ausführbar: Startskript, apphost, native Libs, evtl. mitgelieferte Helfer
chmod 0755 "$DEST/Start Stonetavern Launcher"
chmod 0755 "$DEST/lib/WowLauncher"
[[ -f "$DEST/lib/createdump" ]] && chmod 0755 "$DEST/lib/createdump"
find "$DEST" -type f -name '*.so' -exec chmod 0755 {} +

# ---------------------------------------------------------------------------
# 6) Deterministisch tarren + xz
# ---------------------------------------------------------------------------
log "Tarball bauen: $TARBALL"
tar --sort=name \
    --format=gnu \
    --owner=0 --group=0 --numeric-owner \
    --mtime="@${SOURCE_DATE_EPOCH}" \
    --no-xattrs --no-acls --no-selinux \
    -C "$STAGE" -cf - "$WRAPPER" \
  | xz -9 -e -T1 -c > "$TARBALL"

SIZE_BYTES="$(stat -c%s "$TARBALL")"
SHA="$(sha256sum "$TARBALL" | awk '{print $1}')"

log "fertig"
printf 'Tarball : %s\n' "$TARBALL"
printf 'Größe   : %s Bytes (%s)\n' "$SIZE_BYTES" "$(numfmt --to=iec --suffix=B "$SIZE_BYTES" 2>/dev/null || echo "$SIZE_BYTES B")"
printf 'SHA256  : %s\n' "$SHA"
printf 'Version : %s\n' "$VERSION"
