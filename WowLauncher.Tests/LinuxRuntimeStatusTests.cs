using System;
using WowLauncher.Localization;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The Linux runtime line in Settings says what really starts each client (owner 2026-09-28: "we run
/// on Proton now, is this window still right?"). It was not: it read "In use: /usr/bin/wine" while
/// both clients started on Proton, and no test looked at it at all.
/// </summary>
public sealed class LinuxRuntimeStatusTests
{
    private sealed class Config : IConfigService
    {
        public LauncherConfig Cfg = new();
        public LauncherConfig Load() => Cfg;
        public void Save(LauncherConfig config) => Cfg = config;
        public bool LastSaveSucceeded => true;
    }

    private static (SettingsViewModel Vm, Config Cfg) Vm(string? geProton, bool python = true, bool startScript = true)
    {
        var cfg = new Config();
        var vm = new SettingsViewModel(cfg, new NullFolderPicker())
        {
            FindGeProton = () => geProton,
            ProtonPythonOk = () => python,
            HasStartScript = _ => startScript,
        };
        return (vm, cfg);
    }

    [Fact]
    public void BothClients_ShowTheirProton_NotTheWineOfTheMenu()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (vm, _) = Vm("/home/p/.local/share/stonetavern/runners/GE-Proton11-7-x86_64/proton");

        var line = vm.LinuxRuntimeStatus;

        Assert.Contains(Loc.T("Settings_Runtime_PackageProton"), line);
        Assert.Contains("GE-Proton11-7-x86_64", line);
        Assert.DoesNotContain("/usr/bin/wine", line);
    }

    [Fact]
    public void WithoutGeProtonYet_ItSaysTheLauncherFetchesIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (vm, _) = Vm(geProton: null);

        Assert.Contains(Loc.T("Settings_Runtime_GeProtonPinned"), vm.LinuxRuntimeStatus);
    }

    [Fact]
    public void TooOldPython_SaysWhy_AndNamesTheFallback()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (vm, _) = Vm("/x/GE-Proton11-7-x86_64/proton", python: false);

        var line = vm.LinuxRuntimeStatus;

        Assert.Contains("Python 3.11", line);
        Assert.DoesNotContain("GE-Proton11-7-x86_64", line);
    }

    /// <summary>A 1.12.1 the player installed without the package start script is the one client the
    /// menu still decides for, and the line says that one.</summary>
    [Fact]
    public void AnOwn112Install_WithoutTheStartScript_ShowsTheMenuChoice()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (vm, cfg) = Vm("/x/GE-Proton11-7-x86_64/proton", startScript: false);
        cfg.Cfg.ClientInstalls[5875] = "/home/p/Games/WoW";

        Assert.DoesNotContain(Loc.T("Settings_Runtime_PackageProton"), vm.LinuxRuntimeStatus);
    }
}
