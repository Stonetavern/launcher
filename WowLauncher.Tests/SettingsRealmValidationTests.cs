using System;
using System.Linq;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The form side of realm validation. <see cref="RealmlistWriteTests"/> pins what happens at the write;
/// this file pins what happens where the player types, which is the only place a typo can still be
/// fixed by the person who made it.
///
/// <para>Two fields, two different failures. The realmlist address ends up verbatim in files the game
/// client reads as configuration, so a value carrying a line break appends directives of its own. The
/// manifest URL is handed to HttpClient, and a value without a scheme throws there, is caught, and
/// drops the launcher into simple mode: no client management, no downloads, no updates, and not a word
/// on screen about why. Both must be refused at the form, with the reason shown, and nothing stored.</para>
/// </summary>
public sealed class SettingsRealmValidationTests
{
    private sealed class MemoryConfig : IConfigService
    {
        public LauncherConfig Current = new();
        public LauncherConfig Load() => Current;
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }

    private static (SettingsViewModel vm, MemoryConfig cfg) NewSettings()
    {
        var cfg = new MemoryConfig();
        return (new SettingsViewModel(cfg, new NullFolderPicker()), cfg);
    }

    // ── The address field ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAddressCarryingADirective_IsRefused_AndNothingIsStored()
    {
        var (vm, cfg) = NewSettings();
        var railBefore = vm.Realms.Count;

        vm.NewRealmName = "My realm";
        vm.NewRealmAddress = "play.example.invalid\nset gxWindow \"0\"";
        vm.AddRealmCommand.Execute(null);

        Assert.True(vm.HasAddRealmError, "The player was given no reason why the realm did not appear.");
        Assert.Empty(cfg.Current.Realms);
        Assert.Equal(railBefore, vm.Realms.Count);
        // The form keeps what was typed, so the typo can be corrected instead of retyped.
        Assert.Equal("My realm", vm.NewRealmName);
    }

    // ── The manifest URL field ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AManifestUrlWithoutAScheme_IsRefused_AndNothingIsStored()
    {
        var (vm, cfg) = NewSettings();

        vm.NewRealmName = "My realm";
        vm.NewRealmAddress = "play.example.invalid";
        vm.NewRealmManifestUrl = "downloads.example.com/manifest.json";   // the shape that has no scheme
        vm.AddRealmCommand.Execute(null);

        Assert.True(vm.HasAddRealmError,
            "A realm was stored with a manifest URL that can never be fetched. The launcher falls back " +
            "to simple mode silently and the player has no way to find out why.");
        Assert.Empty(cfg.Current.Realms);
        Assert.DoesNotContain(vm.Realms, r => r.Name == "My realm");
    }

    [Theory]
    [InlineData("https://downloads.example.invalid/manifest.json", true)]
    [InlineData("http://downloads.example.invalid/manifest.json", true)]
    [InlineData("downloads.example.invalid/manifest.json", false)]  // no scheme -> relative Uri
    [InlineData("/manifest.json", false)]
    [InlineData("ftp://downloads.example.invalid/manifest.json", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ManifestUrlValidation_AcceptsOnlyAbsoluteHttpAddresses(string url, bool expected)
        => Assert.Equal(expected, ManifestService.IsValidManifestUrl(url));

    // ── The accepting case, so the guards above cannot pass by refusing everything ───────────────

    [Fact]
    public void AGoodRealmIsStored_WithItsAddressAndManifest_AndTheFormClears()
    {
        var (vm, cfg) = NewSettings();
        var changed = 0;
        vm.RealmsChanged += () => changed++;

        vm.NewRealmName = "My realm";
        vm.NewRealmAddress = "  play.example.invalid  ";
        vm.NewRealmManifestUrl = "https://downloads.example.invalid/manifest.json";
        vm.AddRealmCommand.Execute(null);

        Assert.False(vm.HasAddRealmError);
        var stored = Assert.Single(cfg.Current.Realms);
        Assert.Equal("My realm", stored.Name);
        Assert.Equal("play.example.invalid", stored.RealmlistAddress);
        Assert.Equal("https://downloads.example.invalid/manifest.json", stored.ManifestUrl);
        Assert.Equal(stored.Id, cfg.Current.SelectedRealmId);

        Assert.Contains(vm.Realms, r => r.Id == stored.Id);
        Assert.Equal("", vm.NewRealmName);
        Assert.Equal("", vm.NewRealmAddress);
        Assert.Equal("", vm.NewRealmManifestUrl);
        Assert.True(changed > 0, "The v3 rail is only rebuilt when this event fires.");
    }

    [Fact]
    public void AnEmptyManifestUrlIsFine_ThatIsSimpleMode()
    {
        var (vm, cfg) = NewSettings();

        vm.NewRealmName = "Someone elses server";
        vm.NewRealmAddress = "play.example.invalid";
        vm.NewRealmManifestUrl = "";
        vm.AddRealmCommand.Execute(null);

        Assert.False(vm.HasAddRealmError);
        var stored = Assert.Single(cfg.Current.Realms);
        Assert.Null(stored.ManifestUrl);
    }
}

/// <summary>
/// The service side of the same field. The form above is not the only way a manifest URL gets into the
/// config: it also arrives from a hand-edited launcher_config.json, and from a config written by an
/// older launcher build that had no validation at all. An unusable value must be refused before it
/// reaches HttpClient, where it throws an InvalidOperationException that the catch-all turns into a
/// silent simple-mode fallback.
/// </summary>
public sealed class ManifestFetchGuardTests
{
    private sealed class UrlConfig(string url) : IConfigService
    {
        private readonly string _url = url;

        public LauncherConfig Load() => new() { ManifestUrl = _url };
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    private sealed class CountingHandler : System.Net.Http.HttpMessageHandler
    {
        public int Calls;
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref Calls);
            return System.Threading.Tasks.Task.FromResult(
                new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    /// <summary>Collects what the service actually said, because that is the whole difference between
    /// the two ways this ends up returning null.</summary>
    private sealed class Collector : Serilog.Core.ILogEventSink
    {
        public readonly System.Collections.Generic.List<Serilog.Events.LogEvent> Events = [];
        public void Emit(Serilog.Events.LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    /// <summary>
    /// Both with and without the guard the call ends in null and no request on the wire - HttpClient
    /// rejects a relative Uri before it reaches a handler. What differs, and what decides whether anyone
    /// can ever diagnose this, is HOW it ends: a named configuration error that points at the URL, or an
    /// unexpected exception swallowed by a catch-all.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task ASchemelessManifestUrlInTheConfig_IsRefusedAsAConfigError_NotSwallowedAsACrash()
    {
        var handler = new CountingHandler();
        var collector = new Collector();
        var svc = new ManifestService(new System.Net.Http.HttpClient(handler),
            new UrlConfig("downloads.example.invalid/manifest.json"),
            new Serilog.LoggerConfiguration().WriteTo.Sink(collector).CreateLogger());

        var manifest = await svc.FetchAsync();

        Assert.Null(manifest);
        Assert.Equal(0, handler.Calls);

        var errors = collector.Events
            .Where(e => e.Level >= Serilog.Events.LogEventLevel.Error)
            .ToList();
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.Null(e.Exception));
        Assert.Contains(errors, e =>
            e.RenderMessage().Contains("downloads.example.invalid/manifest.json", StringComparison.Ordinal));
    }
}
