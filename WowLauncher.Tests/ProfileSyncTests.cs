using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The realm profile that follows a Stonetavern account. Every test here is about ONE promise: the
/// local config is the source of truth, and no failure of ours may quietly take a realm away from a
/// player.
/// </summary>
public sealed class ProfileSyncTests
{
    private sealed class Cfg(LauncherConfig c) : IConfigService
    {
        public LauncherConfig Current = c;
        public LauncherConfig Load() => Current;
        public void Save(LauncherConfig x) { Current = x; Saves++; }
        public int Saves;
        public bool LastSaveSucceeded => true;
    }

    private sealed class Auth(string? token) : ILauncherAuthService
    {
        public bool IsLoggedIn => token is not null;
        public string? CurrentToken => token;
        public string? CurrentAccount => token is null ? null : "TESTER";
        public Task<LoginOutcome> LoginAsync(string u, string p, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("unused"));
        public Task<LoginOutcome> RegisterAsync(RegisterRequest r, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("unused"));
        public void Logout() { }
    }

    /// <summary>Answers a scripted sequence of responses and records what was sent.</summary>
    private sealed class Handler(params (HttpStatusCode code, string body)[] script) : HttpMessageHandler
    {
        private int _i;
        public readonly List<string> SentBodies = [];
        public readonly List<string> SentMethods = [];

        /// <summary>Runs just before the n-th response is handed back, so a test can change the world
        /// mid-flight the way a player would.</summary>
        public Action<int>? BeforeResponse;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            BeforeResponse?.Invoke(_i);
            SentMethods.Add(req.Method.Method);
            SentBodies.Add(req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct));
            var (code, body) = script[Math.Min(_i++, script.Length - 1)];
            return new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static LauncherConfig ConfigWith(params string[] ownRealmIds)
    {
        var cfg = new LauncherConfig
        {
            ProfileSyncEnabled = true,
            SelectedRealmId = "elwynn",
            Realms =
            [
                new RealmEntry { Id = "elwynn", Name = "Elwynn", RealmlistAddress = "play.stonetavern.app",
                                 ClientKey = "1.12.1", IsPreset = true },
                .. ownRealmIds.Select(id => new RealmEntry
                {
                    Id = id, Name = id, RealmlistAddress = $"{id}.example.test", ClientKey = "1.12.1",
                }),
            ]
        };
        return cfg;
    }

    private static (HttpProfileSyncService svc, Cfg cfg, Handler h) Build(
        LauncherConfig config, string? token, params (HttpStatusCode, string)[] script)
    {
        var cfg = new Cfg(config);
        var h = new Handler(script);
        var svc = new HttpProfileSyncService(new HttpClient(h), cfg, new Auth(token),
            new Serilog.LoggerConfiguration().CreateLogger());
        return (svc, cfg, h);
    }

    // ── The union, which is the whole point ────────────────────────────────────────────────────

    [Fact]
    public void Union_KeepsRealmsFromBothSides()
    {
        // Machine A added "alpha", machine B added "beta". Last-write-wins would delete one of them
        // and the player would only notice by going looking. The union cannot.
        var server = new LauncherProfile
        {
            Realms = [new ProfileRealm { Id = "alpha", Name = "Alpha", RealmlistAddress = "a.test", ClientKey = "1.12.1" }],
        };
        var local = new LauncherProfile
        {
            SelectedRealmId = "beta",
            Realms = [new ProfileRealm { Id = "beta", Name = "Beta", RealmlistAddress = "b.test", ClientKey = "1.12.1" }],
        };

        var merged = ProfileMerge.Union(server, local);

        Assert.Equal(["alpha", "beta"], merged.Realms.Select(r => r.Id));
        Assert.Equal("beta", merged.SelectedRealmId);  // the machine in front of the player wins
    }

    [Fact]
    public void Union_LocalWinsOnTheSameId()
    {
        var server = new LauncherProfile
        {
            Realms = [new ProfileRealm { Id = "x", Name = "Old", RealmlistAddress = "old.test", ClientKey = "1.12.1" }],
        };
        var local = new LauncherProfile
        {
            Realms = [new ProfileRealm { Id = "x", Name = "New", RealmlistAddress = "new.test", ClientKey = "1.12.1" }],
        };

        Assert.Equal("new.test", ProfileMerge.Union(server, local).Realms.Single().RealmlistAddress);
    }

