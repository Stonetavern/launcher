namespace WowLauncher.Services.Platform;

using System.Diagnostics;
using WowLauncher.Localization;
using WowLauncher.Models;

/// <summary>
/// Result of making a platform's game runtime ready. <see cref="Error"/> is a finished, player-facing
/// English sentence that says what to do next — never a bare state ("could not be initialised") that
/// leaves the reader where they were. That was the actual defect on 2026-08-24: a player hit
/// "Wine prefix (GPTK) could not be initialised", had no idea what GPTK was, installed the wrong Wine
/// from Homebrew and was stuck for an evening.
/// </summary>
public sealed record RuntimeReadiness(bool Ok, string? Error = null)
{
    public static RuntimeReadiness Ready => new(true);
    public static RuntimeReadiness Failed(string error) => new(false, error);
}

/// <summary>
/// Makes the platform's game runtime available before a launch, downloading it if it is missing.
///
/// <para><b>Why this is its own service and not a branch inside the launcher.</b> Only macOS needs
/// one today. Windows runs the client natively and Linux resolves Wine from the system, so both get
/// the no-op. Keeping the decision here means the view model calls one method on every platform and
/// carries no <c>if (macOS)</c>.</para>
/// </summary>
public interface IGameRuntimeProvisioner
{
    /// <summary>True when <paramref name="clientExeName"/> needs a provisioned runtime on this
    /// platform. False short-circuits the whole step, including the "checking" message.</summary>
    bool NeedsRuntime(string clientExeName);

    /// <summary>Ensure the runtime is present. Returns immediately when it already is.</summary>
    Task<RuntimeReadiness> EnsureAsync(
        IProgress<DownloadProgress>? progress = null,
        IProgress<string>? step = null,
        CancellationToken ct = default);

    /// <summary>Path-aware variant: a runtime that lives in the client package (Linux <c>START.sh</c>,
    /// 2026-09-22) can only be found with the install path. Default = the name-only answer, so the
    /// macOS and no-op provisioners are unchanged.</summary>
    bool NeedsRuntimeForExe(string exePath) => NeedsRuntime(Path.GetFileName(exePath));

    /// <summary>Path-aware variant of <see cref="EnsureAsync"/>; same default rule as above.</summary>
    Task<RuntimeReadiness> EnsureForExeAsync(
        string exePath,
        IProgress<DownloadProgress>? progress = null,
        IProgress<string>? step = null,
        CancellationToken ct = default) => EnsureAsync(progress, step, ct);
}

/// <summary>Windows and Linux: nothing to provision. Deliberately not "return Ready" from a shared
/// base — an explicit type is greppable and cannot be mistaken for an unfinished branch.</summary>
public sealed class NoGameRuntimeProvisioner : IGameRuntimeProvisioner
{
    public bool NeedsRuntime(string clientExeName) => false;

    public Task<RuntimeReadiness> EnsureAsync(
        IProgress<DownloadProgress>? progress = null,
        IProgress<string>? step = null,
        CancellationToken ct = default) => Task.FromResult(RuntimeReadiness.Ready);
}

/// <summary>
/// macOS: fetches the free Gcenx "Game Porting Toolkit" Wine when it is not already installed.
///
/// <para><b>What this replaces.</b> Until 2026-08-24 the player had to find a 260 MB tarball on
/// GitHub, extract it to /Applications under an exact name, and run two Terminal commands to strip
/// Apple's quarantine flag and re-sign it. Nothing in the launcher said so; the failure surfaced as
/// one line about a "Wine prefix". A player reported it and had already installed Homebrew's
/// wine-devel, which this launcher never looks at and which cannot draw this client anyway.</para>
///
/// <para><b>Why we download from Gcenx rather than mirror it ourselves.</b> The toolkit bundles
/// Apple's <c>D3DMetal.framework</c>, which is closed source and whose redistribution terms are not
/// something this project has cleared. Fetching the same file the player could fetch by hand is not
/// redistribution by us. It also keeps the .app free of a component that would block the
/// open-source release outright. The cost is a hard dependency on a GitHub release URL — pinned by
/// tag AND by SHA256 below, so a moved or altered file fails closed instead of installing something
/// nobody checked.</para>
///
/// <para><b>The two post-extract commands are not optional.</b> A downloaded bundle carries
/// <c>com.apple.quarantine</c>; the ad-hoc signature inside the tarball does not survive Apple's
/// checks once it is set. Without both steps the toolkit is present, looks installed, and refuses to
/// run — the worst kind of failure, because every path check passes. This mirrors what the Homebrew
/// cask does in its postflight.</para>
/// </summary>
public sealed class MacGptkProvisioner : IGameRuntimeProvisioner
{
    /// <summary>Release 3.0-2, not the newer 3.0-3. This exact build is the one that was measured
    /// reaching the in-world state on 2026-07-23. 3.0-3 has never been tested here, and a player
    /// mid-problem is not the place to find out (2026-08-24).</summary>
    public const string DefaultUrl =
        "https://github.com/Gcenx/game-porting-toolkit/releases/download/" +
        "Game-Porting-Toolkit-3.0-2/game-porting-toolkit-3.0-2.tar.xz";

