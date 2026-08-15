using System;
using System.IO;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Proofs for the release policy that runs behind the signature check (Codex review 2026-07-27).
/// The gap it closes: a signature says "we published this", never "this is current". Without the three
/// bindings below, whoever controls delivery replays an older — genuine, correctly signed — manifest
/// and pins players on a known-vulnerable launcher build, and the signature check waves it through
/// because there is nothing wrong with it except its age.
///
/// <list type="bullet">
/// <item><b>serial</b> — monotone counter against a locally persisted high-water mark (replay).</item>
/// <item><b>expires</b> — bounds how long a captured manifest stays usable at all.</item>
/// <item><b>channel</b> — a staging manifest signed with the same key must not install on stable.</item>
/// </list>
///
/// Every field is mandatory: absence is a refusal, because the manifest is attacker-influenced input
/// and "delete a line to disable a check" is not a security property.
/// </summary>
public sealed class ManifestReleasePolicyTests
{
    private static Serilog.ILogger Log => new Serilog.LoggerConfiguration().CreateLogger();

    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static ServerManifest Manifest(long? serial = 7, string? expires = "2026-08-27T00:00:00Z",
        string? channel = "stable") => new()
        {
            Serial = serial,
            Expires = expires,
            Channel = channel,
        };

    private static ManifestReleasePolicy Policy(IManifestTrustStore store, string? channel = null,
        DateTimeOffset? now = null) =>
        new(store, Log, channel, new StubTime(now ?? Now));

    // ─── Anti-Rollback ────────────────────────────────────────────────────

    [Fact]
    public void OlderSerial_IsRefused_AndTheStoredFloorStaysWhereItWas()
    {
        var store = new MemoryStore(highest: 100);

        var verdict = Policy(store).Admit(Manifest(serial: 99));

        Assert.False(verdict.Ok);
        Assert.Contains("replay", verdict.Reason, StringComparison.Ordinal);
        // The floor must not move on a refusal — a rejected manifest that still lowered (or even
        // touched) the high-water mark would hand the attacker the very state he is attacking.
        Assert.Equal(100, store.Highest);
        Assert.False(store.WasWritten);
    }

    [Fact]
    public void SameSerial_IsAccepted_BecauseItIsTheSameReleaseFetchedAgain()
    {
        var store = new MemoryStore(highest: 100);

        Assert.True(Policy(store).Admit(Manifest(serial: 100)).Ok);
    }

    [Fact]
    public void HigherSerial_IsAccepted_AndRaisesTheFloor()
    {
        var store = new MemoryStore(highest: 100);

        Assert.True(Policy(store).Admit(Manifest(serial: 101)).Ok);
        Assert.Equal(101, store.Highest);
    }

    [Fact]
    public void UnreadableAntiRollbackState_RefusesEverything()
    {
        // Writes are atomic, so an unreadable floor is a signal, not an ordinary power-cut artefact.
        // Accepting here would silently disable rollback protection — the exact outcome an attacker
        // with local write access would engineer.
        Assert.False(Policy(new MemoryStore(highest: null)).Admit(Manifest()).Ok);
    }

    // ─── Expiry ───────────────────────────────────────────────────────────

