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
        /// <summary>Stand-in for the real thing that can throw mid-launch (a wine host that dies, a
        /// winepath call that faults) — the case the rollback exists for.</summary>
        public bool ThrowOnWinePath;

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
            if (ThrowOnWinePath) throw new InvalidOperationException("wine host died mid-launch");
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

    /// <summary>Write a <c>.build.info</c> beside the bundle's Data folder, the file the client's own
    /// installer leaves behind and the only honest source for "which languages does this copy have".
    /// Shape copied from the real one (build 42597).</summary>
    private void WriteBuildInfo(params string[] textLocales)
    {
        var installRoot = Path.Combine(_root, "World of Warcraft");
        Directory.CreateDirectory(installRoot);
        var tags = string.Join(":", textLocales.Select(l => $"Windows x86_64 EU? geoip-BG? {l} text?"));
        File.WriteAllText(Path.Combine(installRoot, ".build.info"),
            "Branch!STRING:0|Active!DEC:1|Tags!STRING:0|Version!STRING:0|Product!STRING:0\n"
            + $"eu|1|{tags}|1.14.2.42597|wow_classic_era\n");
    }

    private static Harness NewLauncher(
        FakeWine? wine = null, FakeProxy? proxy = null,
        DelayedDetector? detector = null, FixedDisplay? display = null,
        Func<string, PeArch>? peArch = null, IGameSession? session = null,
        Func<string?>? locale = null)
    {
        wine ??= new FakeWine();
        proxy ??= new FakeProxy();
        detector ??= new DelayedDetector();
        display ??= new FixedDisplay();
        // The bundle these tests build is made of placeholder files, not real PE binaries, so the
        // bitness gate is fed a seam. Tests that are ABOUT the gate pass their own.
        peArch ??= _ => PeArch.X64;
        // A short real window with a short real delay: the deadline is wall-clock (as it must be in
        // production), so an instant delay would spin the CPU for the whole window instead of waiting.
        var launcher = new ModernClientLauncher(
            Logger(), wine, detector, display, _ => proxy,
            session: session,
            locale: locale,
            peArch: peArch,
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
        var launcher = NewLauncher(proxy: proxy, session: session).Launcher;

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
        var launcher = NewLauncher(
            wine: new FakeWine { StartSucceeds = false }, proxy: proxy, session: session).Launcher;

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(1, proxy.StopCount);
        Assert.Null(session.Attached);
    }

    // ── Language ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A language the installation carries is written into Config.wtf, so the client comes up
    /// in it. Measured against the real client on 2026-08-03: ten locales ship inside the 1.14.2
    /// package, and switching textLocale is all it takes — nothing is downloaded.</summary>
    [Fact]
    public async Task AnInstalledLanguage_IsWrittenIntoTheClientConfig()
    {
        var exe = MakeBundle();
        WriteBuildInfo("enUS", "deDE", "frFR");
        var launcher = NewLauncher(locale: () => "deDE").Launcher;

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        var config = await File.ReadAllTextAsync(ModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.Contains("SET textLocale \"deDE\"", config, StringComparison.Ordinal);
    }

    /// <summary>The one that matters. A language this copy does NOT carry is not a fallback to English
    /// inside the client — it is ERROR #134 before any window appears, which reads to a player like a
    /// broken install (reproduced with itIT on 2026-08-03). So the launcher downgrades to English
    /// rather than handing the client a language that kills it.</summary>
    [Fact]
    public async Task ALanguageThisClientDoesNotHave_IsDowngraded_NotWritten()
    {
        var exe = MakeBundle();
        WriteBuildInfo("enUS", "deDE");
        var launcher = NewLauncher(locale: () => "itIT").Launcher;

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        var config = await File.ReadAllTextAsync(ModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.Contains("SET textLocale \"enUS\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("itIT", config, StringComparison.Ordinal);
    }

    /// <summary>No <c>.build.info</c> at all (a hand-assembled client) means English, for the same
    /// reason: an unverified language is a crash, and there is nothing here to verify against.</summary>
    [Fact]
    public async Task WithNoInstallationInfo_EnglishIsWritten()
    {
        var exe = MakeBundle();   // deliberately no WriteBuildInfo
        var launcher = NewLauncher(locale: () => "deDE").Launcher;

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        var config = await File.ReadAllTextAsync(ModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.Contains("SET textLocale \"enUS\"", config, StringComparison.Ordinal);
    }

    /// <summary>Spoken audio is left alone. Whether each locale carries speech was never measured, and
    /// an unmeasured audioLocale is the same #134 crash with a different file id.</summary>
    [Fact]
    public async Task TheAudioLanguageIsNeverTouched()
    {
        var exe = MakeBundle();
        WriteBuildInfo("enUS", "deDE");
        var launcher = NewLauncher(locale: () => "deDE").Launcher;

        await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        var config = await File.ReadAllTextAsync(ModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.DoesNotContain("audioLocale", config, StringComparison.Ordinal);
    }

    /// <summary>No language wired (the caller does not care) leaves the file's own setting untouched
    /// — the launcher does not invent a language nobody asked for.</summary>
    [Fact]
    public async Task WithNoLanguageWired_TheConfigKeepsWhateverItHad()
    {
        var exe = MakeBundle();
        WriteBuildInfo("enUS", "deDE");
        var launcher = NewLauncher().Launcher;

        await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        var config = await File.ReadAllTextAsync(ModernClientLayout.Resolve(exe)!.ConfigWtf);
        Assert.DoesNotContain("textLocale", config, StringComparison.Ordinal);
    }

    // ── Rollback and the bitness gate (the Linux hardening Windows already had) ────────────────

    /// <summary>An EXCEPTION between "the proxy is running" and "the session owns it" must still stop the
    /// proxy. Before the finally existed, every failure path stopped it by hand and a throw skipped them
    /// all, leaving HermesProxy on 1119 with nothing pointing at it — the next Play then failed on a busy
    /// port for a reason invisible to the player.</summary>
    [Fact]
    public async Task AThrowAfterTheProxyStarted_StillStopsIt()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy();
        var session = new FakeSession();
        var launcher = NewLauncher(
            wine: new FakeWine { ThrowOnWinePath = true }, proxy: proxy, session: session).Launcher;

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(1, proxy.StartCount);
        Assert.Equal(1, proxy.StopCount);   // rolled back despite nobody catching the throw inline
        Assert.Null(session.Attached);
    }

    /// <summary>A handed-off launch is NOT rolled back: the client is running and the session owns the
    /// reap. Without this the finally would kill the proxy out from under a live game.</summary>
    [Fact]
    public async Task AHandedOffLaunch_IsNeverRolledBack()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy();
        var session = new FakeSession();
        var launcher = NewLauncher(proxy: proxy, session: session).Launcher;

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.True(result.Started);
        Assert.Equal(0, proxy.StopCount);
    }

    /// <summary>A 32-bit client is refused BEFORE anything starts. It is not this package, and letting it
    /// through produces a Wine failure that reads like a broken install instead of a wrong file.</summary>
    [Fact]
    public async Task AWrongArchitectureClient_IsRefusedBeforeTheProxyStarts()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy();
        var launcher = NewLauncher(proxy: proxy, peArch: _ => PeArch.X86).Launcher;

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, proxy.StartCount);
        Assert.Contains("64-bit", result.Error);
    }

    /// <summary>A file we cannot read as a Windows program is a refusal too — "unknown" is not "probably
    /// fine". The Arctium patcher is checked as well as the client; both are handed to Wine.</summary>
    [Fact]
    public async Task AnUnreadablePatcher_IsRefused()
    {
        var exe = MakeBundle();
        var proxy = new FakeProxy();
        var launcher = NewLauncher(
            proxy: proxy,
            peArch: p => p.EndsWith(ModernClientLayout.ArctiumExeName, StringComparison.Ordinal)
                ? PeArch.Unknown
                : PeArch.X64).Launcher;

        var result = await launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, proxy.StartCount);
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

    /// <summary>Fail-closed endpoint (2026-07-27), the Linux counterpart to the Windows and macOS rule:
    /// when the portal cannot be confirmed on disk, the launch is refused instead of starting the client
    /// against whatever the file held before.
    ///
    /// <para>Until this test existed, Linux logged "starting anyway with whatever it already holds" and
    /// went on. A stale portal there means the game comes up looking perfectly healthy and talks to the
    /// WRONG server — the same silent-failure shape as the 1.12 farclip bug, where the client started
    /// fine and rendered a broken world. Nothing crashes, nothing logs red.</para>
    ///
    /// <para>Reproduced the same way as the Windows test: a directory sits where Config.wtf must be, so
    /// both the write and the readback fail.</para></summary>
    [Fact]
    public async Task WhenThePortalCannotBeConfirmed_TheLaunchIsRefused_BeforeAnythingStarts()
    {
        var exe = MakeBundle(withConfig: false);
        var layout = ModernClientLayout.Resolve(exe)!;
        Directory.CreateDirectory(layout.ConfigWtf);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);
        Assert.Empty(h.Wine.Runs);
    }

    /// <summary>The other half of the same rule, and the reason it checks the FILE and not the write:
    /// a config that already carries the right portal must still launch even if rewriting it fails.
    /// Guarding the write instead of the result would have turned a recoverable state into a dead
    /// button — the exact objection the old fail-open comment raised.</summary>
    [Fact]
    public async Task AnAlreadyCorrectPortal_StillLaunches_EvenWhenTheFileIsReadOnly()
    {
        var exe = MakeBundle(withConfig: false);
        var layout = ModernClientLayout.Resolve(exe)!;
        Directory.CreateDirectory(Path.GetDirectoryName(layout.ConfigWtf)!);
        File.WriteAllText(layout.ConfigWtf, "SET portal \"127.0.0.1:1119\"\n");
        var ro = new FileInfo(layout.ConfigWtf) { IsReadOnly = true };
        try
        {
            var h = NewLauncher();

            var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

            Assert.True(result.Started);
        }
        finally
        {
            ro.IsReadOnly = false;
        }
    }

    /// <summary>A stale duplicate portal line stops the launch (Codex review 2026-07-27).
    ///
    /// <para>WtfConfigWriter rewrites the FIRST matching line and leaves later duplicates alone. An
    /// "at least one line matches" readback would therefore pass on a file that still carries the old
    /// address further down — and if the client honours the last definition, the game connects to the
    /// wrong server while the launcher reports success. Which line 1.14.2 actually honours is not
    /// documented, so the check refuses to depend on it.</para></summary>
    [Fact]
    public async Task ASecondStalePortalLine_StopsTheLaunch_RatherThanGamblingOnWhichOneWins()
    {
        var exe = MakeBundle(withConfig: false);
        var layout = ModernClientLayout.Resolve(exe)!;
        Directory.CreateDirectory(Path.GetDirectoryName(layout.ConfigWtf)!);
        // First line is what the writer will rewrite; the second survives and points elsewhere.
        File.WriteAllLines(layout.ConfigWtf,
        [
            "SET portal \"127.0.0.1:1119\"",
            "SET gxApi \"D3D12\"",
            "SET portal \"logon.example.invalid\"",
        ]);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);
        Assert.Empty(h.Wine.Runs);
    }

    /// <summary>A stale portal line written with a TAB instead of a space is caught too (Codex review
    /// round 2, 2026-07-27).
    ///
    /// <para>The matcher used to require the literal string "SET " with an ASCII space, so
    /// <c>SET\tportal "…"</c> was invisible to both the writer and the readback. If the client's own
    /// parser is more permissive — and its grammar is not documented anywhere we can rely on — such a
    /// line could sit beside the canonical one and win. Recognising more lines is the safe direction:
    /// a line we see is one we either rewrite or refuse to start against.</para></summary>
    [Fact]
    public async Task AStalePortalLineWrittenWithATab_IsCaughtToo_NotJustSpaceSeparatedOnes()
    {
        var exe = MakeBundle(withConfig: false);
        var layout = ModernClientLayout.Resolve(exe)!;
        Directory.CreateDirectory(Path.GetDirectoryName(layout.ConfigWtf)!);
        File.WriteAllLines(layout.ConfigWtf,
        [
            "SET portal \"127.0.0.1:1119\"",
            "SET\tportal \"logon.example.invalid\"",
        ]);
        var h = NewLauncher();

        var result = await h.Launcher.LaunchAsync(exe, Path.GetDirectoryName(exe)!);

        Assert.False(result.Started);
        Assert.Equal(0, h.Proxy.StartCount);
    }

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
