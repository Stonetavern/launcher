namespace WowLauncher.Services.Platform;

using System.Text.RegularExpressions;

/// <summary>
/// Finds a Steam-installed <c>GE-Proton</c> compatibility tool, the runner the owner measured the
/// modern 1.14.2 client on with JimsProxy v5.2.1-beta.4 (Ledger run U, in the world,
/// <c>/mnt/data/wow/beta-test-linux/diag-results/LEDGER.txt</c>) — KONZEPT §13's new Linux runner
/// order puts this FIRST, ahead of <see cref="WineGeLocator"/>'s wine-ge and the system Wine.
///
/// <para><b>Why GE-Proton now, when <see cref="WineGeLocator"/>'s own doc comment argues the opposite
/// (owner decision 2026-07-21).</b> That decision was about D3D12 availability, which both runners
/// have — it was never about which one avoids the Wine world-entry crash <c>ProxyBinaryResolver</c>
/// documents. The 2026-07-21 measurement predates JimsProxy v5.2.1-beta.4 entirely; the run that
/// matters here is the one dated 2026-09-19, and it used GE-Proton, not wine-ge. Superseding, not
/// retracting: wine-ge stays a working fallback for a machine without Steam.</para>
///
/// <para>Selection mirrors the measured beta script exactly - glob
/// <c>~/.local/share/Steam/compatibilitytools.d/GE-Proton*</c> and
/// <c>~/.steam/root/compatibilitytools.d/GE-Proton*</c>, highest version wins, <c>sort -V</c>-equivalent
/// numeric comparison. A name like <c>GE-Proton9-27</c> mixes letters and digits within a segment
/// (unlike wine-ge's plain <c>wine-ge-8-26</c>), so the numeric key is pulled with a digit-run regex
/// rather than a plain <c>Split('-')</c> — lexical sorting would otherwise still put
/// <c>GE-Proton9-27</c> above <c>GE-Proton11-7</c>, the exact trap <see cref="WineGeLocator"/> already
/// documents for its own naming scheme.</para>
/// </summary>
public static class GeProtonLocator
{
    /// <summary>Both directories Steam is known to keep <c>compatibilitytools.d</c> under, relative to
    /// a home directory — a native Steam install and a Flatpak/alternate one under <c>~/.steam/root</c>.
    /// Both are probed; neither existing is not an error.</summary>
    public static IReadOnlyList<string> RunnersDirsFor(string homeDir) =>
    [
        Path.Combine(homeDir, ".local", "share", "Steam", "compatibilitytools.d"),
        Path.Combine(homeDir, ".steam", "root", "compatibilitytools.d"),
    ];

    /// <summary>The real Steam install <see cref="GeProtonEnvironment.Build"/> needs for
    /// <c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c> - the same native path the measured script defaults to
    /// (line 55: <c>STEAM_COMPAT_CLIENT_INSTALL_PATH="${STEAM_COMPAT_CLIENT_INSTALL_PATH:-$HOME/.local/share/Steam}"</c>).
    /// Not the alternate <c>~/.steam/root</c> location <see cref="RunnersDirsFor"/> also probes for
    /// runners - GE-Proton itself only ever needs the native path here, whichever directory it was
    /// actually found under.</summary>
    public static string SteamCompatClientInstallPathFor(string homeDir) =>
        Path.Combine(homeDir, ".local", "share", "Steam");

    /// <summary>The newest usable GE-Proton's <c>proton</c> script, or null if none is installed or
    /// executable. Injectable runner directories so tests can point at a fabricated tree instead of the
    /// machine's real Steam install (the launcher must never depend on what happens to be installed on
    /// the dev box).</summary>
    public static string? FindLatest(IReadOnlyList<string> runnersDirs)
    {
        ArgumentNullException.ThrowIfNull(runnersDirs);

        var candidates = new List<string>();
        foreach (var runnersDir in runnersDirs)
        {
            try
            {
                if (!Directory.Exists(runnersDir)) continue;
                candidates.AddRange(
                    Directory.EnumerateDirectories(runnersDir, "GE-Proton*")
                        .Select(d => Path.Combine(d, "proton"))
                        .Where(IsExecutable));
            }
            catch (Exception)
            {
                // An unreadable runners directory means "nothing usable here", not a crash - the
                // caller falls back to the next source in the runner order (umu, then wine-ge, then
                // system Wine), all of which are working paths.
            }
        }

        return candidates
            .OrderByDescending(p => VersionKey(RunnerNameOf(p)), VersionKeyComparer.Instance)
            .FirstOrDefault();
    }

