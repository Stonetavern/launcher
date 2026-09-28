namespace WowLauncher.Infrastructure;

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WowLauncher.Services;
using WowLauncher.Services.Patching;
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
                        var proxyLog = Path.Combine(sp.GetRequiredService<IAppPaths>().LogDir,
                            ProxyOutputLog.FileNameFor("jims"));
                        var nativeStarter = new WindowsGameLauncher(logger);
                        var detector = sp.GetRequiredService<IGameProcessDetector>();
                        var modern = new WindowsModernClientLauncher(
                            logger, nativeStarter, detector,
                            layout => new JimsProxyRunner(
                                logger, layout.ProxyExe, [], Path.Combine(shareDir, "jims-proxy.pid"),
                                outputLogPath: proxyLog),
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
                            nativeStarter, logger, ActiveRealmAddress(sp),
                            display: sp.GetRequiredService<IClientDisplayService>());
                        return new WindowsGameLauncherRouter(legacyNative, modern, logger);
                    });
                    services.AddSingleton<IInstallRootsProvider, WindowsInstallRootsProvider>();
                    // Built here, inside the OperatingSystem.IsWindows() guard: CA1416 does not follow the
                    // guard into a lambda, so constructing it inside the factory warned on every build.
                    // The probe has no state and no constructor work, so building it eagerly costs nothing.
                    IDisplayProbe windowsDisplayProbe = new WindowsDisplayProbe();
                    services.AddSingleton<IClientDisplayService>(sp =>
                        new ClientDisplayService(windowsDisplayProbe, sp.GetRequiredService<Serilog.ILogger>()));
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
                            ActiveRealmAddress(sp),
                            display: sp.GetRequiredService<IClientDisplayService>(),
                            // The 1.12.1 install, for the START.sh readiness check (the package brings
                            // its own pinned Proton since 2026-09-22). Read fresh: the path changes on
                            // install/move without a restart.
                            legacyClientDir: () => cfgSvc.Load().ClientInstalls.TryGetValue(
                                5875, out var d) ? d : null);

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
                        var wineGeBackedModernWine = new WineGameLauncher(
                            logger, WineOptions.ModernForShareDir(shareDir, initialModernBinary),
                            // Und hier bevorzugt "Automatisch" wine-ge - der Runner, auf dem der
                            // moderne Client die meisten Belege hat.
                            () => RuntimeNow(preferWineGe: true).Path);
                        // KONZEPT §13 (owner measurement 2026-09-19, Ledger run U): GE-Proton first,
                        // ahead of wine-ge/system Wine - JimsProxy v5.2.1-beta.4 no longer crashes on
                        // world entry, and that fix was measured under GE-Proton specifically. Resolved
                        // fresh on every launch (GeProtonLocator.FindLatestForCurrentUser is called
                        // inside ModernLinuxWineHost.Resolve, not here), the same "no restart needed"
                        // guarantee RuntimeNow already gives wine-ge/system Wine. When no GE-Proton is
                        // installed this falls straight through to wineGeBackedModernWine - the EXACT
                        // object constructed above, unchanged - so a player without Steam sees no
                        // behaviour difference at all from before this pass.
                        IWineHost modernWine = new ModernLinuxWineHost(
                            logger,
                            geProton: GeProtonLocator.FindLatestForCurrentUser,
                            steamCompatClientInstallPath: () => GeProtonLocator.SteamCompatClientInstallPathFor(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                            fallback: () => wineGeBackedModernWine);
                        IGameLauncher modernLauncher = new ModernClientLauncher(
                            logger, modernWine, detector, display,
                            layout => new HermesProxyRunner(
                                logger, layout.ProxyExe, [], Path.Combine(shareDir, "hermes-proxy.pid"),
                                // The shared-tree layout (KONZEPT §13) puts the binary in Hermes/bin/
                                // while CSV/config stay in Hermes/ - starting from the binary's own
                                // directory (the old default) would make it die looking for
                                // Hermes/bin/CSV/... that is not there. layout.ProxyDir is already the
                                // resolved DATA directory (ProxyBinaryResolver.LinuxDataDir), correct
                                // for both the new and the old per-OS tree.
                                workingDirectory: layout.ProxyDir,
                                outputLogPath: Path.Combine(
                                    sp.GetRequiredService<IAppPaths>().LogDir, ProxyOutputLog.FileNameFor("hermes")),
                                // REST/realm/instance ports move off anything that already holds them
                                // (2026-09-27: a local llama-server on 8081 left the client on
                                // "Connecting"); see HermesPortPlan.
                                relocateAuxiliaryPorts: true),
                            // The session the launcher hands the live proxy to, so it (alive in the
                            // tray now) reaps it when the game ends instead of leaving it detached.
                            sp.GetRequiredService<IGameSession>(),
                            ActiveRealmAddress(sp),
                            ActiveLocale(sp));

                        return new LinuxGameLauncherRouter(legacyWine, modernLauncher, logger);
                    });
                    services.AddSingleton<IInstallRootsProvider, LinuxInstallRootsProvider>();
                    services.AddSingleton<IClientDisplayService>(sp =>
                        new ClientDisplayService(new LinuxDisplayProbe(), sp.GetRequiredService<Serilog.ILogger>()));
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
                        // Die Aufloesung oben laeuft EINMAL, beim Bau des Containers. Der Werkzeugkasten
                        // kann aber spaeter kommen: MacGptkProvisioner laedt ihn beim ersten Spielen
                        // nach. Ohne den zweiten Blick behielte der Launcher fuer den Rest der Sitzung
                        // den Platzhalternamen "wine64" -- und scheiterte an etwas, das daneben liegt.
                        var wine = new MacWineHost(
                            logger, WineOptions.ModernForShareDir(shareDir, wine64),
                            () => MacWineHost.ResolveWine64(shareDir, bundleResourcesDir));
                        // OpenSSL is required by the BINARY, not by the platform. The old native
                        // HermesProxy linked against a system OpenSSL 3 — for it the check stays
                        // fail-closed, because starting it without gives the player a vague "proxy did not
                        // start" instead of a precise sentence. The JimsProxy builds we ship are
                        // self-contained .NET 10 and use Apple's own crypto: BOTH macOS binaries of build
                        // 5.2.0 contain 487 references to AppleCrypto and ZERO to Native.OpenSsl, while the
                        // linux-x64 binary of the same build has 335 — the probe demonstrably measures
                        // (2026-08-24). For those, a missing OpenSSL is not a reason to refuse: doing so
                        // blocked players over a dependency the proxy does not have (report ST-9YBC-Y1BX).
                        // Which of the two it is comes from the resolver, not from a guess here.
                        var openSslRuntime = new MacOpenSslRuntime(AppContext.BaseDirectory);
                        var openSslEnvironment = openSslRuntime.ResolveEnvironmentOverrides();

                        return new MacModernClientLauncher(
                            logger, wine, detector,
                            layout => layout.ProxyRequiresOpenSsl && openSslEnvironment is null
                                ? new UnavailableGameProxy(MacOpenSslRuntime.MissingRuntimeMessage)
                                : new HermesProxyRunner(
                                    logger, layout.ProxyExe, [], Path.Combine(shareDir, "hermes-proxy.pid"),
                                    openSslEnvironment,
                                    // The macOS package keeps config and CSV data in Hermes/ while the
                                    // binary lives in Hermes/bin/ — starting it from the binary's own
                                    // directory would make it die on a missing Hermes/CSV/... path.
                                    workingDirectory: layout.ProxyDir,
                                    // The Mac is the platform where the proxy's own words are hardest to
                                    // come by (2026-08-24: a login that bounced back to the login screen
                                    // left nothing behind but a flawless launcher log).
                                    outputLogPath: Path.Combine(
                                        sp.GetRequiredService<IAppPaths>().LogDir,
                                        ProxyOutputLog.FileNameFor("hermes")),
                                    relocateAuxiliaryPorts: true),
                            sp.GetRequiredService<IGameSession>(),
                            ActiveRealmAddress(sp));
                    });
                    // Install discovery + self-update are not wired for macOS yet (the client is obtained
                    // through the launcher's own download/install into ShareDir); neutral for now.
                    services.AddSingleton<IInstallRootsProvider, NeutralInstallRootsProvider>();
                    services.AddSingleton<IClientDisplayService>(sp =>
                        new ClientDisplayService(probe: null, sp.GetRequiredService<Serilog.ILogger>()));
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
                    services.AddSingleton<IClientDisplayService>(sp =>
                        new ClientDisplayService(probe: null, sp.GetRequiredService<Serilog.ILogger>()));
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
                // Nur macOS braucht eine nachgeladene Laufzeitumgebung: Windows startet nativ, Linux
                // findet Wine im System. Die Entscheidung faellt hier, damit das Ansichtsmodell auf
                // jeder Plattform denselben einen Aufruf macht und kein "wenn macOS" traegt.
                // Linux (2026-09-22): the 1.12.1 package ships START.sh, which fetches its pinned Proton
                // itself; the provisioner only runs its --prepare step with visible progress.
                services.AddSingleton<IGameRuntimeProvisioner>(sp => OperatingSystem.IsMacOS()
                    ? new MacGptkProvisioner(
                        sp.GetRequiredService<IDownloadService>(),
                        sp.GetRequiredService<IAppPaths>(),
                        sp.GetRequiredService<Serilog.ILogger>())
                    : OperatingSystem.IsLinux()
                        ? new LinuxStartScriptProvisioner(
                            sp.GetRequiredService<Serilog.ILogger>(), sp.GetRequiredService<IDownloadService>())
                        : new NoGameRuntimeProvisioner());
                services.AddSingleton<IServerStatusService>(sp => new ServerStatusService(
                    LongLivedClient(TimeSpan.FromSeconds(10), retries: 1, delay: TimeSpan.FromSeconds(2)),
                    sp.GetRequiredService<Serilog.ILogger>()));
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
                // S2 patcher engine (ARCHITEKTUR-v2-patcher.md §4/§5): fetches+trust-binds files.json,
                // then drives Delta/PerFile/FullZip. Its own HTTP client — files.json and per-file
                // downloads are a different traffic shape (many small/medium GETs) than the client ZIP
                // and manifest clients above, but the same long-lived-handler reasoning applies.
                services.AddSingleton<IClientFileManifestLoader>(sp => new ClientFileManifestLoader(
                    LongLivedClient(TimeSpan.FromSeconds(30), retries: 2, delay: TimeSpan.FromSeconds(2)),
                    sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton<IButlerSidecar>(sp => new ButlerSidecar(
                    sp.GetRequiredService<IDownloadService>(),
                    sp.GetRequiredService<IAppPaths>(),
                    sp.GetRequiredService<Serilog.ILogger>()));
                services.AddSingleton(sp => new ClientPatchEngine(
                    sp.GetRequiredService<IClientFileManifestLoader>(),
                    sp.GetRequiredService<IDownloadService>(),
                    sp.GetRequiredService<IClientVerifyService>(),
                    sp.GetRequiredService<IButlerSidecar>(),
                    sp.GetRequiredService<IGameProcessDetector>(),
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
                    checkLog: sp.GetRequiredService<IUpdateCheckLog>(),
                    // Release 1.8.11 (Stolperfallen-Preflight): TRANSLOCATED / PATH_NOT_WRITABLE before
                    // the self-update swap. No dependency of its own (probes call straight into the
                    // OS), so a plain instance is enough — see IInstallEnvironmentGate for why this is
                    // optional on the constructor rather than required.
                    envGate: new InstallEnvironmentGate()));
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
                    // Real accounts only: the demo backend has nothing worth remembering.
                    services.AddSingleton<IUsernameMemory>(sp => new FileUsernameMemory(
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
                // The 1.9 login shell signs in over the same launcher auth (Spec §12.1 A), through the
                // narrow gateway seam. Registered for the player path; tests and the QA render harness
                // substitute the fake.
                services.AddSingleton<Startup.IAuthGateway>(sp =>
                    new Startup.LauncherAuthGateway(sp.GetRequiredService<ILauncherAuthService>()));
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
                            // Same resolver as the Settings line: what really starts (package
                            // Proton, GE-Proton), the Wine menu only where it is the fallback.
                            return LinuxRuntimeInUseResolver.ReportLine(LinuxRuntimeInUseResolver.For(
                                client, c.ClientInstalls.GetValueOrDefault(client.Build),
                                c.LinuxRuntime, c.LinuxRuntimeCustomPath,
                                GeProtonLocator.FindLatestForCurrentUser, ProtonPython.IsEnoughOnThisMachine,
                                dir => LinuxStartScript.Find(dir, File.Exists) is not null));
                        });
                });
                services.AddSingleton<SettingsViewModel>(sp => new SettingsViewModel(
                    sp.GetRequiredService<IConfigService>(),
                    sp.GetRequiredService<IFolderPickerService>(),
                    sp.GetService<IDesktopIntegrationService>(),
                    sp.GetRequiredService<StartReport>(),
                    sp.GetRequiredService<IClipboardService>(),
                    sp.GetRequiredService<IUpdateCheckLog>(),
                    display: sp.GetRequiredService<IClientDisplayService>(),
                    supportPackage: new SupportPackage(sp.GetRequiredService<IAppPaths>(),
                        sp.GetRequiredService<IConfigService>(), sp.GetRequiredService<StartReport>(),
                        sp.GetRequiredService<Serilog.ILogger>())));
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
    /// <summary>Only these are worth sending again. A 401/403/404 is a decided answer — repeating it
    /// doubles the load and the log for a result that cannot change (Codex review 2026-08-24).</summary>
    private static bool WorthRetrying(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout ||
        status == HttpStatusCode.TooManyRequests ||
        (int)status >= 500;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        for (int i = 0; i <= _max; i++)
        {
            try
            {
                var resp = await base.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode || i == _max || !WorthRetrying(resp.StatusCode)) return resp;
                Log.Warning("HTTP {Method} {Url} → {Status} ({A}/{M})", req.Method, req.RequestUri, (int)resp.StatusCode, i + 1, _max + 1);
            }
            // No exception object here on purpose. This line fires once per attempt per poll, and with a
            // stack trace it is ~1500 characters each — enough of them and the problem report a player
            // sends carries nothing BUT retry noise, which is how report ST-8PXS-EGCY arrived without the
            // error it was about. The caller still logs the final failure with its stack.
            catch (HttpRequestException ex) when (i < _max)
            { Log.Warning("HTTP {Method} {Url} failed ({A}/{M}): {Reason}", req.Method, req.RequestUri, i + 1, _max + 1, ex.Message); }
            if (i < _max) await Task.Delay(_delay * Math.Pow(2, i), ct);
        }
        throw new HttpRequestException("All retries exhausted");
    }
}
