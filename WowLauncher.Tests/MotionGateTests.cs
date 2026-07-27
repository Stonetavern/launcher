using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The motion gate: the v3 shell must not animate anything while its window is minimised or hidden.
/// This is a hard product rule, not taste — the player spends far more time in the game than in the
/// launcher, and a launcher that keeps a core warm behind the game is a defect.
///
/// <para>The gate is a class ("live") on the shell Border that the code-behind drops when the window
/// goes away; every endless animation is selectored as a descendant of <c>Border.live</c> and stops
/// with it. That mechanism was already there and already half-wired: three endless animations
/// (Image.flicker, Ellipse.pulse, Ellipse.pulseGlow) live in the SHARED Styles.axaml with ungated
/// selectors, and the v3 markup wore those classes, so they kept running behind a minimised window.
/// "Halfway built looks exactly like built" — which is why the coverage is asserted here instead of
/// being claimed in a comment.</para>
///
/// <para>These are source-text assertions, not headless render tests: the test project deliberately
/// carries no Avalonia.Headless reference (no new packages). That is a real limitation, and the
/// tests are written so they fail loudly if the sources move, never silently pass.</para>
/// </summary>
public sealed partial class MotionGateTests
{
    // ── Source access ───────────────────────────────────────────────────────────────────────────