    /// <summary>Measured on the downloaded file, 260 757 848 bytes (2026-08-24). A mismatch means the
    /// artefact is not the one that was verified — it is deleted, never extracted.</summary>
    public const string DefaultSha256 =
        "c16b3b40b9a34853fc1f4546d13d20d28bc06e0f2edcfcf425df2ef7f2ec4ba4";

    /// <summary>Where the extracted toolkit lands. Chosen to match the SECOND candidate that
    /// <see cref="MacWineHost.ResolveWine64"/> already probes, so the resolver finds it with no
    /// change: the tarball's own top-level entry is "Game Porting Toolkit.app".</summary>
    public const string InstallDirName = "gptk";

    private const string BundleName = "Game Porting Toolkit.app";
    private const string ArchiveName = "game-porting-toolkit-3.0-2.tar.xz";

    /// <summary>The toolkit's wine64 is an x86_64 binary, so EVERY launch goes through
    /// <c>arch -x86_64</c>. On a fresh Apple Silicon Mac that translator is not installed: macOS
    /// offers to install it when a user double-clicks an Intel app in Finder, but a child process
    /// started from inside a running app gets no such prompt — the call just fails. The player would
    /// see "Wine could not be started" and have no way to guess why.
    ///
    /// This probe is the same mechanism the launch itself uses, so it cannot pass while the launch
    /// fails: run the smallest possible Intel binary and require exit 0.</summary>
    private const string ArchTool = "/usr/bin/arch";

    private readonly IDownloadService _download;
    private readonly IAppPaths _paths;
    private readonly Serilog.ILogger _log;
    private readonly string _url;
    private readonly string _sha256;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<int>> _run;
    private readonly Func<string, string?> _resolveWine;
    private readonly Func<bool> _needsRosetta;

    public MacGptkProvisioner(IDownloadService download, IAppPaths paths, Serilog.ILogger log)
        : this(download, paths, log, DefaultUrl, DefaultSha256, null, null, null) { }

    /// <summary>Test seam. The process runner and the wine resolver are injected so the whole
    /// sequence — including "the tool reported success but wine64 is still not there" — can be
    /// proven without a Mac, a network, or 260 MB.</summary>
    internal MacGptkProvisioner(
        IDownloadService download, IAppPaths paths, Serilog.ILogger log,
        string url, string sha256,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<int>>? run,
        Func<string, string?>? resolveWine,
        Func<bool>? needsRosetta)
    {
        _download = download;
        _paths = paths;
        _log = log;
        _url = url;
        _sha256 = sha256;
        _run = run ?? RunProcessAsync;
        _resolveWine = resolveWine ?? (shareDir => MacWineHost.ResolveWine64(shareDir, BundleResourcesDir()));
        // Eingehaengt, damit der Test die Frage "Apple Silicon?" selbst beantworten kann. Ohne diese
        // Naht liefe der Rosetta-Test auf jeder x64-Maschine (Fedora, CI) durch, ohne EINE Zusicherung
        // zu pruefen -- gruen, aber ohne Aussage. Genau die Sorte Test, die dieses Projekt nicht will.
        _needsRosetta = needsRosetta ?? (() =>
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64);
    }

    /// <summary>Only the modern (1.14.2) client runs through Wine on macOS; it is also the only one
    /// the Mac ships. The check stays explicit so a future 1.12 Mac path does not silently inherit a
    /// 260 MB download it does not need.</summary>
    public bool NeedsRuntime(string clientExeName) =>
        OperatingSystem.IsMacOS() && ClientVersion.ExeNameNeedsModernRuntime(clientExeName);

    public async Task<RuntimeReadiness> EnsureAsync(
        IProgress<DownloadProgress>? progress = null,
        IProgress<string>? step = null,
        CancellationToken ct = default)
    {
        var shareDir = _paths.ShareDir;

        // 🔴 Rosetta first, BEFORE the 260 MB download. Without it nothing that follows can work, and
        // finding out after the download wastes the player's evening twice over. Deliberately ahead
        // of the "already installed" fast path too: a toolkit that is present is still unusable
        // without the translator, and the message must name the cause either way.
        if (!await HasRosettaAsync(ct).ConfigureAwait(false))
        {
            _log.Error("Rosetta 2 is missing; {Tool} -x86_64 cannot run", ArchTool);
            return RuntimeReadiness.Failed(Loc.T("Runtime_Fail_Rosetta"));
        }

        // Already there? Say nothing and get out of the way — the common case must cost nothing.
        if (_resolveWine(shareDir) is { } existing)
        {
            _log.Debug("Game Porting Toolkit already present at {Path}", existing);
            return RuntimeReadiness.Ready;
        }

        _log.Information("Game Porting Toolkit not found; fetching {Url}", _url);
        step?.Report(Loc.T("Runtime_Downloading"));

        var archive = Path.Combine(_paths.CacheDir, ArchiveName);
        try { Directory.CreateDirectory(_paths.CacheDir); }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not create cache dir {Dir}", _paths.CacheDir);
            return RuntimeReadiness.Failed(Loc.T("Runtime_Fail_Disk"));
        }

