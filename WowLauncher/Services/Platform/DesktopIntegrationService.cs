using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WowLauncher.Services.Platform;

/// <summary>
/// Opt-in Linux desktop integration: on request, install a <c>.desktop</c> entry and the launcher's
/// own lantern icon into the user's XDG data dirs so KDE/GNOME show the mark for the app in the
/// menu <em>and</em> for the running window in the taskbar.
///
/// <para><b>Why this is a feature and not something done at startup.</b> Writing into
/// <c>~/.local/share</c> is a system side effect the runbook forbids doing silently
/// (<c>deploy/package-linux.sh</c> ships the <c>.desktop</c> as documentation, never auto-installs it).
/// So nothing here runs unless the player presses the button.</para>
///
/// <para><b>The two problems this solves.</b> (1) The shipped AppImage embeds a <c>.desktop</c> and
/// icons, but the AppImage file on disk is not registered with the desktop, so Plasma shows a generic
/// icon for it. (2) The embedded entry historically carried no <c>StartupWMClass</c>, so even a
/// registered entry would not match the <em>running</em> window (its <c>WM_CLASS</c> is
/// <c>WowLauncher</c>, set in <c>Program.BuildAvaloniaApp</c>). The entry written here carries
/// <c>StartupWMClass=WowLauncher</c> and an <c>Exec</c> that points at the real launch target
/// (the <c>$APPIMAGE</c> file under AppImage, else the executable), which fixes both.</para>
///
/// <para>Everything is best-effort and never throws to the caller: the result is a value, not an
/// exception. Non-Linux hosts get <see cref="UnsupportedDesktopIntegrationService"/>
/// (<see cref="IsSupported"/> = false) so the UI simply hides the button.</para>
/// </summary>
public interface IDesktopIntegrationService
{
    /// <summary>True only where menu integration is meaningful (Linux). The UI hides the button otherwise.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// True when the <c>.desktop</c> already exists <em>and</em> its <c>Exec</c> points at the current
    /// launch target. A stale entry (moved AppImage, reinstalled elsewhere) reads as not installed, so
    /// a re-run repoints it. Never throws.
    /// </summary>
    bool IsInstalled();

