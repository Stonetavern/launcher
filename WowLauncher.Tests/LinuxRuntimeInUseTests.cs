using System;
using WowLauncher.Models;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// One answer to "what starts this client on Linux" for Settings AND the start report. The report
/// still said "Runner: /usr/bin/wine" after Settings had been corrected to Proton (2026-09-28): the
/// rendered page showed it, no test did.
/// </summary>
public sealed class LinuxRuntimeInUseTests
{
    private static LinuxRuntimeInUse For(string key, string? dir, string? geProton = null, bool python = true, bool startScript = true) =>
        LinuxRuntimeInUseResolver.For(ClientVersion.ByKey(key), dir, "auto", null,
            () => geProton, () => python, _ => startScript);

    [Fact]
    public void Classic_FromThePackage_RunsItsOwnProton_AndTheReportSaysSo()
    {
        var r = For("1.12.1", "/games/Classic-1.12.1");

        Assert.Equal(LinuxRuntimeSource.PackageProton, r.Source);
        Assert.Contains("Proton", LinuxRuntimeInUseResolver.ReportLine(r));
        Assert.DoesNotContain("wine", LinuxRuntimeInUseResolver.ReportLine(r), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classic_NotInstalledYet_CountsAsThePackage()
    {
        Assert.Equal(LinuxRuntimeSource.PackageProton, For("1.12.1", null).Source);
    }

    [Fact]
    public void Classic_WithoutStartScript_FallsBackToTheWineMenu()
    {
        Assert.Equal(LinuxRuntimeSource.Wine, For("1.12.1", "/games/own-copy", startScript: false).Source);
    }

    [Fact]
    public void Modern_NamesTheGeProtonFound_OrThePinnedOne()
    {
        var found = For("1.14.2", null, geProton: "/steam/compatibilitytools.d/GE-Proton10-3/proton");
        Assert.Equal(LinuxRuntimeSource.GeProton, found.Source);
        Assert.Equal("GE-Proton GE-Proton10-3", LinuxRuntimeInUseResolver.ReportLine(found));

        Assert.Equal(LinuxRuntimeSource.GeProtonPinned, For("1.14.2", null).Source);
    }

    [Fact]
    public void Modern_WithTooOldPython_FallsBackToWine_AndSaysWhy()
    {
        var r = For("1.14.2", null, geProton: "/x/GE-Proton10-3/proton", python: false);

        Assert.Equal(LinuxRuntimeSource.Wine, r.Source);
        Assert.EndsWith("(Python too old for Proton)", LinuxRuntimeInUseResolver.ReportLine(r));
    }
}
