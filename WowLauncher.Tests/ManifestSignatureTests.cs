using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Proofs for the manifest authenticity gate (2026-07-27). Until this layer existed the launcher
/// downloaded whatever <c>manifest.json</c> advertised and checked only the SHA-256 written in that
/// same file — which stops a corrupted download and nothing else, because whoever can rewrite the
/// manifest rewrites the hash in the same edit. These tests pin the property that fixes it: without a
/// signature made by the offline release key, the launcher does NOTHING — no download, no swap, and
/// not even a hint that a new version exists.
///
/// <para>Everything here runs without a network and without the real release key: the unit tests
/// generate a throwaway key pair per case, and the interop case uses a fixture signed by
/// <c>(internal design notes, not published)</c> with a throwaway key whose private half
/// was never stored. That fixture is the only thing that can catch a cross-language encoding drift
/// (python emits DER, the launcher expects fixed-width r||s) — a C#-signs/C#-verifies test would stay
/// green through exactly that bug.</para>
/// </summary>
public sealed class ManifestSignatureTests
{
    private static Serilog.ILogger Log => new Serilog.LoggerConfiguration().CreateLogger();

    private const string ManifestUrl = "https://downloads.example.invalid/manifest.json";
    private const string SignatureUrl = ManifestUrl + ".sig";

    // ─── Key helpers (throwaway keys, generated per test) ─────────────────

    private static (string PublicKeyBase64, ECDsa Key) NewKeyPair()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    /// <summary>Signs exactly as <c>deploy/sign-manifest.py</c> does: SHA-256, r||s fixed width, base64.</summary>
    private static string Sign(ECDsa key, byte[] payload) =>
        Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // ─── ManifestSignature: the four cases the gate stands or falls on ────

    [Fact]
    public void ValidSignature_IsAccepted()
    {
        var (pub, key) = NewKeyPair();
        using var _ = key;
        var manifest = Bytes("""{"product":"stonetavern-classic"}""");

        var verdict = new ManifestSignature(pub).Verify(manifest, Sign(key, manifest));

        Assert.True(verdict.Ok, verdict.Reason);
    }