    [Fact]
    public void Union_DropsARealmTheServerSentWithAHostileManifestScheme()
    {
        // The manifest URL is fetched by the launcher. A file:/javascript: value coming back from any
        // source must not survive into the config.
        var server = new LauncherProfile
        {
            Realms = [new ProfileRealm { Id = "x", Name = "X", RealmlistAddress = "x.test",
                                         ClientKey = "1.12.1", ManifestUrl = "file:///etc/passwd" }],
        };

        var merged = ProfileMerge.Union(server, new LauncherProfile());

        Assert.Equal("", merged.Realms.Single().ManifestUrl);
    }

    [Fact]
    public void Profile_CarriesNoMachineLocalPaths()
    {
        // Syncing install paths would make a Linux launcher believe in C:\Games\WoW: the client counts
        // as installed, PLAY points at nothing, and nothing ever turns red.
        var props = typeof(LauncherProfile).GetProperties().Select(p => p.Name)
            .Concat(typeof(ProfileRealm).GetProperties().Select(p => p.Name)).ToList();

        Assert.DoesNotContain("ClientInstalls", props);
        Assert.DoesNotContain("PreferredInstallRoot", props);
        Assert.DoesNotContain("WowExecutablePath", props);
    }

    [Fact]
    public void Profile_LeavesPresetsOut()
    {
        var profile = LauncherProfile.FromConfig(ConfigWith("mine"));

        Assert.Equal(["mine"], profile.Realms.Select(r => r.Id));
    }

    // ── Deleting has to mean deleting (Codex-Befund 2026-07-23) ────────────────────────────────

    [Fact]
    public void Union_ARealmTheServerStillHas_StaysDeletedWhenLocallyBuried()
    {
        // Without the grave marker this realm comes back on EVERY sync, forever, and the player
        // watches it reappear after every removal.
        var server = new LauncherProfile
        {
            Realms = [new ProfileRealm { Id = "gone", Name = "Gone", RealmlistAddress = "g.test", ClientKey = "1.12.1" }],
        };
        var local = new LauncherProfile { DeletedRealms = [new ProfileGrave { Id = "gone", DeletedAt = 200 }] };

        var merged = ProfileMerge.Union(server, local);

        Assert.Empty(merged.Realms);
        Assert.Equal(["gone"], merged.DeletedRealmIds);
    }

    [Fact]
    public void Union_AGraveBeatsARealmThatIsMerelyStillPresentLocally()
    {
        // The trap Codex found: "it exists here, so the player re-added it" cannot be told apart from
        // "this machine has not seen the deletion yet" - and the second is the NORMAL case. With the
        // revive-on-presence rule every device resurrected every deletion and the two wrote it back
        // and forth forever.
        var server = new LauncherProfile { DeletedRealms = [new ProfileGrave { Id = "x", DeletedAt = 200 }] };
        var local = new LauncherProfile
        {
            // Der Realm liegt hier noch, wurde aber NICHT neu angelegt: AddedAt ist aelter als das Grab.
            Realms = [new ProfileRealm { Id = "x", Name = "X", RealmlistAddress = "x.test", ClientKey = "1.12.1", AddedAt = 100 }],
        };

        var merged = ProfileMerge.Union(server, local);

        Assert.Empty(merged.Realms);
        Assert.Equal(["x"], merged.DeletedRealmIds);
    }

    [Fact]
    public void FromConfig_KeepsEveryGraveVerbatim()
    {
        // Exactly one place clears a grave, and it is the add button. A second, quieter rule here
        // would be the same trap in another coat.
        var cfg = ConfigWith("mine");
        cfg.DeletedRealms = [new ProfileGrave { Id = "mine", DeletedAt = 1 },
                             new ProfileGrave { Id = "really-gone", DeletedAt = 2 }];

        Assert.Equal(["mine", "really-gone"], LauncherProfile.FromConfig(cfg).DeletedRealmIds);
    }

    [Fact]
    public void Union_ReaddingAfterTheDeletion_WinsAndClearsTheGrave()
    {
        // The dead end Codex found in round three: with the grave always winning, a realm id the
        // player once deleted could never be used again - on any machine, forever. What decides is
        // which act happened LATER, and only a timestamp carries that.
        var server = new LauncherProfile { DeletedRealms = [new ProfileGrave { Id = "x", DeletedAt = 100 }] };
        var local = new LauncherProfile
        {
            Realms = [new ProfileRealm { Id = "x", Name = "X again", RealmlistAddress = "x.test",
                                         ClientKey = "1.12.1", AddedAt = 200 }],
        };

        var merged = ProfileMerge.Union(server, local);

        Assert.Equal(["x"], merged.Realms.Select(r => r.Id));
        Assert.Empty(merged.DeletedRealms);   // die Frage ist beantwortet, das Grab kann weg
    }

    // ── The selected realm is applied, not just uploaded ───────────────────────────────────────

