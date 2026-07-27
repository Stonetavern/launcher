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
/// The 1.14.2 start sequence, rebuilt from the one script a character has actually reached the world
/// through. Every assertion here corresponds to a specific silent failure: a client pointed at no
/// proxy sits at the login screen, a client on D3D11 renders the world with no UI at all, a client on
/// the wrong fullscreen resolution shows no window under Wayland, and a launch that reports success
/// when Arctium exits reports success before the game exists.
/// </summary>
public sealed class ModernClientLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "modern-launch-" + Guid.NewGuid().ToString("N"));

    private static Serilog.ILogger Logger() => new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>A complete bundle on disk, in the shape the packaging step produces.</summary>
    private string MakeBundle(bool withArctium = true, bool withProxy = true, bool withConfig = true)
    {
        var clientDir = Path.Combine(_root, "World of Warcraft", "_classic_era_");
        Directory.CreateDirectory(clientDir);
        var exe = Path.Combine(clientDir, "WowClassic.exe");
        File.WriteAllText(exe, "MZ");

        if (withArctium)
        {
            var launcherDir = Path.Combine(_root, "Launcher");
            Directory.CreateDirectory(launcherDir);
            File.WriteAllText(Path.Combine(launcherDir, ModernClientLayout.ArctiumExeName), "MZ");
        }
        if (withProxy)
        {
            var proxyDir = Path.Combine(_root, "Hermes", "linux");
            Directory.CreateDirectory(proxyDir);
            File.WriteAllText(Path.Combine(proxyDir, ModernClientLayout.ProxyExeName), "ELF");
        }
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
        public string? WindowsPath = @"Z:\bundle\World of Warcraft\_classic_era_";
        public string? WinePathRequestedFor;

        public Task<GameLaunchResult> RunAsync(string exePath, string cwd, IReadOnlyList<string> args)
        {
            Runs.Add((exePath, cwd, args));
            return Task.FromResult(StartSucceeds
                ? GameLaunchResult.Ok(4711)
                : GameLaunchResult.Failed("wine refused"));
        }

        public Task<string?> ToWindowsPathAsync(string unixPath)
        {
            WinePathRequestedFor = unixPath;
            return Task.FromResult(WindowsPath);
        }
    }

    private sealed class FakeProxy : IGameProxy
    {
        public bool Ready = true;
        public int StartCount;
        public int StopCount;
        public int RequestedPort;

        public Task<GameProxyResult> StartAndWaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default)
        {
            StartCount++;
            RequestedPort = port;
            return Task.FromResult(Ready
                ? GameProxyResult.Ok(999)
                : GameProxyResult.Failed("the proxy exited before it started listening"));
        }

        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
    }

    /// <summary>Reports the client as running only after <see cref="AppearsAfter"/> polls, the way a
    /// real one does: Arctium patches and launches, and the game process shows up a beat later.</summary>
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

    private sealed class FixedDisplay : IDisplayResolution
    {
        public string? Value = "2560x1440";
        public string? Current() => Value;
    }

    private sealed class FakeSession : IGameSession
    {
        public IGameProxy? Attached;
        public void AttachProxy(IGameProxy proxy) => Attached = proxy;
        public bool HasProxy => Attached is not null;
        public Task<bool> StopProxyIfNoGameAsync(string? clientExePath = null) => Task.FromResult(false);
        public Task MonitorUntilExitAsync(string clientExePath, System.Threading.CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed record Harness(
        ModernClientLauncher Launcher, FakeWine Wine, FakeProxy Proxy, DelayedDetector Detector, FixedDisplay Display);

    private static Harness NewLauncher(
        FakeWine? wine = null, FakeProxy? proxy = null,
        DelayedDetector? detector = null, FixedDisplay? display = null)
    {
        wine ??= new FakeWine();
        proxy ??= new FakeProxy();
        detector ??= new DelayedDetector();
        display ??= new FixedDisplay();
        // A short real window with a short real delay: the deadline is wall-clock (as it must be in
        // production), so an instant delay would spin the CPU for the whole window instead of waiting.
        var launcher = new ModernClientLauncher(
            Logger(), wine, detector, display, _ => proxy,
            clientAppearTimeout: TimeSpan.FromMilliseconds(300),
            delay: _ => Task.Delay(5));
        return new Harness(launcher, wine, proxy, detector, display);
    }

    // ── Layout ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Layout_DerivesEveryPartFromTheClientExe()
    {
        var exe = MakeBundle();
        var layout = ModernClientLayout.Resolve(exe);

        Assert.NotNull(layout);
        Assert.Equal(Path.GetFullPath(_root), layout!.BundleRoot);
        Assert.Equal(Path.Combine(_root, "Launcher", ModernClientLayout.ArctiumExeName), layout.ArctiumExe);
        Assert.Equal(Path.Combine(_root, "Hermes", "linux", ModernClientLayout.ProxyExeName), layout.ProxyExe);
        Assert.Equal(
            Path.Combine(_root, "World of Warcraft", "_classic_era_", "WTF", "Config.wtf"),
            layout.ConfigWtf);
    }

    // ── The sequence ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AHappyLaunch_WritesTheConfig_StartsTheProxy_ThenRunsArctium()
    {
        var exe = MakeBundle();
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        Assert.Equal(1, h.Proxy.StartCount);
        Assert.Equal(ModernClientLauncher.ProxyPort, h.Proxy.RequestedPort);

        var (Exe, Cwd, Args) = Assert.Single(h.Wine.Runs);
        Assert.EndsWith(ModernClientLayout.ArctiumExeName, Exe);
        Assert.Contains("--version=ClassicEra", Args);
        Assert.Contains("--path", Args);
        Assert.Contains(h.Wine.WindowsPath!, Args);
        // Arctium runs from its own folder, and the Windows path it is handed is the CLIENT folder.
        Assert.Equal(Path.Combine(_root, "Launcher"), Cwd);
        Assert.Equal(Path.Combine(_root, "World of Warcraft", "_classic_era_"), h.Wine.WinePathRequestedFor);
    }

    /// <summary>On a confirmed launch the started proxy is handed to the session (not detached), so the
    /// launcher — alive in the tray now — can reap it when the game ends. It is NOT stopped here: the
    /// game is still up.</summary>
    [Fact]
    public async Task AHappyLaunch_HandsTheProxyToTheSession_ForLaterReap()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy();
        var session = new FakeSession();
        var launcher = new ModernClientLauncher(
            Logger(), new FakeWine(), new DelayedDetector(), new FixedDisplay(), _ => proxy,
            session: session,
            clientAppearTimeout: TimeSpan.FromMilliseconds(300), delay: _ => Task.Delay(5));

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        Assert.Same(proxy, session.Attached);   // handed off for later reap
        Assert.Equal(0, proxy.StopCount);        // NOT stopped — the client is still running
    }

    /// <summary>A launch that fails still stops the proxy inline and never hands it to the session —
    /// there is nothing running to reap later.</summary>
    [Fact]
    public async Task AFailedLaunch_StopsTheProxyInline_AndNeverAttachesIt()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy();
        var session = new FakeSession();
        var launcher = new ModernClientLauncher(
            Logger(), new FakeWine { StartSucceeds = false }, new DelayedDetector(), new FixedDisplay(),
            _ => proxy, session: session,
            clientAppearTimeout: TimeSpan.FromMilliseconds(300), delay: _ => Task.Delay(5));

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(1, proxy.StopCount);
        Assert.Null(session.Attached);
    }

    /// <summary>The client exe is never what gets started: the static custom-server build ACCESS_VIOLATEs
    /// under Wine, which is the entire reason Arctium is in this sequence.</summary>
    [Fact]
    public async Task TheClientExeItself_IsNeverStartedDirectly()
    {
        var exe = MakeBundle();
        var h = NewLauncher();

        await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.DoesNotContain(h.Wine.Runs, r => r.Exe.EndsWith("WowClassic.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheConfig_PointsAtTheProxy_PinsD3D12_AndPinsTheRealResolution()
    {
        var exe = MakeBundle();
        var h = NewLauncher();

        await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        var cfg = File.ReadAllLines(ModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.Contains("SET portal \"127.0.0.1:1119\"", cfg);
        Assert.Contains("SET gxApi \"D3D12\"", cfg);
        Assert.Contains("SET gxFullscreenResolution \"2560x1440\"", cfg);
        // D3D11 was there before and had to be REPLACED, not joined by a second line: the client reads
        // one of them and a duplicate key is a coin flip between "works" and "no UI at all".
        Assert.DoesNotContain("SET gxApi \"D3D11\"", cfg);
        Assert.Single(cfg, l => l.StartsWith("SET gxApi ", StringComparison.OrdinalIgnoreCase));
        // The player's own unrelated settings survive untouched.
        Assert.Contains("SET Sound_MusicVolume \"0.4\"", cfg);
        Assert.Contains("SET locale \"enUS\"", cfg);
    }

    [Fact]
    public async Task WithNoReadableResolution_TheResolutionIsLeftAlone_RatherThanGuessed()
    {
        var exe = MakeBundle();
        var h = NewLauncher(display: new FixedDisplay { Value = null });

        await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        var cfg = File.ReadAllLines(ModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.Contains("SET gxApi \"D3D12\"", cfg);
        Assert.DoesNotContain(cfg, l => l.StartsWith("SET gxFullscreenResolution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AMissingConfigFile_IsCreated_NotATripwire()
    {
        var exe = MakeBundle(withConfig: false);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        var cfg = File.ReadAllLines(ModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.Contains("SET portal \"127.0.0.1:1119\"", cfg);
    }

    // ── Refusals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Order matters and is asserted, not assumed: a client started before the proxy listens
    /// reaches the login screen and stops there with no error of any kind.</summary>
    [Fact]
    public async Task WhenTheProxyNeverListens_TheClientIsNeverStarted()
    {
        var exe = MakeBundle();
        var h = NewLauncher(proxy: new FakeProxy { Ready = false });

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Empty(h.Wine.Runs);
    }

    /// <summary>Arctium exits within seconds by design. If its exit were the launch result, this would
    /// report success on a game that never came up.</summary>
    [Fact]
    public async Task WhenTheClientNeverAppears_TheLaunchFails_EvenThoughArctiumStartedFine()
    {
        var exe = MakeBundle();
        var h = NewLauncher(detector: new DelayedDetector { NeverAppears = true });

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Single(h.Wine.Runs);                     // Arctium DID start and DID succeed
        Assert.Equal(1, h.Proxy.StopCount);             // and the proxy was not left behind
    }

    [Fact]
    public async Task WhenArctiumCannotStart_TheProxyIsStoppedAgain()
    {
        var exe = MakeBundle();
        var h = NewLauncher(wine: new FakeWine { StartSucceeds = false });

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(1, h.Proxy.StopCount);
    }

    [Fact]
    public async Task WhenWinepathFails_NothingIsStartedWithAGuessedPath()
    {
        var exe = MakeBundle();
        var h = NewLauncher(wine: new FakeWine { WindowsPath = null });

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Empty(h.Wine.Runs);
        Assert.Equal(1, h.Proxy.StopCount);
    }

    [Theory]
    [InlineData(false, true)]    // no Arctium
    [InlineData(true, false)]    // no proxy binary
    public async Task AnIncompletePackage_IsRefusedBeforeAnythingIsStarted(bool arctium, bool proxy)
    {
        var exe = MakeBundle(withArctium: arctium, withProxy: proxy);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);
        Assert.Empty(h.Wine.Runs);
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

/// <summary>Config.wtf line handling on its own: one grammar, one key per line, and the player's other
/// settings are none of the launcher's business.</summary>
public sealed class WtfConfigWriterTests
{
    [Fact]
    public void AnExistingKeyIsReplacedInPlace_NotAppendedTwice()
    {
        var result = WtfConfigWriter.ApplyToLines(
            ["SET gxApi \"D3D11\"", "SET locale \"enUS\""],
            new Dictionary<string, string> { ["gxApi"] = "D3D12" });

        Assert.Equal(["SET gxApi \"D3D12\"", "SET locale \"enUS\""], result);
    }

    [Fact]
    public void AMissingKeyIsAppended()
    {
        var result = WtfConfigWriter.ApplyToLines(
            ["SET locale \"enUS\""],
            new Dictionary<string, string> { ["portal"] = "127.0.0.1:1119" });

        Assert.Equal(["SET locale \"enUS\"", "SET portal \"127.0.0.1:1119\""], result);
    }

    /// <summary>WoW writes these keys in varying case; a case-sensitive match would append a duplicate
    /// and leave the real setting in place, which reads as "the launcher did nothing".</summary>
    [Fact]
    public void KeyMatchingIsCaseInsensitive()
    {
        var result = WtfConfigWriter.ApplyToLines(
            ["SET GxApi \"D3D11\""],
            new Dictionary<string, string> { ["gxApi"] = "D3D12" });

        Assert.Single(result);
        Assert.Equal("SET gxApi \"D3D12\"", result[0]);
    }

    /// <summary>A prefix must not count as a match: gxApi and gxApiHint are different settings, and
    /// rewriting the wrong line would silently drop the one the client actually reads.</summary>
    [Fact]
    public void ALongerKeyWithTheSamePrefix_IsNotMistakenForTheKey()
    {
        var result = WtfConfigWriter.ApplyToLines(
            ["SET gxApiHint \"whatever\""],
            new Dictionary<string, string> { ["gxApi"] = "D3D12" });

        Assert.Equal(["SET gxApiHint \"whatever\"", "SET gxApi \"D3D12\""], result);
    }

    [Fact]
    public void UnrelatedLinesAreLeftExactlyAsTheyWere()
    {
        var original = new[] { "SET Sound_MusicVolume \"0.4\"", "SET readTOS \"1\"", "", "SET gxApi \"D3D11\"" };

        var result = WtfConfigWriter.ApplyToLines(original, new Dictionary<string, string> { ["gxApi"] = "D3D12" });

        Assert.Equal("SET Sound_MusicVolume \"0.4\"", result[0]);
        Assert.Equal("SET readTOS \"1\"", result[1]);
        Assert.Equal("", result[2]);
    }
}

/// <summary>Reading the active display mode out of real xrandr output.</summary>
public sealed class XrandrParsingTests
{
    private const string Sample = """
        Screen 0: minimum 16 x 16, current 2560 x 1440, maximum 32767 x 32767
        DP-1 connected primary 2560x1440+0+0 (normal left inverted right x axis y axis) 597mm x 336mm
           2560x1440     59.95*+  144.00
           1920x1080     60.00    59.94
        HDMI-1 disconnected (normal left inverted right x axis y axis)
        """;

    [Fact]
    public void TheModeMarkedCurrentIsTheOneReturned() =>
        Assert.Equal("2560x1440", XrandrDisplayResolution.ParseActiveMode(Sample));

    /// <summary>No starred mode means no answer. A guess here maps the game window off-screen under
    /// Wayland and the player sees nothing at all, which is worse than leaving the setting alone.</summary>
    [Fact]
    public void WithNoCurrentMode_NothingIsReturned() =>
        Assert.Null(XrandrDisplayResolution.ParseActiveMode("DP-1 disconnected\n   1920x1080  60.00\n"));

    [Fact]
    public void GarbageIsNotMistakenForAMode() =>
        Assert.Null(XrandrDisplayResolution.ParseActiveMode("some * thing\nnot-a-mode*\n"));
}
