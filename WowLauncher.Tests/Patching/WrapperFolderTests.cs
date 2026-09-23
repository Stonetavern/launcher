namespace WowLauncher.Tests.Patching;

using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using WowLauncher.Services;
using WowLauncher.Services.Patching;
using Xunit;

/// <summary>
/// E2E 2026-09-23: the first 1.12.1 install through the patch engine failed. The package ZIP wraps the
/// client in one folder (so a website download lands in a folder of its own), files.json is
/// root-relative, and the engine extracted the wrapper as it was: every file one level too deep,
/// VERIFY saw 57 foreign files, per-file downloaded the whole client a second time and still ended in
/// "verify failed". These pin the rule that decides what a wrapper is, and the extract that drops it.
/// </summary>
public sealed class WrapperFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wrap-" + Guid.NewGuid().ToString("N"));
    public WrapperFolderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    [Fact]
    public void ClassicPackage_HasAWrapper()
    {
        var zip = new[] { "Stonetavern-Classic-1.12.1-v1.6/WoW.exe", "Stonetavern-Classic-1.12.1-v1.6/Data/dbc.MPQ" };
        var files = new[] { "WoW.exe", "Data/dbc.MPQ" };
        Assert.Equal("Stonetavern-Classic-1.12.1-v1.6/", ClientPatchEngine.WrapperToStrip(zip, files));
    }

    [Fact]
    public void ModernTree_FolderFilesJsonNames_IsContent_NotAWrapper()
    {
        var zip = new[] { "World of Warcraft/_classic_era_/WowClassic.exe", "World of Warcraft/Data/data.000" };
        var files = new[] { "World of Warcraft/_classic_era_/WowClassic.exe", "World of Warcraft/Data/data.000" };
        Assert.Null(ClientPatchEngine.WrapperToStrip(zip, files));
    }

    [Fact]
    public void RootLevelFiles_OrTwoTopFolders_HaveNoWrapper()
    {
        Assert.Null(ClientPatchEngine.WrapperToStrip(new[] { "WoW.exe", "Data/dbc.MPQ" }, new[] { "WoW.exe" }));
        Assert.Null(ClientPatchEngine.WrapperToStrip(new[] { "A/WoW.exe", "B/x" }, new[] { "WoW.exe" }));
        Assert.Null(ClientPatchEngine.WrapperToStrip(Array.Empty<string>(), new[] { "WoW.exe" }));
    }

    [Fact]
    public async Task Extract_DropsTheWrapper_AndStillPreservesPlayerFiles()
    {
        var zipPath = Path.Combine(_dir, "c.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (name, body) in new[] { ("W/WoW.exe", "exe"), ("W/Data/dbc.MPQ", "mpq"), ("W/WTF/Config.wtf", "shipped") })
            {
                using var s = new StreamWriter(zip.CreateEntry(name).Open());
                s.Write(body);
            }
        }
        var dest = Path.Combine(_dir, "install");
        Directory.CreateDirectory(Path.Combine(dest, "WTF"));
        File.WriteAllText(Path.Combine(dest, "WTF", "Config.wtf"), "player");

        var outcome = await new DownloadService(new HttpClient(), Serilog.Core.Logger.None)
            .ExtractClientWithReasonAsync(zipPath, dest, freshInstall: true, stripTopFolder: "W/");

        Assert.True(outcome.Ok, $"failure={outcome.Failure}");
        Assert.Equal("exe", File.ReadAllText(Path.Combine(dest, "WoW.exe")));
        Assert.Equal("mpq", File.ReadAllText(Path.Combine(dest, "Data", "dbc.MPQ")));
        Assert.False(Directory.Exists(Path.Combine(dest, "W")));
        Assert.Equal("player", File.ReadAllText(Path.Combine(dest, "WTF", "Config.wtf")));
    }
}

/// <summary>E2E 2026-09-23: the first-install folder dialog opened at "/".</summary>
public sealed class FolderPickerStartTests
{
    [Fact]
    public void NoStartFolder_OpensAtHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, WowLauncher.Services.AvaloniaFolderPickerService.StartFolderOrHome(null));
        Assert.Equal(home, WowLauncher.Services.AvaloniaFolderPickerService.StartFolderOrHome("/does/not/exist-" + Guid.NewGuid()));
    }

    [Fact]
    public void AnExistingStartFolder_Wins()
    {
        var tmp = Path.GetTempPath();
        Assert.Equal(tmp, WowLauncher.Services.AvaloniaFolderPickerService.StartFolderOrHome(tmp));
    }
}
