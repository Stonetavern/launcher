using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// A client the player brought himself is not a damaged copy of ours.
///
/// <para>Report ST-KQYG-ARA3 (2026-08-20): the player had an original Blizzard Classic Era client in
/// <c>~/Downloads/WoW Classic 1.14.2/</c>. The launcher measured it against OUR package manifest, found
/// none of its 1106 files (the five "ok" were exemptions that are never looked for on disk), concluded
/// the client was out of date and offered an 8 GB update — which could not run, because the disk was
/// 4,5 GB short. The distinction these tests pin is the one that was missing: "this package is damaged"
/// versus "this package is not installed here".</para>
/// </summary>
public sealed class ForeignClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foreign-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>A manifest of our package: the proxy tree plus the client tree, both rooted at the
    /// package root, exactly as <c>modern-1.14.2-macos-files.json</c> is shaped.</summary>
    private static ClientFileManifest OurPackageManifest(int csvCount = 120)
    {
        var files = new List<ClientFileEntry>();
        for (var i = 0; i < csvCount; i++)
            files.Add(new ClientFileEntry { Path = $"Hermes/CSV/Table{i:D3}.csv", Size = 4, Sha256 = Sha("data") });
        files.Add(new ClientFileEntry
        {
            Path = "World of Warcraft/_classic_era_/WowClassic.exe",
            Size = 2,
            Sha256 = Sha("MZ"),
        });
        return new ClientFileManifest { Build = 42597, Files = files };
    }

    private static string Sha(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    [Fact]
    public async Task AForeignClient_IsReportedAsADifferentPackage_NotAsDamage()
    {
        // The shape a Blizzard client unpacks into: _classic_era_ directly under the folder, no Hermes,
        // no "World of Warcraft" level in between.
        var clientDir = Path.Combine(_root, "WoW Classic 1.14.2", "_classic_era_");
        Directory.CreateDirectory(clientDir);
        await File.WriteAllTextAsync(Path.Combine(clientDir, "WowClassic.exe"), "MZ");

        var report = await new ClientVerifyService(Log()).VerifyAsync(clientDir, OurPackageManifest());

        Assert.False(report.IsIntact);
        Assert.True(report.LooksLikeADifferentPackage,
            $"checkable={report.Checkable} found={report.FoundAtManifestPath} missing={report.Missing.Count} corrupt={report.Corrupt.Count} ok={report.Ok} total={report.Total}");
        Assert.Equal(0, report.FoundAtManifestPath);
        Assert.True(report.Checkable >= VerifyReport.MinimumEntriesForLayoutVerdict);
    }

    [Fact]
    public async Task AGenuinelyDamagedInstallOfOurs_IsStillDamage_NotAForeignClient()
    {
        // Our layout, most of it deleted — but SOME manifest files are where they belong. That evidence
        // is the whole difference: this one must stay repairable.
        var manifest = OurPackageManifest();
        var packageRoot = Path.Combine(_root, "Stonetavern");
        var clientDir = Path.Combine(packageRoot, "World of Warcraft", "_classic_era_");
        Directory.CreateDirectory(Path.Combine(packageRoot, "Hermes", "CSV"));
        Directory.CreateDirectory(clientDir);
        await File.WriteAllTextAsync(Path.Combine(clientDir, "WowClassic.exe"), "MZ");
        foreach (var i in Enumerable.Range(0, 5))
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "Hermes", "CSV", $"Table{i:D3}.csv"), "data");

        var report = await new ClientVerifyService(Log()).VerifyAsync(clientDir, manifest);

        Assert.False(report.IsIntact);
        Assert.False(report.LooksLikeADifferentPackage);
        Assert.True(report.FoundAtManifestPath > 0);
    }

    [Fact]
    public void ContentRoot_FindsThePackageRoot_EvenWhenTheFirstTwoDozenEntriesAreGone()
    {
        // The manifest is alphabetical, so its first entries are all Hermes/CSV/. When exactly that part
        // is missing, a 24-entry sample sees nothing and falls back to the exe directory — and a client
        // whose game files are fine reads as ~1100 files missing (Codex review 2026-08-24).
        var packageRoot = Path.Combine(_root, "Stonetavern");
        var clientDir = Path.Combine(packageRoot, "World of Warcraft", "_classic_era_");
        Directory.CreateDirectory(clientDir);
        File.WriteAllText(Path.Combine(clientDir, "WowClassic.exe"), "MZ");

        var paths = Enumerable.Range(0, 40).Select(i => $"Hermes/CSV/Table{i:D3}.csv")
            .Append("World of Warcraft/_classic_era_/WowClassic.exe")
            .ToList();

        var resolved = ContentRoot.Resolve(clientDir, paths);

        Assert.Equal(Path.GetFullPath(packageRoot), Path.GetFullPath(resolved));
    }
}
