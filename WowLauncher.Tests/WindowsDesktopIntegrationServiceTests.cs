using System;
using System.IO;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The Windows "Add to menu" pendant: it resolves a Start menu shortcut spec and writes it through an
/// injected seam. The real COM <c>.lnk</c> write is Windows-only and E2E-only; everything here proves the
/// resolution and the install/idempotency logic on any OS with a fake writer, the same way the Linux
/// desktop-integration tests avoid touching the real desktop.
/// </summary>
public sealed class WindowsDesktopIntegrationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "win-desktop-" + Guid.NewGuid().ToString("N"));

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── resolution ─────────────────────────────────────────────────────────

    [Fact]
    public void ResolveSpec_PutsTheShortcutInPrograms_AndPointsBothTargetAndIconAtTheExe()
    {
        var programs = Path.Combine(_root, "Programs");
        var exe = Path.Combine(_root, "app", "WowLauncher.exe");

        var spec = WindowsDesktopIntegrationService.ResolveSpec(programs, exe);

        Assert.Equal(Path.Combine(programs, "Stonetavern Launcher.lnk"), spec.ShortcutPath);
        Assert.Equal(exe, spec.TargetPath);
        Assert.Equal(exe, spec.IconPath);   // the exe embeds the branded icon
        Assert.Equal(Path.Combine(_root, "app"), spec.WorkingDirectory);
    }

    [Fact]
    public void ResolveSpec_WithNoProcessPath_FallsBackToTheAppId_WithoutThrowing()
    {
        var spec = WindowsDesktopIntegrationService.ResolveSpec(Path.Combine(_root, "Programs"), null);

        Assert.Equal(DesktopIntegration.AppId, spec.TargetPath);
        Assert.Equal(string.Empty, spec.WorkingDirectory);
    }

    // ── behaviour ──────────────────────────────────────────────────────────

    [Fact]
    public void IsSupported_IsTrue_SoTheSettingsButtonShowsOnWindows()
    {
        var svc = new WindowsDesktopIntegrationService(Spec(), Log(), _ => true);
        Assert.True(svc.IsSupported);
    }

    [Fact]
    public async Task InstallAsync_WritesTheShortcut_AndReportsInstalled()
    {
        WindowsShortcutSpec? seen = null;
        var svc = new WindowsDesktopIntegrationService(Spec(), Log(), s => { seen = s; return true; });

        var result = await svc.InstallAsync();

        Assert.True(result.Ok);
        Assert.Equal(DesktopIntegrationOutcome.Installed, result.Outcome);
        Assert.NotNull(seen);
        Assert.EndsWith("Stonetavern Launcher.lnk", seen!.ShortcutPath);
    }

    [Fact]
    public async Task InstallAsync_CreatesTheProgramsDirectory_WhenItIsMissing()
    {
        var spec = Spec();
        Assert.False(Directory.Exists(Path.GetDirectoryName(spec.ShortcutPath)!));

        var svc = new WindowsDesktopIntegrationService(spec, Log(), _ => true);
        await svc.InstallAsync();

        Assert.True(Directory.Exists(Path.GetDirectoryName(spec.ShortcutPath)!));
    }

    /// <summary>A writer that does nothing (the off-Windows default, or a real COM failure) must not be
    /// reported as success — the button would then lie about having added the entry.</summary>
    [Fact]
    public async Task InstallAsync_WhenTheWriteDoesNothing_ReportsFailed_NotSuccess()
    {
        var svc = new WindowsDesktopIntegrationService(Spec(), Log(), _ => false);

        var result = await svc.InstallAsync();

        Assert.False(result.Ok);
        Assert.Equal(DesktopIntegrationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void IsInstalled_ReflectsWhetherTheShortcutFileExists()
    {
        var spec = Spec();
        var svc = new WindowsDesktopIntegrationService(spec, Log(), _ => true);

        Assert.False(svc.IsInstalled());

        Directory.CreateDirectory(Path.GetDirectoryName(spec.ShortcutPath)!);
        File.WriteAllText(spec.ShortcutPath, "");
        Assert.True(svc.IsInstalled());
    }

    private WindowsShortcutSpec Spec() =>
        WindowsDesktopIntegrationService.ResolveSpec(
            Path.Combine(_root, "Programs"), Path.Combine(_root, "app", "WowLauncher.exe"));
}
