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

    /// <summary>Entries that were actually looked for on disk — everything except the preserved and the
    /// volatile ones, which count as ok without any file access. Without this number a report of
    /// "5 ok, 1101 missing" reads like a mostly-broken install, when in truth NOTHING was found and the
    /// five are exemptions.</summary>
    public int Checkable { get; set; }

    /// <summary>How many of those checkable entries were found at the manifest path (whether or not
    /// their content matched). Zero is the interesting value: it means no part of this package layout
    /// exists here.</summary>
    public int FoundAtManifestPath { get; set; }

    /// <summary>No missing and no corrupt files → the existing install already matches the manifest,
    /// nothing needs to be downloaded.</summary>
    public bool IsIntact => Missing.Count == 0 && Corrupt.Count == 0;

    /// <summary>Below this, "everything is missing" is not evidence of anything — a small or partial
    /// manifest could legitimately miss entirely.</summary>
    public const int MinimumEntriesForLayoutVerdict = 100;

    /// <summary>
    /// The manifest describes a package that is NOT installed here — not a damaged copy of it.
    ///
    /// <para>The distinction matters because the two look identical from the outside and the responses
    /// are opposites: a damaged package is repaired by downloading it again, while a client that was
    /// never our package must be left alone. A player who brought his own Blizzard client
    /// (<c>~/Downloads/WoW Classic 1.14.2/</c>, report ST-KQYG-ARA3) got told his client needed an 8 GB
    /// update, over and over, because every single manifest path missed.</para>
    ///
    /// <para>Three conditions together, and the third carries the weight: enough entries to judge at all,
    /// essentially all of them missing, and — decisively — <b>not one manifest file present anywhere</b>.
    /// A genuinely damaged package still has files where the manifest says; they show up as corrupt or as
    /// found-and-wrong, and it stays repairable. That is why a bare percentage would not do.</para>
    /// </summary>
    public bool LooksLikeADifferentPackage =>
        Checkable >= MinimumEntriesForLayoutVerdict &&
        FoundAtManifestPath == 0 &&
        Corrupt.Count == 0 &&
        Missing.Count >= Checkable;
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

        // A backslash in a wire path is a generator bug (MANIFEST-SCHEMA.md §files[].path: "/" always).
        // On Windows it is harmless, on Unix such an entry will read as missing — which is correct, but
        // only useful if somebody can see WHY. Said once with a count, not once per entry.
        var offenders = manifest.Files.Count(f => ManifestPath.ViolatesSeparatorRule(f.Path));
        if (offenders > 0)
            _log.Warning(
                "Verify: {Count} of {Total} manifest paths contain a backslash — MANIFEST-SCHEMA.md requires \"/\". " +
                "On this platform they are taken literally, so they will read as missing unless the files really carry that name",
                offenders, manifest.Files.Count);

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

            // From here on the entry is really looked for on disk. Counting these separately is what
            // makes "nothing of this package is here" distinguishable from "this package is damaged".
            report.Checkable++;

            // entry.Path is always "/"-separated on the wire (MANIFEST-SCHEMA.md §files[].path); this
            // maps it onto the platform separator. On Windows that is all there is to it, because a
            // backslash cannot occur IN a Windows file name — it is a separator there no matter what.
            //
            // 🔴 On Unix a backslash is a perfectly legal character in a file name, so it must NOT be
            // reinterpreted as a separator: doing so would let the entry "dir\file" be satisfied by a
            // completely different file at "dir/file" — a verifier that reports a file as present
            // when it is not, which is the one direction this check may never fail in. An earlier
            // version of this fix did exactly that (and its test only proved the reinterpretation, not
            // its correctness); Codex review 2026-09-14 caught it. The schema violation is reported
            // instead of silently papered over, see ReportSchemaViolation below.
            var relative = ManifestPath.ToLocal(entry.Path);
            var fullPath = Path.Combine(root, relative);

            // Stage 1 - existence. Cheapest possible check, no I/O beyond a stat.
            if (!File.Exists(fullPath))
            {
                report.Missing.Add(entry.Path);
                continue;
            }

            report.FoundAtManifestPath++;

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

        _log.Information(
            "Verify: {Ok} ok, {Missing} missing, {Corrupt} corrupt (of {Total}; {Checkable} really checked, {Found} found at their manifest path)",
            report.Ok, report.Missing.Count, report.Corrupt.Count, report.Total,
            report.Checkable, report.FoundAtManifestPath);
        if (report.LooksLikeADifferentPackage)
            _log.Information(
                "Verify: not one of {Checkable} manifest files exists under {Root} — this install is not this package, so it is not damaged either",
                report.Checkable, root);
        return report;
    }
}
