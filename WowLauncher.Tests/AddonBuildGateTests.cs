using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Die Liste zeigt auf jedem Client absichtlich ALLE Pakete, auch die des anderen Spielstands
/// (Owner-Entscheid 2026-08-12). Damit ist der frueher alleinige Listenfilter keine Schranke mehr,
/// und die Sperre muss im Installationspfad sitzen. Beides gehoert zusammen: ohne die Sperre waere
/// das Anzeigen genau die Fehlbedienung, die es verhindern soll.
/// </summary>
public class AddonBuildGateTests
{
    private const int Vanilla = 5875;
    private const int Modern = 42597;

    private static AddonEntry Entry(string id, params int[] builds) => new()
    {
        Id = id,
        Name = id,
        Version = "1",
        Url = "https://addons.stonetavern.app/" + id + ".zip",
        Sha256 = new string('a', 64),
        Size = 1024,
        Folders = [id],
        Builds = builds.ToList(),
    };

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wl-addon-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Der Katalogfilter selbst ──────────────────────────────────────────────

    [Fact]
    public void EntryDeclaredForOneBuild_DoesNotSupportTheOther()
    {
        Assert.True(Entry("a", Vanilla).SupportsBuild(Vanilla));
        Assert.False(Entry("a", Vanilla).SupportsBuild(Modern));
        Assert.True(Entry("b", Modern).SupportsBuild(Modern));
        Assert.False(Entry("b", Modern).SupportsBuild(Vanilla));
    }

    [Fact]
    public void EntryWithoutAnyBuild_SupportsNothing()
    {
        // 🔴 Vorher galt eine leere Liste als "passt zu allen Builds" — fail-open. Ein Katalog-Eintrag
        // ohne Build-Angabe ist ein Datenfehler; ihn ueberall anzubieten macht daraus ein
        // Spielerproblem.
        var e = Entry("nobuild");
        Assert.False(e.SupportsBuild(Vanilla));
        Assert.False(e.SupportsBuild(Modern));
    }

    // ── Die Liste zeigt alles, markiert aber ehrlich ───────────────────────────

    [Fact]
    public void Join_ShowsEveryPackage_AndMarksTheOnesForAnotherClient()
    {
        var dir = NewTempDir();
        try
        {
            var offered = new List<AddonEntry> { Entry("vanilla-only", Vanilla), Entry("modern-only", Modern) };

            var rows = AddonService.Join(offered, dir, Modern);

            Assert.Equal(2, rows.Count);
            Assert.True(rows.Single(r => r.Id == "modern-only").FitsClient);
            Assert.False(rows.Single(r => r.Id == "vanilla-only").FitsClient);
            // Die Build-Angabe reist mit, damit die Zeile den Grund nennen kann.
            Assert.Equal([Vanilla], rows.Single(r => r.Id == "vanilla-only").DeclaredBuilds);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Join_WithoutAKnownClient_MarksNothingAsUnfit()
    {
        // Kein Client aufgeloest heisst nicht "nichts passt" — sonst behauptet ein leerer Zustand,
        // die gesamte Auswahl sei unbrauchbar.
        var dir = NewTempDir();
        try
        {
            var rows = AddonService.Join([Entry("a", Vanilla), Entry("b", Modern)], dir, build: 0);
            Assert.All(rows, r => Assert.True(r.FitsClient));
        }
        finally { Directory.Delete(dir, true); }
    }

}
