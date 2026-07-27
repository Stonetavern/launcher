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
/// The native-Windows 1.14.2 start sequence, rebuilt from <c>Play Stonetavern.cmd</c> (the one path a
/// Windows character has reached the world through): JimsProxy first and proven listening, then the
/// custom-server client started DIRECTLY — no Wine, no winepath, no Arctium. Every assertion maps to a
/// specific silent failure: a client started before the proxy binds sits at the login screen; the client
/// exe that actually reaches a custom server is <c>WowClassic_ForCustomServers.exe</c>, not
/// <c>WowClassic.exe</c>; and a start call returning is not proof the game came up.
/// </summary>
public sealed class WindowsModernClientLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "win-modern-launch-" + Guid.NewGuid().ToString("N"));

    private static Serilog.ILogger Logger() => new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>A complete Windows bundle on disk, in the shape the packaging step produces.</summary>
    private const string ExpectedHost = "play.stonetavern.app";

    private string MakeBundle(bool withProxy = true, bool withCustomServer = true, bool withConfig = true,
        bool withProxyConfig = true, string proxyServerAddress = ExpectedHost)
    {
        var clientDir = Path.Combine(_root, "World of Warcraft", "_classic_era_");
        Directory.CreateDirectory(clientDir);
        var exe = Path.Combine(clientDir, "WowClassic.exe");
        File.WriteAllText(exe, "MZ");

        if (withCustomServer)
            File.WriteAllText(Path.Combine(clientDir, WindowsModernClientLayout.CustomServerExeName), "MZ");
        if (withProxy)
        {
            var proxyDir = Path.Combine(_root, "Hermes");
            Directory.CreateDirectory(proxyDir);
            File.WriteAllText(Path.Combine(proxyDir, WindowsModernClientLayout.ProxyExeName), "MZ");
        }
        if (withProxyConfig)
        {
            var proxyDir = Path.Combine(_root, "Hermes");
            Directory.CreateDirectory(proxyDir);
            File.WriteAllText(Path.Combine(proxyDir, "HermesProxy.config"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><appSettings>\n" +
                $"  <add key=\"ServerAddress\" value=\"{proxyServerAddress}\" />\n" +
                "  <add key=\"BNetPort\" value=\"1119\" />\n</appSettings></configuration>\n");
        }
        if (withConfig)
        {
            var wtf = Path.Combine(clientDir, "WTF");
            Directory.CreateDirectory(wtf);
            File.WriteAllLines(Path.Combine(wtf, "Config.wtf"),
                ["SET gxApi \"D3D12\"", "SET Sound_MusicVolume \"0.4\""]);
        }
        return exe;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Doubles ───────────────────────────────────────────────────────────────────────────────

    private sealed class FakeNativeStarter : IGameLauncher
    {
        public readonly List<(string Exe, string Cwd)> Runs = [];
        public bool StartSucceeds = true;

        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
        {
            Runs.Add((exePath, workingDirectory));
            return Task.FromResult(StartSucceeds
                ? GameLaunchResult.Ok(4711)
                : GameLaunchResult.Failed("native start refused"));
        }
    }

    private sealed class FakeProxy : IGameProxy
    {
        public bool Ready = true;
        public bool StillListening = true;   // the pre-client re-check (Finding 1)
        public int StartCount;
        public int StopCount;
        public int RequestedPort;

        public Task<GameProxyResult> StartAndWaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default)
        {
            StartCount++;
            RequestedPort = port;
            return Task.FromResult(Ready ? GameProxyResult.Ok(999) : GameProxyResult.Failed("proxy exited before listening"));
        }

        public Task StopAsync() { StopCount++; return Task.CompletedTask; }

        public Task<bool> VerifyStillListeningAsync(int port, CancellationToken ct = default) =>
            Task.FromResult(StillListening);
    }

    private sealed class DelayedDetector : IGameProcessDetector
    {
        public int AppearsAfter = 2;
        public int Polls;
        public bool NeverAppears;

        public bool IsGameRunning(string? expectedExePath)
        {
            Polls++;
            return !NeverAppears && Polls > AppearsAfter;
        }
    }

    private sealed class FakeSession : IGameSession
    {
        public IGameProxy? Attached;
        public int? BoundPid;
        public void AttachProxy(IGameProxy proxy) => Attached = proxy;
        public void BindClientProcess(int pid) => BoundPid = pid;
        public bool HasProxy => Attached is not null;
        public Task<bool> StopProxyIfNoGameAsync(string? clientExePath = null) => Task.FromResult(false);
        public Task MonitorUntilExitAsync(string clientExePath, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record Harness(
        WindowsModernClientLauncher Launcher, FakeNativeStarter Native, FakeProxy Proxy, DelayedDetector Detector);

    private static Harness NewLauncher(
        FakeNativeStarter? native = null, FakeProxy? proxy = null,
        DelayedDetector? detector = null, FakeSession? session = null,
        Func<string, PeArch>? peArch = null, Func<int, bool>? isPidAlive = null)
    {
        native ??= new FakeNativeStarter();
        proxy ??= new FakeProxy();
        detector ??= new DelayedDetector();
        // A session is REQUIRED now (no reap owner = no successful launch), so always supply one.
        // The test bundles write "MZ" stubs, not real PE binaries — inject an arch reader that reports
        // the required x64 so the fail-closed bitness check passes for the happy paths (a dedicated test
        // drives the mismatch). isPidAlive defaults to "the started client is up" so the PID-based appear
        // is deterministic; the never-appears test overrides it to false.
        var launcher = new WindowsModernClientLauncher(
            Logger(), native, detector, _ => proxy, session: session ?? new FakeSession(),
            peArch: peArch ?? (_ => PeArch.X64),
            isPidAlive: isPidAlive ?? (_ => true),
            clientAppearTimeout: TimeSpan.FromMilliseconds(300), delay: _ => Task.Delay(5));
        return new Harness(launcher, native, proxy, detector);
    }

    // ── Layout ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Layout_DerivesEveryPartFromTheClientExe()
    {
        var exe = MakeBundle();
        var layout = WindowsModernClientLayout.Resolve(exe);

        Assert.NotNull(layout);
        Assert.Equal(Path.GetFullPath(_root), layout!.BundleRoot);
        Assert.Equal(Path.Combine(_root, "Hermes", WindowsModernClientLayout.ProxyExeName), layout.ProxyExe);
        Assert.Equal(
            Path.Combine(_root, "World of Warcraft", "_classic_era_", WindowsModernClientLayout.CustomServerExeName),
            layout.CustomServerExe);
        Assert.Equal(
            Path.Combine(_root, "World of Warcraft", "_classic_era_", "WTF", "Config.wtf"),
            layout.ConfigWtf);
    }

    // ── The sequence ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AHappyLaunch_StartsTheProxy_ThenStartsTheCustomServerExeNatively()
    {
        var exe = MakeBundle();
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        Assert.Equal(1, h.Proxy.StartCount);
        Assert.Equal(WindowsModernClientLauncher.ProxyPort, h.Proxy.RequestedPort);

        var (Exe, Cwd) = Assert.Single(h.Native.Runs);
        // The custom-server exe is what gets started, from the client directory.
        Assert.EndsWith(WindowsModernClientLayout.CustomServerExeName, Exe);
        Assert.Equal(Path.Combine(_root, "World of Warcraft", "_classic_era_"), Cwd);
    }

    /// <summary>The retail-named WowClassic.exe is never the exe started: on a custom server that is the
    /// wrong binary (the ForCustomServers build is the one that connects). This mirrors the Linux launcher
    /// never starting the client exe directly, for the same reason on the other side.</summary>
    [Fact]
    public async Task TheRetailClientExe_IsNeverStartedDirectly()
    {
        var exe = MakeBundle();
        var h = NewLauncher();

        await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.DoesNotContain(h.Native.Runs, r =>
            Path.GetFileName(r.Exe).Equals("WowClassic.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AHappyLaunch_HandsTheProxyToTheSession_ForLaterReap()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy();
        var session = new FakeSession();
        var h = NewLauncher(proxy: proxy, session: session);

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        Assert.Same(proxy, session.Attached);   // handed off for later reap
        Assert.Equal(0, proxy.StopCount);        // NOT stopped — the client is still running
    }

    [Fact]
    public async Task AFailedNativeStart_StopsTheProxyInline_AndNeverAttachesIt()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy();
        var session = new FakeSession();
        var h = NewLauncher(native: new FakeNativeStarter { StartSucceeds = false }, proxy: proxy, session: session);

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(1, proxy.StopCount);
        Assert.Null(session.Attached);
    }

    /// <summary>The config is pointed at the local proxy (portal), but — unlike the Linux path — gxApi is
    /// left exactly as the shipped bundle has it: the D3D12/Wayland workarounds are Wine-only and forcing
    /// them on a native client is both unnecessary and a risk of overriding a working setting.</summary>
    [Fact]
    public async Task TheConfig_PointsAtTheProxy_AndDoesNotTouchGxApi()
    {
        var exe = MakeBundle();
        var h = NewLauncher();

        await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        var cfg = File.ReadAllLines(WindowsModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.Contains("SET portal \"127.0.0.1:1119\"", cfg);
        // gxApi is neither forced nor duplicated: the one the bundle shipped stands.
        Assert.Single(cfg, l => l.StartsWith("SET gxApi ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("SET gxApi \"D3D12\"", cfg);
        Assert.Contains("SET Sound_MusicVolume \"0.4\"", cfg);
    }

    [Fact]
    public async Task AMissingConfigFile_IsCreated_NotATripwire()
    {
        var exe = MakeBundle(withConfig: false);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        var cfg = File.ReadAllLines(WindowsModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.Contains("SET portal \"127.0.0.1:1119\"", cfg);
    }

    // ── Refusals ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenTheProxyNeverListens_TheClientIsNeverStarted()
    {
        var exe = MakeBundle();
        var h = NewLauncher(proxy: new FakeProxy { Ready = false });

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Empty(h.Native.Runs);
    }

    [Fact]
    public async Task WhenTheClientNeverAppears_TheLaunchFails_AndTheProxyIsStopped()
    {
        var exe = MakeBundle();
        // Neither the started PID nor the name-scan ever reports the client up.
        var h = NewLauncher(detector: new DelayedDetector { NeverAppears = true }, isPidAlive: _ => false);

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Single(h.Native.Runs);        // the native start DID run
        Assert.Equal(1, h.Proxy.StopCount);  // and the proxy was not left behind (rollback)
    }

    /// <summary>PID is authoritative for "did the client come up": when the started PID is dead, a
    /// same-named FOREIGN process (name-scan says "running") must NOT count as our client appearing —
    /// otherwise the launch would succeed against a client that is not ours (Codex review).</summary>
    [Fact]
    public async Task WithADeadStartedPid_ANameScanMatchDoesNotCountAsTheClientAppearing()
    {
        var exe = MakeBundle();
        // Our started PID is dead, but a same-named process makes the name-scan report "running".
        var h = NewLauncher(isPidAlive: _ => false, detector: new DelayedDetector { AppearsAfter = 0 });

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);        // our specific client is dead → not appeared
        Assert.Equal(1, h.Proxy.StopCount);  // rolled back
    }

    // ── Hardening round 2 (Codex re-verify): required session, endpoint config, re-check ─────────

    /// <summary>No reap owner means the proxy would never be reaped → orphan. A null session is a
    /// programming error, refused at construction, not a supported "launch without an owner" mode.</summary>
    [Fact]
    public void ConstructingWithoutASession_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowsModernClientLauncher(
            Logger(), new FakeNativeStarter(), new DelayedDetector(), _ => new FakeProxy(), session: null!));
    }

    /// <summary>Fail-closed proxy endpoint: a proxy config with no ServerAddress is refused before the
    /// proxy starts — otherwise the proxy would relay to nowhere (or the wrong place) and the client would
    /// "successfully" connect to nothing.</summary>
    [Fact]
    public async Task AMissingProxyEndpointConfig_IsRefused_BeforeTheProxyStarts()
    {
        var exe = MakeBundle(withProxyConfig: false);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);
        Assert.Empty(h.Native.Runs);
    }

    /// <summary>And a proxy pointed at the WRONG realm is refused too — the same silent "start against the
    /// wrong endpoint" the client-side portal readback guards, closed on the proxy side.</summary>
    [Fact]
    public async Task AProxyPointedAtTheWrongRealm_IsRefused_BeforeTheProxyStarts()
    {
        var exe = MakeBundle(proxyServerAddress: "evil.example.com");
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);
        Assert.Empty(h.Native.Runs);
    }

    /// <summary>The pre-client ownership re-check (Finding 1): if the proxy no longer owns the port right
    /// before the client would start, the launch is rolled back — the proxy is stopped and the client is
    /// never started against a listener the launcher no longer controls.</summary>
    [Fact]
    public async Task WhenTheProxyLosesThePortBeforeClientStart_TheLaunchRollsBack()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy { StillListening = false };   // re-check fails after a ready start
        var h = NewLauncher(proxy: proxy);

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(1, proxy.StartCount);   // it did start
        Assert.Empty(h.Native.Runs);         // but the client was never started
        Assert.Equal(1, proxy.StopCount);    // and the proxy was rolled back
    }

    // ── Hardening round 1 (Codex review): ownership, fail-closed validation, rollback ────────────

    /// <summary>The reap must be bound to the CONCRETE client PID (ownership), not a name-scan a foreign
    /// same-named process could satisfy. On a confirmed launch the started process's PID is bound to the
    /// session before the proxy is handed over.</summary>
    [Fact]
    public async Task AHappyLaunch_BindsTheStartedClientPid_ToTheSession()
    {
        var exe = MakeBundle();
        var session = new FakeSession();
        var native = new FakeNativeStarter();   // returns Ok(4711)
        var h = NewLauncher(native: native, session: session);

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        Assert.Equal(4711, session.BoundPid);   // the exact process the reap will track
        Assert.Equal(4711, result.ProcessId);
    }

    /// <summary>Fail-closed bitness: a client exe of the wrong architecture is refused BEFORE the proxy
    /// or client is started — a 32-bit exe for the 64-bit build is the wrong client and would fail in a
    /// way that looks like something else.</summary>
    [Fact]
    public async Task AWrongArchitectureClient_IsRefused_BeforeAnythingStarts()
    {
        var exe = MakeBundle();
        var h = NewLauncher(peArch: _ => PeArch.X86);

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);
        Assert.Empty(h.Native.Runs);
        Assert.NotNull(result.Error);
    }

    /// <summary>Fail-closed endpoint: when the portal cannot be confirmed on disk after the write, the
    /// launch is refused rather than starting the client against an unverified (possibly wrong) endpoint.
    /// Reproduced by making Config.wtf unwritable — a directory sits where the file must be.</summary>
    [Fact]
    public async Task WhenThePortalCannotBeConfirmed_TheLaunchIsRefused_BeforeAnythingStarts()
    {
        var exe = MakeBundle(withConfig: false);
        var layout = WindowsModernClientLayout.Resolve(exe)!;
        // A directory named "Config.wtf" makes the write (and the readback) fail.
        Directory.CreateDirectory(layout.ConfigWtf);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);
        Assert.Empty(h.Native.Runs);
    }

    /// <summary>Guaranteed rollback on an unexpected exception mid-flight: if the native start throws
    /// after the proxy is owned, the proxy is still stopped (no orphan on 1119) and the session never
    /// takes ownership.</summary>
    [Fact]
    public async Task AnExceptionAfterTheProxyStarts_StillStopsTheProxy_AndDoesNotHandOff()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy();
        var session = new FakeSession();
        var throwingStarter = new ThrowingNativeStarter();
        var launcher = new WindowsModernClientLauncher(
            Logger(), throwingStarter, new DelayedDetector(), _ => proxy, session: session,
            peArch: _ => PeArch.X64, isPidAlive: _ => true,
            clientAppearTimeout: TimeSpan.FromMilliseconds(300), delay: _ => Task.Delay(5));

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(1, proxy.StartCount);
        Assert.Equal(1, proxy.StopCount);        // rolled back
        Assert.Null(session.Attached);           // never handed off
        Assert.Null(session.BoundPid);
    }

    private sealed class ThrowingNativeStarter : IGameLauncher
    {
        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory) =>
            throw new InvalidOperationException("boom mid-launch");
    }

    [Theory]
    [InlineData(false, true)]    // no proxy binary
    [InlineData(true, false)]    // no custom-server exe
    public async Task AnIncompletePackage_IsRefusedBeforeAnythingIsStarted(bool proxy, bool customServer)
    {
        var exe = MakeBundle(withProxy: proxy, withCustomServer: customServer);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);
        Assert.Empty(h.Native.Runs);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AMissingClientExe_IsRefused()
    {
        var h = NewLauncher();
        var result = await h.Launcher.LaunchAsync(Path.Combine(_root, "nope", "WowClassic.exe"), _root);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);
    }
}

