using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The realm a launch actually reaches. Owner report 2026-07-27: "I add a realm and it does not start
/// with the address I entered." Three independent sinks decide that, and the launcher served only one
/// of them — so the tests here are one per sink plus the precedence rule that feeds them:
///
/// <list type="bullet">
/// <item>the loader script rewrites realmlist.wtf from its own default AFTER the launcher wrote it
/// (<c>REALMLIST="${REALMLIST:-play.stonetavern.app}"</c>) — proven by the environment it is handed;</item>
/// <item>the 1.14.2 proxy carries the realm in its OWN config (<c>ServerAddress</c>), which shipped
/// pinned to Stonetavern;</item>
/// <item>the manifest used to overwrite a player's typed address unconditionally.</item>
/// </list>
/// </summary>
public sealed class RealmBindingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "realm-bind-" + Guid.NewGuid().ToString("N"));

    public RealmBindingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static Serilog.ILogger Logger() => new Serilog.LoggerConfiguration().CreateLogger();

    // ── The address itself ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("play.stonetavern.app", "play.stonetavern.app", null)]
    [InlineData("  play.example.invalid  ", "play.example.invalid", null)]
    [InlineData("play.example.invalid:3725", "play.example.invalid", 3725)]
    [InlineData("10.0.0.5:3724", "10.0.0.5", 3724)]
    [InlineData("[fd00::1]:3724", "[fd00::1]", 3724)]
    public void Parse_SplitsHostAndPort(string raw, string host, int? port)
    {
        var address = RealmAddress.Parse(raw);

        Assert.NotNull(address);
        Assert.Equal(host, address!.Host);
        Assert.Equal(port, address.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("play.example.invalid:0")]        // not a port
    [InlineData("play.example.invalid:70000")]    // not a port
    [InlineData("play.example.invalid:abc")]      // not a port
    [InlineData("play.example.invalid\nSET x \"y\"")]  // would become an extra directive in a .wtf file
    [InlineData("play example invalid")]
    public void Parse_RefusesAnythingUnsafe(string? raw) => Assert.Null(RealmAddress.Parse(raw));

    [Fact]
    public void Value_RoundTripsWhatWasParsed()
    {
        Assert.Equal("play.example.invalid:3725", RealmAddress.Parse("play.example.invalid:3725")!.Value);
        Assert.Equal("play.example.invalid", RealmAddress.Parse("play.example.invalid")!.Value);
    }

    // ── Precedence: whose address wins ────────────────────────────────────────────────────────

    [Fact]
    public void APlayersOwnRealm_IsNeverOverriddenByTheManifest()
    {
        // The reported defect, at the level it lives: a custom realm, a manifest in play (the player
        // pointed it at the Stonetavern manifest, or kept one) — the typed address must survive.
        var own = new RealmEntry { Id = "my-realm", Name = "My realm", RealmlistAddress = "play.example.invalid" };

        var effective = RealmBinding.Effective(own, shippedAddress: null, manifestRealmlist: "play.stonetavern.app");

        Assert.Equal("play.example.invalid", effective);
    }

    [Fact]
    public void AnUntouchedPreset_FollowsTheManifest()
    {
        // The one case the manifest may still move: the operator relocates the shipped realm and every
        // launcher that never had its address edited follows, with no new release.
        var preset = RealmRegistry.Presets()[0];

        var effective = RealmBinding.Effective(
            preset, RealmRegistry.StonetavernAddress, manifestRealmlist: "play2.stonetavern.app");

        Assert.Equal("play2.stonetavern.app", effective);
    }

    [Fact]
    public void AnEditedPreset_KeepsThePlayersAddress()
    {
        var preset = RealmRegistry.Presets()[0];
        preset.RealmlistAddress = "my.own.invalid";

        var effective = RealmBinding.Effective(
            preset, RealmRegistry.StonetavernAddress, manifestRealmlist: "play2.stonetavern.app");

        Assert.Equal("my.own.invalid", effective);
    }

    // ── Sink 1: the proxy's own config (1.14.2) ───────────────────────────────────────────────

    private string WriteProxyConfig() =>
        WriteProxyConfig("""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <appSettings>
            <add key="ClientBuild" value="42597" />
            <add key="ServerAddress" value="play.stonetavern.app" />
            <add key="ServerPort" value="3724" />
            <add key="BNetPort" value="1119" />
          </appSettings>
        </configuration>
        """);

    private string WriteProxyConfig(string xml)
    {
        var path = Path.Combine(_dir, "HermesProxy.config");
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void PointProxyAtRealm_WritesTheHost_AndLeavesEverythingElseAlone()
    {
        var path = WriteProxyConfig();

        var ok = RealmBinding.PointProxyAtRealm(path, RealmAddress.Parse("play.example.invalid")!, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("play.example.invalid", ProxyEndpointConfig.ReadServerAddress(path));
        // A bare host names no port, so the port the package ships with is left as it is rather than guessed.
        Assert.Equal("3724", ProxyEndpointConfig.ReadKey(path, "ServerPort"));
        // Settings the launcher has no opinion about survive the rewrite.
        Assert.Equal("42597", ProxyEndpointConfig.ReadKey(path, "ClientBuild"));
        Assert.Equal("1119", ProxyEndpointConfig.ReadKey(path, "BNetPort"));
    }

    [Fact]
    public void PointProxyAtRealm_WritesThePort_WhenTheRealmNamesOne()
    {
        var path = WriteProxyConfig();

        Assert.True(RealmBinding.PointProxyAtRealm(path, RealmAddress.Parse("play.example.invalid:3725")!, out _));

        Assert.Equal("play.example.invalid", ProxyEndpointConfig.ReadServerAddress(path));
        Assert.Equal("3725", ProxyEndpointConfig.ReadKey(path, "ServerPort"));
    }

    [Fact]
    public void PointProxyAtRealm_AddsTheKey_WhenTheConfigDoesNotCarryIt()
    {
        var path = WriteProxyConfig("""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <appSettings>
            <add key="BNetPort" value="1119" />
          </appSettings>
        </configuration>
        """);

        Assert.True(RealmBinding.PointProxyAtRealm(path, RealmAddress.Parse("play.example.invalid")!, out _));

        Assert.Equal("play.example.invalid", ProxyEndpointConfig.ReadServerAddress(path));
    }

    [Fact]
    public void PointProxyAtRealm_Refuses_WhenThereIsNoConfig()
    {
        var missing = Path.Combine(_dir, "nope", "HermesProxy.config");

        var ok = RealmBinding.PointProxyAtRealm(missing, RealmAddress.Parse("play.example.invalid")!, out var error);

        Assert.False(ok);
        Assert.Contains(missing, error, StringComparison.Ordinal);
    }

    [Fact]
    public void PointProxyAtRealm_Refuses_WhenTheConfigIsNotReadableAsXml()
    {
        // Fail-closed: an unparseable config means the proxy would keep whatever it had — which is a
        // different realm than the player picked, and starting anyway is the silent failure to avoid.
        var path = WriteProxyConfig("this is not xml");

        Assert.False(RealmBinding.PointProxyAtRealm(path, RealmAddress.Parse("play.example.invalid")!, out var error));
        Assert.NotEqual("", error);
    }

    // ── Sink 2: the loader script's own realmlist write (1.12.1) ──────────────────────────────

    [Fact]
    public async Task TheLoaderScript_IsHandedTheSelectedRealm()
    {
        // Without this the script's own line — REALMLIST="${REALMLIST:-play.stonetavern.app}" — wins,
        // and it runs AFTER the launcher wrote realmlist.wtf. Everything the launcher did was correct,
        // on disk, and undone a second later.
        using var t = new TempLoaderClient(_dir);
        IReadOnlyDictionary<string, string>? passed = null;
        var launcher = new LoaderScriptLauncher(
            new NeverLauncher(), Logger(),
            realmAddress: () => "play.example.invalid:3725",
            runScript: (_, env) => { passed = env; return GameLaunchResult.Ok(1); });

        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(result.Started);
        Assert.NotNull(passed);
        Assert.Equal("play.example.invalid:3725", passed![RealmBinding.RealmlistEnvVar]);
    }

    [Fact]
    public async Task TheLoaderScript_IsRefused_WhenTheAddressIsNotUsable()
    {
        // A mangled address must not fall back to launch.sh's shipped default. Starting a custom-realm
        // player on Stonetavern would look successful while violating their explicit choice.
        using var t = new TempLoaderClient(_dir);
        var called = false;
        var launcher = new LoaderScriptLauncher(
            new NeverLauncher(), Logger(),
            realmAddress: () => "play.example.invalid\nSET portal \"evil\"",
            runScript: (_, _) => { called = true; return GameLaunchResult.Ok(1); });

        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.False(result.Started);
        Assert.False(called);
        Assert.Contains("realm address", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NeverLauncher : IGameLauncher
    {
        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory) =>
            throw new InvalidOperationException("the loader script must be used, not the bare start");
    }

    private sealed class TempLoaderClient : IDisposable
    {
        public string Dir { get; }
        public TempLoaderClient(string root)
        {
            Dir = Path.Combine(root, "client-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "WoW.exe"), "MZ");
            File.WriteAllText(Path.Combine(Dir, "launch.sh"), "#!/usr/bin/env bash\n");
        }
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { } }
    }

    // ── Sink 3: the modern launcher refuses rather than connecting somewhere else ──────────────

    [Fact]
    public async Task TheModernLaunch_PointsTheProxyAtTheRealm_BeforeStartingIt()
    {
        var (exe, proxyDir) = MakeModernBundle();
        WriteProxyConfigIn(proxyDir);
        var proxy = new CountingProxy();

        var launcher = new ModernClientLauncher(
            Logger(), new StubWine(), new AlwaysRunning(), new NoDisplay(), _ => proxy,
            session: null, peArch: _ => PeArch.X64, realmAddress: () => "play.example.invalid:3725",
            clientAppearTimeout: TimeSpan.FromMilliseconds(100), delay: _ => Task.Delay(1));

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        Assert.Equal(1, proxy.StartCount);
        var configPath = Path.Combine(proxyDir, "HermesProxy.config");
        Assert.Equal("play.example.invalid", ProxyEndpointConfig.ReadServerAddress(configPath));
        Assert.Equal("3725", ProxyEndpointConfig.ReadKey(configPath, "ServerPort"));
    }

    [Fact]
    public async Task TheModernLaunch_IsRefused_WhenTheRealmAddressIsUnusable()
    {
        var (exe, proxyDir) = MakeModernBundle();
        WriteProxyConfigIn(proxyDir);
        var proxy = new CountingProxy();

        var launcher = new ModernClientLauncher(
            Logger(), new StubWine(), new AlwaysRunning(), new NoDisplay(), _ => proxy,
            session: null, peArch: _ => PeArch.X64, realmAddress: () => "",
            clientAppearTimeout: TimeSpan.FromMilliseconds(100), delay: _ => Task.Delay(1));

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, proxy.StartCount);   // nothing was started against the wrong realm
    }

    [Fact]
    public async Task TheModernLaunch_IsRefused_WhenTheProxyConfigIsMissing()
    {
        var (exe, _) = MakeModernBundle();   // proxy binary present, its config is not
        var proxy = new CountingProxy();

        var launcher = new ModernClientLauncher(
            Logger(), new StubWine(), new AlwaysRunning(), new NoDisplay(), _ => proxy,
            session: null, peArch: _ => PeArch.X64, realmAddress: () => "play.example.invalid",
            clientAppearTimeout: TimeSpan.FromMilliseconds(100), delay: _ => Task.Delay(1));

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, proxy.StartCount);
    }

    private (string Exe, string ProxyDir) MakeModernBundle()
    {
        var root = Path.Combine(_dir, "bundle-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(root, "World of Warcraft", "_classic_era_");
        Directory.CreateDirectory(clientDir);
        var exe = Path.Combine(clientDir, "WowClassic.exe");
        File.WriteAllText(exe, "MZ");

        var launcherDir = Path.Combine(root, "Launcher");
        Directory.CreateDirectory(launcherDir);
        File.WriteAllText(Path.Combine(launcherDir, ModernClientLayout.ArctiumExeName), "MZ");

        var proxyDir = Path.Combine(root, "Hermes", "linux");
        Directory.CreateDirectory(proxyDir);
        File.WriteAllText(Path.Combine(proxyDir, ModernClientLayout.ProxyExeName), "ELF");

        return (exe, proxyDir);
    }

    // ── The client's own files: duplicates and a readback that throws ─────────────────────────
    // Codex review 2026-08-09, findings 1 and 5.

    /// <summary>A stale second <c>SET realmList</c> line does not survive the write, and the readback
    /// does not report success while it is still there.
    ///
    /// <para>Before this, <c>WtfFile.SetVar</c> rewrote the FIRST match and <c>ReadVar</c> accepted the
    /// FIRST match: a Config.wtf carrying the package address at the top and an old one further down
    /// came out of a launch "written and confirmed" with the old line untouched. If the client honours
    /// the last definition, that is a player on the wrong realm with a green launcher.</para></summary>
    [Fact]
    public void WriteClientRealm_LeavesExactlyOneRealmListLine_WhenTheConfigHadDuplicates()
    {
        var wtf = Path.Combine(_dir, "WTF");
        Directory.CreateDirectory(wtf);
        File.WriteAllText(Path.Combine(wtf, "Config.wtf"), string.Join("\n",
        [
            "SET realmList \"play.stonetavern.app\"",
            "SET locale \"deDE\"",
            "SET realmList \"old.example.invalid\"",
        ]) + "\n");

        var ok = RealmBinding.WriteClientRealm(
            _dir, RealmAddress.Parse("play.example.invalid:3725")!, out var error);

        Assert.True(ok, error);
        var config = File.ReadAllText(Path.Combine(wtf, "Config.wtf"));
        Assert.Equal("SET realmList \"play.example.invalid:3725\"\nSET locale \"deDE\"\n", config);
        Assert.Single(WtfFile.ReadValues(Path.Combine(wtf, "Config.wtf"), "realmList"));
    }

    /// <summary>An ambiguous config that the write could not repair is a refusal with a message that
    /// names both values — never a silent pass on the first line.</summary>
    [Fact]
    public void ReadVar_RefusesAnAmbiguousConfig_AndTheValuesAreStillReadableForTheMessage()
    {
        var path = Path.Combine(_dir, "Config.wtf");
        File.WriteAllText(path,
            "SET realmList \"play.example.invalid\"\nSET realmList \"old.example.invalid\"\n");

        Assert.Null(WtfFile.ReadVar(path, "realmList"));
        Assert.Equal(["play.example.invalid", "old.example.invalid"], WtfFile.ReadValues(path, "realmList"));
    }

    /// <summary>The write lands and the READBACK fails: that must become the same readable refusal as a
    /// failed write, not an exception out of the Play path (finding 5).
    ///
    /// <para>The readback used to sit outside the try, and nothing above <c>ClientService.LaunchAsync</c>
    /// catches: the player got no message and the UI stayed in "launching". Reproduced here with a
    /// write-only <c>realmlist.wtf</c> — <c>File.WriteAllText</c> succeeds on it, <c>File.ReadAllText</c>
    /// throws <c>UnauthorizedAccessException</c>, which is exactly the shape of an antivirus or an ACL
    /// change between the two calls on Windows.</para></summary>
    [Fact]
    public void WriteClientRealm_RefusesReadably_WhenTheReadbackItselfThrows()
    {
        if (OperatingSystem.IsWindows()) return;  // file modes are the POSIX way to force this
        var realmlist = Path.Combine(_dir, "realmlist.wtf");
        File.WriteAllText(realmlist, "set realmlist play.stonetavern.app\n");
        File.SetUnixFileMode(realmlist, UnixFileMode.UserWrite);  // writable, NOT readable

        var ok = RealmBinding.WriteClientRealm(
            _dir, RealmAddress.Parse("play.example.invalid")!, out var error);

        Assert.False(ok);
        Assert.Contains(_dir, error, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error));
        File.SetUnixFileMode(realmlist, UnixFileMode.UserRead | UnixFileMode.UserWrite);  // so Dispose can clean up
    }

    private static void WriteProxyConfigIn(string proxyDir) =>
        File.WriteAllText(Path.Combine(proxyDir, "HermesProxy.config"), """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <appSettings>
            <add key="ServerAddress" value="play.stonetavern.app" />
            <add key="ServerPort" value="3724" />
          </appSettings>
        </configuration>
        """);

    private sealed class CountingProxy : IGameProxy
    {
        public int StartCount;
        public Task<GameProxyResult> StartAndWaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default)
        {
            StartCount++;
            return Task.FromResult(GameProxyResult.Ok(4242));
        }
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class StubWine : IWineHost
    {
        public Task<GameLaunchResult> RunAsync(string exePath, string cwd, IReadOnlyList<string> args) =>
            Task.FromResult(GameLaunchResult.Ok(17));
        public Task<string?> ToWindowsPathAsync(string unixPath) => Task.FromResult<string?>(@"Z:\client");
    }

    private sealed class AlwaysRunning : IGameProcessDetector
    {
        public bool IsGameRunning(string? expectedExePath) => true;
    }

    private sealed class NoDisplay : IDisplayResolution
    {
        public string? Current() => null;
    }
}
