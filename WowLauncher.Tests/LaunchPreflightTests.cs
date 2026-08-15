using System;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Die Frage „kann hier ueberhaupt gestartet werden", getrennt vom Start.
///
/// <para><b>Warum es das gibt.</b> Unter Linux entscheidet sich das an Dingen, die der Launcher nicht
/// mitliefert: ob Wine da ist, ob es 32-Bit kann, ob der Grafiktreiber Vulkan in der noetigen Fassung
/// meldet. Bis zum 2026-08-05 erfuhr man das erst NACH dem Druck auf Spielen - und war damit von
/// aussen nicht messbar. Der erste Lauf in einer nackten VM konnte genau diese Meldung nicht pruefen,
/// weil sie ohne Klick nicht entsteht und der Klick dort nicht ausloesbar war.</para>
///
/// <para><b>Die Falle, die dabei zweimal zugeschnappt ist</b>, und deshalb der Kern dieser Datei: die
/// Schnittstelle gibt „bereit" als Vorgabe. Jede Huelle und jede Weiche, die das nicht
/// ueberschreibt, meldet damit „bereit" - auch auf einer Maschine ganz ohne Wine. Erst hat die Weiche
/// den falschen Zweig gefragt, dann hat die Huelle darueber gar nicht gefragt. Beides sah aus wie
/// eine bestandene Pruefung.</para>
/// </summary>
public sealed class LaunchPreflightTests
{
    private const string Vanilla = "WoW.exe";
    private const string Classic = "WowClassic.exe";

    /// <summary>
    /// 🔴 Die Weiche muss den Zweig fragen, den sie auch starten wuerde. Die beiden Clients haben
    /// verschiedene Voraussetzungen, also ist eine Pruefung am falschen Zweig schlimmer als keine:
    /// sie sagt „bereit" ueber etwas, das gar nicht laufen soll.
    /// </summary>
    [Theory]
    [InlineData(Vanilla, "kein system-wine")]
    [InlineData(Classic, "kein wine-ge")]
    public async Task DieWeiche_FragtDenZweigDenSieAuchStartenWuerde(string exe, string erwartet)
    {
        var router = new LinuxGameLauncherRouter(
            legacyWine: new Sagt("kein system-wine"),
            modernWine: new Sagt("kein wine-ge"),
            logger: Log());

        Assert.Equal(erwartet, await router.CheckReadyAsync(exe));
    }

    /// <summary>Ohne Wine gibt es fuer den modernen Client gar keinen Zweig. Die Weiche muss das
    /// selbst beantworten, und zwar mit dem Satz, den sie auch beim Start zeigen wuerde.</summary>
    [Fact]
    public async Task OhneJedeWine_WirdDerModerneClientAbgelehnt()
    {
        var router = new LinuxGameLauncherRouter(
            legacyWine: new Sagt("kein system-wine"), modernWine: null, logger: Log());

        var antwort = await router.CheckReadyAsync(Classic);

        Assert.NotNull(antwort);
        Assert.Contains("Wine", antwort!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔴 Der Fehler, der zweimal passiert ist. Eine Huelle reicht die Frage weiter, statt die
    /// Vorgabe der Schnittstelle stehen zu lassen - sonst meldet sie „bereit" fuer einen inneren
    /// Launcher, den niemand gefragt hat.
    /// </summary>
    [Fact]
    public async Task DieHuelle_ReichtDieFrageWeiterStattSelbstJaZuSagen()
    {
        var huelle = new LoaderScriptLauncher(new Sagt("kein wine"), Log());

        Assert.Equal("kein wine", await huelle.CheckReadyAsync(Vanilla));
    }

    /// <summary>Und der Gegenfall, ohne den der obige nichts beweist: sagt der innere „bereit", muss
    /// auch die Huelle „bereit" sagen. Eine Pruefung, die immer etwas findet, ist genauso wertlos wie
    /// eine, die nie etwas findet.</summary>
    [Fact]
    public async Task IstDerInnereBereit_IstEsAuchDieHuelle()
    {
        var huelle = new LoaderScriptLauncher(new Sagt(null), Log());

        Assert.Null(await huelle.CheckReadyAsync(Vanilla));
    }

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>Ein Launcher, der auf die Frage genau eine Antwort gibt - und beim Start sofort
    /// scheitert, damit ein Test, der versehentlich startet, auffliegt statt durchzulaufen.</summary>
    private sealed class Sagt(string? antwort) : IGameLauncher
    {
        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory) =>
            throw new InvalidOperationException("Dieser Test startet nichts.");

        public Task<string?> CheckReadyAsync(string exeName) => Task.FromResult(antwort);
    }
}
