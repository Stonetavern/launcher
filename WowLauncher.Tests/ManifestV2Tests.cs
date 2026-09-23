using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// S1 — Manifest v2 (ARCHITEKTUR-v2-patcher.md §2): the five additive fields on a client entry
/// (<c>files_sha256</c>, <c>files_base</c>, <c>deltas</c>, <c>protected</c>), the same
/// <c>protected</c> field on <see cref="ClientFileManifest"/>, and the hash binding
/// <see cref="ClientFileManifestLoader"/> enforces between a signed manifest's
/// <c>files_sha256</c> and the files.json it actually fetches.
///
/// <para>Deliberately proves the OLD contract stays intact: <see cref="ManifestContractTests"/>'s own
/// fixtures (<c>manifest-old.json</c>/<c>manifest-new.json</c>) are re-parsed here too, so this file
/// would fail first if the new properties ever changed how an old document deserialises.</para>
/// </summary>
public sealed class ManifestV2Tests
{
    private static Serilog.ILogger Log => new Serilog.LoggerConfiguration().CreateLogger();

    private static readonly JsonSerializerOptions CaseInsensitive =
        new() { PropertyNameCaseInsensitive = true };

    // ─── (a) client entry with all v2 fields parses ────────────────────────

    private const string ClientWithV2Fields = """
    {
      "build": 42597, "os": "windows", "version": "1.4",
      "url": "https://downloads.example.invalid/full.zip",
      "size": 8240519812,
      "sha256": "624808bb00000000000000000000000000000000000000000000000000000000",
      "files_url": "https://downloads.example.invalid/files.json",
      "files_sha256": "aaaa000000000000000000000000000000000000000000000000000000000",
      "files_base": "https://downloads.example.invalid/tree/",
      "deltas": [
        { "from": "1.3.1", "url": "https://downloads.example.invalid/1.3.1-to-1.4.pwr", "size": 153119,
          "sha256": "bbbb000000000000000000000000000000000000000000000000000000000",
          "sig_url": "https://downloads.example.invalid/1.3.1-to-1.4.pwr.sig",
          "sig_sha256": "cccc000000000000000000000000000000000000000000000000000000000" }
      ],
      "protected": ["WTF/", "Interface/AddOns/", "Screenshots/", "Cache/", "Logs/"]
    }
    """;

    [Fact]
    public void ClientEntry_WithAllV2Fields_Parses()
    {
        var client = JsonSerializer.Deserialize<ManifestFile>(ClientWithV2Fields, CaseInsensitive)!;

        Assert.Equal(42597, client.Build);
        Assert.Equal("windows", client.Os);
        Assert.Equal("https://downloads.example.invalid/files.json", client.FilesUrl);
        Assert.Equal("aaaa000000000000000000000000000000000000000000000000000000000", client.FilesSha256);
        Assert.Equal("https://downloads.example.invalid/tree/", client.FilesBase);
        Assert.Single(client.Deltas);
        Assert.Equal("1.3.1", client.Deltas[0].From);
        Assert.Equal(153119, client.Deltas[0].Size);
        Assert.Equal("https://downloads.example.invalid/1.3.1-to-1.4.pwr.sig", client.Deltas[0].SigUrl);
        Assert.Equal(
            new[] { "WTF/", "Interface/AddOns/", "Screenshots/", "Cache/", "Logs/" }, client.Protected);
    }

    // ─── (b) old client entry (no v2 fields) parses identically to today ───

    private const string ClientWithoutV2Fields = """
    {
      "build": 42597, "os": "windows", "version": "1.3.1",
      "url": "https://downloads.example.invalid/full.zip",
      "size": 8240519812,
      "sha256": "624808bb00000000000000000000000000000000000000000000000000000000"
    }
    """;

    [Fact]
    public void ClientEntry_WithoutV2Fields_YieldsNullsAndEmptyLists_NoException()
    {
        var client = JsonSerializer.Deserialize<ManifestFile>(ClientWithoutV2Fields, CaseInsensitive)!;

        Assert.Null(client.FilesUrl);
        Assert.Null(client.FilesSha256);
        Assert.Null(client.FilesBase);
        Assert.Empty(client.Deltas);
        Assert.Empty(client.Protected);
    }

