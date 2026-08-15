using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The modern client package expands to a tree whose root sits TWO levels above the folder the
/// executable lives in, while the launcher stores only the executable's folder. Everything here
/// pins down that the manifest is measured against the package root regardless — the bug that made
/// Repair re-download 8.5 GB for a perfectly intact install (2026-08-12).
/// </summary>
public class ContentRootTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wl-croot-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Builds the real modern layout: Hermes/ beside "World of Warcraft/&lt;flavor&gt;/".</summary>
    private static (string packageRoot, string exeDir) NewModernTree()
    {
        var root = NewTempDir();
        var exeDir = Path.Combine(root, "World of Warcraft", "_classic_era_");
        Directory.CreateDirectory(exeDir);
        Write(root, "Hermes/CSV/AreaNames.csv", "area-names");
        Write(root, "Hermes/CSV/AuraSpells1.csv", "aura-spells");
        Write(root, "World of Warcraft/_classic_era_/WowClassic.exe", "exe");
        Write(root, "World of Warcraft/_classic_era_/Data/patch.mpq", "patch");
        return (root, exeDir);
    }

    [Fact]
    public void Resolve_WalksUpToThePackageRoot_ForTheModernLayout()
    {
        var (packageRoot, exeDir) = NewModernTree();
        try
        {
            var manifestPaths = new[]
            {
                "Hermes/CSV/AreaNames.csv",
                "Hermes/CSV/AuraSpells1.csv",
                "World of Warcraft/_classic_era_/WowClassic.exe",
            };

            var resolved = ContentRoot.Resolve(exeDir, manifestPaths);

            Assert.Equal(packageRoot, resolved);
        }
        finally { Directory.Delete(packageRoot, true); }
    }

    [Fact]
    public void Resolve_KeepsTheStartDirectory_ForTheFlatVanillaLayout()
    {
        // Vanilla ships WoW.exe and Data/ directly in the client folder: root == exe dir, and the
        // resolver must not wander upwards and start measuring against the parent.
        var root = NewTempDir();
        try
        {
            Write(root, "WoW.exe", "exe");
            Write(root, "Data/patch.MPQ", "patch");

            var resolved = ContentRoot.Resolve(root, new[] { "WoW.exe", "Data/patch.MPQ" });

            Assert.Equal(root, resolved);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Resolve_KeepsTheStartDirectory_WhenNothingMatchesAnywhere()
    {
        // A fresh install has no files yet. Guessing a parent here would extract outside the folder
        // the player picked, so the start directory has to win.
        var root = NewTempDir();
        try
        {
            var resolved = ContentRoot.Resolve(root, new[] { "Hermes/CSV/AreaNames.csv", "WoW.exe" });

            Assert.Equal(root, resolved);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Resolve_IgnoresDirectoryEntries()
    {
        // "WTF/" exists at several levels of a nested tree; voting on directory entries would pick
        // the wrong candidate. Only files may count.
        var (packageRoot, exeDir) = NewModernTree();
        try
        {
            Directory.CreateDirectory(Path.Combine(packageRoot, "WTF"));
            Directory.CreateDirectory(Path.Combine(exeDir, "WTF"));

            var resolved = ContentRoot.Resolve(exeDir, new[] { "WTF/", "Hermes/CSV/AreaNames.csv" });

            Assert.Equal(packageRoot, resolved);
        }
        finally { Directory.Delete(packageRoot, true); }
    }

    [Fact]
    public async Task Verify_ReportsAnIntactModernInstallAsIntact()
    {
        // The end-to-end shape of the real bug: a complete, untouched install measured through the
        // exe directory. Before the fix every entry came back "missing" and Repair pulled 8.5 GB.
        var (packageRoot, exeDir) = NewModernTree();
        try
        {
            var manifest = new ClientFileManifest
            {
                Build = 42597,
                Files =
                [
                    Entry(packageRoot, "Hermes/CSV/AreaNames.csv"),
                    Entry(packageRoot, "Hermes/CSV/AuraSpells1.csv"),
                    Entry(packageRoot, "World of Warcraft/_classic_era_/WowClassic.exe"),
                    Entry(packageRoot, "World of Warcraft/_classic_era_/Data/patch.mpq"),
                ],
            };

            var report = await new ClientVerifyService(Serilog.Log.Logger).VerifyAsync(exeDir, manifest);

            Assert.True(report.IsIntact,
                $"intact modern install reported {report.Missing.Count} missing / {report.Corrupt.Count} corrupt");
            Assert.Equal(4, report.Ok);
        }
        finally { Directory.Delete(packageRoot, true); }
    }

    [Fact]
    public async Task Verify_StillFindsARealDefect_InTheModernLayout()
    {
        // Guard against "fixed by never reporting anything": a genuinely damaged file must still be
        // caught once the root is resolved correctly.
        var (packageRoot, exeDir) = NewModernTree();
        try
        {
            var manifest = new ClientFileManifest
            {
                Build = 42597,
                Files =
                [
                    Entry(packageRoot, "Hermes/CSV/AreaNames.csv"),
                    new ClientFileEntry
                    {
                        Path = "World of Warcraft/_classic_era_/Data/patch.mpq",
                        Size = 999_999,
                        Sha256 = new string('a', 64),
                    },
                ],
            };

            var report = await new ClientVerifyService(Serilog.Log.Logger).VerifyAsync(exeDir, manifest);

            Assert.False(report.IsIntact);
            Assert.Contains("World of Warcraft/_classic_era_/Data/patch.mpq", report.Corrupt);
            Assert.Equal(1, report.Ok);
        }
        finally { Directory.Delete(packageRoot, true); }
    }

    private static ClientFileEntry Entry(string root, string rel)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(full);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return new ClientFileEntry
        {
            Path = rel,
            Size = bytes.Length,
            Sha256 = System.Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant(),
        };
    }
}
