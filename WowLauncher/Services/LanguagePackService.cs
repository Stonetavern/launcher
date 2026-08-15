namespace WowLauncher.Services;

using System.IO.Compression;
using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>
/// Was der Launcher ueber ein installiertes Sprachpaket sagen kann, wenn er es mit dem vergleicht,
/// was gerade veroeffentlicht ist.
/// </summary>
public enum LanguagePackState
{
    /// <summary>Diese Sprache liegt nicht auf der Platte (oder ist Englisch, das in den Basisdateien
    /// steckt und nie ein Paket braucht).</summary>
    NotInstalled,

    /// <summary>Installiert, und es stammt aus genau dem Archiv, das der Server anbietet.</summary>
    UpToDate,

    /// <summary>Installiert, stammt aber aus einem ANDEREN Archiv als dem angebotenen. Der einzige
    /// Zustand, in dem sich ein Neuladen lohnt.</summary>
    Outdated,

    /// <summary>Installiert, und niemand hat aufgeschrieben, woher es kam - eine Installation von
    /// vor dem Herkunftsmarker oder eine von Hand hingelegte Datei. Ausdruecklich NICHT "veraltet":
    /// wahrscheinlich ist es in Ordnung, und jemanden auf Verdacht 90 MB laden zu lassen, kostet
    /// echte Bandbreite fuer ein Vielleicht.</summary>
    OriginUnknown,

    /// <summary>Installiert, aber der Server bietet fuer diese Sprache gerade gar nichts an - es gibt
    /// also nichts, wogegen sich vergleichen liesse.</summary>
    NothingOffered,
}

/// <summary>Getting a language onto a 1.12.1 install: fetch the pack if it is not there yet, then hand
/// over to <see cref="VanillaLocalePacks"/>, which is what actually makes it take effect.</summary>
public interface ILanguagePackService
{
    /// <summary>
    /// Wie das installierte Paket zu dem steht, was der Server anbietet. Reine Auskunft: kein
    /// Download, keine Aenderung, kein Netz.
    ///
    /// <para><b>Warum das ueberhaupt gebraucht wird.</b> Der Wechsel fragt "liegt die Datei da" und
    /// laedt dann nicht mehr. Fuer einen Wechsel ist das richtig, fuer eine Fehlerbehebung falsch:
    /// wer deDE einmal installiert hat, behaelt es fuer immer, auch wenn wir ein korrigiertes Paket
    /// veroeffentlichen. Kein Fehler, keine Meldung - die Sprache gilt als installiert, und das ist
    /// sie ja auch, nur nicht die veroeffentlichte.</para>
    /// </summary>
    LanguagePackState State(string clientDir, string locale, ManifestLanguagePack? pack);

