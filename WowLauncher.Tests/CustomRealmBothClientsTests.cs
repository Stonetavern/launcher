using System.Linq;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Ein selbst angelegter Realm mit BEIDEN Spielclients (Owner-Befund 2026-08-05).
///
/// <para>Der Owner hat <c>localhost</c> angelegt, ihm 1.12.1 gegeben — und danach denselben Realm
/// ein <b>zweites Mal</b> angelegt, nur um an 1.14.2 zu kommen. Zwei Einträge in der Leiste für eine
/// Adresse, und keiner weiß vom anderen.</para>
///
/// <para>Der Grund war eine Datenentscheidung, keine Absicht: <c>ClientKeys</c> galt als Stammdaten
/// der ausgelieferten Presets und wurde beim Anlegen nie gesetzt. Stonetavern trug beide, ein selbst
/// angelegter Realm konnte es nicht — und die Umschaltleiste über dem Spielen-Knopf, die genau an
/// dieser Liste hängt, erschien dort deshalb nie.</para>
/// </summary>
public sealed class CustomRealmBothClientsTests
{
    /// <summary>🔴 Gibt den Realm zurück, den das ViewModel WIRKLICH hält — nicht den, den der Test
    /// angelegt hat. Seit die Konfiguration durch JSON geht (siehe <see cref="BothConfig"/>), sind das
    /// zwei verschiedene Objekte, und ein Test, der auf seiner eigenen Kopie prüft, misst sich
    /// selbst.</summary>
    private static SettingsViewModel NewVm(out RealmEntry custom)
    {
        var cfg = new BothConfig();
        var c = cfg.Load();
        c.Realms.Add(new RealmEntry
        {
            Id = "localhost", Name = "localhost", RealmlistAddress = "127.0.0.1",
            ClientKey = "1.12.1", IsPreset = false, IsLive = true,
        });
        cfg.Save(c);

        var vm = new SettingsViewModel(cfg, new NullFolderPicker());
        vm.SelectedRealm = vm.Realms.First(r => r.Id == "localhost");
        custom = vm.SelectedRealm;
        return vm;
    }

    /// <summary>Der Schalter ist da, wo die Frage sich stellt.</summary>
    [Fact]
    public void FuerEinenEigenenRealm_GibtEsDenSchalter()
    {
        var vm = NewVm(out _);

        Assert.True(vm.ShowRealmBothClients);
        Assert.False(vm.RealmOffersBothClients);   // Ausgangszustand: ein Client
    }

    /// <summary>Presets tragen ihre Builds als ausgelieferte Stammdaten. Ein Haken, der sie ändert,
    /// wäre ein Spieler, der an Daten schreibt, die beim nächsten Start wieder überschrieben
    /// werden — ein Schalter, der zurückspringt.</summary>
    [Fact]
    public void FuerEinPreset_GibtEsIhnNicht()
    {
        var vm = NewVm(out _);
        vm.SelectedRealm = vm.Realms.First(r => r.IsPreset);

        Assert.False(vm.ShowRealmBothClients);
    }

    /// <summary>Einschalten trägt ALLE auslieferbaren Clients ein, nicht zwei fest verdrahtete —
    /// kommt je ein dritter dazu, greift derselbe Schalter weiter.</summary>
    [Fact]
    public void Einschalten_TraegtAlleAuslieferbarenClientsEin()
    {
        var vm = NewVm(out var custom);

        vm.RealmOffersBothClients = true;

        Assert.NotNull(custom.ClientKeys);
        Assert.Equal(
            vm.AddableClientVersions.Select(c => c.Key).OrderBy(k => k),
            custom.ClientKeys!.OrderBy(k => k));
        Assert.True(custom.HasMultipleClients, "die Umschaltleiste haengt genau an dieser Zahl");
    }

    /// <summary>🔴 Der gebundene Client bleibt, was er war. Ein Haken, der nebenbei umstellt, womit
    /// gestartet wird, ändert die Spielfläche ohne dass jemand das verlangt hat — und der Spieler
    /// merkt es erst, wenn der falsche Client hochkommt.</summary>
    [Fact]
    public void Einschalten_AendertNichtWomitGestartetWird()
    {
        var vm = NewVm(out var custom);
        var vorher = custom.ClientKey;

        vm.RealmOffersBothClients = true;

        Assert.Equal(vorher, custom.ClientKey);
    }

    /// <summary>Und wieder aus: zurück auf den einen gebundenen Client. Wichtig, dass hier nichts
    /// hängen bleibt — eine Liste, die sich nicht mehr leeren lässt, wäre eine Einbahnstraße.</summary>
    [Fact]
    public void Ausschalten_LaesstNurDenGebundenenClient()
    {
        var vm = NewVm(out var custom);
        vm.RealmOffersBothClients = true;

        vm.RealmOffersBothClients = false;

        Assert.False(vm.RealmOffersBothClients);
        Assert.False(custom.HasMultipleClients);
        Assert.Equal("1.12.1", custom.Client.Key);
    }

    /// <summary>Die Entscheidung überlebt einen Neustart. Sie steht in der Konfiguration, nicht nur
    /// im Objekt in der Hand — sonst wäre der Haken beim nächsten Start wieder aus, und niemand
    /// wüsste warum.</summary>
    [Fact]
    public void DieEntscheidung_UeberlebtEinenNeustart()
    {
        var cfg = new BothConfig();
        var c = cfg.Load();
        c.Realms.Add(new RealmEntry
        {
            Id = "localhost", Name = "localhost", RealmlistAddress = "127.0.0.1",
            ClientKey = "1.12.1", IsPreset = false, IsLive = true,
        });
        cfg.Save(c);

        var first = new SettingsViewModel(cfg, new NullFolderPicker());
        first.SelectedRealm = first.Realms.First(r => r.Id == "localhost");
        first.RealmOffersBothClients = true;

        var second = new SettingsViewModel(cfg, new NullFolderPicker());
        second.SelectedRealm = second.Realms.First(r => r.Id == "localhost");

        Assert.True(second.RealmOffersBothClients);
    }

    /// <summary>
    /// 🔴 Speichert über JSON, nicht über eine Referenz — und das ist der Unterschied zwischen einem
    /// Test und einem Placebo.
    ///
    /// <para>Die erste Fassung gab in <c>Load</c> dasselbe Objekt zurück, das <c>Save</c> bekommen
    /// hatte. Damit sieht ein zweites ViewModel jede Änderung am Realm-Objekt sofort — ganz ohne
    /// Speichern. Die Gegenprobe hat es gezeigt: mit ausgebautem <c>PersistRealms()</c> blieb der
    /// Neustart-Test grün. Ein Rundlauf durch JSON ist das, was auf der Platte wirklich passiert,
    /// also ist es das, was hier passieren muss.</para>
    /// </summary>
    private sealed class BothConfig : IConfigService
    {
        private string _json = System.Text.Json.JsonSerializer.Serialize(
            new LauncherConfig { RealmlistAddress = "play.stonetavern.app" });

        public LauncherConfig Load() =>
            System.Text.Json.JsonSerializer.Deserialize<LauncherConfig>(_json) ?? new LauncherConfig();

        public void Save(LauncherConfig config) =>
            _json = System.Text.Json.JsonSerializer.Serialize(config);

        public bool LastSaveSucceeded => true;
    }
}
