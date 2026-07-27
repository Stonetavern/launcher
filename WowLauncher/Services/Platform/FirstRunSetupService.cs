using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Services;

namespace WowLauncher.Services.Platform;

/// <summary>
/// One-time, first-run desktop setup for the Linux AppImage. Owner UX (2026-07-24): the player double-
/// clicks the downloaded <c>.AppImage</c> once and everything happens on its own — the launcher moves
/// itself into <c>~/Applications</c>, a lantern shortcut appears on the desktop, and a search/menu
/// entry is created. Never asked again.
///
/// <para><b>Why this is separate from <see cref="IDesktopIntegrationService"/>.</b> That service is the
/// opt-in "Add to menu" button: menu entry + hicolor icons only, on explicit request. This one is the
/// automatic first-run flow the owner asked for: it additionally relocates the AppImage into
/// <c>~/Applications</c> (so the durable launch target is a stable path, not the player's Downloads
/// folder) and writes a <em>trusted, executable</em> shortcut onto the desktop — the two steps a player
/// cannot be expected to do by hand (<c>chmod +x</c> plus the KDE/GNOME trust dance). It reuses
/// <see cref="LinuxDesktopIntegrationService"/> for the menu+icons half so there is one writer for the
/// <c>.desktop</c> body and the hicolor tree.</para>
///
/// <para><b>Guards.</b> It only ever acts when the process is actually running from an AppImage
/// (<c>$APPIMAGE</c> is set) and the one-time flag in the config is still clear. Off an AppImage
/// (tarball, dev run, tests) it is a no-op. Every file operation is best-effort and never throws to the
/// caller — a read-only home or a missing tool leaves the launcher running normally.</para>
/// </summary>
public interface IFirstRunSetup
{
    /// <summary>Run the one-time setup if the guards allow it. Never throws; safe to fire-and-forget.</summary>
    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Factory + pure guard for the first-run setup.</summary>
public static class FirstRunSetup
{
    /// <summary>
    /// The one-time setup should run only when we are launched from an AppImage (a durable
    /// <c>$APPIMAGE</c> path exists) and it has not been done before. Pure so it is unit-testable
    /// without touching the environment.
    /// </summary>
    public static bool ShouldRun(string? appImagePath, bool alreadyDone) =>
        !alreadyDone && !string.IsNullOrEmpty(appImagePath);

    /// <summary>The correct service for this OS. Only Linux does anything; every other host gets an
    /// inert stub so the caller can invoke it unconditionally.</summary>
    public static IFirstRunSetup ForCurrentOs(IConfigService config, Serilog.ILogger? log = null)
    {
        if (!OperatingSystem.IsLinux()) return new UnsupportedFirstRunSetup();
        return new LinuxFirstRunSetup(ResolveLayout(), config, log ?? Serilog.Log.Logger);
    }

    /// <summary>Read the real environment into a <see cref="FirstRunLayout"/>.</summary>
    internal static FirstRunLayout ResolveLayout()
    {
        var home = HomeDir();
        return new FirstRunLayout(
            AppImagePath: Environment.GetEnvironmentVariable("APPIMAGE"),
            ApplicationsDir: Path.Combine(home, "Applications"),
            DesktopDir: LinuxFirstRunSetup.ResolveDesktopDir(home),
            DataHome: DesktopIntegration.DataHomeRoot(),
            AppDir: Environment.GetEnvironmentVariable("APPDIR"),
            ProcessPath: Environment.ProcessPath);
    }

    internal static string HomeDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) return home;
        return Environment.GetEnvironmentVariable("HOME") ?? ".";
    }
}

/// <summary>
/// The environment inputs the Linux setup writes against, captured so a test can supply throwaway
/// directories and a fake AppImage without touching the real desktop or the real HOME.
/// </summary>
/// <param name="AppImagePath"><c>$APPIMAGE</c> — the durable path of the running .AppImage, or null
/// when not launched from one (then the setup is a no-op).</param>
/// <param name="ApplicationsDir">Where to relocate the AppImage to (<c>~/Applications</c>).</param>
/// <param name="DesktopDir">The user's desktop directory (XDG, may be localised).</param>
/// <param name="DataHome">XDG data-home root for the menu entry + hicolor icons (<c>~/.local/share</c>).</param>
/// <param name="AppDir"><c>$APPDIR</c> — the AppImage mount root, where the icon sources live.</param>
/// <param name="ProcessPath"><see cref="Environment.ProcessPath"/> — icon-source fallback.</param>
public sealed record FirstRunLayout(
    string? AppImagePath,
    string ApplicationsDir,
    string DesktopDir,
    string DataHome,
    string? AppDir,
    string? ProcessPath);

