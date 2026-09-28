using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using WowLauncher.Infrastructure;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;

namespace WowLauncher;

sealed class Program
{
    // WP3: resolved once so the crash-shield (which is armed before the host is built) writes the
    // crash log to the same place as everything else — StateDir (Linux XDG, Windows next-to-exe).
    private static readonly IAppPaths Paths = AppPaths.ForCurrentOs();

    [STAThread]
    public static void Main(string[] args)
    {
        // The first-frame budget of the 1.9 login shell (Spec §4) is measured from here.
        Startup.StartupClock.Start();

        // Global crash-shield (§7 / CLAUDE.md §7): no naked stack trace ever reaches the player.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        // Headless E2E smoke test (no GUI): exercise the full managed download path against the
        // live manifest — fetch → resolve client URL → download → SHA256 verify → extract → locate
        // WoW.exe (incl. single-wrapping-folder client ZIPs). Proves the launcher↔distribution
        // wiring end-to-end. `--e2e [destDir]`; exit 0 = pass, non-zero = fail.
        if (args.Contains("--e2e"))
        {
            Environment.Exit(RunE2EAsync(args).GetAwaiter().GetResult());
            return;
        }

        // Startvorbereitung als Frage, nicht als Nebenwirkung: `--preflight` sagt, ob ein Start hier
        // gelingen kann, und gibt genau den Satz aus, den ein Spieler zu lesen bekaeme.
        //
        // 🔴 Gebaut, weil der erste Lauf in einer nackten VM (2026-08-05) genau diese Meldung NICHT
        // pruefen konnte: sie entsteht erst durch einen Druck auf Spielen, und der war in der VM mit
        // keinem Mittel ausloesbar. Eine Auskunft, die nur ein Mensch mit einer Maus hervorlocken
        // kann, ist auf jedem Prueftstand unsichtbar - und damit unbelegt.
        //
        // Startet nichts. Exit 0 = startklar, 1 = nicht startklar (Grund auf stdout), 2 = kein Client.
        if (args.Contains("--preflight"))
        {
            Environment.Exit(RunPreflightAsync(args).GetAwaiter().GetResult());
            return;
        }

        // The Stonetavern folder: new player, existing player, the copy a setup started, or an old
        // download of a player who set the folder up. Read-only, before anything loads the config
        // (loading writes a default one and would erase the evidence). A stray old copy hands over to
        // the launcher in the folder and quits, before a window exists. QA stills never get here.
        if (!args.Contains("--screenshot"))
        {
            Library = LibraryClassifier.Decide(LibraryProbe.Read(Paths, args));
            if (Library.Kind == LibraryDecisionKind.Forward && Library.Target is { } target && Forward(target, args))
                return;

            // One launcher per player (SingleInstance): a second start brings the running one to the
            // front (out of the tray too) and quits, instead of a second window fighting the first over
            // the config and the realm proxy. Fails open: no answer within 3 s, start as always.
            var instance = SingleInstance.ForCurrentUser(Paths.StateDir);
            if (!instance.TryAcquire())
            {
                if (instance.SignalFirst(TimeSpan.FromSeconds(3)))
                {
                    WriteForwardNote("the running launcher (brought to the front)");
                    return;
                }
            }
            SingleInstance.Current = instance;
        }

        // Which skin: v1 (default) or v2 "Obsidian Instrument" (`--ui v2`). Decided before the
        // ViewModels are built, because the telemetry column asks Ui.Demo at construction time.
        Ui.Configure(args);

        // 🔴 Bevor Avalonia es versucht: kann hier ueberhaupt ein Fenster aufgehen? Ohne diese
        // Pruefung endet der Start mit "System.Exception: XOpenDisplay failed" und acht Zeilen
        // Spurabzug - gemessen beim ersten Lauf in der nackten Ubuntu-VM am 2026-08-05. Das ist ein
        // Text fuer Entwickler, und er widerspricht der eigenen Regel: kein nackter Spurabzug
        // erreicht je einen Spieler. Abgestuerzt ist dabei gar nichts; es fehlte ein Bildschirm.
        static string? Env(string name) => Environment.GetEnvironmentVariable(name);
        if (GraphicalSession.Missing(Env, OperatingSystem.IsLinux()) is { } missing)
        {
            Console.Error.WriteLine(missing);
            Environment.Exit(3);
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (OperatingSystem.IsLinux() && IsDisplayFailure(ex))
        {
            // Der haeufigere Fall: eine Anzeige ist ANGEGEBEN, laesst sich aber nicht oeffnen (ueber
            // SSH gestartet, als Dienst gestartet, fehlende Berechtigung). Die Umgebung steht mit im
            // Text, weil sie die halbe Diagnose ist - und wer das liest, hat kein Fenster, in dem er
            // etwas nachschlagen koennte.
            Console.Error.WriteLine(GraphicalSession.Refused(Env, ex.Message));
            WriteCrashLog(ex, "no usable display");
            Environment.Exit(3);
        }
    }

    /// <summary>What the start found about the Stonetavern folder (see <see cref="LibraryClassifier"/>).
    /// Legacy (the start as before) unless Main decided otherwise.</summary>
    internal static LibraryDecision Library { get; private set; } =
        new(LibraryDecisionKind.Legacy, Reason: "not decided (QA or test)");

    /// <summary>Start the launcher in the Stonetavern folder with the same arguments and quit. False
    /// when it cannot be started: then this copy runs as it would have, never nothing at all.</summary>
    private static bool Forward(string target, string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(target) ?? "",
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // The launcher in the folder must not inherit this copy's open files: the AppImage runtime
            // keeps a handle on its own mount, and passing it on keeps this copy mounted for the whole
            // session (measured on the setup handoff, 2026-09-28).
            WowLauncher.Services.Platform.InheritedFds.KeepFromChildren();
            if (System.Diagnostics.Process.Start(psi) is null) return false;
            WriteForwardNote(target);
            return true;
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex, $"forward to {target}");
            Library = new(LibraryDecisionKind.Legacy, Reason: "forward failed");
            return false;
        }
    }

    /// <summary>One line in the state dir, so a support look at the old copy shows where it went.</summary>
    private static void WriteForwardNote(string target)
    {
        try
        {
            Directory.CreateDirectory(Paths.StateDir);
            File.AppendAllText(Path.Combine(Paths.StateDir, "launcher-forward.log"),
                $"{DateTimeOffset.Now:O} forwarded to {target}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // A note, not a requirement.
        }
    }

    /// <summary>
    /// Ob dieser Fehlschlag daher kommt, dass keine Anzeige zu bekommen war.
    ///
    /// <para>An der Meldung erkannt und nicht am Typ, weil Avalonia hier eine nackte
    /// <see cref="Exception"/> wirft. Bewusst eng gehalten: was NICHT sicher eine Anzeigefrage ist,
    /// muss weiter durchschlagen und im Absturzprotokoll landen. Ein zu weiter Fang wuerde echte
    /// Fehler in eine beruhigende Meldung ueber Bildschirme verwandeln.</para>
    /// </summary>
    private static bool IsDisplayFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message ?? "";
            if (m.Contains("XOpenDisplay", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Could not initialize GTK", StringComparison.OrdinalIgnoreCase)
                || m.Contains("wl_display", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Kann hier gestartet werden, und wenn nicht: was stuende auf dem Bildschirm?
    ///
    /// <para>Fragt genau die Kette, die der Druck auf Spielen auch fragt - denselben Launcher, dieselbe
    /// Bereitschaftspruefung, dieselben Saetze. Ein zweiter, eigener Pfad waere hier das Gegenteil
    /// eines Beweises: er koennte gelingen, waehrend der echte scheitert.</para>
    /// </summary>
    private static async Task<int> RunPreflightAsync(string[] args)
    {
        try
        {
            using var host = DependencyInjection.BuildHost(args);
            var cfg = host.Services.GetRequiredService<IConfigService>().Load();
            var realm = RealmRegistry.All(cfg).FirstOrDefault(r => r.Id == cfg.SelectedRealmId);
            var client = realm?.Client ?? ClientVersion.Default;

            Console.WriteLine($"Realm: {realm?.Name ?? "none"} ({cfg.RealmlistAddress})");
            Console.WriteLine($"Client: {client.PreciseLabel}");

            if (!cfg.ClientInstalls.TryGetValue(client.Build, out var dir) || string.IsNullOrWhiteSpace(dir))
            {
                Console.WriteLine("No client is registered for this build. Nothing to start yet.");
                return 2;
            }
            Console.WriteLine($"Client folder: {dir}");

            // Derselbe Launcher, den der Start nimmt - inklusive der Weiche zwischen 1.12.1 und 1.14.2.
            var launcher = host.Services.GetRequiredService<IGameLauncher>();
            var problem = await launcher.CheckReadyAsync(client.ExeName);
            if (problem is null)
            {
                Console.WriteLine("Ready to start.");
                return 0;
            }

            Console.WriteLine("NOT ready to start. This is what a player would read:");
            Console.WriteLine();
            Console.WriteLine(problem);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Preflight failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunE2EAsync(string[] args)
    {
        void Say(string m) => Console.WriteLine($"[e2e] {m}");
        try
        {
            using var host = DependencyInjection.BuildHost(args);
            var manifestSvc = host.Services.GetRequiredService<IManifestService>();
            var download = host.Services.GetRequiredService<IDownloadService>();
            var client = host.Services.GetRequiredService<IClientService>();
            var paths = host.Services.GetRequiredService<IAppPaths>();

            Say("Fetching manifest…");
            var manifest = await manifestSvc.FetchAsync();
            if (manifest is null) { Say("FAIL: manifest fetch returned null"); return 2; }
            Say($"Manifest v{manifest.CurrentVersion}, active_phase={manifest.ActivePhase}");

            var phase = manifest.Phases.FirstOrDefault(p => p.Phase == manifest.ActivePhase);
            var url = phase?.Client?.Url ?? manifest.Base?.Url ?? "";
            var sha = phase?.Client?.Sha256 ?? manifest.Base?.Sha256 ?? "";
            if (string.IsNullOrWhiteSpace(url)) { Say("FAIL: no client URL in manifest"); return 3; }
            Say($"Client URL: {url}");

            var idx = Array.IndexOf(args, "--e2e");
            var destDir = idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--")
                ? args[idx + 1]
                : Path.Combine(paths.ShareDir, "e2e-client");
            var zipPath = Path.Combine(paths.CacheDir, "e2e-download.zip");
            Say($"Download → {zipPath}");

            var dl = await download.DownloadFileAsync(url, zipPath);
            if (!dl.Ok) { Say($"FAIL: download — {dl.UserMessage}"); return 4; }
            Say($"Downloaded {new FileInfo(zipPath).Length / 1_048_576} MB");

            Say("Verifying SHA256…");
            if (string.IsNullOrWhiteSpace(sha)) { Say("FAIL: the manifest carries no SHA256 for the client, stopping here (an unverifiable artifact is never extracted)"); return 5; }
            if (!await download.VerifyHashAsync(zipPath, sha)) { Say("FAIL: SHA256 mismatch"); return 5; }
            Say("SHA256 OK");

            Say($"Extract → {destDir}");
            if (!await download.ExtractClientAsync(zipPath, destDir)) { Say("FAIL: extract"); return 6; }

            var exe = client.FindWowExe(destDir);
            if (string.IsNullOrEmpty(exe)) { Say("FAIL: WoW.exe not found after extract"); return 7; }
            Say($"PASS: WoW.exe located at {exe}");
            var build = client.DetectBuild(Path.GetDirectoryName(exe)!);
            Say($"DetectBuild → {(build?.ToString() ?? "unknown")}");

            try { File.Delete(zipPath); } catch { /* regenerable */ }
            return 0;
        }
        catch (Exception ex)
        {
            Say($"FAIL: exception — {ex.Message}");
            return 1;
        }
    }

    private static void WriteCrashLog(Exception? ex, string source)
    {
        try
        {
            Directory.CreateDirectory(Paths.StateDir);
            var path = Path.Combine(Paths.StateDir, "launcher_crash.log");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* nichts, was wir hier sinnvoll tun könnten */ }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Linux taskbar identity. The window icon alone is not enough on Wayland: the compositor
            // matches a window to its .desktop entry by app id, and without a match KDE/GNOME show a
            // placeholder instead of our mark. The id equals the .desktop file name written by
            // deploy/package-linux.sh (integration/stonetavern-launcher.desktop). X11 uses the same
            // string as WM_CLASS, so one value serves both.
            .With(new X11PlatformOptions { WmClass = DesktopIntegration.WmClass })
            .WithInterFont()
            .LogToTrace();
}