        // A previously downloaded, VERIFIED archive is reused. Verified, not merely present: a
        // half-written file sits on disk looking exactly like a whole one.
        var haveArchive = File.Exists(archive)
                          && await _download.VerifyHashAsync(archive, _sha256, ct).ConfigureAwait(false);

        if (!haveArchive)
        {
            var result = await _download.DownloadFileAsync(_url, archive, progress, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                _log.Error("Toolkit download failed: {Failure} {Detail}", result.Failure, result.Detail);
                return RuntimeReadiness.Failed(result.UserMessage + " " + Loc.T("Runtime_Fail_Retry"));
            }

            if (!await _download.VerifyHashAsync(archive, _sha256, ct).ConfigureAwait(false))
            {
                // Fail closed and REMOVE it: leaving an unverified 260 MB archive behind means the
                // next run reuses it after the same check, forever.
                _log.Error("Toolkit checksum mismatch; deleting {Path}", archive);
                TryDelete(archive);
                return RuntimeReadiness.Failed(Loc.T("Runtime_Fail_Checksum"));
            }
        }

        step?.Report(Loc.T("Runtime_Installing"));
        var installDir = Path.Combine(shareDir, InstallDirName);
        var bundle = Path.Combine(installDir, BundleName);

        try { Directory.CreateDirectory(installDir); }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not create {Dir}", installDir);
            return RuntimeReadiness.Failed(Loc.T("Runtime_Fail_Disk"));
        }

        // .NET reads tar but not xz, so this goes through the system tar, which handles both on
        // macOS. Extraction is the only place a shell tool is unavoidable here.
        var untar = await _run("/usr/bin/tar", ["-xf", archive, "-C", installDir], ct).ConfigureAwait(false);
        if (untar != 0 || !Directory.Exists(bundle))
        {
            _log.Error("tar exited {Code}; bundle present: {Present}", untar, Directory.Exists(bundle));
            return RuntimeReadiness.Failed(Loc.T("Runtime_Fail_Extract"));
        }

        // Both of these, in this order. Skipping either leaves a toolkit that is present and refuses
        // to run. Their exit codes are logged but not fatal on their own: the only verdict that
        // counts is whether wine64 resolves AND starts, checked below.
        var dequarantine = await _run("/usr/bin/xattr",
            ["-drs", "com.apple.quarantine", bundle], ct).ConfigureAwait(false);
        var resign = await _run("/usr/bin/codesign",
            ["--force", "--deep", "-s", "-", bundle], ct).ConfigureAwait(false);
        _log.Information("xattr exited {X}, codesign exited {C}", dequarantine, resign);

        // 🔴 The acceptance check. "tar said 0" is not proof the runtime works — that is exactly the
        // green-but-wrong shape this project keeps getting caught by. Resolve the binary the launch
        // will actually use, then RUN it and require it to answer.
        var wine = _resolveWine(shareDir);
        if (wine is null || !File.Exists(wine))
        {
            _log.Error("wine64 still not resolvable under {Dir} after install", installDir);
            return RuntimeReadiness.Failed(Loc.T("Runtime_Fail_Incomplete"));
        }

        var probe = await _run(wine, ["--version"], ct).ConfigureAwait(false);
        if (probe != 0)
        {
            _log.Error("Installed wine64 at {Path} exited {Code} on --version", wine, probe);
            return RuntimeReadiness.Failed(Loc.T("Runtime_Fail_Blocked"));
        }

        _log.Information("Game Porting Toolkit installed and answering at {Path}", wine);
        return RuntimeReadiness.Ready;
    }

    /// <summary>True when x86_64 binaries can run. On an Intel Mac that is trivially the case, and on
    /// an Apple Silicon Mac it is exactly the question "is Rosetta 2 installed".
    ///
    /// The architecture test looks at THIS process: the arm64 build reports Arm64 and needs the
    /// probe; the x64 build reports X64, which can only be running because Rosetta is already
    /// translating it — probing there would be asking a question whose answer is standing in front
    /// of us.</summary>
    private async Task<bool> HasRosettaAsync(CancellationToken ct)
    {
        if (!_needsRosetta()) return true;

        try
        {
            return await _run(ArchTool, ["-x86_64", "/usr/bin/true"], ct).ConfigureAwait(false) == 0;
        }
        catch (Exception ex)
        {
            // A throwing probe is not a proven absence — but it is also not a usable runtime, and the
            // Rosetta message is the most useful thing to say either way.
            _log.Warning(ex, "Rosetta probe could not run");
            return false;
        }
    }

    /// <summary>The .app's Resources dir, sibling of the running binary's Contents/MacOS.</summary>
    private static string BundleResourcesDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources"));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort; the hash check gates use either way */ }
    }

    private static async Task<int> RunProcessAsync(
        string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi);
        if (process is null) return -1;

        // Drain both pipes, or a chatty tool fills the buffer and deadlocks the wait.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }
}
