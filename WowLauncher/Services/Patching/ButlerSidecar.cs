namespace WowLauncher.Services.Patching;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using WowLauncher.Services.Platform;

/// <summary>Result of one butler invocation. <see cref="Ok"/> is deliberately NOT just "exit code
/// 0" for <c>apply</c> — ARCHITEKTUR-v2-patcher.md §5/S0: "butler apply Exit 0 ist KEINE Aussage".
/// <see cref="ClientPatchEngine"/> always runs <see cref="IButlerSidecar.VerifyAsync"/> after
/// <see cref="IButlerSidecar.ApplyAsync"/> regardless of what this record says about the apply.</summary>
public readonly record struct ButlerResult(bool Ok, int ExitCode, string? Detail)
{
    public static ButlerResult Success(int exitCode) => new(true, exitCode, null);
    public static ButlerResult Fail(int exitCode, string detail) => new(false, exitCode, detail);
}

/// <summary>
/// The butler sidecar process (ARCHITEKTUR-v2-patcher.md §5): a pinned, hash-verified external binary
/// that applies a diff (<c>.pwr</c>) to an install tree and independently verifies the result against
/// a detached signature. The launcher never fetches butler itself at runtime — it ships pinned inside
/// the launcher's own release (<c>tools/butler/&lt;rid&gt;/</c>), the same trust model as every other
/// artefact this launcher writes to a player's disk.
/// </summary>
public interface IButlerSidecar
{
    /// <summary>True only when a butler binary for this OS/arch exists at the expected path AND its
    /// SHA-256 matches the pinned value RIGHT NOW — checked fresh on every read, never cached, so a
    /// binary replaced or corrupted after the launcher started is caught before the next Delta
    /// attempt (ARCHITEKTUR-v2-patcher.md §5: "vor jedem Aufruf nachgemessen"). False closes the
    /// Delta route; PER_FILE is unaffected.</summary>
    bool IsAvailable { get; }

    /// <summary>The pinned version string when <see cref="IsAvailable"/>, for logging/diagnostics
    /// only — nothing in this class branches on it.</summary>
    string? VersionOrNull { get; }

    /// <summary>Applies <paramref name="pwrPath"/> to <paramref name="targetDir"/> in place, using
    /// <paramref name="stagingDir"/> for butler's own checkpoints. Exit 0 is reported as
    /// <see cref="ButlerResult.Ok"/> but is NOT proof the tree is correct — the caller must still run
    /// <see cref="VerifyAsync"/> (S0 measured a case where <c>apply</c> exits 0 against the wrong base
    /// and silently produces a corrupt file).</summary>
    Task<ButlerResult> ApplyAsync(string pwrPath, string stagingDir, string targetDir,
        IProgress<string>? progress, CancellationToken ct);

    /// <summary>Verifies <paramref name="targetDir"/> against the detached signature at
    /// <paramref name="sigPath"/>. This is the actual proof step — <see cref="ClientPatchEngine"/>
    /// treats a Delta as successful only when THIS also reports <see cref="ButlerResult.Ok"/>.</summary>
    Task<ButlerResult> VerifyAsync(string sigPath, string targetDir, CancellationToken ct);

    /// <summary>
    /// Ensures a hash-verified butler is available for this OS/arch, fetching it into the per-OS
    /// data directory when neither the bundled copy next to the launcher (location 1) nor a
    /// previously fetched copy (location 2) already verifies.
    ///
    /// <para><b>Why this exists.</b> The Windows self-update swap only ships the exe
    /// (<c>launcher.url</c>), and the Linux self-update only ships the AppImage — neither carries
    /// <c>tools/butler/</c>. A player who self-updated from 1.8.x through either path has no butler
    /// next to their launcher at all, so the Delta route is closed forever unless something fetches
    /// it once (ARCHITEKTUR-v2-patcher.md §5, Teil A).</para>
    ///
    /// <para>Fetches all pinned files for this platform (the exe plus its two 7z sidecars) into a
    /// scratch directory, verifies EVERY one against the pin before anything is moved to the real
    /// location, and only then swaps it in — a failure partway through never leaves a partial or
    /// wrong-hash file at the target. Never throws for anything short of cancellation: a failure here
    /// just means <see cref="IsAvailable"/> stays false and PLAN falls through to PER_FILE.</para>
    /// </summary>
    Task<bool> EnsureAvailableAsync(CancellationToken ct);
}

/// <summary>One pinned sidecar file (name + hex SHA-256), relative to a butler RID directory.</summary>
internal readonly record struct ButlerFile(string Name, string Sha256);

