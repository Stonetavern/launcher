namespace WowLauncher.Infrastructure;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using WowLauncher.Views;

public static class DependencyInjection
{
    public static IHost BuildHost(string[] args)
    {
        // WP3: one central path resolver (Windows = next-to-exe byte-gleich, Linux = XDG). Built here
        // so the Serilog sink below and the DI graph share the exact same instance. Create the dirs
        // up front so the log sink and config writer never race a missing directory.
        IAppPaths paths = AppPaths.ForCurrentOs();
        paths.EnsureDirectories();

        return Host.CreateDefaultBuilder(args)
            .UseSerilog((ctx, lc) => lc
                .MinimumLevel.Debug()
                // Ohne diese Overrides ertraenkt Microsoft.Extensions.Http das Log in seinem
                // eigenen Handler-Housekeeping: im Windows-Lauf vom 2026-07-12 waren 222 von 365
                // Zeilen "HttpMessageHandler cleanup cycle" im 10-Sekunden-Takt. Das Log war als
                // Diagnose-Instrument wertlos und waechst, solange der Launcher offen ist.
                // Unsere Services loggen ueber Serilog.ILogger direkt (kein Microsoft-SourceContext)
                // und bleiben davon unberuehrt.
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
                .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
                .WriteTo.File(System.IO.Path.Combine(paths.LogDir, "launcher.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 16 * 1024 * 1024, rollOnFileSizeLimit: true,
                    restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug))
            .ConfigureServices(services =>
            {
                // WP3: the shared path resolver + config service that reads/writes at the resolved
                // location. Windows keeps writing next to the exe (byte-gleich); Linux uses XDG and
                // migrates a legacy next-to-binary config once (ConfigService.EnsureMigrated).
                services.AddSingleton<IAppPaths>(paths);
                services.AddSingleton<IConfigService>(sp => new ConfigService(sp.GetRequiredService<IAppPaths>()));
                // The window close button preference (Ask / Background / Quit). A thin seam over the
                // config so the close flow does not depend on the whole config surface.
                services.AddSingleton<IClosePreferenceService>(sp => new ClosePreferenceService(
                    sp.GetRequiredService<IConfigService>()));
                // Lets a ViewModel hide the shell into the tray / bring it back without referencing a
                // window type. App wires the real actions once it has built the window (SetUpTrayIcon).
                services.AddSingleton<IShellWindowController, ShellWindowController>();
                // Owns a launched game's out-of-process tail: the modern proxy + the wait for the client
                // to quit so it can be reaped (never while the game is still up). Needs the running-game
                // detector, so it is registered after the OS branch below fills IGameProcessDetector —
                // resolution is lazy, so registration order does not matter here.
                services.AddSingleton<IGameSession>(sp => new GameSession(
                    sp.GetRequiredService<IGameProcessDetector>(), sp.GetRequiredService<Serilog.ILogger>()));
                // First-install folder picker (WP: player-chosen install root). Avalonia-backed, so it
                // must resolve TopLevel/StorageProvider lazily at call time (no MainWindow exists yet
                // when the DI graph is built) — see AvaloniaFolderPickerService's own headless guard.
                services.AddSingleton<IFolderPickerService>(sp => new AvaloniaFolderPickerService(
                    sp.GetRequiredService<Serilog.ILogger>()));
                // Opt-in Linux desktop integration (menu entry + taskbar icon). ForCurrentOs hands a
                // real writer on Linux and an inert stub elsewhere, so Settings can bind the button
                // unconditionally and it only shows where IsSupported is true.
                services.AddSingleton<IDesktopIntegrationService>(sp => DesktopIntegration.ForCurrentOs(
                    sp.GetRequiredService<Serilog.ILogger>()));
                // Automatic one-time AppImage first-run setup (owner UX 2026-07-24): on the first start
                // FROM an .AppImage, relocate into ~/Applications, drop a trusted desktop shortcut and
                // register the menu entry, then never again. Inert off an AppImage / on non-Linux.
                services.AddSingleton<IFirstRunSetup>(sp => FirstRunSetup.ForCurrentOs(
                    sp.GetRequiredService<IConfigService>(), sp.GetRequiredService<Serilog.ILogger>()));

                // Platform seams (WP1): client-start, install discovery, running-game detection, and
                // self-update swap differ per OS. Windows = the byte-for-byte behaviour that ships
                // live today; Linux = neutral/stub impls that WP2/WP4/WP7 flesh out. Codex F6b: an
                // explicit third branch — macOS (and any other host) gets platform-NEUTRAL stubs, not
                // the Linux impls (no /proc scan, no "Linux" wording); macOS gets its own impls later.
                if (OperatingSystem.IsWindows())
                {
                    // Native Windows launch router (parallel to LinuxGameLauncherRouter): 1.12.1 keeps
                    // the plain, byte-for-byte native start; 1.14.2 goes through the native proxy+client
                    // sequence — JimsProxy.exe (not HermesProxy-linux), the custom-server client started
                    // directly (no Arctium, which is only needed under Wine). The modern path hands the
                    // live proxy to IGameSession so the launcher (alive in the tray after Play) reaps it
                    // when the game ends, never before — the same contract the Linux modern path upholds.
                    // Before this, the Windows branch registered only the plain native launcher, which
                    // would have started the 1.14.2 client with NO proxy: it would reach the login screen
                    // and stop there with no error.
                    services.AddSingleton<IGameLauncher>(sp =>
                    {
                        var logger = sp.GetRequiredService<Serilog.ILogger>();
                        var shareDir = sp.GetRequiredService<IAppPaths>().ShareDir;
                        var nativeStarter = new WindowsGameLauncher(logger);
                        var detector = sp.GetRequiredService<IGameProcessDetector>();
                        var modern = new WindowsModernClientLauncher(
                            logger, nativeStarter, detector,
                            layout => new JimsProxyRunner(
                                logger, layout.ProxyExe, [], Path.Combine(shareDir, "jims-proxy.pid")),
                            sp.GetRequiredService<IGameSession>());
                        // 1.12.1: when the installed client ships its own loader batch (the tuned
                        // Stonetavern-Classic package: launch.bat → VanillaFixes → WoW_tweaked, plus DXVK
                        // and display setup), start THAT, not bare WoW.exe — otherwise the RDTSC fix, DXVK
                        // and the tweaks are silently skipped, exactly as on Linux. Installs without a
                        // loader keep the plain native start (WindowsLoaderScriptLauncher falls back to the
                        // inner launcher). The modern (1.14.2) path keeps the RAW nativeStarter — it starts
                        // its own client exe, never through a legacy loader batch.
                        var legacyNative = new WindowsLoaderScriptLauncher(nativeStarter, logger);
                        return new WindowsGameLauncherRouter(legacyNative, modern, logger);
                    });
                    services.AddSingleton<IInstallRootsProvider, WindowsInstallRootsProvider>();
                    services.AddSingleton<IGameProcessDetector, WindowsGameProcessDetector>();
                    services.AddSingleton<IUpdateSwapStrategy, WindowsUpdateSwapStrategy>();
                    // Windows start is authoritative → exit immediately (shipped behaviour, unchanged).
                    services.AddSingleton<ILaunchExitPolicy, ImmediateLaunchExitPolicy>();
                }
                else if (OperatingSystem.IsLinux())
                {
                    // WP2: real Wine launcher (functional prefix probe) into the managed prefix, plus a
                    // grace-window exit policy — a successful `wine` start is not proof the client runs.
                    // WP3: the managed Wine prefix lives under the launcher's XDG data dir — pass the
                    // injected ShareDir so the prefix path comes from IAppPaths, not a second source.
                    // Runtime split (HANDOFF 2026-07-22 §2, owner decision 2026-07-21): 1.12.1 stays on
                    // system Wine (proven, 32-bit, needs no container); 1.14.2 goes to a Lutris wine-ge
                    // runner when one is installed - the only path proven end to end - and falls back to
                    // umu/Proton only when there is none. The router picks per launch off the resolved
                    // exe's own name; see LinuxGameLauncherRouter.
                    services.AddSingleton<IGameLauncher>(sp =>
                    {
                        var logger = sp.GetRequiredService<Serilog.ILogger>();
                        var shareDir = sp.GetRequiredService<IAppPaths>().ShareDir;
                        // 1.12.1: when the installed client ships its own loader script (the tuned
                        // Stonetavern-Classic package: launch.sh → VanillaFixes → WoW_tweaked, plus DXVK
                        // and display setup), start THAT, not bare `wine WoW.exe` — otherwise the RDTSC
                        // fix, DXVK and the tweaks are silently skipped. Installs without a loader keep
                        // the plain wine start (LoaderScriptLauncher falls back to the inner launcher).
                        var legacyWine = new LoaderScriptLauncher(
                            new WineGameLauncher(logger, WineOptions.ForShareDir(shareDir)), logger);

                        var detector = sp.GetRequiredService<IGameProcessDetector>();
                        var display = new XrandrDisplayResolution(logger);

                        // The modern client is never started by running its exe: proxy first, then the
                        // Arctium patcher, then wait for the game itself. ModernClientLauncher owns that
                        // whole sequence and takes the Wine environment to run it in - wine-ge when the
                        // player has one, which is the setup the client is proven on.
                        var wineGe = WineGeLocator.FindLatestForCurrentUser();
                        IGameLauncher? modernLauncher = null;
                        if (wineGe is not null)
                        {
                            logger.Information("wine-ge runner found for the modern client: {Wine}", wineGe);
                            var modernWine = new WineGameLauncher(
                                logger, WineOptions.ModernForShareDir(shareDir, wineGe));
                            modernLauncher = new ModernClientLauncher(
                                logger, modernWine, detector, display,
                                layout => new HermesProxyRunner(
                                    logger, layout.ProxyExe, [], Path.Combine(shareDir, "hermes-proxy.pid")),
                                // The session the launcher hands the live proxy to, so it (alive in the
                                // tray now) reaps it when the game ends instead of leaving it detached.
                                sp.GetRequiredService<IGameSession>());
                        }
                        else
                        {
                            logger.Information(
                                "No wine-ge runner found; the modern client falls back to umu/Proton");
                        }

                        var protonFallback = new UmuGameLauncher(logger, UmuOptions.ForShareDir(shareDir));
                        return new LinuxGameLauncherRouter(legacyWine, modernLauncher, protonFallback, logger);
                    });
                    services.AddSingleton<IInstallRootsProvider, LinuxInstallRootsProvider>();
                    services.AddSingleton<IGameProcessDetector, LinuxGameProcessDetector>();
                    services.AddSingleton<IUpdateSwapStrategy, LinuxUpdateSwapStrategy>();
                    services.AddSingleton<ILaunchExitPolicy>(sp => new GraceWindowLaunchExitPolicy(
                        sp.GetRequiredService<IGameProcessDetector>(), sp.GetRequiredService<Serilog.ILogger>()));
                }
                else if (OperatingSystem.IsMacOS())
                {
                    // macOS modern-only branch (no 1.12 on the Mac — owner scope 2026-07-23). The 1.14.2
                    // client runs under the FREE, redistributable Gcenx Game Porting Toolkit Wine (wine-7.7
                    // + Apple's D3DMetal), proven to reach the in-world state on Stonetavern without
                    // CrossOver. It goes through the pre-patched WowClassic_ForCustomServers.exe directly
                    // (no Arctium — Arctium hangs under GPTK-Wine, and the static exe already runs). The
                    // proxy is the native macOS HermesProxy under Hermes/. There is no legacy router: the
                    // Mac only ever launches the modern client.
                    services.AddSingleton<IGameProcessDetector>(sp =>
                        new MacGameProcessDetector(sp.GetRequiredService<Serilog.ILogger>()));
                    services.AddSingleton<IGameLauncher>(sp =>
                    {
                        var logger = sp.GetRequiredService<Serilog.ILogger>();
                        var shareDir = sp.GetRequiredService<IAppPaths>().ShareDir;
                        var detector = sp.GetRequiredService<IGameProcessDetector>();
                        // Resolve GPTK's wine64: a copy bundled in the .app FIRST, then a user-installed
                        // GPTK, then the launcher data dir. The bundled path is <App>.app/Contents/Resources
                        // — the running binary lives in Contents/MacOS (AppContext.BaseDirectory), so
                        // Resources is its sibling. Passing it makes a shipped, notarised DMG that bundles
                        // GPTK actually discover its own wine (Codex review: without this the packaged app
                        // always fell through to the plain "wine64" name). When nothing is found the path
                        // still falls back so the DI graph builds and MacWineHost.RunAsync surfaces a clear
                        // "Game Porting Toolkit not found" error rather than a container build failure.
                        var bundleResourcesDir = Path.GetFullPath(
                            Path.Combine(AppContext.BaseDirectory, "..", "Resources"));
                        var wine64 = MacWineHost.ResolveWine64(shareDir, bundleResourcesDir) ?? "wine64";
                        var wine = new MacWineHost(logger, WineOptions.ModernForShareDir(shareDir, wine64));
                        var openSslRuntime = new MacOpenSslRuntime(AppContext.BaseDirectory);
                        var openSslEnvironment = openSslRuntime.ResolveEnvironmentOverrides();
                        if (openSslEnvironment is null)
                            logger.Error("{Message}", MacOpenSslRuntime.MissingRuntimeMessage);

                        return new MacModernClientLauncher(
                            logger, wine, detector,
                            layout => openSslEnvironment is null
                                ? new UnavailableGameProxy(MacOpenSslRuntime.MissingRuntimeMessage)
                                : new HermesProxyRunner(
                                    logger, layout.ProxyExe, [], Path.Combine(shareDir, "hermes-proxy.pid"), openSslEnvironment),
                            sp.GetRequiredService<IGameSession>());
                    });
                    // Install discovery + self-update are not wired for macOS yet (the client is obtained
                    // through the launcher's own download/install into ShareDir); neutral for now.
                    services.AddSingleton<IInstallRootsProvider, NeutralInstallRootsProvider>();
                    services.AddSingleton<IUpdateSwapStrategy, UnsupportedUpdateSwapStrategy>();
                    // A GPTK-Wine start is not proof the client runs (same as Linux) — grace-window the exit.
                    services.AddSingleton<ILaunchExitPolicy>(sp => new GraceWindowLaunchExitPolicy(
                        sp.GetRequiredService<IGameProcessDetector>(), sp.GetRequiredService<Serilog.ILogger>()));
                }
                else // any other host — neutral stubs with platform-neutral messages.
                {
                    services.AddSingleton<IGameLauncher, UnsupportedGameLauncher>();
                    services.AddSingleton<IInstallRootsProvider, NeutralInstallRootsProvider>();
                    services.AddSingleton<IGameProcessDetector, StubGameProcessDetector>();
                    services.AddSingleton<IUpdateSwapStrategy, UnsupportedUpdateSwapStrategy>();
                    // Never reached with Started=true (the launcher stub returns Failed), but wired for
                    // completeness so ILaunchExitPolicy always resolves.
                    services.AddSingleton<ILaunchExitPolicy, ImmediateLaunchExitPolicy>();
                }

                // Every service here is consumed by a SINGLETON ViewModel, so it lives for the whole
                // process. Typed clients (AddHttpClient<I,T>) are transient and expected to be
                // short-lived — captured in a singleton they freeze their HttpMessageHandler forever
                // and stop reacting to DNS changes. That is Microsoft's documented anti-pattern
                // ("Avoid typed clients in singleton services"), and the launcher log proved it:
                // after "HttpMessageHandler expired after 120000ms" the factory kept reporting
                // "cleanup cycle — processed: 0 items, remaining: 3 items" every 10s, forever.
                // Nothing was ever released, and the churn drowned the log.
                //
                // For long-lived clients the documented answer is not the factory but a handler with
                // a bounded PooledConnectionLifetime: connections (and their DNS resolution) are
                // recycled on their own, the client may live as long as the app.
                services.AddSingleton<IManifestService>(sp => new ManifestService(
                    LongLivedClient(TimeSpan.FromSeconds(30), retries: 3, delay: TimeSpan.FromSeconds(2)),
                    sp.GetRequiredService<IConfigService>(), sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IDownloadService>(sp => new DownloadService(
                    LongLivedClient(TimeSpan.FromMinutes(30), retries: 2, delay: TimeSpan.FromSeconds(5)),
                    sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IServerStatusService>(sp => new ServerStatusService(
                    LongLivedClient(TimeSpan.FromSeconds(10), retries: 1, delay: TimeSpan.FromSeconds(2)),
                    sp.GetRequiredService<IConfigService>(), sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IClientService, ClientService>();
                // Phase 1 repair-without-redownload (deploy/MANIFEST-SCHEMA.md §files_url): pure
                // file-system/hash logic, no HTTP, so it needs nothing but the logger.
                services.AddSingleton<IClientVerifyService>(sp => new ClientVerifyService(
                    sp.GetRequiredService<Serilog.ILogger>()));
                // WP4: the update channel decides which manifest field this process reads and whether it
                // may auto-apply. Windows → launcher (auto-apply, shipped behaviour); Linux → launcher_linux
                // (Check + Notify only); any other host → None (no launcher field wired yet).
                var updateChannel = OperatingSystem.IsWindows() ? LauncherUpdateChannel.Windows
                    : OperatingSystem.IsLinux() ? LauncherUpdateChannel.Linux
                    : LauncherUpdateChannel.None;
                services.AddSingleton<IUpdateService>(sp => new UpdateService(
                    sp.GetRequiredService<IDownloadService>(),
                    sp.GetRequiredService<Serilog.ILogger>(),
                    sp.GetRequiredService<IUpdateSwapStrategy>(),
                    updateChannel));
                // Singleton so the in-memory news cache is shared by both the rail (PlayVM) and
                // the PatchNotes section (one fetch, not two).
                services.AddSingleton<INewsService>(sp => new NewsService(
                    LongLivedClient(TimeSpan.FromSeconds(10), retries: 1, delay: TimeSpan.FromSeconds(2)),
                    sp.GetRequiredService<IConfigService>(),
                    sp.GetRequiredService<IAppPaths>(),
                    sp.GetRequiredService<Serilog.ILogger>(),
                    // --demo fills the rail with sample entries. Outside demo mode an unreachable feed
                    // leaves the rail empty rather than inventing news (BRAND_BIBLE §3).
                    Ui.Demo));
                // v3 auth + friends/presence. Under --demo: an always-signed-in stub + the in-memory
                // Mock, so the friends sidebar renders with no credentials and no backend. Otherwise the
                // real chain: a DPAPI/file-backed token store, the SRP6 login service, and the HTTP
                // friends service that pulls its bearer from the auth service. Both must be registered
                // BEFORE ShellViewModel (constructor dependencies).
                if (Ui.Demo)
                {
                    services.AddSingleton<ILauncherAuthService, DemoLauncherAuthService>();
                    services.AddSingleton<IFriendsPresenceService, MockFriendsPresenceService>();
                    // The armory has no backend yet (HANDOFF-armory.md), so the mock is the ONLY way
                    // the section can be judged at all today.
                    services.AddSingleton<IArmoryService, MockArmoryService>();
                    // Demo/offline: a sync that never talks to anything, so the switch in Settings
                    // stays usable while the panel is being judged without a server.
                    services.AddSingleton<IProfileSyncService, NoProfileSync>();
                }
                else
                {
                    services.AddSingleton<ITokenStore>(sp => new FileTokenStore(
                        sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<Serilog.ILogger>()));
                    services.AddSingleton<ILauncherAuthService>(sp => new LauncherAuthService(
                        LongLivedClient(TimeSpan.FromSeconds(15), retries: 1, delay: TimeSpan.FromSeconds(2)),
                        sp.GetRequiredService<IConfigService>(),
                        sp.GetRequiredService<ITokenStore>(),
                        sp.GetRequiredService<Serilog.ILogger>()));
                    services.AddSingleton<IFriendsPresenceService>(sp => new HttpFriendsPresenceService(
                        LongLivedClient(TimeSpan.FromSeconds(15), retries: 1, delay: TimeSpan.FromSeconds(2)),
                        sp.GetRequiredService<IConfigService>(),
                        sp.GetRequiredService<ILauncherAuthService>(),
                        sp.GetRequiredService<Serilog.ILogger>()));
                    // Wired now, dormant until the endpoint exists: against today's server every call
                    // is a 404, which the service already reports as "not available right now" - the
                    // same quiet empty state as being offline. Nothing to break, nothing to gate.
                    services.AddSingleton<IArmoryService>(sp => new HttpArmoryService(
                        LongLivedClient(TimeSpan.FromSeconds(15), retries: 1, delay: TimeSpan.FromSeconds(2)),
                        sp.GetRequiredService<IConfigService>(),
                        sp.GetRequiredService<ILauncherAuthService>(),
                        sp.GetRequiredService<Serilog.ILogger>()));
                    services.AddSingleton<IProfileSyncService>(sp => new HttpProfileSyncService(
                        LongLivedClient(TimeSpan.FromSeconds(15), retries: 1, delay: TimeSpan.FromSeconds(2)),
                        sp.GetRequiredService<IConfigService>(),
                        sp.GetRequiredService<ILauncherAuthService>(),
                        sp.GetRequiredService<Serilog.ILogger>()));
                }
                services.AddSingleton<ArmoryViewModel>();
                services.AddSingleton<LoginViewModel>();
                services.AddSingleton<PlayViewModel>();
                services.AddSingleton<PatchNotesViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<ShellViewModel>();
            })
            .Build();
    }

    /// <summary>
    /// An HttpClient that may safely be held for the lifetime of the app: the RetryHandler sits on
    /// top of a SocketsHttpHandler whose connections expire after two minutes, so DNS changes are
    /// picked up without the IHttpClientFactory's handler-rotation machinery (which cannot work
    /// when a singleton holds the client — see the note above).
    /// </summary>
    private static HttpClient LongLivedClient(TimeSpan timeout, int retries, TimeSpan delay)
    {
        var pipeline = new RetryHandler(retries, delay)
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            },
        };
        var client = new HttpClient(pipeline) { Timeout = timeout };
        client.DefaultRequestHeaders.Add("User-Agent", "WowLauncher/1.0");
        return client;
    }
}

public sealed class RetryHandler : DelegatingHandler
{
    private readonly int _max; private readonly TimeSpan _delay;
    public RetryHandler(int maxRetries, TimeSpan baseDelay) { _max = maxRetries; _delay = baseDelay; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        for (int i = 0; i <= _max; i++)
        {
            try
            {
                var resp = await base.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode || i == _max) return resp;
                Log.Warning("HTTP {Method} {Url} → {Status} ({A}/{M})", req.Method, req.RequestUri, (int)resp.StatusCode, i + 1, _max + 1);
            }
            catch (HttpRequestException ex) when (i < _max)
            { Log.Warning(ex, "HTTP {Method} {Url} failed ({A}/{M})", req.Method, req.RequestUri, i + 1, _max + 1); }
            if (i < _max) await Task.Delay(_delay * Math.Pow(2, i), ct);
        }
        throw new HttpRequestException("All retries exhausted");
    }
}
