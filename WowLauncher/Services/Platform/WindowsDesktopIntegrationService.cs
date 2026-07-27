using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace WowLauncher.Services.Platform;

/// <summary>
/// Everything the Windows shortcut writer needs, captured so a test can supply throwaway paths without
/// touching the real Start menu or reading the real environment.
/// </summary>
/// <param name="ShortcutPath">The <c>.lnk</c> to write (under the user's Start menu Programs folder).</param>
/// <param name="TargetPath">The launcher executable the shortcut points at.</param>
/// <param name="WorkingDirectory">The shortcut's working directory (the exe's own folder).</param>
/// <param name="IconPath">Where the icon comes from — the exe itself, which embeds the branded
/// <c>stonetavern.ico</c> (see WowLauncher.csproj ApplicationIcon), so no separate icon file ships.</param>
/// <param name="Description">The shortcut tooltip.</param>
public sealed record WindowsShortcutSpec(
    string ShortcutPath,
    string TargetPath,
    string WorkingDirectory,
    string IconPath,
    string Description);

/// <summary>
/// Windows pendant to <see cref="LinuxDesktopIntegrationService"/>: the opt-in "Add to menu" action puts
/// a Start menu shortcut (with the launcher's own lantern icon, embedded in the exe) into the user's
/// Programs folder, so Windows shows the mark for the app in the Start menu and search. It implements the
/// SAME <see cref="IDesktopIntegrationService"/> seam the Linux service does, so Settings binds the
/// button unconditionally and it simply becomes live on Windows too (before this, Windows fell through to
/// <see cref="UnsupportedDesktopIntegrationService"/> and the button was hidden).
///
/// <para><b>Why a shortcut and not a registry entry.</b> A per-user Start menu <c>.lnk</c> needs no
/// admin rights, no installer, and no uninstall bookkeeping — deleting the file removes it. It is written
/// through the OS-provided <c>IShellLink</c> COM interface, so there is no third-party dependency and
/// nothing native to self-extract (both hard constraints: 100% OSS for SignPath, and no Defender dropper
/// heuristic).</para>
///
/// <para><b>Testability.</b> The shortcut path/target/icon resolution is pure and unit-tested on any OS;
/// the actual COM write is an injected seam (<see cref="WindowsShortcutSpec"/> → bool), defaulting to the
/// real writer on Windows and replaced by a recorder in tests. The COM write itself is
/// <see cref="SupportedOSPlatformAttribute">windows</see>-only and therefore E2E-only.</para>
/// </summary>
public sealed class WindowsDesktopIntegrationService : IDesktopIntegrationService
{
    private readonly WindowsShortcutSpec _spec;
    private readonly Serilog.ILogger _log;
    private readonly Func<WindowsShortcutSpec, bool> _writeShortcut;

    /// <param name="spec">Where to write and what to point at.</param>
    /// <param name="log">Serilog sink; failures are logged, never surfaced as throws.</param>
    /// <param name="writeShortcut">Test seam for the COM write. Null = the real <c>IShellLink</c> writer
    /// (best-effort on Windows, a no-op returning false off Windows).</param>
    public WindowsDesktopIntegrationService(
        WindowsShortcutSpec spec,
        Serilog.ILogger log,
        Func<WindowsShortcutSpec, bool>? writeShortcut = null)
    {
        _spec = spec;
        _log = log;
        _writeShortcut = writeShortcut ?? DefaultWrite;
    }

    /// <summary>Build the service for the current user's real environment: the Start menu Programs
    /// folder, the running executable as both target and icon source.</summary>
    public static WindowsDesktopIntegrationService ForCurrentUser(Serilog.ILogger log) =>
        new(ResolveSpec(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.ProcessPath),
            log);

    public bool IsSupported => true;

