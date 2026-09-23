namespace WowLauncher.Services.Platform;

/// <summary>
/// Finds the filesystem that actually holds a path.
///
/// <para><b>Why this exists.</b> The obvious form — <c>new DriveInfo(Path.GetPathRoot(dir))</c> — is
/// correct on Windows and wrong on Linux and macOS. <c>Path.GetPathRoot</c> is purely lexical: for
/// <em>every</em> absolute Unix path it returns <c>"/"</c>, so the probe measured the root filesystem
/// no matter where the client was being installed. On a Steam Deck the root partition is small and
/// nearly full while <c>/home</c> and the SD card under <c>/run/media/…</c> are separate, roomy
/// mounts — the launcher refused an install with "need ~17922 MB, have 866 MB on /" onto a disk that
/// had plenty. A player reported exactly that on 2026-08-28, and from where they stood the launcher
/// was simply lying.</para>
///
/// <para>Correct answer: of all mounted filesystems, the one whose mount point is the longest prefix
/// of the target path. That is the same rule <c>df</c> applies, and on Windows it degenerates to the
/// drive letter, so one implementation serves both.</para>
/// </summary>
public static class DiskSpace
{
    /// <summary>The filesystem containing <paramref name="path"/>, or <c>null</c> when it cannot be
    /// determined. Never throws: a failed probe is not evidence of a full disk, so the caller is meant
    /// to proceed rather than block on a guess.</summary>
    public static DriveInfo? ForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var full = Path.GetFullPath(path);

            DriveInfo? best = null;
            var bestLength = -1;

            foreach (var drive in DriveInfo.GetDrives())
            {
                string mount;
                try
                {
                    // A mount can disappear or be unreadable between enumeration and access
                    // (removable media, a stale network share) — skip it, never let it kill the probe.
                    if (!drive.IsReady) continue;
                    mount = drive.RootDirectory.FullName;
                }
                catch
                {
                    continue;
                }

                if (!IsPrefixOf(mount, full)) continue;
                if (mount.Length <= bestLength) continue;

                best = drive;
                bestLength = mount.Length;
            }

            // No mount matched (an unmounted target, an exotic path): fall back to the lexical root so
            // the caller still gets an answer on Windows, where that answer is the right one anyway.
            if (best is null)
            {
                var root = Path.GetPathRoot(full);
                if (!string.IsNullOrEmpty(root)) return new DriveInfo(root);
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="mount"/> is <paramref name="path"/> or contains it. Compared
    /// per path segment so that the mount "/run/media/x" does not claim to contain "/run/mediafoo".</summary>
    private static bool IsPrefixOf(string mount, string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var normalisedMount = Trim(mount);
        var normalisedPath = Trim(path);

        if (normalisedMount.Length == 0) return true;                       // "/" trims to empty
        if (string.Equals(normalisedMount, normalisedPath, comparison)) return true;

        return normalisedPath.StartsWith(normalisedMount, comparison)
            && normalisedPath.Length > normalisedMount.Length
            && normalisedPath[normalisedMount.Length] == Path.DirectorySeparatorChar;

        static string Trim(string s) =>
            s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
