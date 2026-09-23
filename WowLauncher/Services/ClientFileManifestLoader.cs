namespace WowLauncher.Services;

using System.Security.Cryptography;
using System.Text.Json;
using WowLauncher.Models;

/// <summary>
/// Thrown when a fetched <c>files.json</c> cannot be trusted: its bytes do not match the
/// <see cref="ManifestFile.FilesSha256"/> the (already signature-verified) manifest bound it to, the
/// manifest describes a v2 per-file/delta layout without ever publishing that binding, or an entry's
/// path violates the path policy (ARCHITEKTUR-v2-patcher.md §2/§3).
///
/// <para>Deliberately a hard failure, not a null return. <see
/// cref="IManifestService.FetchFileManifestAsync"/> already has an offline-first null path for "the
/// optional Phase-1 file manifest could not be fetched" — this loader answers a different question,
/// "can what came back be trusted", and folding a trust failure into that same null would make a
/// tampered files.json indistinguishable from a server that simply has none, which is exactly the
/// silent-degrade the hash binding exists to prevent.</para>
/// </summary>
public sealed class ManifestTrustException : Exception
{
    public ManifestTrustException(string message) : base(message)
    {
    }

    public ManifestTrustException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Fetches the per-file manifest a client entry's <see cref="ManifestFile.FilesUrl"/> points at and
/// binds it to the signed <see cref="ManifestFile.FilesSha256"/> before handing it to a caller
/// (ARCHITEKTUR-v2-patcher.md §2, "Vertrauenskette"). This sits ABOVE
/// <see cref="IClientVerifyService"/>: that service trusts whatever <see cref="ClientFileManifest"/>
/// it is given, so something upstream has to establish that the document is the one the operator
/// actually signed off on.
/// </summary>
public interface IClientFileManifestLoader
{
    /// <summary>
    /// Loads and trust-checks the file manifest for <paramref name="client"/>.
    ///
    /// <list type="bullet">
    /// <item>No <see cref="ManifestFile.FilesUrl"/> → <c>null</c> (nothing to load, today's whole-ZIP
    /// path applies).</item>
    /// <item><c>FilesUrl</c> present, <see cref="ManifestFile.FilesSha256"/> present → the fetched
    /// bytes MUST hash to it, or this throws <see cref="ManifestTrustException"/>.</item>
    /// <item><c>FilesUrl</c> present, <c>FilesSha256</c> ABSENT, but <see cref="ManifestFile.FilesBase"/>
    /// or <see cref="ManifestFile.Deltas"/> are present → throws <see cref="ManifestTrustException"/>:
    /// a v2 layout without a hash binding cannot be trusted enough to drive deltas/per-file downloads.</item>
    /// <item><c>FilesUrl</c> present, no v2 fields at all → exactly today's behaviour: the document is
    /// parsed and returned unauthenticated beyond its own per-file hashes, which
    /// <see cref="IClientVerifyService"/> already re-checks entry by entry.</item>
    /// </list>
    ///
    /// A network failure (offline, timeout, non-2xx) or a malformed download that never made it to
    /// disk in one piece is reported as <c>null</c>, the same offline-first contract as
    /// <see cref="IManifestService.FetchFileManifestAsync"/> — only a document whose CONTENT is wrong
    /// throws.
    /// </summary>
    Task<ClientFileManifest?> LoadAsync(ManifestFile client, CancellationToken ct = default);
}

public sealed class ClientFileManifestLoader : IClientFileManifestLoader
{
    private readonly HttpClient _httpClient;
    private readonly Serilog.ILogger _log;

    public ClientFileManifestLoader(HttpClient httpClient, Serilog.ILogger log)
    {
        _httpClient = httpClient;
        _log = log.ForContext<ClientFileManifestLoader>();
    }

    public async Task<ClientFileManifest?> LoadAsync(ManifestFile client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (string.IsNullOrWhiteSpace(client.FilesUrl))
            return null;

        if (!ManifestService.IsValidManifestUrl(client.FilesUrl))
        {
            _log.Warning("files_url {Url} is not an absolute http(s) address — skipping", client.FilesUrl);
            return null;
        }

        byte[] raw;
        try
        {
            _log.Information("Fetching files.json from {Url}", client.FilesUrl);
            using var response = await _httpClient
                .GetAsync(client.FilesUrl, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            raw = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _log.Warning(ex, "files.json download failed — falling back to whole-ZIP repair");
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _log.Warning("files.json fetch timed out — falling back to whole-ZIP repair");
            return null;
        }

        var hasV2Fields = !string.IsNullOrWhiteSpace(client.FilesBase) || client.Deltas.Count > 0;

        if (!string.IsNullOrWhiteSpace(client.FilesSha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
            var expected = client.FilesSha256.Trim().ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new ManifestTrustException(
                    $"files.json at {client.FilesUrl} does not match files_sha256 " +
                    $"(expected {expected}, got {actual}) — refusing to trust it");
        }
        else if (hasV2Fields)
        {
            // A v2 layout (files_base/deltas) tells the patch engine where to fetch single files or
            // apply a binary delta. Doing that against a files.json nothing in the SIGNED manifest
            // vouches for would let whoever controls files_url alone redirect every one of those
            // downloads — the exact gap files_sha256 exists to close (ARCHITEKTUR-v2-patcher.md §2).
            throw new ManifestTrustException(
                $"manifest entry for build {client.Build} declares files_base/deltas but no " +
                "files_sha256 — refusing to trust files.json without a hash binding");
        }
        // else: no files_sha256, no v2 fields → exactly today's behaviour (unauthenticated files.json,
        // same as IManifestService.FetchFileManifestAsync).

        ClientFileManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(raw, ClientFileManifestJsonContext.Default.ClientFileManifest)
                ?? throw new ManifestTrustException($"files.json at {client.FilesUrl} deserialised to null");
        }
        catch (JsonException ex)
        {
            throw new ManifestTrustException($"files.json at {client.FilesUrl} is not valid JSON", ex);
        }

        foreach (var entry in manifest.Files)
        {
            ClientFilePathPolicy.Validate(entry.Path);
            ClientFilePathPolicy.ValidateOs(entry.Os);
        }

        return manifest;
    }
}

/// <summary>
/// The path policy a v2 files.json entry must satisfy (ARCHITEKTUR-v2-patcher.md §2/§3): relative,
/// <c>/</c>-separated, no <c>..</c> segment, no absolute path, no drive letter, no backslash.
///
/// <para><b>Why this is stricter than <see cref="ManifestPath"/>.</b> <see cref="ManifestPath"/>
/// backs the existing whole-ZIP verify/repair path and deliberately only LOGS a backslash violation
/// instead of refusing the manifest outright — refusing there locks every player out of updating over
/// one bad entry (arbitration 2026-09-14 in that file's own doc comment). Here the same kind of entry
/// can end up as the destination of a PER_FILE download or inside a butler delta's file list; a path
/// that escapes the install root there is not a display glitch, it is a write outside
/// <c>&lt;installRoot&gt;</c>. A new, trust-critical consumer earns its own strict rule rather than
/// loosening — or silently depending on — the lenient one.</para>
/// </summary>
public static class ClientFilePathPolicy
{
    public static void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ManifestTrustException("files.json entry has an empty path");

