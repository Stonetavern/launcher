using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The realmlist address is written verbatim into files the game client reads as CONFIGURATION
/// (<c>realmlist.wtf</c>, <c>Data/&lt;locale&gt;/realmlist.wtf</c>, <c>WTF/Config.wtf</c>). A value
/// carrying a newline therefore does not corrupt one setting, it appends further directives that the
/// client obeys. The value reaches this code from three places the launcher does not control: the
/// settings form, the server manifest, and a hand-edited launcher_config.json.
///
/// <para>These tests pin the write side. <see cref="SettingsRealmValidationTests"/> pins the form.</para>
/// </summary>
public sealed class RealmlistWriteTests
{
    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    private sealed class StubConfig : IConfigService
    {
        public LauncherConfig Load() => new();
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    private sealed class NoRoots : IInstallRootsProvider
    {
        public IEnumerable<string> ExeSearchPaths(string configuredPath) => [];
        public IEnumerable<string> CommonInstallRoots() => [];
        public IReadOnlyList<string> InstallFolderNames() => [];
    }

    private sealed class NotRunning : IGameProcessDetector
    {
        public bool IsGameRunning(string? expectedExePath) => false;
    }

    private sealed class NoLauncher : IGameLauncher
    {
        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory) =>
            Task.FromResult(new GameLaunchResult(false, null, "test"));
    }

    private static ClientService NewService() =>
        new(new StubConfig(), new Serilog.LoggerConfiguration().CreateLogger(),
            new NoRoots(), new NotRunning(), new NoLauncher());

    private static string NewWowDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "st-realmlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Tests ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAddressCarryingANewline_IsRefused_AndTheOldRealmlistSurvives()
    {
        var dir = NewWowDir();
        var path = Path.Combine(dir, "realmlist.wtf");
        File.WriteAllText(path, "set realmlist old.stonetavern.app\n");

        // The shape that matters: everything after the newline would be a directive of its own.
        NewService().SetRealmlist(dir, "play.stonetavern.app\nset gxWindow \"0\"");

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("gxWindow", text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("old.stonetavern.app", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SurroundingWhitespaceIsToleratedAndTrimmed()
    {
        var dir = NewWowDir();
        NewService().SetRealmlist(dir, "  play.stonetavern.app\n");

        var text = File.ReadAllText(Path.Combine(dir, "realmlist.wtf"));
        Assert.Equal("set realmlist play.stonetavern.app\n", text);
    }

    [Fact]
    public void ConfigureClient_WritesNothingWhenTheAddressCarriesADirective()
    {
        var dir = NewWowDir();
        NewService().ConfigureClient(dir, "enUS", "play.stonetavern.app\nSET hwDetect \"0\"");

        Assert.False(File.Exists(Path.Combine(dir, "WTF", "Config.wtf")),
            "A realmlist address that would inject a second directive must abort the whole write.");
        Assert.False(File.Exists(Path.Combine(dir, "realmlist.wtf")));
    }

    [Fact]
    public void ConfigureClient_WritesExactlyTwoDirectivesForAGoodAddress()
    {
        var dir = NewWowDir();
        NewService().ConfigureClient(dir, "enUS", "play.stonetavern.app");

        var cfg = File.ReadAllText(Path.Combine(dir, "WTF", "Config.wtf"));
        Assert.Equal(2, cfg.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("realmList \"play.stonetavern.app\"", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocaleThatIsAPathTraversal_IsRejected()
    {
        var dir = NewWowDir();
        NewService().ConfigureClient(dir, "../../evil", "play.stonetavern.app");

        var cfg = File.ReadAllText(Path.Combine(dir, "WTF", "Config.wtf"));
        Assert.DoesNotContain("..", cfg, StringComparison.Ordinal);
        Assert.Contains("enUS", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyAddress_WritesNothingAtAll()
    {
        var dir = NewWowDir();
        NewService().SetRealmlist(dir, "   ");

        Assert.False(File.Exists(Path.Combine(dir, "realmlist.wtf")),
            "Writing an empty realmlist points the client at nothing and silently breaks it.");
    }

    [Theory]
    [InlineData("play.stonetavern.app", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("play.stonetavern.app:8085", true)]
    [InlineData("[::1]", true)]
    [InlineData("play.stonetavern.app\nset x 1", false)]
    [InlineData("play stonetavern app", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void AddressValidation_AcceptsAddressesAndRejectsEverythingElse(string address, bool expected)
        => Assert.Equal(expected, ClientService.IsValidRealmlistAddress(address));
}