    /// <summary>The existing WP4/WP6 contract fixtures must keep parsing unchanged now that
    /// <see cref="ManifestFile"/> carries five more optional properties — same fixtures
    /// <see cref="ManifestContractTests"/> already locks behaviour against.</summary>
    [Theory]
    [InlineData("manifest-old.json")]
    [InlineData("manifest-new.json")]
    public void PreexistingContractFixtures_StillParse(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName);
        var json = File.ReadAllText(path);

        var manifest = JsonSerializer.Deserialize<ServerManifest>(json, CaseInsensitive);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Launcher);
    }

    // ─── (f) ClientFileManifest.Protected round-trips via the source-generated context ─────

    [Fact]
    public void ClientFileManifest_Protected_ParsesViaSourceGeneratedContext()
    {
        const string json = """
        {"build":42597,"version":"1.4","files":[{"path":"WoW.exe","size":10,"sha256":"deadbeef"}],
         "protected":["WTF/","Cache/"]}
        """;
        var bytes = Encoding.UTF8.GetBytes(json);

        var manifest = JsonSerializer.Deserialize(bytes, ClientFileManifestJsonContext.Default.ClientFileManifest)!;

        Assert.Equal(new[] { "WTF/", "Cache/" }, manifest.Protected);
    }

    [Fact]
    public void ClientFileManifest_WithoutProtected_YieldsEmptyList()
    {
        const string json = """
        {"build":42597,"version":"1.4","files":[{"path":"WoW.exe","size":10,"sha256":"deadbeef"}]}
        """;
        var bytes = Encoding.UTF8.GetBytes(json);

        var manifest = JsonSerializer.Deserialize(bytes, ClientFileManifestJsonContext.Default.ClientFileManifest)!;

        Assert.Empty(manifest.Protected);
    }

    // ─── ClientFileManifestLoader — hash binding (ManifestTrustException) ──

    private sealed class StubHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
            return Task.FromResult(response);
        }
    }

    private static byte[] FilesJsonBytes(string extraPath = "") =>
        Encoding.UTF8.GetBytes(
            $$"""
            {"build":42597,"version":"1.4","files":[
              {"path":"WoW.exe","size":10,"sha256":"deadbeef"}{{extraPath}}
            ]}
            """);

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static ClientFileManifestLoader LoaderFor(byte[] body, out StubHandler handler,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        handler = new StubHandler(body, status);
        return new ClientFileManifestLoader(new HttpClient(handler), Log);
    }

    /// <summary>(c) A one-byte-manipulated files.json must be rejected, never silently accepted.
    /// Placebo probe executed during implementation: with the hash comparison commented out, this test
    /// went RED (no exception thrown) before the check was restored — see task report.</summary>
    [Fact]
    public async Task LoadAsync_TamperedFilesJson_ThrowsManifestTrustException()
    {
        var original = FilesJsonBytes();
        var correctHash = Sha256Hex(original);
        // Change one digit of a still-valid JSON document (structure intact) so the failure proves the
        // HASH comparison, not a JSON-parse error catching the tamper for the wrong reason.
        var tamperedJson = Encoding.UTF8.GetString(original).Replace("\"size\":10", "\"size\":11");
        Assert.NotEqual(Encoding.UTF8.GetString(original), tamperedJson); // guard against a no-op replace
        var tampered = Encoding.UTF8.GetBytes(tamperedJson);

        var loader = LoaderFor(tampered, out _);
        var client = new ManifestFile
        {
            Build = 42597,
            FilesUrl = "https://downloads.example.invalid/files.json",
            FilesSha256 = correctHash,
        };

        var ex = await Assert.ThrowsAsync<ManifestTrustException>(() => loader.LoadAsync(client));
        Assert.Contains("files_sha256", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>(d) v2 fields (here: deltas) without a files_sha256 binding must be refused — an
    /// unbound files.json cannot be trusted to drive delta/per-file downloads.</summary>
    [Fact]
    public async Task LoadAsync_DeltasWithoutFilesSha256_ThrowsManifestTrustException()
    {
        var bytes = FilesJsonBytes();
        var loader = LoaderFor(bytes, out _);
        var client = new ManifestFile
        {
            Build = 42597,
            FilesUrl = "https://downloads.example.invalid/files.json",
            FilesSha256 = null,
            Deltas = { new ManifestDelta { From = "1.3.1", Url = "https://downloads.example.invalid/d.pwr" } },
        };

        var ex = await Assert.ThrowsAsync<ManifestTrustException>(() => loader.LoadAsync(client));
        Assert.Contains("files_sha256", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Same refusal when it is <c>files_base</c> rather than <c>deltas</c> that is present
    /// without a hash binding.</summary>
    [Fact]
    public async Task LoadAsync_FilesBaseWithoutFilesSha256_ThrowsManifestTrustException()
    {
        var bytes = FilesJsonBytes();
        var loader = LoaderFor(bytes, out _);
        var client = new ManifestFile
        {
            Build = 42597,
            FilesUrl = "https://downloads.example.invalid/files.json",
            FilesBase = "https://downloads.example.invalid/tree/",
        };

        await Assert.ThrowsAsync<ManifestTrustException>(() => loader.LoadAsync(client));
    }

    /// <summary>No files_sha256 and no v2 fields → exactly today's behaviour: parses, unauthenticated
    /// beyond its own entries (which <c>ClientVerifyService</c> re-checks per file anyway).</summary>
    [Fact]
    public async Task LoadAsync_NoV2FieldsAtAll_ParsesWithoutHashCheck()
    {
        var bytes = FilesJsonBytes();
        var loader = LoaderFor(bytes, out _);
        var client = new ManifestFile
        {
            Build = 42597,
            FilesUrl = "https://downloads.example.invalid/files.json",
        };

        var manifest = await loader.LoadAsync(client);

        Assert.NotNull(manifest);
        Assert.Equal(42597, manifest!.Build);
    }

    [Fact]
    public async Task LoadAsync_ValidHashWithV2Fields_Parses()
    {
        var bytes = FilesJsonBytes();
        var hash = Sha256Hex(bytes);
        var loader = LoaderFor(bytes, out _);
        var client = new ManifestFile
        {
            Build = 42597,
            FilesUrl = "https://downloads.example.invalid/files.json",
            FilesSha256 = hash,
            FilesBase = "https://downloads.example.invalid/tree/",
        };

        var manifest = await loader.LoadAsync(client);

        Assert.NotNull(manifest);
    }

    [Fact]
    public async Task LoadAsync_NoFilesUrl_ReturnsNull_NeverCallsHttp()
    {
        var loader = LoaderFor(FilesJsonBytes(), out var handler);

        var manifest = await loader.LoadAsync(new ManifestFile());

        Assert.Null(manifest);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>A bad path INSIDE an otherwise correctly-hashed files.json must still be rejected —
    /// the hash binding proves the document is the one that was signed off on, not that its content is
    /// safe to act on.</summary>
    [Fact]
    public async Task LoadAsync_BadPathInsideValidatedFilesJson_ThrowsManifestTrustException()
    {
        var bytes = FilesJsonBytes(""",{"path":"../evil","size":1,"sha256":"deadbeef"}""");
        var hash = Sha256Hex(bytes);
        var loader = LoaderFor(bytes, out _);
        var client = new ManifestFile
        {
            Build = 42597,
            FilesUrl = "https://downloads.example.invalid/files.json",
            FilesSha256 = hash,
        };

        await Assert.ThrowsAsync<ManifestTrustException>(() => loader.LoadAsync(client));
    }

    // ─── (e) path policy: five hostile paths, each rejected on its own ─────

    [Theory]
    [InlineData("../x")]
    [InlineData("C:/x")]
    [InlineData("/x")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("CON")]                      // R1: reserved Windows names, bare
    [InlineData("con.txt")]                  // ... and with an extension
    [InlineData("Data/PRN")]                 // ... in a subdirectory
    [InlineData("LPT9.log")]                 // ... COM1-9/LPT1-9 are reserved too
    [InlineData("aux")]                      // ... case-insensitive
    public void PathPolicy_RejectsHostilePath(string path)
    {
        Assert.Throws<ManifestTrustException>(() => ClientFilePathPolicy.Validate(path));
    }

    /// <summary>R1: the NUL case gets its own fact, not an InlineData row — xunit truncates a NUL in a
    /// theory argument, so the row would have tested "Data/" and passed with the rule disabled
    /// (measured 2026-09-20: the whole reserved-name placebo run left it green).</summary>
    [Fact]
    public void PathPolicy_RejectsANulCharacter()
    {
        Assert.Throws<ManifestTrustException>(() => ClientFilePathPolicy.Validate("Data/" + '\0' + "hidden"));
    }

    [Theory]
    [InlineData("Data/patch.mpq")]
    [InlineData("WoW.exe")]
    [InlineData("Interface/AddOns/Foo/Foo.lua")]
    [InlineData("Data/CONSOLE.log")]         // not reserved — only the exact stem CON is
    [InlineData("Hermes/bin/JimsProxy-linux-x64")]
    public void PathPolicy_AllowsOrdinaryRelativePath(string path)
    {
        ClientFilePathPolicy.Validate(path); // must not throw
    }

    // ─── R1: the filesystem half — a symlink already on disk must not redirect a write ─────

    [Fact]
    public void PathPolicy_IsInsideRoot_AllowsPathsUnderTheRoot()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation needs privileges there
        var tmp = Patching.PatchingFakes.NewTempDir();
        var root = Path.Combine(tmp, "root");
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        File.WriteAllText(Path.Combine(root, "Data", "a.bin"), "x");

        Assert.True(ClientFilePathPolicy.IsInsideRoot(root, Path.Combine(root, "Data", "a.bin"), out var reason), reason);
        Assert.True(ClientFilePathPolicy.IsInsideRoot(root, Path.Combine(root, "Data", "not-yet.bin"), out reason), reason);
    }

    [Fact]
    public void PathPolicy_IsInsideRoot_RefusesASymlinkedDirectoryLeadingOutside()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation needs privileges there
        var tmp = Patching.PatchingFakes.NewTempDir();
        var root = Path.Combine(tmp, "root");
        var outside = Path.Combine(tmp, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "Data"), outside);

        Assert.False(ClientFilePathPolicy.IsInsideRoot(root, Path.Combine(root, "Data", "evil.bin"), out var reason));
        Assert.Contains("outside", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathPolicy_IsInsideRoot_RefusesASymlinkedFileLeadingOutside()
    {
        if (OperatingSystem.IsWindows()) return;
        var tmp = Patching.PatchingFakes.NewTempDir();
        var root = Path.Combine(tmp, "root");
        Directory.CreateDirectory(root);
        var secret = Path.Combine(tmp, "secret.bin");
        File.WriteAllText(secret, "secret");
        File.CreateSymbolicLink(Path.Combine(root, "WoW.exe"), secret);

        Assert.False(ClientFilePathPolicy.IsInsideRoot(root, Path.Combine(root, "WoW.exe"), out _));
    }

    // ─── (f) KONZEPT §13: the per-file "os" tag is fail-closed on an unknown value ─────

    [Fact]
    public void PathPolicy_ValidateOs_AllowsNullAndTheThreeKnownValues()
    {
        ClientFilePathPolicy.ValidateOs(null); // absent = every OS, must not throw
        ClientFilePathPolicy.ValidateOs([]);
        ClientFilePathPolicy.ValidateOs(["windows"]);
        ClientFilePathPolicy.ValidateOs(["Linux"]); // case-insensitive
        ClientFilePathPolicy.ValidateOs(["macos"]);
    }

    [Theory]
    [InlineData("wine")]
    [InlineData("android")]
    [InlineData("")]
    public void PathPolicy_ValidateOs_RejectsAnythingNotOnTheKnownList(string bogus)
    {
        Assert.Throws<ManifestTrustException>(() => ClientFilePathPolicy.ValidateOs([bogus]));
    }

    [Fact]
    public async Task LoadAsync_RejectsAFilesJsonWithAnUnknownOsValue()
    {
        var bytes = FilesJsonBytes(""",{"path":"Hermes/JimsProxy","size":1,"sha256":"deadbeef","os":["amiga"]}""");
        var hash = Sha256Hex(bytes);
        var loader = LoaderFor(bytes, out _);
        var client = new ManifestFile
        {
            Build = 42597,
            FilesUrl = "https://downloads.example.invalid/files.json",
            FilesSha256 = hash,
        };

        await Assert.ThrowsAsync<ManifestTrustException>(() => loader.LoadAsync(client));
    }
}
