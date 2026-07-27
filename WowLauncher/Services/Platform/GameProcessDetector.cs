namespace WowLauncher.Services.Platform;

using WowLauncher.Models;

/// <summary>
/// Detects whether a WoW client is currently running — the launcher must not write client files
/// (download/extract/repair) while the game holds locks on them (Hermes Q1). Cross-instance:
/// it also catches a client the player started outside the launcher (Codex A1).
/// </summary>
public interface IGameProcessDetector
{
    /// <summary>True if a WoW client is running. <paramref name="expectedExePath"/> is the absolute
    /// WoW.exe path when known; the Linux detector canonicalises against it, the Windows detector
    /// ignores it (its process-name scan is intentionally path-agnostic).</summary>
    bool IsGameRunning(string? expectedExePath);
}

/// <summary>
/// Windows detector — a case-insensitive process-name scan, derived from every known client exe
/// name (<see cref="ClientVersion.ExeNames"/>, extension stripped: "WoW", "WowClassic", …). This
/// deliberately matches a client the player launched by any means, so the launcher never overwrites
/// a locked install (Codex A1: do not regress this) — and covers every build, not only "WoW" (Codex
/// Finding 1: a running 1.14.2 client is process "WowClassic" and used to be invisible here).
/// </summary>
public sealed class WindowsGameProcessDetector : IGameProcessDetector
{
    /// <summary>Process names that are a running client but are NOT in the shared exe-name vocabulary
    /// (<see cref="ClientVersion.ExeNames"/>). The native Windows 1.14.2 launch starts
    /// <c>WowClassic_ForCustomServers.exe</c> (the static custom-server build), which runs under its own
    /// name — not "WowClassic". Without this the launcher would (1) miss a running custom-server client
    /// and overwrite files it has open during a download/repair (Hermes Q1), and (2) conclude the game
    /// "already ended" the instant it launched and reap the realm proxy mid-play. Kept OUT of
    /// <see cref="ClientVersion.ExeNames"/> on purpose: that list is install-discovery/build-resolution
    /// vocabulary, and the custom-server exe is a launch/runtime detail, not an install the launcher
    /// resolves a build from.</summary>
    private static readonly string[] AdditionalRuntimeNames =
        [Path.GetFileNameWithoutExtension(WindowsModernClientLayout.CustomServerExeName)];

    private readonly Serilog.ILogger _logger;

    public WindowsGameProcessDetector(Serilog.ILogger logger) => _logger = logger;

    public bool IsGameRunning(string? expectedExePath)
    {
        // Each client exe locks itself while running — never touch client files mid-game (Hermes Q1).
        // GetProcessesByName is case-insensitive on Windows → one scan per known name covers all casings.
        try
        {
            var names = ClientVersion.ExeNames
                .Select(exe => Path.GetFileNameWithoutExtension(exe))
                .Concat(AdditionalRuntimeNames);
            foreach (var name in names)
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(name);
                try
                {
                    if (procs.Length > 0) return true;
                }
                finally
                {
                    foreach (var p in procs) p.Dispose();
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Process scan failed");
            return false;
        }
    }
}

/// <summary>
/// Linux detector — instance-independent scan of <c>/proc</c>. A private-server client under Wine
/// shows up as the wine loader with the WoW.exe argument as a <em>Windows</em> path (e.g.
/// <c>Z:\opt\wow\WoW.exe</c> or <c>C:\Games\WoW\WoW.exe</c>), which never matches a Linux path by
/// raw string compare (Codex F2). Per PID we therefore: canonicalise <c>/proc/&lt;pid&gt;/exe</c>
/// through real symlink resolution and, for wine loaders, TRANSLATE the WoW.exe cmdline argument from
/// its Wine drive (<c>Z:</c> → <c>/</c>, <c>C:</c> → <c>&lt;WINEPREFIX&gt;/drive_c</c>) before
/// comparing to the (equally canonicalised) expected path. Conservative by design: when a WoW.exe
/// argument cannot be translated we treat the game as running — a false positive (a blocked download)
/// is safe; a false negative (writing into a live install) corrupts the client.
/// </summary>
public sealed class LinuxGameProcessDetector : IGameProcessDetector
{
    private static readonly string[] WineLoaders =
        ["wine", "wine64", "wine-preloader", "wine64-preloader", "wineserver"];

