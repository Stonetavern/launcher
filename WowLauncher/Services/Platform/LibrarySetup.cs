using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using WowLauncher.Localization;

namespace WowLauncher.Services.Platform;

/// <summary>The machine facts the setup reads and the one thing it does to the outside (start a
/// process), as a seam so every step can be tested against throwaway folders.</summary>
public sealed record LibrarySetupHost(
    InstallHostOs Os,
    string BaseDirectory,
    string? AppImagePath,
    string? ProcessPath,
    IReadOnlyDictionary<string, string> Env,
    bool IsElevated,
    Func<string, bool> IsNoExec,
    Func<string, long?> FreeBytes,
    Func<string, IReadOnlyList<string>, string, Func<bool>?> Start,
    TimeSpan HandoffTimeout)
{
    public static LibrarySetupHost ForCurrentProcess() => new(
        InstallEnvironmentProbes.CurrentOs(),
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } a ? a : null,
        Environment.ProcessPath,
        InstallEnvironmentProbes.CollectEnvironmentVars(),
        InstallEnvironmentProbes.IsElevated(),
        LinuxMountInfo.IsNoExec,
        path => DiskSpace.ForPath(path)?.AvailableFreeSpace,
        StartDetached,
        TimeSpan.FromMinutes(2));

    /// <summary>Start without the shell: the copy is the same program the player already chose to run,
    /// so there is no second "do you want to run this" in between. Returns "has it exited".</summary>
    private static Func<bool>? StartDetached(string file, IReadOnlyList<string> args, string workDir)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = workDir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        InheritedFds.KeepFromChildren();   // the copy must not pin this AppImage's mount (E2E 2026-09-28)
        // The launcher started here takes over as THE launcher: let go of the one-launcher lock first,
        // or it would find it held, bring this window forward and quit (SingleInstance).
        SingleInstance.Current?.Release();
        var p = Process.Start(psi);
        if (p is null)
        {
            SingleInstance.Current?.Reacquire();
            return null;
        }
        return () => { try { return p.HasExited; } catch (Exception) { return true; } };
    }
}

/// <summary>What the setup page shows for a chosen folder, before anything is written.</summary>
public sealed record LibraryCheck(string Root, IReadOnlyList<PreflightFinding> Findings, long? FreeBytes,
                                  string? ExistingLauncher)
{
    public bool IsBlocked => Findings.Any(f => f.Severity == PreflightSeverity.Block);
}

public enum LibrarySetupOutcome
{
    /// <summary>The launcher in the library is running and finished the setup: quit this one.</summary>
    HandedOver,

    /// <summary>Set up without moving the launcher (macOS, a dev run, a build without a payload list):
    /// continue in this process.</summary>
    SetUpInPlace,

    /// <summary>The folder already holds a finished library; its launcher was started: quit this one.</summary>
    AlreadySetUp,

    Failed,
}

public sealed record LibrarySetupResult(LibrarySetupOutcome Outcome, string? Message = null);

public enum LibrarySetupStage { Checking, Copying, Verifying, Starting, WaitingForHandoff }

/// <summary>
/// The first-start setup of the Stonetavern folder (<see cref="LibraryNames"/>), as a transaction
/// (Codex Terra 2026-09-28, blocker 4): lock, check, copy into a staging folder, prove the copy, start
/// it, and only when the copy has come up and finished the setup does this launcher quit. Until then
/// nothing outside the staging folder has changed, and any failure leaves this launcher running as
/// it was. Two double clicks cannot both set up the same folder (the lock).
///
/// <para>Who writes what: this (the original, usually in Downloads) copies and starts. The copy, started
/// with <see cref="LibraryProbe.HandoffArg"/>, claims the handoff, writes the config, the marker and the
/// Windows pointer, and says "done" only after its first frame is on screen
/// (<see cref="CompleteHandoff"/>). A crash before that frame (a missing DLL, SmartScreen, noexec) means
/// no "done", and the original rolls back and says so.</para>
/// </summary>
public sealed class LibrarySetup
{
    /// <summary>Both clients plus room to patch, rounded up: below this the setup warns.</summary>
    public const long RecommendedFreeBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>Not even the launcher fits: below this the setup refuses.</summary>
    public const long MinimumFreeBytes = 512L * 1024 * 1024;

