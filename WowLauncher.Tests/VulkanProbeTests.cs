using System;
using System.IO;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The Vulkan pre-check that replaces a crash on world entry with a sentence. Every manifest here is
/// written by the test: reading the dev machine's real driver would make this pass for reasons that
/// have nothing to do with the code, and would stay green if the parser returned a constant.
/// </summary>
public sealed class VulkanProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vulkan-tests-" + Guid.NewGuid().ToString("N"));

    private string Dir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteIcd(string dir, string fileName, string apiVersion) =>
        File.WriteAllText(
            Path.Combine(dir, fileName),
            "{\"file_format_version\":\"1.0.0\",\"ICD\":{\"library_path\":\"libvulkan_x.so\"," +
            "\"api_version\":\"" + apiVersion + "\"}}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void NoManifests_ReportsNoDriver()
    {
        var result = VulkanProbe.FromDirs([Dir("empty")]);

        Assert.False(result.DriverFound);
        Assert.False(result.MeetsModernClientRequirement);
    }

    [Fact]
    public void MissingDirectory_ReportsNoDriver_WithoutThrowing()
    {
        var result = VulkanProbe.FromDirs([Path.Combine(_root, "never-created")]);

        Assert.False(result.DriverFound);
    }

    [Fact]
    public void ModernDriver_MeetsTheRequirement()
    {
        var dir = Dir("modern");
        WriteIcd(dir, "radeon_icd.json", "1.4.303");

        var result = VulkanProbe.FromDirs([dir]);

        Assert.True(result.DriverFound);
        Assert.Equal(new Version(1, 4, 303), result.ApiVersion);
        Assert.True(result.MeetsModernClientRequirement);
    }

    /// <summary>1.2 is a real driver, and it is not enough. Reporting it as "found, therefore fine"
    /// is the failure this check exists to catch: the client would start and then die on world entry.</summary>
    [Fact]
    public void OldDriver_IsFoundButDoesNotMeetTheRequirement()
    {
        var dir = Dir("old");
        WriteIcd(dir, "old_icd.json", "1.2.170");

        var result = VulkanProbe.FromDirs([dir]);

        Assert.True(result.DriverFound);
        Assert.False(result.MeetsModernClientRequirement);
    }

    [Fact]
    public void ExactlyOnePointThree_MeetsTheRequirement()
    {
        var dir = Dir("boundary");
        WriteIcd(dir, "icd.json", "1.3.0");

        Assert.True(VulkanProbe.FromDirs([dir]).MeetsModernClientRequirement);
    }

    [Fact]
    public void HighestVersionAcrossDirectoriesWins()
    {
        var a = Dir("a");
        var b = Dir("b");
        WriteIcd(a, "one.json", "1.1.0");
        WriteIcd(b, "two.json", "1.3.280");

        var result = VulkanProbe.FromDirs([a, b]);

        Assert.Equal(new Version(1, 3, 280), result.ApiVersion);
    }

    /// <summary>A file we cannot parse must not count as a driver. Counting it would turn a corrupt
    /// manifest into a false "Vulkan is fine" and put the crash back where it was.</summary>
    [Fact]
    public void MalformedOrForeignJson_IsNotCountedAsADriver()
    {
        var dir = Dir("junk");
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ this is not json");
        File.WriteAllText(Path.Combine(dir, "other.json"), """{"layer":{"name":"VK_LAYER_x"}}""");

        var result = VulkanProbe.FromDirs([dir]);

        Assert.False(result.DriverFound);
    }

    [Fact]
    public void ManifestWithoutApiVersion_IsNotCountedAsADriver()
    {
        var dir = Dir("noversion");
        File.WriteAllText(
            Path.Combine(dir, "icd.json"),
            """{"file_format_version":"1.0.0","ICD":{"library_path":"libvulkan_x.so"}}""");

        Assert.False(VulkanProbe.FromDirs([dir]).DriverFound);
    }

    [Fact]
    public void GoodManifestBesideABrokenOne_StillCounts()
    {
        var dir = Dir("mixed");
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ nope");
        WriteIcd(dir, "good.json", "1.3.1");

        var result = VulkanProbe.FromDirs([dir]);

        Assert.True(result.DriverFound);
        Assert.True(result.MeetsModernClientRequirement);
    }

    [Fact]
    public void DefaultSearchDirs_IncludeTheStandardSystemLocation() =>
        Assert.Contains("/usr/share/vulkan/icd.d", VulkanProbe.DefaultSearchDirs());
}