/// <summary>Everything pinned for one OS: the RID directory name, which file is the executable
/// (for chmod/invocation), and every file — exe plus 7z sidecars — that must ALL verify for the
/// directory to count as a usable butler.</summary>
internal readonly record struct ButlerPin(string Rid, string ExeName, IReadOnlyList<ButlerFile> Files);

/// <inheritdoc cref="IButlerSidecar"/>
public sealed class ButlerSidecar : IButlerSidecar
{
    /// <summary>Pinned SHA-256 of the butler executable AND its two 7z/libc7zip sidecars — butler
    /// cannot open a <c>.pwr</c> without them, so an unpinned sidecar DLL/SO would be exactly the gap
    /// the exe pin exists to close. Sources: the exe hashes from
    /// <c>(internal design notes, not published)</c> (v15.31.0, checked
    /// 2026-09-14 against four independent sources — that repo's <c>BESCHAFFUNG.md</c>); the 7z
    /// sidecar hashes measured directly off the vendored files this repo already ships under
    /// <c>WowLauncher/tools/butler/&lt;rid&gt;/</c> (Teil A, 2026-09-19). itch.io publishes no
    /// checksum file for any of these, so this dictionary is the only thing standing between "the
    /// launcher runs a signed download" and "the launcher runs whatever happens to be at that URL
    /// today".</summary>
    private static readonly IReadOnlyDictionary<string, ButlerPin> Pins = new Dictionary<string, ButlerPin>
    {
        ["windows"] = new ButlerPin("win-x64", "butler.exe",
        [
            new ButlerFile("butler.exe", "2bbb92888003a374b9ea34103cadab74d237b64ce6a95c62a0001604fa4e6694"),
            new ButlerFile("7z.dll", "4b77ce85d5cac538cc2b1a2d498af607dd5650975fd67549422702545290ef57"),
            new ButlerFile("c7zip.dll", "9f79ae9b22d4b5a608eb35987419731ca85c7e531c9a9fa0af8099f783b6b9f5"),
        ]),
        ["linux"] = new ButlerPin("linux-x64", "butler",
        [
            new ButlerFile("butler", "578e1ebe8548ddf2a1b8374d5a85c0308668df3c06a2a1b9edb6ad1112c606eb"),
            new ButlerFile("7z.so", "334ed1aaaacd3ddefb41db6ae7c3d766d40782095e7a1ed6c7105b3ca9d1ba88"),
            new ButlerFile("libc7zip.so", "0370a19507b3c54e3ee8730feb344f54dc5819775f2c80a27d970084f9178c7c"),
        ]),
        // macOS (darwin-amd64): deliberately NOT pinned yet. Hash-pinning butler needs the same
        // four-independent-source method BESCHAFFUNG.md used for windows/linux, which has not run
        // for darwin-amd64. Left open rather than guessed — an unpinned "trust it" entry here would
        // be exactly the gap the pinning exists to close. IsAvailable is false on macOS until this
        // is filled in; the Delta route stays closed there, PER_FILE carries macOS in the meantime.
    };

    public const string Version = "15.31.0";

    /// <summary>Path below the download origin a missing/incomplete butler is fetched from when
    /// neither location on disk verifies — Teil A, ARCHITEKTUR-v2-patcher.md §5.
    ///
    /// <para>The origin is the launcher's own channel (<see cref="LauncherChannel.BaseUrl"/>), not a
    /// fixed host: until 2026-09-23 this was a constant pointing at the stable CDN, so a beta build
    /// and a <c>STONETAVERN_DOWNLOAD_BASE</c> test run both fetched from production, and the fetch
    /// path could never be exercised end to end before release (measured in the Windows E2E: every
    /// file answered 404 there, and nothing short of production could have shown it).</para></summary>
    internal const string DownloadPath = "/tools/butler";

    private readonly string _downloadBaseUrl;

    /// <summary>How long one butler invocation may run before it is killed — ARCHITEKTUR-v2-patcher.md
    /// §5. A multi-GB delta apply/verify measured in the tens of seconds on local SSD (S0); thirty
    /// minutes gives real headroom for a slow disk or a large delta without hanging forever on a
    /// wedged process.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private readonly string _baseDir;
    private readonly IDownloadService? _download;
    private readonly IAppPaths? _paths;
    private readonly Serilog.ILogger _log;

    /// <summary>Production wiring: bundled location is next to the running launcher,
    /// <paramref name="paths"/> gives the per-OS data dir <see cref="EnsureAvailableAsync"/> fetches
    /// into when the bundled copy is missing or fails its hash.</summary>
    public ButlerSidecar(IDownloadService download, IAppPaths paths, Serilog.ILogger log)
        : this(AppContext.BaseDirectory, download, paths, log, LauncherChannel.BaseUrl) { }

