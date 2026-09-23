using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Der Werkzeugkasten, den der Mac zum Zeichnen braucht, wird jetzt vom Launcher geholt statt vom
/// Spieler. Diese Tests decken die Faelle ab, in denen genau das schiefgehen kann, OHNE dass etwas
/// rot wird -- die Sorte Fehler, die dieses Projekt schon zweimal einen Tag gekostet hat:
///
///   * ein Archiv mit falscher Pruefsumme wird trotzdem ausgepackt
///   * "tar hat 0 gemeldet" wird als Beweis genommen, dass die Umgebung laeuft
///   * ein halb geladenes Archiv bleibt liegen und wird bei jedem Start wiederverwendet
///
/// Alle Tests laufen ohne Mac, ohne Netz und ohne 260 MB: Prozessaufrufe und die Suche nach dem
/// Wine-Programm sind eingehaengt.
/// </summary>
public class GameRuntimeProvisionerTests
{
    private static readonly Serilog.ILogger Log = Serilog.Log.Logger;
    private const string Sha = "aaaabbbbccccddddeeeeffff00001111222233334444555566667777888899990";

    // ── Doubles ──────────────────────────────────────────────────────────────────────────────

    private sealed class TempPaths : IAppPaths, IDisposable
    {
        public string Root { get; }
        public TempPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), "stv-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public string ConfigDir => Root;
        public string StateDir => Root;
        public string CacheDir => Path.Combine(Root, "cache");
        public string LogDir => Root;
        public string ShareDir => Path.Combine(Root, "share");
        public string ConfigFilePath => Path.Combine(Root, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(Root, "news-cache.json");
        public string ClientInstallDir(int b) => Path.Combine(Root, $"c{b}");
        public string ClientDownloadZip(int b) => Path.Combine(Root, $"c{b}.zip");
        public void EnsureDirectories() { Directory.CreateDirectory(CacheDir); Directory.CreateDirectory(ShareDir); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    /// <summary>Schreibt eine Platzhalterdatei und beantwortet die Pruefsummenfrage nach Vorgabe.</summary>
    private sealed class FakeDownloads : IDownloadService
    {
        public bool HashOk = true;
        public int Downloads;
        public int HashChecks;
        public DownloadResult Result = DownloadResult.Success;

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Downloads++;
            if (Result.Ok)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.WriteAllText(destPath, "archiv");
            }
            return Task.FromResult(Result);
        }

        public Task<bool> VerifyHashAsync(string path, string expected, CancellationToken ct = default)
        {
            HashChecks++;
            return Task.FromResult(HashOk && File.Exists(path));
        }

        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null,
            CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ExtractClientAsync(string z, string d, IProgress<string>? p = null,
            CancellationToken ct = default) => Task.FromResult(true);
    
        /// <summary>Pflichtteil der Schnittstelle: ohne Grund gilt der Fehlschlag als nicht behebbar,
        /// also als kaputtes Paket. Das ist die sichere Richtung fuer eine Attrappe.</summary>
        public async System.Threading.Tasks.Task<WowLauncher.Models.ExtractOutcome> ExtractClientWithReasonAsync(
            string zipPath, string destDir, bool freshInstall,
            System.IProgress<string>? progress = null,
            System.Threading.CancellationToken ct = default) =>
            await ExtractClientAsync(zipPath, destDir, progress, ct).ConfigureAwait(false)
                ? WowLauncher.Models.ExtractOutcome.Success
                : WowLauncher.Models.ExtractOutcome.Fail(WowLauncher.Models.ExtractFailure.Unknown);
    }

    /// <summary>Zeichnet auf, welche Programme mit welchen Argumenten aufgerufen wurden.</summary>
    private sealed class Recorder
    {
        public readonly List<string> Calls = [];
        public readonly Dictionary<string, int> ExitCodes = [];
        public Action<string>? OnCall;

        public Task<int> Run(string exe, IReadOnlyList<string> args, CancellationToken ct)
        {
            var name = Path.GetFileName(exe);
            Calls.Add(name + " " + string.Join(" ", args));
            OnCall?.Invoke(name);
            return Task.FromResult(ExitCodes.TryGetValue(name, out var code) ? code : 0);
        }
    }

