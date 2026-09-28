namespace WowLauncher.Services.Platform;

using WowLauncher.Models;

/// <summary>
/// The ONE GE-Proton build Stonetavern ships for Linux, shared by both clients.
///
/// <para><b>Why the modern client needed this (2026-09-23, player report "Linux Launcher Update broke
/// my game").</b> The 1.12.1 package's <c>START.sh</c> downloads this pinned GE-Proton itself. The
/// modern (1.14.2) client never had that: <see cref="GeProtonLocator"/> only looked in Steam's
/// <c>compatibilitytools.d</c>, so every player without Steam plus a hand-installed GE-Proton fell
/// through to system Wine, the setup the world-entry crash was measured on.</para>
///
/// <para><b>Same URL, checksum, folder and cache as <c>START.sh</c></b>
/// (<c>/mnt/data/wow/releases/classic-1.12.1/src/START.sh</c>, lines 26-36): a player who plays
/// both clients downloads it once, and the launcher and a manual <c>START.sh</c> run can never
/// disagree about which build is in use. A bump here is a bump there, after a green test run.</para>
/// </summary>
public static class PinnedGeProton
{
    public const string DirName = "GE-Proton11-7-x86_64";
    public const string Url =
        "https://github.com/GloriousEggroll/proton-ge-custom/releases/download/GE-Proton11-7/GE-Proton11-7-x86_64.tar.gz";
    public const string Sha256 = "c5448b76a230384e2d7bc6beb5ccb97bafb7e2c3b6c527cb03a1a546bbcb00a0";

    /// <summary>The marker <c>START.sh</c>'s <c>install_pinned</c> checks; written the same way here so
    /// the script accepts a folder the launcher unpacked.</summary>
    public const string OkMarker = ".stonetavern-ok";