    /// <summary>Test seam: a fixed base directory instead of the running process's own, so a test can
    /// point this at a fixture folder without needing a real publish layout. No download service or
    /// app paths — this overload only ever proves <see cref="IsAvailable"/>/<see cref="ApplyAsync"/>/
    /// <see cref="VerifyAsync"/> against a bundled fixture; <see cref="EnsureAvailableAsync"/>'s fetch
    /// path is proven separately, with fakes, via the four-argument overload below.</summary>
    internal ButlerSidecar(string baseDir, Serilog.ILogger log) : this(baseDir, null, null, log) { }

    /// <summary>Full test seam: every dependency injectable.</summary>
    internal ButlerSidecar(string baseDir, IDownloadService? download, IAppPaths? paths, Serilog.ILogger log,
        string origin = LauncherChannel.StableBaseUrl)
    {
        _baseDir = baseDir;
        _downloadBaseUrl = origin.TrimEnd('/') + DownloadPath;
        _download = download;
        _paths = paths;
        _log = log.ForContext<ButlerSidecar>();
    }

    private static string OsKey =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported";

    /// <summary>True only when every pinned file in <paramref name="files"/> exists under
    /// <paramref name="dir"/> AND matches its pinned SHA-256, checked fresh — never cached, so a
    /// binary replaced or corrupted after the launcher started is caught before the next Delta
    /// attempt (ARCHITEKTUR-v2-patcher.md §5: "vor jedem Aufruf nachgemessen").</summary>
    private static bool DirIsValid(string dir, IReadOnlyList<ButlerFile> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(dir, file.Name);
            if (!File.Exists(path)) return false;
            try
            {
                using var stream = File.OpenRead(path);
                var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal)) return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Location (1), bundled next to the running launcher, wins whenever it verifies.
    /// Location (2), fetched on demand into the per-OS data dir, is the fallback for a
    /// Bestandsinstallation that self-updated through an exe-only or AppImage-only channel and never
    /// got <c>tools/butler/</c> on disk at all (Teil A).</summary>
    private string? ResolveValidDir()
    {
        if (!Pins.TryGetValue(OsKey, out var pin)) return null;

        var bundled = Path.Combine(_baseDir, "tools", "butler", pin.Rid);
        if (DirIsValid(bundled, pin.Files)) return bundled;

        if (_paths is not null)
        {
            var fetched = Path.Combine(_paths.ShareDir, "tools", "butler", Version, pin.Rid);
            if (DirIsValid(fetched, pin.Files)) return fetched;
        }

        return null;
    }

    private string? BinaryPath()
    {
        if (!Pins.TryGetValue(OsKey, out var pin)) return null;
        var dir = ResolveValidDir();
        return dir is null ? null : Path.Combine(dir, pin.ExeName);
    }

    public bool IsAvailable
    {
        get
        {
            if (!Pins.ContainsKey(OsKey)) return false;
            if (ResolveValidDir() is not null) return true;
            _log.Debug("patch: no valid butler sidecar for {Os} (bundled or fetched) — Delta route disabled", OsKey);
            return false;
        }
    }

    public string? VersionOrNull => IsAvailable ? Version : null;

