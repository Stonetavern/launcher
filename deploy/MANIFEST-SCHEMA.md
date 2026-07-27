# Manifest-Schema — `launcher_linux` (WP4 → WP6)

**Datum:** 2026-07-17 · **Kontext:** Mechagon / Stonetavern WowLauncher · Linux-Spur (PLAN §1.6, Codex A5)

Dieses Dokument hält das **exakte** JSON-Schema fest, mit dem WP6 das Produktions-Manifest
`https://downloads.stonetavern.app/manifest.json` **additiv** erweitert. Der Launcher-Code (WP4) parst
diese Form bereits; die Contract-Fixtures unter
`/AI/projects/mechagon/WowLauncher.Tests/fixtures/` beweisen alle vier Kombinationen, **bevor** das
Prod-Manifest angefasst wird.

## Die Regel in einem Satz

Ein **neues Top-Level-Feld** `launcher_linux` — gleicher Typ wie das bestehende `launcher`
(`version` / `url` / `sha256` / `size`). **Niemals** in `launcher` verschachteln. Fehlt das Feld,
zeigt der Linux-Launcher **keinen** Hinweis und wirft **keinen** Fehler; der Windows-Launcher
**ignoriert** das Feld vollständig.

## Exaktes JSON (Beispiel — additiv einzufügen)

```json
{
  "product": "stonetavern-classic",
  "current_version": "1.12.1",
  "build_date": "2026-05-30",

  "launcher": {
    "version": "2.0.0",
    "url": "https://downloads.stonetavern.app/launcher/WowLauncher-2.0.0.exe",
    "size": 41943040,
    "sha256": "<sha256-hex-der-windows-exe>"
  },

  "launcher_linux": {
    "version": "3.0.0",
    "url": "https://downloads.stonetavern.app/launcher/WowLauncher-3.0.0-linux-x64.tar.xz",
    "size": 62914560,
    "sha256": "<sha256-hex-des-linux-tarballs>"
  }
}
```

`launcher_linux` steht **auf derselben Ebene** wie `launcher`, `base`, `patches`, `phases` — nicht
darin. Alle übrigen Felder (`base`, `patches`, `active_phase`, `phases`, …) bleiben unverändert.

## Feld-Semantik (identisch zu `launcher`)

| Feld | Typ | Bedeutung |
|---|---|---|
| `version` | string | **Strikt numerisches `System.Version`-Format** (`Major.Minor.Build`, z.B. `2.1.0`; ein führendes `v` wird toleriert). **Kein SemVer** — Prerelease-/Build-Suffixe (`-beta.1`, `+build`) sind ungültig und werden mit Warnung verworfen. Dreigliedrig pflegen (eine vierte Stelle zählt als neuer als die dreigliedrige Basis). Der Hinweis erscheint **nur**, wenn diese Version **echt neuer** ist als die laufende Launcher-Assembly-Version. |
| `url` | string | Voll-qualifizierte Download-URL des Linux-Tarballs. In V1 **rein informativ** — der Launcher lädt **nicht** automatisch; der Hinweis verweist auf `downloads.stonetavern.app`. |
| `sha256` | string | Hex-Digest. In V1 (Check+Notify) noch nicht scharf verwendet, aber **Pflichtfeld** für WP7 (Signierung + Auto-Apply). Jetzt schon korrekt setzen. |
| `size` | number (long) | Byte-Größe des Tarballs. Informativ. |

## Verhalten des Launchers (WP4)

- **Linux** liest `launcher_linux`. Ist die Version neuer als die laufende Assembly, zeigt der Launcher
  einen unaufdringlichen Hinweis: *„Neue Launcher-Version `X` verfügbar — Download auf
  downloads.stonetavern.app"*. **Kein** automatischer Download, **kein** Swap (Auto-Apply kommt erst
  mit Artefakt-Signierung, PLAN §1.5 / WP7). Fehlt `launcher_linux` → gar kein Hinweis, kein Fehler.
- **Windows** liest ausschließlich `launcher` (unverändertes Auto-Apply-Verhalten). `launcher_linux`
  wird komplett ignoriert.

## Roll-out-Hinweis für WP6 🔴

`downloads.stonetavern.app` ist **Produktion mit echten Spielern**. Die Erweiterung ist additiv und
reversibel, aber outward (CORE §2-Gate: ein Satz Ankündigung, dann User-Go). Vor dem Schreiben:
Tarball nach `/srv/downloads` legen, `sha256` und `size` **aus der real ausgelieferten Datei**
berechnen (nicht raten), dann das Feld ins Manifest einfügen. Windows-Spieler sind durch die
Ignoranz des Feldes garantiert unberührt — bewiesen durch Contract-Test (c)
(`ManifestContractTests.C_NewManifest_Windows_IgnoresLauncherLinux_LauncherWins`).

