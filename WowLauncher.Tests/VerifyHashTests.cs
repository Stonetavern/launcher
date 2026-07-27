using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Guards the SHA256 invariant (Codex A2 / PLAN §2): an unverifiable artefact must be REFUSED,
/// never extracted. The empty-hash negative test is the placebo target — with the WP1(e) fix
/// reverted it must go red (documented in the WP1 report).
/// </summary>
public sealed class VerifyHashTests
{
    // SHA256("mechagon") — computed independently (sha256sum), so the positive test is not circular.
    private const string Content = "mechagon";
    private const string CorrectHash = "e4c570be5ae3bfc90858f3e78beab7538fd3cca3a82f139d6021aef9a02a5010";
    private const string WrongHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static DownloadService NewService() =>
        new(new HttpClient(), new Serilog.LoggerConfiguration().CreateLogger());

    private static async Task<string> WriteTempFileAsync(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wp1-verify-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task EmptyExpectedHash_HardFails()
    {
        var path = await WriteTempFileAsync(Content);
        try
        {
            var ok = await NewService().VerifyHashAsync(path, "");
            Assert.False(ok, "An empty expected hash must be a hard failure — never extract an unverifiable artefact.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task NullExpectedHash_HardFails()
    {
        // System.Text.Json can hand a non-nullable string property a null when the JSON key is
        // present with a null value (ServerManifest.ManifestFile.Sha256), so null must be a hard
        // failure exactly like empty/whitespace — never extract an unverifiable artefact (Codex F5a).
        var path = await WriteTempFileAsync(Content);
        try
        {
            var ok = await NewService().VerifyHashAsync(path, null!);
            Assert.False(ok, "A null expected hash must be a hard failure — never extract an unverifiable artefact.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UppercaseHexHash_Passes()
    {
        // The comparison is OrdinalIgnoreCase (Convert.ToHexString yields upper-case; the manifest
        // may too) — an upper-case digest must verify just like the lower-case one (Codex F5b).
        var path = await WriteTempFileAsync(Content);
        try
        {
            var ok = await NewService().VerifyHashAsync(path, CorrectHash.ToUpperInvariant());
            Assert.True(ok, "An upper-case hex digest must verify — the comparison is case-insensitive.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WhitespaceExpectedHash_HardFails()
    {
        var path = await WriteTempFileAsync(Content);
        try
        {
            var ok = await NewService().VerifyHashAsync(path, "   ");
            Assert.False(ok, "A whitespace-only expected hash must be treated as missing → hard failure.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CorrectHash_Passes()
    {
        var path = await WriteTempFileAsync(Content);
        try
        {
            var ok = await NewService().VerifyHashAsync(path, CorrectHash);
            Assert.True(ok, "A file whose SHA256 matches the expected digest must verify.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WrongHash_Fails()
    {
        var path = await WriteTempFileAsync(Content);
        try
        {
            var ok = await NewService().VerifyHashAsync(path, WrongHash);
            Assert.False(ok, "A file whose SHA256 differs from the expected digest must be rejected.");
        }
        finally { File.Delete(path); }
    }
}
