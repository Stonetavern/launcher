namespace WowLauncher.Services.Platform;

/// <summary>
/// Finds a Lutris-installed <c>wine-ge</c> runner, the Wine build the modern 1.14.2 client is proven
/// to run on.
///
/// <para><b>Why wine-ge and not Proton (owner decision 2026-07-21).</b> An earlier note claimed D3D12
/// was Proton-only. It is not: the one start path proven end to end on this project - a player in the
/// world - is <c>Play Stonetavern.sh</c>, which uses a Lutris wine-ge runner and never touches Proton.
/// wine-ge carries <c>d3d12.dll</c>/<c>dxgi.dll</c> (vkd3d + DXVK) in the runner itself, so a prefix
/// booted with it comes up with the D3D12 layer present. umu/Proton stays as the fallback for players
/// without Lutris, but it is the second choice, not the first: it downloads roughly 2.9 GB on first
/// run and is unproven for this client.</para>
///
/// <para>Selection mirrors the shell script exactly - glob
/// <c>~/.local/share/lutris/runners/wine/wine-ge-*/bin/wine</c>, highest version wins - because that
/// is the selection the working path uses. Version ordering is numeric per segment (<c>sort -V</c>),
/// not lexical: lexical sorting puts <c>wine-ge-8-9</c> above <c>wine-ge-8-26</c> and would pick an
/// older runner while looking correct.</para>
/// </summary>
public static class WineGeLocator
{
    /// <summary>The Lutris runner directory holding the wine builds, relative to a home directory.</summary>
    public static string RunnersDirFor(string homeDir) =>
        Path.Combine(homeDir, ".local", "share", "lutris", "runners", "wine");

    /// <summary>The newest usable wine-ge runner binary, or null if none is installed. Injectable
    /// runner directory so tests can point at a fabricated tree instead of the machine's real Lutris
    /// install (the launcher must never depend on what happens to be installed on the dev box).</summary>
    public static string? FindLatest(string runnersDir)
    {
        List<string> candidates;
        try
        {
            if (!Directory.Exists(runnersDir))
                return null;
            candidates = Directory
                .EnumerateDirectories(runnersDir, "wine-ge-*")
                .Select(d => Path.Combine(d, "bin", "wine"))
                .Where(File.Exists)
                .ToList();
        }
        catch (Exception)
        {
            // An unreadable runner directory means "no wine-ge available", not a crash: the caller
            // falls back to umu/Proton, which is a working path, and a launcher must not die on a
            // permissions quirk in someone else's directory tree.
            return null;
        }

        return candidates
            .OrderByDescending(p => VersionKey(RunnerNameOf(p)), VersionKeyComparer.Instance)
            .FirstOrDefault();
    }

    /// <summary>The newest wine-ge for the current user, or null. Returns null off Linux - the Lutris
    /// layout is Linux-only and probing for it elsewhere would be a false positive waiting to happen.</summary>
    public static string? FindLatestForCurrentUser()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : FindLatest(RunnersDirFor(home));
    }

    private static string RunnerNameOf(string wineBinaryPath)
    {
        // <runners>/wine-ge-8-26-x86_64/bin/wine -> wine-ge-8-26-x86_64
        var bin = Path.GetDirectoryName(wineBinaryPath);
        var runner = bin is null ? null : Path.GetDirectoryName(bin);
        return runner is null ? string.Empty : Path.GetFileName(runner);
    }

    /// <summary>Numeric segments of a runner name, in order: <c>wine-ge-8-26-x86_64</c> -> [8, 26].
    /// Non-numeric parts are dropped, so the architecture suffix does not disturb the ordering.</summary>
    internal static IReadOnlyList<int> VersionKey(string runnerName) =>
        runnerName
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var n) ? n : (int?)null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToList();

    private sealed class VersionKeyComparer : IComparer<IReadOnlyList<int>>
    {
        public static readonly VersionKeyComparer Instance = new();

        public int Compare(IReadOnlyList<int>? x, IReadOnlyList<int>? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Max(x.Count, y.Count); i++)
            {
                var a = i < x.Count ? x[i] : 0;
                var b = i < y.Count ? y[i] : 0;
                if (a != b) return a.CompareTo(b);
            }
            return 0;
        }
    }
}