    /// <summary>Baut einen Provisioner, bei dem das Wine-Programm erst existiert, wenn
    /// <paramref name="recorder"/> das Auspacken gemeldet hat -- so wie in der Wirklichkeit.</summary>
    private static MacGptkProvisioner Build(
        TempPaths paths, FakeDownloads downloads, Recorder recorder,
        bool wineAppearsAfterExtract = true, bool winePresentFromStart = false,
        bool appleSilicon = false)
    {
        var winePath = Path.Combine(paths.ShareDir, "gptk", "wine64");
        if (winePresentFromStart)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(winePath)!);
            File.WriteAllText(winePath, "wine");
        }
        recorder.OnCall = name =>
        {
            if (name == "tar" && wineAppearsAfterExtract)
            {
                // tar legt sowohl das Bundle als auch das Programm an
                Directory.CreateDirectory(Path.Combine(paths.ShareDir, "gptk", "Game Porting Toolkit.app"));
                Directory.CreateDirectory(Path.GetDirectoryName(winePath)!);
                File.WriteAllText(winePath, "wine");
            }
        };
        return new MacGptkProvisioner(
            downloads, paths, Log, "https://example.invalid/gptk.tar.xz", Sha,
            recorder.Run,
            _ => File.Exists(winePath) ? winePath : null,
            () => appleSilicon);
    }

    // ── Die Faelle ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IstDerWerkzeugkastenSchonDa_wirdNichtsGeladen()
    {
        using var paths = new TempPaths();
        var dl = new FakeDownloads();
        var rec = new Recorder();
        var sut = Build(paths, dl, rec, winePresentFromStart: true);

        var result = await sut.EnsureAsync();

        Assert.True(result.Ok);
        Assert.Equal(0, dl.Downloads);
        Assert.Empty(rec.Calls);
    }

    /// <summary>🔴 Der wichtigste Test. Ein Archiv, dessen Pruefsumme nicht stimmt, darf NIEMALS
    /// ausgepackt werden -- und es muss verschwinden, sonst prueft der naechste Start dieselbe
    /// kaputte Datei und scheitert wieder an derselben Stelle, fuer immer.</summary>
    [Fact]
    public async Task PasstDiePruefsummeNicht_wirdNichtsAusgepacktUndDieDateiVerschwindet()
    {
        using var paths = new TempPaths();
        var dl = new FakeDownloads { HashOk = false };
        var rec = new Recorder();
        var sut = Build(paths, dl, rec);

        var result = await sut.EnsureAsync();

        Assert.False(result.Ok);
        Assert.DoesNotContain(rec.Calls, c => c.StartsWith("tar", StringComparison.Ordinal));
        var archive = Path.Combine(paths.CacheDir, "game-porting-toolkit-3.0-2.tar.xz");
        Assert.False(File.Exists(archive));
    }

    /// <summary>Beide Schritte gegen Apples Sperre muessen laufen, und in dieser Reihenfolge. Fehlt
    /// einer, liegt der Werkzeugkasten da, sieht installiert aus und startet nicht -- die
    /// schlimmste Form, weil jede Pfadpruefung gruen bleibt.</summary>
    [Fact]
    public async Task NachDemAuspacken_laufenEntsperrenUndNeuSignieren()
    {
        using var paths = new TempPaths();
        var dl = new FakeDownloads();
        var rec = new Recorder();
        var sut = Build(paths, dl, rec);

        var result = await sut.EnsureAsync();

        Assert.True(result.Ok);
        var order = rec.Calls.Select(c => c.Split(' ')[0]).ToList();
        Assert.Equal("tar", order[0]);
        Assert.Equal("xattr", order[1]);
        Assert.Equal("codesign", order[2]);
        Assert.Contains(rec.Calls, c => c.Contains("com.apple.quarantine", StringComparison.Ordinal));
    }

    /// <summary>🔴 Placebo-Sperre. "tar hat 0 gemeldet" ist KEIN Beweis, dass die Umgebung laeuft.
    /// Meldet jeder Schritt Erfolg, das Programm ist danach aber nicht da, muss der Lauf scheitern.</summary>
    [Fact]
    public async Task MeldenAlleWerkzeugeErfolg_dasProgrammFehltAber_scheitertDerLauf()
    {
        using var paths = new TempPaths();
        var dl = new FakeDownloads();
        var rec = new Recorder();
        var sut = Build(paths, dl, rec, wineAppearsAfterExtract: false);

        var result = await sut.EnsureAsync();

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    /// <summary>Die Umgebung wird gestartet, nicht nur gefunden. macOS kann eine Datei durchlassen
    /// und ihre Ausfuehrung trotzdem verweigern; dann ist sie vorhanden und nutzlos.</summary>
    [Fact]
    public async Task LaesstSichDasProgrammNichtStarten_scheitertDerLauf()
    {
        using var paths = new TempPaths();
        var dl = new FakeDownloads();
        var rec = new Recorder();
        rec.ExitCodes["wine64"] = 126;   // macOS verweigert die Ausfuehrung
        var sut = Build(paths, dl, rec);

        var result = await sut.EnsureAsync();

        Assert.False(result.Ok);
        Assert.Contains(rec.Calls, c => c.StartsWith("wine64", StringComparison.Ordinal));
    }

    /// <summary>Ein bereits geladenes UND geprueftes Archiv wird wiederverwendet. Bei 260 MB ist das
    /// der Unterschied zwischen "gleich nochmal" und "noch einmal von vorn".</summary>
    [Fact]
    public async Task LiegtEinGepruefteArchivSchonDa_wirdNichtErneutGeladen()
    {
        using var paths = new TempPaths();
        var dl = new FakeDownloads();
        var rec = new Recorder();
        Directory.CreateDirectory(paths.CacheDir);
        File.WriteAllText(Path.Combine(paths.CacheDir, "game-porting-toolkit-3.0-2.tar.xz"), "archiv");
        var sut = Build(paths, dl, rec);

        var result = await sut.EnsureAsync();

        Assert.True(result.Ok);
        Assert.Equal(0, dl.Downloads);
        Assert.Contains(rec.Calls, c => c.StartsWith("tar", StringComparison.Ordinal));
    }

    /// <summary>Bricht der Download ab, bleibt es beim Fehler -- und der Text sagt, wie es weitergeht,
    /// statt nur einen Zustand zu benennen.</summary>
    [Fact]
    public async Task ScheitertDerDownload_kommtEinSatzDerWeiterhilft()
    {
        using var paths = new TempPaths();
        var dl = new FakeDownloads { Result = DownloadResult.Fail(DownloadFailure.Network) };
        var rec = new Recorder();
        var sut = Build(paths, dl, rec);

        var result = await sut.EnsureAsync();

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(rec.Calls);
    }

    /// <summary>🔴 Ohne Rosetta laeuft der Werkzeugkasten nicht -- er ist ein Intel-Programm. Das muss
    /// auffallen, BEVOR 260 MB geladen werden, sonst wartet der Spieler vier Minuten auf eine
    /// Absage. Und die Meldung muss den Grund nennen: "Wine liess sich nicht starten" schickt
    /// Leute zu Homebrew, wie am 2026-08-24 geschehen.</summary>
    [Fact]
    public async Task FehltRosetta_wirdNichtsGeladenUndDerGrundStehtDrin()
    {
        using var paths = new TempPaths();
        var dl = new FakeDownloads();
        var rec = new Recorder();
        rec.ExitCodes["arch"] = 1;              // Rosetta nicht vorhanden
        var sut = Build(paths, dl, rec, appleSilicon: true);

        var result = await sut.EnsureAsync();

        Assert.False(result.Ok);
        Assert.Equal(0, dl.Downloads);
        Assert.DoesNotContain(rec.Calls, c => c.StartsWith("tar", StringComparison.Ordinal));
        Assert.Contains("Rosetta", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Die Umkehrung: meldet die Probe Erfolg, laeuft alles normal weiter. Ohne diesen Test
    /// wuerde eine Probe, die IMMER scheitert, oben gruen aussehen. Beide Faelle laufen ueber die
    /// eingehaengte Architektur-Antwort, also auch auf x64-Maschinen -- ein Test, der nur auf einem
    /// Mac etwas prueft, prueft in dieser Werkstatt nie etwas.</summary>
    [Fact]
    public async Task IstRosettaDa_laeuftDieBeschaffungGanzNormalDurch()
    {
        using var paths = new TempPaths();
        var dl = new FakeDownloads();
        var rec = new Recorder();          // arch liefert per Vorgabe 0
        var sut = Build(paths, dl, rec, appleSilicon: true);

        var result = await sut.EnsureAsync();

        Assert.True(result.Ok);
        Assert.Contains(rec.Calls, c => c.StartsWith("tar", StringComparison.Ordinal));
    }

    /// <summary>Windows und Linux fassen das gar nicht erst an.</summary>
    [Fact]
    public void AufAnderenSystemen_gibtEsNichtsZuBeschaffen()
    {
        var sut = new NoGameRuntimeProvisioner();
        Assert.False(sut.NeedsRuntime("WowClassic_ForCustomServers.exe"));
        Assert.True(sut.EnsureAsync().Result.Ok);
    }
}
