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
/// The macOS 1.14.2 start sequence, mirrored from Play-Stonetavern-FreeWine.command: native HermesProxy,
/// then the pre-patched WowClassic_ForCustomServers.exe under GPTK-Wine, directly (no Arctium). Every
/// assertion maps to a specific silent failure — a client pointed at no proxy sits at the login screen, a
/// client on D3D11 renders the world with no UI, a launch against the wrong proxy endpoint connects to the
/// wrong place, and Arctium (used on Linux) hangs under GPTK-Wine so the exe must be started directly.
/// </summary>
public sealed class MacModernClientLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mac-modern-launch-" + Guid.NewGuid().ToString("N"));

    private static Serilog.ILogger Logger() => new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>A complete macOS bundle on disk, in the shape the packaging step produces.</summary>
    private string MakeBundle(
        bool withProxy = true, bool withCustomServer = true, bool withConfig = true,
        string proxyServerAddress = "play.stonetavern.app")
    {
        var clientDir = Path.Combine(_root, "World of Warcraft", "_classic_era_");
        Directory.CreateDirectory(clientDir);
        var exe = Path.Combine(clientDir, "WowClassic.exe");
        File.WriteAllText(exe, "MZ");
        if (withCustomServer)
            File.WriteAllText(Path.Combine(clientDir, MacModernClientLayout.CustomServerExeName), "MZ");

        var proxyDir = Path.Combine(_root, "Hermes");
        Directory.CreateDirectory(proxyDir);
        if (withProxy)
            File.WriteAllText(Path.Combine(proxyDir, MacModernClientLayout.ProxyExeName), "MACHO");
        // HermesProxy.config beside the proxy — the fail-closed endpoint check reads its ServerAddress.
        File.WriteAllText(Path.Combine(proxyDir, "HermesProxy.config"),
            "<configuration><appSettings>" +
            $"<add key=\"ServerAddress\" value=\"{proxyServerAddress}\" />" +
            "</appSettings></configuration>");

        if (withConfig)
        {
            var wtf = Path.Combine(clientDir, "WTF");
            Directory.CreateDirectory(wtf);
            File.WriteAllLines(Path.Combine(wtf, "Config.wtf"),
                ["SET locale \"enUS\"", "SET gxApi \"D3D11\"", "SET Sound_MusicVolume \"0.4\""]);
        }
        return exe;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Doubles ───────────────────────────────────────────────────────────────────────────────

    private sealed class FakeWine : IWineHost
    {
        public readonly List<(string Exe, string Cwd, IReadOnlyList<string> Args)> Runs = [];
        public bool StartSucceeds = true;

        public Task<GameLaunchResult> RunAsync(string exePath, string cwd, IReadOnlyList<string> args)
        {
            Runs.Add((exePath, cwd, args));
            return Task.FromResult(StartSucceeds
                ? GameLaunchResult.Ok(4711)
                : GameLaunchResult.Failed("wine refused"));
        }

        public Task<string?> ToWindowsPathAsync(string unixPath) => Task.FromResult<string?>(null);
    }

    private sealed class FakeProxy : IGameProxy
    {
        public bool Ready = true;
        public int StartCount;
        public int StopCount;

        public Task<GameProxyResult> StartAndWaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default)
        {
            StartCount++;
            return Task.FromResult(Ready
                ? GameProxyResult.Ok(999)
                : GameProxyResult.Failed("the proxy exited before it started listening"));
        }

        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
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
        public int BoundPid = -1;
        public void AttachProxy(IGameProxy proxy) => Attached = proxy;
        public bool HasProxy => Attached is not null;
        public void BindClientProcess(int pid) => BoundPid = pid;
        public Task<bool> StopProxyIfNoGameAsync(string? clientExePath = null) => Task.FromResult(false);
        public Task MonitorUntilExitAsync(string clientExePath, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record Harness(
        MacModernClientLauncher Launcher, FakeWine Wine, FakeProxy Proxy, DelayedDetector Detector, FakeSession Session);

    private static Harness NewLauncher(
        FakeWine? wine = null, FakeProxy? proxy = null, DelayedDetector? detector = null, FakeSession? session = null)
    {
        wine ??= new FakeWine();
        proxy ??= new FakeProxy();
        detector ??= new DelayedDetector();
        session ??= new FakeSession();
        var launcher = new MacModernClientLauncher(
            Logger(), wine, detector, _ => proxy, session,
            clientAppearTimeout: TimeSpan.FromMilliseconds(300),
            delay: _ => Task.Delay(5));
        return new Harness(launcher, wine, proxy, detector, session);
    }

    // ── Layout ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Layout_DerivesEveryPartFromTheClientExe()
    {
        var exe = MakeBundle();
        var layout = MacModernClientLayout.Resolve(exe)!;

        Assert.Equal(_root, layout.BundleRoot);
        Assert.Equal(Path.Combine(_root, "Hermes", "HermesProxy"), layout.ProxyExe);
        Assert.Equal(
            Path.Combine(_root, "World of Warcraft", "_classic_era_", "WowClassic_ForCustomServers.exe"),
            layout.CustomServerExe);
        Assert.EndsWith(Path.Combine("_classic_era_", "WTF", "Config.wtf"), layout.ConfigWtf);
    }

    [Fact]
    public void Layout_ProxyIsUnderHermes_NotHermesLinux()
    {
        // Mutation guard vs a copy-paste of the Linux layout (Hermes/linux/HermesProxy) — the native macOS
        // proxy sits directly under Hermes/.
        var exe = MakeBundle();
        var layout = MacModernClientLayout.Resolve(exe)!;
        Assert.DoesNotContain(Path.Combine("Hermes", "linux"), layout.ProxyExe);
        Assert.Equal(Path.Combine(_root, "Hermes"), layout.ProxyDir);
    }

    [Fact]
    public void Layout_NullWhenPathTooShallowForABundle()
    {
        // A client exe without three parent levels (root/World of Warcraft/_classic_era_) cannot be a
        // bundle: Resolve returns null and the caller turns that into a "package incomplete" sentence.
        Assert.Null(MacModernClientLayout.Resolve("/WowClassic.exe"));
    }

    // ── Launch ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Launch_HappyPath_ProxyThenClientUnderWine_ThenHandedToSession()
    {
        var exe = MakeBundle();
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        Assert.Equal(1, h.Proxy.StartCount);
        Assert.Same(h.Proxy, h.Session.Attached);   // proxy handed to the session (reaped on game exit)
        Assert.Equal(0, h.Proxy.StopCount);          // not rolled back on the happy path
    }

    [Fact]
    public async Task Launch_StartsTheCustomServerExe_Directly_NotArctium_NotRetailExe()
    {
        var exe = MakeBundle();
        var h = NewLauncher();

        await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        var (Exe, Cwd, Args) = Assert.Single(h.Wine.Runs);
        Assert.EndsWith("WowClassic_ForCustomServers.exe", Exe);   // the pre-patched exe…
        Assert.DoesNotContain("Arctium", Exe);                     // …not Arctium…
        Assert.Empty(Args);                                        // …started directly, no patch args
        Assert.Equal(Path.Combine(_root, "World of Warcraft", "_classic_era_"), Cwd);
    }

    [Fact]
    public async Task Launch_WritesPortalAndGxApiD3D12()
    {
        var exe = MakeBundle();
        var h = NewLauncher();

        await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        var cfg = File.ReadAllLines(MacModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.Contains("SET portal \"127.0.0.1:1119\"", cfg);
        Assert.Contains("SET gxApi \"D3D12\"", cfg);   // Wine renderer needs D3D12→D3DMetal, not the D3D11 it had
    }

    [Fact]
    public async Task Launch_FailsAndDoesNotStartClient_WhenProxyNeverListens()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy { Ready = false };
        var h = NewLauncher(proxy: proxy);

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Empty(h.Wine.Runs);                 // the client is never started without a listening proxy
        Assert.Null(h.Session.Attached);           // nothing handed off
    }

    [Fact]
    public async Task Launch_RollsBackProxy_WhenClientNeverAppears()
    {
        var exe = MakeBundle();
        var detector = new DelayedDetector { NeverAppears = true };
        var proxy = new FakeProxy();
        var h = NewLauncher(proxy: proxy, detector: detector);

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(1, proxy.StopCount);          // the proxy the session never took ownership of is stopped
        Assert.Null(h.Session.Attached);
    }

    [Fact]
    public async Task Launch_RefusesWrongProxyEndpoint_BeforeStartingAnything()
    {
        var exe = MakeBundle(proxyServerAddress: "evil.example.com");
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);       // fail-closed: proxy never even started
        Assert.Empty(h.Wine.Runs);
    }

    [Fact]
    public async Task Launch_FailsWhenCustomServerExeMissing()
    {
        var exe = MakeBundle(withCustomServer: false);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Contains("WowClassic_ForCustomServers.exe", result.Error);
        Assert.Equal(0, h.Proxy.StartCount);
    }

    [Fact]
    public async Task Launch_FailsWhenProxyBinaryMissing()
    {
        var exe = MakeBundle(withProxy: false);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Contains("HermesProxy", result.Error);
    }

    // ── Detector ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Detector_ReportsRunning_WhenTheScanMatches()
    {
        var hits = new MacGameProcessDetector(Logger(), matches: p => p == MacGameProcessDetector.ClientPattern);
        Assert.True(hits.IsGameRunning("/anything/ignored"));

        var misses = new MacGameProcessDetector(Logger(), matches: _ => false);
        Assert.False(misses.IsGameRunning(null));
    }

    [Fact]
    public void Detector_ScansForWowClassic_Pattern()
    {
        string? asked = null;
        var d = new MacGameProcessDetector(Logger(), matches: p => { asked = p; return false; });
        d.IsGameRunning(null);
        Assert.Equal("WowClassic", asked);
    }
}
