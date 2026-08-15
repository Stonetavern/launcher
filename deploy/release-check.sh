#!/usr/bin/env bash
# WP5b — Release-Gate (Mechagon / Stonetavern WowLauncher, Linux-Spur)
#
# CI-Gate aus PLAN §3b. Prüft den Release-Tarball gegen die Härtungs-/Reputations-
# Checkliste (research/RESEARCH-linux-security-hygiene-2026-07-17.md):
#   (a) ELF-Sweep über den GESAMTEN entpackten Baum: PT_GNU_STACK vorhanden & ohne E;
#       kein DT_TEXTREL. Genau EIN Wrapper-Verzeichnis erzwungen.
#   (b) verbotene Flags: kein SingleFile-Bundle (Datei-Menge plausibel, .deps.json + lose dlls)
#   (I) Invarianten: keine Blizzard-Dateitypen, keine Secrets/Build-Pfade
#   (P) BUILDINFO: commit == aktueller HEAD (Stale-Publish-Schutz, Codex F4)
#   (c) ClamAV: Archiv + entpackter Baum (+ AppImage)
#   (d) SHA256SUMS erzeugen
#   (e) GPG: Detached-Signatur
#   (f) Determinismus: zweimal packen, sha256 vergleichen (byte-identisch)
#   (g) PASS/WARN/SKIP/FAIL-Tabelle, Exit != 0 bei irgendeinem FAIL
#
# ZWEI MODI (Codex F3 — Orchestrator-Arbitrage):
#   Dev-Modus (default): fehlendes ClamAV/GPG = SKIP (laut). Fußzeile weist auf --release hin.
#   --release (strikt):  ClamAV PFLICHT (fehlend/Timeout/Fehler = FAIL), GPG-Signatur PFLICHT
#                        (kein Key = FAIL), BUILDINFO-commit != HEAD = FAIL. AppImage bleibt
#                        optional — aber WENN vorhanden, wird es mitgescannt/gehasht/signiert.
#   WP6-Upload NUR nach '--release'-GRÜN (RELEASE-RUNBOOK §8).
#
# Aufruf:   release-check.sh [--release] [pfad/zum/tarball.tar.xz]
#   Ohne Pfad: neuester *-linux-x64.tar.xz unter deploy/dist/.
# Keine Codeänderung an WowLauncher/. Kein sudo, keine Uploads, kein Key-Erzeugen.
set -uo pipefail
export LC_ALL=C TZ=UTC

ROOT="(internal design notes, not published)"
DISTDIR="${OUTDIR:-${ROOT}/deploy/dist}"
PACKAGER="${ROOT}/deploy/package-linux.sh"

# ---- Argumente -------------------------------------------------------------
RELEASE_MODE=0
TARBALL=""
for a in "$@"; do
  case "$a" in
    --release) RELEASE_MODE=1 ;;
    -*)        printf 'release-check: unbekanntes Flag: %s\n' "$a" >&2; exit 2 ;;
    *)         TARBALL="$a" ;;
  esac
done
MODE_LABEL="DEV"; (( RELEASE_MODE )) && MODE_LABEL="RELEASE"

# ---- Ergebnis-Sammler ------------------------------------------------------
declare -a R_NAME R_STATUS R_DETAIL
add() { R_NAME+=("$1"); R_STATUS+=("$2"); R_DETAIL+=("$3"); }
FAILS=0
fail_add() { add "$1" "FAIL" "$2"; FAILS=$((FAILS+1)); }
note() { printf '  %s\n' "$*"; }

# In-Release harter FAIL, im Dev-Modus je nach Aufrufer SKIP oder WARN.
# usage: soft_add <name> <dev_status(SKIP|WARN)> <detail>
soft_add() {
  local name="$1" devstatus="$2" detail="$3"
  if (( RELEASE_MODE )); then
    fail_add "$name" "$detail"
  else
    add "$name" "$devstatus" "$detail"
  fi
}

# ---- Tarball bestimmen -----------------------------------------------------
if [[ -z "$TARBALL" ]]; then
  TARBALL="$(ls -1t "$DISTDIR"/*-linux-x64.tar.xz 2>/dev/null | head -1 || true)"