---

# Manifest-Schema — `files_url` (Repair Phase 1)

**Datum:** 2026-07-21 · **Kontext:** WowLauncher Repair-ohne-Redownload

## Das Problem in einem Satz

`base.sha256`/`phases[].client.sha256` sind je EIN Hash über das GANZE Client-ZIP (mehrere GB). Damit
kann der Launcher nur sagen "das ZIP war korrekt oder nicht" — niemals, WELCHE Datei kaputt ist. Der
Repair-Knopf lädt deshalb heute IMMER das komplette ZIP neu, egal ob eine 2 KB-Datei fehlt.

## Die Regel in einem Satz

Ein neues OPTIONALES Feld `files_url` auf `base` und auf `phases[].client` (gleiche Ebene wie `url`,
`size`, `sha256` — nicht verschachtelt). Zeigt auf ein per-Datei-Manifest im Schema unten. Fehlt es,
verhält sich Repair **exakt wie heute** (volles ZIP) — kein Verhaltensunterschied, kein Fehler.

## Exaktes JSON (Beispiel — additiv einzufügen)

```json
{
  "base": {
    "version": "1.12.1",
    "url": "https://downloads.stonetavern.app/client/vanilla-1.12.1.zip",
    "size": 5368709120,
    "sha256": "<sha256-hex-des-ganzen-zips>",
    "files_url": "https://downloads.stonetavern.app/client/vanilla-1.12.1-files.json"
  }
}
```

## Schema von `files_url` (die Datei, auf die es zeigt)

```json
{
  "build": 5875,
  "version": "1.12.1",
  "files": [
    { "path": "Data/patch.mpq", "size": 123456, "sha256": "<hex>" },
    { "path": "WoW.exe", "size": 7340032, "sha256": "<hex>" }
  ]
}
```

| Feld | Typ | Bedeutung |
|---|---|---|
| `build` | number | Game-Build (z.B. `5875`), zur Selbstkontrolle gegen den Phase-Slug. |
| `version` | string | Client-Version, informativ. |
| `files[].path` | string | Pfad relativ zum Install-Root, **immer `/` als Trenner** (auch aus einem Windows-Generator-Lauf) — dieselbe Konvention wie ZIP-Einträge in `DownloadService.ExtractClientAsync`. |
| `files[].size` | number (long) | Byte-Größe. Wird VOR dem Hash geprüft (billig zuerst). |
| `files[].sha256` | string | Hex-Digest der Datei. |

## Verhalten des Launchers

`files_url` fehlt → `PlayViewModel.Repair()` verhält sich unverändert: sofortiger Volldownload.
`files_url` vorhanden → `IClientVerifyService.VerifyAsync` prüft erst den bestehenden Install-Ordner
Datei für Datei (Existenz → Größe → erst dann Hash), meldet Fortschritt in der ActionBar, und lädt
das ZIP nur neu, wenn wirklich etwas fehlt oder beschädigt ist. Dateien unter `WTF/`,
`Interface/AddOns/`, `Screenshots/` oder `realmlist.wtf` werden NIE als Defekt gemeldet — das sind vom
Spieler geänderte Daten, die der Extract ohnehin verschont.

## Generator

`deploy/make-file-manifest.sh <client-dir> <build-nummer>` erzeugt `files.json` aus einem bereits
entpackten Client-Verzeichnis (deterministisch sortiert).

---

# Manifest-Schema — `phases[].clients[]` (mehrere Client-Builds pro Phase)

**Datum:** 2026-07-22 · **Anlass:** 1.14.2 (Build 42597) soll aus dem Launcher ladbar sein, nicht nur
„bring deinen eigenen Client". Owner-Freigabe am 2026-07-22.

## Die Regel in einem Satz

Eine **neue Liste** `clients` **neben** dem bestehenden `client` innerhalb eines Phase-Eintrags. Jeder
Eintrag hat dieselben Felder wie `client`, plus **`build`** (Pflicht) und **`os`** (optional).

## Warum nicht einfach eine zweite Phase

Eine Phase ist **Server-Progression** — welcher Content live ist. Beide Vanilla-Clients spielen
**dieselbe** Phase. 1.14.2 als eigene Phase zu modellieren würde einen Progressionsschritt behaupten,
den es nicht gibt, und jeden Realm darauf mitziehen.