    /// <summary>Install icons + <c>.desktop</c> into the user's XDG dirs, then best-effort refresh the
    /// desktop/icon caches. Idempotent. Never throws — failures come back as a
    /// <see cref="DesktopIntegrationResult"/>.</summary>
    Task<DesktopIntegrationResult> InstallAsync(CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IDesktopIntegrationService.InstallAsync"/> did.</summary>
public enum DesktopIntegrationOutcome
{
    /// <summary>Icons and/or the <c>.desktop</c> were written this call.</summary>
    Installed,

    /// <summary>Already present and pointing at the current launch target; nothing to do.</summary>
    AlreadyInstalled,

    /// <summary>This host does not support menu integration (non-Linux).</summary>
    Unsupported,

    /// <summary>Writing failed (permissions, read-only home, …). The launcher keeps running.</summary>
    Failed,
}

/// <summary>Result value of an install attempt. <see cref="Ok"/> is true for both success outcomes.</summary>
public sealed record DesktopIntegrationResult(DesktopIntegrationOutcome Outcome, string Message)
{
    /// <summary>True when the menu entry is now in place (freshly written or already there).</summary>
    public bool Ok => Outcome is DesktopIntegrationOutcome.Installed or DesktopIntegrationOutcome.AlreadyInstalled;
}

/// <summary>One source icon to install, tagged with the hicolor pixel size it belongs in.</summary>
public sealed record DesktopIconSource(int Size, string SourcePath);

/// <summary>
/// The environment inputs the Linux service writes against, captured so a test can supply a temporary
/// HOME, a chosen <c>Exec</c> line and fake icon sources without touching process globals.
/// </summary>
/// <param name="DataHome">The XDG data-home <b>root</b> (e.g. <c>~/.local/share</c>), NOT the app
/// sub-directory — icons go under <c>{DataHome}/icons/hicolor</c> and the entry under
/// <c>{DataHome}/applications</c>.</param>
/// <param name="ExecLine">The resolved <c>Exec=</c> value (already quoted if it needs it).</param>
/// <param name="IconSources">Icons to copy, one per size that was found on disk (may be empty).</param>
public sealed record DesktopIntegrationLayout(
    string DataHome,
    string ExecLine,
    IReadOnlyList<DesktopIconSource> IconSources);

/// <summary>Factory + shared identity constants for desktop integration.</summary>
public static class DesktopIntegration
{
    /// <summary>The application id — file stem of both the icon and the <c>.desktop</c> entry. Must
    /// equal the id in <c>deploy/package-linux.sh</c> / <c>deploy/build-appimage.sh</c>.</summary>
    public const string AppId = "stonetavern-launcher";

    /// <summary>The running window's <c>WM_CLASS</c>, set in <c>Program.BuildAvaloniaApp</c>. The
    /// <c>.desktop</c> entry's <c>StartupWMClass</c> must equal this or the taskbar shows a placeholder
    /// for the live window even when the menu icon is correct.</summary>
    public const string WmClass = "WowLauncher";

    /// <summary>Human-readable menu name.</summary>
    public const string DisplayName = "Stonetavern Launcher";

    /// <summary>The hicolor sizes the launcher ships and installs, largest first.</summary>
    internal static readonly int[] IconSizes = [512, 256, 128];

    /// <summary>The correct service for this OS: Linux writes a <c>.desktop</c> menu entry, Windows
    /// writes a Start menu <c>.lnk</c> (<see cref="WindowsDesktopIntegrationService"/>), any other host
    /// gets an unsupported stub (button hidden).</summary>
    public static IDesktopIntegrationService ForCurrentOs(Serilog.ILogger? log = null) =>
        OperatingSystem.IsLinux()
            ? new LinuxDesktopIntegrationService(ResolveLayout(), log ?? Serilog.Log.Logger)
            : OperatingSystem.IsWindows()
                ? WindowsDesktopIntegrationService.ForCurrentUser(log ?? Serilog.Log.Logger)
                : new UnsupportedDesktopIntegrationService();

    /// <summary>Read the real environment into a <see cref="DesktopIntegrationLayout"/>.</summary>
    internal static DesktopIntegrationLayout ResolveLayout()
    {
        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var processPath = Environment.ProcessPath;
        return new DesktopIntegrationLayout(
            DataHomeRoot(),
            LinuxDesktopIntegrationService.ResolveExecLine(appImage, processPath),
            LinuxDesktopIntegrationService.ResolveIconSources(appDir, processPath, File.Exists));
    }

    /// <summary>XDG data-home root: <c>$XDG_DATA_HOME</c> when absolute, else <c>~/.local/share</c>
    /// (same rule as <see cref="XdgAppPaths"/>, but the root — icons/applications hang directly off it).</summary>
    internal static string DataHomeRoot()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)) return xdg;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        return Path.Combine(home, ".local", "share");
    }
}

/// <summary>Non-Linux stub: nothing to integrate, so the UI never offers the button.</summary>
public sealed class UnsupportedDesktopIntegrationService : IDesktopIntegrationService
{
    public bool IsSupported => false;
    public bool IsInstalled() => false;

    public Task<DesktopIntegrationResult> InstallAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DesktopIntegrationResult(
            DesktopIntegrationOutcome.Unsupported, "Menu integration is only available on Linux."));
}

/// <summary>
/// Linux implementation. Writes <c>{DataHome}/applications/stonetavern-launcher.desktop</c> and the
/// icon into each <c>{DataHome}/icons/hicolor/{size}x{size}/apps/</c> it has a source for, then
/// best-effort refreshes the caches. Pure resolution (<see cref="ResolveExecLine"/>,
/// <see cref="ResolveIconSources"/>, <see cref="BuildDesktopEntry"/>) is static and testable; the
/// instance does only the file writes against an injected <see cref="DesktopIntegrationLayout"/>.
/// </summary>
public sealed class LinuxDesktopIntegrationService : IDesktopIntegrationService
{
    private readonly DesktopIntegrationLayout _layout;
    private readonly Serilog.ILogger _log;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task>? _runCommand;

