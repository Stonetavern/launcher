using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Ein leerer Addon-Katalog hat zwei Gründe, und der Launcher nannte immer denselben.
///
/// <para><b>Der Befund</b> (Owner, 2026-08-13). Auf der Addon-Seite stand „Für diesen Client werden
/// noch keine Addons angeboten", während der Server 36 Pakete für 1.14.2 auslieferte (nachgemessen an
/// <c>https://stonetavern.app/api/launcher/addons</c>: 584 Einträge, davon 36 mit Build 42597). Im
/// Protokoll des Tages stand kein einziger Katalog-Eintrag, am Vortag vier — der Abruf hatte also gar
/// nicht stattgefunden. Er endete am Anmeldegatter, das einen leeren Katalog zurückgibt und schweigt.</para>
///
/// <para><b>Warum das mehr ist als ein falscher Satz.</b> Das Flag, das genau diesen Unterschied
/// tragen soll, wird beim Aufbau der Oberfläche einmal gesetzt. Läuft die Anmeldung während der
/// Sitzung ab, bleibt es auf „angemeldet" stehen: die Seite behauptet dann etwas über den KATALOG,
/// was in Wahrheit über die SITZUNG gilt. Wer das liest, sucht den Fehler bei den Addons.</para>
/// </summary>
public sealed class AddonSignInReasonTests
{
    private const int Modern = 42597;

    /// <summary>🔴 Der Fall des Owners: die Anmeldung ist weg, der Katalog kommt leer zurück — und die
    /// Seite sagt das auch, statt über Addons zu sprechen.</summary>
    [Fact]
    public async Task EineAbgelaufeneAnmeldung_WirdAlsSolcheGenannt()
    {
        var vm = NewAddons(new GatedCatalog());

        await vm.LoadAsync(Modern);

        Assert.True(vm.SignedOut, "der Katalog kam nicht wegen fehlender Pakete leer zurück");
        Assert.False(vm.ShowEmpty, "„es gibt keine Addons\" wäre hier der falsche Grund");
    }

    /// <summary>Die Gegenprobe: liefert der Katalog wirklich nichts für diesen Client, dann bleibt es
    /// bei der Aussage über die Addons. Ohne diesen Fall wäre der Test oben auch mit einem Flag grün,
    /// das einfach immer gesetzt wird.</summary>
    [Fact]
    public async Task EinWirklichLeererKatalog_SprichtWeiterUeberAddons()
    {
        var vm = NewAddons(new EmptyCatalog());

        await vm.LoadAsync(Modern);

        Assert.False(vm.SignedOut);
    }

    private static AddonsViewModel NewAddons(IAddonService addons)
    {
        var log = Serilog.Core.Logger.None;
        var dir = Path.Combine(Path.GetTempPath(), "addon-reason-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfg = new MemoryConfig(dir);
        return new AddonsViewModel(addons, cfg, new AddonProfileService(log), log);
    }

    // ── Attrappen ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Der Dienst hinter dem Anmeldegatter: leerer Katalog, und er sagt warum.</summary>
    private sealed class GatedCatalog : NoAddons
    {
        public override bool CatalogNeedsSignIn => true;
    }

    /// <summary>Angemeldet, aber für diesen Client ist wirklich nichts dabei.</summary>
    private sealed class EmptyCatalog : NoAddons;

    private abstract class NoAddons : IAddonService
    {
        public Task<AddonCatalog> GetCatalogAsync(bool force = false, CancellationToken ct = default) =>
            Task.FromResult(AddonCatalog.Empty);
        public virtual bool CatalogNeedsSignIn => false;
        public Task<IReadOnlyList<AddonStatus>> GetStatusAsync(string clientDir, int build, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AddonStatus>>([]);
        public Task<AddonActionResult> InstallAsync(string clientDir, AddonEntry entry, int build = 0,
            IProgress<string>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(AddonActionResult.Failed("test"));
        public Task<AddonActionResult> RemoveAsync(string clientDir, string addonId, CancellationToken ct = default) =>
            Task.FromResult(AddonActionResult.Failed("test"));
    }

    private sealed class MemoryConfig(string clientDir) : IConfigService
    {
        public LauncherConfig Current = new()
        {
            ClientInstalls = new Dictionary<int, string> { [Modern] = clientDir },
        };
        public LauncherConfig Load() => Current;
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }
}