## Exaktes JSON (additiv einzufügen)

```json
{
  "phases": [
    {
      "phase": "vanilla",
      "realmlist": "play.stonetavern.app",

      "client": {
        "version": "1.12.1",
        "url": "https://downloads.stonetavern.app/clients/stonetavern-1.12.1/Stonetavern-WoW-1.12.1-v1.2.zip",
        "size": 5316714041,
        "sha256": "9cd0a7b8…"
      },

      "clients": [
        {
          "build": 42597, "os": "windows", "version": "1.3.1",
          "url": "https://downloads.stonetavern.app/clients/stonetavern-modern-1.14.2/Stonetavern-Modern-1.14.2-v1.3.1.zip",
          "size": 8240519812,
          "sha256": "624808bb…"
        },
        {
          "build": 42597, "os": "linux", "version": "1.3.1",
          "url": "https://downloads.stonetavern.app/clients/stonetavern-modern-1.14.2/Stonetavern-Modern-1.14.2-Linux-v1.3.1.zip",
          "size": 8272101832,
          "sha256": "4ee513a5…"
        }
      ]
    }
  ]
}
```

Einsatzbereite Vorlage mit echten Hashes: **`/AI/projects/wow/launcher/deploy/manifest-with-1142.json`**.
Sie ist nachweislich additiv — entfernt man das `clients`-Feld, ist sie byte-identisch mit dem
lebenden Manifest.

## Feld-Semantik

| Feld | Typ | Bedeutung |
|---|---|---|
| `build` | number | **Pflicht.** Der Game-Build, für den dieses Paket ist (`5875`, `42597`). Fehlt er, matcht der Eintrag **nie** — `null` ist kein Platzhalter für „passt zu allem". Ein Eintrag, der nicht sagt, für welchen Client er ist, darf nicht an den nächstbesten gehen. |
| `os` | string | Optional: `"windows"`, `"linux"`, `"macos"`. Fehlt = gilt überall. Ein **unbekannter** Wert matcht **nie**. |
| `version` / `url` / `size` / `sha256` / `files_url` | — | Identisch zu `client`. `sha256` ist Pflicht: der Launcher verifiziert **vor** dem Entpacken. |

## Auflösungsregeln (`PhaseManifest.ClientForBuild`)

1. Ein Eintrag in `clients` mit passendem `build` **und** passendem OS. Ein OS-spezifischer Eintrag
   schlägt einen OS-neutralen.
2. Sonst: Ist der Build in `clients` **überhaupt** genannt (nur für andere Plattformen), dann
   **nichts** — niemals ersatzweise das kanonische Paket der Phase.
3. Sonst: `client` (bzw. `base`) — aber **nur**, wenn der gesuchte Build der kanonische Build der Phase
   ist. Das 1.12.1-ZIP darf nie eine 42597-Anfrage beantworten; es würde den falschen Client über eine
   funktionierende Installation entpacken.

## 🔴 Warum `os` bei 1.14.2 nicht optional ist

Die beiden 1.14.2-Pakete sind **nicht austauschbar** (am 2026-07-22 im Archiv nachgesehen):

| | Windows-Paket | Linux-Paket |
|---|---|---|
| Proxy | `Hermes/CSV` (105 CSV) | `Hermes/linux/` + `Hermes/linux/CSV` (93 CSV), native ELF |
| Arctium | **fehlt** (unter Windows nicht nötig) | `Launcher/Arctium WoW Launcher.exe` |
| Start | `Play Stonetavern.cmd` | `Play Stonetavern.sh` |

Ein Linux-Spieler mit dem Windows-Paket bekäme eine Installation, die der Launcher zu Recht ablehnt —
**nach 8 GB Download**. Beide entpacken flach (kein `Stonetavern/`-Wrapper), was `ModernClientLayout`
erwartet.

## Verhalten des Launchers

- **Eintrag vorhanden:** normaler DOWNLOAD-Knopf, SHA256-Verify vor Extract, Version-Vergleich gegen
  `InstalledClientVersions[build]`.
- **Kein Eintrag:** der Launcher sagt ehrlich „bring deinen eigenen Client" und **deaktiviert** die
  Aktion, statt einen Klick anzubieten, der nur scheitern kann.
- **Alter Launcher, neues Manifest:** ignoriert `clients` (System.Text.Json überspringt Unbekanntes)
  und verhält sich exakt wie heute.