    [Fact]
    public void TamperedManifest_IsRejected()
    {
        var (pub, key) = NewKeyPair();
        using var _ = key;
        var original = Bytes("""{"launcher":{"version":"2.0.0"}}""");
        var signature = Sign(key, original);

        // One byte of difference — the shape an attacker who can write the CDN would produce.
        var tampered = Bytes("""{"launcher":{"version":"9.0.0"}}""");

        var verdict = new ManifestSignature(pub).Verify(tampered, signature);

        Assert.False(verdict.Ok);
        Assert.Contains("does not match", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void MissingSignatureFile_IsRejected(string? signatureContent)
    {
        var (pub, key) = NewKeyPair();
        using var _ = key;

        var verdict = new ManifestSignature(pub).Verify(Bytes("{}"), signatureContent);

        Assert.False(verdict.Ok);
    }

    [Fact]
    public void SignatureFromAnotherKey_IsRejected()
    {
        var (pub, key) = NewKeyPair();
        using var _ = key;
        var (_, attackerKey) = NewKeyPair();
        using var __ = attackerKey;
        var manifest = Bytes("""{"launcher":{"version":"2.0.0"}}""");

        // Perfectly well-formed signature over the exact bytes — just not by the release key. This is
        // the "attacker signs their own manifest" case, and it must fail on the KEY, not the content.
        var verdict = new ManifestSignature(pub).Verify(manifest, Sign(attackerKey, manifest));

        Assert.False(verdict.Ok);
    }

    // ─── Fail-closed on the key itself ────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64-at-all!!")]
    [InlineData("AAAA")] // valid base64, not a key
    public void UnusableEmbeddedKey_RefusesEverything(string? publicKey)
    {
        var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var _ = signer;
        var manifest = Bytes("{}");

        var subject = new ManifestSignature(publicKey);

        Assert.False(subject.HasUsableKey);
        Assert.NotEmpty(subject.KeyProblem);
        // A correctly signed manifest must ALSO be refused: "no key" is never "then without a check".
        Assert.False(subject.Verify(manifest, Sign(signer, manifest)).Ok);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("QUFB")]                      // base64, but far too short to be a signature
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAA")]  // 18 bytes
    public void MalformedSignature_IsRejected(string signature)
    {
        var (pub, key) = NewKeyPair();
        using var _ = key;

        Assert.False(new ManifestSignature(pub).Verify(Bytes("{}"), signature).Ok);
    }

    [Fact]
    public void SignatureSurvivesTrailingNewline_BecauseTextFilesGrowThem()
    {
        var (pub, key) = NewKeyPair();
        using var _ = key;
        var manifest = Bytes("""{"a":1}""");

        Assert.True(new ManifestSignature(pub).Verify(manifest, Sign(key, manifest) + "\r\n").Ok);
    }

    // ─── Key rotation: several trusted keys at once ───────────────────────

    [Fact]
    public void DuringARotation_EitherTrustedKeyVerifies_ButAThirdDoesNot()
    {
        var (pubA, keyA) = NewKeyPair();
        using var _ = keyA;
        var (pubB, keyB) = NewKeyPair();
        using var __ = keyB;
        var (_, keyC) = NewKeyPair();
        using var ___ = keyC;
        var manifest = Bytes("""{"serial":1}""");

        // A launcher built during the transition carries the old key A and the new key B. Without this,
        // rotating the release key would permanently lock out every launcher already in the field: the
        // only thing that could teach them the new key is an update, and the update needs the new key.
        var subject = new ManifestSignature(pubA, pubB);

        Assert.True(subject.Verify(manifest, Sign(keyA, manifest)).Ok);
        Assert.True(subject.Verify(manifest, Sign(keyB, manifest)).Ok);
        // A key that was never embedded stays worthless, however well-formed its signature is.
        Assert.False(subject.Verify(manifest, Sign(keyC, manifest)).Ok);
    }

    [Fact]
    public void AnUnusableEntryDoesNotPoisonTheUsableOnes()
    {
        var (pub, key) = NewKeyPair();
        using var _ = key;
        var manifest = Bytes("{}");

        // A typo'd or retired entry next to a good one must not disable the good one — otherwise a
        // rotation with a copy/paste slip silently bricks updates for everyone.
        var subject = new ManifestSignature("not-a-key", pub, "");

        Assert.True(subject.HasUsableKey);
        Assert.True(subject.Verify(manifest, Sign(key, manifest)).Ok);
    }

    // ─── Cross-language interop: python signs, the launcher verifies ──────

    [Fact]
    public void PythonSignedFixture_IsAccepted_AndItsTamperedTwinIsNot()
    {
        var manifest = File.ReadAllBytes(FixturePath("manifest-signed.json"));
        var signature = File.ReadAllText(FixturePath("manifest-signed.json.sig"));
        var subject = new ManifestSignature(FixturePublicKeyBase64);

        var verdict = subject.Verify(manifest, signature);
        Assert.True(verdict.Ok, verdict.Reason);

        // Same fixture, one byte appended — the encoding is not accidentally accepting everything.
        var tampered = new byte[manifest.Length + 1];
        manifest.CopyTo(tampered, 0);
        tampered[^1] = (byte)' ';
        Assert.False(subject.Verify(tampered, signature).Ok);
    }

    [Fact]
    public void ShippedEmbeddedKey_IsWiredThroughStatically()
    {
        // Documents the deployment order rather than a value: as long as no release key is embedded,
        // ManifestSignature.Embedded refuses everything and the launcher offers no updates at all.
        // That is intended fail-closed behaviour, and it is why the key and the published
        // manifest.json.sig have to land together, before a build with this gate reaches players.
        Assert.Equal(
            ManifestSignature.EmbeddedPublicKeysBase64.Length == 0,
            !ManifestSignature.Embedded.HasUsableKey);
    }

    // ─── The gate end to end (stubbed HTTP, no network) ───────────────────

    [Fact]
    public async Task Gate_ReturnsSignedManifest_WhenSignatureIsValid()
    {
        var manifest = File.ReadAllBytes(FixturePath("manifest-signed.json"));
        var signature = File.ReadAllText(FixturePath("manifest-signed.json.sig"));
        var gate = NewGate(FixturePublicKeyBase64, manifest, signature);

        var result = await gate.AcquireVerifiedAsync();

        Assert.NotNull(result);
        Assert.Equal("2.0.0", result!.Launcher!.Version);
        Assert.Equal("3.0.0", result.LauncherLinux!.Version);
    }

    [Fact]
    public async Task Gate_ReturnsNull_WhenSignatureFileIsMissing()
    {
        var manifest = File.ReadAllBytes(FixturePath("manifest-signed.json"));
        var gate = NewGate(FixturePublicKeyBase64, manifest, signatureBody: null);

        Assert.Null(await gate.AcquireVerifiedAsync());
    }

    [Fact]
    public async Task Gate_ReturnsNull_WhenManifestWasAlteredAfterSigning()
    {
        var manifest = File.ReadAllBytes(FixturePath("manifest-signed.json"));
        var signature = File.ReadAllText(FixturePath("manifest-signed.json.sig"));
        var altered = Encoding.UTF8.GetString(manifest).Replace("\"2.0.0\"", "\"9.9.9\"", StringComparison.Ordinal);

        var gate = NewGate(FixturePublicKeyBase64, Bytes(altered), signature);

        Assert.Null(await gate.AcquireVerifiedAsync());
    }

    // ─── UpdateService: unsigned ⇒ no download, no swap, no hint ──────────

    [Fact]
    public async Task Windows_WithoutValidSignature_NeitherDownloadsNorSwapsNorHints()
    {
        var dl = new RecordingDownload();
        var swap = new RecordingSwap(isSupported: true);
        // The manifest handed in advertises a much newer launcher — under the old code this WAS the
        // auto-apply path. The only thing standing between it and a swap is the refusing gate.
        var manifest = TemptingManifest();
        var svc = new UpdateService(dl, Log, swap, new RefusingGate(), LauncherUpdateChannel.Windows,
            new Version(1, 0, 0));

        var applied = await svc.CheckAndApplyAsync(manifest);

        Assert.False(applied);
        Assert.Null(dl.LastDownloadUrl);            // nothing was fetched
        Assert.False(swap.Applied);                 // nothing was swapped
        Assert.Null(svc.CheckForNotice(manifest));  // and the player is told nothing either
    }

    [Fact]
    public async Task Linux_WithoutValidSignature_ShowsNoNotice()
    {
        var dl = new RecordingDownload();
        var swap = new RecordingSwap(isSupported: false);
        var manifest = TemptingManifest();
        var svc = new UpdateService(dl, Log, swap, new RefusingGate(), LauncherUpdateChannel.Linux,
            new Version(1, 0, 0));

        await svc.CheckAndApplyAsync(manifest);

        Assert.Null(svc.CheckForNotice(manifest));
        Assert.Null(dl.LastDownloadUrl);
    }

    [Fact]
    public async Task Notice_FallsSilentAgain_WhenALaterCheckFailsVerification()
    {
        // A verdict must not outlive the round that produced it: yesterday's valid signature is no
        // licence to keep advertising an update after the server started serving unsigned manifests.
        var manifest = TemptingManifest();
        var gate = new SwitchableGate(manifest);
        var svc = new UpdateService(new RecordingDownload(), Log, new RecordingSwap(false), gate,
            LauncherUpdateChannel.Linux, new Version(1, 0, 0));

        await svc.CheckAndApplyAsync(manifest);
        Assert.NotNull(svc.CheckForNotice(manifest));

        gate.Verified = null;
        await svc.CheckAndApplyAsync(manifest);
        Assert.Null(svc.CheckForNotice(manifest));
    }

    [Fact]
    public async Task NoticeWithoutAPrecedingCheck_IsSilent()
    {
        var svc = new UpdateService(new RecordingDownload(), Log, new RecordingSwap(false),
            new RefusingGate(), LauncherUpdateChannel.Linux, new Version(1, 0, 0));

        // Never verified anything → the passive path has no authority to speak.
        Assert.Null(svc.CheckForNotice(TemptingManifest()));
        await Task.CompletedTask;
    }

    // ─── Fixture + doubles ────────────────────────────────────────────────

    /// <summary>Public half of the throwaway key that signed <c>fixtures/manifest-signed.json</c> with
    /// <c>deploy/sign-manifest.py</c>. Test material only — never a release key; the private half was
    /// generated in a scratch directory and never stored.</summary>
    private const string FixturePublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/YkKNfZYBZ9bqGsQLC9gAysPtDHbUL4+maeYAnszGJ3XHhntJOVxI4bBbT5bO62AK5DHUOq+eHdJU5mzdSF+iA==";

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static ServerManifest TemptingManifest() => new()
    {
        Launcher = new ManifestFile
        {
            Version = "9.9.9",
            Url = "https://evil.example.invalid/WowLauncher.exe",
            Sha256 = "9999999999999999999999999999999999999999999999999999999999999999",
        },
        LauncherLinux = new ManifestFile
        {
            Version = "9.9.9",
            Url = "https://evil.example.invalid/WowLauncher.tar.xz",
            Sha256 = "9999999999999999999999999999999999999999999999999999999999999999",
        },
    };

    private static ManifestSignatureGate NewGate(string publicKey, byte[] manifestBody, string? signatureBody) =>
        new(new HttpClient(new StubServer(manifestBody, signatureBody)),
            new FixedConfig(ManifestUrl), new ManifestSignature(publicKey),
            // A real policy over a fresh in-memory floor: the gate must pass BOTH checks, and the
            // fixture is authored to satisfy the release policy (serial 42, stable, expires 2099).
            new ManifestReleasePolicy(new MemoryTrustStore(), Log), Log);

    private sealed class MemoryTrustStore : IManifestTrustStore
    {
        private long _highest;

        public long? ReadHighestSerial() => _highest;

        public void Remember(long serial) => _highest = Math.Max(_highest, serial);
    }

    /// <summary>Serves the manifest and (optionally) its signature; a null signature body answers 404,
    /// i.e. "the server has not published a signature".</summary>
    private sealed class StubServer(byte[] manifest, string? signature) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url == SignatureUrl)
            {
                return Task.FromResult(signature is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature) });
            }
            if (url == ManifestUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(manifest),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FixedConfig(string manifestUrl) : IConfigService
    {
        public bool LastSaveSucceeded => true;

        public LauncherConfig Load() => new() { ManifestUrl = manifestUrl };

        public void Save(LauncherConfig config) { }
    }

    /// <summary>The server published nothing usable — every check refuses.</summary>
    private sealed class RefusingGate : IManifestSignatureGate
    {
        public Task<ServerManifest?> AcquireVerifiedAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);
    }

    private sealed class SwitchableGate(ServerManifest? verified) : IManifestSignatureGate
    {
        public ServerManifest? Verified { get; set; } = verified;

        public Task<ServerManifest?> AcquireVerifiedAsync(CancellationToken ct = default) =>
            Task.FromResult(Verified);
    }

    private sealed class RecordingDownload : IDownloadService
    {
        public string? LastDownloadUrl { get; private set; }

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            LastDownloadUrl = url;
            return Task.FromResult(DownloadResult.Success);
        }

        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult(!string.IsNullOrWhiteSpace(expectedSha256));

        public Task<bool> ExtractZipAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class RecordingSwap(bool isSupported) : IUpdateSwapStrategy
    {
        public bool IsSupported { get; } = isSupported;

        public bool Applied { get; private set; }

        public bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null)
        {
            Applied = true;
            return true;
        }
    }
}
