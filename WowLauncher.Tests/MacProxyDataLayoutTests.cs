using System;
using System.IO;
using System.Linq;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The macOS package separates the proxy binary (<c>Hermes/bin/</c>) from the data it reads
/// (<c>Hermes/</c>), and the proxy switches into its own binary directory before looking. Measured on
/// Apple Silicon 2026-08-24: "Fail to load config file 'HermesProxy.config'", port 1119 never bound.
///
/// <para>These tests prove the repair does exactly two things: it links what is missing, and it keeps its
/// hands off everything else. The second half matters more — a repair that overwrites a player's files is
/// worse than the bug.</para>
/// </summary>
public sealed class MacProxyDataLayoutTests
{
    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>A package of the shipped macOS shape: binary in bin/, data one level up.</summary>
    private static (string ProxyExe, string DataDir, string Root) MacShapedPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"st-maclayout-{Guid.NewGuid():N}");
        var data = Path.Combine(root, "Hermes");
        var bin = Path.Combine(data, "bin");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(data, "CSV"));
        Directory.CreateDirectory(Path.Combine(data, "AccountData"));
        File.WriteAllText(Path.Combine(data, "HermesProxy.config"), "ServerAddress=play.stonetavern.app\n");
        File.WriteAllText(Path.Combine(bin, "JimsProxy-arm64"), "binary");
        return (Path.Combine(bin, "JimsProxy-arm64"), data, root);
    }

    [Fact]
    public void LinksTheDataTheProxyReadsFromBesideItsBinary()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var (exe, data, root) = MacShapedPackage();
        try
        {
            var linked = MacProxyDataLayout.EnsureDataBesideBinary(exe, data, Log());

            Assert.Equal(
                ["AccountData", "CSV", "HermesProxy.config"],
                linked.OrderBy(e => e, StringComparer.Ordinal).ToArray());

            var bin = Path.GetDirectoryName(exe)!;
            // What the proxy will actually try to open, read through the link.
            Assert.Contains("play.stonetavern.app",
                File.ReadAllText(Path.Combine(bin, "HermesProxy.config")), StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(bin, "CSV")), "CSV is not reachable beside the binary");
            Assert.True(Directory.Exists(Path.Combine(bin, "AccountData")), "AccountData is not reachable");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TheLinksAreRelative_SoMovingTheClientDirectoryDoesNotBreakThem()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var (exe, data, root) = MacShapedPackage();
        var moved = root + "-moved";
        try
        {
            MacProxyDataLayout.EnsureDataBesideBinary(exe, data, Log());
            Directory.Move(root, moved);

            var movedConfig = Path.Combine(moved, "Hermes", "bin", "HermesProxy.config");
            Assert.Contains("play.stonetavern.app", File.ReadAllText(movedConfig), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(moved)) Directory.Delete(moved, recursive: true);
        }
    }

    [Fact]
    public void DoesNothingWhenTheBinaryAlreadySitsWithItsData()
    {
        // The Windows and Linux shape, and a fixed macOS package. A repair that fires here would be a bug.
        var root = Path.Combine(Path.GetTempPath(), $"st-maclayout-{Guid.NewGuid():N}");
        var hermes = Path.Combine(root, "Hermes");
        Directory.CreateDirectory(Path.Combine(hermes, "CSV"));
        File.WriteAllText(Path.Combine(hermes, "HermesProxy.config"), "ServerAddress=play.stonetavern.app\n");
        File.WriteAllText(Path.Combine(hermes, "JimsProxy"), "binary");
        try
        {
            var linked = MacProxyDataLayout.EnsureDataBesideBinary(
                Path.Combine(hermes, "JimsProxy"), hermes, Log());
            Assert.Empty(linked);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NeverReplacesSomethingThatIsAlreadyThere()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        // The owner's hand-made workaround (a real copy in bin/) must survive untouched — and so must a
        // second run of the repair itself, which is the same situation.
        var (exe, data, root) = MacShapedPackage();
        var bin = Path.GetDirectoryName(exe)!;
        File.WriteAllText(Path.Combine(bin, "HermesProxy.config"), "ServerAddress=hand-made.example\n");
        try
        {
            var linked = MacProxyDataLayout.EnsureDataBesideBinary(exe, data, Log());

            Assert.DoesNotContain("HermesProxy.config", linked);
            Assert.Equal("ServerAddress=hand-made.example\n",
                File.ReadAllText(Path.Combine(bin, "HermesProxy.config")));

            // Second run: idempotent, nothing left to do.
            Assert.Empty(MacProxyDataLayout.EnsureDataBesideBinary(exe, data, Log()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SkipsEntriesThePackageDoesNotHave()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        // A dangling link would make the proxy fail in a NEW way, which is strictly worse than a missing
        // optional directory.
        var (exe, data, root) = MacShapedPackage();
        Directory.Delete(Path.Combine(data, "AccountData"));
        try
        {
            var linked = MacProxyDataLayout.EnsureDataBesideBinary(exe, data, Log());

            Assert.DoesNotContain("AccountData", linked);
            var bin = Path.GetDirectoryName(exe)!;
            Assert.False(File.Exists(Path.Combine(bin, "AccountData")) ||
                         Directory.Exists(Path.Combine(bin, "AccountData")),
                "a link was created for an entry the package does not have");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
