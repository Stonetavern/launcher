using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WowLauncher.Infrastructure;
using WowLauncher.Localization;
using WowLauncher.Services;
using WowLauncher.ViewModels;
using WowLauncher.Views;

namespace WowLauncher;

public partial class App : Application
{
    private IHost? _host;

    // ─── Tray + close-to-background state ─────────────────────────────────────
    // Held for the app lifetime: the tray icon is the only way back once the window hides, so it must
    // outlive every window. _closePref decides Ask/Background/Quit; _shuttingDown lets the deliberate
    // close inside QuitApp pass straight through the Closing interceptor instead of looping.
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private IClosePreferenceService? _closePref;
    private TrayIcon? _trayIcon;
    private bool _trayAvailable;
    private bool _shuttingDown;

    // ─── Agent control surface (opt-in, --agent-control) ──────────────────────
    // Null on every normal run. Held for the app lifetime so the listener can be torn down and its
    // handshake file removed on the way out; a leftover file would advertise a dead port.
    private Services.AgentControl.AgentControlServer? _agentControl;
    private string? _agentControlStateDir;
    private readonly List<PosixSignalRegistration> _agentControlSignals = new();

    // ─── Start screen state ───────────────────────────────────────────────────
    // _startupSettled flips once RunStartupAsync has an outcome: before that, a close on the splash is
    // the player asking to abort the start (_splashAborted → quit); afterwards it is our own close in
    // the finally and must pass straight through. The CTS lives for the process because the splash is
    // shown exactly once.
    private readonly CancellationTokenSource _startupCts = new();
    private bool _startupSettled;
    private bool _splashAborted;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // ─── Boot frame (2026-09-28) ─────────────────────────────────────────────
    // The window on screen before anything expensive exists. Null once it has handed over (or on a QA
    // still, which never shows one). _bootHandedOver tells "closed because the next window stands"
    // from "closed by the player before anything else appeared" (that one ends the app).
    // The window on screen before the next one takes over: the boot frame, or the setup page when a
    // setup continues in this process. Both draw the lantern at the login shell's spot.
    private Window? _boot;
    private bool _bootHandedOver;
    private bool _frameworkCompleted;
    private TimeSpan _tFramework;

    public override void OnFrameworkInitializationCompleted()
    {
        _tFramework = Startup.StartupClock.Elapsed;
        Startup.StartupClock.Mark("framework");

        // Interactive start: put the boot frame up NOW and build everything else after its first
        // frame. Before this, the first picture waited for the DI host, the config, the tray, the
        // start services and the whole login window (~690 ms warm, measured 2026-09-28; 12.5 s on a
        // cold start that morning). QA stills (--screenshot) keep the old single-window path: they
        // render one window and exit.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime bootDesktop
            && !(bootDesktop.Args ?? []).Contains("--screenshot"))
        {
            _desktop = bootDesktop;
            // Closing the boot frame on handover must not end the app; QuitApp drives shutdown.
            bootDesktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var boot = new Views.BootWindow();
            _boot = boot;
            boot.Closed += (_, _) =>
            {
                if (!_bootHandedOver) bootDesktop.Shutdown();
            };
            // Posted, not run inside the frame callback: the frame is presented first, then the work.
            boot.FirstFrameRendered += () => Dispatcher.UIThread.Post(StartServices, DispatcherPriority.Background);
            bootDesktop.MainWindow = boot;
            CompleteFrameworkInit();
            return;
        }

