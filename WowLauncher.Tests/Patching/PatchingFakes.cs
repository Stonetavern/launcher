using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Patching;
using WowLauncher.Services.Platform;

namespace WowLauncher.Tests.Patching;

/// <summary>Test doubles shared by the <see cref="ClientPatchEngine"/> test suite. Kept deliberately
/// simple/in-memory (no HTTP) so PLAN/PerFile/Delta-fallback/foreign-file tests run in milliseconds;
/// the two cases that genuinely need HTTP semantics (resume, cancel-mid-download) use the real
/// <see cref="WowLauncher.Services.DownloadService"/> against a fake <see cref="HttpMessageHandler"/>
/// instead (see <see cref="ClientPatchEnginePerFileNetworkTests"/>).</summary>
internal static class PatchingFakes
{
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"st-patch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static ClientFileEntry Entry(string path, byte[] content) => new()
    {
        Path = path,
        Size = content.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
    };

    public static void WriteFile(string root, string relPath, byte[] content)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }
}

/// <summary>Fetches a files.json handed to it at construction time — no HTTP, no trust checks (those
/// are <see cref="ClientFileManifestLoader"/>'s own, already-tested job).</summary>
internal sealed class FakeManifestLoader : IClientFileManifestLoader
{
    private readonly ClientFileManifest? _manifest;
    public FakeManifestLoader(ClientFileManifest? manifest) => _manifest = manifest;
    public Task<ClientFileManifest?> LoadAsync(ManifestFile client, CancellationToken ct = default) =>
        Task.FromResult(_manifest);
}

/// <summary>In-memory "server": URLs map to byte content, looked up by whatever
/// <see cref="ManifestFile.FilesBase"/>/<see cref="ManifestDelta.Url"/> the test wires up. Simulates
/// exactly the two operations <see cref="ClientPatchEngine"/> needs from <see cref="IDownloadService"/>
/// — a full-file GET-with-verify and a ZIP extract — without any real I/O beyond the local temp dirs
/// the tests already use.</summary>
internal sealed class FakeDownloadService : IDownloadService
{
    public readonly Dictionary<string, byte[]> Content = new(StringComparer.Ordinal);
    public readonly HashSet<string> FailUrls = [];
    public readonly List<string> Requested = [];
    /// <summary>What <see cref="ExtractClientWithReasonAsync"/> should write into destDir, keyed by
    /// the zip path it was asked to extract.</summary>
    public readonly Dictionary<string, Dictionary<string, byte[]>> ZipContents = new(StringComparer.Ordinal);

    public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Requested.Add(url);
        if (FailUrls.Contains(url))
            return Task.FromResult(DownloadResult.Fail(DownloadFailure.ServerError, "simulated failure"));
        if (!Content.TryGetValue(url, out var bytes))
            return Task.FromResult(DownloadResult.Fail(DownloadFailure.ServerError, "HTTP 404 (fake, unknown url)"));

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.WriteAllBytes(destPath, bytes);
        progress?.Report(new DownloadProgress { BytesDownloaded = bytes.Length, TotalBytes = bytes.Length });
        return Task.FromResult(DownloadResult.Success);
    }

    public Task<bool> ExtractZipAsync(string zipPath, string destDir,
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> ExtractClientAsync(string zipPath, string destDir,
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        ExtractClientWithReasonAsync(zipPath, destDir, freshInstall: true, progress, ct)
            .ContinueWith(t => t.Result.Ok, ct);

    public Task<ExtractOutcome> ExtractClientWithReasonAsync(string zipPath, string destDir, bool freshInstall,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!ZipContents.TryGetValue(zipPath, out var files))
            return Task.FromResult(ExtractOutcome.Fail(ExtractFailure.BadArchive));

        Directory.CreateDirectory(destDir);
        foreach (var (rel, bytes) in files)
            PatchingFakes.WriteFile(destDir, rel, bytes);
        return Task.FromResult(ExtractOutcome.Success);
    }

    public async Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || !File.Exists(path)) return false;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A butler that never runs a real process — apply/verify results are scripted per test.</summary>
internal sealed class FakeButlerSidecar : IButlerSidecar
{
    public bool IsAvailable { get; set; } = true;
    public string? VersionOrNull => IsAvailable ? "15.31.0-fake" : null;
    public ButlerResult ApplyResult { get; set; } = ButlerResult.Success(0);
    public ButlerResult VerifyResult { get; set; } = ButlerResult.Success(0);
    public int ApplyCalls { get; private set; }
    public int VerifyCalls { get; private set; }

    /// <summary>When set, ApplyAsync actually copies these bytes into targetDir/relPath so a
    /// subsequent real <see cref="IClientVerifyService.VerifyAsync"/> sees the "patched" tree —
    /// needed by tests that check the file landed, not just that Ok was returned.</summary>
    public Dictionary<string, byte[]>? FilesToWriteOnApply { get; set; }

    public Task<ButlerResult> ApplyAsync(string pwrPath, string stagingDir, string targetDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        ApplyCalls++;
        if (FilesToWriteOnApply is not null && ApplyResult.Ok)
            foreach (var (rel, bytes) in FilesToWriteOnApply)
                PatchingFakes.WriteFile(targetDir, rel, bytes);
        return Task.FromResult(ApplyResult);
    }

    public Task<ButlerResult> VerifyAsync(string sigPath, string targetDir, CancellationToken ct)
    {
        VerifyCalls++;
        return Task.FromResult(VerifyResult);
    }

    public int EnsureAvailableCalls { get; private set; }

    /// <summary>Scripted return. Mirrors the real contract: a successful fetch makes
    /// <see cref="IsAvailable"/> true from then on (so a PLAN test can start with an unavailable
    /// butler and prove the engine's call to this method is what unlocks Delta), a failed one leaves
    /// it exactly as it was.</summary>
    public bool EnsureAvailableResult { get; set; } = true;

    public Task<bool> EnsureAvailableAsync(CancellationToken ct)
    {
        EnsureAvailableCalls++;
        if (EnsureAvailableResult) IsAvailable = true;
        return Task.FromResult(EnsureAvailableResult);
    }
}

internal sealed class FakeGameProcessDetector : IGameProcessDetector
{
    public bool Running { get; set; }
    public bool IsGameRunning(string? expectedExePath) => Running;
}
