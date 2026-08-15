using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Woher der Launcher sein EIGENES Update holt — und woher ausdrücklich nicht.
///
/// <para>Befund vom 2026-08-05, gefunden beim Reparieren eines selbst angelegten Realms: das
/// Selbst-Update las dasselbe Manifest wie der Client, und das hängt am gewählten Realm. Wer einen
/// eigenen Realm auswählte, bekam damit <b>gar keine Launcher-Updates mehr</b> — ohne Fehler, ohne
/// Meldung. Der Launcher altert einfach, und zwar bei genau den Spielern, die eigene Realms anlegen
/// und Fehlerbehebungen am dringendsten brauchen.</para>
///
/// <para>Die zweite Hälfte ist die unangenehmere: ein fremder Realm konnte mitreden, welches Binary
/// sich der Launcher für sich selbst herunterlädt. Das Signatur-Gate hat das aufgefangen — eine
/// fremde Signatur wird abgelehnt —, es war also fail-closed. Aber eine Tür, die nur zufällt, weil
/// jemand anders davorsteht, sollte gar nicht erst offen sein.</para>
/// </summary>
public sealed class LauncherManifestPinTests
{
    /// <summary>Die Adresse ist eine Konstante im Programm, kein Konfigurationswert. Eine
    /// handeditierte <c>launcher_config.json</c> darf nicht bestimmen, woher sich der Launcher seine
    /// nächste Fassung holt — das wäre eine Codeausführung über eine Textdatei.</summary>
    [Fact]
    public async Task DasLauncherManifest_KommtVonStonetavern_EgalWasInDerConfigSteht()
    {
        var cfg = new PinConfig { ManifestUrl = "https://fremder-realm.example/manifest.json" };
        var http = new RecordingHttp();
        var svc = new ManifestService(http.Client, cfg, new Serilog.LoggerConfiguration().CreateLogger());

        await svc.FetchLauncherManifestAsync();

        Assert.Contains(RealmRegistry.StonetavernManifest, http.Urls);
        Assert.DoesNotContain("fremder-realm.example", string.Join(" ", http.Urls));
    }

    /// <summary>Und die andere Richtung: das Manifest des CLIENTS bleibt realm-gebunden. Welches
    /// Spielpaket ein Realm ausliefert, ist seine Sache — nur was der Launcher mit sich selbst tut,
    /// ist es nicht. Ein Fix, der auch das gepinnt hätte, wäre über das Ziel hinausgeschossen und
    /// hätte eigene Realms endgültig unbrauchbar gemacht.</summary>
    [Fact]
    public async Task DasClientManifest_BleibtAmRealm()
    {
        var cfg = new PinConfig { ManifestUrl = "https://eigener-realm.example/manifest.json" };
        var http = new RecordingHttp();
        var svc = new ManifestService(http.Client, cfg, new Serilog.LoggerConfiguration().CreateLogger());

        await svc.FetchAsync();

        Assert.Contains("https://eigener-realm.example/manifest.json", http.Urls);
    }

    /// <summary>Ein Realm ohne Manifest ist der Fall, der den Befund erzeugt hat: der Client hat dort
    /// nichts zu holen (und fragt gar nicht erst), der Launcher aber sehr wohl.</summary>
    [Fact]
    public async Task OhneRealmManifest_PrueftDerLauncherTrotzdem()
    {
        var cfg = new PinConfig { ManifestUrl = "" };
        var http = new RecordingHttp();
        var svc = new ManifestService(http.Client, cfg, new Serilog.LoggerConfiguration().CreateLogger());

        await svc.FetchAsync();
        Assert.Empty(http.Urls);          // simple mode: nichts zu fragen

        await svc.FetchLauncherManifestAsync();
        Assert.Single(http.Urls);         // der Launcher fragt trotzdem
        Assert.Contains(RealmRegistry.StonetavernManifest, http.Urls);
    }

    // ── Doubles ─────────────────────────────────────────────────────────────────────────────────

    private sealed class PinConfig : IConfigService
    {
        public string ManifestUrl { get; set; } = "";
        public LauncherConfig Load() => new() { ManifestUrl = ManifestUrl };
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    /// <summary>Schreibt mit, welche Adressen wirklich angefragt wurden. Der Inhalt ist egal — die
    /// Frage dieses Tests ist ausschliesslich das ZIEL, und das ist am Aufruf messbar, nicht an dem,
    /// was zurueckkommt.</summary>
    private sealed class RecordingHttp
    {
        public List<string> Urls { get; } = [];
        public System.Net.Http.HttpClient Client { get; }

        public RecordingHttp() => Client = new System.Net.Http.HttpClient(new Handler(Urls));

        private sealed class Handler(List<string> urls) : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                urls.Add(request.RequestUri?.ToString() ?? "");
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }
        }
    }
}