    /// <param name="layout">Where to write and what <c>Exec</c>/icons to use.</param>
    /// <param name="log">Serilog sink; failures are logged at Debug/Warning, never surfaced as throws.</param>
    /// <param name="runCommand">Test seam for the cache-refresh spawns. Null = spawn real processes
    /// (best-effort). A test passes a recorder so nothing touches the real desktop.</param>
    public LinuxDesktopIntegrationService(
        DesktopIntegrationLayout layout,
        Serilog.ILogger log,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? runCommand = null)
    {
        _layout = layout;
        _log = log;
        _runCommand = runCommand;
    }

    public bool IsSupported => true;

    private string ApplicationsDir => Path.Combine(_layout.DataHome, "applications");
    private string IconThemeDir => Path.Combine(_layout.DataHome, "icons", "hicolor");
    private string DesktopFilePath => Path.Combine(ApplicationsDir, $"{DesktopIntegration.AppId}.desktop");

    public bool IsInstalled()
    {
        try
        {
            if (!File.Exists(DesktopFilePath)) return false;
            var exec = ReadExecLine(File.ReadAllLines(DesktopFilePath));
            return string.Equals(exec, _layout.ExecLine, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Desktop integration: could not read existing entry at {Path}", DesktopFilePath);
            return false;
        }
    }

    public async Task<DesktopIntegrationResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (IsInstalled())
                return new DesktopIntegrationResult(
                    DesktopIntegrationOutcome.AlreadyInstalled, "The menu entry is already in place.");

            Directory.CreateDirectory(ApplicationsDir);

            var installedSizes = 0;
            foreach (var icon in _layout.IconSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetDir = Path.Combine(IconThemeDir, $"{icon.Size}x{icon.Size}", "apps");
                Directory.CreateDirectory(targetDir);
                var target = Path.Combine(targetDir, $"{DesktopIntegration.AppId}.png");
                File.Copy(icon.SourcePath, target, overwrite: true);
                installedSizes++;
            }

            // Atomic write: a truncated .desktop mid-write would be a broken menu entry. Write a temp
            // file next to the target, then move it into place.
            var contents = BuildDesktopEntry(_layout.ExecLine);
            var tmp = DesktopFilePath + ".tmp";
            await File.WriteAllTextAsync(tmp, contents, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(tmp, DesktopFilePath, overwrite: true);

            await RefreshCachesAsync(cancellationToken).ConfigureAwait(false);

            _log.Information(
                "Desktop integration installed: {Desktop} (Exec={Exec}), {Count} icon size(s)",
                DesktopFilePath, _layout.ExecLine, installedSizes);
            return new DesktopIntegrationResult(
                DesktopIntegrationOutcome.Installed, "Added to your applications menu.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Desktop integration failed writing to {Dir}", _layout.DataHome);
            return new DesktopIntegrationResult(
                DesktopIntegrationOutcome.Failed, "Could not add the menu entry. Your home folder may be read-only.");
        }
    }

    // ── pure, testable resolution ──────────────────────────────────────────

    /// <summary>
    /// Resolve the durable <c>Exec=</c> target. Under AppImage the running executable is the extracted
    /// apphost inside the read-only mount, which vanishes on exit; the launch target that survives is
    /// the <c>.AppImage</c> file itself, which AppImage exposes as <c>$APPIMAGE</c>. Outside AppImage
    /// the executable path (<c>Environment.ProcessPath</c>) is the target. Quoted when it contains a
    /// space, per the Desktop Entry spec.
    /// </summary>
    internal static string ResolveExecLine(string? appImage, string? processPath)
    {
        if (!string.IsNullOrEmpty(appImage)) return QuoteExec(appImage);
        if (!string.IsNullOrEmpty(processPath)) return QuoteExec(processPath);
        // Last resort: rely on PATH. Should not happen for a running process.
        return DesktopIntegration.AppId;
    }

