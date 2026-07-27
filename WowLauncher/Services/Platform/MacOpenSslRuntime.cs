namespace WowLauncher.Services.Platform;

/// <summary>Resolves the OpenSSL 3 runtime used by the native macOS HermesProxy.</summary>
public sealed class MacOpenSslRuntime
{
    public const string DyldLibraryPath = "DYLD_LIBRARY_PATH";
    public const string MissingRuntimeMessage =
        "The bundled OpenSSL runtime is missing. Reinstall the Stonetavern launcher.";

    private const string CryptoLibrary = "libcrypto.3.dylib";
    private const string SslLibrary = "libssl.3.dylib";
    private readonly string _bundleLibraryDirectory;
    private readonly string _homebrewLibraryDirectory;

    public MacOpenSslRuntime(string baseDirectory, string? homebrewLibraryDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _bundleLibraryDirectory = Path.GetFullPath(
            Path.Combine(baseDirectory, "..", "Resources", "openssl"));
        _homebrewLibraryDirectory = homebrewLibraryDirectory
            ?? "/opt/homebrew/opt/openssl@3/lib";
    }

    /// <summary>Returns the bundled library directory first. Homebrew is only a developer fallback
    /// when a launcher bundle has no OpenSSL directory at all; a partial bundle is a release error.</summary>
    public string? ResolveLibraryDirectory()
    {
        if (Directory.Exists(_bundleLibraryDirectory))
            return HasRequiredLibraries(_bundleLibraryDirectory) ? _bundleLibraryDirectory : null;

        return HasRequiredLibraries(_homebrewLibraryDirectory) ? _homebrewLibraryDirectory : null;
    }

    public IReadOnlyDictionary<string, string>? ResolveEnvironmentOverrides()
    {
        var libraryDirectory = ResolveLibraryDirectory();
        return libraryDirectory is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DyldLibraryPath] = libraryDirectory,
            };
    }

    private static bool HasRequiredLibraries(string directory) =>
        File.Exists(Path.Combine(directory, CryptoLibrary)) &&
        File.Exists(Path.Combine(directory, SslLibrary));
}

/// <summary>Fails before Hermes is spawned when the launcher runtime is incomplete.</summary>
public sealed class UnavailableGameProxy(string error) : IGameProxy
{
    public Task<GameProxyResult> StartAndWaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default) =>
        Task.FromResult(GameProxyResult.Failed(error));

    public Task StopAsync() => Task.CompletedTask;
}
