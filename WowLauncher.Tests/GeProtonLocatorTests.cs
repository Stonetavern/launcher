using System;
using System.Collections.Generic;
using System.IO;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// GE-Proton discovery for the new Linux runner order (KONZEPT §13, owner measurement 2026-09-19,
/// Ledger run U). Every case is fabricated on disk, same reasoning as <see cref="WineGeLocatorTests"/>:
/// a test that passes only because this workstation happens to have Steam installed proves nothing
/// about a player's machine.
/// </summary>
public sealed class GeProtonLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "geproton-tests-" + Guid.NewGuid().ToString("N"));

    private string MakeRunner(string dirsRoot, string name)
    {
        var dir = Path.Combine(dirsRoot, name);
        Directory.CreateDirectory(dir);
        var proton = Path.Combine(dir, "proton");
        File.WriteAllText(proton, "#!/bin/sh\n");
        File.SetUnixFileMode(proton,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return proton;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void NoRunnersDirectories_ReturnsNull() =>
        Assert.Null(GeProtonLocator.FindLatest([Path.Combine(_root, "does-not-exist")]));

    [Fact]
    public void EmptyRunnersDirectory_ReturnsNull()
    {
        var dir = Path.Combine(_root, "compatibilitytools.d");
        Directory.CreateDirectory(dir);
        Assert.Null(GeProtonLocator.FindLatest([dir]));
    }

    [Fact]
    public void NonExecutableProton_IsSkipped()
    {
        var dir = Path.Combine(_root, "compatibilitytools.d");
        var protonDir = Path.Combine(dir, "GE-Proton9-27");
        Directory.CreateDirectory(protonDir);
        // No execute bit set - a file that merely exists is not a usable runner.
        File.WriteAllText(Path.Combine(protonDir, "proton"), "#!/bin/sh\n");

        Assert.Null(GeProtonLocator.FindLatest([dir]));
    }

    [Fact]
    public void HighestVersion_Wins_NumericNotLexical()
    {
        // The exact trap WineGeLocator documents for its own naming scheme: lexical sort puts
        // "GE-Proton9-27" above "GE-Proton11-7" and would pick the OLDER runner while looking correct.
        var dir = Path.Combine(_root, "compatibilitytools.d");
        var older = MakeRunner(dir, "GE-Proton9-27");
        var newer = MakeRunner(dir, "GE-Proton11-7");

        var found = GeProtonLocator.FindLatest([dir]);

        Assert.Equal(newer, found);
        Assert.NotEqual(older, found);
    }

    [Fact]
    public void BothSteamRootsAreProbed_HighestAcrossBothWins()
    {
        // The beta script globs BOTH ~/.local/share/Steam/... and ~/.steam/root/... - a native install
        // and a Flatpak/alternate one can each carry a different GE-Proton, and the newer one across
        // BOTH must win regardless of which directory it happens to live in.
        var nativeDir = Path.Combine(_root, "native", "compatibilitytools.d");
        var altDir = Path.Combine(_root, "alt", "compatibilitytools.d");
        var native = MakeRunner(nativeDir, "GE-Proton9-27");
        var alt = MakeRunner(altDir, "GE-Proton11-7");

        var found = GeProtonLocator.FindLatest([nativeDir, altDir]);

        Assert.Equal(alt, found);
        Assert.NotEqual(native, found);
    }

    [Fact]
    public void RunnersDirsFor_NamesBothKnownSteamLocations()
    {
        var dirs = GeProtonLocator.RunnersDirsFor("/home/player");

        Assert.Equal(2, dirs.Count);
        Assert.Contains(Path.Combine("/home/player", ".local", "share", "Steam", "compatibilitytools.d"), dirs);
        Assert.Contains(Path.Combine("/home/player", ".steam", "root", "compatibilitytools.d"), dirs);
    }

    [Fact]
    public void FindLatestForCurrentUser_ReturnsNullOffLinux()
    {
        if (OperatingSystem.IsLinux()) return; // this assertion is only meaningful off Linux
        Assert.Null(GeProtonLocator.FindLatestForCurrentUser());
    }
}

/// <summary>The GE-Proton environment builder is a pure function of its three inputs (the whole point
/// of splitting it out of the actual process start, which needs no test of its own) - it is provable
/// without ever launching `proton` for real.</summary>
public sealed class GeProtonEnvironmentTests
{
    [Fact]
    public void Build_SetsAllThreeVariablesExactlyAsTheMeasuredScriptDoes()
    {
        var env = GeProtonEnvironment.Build(
            installRoot: "/home/player/Stonetavern",
            protonDirName: "GE-Proton11-7",
            steamCompatClientInstallPath: "/home/player/.local/share/Steam");

        Assert.Equal(3, env.Count);
        Assert.Equal("/home/player/.local/share/Steam", env["STEAM_COMPAT_CLIENT_INSTALL_PATH"]);
        Assert.Equal(
            Path.Combine("/home/player/Stonetavern", "proton-compat", "GE-Proton11-7"),
            env["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(
            Path.Combine("/home/player/Stonetavern", "proton-compat", "GE-Proton11-7", "pfx"),
            env["WINEPREFIX"]);
    }

    [Fact]
    public void Build_ScopesThePrefixToTheProtonVersion_SoSwitchingVersionsGetsAFreshOne()
    {
        var older = GeProtonEnvironment.Build("/root", "GE-Proton9-27", "/steam");
        var newer = GeProtonEnvironment.Build("/root", "GE-Proton11-7", "/steam");

        Assert.NotEqual(older["STEAM_COMPAT_DATA_PATH"], newer["STEAM_COMPAT_DATA_PATH"]);
        Assert.NotEqual(older["WINEPREFIX"], newer["WINEPREFIX"]);
    }

    [Fact]
    public void DirNameFromProtonExe_ReadsTheParentDirectoryName()
    {
        var exe = Path.Combine("/home/player", "compatibilitytools.d", "GE-Proton11-7", "proton");

        Assert.Equal("GE-Proton11-7", GeProtonEnvironment.DirNameFromProtonExe(exe));
    }
}