    /// <summary>
    /// Find the launcher's own icon for each shipped size. Under AppImage the icons live in the mounted
    /// <c>$APPDIR/usr/share/icons/hicolor</c> tree (all three sizes); outside AppImage they may sit in
    /// the same tree next to the executable, or — in the release tarball — as a single 256px icon under
    /// <c>integration/</c>. Only sizes actually found are returned, so a missing size is skipped rather
    /// than upscaled into the wrong slot.
    /// </summary>
    internal static IReadOnlyList<DesktopIconSource> ResolveIconSources(
        string? appDir, string? processPath, Func<string, bool> fileExists)
    {
        var exeDir = string.IsNullOrEmpty(processPath) ? null : Path.GetDirectoryName(processPath);
        var found = new List<DesktopIconSource>();

        foreach (var size in DesktopIntegration.IconSizes)
        {
            string? pick = null;
            foreach (var candidate in IconCandidates(size, appDir, exeDir))
            {
                if (fileExists(candidate)) { pick = candidate; break; }
            }
            if (pick is not null) found.Add(new DesktopIconSource(size, pick));
        }
        return found;
    }

    private static IEnumerable<string> IconCandidates(int size, string? appDir, string? exeDir)
    {
        var rel = Path.Combine("usr", "share", "icons", "hicolor", $"{size}x{size}", "apps",
            $"{DesktopIntegration.AppId}.png");

        // 1) AppImage mount.
        if (!string.IsNullOrEmpty(appDir))
            yield return Path.Combine(appDir, rel);

        if (!string.IsNullOrEmpty(exeDir))
        {
            // 2) Same hicolor tree laid out next to the executable.
            yield return Path.Combine(exeDir, rel);
            // 3) Tarball fallback: a single 256px icon in integration/ (next to lib/ or next to the exe).
            if (size == 256)
            {
                var file = $"{DesktopIntegration.AppId}.png";
                yield return Path.Combine(exeDir, "..", "integration", file);
                yield return Path.Combine(exeDir, "integration", file);
            }
        }
    }

    /// <summary>Build the <c>.desktop</c> body. Carries <c>StartupWMClass</c> so the running window is
    /// matched, and the resolved <c>Exec</c> so launching from the menu starts the same binary.</summary>
    internal static string BuildDesktopEntry(string execLine)
    {
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append($"Name={DesktopIntegration.DisplayName}\n");
        sb.Append("Comment=Launcher for the Stonetavern realm (Vanilla WoW 1.12.1)\n");
        sb.Append($"Exec={execLine}\n");
        sb.Append($"Icon={DesktopIntegration.AppId}\n");
        sb.Append("Terminal=false\n");
        sb.Append("Categories=Game;\n");
        sb.Append("StartupNotify=true\n");
        sb.Append($"StartupWMClass={DesktopIntegration.WmClass}\n");
        return sb.ToString();
    }

    /// <summary>Read the <c>Exec=</c> value from a parsed <c>.desktop</c>, or null if absent.</summary>
    internal static string? ReadExecLine(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("Exec=", StringComparison.Ordinal))
                return trimmed["Exec=".Length..];
        }
        return null;
    }

    /// <summary>Wrap in double quotes when the path contains whitespace (Desktop Entry spec: reserved
    /// characters in an argument must be quoted). Backslash and double-quote are escaped inside.</summary>
    private static string QuoteExec(string path)
    {
        if (!path.Any(char.IsWhiteSpace)) return path;
        var escaped = path.Replace("\\", "\\\\", StringComparison.Ordinal)
                          .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    // ── best-effort cache refresh ──────────────────────────────────────────

    private async Task RefreshCachesAsync(CancellationToken cancellationToken)
    {
        // All optional: the entry is valid on disk without them, they just make some desktops notice
        // it sooner. Every spawn is guarded by a PATH lookup and every failure is swallowed.
        await TryRun("update-desktop-database", [ApplicationsDir], cancellationToken).ConfigureAwait(false);
        await TryRun("gtk-update-icon-cache", ["-q", "-t", "-f", IconThemeDir], cancellationToken).ConfigureAwait(false);
        await TryRun("xdg-desktop-menu", ["forceupdate"], cancellationToken).ConfigureAwait(false);
    }

    private async Task TryRun(string tool, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (_runCommand is not null)
        {
            try { await _runCommand(tool, args, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _log.Debug(ex, "Desktop integration: injected runner threw for {Tool}", tool); }
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
            _log.Debug(ex, "Desktop integration: cache-refresh tool {Tool} failed (ignored)", tool);
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