fi
if [[ -z "$TARBALL" || ! -f "$TARBALL" ]]; then
  printf 'release-check: FEHLER: kein Tarball gefunden (arg oder %s/*-linux-x64.tar.xz). Erst deploy/package-linux.sh laufen lassen.\n' "$DISTDIR" >&2
  exit 2
fi
TARBALL="$(readlink -f "$TARBALL")"
# Optionales AppImage (Komfortformat) — wird, WENN vorhanden, mitgeprüft (Codex F7).
APPIMAGE="$(ls -1t "$DISTDIR"/*-x86_64.AppImage 2>/dev/null | head -1 || true)"
[[ -n "$APPIMAGE" && -f "$APPIMAGE" ]] && APPIMAGE="$(readlink -f "$APPIMAGE")" || APPIMAGE=""

printf '== Release-Check [%s-MODUS]: %s ==\n' "$MODE_LABEL" "$TARBALL"
[[ -n "$APPIMAGE" ]] && printf '   + AppImage: %s\n' "$APPIMAGE"

WORK="$(mktemp -d /tmp/stonetavern-relcheck.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT
TREE="${WORK}/extract"
mkdir -p "$TREE"

# ---------------------------------------------------------------------------
# Entpacken
# ---------------------------------------------------------------------------
if ! tar -xf "$TARBALL" -C "$TREE" 2>"$WORK/untar.err"; then
  printf 'release-check: FEHLER beim Entpacken:\n'; cat "$WORK/untar.err" >&2; exit 2
fi