/// <summary>Non-Linux stub: nothing to set up.</summary>
public sealed class UnsupportedFirstRunSetup : IFirstRunSetup
{
    public Task RunAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Linux implementation. On the first start from an AppImage: copy the .AppImage into
/// <c>~/Applications</c> (executable), register the menu entry + icons through
/// <see cref="LinuxDesktopIntegrationService"/> pointed at that stable copy, and write a trusted,
/// executable desktop shortcut. Then flip the one-time flag in the config so it never runs again.
/// </summary>
public sealed class LinuxFirstRunSetup : IFirstRunSetup
{
    private const string AppId = DesktopIntegration.AppId;

    private readonly FirstRunLayout _layout;
    private readonly IConfigService _config;
    private readonly Serilog.ILogger _log;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task>? _runCommand;

    /// <param name="runCommand">Test seam for the trust/refresh spawns (gio). Null = spawn real
    /// processes best-effort; a test passes a recorder so nothing touches the real desktop.</param>
    public LinuxFirstRunSetup(
        FirstRunLayout layout,
        IConfigService config,
        Serilog.ILogger log,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? runCommand = null)
    {
        _layout = layout;
        _config = config;
        _log = log;
        _runCommand = runCommand;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cfg = _config.Load();
            if (!FirstRunSetup.ShouldRun(_layout.AppImagePath, cfg.DesktopIntegrationDone))
                return;

            var appImage = _layout.AppImagePath!;
            _log.Information("First-run AppImage setup starting (source {AppImage})", appImage);

            // 1) Relocate into ~/Applications so the durable launch target is a stable path, not the
            //    Downloads folder the player may empty. Copy (never move): the file is running right
            //    now, and deleting a player's download silently is a surprise we do not want.
            var launchTarget = TryRelocate(appImage);

            // 2) Menu entry + hicolor icons, pointed at the relocated copy (or the original if the copy
            //    did not happen). One writer for the .desktop body and the icon tree.
            var execLine = LinuxDesktopIntegrationService.ResolveExecLine(launchTarget, null);
            var menuOk = await TryInstallMenuAsync(execLine, cancellationToken).ConfigureAwait(false);

            // 3) A trusted, executable shortcut on the desktop — the chmod+trust a player cannot do.
            var desktopOk = await TryWriteDesktopShortcutAsync(execLine, cancellationToken).ConfigureAwait(false);

            // Flip the one-time flag only if at least one visible entry landed. If the whole thing
            // failed (read-only home), leave it clear so a later start retries — every step is
            // idempotent, so a retry cannot double-install.
            if (menuOk || desktopOk)
            {
                cfg.DesktopIntegrationDone = true;
                _config.Save(cfg);
                _log.Information(
                    "First-run setup done: menu={Menu}, desktop={Desktop}, target={Target}",
                    menuOk, desktopOk, launchTarget);
            }
            else
            {
                _log.Warning("First-run setup made no entry (home may be read-only); will retry next start");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning(ex, "First-run AppImage setup failed (ignored — launcher keeps running)");
        }
    }

    // ── steps ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Copy the running AppImage into <c>~/Applications</c> and make it executable; return the path the
    /// shortcuts should point at. Returns the original path unchanged when the copy is unnecessary
    /// (already there) or fails (so the shortcuts still point at something runnable).
    /// </summary>
    private string TryRelocate(string appImage)
    {
        try
        {
            var target = ResolveTargetPath(appImage, _layout.ApplicationsDir);
            if (PathsEqual(appImage, target))
                return appImage; // already launched from ~/Applications

            Directory.CreateDirectory(_layout.ApplicationsDir);

            // Do not clobber an existing same-named file (a previous install of the same version); just
            // point at it. Only copy when the target is absent.
            if (!File.Exists(target))
            {
                File.Copy(appImage, target, overwrite: false);
                MakeExecutable(target);
                _log.Information("Relocated AppImage into {Target}", target);
            }
            return target;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not relocate the AppImage into {Dir}; using the original path", _layout.ApplicationsDir);
            return appImage;
        }
    }

