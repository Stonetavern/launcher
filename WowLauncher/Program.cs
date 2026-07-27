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

        // Which skin: v1 (default) or v2 "Obsidian Instrument" (`--ui v2`). Decided before the
        // ViewModels are built, because the telemetry column asks Ui.Demo at construction time.
        Ui.Configure(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
            .With(new X11PlatformOptions { WmClass = "stonetavern-launcher" })
            .WithInterFont()
            .LogToTrace();
}