    private readonly Serilog.ILogger _logger;

    public LinuxGameProcessDetector(Serilog.ILogger logger) => _logger = logger;

    public bool IsGameRunning(string? expectedExePath)
    {
        // Canonicalise the expected path the SAME way candidates are (real symlink resolution), so a
        // symlinked install dir doesn't defeat the equality check (Codex F2a).
        string? expected = string.IsNullOrWhiteSpace(expectedExePath) ? null : Canonicalise(expectedExePath);

        try
        {
            foreach (var pidDir in Directory.EnumerateDirectories("/proc"))
            {
                var leaf = Path.GetFileName(pidDir);
                if (!int.TryParse(leaf, out _)) continue; // only numeric PID dirs
                try
                {
                    if (MatchesPid(pidDir, expected)) return true;
                }
                catch
                {
                    // Process vanished mid-scan, or /proc entry not readable — skip, never assume running.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "/proc scan failed");
        }
        return false;
    }

    private bool MatchesPid(string pidDir, string? expected)
    {
        var exeTarget = ResolveLink(Path.Combine(pidDir, "exe"));
        if (exeTarget is null) return false;

        var exeLeaf = Path.GetFileName(exeTarget);

        // Direct case: the process image IS WoW.exe (started natively or via a thin wrapper).
        if (IsWowExeLeaf(exeLeaf))
            return Matches(Canonicalise(exeTarget), expected);

        // Wine case: the image is the wine loader; the real target is a cmdline argument carrying a
        // Windows path → translate it to its Linux path before comparing (Codex F2b).
        if (WineLoaders.Any(w => string.Equals(w, exeLeaf, StringComparison.OrdinalIgnoreCase)))
        {
            var cwd = ResolveLink(Path.Combine(pidDir, "cwd"));
            string? winePrefix = null;
            var prefixResolved = false;

            foreach (var arg in ReadCmdline(pidDir))
            {
                var leaf = Path.GetFileName(arg.Replace('\\', '/'));
                if (!IsWowExeLeaf(leaf)) continue;

                if (!prefixResolved) { winePrefix = ResolveWinePrefix(pidDir, cwd); prefixResolved = true; }

                var translated = TranslateWinePath(arg, winePrefix, cwd);
                if (translated is null)
                {
                    // Codex F2c: translation failed (unknown drive without a prefix, etc.). A running
                    // wine process with a WoW.exe argument we can't place is treated as "game running"
                    // — block the write. False positive > false negative for this guard.
                    _logger.Debug("Wine WoW.exe argument could not be translated ({Arg}), falling back to conservative basename match", arg);
                    return true;
                }

                if (Matches(Canonicalise(translated), expected)) return true;
            }
        }
        return false;
    }

    /// <summary>True for any known client exe leaf (<see cref="ClientVersion.ExeNames"/>), not only
    /// "WoW.exe" — a 1.14.2 Classic Era client under Wine shows up as WowClassic.exe, and matching
    /// only the vanilla name meant the Linux detector never saw it (Codex Finding 1).</summary>
    private static bool IsWowExeLeaf(string leaf) =>
        ClientVersion.ExeNames.Any(exe => string.Equals(leaf, exe, StringComparison.OrdinalIgnoreCase));

    // With a known expected path: exact canonical match (case-insensitive, covering the Windows part).
    // Without one: any WoW.exe counts (conservative).
    private static bool Matches(string canonicalCandidate, string? expected) =>
        expected is null
            ? IsWowExeLeaf(Path.GetFileName(canonicalCandidate))
            : string.Equals(canonicalCandidate, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Translate a Wine cmdline path to its Linux path. Returns null when it cannot be
    /// resolved (unknown drive without a reachable prefix) → caller does a conservative basename match.</summary>
    private static string? TranslateWinePath(string arg, string? winePrefix, string? cwd)
    {
        // Drive-letter Windows path: X:\… or X:/…
        if (arg.Length >= 2 && char.IsLetter(arg[0]) && arg[1] == ':')
        {
            var drive = char.ToUpperInvariant(arg[0]);
            var rest = (arg.Length > 2 ? arg[2..] : string.Empty).Replace('\\', '/').TrimStart('/');
            return drive switch
            {
                'Z' => "/" + rest,                                                    // Wine maps Z: → filesystem root
                'C' when winePrefix is not null => Path.Combine(winePrefix, "drive_c", rest),
                'C' => null,                                                          // no prefix → can't place C:
                _ when winePrefix is not null => ResolveDosDevice(winePrefix, drive, rest),
                _ => null,
            };
        }

        // Already a Unix path, or a relative one → resolve against the process cwd.
        var unix = arg.Replace('\\', '/');
        if (Path.IsPathRooted(unix)) return unix;
        return cwd is not null ? Path.Combine(cwd, unix) : null;
    }

    /// <summary>Resolve a non-C/Z Wine drive via its <c>dosdevices/&lt;x&gt;:</c> symlink, best-effort.</summary>
    private static string? ResolveDosDevice(string winePrefix, char drive, string rest)
    {
        try
        {
            var link = Path.Combine(winePrefix, "dosdevices", $"{char.ToLowerInvariant(drive)}:");
            var target = File.ResolveLinkTarget(link, returnFinalTarget: true);
            return target is null ? null : Path.Combine(target.FullName, rest);
        }
        catch { return null; }
    }

    /// <summary>Best-effort WINEPREFIX for a PID: explicit env → HOME/.wine → derive from a
    /// drive_c-nested cwd. Null when none can be determined.</summary>
    private static string? ResolveWinePrefix(string pidDir, string? cwd)
    {
        var env = ReadEnviron(pidDir);
        string? Lookup(string key)
        {
            var p = key + "=";
            foreach (var kv in env)
                if (kv.StartsWith(p, StringComparison.Ordinal))
                {
                    var v = kv[p.Length..];
                    return string.IsNullOrWhiteSpace(v) ? null : v;
                }
            return null;
        }

        var wp = Lookup("WINEPREFIX");
        if (wp is not null) return wp;
        var home = Lookup("HOME");
        if (home is not null) return Path.Combine(home, ".wine");

        // Derive: if the process runs inside a drive_c, the prefix is that dir's parent.
        if (cwd is not null)
        {
            var marker = Path.DirectorySeparatorChar + "drive_c";
            var idx = cwd.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return cwd[..idx];
        }
        return null;
    }

    /// <summary>Canonicalise a path: resolve a symlinked final component AND a symlinked parent dir
    /// to their real targets (best-effort realpath), else a lexical full path. Never throws.</summary>
    private static string Canonicalise(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return path; }

        // Walk every segment from the root and resolve link chains along the way — a symlink may
        // sit in ANY ancestor (/opt/link/sub/WoW.exe), not only in the final component or parent.
        try
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return full;
            var current = root;
            foreach (var segment in full[root.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo? resolved = null;
                try { resolved = Directory.ResolveLinkTarget(current, returnFinalTarget: true); }
                catch { /* kein Dir-Link */ }
                if (resolved is null)
                {
                    try { resolved = File.ResolveLinkTarget(current, returnFinalTarget: true); }
                    catch { /* kein File-Link / unzugänglich → lexical */ }
                }
                if (resolved is not null) current = resolved.FullName;
            }
            full = current;
        }
        catch { /* best-effort */ }

        return full;
    }

    private static string? ResolveLink(string linkPath)
    {
        try
        {
            var target = File.ResolveLinkTarget(linkPath, returnFinalTarget: true);
            return target?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadEnviron(string pidDir)
    {
        try
        {
            var raw = File.ReadAllText(Path.Combine(pidDir, "environ"));
            return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch { return []; }
    }

    private static IEnumerable<string> ReadCmdline(string pidDir)
    {
        string raw;
        try { raw = File.ReadAllText(Path.Combine(pidDir, "cmdline")); }
        catch { return []; }
        // /proc/<pid>/cmdline is NUL-separated with a trailing NUL.
        return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>
/// Neutral detector for platforms without a dedicated implementation yet (macOS and any other host,
/// Codex F6b). There is no <c>/proc</c> to scan and no launch path on these platforms, so it reports
/// "not running" rather than pretending a Linux scan applies.
/// </summary>
public sealed class StubGameProcessDetector : IGameProcessDetector
{
    private readonly Serilog.ILogger _logger;

    public StubGameProcessDetector(Serilog.ILogger logger) => _logger = logger;

    public bool IsGameRunning(string? expectedExePath)
    {
        _logger.Debug("Running-game detection is not supported on this operating system (WP1 stub)");
        return false;
    }
}

/// <summary>
/// macOS detector — a full-command-line process scan (<c>pgrep -f WowClassic</c>), the exact signal the
/// proven <c>Play-Stonetavern-FreeWine.command</c> uses. Path-agnostic like the Windows detector, and for
/// the same reason it cannot be a PID/path match: the 1.14.2 client runs under GPTK-Wine + Rosetta, so the
/// process the launcher started is the <c>arch</c>/<c>wine64</c> loader and the game itself shows up only
/// as a <c>WowClassic…</c> string in some Wine process's command line. A name scan catches it whether the
/// player started it through the launcher or by hand, so the launcher never overwrites a locked install
/// mid-game (Hermes Q1) and never reaps the realm proxy while a character is still in the world.
/// </summary>
public sealed class MacGameProcessDetector : IGameProcessDetector
{
    /// <summary>The substring that identifies a running client on the command line. Both the retail-named
    /// <c>WowClassic.exe</c> and the launched <c>WowClassic_ForCustomServers.exe</c> start with it, so one
    /// pattern covers the client however it was started.</summary>
    internal const string ClientPattern = "WowClassic";

    private readonly Serilog.ILogger _logger;
    private readonly Func<string, bool> _matches; // seam: "is any process' command line matching this?"

    public MacGameProcessDetector(Serilog.ILogger logger, Func<string, bool>? matches = null)
    {
        _logger = logger;
        _matches = matches ?? PgrepMatches;
    }

    /// <summary>True if a client process is running. <paramref name="expectedExePath"/> is intentionally
    /// ignored — the Wine command line carries a Windows path that never matches a macOS path by raw
    /// compare, so the scan keys on the process-name substring instead (same choice as the Windows detector).</summary>
    public bool IsGameRunning(string? expectedExePath)
    {
        try
        {
            return _matches(ClientPattern);
        }
        catch (Exception ex)
        {
            // On error, report not-running — the SAME choice the Linux (/proc) and Windows (name-scan)
            // detectors make (Codex review flagged the overwrite risk here; it applies equally to all three
            // and is a deliberate, codebase-wide convention, not a macOS regression). The reason it cannot
            // simply flip to "assume running on error": this same method is the appear-detection signal in
            // MacModernClientLauncher.WaitForClientAsync, where a spurious "running" on a transient pgrep
            // error would falsely report the client came up. pgrep is a base macOS tool always present, so
            // the realistic failure is a rare 5s timeout, not a missing binary; treating that as not-running
            // keeps both callers correct rather than fixing one and breaking the other.
            _logger.Debug(ex, "pgrep scan failed");
            return false;
        }
    }

    /// <summary>Real scan: <c>pgrep -f &lt;pattern&gt;</c> — exit 0 means at least one process' full command
    /// line matched. Never throws; a missing pgrep or any error is reported as "no match".</summary>
    private static bool PgrepMatches(string pattern)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("pgrep")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(pattern);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0; // pgrep: 0 = one or more matched, 1 = none, >1 = error
        }
        catch
        {
            return false;
        }
    }
}