    private async Task<bool> TryInstallMenuAsync(string execLine, CancellationToken cancellationToken)
    {
        try
        {
            var icons = LinuxDesktopIntegrationService.ResolveIconSources(
                _layout.AppDir, _layout.ProcessPath, File.Exists);
            var layout = new DesktopIntegrationLayout(_layout.DataHome, execLine, icons);
            var svc = new LinuxDesktopIntegrationService(layout, _log, _runCommand);
            var result = await svc.InstallAsync(cancellationToken).ConfigureAwait(false);
            return result.Ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning(ex, "First-run menu registration failed");
            return false;
        }
    }

    /// <summary>
    /// Write <c>{DesktopDir}/stonetavern-launcher.desktop</c>, make it executable, and mark it trusted.
    /// Both are needed or KDE/GNOME show a plain text file ("Open with KWrite") instead of a launcher.
    /// The GNOME trust bit is <c>gio metadata::trusted</c>; KDE has no public CLI for its own trust
    /// store, so the executable bit is what makes Plasma render it as a launcher — best-effort, and the
    /// entry is still valid and double-clickable regardless.
    /// </summary>
    private async Task<bool> TryWriteDesktopShortcutAsync(string execLine, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(_layout.DesktopDir))
            {
                _log.Debug("No desktop directory at {Dir}; skipping desktop shortcut", _layout.DesktopDir);
                return false;
            }

            var path = Path.Combine(_layout.DesktopDir, $"{AppId}.desktop");
            var body = LinuxDesktopIntegrationService.BuildDesktopEntry(execLine);

            // Atomic write so a truncated file mid-write is never left as a broken shortcut.
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, body, new System.Text.UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);

            MakeExecutable(path);
            await TryRun("gio", ["set", path, "metadata::trusted", "true"], cancellationToken).ConfigureAwait(false);

            _log.Information("Wrote trusted desktop shortcut {Path}", path);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not write the desktop shortcut in {Dir}", _layout.DesktopDir);
            return false;
        }
    }

    // ── pure, testable helpers ────────────────────────────────────────────────

    /// <summary>The relocation target: the same file name under <c>~/Applications</c>.</summary>
    internal static string ResolveTargetPath(string appImagePath, string applicationsDir) =>
        Path.Combine(applicationsDir, Path.GetFileName(appImagePath));

    /// <summary>Linux paths are case-sensitive; compare on the full path.</summary>
    internal static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    /// <summary>
    /// Resolve the user's desktop directory: <c>xdg-user-dir DESKTOP</c> (honours a localised name like
    /// "Schreibtisch"), falling back to <c>~/Desktop</c>. Best-effort and quick; never throws.
    /// </summary>
    internal static string ResolveDesktopDir(string home)
    {
        var fallback = Path.Combine(home, "Desktop");
        try
        {
            var psi = new ProcessStartInfo("xdg-user-dir")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("DESKTOP");
            using var proc = Process.Start(psi);
            if (proc is null) return fallback;
            var outp = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(2000)) { try { proc.Kill(); } catch { /* ignore */ } return fallback; }
            return proc.ExitCode == 0 && outp.Length > 0 && Path.IsPathRooted(outp) ? outp : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    // ── best-effort process/file helpers ──────────────────────────────────────

    private void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsLinux()) return; // Unix mode bits only exist here; the class is Linux-only anyway.
        try
        {
            const UnixFileMode rwxr_xr_x =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, rwxr_xr_x);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not set +x on {Path}", path);
        }
    }

    private async Task TryRun(string tool, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (_runCommand is not null)
        {
            try { await _runCommand(tool, args, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _log.Debug(ex, "First-run: injected runner threw for {Tool}", tool); }
            return;
        }

        try
        {
            if (!IsOnPath(tool)) return;
            var psi = new ProcessStartInfo(tool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return;
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "First-run: tool {Tool} failed (ignored)", tool);
        }
    }

    private static bool IsOnPath(string tool)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir, tool))) return true; }
            catch { /* an unreadable PATH entry is not our problem */ }
        }
        return false;
    }
}
