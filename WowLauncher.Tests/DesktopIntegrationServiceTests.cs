using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Proofs for the opt-in Linux desktop integration. Two things must hold: the written
/// <c>.desktop</c> carries the exact <c>StartupWMClass</c> and <c>Exec</c> that make KDE/GNOME match
/// the running window and relaunch the right binary, and the <c>Exec</c> resolution follows the
/// AppImage rule ($APPIMAGE wins over the extracted apphost path).
///
/// <para>The service writes against an injected <see cref="DesktopIntegrationLayout"/>, so every test
/// points it at a throwaway temp HOME and a no-op cache-refresh runner — nothing touches the real
/// desktop.</para>
/// </summary>
public sealed class DesktopIntegrationServiceTests
{
    private const string AppId = "stonetavern-launcher";

    private static (LinuxDesktopIntegrationService svc, string dataHome, List<string> ran) Build(
        string execLine, IReadOnlyList<DesktopIconSource> icons)
    {
        var dataHome = Path.Combine(Path.GetTempPath(), "mechagon-desktop-" + Guid.NewGuid().ToString("N"));
        var layout = new DesktopIntegrationLayout(dataHome, execLine, icons);
        var ran = new List<string>();
        Task runner(string tool, IReadOnlyList<string> _, CancellationToken __) { ran.Add(tool); return Task.CompletedTask; }
        var svc = new LinuxDesktopIntegrationService(layout, Serilog.Log.Logger, runner);
        return (svc, dataHome, ran);
    }

    // ── Exec resolution (the AppImage rule) ────────────────────────────────

    [Fact]
    public void ResolveExec_PrefersAppImageEnv_OverProcessPath()
    {
        // Under AppImage the process path is the extracted apphost in the mount; $APPIMAGE is the
        // durable .AppImage file and must win.
        var exec = LinuxDesktopIntegrationService.ResolveExecLine(
            appImage: "/home/p/Apps/Stonetavern.AppImage",
            processPath: "/tmp/.mount_abc/usr/bin/WowLauncher");
        Assert.Equal("/home/p/Apps/Stonetavern.AppImage", exec);
    }

    [Fact]
    public void ResolveExec_FallsBackToProcessPath_WithoutAppImage()
    {
        var exec = LinuxDesktopIntegrationService.ResolveExecLine(
            appImage: null, processPath: "/opt/stonetavern/lib/WowLauncher");
        Assert.Equal("/opt/stonetavern/lib/WowLauncher", exec);
    }

    [Fact]
    public void ResolveExec_QuotesPathWithSpaces()
    {
        var exec = LinuxDesktopIntegrationService.ResolveExecLine(
            appImage: "/home/p/My Games/Stonetavern.AppImage", processPath: null);
        Assert.Equal("\"/home/p/My Games/Stonetavern.AppImage\"", exec);
    }

    // ── .desktop contents ──────────────────────────────────────────────────

    [Fact]
    public async Task Install_WritesDesktopEntry_WithStartupWmClassAndExec()
    {
        const string exec = "/home/p/Apps/Stonetavern.AppImage";
        var (svc, dataHome, ran) = Build(exec, []);
        try
        {
            var result = await svc.InstallAsync();

            Assert.True(result.Ok);
            Assert.Equal(DesktopIntegrationOutcome.Installed, result.Outcome);

            var desktopPath = Path.Combine(dataHome, "applications", $"{AppId}.desktop");
            Assert.True(File.Exists(desktopPath));
            var body = await File.ReadAllTextAsync(desktopPath);

            Assert.Contains("[Desktop Entry]", body, StringComparison.Ordinal);
            Assert.Contains("StartupWMClass=WowLauncher", body, StringComparison.Ordinal);
            Assert.Contains($"Exec={exec}", body, StringComparison.Ordinal);
            Assert.Contains($"Icon={AppId}", body, StringComparison.Ordinal);
            Assert.Contains("Categories=Game;", body, StringComparison.Ordinal);
            Assert.Contains("StartupNotify=true", body, StringComparison.Ordinal);
            Assert.Contains("Terminal=false", body, StringComparison.Ordinal);
            // exactly one Type= line (regression against an earlier duplicate)
            Assert.Equal(1, body.Split('\n').Count(l => l.StartsWith("Type=", StringComparison.Ordinal)));

            // the best-effort cache refresh was invoked through the injected runner, not real processes
            Assert.Contains("update-desktop-database", ran);
        }
        finally { TryDelete(dataHome); }
    }

