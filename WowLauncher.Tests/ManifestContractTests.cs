using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// WP4 Manifest-Contract (Codex A5). Locks the additive <c>launcher_linux</c> schema and the
/// platform-aware update behaviour across the four combinations the plan calls out:
///
/// <list type="table">
/// <item>(a) OLD manifest (no launcher_linux) + Windows → behaves exactly as today (reads launcher, auto-applies).</item>
/// <item>(b) OLD manifest + Linux → no hint, no error, no download, no swap.</item>
/// <item>(c) NEW manifest (with launcher_linux) + Windows → launcher_linux is IGNORED, launcher wins.</item>
/// <item>(d) NEW manifest + Linux → notify with the launcher_linux version, still no download/swap.</item>
/// </list>
///
/// Fixtures are synthetic JSON (no real URLs/hashes) copied next to the test assembly — they are the
/// exact on-wire shape WP6 will serve, proven here before the prod manifest is ever touched.
/// </summary>
public sealed class ManifestContractTests
{
    // The running assembly is treated as 1.4.0 for every case so the fixture versions
    // (launcher 2.0.0, launcher_linux 3.0.0) are unambiguously "newer". Injected, so the test is
    // independent of the real AssemblyVersion.
    private static readonly Version Current = new(1, 4, 0);

    private static Serilog.ILogger Log => new Serilog.LoggerConfiguration().CreateLogger();

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static ServerManifest Load(string name)
    {
        var json = File.ReadAllText(FixturePath(name));
        // Mirror ManifestService's exact deserialisation path (reflection + case-insensitive).
        return JsonSerializer.Deserialize<ServerManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"fixture {name} deserialised to null");
    }

    private static UpdateService Service(LauncherUpdateChannel channel, FakeDownload dl, FakeSwap swap) =>
        new(dl, Log, swap, channel, Current);

    // ─── Deserialisation contract ─────────────────────────────────────────

    [Fact]
    public void OldManifest_HasNoLauncherLinux_ButLauncherIntact()
    {
        var m = Load("manifest-old.json");
        Assert.NotNull(m.Launcher);
        Assert.Equal("2.0.0", m.Launcher!.Version);
        Assert.Null(m.LauncherLinux); // absent field → null, never an error
    }

    [Fact]
    public void NewManifest_LauncherLinuxIsTopLevel_NotNestedInLauncher()
    {
        var m = Load("manifest-new.json");
        Assert.NotNull(m.Launcher);
        Assert.Equal("2.0.0", m.Launcher!.Version); // Windows field untouched by the addition
        Assert.NotNull(m.LauncherLinux);
        Assert.Equal("3.0.0", m.LauncherLinux!.Version);
        Assert.Equal(
            "https://downloads.stonetavern.app/launcher/WowLauncher-3.0.0-linux-x64.tar.xz",
            m.LauncherLinux.Url);
        Assert.Equal(62914560, m.LauncherLinux.Size);
        Assert.Equal("3333333333333333333333333333333333333333333333333333333333333333", m.LauncherLinux.Sha256);
    }

    // ─── (a) OLD + Windows → behaves as today (auto-apply of launcher) ─────

    [Fact]
    public async Task A_OldManifest_Windows_AutoAppliesLauncherAsToday()
    {
        var dl = new FakeDownload();
        var swap = new FakeSwap(isSupported: true);
        var svc = Service(LauncherUpdateChannel.Windows, dl, swap);
        var m = Load("manifest-old.json");

        var applied = await svc.CheckAndApplyAsync(m);

        Assert.True(applied); // launcher 2.0.0 > 1.4.0 → download, verify, swap launched — shipped behaviour
        Assert.Equal(m.Launcher!.Url, dl.LastDownloadUrl);
        Assert.True(swap.Applied);
        Assert.Null(svc.CheckForNotice(m)); // Windows never emits a passive notice
    }

    // ─── (b) OLD + Linux → no hint, no error, no download, no swap ─────────

    [Fact]
    public async Task B_OldManifest_Linux_NoHintNoDownloadNoSwap()
    {
        var dl = new FakeDownload();
        var swap = new FakeSwap(isSupported: false);
        var svc = Service(LauncherUpdateChannel.Linux, dl, swap);
        var m = Load("manifest-old.json");

        Assert.Null(svc.CheckForNotice(m)); // no launcher_linux → no hint (and no throw)

        var applied = await svc.CheckAndApplyAsync(m);
        Assert.False(applied);
        Assert.Null(dl.LastDownloadUrl); // KEIN Download-Start on Linux
        Assert.False(swap.Applied);      // KEIN Swap
    }

    // ─── (c) NEW + Windows → launcher_linux IGNORED, launcher wins ─────────

    [Fact]
    public async Task C_NewManifest_Windows_IgnoresLauncherLinux_LauncherWins()
    {
        var dl = new FakeDownload();
        var swap = new FakeSwap(isSupported: true);
        var svc = Service(LauncherUpdateChannel.Windows, dl, swap);
        var m = Load("manifest-new.json");

        var applied = await svc.CheckAndApplyAsync(m);

        Assert.True(applied);
        Assert.Equal(m.Launcher!.Url, dl.LastDownloadUrl);       // the Windows launcher was fetched…
        Assert.NotEqual(m.LauncherLinux!.Url, dl.LastDownloadUrl); // …NOT the Linux one
        Assert.Null(svc.CheckForNotice(m)); // Windows ignores launcher_linux entirely
    }

    // ─── (d) NEW + Linux → notify with correct version, still no apply ─────

    [Fact]
    public async Task D_NewManifest_Linux_NotifiesWithLauncherLinuxVersion()
    {
        var dl = new FakeDownload();
        var swap = new FakeSwap(isSupported: false);
        var svc = Service(LauncherUpdateChannel.Linux, dl, swap);
        var m = Load("manifest-new.json");

        var notice = svc.CheckForNotice(m);

        Assert.NotNull(notice);
        Assert.Equal("3.0.0", notice!.Version);                 // reads launcher_linux, not launcher (2.0.0)
        Assert.Equal(UpdateService.DownloadPage, notice.DownloadPage);

        // Notify only — never auto-applies (PLAN §1.5 / WP7).
        var applied = await svc.CheckAndApplyAsync(m);
        Assert.False(applied);
        Assert.Null(dl.LastDownloadUrl);
        Assert.False(swap.Applied);
    }

    // ─── A newer-than-current Linux build is required for a notice ─────────

    [Fact]
    public void Notice_IsNull_WhenLauncherLinuxNotNewerThanCurrent()
    {
        var m = Load("manifest-new.json");
        // Pretend the running build already equals the advertised Linux build (3.0.0) → no notice.
        var svc = new UpdateService(new FakeDownload(), Log, new FakeSwap(false),
            LauncherUpdateChannel.Linux, new Version(3, 0, 0));
        Assert.Null(svc.CheckForNotice(m));
    }

    // ─── Version-Contract-Matrix (Codex-Gate WP4: Schema = strikt numerisches System.Version,
    //     "v"-Präfix toleriert, Unparsebares laut verwerfen — nie still als "kein Update") ────

    private static ServerManifest LinuxManifest(string version) => new()
    {
        LauncherLinux = new ManifestFile { Version = version, Url = "https://example.invalid/l" },
    };

    private static UpdateService LinuxService(Version current) =>
        new(new FakeDownload(), Log, new FakeSwap(false), LauncherUpdateChannel.Linux, current);

    [Fact]
    public void Notice_IsNull_OnDowngrade() =>
        Assert.Null(LinuxService(new Version(4, 0, 0)).CheckForNotice(LinuxManifest("3.0.0")));

    [Fact]
    public void Notice_IsNull_OnGarbageVersion() =>
        Assert.Null(LinuxService(new Version(1, 0, 0)).CheckForNotice(LinuxManifest("not-a-version")));

    [Fact]
    public void Notice_IsNull_OnSemVerPrerelease_SchemaIsStrictlyNumeric() =>
        Assert.Null(LinuxService(new Version(1, 0, 0)).CheckForNotice(LinuxManifest("3.0.0-beta.1")));

    [Fact]
    public void Notice_Appears_WithToleratedVPrefix()
    {
        var notice = LinuxService(new Version(3, 0, 0)).CheckForNotice(LinuxManifest("v4.0.0"));
        Assert.NotNull(notice);
        Assert.Equal("4.0.0", notice!.Version);
    }

    [Fact]
    public void FourComponent_IsNewerThanThreeComponent_SystemVersionSemantics()
    {
        // System.Version: "3.0.0.0" (Revision 0) > new Version(3,0,0) (Revision -1). Dokumentierte
        // Konsequenz (MANIFEST-SCHEMA.md): Manifest-Versionen dreigliedrig pflegen.
        var notice = LinuxService(new Version(3, 0, 0)).CheckForNotice(LinuxManifest("3.0.0.0"));
        Assert.NotNull(notice);
    }

    // ─── Test doubles ─────────────────────────────────────────────────────

    private sealed class FakeDownload : IDownloadService
    {
        public string? LastDownloadUrl { get; private set; }

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            LastDownloadUrl = url;
            return Task.FromResult(DownloadResult.Success); // pretend the bytes arrived
        }

        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult(!string.IsNullOrWhiteSpace(expectedSha256)); // honour the hard-hash gate

        public Task<bool> ExtractZipAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeSwap(bool isSupported) : IUpdateSwapStrategy
    {
        public bool IsSupported { get; } = isSupported;
        public bool Applied { get; private set; }

        public bool ApplySwap(string newExePath, string currentExePath, string appDir)
        {
            Applied = true;
            return true;
        }
    }
}