    [Fact]
    public void Union_FreshMachine_TakesTheRealmTheAccountWasOn()
    {
        // A fresh install points at a preset, so "local always wins" would upload the field and then
        // ignore it forever - a half mechanism that looks like a feature.
        var server = new LauncherProfile
        {
            SelectedRealmId = "mine",
            Realms = [new ProfileRealm { Id = "mine", Name = "Mine", RealmlistAddress = "m.test", ClientKey = "1.12.1" }],
        };
        var local = new LauncherProfile { SelectedRealmId = "not-here-yet" };

        Assert.Equal("mine", ProfileMerge.Union(server, local).SelectedRealmId);
    }

    [Fact]
    public void Union_LocalPickIsReal_Wins()
    {
        var server = new LauncherProfile { SelectedRealmId = "server-choice" };
        var local = new LauncherProfile { SelectedRealmId = "elwynn" };   // a shipped preset

        Assert.Equal("elwynn", ProfileMerge.Union(server, local).SelectedRealmId);
    }

    // ── Two syncs at once must not undo each other ─────────────────────────────────────────────

    [Fact]
    public async Task Sync_RunningTwiceAtOnce_DoesNotRaceItself()
    {
        // Adding and removing realms each start a sync. Two runs in flight mean the older one holds a
        // stale snapshot, and if it wins the race it writes the OLD state back for good.
        var (svc, _, h) = Build(ConfigWith("mine"), "token",
            (HttpStatusCode.OK, """{"version":1,"profile":{"realms":[],"selectedRealmId":"","deletedRealmIds":[]}}"""),
            (HttpStatusCode.OK, """{"version":2,"profile":{"realms":[],"selectedRealmId":"","deletedRealmIds":[]}}"""));

        var a = svc.SyncAsync();
        var b = svc.SyncAsync();
        await Task.WhenAll(a, b);

        // Whatever the interleaving, the two runs never overlap: a GET is never followed by another
        // GET before its PUT.
        for (var i = 0; i + 1 < h.SentMethods.Count; i++)
            if (h.SentMethods[i] == "GET") Assert.Equal("PUT", h.SentMethods[i + 1]);
    }

    // ── Failure paths: the config must survive all of them untouched ───────────────────────────

    [Fact]
    public async Task Sync_OffByDefault_DoesNothing()
    {
        var cfg = ConfigWith("mine");
        cfg.ProfileSyncEnabled = false;
        var (svc, _, h) = Build(cfg, "token", (HttpStatusCode.OK, "{}"));

        Assert.Equal(ProfileSyncOutcome.Skipped, await svc.SyncAsync());
        Assert.Empty(h.SentMethods);   // not a single request without consent
    }

    [Fact]
    public async Task Sync_ServerDown_LeavesTheRealmsAlone()
    {
        // A 500 is "we could not check", never "you have no realms".
        var (svc, cfg, _) = Build(ConfigWith("mine"), "token", (HttpStatusCode.InternalServerError, ""));

        Assert.Equal(ProfileSyncOutcome.Unavailable, await svc.SyncAsync());
        Assert.Contains(cfg.Current.Realms, r => r.Id == "mine");
        Assert.Equal(0, cfg.Saves);
    }

    [Fact]
    public async Task Sync_UnreadableBody_IsNotAnEmptyProfile()
    {
        // A 200 with junk in it must not be read as "the server has nothing", or the next push would
        // wipe the stored copy.
        var (svc, cfg, _) = Build(ConfigWith("mine"), "token", (HttpStatusCode.OK, "not json"));

        Assert.Equal(ProfileSyncOutcome.Unavailable, await svc.SyncAsync());
        Assert.Contains(cfg.Current.Realms, r => r.Id == "mine");
    }

    [Fact]
    public async Task Sync_ExpiredToken_IsReportedNotSwallowed()
    {
        // The token lives 24 hours and is never renewed. Treating a 401 as "degraded" would mean a
        // profile that silently stops syncing on day two.
        var (svc, _, _) = Build(ConfigWith("mine"), "token", (HttpStatusCode.Unauthorized, ""));

        Assert.Equal(ProfileSyncOutcome.SignedOut, await svc.SyncAsync());
    }