    /// <summary>True when the Start menu shortcut already exists. A shortcut that points at a moved exe is
    /// rare on Windows (the launcher is portable, next to the client) and reading a <c>.lnk</c> target
    /// needs COM, so — unlike the Linux exec-line compare — existence is the check. A re-run overwrites it
    /// regardless, so a stale target self-heals on the next "Add to menu".</summary>
    public bool IsInstalled()
    {
        try { return File.Exists(_spec.ShortcutPath); }
        catch (Exception ex)
        {
            _log.Debug(ex, "Desktop integration: could not check the shortcut at {Path}", _spec.ShortcutPath);
            return false;
        }
    }

    public Task<DesktopIntegrationResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dir = Path.GetDirectoryName(_spec.ShortcutPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (!_writeShortcut(_spec))
            {
                _log.Warning("Desktop integration: writing the Start menu shortcut at {Path} did nothing",
                    _spec.ShortcutPath);
                return Task.FromResult(new DesktopIntegrationResult(
                    DesktopIntegrationOutcome.Failed, "Could not add the Start menu shortcut."));
            }

            _log.Information("Desktop integration installed: {Shortcut} → {Target}",
                _spec.ShortcutPath, _spec.TargetPath);
            return Task.FromResult(new DesktopIntegrationResult(
                DesktopIntegrationOutcome.Installed, "Added to your Start menu."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Desktop integration failed writing the shortcut at {Path}", _spec.ShortcutPath);
            return Task.FromResult(new DesktopIntegrationResult(
                DesktopIntegrationOutcome.Failed, "Could not add the Start menu shortcut."));
        }
    }

    // ── pure, testable resolution ──────────────────────────────────────────

    /// <summary>Resolve the shortcut spec from the Start menu Programs folder and the running exe. The
    /// shortcut file is <c>{Programs}/Stonetavern Launcher.lnk</c>; the target and the icon are both the
    /// executable (it embeds the branded icon). Returns a spec even when <paramref name="processPath"/> is
    /// null (falls back to the app id) so the shape is always well-formed for a test.</summary>
    internal static WindowsShortcutSpec ResolveSpec(string programsFolder, string? processPath)
    {
        var target = string.IsNullOrEmpty(processPath) ? DesktopIntegration.AppId : processPath;
        var workingDir = string.IsNullOrEmpty(processPath)
            ? string.Empty
            : Path.GetDirectoryName(processPath) ?? string.Empty;
        var shortcut = Path.Combine(programsFolder, $"{DesktopIntegration.DisplayName}.lnk");
        return new WindowsShortcutSpec(
            ShortcutPath: shortcut,
            TargetPath: target,
            WorkingDirectory: workingDir,
            IconPath: target,
            Description: "Launcher for the Stonetavern realm (Vanilla WoW 1.12.1)");
    }

    // ── the real COM write (Windows only, E2E-only) ────────────────────────

    /// <summary>Default writer: the real <c>IShellLink</c> COM write on Windows, a no-op (false) off
    /// Windows. The OS guard keeps the platform analyzer satisfied and makes the whole class safe to
    /// construct and unit-test on Linux with an injected fake writer.</summary>
    private static bool DefaultWrite(WindowsShortcutSpec spec)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return ComShortcutWriter.Write(spec);
    }

    [SupportedOSPlatform("windows")]
    private static class ComShortcutWriter
    {
        public static bool Write(WindowsShortcutSpec spec)
        {
            // Cast via object: the coclass and the interface have no compile-time relationship, but the
            // COM runtime resolves it through QueryInterface at the (object) boundary.
            var link = (IShellLinkW)(object)new CShellLink();
            link.SetPath(spec.TargetPath);
            if (!string.IsNullOrEmpty(spec.WorkingDirectory))
                link.SetWorkingDirectory(spec.WorkingDirectory);
            link.SetIconLocation(spec.IconPath, 0);
            // The tooltip has a hard length cap (INFOTIPSIZE); the description here is well under it.
            link.SetDescription(spec.Description);

            var file = (IPersistFile)link;
            file.Save(spec.ShortcutPath, fRemember: true);
            return true;
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private sealed class CShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
                int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
                int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
                [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }
}