    [Fact]
    public void ExpiredManifest_IsRefused()
    {
        var expired = Manifest(expires: "2026-07-01T00:00:00Z"); // 26 days before "now"

        var verdict = Policy(new MemoryStore(0)).Admit(expired);

        Assert.False(verdict.Ok);
        Assert.Contains("expired", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void JustInsideTheClockSkewGrace_IsAccepted()
    {
        // Expired 23 h ago: within the 24 h grace that exists for machines whose clock is genuinely
        // wrong (dead CMOS battery, resumed VM, UTC-vs-local confusion).
        var expires = Now.AddHours(-23).ToString("O");

        Assert.True(Policy(new MemoryStore(0)).Admit(Manifest(expires: expires)).Ok);
    }

    [Fact]
    public void JustOutsideTheClockSkewGrace_IsRefused()
    {
        var expires = Now.AddHours(-25).ToString("O");

        Assert.False(Policy(new MemoryStore(0)).Admit(Manifest(expires: expires)).Ok);
    }

    [Fact]
    public void UnparsableExpires_IsRefused() =>
        Assert.False(Policy(new MemoryStore(0)).Admit(Manifest(expires: "irgendwann")).Ok);

    // ─── Channel ──────────────────────────────────────────────────────────

    [Fact]
    public void ForeignChannel_IsRefused()
    {
        var verdict = Policy(new MemoryStore(0)).Admit(Manifest(channel: "canary"));

        Assert.False(verdict.Ok);
        Assert.Contains("channel", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelComparisonIgnoresCaseAndPadding_BecauseThatIsAnAuthoringSlipNotAnAttack() =>
        Assert.True(Policy(new MemoryStore(0)).Admit(Manifest(channel: " Stable ")).Ok);

    [Fact]
    public void ABetaLauncherRefusesAStableManifest_AndViceVersa()
    {
        Assert.False(Policy(new MemoryStore(0), channel: "beta").Admit(Manifest(channel: "stable")).Ok);
        Assert.True(Policy(new MemoryStore(0), channel: "beta").Admit(Manifest(channel: "beta")).Ok);
    }

    // ─── Missing fields, one at a time ────────────────────────────────────

    [Fact]
    public void MissingSerial_IsRefused() =>
        Assert.False(Policy(new MemoryStore(0)).Admit(Manifest(serial: null)).Ok);

    [Fact]
    public void MissingExpires_IsRefused() =>
        Assert.False(Policy(new MemoryStore(0)).Admit(Manifest(expires: null)).Ok);

    [Fact]
    public void MissingChannel_IsRefused() =>
        Assert.False(Policy(new MemoryStore(0)).Admit(Manifest(channel: null)).Ok);

    [Fact]
    public void NegativeSerial_IsRefused() =>
        Assert.False(Policy(new MemoryStore(0)).Admit(Manifest(serial: -1)).Ok);

    // ─── The real on-disk store ───────────────────────────────────────────

    [Fact]
    public void RealStore_FirstRunHasNoFloor_AndRemembersMonotonically()
    {
        using var paths = new TempPaths();
        var store = new ManifestTrustStore(paths, Log);

        Assert.Equal(0, store.ReadHighestSerial()); // no file yet → any serial is a legitimate start

        store.Remember(5);
        Assert.Equal(5, store.ReadHighestSerial());

        store.Remember(3); // a lower value must never pull the floor back down
        Assert.Equal(5, store.ReadHighestSerial());

        Assert.True(File.Exists(store.StatePath));
    }

    [Fact]
    public void RealStore_SurvivesAProcessBoundary_BecauseTheFloorIsThePointOfPersisting()
    {
        using var paths = new TempPaths();
        new ManifestTrustStore(paths, Log).Remember(77);

        Assert.Equal(77, new ManifestTrustStore(paths, Log).ReadHighestSerial());
    }

    [Fact]
    public void RealStore_CorruptFile_ReadsAsNull_WhichThePolicyTurnsIntoARefusal()
    {
        using var paths = new TempPaths();
        var store = new ManifestTrustStore(paths, Log);
        File.WriteAllText(store.StatePath, "{ \"highest_serial\": ");

        Assert.Null(store.ReadHighestSerial());
        Assert.False(new ManifestReleasePolicy(store, Log, null, new StubTime(Now)).Admit(Manifest()).Ok);
    }

    [Fact]
    public void RealStore_LivesInStateDir_NotInThePlayerEditableConfig()
    {
        using var paths = new TempPaths();
        var store = new ManifestTrustStore(paths, Log);

        Assert.StartsWith(paths.StateDir, store.StatePath, StringComparison.Ordinal);
        Assert.NotEqual(paths.ConfigFilePath, store.StatePath);
    }

    // ─── Doubles ──────────────────────────────────────────────────────────

    private sealed class MemoryStore(long? highest) : IManifestTrustStore
    {
        public long? Highest { get; private set; } = highest;

        public bool WasWritten { get; private set; }

        public long? ReadHighestSerial() => Highest;

        public void Remember(long serial)
        {
            WasWritten = true;
            if (Highest is null || serial > Highest)
                Highest = serial;
        }
    }

    private sealed class StubTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempPaths : IAppPaths, IDisposable
    {
        public readonly string Root = Path.Combine(
            Path.GetTempPath(), "st-trust-test-" + Guid.NewGuid().ToString("N"));

        public TempPaths() => Directory.CreateDirectory(Root);

        public string ConfigDir => Root;

        public string StateDir => Root;

        public string CacheDir => Root;

        public string LogDir => Root;

        public string ShareDir => Root;

        public string ConfigFilePath => Path.Combine(Root, "launcher_config.json");

        public string NewsCacheFilePath => Path.Combine(Root, "news-cache.json");

        public string ClientInstallDir(int gameBuild) => Path.Combine(Root, $"WoW-Client-{gameBuild}");

        public string ClientDownloadZip(int gameBuild) => Path.Combine(Root, $"WoW-Client-{gameBuild}.zip");

        public void EnsureDirectories() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* best-effort */ }
        }
    }
}