    private readonly LibrarySetupHost _host;
    private readonly Serilog.ILogger _log;
    private readonly Action<string, string?> _commitInPlace;

    /// <param name="commitInPlace">Writes the config for a setup that does not move the launcher
    /// (root, launcher dir). Production: <see cref="Commit"/> against the real config service.</param>
    public LibrarySetup(LibrarySetupHost host, Serilog.ILogger log, Action<string, string?> commitInPlace)
    {
        _host = host;
        _log = log;
        _commitInPlace = commitInPlace;
    }

    // ── check ───────────────────────────────────────────────────────────────

    public LibraryCheck Check(string root)
    {
        var findings = new List<PreflightFinding>();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "SETUP_PATH_INVALID", "Setup_PathInvalid"));
            return new LibraryCheck(root, findings, null, null);
        }
        root = Path.GetFullPath(root);

        var existingAncestor = NearestExisting(root);
        var writable = existingAncestor is not null && ProbeWritable(Directory.Exists(root) ? root : existingAncestor);

        // The 1.8.11 rules (system folders, OneDrive, protected folders, path length, writable), checked
        // against the folder the 1.12.1 package goes into, exactly as the download checks it: that
        // 32-bit client has the path length limit, and the rule's reserve already covers every path
        // inside the package (its own top folder included). Counting that folder here as well refused
        // the suggested Windows folder for a user name of ten letters.
        var deepest = Path.Combine(root, LibraryNames.PackageDir(5875));
        findings.AddRange(InstallEnvironmentPreflight.Evaluate(new InstallEnvironmentFacts(
            _host.Os, _host.BaseDirectory, deepest, _host.IsElevated, _host.Env,
            HasQuarantineAttr: false, WriteProbeSucceeded: writable,
            LongestRelativePathInManifest: InstallEnvironmentPreflight.DefaultLongestRelativePathReserve,
            GameProcessRunning: false)));

        if (_host.Os == InstallHostOs.Linux && _host.IsNoExec(root))
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "SETUP_NOEXEC", "Setup_PathNoExec"));

        var free = existingAncestor is null ? null : _host.FreeBytes(existingAncestor);
        if (free is { } bytes && bytes < MinimumFreeBytes)
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "SETUP_NO_SPACE", "Setup_NoSpace"));
        else if (free is { } b2 && b2 < RecommendedFreeBytes)
            findings.Add(new PreflightFinding(PreflightSeverity.Warn, "SETUP_LOW_SPACE", "Setup_LowSpace"));

        var existing = LibraryProbe.LauncherIn(root);
        var launcherDir = Path.Combine(root, LibraryNames.LauncherDir);
        if (existing is null && MovesLauncher && IsForeignNonEmpty(launcherDir))
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "SETUP_LAUNCHER_TAKEN", "Setup_LauncherTaken"));

        return new LibraryCheck(root, findings, free, existing);
    }

    /// <summary>The sentence for a finding, including the setup's own.</summary>
    public static string DisplayText(PreflightFinding finding) => finding.Code switch
    {
        "SETUP_PATH_INVALID" => Loc.T("Setup_PathInvalid"),
        "SETUP_NOEXEC" => Loc.T("Setup_PathNoExec"),
        "SETUP_NO_SPACE" => Loc.T("Setup_NoSpace"),
        "SETUP_LOW_SPACE" => Loc.T("Setup_LowSpace"),
        "SETUP_LAUNCHER_TAKEN" => Loc.T("Setup_LauncherTaken"),
        _ => InstallEnvironmentPreflight.DisplayText(finding),
    };

    /// <summary>The durable path of this launcher: the AppImage on Linux, the exe elsewhere.</summary>
    private string? SelfPath => _host.Os == InstallHostOs.Linux ? _host.AppImagePath : _host.ProcessPath;

    /// <summary>Whether this launcher can copy itself into the library. Windows needs the payload list
    /// of its release (without it a copy could be partial), Linux the AppImage. macOS keeps its .app.</summary>
    public bool MovesLauncher => _host.Os switch
    {
        InstallHostOs.Windows => File.Exists(Path.Combine(_host.BaseDirectory, LibraryNames.PayloadList))
                                 && !string.IsNullOrEmpty(_host.ProcessPath),
        InstallHostOs.Linux => !string.IsNullOrEmpty(_host.AppImagePath) && File.Exists(_host.AppImagePath),
        _ => false,
    };

    // ── run ─────────────────────────────────────────────────────────────────

    public async Task<LibrarySetupResult> RunAsync(string root, IProgress<LibrarySetupStage>? progress,
                                                   CancellationToken ct = default)
    {
        progress?.Report(LibrarySetupStage.Checking);
        var check = Check(root);
        root = check.Root;
        if (check.IsBlocked)
            return new(LibrarySetupOutcome.Failed, DisplayText(check.Findings.First(f => f.Severity == PreflightSeverity.Block)));

        FileStream? setupLock = null;
        string? launcherDir = null;
        string? staging = null;
        var nonce = Guid.NewGuid().ToString("N");
        try
        {
            var setupDir = Path.Combine(root, LibraryNames.SetupDir);
            Directory.CreateDirectory(setupDir);
            try
            {
                setupLock = new FileStream(Path.Combine(setupDir, "lock"), FileMode.OpenOrCreate,
                                           FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return new(LibrarySetupOutcome.Failed, Loc.T("Setup_Busy"));
            }
            RemoveStagingLeftovers(root);
            AbandonStaleHandoffs(setupDir);

            // A finished library: open it instead of building a second one. If that launcher is this
            // very process (its config was lost), starting it would start this page again, forever:
            // write the config here instead and carry on.
            if (LibraryProbe.LauncherIn(root) is { } existing
                && SelfPath is { } self && LibraryClassifier.SamePath(existing, self, _host.Os != InstallHostOs.Linux))
            {
                _log.Information("Library setup: {Root} is already set up and this is its launcher; config rewritten", root);
                _commitInPlace(root, Path.GetDirectoryName(existing));
                return new(LibrarySetupOutcome.SetUpInPlace);
            }
            if (LibraryProbe.LauncherIn(root) is { } other)
            {
                _log.Information("Library setup: {Root} is already set up, starting {Launcher}", root, other);
                return _host.Start(other, [], Path.GetDirectoryName(other)!) is null
                    ? new(LibrarySetupOutcome.Failed, Loc.T("Setup_StartFailed"))
                    : new(LibrarySetupOutcome.AlreadySetUp);
            }

            if (!MovesLauncher)
            {
                _log.Information("Library setup: {Root} without moving the launcher ({Os}, payload list or AppImage absent)",
                                 root, _host.Os);
                _commitInPlace(root, null);
                return new(LibrarySetupOutcome.SetUpInPlace);
            }

            launcherDir = Path.Combine(root, LibraryNames.LauncherDir);
            RemoveOwnedLeftover(launcherDir);
            if (IsForeignNonEmpty(launcherDir))
                return new(LibrarySetupOutcome.Failed, Loc.T("Setup_LauncherTaken"));

            progress?.Report(LibrarySetupStage.Copying);
            staging = Path.Combine(root, $"{LibraryNames.LauncherDir}.staging-{nonce}");
            var launcherFile = await CopyLauncherAsync(staging, progress, ct).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(staging, LibraryNames.OwnedMarker), nonce);
            if (Directory.Exists(launcherDir)) Directory.Delete(launcherDir, recursive: false);   // empty by the check above
            Directory.Move(staging, launcherDir);
            staging = null;
            var target = Path.Combine(launcherDir, launcherFile);

            var handoff = new LibraryHandoff(root, nonce, launcherDir, _host.ProcessPath ?? "");
            WriteAtomic(Path.Combine(setupDir, $"pending-{nonce}.json"),
                        JsonSerializer.Serialize(handoff, LibraryJson.Default.LibraryHandoff));

            progress?.Report(LibrarySetupStage.Starting);
            _log.Information("Library setup: starting {Target} to finish the setup of {Root}", target, root);
            var hasExited = _host.Start(target, [LibraryProbe.HandoffArg, nonce, LibraryProbe.RootArg, root], launcherDir);
            if (hasExited is null)
            {
                Rollback(setupDir, nonce, launcherDir);
                return new(LibrarySetupOutcome.Failed, Loc.T("Setup_StartFailed"));
            }

            progress?.Report(LibrarySetupStage.WaitingForHandoff);
            var outcome = await WaitForHandoffAsync(setupDir, nonce, hasExited, ct).ConfigureAwait(false);
            if (outcome is null)
            {
                _log.Information("Library setup: {Root} handed over to {Target}", root, target);
                Cleanup(setupDir, nonce);
                return new(LibrarySetupOutcome.HandedOver);
            }
            if (IsCommitted(root, launcherDir))
            {
                // The copy wrote the config and the marker and died before it said "done". The folder
                // is complete; rolling back now would delete the launcher the marker points to.
                _log.Warning("Library setup {Nonce}: the copy finished {Root} but quit before saying so; starting it again",
                             nonce, root);
                KeepCommitted(setupDir, nonce, launcherDir);
                return _host.Start(target, [], launcherDir) is null
                    ? new(LibrarySetupOutcome.Failed, Loc.T("Setup_StartFailed"))
                    : new(LibrarySetupOutcome.AlreadySetUp);
            }
            Rollback(setupDir, nonce, launcherDir);
            return new(LibrarySetupOutcome.Failed, outcome);
        }
        catch (OperationCanceledException)
        {
            if (launcherDir is not null) Rollback(Path.Combine(root, LibraryNames.SetupDir), nonce, launcherDir);
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Library setup of {Root} failed", root);
            if (launcherDir is not null) Rollback(Path.Combine(root, LibraryNames.SetupDir), nonce, launcherDir);
            return new(LibrarySetupOutcome.Failed, Loc.T("Setup_Failed"));
        }
        finally
        {
            if (staging is not null) TryDeleteDir(staging);
            setupLock?.Dispose();
        }
    }

    /// <summary>Null when the copy said "done"; otherwise the sentence for the player. A copy that
    /// claimed the handoff is waited for a little longer (it is running), one that never claimed it is
    /// abandoned: the rename makes sure it cannot claim it afterwards.</summary>
    private async Task<string?> WaitForHandoffAsync(string setupDir, string nonce, Func<bool> hasExited,
                                                   CancellationToken ct)
    {
        var pending = Path.Combine(setupDir, $"pending-{nonce}.json");
        var done = Path.Combine(setupDir, $"done-{nonce}");
        var failed = Path.Combine(setupDir, $"failed-{nonce}");
        var deadline = DateTime.UtcNow + _host.HandoffTimeout;
        var extended = false;
        while (true)
        {
            if (File.Exists(done)) return null;
            if (File.Exists(failed)) return Loc.T("Setup_Failed");
            if (hasExited())
            {
                // It may have written "done" and quit right after; look once more.
                await Task.Delay(300, ct).ConfigureAwait(false);
                return File.Exists(done) ? null : Loc.T("Setup_StartFailed");
            }
            if (DateTime.UtcNow > deadline)
            {
                if (TryMove(pending, Path.Combine(setupDir, $"abandoned-{nonce}.json")))
                    return Loc.T("Setup_StartFailed");
                if (extended) return Loc.T("Setup_StartFailed");
                extended = true;                       // claimed: it runs, give it one more minute
                deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    // ── copy ────────────────────────────────────────────────────────────────

    /// <summary>Copy the complete launcher into <paramref name="staging"/> and prove every file; returns
    /// the launcher's file name inside it. Throws on any difference: a partial launcher is worse than
    /// none (Codex Terra 2026-09-28, blocker 3).</summary>
    private async Task<string> CopyLauncherAsync(string staging, IProgress<LibrarySetupStage>? progress,
                                                 CancellationToken ct)
    {
        Directory.CreateDirectory(staging);
        if (_host.Os == InstallHostOs.Linux)
        {
            var source = _host.AppImagePath!;
            var dest = Path.Combine(staging, LibraryNames.AppImageName);
            await CopyFileAsync(source, dest, ct).ConfigureAwait(false);
            progress?.Report(LibrarySetupStage.Verifying);
            if (!string.Equals(await HashAsync(source, ct).ConfigureAwait(false),
                               await HashAsync(dest, ct).ConfigureAwait(false), StringComparison.Ordinal))
                throw new IOException("The copied AppImage differs from the original.");
            if (!UnixExecBit.Ensure(dest))
                throw new IOException("The copied AppImage could not be made executable.");
            return LibraryNames.AppImageName;
        }

        // Windows: exactly the files the release lists, each checked against the hash the release
        // recorded, so a damaged original is caught as well as a damaged copy.
        var entries = ReadPayloadList(Path.Combine(_host.BaseDirectory, LibraryNames.PayloadList));
        foreach (var (hash, rel) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var src = Path.Combine(_host.BaseDirectory, rel);
            var dest = Path.Combine(staging, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await CopyFileAsync(src, dest, ct).ConfigureAwait(false);
        }
        progress?.Report(LibrarySetupStage.Verifying);
        foreach (var (hash, rel) in entries)
        {
            var actual = await HashAsync(Path.Combine(staging, rel), ct).ConfigureAwait(false);
            if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The copied file {rel} does not match the release.");
        }
        File.Copy(Path.Combine(_host.BaseDirectory, LibraryNames.PayloadList),
                  Path.Combine(staging, LibraryNames.PayloadList));
        var exe = Path.GetFileName(_host.ProcessPath!);
        if (!entries.Any(e => string.Equals(e.Path, exe, StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"The release list does not name {exe}.");
        return exe;
    }

    /// <summary><c>sha256sum</c> format: "hash  relative/path" per line. Paths are relative, inside the
    /// launcher folder, with no way out of it.</summary>
    internal static IReadOnlyList<(string Hash, string Path)> ReadPayloadList(string file)
    {
        var result = new List<(string, string)>();
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var space = line.IndexOf(' ');
            if (space != 64) throw new InvalidDataException($"Bad payload line: {line}");
            var rel = line[(space + 1)..].TrimStart(' ', '*').Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(rel) || rel.Split(Path.DirectorySeparatorChar).Contains(".."))
                throw new InvalidDataException($"Payload path leaves the launcher folder: {rel}");
            result.Add((line[..64], rel));
        }
        if (result.Count == 0) throw new InvalidDataException("Empty payload list.");
        return result;
    }

    private static async Task CopyFileAsync(string src, string dest, CancellationToken ct)
    {
        await using var from = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var to = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await from.CopyToAsync(to, ct).ConfigureAwait(false);
        await to.FlushAsync(ct).ConfigureAwait(false);
        to.Flush(flushToDisk: true);
    }

    private static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        await using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false));
    }

    // ── handoff: the copy's side ────────────────────────────────────────────

    /// <summary>
    /// Called by the copy, after its first frame is on screen. Claims the handoff (an atomic rename, so
    /// an original that gave up in the same moment wins or loses cleanly), then lets
    /// <paramref name="commit"/> write the config, and only then says "done". False means this process
    /// has no setup to finish (abandoned, or not ours) and should quit.
    /// </summary>
    public static bool CompleteHandoff(string root, string nonce, Action<string, string> commit, Serilog.ILogger log)
    {
        var setupDir = Path.Combine(root, LibraryNames.SetupDir);
        var pending = Path.Combine(setupDir, $"pending-{nonce}.json");
        var claimed = Path.Combine(setupDir, $"claimed-{nonce}.json");
        if (!TryMove(pending, claimed))
        {
            log.Warning("Library handoff {Nonce} for {Root}: nothing to claim (given up or not ours)", nonce, root);
            return false;
        }
        try
        {
            var handoff = JsonSerializer.Deserialize(File.ReadAllText(claimed), LibraryJson.Default.LibraryHandoff)
                          ?? throw new InvalidDataException("empty handoff");
            commit(handoff.Root, handoff.LauncherDir);
            TryDelete(Path.Combine(handoff.LauncherDir, LibraryNames.OwnedMarker));
            WriteAtomic(Path.Combine(setupDir, $"done-{nonce}"), DateTimeOffset.UtcNow.ToString("O"));
            log.Information("Library handoff {Nonce}: {Root} set up, launcher in {Dir}", nonce, handoff.Root, handoff.LauncherDir);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Library handoff {Nonce} for {Root} could not be completed", nonce, root);
            try { WriteAtomic(Path.Combine(setupDir, $"failed-{nonce}"), ex.GetType().Name); }
            catch (Exception) { /* the original times out instead */ }
            return false;
        }
    }

    /// <summary>
    /// What a finished setup leaves: the config names the library (and on Linux marks the desktop
    /// integration as done, so the old first-run step does not copy the AppImage to ~/Applications as
    /// well), the marker says where the launcher is, and Windows gets the pointer the old download uses
    /// to find it. The config is written first and the marker last: a marker means complete.
    /// </summary>
    public static void Commit(IConfigService config, string root, string? launcherDir)
    {
        Directory.CreateDirectory(root);
        var cfg = config.Load();
        cfg.LibraryRoot = root;
        if (launcherDir is not null && OperatingSystem.IsLinux()) cfg.DesktopIntegrationDone = true;
        config.Save(cfg);
        if (!config.LastSaveSucceeded) throw new IOException("The launcher config could not be saved.");
        new LibraryMarker(1, root, launcherDir, DateTimeOffset.UtcNow).Write();
        if (launcherDir is not null && OperatingSystem.IsWindows())
        {
            try { WindowsLibraryPointer.Write(root); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Could not write the library pointer for {Root}", root); }
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private void Rollback(string setupDir, string nonce, string launcherDir)
    {
        if (IsCommitted(Path.GetDirectoryName(setupDir)!, launcherDir))
        {
            KeepCommitted(setupDir, nonce, launcherDir);
            return;
        }
        foreach (var name in new[] { $"pending-{nonce}.json", $"claimed-{nonce}.json" })
            TryMove(Path.Combine(setupDir, name), Path.Combine(setupDir, $"abandoned-{nonce}-{name}"));
        RemoveOwnedLeftover(launcherDir);
        // This launcher carries on as the player's launcher; the copy that was to take over is gone.
        SingleInstance.Current?.Reacquire();
        _log.Information("Library setup {Nonce}: rolled back, {Dir} removed where it was ours", nonce, launcherDir);
    }

    /// <summary>The marker names this launcher folder: the copy committed, the folder is the player's
    /// library now. The marker is written last (<see cref="Commit"/>), so it alone decides.</summary>
    private static bool IsCommitted(string root, string launcherDir) =>
        LibraryMarker.TryRead(root) is { LauncherDir: { } dir }
        && LibraryClassifier.SamePath(dir, launcherDir, !OperatingSystem.IsLinux());

    /// <summary>Finish what the copy did not: it is ours no more (no owned marker, so no later setup
    /// removes it), and the handoff files go.</summary>
    private void KeepCommitted(string setupDir, string nonce, string launcherDir)
    {
        TryDelete(Path.Combine(launcherDir, LibraryNames.OwnedMarker));
        foreach (var name in new[] { $"pending-{nonce}.json", $"claimed-{nonce}.json", $"done-{nonce}" })
            TryDelete(Path.Combine(setupDir, name));
        _log.Information("Library setup {Nonce}: {Dir} is committed, kept", nonce, launcherDir);
    }

    /// <summary>A setup cut off while copying leaves its staging folder (the whole launcher, about 54 MB
    /// on Linux); every attempt has its own nonce, so nothing else would ever remove it. Only our exact
    /// name counts, and only under the setup lock, so no running setup loses its copy.</summary>
    internal void RemoveStagingLeftovers(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, LibraryNames.LauncherDir + ".staging-*"))
            {
                var suffix = Path.GetFileName(dir)[(LibraryNames.LauncherDir.Length + ".staging-".Length)..];
                if (suffix.Length != 32 || !suffix.All(Uri.IsHexDigit)) continue;
                TryDeleteDir(dir);
                _log.Information("Library setup: removed {Dir}, left by an interrupted setup", dir);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Library setup: could not look for leftovers in {Root}", root);
        }
    }

    /// <summary>A handoff still "pending" while we hold the setup lock belongs to a setup whose original
    /// died (no one else can be running). Give it up the same way a timeout does, so a copy of that
    /// attempt that starts late (SmartScreen, a slow disk) finds nothing to claim.</summary>
    private void AbandonStaleHandoffs(string setupDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(setupDir, "pending-*.json").ToList())
            {
                var name = Path.GetFileName(file);
                var nonce = name["pending-".Length..^".json".Length];
                if (TryMove(file, Path.Combine(setupDir, $"abandoned-{nonce}-{name}")))
                    _log.Information("Library setup: gave up {Name}, left by a setup that did not finish", name);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Library setup: could not look for unfinished handoffs in {Dir}", setupDir);
        }
    }

    private static void Cleanup(string setupDir, string nonce)
    {
        foreach (var name in new[] { $"claimed-{nonce}.json", $"done-{nonce}" })
            TryDelete(Path.Combine(setupDir, name));
        // Earlier attempts that were given up: kept for the log until one succeeded, now nobody reads them.
        try
        {
            foreach (var file in Directory.EnumerateFiles(setupDir, "abandoned-*")) TryDelete(file);
        }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>A launcher folder an earlier, unfinished setup left behind carries our marker: remove
    /// it. Anything without the marker is not ours and is never touched.</summary>
    internal void RemoveOwnedLeftover(string launcherDir)
    {
        var marker = Path.Combine(launcherDir, LibraryNames.OwnedMarker);
        if (!File.Exists(marker)) return;
        // The marker goes last: if a file is still held (the copy is still exiting), what is left keeps
        // the marker and a retry can remove it, instead of looking like a folder that is not ours.
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(launcherDir).ToList())
            {
                if (string.Equals(entry, marker, StringComparison.Ordinal)) continue;
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            File.Delete(marker);
            Directory.Delete(launcherDir);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not remove the unfinished launcher folder {Dir}; a later setup retries", launcherDir);
        }
    }

    private static bool IsForeignNonEmpty(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()
                   && !File.Exists(Path.Combine(dir, LibraryNames.OwnedMarker));
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static string? NearestExisting(string path)
    {
        var p = path;
        while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
        return string.IsNullOrEmpty(p) ? null : p;
    }

    /// <summary>A create-and-delete probe in a folder that exists; unlike the download preflight this
    /// never creates the chosen folder just to look at it.</summary>
    private static bool ProbeWritable(string existingDir)
    {
        try
        {
            var probe = Path.Combine(existingDir, $".st-setup-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* best effort */ }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* a running copy may hold it; the owned marker lets a retry remove it */ }
    }
}