    [Fact]
    public async Task Sync_ARealmAddedDuringTheRequest_SurvivesOurOwnWriteBack()
    {
        // A sync takes seconds of network time. If the write-back used the snapshot the run started
        // with, a realm the player added in those seconds would be wiped by our own stale copy - and
        // the queued rerun would then read the already-overwritten state and never notice.
        var cfg = new Cfg(ConfigWith("mine"));
        var h = new Handler(
            (HttpStatusCode.OK, """{"version":1,"profile":{"realms":[],"selectedRealmId":"","deletedRealmIds":[]}}"""),
            (HttpStatusCode.OK, """{"version":2,"profile":{"realms":[],"selectedRealmId":"","deletedRealmIds":[]}}"""));
        var svc = new HttpProfileSyncService(new HttpClient(h), cfg, new Auth("token"),
            new Serilog.LoggerConfiguration().CreateLogger());

        // The player adds a realm while the PUT is on the wire.
        h.BeforeResponse = i =>
        {
            if (i != 1) return;
            cfg.Current.Realms.Add(new RealmEntry
            {
                Id = "added-midflight",
                Name = "Mid",
                RealmlistAddress = "mid.test",
                ClientKey = "1.12.1",
            });
        };

        await svc.SyncAsync();

        Assert.Contains(cfg.Current.Realms, r => r.Id == "added-midflight");
        Assert.Contains(cfg.Current.Realms, r => r.Id == "mine");
    }

    // ── The happy paths ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_PullsARealmTheAccountCarries()
    {
        var server = """
            {"version":4,"profile":{"realms":[
              {"id":"other","name":"Other","realmlistAddress":"o.test","clientKey":"1.12.1","manifestUrl":""}],
              "selectedRealmId":"other"}}
            """;
        var (svc, cfg, h) = Build(ConfigWith("mine"), "token",
            (HttpStatusCode.OK, server),
            (HttpStatusCode.OK, """{"version":5,"profile":{"realms":[],"selectedRealmId":""}}"""));

        Assert.Equal(ProfileSyncOutcome.Synced, await svc.SyncAsync());
        Assert.Contains(cfg.Current.Realms, r => r.Id == "other");   // arrived from the account
        Assert.Contains(cfg.Current.Realms, r => r.Id == "mine");    // and nothing was lost
        Assert.Contains(cfg.Current.Realms, r => r.IsPreset);        // presets untouched
        Assert.Equal(5, cfg.Current.ProfileVersion);
        Assert.Equal(["GET", "PUT"], h.SentMethods);
    }

    [Fact]
    public async Task Sync_NothingToDo_DoesNotPush()
    {
        // Pushing on every start would bump the version forever and turn a quiet feature into a write
        // amplifier.
        var same = """
            {"version":9,"profile":{"realms":[
              {"id":"mine","name":"mine","realmlistAddress":"mine.example.test","clientKey":"1.12.1","manifestUrl":""}],
              "selectedRealmId":"elwynn"}}
            """;
        var (svc, _, h) = Build(ConfigWith("mine"), "token", (HttpStatusCode.OK, same));

        Assert.Equal(ProfileSyncOutcome.UpToDate, await svc.SyncAsync());
        Assert.Equal(["GET"], h.SentMethods);
    }

    [Fact]
    public async Task Sync_Conflict_MergesAgainstTheOtherMachineAndRetriesOnce()
    {
        // Someone wrote between our GET and our PUT. Their realm must survive ours.
        var get = """{"version":1,"profile":{"realms":[],"selectedRealmId":""}}""";
        var conflict = """
            {"error":{"code":"conflict"},"version":2,"profile":{"realms":[
              {"id":"theirs","name":"Theirs","realmlistAddress":"t.test","clientKey":"1.12.1","manifestUrl":""}],
              "selectedRealmId":""}}
            """;
        var ok = """{"version":3,"profile":{"realms":[],"selectedRealmId":""}}""";
        var (svc, cfg, h) = Build(ConfigWith("mine"), "token",
            (HttpStatusCode.OK, get), (HttpStatusCode.Conflict, conflict), (HttpStatusCode.OK, ok));

        Assert.Equal(ProfileSyncOutcome.Synced, await svc.SyncAsync());
        Assert.Contains(cfg.Current.Realms, r => r.Id == "theirs");
        Assert.Contains(cfg.Current.Realms, r => r.Id == "mine");
        Assert.Equal(["GET", "PUT", "PUT"], h.SentMethods);
        Assert.Contains("\"baseVersion\":2", h.SentBodies[2]);   // retried against THEIR version
    }

    [Fact]
    public async Task Sync_SecondConflict_GivesUpWithoutTouchingTheConfig()
    {
        var get = """{"version":1,"profile":{"realms":[],"selectedRealmId":""}}""";
        var conflict = """{"version":2,"profile":{"realms":[],"selectedRealmId":""}}""";
        var (svc, cfg, _) = Build(ConfigWith("mine"), "token",
            (HttpStatusCode.OK, get), (HttpStatusCode.Conflict, conflict), (HttpStatusCode.Conflict, conflict));

        Assert.Equal(ProfileSyncOutcome.Unavailable, await svc.SyncAsync());
        Assert.Equal(0, cfg.Saves);
    }
}