    /// <summary><c>${STONETAVERN_DATA_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/stonetavern}</c>.</summary>
    public static string DataDir(Func<string, string?> env, string homeDir)
    {
        var explicitDir = env("STONETAVERN_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDir)) return explicitDir;
        var xdg = env("XDG_DATA_HOME");
        var dataHome = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(homeDir, ".local", "share") : xdg;
        return Path.Combine(dataHome, "stonetavern");
    }

    /// <summary>Holds <see cref="DirName"/>/proton; a runners directory in the sense of
    /// <see cref="GeProtonLocator.FindLatest"/>.</summary>
    public static string RunnersDir(Func<string, string?> env, string homeDir) =>
        Path.Combine(DataDir(env, homeDir), "runtime", "ge-proton");

    /// <summary><c>${XDG_CACHE_HOME:-$HOME/.cache}/stonetavern</c>, where the archive is kept.</summary>
    public static string CacheDir(Func<string, string?> env, string homeDir)
    {
        var xdg = env("XDG_CACHE_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(homeDir, ".cache") : xdg, "stonetavern");
    }

    public static string ArchiveName => Path.GetFileName(new Uri(Url).AbsolutePath);

    public static string HomeDir() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string RunnersDirForCurrentUser() => RunnersDir(Environment.GetEnvironmentVariable, HomeDir());
}

/// <summary>
/// Linux, modern client: downloads <see cref="PinnedGeProton"/> when no GE-Proton is installed, so the
/// client runs under Proton instead of falling back to system Wine. Checksum BEFORE unpacking, unpack
/// into a <c>.partial</c> folder and rename only when the build is complete.
/// </summary>
internal sealed class PinnedGeProtonInstaller
{
    private readonly IDownloadService _download;
    private readonly Serilog.ILogger _log;
    private readonly string _runnersDir;
    private readonly string _cacheDir;
    private readonly string _url;
    private readonly string _sha256;
    private readonly Func<string?> _findGeProton;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<int>> _run;

    public PinnedGeProtonInstaller(
        IDownloadService download, Serilog.ILogger log, string runnersDir, string cacheDir,
        Func<string?> findGeProton,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<int>>? run = null,
        string url = PinnedGeProton.Url, string sha256 = PinnedGeProton.Sha256)
    {
        _download = download;
        _log = log;
        _runnersDir = runnersDir;
        _cacheDir = cacheDir;
        _findGeProton = findGeProton;
        _run = run ?? RunProcessAsync;
        _url = url;
        _sha256 = sha256;
    }

    public async Task<RuntimeReadiness> EnsureAsync(
        IProgress<DownloadProgress>? progress, IProgress<string>? step, CancellationToken ct)
    {
        if (_findGeProton() is { } existing)
        {
            _log.Debug("GE-Proton already present at {Path}", existing);
            return RuntimeReadiness.Ready;
        }

        var archive = Path.Combine(_cacheDir, PinnedGeProton.ArchiveName);
        try
        {
            Directory.CreateDirectory(_cacheDir);
            Directory.CreateDirectory(Path.GetDirectoryName(_runnersDir)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Could not create {Cache} or the runtime folder", _cacheDir);
            return RuntimeReadiness.Failed(Localization.Loc.T("Runtime_Linux_Fail_Disk"));
        }

        // Verified, not merely present: a half-written archive looks exactly like a whole one.
        var haveArchive = File.Exists(archive)
                          && await _download.VerifyHashAsync(archive, _sha256, ct).ConfigureAwait(false);
        if (!haveArchive)
        {
            _log.Information("No GE-Proton installed; fetching the pinned {Name} from {Url}", PinnedGeProton.DirName, _url);
            step?.Report(Localization.Loc.T("Runtime_Linux_Downloading"));
            var result = await _download.DownloadFileAsync(_url, archive, progress, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                _log.Error("GE-Proton download failed: {Failure} {Detail}", result.Failure, result.Detail);
                return RuntimeReadiness.Failed(result.UserMessage + " " + Localization.Loc.T("Runtime_Fail_Retry"));
            }
            if (!await _download.VerifyHashAsync(archive, _sha256, ct).ConfigureAwait(false))
            {
                _log.Error("GE-Proton checksum mismatch; deleting {Path}", archive);
                TryDeleteFile(archive);
                return RuntimeReadiness.Failed(Localization.Loc.T("Runtime_Linux_Fail_Checksum"));
            }
        }

        step?.Report(Localization.Loc.T("Runtime_Linux_Installing"));
        var partial = _runnersDir + ".partial";
        try
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            Directory.CreateDirectory(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Could not prepare {Dir}", partial);
            return RuntimeReadiness.Failed(Localization.Loc.T("Runtime_Linux_Fail_Disk"));
        }

        // The system tar, exactly as START.sh does: GE-Proton carries symlinks and file modes that
        // have to survive the unpack, and every desktop Linux has tar.
        var untar = await _run("tar", ["-xf", archive, "-C", partial], ct).ConfigureAwait(false);
        var unpacked = Path.Combine(partial, PinnedGeProton.DirName);
        if (untar != 0 || !File.Exists(Path.Combine(unpacked, "proton"))
            || !File.Exists(Path.Combine(unpacked, "files", "bin", "wine")))
        {
            _log.Error("tar exited {Code}; proton present: {Proton}", untar, File.Exists(Path.Combine(unpacked, "proton")));
            TryDeleteDir(partial);
            return RuntimeReadiness.Failed(Localization.Loc.T("Runtime_Linux_Fail_Extract"));
        }

        try
        {
            await File.WriteAllTextAsync(Path.Combine(partial, PinnedGeProton.OkMarker), "", ct).ConfigureAwait(false);
            if (Directory.Exists(_runnersDir))
            {
                // A folder without a usable proton (the finder above said so): set it aside, never
                // unpack into it.
                var aside = $"{_runnersDir}.old.{Environment.ProcessId}";
                Directory.Move(_runnersDir, aside);
                TryDeleteDir(aside);
            }
            Directory.Move(partial, _runnersDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Could not move {Partial} to {Dir}", partial, _runnersDir);
            return RuntimeReadiness.Failed(Localization.Loc.T("Runtime_Linux_Fail_Disk"));
        }

        // The acceptance check is the same lookup the launch itself uses, not "tar said 0".
        if (_findGeProton() is { } installed)
        {
            _log.Information("Pinned GE-Proton installed at {Path}", installed);
            return RuntimeReadiness.Ready;
        }
        _log.Error("GE-Proton unpacked to {Dir} but the locator still finds none", _runnersDir);
        return RuntimeReadiness.Failed(Localization.Loc.T("Runtime_Linux_Fail_Incomplete"));
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<int> RunProcessAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) return -1;
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode;
    }
}
