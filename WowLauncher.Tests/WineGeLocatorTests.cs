using System;
using System.Collections.Generic;
using System.IO;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// wine-ge discovery. Every case here is fabricated on disk rather than read off the dev machine: a
/// test that passes only because this workstation happens to have Lutris installed proves nothing
/// about a player's machine, and would go green on a broken implementation.
/// </summary>
public sealed class WineGeLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "winege-tests-" + Guid.NewGuid().ToString("N"));

    private string MakeRunner(string name)
    {
        var bin = Path.Combine(_root, name, "bin");
        Directory.CreateDirectory(bin);
        var wine = Path.Combine(bin, "wine");
        File.WriteAllText(wine, "#!/bin/sh\n");
        File.SetUnixFileMode(wine,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return wine;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void NoRunnersDirectory_ReturnsNull() =>
        Assert.Null(WineGeLocator.FindLatest(Path.Combine(_root, "does-not-exist")));

    [Fact]
    public void EmptyRunnersDirectory_ReturnsNull()
    {
        Directory.CreateDirectory(_root);
        Assert.Null(WineGeLocator.FindLatest(_root));
    }

    [Fact]
    public void SingleRunner_IsFound()
    {
        var expected = MakeRunner("wine-ge-8-26-x86_64");
        Assert.Equal(expected, WineGeLocator.FindLatest(_root));
    }

    /// <summary>The bug this pins: lexical ordering puts "8-9" above "8-26" and silently selects an
    /// older runner. The shell script this mirrors uses sort -V for exactly that reason.</summary>
    [Fact]
    public void HighestVersionWins_NumericallyNotLexically()
    {
        MakeRunner("wine-ge-8-9-x86_64");
        var newest = MakeRunner("wine-ge-8-26-x86_64");
        MakeRunner("wine-ge-7-42-x86_64");

        Assert.Equal(newest, WineGeLocator.FindLatest(_root));
    }

    [Fact]
    public void MajorVersionOutranksMinor()
    {
        MakeRunner("wine-ge-8-26-x86_64");
        var newest = MakeRunner("wine-ge-9-1-x86_64");

        Assert.Equal(newest, WineGeLocator.FindLatest(_root));
    }

    /// <summary>A runner directory with no bin/wine inside is not a usable runner. Picking it would
    /// hand the launcher a path that fails at Process.Start with no useful message.</summary>
    [Fact]
    public void RunnerWithoutWineBinary_IsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "wine-ge-9-9-x86_64", "bin"));
        var usable = MakeRunner("wine-ge-8-26-x86_64");

        Assert.Equal(usable, WineGeLocator.FindLatest(_root));
    }

    /// <summary>Only wine-ge runners qualify. Lutris keeps other builds in the same directory, and a
    /// plain lutris-wine has no D3D12 layer, so matching it would reintroduce the exact silent
    /// failure the wine-ge preference exists to prevent.</summary>
    [Fact]
    public void NonWineGeRunners_AreIgnored()
    {
        MakeRunner("lutris-7.2-2-x86_64");
        MakeRunner("wine-staging-9-0");

        Assert.Null(WineGeLocator.FindLatest(_root));
    }

    [Fact]
    public void NewerRunnerWithoutExecutePermission_IsIgnored()
    {
        var usable = MakeRunner("wine-ge-8-26-x86_64");
        var unusable = MakeRunner("wine-ge-9-99-x86_64");
        File.SetUnixFileMode(unusable, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Equal(usable, WineGeLocator.FindLatest(_root));
    }

    [Theory]
    [InlineData("wine-ge-8-26-x86_64", new[] { 8, 26 })]
    [InlineData("wine-ge-9-1", new[] { 9, 1 })]
    [InlineData("wine-ge", new int[0])]
    public void VersionKey_KeepsOnlyNumericSegments(string name, int[] expected) =>
        Assert.Equal(expected, (IEnumerable<int>)WineGeLocator.VersionKey(name));

    [Fact]
    public void RunnersDirFor_MatchesTheLutrisLayout() =>
        Assert.Equal(
            Path.Combine("/home/x", ".local", "share", "lutris", "runners", "wine"),
            WineGeLocator.RunnersDirFor("/home/x"));
}