/// <summary>The Windows launch router picks the path off the resolved exe's own name, exactly as the
/// Linux one does: the modern build (WowClassic.exe) goes through the native proxy+client sequence, the
/// legacy build (WoW.exe) through the plain native start.</summary>
public sealed class WindowsGameLauncherRouterTests
{
    private static Serilog.ILogger Logger() => new Serilog.LoggerConfiguration().CreateLogger();

    private sealed class TaggedLauncher(string tag) : IGameLauncher
    {
        private readonly string _tag = tag;
        public string? SawExe;

        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
        {
            SawExe = exePath;
            return Task.FromResult(GameLaunchResult.Ok(1));
        }
        public string Tag => _tag;
    }

    // The router keys off Path.GetFileName, which splits on the RUNNING OS's separator; on Windows (the
    // only OS this router runs on) that is the backslash. Build the paths with Path.Combine so the test
    // exercises the same file-name extraction on the Linux test host.

    [Fact]
    public async Task TheModernBuild_GoesToTheModernLauncher()
    {
        var legacy = new TaggedLauncher("legacy");
        var modern = new TaggedLauncher("modern");
        var router = new WindowsGameLauncherRouter(legacy, modern, Logger());
        var exe = Path.Combine("wow", "_classic_era_", "WowClassic.exe");

        await router.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.Equal(exe, modern.SawExe);
        Assert.Null(legacy.SawExe);
    }

    [Fact]
    public async Task TheLegacyBuild_GoesToThePlainNativeStart()
    {
        var legacy = new TaggedLauncher("legacy");
        var modern = new TaggedLauncher("modern");
        var router = new WindowsGameLauncherRouter(legacy, modern, Logger());
        var exe = Path.Combine("wow", "WoW.exe");

        await router.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.Equal(exe, legacy.SawExe);
        Assert.Null(modern.SawExe);
    }
}