    [Fact]
    public async Task Install_CopiesIcons_IntoHicolorTree()
    {
        var iconSrc = Path.Combine(Path.GetTempPath(), "mechagon-icon-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(iconSrc, [0x89, 0x50, 0x4E, 0x47]); // PNG magic; content is irrelevant here
        var (svc, dataHome, _) = Build("/opt/WowLauncher", [new DesktopIconSource(256, iconSrc)]);
        try
        {
            var result = await svc.InstallAsync();
            Assert.True(result.Ok);

            var target = Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps", $"{AppId}.png");
            Assert.True(File.Exists(target));
        }
        finally { TryDelete(dataHome); File.Delete(iconSrc); }
    }

    [Fact]
    public async Task IsInstalled_TrueOnlyWhenExecMatches()
    {
        var (svc, dataHome, _) = Build("/opt/WowLauncher", []);
        try
        {
            Assert.False(svc.IsInstalled()); // nothing written yet
            await svc.InstallAsync();
            Assert.True(svc.IsInstalled()); // written and Exec matches

            // A second install is a no-op (idempotent) reported as AlreadyInstalled.
            var again = await svc.InstallAsync();
            Assert.Equal(DesktopIntegrationOutcome.AlreadyInstalled, again.Outcome);

            // A layout with a different Exec (moved AppImage) reads the same file as NOT installed.
            var moved = new LinuxDesktopIntegrationService(
                new DesktopIntegrationLayout(dataHome, "/somewhere/else/WowLauncher", []),
                Serilog.Log.Logger, (_, _, _) => Task.CompletedTask);
            Assert.False(moved.IsInstalled());
        }
        finally { TryDelete(dataHome); }
    }

    // ── icon source resolution ─────────────────────────────────────────────

    [Fact]
    public void ResolveIconSources_UsesAppDirHicolorTree_UnderAppImage()
    {
        const string appDir = "/tmp/.mount_abc";
        static bool Exists(string p) => p.StartsWith(appDir, StringComparison.Ordinal); // all three sizes present in the mount

        var icons = LinuxDesktopIntegrationService.ResolveIconSources(
            appDir, processPath: "/tmp/.mount_abc/usr/bin/WowLauncher", fileExists: Exists);

        Assert.Equal(3, icons.Count);
        Assert.All(icons, i => Assert.StartsWith(appDir, i.SourcePath));
        Assert.Contains(icons, i => i.Size == 512);
        Assert.Contains(icons, i => i.Size == 256);
        Assert.Contains(icons, i => i.Size == 128);
    }

    [Fact]
    public void ResolveIconSources_FallsBackToIntegration256_InTarball()
    {
        // Tarball layout: exe under <root>/lib, single 256px icon under <root>/integration.
        const string exeDir = "/opt/stonetavern/lib";
        var integration = Path.GetFullPath(Path.Combine(exeDir, "..", "integration", $"{AppId}.png"));
        bool Exists(string p) => Path.GetFullPath(p) == integration;

        var icons = LinuxDesktopIntegrationService.ResolveIconSources(
            appDir: null, processPath: Path.Combine(exeDir, "WowLauncher"), fileExists: Exists);

        var only = Assert.Single(icons);
        Assert.Equal(256, only.Size);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* temp scratch */ }
    }
}
