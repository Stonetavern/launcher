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

    /// <summary>
    /// Picks the directory <paramref name="relativePaths"/> are relative to, starting at
    /// <paramref name="startDir"/> and walking up at most <see cref="MaxParentLevels"/> levels.
    /// The candidate where the most samples exist wins; ties go to the deepest candidate, so an
    /// unchanged flat layout keeps resolving to <paramref name="startDir"/> exactly as before.
    /// Returns <paramref name="startDir"/> when nothing matches anywhere — a fresh install has no
    /// files yet, and guessing a parent there would extract outside the chosen folder.
    /// </summary>
    public static string Resolve(string startDir, IEnumerable<string> relativePaths)
    {
        if (string.IsNullOrWhiteSpace(startDir)) return startDir;

        // Samples must be FILES, not directories: a directory entry like "WTF/" exists at several
        // levels of a nested tree and would vote for the wrong candidate.
        var samples = (relativePaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p) && !p.EndsWith('/') && !p.EndsWith('\\'))
            .Select(p => p.Replace('\\', Path.DirectorySeparatorChar)
                          .Replace('/', Path.DirectorySeparatorChar))
            .Take(24)
            .ToList();
        if (samples.Count == 0) return startDir;

        string? best = null;
        var bestHits = 0;

        var candidate = startDir;
        for (var level = 0; level <= MaxParentLevels && candidate is not null; level++)
        {
            if (Directory.Exists(candidate))
            {
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

        return bestHits > 0 ? best! : startDir;
    }
}
