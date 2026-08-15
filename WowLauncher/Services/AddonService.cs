namespace WowLauncher.Services;

using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text.Json;
using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>The outcome of installing or removing one addon, in words a player can act on.</summary>
public sealed record AddonActionResult(bool Ok, string? Error = null)
{
    public static AddonActionResult Success => new(true);
    public static AddonActionResult Failed(string error) => new(false, error);
}

/// <summary>
/// One row of the addons screen: what the catalog offers, joined with what is actually on disk.
/// A folder the launcher did not write is reported too, as <see cref="IsManaged"/> false — the player
/// installed it by hand and it must never be touched, but pretending not to see it would make the
/// screen lie about what is loaded in the game.
/// </summary>
public sealed record AddonStatus(
    string Id,
    string Name,
    string Summary,
    string Author,
    string License,
    string Homepage,
    string? AvailableVersion,
    string? InstalledVersion,
    bool IsManaged,
    long Size,
    bool FitsClient = true,
    IReadOnlyList<int>? Builds = null)
{
    public bool IsInstalled => InstalledVersion is not null;

    /// <summary>Die Builds, fuer die das Paket erklaert ist — fuer den Hinweis in der Liste, wenn es
    /// nicht zum laufenden Client passt.</summary>
    public IReadOnlyList<int> DeclaredBuilds => Builds ?? [];

    /// <summary>An update exists when the catalog names a different version than what is installed.
    /// Deliberately a string inequality, not a version comparison: upstream versioning schemes differ
    /// wildly across addons, and "different from what we shipped" is the honest, checkable statement.</summary>
    public bool HasUpdate => IsManaged && IsInstalled && AvailableVersion is not null
                             && !string.Equals(InstalledVersion, AvailableVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Installed by hand by the player (or by another tool) — shown, never managed.</summary>
    public bool IsForeign => IsInstalled && !IsManaged;
}

public interface IAddonService
{
    /// <summary>The realm's addon catalog, offline-first (memory → network → disk cache). Returns an
    /// empty catalog rather than throwing: the addons screen must degrade to "nothing to offer right
    /// now", never to a crash or an error dialog.</summary>
    Task<AddonCatalog> GetCatalogAsync(bool force = false, CancellationToken ct = default);

    /// <summary>Ob der letzte Katalogabruf am Anmeldegatter endete statt am Netz oder am Server.
    /// Ein leerer Katalog hat zwei voellig verschiedene Gruende, und der Unterschied gehoert auf den
    /// Bildschirm: "es gibt nichts" schickt jemanden auf die Suche nach einem Problem, das nicht
    /// existiert, wenn in Wahrheit nur die Anmeldung abgelaufen ist. Voreinstellung false, damit
    /// jede vorhandene Attrappe unveraendert weiterlaeuft.</summary>
    bool CatalogNeedsSignIn => false;

    /// <summary>What the catalog offers for <paramref name="build"/>, joined with what is installed in
    /// <paramref name="clientDir"/> (the client root that holds <c>Interface/AddOns</c>).</summary>
    Task<IReadOnlyList<AddonStatus>> GetStatusAsync(string clientDir, int build, CancellationToken ct = default);

    /// <summary>Install or update one addon into <paramref name="clientDir"/>. Downloads, verifies the
    /// hash BEFORE extracting, and writes only into the folders the catalog declares.</summary>
    /// <param name="build">Der Client-Build, gegen den geprueft wird. 0 heisst "kein Client
    /// bekannt" und ueberspringt die Pruefung — das ist der Zustand vor der Client-Aufloesung, nicht
    /// eine Abkuerzung: der Produktionsweg (AddonsViewModel) gibt den Build immer mit.</param>
    Task<AddonActionResult> InstallAsync(string clientDir, AddonEntry entry, int build = 0,
        IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Remove an addon the launcher installed. Never removes a folder the launcher did not
    /// write — a hand-installed addon of the same name stays.</summary>
    Task<AddonActionResult> RemoveAsync(string clientDir, string addonId, CancellationToken ct = default);
}

/// <summary>
/// Addon management for one client install. The whole design follows from two facts:
///
/// <list type="number">
/// <item><b>An addon is a set of folders, not a file.</b> So the launcher records exactly which folders
/// it created (<see cref="InstalledAddonState"/>, written into the addons directory itself) and touches
/// nothing else. Removing "Questie" must not delete a Questie the player installed by hand, and updating
/// must not leave half of an old version behind.</item>
/// <item><b>The package comes off the network.</b> So the hash is verified BEFORE anything is extracted
/// (hard invariant: an unverified artefact is never unpacked), every entry is checked to stay inside the
/// declared folders (zip-slip and "writes into WTF/" both refused), and a package that violates either
/// is discarded whole rather than partially applied.</item>
/// </list>
///
/// <para>Addon settings live in <c>WTF/</c>, not in <c>Interface/AddOns/</c>, so removing or updating an
/// addon never destroys a player's configuration for it.</para>
/// </summary>
public sealed class AddonService : IAddonService
{
    /// <summary>The state file, inside the addons directory it describes. The leading dot keeps it out
    /// of the way; the game ignores anything that is not a folder with a .toc.</summary>
    internal const string StateFileName = ".stonetavern-addons.json";

    /// <summary>Where the catalog is served, relative to the distribution host (fallback source).</summary>
    internal const string CatalogFile = "addons.json";

    /// <summary>The website route that serves the SAME list the addons page shows. Preferred source:
    /// the site owns the catalogue (uploads, moderation, removals), so a pack a GM removes disappears
    /// from both surfaces at once instead of the launcher keeping its own copy of the truth.</summary>
    internal const string CatalogRoute = "/api/launcher/addons";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly IConfigService _config;
    private readonly IDownloadService _downloads;
    private readonly Serilog.ILogger _log;
    private readonly string _cachePath;

    private AddonCatalog? _memCache;
    private DateTime _fetchedUtc = DateTime.MinValue;

    /// <summary>True when the catalogue currently held came off the network, false when it is the disk
    /// cache. An install is refused on a cached catalogue: a pack a GM removed is gone from the live
    /// list but still in yesterday's copy, and installing from that copy would defeat the moderation
    /// the whole single-source design exists for (Codex review 2026-07-27). Browsing offline is fine —
    /// only writing into the game folder needs the confirmation.</summary>
    private bool _catalogIsLive;

    /// <summary>Siehe <see cref="IAddonService.CatalogNeedsSignIn"/>.</summary>
    private bool _catalogNeedsSignIn;

    /// <inheritdoc />
    public bool CatalogNeedsSignIn => _catalogNeedsSignIn;

    /// <summary>Who is signed in. Optional so every existing construction site keeps working and so a
    /// test can leave it out; null means "no gate", which is the behaviour that shipped before
    /// 2026-08-05.</summary>
    private readonly ILauncherAuthService? _auth;

    public AddonService(HttpClient http, IConfigService config, IDownloadService downloads,
        IAppPaths paths, Serilog.ILogger log, ILauncherAuthService? auth = null)
    {
        _http = http;
        _config = config;
        _downloads = downloads;
        _log = log;
        _auth = auth;
        _cachePath = Path.Combine(paths.CacheDir, "addons-catalog.json");
    }

    /// <summary>The addons directory of a client install. The 1.12.1 client has it at the root; the
    /// 1.14.2 bundle keeps the client itself one level down, so both shapes are accepted and the one
    /// that exists wins. A fresh install with neither gets the root one created on first install.</summary>
    public static string AddonsDir(string clientDir)
    {
        var direct = Path.Combine(clientDir, "Interface", "AddOns");
        if (Directory.Exists(direct)) return direct;

        var modern = Path.Combine(clientDir, "World of Warcraft", "_classic_era_", "Interface", "AddOns");
        return Directory.Exists(modern) ? modern : direct;
    }

    // ── Catalog ───────────────────────────────────────────────────────────────────────────────

    public async Task<AddonCatalog> GetCatalogAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _memCache is not null && DateTime.UtcNow - _fetchedUtc < Freshness) return _memCache;

        // 🔴 Signed out means no catalogue at all - not the disk cache either (Owner 2026-08-05,
        // "addons soll auch gated sein durch login"). Serving yesterday's copy to somebody who is no
        // longer signed in would be a gate that only holds until the first time it was passed.
        var token = _auth?.CurrentToken;
        if (_auth is not null && (!_auth.IsLoggedIn || string.IsNullOrEmpty(token)))
        {
            _catalogIsLive = false;
            _catalogNeedsSignIn = true;
            // 🔴 Diese Zeile fehlte, und das kostete einen halben Vormittag. Am 2026-08-13 stand auf
            // der Addon-Seite "fuer diesen Client werden noch keine Addons angeboten", waehrend der
            // Server 36 Stueck fuer 1.14.2 auslieferte. Im Protokoll des Tages: kein einziger
            // Katalog-Eintrag, am Vortag vier. Ein Rueckweg ohne Spur sieht aus wie ein Aufruf, der
            // nie stattfand — drei verschiedene Ursachen, ein identisches Bild.
            _log.Information("Addon-Katalog nicht geholt: keine gueltige Anmeldung");
            return AddonCatalog.Empty;
        }

        _catalogNeedsSignIn = false;

        foreach (var url in CatalogUrls())
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<AddonCatalog>(json, JsonOpts);
                if (parsed is not null)
                {
                    _memCache = parsed;
                    _fetchedUtc = DateTime.UtcNow;
                    _catalogIsLive = true;
                    TryWriteCache(json);
                    _log.Information("Addon catalog: {Count} entries from {Url}", parsed.Addons.Count, url);
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Addon catalog fetch failed ({Url}) — trying the next source", url);
            }
        }

        var cached = TryReadCache();
        if (cached is not null)
        {
            _memCache = cached;
            _fetchedUtc = DateTime.UtcNow;
            _catalogIsLive = false;
            return cached;
        }

        _catalogIsLive = false;

        // Offline with nothing cached: an empty catalog, which the screen renders as "nothing to offer
        // right now". Never an invented list, never an error box.
        return AddonCatalog.Empty;
    }

    /// <summary>The sources to try, in order: the website route first, the distribution host's static
    /// <c>addons.json</c> second. Both serve the same schema, so the second is a genuine fallback (a
    /// site outage still leaves the launcher able to offer whatever the download host carries) rather
    /// than a second, competing catalogue.</summary>
    internal IReadOnlyList<string> CatalogUrls()
    {
        var cfg = _config.Load();
        var urls = new List<string>();

        var site = cfg.SiteBaseUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(site)) urls.Add(site.TrimEnd('/') + CatalogRoute);

        var host = cfg.PatchServerBaseUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(host)) urls.Add(host.TrimEnd('/') + "/" + CatalogFile);

        return urls;
    }

    private void TryWriteCache(string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception ex) { _log.Debug(ex, "Could not cache the addon catalog"); }
    }

    private AddonCatalog? TryReadCache()
    {
        try
        {
            return File.Exists(_cachePath)
                ? JsonSerializer.Deserialize<AddonCatalog>(File.ReadAllText(_cachePath), JsonOpts)
                : null;
        }
        catch (Exception ex) { _log.Debug(ex, "Could not read the cached addon catalog"); return null; }
    }

    // ── State on disk ─────────────────────────────────────────────────────────────────────────

    internal static InstalledAddonState ReadState(string addonsDir)
    {
        try
        {
            var path = Path.Combine(addonsDir, StateFileName);
            if (!File.Exists(path)) return new InstalledAddonState();
            return JsonSerializer.Deserialize<InstalledAddonState>(File.ReadAllText(path), JsonOpts)
                   ?? new InstalledAddonState();
        }
        catch
        {
            // A corrupt state file means "we know of nothing", not "delete everything": the worst case
            // is that a managed addon shows up as hand-installed, which is safe (it is then untouched).
            return new InstalledAddonState();
        }
    }

    private bool WriteState(string addonsDir, InstalledAddonState state)
    {
        try
        {
            Directory.CreateDirectory(addonsDir);
            var path = Path.Combine(addonsDir, StateFileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, WriteOpts));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not write the addon state file in {Dir}", addonsDir);
            return false;
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AddonStatus>> GetStatusAsync(
        string clientDir, int build, CancellationToken ct = default)
    {
        var catalog = await GetCatalogAsync(ct: ct).ConfigureAwait(false);
        var addonsDir = AddonsDir(clientDir);
        // Bewusst der ganze Katalog, nicht ForBuild(build): der Spieler soll auf JEDEM Client sehen,
        // was es gibt — auch die Pakete des anderen Clients, auch wenn diese Plattform den anderen
        // Client heute nicht anbietet (Owner-Entscheid 2026-08-12). Was nicht passt, ist als solches
        // markiert und wird von InstallAsync hart abgelehnt; die Liste ist eine Auskunft, keine
        // Freigabe.
        return Join(catalog.Addons.Where(x => x.IsUsable).ToList(), addonsDir, build);
    }

    /// <summary>The pure join of "what is offered" and "what is on disk", so the whole reporting rule
    /// is testable without a network or a UI.</summary>
    internal static IReadOnlyList<AddonStatus> Join(IReadOnlyList<AddonEntry> offered, string addonsDir,
        int build = 0)
    {
        var state = ReadState(addonsDir);
        var managed = state.Addons.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var rows = new List<AddonStatus>();

        foreach (var entry in offered)
        {
            managed.TryGetValue(entry.Id, out var record);

            // The record is only believed while its folders are still there: a player who deleted the
            // folder by hand has uninstalled it, whatever our state file remembers (measure the disk,
            // not the bookkeeping).
            var installed = record is not null && record.Folders.Count > 0
                            && record.Folders.All(f => Directory.Exists(Path.Combine(addonsDir, f)));

            var foreign = !installed && entry.Folders.Any(f => Directory.Exists(Path.Combine(addonsDir, f)));

            rows.Add(new AddonStatus(
                Id: entry.Id,
                Name: entry.Name,
                Summary: entry.Summary,
                Author: entry.Author,
                License: entry.License,
                Homepage: entry.Homepage,
                AvailableVersion: entry.Version,
                InstalledVersion: installed ? record!.Version : (foreign ? "" : null),
                IsManaged: installed,
                Size: entry.Size,
                // build == 0 heisst "kein Client bekannt": dann wird nichts als unpassend markiert,
                // sonst behauptet ein leerer Zustand, alles passe nicht.
                FitsClient: build == 0 || entry.SupportsBuild(build),
                Builds: entry.Builds));
        }

        return rows;
    }

    // ── Install / update ──────────────────────────────────────────────────────────────────────

    public async Task<AddonActionResult> InstallAsync(string clientDir, AddonEntry entry, int build = 0,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsUsable) return AddonActionResult.Failed(UnusableEntryMessage(entry));

        // 🔴 Die zweite Verteidigungslinie, und ab jetzt die einzige, die zaehlt. Vorher war der
        // Listenfilter in GetStatusAsync die ALLEINIGE Schranke: InstallAsync bekam den Build nicht
        // einmal uebergeben. Seit die Liste absichtlich auch fremde Pakete zeigt, waere das offen —
        // ein 1.14.2-Addon in einem 1.12-Client ruft API auf, die es dort nicht gibt, und nimmt die
        // Spieleroberflaeche mit. Der Build wird hier geprueft, nicht in der Anzeige, weil eine
        // Anzeige umgangen werden kann und eine Sperre im Installationspfad nicht.
        if (build != 0 && !entry.SupportsBuild(build))
        {
            _log.Warning("Refusing to install {Addon}: declared for {Declared}, client is {Build}",
                entry.Id, string.Join(",", entry.Builds), build);
            return AddonActionResult.Failed(WrongBuildMessage(entry.Name, entry.Builds, build));
        }

        var addonsDir = AddonsDir(clientDir);
        try { Directory.CreateDirectory(addonsDir); }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not create the addons folder {Dir}", addonsDir);
            return AddonActionResult.Failed(CannotWriteMessage(addonsDir));
        }

        // Confirm the addon is STILL offered, from a catalogue that was actually fetched just now. A
        // removed pack stays in the disk cache (and in a stale static fallback) and would otherwise
        // remain installable long after a GM pulled it.
        var live = await GetCatalogAsync(force: true, ct).ConfigureAwait(false);
        if (!_catalogIsLive) return AddonActionResult.Failed(CannotConfirmMessage(entry.Name));
        if (!live.Addons.Any(a => string.Equals(a.Id, entry.Id, StringComparison.OrdinalIgnoreCase) && a.IsUsable))
        {
            _log.Warning("Refusing to install {Addon}: it is no longer in the catalogue", entry.Id);
            return AddonActionResult.Failed(NoLongerOfferedMessage(entry.Name));
        }

        var state = ReadState(addonsDir);
        var known = state.Addons.FirstOrDefault(a => string.Equals(a.Id, entry.Id, StringComparison.OrdinalIgnoreCase));

        // Early conflict answer when the catalogue declares the layout — before a single byte is
        // downloaded. For a community upload the same rule is applied further down, once the verified
        // package has named its folders itself.
        if (entry.Folders.Count > 0 && ConflictIn(entry.Folders, addonsDir, state, entry.Id) is string early)
            return AddonActionResult.Failed(early);

        var zipPath = Path.Combine(Path.GetTempPath(), $"stonetavern-addon-{entry.Id}-{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(addonsDir, StagingPrefix + Guid.NewGuid().ToString("N")[..8]);
        var backup = Path.Combine(addonsDir, BackupPrefix + Guid.NewGuid().ToString("N")[..8]);

        // The backup holds the player's PREVIOUS version while the new one is moved into place. It is
        // ours to delete only while we know the player still has that version somewhere — the moment a
        // restore fails, the copy inside the backup is the only one left in the world, and the cleanup
        // below must keep its hands off it.
        var backupIsTheOnlyCopy = false;
        try
        {
            progress?.Report($"Downloading {entry.Name}…");
            var download = await _downloads.DownloadFileAsync(entry.Url, zipPath, null, ct).ConfigureAwait(false);
            if (!download.Ok)
            {
                _log.Error("Addon download failed: {Addon} ({Failure} {Detail})",
                    entry.Id, download.Failure, download.Detail);
                return AddonActionResult.Failed(DownloadFailedMessage(entry.Name, download.UserMessage));
            }

            // Verify BEFORE extracting. Not after, not "while": an unverified archive is never unpacked.
            progress?.Report("Checking the download…");
            if (!await _downloads.VerifyHashAsync(zipPath, entry.Sha256, ct).ConfigureAwait(false))
            {
                _log.Error("Addon hash mismatch: {Addon}", entry.Id);
                return AddonActionResult.Failed(HashMismatchMessage(entry.Name));
            }

            // Which folders this package owns. Declared by the catalogue when it knows, otherwise read
            // off the VERIFIED package: its top-level directories. Deriving is not trusting — the same
            // boundary is enforced against the package either way, it was just learned from the zip
            // rather than announced.
            var folders = entry.Folders.Count > 0 ? entry.Folders : DeriveFolders(zipPath);
            if (folders.Count == 0) return AddonActionResult.Failed(BadLayoutMessage(entry.Name));

            // The full conflict rule, now that the folders are known — and it runs on UPDATES too, not
            // only on first install (Codex review 2026-07-27): an upstream package that renames its
            // folder to one another managed addon owns would otherwise delete that addon's files while
            // "updating" itself, and a folder the player put there by hand would be overwritten.
            if (ConflictIn(folders, addonsDir, state, entry.Id) is string conflict)
                return AddonActionResult.Failed(conflict);

            // Extract into a STAGING folder first and swap only once the whole package is on disk
            // (Codex review): deleting the old version before the new one exists means a failure
            // halfway — full disk, read-only file, killed process — leaves the player with no addon at
            // all. Staging plus swap makes the visible states "old" and "new", never "gone".
            progress?.Report($"Installing {entry.Name}…");
            Directory.CreateDirectory(staging);
            if (!ExtractIntoAddons(zipPath, staging, folders, entry, out var error))
                return AddonActionResult.Failed(error);

            if (!SwapIntoPlace(addonsDir, staging, backup, folders, known?.Folders,
                    out var swapError, out backupIsTheOnlyCopy))
                return AddonActionResult.Failed(swapError);

            state.Addons.RemoveAll(a => string.Equals(a.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            state.Addons.Add(new InstalledAddonRecord
            {
                Id = entry.Id,
                Name = entry.Name,
                Version = entry.Version,
                Folders = [.. folders],
            });
            if (!WriteState(addonsDir, state))
            {
                // The files are there but we cannot prove ownership of them, and an addon we cannot
                // remove later is worse than one that was never installed. Taking the new version away
                // must not take the OLD one with it, though: without the restore below, a player who
                // owned a working addon and hit a read-only state file ended up owning nothing.
                RemoveFolders(addonsDir, folders);
                backupIsTheOnlyCopy = !RestoreFromBackup(addonsDir, backup);
                return AddonActionResult.Failed(CannotWriteMessage(addonsDir));
            }

            _log.Information("Addon installed: {Addon} {Version} → {Dir}", entry.Id, entry.Version, addonsDir);
            return AddonActionResult.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Addon install failed: {Addon}", entry.Id);
            return AddonActionResult.Failed(GenericFailureMessage(entry.Name));
        }
        finally
        {
            // Nothing of ours is ever left lying around, whichever way we left the method.
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* best effort */ }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }

            // …with one exception, and it is the whole point of the flag: when a restore failed, the
            // folders sitting in the backup are the player's only remaining copy of an addon they had
            // installed and working. Deleting it here would turn a failed update into permanent data
            // loss, quietly, on the cleanup path where nobody looks. It stays, and the message tells
            // the player where it is.
            if (backupIsTheOnlyCopy)
                _log.Warning("Keeping the addon backup at {Backup}: it holds the only copy of the " +
                    "player's previous {Addon}", backup, entry.Id);
            else
                try { if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true); } catch { }
        }
    }

    /// <summary>Caps for one addon package. Generous for real addons (the biggest quest database
    /// addons are tens of MB and a few thousand files) and far below "fills the disk".</summary>
    internal const int MaxEntries = 20_000;
    internal const long MaxUnpackedBytes = 512L * 1024 * 1024;

    /// <summary>Working directory names inside <c>Interface/AddOns</c>. Prefixed with a dot so the game
    /// ignores them, and always removed again — they only exist for the length of one install.</summary>
    internal const string StagingPrefix = ".stonetavern-staging-";
    internal const string BackupPrefix = ".stonetavern-backup-";

    /// <summary>
    /// Why <paramref name="folders"/> may not be written, or null when they may. Three refusals, all of
    /// them "someone else's files": a folder owned by ANOTHER managed addon (an upstream rename must not
    /// eat its neighbour), a folder that exists but belongs to nobody we know (hand-installed), and a
    /// folder that is a symlink or junction (extraction into it would write outside the addons directory
    /// entirely, which the per-entry path check cannot see because it is lexical).
    /// </summary>
    internal static string? ConflictIn(
        IReadOnlyList<string> folders, string addonsDir, InstalledAddonState state, string addonId)
    {
        var mine = state.Addons
            .FirstOrDefault(a => string.Equals(a.Id, addonId, StringComparison.OrdinalIgnoreCase))?.Folders
            ?? [];

        foreach (var folder in folders)
        {
            var owner = state.Addons.FirstOrDefault(a =>
                !string.Equals(a.Id, addonId, StringComparison.OrdinalIgnoreCase)
                && a.Folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)));
            if (owner is not null) return OwnedByOtherMessage(folder, owner.Name);

            var path = Path.Combine(addonsDir, folder);
            if (IsLink(path)) return LinkedFolderMessage(folder, path);

            var isMine = mine.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (!isMine && Directory.Exists(path)) return ForeignFolderMessage(folder, path);
        }
        return null;
    }

    /// <summary>True when the path is a symlink/junction (a reparse point). Such a folder is refused
    /// rather than followed: every path check the extractor does is lexical, so a link inside the addons
    /// directory is exactly the way out of it.</summary>
    private static bool IsLink(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return false;
            return new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null;
        }
        catch { return true; }   // cannot tell → treat as unsafe
    }

    /// <summary>
    /// Put the staged folders in place: move whatever is there now aside, move the new ones in, and roll
    /// the old ones back if any move fails. The swap is a sequence of renames inside ONE directory, so
    /// each step is atomic on every filesystem the launcher runs on, and the window in which a folder is
    /// absent is a rename rather than a copy.
    /// </summary>
    private bool SwapIntoPlace(string addonsDir, string staging, string backup,
        IReadOnlyList<string> folders, IReadOnlyList<string>? previous,
        out string error, out bool backupIsTheOnlyCopy)
    {
        error = "";
        backupIsTheOnlyCopy = false;
        Directory.CreateDirectory(backup);
        var movedAside = new List<string>();

        try
        {
            // Everything the new version replaces goes aside first: its own folders, plus any folder the
            // previous version owned that this one no longer ships (or it would keep loading).
            var toReplace = folders
                .Concat(previous ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var folder in toReplace)
            {
                var live = Path.Combine(addonsDir, folder);
                if (!Directory.Exists(live)) continue;
                Directory.Move(live, Path.Combine(backup, folder));
                movedAside.Add(folder);
            }

            foreach (var folder in folders)
                Directory.Move(Path.Combine(staging, folder), Path.Combine(addonsDir, folder));

            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Addon swap failed — rolling back to the previous version");
            // Roll back: remove whatever of the new version already landed, then put the old back.
            foreach (var folder in folders)
            {
                var live = Path.Combine(addonsDir, folder);
                try { if (Directory.Exists(live)) Directory.Delete(live, recursive: true); } catch { }
            }
            var restored = true;
            foreach (var folder in movedAside)
            {
                try { Directory.Move(Path.Combine(backup, folder), Path.Combine(addonsDir, folder)); }
                catch (Exception restoreEx)
                {
                    _log.Error(restoreEx, "Could not restore {Folder} from the backup at {Backup}", folder, backup);
                    restored = false;
                }
            }

            // A rollback that only mostly worked is not a rollback. Say so, and keep what is left.
            backupIsTheOnlyCopy = !restored;
            error = restored ? SwapFailedMessage(addonsDir) : SwapFailedAndKeptMessage(addonsDir, backup);
            return false;
        }
    }

    /// <summary>
    /// Move everything the backup holds back into the addons folder. True when the backup is empty
    /// afterwards — that is, when the player has their previous version back and the backup may be
    /// thrown away. False means at least one folder is still only in the backup, and the caller must
    /// leave it alone.
    /// </summary>
    internal bool RestoreFromBackup(string addonsDir, string backup)
    {
        if (!Directory.Exists(backup)) return true;

        var complete = true;
        foreach (var dir in Directory.GetDirectories(backup))
        {
            var name = Path.GetFileName(dir);
            try
            {
                var target = Path.Combine(addonsDir, name);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(dir, target);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Could not restore {Folder} from the backup at {Backup}", name, backup);
                complete = false;
            }
        }
        return complete;
    }

    /// <summary>
    /// The top-level directories of a package — what a community upload owns, since the website's
    /// catalogue records a name, a size and a hash but no folder layout. Returns an empty list when the
    /// package is not shaped like an addon: a file at the root (an addon is folders, and a loose file
    /// there would land directly in <c>Interface/AddOns</c>), or a top-level name that is not a plain
    /// folder name. Both are refusals, not things to work around — the empty list aborts the install.
    /// </summary>
    internal static List<string> DeriveFolders(string zipPath)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var zipEntry in zip.Entries)
            {
                var path = zipEntry.FullName.Replace('\\', '/').TrimStart('/');
                if (path.Length == 0) continue;

                var slash = path.IndexOf('/');
                if (slash < 0)
                {
                    // A directory entry without a trailing slash is legal in some writers; a FILE at the
                    // root is not an addon.
                    if (!string.IsNullOrEmpty(zipEntry.Name)) return [];   // a loose file at the root is not an addon
                    continue;
                }

                var top = path[..slash];
                if (!Models.AddonEntry.IsSafeFolderName(top)) return [];
                if (seen.Add(top)) folders.Add(top);
            }
        }
        catch (Exception)
        {
            return [];
        }
        return folders;
    }

    /// <summary>
    /// Unpack the archive, accepting ONLY entries that live inside the folders the catalog declares.
    /// Both traps are closed here: an entry whose path escapes the addons directory (zip slip) and an
    /// entry that is inside it but outside this addon's folders (a package writing into WTF/, or into
    /// another addon). Either one discards the whole package rather than applying part of it.
    /// </summary>
    internal bool ExtractIntoAddons(
        string zipPath, string addonsDir, IReadOnlyList<string> folders, AddonEntry entry, out string error)
    {
        error = "";
        var root = Path.GetFullPath(addonsDir);
        var allowed = folders
            .Select(f => Path.GetFullPath(Path.Combine(root, f)))
            .ToList();

        static bool IsInside(string candidate, string parent) =>
            candidate.Equals(parent, StringComparison.Ordinal)
            || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var wroteSomething = false;

            // A hash that matches proves the file is the one the catalogue names — it says nothing about
            // what unpacking it costs. A package can be small and expand to fill the disk, or carry a
            // million entries. Both are refused by measure, not by trust (Codex review 2026-07-27).
            if (zip.Entries.Count > MaxEntries)
            {
                _log.Error("Refusing addon package {Addon}: {Count} entries", entry.Id, zip.Entries.Count);
                error = OversizedPackageMessage(entry.Name);
                return false;
            }
            long declared = 0;
            foreach (var e in zip.Entries) declared += e.Length;
            if (declared > MaxUnpackedBytes)
            {
                _log.Error("Refusing addon package {Addon}: unpacks to {Bytes} B", entry.Id, declared);
                error = OversizedPackageMessage(entry.Name);
                return false;
            }

            foreach (var zipEntry in zip.Entries)
            {
                if (string.IsNullOrEmpty(zipEntry.Name) && zipEntry.FullName.EndsWith('/')) continue; // directory
                var target = Path.GetFullPath(Path.Combine(root, zipEntry.FullName));

                if (!IsInside(target, root) || !allowed.Any(a => IsInside(target, a)))
                {
                    _log.Error("Refusing addon package {Addon}: entry {Entry} writes outside its own folders",
                        entry.Id, zipEntry.FullName);
                    error = BadPackageMessage(entry.Name, zipEntry.FullName);
                    return false;
                }

                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                zipEntry.ExtractToFile(target, overwrite: true);
                wroteSomething = true;
            }

            if (!wroteSomething)
            {
                error = EmptyPackageMessage(entry.Name);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not unpack the addon package {Addon}", entry.Id);
            error = GenericFailureMessage(entry.Name);
            return false;
        }
    }

    // ── Remove ────────────────────────────────────────────────────────────────────────────────

    public Task<AddonActionResult> RemoveAsync(string clientDir, string addonId, CancellationToken ct = default)
    {
        var addonsDir = AddonsDir(clientDir);
        var state = ReadState(addonsDir);
        var record = state.Addons.FirstOrDefault(a => string.Equals(a.Id, addonId, StringComparison.OrdinalIgnoreCase));

        // Nothing WE installed → nothing we remove. A hand-installed addon of the same name is the
        // player's, and deleting it because the catalog knows the name would be destroying their data.
        if (record is null) return Task.FromResult(AddonActionResult.Failed(NotOursMessage(addonId)));

        RemoveFolders(addonsDir, record.Folders);
        state.Addons.Remove(record);
        if (!WriteState(addonsDir, state))
            return Task.FromResult(AddonActionResult.Failed(CannotWriteMessage(addonsDir)));

        _log.Information("Addon removed: {Addon}", addonId);
        return Task.FromResult(AddonActionResult.Success);
    }

    /// <summary>Delete the named folders, and only those, under the addons directory. Every name is
    /// re-checked here rather than trusted from the state file: that file is on disk and a hand-edited
    /// one must not be able to talk the launcher into deleting somewhere else.</summary>
    private void RemoveFolders(string addonsDir, IEnumerable<string> folders)
    {
        var root = Path.GetFullPath(addonsDir);
        foreach (var folder in folders)
        {
            if (!AddonEntry.IsSafeFolderName(folder))
            {
                _log.Error("Refusing to remove {Folder}: not a plain addon folder name", folder);
                continue;
            }
            var target = Path.GetFullPath(Path.Combine(root, folder));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); }
            catch (Exception ex) { _log.Warning(ex, "Could not remove addon folder {Folder}", target); }
        }
    }

    // ── Player-facing text ────────────────────────────────────────────────────────────────────

    private static string UnusableEntryMessage(AddonEntry entry) =>
        $"{(string.IsNullOrWhiteSpace(entry.Name) ? "This addon" : entry.Name)} cannot be installed: " +
        "the addon list on the server is incomplete for it. Try again later.";

    private static string CannotWriteMessage(string addonsDir) =>
        "The launcher could not write to the addons folder.\n" +
        $"Folder: {addonsDir}\n" +
        "Check that the client folder is not read-only, then try again.";

    private static string ForeignFolderMessage(string folder, string path) =>
        $"{folder} is already in your AddOns folder, but the launcher did not put it there.\n" +
        $"Folder: {path}\n" +
        "It is left untouched. Remove it yourself first if you want the launcher to manage it.";

    private static string OwnedByOtherMessage(string folder, string otherAddon) =>
        $"This package wants the folder {folder}, which belongs to {otherAddon}.\n" +
        "Nothing was changed. Two addons cannot share a folder, so one of them has to go first.";

    private static string LinkedFolderMessage(string folder, string path) =>
        $"{folder} is a link, not a real folder, so the launcher will not write through it.\n" +
        $"Folder: {path}\n" +
        "Replace it with a normal folder if you want the launcher to manage this addon.";

    private static string SwapFailedMessage(string addonsDir) =>
        "The addon could not be put in place, so the previous version was restored.\n" +
        $"Folder: {addonsDir}\n" +
        "Check that the client folder is not read-only and that the disk is not full, then try again.";

    /// <summary>When the rollback could not put everything back. The old version is not gone, but it is
    /// not in place either, and the player is the only one who can decide what to do with it — so they
    /// get the exact folder rather than a reassurance.</summary>
    private static string SwapFailedAndKeptMessage(string addonsDir, string backup) =>
        "The addon could not be put in place, and the previous version could not be moved back.\n" +
        $"It is not lost: the launcher kept it at {backup}\n" +
        $"Move the folders in there back into {addonsDir} yourself, or install the addon again.";

    private static string CannotConfirmMessage(string name) =>
        $"{name} was not installed: the launcher could not reach the realm to confirm this addon is " +
        "still offered.\nTry again when you are online.";

    /// <summary>Sagt beim Versions-Konflikt, WAS nicht passt — die reine Absage laesst den Spieler
    /// raten, ob der Download kaputt ist oder das Paket nicht zu seinem Spiel gehoert.</summary>
    private static string WrongBuildMessage(string name, IReadOnlyList<int> declared, int build)
    {
        static string Label(int b) => b switch
        {
            5875 => "1.12",
            42597 => "1.14.2",
            _ => b.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var forWhat = declared.Count == 0
            ? "no game version at all"
            : string.Join(" or ", declared.Select(Label));
        return $"{name} is made for {forWhat} and this client is {Label(build)}, "
             + "so the launcher did not install it. Addons built for another game version can break "
             + "the interface in ways that look like a broken client.";
    }

    private static string NoLongerOfferedMessage(string name) =>
        $"{name} is no longer offered on this realm, so the launcher did not install it.";

    private static string OversizedPackageMessage(string name) =>
        $"{name} was not installed: its package is far larger than an addon should be once unpacked.\n" +
        "Nothing was unpacked.";

    private static string DownloadFailedMessage(string name, string? detail) =>
        $"{name} could not be downloaded.\n" +
        (string.IsNullOrWhiteSpace(detail) ? "" : detail + "\n") +
        "The launcher log has the details.";

    private static string HashMismatchMessage(string name) =>
        $"{name} was not installed: the downloaded file did not match what the server says it should be.\n" +
        "Nothing was unpacked. Try again later.";

    private static string BadPackageMessage(string name, string entryPath) =>
        $"{name} was not installed: its package tries to write outside its own folder ({entryPath}).\n" +
        "Nothing was unpacked.";

    private static string BadLayoutMessage(string name) =>
        $"{name} was not installed: its package is not laid out like an addon (it must contain one or " +
        "more addon folders, and nothing loose beside them).\nNothing was unpacked.";

    private static string EmptyPackageMessage(string name) =>
        $"{name} was not installed: its package contains no files for this addon.";

    private static string GenericFailureMessage(string name) =>
        $"{name} could not be installed. The launcher log has the details.";

    private static string NotOursMessage(string addonId) =>
        $"{addonId} was not installed by the launcher, so it is not removed here. " +
        "Delete its folder yourself if you want it gone.";
}
