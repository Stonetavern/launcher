using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WowLauncher.Services;

/// <summary>
/// Resolves the directory that manifest- and zip-relative paths are measured against.
///
/// Why this exists: <c>LauncherConfig.ClientInstalls[build]</c> stores the folder the game
/// EXECUTABLE lives in, because that is what launching needs (realmlist, Config.wtf, WTF/). The
/// download packages, however, are rooted one or two levels higher — the modern 1.14.2 package
/// expands to
/// <code>
///   &lt;package root&gt;/Hermes/...                          (1071 entries)
///   &lt;package root&gt;/World of Warcraft/_classic_era_/...   (52 entries, exe lives here)
/// </code>
/// so <c>Path.Combine(ClientInstalls[build], "Hermes/CSV/AreaNames.csv")</c> points at a file that
/// does not exist. Measured 2026-08-12: every one of the 1123 entries in the shipped
/// <c>modern-1.14.2-windows-files.json</c> misses that way, which made Repair report a fully intact
/// client as completely broken and re-download 8.5 GB — every single time. Extracting into that same
/// wrong directory would have nested the tree a second level deep.
///
/// Rather than add a second config field (and a migration for every existing install), the root is
/// derived from evidence on disk: try the start directory and its parents, and keep the candidate
/// where the sample paths actually resolve. That works for the flat Vanilla layout (root == exe dir)
/// and the nested modern layout alike, and keeps working if a future package changes its depth.
/// </summary>
public static class ContentRoot
{
    /// <summary>How many parent levels to consider. The modern package needs 2; 3 leaves headroom
    /// without ever escaping into unrelated territory like the user's home directory.</summary>
    public const int MaxParentLevels = 3;

    /// <summary>How many manifest paths the root vote considers.
    ///
    /// <para>This used to be 24, and 24 was too few. The manifest is alphabetical, so the first two dozen
    /// entries all live under <c>Hermes/CSV/</c>: if exactly that part of a package is damaged or was
    /// never extracted, every sample misses, the root falls back to the exe directory, and a client whose
    /// game files are perfectly fine is reported as ~1100 files missing — indistinguishable from a foreign
    /// client (Codex review 2026-08-24, finding 2). 4096 covers every package we ship whole. The cost is
    /// at most four stat calls per entry, once, immediately before a multi-gigabyte repair decision.</para>
    /// </summary>
    public const int MaxSamples = 4096;

    /// <summary>
    /// Picks the directory <paramref name="relativePaths"/> are relative to, starting at
    /// <paramref name="startDir"/> and walking up at most <see cref="MaxParentLevels"/> levels.
    ///
    /// <para>A parent is only ever a candidate when the package itself says so: the parent
    /// <c>L</c> levels up is admissible if some sample path has a directory prefix of exactly
    /// <c>L</c> components that equals the last <c>L</c> components of <paramref name="startDir"/>.
    /// For the modern package that is <c>World of Warcraft/_classic_era_</c>, two levels — the
    /// folder the exe lives in is literally named like the path inside the zip. A parent that the
    /// package does not describe is never considered, however many files happen to exist there.</para>
    ///
    /// <para>Why the vote alone was not enough (2026-09-04, three reports on one day): a player with a
    /// Blizzard client at <c>E:\World of Warcraft</c> and a Stonetavern install two levels below
    /// <c>E:\</c> had the drive root win the vote — 52 of the package's 1123 entries exist under a
    /// retail client, and 52 beats an emptied install's 0 — so update and repair extracted into a
    /// folder the player never chose and died on its read-only files. No share threshold separates
    /// that from a legitimate repair whose Hermes half is gone (also 52 hits, same ratio); the shape
    /// of the path does.</para>
    ///
    /// <para>Among admissible candidates the one where the most samples exist wins; ties go to the
    /// deepest, so an unchanged flat layout keeps resolving to <paramref name="startDir"/>. When no
    /// sample exists anywhere — an emptied install — the structurally matching parent is returned
    /// rather than <paramref name="startDir"/>, because extracting the modern package into its own
    /// exe folder nests a second <c>World of Warcraft/_classic_era_</c> inside the first. A flat
    /// layout has no such parent and still returns <paramref name="startDir"/>.</para>
    /// </summary>
    public static string Resolve(string startDir, IEnumerable<string> relativePaths)
    {
        if (string.IsNullOrWhiteSpace(startDir)) return startDir;

        // Samples must be FILES, not directories: a directory entry like "WTF/" exists at several
        // levels of a nested tree and would vote for the wrong candidate.
        // 🔴 The separator rule is ManifestPath's, not this method's own: until 2026-09-14 this half
        // mapped BOTH separators while ClientVerifyService mapped only "/", so on macOS and Linux an
        // entry with a backslash was counted as present here and reported missing there. One rule,
        // one place — and on Unix that rule leaves a backslash alone, because it is a legal character
        // in a file name and reinterpreting it would make a root vote count a file that is not there.
        var files = (relativePaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p) && !p.EndsWith('/') && !p.EndsWith('\\'))
            .Select(ManifestPath.ToLocal)
            .ToList();
        if (files.Count == 0) return startDir;

        // Every directory prefix the package contains — the shapes a parent must match to be
        // admissible. Built from ALL paths, not the capped sample: this is string work without a
        // single stat, and a package whose exe folder sorts behind the cap must still describe it.
        var packageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var parts = file.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var l = 1; l < parts.Length && l <= MaxParentLevels; l++)
                packageDirs.Add(string.Join(Path.DirectorySeparatorChar, parts, 0, l));
        }

        var samples = files.Count > MaxSamples ? files.Take(MaxSamples).ToList() : files;

        string[] startParts;
        try
        {
            startParts = Path.GetFullPath(startDir)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception) { return startDir; }

        string? best = null;
        var bestHits = 0;
        string? structural = null;   // the highest admissible parent, evidence or not

        var candidate = startDir;
        for (var level = 0; level <= MaxParentLevels && candidate is not null; level++)
        {
            if (IsAdmissible(level, startParts, packageDirs) && Directory.Exists(candidate))
            {
                if (level > 0) structural = candidate;

                var hits = samples.Count(s => File.Exists(Path.Combine(candidate, s)));
                // Strictly greater: the deepest candidate (tried first) wins ties.
                if (hits > bestHits)
                {
                    bestHits = hits;
                    best = candidate;
                }
            }

            try { candidate = Path.GetDirectoryName(Path.GetFullPath(candidate)); }
            catch (Exception) { break; }
        }

        return bestHits > 0 ? best! : structural ?? startDir;
    }

    /// <summary>Level 0 is the start directory itself and always admissible. A parent at level
    /// <paramref name="level"/> is admissible when the package contains a directory whose relative
    /// path equals the last <paramref name="level"/> components of the start directory.</summary>
    internal static bool IsAdmissible(int level, string[] startParts, HashSet<string> packageDirs)
    {
        if (level == 0) return true;
        if (level > startParts.Length) return false;
        var tail = string.Join(Path.DirectorySeparatorChar, startParts, startParts.Length - level, level);
        return packageDirs.Contains(tail);
    }
}
