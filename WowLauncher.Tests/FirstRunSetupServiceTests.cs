using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Proofs for the automatic one-time AppImage first-run setup (owner UX 2026-07-24): the launcher
/// relocates itself into <c>~/Applications</c>, drops a trusted desktop shortcut and registers the menu
/// entry — once, then never again. Everything runs against throwaway temp directories and a recording
/// command runner, so nothing touches the real desktop or HOME.
/// </summary>
public sealed class FirstRunSetupServiceTests
{
    private const string AppId = "stonetavern-launcher";

    // ── pure guard ─────────────────────────────────────────────────────────

    [Fact]
    public void ShouldRun_TrueOnlyFromAppImage_AndNotAlreadyDone()
    {
        Assert.True(FirstRunSetup.ShouldRun("/home/p/Downloads/Stonetavern.AppImage", alreadyDone: false));
        Assert.False(FirstRunSetup.ShouldRun("/home/p/Downloads/Stonetavern.AppImage", alreadyDone: true));
        Assert.False(FirstRunSetup.ShouldRun(null, alreadyDone: false));
        Assert.False(FirstRunSetup.ShouldRun("", alreadyDone: false));
    }

    [Fact]
    public void ResolveTargetPath_KeepsFileName_UnderApplications()
    {
        var target = LinuxFirstRunSetup.ResolveTargetPath(
            "/home/p/Downloads/stonetavern-launcher-1.2.0-x86_64.AppImage", "/home/p/Applications");
        Assert.Equal("/home/p/Applications/stonetavern-launcher-1.2.0-x86_64.AppImage", target);
    }

    // ── full first-run flow ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Relocates_RegistersMenu_WritesTrustedDesktopShortcut_AndFlipsFlag()
    {
        using var t = new Temp();
        var appImage = t.FakeAppImage("stonetavern-launcher-1.2.0-x86_64.AppImage");
        var config = new FakeConfig(new LauncherConfig());
        var (setup, ran) = t.Build(appImage, config);

        await setup.RunAsync();

        // 1) relocated into ~/Applications and made executable
        var relocated = Path.Combine(t.ApplicationsDir, Path.GetFileName(appImage));
        Assert.True(File.Exists(relocated));
        if (OperatingSystem.IsLinux())
            Assert.True(File.GetUnixFileMode(relocated).HasFlag(UnixFileMode.UserExecute));

        // 2) menu entry written, pointing at the RELOCATED copy (not the download)
        var menu = Path.Combine(t.DataHome, "applications", $"{AppId}.desktop");
        Assert.True(File.Exists(menu));
        Assert.Contains($"Exec={relocated}", await File.ReadAllTextAsync(menu), StringComparison.Ordinal);

        // 3) trusted, executable desktop shortcut
        var shortcut = Path.Combine(t.DesktopDir, $"{AppId}.desktop");
        Assert.True(File.Exists(shortcut));
        if (OperatingSystem.IsLinux())
            Assert.True(File.GetUnixFileMode(shortcut).HasFlag(UnixFileMode.UserExecute));
        Assert.Contains("StartupWMClass=WowLauncher", await File.ReadAllTextAsync(shortcut), StringComparison.Ordinal);
        Assert.Contains("gio", ran); // trust marked through the injected runner, no real process

        // 4) one-time flag flipped so it never runs again
        Assert.True(config.Current.DesktopIntegrationDone);
    }

    [Fact]
    public async Task RunAsync_IsNoOp_WhenAlreadyDone()
    {
        using var t = new Temp();
        var appImage = t.FakeAppImage("Stonetavern.AppImage");
        var config = new FakeConfig(new LauncherConfig { DesktopIntegrationDone = true });
        var (setup, _) = t.Build(appImage, config);

        await setup.RunAsync();

        Assert.False(File.Exists(Path.Combine(t.ApplicationsDir, Path.GetFileName(appImage))));
        Assert.False(File.Exists(Path.Combine(t.DesktopDir, $"{AppId}.desktop")));
    }

    [Fact]
    public async Task RunAsync_IsNoOp_WhenNotLaunchedFromAppImage()
    {
        using var t = new Temp();
        var config = new FakeConfig(new LauncherConfig());
        // No $APPIMAGE → not an AppImage run.
        var layout = new FirstRunLayout(
            AppImagePath: null, ApplicationsDir: t.ApplicationsDir, DesktopDir: t.DesktopDir,
            DataHome: t.DataHome, AppDir: null, ProcessPath: null);
        var setup = new LinuxFirstRunSetup(layout, config, Serilog.Log.Logger, t.Runner);

        await setup.RunAsync();

        Assert.False(config.Current.DesktopIntegrationDone);
        Assert.False(File.Exists(Path.Combine(t.DesktopDir, $"{AppId}.desktop")));
    }

    [Fact]
    public async Task RunAsync_DoesNotClobber_AnExistingRelocatedCopy()
    {
        using var t = new Temp();
        var appImage = t.FakeAppImage("Stonetavern.AppImage", content: "new");
        // A same-named file already sits in ~/Applications (a previous install). It must be kept.
        Directory.CreateDirectory(t.ApplicationsDir);
        var existing = Path.Combine(t.ApplicationsDir, "Stonetavern.AppImage");
        await File.WriteAllTextAsync(existing, "existing");
        var config = new FakeConfig(new LauncherConfig());
        var (setup, _) = t.Build(appImage, config);

        await setup.RunAsync();

        Assert.Equal("existing", await File.ReadAllTextAsync(existing)); // untouched
        Assert.True(config.Current.DesktopIntegrationDone);              // still completed
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class Temp : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "mechagon-firstrun-" + Guid.NewGuid().ToString("N"));

        public Temp()
        {
            Directory.CreateDirectory(SrcDir);
            Directory.CreateDirectory(DesktopDir); // the shortcut writer requires an existing desktop dir
        }

        public string SrcDir => Path.Combine(_root, "Downloads");
        public string ApplicationsDir => Path.Combine(_root, "Applications");
        public string DesktopDir => Path.Combine(_root, "Desktop");
        public string DataHome => Path.Combine(_root, "share");

        public List<string> Ran { get; } = [];

        public Func<string, IReadOnlyList<string>, CancellationToken, Task> Runner =>
            (tool, _, _) => { Ran.Add(tool); return Task.CompletedTask; };

        public string FakeAppImage(string name, string content = "appimage")
        {
            var p = Path.Combine(SrcDir, name);
            File.WriteAllText(p, content);
            return p;
        }

        public (LinuxFirstRunSetup setup, List<string> ran) Build(string appImage, IConfigService config)
        {
            var layout = new FirstRunLayout(
                AppImagePath: appImage, ApplicationsDir: ApplicationsDir, DesktopDir: DesktopDir,
                DataHome: DataHome, AppDir: null, ProcessPath: null);
            return (new LinuxFirstRunSetup(layout, config, Serilog.Log.Logger, Runner), Ran);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch { /* temp scratch */ }
        }
    }

    private sealed class FakeConfig(LauncherConfig cfg) : IConfigService
    {
        private LauncherConfig _cfg = cfg;

        public LauncherConfig Current => _cfg;
        public LauncherConfig Load() => _cfg;
        public void Save(LauncherConfig config) { _cfg = config; LastSaveSucceeded = true; }
        public bool LastSaveSucceeded { get; private set; } = true;
    }
}
