using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Guards the Repair-without-redownload contract (deploy/MANIFEST-SCHEMA.md §files_url): the whole
/// point of a per-file manifest is telling missing from corrupt from fine WITHOUT re-hashing the
/// world, and never flagging player-owned data. Every case here has a matching mutation probe in the
/// implementation report — a test that would stay green with its own check removed is a placebo.
/// </summary>
public sealed class ClientVerifyServiceTests
{
    private static ClientVerifyService NewService() =>
        new(new Serilog.LoggerConfiguration().CreateLogger());

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cvs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string dir, string relPath, string content)
    {
        var full = Path.Combine(dir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Sha256Of(string content)
    {
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static ClientFileEntry Entry(string path, string content) =>
        new() { Path = path, Size = System.Text.Encoding.UTF8.GetByteCount(content), Sha256 = Sha256Of(content) };

    private static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort tmp cleanup */ }
    }

    [Fact]
    public async Task IntactTree_ReportsIntact_NoFalseAlarms()
    {
        var dir = NewTempDir();
        try
        {
            WriteFile(dir, "WoW.exe", "exe-content");
            WriteFile(dir, "Data/patch.mpq", "patch-content");
            var manifest = new ClientFileManifest
            {
                Build = 5875,
                Files =
                [
                    Entry("WoW.exe", "exe-content"),
                    Entry("Data/patch.mpq", "patch-content"),
                ],
            };

            var report = await NewService().VerifyAsync(dir, manifest);

            Assert.True(report.IsIntact, "An install that matches the manifest byte-for-byte must report intact.");
            Assert.Equal(2, report.Ok);
            Assert.Empty(report.Missing);
            Assert.Empty(report.Corrupt);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task MissingFile_IsReportedMissing()
    {
        var dir = NewTempDir();
        try
        {
            WriteFile(dir, "WoW.exe", "exe-content");
            // Data/patch.mpq deliberately absent.
            var manifest = new ClientFileManifest
            {
                Build = 5875,
                Files =
                [
                    Entry("WoW.exe", "exe-content"),
                    Entry("Data/patch.mpq", "patch-content"),
                ],
            };

            var report = await NewService().VerifyAsync(dir, manifest);

            Assert.False(report.IsIntact);
            Assert.Contains("Data/patch.mpq", report.Missing);
            Assert.Empty(report.Corrupt);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task SizeMismatch_IsCorrupt_WithoutHashing()
    {
        var dir = NewTempDir();
        try
        {
            // The trap: the on-disk content's REAL hash is put in the manifest (so if the size stage
            // were skipped, hashing would say "match" - Ok, not Corrupt), but the declared size is
            // wrong. Only a size check that runs and is trusted BEFORE hashing catches this; a
            // service that hashes first (or instead) would wrongly call this file fine.
            const string onDisk = "short";
            WriteFile(dir, "Data/patch.mpq", onDisk);
            var manifest = new ClientFileManifest
            {
                Build = 5875,
                Files = [new ClientFileEntry { Path = "Data/patch.mpq", Size = 999_999, Sha256 = Sha256Of(onDisk) }],
            };

            var report = await NewService().VerifyAsync(dir, manifest);

            Assert.False(report.IsIntact,
                "A declared size that does not match the file on disk must fail verification even " +
                "though the content hash (if it were computed) would match - size is checked first.");
            Assert.Contains("Data/patch.mpq", report.Corrupt);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task SameSize_DifferentContent_IsCorrupt_ViaHash()
    {
        var dir = NewTempDir();
        try
        {
            // "abcde" and "xyzzz" are both 5 bytes: only a hash catches this, not the size stage.
            const string onDisk = "abcde";
            const string expected = "xyzzz";
            Assert.Equal(onDisk.Length, expected.Length);
            WriteFile(dir, "Data/patch.mpq", onDisk);
            var manifest = new ClientFileManifest
            {
                Build = 5875,
                Files = [Entry("Data/patch.mpq", expected)],
            };

            var report = await NewService().VerifyAsync(dir, manifest);

            Assert.False(report.IsIntact);
            Assert.Contains("Data/patch.mpq", report.Corrupt);
        }
        finally { DeleteDir(dir); }
    }

    [Theory]
    [InlineData("WTF/config.wtf")]
    [InlineData("Screenshots/shot1.tga")]
    [InlineData("realmlist.wtf")]
    // The modern client ships its tree nested under "World of Warcraft/<flavor>/", so the same
    // player-owned paths arrive with a prefix. Only the flat Vanilla layout was covered before,
    // which is why a leading-prefix-only Preserve rule looked correct for a year.
    [InlineData("World of Warcraft/_classic_era_/WTF/Config.wtf")]
    [InlineData("World of Warcraft/_classic_era_/WTF/SavedVariables/Blizzard_Console.lua")]
    [InlineData("World of Warcraft/_classic_era_/Screenshots/shot1.tga")]
    [InlineData("World of Warcraft/_classic_era_/realmlist.wtf")]
    public async Task PreservedPaths_AreNeverFlagged_EvenWhenWrong(string preservedPath)
    {
        var dir = NewTempDir();
        try
        {
            // Player-edited on disk: wrong size AND wrong content vs. the manifest entry.
            WriteFile(dir, preservedPath, "player-edited-completely-different-content");
            var manifest = new ClientFileManifest
            {
                Build = 5875,
                Files = [Entry(preservedPath, "original-shipped-content")],
            };

            var report = await NewService().VerifyAsync(dir, manifest);

            Assert.True(report.IsIntact, $"{preservedPath} is player-owned data the extractor never overwrites — it must never be reported as a defect.");
            Assert.DoesNotContain(preservedPath, report.Missing);
            Assert.DoesNotContain(preservedPath, report.Corrupt);
        }
        finally { DeleteDir(dir); }
    }

    [Theory]
    [InlineData("Interface/AddOns/JimsPlus/Core.lua")]
    [InlineData("World of Warcraft/_classic_era_/Interface/AddOns/JimsPlus/Core.lua")]
    public async Task ShippedServerAddon_IsRepairable_NotTreatedAsPlayerData(string addonPath)
    {
        // The bundled server addon travels inside the client package and nowhere else — no addon
        // catalogue carries it. If Preserve covered Interface/AddOns/, every addon update would
        // silently stop reaching existing installs. A damaged copy must be reported as a defect.
        var dir = NewTempDir();
        try
        {
            WriteFile(dir, addonPath, "locally-modified-or-damaged");
            var manifest = new ClientFileManifest
            {
                Build = 42597,
                Files = [Entry(addonPath, "the-shipped-version")],
            };

            var report = await NewService().VerifyAsync(dir, manifest);

            Assert.False(report.IsIntact, "a damaged shipped addon must be repairable");
            Assert.Contains(addonPath, report.Corrupt);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task PlayerOwnAddon_IsNeverTouched_BecauseItIsNotInTheManifest()
    {
        // The player's own addons are safe by construction rather than by a preserve rule: they
        // appear in neither the manifest nor the zip, so nothing ever looks at them.
        var dir = NewTempDir();
        try
        {
            WriteFile(dir, "Interface/AddOns/MyOwnAddon/addon.lua", "player-authored");
            var manifest = new ClientFileManifest
            {
                Build = 42597,
                Files = [Entry("Data/patch.mpq", "shipped")],
            };
            WriteFile(dir, "Data/patch.mpq", "shipped");

            var report = await NewService().VerifyAsync(dir, manifest);

            Assert.True(report.IsIntact);
            Assert.Equal(1, report.Total);
            Assert.True(File.Exists(Path.Combine(dir, "Interface", "AddOns", "MyOwnAddon", "addon.lua")));
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task PreservedPath_AlsoNotFlaggedWhenMissing()
    {
        // A player who deleted their WTF folder should not have Repair scream about it either -
        // Preserve wins before the missing-file check even runs.
        var dir = NewTempDir();
        try
        {
            var manifest = new ClientFileManifest
            {
                Build = 5875,
                Files = [Entry("WTF/config.wtf", "original-shipped-content")],
            };

            var report = await NewService().VerifyAsync(dir, manifest);

            Assert.True(report.IsIntact);
            Assert.Empty(report.Missing);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task Progress_IsReportedForEveryFile()
    {
        var dir = NewTempDir();
        try
        {
            WriteFile(dir, "a.txt", "aaa");
            WriteFile(dir, "b.txt", "bbb");
            var manifest = new ClientFileManifest
            {
                Build = 5875,
                Files = [Entry("a.txt", "aaa"), Entry("b.txt", "bbb")],
            };

            var seen = new System.Collections.Generic.List<int>();
            var progress = new Progress<VerifyProgress>(p => seen.Add(p.Checked));

            await NewService().VerifyAsync(dir, manifest, progress);
            // Progress callbacks are marshalled asynchronously by SynchronizationContext.Post in some
            // hosts; give them a beat to land before asserting (xunit has no SynchronizationContext by
            // default so in practice this is synchronous, but this keeps the assertion honest either way).
            await Task.Delay(10);

            Assert.Equal([1, 2], seen);
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>
    /// A manifest.json exactly like every one served today (no <c>files_url</c> anywhere) must
    /// deserialise with <see cref="ManifestFile.FilesUrl"/> null on every entry — the additive field
    /// changes nothing about the existing wire contract. PlayViewModel.Repair() reads that null and
    /// takes the old always-re-download path (proven by the mutation probe in the implementation
    /// report, not re-provable here without dragging in the whole download pipeline).
    /// </summary>
    [Fact]
    public void ManifestWithoutFilesUrl_DeserialisesWithNullFilesUrl_OldPathUnaffected()
    {
        const string json = """
        {
          "product": "stonetavern-classic",
          "current_version": "1.12.1",
          "build_date": "2026-05-30",
          "base": {
            "version": "1.12.1",
            "url": "https://downloads.stonetavern.app/client/vanilla-1.12.1.zip",
            "size": 5368709120,
            "sha256": "abc123"
          }
        }
        """;

        var manifest = JsonSerializer.Deserialize<ServerManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Base);
        Assert.Null(manifest.Base!.FilesUrl);
        // Everything else on Base still reads exactly as before - the new field is purely additive.
        Assert.Equal("https://downloads.stonetavern.app/client/vanilla-1.12.1.zip", manifest.Base.Url);
        Assert.Equal("abc123", manifest.Base.Sha256);
    }
}
