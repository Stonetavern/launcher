using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Der Weg zu „Passwort vergessen" — und die Entscheidung, ihn NICHT im Launcher nachzubauen.
///
/// <para>🔴 Der Anlass ist eine korrigierte Fehldiagnose, und sie steht hier, weil sie sonst wieder
/// gemacht wird. Am 2026-08-05 führte unsere eigene Feature-Liste „kein Passwort vergessen" als den
/// einen Punkt, der einen Spieler komplett aussperrt. Eine kontextfreie Zweitinstanz hatte es
/// gefunden, ich hatte es übernommen — beide hatten dieselbe Quelle angesehen:
/// <c>ILauncherAuthService</c> kennt Login, Register, Logout und sonst nichts.</para>
///
/// <para>Nachgesehen, bevor gebaut wurde: die Website hat den Reset längst, vollständig, mit
/// Token-Fluss und Ratenbegrenzung auf IP und Konto. <b>Niemand war je ausgesperrt.</b> Es fehlte
/// ausschließlich der Weg dorthin aus dem Launcher. Aus einem „mittleren" Feature wurde ein Link.</para>
///
/// <para>Die Lehre ist nicht „Codex lag falsch" — es lag richtig über den Launcher und falsch über
/// das Produkt. Die Lehre ist: eine Abwesenheit in EINER Schnittstelle ist keine Abwesenheit im
/// System.</para>
/// </summary>
public sealed class ForgotPasswordTests
{
    /// <summary>Der Link führt auf die Seite der KONFIGURIERTEN Website. Ein eigener Realm kann eine
    /// eigene haben, und ein Link, der dort auf stonetavern.app zeigt, schickt einen Spieler zum
    /// Zurücksetzen eines Kontos, das er dort nicht hat.</summary>
    [Fact]
    public void DerLink_FolgtDerKonfiguriertenWebsite()
    {
        var vm = new LoginViewModel(new NoAuth(),
            new SiteConfig { SiteBaseUrl = "https://realm.example" });

        Assert.Equal("https://realm.example/forgot-password", vm.ForgotPasswordUrl);
    }

    /// <summary>Ein abschließender Schrägstrich in der Konfiguration darf keine doppelte Adresse
    /// erzeugen. Klingt klein, ist es auch — und genau deshalb prüft es sonst niemand.</summary>
    [Fact]
    public void EinSchraegstrichAmEnde_ErzeugtKeineDoppelteAdresse()
    {
        var vm = new LoginViewModel(new NoAuth(),
            new SiteConfig { SiteBaseUrl = "https://realm.example/" });

        Assert.Equal("https://realm.example/forgot-password", vm.ForgotPasswordUrl);
    }

    /// <summary>Ohne konfigurierte Website die ausgelieferte Adresse. Ein Link ins Leere wäre
    /// schlimmer als kein Link: er sieht aus wie ein Ausweg und ist keiner.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void OhneKonfiguration_ZeigtDerLinkAufStonetavern(string site)
    {
        var vm = new LoginViewModel(new NoAuth(), new SiteConfig { SiteBaseUrl = site });

        Assert.Equal("https://stonetavern.app/forgot-password", vm.ForgotPasswordUrl);
    }

    /// <summary>Auch ohne jede Konfiguration (die Testhosts bauen diese View so) zeigt der Link
    /// irgendwohin Sinnvolles statt zu werfen.</summary>
    [Fact]
    public void OhneJedeKonfiguration_TraegtDerLinkTrotzdem()
    {
        var vm = new LoginViewModel(new NoAuth());

        Assert.Equal("https://stonetavern.app/forgot-password", vm.ForgotPasswordUrl);
    }

    /// <summary>Der Weg ist in der Anmeldekarte sichtbar, und zwar auf der ANMELDE-Seite: es ist die
    /// Frage, die man sich stellt, nachdem das Anmelden nicht ging. Auf der Registrierseite wäre sie
    /// sinnlos.</summary>
    [Fact]
    public void DieAnmeldekarte_TraegtDenWeg()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDir(), "Views", "LoginView.axaml"));
        var stripped = Regex.Replace(xaml, "<!--.*?-->", " ", RegexOptions.Singleline);

        Assert.Contains("ForgotPasswordCommand", stripped);
        Assert.Contains("loc:Tr Login_Forgot", stripped);

        // Vor dem Wechsel zum Registrieren, nach dem Anmelden-Knopf.
        var forgot = stripped.IndexOf("ForgotPasswordCommand", StringComparison.Ordinal);
        var register = stripped.IndexOf("ShowRegisterCommand", StringComparison.Ordinal);
        Assert.True(forgot > 0 && register > forgot,
            "the forgot link is not between signing in and creating an account");
    }

    /// <summary>🔴 Die Entscheidung selbst, festgenagelt: der Launcher bekommt KEINEN eigenen
    /// Reset-Pfad. Ein zweiter Weg an dieselben Kontodaten hieße eine zweite Ratenbegrenzung, ein
    /// zweiter Token-Umgang und zweimal so viele Stellen, an denen eine Kontoübernahme entstehen
    /// kann — für null zusätzliche Fähigkeit. Wer das später doch einbaut, soll hier vorbeikommen
    /// und den Grund lesen müssen.</summary>
    [Fact]
    public void DerLauncher_HatKeinenEigenenResetPfad()
    {
        var iface = typeof(ILauncherAuthService);
        foreach (var m in iface.GetMethods())
            Assert.DoesNotContain("reset", m.Name, StringComparison.OrdinalIgnoreCase);

        var svc = File.ReadAllText(Path.Combine(AppDir(), "Services", "LauncherAuthService.cs"));
        Assert.DoesNotContain("/reset", svc, StringComparison.OrdinalIgnoreCase);
    }

    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "WowLauncher");
            if (File.Exists(Path.Combine(candidate, "Styles.v3.axaml"))) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("WowLauncher source tree not found");
    }

    private sealed class SiteConfig : IConfigService
    {
        public string SiteBaseUrl { get; set; } = "";
        public LauncherConfig Load() => new() { SiteBaseUrl = SiteBaseUrl };
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    private sealed class NoAuth : ILauncherAuthService
    {
        public bool IsLoggedIn => false;
        public string? CurrentToken => null;
        public string? CurrentAccount => null;
        public Task<LoginOutcome> LoginAsync(string u, string p, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("test"));
        public Task<LoginOutcome> RegisterAsync(RegisterRequest r, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("test"));
        public void Logout() { }
    }
}