    /// <summary>The newest GE-Proton for the current user, or null. Returns null off Linux - the
    /// Steam <c>compatibilitytools.d</c> layout is Linux-only and probing for it elsewhere would be a
    /// false positive waiting to happen.</summary>
    public static string? FindLatestForCurrentUser()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : FindLatest(AllRunnersDirsFor(Environment.GetEnvironmentVariable, home));
    }

    /// <summary>Steam's two runner folders plus Stonetavern's own (<see cref="PinnedGeProton"/>,
    /// shared with the 1.12 <c>START.sh</c>). Before 2026-09-23 only Steam's were searched, so a
    /// player without Steam never got Proton for the modern client.</summary>
    public static IReadOnlyList<string> AllRunnersDirsFor(Func<string, string?> env, string homeDir) =>
        [.. RunnersDirsFor(homeDir), PinnedGeProton.RunnersDir(env, homeDir)];

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path) || !OperatingSystem.IsLinux()) return false;
        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode anyExecute = UnixFileMode.UserExecute |
                                            UnixFileMode.GroupExecute |
                                            UnixFileMode.OtherExecute;
            return (mode & anyExecute) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string RunnerNameOf(string protonScriptPath)
    {
        // <compatibilitytools.d>/GE-Proton11-7/proton -> GE-Proton11-7
        var dir = Path.GetDirectoryName(protonScriptPath);
        return dir is null ? string.Empty : Path.GetFileName(dir);
    }

    /// <summary>Numeric segments of a runner name, in order: <c>GE-Proton11-7</c> -> [11, 7]. Pulled
    /// with a digit-run regex rather than <c>Split('-')</c> because "Proton11" mixes letters and
    /// digits in one dash-separated segment - splitting on '-' alone would leave "Proton11" unparsed
    /// as a whole and drop it from the key entirely.</summary>
    internal static IReadOnlyList<int> VersionKey(string runnerName) =>
        Regex.Matches(runnerName, @"\d+").Select(m => int.Parse(m.Value)).ToList();

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

/// <summary>
/// The environment a GE-Proton launch needs, built as a pure function so it is provable without
/// actually starting a process — mirrors the measured beta script exactly
/// (<c>/mnt/data/wow/beta-test-linux/Play Stonetavern (JimsProxy-Beta).sh</c> lines 40-58).
///
/// <para><b>What was NOT found in that script to carry over (2026-09-20 measurement gap, noted rather
/// than invented).</b> The task brief expected D3D12/vkd3d-specific environment lines "ab Zeile 104";
/// the actual file on disk (156 lines total) sets no D3D12/vkd3d/DXVK environment variables anywhere -
/// the D3D12 selection happens entirely through <c>Config.wtf</c>'s <c>gxApi</c> key, which
/// <see cref="WtfConfigWriter"/> already writes for the Linux and macOS modern-client paths. There is
/// nothing D3D12-specific to add here; if a future measurement finds real env vars, they belong beside
/// <see cref="Build"/>, not invented ahead of that evidence.</para>
/// </summary>
public static class GeProtonEnvironment
{
    /// <summary>The folder inside the client install that holds the per-GE-Proton prefixes. The patch
    /// engine skips it: it is the launcher's, not part of the client (ClientPatchEngine.IsLauncherOwned).</summary>
    public const string CompatFolderName = "proton-compat";

    /// <summary>
    /// <paramref name="installRoot"/>: the bundle root (parallel to <see cref="UmuOptions.PrefixPath"/>'s
    /// use of the launcher's own data dir - GE-Proton's prefix instead lives NEXT TO the client, exactly
    /// where the beta script puts it, so a player who has both GE-Proton and umu installed never shares
    /// a prefix between the two runners).
    /// <paramref name="protonDirName"/>: the GE-Proton directory name (<c>GE-Proton11-7</c>), used
    /// verbatim as the per-version prefix folder name so switching GE-Proton versions gets a fresh
    /// prefix rather than reusing one a different Proton build wrote.
    /// <paramref name="steamCompatClientInstallPath"/>: the real Steam install
    /// (<c>~/.local/share/Steam</c>) Proton needs to find shared runtime pieces; caller-supplied so a
    /// test never depends on a real Steam install being present.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(
        string installRoot, string protonDirName, string steamCompatClientInstallPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(protonDirName);
        ArgumentException.ThrowIfNullOrWhiteSpace(steamCompatClientInstallPath);

        var compatDataPath = Path.Combine(installRoot, CompatFolderName, protonDirName);
        return new Dictionary<string, string>
        {
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steamCompatClientInstallPath,
            ["STEAM_COMPAT_DATA_PATH"] = compatDataPath,
            ["WINEPREFIX"] = Path.Combine(compatDataPath, "pfx"),
        };
    }

    /// <summary>The GE-Proton directory name from its resolved <c>proton</c> script path, exactly the
    /// spelling <see cref="Build"/> needs for <paramref name="protonDirName"/> - one level up from the
    /// executable, same relationship <see cref="GeProtonLocator"/> uses internally for version
    /// sorting.</summary>
    public static string DirNameFromProtonExe(string protonExePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protonExePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(protonExePath));
        return dir is null ? "" : Path.GetFileName(dir);
    }
}
