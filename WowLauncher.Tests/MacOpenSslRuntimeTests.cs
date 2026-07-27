using System;
using System.IO;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

public sealed class MacOpenSslRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mac-openssl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveLibraryDirectory_BundledRuntimeWinsOverHomebrewFallback()
    {
        var baseDirectory = Path.Combine(_root, "Stonetavern.app", "Contents", "MacOS");
        var bundled = Path.Combine(_root, "Stonetavern.app", "Contents", "Resources", "openssl");
        var homebrew = Path.Combine(_root, "homebrew");
        AddLibraries(bundled);
        AddLibraries(homebrew);

        var runtime = new MacOpenSslRuntime(baseDirectory, homebrew);

        Assert.Equal(bundled, runtime.ResolveLibraryDirectory());
        Assert.Equal(bundled, runtime.ResolveEnvironmentOverrides()![MacOpenSslRuntime.DyldLibraryPath]);
    }

    [Fact]
    public void ResolveLibraryDirectory_RejectsPartialBundledRuntimeInsteadOfUsingFallback()
    {
        var baseDirectory = Path.Combine(_root, "Stonetavern.app", "Contents", "MacOS");
        var bundled = Path.Combine(_root, "Stonetavern.app", "Contents", "Resources", "openssl");
        var homebrew = Path.Combine(_root, "homebrew");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "libcrypto.3.dylib"), "test");
        AddLibraries(homebrew);

        var runtime = new MacOpenSslRuntime(baseDirectory, homebrew);

        Assert.Null(runtime.ResolveLibraryDirectory());
        Assert.Null(runtime.ResolveEnvironmentOverrides());
    }

    private static void AddLibraries(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "libcrypto.3.dylib"), "test");
        File.WriteAllText(Path.Combine(directory, "libssl.3.dylib"), "test");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
