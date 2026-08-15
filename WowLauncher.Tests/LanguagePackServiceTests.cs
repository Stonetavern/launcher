using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Getting a 1.12.1 language pack from the download host onto a player's install.
///
/// <para>The pack is a ~90 MB archive fetched over the network and unpacked into the directory that
/// also holds the client. Two things must therefore hold no matter what arrives: nothing is unpacked
/// before the checksum matches, and nothing lands anywhere except the one file a language pack is
/// allowed to carry.</para>
/// </summary>
public sealed class LanguagePackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "st-langpack-" + Guid.NewGuid().ToString("N"));

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private LanguagePackService NewService(IDownloadService downloads) =>
        new(downloads, new VanillaLocalePacks(Log()), Log());

    private static ManifestLanguagePack Pack(string locale) => new()
    {
        Locale = locale, Build = 5875, Url = $"https://example.invalid/lang-{locale}.zip",
        Sha256 = "deadbeef", Size = 1234,
    };

    /// <summary>A zip carrying the named entries, written wherever the fake download is asked to put
    /// its file.</summary>
    private static void WriteZip(string path, params (string Entry, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
    }

    [Fact]
    public async Task AnInstalledLanguage_IsActivatedWithoutDownloading()
    {
        var packFile = VanillaLocalePacks.PackPath(_root, "deDE");
        Directory.CreateDirectory(Path.GetDirectoryName(packFile)!);
        File.WriteAllText(packFile, "german-pack");
        var downloads = new FakeDownloads();
        var svc = NewService(downloads);

        var result = await svc.EnsureAsync(_root, "deDE", Pack("deDE"));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, downloads.Calls);
        Assert.Equal("german-pack",
            File.ReadAllText(Path.Combine(_root, "Data", VanillaLocalePacks.SlotFileName)));
    }

    [Fact]
    public async Task ADownloadedPack_IsUnpackedAndActivated()
    {
        var downloads = new FakeDownloads
        {
            OnDownload = dest => WriteZip(dest, ("Data/deDE/locale-deDE.MPQ", "german-pack")),
        };
        var svc = NewService(downloads);

        var result = await svc.EnsureAsync(_root, "deDE", Pack("deDE"));

        Assert.True(result.Ok, result.Error);
        Assert.Equal("german-pack",
            File.ReadAllText(Path.Combine(_root, "Data", VanillaLocalePacks.SlotFileName)));
    }

    /// <summary>A pack whose bytes do not match the manifest is never unpacked — and is deleted rather
    /// than kept, so the next attempt does not resume onto bytes already known to be wrong.</summary>
    [Fact]
    public async Task AWrongChecksum_UnpacksNothing()
    {
        var downloads = new FakeDownloads
        {
            HashOk = false,
            OnDownload = dest => WriteZip(dest, ("Data/deDE/locale-deDE.MPQ", "tampered")),
        };
        var svc = NewService(downloads);

        var result = await svc.EnsureAsync(_root, "deDE", Pack("deDE"));

        Assert.False(result.Ok);
        Assert.Equal("enUS", result.Locale);
        Assert.False(File.Exists(VanillaLocalePacks.PackPath(_root, "deDE")));
        Assert.False(File.Exists(Path.Combine(_root, "Data", VanillaLocalePacks.SlotFileName)));
    }

    /// <summary>The archive is not trusted to contain only what it should: an extra entry — a patched
    /// WoW.exe, a DLL beside it — must not be written anywhere.</summary>
    [Fact]
    public async Task ExtraEntriesInTheArchive_AreNotWritten()
    {
        var downloads = new FakeDownloads
        {
            OnDownload = dest => WriteZip(dest,
                ("Data/deDE/locale-deDE.MPQ", "german-pack"),
                ("WoW.exe", "not-the-real-one"),
                ("../evil.dll", "escape")),
        };
        var svc = NewService(downloads);

        var result = await svc.EnsureAsync(_root, "deDE", Pack("deDE"));

        Assert.True(result.Ok, result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "WoW.exe")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil.dll")));
    }

    /// <summary>An archive that carries a DIFFERENT language than the one asked for installs nothing.
    /// Unpacking it anyway would give the player a language they did not pick, and it would verify and
    /// extract cleanly on the way there.</summary>
    [Fact]
    public async Task AnArchiveWithTheWrongLanguage_InstallsNothing()
    {
        var downloads = new FakeDownloads
        {
            OnDownload = dest => WriteZip(dest, ("Data/frFR/locale-frFR.MPQ", "french-pack")),
        };
        var svc = NewService(downloads);

        var result = await svc.EnsureAsync(_root, "deDE", Pack("deDE"));

        Assert.False(result.Ok);
        Assert.False(File.Exists(VanillaLocalePacks.PackPath(_root, "frFR")));
        Assert.False(File.Exists(VanillaLocalePacks.PackPath(_root, "deDE")));
    }

    /// <summary>Coordinates that name another locale are refused rather than followed: a mis-built
    /// manifest must not be able to install German when the player picked Spanish.</summary>
    [Fact]
    public async Task CoordinatesForAnotherLocale_AreRefused()
    {
        var downloads = new FakeDownloads();
        var svc = NewService(downloads);

        var result = await svc.EnsureAsync(_root, "esES", Pack("deDE"));

        Assert.False(result.Ok);
        Assert.Equal(0, downloads.Calls);
    }

    /// <summary>No coordinates at all is the LAUNCH path: it applies what is installed and never
    /// downloads, so a missing pack leaves the player on the language they have instead of holding the
    /// game behind 90 MB.</summary>
    [Fact]
    public async Task WithoutCoordinates_NothingIsDownloaded()
    {
        var downloads = new FakeDownloads();
        var svc = NewService(downloads);

        var result = await svc.EnsureAsync(_root, "ruRU", pack: null);

        Assert.False(result.Ok);
        Assert.Equal("enUS", result.Locale);
        Assert.Equal(0, downloads.Calls);
    }

    [Fact]
    public async Task EnglishNeedsNoPack()
    {
        var downloads = new FakeDownloads();
        var svc = NewService(downloads);

        var result = await svc.EnsureAsync(_root, "enUS", pack: null);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, downloads.Calls);
    }

    /// <summary>The staging copy is a second 90 MB file on a player's disk. It goes away whether the
    /// install worked or not.</summary>
    [Fact]
    public async Task TheDownloadedArchive_IsNotLeftBehind()
    {
        var downloads = new FakeDownloads
        {
            OnDownload = dest => WriteZip(dest, ("Data/deDE/locale-deDE.MPQ", "german-pack")),
        };
        var svc = NewService(downloads);

        await svc.EnsureAsync(_root, "deDE", Pack("deDE"));

        var staging = Path.Combine(_root, "Data", ".lang-download");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFiles(staging).Any());
    }

    private sealed class FakeDownloads : IDownloadService
    {
        public int Calls { get; private set; }
        public bool HashOk { get; init; } = true;
        public Action<string>? OnDownload { get; init; }

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            OnDownload?.Invoke(destPath);
            return Task.FromResult(File.Exists(destPath)
                ? DownloadResult.Success
                : DownloadResult.Fail(DownloadFailure.Network));
        }

        public Task<bool> ExtractZipAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "A language pack must never go through the general extractor - it would write every " +
                "entry the archive happens to carry.");

        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("A language pack is not a client package.");

        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult(HashOk);
    }
}