        if (path.Contains('\\', StringComparison.Ordinal))
            throw new ManifestTrustException($"files.json path '{path}' contains a backslash — '/' only");

        if (path.StartsWith('/'))
            throw new ManifestTrustException($"files.json path '{path}' is absolute");

        // A drive letter ("C:/x") only ever appears as a single letter followed by ':' — checking the
        // literal shape rather than calling Path.IsPathRooted keeps this rule identical on every OS
        // the launcher builds for (Path.IsPathRooted's answer for "C:/x" differs between Windows and
        // Unix .NET runtimes).
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            throw new ManifestTrustException($"files.json path '{path}' has a drive letter");

        if (path.Contains('\0', StringComparison.Ordinal))
            throw new ManifestTrustException($"files.json path '{path}' contains a NUL character");

        foreach (var segment in path.Split('/'))
        {
            if (IsReservedWindowsName(segment))
                throw new ManifestTrustException(
                    $"files.json path '{path}' contains the reserved Windows name '{segment}'");
        }

        if (path.Split('/').Any(segment => segment == ".."))
            throw new ManifestTrustException($"files.json path '{path}' contains '..'");
    }

    /// <summary>Windows refuses these as file names whatever the extension is ("CON", "con.txt",
    /// "LPT9.log") and the write fails there. Checking it here means one and the same package cannot
    /// pass on Linux and break every Windows install — the shared tree of KONZEPT §13 makes exactly
    /// that possible for the first time.</summary>
    private static bool IsReservedWindowsName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9';
    }

    /// <summary>The three OS values a v2 files.json entry's <c>os</c> field may carry (KONZEPT §13).
    /// Null/empty is not validated here — it means "every OS" and is the common case for a shared
    /// tree. An unrecognised value is refused rather than silently treated as "every OS" or "no OS":
    /// either guess could hide a whole platform's proxy from a player it was meant for, or hand
    /// everyone a file meant for one OS only, and both are worse than refusing the manifest outright.
    /// </summary>
    private static readonly string[] KnownOs = ["windows", "linux", "macos"];

    public static void ValidateOs(IReadOnlyList<string>? os)
    {
        if (os is null) return;
        foreach (var value in os)
        {
            if (!KnownOs.Any(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase)))
                throw new ManifestTrustException(
                    $"files.json entry declares os '{value}', which is not one of windows/linux/macos");
        }
    }

    /// <summary>
    /// The write-side rule PER_FILE has to check on the REAL filesystem (R1, KONZEPT §12): the path
    /// string may be clean and still lead outside the install root, because a symlink or NTFS junction
    /// already on disk redirects the write. Returns false with a reason when the resolved path leaves
    /// <paramref name="root"/> — the caller reports that as a finding instead of writing.
    ///
    /// <para>Walks the existing parts of the path; a part that does not exist yet cannot redirect
    /// anything and ends the walk. A link whose final target stays inside the root is fine (a player
    /// pointing their own Data folder at another drive is their business, as long as it is inside).</para>
    /// </summary>
    public static bool IsInsideRoot(string root, string path, out string reason)
    {
        reason = "";
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(path);
        var rootPrefix = rootFull + Path.DirectorySeparatorChar;

        if (!target.StartsWith(rootPrefix, comparison) && !target.Equals(rootFull, comparison))
        {
            reason = $"'{path}' resolves to '{target}', outside {rootFull}";
            return false;
        }

        var relative = Path.GetRelativePath(rootFull, target);
        var current = rootFull;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0 || part == ".") continue;
            current = Path.Combine(current, part);
            FileSystemInfo? info = Directory.Exists(current) ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;
            if (info is null) break;   // the rest does not exist yet -> nothing can redirect

            FileSystemInfo? resolved;
            try { resolved = info.ResolveLinkTarget(returnFinalTarget: true); }
            catch (IOException) { resolved = null; }
            if (resolved is null) continue;   // not a link (or unreadable, checked by the write itself)

            var resolvedFull = Path.GetFullPath(resolved.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!resolvedFull.Equals(rootFull, comparison) && !resolvedFull.StartsWith(rootPrefix, comparison))
            {
                reason = $"'{current}' is a link to '{resolvedFull}', outside {rootFull}";
                return false;
            }
        }
        return true;
    }
}
