using System.IO;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Bis 2026-08-12 fuehrte der Launcher einen Addon-Satz pro Realm und liess weitere anlegen. Auf
/// Owner-Entscheid gibt es jetzt genau EINEN Satz, nicht loeschbar und nicht vermehrbar. Die Tests
/// dieser Datei hielten vorher das Anlegen und den Waehler fest; sie halten jetzt fest, dass die
/// Umstellung keinem Spieler etwas wegnimmt.
///
/// <para>Das ist der eigentliche Punkt: wer von zwei Saetzen auf einen reduziert, hat einen Ordner
/// voller Addons zu viel — und der gehoert dem Spieler. Er wird geparkt gelassen, nicht geraeumt.</para>
/// </summary>
public class AddonSetCreationTests
{
    private static string NewClientDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wl-sets-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(dir, "Interface", "AddOns"));
        return dir;
    }

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    private static string AddonsDir(string clientDir) => Path.Combine(clientDir, "Interface", "AddOns");

    [Fact]
    public void Migration_SetztDenAktivenSatzAufDefault_OhneEtwasZuVerschieben()
    {
        var client = NewClientDir();
        try
        {
            var svc = new AddonProfileService(Log());
            // Ein Spieler mit dem alten Zustand: aktiver Satz "elwynn", darin ein installiertes Addon.
            File.WriteAllText(Path.Combine(AddonsDir(client), AddonProfileService.MarkerName), "elwynn");
            Directory.CreateDirectory(Path.Combine(AddonsDir(client), "Questie"));
            File.WriteAllText(Path.Combine(AddonsDir(client), "Questie", "Questie.toc"), "## Title: Questie");

            var was = svc.MigrateToDefault(client);

            Assert.Equal("elwynn", was);
            Assert.Equal(AddonProfileService.DefaultProfile, svc.ActiveProfile(client));
            // Das Addon liegt unveraendert da, wo es lag.
            Assert.True(File.Exists(Path.Combine(AddonsDir(client), "Questie", "Questie.toc")));
        }
        finally { Directory.Delete(client, true); }
    }

    [Fact]
    public void Migration_LaesstEinenGeparktenSatzInRuhe()
    {
        // 🔴 Der Fall, der wehtun wuerde: der Spieler hat einen zweiten Satz, der gerade nicht aktiv
        // ist. Eine Migration, die "aufraeumt", loescht damit Addons, die er selbst installiert hat.
        var client = NewClientDir();
        try
        {
            var svc = new AddonProfileService(Log());
            File.WriteAllText(Path.Combine(AddonsDir(client), AddonProfileService.MarkerName), "elwynn");

            var parked = AddonsDir(client) + ".barrens";
            Directory.CreateDirectory(Path.Combine(parked, "Details"));
            File.WriteAllText(Path.Combine(parked, "Details", "Details.toc"), "## Title: Details");

            svc.MigrateToDefault(client);

            Assert.True(Directory.Exists(Path.Combine(parked, "Details")),
                "der geparkte Satz des Spielers wurde angetastet");
            Assert.True(File.Exists(Path.Combine(parked, "Details", "Details.toc")));
        }
        finally { Directory.Delete(client, true); }
    }

    [Fact]
    public void Migration_IstWiederholbar_UndAendertNichtsWennSchonDefault()
    {
        var client = NewClientDir();
        try
        {
            var svc = new AddonProfileService(Log());
            File.WriteAllText(Path.Combine(AddonsDir(client), AddonProfileService.MarkerName),
                AddonProfileService.DefaultProfile);

            Assert.Null(svc.MigrateToDefault(client));
            Assert.Equal(AddonProfileService.DefaultProfile, svc.ActiveProfile(client));
        }
        finally { Directory.Delete(client, true); }
    }

    [Fact]
    public void Migration_OhneMarker_LaesstDenOrdnerWieErIst()
    {
        // Eine Installation aus der Zeit vor den Saetzen: kein Marker, aber Addons drin. Der Ordner
        // gehoert dem Spieler und wird adoptiert, nicht umgebaut.
        var client = NewClientDir();
        try
        {
            var svc = new AddonProfileService(Log());
            Directory.CreateDirectory(Path.Combine(AddonsDir(client), "Handmade"));

            svc.MigrateToDefault(client);

            Assert.True(Directory.Exists(Path.Combine(AddonsDir(client), "Handmade")));
        }
        finally { Directory.Delete(client, true); }
    }
}
