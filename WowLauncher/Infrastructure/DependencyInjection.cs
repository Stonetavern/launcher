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

                // The realm THIS launch goes to, as one function every launch path can ask. It reads
                // the flat config field the play surface freezes right before starting (see
                // PlayViewModel.LaunchCoreAsync), so the client's realmlist, the loader script's
                // REALMLIST and the 1.14.2 proxy's ServerAddress cannot disagree about it — the exact
                // drift that made a player-added realm start against Stonetavern instead.
                static Func<string?> ActiveRealmAddress(IServiceProvider sp) => () =>
                {
                    try { return sp.GetRequiredService<IConfigService>().Load().RealmlistAddress; }
                    catch { return null; }
                };

                // The language the player picked, read at launch time rather than captured at wiring
                // time — the pick can change between the two. Whether the installed client can honour
                // it is decided where the client is (ModernClientLauncher), not here.
                static Func<string?> ActiveLocale(IServiceProvider sp) => () =>
                {
                    try { return sp.GetRequiredService<IConfigService>().Load().Locale; }
                    catch { return null; }
                };

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
                            sp.GetRequiredService<IGameSession>(),
                            ActiveRealmAddress(sp));
                        // 1.12.1: when the installed client ships its own loader batch (the tuned
                        // Stonetavern-Classic package: launch.bat → VanillaFixes → WoW_tweaked, plus DXVK
                        // and display setup), start THAT, not bare WoW.exe — otherwise the RDTSC fix, DXVK
                        // and the tweaks are silently skipped, exactly as on Linux. Installs without a
                        // loader keep the plain native start (WindowsLoaderScriptLauncher falls back to the
                        // inner launcher). The modern (1.14.2) path keeps the RAW nativeStarter — it starts
                        // its own client exe, never through a legacy loader batch.
                        var legacyNative = new WindowsLoaderScriptLauncher(
                            nativeStarter, logger, ActiveRealmAddress(sp));
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
                        // Womit gestartet wird, kommt jetzt aus der Einstellung des Spielers statt
                        // aus einer Regel, die niemand sehen kann (Owner 2026-08-04/05). "auto" ist
                        // der bisherige Zustand, also aendert sich fuer niemanden etwas, der nichts
                        // einstellt - und der Launcher SAGT, was er nimmt, auch wenn er auf etwas
                        // anderes zurueckfaellt als gewuenscht.
                        // 🔴 Bei JEDEM Start neu auflösen, nicht einmal beim Aufbau. Dieser Launcher
                        // ist ein Singleton; eine einmal gelesene Konfiguration hiesse, dass eine
                        // Aenderung in den Einstellungen erst nach einem Neustart wirkt - waehrend
                        // die Statuszeile dort sofort das Gegenteil behauptet. Genau die stille
                        // Falschaussage, gegen die diese Einstellung gebaut wurde (Zweitinstanz,
                        // 2026-08-05).
                        var cfgSvc = sp.GetRequiredService<IConfigService>();
                        LinuxRuntimeDecision RuntimeNow(bool preferWineGe)
                        {
                            var c = cfgSvc.Load();
                            return LinuxRuntimeSelection.ForCurrentUser(
                                c.LinuxRuntime, c.LinuxRuntimeCustomPath, preferWineGe);
                        }

                        var runtimeCfg = cfgSvc.Load();
                        // Zwei Entscheidungen, nicht eine: "Automatisch" heisst fuer 1.12.1 System-Wine
                        // (32-Bit-Binary; ein wine-ge-Runner ist oft ein reiner x86_64-Bau) und fuer
                        // 1.14.2 wine-ge zuerst. Ein gemeinsames "Automatisch" hat am 2026-08-05 den
                        // 1.12.1-Start still auf wine-ge umgestellt - aufgefallen ist es nur, weil die
                        // Statuszeile dieser Funktion es hinschrieb.
                        var legacyRuntime = LinuxRuntimeSelection.ForCurrentUser(
                            runtimeCfg.LinuxRuntime, runtimeCfg.LinuxRuntimeCustomPath,
                            autoPrefersWineGe: false);
                        var runtime = LinuxRuntimeSelection.ForCurrentUser(
                            runtimeCfg.LinuxRuntime, runtimeCfg.LinuxRuntimeCustomPath,
                            autoPrefersWineGe: true);
                        logger.Information(
                            "Linux runtime: 1.12.1 -> {Legacy} ({LegacyReason}); 1.14.2 -> {Modern} ({ModernReason}); requested {Requested}",
                            legacyRuntime.Found ? legacyRuntime.Path : "<nothing>", legacyRuntime.Reason,
                            runtime.Found ? runtime.Path : "<nothing>", runtime.Reason,
                            runtime.Requested);

                        // 1.12.1 laeuft weiter unter einer 32-Bit-faehigen Wine. Die Wahl greift hier
                        // genauso, denn genau darum ging es: ein Spieler, dessen Distribution ein
                        // kaputtes System-Wine mitbringt, kann auf wine-ge oder einen eigenen Pfad
                        // ausweichen, statt gar nicht zu starten.
                        var legacyOptions = legacyRuntime.Found
                            ? WineOptions.ForShareDir(shareDir) with { WineBinary = legacyRuntime.Path }
                            : WineOptions.ForShareDir(shareDir);
                        var legacyWine = new LoaderScriptLauncher(
                            new WineGameLauncher(logger, legacyOptions,
                                // Beim Start neu gefragt: 1.12.1 ist 32-Bit, "Automatisch" heisst hier
                                // System-Wine.
                                () => RuntimeNow(preferWineGe: false).Path),
                            logger,
                            ActiveRealmAddress(sp));

                        var detector = sp.GetRequiredService<IGameProcessDetector>();
                        var display = new XrandrDisplayResolution(logger);

                        // The modern client is never started by running its exe: proxy first, then the
                        // Arctium patcher, then wait for the game itself. ModernClientLauncher owns that
                        // whole sequence and takes the Wine environment to run it in - wine-ge when the
                        // player has one, which is the setup the client is proven on.
                        // Dieselbe Entscheidung fuer den modernen Client. Frueher loeste er sie
                        // selbst auf (ModernWineRuntime), was dieselbe Regel an zwei Stellen war -
                        // genau die Bauform, die auseinanderlaeuft, sobald eine Seite eine Einstellung
                        // bekommt und die andere nicht.
                        // Construct this path even when Wine is absent at application start. The actual
                        // binary is resolved at every Play click; otherwise a player who supplies a valid
                        // custom runtime in Settings has to restart before the router stops refusing the
                        // modern client. Its readiness probe supplies the actionable missing-Wine error.
                        var initialModernBinary = runtime.Found ? runtime.Path : "wine";
                        var modernWine = new WineGameLauncher(
                            logger, WineOptions.ModernForShareDir(shareDir, initialModernBinary),
                            // Und hier bevorzugt "Automatisch" wine-ge - der Runner, auf dem der
                            // moderne Client die meisten Belege hat.
                            () => RuntimeNow(preferWineGe: true).Path);
                        IGameLauncher modernLauncher = new ModernClientLauncher(
                            logger, modernWine, detector, display,
                            layout => new HermesProxyRunner(
                                logger, layout.ProxyExe, [], Path.Combine(shareDir, "hermes-proxy.pid")),
                            // The session the launcher hands the live proxy to, so it (alive in the
                            // tray now) reaps it when the game ends instead of leaving it detached.
                            sp.GetRequiredService<IGameSession>(),
                            ActiveRealmAddress(sp),
                            ActiveLocale(sp));

                        return new LinuxGameLauncherRouter(legacyWine, modernLauncher, logger);
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
                            sp.GetRequiredService<IGameSession>(),
                            ActiveRealmAddress(sp));
                    });
                    // Install discovery + self-update are not wired for macOS yet (the client is obtained
                    // through the launcher's own download/install into ShareDir); neutral for now.
                    services.AddSingleton<IInstallRootsProvider, NeutralInstallRootsProvider>();
                    services.AddSingleton<IUpdateSwapStrategy>(sp =>
                        new MacUpdateSwapStrategy(sp.GetRequiredService<Serilog.ILogger>()));
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
                // Wann der Launcher den Update-Server zuletzt WIRKLICH erreicht hat, und was dabei
                // angeboten wurde. Zwei getrennte Eintraege von zwei getrennten Stellen: der Abruf
                // meldet die Erreichbarkeit, die Fassung meldet erst der Update-Dienst - und der erst
                // nach der Signaturpruefung, weil eine unbeglaubigte Versionsnummer nicht vor einen
                // Spieler gehoert.
                services.AddSingleton<IUpdateCheckLog>(sp => new UpdateCheckLog(
                    sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IManifestService>(sp => new ManifestService(
                    LongLivedClient(TimeSpan.FromSeconds(30), retries: 3, delay: TimeSpan.FromSeconds(2)),
                    sp.GetRequiredService<IConfigService>(), sp.GetRequiredService<Serilog.ILogger>(),
                    sp.GetRequiredService<IUpdateCheckLog>()));
                services.AddSingleton<IDownloadService>(sp => new DownloadService(
                    LongLivedClient(TimeSpan.FromMinutes(30), retries: 2, delay: TimeSpan.FromSeconds(5)),
                    sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IServerStatusService>(sp => new ServerStatusService(
                    LongLivedClient(TimeSpan.FromSeconds(10), retries: 1, delay: TimeSpan.FromSeconds(2)),
                    sp.GetRequiredService<IConfigService>(), sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IClientService, ClientService>();
                // A problem report is one small POST a player makes at most a few times ever, so the
                // client is sized for a slow connection rather than throughput, with no retry: a
                // silent second delivery would put the same report in the inbox twice.
                services.AddSingleton<ProblemReport>(sp => new ProblemReport(
                    sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IConfigService>()));
                services.AddSingleton<IProblemReportSender>(sp => new ProblemReportSender(
                    LongLivedClient(TimeSpan.FromSeconds(30), retries: 0, delay: TimeSpan.Zero),
                    sp.GetRequiredService<IConfigService>(), sp.GetRequiredService<Serilog.ILogger>()));
                // Phase 1 repair-without-redownload (deploy/MANIFEST-SCHEMA.md §files_url): pure
                // file-system/hash logic, no HTTP, so it needs nothing but the logger.
                services.AddSingleton<IClientVerifyService>(sp => new ClientVerifyService(
                    sp.GetRequiredService<Serilog.ILogger>()));
                // WP4: the update channel decides which manifest field this process reads and whether it
                // may auto-apply. Windows → launcher (auto-apply, shipped behaviour); Linux → launcher_linux
                // (Check + Notify only); any other host → None (no launcher field wired yet).
                var updateChannel = OperatingSystem.IsWindows() ? LauncherUpdateChannel.Windows
                    : OperatingSystem.IsLinux() ? LauncherUpdateChannel.Linux
                    : OperatingSystem.IsMacOS() ? LauncherUpdateChannel.MacOs
                    : LauncherUpdateChannel.None;
                // Authenticity gate in front of the update path: manifest.json must carry a valid
                // detached signature made with the release key baked into this binary, or no update
                // step happens at all. Its own HTTP client because it re-reads the manifest bytes the
                // signature covers (ManifestService hands out a parsed object, and a signature covers
                // bytes) — kilobytes, once per update check.
                services.AddSingleton(ManifestSignature.Embedded);
                // Release policy behind the signature: a genuine manifest still has to be CURRENT
                // (serial), still valid (expires) and for THIS channel. Its anti-rollback floor lives in
                // StateDir — launcher-owned state, not the player-editable config.
                services.AddSingleton<IManifestTrustStore>(sp => new ManifestTrustStore(
                    sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton(sp => new ManifestReleasePolicy(
                    sp.GetRequiredService<IManifestTrustStore>(), sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IManifestSignatureGate>(sp => new ManifestSignatureGate(
                    LongLivedClient(TimeSpan.FromSeconds(30), retries: 2, delay: TimeSpan.FromSeconds(2)),
                    sp.GetRequiredService<IConfigService>(),
                    sp.GetRequiredService<ManifestSignature>(),
                    sp.GetRequiredService<ManifestReleasePolicy>(),
                    sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IUpdateAttemptLedger>(sp => new UpdateAttemptLedger(
                    sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IUpdateHealth>(sp => new UpdateHealth(
                    sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IUpdateService>(sp => new UpdateService(
                    sp.GetRequiredService<IDownloadService>(),
                    sp.GetRequiredService<Serilog.ILogger>(),
                    sp.GetRequiredService<IUpdateSwapStrategy>(),
                    sp.GetRequiredService<IManifestSignatureGate>(),
                    updateChannel,
                    currentVersion: null,
                    // Ohne dieses Argument liefe der Launcher in die zweite Endlosschleife vom
                    // 2026-08-01 zurück: ein Update, das sich nicht installieren lässt, würde bei
                    // jedem Start erneut geladen und erneut versucht (Akte
                    // decisions/2026-08-01-update-loop-assemblyversion.md).
                    attempts: sp.GetRequiredService<IUpdateAttemptLedger>(),
                    // Die andere Hälfte der Bremse: der Ledger fängt „das Update kam nie an", dies
                    // hier „es kam an und startet nicht". Ohne das Argument tauscht der Launcher
                    // ohne Netz — ein Build, der beim Start abstürzt, bliebe für immer stehen.
                    health: sp.GetRequiredService<IUpdateHealth>(),
                    checkLog: sp.GetRequiredService<IUpdateCheckLog>()));
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
                // Addon catalog + installer. Singleton so the catalog is fetched once per session and
                // shared; it reuses the download service so the hash-verify-before-extract rule is the
                // same one the client download obeys, not a second copy of it.
                services.AddSingleton<IAddonService>(sp => new AddonService(
                    LongLivedClient(TimeSpan.FromSeconds(15), retries: 1, delay: TimeSpan.FromSeconds(2)),
                    sp.GetRequiredService<IConfigService>(),
                    sp.GetRequiredService<IDownloadService>(),
                    sp.GetRequiredService<IAppPaths>(),
                    sp.GetRequiredService<Serilog.ILogger>(),
                    // Der Katalog haengt seit 2026-08-05 am Login (Owner). Ohne Anmeldung wird gar
                    // nicht erst gefragt - auch der Plattencache nicht ausgeliefert, sonst haelt die
                    // Sperre nur bis zum ersten Mal, an dem sie passiert wurde.
                    sp.GetRequiredService<ILauncherAuthService>()));
                // One addon set per realm. Stateless apart from the disk it reads, so a singleton
                // is enough and both the addons section and the launch path share it.
                services.AddSingleton<AddonProfileService>();
                // The 1.12.1 language switch. Stateless apart from the client directory it reads, so
                // one instance serves the language menu and the launch path alike.
                services.AddSingleton<VanillaLocalePacks>();
                services.AddSingleton<ILanguagePackService>(sp => new LanguagePackService(
                    sp.GetRequiredService<IDownloadService>(),
                    sp.GetRequiredService<VanillaLocalePacks>(),
                    sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<AddonsViewModel>(sp => new AddonsViewModel(
                    sp.GetRequiredService<IAddonService>(),
                    sp.GetRequiredService<IConfigService>(),
                    sp.GetRequiredService<AddonProfileService>(),
                    sp.GetRequiredService<Serilog.ILogger>(),
                    sp.GetRequiredService<IGameProcessDetector>()));
                services.AddSingleton<ArmoryViewModel>();
                services.AddSingleton<LoginViewModel>();
                services.AddSingleton<PlayViewModel>();
                services.AddSingleton<PatchNotesViewModel>();
                // Startbericht zum Kopieren: der Zustand, den ein Spieler nicht kennt und ohne den
                // "ich komme nicht in die Welt" nicht zu beantworten ist. Die drei Angaben, die nicht
                // in der Konfiguration stehen, kommen aus denselben Quellen, aus denen der Start sie
                // holt - nicht aus einer zweiten Rechnung daneben, sonst berichtet der Launcher etwas
                // anderes, als er tut.
                services.AddSingleton<IClipboardService>(sp => new AvaloniaClipboardService(
                    sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<StartReport>(sp =>
                {
                    var cfgSvc = sp.GetRequiredService<IConfigService>();
                    var profiles = sp.GetRequiredService<AddonProfileService>();
                    return new StartReport(cfgSvc, sp.GetRequiredService<IAppPaths>(),
                        checkLog: sp.GetRequiredService<IUpdateCheckLog>(),
                        addonSet: () =>
                        {
                            var c = cfgSvc.Load();
                            var realm = Models.RealmRegistry.All(c).FirstOrDefault(r => r.Id == c.SelectedRealmId);
                            var build = (realm?.Client ?? Models.ClientVersion.Default).Build;
                            return c.ClientInstalls.TryGetValue(build, out var dir) && !string.IsNullOrWhiteSpace(dir)
                                ? profiles.ActiveProfile(dir)
                                : null;
                        },
                        runtimeFor: client =>
                        {
                            if (!OperatingSystem.IsLinux()) return "";
                            // Bei jedem Bauen des Berichts neu aufgeloest, aus derselben Funktion, die
                            // der Start benutzt. "Automatisch" heisst fuer die beiden Clients nicht
                            // dasselbe, deshalb entscheidet der Client die Vorliebe.
                            var c = cfgSvc.Load();
                            var d = LinuxRuntimeSelection.ForCurrentUser(
                                c.LinuxRuntime, c.LinuxRuntimeCustomPath,
                                autoPrefersWineGe: client.NeedsModernRuntime);
                            if (!d.Found) return "none found";
                            return d.FellBack ? d.Path + " (fallback, the chosen one is not usable)" : d.Path;
                        });
                });
                services.AddSingleton<SettingsViewModel>(sp => new SettingsViewModel(
                    sp.GetRequiredService<IConfigService>(),
                    sp.GetRequiredService<IFolderPickerService>(),
                    sp.GetService<IDesktopIntegrationService>(),
                    sp.GetRequiredService<StartReport>(),
                    sp.GetRequiredService<IClipboardService>(),
                    sp.GetRequiredService<IUpdateCheckLog>()));
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
    /// <summary>Die Produktversion dieses Builds, einmal ermittelt. Siehe
    /// <see cref="Services.UpdateService.RunningVersion"/> für die Begründung, warum nicht
    /// <c>AssemblyName.Version</c>.</summary>
    internal static string LauncherUserAgent => $"StonetavernLauncher/{LauncherUserAgentVersion}";

    private static readonly string LauncherUserAgentVersion =
        Services.UpdateService.RunningVersion(
            System.Reflection.Assembly.GetExecutingAssembly()).ToString();

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
        // Die ECHTE Version, nicht eine feste Zahl. Dieser Kopfzeile begegnet der Betreiber in den
        // Server-Logs, wenn er einer Meldung nachgeht — und "WowLauncher/1.0" beantwortet die erste
        // Frage jeder Diagnose ("welcher Build?") mit einer Unwahrheit. Dieselbe Quelle wie der
        // Update-Vergleich: die Produktversion, nicht AssemblyName.Version (die steht seit 2026-08-01
        // fest auf einer Zahl, die nichts über den Build aussagt).
        client.DefaultRequestHeaders.Add("User-Agent", LauncherUserAgent);
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