        StartServices();
    }

    /// <summary>The base call, exactly once, whichever path gets there first.</summary>
    private void CompleteFrameworkInit()
    {
        if (_frameworkCompleted) return;
        _frameworkCompleted = true;
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Show the window that takes over from the boot frame. The login shell opens exactly where the
    /// boot frame is and glides the lantern from the boot frame's spot to its own; the boot frame
    /// closes once the new window's first frame is on screen, so there is never a gap. Without a boot
    /// frame (QA) this only assigns the main window, as before.
    /// </summary>
    private void TakeOverFromBoot(IClassicDesktopStyleApplicationLifetime desktop, Window next)
    {
        desktop.MainWindow = next;
        if (_boot is not { } boot) return;

        next.WindowStartupLocation = WindowStartupLocation.Manual;
        next.Position = boot.Position;
        if (next is Views.LoginShellWindow login)
        {
            login.GlideFrom = (boot as Views.ISigilFrame)?.SigilCentre();
            login.FirstFrameRendered += CloseBoot;
            login.Show();
        }
        else if (next is Views.SetupWindow setup)
        {
            setup.FirstFrameRendered += CloseBoot;
            setup.Show();
        }
        else
        {
            next.Show();
            CloseBoot();
        }
    }

    private void CloseBoot()
    {
        if (_boot is not { } boot) return;
        _bootHandedOver = true;
        _boot = null;
        boot.Close();
    }

    private void StartServices()
    {
        var tFramework = _tFramework;
        _host = DependencyInjection.BuildHost([]);
        var tHost = Startup.StartupClock.Elapsed;
        Startup.StartupClock.Mark("host");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

            // 🔴 The Stonetavern folder (Services/Platform/StonetavernLibrary.cs), decided in Main BEFORE
            // anything loads the config: loading it writes a default one, which would erase the only
            // sign that this is an existing player. Existing players (Legacy) and QA stills fall
            // straight through to the start below, unchanged.
            // A later start of the launcher asks this one to come to the front (SingleInstance).
            Services.Platform.SingleInstance.Current?.Listen(() => Dispatcher.UIThread.Post(ShowMainWindow));

            var library = Program.Library;
            // One line in every log: which of the start paths this was, and why. Without it an
            // existing player's log (Legacy) says nothing about the folder at all, and support cannot
            // tell "never set up" from "set up and then lost" (E2E 2026-09-28).
            Serilog.Log.Information("Stonetavern folder: {Kind} ({Reason}) {Root}",
                library.Kind, library.Reason, library.Root ?? "");
            if (library.Kind == Services.Platform.LibraryDecisionKind.Handoff && _boot is not null)
            {
                if (!FinishLibraryHandoff(library))
                {
                    desktop.Shutdown();
                    return;
                }
            }
            else if (library.Kind == Services.Platform.LibraryDecisionKind.Setup && _boot is not null)
            {
                ShowLibrarySetup(desktop);
                CompleteFrameworkInit();
                return;
            }

            StartLauncher(desktop, tFramework, tHost);
        }

        CompleteFrameworkInit();
    }

    /// <summary>
    /// This process is the copy a setup started inside the new Stonetavern folder, and its first frame
    /// is on screen, which proves it runs here (a missing DLL, SmartScreen or noexec would have stopped
    /// it before this line). Claim the handoff, write the config and the marker, say "done" so the
    /// original quits, then start like any launcher. False: nothing to finish, quit.
    /// </summary>
    private bool FinishLibraryHandoff(Services.Platform.LibraryDecision library)
    {
        var log = _host!.Services.GetRequiredService<Serilog.ILogger>();
        var config = _host.Services.GetRequiredService<Services.IConfigService>();
        var ok = Services.Platform.LibrarySetup.CompleteHandoff(library.Root!, library.Nonce!,
            (root, launcherDir) => Services.Platform.LibrarySetup.Commit(config, root, launcherDir), log);
        if (ok)
            _ = Task.Run(() => Services.Platform.LibraryShortcuts.CreateAsync(library.Root!, config, log));
        return ok;
    }

    /// <summary>
    /// A new player: the setup page instead of the login. It either hands over to the launcher it put
    /// into the chosen folder (this one quits), or, where the launcher does not move (macOS, a build
    /// without a payload list), writes the config here and carries on into the normal start.
    /// </summary>
    private void ShowLibrarySetup(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var services = _host!.Services;
        var log = services.GetRequiredService<Serilog.ILogger>();
        // English, like the login that follows: a new config starts on enUS, and the page should not
        // speak another language than the next one.
        var config = services.GetRequiredService<Services.IConfigService>();
        var setup = new Services.Platform.LibrarySetup(Services.Platform.LibrarySetupHost.ForCurrentProcess(), log,
            (root, launcherDir) => Services.Platform.LibrarySetup.Commit(config, root, launcherDir));
        var vm = new SetupViewModel(setup, services.GetRequiredService<Services.IFolderPickerService>(),
            Services.Platform.LibraryNames.DefaultRoot(Services.Platform.LibraryProbe.HomeDir()));
        var window = new Views.SetupWindow { DataContext = vm };
        var handedOn = false;
        window.Closed += (_, _) =>
        {
            if (!handedOn) desktop.Shutdown();
        };
        vm.Finished += result =>
        {
            handedOn = true;
            log.Information("Library setup finished: {Outcome}", result.Outcome);
            if (result.Outcome == Services.Platform.LibrarySetupOutcome.SetUpInPlace)
            {
                // Continue here: the setup page is the frame the login shell takes over from.
                _boot = window;
                _bootHandedOver = false;
                StartLauncher(desktop, _tFramework, Startup.StartupClock.Elapsed);
            }
            else
            {
                desktop.Shutdown();
            }
        };
        TakeOverFromBoot(desktop, window);
        log.Information("Library setup shown: no earlier install found ({Reason})", Program.Library.Reason);
    }

    /// <summary>The launcher start as it was before the Stonetavern folder existed: config, language,
    /// tray, then the login shell (or the splash). Every existing player takes exactly this path.</summary>
    private void StartLauncher(IClassicDesktopStyleApplicationLifetime desktop, TimeSpan tFramework, TimeSpan tHost)
    {
        {
            // Before anything is built: the launcher speaks whatever the player picked for the GAME
            // (Loc.ForClientLocale). Doing it here rather than in a view model means the first frame
            // is already in the right language, instead of flickering from English one frame later.
            var startCfg = _host!.Services.GetRequiredService<Services.IConfigService>().Load();
            // Eigene Launcher-Sprache gewinnt; leer heisst weiterhin "der Spielsprache folgen".
            Localization.Loc.Use(string.IsNullOrWhiteSpace(startCfg.LauncherLanguage)
                ? Localization.Loc.ForClientLocale.GetValueOrDefault(startCfg.Locale, "en")
                : startCfg.LauncherLanguage);
            Startup.StartupClock.Mark("config");

            var args = desktop.Args ?? [];

            // The shell (ViewModels + the v3 window) costs ~450 ms of the cold start (measured
            // 2026-09-20, Release: view models 132 ms, ShellV3Window 317 ms). The shipped path builds
            // it here, before anything is on screen. The 1.9 login shell (flag below) defers it until
            // the player signs in, so its first frame is not paying for a window nobody sees yet.
            (ShellViewModel Shell, Window Window) BuildShell()
            {
                var shellVm = _host.Services.GetRequiredService<ShellViewModel>();
                var tShellVm = Startup.StartupClock.Elapsed;

                // Three skins, one binary. v3 is what ships (default); v1 and v2 stay reachable via
                // `--ui v1` / `--ui v2`. Same ViewModels throughout, so they compare on one machine.
                Window shellWindow = Ui.V2 ? new ShellV2Window { DataContext = shellVm }
                    : Ui.V1 ? new MainWindow { DataContext = shellVm }
                    : new ShellV3Window { DataContext = shellVm };
                // Where the cold start goes (Spec §4 budget): framework up, DI host, ViewModels, window.
                Serilog.Log.Information("Startup clock: framework {F:F0} ms, host {H:F0} ms, view models {V:F0} ms, shell window {W:F0} ms",
                    tFramework.TotalMilliseconds, tHost.TotalMilliseconds, tShellVm.TotalMilliseconds,
                    Startup.StartupClock.Elapsed.TotalMilliseconds);
                return (shellVm, shellWindow);
            }

            var loginShell = !args.Contains("--screenshot") && Startup.LoginShellFlag.IsEnabled(startCfg);
            ShellViewModel shell = null!;
            Window window = null!;
            if (!loginShell) (shell, window) = BuildShell();

            // ─── QA render path (unchanged) ───────────────────────────────────
            // A --screenshot render seeds state and Environment.Exit(0)s without ever closing a
            // window, so it needs neither the tray nor the explicit-shutdown mode (which would
            // otherwise keep the process alive after the render), and no start screen either: the
            // splash's whole job is to be gone by the time anyone looks. The one exception is a shot
            // OF the splash (`--section splash`), which is how it gets looked at at all.
            // --e2e never reaches Avalonia at all (Program.cs).
            if (args.Contains("--screenshot"))
            {
                if (SectionArg(args) == "setup")
                {
                    // The first-start page of the Stonetavern folder (TODO C7). Only a fresh machine
                    // ever sees it, so without this branch nobody would look at it before a player
                    // does. `--root <folder>` shows the checks for another place (a noexec mount, a
                    // folder that is taken). The checks only read; nothing is created or copied.
                    var log = _host.Services.GetRequiredService<Serilog.ILogger>();
                    var rootIdx = Array.IndexOf(args, "--root");
                    var root = rootIdx >= 0 && rootIdx + 1 < args.Length
                        ? args[rootIdx + 1]
                        : Services.Platform.LibraryNames.DefaultRoot(Services.Platform.LibraryProbe.HomeDir());
                    var setup = new Services.Platform.LibrarySetup(Services.Platform.LibrarySetupHost.ForCurrentProcess(), log,
                        (_, _) => { });
                    var shot = new Views.SetupWindow
                    {
                        DataContext = new SetupViewModel(setup, _host.Services.GetRequiredService<Services.IFolderPickerService>(), root),
                    };
                    desktop.MainWindow = shot;
                    HandleScreenshotMode(desktop, shot);
                }
                else if (SectionArg(args) == "splash")
                {
                    var shot = new SplashWindow { DataContext = SplashShotViewModel(args) };
                    desktop.MainWindow = shot;
                    HandleScreenshotMode(desktop, shot);
                }
                else if (SectionArg(args) == "login")
                {
                    // The 1.9 login/loading shell, seeded (Startup.LoginShellQa): `--state cold|
                    // checking|ready|offline|banner|error|timeout|transition`. Fake pipeline, fake
                    // gateway, shipped window. Same 3 s settle as every other still.
                    var state = StateArg(args);
                    var shot = new LoginShellWindow
                    {
                        DataContext = Startup.LoginShellQa.ViewModelFor(state, args.Contains("--allow-skip")),
                        HoldAtPhase0 = Startup.LoginShellQa.HoldsAtPhase0(state),
                    };
                    // The shipped window is fixed at 1000x640 (brand constraint). For a still at
                    // another size (Steam Deck 1280x800) the window itself is resized, otherwise the
                    // layout would render at 1000x640 inside a larger canvas and prove nothing about
                    // how the ratio-based composition holds.
                    var sizeIdx = Array.IndexOf(args, "--size");
                    if (sizeIdx >= 0 && sizeIdx + 1 < args.Length)
                    {
                        var parts = args[sizeIdx + 1].Split('x', 'X');
                        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                        {
                            shot.MinWidth = shot.Width = w;
                            shot.MinHeight = shot.Height = h;
                        }
                    }
                    desktop.MainWindow = shot;
                    HandleScreenshotMode(desktop, shot);
                }
                else
                {
                    desktop.MainWindow = window;
                    // QA-State-Screenshot (--state) seedet den Zustand selbst → InitAsync NICHT
                    // starten, sonst überschreibt/blockiert dessen async Manifest/Realm-Pfad den
                    // geseedeten Zustand.
                    if (!args.Contains("--state"))
                        _ = Dispatcher.UIThread.InvokeAsync(shell.InitAsync);
                    HandleScreenshotMode(desktop, window);
                }

                CompleteFrameworkInit();
                return;
            }

            // ─── Interactive path ─────────────────────────────────────────────
            {
                _closePref = _host.Services.GetRequiredService<IClosePreferenceService>();
                // Hiding the window must NOT end the app (the default OnLastWindowClose would quit the
                // moment the window disappears into the tray). We drive shutdown ourselves from QuitApp.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                SetUpTrayIcon();
                Startup.StartupClock.Mark("tray");

                // Automatic one-time AppImage first-run setup (owner UX 2026-07-24): relocate into
                // ~/Applications, drop a trusted desktop shortcut, register the menu entry. Runs off the
                // UI thread (pure file IO, self-contained error handling) so it never blocks the ≤800ms
                // interactive budget; a no-op off an AppImage / after the first run / on non-Linux.
                var firstRun = _host.Services.GetRequiredService<Services.Platform.IFirstRunSetup>();
                _ = Task.Run(() => firstRun.RunAsync());

                // 1.9 login/loading shell behind its flag (Models.LauncherConfig.LauncherShell). The
                // default stays the splash path below, byte for byte. The shell is built lazily in
                // there; everything that needs it (close interception, tray seam, agent control) is
                // wired the moment it exists.
                if (loginShell)
                {
                    RunLoginShell(desktop, startCfg, () =>
                    {
                        var built = BuildShell();
                        WireShellWindow(built.Shell, built.Window, args);
                        return built;
                    });
                    CompleteFrameworkInit();
                    return;
                }

                WireShellWindow(shell, window, args);

                // The start screen owns the boot. The shell is built but NOT shown: the splash is the
                // window the lifetime opens, and the shell only appears once the startup work has an
                // outcome. Every outcome — done, failed, cancelled, out of budget — lands in the
                // finally of RunStartupAsync, which is the only place either window is switched. That
                // is the whole guarantee: there is no path on which the splash stays up.
                var work = new ShellStartupWork(shell, _host.Services.GetRequiredService<IUpdateService>());
                var splashVm = new SplashViewModel(work);
                var splash = new SplashWindow { DataContext = splashVm };
                splash.Closing += OnSplashClosing;
                TakeOverFromBoot(desktop, splash);

                _ = Dispatcher.UIThread.InvokeAsync(() => RunStartupAsync(splash, splashVm, work, window));
            }
        }

        CompleteFrameworkInit();
    }

    /// <summary>Everything the interactive path hooks onto the shell window: close interception, the
    /// hide-to-tray seam, the agent control surface. One place, called by both start paths.</summary>
    private void WireShellWindow(ShellViewModel shell, Window window, string[] args)
    {
        window.Closing += OnMainWindowClosing;

        // Wire the hide-to-tray / restore seam the Play flow uses — but only when a real tray
        // exists (otherwise a hidden window could never be brought back, so PlayViewModel keeps
        // its hard-exit fallback). The callbacks marshal to the UI thread themselves, so the
        // controller and the ViewModel stay free of any window/dispatcher reference.
        if (_trayAvailable)
        {
            var controller = _host!.Services.GetRequiredService<IShellWindowController>() as ShellWindowController;
            controller?.Configure(
                hideToTray: () => Dispatcher.UIThread.Post(window.Hide),
                restoreFromTray: () => Dispatcher.UIThread.Post(ShowMainWindow));
        }

        // Die Steuerflaeche fuer Pruefstaende — aus, solange sie niemand anfordert.
        // Absichtlich hier: das Fenster steht, die ViewModels leben, ein Aufruf trifft also
        // denselben Zustand, den ein Mensch vor sich haette.
        StartAgentControlIfRequested(shell, args);
    }

    // ─── Start screen ─────────────────────────────────────────────────────────

    /// <summary>
    /// Wait out the startup behind the splash, then hand the desktop to the shell.
    ///
    /// <para>The <c>finally</c> is the contract: whatever the outcome and whatever throws on the way,
    /// the shell is shown and the splash is closed. Show first, close second, so the desktop is never
    /// left without one of our windows for a frame.</para>
    ///
    /// <para>The launcher self-update is the path that does NOT come back here: it downloads, verifies,
    /// hands the swap to a helper and ends the process, so <see cref="SplashViewModel"/>'s update
    /// wording is the last thing on screen. If the swap ever stalls, the ViewModel's grace window
    /// expires and this method continues into the shell rather than leaving a frozen frame.</para>
    /// </summary>
    private async Task RunStartupAsync(Window splash, SplashViewModel vm, ShellStartupWork work, Window shellWindow)
    {
        try
        {
            var outcome = await vm.RunAsync(_startupCts.Token);
            Serilog.Log.Information("Start screen finished: {Outcome}", outcome);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Start screen failed unexpectedly - continuing into the shell");
        }
        finally
        {
            work.Dispose();
            _startupSettled = true;

            if (_splashAborted)
            {
                // The player closed the start screen before it was done. That is "I do not want to
                // start", not "show me the launcher anyway".
                splash.Close();
                await QuitAppAsync();
            }
            else
            {
                shellWindow.Show();
                if (_desktop is not null) _desktop.MainWindow = shellWindow;
                splash.Close();

                // 🔴 Hier, und nicht früher. Der Gesundheitsvertrag eines Selbst-Updates gilt als
                // erfüllt, wenn das Hauptfenster wirklich steht — nicht schon, wenn Main betreten
                // wurde. Genau dazwischen liegt der Fall, den das Ganze abfängt: ein Build, dem eine
                // native Bibliothek fehlt, stirbt in der Avalonia-Initialisierung, bevor je ein
                // Fenster erscheint (am 2026-08-04 dreimal gemessen). Wer die Meldung vorziehen
                // würde, bestätigte einen Start, den es nicht gab, und der Rückweg entfiele.
                _host?.Services.GetService<Services.IUpdateHealth>()?.ReportHealthy();
            }
        }
    }

    /// <summary>A close on the splash before the startup settled is an abort, not a window event to
    /// obey: cancel the wait and let <see cref="RunStartupAsync"/>'s finally quit the app. Afterwards
    /// the close is our own and passes through.</summary>
    private void OnSplashClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_startupSettled) return;
        e.Cancel = true;
        _splashAborted = true;
        _startupCts.Cancel();
    }

    /// <summary>The lowercased <c>--section</c> argument, or "" when absent.</summary>
    private static string SectionArg(string[] args)
    {
        var i = Array.IndexOf(args, "--section");
        return i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : "";
    }

    /// <summary>The lowercased <c>--state</c> argument, or null when absent.</summary>
    private static string? StateArg(string[] args)
    {
        var i = Array.IndexOf(args, "--state");
        return i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : null;
    }

    // ─── 1.9 login/loading shell (config LauncherShell=login) ──────────────────

    /// <summary>
    /// The login screen that is also the loading screen (design/SPEC-2026-09-20). Replaces the splash
    /// on this path; the shell it hands over to is the SAME v3 window as always, built above.
    ///
    /// <para>Three ways out, all of them here: the Phase-3 storyboard completed (show the shell, close
    /// the login window, run the shell's own init); the self-update swap started (the process ends,
    /// exactly like PlayViewModel.InitAsync does it); the player closed the window before any of that
    /// (that is "I do not want to start": quit).</para>
    /// </summary>
    private void RunLoginShell(IClassicDesktopStyleApplicationLifetime desktop, Models.LauncherConfig cfg,
                               Func<(ShellViewModel Shell, Window Window)> buildShell)
    {
        var services = _host!.Services;

        // Built once, on the first phase that needs it (Phase 2 or the offline path): the ~450 ms it
        // costs then hides behind the sign-in wait instead of in front of the first frame.
        (ShellViewModel Shell, Window Window)? built = null;
        (ShellViewModel Shell, Window Window) EnsureShell() => built ??= buildShell();
        var (pipeline, facts) = Startup.LauncherInitSteps.Build(
            services.GetRequiredService<IConfigService>(),
            services.GetRequiredService<IManifestService>(),
            services.GetRequiredService<IUpdateService>(),
            services.GetRequiredService<IServerStatusService>(),
            services.GetRequiredService<IClientService>());

        // §12.1 A is decided: the player path signs in against the launcher service (the adapter is
        // registered in DI). The QA render harness (--screenshot) seeds the fake instead.
        var gateway = services.GetRequiredService<Startup.IAuthGateway>();
        Startup.StartupClock.Mark("services");
        var vm = new LoginShellViewModel(pipeline, facts, gateway, cfg.LauncherShellAllowSkipSignIn,
                                         remembered: services.GetService<IUsernameMemory>());
        var tBeforeWindow = Startup.StartupClock.Elapsed;
        var login = new LoginShellWindow { DataContext = vm };
        Startup.StartupClock.Mark("window");
        // 🔴 The self-update health contract is answered at the FIRST FRAME of the login window, the
        // same "a window really stands" moment the splash path uses. Answering only after sign-in
        // (as until 1.9.1) meant: a player who updated and closed the launcher at the login screen
        // within the 90 s window was rolled back and the new version quarantined for good
        // (measured E2E 2026-09-23, 1.8.11 -> 1.9.1: "health FEHLGESCHLAGEN ... in Quarantaene").
        login.FirstFrameRendered += () => services.GetService<IUpdateHealth>()?.ReportHealthy();
        Serilog.Log.Information("Startup clock: login shell window built at {W:F0} ms (started at {S:F0} ms)",
            Startup.StartupClock.Elapsed.TotalMilliseconds, tBeforeWindow.TotalMilliseconds);
        var handedOver = false;
        // Spec §12.1 A: a real sign-in ends in the same state the Account tab reaches, so the shell
        // has to be told about it (it may have been built while the attempt was still in flight).
        var signedIn = false;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LoginShellViewModel.Phase)) return;
            if (vm.Phase == LoginPhase.Succeeded) signedIn = true;
            if (vm.Phase is LoginPhase.Authenticating or LoginPhase.Transition)
                EnsureShell();
        };

        vm.TransitionCompleted += () =>
        {
            handedOver = true;
            var (shell, shellWindow) = EnsureShell();
            if (signedIn) shell.NotifySignedIn();
            shellWindow.Show();
            desktop.MainWindow = shellWindow;
            login.Close();
            // The shell's own startup (news, manifest, installed client, realm dot), as before.
            _ = Dispatcher.UIThread.InvokeAsync(shell.InitAsync);
            // Same contract as the splash path: healthy means the main window really stands.
            services.GetService<IUpdateHealth>()?.ReportHealthy();
        };

        vm.HaltRequested += () =>
        {
            handedOver = true;
            // Mirrors PlayViewModel.InitAsync: a moment for the sentence to be seen, then the swap
            // helper needs the file lock released.
            DispatcherTimer.RunOnce(() => Environment.Exit(0), TimeSpan.FromMilliseconds(500));
        };

        login.Closing += async (_, _) =>
        {
            if (handedOver) return;
            _startupSettled = true;
            await QuitAppAsync();
        };

        TakeOverFromBoot(desktop, login);
    }

    /// <summary>QA-only: a splash ViewModel for a still render. <c>--state update</c> shows the
    /// self-update wording, so the message a player sees mid-swap can be reviewed without triggering a
    /// swap. The work never completes, so nothing runs behind the frame.</summary>
    private static SplashViewModel SplashShotViewModel(string[] args)
    {
        var vm = new SplashViewModel(new IdleStartupWork());
        var i = Array.IndexOf(args, "--state");
        if (i >= 0 && i + 1 < args.Length && args[i + 1].ToLowerInvariant() == "update")
        {
            vm.IsUpdatingLauncher = true;
            vm.Status = Loc.T("Splash_Status_UpdatingLauncher");
        }
        return vm;
    }

    private sealed class IdleStartupWork : IStartupWork
    {
        public event EventHandler? LauncherUpdateStarted { add { } remove { } }

        public Task RunAsync(System.Threading.CancellationToken ct) =>
            new TaskCompletionSource().Task;
    }

    // ─── System tray ──────────────────────────────────────────────────────────

    /// <summary>
    /// Build the tray icon: the lantern mark, a tooltip, left-click to bring the window back, and a
    /// menu with Open + Quit. Primary target is Linux/KDE (Plasma renders an SNI tray). If the host has
    /// no tray at all the creation is wrapped so a failure only leaves <see cref="_trayAvailable"/>
    /// false - the close flow then quits honestly instead of hiding the window where nothing can restore
    /// it.
    /// </summary>
    private void SetUpTrayIcon()
    {
        try
        {
            var open = new NativeMenuItem { Header = Loc.T("Tray_Open") };
            open.Click += (_, _) => ShowMainWindow();
            var quit = new NativeMenuItem { Header = Loc.T("Tray_Quit") };
            quit.Click += async (_, _) => await QuitAppAsync();

            _trayIcon = new TrayIcon
            {
                Icon = MenuBarIcon(),
                ToolTipText = Loc.T("Tray_Tooltip"),
                IsVisible = true,
                // Left-click primary action (Windows/macOS): bring the launcher back.
                Command = new RelayCommand(ShowMainWindow),
                Menu = new NativeMenu { Items = { open, quit } },
            };

            TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
            _trayAvailable = true;

            // Die Menueleiste wechselt mit dem System die Farbe, das Symbol muss mitgehen. Auf den
            // anderen Systemen faellt das weg: Windows und die Linux-Trays zeigen die farbige Marke.
            if (OperatingSystem.IsMacOS() && PlatformSettings is not null)
                PlatformSettings.ColorValuesChanged += (_, _) =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { if (_trayIcon is not null) _trayIcon.Icon = MenuBarIcon(); }
                        catch (Exception ex) { Serilog.Log.Debug(ex, "Menueleisten-Symbol nicht umgestellt"); }
                    });
        }
        catch (Exception ex)
        {
            // No tray host on this machine: keep running with a normal window, never crash. Close then
            // means quit (see OnMainWindowClosing), so the window is never hidden into nothing.
            _trayAvailable = false;
            Serilog.Log.Warning(ex, "System tray unavailable - close will quit instead of hiding");
        }
    }

    /// <summary>
    /// Das Symbol fuer die Ablage: auf macOS eine einfarbige Silhouette, sonst die farbige Laterne.
    ///
    /// <para><b>Warum getrennt</b> (Owner-Befund 2026-08-13). Die Marke ist beige mit oranger Flamme.
    /// In der macOS-Menueleiste stehen daneben ausschliesslich einfarbige Symbole, die dem
    /// Erscheinungsbild folgen — die Laterne stach als einziger heller Fleck heraus und war auf einer
    /// hellen Leiste zugleich kaum zu erkennen. Apple loest das mit einem Template-Image, das das
    /// System selbst einfaerbt; Avalonia reicht ein solches Bild nicht als Template durch, also wird
    /// hier von Hand entschieden, was ein Template-Image automatisch tun wuerde: schwarz auf heller
    /// Leiste, weiss auf dunkler.</para>
    ///
    /// <para>Fehlt die Auskunft ueber das Erscheinungsbild, gilt hell — das ist die Voreinstellung von
    /// macOS, und ein schwarzes Symbol auf dunkler Leiste waere unsichtbar, ein weisses auf heller
    /// ebenso. Es gibt hier keine unschaedliche Ratefarbe, also wird der haeufigere Fall genommen.</para>
    /// </summary>
    private WindowIcon MenuBarIcon()
    {
        var asset = "avares://WowLauncher/Assets/lantern.png";
        if (OperatingSystem.IsMacOS())
        {
            var dunkleLeiste = PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
            asset = dunkleLeiste
                ? "avares://WowLauncher/Assets/lantern-menubar-light.png"
                : "avares://WowLauncher/Assets/lantern-menubar-dark.png";
        }

        return new WindowIcon(AssetLoader.Open(new Uri(asset)));
    }

    /// <summary>Bring the launcher back from the tray: visible, un-minimised, focused. The v3 shell
    /// re-arms its motion gate off the IsVisible/WindowState change, so animations resume here.</summary>
    private void ShowMainWindow()
    {
        if (_desktop is null) return;
        if (Startup.BringForward.Pick(_desktop.MainWindow, _desktop.Windows) is not { } window)
        {
            Serilog.Log.Information("Come to the front: no open window to show");
            return;
        }
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Really quit: gracefully stop a launcher-owned realm proxy, hide the tray icon, and end
    /// the Avalonia app loop. The proxy is only stopped when NO client is still running (IGameSession
    /// checks first) — a deliberate quit must never cut a playing user's connection; a game still up
    /// keeps its proxy, and the pidfile reap on the next launch covers that leftover. The stop itself is
    /// graceful (SIGTERM, then SIGKILL only on timeout — see HermesProxyRunner).</summary>
    private async Task QuitAppAsync()
    {
        _shuttingDown = true;
        try
        {
            var session = _host?.Services.GetService<WowLauncher.Services.Platform.IGameSession>();
            if (session is not null) await session.StopProxyIfNoGameAsync();
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Stopping the realm proxy on quit failed"); }
        try { if (_trayIcon is not null) _trayIcon.IsVisible = false; }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Hiding the tray icon on quit failed"); }
        await StopAgentControlAsync();
        _desktop?.Shutdown();
    }

    /// <summary>
    /// Starts the loopback control surface — only when <c>--agent-control</c> was passed.
    ///
    /// <para>🔴 Never on a normal run. A failure here must not cost the player his launcher, so it is
    /// contained: a port already taken, a read-only state directory or a blocked socket downgrades to a
    /// log line, and the app carries on exactly as if the switch had been absent.</para>
    /// </summary>
    private void StartAgentControlIfRequested(ShellViewModel shell, string[] args)
    {
        Services.AgentControl.AgentControlOptions options;
        try
        {
            options = Services.AgentControl.AgentControlOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            // A malformed port is the operator's mistake, and staying silent about it would send a
            // harness looking for a door that was never opened.
            Serilog.Log.Error("Agent control not started: {Reason}", ex.Message);
            return;
        }

        if (!options.Enabled) return;

        try
        {
            var surface = new Services.AgentControl.PlayViewModelAgentSurface(shell.Play);
            var server = new Services.AgentControl.AgentControlServer(surface, Serilog.Log.Logger);
            server.Start(options.Port);

            var stateDir = _host!.Services
                .GetRequiredService<Services.Platform.IAppPaths>().StateDir;
            var handshake = Services.AgentControl.AgentControlHandshake.Write(
                stateDir, server.Port, server.Token);

            _agentControl = server;
            _agentControlStateDir = stateDir;

            // Auch wenn der Weg nicht ueber QuitApp fuehrt, darf keine Datei zurueckbleiben, die
            // einen toten Port bewirbt.
            //
            // ProcessExit allein genuegt nicht: unter Avalonia auf Linux beendet SIGTERM den Prozess,
            // ohne dass der Handler noch laeuft (gemessen 2026-08-24 — die Datei lag danach weiterhin
            // da). Deshalb zusaetzlich die Signale selbst. Gegen SIGKILL hilft beides nicht; dafuer
            // traegt die Datei die Prozessnummer, und der Client prueft sie, statt ihr zu glauben
            // (siehe (internal design notes, not published)).
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                Services.AgentControl.AgentControlHandshake.Delete(stateDir);

            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
            {
                // Nicht Cancel setzen: das Signal soll weiterhin beenden, es soll nur vorher
                // aufgeraeumt werden.
                _agentControlSignals.Add(PosixSignalRegistration.Create(signal,
                    _ => Services.AgentControl.AgentControlHandshake.Delete(stateDir)));
            }

            Serilog.Log.Information("Agent control ready — handshake at {Path}", handshake);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Agent control could not start — continuing without it");
            _agentControl = null;
        }
    }

    /// <summary>Tears the surface down and removes the handshake file, so nothing advertises a dead
    /// port. Never throws: a quit must not fail over a test hook.</summary>
    private async Task StopAgentControlAsync()
    {
        if (_agentControl is null) return;

        try { await _agentControl.DisposeAsync(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Stopping the agent control surface failed"); }

        if (_agentControlStateDir is not null)
            Services.AgentControl.AgentControlHandshake.Delete(_agentControlStateDir);

        _agentControl = null;
    }

    /// <summary>
    /// The close button (the titlebar cross calls Window.Close, and a window-manager close raises the
    /// same event) is intercepted here. It never closes the window directly: it is cancelled and the
    /// saved preference decides. Ask shows the prompt; Background hides to the tray; Quit ends the app.
    /// </summary>
    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shuttingDown) return;              // QuitApp is closing us on purpose - let it through
        e.Cancel = true;                        // we, not the window manager, decide what close means
        if (sender is not Window window) return;

        // No tray to hide into → the only safe close is a real quit (never orphan an invisible process).
        if (!_trayAvailable) { await QuitAppAsync(); return; }

        var resolution = CloseBehavior.Resolve(_closePref!.Load());
        if (resolution == CloseBehavior.Resolution.Prompt)
        {
            var answer = await new ClosePromptWindow().ShowDialog<ClosePromptResult?>(window);
            if (answer is null) return;         // dismissed (Escape / WM close) → stay exactly as we are
            var (act, persist) = CloseBehavior.FromPrompt(answer.KeepRunning, answer.Remember);
            if (persist is { } p) _closePref.Save(p);
            resolution = act;
        }

        if (resolution == CloseBehavior.Resolution.Background) window.Hide();
        else await QuitAppAsync();
    }

    /// <summary>
    /// QA-Gate (§10): `WowLauncher.exe --screenshot &lt;pfad&gt; [--section play|patch|armory|addons|settings]`
    /// rendert das Fenster headless (Skia/CPU, kein GPU-Swapchain nötig) als PNG und beendet sich.
    /// </summary>
    private static void HandleScreenshotMode(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        var args = desktop.Args ?? [];
        var idx = Array.IndexOf(args, "--screenshot");
        if (idx < 0 || idx + 1 >= args.Length) return;

        var path = args[idx + 1];

        var secIdx = Array.IndexOf(args, "--section");
        if (secIdx >= 0 && secIdx + 1 < args.Length && window.DataContext is ShellViewModel svm)
        {
            svm.Selected = args[secIdx + 1].ToLowerInvariant() switch
            {
                "patch" or "patchnotes" => ShellViewModel.Section.PatchNotes,
                "armory" => ShellViewModel.Section.Armory,
                "addons" => ShellViewModel.Section.Addons,
                "settings" => ShellViewModel.Section.Settings,
                _ => ShellViewModel.Section.Play,
            };
        }

        // Optional --realm <id> (v3): seed the selected realm before the render so a single realm
        // view (art + header) can be shot. Additive — unknown/absent id keeps the default realm.
        var realmIdx = Array.IndexOf(args, "--realm");
        if (realmIdx >= 0 && realmIdx + 1 < args.Length && window.DataContext is ShellViewModel rvm)
        {
            var realmId = args[realmIdx + 1].ToLowerInvariant();
            var realm = rvm.Realms.FirstOrDefault(r => r.Id == realmId);
            if (realm is not null) rvm.SelectedRealm = realm;
        }

        // QA-only: --client <key> drives the hero's client toggle (RealmEntry.HasMultipleClients) the
        // same way a real click does, so a state-shot can show a build that has no local install
        // without needing an actual second client on the render machine.
        var clientIdx = Array.IndexOf(args, "--client");
        if (clientIdx >= 0 && clientIdx + 1 < args.Length && window.DataContext is ShellViewModel cvm)
        {
            var key = args[clientIdx + 1];
            var pick = cvm.SelectedRealm?.AvailableClients.FirstOrDefault(c => c.Key == key);
            if (pick is not null) _ = cvm.SelectRealmClientCommand.ExecuteAsync(pick);
        }

        // QA-only: --section <play|armory|addons|settings|account> öffnet einen Bereich der Hülle,
        // wie ein Klick auf den Reiter. Ohne das ist ein Standbild nur von der Spielseite zu haben,
        // und genau die Bereiche, die selten aufgemacht werden, sind die, in denen ein Fehler lange
        // unbemerkt bleibt (Arsenal, Einstellungen). "splash" wird weiter oben abgefangen und kommt
        // hier nie an. Ein unbekannter Name lässt den Bereich, wo er ist — ein Tippfehler soll ein
        // Bild liefern, nicht einen Absturz.
        var sectionName = SectionArg(args);
        if (sectionName.Length > 0 && window.DataContext is ShellViewModel secvm)
        {
            switch (sectionName)
            {
                case "armory":   secvm.GoArmoryCommand.Execute(null); break;
                case "addons":   secvm.GoAddonsCommand.Execute(null); break;
                case "settings": secvm.GoSettingsCommand.Execute(null); break;
                case "account":  secvm.GoAccountCommand.Execute(null); break;
                case "register": secvm.GoRegisterCommand.Execute(null); break;
                case "play":     secvm.GoPlayCommand.Execute(null); break;
            }
        }

        // Optional --size WxH: QA-Render über verschiedene Auflösungen. Treibt direkt die
        // Measure/Arrange/Bitmap-Größe (NICHT window.Width — das clampt der headless-Screen).
        double renderW = window.Width, renderH = window.Height;
        var sizeIdx = Array.IndexOf(args, "--size");
        if (sizeIdx >= 0 && sizeIdx + 1 < args.Length)
        {
            var parts = args[sizeIdx + 1].Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
            {
                window.WindowState = WindowState.Normal;
                renderW = w;
                renderH = h;
            }
        }

        // Optional --state <name>: QA-Render eines bestimmten ActionBar-Zustands (z.B. downloading),
        // gesetzt NACH InitAsync (im Render-Callback), damit Init ihn nicht überschreibt.
        var stateIdx = Array.IndexOf(args, "--state");
        var seedState = stateIdx >= 0 && stateIdx + 1 < args.Length ? args[stateIdx + 1].ToLowerInvariant() : null;

        // QA-only demo switch: the add-realm overlay only shows on a real click, so a state-shot needs
        // a way to force it open (mutation/screenshot-gate requirement, no other caller sets this).
        if (args.Contains("--open-add-realm") && window.DataContext is ShellViewModel avm)
            avm.IsAddRealmOpen = true;

        // Same for the gear: the dialog in its edit mode, for the realm selected with --realm.
        if (args.Contains("--open-edit-realm") && window.DataContext is ShellViewModel evm
            && evm.OpenEditRealmCommand.CanExecute(null))
            evm.OpenEditRealmCommand.Execute(null);

        // QA-only: einen angemeldeten Zustand zeichnen. Gebaut am 2026-08-05, als der Addon-Katalog
        // hinter den Login wanderte: alles, was nur Angemeldete sehen, war damit unsichtbar fuer den
        // Renderpfad - und ein Bildschirm, den ich nicht ansehen kann, ist einer, den ich nicht
        // pruefen kann. Genau dieselbe Luecke hat am 2026-08-04 drei Anzeigefehler durchgelassen,
        // die alle Tests bestanden hatten.
        //
        // Faelscht ausschliesslich die Anzeige. Kein Token, keine Anfrage, kein Konto - der Dienst
        // bleibt abgemeldet, also kann daraus nie ein "es geht doch" werden, das in Wirklichkeit
        // an einer echten Anmeldung haengt.
        if (args.Contains("--signed-in") && window.DataContext is ShellViewModel lvm)
        {
            lvm.IsLoggedIn = true;
            lvm.Addons.SignedOut = false;
        }

        window.Show();

        // QA-State VOR dem Render-Timer seeden → Bindings/Layout haben die vollen 3s zum Settlen.
        if (seedState is not null && window.DataContext is ShellViewModel sv)
            SeedQaState(sv.Play, seedState);

        // Layout + async InitAsync abwarten, dann einen Frame rendern.
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                // QA-only --scroll end: alles, was unter dem Falz liegt, ins Bild holen. Das Fenster
                // hat eine feste Hoehe, --size vergroessert nur die Leinwand - eine laengere Seite
                // (die Einstellungen sind laenger als das Fenster) blieb damit unpruefbar. Ein
                // Bildschirm, den ich nicht ansehen kann, ist einer, den ich nicht pruefen kann;
                // genau diese Luecke hat am 2026-08-04 drei Anzeigefehler durchgelassen.
                if (args.Contains("--scroll"))
                {
                    var where = Array.IndexOf(args, "--scroll") + 1;
                    var toEnd = where >= args.Length || args[where].Equals("end", StringComparison.OrdinalIgnoreCase);
                    foreach (var sv in window.GetVisualDescendants().OfType<ScrollViewer>())
                    {
                        if (toEnd) sv.ScrollToEnd();
                        else if (double.TryParse(args[where], out var y))
                            sv.Offset = sv.Offset.WithY(y);
                    }
                    window.UpdateLayout();
                }

                var size = new PixelSize((int)renderW, (int)renderH);
                using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
                window.Measure(new Size(renderW, renderH));
                window.Arrange(new Rect(0, 0, renderW, renderH));
                rtb.Render(window);
                rtb.Save(path);
            }
            finally
            {
                Environment.Exit(0);
            }
        }, TimeSpan.FromSeconds(3));
    }

    /// <summary>QA-only: seed a representative ActionBar state for a screenshot (no live download needed).</summary>
    private static void SeedQaState(WowLauncher.ViewModels.PlayViewModel play, string state)
    {
        switch (state)
        {
            case "downloading":
                play.DownloadProgress = 43;
                // Same catalog as the live path, so the QA shot shows the shipped wording.
                play.DownloadDetail = Localization.Loc.F("Play_Detail_Progress",
                    "2106", "4912", "8.2");
                play.State = WowLauncher.Models.LauncherState.Downloading;
                break;
            case "update":
                play.State = WowLauncher.Models.LauncherState.UpdateAvailable;
                break;
            case "error":
                play.DownloadErrorDetail = Localization.Loc.T("Download_Fail_Network");
                play.State = WowLauncher.Models.LauncherState.DownloadError;
                break;
        }
    }
}