    public async Task<bool> EnsureAvailableAsync(CancellationToken ct)
    {
        if (IsAvailable) return true;

        if (!Pins.TryGetValue(OsKey, out var pin))
            return false; // unsupported OS (macOS) — nothing pinned to fetch, PER_FILE carries it

        if (_download is null || _paths is null)
        {
            _log.Debug("patch: butler sidecar fetch skipped — no download service/app paths wired");
            return false;
        }

        var versionDir = Path.Combine(_paths.ShareDir, "tools", "butler", Version, pin.Rid);
        var tmpDir = versionDir + $".tmp-{Guid.NewGuid():N}";

        try
        {
            Directory.CreateDirectory(tmpDir);

            foreach (var file in pin.Files)
            {
                ct.ThrowIfCancellationRequested();
                var url = $"{_downloadBaseUrl}/{Version}/{pin.Rid}/{Uri.EscapeDataString(file.Name)}";
                var dest = Path.Combine(tmpDir, file.Name);

                var result = await _download.DownloadFileAsync(url, dest, progress: null, ct).ConfigureAwait(false);
                if (!result.Ok)
                {
                    _log.Warning("patch: butler sidecar fetch failed for {File} ({Url}): {Failure} {Detail}",
                        file.Name, url, result.Failure, result.Detail);
                    return false;
                }

                // Verified INSIDE tmpDir, before anything reaches versionDir — a bad file here returns
                // false immediately and the move below never runs, so the real location never sees a
                // partial or wrong-hash fetch.
                if (!await _download.VerifyHashAsync(dest, file.Sha256, ct).ConfigureAwait(false))
                {
                    _log.Warning(
                        "patch: downloaded butler file {File} does not match its pinned SHA-256 — discarding, Delta route stays disabled",
                        file.Name);
                    return false;
                }
            }

            if (Directory.Exists(versionDir))
            {
                try { Directory.Delete(versionDir, recursive: true); }
                catch (Exception ex)
                {
                    _log.Debug(ex, "patch: could not clear stale {Dir} before moving in the fresh butler fetch", versionDir);
                }
            }
            Directory.Move(tmpDir, versionDir);

            if (OperatingSystem.IsLinux())
            {
                var exePath = Path.Combine(versionDir, pin.ExeName);
                try
                {
                    File.SetUnixFileMode(exePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "patch: could not chmod +x the fetched butler binary at {Path}", exePath);
                    return false;
                }
            }

            var ok = DirIsValid(versionDir, pin.Files);
            if (ok)
                _log.Information("patch: butler sidecar fetched and verified at {Dir}", versionDir);
            else
                _log.Warning("patch: butler sidecar landed at {Dir} but failed final verification", versionDir);
            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "patch: could not fetch the butler sidecar into {Dir}", versionDir);
            return false;
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
            catch (Exception ex) { _log.Debug(ex, "patch: could not clean up scratch dir {Dir}", tmpDir); }
        }
    }

    public Task<ButlerResult> ApplyAsync(string pwrPath, string stagingDir, string targetDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(stagingDir);
        return RunAsync(["apply", $"--staging-dir={stagingDir}", pwrPath, targetDir], progress, ct);
    }

    public Task<ButlerResult> VerifyAsync(string sigPath, string targetDir, CancellationToken ct) =>
        RunAsync(["verify", sigPath, targetDir], null, ct);

    /// <summary>
    /// Runs butler with <c>--json</c> and reads its JSON-Lines stdout (measured shapes, 2026-09-19:
    /// <c>{"type":"log","level":..,"message":..}</c>, <c>{"type":"progress","progress":0..1,...}</c>,
    /// <c>{"type":"error","message":..}</c>). Anything on stderr that never made it onto stdout as
    /// structured JSON (butler prints a plain-text "bailing out: ..." there on a fatal error) is
    /// captured and folded into the failure detail rather than dropped.
    /// </summary>
    private async Task<ButlerResult> RunAsync(string[] args, IProgress<string>? progress, CancellationToken ct)
    {
        if (!IsAvailable)
            return ButlerResult.Fail(-1, "butler is not available (missing or hash mismatch)");

        var exe = BinaryPath()!;
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--json");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var errorMessages = new List<string>();
        var stderrLines = new List<string>();
        var sawError = false;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = e.Data;
            JsonElement root;
            try { root = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { return; } // not every line is guaranteed JSON — ignore, not fatal

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "error":
                    sawError = true;
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? line : line;
                    lock (errorMessages) errorMessages.Add(msg);
                    _log.Warning("patch: butler error: {Message}", msg);
                    break;
                case "log":
                    var text = root.TryGetProperty("message", out var lm) ? lm.GetString() : null;
                    if (!string.IsNullOrEmpty(text)) progress?.Report(text);
                    break;
                case "progress":
                    if (root.TryGetProperty("progress", out var p) && p.TryGetDouble(out var frac))
                        progress?.Report($"{frac * 100.0:F0}%");
                    break;
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderrLines) stderrLines.Add(e.Data);
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var killRegistration = timeoutCts.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _log.Debug(ex, "patch: could not kill butler process"); }
        });

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ButlerResult.Fail(-1, "cancelled");
        }
        catch (OperationCanceledException)
        {
            return ButlerResult.Fail(-1, $"butler did not finish within {Timeout.TotalMinutes:F0} minutes");
        }

        if (process.ExitCode == 0 && !sawError)
            return ButlerResult.Success(0);

        var detail = errorMessages.Count > 0
            ? string.Join("; ", errorMessages)
            : stderrLines.Count > 0
                ? string.Join("; ", stderrLines.Take(3))
                : $"butler exited {process.ExitCode}";
        return ButlerResult.Fail(process.ExitCode, detail);
    }
}
