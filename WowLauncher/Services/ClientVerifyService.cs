namespace WowLauncher.Services;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;

/// <summary>
/// Reported once verification of the whole file list finishes. <see cref="IsIntact"/> is the single
/// question <c>PlayViewModel.Repair()</c> needs answered: is there anything a re-download would fix.
/// </summary>
public sealed class VerifyReport
{
    public List<string> Missing { get; } = [];
    public List<string> Corrupt { get; } = [];
    public int Ok { get; set; }
    public int Total { get; set; }

    /// <summary>No missing and no corrupt files → the existing install already matches the manifest,
    /// nothing needs to be downloaded.</summary>
    public bool IsIntact => Missing.Count == 0 && Corrupt.Count == 0;
}

/// <summary>Fired after each file so the ActionBar can show "Checking N of M files" instead of a
/// frozen progress bar during a multi-minute hash pass over a 5 GB tree.</summary>
public sealed record VerifyProgress(int Checked, int Total, string CurrentPath);

/// <summary>
/// Checks an existing client install against a per-file manifest so <c>Repair</c> can tell whether a
/// re-download is actually needed, instead of always re-fetching the whole multi-GB ZIP
/// (<c>deploy/MANIFEST-SCHEMA.md</c> §files_url).
/// </summary>
public interface IClientVerifyService
{
    /// <summary>
    /// Checks every entry of <paramref name="manifest"/> against <paramref name="installDir"/>.
    /// Cheapest check first: a missing file never gets a size/hash check, a size mismatch never gets
    /// hashed — on a 5 GB tree that is the difference between seconds and minutes. Files the extractor
    /// preserves (WTF/, Interface/AddOns/, Screenshots/, realmlist.wtf) are never reported as defects,
    /// no matter what is on disk - the player owns that data.
    /// </summary>
    Task<VerifyReport> VerifyAsync(string installDir, ClientFileManifest manifest,
        IProgress<VerifyProgress>? progress = null, CancellationToken ct = default);
}

public sealed class ClientVerifyService : IClientVerifyService
{
    private readonly Serilog.ILogger _log;

    public ClientVerifyService(Serilog.ILogger log)
    {
        _log = log.ForContext<ClientVerifyService>();
    }

    public async Task<VerifyReport> VerifyAsync(string installDir, ClientFileManifest manifest,
        IProgress<VerifyProgress>? progress = null, CancellationToken ct = default)
    {
        var report = new VerifyReport { Total = manifest.Files.Count };
        var checkedCount = 0;

        // The manifest is rooted at the PACKAGE root, while installDir is the folder the executable
        // lives in — one or two levels deeper for the modern client (ContentRoot explains why).
        // Measuring against installDir reported all 1123 files of an intact install as missing and
        // triggered a full 8.5 GB re-download on every Repair (measured 2026-08-12).
        var root = ContentRoot.Resolve(installDir, manifest.Files.Select(f => f.Path));
        if (!string.Equals(root, installDir, StringComparison.Ordinal))
            _log.Information("Verify: manifest paths resolve against {Root}, not {InstallDir}", root, installDir);

        foreach (var entry in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            checkedCount++;
            progress?.Report(new VerifyProgress(checkedCount, report.Total, entry.Path));

            // Player-owned data the extractor itself never overwrites: never flag it as broken,
            // whatever state it is actually in on disk (Preserve rule, DownloadService.IsPreserved).
            if (DownloadService.IsPreserved(entry.Path))
            {
                report.Ok++;
                continue;
            }

            // Zustand, den der Client selbst fortschreibt (CASC-Indexgenerationen, lru_status,
            // shmem, .build.info). Er steht im Paket, kann aber nach dem ersten Spielstart nicht mehr
            // zum Manifest passen — als Defekt gezaehlt wuerde er jeden Repair in einen
            // Voll-Download zwingen.
            if (DownloadService.IsVolatileRuntimeState(entry.Path))
            {
                report.Ok++;
                continue;
            }

            // entry.Path is always "/"-separated on the wire (MANIFEST-SCHEMA.md); Path.Combine on
            // Linux would otherwise treat a literal "\" as part of the file name instead of a separator.
            var relative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(root, relative);

            // Stage 1 - existence. Cheapest possible check, no I/O beyond a stat.
            if (!File.Exists(fullPath))
            {
                report.Missing.Add(entry.Path);
                continue;
            }

            // Stage 2 - size. Still just a stat, no file content read. Catches truncated/replaced
            // files without ever touching the (potentially GB-sized) content.
            var actualSize = new FileInfo(fullPath).Length;
            if (actualSize != entry.Size)
            {
                report.Corrupt.Add(entry.Path);
                continue;
            }

            // Stage 3 - hash. Only reached once existence and size both already matched, and streamed
            // so a multi-GB file never sits fully in memory.
            string actualHash;
            try
            {
                await using var stream = File.OpenRead(fullPath);
                var digest = await SHA256.HashDataAsync(stream, ct);
                actualHash = Convert.ToHexString(digest).ToLowerInvariant();
            }
            catch (IOException ex)
            {
                // A file we can prove exists but cannot read (locked, permission, disk error) is not
                // provably intact either - treat it as corrupt rather than silently skipping it.
                _log.Warning(ex, "Verify: could not read {Path} for hashing, treating as corrupt", fullPath);
                report.Corrupt.Add(entry.Path);
                continue;
            }

            if (!string.Equals(actualHash, entry.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                report.Corrupt.Add(entry.Path);
                continue;
            }

            report.Ok++;
        }

        _log.Information("Verify: {Ok} ok, {Missing} missing, {Corrupt} corrupt (of {Total})",
            report.Ok, report.Missing.Count, report.Corrupt.Count, report.Total);
        return report;
    }
}