# Codex F1: GENAU EIN Wrapper-Verzeichnis erzwingen (mehr/anders = FAIL). Kein head -1 mehr,
# das eine zweite Top-Level-Datei still übergehen würde.
mapfile -t TOP < <(find "$TREE" -maxdepth 1 -mindepth 1 | sort)
if (( ${#TOP[@]} != 1 )) || [[ ! -d "${TOP[0]}" ]]; then
  printf 'release-check: FEHLER: erwartet GENAU EIN Wrapper-Verzeichnis auf Top-Level, gefunden %d Eintrag/Einträge:\n' "${#TOP[@]}" >&2
  printf '    %s\n' "${TOP[@]#$TREE/}" >&2
  exit 2
fi
PAYLOAD="${TOP[0]}"

# ---------------------------------------------------------------------------
# (a) ELF-Sweep — über den GESAMTEN Baum (Codex F1), readelf-Fehler = FAIL (Codex F2)
# ---------------------------------------------------------------------------
printf '\n-- (a) ELF-Sweep (PT_GNU_STACK ohne E, kein TEXTREL; ganzer Baum) --\n'
is_elf() { [[ "$(head -c4 "$1" 2>/dev/null | od -An -tx1 | tr -d ' \n')" == "7f454c46" ]]; }
elf_count=0; elf_bad=0; bad_list=""
while IFS= read -r -d '' f; do
  is_elf "$f" || continue
  elf_count=$((elf_count+1))
  rel="${f#$TREE/}"
  # readelf -l: Exit-Status MUSS 0 sein — sonst ist die Analyse wertlos (Codex F2:
  # ein unterdrückter readelf-Fehler passierte bisher als „kein TEXTREL / GNU_STACK ok").
  lprog="$(readelf -lW "$f" 2>"$WORK/readelf.err")"; lrc=$?
  if (( lrc != 0 )); then
    elf_bad=$((elf_bad+1)); bad_list+=$'\n    '"$rel: readelf -l fehlgeschlagen (rc=$lrc)"; note "FAIL $rel: readelf -l rc=$lrc"; continue
  fi
  gnustack="$(printf '%s\n' "$lprog" | awk '/GNU_STACK/{print $(NF-1); found=1} END{if(!found)print "MISSING"}')"
  if [[ "$gnustack" == "MISSING" ]]; then
    elf_bad=$((elf_bad+1)); bad_list+=$'\n    '"$rel: KEIN PT_GNU_STACK"; note "FAIL $rel: kein PT_GNU_STACK"; continue
  fi
  if [[ "$gnustack" == *E* ]]; then
    elf_bad=$((elf_bad+1)); bad_list+=$'\n    '"$rel: executable stack ($gnustack)"; note "FAIL $rel: executable stack ($gnustack)"; continue
  fi
  # readelf -d: ebenfalls Exit-Status prüfen, dann auf TEXTREL grepen.
  dprog="$(readelf -dW "$f" 2>"$WORK/readelf.err")"; drc=$?
  if (( drc != 0 )); then
    elf_bad=$((elf_bad+1)); bad_list+=$'\n    '"$rel: readelf -d fehlgeschlagen (rc=$drc)"; note "FAIL $rel: readelf -d rc=$drc"; continue
  fi
  if printf '%s\n' "$dprog" | grep -qiE 'TEXTREL'; then
    elf_bad=$((elf_bad+1)); bad_list+=$'\n    '"$rel: DT_TEXTREL"; note "FAIL $rel: DT_TEXTREL"; continue
  fi
done < <(find "$TREE" -type f -print0)
note "ELF-Dateien geprüft: $elf_count, beanstandet: $elf_bad"
if (( elf_count == 0 )); then
  # Leere-Menge-Falle (Codex F2): 0 ELFs ist KEIN PASS, sondern ein leerer/kaputter Publish.
  fail_add "ELF-Sweep" "keine ELF-Dateien im Baum gefunden (Publish leer/kaputt?)"
elif (( elf_bad == 0 )); then
  add "ELF-Sweep" "PASS" "$elf_count ELFs: alle PT_GNU_STACK=RW, kein TEXTREL"
else
  fail_add "ELF-Sweep" "$elf_bad/$elf_count ELF(s) beanstandet:${bad_list}"
fi

# ---------------------------------------------------------------------------
# (b) verbotene Flags / kein SingleFile-Bundle
# ---------------------------------------------------------------------------
printf '\n-- (b) SingleFile-Bundle-Nachweis (Datei-Menge plausibel) --\n'
# Seit 1.1.0 liegt die Laufzeit in lib/, damit ein Spieler nach dem Entpacken
# EINE Datei sieht statt 270. Beide Layouts pruefen, damit das Gate nicht am
# Umzug scheitert und, wichtiger, nicht still durchwinkt: faende es lib/ nicht,
# waeren managed_dlls=0 und der SingleFile-Verdacht wuerde faelschlich anschlagen.
RUNTIME="$PAYLOAD"; [[ -d "$PAYLOAD/lib" ]] && RUNTIME="$PAYLOAD/lib"
total_files="$(find "$PAYLOAD" -type f | wc -l)"
managed_dlls="$(find "$RUNTIME" -maxdepth 1 -type f -name '*.dll' | wc -l)"
has_deps="no"; [[ -f "$RUNTIME/WowLauncher.deps.json" ]] && has_deps="yes"
apphost_size=0; [[ -f "$RUNTIME/WowLauncher" ]] && apphost_size="$(stat -c%s "$RUNTIME/WowLauncher")"
note "Dateien gesamt: $total_files · managed .dll (top): $managed_dlls · deps.json: $has_deps · apphost: ${apphost_size}B"
# Ein SingleFile-Bundle hätte: keine losen managed dlls, kein deps.json, riesigen apphost (~40MB+).
if [[ "$has_deps" == "yes" && "$managed_dlls" -ge 10 && "$apphost_size" -lt 5000000 ]]; then
  add "SingleFile-Verbot" "PASS" "Multi-File-Publish: deps.json + $managed_dlls lose dlls, apphost ${apphost_size}B (kein Bundle)"
else
  fail_add "SingleFile-Verbot" "sieht nach SingleFile/Bundle aus (deps=$has_deps dlls=$managed_dlls apphost=${apphost_size}B)"
fi

# ---------------------------------------------------------------------------
# (I) Invarianten-Sweeps (Codex F6) — Blizzard-Assets & Secrets/Build-Pfade
# ---------------------------------------------------------------------------
printf '\n-- (I) Invarianten: Blizzard-Assets & Secrets/Build-Pfade --\n'
# (I.a) Blizzard-Dateitypen (AGENTS.md/CLAUDE.md §10 — dürfen NIE ins Release).
mapfile -t bliz < <(find "$TREE" -type f \( \
     -iname '*.mpq' -o -iname '*.blp' -o -iname '*.m2' -o -iname '*.wmo' \
  -o -iname '*.adt' -o -iname '*.wdt' -o -iname '*.dbc' -o -iname 'WoW.exe' -o -iname 'WowClassic.exe' \) 2>/dev/null)
if (( ${#bliz[@]} > 0 )); then
  printf '    %s\n' "${bliz[@]#$TREE/}"
  fail_add "Blizzard-Assets" "${#bliz[@]} verbotene Blizzard-Datei(en) im Baum — Copyright-Leak"
else
  add "Blizzard-Assets" "PASS" "keine .mpq/.blp/.m2/.wmo/.adt/.wdt/.dbc/WoW.exe/WowClassic.exe im Baum"
fi
# (I.b) Secrets & Build-Pfade — Codex-Re-Gate: grep -a durchsucht auch BINÄRDATEIEN
# (ein eingebetteter Key/Buildpfad in einer .dll darf nicht per -I übersprungen werden).
secret_hits=""
p_hits="$(grep -Rla '/AI/' "$TREE" 2>/dev/null || true)"
k_hits="$(grep -Rla -e 'PRIVATE KEY' -e 'BEGIN OPENSSH' "$TREE" 2>/dev/null || true)"
mapfile -t env_hits < <(find "$TREE" -type f -name '.env*' 2>/dev/null)
[[ -n "$p_hits" ]] && secret_hits+=$'\n    Build-Pfad /AI/: '"${p_hits//$'\n'/ }"
[[ -n "$k_hits" ]] && secret_hits+=$'\n    Key-Material: '"${k_hits//$'\n'/ }"
(( ${#env_hits[@]} > 0 )) && secret_hits+=$'\n    .env: '"$(printf '%s ' "${env_hits[@]}")"
if [[ -n "$secret_hits" ]]; then
  printf '%s\n' "$secret_hits"
  fail_add "Secrets/Build-Pfade" "Fund im Baum:${secret_hits}"
else
  add "Secrets/Build-Pfade" "PASS" "kein (internal design notes, not published), kein PRIVATE KEY/OPENSSH, keine .env-Datei"
fi

# ---------------------------------------------------------------------------
# (P) BUILDINFO — Provenienz/Stale-Publish-Schutz (Codex F4)
# ---------------------------------------------------------------------------
printf '\n-- (P) BUILDINFO (commit == HEAD) --\n'
BI="$PAYLOAD/BUILDINFO"
HEAD_COMMIT="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
if [[ ! -f "$BI" ]]; then
  soft_add "BUILDINFO" "WARN" "keine BUILDINFO im Tarball — mit aktuellem package-linux.sh neu packen"
else
  bi_commit="$(sed -n 's/^commit=//p' "$BI" | head -1)"
  bi_dirty="$(sed -n 's/^dirty=//p' "$BI" | head -1)"
  note "BUILDINFO: commit=${bi_commit:-?} dirty=${bi_dirty:-?} · HEAD=$HEAD_COMMIT"
  if [[ "$bi_dirty" == "yes" ]]; then
    fail_add "BUILDINFO" "Tarball aus dirty Working-Tree gebaut (dirty=yes) — nicht reproduzierbar"
  elif [[ -z "$bi_commit" || "$bi_commit" == "unknown" ]]; then
    soft_add "BUILDINFO" "WARN" "BUILDINFO ohne verwertbaren commit="
  elif [[ "$bi_commit" == "$HEAD_COMMIT" ]]; then
    add "BUILDINFO" "PASS" "commit == HEAD (${bi_commit:0:7})"
  else
    soft_add "BUILDINFO" "WARN" "commit ${bi_commit:0:7} != HEAD ${HEAD_COMMIT:0:7} — HEAD wanderte seit dem Packen; neu packen"
  fi
fi

# ---------------------------------------------------------------------------
# (c) ClamAV
# ---------------------------------------------------------------------------
printf '\n-- (c) ClamAV (Archiv + Baum%s) --\n' "$([[ -n "$APPIMAGE" ]] && echo ' + AppImage')"
if command -v clamscan >/dev/null 2>&1; then
  cav_out="$WORK/clamav.log"
  cav_targets=("$TARBALL" "$PAYLOAD"); [[ -n "$APPIMAGE" ]] && cav_targets+=("$APPIMAGE")
  timeout 900 clamscan -r --no-summary --stdout "${cav_targets[@]}" >"$cav_out" 2>&1
  cav_rc=$?
  infected="$(grep -c 'FOUND$' "$cav_out" 2>/dev/null || echo 0)"
  case "$cav_rc" in
    0) add "ClamAV" "PASS" "0 Funde (Archiv+Baum$([[ -n "$APPIMAGE" ]] && echo '+AppImage'), DB $(sigtool --info /var/lib/clamav/daily.cld 2>/dev/null | awk -F': ' '/Version/{print $2; exit}'))" ;;
    1) note "clamscan-Funde:"; grep 'FOUND$' "$cav_out" | sed 's/^/    /'
       fail_add "ClamAV" "$infected Fund(e) — siehe $cav_out" ;;
    124) soft_add "ClamAV" "SKIP" "Timeout (>900s) — manuell nachscannen" ;;
    *) note "clamscan-Fehler (rc=$cav_rc):"; tail -3 "$cav_out" | sed 's/^/    /'
       soft_add "ClamAV" "SKIP" "Scanner-Fehler rc=$cav_rc (z.B. fehlende/kaputte DB)" ;;
  esac
else
  soft_add "ClamAV" "SKIP" "clamscan nicht installiert — vor Release: dnf install clamav && freshclam"
fi

# ---------------------------------------------------------------------------
# (f) Determinismus: zweimal frisch packen, sha256 vergleichen
#     (vor (d)/(e), damit SHA256SUMS über das reproduzierte Artefakt läuft)
# ---------------------------------------------------------------------------
printf '\n-- (f) Determinismus (2x packen, byte-identisch) --\n'
if [[ -x "$PACKAGER" ]]; then
  d1="$WORK/det1"; d2="$WORK/det2"; mkdir -p "$d1" "$d2"
  OUTDIR="$d1" bash "$PACKAGER" >"$WORK/pack1.log" 2>&1; p1rc=$?
  OUTDIR="$d2" bash "$PACKAGER" >"$WORK/pack2.log" 2>&1; p2rc=$?
  t1="$(ls -1 "$d1"/*.tar.xz 2>/dev/null | head -1 || true)"
  t2="$(ls -1 "$d2"/*.tar.xz 2>/dev/null | head -1 || true)"
  if (( p1rc != 0 || p2rc != 0 )) || [[ -z "$t1" || -z "$t2" ]]; then
    note "Packen fehlgeschlagen (rc1=$p1rc rc2=$p2rc)"; tail -3 "$WORK/pack1.log" | sed 's/^/    /'
    fail_add "Determinismus" "Re-Pack fehlgeschlagen (rc1=$p1rc rc2=$p2rc)"
  else
    s1="$(sha256sum "$t1" | awk '{print $1}')"; s2="$(sha256sum "$t2" | awk '{print $1}')"
    note "Pack #1: $s1"; note "Pack #2: $s2"
    if [[ "$s1" == "$s2" ]]; then
      s_rc="$(sha256sum "$TARBALL" | awk '{print $1}')"
      if [[ "$s1" == "$s_rc" ]]; then
        add "Determinismus" "PASS" "byte-identisch inkl. geprüftem RC: $s1"
      else
        # Re-Packs stimmen überein, aber der geprüfte Kandidat weicht ab → er stammt aus einem
        # anderen Stand (Epoch/Inhalt). Im Release-Modus disqualifiziert das den Kandidaten.
        note "RC:      $s_rc (!= Re-Pack)"
        soft_add "Determinismus" "WARN" "Re-Packs identisch, aber geprüfter Tarball weicht ab ($s_rc) — Kandidat neu packen"
      fi
    else
      fail_add "Determinismus" "SHA divergiert ($s1 != $s2)"
    fi
  fi
else
  soft_add "Determinismus" "SKIP" "package-linux.sh nicht ausführbar: $PACKAGER"
fi

# ---------------------------------------------------------------------------
# (d) SHA256SUMS erzeugen (Tarball + AppImage, falls vorhanden)
# ---------------------------------------------------------------------------
printf '\n-- (d) SHA256SUMS --\n'
mkdir -p "$DISTDIR"
SUMS="$DISTDIR/SHA256SUMS"
sums_files=("$(basename "$TARBALL")")
[[ -n "$APPIMAGE" ]] && sums_files+=("$(basename "$APPIMAGE")")
if ( cd "$DISTDIR" && sha256sum "${sums_files[@]}" > "$SUMS" ); then
  while IFS= read -r line; do note "$line"; done < "$SUMS"
  add "SHA256SUMS" "PASS" "$SUMS erzeugt (${#sums_files[@]} Datei(en))"
else
  fail_add "SHA256SUMS" "konnte SHA256SUMS nicht schreiben"
fi

# ---------------------------------------------------------------------------
# (e) GPG-Signatur (Codex F3: --release = Pflicht; Dev = SKIP wenn kein Key)
# ---------------------------------------------------------------------------
printf '\n-- (e) GPG-Signatur --\n'
have_key="no"
if command -v gpg >/dev/null 2>&1; then
  if timeout 20 gpg --list-secret-keys --with-colons 2>/dev/null | grep -q '^sec'; then
    have_key="yes"
  fi
fi
if [[ "$have_key" == "yes" && -f "$SUMS" ]]; then
  if gpg --yes --armor --detach-sign --output "${SUMS}.asc" "$SUMS" 2>"$WORK/gpg.err"; then
    add "GPG-Signatur" "PASS" "${SUMS}.asc erzeugt (deckt Tarball$([[ -n "$APPIMAGE" ]] && echo '+AppImage') via SHA256SUMS)"
  else
    note "$(tail -2 "$WORK/gpg.err")"
    fail_add "GPG-Signatur" "Signieren fehlgeschlagen (Key vorhanden)"
  fi
else
  soft_add "GPG-Signatur" "SKIP" "kein GPG-Secret-Key konfiguriert — vor erstem echten Release einrichten (Key erzeugen, Fingerprint veröffentlichen); NICHT durch dieses Skript"
fi

# ---------------------------------------------------------------------------
# (g) Tabelle + Exit
# ---------------------------------------------------------------------------
printf '\n================= RELEASE-CHECK ERGEBNIS [%s-MODUS] =================\n' "$MODE_LABEL"
printf '%-20s %-6s %s\n' "CHECK" "STATUS" "DETAIL"
printf '%-20s %-6s %s\n' "--------------------" "------" "----------------------------------------"
n_pass=0; n_warn=0; n_skip=0; n_fail=0
for i in "${!R_NAME[@]}"; do
  st="${R_STATUS[$i]}"
  case "$st" in
    PASS) n_pass=$((n_pass+1));; WARN) n_warn=$((n_warn+1));;
    SKIP) n_skip=$((n_skip+1));; FAIL) n_fail=$((n_fail+1));;
  esac
  d1line="${R_DETAIL[$i]%%$'\n'*}"
  printf '%-20s %-6s %s\n' "${R_NAME[$i]}" "$st" "$d1line"
done
printf '====================================================================\n'
printf 'PASS=%d  WARN=%d  SKIP=%d  FAIL=%d\n' "$n_pass" "$n_warn" "$n_skip" "$n_fail"

if (( FAILS > 0 )); then
  printf 'GATE: ROT — %d FAIL. Release blockiert.\n' "$FAILS"
  exit 1
fi
if (( RELEASE_MODE )); then
  printf 'GATE: GRÜN [RELEASE] — kein FAIL. Freigabe für WP6-Upload (RELEASE-RUNBOOK §8).\n'
else
  printf 'GATE: GRÜN [DEV] — kein FAIL (SKIP/WARN = bewusst offen).\n'
  printf 'DEV-MODUS — für WP6/echtes Release: release-check.sh --release (ClamAV+GPG dann PFLICHT).\n'
fi
exit 0
