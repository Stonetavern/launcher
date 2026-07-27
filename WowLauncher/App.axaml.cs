using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _host = DependencyInjection.BuildHost([]);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var shell = _host.Services.GetRequiredService<ShellViewModel>();

            // Three skins, one binary. v3 is what ships (default); v1 and v2 stay reachable via
            // `--ui v1` / `--ui v2`. Same ViewModels throughout, so they compare on one machine.
            Window window = Ui.V2 ? new ShellV2Window { DataContext = shell }
                : Ui.V1 ? new MainWindow { DataContext = shell }
                : new ShellV3Window { DataContext = shell };
            desktop.MainWindow = window;

            // Fire-and-observe: kein Blocking-IO im Konstruktor (§10, ≤ 800ms bis interaktiv).
            // QA-State-Screenshot (--state) seedet den Zustand selbst → InitAsync NICHT starten,
            // sonst überschreibt/blockiert dessen async Manifest/Realm-Pfad den geseedeten Zustand.
            var args = desktop.Args ?? [];
            var qaState = args.Contains("--screenshot") && args.Contains("--state");
            if (!qaState)
                _ = Dispatcher.UIThread.InvokeAsync(shell.InitAsync);

            // The tray + close-to-background feature is for the interactive app only. A --screenshot
            // render seeds state and Environment.Exit(0)s without ever closing a window, so it neither
            // needs the tray nor the explicit-shutdown mode (which would otherwise keep the process
            // alive after the render). --e2e never reaches Avalonia at all (Program.cs).
            if (!args.Contains("--screenshot"))
            {
                _closePref = _host.Services.GetRequiredService<IClosePreferenceService>();
                // Hiding the window must NOT end the app (the default OnLastWindowClose would quit the
                // moment the window disappears into the tray). We drive shutdown ourselves from QuitApp.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                SetUpTrayIcon();
                window.Closing += OnMainWindowClosing;

                // Wire the hide-to-tray / restore seam the Play flow uses — but only when a real tray
                // exists (otherwise a hidden window could never be brought back, so PlayViewModel keeps
                // its hard-exit fallback). The callbacks marshal to the UI thread themselves, so the
                // controller and the ViewModel stay free of any window/dispatcher reference.
                if (_trayAvailable)
                {
                    var controller = _host.Services.GetRequiredService<IShellWindowController>() as ShellWindowController;
                    controller?.Configure(
                        hideToTray: () => Dispatcher.UIThread.Post(window.Hide),
                        restoreFromTray: () => Dispatcher.UIThread.Post(ShowMainWindow));
                }

                // Automatic one-time AppImage first-run setup (owner UX 2026-07-24): relocate into
                // ~/Applications, drop a trusted desktop shortcut, register the menu entry. Runs off the
                // UI thread (pure file IO, self-contained error handling) so it never blocks the ≤800ms
                // interactive budget; a no-op off an AppImage / after the first run / on non-Linux.
                var firstRun = _host.Services.GetRequiredService<Services.Platform.IFirstRunSetup>();
                _ = Task.Run(() => firstRun.RunAsync());
            }

            HandleScreenshotMode(desktop, window);
        }

        base.OnFrameworkInitializationCompleted();
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
                Icon = new WindowIcon(AssetLoader.Open(
                    new Uri("avares://WowLauncher/Assets/lantern.png"))),
                ToolTipText = Loc.T("Tray_Tooltip"),
                IsVisible = true,
                // Left-click primary action (Windows/macOS): bring the launcher back.
                Command = new RelayCommand(ShowMainWindow),
                Menu = new NativeMenu { Items = { open, quit } },
            };

            TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
            _trayAvailable = true;
        }
        catch (Exception ex)
        {
            // No tray host on this machine: keep running with a normal window, never crash. Close then
            // means quit (see OnMainWindowClosing), so the window is never hidden into nothing.
            _trayAvailable = false;
            Serilog.Log.Warning(ex, "System tray unavailable - close will quit instead of hiding");
        }
    }

    /// <summary>Bring the launcher back from the tray: visible, un-minimised, focused. The v3 shell
    /// re-arms its motion gate off the IsVisible/WindowState change, so animations resume here.</summary>
    private void ShowMainWindow()
    {
        if (_desktop?.MainWindow is not { } window) return;
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
        _desktop?.Shutdown();
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
    /// QA-Gate (§10): `WowLauncher.exe --screenshot &lt;pfad&gt; [--section play|patch|armory|settings]`
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

        window.Show();

        // QA-State VOR dem Render-Timer seeden → Bindings/Layout haben die vollen 3s zum Settlen.
        if (seedState is not null && window.DataContext is ShellViewModel sv)
            SeedQaState(sv.Play, seedState);

        // Layout + async InitAsync abwarten, dann einen Frame rendern.
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
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