    /// <summary>
    /// Ein installiertes Paket durch das aktuell angebotene ERSETZEN - auch wenn schon eines da ist.
    /// Genau das tut <see cref="EnsureAsync"/> ausdruecklich nicht.
    ///
    /// <para>Ist die Sprache gerade aktiv, wird sie danach neu aktiviert, sonst laege die alte Fassung
    /// weiter im Patch-Platz und der Spieler saehe von der Aktualisierung nichts.</para>
    /// </summary>
    Task<VanillaLocaleResult> UpdateAsync(string clientDir, string locale, ManifestLanguagePack? pack,
        bool gameRunning = false, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>What this install can switch to right now, without a download.</summary>
    IReadOnlyList<string> Installed(string clientDir);

    /// <summary>The language the client will start in.</summary>
    string Active(string clientDir);

    /// <summary>
    /// Make <paramref name="locale"/> the language of this install, downloading its pack first when it
    /// is not present. Never throws: the result carries the language that IS active afterwards, so a
    /// caller can go ahead and launch with it.
    /// </summary>
    Task<VanillaLocaleResult> EnsureAsync(string clientDir, string locale, ManifestLanguagePack? pack,
        bool gameRunning = false, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// <inheritdoc cref="ILanguagePackService"/>
///
/// <para><b>Why the extraction is targeted rather than "unzip it".</b> A language pack is a ZIP from
/// the download host, and the launcher extracts it into a directory that also holds the client. Only
/// the one entry the pack is supposed to carry — <c>Data/&lt;loc&gt;/locale-&lt;loc&gt;.MPQ</c> — is
/// written; everything else in the archive is skipped and logged. A general extract would let a
/// tampered or mis-built pack drop a file next to <c>WoW.exe</c>, and it would look like a successful
/// install right up to the point where it is not.</para>
///
/// <para><b>The hash is checked before anything is extracted</b>, and an unverifiable download is
/// deleted rather than kept for a retry to resume onto.</para>
/// </summary>
public sealed class LanguagePackService : ILanguagePackService
{
    private readonly IDownloadService _download;
    private readonly VanillaLocalePacks _packs;
    private readonly Serilog.ILogger _log;

    public LanguagePackService(IDownloadService download, VanillaLocalePacks packs, Serilog.ILogger log)
    {
        _download = download;
        _packs = packs;
        _log = log.ForContext<LanguagePackService>();
    }

    public IReadOnlyList<string> Installed(string clientDir) => _packs.Installed(clientDir);

    public string Active(string clientDir) => _packs.Active(clientDir);

    /// <inheritdoc/>
    public LanguagePackState State(string clientDir, string locale, ManifestLanguagePack? pack)
    {
        // Englisch steckt in den Basisdateien und hat nie ein Paket. Es hier als "nicht installiert"
        // zu melden waere formal wahr und praktisch irrefuehrend.
        if (!VanillaLocalePacks.IsLocaleCode(locale) || locale == VanillaLocalePacks.BaseLocale)
            return LanguagePackState.NotInstalled;

        if (!_packs.Installed(clientDir).Contains(locale, StringComparer.Ordinal))
            return LanguagePackState.NotInstalled;

        // Kein Angebot heisst nicht "aktuell". Es heisst, dass es nichts gibt, wogegen man vergleichen
        // koennte - und ein Vergleich gegen nichts, der "aktuell" sagt, ist die Sorte beruhigende
        // Falschaussage, gegen die dieses Projekt seine Regeln hat.
        if (pack is null || string.IsNullOrWhiteSpace(pack.Sha256)
            || !string.Equals(pack.Locale, locale, StringComparison.Ordinal))
            return LanguagePackState.NothingOffered;

        var origin = _packs.PackOrigin(clientDir, locale);
        if (origin is null) return LanguagePackState.OriginUnknown;

        return string.Equals(origin, pack.Sha256.Trim(), StringComparison.OrdinalIgnoreCase)
            ? LanguagePackState.UpToDate
            : LanguagePackState.Outdated;
    }

    /// <inheritdoc/>
    public async Task<VanillaLocaleResult> UpdateAsync(string clientDir, string locale,
        ManifestLanguagePack? pack, bool gameRunning = false,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (!VanillaLocalePacks.IsLocaleCode(locale))
                return VanillaLocaleResult.Failed(_packs.Active(clientDir), $"{locale} is not a language code.");

            if (pack is null || string.IsNullOrWhiteSpace(pack.Url))
                return VanillaLocaleResult.Failed(_packs.Active(clientDir),
                    $"The server does not offer a {locale} language pack for this client.");

            if (!string.Equals(pack.Locale, locale, StringComparison.Ordinal))
                return VanillaLocaleResult.Failed(_packs.Active(clientDir),
                    $"The download coordinates say {pack.Locale}, not {locale}.");

            // 🔴 Vor dem Herunterladen, nicht danach. Die aktive Sprache liegt im Patch-Platz, und den
            // zu ersetzen, waehrend der Client laeuft, scheitert auf Windows und gelingt auf Linux -
            // wo das laufende Spiel danach aus einer Datei liest, die niemand mehr findet. Wer erst
            // 90 MB laedt und dann absagt, hat die Bandbreite trotzdem verbraucht.
            var wasActive = _packs.Active(clientDir) == locale;
            if (gameRunning && wasActive)
                return VanillaLocaleResult.Failed(locale,
                    "The game is running. Close it before updating the language.");

            // Die aktive Sprache zuerst aus dem Platz nehmen: sonst schriebe der Entpacker die neue
            // Fassung in den Ordner, waehrend die alte im Platz liegen bleibt - und der Spieler saehe
            // von der Aktualisierung nichts, obwohl alles Erfolg meldet.
            if (wasActive)
            {
                var parked = _packs.Apply(clientDir, VanillaLocalePacks.BaseLocale, gameRunning);
                if (!parked.Ok) return parked;
            }

            var failure = await FetchAsync(clientDir, locale, pack, progress, ct).ConfigureAwait(false);
            if (failure is not null)
            {
                // Fehlgeschlagen: den vorherigen Zustand wiederherstellen. Der Spieler hatte eine
                // funktionierende Sprache, und die soll er behalten.
                if (wasActive) _packs.Apply(clientDir, locale, gameRunning);
                return VanillaLocaleResult.Failed(_packs.Active(clientDir), failure);
            }

            return wasActive
                ? _packs.Apply(clientDir, locale, gameRunning)
                : VanillaLocaleResult.Success(_packs.Active(clientDir));
        }
        catch (OperationCanceledException)
        {
            return VanillaLocaleResult.Failed(_packs.Active(clientDir), "The language download was cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Updating the {Locale} language pack failed", locale);
            return VanillaLocaleResult.Failed(_packs.Active(clientDir),
                $"The {locale} language pack could not be updated.");
        }
    }

    public async Task<VanillaLocaleResult> EnsureAsync(string clientDir, string locale,
        ManifestLanguagePack? pack, bool gameRunning = false,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (!VanillaLocalePacks.IsLocaleCode(locale))
                return VanillaLocaleResult.Failed(_packs.Active(clientDir), $"{locale} is not a language code.");

            // Already on disk (or English, which is in the base MPQs) — nothing to fetch.
            if (_packs.Installed(clientDir).Contains(locale, StringComparer.Ordinal))
                return _packs.Apply(clientDir, locale, gameRunning);

            if (pack is null || string.IsNullOrWhiteSpace(pack.Url))
                return VanillaLocaleResult.Failed(_packs.Active(clientDir),
                    $"The server does not offer a {locale} language pack for this client.");

            if (!string.Equals(pack.Locale, locale, StringComparison.Ordinal))
                return VanillaLocaleResult.Failed(_packs.Active(clientDir),
                    $"The download coordinates say {pack.Locale}, not {locale}.");

            var ok = await FetchAsync(clientDir, locale, pack, progress, ct).ConfigureAwait(false);
            if (ok is not null) return VanillaLocaleResult.Failed(_packs.Active(clientDir), ok);

            return _packs.Apply(clientDir, locale, gameRunning);
        }
        catch (OperationCanceledException)
        {
            return VanillaLocaleResult.Failed(_packs.Active(clientDir), "The language download was cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Installing the {Locale} language pack failed", locale);
            return VanillaLocaleResult.Failed(_packs.Active(clientDir),
                $"The {locale} language pack could not be installed.");
        }
    }

    /// <summary>Download, verify, extract. Returns null on success, or a player-facing reason.</summary>
    private async Task<string?> FetchAsync(string clientDir, string locale, ManifestLanguagePack pack,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var staging = Path.Combine(clientDir, "Data", ".lang-download");
        Directory.CreateDirectory(staging);
        var zipPath = Path.Combine(staging, $"lang-{locale}.zip");

        try
        {
            var result = await _download.DownloadFileAsync(pack.Url, zipPath, progress, ct).ConfigureAwait(false);
            if (!result.Ok)
                return $"The {locale} language pack could not be downloaded: {result.UserMessage}";

            if (!await _download.VerifyHashAsync(zipPath, pack.Sha256, ct).ConfigureAwait(false))
            {
                // Deleted, not kept: a resume would splice onto bytes already known to be wrong.
                TryDelete(zipPath);
                return $"The downloaded {locale} language pack did not match its checksum and was discarded.";
            }

            var problem = Extract(clientDir, locale, zipPath);
            if (problem is not null) return problem;

            // Woher es kam, sofort danebenschreiben. Ohne diese Zeile weiss der naechste Start nur,
            // DASS eine Sprache installiert ist, nicht WELCHE Fassung - und genau das war die Luecke:
            // ein korrigiertes Paket haette nie jemanden erreicht, der die Sprache schon hatte.
            if (!_packs.WritePackOrigin(clientDir, locale, pack.Sha256))
                _log.Warning("Installed the {Locale} pack but could not record where it came from - " +
                    "a later update to this pack will show as origin unknown instead of outdated", locale);

            return null;
        }
        finally
        {
            TryDelete(zipPath);
            try { if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging); }
            catch (Exception ex) { _log.Debug(ex, "Could not remove the language staging folder"); }
        }
    }

    /// <summary>Write the one file this pack is allowed to carry, and nothing else.</summary>
    private string? Extract(string clientDir, string locale, string zipPath)
    {
        var wanted = $"data/{locale}/locale-{locale}.mpq";
        var target = VanillaLocalePacks.PackPath(clientDir, locale);

        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').TrimStart('/')
                    .Equals(wanted, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
                return $"The {locale} language pack does not contain a {locale} language file.";

            foreach (var other in zip.Entries)
                if (!ReferenceEquals(other, entry) && !string.IsNullOrEmpty(other.Name))
                    _log.Warning("Skipping unexpected entry {Entry} in the {Locale} language pack",
                        other.FullName, locale);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);

            // Measured, not assumed: the client is about to be told this language exists.
            var written = new FileInfo(target);
            if (!written.Exists || written.Length != entry.Length)
            {
                TryDelete(target);
                return $"The {locale} language pack did not unpack completely.";
            }

            _log.Information("Installed the {Locale} language pack ({Bytes} bytes)", locale, written.Length);
            return null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unpacking the {Locale} language pack failed", locale);
            TryDelete(target);
            return $"The {locale} language pack could not be unpacked.";
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _log.Debug(ex, "Could not delete {Path}", path); }
    }
}