    /// <summary>The app source directory. Walks up from the test binary; asserts rather than
    /// returning null, so a moved tree is a red test and never a silently skipped one.</summary>
    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "WowLauncher");
            if (File.Exists(Path.Combine(candidate, "Styles.v3.axaml"))) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"WowLauncher source tree not found above {AppContext.BaseDirectory}");
    }

    private static string ReadSource(string relative)
    {
        var path = Path.Combine(AppDir(), relative);
        Assert.True(File.Exists(path), $"expected source file missing: {path}");
        return File.ReadAllText(path);
    }

    private static string StripComments(string xaml) =>
        MyRegex().Replace(xaml, " ");

    // ── Tiny AXAML models ───────────────────────────────────────────────────────────────────────

    private sealed partial record StyleBlock(string Selector, string Body)
    {
        public bool IsEndless =>
            MyRegex().IsMatch(Body);

        /// <summary>The compound selector the style actually lands on (everything after the last
        /// descendant step), split into element type and required classes.</summary>
        public IEnumerable<(string Type, HashSet<string> Classes)> Targets()
        {
            foreach (var alternative in Selector.Split(','))
            {
                var compound = alternative.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (compound is null) continue;
                var parts = compound.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var type = compound.StartsWith('.') ? "*" : parts[0];
                var classes = compound.StartsWith('.') ? parts.ToHashSet() : [.. parts.Skip(1)];
                // ":pointerover" and friends are pseudo-classes, not ambient state — strip them.
                yield return (type.Split(':')[0], classes.Select(c => c.Split(':')[0]).ToHashSet());
            }
        }

        public bool IsGated => Selector.Split(',').All(a => a.Trim().StartsWith("Border.live ", StringComparison.Ordinal));

        [GeneratedRegex("IterationCount\\s*=\\s*\"(INFINITE|Infinite)\"", RegexOptions.IgnoreCase, "de-AT")]
        private static partial Regex MyRegex();
    }

    private static IReadOnlyList<StyleBlock> ParseStyles(string relative)
    {
        var xaml = StripComments(ReadSource(relative));
        var blocks = new List<StyleBlock>();
        foreach (Match m in Regex.Matches(xaml, "<Style\\s+Selector=\"([^\"]+)\"\\s*>(.*?)</Style>",
                                          RegexOptions.Singleline))
            blocks.Add(new StyleBlock(m.Groups[1].Value, m.Groups[2].Value));
        Assert.NotEmpty(blocks);
        return blocks;
    }

    private sealed record Element(string Type, HashSet<string> Classes);

    /// <summary>Every element in a markup file with the class set it wears (static Classes="a b"
    /// plus every conditional Classes.x="{Binding …}").</summary>
    private static IReadOnlyList<Element> ParseElements(string relative)
    {
        var xaml = StripComments(ReadSource(relative));
        var elements = new List<Element>();
        foreach (Match m in Regex.Matches(xaml, "<([A-Za-z][\\w.]*)\\b([^<>]*)>", RegexOptions.Singleline))
        {
            var type = m.Groups[1].Value;
            if (type.Contains('.')) continue;   // property element, e.g. <Border.Background>
            var attrs = m.Groups[2].Value;
            var classes = new HashSet<string>();
            var plain = Regex.Match(attrs, "\\bClasses\\s*=\\s*\"([^\"]*)\"");
            if (plain.Success)
                foreach (var c in plain.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    classes.Add(c);
            foreach (Match cond in Regex.Matches(attrs, "\\bClasses\\.([\\w]+)\\s*="))
                classes.Add(cond.Groups[1].Value);
            if (classes.Count > 0) elements.Add(new Element(type, classes));
        }
        Assert.NotEmpty(elements);
        return elements;
    }

    /// <summary>The markup that the v3 shell renders. LoginView is included because the v3 shell is
    /// its only host (ShellV3Window.axaml embeds it in the friends sidebar when signed out).</summary>
    private static readonly string[] V3Markup = ["Views/ShellV3Window.axaml", "Views/LoginView.axaml"];

    private static readonly string[] AllStyleSheets = ["Styles.axaml", "Styles.v2.axaml", "Styles.v3.axaml"];

    // ── The finding this round exists for ───────────────────────────────────────────────────────

    [Fact]
    public void EveryEndlessAnimationReachableFromTheV3ShellIsBehindTheLiveGate()
    {
        var v3Elements = V3Markup.SelectMany(ParseElements).ToList();
        var endless = AllStyleSheets.SelectMany(ParseStyles).Where(s => s.IsEndless).ToList();

        // Sanity: the parser has to actually see the animations, or this test proves nothing.
        Assert.True(endless.Count >= 5, $"only {endless.Count} endless styles parsed — parser broken?");

        var ungated = new List<string>();
        foreach (var style in endless.Where(s => !s.IsGated))
            foreach (var (type, classes) in style.Targets())
                if (v3Elements.Any(e => e.Type == type && classes.IsSubsetOf(e.Classes)))
                    ungated.Add(style.Selector);

        Assert.True(ungated.Count == 0,
            "endless animations reachable from the v3 shell without the Border.live gate: "
            + string.Join(", ", ungated.Distinct()));
    }

    [Fact]
    public void TheV3ShellReallyWearsFiveGatedAmbientAnimations()
    {
        var v3Elements = V3Markup.SelectMany(ParseElements).ToList();
        var gatedEndless = AllStyleSheets.SelectMany(ParseStyles)
            .Where(s => s.IsEndless && s.IsGated)
            .Where(s => s.Targets().Any(t => v3Elements.Any(e => e.Type == t.Type && t.Classes.IsSubsetOf(e.Classes))))
            .Select(s => s.Selector)
            .ToList();

        // hearth, PLAY breathe, v3flicker, v3pulse, v3pulseGlow. If this drops, motion was deleted;
        // if it grows, the new one still had to pass the gate test above.
        Assert.Equal(5, gatedEndless.Count);
    }

    [Fact]
    public void TheKenBurnsTransitionIsGated_NotOnlyItsTimer()
    {
        var v3 = ParseStyles("Styles.v3.axaml");

        var withTransition = v3.Where(s => s.Selector.Contains("kenburns", StringComparison.Ordinal)
                                           && s.Body.Contains("TransformOperationsTransition", StringComparison.Ordinal))
                               .ToList();
        Assert.NotEmpty(withTransition);
        Assert.All(withTransition, s => Assert.True(s.IsGated,
            $"the 34s Ken Burns transition keeps compositing behind a hidden window: {s.Selector}"));
    }

    [Fact]
    public void TheGateItselfIsWiredToVisibilityAndWindowState()
    {
        var code = ReadSource("Views/ShellV3Window.axaml.cs");
        Assert.Contains("IsVisible && WindowState != WindowState.Minimized", code, StringComparison.Ordinal);
        Assert.Contains("Classes.Set(\"live\", live)", code, StringComparison.Ordinal);
        Assert.Contains("_kenBurns.Stop()", code, StringComparison.Ordinal);

        var markup = StripComments(ReadSource("Views/ShellV3Window.axaml"));
        Assert.Matches("<Border\\s+x:Name=\"Shell\"\\s+Classes=\"live\"", markup);
    }

    // ── Finding 2: the arrival animation must mean arrival ──────────────────────────────────────

    [Fact]
    public async Task APresenceChangeUpdatesTheRowInPlace_SoTheArrivalAnimationDoesNotReplay()
    {
        var friends = new ScriptedFriends(
        [
            new FriendPresence("aria", PresenceStatus.InGame, "In Barrens", "barrens"),
            new FriendPresence("bran", PresenceStatus.Online, "Idle", null),
        ]);
        var shell = NewShell(friends);
        shell.IsLoggedIn = true;

        await shell.LoadFriendsAsync();
        var first = shell.Friends[0];
        var second = shell.Friends[1];
        Assert.Equal("aria", first.Account);
        Assert.Equal("In Barrens", first.Activity);

        // Same roster, one friend walked into another zone — exactly what the 18s poll sees.
        friends.Roster =
        [
            new FriendPresence("aria", PresenceStatus.InGame, "In Elwynn", "elwynn"),
            new FriendPresence("bran", PresenceStatus.Online, "Idle", null),
        ];
        await shell.LoadFriendsAsync();

        // Same row objects → the ItemsControl keeps its containers → Grid.v3enterrow does not replay.
        Assert.Same(first, shell.Friends[0]);
        Assert.Same(second, shell.Friends[1]);
        Assert.Equal("In Elwynn", shell.Friends[0].Activity);
        Assert.Equal("elwynn", shell.Friends[0].Realm);
    }

    [Fact]
    public async Task ARealArrivalStillProducesANewRow()
    {
        var friends = new ScriptedFriends([new FriendPresence("aria", PresenceStatus.Online, "Idle", null)]);
        var shell = NewShell(friends);
        shell.IsLoggedIn = true;

        await shell.LoadFriendsAsync();
        var aria = shell.Friends[0];

        // The newcomer arrives in the same tick in which the resident changes activity: only the
        // newcomer may be a new row, or the whole list lifts in again around them.
        friends.Roster =
        [
            new FriendPresence("aria", PresenceStatus.InGame, "In Elwynn", "elwynn"),
            new FriendPresence("zul", PresenceStatus.Online, "Idle", null),
        ];
        await shell.LoadFriendsAsync();

        Assert.Equal(2, shell.Friends.Count);
        Assert.Same(aria, shell.Friends[0]);           // the resident did not "arrive" again
        Assert.Equal("In Elwynn", shell.Friends[0].Activity);
        Assert.Equal("zul", shell.Friends[1].Account); // the newcomer did
    }

    [Fact]
    public async Task AChangedPresenceRaisesTheViewFlags()
    {
        var friends = new ScriptedFriends([new FriendPresence("aria", PresenceStatus.Online, "Idle", null)]);
        var shell = NewShell(friends);
        shell.IsLoggedIn = true;
        await shell.LoadFriendsAsync();

        var row = shell.Friends[0];
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        friends.Roster = [new FriendPresence("aria", PresenceStatus.Offline, "", null)];
        await shell.LoadFriendsAsync();

        Assert.True(row.IsPresenceOffline);
        Assert.False(row.IsOnline);
        Assert.Contains(nameof(FriendRow.IsPresenceOffline), changed);
        Assert.Contains(nameof(FriendRow.Activity), changed);
    }

    // ── Fixture (the shell needs its whole service surface; all doubles are inert) ───────────────

    private sealed class ScriptedFriends(IReadOnlyList<FriendPresence> roster) : IFriendsPresenceService
    {
        public IReadOnlyList<FriendPresence> Roster = roster;

        public Task<IReadOnlyList<FriendPresence>> GetFriendsAsync(CancellationToken ct = default) =>
            Task.FromResult(Roster);
        public Task<AddFriendResult> AddFriendAsync(string account, CancellationToken ct = default) =>
            Task.FromResult(AddFriendResult.Rejected("test"));
    }

    private sealed class MemoryConfig : IConfigService
    {
        private LauncherConfig _current = new()
        {
            ManifestUrl = "https://downloads.example.invalid/manifest.json",
            RealmlistAddress = "play.stonetavern.app",
        };
        public LauncherConfig Load() => _current;
        public void Save(LauncherConfig config) => _current = config;
        public bool LastSaveSucceeded => true;
    }

    private sealed class NoManifest : IManifestService
    {
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);
        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    private sealed class NoClient : IClientService
    {
        public string? FindWowExe(string? configuredPath = null) => null;
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) => null;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) =>
            new Dictionary<int, string>();
        public int? DetectBuild(string wowDirectory) => null;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath) =>
            Task.FromResult(new GameLaunchResult(false, null, "test"));
    }

    private sealed class OfflineStatus : IServerStatusService
    {
        public Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default) =>
            Task.FromResult(new ServerStatusResult { Online = false, PlayerCount = 0 });
    }

    private sealed class NoDownload : IDownloadService
    {
        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult.Fail(DownloadFailure.Network));
        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> ExtractClientAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> VerifyHashAsync(string path, string expected, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class NoUpdate : IUpdateService
    {
        public Task<bool> CheckAndApplyAsync(ServerManifest? m, CancellationToken ct = default) => Task.FromResult(false);
        public LauncherUpdateNotice? CheckForNotice(ServerManifest? m) => null;
    }

    private sealed class NoNews : INewsService
    {
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(bool forceRefresh = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NewsItem>>([]);
    }

    private sealed class ExitNow : ILaunchExitPolicy
    {
        public Task<bool> ConfirmClientRunningAsync(string exePath) => Task.FromResult(false);
    }

    private sealed class TempPaths : IAppPaths
    {
        public string Root => _root;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-motion-" + Guid.NewGuid().ToString("N"));
        public string ConfigDir => _root;
        public string StateDir => _root;
        public string CacheDir => _root;
        public string LogDir => _root;
        public string ShareDir => _root;
        public string ConfigFilePath => Path.Combine(_root, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(_root, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(_root, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(_root, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(_root);
    }

    private sealed class SignedOutAuth : ILauncherAuthService
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

    private sealed class NoArmory : IArmoryService
    {
        public Task<ArmoryRoster> GetCharactersAsync(string realmId, CancellationToken ct = default) =>
            Task.FromResult(ArmoryRoster.Empty(ArmoryStatus.SignedOut));
    }

    private static ShellViewModel NewShell(IFriendsPresenceService friends)
    {
        var cfg = new MemoryConfig();
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        var play = new PlayViewModel(cfg, new NoManifest(), new NoClient(), new OfflineStatus(),
            new NoDownload(), new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(),
            paths, new FixedFolderPicker(paths.Root), log);
        var auth = new SignedOutAuth();
        return new ShellViewModel(play, new PatchNotesViewModel(new NoNews(), cfg, log),
            new SettingsViewModel(cfg, new NullFolderPicker()), friends, auth, new LoginViewModel(auth), cfg,
            new ArmoryViewModel(new NoArmory(), log));
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex MyRegex();
}
